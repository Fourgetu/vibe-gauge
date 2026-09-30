using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows.Services;

namespace VibeGauge.Windows.ViewModels;

public sealed class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DashboardCoordinator coordinator;
    private readonly StartupRegistrar startup;
    private readonly ProxyManager proxy;
    private readonly ProxySettings proxySettings;
    private readonly TokenUnitSettings tokenUnitSettings;
    private readonly ClientVisibilitySettings clientVisibilitySettings;
    private readonly HashSet<string> hiddenClients;
    private bool internationalTokens;
    private readonly DispatcherTimer timer;
    private bool refreshing;
    private string statusText = "正在读取本地数据...";
    private string headerMemoryText = "内存 --";
    private string headerApiText = "API 今日 --";
    private string memoryText = "--";
    private string cpuText = "--";
    private string diskText = "--";
    private string pageFileText = "--";
    private string npxText = "--";
    private string mcpText = "--";
    private string contextText = "暂无";
    private string totalTokensText = "暂无";
    private string cacheHitText = "暂无";
    private string generatedText = "暂无";
    private string thinkingText = "暂无";
    private string callsText = "0";
    private string apiHeadline = "未检测到 API 日志";
    private string apiDetail = "Phase 2 已暂停，仅显示已有本地记录";
    private string proxyStatusText = "Stopped";
    private string proxyDetail = "未运行";
    private string proxyTone = "Muted";
    private string proxyActionText = "启动";
    private string activeProviderText = "0 个活动";
    private double cacheHitPercent;
    private double memoryUsedPercent;
    private double diskUsedPercent;
    private bool startupEnabled;
    private int selectedTab;
    private string selectedApiRange = "today";
    private DashboardSnapshot? lastSnapshot;
    private string? selectedProviderName;
    public string? SelectedProviderName => selectedProviderName;
    public bool IsProviderDetailOpen => selectedProviderName is not null;
    public bool IsOverview => !IsProviderDetailOpen;
    internal DashboardSnapshot? CurrentSnapshot => lastSnapshot;

    public bool OpenProviderDetail(string name)
    {
        if (!Platforms.Any(x => x.Name == name && x.CanOpenDetail)) return false;
        selectedProviderName = name;
        Raise(nameof(SelectedProviderName)); Raise(nameof(IsProviderDetailOpen)); Raise(nameof(IsOverview));
        return true;
    }

    public void CloseProviderDetail()
    {
        if (selectedProviderName is null) return;
        selectedProviderName = null;
        Raise(nameof(SelectedProviderName)); Raise(nameof(IsProviderDetailOpen)); Raise(nameof(IsOverview));
    }

    public DashboardViewModel(
        DashboardCoordinator coordinator,
        StartupRegistrar startup,
        ProxyManager proxy,
        ProxySettings proxySettings)
    {
        this.coordinator = coordinator;
        this.startup = startup;
        this.proxy = proxy;
        this.proxySettings = proxySettings;
        tokenUnitSettings = new(coordinator.Paths.LocalDataRoot);
        internationalTokens = tokenUnitSettings.Load() == TokenUnit.International;
        clientVisibilitySettings = new(coordinator.Paths.LocalDataRoot);
        hiddenClients = clientVisibilitySettings.Load();
        EnsureClientOptions(["Claude", "Codex", "Gemini", "ZCode", "PI-Desktop", "Ollama", "WorkBuddy", "DSH Desktop"]);
        startupEnabled = startup.IsEnabled;
        autoStartProxy = proxySettings.AutoStart;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DashboardSnapshot>? SnapshotChanged;

    public ObservableCollection<PlatformRow> Platforms { get; } = [];
    public ObservableCollection<ClientDisplayOption> ClientOptions { get; } = [];
    public bool HasNoVisibleClients => Platforms.Count == 0;

    public bool IsClientVisible(string name) => !hiddenClients.Contains(name == "Claude Code" ? "Claude" : name);

    public SessionSummary? VisibleSessions(SessionSummary? sessions) => sessions is null || hiddenClients.Count == 0 ? sessions :
        new(sessions.Active.Where(x => IsClientVisible(x.Tool)).ToArray(), sessions.Pending.Where(x => IsClientVisible(x.Tool)).ToArray());

    private void EnsureClientOptions(IEnumerable<string> names)
    {
        foreach (var name in names)
            if (!ClientOptions.Any(x => x.Name == name))
                ClientOptions.Add(new(name, IsClientVisible(name), SetClientVisibility));
    }

    private void SetClientVisibility(string name, bool visible)
    {
        if (visible) hiddenClients.Remove(name); else hiddenClients.Add(name);
        var saved = clientVisibilitySettings.Save(hiddenClients);
        if (lastSnapshot is { } snapshot)
        {
            Apply(snapshot);
            SnapshotChanged?.Invoke(this, snapshot);
        }
        if (!saved) StatusText = "显示已更新，但设置保存失败，重启后可能恢复默认";
    }
    public ObservableCollection<UsageSourceRow> UsageSources { get; } = [];
    public ObservableCollection<RecentRow> Recent { get; } = [];
    public ObservableCollection<ApiProviderRow> ApiProviders { get; } = [];
    public ObservableCollection<ApiModelRow> ApiModels { get; } = [];
    public ObservableCollection<ApiRecentRow> ApiRecent { get; } = [];
    public ObservableCollection<OrphanProcess> Orphans { get; } = [];

    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string AppVersionText => AppVersionInfo.DisplayVersion;
    public TokenUnit SelectedTokenUnit => internationalTokens ? TokenUnit.International : TokenUnit.Chinese;
    public string TokenUnitLabel => internationalTokens ? "K / M / B" : "万 / 亿";
    public bool UseInternationalTokens
    {
        get => internationalTokens;
        set
        {
            if (value == internationalTokens) return;
            Set(ref internationalTokens, value);
            Raise(nameof(SelectedTokenUnit));
            Raise(nameof(TokenUnitLabel));
            var saved = tokenUnitSettings.Save(SelectedTokenUnit);
            if (lastSnapshot is { } snapshot)
            {
                Apply(snapshot);
                SnapshotChanged?.Invoke(this, snapshot);
            }
            if (!saved) StatusText = "单位已切换，但设置保存失败，重启后可能恢复默认";
        }
    }
    private string Tokens(long value) => Formatting.Tokens(value, SelectedTokenUnit);
    public string HeaderMemoryText { get => headerMemoryText; private set => Set(ref headerMemoryText, value); }
    public string HeaderApiText { get => headerApiText; private set => Set(ref headerApiText, value); }
    public string MemoryText { get => memoryText; private set => Set(ref memoryText, value); }
    public string CpuText { get => cpuText; private set => Set(ref cpuText, value); }
    public string DiskText { get => diskText; private set => Set(ref diskText, value); }
    public string PageFileText { get => pageFileText; private set => Set(ref pageFileText, value); }
    public string NpxText { get => npxText; private set => Set(ref npxText, value); }
    public string McpText { get => mcpText; private set => Set(ref mcpText, value); }
    public string ContextText { get => contextText; private set => Set(ref contextText, value); }
    public string TotalTokensText { get => totalTokensText; private set => Set(ref totalTokensText, value); }
    public string CacheHitText { get => cacheHitText; private set => Set(ref cacheHitText, value); }
    public string GeneratedText { get => generatedText; private set => Set(ref generatedText, value); }
    public string ThinkingText { get => thinkingText; private set => Set(ref thinkingText, value); }
    public string CallsText { get => callsText; private set => Set(ref callsText, value); }
    public string ApiHeadline { get => apiHeadline; private set => Set(ref apiHeadline, value); }
    public string ApiDetail { get => apiDetail; private set => Set(ref apiDetail, value); }
    public string ProxyStatusText { get => proxyStatusText; private set => Set(ref proxyStatusText, value); }
    public string ProxyDetail { get => proxyDetail; private set => Set(ref proxyDetail, value); }
    public string ProxyTone { get => proxyTone; private set => Set(ref proxyTone, value); }
    public string ProxyActionText { get => proxyActionText; private set => Set(ref proxyActionText, value); }
    public string ProxyEndpoint => proxy.Prefix;
    public async Task ConfigureProxyAsync(int port, string? route)
    {
        await proxy.ConfigureAsync(port, route);
        Raise(nameof(ProxyEndpoint));
        await RefreshAsync();
    }
    public string ActiveProviderText { get => activeProviderText; private set => Set(ref activeProviderText, value); }
    public double CacheHitPercent { get => cacheHitPercent; private set => Set(ref cacheHitPercent, value); }
    public double MemoryUsedPercent { get => memoryUsedPercent; private set => Set(ref memoryUsedPercent, value); }
    public double DiskUsedPercent { get => diskUsedPercent; private set => Set(ref diskUsedPercent, value); }
    public bool IsRefreshing { get => refreshing; private set => Set(ref refreshing, value); }
    public int OrphanCount => Orphans.Count;
    public double OrphanMemoryMb => Orphans.Sum(x => x.MemoryMb);
    public bool IsSubscriptionSelected => selectedTab == 0;
    public bool IsApiSelected => selectedTab == 1;
    public bool IsSystemSelected => selectedTab == 2;
    public bool IsStatsSelected => selectedTab == 3;
    public bool IsNetworkSelected => selectedTab == 4;
    public AppPaths Paths => coordinator.Paths;
    public void InvalidateOfficial() => coordinator.InvalidateOfficial();
    public bool EnsureDurableHistory() => coordinator.EnsureDurableHistory();
    public bool HasNoRecent => Recent.Count == 0;
    public bool HasApiProviders => ApiProviders.Count > 0;
    public bool HasApiModels => ApiModels.Count > 0;
    public bool HasApiRecent => ApiRecent.Count > 0;
    public bool IsApiToday => selectedApiRange == "today";
    public bool IsApi7Days => selectedApiRange == "7d";
    public bool IsApi30Days => selectedApiRange == "30d";
    public bool IsApiAll => selectedApiRange == "all";
    private bool autoStartProxy;

    public bool AutoStartProxy
    {
        get => autoStartProxy;
        set
        {
            if (value == autoStartProxy) return;
            proxySettings.AutoStart = value;
            Set(ref autoStartProxy, value);
        }
    }

    public bool StartupEnabled
    {
        get => startupEnabled;
        set
        {
            if (value == startupEnabled) return;
            startup.SetEnabled(value);
            Set(ref startupEnabled, value);
        }
    }

    public void SelectTab(int index)
    {
        CloseProviderDetail();
        if (selectedTab == index) return;
        selectedTab = index;
        Raise(nameof(IsSubscriptionSelected));
        Raise(nameof(IsApiSelected));
        Raise(nameof(IsSystemSelected));
        Raise(nameof(IsStatsSelected));
        Raise(nameof(IsNetworkSelected));
    }

    public void SelectApiRange(string range)
    {
        if (selectedApiRange == range) return;
        selectedApiRange = range;
        Raise(nameof(IsApiToday));
        Raise(nameof(IsApi7Days));
        Raise(nameof(IsApi30Days));
        Raise(nameof(IsApiAll));
        if (lastSnapshot is not null) ApplyApi(lastSnapshot.Api);
    }

    public async Task ToggleProxyAsync()
    {
        var status = lastSnapshot?.Proxy ?? await proxy.GetStatusAsync();
        ProxyRuntimeStatus? actionStatus = null;
        if (status.State == ProxyRunState.Running) actionStatus = await proxy.StopOwnedAsync();
        else if (status.State != ProxyRunState.PortOccupied) await proxy.StartAsync();
        await RefreshAsync();
        if (actionStatus is { State: ProxyRunState.Running } &&
            actionStatus.Detail.Contains("不是由本次", StringComparison.Ordinal))
            ApplyProxy(actionStatus);
    }

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var snapshot = await coordinator.ScanAsync();
            Apply(snapshot);
            StatusText = $"更新于 {snapshot.CapturedAt:HH:mm:ss}";
            SnapshotChanged?.Invoke(this, snapshot);
        }
        catch (Exception error) { StatusText = "刷新失败：" + error.Message; }
        finally { IsRefreshing = false; }
    }

    public async Task<CleanupResult> CleanAsync()
    {
        var targets = Orphans.ToArray();
        var result = await coordinator.CleanAsync(targets);
        await RefreshAsync();
        return result;
    }

    private void Apply(DashboardSnapshot value)
    {
        lastSnapshot = value;
        var system = value.System;
        HeaderMemoryText = $"RAM {system.AvailableMemoryPercent}% 可用";
        HeaderApiText = $"API 今日 {value.Api.Calls} 次";
        MemoryUsedPercent = Math.Clamp(100 - system.AvailableMemoryPercent, 0, 100);
        DiskUsedPercent = system.DiskTotalGb <= 0 ? 0 : Math.Clamp((system.DiskTotalGb - system.DiskFreeGb) / system.DiskTotalGb * 100, 0, 100);
        MemoryText = $"已用 {system.UsedMemoryGb:0.0} / {system.TotalMemoryGb:0.0} GB";
        CpuText = $"{system.CpuPercent:0}%";
        DiskText = $"可用 {system.DiskFreeGb:0.0} / {system.DiskTotalGb:0.0} GB";
        PageFileText = $"页面文件 {system.PageFileUsedGb:0.00} GB";
        NpxText = $"NPX 缓存 {system.NpxCacheMb:0} MB";
        McpText = value.Processes.ActiveMcpProcesses > 0
            ? $"{value.Processes.ActiveMcpProcesses} 个活动进程 · {value.Processes.ActiveMcpMemoryMb:0} MB"
            : "没有活动 MCP 进程";

        var usage = value.Usage;
        ContextText = usage.Turns == 0 ? "暂无" : Tokens(usage.ContextTokens);
        TotalTokensText = usage.Turns == 0 ? "暂无" : Tokens(usage.TotalTokens);
        CacheHitText = usage.CacheHitRate is { } hit ? $"{hit:0.0}%" : "暂无";
        CacheHitPercent = usage.CacheHitRate ?? 0;
        GeneratedText = usage.Turns == 0 ? "暂无" : Tokens(usage.OutputTokens);
        ThinkingText = usage.Turns == 0 ? "暂无" : Tokens(usage.ThinkingTokens);
        CallsText = usage.Turns.ToString();

        var recordedSources = usage.Sources.Where(s => s.Turns > 0).Select(s => s.Name)
            .Concat((value.Statistics?.DailyModels ?? []).Where(x => x.Usage.Calls > 0).Select(x => x.Usage.Source))
            .ToHashSet(StringComparer.Ordinal);
        EnsureClientOptions(value.Platforms.Select(x => x.Name));
        Platforms.ReplaceWith(value.Platforms.Where(x => IsClientVisible(x.Name)).Select(x => PlatformRow.From(x, value.Statistics?.ProfileFor(x.Name), SelectedTokenUnit) with
            { CanOpenDetail = ProviderDetails.CanOpen(x) || recordedSources.Any(s => ProviderDetails.Matches(x.Name, s)) ||
                value.Sessions?.Active.Any(s => ProviderDetails.Matches(x.Name, s.Tool)) == true }),
            (old, next) => old.Quotas.SequenceEqual(next.Quotas) && old with { Quotas = next.Quotas } == next);
        Raise(nameof(HasNoVisibleClients));
        if (selectedProviderName is { } selected && !Platforms.Any(x => x.Name == selected)) CloseProviderDetail();
        ActiveProviderText = $"{value.Platforms.Count(x => x.IsRunning && IsClientVisible(x.Name))} 个活动";
        UsageSources.ReplaceWith(usage.Sources.Where(x => IsClientVisible(x.Name)).Select(x => UsageSourceRow.From(x, SelectedTokenUnit)));
        Recent.ReplaceWith(usage.Recent.Where(x => IsClientVisible(x.Source)).Select(x => RecentRow.From(x, SelectedTokenUnit)));
        Raise(nameof(HasNoRecent));
        ApplyApi(value.Api);
        Orphans.ReplaceWith(value.Processes.Orphans);

        ApplyProxy(value.Proxy ?? ProxyRuntimeStatus.Stopped);
        Raise(nameof(OrphanCount));
        Raise(nameof(OrphanMemoryMb));
    }

    private void ApplyApi(ApiUsageSummary api)
    {
        var range = api.Ranges?.FirstOrDefault(x => x.Key == selectedApiRange)
                    ?? api.Ranges?.FirstOrDefault()
                    ?? new ApiRangeSummary("today", "今日", api.Calls, api.ContextTokens, 0, 0, api.OutputTokens,
                        api.ThinkingTokens, 0, 0, api.Providers, []);
        ApiProviders.ReplaceWith(range.Providers.Select(x => ApiProviderRow.From(x, SelectedTokenUnit)));
        ApiModels.ReplaceWith(range.Models.Select(x => ApiModelRow.From(x, SelectedTokenUnit)));
        ApiRecent.ReplaceWith((api.Recent ?? []).Select(x => ApiRecentRow.From(x, SelectedTokenUnit)));
        Raise(nameof(HasApiProviders)); Raise(nameof(HasApiModels)); Raise(nameof(HasApiRecent));
        ApiHeadline = range.Calls > 0
            ? $"{range.Label}{(range.UnknownUsage > 0 ? "已记录" : "总")} Token {Tokens(range.TotalTokens)} · {range.Calls} 次" + (range.UnknownUsage > 0 ? $"\n{range.UnknownUsage} 次成功调用用量未知" : "")
            : api.Note;
        ApiDetail = range.Calls > 0
            ? $"输入 {Tokens(range.ContextTokens)} · 输出 {Tokens(range.OutputTokens)}\n缓存 {Tokens(range.CacheReadTokens)} · 思考 {Tokens(range.ThinkingTokens)} · 错误 {range.Errors}"
            : "API Usage 与 CLI Usage 独立统计";
        if (range.Quality is { } quality && range.Calls > 0) ApiDetail += "\n" + quality.Description;
        if (range.Cost is { } cost) ApiDetail += "\n" + cost.Description;
    }

    private void ApplyProxy(ProxyRuntimeStatus value)
    {
        ProxyStatusText = value.State switch
        {
            ProxyRunState.Running => "Running",
            ProxyRunState.PortOccupied => "Port occupied",
            ProxyRunState.Starting => "Starting",
            ProxyRunState.Error => "Error",
            _ => "Stopped"
        };
        ProxyDetail = value.State == ProxyRunState.Running && value.Detail == "运行中"
            ? $"{proxy.Prefix} · 本次 {value.Requests} 请求 · 解析 {value.Parsed}"
            : value.Detail;
        ProxyTone = value.State switch
        {
            ProxyRunState.Running => "Good",
            ProxyRunState.PortOccupied or ProxyRunState.Error => "Danger",
            ProxyRunState.Starting => "Warning",
            _ => "Muted"
        };
        ProxyActionText = value.State == ProxyRunState.Running ? "停止" : "启动";
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() { timer.Stop(); coordinator.Dispose(); }
}

