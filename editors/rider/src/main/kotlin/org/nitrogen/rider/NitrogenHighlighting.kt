package org.nitrogen.rider

import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.customization.LspCustomization
import com.intellij.platform.lsp.api.customization.LspInlayHintCustomizer
import com.intellij.platform.lsp.api.customization.LspInlayHintSupport
import com.intellij.platform.lsp.api.customization.LspSemanticTokensCustomizer
import com.intellij.platform.lsp.api.customization.LspSemanticTokensSupport
import com.intellij.psi.PsiFile

/**
 * Semantic tokens for every file the client serves. The platform asks a server for them only in plain
 * text and TextMate files by default, which leaves a Nitrogen language (it has its own file type) and
 * C# strings uncoloured.
 */
object NitrogenSemanticTokens : LspSemanticTokensSupport() {
    override fun shouldAskServerForSemanticTokens(psiFile: PsiFile): Boolean = true
}

/** Inlay hints for every file the client serves: a language with an evaluation profile shows each statement's value. */
object NitrogenInlayHints : LspInlayHintSupport() {
    override fun shouldAskServerForInlayHints(file: VirtualFile): Boolean = true
}

/** A language's own client: the platform's defaults, with semantic tokens and inlay hints everywhere. */
open class NitrogenCustomization : LspCustomization() {
    override val semanticTokensCustomizer: LspSemanticTokensCustomizer = NitrogenSemanticTokens
    override val inlayHintCustomizer: LspInlayHintCustomizer = NitrogenInlayHints
}
