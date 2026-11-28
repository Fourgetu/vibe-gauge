using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class ClaudeDesktopQuotaClientTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly ClaudeDesktopQuotaAccount OAuthAccount = new("test-profile", "opaque-scope", "opaque-revision", "Pro", "oauth");
    private static readonly ClaudeDesktopQuotaAccount WebAccount = new("test-profile", "web-scope", "web-revision", "Web 订阅", "web");
    private const string FakeOAuthToken = "sk-ant-oat-test-only-token";
    private const string FakeSessionKey = "sk-ant-sid01-test-only-session-key";

    [Theory]
    [InlineData("sk-ant-sid01-test-only-session-key", "sk-ant-sid01-test-only-session-key")]
    [InlineData("Cookie: foo=bar; sessionKey=sk-ant-sid01-test-only-session-key; other=1", "sk-ant-sid01-test-only-session-key")]
    [InlineData("sessionKey=sk-ant-sid01-test-only-session-key", "sk-ant-sid01-test-only-session-key")]
    [InlineData("not-a-valid-key", null)]
    [InlineData("", null)]
    public void ExtractsOnlyValidSessionKeyFromRawOrCookieHeader(string raw, string? expected)
    {
        Assert.Equal(expected, ClaudeDesktopCredentials.ExtractSessionKey(raw));
    }

    [Fact]
    public void ReadsPlainClaudeCodeCredentialsWithProfileScopeWithoutDpapi()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-claude-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = new
            {
                claudeAiOauth = new
                {
                    accessToken = FakeOAuthToken,
                    expiresAt = Now.AddHours(2).ToUnixTimeMilliseconds(),
                    scopes = new[] { "user:inference", "user:profile" },
                    subscriptionType = "max",
                    rateLimitTier = "default_claude_max_5x"
                }
            };
            File.WriteAllText(Path.Combine(root, ".credentials.json"), JsonSerializer.Serialize(payload));
            var cred = ClaudeDesktopCredentials.ReadClaudeCodeOAuth(root, root, Now)!;
            Assert.NotNull(cred);
            Assert.Equal(FakeOAuthToken, cred.Secret);
            Assert.Equal("Max 5x", cred.Account.Tier);
            Assert.Equal("oauth", cred.Account.SourceKind);
            Assert.DoesNotContain(FakeOAuthToken, JsonSerializer.Serialize(cred.Account));
            Assert.DoesNotContain(FakeOAuthToken, JsonSerializer.Serialize(cred));
            Assert.DoesNotContain(FakeOAuthToken, cred.ToString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("missing-profile-scope")]
    [InlineData("mcp-only")]
    public void RejectsExpiredOrMissingProfileScopeClaudeCodeCredentials(string scenario)
    {
        var json = scenario switch
        {
            "expired" => JsonSerializer.Serialize(new
            {
                claudeAiOauth = new { accessToken = FakeOAuthToken, expiresAt = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), scopes = new[] { "user:profile" } }
            }),
            "missing-profile-scope" => JsonSerializer.Serialize(new
            {
                claudeAiOauth = new { accessToken = FakeOAuthToken, expiresAt = Now.AddHours(1).ToUnixTimeMilliseconds(), scopes = new[] { "user:inference" } }
            }),
            _ => JsonSerializer.Serialize(new { mcpOAuth = new { someServer = new { accessToken = FakeOAuthToken } } })
        };
        using var doc = JsonDocument.Parse(json);
        Assert.Null(ClaudeDesktopCredentials.SelectClaudeCodeOAuth(doc.RootElement, Path.GetTempPath(), Now));
    }

    [Fact]
    public void OffModeDisablesCredentialAccessAndNeverReadsSecrets()
    {
        var readCalled = false;
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("must not call network")));
        var client = new ClaudeDesktopQuotaClient(http, () => "off", (_, _, _) => { readCalled = true; return null; });
        var access = client.ReadAccount("profile");
        Assert.True(access.Disabled);
        Assert.Null(access.Account);
        Assert.False(readCalled);
    }

    [Fact]
    public async Task OAuthModeCallsUsageEndpointAndParsesQuotas()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(FakeOAuthToken, request.Headers.Authorization!.Parameter);
            Assert.Contains("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta"));
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"five_hour":{"utilization":2,"resets_at":"2030-01-01T18:00:00Z"},"seven_day":{"utilization":0,"resets_at":null}}""") };
        });
        using var http = new HttpClient(handler);
        var client = new ClaudeDesktopQuotaClient(http, () => "oauth", (_, _, _) => new(OAuthAccount, FakeOAuthToken));
        var result = await client.ReadAsync(OAuthAccount, CancellationToken.None);
        Assert.Empty(result.Error);
        Assert.Equal(2, result.FiveHour!.UsedPercent);
        Assert.Equal(0, result.Weekly!.UsedPercent);
        var row = PlatformRow.From(new("Claude", "Pro", true, 1, ProviderDataState.Available, "", result.FiveHour, result.Weekly));
        Assert.True(row.ShowReset);
        Assert.True(row.ShowTier);
        Assert.False(row.IsWide);
        Assert.Equal("1 个会话", row.SessionText);
    }

    [Fact]
    public async Task WebModeResolvesOrgUuidAndFetchesUsageWithManualSessionKey()
    {
        var calls = new List<string>();
        var handler = new Handler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            Assert.Contains("sessionKey=" + FakeSessionKey, request.Headers.GetValues("Cookie"));
            if (request.RequestUri.AbsoluteUri == "https://claude.ai/api/organizations")
                return new(HttpStatusCode.OK) { Content = new StringContent("""[{"uuid":"org-1234-abcd","name":"Personal"}]""") };
            Assert.Equal("https://claude.ai/api/organizations/org-1234-abcd/usage", request.RequestUri.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"five_hour":{"utilization":35,"resets_at":"2030-01-01T18:00:00Z"},"seven_day":{"utilization":12,"resets_at":null}}""") };
        });
        using var http = new HttpClient(handler);
        var client = new ClaudeDesktopQuotaClient(http, () => "web", (_, _, _) => new(WebAccount, FakeSessionKey));
        var result = await client.ReadAsync(WebAccount, CancellationToken.None);
        Assert.Empty(result.Error);
        Assert.Equal(35, result.FiveHour!.UsedPercent);
        Assert.Equal(12, result.Weekly!.UsedPercent);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task WebModeDetectsCloudflareChallengeWithoutBypassing()
    {
        var handler = new Handler(_ =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html>Just a moment...</html>", System.Text.Encoding.UTF8, "text/html")
            };
            res.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
            return res;
        });
        using var http = new HttpClient(handler);
        var client = new ClaudeDesktopQuotaClient(http, () => "web", (_, _, _) => new(WebAccount, FakeSessionKey));
        var result = await client.ReadAsync(WebAccount, CancellationToken.None);
        Assert.True(result.RateLimited);
        Assert.Contains("Cloudflare", result.Error);
        Assert.Null(result.FiveHour);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task HttpFailuresNeverBecomeZeroQuotaOrExposeBody(int code)
    {
        using var http = new HttpClient(new Handler(_ => new((HttpStatusCode)code) { Content = new StringContent("PRIVATE response body") }));
        var client = new ClaudeDesktopQuotaClient(http, () => "oauth", (_, _, _) => new(OAuthAccount, FakeOAuthToken));
        var result = await client.ReadAsync(OAuthAccount, CancellationToken.None);
        Assert.NotEmpty(result.Error);
        Assert.Null(result.FiveHour);
        Assert.DoesNotContain("PRIVATE", result.Error);
    }

    [Fact]
    public async Task CredentialChangeDuringRequestDiscardsTheResponse()
    {
        var current = OAuthAccount;
        using var http = new HttpClient(new Handler(_ =>
        {
            current = OAuthAccount with { Scope = "other-account" };
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"five_hour":{"utilization":99}}""") };
        }));
        var client = new ClaudeDesktopQuotaClient(http, () => "oauth", (_, _, _) => new(current, FakeOAuthToken));
        var result = await client.ReadAsync(OAuthAccount, CancellationToken.None);
        Assert.NotEmpty(result.Error);
        Assert.Null(result.FiveHour);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