public sealed record QuotaRow(string Label, double Percent, string PercentText, string ResetText, string Tone,
    string TrustText, string ForecastText, bool ForecastWarning = false, bool Secondary = false)
{
    public string Tooltip => $"{Label} · {TrustText} · {ResetText}\n{ForecastText}";
    public static QuotaRow? Create(string label, QuotaWindow? window, ActivityProfile? profile = null, bool secondary = false)
    {
        if (window is null) return null;
        var percent = window.EffectivePercent(DateTimeOffset.Now);
        var now = DateTimeOffset.Now;
        var trust = window.Trust(now);
        var burn = QuotaForecast.Calculate(window, now, profile);
        return new(label, percent, trust == "等待新回报" ? "—" : $"{percent}%",
            (window.IsRolling ? "释放 " : "重置 ") + Formatting.Countdown(window.ResetsAt, now),
            trust is "可能过期" or "等待新回报" ? "Muted" : ToneFor(percent), trust,
            burn is null ? "" : $"预计重置前 {burn.ProjectedPercent}% · {burn.Basis}", burn?.ProjectedPercent >= 100, secondary);
    }

    private static string ToneFor(int percent) => percent >= 100 ? "Danger" : percent >= 80 ? "Warning" : "Good";
}

public sealed record PlatformRow(
    string Name,
    string Tier,
    string RuntimeText,
    string SessionText,
    string Detail,
    string Tone,
    IReadOnlyList<QuotaRow> Quotas,
    bool HasQuota,
    bool HasNoQuota,
    bool IsWide,
    bool ShowDetail = false,
    bool IsCompact = false,
    string CompactDetail = "",
    bool CanOpenDetail = false)
{
    public string DisplayDetail => CompactDetail.Length > 0 ? CompactDetail : Detail;
    public bool ShowQuotaMeters => HasQuota && Name != "Gemini";
    public bool ShowCompactQuotas => HasQuota && Name == "Gemini";
    public bool ShowForecast => HasForecast && !IsCompact;
    public bool ShowReset => HasQuota && !IsCompact;
    public string CompactQuotaSummary => string.Join("\n", Quotas.GroupBy(x => x.Secondary)
        .Select(group => string.Join(" · ", group.Select(x => $"{x.Label} {x.PercentText}"))));
    public string FullTooltip => string.Join("\n", new[] { string.Join(" · ",
            new[] { Name, Tier == "未知" ? "" : Tier, SessionText }.Where(x => x.Length > 0)), Detail }
        .Concat(Quotas.Select(x => $"{x.Label} {x.PercentText} · {x.TrustText} · {x.ResetText}" +
            (x.ForecastText.Length > 0 ? "\n" + x.ForecastText : ""))).Where(x => x.Length > 0));
    public bool IsLocal => Tier == "Local" || Name is "Ollama" or "LM Studio" or "llama.cpp";
    public bool ShowTier => Tier is not ("未知" or "" or "未登录" or "未检测到" or "未安装") && (IsWide || Tier.Length <= 9 && Name.Length + Tier.Length <= 18);
    public double TitleMaxWidth => IsWide ? 270 : ShowTier ? 85 : 120;
    public bool HasSecondary => Quotas.Any(x => x.Secondary);
    public IEnumerable<QuotaRow> PrimaryQuotas => Quotas.Where(x => !x.Secondary);
    public IEnumerable<QuotaRow> SecondaryQuotas => Quotas.Where(x => x.Secondary);
    private QuotaRow? Forecast => Quotas.FirstOrDefault(x => x.Label == "W" && x.ForecastText.Length > 0)
        ?? Quotas.FirstOrDefault(x => x.ForecastText.Length > 0);
    public string ForecastSummary => Forecast is { } forecast ? $"{(forecast.ForecastWarning ? "↗" : "→")} {forecast.Label} {forecast.ForecastText}" : "";
    public bool HasForecast => Forecast is not null;
    public bool ForecastWarning => Forecast?.ForecastWarning == true;
    public string ResetSummary => "重置 " + string.Join(" · ", Quotas.Select(x => x.Label + " " + x.ResetText.Replace("重置 ", "")));
    public string TrustSummary => string.Join(" · ", Quotas.Where(x => x.TrustText != "官方回报").Select(x => x.TrustText).Distinct());
    public bool HasTrustNote => TrustSummary.Length > 0;
    public static PlatformRow From(PlatformStatus value, ActivityProfile? profile = null, TokenUnit unit = TokenUnit.Chinese)
    {
        if (value.DesktopTokens is { } desktopTokens)
        {
            var display = desktopTokens.Format(unit);
            value = value with { Detail = display.Detail, CompactDetail = display.Compact };
        }
        else if (value.ReportedTokens is { HasValues: true } totals)
            value = value with { CompactDetail = value.CompactDetail.Split('\n')[0] + "\n" + totals.Format(unit) };
        var runtime = value.IsRunning ? "运行中" : "未运行";
        var desktop = value.Name is PiDesktopUsage.SourceName or ZCodeUsage.SourceName or WorkBuddyUsage.SourceName or DshUsage.SourceName;
        var session = value.AlwaysShowDetail ? (value.DataState == ProviderDataState.Available ? "已同步" : "查询失败") : desktop ? runtime : value.Name == "Ollama"
            ? value.ModelCount is { } count ? $"{count} 个模型" : "模型数未知"
            : $"{value.Sessions} 个会话";
        var detail = value.AlwaysShowDetail || desktop ? value.Detail : value.DataState switch
        {
            ProviderDataState.NotSignedIn => "未登录",
            ProviderDataState.NoQuota => "未检测到额度",
            ProviderDataState.ReadFailed => "读取失败",
            ProviderDataState.Stale => "额度数据过期",
            ProviderDataState.NotRunning => "未运行",
            _ => value.Detail
        };
        var tone = desktop ? (value.DataState == ProviderDataState.ReadFailed ? "Danger" : value.IsRunning ? "Good" : "Muted") : value.DataState switch
        {
            ProviderDataState.ReadFailed => "Danger",
            ProviderDataState.Stale => "Warning",
            ProviderDataState.Available when value.IsRunning => "Good",
            ProviderDataState.Available => "Good",
            _ => "Muted"
        };
        var five = QuotaRow.Create("5H", value.FiveHour, profile);
        var weekly = QuotaRow.Create("W", value.Weekly, profile);
        var secondaryFive = QuotaRow.Create(value.SecondaryPoolName.Length > 0 ? $"{value.SecondaryPoolName} 5H" : "第二池 5H", value.SecondaryFiveHour, secondary: true);
        var secondaryWeekly = QuotaRow.Create(value.SecondaryPoolName.Length > 0 ? $"{value.SecondaryPoolName} W" : "第二池 W", value.SecondaryWeekly, secondary: true);
        var monthly = QuotaRow.Create("M", value.Monthly, profile);
        var daily = QuotaRow.Create("D", value.Daily, profile);
        var quotas = new[] { five, daily, weekly, secondaryFive, secondaryWeekly, monthly }.Where(x => x is not null).Select(x => x!).ToArray();
        return new(
            value.Name, value.Tier.Length == 0 ? "未知" : value.Tier, runtime, session, detail, tone,
            quotas, quotas.Length > 0, quotas.Length == 0,
            value.Name != "Gemini" && (value.AlwaysShowDetail || value.Name.Length > 14 || value.Monthly is not null || value.SecondaryFiveHour is not null),
            quotas.Length == 0 || value.AlwaysShowDetail,
            desktop || value.AlwaysShowDetail || value.Name == "Gemini",
            value.CompactDetail.Length > 0 ? value.CompactDetail : value.AlwaysShowDetail
                ? string.Join("\n", detail.Split('\n').Take(2)) : "", ProviderDetails.CanOpen(value));
    }
}

