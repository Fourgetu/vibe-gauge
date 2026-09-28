using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class NetworkMonitor
{
    private readonly Dictionary<string, (long Rx, long Tx, long Tick)> previous = [];
    public IReadOnlyList<NetworkAdapterInfo> Scan()
    {
        var result = new List<NetworkAdapterInfo>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            try
            {
                var addresses = adapter.GetIPProperties();
                var stats = adapter.GetIPStatistics();
                var now = Environment.TickCount64;
                var old = previous.GetValueOrDefault(adapter.Id, (Rx: stats.BytesReceived, Tx: stats.BytesSent, Tick: now));
                var seconds = (now - old.Tick) / 1000d;
                previous[adapter.Id] = (stats.BytesReceived, stats.BytesSent, now);
                result.Add(new(adapter.Name, adapter.NetworkInterfaceType.ToString(),
                    string.Join(" · ", addresses.UnicastAddresses.Select(x => x.Address.ToString())),
                    string.Join(", ", addresses.GatewayAddresses.Select(x => x.Address.ToString())),
                    string.Join(", ", addresses.DnsAddresses.Select(x => x.ToString())),
                    seconds <= 0 ? 0 : Math.Max(0, stats.BytesReceived - old.Rx) / seconds,
                    seconds <= 0 ? 0 : Math.Max(0, stats.BytesSent - old.Tx) / seconds));
            }
            catch (NetworkInformationException) { }
        }
        return result;
    }

    public static async Task<string> ProbeEgressAsync(bool ipv6 = false)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = !ipv6, ConnectTimeout = TimeSpan.FromSeconds(5) };
        if (ipv6) handler.ConnectCallback = async (context, cancellation) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetworkV6, cancellation);
            foreach (var address in addresses.Where(x => !x.IsIPv4MappedToIPv6))
            {
                var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellation); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); }
            }
            throw new HttpRequestException("没有可直连 IPv6 地址");
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 64 * 1024 };
        try
        {
            var text = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace");
            var fields = text.Split('\n').Where(x => x.Contains('=')).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
            if (!fields.TryGetValue("ip", out var ip) || !IPAddress.TryParse(ip, out var address)) return "响应没有有效出口地址";
            if (ipv6 && (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6)) return "未确认 IPv6 直连";
            return ipv6 ? $"IPv6 可直连：{address}（并不单独证明 DNS 泄漏）" : $"系统代理路径出口：{address} · {fields.GetValueOrDefault("loc", "未知地区")}";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or SocketException)
        { return ipv6 ? "未检测到可直连 IPv6" : "出口检测失败，请检查网络或系统代理"; }
    }
}
