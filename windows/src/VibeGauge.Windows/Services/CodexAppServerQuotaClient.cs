using System.Diagnostics;
using System.IO;
using System.Text;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

internal sealed class CodexAppServerQuotaClient(AppPaths paths) : ICodexQuotaClient
{
    private string? executable;

    public async Task<CodexQuotaResult> ReadAsync(CodexQuotaRequest account, CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        Process? process = null;
        Task? stderr = null;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (!SameLogin(account)) return new(Error: "登录信息已变化，等待重新查询当前账号");
            if (executable is null || !File.Exists(executable)) executable = FindExecutable(paths);
            if (executable is null) return new(Error: "未找到 Codex CLI，请安装或启动 Codex 客户端后刷新");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Home,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
            };
            // Use the same credential file the scanner observed; never send credentials in arguments.
            start.Environment["CODEX_HOME"] = paths.CodexRoot;
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            start.ArgumentList.Add("app-server");
            process = Process.Start(start) ?? throw new IOException("Codex could not start");
            stderr = Drain(process.StandardError, timeout.Token);
            var result = await CodexAppServerProtocol.ReadAsync(process.StandardOutput, process.StandardInput,
                account, DateTimeOffset.UtcNow, timeout.Token).ConfigureAwait(false);
            return SameLogin(account) ? result : new(Error: "查询期间登录信息发生变化，等待重新查询");
        }
        catch (OperationCanceledException) { return new(Error: cancellationToken.IsCancellationRequested ? "额度查询已取消" : "官方额度查询超时，请检查网络后刷新"); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { return new(Error: "无法读取 Codex 官方额度，请检查客户端登录与网络"); }
        finally
        {
            timeout.Cancel();
            if (process is not null)
            {
                try
                {
                    process.StandardInput.Close();
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
                finally { process.Dispose(); }
            }
            if (stderr is not null) await stderr.ConfigureAwait(false);
        }
    }

    private bool SameLogin(CodexQuotaRequest account)
    {
        var file = new FileInfo(Path.Combine(paths.CodexRoot, "auth.json"));
        return file.Exists && file.LastWriteTimeUtc == account.AuthUpdatedAt.UtcDateTime;
    }

    private static async Task Drain(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) > 0) { } }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    internal static string? FindExecutable(AppPaths paths)
    {
        var local = Path.Combine(paths.Home, "AppData", "Local", "OpenAI", "Codex", "bin");
        try
        {
            if (Directory.Exists(local))
            {
                var desktop = Directory.EnumerateDirectories(local).Select(dir => new FileInfo(Path.Combine(dir, "codex.exe")))
                    .Where(file => file.Exists).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
                if (desktop is not null) return desktop.FullName;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var npm = Path.Combine(paths.Home, "AppData", "Roaming", "npm", "node_modules", "@openai", "codex");
        foreach (var (package, target) in new[] { ("codex-win32-x64", "x86_64-pc-windows-msvc"), ("codex-win32-arm64", "aarch64-pc-windows-msvc") })
        foreach (var vendor in new[] { Path.Combine(npm, "node_modules", "@openai", package, "vendor"), Path.Combine(npm, "vendor") })
        foreach (var bin in new[] { "bin", "codex" })
        {
            var candidate = Path.Combine(vendor, target, bin, "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return OfficialSources.FindExecutable(paths, "codex");
    }
}
