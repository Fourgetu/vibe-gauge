using System.Net;

namespace VibeGauge.Core;

public sealed record NetworkHealthResult(string Verdict, string Message);

public static class NetworkHealth
{
    private static readonly HashSet<string> DomesticDns = ["223.5.5.5", "223.6.6.6", "119.29.29.29", "114.114.114.114", "180.76.76.76", "1.12.12.12", "120.53.53.53"];

    public static NetworkHealthResult Dns(IEnumerable<string> servers)
    {
        var values = servers.Select(x => x.Trim().Trim('[', ']')).Where(x => x.Length > 0).Distinct().ToArray();
        if (values.Any(DomesticDns.Contains))
            return new("warning", "发现国内公共 DNS，可能未由代理接管；不等于已发生泄漏。");
        if (values.Length > 0 && values.All(x => IPAddress.TryParse(x, out var ip) && ip.GetAddressBytes() is [198, 18 or 19, _, _]))
            return new("managed", "DNS 指向 Fake-IP 网段，符合代理接管特征；仍需核验代理 DNS 策略。");
        return new("unknown", "无法仅凭本机 DNS 确认泄漏；本地或路由器 DNS 的上游策略未知。");
    }

    public static NetworkHealthResult Ipv6(bool receivedIpv6Response, bool ipv4Reachable, bool connectivityFailure, string ip = "")
    {
        if (receivedIpv6Response)
            return new("warning", "IPv6 可直连，可能绕过系统代理" + (ip.Length > 0 ? "：" + ip : "（响应无有效 trace）") + "。");
        if (ipv4Reachable && connectivityFailure)
            return new("unavailable", "本次未发现 IPv6 直连（IPv4 可达）；不代表所有目标都被阻断。");
        return new("unknown", ipv4Reachable ? "IPv6 检测失败，无法判定（例如 TLS 或响应错误）。" : "IPv6 结果未知：IPv4 对照也不可达，可能断网或目标受限。");
    }
}
