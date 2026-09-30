using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class OfficialSources(AppPaths paths)
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 512 * 1024 };
    private Task<IReadOnlyList<PlatformStatus>>? pending;
    private IReadOnlyList<PlatformStatus> cached = [];
    private DateTimeOffset refreshed;
    private bool force;
    public void Invalidate() { force = true; cached = []; }

    public IReadOnlyList<PlatformStatus> Scan()
    {
        if (pending is { IsCompleted: true })
        {
            if (pending.IsCompletedSuccessfully) cached = pending.Result;
            pending = null;
            refreshed = DateTimeOffset.Now;
        }
        if (pending is null && (force || DateTimeOffset.Now - refreshed >= TimeSpan.FromMinutes(5)))
        { force = false; pending = Task.Run(Refresh); }
        return cached;
    }

    private async Task<IReadOnlyList<PlatformStatus>> Refresh()
    {
        if (paths.Home != new AppPaths().Home) return [];
        var tasks = new List<Task<PlatformStatus>>();
        // Isolated diagnostic fixtures never touch the real user's Credential Manager.
        if (paths.Home == new AppPaths().Home)
            try { tasks.AddRange(UsageKeyVault.List().Select(ProbeKey)); } catch (System.ComponentModel.Win32Exception) { }
        foreach (var (name, title, arguments) in new[]
        {
            ("arkcli", "火山方舟 Coding Plan", new[] { "usage", "plan", "--product", "coding-plan", "--format", "json" }),
            ("bl", "阿里云百炼 Coding Plan", new[] { "usage", "coding-plan", "--output", "json" })
        })
        {
            var executable = FindExecutable(paths, name);
            if (executable is not null) tasks.Add(ProbeCli(executable, title, arguments));
        }
        return await Task.WhenAll(tasks);
    }

    private static async Task<PlatformStatus> ProbeKey(UsageKeyIdentity identity)
    {
        if (identity.IsCustom)
        {
            var customTitle = identity.DisplayName + " · " + identity.Fingerprint;
            try { return await Sub2ApiClient.Shared.ProbeAsync(identity.Host, UsageKeyVault.Read(identity), customTitle); }
            catch (System.ComponentModel.Win32Exception) { return Sub2ApiParser.Failure(customTitle, "无法读取 Windows 凭据，请重新登记密钥"); }
            catch (ArgumentException) { return Sub2ApiParser.Failure(customTitle, "站点地址或密钥格式无效，请重新登记"); }
        }
        var title = identity.Host switch
        {
            "open.bigmodel.cn" => "GLM Coding",
            "api.z.ai" => "Z.ai Coding",
            "api.minimaxi.com" => "MiniMax",
            "api.deepseek.com" => "DeepSeek",
            "openrouter.ai" => "OpenRouter",
            _ => "Moonshot"
        };
        title += " · " + identity.Fingerprint;
        var missing = new PlatformStatus(title, "已登记", false, 0, ProviderDataState.ReadFailed, "官方额度查询失败");
        try
        {
            var key = UsageKeyVault.Read(identity);
            var path = identity.Host switch
            {
                "open.bigmodel.cn" or "api.z.ai" => "/api/monitor/usage/quota/limit",
                "api.minimaxi.com" => "/v1/token_plan/remains",
                "api.deepseek.com" => "/user/balance",
                "openrouter.ai" => "/api/v1/auth/key",
                _ => "/v1/users/me/balance"
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + identity.Host + path);
            request.Headers.Authorization = new("Bearer", key);
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return missing with { Detail = $"官方接口 HTTP {(int)response.StatusCode}，请检查 key 与套餐" };
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            return OfficialQuotaParser.Provider(identity.Host, title, doc.RootElement, DateTimeOffset.Now);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or System.ComponentModel.Win32Exception)
        { return missing; }
    }

    private async Task<PlatformStatus> ProbeCli(string executable, string title, string[] arguments)
    {
        var empty = new PlatformStatus(title, "官方 CLI", false, 0, ProviderDataState.ReadFailed, "请先在终端登录官方 CLI");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = paths.Home
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var output = ReadLimited(process.StandardOutput, timeout.Token);
            var error = ReadLimited(process.StandardError, timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            var text = await output;
            _ = await error;
            if (process.ExitCode != 0) return empty;
            using var doc = JsonDocument.Parse(text);
            return OfficialQuotaParser.Cli(title, doc.RootElement, DateTimeOffset.Now);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or JsonException) { return empty; }
    }
    private static async Task<string> ReadLimited(StreamReader reader, CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
        var chunk = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(chunk.AsMemory(), token)) > 0)
            if (text.Length < 512 * 1024) text.Append(chunk, 0, Math.Min(count, 512 * 1024 - text.Length));
        return text.ToString();
    }
    public static string? FindExecutable(AppPaths paths, string name, IEnumerable<string>? searchDirectories = null)
    {
        var directories = searchDirectories ?? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat([Path.Combine(paths.Home, ".local", "bin"), Path.Combine(paths.Home, ".volta", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")]);
        // Only native executables are launched; batch wrappers remain an explicit user terminal action.
        foreach (var directory in directories.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var root = directory.Trim('"');
            var native = Path.Combine(root, name + ".exe");
            if (File.Exists(native)) return native;
            if (name != "arkcli") continue;
            var package = Path.Combine(root, "node_modules", "@volcengine", "ark-cli");
            JsonDocument? manifest;
            try { manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "package.json"))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { continue; }
            using var manifestLifetime = manifest;
            if (manifest?.RootElement.ValueKind != JsonValueKind.Object ||
                (!manifest.RootElement.TryGetProperty("name", out var packageName) || packageName.ValueKind != JsonValueKind.String || packageName.GetString() != "@volcengine/ark-cli") ||
                !manifest.RootElement.TryGetProperty("bin", out var bin) || bin.ValueKind != JsonValueKind.Object ||
                (!bin.TryGetProperty("arkcli", out var entry) || entry.ValueKind != JsonValueKind.String || entry.GetString() != "scripts/run.js")) continue;
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
            if (arch is not (System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64)) continue;
            native = Path.Combine(package, "bin", arch == System.Runtime.InteropServices.Architecture.Arm64
                ? "arkcli-windows-arm64.exe" : "arkcli-windows-amd64.exe");
            if (File.Exists(native)) return native;
        }
        return null;
    }
}
