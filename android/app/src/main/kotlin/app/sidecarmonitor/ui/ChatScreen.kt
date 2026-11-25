package app.sidecarmonitor.ui

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Build
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sidecarmonitor.AssistantState
import app.sidecarmonitor.ChatLine
import app.sidecarmonitor.UiState

private val UserBubble = Color(0xFF2B5CE6)
private val LinkBlue = Color(0xFF8AB4FF)

/**
 * SideCar chat: a typical agent chat. Your messages on the right, SideCar's on the left (light markdown, tappable
 * links, what it's doing while it works), a composer with Send, and a mic button for voice mode (hands-free: it
 * listens, answers out loud, and listens again). Typed chats aren't spoken.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun ChatScreen(
    state: UiState,
    onSend: (String) -> Unit,
    onVoice: (Boolean) -> Unit,
    onStop: () -> Unit,
    onNewChat: () -> Unit,
    onClose: () -> Unit,
) {
    var input by remember { mutableStateOf("") }
    val listState = rememberLazyListState()
    val busy = state.assistant == AssistantState.Thinking || state.assistant == AssistantState.Speaking || state.assistant == AssistantState.Connecting
    val waiting = busy && (state.lines.lastOrNull()?.fromUser != false) // thinking, nothing streamed yet
    LaunchedEffect(state.lines.size, state.lines.lastOrNull()?.text, waiting) {
        val count = state.lines.size + if (waiting) 1 else 0
        if (count > 0) listState.animateScrollToItem(count - 1)
    }

    Column(Modifier.fillMaxSize().background(SideCarColors.Background).safeDrawingPadding().imePadding()) {
        // Top bar
        Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onClose, Modifier.size(56.dp)) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Close chat", Modifier.size(28.dp)) }
            Column(Modifier.weight(1f)) {
                Text("SideCar", fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
                Text(when {
                    state.voiceMode -> "Voice mode"
                    busy -> state.assistantNote
                    else -> "Ask anything, or tap the mic to talk"
                }, fontSize = 13.sp, color = SideCarColors.Muted, maxLines = 1)
            }
            IconButton(onClick = onNewChat, Modifier.size(56.dp), enabled = state.lines.isNotEmpty()) {
                Icon(Icons.Filled.Add, "New chat", Modifier.size(28.dp))
            }
        }

        // Messages
        LazyColumn(Modifier.weight(1f).fillMaxWidth().padding(horizontal = 14.dp), state = listState,
            verticalArrangement = Arrangement.spacedBy(14.dp)) {
            if (state.lines.isEmpty()) item {
                Column(Modifier.fillMaxWidth().padding(top = 48.dp), horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    Text("How can I help?", fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf("What's on my calendar today?", "What needs me on my to-do list?", "Anything worth reading in my feeds?",
                            "Remind me in an hour to call back", "Find me free time this week").forEach { s ->
                            Text(s, Modifier.clip(RoundedCornerShape(18.dp)).background(SideCarColors.Panel).clickable { onSend(s) }
                                .padding(horizontal = 14.dp, vertical = 10.dp), fontSize = 14.sp)
                        }
                    }
                }
            }
            itemsIndexed(state.lines) { _, line -> if (line.fromUser) UserMessage(line.text) else AgentMessage(line) }
            if (waiting) item { Thinking(state.assistantNote) }
            item { Spacer(Modifier.height(4.dp)) }
        }

        // Composer, or the voice panel
        if (state.voiceMode) VoicePanel(state, onDone = { onVoice(true) }, onExit = { onVoice(false) })
        else Row(Modifier.fillMaxWidth().padding(10.dp), verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            TextField(
                value = input, onValueChange = { input = it },
                modifier = Modifier.weight(1f).clip(RoundedCornerShape(26.dp)),
                placeholder = { Text("Message SideCar") }, maxLines = 5,
                colors = TextFieldDefaults.colors(
                    focusedContainerColor = SideCarColors.Panel, unfocusedContainerColor = SideCarColors.Panel,
                    focusedIndicatorColor = Color.Transparent, unfocusedIndicatorColor = Color.Transparent,
                ),
            )
            when {
                busy -> RoundButton(Icons.Filled.Stop, "Stop", SideCarColors.Panel) { onStop() }
                input.isNotBlank() -> RoundButton(Icons.AutoMirrored.Filled.Send, "Send", UserBubble) { onSend(input.trim()); input = "" }
                else -> RoundButton(Icons.Filled.Mic, "Voice mode", UserBubble) { onVoice(true) }
            }
        }
    }
}

@Composable
private fun RoundButton(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, color: Color, onClick: () -> Unit) =
    Box(Modifier.size(56.dp).clip(CircleShape).background(color).clickable(onClick = onClick), contentAlignment = Alignment.Center) {
        Icon(icon, label, Modifier.size(28.dp), tint = Color.White)
    }

@Composable
private fun UserMessage(text: String) {
    Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.CenterEnd) {
        Text(text, Modifier.widthIn(max = 300.dp).clip(RoundedCornerShape(20.dp, 20.dp, 6.dp, 20.dp)).background(UserBubble)
            .padding(horizontal = 14.dp, vertical = 10.dp), fontSize = 16.sp, color = Color.White)
    }
}

@Composable
private fun AgentMessage(line: ChatLine) {
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        line.tools.distinct().forEach { tool ->
            Row(Modifier.clip(RoundedCornerShape(12.dp)).background(SideCarColors.Panel).padding(horizontal = 10.dp, vertical = 5.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Icon(Icons.Filled.Build, null, Modifier.size(14.dp), tint = SideCarColors.Muted)
                Text(tool, fontSize = 12.sp, color = SideCarColors.Muted)
            }
        }
        if (line.text.isNotBlank()) Markdown(line.text)
    }
}

@Composable
private fun Thinking(note: String) {
    val pulse by rememberInfiniteTransition(label = "dots").animateFloat(0.3f, 1f, infiniteRepeatable(tween(600), RepeatMode.Reverse), label = "a")
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        repeat(3) { i -> Box(Modifier.size(8.dp).scale(if (i == 1) pulse else 1.3f - pulse).clip(CircleShape).background(SideCarColors.Muted)) }
        Text(note, fontSize = 13.sp, color = SideCarColors.Muted)
    }
}

/** Voice mode: mic level, what it's doing, Done (send what you said now) and the keyboard to go back to typing. */
@Composable
private fun VoicePanel(state: UiState, onDone: () -> Unit, onExit: () -> Unit) {
    val listening = state.assistant == AssistantState.Listening
    Column(Modifier.fillMaxWidth().background(SideCarColors.Panel).padding(16.dp), horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Box(Modifier.size(96.dp), contentAlignment = Alignment.Center) {
            Box(Modifier.size(96.dp).scale(if (listening) 0.7f + state.micLevel * 0.6f else 0.7f).clip(CircleShape).background(UserBubble.copy(alpha = 0.35f)))
            Box(Modifier.size(64.dp).clip(CircleShape).background(UserBubble), contentAlignment = Alignment.Center) {
                Icon(Icons.Filled.Mic, null, Modifier.size(32.dp), tint = Color.White)
            }
        }
        Text(state.assistantNote, fontSize = 15.sp)
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            if (listening) Text("Done", Modifier.clip(RoundedCornerShape(24.dp)).background(UserBubble).clickable(onClick = onDone)
                .padding(horizontal = 28.dp, vertical = 12.dp), fontSize = 16.sp)
            Row(Modifier.clip(RoundedCornerShape(24.dp)).background(SideCarColors.Steel).clickable(onClick = onExit).padding(horizontal = 20.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(Icons.Filled.Keyboard, null, Modifier.size(20.dp)); Text("Type instead", fontSize = 16.sp)
            }
        }
    }
}

