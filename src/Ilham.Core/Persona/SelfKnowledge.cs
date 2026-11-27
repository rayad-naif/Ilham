using System.Reflection;
using Ilham.Core.Memory;

namespace Ilham.Core.Persona;

/// <summary>A piece of Ilham's own documentation she can quote from.</summary>
/// <param name="Doc">Which document it came from (e.g. "DEVELOPER.md").</param>
/// <param name="Heading">The section heading.</param>
/// <param name="Text">The section text.</param>
public sealed record SelfChunk(string Doc, string Heading, string Text);

/// <summary>
/// Ilham's knowledge of herself. Her own docs (README + everything in <c>docs/</c>) are compiled into the
/// assembly, split into sections, and retrieved by relevance — so when you ask how she's built, how to change
/// her, or where a feature lives, she answers from her real documentation instead of guessing. This is honest
/// retrieval (she quotes her docs), not a claim to know things she doesn't.
/// </summary>
public static class SelfKnowledge
{
    private const string Prefix = "Ilham.Core.SelfDocs.";
    private static readonly Lazy<IReadOnlyList<SelfChunk>> Chunks = new(Load);

    /// <summary>All documentation sections she carries.</summary>
    public static IReadOnlyList<SelfChunk> All => Chunks.Value;

    /// <summary>Names of the documents she knows (e.g. README.md, DEVELOPER.md).</summary>
    public static IReadOnlyList<string> Documents => All.Select(c => c.Doc).Distinct().ToList();

    /// <summary>
    /// The sections most relevant to <paramref name="query"/>, by TF-IDF cosine similarity over her docs.
    /// Returns nothing when the query doesn't look like it's about her (so normal chat isn't polluted).
    /// </summary>
    /// <param name="query">The user's message.</param>
    /// <param name="k">Maximum sections.</param>
    /// <param name="minScore">Relevance floor; below this, a section is dropped.</param>
    public static IReadOnlyList<SelfChunk> Retrieve(string query, int k = 3, double minScore = 0.06)
    {
        var chunks = All;
        if (chunks.Count == 0) return [];
        var docs = chunks.Select(c => MemoryStore.Tokenize(c.Heading + " " + c.Text).ToList()).ToList();

        var df = new Dictionary<string, int>();
        foreach (var d in docs) foreach (var t in d.Distinct()) df[t] = df.GetValueOrDefault(t) + 1;
        double Idf(string t) => Math.Log(1 + (double)chunks.Count / df.GetValueOrDefault(t, 1));
        Dictionary<string, double> Vec(IEnumerable<string> toks) => toks.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count() * Idf(g.Key));
        static double Norm(Dictionary<string, double> v) => Math.Sqrt(v.Values.Sum(x => x * x));

        var q = Vec(MemoryStore.Tokenize(query));
        double qn = Norm(q);
        if (qn == 0) return [];
        return chunks.Select((c, i) =>
            {
                var v = Vec(docs[i]);
                double dot = q.Sum(kv => v.TryGetValue(kv.Key, out double w) ? kv.Value * w : 0);
                return (Chunk: c, Score: dot / (qn * Norm(v) + 1e-9));
            })
            .Where(x => x.Score >= minScore)
            .OrderByDescending(x => x.Score)
            .Take(k)
            .Select(x => x.Chunk)
            .ToList();
    }

    private static IReadOnlyList<SelfChunk> Load()
    {
        var asm = typeof(SelfKnowledge).Assembly;
        var chunks = new List<SelfChunk>();
        foreach (string res in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            string doc = res[Prefix.Length..];
            using var stream = asm.GetManifestResourceStream(res);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            chunks.AddRange(SplitSections(doc, reader.ReadToEnd()));
        }
        return chunks;
    }

    /// <summary>Splits a markdown document into (heading, body) sections on its ## / ### headings.</summary>
    internal static IEnumerable<SelfChunk> SplitSections(string doc, string markdown)
    {
        string heading = doc;
        var body = new System.Text.StringBuilder();
        foreach (string line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal))
            {
                if (body.Length > 0) { yield return new SelfChunk(doc, heading, body.ToString().Trim()); body.Clear(); }
                heading = line.TrimStart('#', ' ').Trim();
            }
            else body.AppendLine(line);
        }
        if (body.ToString().Trim().Length > 0) yield return new SelfChunk(doc, heading, body.ToString().Trim());
    }
}
