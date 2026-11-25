package app.sidecarmonitor

import app.sidecarmonitor.data.Outbox
import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.sidecarmonitor.data.ChatEvent
import app.sidecarmonitor.data.KioskStatus
import app.sidecarmonitor.data.Pairing
import app.sidecarmonitor.data.PairingStore
import app.sidecarmonitor.data.SideCarClient
import app.sidecarmonitor.data.TaskDetail
import app.sidecarmonitor.data.TasksPayload
import app.sidecarmonitor.data.TaskRow
import app.sidecarmonitor.data.WeekPlan
import app.sidecarmonitor.data.VoicePlayer
import app.sidecarmonitor.data.VoiceRecorder
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout

enum class AssistantState { Idle, Connecting, Listening, Thinking, Speaking }

data class ChatLine(val fromUser: Boolean, val text: String, val tools: List<String> = emptyList(), val error: Boolean = false,
                    /** Set while the message waits in the outbox (the kiosk was out of reach). */
                    val outboxId: String? = null)

data class UiState(
    val paired: Boolean = false,
    val connected: Boolean = false,
    val connectionLabel: String = "",
    val status: KioskStatus? = null,
    val lastError: String? = null,
    val assistantOpen: Boolean = false,
    val assistant: AssistantState = AssistantState.Idle,
    val assistantNote: String = "Tap Talk, or type below",
    /** Voice mode in the chat: listen, answer out loud, listen again. Typed chats aren't spoken. */
    val voiceMode: Boolean = false,
    /** Writes and messages waiting to reach the kiosk (see Outbox). */
    val outbox: List<Outbox.Item> = emptyList(),
    val micLevel: Float = 0f,
    val lines: List<ChatLine> = emptyList(),
    val tasks: TasksPayload? = null,
    val openTask: TaskDetail? = null,
    val taskMessage: String? = null,
    val taskBusy: Boolean = false,
    val week: WeekPlan? = null,
    val weekOffset: Int = 0,
)

/**
 * The phone app's brain: pairing, polling the kiosk for status, and the voice assistant loop
 * (record → kiosk transcribes → agent replies over WebSocket → kiosk speaks it in the MAI voice).
 */
class AppViewModel(app: Application) : AndroidViewModel(app) {
    private val store = PairingStore(app)
    private val recorder = VoiceRecorder()
    private val player = VoicePlayer(app)
    private var client: SideCarClient? = null
    private var chat: SideCarClient.ChatConnection? = null
    private var chatJob: Job? = null
    private var turnJob: Job? = null
    private var pollJob: Job? = null

    // Reply tracking for the current turn
    private var replyId: String? = null
    private var replyText = StringBuilder()
    private var replyDone: CompletableDeferred<String>? = null

    // Sentence-by-sentence speech: how much of the reply has been queued for speaking, and the queue.
    private var spokenUpTo = 0
    private var sentences: Channel<String>? = null
    private var acceptedId: CompletableDeferred<String>? = null

    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state.asStateFlow()