public sealed record UsageSourceRow(string Name, string Metrics, string Note, string Tone, string TotalText = "", bool HasUsage = false)
{
    public static UsageSourceRow From(UsageSourceSummary value, TokenUnit unit = TokenUnit.Chinese)
    {
        var metrics = value.State == UsageDataState.Available
            ? $"上下文 {Formatting.Tokens(value.ContextTokens, unit)} · 输出 {Formatting.Tokens(value.OutputTokens, unit)} · 思考 {Formatting.Tokens(value.ThinkingTokens, unit)}"
            : value.Note;
        var available = value.State == UsageDataState.Available;
        return new(value.Name, metrics, value.Note, available ? "Good" : "Muted",
            available ? $"总 Token {Formatting.Tokens(value.TotalTokens, unit)} · {value.Turns} 次" : "", available);
    }
}

public sealed record RecentRow(string Model, string Source, string Metrics, string CacheText, double CachePercent, string Time, string Tone)
{
    public static RecentRow From(InteractionRecord value, TokenUnit unit = TokenUnit.Chinese)
    {
        var hit = value.CacheHitRate;
        var tone = hit is null ? "Muted" : hit >= 80 ? "Good" : hit > 50 ? "Warning" : "Muted";
        return new(
            Formatting.ModelDisplayName(value.Model),
            value.Source,
            $"总 Token {Formatting.Tokens(value.TotalTokens, unit)}\n上下文 {Formatting.Tokens(value.ContextTokens, unit)} · 输出 {Formatting.Tokens(value.OutputTokens, unit)} · 思考 {Formatting.Tokens(value.ThinkingTokens, unit)}",
            hit is { } rate ? $"{rate:0.0}% 命中" : "暂无缓存数据",
            hit ?? 0,
            RelativeTime(value.Timestamp),
            tone);
    }

