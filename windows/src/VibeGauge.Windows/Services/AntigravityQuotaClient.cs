using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

internal sealed class AntigravityQuotaClient(AppPaths paths)
{
    internal async Task<AntigravityQuota> ReadAsync(CancellationToken token)
    {
        var exe = Path.Combine(paths.GeminiRoot, "bin", "agy.exe");
        if (!File.Exists(exe)) return new(Error: "未找到官方 agy CLI；桌面 Token 统计不受影响");
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(shell) && !shell.Any(c => char.IsWhiteSpace(c) || "&|()<>^%!\"".Contains(c)))
        {
            try { if (await ReadStatusAsync(exe, shell, token).ConfigureAwait(false) is { } result) return result; }
            catch (Exception e) when (!token.IsCancellationRequested && e is IOException or OperationCanceledException or
                JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
        }
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = paths.Home
        };
        foreach (var arg in new[] { "--print", "/usage", "--output-format", "json" }) start.ArgumentList.Add(arg);
        start.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        timeout.Token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("agy");
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            process.StandardInput.Close();
            var json = new StringBuilder();
            var chunk = new char[2048];
            int length;
            while ((length = await process.StandardOutput.ReadAsync(chunk.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (json.Length + length > 131072) throw new InvalidDataException("Oversized quota response");
                json.Append(chunk, 0, length);
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? AntigravityQuota.Parse(json.ToString(), DateTimeOffset.UtcNow) :
                new(Error: "agy 额度查询失败，请检查 CLI 登录与网络");
        }
        catch (OperationCanceledException) { return new(Error: "agy 额度查询超时或已取消"); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            timeout.Cancel();
            try { await stderr.ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task<AntigravityQuota?> ReadStatusAsync(string exe, string shell, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var nonce = Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream("vg-agy-" + nonce, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // agy emits plan_tier only in console mode. A per-process override leaves the user's settings alone.
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Home };
        start.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";
        start.ArgumentList.Add("--settings");
        // agy's Windows command runner cannot reliably round-trip a quoted executable/argument.
        // The encoded script is fixed except for our random pipe name; no user data is executable.
        var script = $$"""
            $ErrorActionPreference='Stop'
            $s=[Console]::In.ReadToEnd() | ConvertFrom-Json
            if (!$s.plan_tier -or !$s.quota) { return }
            $buckets=@{}
            foreach($id in 'gemini-5h','gemini-weekly','3p-5h','3p-weekly') {
                $b=$s.quota.$id
                if ($null -ne $b.remaining_fraction) {
                    $buckets[$id]=@{remaining_fraction=$b.remaining_fraction; reset_time=$b.reset_time; disabled=($b.disabled -eq $true)}
                }
            }
            if (!$buckets.Count) { return }
            $bytes=[Text.Encoding]::UTF8.GetBytes((@{plan_tier=$s.plan_tier;quota=$buckets} | ConvertTo-Json -Compress -Depth 4))
            $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','vg-agy-{{nonce}}',[IO.Pipes.PipeDirection]::Out)
            try { $pipe.Connect(700); $header=[BitConverter]::GetBytes($bytes.Length); $pipe.Write($header,0,4); $pipe.Write($bytes,0,$bytes.Length) }
            finally { $pipe.Dispose() }
            """;
        var command = shell + " -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        start.ArgumentList.Add(JsonSerializer.Serialize(new { statusLine = new { type = "command", command } }));
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("agy");
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            var header = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            var length = BitConverter.ToInt32(header);
            if (length is <= 0 or > 8192) return null;
            var bytes = new byte[length];
            await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(bytes);
            return AntigravityQuota.FromStatusLine(json.RootElement, DateTimeOffset.UtcNow);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
