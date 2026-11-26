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
    /** "camera", "door" (doors, locks, garage, alarms), "house" (automations), "weather" (weather alerts) or "sidecar" (SideCar's own actions). */
    val source: String = "camera",
    val alert: Boolean = false,
    /** Cleared from the feed (still shown in History). */
    val cleared: Boolean = false,
    /** One line of context, e.g. a weather alert's "from NW, average 11 mph; forecast had gusts of 9 mph". */
    val detail: String = "",
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
    val health: String = "", val sources: List<LiSource> = emptyList(),
)

@Serializable
data class LiPodcast(
    val status: String = "", val at: String? = null, val seconds: Double = 0.0, val posts: Int = 0,
    val cleared: Boolean = false, val candidates: Int = 0, val hasNews: Boolean = false,
    /** Where each paragraph starts (seconds) and the posts it covers, for "Now discussing". */
    val segments: List<PodSegment> = emptyList(),
) {
    fun postsAt(seconds: Double): List<String> = segments.lastOrNull { it.start <= seconds + 0.3 }?.posts.orEmpty()
}

@Serializable
data class PodSegment(val start: Double = 0.0, val posts: List<String> = emptyList())

@Serializable
data class LiPerson(val url: String, val name: String = "", val headline: String = "", val summary: String = "", val posts: List<LiPost> = emptyList())

@Serializable
data class ReminderItem(val id: String, val text: String, val dueText: String = "", val repeats: String = "", val snoozes: Int = 0)

@Serializable
data class RemindersInfo(val available: Boolean = false, val due: List<ReminderItem> = emptyList())

@Serializable
data class XPost(
    val id: String, val author: String = "", val username: String = "", val authorId: String = "", val text: String = "",
    val created: String = "", val likes: Int = 0, val reposts: Int = 0, val replies: Int = 0, val repost: Boolean = false,
    val link: String = "", val signal: Boolean = false, val yours: String? = null, val saved: Boolean = false, val analysis: String = "",
)

@Serializable
data class XAccountInfo(val id: String = "", val name: String = "", val username: String = "", val seen: Int = 0, val signal: Int = 0, val noise: Int = 0)

@Serializable
data class XInfo(
    val available: Boolean = false, val busy: Boolean = false, val lastUpdate: String? = null, val lastResult: String = "", val postsRead: Int = 0,
    val signal: List<XPost> = emptyList(), val noise: List<XPost> = emptyList(), val saved: List<XPost> = emptyList(),
    val listCandidates: List<XAccountInfo> = emptyList(), val noisy: List<XAccountInfo> = emptyList(),
    val podcast: LiPodcast = LiPodcast(), val health: String = "",
)

@Serializable
data class IdeaEvidence(val source: String = "", val author: String = "", val quote: String = "", val link: String = "", val at: String = "")

@Serializable
data class IdeaInfo(
    val id: String = "", val kind: String = "", val kindLabel: String = "", val title: String = "", val pitch: String = "", val why: String = "",
    val whoPays: String = "", val smallest: String = "", val effort: String = "", val score: Int = 0, val status: String = "",
    val pmcollab: Boolean = false, val sources: Int = 0, val evidence: List<IdeaEvidence> = emptyList(), val signal: String? = null,
)

@Serializable
data class IdeasInfo(val available: Boolean = false, val busy: Boolean = false, val lastResult: String = "", val ideas: List<IdeaInfo> = emptyList(),
                     val pmcollab: Boolean = false)

@Serializable
data class MailItem(val id: String = "", val from: String = "", val subject: String = "", val received: String = "", val folder: String = "",
                    val why: String = "", val summary: String = "", val flag: Boolean = false, val link: String = "",
                    val canReturn: Boolean = false, val returned: Boolean = false)

@Serializable
data class MailDigest(val id: String = "", val at: String = "", val title: String = "", val counts: String = "", val text: String = "", val status: String = "", val sorted: Int = 0,
                      val kept: Int = 0, val audio: Boolean = false, val items: List<MailItem> = emptyList(), val order: List<String> = emptyList())

