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
    val id: String = "",
    /** Pictures the kiosk took when it happened: 1 = start, 2 = when it became a person or vehicle. */
    val snapshots: List<Int> = emptyList(),
    /** The decision model's label for the picture, e.g. "Delivery · FYI" (empty until judged). */
    val ai: String = "",
    val attention: String = "",
    /** "camera", "door" (doors, locks, garage, alarms), "house" (automations) or "sidecar" (SideCar's own actions). */
    val source: String = "camera",
    val alert: Boolean = false,
    /** Cleared from the feed (still shown in History). */
    val cleared: Boolean = false,
)

@Serializable
data class HomeDevice(val name: String, val domain: String, val state: String, val area: String, val on: Boolean)

@Serializable
data class HomeScript(val tool: String, val name: String)

/** The kiosk's Home Assistant view: devices by room, scripts, recent actions, and whether SideCar's control is paused. */
@Serializable
data class HomeInfo(
    val available: Boolean = false,
    val paused: String? = null,
    val lightsOn: Int = 0,
    val lights: Int = 0,
    val devices: List<HomeDevice> = emptyList(),
    val scripts: List<HomeScript> = emptyList(),
    val recent: List<String> = emptyList(),
)

@Serializable
data class LiPost(
    val id: String, val author: String = "", val authorUrl: String = "", val headline: String = "", val age: String = "",
    val context: String = "", val text: String = "", val link: String = "", val promoted: Boolean = false,
    val signal: Boolean = false, val yours: String? = null, val score: Double = 0.0,
)

@Serializable
data class LinkedInInfo(
    val available: Boolean = false, val running: Boolean = false, val needsAttention: Boolean = false,
    val lastRun: String? = null, val lastRunResult: String = "", val digest: String = "",
    val signal: List<LiPost> = emptyList(), val noise: List<LiPost> = emptyList(),
    val podcast: LiPodcast = LiPodcast(),
)

@Serializable
data class LiPodcast(
    val status: String = "", val at: String? = null, val seconds: Double = 0.0, val posts: Int = 0,
    val cleared: Boolean = false, val candidates: Int = 0,
)

@Serializable
data class LiPerson(val url: String, val name: String = "", val headline: String = "", val summary: String = "", val posts: List<LiPost> = emptyList())

@Serializable
data class ReminderItem(val id: String, val text: String, val dueText: String = "", val repeats: String = "", val snoozes: Int = 0)

