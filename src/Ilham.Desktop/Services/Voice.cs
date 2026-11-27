using System.Diagnostics;
using System.Text;

namespace Ilham.Desktop.Services;

/// <summary>Ilham's voice: speaking (TTS) and listening (STT).</summary>
public interface IVoice
{
    /// <summary>True if she can speak on this machine.</summary>
    bool CanSpeak { get; }
    /// <summary>True if she can listen on this machine.</summary>
    bool CanListen { get; }

    /// <summary>Speaks text, pulsing <paramref name="amplitude"/> for lip-sync while she talks.</summary>
    /// <param name="text">What to say.</param>
    /// <param name="language">"mix", "urdu" or "english".</param>
    /// <param name="mood">Her mood (affects rate/pitch).</param>
    /// <param name="amplitude">Callback with 0..1 mouth openness.</param>
    void Speak(string text, string language, string mood, Action<double> amplitude);

    /// <summary>Stops any current speech.</summary>
    void StopSpeaking();

    /// <summary>Listens for one spoken phrase and returns the recognised text (null if nothing / cancelled).</summary>
    /// <param name="ct">Cancellation.</param>
    Task<string?> ListenOnceAsync(CancellationToken ct);
}

/// <summary>Picks the right voice for this OS.</summary>
public static class Voice
{
    /// <summary>Windows → real speech via the built-in engine; other OSes → silent (so it still runs).</summary>
    public static IVoice Create() => OperatingSystem.IsWindows() ? new WindowsVoice() : new NullVoice();
}

/// <summary>No-op voice for non-Windows dev/CI — everything else keeps working, she just doesn't talk.</summary>
public sealed class NullVoice : IVoice
{
    /// <inheritdoc />
    public bool CanSpeak => false;
    /// <inheritdoc />
    public bool CanListen => false;
    /// <inheritdoc />
    public void Speak(string text, string language, string mood, Action<double> amplitude) { }
    /// <inheritdoc />
    public void StopSpeaking() { }
    /// <inheritdoc />
    public Task<string?> ListenOnceAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>
/// Windows speech via the built-in System.Speech engine, driven through short PowerShell scripts so no extra
/// native package is needed. TTS speaks the text; STT does one-shot dictation. Lip-sync amplitude is a gentle
/// synthetic envelope (the CLI doesn't expose phoneme timing), which is enough to move her mouth naturally.
/// </summary>
public sealed class WindowsVoice : IVoice
{
    private Process? _speaking;
    private CancellationTokenSource? _ampCts;

    /// <inheritdoc />
    public bool CanSpeak => OperatingSystem.IsWindows();
    /// <inheritdoc />
    public bool CanListen => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public void Speak(string text, string language, string mood, Action<double> amplitude)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(text)) return;
        StopSpeaking();
        int rate = mood switch { "excited" => 2, "happy" => 1, "teasing" => 1, "sad" => -2, "worried" => -1, "angry" => 1, _ => 0 };
        string script =
            "Add-Type -AssemblyName System.Speech; " +
            "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
            "try { $s.SelectVoiceByHints('Female') } catch {}; " +
            $"$s.Rate = {rate}; $s.Speak('{Escape(text)}'); $s.Dispose();";

        try
        {
            _speaking = Start(script);
            // Pulse the mouth while the speak process runs.
            _ampCts = new CancellationTokenSource();
            var proc = _speaking;
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    while (proc is { HasExited: false } && !_ampCts.Token.IsCancellationRequested)
                    {
                        amplitude(0.25 + 0.55 * Math.Abs(Math.Sin(sw.Elapsed.TotalSeconds * 11)));
                        await Task.Delay(55, _ampCts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                finally { amplitude(0); }
            });
        }
        catch { amplitude(0); }
    }

    /// <inheritdoc />
    public void StopSpeaking()
    {
        _ampCts?.Cancel();
        try { if (_speaking is { HasExited: false }) _speaking.Kill(entireProcessTree: true); } catch { }
        _speaking = null;
    }

    /// <inheritdoc />
    public async Task<string?> ListenOnceAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string script =
            "Add-Type -AssemblyName System.Speech; " +
            "$r = New-Object System.Speech.Recognition.SpeechRecognitionEngine; " +
            "$r.SetInputToDefaultAudioDevice(); " +
            "$r.LoadGrammar((New-Object System.Speech.Recognition.DictationGrammar)); " +
            "$res = $r.Recognize([TimeSpan]::FromSeconds(12)); " +
            "if ($res -ne $null) { [Console]::Out.WriteLine($res.Text) }; $r.Dispose();";
        try
        {
            using var proc = Start(script, redirectOut: true);
            string output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            string text = output.Trim();
            return text.Length > 0 ? text : null;
        }
        catch { return null; }
    }

    private static Process Start(string script, bool redirectOut = false)
    {
        var psi = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOut,
            StandardOutputEncoding = redirectOut ? Encoding.UTF8 : null,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell.");
    }

    // Single-quote escaping for a PowerShell single-quoted string, with newlines flattened.
    private static string Escape(string s) => s.Replace("'", "''").Replace("\r", " ").Replace("\n", " ");
}
