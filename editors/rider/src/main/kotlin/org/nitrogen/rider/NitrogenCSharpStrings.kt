package org.nitrogen.rider

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.ProjectWideLspClientDescriptor
import com.intellij.platform.lsp.api.customization.LspCallHierarchyDisabled
import com.intellij.platform.lsp.api.customization.LspCallHierarchyCustomizer
import com.intellij.platform.lsp.api.customization.LspCodeActionsCustomizer
import com.intellij.platform.lsp.api.customization.LspCodeActionsDisabled
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
import com.intellij.platform.lsp.api.customization.LspInlayHintDisabled
import com.intellij.platform.lsp.api.customization.LspOnTypeFormattingCustomizer
import com.intellij.platform.lsp.api.customization.LspOnTypeFormattingDisabled
import com.intellij.platform.lsp.api.customization.LspOptimizeImportsCustomizer
import com.intellij.platform.lsp.api.customization.LspOptimizeImportsDisabled
import com.intellij.platform.lsp.api.customization.LspRenameCustomizer
import com.intellij.platform.lsp.api.customization.LspRenameDisabled
import com.intellij.platform.lsp.api.customization.LspSelectionRangeCustomizer
import com.intellij.platform.lsp.api.customization.LspSelectionRangeDisabled
import com.intellij.platform.lsp.api.customization.LspSignatureHelpCustomizer
import com.intellij.platform.lsp.api.customization.LspSignatureHelpDisabled
import com.intellij.platform.lsp.api.customization.LspTypeHierarchyCustomizer
import com.intellij.platform.lsp.api.customization.LspTypeHierarchyDisabled
import com.intellij.platform.lsp.api.customization.LspWorkspaceSymbolCustomizer
import com.intellij.platform.lsp.api.customization.LspWorkspaceSymbolDisabled

/**
 * Languages inside tagged C# strings (`/*lang=calc*/ "1 + 2;"`, or `// language=calc` before the
 * statement). C# files get a Nitrogen client of their own, separate from the one for the language's
 * files, because the platform switches features per client rather than per file. The server answers it
 * only inside tagged strings: its colours are added to Rider's, and diagnostics, completion, hover, go to
 * definition and find usages work in the strings. Everything that would compete with Rider's C# support
 * (rename, structure view, formatting, code actions, highlighting usages, hints) is left to Rider.
 */
class NitrogenCSharpClient(project: Project, name: String, private val commandLine: () -> GeneralCommandLine) :
    ProjectWideLspClientDescriptor(project, "$name in C# strings") {

    override fun isSupportedFile(file: VirtualFile): Boolean = isCSharp(file)

    override fun createCommandLine(): GeneralCommandLine = commandLine()

    override val lspCustomization: LspCustomization = object : NitrogenCustomization() {
        override val renameCustomizer: LspRenameCustomizer = LspRenameDisabled
        override val documentSymbolCustomizer: LspDocumentSymbolCustomizer = LspDocumentSymbolDisabled
        override val formattingCustomizer: LspFormattingCustomizer = LspFormattingDisabled
        override val onTypeFormattingCustomizer: LspOnTypeFormattingCustomizer = LspOnTypeFormattingDisabled
        override val codeActionsCustomizer: LspCodeActionsCustomizer = LspCodeActionsDisabled
        override val commandsCustomizer: LspCommandsCustomizer = LspCommandsDisabled
        override val optimizeImportsCustomizer: LspOptimizeImportsCustomizer = LspOptimizeImportsDisabled
        override val documentHighlightsCustomizer: LspDocumentHighlightsCustomizer = LspDocumentHighlightsDisabled
        override val documentColorCustomizer: LspDocumentColorCustomizer = LspDocumentColorDisabled
        override val documentLinkCustomizer: LspDocumentLinkCustomizer = LspDocumentLinkDisabled
        override val foldingRangeCustomizer: LspFoldingRangeCustomizer = LspFoldingRangeDisabled
        override val inlayHintCustomizer: LspInlayHintCustomizer = LspInlayHintDisabled
        override val codeLensCustomizer: LspCodeLensCustomizer = LspCodeLensDisabled
        override val selectionRangeCustomizer: LspSelectionRangeCustomizer = LspSelectionRangeDisabled
        override val signatureHelpCustomizer: LspSignatureHelpCustomizer = LspSignatureHelpDisabled
        override val goToTypeDefinitionCustomizer: LspGoToTypeDefinitionCustomizer = LspGoToTypeDefinitionDisabled
        override val callHierarchyCustomizer: LspCallHierarchyCustomizer = LspCallHierarchyDisabled
        override val typeHierarchyCustomizer: LspTypeHierarchyCustomizer = LspTypeHierarchyDisabled
        override val workspaceSymbolCustomizer: LspWorkspaceSymbolCustomizer = LspWorkspaceSymbolDisabled
    }

    companion object {
        fun isCSharp(file: VirtualFile): Boolean = file.extension.equals("cs", ignoreCase = true)
    }
}
