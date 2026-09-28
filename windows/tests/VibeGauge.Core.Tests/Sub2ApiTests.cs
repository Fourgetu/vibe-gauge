using System.Text.Json;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class Sub2ApiTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00+08:00");
    private static PlatformStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Sub2ApiParser.Parse("My site", doc.RootElement, Now);
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com/v1/usage")]
    [InlineData("https://example.com/", "https://example.com/v1/usage")]
    [InlineData("https://EXAMPLE.com/v1/", "https://example.com/v1/usage")]
    [InlineData("https://example.com/prefix/v1/usage", "https://example.com/prefix/v1/usage")]
    [InlineData("https://example.com:8443/sub", "https://example.com:8443/sub/v1/usage")]
    [InlineData("https://example.com/v1/v1", "https://example.com/v1/v1/usage")]
    [InlineData("http://localhost:8080", "http://localhost:8080/v1/usage")]
    [InlineData("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080/v1/usage")]
    [InlineData("http://[::1]:8080", "http://[::1]:8080/v1/usage")]
    public void NormalizesUsageEndpointAndPreservesPrefixes(string input, string expected)
    {
        Assert.Equal(expected, Sub2ApiEndpoint.UsageUri(input).AbsoluteUri);
        var normalized = Sub2ApiEndpoint.Normalize(input);
        Assert.Equal(normalized, Sub2ApiEndpoint.Normalize(normalized));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://192.168.1.1")]
    [InlineData("https://user:password@example.com")]
    [InlineData("https://example.com?key=secret")]
    [InlineData("https://example.com#fragment")]
    [InlineData("ftp://example.com")]
    [InlineData("example.com")]
    [InlineData("https://example.com/\\evil")]
    [InlineData("https://exa\nmple.com")]
    [InlineData("")]
    public void RejectsUnsafeOrAmbiguousEndpoints(string input) =>
        Assert.Throws<ArgumentException>(() => Sub2ApiEndpoint.Normalize(input));

    [Fact]
    public void ShowsServerWalletAndActualKeyUsageWithoutInventingQuota()
    {
        var value = Parse("""
            {"mode":"unrestricted","isValid":true,"unit":"USD","balance":12.3456,
             "usage":{"today":{"actual_cost":1.2345,"cost":99,"requests":12,"total_tokens":3000},
                      "total":{"actual_cost":8,"requests":40}}}
            """);
        Assert.Equal(ProviderDataState.Available, value.DataState);
        Assert.Contains("钱包余额 12.3456 USD", value.Detail);
        Assert.Contains("今日 1.2345 USD · 12 次 · 3,000 tokens", value.Detail);
        Assert.DoesNotContain("99", value.Detail);
        Assert.Null(value.FiveHour);
        Assert.Null(value.Daily);
        Assert.True(value.AlwaysShowDetail);
    }

    [Fact]
    public void ShowsKeyQuotaAndEachServerRateWindow()
    {
        var value = Parse("""
            {"mode":"quota_limited","isValid":true,"status":"quota_exhausted",
             "quota":{"limit":100,"used":110,"remaining":0,"unit":"USD"},
             "rate_limits":[{"window":"5h","limit":20,"used":10,"reset_at":"2026-09-28T16:00:00+08:00"},
                            {"window":"1d","limit":40,"used":10},
                            {"window":"7d","limit":100,"used":110}]}
            """);
        Assert.Equal(50, value.FiveHour!.UsedPercent);
        Assert.Equal(Now.AddHours(4), value.FiveHour.ResetsAt);
        Assert.Equal(25, value.Daily!.UsedPercent);
        Assert.Equal(100, value.Weekly!.UsedPercent);
        Assert.Contains("Key 剩余额度 0.00 USD", value.Detail);
        Assert.Contains("已用尽", value.Detail);
    }

    [Fact]
    public void ParsesSubscriptionAndDoesNotGuessDailyMonthlyResetTimes()
    {
        var value = Parse("""
            {"mode":"unrestricted","isValid":true,"remaining":3,"unit":"USD",
             "subscription":{"daily_usage_usd":2,"daily_limit_usd":5,"weekly_usage_usd":4,"weekly_limit_usd":20,
               "monthly_usage_usd":10,"monthly_limit_usd":100,"weekly_window_start":"2026-09-25T12:00:00+08:00",
               "expires_at":"2026-10-25T12:00:00+08:00"}}
            """);
        Assert.Equal(40, value.Daily!.UsedPercent);
        Assert.Equal(20, value.Weekly!.UsedPercent);
        Assert.Equal(10, value.Monthly!.UsedPercent);
        Assert.Null(value.Daily.ResetsAt);
        Assert.Null(value.Monthly.ResetsAt);
        Assert.Equal(Now.AddDays(4), value.Weekly.ResetsAt);
        Assert.Contains("日用量 2.00 USD / 5.00 USD", value.Detail);
    }

    [Fact]
    public void MissingZeroAndNegativeLimitsNeverBecomeFabricatedProgress()
    {
        var value = Parse("""
            {"mode":"unrestricted","isValid":true,"remaining":-1,
             "subscription":{"daily_usage_usd":2,"daily_limit_usd":0,"monthly_usage_usd":3,"monthly_limit_usd":null},
             "rate_limits":[{"window":"5h","limit":1},{"window":"7d","limit":2,"used":-5}]}
            """);
        Assert.Contains("未设置额度上限", value.Detail);
        Assert.Null(value.FiveHour);
        Assert.Null(value.Daily);
        Assert.Null(value.Weekly);
        Assert.Null(value.Monthly);
    }

    [Fact]
    public void InvalidKeyIsNotReportedAsSuccessful()
    {
        var value = Parse("""{"mode":"unrestricted","isValid":false}""");
        Assert.Equal(ProviderDataState.ReadFailed, value.DataState);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"isValid\":\"true\"}")]
    [InlineData("{\"isValid\":true}")]
    [InlineData("{\"isValid\":true,\"mode\":\"unsupported\"}")]
    public void RejectsIncompatibleResponses(string json) => Assert.Throws<JsonException>(() => Parse(json));
}
