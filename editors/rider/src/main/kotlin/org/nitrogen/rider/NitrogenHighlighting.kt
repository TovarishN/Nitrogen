package org.nitrogen.rider

import com.intellij.platform.lsp.api.customization.LspCustomization
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

/** A language's own client: the platform's defaults, with semantic tokens everywhere. */
open class NitrogenCustomization : LspCustomization() {
    override val semanticTokensCustomizer: LspSemanticTokensCustomizer = NitrogenSemanticTokens
}
