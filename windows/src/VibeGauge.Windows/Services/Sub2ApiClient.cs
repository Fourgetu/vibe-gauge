using System.Net;
using System.Net.Http;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class Sub2ApiClient(HttpClient client)
{
    public static Sub2ApiClient Shared { get; } = new(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 512 * 1024 });

    public async Task<PlatformStatus> ProbeAsync(string endpoint, string key, string title, CancellationToken token = default)
    {
        var uri = Sub2ApiEndpoint.UsageUri(endpoint);
        UsageKeyVault.ValidateKey(key);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new("Bearer", key);
            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "密钥无效或已过期（HTTP 401）",
                    HttpStatusCode.Forbidden => "密钥无权查询此站点（HTTP 403）",
                    HttpStatusCode.NotFound => "站点没有 /v1/usage 接口，请检查地址和 sub2api 版本（HTTP 404）",
                    HttpStatusCode.TooManyRequests => "站点查询过于频繁，请稍后重试（HTTP 429）",
                    >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => "站点要求跳转；为保护密钥，未跟随跳转，请填写最终站点地址",
                    _ => $"站点查询失败（HTTP {(int)response.StatusCode}）"
                };
                return Sub2ApiParser.Failure(title, message);
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
            return Sub2ApiParser.Parse(title, doc.RootElement, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) { return Sub2ApiParser.Failure(title, token.IsCancellationRequested ? "查询已取消" : "站点响应超时，请稍后重试"); }
        catch (HttpRequestException) { return Sub2ApiParser.Failure(title, "连接失败，请检查地址、网络或 HTTPS 证书"); }
        catch (JsonException) { return Sub2ApiParser.Failure(title, "返回格式不是支持的 sub2api 用量数据"); }
    }
}
