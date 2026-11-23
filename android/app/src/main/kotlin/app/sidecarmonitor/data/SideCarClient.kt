package app.sidecarmonitor.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

// ---- What the kiosk sends back -------------------------------------------------------------

@Serializable
data class KioskStatus(
    val weather: JsonObject? = null,
    val weatherSummary: String = "",
    val cameras: List<CameraInfo> = emptyList(),
    val activity: List<ActivityItem> = emptyList(),
)

@Serializable
data class CameraInfo(val index: Int, val name: String, val live: Boolean)

@Serializable
data class ActivityItem(
    val camera: String,
    val kind: String,
    val start: String,
    val active: Boolean,
    val duration: String,
)

/** One message from the agent over the chat WebSocket. */
data class ChatEvent(val type: String, val id: String?, val text: String?, val name: String?, val message: String?)

/**
 * Talks to the kiosk gateway over HTTPS. Trust comes from pinning: the kiosk's self-signed certificate
 * is accepted only if its SHA-256 matches the fingerprint from the pairing QR code, and every request
 * carries the pairing key. Tries the home address first, then Tailscale.
 */
class SideCarClient(private val pairing: Pairing) {
    private val json = Json { ignoreUnknownKeys = true }
    private val client: OkHttpClient
    @Volatile private var base: String = pairing.addresses.first()

    init {
        val pin = PinnedTrustManager(pairing.cert)
        val ssl = SSLContext.getInstance("TLS").apply { init(null, arrayOf(pin), null) }
        client = OkHttpClient.Builder()
            .sslSocketFactory(ssl.socketFactory, pin)
            // The certificate itself is pinned, so the IP/hostname in the URL doesn't need to match it.
            .hostnameVerifier { _, _ -> true }
            .connectTimeout(4, TimeUnit.SECONDS)
            .readTimeout(90, TimeUnit.SECONDS)
            .pingInterval(20, TimeUnit.SECONDS)
            .addInterceptor { chain ->
                chain.proceed(chain.request().newBuilder().header("Authorization", "Bearer ${pairing.key}").build())
            }
            .build()
    }

    /** Which address is in use, for display ("Home" or "Tailscale"). */
    val connectionLabel: String
        get() = if (base.contains("://100.")) "via Tailscale" else "on home Wi-Fi"

    /** Finds an address that answers, trying each in order. */
    suspend fun connect(): Boolean = withContext(Dispatchers.IO) {
        for (address in pairing.addresses) {
            val ok = runCatching {
                client.newCall(Request.Builder().url("$address/api/ping").build()).execute().use { it.isSuccessful }
            }.getOrDefault(false)
            if (ok) { base = address; return@withContext true }
        }
        false
    }

    suspend fun status(): KioskStatus = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/status").build()).use { json.decodeFromString(it.body.string()) }
    }

    suspend fun snapshot(index: Int): ByteArray = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/cameras/$index/snapshot").build()).use { it.body.bytes() }
    }

    /** Sends a 16 kHz mono WAV recording; returns what was said. */
    suspend fun listen(wav: ByteArray): String = withContext(Dispatchers.IO) {
        val req = Request.Builder().url("$base/api/listen").post(wav.toRequestBody("audio/wav".toMediaType())).build()
        call(req).use { json.parseToJsonElement(it.body.string()).jsonObject["text"]?.jsonPrimitive?.content ?: "" }
    }

    /** The kiosk's MAI voice reading [text], as a WAV file. */
    suspend fun speak(text: String): ByteArray = withContext(Dispatchers.IO) {
        val body = buildJsonObject { put("text", text) }.toString().toRequestBody("application/json".toMediaType())
        call(Request.Builder().url("$base/api/speak").post(body).build()).use { it.body.bytes() }
    }

    /** Opens the chat WebSocket. Send with [ChatConnection.send]; events arrive on [ChatConnection.events]. */
    fun openChat(): ChatConnection = ChatConnection()

    inner class ChatConnection {
        private var socket: WebSocket? = null

        val events: Flow<ChatEvent> = callbackFlow {
            val ws = client.newWebSocket(Request.Builder().url(base.replace("https://", "wss://") + "/api/chat").build(),
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        val o = runCatching { json.parseToJsonElement(text).jsonObject }.getOrNull() ?: return
                        fun s(k: String) = (o[k] as? JsonElement)?.let { runCatching { it.jsonPrimitive.content }.getOrNull() }
                        trySend(ChatEvent(s("type") ?: "", s("id"), s("text"), s("name"), s("message")))
                    }

                    override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                        trySend(ChatEvent("error", null, null, null, "Lost connection to the kiosk: ${t.message}"))
                        close()
                    }

                    override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { close() }
                })
            socket = ws
            awaitClose { ws.close(1000, "bye") }
        }

        fun send(text: String) =
            socket?.send(buildJsonObject { put("type", "chat"); put("text", text) }.toString()) ?: false

        fun endConversation() = socket?.send("""{"type":"end_conversation"}""") ?: false
    }

    private fun call(req: Request): Response {
        val r = client.newCall(req).execute()
        if (!r.isSuccessful) { r.close(); throw IllegalStateException("Kiosk returned HTTP ${r.code}") }
        return r
    }
}

/** Accepts only the kiosk certificate whose SHA-256 matches the paired fingerprint. */
private class PinnedTrustManager(pinHex: String) : X509TrustManager {
    private val pin = pinHex.uppercase()

    override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
        val leaf = chain.firstOrNull() ?: throw CertificateException("No certificate")
        val hash = MessageDigest.getInstance("SHA-256").digest(leaf.encoded).joinToString("") { "%02X".format(it) }
        if (hash != pin) throw CertificateException("Kiosk certificate doesn't match the paired one")
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) =
        throw CertificateException("Not used")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
