using System.IO;
using System.Text.RegularExpressions;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed partial class WindowsSystemScanner
{
    internal static bool IsClaudeDesktop(ProcessSnapshot process)
    {
        var executable = process.ExecutablePath;
        if (!Path.GetFileName(executable).Equals("claude.exe", StringComparison.OrdinalIgnoreCase)) return false;
        // Store and classic Electron installs. The native Code CLI also uses claude.exe.
        return Regex.IsMatch(executable, @"\\WindowsApps\\Claude_[^\\]+\\app\\claude\.exe$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(executable, @"\\Claude\\app-[^\\]+\\claude\.exe$", RegexOptions.IgnoreCase) ||
            File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "resources", "app.asar"));
    }

    private static bool IsClaudeCode(ProcessSnapshot process, string command) =>
        !command.Contains("mcp-server", StringComparison.OrdinalIgnoreCase) &&
        (Path.GetFileName(process.ExecutablePath).Equals("claude.exe", StringComparison.OrdinalIgnoreCase) ||
            process.CommandLine.Length > 0 && IsClaude(command));

    private static string ClaudeDataDirectory(int root, IReadOnlyDictionary<int, ProcessSnapshot> processes)
    {
        var directories = processes.Values.Where(p => IsClaudeDesktop(p) && IsDescendantOrSelf(p.ProcessId, root, processes))
            .Select(p => Regex.Match(p.CommandLine, "(?:^|\\s)--user-data-dir(?:=|\\s+)(?:\"([^\"]+)\"|([^\\s]+))", RegexOptions.IgnoreCase))
            .Where(m => m.Success).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return directories.Length == 1 ? directories[0] : "";
    }

    private static bool IsDescendantOrSelf(int pid, int root, IReadOnlyDictionary<int, ProcessSnapshot> processes)
    {
        for (var hops = 0; pid > 0 && hops < 30; hops++)
        {
            if (pid == root) return true;
            pid = processes.TryGetValue(pid, out var p) ? p.ParentProcessId : 0;
        }
        return false;
    }
}
