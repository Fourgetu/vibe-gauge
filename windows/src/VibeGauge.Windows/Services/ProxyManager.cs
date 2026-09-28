using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class ProxyManager : IDisposable
{
    private readonly AppPaths paths;
    private readonly HttpClient client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMilliseconds(650) };
    private Process? ownedProcess;
    private string controlToken = "";

    public ProxyManager(AppPaths? paths = null)
    {
        this.paths = paths ?? new AppPaths();
        Port = ProxyConfiguration.Load(this.paths.LocalDataRoot).Port;
    }
    public int Port { get; private set; }
    public string Prefix => $"http://127.0.0.1:{Port}/";
    public async Task ConfigureAsync(int port, string? route)
    {
        if (port is < 1024 or > 65535 || !ProxyConfiguration.ValidRoute(route))
            throw new ArgumentException("端口必须为 1024–65535，上游必须是 HTTP 代理地址、direct 或留空。");
        var status = await StopOwnedAsync();
        if (status.State == ProxyRunState.Running) throw new InvalidOperationException("请先退出另一个正在运行的 VibeGauge，再修改代理设置。");
        new ProxyConfiguration(port, route).Save(paths.LocalDataRoot);
        Port = port;
    }

    public async Task<ProxyRuntimeStatus> GetStatusAsync()
    {
        try
        {
            using var response = await client.GetAsync(Prefix + "_vibegauge/health");
            if (!response.IsSuccessStatusCode) return await PortOccupiedStatusAsync();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            if (GetString(root, "product") != "VibeGauge.Proxy") return await PortOccupiedStatusAsync();
            return new(
                ProxyRunState.Running,
                Port,
                (int)GetLong(root, "requests"),
                (int)GetLong(root, "parsed"),
                (int)GetLong(root, "errors"),
                GetLong(root, "uptimeSeconds"),
                GetString(root, "upstream_error") is { Length: > 0 } routeError ? routeError : "运行中");
        }
        catch { return await PortOccupiedStatusAsync(); }
    }

    public async Task<ProxyRuntimeStatus> StartAsync()
    {
        var current = await GetStatusAsync();
        if (current.State == ProxyRunState.Running) return current;
        if (current.State == ProxyRunState.PortOccupied) return current;
        var executable = FindProxyExecutable();
        if (executable is null)
            return new(ProxyRunState.Error, Port, 0, 0, 0, 0, "找不到 VibeGauge.Proxy.exe");

        controlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add($"--port={Port}");
        start.ArgumentList.Add($"--data-dir={paths.LocalDataRoot}");
        start.ArgumentList.Add($"--control-token={controlToken}");
        try { ownedProcess = Process.Start(start); }
        catch (Exception error)
        {
            return new(ProxyRunState.Error, Port, 0, 0, 0, 0, "启动失败：" + error.GetType().Name);
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(100);
            if (ownedProcess is { HasExited: true })
                return new(ProxyRunState.Error, Port, 0, 0, 0, 0, $"Proxy 已退出 ({ownedProcess.ExitCode})");
            var status = await GetStatusAsync();
            if (status.State is ProxyRunState.Running or ProxyRunState.PortOccupied) return status;
        }
        return new(ProxyRunState.Error, Port, 0, 0, 0, 0, "启动超时");
    }

    public async Task<ProxyRuntimeStatus> StopOwnedAsync()
    {
        if (ownedProcess is null || ownedProcess.HasExited)
        {
            var current = await GetStatusAsync();
            return current.State == ProxyRunState.Running
                ? current with { Detail = "当前 Proxy 不是由本次 VibeGauge 启动，未执行停止" }
                : current;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Prefix + "_vibegauge/shutdown");
            request.Headers.TryAddWithoutValidation("X-VibeGauge-Control", controlToken);
            using var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await ownedProcess.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { }
            }
        }
        catch { }
        if (!ownedProcess.HasExited)
        {
            try
            {
                ownedProcess.Kill(entireProcessTree: true);
                await ownedProcess.WaitForExitAsync();
            }
            catch { }
        }
        ownedProcess.Dispose();
        ownedProcess = null;
        controlToken = "";
        return await GetStatusAsync();
    }

    private async Task<ProxyRuntimeStatus> PortOccupiedStatusAsync()
    {
        using var tcp = new TcpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await tcp.ConnectAsync("127.0.0.1", Port, timeout.Token);
            return new(ProxyRunState.PortOccupied, Port, 0, 0, 0, 0, $"端口 {Port} 已被其他程序占用");
        }
        catch { return ProxyRuntimeStatus.Stopped with { Port = Port }; }
    }

    public static string? FindProxyExecutable()
    {
        var current = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(current))
        {
            var beside = Path.Combine(Path.GetDirectoryName(current)!, "VibeGauge.Proxy.exe");
            if (File.Exists(beside)) return beside;
        }
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(parent.FullName, "VibeGauge.Proxy", "bin", configuration, "net10.0", "win-x64", "VibeGauge.Proxy.exe");
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private static string GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long GetLong(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;

    public void Dispose()
    {
        client.Dispose();
        ownedProcess?.Dispose();
    }
}
