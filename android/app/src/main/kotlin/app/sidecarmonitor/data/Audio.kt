package app.sidecarmonitor.data

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioFormat
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

/**
 * Records 16 kHz mono 16-bit PCM from the mic until you pause after speaking (or [stop] is called),
 * and returns it as a WAV file for the kiosk's MAI-Transcribe.
 */
class VoiceRecorder {
    @Volatile private var stopRequested = false

    fun stop() { stopRequested = true }

    @SuppressLint("MissingPermission") // the caller checks RECORD_AUDIO first
    suspend fun record(onLevel: (Float) -> Unit = {}): ByteArray = withContext(Dispatchers.IO) {
        stopRequested = false
        val rate = 16_000
        val min = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val rec = AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, rate,
            AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, maxOf(min, rate))
        val pcm = ByteArrayOutputStream()
        val buf = ShortArray(rate / 10) // 100 ms
        var heardSpeech = false
        var quietMs = 0
        var totalMs = 0
        rec.startRecording()
        try {
            while (!stopRequested && totalMs < 20_000) {
                val n = rec.read(buf, 0, buf.size)
                if (n <= 0) continue
                val bytes = ByteBuffer.allocate(n * 2).order(ByteOrder.LITTLE_ENDIAN)
                var sum = 0.0
                for (i in 0 until n) { bytes.putShort(buf[i]); sum += buf[i] * buf[i].toDouble() }
                pcm.write(bytes.array())
                val rms = sqrt(sum / n).toFloat()
                onLevel((rms / 3000f).coerceIn(0f, 1f))
                totalMs += 100
                if (rms > 900) { heardSpeech = true; quietMs = 0 } else quietMs += 100
                // Stop ~1.2 s after you finish talking, or after 6 s if you never start.
                if (heardSpeech && quietMs >= 1200) break
                if (!heardSpeech && totalMs >= 6000) break
            }
        } finally {
            rec.stop()
            rec.release()
        }
        if (!heardSpeech) ByteArray(0) else wav(pcm.toByteArray(), rate)
    }

    private fun wav(pcm: ByteArray, rate: Int): ByteArray {
        val header = ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN).apply {
            put("RIFF".toByteArray()); putInt(36 + pcm.size); put("WAVE".toByteArray())
            put("fmt ".toByteArray()); putInt(16); putShort(1); putShort(1); putInt(rate); putInt(rate * 2)
            putShort(2); putShort(16)
            put("data".toByteArray()); putInt(pcm.size)
        }
        return header.array() + pcm
    }
}

/** Plays the kiosk's MAI-voice WAV replies as speech on the media volume, with transient audio focus. */
class VoicePlayer(private val context: Context) {
    private var player: MediaPlayer? = null
    private val audio = context.getSystemService(Context.AUDIO_SERVICE) as android.media.AudioManager
    private val attributes = android.media.AudioAttributes.Builder()
        .setUsage(android.media.AudioAttributes.USAGE_MEDIA) // follows the media volume
        .setContentType(android.media.AudioAttributes.CONTENT_TYPE_SPEECH)
        .build()
    private val focus = android.media.AudioFocusRequest.Builder(android.media.AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_MAY_DUCK)
        .setAudioAttributes(attributes)
        .build()

    suspend fun play(wav: ByteArray) {
        stop()
        if (wav.size < 64) throw IllegalStateException("The kiosk sent no audio for the reply")
        val file = File(context.cacheDir, "reply.wav").apply { writeBytes(wav) }
        audio.requestAudioFocus(focus)
        try {
            playFile(file)
        } finally {
            audio.abandonAudioFocusRequest(focus)
        }
    }

    private suspend fun playFile(file: File) {
        suspendCancellableCoroutine { cont ->
            val mp = MediaPlayer().apply {
                setAudioAttributes(attributes)
                setDataSource(file.absolutePath)
                setOnCompletionListener { if (cont.isActive) cont.resume(Unit) }
                setOnErrorListener { _, _, _ -> if (cont.isActive) cont.resume(Unit); true }
                prepare()
                start()
            }
            player = mp
            cont.invokeOnCancellation { stop() }
        }
        stop()
    }

    fun stop() {
        player?.runCatching { stop(); release() }
        player = null
    }
}
