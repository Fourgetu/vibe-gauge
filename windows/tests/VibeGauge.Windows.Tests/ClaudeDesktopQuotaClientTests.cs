using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class ClaudeDesktopQuotaClientTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly ClaudeDesktopQuotaAccount Account = new("test-profile", "opaque-scope", "opaque-revision", "Pro");
    private const string FakeToken = "test-only-bearer";
    private static object Entry(bool identity = true, double? expiry = null) => new
    {
        token = FakeToken, identityChecked = identity, expiresAt = expiry ?? Now.AddHours(1).ToUnixTimeMilliseconds(), subscriptionType = "pro"
    };
    private static string Key(string account = "account-a", string org = "org-a", string origin = "https://api.anthropic.com") =>
        $"acct:{account}|client:{org}:{origin}:user:profile user:inference";

    [Fact]
    public void SelectsOnlyCurrentAccountAndDoesNotExposeCredentialsInMetadata()
    {
        using var cache = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            { [Key()] = Entry(), [Key(account: "old-account")] = Entry(), [Key(origin: "https://api.anthropic.com.attacker.invalid")] = Entry() }));
        var selected = ClaudeDesktopCredentials.Select(cache.RootElement, "account-a", Path.GetTempPath(), Now)!;
        Assert.Equal(FakeToken, selected.Token);
        Assert.Equal("Pro", selected.Account.Tier);
        Assert.DoesNotContain(FakeToken, JsonSerializer.Serialize(selected.Account));
        Assert.DoesNotContain(FakeToken, JsonSerializer.Serialize(selected));
        Assert.DoesNotContain("account-a", selected.Account.Scope);
        Assert.DoesNotContain(FakeToken, selected.ToString());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("unverified")]
    [InlineData("wrong-origin")]
    [InlineData("wrong-account")]
    [InlineData("multiple-orgs")]
    public void UnusableOrAmbiguousCredentialsAreRejected(string scenario)
    {
        var entries = new Dictionary<string, object> { [Key(account: scenario == "wrong-account" ? "old-account" : "account-a",
            origin: scenario == "wrong-origin" ? "https://example.invalid" : "https://api.anthropic.com")] =
            Entry(identity: scenario != "unverified", expiry: scenario == "expired" ? Now.AddSeconds(-1).ToUnixTimeMilliseconds() : null) };
        if (scenario == "multiple-orgs") entries[Key(org: "org-b")] = Entry();
        using var cache = JsonDocument.Parse(JsonSerializer.Serialize(entries));
        Assert.Null(ClaudeDesktopCredentials.Select(cache.RootElement, "account-a", Path.GetTempPath(), Now));
    }

    [Fact]
    public void ReadsElectronV10CacheWithCurrentUserDpapiWithoutModifyingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-claude-crypto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var wrapped = "DPAPI"u8.ToArray().Concat(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)).ToArray();
            var plain = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { [Key()] = Entry() });
            var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
            using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag);
            var encrypted = Convert.ToBase64String("v10"u8.ToArray().Concat(nonce).Concat(cipher).Concat(tag).ToArray());
            var config = new Dictionary<string, object> { ["windowSizeWasSignedIn"] = true, ["lastKnownAccountUuid"] = "account-a", ["oauth:tokenCacheV2"] = encrypted };
            var content = JsonSerializer.Serialize(config);
            File.WriteAllText(Path.Combine(root, "config.json"), content);
            File.WriteAllText(Path.Combine(root, "Local State"), JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String(wrapped) } }));
            var result = ClaudeDesktopCredentials.Read(root, Now)!;
            Assert.Equal(FakeToken, result.Token);
            Assert.Equal(content, File.ReadAllText(Path.Combine(root, "config.json")));
            config["windowSizeWasSignedIn"] = false;
            File.WriteAllText(Path.Combine(root, "config.json"), JsonSerializer.Serialize(config));
            Assert.Null(ClaudeDesktopCredentials.Read(root, Now));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UsesOnlyOfficialReadEndpointAndParsesResetTime()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(FakeToken, request.Headers.Authorization!.Parameter);
            Assert.Contains("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta"));
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"five_hour":{"utilization":2,"resets_at":"2030-01-01T18:00:00Z"},"seven_day":{"utilization":0,"resets_at":null}}""") };
        });
        using var http = new HttpClient(handler);
        var client = new ClaudeDesktopQuotaClient(http, (_, _) => new(Account, FakeToken));
        var result = await client.ReadAsync(Account, CancellationToken.None);
        Assert.Empty(result.Error);
        Assert.Equal(2, result.FiveHour!.UsedPercent);
        Assert.Equal(0, result.Weekly!.UsedPercent);
        var row = PlatformRow.From(new(ClaudeDesktop.Name, "Pro", true, 1, ProviderDataState.Available, "", result.FiveHour, result.Weekly));
        Assert.True(row.ShowReset);
        Assert.True(row.ShowTier);
        Assert.True(row.IsWide);
        Assert.Equal("运行中", row.SessionText);
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
        var client = new ClaudeDesktopQuotaClient(http, (_, _) => new(Account, FakeToken));
        var result = await client.ReadAsync(Account, CancellationToken.None);
        Assert.NotEmpty(result.Error);
        Assert.Null(result.FiveHour);
        Assert.DoesNotContain("PRIVATE", result.Error);
    }

    [Fact]
    public async Task AccountSwitchDuringRequestDiscardsTheResponse()
    {
        var current = Account;
        using var http = new HttpClient(new Handler(_ =>
        {
            current = Account with { Scope = "other-account" };
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"five_hour":{"utilization":99}}""") };
        }));
        var client = new ClaudeDesktopQuotaClient(http, (_, _) => new(current, FakeToken));
        var result = await client.ReadAsync(Account, CancellationToken.None);
        Assert.NotEmpty(result.Error);
        Assert.Null(result.FiveHour);
    }

    [Fact]
    public void DecryptionErrorsAreSanitized()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var client = new ClaudeDesktopQuotaClient(http, (_, _) => throw new CryptographicException("PRIVATE details"));
        var result = client.ReadAccount("profile");
        Assert.Null(result.Account);
        Assert.DoesNotContain("PRIVATE", result.Error);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
