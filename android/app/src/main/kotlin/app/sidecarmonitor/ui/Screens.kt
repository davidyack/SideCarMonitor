package app.sidecarmonitor.ui

import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import android.graphics.BitmapFactory
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.Chat
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.material.icons.filled.Groups
import androidx.compose.material.icons.filled.Lightbulb
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Thermostat
import androidx.compose.material.icons.filled.Videocam
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.AssistantState
import app.sidecarmonitor.UiState
import kotlinx.coroutines.delay
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.time.LocalTime
import java.time.format.DateTimeFormatter

enum class Screen { Home, Cameras, Weather, Activity, Tasks, Week, House, LinkedIn, X, Day, Ideas, Settings }

private fun JsonObject?.str(key: String): String? =
    this?.get(key)?.let { runCatching { it.jsonPrimitive.content }.getOrNull() }?.takeIf { it != "null" }

// ---- Home: Windows Phone style tiles --------------------------------------------------------

@Composable
fun HomeScreen(state: UiState, vm: app.sidecarmonitor.AppViewModel, onOpen: (Screen) -> Unit, onTalk: () -> Unit) {
    val now by produceState(LocalTime.now()) { while (true) { value = LocalTime.now(); delay(1000) } }
    val weather = state.status?.weather
    val cams = state.status?.cameras.orEmpty()
    val activity = state.status?.activity.orEmpty()
    val activeNow = activity.firstOrNull { it.active }
    // Next meeting (both calendars) for the clock tile: amber from 15 minutes before.
    var meetings by remember { mutableStateOf<List<app.sidecarmonitor.data.MeetingInfo>>(emptyList()) }
    LaunchedEffect(state.connected) {
        while (true) {
            runCatching { vm.kiosk?.meetings() }.getOrNull()?.let { meetings = it.meetings }
            delay(120_000)
        }
    }
    val nowInstant = java.time.OffsetDateTime.now()
    val next = meetings.mapNotNull { m -> runCatching { Triple(m, java.time.OffsetDateTime.parse(m.start), java.time.OffsetDateTime.parse(m.end)) }.getOrNull() }
        .filter { (m, _, end) -> end.isAfter(nowInstant) && !m.subject.lowercase().startsWith("hold") }
        .minByOrNull { it.second }
    val untilMin = next?.let { java.time.Duration.between(nowInstant, it.second).toMinutes() }
    val meetingSoon = untilMin != null && untilMin <= 15 && untilMin > -5
    // X, LinkedIn and House tiles: real numbers (unread signal and the newest post; lights on and any unlocked doors).
    var summary by remember { mutableStateOf<app.sidecarmonitor.data.HomeSummary?>(null) }
    LaunchedEffect(state.connected) { while (true) { runCatching { vm.kiosk?.summary() }.getOrNull()?.let { summary = it }; delay(120_000) } }
    // Ideas tile: count of new ideas and the best one's title.
    var ideas by remember { mutableStateOf<app.sidecarmonitor.data.IdeasInfo?>(null) }
    LaunchedEffect(state.connected) { while (true) { runCatching { vm.kiosk?.ideas("all") }.getOrNull()?.let { ideas = it }; delay(300_000) } }

    Column(Modifier.fillMaxSize().verticalScroll(androidx.compose.foundation.rememberScrollState()).padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        // Short outage: say the data is a saved copy (and from when), instead of going blank.
        val savedAt by app.sidecarmonitor.data.OfflineCache.staleSince.collectAsState()
        savedAt?.let { at ->
            val time = java.time.Instant.ofEpochMilli(at).atZone(java.time.ZoneId.systemDefault()).format(DateTimeFormatter.ofPattern("h:mm a"))
            Text("Kiosk out of reach \u00B7 showing saved data from $time", Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp),
                fontSize = 14.sp, color = SideCarColors.Amber)
        }
        OutboxBanner(state.outbox, onRetry = vm::flushOutbox, onApprove = vm::approveOutbox, onDiscard = vm::discardOutbox)
        BriefCard(vm)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Tile(if (meetingSoon) SideCarColors.Amber else SideCarColors.Panel, Modifier.weight(1f).aspectRatio(1f), onClick = { onOpen(Screen.Day) }) {
                Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.SpaceBetween) {
                    Text(now.format(DateTimeFormatter.ofPattern("h:mm")), fontSize = 44.sp, fontWeight = FontWeight.Light)
                    if (next != null && untilMin != null) Column {
                        Text(when {
                            untilMin <= 0 -> "Now \u00B7 ${next.first.calendar}"
                            untilMin <= 60 -> "In $untilMin min \u00B7 ${next.first.calendar}"
                            else -> "${next.second.atZoneSameInstant(java.time.ZoneId.systemDefault()).format(DateTimeFormatter.ofPattern("h:mm a"))} \u00B7 ${next.first.calendar}"
                        }, fontSize = 12.sp, color = if (meetingSoon) Color.White else SideCarColors.Muted)
                        Text(next.first.subject, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    }
                    Text(
                        if (state.connected) "Kiosk ${state.connectionLabel}" else (state.lastError ?: "Connecting…"),
                        color = if (state.connected) SideCarColors.Ok else SideCarColors.Muted, fontSize = 13.sp,
                        maxLines = 2, overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            Tile(SideCarColors.Amber, Modifier.weight(1f).aspectRatio(1f), onClick = { onOpen(Screen.Weather) },
                icon = Icons.Filled.Thermostat, value = weather.str("temperature") ?: "--",
                detail = summary?.weatherNext ?: weather.str("condition") ?: "", label = "Weather")
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Tile(SideCarColors.Cobalt, Modifier.weight(2f).height(150.dp), onClick = { onOpen(Screen.Cameras) },
                icon = Icons.Filled.Videocam,
                value = if (cams.isEmpty()) "--" else "${cams.count { it.live }}/${cams.size}",
                detail = "live", label = "Cameras")
            Tile(SideCarColors.Teal, Modifier.weight(1f).height(150.dp), onClick = { onOpen(Screen.House) },
                icon = Icons.Filled.Home, value = summary?.house?.lightsOn?.toString() ?: "",
                detail = summary?.house?.let { h ->
                    when {
                        h.paused != null -> "Home control paused"
                        h.unlocked.isNotEmpty() -> "Unlocked: ${h.unlocked.joinToString()}"
                        else -> "${h.lightsOn} of ${h.lights} lights on" + if (h.locks > 0) " \u00B7 all locked" else ""
                    }
                } ?: "Lights, scripts", label = "House")
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Tile(if (activeNow != null) SideCarColors.Alert else SideCarColors.Crimson, Modifier.weight(1f).aspectRatio(1f),
                onClick = { onOpen(Screen.Activity) }, icon = Icons.Filled.Notifications,
                // Cleared items stay in History but don't count here.
                value = activity.count { !it.cleared }.toString(),
                detail = activeNow?.let { "${it.kind} at ${it.camera}" } ?: activity.firstOrNull { !it.cleared }?.let { "Last: ${it.kind}" } ?: "Nothing new",
                label = "Activity")
            Tile(Color(0xFF2F3640), Modifier.weight(1f).aspectRatio(1f), onClick = { onOpen(Screen.X) },
                icon = null, value = summary?.x?.signal?.toString() ?: "\uD835\uDD4F",
                detail = summary?.x?.newest ?: "Signal", label = "X")
        }
        val todo = state.tasks?.views
        val needs = todo?.needsMe.orEmpty()
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Tile(if (needs.isNotEmpty()) SideCarColors.Crimson else SideCarColors.Emerald, Modifier.weight(1.4f).height(130.dp),
                onClick = { onOpen(Screen.Week) }, icon = Icons.Filled.Checklist,
                value = (todo?.today?.size ?: 0).toString(),
                detail = if (needs.isNotEmpty()) "${needs.size} need${if (needs.size == 1) "s" else ""} you"
                         else todo?.today?.firstOrNull()?.title ?: "Nothing for today",
                label = "To-do")
            Tile(Color(0xFF0A66C2), Modifier.weight(1f).height(130.dp), onClick = { onOpen(Screen.LinkedIn) },
                icon = Icons.Filled.Groups, value = summary?.linkedin?.signal?.toString() ?: "",
                detail = summary?.linkedin?.newest ?: "Signal", label = "LinkedIn")
            Tile(SideCarColors.Steel, Modifier.weight(0.8f).height(130.dp), onClick = { onOpen(Screen.Settings) },
                icon = Icons.Filled.Settings, value = "", detail = "", label = "Settings")
        }
        if (ideas?.available == true) {
            Tile(SideCarColors.Mauve, Modifier.fillMaxWidth().height(110.dp), onClick = { onOpen(Screen.Ideas) },
                icon = androidx.compose.material.icons.Icons.Filled.Lightbulb, value = (ideas?.ideas?.count { it.status == "new" } ?: 0).toString(),
                detail = ideas?.ideas?.firstOrNull()?.title ?: "Reading your feeds", label = "Ideas")
        }
    }
}

