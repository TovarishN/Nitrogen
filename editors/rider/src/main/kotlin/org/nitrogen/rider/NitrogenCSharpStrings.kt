package org.nitrogen.rider

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.ProjectWideLspClientDescriptor
import com.intellij.platform.lsp.api.customization.LspCallHierarchyDisabled
import com.intellij.platform.lsp.api.customization.LspCallHierarchyCustomizer
import com.intellij.platform.lsp.api.customization.LspCodeLensCustomizer
import com.intellij.platform.lsp.api.customization.LspCodeLensDisabled
import com.intellij.platform.lsp.api.customization.LspCommandsCustomizer
import com.intellij.platform.lsp.api.customization.LspCommandsDisabled
import com.intellij.platform.lsp.api.customization.LspCustomization
import com.intellij.platform.lsp.api.customization.LspDocumentColorCustomizer
import com.intellij.platform.lsp.api.customization.LspDocumentColorDisabled
import com.intellij.platform.lsp.api.customization.LspDocumentHighlightsCustomizer
import com.intellij.platform.lsp.api.customization.LspDocumentHighlightsDisabled
import com.intellij.platform.lsp.api.customization.LspDocumentLinkCustomizer
import com.intellij.platform.lsp.api.customization.LspDocumentLinkDisabled
import com.intellij.platform.lsp.api.customization.LspDocumentSymbolCustomizer
import com.intellij.platform.lsp.api.customization.LspDocumentSymbolDisabled
import com.intellij.platform.lsp.api.customization.LspFoldingRangeCustomizer
import com.intellij.platform.lsp.api.customization.LspFoldingRangeDisabled
import com.intellij.platform.lsp.api.customization.LspFormattingCustomizer
import com.intellij.platform.lsp.api.customization.LspFormattingDisabled
import com.intellij.platform.lsp.api.customization.LspGoToTypeDefinitionCustomizer
import com.intellij.platform.lsp.api.customization.LspGoToTypeDefinitionDisabled
import com.intellij.platform.lsp.api.customization.LspInlayHintCustomizer
import com.intellij.platform.lsp.api.customization.LspOnTypeFormattingCustomizer
import com.intellij.platform.lsp.api.customization.LspOnTypeFormattingDisabled
import com.intellij.platform.lsp.api.customization.LspOptimizeImportsCustomizer
import com.intellij.platform.lsp.api.customization.LspOptimizeImportsDisabled
import com.intellij.platform.lsp.api.customization.LspRenameCustomizer
import com.intellij.platform.lsp.api.customization.LspRenameDisabled
import com.intellij.platform.lsp.api.customization.LspSelectionRangeCustomizer
import com.intellij.platform.lsp.api.customization.LspSelectionRangeDisabled
import com.intellij.platform.lsp.api.customization.LspTypeHierarchyCustomizer
import com.intellij.platform.lsp.api.customization.LspTypeHierarchyDisabled
import com.intellij.platform.lsp.api.customization.LspWorkspaceSymbolCustomizer
import com.intellij.platform.lsp.api.customization.LspWorkspaceSymbolDisabled
import java.nio.file.Files

/**
 * Languages inside tagged C# strings (`/*lang=calc*/ "1 + 2;"`, or `// language=calc` before the
 * statement). C# files get a Nitrogen client of their own, separate from the one for the language's
 * files, because the platform switches features per client rather than per file. The server answers it
 * only inside tagged strings: its colours are added to Rider's, and diagnostics, completion, hover, go to
 * definition, find usages, statement values, quick fixes and signature help work in the strings. Quick fixes
 * and signature help stay on because the server gives none outside a tagged string, so Rider's Alt+Enter and
 * parameter info for ordinary C# are its own. Everything that would compete with Rider's C# support (rename,
 * structure view, formatting, highlighting usages) is left to Rider.
 *
 * [skipLanguages] names the languages (by name or extension) this client's server leaves alone: a client
 * whose server reads the workspace's nitrogen.json skips those another installed plugin carries itself,
 * so their strings are not served twice.
 */
