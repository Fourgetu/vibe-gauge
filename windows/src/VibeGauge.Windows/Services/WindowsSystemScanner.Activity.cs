using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed partial class WindowsSystemScanner
{
    private Dictionary<int, ProcessSnapshot> activityMetadata = [];

    private static bool IsActivityCandidate(ProcessSnapshot value)
    {
        var command = value.CommandLine.Length > 0 ? value.CommandLine : value.Name;
        return IsClaude(command) || IsCodex(command) || IsGemini(command) || IsOllama(command) ||
            IsPiDesktop(value) || IsZCode(value) || IsWorkBuddy(value) || IsDsh(value);
    }

    // Toolhelp provides current PIDs and ancestry without WMI, services, tasks or logs.
    // Command-line-only node CLIs are discovered by the regular five-second scan.
    public ProcessReport ScanActivity()
    {
        var metadata = Volatile.Read(ref activityMetadata);
        var live = new Dictionary<int, ProcessSnapshot>();
        using var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32First(handle, ref entry)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        do
        {
            var pid = (int)entry.ProcessId;
            var item = new ProcessSnapshot(pid, (int)entry.ParentProcessId, entry.ExeFile, "", "", 0, null, 0);
            if (metadata.ContainsKey(pid) || IsActivityCandidate(item))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var started = new DateTimeOffset(process.StartTime.ToUniversalTime());
                    // PID reuse must never inherit another process's command line.
                    if (metadata.TryGetValue(pid, out var old) && SameActivityIdentity(old, item, started))
                        item = old with { ParentProcessId = item.ParentProcessId };
                    else item = item with { StartedAt = started };
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                { continue; }
            }
            live[pid] = item;
        } while (Process32Next(handle, ref entry));
        var errorCode = Marshal.GetLastWin32Error();
        if (errorCode != 18) throw new System.ComponentModel.Win32Exception(errorCode);
        return BuildReport(live, null, null, [], includeMcp: false);
    }

    internal static bool SameActivityIdentity(ProcessSnapshot old, ProcessSnapshot current, DateTimeOffset started) =>
        old.StartedAt is { } previous && Math.Abs((previous - started).TotalMilliseconds) < 1 &&
        old.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle handle, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle handle, ref ProcessEntry entry);
}
