package org.nitrogen.rider

import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.PathEnvironmentVariableUtil
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import java.io.File

/** The Nitrogen executable chosen in Settings | Tools | Nitrogen; blank means the plugin's default. */
@Service(Service.Level.APP)
@State(name = "NitrogenSettings", storages = [Storage("nitrogen.xml")])
class NitrogenSettings : PersistentStateComponent<NitrogenSettings.State> {
    data class State(var executable: String = "")

    private var state = State()

    override fun getState(): State = state

    override fun loadState(state: State) {
        this.state = state
    }

    var executable: String
        get() = state.executable
        set(value) {
            state.executable = value.trim()
        }

    /** The executable to run: the setting, else [default]; a bare name is looked up on PATH. */
    fun resolveExecutable(default: String): String {
        val name = executable.ifBlank { default }
        val file = File(name)
        val found = if (file.isAbsolute || name.contains(File.separatorChar)) file.takeIf { it.canExecute() }
            else PathEnvironmentVariableUtil.findInPath(name)
        return found?.absolutePath ?: throw ExecutionException(
            "Nitrogen executable '$name' was not found. Set its path in Settings | Tools | Nitrogen.")
    }

    companion object {
        fun getInstance(): NitrogenSettings =
            ApplicationManager.getApplication().getService(NitrogenSettings::class.java)
    }
}
