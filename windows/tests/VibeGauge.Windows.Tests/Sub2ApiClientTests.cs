using System.Net;
using System.Net.Http;
using System.Text;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class Sub2ApiClientTests
{
    [Fact]
    public async Task SendsOnlyTheEnteredKeyToTheExactNormalizedEndpoint()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://site.example/prefix/v1/usage", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-only-key", request.Headers.Authorization!.ToString());
            Assert.Null(request.Content);
            return Task.FromResult(Json("""{"isValid":true,"mode":"unrestricted","balance":2,"unit":"USD"}"""));
        }));
        var result = await new Sub2ApiClient(http).ProbeAsync("https://site.example/prefix/v1", "test-only-key", "Test");
        Assert.Equal(ProviderDataState.Available, result.DataState);
        Assert.Contains("2.00 USD", result.Detail);
    }

    [Theory]
    [InlineData(401, "密钥无效")]
    [InlineData(403, "无权")]
    [InlineData(404, "/v1/usage")]
    [InlineData(429, "频繁")]
    [InlineData(302, "未跟随跳转")]
    [InlineData(500, "HTTP 500")]
    public async Task ErrorMessagesNeverEchoResponseBodiesOrKeys(int code, string message)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = Json("sensitive-server-response test-only-key");
            response.StatusCode = (HttpStatusCode)code;
            response.Headers.Location = new Uri("https://other.example");
            return Task.FromResult(response);
        }));
        var result = await new Sub2ApiClient(http).ProbeAsync("https://site.example", "test-only-key", "Test");
        Assert.Equal(1, calls);
        Assert.Equal(ProviderDataState.ReadFailed, result.DataState);
        Assert.Contains(message, result.Detail);
        Assert.DoesNotContain("test-only-key", result.Detail);
        Assert.DoesNotContain("sensitive-server-response", result.Detail);
        Assert.Contains(message, PlatformRow.From(result).Detail);
    }

    [Theory]
    [InlineData("<html>login</html>")]
    [InlineData("{\"error\":\"test-only-key\"}")]
    [InlineData("[]")]
    public async Task RejectsMalformedOrUnsupportedJson(string json)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(json))));
        var result = await new Sub2ApiClient(http).ProbeAsync("https://site.example", "test-only-key", "Test");
        Assert.Equal(ProviderDataState.ReadFailed, result.DataState);
        Assert.Contains("返回格式", result.Detail);
    }

    [Fact]
    public async Task TimeoutAndNetworkFailureStayReadable()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("sensitive-address")));
        var result = await new Sub2ApiClient(http).ProbeAsync("https://site.example", "test-only-key", "Test");
        Assert.Contains("超时", result.Detail);
        using var offline = new HttpClient(new Handler((_, _) => throw new HttpRequestException("sensitive-address")));
        result = await new Sub2ApiClient(offline).ProbeAsync("https://site.example", "test-only-key", "Test");
        Assert.Contains("连接失败", result.Detail);
        Assert.DoesNotContain("sensitive-address", result.Detail);
    }

    [Fact]
    public async Task UnsafeAddressOrKeyNeverCausesANetworkRequest()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        var client = new Sub2ApiClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ProbeAsync("http://site.example", "test", "Test"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ProbeAsync("https://site.example", "test\r\nInjected: value", "Test"));
    }

    [Fact]
    public void RemoteCardsKeepDetailAlongsideMetersAndUseNoFakeSessions()
    {
        var status = new PlatformStatus("Custom site", "sub2api", false, 0, ProviderDataState.Available, "今日 1.00 USD",
            Daily: new(50, null, DateTimeOffset.Now, TimeSpan.FromDays(1)), AlwaysShowDetail: true);
        var row = PlatformRow.From(status);
        Assert.True(row.ShowDetail);
        Assert.True(row.IsWide);
        Assert.True(row.HasQuota);
        Assert.Equal("D", Assert.Single(row.Quotas).Label);
        Assert.Equal("已同步", row.SessionText);
        Assert.Equal(status.Detail, row.Detail);
    }

    [Fact]
    public void CustomIdentityDoesNotChangeOfficialIdentity()
    {
        var official = new UsageKeyIdentity("api.deepseek.com", "12345678");
        var custom = new UsageKeyIdentity("https://site.example/prefix", "12345678", "My site");
        Assert.False(official.IsCustom);
        Assert.Equal("api.deepseek.com", official.DisplayName);
        Assert.True(custom.IsCustom);
        Assert.Equal("My site", custom.DisplayName);
        Assert.NotEqual(official.Host, custom.Host);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
