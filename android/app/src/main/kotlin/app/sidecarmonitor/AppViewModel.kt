package app.sidecarmonitor

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.sidecarmonitor.data.ChatEvent
import app.sidecarmonitor.data.KioskStatus
import app.sidecarmonitor.data.Pairing
import app.sidecarmonitor.data.PairingStore
import app.sidecarmonitor.data.SideCarClient
import app.sidecarmonitor.data.VoicePlayer
import app.sidecarmonitor.data.VoiceRecorder
import kotlinx.coroutines.CompletableDeferred
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

data class ChatLine(val fromUser: Boolean, val text: String)

data class UiState(
    val paired: Boolean = false,
    val connected: Boolean = false,
    val connectionLabel: String = "",
    val status: KioskStatus? = null,
    val lastError: String? = null,
    val assistantOpen: Boolean = false,
    val assistant: AssistantState = AssistantState.Idle,
    val assistantNote: String = "Tap Talk, or type below",
    val micLevel: Float = 0f,
    val lines: List<ChatLine> = emptyList(),
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
    private var acceptedId: CompletableDeferred<String>? = null

    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state.asStateFlow()

    init {
        store.load()?.let { usePairing(it) }
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
            _state.update { it.copy(connected = true, connectionLabel = c.connectionLabel, status = s, lastError = null) }
        } catch (e: Exception) {
            _state.update { it.copy(connected = false, lastError = e.message) }
        }
    }

    suspend fun snapshot(index: Int): ByteArray? = runCatching { client?.snapshot(index) }.getOrNull()

    // ---- Voice assistant --------------------------------------------------------------------

    fun openAssistant(startListening: Boolean) {
        _state.update { it.copy(assistantOpen = true) }
        if (startListening) talk()
    }

    fun closeAssistant() {
        stopTurn()
        if (_state.value.lines.isNotEmpty()) chat?.endConversation()
        _state.update { it.copy(assistantOpen = false, assistant = AssistantState.Idle) }
    }

    /** Listen, then answer. */
    fun talk() {
        if (_state.value.assistant == AssistantState.Listening) { recorder.stop(); return } // "Done"
        stopTurn()
        turnJob = viewModelScope.launch {
            val c = client ?: return@launch
            try {
                setAssistant(AssistantState.Listening, "Listening…")
                val wav = recorder.record { level -> _state.update { it.copy(micLevel = level) } }
                _state.update { it.copy(micLevel = 0f) }
                if (wav.isEmpty()) { setAssistant(AssistantState.Idle, "Didn't catch that. Tap Talk to try again."); return@launch }
                setAssistant(AssistantState.Thinking, "Thinking…")
                val heard = c.listen(wav)
                if (heard.isBlank()) { setAssistant(AssistantState.Idle, "Didn't catch that. Tap Talk to try again."); return@launch }
                respond(heard)
            } catch (e: Exception) {
                setAssistant(AssistantState.Idle, e.message ?: "Something went wrong")
            }
        }
    }

    fun sendText(text: String) {
        if (text.isBlank()) return
        stopTurn()
        turnJob = viewModelScope.launch {
            try { respond(text.trim()) } catch (e: Exception) { setAssistant(AssistantState.Idle, e.message ?: "Something went wrong") }
        }
    }

    fun stopTurn() {
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
        if (!connection.send(text)) throw IllegalStateException("Not connected to the kiosk")

        val reply = withTimeout(180_000) {
            replyId = acceptedId!!.await()
            replyDone!!.await()
        }
        setAssistant(AssistantState.Speaking, "Speaking…")
        try {
            player.play(c.speak(reply))
            setAssistant(AssistantState.Idle, "Tap Talk, or type below")
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            // Show it rather than failing silently: the text reply is still on screen.
            setAssistant(AssistantState.Idle, "Couldn't play the reply: ${e.message}")
        }
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
            }
            "message" -> if (e.id == replyId && e.text != null) {
                replyText = StringBuilder(e.text)
                upsertReply(e.text)
                replyDone?.complete(e.text)
            }
            "tool" -> if (e.id == replyId) {
                setAssistant(AssistantState.Thinking, if (e.name == "get_local_weather") "Checking the weather…" else "Working…")
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

    private fun upsertReply(text: String) = _state.update { s ->
        val lines = s.lines.toMutableList()
        if (lines.isNotEmpty() && !lines.last().fromUser) lines[lines.size - 1] = ChatLine(false, text)
        else lines += ChatLine(false, text)
        s.copy(lines = lines)
    }

    private fun addLine(line: ChatLine) = _state.update { it.copy(lines = (it.lines + line).takeLast(40)) }

    private fun setAssistant(a: AssistantState, note: String) =
        _state.update { it.copy(assistant = a, assistantNote = note) }

    override fun onCleared() {
        recorder.stop(); player.stop()
    }
}
