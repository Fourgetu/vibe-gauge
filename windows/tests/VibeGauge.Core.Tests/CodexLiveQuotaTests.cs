using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class CodexLiveQuotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 6, 0, 0, TimeSpan.Zero);
    private static CodexQuotaRequest Account(string scope = "pro-a") => new(scope, "Pro", true, Now.AddHours(-1));
    private static PlatformStatus Local(string scope = "pro-a") => new("Codex", "Pro", true, 2,
        ProviderDataState.NoQuota, "waiting", QuotaScope: scope);
    private static CodexQuotaResult Report(int percent = 44, DateTimeOffset? at = null) => new(
        Weekly: new(percent, Now.AddDays(3), at ?? Now, TimeSpan.FromDays(7)));

    [Fact]
    public void LoginWithoutAnySessionQuotaReceivesOfficialWeeklyWindow()
    {
        var client = new Client(_ => Task.FromResult(Report()));
        using var refresh = new CodexQuotaRefresh(client, new Clock());
        refresh.Apply(Local(), Account());
        var result = refresh.Apply(Local(), Account());
        Assert.Equal(44, result.Weekly?.UsedPercent);
        Assert.Null(result.FiveHour);
        Assert.Equal(ProviderDataState.Available, result.DataState);
        Assert.True(result.IsRunning);
        Assert.Equal(2, result.Sessions);
        Assert.Equal("pro-a", result.QuotaScope);
    }

    [Fact]
    public void AutomaticQueriesAreThrottledButManualRefreshRetriesImmediately()
    {
        var clock = new Clock();
        var client = new Client(_ => Task.FromResult(Report()));
        using var refresh = new CodexQuotaRefresh(client, clock);
        for (var i = 0; i < 20; i++) refresh.Apply(Local(), Account());
        Assert.Equal(1, client.Calls);
        clock.Now = Now.AddSeconds(61);
        refresh.Apply(Local(), Account());
        Assert.Equal(2, client.Calls);
        refresh.Apply(Local(), Account());
        refresh.Invalidate();
        refresh.Apply(Local(), Account());
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task SwitchingAccountsDiscardsTheOldInFlightResponse()
    {
        var old = new TaskCompletionSource<CodexQuotaResult>();
        var current = new TaskCompletionSource<CodexQuotaResult>();
        var client = new Client(a => a.Scope == "pro-a" ? old.Task : current.Task);
        using var refresh = new CodexQuotaRefresh(client, new Clock());
        Assert.Null(refresh.Apply(Local(), Account()).Weekly);
        Assert.Null(refresh.Apply(Local("pro-b"), Account("pro-b")).Weekly);
        Assert.Equal(2, client.Calls);
        Assert.True(client.Tokens[0].IsCancellationRequested);
        old.SetResult(Report(99));
        Assert.Null(refresh.Apply(Local("pro-b"), Account("pro-b")).Weekly);
        current.SetResult(Report(8));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        PlatformStatus result;
        do
        {
            result = refresh.Apply(Local("pro-b"), Account("pro-b"));
            if (result.Weekly is not null) break;
            await Task.Delay(10);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal(8, result.Weekly?.UsedPercent);
    }

    [Fact]
    public void ApiKeyModeClearsCachedSubscriptionAndNeverQueries()
    {
        var client = new Client(_ => Task.FromResult(Report()));
        using var refresh = new CodexQuotaRefresh(client, new Clock());
        refresh.Apply(Local(), Account());
        Assert.NotNull(refresh.Apply(Local(), Account()).Weekly);
        var api = Local("api-key") with { Tier = "API Key" };
        Assert.Null(refresh.Apply(api, null).Weekly);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public void FailedRefreshRetainsOnlySameAccountLastGoodReportAndMarksItStale()
    {
        var fail = false;
        var client = new Client(_ => Task.FromResult(fail ? new CodexQuotaResult(Error: "查询超时") : Report()));
        using var refresh = new CodexQuotaRefresh(client, new Clock());
        refresh.Apply(Local(), Account());
        refresh.Apply(Local(), Account());
        fail = true;
        refresh.Invalidate();
        refresh.Apply(Local(), Account());
        var result = refresh.Apply(Local(), Account());
        Assert.Equal(44, result.Weekly?.UsedPercent);
        Assert.Equal(ProviderDataState.Stale, result.DataState);
        Assert.Contains("查询超时", result.Detail);
        refresh.Apply(Local("pro-b"), Account("pro-b"));
        Assert.Null(refresh.Apply(Local("pro-b"), Account("pro-b")).Weekly);
    }

    [Fact]
    public void WeeklyOnlyProResponseRemovesAnOlderFiveHourWindow()
    {
        var client = new Client(_ => Task.FromResult(Report()));
        using var refresh = new CodexQuotaRefresh(client, new Clock());
        var old = Local() with
        {
            FiveHour = new(12, Now.AddHours(2), Now.AddMinutes(-5), TimeSpan.FromHours(5)),
            Weekly = Report(40, Now.AddMinutes(-5)).Weekly
        };
        refresh.Apply(old, Account());
        var result = refresh.Apply(old, Account());
        Assert.Null(result.FiveHour);
        Assert.Equal(44, result.Weekly?.UsedPercent);
    }

    [Fact]
    public void NewerLocalQuotaIsNotReplacedByAnOlderOfficialSnapshot()
    {
        var client = new Client(_ => Task.FromResult(Report()));
        using var refresh = new CodexQuotaRefresh(client, new Clock());
        refresh.Apply(Local(), Account());
        var local = Local() with { Weekly = Report(47, Now.AddMinutes(1)).Weekly, DataState = ProviderDataState.Available };
        Assert.Equal(47, refresh.Apply(local, Account()).Weekly?.UsedPercent);
    }

    private sealed class Client(Func<CodexQuotaRequest, Task<CodexQuotaResult>> read) : ICodexQuotaClient
    {
        public int Calls { get; private set; }
        public List<CancellationToken> Tokens { get; } = [];
        public Task<CodexQuotaResult> ReadAsync(CodexQuotaRequest account, CancellationToken cancellationToken)
        { Calls++; Tokens.Add(cancellationToken); return read(account); }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = CodexLiveQuotaTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