class NitrogenCSharpClient(
    project: Project,
    name: String,
    private val commandLine: () -> GeneralCommandLine,
    private val skipLanguages: () -> List<String> = { emptyList() },
) : ProjectWideLspClientDescriptor(project, "$name in C# strings") {

    override fun isSupportedFile(file: VirtualFile): Boolean = isCSharp(file)

    override fun createCommandLine(): GeneralCommandLine = commandLine()

    override fun createInitializationOptions(): Any = mapOf("skipLanguages" to skipLanguages())

    override val lspCustomization: LspCustomization = object : NitrogenCustomization() {
        override val renameCustomizer: LspRenameCustomizer = LspRenameDisabled
        override val documentSymbolCustomizer: LspDocumentSymbolCustomizer = LspDocumentSymbolDisabled
        override val formattingCustomizer: LspFormattingCustomizer = LspFormattingDisabled
        override val onTypeFormattingCustomizer: LspOnTypeFormattingCustomizer = LspOnTypeFormattingDisabled
        override val commandsCustomizer: LspCommandsCustomizer = LspCommandsDisabled
        override val optimizeImportsCustomizer: LspOptimizeImportsCustomizer = LspOptimizeImportsDisabled
        override val documentHighlightsCustomizer: LspDocumentHighlightsCustomizer = LspDocumentHighlightsDisabled
        override val documentColorCustomizer: LspDocumentColorCustomizer = LspDocumentColorDisabled
        override val documentLinkCustomizer: LspDocumentLinkCustomizer = LspDocumentLinkDisabled
        override val foldingRangeCustomizer: LspFoldingRangeCustomizer = LspFoldingRangeDisabled
        override val inlayHintCustomizer: LspInlayHintCustomizer = NitrogenInlayHints
        override val codeLensCustomizer: LspCodeLensCustomizer = LspCodeLensDisabled
        override val selectionRangeCustomizer: LspSelectionRangeCustomizer = LspSelectionRangeDisabled
        override val goToTypeDefinitionCustomizer: LspGoToTypeDefinitionCustomizer = LspGoToTypeDefinitionDisabled
        override val callHierarchyCustomizer: LspCallHierarchyCustomizer = LspCallHierarchyDisabled
        override val typeHierarchyCustomizer: LspTypeHierarchyCustomizer = LspTypeHierarchyDisabled
        override val workspaceSymbolCustomizer: LspWorkspaceSymbolCustomizer = LspWorkspaceSymbolDisabled
    }

    companion object {
        fun isCSharp(file: VirtualFile): Boolean = file.extension.equals("cs", ignoreCase = true)

        /**
         * The languages the other loaded Nitrogen plugins carry in their own bundle (bundle/language/nitrogen.json):
         * their names and extensions without the dot.
         */
        fun languagesOfOtherPlugins(ownId: String): List<String> = PluginManagerCore.loadedPlugins
            .filter { it.pluginId.idString.startsWith("org.nitrogen.rider") && it.pluginId.idString != ownId }
            .mapNotNull { plugin -> plugin.pluginPath.resolve("bundle/language/nitrogen.json").takeIf { Files.isRegularFile(it) } }
            .flatMap { languagesIn(Files.readString(it)) }
            .distinct()

        private val name = Regex(""""name"\s*:\s*"([^"]+)"""")
        private val extensions = Regex(""""extensions"\s*:\s*\[([^\]]*)]""")
        private val string = Regex(""""([^"]+)"""")

        /** The language names and extensions (without the dot) a nitrogen.json declares. */
        fun languagesIn(json: String): List<String> =
            name.findAll(json).map { it.groupValues[1] }.toList() +
                extensions.findAll(json).flatMap { list -> string.findAll(list.groupValues[1]).map { it.groupValues[1].removePrefix(".") } }
    }
}
