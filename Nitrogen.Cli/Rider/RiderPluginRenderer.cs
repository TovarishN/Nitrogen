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
                ["gradle.properties"] = GradleProperties,
                ["build.gradle.kts"] = BuildGradle,
                ["src/main/resources/META-INF/plugin.xml"] = PluginXml(request.Model),
                ["src/main/kotlin/org/nitrogen/rider/NitrogenSettings.kt"] = SettingsKt,
                ["src/main/kotlin/org/nitrogen/rider/NitrogenConfigurable.kt"] = ConfigurableKt,
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

    const string GradleProperties = """
kotlin.code.style=official
# The IDE provides the Kotlin standard library; plugins must not bundle their own.
kotlin.stdlib.default.dependency=false

""";

    const string BuildGradle = """
plugins {
    id("java")
    kotlin("jvm") version "2.4.0"
    id("org.jetbrains.intellij.platform") version "2.2.1"
}

repositories {
    mavenCentral()
    intellijPlatform { defaultRepositories() }
}

dependencies {
    intellijPlatform { rider("2026.2") }
}

intellijPlatform { pluginConfiguration { ideaVersion { sinceBuild = "262" } } }

// Rider 262 runs plugins on Java 21. Pin both compilers to it so the build does
// not depend on the JDK that happens to run Gradle.
tasks.withType<JavaCompile>().configureEach { options.release = 21 }
kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_21
        freeCompilerArgs.add("-Xjdk-release=21")
    }
}
""";

    const string SettingsKt = """
package org.nitrogen.rider

import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.PathEnvironmentVariableUtil
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import java.io.File

/** The Nitrogen executable chosen in Settings | Tools | Nitrogen; blank means the plugin's default. */
@Service(Service.Level.APP)
@State(name = "NitrogenSettings", storages = [Storage("nitrogen.xml")])
class NitrogenSettings : PersistentStateComponent<NitrogenSettings.State> {
    data class State(var executable: String = "")

    private var state = State()

    override fun getState(): State = state

    override fun loadState(state: State) {
        this.state = state
    }

    var executable: String
        get() = state.executable
        set(value) {
            state.executable = value.trim()
        }

    /** The executable to run: the setting, else [default]; a bare name is looked up on PATH. */
    fun resolveExecutable(default: String): String {
        val name = executable.ifBlank { default }
        val file = File(name)
        val found = if (file.isAbsolute || name.contains(File.separatorChar)) file.takeIf { it.canExecute() }
            else PathEnvironmentVariableUtil.findInPath(name)
        return found?.absolutePath ?: throw ExecutionException(
            "Nitrogen executable '$name' was not found. Set its path in Settings | Tools | Nitrogen.")
    }

    companion object {
        fun getInstance(): NitrogenSettings =
            ApplicationManager.getApplication().getService(NitrogenSettings::class.java)
    }
}

""";

    const string ConfigurableKt = """
package org.nitrogen.rider

import com.intellij.openapi.fileChooser.FileChooserDescriptorFactory
import com.intellij.openapi.options.Configurable
import com.intellij.openapi.project.ProjectManager
import com.intellij.openapi.ui.TextFieldWithBrowseButton
import com.intellij.platform.lsp.api.LspClientManager
import java.awt.BorderLayout
import javax.swing.JComponent
import javax.swing.JLabel
import javax.swing.JPanel

/** Settings | Tools | Nitrogen: the executable the language server runs; applying restarts running servers. */
class NitrogenConfigurable : Configurable {
    private var path: TextFieldWithBrowseButton? = null

    override fun getDisplayName(): String = "Nitrogen"

    override fun createComponent(): JComponent {
        val field = TextFieldWithBrowseButton()
        field.addBrowseFolderListener(null, FileChooserDescriptorFactory.singleFile())
        field.text = NitrogenSettings.getInstance().executable
        path = field
        val row = JPanel(BorderLayout(8, 0))
        row.add(JLabel("Nitrogen executable:"), BorderLayout.WEST)
        row.add(field, BorderLayout.CENTER)
        val panel = JPanel(BorderLayout(0, 4))
        panel.add(row, BorderLayout.NORTH)
        panel.add(JLabel("Leave blank for the plugin's default. A name without a path is looked up on PATH."), BorderLayout.CENTER)
        return panel
    }

    override fun isModified(): Boolean = path?.text?.trim() != NitrogenSettings.getInstance().executable

    override fun apply() {
        NitrogenSettings.getInstance().executable = path?.text.orEmpty()
        for (project in ProjectManager.getInstance().openProjects)
            LspClientManager.getInstance(project).stopAndRestartClientsIfNeeded(NitrogenLspSupport::class.java)
    }

    override fun reset() {
        path?.text = NitrogenSettings.getInstance().executable
    }

    override fun disposeUIResources() {
        path = null
    }
}

""";

    static string PluginXml(RiderPluginModel model) => $$"""
<idea-plugin>
  <id>org.nitrogen.rider.{{model.PluginId}}</id>
  <name>{{Escape(model.DisplayName)}} for Rider</name>
  <vendor>Nitrogen</vendor>
  <depends>com.intellij.modules.lsp</depends>
  <depends>com.intellij.modules.ultimate</depends>
  <extensions defaultExtensionNs="com.intellij">
    <fileType name="{{Escape(model.PluginId)}}" language="Nitrogen" extensions="{{string.Join(';', model.Extensions.Select(x => x.TrimStart('.')).Select(Escape))}}" implementationClass="org.nitrogen.rider.NitrogenFileType" />
    <applicationConfigurable parentId="tools" instance="org.nitrogen.rider.NitrogenConfigurable" id="org.nitrogen.rider.{{model.PluginId}}.settings" displayName="{{Escape(model.DisplayName)}}" />
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
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspIntegrationProvider
import com.intellij.platform.lsp.api.LspIntegrationProvider.LspClientStarter
import com.intellij.platform.lsp.api.ProjectWideLspClientDescriptor

class NitrogenLspSupport : LspIntegrationProvider {
    companion object {
        const val defaultExecutable = "{{EscapeKotlin(request.NitrogenPath)}}"
        const val startRule = "{{EscapeKotlin(request.Model.StartRule)}}"
    }

    override fun fileOpened(project: Project, file: VirtualFile, clientStarter: LspClientStarter) {
        if (file.extension in setOf({{string.Join(", ", request.Model.Extensions.Select(x => "\"" + EscapeKotlin(x.TrimStart('.')) + "\""))}})) {
            clientStarter.ensureClientStarted(NitrogenClientDescriptor(project))
        }
    }

    private class NitrogenClientDescriptor(project: Project) : ProjectWideLspClientDescriptor(project, "{{EscapeKotlin(request.Model.DisplayName)}}") {
        override fun isSupportedFile(file: VirtualFile): Boolean = file.extension in setOf({{string.Join(", ", request.Model.Extensions.Select(x => "\"" + EscapeKotlin(x.TrimStart('.')) + "\""))}})
        override fun createCommandLine(): GeneralCommandLine =
            GeneralCommandLine(NitrogenSettings.getInstance().resolveExecutable(defaultExecutable), "lsp")
    }
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
