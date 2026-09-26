using Microsoft.CodeAnalysis.CSharp;
using CodeAnalysis = Microsoft.CodeAnalysis;

namespace Nitrogen.Tests;

/// <summary>Compiles emitted sources against the runtime in memory and returns the errors.</summary>
internal static class GeneratedCompilation
{
    public static IReadOnlyList<string> Errors(IEnumerable<string> sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source, parseOptions)).ToList();

        // The platform assemblies plus Nitrogen.Runtime; not this test assembly, which already
        // contains generated copies of the same types.
        string[] paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator)
            ?? AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => a.Location).ToArray();
        var references = paths
            .Where(p => p.Length > 0 && !Path.GetFileName(p).StartsWith("Nitrogen.Tests", StringComparison.Ordinal))
            .Append(typeof(global::Nitrogen.Language).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(p => CodeAnalysis.MetadataReference.CreateFromFile(p));

        var compilation = CSharpCompilation.Create("NitrogenGeneratedCheck", trees, references,
            new CSharpCompilationOptions(CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true, nullableContextOptions: CodeAnalysis.NullableContextOptions.Enable));
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }
}
