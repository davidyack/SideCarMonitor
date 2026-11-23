package app.sidecarmonitor.watch

import androidx.wear.watchface.complications.data.ComplicationData
import androidx.wear.watchface.complications.data.ComplicationType
import androidx.wear.watchface.complications.data.PlainComplicationText
import androidx.wear.watchface.complications.data.ShortTextComplicationData
import androidx.wear.watchface.complications.datasource.ComplicationRequest
import androidx.wear.watchface.complications.datasource.SuspendingComplicationDataSourceService
import kotlinx.coroutines.withTimeoutOrNull

/** Watch-face complication: outdoor temperature from your Tempest station (via the phone and kiosk). */
class TemperatureComplicationService : SuspendingComplicationDataSourceService() {

    override fun getPreviewData(type: ComplicationType): ComplicationData? =
        if (type == ComplicationType.SHORT_TEXT) shortText("24°", "Out") else null

    override suspend fun onComplicationRequest(request: ComplicationRequest): ComplicationData? {
        if (request.complicationType != ComplicationType.SHORT_TEXT) return null
        val home = withTimeoutOrNull(8_000) { runCatching { PhoneLink(this@TemperatureComplicationService).home() }.getOrNull() }
        return shortText(home?.temp ?: "--", "Out")
    }

    private fun shortText(value: String, title: String) =
        ShortTextComplicationData.Builder(
            PlainComplicationText.Builder(value).build(),
            PlainComplicationText.Builder("Outdoor temperature $value").build(),
        ).setTitle(PlainComplicationText.Builder(title).build()).build()
}
