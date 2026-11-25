package app.sidecarmonitor.car

import android.content.ComponentName
import android.content.Context
import android.net.Uri
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.MediaController
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import androidx.media3.session.SessionToken
import java.io.File

/**
 * Briefing audio for Car mode: one ExoPlayer, published as a media session so it keeps playing with the screen off and
 * answers lock-screen, headphone and steering-wheel buttons. Takes audio focus, so other audio (music) pauses for it.
 */
object CarAudio {
    data class Item(val key: String, val title: String, val file: File)

    private var _player: ExoPlayer? = null
    val player: ExoPlayer get() = _player!!
    private var controller: MediaController? = null

    fun init(context: Context) {
        if (_player != null) return
        _player = ExoPlayer.Builder(context.applicationContext)
            .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_SPEECH).build(), true)
            .setHandleAudioBecomingNoisy(true) // pause if the headphones or car disconnect
            .build()
        // Bind the session service so playback becomes a proper background media session with a notification.
        val token = SessionToken(context, ComponentName(context, CarAudioService::class.java))
        MediaController.Builder(context, token).buildAsync().addListener({ }, { it.run() })
    }

    private fun mediaItem(i: Item) = MediaItem.Builder().setUri(Uri.fromFile(i.file)).setMediaId(i.key)
        .setMediaMetadata(MediaMetadata.Builder().setTitle(i.title).setArtist("SideCar").build()).build()

    /** Starts a new queue (replacing what was playing), resuming at [startMs] in the first item. */
    fun playNew(first: Item, startMs: Long = 0) {
        player.setMediaItem(mediaItem(first), startMs)
        player.prepare(); player.play()
    }

    /** Adds to the end of the queue (the next briefing, fetched while the first plays). */
    fun append(item: Item) {
        player.addMediaItem(mediaItem(item))
        if (player.playbackState == androidx.media3.common.Player.STATE_ENDED) { player.seekToNextMediaItem(); player.play() }
    }

    fun currentKey(): String? = _player?.currentMediaItem?.mediaId
    fun currentTitle(): String? = _player?.currentMediaItem?.mediaMetadata?.title?.toString()
}

/** The media session for Car mode's player (Android shows the playback notification and routes media buttons). */
class CarAudioService : MediaSessionService() {
    private var session: MediaSession? = null

    override fun onCreate() {
        super.onCreate()
        CarAudio.init(this)
        session = MediaSession.Builder(this, CarAudio.player).build()
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = session

    override fun onDestroy() {
        session?.release(); session = null
        super.onDestroy()
    }
}
