package app.sidecarmonitor.ui

import android.content.Intent
import android.net.Uri
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.AppViewModel
import app.sidecarmonitor.data.LiPerson
import app.sidecarmonitor.data.LiPost
import app.sidecarmonitor.data.LinkedInInfo
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private val LinkedInBlue = Color(0xFF0A66C2)

/**
 * LinkedIn, as read and sorted by the kiosk: Signal and Noise tabs, Noise / Not noise on each post (that's how
 * it learns), Profile (their posts over time + AI summary) and Open (LinkedIn app or browser). Run now asks the
 * kiosk to read the feed again.
 */
@Composable
fun LinkedInScreen(vm: AppViewModel, onBack: () -> Unit) {
    var info by remember { mutableStateOf<LinkedInInfo?>(null) }
    var tab by remember { mutableStateOf("signal") }
    var note by remember { mutableStateOf("Loading…") }
    var person by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    suspend fun reload() {
        val i = vm.linkedIn()
        info = i
        note = when {
            i == null -> "Can't reach the kiosk"
            !i.available -> "LinkedIn isn't set up on the kiosk"
            i.needsAttention -> "LinkedIn needs a sign-in on the kiosk"
            i.running -> "Reading your feed on the kiosk…"
            else -> i.lastRunResult.ifBlank { "Not read yet: tap Run now" }
        }
    }
    LaunchedEffect(Unit) { while (true) { reload(); delay(if (info?.running == true || info?.podcast?.status == "preparing") 5_000 else 60_000) } }

    person?.let { url ->
        BackHandler { person = null }
        PersonView(vm, url) { person = null }
        return
    }
    BackHandler(onBack = onBack)

    Column(Modifier.fillMaxSize()) {
        PageHeader("LinkedIn", onBack)
        Row(Modifier.padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            val i = info
            TabChip("Signal ${i?.signal?.size ?: 0}", tab == "signal") { tab = "signal" }
            TabChip("Noise ${i?.noise?.size ?: 0}", tab == "noise") { tab = "noise" }
            Spacer(Modifier.weight(1f))
            TabChip("Run now", false, LinkedInBlue) { scope.launch { vm.linkedInRun(); delay(1500); reload() } }
        }
        Text(note, Modifier.padding(horizontal = 16.dp, vertical = 6.dp), color = SideCarColors.Muted, fontSize = 13.sp,
            maxLines = 1, overflow = TextOverflow.Ellipsis)
        val posts = if (tab == "signal") info?.signal.orEmpty() else info?.noise.orEmpty()
        LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            if (tab == "signal") info?.let { i -> item { PodcastCard(vm, i) { scope.launch { reload() } } } }
            val digest = info?.digest.orEmpty()
            if (tab == "signal" && digest.isNotBlank()) item {
                Column(Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp)) {
                    Text("Digest", fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
                    Text(digest, fontSize = 14.sp, color = Color(0xFF6CB4FF))
                }
            }
            items(posts, key = { it.id }) { p ->
                PostCard(p,
                    onLabel = { scope.launch { vm.linkedInLabel(p.id, !p.signal); reload() } },
                    onProfile = if (p.authorUrl.isNotBlank()) ({ person = p.authorUrl }) else null)
            }
            if (posts.isEmpty()) item { Text(if (tab == "signal") "Nothing here yet." else "No noise.", color = SideCarColors.Muted) }
            item { Spacer(Modifier.height(96.dp)) } // room for the mic button
        }
    }
}

@Composable
private fun TabChip(text: String, selected: Boolean, color: Color = SideCarColors.Cobalt, onClick: () -> Unit) =
    Text(text, Modifier.background(if (selected || color != SideCarColors.Cobalt) color else SideCarColors.Panel)
        .clickable(onClick = onClick).padding(horizontal = 14.dp, vertical = 10.dp), fontSize = 15.sp)

