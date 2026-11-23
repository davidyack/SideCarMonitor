package app.sidecarmonitor.watch

import android.content.Context
import com.google.android.gms.wearable.ChannelClient
import com.google.android.gms.wearable.Wearable
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.tasks.await
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.io.DataInputStream
import java.util.UUID

/** What the watch shows: weather, to-do counts and today's tasks (from the phone, which asks the kiosk). */
@Serializable
data class WatchHome(
    val connected: Boolean = false,
    val via: String = "",
    val temp: String = "--",
    val condition: String = "",
    val feelsLike: String = "",
    val wind: String = "",
    val todayCount: Int = 0,
    val needsMeCount: Int = 0,
    val next: String = "",
    val today: List<WatchTask> = emptyList(),
    val activity: String = "",
    val activityId: String = "",
    /** Which picture of the last event to show (0 = none). */
    val activityShot: Int = 0,
    val error: String? = null,
)

@Serializable
data class WatchTask(val id: String, val title: String, val hard: Boolean = false, val overdue: Boolean = false, val needsMe: Boolean = false)

data class TalkReply(val heard: String, val reply: String, val error: String?, val audio: ByteArray)

/**
 * The watch never talks to the kiosk directly: the SideCar phone app is the bridge (it holds the pairing).
 * Small requests go over MessageClient RPC; voice goes over ChannelClient streams.
 * Paths must match the phone's WatchBridgeService.
 */
class PhoneLink(private val context: Context) {
    private val json = Json { ignoreUnknownKeys = true }

    companion object {
        const val HOME = "/sidecar/home"
        const val TASK_DONE = "/sidecar/task/done"
        const val ASK = "/sidecar/ask"
        const val EVENT_PICTURE = "/sidecar/event/picture"
        const val TALK_PREFIX = "/sidecar/talk/"
        const val REPLY_PREFIX = "/sidecar/reply/"
    }

    /** The connected phone (Pixel Watch pairs with exactly one phone). */
    private suspend fun phoneNodeId(): String {
        val nodes = Wearable.getNodeClient(context).connectedNodes.await()
        return (nodes.firstOrNull { it.isNearby } ?: nodes.firstOrNull())?.id
            ?: throw IllegalStateException("Phone not connected")
    }

    private suspend fun request(path: String, body: String = "{}"): JsonObject = withContext(Dispatchers.IO) {
        val bytes = withTimeout(20_000) {
            Wearable.getMessageClient(context).sendRequest(phoneNodeId(), path, body.encodeToByteArray()).await()
        }
        val o = json.parseToJsonElement(bytes.decodeToString()).jsonObject
        o["error"]?.jsonPrimitive?.content?.let { throw IllegalStateException(it) }
        o
    }

    suspend fun home(): WatchHome = json.decodeFromJsonElement(WatchHome.serializer(), request(HOME))

    suspend fun markDone(id: String): WatchHome =
        json.decodeFromJsonElement(WatchHome.serializer(), request(TASK_DONE, """{"id":${Json.encodeToString(id)}}"""))

    /** The small picture the kiosk took when an event happened, or null. */
    suspend fun eventPicture(id: String, n: Int): ByteArray? = withContext(Dispatchers.IO) {
        val bytes = withTimeout(20_000) {
            Wearable.getMessageClient(context).sendRequest(phoneNodeId(), EVENT_PICTURE,
                """{"id":${Json.encodeToString(id)},"n":$n}""".encodeToByteArray()).await()
        }
        bytes.takeIf { it.size > 2 && it[0] == 0xFF.toByte() && it[1] == 0xD8.toByte() } // JPEG, not an error
    }

    suspend fun ask(text: String): String =
        request(ASK, """{"text":${Json.encodeToString(text)}}""")["reply"]?.jsonPrimitive?.content ?: ""

    /**
     * Streams a WAV recording to the phone and waits for the answer streamed back:
     * what you said, SideCar's reply, and the reply audio in the kiosk's MAI voice.
     */
    suspend fun talk(wav: ByteArray): TalkReply = withContext(Dispatchers.IO) {
        val channels = Wearable.getChannelClient(context)
        val id = UUID.randomUUID().toString().take(8)
        val replyChannel = CompletableDeferred<ChannelClient.Channel>()
        val callback = object : ChannelClient.ChannelCallback() {
            override fun onChannelOpened(channel: ChannelClient.Channel) {
                if (channel.path == REPLY_PREFIX + id) replyChannel.complete(channel)
            }
        }
        channels.registerChannelCallback(callback).await()
        try {
            val out = channels.openChannel(phoneNodeId(), TALK_PREFIX + id).await()
            channels.getOutputStream(out).await().use { it.write(wav); it.flush() }

            val back = withTimeout(150_000) { replyChannel.await() }
            DataInputStream(channels.getInputStream(back).await()).use { input ->
                val headerLen = input.readInt()
                val header = ByteArray(headerLen).also { input.readFully(it) }
                val audio = input.readBytes()
                val o = json.parseToJsonElement(header.decodeToString()).jsonObject
                TalkReply(
                    heard = o["heard"]?.jsonPrimitive?.content ?: "",
                    reply = o["reply"]?.jsonPrimitive?.content ?: "",
                    error = o["error"]?.jsonPrimitive?.content,
                    audio = audio,
                )
            }
        } finally {
            channels.unregisterChannelCallback(callback)
        }
    }
}
