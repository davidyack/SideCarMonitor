package app.sidecarmonitor.reminders

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
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
import app.sidecarmonitor.data.PairingStore
import app.sidecarmonitor.data.SideCarClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.util.concurrent.TimeUnit

/**
 * Phone side of reminders: every 15 minutes (Android's shortest background interval) and whenever the app opens,
 * ask the kiosk which reminders are due and post a notification for each, with Done / 1 hour / To-do buttons.
 * A due reminder re-alerts on every check until it's handled, which is the "bug me until it's done" part.
 * The Pixel Watch mirrors these notifications.
 */
object Reminders {
    private const val CHANNEL = "reminders"
    private const val WORK = "sidecar-reminders"

    fun schedule(context: Context) {
        val wm = WorkManager.getInstance(context)
        wm.enqueueUniquePeriodicWork(WORK, ExistingPeriodicWorkPolicy.KEEP,
            PeriodicWorkRequestBuilder<ReminderWorker>(15, TimeUnit.MINUTES).build())
        checkNow(context)
    }

    fun checkNow(context: Context) {
        WorkManager.getInstance(context).enqueueUniqueWork("$WORK-now", ExistingWorkPolicy.REPLACE,
            OneTimeWorkRequestBuilder<ReminderWorker>().build())
    }

    internal fun notificationId(reminderId: String) = 40_000 + (reminderId.hashCode() and 0x7fff)

    internal suspend fun client(context: Context): SideCarClient? {
        val pairing = PairingStore(context).load() ?: return null
        return SideCarClient(pairing).takeIf { it.connect() }
    }

    internal fun ensureChannel(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Reminders", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Reminders you asked SideCar for. They come back until you mark them done."
            })
    }

    internal fun post(context: Context, id: String, text: String, detail: String) {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        ensureChannel(context)
        fun action(label: String, action: String, minutes: Int = 60): NotificationCompat.Action {
            val intent = Intent(context, ReminderActionReceiver::class.java)
                .putExtra("id", id).putExtra("action", action).putExtra("minutes", minutes)
            val pi = PendingIntent.getBroadcast(context, (id + action).hashCode(), intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            return NotificationCompat.Action.Builder(0, label, pi).build()
        }
        val open = PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val n = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle(text)
            .setContentText(detail)
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setOnlyAlertOnce(false) // re-alert on every check: it keeps bugging you
            .setAutoCancel(false)
            .setContentIntent(open)
            .addAction(action("Done", "done"))
            .addAction(action("1 hour", "snooze", 60))
            .addAction(action("To-do", "todo"))
            .build()
        NotificationManagerCompat.from(context).notify(notificationId(id), n)
    }
}

class ReminderWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val c = Reminders.client(applicationContext) ?: return Result.success() // not paired / kiosk unreachable: try next time
        val due = runCatching { c.dueReminders() }.getOrNull() ?: return Result.success()
        val prefs = applicationContext.getSharedPreferences("reminders", Context.MODE_PRIVATE)
        val shown = prefs.getStringSet("shown", emptySet()).orEmpty()
        for (r in due) {
            val detail = listOfNotNull("Due ${r.dueText}", r.repeats.takeIf { it.isNotBlank() }, "snoozed ${r.snoozes}×".takeIf { r.snoozes > 0 })
                .joinToString(" · ")
            Reminders.post(applicationContext, r.id, r.text, detail)
        }
        // Clear notifications for reminders handled elsewhere (e.g. on the kiosk).
        val nm = NotificationManagerCompat.from(applicationContext)
        (shown - due.map { it.id }.toSet()).forEach { nm.cancel(Reminders.notificationId(it)) }
        prefs.edit().putStringSet("shown", due.map { it.id }.toSet()).apply()
        // Meeting heads-ups (prep ~30 min before, Join at 5) are scheduled from the same check.
        runCatching { MeetingAlerts.schedule(applicationContext, c) }
        return Result.success()
    }
}

/** Done / 1 hour / To-do from the notification: tell the kiosk, then clear the notification. */
class ReminderActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val id = intent.getStringExtra("id") ?: return
        val action = intent.getStringExtra("action") ?: "done"
        val minutes = intent.getIntExtra("minutes", 60)
        val pending = goAsync()
        CoroutineScope(Dispatchers.IO).launch {
            try {
                val error = Reminders.client(context)?.reminderAction(id, action, minutes) ?: "Can't reach the kiosk"
                if (error == null) NotificationManagerCompat.from(context).cancel(Reminders.notificationId(id))
                else Reminders.post(context, id, "Couldn't update the reminder", error)
            } finally {
                pending.finish()
            }
        }
    }
}
