using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nitrogen.Cli;

/// <summary>Renders a self-contained VS Code extension project for one language (spec: installable language plugins).</summary>
internal static class VsCodeRenderer
{
    static readonly UTF8Encoding s_utf8 = new(false);

    public static string ExtensionName(LanguagePluginModel model) => "nitrogen-" + model.PluginId;

    public static void Render(VsCodeRequest request, CancellationToken cancel)
    {
        var model = request.Model;
        string output = request.OutputDirectory;
        string parent = Directory.CreateDirectory(Path.GetFullPath(Path.Combine(output, ".."))).FullName;
        string staging = Path.Combine(parent, $".{Path.GetFileName(output)}.nitrogen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["package.json"] = PackageJson(model),
                ["package-lock.json"] = Lockfile(model),
                ["tsconfig.json"] = Resource("vscode/tsconfig.json"),
                ["language-configuration.json"] = LanguageConfiguration,
                [".vscodeignore"] = VscodeIgnore,
                ["README.md"] = Readme(model),
                ["src/extension.ts"] = ExtensionTs(model),
                ["src/csharpStrings.ts"] = Resource("vscode/csharpStrings.ts"),
            };
            foreach (var (relative, content) in files)
            {
                cancel.ThrowIfCancellationRequested();
                string path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content.Replace("\r\n", "\n"), s_utf8);
            }
            LanguageBundle.Stage(model, request.ServerDirectory, Path.Combine(staging, "bundle"));

            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            Directory.Move(staging, output);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    static string Resource(string name)
    {
        using var stream = typeof(VsCodeRenderer).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing embedded resource '{name}'");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Two-space JSON with LF endings; npm files keep characters such as &amp; and ^ unescaped.</summary>
    static string Json(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    static string PackageJson(LanguagePluginModel model)
    {
        var template = JsonNode.Parse(Resource("vscode/package.json"))!;
        string name = ExtensionName(model);
        var extensions = new JsonArray(model.Extensions.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        return Json(new JsonObject
        {
            ["name"] = name,
            ["displayName"] = model.DisplayName,
            ["description"] = $"{model.DisplayName} language support through nitrogen lsp.",
            ["version"] = model.Version,
            ["publisher"] = "nitrogen",
            ["engines"] = template["engines"]!.DeepClone(),
            ["main"] = "./out/extension.js",
            ["activationEvents"] = new JsonArray("onLanguage:" + name, "onLanguage:csharp"), // C# strings tagged with the language
            ["contributes"] = new JsonObject
            {
                ["languages"] = new JsonArray(new JsonObject
                {
                    ["id"] = name,
                    ["aliases"] = new JsonArray(model.DisplayName),
                    ["extensions"] = extensions,
                    ["configuration"] = "./language-configuration.json",
                }),
                ["configuration"] = new JsonObject
                {
                    ["title"] = model.DisplayName,
                    ["properties"] = new JsonObject
                    {
                        [name + ".dotnetPath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["default"] = "",
                            ["description"] = "The dotnet executable that runs the bundled server; empty finds it through DOTNET_ROOT, the standard install locations, and PATH.",
                        },
                        [name + ".server.path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["default"] = "",
                            ["description"] = "A nitrogen executable to run instead of the bundled server (for development).",
                        },
                    },
                },
            },
            ["scripts"] = new JsonObject
            {
                ["compile"] = template["scripts"]!["compile"]!.DeepClone(),
                ["package"] = "vsce package --no-dependencies --skip-license --allow-missing-repository",
            },
            ["dependencies"] = template["dependencies"]!.DeepClone(),
            ["devDependencies"] = template["devDependencies"]!.DeepClone(),
        });
    }

    /// <summary>The extension's lock, renamed: the dependency tree is the same, so npm ci stays reproducible.</summary>
    static string Lockfile(LanguagePluginModel model)
    {
        var lockfile = JsonNode.Parse(Resource("vscode/package-lock.json"))!.AsObject();
        string name = ExtensionName(model);
        lockfile["name"] = name;
        lockfile["version"] = model.Version;
        var root = lockfile["packages"]![""]!.AsObject();
        root["name"] = name;
        root["version"] = model.Version;
        root.Remove("license");
        return Json(lockfile);
    }

    const string LanguageConfiguration = """
{
  "brackets": [["{", "}"], ["[", "]"], ["(", ")"]],
  "autoClosingPairs": [["{", "}"], ["[", "]"], ["(", ")"], { "open": "\"", "close": "\"" }],
  "surroundingPairs": [["{", "}"], ["[", "]"], ["(", ")"], ["\"", "\""]]
}

""";

    const string VscodeIgnore = """
src/**
node_modules/**
tsconfig.json
**/*.map

""";

    static string Readme(LanguagePluginModel model) => $$"""
# {{model.DisplayName}}

{{model.DisplayName}} support for `{{string.Join("`, `", model.Extensions)}}` files: diagnostics, go to definition, references, rename, and semantic colouring, served by the bundled Nitrogen language server. The same works inside C# string literals tagged with the language, such as `/*lang={{model.Extensions.FirstOrDefault()?.TrimStart('.')}}*/ "..."` or a `// language={{model.Extensions.FirstOrDefault()?.TrimStart('.')}}` comment before the statement. When the language's helper sources export an evaluation profile, its files also show each statement's value as an inlay hint.

The server runs on the .NET 10 runtime. If `dotnet` is not found through `DOTNET_ROOT`, the standard install locations, or `PATH`, set `{{ExtensionName(model)}}.dotnetPath`.

Generated by `nitrogen generate vscode`.

""";

    static string ExtensionTs(LanguagePluginModel model) => $$"""
import * as fs from 'fs';
import * as path from 'path';
import { ExtensionContext, window, workspace } from 'vscode';
import { LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import { CSharpStrings, csharpSelector } from './csharpStrings';

const LANGUAGE = {{JsonSerializer.Serialize(ExtensionName(model))}};
const DISPLAY = {{JsonSerializer.Serialize(model.DisplayName)}};

let client: LanguageClient | undefined;
let strings: CSharpStrings | undefined;

/** The dotnet host: the setting, then DOTNET_ROOT, then the standard install locations, then PATH. */
function dotnet(): string {
  const configured = workspace.getConfiguration(LANGUAGE).get<string>('dotnetPath');
  if (configured) return configured;
  const exe = process.platform === 'win32' ? 'dotnet.exe' : 'dotnet';
  const candidates = [
    process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, exe) : undefined,
    process.platform === 'win32' ? path.join(process.env.ProgramFiles ?? 'C:\\Program Files', 'dotnet', exe) : undefined,
    process.platform === 'darwin' ? '/usr/local/share/dotnet/dotnet' : undefined,
    process.platform === 'linux' ? '/usr/share/dotnet/dotnet' : undefined,
    process.platform === 'linux' ? '/usr/lib/dotnet/dotnet' : undefined,
  ];
  return candidates.find((candidate): candidate is string => candidate !== undefined && fs.existsSync(candidate)) ?? exe;
}

export async function activate(context: ExtensionContext): Promise<void> {
  const config = context.asAbsolutePath(path.join('bundle', 'language', 'nitrogen.json'));
  const server = workspace.getConfiguration(LANGUAGE).get<string>('server.path');
  const serverOptions: ServerOptions = server
    ? { command: server, args: ['lsp', '--config', config] }
    : { command: dotnet(), args: [context.asAbsolutePath(path.join('bundle', 'server', 'nitrogen.dll')), 'lsp', '--config', config] };
  strings = new CSharpStrings(() => client);
  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: 'file', language: LANGUAGE }, { scheme: 'untitled', language: LANGUAGE }, ...csharpSelector],
    middleware: strings.middleware,
  };
  client = new LanguageClient(LANGUAGE, DISPLAY, serverOptions, clientOptions);
  try {
    await client.start();
  } catch (error) {
    client = undefined;
    void window.showErrorMessage(
      `${DISPLAY}: the language server did not start (${error}). It needs the .NET 10 runtime; set "${LANGUAGE}.dotnetPath" if dotnet is not found.`);
  }
}

export function deactivate(): Thenable<void> | undefined {
  strings?.dispose();
  return client?.stop();
}

""";
}