/** Light markdown for replies: paragraphs, "- " bullets, numbered lines, **bold**, `code`, and [links](https://...). */
@Composable
private fun Markdown(text: String) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        for (raw in text.trim().split('\n')) {
            val line = raw.trimEnd()
            if (line.isBlank()) continue
            val heading = line.startsWith("#")
            val bullet = Regex("^\\s*[-*•]\\s+").find(line)
            val number = Regex("^\\s*(\\d+)[.)]\\s+").find(line)
            val body = when {
                heading -> line.trimStart('#', ' ')
                bullet != null -> line.substring(bullet.range.last + 1)
                number != null -> line.substring(number.range.last + 1)
                else -> line
            }
            Row {
                if (bullet != null) Text("•  ", fontSize = 16.sp)
                if (number != null) Text("${number.groupValues[1]}.  ", fontSize = 16.sp)
                Text(inline(body), fontSize = if (heading) 18.sp else 16.sp, fontWeight = if (heading) FontWeight.SemiBold else null, lineHeight = 23.sp)
            }
        }
    }
}

private fun inline(s: String): AnnotatedString = buildAnnotatedString {
    val token = Regex("\\*\\*(.+?)\\*\\*|`([^`]+)`|\\[([^\\]]+)]\\((https?://[^)\\s]+)\\)|(https?://\\S+)")
    var at = 0
    for (m in token.findAll(s)) {
        append(s.substring(at, m.range.first))
        val (bold, code, label, url, bare) = m.destructured
        when {
            bold.isNotEmpty() -> withStyle(SpanStyle(fontWeight = FontWeight.SemiBold)) { append(bold) }
            code.isNotEmpty() -> withStyle(SpanStyle(background = Color(0xFF2A2F3A))) { append(code) }
            label.isNotEmpty() -> withLink(LinkAnnotation.Url(url, TextLinkStyles(SpanStyle(color = LinkBlue, textDecoration = TextDecoration.Underline)))) { append(label) }
            else -> withLink(LinkAnnotation.Url(bare, TextLinkStyles(SpanStyle(color = LinkBlue, textDecoration = TextDecoration.Underline)))) { append(bare) }
        }
        at = m.range.last + 1
    }
    append(s.substring(at))
}
