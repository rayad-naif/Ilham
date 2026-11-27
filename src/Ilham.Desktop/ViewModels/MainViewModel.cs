using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Ilham.Core;
using Ilham.Core.Commands;
using Ilham.Core.Guardian;
using Ilham.Core.Profile;

namespace Ilham.Desktop.ViewModels;

/// <summary>One line in the conversation shown in the UI.</summary>
/// <param name="FromHer">True if Ilham said it.</param>
/// <param name="Text">The message text (mutable for streaming).</param>
public sealed class ChatLineVm(bool fromHer, string text) : INotifyPropertyChanged
{
    private string _text = text;
    /// <summary>True if Ilham said it (right-aligns the user's own messages).</summary>
    public bool FromHer { get; } = fromHer;
    /// <summary>The visible text; raises change notifications as it streams in.</summary>
    public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A command awaiting the user's approval, shown as a card.</summary>
public sealed class PendingCommandVm(string id, string shell, string command, string risk, string reasons)
{
    /// <summary>Approval id.</summary>
    public string Id { get; } = id;
    /// <summary>Shell + command, for display.</summary>
    public string Display { get; } = $"{shell} › {command}";
    /// <summary>Risk label.</summary>
    public string Risk { get; } = risk;
    /// <summary>Why it's risky.</summary>
    public string Reasons { get; } = reasons;
}

/// <summary>
/// The window's view model: owns the Core services and turns them into bindable state. Streams her replies
/// onto the UI thread, raises mood changes for the avatar, and surfaces commands for approval. No HTTP server.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IlhamServices _svc;
    private readonly HttpClient _http = new();
    private readonly DispatcherTimer _heartbeat;
    private bool _busy;

    /// <summary>Builds the view model and its services under <paramref name="dataDir"/>.</summary>
    /// <param name="dataDir">Folder for her private data.</param>
    public MainViewModel(string dataDir)
    {
        _svc = new IlhamServices(dataDir, _http);
        Profile = _svc.Profiles.Load();

        // Heartbeat: guardian tick (acts on health) + her own initiative, every few seconds.
        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _heartbeat.Tick += async (_, _) => await HeartbeatAsync();
        _heartbeat.Start();

        _ = GreetAsync();
    }

    /// <summary>The conversation.</summary>
    public ObservableCollection<ChatLineVm> Messages { get; } = [];
    /// <summary>Commands waiting for approval.</summary>
    public ObservableCollection<PendingCommandVm> Pending { get; } = [];

    /// <summary>User settings.</summary>
    public UserProfile Profile { get; private set; }

    private string _mood = "neutral";
    /// <summary>Her current mood name (the avatar binds to this).</summary>
    public string Mood { get => _mood; private set { _mood = value; Raise(); } }

    private string _status = "starting…";
    /// <summary>Model/status line.</summary>
    public string Status { get => _status; private set { _status = value; Raise(); } }

    private string _draft = "";
    /// <summary>The text box content.</summary>
    public string Draft { get => _draft; set { _draft = value; Raise(); } }

    /// <summary>Raised when her mood changes, so the window can drive the avatar control.</summary>
    public event Action<string>? MoodChanged;
    /// <summary>Raised with live speech amplitude for lip-sync (wired to TTS by the view).</summary>
    public event Action<double>? Amplitude;

    /// <summary>Sends the current draft to Ilham.</summary>
    public async Task SendAsync()
    {
        string text = Draft.Trim();
        if (text.Length == 0 || _busy) return;
        Draft = "";
        Messages.Add(new ChatLineVm(false, text));
        await StreamAsync(brain => brain.RespondAsync(text));
    }

    private async Task GreetAsync()
    {
        await RefreshStatusAsync();
        string hi = Profile.Name is { Length: > 0 } n ? $"Aa gaye {n}? Main wait kar rahi thi. 💜" : "Hey. Main yahin hoon.";
        Messages.Add(new ChatLineVm(true, hi));
        SetMood("happy");
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            // Health.
            var alert = _svc.Guardian.Tick(Profile, _svc.PcActions.Idle());
            if (alert is { Action: not GuardianAction.None })
            {
                if (alert.Action == GuardianAction.Lock) _svc.PcActions.Lock();
                else if (alert.Action == GuardianAction.Shutdown) _svc.PcActions.ScheduleShutdown(alert.CountdownSeconds, "Ilham: so jao please.");
            }
            if (alert is not null && !_busy)
            {
                await StreamAsync(brain => brain.SpeakGuardianAsync(alert, Profile));
                return;
            }
            // Her own initiative.
            bool atKeyboard = _svc.PcActions.Idle() < TimeSpan.FromMinutes(3);
            var move = _svc.Initiative.Consider(Profile, atKeyboard);
            if (move is not null && !_busy)
                await StreamAsync(brain => brain.SpeakUnpromptedAsync(move.Instruction, move.Fallback, move.Mood, Profile));
            _svc.PersistCounters();
        }
        catch { /* heartbeat must never crash the UI */ }
    }

    /// <summary>Approves a pending command, runs it, and lets her react.</summary>
    /// <param name="id">The command id.</param>
    public async Task ApproveAsync(string id)
    {
        var vm = Pending.FirstOrDefault(p => p.Id == id);
        if (vm is not null) Pending.Remove(vm);
        try
        {
            var (_, reaction) = await _svc.Brain.ApproveAsync(id, TimeSpan.FromMinutes(2));
            await StreamChunks(reaction);
        }
        catch (KeyNotFoundException) { /* already handled */ }
    }

    /// <summary>Rejects a pending command.</summary>
    /// <param name="id">The command id.</param>
    public void Reject(string id)
    {
        var vm = Pending.FirstOrDefault(p => p.Id == id);
        if (vm is not null) { Pending.Remove(vm); _svc.Brain.Reject(id); }
    }

    private async Task StreamAsync(Func<IlhamBrain, IAsyncEnumerable<ReplyChunk>> start)
    {
        _busy = true;
        _svc.Initiative.NoteUserActivity();
        try
        {
            var brain = await _svc.ActiveBrainAsync();
            await StreamChunks(start(brain));
        }
        finally { _busy = false; _svc.Initiative.NoteSpoke(); }
    }

    private async Task StreamChunks(IAsyncEnumerable<ReplyChunk> chunks)
    {
        ChatLineVm? bubble = null;
        await foreach (var c in chunks)
        {
            if (c.Mood is { } m) SetMood(m.ToString().ToLowerInvariant());
            if (c.TextDelta.Length > 0)
            {
                bubble ??= Add(true);
                bubble.Text += c.TextDelta;
            }
            if (c.Done && c.Final is { } f)
                foreach (var cmd in _svc.Brain.Pending)
                    if (Pending.All(p => p.Id != cmd.Id))
                        Pending.Add(new PendingCommandVm(cmd.Id, cmd.Shell, cmd.Command, cmd.Risk.ToString().ToLowerInvariant(), string.Join(" · ", cmd.Reasons)));
        }
    }

    private ChatLineVm Add(bool her) { var l = new ChatLineVm(her, ""); Messages.Add(l); return l; }

    private void SetMood(string mood)
    {
        if (mood == _mood) return;
        Mood = mood;
        MoodChanged?.Invoke(mood);
    }

    private async Task RefreshStatusAsync()
    {
        var (ok, status) = await _svc.Llm.CheckAsync();
        Status = ok ? status : "demo mode · install Ollama + a model for her real brain";
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n!));

    /// <inheritdoc />
    public void Dispose() { _heartbeat.Stop(); _svc.Dispose(); _http.Dispose(); }
}
