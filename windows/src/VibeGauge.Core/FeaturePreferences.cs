using System.Text.Json;

namespace VibeGauge.Core;

public sealed record FeaturePreferences
{
    public bool NetworkDiagnostics { get; init; }
    public bool EgressNotifications { get; init; }
    public bool PendingNotifications { get; init; }
    public bool QuotaNotifications { get; init; }
    public bool MemoryNotifications { get; init; }
    public bool DiskNotifications { get; init; }
    public bool AutoReap { get; init; }
    public bool CheckUpdates { get; init; }
    public string Language { get; init; } = "system";
    public int QuotaThreshold { get; init; } = 90;
    public int MemoryFreeThreshold { get; init; } = 10;
    public int DiskFreeGbThreshold { get; init; } = 10;
    public int RetentionDays { get; init; } = 90;
    public string ClashController { get; init; } = "http://127.0.0.1:9090";
    public static FeaturePreferences Load(AppPaths paths)
    {
        try { return JsonSerializer.Deserialize<FeaturePreferences>(File.ReadAllText(Path.Combine(paths.LocalDataRoot, "features.json"))) ?? new(); }
        catch { return new(); }
    }
    public void Save(AppPaths paths)
    {
        Directory.CreateDirectory(paths.LocalDataRoot);
        var file = Path.Combine(paths.LocalDataRoot, "features.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }
}

public sealed record EgressInfo(string Provider, string Host, string Ip, string Region, string Colo, string Error);
public sealed record NetworkDiagnosticsReport(DateTimeOffset CapturedAt, IReadOnlyList<EgressInfo> Exits,
    IReadOnlyList<string> Proxy, IReadOnlyList<string> Local, IReadOnlyList<string> Changes, string Dns, string Ipv6);