    private static string RelativeTime(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.Now - timestamp;
        if (age.TotalSeconds < 60) return $"{Math.Max(1, (int)age.TotalSeconds)}秒前";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}分钟前";
        return timestamp.LocalDateTime.ToString("HH:mm");
    }
}

public sealed record ApiProviderRow(string Name, string Calls, string Metrics, string CacheText, string HealthText, string Detail = "")
{
    public static ApiProviderRow From(ApiProviderSummary value, TokenUnit unit = TokenUnit.Chinese) => new(
        value.Name,
        $"{value.Calls} 次",
        $"{(value.UnknownUsage > 0 ? "已记录" : "总")} Token {Formatting.Tokens(value.TotalTokens, unit)}\n输入 {Formatting.Tokens(value.ContextTokens, unit)} · 输出 {Formatting.Tokens(value.OutputTokens, unit)} · 思考 {Formatting.Tokens(value.ThinkingTokens, unit)}",
        $"缓存 {Formatting.Tokens(value.CacheReadTokens, unit)} · 写入 {Formatting.Tokens(value.CacheWriteTokens, unit)}",
        $"错误 {value.Errors} · 平均 {value.AverageLatencyMs} ms" + (value.UnknownUsage > 0 ? $" · 用量未知 {value.UnknownUsage} 次" : "") +
        (value.Quality is { } quality ? "\n" + quality.Description + (quality.Limits.Count > 0 ? $"\n限流回报 {quality.Limits.Count} 项（悬停查看）" : "") : ""),
        value.Quality is { } detail ? string.Join("\n", detail.Limits.Select(x => $"{x.Header}: {x.DisplayValue} · {x.CapturedAt.LocalDateTime:MM-dd HH:mm}")) : "");
}

