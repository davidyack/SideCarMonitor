package app.sidecarmonitor.reminders

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import app.sidecarmonitor.MainActivity
import app.sidecarmonitor.R
import app.sidecarmonitor.data.MeetingInfo
import app.sidecarmonitor.data.SideCarClient
import kotlinx.coroutines.delay
import java.time.Duration
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.concurrent.TimeUnit

/**
 * Meeting heads-ups on the phone (and the watch, which mirrors notifications): the prep note about 30 minutes
 * before each real meeting, and Join 5 minutes before. Scheduled from the kiosk's /api/meetings on every reminder
 * check (every 15 minutes and when the app opens); each alert re-reads the meeting when it fires, so a moved or
 * cancelled meeting doesn't buzz you.
 */
object MeetingAlerts {
    private const val CHANNEL = "meetings"

    suspend fun schedule(context: Context, client: SideCarClient) {
        val info = runCatching { client.meetings() }.getOrNull() ?: return
        val wm = WorkManager.getInstance(context)
        val now = OffsetDateTime.now()
        for (m in info.meetings) {
            val start = runCatching { OffsetDateTime.parse(m.start) }.getOrNull() ?: continue
            val until = Duration.between(now, start)
            if (until.isNegative) continue
            fun enqueue(kind: String, before: Long) {
                val delay = until.minusMinutes(before)
                // Already inside the window (e.g. the app just opened 20 minutes before): alert now, unless it's nearly time.
                if (delay.isNegative && until.toMinutes() < 3) return
                wm.enqueueUniqueWork("meeting-$kind-${m.key.hashCode()}", ExistingWorkPolicy.KEEP,
                    OneTimeWorkRequestBuilder<MeetingAlertWorker>()
                        .setInitialDelay(delay.toMillis().coerceAtLeast(0), TimeUnit.MILLISECONDS)
                        .setInputData(workDataOf("key" to m.key, "kind" to kind)).build())
            }
            if (m.worthPrep && until.toMinutes() > 6) enqueue("prep", info.leadMinutes.toLong())
            if (m.worthPrep || m.joinUrl.isNotBlank()) enqueue("join", 5)
        }
    }

    internal fun post(context: Context, m: MeetingInfo, kind: String) {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val nm = context.getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Meetings", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Heads-up with prep before a meeting, and Join when it's about to start."
            })
        val start = runCatching { OffsetDateTime.parse(m.start).atZoneSameInstant(ZoneId.systemDefault()) }.getOrNull()
        val minutes = start?.let { Duration.between(OffsetDateTime.now(), it).toMinutes().coerceAtLeast(0) } ?: 0
        val time = start?.format(DateTimeFormatter.ofPattern("h:mm a")) ?: ""
        val title = if (kind == "prep") "In $minutes min: ${m.subject}" else "Starting at $time: ${m.subject}"
        val summary = listOf(time, m.calendar, m.organizer.takeIf { it.isNotBlank() }?.let { "Organizer: $it" }).filterNotNull().filter { it.isNotBlank() }.joinToString(" · ")
        val body = if (m.prep.isNotBlank()) m.prep else summary
        val open = PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val n = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle(title)
            .setContentText(body.lineSequence().firstOrNull() ?: summary)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setCategory(NotificationCompat.CATEGORY_EVENT)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setContentIntent(open)
            .setTimeoutAfter(Duration.ofMinutes(minutes + 15).toMillis())
        if (m.joinUrl.isNotBlank()) {
            val join = PendingIntent.getActivity(context, (m.key + kind).hashCode(), Intent(Intent.ACTION_VIEW, Uri.parse(m.joinUrl)), PendingIntent.FLAG_IMMUTABLE)
            n.addAction(NotificationCompat.Action.Builder(0, "Join", join).build())
        }
        NotificationManagerCompat.from(context).notify(50_000 + ((m.key + kind).hashCode() and 0x7fff), n.build())
    }
}

class MeetingAlertWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val key = inputData.getString("key") ?: return Result.success()
        val kind = inputData.getString("kind") ?: "join"
        val c = Reminders.client(applicationContext) ?: return Result.success()
        var m = runCatching { c.meetings() }.getOrNull()?.meetings?.firstOrNull { it.key == key } ?: return Result.success() // moved or cancelled
        // The kiosk writes the prep note right around now: give it a moment.
        if (kind == "prep" && m.prep.isBlank()) {
            delay(90_000)
            m = runCatching { c.meetings() }.getOrNull()?.meetings?.firstOrNull { it.key == key } ?: m
        }
        MeetingAlerts.post(applicationContext, m, kind)
        return Result.success()
    }
}
