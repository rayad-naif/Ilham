using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ilham.App;
using Ilham.Core;
using Ilham.Core.Emotions;
using Ilham.Core.Guardian;
using Ilham.Core.Perception;
using Ilham.Core.Profile;

// Ilham — a private AI companion that runs entirely on your own PC.
//   dotnet run --project src/Ilham.App      → http://localhost:8787
// Her brain is a local model (Ollama); her memory, emotions and vault live in a SQLite file on disk.
// Nothing here talks to the cloud. Phone access: open the same URL from your phone on your home Wi-Fi.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

string dataDir = builder.Configuration["DataDir"] ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ilham");
builder.Services.AddSingleton(sp => new IlhamHost(dataDir, sp.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddHostedService<GuardianLoop>();

// Allow the phone on the LAN to reach the same instance.
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:8787");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
var host = app.Services.GetRequiredService<IlhamHost>();

// ───────────────────────── chat (server-sent events) ─────────────────────────

app.MapGet("/api/chat", async (HttpContext ctx, string message, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var brain = await host.ActiveBrainAsync(ct);
    async Task Send(string ev, object data) { await ctx.Response.WriteAsync($"event: {ev}\ndata: {JsonSerializer.Serialize(data, json)}\n\n", ct); await ctx.Response.Body.FlushAsync(ct); }

    try
    {
        await foreach (var chunk in brain.RespondAsync(message, ct))
        {
            if (chunk.TextDelta.Length > 0) await Send("delta", new { text = chunk.TextDelta, mood = chunk.Mood?.ToString()?.ToLowerInvariant() });
            if (chunk.Done) await Send("done", new
            {
                mood = chunk.Final!.Mood.ToString().ToLowerInvariant(),
                pending = brain.Pending.Select(p => new { p.Id, p.Shell, p.Command, risk = p.Risk.ToString().ToLowerInvariant(), p.Reasons }),
            });
        }
    }
    catch (OperationCanceledException) { /* client went away */ }
    // Keep the pending-command list reachable for the approval endpoints.
    PendingBridge.Replace(brain);
});

// ───────────────────────── command approval ─────────────────────────

app.MapPost("/api/command/{id}/approve", async (string id, CancellationToken ct) =>
{
    var brain = PendingBridge.Current ?? host.Brain;
    try
    {
        var (result, reaction) = await brain.ApproveAsync(id, TimeSpan.FromMinutes(2), ct);
        var text = new StringBuilder();
        await foreach (var c in reaction) text.Append(c.TextDelta);
        return Results.Json(new { result.ExitCode, result.Output, result.TimedOut, reaction = text.ToString() }, json);
    }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "That command is no longer pending." }); }
});

app.MapPost("/api/command/{id}/reject", (string id) =>
    (PendingBridge.Current ?? host.Brain).Reject(id) ? Results.Ok() : Results.NotFound());

// ───────────────────────── perception (webcam / voice read) ─────────────────────────

app.MapPost("/api/perceive", (PerceptionReport report) =>
{
    // The browser reads the user's own face/voice and sends a summary; she folds it into her mood.
    foreach (var (mood, amount, reason) in PerceptionInterpreter.ToFeelings(report))
        host.Emotions.Feel(mood, amount, reason);
    return Results.Ok(new { mood = host.Emotions.Dominant.ToString().ToLowerInvariant() });
});

// ───────────────────────── mood / guardian status ─────────────────────────

app.MapGet("/api/state", (CancellationToken ct) =>
{
    var profile = host.Profiles.Load();
    return Results.Json(new
    {
        mood = host.Emotions.Dominant.ToString().ToLowerInvariant(),
        moods = host.Emotions.Snapshot().ToDictionary(k => k.Key.ToString().ToLowerInvariant(), v => Math.Round(v.Value, 3)),
        guardian = host.Guardian.Status(profile),
        profile.Name,
    }, json);
});

