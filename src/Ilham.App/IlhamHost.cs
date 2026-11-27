using Ilham.Core;
using Ilham.Core.Emotions;
using Ilham.Core.Guardian;
using Ilham.Core.Llm;
using Ilham.Core.Memory;
using Ilham.Core.Profile;
using Ilham.Core.Vault;

namespace Ilham.App;

/// <summary>
/// The single long-lived home for Ilham inside the app: her memory, emotions, guardian, brain and vault,
/// all built around one SQLite file on disk. Everything here stays on this machine.
/// </summary>
public sealed class IlhamHost : IDisposable
{
    /// <summary>Creates the host, opening (or creating) her database under the given data folder.</summary>
    /// <param name="dataDir">Folder for her private data (database lives here).</param>
    /// <param name="httpFactory">For talking to the local model server.</param>
    public IlhamHost(string dataDir, IHttpClientFactory httpFactory)
    {
        Directory.CreateDirectory(dataDir);
        DataDir = dataDir;
        Memory = new MemoryStore(Path.Combine(dataDir, "ilham.db"));
        Profiles = new ProfileStore(Memory);
        Vault = new SecretVault(Memory);
        Emotions = new EmotionEngine();
        Guardian = new HealthGuardian(Emotions);
        Initiative = new Core.Initiative.InitiativeEngine(Emotions);
        PcActions = Core.Guardian.PcActions.ForCurrentOs();

        var http = httpFactory.CreateClient("ollama");
        http.Timeout = TimeSpan.FromMinutes(5);
        Llm = new OllamaClient(http, () => { var p = Profiles.Load(); return (p.OllamaUrl, p.Model); });
        Demo = new ScriptedLlmClient();
        Brain = new IlhamBrain(Llm, Memory, Emotions, Guardian);

        // Restore today's screen-time counter so a restart doesn't forget.
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.DateTime);
        Guardian.Restore(today, Memory.Get($"active-minutes-{today:yyyyMMdd}", 0.0));
    }

    /// <summary>Folder holding her private data.</summary>
    public string DataDir { get; }
    /// <summary>Her memory store.</summary>
    public MemoryStore Memory { get; }
    /// <summary>Profile and override-password store.</summary>
    public ProfileStore Profiles { get; }
    /// <summary>Her encrypted, user-filled secret vault.</summary>
    public SecretVault Vault { get; }
    /// <summary>Her emotional state.</summary>
    public EmotionEngine Emotions { get; }
    /// <summary>The health guardian.</summary>
    public HealthGuardian Guardian { get; }
    /// <summary>Decides when she speaks on her own.</summary>
    public Core.Initiative.InitiativeEngine Initiative { get; }
    /// <summary>OS actions (lock/shutdown/idle).</summary>
    public IPcActions PcActions { get; }
    /// <summary>The real local model client.</summary>
    public OllamaClient Llm { get; }
    /// <summary>Scripted fallback used when no model is available.</summary>
    public ScriptedLlmClient Demo { get; }
    /// <summary>Her reasoning/orchestration brain (bound to the live model).</summary>
    public IlhamBrain Brain { get; private set; }

    /// <summary>A brain bound to whichever client is currently usable (real model, else demo).</summary>
    public async Task<IlhamBrain> ActiveBrainAsync(CancellationToken ct = default)
    {
        var (ok, _) = await Llm.CheckAsync(ct);
        return ok ? Brain : new IlhamBrain(Demo, Memory, Emotions, Guardian);
    }

    /// <summary>Persists today's screen-time so it survives a restart.</summary>
    public void PersistCounters() => Memory.Set($"active-minutes-{Guardian.Day:yyyyMMdd}", Guardian.ActiveMinutesToday);

    /// <inheritdoc />
    public void Dispose() { PersistCounters(); Memory.Dispose(); }
}
