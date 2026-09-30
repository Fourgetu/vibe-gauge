using System.IO;
using System.Text.Json;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class CodexAppServerQuotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 6, 0, 0, TimeSpan.Zero);
    private const string WeeklyOnly = """{"rateLimits":{"planType":"pro","primary":{"usedPercent":44,"windowDurationMins":10080,"resetsAt":1791064800},"secondary":null}}""";

    private static async Task<(CodexQuotaResult Result, string Requests)> Read(string body, string tier = "Pro", string plan = "pro", string? error = null)
    {
        using var payload = JsonDocument.Parse(body);
        var input = string.Join('\n',
            """{"id":1,"result":{"userAgent":"test"}}""",
            JsonSerializer.Serialize(new { id = 2, result = new { account = new { type = "chatgpt", planType = plan } } }),
            error ?? JsonSerializer.Serialize(new { id = 3, result = payload.RootElement })) + "\n";
        using var reader = new StringReader(input);
        using var writer = new StringWriter();
        var result = await CodexAppServerProtocol.ReadAsync(reader, writer, new("scope", tier, true, Now.AddHours(-1)), Now, CancellationToken.None);
        return (result, writer.ToString());
    }

    [Fact]
    public async Task ProWeeklyInPrimarySlotIsNotMislabelledAsFiveHours()
    {
        var (result, requests) = await Read(WeeklyOnly);
        Assert.Empty(result.Error);
        Assert.Null(result.FiveHour);
        Assert.Equal(44, result.Weekly?.UsedPercent);
        Assert.Equal(TimeSpan.FromDays(7), result.Weekly?.Window);
        Assert.Equal(Now, result.Weekly?.CapturedAt);
        var status = new PlatformStatus("Codex", "Pro", true, 2, ProviderDataState.Available, "", Weekly: result.Weekly);
        Assert.Equal("W", Assert.Single(PlatformRow.From(status).Quotas).Label);
        Assert.Equal("7 天窗口", Assert.Single(ProviderDetails.Windows(status)).Label);
        var sent = requests.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonDocument.Parse(x.Trim())).ToArray();
        try
        {
            Assert.Equal(new[] { "initialize", "initialized", "account/read", "account/rateLimits/read" }, sent.Select(x => x.RootElement.GetProperty("method").GetString()));
            Assert.False(sent[2].RootElement.GetProperty("params").GetProperty("refreshToken").GetBoolean());
            Assert.DoesNotContain("auth_token", requests);
        }
        finally { foreach (var doc in sent) doc.Dispose(); }
    }

    [Theory]
    [InlineData("Plus", "plus")]
    [InlineData("Team", "team")]
    public async Task DualWindowAccountsRetainBothFiveHourAndWeeklyQuota(string tier, string plan)
    {
        var body = JsonSerializer.Serialize(new { rateLimits = new { planType = plan,
            primary = new { usedPercent = 12, windowDurationMins = 300, resetsAt = 1790757600 },
            secondary = new { usedPercent = 44, windowDurationMins = 10080, resetsAt = 1791064800 } } });
        var (result, _) = await Read(body, tier, plan);
        Assert.Equal(12, result.FiveHour?.UsedPercent);
        Assert.Equal(44, result.Weekly?.UsedPercent);
    }

    [Fact]
    public async Task AuthoritativeMainBucketWinsAndExtraPoolKeepsItsOwnPlan()
    {
        var (result, _) = await Read("""
            {"rateLimits":{"planType":"team","primary":{"usedPercent":99,"windowDurationMins":300}},
             "rateLimitsByLimitId":{
               "codex":{"planType":"pro","primary":{"usedPercent":44,"windowDurationMins":10080}},
               "spark":{"planType":"free","limitName":"Spark","primary":{"usedPercent":8,"windowDurationMins":300}}
             }}
            """);
        Assert.Empty(result.Error);
        Assert.Null(result.FiveHour);
        Assert.Equal(44, result.Weekly?.UsedPercent);
        Assert.Equal(8, Assert.Single(result.Extra!).Window.UsedPercent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rateLimits\":{\"primary\":null,\"secondary\":null}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":null,\"windowDurationMins\":300}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":12,\"windowDurationMins\":0}}}")]
    public async Task MissingOrInvalidWindowsNeverBecomeZeroPercentQuota(string body)
    {
        var (result, _) = await Read(body);
        Assert.Null(result.FiveHour);
        Assert.Null(result.Weekly);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public async Task AccountPlanChangeDoesNotLeakAnotherAccountsQuota()
    {
        var (result, _) = await Read(WeeklyOnly, "Pro", "team");
        Assert.Null(result.Weekly);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public async Task RpcErrorsNeverExposeRawServerMessagesOrCredentials()
    {
        var (result, _) = await Read("{}", error: """{"id":3,"error":{"code":-32000,"message":"SECRET access_token=user@example.com"}}""");
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("SECRET", result.Error);
        Assert.DoesNotContain("user@example.com", result.Error);
    }
}
