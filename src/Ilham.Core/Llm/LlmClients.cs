using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ilham.Core.Llm;

/// <summary>A chat message in the format local models expect.</summary>
/// <param name="Role">"system", "user" or "assistant".</param>
/// <param name="Content">Text.</param>
public sealed record ChatMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);

/// <summary>A language model Ilham can think with.</summary>
public interface ILlmClient
{
    /// <summary>Streams the reply token by token.</summary>
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);

    /// <summary>Whether the model is reachable, and a human-readable status.</summary>
    Task<(bool Ok, string Status)> CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// Talks to a local Ollama server (free, runs on your GPU, no data leaves the PC).
/// Uses the native <c>/api/chat</c> endpoint, which streams one JSON object per line.
/// </summary>
public sealed class OllamaClient(HttpClient http, Func<(string Url, string Model)> settings) : ILlmClient
{
    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (url, model) = settings();
        var body = new
        {
            model,
            messages,
            stream = true,
            keep_alive = "30m",
            options = new { temperature = 0.8, top_p = 0.9, num_ctx = 8192, repeat_penalty = 1.08 },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/api/chat") { Content = JsonContent.Create(body) };
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            string err = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(res.StatusCode == System.Net.HttpStatusCode.NotFound && err.Contains("model")
                ? $"Model '{model}' isn't downloaded yet. Run: ollama pull {model}"
                : $"Ollama error {(int)res.StatusCode}: {err}");
        }
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e)) throw new InvalidOperationException(e.GetString());
            if (root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } token)
                yield return token;
            if (root.TryGetProperty("done", out var d) && d.GetBoolean()) yield break;
        }
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string Status)> CheckAsync(CancellationToken ct = default)
    {
        var (url, model) = settings();
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{url.TrimEnd('/')}/api/tags", ct));
            var names = doc.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString() ?? "").ToList();
            bool has = names.Any(n => n == model || n == $"{model}:latest" || n.StartsWith(model + ":", StringComparison.Ordinal) && !model.Contains(':'));
            return has ? (true, $"Connected · {model}") : (false, $"Ollama is running, but '{model}' isn't downloaded. Run: ollama pull {model}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, "Ollama isn't running. Install it from ollama.com and start it.");
        }
    }
}

/// <summary>
/// A scripted stand-in used when no model is available (demo mode and tests). It follows the same reply
/// protocol as the real model so every feature can be exercised without a GPU.
/// </summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string user = messages.LastOrDefault(m => m.Role == "user")?.Content.ToLowerInvariant() ?? "";
        string reply =
            user.Contains("birthday") && System.Text.RegularExpressions.Regex.Match(user, @"(\d{1,2})\s*(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)") is { Success: true } m
                ? $"[mood:excited] Noted! {m.Groups[1].Value} {m.Groups[2].Value} — I'm going to make it special. 🎂 [occasion: Your birthday | {MonthNum(m.Groups[2].Value):00}-{int.Parse(m.Groups[1].Value):00}]"
            : user.Contains("list files") || user.Contains("dir")
                ? "[mood:teasing] Fine, I'll do your chores. Approve this: [run:powershell: Get-ChildItem | Select-Object -First 10 Name]"
            : user.Contains("tired") || user.Contains("thak")
                ? "[mood:worried] Thak gaye ho? Pehle paani piyo, phir 10 minute aankhen band. Main yahin hoon. [remember: He gets tired in the evenings]"
            : user.StartsWith("[command result]")
                ? "[mood:happy] Done. See? Main kitni useful hoon. 😌"
            : "[mood:teasing] Demo mode hai abhi, Ollama install karo toh main sach mein smart ho jaungi. 😏";
        foreach (string word in System.Text.RegularExpressions.Regex.Split(reply, @"(?<=\s)"))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(8, ct);
            yield return word;
        }
    }

    /// <inheritdoc />
    public Task<(bool Ok, string Status)> CheckAsync(CancellationToken ct = default) => Task.FromResult((true, "Demo mode (scripted replies)"));

    private static int MonthNum(string m) => Array.IndexOf(["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"], m) + 1;
}
