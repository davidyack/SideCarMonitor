package app.sidecarmonitor.data

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import java.io.File
import java.io.IOException
import java.security.MessageDigest

/**
 * Keeps the phone useful through short outages: every successful read from the kiosk (status, to-dos, week,
 * calendar, feeds, ideas, weather, house, reminders, event pictures...) is saved on the phone, and when the kiosk
 * can't be reached the last saved copy is returned instead of an error, so screens show data, not blanks.
 * [staleSince] says when the oldest copy being shown was saved (null while everything is live).
 * Not cached: live camera pictures (an old frame would mislead), briefing audio (large), the chat socket.
 */
object OfflineCache {
    private var dir: File? = null
    private const val MAX_BYTES = 25L * 1024 * 1024
    private val _staleSince = MutableStateFlow<Long?>(null)
    val staleSince: StateFlow<Long?> = _staleSince
    private val lastHash = HashMap<String, Int>()

    fun init(context: Context) {
        if (dir == null) dir = File(context.filesDir, "offline-cache").apply { mkdirs() }
    }

    /** Which reads are worth keeping (by path + query, so it works over home Wi-Fi or Tailscale alike). */
    private fun keyFor(req: Request): String? {
        if (req.method != "GET") return null
        val path = req.url.encodedPath
        if (!path.startsWith("/api/") || path == "/api/ping" || path == "/api/chat") return null
        if (path.startsWith("/api/cameras/") || path.endsWith("/audio")) return null
        val raw = path + (req.url.encodedQuery?.let { "?$it" } ?: "")
        return MessageDigest.getInstance("SHA-1").digest(raw.toByteArray()).joinToString("") { "%02x".format(it) }
    }

    private fun store(key: String, bytes: ByteArray, type: String?) {
        val d = dir ?: return
        val hash = bytes.contentHashCode()
        synchronized(lastHash) { if (lastHash[key] == hash) return; lastHash[key] = hash } // unchanged: skip the write
        runCatching {
            File(d, "$key.bin").writeBytes(bytes)
            File(d, "$key.meta").writeText("${type ?: ""}\n${System.currentTimeMillis()}")
            prune(d)
        }
    }

    private fun load(key: String): Pair<ByteArray, Pair<String, Long>>? {
        val d = dir ?: return null
        val bin = File(d, "$key.bin"); val meta = File(d, "$key.meta")
        if (!bin.exists() || !meta.exists()) return null
        return runCatching {
            val (type, at) = meta.readText().split('\n').let { it[0] to (it.getOrNull(1)?.toLongOrNull() ?: 0L) }
            bin.readBytes() to (type to at)
        }.getOrNull()
    }

    private fun prune(d: File) {
        val files = d.listFiles { f -> f.name.endsWith(".bin") }?.sortedBy { it.lastModified() } ?: return
        var total = files.sumOf { it.length() }
        for (f in files) {
            if (total <= MAX_BYTES) break
            total -= f.length(); f.delete(); File(d, f.name.removeSuffix(".bin") + ".meta").delete()
        }
    }

    private fun cached(req: Request, key: String): Response? {
        val (bytes, meta) = load(key) ?: return null
        val (type, at) = meta
        _staleSince.value = minOf(_staleSince.value ?: at, at)
        return Response.Builder().request(req).protocol(Protocol.HTTP_1_1).code(200).message("OK (saved copy)")
            .header("X-SideCar-Saved-At", at.toString())
            .body(bytes.toResponseBody(type.toMediaTypeOrNull())).build()
    }

    /** OkHttp interceptor: save good reads; serve the saved copy when the kiosk can't be reached (or errors out). */
    val interceptor = Interceptor { chain ->
        val req = chain.request()
        val key = keyFor(req) ?: return@Interceptor chain.proceed(req)
        val res = try { chain.proceed(req) } catch (e: IOException) { return@Interceptor cached(req, key) ?: throw e }
        when {
            res.isSuccessful -> {
                val type = res.body.contentType()
                val bytes = res.body.bytes()
                store(key, bytes, type?.toString())
                _staleSince.value = null
                res.newBuilder().body(bytes.toResponseBody(type)).build()
            }
            res.code >= 500 -> cached(req, key)?.also { res.close() } ?: res
            else -> res
        }
    }
}
