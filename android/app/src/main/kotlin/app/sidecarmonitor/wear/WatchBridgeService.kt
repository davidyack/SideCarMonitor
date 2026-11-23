package app.sidecarmonitor.wear

import app.sidecarmonitor.data.PairingStore
import app.sidecarmonitor.data.SideCarClient
import com.google.android.gms.tasks.Task
import com.google.android.gms.wearable.ChannelClient
import com.google.android.gms.wearable.Wearable
import com.google.android.gms.wearable.WearableListenerService
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.tasks.asTask
import kotlinx.coroutines.tasks.await
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import java.io.DataOutputStream

/**
 * The Pixel Watch talks to the kiosk through this service, over the Wear OS data layer.
 * The phone holds the pairing (kiosk address, key, pinned certificate), so the watch never signs in.
 *
 * Requests (MessageClient RPC, JSON in/out):
 *   /sidecar/home        → weather, to-do counts, today's tasks, connection state
 *   /sidecar/task/done   → {"id"} mark done (as you), then the new home
 *   /sidecar/ask         → {"text"} ask SideCar by text, returns {"reply"}
 *   /sidecar/event/picture → {"id","n"} the event picture as a small JPEG (raw bytes, not JSON)
 * Voice (ChannelClient streams):
 *   watch opens /sidecar/talk/<id> and streams a 16 kHz WAV; the phone has the kiosk transcribe it,
 *   asks the agent, has the kiosk speak the reply in the MAI voice, and streams it back on
 *   /sidecar/reply/<id> as [int json length][json {heard, reply, error}][WAV bytes].
 */
