package app.sidecarmonitor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.AppViewModel
import app.sidecarmonitor.UiState
import app.sidecarmonitor.data.ForecastInfo
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive

private val RainBlue = Color(0xFF6CB4FF)
private val CardShape = RoundedCornerShape(12.dp)

/**
 * Weather, laid out like the kiosk's: now (your Tempest), the next 24 hours as a strip, 10 days with a low-high bar on
 * one shared scale, and the station details as a grid.
 */
@Composable
fun WeatherScreen(state: UiState, vm: AppViewModel, onBack: () -> Unit) {
    val w = state.status?.weather
    fun s(key: String): String? = (w as? JsonObject)?.get(key)?.jsonPrimitive?.content?.takeIf { it.isNotBlank() && it != "null" }
    var f by remember { mutableStateOf<ForecastInfo?>(null) }
    LaunchedEffect(Unit) { f = runCatching { vm.kiosk?.forecast() }.getOrNull() }
    val today = f?.daily?.firstOrNull()

    Column(Modifier.fillMaxSize()) {
        PageHeader("Weather", onBack)
        Column(Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            // Now
            Row(Modifier.fillMaxWidth().clip(CardShape).background(SideCarColors.Panel).padding(18.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(s("temperature") ?: "--", fontSize = 64.sp, fontWeight = FontWeight.Light)
                    Text(s("condition") ?: today?.conditions.orEmpty(), fontSize = 18.sp)
                    Text(listOfNotNull(s("feelsLike")?.let { "Feels like $it" }, today?.let { "H ${it.high}  L ${it.low}" }).joinToString("  ·  "),
                        fontSize = 14.sp, color = SideCarColors.Muted)
                }
                Text(f?.hourly?.firstOrNull()?.icon ?: "", fontSize = 64.sp)
            }
            Text("Your Tempest · reading at ${s("observedAt") ?: "--"}" + (f?.updated?.let { " · forecast $it" } ?: ""),
                fontSize = 12.sp, color = SideCarColors.Muted, modifier = Modifier.padding(start = 4.dp))

            f?.error?.let { Text(it, color = SideCarColors.Muted, fontSize = 14.sp) }

            // Coming up
            if (!f?.highlights.isNullOrEmpty()) Section("Coming up") {
                f!!.highlights.forEach { h ->
                    Row(verticalAlignment = Alignment.Top) {
                        Text("•", color = SideCarColors.Amber, fontSize = 15.sp, modifier = Modifier.width(16.dp))
                        Text(h, fontSize = 15.sp)
                    }
                }
            }

            // Next 24 hours
            if (!f?.hourly.isNullOrEmpty()) Section("Next 24 hours") {
                Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    f!!.hourly.forEach { h ->
                        Column(Modifier.width(58.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(h.time, fontSize = 12.sp, color = SideCarColors.Muted)
                            Text(h.icon, fontSize = 24.sp)
                            Text(h.temp, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                            Text(h.rain.ifBlank { " " }, fontSize = 12.sp, color = RainBlue)
                        }
                    }
                }
            }

            // 10 days, one shared temperature scale
            if (!f?.daily.isNullOrEmpty()) Section("10 days") {
                val days = f!!.daily
                val lo = days.mapNotNull { it.lowC }.minOrNull() ?: 0.0
                val hi = days.mapNotNull { it.highC }.maxOrNull() ?: 1.0
                val span = (hi - lo).coerceAtLeast(1.0)
                days.forEach { d ->
                    Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(d.day, Modifier.width(56.dp), fontSize = 16.sp, fontWeight = if (d.day == "Today") FontWeight.SemiBold else null)
                        Text(d.icon, Modifier.width(36.dp), fontSize = 22.sp)
                        Text(d.rain, Modifier.width(44.dp), fontSize = 13.sp, color = RainBlue)
                        Text(d.low, Modifier.width(40.dp), fontSize = 15.sp, color = SideCarColors.Muted, textAlign = TextAlign.End)
                        // Low-high bar on the week's scale.
                        Box(Modifier.weight(1f).padding(horizontal = 10.dp).height(6.dp).clip(RoundedCornerShape(3.dp)).background(Color(0xFF2A2F3A))) {
                            val start = (((d.lowC ?: lo) - lo) / span).toFloat().coerceIn(0f, 1f)
                            val end = (((d.highC ?: hi) - lo) / span).toFloat().coerceIn(start, 1f)
                            Row(Modifier.fillMaxWidth()) {
                                if (start > 0f) Spacer(Modifier.weight(start))
                                Box(Modifier.weight((end - start).coerceAtLeast(0.04f)).height(6.dp).clip(RoundedCornerShape(3.dp))
                                    .background(Brush.horizontalGradient(listOf(RainBlue, SideCarColors.Amber))))
                                if (end < 1f) Spacer(Modifier.weight(1f - end))
                            }
                        }
                        Text(d.high, Modifier.width(40.dp), fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                    }
                }
                today?.let { t ->
                    if (t.sunrise != null || t.sunset != null)
                        Text("Sunrise ${t.sunrise ?: "--"}  ·  Sunset ${t.sunset ?: "--"}", fontSize = 13.sp, color = SideCarColors.Muted,
                            modifier = Modifier.padding(top = 4.dp))
                }
            }

            // Station details
            Section("At your station") {
                val cells = listOf(
                    Triple("Wind", s("wind"), s("windGust")?.let { "Gusts $it" }),
                    Triple("Humidity", s("humidity"), s("dewPoint")?.let { "Dew point $it" }),
                    // "23.81 inHg station (falling)": the reading big, the rest underneath.
                    s("pressure")?.split(' ', limit = 3).let { parts ->
                        Triple("Pressure", parts?.take(2)?.joinToString(" "), parts?.getOrNull(2))
                    },
                    Triple("Rain today", s("rainToday"), s("precipitationNow")?.let { "Now: $it" }),
                    Triple("Lightning today", s("lightningStrikesToday"), s("lastLightning")),
                    Triple("Feels like", s("feelsLike"), null),
                )
                cells.chunked(2).forEach { row ->
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        row.forEach { (title, value, detail) ->
                            Column(Modifier.weight(1f).clip(RoundedCornerShape(10.dp)).background(Color(0xFF1C2028)).padding(12.dp)) {
                                Text(title, fontSize = 12.sp, color = SideCarColors.Muted)
                                Text(value ?: "--", fontSize = 20.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                Text(detail ?: " ", fontSize = 12.sp, color = SideCarColors.Muted, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                        }
                    }
                    Spacer(Modifier.height(8.dp))
                }
            }
            Spacer(Modifier.height(96.dp))
        }
    }
}

@Composable
private fun Section(title: String, content: @Composable () -> Unit) {
    Column(Modifier.fillMaxWidth().clip(CardShape).background(SideCarColors.Panel).padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(title.uppercase(), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = SideCarColors.Muted, letterSpacing = 1.sp)
        content()
    }
}
