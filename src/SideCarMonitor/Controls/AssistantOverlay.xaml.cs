using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SideCarMonitor.Services;

namespace SideCarMonitor.Controls;

/// <summary>One line in the assistant conversation.</summary>
public sealed class ChatLine : INotifyPropertyChanged
{
    public ChatLine(bool fromUser, string text)
    {
        FromUser = fromUser;
        _text = text;
    }

    public bool FromUser { get; }
    public HorizontalAlignment Alignment => FromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Brush Background => (Brush)Application.Current.Resources[FromUser ? "TileCobalt" : "PanelBorder"];

    private string _text;
    public string Text
    {
        get => _text;
        set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Floating mic button (lower-right, every page) and the voice panel it opens:
/// listen (or type) -> send to SideCarAgent -> show the reply as it streams -> speak it.
/// </summary>
public sealed partial class AssistantOverlay : UserControl
{
    private enum State { Idle, Starting, Listening, Thinking, Speaking }

    private static readonly TimeSpan AutoClose = TimeSpan.FromSeconds(60);

    private readonly ObservableCollection<ChatLine> _lines = [];
    private readonly DispatcherQueueTimer _idleTimer;
    private AgentHost? _agent;
    private VoiceService? _voice;
    private string? _notConfiguredReason;
    private CancellationTokenSource? _turnCts;
    private CancellationTokenSource? _listenCts;
    private ChatLine? _replyLine;
    private string? _replyId;
    private TaskCompletionSource<string>? _replyDone;
    private State _state;

    /// <summary>True while the panel is open or the assistant is listening, thinking or speaking.</summary>
    public bool IsBusy => _state != State.Idle || Panel.Visibility == Visibility.Visible;

    public AssistantOverlay()
    {
        InitializeComponent();
        Conversation.ItemsSource = _lines;
        _idleTimer = DispatcherQueue.CreateTimer();
        _idleTimer.Interval = AutoClose;
        _idleTimer.IsRepeating = false;
        _idleTimer.Tick += (_, _) => { if (_state == State.Idle) ClosePanel(); };
        SetState(State.Idle);
    }

    /// <summary>Optional context for the agent, e.g. which task you're looking at (so "it" resolves).</summary>
    public Func<string?>? ContextProvider { get; set; }

    /// <summary>Wires up the agent and speech services. Call once from the window.</summary>
    public void Initialize(AgentHost? agent, VoiceService? voice, string? notConfiguredReason)
    {
        _agent = agent;
        _voice = voice;
        _notConfiguredReason = notConfiguredReason;
        if (_agent is null) return;

        _agent.Delta += (id, text) =>
        {
            if (id != _replyId) return;
            _replyLine ??= AddLine(fromUser: false, "");
            _replyLine.Text += text;
            ScrollToEnd();
        };
        _agent.Message += (id, text) =>
        {
            if (id != _replyId) return;
            if (string.IsNullOrWhiteSpace(text)) return;
            _replyLine ??= AddLine(fromUser: false, "");
            _replyLine.Text = text; // final text replaces the streamed chunks
            // Not done yet: a reply can have several messages around tool calls. Idle ends the turn.
        };
        _agent.ToolStarted += (id, name) =>
        {
            if (id == _replyId && _state == State.Thinking)
                StatusText.Text = name == "get_local_weather" ? "Checking the weather…" : $"Using {name}…";
        };
        _agent.Idle += id => { if (id == _replyId) _replyDone?.TrySetResult(_replyLine?.Text ?? ""); };
        _agent.Error += (id, message) =>
        {
            if (id is not null && id != _replyId) return;
            if (_replyDone is not null) _replyDone.TrySetException(new InvalidOperationException(message));
            else AddLine(fromUser: false, message);
        };
    }

    // ---- Open / close ---------------------------------------------------------------

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        OpenPanel();
        _voiceConversation = true;
        await ListenAndRespondAsync();
    }

    /// <summary>Started by voice: after each spoken reply the mic reopens until you go quiet, say "that's all", or tap Stop.</summary>
    private bool _voiceConversation;

    private static readonly HashSet<string> EndPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "thanks", "thank you", "thanks a lot", "thank you very much", "no thanks", "no thank you", "that's all", "that is all",
        "that's it", "that's all thanks", "that's all thank you", "that's it thanks", "never mind", "nevermind", "stop",
        "goodbye", "bye", "done", "i'm done", "all set", "i'm all set", "nothing", "nope", "no", "we're done", "cancel",
    };

    private static bool IsEndPhrase(string said)
    {
        var s = new string(said.ToLowerInvariant().Where(c => char.IsLetter(c) || c == ' ' || c == '\'').ToArray()).Trim();
        foreach (var prefix in new[] { "ok ", "okay ", "great ", "perfect ", "cool " }) if (s.StartsWith(prefix)) s = s[prefix.Length..].Trim();
        s = s.Replace(" sidecar", "").Trim();
        return EndPhrases.Contains(s);
    }

    private void OpenPanel()
    {
        Backdrop.Visibility = Visibility.Visible;
        Panel.Visibility = Visibility.Visible;
        MicButton.Visibility = Visibility.Collapsed;
        _idleTimer.Stop();
    }

