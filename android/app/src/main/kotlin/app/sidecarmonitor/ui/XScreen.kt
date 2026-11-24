package app.sidecarmonitor.ui

import android.content.Intent
import android.net.Uri
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.AppViewModel
import app.sidecarmonitor.data.LiPodcast
import app.sidecarmonitor.data.XInfo
import app.sidecarmonitor.data.XPost
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private val XInk = Color(0xFF2F3640)

/** X, read and sorted by the kiosk: Update, Signal / Noise / Saved, Actions per post, Play briefing. */
@Composable
fun XScreen(vm: AppViewModel, onBack: () -> Unit) {
    var info by remember { mutableStateOf<XInfo?>(null) }
    var tab by remember { mutableStateOf("signal") }
    var note by remember { mutableStateOf("Loading…") }
    var analysisFor by remember { mutableStateOf<XPost?>(null) }
    val scope = rememberCoroutineScope()

    suspend fun reload() {
        val i = runCatching { vm.kiosk?.x() }.getOrNull()
        info = i
        note = when {
            i == null -> "Can't reach the kiosk"
            !i.available -> "X isn't set up on the kiosk"
            i.busy -> "Working on the kiosk…"
            else -> i.lastResult.ifBlank { "Tap Update to pull up to 100 new posts (about \$0.50)" }
        }
    }
    LaunchedEffect(Unit) { while (true) { reload(); delay(if (info?.busy == true || info?.podcast?.status == "preparing") 4_000 else 60_000) } }

    analysisFor?.let { p ->
        BackHandler { analysisFor = null }
        AnalysisView(vm, p) { analysisFor = null; scope.launch { reload() } }
        return
    }
    BackHandler(onBack = onBack)

    Column(Modifier.fillMaxSize()) {
        PageHeader("X", onBack)
        info?.let { i ->
            BriefingBarFor(
                podcast = i.podcast,
                audio = { vm.kiosk?.xPodcastAudio() },
                prepare = { vm.kiosk?.xPodcastPrepare() },
                clear = { vm.kiosk?.xPodcastClear() },
                interrupt = { pos, heard -> vm.xInterrupt(pos, heard) },
                onChanged = { scope.launch { reload() } },
            )
        }
        Row(Modifier.padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            val i = info
            Chip("Signal ${i?.signal?.size ?: 0}", tab == "signal") { tab = "signal" }
            Chip("Noise ${i?.noise?.size ?: 0}", tab == "noise") { tab = "noise" }
            Chip("Saved ${i?.saved?.size ?: 0}", tab == "saved") { tab = "saved" }
            Chip("Sources", tab == "sources") { tab = "sources" }
            Spacer(Modifier.weight(1f))
            Chip("↻ Update", false, SideCarColors.Cobalt) { scope.launch { vm.kiosk?.xUpdate(); note = "Updating on the kiosk…"; delay(2000); reload() } }
        }
        Text(note, Modifier.padding(horizontal = 16.dp, vertical = 6.dp), color = SideCarColors.Muted, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
        info?.health?.takeIf { it.isNotBlank() }?.let {
            Text(it, Modifier.padding(start = 16.dp, end = 16.dp, bottom = 6.dp), color = Color(0xFF6CB4FF), fontSize = 13.sp)
        }
        if (tab == "sources") {
            LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                item { Text("Accounts that fill your timeline without signal. Unfollow asks you to confirm.", fontSize = 13.sp, color = SideCarColors.Muted) }
                items(info?.noisy.orEmpty(), key = { it.id }) { a -> NoisyRow(vm, a) { scope.launch { reload() } } }
                if (info?.noisy.isNullOrEmpty()) item { Text("No noisy accounts yet.", color = SideCarColors.Muted) }
                item { Spacer(Modifier.height(96.dp)) }
            }
            return@Column
        }
        val posts = when (tab) { "noise" -> info?.noise; "saved" -> info?.saved; else -> info?.signal }.orEmpty()
        LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(posts, key = { it.id }) { p -> XCard(vm, p, onAnalyze = { analysisFor = p }) { scope.launch { reload() } } }
            if (posts.isEmpty()) item { Text("Nothing here. Tap Update.", color = SideCarColors.Muted) }
            item { Spacer(Modifier.height(96.dp)) }
        }
    }
}

