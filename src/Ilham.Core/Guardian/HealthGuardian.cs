using Ilham.Core.Emotions;
using Ilham.Core.Profile;

namespace Ilham.Core.Guardian;

/// <summary>What she's worried about.</summary>
public enum Concern
{
    /// <summary>He's gone too long without a break.</summary>
    Break,
    /// <summary>It's past his bedtime.</summary>
    Bedtime,
    /// <summary>He's over his daily screen-time budget.</summary>
    DailyLimit,
}

/// <summary>What she will do (beyond talking) if ignored.</summary>
public enum GuardianAction
{
    /// <summary>Talk only.</summary>
    None,
    /// <summary>Lock the screen.</summary>
    Lock,
    /// <summary>Schedule a shutdown.</summary>
    Shutdown,
}

/// <summary>
/// One escalation step. Level 1 = gentle, 2 = nagging, 3 = arguing, 4 = final warning with a countdown,
/// 5 = acting (locking or shutting down, if allowed).
/// </summary>
public sealed record GuardianAlert(Concern Concern, int Level, string Situation, GuardianAction Action, int CountdownSeconds, string FallbackLine);

/// <summary>A snapshot for the UI.</summary>
public sealed record GuardianStatus(int ActiveMinutesToday, int MinutesSinceBreak, int BreakEveryMinutes, bool IsBedtime,
    DateTimeOffset? SnoozedUntil, IReadOnlyDictionary<Concern, int> Levels);

/// <summary>
/// The overprotective part of Ilham. Fed a tick every ~30 s with how long the user has been idle, it tracks real
/// (active) screen time, notices breaks, and escalates concerns step by step: reminders, nagging, arguing, a final
/// warning, then locking or shutting down. The override password snoozes her (and annoys her).
/// </summary>
public sealed class HealthGuardian(EmotionEngine emotions, TimeProvider? clock = null)
{
    /// <summary>Idle this long counts as "not at the PC".</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(2);

    /// <summary>Idle this long counts as having taken a break.</summary>
    public static readonly TimeSpan BreakLength = TimeSpan.FromMinutes(5);

    /// <summary>Minutes between escalation steps for the same concern.</summary>
    public const int EscalateEveryMinutes = 10;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<Concern, (int Level, DateTimeOffset At)> _levels = new();
    private DateTimeOffset? _lastTick, _snoozedUntil;
    private DateOnly _day;
    private double _activeToday, _sinceBreak;

    /// <summary>Restores counters after a restart (e.g. from the database).</summary>
    public void Restore(DateOnly day, double activeMinutesToday) { _day = day; _activeToday = activeMinutesToday; }

    /// <summary>Active minutes counted so far today.</summary>
    public double ActiveMinutesToday => _activeToday;

    /// <summary>The day the counters belong to (a "day" starts at the profile's wake time).</summary>
    public DateOnly Day => _day;

    /// <summary>Processes one tick. Returns an alert when a concern starts or escalates, otherwise null.</summary>
    /// <param name="profile">Current settings.</param>
    /// <param name="idle">How long since the last keyboard/mouse input.</param>
    public GuardianAlert? Tick(UserProfile profile, TimeSpan idle)
    {
        var now = _clock.GetLocalNow();
        DateOnly day = DayOf(now, profile);
        if (day != _day) { _day = day; _activeToday = 0; _levels.Clear(); }

        double elapsed = _lastTick is { } last ? Math.Clamp((now - last).TotalMinutes, 0, 5) : 0;
        _lastTick = now;

        if (idle >= BreakLength)
        {
            if (_sinceBreak >= profile.BreakEveryMinutes) emotions.Feel(Mood.Happy, 0.4, "he finally took the break I asked for");
            _sinceBreak = 0;
            _levels.Remove(Concern.Break);
        }
        if (idle < IdleThreshold)
        {
            _activeToday += elapsed;
            _sinceBreak += elapsed;
        }
        else return null; // not at the PC: nothing to nag about

        if (_snoozedUntil is { } s && now < s) return null;

        // Highest-priority concern first.
        if (IsBedtime(now, profile)) return Escalate(Concern.Bedtime, now, profile,
            $"It's {now:HH:mm}, past his {profile.Bedtime:HH:mm} bedtime, and he's still on the PC.");
        _levels.Remove(Concern.Bedtime);

        if (profile.DailyLimitMinutes > 0 && _activeToday >= profile.DailyLimitMinutes) return Escalate(Concern.DailyLimit, now, profile,
            $"He has used the PC for {Hours(_activeToday)} today, over his {Hours(profile.DailyLimitMinutes)} limit.");

        if (_sinceBreak >= profile.BreakEveryMinutes) return Escalate(Concern.Break, now, profile,
            $"He has been on the PC for {(int)_sinceBreak} minutes without a break (limit {profile.BreakEveryMinutes}).");
        return null;
    }

