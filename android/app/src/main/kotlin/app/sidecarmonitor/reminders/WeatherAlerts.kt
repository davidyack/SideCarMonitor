package app.sidecarmonitor.reminders

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
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import app.sidecarmonitor.MainActivity
import app.sidecarmonitor.R
import java.time.Duration
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.util.concurrent.TimeUnit

/**
 * Phone side of weather alerts: every 15 minutes (Android's shortest background interval) and whenever the app
 * opens, read the kiosk's Activity feed and post a notification for each new weather alert from the last 3 hours
 * (gusts, a sudden drop, rain or snow starting or coming, lightning). Each alert notifies once; the Pixel Watch
 * mirrors the notification. The kiosk decides what's worth an alert, so the phone just relays it.
 */
object WeatherAlerts {
    private const val CHANNEL = "weather"
    private const val WORK = "sidecar-weather"
    private val RECENT: Duration = Duration.ofHours(3)

    fun schedule(context: Context) {
        val wm = WorkManager.getInstance(context)
        wm.enqueueUniquePeriodicWork(WORK, ExistingPeriodicWorkPolicy.KEEP,
            PeriodicWorkRequestBuilder<WeatherAlertWorker>(15, TimeUnit.MINUTES).build())
        wm.enqueueUniqueWork("$WORK-now", ExistingWorkPolicy.REPLACE, OneTimeWorkRequestBuilder<WeatherAlertWorker>().build())
    }

    internal fun isRecent(start: String): Boolean {
        val at = runCatching { OffsetDateTime.parse(start).toLocalDateTime() }.getOrNull()
            ?: runCatching { LocalDateTime.parse(start) }.getOrNull()
            ?: return false
        return Duration.between(at, LocalDateTime.now()) < RECENT
    }

    private fun ensureChannel(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Weather alerts", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Big weather changes at home: strong gusts, sudden drops, rain or snow starting or on the way, lightning."
            })
    }

    internal fun post(context: Context, id: String, title: String, detail: String) {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        ensureChannel(context)
        val open = PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val n = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle(title)
            .setContentText(detail)
            .setStyle(NotificationCompat.BigTextStyle().bigText(detail))
            .setSubText("Weather")
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setTimeoutAfter(RECENT.toMillis())   // stale weather news clears itself
            .setContentIntent(open)
            .build()
        NotificationManagerCompat.from(context).notify(50_000 + (id.hashCode() and 0x7fff), n)
    }
}

class WeatherAlertWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val c = Reminders.client(applicationContext) ?: return Result.success() // not paired / kiosk unreachable: try next time
        val activity = runCatching { c.status().activity }.getOrNull() ?: return Result.success()
        val prefs = applicationContext.getSharedPreferences("weather-alerts", Context.MODE_PRIVATE)
        val notified = prefs.getStringSet("notified", emptySet()).orEmpty()
        val fresh = activity.filter { it.source == "weather" && it.id.isNotBlank() && WeatherAlerts.isRecent(it.start) }
        // Oldest first, so they stack in the order they happened.
        for (a in fresh.filter { it.id !in notified }.reversed()) WeatherAlerts.post(applicationContext, a.id, a.kind, a.detail)
        // Remember only what's still recent, so the set doesn't grow forever.
        prefs.edit().putStringSet("notified", fresh.map { it.id }.toSet()).apply()
        return Result.success()
    }
}
