package app.sidecarmonitor.ui

import android.content.Intent
import android.net.Uri
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.AppViewModel
import app.sidecarmonitor.data.BriefInfo
import app.sidecarmonitor.data.CalDay
import app.sidecarmonitor.data.CalEventInfo
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter

private fun calColor(c: String) = if (c.equals("PMCollab", true)) SideCarColors.Mauve else SideCarColors.Cobalt
private fun parseTime(s: String): LocalDateTime? = runCatching { OffsetDateTime.parse(s).toLocalDateTime() }.getOrElse { runCatching { LocalDateTime.parse(s) }.getOrNull() }
private val Hm = DateTimeFormatter.ofPattern("h:mm a")

/** Day view of both calendars (CTC + PMCollab): page days, tap a meeting for Join / Open, Block time. */
@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
fun DayScreen(vm: AppViewModel, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var day by remember { mutableStateOf(LocalDate.now()) }
    var data by remember { mutableStateOf<CalDay?>(null) }
    var note by remember { mutableStateOf("Loading…") }
    var adding by remember { mutableStateOf(false) }
    var selected by remember { mutableStateOf<CalEventInfo?>(null) }
    suspend fun reload() {
        note = "Loading…"
        data = runCatching { vm.kiosk?.calendarDay(day.toString()) }.getOrNull()
        note = when { data == null -> "Can't reach the kiosk"; data?.available != true -> "Calendars aren't set up"; else -> "${data!!.events.count { !it.allDay }} meetings · ${data!!.calendars.joinToString("  ")}" }
    }
    LaunchedEffect(day) { selected = null; reload() }

    Column(Modifier.fillMaxSize()) {
        PageHeader(if (day == LocalDate.now()) "Today, ${day.format(DateTimeFormatter.ofPattern("EEE MMM d"))}" else day.format(DateTimeFormatter.ofPattern("EEEE, MMM d")), onBack)
        Row(Modifier.padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            Btn("◀", SideCarColors.Panel) { day = day.minusDays(1) }
            Btn("Today", SideCarColors.Panel) { day = LocalDate.now() }
            Btn("▶", SideCarColors.Panel) { day = day.plusDays(1) }
            Spacer(Modifier.weight(1f))
            Btn("+ Block time", SideCarColors.Emerald) { adding = true }
        }
        Text(note, Modifier.padding(horizontal = 16.dp, vertical = 6.dp), color = SideCarColors.Muted, fontSize = 13.sp)
        if (adding) AddBlock(vm, day, data?.calendars.orEmpty(), onDone = { adding = false; scope.launch { reload() } })
        LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(data?.events.orEmpty()) { e ->
                val s = parseTime(e.start); val en = parseTime(e.end)
                Column(Modifier.fillMaxWidth().background(calColor(e.calendar)).clickable { selected = if (selected == e) null else e }.padding(12.dp)) {
                    Text(e.subject, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
                    Text((if (e.allDay) "All day" else "${s?.format(Hm)} – ${en?.format(Hm)}") + " · ${e.calendar}" + if (e.location.isNotBlank()) " · ${e.location}" else "",
                        fontSize = 13.sp, color = Color.White.copy(alpha = 0.85f))
                    if (selected == e) Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        if (e.organizer.isNotBlank()) Text("Organizer: ${e.organizer}", fontSize = 13.sp, modifier = Modifier.weight(1f))
                        if (e.joinUrl.isNotBlank()) Btn("Join", SideCarColors.Emerald) { runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(e.joinUrl))) } }
                        if (e.webLink.isNotBlank()) Btn("Open", SideCarColors.Steel) { runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(e.webLink))) } }
                    }
                }
            }
            if (data?.events?.isEmpty() == true) item { Text("Nothing scheduled.", color = SideCarColors.Muted) }
            item { Spacer(Modifier.height(96.dp)) }
        }
    }
}

@Composable
private fun Btn(text: String, color: Color, onClick: () -> Unit) =
    Text(text, Modifier.background(color).clickable(onClick = onClick).padding(horizontal = 14.dp, vertical = 10.dp), fontSize = 15.sp)

