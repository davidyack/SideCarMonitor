package app.sidecarmonitor.watch

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.MediaPlayer
import android.media.MediaRecorder
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.coroutines.resume
import kotlin.math.sqrt

/** Records 16 kHz mono PCM on the watch mic until you pause (or [stop]), as a WAV for MAI-Transcribe. */
class WatchRecorder {
    @Volatile private var stopRequested = false

    fun stop() { stopRequested = true }

    @SuppressLint("MissingPermission") // checked by the caller
    suspend fun record(onLevel: (Float) -> Unit = {}): ByteArray = withContext(Dispatchers.IO) {
        stopRequested = false
        val rate = 16_000
        val min = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val rec = AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, rate,
            AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, maxOf(min, rate))
        val pcm = ByteArrayOutputStream()
        val buf = ShortArray(rate / 10)
        var heard = false
        var quietMs = 0
        var totalMs = 0
        rec.startRecording()
        try {
            while (!stopRequested && totalMs < 15_000) {
                val n = rec.read(buf, 0, buf.size)
                if (n <= 0) continue
                val bytes = ByteBuffer.allocate(n * 2).order(ByteOrder.LITTLE_ENDIAN)
                var sum = 0.0
                for (i in 0 until n) { bytes.putShort(buf[i]); sum += buf[i] * buf[i].toDouble() }
                pcm.write(bytes.array())
                val rms = sqrt(sum / n).toFloat()
                onLevel((rms / 3000f).coerceIn(0f, 1f))
                totalMs += 100
                if (rms > 700) { heard = true; quietMs = 0 } else quietMs += 100
                if (heard && quietMs >= 1200) break
                if (!heard && totalMs >= 6000) break
            }
        } finally {
            rec.stop(); rec.release()
        }
        if (!heard) ByteArray(0) else wav(pcm.toByteArray(), rate)
    }

    private fun wav(pcm: ByteArray, rate: Int): ByteArray {
        val h = ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN).apply {
            put("RIFF".toByteArray()); putInt(36 + pcm.size); put("WAVE".toByteArray())
            put("fmt ".toByteArray()); putInt(16); putShort(1); putShort(1); putInt(rate); putInt(rate * 2); putShort(2); putShort(16)
            put("data".toByteArray()); putInt(pcm.size)
        }
        return h.array() + pcm
    }
}

/** Plays SideCar's reply (MAI voice WAV) on the watch speaker. */
class WatchPlayer(private val context: Context) {
    private var player: MediaPlayer? = null
    private val audio = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    private val attributes = AudioAttributes.Builder()
        .setUsage(AudioAttributes.USAGE_MEDIA)
        .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
        .build()
    private val focus = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_MAY_DUCK).setAudioAttributes(attributes).build()

    suspend fun play(wav: ByteArray) {
        stop()
        if (wav.size < 64) return
        val file = File(context.cacheDir, "reply.wav").apply { writeBytes(wav) }
        audio.requestAudioFocus(focus)
        try {
            suspendCancellableCoroutine { cont ->
                val mp = MediaPlayer().apply {
                    setAudioAttributes(attributes)
                    setDataSource(file.absolutePath)
                    setOnCompletionListener { if (cont.isActive) cont.resume(Unit) }
                    setOnErrorListener { _, _, _ -> if (cont.isActive) cont.resume(Unit); true }
                    prepare(); start()
                }
                player = mp
                cont.invokeOnCancellation { stop() }
            }
        } finally {
            stop()
            audio.abandonAudioFocusRequest(focus)
        }
    }

    fun stop() {
        player?.runCatching { stop(); release() }
        player = null
    }
}
