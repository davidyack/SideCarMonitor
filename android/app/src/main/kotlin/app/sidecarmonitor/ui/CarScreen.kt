package app.sidecarmonitor.ui

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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.AppViewModel
import app.sidecarmonitor.car.CarAudio
import app.sidecarmonitor.car.CarMode
import kotlinx.coroutines.delay

/** Car mode: big targets, the screen stays on, and it listens for commands while open (see [CarMode]). */
@Composable
fun CarScreen(vm: AppViewModel, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    val context = LocalContext.current
    val view = LocalView.current
    val car = remember { CarMode(context) { vm.kiosk } }
    DisposableEffect(Unit) {
        view.keepScreenOn = true
        car.start()
        onDispose { view.keepScreenOn = false; car.stop() }
    }
    var title by remember { mutableStateOf<String?>(null) }
    var playing by remember { mutableStateOf(false) }
    var progress by remember { mutableStateOf(0f) }
    var time by remember { mutableStateOf("") }
    LaunchedEffect(Unit) {
        while (true) {
            val p = CarAudio.player
            title = CarAudio.currentTitle(); playing = p.isPlaying
            val d = p.duration.takeIf { it > 0 } ?: 0L
            progress = if (d > 0) (p.currentPosition.toFloat() / d).coerceIn(0f, 1f) else 0f
            time = if (d > 0) "${fmt(p.currentPosition)} / ${fmt(d)}" else ""
            delay(500)
        }
    }

    Column(Modifier.fillMaxSize().background(Color(0xFF07090D)).padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Car", fontSize = 30.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
            Box(Modifier.size(18.dp).clip(CircleShape).background(if (car.micOn) SideCarColors.Ok else SideCarColors.Steel))
            Spacer(Modifier.size(8.dp))
            Text(if (car.micOn) "Listening" else "…", fontSize = 16.sp, color = SideCarColors.Muted)
            Spacer(Modifier.size(16.dp))
            Text("Exit", Modifier.clip(RoundedCornerShape(8.dp)).background(SideCarColors.Panel).clickable(onClick = onBack)
                .padding(horizontal = 18.dp, vertical = 12.dp), fontSize = 18.sp)
        }

        // Now playing
        Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(SideCarColors.Panel).padding(18.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(title ?: "Say “brief me”", fontSize = 26.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (title != null) {
                LinearProgressIndicator(progress = { progress }, Modifier.fillMaxWidth().height(6.dp))
                Text(time, fontSize = 15.sp, color = SideCarColors.Muted)
            }
            Text(car.status, fontSize = 16.sp, color = Color(0xFF6CB4FF), maxLines = 3, overflow = TextOverflow.Ellipsis)
            if (car.heard.isNotBlank()) Text("Heard: “${car.heard}”", fontSize = 14.sp, color = SideCarColors.Muted, maxLines = 2, overflow = TextOverflow.Ellipsis)
        }

        // Big buttons
        @Composable fun Big(text: String, color: Color, modifier: Modifier, onClick: () -> Unit) =
            Box(modifier.clip(RoundedCornerShape(14.dp)).background(color).clickable(onClick = onClick), contentAlignment = Alignment.Center) {
                Text(text, fontSize = 24.sp, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center)
            }
        Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Big("Brief me", SideCarColors.Cobalt, Modifier.weight(1f).fillMaxSize()) { car.handle("brief me") }
            Big(if (playing) "⏸ Pause" else "▶ Play", SideCarColors.Emerald, Modifier.weight(1f).fillMaxSize()) { car.handle(if (playing) "pause" else "resume") }
        }
        Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Big("⏮ Back", SideCarColors.Steel, Modifier.weight(1f).fillMaxSize()) { car.handle("back") }
            Big("Skip ⏭", SideCarColors.Steel, Modifier.weight(1f).fillMaxSize()) { car.handle("skip") }
        }
        Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Big("🏷 Tag", SideCarColors.Amber, Modifier.weight(1f).fillMaxSize()) { car.handle("tag that") }
            Big("🎙 Ask", SideCarColors.Mauve, Modifier.weight(1f).fillMaxSize()) { car.askByButton() }
        }
        Text("Say: brief me · play LinkedIn · play X · pause · resume · skip · back · next briefing · tag that · “SideCar, …”",
            fontSize = 13.sp, color = SideCarColors.Muted)
    }
}

private fun fmt(ms: Long) = "${ms / 60000}:${"%02d".format(ms / 1000 % 60)}"
