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
    public bool LanClashController { get; init; }
    public string ClashSourceIp { get; init; } = "";
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

public sealed record EgressInfo(string Provider, string Host, string Ip, string Region, string Colo, string Error, long? LatencyMs = null, DateTimeOffset? CapturedAt = null, string Chain = "");
public sealed record ProxyConnection(string Provider, string Host, string Chain);
public sealed record NetworkDiagnosticsReport(DateTimeOffset CapturedAt, IReadOnlyList<EgressInfo> Exits,
    IReadOnlyList<string> Proxy, IReadOnlyList<string> Local, IReadOnlyList<string> Changes, string Dns, string Ipv6, IReadOnlyList<ProxyConnection>? Connections = null)
{
    public string Comparison
    {
        get
        {
            var traces = Exits.Where(x => x.Provider != "Gemini").ToArray();
            var valid = traces.Where(x => x.Error.Length == 0 && x.Ip.Length > 0).ToArray();
            var state = valid.Length == 0 ? "无有效出口结果" : valid.Length == 1 ? "仅一个有效结果，无法比较" :
                valid.Select(x => x.Ip).Distinct().Count() == 1 ? "已确认的出口一致" : "各站点出口不同，可能由分流规则造成";
            return $"{state} · 成功 {valid.Length}/{traces.Length} · Gemini 使用活动连接链判断";
        }
    }
}
