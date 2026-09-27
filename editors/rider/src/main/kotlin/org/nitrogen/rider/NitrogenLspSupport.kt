package org.nitrogen.rider

import com.intellij.execution.configurations.GeneralCommandLine

object NitrogenLspSupport {
    const val defaultExecutable = "nitrogen"

    fun command(configured: String?, bundled: String? = null): GeneralCommandLine =
        GeneralCommandLine(configured?.takeIf { it.isNotBlank() } ?: bundled ?: defaultExecutable, "lsp")
}
