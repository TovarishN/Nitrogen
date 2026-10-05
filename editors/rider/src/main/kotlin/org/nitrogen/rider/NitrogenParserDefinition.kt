package org.nitrogen.rider

import com.intellij.extapi.psi.ASTWrapperPsiElement
import com.intellij.extapi.psi.PsiFileBase
import com.intellij.lang.ASTNode
import com.intellij.lang.ParserDefinition
import com.intellij.lang.PsiBuilder
import com.intellij.lang.PsiParser
import com.intellij.lexer.Lexer
import com.intellij.lexer.LexerBase
import com.intellij.openapi.fileTypes.FileType
import com.intellij.openapi.project.Project
import com.intellij.psi.FileViewProvider
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.tree.IElementType
import com.intellij.psi.tree.IFileElementType
import com.intellij.psi.tree.TokenSet

/**
 * The smallest syntax the platform needs for the language: its whole text is one token. The language
 * server does the real work. Without it, Rider fails to inject the language where a C# string is tagged
 * with `language=...`, and reports that as an IDE error.
 */
class NitrogenParserDefinition : ParserDefinition {
    override fun createLexer(project: Project?): Lexer = WholeTextLexer()

    override fun createParser(project: Project?): PsiParser = PsiParser { root, builder ->
        val marker = builder.mark()
        while (!builder.eof()) builder.advanceLexer()
        marker.done(root)
        builder.treeBuilt
    }

    override fun getFileNodeType(): IFileElementType = FILE

    override fun getCommentTokens(): TokenSet = TokenSet.EMPTY

    override fun getStringLiteralElements(): TokenSet = TokenSet.EMPTY

    override fun createElement(node: ASTNode): PsiElement = ASTWrapperPsiElement(node)

    override fun createFile(viewProvider: FileViewProvider): PsiFile = NitrogenPsiFile(viewProvider)

    private class NitrogenPsiFile(viewProvider: FileViewProvider) : PsiFileBase(viewProvider, NitrogenLanguage) {
        override fun getFileType(): FileType = viewProvider.fileType.takeIf { it is NitrogenFileType } ?: NitrogenFileType()
    }

    /** One token for the whole buffer, or none when it is empty. */
    private class WholeTextLexer : LexerBase() {
        private var buffer: CharSequence = ""
        private var start = 0
        private var end = 0
        private var done = true

        override fun start(buffer: CharSequence, startOffset: Int, endOffset: Int, initialState: Int) {
            this.buffer = buffer
            start = startOffset
            end = endOffset
            done = startOffset >= endOffset
        }

        override fun getState(): Int = 0
        override fun getTokenType(): IElementType? = if (done) null else TEXT
        override fun getTokenStart(): Int = start
        override fun getTokenEnd(): Int = end
        override fun advance() { done = true }
        override fun getBufferSequence(): CharSequence = buffer
        override fun getBufferEnd(): Int = end
    }

    companion object {
        val FILE = IFileElementType(NitrogenLanguage)
        val TEXT = IElementType("TEXT", NitrogenLanguage)
    }
}
