package app.sidecarmonitor.watch

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Stop
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.wear.compose.foundation.lazy.ScalingLazyColumn
import androidx.wear.compose.foundation.lazy.rememberScalingLazyListState
import androidx.wear.compose.material3.AppScaffold
import androidx.wear.compose.material3.Button
import androidx.wear.compose.material3.ButtonDefaults
import androidx.wear.compose.material3.CircularProgressIndicator
import androidx.wear.compose.material3.Icon
import androidx.wear.compose.material3.ListHeader
import androidx.wear.compose.material3.MaterialTheme
import androidx.wear.compose.material3.ScreenScaffold
import androidx.wear.compose.material3.Text
import androidx.wear.compose.material3.TimeText
import androidx.wear.compose.navigation.SwipeDismissableNavHost
import androidx.wear.compose.navigation.composable
import androidx.wear.compose.navigation.rememberSwipeDismissableNavController

/** Same tile colors as the kiosk and phone. */
private object Tiles {
    val Cobalt = Color(0xFF0050EF)
    val Emerald = Color(0xFF008A00)
    val Amber = Color(0xFFD88C00)
    val Crimson = Color(0xFFA20025)
    val Mauve = Color(0xFF76608A)
    val Muted = Color(0xFF8A96A3)
}

class MainActivity : ComponentActivity() {
    private val vm: WatchViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val startTalking = intent?.getBooleanExtra(EXTRA_TALK, false) == true
        setContent {
            MaterialTheme {
                val ui by vm.ui.collectAsStateWithLifecycle()
                val nav = rememberSwipeDismissableNavController()
                val mic = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok -> if (ok) vm.talk() }
                fun talk() {
                    if (nav.currentDestination?.route != "talk") nav.navigate("talk")
                    if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) vm.talk()
                    else mic.launch(Manifest.permission.RECORD_AUDIO)
                }
                LaunchedEffect(Unit) { if (startTalking) talk() }

                AppScaffold(timeText = { TimeText() }) {
                    SwipeDismissableNavHost(navController = nav, startDestination = "home") {
                        composable("home") { HomeScreen(ui, onTalk = ::talk, onToday = { nav.navigate("today") }, onRefresh = vm::refresh) }
                        composable("talk") { TalkScreen(ui, onTalk = ::talk, onStop = vm::stopTalk) }
                        composable("today") { TodayScreen(ui, onDone = vm::markDone) }
                    }
                }
            }
        }
    }

    override fun onResume() { super.onResume(); vm.refresh() }

    companion object { const val EXTRA_TALK = "talk" }
}

@Composable
private fun HomeScreen(ui: WatchUi, onTalk: () -> Unit, onToday: () -> Unit, onRefresh: () -> Unit) {
    val list = rememberScalingLazyListState()
    val h = ui.home
    ScreenScaffold(scrollState = list) {
        ScalingLazyColumn(state = list, modifier = Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            item { ListHeader { Text("SideCar") } }
            item {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(h?.temp ?: "--", fontSize = 40.sp, fontWeight = FontWeight.Light)
                    Text(listOfNotNull(h?.condition?.takeIf { it.isNotBlank() }, h?.wind?.takeIf { it.isNotBlank() }).joinToString(" · "),
                        fontSize = 13.sp, color = Tiles.Muted, textAlign = TextAlign.Center)
                }
            }
            item {
                Button(onClick = onTalk, modifier = Modifier.fillMaxWidth(),
                    colors = ButtonDefaults.buttonColors(containerColor = Tiles.Cobalt, contentColor = Color.White, iconColor = Color.White),
                    icon = { Icon(Icons.Filled.Mic, null) }, label = { Text("Talk to SideCar") })
            }
            item {
                val needs = h?.needsMeCount ?: 0
                Button(onClick = onToday, modifier = Modifier.fillMaxWidth(),
                    colors = ButtonDefaults.buttonColors(containerColor = if (needs > 0) Tiles.Crimson else Tiles.Emerald, contentColor = Color.White, iconColor = Color.White),
                    icon = { Icon(Icons.Filled.CheckCircle, null) },
                    label = { Text("To-do · ${h?.todayCount ?: 0} today") },
                    secondaryLabel = { Text(if (needs > 0) "$needs need you" else h?.next?.ifBlank { "Nothing for today" } ?: "", maxLines = 1) })
            }
            if (!h?.activity.isNullOrBlank()) item { Text("Last: ${h?.activity}", fontSize = 12.sp, color = Tiles.Muted, textAlign = TextAlign.Center) }
            when {
                ui.loading && h == null -> item { CircularProgressIndicator(Modifier.size(32.dp)) }
                ui.error != null -> {
                    item { Text(ui.error, fontSize = 12.sp, color = Tiles.Amber, textAlign = TextAlign.Center) }
                    item {
                        Button(onClick = onRefresh, icon = { Icon(Icons.Filled.Refresh, null) }, label = { Text("Retry") })
                    }
                }
            }
        }
    }
}

