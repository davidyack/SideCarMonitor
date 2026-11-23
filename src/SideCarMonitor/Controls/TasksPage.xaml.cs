using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SideCarMonitor.Services;
using SideCarTasks;

namespace SideCarMonitor.Controls;

/// <summary>
/// The to-do list page: Today / Upcoming / Needs me / Done, and a touch-friendly task detail with the
/// deliverable, notes and activity. Every change goes through <see cref="TaskService"/> as the user.
/// </summary>
public sealed partial class TasksPage : UserControl
{
    private TaskService? _tasks;
    private TaskAssistant? _assistant;
    private TaskView _view = TaskView.Today;
    private string? _selectedId;
    private bool _dirty;
    private bool _rebuilding;

    // Edits in progress for the selected task (saved together with a version check)
    private TextBox? _titleBox, _detailsBox, _deliverableBox;
    private CalendarDatePicker? _datePicker;
    private ToggleSwitch? _hardSwitch;
    private Priority? _editPriority;
    private int _loadedVersion;
    private bool _weekMode;
    private int _weekOffset;

    public event Action? HomeRequested;

    /// <summary>The task you're looking at, so "it" in chat/voice means this task.</summary>
    public TaskItem? SelectedTask => _selectedId is null ? null : _tasks?.Get(_selectedId);

    public TasksPage() => InitializeComponent();

    public void Initialize(TaskService tasks, TaskAssistant? assistant)
    {
        _tasks = tasks;
        _assistant = assistant;
        tasks.Store.Changed += _ => DispatcherQueue.TryEnqueue(Refresh);
        if (assistant is not null)
            assistant.ReviewStateChanged += (id, _) => DispatcherQueue.TryEnqueue(() => { if (id == _selectedId && !_dirty) ShowDetail(); });
        Refresh();
    }

    public void Show(string? taskId = null)
    {
        if (taskId is not null) { _selectedId = taskId; _dirty = false; }
        Refresh();
    }

    // ---- List ----------------------------------------------------------------------------------

    public void Refresh()
    {
        if (_tasks is null) return;
        var counts = Enum.GetValues<TaskView>().ToDictionary(v => v, v => _tasks.View(v).Count);
        TabToday.Content = $"Today {counts[TaskView.Today]}";
        TabUpcoming.Content = $"Upcoming {counts[TaskView.Upcoming]}";
        TabNeedsMe.Content = $"Needs me {counts[TaskView.NeedsMe]}";
        TabDone.Content = "Done";
        foreach (var (b, v) in new[] { (TabToday, TaskView.Today), (TabUpcoming, TaskView.Upcoming), (TabNeedsMe, TaskView.NeedsMe), (TabDone, TaskView.Done) })
        {
            b.Background = (Brush)Application.Current.Resources[v == _view ? "TileEmerald" : (v == TaskView.NeedsMe && counts[v] > 0 ? "TileCrimson" : "PanelBackground")];
            b.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        }
        SubtitleText.Text = $"{_tasks.Today:dddd, MMMM d}";

        var brief = _tasks.Store.Read().Briefs.LastOrDefault();
        BriefCard.Visibility = brief is { Dismissed: false } && brief.Date == _tasks.Today ? Visibility.Visible : Visibility.Collapsed;
        BriefText.Text = brief?.Text ?? "";

        var items = _tasks.View(_view);
        _rebuilding = true;
        TaskList.Items.Clear();
        foreach (var t in items) TaskList.Items.Add(Row(t));
        TaskList.SelectedIndex = items.ToList().FindIndex(t => t.Id == _selectedId);
        _rebuilding = false;
        EmptyText.Text = items.Count == 0 ? _view switch
        {
            TaskView.Today => "Nothing for today.",
            TaskView.NeedsMe => "Nothing needs you.",
            TaskView.Done => "Nothing finished yet.",
            _ => "Nothing upcoming.",
        } : "";

        if (_weekMode) BuildWeek();
        if (!_dirty) ShowDetail();
    }

    // ---- Weekly plan (drag between days) -----------------------------------------------------

