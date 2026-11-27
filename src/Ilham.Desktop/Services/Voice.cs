using System.Diagnostics;
using System.Text;
using NAudio.Wave; // ToSampleProvider() / Init(ISampleProvider) extension methods live here

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
    /// <summary>
    /// Windows → a natural neural voice when a <c>/tts</c> URL is configured (falling back to the built-in
    /// Windows voice otherwise); other OSes → silent (so it still runs).
    /// </summary>
    /// <param name="ttsUrl">Reads the current cloud TTS endpoint (empty = use the built-in voice).</param>
    public static IVoice Create(Func<string>? ttsUrl = null) =>
        OperatingSystem.IsWindows() ? new CloudEdgeVoice(ttsUrl ?? (() => "")) : new NullVoice();
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

/// <summary>
/// A natural neural voice. When a <c>/tts</c> endpoint is configured (e.g. the edge-tts server running on the
/// cloud GPU box), she speaks with a real Urdu/English neural voice fetched as audio and played here, with
/// lip-sync driven by the actual waveform. If no endpoint is set, or the request fails (offline), she falls
/// back to the built-in Windows voice so she's never mute. Listening (STT) always uses the Windows engine.
/// </summary>
public sealed class CloudEdgeVoice(Func<string> ttsUrl) : IVoice
{
    private readonly WindowsVoice _fallback = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly object _gate = new();
    private NAudio.Wave.IWavePlayer? _player;
    private IDisposable? _reader;
    private CancellationTokenSource? _cts;

    /// <inheritdoc />
    public bool CanSpeak => OperatingSystem.IsWindows();
    /// <inheritdoc />
    public bool CanListen => _fallback.CanListen;

    /// <inheritdoc />
    public void Speak(string text, string language, string mood, Action<double> amplitude)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(text)) return;
        string url = ttsUrl().Trim();
        if (url.Length == 0) { _fallback.Speak(text, language, mood, amplitude); return; }

        StopSpeaking();
        var cts = new CancellationTokenSource();
        lock (_gate) _cts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                byte[] audio = await FetchAsync(url, text, VoiceFor(language), RateFor(mood), cts.Token);
                if (cts.Token.IsCancellationRequested) return;
                Play(audio, amplitude, cts.Token);
            }
            catch (OperationCanceledException) { amplitude(0); }
            catch
            {
                // Cloud voice unreachable — don't leave her silent; use the built-in voice.
                if (!cts.Token.IsCancellationRequested) _fallback.Speak(text, language, mood, amplitude);
            }
        }, cts.Token);
    }

    /// <inheritdoc />
    public void StopSpeaking()
    {
        _fallback.StopSpeaking();
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { }
            try { _player?.Stop(); } catch { }
            try { _player?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            _player = null; _reader = null; _cts = null;
        }
    }

    /// <inheritdoc />
    public Task<string?> ListenOnceAsync(CancellationToken ct) => _fallback.ListenOnceAsync(ct);

    private async Task<byte[]> FetchAsync(string url, string text, string voice, string rate, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { text, voice, rate });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/tts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("audio/wav");
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    // Plays audio (WAV or MP3) on this machine, pulsing the mouth from the live waveform's loudness.
    private void Play(byte[] audio, Action<double> amplitude, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        var ms = new MemoryStream(audio);
        bool isWav = audio.Length > 4 && audio[0] == 'R' && audio[1] == 'I' && audio[2] == 'F' && audio[3] == 'F';
        NAudio.Wave.WaveStream wave = isWav ? new NAudio.Wave.WaveFileReader(ms) : new NAudio.Wave.Mp3FileReader(ms);
        var amp = new AmplitudeSampleProvider(wave.ToSampleProvider(), amplitude);
        var player = new NAudio.Wave.WaveOutEvent();
        player.PlaybackStopped += (_, _) =>
        {
            amplitude(0);
            try { wave.Dispose(); } catch { }
            try { player.Dispose(); } catch { }
        };
        lock (_gate)
        {
            if (ct.IsCancellationRequested) { wave.Dispose(); player.Dispose(); amplitude(0); return; }
            _reader = wave; _player = player;
        }
        player.Init(amp);
        player.Play();
    }

    // "mix"/"urdu" → a real Pakistani Urdu female voice; "english" → a natural English female voice.
    private static string VoiceFor(string language) =>
        language?.ToLowerInvariant() == "english" ? "en-US-AriaNeural" : "ur-PK-UzmaNeural";

    // Mood → speaking rate (edge-tts percentage), so she sounds lively or subdued to match her feeling.
    private static string RateFor(string mood) => mood switch
    {
        "excited" => "+16%",
        "happy" or "loving" => "+8%",
        "teasing" or "angry" => "+6%",
        "worried" => "-6%",
        "sad" => "-12%",
        _ => "+0%",
    };
}

/// <summary>Wraps a sample stream and reports its running loudness (0..1) so the avatar's mouth tracks speech.</summary>
internal sealed class AmplitudeSampleProvider(NAudio.Wave.ISampleProvider source, Action<double> amplitude)
    : NAudio.Wave.ISampleProvider
{
    public NAudio.Wave.WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int n = source.Read(buffer, offset, count);
        if (n > 0)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) { float s = buffer[offset + i]; sum += s * s; }
            double rms = Math.Sqrt(sum / n);             // 0..~1
            double level = Math.Clamp(rms * 3.2, 0, 1);  // lift quiet speech into a visible mouth range
            amplitude(0.12 + 0.78 * level);
        }
        return n;
    }
}
