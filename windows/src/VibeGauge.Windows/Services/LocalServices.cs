using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class LocalServices(AppPaths paths)
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMilliseconds(700), MaxResponseContentBufferSize = 512 * 1024 };
    private DateTimeOffset captured;
    private IReadOnlyList<PlatformStatus> cached = [];

    public async Task<IReadOnlyList<PlatformStatus>> ScanAsync(IReadOnlyList<LocalRuntime>? runtimes = null)
    {
        if (DateTimeOffset.Now - captured < TimeSpan.FromSeconds(30)) return cached;
        var rows = new List<PlatformStatus>();
        foreach (var (name, port) in new[] { ("LM Studio", 1234), ("llama.cpp", 8080) }.Concat((runtimes ?? []).Select(x => (x.Name, x.Port))).Distinct())
        {
            var label = name == "llama.cpp" && port != 8080 ? $"llama.cpp :{port}" : name;
            try
            {
                if (name == "LM Studio")
                {
                    IReadOnlyList<string>? loaded = null;
                    foreach (var version in new[] { "v1", "v0" })
                        try
                        {
                            using var lm = await GetJson($"http://127.0.0.1:{port}/api/{version}/models");
                            loaded = LocalModelParser.LmStudio(lm.RootElement, version == "v0");
                            if (loaded is not null) break;
                        }
                        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
                    if (loaded is not null)
                    {
                        rows.Add(new(name, "本地", true, 0, ProviderDataState.Available,
                            loaded.Count > 0 ? string.Join(", ", loaded) : "服务在线 · 无已加载模型", ModelCount: loaded.Count));
                        continue;
                    }
                }
                using var document = await GetJson($"http://127.0.0.1:{port}/v1/models");
                if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
                var ids = data.EnumerateArray().Where(x => x.TryGetProperty("id", out _))
                    .Select(x => x.GetProperty("id").GetString()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
                rows.Add(new(label, "本地", true, 0, ProviderDataState.Available,
                    name == "LM Studio" ? "服务在线 · 已加载模型数量未知" : ids.Length > 0 ? string.Join(", ", ids) : "服务在线，未报告模型",
                    ModelCount: name == "LM Studio" ? null : ids.Length));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (runtimes?.Any(x => x.Name == name && x.Port == port) == true)
                    rows.Add(new(label, "本地", true, 1, ProviderDataState.ReadFailed, $"进程运行中 · 端口 {port} 未响应（可能正在加载或未开启服务）", AlwaysShowDetail: true));
            }
        }
        var kimi = Path.Combine(paths.Home, ".kimi-code");
        if (Directory.Exists(kimi)) rows.Add(await ReadKimi(kimi));
        captured = DateTimeOffset.Now;
        return cached = rows;
    }

    private static async Task<JsonDocument> GetJson(string url, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<PlatformStatus> ReadKimi(string root)
    {
        var empty = new PlatformStatus("Kimi Code", "已安装", false, 0, ProviderDataState.NoQuota, "运行 kimi web 后可读取官方额度");
        var tokenPath = Path.Combine(root, "server.token");
        if (!File.Exists(tokenPath)) return empty;
        var ports = new HashSet<int> { 58627, 58628, 58629 };
        var instances = Path.Combine(root, "server", "instances");
        if (Directory.Exists(instances))
            foreach (var path in Directory.EnumerateFiles(instances).Take(16))
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("port", out var p) && p.TryGetInt32(out var port) && port is >= 1024 and <= 65535) ports.Add(port);
                    else if (doc.RootElement.TryGetProperty("url", out var u) && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) && uri.Port >= 1024) ports.Add(uri.Port);
                }
                catch (Exception e) when (e is IOException or JsonException) { }
        foreach (var port in ports)
        {
            try
            {
                if (!PortOwnedByCurrentUser(port)) continue;
                var url = $"http://127.0.0.1:{port}";
                using var health = await GetJson(url + "/api/v1/healthz");
                if (!health.RootElement.TryGetProperty("request_id", out _) || !PortOwnedByCurrentUser(port)) continue;
                var token = (await File.ReadAllTextAsync(tokenPath)).Trim();
                if (token.Length == 0 || token.Length > 4096) continue;
                using var data = await GetJson(url + "/api/v1/oauth/usage", token);
                return OfficialQuotaParser.Kimi(data.RootElement, DateTimeOffset.Now);
            }
            catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException or JsonException or UnauthorizedAccessException) { }
        }
        return empty;
    }

    private static bool PortOwnedByCurrentUser(int port)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size <= 4) return false;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) return false;
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var offset = 4 + i * 24;
                var local = Marshal.ReadInt32(buffer, offset + 4);
                var p = (Marshal.ReadByte(buffer, offset + 8) << 8) | Marshal.ReadByte(buffer, offset + 9);
                if (p != port || (local != 0 && local != 0x0100007f)) continue;
                var pid = Marshal.ReadInt32(buffer, offset + 20);
                using var process = new ManagementObject($"Win32_Process.Handle='{pid}'");
                process.Get();
                using var owner = process.InvokeMethod("GetOwnerSid", null, null);
                return owner?["Sid"] as string == WindowsIdentity.GetCurrent().User?.Value;
            }
        }
        catch (ManagementException) { }
        finally { Marshal.FreeHGlobal(buffer); }
        return false;
    }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
