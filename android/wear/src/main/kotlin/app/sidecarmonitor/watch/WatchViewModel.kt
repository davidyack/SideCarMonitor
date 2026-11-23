package app.sidecarmonitor.watch

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.wear.tiles.TileService
import androidx.compose.ui.graphics.asImageBitmap
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

enum class TalkState { Idle, Listening, Thinking, Speaking }

data class WatchUi(
    val home: WatchHome? = null,
    val loading: Boolean = true,
    val error: String? = null,
    val talk: TalkState = TalkState.Idle,
    val talkNote: String = "Tap to talk",
    val heard: String = "",
    val reply: String = "",
    val level: Float = 0f,
    /** Picture of the last camera event. */
    val activityPicture: androidx.compose.ui.graphics.ImageBitmap? = null,
)

class WatchViewModel(app: Application) : AndroidViewModel(app) {
    private val link = PhoneLink(app)
    private val recorder = WatchRecorder()
    private val player = WatchPlayer(app)
    private var talkJob: Job? = null

    private val _ui = MutableStateFlow(WatchUi())
    val ui: StateFlow<WatchUi> = _ui.asStateFlow()

    init { refresh() }

    fun refresh() {
        viewModelScope.launch {
            _ui.update { it.copy(loading = true) }
            try {
                val home = link.home()
                _ui.update { it.copy(home = home, loading = false, error = null) }
                loadActivityPicture(home)
            } catch (e: Exception) {
                _ui.update { it.copy(loading = false, error = e.message ?: "Can't reach the phone") }
            }
        }
    }

    private var pictureFor = ""

    private suspend fun loadActivityPicture(home: WatchHome) {
        val key = "${home.activityId}-${home.activityShot}"
        if (home.activityShot == 0) { pictureFor = ""; _ui.update { it.copy(activityPicture = null) }; return }
        if (key == pictureFor) return
        val bytes = runCatching { link.eventPicture(home.activityId, home.activityShot) }.getOrNull() ?: return
        val image = android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap() ?: return
        pictureFor = key
        _ui.update { it.copy(activityPicture = image) }
    }

    fun markDone(id: String) {
        viewModelScope.launch {
            try {
                val home = link.markDone(id)
                _ui.update { it.copy(home = home, error = null) }
                TileService.getUpdater(getApplication()).requestUpdate(SideCarTileService::class.java)
            } catch (e: Exception) {
                _ui.update { it.copy(error = e.message) }
            }
        }
    }

    /** Tap: listen → phone/kiosk transcribe → SideCar answers → MAI voice plays here. Tap again while listening = done. */
    fun talk() {
        if (_ui.value.talk == TalkState.Listening) { recorder.stop(); return }
        stopTalk()
        talkJob = viewModelScope.launch {
            try {
                set(TalkState.Listening, "Listening…")
                _ui.update { it.copy(heard = "", reply = "") }
                val wav = recorder.record { l -> _ui.update { it.copy(level = l) } }
                _ui.update { it.copy(level = 0f) }
                if (wav.isEmpty()) { set(TalkState.Idle, "Didn't catch that"); return@launch }
                set(TalkState.Thinking, "Thinking…")
                val r = link.talk(wav)
                _ui.update { it.copy(heard = r.heard, reply = r.reply) }
                if (r.error != null) { set(TalkState.Idle, r.error); return@launch }
                set(TalkState.Speaking, "Speaking…")
                player.play(r.audio)
                set(TalkState.Idle, "Tap to talk")
                refresh() // the answer may have changed tasks
            } catch (e: Exception) {
                set(TalkState.Idle, e.message ?: "Something went wrong")
            }
        }
    }

    fun stopTalk() {
        recorder.stop()
        player.stop()
        talkJob?.cancel()
        if (_ui.value.talk != TalkState.Idle) set(TalkState.Idle, "Tap to talk")
    }

    private fun set(s: TalkState, note: String) = _ui.update { it.copy(talk = s, talkNote = note) }

    override fun onCleared() { recorder.stop(); player.stop() }
}