@Composable
private fun TalkScreen(ui: WatchUi, onTalk: () -> Unit, onStop: () -> Unit) {
    val list = rememberScalingLazyListState()
    ScreenScaffold(scrollState = list) {
        ScalingLazyColumn(state = list, modifier = Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            item { ListHeader { Text(ui.talkNote, textAlign = TextAlign.Center) } }
            if (ui.talk == TalkState.Thinking || ui.talk == TalkState.Speaking) item { CircularProgressIndicator(Modifier.size(28.dp)) }
            if (ui.talk == TalkState.Listening) item {
                Row(horizontalArrangement = Arrangement.Center) {
                    Text("●".repeat(1 + (ui.level * 6).toInt()), color = Tiles.Cobalt, fontSize = 18.sp)
                }
            }
            if (ui.heard.isNotBlank()) item {
                Text(ui.heard, Modifier.padding(horizontal = 8.dp), fontSize = 13.sp, color = Tiles.Muted, textAlign = TextAlign.Center)
            }
            if (ui.reply.isNotBlank()) item {
                Text(ui.reply, Modifier.padding(horizontal = 8.dp), fontSize = 15.sp, textAlign = TextAlign.Center)
            }
            item {
                Button(onClick = onTalk, modifier = Modifier.fillMaxWidth(),
                    colors = ButtonDefaults.buttonColors(containerColor = Tiles.Cobalt, contentColor = Color.White, iconColor = Color.White),
                    icon = { Icon(Icons.Filled.Mic, null) },
                    label = { Text(if (ui.talk == TalkState.Listening) "Done" else "Talk") })
            }
            if (ui.talk != TalkState.Idle) item {
                Button(onClick = onStop, icon = { Icon(Icons.Filled.Stop, null) }, label = { Text("Stop") })
            }
        }
    }
}

@Composable
private fun TodayScreen(ui: WatchUi, onDone: (String) -> Unit) {
    val list = rememberScalingLazyListState()
    val tasks = ui.home?.today.orEmpty()
    ScreenScaffold(scrollState = list) {
        ScalingLazyColumn(state = list, modifier = Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            item { ListHeader { Text("Today") } }
            if (tasks.isEmpty()) item { Text("Nothing for today", color = Tiles.Muted) }
            tasks.forEach { t ->
                item(key = t.id) {
                    Button(onClick = { onDone(t.id) }, modifier = Modifier.fillMaxWidth(),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = if (t.needsMe || (t.hard && t.overdue)) Tiles.Crimson else Tiles.Mauve, contentColor = Color.White, iconColor = Color.White),
                        icon = { Icon(Icons.Filled.CheckCircle, "Mark done") },
                        label = { Text(t.title, maxLines = 2) },
                        secondaryLabel = {
                            Text(when { t.needsMe -> "Needs you"; t.hard && t.overdue -> "Overdue deadline"; t.hard -> "Deadline"; else -> "Tap to mark done" },
                                maxLines = 1)
                        })
                }
            }
        }
    }
}
