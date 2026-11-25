package app.sidecarmonitor.ui

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
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
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.AppViewModel
import app.sidecarmonitor.data.IdeasInfo
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** The Ideas tab (LinkedIn and X): PMCollab opportunities and wild ideas from your feeds, best first. */
@Composable
fun IdeasList(vm: AppViewModel) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var filter by remember { mutableStateOf("all") }
    var info by remember { mutableStateOf<IdeasInfo?>(null) }
    suspend fun reload() { info = runCatching { vm.kiosk?.ideas(filter) }.getOrNull() }
    LaunchedEffect(filter) { while (true) { reload(); delay(if (info?.busy == true) 15_000 else 120_000) } }

    LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        item {
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                listOf("all" to "All", "pmcollab" to "PMCollab", "wild" to "Wild ideas").forEach { (k, label) ->
                    Text(label, Modifier.background(if (filter == k) SideCarColors.Cobalt else SideCarColors.Panel).clickable { filter = k }
                        .padding(horizontal = 12.dp, vertical = 9.dp), fontSize = 14.sp)
                }
            }
        }
        item {
            Text(when {
                info == null -> "Loading…"
                info?.available != true -> "The idea radar isn't set up on the kiosk"
                info?.busy == true -> "Reading your feeds for ideas…"
                else -> info?.lastResult.orEmpty()
            }, fontSize = 13.sp, color = SideCarColors.Muted)
        }
        itemsIndexed(info?.ideas.orEmpty(), key = { _, i -> i.id }) { index, idea ->
            Column(Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text("#${index + 1}  ${idea.kindLabel}  ·  score ${idea.score}" + (if (idea.effort.isNotBlank()) "  ·  effort ${idea.effort}" else "") +
                    "  ·  ${idea.sources} source(s)" + if (idea.status == "todo") "  ·  on your to-do list" else "",
                    fontSize = 12.sp, color = if (idea.pmcollab) Color(0xFF6CB4FF) else SideCarColors.Amber)
                Text(idea.title, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
                Text(idea.pitch, fontSize = 14.sp)
                @Composable fun Line(label: String, text: String) {
                    if (text.isBlank()) return
                    Text(buildAnnotatedString { withStyle(SpanStyle(color = SideCarColors.Muted)) { append("$label: ") }; append(text) }, fontSize = 13.sp)
                }
                Line(if (idea.pmcollab) "The pain" else "Why now", idea.why)
                Line("Who'd pay", idea.whoPays)
                Line("Smallest version", idea.smallest)
                idea.evidence.take(2).forEach { e ->
                    Text("“${e.quote}” — ${e.author} (${if (e.source == "x") "X" else "LinkedIn"})", fontSize = 12.sp, color = SideCarColors.Muted, maxLines = 2)
                }
                Row(Modifier.padding(top = 6.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    if (idea.status != "todo") Chip("Make to-do", SideCarColors.Emerald) { scope.launch { vm.kiosk?.ideaAction(idea.id, "todo"); reload() } }
                    Chip("Not useful", SideCarColors.Steel) { scope.launch { vm.kiosk?.ideaAction(idea.id, "dismiss"); reload() } }
                    idea.evidence.firstOrNull { it.link.isNotBlank() }?.let { e ->
                        Chip("Source", SideCarColors.Steel) { runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(e.link))) } }
                    }
                }
            }
        }
        item { Spacer(Modifier.height(96.dp)) }
    }
}

@Composable
private fun Chip(text: String, color: Color, onClick: () -> Unit) =
    Text(text, Modifier.background(color).clickable(onClick = onClick).padding(horizontal = 14.dp, vertical = 10.dp), fontSize = 14.sp)