@Serializable
data class RemindersInfo(val available: Boolean = false, val due: List<ReminderItem> = emptyList())

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

    suspend fun home(): HomeInfo = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/home").build()).use { json.decodeFromString(it.body.string()) }
    }

    /** Posts a small JSON body to the kiosk; returns the error text, or null when it worked. */
    private suspend fun postJson(path: String, body: kotlinx.serialization.json.JsonObject): String? = withContext(Dispatchers.IO) {
        val req = Request.Builder().url("$base$path")
            .post(body.toString().toRequestBody("application/json".toMediaType())).build()
        runCatching { call(req).use { null } }.getOrElse { it.message ?: "Failed" }
    }

    suspend fun linkedIn(): LinkedInInfo = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/linkedin").build()).use { json.decodeFromString(it.body.string()) }
    }

    suspend fun linkedInLabel(id: String, signal: Boolean) =
        postJson("/api/linkedin/label", buildJsonObject { put("id", id); put("signal", signal) })

    suspend fun linkedInRun() = postJson("/api/linkedin/run", buildJsonObject { })

    suspend fun linkedInPodcastPrepare() = postJson("/api/linkedin/podcast", buildJsonObject { })
    suspend fun linkedInPodcastClear() = postJson("/api/linkedin/podcast/clear", buildJsonObject { })

    /** Interrupt: what you said and where the episode was; returns SideCar's short confirmation. */
    suspend fun linkedInPodcastInterrupt(text: String, positionSeconds: Double): String = withContext(Dispatchers.IO) {
        val body = buildJsonObject { put("text", text); put("position", positionSeconds) }.toString()
            .toRequestBody("application/json".toMediaType())
        call(Request.Builder().url("$base/api/linkedin/podcast/interrupt").post(body).build()).use {
            json.parseToJsonElement(it.body.string()).jsonObject["reply"]?.jsonPrimitive?.content ?: "Saved"
        }
    }

    /** Downloads the episode's audio (WAV) through the pinned connection. */
    suspend fun linkedInPodcastAudio(): ByteArray = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/linkedin/podcast/audio").build()).use { it.body.bytes() }
    }

    suspend fun linkedInPerson(url: String): LiPerson = withContext(Dispatchers.IO) {
        val u = "$base/api/linkedin/person?url=" + java.net.URLEncoder.encode(url, "UTF-8")
        call(Request.Builder().url(u).build()).use { json.decodeFromString(it.body.string()) }
    }

    suspend fun linkedInRefresh(url: String) = postJson("/api/linkedin/person/refresh", buildJsonObject { put("url", url) })

    /** Reminders that are due now (they nag until handled). */
    suspend fun clearActivity() = postJson("/api/activity/clear", buildJsonObject { })

    suspend fun dueReminders(): List<ReminderItem> = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/reminders").build()).use {
            json.decodeFromString<RemindersInfo>(it.body.string()).due
        }
    }

    /** done | todo | cancel | snooze (minutes; -1 = tomorrow 9 AM). Returns the error, or null. */
    suspend fun reminderAction(id: String, action: String, minutes: Int = 60) =
        postJson("/api/reminders/$id/action", buildJsonObject { put("action", action); put("minutes", minutes) })

    suspend fun homeSet(d: HomeDevice, on: Boolean) =
        postJson("/api/home/set", buildJsonObject { put("name", d.name); put("domain", d.domain); put("on", on) })

    suspend fun homeScript(s: HomeScript) = postJson("/api/home/script", buildJsonObject { put("tool", s.tool) })

    suspend fun homeResume() = postJson("/api/home/resume", buildJsonObject { })

    /** A picture the kiosk took when an activity event happened. */
    suspend fun eventSnapshot(id: String, n: Int): ByteArray = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/events/$id/snapshot/$n").build()).use { it.body.bytes() }
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

    // ---- To-do list ------------------------------------------------------------------------

    suspend fun tasks(): TasksPayload = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/tasks").build()).use { json.decodeFromString(it.body.string()) }
    }

    suspend fun week(start: String?): WeekPlan = withContext(Dispatchers.IO) {
        val q = start?.let { "?start=$it" } ?: ""
        call(Request.Builder().url("$base/api/tasks/week$q").build()).use { json.decodeFromString(it.body.string()) }
    }

    /** Runs an action on any task by id (not just the open one). */
    suspend fun taskActionById(id: String, action: String, extra: Map<String, Any?> = emptyMap()) = taskAction(id, action, extra)

    suspend fun task(id: String): TaskDetail = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/tasks/$id").build()).use { json.decodeFromString(it.body.string()) }
    }

    suspend fun addTask(title: String, pinToday: Boolean): TaskDetail = withContext(Dispatchers.IO) {
        val body = buildJsonObject { put("title", title); put("pinToday", pinToday) }.toString()
            .toRequestBody("application/json".toMediaType())
        callOrRule(Request.Builder().url("$base/api/tasks").post(body).build())
    }

    /** Any task action: done, reopen, pin, unpin, help_on, help_off, drop_date, resume, accept, dismiss,
     *  undo (entryId), note (text), deliverable (content), edit (fields + expectedVersion), take_pass. */
    suspend fun taskAction(id: String, action: String, extra: Map<String, Any?> = emptyMap()): TaskDetail? =
        withContext(Dispatchers.IO) {
            val body = buildJsonObject {
                put("action", action)
                extra.forEach { (k, v) ->
                    when (v) {
                        is String -> put(k, v)
                        is Boolean -> put(k, v)
                        is Int -> put(k, v)
                        null -> {}
                        else -> put(k, v.toString())
                    }
                }
            }.toString().toRequestBody("application/json".toMediaType())
            if (action == "take_pass") {
                call(Request.Builder().url("$base/api/tasks/$id/action").post(body).build()).close()
                null
            } else callOrRule(Request.Builder().url("$base/api/tasks/$id/action").post(body).build())
        }

    /** Returns the task, or throws with the kiosk's reason (e.g. "This task changed since you opened it"). */
    private fun callOrRule(req: Request): TaskDetail {
        client.newCall(req).execute().use { r ->
            val text = r.body.string()
            if (r.code == 409 || r.code == 400) {
                val msg = runCatching { json.parseToJsonElement(text).jsonObject["error"]?.jsonPrimitive?.content }.getOrNull()
                throw IllegalStateException(msg ?: "The kiosk refused that change")
            }
            if (!r.isSuccessful) throw IllegalStateException("Kiosk returned HTTP ${r.code}")
            return json.decodeFromString(text)
        }
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
