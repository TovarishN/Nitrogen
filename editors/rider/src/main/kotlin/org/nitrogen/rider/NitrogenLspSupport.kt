package org.nitrogen.rider

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspClientStarter
import com.intellij.platform.lsp.api.LspIntegrationProvider
import com.intellij.platform.lsp.api.ProjectWideLspClientDescriptor

class NitrogenLspSupport : LspIntegrationProvider {
    companion object { const val defaultExecutable = "nitrogen" }

    override fun fileOpened(project: Project, file: VirtualFile, clientStarter: LspClientStarter) {
        if (file.extension == "ngr") clientStarter.ensureClientStarted(NitrogenClientDescriptor(project))
    }

    private class NitrogenClientDescriptor(project: Project) : ProjectWideLspClientDescriptor(project, "Nitrogen") {
        override fun isSupportedFile(file: VirtualFile): Boolean = file.extension == "ngr"
        override fun createCommandLine(): GeneralCommandLine = GeneralCommandLine(defaultExecutable, "lsp")
    }
}
