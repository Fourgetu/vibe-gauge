namespace VibeGauge.Core;

public sealed class AppPaths
{
    public AppPaths(string? home = null, string? localAppData = null)
    {
        Home = Path.GetFullPath(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        LocalDataRoot = Path.GetFullPath(localAppData ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeGauge"));
    }

    public string Home { get; }
    public string LocalDataRoot { get; }
    public string LegacyDataRoot => Path.Combine(Home, ".config", "vibegauge");
    public string ClaudeRoot => Path.Combine(Home, ".claude");
    public string ClaudeSettings => Path.Combine(Home, ".claude.json");
    public string CodexRoot => Path.Combine(Home, ".codex");
    public string GeminiRoot => Path.Combine(Home, ".gemini");
    public string PiDesktopRoot => Path.Combine(Home, ".pi-desktop");
    public string PiDesktopSessions => Path.Combine(PiDesktopRoot, "sessions");
    public string AgyQuota => Path.Combine(Home, ".cache", "agy-hud", "quota_cache.json");

    public string ResolveOwnDataFile(string fileName)
    {
        var current = Path.Combine(LocalDataRoot, fileName);
        if (File.Exists(current)) return current;
        var legacy = Path.Combine(LegacyDataRoot, fileName);
        return File.Exists(legacy) ? legacy : current;
    }
}
