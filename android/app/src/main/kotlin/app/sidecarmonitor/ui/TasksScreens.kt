package app.sidecarmonitor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import kotlinx.coroutines.launch
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.UiState
import app.sidecarmonitor.data.TaskRow

private fun priorityColor(p: String) = when (p) {
    "High" -> SideCarColors.Crimson
    "Medium" -> SideCarColors.Amber
    else -> SideCarColors.Steel
}

/** Today / Upcoming / Needs me / Done, with an add box. Mirrors the kiosk's To-do page. */
@Composable
fun TasksScreen(state: UiState, onOpen: (String) -> Unit, onAdd: (String, Boolean) -> Unit, onWeek: () -> Unit,
                onDismissBrief: () -> Unit = {}, onBack: () -> Unit) {
    var tab by rememberSaveable { mutableStateOf("today") }
    var newTitle by remember { mutableStateOf("") }
    val views = state.tasks?.views
    val rows = when (tab) {
        "upcoming" -> views?.upcoming
        "needsMe" -> views?.needsMe
        "done" -> views?.done
        else -> views?.today
    }.orEmpty()

    Column(Modifier.fillMaxSize().imePadding()) {
        PageHeader("To-do", onBack)
        Column(Modifier.padding(horizontal = 12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            TaskButton("Weekly plan (drag tasks between days)", SideCarColors.Cobalt, onWeek)
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                for ((key, label, count) in listOf(
                    Triple("today", "Today", views?.today?.size),
                    Triple("upcoming", "Upcoming", views?.upcoming?.size),
                    Triple("needsMe", "Needs me", views?.needsMe?.size),
                    Triple("done", "Done", null),
                )) {
                    val selected = tab == key
                    val urgent = key == "needsMe" && (count ?: 0) > 0
                    Box(
                        Modifier.weight(1f).height(52.dp).clip(RoundedCornerShape(6.dp))
                            .background(if (selected) SideCarColors.Emerald else if (urgent) SideCarColors.Crimson else SideCarColors.Panel)
                            .clickable { tab = key },
                        contentAlignment = Alignment.Center,
                    ) { Text(if (count != null) "$label $count" else label, fontSize = 14.sp, maxLines = 1) }
                }
            }
            state.tasks?.brief?.let { brief ->
                Column(Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(10.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("Today's brief", Modifier.weight(1f), color = SideCarColors.Amber, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
                        Text("Dismiss", Modifier.background(SideCarColors.Steel).clickable(onClick = onDismissBrief)
                            .padding(horizontal = 16.dp, vertical = 10.dp), fontSize = 14.sp)
                    }
                    Text(brief, fontSize = 14.sp)
                }
            }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(newTitle, { newTitle = it }, Modifier.weight(1f), placeholder = { Text("Add a task") }, singleLine = true)
                IconButton(
                    onClick = { onAdd(newTitle, tab == "today"); newTitle = "" },
                    modifier = Modifier.size(56.dp).clip(RoundedCornerShape(6.dp)).background(SideCarColors.Emerald),
                ) { Icon(Icons.Filled.Add, "Add task", Modifier.size(30.dp), tint = Color.White) }
            }
            state.taskMessage?.let { Text(it, color = SideCarColors.Alert, fontSize = 14.sp) }
        }
        if (rows.isEmpty()) {
            Text(when (tab) { "needsMe" -> "Nothing needs you."; "done" -> "Nothing finished yet."; "upcoming" -> "Nothing upcoming."; else -> "Nothing for today." },
                Modifier.padding(16.dp), color = SideCarColors.Muted)
        }
        LazyColumn(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            items(rows, key = { it.id }) { TaskRowView(it) { onOpen(it.id) } }
        }
    }
}

@Composable
private fun TaskRowView(t: TaskRow, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 64.dp).background(SideCarColors.Panel).clickable(onClick = onClick)) {
        Box(Modifier.width(6.dp).fillMaxHeight().heightIn(min = 64.dp).background(priorityColor(t.priority)))
        Column(Modifier.padding(12.dp)) {
            Text(t.title, fontSize = 18.sp, maxLines = 2, overflow = TextOverflow.Ellipsis,
                textDecoration = if (t.status == "Done") TextDecoration.LineThrough else null)
            val bits = buildList {
                t.dateLabel?.let { add((if (t.hard) "Due " else "") + it + if (t.overdue) " · overdue" else "") }
                if (t.pinned) add("On Today")
                t.draftVersion?.let { add("Draft v$it") }
                if (t.question) add("Question for you")
                if (t.revision) add("Revision to review")
                if (!t.help) add("Assistant off")
            }
            if (bits.isNotEmpty()) Text(bits.joinToString(" · "), fontSize = 13.sp,
                color = if (t.overdue && t.hard) SideCarColors.Alert else SideCarColors.Muted)
        }
    }
}

