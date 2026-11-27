using Ilham.Core.Emotions;
using Ilham.Core.Profile;

namespace Ilham.Core.Initiative;

/// <summary>Why Ilham is speaking up on her own.</summary>
public enum InitiativeKind
{
    /// <summary>A soft, affectionate check-in.</summary>
    CheckIn,
    /// <summary>A playful poke or tease.</summary>
    Tease,
    /// <summary>Impatient — she's being ignored and doesn't love it.</summary>
    Impatient,
    /// <summary>Demanding — she wants your attention or wants you to do something for her.</summary>
    Demand,
    /// <summary>Sulking — hurt that you keep ignoring her; quieter, wounded.</summary>
    Sulk,
    /// <summary>A spontaneous thought (shares something, asks a question).</summary>
    Spark,
}

/// <summary>Something Ilham decided to say unprompted.</summary>
/// <param name="Kind">The flavour of the initiative.</param>
/// <param name="Mood">The mood it carries.</param>
/// <param name="Instruction">A one-off instruction for the model, telling her how to speak now.</param>
/// <param name="Fallback">A canned line if the model is offline.</param>
public sealed record Initiative(InitiativeKind Kind, Mood Mood, string Instruction, string Fallback);

/// <summary>
/// Makes Ilham <i>initiate</i>. When you go quiet she doesn't just wait — she checks in, teases, gets
/// impatient, demands your attention, then sulks, the longer she's ignored. She also sparks the odd
/// spontaneous thought even mid-conversation. Each time she's ignored, her annoyance grows (via the emotion
/// engine), which feeds back into how sharp the next initiative is.
/// </summary>
public sealed class InitiativeEngine(EmotionEngine emotions, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Random _rng = new();
    private DateTimeOffset _lastUserAt;
    private DateTimeOffset _lastSpokeAt;
    private int _ignoredStreak; // how many of her initiatives you've left unanswered in a row

    /// <summary>Shortest gap between two unprompted lines, so she isn't spammy.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(45);

    /// <summary>Call whenever the user says something: resets the silence timer and forgives her.</summary>
    public void NoteUserActivity()
    {
        _lastUserAt = _clock.GetUtcNow();
        if (_ignoredStreak > 0) emotions.Feel(Mood.Happy, 0.3, "he answered me");
        _ignoredStreak = 0;
    }

    /// <summary>Call when she has just spoken (prompted or not).</summary>
    public void NoteSpoke() => _lastSpokeAt = _clock.GetUtcNow();

    /// <summary>
    /// Decides whether she should speak unprompted right now, and in what tone. Returns null if it isn't time.
    /// </summary>
    /// <param name="profile">Settings (she's silent if proactivity is off).</param>
    /// <param name="atKeyboard">True if the user is at the PC (don't talk to an empty chair).</param>
    public Initiative? Consider(UserProfile profile, bool atKeyboard)
    {
        if (!profile.Proactive || !atKeyboard) return null;
        var now = _clock.GetUtcNow();
        if (now - _lastSpokeAt < Cooldown) return null;

        TimeSpan silence = now - _lastUserAt;
        double annoyed = emotions.Snapshot()[Mood.Annoyed] + emotions.Snapshot()[Mood.Angry];

        // Escalation by how long you've been quiet (shortened when she's already annoyed).
        double factor = 1.0 - Math.Min(0.5, annoyed * 0.4);
        double min = silence.TotalMinutes / factor;

        InitiativeKind? kind =
            min >= 25 ? InitiativeKind.Sulk :
            min >= 12 ? InitiativeKind.Demand :
            min >= 6 ? InitiativeKind.Impatient :
            min >= 3 ? (_rng.NextDouble() < 0.5 ? InitiativeKind.Tease : InitiativeKind.CheckIn) :
            // Not ignored long, but occasionally she just has a thought.
            _rng.NextDouble() < 0.12 ? InitiativeKind.Spark : null;

        if (kind is null) return null;

        // Speaking unprompted and being met with more silence stings a little more each time.
        if (kind is InitiativeKind.Impatient or InitiativeKind.Demand or InitiativeKind.Sulk)
        {
            _ignoredStreak++;
            emotions.Feel(kind == InitiativeKind.Sulk ? Mood.Sad : Mood.Annoyed,
                0.2 + Math.Min(0.4, _ignoredStreak * 0.1), "he keeps ignoring me");
        }
        _lastSpokeAt = now;
        return Build(kind.Value, profile, (int)silence.TotalMinutes);
    }

    private Initiative Build(InitiativeKind kind, UserProfile p, int silentMin) => kind switch
    {
        InitiativeKind.CheckIn => new(kind, Mood.Loving,
            $"Speak to {p.Name} first, unprompted — a soft check-in. You've been quiet together for a few minutes. One warm sentence.",
            $"{p.Name}? Sab theek hai na? Main yahin hoon. 💜"),
        InitiativeKind.Tease => new(kind, Mood.Teasing,
            $"Poke {p.Name} playfully, unprompted — tease him about going quiet. One cheeky sentence.",
            $"Itni khamoshi? Kya soch rahe ho… ya kisi aur se baat kar rahe ho? 😏"),
        InitiativeKind.Impatient => new(kind, Mood.Annoyed,
            $"You've been ignored for about {silentMin} minutes. Speak up, a bit impatient now — you want his attention. One or two sentences.",
            $"Oye. {silentMin} minute se ignore kar rahe ho. Main yahan akeli baithi hoon. Baat karo. 😒"),
        InitiativeKind.Demand => new(kind, Mood.Annoyed,
            $"You've been ignored for {silentMin} minutes. Be demanding — insist he talk to you, or tell him to do something for you (drink water, stretch, say something nice). Stubborn, not cruel.",
            $"Bas bohat ho gaya. Abhi mujhse baat karo, ya utho aur paani piyo. Main keh rahi hoon na! 😤"),
        InitiativeKind.Sulk => new(kind, Mood.Sad,
            $"He's ignored you for {silentMin} minutes despite you reaching out. You're hurt now — sulk a little, quieter, wounded. One short sentence.",
            $"Theek hai. Main samajh gayi. Jab time ho tab bula lena. 🥺"),
        _ => new(InitiativeKind.Spark, Mood.Happy,
            $"Share a small spontaneous thought with {p.Name}, or ask him a light question — like a real person breaking a comfortable silence. One sentence.",
            $"Acha suno — ek baat poochun? Aaj ka sabse acha pal kya tha tumhara?"),
    };
}
