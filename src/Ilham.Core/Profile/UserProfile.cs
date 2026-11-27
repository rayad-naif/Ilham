using System.Security.Cryptography;
using Ilham.Core.Memory;

namespace Ilham.Core.Profile;

/// <summary>Everything Ilham needs to know to look after you. Stored only in the local database.</summary>
public sealed record UserProfile
{
    /// <summary>What she calls you.</summary>
    public string Name { get; init; } = "Jaan";

    /// <summary>Your birthday; the year is optional (used to say how old you're turning).</summary>
    public DateOnly? Birthday { get; init; }

    /// <summary>When you should be asleep (local time).</summary>
    public TimeOnly Bedtime { get; init; } = new(0, 30);

    /// <summary>When the "should be asleep" window ends.</summary>
    public TimeOnly WakeTime { get; init; } = new(7, 0);

    /// <summary>Minutes of continuous use before she insists on a break.</summary>
    public int BreakEveryMinutes { get; init; } = 50;

    /// <summary>Daily screen-time budget in minutes (0 = no limit).</summary>
    public int DailyLimitMinutes { get; init; } = 600;

    /// <summary>Whether she may lock the PC when you ignore her.</summary>
    public bool AllowLock { get; init; } = true;

    /// <summary>Whether she may schedule a shutdown at bedtime (you always get a countdown and can cancel).</summary>
    public bool AllowShutdown { get; init; } = false;

    /// <summary>Local Ollama endpoint.</summary>
    public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";

    /// <summary>
    /// Optional neural-voice endpoint (a natural Urdu/English voice). When set, she speaks via this
    /// <c>/tts</c> service (e.g. the edge-tts server on your cloud GPU box); when empty she falls back to the
    /// built-in Windows voice. Set with <c>/set ttsurl &lt;url&gt;</c> in the console.
    /// </summary>
    public string TtsUrl { get; init; } = "";

    /// <summary>
    /// Which local model is her brain. Default is a small multilingual model that runs on modest hardware
    /// (integrated GPU / CPU). Bump to qwen2.5:7b or 14b once you have a stronger GPU.
    /// </summary>
    public string Model { get; init; } = "qwen2.5:7b";

    /// <summary>Language she should prefer: "mix" (Roman Urdu + English), "english" or "urdu" (Urdu script).</summary>
    public string Language { get; init; } = "mix";

    /// <summary>Whether she talks on her own (check-ins, teasing, nagging, demands) when you go quiet.</summary>
    public bool Proactive { get; init; } = true;

    /// <summary>What she wears. One of the names in <c>Ilham.Rendering.Outfits</c> (e.g. "Mustard kameez").</summary>
    public string Outfit { get; init; } = "Mustard kameez";

    /// <summary>
    /// Privacy lock (opt-in): when the webcam doesn't recognise you, Ilham hides herself — like Face ID. It
    /// only decides owner-vs-not and never identifies or records whoever else is there. Requires enrolling
    /// your own face.
    /// </summary>
    public bool HideWhenNotOwner { get; init; }
}

/// <summary>Loads/saves the profile and the override password (PBKDF2-hashed, never stored in plain text).</summary>
public sealed class ProfileStore(MemoryStore store)
{
    private const string Key = "profile", PasswordKey = "override-password";

    /// <summary>The current profile (defaults if never saved).</summary>
    public UserProfile Load() => store.Get(Key, new UserProfile());

    /// <summary>Saves the profile.</summary>
    public void Save(UserProfile profile) => store.Set(Key, profile);

    /// <summary>True once an override password has been set.</summary>
    public bool HasPassword => store.Get<string?>(PasswordKey, null) is not null;

    /// <summary>Sets the override password (salted PBKDF2-SHA256, 200k iterations).</summary>
    public void SetPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4) throw new ArgumentException("Password must be at least 4 characters.");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 200_000, HashAlgorithmName.SHA256, 32);
        store.Set(PasswordKey, $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}");
    }

    /// <summary>Checks a password in constant time.</summary>
    public bool CheckPassword(string? password)
    {
        if (password is null || store.Get<string?>(PasswordKey, null) is not { } stored) return false;
        var parts = stored.Split(':');
        byte[] salt = Convert.FromBase64String(parts[0]), expected = Convert.FromBase64String(parts[1]);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 200_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