@Composable
private fun Chip(text: String, selected: Boolean, color: Color = SideCarColors.Cobalt, onClick: () -> Unit) =
    Text(text, Modifier.background(if (selected || color != SideCarColors.Cobalt) color else SideCarColors.Panel)
        .clickable(onClick = onClick).padding(horizontal = 12.dp, vertical = 10.dp), fontSize = 14.sp)

@Composable
private fun XCard(vm: AppViewModel, p: XPost, onAnalyze: () -> Unit, onChanged: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var menu by remember { mutableStateOf(false) }
    var confirmUnfollow by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf("") }
    fun open(url: String) = runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) }

    Column(Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text("${p.author}  @${p.username}" + if (p.repost) "  ·  repost" else "", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(p.text, fontSize = 14.sp, maxLines = 6, overflow = TextOverflow.Ellipsis)
        Text("${p.likes} likes · ${p.reposts} reposts · ${p.replies} replies", fontSize = 12.sp, color = SideCarColors.Muted)
        if (confirmUnfollow) Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("Unfollow @${p.username}?", fontSize = 15.sp)
            Text("Yes, unfollow", Modifier.background(SideCarColors.Alert).clickable {
                confirmUnfollow = false
                scope.launch { status = vm.kiosk?.xUnfollow(p.authorId)?.let { "Couldn't: $it" } ?: "Unfollowed"; onChanged() }
            }.padding(12.dp), fontSize = 14.sp)
            Text("Cancel", Modifier.background(SideCarColors.Steel).clickable { confirmUnfollow = false }.padding(12.dp), fontSize = 14.sp)
        } else Box {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("Actions ▾", Modifier.background(SideCarColors.Cobalt).clickable { menu = true }.padding(horizontal = 22.dp, vertical = 12.dp), fontSize = 16.sp)
                if (p.saved) Text("Saved ✓", color = Color(0xFF6CB4FF), fontSize = 13.sp)
                if (p.analysis.isNotBlank()) Text("Analyzed ✓", color = Color(0xFF6CB4FF), fontSize = 13.sp)
                if (status.isNotBlank()) Text(status, color = SideCarColors.Muted, fontSize = 13.sp)
            }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                @Composable fun Item(text: String, action: () -> Unit) =
                    DropdownMenuItem(text = { Text(text, fontSize = 18.sp, modifier = Modifier.padding(vertical = 6.dp)) }, onClick = { menu = false; action() })
                Item(if (p.signal) "Noise" else "Not noise") { scope.launch { vm.kiosk?.xLabel(p.id, !p.signal); onChanged() } }
                Item(if (p.analysis.isNotBlank()) "Analysis" else "Analyze (${p.replies} replies)", onAnalyze)
                Item("Ask Grok") {
                    open("https://x.com/i/grok?text=" + Uri.encode("Read this X post AND its replies: ${p.link} . Be brief, at most 6 bullets of under 20 words each: the gist; credibility; how the replies reacted and the main pushback; the best insight from the replies (with @handle); why it matters; one suggested action if any."))
                }
                Item(if (p.saved) "Saved ✓" else "Save") { if (!p.saved) scope.launch { status = vm.kiosk?.xSave(p.id)?.let { "Couldn't save: $it" } ?: "Saved"; onChanged() } }
                Item("Unfollow @${p.username}") { confirmUnfollow = true }
                Item("Open on X") { open(p.link) }
            }
        }
    }
}

