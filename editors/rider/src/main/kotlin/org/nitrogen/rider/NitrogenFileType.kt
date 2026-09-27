package org.nitrogen.rider

import com.intellij.lang.Language
import com.intellij.openapi.fileTypes.LanguageFileType

object NitrogenLanguage : Language("Nitrogen")

class NitrogenFileType : LanguageFileType(NitrogenLanguage) {
    override fun getName() = "NitrogenGrammar"
    override fun getDescription() = "Nitrogen grammar"
    override fun getDefaultExtension() = "ngr"
    override fun getIcon() = null
}
