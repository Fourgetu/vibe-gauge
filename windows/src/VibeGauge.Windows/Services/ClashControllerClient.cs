using System.Net;
using System.Net.Http;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

internal sealed record ClashSnapshot(IReadOnlyList<string> Rows, IReadOnlyList<ProxyConnection> Connections, bool Available, string Error, string Scope = "");

internal sealed class ClashControllerClient(AppPaths paths, HttpMessageHandler? handler = null, Func<string?>? environmentSecret = null)
{
    internal async Task<ClashSnapshot> ReadAsync(string? endpoint = null, string? secretOverride = null, bool? allowLan = null, string? sourceIp = null)
    {
        Uri address;
        string? secret;
        string filter;
        try
        {
            var options = FeaturePreferences.Load(paths);
            address = ClashControllerCredentials.NormalizeEndpoint(endpoint ?? options.ClashController, allowLan ?? options.LanClashController);
            filter = ClashControllerCredentials.IsLocal(address) ? "" : ClashControllerCredentials.NormalizeSourceIp(sourceIp ?? options.ClashSourceIp);
            secret = secretOverride ?? new ClashControllerCredentials(paths, environmentSecret, allowLan ?? options.LanClashController).Resolve(address.AbsoluteUri);
            if (!string.IsNullOrEmpty(secret)) ClashControllerCredentials.ValidateSecret(secret);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return new([error.Message], [], false, error.Message); }
        using var client = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 1024 * 1024 };
        if (!string.IsNullOrEmpty(secret)) client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        var rows = new List<string>();
        var scope = ClashControllerCredentials.IsLocal(address) ? "" : filter.Length == 0 ? "软路由全设备连接表" : "软路由来源设备 " + filter;
        if (scope.Length > 0) rows.Add(scope + " · " + address.GetLeftPart(UriPartial.Authority));
        var connections = new List<ProxyConnection>();
        var available = false;
        var connectionError = "";
        foreach (var path in new[] { "proxies", "connections" })
        {
            string error;
            try
            {
                using var response = await client.GetAsync(new Uri(address, path));
                if (!response.IsSuccessStatusCode)
                    error = response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "控制器认证失败（401/403），请在设置 → 网络诊断中填写正确的控制器密钥。",
                        HttpStatusCode.NotFound => "控制器接口不存在（404），请检查控制器地址和端口。",
                        _ when (int)response.StatusCode is >= 300 and < 400 => "控制器返回重定向，已停止请求，请检查控制器地址。",
                        _ => "控制器请求失败，HTTP " + (int)response.StatusCode
                    };
                else
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var property = path == "connections" ? JsonValueKind.Array : JsonValueKind.Object;
                    if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(path, out var value) || value.ValueKind != property)
                        error = "控制器返回格式无效，请确认端口提供 Clash / Mihomo 控制接口。";
                    else
                    {
                        if (path == "connections")
                        {
                            available = true; connections.AddRange(NetworkDiagnostics.ParseConnections(doc.RootElement, filter));
                            rows.AddRange(connections.Select(x => $"{x.Host} · {(x.Chain.Length > 0 ? x.Chain : "未回报链路")}"));
                        }
                        else rows.AddRange(NetworkDiagnostics.ParseClash(doc.RootElement));
                        continue;
                    }
                }
            }
            catch (TaskCanceledException) { error = "控制器请求超时，请检查代理服务和防火墙。"; }
            catch (HttpRequestException) { error = "无法连接控制器，请检查地址、端口、服务监听和 HTTPS 证书。"; }
            catch (JsonException) { error = "控制器返回格式无效，请确认端口提供 Clash / Mihomo 控制接口。"; }
            rows.Add(path + "：" + error);
            if (path == "connections") connectionError = error;
        }
        return new(rows, connections, available, connectionError, scope);
    }
    internal static EgressInfo GeminiExit(ClashSnapshot proxy)
    {
        var gemini = proxy.Connections.Where(x => x.Provider == "Gemini").ToArray();
        return new("Gemini", "gemini.google.com / generativelanguage.googleapis.com" + (proxy.Scope.Length > 0 ? " · " + proxy.Scope : ""), "", "", "",
            !proxy.Available ? proxy.Error : gemini.Length == 0 ? "未发现 Gemini 活动连接" :
                !gemini.Any(x => x.Chain.Length > 0) ? "有活动连接，但连接表未提供出站链路" : "",
            CapturedAt: DateTimeOffset.Now, Chain: string.Join("\n", gemini.Where(x => x.Chain.Length > 0).Select(x => x.Host + " · " + x.Chain).Distinct()));
    }
}
