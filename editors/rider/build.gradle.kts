plugins {
    id("java")
    kotlin("jvm") version "2.4.0"
    id("org.jetbrains.intellij.platform") version "2.19.0"
}

repositories {
    mavenCentral()
    intellijPlatform { defaultRepositories() }
}

dependencies {
    // The Rider installer is not supported as a target; use the Maven distribution.
    intellijPlatform { rider("2026.2") { useInstaller = false } }
    testImplementation(kotlin("test"))
    // The platform's JUnit 5 session listener, found on the test classpath, loads JUnit 4 classes.
    testRuntimeOnly("junit:junit:4.13.2")
}

tasks.test { useJUnitPlatform() }

intellijPlatform {
    pluginConfiguration {
        ideaVersion { sinceBuild = "262" }
    }
}
