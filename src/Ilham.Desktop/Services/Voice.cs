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

    /// <summary>Set when the last cloud-voice attempt failed, so the console can explain why she sounded robotic.</summary>
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public void Speak(string text, string language, string mood, Action<double> amplitude)
    {
        if (!OperatingSystem.IsWindows()) return;
        string spoken = SpeechText.Clean(text); // no emojis, markdown or tags read aloud
        if (spoken.Length == 0) return;
        string url = ttsUrl().Trim();
        if (url.Length == 0) { _fallback.Speak(spoken, language, mood, amplitude); return; }

        StopSpeaking();
        var cts = new CancellationTokenSource();
        lock (_gate) _cts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                byte[] audio = await FetchAsync(url, spoken, VoiceFor(language), RateFor(mood), PitchFor(mood), cts.Token);
                if (cts.Token.IsCancellationRequested) return;
                LastError = null;
                Play(audio, amplitude, cts.Token);
            }
            catch (OperationCanceledException) { amplitude(0); }
            catch (Exception ex)
            {
                // Cloud voice unreachable/failed — record why, and don't leave her silent.
                LastError = ex.Message;
                if (!cts.Token.IsCancellationRequested) _fallback.Speak(spoken, language, mood, amplitude);
            }
        }, cts.Token);
    }

    /// <summary>
    /// Diagnoses the neural voice end-to-end and returns a human-readable result: whether a URL is set, whether
    /// the endpoint answered, how much audio came back, and whether it decoded — so a robotic voice is never a
    /// silent mystery. Also plays the sample if it worked.
    /// </summary>
    public async Task<string> TestAsync(string language, Action<double> amplitude)
    {
        string url = ttsUrl().Trim();
        if (url.Length == 0)
            return "No voice URL set — she's using the built-in Windows voice (robotic). Set one with /set ttsurl <url> from your Kaggle cell.";
        string sample = language?.ToLowerInvariant() == "english"
            ? "Hi, this is my real voice. Can you hear me clearly?"
            : "Assalam o alaikum, yeh meri asli awaaz hai. Theek lag rahi hai?";
        try
        {
            byte[] audio = await FetchAsync(url, sample, VoiceFor(language), "+0%", "+0Hz", CancellationToken.None);
            if (audio.Length < 128) return $"Voice endpoint answered but sent only {audio.Length} bytes — the server may have errored. Check the Kaggle cell for a /tts error.";
            string kind = audio.Length > 4 && audio[0] == 'R' && audio[1] == 'I' && audio[2] == 'F' && audio[3] == 'F' ? "WAV" : "MP3";
            try
            {
                StopSpeaking();
                Play(audio, amplitude, CancellationToken.None);
                return $"✓ Neural voice OK — {audio.Length} bytes of {kind} playing now. If you heard it, the natural voice works.";
            }
            catch (Exception ex)
            {
                return $"Audio came back ({audio.Length} bytes, {kind}) but playback failed here: {ex.Message}. That's why she sounds robotic — tell me this and I'll switch the audio format.";
            }
        }
        catch (Exception ex)
        {
            return $"Couldn't reach the voice endpoint: {ex.Message}. Is the Kaggle cell running and the /set ttsurl value the SECOND (voice) URL it printed?";
        }
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

    private async Task<byte[]> FetchAsync(string url, string text, string voice, string rate, string pitch, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { text, voice, rate, pitch });
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
    private static string VoiceFor(string? language) =>
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

    // Mood → pitch, so her feeling colours the voice, not just its speed.
    private static string PitchFor(string mood) => mood switch
    {
        "excited" => "+18Hz",
        "happy" or "loving" => "+10Hz",
        "teasing" => "+8Hz",
        "angry" => "+6Hz",
        "worried" => "-6Hz",
        "sad" => "-14Hz",
        _ => "+0Hz",
    };
}

/// <summary>
/// Cleans a reply so it is spoken, not spelled out: strips emojis, variation selectors and markdown symbols so
/// she never reads "red heart" or "asterisk", and flattens whitespace. The chat bubble keeps the emojis; only
/// the spoken copy is cleaned.
/// </summary>
public static class SpeechText
{
    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            int v = rune.Value;
            // Drop emoji & pictographs, dingbats, symbols, arrows, flags, ZWJ and variation selectors.
            if (v >= 0x1F000 || v == 0x200D || v == 0xFE0F || (v >= 0x2190 && v <= 0x27BF) || (v >= 0x2B00 && v <= 0x2BFF))
                continue;
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune.ToString(), 0);
            if (cat == System.Globalization.UnicodeCategory.OtherSymbol) continue;
            // Markdown/formatting noise that shouldn't be read aloud.
            if (v is '*' or '_' or '#' or '`' or '~' or '>' or '|') { sb.Append(' '); continue; }
            sb.Append(rune.ToString());
        }
        // Collapse runs of whitespace to single spaces.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return cleaned;
    }
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