/** One task: decisions, question, deliverable (with Accept/Dismiss), switches, notes and history with Undo. */
@Composable
fun TaskDetailScreen(state: UiState, onAction: (String, Map<String, Any?>) -> Unit,
                     dictate: (suspend () -> String)? = null, stopDictate: () -> Unit = {}, onBack: () -> Unit) {
    val d = state.openTask ?: return
    val t = d.row
    var note by remember(t.id) { mutableStateOf("") }
    var answer by remember(t.id) { mutableStateOf("") }
    var editing by remember(t.id) { mutableStateOf(false) }
    var draft by remember(t.id, d.deliverable?.number) { mutableStateOf(d.deliverable?.content ?: "") }

    Column(Modifier.fillMaxSize().imePadding()) {
        PageHeader("Task", onBack)
        Column(Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 4.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(t.title, fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
            val meta = buildList {
                add("${t.priority} priority")
                t.dateLabel?.let { add((if (t.hard) "Hard deadline " else "Target ") + it + if (t.overdue) " (overdue)" else "") } ?: add("No date")
                if (t.status == "Done") add("Done")
            }
            Text(meta.joinToString(" · "), color = if (t.overdue) SideCarColors.Alert else SideCarColors.Muted, fontSize = 15.sp)
            d.link?.takeIf { it.startsWith("https://") || it.startsWith("http://") }?.let { link ->
                val uri = androidx.compose.ui.platform.LocalUriHandler.current
                TaskButton("↗ Open item" + (d.linkLabel?.let { " ($it)" } ?: ""), SideCarColors.Cobalt) {
                    runCatching { uri.openUri(link) }
                }
            }
            d.details?.let { Text(it, fontSize = 16.sp) }
            state.taskMessage?.let { Banner(it, SideCarColors.Steel) }

            if (d.needsDecision) {
                Banner(if (d.rolloverPaused) "Overdue, and automatic moves are paused. What should happen?"
                       else "Overdue, and it's already moved ${d.rolloverCount} times. What should happen?", SideCarColors.Crimson)
                Text("Pick a date by asking SideCar (\"move it to Friday\"), or:", color = SideCarColors.Muted, fontSize = 14.sp)
                ActionRow(listOf("Drop the date" to "drop_date", "Mark done" to "done"), onAction)
            } else if (d.rolloverPaused) {
                ActionRow(listOf("Resume automatic moves" to "resume"), onAction)
            }

            d.question?.let { q ->
                Banner("Assistant asks: $q", SideCarColors.Amber)
                OutlinedTextField(answer, { answer = it }, Modifier.fillMaxWidth(), placeholder = { Text("Your answer") })
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    dictate?.let { DictateButton(it, stopDictate) { said -> answer = (answer + " " + said).trim() } }
                    TaskButton("Send answer", SideCarColors.Cobalt) { onAction("note", mapOf("text" to answer)); answer = "" }
                }
            }

            SwitchRow("On Today", t.pinned) { onAction(if (it) "pin" else "unpin", emptyMap()) }
            SwitchRow("Let assistant help", t.help) { onAction(if (it) "help_on" else "help_off", emptyMap()) }

            ActionRow(listOf("Take a pass" to "take_pass", if (t.status == "Done") "Reopen" to "reopen" else "Mark done" to "done"), onAction)

            // Deliverable
            Text(d.deliverable?.let { "Deliverable v${it.number} · by ${if (it.author == "User") "you" else "the assistant"} · ${it.at}" }
                ?: "Deliverable (none yet)", color = SideCarColors.Muted, fontSize = 14.sp)
            if (editing) {
                OutlinedTextField(draft, { draft = it }, Modifier.fillMaxWidth().heightIn(min = 160.dp))
                ActionRow(listOf("Save my edit" to "deliverable", "Cancel" to "cancel")) { a, _ ->
                    if (a == "deliverable") onAction("deliverable", mapOf("content" to draft))
                    editing = false
                }
            } else {
                d.deliverable?.let {
                    Text(it.content, Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp), fontSize = 15.sp)
                }
                TaskButton(if (d.deliverable == null) "Write one myself" else "Edit", SideCarColors.Panel) { editing = true }
            }
            d.pendingRevision?.let { rev ->
                Banner("The assistant suggested a revision (yours is kept until you choose):", SideCarColors.Amber)
                Text(rev, Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(12.dp), fontSize = 15.sp)
                ActionRow(listOf("Accept" to "accept", "Dismiss" to "dismiss"), onAction)
            }

            // Notes and activity
            Text("Notes and activity", color = SideCarColors.Muted, fontSize = 14.sp)
            OutlinedTextField(note, { note = it }, Modifier.fillMaxWidth(), placeholder = { Text("Add a note") })
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                dictate?.let { DictateButton(it, stopDictate) { said -> note = (note + " " + said).trim() } }
                TaskButton("Add note", SideCarColors.Panel) { onAction("note", mapOf("text" to note)); note = "" }
            }
            for (h in d.history) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    val who = when (h.author) { "User" -> "You"; "Assistant" -> "Assistant"; else -> "SideCar" }
                    Text("${h.at} · $who: ${h.text}", Modifier.weight(1f), fontSize = 14.sp,
                        color = if (h.kind == "Activity") SideCarColors.Muted else Color.White)
                    if (h.canUndo) {
                        Spacer(Modifier.width(8.dp))
                        Button(onClick = { onAction("undo", mapOf("entryId" to h.id)) }, Modifier.height(48.dp),
                            colors = ButtonDefaults.buttonColors(containerColor = SideCarColors.PanelBorder, contentColor = Color.White)) { Text("Undo") }
                    }
                }
            }
            Spacer(Modifier.height(96.dp)) // room above the mic button
        }
    }
}

