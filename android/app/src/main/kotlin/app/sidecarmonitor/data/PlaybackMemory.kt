package app.sidecarmonitor.data

import android.content.Context
import android.media.MediaPlayer

/**
 * Remembers where you were in a briefing (LinkedIn, X, morning/evening brief) so reopening the app or the screen
 * picks up there instead of the start. Keys name one episode, e.g. "li:<episode time>". Forgotten when it finishes.
 */
object PlaybackMemory {
    private fun prefs(context: Context) = context.getSharedPreferences("playback", Context.MODE_PRIVATE)

    /** Seeks a freshly prepared player to the saved spot (a few seconds back); returns that position in ms, or 0. */
    fun resume(context: Context, key: String, player: MediaPlayer): Int {
        val at = prefs(context).getInt(key, 0)
        if (at < 5_000 || at > player.duration - 5_000) return 0
        val to = (at - 3_000).coerceAtLeast(0)
        player.seekTo(to)
        return to
    }

    fun save(context: Context, key: String, positionMs: Int) {
        if (positionMs >= 5_000) prefs(context).edit().putInt(key, positionMs).apply()
    }

    fun forget(context: Context, key: String) = prefs(context).edit().remove(key).apply()

    fun format(ms: Int) = "${ms / 60_000}:${"%02d".format(ms / 1000 % 60)}"
}
