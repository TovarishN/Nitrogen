using System.Text;
using System.Security.Cryptography;

namespace Nitrogen.Cli;

internal static class RiderPluginRenderer
{
    public static void Render(RiderPluginRequest request, string outputDirectory, CancellationToken cancel)
    {
        string parent = Path.GetFullPath(Path.Combine(outputDirectory, ".."));
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".{Path.GetFileName(outputDirectory)}.nitrogen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["settings.gradle.kts"] = "rootProject.name = \"" + request.Model.PluginId + "-rider\"\n",
                ["gradle.properties"] = "kotlin.code.style=official\n",
                ["build.gradle.kts"] = BuildGradle,
                ["src/main/resources/META-INF/plugin.xml"] = PluginXml(request.Model),
                ["src/main/kotlin/org/nitrogen/rider/NitrogenSettings.kt"] = SettingsKt,
                ["src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt"] = LspKt(request),
                ["src/main/kotlin/org/nitrogen/rider/NitrogenFileType.kt"] = FileTypeKt(request.Model),
                ["README.md"] = Readme(request),
                ["src/main/resources/nitrogen-bundles.json"] = BundlesJson(request.Bundles),
            };
            foreach (var (relative, content) in files.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                cancel.ThrowIfCancellationRequested();
                string path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
            }
            foreach (RiderBundleInput bundle in request.Bundles.OrderBy(x => x.Target, StringComparer.Ordinal))
            {
                cancel.ThrowIfCancellationRequested();
                string destination = Path.Combine(staging, "src", "main", "resources", "bundled", bundle.Target, Path.GetFileName(bundle.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(bundle.Path, destination, overwrite: true);
            }

            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
            Directory.Move(staging, outputDirectory);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    const string BuildGradle = """
plugins {
    id("java")
    kotlin("jvm") version "2.0.21"
    id("org.jetbrains.intellij.platform") version "2.2.1"
}

repositories {
    mavenCentral()
    intellijPlatform { defaultRepositories() }
}

dependencies {
    intellijPlatform { rider("2024.3") }
}

intellijPlatform { pluginConfiguration { ideaVersion { sinceBuild = "243" } } }
""";

    const string SettingsKt = """
package org.nitrogen.rider

import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage

@State(name = "NitrogenSettings", storages = [Storage("nitrogen.xml")])
class NitrogenSettings : PersistentStateComponent<NitrogenSettings.State> {
    data class State(var executable: String = "nitrogen")
    private var state = State()
    override fun getState(): State = state
    override fun loadState(state: State) { this.state = state }
}
""";

    static string PluginXml(RiderPluginModel model) => $$"""
<idea-plugin>
  <id>org.nitrogen.rider.{{model.PluginId}}</id>
  <name>{{Escape(model.DisplayName)}} for Rider</name>
  <vendor>Nitrogen</vendor>
  <depends>com.intellij.modules.platform</depends>
  <extensions defaultExtensionNs="com.intellij">
    <fileType name="{{Escape(model.DisplayName)}}" language="Nitrogen" fileNames="{{string.Join(';', model.Extensions.Select(Escape))}}" implementationClass="org.nitrogen.rider.NitrogenFileType" />
  </extensions>
  <extensions defaultExtensionNs="com.intellij.platform.lsp">
    <integrationProvider implementation="org.nitrogen.rider.NitrogenLspSupport" />
  </extensions>
</idea-plugin>
""";

    static string FileTypeKt(RiderPluginModel model) => $$"""
package org.nitrogen.rider

import com.intellij.openapi.fileTypes.LanguageFileType
import com.intellij.lang.Language

object NitrogenLanguage : Language("Nitrogen")

class NitrogenFileType : LanguageFileType(NitrogenLanguage) {
    override fun getName() = "{{model.PluginId}}"
    override fun getDescription() = "{{EscapeKotlin(model.DisplayName)}}"
    override fun getDefaultExtension() = "{{EscapeKotlin(model.Extensions.First().TrimStart('.'))}}"
    override fun getIcon() = null
}
""";

    static string LspKt(RiderPluginRequest request) => $$"""
package org.nitrogen.rider

import com.intellij.execution.configurations.GeneralCommandLine

object NitrogenLspSupport {
    const val defaultExecutable = "{{EscapeKotlin(request.NitrogenPath)}}"
    const val startRule = "{{EscapeKotlin(request.Model.StartRule)}}"
    fun command(executable: String = defaultExecutable): GeneralCommandLine =
        GeneralCommandLine(executable, "lsp")
}
""";

    static string Readme(RiderPluginRequest request) => $$"""
# {{request.Model.DisplayName}} for Rider

This plugin starts `nitrogen lsp` for `{{string.Join("`, `", request.Model.Extensions)}}` files.

Set the Nitrogen executable in Rider's Nitrogen settings. The generated plugin defaults to `{{request.NitrogenPath}}` and expects the grammar start rule `{{request.Model.StartRule}}`.

Build with `./gradlew buildPlugin` and install the resulting ZIP from Rider's plugin settings.
""";

    static string BundlesJson(IReadOnlyList<RiderBundleInput> bundles)
    {
        var entries = bundles.OrderBy(x => x.Target, StringComparer.Ordinal).Select(x =>
            $"  {{ \"target\": \"{Escape(x.Target)}\", \"file\": \"bundled/{Escape(x.Target)}/{Escape(Path.GetFileName(x.Path))}\", \"sha256\": \"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x.Path))).ToLowerInvariant()}\" }}");
        return "{\n\"bundles\": [\n" + string.Join(",\n", entries) + "\n]\n}\n";
    }

    static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? value;
    static string EscapeKotlin(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
