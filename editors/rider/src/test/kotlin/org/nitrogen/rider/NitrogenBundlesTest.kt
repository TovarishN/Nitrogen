package org.nitrogen.rider

import com.intellij.execution.ExecutionException
import java.io.InputStream
import java.nio.file.Files
import java.nio.file.Path
import java.security.MessageDigest
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class NitrogenBundlesTest {
    private val cache: Path = Files.createTempDirectory("nitrogen-bundles-test")
    private val server = "#!/bin/sh\necho nitrogen\n".toByteArray()
    private val serverHash = MessageDigest.getInstance("SHA-256").digest(server).joinToString("") { "%02x".format(it) }

    /** The descriptor exactly as `nitrogen generate rider --bundle macos-aarch64=...` writes it. */
    private val descriptor = """
        {
        "bundles": [
          { "target": "linux-x64", "file": "bundled/linux-x64/nitrogen", "sha256": "${"0".repeat(64)}" },
          { "target": "macos-aarch64", "file": "bundled/macos-aarch64/nitrogen", "sha256": "$serverHash" }
        ]
        }
    """.trimIndent()

    private fun resources(vararg entries: Pair<String, ByteArray>): (String) -> InputStream? {
        val map = entries.toMap()
        return { name -> map[name]?.inputStream() }
    }

    @AfterTest
    fun cleanUp() {
        cache.toFile().deleteRecursively()
    }

    @Test
    fun `targets name the operating system and cpu as the generator does`() {
        assertEquals("macos-aarch64", NitrogenBundles.target(mac = true, linux = false, windows = false, arm64 = true, x64 = false))
        assertEquals("macos-x64", NitrogenBundles.target(mac = true, linux = false, windows = false, arm64 = false, x64 = true))
        assertEquals("linux-x64", NitrogenBundles.target(mac = false, linux = true, windows = false, arm64 = false, x64 = true))
        assertEquals("windows-x64", NitrogenBundles.target(mac = false, linux = false, windows = true, arm64 = false, x64 = true))
        assertNull(NitrogenBundles.target(mac = false, linux = true, windows = false, arm64 = true, x64 = false))
    }

    @Test
    fun `the generated descriptor lists every bundle`() {
        val bundles = NitrogenBundles.parse(descriptor)
        assertEquals(listOf("linux-x64", "macos-aarch64"), bundles.map { it.target })
        assertEquals("bundled/macos-aarch64/nitrogen", bundles[1].file)
        assertEquals(serverHash, bundles[1].sha256)
    }

    @Test
    fun `no descriptor or no bundle for this machine selects nothing`() {
        assertNull(NitrogenBundles.executableFor("macos-aarch64", resources(), cache))
        val onlyLinux = resources("nitrogen-bundles.json" to descriptor.toByteArray())
        assertNull(NitrogenBundles.executableFor("windows-x64", onlyLinux, cache))
        assertNull(NitrogenBundles.executableFor(null, onlyLinux, cache))
    }

    @Test
    fun `the matching bundle is extracted, verified and made executable`() {
        val plugin = resources("nitrogen-bundles.json" to descriptor.toByteArray(), "bundled/macos-aarch64/nitrogen" to server)
        val path = Path.of(NitrogenBundles.executableFor("macos-aarch64", plugin, cache)!!)
        assertTrue(path.startsWith(cache.resolve(serverHash)))
        assertEquals(server.toList(), Files.readAllBytes(path).toList())
        assertTrue(Files.isExecutable(path))

        // A damaged extracted copy is replaced rather than run.
        Files.write(path, "tampered".toByteArray())
        assertEquals(path.toString(), NitrogenBundles.executableFor("macos-aarch64", plugin, cache))
        assertEquals(server.toList(), Files.readAllBytes(path).toList())
    }

    @Test
    fun `a bundle that does not match its checksum or is missing is an error`() {
        val tampered = resources("nitrogen-bundles.json" to descriptor.toByteArray(), "bundled/macos-aarch64/nitrogen" to "other".toByteArray())
        assertFailsWith<ExecutionException> { NitrogenBundles.executableFor("macos-aarch64", tampered, cache) }
        val missing = resources("nitrogen-bundles.json" to descriptor.toByteArray())
        assertFailsWith<ExecutionException> { NitrogenBundles.executableFor("macos-aarch64", missing, cache) }
    }
}
