package app.sidecarmonitor.ui

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.AppViewModel
import app.sidecarmonitor.data.MailDigest
import app.sidecarmonitor.data.MailInfo
import app.sidecarmonitor.data.MailItem
import app.sidecarmonitor.data.PlaybackMemory
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * Mail: one digest per hourly triage run (CTC → "#  SideCar"). Each digest reads as text, plays as audio, and is archived
 * as a whole set. Tap a line to open its message (a merged line lists its messages).
 */
@Composable
fun MailScreen(vm: AppViewModel, onBack: () -> Unit) {
    androidx.activity.compose.BackHandler(onBack = onBack)
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var info by remember { mutableStateOf<MailInfo?>(null) }
    var note by remember { mutableStateOf("") }
    var undoId by remember { mutableStateOf<String?>(null) }
    var undoAll by remember { mutableStateOf<List<String>?>(null) }
    val opened = remember { mutableStateListOf<String>() }
    val archived = remember { mutableStateListOf<String>() }
    var player by remember { mutableStateOf<android.media.MediaPlayer?>(null) }
    var playingId by remember { mutableStateOf<String?>(null) }
    var playing by remember { mutableStateOf(false) }
    var position by remember { mutableStateOf(0) }
    var loadingId by remember { mutableStateOf<String?>(null) }

    suspend fun reload() { runCatching { vm.kiosk?.mail() }.getOrNull()?.let { info = it } }
    LaunchedEffect(Unit) {
        while (true) {
            reload()
            delay(if (info?.busy == true || info?.digests?.any { it.status == "writing" } == true) 10_000 else 60_000)
        }
    }
    LaunchedEffect(playingId) { while (playingId != null) { player?.let { position = it.currentPosition; playing = it.isPlaying }; delay(500) } }

    fun stop() {
        player?.let { p -> playingId?.let { PlaybackMemory.save(context, "mail:$it", p.currentPosition) }; runCatching { p.release() } }
        player = null; playingId = null; playing = false
    }
    DisposableEffect(Unit) { onDispose { stop() } }

    fun play(d: MailDigest) {
        if (playingId == d.id) { player?.let { if (it.isPlaying) it.pause() else it.start() }; return }
        stop()
        loadingId = d.id
        scope.launch {
            val bytes = runCatching { vm.kiosk?.mailDigestAudio(d.id) }.getOrNull()
            loadingId = null
            if (bytes == null) { note = "Couldn't download the digest audio"; return@launch }
            val file = java.io.File(context.cacheDir, "mail-digest.wav").apply { writeBytes(bytes) }
            val mp = android.media.MediaPlayer().apply {
                setDataSource(file.absolutePath); prepare()
                setOnCompletionListener { PlaybackMemory.forget(context, "mail:${d.id}"); stop() }
            }
            val at = PlaybackMemory.resume(context, "mail:${d.id}", mp)
            if (at > 0) note = "Picking up where you left off (${PlaybackMemory.format(at)})"
            mp.start()
            player = mp; playingId = d.id; playing = true
        }
    }

    Column(Modifier.fillMaxSize()) {
        PageHeader("Mail", onBack)
        val i = info
        LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            item {
                Text(
                    when {
                        i == null -> "Loading…"
                        !i.available -> "Mail isn't set up on the kiosk."
                        else -> (if (i.trial) "TRIAL: nothing is moved yet · " else "") + (if (i.busy) "Sorting new mail…" else i.lastResult) + " · hourly · " + i.catchUp
                    }, fontSize = 13.sp, color = SideCarColors.Muted)
                Row(Modifier.padding(top = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    val openCount = i?.digests.orEmpty().count { it.id !in archived }
                    if (openCount > 1) Chip("Archive all ($openCount)", SideCarColors.Emerald) {
                        stop()
                        val ids = i?.digests.orEmpty().map { it.id }.filter { it !in archived }
                        archived.addAll(ids); undoAll = ids; undoId = null; note = "Archived ${ids.size} digests."
                        scope.launch { vm.kiosk?.mailArchiveAll() }
                    }
                    Chip(if (i?.busy == true) "Sorting…" else "Run now", SideCarColors.PanelBorder) {
                        scope.launch { vm.kiosk?.mailRun(); note = "Checking for new mail…"; delay(3_000); reload() }
                    }
                }
                if (note.isNotEmpty()) Row(Modifier.padding(top = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(note, fontSize = 14.sp, color = Color(0xFF6CB4FF), modifier = Modifier.weight(1f))
                    undoAll?.let { ids ->
                        Chip("Undo", SideCarColors.PanelBorder) {
                            archived.removeAll(ids); undoAll = null; note = ""
                            scope.launch { vm.kiosk?.mailArchiveAll(ids); reload() }
                        }
                    }
                    undoId?.let { id ->
                        Chip("Undo", SideCarColors.PanelBorder) {
                            archived.remove(id); undoId = null; note = ""
                            scope.launch { vm.kiosk?.mailArchive(id, false); reload() }
                        }
                    }
                }
            }
            val digests = i?.digests.orEmpty().filter { it.id !in archived }
            if (i?.available == true && digests.isEmpty()) item {
                Text("All caught up: no digests waiting.", fontSize = 15.sp, color = SideCarColors.Muted, modifier = Modifier.padding(vertical = 12.dp))
            }
            items(digests, key = { it.id }) { d ->
                Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(SideCarColors.Panel).padding(14.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    val isOpen = d.id in opened || (opened.none { o -> digests.any { it.id == o } } && d.id == digests.firstOrNull()?.id)
                    Text((if (isOpen) "\u25BE " else "\u25B8 ") + (if (d.title.isNotEmpty()) "${d.title} · " else "") + "${time(d.at, "EEE h:mm a")} · ${d.sorted} sorted",
                        modifier = Modifier.clickable { if (isOpen) { opened.remove(d.id); if (opened.isEmpty()) opened.add("-") } else opened.add(d.id) },
                        fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        when {
                            d.status == "writing" -> Text("Writing the digest…", fontSize = 14.sp, color = SideCarColors.Muted)
                            loadingId == d.id -> Chip("Loading…", SideCarColors.Cobalt) {}
                            playingId == d.id -> Chip(if (playing) "⏸ Pause  ${PlaybackMemory.format(position)}" else "▶ Play  ${PlaybackMemory.format(position)}", SideCarColors.Cobalt) { play(d) }
                            d.audio -> Chip("▶ Play", SideCarColors.Cobalt) { play(d) }
                        }
                        Chip("Archive", SideCarColors.Emerald) {
                            if (playingId == d.id) stop()
                            archived.add(d.id); undoId = d.id; note = "Archived the ${time(d.at, "h:mm a")} digest."
                            scope.launch { vm.kiosk?.mailArchive(d.id, true) }
                        }
                    }
                    if (!isOpen) Text(d.counts, fontSize = 13.sp, color = SideCarColors.Muted)
                    if (isOpen) DigestSections(d) { link -> runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(link))) } }
                }
            }
            item { androidx.compose.foundation.layout.Spacer(Modifier.padding(40.dp)) }
        }
    }
}

