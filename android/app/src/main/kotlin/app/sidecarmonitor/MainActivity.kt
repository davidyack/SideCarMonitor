package app.sidecarmonitor

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.sidecarmonitor.ui.ActivityScreen
import app.sidecarmonitor.ui.AssistantPanel
import app.sidecarmonitor.ui.CamerasScreen
import app.sidecarmonitor.ui.HomeScreen
import app.sidecarmonitor.ui.MicFab
import app.sidecarmonitor.ui.PairScreen
import app.sidecarmonitor.ui.Screen
import app.sidecarmonitor.ui.SettingsScreen
import app.sidecarmonitor.ui.SideCarColors
import app.sidecarmonitor.ui.SideCarTheme
import app.sidecarmonitor.ui.WeatherScreen
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning

class MainActivity : ComponentActivity() {
    private val vm: AppViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            SideCarTheme {
              // Surface sets the default text/icon color (white) for everything inside.
              androidx.compose.material3.Surface(Modifier.fillMaxSize(), color = SideCarColors.Background, contentColor = androidx.compose.ui.graphics.Color.White) {
                val state by vm.state.collectAsStateWithLifecycle()
                var screen by rememberSaveable { mutableStateOf(Screen.Home) }
                var pairError by rememberSaveable { mutableStateOf<String?>(null) }

                val micPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
                    if (granted) vm.openAssistant(startListening = true) else vm.openAssistant(startListening = false)
                }
                fun talk() {
                    if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED)
                        vm.openAssistant(startListening = true)
                    else micPermission.launch(Manifest.permission.RECORD_AUDIO)
                }
                fun scan() {
                    // Google's code scanner: no camera permission needed in the app.
                    val options = GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()
                    GmsBarcodeScanning.getClient(this, options).startScan()
                        .addOnSuccessListener { code ->
                            pairError = if (vm.pair(code.rawValue ?: "")) null else "That isn't a SideCar pairing code."
                            if (pairError == null) screen = Screen.Home
                        }
                        .addOnFailureListener { pairError = "Scanner problem: ${it.message}" }
                }

                Box(Modifier.fillMaxSize().background(SideCarColors.Background)) {
                    if (!state.paired) {
                        Box(Modifier.safeDrawingPadding()) { PairScreen(pairError, ::scan) }
                        return@Box
                    }
                    BackHandler(enabled = state.assistantOpen || screen != Screen.Home) {
                        if (state.assistantOpen) vm.closeAssistant() else screen = Screen.Home
                    }
                    Box(Modifier.fillMaxSize().safeDrawingPadding()) {
                        when (screen) {
                            Screen.Home -> HomeScreen(state, onOpen = { screen = it }, onTalk = ::talk)
                            Screen.Cameras -> CamerasScreen(state, vm::snapshot) { screen = Screen.Home }
                            Screen.Weather -> WeatherScreen(state) { screen = Screen.Home }
                            Screen.Activity -> ActivityScreen(state) { screen = Screen.Home }
                            Screen.Settings -> SettingsScreen(state, onScan = ::scan, onUnpair = vm::unpair) { screen = Screen.Home }
                        }
                        if (!state.assistantOpen) MicFab(::talk, Modifier.align(Alignment.BottomEnd).padding(20.dp))
                    }
                    if (state.assistantOpen) {
                        AssistantPanel(state, onTalk = ::talk, onSend = vm::sendText, onStop = vm::stopTurn, onClose = vm::closeAssistant)
                    }
                }
              }
            }
        }
    }

    override fun onStart() { super.onStart(); vm.startPolling() }
    override fun onStop() { super.onStop(); vm.stopPolling() }
}
