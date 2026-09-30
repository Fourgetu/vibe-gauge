using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class NetworkDiagnostics
{
    internal static readonly (string Provider, string Host)[] Targets =
        [("Claude", "api.anthropic.com"), ("ChatGPT / Codex", "chatgpt.com"), ("OpenAI API", "api.openai.com"), ("Cloudflare", "www.cloudflare.com")];
    private readonly AppPaths paths;
    private readonly Func<Task<NetworkDiagnosticsReport>> collect;
    private readonly object sync = new();
    private readonly Dictionary<string, EgressInfo> baseline;
    private readonly string baselineFile;
    private NetworkDiagnosticsReport? cached;
    private Task<NetworkDiagnosticsReport>? pending;
    private DateTimeOffset lastAttempt;
    public NetworkDiagnostics(AppPaths paths, Func<Task<NetworkDiagnosticsReport>>? collect = null)
    {
        this.paths = paths;
        this.collect = collect ?? CollectAsync;
        baselineFile = Path.Combine(paths.LocalDataRoot, "egress-baseline.json");
        try { baseline = JsonSerializer.Deserialize<Dictionary<string, EgressInfo>>(File.ReadAllText(baselineFile)) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { baseline = []; }
    }
    public NetworkDiagnosticsReport? Scan()
    {
        lock (sync)
        {
            if (FeaturePreferences.Load(paths).NetworkDiagnostics && (pending is null || pending.IsCompleted) &&
                DateTimeOffset.Now - lastAttempt >= TimeSpan.FromMinutes(5)) _ = RefreshAsync();
            return cached;
        }
    }
    public Task<NetworkDiagnosticsReport> RefreshAsync()
    {
        lock (sync)
        {
            if (pending is { IsCompleted: false }) return pending;
            lastAttempt = DateTimeOffset.Now;
            return pending = RefreshCoreAsync();
        }
    }
    private async Task<NetworkDiagnosticsReport> RefreshCoreAsync()
    {
        var report = await collect();
        lock (sync)
        {
            var changes = new List<string>();
            foreach (var next in report.Exits.Where(x => x.Error.Length == 0 && IPAddress.TryParse(x.Ip, out _)))
            {
                if (baseline.TryGetValue(next.Host, out var old) && old is not null && (old.Ip != next.Ip || old.Region != next.Region))
                    changes.Add($"{next.Provider} 出口变化：{old.Ip} ({old.Region}) → {next.Ip} ({next.Region})");
                baseline[next.Host] = next;
            }
            try
            {
                Directory.CreateDirectory(paths.LocalDataRoot);
                File.WriteAllText(baselineFile + ".tmp", JsonSerializer.Serialize(baseline));
                File.Move(baselineFile + ".tmp", baselineFile, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { changes.Add("出口基准保存失败，重启后可能无法比较"); }
            return cached = report with { Changes = changes };
        }
    }
    private async Task<NetworkDiagnosticsReport> CollectAsync()
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(4) })
            { Timeout = TimeSpan.FromSeconds(7), MaxResponseContentBufferSize = 64 * 1024 };
        var probes = Targets.Select(async t =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var text = await client.GetStringAsync($"https://{t.Host}/cdn-cgi/trace");
                var fields = text.Split('\n').Where(x => x.Contains('=')).Select(x => x.Split('=', 2)).GroupBy(x => x[0]).ToDictionary(x => x.Key, x => x.First()[1].Trim());
                var ip = fields.GetValueOrDefault("ip", "");
                return IPAddress.TryParse(ip, out _) ? new EgressInfo(t.Provider, t.Host, ip, fields.GetValueOrDefault("loc", ""), fields.GetValueOrDefault("colo", ""), "", watch.ElapsedMilliseconds, DateTimeOffset.Now) :
                    new EgressInfo(t.Provider, t.Host, "", "", "", "未回报有效 trace（不等于无法使用 API）", watch.ElapsedMilliseconds, DateTimeOffset.Now);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new EgressInfo(t.Provider, t.Host, "", "", "", "检测失败 / 站点不支持 trace", watch.ElapsedMilliseconds, DateTimeOffset.Now); }
        }).ToArray();
        var proxyTask = new ClashControllerClient(paths).ReadAsync();
        var local = LocalAsync();
        var ipv6 = NetworkMonitor.ProbeEgressAsync(true);
        var dns = DnsAsync(Targets.Select(x => x.Host));
        var exits = (await Task.WhenAll(probes)).ToList();
        var proxy = await proxyTask;
        exits.Add(ClashControllerClient.GeminiExit(proxy));
        return new(DateTimeOffset.Now, exits, proxy.Rows, await local, [], await dns, await ipv6, proxy.Connections);
    }
    internal static IReadOnlyList<string> ParseClash(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return ["Clash 返回格式无效"];
        var rows = new List<string>();
        if (root.TryGetProperty("proxies", out var proxies) && proxies.ValueKind == JsonValueKind.Object)
            foreach (var p in proxies.EnumerateObject().Take(100))
                if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("now", out var selected) && selected.ValueKind == JsonValueKind.String)
                    rows.Add($"代理组 {p.Name} → {selected.GetString()}");
        rows.AddRange(ParseConnections(root).Select(x => $"{x.Host} · {(x.Chain.Length > 0 ? x.Chain : "未回报链路")}"));
        return rows;
    }
    internal static IReadOnlyList<ProxyConnection> ParseConnections(JsonElement root, string? sourceIp = null)
    {
        var rows = new List<ProxyConnection>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Array) return rows;
        foreach (var c in connections.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            var metadata = c.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? m : c;
            if (!string.IsNullOrEmpty(sourceIp) && (!metadata.TryGetProperty("sourceIP", out var source) || source.ValueKind != JsonValueKind.String ||
                !IPAddress.TryParse(source.GetString(), out var sourceAddress) || !IPAddress.TryParse(sourceIp, out var filter) || !sourceAddress.Equals(filter))) continue;
            var host = metadata.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()! :
                c.TryGetProperty("host", out h) && h.ValueKind == JsonValueKind.String ? h.GetString()! : "";
            host = host.Trim().Trim('.').ToLowerInvariant();
            bool Domain(string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
            var provider = Domain("anthropic.com") || Domain("claude.ai") ? "Claude" : Domain("api.openai.com") ? "OpenAI API" : Domain("chatgpt.com") || Domain("openai.com") ? "ChatGPT / Codex" :
                Domain("gemini.google.com") || Domain("googleapis.com") || Domain("generativelanguage.google.com") ? "Gemini" : "";
            if (provider.Length == 0) continue;
            var chain = c.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array
                ? string.Join(" → ", chains.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())) : "";
            rows.Add(new(provider, host, chain));
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
