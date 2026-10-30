using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class CodexProxyRsClientTests
{
    internal const string Budget = """{"unit":"USD","daily":{"total":"10","used":"2.25","remaining":"7.75","resetsAt":"2030-01-02T00:00:00Z"},"weekly":{"total":"50","used":"20.5","remaining":"29.5","resetsAt":null}}""";
    internal const string Overview = """{"code":200,"data":{"startTime":"2026-10-01T00:00:00Z","endTime":"2026-10-01T12:00:00Z","summary":{"requests":1,"inputTokens":100,"outputTokens":20,"cachedInputTokens":80,"cacheWriteInputTokens":0,"reasoningOutputTokens":5,"totalTokens":120,"costUsd":"0.001","costIncomplete":false},"analytics":{"state":"ready"},"trend":[{"time":"2026-10-01T00:00:00Z","label":"10-01","bucketSeconds":86400,"requests":1,"inputTokens":100,"outputTokens":20,"cachedInputTokens":80,"cacheWriteInputTokens":0,"reasoningOutputTokens":5,"totalTokens":120,"costUsd":"0.001","costIncomplete":false}]}}""";
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Login()
    {
        var response = Json(JsonSerializer.Serialize(new { code = 200, data = new { role = "key", expiresAt = DateTimeOffset.UtcNow.AddHours(2) } }));
        response.Headers.Add("Set-Cookie", "cpr_session=test-session; HttpOnly; Secure; Path=/; SameSite=Lax"); return response;
    }
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }

    private sealed class Server
    {
        public int Logins, Requests; public bool Fail, RejectSession, InvalidKey, Incomplete;
        public async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; Assert.Equal("site.example", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/prefix/v1/usage")
            {
                Assert.Equal("Bearer fixture-key", request.Headers.Authorization!.ToString());
                Assert.False(request.Headers.Contains("Cookie"));
                return Json(Budget, InvalidKey ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
            }
            Assert.Null(request.Headers.Authorization);
            if (path == "/prefix/api/auth/login")
            {
                Logins++; Assert.False(request.Headers.Contains("Cookie"));
                using var content = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("key", content.RootElement.GetProperty("mode").GetString());
                Assert.Equal("fixture-key", content.RootElement.GetProperty("apiKey").GetString());
                return Login();
            }
            Assert.Equal("cpr_session=test-session", Assert.Single(request.Headers.GetValues("Cookie")));
            if (RejectSession) { RejectSession = false; return Json("secret", HttpStatusCode.Unauthorized); }
            if (Fail) return Json("fixture-key secret", HttpStatusCode.BadGateway);
            if (path.EndsWith("/overview"))
            {
                var payload = JsonNode.Parse(Overview)!;
                if (Incomplete) payload["data"]!["trend"] = new JsonArray();
                return Json(payload.ToJsonString());
            }
            Assert.EndsWith("/records", path);
            Assert.Contains("kind=all", request.RequestUri.Query);
            return Json("""{"code":200,"data":{"items":[{"id":"a","createdAtDisplay":"2026-10-01 12:00:00","model":"gpt-test","status":"running","tokenDetails":null,"latencyMs":null}],"total":1}}""");
        }
    }
    [Fact]
    public async Task UsesKeyLoginCachesSessionAndKeepsRemoteTotalsSeparate()
    {
        var server = new Server(); using var http = new HttpClient(new Handler(server.Send)); using var client = new CodexProxyRsClient(http);
        var first = await client.ProbeAsync("https://site.example/prefix/v1", "fixture-key", "Site");
        var again = await client.ProbeAsync("https://site.example/prefix", "fixture-key", "Site");
        Assert.Equal(ProviderDataState.Available, first.DataState);
        Assert.Equal(1, server.Logins); Assert.Equal(120, again.RemoteUsage!.Cumulative.Total);
        Assert.Null(again.RemoteUsage.Calls[0].Tokens); Assert.Equal(1, again.RemoteUsage.Today.Summary.Requests);
        Assert.Equal(0.001m, again.RemoteUsage.Today.Summary.CostUsd);
    }
    [Fact]
    public async Task RelogsOnceAfterSessionExpiryAndRetainsLabeledOldDataOnFailure()
    {
        var server = new Server(); using var http = new HttpClient(new Handler(server.Send)); using var client = new CodexProxyRsClient(http);
        var good = await client.ProbeAsync("https://site.example/prefix", "fixture-key", "Site");
        server.RejectSession = true;
        Assert.Equal(ProviderDataState.Available, (await client.ProbeAsync("https://site.example/prefix", "fixture-key", "Site")).DataState);
        Assert.Equal(2, server.Logins);
        server.Fail = true;
        var fail = await client.ProbeAsync("https://site.example/prefix", "fixture-key", "Site");
        Assert.Equal(ProviderDataState.ReadFailed, fail.DataState);
        Assert.Contains("旧数据", fail.RemoteUsage!.Note); Assert.Equal(good.RemoteUsage!.Cumulative.Total, fail.RemoteUsage.Cumulative.Total);
        Assert.DoesNotContain("fixture-key", fail.Detail);
        server.InvalidKey = true;
        Assert.Null((await client.ProbeAsync("https://site.example/prefix", "fixture-key", "Site")).RemoteUsage);
    }
    [Theory]
    [InlineData(302)] [InlineData(404)] [InlineData(429)]
    public async Task HttpErrorsDoNotFollowRedirectsOrExposeBodies(int code)
    {
        var count = 0;
        using var http = new HttpClient(new Handler((_, _) => { count++; return Task.FromResult(Json("fixture-key secret", (HttpStatusCode)code)); }));
        using var client = new CodexProxyRsClient(http);
        var result = await client.ProbeAsync("https://site.example", "fixture-key", "Site");
        Assert.Equal(1, count); Assert.Equal(ProviderDataState.ReadFailed, result.DataState); Assert.DoesNotContain("fixture-key", result.Detail);
    }
    [Fact]
    public void SeparateBackendIdentityDoesNotOverwriteSub2ApiCredential()
    {
        var cpr = new UsageKeyIdentity("https://site.example", "12345678", "Site", CodexProxyRsParser.Kind);
        var sub = new UsageKeyIdentity("https://site.example/v1/usage", "12345678", "Site");
        Assert.True(cpr.IsCustom); Assert.True(cpr.IsCodexProxyRs); Assert.NotEqual(cpr.StorageHost, sub.StorageHost);
    }
    [Fact]
    public async Task CancelledBeforeRequestDoesNotSendOrThrow()
    {
        var server = new Server(); using var http = new HttpClient(new Handler(server.Send));
        using var client = new CodexProxyRsClient(http);
        var result = await client.ProbeAsync("https://site.example/prefix", "fixture-key", "Site", new CancellationToken(true));
        Assert.Equal(ProviderDataState.ReadFailed, result.DataState);
        Assert.Equal(0, server.Requests);
    }
    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"Start\":\"2026-10-01T00:00:00Z\",\"Seconds\":86400,\"Label\":\"day\",\"Metrics\":null}]")]
    [InlineData("{broken")]
    public async Task CorruptCacheIsIgnoredAndReplaced(string corrupt)
    {
        var folder = Path.Combine(Path.GetTempPath(), "vibegauge-cpr-" + Guid.NewGuid().ToString("N"));
        var server = new Server(); using var http = new HttpClient(new Handler(server.Send));
        try
        {
            using (var first = new CodexProxyRsClient(http, folder))
                await first.ProbeAsync("https://site.example/prefix", "fixture-key", "Site");
            File.WriteAllText(Assert.Single(Directory.GetFiles(folder)), corrupt);
            using var second = new CodexProxyRsClient(http, folder);
            var result = await second.ProbeAsync("https://site.example/prefix", "fixture-key", "Site");
            Assert.Equal(ProviderDataState.Available, result.DataState);
            Assert.Equal(120, result.RemoteUsage!.Cumulative.Total);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task RetainedHistorySurvivesRestartAndPartialTrendDoesNotEraseIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vibegauge-cpr-" + Guid.NewGuid().ToString("N"));
        var server = new Server(); using var http = new HttpClient(new Handler(server.Send));
        try
        {
            using (var first = new CodexProxyRsClient(http, folder))
                Assert.Equal(120, (await first.ProbeAsync("https://site.example/prefix", "fixture-key", "Site")).RemoteUsage!.Cumulative.Total);
            var saved = File.ReadAllText(Assert.Single(Directory.GetFiles(folder)));
            Assert.DoesNotContain("fixture-key", saved); Assert.DoesNotContain("test-session", saved);
            server.Incomplete = true;
            using var second = new CodexProxyRsClient(http, folder);
            var value = await second.ProbeAsync("https://site.example/prefix", "fixture-key", "Site");
            Assert.Equal(120, value.RemoteUsage!.Cumulative.Total); Assert.Contains("尚未完整", value.RemoteUsage.Note);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
