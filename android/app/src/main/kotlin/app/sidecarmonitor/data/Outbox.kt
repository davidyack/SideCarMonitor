package app.sidecarmonitor.data

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import app.sidecarmonitor.MainActivity
import app.sidecarmonitor.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import java.io.File
import java.io.IOException
import java.util.UUID
import java.util.concurrent.TimeUnit

/**
 * Nothing you send gets lost. Every write to the kiosk is saved here first and sent with an Idempotency-Key, so a
 * retry never runs twice on the kiosk. If the kiosk can't be reached it waits and is retried when the network comes
 * back, when the app opens, and in the background (WorkManager, network required). The kiosk refusing something
 * (4xx) is not retried: the item is marked failed with the reason.
 *
 * Kinds: "post" (any action), "ask" (a chat message; answered by /api/ask), "voice-ask" (a recording to transcribe,
 * then ask), "voice-note" (a recording to transcribe and add as a note on a to-do). A chat message older than an
 * hour isn't sent on its own: it waits for you to say Send or Discard ("confirm").
 */
object Outbox {
    @Serializable
    data class Item(
        val id: String, val kind: String, val label: String, val createdAt: Long,
        val path: String = "", val body: String = "", val text: String = "", val audioFile: String? = null, val taskId: String? = null,
        val status: String = "waiting", // waiting | confirm | approved | failed
        val error: String? = null, val attempts: Int = 0,
    )

    /** A reply to a queued chat message (shown in the chat, or as a notification when the app isn't open). */
    @Serializable
    data class Answer(val id: String, val question: String, val reply: String, val at: Long)

    sealed interface Result {
        data class Sent(val body: String) : Result
        data class Offline(val reason: String) : Result
        data class Refused(val reason: String) : Result
    }

    private const val STALE_CHAT_MS = 60 * 60 * 1000L
    private val json = Json { ignoreUnknownKeys = true }
    private val mutex = Mutex()
    private var dir: File? = null
    private var appContext: Context? = null
    private val _items = MutableStateFlow<List<Item>>(emptyList())
    val items: StateFlow<List<Item>> = _items
    private val _answers = MutableStateFlow<List<Answer>>(emptyList())
    val answers: StateFlow<List<Answer>> = _answers
    val answered = MutableSharedFlow<Answer>(extraBufferCapacity = 8)

    /** Call from anywhere before use (app start, receivers, workers); safe to call again. */
    fun init(context: Context) {
        if (dir != null) return
        appContext = context.applicationContext
        dir = File(context.filesDir, "outbox").apply { mkdirs() }
        _items.value = runCatching { json.decodeFromString(ListSerializer(Item.serializer()), File(dir, "items.json").readText()) }.getOrDefault(emptyList())
        _answers.value = runCatching { json.decodeFromString(ListSerializer(Answer.serializer()), File(dir, "answers.json").readText()) }.getOrDefault(emptyList())
    }

    private fun persist() {
        val d = dir ?: return
        File(d, "items.json").writeText(json.encodeToString(ListSerializer(Item.serializer()), _items.value))
        File(d, "answers.json").writeText(json.encodeToString(ListSerializer(Answer.serializer()), _answers.value))
    }

    private fun upsert(item: Item) { _items.value = _items.value.filter { it.id != item.id } + item; persist() }
    private fun remove(id: String) {
        _items.value.firstOrNull { it.id == id }?.audioFile?.let { File(it).delete() }
        _items.value = _items.value.filter { it.id != id }; persist()
    }

    fun newId(): String = UUID.randomUUID().toString()

    /** Saves a recording so it survives until it's sent. */
    fun saveAudio(wav: ByteArray): String = File(dir, "${newId()}.wav").apply { writeBytes(wav) }.absolutePath

    /**
     * Sends a write now if it can, else keeps it. Returns null when it was sent or is waiting (it will go), or the
     * kiosk's reason when it refused.
     */
    suspend fun post(client: SideCarClient, path: String, body: String, label: String): String? {
        val item = Item(newId(), "post", label, System.currentTimeMillis(), path = path, body = body)
        upsert(item) // saved before the first try: even a crash mid-send doesn't lose it
        return when (val r = client.postRaw(path, body, item.id)) {
            is Result.Sent -> { remove(item.id); null }
            is Result.Refused -> { remove(item.id); r.reason }
            is Result.Offline -> { scheduleRetry(); null }
        }
    }

    /** Keeps a write whose first try (made with this same id as its key) couldn't reach the kiosk. */
    fun queuePost(path: String, body: String, label: String, id: String) {
        upsert(Item(id, "post", label, System.currentTimeMillis(), path = path, body = body)); scheduleRetry()
    }

    /** Queues a chat message (or a voice recording) that couldn't go live. */
    fun queueAsk(text: String, audioFile: String? = null): Item {
        val item = Item(newId(), if (audioFile != null) "voice-ask" else "ask",
            if (audioFile != null) "Voice message" else "Message: ${text.take(40)}", System.currentTimeMillis(), text = text, audioFile = audioFile)
        upsert(item); scheduleRetry(); return item
    }