    init {
        Outbox.init(app)
        store.load()?.let { usePairing(it) }
        viewModelScope.launch { Outbox.items.collect { items -> _state.update { it.copy(outbox = items) } } }
        // Replies to queued messages: into the chat (the question bubble stops showing "waiting").
        viewModelScope.launch { Outbox.answered.collect(::onAnswer) }
        Outbox.consumeAnswers().forEach(::onAnswer)
        // Network back: send whatever is waiting.
        val cm = app.getSystemService(android.net.ConnectivityManager::class.java)
        cm?.registerDefaultNetworkCallback(object : android.net.ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: android.net.Network) { flushOutbox() }
        })
    }

    /** Sends whatever is waiting in the outbox (on network back, app open, Retry now). */
    fun flushOutbox() {
        viewModelScope.launch {
            val c = client ?: return@launch
            if (Outbox.items.value.isEmpty()) return@launch
            runCatching { c.connect(); Outbox.flush(c) }
        }
    }

    fun approveOutbox(id: String) { Outbox.approve(id); flushOutbox() }
    fun discardOutbox(id: String) {
        Outbox.discard(id)
        _state.update { s -> s.copy(lines = s.lines.map { if (it.outboxId == id) it.copy(text = it.text + "  (not sent)", outboxId = null) else it }) }
    }

    private fun onAnswer(a: Outbox.Answer) = _state.update { s ->
        val lines = s.lines.map { if (it.outboxId == a.id) it.copy(text = if (it.text.startsWith("\uD83C\uDFA4")) a.question else it.text, outboxId = null) else it }.toMutableList()
        if (lines.none { it.fromUser && it.text == a.question }) lines += ChatLine(true, a.question)
        lines += ChatLine(false, a.reply)
        s.copy(lines = lines.takeLast(40))
    }

    // ---- Pairing ----------------------------------------------------------------------------

    /** Returns false if the QR code isn't a SideCar pairing code. */
    fun pair(qr: String): Boolean {
        val p = Pairing.parse(qr) ?: return false
        store.save(p)
        usePairing(p)
        return true
    }

    fun unpair() {
        store.clear()
        pollJob?.cancel(); chatJob?.cancel()
        client = null; chat = null
        _state.value = UiState()
    }

    private fun usePairing(p: Pairing) {
        client = SideCarClient(p)
        _state.update { it.copy(paired = true) }
        startPolling()
    }

    // ---- Status polling ---------------------------------------------------------------------

    fun startPolling() {
        if (pollJob?.isActive == true || client == null) return
        pollJob = viewModelScope.launch {
            while (isActive) {
                refreshOnce()
                delay(if (_state.value.connected) 15_000 else 5_000)
            }
        }
    }

    fun stopPolling() { pollJob?.cancel(); pollJob = null }

    suspend fun refreshOnce() {
        val c = client ?: return
        try {
            if (!_state.value.connected && !c.connect()) {
                _state.update { it.copy(connected = false, lastError = "Can't reach the kiosk") }
                return
            }
            val s = c.status()
            val tasks = runCatching { c.tasks() }.getOrNull()
            val open = _state.value.openTask?.let { o -> runCatching { c.task(o.row.id) }.getOrNull() }
            _state.update {
                it.copy(connected = true, connectionLabel = c.connectionLabel, status = s, lastError = null,
                    tasks = tasks ?: it.tasks,
                    // Don't replace the open task while an action is in flight.
                    openTask = if (it.taskBusy || it.openTask == null) it.openTask else (open ?: it.openTask))
            }
        } catch (e: Exception) {
            _state.update { it.copy(connected = false, lastError = e.message) }
        }
    }

    suspend fun snapshot(index: Int): ByteArray? = runCatching { client?.snapshot(index) }.getOrNull()

    /** Clears the Activity feed on the kiosk (History keeps everything), then refreshes. */
    fun clearActivity() { viewModelScope.launch { client?.clearActivity(); refreshOnce() } }

    /** The paired kiosk connection, for screens that call it directly (X, calendar, briefs). */
    val kiosk: app.sidecarmonitor.data.SideCarClient? get() = client

    /** Briefing Tag for X: save an "X Followup: ..." to-do linked to the post; what you say next becomes its note. */
    suspend fun xInterrupt(positionSeconds: Double, onHeard: (String) -> Unit): String {
        val c = client ?: return "Not connected"
        val wav = app.sidecarmonitor.data.VoiceRecorder().record()
        val said = runCatching { c.listen(wav) }.getOrDefault("") // nothing heard: still tag, just without a note
        if (said.isNotBlank()) onHeard(said)
        return runCatching { c.xPodcastInterrupt(said, positionSeconds) }.getOrElse { "Couldn't save that: ${it.message}" }
    }

    // ---- LinkedIn (read and sorted by the kiosk) ----
    suspend fun linkedIn(): app.sidecarmonitor.data.LinkedInInfo? = runCatching { client?.linkedIn() }.getOrNull()
    suspend fun linkedInLabel(id: String, signal: Boolean): String? = client?.linkedInLabel(id, signal) ?: "Not connected"
    suspend fun linkedInRun(): String? = client?.linkedInRun() ?: "Not connected"
    suspend fun linkedInPodcastPrepare(): String? = client?.linkedInPodcastPrepare() ?: "Not connected"
    suspend fun linkedInPodcastClear(): String? = client?.linkedInPodcastClear() ?: "Not connected"

    /** Briefing Tag for LinkedIn: save a "LinkedIn Followup: ..." to-do linked to the post; what you say next becomes its note. */
    suspend fun podcastInterrupt(positionSeconds: Double, onHeard: (String) -> Unit): String {
        val c = client ?: return "Not connected"
        val wav = app.sidecarmonitor.data.VoiceRecorder().record()
        val said = runCatching { c.listen(wav) }.getOrDefault("") // nothing heard: still tag, just without a note
        if (said.isNotBlank()) onHeard(said)
        return runCatching { c.linkedInPodcastInterrupt(said, positionSeconds) }.getOrElse { "Couldn't save that: ${it.message}" }
    }
    suspend fun linkedInPodcastAudio(): ByteArray? = runCatching { client?.linkedInPodcastAudio() }.getOrNull()
    suspend fun linkedInPerson(url: String): app.sidecarmonitor.data.LiPerson? = runCatching { client?.linkedInPerson(url) }.getOrNull()
    suspend fun linkedInRefresh(url: String): String? = client?.linkedInRefresh(url) ?: "Not connected"

    // ---- House (Home Assistant through the kiosk) ----
    suspend fun home(): app.sidecarmonitor.data.HomeInfo? = runCatching { client?.home() }.getOrNull()
    suspend fun homeSet(d: app.sidecarmonitor.data.HomeDevice, on: Boolean): String? = client?.homeSet(d, on) ?: "Not connected"
    suspend fun homeScript(s: app.sidecarmonitor.data.HomeScript): String? = client?.homeScript(s) ?: "Not connected"
    suspend fun homeResume(): String? = client?.homeResume() ?: "Not connected"

    private val eventPictures = java.util.concurrent.ConcurrentHashMap<String, ByteArray>()

    /** Event pictures never change, so they're cached for the session. */
    suspend fun eventSnapshot(id: String, n: Int): ByteArray? = eventPictures["$id-$n"]
        ?: runCatching { client?.eventSnapshot(id, n) }.getOrNull()?.also { eventPictures["$id-$n"] = it }

    // ---- To-do list -------------------------------------------------------------------------

    fun openTask(id: String) {
        viewModelScope.launch {
            val c = client ?: return@launch
            try { _state.update { it.copy(openTask = c.task(id), taskMessage = null) } }
            catch (e: Exception) { _state.update { it.copy(taskMessage = e.message) } }
        }
    }

    fun closeTask() = _state.update { it.copy(openTask = null, taskMessage = null) }

    // ---- Weekly plan --------------------------------------------------------------------------

    fun loadWeek(offset: Int = _state.value.weekOffset) {
        viewModelScope.launch {
            val c = client ?: return@launch
            val start = java.time.LocalDate.now().plusWeeks(offset.toLong()).toString()
            try { _state.update { it.copy(week = c.week(start), weekOffset = offset, taskMessage = null) } }
            catch (e: Exception) { _state.update { it.copy(taskMessage = e.message) } }
        }
    }

    fun shiftWeek(delta: Int) = loadWeek(_state.value.weekOffset + delta)

    /** Drag-and-drop: a day sets a flexible target date (your explicit reschedule); null clears the day. */
    fun moveTaskToDay(taskId: String, date: String?) {
        viewModelScope.launch {
            val c = client ?: return@launch
            try {
                val extra: Map<String, Any?> = if (date == null) mapOf("clearDate" to true) else mapOf("date" to date, "hardDeadline" to false)
                c.taskActionById(taskId, "edit", extra)
                _state.update { it.copy(taskMessage = null) }
            } catch (e: Exception) {
                _state.update { it.copy(taskMessage = e.message) }
            }
            loadWeek()
            runCatching { c.tasks() }.getOrNull()?.let { t -> _state.update { it.copy(tasks = t) } }
        }
    }

    fun toggleDone(row: TaskRow) {
        viewModelScope.launch {
            val c = client ?: return@launch
            try { c.taskActionById(row.id, if (row.status == "Done") "reopen" else "done") }
            catch (e: Exception) { _state.update { it.copy(taskMessage = e.message) } }
            loadWeek()
        }
    }

    /** Dismiss the to-do list's daily brief (hidden right away; the outbox makes sure the kiosk hears it). */
    fun dismissTaskBrief() {
        _state.update { s -> s.copy(tasks = s.tasks?.copy(brief = null)) }
        viewModelScope.launch { client?.taskBriefDismiss() }
    }

    fun addTask(title: String, pinToday: Boolean) {
        if (title.isBlank()) return
        viewModelScope.launch {
            val c = client ?: return@launch
            try {
                c.addTask(title.trim(), pinToday)
                _state.update { it.copy(tasks = c.tasks(), taskMessage = null) }
            } catch (e: Exception) { _state.update { it.copy(taskMessage = e.message) } }
        }
    }

    /** Runs a task action on the kiosk (as you) and shows the saved result, or why it was refused. */
    fun taskAction(action: String, extra: Map<String, Any?> = emptyMap()) {
        val id = _state.value.openTask?.row?.id ?: return
        viewModelScope.launch {
            val c = client ?: return@launch
            _state.update { it.copy(taskBusy = true, taskMessage = null) }
            try {
                val updated = c.taskAction(id, action, extra)
                val msg = if (action == "take_pass") "The assistant is taking a pass. Check back in a minute." else null
                _state.update { it.copy(openTask = updated ?: it.openTask, tasks = c.tasks(), taskMessage = msg) }
            } catch (e: Exception) {
                // Refused (e.g. changed since you opened it): show why and reload the latest.
                val latest = runCatching { c.task(id) }.getOrNull()
                _state.update { it.copy(taskMessage = e.message, openTask = latest ?: it.openTask) }
            } finally {
                _state.update { it.copy(taskBusy = false) }
            }
        }
    }

    // ---- Voice assistant --------------------------------------------------------------------

    fun openAssistant(startListening: Boolean) {
        _state.update { it.copy(assistantOpen = true) }
        if (startListening) talk()
    }

    /** The chat's mic button: on starts voice mode (or, while listening, means "Done"); off goes back to typing. */
    fun setVoiceMode(on: Boolean) {
        if (on) talk()
        else { stopTurn(); _state.update { it.copy(voiceMode = false) }; setAssistant(AssistantState.Idle, "Ask anything") }
    }

    /** New chat: the agent wraps up the old one (summary and facts) and the screen starts empty. */
    fun newChat() {
        stopTurn()
        if (_state.value.lines.isNotEmpty()) chat?.endConversation()
        _state.update { it.copy(lines = emptyList(), voiceMode = false) }
    }

    fun closeAssistant() {
        stopTurn()
        if (_state.value.lines.isNotEmpty()) chat?.endConversation()
        _state.update { it.copy(assistantOpen = false, assistant = AssistantState.Idle, voiceMode = false) }
    }

    /** Started by voice: after each spoken reply the mic reopens until you go quiet, say "that's all", or tap Stop. */
    private var voiceConversation = false

    private val endPhrases = setOf(
        "thanks", "thank you", "thanks a lot", "thank you very much", "no thanks", "no thank you", "that's all", "that is all",
        "that's it", "that's all thanks", "that's all thank you", "that's it thanks", "never mind", "nevermind", "stop",
        "goodbye", "bye", "done", "i'm done", "all set", "i'm all set", "nothing", "nope", "no", "we're done", "cancel",
    )

    private fun isEndPhrase(said: String): Boolean {
        var s = said.lowercase().filter { it.isLetter() || it == ' ' || it == '\'' || it == '’' }.replace('’', '\'').trim()
        for (prefix in listOf("ok ", "okay ", "great ", "perfect ", "cool ")) if (s.startsWith(prefix)) s = s.removePrefix(prefix).trim()
        return s.replace(" sidecar", "").trim() in endPhrases
    }

    /** Listen, then answer. Tapping Talk while listening means "Done": send what was heard. */
    fun talk() {
        if (_state.value.assistant == AssistantState.Listening) { recorder.stop(); return } // "Done"
        voiceConversation = true
        _state.update { it.copy(voiceMode = true) }
        listenAndRespond(followUp = false)
    }

    private fun listenAndRespond(followUp: Boolean) {
        stopTurn(keepConversation = true)
        turnJob = viewModelScope.launch {
            val c = client ?: return@launch
            try {
                setAssistant(AssistantState.Listening, if (followUp) "Go ahead… (or say “that's all”)" else "Listening…")
                val wav = recorder.record(onLevel = { level -> _state.update { it.copy(micLevel = level) } }, noSpeechMs = if (followUp) 8_000 else 8_000)
                _state.update { it.copy(micLevel = 0f) }
                if (wav.isEmpty()) {
                    voiceConversation = false
                    _state.update { it.copy(voiceMode = false) }
                    setAssistant(AssistantState.Idle, if (followUp) "Tap Talk to continue." else "Didn't catch that. Tap Talk to try again.")
                    return@launch
                }
                setAssistant(AssistantState.Thinking, "Thinking…")
                val heard = try { c.listen(wav) } catch (e: java.io.IOException) {
                    // Out of reach: keep the recording; it's transcribed and answered when the kiosk is back.
                    val item = Outbox.queueAsk("", Outbox.saveAudio(wav))
                    addLine(ChatLine(true, "\uD83C\uDFA4 Voice message", outboxId = item.id))
                    voiceConversation = false
                    _state.update { it.copy(voiceMode = false) }
                    setAssistant(AssistantState.Idle, "Waiting to send: the kiosk is out of reach")
                    return@launch
                }
                if (heard.isBlank()) { voiceConversation = false; setAssistant(AssistantState.Idle, "Didn't catch that. Tap Talk to try again."); return@launch }
                if (followUp && isEndPhrase(heard)) {
                    voiceConversation = false
                    _state.update { it.copy(voiceMode = false) }
                    addLine(ChatLine(true, heard))
                    setAssistant(AssistantState.Idle, "Okay. Tap Talk anytime.")
                    return@launch
                }
                respond(heard)
                // Hands-free: reopen the mic for your answer or follow-up (after a beat, so it doesn't hear itself).
                if (voiceConversation && _state.value.assistantOpen) {
                    kotlinx.coroutines.delay(350)
                    if (voiceConversation && _state.value.assistantOpen) listenAndRespond(followUp = true)
                }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                voiceConversation = false
                _state.update { it.copy(voiceMode = false) }
                showError(e.message ?: "Something went wrong")
            }
        }
    }

    /** Dictation for text fields (notes, answers): record, transcribe on the kiosk, return the words ("" if none). */
    suspend fun dictate(taskId: String? = null, onLevel: (Float) -> Unit = {}): String {
        val c = client ?: return ""
        val wav = recorder.record(onLevel = onLevel)
        if (wav.isEmpty()) return ""
        return try { c.listen(wav) } catch (e: java.io.IOException) {
            // Out of reach: the recording is kept and added to this to-do as a note when the kiosk is back.
            if (taskId != null) {
                Outbox.queueVoiceNote(taskId, Outbox.saveAudio(wav))
                _state.update { it.copy(taskMessage = "Saved your recording. It'll be added as a note when the kiosk is back in reach.") }
            }
            ""
        }
    }

    fun stopDictation() = recorder.stop()

    fun sendText(text: String) {
        if (text.isBlank()) return
        voiceConversation = false
        _state.update { it.copy(voiceMode = false) }
        stopTurn()
        turnJob = viewModelScope.launch {
            try { respond(text.trim()) }
            catch (e: kotlinx.coroutines.CancellationException) { throw e }
            catch (e: Exception) { showError(e.message ?: "Something went wrong") }
        }
    }

    fun stopTurn(keepConversation: Boolean = false) {
        if (!keepConversation) voiceConversation = false
        recorder.stop()
        player.stop()
        turnJob?.cancel()
        turnJob = null
        if (_state.value.assistant != AssistantState.Idle) setAssistant(AssistantState.Idle, "Tap Talk, or type below")
    }

    private suspend fun respond(text: String) {
        val c = client ?: return
        addLine(ChatLine(true, text))
        setAssistant(AssistantState.Thinking, "Thinking…")
        val connection = ensureChat()

        replyText = StringBuilder()
        replyId = null
        acceptedId = CompletableDeferred()
        replyDone = CompletableDeferred()
        // If you're looking at a task, "it" means that task.
        val context = _state.value.openTask?.row?.let { "[Context: the user is looking at task ${it.id} \"${it.title}\"]\n" } ?: ""
        if (!connection.sendWhenOpen(context + text)) {
            // Out of reach: the message waits in the outbox and is answered when the kiosk is back.
            chatJob?.cancel(); chat = null
            val item = Outbox.queueAsk(context + text)
            _state.update { s ->
                val lines = s.lines.toMutableList()
                val i = lines.indexOfLast { it.fromUser && it.text == text }
                if (i >= 0) lines[i] = lines[i].copy(outboxId = item.id)
                s.copy(lines = lines)
            }
            setAssistant(AssistantState.Idle, "Waiting to send: the kiosk is out of reach")
            return
        }

        coroutineScope {
            // Speak while the reply is still streaming: each finished sentence is synthesized by the kiosk
            // (in parallel, ahead of time) and played in order. Much faster than waiting for the whole reply.
            val queue = Channel<String>(Channel.UNLIMITED)
            sentences = queue
            spokenUpTo = 0
            val audio = Channel<Deferred<ByteArray>>(Channel.UNLIMITED)
            val producer = launch {
                for (s in queue) audio.send(async(Dispatchers.IO) { c.speak(s) })
                audio.close()
            }
            val speaker = launch {
                var failed: String? = null
                for (clip in audio) {
                    try {
                        val wav = clip.await()
                        setAssistant(AssistantState.Speaking, "Speaking\u2026")
                        player.play(wav)
                    } catch (e: kotlinx.coroutines.CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        failed = e.message // keep going with the next sentence; the text is on screen
                    }
                }
                if (failed != null) setAssistant(AssistantState.Idle, "Couldn't play part of the reply: $failed")
            }

            withTimeout(180_000) {
                replyId = acceptedId!!.await()
                replyDone!!.await()
            }
            viewModelScope.launch { refreshOnce() }
            queueSentences(final = true)
            queue.close()
            producer.join()
            speaker.join()
            sentences = null
            if (_state.value.assistant != AssistantState.Idle) setAssistant(AssistantState.Idle, "Tap Talk, or type below")
        }
    }

    /** Queues the newly finished sentences of the streaming reply for speech (all of the rest when [final]). */
    private fun queueSentences(final: Boolean) {
        val q = sentences ?: return
        if (!_state.value.voiceMode) return // typed chats aren't spoken
        val text = replyText.toString()
        if (spokenUpTo > text.length) return
        var end = spokenUpTo
        if (final) end = text.length
        else {
            for (i in spokenUpTo until text.length - 1) {
                val ch = text[i]
                if ((ch == '.' || ch == '!' || ch == '?' || ch == '\n') && text[i + 1].isWhitespace() && i + 1 - spokenUpTo >= 8) end = i + 1
            }
        }
        if (end <= spokenUpTo) return
        val piece = text.substring(spokenUpTo, end).trim()
        spokenUpTo = end
        if (piece.any { it.isLetterOrDigit() }) q.trySend(piece)
    }

    private fun ensureChat(): SideCarClient.ChatConnection {
        chat?.let { if (chatJob?.isActive == true) return it }
        val connection = client!!.openChat()
        chat = connection
        chatJob = viewModelScope.launch { connection.events.collect(::onChatEvent) }
        return connection
    }

    private fun onChatEvent(e: ChatEvent) {
        when (e.type) {
            "accepted" -> if (acceptedId?.isActive == true) {
                replyId = e.id // set right away so the first reply chunks aren't dropped
                acceptedId?.complete(e.id ?: "")
            }
            "delta" -> if (e.id == replyId && e.text != null) {
                replyText.append(e.text)
                upsertReply(replyText.toString())
                queueSentences(final = false)
            }
            "message" -> if (e.id == replyId && !e.text.isNullOrBlank()) {
                // A turn can have several messages around tool calls; keep the latest full text.
                // Only re-sync speech if the full text still starts with what was already spoken.
                val spokenPrefix = replyText.substring(0, spokenUpTo.coerceAtMost(replyText.length))
                replyText = StringBuilder(e.text)
                if (!e.text.startsWith(spokenPrefix)) spokenUpTo = e.text.length // don't repeat or garble
                upsertReply(e.text)
            }
            "tool" -> if (e.id == replyId) {
                val label = toolLabel(e.name ?: "")
                setAssistant(AssistantState.Thinking, "$label\u2026")
                _state.update { s ->
                    val lines = s.lines.toMutableList()
                    if (lines.isNotEmpty() && !lines.last().fromUser) lines[lines.size - 1] = lines.last().copy(tools = lines.last().tools + label)
                    else lines += ChatLine(false, "", listOf(label))
                    s.copy(lines = lines)
                }
            }
            "idle" -> if (e.id == replyId) replyDone?.complete(replyText.toString())
            "error" -> {
                val msg = e.message ?: "The assistant had a problem"
                if (replyDone?.isActive == true) replyDone?.completeExceptionally(IllegalStateException(msg))
                else setAssistant(AssistantState.Idle, msg)
                if (e.id == null) { chatJob?.cancel(); chat = null } // connection-level problem: reconnect next time
            }
        }
    }

    /** What a tool call looks like in the chat ("Searching the web"). */
    private fun toolLabel(name: String) = when {
        name == "web_search" -> "Searching the web"
        name.startsWith("calendar") -> "Checking your calendars"
        name.startsWith("task") -> "Looking at your to-dos"
        name.startsWith("reminder") -> "Working on reminders"
        name.startsWith("home") -> "Talking to the house"
        name.contains("weather") -> "Checking the weather"
        name == "flight_search" -> "Searching flights"
        name.contains("camera") || name.contains("event") -> "Checking the cameras"
        else -> "Working"
    }

    private fun upsertReply(text: String) = _state.update { s ->
        val lines = s.lines.toMutableList()
        if (lines.isNotEmpty() && !lines.last().fromUser) lines[lines.size - 1] = lines.last().copy(text = text)
        else lines += ChatLine(false, text)
        s.copy(lines = lines)
    }

    /** A failure shows up in the conversation (not just the status line), so a send never silently disappears. */
    private fun showError(message: String) {
        setAssistant(AssistantState.Idle, message)
        addLine(ChatLine(false, message, error = true))
    }

    private fun addLine(line: ChatLine) = _state.update { it.copy(lines = (it.lines + line).takeLast(40)) }

    private fun setAssistant(a: AssistantState, note: String) =
        _state.update { it.copy(assistant = a, assistantNote = note) }

    override fun onCleared() {
        recorder.stop(); player.stop()
    }
}
