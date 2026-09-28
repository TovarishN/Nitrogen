package org.nitrogen.rider

import com.intellij.openapi.fileChooser.FileChooserDescriptorFactory
import com.intellij.openapi.options.Configurable
import com.intellij.openapi.project.ProjectManager
import com.intellij.openapi.ui.TextFieldWithBrowseButton
import com.intellij.platform.lsp.api.LspClientManager
import java.awt.BorderLayout
import javax.swing.JComponent
import javax.swing.JLabel
import javax.swing.JPanel

/** Settings | Tools | Nitrogen: the executable the language server runs; applying restarts running servers. */
class NitrogenConfigurable : Configurable {
    private var path: TextFieldWithBrowseButton? = null

    override fun getDisplayName(): String = "Nitrogen"

    override fun createComponent(): JComponent {
        val field = TextFieldWithBrowseButton()
        field.addBrowseFolderListener(null, FileChooserDescriptorFactory.singleFile())
        field.text = NitrogenSettings.getInstance().executable
        path = field
        val row = JPanel(BorderLayout(8, 0))
        row.add(JLabel("Nitrogen executable:"), BorderLayout.WEST)
        row.add(field, BorderLayout.CENTER)
        val panel = JPanel(BorderLayout(0, 4))
        panel.add(row, BorderLayout.NORTH)
        panel.add(JLabel("Leave blank for the plugin's default. A name without a path is looked up on PATH."), BorderLayout.CENTER)
        return panel
    }

    override fun isModified(): Boolean = path?.text?.trim() != NitrogenSettings.getInstance().executable

    override fun apply() {
        NitrogenSettings.getInstance().executable = path?.text.orEmpty()
        for (project in ProjectManager.getInstance().openProjects)
            LspClientManager.getInstance(project).stopAndRestartClientsIfNeeded(NitrogenLspSupport::class.java)
    }

    override fun reset() {
        path?.text = NitrogenSettings.getInstance().executable
    }

    override fun disposeUIResources() {
        path = null
    }
}
