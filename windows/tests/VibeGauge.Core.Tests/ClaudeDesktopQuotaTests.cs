using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ClaudeDesktopQuotaTests
{
    private static readonly PlatformStatus Local = new(ClaudeDesktop.Name, "已登录", true, 1, ProviderDataState.NoQuota, "",
        Metadata: new([], [Path.Combine(Path.GetTempPath(), "claude-test-profile", "config.json")]));

    [Fact]
    public void OfficialWindowsKeepResetOffsetsAndZeroUsage()
    {
        using var json = JsonDocument.Parse("""{"five_hour":{"utilization":2.1,"resets_at":"2030-01-01T10:00:00-08:00"},"seven_day":{"utilization":0,"resets_at":null},"seven_day_sonnet":{"utilization":5,"resets_at":null}}""");
        var now = DateTimeOffset.UtcNow;
        var result = ClaudeDesktopQuotaParser.Parse(json.RootElement, now);
        Assert.Equal(3, result.FiveHour!.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T18:00:00Z"), result.FiveHour.ResetsAt);
        Assert.Equal(now, result.FiveHour.CapturedAt);
        Assert.Equal(0, result.Weekly!.UsedPercent);
        Assert.Null(result.Weekly.ResetsAt);
        Assert.Single(result.Extra!);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"five_hour\":null}")]
    [InlineData("{\"five_hour\":{\"utilization\":null}}")]
    [InlineData("{\"five_hour\":{\"utilization\":-1}}")]
    [InlineData("{\"five_hour\":{\"utilization\":\"0\"}}")]
    public void MissingOrInvalidUsageIsNotReportedAsZero(string content)
    {
        using var json = JsonDocument.Parse(content);
        var result = ClaudeDesktopQuotaParser.Parse(json.RootElement, DateTimeOffset.UtcNow);
        Assert.Null(result.FiveHour);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public void PollingIsThrottledAndManualRefreshRetriesImmediately()
    {
        var clock = new Clock(); var client = new Client(clock);
        using var refresh = new ClaudeDesktopQuotaRefresh(client, clock);
        refresh.Apply(Local);
        Assert.Equal(1, client.Calls);
        var status = refresh.Apply(Local)!;
        Assert.Equal(ProviderDataState.Available, status.DataState);
        Assert.Equal("Pro", status.Tier);
        Assert.Equal("scope-a", status.QuotaScope);
        for (var i = 0; i < 20; i++) refresh.Apply(Local);
        Assert.Equal(1, client.Calls);
        clock.Now = clock.Now.AddSeconds(60); refresh.Apply(Local);
        Assert.Equal(2, client.Calls);
        refresh.Apply(Local); refresh.Invalidate(); refresh.Apply(Local);
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public void FailedPollKeepsOnlyThisAccountsDataAndMarksItUntrusted()
    {
        var clock = new Clock(); var client = new Client(clock);
        using var refresh = new ClaudeDesktopQuotaRefresh(client, clock);
        refresh.Apply(Local); var first = refresh.Apply(Local)!;
        client.Error = "rate limited";
        refresh.Invalidate(); refresh.Apply(Local); var failed = refresh.Apply(Local)!;
        Assert.Equal(ProviderDataState.Stale, failed.DataState);
        Assert.Equal(first.FiveHour!.CapturedAt, failed.FiveHour!.CapturedAt);
        Assert.Equal("可能过期", failed.FiveHour.Trust(clock.Now));
        client.Account = client.Account with { Scope = "scope-b" };
        var changed = refresh.Apply(Local)!;
        Assert.Null(changed.FiveHour);
        Assert.Equal("scope-b", changed.QuotaScope);
    }

    [Fact]
    public void LogoutAndMissingCredentialsDiscardCachedQuota()
    {
        var clock = new Clock(); var client = new Client(clock);
        using var refresh = new ClaudeDesktopQuotaRefresh(client, clock);
        refresh.Apply(Local); Assert.NotNull(refresh.Apply(Local)!.FiveHour);
        var loggedOut = Local with { Tier = "未登录", DataState = ProviderDataState.NotSignedIn };
        Assert.Null(refresh.Apply(loggedOut)!.FiveHour);
        refresh.Apply(Local); Assert.NotNull(refresh.Apply(Local)!.FiveHour);
        client.HasCredentials = false;
        Assert.Null(refresh.Apply(Local)!.FiveHour);
        Assert.Null(refresh.Apply(null));
    }

    [Fact]
    public async Task SwitchedAccountCannotReceiveOldInflightResponse()
    {
        var clock = new Clock(); var client = new Client(clock) { Pending = new() };
        using var refresh = new ClaudeDesktopQuotaRefresh(client, clock);
        refresh.Apply(Local);
        var old = client.Pending;
        client.Account = client.Account with { Scope = "scope-b" };
        client.Pending = new();
        refresh.Apply(Local);
        old.SetResult(new(new(99, clock.Now.AddHours(1), clock.Now, TimeSpan.FromHours(5))));
        Assert.Null(refresh.Apply(Local)!.FiveHour);
        client.Pending.SetResult(new(new(7, clock.Now.AddHours(1), clock.Now, TimeSpan.FromHours(5))));
        PlatformStatus? result = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            result = refresh.Apply(Local);
            if (result?.FiveHour is not null) break;
            await Task.Delay(10);
        }
        Assert.Equal(7, result!.FiveHour!.UsedPercent);
    }

    [Fact]
    public void RotatedCredentialIsRetriedWithoutWaitingForTimer()
    {
        var clock = new Clock(); var client = new Client(clock);
        using var refresh = new ClaudeDesktopQuotaRefresh(client, clock);
        refresh.Apply(Local); refresh.Apply(Local);
        client.Account = client.Account with { Revision = "rotated" };
        refresh.Apply(Local);
        Assert.Equal(2, client.Calls);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Client(Clock clock) : IClaudeDesktopQuotaClient
    {
        public ClaudeDesktopQuotaAccount Account = new("test", "scope-a", "revision-a", "Pro");
        public bool HasCredentials = true;
        public int Calls;
        public string Error = "";
        public TaskCompletionSource<ClaudeDesktopQuotaResult>? Pending;
        public ClaudeDesktopQuotaAccess ReadAccount(string profile) => HasCredentials ? new(Account) : new(Error: "expired");
        public Task<ClaudeDesktopQuotaResult> ReadAsync(ClaudeDesktopQuotaAccount account, CancellationToken cancellationToken)
        {
            Calls++;
            return Pending?.Task ?? Task.FromResult(Error.Length > 0 ? new ClaudeDesktopQuotaResult(Error: Error) :
                new(new(20, clock.Now.AddHours(2), clock.Now, TimeSpan.FromHours(5))));
        }
    }
}
