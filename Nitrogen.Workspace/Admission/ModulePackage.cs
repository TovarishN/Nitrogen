using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nitrogen.Workspace.Admission;

public sealed record AdmissionDiagnostic(string Code, string Stage, string Path,
    int Line, int Column, string Message);

public sealed record ModuleExample(string Id, string Path, string Source,
    IReadOnlyList<string> ExpectedDiagnostics, string? ExpectedResult, string? StartRule = null);

public sealed record ModulePackageLoadResult(ModulePackage? Package,
    IReadOnlyList<AdmissionDiagnostic> Diagnostics);

/// <summary>A loader-created package. External callers cannot bypass manifest validation.</summary>
public sealed record ModulePackage
{
    internal ModulePackage(string id, string profileId, string moduleName, string startRule,
        IReadOnlyDictionary<string, string> grammars, IReadOnlyList<ModuleExample> examples,
        IReadOnlyList<string> requestedCapabilities, string sha256)
    {
        Id = id;
        ProfileId = profileId;
        ModuleName = moduleName;
        StartRule = startRule;
        Grammars = new ReadOnlyDictionary<string, string>(new SortedDictionary<string, string>(
            grammars.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));
        Examples = Array.AsReadOnly(examples.Select(example => example with
        {
            ExpectedDiagnostics = Array.AsReadOnly(example.ExpectedDiagnostics.ToArray())
        }).ToArray());
        RequestedCapabilities = Array.AsReadOnly(requestedCapabilities.Order(StringComparer.Ordinal).ToArray());
        Sha256 = sha256;
    }

    public string Id { get; internal init; }
    public string ProfileId { get; internal init; }
    public string ModuleName { get; internal init; }
    public string StartRule { get; internal init; }
    public IReadOnlyDictionary<string, string> Grammars { get; internal init; }
    public IReadOnlyList<ModuleExample> Examples { get; internal init; }
    public IReadOnlyList<string> RequestedCapabilities { get; internal init; }
    public string Sha256 { get; internal init; }
}

/// <summary>Reads reviewed, local declarative packages without following package file links.</summary>
public static class ModulePackageLoader
{
    const int ManifestLimit = 16 * 1024;
    const int GrammarLimit = 64 * 1024;
    const int GrammarTotalLimit = 256 * 1024;
    const int ExampleLimit = 16 * 1024;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static readonly HashSet<string> RootFields = ["id", "profile", "module", "start", "grammars", "examples", "capabilities"];
    static readonly HashSet<string> ExampleFields = ["id", "path", "start", "source", "diagnostics", "expectedResult"];

    public static ModulePackageLoadResult Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            string manifestPath = Path.Combine(directory, "module.json");
            var manifestBytes = ReadBounded(manifestPath, ManifestLimit);
            using var json = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            CheckObject(root, RootFields);
            string id = RequiredString(root, "id");
            string profile = RequiredString(root, "profile");
            string module = RequiredString(root, "module");
            string start = RequiredString(root, "start");
            var capabilitiesJson = RequiredArray(root, "capabilities");
            if (capabilitiesJson.GetArrayLength() > 16) throw Invalid("at most sixteen capabilities are allowed");
            var requested = capabilitiesJson.EnumerateArray().Select(String).ToArray();
            if (requested.Any(string.IsNullOrWhiteSpace) ||
                requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
                throw Invalid("invalid or duplicate capability ID");
            Array.Sort(requested, StringComparer.Ordinal);

            var grammarNames = RequiredArray(root, "grammars");
            if (grammarNames.GetArrayLength() is < 1 or > 4) throw Invalid("one to four grammars are required");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in grammarNames.EnumerateArray())
            {
                string name = String(item);
                if (!SafeGrammarName(name) || !names.Add(name)) throw Invalid($"invalid or duplicate grammar path '{name}'");
            }