@Composable
private fun PostCard(p: LiPost, onLabel: (() -> Unit)?, onProfile: (() -> Unit)?, compact: Boolean = false) {
    val context = LocalContext.current
    Column(Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Text(listOfNotNull(p.author, p.age.takeIf { it.isNotBlank() }, "Promoted".takeIf { p.promoted }).joinToString("  ·  "),
            fontSize = 16.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
        if (!compact && p.headline.isNotBlank()) Text(p.headline, fontSize = 13.sp, color = SideCarColors.Muted, maxLines = 1, overflow = TextOverflow.Ellipsis)
        if (!compact && p.context.isNotBlank()) Text(p.context, fontSize = 13.sp, color = Color(0xFF6CB4FF), maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(p.text, fontSize = 14.sp, maxLines = if (compact) 3 else 6, overflow = TextOverflow.Ellipsis)
        Row(Modifier.padding(top = 6.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            onLabel?.let { ActionChip(if (p.signal) "Noise" else "Not noise", if (p.signal) SideCarColors.Alert else SideCarColors.Emerald, it) }
            onProfile?.let { ActionChip("Profile", SideCarColors.Mauve, it) }
            if (p.link.isNotBlank()) ActionChip("Open", SideCarColors.Steel) {
                runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(p.link))) }
            }
        }
    }
}

@Composable
private fun ActionChip(text: String, color: Color, onClick: () -> Unit) =
    Text(text, Modifier.background(color).clickable(onClick = onClick).padding(horizontal = 14.dp, vertical = 9.dp), fontSize = 14.sp)

@Composable
private fun PersonView(vm: AppViewModel, url: String, onBack: () -> Unit) {
    var p by remember { mutableStateOf<LiPerson?>(null) }
    var note by remember { mutableStateOf("Summarizing…") }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    LaunchedEffect(url) { p = vm.linkedInPerson(url); note = if (p == null) "Couldn't load this profile" else "" }

    Column(Modifier.fillMaxSize()) {
        PageHeader(p?.name ?: "Profile", onBack)
        LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            item {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    p?.headline?.takeIf { it.isNotBlank() }?.let { Text(it, fontSize = 14.sp, color = SideCarColors.Muted) }
                    Text(p?.summary?.takeIf { it.isNotBlank() } ?: note, fontSize = 15.sp, color = Color(0xFF6CB4FF))
                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        ActionChip("Refresh from LinkedIn", LinkedInBlue) {
                            note = "Reading their recent activity on the kiosk…"; p = p?.copy(summary = "")
                            scope.launch { vm.linkedInRefresh(url); p = vm.linkedInPerson(url) }
                        }
                        ActionChip("Open profile", SideCarColors.Steel) { runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) } }
                    }
                    Text("Their posts (${p?.posts?.size ?: 0})", Modifier.padding(top = 6.dp), fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                }
            }
            items(p?.posts.orEmpty(), key = { it.id }) { post -> PostCard(post, onLabel = null, onProfile = null, compact = true) }
            item { Spacer(Modifier.height(96.dp)) }
        }
    }
}


/**
 * Podcast: an audio rundown of your signal posts, made on the kiosk. Downloads the episode and plays it here with
 * play/pause, back 15 s, forward 30 s and a seek bar. "Done" clears the episode's posts from the feed (they stay on
 * profiles).
 */
