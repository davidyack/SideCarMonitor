package app.sidecarmonitor.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.AppViewModel
import app.sidecarmonitor.data.HomeDevice
import app.sidecarmonitor.data.HomeInfo
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * House: Home Assistant through the kiosk. Lights, switches and blinds by room as big toggles,
 * scripts as buttons, and the recent action log. Your taps are logged as yours; only SideCar's own
 * actions go through the decision model's odd-activity check.
 */
@Composable
fun HouseScreen(vm: AppViewModel, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    var info by remember { mutableStateOf<HomeInfo?>(null) }
    var note by remember { mutableStateOf("Loading…") }
    var busy by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    suspend fun reload() {
        val h = vm.home()
        info = h
        note = when {
            h == null -> "Can't reach the kiosk"
            !h.available -> "Home Assistant isn't set up on the kiosk"
            else -> "${h.lightsOn} light${if (h.lightsOn == 1) "" else "s"} on"
        }
    }
    LaunchedEffect(Unit) { while (true) { reload(); delay(30_000) } }

    fun act(key: String, call: suspend () -> String?) {
        if (busy != null) return
        busy = key
        scope.launch {
            val error = call()
            if (error != null) note = error
            delay(700) // let Home Assistant report the new state
            reload()
            busy = null
        }
    }

    Column(Modifier.fillMaxSize()) {
        PageHeader("House", onBack)
        Text(note, Modifier.padding(horizontal = 16.dp), color = SideCarColors.Muted, fontSize = 14.sp)
        val h = info
        LazyColumn(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            if (h?.paused != null) item {
                Row(Modifier.fillMaxWidth().padding(top = 8.dp).background(SideCarColors.Alert).padding(12.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    Text("SideCar's home control is paused: ${h.paused}", Modifier.weight(1f), fontSize = 15.sp)
                    Text("Resume", Modifier.background(Color.Black.copy(alpha = 0.25f)).clickable {
                        act("resume") { vm.homeResume() }
                    }.padding(horizontal = 14.dp, vertical = 10.dp), fontWeight = FontWeight.SemiBold)
                }
            }
            if (!h?.scripts.isNullOrEmpty()) {
                item { Section("Scripts") }
                // Two to a row, like the room tiles.
                items(h!!.scripts.chunked(2), key = { row -> "s-" + row.joinToString { it.tool } }) { row ->
                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        row.forEach { s ->
                            Text(s.name, Modifier.weight(1f).heightIn(min = 72.dp)
                                .background(if (busy == s.tool) SideCarColors.Teal.copy(alpha = 0.5f) else SideCarColors.Teal)
                                .clickable { act(s.tool) { vm.homeScript(s) } }.padding(14.dp),
                                fontSize = 17.sp, maxLines = 2, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                        }
                        if (row.size == 1) androidx.compose.foundation.layout.Spacer(Modifier.weight(1f))
                    }
                }
            }
            h?.devices.orEmpty().groupBy { it.area }.forEach { (room, devices) ->
                item(key = "r-$room") { Section(room) }
                items(devices.chunked(2), key = { row -> "d-" + row.joinToString { it.domain + it.name } }) { row ->
                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        row.forEach { d -> DeviceTile(d, Modifier.weight(1f), busy == d.domain + d.name) { act(d.domain + d.name) { vm.homeSet(d, !d.on) } } }
                        if (row.size == 1) androidx.compose.foundation.layout.Spacer(Modifier.weight(1f))
                    }
                }
            }
            if (!h?.recent.isNullOrEmpty()) {
                item { Section("Recent") }
                items(h!!.recent) { line ->
                    Text(line, fontSize = 13.sp,
                        color = if (line.contains("Paused") || line.contains("(blocked)")) SideCarColors.Alert else SideCarColors.Muted)
                }
            }
            item { androidx.compose.foundation.layout.Spacer(Modifier.height(96.dp)) } // room for the mic button
        }
    }
}

@Composable
private fun Section(title: String) =
    Text(title, Modifier.padding(top = 12.dp, bottom = 2.dp), fontSize = 18.sp, fontWeight = FontWeight.SemiBold)

/** Modifier for a full-width tappable block. */
@Composable
private fun Box(color: Color, busy: Boolean, onClick: () -> Unit): Modifier =
    Modifier.fillMaxWidth().background(if (busy) color.copy(alpha = 0.5f) else color).clickable(onClick = onClick).padding(16.dp)

@Composable
private fun DeviceTile(d: HomeDevice, modifier: Modifier, busy: Boolean, onClick: () -> Unit) {
    val state = if (d.domain == "cover") (if (d.on) "Open" else "Closed") else (if (d.on) "On" else "Off")
    val color = if (d.on) SideCarColors.Amber else SideCarColors.Panel
    Column(modifier.height(76.dp).background(if (busy) color.copy(alpha = 0.5f) else color).clickable(onClick = onClick).padding(12.dp),
        verticalArrangement = Arrangement.SpaceBetween) {
        Text(d.name, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
        Text(state, fontSize = 13.sp, color = Color.White.copy(alpha = 0.8f))
    }
}
