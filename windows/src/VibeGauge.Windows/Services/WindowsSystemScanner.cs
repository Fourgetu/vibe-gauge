using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class WindowsSystemScanner
{
    private readonly object sync = new();
    private DateTimeOffset npxMeasuredAt;
    private double npxCacheMb;
    private ulong previousIdle;
    private ulong previousKernel;
    private ulong previousUser;

    private static readonly HashSet<string> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "node.exe", "python", "python.exe", "python3", "python3.exe", "bun", "bun.exe",
        "deno", "deno.exe", "uv", "uv.exe", "uvx", "uvx.exe", "npx", "npx.cmd"
    };

    private static readonly (string Key, string Label)[] McpServices =
    [
        ("apple-docs", "Apple Docs 接口服务"),
        ("chrome-devtools", "Chrome DevTools 自动化插件"),
        ("notebooklm", "NotebookLM 交互插件"),
        ("xcodebuildmcp", "Xcode 构建工具插件"),
        ("mcpvault", "Obsidian Vault 插件"),
        ("magicuidesign", "Magic UI 设计工具"),
        ("meigen", "Meigen 图像服务"),
        ("context7", "Context7 检索服务")
    ];

    public (SystemMetrics Metrics, ProcessReport Processes) Scan()
    {
        lock (sync)
        {
            var processes = ReadProcesses();
            var listeners = NativeMethods.ListeningProcessIds();
            var servicePids = ReadServiceProcessIds();
            var protectedTokens = ReadStartupTokens().Concat(ReadScheduledTaskTokens()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var report = BuildProcessReport(processes, listeners, servicePids, protectedTokens);
            return (ReadMetrics(), report);
        }
    }

    public CleanupResult Clean(IReadOnlyList<OrphanProcess> requested)
    {
        var fresh = Scan();
        var candidates = fresh.Processes.Orphans.ToDictionary(x => x.ProcessId);
        var killed = 0;
        var skipped = 0;
        var freed = 0d;
        var messages = new List<string>();
        foreach (var target in requested)
        {
            if (!candidates.TryGetValue(target.ProcessId, out var current) || current.CommandFingerprint != target.CommandFingerprint || current.StartedAt != target.StartedAt)
            {
                skipped++;
                messages.Add($"PID {target.ProcessId} 已变化，跳过");
                continue;
            }
            try
            {
                using var process = Process.GetProcessById(target.ProcessId);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
                killed++;
                freed += target.MemoryMb;
            }
            catch (Exception error)
            {
                skipped++;
                messages.Add($"PID {target.ProcessId}: {error.Message}");
            }
        }
        return new(killed, skipped, freed, messages);
    }

    private SystemMetrics ReadMetrics()
    {
        var memory = new NativeMethods.MemoryStatusEx();
        NativeMethods.GlobalMemoryStatusEx(memory);
        const double gb = 1024d * 1024 * 1024;
        var total = memory.TotalPhysical / gb;
        var available = memory.AvailablePhysical / gb;
        var pageUsed = ReadPageFileUsedGb();
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var drive = new DriveInfo(root);
        return new(
            total <= 0 ? 0 : (int)Math.Round(available / total * 100),
            Math.Max(0, total - available), total, pageUsed, ReadCpuPercent(),
            drive.AvailableFreeSpace / gb, drive.TotalSize / gb, ReadNpxCacheMb());
    }

    private static double ReadPageFileUsedGb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT CurrentUsage FROM Win32_PageFileUsage");
            using var results = searcher.Get();
            var megabytes = 0d;
            foreach (ManagementObject item in results)
            {
                using (item) megabytes += Convert.ToDouble(item["CurrentUsage"] ?? 0);
            }
            return megabytes / 1024d;
        }
        catch { return 0; }
    }

    private double ReadCpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var idleNow = idle.ToUInt64();
        var kernelNow = kernel.ToUInt64();
        var userNow = user.ToUInt64();
        var totalDelta = kernelNow - previousKernel + userNow - previousUser;
        var idleDelta = idleNow - previousIdle;
        previousIdle = idleNow;
        previousKernel = kernelNow;
        previousUser = userNow;
        return totalDelta == 0 ? 0 : Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private double ReadNpxCacheMb()
    {
        var now = DateTimeOffset.Now;
        if (now - npxMeasuredAt < TimeSpan.FromMinutes(5)) return npxCacheMb;
        npxMeasuredAt = now;
        var configured = Environment.GetEnvironmentVariable("NPM_CONFIG_CACHE");
        var candidates = new[]
        {
            configured,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm-cache")
        };
        var root = candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x));
        if (root is null) return npxCacheMb = 0;
        var npx = Path.Combine(root, "_npx");
        if (!Directory.Exists(npx)) return npxCacheMb = 0;
        try { npxCacheMb = Directory.EnumerateFiles(npx, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length) / 1024d / 1024; }
        catch { npxCacheMb = 0; }
        return npxCacheMb;
    }

    private static Dictionary<int, ProcessSnapshot> ReadProcesses()
    {
        var result = new Dictionary<int, ProcessSnapshot>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId,ParentProcessId,Name,CommandLine,ExecutablePath,WorkingSetSize,CreationDate,SessionId FROM Win32_Process");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var pid = Convert.ToInt32(item["ProcessId"] ?? 0);
                    if (pid <= 0) continue;
                    DateTimeOffset? started = null;
                    var creation = item["CreationDate"] as string;
                    if (!string.IsNullOrEmpty(creation))
                    {
                        try { started = new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(creation)); } catch { }
                    }
                    result[pid] = new(
                        pid,
                        Convert.ToInt32(item["ParentProcessId"] ?? 0),
                        item["Name"]?.ToString() ?? "",
                        item["CommandLine"]?.ToString() ?? "",
                        item["ExecutablePath"]?.ToString() ?? "",
                        Convert.ToDouble(item["WorkingSetSize"] ?? 0) / 1024d / 1024,
                        started,
                        Convert.ToInt32(item["SessionId"] ?? -1));
                }
            }
        }
        catch (ManagementException) { }
        return result;
    }

    private static HashSet<int> ReadServiceProcessIds()
    {
        var result = new HashSet<int>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service WHERE ProcessId <> 0");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
                using (item) result.Add(Convert.ToInt32(item["ProcessId"] ?? 0));
        }
        catch { }
        return result;
    }

    private static IEnumerable<string> ReadStartupTokens()
    {
        foreach (var pair in new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run")
        })
        {
            RegistryKey? key = null;
            try
            {
                key = pair.Item1.OpenSubKey(pair.Item2);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name)?.ToString() is { Length: > 5 } value) yield return value;
            }
            finally { key?.Dispose(); }
        }
    }

    private static IEnumerable<string> ReadScheduledTaskTokens()
    {
        var values = new List<string>();
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return values;
            service = Activator.CreateInstance(type)!;
            ((dynamic)service).Connect();
            ReadTaskFolder(((dynamic)service).GetFolder("\\"), values);
        }
        catch { }
        finally { ReleaseCom(service); }
        return values;
    }

    private static void ReadTaskFolder(dynamic folder, List<string> values)
    {
        object? tasks = null, folders = null;
        try
        {
            tasks = folder.GetTasks(1);
            foreach (dynamic task in (dynamic)tasks)
            {
                object? definition = null, actions = null;
                try
                {
                    definition = task.Definition;
                    actions = ((dynamic)definition).Actions;
                    foreach (dynamic action in (dynamic)actions)
                    {
                        try
                        {
                            var path = action.Path as string;
                            var args = action.Arguments as string;
                            if (!string.IsNullOrWhiteSpace(path)) values.Add(path + " " + args);
                        }
                        catch { }
                        finally { ReleaseCom(action); }
                    }
                }
                finally { ReleaseCom(actions); ReleaseCom(definition); ReleaseCom(task); }
            }
            folders = folder.GetFolders(0);
            foreach (dynamic child in (dynamic)folders)
            {
                ReadTaskFolder(child, values);
            }
        }
        catch { }
        finally { ReleaseCom(folders); ReleaseCom(tasks); ReleaseCom(folder); }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static ProcessReport BuildProcessReport(
        IReadOnlyDictionary<int, ProcessSnapshot> processes,
        IReadOnlySet<int> listeners,
        IReadOnlySet<int> servicePids,
        IReadOnlyList<string> protectedTokens)
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentSession = currentProcess.SessionId;
        var matched = new Dictionary<int, string>();
        foreach (var process in processes.Values)
        {
            var command = process.CommandLine.Length > 0 ? process.CommandLine : process.Name;
            if (IsClaude(command)) matched[process.ProcessId] = "claude";
            else if (IsCodex(command)) matched[process.ProcessId] = "codex";
            else if (IsGemini(command)) matched[process.ProcessId] = "gemini";
            else if (IsPiDesktop(process)) matched[process.ProcessId] = "pi-desktop";
        }
        var ollamaRunning = processes.Values.Any(process =>
            IsOllama(process.CommandLine.Length > 0 ? process.CommandLine : process.Name));
        var roots = matched.Where(x => !HasMatchedAncestor(x.Key, processes, matched)).ToArray();
        var activeMcp = 0;
        var activeMemory = 0d;
        var orphans = new List<OrphanProcess>();
        var protectedReasons = new List<string>();
        foreach (var process in processes.Values)
        {
            if (!IsMcpRunner(process, out var serviceName)) continue;
            var parentAlive = process.ParentProcessId > 0 && processes.ContainsKey(process.ParentProcessId);
            if (parentAlive)
            {
                activeMcp++;
                activeMemory += process.MemoryMb;
                continue;
            }
            string? protection = null;
            if (process.SessionId != currentSession) protection = "其他用户或服务会话";
            else if (servicePids.Contains(process.ProcessId)) protection = "Windows 服务托管";
            else if (listeners.Contains(process.ProcessId)) protection = "正在监听端口";
            else if (protectedTokens.Any(x => CommandMatchesRegistration(process, x))) protection = "登录启动项或计划任务托管";
            else if (process.ExecutablePath.Contains("Windows", StringComparison.OrdinalIgnoreCase)) protection = "系统目录";
            if (protection is not null)
            {
                protectedReasons.Add($"PID {process.ProcessId}: {protection}");
                continue;
            }
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(process.Name + "\n" + process.CommandLine)))[..16];
            orphans.Add(new(process.ProcessId, process.StartedAt, fingerprint, process.Name, process.CommandLine, process.MemoryMb, serviceName, "父进程已退出，且未监听端口/未被系统托管"));
        }
        return new(
            roots.Count(x => x.Value == "claude"),
            roots.Count(x => x.Value == "codex"),
            roots.Count(x => x.Value == "gemini"),
            roots.Count(x => x.Value == "pi-desktop"),
            ollamaRunning,
            activeMcp, activeMemory, orphans.OrderByDescending(x => x.MemoryMb).ToArray(), protectedReasons.Take(30).ToArray());
    }

    private static bool HasMatchedAncestor(int pid, IReadOnlyDictionary<int, ProcessSnapshot> processes, IReadOnlyDictionary<int, string> matched)
    {
        var current = processes.TryGetValue(pid, out var item) ? item.ParentProcessId : 0;
        for (var hops = 0; current > 0 && hops < 30; hops++)
        {
            if (matched.ContainsKey(current)) return true;
            current = processes.TryGetValue(current, out var parent) ? parent.ParentProcessId : 0;
        }
        return false;
    }

    private static bool IsMcpRunner(ProcessSnapshot process, out string serviceName)
    {
        var executable = Path.GetFileName(process.ExecutablePath.Length > 0 ? process.ExecutablePath : process.Name);
        var command = process.CommandLine.ToLowerInvariant();
        if (!Runners.Contains(executable) && !Runners.Any(x => command.StartsWith(x + " ", StringComparison.OrdinalIgnoreCase)))
        {
            serviceName = "";
            return false;
        }
        foreach (var definition in McpServices)
        {
            if (command.Contains(definition.Key, StringComparison.OrdinalIgnoreCase))
            {
                serviceName = definition.Label;
                return true;
            }
        }
        if (command.Contains("@modelcontextprotocol", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("mcp-server", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("\\_npx\\", StringComparison.OrdinalIgnoreCase) && command.Contains("mcp", StringComparison.OrdinalIgnoreCase))
        {
            serviceName = "MCP 服务";
            return true;
        }
        serviceName = "";
        return false;
    }

    private static bool IsClaude(string command) => IsCli(command, "claude") && !command.Contains("mcp-server", StringComparison.OrdinalIgnoreCase);
    private static bool IsCodex(string command) => IsCli(command, "codex") && !command.Contains("mcp-server", StringComparison.OrdinalIgnoreCase) && !command.Contains("codex-path", StringComparison.OrdinalIgnoreCase);
    private static bool IsGemini(string command) => IsCli(command, "agy") || IsCli(command, "gemini");
    public static bool IsPiDesktop(ProcessSnapshot process) =>
        Path.GetFileName(process.ExecutablePath.Length > 0 ? process.ExecutablePath : process.Name)
            .Equals("PI-Desktop.exe", StringComparison.OrdinalIgnoreCase);
    private static bool IsOllama(string command)
    {
        var value = command.Trim().TrimStart('"').ToLowerInvariant();
        var first = value.Split(' ', 2)[0].Trim('"');
        return Path.GetFileNameWithoutExtension(first).Equals("ollama", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("\\ollama.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCli(string command, string name)
    {
        var value = command.Trim().TrimStart('"').ToLowerInvariant();
        if (value.StartsWith("cmd.exe ") || value.StartsWith("powershell.exe ") || value.StartsWith("pwsh.exe ")) return false;
        var first = value.Split(' ', 2)[0].Trim('"');
        var file = Path.GetFileNameWithoutExtension(first);
        if (file.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Equals("node", StringComparison.OrdinalIgnoreCase))
            return value.Contains($"\\{name}", StringComparison.OrdinalIgnoreCase) || value.Contains($"/{name}", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool CommandMatchesRegistration(ProcessSnapshot process, string registration)
    {
        if (registration.Contains(process.ExecutablePath, StringComparison.OrdinalIgnoreCase) && process.ExecutablePath.Length > 5) return true;
        var tokens = process.CommandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim('"')).Where(x => x.Length >= 8).Take(4);
        return tokens.Any(x => registration.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static class NativeMethods
    {
        private const int AddressFamilyInet = 2;
        private const int AddressFamilyInet6 = 23;
        private const int TcpTableOwnerPidListener = 3;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        internal static HashSet<int> ListeningProcessIds()
        {
            var result = new HashSet<int>();
            ReadTable(AddressFamilyInet, 24, 20, result);
            ReadTable(AddressFamilyInet6, 56, 52, result);
            return result;
        }

        private static void ReadTable(int family, int rowSize, int pidOffset, HashSet<int> result)
        {
            var size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TcpTableOwnerPidListener, 0);
            if (size <= 4) return;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, true, family, TcpTableOwnerPidListener, 0) != 0) return;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++) result.Add(Marshal.ReadInt32(buffer, 4 + i * rowSize + pidOffset));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal sealed class MemoryStatusEx
        {
            internal uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
            internal uint MemoryLoad;
            internal ulong TotalPhysical;
            internal ulong AvailablePhysical;
            internal ulong TotalPageFile;
            internal ulong AvailablePageFile;
            internal ulong TotalVirtual;
            internal ulong AvailableVirtual;
            internal ulong AvailableExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            internal uint Low;
            internal uint High;
            internal ulong ToUInt64() => ((ulong)High << 32) | Low;
        }
    }
}
