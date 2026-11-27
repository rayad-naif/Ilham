using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ilham.Core.Memory;

namespace Ilham.Core.Vault;

/// <summary>A secret entry's public label (never includes the secret itself).</summary>
/// <param name="Id">Entry id.</param>
/// <param name="Title">What it's for, e.g. "Netflix", "Wi-Fi".</param>
/// <param name="Username">Optional username/email shown in the UI.</param>
/// <param name="Note">Optional non-secret note.</param>
/// <param name="Updated">Last changed.</param>
public sealed record VaultEntry(long Id, string Title, string? Username, string? Note, DateTimeOffset Updated);

/// <summary>
/// A local, encrypted store that <b>you fill yourself</b> — Ilham never scans your browser, OS, or any
/// password manager. You type a secret in, she keeps it encrypted on your disk, and hands exactly one
/// back only when you ask her to.
/// </summary>
/// <remarks>
/// <para><b>What this is not:</b> there is deliberately no "import from Chrome / Windows Hello / Google"
/// path. Reading another app's saved credentials is how credential stealers work, so it isn't built. Every
/// entry here exists because you chose to add it.</para>
/// <para><b>Crypto.</b> The master password is stretched with PBKDF2-SHA256 (300k iterations, per-vault
/// random salt) into a 256-bit key. Each secret is sealed with AES-256-GCM under a fresh random nonce, so
/// the ciphertext is both encrypted and tamper-evident. The key lives in memory only while the vault is
/// unlocked; the master password is never stored. Forget it and the secrets are unrecoverable by design.</para>
/// </remarks>
public sealed class SecretVault(MemoryStore store)
{
    private const string SaltKey = "vault-salt", VerifierKey = "vault-verifier";
    private const int Iterations = 300_000;
    private byte[]? _key; // present only while unlocked

    /// <summary>True once a master password has been set up.</summary>
    public bool IsInitialized => store.Get<string?>(SaltKey, null) is not null;

    /// <summary>True while the vault is unlocked and secrets can be read or written.</summary>
    public bool IsUnlocked => _key is not null;

    /// <summary>Sets the master password for a brand-new vault.</summary>
    /// <param name="masterPassword">At least 6 characters. There is no recovery if forgotten.</param>
    public void Initialize(string masterPassword)
    {
        if (IsInitialized) throw new InvalidOperationException("Vault already set up. Use Unlock.");
        if (masterPassword.Length < 6) throw new ArgumentException("Master password must be at least 6 characters.");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        store.Set(SaltKey, Convert.ToBase64String(salt));
        _key = DeriveKey(masterPassword, salt);
        // Store an encrypted known value so a wrong password is detected without touching real secrets.
        store.Set(VerifierKey, Convert.ToBase64String(Seal(Encoding.UTF8.GetBytes("ilham-vault-ok"))));
    }

    /// <summary>Unlocks an existing vault. Returns false on a wrong password (nothing is revealed).</summary>
    /// <param name="masterPassword">The master password.</param>
    public bool Unlock(string masterPassword)
    {
        if (!IsInitialized) throw new InvalidOperationException("Vault not set up yet.");
        byte[] salt = Convert.FromBase64String(store.Get<string>(SaltKey, ""));
        byte[] candidate = DeriveKey(masterPassword, salt);
        try
        {
            byte[] check = Open(Convert.FromBase64String(store.Get<string>(VerifierKey, "")), candidate);
            if (!check.AsSpan().SequenceEqual("ilham-vault-ok"u8)) return false;
            _key = candidate;
            return true;
        }
        catch (CryptographicException) { return false; } // bad key fails the GCM tag
    }

    /// <summary>Locks the vault, wiping the key from memory. Call when you step away.</summary>
    public void Lock()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    /// <summary>Adds or updates a secret (vault must be unlocked).</summary>
    /// <param name="title">Label.</param>
    /// <param name="secret">The secret value, encrypted before it touches disk.</param>
    /// <param name="username">Optional username/email.</param>
    /// <param name="note">Optional non-secret note.</param>
    public long Put(string title, string secret, string? username = null, string? note = null)
    {
        RequireUnlocked();
        var entries = LoadIndex();
        long id = entries.Count == 0 ? 1 : entries.Max(e => e.Id) + 1;
        var existing = entries.FirstOrDefault(e => string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) id = existing.Id;
        store.Set($"vault-secret-{id}", Convert.ToBase64String(Seal(Encoding.UTF8.GetBytes(secret))));
        entries.RemoveAll(e => e.Id == id);
        entries.Add(new VaultEntry(id, title.Trim(), username, note, DateTimeOffset.UtcNow));
        SaveIndex(entries);
        return id;
    }

    /// <summary>Lists entry labels (no secrets). Works even while locked, since titles aren't secret.</summary>
    public IReadOnlyList<VaultEntry> List() => LoadIndex().OrderBy(e => e.Title).ToList();

    /// <summary>
    /// Reveals one secret by id (vault must be unlocked). This is the only way to read a value, and the UI
    /// requires an explicit action, so a secret never leaves the vault on its own.
    /// </summary>
    /// <param name="id">Entry id.</param>
    public string Reveal(long id)
    {
        RequireUnlocked();
        string? blob = store.Get<string?>($"vault-secret-{id}", null) ?? throw new KeyNotFoundException($"No vault entry {id}.");
        return Encoding.UTF8.GetString(Open(Convert.FromBase64String(blob), _key!));
    }

    /// <summary>Deletes a secret.</summary>
    public bool Delete(long id)
    {
        var entries = LoadIndex();
        if (entries.RemoveAll(e => e.Id == id) == 0) return false;
        store.Set($"vault-secret-{id}", "");
        SaveIndex(entries);
        return true;
    }

    private List<VaultEntry> LoadIndex() => store.Get("vault-index", new List<VaultEntry>());
    private void SaveIndex(List<VaultEntry> entries) => store.Set("vault-index", entries);
    private void RequireUnlocked() { if (_key is null) throw new InvalidOperationException("Vault is locked. Unlock it first."); }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>AES-256-GCM seal: output is nonce(12) ‖ tag(16) ‖ ciphertext.</summary>
    private byte[] Seal(byte[] plaintext) => SealWith(plaintext, _key!);

    private static byte[] SealWith(byte[] plaintext, byte[] key)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plaintext.Length], tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    private static byte[] Open(byte[] blob, byte[] key)
    {
        byte[] nonce = blob[..12], tag = blob[12..28], cipher = blob[28..];
        byte[] plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, cipher, tag, plain); // throws CryptographicException on wrong key or tampering
        return plain;
    }
}