private val sectionHead = Regex("^(Kept for you|Dev|Lists|Newsletters|Updates|Invites|Orders(?: & Receipts| and receipts)?|Notifications|Sales|Suspicious)\\s*[.:]\\s*", RegexOption.IGNORE_CASE)

private fun sectionColor(h: String) = when (h.lowercase()) {
    "kept for you" -> SideCarColors.Amber
    "dev" -> SideCarColors.Cobalt
    "lists" -> SideCarColors.Teal
    "newsletters" -> SideCarColors.Mauve
    "invites" -> SideCarColors.Emerald
    "notifications" -> Color(0xFF0A66C2)
    "sales" -> SideCarColors.Crimson
    "suspicious" -> SideCarColors.Alert
    else -> if (h.lowercase().startsWith("orders")) SideCarColors.Emerald else if (h.lowercase().startsWith("newsletter")) SideCarColors.Mauve else SideCarColors.Steel
}

private val lineTag = Regex("^\\[([^\\]]*)\\]\\s*")

/** The digest as section cards: colored stripe and heading per folder, one tappable point per line (opens its message). */
@Composable
private fun DigestSections(d: MailDigest, open: (String) -> Unit) {
    var heading = "Kept for you"
    val byId = d.items.associateBy { it.id }
    d.text.split("\n\n").map { it.trim() }.filter { it.isNotEmpty() }.forEach { block ->
        val lines = block.split("\n").map { it.trim() }.filter { it.isNotEmpty() }.toMutableList()
        val nl = Regex("^Newsletter:\\s*(.+?)\\.?$", RegexOption.IGNORE_CASE).find(lines[0])
        val m = nl ?: sectionHead.find(lines[0])
        if (nl != null) { heading = "Newsletter \u00B7 " + nl.groupValues[1]; lines[0] = "" } // each newsletter its own card
        else if (m != null) { heading = m.groupValues[1]; lines[0] = lines[0].substring(m.value.length) }
        if (heading.equals("Kept for you", ignoreCase = true)) return@forEach // still in the Inbox
        val points = lines.filter { it.isNotEmpty() }.flatMap { line ->
            val tag = lineTag.find(line)
            if (tag != null) listOf(line.substring(tag.value.length) to Regex("M?(\\d+)").findAll(tag.groupValues[1])
                .mapNotNull { d.order.getOrNull(it.groupValues[1].toInt() - 1)?.let(byId::get) }.toList())
            else line.split(Regex("(?<=[.!?])\\s+(?=[A-Z0-9\"])")).filter { it.isNotBlank() }.map { it.trim() to emptyList<MailItem>() }
        }
        if (points.isEmpty()) return@forEach
        val color = sectionColor(heading)
        Row(Modifier.fillMaxWidth().height(androidx.compose.foundation.layout.IntrinsicSize.Min).clip(RoundedCornerShape(10.dp)).background(SideCarColors.Background)) {
            androidx.compose.foundation.layout.Box(Modifier.width(5.dp).fillMaxHeight().background(color))
            Column(Modifier.padding(start = 10.dp, top = 10.dp, end = 8.dp, bottom = 10.dp), verticalArrangement = Arrangement.spacedBy(7.dp)) {
                if (m != null) Text(heading.uppercase(), fontSize = 12.sp, fontWeight = FontWeight.Bold, color = color, letterSpacing = 1.sp)
                points.forEach { (text, items) ->
                    val links = items.filter { it.link.isNotEmpty() }
                    var pick by remember { mutableStateOf(false) }
                    androidx.compose.foundation.layout.Box {
                        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(6.dp))
                            .clickable(enabled = links.isNotEmpty()) { if (links.size == 1) open(links[0].link) else pick = true }
                            .padding(horizontal = 4.dp, vertical = 2.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text("\u2022", fontSize = 14.sp, color = color)
                            Text(text + if (links.size > 1) "  (${links.size})" else "", fontSize = 14.sp, lineHeight = 21.sp, color = Color(0xFFE6E9ED))
                        }
                        androidx.compose.material3.DropdownMenu(expanded = pick, onDismissRequest = { pick = false }) {
                            links.forEach { item ->
                                androidx.compose.material3.DropdownMenuItem(text = { Text("${item.from}: ${item.subject}", fontSize = 14.sp, maxLines = 2, overflow = TextOverflow.Ellipsis) },
                                    onClick = { pick = false; open(item.link) })
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun Chip(text: String, color: Color, onClick: () -> Unit) =
    Text(text, Modifier.clip(RoundedCornerShape(8.dp)).background(color).clickable(onClick = onClick).padding(horizontal = 14.dp, vertical = 10.dp),
        fontSize = 15.sp, color = Color.White)

private fun time(iso: String, pattern: String): String =
    runCatching { OffsetDateTime.parse(iso).atZoneSameInstant(ZoneId.systemDefault()).format(DateTimeFormatter.ofPattern(pattern)) }.getOrDefault("")
