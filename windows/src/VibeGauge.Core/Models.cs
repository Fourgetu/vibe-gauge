using System.Text.Json.Serialization;

namespace VibeGauge.Core;

public enum ProviderDataState
{
    Available,
    NotSignedIn,
    NoQuota,
    ReadFailed,
    Stale,
    NotRunning
}

public enum UsageDataState
{
    Available,
    NoLocalStats,
    NotDetected,
    ReadFailed
}

public sealed record QuotaWindow(
    int UsedPercent,
    DateTimeOffset? ResetsAt,
    DateTimeOffset? CapturedAt,
    TimeSpan Window,
    bool IsRolling = false,
    bool IsEstimate = false,
    int ReleaseCount = 0,
    double RecentPercentPerHour = 0,
    int RecentSpanMinutes = 0,
    int? LastCyclePercent = null)
{
    public int EffectivePercent(DateTimeOffset now) =>
        !IsRolling && ResetsAt is { } reset && reset <= now ? 0 : Math.Clamp(UsedPercent, 0, 100);

    public TimeSpan? Age(DateTimeOffset now) => CapturedAt is { } captured ? now - captured : null;

    public string Trust(DateTimeOffset now) =>
        !IsRolling && ResetsAt <= now ? "等待新回报" :
        IsRolling || IsEstimate ? "本机估算" :
        Age(now)?.TotalSeconds > (Window > TimeSpan.Zero ? Window.TotalSeconds / 5 : 86400)
            ? "可能过期" : "官方回报";
}

public sealed record PlatformStatus(
    string Name,
    string Tier,
    bool IsRunning,
    int Sessions,
    ProviderDataState DataState,
    string Detail,
    QuotaWindow? FiveHour = null,
    QuotaWindow? Weekly = null,
    string SecondaryPoolName = "",
    QuotaWindow? SecondaryFiveHour = null,
    QuotaWindow? SecondaryWeekly = null,
    int? ModelCount = null,
    QuotaWindow? Monthly = null,
    QuotaWindow? Daily = null,
    bool AlwaysShowDetail = false,
    string CompactDetail = "",
    DesktopTokenDisplay? DesktopTokens = null,
    ProviderTokenTotals? ReportedTokens = null);

public sealed record InteractionRecord(
    string Id,
    string Source,
    string Model,
    DateTimeOffset Timestamp,
    long ContextTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    long ThinkingTokens,
    int Status = 0,
    int LatencyMs = 0)
{
    [JsonIgnore] public long TotalTokens => ContextTokens + OutputTokens;
    public double? CacheHitRate => ContextTokens <= 0 ? null : CacheReadTokens * 100.0 / ContextTokens;
}

public sealed record UsageSourceSummary(
    string Name,
    UsageDataState State,
    int Turns,
    long ContextTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    long ThinkingTokens,
    string Note)
{
    [JsonIgnore] public long TotalTokens => ContextTokens + OutputTokens;
    public double? CacheHitRate => ContextTokens <= 0 ? null : CacheReadTokens * 100.0 / ContextTokens;
}

public sealed record UsageSummary(
    int Turns,
    long ContextTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    long ThinkingTokens,
    IReadOnlyList<UsageSourceSummary> Sources,
    IReadOnlyList<InteractionRecord> Recent,
    string Error = "")
{
    public static UsageSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, [], [], "");
    [JsonIgnore] public long TotalTokens => ContextTokens + OutputTokens;
    public double? CacheHitRate => ContextTokens <= 0 ? null : CacheReadTokens * 100.0 / ContextTokens;
}

public sealed record ApiProviderSummary(
    string Name,
    int Calls,
    long ContextTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    long ThinkingTokens,
    int Errors = 0,
    int AverageLatencyMs = 0)
{
    [JsonIgnore] public long TotalTokens => ContextTokens + OutputTokens;
    public double? CacheHitRate => ContextTokens <= 0 ? null : CacheReadTokens * 100.0 / ContextTokens;
}