@Composable
fun Tile(
    color: Color,
    modifier: Modifier,
    onClick: (() -> Unit)?,
    icon: ImageVector? = null,
    value: String = "",
    detail: String = "",
    label: String = "",
    content: (@Composable () -> Unit)? = null,
) {
    Box(
        modifier
            .background(color)
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(14.dp),
    ) {
        if (content != null) { content(); return@Box }
        if (icon != null) Icon(icon, null, Modifier.size(40.dp).align(Alignment.TopStart), tint = Color.White)
        if (value.isNotEmpty()) Text(value, Modifier.align(Alignment.TopEnd), fontSize = 28.sp, color = Color.White)
        Column(Modifier.align(Alignment.BottomStart)) {
            if (detail.isNotEmpty()) Text(detail, fontSize = 13.sp, color = Color.White.copy(alpha = 0.85f), maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(label, fontSize = 18.sp, color = Color.White)
        }
    }
}

// ---- Detail screens ---------------------------------------------------------------------------

@Composable
fun PageHeader(title: String, onBack: () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = onBack, modifier = Modifier.size(56.dp)) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") }
        Text(title, fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
fun CamerasScreen(state: UiState, snapshot: suspend (Int) -> ByteArray?, onBack: () -> Unit) {
    Column(Modifier.fillMaxSize()) {
        PageHeader("Cameras", onBack)
        LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            items(state.status?.cameras.orEmpty()) { cam ->
                // Refresh each camera's still every 5 seconds while this screen is open.
                val image by produceState<androidx.compose.ui.graphics.ImageBitmap?>(null, cam.index) {
                    while (true) {
                        snapshot(cam.index)?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }?.let { value = it.asImageBitmap() }
                        delay(5000)
                    }
                }
                Column {
                    Box(Modifier.fillMaxWidth().aspectRatio(16f / 9f).clip(RoundedCornerShape(8.dp)).background(Color.Black)) {
                        image?.let { Image(it, cam.name, Modifier.fillMaxSize(), contentScale = ContentScale.Fit) }
                    }
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(10.dp).clip(CircleShape).background(if (cam.live) SideCarColors.Ok else SideCarColors.Muted))
                        Spacer(Modifier.width(8.dp))
                        Text(cam.name, fontSize = 18.sp)
                    }
                }
            }
        }
    }
}


