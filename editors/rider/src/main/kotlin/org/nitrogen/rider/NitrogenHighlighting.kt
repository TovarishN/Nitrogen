package org.nitrogen.rider

import com.intellij.openapi.editor.DefaultLanguageHighlighterColors as Colors
import com.intellij.openapi.editor.colors.TextAttributesKey
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

    /**
     * The TextMate palette (the server sends it to clients with `paletteTokens`): token types named after scopes, each
     * given the Rider colour closest to its scope. `ngr…` types are the .ngr grammar's scopes, mapped by their prefix.
     */
    override fun getTextAttributesKey(tokenType: String, modifiers: List<String>): TextAttributesKey? = when (tokenType) {
        "operationWord" -> Colors.FUNCTION_CALL
        "declarationKeyword", "sectionKeyword" -> Colors.KEYWORD
        "modifierWord" -> Colors.METADATA
        "constantWord" -> Colors.CONSTANT
        "blockBracket" -> Colors.BRACES
        "parenthesis" -> Colors.PARENTHESES
        "squareBracket" -> Colors.BRACKETS
        "terminator" -> Colors.SEMICOLON
        "separator" -> Colors.COMMA
        "operator" -> Colors.OPERATION_SIGN
        else -> if (tokenType.startsWith("ngr")) ngrColor(tokenType.removePrefix("ngr")) else super.getTextAttributesKey(tokenType, modifiers)
    }

    private fun ngrColor(scope: String): TextAttributesKey = when {
        scope == "KeywordOperatorWord" -> Colors.KEYWORD
        scope.startsWith("KeywordOperator") -> Colors.OPERATION_SIGN
        scope.startsWith("Keyword") || scope.startsWith("Storage") -> Colors.KEYWORD
        scope.startsWith("EntityNameFunctionCall") || scope.startsWith("SupportFunction") -> Colors.FUNCTION_CALL
        scope.startsWith("EntityNameFunction") -> Colors.FUNCTION_DECLARATION
        scope.startsWith("EntityNameNamespace") -> Colors.IDENTIFIER
        scope.startsWith("EntityNameType") || scope.startsWith("SupportType") -> Colors.CLASS_NAME
        scope.startsWith("VariableParameter") -> Colors.PARAMETER
        scope.startsWith("VariableOther") -> Colors.INSTANCE_FIELD
        scope.startsWith("Variable") -> Colors.LOCAL_VARIABLE
        scope.startsWith("ConstantNumeric") -> Colors.NUMBER
        scope.startsWith("ConstantCharacterEscape") -> Colors.VALID_STRING_ESCAPE
        scope.startsWith("Constant") -> Colors.CONSTANT
        scope.startsWith("String") -> Colors.STRING
        scope.startsWith("CommentBlock") -> Colors.BLOCK_COMMENT
        scope.startsWith("Comment") -> Colors.LINE_COMMENT
        scope.contains("Terminator") -> Colors.SEMICOLON
        scope.contains("Comma") -> Colors.COMMA
        scope.contains("Brackets") || scope.contains("CharacterClass") -> Colors.BRACKETS
        scope.contains("Block") || scope.contains("Module") || scope.contains("Semantics") -> Colors.BRACES
        scope.startsWith("PunctuationSection") -> Colors.PARENTHESES
        else -> Colors.OPERATION_SIGN
    }
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