class WatchBridgeService : WearableListenerService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val json = Json { ignoreUnknownKeys = true }

    private suspend fun client(): SideCarClient {
        val pairing = PairingStore(this).load() ?: throw IllegalStateException("Pair the SideCar phone app with the kiosk first")
        return SideCarClient(pairing).also { if (!it.connect()) throw IllegalStateException("Can't reach the kiosk") }
    }

    override fun onRequest(nodeId: String, path: String, request: ByteArray): Task<ByteArray>? =
        scope.async {
            runCatching {
                when (path) {
                    WatchPaths.HOME -> home(client())
                    WatchPaths.TASK_DONE -> {
                        val c = client()
                        val id = json.parseToJsonElement(request.decodeToString()).jsonObject["id"]?.jsonPrimitive?.content ?: ""
                        c.taskActionById(id, "done")
                        home(c)
                    }
                    WatchPaths.EVENT_PICTURE -> {
                        val o = json.parseToJsonElement(request.decodeToString()).jsonObject
                        val jpeg = client().eventSnapshot(o["id"]?.jsonPrimitive?.content ?: "", o["n"]?.jsonPrimitive?.content?.toIntOrNull() ?: 1)
                        return@async shrink(jpeg, 320) // the data layer caps messages at ~100 KB
                    }
                    WatchPaths.ASK -> {
                        val text = json.parseToJsonElement(request.decodeToString()).jsonObject["text"]?.jsonPrimitive?.content ?: ""
                        buildJsonObject { put("reply", askOnce(client(), text)) }.toString()
                    }
                    else -> buildJsonObject { put("error", "Unknown request $path") }.toString()
                }
            }.getOrElse { e -> buildJsonObject { put("error", e.message ?: "Something went wrong") }.toString() }
                .encodeToByteArray()
        }.asTask()

    override fun onChannelOpened(channel: ChannelClient.Channel) {
        if (!channel.path.startsWith(WatchPaths.TALK_PREFIX)) return
        val id = channel.path.removePrefix(WatchPaths.TALK_PREFIX)
        scope.launch {
            val channels = Wearable.getChannelClient(this@WatchBridgeService)
            var heard = ""
            var reply = ""
            var error: String? = null
            var audio = ByteArray(0)
            try {
                val wav = channels.getInputStream(channel).await().use { it.readBytes() }
                val c = client()
                heard = c.listen(wav)
                if (heard.isBlank()) error = "Didn't catch that"
                else {
                    reply = askOnce(c, heard)
                    audio = runCatching { c.speak(reply) }.getOrDefault(ByteArray(0))
                }
            } catch (e: Exception) {
                error = e.message ?: "Something went wrong"
            }
            // Stream the answer back to the watch.
            runCatching {
                val back = channels.openChannel(channel.nodeId, WatchPaths.REPLY_PREFIX + id).await()
                channels.getOutputStream(back).await().use { raw ->
                    val out = DataOutputStream(raw)
                    val header = buildJsonObject {
                        put("heard", heard); put("reply", reply); error?.let { put("error", it) }
                    }.toString().encodeToByteArray()
                    out.writeInt(header.size)
                    out.write(header)
                    out.write(audio)
                    out.flush()
                }
            }
        }
    }

    /** One question to the agent over the kiosk's chat WebSocket; returns the final reply text. */
    private suspend fun askOnce(c: SideCarClient, text: String): String {
        val chat = c.openChat()
        val result = CompletableDeferred<String>()
        var myId: String? = null
        val sb = StringBuilder()
        val collector = scope.launch {
            chat.events.collect { e ->
                when (e.type) {
                    "accepted" -> if (myId == null) myId = e.id
                    "delta" -> if (e.id == myId) sb.append(e.text ?: "")
                    // A turn can have several messages around tool calls; the turn ends at idle.
                    "message" -> if (e.id == myId && !e.text.isNullOrBlank()) { sb.clear(); sb.append(e.text) }
                    "idle" -> if (e.id == myId) result.complete(sb.toString())
                    "error" -> if (e.id == null || e.id == myId) result.completeExceptionally(IllegalStateException(e.message ?: "Assistant error"))
                }
            }
        }
        try {
            // The socket opens when collection starts; send as soon as it's ready.
            var sent = false
            repeat(50) { if (!sent) { sent = chat.send(text); if (!sent) delay(100) } }
            if (!sent) throw IllegalStateException("Couldn't reach the assistant")
            return withTimeout(120_000) { result.await() }.trim()
        } finally {
            collector.cancel()
        }
    }

    private suspend fun home(c: SideCarClient): String {
        val status = runCatching { c.status() }.getOrNull()
        val tasks = runCatching { c.tasks() }.getOrNull()
        val w = status?.weather
        fun ws(k: String) = w?.get(k)?.let { runCatching { it.jsonPrimitive.content }.getOrNull() }
        val today = tasks?.views?.today.orEmpty()
        val needs = tasks?.views?.needsMe.orEmpty()
        return buildJsonObject {
            put("connected", true)
            put("via", c.connectionLabel)
            put("temp", ws("temperature") ?: "--")
            put("condition", ws("condition") ?: "")
            put("feelsLike", ws("feelsLike") ?: "")
            put("wind", ws("wind") ?: "")
            put("todayCount", today.size)
            put("needsMeCount", needs.size)
            put("next", (needs.firstOrNull() ?: today.firstOrNull())?.title ?: "")
            put("today", buildJsonArray {
                (needs + today).distinctBy { it.id }.take(20).forEach { t ->
                    add(buildJsonObject {
                        put("id", t.id); put("title", t.title); put("hard", t.hard); put("overdue", t.overdue); put("needsMe", t.needsMe)
                    })
                }
            })
            val last = status?.activity?.firstOrNull()
            put("activity", last?.let { "${it.kind} at ${it.camera}" } ?: "")
            put("activityId", last?.id ?: "")
            put("activityShot", last?.snapshots?.lastOrNull() ?: 0)
        }.toString()
    }

    private fun shrink(jpeg: ByteArray, width: Int): ByteArray {
        val src = android.graphics.BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size) ?: return jpeg
        val scaled = if (src.width <= width) src else android.graphics.Bitmap.createScaledBitmap(src, width, src.height * width / src.width, true)
        return java.io.ByteArrayOutputStream().also { scaled.compress(android.graphics.Bitmap.CompressFormat.JPEG, 75, it) }.toByteArray()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}

/** Data-layer paths shared with the watch app (keep in sync with wear/.../PhoneLink.kt). */
object WatchPaths {
    const val HOME = "/sidecar/home"
    const val TASK_DONE = "/sidecar/task/done"
    const val ASK = "/sidecar/ask"
    const val EVENT_PICTURE = "/sidecar/event/picture"
    const val TALK_PREFIX = "/sidecar/talk/"
    const val REPLY_PREFIX = "/sidecar/reply/"
}