    private void ClosePanel()
    {
        CancelTurn();
        _voiceConversation = false;
        // Let the agent wrap up: save a summary and any new facts, clear its working memory.
        if (_lines.Count > 0 && _agent is not null) _ = _agent.EndConversationAsync();
        Backdrop.Visibility = Visibility.Collapsed;
        Panel.Visibility = Visibility.Collapsed;
        MicButton.Visibility = Visibility.Visible;
        _idleTimer.Stop();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => ClosePanel();
    private void Backdrop_Tapped(object sender, TappedRoutedEventArgs e) => ClosePanel();

    // ---- Talk / type / stop ---------------------------------------------------------

    private async void Talk_Click(object sender, RoutedEventArgs e)
    {
        // While listening, the button reads "Done": stop listening and send what was heard so far.
        if (_state == State.Listening) { _listenCts?.Cancel(); return; }
        _voiceConversation = true;
        await ListenAndRespondAsync();
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendTypedAsync();

    private async void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await SendTypedAsync(); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) { _voiceConversation = false; CancelTurn(); }

    private async Task SendTypedAsync()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0 || _state is State.Starting or State.Thinking) return;
        Input.Text = "";
        _voiceConversation = false;
        CancelTurn();
        await RespondAsync(text);
    }

    private async Task ListenAndRespondAsync(bool followUp = false)
    {
        if (!Ready()) return;
        CancelTurn();
        var cts = _turnCts = new CancellationTokenSource();
        try
        {
            SetState(State.Listening);
            var hint = followUp ? "Go ahead\u2026 (or say \u201Cthat's all\u201D)" : MicHint();
            PartialText.Text = hint;
            PartialText.Visibility = Visibility.Visible;
            using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            _listenCts = listenCts;
            var heard = await _voice!.ListenOnceAsync(
                partial => DispatcherQueue.TryEnqueue(() => PartialText.Text = partial), listenCts.Token,
                followUp ? _voice.FollowUpTimeout : null);
            _listenCts = null;
            PartialText.Visibility = Visibility.Collapsed;

            if (cts.IsCancellationRequested) return;
            if (heard.Length == 0)
            {
                _voiceConversation = false;
                SetState(State.Idle, followUp ? "Tap Talk to continue." : "Didn't catch that. Tap Talk to try again.");
                return;
            }
            if (followUp && IsEndPhrase(heard))
            {
                _voiceConversation = false;
                AddLine(fromUser: true, heard);
                SetState(State.Idle, "Okay. Tap Talk anytime.");
                return;
            }
            await RespondAsync(heard);
        }
        catch (Exception ex)
        {
            PartialText.Visibility = Visibility.Collapsed;
            SetState(State.Idle, ex.Message);
        }
    }

    /// <summary>Sends text to the agent, streams the reply into the panel, then speaks it.</summary>
    private async Task RespondAsync(string text)
    {
        if (!Ready()) return;
        // Each reply gets its own cancel switch; a just-cancelled listen must not cancel this turn.
        var cts = _turnCts = new CancellationTokenSource();
        var spoke = false;
        AddLine(fromUser: true, text);
        try
        {
            if (!_agent!.IsRunning) SetState(State.Starting);
            await _agent.EnsureStartedAsync(cts.Token);

            SetState(State.Thinking);
            _replyLine = null;
            _replyDone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = ContextProvider?.Invoke();
            _replyId = await _agent.SendChatAsync(context is null ? text : $"{context}\n{text}", cts.Token);
            var reply = await _replyDone.Task.WaitAsync(cts.Token);

            SetState(State.Speaking);
            await _voice!.SpeakAsync(reply, cts.Token);
            SetState(State.Idle);
            spoke = !cts.IsCancellationRequested;
        }
        catch (OperationCanceledException) { SetState(State.Idle); }
        catch (Exception ex) { SetState(State.Idle, ex.Message); }
        finally
        {
            _replyId = null;
            _replyDone = null;
            if (_turnCts == cts) { _turnCts = null; cts.Dispose(); }
        }
        // Voice conversation: reopen the mic for your answer or follow-up (after a beat, so it doesn't hear itself).
        if (spoke && _voiceConversation && Panel.Visibility == Visibility.Visible)
        {
            await Task.Delay(350);
            if (_voiceConversation && _state == State.Idle && Panel.Visibility == Visibility.Visible) await ListenAndRespondAsync(followUp: true);
        }
    }

    private string MicHint() =>
        string.IsNullOrEmpty(_voice?.MicrophoneName) ? "" : $"Using {_voice.MicrophoneName}";

    private bool Ready()
    {
        if (_agent is not null && _voice is not null) return true;
        SetState(State.Idle, _notConfiguredReason ?? "The assistant isn't set up yet.");
        return false;
    }

    private void CancelTurn()
    {
        _turnCts?.Cancel();
        _turnCts = null;
        _ = _voice?.StopSpeakingAsync();
    }

    // ---- UI helpers -----------------------------------------------------------------

    private ChatLine AddLine(bool fromUser, string text)
    {
        var line = new ChatLine(fromUser, text);
        _lines.Add(line);
        while (_lines.Count > 30) _lines.RemoveAt(0);
        ScrollToEnd();
        return line;
    }

    private void ScrollToEnd()
    {
        if (_lines.Count > 0) Conversation.ScrollIntoView(_lines[^1]);
    }

    private void SetState(State state, string? message = null)
    {
        _state = state;
        (StatusIcon.Glyph, StatusText.Text) = state switch
        {
            State.Starting => ("", "Starting the assistant…"),
            State.Listening => ("", "Listening…"),
            State.Thinking => ("", "Thinking…"),
            State.Speaking => ("", "Speaking…"),
            _ => ("", message ?? "Tap Talk, or type below"),
        };
        TalkButtonText.Text = state == State.Listening ? "Done" : "Talk";
        StopButton.IsEnabled = state is State.Listening or State.Thinking or State.Speaking or State.Starting;

        if (state == State.Idle && Panel.Visibility == Visibility.Visible) _idleTimer.Start();
        else _idleTimer.Stop();
    }
}
