using System.Runtime.CompilerServices;
using System.Text;
using Ilham.Core.Commands;
using Ilham.Core.Emotions;
using Ilham.Core.Guardian;
using Ilham.Core.Llm;
using Ilham.Core.Memory;
using Ilham.Core.Occasions;
using Ilham.Core.Persona;
using Ilham.Core.Profile;

namespace Ilham.Core;

/// <summary>A piece of Ilham's streamed reply as it arrives.</summary>
/// <param name="TextDelta">New visible text since the last chunk (tags already stripped).</param>
/// <param name="Mood">Her mood once known.</param>
/// <param name="Done">True on the final chunk, when <see cref="Final"/> is set.</param>
/// <param name="Final">The fully parsed reply (only on the last chunk).</param>
public sealed record ReplyChunk(string TextDelta, Mood? Mood, bool Done, ParsedReply? Final = null);

/// <summary>
/// The heart of Ilham: turns a user message into a reply by assembling her persona, mood, memories and the
/// day's health situation, streaming the model, then applying what she decided (new memories, saved dates,
/// commands to approve). Pure orchestration — no UI, no OS calls — so it is fully unit-testable.
/// </summary>
public sealed class IlhamBrain(ILlmClient llm, MemoryStore memory, EmotionEngine emotions, HealthGuardian guardian, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Pending commands awaiting the user's approval, by id.</summary>
    private readonly Dictionary<string, ProposedCommand> _pending = new();

    /// <summary>Commands she has proposed and the user has not yet approved or rejected.</summary>
    public IReadOnlyCollection<ProposedCommand> Pending => _pending.Values;

    /// <summary>Streams Ilham's reply to a user message and records its consequences.</summary>
    /// <param name="userText">What the user said.</param>
    /// <param name="ct">Cancellation.</param>
    public async IAsyncEnumerable<ReplyChunk> RespondAsync(string userText, [EnumeratorCancellation] CancellationToken ct = default)
    {
        memory.AddMessage("user", userText);
        var messages = BuildContext(userText);

        var raw = new StringBuilder();
        string shownSoFar = "";
        Mood? announced = null;

        await foreach (string token in SafeStream(messages, ct))
        {
            raw.Append(token);
            string visible = ReplyParser.VisiblePrefix(raw.ToString());
            if (announced is null)
            {
                var peek = ReplyParser.Parse(raw.ToString());
                if (peek.Mood != Mood.Neutral) announced = peek.Mood;
            }
            if (visible.Length > shownSoFar.Length)
            {
                string delta = visible[shownSoFar.Length..];
                shownSoFar = visible;
                yield return new ReplyChunk(delta, announced, false);
            }
        }

        ParsedReply parsed = ReplyParser.Parse(raw.ToString());
        Apply(parsed);
        yield return new ReplyChunk("", parsed.Mood, true, parsed);
    }

    /// <summary>Builds a reply to a guardian health alert, in her own voice.</summary>
    /// <param name="alert">The escalation step.</param>
    /// <param name="profile">User settings.</param>
    /// <param name="ct">Cancellation.</param>
    public async IAsyncEnumerable<ReplyChunk> SpeakGuardianAsync(GuardianAlert alert, UserProfile profile, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new("system", SystemPrompt(profile, "health check")),
            new("user", PersonaBuilder.GuardianInstruction(alert, profile)),
        };
        var raw = new StringBuilder();
        string shown = "";
        bool any = false;
        await foreach (string token in SafeStream(messages, ct))
        {
            raw.Append(token);
            string visible = ReplyParser.VisiblePrefix(raw.ToString());
            if (visible.Length > shown.Length) { any = true; string d = visible[shown.Length..]; shown = visible; yield return new ReplyChunk(d, alert.Level >= 3 ? Mood.Annoyed : Mood.Worried, false); }
        }
        string text = any ? ReplyParser.Parse(raw.ToString()).Text : alert.FallbackLine; // model offline → canned line
        if (!any) yield return new ReplyChunk(text, Mood.Worried, false);
        memory.AddMessage("assistant", text, (alert.Level >= 3 ? Mood.Annoyed : Mood.Worried).ToString());
        yield return new ReplyChunk("", null, true, new ParsedReply(text, Mood.Worried, [], [], []));
    }

    /// <summary>Approves a pending command and runs it, then lets Ilham react to the result.</summary>
    /// <param name="id">The proposed command id.</param>
    /// <param name="timeout">How long to allow it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(CommandResult Result, IAsyncEnumerable<ReplyChunk> Reaction)> ApproveAsync(string id, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_pending.Remove(id, out var cmd)) throw new KeyNotFoundException($"No pending command {id}.");
        CommandResult result = await CommandRunner.RunAsync(cmd.Shell, cmd.Command, timeout, ct);
        string summary = $"[Command result] `{cmd.Command}` exited {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}:\n{result.Output}";
        return (result, RespondInternal(summary, role: "user_system"));
    }

    /// <summary>Rejects a pending command without running it.</summary>
    /// <param name="id">The proposed command id.</param>
    public bool Reject(string id) => _pending.Remove(id);

    private async IAsyncEnumerable<ReplyChunk> RespondInternal(string text, string role, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in RespondAsync(text, ct)) yield return chunk;
    }

    private void Apply(ParsedReply parsed)
    {
        memory.AddMessage("assistant", parsed.Text, parsed.Mood.ToString());
        emotions.Feel(parsed.Mood, 0.35, "talking with him");
        foreach (string fact in parsed.Memories) memory.AddFact(fact, "learned");
        foreach (var o in parsed.Occasions) memory.AddOccasion(o.Title, o.Month, o.Day, o.Year, o.Title.Contains("birthday", StringComparison.OrdinalIgnoreCase) ? "birthday" : "event");
        foreach (var c in parsed.Commands)
        {
            var (risk, reasons) = CommandSafety.Classify(c.Command);
            string cid = Guid.NewGuid().ToString("N")[..8];
            _pending[cid] = new ProposedCommand(cid, c.Shell, c.Command, risk, reasons);
        }
    }

    private List<ChatMessage> BuildContext(string userText)
    {
        var profile = new ProfileStore(memory).Load();
        var messages = new List<ChatMessage> { new("system", SystemPrompt(profile, userText)) };
        foreach (var line in memory.RecentMessages(16))
            messages.Add(new ChatMessage(line.Role == "assistant" ? "assistant" : "user", line.Content));
        return messages;
    }

    private string SystemPrompt(UserProfile profile, string context)
    {
        var relevant = memory.Search(context, 6).Select(s => s.Fact).ToList();
        var occasions = OccasionCalendar.Upcoming(profile, memory.Occasions(), DateOnly.FromDateTime(_clock.GetLocalNow().DateTime));
        return PersonaBuilder.Build(profile, emotions, relevant, occasions, guardian.Status(profile), _clock.GetLocalNow());
    }

    private async IAsyncEnumerable<string> SafeStream(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct)
    {
        IAsyncEnumerator<string> e;
        try { e = llm.StreamAsync(messages, ct).GetAsyncEnumerator(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { yield break; } // handled by caller's fallback
        await using (e)
        {
            while (true)
            {
                bool ok;
                try { ok = await e.MoveNextAsync(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { yield break; }
                if (!ok) yield break;
                yield return e.Current;
            }
        }
    }
}
