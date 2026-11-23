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
    LaunchedEffect(Unit) { while (true) { reload(); delay(if (info?.running == true) 5_000 else 60_000) } }

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