@Composable
private fun PodcastCard(vm: AppViewModel, info: LinkedInInfo, onChanged: () -> Unit) {
    val p = info.podcast
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var player by remember { mutableStateOf<android.media.MediaPlayer?>(null) }
    var loadedFor by remember { mutableStateOf<String?>(null) }
    var playing by remember { mutableStateOf(false) }
    var position by remember { mutableStateOf(0) }
    var duration by remember { mutableStateOf(0) }
    var note by remember { mutableStateOf("") }

    androidx.compose.runtime.DisposableEffect(Unit) { onDispose { player?.release(); player = null } }
    LaunchedEffect(playing) { while (playing) { position = player?.currentPosition ?: 0; delay(500) } }

    suspend fun load(): android.media.MediaPlayer? {
        if (player != null && loadedFor == p.at) return player
        note = "Downloading the episode\u2026"
        val bytes = vm.linkedInPodcastAudio() ?: run { note = "Couldn't download the episode"; return null }
        val file = java.io.File(context.cacheDir, "linkedin-podcast.wav").apply { writeBytes(bytes) }
        player?.release()
        val mp = android.media.MediaPlayer().apply {
            setAudioAttributes(android.media.AudioAttributes.Builder().setUsage(android.media.AudioAttributes.USAGE_MEDIA)
                .setContentType(android.media.AudioAttributes.CONTENT_TYPE_SPEECH).build())
            setDataSource(file.absolutePath)
            prepare()
            setOnCompletionListener { playing = false; position = duration }
        }
        player = mp; loadedFor = p.at; duration = mp.duration; note = ""
        return mp
    }

    Column(Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("Podcast", fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
        val ready = p.status == "ready" && !p.cleared
        when {
            p.status == "preparing" -> Text("Preparing your episode on the kiosk\u2026", color = Color(0xFF6CB4FF), fontSize = 14.sp)
            p.status.startsWith("failed") -> Text("Couldn't make it: ${p.status.removePrefix("failed:").trim()}", color = SideCarColors.Alert, fontSize = 13.sp)
        }
        if (ready) {
            Text("${maxOf(1, (p.seconds / 60).toInt())} min \u00B7 ${p.posts} posts", color = SideCarColors.Muted, fontSize = 13.sp)
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                ActionChip("\u21BA 15", SideCarColors.Steel) { player?.let { it.seekTo(maxOf(0, it.currentPosition - 15_000)); position = it.currentPosition } }
                ActionChip(if (playing) "Pause" else "Play", LinkedInBlue) {
                    scope.launch {
                        val mp = load() ?: return@launch
                        if (playing) { mp.pause(); playing = false } else { mp.start(); playing = true }
                    }
                }
                ActionChip("30 \u21BB", SideCarColors.Steel) { player?.let { it.seekTo(minOf(it.duration, it.currentPosition + 30_000)); position = it.currentPosition } }
            }
            ActionChip(if (note.startsWith("Listening")) "Listening\u2026" else "\u270B Interrupt", SideCarColors.Amber) {
                if (note.startsWith("Listening")) return@ActionChip
                scope.launch {
                    val mp = load() ?: return@launch
                    val wasPlaying = playing
                    mp.pause(); playing = false
                    note = "Listening\u2026 say what you want to do (e.g. \u201Cfollow up with them on this\u201D)"
                    note = vm.podcastInterrupt(mp.currentPosition / 1000.0) { heard -> note = "\u201C$heard\u201D \u2026 saving" }
                    if (wasPlaying || mp.currentPosition > 0) { mp.start(); playing = true }
                }
            }
            if (duration > 0) {
                androidx.compose.material3.Slider(
                    value = position.toFloat(), valueRange = 0f..duration.toFloat(),
                    onValueChange = { position = it.toInt() }, onValueChangeFinished = { player?.seekTo(position) })
                Text("${position / 60000}:${"%02d".format(position / 1000 % 60)} / ${duration / 60000}:${"%02d".format(duration / 1000 % 60)}",
                    color = SideCarColors.Muted, fontSize = 12.sp)
            }
            Row(Modifier.clickable {
                    player?.pause(); playing = false
                    scope.launch { vm.linkedInPodcastClear(); onChanged() }
                }, verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                androidx.compose.material3.Checkbox(checked = false, onCheckedChange = {
                    player?.pause(); playing = false
                    scope.launch { vm.linkedInPodcastClear(); onChanged() }
                })
                Text("Done: clear these ${p.posts} posts from the feed", fontSize = 14.sp)
            }
        } else if (p.status == "ready" && p.cleared) Text("Last episode is done.", color = SideCarColors.Muted, fontSize = 13.sp)
        if (p.status != "preparing" && p.candidates > 0 && (!ready || p.candidates != p.posts))
            ActionChip(if (ready) "New episode (${p.candidates} posts)" else "Prepare episode (${p.candidates} posts)", LinkedInBlue) {
                scope.launch { vm.linkedInPodcastPrepare(); delay(1500); onChanged() }
            }
        if (note.isNotBlank()) Text(note, color = SideCarColors.Muted, fontSize = 13.sp)
    }
}