/** Speak instead of typing: records until you pause, transcribes on the kiosk, adds the words to the field. */
@Composable
private fun DictateButton(dictate: suspend () -> String, stop: () -> Unit, onText: (String) -> Unit) {
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    var listening by remember { mutableStateOf(false) }
    TaskButton(if (listening) "Listening\u2026 tap when done" else "\uD83C\uDF99 Speak", if (listening) SideCarColors.Alert else SideCarColors.Mauve) {
        if (listening) { stop(); return@TaskButton }
        listening = true
        scope.launch {
            val said = runCatching { dictate() }.getOrDefault("")
            listening = false
            if (said.isNotBlank()) onText(said)
        }
    }
}

@Composable
private fun Banner(text: String, color: Color) =
    Text(text, Modifier.fillMaxWidth().background(color).padding(12.dp), fontSize = 16.sp, color = Color.White)

@Composable
private fun SwitchRow(label: String, on: Boolean, onChange: (Boolean) -> Unit) =
    Row(Modifier.fillMaxWidth().heightIn(min = 56.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.weight(1f), fontSize = 18.sp)
        Switch(checked = on, onCheckedChange = onChange)
    }

@Composable
private fun TaskButton(text: String, color: Color, onClick: () -> Unit) =
    Button(onClick = onClick, Modifier.fillMaxWidth().height(56.dp), shape = RoundedCornerShape(6.dp),
        colors = ButtonDefaults.buttonColors(containerColor = color, contentColor = Color.White)) { Text(text, fontSize = 17.sp) }

@Composable
private fun ActionRow(actions: List<Pair<String, String>>, onAction: (String, Map<String, Any?>) -> Unit) =
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        for ((label, action) in actions) {
            val color = when (action) {
                "done" -> SideCarColors.Emerald; "take_pass" -> SideCarColors.Mauve; "accept" -> SideCarColors.Emerald
                "deliverable" -> SideCarColors.Cobalt; else -> SideCarColors.PanelBorder
            }
            Button(onClick = { onAction(action, emptyMap()) }, Modifier.weight(1f).height(56.dp), shape = RoundedCornerShape(6.dp),
                colors = ButtonDefaults.buttonColors(containerColor = color, contentColor = Color.White)) { Text(label, fontSize = 16.sp) }
        }
    }
