using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class NetworkMonitor
{
    private readonly Dictionary<string, (long Rx, long Tx, long Tick)> previous = [];
    private readonly object sync = new();
    private long lastSample;
    private IReadOnlyList<NetworkAdapterInfo> cached = [];
    public IReadOnlyList<NetworkAdapterInfo> Scan()
    {
        lock (sync)
        {
            if (cached.Count > 0 && Environment.TickCount64 - lastSample < 700) return cached;
            cached = ScanCore(); lastSample = Environment.TickCount64;
            return cached;
        }
    }
    private IReadOnlyList<NetworkAdapterInfo> ScanCore()
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
        if (ipv6) return (await CheckIpv6Async()).Message;
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(5) };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 64 * 1024 };
        try
        {
            var text = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace");
            var fields = text.Split('\n').Where(x => x.Contains('=')).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
            if (!fields.TryGetValue("ip", out var ip) || !IPAddress.TryParse(ip, out var address)) return "响应没有有效出口地址";
            return $"系统代理路径出口：{address} · {fields.GetValueOrDefault("loc", "未知地区")}";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or SocketException)
        { return "出口检测失败，请检查网络或系统代理"; }
    }

    public static async Task<NetworkHealthResult> CheckIpv6Async()
    {
        var ipv6 = ProbeFamilyAsync(AddressFamily.InterNetworkV6);
        var ipv4 = ProbeFamilyAsync(AddressFamily.InterNetwork);
        var v6 = await ipv6; var v4 = await ipv4;
        return NetworkHealth.Ipv6(v6.Response, v4.Response, v6.ConnectivityFailure, v6.Ip);
    }

    private static async Task<(bool Response, bool ConnectivityFailure, string Ip)> ProbeFamilyAsync(AddressFamily family)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5) };
        handler.ConnectCallback = async (context, cancellation) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, family, cancellation);
            foreach (var address in addresses.Where(x => !x.IsIPv4MappedToIPv6))
            {
                var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellation); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); if (cancellation.IsCancellationRequested) throw; }
            }
            throw new SocketException((int)SocketError.NetworkUnreachable);
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await client.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // Any HTTP response over the forced IPv6 socket proves reachability, even 403 or a missing trace.
            var ip = "";
            try
            {
                await response.Content.LoadIntoBufferAsync(64 * 1024, timeout.Token);
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                var value = text.Split('\n').FirstOrDefault(x => x.StartsWith("ip=", StringComparison.Ordinal))?[3..].Trim();
                if (IPAddress.TryParse(value, out var address) && address.AddressFamily == family && !address.IsIPv4MappedToIPv6) ip = address.ToString();
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.IO.IOException) { }
            return (true, false, ip);
        }
        catch (OperationCanceledException) { return (false, true, ""); }
        catch (HttpRequestException error)
        { return (false, error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError, ""); }
        catch (SocketException) { return (false, true, ""); }
    }
}
