using System.Net;

namespace VibeGauge.Proxy.Security;

public sealed class RequestValidator(ProxyOptions options)
{
    private static readonly string[] BrowserHeaders = ["Origin", "Sec-Fetch-Site", "Sec-Fetch-Dest"];

    public string? ValidateClient(HttpRequest request)
    {
        var host = request.Host.Host;
        var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        if (!local || request.Host.Port != options.Port) return "proxy requires a loopback client Host";
        if (BrowserHeaders.Any(request.Headers.ContainsKey)) return "browser-originated requests are not allowed";
        return null;
    }
}
