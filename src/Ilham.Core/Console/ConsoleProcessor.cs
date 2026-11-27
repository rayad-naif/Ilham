using System.Text;
using Ilham.Core.Commands;
using Ilham.Core.Memory;
using Ilham.Core.Outfits;
using Ilham.Core.Persona;
using Ilham.Core.Profile;
using Ilham.Core.Vault;

namespace Ilham.Core.Console;

/// <summary>
/// The built-in console: lets you edit and operate Ilham directly from inside the app. A line starting with
/// <c>/</c> is a meta-command (edit settings, memory, the vault, show status); anything else is run as a shell
/// command on your own PC. Because <b>you</b> type these yourself, they run without Ilham's approval gate —
/// you are the operator here, not her.
/// </summary>
public sealed class ConsoleProcessor(MemoryStore memory, ProfileStore profiles, SecretVault vault,
    Func<string, (bool Ok, string Status)>? modelCheck = null)
{
    /// <summary>Which shell `plain` commands run in ("cmd", "powershell" or "bash").</summary>
    public string Shell { get; set; } = OperatingSystem.IsWindows() ? "powershell" : "bash";

    /// <summary>Runs one console line and returns its text output.</summary>
    /// <param name="line">What the user typed.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> RunAsync(string line, CancellationToken ct = default)
    {
        line = line.Trim();
        if (line.Length == 0) return "";
        if (!line.StartsWith('/'))
        {
            var r = await CommandRunner.RunAsync(Shell, line, TimeSpan.FromMinutes(2), ct);
            return r.Output.Length == 0 ? $"(exit {r.ExitCode})" : r.Output;
        }

        string[] parts = line[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
        string cmd = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1] : "";
        try
        {
            return cmd switch
            {
                "help" or "?" => Help,
                "profile" => ShowProfile(),
                "set" => SetField(arg),
                "password" => SetPassword(arg),
                "facts" => List(memory.Facts().Select(f => $"#{f.Id}  {f.Text}"), "No facts yet."),
                "remember" => Remember(arg),
                "forget-fact" => long.TryParse(arg, out var fid) && memory.DeleteFact(fid) ? $"Forgot fact #{fid}." : "Usage: /forget-fact <id>",
                "occasions" => List(memory.Occasions().Select(o => $"#{o.Id}  {o.Title} — {o.Month:00}-{o.Day:00}"), "No dates saved."),
                "occasion" => AddOccasion(arg),
                "outfit" => SetOutfit(arg),
                "model" => modelCheck is null ? "(model status unavailable)" : Model(),
                "docs" => "She knows: " + string.Join(", ", SelfKnowledge.Documents),
                "doc" => ShowDoc(arg),
                "vault" => Vault(arg),
                _ => $"Unknown command '/{cmd}'. Try /help.",
            };
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private const string Help = """
        Console — edit her directly. Anything not starting with / runs as a shell command.
          /profile                     show all settings
          /set name <X>                what she calls you
          /set model <X>               her brain (e.g. qwen2.5:7b, qwen2.5:32b)
          /set ollamaurl <url>         point her at a remote Ollama (e.g. a cloud GPU)
          /set ttsurl <url>            natural neural voice endpoint (cloud edge-tts); "off" to disable
          /set language mix|urdu|english
          /set bedtime HH:MM | /set wake HH:MM
          /set break <min> | /set limit <min>
          /set proactive on|off | /set lock on|off | /set shutdown on|off
          /set hidewhennotowner on|off | /set outfit <name>
          /password <X>                set the override password
          /facts  /remember <text>  /forget-fact <id>
          /occasions  /occasion <Title> | MM-DD
          /outfit <name>               change what she's wearing now
          /model                       model status
          /docs  /doc <NAME>           her own documentation
          /vault  /vault init <pw>  /vault unlock <pw>  /vault add <Title> | <secret>  /vault reveal <id>  /vault lock
          /shell cmd|powershell|bash   choose the shell for plain commands
        """;

    private string ShowProfile()
    {
        var p = profiles.Load();
        return $"""
            name: {p.Name}
            birthday: {(p.Birthday is { } b ? b.ToString("yyyy-MM-dd") : "(not set)")}
            bedtime: {p.Bedtime:HH\:mm}   wake: {p.WakeTime:HH\:mm}
            break every: {p.BreakEveryMinutes} min   daily limit: {p.DailyLimitMinutes} min
            model: {p.Model}   language: {p.Language}   outfit: {p.Outfit}
            ollama: {p.OllamaUrl}   voice(tts): {(p.TtsUrl.Length > 0 ? p.TtsUrl : "(built-in Windows voice)")}
            proactive: {On(p.Proactive)}   lock: {On(p.AllowLock)}   shutdown: {On(p.AllowShutdown)}   hide-when-not-owner: {On(p.HideWhenNotOwner)}
            override password: {(profiles.HasPassword ? "set" : "not set")}
            """;
    }

    private string SetField(string arg)
    {
        var kv = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (kv.Length < 2) return "Usage: /set <field> <value>  (try /help)";
        string field = kv[0].ToLowerInvariant(), val = kv[1];
        var p = profiles.Load();
        p = field switch
        {
            "name" => p with { Name = val },
            "model" => p with { Model = val },
            "ollamaurl" or "url" => p with { OllamaUrl = val.TrimEnd('/') },
            "ttsurl" or "voiceurl" => p with { TtsUrl = val.Trim() is "off" or "none" or "-" ? "" : val.TrimEnd('/') },
            "language" => val.ToLowerInvariant() is "mix" or "urdu" or "english" ? p with { Language = val.ToLowerInvariant() } : throw new ArgumentException("language must be mix, urdu or english"),
            "bedtime" => p with { Bedtime = TimeOnly.Parse(val) },
            "wake" => p with { WakeTime = TimeOnly.Parse(val) },
            "break" => p with { BreakEveryMinutes = Pos(val) },
            "limit" => p with { DailyLimitMinutes = int.Parse(val) },
            "proactive" => p with { Proactive = Flag(val) },
            "lock" => p with { AllowLock = Flag(val) },
            "shutdown" => p with { AllowShutdown = Flag(val) },
            "hidewhennotowner" => p with { HideWhenNotOwner = Flag(val) },
            "outfit" => OutfitWardrobe.IsKnown(val) ? p with { Outfit = OutfitWardrobe.Canonical(val)! } : throw new ArgumentException($"unknown outfit. One of: {string.Join(", ", OutfitWardrobe.Names)}"),
            "birthday" => p with { Birthday = DateOnly.Parse(val) },
            _ => throw new ArgumentException($"unknown field '{field}'"),
        };
        profiles.Save(p);
        return $"Set {field} = {val}.";
    }

    private string SetPassword(string arg)
    {
        if (arg.Length < 4) return "Usage: /password <at least 4 chars>";
        profiles.SetPassword(arg);
        return "Override password set.";
    }

    private string Remember(string arg)
    {
        if (arg.Length == 0) return "Usage: /remember <fact>";
        var f = memory.AddFact(arg, "manual");
        return f is null ? "She already knew that." : $"Remembered (#{f.Id}).";
    }

    private string AddOccasion(string arg)
    {
        var parts = arg.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !parts[1].Contains('-')) return "Usage: /occasion <Title> | MM-DD  (or YYYY-MM-DD)";
        var d = parts[1].Split('-');
        int month, day; int? year = null;
        if (d.Length == 3) { year = int.Parse(d[0]); month = int.Parse(d[1]); day = int.Parse(d[2]); }
        else { month = int.Parse(d[0]); day = int.Parse(d[1]); }
        var o = memory.AddOccasion(parts[0], month, day, year, parts[0].Contains("birthday", StringComparison.OrdinalIgnoreCase) ? "birthday" : "event");
        return o is null ? "Already saved." : $"Saved '{o.Title}' ({month:00}-{day:00}).";
    }

    private string SetOutfit(string arg)
    {
        if (!OutfitWardrobe.IsKnown(arg)) return $"Unknown outfit. One of: {string.Join(", ", OutfitWardrobe.Names)}";
        var p = profiles.Load();
        profiles.Save(p with { Outfit = OutfitWardrobe.Canonical(arg)! });
        return $"She's now wearing {OutfitWardrobe.Canonical(arg)}.";
    }

    private string Model()
    {
        var (ok, status) = modelCheck!(profiles.Load().Model);
        return (ok ? "✓ " : "✗ ") + status;
    }

    private static string ShowDoc(string name)
    {
        if (name.Length == 0) return "Usage: /doc <NAME>  — one of: " + string.Join(", ", SelfKnowledge.Documents);
        var sections = SelfKnowledge.All.Where(c => c.Doc.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sections.Count == 0) return $"No doc matching '{name}'. Known: {string.Join(", ", SelfKnowledge.Documents)}";
        return $"{sections[0].Doc} sections:\n" + string.Join("\n", sections.Select(s => "  • " + s.Heading));
    }

    private string Vault(string arg)
    {
        var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
        string op = (sub.Length > 0 ? sub[0] : "").ToLowerInvariant(), rest = sub.Length > 1 ? sub[1] : "";
        switch (op)
        {
            case "" or "list":
                return vault.IsInitialized ? List(vault.List().Select(e => $"#{e.Id}  {e.Title}{(e.Username is { Length: > 0 } ? " (" + e.Username + ")" : "")}"), "Vault is empty.") : "Vault not set up. Use /vault init <master-password>.";
            case "init": vault.Initialize(rest); return "Vault created and unlocked.";
            case "unlock": return vault.Unlock(rest) ? "Vault unlocked." : "Wrong master password.";
            case "lock": vault.Lock(); return "Vault locked.";
            case "add":
                var ap = rest.Split('|', 2, StringSplitOptions.TrimEntries);
                if (ap.Length != 2) return "Usage: /vault add <Title> | <secret>";
                return $"Saved '{ap[0]}' (#{vault.Put(ap[0], ap[1])}).";
            case "reveal":
                return long.TryParse(rest, out var vid) ? vault.Reveal(vid) : "Usage: /vault reveal <id>";
            default: return "Vault: list | init <pw> | unlock <pw> | add <Title> | <secret> | reveal <id> | lock";
        }
    }

    private static string List(IEnumerable<string> items, string empty)
    {
        var list = items.ToList();
        return list.Count == 0 ? empty : string.Join("\n", list);
    }
    private static string On(bool b) => b ? "on" : "off";
    private static int Pos(string v) => int.Parse(v) is var n && n > 0 ? n : throw new ArgumentException("must be positive");
    private static bool Flag(string v) => v.ToLowerInvariant() is "on" or "true" or "yes" or "1";
}
