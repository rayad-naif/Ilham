using System.Runtime.CompilerServices;
using Ilham.Core;
using Ilham.Core.Commands;
using Ilham.Core.Emotions;
using Ilham.Core.Guardian;
using Ilham.Core.Initiative;
using Ilham.Core.Llm;
using Ilham.Core.Memory;
using Ilham.Core.Profile;
using Microsoft.Extensions.Time.Testing;

namespace Ilham.Tests;

/// <summary>An LLM stub that returns whatever script is queued, so brain behaviour is deterministic.</summary>
internal sealed class FakeLlm : ILlmClient
{
    private readonly Queue<string> _replies = new();
    public bool Fail { get; set; }
    public void Enqueue(string reply) => _replies.Enqueue(reply);

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("model offline");
        string reply = _replies.Count > 0 ? _replies.Dequeue() : "[mood:neutral] ok";
        foreach (var w in reply.Split(' ')) { await Task.Yield(); yield return w + " "; }
    }
    public Task<(bool Ok, string Status)> CheckAsync(CancellationToken ct = default) => Task.FromResult((true, "fake"));
}

public class BrainTests
{
    private static (IlhamBrain Brain, MemoryStore Mem, FakeLlm Llm, EmotionEngine Emo) Make()
    {
        var mem = new MemoryStore(":memory:");
        var emo = new EmotionEngine();
        var guardian = new HealthGuardian(emo);
        var llm = new FakeLlm();
        return (new IlhamBrain(llm, mem, emo, guardian), mem, llm, emo);
    }

    private static async Task<string> Collect(IAsyncEnumerable<ReplyChunk> chunks)
    {
        var text = "";
        await foreach (var c in chunks) text += c.TextDelta;
        return text;
    }

    [Fact]
    public async Task Respond_StreamsCleanText_AndAppliesMemoryOccasionMood()
    {
        var (brain, mem, llm, emo) = Make();
        llm.Enqueue("Theek hai [mood:happy] yaad rakhungi. [remember: he prefers tea over coffee] [occasion: Your birthday | 1998-03-14]");
        string shown = await Collect(brain.RespondAsync("my birthday is 14 march, I like tea"));

        Assert.DoesNotContain("[", shown);               // no tags leak to the user
        Assert.Contains("yaad rakhungi", shown);
        Assert.Contains(mem.Facts(), f => f.Text.Contains("tea"));
        Assert.Contains(mem.Occasions(), o => o is { Month: 3, Day: 14, Kind: "birthday" });
        Assert.Equal(Mood.Happy, emo.Dominant);
        Assert.Equal("user", mem.RecentMessages()[0].Role);
        Assert.Equal("assistant", mem.RecentMessages()[^1].Role);
    }

    [Fact]
    public async Task Respond_QueuesCommandForApproval_DoesNotRunUntilApproved()
    {
        var (brain, _, llm, _) = Make();
        string shell = OperatingSystem.IsWindows() ? "cmd" : "bash"; // use a shell the host actually has
        llm.Enqueue($"Sure [mood:teasing] [run:{shell}: echo hello-from-ilham]");
        await Collect(brain.RespondAsync("print hello"));

        var pending = brain.Pending.Single();
        Assert.Equal(shell, pending.Shell);
        Assert.Equal(Risk.Safe, pending.Risk);

        llm.Enqueue("[mood:happy] dekha, ho gaya");
        var (result, reaction) = await brain.ApproveAsync(pending.Id, TimeSpan.FromSeconds(10));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-from-ilham", result.Output);
        await Collect(reaction); // she reacts to the result
        Assert.Empty(brain.Pending);
    }

