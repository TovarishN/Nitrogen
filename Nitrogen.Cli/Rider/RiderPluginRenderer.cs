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
                ["build.gradle.kts"] = BuildGradle(request),
                ["src/main/resources/META-INF/plugin.xml"] = PluginXml(request.Model),
                ["src/main/kotlin/org/nitrogen/rider/NitrogenSettings.kt"] = InPackage(SettingsKt, request.Model),
                ["src/main/kotlin/org/nitrogen/rider/NitrogenConfigurable.kt"] = InPackage(ConfigurableKt, request.Model),
                ["src/main/kotlin/org/nitrogen/rider/NitrogenBundles.kt"] = InPackage(BundlesKt, request.Model),
                ["src/main/kotlin/org/nitrogen/rider/NitrogenPlugin.kt"] = PluginKt(request.Model),
                ["src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt"] = request.SelfContainedServer is null ? LspKt(request) : SelfContainedLspKt(request),
                ["src/main/kotlin/org/nitrogen/rider/NitrogenFileType.kt"] = FileTypeKt(request.Model),
                ["README.md"] = Readme(request),
                ["src/main/resources/nitrogen-bundles.json"] = BundlesJson(request.Bundles),
            };
            if (request.SelfContainedServer is not null)
                files["src/main/kotlin/org/nitrogen/rider/NitrogenLanguageBundle.kt"] = LanguageBundleKt(request.Model);
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

            if (request.SelfContainedServer is { } server) LanguageBundle.Stage(request.Model, server, Path.Combine(staging, "bundle"));

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

    static string BuildGradle(RiderPluginRequest request) => $$"""
plugins {
    id("java")
    kotlin("jvm") version "2.4.0"
    id("org.jetbrains.intellij.platform") version "2.19.0"
}

version = "{{request.Model.Version}}"

repositories {
    mavenCentral()
    intellijPlatform { defaultRepositories() }
}

dependencies {
    // The Rider installer is not supported as a target; use the Maven distribution.
    intellijPlatform { rider("2026.2") { useInstaller = false } }
}

intellijPlatform { pluginConfiguration { ideaVersion { sinceBuild = "262" } } }
""" + (request.SelfContainedServer is null ? "" : """

tasks.prepareSandbox {
    // The language bundle (grammar, helper sources, portable server) sits beside lib/ in the installed plugin.
    from(layout.projectDirectory.dir("bundle")) { into(pluginName.map { "$it/bundle" }) }
}
""");

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
@State(name = NitrogenPlugin.SETTINGS_NAME, storages = [Storage(NitrogenPlugin.SETTINGS_FILE)])
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

    /**
     * The executable to run: the setting when set (an invalid one is an error, never a fallback), else the
     * plugin's bundled server for this machine, else [default]. A bare name is looked up on PATH.
     */
    fun resolveExecutable(default: String): String {
        val name = executable.ifBlank { null } ?: NitrogenBundles.current() ?: default
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

    const string BundlesKt = """"
package org.nitrogen.rider

import com.intellij.execution.ExecutionException
import com.intellij.openapi.application.PathManager
import com.intellij.openapi.util.SystemInfo
import com.intellij.util.system.CpuArch
import java.io.InputStream
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.security.DigestInputStream
import java.security.MessageDigest

/**
 * Server executables packaged by `nitrogen generate rider --bundle`. The generator records them in
 * /nitrogen-bundles.json with their SHA-256; the matching one is extracted from the plugin jar,
 * verified, and run from the IDE's system directory.
 */
object NitrogenBundles {
    data class Bundle(val target: String, val file: String, val sha256: String)

    private val entry = Regex("""\{\s*"target":\s*"([^"]+)",\s*"file":\s*"([^"]+)",\s*"sha256":\s*"([0-9a-f]{64})"\s*}""")

    /** The entries of the generator's descriptor. */
    fun parse(json: String): List<Bundle> =
        entry.findAll(json).map { Bundle(it.groupValues[1], it.groupValues[2], it.groupValues[3]) }.toList()

    /** The bundle target of an operating system and CPU, named as the generator names them; null when none can match. */
    fun target(mac: Boolean, linux: Boolean, windows: Boolean, arm64: Boolean, x64: Boolean): String? = when {
        mac && arm64 -> "macos-aarch64"
        mac && x64 -> "macos-x64"
        linux && x64 -> "linux-x64"
        windows && x64 -> "windows-x64"
        else -> null
    }

    /** The bundled executable for this machine, or null when the plugin bundles none for it. */
    fun current(): String? = executableFor(
        target(SystemInfo.isMac, SystemInfo.isLinux, SystemInfo.isWindows, CpuArch.isArm64(), CpuArch.isIntel64()),
        { name -> NitrogenBundles::class.java.getResourceAsStream("/$name") },
        PathManager.getSystemDir().resolve("nitrogen-bundles"))

    /**
     * The path of [target]'s bundle, extracted under [cache] in a directory named by its SHA-256; null
     * when [resource] has no descriptor or no bundle for [target]. A copy that no longer matches is
     * replaced, and a missing or mismatched bundle is an error rather than a different binary.
     */
    fun executableFor(target: String?, resource: (String) -> InputStream?, cache: Path): String? {
        val descriptor = resource("nitrogen-bundles.json")?.use { it.readBytes().toString(Charsets.UTF_8) } ?: return null
        val bundle = parse(descriptor).firstOrNull { it.target == target } ?: return null
        val destination = cache.resolve(bundle.sha256).resolve(bundle.file.substringAfterLast('/'))
        if (!Files.isRegularFile(destination) || sha256(destination) != bundle.sha256) {
            Files.createDirectories(destination.parent)
            val staging = Files.createTempFile(destination.parent, "nitrogen", ".tmp")
            try {
                val digest = MessageDigest.getInstance("SHA-256")
                val stream = resource(bundle.file)
                    ?: throw ExecutionException("Bundled Nitrogen server '${bundle.file}' is missing from the plugin.")
                DigestInputStream(stream, digest).use { Files.copy(it, staging, StandardCopyOption.REPLACE_EXISTING) }
                if (hex(digest.digest()) != bundle.sha256)
                    throw ExecutionException("Bundled Nitrogen server '${bundle.file}' does not match its recorded SHA-256.")
                Files.move(staging, destination, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
            } finally {
                Files.deleteIfExists(staging)
            }
        }
        destination.toFile().setExecutable(true)
        return destination.toString()
    }

    private fun sha256(file: Path): String {
        val digest = MessageDigest.getInstance("SHA-256")
        DigestInputStream(Files.newInputStream(file), digest).use { it.transferTo(java.io.OutputStream.nullOutputStream()) }
        return hex(digest.digest())
    }

    private fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }
}

"""";

    /// <summary>
    /// The plugin's own Kotlin package. Rider registers services by class name, so plugins sharing
    /// class names cannot be installed together: the second gets the first's settings service.
    /// </summary>
    static string KotlinPackage(LanguagePluginModel model) => "org.nitrogen.rider.lang_" + model.PluginId.Replace('-', '_');

    /// <summary>The plugin's language ID, unique like its package.</summary>
    static string LanguageId(LanguagePluginModel model) => "Nitrogen." + model.PluginId;

    /// <summary>
    /// A template file shared verbatim with editors/rider, moved into the plugin's package. The templates
    /// are raw strings, so a CRLF checkout (core.autocrlf, core.eol) gives them CRLF line endings.
    /// </summary>
    internal static string InPackage(string kotlin, LanguagePluginModel model)
    {
        kotlin = kotlin.Replace("\r\n", "\n");
        const string template = "package org.nitrogen.rider\n";
        if (!kotlin.StartsWith(template, StringComparison.Ordinal))
            throw new InvalidOperationException("shared Kotlin must start with the template package");
        return "package " + KotlinPackage(model) + "\n" + kotlin[template.Length..];
    }

    static string PluginKt(LanguagePluginModel model) => $$"""
package {{KotlinPackage(model)}}

/**
 * This plugin's application-wide names. Rider keeps settings state names for the whole IDE, so each
 * generated plugin has its own and several Nitrogen plugins can be installed together.
 */
object NitrogenPlugin {
    const val SETTINGS_NAME = "NitrogenSettings.{{model.PluginId}}"
    const val SETTINGS_FILE = "nitrogen-{{model.PluginId}}.xml"
}

""";

    static string PluginXml(LanguagePluginModel model) => $$"""
<idea-plugin>
  <id>org.nitrogen.rider.{{model.PluginId}}</id>
  <name>{{Escape(model.DisplayName)}} for Rider</name>
  <vendor>Nitrogen</vendor>
  <depends>com.intellij.modules.lsp</depends>
  <depends>com.intellij.modules.ultimate</depends>
  <extensions defaultExtensionNs="com.intellij">
    <fileType name="{{Escape(model.PluginId)}}" language="{{LanguageId(model)}}" extensions="{{string.Join(';', model.Extensions.Select(x => x.TrimStart('.')).Select(Escape))}}" implementationClass="{{KotlinPackage(model)}}.NitrogenFileType" />
    <applicationConfigurable parentId="tools" instance="{{KotlinPackage(model)}}.NitrogenConfigurable" id="org.nitrogen.rider.{{model.PluginId}}.settings" displayName="{{Escape(model.DisplayName)}}" />
  </extensions>
  <extensions defaultExtensionNs="com.intellij.platform.lsp">
    <integrationProvider implementation="{{KotlinPackage(model)}}.NitrogenLspSupport" />
  </extensions>
</idea-plugin>
""";

    static string FileTypeKt(LanguagePluginModel model) => $$"""
package {{KotlinPackage(model)}}

import com.intellij.openapi.fileTypes.LanguageFileType
import com.intellij.lang.Language

object NitrogenLanguage : Language("{{LanguageId(model)}}")

class NitrogenFileType : LanguageFileType(NitrogenLanguage) {
    override fun getName() = "{{model.PluginId}}"
    override fun getDescription() = "{{EscapeKotlin(model.DisplayName)}}"
    override fun getDefaultExtension() = "{{EscapeKotlin(model.Extensions.First().TrimStart('.'))}}"
    override fun getIcon() = null
}
""";

    static string LspKt(RiderPluginRequest request) => $$"""
package {{KotlinPackage(request.Model)}}

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

    static string SelfContainedLspKt(RiderPluginRequest request) => $$"""
package {{KotlinPackage(request.Model)}}

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspIntegrationProvider
import com.intellij.platform.lsp.api.LspIntegrationProvider.LspClientStarter
import com.intellij.platform.lsp.api.ProjectWideLspClientDescriptor

class NitrogenLspSupport : LspIntegrationProvider {
    companion object {
        const val defaultExecutable = "{{EscapeKotlin(request.NitrogenPath)}}"
        val extensions = setOf({{string.Join(", ", request.Model.Extensions.Select(x => "\"" + EscapeKotlin(x.TrimStart('.')) + "\""))}})
    }

    override fun fileOpened(project: Project, file: VirtualFile, clientStarter: LspClientStarter) {
        if (file.extension in extensions) clientStarter.ensureClientStarted(NitrogenClientDescriptor(project))
    }

    private class NitrogenClientDescriptor(project: Project) : ProjectWideLspClientDescriptor(project, "{{EscapeKotlin(request.Model.DisplayName)}}") {
        override fun isSupportedFile(file: VirtualFile): Boolean = file.extension in extensions

        /** The executable set in Settings when there is one, else the bundled server on the dotnet host; both get the bundled config. */
        override fun createCommandLine(): GeneralCommandLine {
            val bundle = NitrogenLanguageBundle.directory()
            val config = bundle.resolve("language/nitrogen.json").toString()
            val settings = NitrogenSettings.getInstance()
            return if (settings.executable.isNotBlank())
                GeneralCommandLine(settings.resolveExecutable(defaultExecutable), "lsp", "--config", config)
            else
                GeneralCommandLine(NitrogenLanguageBundle.dotnet(), bundle.resolve("server/nitrogen.dll").toString(), "lsp", "--config", config)
        }
    }
}
""";

    static string LanguageBundleKt(LanguagePluginModel model) => $$"""
package {{KotlinPackage(model)}}

import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.PathEnvironmentVariableUtil
import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.openapi.extensions.PluginId
import com.intellij.openapi.util.SystemInfo
import java.io.File
import java.nio.file.Files
import java.nio.file.Path

/** The language bundle installed beside the plugin's lib/, and the dotnet host that runs its server. */
object NitrogenLanguageBundle {
    fun directory(): Path {
        val plugin = PluginManagerCore.getPlugin(PluginId.getId("org.nitrogen.rider.{{model.PluginId}}"))
            ?: throw ExecutionException("The {{EscapeKotlin(model.DisplayName)}} plugin is not installed.")
        val bundle = plugin.pluginPath.resolve("bundle")
        if (!Files.isRegularFile(bundle.resolve("server/nitrogen.dll")))
            throw ExecutionException("The {{EscapeKotlin(model.DisplayName)}} plugin's language bundle is missing; reinstall the plugin.")
        return bundle
    }

    /** dotnet: DOTNET_ROOT, then the standard install locations, then PATH. */
    fun dotnet(): String {
        val exe = if (SystemInfo.isWindows) "dotnet.exe" else "dotnet"
        val candidates = listOfNotNull(
            System.getenv("DOTNET_ROOT")?.let { File(it, exe) },
            if (SystemInfo.isWindows) File(System.getenv("ProgramFiles") ?: "C:\\Program Files", "dotnet\\" + exe) else null,
            if (SystemInfo.isMac) File("/usr/local/share/dotnet/dotnet") else null,
            if (SystemInfo.isLinux) File("/usr/share/dotnet/dotnet") else null,
            if (SystemInfo.isLinux) File("/usr/lib/dotnet/dotnet") else null)
        candidates.firstOrNull { it.canExecute() }?.let { return it.absolutePath }
        PathEnvironmentVariableUtil.findInPath(exe)?.let { return it.absolutePath }
        throw ExecutionException(
            "{{EscapeKotlin(model.DisplayName)}} needs the .NET 10 runtime: install it, set DOTNET_ROOT, or set a Nitrogen executable in Settings | Tools | {{EscapeKotlin(model.DisplayName)}}.")
    }
}
""";

    static string Readme(RiderPluginRequest request) => $$"""
# {{request.Model.DisplayName}} for Rider

This plugin starts `nitrogen lsp` for `{{string.Join("`, `", request.Model.Extensions)}}` files.

The server runs the first of: the executable set in Settings | Tools | {{request.Model.DisplayName}}; the bundled server for this machine, if the plugin was generated with `--bundle`; `{{request.NitrogenPath}}`. The plugin expects the grammar start rule `{{request.Model.StartRule}}`.

A bundle must be a self-contained single-file server, for example `dotnet publish Nitrogen.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true`. Rider extracts it once, checks its SHA-256, and runs it from its system directory.

Build with `gradle buildPlugin` and install the resulting ZIP from Rider's plugin settings.
""" + (request.SelfContainedServer is null ? "" : """

This plugin carries its language and a portable server in `bundle/`, run with `dotnet` (.NET 10); a Nitrogen executable set in Settings replaces the bundled server.
""");

    static string BundlesJson(IReadOnlyList<RiderBundleInput> bundles)
    {
        var entries = bundles.OrderBy(x => x.Target, StringComparer.Ordinal).Select(x =>
            $"  {{ \"target\": \"{Escape(x.Target)}\", \"file\": \"bundled/{Escape(x.Target)}/{Escape(Path.GetFileName(x.Path))}\", \"sha256\": \"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x.Path))).ToLowerInvariant()}\" }}");
        return "{\n\"bundles\": [\n" + string.Join(",\n", entries) + "\n]\n}\n";
    }

    static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? value;
    static string EscapeKotlin(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
