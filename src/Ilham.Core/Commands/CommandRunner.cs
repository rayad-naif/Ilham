using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Ilham.Core.Commands;

/// <summary>How risky a command looks.</summary>
public enum Risk
{
    /// <summary>Read-only or harmless.</summary>
    Safe,
    /// <summary>Changes files, software or settings — confirm.</summary>
    Caution,
    /// <summary>Could destroy data or the system — confirm loudly.</summary>
    Dangerous,
}

/// <summary>A command Ilham wants to run. Nothing runs until you approve it.</summary>
/// <param name="Id">Approval id.</param>
/// <param name="Shell">"powershell", "cmd" or "bash".</param>
/// <param name="Command">The command line.</param>
/// <param name="Risk">Assessed risk.</param>
/// <param name="Reasons">Why it's rated that way.</param>
public sealed record ProposedCommand(string Id, string Shell, string Command, Risk Risk, IReadOnlyList<string> Reasons);

/// <summary>Outcome of an approved command.</summary>
public sealed record CommandResult(int ExitCode, string Output, bool TimedOut);

/// <summary>Rates commands by pattern so destructive ones always get a louder confirmation.</summary>
public static class CommandSafety
{
    private static readonly (Regex Pattern, Risk Risk, string Why)[] Rules =
    [
        (R(@"\b(format|diskpart|mkfs(\.\w+)?|fdisk|parted)\b"), Risk.Dangerous, "formats or repartitions a disk"),
        (R(@"\brm\s+(-\w*r\w*f|-\w*f\w*r)\b|\brm\s+-rf\b"), Risk.Dangerous, "recursively force-deletes files"),
        (R(@"\b(del|erase)\b.*\s/s\b|\brd\b.*\s/s\b|\brmdir\b.*\s/s\b|Remove-Item\b.*-Recurse"), Risk.Dangerous, "deletes folders recursively"),
        (R(@"\bdd\s+if=|>\s*/dev/sd"), Risk.Dangerous, "writes raw data to a disk"),
        (R(@"\b(reg\s+delete|Remove-ItemProperty|bcdedit|vssadmin\s+delete|cipher\s+/w)\b"), Risk.Dangerous, "changes system registry or boot/recovery data"),
        (R(@"\bshutdown\b|\bStop-Computer\b|\bRestart-Computer\b|\breboot\b|\bpoweroff\b"), Risk.Caution, "turns off or restarts the PC"),
        (R(@"\b(Set-ExecutionPolicy|netsh\s+advfirewall|Disable-|Set-MpPreference|ufw\s+disable)\b"), Risk.Caution, "changes security settings"),
        (R(@"\b(sudo|runas|Start-Process\b.*-Verb\s+RunAs)\b"), Risk.Caution, "runs with administrator rights"),
        (R(@"\b(curl|wget|Invoke-WebRequest|iwr|irm)\b.*\|\s*(sh|bash|iex|Invoke-Expression)"), Risk.Dangerous, "downloads and runs code from the internet"),
        (R(@"\b(del|erase|rm|Remove-Item|mv|move|Rename-Item)\b"), Risk.Caution, "deletes or moves files"),
        (R(@"\b(taskkill|Stop-Process|kill|pkill)\b"), Risk.Caution, "kills running programs"),
        (R(@"\b(apt|apt-get|winget|choco|pip|npm)\s+(install|remove|uninstall|purge)\b"), Risk.Caution, "installs or removes software"),
    ];

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Rates a command; the highest matching rule wins.</summary>
    public static (Risk Risk, IReadOnlyList<string> Reasons) Classify(string command)
    {
        var hits = Rules.Where(r => r.Pattern.IsMatch(command)).ToList();
        if (hits.Count == 0) return (Risk.Safe, []);
        return (hits.Max(h => h.Risk), hits.Select(h => h.Why).Distinct().ToList());
    }
}

/// <summary>Runs approved commands and captures their output.</summary>
public static class CommandRunner
{
    /// <summary>Largest output kept (the rest is cut, so it fits in her context).</summary>
    public const int MaxOutput = 6000;

    /// <summary>Runs <paramref name="command"/> in the given shell with a timeout.</summary>
    public static async Task<CommandResult> RunAsync(string shell, string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = shell switch
        {
            "cmd" => new ProcessStartInfo("cmd.exe", ["/d", "/c", command]),
            "powershell" => new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh", ["-NoProfile", "-NonInteractive", "-Command", command]),
            "bash" => new ProcessStartInfo("bash", ["-lc", command]),
            _ => throw new ArgumentException($"Unknown shell '{shell}'."),
        };
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        void Append(string? line) { if (line is null) return; lock (output) if (output.Length < MaxOutput) output.AppendLine(line); }
        proc.OutputDataReceived += (_, e) => Append(e.Data);
        proc.ErrorDataReceived += (_, e) => Append(e.Data);
        try { proc.Start(); }
        catch (Exception ex) { return new CommandResult(-1, $"Could not start {shell}: {ex.Message}", false); }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new CommandResult(-1, Cut(output.ToString()) + "\n[stopped: took too long]", true);
        }
        proc.WaitForExit(); // flush async output
        return new CommandResult(proc.ExitCode, Cut(output.ToString()), false);
    }

    private static string Cut(string s) => s.Length <= MaxOutput ? s.TrimEnd() : s[..MaxOutput] + "\n[…output cut]";
}
