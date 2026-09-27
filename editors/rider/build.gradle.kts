plugins {
    id("java")
    kotlin("jvm") version "2.0.21"
    id("org.jetbrains.intellij.platform") version "2.2.1"
}

repositories {
    mavenCentral()
    intellijPlatform { defaultRepositories() }
}

dependencies {
    intellijPlatform { rider("2026.2") }
}

intellijPlatform {
    pluginConfiguration {
        ideaVersion { sinceBuild = "262" }
    }
}