            var examplesJson = RequiredArray(root, "examples");
            if (examplesJson.GetArrayLength() is < 1 or > 16) throw Invalid("one to sixteen examples are required");
            var examples = new List<ModuleExample>();
            var exampleIds = new HashSet<string>(StringComparer.Ordinal);
            var examplePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in examplesJson.EnumerateArray())
            {
                CheckObject(element, ExampleFields);
                string exampleId = RequiredString(element, "id");
                string path = RequiredString(element, "path");
                string? exampleStart = element.TryGetProperty("start", out var selected) ? String(selected) : null;
                if (exampleStart is not null && string.IsNullOrWhiteSpace(exampleStart))
                    throw Invalid($"example '{exampleId}' has a blank start rule");
                string source = RequiredString(element, "source", allowEmpty: true);
                if (!exampleIds.Add(exampleId) || !examplePaths.Add(path) || !SafeExamplePath(path))
                    throw Invalid($"invalid or duplicate example '{exampleId}'");
                if (StrictUtf8.GetByteCount(source) > ExampleLimit) throw Invalid($"example '{exampleId}' exceeds 16 KiB");
                var expectedJson = RequiredArray(element, "diagnostics");
                var expected = expectedJson.EnumerateArray().Select(String).ToArray();
                if (expected.Any(string.IsNullOrWhiteSpace)) throw Invalid("diagnostic codes cannot be blank");
                string? result = element.TryGetProperty("expectedResult", out var value) ? String(value) : null;
                if (expected.Length == 0 && result is null || expected.Length > 0 && result is not null)
                    throw Invalid($"example '{exampleId}' needs a result exactly when it expects no diagnostics");
                examples.Add(new ModuleExample(exampleId, path, source, Array.AsReadOnly(expected), result,
                    exampleStart));
            }

            var grammars = new SortedDictionary<string, string>(StringComparer.Ordinal);
            int total = 0;
            foreach (string name in names.Order(StringComparer.Ordinal))
            {
                string path = Path.Combine(directory, name);
                var info = new FileInfo(path);
                if (info.LinkTarget is not null) throw Invalid($"grammar '{name}' is a symbolic link");
                var bytes = ReadBounded(path, GrammarLimit);
                total += bytes.Length;
                if (total > GrammarTotalLimit) throw Invalid("grammar total exceeds 256 KiB");
                grammars.Add(name, StrictUtf8.GetString(bytes));
            }
            string hash = Hash(id, profile, module, start, requested, grammars, examples);
            return new ModulePackageLoadResult(new ModulePackage(id, profile, module, start, grammars,
                examples.AsReadOnly(), requested, hash), []);
        }
        catch (Exception error) when (error is PackageFormatException or JsonException or IOException or
                                      UnauthorizedAccessException or DecoderFallbackException)
        {
            return new ModulePackageLoadResult(null,
                [new AdmissionDiagnostic("NA0001", "package", "module.json", 0, 0, error.Message)]);
        }
    }

    static byte[] ReadBounded(string path, int max)
    {
        var file = new FileInfo(path);
        if (file.Length > max) throw Invalid($"'{Path.GetFileName(path)}' exceeds {max} bytes");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > max) throw Invalid($"'{Path.GetFileName(path)}' exceeds {max} bytes");
        return bytes;
    }

    static bool SafeGrammarName(string name) => name.Length > 4 && name.EndsWith(".ngr", StringComparison.Ordinal) &&
        name.IndexOfAny(['/', '\\']) < 0 && !Path.IsPathRooted(name) && name is not "." or "..";

    static bool SafeExamplePath(string path) => path.Length > 0 && path.IndexOfAny(['/', '\\']) < 0 &&
        path is not "." or ".." && !Path.IsPathRooted(path);

    static void CheckObject(JsonElement element, HashSet<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("expected a JSON object");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || !found.Add(property.Name))
                throw Invalid($"unknown or duplicate property '{property.Name}'");
    }

    static string RequiredString(JsonElement element, string name, bool allowEmpty = false)
    {
        if (!element.TryGetProperty(name, out var value)) throw Invalid($"missing '{name}'");
        string text = String(value);
        if (!allowEmpty && string.IsNullOrWhiteSpace(text)) throw Invalid($"'{name}' cannot be blank");
        return text;
    }

    static string String(JsonElement element) => element.ValueKind == JsonValueKind.String
        ? element.GetString()!
        : throw Invalid("expected a JSON string");

    static JsonElement RequiredArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value : throw Invalid($"missing or invalid '{name}' array");

    static PackageFormatException Invalid(string message) => new(message);

    static string Hash(string id, string profile, string module, string start, IReadOnlyList<string> requested,
        IReadOnlyDictionary<string, string> grammars, IReadOnlyList<ModuleExample> examples)
    {
        using var stream = new MemoryStream();
        void Field(string text)
        {
            byte[] bytes = StrictUtf8.GetBytes(text);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        Field(id); Field(profile); Field(module); Field(start);
        Field("capabilities"); Field(requested.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var capability in requested) Field(capability);
        foreach (var (path, source) in grammars) { Field(path); Field(source); }
        foreach (var example in examples)
        {
            Field(example.Id); Field(example.Path); Field(example.StartRule ?? ""); Field(example.Source);
            foreach (var code in example.ExpectedDiagnostics) Field(code);
            Field(example.ExpectedResult ?? "");
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    sealed class PackageFormatException(string message) : Exception(message);
}