@Serializable
data class MailInfo(val available: Boolean = false, val trial: Boolean = false, val busy: Boolean = false, val lastResult: String = "", val catchUp: String = "",
                    val folders: List<String> = emptyList(), val digests: List<MailDigest> = emptyList())

@Serializable
data class FeedSummary(val signal: Int = 0, val newest: String? = null)

@Serializable
data class HouseSummary(val paused: String? = null, val lightsOn: Int = 0, val lights: Int = 0, val locks: Int = 0, val unlocked: List<String> = emptyList())

@Serializable
data class HomeSummary(val x: FeedSummary? = null, val linkedin: FeedSummary? = null, val house: HouseSummary? = null, val weatherNext: String? = null)

@Serializable
data class LiSource(val author: String = "", val headline: String = "", val posts: Int = 0, val signal: Int = 0, val ads: Int = 0, val suppressed: Boolean = false)

@Serializable
data class MeetingInfo(
    val key: String = "", val calendar: String = "", val subject: String = "", val start: String = "", val end: String = "",
    val location: String = "", val organizer: String = "", val joinUrl: String = "", val showAs: String = "", val response: String = "",
    val worthPrep: Boolean = false, val prep: String = "",
)

@Serializable
data class MeetingsInfo(val available: Boolean = false, val leadMinutes: Int = 30, val meetings: List<MeetingInfo> = emptyList())

@Serializable
data class ForecastInfo(
    val updated: String = "", val station: String = "", val highlights: List<String> = emptyList(), val hours: List<String> = emptyList(),
    val days: List<String> = emptyList(), val error: String? = null,
    val hourly: List<ForecastHour> = emptyList(), val daily: List<ForecastDay> = emptyList(),
)

@Serializable
data class ForecastHour(val time: String = "", val icon: String = "", val temp: String = "", val conditions: String = "", val rain: String = "", val wind: String = "")

@Serializable
data class ForecastDay(
    val day: String = "", val date: String = "", val icon: String = "", val conditions: String = "", val high: String = "", val low: String = "",
    val highC: Double? = null, val lowC: Double? = null, val rain: String = "", val sunrise: String? = null, val sunset: String? = null,
)

@Serializable
data class CalEventInfo(
    val calendar: String = "", val subject: String = "", val start: String = "", val end: String = "", val allDay: Boolean = false,
    val showAs: String = "", val location: String = "", val joinUrl: String = "", val organizer: String = "", val webLink: String = "",
)

@Serializable
data class CalDay(val available: Boolean = false, val date: String = "", val calendars: List<String> = emptyList(), val events: List<CalEventInfo> = emptyList())