@Composable
fun ActivityScreen(state: UiState, snapshot: suspend (Int) -> ByteArray?, eventSnapshot: suspend (String, Int) -> ByteArray?,
                   onClear: () -> Unit, onBack: () -> Unit) {
    var history by remember { mutableStateOf(false) }
    var filter by remember { mutableStateOf("all") }
    // Tapping an event opens its details; Back returns to the list.
    var selected by remember { mutableStateOf<app.sidecarmonitor.data.ActivityItem?>(null) }
    selected?.let { a ->
        androidx.activity.compose.BackHandler { selected = null }
        ActivityDetail(state, a, snapshot, eventSnapshot) { selected = null }
        return
    }
    Column(Modifier.fillMaxSize()) {
        PageHeader("Activity", onBack)
        val all = state.status?.activity.orEmpty()
        val items = (if (history) all else all.filter { !it.cleared }).filter {
            when (filter) { "camera" -> it.source == "camera"; "door" -> it.source == "door"; "house" -> it.source in listOf("house", "sidecar"); "weather" -> it.source == "weather"; "system" -> it.source in listOf("system", "sidecar"); else -> true }
        }
        Row(Modifier.padding(horizontal = 12.dp).horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf("all" to "All", "camera" to "Cameras", "door" to "Doors", "house" to "House", "weather" to "Weather", "system" to "System").forEach { (k, label) ->
                Text(label, Modifier.background(if (filter == k) SideCarColors.Cobalt else SideCarColors.Panel).clickable { filter = k }
                    .padding(horizontal = 10.dp, vertical = 8.dp), fontSize = 13.sp)
            }
        }
        Row(Modifier.padding(horizontal = 12.dp, vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("Clear", Modifier.background(SideCarColors.Crimson).clickable { history = false; onClear() }
                .padding(horizontal = 16.dp, vertical = 10.dp), fontSize = 15.sp)
            Text(if (history) "History: on" else "History", Modifier.background(if (history) SideCarColors.Amber else SideCarColors.Panel)
                .clickable { history = !history }.padding(horizontal = 16.dp, vertical = 10.dp), fontSize = 15.sp)
            val hidden = all.count { it.cleared }
            if (hidden > 0) Text("$hidden cleared", Modifier.padding(top = 10.dp), color = SideCarColors.Muted, fontSize = 13.sp)
        }
        if (items.isEmpty()) Text(if (all.isEmpty()) "No activity yet" else "All clear", Modifier.padding(16.dp), color = SideCarColors.Muted)
        LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            items(items, key = { it.id.ifBlank { it.start + it.camera } }) { a ->
                Row(Modifier.fillMaxWidth().background(if (a.active || a.alert) SideCarColors.Crimson else SideCarColors.Panel)
                    .clickable { selected = a }.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("${a.kind} \u00B7 ${a.camera}", fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
                        Text("${eventTime(a.start)} \u00B7 ${a.duration}", color = Color.White.copy(alpha = 0.75f), fontSize = 14.sp)
                        if (a.ai.isNotBlank()) Text(a.ai, color = Color(0xFF6CB4FF), fontSize = 14.sp)
                    }
                    a.snapshots.lastOrNull()?.let { n ->
                        EventPicture(a.id, n, eventSnapshot, Modifier.width(96.dp).aspectRatio(16f / 9f).clip(RoundedCornerShape(4.dp)), ContentScale.Crop)
                    }
                }
            }
        }
    }
}

