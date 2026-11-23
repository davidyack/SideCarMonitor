package app.sidecarmonitor.watch

import androidx.concurrent.futures.SuspendToFutureAdapter
import androidx.wear.protolayout.ActionBuilders
import androidx.wear.protolayout.ColorBuilders.argb
import androidx.wear.protolayout.DimensionBuilders.dp
import androidx.wear.protolayout.DimensionBuilders.expand
import androidx.wear.protolayout.DimensionBuilders.sp
import androidx.wear.protolayout.LayoutElementBuilders
import androidx.wear.protolayout.ModifiersBuilders
import androidx.wear.protolayout.ResourceBuilders
import androidx.wear.protolayout.TimelineBuilders
import androidx.wear.tiles.RequestBuilders
import androidx.wear.tiles.TileBuilders
import androidx.wear.tiles.TileService
import com.google.common.util.concurrent.ListenableFuture
import kotlinx.coroutines.withTimeoutOrNull

/**
 * SideCar tile: outdoor temperature, today's to-do count, the next task, and a big Talk button
 * that opens the app straight into listening. Data comes from the phone (which asks the kiosk).
 */
class SideCarTileService : TileService() {
    private val resourcesVersion = "1"

    override fun onTileRequest(requestParams: RequestBuilders.TileRequest): ListenableFuture<TileBuilders.Tile> =
        SuspendToFutureAdapter.launchFuture {
            val home = withTimeoutOrNull(8_000) { runCatching { PhoneLink(this@SideCarTileService).home() }.getOrNull() }
            TileBuilders.Tile.Builder()
                .setResourcesVersion(resourcesVersion)
                .setFreshnessIntervalMillis(15 * 60 * 1000L)
                .setTileTimeline(TimelineBuilders.Timeline.fromLayoutElement(layout(home)))
                .build()
        }

    override fun onTileResourcesRequest(requestParams: RequestBuilders.ResourcesRequest): ListenableFuture<ResourceBuilders.Resources> =
        SuspendToFutureAdapter.launchFuture { ResourceBuilders.Resources.Builder().setVersion(resourcesVersion).build() }

    private fun layout(home: WatchHome?): LayoutElementBuilders.LayoutElement {
        val talk = ModifiersBuilders.Clickable.Builder()
            .setId("talk")
            .setOnClick(
                ActionBuilders.LaunchAction.Builder()
                    .setAndroidActivity(
                        ActionBuilders.AndroidActivity.Builder()
                            .setPackageName(packageName)
                            .setClassName(MainActivity::class.java.name)
                            .addKeyToExtraMapping(MainActivity.EXTRA_TALK, ActionBuilders.booleanExtra(true))
                            .build())
                    .build())
            .build()

        val needs = home?.needsMeCount ?: 0
        val column = LayoutElementBuilders.Column.Builder()
            .setHorizontalAlignment(LayoutElementBuilders.HORIZONTAL_ALIGN_CENTER)
            .addContent(text(home?.temp ?: "--", 34f, 0xFFFFFFFF.toInt()))
            .addContent(text(home?.condition?.ifBlank { null } ?: if (home == null) "Open SideCar on your phone" else "", 13f, 0xFF8A96A3.toInt()))
            .addContent(spacer(6f))
            .addContent(text(
                if (home == null) "" else if (needs > 0) "$needs need you" else "${home.todayCount} to-do today",
                15f, if (needs > 0) 0xFFFF4D4F.toInt() else 0xFF3DD68C.toInt()))
            .addContent(text(home?.next?.take(28) ?: "", 12f, 0xFFFFFFFF.toInt()))
            .addContent(spacer(8f))
            .addContent(
                LayoutElementBuilders.Box.Builder()
                    .setWidth(dp(120f)).setHeight(dp(44f))
                    .setHorizontalAlignment(LayoutElementBuilders.HORIZONTAL_ALIGN_CENTER)
                    .setVerticalAlignment(LayoutElementBuilders.VERTICAL_ALIGN_CENTER)
                    .setModifiers(
                        ModifiersBuilders.Modifiers.Builder()
                            .setClickable(talk)
                            .setBackground(
                                ModifiersBuilders.Background.Builder()
                                    .setColor(argb(0xFF0050EF.toInt()))
                                    .setCorner(ModifiersBuilders.Corner.Builder().setRadius(dp(22f)).build())
                                    .build())
                            .setSemantics(ModifiersBuilders.Semantics.Builder().setContentDescription("Talk to SideCar").build())
                            .build())
                    .addContent(text("Talk", 16f, 0xFFFFFFFF.toInt()))
                    .build())
            .build()

        return LayoutElementBuilders.Box.Builder()
            .setWidth(expand()).setHeight(expand())
            .setHorizontalAlignment(LayoutElementBuilders.HORIZONTAL_ALIGN_CENTER)
            .setVerticalAlignment(LayoutElementBuilders.VERTICAL_ALIGN_CENTER)
            .addContent(column)
            .build()
    }

    private fun text(value: String, size: Float, color: Int): LayoutElementBuilders.LayoutElement =
        LayoutElementBuilders.Text.Builder()
            .setText(value)
            .setMaxLines(1)
            .setFontStyle(LayoutElementBuilders.FontStyle.Builder().setSize(sp(size)).setColor(argb(color)).build())
            .build()

    private fun spacer(height: Float): LayoutElementBuilders.LayoutElement =
        LayoutElementBuilders.Spacer.Builder().setHeight(dp(height)).build()
}
