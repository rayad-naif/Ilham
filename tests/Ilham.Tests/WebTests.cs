using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ilham.Tests;

public class WebTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.UseSetting("DataDir", Path.Combine(Path.GetTempPath(), "ilham-test-" + Guid.NewGuid().ToString("N")))
         .UseSetting("Urls", "http://127.0.0.1:0")).CreateClient();

    [Fact]
    public async Task HomePage_And_Assets_AreServed()
    {
        var c = Client();
        Assert.Contains("Ilham", await c.GetStringAsync("/"));
        Assert.True((await c.GetAsync("/app.js")).IsSuccessStatusCode);
        Assert.True((await c.GetAsync("/avatar.js")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Model_ReportsDemoWhenNoOllama()
    {
        var doc = await Client().GetFromJsonAsync<JsonElement>("/api/model");
        Assert.True(doc.GetProperty("demo").GetBoolean()); // no Ollama in CI → demo mode
    }

    [Fact]
    public async Task Chat_StreamsAndDetectsBirthday()
    {
        var c = Client();
        string sse = await c.GetStringAsync("/api/chat?message=" + Uri.EscapeDataString("my birthday is 14 march"));
        Assert.Contains("event: delta", sse);
        Assert.Contains("event: done", sse);
        var mem = await c.GetFromJsonAsync<JsonElement>("/api/memory");
        Assert.True(mem.GetProperty("occasions").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Vault_InitUnlockPutReveal_OverHttp()
    {
        var c = Client();
        await c.PostAsJsonAsync("/api/vault/init", new { password = "masterpass" });
        await c.PostAsJsonAsync("/api/vault/put", new { title = "Wifi", secret = "home1234", username = (string?)null, note = (string?)null });
        var list = await c.GetFromJsonAsync<JsonElement>("/api/vault");
        long id = list.GetProperty("entries")[0].GetProperty("id").GetInt64();
        var reveal = await c.GetFromJsonAsync<JsonElement>($"/api/vault/{id}/reveal");
        Assert.Equal("home1234", reveal.GetProperty("secret").GetString());
    }

    [Fact]
    public async Task Perceive_FoldsFaceIntoMood()
    {
        var c = Client();
        var res = await c.PostAsJsonAsync("/api/perceive", new { FaceExpression = "happy", FaceConfidence = 0.9, EyesTired = false, VoiceEnergy = (double?)null, VoicePace = (double?)null, Transcript = (string?)null });
        res.EnsureSuccessStatusCode();
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/state")).TryGetProperty("mood", out _));
    }
}