/** "Mon 1:42 PM" from the kiosk's ISO timestamp. */
private fun eventTime(iso: String): String = runCatching {
    java.time.OffsetDateTime.parse(iso).format(DateTimeFormatter.ofPattern("EEE h:mm a"))
}.getOrElse { runCatching {
    java.time.LocalDateTime.parse(iso).format(DateTimeFormatter.ofPattern("EEE h:mm a"))
}.getOrElse { iso.substringAfter('T').take(5) } }

@Composable
private fun EventPicture(id: String, n: Int, eventSnapshot: suspend (String, Int) -> ByteArray?, modifier: Modifier, scale: ContentScale = ContentScale.Fit) {
    val image by produceState<androidx.compose.ui.graphics.ImageBitmap?>(null, id, n) {
        value = eventSnapshot(id, n)?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }?.asImageBitmap()
    }
    Box(modifier.background(Color.Black), contentAlignment = Alignment.Center) {
        image?.let { Image(it, null, Modifier.fillMaxSize(), contentScale = scale) }
    }
}

@Composable
private fun ActivityDetail(state: UiState, a: app.sidecarmonitor.data.ActivityItem, snapshot: suspend (Int) -> ByteArray?,
                           eventSnapshot: suspend (String, Int) -> ByteArray?, onBack: () -> Unit) {
    val camera = state.status?.cameras?.firstOrNull { it.name == a.camera }
    // Live view of that camera, refreshed every 3 seconds.
    val live by produceState<androidx.compose.ui.graphics.ImageBitmap?>(null, camera?.index) {
        val index = camera?.index ?: return@produceState
        while (true) {
            snapshot(index)?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }?.let { value = it.asImageBitmap() }
            delay(3000)
        }
    }
    val pictureModifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f).clip(RoundedCornerShape(8.dp))
    Column(Modifier.fillMaxSize()) {
        PageHeader("${a.kind} \u00B7 ${a.camera}", onBack)
        LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            if (a.active) item { Text("Happening now", Modifier.fillMaxWidth().background(SideCarColors.Crimson).padding(12.dp), fontSize = 18.sp) }
            if (a.source == "camera") items(a.snapshots) { n ->
                EventPicture(a.id, n, eventSnapshot, pictureModifier)
                Text(if (n == 1) "When it started" else "When it was seen as a ${a.kind.lowercase()}",
                    Modifier.padding(top = 4.dp), color = SideCarColors.Muted, fontSize = 13.sp)
            }
            if (a.source == "camera" && a.snapshots.isEmpty()) item { Text("No picture was saved for this event", color = SideCarColors.Muted, fontSize = 13.sp) }
            if (a.source == "weather") { if (a.detail.isNotBlank()) item { DetailRow("Details", a.detail) } }
            else if (a.ai.isNotBlank()) item { DetailRow("AI says", a.ai) }
            item { DetailRow(if (a.source == "camera") "Camera saw" else "What", a.kind) }
            item { DetailRow(if (a.source == "camera") "Camera" else "Where", a.camera) }
            item { DetailRow("Started", eventTime(a.start)) }
            if (a.source == "camera") item { DetailRow("Lasted", a.duration) }
            if (a.source == "camera") item {
                Box(pictureModifier.background(Color.Black), contentAlignment = Alignment.Center) {
                    live?.let { Image(it, a.camera, Modifier.fillMaxSize(), contentScale = ContentScale.Fit) }
                        ?: Text(if (camera == null) "Camera not found" else "Loading\u2026", color = SideCarColors.Muted)
                }
                Text("Live now", Modifier.padding(top = 4.dp, bottom = 16.dp), color = SideCarColors.Muted, fontSize = 13.sp)
            }
        }
    }
}

