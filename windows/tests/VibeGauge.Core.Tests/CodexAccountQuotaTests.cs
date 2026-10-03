using System.Text;
using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class CodexAccountQuotaTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "vibegauge-account-quota-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset now = DateTimeOffset.Now;
    private AppPaths Paths => new(home, Path.Combine(home, "local"));
    private string Auth => Path.Combine(Paths.CodexRoot, "auth.json");
    private string Log => Path.Combine(Paths.CodexRoot, "sessions", "session.jsonl");

    public CodexAccountQuotaTests() => Directory.CreateDirectory(Path.GetDirectoryName(Log)!);
    private PlatformStatus Read(QuotaScanner scanner) => scanner.Scan(ProcessReport.Empty).Single(x => x.Name == "Codex");
    private void Login(string plan, string account, DateTimeOffset at, int revision = 1)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new { chatgpt_plan_type = plan, chatgpt_account_id = account },
            ["iat"] = revision
        }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(Auth, JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt", OPENAI_API_KEY = (string?)null,
            tokens = new { id_token = $"header.{payload}.signature", access_token = "FIXTURE-SECRET" }
        }));
        File.SetLastWriteTimeUtc(Auth, at.UtcDateTime);
    }
    private string Quota(string plan, DateTimeOffset at, int percent = 21, string id = "codex") => JsonSerializer.Serialize(new
    {
        timestamp = at,
        rate_limits = new
        {
            limit_id = id, plan_type = plan,
            secondary = new { used_percent = percent, window_minutes = 10080, resets_at = now.AddDays(3).ToUnixTimeSeconds() }
        }
    });

    [Fact]
    public void CurrentProLoginCannotBeOverwrittenByTeamSession()
    {
        Login("pro", "current", now.AddMinutes(-20));
        File.WriteAllText(Log, Quota("team", now.AddMinutes(-5)));
        var result = Read(new(Paths));
        Assert.Equal("Pro", result.Tier);
        Assert.Null(result.Weekly);
        Assert.Contains("等待当前登录的新额度回报", result.Detail);
    }

    [Fact]
    public void ApiKeyModeDoesNotReuseSubscriptionQuotas()
    {
        File.WriteAllText(Auth, """{"OPENAI_API_KEY":"FIXTURE-SECRET"}""");
        File.WriteAllText(Log, Quota("team", now));
        var result = Read(new(Paths));
        Assert.Equal("API Key", result.Tier);
        Assert.Null(result.Weekly);
        Assert.Empty(result.ExtraQuotas ?? []);
    }

    [Fact]
    public void NewLoginWithoutPlanDoesNotInferTeamFromBeforeLogin()
    {
        File.WriteAllText(Auth, """{"auth_mode":"chatgpt","tokens":{}}""");
        File.SetLastWriteTimeUtc(Auth, now.AddMinutes(-10).UtcDateTime);
        File.WriteAllText(Log, Quota("team", now.AddHours(-2)));
        var result = Read(new(Paths));
        Assert.Equal("ChatGPT 登录", result.Tier);
        Assert.Null(result.Weekly);
    }

    [Fact]
    public void SwitchingAccountFiltersCachedFilesAndAcceptsNewReport()
    {
        Login("team", "old", now.AddMinutes(-30));
        File.WriteAllText(Log, Quota("team", now.AddMinutes(-25)));
        var scanner = new QuotaScanner(Paths);
        Assert.Equal(21, Read(scanner).Weekly?.UsedPercent);
        Login("pro", "new", now.AddMinutes(-10));
        var switched = Read(scanner);
        Assert.Equal("Pro", switched.Tier);
        Assert.Null(switched.Weekly);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Log)!, "new.jsonl"), Quota("pro", now.AddMinutes(-5), 8));
        Assert.Equal(8, Read(scanner).Weekly?.UsedPercent);
    }

    [Fact]
    public void SamePlanAccountSwitchCannotReuseOldAccountQuota()
    {
        Login("pro", "first", now.AddMinutes(-30));
        File.WriteAllText(Log, Quota("pro", now.AddMinutes(-25)));
        var scanner = new QuotaScanner(Paths);
        Assert.NotNull(Read(scanner).Weekly);
        Login("pro", "second", now.AddMinutes(-10));
        Assert.Null(Read(scanner).Weekly);
    }

    [Fact]
    public void TokenRefreshForSameKnownAccountPreservesObservedQuota()
    {
        Login("pro", "same", now.AddMinutes(-30));
        File.WriteAllText(Log, Quota("pro", now.AddMinutes(-25)));
        var scanner = new QuotaScanner(Paths);
        var first = Read(scanner);
        Login("pro", "same", now.AddMinutes(-10), revision: 2);
        var refreshed = Read(scanner);
        Assert.Equal(first.Weekly, refreshed.Weekly);
        Assert.Equal(first.QuotaScope, refreshed.QuotaScope);
        Assert.DoesNotContain("same", refreshed.QuotaScope);
        Assert.DoesNotContain("FIXTURE-SECRET", JsonSerializer.Serialize(refreshed));
    }

    [Fact]
    public void FreshScannerWaitsForReportAfterLatestLoginFile()
    {
        Login("pro", "current", now.AddMinutes(-10));
        File.WriteAllText(Log, Quota("pro", now.AddHours(-2)));
        Assert.Null(Read(new(Paths)).Weekly);
    }

    [Fact]
    public void MismatchedAccountErrorAndExtraPoolsCannotOverrideCurrentQuota()
    {
        Login("pro", "current", now.AddMinutes(-20));
        File.WriteAllLines(Log,
        [
            Quota("team", now.AddMinutes(-10)), Quota("free", now.AddMinutes(-9), 80, "spark"),
            JsonSerializer.Serialize(new { timestamp = now.AddMinutes(-8), payload = new { codex_error_info = "usage_limit_exceeded", message = "limit" } })
        ]);
        var result = Read(new(Paths));
        Assert.Equal("Pro", result.Tier);
        Assert.Null(result.Weekly);
        Assert.Empty(result.ExtraQuotas ?? []);
    }

    [Fact]
    public void CurrentAccountExtraPoolMayHaveDifferentPlanLabel()
    {
        Login("pro", "current", now.AddMinutes(-20));
        File.WriteAllLines(Log, [Quota("pro", now.AddMinutes(-10)), Quota("free", now.AddMinutes(-9), 80, "spark")]);
        var result = Read(new(Paths));
        Assert.Equal("Pro", result.Tier);
        Assert.Equal(21, result.Weekly?.UsedPercent);
        Assert.Equal(80, Assert.Single(result.ExtraQuotas!).Window.UsedPercent);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    public void InvalidCurrentAuthDoesNotFallBackToHistoricalTeam(string content)
    {
        File.WriteAllText(Auth, content);
        File.WriteAllText(Log, Quota("team", now));
        var result = Read(new(Paths));
        Assert.Equal(ProviderDataState.ReadFailed, result.DataState);
        Assert.Null(result.Weekly);
        Assert.DoesNotContain("Team", result.Tier);
    }

    [Fact]
    public void CurrentProWithoutFreshLogsCanUseItsOfficialQuotaResponse()
    {
        Login("pro", "current", now.AddMinutes(-10));
        File.WriteAllText(Log, Quota("team", now.AddHours(-2), 99));
        var client = new OfficialClient(now);
        using var scanner = new QuotaScanner(Paths, client);
        Read(scanner);
        var result = Read(scanner);
        Assert.Equal("Pro", result.Tier);
        Assert.Equal(44, result.Weekly?.UsedPercent);
        Assert.Null(result.FiveHour);
        Assert.Equal(result.QuotaScope, client.Request?.Scope);
        Assert.DoesNotContain("current", result.QuotaScope);
        Assert.DoesNotContain("FIXTURE-SECRET", JsonSerializer.Serialize(result));
    }

    private sealed class OfficialClient(DateTimeOffset captured) : ICodexQuotaClient
    {
        public CodexQuotaRequest? Request { get; private set; }
        public Task<CodexQuotaResult> ReadAsync(CodexQuotaRequest account, CancellationToken token)
        {
            Request = account;
            return Task.FromResult(new CodexQuotaResult(Weekly: new(44, captured.AddDays(3), captured, TimeSpan.FromDays(7))));
        }
    }

    [Fact]
    public void DifferentAccountsDoNotShareQuotaBurnSamples()
    {
        var sampler = new QuotaSampler(Paths);
        var first = new PlatformStatus("Codex", "Pro", true, 1, ProviderDataState.Available, "",
            Weekly: new(20, now.AddDays(3), now, TimeSpan.FromDays(7)), QuotaScope: "first");
        sampler.Apply([first], now);
        var second = first with { QuotaScope = "second", Weekly = first.Weekly! with { UsedPercent = 40, CapturedAt = now.AddMinutes(20) } };
        var result = Assert.Single(new QuotaSampler(Paths).Apply([second], now.AddMinutes(20)));
        Assert.Equal(0, result.Weekly!.RecentPercentPerHour);
        var sameAccount = first with { Weekly = second.Weekly };
        Assert.Equal(60, Assert.Single(sampler.Apply([sameAccount], now.AddMinutes(20))).Weekly!.RecentPercentPerHour);
    }

    [Fact]
    public void DifferentAccountsDoNotSuppressEachOthersQuotaNotifications()
    {
        var platform = new PlatformStatus("Codex", "Pro", true, 1, ProviderDataState.Available, "",
            Weekly: new(95, now.AddDays(3), now, TimeSpan.FromDays(7)), QuotaScope: "first");
        var snapshot = new DashboardSnapshot(now, new(50, 8, 32, 0, 5, 100, 500, 0), ProcessReport.Empty,
            [platform], UsageSummary.Empty, ApiUsageSummary.Empty);
        var policy = new AttentionPolicy(Paths);
        var options = new FeaturePreferences { QuotaNotifications = true };
        var low = platform with { Weekly = platform.Weekly! with { UsedPercent = 70 } };
        Assert.Empty(policy.Evaluate(snapshot with { Platforms = [low] }, options));
        Assert.Single(policy.Evaluate(snapshot, options));
        Assert.Empty(policy.Evaluate(snapshot, options));
        Assert.Empty(policy.Evaluate(snapshot with { Platforms = [low with { QuotaScope = "second" }] }, options));
        Assert.Single(policy.Evaluate(snapshot with { Platforms = [platform with { QuotaScope = "second" }] }, options));
        Assert.Empty(new AttentionPolicy(Paths).Evaluate(snapshot, options));
    }

    [Fact]
    public void ApiModeShowsNumericUsageAndAccountModeRestoresOnlyFreshQuota()
    {
        var today = new UsageSourceSummary("Codex", UsageDataState.Available, 2, 100, 50, 0, 20, 5, "");
        var total = today with { Turns = 10, ContextTokens = 1000, OutputTokens = 200 };
        var usage = UsageSummary.Empty with { Sources = [today] };
        var scanner = new QuotaScanner(Paths);
        File.WriteAllText(Auth, """{"auth_mode":"apikey","OPENAI_API_KEY":"fixture"}""");
        PlatformStatus Scan() => scanner.Scan(ProcessReport.Empty, usage, codexTotal: total).Single(x => x.Name == "Codex");
        var api = Scan();
        Assert.Equal(120, api.DesktopTokens?.Today?.TotalTokens);
        Assert.Equal(1200, api.DesktopTokens?.Total?.TotalTokens);
        Assert.Null(api.Weekly);
        Login("pro", "new", now.AddMinutes(-2));
        File.WriteAllText(Log, Quota("pro", now.AddMinutes(-1), 37));
        var account = Scan();
        Assert.Null(account.DesktopTokens); Assert.Equal(37, account.Weekly?.UsedPercent);
        File.WriteAllText(Auth, """{"auth_mode":"apikey","OPENAI_API_KEY":"fixture"}""");
        Assert.NotNull(Scan().DesktopTokens); Assert.Null(Scan().Weekly);
        File.WriteAllText(Auth, "broken");
        Assert.Null(Scan().DesktopTokens); Assert.Equal(ProviderDataState.ReadFailed, Scan().DataState);
    }

    public void Dispose() { if (Directory.Exists(home)) Directory.Delete(home, true); }
}