public sealed record ApiModelSummary(
    string Provider,
    string Model,
    int Calls,
    long ContextTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    long ThinkingTokens,
    int Errors,
    int AverageLatencyMs)
{
    [JsonIgnore] public long TotalTokens => ContextTokens + OutputTokens;
}

public sealed record ApiRecentCall(
    string Provider,
    string Model,
    DateTimeOffset Timestamp,
    long TotalTokens,
    int Status,
    int LatencyMs);

public sealed record ApiRangeSummary(
    string Key,
    string Label,
    int Calls,
    long ContextTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    long ThinkingTokens,
    int Errors,
    int AverageLatencyMs,
    IReadOnlyList<ApiProviderSummary> Providers,
    IReadOnlyList<ApiModelSummary> Models)
{
    [JsonIgnore] public long TotalTokens => ContextTokens + OutputTokens;
}

public sealed record ApiUsageSummary(
    UsageDataState State,
    int Calls,
    long ContextTokens,
    long OutputTokens,
    long ThinkingTokens,
    IReadOnlyList<ApiProviderSummary> Providers,
    string Note,
    IReadOnlyList<ApiRangeSummary>? Ranges = null,
    IReadOnlyList<ApiRecentCall>? Recent = null)
{
    public static ApiUsageSummary Empty { get; } = new(UsageDataState.NotDetected, 0, 0, 0, 0, [], "未检测到 API 日志", [], []);
}

public enum ProxyRunState
{
    Stopped,
    Running,
    PortOccupied,
    Starting,
    Error
}

public sealed record ProxyRuntimeStatus(
    ProxyRunState State,
    int Port,
    int Requests,
    int Parsed,
    int Errors,
    long UptimeSeconds,
    string Detail)
{
    public static ProxyRuntimeStatus Stopped { get; } = new(ProxyRunState.Stopped, 18790, 0, 0, 0, 0, "未运行");
}

public sealed record UsageScanResult(UsageSummary Cli, ApiUsageSummary Api, UsageStatistics? Statistics = null,
    UsageSourceSummary? PiDesktopTotal = null, UsageSourceSummary? ZCodeTotal = null);

public sealed record UsageScannerDiagnostics(int FilesDiscovered, int FilesRead, long BytesRead);

public sealed record SystemMetrics(
    int AvailableMemoryPercent,
    double UsedMemoryGb,
    double TotalMemoryGb,
    double PageFileUsedGb,
    double CpuPercent,
    double DiskFreeGb,
    double DiskTotalGb,
    double NpxCacheMb);

public sealed record ProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string CommandLine,
    string ExecutablePath,
    double MemoryMb,
    DateTimeOffset? StartedAt,
    int SessionId);

public sealed record OrphanProcess(
    int ProcessId,
    DateTimeOffset? StartedAt,
    string CommandFingerprint,
    string Name,
    string CommandLine,
    double MemoryMb,
    string ServiceName,
    string Reason);

public sealed record CleanupResult(int Killed, int Skipped, double FreedMemoryMb, IReadOnlyList<string> Messages);

public sealed record ProcessReport(
    int ClaudeSessions,
    int CodexSessions,
    int GeminiSessions,
    int PiDesktopProcesses,
    bool OllamaRunning,
    int ActiveMcpProcesses,
    double ActiveMcpMemoryMb,
    IReadOnlyList<OrphanProcess> Orphans,
    IReadOnlyList<string> ProtectedReasons,
    int ZCodeProcesses = 0)
{
    public static ProcessReport Empty { get; } = new(0, 0, 0, 0, false, 0, 0, [], []);
}

public sealed record DashboardSnapshot(
    DateTimeOffset CapturedAt,
    SystemMetrics System,
    ProcessReport Processes,
    IReadOnlyList<PlatformStatus> Platforms,
    UsageSummary Usage,
    ApiUsageSummary Api,
    ProxyRuntimeStatus? Proxy = null,
    UsageStatistics? Statistics = null,
    SessionSummary? Sessions = null,
    IReadOnlyList<NetworkAdapterInfo>? Network = null);
