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
    private readonly Ilham.Core.Console.ConsoleProcessor _console;
    private readonly SemaphoreSlim _turn = new(1, 1); // one speaker at a time; your messages are never dropped
    private readonly Services.IVoice _voice;
    private CancellationTokenSource? _listenCts;

    /// <summary>Builds the view model and its services under <paramref name="dataDir"/>.</summary>
    /// <param name="dataDir">Folder for her private data.</param>
    public MainViewModel(string dataDir)
    {
        _svc = new IlhamServices(dataDir, _http);
        Profile = _svc.Profiles.Load();
        _voice = Services.Voice.Create(() => Profile.TtsUrl); // natural cloud voice when a /tts URL is set
        _console = new Ilham.Core.Console.ConsoleProcessor(_svc.Memory, _svc.Profiles, _svc.Vault,
            model => Task.Run(() => _svc.Llm.CheckAsync()).GetAwaiter().GetResult());
        ConsoleLines.Add("Console ready. Type /help, or a shell command. You're the operator here.");
        _speakEnabled = _voice.CanSpeak; // she talks by default where speech is available

        // If you drop a realistic portrait at %LocalAppData%/Ilham/portrait.(png|jpg|jpeg|webp), she wears
        // that real face; otherwise the drawn avatar is used. Her picture, shown unchanged.
        foreach (var ext in new[] { "png", "jpg", "jpeg", "webp" })
        {
            string pf = Path.Combine(dataDir, "portrait." + ext);
            if (File.Exists(pf)) { try { Portrait = new Avalonia.Media.Imaging.Bitmap(pf); } catch { } break; }
        }
        _showPortrait = Portrait is not null;

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

    /// <summary>A realistic portrait of her, if you provided one (else null → the drawn avatar is used).</summary>
    public Avalonia.Media.Imaging.Bitmap? Portrait { get; }

    /// <summary>True when a portrait image is available to show.</summary>
    public bool HasPortrait => Portrait is not null;

    private bool _showPortrait;
    /// <summary>Whether to show the realistic portrait (true) or the animated drawn face (false).</summary>
    public bool ShowPortrait { get => _showPortrait; set { _showPortrait = value; Raise(); Raise(nameof(ShowFace)); } }

    /// <summary>The inverse of <see cref="ShowPortrait"/> — the animated face is shown when the portrait isn't.</summary>
    public bool ShowFace => !_showPortrait;

    /// <summary>Flips between the realistic portrait and the animated face.</summary>
    public void ToggleFace() { if (HasPortrait) ShowPortrait = !ShowPortrait; }

    /// <summary>Lines shown in the built-in console (newest appended).</summary>
    public ObservableCollection<string> ConsoleLines { get; } = [];

    private string _consoleInput = "";
    /// <summary>The console input box.</summary>
    public string ConsoleInput { get => _consoleInput; set { _consoleInput = value; Raise(); } }

    private bool _consoleVisible;
    /// <summary>Whether the console panel is shown.</summary>
    public bool ConsoleVisible { get => _consoleVisible; set { _consoleVisible = value; Raise(); } }

    /// <summary>Shows/hides the console.</summary>
    public void ToggleConsole() => ConsoleVisible = !ConsoleVisible;

    /// <summary>Runs the current console line and appends the output.</summary>
    public async Task RunConsoleAsync()
    {
        string line = ConsoleInput.Trim();
        if (line.Length == 0) return;
        ConsoleInput = "";
        ConsoleLines.Add("› " + line);
        string output;
        try { output = await _console.RunAsync(line); }
        catch (Exception ex) { output = "Error: " + ex.Message; }
        foreach (string l in output.Replace("\r\n", "\n").Split('\n')) ConsoleLines.Add(l);
        while (ConsoleLines.Count > 400) ConsoleLines.RemoveAt(0); // keep it bounded

        // Reflect any settings/outfit the console just changed.
        var updated = _svc.Profiles.Load();
        if (updated.Outfit != Profile.Outfit) OutfitChanged?.Invoke(updated.Outfit);
        Profile = updated;
    }

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
    /// <summary>Raised with live speech amplitude for lip-sync (driven by TTS).</summary>
    public event Action<double>? Amplitude;
    /// <summary>Raised when she changes outfit, so the window applies it to the avatar.</summary>
    public event Action<string>? OutfitChanged;

    /// <summary>Whether she speaks her replies aloud.</summary>
    public bool CanSpeak => _voice.CanSpeak;
    /// <summary>Whether listening (mic) is available.</summary>
    public bool CanListen => _voice.CanListen;

    private bool _speakEnabled;
    /// <summary>When on, she reads her replies aloud.</summary>
    public bool SpeakEnabled { get => _speakEnabled; set { _speakEnabled = value; Raise(); if (!value) _voice.StopSpeaking(); } }

    private bool _micEnabled;
    /// <summary>When on, she listens on the mic and sends what she hears.</summary>
    public bool MicEnabled
    {
        get => _micEnabled;
        set { if (_micEnabled == value) return; _micEnabled = value; Raise(); if (value) StartListening(); else StopListening(); }
    }

    private void StartListening()
    {
        if (!_voice.CanListen) { _micEnabled = false; Raise(nameof(MicEnabled)); return; }
        _listenCts = new CancellationTokenSource();
        var ct = _listenCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                string? heard = await _voice.ListenOnceAsync(ct);
                if (ct.IsCancellationRequested) break;
                if (!string.IsNullOrWhiteSpace(heard))
                    await Dispatcher.UIThread.InvokeAsync(async () => { Draft = heard!; await SendAsync(); });
            }
        }, ct);
    }

    private void StopListening() { _listenCts?.Cancel(); _listenCts = null; }

    private void WearOutfit(string outfit, bool persist)
    {
        if (!Ilham.Core.Outfits.OutfitWardrobe.IsKnown(outfit)) return;
        Profile = Profile with { Outfit = outfit };
        if (persist) _svc.Profiles.Save(Profile);
        OutfitChanged?.Invoke(outfit);
    }

    /// <summary>Sends the current draft to Ilham. Your message is never dropped — it waits its turn if she's mid-sentence.</summary>
    public async Task SendAsync()
    {
        string text = Draft.Trim();
        if (text.Length == 0) return;
        Draft = "";
        Messages.Add(new ChatLineVm(false, text));
        _svc.Initiative.NoteUserActivity();
        await _turn.WaitAsync();
        try { await StreamCore(brain => brain.RespondAsync(text)); }
        catch (Exception ex) { Add(true).Text = $"(Kuch gadbad ho gayi: {ex.Message})"; }
        finally { _turn.Release(); _svc.Initiative.NoteSpoke(); }
    }

    private async Task GreetAsync()
    {
        await RefreshStatusAsync();
        // She dresses herself for the day (festive if there's an occasion today).
        var today = DateOnly.FromDateTime(DateTime.Now);
        bool occasionToday = Ilham.Core.Occasions.OccasionCalendar
            .Upcoming(Profile, _svc.Memory.Occasions(), today).Any(o => o.DaysAway == 0);
        string dayOutfit = Ilham.Core.Outfits.OutfitWardrobe.ChooseForDay(today, Ilham.Core.Emotions.Mood.Happy, occasionToday);
        WearOutfit(dayOutfit, persist: true);

        string hi = Profile.Name is { Length: > 0 } n ? $"Aa gaye {n}? Main wait kar rahi thi. 💜" : "Hey. Main yahin hoon.";
        Messages.Add(new ChatLineVm(true, hi));
        SetMood("happy");
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            // Health alerts act even while she's mid-turn; but she only *speaks* if nobody else is talking.
            var alert = _svc.Guardian.Tick(Profile, _svc.PcActions.Idle());
            if (alert is { Action: not GuardianAction.None })
            {
                if (alert.Action == GuardianAction.Lock) _svc.PcActions.Lock();
                else if (alert.Action == GuardianAction.Shutdown) _svc.PcActions.ScheduleShutdown(alert.CountdownSeconds, "Ilham: so jao please.");
            }
            _svc.PersistCounters();

            if (!await _turn.WaitAsync(0)) return; // someone's talking (you, or her) — don't interrupt or block you
            try
            {
                if (alert is not null)
                    await StreamCore(brain => brain.SpeakGuardianAsync(alert, Profile));
                else if (_svc.Initiative.Consider(Profile, _svc.PcActions.Idle() < TimeSpan.FromMinutes(3)) is { } move)
                    await StreamCore(brain => brain.SpeakUnpromptedAsync(move.Instruction, move.Fallback, move.Mood, Profile));
            }
            finally { _turn.Release(); _svc.Initiative.NoteSpoke(); }
        }
        catch { /* heartbeat must never crash the UI */ }
    }

    /// <summary>Approves a pending command, runs it, and lets her react.</summary>
    /// <param name="id">The command id.</param>
    public async Task ApproveAsync(string id)
    {
        var vm = Pending.FirstOrDefault(p => p.Id == id);
        if (vm is not null) Pending.Remove(vm);
        await _turn.WaitAsync();
        try
        {
            var (_, reaction) = await _svc.Brain.ApproveAsync(id, TimeSpan.FromMinutes(2));
            await StreamChunks(reaction);
        }
        catch (KeyNotFoundException) { /* already handled */ }
        catch (Exception ex) { Add(true).Text = $"(Command chalane mein dikkat: {ex.Message})"; }
        finally { _turn.Release(); }
    }

    /// <summary>Rejects a pending command.</summary>
    /// <param name="id">The command id.</param>
    public void Reject(string id)
    {
        var vm = Pending.FirstOrDefault(p => p.Id == id);
        if (vm is not null) { Pending.Remove(vm); _svc.Brain.Reject(id); }
    }

    /// <summary>Runs one streamed reply (caller holds the turn lock).</summary>
    private async Task StreamCore(Func<IlhamBrain, IAsyncEnumerable<ReplyChunk>> start)
    {
        var brain = await _svc.ActiveBrainAsync();
        await StreamChunks(start(brain));
    }

    private async Task StreamChunks(IAsyncEnumerable<ReplyChunk> chunks)
    {
        ChatLineVm? bubble = null;
        bool gotText = false;
        string fullText = "";
        string? mood = null;
        await foreach (var c in chunks)
        {
            if (c.Mood is { } m) { mood = m.ToString().ToLowerInvariant(); SetMood(mood); }
            if (c.TextDelta.Length > 0)
            {
                gotText = true;
                bubble ??= Add(true);
                bubble.Text += c.TextDelta;
                fullText += c.TextDelta;
            }
            if (c.Done && c.Final is { } f)
            {
                if (f.Outfit is { } outfit) WearOutfit(outfit, persist: false); // she already saved it; just show it
                foreach (var cmd in _svc.Brain.Pending)
                    if (Pending.All(p => p.Id != cmd.Id))
                        Pending.Add(new PendingCommandVm(cmd.Id, cmd.Shell, cmd.Command, cmd.Risk.ToString().ToLowerInvariant(), string.Join(" · ", cmd.Reasons)));
            }
        }

        // She produced nothing — don't leave you staring at silence. Explain why.
        if (!gotText)
        {
            var (ok, status) = await _svc.Llm.CheckAsync();
            Add(true).Text = ok
                ? "(Jawab aaya hi nahi — model se baat adhoori reh gayi. Dobara try karo; console mein /model se status dekho.)"
                : $"(Main abhi soch nahi paa rahi: {status})";
        }
        else if (SpeakEnabled && fullText.Length > 0)
            _voice.Speak(fullText, Profile.Language, mood ?? "neutral", a => Amplitude?.Invoke(a));
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
    public void Dispose() { StopListening(); _voice.StopSpeaking(); _heartbeat.Stop(); _svc.Dispose(); _http.Dispose(); }
}