    [Fact]
    public async Task RejectedCommand_NeverRuns()
    {
        var (brain, _, llm, _) = Make();
        llm.Enqueue("[mood:neutral] [run:bash: echo should-not-run]");
        await Collect(brain.RespondAsync("do a thing"));
        var id = brain.Pending.Single().Id;
        Assert.True(brain.Reject(id));
        Assert.Empty(brain.Pending);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => brain.ApproveAsync(id, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ModelOffline_FallsBackToCannedGuardianLine()
    {
        var (brain, _, llm, _) = Make();
        llm.Fail = true;
        var profile = new UserProfile { Name = "Rayad" };
        var alert = new GuardianAlert(Concern.Break, 1, "50 min no break", GuardianAction.None, 0, "Rayad, break lo please.");
        string shown = await Collect(brain.SpeakGuardianAsync(alert, profile));
        Assert.Contains("break lo", shown); // canned line, because the model threw
    }
}

public class GuardianTests
{
    private static (HealthGuardian G, EmotionEngine E, FakeTimeProvider T) Make()
    {
        var t = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-01T14:00:00+05:00"));
        var e = new EmotionEngine(t);
        return (new HealthGuardian(e, t), e, t);
    }

    private static readonly TimeSpan Active = TimeSpan.Zero; // "at the keyboard"

    [Fact]
    public void Break_EscalatesStepByStepThenActs()
    {
        var (g, _, t) = Make();
        var profile = new UserProfile { BreakEveryMinutes = 50, AllowLock = true, DailyLimitMinutes = 0 };

        // Drive until the first break alert fires (somewhere around the 50-minute mark).
        GuardianAlert? first = null;
        for (int i = 0; i < 60 && first is null; i++) { first = g.Tick(profile, Active); t.Advance(TimeSpan.FromMinutes(1)); }
        Assert.NotNull(first);
        Assert.Equal(1, first!.Level);

        // Immediately again: no spam within the cooldown.
        Assert.Null(g.Tick(profile, Active));

        // Each escalation interval raises the level, up to acting.
        GuardianAlert? last = first;
        for (int lvl = 2; lvl <= 5; lvl++)
        {
            t.Advance(TimeSpan.FromMinutes(HealthGuardian.EscalateEveryMinutes + 1));
            var a = g.Tick(profile, Active);
            Assert.NotNull(a);
            Assert.Equal(lvl, a!.Level);
            last = a;
        }
        Assert.Equal(5, last!.Level);
        Assert.Equal(GuardianAction.Lock, last.Action);
        Assert.True(last.CountdownSeconds > 0);
    }

    [Fact]
    public void TakingABreak_ResetsTheCounter()
    {
        var (g, _, t) = Make();
        var profile = new UserProfile { BreakEveryMinutes = 50, DailyLimitMinutes = 0 };
        for (int i = 0; i < 55; i++) { g.Tick(profile, Active); t.Advance(TimeSpan.FromMinutes(1)); }
        Assert.True(g.Status(profile).MinutesSinceBreak >= 50);
        g.Tick(profile, HealthGuardian.BreakLength); // away for a break
        Assert.True(g.Status(profile).MinutesSinceBreak < 2);
    }

    [Fact]
    public void IdleTime_DoesNotCountAsScreenTime()
    {
        var (g, _, t) = Make();
        var profile = new UserProfile { DailyLimitMinutes = 0, BreakEveryMinutes = 999 };
        for (int i = 0; i < 30; i++) { g.Tick(profile, TimeSpan.FromMinutes(10)); t.Advance(TimeSpan.FromMinutes(1)); }
        Assert.True(g.ActiveMinutesToday < 1); // all idle
    }

    [Fact]
    public void Snooze_StopsNaggingUntilItExpires()
    {
        var (g, _, t) = Make();
        var profile = new UserProfile { BreakEveryMinutes = 1, DailyLimitMinutes = 0 };
        for (int i = 0; i < 3; i++) { g.Tick(profile, Active); t.Advance(TimeSpan.FromMinutes(1)); }
        g.Snooze(TimeSpan.FromMinutes(30));
        t.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(g.Tick(profile, Active)); // snoozed: silent
    }

    [Theory]
    [InlineData("2026-06-01T01:30:00+05:00", true)]  // after 00:30 bedtime
    [InlineData("2026-06-01T03:00:00+05:00", true)]  // middle of the night
    [InlineData("2026-06-01T23:30:00+05:00", false)] // before bedtime
    [InlineData("2026-06-01T15:00:00+05:00", false)] // afternoon
    public void Bedtime_WindowCrossesMidnight(string nowIso, bool expected)
    {
        var profile = new UserProfile { Bedtime = new TimeOnly(0, 30), WakeTime = new TimeOnly(7, 0) };
        Assert.Equal(expected, HealthGuardian.IsBedtime(DateTimeOffset.Parse(nowIso), profile));
    }
}

public class InitiativeTests
{
    private static (InitiativeEngine E, EmotionEngine Emo, FakeTimeProvider T) Make()
    {
        var t = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-01T14:00:00Z"));
        var emo = new EmotionEngine(t);
        return (new InitiativeEngine(emo, t), emo, t);
    }
    private static readonly UserProfile P = new() { Name = "Rayad", Proactive = true };

    [Fact]
    public void Silence_EscalatesFromCheckInToDemandToSulk()
    {
        var (e, _, t) = Make();
        e.NoteUserActivity();
        var seen = new List<InitiativeKind>();
        // Advance in chunks, collecting the escalation as silence grows.
        foreach (var mins in new[] { 4, 7, 14, 30 })
        {
            t.SetUtcNow(DateTimeOffset.Parse("2026-06-01T14:00:00Z").AddMinutes(mins));
            // give cooldown room each probe
            var move = e.Consider(P, atKeyboard: true);
            if (move is not null) seen.Add(move.Kind);
            t.Advance(InitiativeEngine.Cooldown + TimeSpan.FromSeconds(1));
        }
        Assert.Contains(InitiativeKind.Demand, seen);
        Assert.Contains(InitiativeKind.Sulk, seen);
        Assert.True(seen.IndexOf(InitiativeKind.Demand) < seen.IndexOf(InitiativeKind.Sulk)); // demand before sulk
    }

    [Fact]
    public void RespectsProactiveOff_AndEmptyChair()
    {
        var (e, _, t) = Make();
        e.NoteUserActivity();
        t.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(e.Consider(P with { Proactive = false }, atKeyboard: true)); // disabled
        Assert.Null(e.Consider(P, atKeyboard: false));                           // nobody there
    }

    [Fact]
    public void Cooldown_PreventsSpam()
    {
        var (e, _, t) = Make();
        e.NoteUserActivity();
        t.Advance(TimeSpan.FromMinutes(15));
        Assert.NotNull(e.Consider(P, atKeyboard: true));
        Assert.Null(e.Consider(P, atKeyboard: true)); // immediately after: silent
    }

    [Fact]
    public void BeingIgnored_RaisesAnnoyance_AndAnswerForgives()
    {
        var (e, emo, t) = Make();
        e.NoteUserActivity();
        t.Advance(TimeSpan.FromMinutes(14));
        e.Consider(P, atKeyboard: true); // a demand → annoyance up
        Assert.True(emo.Snapshot()[Mood.Annoyed] > 0.1);
        e.NoteUserActivity();            // he answers
        Assert.True(emo.Snapshot()[Mood.Happy] > 0.3);
    }
}
