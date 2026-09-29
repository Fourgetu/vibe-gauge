using System.Text;
using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ProviderDetailsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-details-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private static PlatformStatus Empty(string name = "Claude") => new(name, "未登录", false, 0, ProviderDataState.NotSignedIn, "暂无数据");
    private static QuotaWindow Window => new(25, DateTimeOffset.Now.AddHours(3), DateTimeOffset.Now, TimeSpan.FromHours(5));

    [Fact]
    public void EmptyCardsDoNotOpenButUsefulDataDoes()
    {
        var empty = Empty();
        Assert.False(ProviderDetails.CanOpen(empty));
        Assert.False(ProviderDetails.CanOpen(empty with { Metadata = new([], ["a source path"]) }));
        Assert.True(ProviderDetails.CanOpen(empty with { FiveHour = Window }));
        Assert.True(ProviderDetails.CanOpen(empty with { ExtraQuotas = [new("extra", Window)] }));
        Assert.True(ProviderDetails.CanOpen(empty with { Metadata = new([new("套餐", "Pro")], []) }));
        Assert.True(ProviderDetails.CanOpen(empty with { IsRunning = true }));
        Assert.True(ProviderDetails.CanOpen(empty with { ReportedTokens = new(12000, null) }));
        Assert.True(ProviderDetails.CanOpen(empty with { AlwaysShowDetail = true }));
    }

    [Theory]
    [InlineData("PI-Desktop")]
    [InlineData("ZCode")]
    [InlineData("WorkBuddy")]
    [InlineData("DSH Desktop")]
    public void DesktopHistoryOpensWithoutRequiringTheClientToRun(string name)
    {
        var usage = new UsageSourceSummary(name, UsageDataState.Available, 2, 12000, 3000, 0, 1000, 20, "");
        Assert.True(ProviderDetails.CanOpen(Empty(name) with { DesktopTokens = new(null, usage, true) }));
        Assert.NotEmpty(ProviderDetails.Sources(Paths, Empty(name)));
    }

    [Theory]
    [InlineData("Claude", "Claude Code", true)]
    [InlineData("Codex", "Codex", true)]
    [InlineData("Codex", "Claude Code", false)]
    [InlineData("WorkBuddy", "DSH Desktop", false)]
    public void SourceMatchingDoesNotBlendClients(string provider, string source, bool expected) =>
        Assert.Equal(expected, ProviderDetails.Matches(provider, source));

    [Fact]
    public void MetadataCopiesOnlyAllowlistedAccountFields()
    {
        Directory.CreateDirectory(Paths.CodexRoot);
        File.WriteAllText(Paths.ClaudeSettings, """{"oauthAccount":{"organizationRateLimitTier":"max_5x","hasExtraUsageEnabled":true,"accessToken":"SECRET","emailAddress":"PRIVATE","accountUuid":"PRIVATE"}}""");
        var claims = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new { chatgpt_plan_type = "pro", chatgpt_subscription_active_until = "2026-10-01", account_id = "PRIVATE" },
            ["email"] = "PRIVATE"
        });
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(Path.Combine(Paths.CodexRoot, "auth.json"), JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt", tokens = new { id_token = $"header.{payload}.signature", access_token = "SECRET", refresh_token = "SECRET" }
        }));
        var claude = ProviderDetails.ReadMetadata(Paths, Empty());
        var codex = ProviderDetails.ReadMetadata(Paths, Empty("Codex"));
        Assert.Equal(2, claude.Rows.Count);
        Assert.Contains(new ProviderInfoRow("额外用量", "已开启"), claude.Rows);
        Assert.Equal(3, codex.Rows.Count);
        Assert.Contains(new ProviderInfoRow("套餐字段", "pro"), codex.Rows);
        var serialized = JsonSerializer.Serialize(new[] { claude, codex });
        Assert.DoesNotContain("SECRET", serialized);
        Assert.DoesNotContain("PRIVATE", serialized);
        Assert.DoesNotContain(payload, serialized);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{bad json")]
    [InlineData("{\"tokens\":{\"id_token\":\"broken.jwt\"}}")]
    public void InvalidMetadataDoesNotBreakDetails(string json)
    {
        Directory.CreateDirectory(Paths.CodexRoot);
        File.WriteAllText(Paths.ClaudeSettings, json);
        File.WriteAllText(Path.Combine(Paths.CodexRoot, "auth.json"), json);
        Assert.Empty(ProviderDetails.ReadMetadata(Paths, Empty()).Rows);
        Assert.Empty(ProviderDetails.ReadMetadata(Paths, Empty("Codex")).Rows);
    }

    [Fact]
    public void CodexExtraBucketsStayIndependentAndCached()
    {
        var folder = Path.Combine(Paths.CodexRoot, "sessions");
        Directory.CreateDirectory(folder);
        var now = DateTimeOffset.Now;
        object Rate(string id, int percent) => new { limit_id = id, limit_name = id == "spark" ? "Codex Spark" : "", plan_type = id == "codex" ? "pro" : "free",
            primary = new { used_percent = percent, window_minutes = 300, resets_at = now.AddHours(4).ToUnixTimeSeconds() } };
        string Line(object limits, int seconds) => JsonSerializer.Serialize(new { timestamp = now.AddSeconds(seconds), rate_limits = limits });
        var file = Path.Combine(folder, "main.jsonl");
        File.WriteAllLines(file, [Line(new[] { Rate("codex", 24), Rate("spark", 80) }, 0), Line(Rate("spark", 90), 1)]);
        var scanner = new QuotaScanner(Paths);
        PlatformStatus Read() => scanner.Scan(ProcessReport.Empty).Single(x => x.Name == "Codex");
        var first = Read();
        Assert.Equal(24, first.FiveHour?.UsedPercent);
        Assert.Equal("Pro", first.Tier);
        Assert.Equal(90, Assert.Single(first.ExtraQuotas!).Window.UsedPercent);
        Assert.StartsWith("Codex Spark", first.ExtraQuotas![0].Label);
        Assert.Equal(first, Read());
        File.WriteAllText(Path.Combine(folder, "newer.jsonl"), Line(Rate("spark", 93), 2));
        Assert.Equal(93, Assert.Single(Read().ExtraQuotas!).Window.UsedPercent);
        File.WriteAllText(file, Line(Rate("spark", 55), 3));
        var extraOnly = Read();
        Assert.Null(extraOnly.FiveHour);
        Assert.Equal(55, Assert.Single(extraOnly.ExtraQuotas!).Window.UsedPercent);
        Assert.True(ProviderDetails.CanOpen(extraOnly));
        Directory.Delete(folder, true);
        Assert.Empty(Read().ExtraQuotas!);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