    private void WeekTab_Click(object sender, RoutedEventArgs e) => SetWeekMode(true);
    private void ListMode_Click(object sender, RoutedEventArgs e) => SetWeekMode(false);
    private void PrevWeek_Click(object sender, RoutedEventArgs e) { _weekOffset--; BuildWeek(); }
    private void NextWeek_Click(object sender, RoutedEventArgs e) { _weekOffset++; BuildWeek(); }

    private void SetWeekMode(bool on)
    {
        _weekMode = on;
        WeekArea.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ListArea.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on) { _weekOffset = 0; BuildWeek(); } else Refresh();
    }

    private void BuildWeek()
    {
        if (_tasks is null) return;
        var today = _tasks.Today;
        var anchor = today.AddDays(7 * _weekOffset);
        var monday = anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7));
        WeekLabel.Text = $"{monday:MMM d} \u2013 {monday.AddDays(6):MMM d}";
        var all = _tasks.All();

        WeekColumns.Children.Clear();
        WeekColumns.ColumnDefinitions.Clear();
        var columns = Enumerable.Range(0, 7).Select(i => (DateOnly?)monday.AddDays(i)).Append(null).ToList();
        for (var i = 0; i < columns.Count; i++)
        {
            WeekColumns.ColumnDefinitions.Add(new ColumnDefinition());
            var day = columns[i];
            var tasks = all.Where(t => day is null
                    ? t.Status == TodoStatus.Open && t.Date is null
                    : t.Date == day && (t.Status == TodoStatus.Open || t.Completed is not null))
                .OrderBy(t => t.Status).ThenBy(t => t.Priority).ToList();
            var column = WeekColumn(day, today, tasks);
            Grid.SetColumn(column, i);
            WeekColumns.Children.Add(column);
        }
    }

    private Border WeekColumn(DateOnly? day, DateOnly today, List<TaskItem> tasks)
    {
        var past = day is { } d0 && d0 < today;
        var header = new StackPanel { Padding = new Thickness(Num("SmallGap")) };
        header.Children.Add(new TextBlock
        {
            Text = day?.ToString("ddd") ?? "No day", FontSize = Num("FontBody"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = day == today ? Res("TileEmerald") : Res("KioskForeground"),
        });
        header.Children.Add(new TextBlock { Text = day?.ToString("MMM d") ?? "yet", FontSize = Num("FontCaption"), Foreground = Res("MutedText") });

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
            CanDragItems = true, AllowDrop = !past, CanReorderItems = false, Tag = day,
        };
        foreach (var t in tasks)
        {
            var text = new TextBlock
            {
                Text = (t.DateType == DateKind.Hard ? "\U0001F512 " : "") + t.Title, TextWrapping = TextWrapping.Wrap,
                FontSize = Num("FontCaption"),
                TextDecorations = t.Status == TodoStatus.Done ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
                Foreground = t.Status == TodoStatus.Done ? Res("MutedText") : Res("KioskForeground"),
            };
            var row = new Border
            {
                Child = text, BorderThickness = new Thickness(Num("Dot") / 2, 0, 0, 0), BorderBrush = PriorityBrush(t.Priority),
                Padding = new Thickness(Num("SmallGap"), Num("SmallGap"), 0, Num("SmallGap")),
            };
            list.Items.Add(new ListViewItem { Content = row, Tag = t.Id, Padding = new Thickness(0), MinHeight = Num("TouchTarget") * 0.8 });
        }
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is Border b && list.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Content == b) is { Tag: string id }) OpenFromWeek(id);
        };
        list.DragItemsStarting += (_, e) =>
        {
            var item = list.Items.OfType<ListViewItem>().FirstOrDefault(i => e.Items.Contains(i.Content) || e.Items.Contains(i));
            var t = item?.Tag is string id ? _tasks!.Get(id) : null;
            // Hard deadlines and finished tasks don't move by dragging.
            if (t is null || t.DateType == DateKind.Hard || t.Status == TodoStatus.Done) { e.Cancel = true; return; }
            e.Data.SetText(t.Id);
            e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        list.DragOver += (_, e) => e.AcceptedOperation = past
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.None
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        list.Drop += async (_, e) =>
        {
            if (past || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
            var id = await e.DataView.GetTextAsync();
            var t = _tasks!.Get(id);
            if (t is null || t.Date == day) return;
            // A drop is your explicit reschedule to a flexible target on that day (or no day).
            Do(() => day is { } d ? _tasks.Edit(Actor.User, id, date: d, dateType: DateKind.Flexible) : _tasks.Edit(Actor.User, id, clearDate: true));
        };

        var panel = new Grid { RowSpacing = Num("SmallGap") };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.Children.Add(header);
        Grid.SetRow(list, 1);
        panel.Children.Add(list);
        return new Border
        {
            Child = panel, Background = Res("PanelBackground"),
            BorderBrush = day == today ? Res("TileEmerald") : Res("PanelBorder"), BorderThickness = new Thickness(day == today ? 2 : 1),
            CornerRadius = (CornerRadius)Application.Current.Resources["SmallCorner"], Opacity = past ? 0.7 : 1,
        };
    }

    private void OpenFromWeek(string id)
    {
        var t = _tasks?.Get(id);
        if (t is null) return;
        _view = t.Status == TodoStatus.Done ? TaskView.Done
              : _tasks!.NeedsMe(t) ? TaskView.NeedsMe
              : _tasks.View(TaskView.Today).Any(x => x.Id == id) ? TaskView.Today : TaskView.Upcoming;
        _selectedId = id;
        _dirty = false;
        SetWeekMode(false);
    }

    private ListViewItem Row(TaskItem t)
    {
        var overdue = _tasks!.IsOverdue(t);
        var grid = new Grid { ColumnSpacing = Num("Gap"), Tag = t.Id };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Num("Dot")) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Border { Background = PriorityBrush(t.Priority), CornerRadius = new CornerRadius(2) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock
        {
            Text = t.Title, FontSize = Num("FontBody"), TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = t.Status == TodoStatus.Done ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
        });
        var bits = new List<string>();
        if (t.Date is { } d) bits.Add($"{(t.DateType == DateKind.Hard ? "Due " : "")}{d:ddd MMM d}{(overdue ? " · overdue" : "")}");
        if (t.PinnedToday) bits.Add("On Today");
        if (t.Deliverable.Current is not null) bits.Add($"Draft v{t.Deliverable.Current.Number}");
        if (t.PendingQuestionId is not null) bits.Add("Question for you");
        if (t.Deliverable.PendingRevision is not null) bits.Add("Revision to review");
        if (_tasks.NeedsDecision(t)) bits.Add("Decide: date?");
        if (!t.AssistantHelp) bits.Add("Assistant off");
        text.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", bits), FontSize = Num("FontCaption"),
            Foreground = (Brush)Application.Current.Resources[overdue && t.DateType == DateKind.Hard ? "AlertBrush" : "MutedText"],
        });
        grid.Children.Add(text);
        return new ListViewItem { Content = grid, Tag = t.Id };
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding || TaskList.SelectedItem is not ListViewItem item) return;
        _selectedId = (string)item.Tag;
        _dirty = false;
        ShowDetail();
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        _view = Enum.Parse<TaskView>((string)((Button)sender).Tag);
        if (_weekMode) SetWeekMode(false); else Refresh();
    }

    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();

    private void DismissBrief_Click(object sender, RoutedEventArgs e)
    {
        _tasks?.Store.Update(Actor.User, d => { if (d.Briefs.LastOrDefault() is { } b) b.Dismissed = true; return true; });
    }

    private void AddTask_Click(object sender, RoutedEventArgs e) => AddFromBox();

    private void NewTaskBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; AddFromBox(); }
    }

    private void AddFromBox()
    {
        if (_tasks is null || string.IsNullOrWhiteSpace(NewTaskBox.Text)) return;
        var t = _tasks.Add(Actor.User, NewTaskBox.Text, pinToday: _view == TaskView.Today);
        NewTaskBox.Text = "";
        if (_view is TaskView.Done or TaskView.NeedsMe) _view = TaskView.Upcoming;
        _selectedId = t.Id;
        _dirty = false;
        Refresh();
    }

    // ---- Detail --------------------------------------------------------------------------------

    private void ShowDetail()
    {
        var t = SelectedTask;
        Detail.Children.Clear();
        NoSelectionText.Visibility = t is null ? Visibility.Visible : Visibility.Collapsed;
        DetailScroll.Visibility = t is null ? Visibility.Collapsed : Visibility.Visible;
        if (t is null || _tasks is null) return;
        _loadedVersion = t.Version;
        _editPriority = null;

        // Banners: what needs you on this task.
        if (_assistant?.IsReviewing(t.Id) == true)
            Detail.Children.Add(Banner("The assistant is working on this…", "TileMauve"));
        if (_tasks.NeedsDecision(t))
        {
            Detail.Children.Add(Banner(t.RolloverPaused
                ? "This flexible target is overdue and automatic moves are paused."
                : $"This flexible target is overdue and has already moved {t.RolloverCount} times. What should happen?", "TileCrimson"));
            Detail.Children.Add(ButtonRow(
                ("Pick a date", "TileCobalt", () => { _datePicker?.Focus(FocusState.Programmatic); if (_datePicker is not null) _datePicker.IsCalendarOpen = true; }),
                ("Drop the date", "PanelBorder", () => Do(() => _tasks.DropDate(Actor.User, t.Id))),
                ("Mark done", "TileEmerald", () => Do(() => _tasks.MarkDone(Actor.User, t.Id)))));
        }
        if (t.RolloverPaused && !_tasks.NeedsDecision(t))
            Detail.Children.Add(ButtonRow(("Resume automatic moves", "PanelBorder", () => Do(() => _tasks.ResumeRollover(Actor.User, t.Id)))));
        if (t.History.FirstOrDefault(e => e.Id == t.PendingQuestionId) is { } q)
        {
            Detail.Children.Add(Banner($"Assistant asks: {q.Text}", "TileAmber"));
            var answer = new TextBox { PlaceholderText = "Your answer", FontSize = Num("FontBody"), MinHeight = Num("TouchTarget"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            Detail.Children.Add(answer);
            Detail.Children.Add(ButtonRow(("Send answer", "TileCobalt", () => Do(() => _tasks.AddNote(Actor.User, t.Id, answer.Text)))));
        }

        // Fields
        _titleBox = new TextBox { Text = t.Title, FontSize = Num("FontTitle"), MinHeight = Num("TouchTarget"), TextWrapping = TextWrapping.Wrap };
        _titleBox.TextChanged += (_, _) => _dirty = true;
        Detail.Children.Add(_titleBox);
        _detailsBox = new TextBox
        {
            Text = t.Details ?? "", PlaceholderText = "Details", FontSize = Num("FontBody"), AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = Num("TouchTarget") * 1.6,
        };
        _detailsBox.TextChanged += (_, _) => _dirty = true;
        Detail.Children.Add(_detailsBox);

        // Priority
        var prio = new Grid { ColumnSpacing = Num("SmallGap") };
        foreach (var (p, i) in new[] { (Priority.High, 0), (Priority.Medium, 1), (Priority.Low, 2) })
        {
            prio.ColumnDefinitions.Add(new ColumnDefinition());
            var b = BigButton(p.ToString(), p == t.Priority ? PriorityBrush(p) : Res("PanelBorder"));
            b.Click += (_, _) =>
            {
                _editPriority = p; _dirty = true;
                foreach (var c in prio.Children.OfType<Button>()) c.Background = Res("PanelBorder");
                b.Background = PriorityBrush(p);
            };
            Grid.SetColumn(b, i);
            prio.Children.Add(b);
        }
        Detail.Children.Add(Label("Priority"));
        Detail.Children.Add(prio);

        // Date
        Detail.Children.Add(Label("Date"));
        var dateRow = new Grid { ColumnSpacing = Num("Gap") };
        dateRow.ColumnDefinitions.Add(new ColumnDefinition());
        dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _datePicker = new CalendarDatePicker
        {
            Date = t.Date?.ToDateTime(TimeOnly.MinValue), PlaceholderText = "No date", FontSize = Num("FontBody"),
            MinHeight = Num("TouchTarget"), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _datePicker.DateChanged += (_, _) => _dirty = true;
        dateRow.Children.Add(_datePicker);
        var clear = BigButton("No date", Res("PanelBorder"));
        clear.Click += (_, _) => { _datePicker.Date = null; _dirty = true; };
        Grid.SetColumn(clear, 1);
        dateRow.Children.Add(clear);
        Detail.Children.Add(dateRow);
        _hardSwitch = Toggle("Hard deadline (never moves on its own)", t.DateType == DateKind.Hard, _ => _dirty = true);
        Detail.Children.Add(_hardSwitch);

        // Pins and switches (save immediately)
        Detail.Children.Add(Toggle("On Today", t.PinnedToday, on => Do(() => _tasks.PinToday(Actor.User, t.Id, on))));
        Detail.Children.Add(Toggle("Let assistant help", t.AssistantHelp, on => Do(() => _tasks.SetAssistantHelp(Actor.User, t.Id, on))));

        // Actions
        Detail.Children.Add(ButtonRow(
            ("Save changes", "TileCobalt", SaveFields),
            ("Take a pass", "TileMauve", () => { if (_assistant is not null) _ = _assistant.ReviewAsync(t.Id, explicitRequest: true); }),
            t.Status == TodoStatus.Open
                ? ("Mark done", "TileEmerald", () => Do(() => _tasks.MarkDone(Actor.User, t.Id)))
                : ("Reopen", "PanelBorder", () => Do(() => _tasks.Reopen(Actor.User, t.Id)))));

        // Deliverable
        var cur = t.Deliverable.Current;
        Detail.Children.Add(Label(cur is null ? "Deliverable (none yet)"
            : $"Deliverable v{cur.Number} · by {(cur.Author == Actor.User ? "you" : "the assistant")} · {cur.At:MMM d h:mm tt}"));
        _deliverableBox = new TextBox
        {
            Text = cur?.Content ?? "", PlaceholderText = "The assistant's draft, plan or comparison shows up here. You can edit it.",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = Num("FontCaption"), MinHeight = Num("TouchTarget") * 2.5,
        };
        _deliverableBox.TextChanged += (_, _) => _dirty = true;
        Detail.Children.Add(_deliverableBox);
        Detail.Children.Add(ButtonRow(("Save my edit", "TileCobalt", () =>
            Do(() => _tasks.SaveDeliverable(Actor.User, t.Id, _deliverableBox.Text)))));
        if (t.Deliverable.PendingRevision is { } rev)
        {
            Detail.Children.Add(Banner("The assistant suggested a revision (yours is kept until you choose):", "TileAmber"));
            Detail.Children.Add(new TextBlock { Text = rev.Content, TextWrapping = TextWrapping.Wrap, FontSize = Num("FontCaption"), IsTextSelectionEnabled = true });
            Detail.Children.Add(ButtonRow(
                ("Accept", "TileEmerald", () => Do(() => _tasks.AcceptRevision(Actor.User, t.Id))),
                ("Dismiss", "PanelBorder", () => Do(() => _tasks.DismissRevision(Actor.User, t.Id)))));
        }

        // Notes & activity
        Detail.Children.Add(Label("Notes and activity"));
        var note = new TextBox { PlaceholderText = "Add a note", FontSize = Num("FontBody"), MinHeight = Num("TouchTarget"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        Detail.Children.Add(note);
        Detail.Children.Add(ButtonRow(("Add note", "PanelBorder", () => Do(() => _tasks.AddNote(Actor.User, t.Id, note.Text)))));
        foreach (var entry in Enumerable.Reverse(t.History).Take(25))
        {
            var row = new Grid { ColumnSpacing = Num("Gap") };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var who = entry.Author switch { Actor.User => "You", Actor.Assistant => "Assistant", _ => "SideCar" };
            row.Children.Add(new TextBlock
            {
                Text = $"{entry.At:MMM d h:mm tt} · {who}: {entry.Text}", TextWrapping = TextWrapping.Wrap, FontSize = Num("FontCaption"),
                Foreground = Res(entry.Kind is EntryKind.Note or EntryKind.Question or EntryKind.Answer ? "KioskForeground" : "MutedText"),
            });
            if (entry.Rollover is { Undone: false } r && r.VersionAfter == t.Version)
            {
                var undo = BigButton("Undo", Res("PanelBorder"));
                undo.Click += (_, _) => Do(() => _tasks.UndoRollover(Actor.User, t.Id, entry.Id));
                Grid.SetColumn(undo, 1);
                row.Children.Add(undo);
            }
            Detail.Children.Add(row);
        }
    }

    private void SaveFields()
    {
        var t = SelectedTask;
        if (_tasks is null || t is null) return;
        var newDate = _datePicker?.Date is { } dt ? DateOnly.FromDateTime(dt.Date) : (DateOnly?)null;
        Do(() => _tasks.Edit(Actor.User, t.Id, _loadedVersion,
            title: _titleBox?.Text, details: _detailsBox?.Text, priority: _editPriority,
            date: newDate, clearDate: newDate is null && t.Date is not null,
            dateType: _hardSwitch?.IsOn == true ? DateKind.Hard : DateKind.Flexible));
    }

    /// <summary>Runs a change; on a rule or version conflict, shows why and reloads the latest version.</summary>
    private async void Do(Func<TaskItem> change)
    {
        try
        {
            change();
            _dirty = false;
            Refresh();
        }
        catch (TaskRuleException ex)
        {
            _dirty = false;
            Refresh();
            await new ContentDialog
            {
                Title = "Couldn't save", Content = ex.Message, CloseButtonText = "OK", XamlRoot = XamlRoot,
                FontSize = Num("FontBody"),
            }.ShowAsync();
        }
    }

    // ---- Little UI helpers --------------------------------------------------------------------

    private static double Num(string key) => (double)Application.Current.Resources[key];
    private static Brush Res(string key) => Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b
        ? b : new SolidColorBrush(Microsoft.UI.Colors.White);

    private static Brush PriorityBrush(Priority p) => Res(p switch { Priority.High => "TileCrimson", Priority.Medium => "TileAmber", _ => "TileSteel" });

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = Num("FontCaption"), Foreground = Res("MutedText"), Margin = new Thickness(0, Num("SmallGap"), 0, 0),
    };

    private static Border Banner(string text, string brush) => new()
    {
        Background = Res(brush), CornerRadius = (CornerRadius)Application.Current.Resources["SmallCorner"],
        Padding = (Thickness)Application.Current.Resources["LabelPadding"],
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = Num("FontBody"), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
    };

    private static Button BigButton(string text, Brush background) => new()
    {
        Content = text, Background = background, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = Num("TouchTarget"), FontSize = Num("FontBody"),
        CornerRadius = (CornerRadius)Application.Current.Resources["SmallCorner"],
    };

    private static Grid ButtonRow(params (string Text, string Brush, Action OnClick)[] buttons)
    {
        var g = new Grid { ColumnSpacing = Num("SmallGap") };
        for (var i = 0; i < buttons.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var (text, brush, onClick) = buttons[i];
            var b = BigButton(text, Res(brush));
            b.Click += (_, _) => onClick();
            Grid.SetColumn(b, i);
            g.Children.Add(b);
        }
        return g;
    }

    private static ToggleSwitch Toggle(string header, bool on, Action<bool> changed)
    {
        var t = new ToggleSwitch { Header = header, IsOn = on, FontSize = Num("FontBody"), MinHeight = Num("TouchTarget") };
        t.Toggled += (_, _) => changed(t.IsOn);
        return t;
    }
}
