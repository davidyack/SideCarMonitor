package app.sidecarmonitor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.CheckBox
import androidx.compose.material.icons.filled.CheckBoxOutlineBlank
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.DragIndicator
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInRoot
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.zIndex
import app.sidecarmonitor.data.TaskRow
import app.sidecarmonitor.data.WeekPlan
import kotlinx.coroutines.delay

/** Where a dragged task can be dropped: a day (yyyy-MM-dd) or "" for "No day yet". */
private data class DropZone(val key: String, val accepts: Boolean)

/**
 * Weekly plan: press and hold a task, drag it to another day, let go. Dropping sets a flexible target
 * date on that day (a manual reschedule). Hard deadlines are locked; past days don't take drops.
 */
@Composable
fun WeekScreen(
    week: WeekPlan?,
    message: String?,
    onMove: (taskId: String, date: String?) -> Unit,
    onToggleDone: (TaskRow) -> Unit,
    onOpen: (String) -> Unit,
    onWeek: (Int) -> Unit,
    onBack: () -> Unit,
) {
    val haptics = LocalHapticFeedback.current
    val scroll = rememberScrollState()
    val zones = remember { mutableStateMapOf<DropZone, Rect>() }
    var dragging by remember { mutableStateOf<TaskRow?>(null) }
    var dragOrigin by remember { mutableStateOf(Offset.Zero) }   // row's top-left in root when drag started
    var dragDelta by remember { mutableStateOf(Offset.Zero) }
    var touchInRow by remember { mutableStateOf(Offset.Zero) }
    val pointer = dragOrigin + touchInRow + dragDelta
    val hovered = dragging?.let { zones.entries.firstOrNull { it.value.contains(pointer) }?.key }

    // Scroll while dragging near the top or bottom edge.
    var viewport by remember { mutableStateOf(Rect.Zero) }
    LaunchedEffect(dragging) {
        while (dragging != null) {
            val edge = 160f
            val step = when {
                pointer.y < viewport.top + edge -> -24f
                pointer.y > viewport.bottom - edge -> 24f
                else -> 0f
            }
            if (step != 0f) { scroll.scrollBy(step); dragOrigin = dragOrigin.copy(y = dragOrigin.y - step) }
            delay(16)
        }
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack, modifier = Modifier.size(56.dp)) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") }
            Column(Modifier.weight(1f)) {
                Text("Week", fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
                Text(week?.weekLabel ?: "", color = SideCarColors.Muted, fontSize = 14.sp)
            }
            IconButton(onClick = { onWeek(-1) }, modifier = Modifier.size(56.dp)) { Icon(Icons.Filled.ChevronLeft, "Previous week", Modifier.size(32.dp)) }
            IconButton(onClick = { onWeek(1) }, modifier = Modifier.size(56.dp)) { Icon(Icons.Filled.ChevronRight, "Next week", Modifier.size(32.dp)) }
        }
        Text("Press and hold a task, then drag it to a day.", Modifier.padding(horizontal = 16.dp), color = SideCarColors.Muted, fontSize = 13.sp)
        message?.let { Text(it, Modifier.padding(horizontal = 16.dp), color = SideCarColors.Alert, fontSize = 14.sp) }

        Box(Modifier.weight(1f).onGloballyPositioned { viewport = it.boundsInRoot() }) {
            Column(Modifier.verticalScroll(scroll, enabled = dragging == null).padding(12.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                if (week == null) Text("Loading…", color = SideCarColors.Muted)
                week?.earlier?.takeIf { it.isNotEmpty() }?.let { earlier ->
                    Section("Earlier (overdue)", null, DropZone("earlier", false), hovered, zones, highlight = SideCarColors.Crimson) {
                        earlier.forEach { TaskLine(it, dragging, onToggleDone, onOpen, onDragStart = { row, o, t -> dragging = row; dragOrigin = o; touchInRow = t; dragDelta = Offset.Zero; haptics.performHapticFeedback(HapticFeedbackType.LongPress) },
                            onDrag = { dragDelta += it }, onDragEnd = { hovered?.takeIf { z -> z.accepts }?.let { z -> dragging?.let { r -> onMove(r.id, z.key.ifEmpty { null }) } }; dragging = null }) }
                    }
                }
                week?.days?.forEach { day ->
                    val zone = DropZone(day.date, !day.isPast)
                    Section(day.label, day.shortLabel, zone, hovered, zones,
                        highlight = if (day.isToday) SideCarColors.Emerald else null) {
                        if (day.tasks.isEmpty()) Text(if (day.isPast) "" else "Drop a task here", color = SideCarColors.Muted, fontSize = 14.sp,
                            modifier = Modifier.padding(vertical = 8.dp))
                        day.tasks.forEach { TaskLine(it, dragging, onToggleDone, onOpen,
                            onDragStart = { row, o, t -> dragging = row; dragOrigin = o; touchInRow = t; dragDelta = Offset.Zero; haptics.performHapticFeedback(HapticFeedbackType.LongPress) },
                            onDrag = { dragDelta += it },
                            onDragEnd = { hovered?.takeIf { z -> z.accepts && z.key != dragging?.date }?.let { z -> dragging?.let { r -> onMove(r.id, z.key.ifEmpty { null }) } }; dragging = null }) }
                    }
                }
                week?.let { w ->
                    Section("No day yet", null, DropZone("", true), hovered, zones, highlight = null) {
                        if (w.noDay.isEmpty()) Text("Drop here to clear a task's day", color = SideCarColors.Muted, fontSize = 14.sp, modifier = Modifier.padding(vertical = 8.dp))
                        w.noDay.forEach { TaskLine(it, dragging, onToggleDone, onOpen,
                            onDragStart = { row, o, t -> dragging = row; dragOrigin = o; touchInRow = t; dragDelta = Offset.Zero; haptics.performHapticFeedback(HapticFeedbackType.LongPress) },
                            onDrag = { dragDelta += it },
                            onDragEnd = { hovered?.takeIf { z -> z.accepts && z.key.isNotEmpty() }?.let { z -> dragging?.let { r -> onMove(r.id, z.key) } }; dragging = null }) }
                    }
                }
                Spacer(Modifier.height(96.dp))
            }

            // The task being dragged floats under your finger.
            dragging?.let { row ->
                val top = pointer.y - viewport.top - touchInRow.y
                Box(Modifier.zIndex(10f).fillMaxWidth().padding(horizontal = 12.dp)
                    .graphicsLayer { translationY = top; shadowElevation = 24f; alpha = 0.95f }) {
                    Row(Modifier.fillMaxWidth().heightIn(min = 56.dp).clip(RoundedCornerShape(6.dp)).background(SideCarColors.Cobalt).padding(14.dp),
                        verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Filled.DragIndicator, null, tint = Color.White)
                        Spacer(Modifier.width(8.dp))
                        Text(row.title, fontSize = 17.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
            }
        }
    }
}

@Composable
private fun Section(
    title: String,
    subtitle: String?,
    zone: DropZone,
    hovered: DropZone?,
    zones: MutableMap<DropZone, Rect>,
    highlight: Color?,
    content: @Composable () -> Unit,
) {
    val isTarget = hovered == zone
    val borderColor = when {
        isTarget && zone.accepts -> SideCarColors.Ok
        isTarget -> SideCarColors.Alert
        highlight != null -> highlight
        else -> SideCarColors.PanelBorder
    }
    Column(
        Modifier.fillMaxWidth()
            .onGloballyPositioned { zones[zone] = it.boundsInRoot() }
            .clip(RoundedCornerShape(8.dp))
            .background(if (isTarget && zone.accepts) SideCarColors.Panel.copy(alpha = 0.6f) else SideCarColors.Panel)
            .border(if (isTarget) 3.dp else 1.dp, borderColor, RoundedCornerShape(8.dp))
            .padding(10.dp),
    ) {
        Row(verticalAlignment = Alignment.Bottom) {
            Text(title, fontSize = 19.sp, fontWeight = FontWeight.SemiBold, color = highlight ?: Color.White)
            subtitle?.let { Spacer(Modifier.width(8.dp)); Text(it, fontSize = 14.sp, color = SideCarColors.Muted) }
        }
        content()
    }
}

@Composable
private fun TaskLine(
    t: TaskRow,
    dragging: TaskRow?,
    onToggleDone: (TaskRow) -> Unit,
    onOpen: (String) -> Unit,
    onDragStart: (TaskRow, Offset, Offset) -> Unit,
    onDrag: (Offset) -> Unit,
    onDragEnd: () -> Unit,
) {
    var rowOrigin by remember { mutableStateOf(Offset.Zero) }
    val done = t.status == "Done"
    val locked = t.hard || done
    Row(
        Modifier.fillMaxWidth().heightIn(min = 56.dp)
            .onGloballyPositioned { rowOrigin = it.positionInRoot() }
            .graphicsLayer { alpha = if (dragging?.id == t.id) 0.3f else 1f }
            .then(if (locked) Modifier else Modifier.pointerInput(t.id) {
                detectDragGesturesAfterLongPress(
                    onDragStart = { touch -> onDragStart(t, rowOrigin, touch) },
                    onDrag = { change, amount -> change.consume(); onDrag(amount) },
                    onDragEnd = onDragEnd,
                    onDragCancel = onDragEnd,
                )
            })
            .clickable { onOpen(t.id) },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        IconButton(onClick = { onToggleDone(t) }, Modifier.size(48.dp)) {
            Icon(if (done) Icons.Filled.CheckBox else Icons.Filled.CheckBoxOutlineBlank, if (done) "Reopen" else "Mark done",
                tint = if (done) SideCarColors.Ok else Color.White, modifier = Modifier.size(28.dp))
        }
        Box(Modifier.width(4.dp).height(28.dp).background(when (t.priority) { "High" -> SideCarColors.Crimson; "Medium" -> SideCarColors.Amber; else -> SideCarColors.Steel }))
        Spacer(Modifier.width(10.dp))
        Text(t.title, Modifier.weight(1f), fontSize = 17.sp, maxLines = 2, overflow = TextOverflow.Ellipsis,
            color = if (done) SideCarColors.Muted else Color.White,
            textDecoration = if (done) TextDecoration.LineThrough else null)
        if (t.hard) Icon(Icons.Filled.Lock, "Hard deadline", tint = SideCarColors.Alert, modifier = Modifier.size(22.dp))
        else if (!done) Icon(Icons.Filled.DragIndicator, "Hold to drag", tint = SideCarColors.Muted, modifier = Modifier.size(22.dp))
    }
}
