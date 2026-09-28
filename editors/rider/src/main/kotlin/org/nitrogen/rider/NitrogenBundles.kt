package org.nitrogen.rider

import com.intellij.execution.ExecutionException
import com.intellij.openapi.application.PathManager
import com.intellij.openapi.util.SystemInfo
import com.intellij.util.system.CpuArch
import java.io.InputStream
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.security.DigestInputStream
import java.security.MessageDigest

/**
 * Server executables packaged by `nitrogen generate rider --bundle`. The generator records them in
 * /nitrogen-bundles.json with their SHA-256; the matching one is extracted from the plugin jar,
 * verified, and run from the IDE's system directory.
 */
object NitrogenBundles {
    data class Bundle(val target: String, val file: String, val sha256: String)

    private val entry = Regex("""\{\s*"target":\s*"([^"]+)",\s*"file":\s*"([^"]+)",\s*"sha256":\s*"([0-9a-f]{64})"\s*}""")

    /** The entries of the generator's descriptor. */
    fun parse(json: String): List<Bundle> =
        entry.findAll(json).map { Bundle(it.groupValues[1], it.groupValues[2], it.groupValues[3]) }.toList()

    /** The bundle target of an operating system and CPU, named as the generator names them; null when none can match. */
    fun target(mac: Boolean, linux: Boolean, windows: Boolean, arm64: Boolean, x64: Boolean): String? = when {
        mac && arm64 -> "macos-aarch64"
        mac && x64 -> "macos-x64"
        linux && x64 -> "linux-x64"
        windows && x64 -> "windows-x64"
        else -> null
    }

    /** The bundled executable for this machine, or null when the plugin bundles none for it. */
    fun current(): String? = executableFor(
        target(SystemInfo.isMac, SystemInfo.isLinux, SystemInfo.isWindows, CpuArch.isArm64(), CpuArch.isIntel64()),
        { name -> NitrogenBundles::class.java.getResourceAsStream("/$name") },
        PathManager.getSystemDir().resolve("nitrogen-bundles"))

    /**
     * The path of [target]'s bundle, extracted under [cache] in a directory named by its SHA-256; null
     * when [resource] has no descriptor or no bundle for [target]. A copy that no longer matches is
     * replaced, and a missing or mismatched bundle is an error rather than a different binary.
     */
    fun executableFor(target: String?, resource: (String) -> InputStream?, cache: Path): String? {
        val descriptor = resource("nitrogen-bundles.json")?.use { it.readBytes().toString(Charsets.UTF_8) } ?: return null
        val bundle = parse(descriptor).firstOrNull { it.target == target } ?: return null
        val destination = cache.resolve(bundle.sha256).resolve(bundle.file.substringAfterLast('/'))
        if (!Files.isRegularFile(destination) || sha256(destination) != bundle.sha256) {
            Files.createDirectories(destination.parent)
            val staging = Files.createTempFile(destination.parent, "nitrogen", ".tmp")
            try {
                val digest = MessageDigest.getInstance("SHA-256")
                val stream = resource(bundle.file)
                    ?: throw ExecutionException("Bundled Nitrogen server '${bundle.file}' is missing from the plugin.")
                DigestInputStream(stream, digest).use { Files.copy(it, staging, StandardCopyOption.REPLACE_EXISTING) }
                if (hex(digest.digest()) != bundle.sha256)
                    throw ExecutionException("Bundled Nitrogen server '${bundle.file}' does not match its recorded SHA-256.")
                Files.move(staging, destination, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
            } finally {
                Files.deleteIfExists(staging)
            }
        }
        destination.toFile().setExecutable(true)
        return destination.toString()
    }

    private fun sha256(file: Path): String {
        val digest = MessageDigest.getInstance("SHA-256")
        DigestInputStream(Files.newInputStream(file), digest).use { it.transferTo(java.io.OutputStream.nullOutputStream()) }
        return hex(digest.digest())
    }

    private fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }
}