    fun queueVoiceNote(taskId: String, audioFile: String): Item {
        val item = Item(newId(), "voice-note", "Voice note on a to-do", System.currentTimeMillis(), audioFile = audioFile, taskId = taskId)
        upsert(item); scheduleRetry(); return item
    }

    /** Send / Discard for a chat message that waited over an hour, or a failed item. */
    fun approve(id: String) { _items.value.firstOrNull { it.id == id }?.let { upsert(it.copy(status = "approved")); scheduleRetry() } }
    fun discard(id: String) = remove(id)
    fun consumeAnswers(): List<Answer> = _answers.value.also { _answers.value = emptyList(); persist() }

    /** Tries everything waiting, oldest first. Returns true when nothing is left waiting to retry. */
    suspend fun flush(client: SideCarClient): Boolean = mutex.withLock {
        withContext(Dispatchers.IO) {
            for (item in _items.value.sortedBy { it.createdAt }) {
                if (item.status == "failed") continue
                val chat = item.kind == "ask" || item.kind == "voice-ask"
                if (chat && item.status != "approved" && System.currentTimeMillis() - item.createdAt > STALE_CHAT_MS) {
                    if (item.status != "confirm") { upsert(item.copy(status = "confirm")); notifyConfirm(item) }
                    continue
                }
                if (item.status == "confirm") continue
                val outcome: Result = try {
                    when (item.kind) {
                        "post" -> client.postRaw(item.path, item.body, item.id)
                        "ask", "voice-ask" -> {
                            val text = if (item.kind == "voice-ask") client.listen(File(item.audioFile!!).readBytes()) else item.text
                            if (text.isBlank()) Result.Refused("Couldn't make out the recording")
                            else {
                                if (item.kind == "voice-ask") upsert(item.copy(kind = "ask", text = text))
                                val reply = client.ask(text, item.id)
                                val answer = Answer(item.id, text, reply, System.currentTimeMillis())
                                if (!answered.tryEmit(answer) || answered.subscriptionCount.value == 0) {
                                    _answers.value = _answers.value + answer; notifyAnswer(answer)
                                }
                                Result.Sent(reply)
                            }
                        }
                        "voice-note" -> {
                            val text = client.listen(File(item.audioFile!!).readBytes())
                            if (text.isBlank()) Result.Refused("Couldn't make out the recording")
                            else client.postRaw("/api/tasks/${item.taskId}/action", """{"action":"note","text":${Json.encodeToString(kotlinx.serialization.serializer<String>(), text)}}""", item.id)
                        }
                        else -> Result.Refused("Unknown item")
                    }
                } catch (e: IOException) { Result.Offline(e.message ?: "offline") }
                catch (e: Exception) { Result.Offline(e.message ?: "error") }
                when (outcome) {
                    is Result.Sent -> remove(item.id)
                    is Result.Refused -> upsert(item.copy(status = "failed", error = outcome.reason))
                    is Result.Offline -> { upsert(item.copy(attempts = item.attempts + 1)); return@withContext false }
                }
            }
            true
        }
    }

    /** Background retry when there's a network (with backoff), even if the app is closed. */
    fun scheduleRetry() {
        val ctx = appContext ?: return
        WorkManager.getInstance(ctx).enqueueUniqueWork("outbox", ExistingWorkPolicy.KEEP,
            OneTimeWorkRequestBuilder<OutboxWorker>()
                .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
                .build())
    }

    private fun channel(ctx: Context) {
        val nm = ctx.getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel("chat") == null)
            nm.createNotificationChannel(NotificationChannel("chat", "SideCar replies", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Replies to messages that were sent when the kiosk came back in reach."
            })
    }

    private fun notify(ctx: Context, id: String, title: String, text: String) {
        if (ContextCompat.checkSelfPermission(ctx, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        channel(ctx)
        val open = PendingIntent.getActivity(ctx, 1, Intent(ctx, MainActivity::class.java).putExtra("openChat", true),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        NotificationManagerCompat.from(ctx).notify(60_000 + (id.hashCode() and 0x7fff), NotificationCompat.Builder(ctx, "chat")
            .setSmallIcon(R.mipmap.ic_launcher).setContentTitle(title).setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text)).setAutoCancel(true).setContentIntent(open).build())
    }

    private fun notifyAnswer(a: Answer) { appContext?.let { notify(it, a.id, "SideCar replied", a.reply) } }
    private fun notifyConfirm(i: Item) {
        appContext?.let { notify(it, i.id, "A message is still waiting", "“${i.text.ifBlank { "Voice message" }.take(80)}” was written over an hour ago. Open the chat to send or discard it.") }
    }
}

class OutboxWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): androidx.work.ListenableWorker.Result {
        Outbox.init(applicationContext)
        if (Outbox.items.value.none { it.status == "waiting" || it.status == "approved" }) return androidx.work.ListenableWorker.Result.success()
        val pairing = PairingStore(applicationContext).load() ?: return androidx.work.ListenableWorker.Result.success()
        val client = SideCarClient(pairing)
        if (!client.connect()) return androidx.work.ListenableWorker.Result.retry()
        return if (Outbox.flush(client)) androidx.work.ListenableWorker.Result.success() else androidx.work.ListenableWorker.Result.retry()
    }
}