@Composable
private fun DetailRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp)) {
        Text(label, Modifier.width(96.dp), color = SideCarColors.Muted, fontSize = 16.sp)
        Text(value, fontSize = 17.sp)
    }
}

@Composable
fun SettingsScreen(state: UiState, onScan: () -> Unit, onUnpair: () -> Unit, onBack: () -> Unit) {
    Column(Modifier.fillMaxSize()) {
        PageHeader("Settings", onBack)
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(if (state.connected) "Connected to the kiosk ${state.connectionLabel}" else "Not connected", fontSize = 16.sp)
            BigButton("Pair again (scan QR)", Icons.Filled.QrCodeScanner, SideCarColors.Cobalt, onScan)
            BigButton("Unpair this phone", Icons.Filled.Close, SideCarColors.Steel, onUnpair)
        }
    }
}

@Composable
fun PairScreen(error: String?, onScan: () -> Unit) {
    Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center) {
        Text("SideCar", fontSize = 40.sp, fontWeight = FontWeight.Light)
        Spacer(Modifier.height(12.dp))
        Text("On the kiosk, open Settings → Pair phone, then scan the QR code.", fontSize = 18.sp, color = SideCarColors.Muted)
        Spacer(Modifier.height(24.dp))
        BigButton("Scan pairing code", Icons.Filled.QrCodeScanner, SideCarColors.Cobalt, onScan)
        if (error != null) { Spacer(Modifier.height(12.dp)); Text(error, color = SideCarColors.Alert) }
    }
}

@Composable
fun BigButton(text: String, icon: ImageVector, color: Color, onClick: () -> Unit) {
    Button(onClick = onClick, Modifier.fillMaxWidth().height(64.dp), shape = RoundedCornerShape(6.dp),
        colors = ButtonDefaults.buttonColors(containerColor = color, contentColor = Color.White)) {
        Icon(icon, null, Modifier.size(26.dp)); Spacer(Modifier.width(12.dp)); Text(text, fontSize = 18.sp)
    }
}

// ---- Outbox: what's waiting to reach the kiosk -------------------------------------------------

/** "2 waiting to send" with Retry now; a message over an hour old asks Send / Discard; refused items show why. */
@Composable
fun OutboxBanner(items: List<app.sidecarmonitor.data.Outbox.Item>, onRetry: () -> Unit, onApprove: (String) -> Unit, onDiscard: (String) -> Unit) {
    if (items.isEmpty()) return
    val waiting = items.filter { it.status == "waiting" || it.status == "approved" }
    Column(Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        if (waiting.isNotEmpty()) Row(verticalAlignment = Alignment.CenterVertically) {
            Text("\u23F1 ${waiting.size} waiting to send", Modifier.weight(1f), fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
            Text("Retry now", Modifier.background(SideCarColors.Cobalt).clickable(onClick = onRetry).padding(horizontal = 14.dp, vertical = 10.dp), fontSize = 14.sp)
        }
        if (waiting.isNotEmpty()) Text(waiting.take(3).joinToString(" \u00B7 ") { it.label } + if (waiting.size > 3) " \u2026" else "",
            fontSize = 13.sp, color = SideCarColors.Muted, maxLines = 2)
        items.filter { it.status == "confirm" || it.status == "failed" }.forEach { item ->
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(if (item.status == "confirm") "${item.label} \u00B7 written over an hour ago" else "Couldn't send ${item.label}: ${item.error}",
                    Modifier.weight(1f), fontSize = 13.sp, color = if (item.status == "failed") SideCarColors.Alert else SideCarColors.Amber, maxLines = 2)
                if (item.status == "confirm") Text("Send", Modifier.background(SideCarColors.Cobalt).clickable { onApprove(item.id) }.padding(horizontal = 12.dp, vertical = 8.dp), fontSize = 13.sp)
                Text("Discard", Modifier.background(SideCarColors.Steel).clickable { onDiscard(item.id) }.padding(horizontal = 12.dp, vertical = 8.dp), fontSize = 13.sp)
            }
        }
    }
}