/** Block time: start (hour chips), length, calendar, title presets. Your own add: logged, not limited. */
@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
private fun AddBlock(vm: AppViewModel, day: LocalDate, calendars: List<String>, onDone: () -> Unit) {
    val scope = rememberCoroutineScope()
    var hour by remember { mutableStateOf(9) }
    var half by remember { mutableStateOf(false) }
    var minutes by remember { mutableStateOf(60) }
    var cal by remember { mutableStateOf(calendars.firstOrNull() ?: "CTC") }
    var title by remember { mutableStateOf("Focus time") }
    var status by remember { mutableStateOf("") }
    Column(Modifier.fillMaxWidth().padding(12.dp).background(SideCarColors.Panel).padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("Block time on ${day.format(DateTimeFormatter.ofPattern("EEE MMM d"))}", fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            (7..19).forEach { h -> Btn(if (h <= 12) "$h${if (h == 12) "p" else "a"}" else "${h - 12}p", if (h == hour) SideCarColors.Cobalt else SideCarColors.Steel) { hour = h } }
            Btn(if (half) ":30" else ":00", SideCarColors.Steel) { half = !half }
        }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) { listOf(30, 60, 90, 120).forEach { m -> Btn(if (m < 60) "$m min" else "${m / 60.0}h".replace(".0", ""), if (m == minutes) SideCarColors.Cobalt else SideCarColors.Steel) { minutes = m } } }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) { calendars.forEach { c -> Btn(c, if (c == cal) calColor(c) else SideCarColors.Steel) { cal = c } } }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf("Focus time", "Busy", "Lunch", "Travel", "Personal").forEach { t -> Btn(t, if (t == title) SideCarColors.Cobalt else SideCarColors.Steel) { title = t } }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            Btn("Add", SideCarColors.Emerald) {
                scope.launch {
                    val start = day.atTime(hour, if (half) 30 else 0).toString()
                    val err = vm.kiosk?.calendarAdd(cal, title, start, minutes) ?: "Not connected"
                    if (err == null) onDone() else status = err
                }
            }
            Btn("Block", SideCarColors.Alert) {
                scope.launch {
                    val err = vm.kiosk?.calendarAdd(cal, "Blocked", day.atTime(hour, if (half) 30 else 0).toString(), minutes) ?: "Not connected"
                    if (err == null) onDone() else status = err
                }
            }
            Btn("Cancel", SideCarColors.Panel) { onDone() }
            if (status.isNotBlank()) Text(status, color = SideCarColors.Alert, fontSize = 13.sp)
        }
    }
}

/** The latest morning/evening brief as a card (home screen): Play reads it aloud, Dismiss hides it. */
@Composable
fun BriefCard(vm: AppViewModel) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var brief by remember { mutableStateOf<BriefInfo?>(null) }
    var expanded by remember { mutableStateOf(false) }
    var player by remember { mutableStateOf<android.media.MediaPlayer?>(null) }
    var status by remember { mutableStateOf("") }
    LaunchedEffect(Unit) {
        // The kiosk connection may not be ready yet at launch: retry soon, then refresh every few minutes.
        while (true) {
            val b = runCatching { vm.kiosk?.brief() }.getOrNull()
            if (b != null && player == null) brief = b
            kotlinx.coroutines.delay(if (b == null) 3_000 else 300_000)
        }
    }
    DisposableEffect(Unit) { onDispose { player?.release() } }
    val b = brief ?: return
    if (!b.available || b.dismissed) return
    Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp).background(SideCarColors.Panel).padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(b.title, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = SideCarColors.Amber)
        Text(b.text, fontSize = 14.sp, maxLines = if (expanded) 40 else 4, modifier = Modifier.clickable { expanded = !expanded })
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            Btn(if (player?.isPlaying == true) "⏹ Stop" else "▶ Play", SideCarColors.Cobalt) {
                player?.let { it.stop(); it.release(); player = null; status = ""; return@Btn }
                scope.launch {
                    status = "Preparing audio…"
                    val bytes = runCatching { vm.kiosk?.briefAudio() }.getOrNull() ?: run { status = "Couldn't get the audio"; return@launch }
                    val f = java.io.File(context.cacheDir, "brief.wav").apply { writeBytes(bytes) }
                    player = android.media.MediaPlayer().apply {
                        setAudioAttributes(android.media.AudioAttributes.Builder().setUsage(android.media.AudioAttributes.USAGE_MEDIA).build())
                        setDataSource(f.absolutePath); prepare(); start(); setOnCompletionListener { status = "" }
                    }
                    status = ""
                }
            }
            Btn("Dismiss", SideCarColors.Amber) { player?.release(); player = null; scope.launch { vm.kiosk?.briefDismiss(); brief = brief?.copy(dismissed = true) } }
            Spacer(Modifier.width(4.dp))
            if (status.isNotBlank()) Text(status, color = SideCarColors.Muted, fontSize = 13.sp)
        }
    }
}
