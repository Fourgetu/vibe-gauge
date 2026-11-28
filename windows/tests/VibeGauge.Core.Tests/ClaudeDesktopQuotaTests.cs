using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ClaudeDesktopQuotaTests
{
    private static readonly PlatformStatus Local = new("Claude", "Pro", true, 1, ProviderDataState.NoQuota, "",
        Metadata: new([], [Path.Combine(Path.GetTempPath(), "claude-test-profile", ".credentials.json")]));

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
        clock.Now = clock.Now.AddMinutes(2); refresh.Apply(Local);
        Assert.Equal(1, client.Calls);
        clock.Now = clock.Now.AddMinutes(1); refresh.Apply(Local);
        Assert.Equal(2, client.Calls);
        refresh.Apply(Local); refresh.Invalidate(); refresh.Apply(Local);
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public void DisabledSourceSkipsNetworkCallsAndPreservesLocalStatus()
    {
        var clock = new Clock(); var client = new Client(clock) { Disabled = true };
        using var refresh = new ClaudeDesktopQuotaRefresh(client, clock);
        var status = refresh.Apply(Local)!;
        Assert.Equal(0, client.Calls);
        Assert.Equal(ProviderDataState.NoQuota, status.DataState);
        Assert.Null(status.FiveHour);
        Assert.Contains("仅本地", status.Detail);
    }

    [Fact]
    public void RateLimitedFailureAppliesExponentialBackoff()
    {
        var clock = new Clock(); var client = new Client(clock) { Error = "429", RateLimited = true };
        using var refresh = new ClaudeDesktopQuotaRefresh(client, clock);
        refresh.Apply(Local); refresh.Apply(Local);
        Assert.Equal(1, client.Calls);
        clock.Now = clock.Now.AddMinutes(3); refresh.Apply(Local);
        Assert.Equal(1, client.Calls);
        clock.Now = clock.Now.AddMinutes(9); refresh.Apply(Local);
        Assert.Equal(2, client.Calls);
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
    public void MissingCredentialsDiscardCachedQuotaAndPreserveStatusLineQuota()
    {
        var clock = new Clock(); var client = new Client(clock);
        using var refresh = new ClaudeDesktopQuotaRefresh(client, clock);
        refresh.Apply(Local); Assert.NotNull(refresh.Apply(Local)!.FiveHour);
        client.HasCredentials = false;
        Assert.Null(refresh.Apply(Local)!.FiveHour);
        Assert.Null(refresh.Apply(null));

        // When statusLine quota is already present on Claude card, disabled/missing OAuth preserves it.
        var withStatusLine = Local with
        {
            DataState = ProviderDataState.Available,
            FiveHour = new(42, clock.Now.AddHours(3), clock.Now.AddMinutes(1), TimeSpan.FromHours(5))
        };
        Assert.Equal(42, refresh.Apply(withStatusLine)!.FiveHour!.UsedPercent);
        client.Disabled = true;
        Assert.Equal(42, refresh.Apply(withStatusLine)!.FiveHour!.UsedPercent);
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
        public bool Disabled;
        public bool RateLimited;
        public int Calls;
        public string Error = "";
        public TaskCompletionSource<ClaudeDesktopQuotaResult>? Pending;
        public ClaudeDesktopQuotaAccess ReadAccount(string profile) => Disabled ? new(Disabled: true) : HasCredentials ? new(Account) : new(Error: "expired");
        public Task<ClaudeDesktopQuotaResult> ReadAsync(ClaudeDesktopQuotaAccount account, CancellationToken cancellationToken)
        {
            Calls++;
            return Pending?.Task ?? Task.FromResult(Error.Length > 0 ? new ClaudeDesktopQuotaResult(Error: Error, RateLimited: RateLimited) :
                new(new(20, clock.Now.AddHours(2), clock.Now, TimeSpan.FromHours(5))));
        }
    }
}