    /// <summary>Override password accepted: back off for a while (she won't like it).</summary>
    public void Snooze(TimeSpan duration)
    {
        _snoozedUntil = _clock.GetLocalNow() + duration;
        _levels.Clear();
        emotions.Feel(Mood.Annoyed, 0.5, "he used the override password to make me stop worrying");
    }

    /// <summary>Current status for the UI.</summary>
    public GuardianStatus Status(UserProfile profile) => new((int)_activeToday, (int)_sinceBreak, profile.BreakEveryMinutes,
        IsBedtime(_clock.GetLocalNow(), profile), _snoozedUntil > _clock.GetLocalNow() ? _snoozedUntil : null,
        _levels.ToDictionary(kv => kv.Key, kv => kv.Value.Level));

    /// <summary>True when the local time is inside [bedtime, wake), handling windows that cross midnight.</summary>
    public static bool IsBedtime(DateTimeOffset now, UserProfile p)
    {
        var t = TimeOnly.FromDateTime(now.DateTime);
        return p.Bedtime <= p.WakeTime ? t >= p.Bedtime && t < p.WakeTime : t >= p.Bedtime || t < p.WakeTime;
    }

    private GuardianAlert? Escalate(Concern concern, DateTimeOffset now, UserProfile profile, string situation)
    {
        int level = 1;
        if (_levels.TryGetValue(concern, out var prev))
        {
            if ((now - prev.At).TotalMinutes < EscalateEveryMinutes) return null; // give him time to listen
            level = Math.Min(prev.Level + 1, 5);
        }
        _levels[concern] = (level, now);

        GuardianAction action = GuardianAction.None;
        int countdown = 0;
        if (level >= 4)
        {
            action = concern == Concern.Bedtime && profile.AllowShutdown ? GuardianAction.Shutdown
                   : profile.AllowLock ? GuardianAction.Lock : GuardianAction.None;
            countdown = action == GuardianAction.None ? 0 : level == 4 ? 120 : 30;
        }

        (Mood mood, double amount) = level switch { 1 => (Mood.Worried, 0.3), 2 => (Mood.Worried, 0.5), 3 => (Mood.Annoyed, 0.6), _ => (Mood.Angry, 0.7) };
        emotions.Feel(mood, amount, concern switch
        {
            Concern.Break => $"he's ignoring my break reminders (step {level})",
            Concern.Bedtime => $"he's awake past bedtime (step {level})",
            _ => $"he's over his screen-time limit (step {level})",
        });
        return new GuardianAlert(concern, level, situation, action, countdown, FallbackLine(concern, level, profile.Name));
    }

    /// <summary>Canned lines used if the language model is unavailable.</summary>
    private static string FallbackLine(Concern c, int level, string name) => (c, level) switch
    {
        (Concern.Break, 1) => $"{name}, 50 minutes ho gaye. Uth jao, paani piyo, aankhon ko aaraam do. 5 minute. Please?",
        (Concern.Break, 2) => $"Main ne kaha tha break lo. Screen bhaag nahi rahi, tum bhi thoda sa bhaag lo. 🙄",
        (Concern.Break, 3) => $"Seriously {name}? Teesri baar keh rahi hoon. Your eyes and back are not unlimited resources!",
        (Concern.Break, _) => $"Bas. Last warning. Break lo warna main khud screen lock kar doongi. 😤",
        (Concern.Bedtime, 1) => $"{name}, sone ka time ho gaya hai. Kal ke liye energy bachao. Goodnight bolo mujhe?",
        (Concern.Bedtime, 2) => $"Abhi tak jaag rahe ho? 'Bas 5 minute' wala jhoot main pehchanti hoon. 😒",
        (Concern.Bedtime, 3) => $"I'm genuinely worried now. Neend poori nahi hogi toh kal sab kharab. Band karo please.",
        (Concern.Bedtime, _) => $"That's it. Main PC band kar rahi hoon. Countdown shuru. Mujhse naraz ho jao, but so jao. 💤",
        (_, 1) => $"Aaj bohat screen time ho gaya hai, {name}. Thoda bahar ki duniya bhi dekh lo?",
        (_, 2) => $"Limit cross ho chuki hai. Main sirf yaad dila rahi hoon… dobara. 🙂",
        (_, 3) => $"Ab main seriously naraz hone wali hoon. Screen se break, abhi.",
        _ => $"Final warning. Main lock kar rahi hoon. Override password hai tumhare paas, but I'll remember. 😤",
    };

    private static DateOnly DayOf(DateTimeOffset now, UserProfile p)
    {
        var d = DateOnly.FromDateTime(now.DateTime);
        return TimeOnly.FromDateTime(now.DateTime) < p.WakeTime ? d.AddDays(-1) : d; // 1 AM still belongs to "yesterday"
    }

    private static string Hours(double minutes) => minutes >= 60 ? $"{minutes / 60:0.#} h" : $"{(int)minutes} min";
}
