using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class NetworkDiagnostics(AppPaths paths)
{
    private NetworkDiagnosticsReport? cached;
    private Task<NetworkDiagnosticsReport>? pending;
    private DateTimeOffset lastAttempt;
    public NetworkDiagnosticsReport? Scan()
    {
        if (pending is { IsCompleted: true })
        {
            if (pending.IsCompletedSuccessfully) cached = pending.Result;
            pending = null;
        }
        if (FeaturePreferences.Load(paths).NetworkDiagnostics && pending is null && DateTimeOffset.Now - lastAttempt >= TimeSpan.FromMinutes(5))
        { lastAttempt = DateTimeOffset.Now; pending = RefreshAsync(); }
        return cached;
    }
    public async Task<NetworkDiagnosticsReport> RefreshAsync()
    {
        var targets = new[] { ("Claude", "api.anthropic.com"), ("Codex", "chatgpt.com"), ("Gemini", "generativelanguage.googleapis.com"), ("Cloudflare", "www.cloudflare.com") };
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(4) })
            { Timeout = TimeSpan.FromSeconds(7), MaxResponseContentBufferSize = 64 * 1024 };
        var probes = targets.Select(async t =>
        {
            try
            {
                var text = await client.GetStringAsync($"https://{t.Item2}/cdn-cgi/trace");
                var fields = text.Split('\n').Where(x => x.Contains('=')).Select(x => x.Split('=', 2)).GroupBy(x => x[0]).ToDictionary(x => x.Key, x => x.First()[1].Trim());
                var ip = fields.GetValueOrDefault("ip", "");
                return IPAddress.TryParse(ip, out _) ? new EgressInfo(t.Item1, t.Item2, ip, fields.GetValueOrDefault("loc", ""), fields.GetValueOrDefault("colo", ""), "") :
                    new EgressInfo(t.Item1, t.Item2, "", "", "", "未回报有效 trace（不等于无法使用 API）");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new EgressInfo(t.Item1, t.Item2, "", "", "", "检测失败 / 站点不支持 trace"); }
        }).ToArray();
        var proxy = ReadClashAsync();
        var local = LocalAsync();
        var ipv6 = NetworkMonitor.ProbeEgressAsync(true);
        var dns = DnsAsync(targets.Select(x => x.Item2));
        var exits = await Task.WhenAll(probes);
        var changes = exits.Where(x => x.Ip.Length > 0).Select(x => (Next: x, Old: cached?.Exits.FirstOrDefault(p => p.Provider == x.Provider)))
            .Where(x => x.Old is { Ip.Length: > 0 } && x.Old.Ip != x.Next.Ip)
            .Select(x => $"{x.Next.Provider} 出口变化：{x.Old!.Ip} → {x.Next.Ip}").ToArray();
        return cached = new(DateTimeOffset.Now, exits, await proxy, await local, changes, await dns, await ipv6);
    }
    private async Task<IReadOnlyList<string>> ReadClashAsync()
    {
        var endpoint = FeaturePreferences.Load(paths).ClashController;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip) || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0)
            return ["Clash 控制器必须为本机 http://127.0.0.1:端口；不会向远程地址发送密钥"];
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 1024 * 1024 };
        var secret = Environment.GetEnvironmentVariable("VIBEGAUGE_CLASH_SECRET");
        if (!string.IsNullOrWhiteSpace(secret)) client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        var rows = new List<string>();
        foreach (var path in new[] { "proxies", "connections" })
            try
            {
                using var doc = JsonDocument.Parse(await client.GetStringAsync(new Uri(uri, path)));
                rows.AddRange(ParseClash(doc.RootElement));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            { rows.Add(path + " 读取失败（只读；需要认证时设置 VIBEGAUGE_CLASH_SECRET）"); }
        return rows;
    }
    internal static IReadOnlyList<string> ParseClash(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return ["Clash 返回格式无效"];
        var rows = new List<string>();
        if (root.TryGetProperty("proxies", out var proxies) && proxies.ValueKind == JsonValueKind.Object)
            foreach (var p in proxies.EnumerateObject().Take(100))
                if (p.Value.TryGetProperty("now", out var selected) && selected.ValueKind == JsonValueKind.String)
                    rows.Add($"代理组 {p.Name} → {selected.GetString()}");
        if (root.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Array)
            foreach (var c in connections.EnumerateArray().Take(200))
            {
                if (!c.TryGetProperty("metadata", out var m)) continue;
                var host = m.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()! : "";
                if (!new[] { "anthropic.com", "openai.com", "chatgpt.com", "googleapis.com", "generativelanguage.google.com" }.Any(x => host == x || host.EndsWith("." + x, StringComparison.OrdinalIgnoreCase))) continue;
                var chain = c.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array
                    ? string.Join(" → ", chains.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())) : "未回报链路";
                rows.Add($"{host} · {chain}");
            }
        return rows;
    }
    private static async Task<string> DnsAsync(IEnumerable<string> hosts)
    {
        var rows = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var host in hosts)
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
                var fake = addresses.Any(a => a.GetAddressBytes() is [198, 18 or 19, _, _]);
                rows.Add($"{host}：{(fake ? "Fake-IP" : string.Join(", ", addresses.Take(3).Select(x => x.ToString())))}");
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException) { rows.Add(host + "：无法解析"); }
        return string.Join("\n", rows) + "\n本机解析结果不能单独证明 DNS 泄漏；需结合代理 DNS 策略核验。";
    }
    private static async Task<IReadOnlyList<string>> LocalAsync()
    {
        var rows = new List<string>();
        var wifi = await ReadProcessAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"), ["wlan", "show", "interfaces"]);
        foreach (var line in wifi.Split('\n').Select(x => x.Trim()).Where(x => x.Contains(':') &&
            new[] { "SSID", "Signal", "信号", "Channel", "频道", "Receive rate", "Transmit rate", "接收速率", "传输速率" }.Any(k => x.StartsWith(k, StringComparison.OrdinalIgnoreCase)) && !x.StartsWith("BSSID"))) rows.Add(line);
        var tailscale = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (File.Exists(tailscale))
            try
            {
                using var doc = JsonDocument.Parse(await ReadProcessAsync(tailscale, ["status", "--json"]));
                rows.Add("Tailscale：" + (doc.RootElement.TryGetProperty("BackendState", out var state) ? state.GetString() : "未知"));
                if (doc.RootElement.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                    rows.Add("Tailscale IP：" + string.Join(", ", ips.EnumerateArray().Select(x => x.GetString())));
            }
            catch (JsonException) { rows.Add("Tailscale 状态读取失败"); }
        return rows;
    }
    internal static async Task<string> ReadProcessAsync(string file, string[] args)
    {
        using var p = new Process { StartInfo = new(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            p.Start(); var output = ReadLimitedAsync(p.StandardOutput, timeout.Token); var error = ReadLimitedAsync(p.StandardError, timeout.Token);
            await p.WaitForExitAsync(timeout.Token); await error; return await output;
        }
        catch (Exception e) when (e is OperationCanceledException or System.ComponentModel.Win32Exception)
        { try { if (!p.HasExited) p.Kill(true); } catch { } return ""; }
    }
    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new System.Text.StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            if (result.Length < 128 * 1024) result.Append(buffer, 0, Math.Min(count, 128 * 1024 - result.Length));
        return result.ToString();
    }
}