// ---- Floating mic + assistant panel -----------------------------------------------------------

@Composable
fun MicFab(onClick: () -> Unit, modifier: Modifier = Modifier) {
    FloatingActionButton(onClick = onClick, modifier = modifier.size(72.dp), shape = CircleShape,
        containerColor = SideCarColors.Cobalt, contentColor = Color.White) {
        Icon(Icons.AutoMirrored.Filled.Chat, "Chat with SideCar", Modifier.size(34.dp))
    }
}

@Composable
fun AssistantPanel(
    state: UiState,
    onTalk: () -> Unit,
    onSend: (String) -> Unit,
    onStop: () -> Unit,
    onClose: () -> Unit,
) {
    var input by remember { mutableStateOf("") }
    val listState = rememberLazyListState()
    LaunchedEffect(state.lines.size, state.lines.lastOrNull()?.text) {
        if (state.lines.isNotEmpty()) listState.animateScrollToItem(state.lines.size - 1)
    }
    Column(Modifier.fillMaxSize().background(SideCarColors.Background).safeDrawingPadding().imePadding().padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Filled.Mic, null, Modifier.size(28.dp))
            Spacer(Modifier.width(8.dp))
            Text(state.assistantNote, Modifier.weight(1f), fontSize = 20.sp, fontWeight = FontWeight.SemiBold, maxLines = 2)
            IconButton(onClick = onClose, Modifier.size(56.dp)) { Icon(Icons.Filled.Close, "Close", Modifier.size(30.dp)) }
        }
        AnimatedVisibility(state.assistant == AssistantState.Listening) {
            LinearProgressIndicator(progress = { state.micLevel }, Modifier.fillMaxWidth().height(6.dp))
        }
        LazyColumn(Modifier.weight(1f), state = listState, verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(state.lines) { line ->
                Box(Modifier.fillMaxWidth(), contentAlignment = if (line.fromUser) Alignment.CenterEnd else Alignment.CenterStart) {
                    Text(line.text, Modifier.clip(RoundedCornerShape(10.dp))
                        .background(if (line.fromUser) SideCarColors.Cobalt else SideCarColors.PanelBorder).padding(12.dp), fontSize = 17.sp)
                }
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(value = input, onValueChange = { input = it }, Modifier.weight(1f),
                placeholder = { Text("Or type here") }, singleLine = true)
            IconButton(onClick = { onSend(input); input = "" }, Modifier.size(56.dp)) {
                Icon(Icons.AutoMirrored.Filled.Send, "Send", Modifier.size(28.dp))
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(onClick = onTalk, Modifier.weight(2f).height(64.dp), shape = RoundedCornerShape(6.dp),
                colors = ButtonDefaults.buttonColors(containerColor = SideCarColors.Cobalt, contentColor = Color.White)) {
                Icon(Icons.Filled.Mic, null, Modifier.size(28.dp)); Spacer(Modifier.width(8.dp))
                Text(if (state.assistant == AssistantState.Listening) "Done" else "Talk", fontSize = 20.sp)
            }
            Button(onClick = onStop, Modifier.weight(1f).height(64.dp), shape = RoundedCornerShape(6.dp),
                enabled = state.assistant != AssistantState.Idle,
                colors = ButtonDefaults.buttonColors(containerColor = SideCarColors.Panel, contentColor = Color.White)) {
                Icon(Icons.Filled.Stop, null, Modifier.size(28.dp)); Spacer(Modifier.width(6.dp)); Text("Stop", fontSize = 18.sp)
            }
        }
    }
}
