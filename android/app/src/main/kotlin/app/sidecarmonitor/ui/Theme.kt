package app.sidecarmonitor.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/** Same palette as the kiosk (Windows Phone tile colors on a near-black background). */
object SideCarColors {
    val Background = Color(0xFF0B0D10)
    val Panel = Color(0xFF14181D)
    val PanelBorder = Color(0xFF232A31)
    val Muted = Color(0xFF8A96A3)
    val Alert = Color(0xFFFF4D4F)
    val Ok = Color(0xFF3DD68C)
    val Cobalt = Color(0xFF0050EF)
    val Crimson = Color(0xFFA20025)
    val Emerald = Color(0xFF008A00)
    val Amber = Color(0xFFD88C00)
    val Teal = Color(0xFF00ABA9)
    val Mauve = Color(0xFF76608A)
    val Steel = Color(0xFF647687)
}

@Composable
fun SideCarTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = SideCarColors.Cobalt,
            background = SideCarColors.Background,
            surface = SideCarColors.Panel,
            onPrimary = Color.White,
            onBackground = Color.White,
            onSurface = Color.White,
        ),
        content = content,
    )
}