public sealed record ApiModelRow(string Provider, string Model, string Calls, string Metrics)
{
    public static ApiModelRow From(ApiModelSummary value, TokenUnit unit = TokenUnit.Chinese) => new(
        value.Provider,
        value.Model,
        $"{value.Calls} 次",
        $"总 Token {Formatting.Tokens(value.TotalTokens, unit)}\n输入 {Formatting.Tokens(value.ContextTokens, unit)} · 输出 {Formatting.Tokens(value.OutputTokens, unit)} · {value.AverageLatencyMs} ms" + (value.UnknownUsage > 0 ? $" · 用量未知 {value.UnknownUsage} 次" : ""));
}

public sealed record ApiRecentRow(string Provider, string Model, string Time, string Tokens, string Status, string Tone)
{
    public static ApiRecentRow From(ApiRecentCall value, TokenUnit unit = TokenUnit.Chinese)
    {
        var good = value.Status is >= 200 and < 400;
        return new(
            value.Provider,
            value.Model,
            value.Timestamp.LocalDateTime.ToString("HH:mm:ss"),
            value.UnknownUsage ? "用量未知" : Formatting.Tokens(value.TotalTokens, unit),
            $"HTTP {value.Status} · {value.LatencyMs} ms",
            good ? "Good" : "Danger");
    }
}

internal static class ObservableCollectionExtensions
{
    internal static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> values, Func<T, T, bool>? same = null)
    {
        same ??= EqualityComparer<T>.Default.Equals;
        var index = 0;
        foreach (var value in values)
        {
            if (index == collection.Count) collection.Add(value);
            else if (!same(collection[index], value)) collection[index] = value;
            index++;
        }
        while (collection.Count > index) collection.RemoveAt(collection.Count - 1);
    }
}
