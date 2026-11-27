namespace Ilham.Core.Emotions;

/// <summary>Ilham's moods. The avatar's face and her tone follow whichever is strongest.</summary>
public enum Mood
{
    /// <summary>Calm, baseline.</summary>
    Neutral,
    /// <summary>Cheerful and content.</summary>
    Happy,
    /// <summary>Warm, affectionate, soft.</summary>
    Loving,
    /// <summary>Playful, sarcastic, joking.</summary>
    Teasing,
    /// <summary>Energised and eager (e.g. a birthday).</summary>
    Excited,
    /// <summary>Concerned for his wellbeing.</summary>
    Worried,
    /// <summary>Irritated he's ignoring her.</summary>
    Annoyed,
    /// <summary>Upset, protective anger.</summary>
    Angry,
    /// <summary>Hurt or low.</summary>
    Sad,
}

/// <summary>Something that changed how she feels, kept so she can say <i>why</i>.</summary>
/// <param name="Mood">The feeling it pushed.</param>
/// <param name="Amount">How strongly (0..1).</param>
/// <param name="Reason">Plain-language cause, e.g. "he skipped two breaks".</param>
/// <param name="At">When it happened.</param>
public sealed record Feeling(Mood Mood, double Amount, string Reason, DateTimeOffset At);

/// <summary>
/// Ilham's emotional state: an intensity in [0, 1] for every mood that decays exponentially toward a
/// resting baseline. Events push intensities up; time calms her down. The strongest mood drives her
/// expression and tone, and the recent reasons are fed into her prompt so her feelings have causes.
/// </summary>
public sealed class EmotionEngine
{
    /// <summary>How long it takes an emotion to fall halfway back to its baseline.</summary>
    public static readonly TimeSpan HalfLife = TimeSpan.FromMinutes(40);

    // Her resting personality: affectionate, playful, a little cheerful.
    private static readonly Dictionary<Mood, double> Baseline = new()
    {
        [Mood.Neutral] = 0.20, [Mood.Happy] = 0.30, [Mood.Loving] = 0.28, [Mood.Teasing] = 0.25,
        [Mood.Excited] = 0.05, [Mood.Worried] = 0.05, [Mood.Annoyed] = 0.03, [Mood.Angry] = 0.0, [Mood.Sad] = 0.0,
    };

    private readonly Dictionary<Mood, double> _intensity;
    private readonly List<Feeling> _recent = [];
    private readonly TimeProvider _clock;
    private DateTimeOffset _lastUpdate;

    /// <summary>Creates an engine at her resting baseline.</summary>
    /// <param name="clock">Time source (injectable for tests).</param>
    public EmotionEngine(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _intensity = new Dictionary<Mood, double>(Baseline);
        _lastUpdate = _clock.GetUtcNow();
    }

    /// <summary>Pushes a mood up by <paramref name="amount"/> and records why.</summary>
    /// <param name="mood">Which feeling.</param>
    /// <param name="amount">Strength in [0, 1]; intensities saturate at 1.</param>
    /// <param name="reason">Why she feels this way.</param>
    public void Feel(Mood mood, double amount, string reason)
    {
        lock (_intensity)
        {
            Decay();
            amount = Math.Clamp(amount, 0, 1);
            // Saturating add: strong feelings grow slower as they approach 1.
            _intensity[mood] += (1 - _intensity[mood]) * amount;
            // Opposite feelings soften each other a little.
            foreach (Mood other in Opposites(mood)) _intensity[other] *= 1 - amount * 0.5;
            _recent.Add(new Feeling(mood, amount, reason, _clock.GetUtcNow()));
            if (_recent.Count > 20) _recent.RemoveAt(0);
        }
    }

    /// <summary>Current intensity of every mood, after decay.</summary>
    public IReadOnlyDictionary<Mood, double> Snapshot()
    {
        lock (_intensity) { Decay(); return new Dictionary<Mood, double>(_intensity); }
    }

    /// <summary>The strongest mood right now.</summary>
    public Mood Dominant => Snapshot().MaxBy(kv => kv.Value).Key;

    /// <summary>The causes of her feelings in the last few hours, newest first.</summary>
    public IReadOnlyList<Feeling> RecentReasons(TimeSpan? window = null)
    {
        var since = _clock.GetUtcNow() - (window ?? TimeSpan.FromHours(3));
        lock (_intensity) return _recent.Where(f => f.At >= since).Reverse().ToList();
    }

    /// <summary>A short description for her system prompt, e.g. "mostly worried (70%), also loving (45%) — because: …".</summary>
    public string Describe()
    {
        var top = Snapshot().Where(kv => kv.Key != Mood.Neutral).OrderByDescending(kv => kv.Value).Take(3).ToList();
        string feelings = string.Join(", also ", top.Select((kv, i) => $"{(i == 0 ? "mostly " : "")}{kv.Key.ToString().ToLowerInvariant()} ({kv.Value:P0})"));
        var reasons = RecentReasons().Take(4).Select(f => f.Reason).ToList();
        return reasons.Count == 0 ? $"You feel {feelings}." : $"You feel {feelings}. Recent reasons: {string.Join("; ", reasons)}.";
    }

    /// <summary>Exponential decay toward baseline: x ← b + (x − b)·2^(−Δt / halfLife).</summary>
    private void Decay()
    {
        var now = _clock.GetUtcNow();
        double factor = Math.Pow(0.5, (now - _lastUpdate).TotalMinutes / HalfLife.TotalMinutes);
        foreach (Mood m in Enum.GetValues<Mood>())
            _intensity[m] = Baseline[m] + (_intensity[m] - Baseline[m]) * factor;
        _lastUpdate = now;
    }

    private static Mood[] Opposites(Mood m) => m switch
    {
        Mood.Happy or Mood.Excited or Mood.Loving => [Mood.Sad, Mood.Angry, Mood.Annoyed],
        Mood.Angry or Mood.Annoyed => [Mood.Happy, Mood.Teasing],
        Mood.Sad => [Mood.Happy, Mood.Excited, Mood.Teasing],
        Mood.Worried => [Mood.Teasing, Mood.Excited],
        _ => [],
    };

    /// <summary>Parses a mood name from her reply tags; unknown names map to <see cref="Mood.Neutral"/>.</summary>
    public static Mood Parse(string? name) =>
        Enum.TryParse(name?.Trim(), ignoreCase: true, out Mood m) ? m : Mood.Neutral;
}
