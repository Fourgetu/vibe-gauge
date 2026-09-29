namespace VibeGauge.Core;

public sealed record InventoryArea(string Name, string Root, bool Sessions, bool Cache = false);
public sealed record InventoryItem(InventoryArea Area, long Bytes, int Files, bool Partial);
public sealed record CleanupFile(string Path, long Bytes, DateTime LastWriteUtc);
public sealed record DiskCleanupPlan(InventoryArea Area, DateTime CutoffUtc, IReadOnlyList<CleanupFile> Files)
{
    public long Bytes => Files.Sum(x => x.Bytes);
}
public sealed record DiskCleanupResult(int Deleted, int Skipped, long Bytes);

public sealed class DiskInventory(AppPaths paths)
{
    public IReadOnlyList<InventoryArea> Areas => [
        new("Claude", Path.Combine(paths.ClaudeRoot, "projects"), true),
        new("Codex", Path.Combine(paths.CodexRoot, "sessions"), true),
        new("Codex archives", Path.Combine(paths.CodexRoot, "archived_sessions"), true),
        new("PI-Desktop", paths.PiDesktopSessions, true), new("WorkBuddy", Path.Combine(paths.WorkBuddyRoot, "projects"), true),
        new("WorkBuddy AI", Path.Combine(paths.WorkBuddyAiRoot, "projects"), true), new("DSH Desktop", paths.DshSessions, true),
        new("ZCode database (read-only)", Path.GetDirectoryName(paths.ZCodeDatabase)!, false),
        new("Ollama models (read-only)", Path.Combine(paths.Home, ".ollama", "models"), false),
        new("LM Studio models (read-only)", Path.Combine(paths.Home, ".lmstudio", "models"), false),
        new("NPX", Path.Combine(paths.Home, "AppData", "Local", "npm-cache", "_npx"), false, true)];

    public IReadOnlyList<InventoryItem> Scan() => Areas.Select(area =>
    {
        var files = Enumerate(area.Root, out var partial);
        return new InventoryItem(area, files.Sum(x => x.Length), files.Count, partial);
    }).ToArray();

    public DiskCleanupPlan Preview(InventoryArea area, int days, DateTimeOffset now)
    {
        ValidateArea(area);
        if (days < 7 || days > 3650) throw new ArgumentOutOfRangeException(nameof(days), "Retention must be 7-3650 days.");
        var cutoff = now.UtcDateTime.AddDays(-days);
        var files = Enumerate(area.Root, out _).Where(x => x.LastWriteTimeUtc < cutoff &&
            (area.Cache || IsSession(area, x.FullName)))
            .Select(x => new CleanupFile(x.FullName, x.Length, x.LastWriteTimeUtc)).ToArray();
        return new(area, cutoff, files);
    }

    public DiskCleanupResult Delete(DiskCleanupPlan plan, Func<bool> ensureDurableHistory)
    {
        ValidateArea(plan.Area);
        if (plan.CutoffUtc > DateTime.UtcNow.AddDays(-7)) throw new IOException("Refusing recent-file cleanup.");
        if (!plan.Area.Cache && !ensureDurableHistory()) throw new IOException("用量账本未成功保存，已取消删除。");
        int deleted = 0, skipped = 0; long bytes = 0;
        foreach (var file in plan.Files)
        {
            try
            {
                if (!SafePath(plan.Area.Root, file.Path) || !plan.Area.Cache && !IsSession(plan.Area, file.Path)) { skipped++; continue; }
                var info = new FileInfo(file.Path);
                if (!info.Exists || info.Length != file.Bytes || info.LastWriteTimeUtc != file.LastWriteUtc || info.LastWriteTimeUtc >= plan.CutoffUtc)
                { skipped++; continue; }
                File.Delete(info.FullName); deleted++; bytes += file.Bytes;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(deleted, skipped, bytes);
    }
    private void ValidateArea(InventoryArea area)
    {
        if (!Areas.Contains(area) || !area.Sessions && !area.Cache) throw new IOException("Not an allowlisted cleanup area.");
    }
    private static bool IsSession(InventoryArea area, string file) => area.Name == "DSH Desktop"
        ? Path.GetFileName(file).StartsWith("session.v", StringComparison.Ordinal) && (file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase))
        : file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
    public static bool SafePath(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            if (current == Path.GetPathRoot(current)) break;
        }
        return true;
    }
    private static List<FileInfo> Enumerate(string root, out bool partial)
    {
        var result = new List<FileInfo>(); partial = false;
        if (!Directory.Exists(root)) return result;
        var pending = new Stack<string>(); pending.Push(root);
        try
        {
            if (!SafePath(root, Path.Combine(root, ".probe"))) return result;
            while (pending.TryPop(out var directory))
            {
                try
                {
                    foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint)) { partial = true; continue; }
                        if (Directory.Exists(item)) pending.Push(item);
                        else result.Add(new(item));
                        if (result.Count >= 200_000 || pending.Count >= 20_000) { partial = true; return result; }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { partial = true; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { partial = true; }
        return result;
    }
}