/** One noisy account: how much of your timeline it takes, with Unfollow (confirm first). */
@Composable
private fun NoisyRow(vm: AppViewModel, a: app.sidecarmonitor.data.XAccountInfo, onChanged: () -> Unit) {
    val scope = rememberCoroutineScope()
    var confirm by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf("") }
    Row(Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Column(Modifier.weight(1f)) {
            Text("${a.name}  @${a.username}", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(status.ifBlank { "${a.noise} of ${a.seen} posts were noise" }, fontSize = 13.sp, color = if (status.isBlank()) SideCarColors.Alert else SideCarColors.Muted)
        }
        if (confirm) {
            Text("Yes, unfollow", Modifier.background(SideCarColors.Alert).clickable {
                confirm = false
                scope.launch { status = vm.kiosk?.xUnfollow(a.id)?.let { "Couldn't: $it" } ?: "Unfollowed"; onChanged() }
            }.padding(12.dp), fontSize = 14.sp)
            Text("Cancel", Modifier.background(SideCarColors.Steel).clickable { confirm = false }.padding(12.dp), fontSize = 14.sp)
        } else if (status.isBlank()) Text("Unfollow", Modifier.background(SideCarColors.Steel).clickable { confirm = true }.padding(12.dp), fontSize = 14.sp)
    }
}

/** Post + replies analysis (about $0.25 when not done yet), as labeled lines. */
@Composable
private fun AnalysisView(vm: AppViewModel, p: XPost, onBack: () -> Unit) {
    var text by remember { mutableStateOf(p.analysis) }
    LaunchedEffect(p.id) {
        if (text.isBlank()) {
            text = "Reading the post and up to 50 replies (about \$0.25)…"
            text = runCatching { vm.kiosk?.xAnalyze(p.id) ?: "Not connected" }.getOrElse { "Couldn't analyze: ${it.message}" }
        }
    }
    Column(Modifier.fillMaxSize()) {
        PageHeader("Analysis: @${p.username}", onBack)
        LazyColumn(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            item { Text(p.text, fontSize = 13.sp, color = SideCarColors.Muted, maxLines = 3, overflow = TextOverflow.Ellipsis) }
            items(text.split('\n').filter { it.isNotBlank() }) { line ->
                val colon = line.indexOf(':')
                if (colon in 1..15) Column {
                    Text(line.substring(0, colon), fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF6CB4FF))
                    Text(line.substring(colon + 1).trim(), fontSize = 16.sp)
                } else Text(line, fontSize = 16.sp)
            }
            item { Spacer(Modifier.height(96.dp)) }
        }
    }
}

/**
 * Briefing bar for any feed: one big Play briefing (prepares a fresh one when there's news); while playing
 * Pause / Tag / Done and a seek bar. Tag saves a follow-up to-do linked to the post, records your note and resumes.
 */