app.MapPost("/api/guardian/snooze", (SnoozeRequest req) =>
{
    if (!host.Profiles.CheckPassword(req.Password)) return Results.Json(new { error = "Wrong override password." }, json, statusCode: 403);
    host.Guardian.Snooze(TimeSpan.FromMinutes(Math.Clamp(req.Minutes, 5, 180)));
    return Results.Ok();
});

// ───────────────────────── profile ─────────────────────────

app.MapGet("/api/profile", () => Results.Json(host.Profiles.Load(), json));
app.MapPost("/api/profile", (UserProfile p) => { host.Profiles.Save(p); return Results.Ok(); });
app.MapPost("/api/profile/password", (PasswordRequest req) => { host.Profiles.SetPassword(req.Password); return Results.Ok(); });
app.MapGet("/api/profile/has-password", () => Results.Json(new { has = host.Profiles.HasPassword }, json));

// ───────────────────────── memory ─────────────────────────

app.MapGet("/api/memory", () => Results.Json(new { facts = host.Memory.Facts(), occasions = host.Memory.Occasions() }, json));
app.MapPost("/api/memory/fact", (TextRequest r) => Results.Json(host.Memory.AddFact(r.Text, "manual"), json));
app.MapDelete("/api/memory/fact/{id:long}", (long id) => host.Memory.DeleteFact(id) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/memory/occasion", (OccasionRequest r) => Results.Json(host.Memory.AddOccasion(r.Title, r.Month, r.Day, r.Year, r.Kind ?? "event"), json));
app.MapDelete("/api/memory/occasion/{id:long}", (long id) => host.Memory.DeleteOccasion(id) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/memory/forget-all", (PasswordRequest req) =>
{
    if (host.Profiles.HasPassword && !host.Profiles.CheckPassword(req.Password)) return Results.Json(new { error = "Wrong password." }, json, statusCode: 403);
    host.Memory.ForgetEverything();
    return Results.Ok();
});

// ───────────────────────── vault (user-filled only) ─────────────────────────

app.MapGet("/api/vault", () => Results.Json(new { host.Vault.IsInitialized, host.Vault.IsUnlocked, entries = host.Vault.List() }, json));
app.MapPost("/api/vault/init", (PasswordRequest r) => { host.Vault.Initialize(r.Password); return Results.Ok(); });
app.MapPost("/api/vault/unlock", (PasswordRequest r) => Results.Json(new { ok = host.Vault.Unlock(r.Password) }, json));
app.MapPost("/api/vault/lock", () => { host.Vault.Lock(); return Results.Ok(); });
app.MapPost("/api/vault/put", (VaultPutRequest r) => Results.Json(new { id = host.Vault.Put(r.Title, r.Secret, r.Username, r.Note) }, json));
app.MapGet("/api/vault/{id:long}/reveal", (long id) => Results.Json(new { secret = host.Vault.Reveal(id) }, json));
app.MapDelete("/api/vault/{id:long}", (long id) => host.Vault.Delete(id) ? Results.Ok() : Results.NotFound());

// ───────────────────────── model status ─────────────────────────

app.MapGet("/api/model", async (CancellationToken ct) =>
{
    var (ok, status) = await host.Llm.CheckAsync(ct);
    var p = host.Profiles.Load();
    return Results.Json(new { ok, status, p.Model, demo = !ok }, json);
});

app.Run();

/// <summary>Keeps the most recent streaming brain reachable for the approval endpoints.</summary>
file static class PendingBridge
{
    public static IlhamBrain? Current { get; private set; }
    public static void Replace(IlhamBrain brain) { if (brain.Pending.Count > 0) Current = brain; }
}

/// <summary>Request bodies.</summary>
file sealed record SnoozeRequest(string Password, int Minutes);
file sealed record PasswordRequest(string Password);
file sealed record TextRequest(string Text);
file sealed record OccasionRequest(string Title, int Month, int Day, int? Year, string? Kind);
file sealed record VaultPutRequest(string Title, string Secret, string? Username, string? Note);

/// <summary>Exposed so integration tests can host the app.</summary>
public partial class Program;