@Serializable
data class BriefInfo(val available: Boolean = false, val kind: String = "", val title: String = "", val at: String = "", val text: String = "", val dismissed: Boolean = false)

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
            // Short outages: reads fall back to the last saved copy instead of failing (see OfflineCache).
            .addInterceptor(OfflineCache.interceptor)
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

    /**
     * Any write to the kiosk goes through the outbox: saved first, sent with an Idempotency-Key, kept and retried if
     * the kiosk can't be reached. Returns null when it was sent or is waiting to send, or the kiosk's reason when it
     * refused.
     */
    private suspend fun postJson(path: String, body: kotlinx.serialization.json.JsonObject): String? =
        Outbox.post(this, path, body.toString(), labelFor(path))

    /** One attempt at a write: Sent, Offline (retry later) or Refused (the kiosk said no; don't retry). */
    suspend fun postRaw(path: String, body: String, key: String): Outbox.Result = withContext(Dispatchers.IO) {
        val req = Request.Builder().url("$base$path").header("Idempotency-Key", key)
            .post(body.toRequestBody("application/json".toMediaType())).build()
        try {
            client.newCall(req).execute().use { r ->
                val text = r.body.string()
                when {
                    r.isSuccessful -> Outbox.Result.Sent(text)
                    r.code >= 500 || r.code == 408 || r.code == 429 -> Outbox.Result.Offline("Kiosk returned HTTP ${r.code}")
                    else -> Outbox.Result.Refused(runCatching { json.parseToJsonElement(text).jsonObject["error"]?.jsonPrimitive?.content }.getOrNull()
                        ?: "The kiosk refused it (HTTP ${r.code})")
                }
            }
        } catch (e: java.io.IOException) { Outbox.Result.Offline(e.message ?: "Can't reach the kiosk") }
    }

    /** A queued chat message, answered as one turn (no live connection needed). Throws IOException to retry later. */
    suspend fun ask(text: String, key: String): String = withContext(Dispatchers.IO) {
        val body = buildJsonObject { put("text", text) }.toString()
        when (val r = postRaw("/api/ask", body, key)) {
            is Outbox.Result.Sent -> json.parseToJsonElement(r.body).jsonObject["reply"]?.jsonPrimitive?.content ?: ""
            is Outbox.Result.Offline -> throw java.io.IOException(r.reason)
            is Outbox.Result.Refused -> throw IllegalStateException(r.reason)
        }
    }

    private fun labelFor(path: String) = when {
        path.startsWith("/api/tasks") -> "To-do change"
        path.startsWith("/api/reminders") -> "Reminder"
        path.startsWith("/api/home") -> "House control"
        path.startsWith("/api/linkedin") -> "LinkedIn action"
        path.startsWith("/api/x") -> "X action"
        path.startsWith("/api/ideas") -> "Idea action"
        path.startsWith("/api/mail") -> "Mail action"
        path.startsWith("/api/calendar") -> "Calendar change"
        path.startsWith("/api/brief") -> "Brief"
        path.startsWith("/api/activity") -> "Activity"
        else -> "Change"
    }

    /** A task write: sent with a key; if the kiosk can't be reached it's kept in the outbox and this throws [Queued]. */
    private fun taskPost(path: String, body: String, label: String, expectTask: Boolean): TaskDetail? {
        val key = Outbox.newId()
        val req = Request.Builder().url("$base$path").header("Idempotency-Key", key)
            .post(body.toRequestBody("application/json".toMediaType())).build()
        try {
            if (!expectTask) { client.newCall(req).execute().close(); return null }
            return callOrRule(req)
        } catch (e: java.io.IOException) {
            Outbox.queuePost(path, body, label, key)
            throw Queued()
        }
    }

    /** The action is saved on the phone and will reach the kiosk when it's back in reach. */
    class Queued : IllegalStateException("Saved. It'll go to the kiosk as soon as it's back in reach.")

    suspend fun linkedIn(): LinkedInInfo = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/linkedin").build()).use { json.decodeFromString(it.body.string()) }
    }

    suspend fun linkedInLabel(id: String, signal: Boolean) =
        postJson("/api/linkedin/label", buildJsonObject { put("id", id); put("signal", signal) })

    suspend fun linkedInRun() = postJson("/api/linkedin/run", buildJsonObject { })

    suspend fun linkedInPodcastPrepare() = postJson("/api/linkedin/podcast", buildJsonObject { })
    suspend fun linkedInPodcastClear() = postJson("/api/linkedin/podcast/clear", buildJsonObject { })

    /** Tag: what you said after tapping Tag (may be empty) and where the episode was; returns SideCar's short confirmation. */
    suspend fun linkedInPodcastInterrupt(text: String, positionSeconds: Double): String = withContext(Dispatchers.IO) {
        val body = buildJsonObject { put("text", text); put("position", positionSeconds) }.toString()
            .toRequestBody("application/json".toMediaType())
        val key = Outbox.newId()
        try {
            call(Request.Builder().url("$base/api/linkedin/podcast/tag").header("Idempotency-Key", key).post(body).build()).use {
                json.parseToJsonElement(it.body.string()).jsonObject["reply"]?.jsonPrimitive?.content ?: "Saved"
            }
        } catch (e: java.io.IOException) {
            Outbox.queuePost("/api/linkedin/podcast/tag", buildJsonObject { put("text", text); put("position", positionSeconds) }.toString(), "LinkedIn tag", key)
            "Tag saved; it'll reach the kiosk when it's back in reach"
        }
    }

    /** Downloads the episode's audio (WAV) through the pinned connection. */
    suspend fun linkedInPodcastAudio(): ByteArray = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/linkedin/podcast/audio").build()).use { it.body.bytes() }
    }

    suspend fun mail(): MailInfo = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/mail").build()).use { json.decodeFromString(it.body.string()) }
    }
    suspend fun mailDigestAudio(id: String): ByteArray = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/mail/digest/$id/audio").build()).use { it.body.bytes() }
    }
    suspend fun mailArchive(id: String, archived: Boolean) = postJson("/api/mail/digest/$id/archive", buildJsonObject { put("archived", archived) })
    suspend fun mailArchiveAll(undo: List<String>? = null) = postJson("/api/mail/archive-all",
        buildJsonObject { if (undo != null) put("undo", kotlinx.serialization.json.JsonArray(undo.map { kotlinx.serialization.json.JsonPrimitive(it) })) })
    suspend fun mailToInbox(id: String) = postJson("/api/mail/item/inbox", buildJsonObject { put("id", id) })
    suspend fun mailRun() = postJson("/api/mail/run", buildJsonObject { })

    suspend fun linkedInPerson(url: String): LiPerson = withContext(Dispatchers.IO) {
        val u = "$base/api/linkedin/person?url=" + java.net.URLEncoder.encode(url, "UTF-8")
        call(Request.Builder().url(u).build()).use { json.decodeFromString(it.body.string()) }
    }

    suspend fun linkedInRefresh(url: String) = postJson("/api/linkedin/person/refresh", buildJsonObject { put("url", url) })

    /** Reminders that are due now (they nag until handled). */
    suspend fun clearActivity() = postJson("/api/activity/clear", buildJsonObject { })

    // ---- X ----
    suspend fun x(): XInfo = withContext(Dispatchers.IO) { call(Request.Builder().url("$base/api/x").build()).use { json.decodeFromString(it.body.string()) } }
    suspend fun xUpdate() = postJson("/api/x/update", buildJsonObject { })
    suspend fun xLabel(id: String, signal: Boolean) = postJson("/api/x/label", buildJsonObject { put("id", id); put("signal", signal) })
    suspend fun xSave(id: String) = postJson("/api/x/save", buildJsonObject { put("id", id) })
    suspend fun xUnfollow(authorId: String) = postJson("/api/x/unfollow", buildJsonObject { put("authorId", authorId) })
    suspend fun xAnalyze(id: String): String = withContext(Dispatchers.IO) {
        val body = buildJsonObject { put("id", id) }.toString().toRequestBody("application/json".toMediaType())
        call(Request.Builder().url("$base/api/x/analyze").post(body).build()).use {
            json.parseToJsonElement(it.body.string()).jsonObject["analysis"]?.jsonPrimitive?.content ?: ""
        }
    }
    suspend fun xPodcastPrepare() = postJson("/api/x/podcast", buildJsonObject { })
    suspend fun xPodcastClear() = postJson("/api/x/podcast/clear", buildJsonObject { })
    suspend fun xPodcastAudio(): ByteArray = withContext(Dispatchers.IO) { call(Request.Builder().url("$base/api/x/podcast/audio").build()).use { it.body.bytes() } }
    suspend fun xPodcastInterrupt(text: String, positionSeconds: Double): String = withContext(Dispatchers.IO) {
        val body = buildJsonObject { put("text", text); put("position", positionSeconds) }.toString().toRequestBody("application/json".toMediaType())
        val key = Outbox.newId()
        try {
            call(Request.Builder().url("$base/api/x/podcast/tag").header("Idempotency-Key", key).post(body).build()).use {
                json.parseToJsonElement(it.body.string()).jsonObject["reply"]?.jsonPrimitive?.content ?: "Saved"
            }
        } catch (e: java.io.IOException) {
            Outbox.queuePost("/api/x/podcast/tag", buildJsonObject { put("text", text); put("position", positionSeconds) }.toString(), "X tag", key)
            "Tag saved; it'll reach the kiosk when it's back in reach"
        }
    }

    suspend fun summary(): HomeSummary = withContext(Dispatchers.IO) { call(Request.Builder().url("$base/api/summary").build()).use { json.decodeFromString(it.body.string()) } }

    // ---- Idea radar ----
    suspend fun ideas(filter: String): IdeasInfo = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/ideas?filter=$filter").build()).use { json.decodeFromString(it.body.string()) }
    }
    suspend fun ideaAction(id: String, action: String) = postJson("/api/ideas/$id/action", buildJsonObject { put("action", action) })

    // ---- Meetings, forecast, LinkedIn suppress ----
    suspend fun meetings(): MeetingsInfo = withContext(Dispatchers.IO) { call(Request.Builder().url("$base/api/meetings").build()).use { json.decodeFromString(it.body.string()) } }
    suspend fun forecast(): ForecastInfo = withContext(Dispatchers.IO) { call(Request.Builder().url("$base/api/weather/forecast").build()).use { json.decodeFromString(it.body.string()) } }
    suspend fun linkedInSuppress(author: String, suppressed: Boolean) =
        postJson("/api/linkedin/suppress", buildJsonObject { put("author", author); put("suppressed", suppressed) })

    // ---- Calendar ----
    suspend fun calendarDay(date: String): CalDay = withContext(Dispatchers.IO) {
        call(Request.Builder().url("$base/api/calendar/day?date=$date").build()).use { json.decodeFromString(it.body.string()) }
    }
    suspend fun calendarAdd(calendar: String, subject: String, start: String, minutes: Int) =
        postJson("/api/calendar/add", buildJsonObject { put("calendar", calendar); put("subject", subject); put("start", start); put("minutes", minutes) })

    // ---- Briefs ----
    suspend fun brief(): BriefInfo = withContext(Dispatchers.IO) { call(Request.Builder().url("$base/api/brief").build()).use { json.decodeFromString(it.body.string()) } }
    suspend fun briefDismiss() = postJson("/api/brief/dismiss", buildJsonObject { })
    /** Weekly plan drop: [ids] is the day's new order (top first); tasks from other days move to [date] (null = no day). */
    suspend fun taskOrder(date: String?, ids: List<String>) = postJson("/api/tasks/order", buildJsonObject {
        put("date", date ?: "")
        put("ids", kotlinx.serialization.json.JsonArray(ids.map { kotlinx.serialization.json.JsonPrimitive(it) }))
    })
    suspend fun taskBriefDismiss() = postJson("/api/tasks/brief/dismiss", buildJsonObject { })
    suspend fun briefMake() = postJson("/api/brief/make", buildJsonObject { })
    suspend fun briefAudio(): ByteArray = withContext(Dispatchers.IO) { call(Request.Builder().url("$base/api/brief/audio").build()).use { it.body.bytes() } }

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
        taskPost("/api/tasks", body, "New to-do: ${title.take(40)}", expectTask = true)!!
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
            }.toString()
            taskPost("/api/tasks/$id/action", body, "To-do: ${action.replace('_', ' ')}", expectTask = action != "take_pass")
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
        /** Completes when the WebSocket is open (true) or failed (false). */
        private val opened = kotlinx.coroutines.CompletableDeferred<Boolean>()

        val events: Flow<ChatEvent> = callbackFlow {
            val ws = client.newWebSocket(Request.Builder().url(base.replace("https://", "wss://") + "/api/chat").build(),
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        val o = runCatching { json.parseToJsonElement(text).jsonObject }.getOrNull() ?: return
                        fun s(k: String) = (o[k] as? JsonElement)?.let { runCatching { it.jsonPrimitive.content }.getOrNull() }
                        trySend(ChatEvent(s("type") ?: "", s("id"), s("text"), s("name"), s("message")))
                    }

                    override fun onOpen(webSocket: WebSocket, response: Response) { opened.complete(true) }

                    override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                        opened.complete(false)
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

        /** Sends once the connection is open (a typed message can otherwise go out before it is). */
        suspend fun sendWhenOpen(text: String): Boolean {
            val ok = kotlinx.coroutines.withTimeoutOrNull(15_000) { opened.await() } ?: false
            return ok && send(text)
        }

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