@Composable
fun BriefingBarFor(
    podcast: LiPodcast,
    audio: suspend () -> ByteArray?,
    prepare: suspend () -> Unit,
    clear: suspend () -> Unit,
    interrupt: suspend (Double, (String) -> Unit) -> String,
    onChanged: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var player by remember { mutableStateOf<android.media.MediaPlayer?>(null) }
    var loadedFor by remember { mutableStateOf<String?>(null) }
    var playing by remember { mutableStateOf(false) }
    var position by remember { mutableStateOf(0) }
    var duration by remember { mutableStateOf(0) }
    var note by remember { mutableStateOf("") }
    var listening by remember { mutableStateOf(false) }
    var waiting by remember { mutableStateOf(false) }
    DisposableEffect(Unit) { onDispose { player?.release(); player = null } }
    val memoryKey = "x:${podcast.at}"
    LaunchedEffect(playing) {
        var n = 0
        while (playing) { position = player?.currentPosition ?: 0; if (++n % 6 == 0) app.sidecarmonitor.data.PlaybackMemory.save(context, memoryKey, position); delay(500) }
    }
    val ready = podcast.status == "ready" && !podcast.cleared

    suspend fun load(): android.media.MediaPlayer? {
        if (player != null && loadedFor == podcast.at) return player
        note = "Downloading the briefing…"
        val bytes = runCatching { audio() }.getOrNull() ?: run { note = "Couldn't download the briefing"; return null }
        val file = java.io.File(context.cacheDir, "briefing-${podcast.at.hashCode()}.wav").apply { writeBytes(bytes) }
        player?.release()
        val mp = android.media.MediaPlayer().apply {
            setAudioAttributes(android.media.AudioAttributes.Builder().setUsage(android.media.AudioAttributes.USAGE_MEDIA)
                .setContentType(android.media.AudioAttributes.CONTENT_TYPE_SPEECH).build())
            setDataSource(file.absolutePath); prepare(); setOnCompletionListener { playing = false; app.sidecarmonitor.data.PlaybackMemory.forget(context, memoryKey) }
        }
        player = mp; loadedFor = podcast.at; duration = mp.duration; note = ""
        val from = app.sidecarmonitor.data.PlaybackMemory.resume(context, memoryKey, mp)
        if (from > 0) { position = from; note = "Picking up where you left off (${app.sidecarmonitor.data.PlaybackMemory.format(from)})" }
        return mp
    }
    LaunchedEffect(podcast.status, podcast.at) { if (waiting && ready) { waiting = false; load()?.let { it.start(); playing = true } } }

    @Composable
    fun Big(text: String, color: Color, modifier: Modifier = Modifier, onClick: () -> Unit) =
        Text(text, modifier.background(color).clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 14.dp), fontSize = 17.sp, fontWeight = FontWeight.SemiBold, maxLines = 1)

    Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        val active = player != null && loadedFor == podcast.at && ready
        if (!active) {
            when {
                podcast.status == "preparing" || waiting -> Big("Preparing briefing…", SideCarColors.Panel, Modifier.fillMaxWidth()) {}
                podcast.status.startsWith("failed") -> Big("Retry briefing", SideCarColors.Alert, Modifier.fillMaxWidth()) { scope.launch { waiting = true; prepare(); delay(1500); onChanged() } }
                else -> Big("▶  Play briefing", SideCarColors.Cobalt, Modifier.fillMaxWidth()) {
                    scope.launch {
                        // An episode you were partway through is finished before a new one is made.
                        val midway = context.getSharedPreferences("playback", android.content.Context.MODE_PRIVATE).contains(memoryKey)
                        if (ready && (!podcast.hasNews || midway)) load()?.let { it.start(); playing = true }
                        else if (podcast.candidates > 0) { waiting = true; prepare(); delay(1500); onChanged() }
                        else note = "Nothing new to brief"
                    }
                }
            }
        } else {
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Big(if (playing) "⏸ Pause" else "▶ Play", SideCarColors.Cobalt, Modifier.weight(1f)) {
                    player?.let { if (playing) { it.pause(); playing = false; app.sidecarmonitor.data.PlaybackMemory.save(context, memoryKey, it.currentPosition) } else { it.start(); playing = true } }
                }
                Big(if (listening) "Listening…" else "🏷 Tag", SideCarColors.Amber, Modifier.weight(1.2f)) {
                    if (listening) return@Big
                    scope.launch {
                        val mp = player ?: return@launch
                        mp.pause(); playing = false; listening = true
                        note = "Tagged — add a note, or stay quiet"
                        note = interrupt(mp.currentPosition / 1000.0) { heard -> note = "“$heard” … saving" }
                        listening = false; mp.start(); playing = true
                    }
                }
                Big("✓ Done", SideCarColors.Emerald, Modifier.weight(1f)) {
                    player?.pause(); playing = false; player?.release(); player = null; app.sidecarmonitor.data.PlaybackMemory.forget(context, memoryKey)
                    scope.launch { clear(); note = "Cleared from the feed"; onChanged() }
                }
            }
            if (duration > 0) Row(verticalAlignment = Alignment.CenterVertically) {
                androidx.compose.material3.Slider(value = position.toFloat(), valueRange = 0f..duration.toFloat(), modifier = Modifier.weight(1f),
                    onValueChange = { position = it.toInt() }, onValueChangeFinished = { player?.seekTo(position) })
                Text("  ${position / 60000}:${"%02d".format(position / 1000 % 60)} / ${duration / 60000}:${"%02d".format(duration / 1000 % 60)}", color = SideCarColors.Muted, fontSize = 12.sp)
            }
        }
        if (note.isNotBlank()) Text(note, color = Color(0xFF6CB4FF), fontSize = 13.sp)
    }
}
