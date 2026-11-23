package app.sidecarmonitor.data

import android.content.Context
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * What the kiosk's "Pair phone" QR code contains: where to reach the kiosk (home Wi-Fi first,
 * then Tailscale for when you're away), the pairing key, and the SHA-256 of its TLS certificate.
 */
@Serializable
data class Pairing(
    val v: Int = 1,
    val url: String = "",
    val urls: List<String> = emptyList(),
    val key: String,
    val cert: String,
) {
    /** All addresses to try, in order. */
    val addresses: List<String> get() = (urls.ifEmpty { listOf(url) }).filter { it.isNotBlank() }.distinct()

    companion object {
        private val json = Json { ignoreUnknownKeys = true }

        fun parse(qr: String): Pairing? = runCatching { json.decodeFromString<Pairing>(qr) }
            .getOrNull()
            ?.takeIf { it.key.length >= 32 && it.cert.length == 64 && it.addresses.isNotEmpty() }

        fun encode(p: Pairing): String = json.encodeToString(serializer(), p)
    }
}

/** Keeps the pairing in the app's private storage. */
class PairingStore(context: Context) {
    private val prefs = context.getSharedPreferences("pairing", Context.MODE_PRIVATE)

    fun load(): Pairing? = prefs.getString("pairing", null)?.let(Pairing::parse)

    fun save(p: Pairing) = prefs.edit().putString("pairing", Pairing.encode(p)).apply()

    fun clear() = prefs.edit().remove("pairing").apply()
}
