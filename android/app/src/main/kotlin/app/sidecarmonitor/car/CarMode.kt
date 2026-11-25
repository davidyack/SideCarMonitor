package app.sidecarmonitor.car

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.speech.tts.TextToSpeech
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.sidecarmonitor.data.Outbox
import app.sidecarmonitor.data.PlaybackMemory
import app.sidecarmonitor.data.SideCarClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.util.Locale

/**
 * Car mode: while its screen is open the phone listens with Android's on-device speech recognition (free, offline,
 * nothing leaves the phone until you ask SideCar something) and acts on short commands:
 *   "brief me" (morning/evening brief, then LinkedIn, then X), "play LinkedIn", "play X", "pause", "resume",
 *   "skip" / "back" (next / previous topic), "next briefing", "tag that" (then your note), "what's playing",
 *   and "SideCar, <question>" (answered out loud in the SideCar voice).
 * Long sentences that don't start with "SideCar" are ignored, so the briefing coming out of the speakers can't
 * trigger commands.
 */
class CarMode(private val context: Context, private val client: () -> SideCarClient?) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var recognizer: SpeechRecognizer? = null
    private var tts: TextToSpeech? = null
    private var listening = false
    private var speaking = false
    private var awaitingTagNote = false
    private var awaitingQuestion = false
    private var queueJob: Job? = null
    private val segments = HashMap<String, List<Double>>() // item key -> paragraph starts (seconds)

    var status by mutableStateOf("Starting…")
    var heard by mutableStateOf("")
    var micOn by mutableStateOf(false)

    fun start() {
        CarAudio.init(context)
        tts = TextToSpeech(context) { tts?.language = Locale.US }
        listening = true
        listen()
        // Keep the place in each briefing (resumes after a stop, a restart, or in the app).
        scope.launch { while (true) { CarAudio.currentKey()?.let { PlaybackMemory.save(context, it, CarAudio.player.currentPosition.toInt()) }; delay(3000) } }
        status = "Listening. Say “brief me”."
    }

    fun stop() {
        listening = false
        recognizer?.destroy(); recognizer = null
        tts?.shutdown(); tts = null
        scope.cancel()
    }

    // ---- Listening ----------------------------------------------------------------------------------------

    private fun listen() {
        if (!listening || speaking) return
        val r = recognizer ?: (if (SpeechRecognizer.isOnDeviceRecognitionAvailable(context)) SpeechRecognizer.createOnDeviceSpeechRecognizer(context)
                               else SpeechRecognizer.createSpeechRecognizer(context)).also { recognizer = it }
        r.setRecognitionListener(object : RecognitionListener {
            override fun onReadyForSpeech(params: Bundle?) { micOn = true }
            override fun onResults(results: Bundle?) {
                micOn = false
                val text = results?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull().orEmpty()
                if (text.isNotBlank()) handle(text)
                restart()
            }
            override fun onError(error: Int) { micOn = false; restart() }
            override fun onPartialResults(partialResults: Bundle?) {
                partialResults?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()?.let { if (it.isNotBlank()) heard = it }
            }
            override fun onBeginningOfSpeech() {}
            override fun onRmsChanged(rmsdB: Float) {}
            override fun onBufferReceived(buffer: ByteArray?) {}
            override fun onEndOfSpeech() {}
            override fun onEvent(eventType: Int, params: Bundle?) {}
        })
        r.startListening(Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
            putExtra(RecognizerIntent.EXTRA_PREFER_OFFLINE, true)
        })
    }

    private fun restart() { scope.launch { delay(250); listen() } }

    // ---- Commands -----------------------------------------------------------------------------------------

    /** Also used by the on-screen buttons. */
    fun handle(raw: String) {
        heard = raw
        var s = raw.lowercase(Locale.US).replace(Regex("[^a-z0-9' ]"), " ").replace(Regex("\\s+"), " ").trim()
        if (awaitingTagNote) { awaitingTagNote = false; tag(raw); return }
        if (awaitingQuestion) { awaitingQuestion = false; ask(raw); return }
        var addressed = false
        for (wake in listOf("hey sidecar ", "hey side car ", "sidecar ", "side car ", "ok sidecar ")) if (s.startsWith(wake)) { s = s.removePrefix(wake).trim(); addressed = true }
        if (!addressed && s.split(' ').size > 5) return // probably the briefing itself, coming out of the speakers
        when {
            s in setOf("brief me", "play briefing", "play briefings", "play my briefing", "briefing", "play the briefing", "morning brief", "play brief") -> briefMe(listOf("brief", "linkedin", "x"))
            s.contains("linkedin") || s.contains("linked in") -> briefMe(listOf("linkedin"))
            s in setOf("play x", "x briefing", "play twitter", "twitter", "play the x briefing") -> briefMe(listOf("x"))
            s in setOf("pause", "stop", "hold on", "wait") -> { CarAudio.player.pause(); status = "Paused" }
            s in setOf("resume", "play", "continue", "go on", "keep going") -> { CarAudio.player.play(); status = "Playing ${CarAudio.currentTitle() ?: ""}" }
            s in setOf("skip", "next", "skip it", "next topic", "skip that") -> jumpTopic(+1)
            s in setOf("back", "go back", "previous", "repeat that", "say that again") -> jumpTopic(-1)
            s in setOf("next briefing", "skip briefing", "skip this briefing") -> { CarAudio.player.seekToNextMediaItem(); status = "Next: ${CarAudio.currentTitle() ?: "done"}" }
            s.startsWith("tag") -> { awaitingTagNote = true; CarAudio.player.pause(); say("Tagged. What's the note?") }
            s in setOf("what's playing", "what is playing", "what's this") -> say(CarAudio.currentTitle() ?: "Nothing is playing")
            addressed && s.isBlank() -> { awaitingQuestion = true; say("Yes?") }
            addressed -> ask(raw.substringAfter(' ', raw))
        }
    }

    /** "skip" / "back": the next or previous paragraph (topic) of the briefing, or the next item. */
    private fun jumpTopic(dir: Int) {
        val p = CarAudio.player
        val starts = segments[CarAudio.currentKey()].orEmpty()
        val now = p.currentPosition / 1000.0
        val target = if (dir > 0) starts.firstOrNull { it > now + 1 } else starts.lastOrNull { it < now - 3 }
        when {
            target != null -> p.seekTo((target * 1000).toLong())
            dir > 0 -> p.seekToNextMediaItem()
            else -> p.seekTo(0)
        }
        p.play()
    }

    // ---- Briefings ----------------------------------------------------------------------------------------

    /** Plays the requested briefings in order; each one is fetched (or prepared on the kiosk) while the previous plays. */
    private fun briefMe(which: List<String>) {
        queueJob?.cancel()
        queueJob = scope.launch {
            val c = client() ?: run { say("I can't reach the kiosk."); return@launch }
            say(if (which.size > 1) "Here's your briefing." else "Getting it.")
            var first = true
            for (kind in which) {
                val item = runCatching { fetch(c, kind) }.getOrNull()
                if (item == null) { if (which.size == 1) say("Nothing new there.") ; continue }
                if (first) { CarAudio.playNew(item, PlaybackMemory.position(context, item.key).toLong()); first = false }
                else CarAudio.append(item)
                status = "Playing ${CarAudio.currentTitle()}"
            }
            if (first) status = "Nothing to brief right now"
        }
    }

    private suspend fun fetch(c: SideCarClient, kind: String): CarAudio.Item? = withContext(Dispatchers.IO) {
        val dir = File(context.cacheDir, "car").apply { mkdirs() }
        when (kind) {
            "brief" -> {
                val b = c.brief(); if (!b.available) return@withContext null
                val f = File(dir, "brief.wav").apply { writeBytes(c.briefAudio()) }
                CarAudio.Item("brief:${b.at}", b.title.ifBlank { "Daily brief" }, f)
            }
            "linkedin" -> {
                var p = c.linkedIn().podcast
                if (p.status != "ready" || p.cleared || p.hasNews) {
                    if (p.candidates == 0 && (p.status != "ready" || p.cleared)) return@withContext null
                    if (p.hasNews || p.status != "ready" || p.cleared) { c.linkedInPodcastPrepare(); status = "Preparing your LinkedIn briefing…"; p = waitReady { c.linkedIn().podcast } ?: return@withContext null }
                }
                segments["li:${p.at}"] = p.segments.map { it.start }
                CarAudio.Item("li:${p.at}", "LinkedIn briefing", File(dir, "linkedin.wav").apply { writeBytes(c.linkedInPodcastAudio()) })
            }
            "x" -> {
                var p = c.x().podcast
                if (p.status != "ready" || p.cleared || p.hasNews) {
                    if (p.candidates == 0 && (p.status != "ready" || p.cleared)) return@withContext null
                    c.xPodcastPrepare(); status = "Preparing your X briefing…"; p = waitReady { c.x().podcast } ?: return@withContext null
                }
                segments["x:${p.at}"] = p.segments.map { it.start }
                CarAudio.Item("x:${p.at}", "X briefing", File(dir, "x.wav").apply { writeBytes(c.xPodcastAudio()) })
            }
            else -> null
        }
    }

    private suspend fun waitReady(get: suspend () -> app.sidecarmonitor.data.LiPodcast): app.sidecarmonitor.data.LiPodcast? {
        delay(3000)
        repeat(60) {
            val p = runCatching { get() }.getOrNull()
            if (p != null && p.status == "ready" && !p.cleared) return p
            if (p != null && p.status.startsWith("failed")) return null
            delay(5000)
        }
        return null
    }

    // ---- Tag and questions --------------------------------------------------------------------------------

    private fun tag(note: String) {
        val key = CarAudio.currentKey().orEmpty()
        val pos = CarAudio.player.currentPosition / 1000.0
        scope.launch {
            val c = client() ?: return@launch
            val reply = withContext(Dispatchers.IO) {
                runCatching {
                    when {
                        key.startsWith("li:") -> c.linkedInPodcastInterrupt(note, pos)
                        key.startsWith("x:") -> c.xPodcastInterrupt(note, pos)
                        else -> "Tags work during the LinkedIn and X briefings."
                    }
                }.getOrElse { "Saved; it'll reach the kiosk when it's back." }
            }
            say(reply.take(120)) { CarAudio.player.play() }
        }
    }

    /** "SideCar, …": answered in the SideCar voice (queued like any chat message if the kiosk is out of reach). */
    private fun ask(question: String) {
        val wasPlaying = CarAudio.player.isPlaying
        CarAudio.player.pause()
        status = "Asking SideCar…"
        scope.launch {
            val c = client() ?: run { say("I can't reach the kiosk."); return@launch }
            val reply = withContext(Dispatchers.IO) {
                runCatching { c.ask(question, Outbox.newId()) }.getOrElse { Outbox.queueAsk(question); "I'll answer that when the kiosk is back in reach." }
            }
            val wav = withContext(Dispatchers.IO) { runCatching { c.speak(reply) }.getOrNull() }
            status = reply.take(140)
            if (wav == null) { say(reply) { if (wasPlaying) CarAudio.player.play() }; return@launch }
            speaking = true; recognizer?.cancel()
            val f = File(context.cacheDir, "car-reply.wav").apply { writeBytes(wav) }
            android.media.MediaPlayer().apply {
                setDataSource(f.absolutePath); prepare()
                setOnCompletionListener { release(); speaking = false; if (wasPlaying) CarAudio.player.play(); listen() }
                start()
            }
        }
    }

    /** Short confirmations in the phone's own voice (instant, offline); listening pauses so it doesn't hear itself. */
    private fun say(text: String, after: (() -> Unit)? = null) {
        val t = tts ?: return
        speaking = true; recognizer?.cancel()
        val id = text.hashCode().toString()
        t.setOnUtteranceProgressListener(object : android.speech.tts.UtteranceProgressListener() {
            override fun onStart(utteranceId: String?) {}
            override fun onDone(utteranceId: String?) { scope.launch { speaking = false; after?.invoke(); listen() } }
            @Deprecated("Deprecated in Java") override fun onError(utteranceId: String?) { scope.launch { speaking = false; listen() } }
        })
        t.speak(text, TextToSpeech.QUEUE_FLUSH, null, id)
    }

    fun askByButton() { awaitingQuestion = true; say("Go ahead.") }
}
