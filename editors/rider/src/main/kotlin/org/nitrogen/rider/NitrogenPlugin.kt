package org.nitrogen.rider

/**
 * This plugin's application-wide names. Rider keeps settings state names for the whole IDE, so each
 * generated plugin has its own and several Nitrogen plugins can be installed together.
 */
object NitrogenPlugin {
    const val SETTINGS_NAME = "NitrogenSettings"
    const val SETTINGS_FILE = "nitrogen.xml"
}
