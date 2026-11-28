using System.Text.RegularExpressions;
using Ilham.Core.Emotions;

namespace Ilham.Core.Persona;

/// <summary>A command Ilham asked to run.</summary>
public sealed record CommandRequest(string Shell, string Command);

/// <summary>A date Ilham asked to remember.</summary>
public sealed record OccasionRequest(string Title, int Month, int Day, int? Year);

/// <summary>Ilham's reply, split into what you see and what the app should do.</summary>
/// <param name="Text">The visible message with all tags removed.</param>
/// <param name="Mood">Her mood for this reply (drives her face).</param>
/// <param name="Memories">Facts to save.</param>
/// <param name="Occasions">Dates to save.</param>
/// <param name="Commands">Commands awaiting your approval.</param>
/// <param name="Outfit">An outfit she decided to change into this turn, or null.</param>
public sealed record ParsedReply(string Text, Mood Mood, IReadOnlyList<string> Memories,
    IReadOnlyList<OccasionRequest> Occasions, IReadOnlyList<CommandRequest> Commands, string? Outfit = null);

/// <summary>
/// Extracts the action tags Ilham's prompt teaches her to use:
/// <c>[mood:x]</c>, <c>[remember: …]</c>, <c>[occasion: Title | YYYY-MM-DD or MM-DD]</c>, <c>[run:shell: …]</c>.
/// Tags can appear anywhere; anything malformed is simply left out of the visible text.
/// </summary>
public static partial class ReplyParser
{
    [GeneratedRegex(@"\[\s*(mood|remember|occasion|run|outfit)\s*:\s*((?:[^\[\]]|\[[^\[\]]*\])*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^(?:(\d{4})-)?(\d{1,2})-(\d{1,2})$")]
    private static partial Regex DateRegex();

    /// <summary>Parses a complete reply.</summary>
    public static ParsedReply Parse(string raw)
    {
        Mood mood = Mood.Neutral;
        bool moodSet = false;
        var memories = new List<string>();
        var occasions = new List<OccasionRequest>();
        var commands = new List<CommandRequest>();
        string? outfit = null;

        string text = TagRegex().Replace(raw, m =>
        {
            string kind = m.Groups[1].Value.ToLowerInvariant(), body = m.Groups[2].Value.Trim();
            switch (kind)
            {
                case "mood" when !moodSet:
                    mood = EmotionEngine.Parse(body);
                    moodSet = true;
                    break;
                case "remember" when body.Length > 0:
                    memories.Add(body);
                    break;
                case "occasion":
                    var parts = body.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && DateRegex().Match(parts[1]) is { Success: true } d)
                    {
                        int month = int.Parse(d.Groups[2].Value), day = int.Parse(d.Groups[3].Value);
                        int? year = d.Groups[1].Success ? int.Parse(d.Groups[1].Value) : null;
                        if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2024, month) && parts[0].Length > 0)
                            occasions.Add(new OccasionRequest(parts[0], month, day, year));
                    }
                    break;
                case "run":
                    var sc = body.Split(':', 2, StringSplitOptions.TrimEntries);
                    // "gui" is mouse/keyboard control (she actually clicks/types); the rest are shells.
                    if (sc.Length == 2 && sc[0].ToLowerInvariant() is "powershell" or "cmd" or "bash" or "gui" && sc[1].Length > 0)
                        commands.Add(new CommandRequest(sc[0].ToLowerInvariant(), sc[1]));
                    break;
                case "outfit" when body.Length > 0:
                    outfit = Outfits.OutfitWardrobe.Canonical(body); // only accept a real outfit name
                    break;
            }
            return "";
        });

        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return new ParsedReply(text, mood, memories, occasions, commands, outfit);
    }

    /// <summary>
    /// For streaming: hides complete tags and any unfinished "[tag:" at the end, so you never see raw tags
    /// flicker on screen while she's typing.
    /// </summary>
    public static string VisiblePrefix(string partial)
    {
        string text = TagRegex().Replace(partial, "");
        int open = text.LastIndexOf('[');
        if (open >= 0 && text.IndexOf(']', open) < 0 && Regex.IsMatch(text[open..], @"^\[\s*(m|mo|moo|mood|r|re|rem|reme|remem|rememb|remembe|remember|o|oc|occ|occa|occas|occasi|occasio|occasion|ou|out|outf|outfi|outfit|ru|run)?\s*(:.*)?$", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            text = text[..open];
        return Regex.Replace(text, @"[ \t]{2,}", " ").Trim();
    }
}
