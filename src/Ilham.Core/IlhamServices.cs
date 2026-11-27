using System.Net.Http;
using Ilham.Core.Emotions;
using Ilham.Core.Guardian;
using Ilham.Core.Initiative;
using Ilham.Core.Llm;
using Ilham.Core.Memory;
using Ilham.Core.Profile;
using Ilham.Core.Vault;

namespace Ilham.Core;

/// <summary>
/// Composes all of Ilham's parts around one SQLite file on disk, with no UI and no web server. Any front-end
/// — the Avalonia desktop app, a test, a future mobile shell — builds one of these and drives it. Everything
/// stays on the local machine.
/// </summary>
public sealed class IlhamServices : IDisposable
{
    /// <summary>Builds the services under <paramref name="dataDir"/>, talking to the local model via <paramref name="http"/>.</summary>
    /// <param name="dataDir">Folder for her private database.</param>
    /// <param name="http">HTTP client for the local Ollama server.</param>
    public IlhamServices(string dataDir, HttpClient http)
    {
        Directory.CreateDirectory(dataDir);
        DataDir = dataDir;
        Memory = new MemoryStore(Path.Combine(dataDir, "ilham.db"));
        Profiles = new ProfileStore(Memory);
        Vault = new SecretVault(Memory);
        Emotions = new EmotionEngine();
        Guardian = new HealthGuardian(Emotions);
        Initiative = new InitiativeEngine(Emotions);
        PcActions = global::Ilham.Core.Guardian.PcActions.ForCurrentOs();

        http.Timeout = TimeSpan.FromMinutes(10); // big models on CPU can be slow — don't cut her off
        Llm = new OllamaClient(http, () => { var p = Profiles.Load(); return (p.OllamaUrl, p.Model); });
        Demo = new ScriptedLlmClient();
        Brain = new IlhamBrain(Llm, Memory, Emotions, Guardian);

        var today = DateOnly.FromDateTime(DateTimeOffset.Now.DateTime);
        Guardian.Restore(today, Memory.Get($"active-minutes-{today:yyyyMMdd}", 0.0));
    }

    /// <summary>Folder holding her private data.</summary>
    public string DataDir { get; }
    /// <summary>Her memory store.</summary>
    public MemoryStore Memory { get; }
    /// <summary>Profile and override-password store.</summary>
    public ProfileStore Profiles { get; }
    /// <summary>Her encrypted, user-filled vault.</summary>
    public SecretVault Vault { get; }
    /// <summary>Her emotional state.</summary>
    public EmotionEngine Emotions { get; }
    /// <summary>The health guardian.</summary>
    public HealthGuardian Guardian { get; }
    /// <summary>When she speaks on her own.</summary>
    public InitiativeEngine Initiative { get; }
    /// <summary>OS actions (lock/shutdown/idle).</summary>
    public IPcActions PcActions { get; }
    /// <summary>Real local model client.</summary>
    public OllamaClient Llm { get; }
    /// <summary>Scripted fallback when no model is available.</summary>
    public ScriptedLlmClient Demo { get; }
    /// <summary>Her orchestration brain (bound to the real model).</summary>
    public IlhamBrain Brain { get; }

    /// <summary>Returns the brain bound to whichever client is usable right now (real model, else demo).</summary>
    public async Task<IlhamBrain> ActiveBrainAsync(CancellationToken ct = default)
    {
        var (ok, _) = await Llm.CheckAsync(ct);
        return ok ? Brain : new IlhamBrain(Demo, Memory, Emotions, Guardian);
    }

    /// <summary>Persists today's screen-time so a restart doesn't forget it.</summary>
    public void PersistCounters() => Memory.Set($"active-minutes-{Guardian.Day:yyyyMMdd}", Guardian.ActiveMinutesToday);

    /// <inheritdoc />
    public void Dispose() { PersistCounters(); Memory.Dispose(); }
}
