using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ilham.Core.Memory;

/// <summary>Something Ilham remembers about you.</summary>
public sealed record Fact(long Id, string Text, string Category, DateTimeOffset Created);

/// <summary>A date to celebrate or remember. <see cref="Year"/> is null for "every year on this day" without a known year.</summary>
public sealed record Occasion(long Id, string Title, int Month, int Day, int? Year, string Kind);

/// <summary>A line of conversation.</summary>
public sealed record ChatLine(long Id, string Role, string Content, string? Mood, DateTimeOffset Created);

/// <summary>
/// Ilham's private memory: a single SQLite file on your own disk. Nothing here ever leaves the machine.
/// Facts are retrieved for each conversation by TF-IDF similarity, so she recalls what's relevant
/// instead of stuffing everything into the prompt.
/// </summary>
public sealed class MemoryStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    /// <summary>Opens (creating if needed) the database at <paramref name="path"/>. Use ":memory:" for tests.</summary>
    /// <param name="path">Database file path.</param>
    public MemoryStore(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS facts (id INTEGER PRIMARY KEY, text TEXT NOT NULL, norm TEXT NOT NULL UNIQUE, category TEXT NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS occasions (id INTEGER PRIMARY KEY, title TEXT NOT NULL, month INT NOT NULL, day INT NOT NULL, year INT, kind TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages (id INTEGER PRIMARY KEY, role TEXT NOT NULL, content TEXT NOT NULL, mood TEXT, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    // ───────────── facts ─────────────

    /// <summary>Stores a fact; returns null if she already knew it (same words, ignoring case and punctuation).</summary>
    public Fact? AddFact(string text, string category = "general")
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        string norm = string.Join(' ', Tokenize(text));
        lock (_lock)
        {
            using var cmd = Cmd("INSERT OR IGNORE INTO facts (text, norm, category, created) VALUES ($t, $n, $c, $d) RETURNING id", ("$t", text), ("$n", norm), ("$c", category), ("$d", Now()));
            object? id = cmd.ExecuteScalar();
            return id is null ? null : new Fact((long)id, text, category, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Every fact, newest first.</summary>
    public IReadOnlyList<Fact> Facts()
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id, text, category, created FROM facts ORDER BY id DESC");
            return Read(cmd, r => new Fact(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3))));
        }
    }

    /// <summary>Forgets one fact.</summary>
    public bool DeleteFact(long id) { lock (_lock) return Cmd("DELETE FROM facts WHERE id = $id", ("$id", id)).ExecuteNonQuery() > 0; }

    /// <summary>
    /// The <paramref name="k"/> facts most relevant to <paramref name="query"/>, by TF-IDF cosine similarity.
    /// </summary>
    /// <remarks>
    /// Each fact is a sparse vector with weight <c>tf(t) · log(1 + N / df(t))</c>. Cosine similarity with the
    /// query vector ranks them; facts sharing no word with the query score 0 and are dropped. Rare words
    /// ("Faisalabad", "biryani") count far more than common ones ("is", "my"). O(N·L) per query, which is
    /// instant for the few thousand facts a person accumulates.
    /// </remarks>
    public IReadOnlyList<(Fact Fact, double Score)> Search(string query, int k = 6)
    {
        var facts = Facts();
        if (facts.Count == 0) return [];
        var docs = facts.Select(f => Tokenize(f.Text).ToList()).ToList();
        var df = new Dictionary<string, int>();
        foreach (var doc in docs) foreach (string t in doc.Distinct()) df[t] = df.GetValueOrDefault(t) + 1;
        double Idf(string t) => Math.Log(1 + (double)facts.Count / df.GetValueOrDefault(t, 1));

        Dictionary<string, double> Vector(IEnumerable<string> tokens) =>
            tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count() * Idf(g.Key));
        static double Norm(Dictionary<string, double> v) => Math.Sqrt(v.Values.Sum(x => x * x));

        var q = Vector(Tokenize(query));
        double qn = Norm(q);
        if (qn == 0) return [];
        return facts.Select((f, i) =>
            {
                var v = Vector(docs[i]);
                double dot = q.Sum(kv => v.TryGetValue(kv.Key, out double w) ? kv.Value * w : 0);
                return (Fact: f, Score: dot / (qn * Norm(v)));
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(k)
            .ToList();
    }

    // ───────────── occasions ─────────────

    /// <summary>Adds a date (birthday, anniversary, exam…). Duplicate title+date is ignored.</summary>
    public Occasion? AddOccasion(string title, int month, int day, int? year, string kind = "event")
    {
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2024, month)) throw new ArgumentException($"Invalid date {month}/{day}.");
        lock (_lock)
        {
            using (var dup = Cmd("SELECT id FROM occasions WHERE lower(title) = lower($t) AND month = $m AND day = $d", ("$t", title.Trim()), ("$m", month), ("$d", day)))
                if (dup.ExecuteScalar() is not null) return null;
            using var cmd = Cmd("INSERT INTO occasions (title, month, day, year, kind) VALUES ($t, $m, $d, $y, $k) RETURNING id",
                ("$t", title.Trim()), ("$m", month), ("$d", day), ("$y", (object?)year ?? DBNull.Value), ("$k", kind));
            return new Occasion((long)cmd.ExecuteScalar()!, title.Trim(), month, day, year, kind);
        }
    }

    /// <summary>All saved occasions.</summary>
    public IReadOnlyList<Occasion> Occasions()
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id, title, month, day, year, kind FROM occasions ORDER BY month, day");
            return Read(cmd, r => new Occasion(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetString(5)));
        }
    }

    /// <summary>Removes an occasion.</summary>
    public bool DeleteOccasion(long id) { lock (_lock) return Cmd("DELETE FROM occasions WHERE id = $id", ("$id", id)).ExecuteNonQuery() > 0; }

    // ───────────── conversation ─────────────

    /// <summary>Appends a message to the conversation log.</summary>
    public void AddMessage(string role, string content, string? mood = null)
    {
        lock (_lock) Cmd("INSERT INTO messages (role, content, mood, created) VALUES ($r, $c, $m, $d)",
            ("$r", role), ("$c", content), ("$m", (object?)mood ?? DBNull.Value), ("$d", Now())).ExecuteNonQuery();
    }

    /// <summary>The last <paramref name="n"/> messages in chronological order.</summary>
    public IReadOnlyList<ChatLine> RecentMessages(int n = 20)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id, role, content, mood, created FROM (SELECT * FROM messages ORDER BY id DESC LIMIT $n) ORDER BY id", ("$n", n));
            return Read(cmd, r => new ChatLine(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), DateTimeOffset.Parse(r.GetString(4))));
        }
    }

    // ───────────── settings ─────────────

    /// <summary>Reads a JSON setting, or <paramref name="fallback"/> if missing.</summary>
    public T Get<T>(string key, T fallback)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT value FROM settings WHERE key = $k", ("$k", key));
            return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) ?? fallback : fallback;
        }
    }

    /// <summary>Writes a JSON setting.</summary>
    public void Set<T>(string key, T value)
    {
        lock (_lock) Cmd("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v",
            ("$k", key), ("$v", JsonSerializer.Serialize(value))).ExecuteNonQuery();
    }

    /// <summary>Erases everything she remembers: facts, dates and conversation. Settings are kept.</summary>
    public void ForgetEverything() { lock (_lock) Exec("DELETE FROM facts; DELETE FROM occasions; DELETE FROM messages;"); }

    /// <inheritdoc />
    public void Dispose() => _db.Dispose();

    /// <summary>Lower-cased word tokens (Latin, Urdu and other scripts), with tiny English/Roman-Urdu stop words removed.</summary>
    public static IEnumerable<string> Tokenize(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(m => m.Value).Where(w => w.Length > 1 && !StopWords.Contains(w));

    private static readonly HashSet<string> StopWords =
    [
        "the", "is", "am", "are", "was", "and", "or", "of", "to", "in", "on", "at", "my", "me", "he", "his", "her", "she",
        "it", "an", "be", "do", "does", "for", "with", "that", "this", "has", "have", "hai", "ka", "ki", "ke", "ko", "se",
        "aur", "mein", "main", "ye", "wo", "hain", "tha", "thi",
    ];

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private void Exec(string sql) { lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); } }
    private SqliteCommand Cmd(string sql, params (string Name, object Value)[] args)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (n, v) in args) c.Parameters.AddWithValue(n, v);
        return c;
    }
    private static List<T> Read<T>(SqliteCommand cmd, Func<SqliteDataReader, T> map)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}
