package app.sidecarmonitor.data

import kotlinx.serialization.Serializable

/** The kiosk's to-do list as the phone sees it (the kiosk's task store is the source of truth). */
@Serializable
data class TasksPayload(
    val today: String = "",
    val todayLabel: String = "",
    val brief: String? = null,
    val views: TaskViews = TaskViews(),
)

@Serializable
data class TaskViews(
    val today: List<TaskRow> = emptyList(),
    val upcoming: List<TaskRow> = emptyList(),
    val needsMe: List<TaskRow> = emptyList(),
    val done: List<TaskRow> = emptyList(),
)

@Serializable
data class TaskRow(
    val id: String,
    val title: String,
    val priority: String = "Medium",
    val status: String = "Open",
    val date: String? = null,
    val dateLabel: String? = null,
    val hard: Boolean = false,
    val pinned: Boolean = false,
    val help: Boolean = true,
    val overdue: Boolean = false,
    val needsMe: Boolean = false,
    val draftVersion: Int? = null,
    val question: Boolean = false,
    val revision: Boolean = false,
)

@Serializable
data class TaskDetail(
    val row: TaskRow,
    val details: String? = null,
    /** The item this to-do is about (e.g. the post tagged in a briefing), opened by "Open item". */
    val link: String? = null,
    val linkLabel: String? = null,
    val version: Int = 0,
    val needsDecision: Boolean = false,
    val rolloverPaused: Boolean = false,
    val rolloverCount: Int = 0,
    val question: String? = null,
    val deliverable: DeliverableInfo? = null,
    val pendingRevision: String? = null,
    val history: List<HistoryItem> = emptyList(),
)

@Serializable
data class DeliverableInfo(val number: Int, val author: String, val at: String, val content: String)

@Serializable
data class HistoryItem(
    val id: String,
    val at: String,
    val author: String,
    val kind: String,
    val text: String,
    val canUndo: Boolean = false,
)

/** Weekly plan: Mon–Sun with tasks per day, overdue from earlier weeks, and undated tasks. */
@Serializable
data class WeekPlan(
    val today: String = "",
    val weekStart: String = "",
    val weekLabel: String = "",
    val days: List<WeekDay> = emptyList(),
    val earlier: List<TaskRow> = emptyList(),
    val noDay: List<TaskRow> = emptyList(),
)

@Serializable
data class WeekDay(
    val date: String,
    val label: String,
    val shortLabel: String,
    val isToday: Boolean = false,
    val isPast: Boolean = false,
    val tasks: List<TaskRow> = emptyList(),
)
