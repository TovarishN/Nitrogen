package org.nitrogen.rider

import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage

@State(name = "NitrogenSettings", storages = [Storage("nitrogen.xml")])
class NitrogenSettings : PersistentStateComponent<NitrogenSettings.State> {
    data class State(var executable: String = "nitrogen")
    private var state = State()
    override fun getState(): State = state
    override fun loadState(state: State) { this.state = state }
}
