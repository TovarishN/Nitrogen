package org.nitrogen.rider

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspIntegrationProvider
import com.intellij.platform.lsp.api.LspIntegrationProvider.LspClientStarter
import com.intellij.platform.lsp.api.ProjectWideLspClientDescriptor
import com.intellij.platform.lsp.api.customization.LspCustomization
import java.io.File

class NitrogenLspSupport : LspIntegrationProvider {
    companion object {
        const val defaultExecutable = "nitrogen"

        fun commandLine(): GeneralCommandLine =
            GeneralCommandLine(NitrogenSettings.getInstance().resolveExecutable(defaultExecutable), "lsp")
    }

    override fun fileOpened(project: Project, file: VirtualFile, clientStarter: LspClientStarter) {
        if (file.extension == "ngr") clientStarter.ensureClientStarted(NitrogenClientDescriptor(project))
        // The project's nitrogen.json declares the languages a C# string can be tagged with.
        if (NitrogenCSharpClient.isCSharp(file) && project.basePath?.let { File(it, "nitrogen.json").isFile } == true)
            clientStarter.ensureClientStarted(NitrogenCSharpClient(project, "Nitrogen", ::commandLine) {
                NitrogenCSharpClient.languagesOfOtherPlugins("org.nitrogen.rider")
            })
    }

    private class NitrogenClientDescriptor(project: Project) : ProjectWideLspClientDescriptor(project, "Nitrogen") {
        override fun isSupportedFile(file: VirtualFile): Boolean = file.extension == "ngr"
        override fun createCommandLine(): GeneralCommandLine = commandLine()
        override val lspCustomization: LspCustomization = NitrogenCustomization()
    }
}
