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
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
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
 * Weekly plan: press and hold a task and drag. While you drag, every day collapses to its header so you can
 * reach any day quickly; hold over a day for a moment and it opens so you can drop the task exactly where it
 * should go in that day's order (a blue line shows the spot). Dropping on a collapsed day puts it last.
 * Moving to another day sets a flexible target there (a manual reschedule). Hard deadlines can be reordered
 * within their day but don't move; past days don't take drops.
 */
@Composable
fun WeekScreen(
    week: WeekPlan?,
    message: String?,
    onOrder: (date: String?, ids: List<String>) -> Unit,
    onToggleDone: (TaskRow) -> Unit,
    onOpen: (String) -> Unit,
    onWeek: (Int) -> Unit,
    onBack: () -> Unit,
) {
    val haptics = LocalHapticFeedback.current
    val scroll = rememberScrollState()
    val zones = remember { mutableStateMapOf<DropZone, Rect>() }
    val rows = remember { mutableStateMapOf<String, Rect>() }      // each task row's bounds in root
    var dragging by remember { mutableStateOf<TaskRow?>(null) }
    var dragStartOrigin by remember { mutableStateOf(Offset.Zero) }
    var dragDelta by remember { mutableStateOf(Offset.Zero) }
    var touchInRow by remember { mutableStateOf(Offset.Zero) }
    var expanded by remember { mutableStateOf<String?>(null) }    // the day opened while dragging
    // The finger in root coordinates: the dragged row's current position + where you touched it + how far you moved.
    // (Using the row's live position keeps this right while days collapse and expand around it.)
    val pointer = (dragging?.let { rows[it.id]?.topLeft } ?: dragStartOrigin) + touchInRow + dragDelta
    val hovered = dragging?.let { zones.entries.firstOrNull { it.value.contains(pointer) }?.key }

    // Hold over a day for a moment: open it so you can pick the exact spot.
    LaunchedEffect(hovered, dragging) {
        if (dragging != null && hovered != null && hovered.accepts && hovered.key != expanded) {
            delay(350)
            expanded = hovered.key
            haptics.performHapticFeedback(HapticFeedbackType.TextHandleMove)
        }
    }

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
            if (step != 0f) scroll.scrollBy(step)
            delay(16)
        }
    }

    fun tasksOf(key: String): List<TaskRow> = when (key) {
        "" -> week?.noDay.orEmpty()
        "earlier" -> week?.earlier.orEmpty()
        else -> week?.days?.firstOrNull { it.date == key }?.tasks.orEmpty()
    }
    /** Where the dragged task would land in an open day: before the first row whose middle is below the finger. */
    fun insertIndex(key: String): Int {
        val others = tasksOf(key).filter { it.id != dragging?.id }
        if (expanded != key) return others.size
        return others.count { (rows[it.id]?.center?.y ?: Float.MAX_VALUE) < pointer.y }
    }

    val startDrag: (TaskRow, Offset, Offset) -> Unit = { row, origin, touch ->
        dragging = row; dragStartOrigin = origin; touchInRow = touch; dragDelta = Offset.Zero; expanded = null
        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
    }
    val endDrag: () -> Unit = {
        val r = dragging
        val z = hovered
        if (r != null && z != null && z.accepts) {
            val others = tasksOf(z.key).filter { it.id != r.id }.map { it.id }.toMutableList()
            others.add(insertIndex(z.key).coerceIn(0, others.size), r.id)
            val before = tasksOf(z.key).map { it.id }
            if (others != before) onOrder(z.key.ifEmpty { null }, others)
        }
        dragging = null; expanded = null
    }

    @Composable
    fun DayTasks(zone: DropZone, tasks: List<TaskRow>, emptyText: String) {
        val open = dragging == null || expanded == zone.key
        // Collapsed while dragging: just a count. Every row is drawn from this one loop either way, so the row
        // you're holding stays the same element as days collapse and open (otherwise the drag would be cancelled).
        if (!open) Text(if (tasks.isEmpty()) "Empty" else "${tasks.size} task${if (tasks.size == 1) "" else "s"}",
            fontSize = 13.sp, color = SideCarColors.Muted, modifier = Modifier.padding(top = 2.dp))
        else if (tasks.isEmpty()) Text(emptyText, color = SideCarColors.Muted, fontSize = 14.sp, modifier = Modifier.padding(vertical = 8.dp))
        val dropAt = if (open && dragging != null && hovered == zone && zone.accepts) insertIndex(zone.key) else -1
        var shown = 0
        tasks.forEach { t ->
            val held = t.id == dragging?.id
            if (!held && shown == dropAt) DropLine()
            key(t.id) {
                if (open || held) TaskLine(t, dragging, onToggleDone, onOpen, startDrag, { dragDelta += it }, endDrag) { id, b -> rows[id] = b }
            }
            if (!held) shown++
        }
        if (dropAt >= 0 && dropAt == shown) DropLine()
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
        Text(if (dragging == null) "Press and hold a task, then drag it to a day. Hold over a day to place it in order."
             else "Hold over a day to open it, then drop where it belongs.",
            Modifier.padding(horizontal = 16.dp), color = SideCarColors.Muted, fontSize = 13.sp)
        message?.let { Text(it, Modifier.padding(horizontal = 16.dp), color = SideCarColors.Alert, fontSize = 14.sp) }

        Box(Modifier.weight(1f).onGloballyPositioned { viewport = it.boundsInRoot() }) {
            Column(Modifier.verticalScroll(scroll, enabled = dragging == null).padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(if (dragging != null) 6.dp else 10.dp)) {
                if (week == null) Text("Loading\u2026", color = SideCarColors.Muted)
                // Today first, then overdue, the rest of the week, no day, and the days already past.
                val days = week?.days.orEmpty()
                val ahead = days.filter { !it.isPast }
                val past = days.filter { it.isPast }
                @Composable fun DaySection(day: app.sidecarmonitor.data.WeekDay) {
                    val zone = DropZone(day.date, !day.isPast)
                    Section(day.label, day.shortLabel, zone, hovered, zones, highlight = if (day.isToday) SideCarColors.Emerald else null) {
                        DayTasks(zone, day.tasks, if (day.isPast) "" else "Drop a task here")
                    }
                }
                ahead.firstOrNull { it.isToday }?.let { DaySection(it) }
                week?.earlier?.takeIf { it.isNotEmpty() }?.let { earlier ->
                    val zone = DropZone("earlier", false)
                    Section("Earlier (overdue)", null, zone, hovered, zones, highlight = SideCarColors.Crimson) { DayTasks(zone, earlier, "") }
                }
                ahead.filter { !it.isToday }.forEach { DaySection(it) }
                week?.let { w ->
                    val zone = DropZone("", true)
                    Section("No day yet", null, zone, hovered, zones, highlight = null) { DayTasks(zone, w.noDay, "Drop here to clear a task's day") }
                }
                if (past.isNotEmpty()) {
                    Text("Earlier this week", Modifier.padding(top = 8.dp), color = SideCarColors.Muted, fontSize = 14.sp)
                    past.forEach { DaySection(it) }
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

/** Where the dragged task will land. */
@Composable
private fun DropLine() =
    Box(Modifier.fillMaxWidth().padding(vertical = 2.dp).height(4.dp).clip(RoundedCornerShape(2.dp)).background(SideCarColors.Cobalt))

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
    onBounds: (String, Rect) -> Unit,
) {
    var rowOrigin by remember { mutableStateOf(Offset.Zero) }
    // The drag handler below is set up once per task; these always hand it the latest callbacks
    // (otherwise the drop would see the hover target from when the row was first drawn: none).
    val startNow by rememberUpdatedState(onDragStart)
    val dragNow by rememberUpdatedState(onDrag)
    val endNow by rememberUpdatedState(onDragEnd)
    val done = t.status == "Done"
    val locked = done // hard deadlines can still be reordered within their day (the kiosk refuses a day change)
    Row(
        Modifier.fillMaxWidth().heightIn(min = 56.dp)
            .onGloballyPositioned { rowOrigin = it.positionInRoot(); onBounds(t.id, it.boundsInRoot()) }
            .graphicsLayer { alpha = if (dragging?.id == t.id) 0.3f else 1f }
            .then(if (locked) Modifier else Modifier.pointerInput(t.id) {
                detectDragGesturesAfterLongPress(
                    onDragStart = { touch -> startNow(t, rowOrigin, touch) },
                    onDrag = { change, amount -> change.consume(); dragNow(amount) },
                    onDragEnd = { endNow() },
                    onDragCancel = { endNow() },
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
        if (t.hard) Icon(Icons.Filled.Lock, "Hard deadline (reorder only)", tint = SideCarColors.Alert, modifier = Modifier.size(22.dp))
        else if (!done) Icon(Icons.Filled.DragIndicator, "Hold to drag", tint = SideCarColors.Muted, modifier = Modifier.size(22.dp))
    }
}
