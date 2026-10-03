using System.Buffers;
using System.Text;
using System.Text.Json;

namespace VibeGauge.Core;

public sealed class QuotaScanner(AppPaths paths, ICodexQuotaClient? codexClient = null) : IDisposable
{
    private readonly CodexQuotaRefresh? codexLive = codexClient is null ? null : new(codexClient);
    public void InvalidateCodex() => codexLive?.Invalidate();
    public void Dispose() => codexLive?.Dispose();
    private static readonly HttpClient OllamaClient = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromMilliseconds(450), MaxResponseContentBufferSize = 512 * 1024 };
    private DateTimeOffset ollamaMeasuredAt;
    private PlatformStatus? ollamaCached;
    private readonly Dictionary<string, CodexFileQuota> codexFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProviderMetadata> metadataCache = new(StringComparer.Ordinal);
    private IReadOnlyList<NamedQuota> codexExtra = Array.Empty<NamedQuota>();
    private string? codexAccountScope;
    private DateTimeOffset? codexQuotaSince;

    public IReadOnlyList<PlatformStatus> Scan(ProcessReport processes, UsageSummary? usage = null, UsageSourceSummary? piDesktopTotal = null,
        UsageSourceSummary? zcodeTotal = null, UsageSourceSummary? workBuddyTotal = null, UsageSourceSummary? dshTotal = null, UsageSourceSummary? codexTotal = null)
    {
        PlatformStatus[] platforms =
        [
            ReadClaude(processes.ClaudeSessions),
            ReadCodex(processes.CodexSessions, usage, codexTotal),
            ReadGemini(processes.GeminiSessions),
            ReadDesktopUsage(ZCodeUsage.SourceName, paths.ZCodeRoot, processes.ZCodeProcesses, usage, zcodeTotal),
            ReadPiDesktop(processes.PiDesktopProcesses, usage, piDesktopTotal),
            ReadOllama(processes.OllamaRunning),
            ReadDesktopUsage(WorkBuddyUsage.SourceName, Directory.Exists(paths.WorkBuddyRoot) ? paths.WorkBuddyRoot : paths.WorkBuddyAiRoot,
                processes.WorkBuddyProcesses, usage, workBuddyTotal),
            ReadDesktopUsage(DshUsage.SourceName, paths.DshRoot, processes.DshProcesses, usage, dshTotal)
        ];
        return platforms.Select(p =>
        {
            var metadata = ProviderDetails.ReadMetadata(paths, p);
            if (metadataCache.TryGetValue(p.Name, out var previous) && previous.Rows.SequenceEqual(metadata.Rows) &&
                previous.Sources.SequenceEqual(metadata.Sources)) metadata = previous;
            else metadataCache[p.Name] = metadata;
            return p with { Metadata = metadata };
        }).ToArray();
    }

    private PlatformStatus ReadClaude(int sessions)
    {
        var running = sessions > 0;
        var tier = "未登录";
        using (var settings = JsonSupport.ReadDocument(paths.ClaudeSettings))
        {
            if (settings?.RootElement.TryGetProperty("oauthAccount", out var account) == true)
            {
                var rate = account.StringOrEmpty("organizationRateLimitTier").ToLowerInvariant();
                var org = account.StringOrEmpty("organizationType").ToLowerInvariant();
                tier = rate switch
                {
                    var x when x.Contains("max_20x") => "Max 20x",
                    var x when x.Contains("max_5x") => "Max 5x",
                    var x when x.Contains("enterprise") => "Enterprise",
                    var x when x.Contains("team") => "Team",
                    var x when x.Contains("pro") => "Pro",
                    var x when x.Contains("max") => "Max",
                    _ when org.Contains("max") => "Max",
                    _ when org.Contains("pro") => "Pro",
                    _ => "已登录"
                };
            }
        }
        if (tier == "未登录" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))) tier = "API Key";

        var candidates = new[] { paths.LocalDataRoot, paths.LegacyDataRoot, paths.ClaudeRoot }
            .Select(dir => Path.Combine(dir, "claude-usage.json")).Distinct().ToArray();
        JsonDocument? selected = null;
        DateTimeOffset? captured = null;
        foreach (var file in candidates)
        {
            var doc = JsonSupport.ReadDocument(file);
            if (doc is null) continue;
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.EnumerateObject().Any(p =>
                (p.Name.StartsWith("five_hour") || p.Name.StartsWith("seven_day")) &&
                Window(p.Value, null, TimeSpan.FromHours(5), "used_percentage", "resets_at") is not null))
            { doc.Dispose(); continue; }
            DateTimeOffset at;
            try { at = Epoch(doc.RootElement.DoubleOrNull("_captured_at")) ?? new DateTimeOffset(File.GetLastWriteTimeUtc(file)); }
            catch (IOException) { doc.Dispose(); continue; }
            if (selected is null || at > captured) { selected?.Dispose(); selected = doc; captured = at; }
            else doc.Dispose();
        }
        using var quota = selected;
        if (quota is null)
        {
            var state = candidates.Any(File.Exists) ? ProviderDataState.ReadFailed :
                tier == "未登录" ? ProviderDataState.NotSignedIn : ProviderDataState.NoQuota;
            return new("Claude", tier, running, sessions, state, StateDetail(state));
        }

        var root = quota.RootElement;
        var five = Window(root, "five_hour", captured, TimeSpan.FromHours(5), "used_percentage", "resets_at");
        var weekly = Window(root, "seven_day", captured, TimeSpan.FromDays(7), "used_percentage", "resets_at");
        var secondaryName = "";
        QuotaWindow? secondaryFive = null, secondaryWeek = null;
        foreach (var property in root.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (property.Name is "five_hour" or "seven_day" or "_captured_at" || property.Value.ValueKind != JsonValueKind.Object) continue;
            var isFive = property.Name.StartsWith("five_hour_", StringComparison.OrdinalIgnoreCase);
            var isWeek = property.Name.StartsWith("seven_day_", StringComparison.OrdinalIgnoreCase);
            if (!isFive && !isWeek) continue;
            var name = property.Name[(property.Name.IndexOf('_', property.Name.IndexOf('_') + 1) + 1)..];
            name = string.IsNullOrEmpty(name) ? "Extra" : char.ToUpperInvariant(name[0]) + name[1..];
            if (secondaryName.Length > 0 && !secondaryName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var window = Window(property.Value, captured, isFive ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7), "used_percentage", "resets_at");
            if (window is null) continue;
            secondaryName = name;
            if (isFive) secondaryFive = window; else secondaryWeek = window;
        }
        var dataState = QuotaState(tier, five, weekly, secondaryFive, secondaryWeek);
        return new("Claude", tier, running, sessions, dataState, StateDetail(dataState), five, weekly, secondaryName, secondaryFive, secondaryWeek);
    }

    private PlatformStatus ReadCodex(int sessionCount, UsageSummary? usage, UsageSourceSummary? total)
    {
        var running = sessionCount > 0;
        var login = ReadCodexLogin();
        var tier = login.Tier;
        if (codexAccountScope != login.Scope)
        {
            codexAccountScope = login.Scope;
            codexQuotaSince = login.UpdatedAt;
        }
        if (login.ApiKey || login.ReadFailed)
        {
            codexFiles.Clear();
            codexExtra = Array.Empty<NamedQuota>();
            var unavailable = new PlatformStatus("Codex", tier, running, sessionCount,
                login.ReadFailed ? ProviderDataState.ReadFailed : ProviderDataState.NoQuota,
                login.ReadFailed ? "当前登录信息读取失败；未采用历史订阅额度。" : "当前本机登录为 API Key；不使用历史订阅套餐和额度。",
                ExtraQuotas: codexExtra, QuotaScope: login.Scope);
            unavailable = codexLive?.Apply(unavailable, null) ?? unavailable;
            if (login.ApiKey && !login.ReadFailed)
            {
                var today = usage?.Sources.FirstOrDefault(x => x.Name == "Codex");
                var tokens = new DesktopTokenDisplay(today, total, Directory.Exists(paths.CodexRoot));
                unavailable = unavailable with { DesktopTokens = tokens,
                    DataState = total?.State == UsageDataState.ReadFailed ? ProviderDataState.ReadFailed : ProviderDataState.Available };
            }
            return unavailable;
        }
        // Quota events do not identify their account. Keep only reports after the
        // observed login boundary; token refreshes for the same identity retain it.
        bool AfterLogin(DateTimeOffset at) => codexQuotaSince is null || at >= codexQuotaSince.Value.AddSeconds(-1);
        var buckets = new Dictionary<string, CodexBucket>(StringComparer.OrdinalIgnoreCase);
        CodexLimitHit? limitHit = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessions = Path.Combine(paths.CodexRoot, "sessions");
        if (Directory.Exists(sessions))
        {
            foreach (var file in SafeFiles(sessions, "*.jsonl"))
            {
                try
                {
                    var info = new FileInfo(file);
                    var lastWrite = info.LastWriteTimeUtc;
                    if (!info.Exists || DateTime.UtcNow - lastWrite > TimeSpan.FromHours(48)) continue;
                    var stamp = new CodexFileStamp(info.Length, lastWrite.Ticks, info.CreationTimeUtc.Ticks);
                    if (!codexFiles.TryGetValue(file, out var cached) || cached.Stamp != stamp)
                    {
                        cached = new(stamp, ReadCodexTail(file, lastWrite));
                        // Only retain parsed quota fields, never log text or JsonDocuments.
                        codexFiles[file] = cached;
                    }
                    seen.Add(file);
                    if (login.KnownPlan && cached.Parsed.Buckets.TryGetValue("codex", out var main) &&
                        main.Plan.Length > 0 && Formatting.CodexPlanLabel(main.Plan) != tier) continue;
                    if (cached.Parsed.Hit is { } hit && AfterLogin(hit.At) && (limitHit is null || hit.At > limitHit.At)) limitHit = hit;
                    foreach (var (id, bucket) in cached.Parsed.Buckets)
                        if (AfterLogin(bucket.Captured) && (!buckets.TryGetValue(id, out var previous) || bucket.Captured > previous.Captured))
                            buckets[id] = bucket;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        foreach (var file in codexFiles.Keys.Where(file => !seen.Contains(file)).ToArray()) codexFiles.Remove(file);
        buckets.TryGetValue("codex", out var newest);
        if (limitHit is { } latest && (newest?.Weekly?.CapturedAt is not { } at || latest.At > at) &&
            (latest.Reset is null || latest.Reset > DateTimeOffset.Now))
        {
            var exhausted = new QuotaWindow(100, latest.Reset ?? newest?.Weekly?.ResetsAt, latest.At, TimeSpan.FromDays(7));
            newest = newest is null ? new(latest.At, null, exhausted, "", "") : newest with { Weekly = exhausted };
        }
        if (!login.KnownPlan && newest?.Plan.Length > 0) tier = Formatting.CodexPlanLabel(newest.Plan);
        var state = newest is null
            ? tier == "未登录" ? ProviderDataState.NotSignedIn : ProviderDataState.NoQuota
            : QuotaState(tier, newest.Five, newest.Weekly);
        var extra = new List<NamedQuota>();
        foreach (var (id, bucket) in buckets.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (id.Equals("codex", StringComparison.OrdinalIgnoreCase)) continue;
            var label = string.IsNullOrWhiteSpace(bucket.Name) ? id : bucket.Name;
            if (bucket.Five is { } five) extra.Add(new(label + " · " + WindowLabel(five), five));
            if (bucket.Weekly is { } week) extra.Add(new(label + " · " + WindowLabel(week), week));
        }
        if (!codexExtra.SequenceEqual(extra)) codexExtra = extra.ToArray();
        var detail = login.Present && newest is null ? "等待当前登录的新额度回报；旧会话额度不代表当前账号。" :
            !login.Present && newest is not null ? "本地历史额度回报；未验证当前登录套餐。" : StateDetail(state);
        var local = new PlatformStatus("Codex", tier, running, sessionCount, state, detail, newest?.Five, newest?.Weekly,
            ExtraQuotas: codexExtra, QuotaScope: login.Present ? login.Scope : "");
        var request = login.Present && (login.KnownPlan || login.Tier == "ChatGPT 登录") && login.UpdatedAt is { } updated
            ? new CodexQuotaRequest(login.Scope, login.Tier, login.KnownPlan, updated) : null;
        return codexLive?.Apply(local, request) ?? local;
    }

    private static string WindowLabel(QuotaWindow window) => window.Window.TotalDays >= 1
        ? $"{window.Window.TotalDays:0.#} 天" : $"{window.Window.TotalHours:0.#} 小时";

    private sealed record CodexLogin(string Tier, string Scope, DateTimeOffset? UpdatedAt,
        bool Present = false, bool KnownPlan = false, bool ApiKey = false, bool ReadFailed = false);

    private CodexLogin ReadCodexLogin()
    {
        var path = Path.Combine(paths.CodexRoot, "auth.json");
        using var auth = JsonSupport.ReadDocument(path);
        if (auth is null && !File.Exists(path)) return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
            ? new("未登录", "missing", null) : new("API Key", "api-key", null, Present: true, ApiKey: true);
        if (auth is null || auth.RootElement.ValueKind != JsonValueKind.Object)
            return new("登录信息不可读", "unreadable", null, Present: true, ReadFailed: true);
        DateTimeOffset? updated;
        try { updated = new DateTimeOffset(File.GetLastWriteTimeUtc(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new("登录信息不可读", "unreadable", null, Present: true, ReadFailed: true); }
        var root = auth.RootElement;
        var mode = root.StringOrEmpty("auth_mode");
        if (mode == "api_key" || mode != "chatgpt" && root.StringOrEmpty("OPENAI_API_KEY").Length > 0)
            return new("API Key", "api-key", updated, Present: true, ApiKey: true);
        if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object &&
            tokens.StringOrEmpty("id_token") is { Length: > 0 } token)
        {
            var (plan, identity) = ReadJwtAccount(token);
            var tier = plan.Length > 0 ? Formatting.CodexPlanLabel(plan) : "ChatGPT 登录";
            return new(tier, AccountScope(tier + "\0" + (identity.Length > 0 ? identity : token)), updated,
                Present: true, KnownPlan: plan.Length > 0);
        }
        return new(mode == "chatgpt" ? "ChatGPT 登录" : "已登录", AccountScope(root.GetRawText()), updated, Present: true);
    }

    private static string AccountScope(string identity) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    private static (string Plan, string Identity) ReadJwtAccount(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return ("", "");
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth))
            {
                var account = auth.StringOrEmpty("chatgpt_account_id");
                var user = auth.StringOrEmpty("chatgpt_user_id");
                var subject = document.RootElement.StringOrEmpty("sub");
                return (auth.StringOrEmpty("chatgpt_plan_type"),
                    account.Length + user.Length + subject.Length > 0 ? string.Join("\0", account, user, subject) : "");
            }
            return ("", "");
        }
        catch { return ("", ""); }
    }

    private PlatformStatus ReadGemini(int sessions)
    {
        var running = sessions > 0;
        var bridgePath = paths.ResolveOwnDataFile("agy-quota.json");
        var candidates = new Dictionary<string, QuotaWindow>();
        var readable = false;
        foreach (var path in new[] { bridgePath, paths.AgyQuota }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var quota = JsonSupport.ReadDocument(path);
            if (quota is null || !quota.RootElement.TryGetProperty("pools", out var pools) || pools.ValueKind != JsonValueKind.Object) continue;
            readable = true;
            var captured = Epoch(quota.RootElement.DoubleOrNull("updated_at"));
            foreach (var pool in new[] { "gemini", "3p" })
            {
                if (!pools.TryGetProperty(pool, out var native)) continue;
                foreach (var (key, span) in new[] { ("5h", TimeSpan.FromHours(5)), ("weekly", TimeSpan.FromDays(7)) })
                {
                    var window = RemainingWindow(native, key, captured, span);
                    var id = pool + "/" + key;
                    if (window is not null && (!candidates.TryGetValue(id, out var previous) ||
                        (window.CapturedAt ?? DateTimeOffset.MinValue) > (previous.CapturedAt ?? DateTimeOffset.MinValue))) candidates[id] = window;
                }
            }
        }
        if (!readable)
        {
            var missingState = File.Exists(paths.AgyQuota) || File.Exists(bridgePath) ? ProviderDataState.ReadFailed :
                Directory.Exists(paths.GeminiRoot) ? ProviderDataState.NoQuota : ProviderDataState.NotSignedIn;
            return new("Gemini", missingState == ProviderDataState.NotSignedIn ? "未登录" : "已检测", running, sessions, missingState, StateDetail(missingState));
        }
        var five = candidates.GetValueOrDefault("gemini/5h");
        var weekly = candidates.GetValueOrDefault("gemini/weekly");
        var secondaryFive = candidates.GetValueOrDefault("3p/5h");
        var secondaryWeekly = candidates.GetValueOrDefault("3p/weekly");
        var state = QuotaState("已登录", five, weekly, secondaryFive, secondaryWeekly);
        return new(
            "Gemini", "已登录", running, sessions, state, StateDetail(state),
            five, weekly,
            "3P",
            secondaryFive, secondaryWeekly);
    }

    private PlatformStatus ReadPiDesktop(int processes, UsageSummary? usage, UsageSourceSummary? total)
        => ReadDesktopUsage(PiDesktopUsage.SourceName, paths.PiDesktopRoot, processes, usage, total);

    private static PlatformStatus ReadDesktopUsage(string name, string root, int processes, UsageSummary? usage, UsageSourceSummary? total)
    {
        var source = usage?.Sources.FirstOrDefault(x => x.Name == name);
        var available = total?.Turns > 0 || source?.State == UsageDataState.Available;
        var failed = total?.State == UsageDataState.ReadFailed;
        var tokens = new DesktopTokenDisplay(source, total, Directory.Exists(root));
        var (detail, compact) = tokens.Format();
        return new(name, "", processes > 0, processes,
            failed ? ProviderDataState.ReadFailed : available ? ProviderDataState.Available : ProviderDataState.NoQuota, detail,
            CompactDetail: compact, DesktopTokens: tokens);
    }

    private PlatformStatus ReadOllama(bool processRunning)
    {
        var now = DateTimeOffset.Now;
        if (ollamaCached is not null && now - ollamaMeasuredAt < TimeSpan.FromSeconds(10)) return ollamaCached;
        ollamaMeasuredAt = now;
        try
        {
            using var response = OllamaClient.GetAsync("http://127.0.0.1:11434/api/ps").GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var count = document.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                ? models.GetArrayLength()
                : 0;
            return ollamaCached = new("Ollama", "本地", true, 1, ProviderDataState.Available,
                count > 0 ? $"{count} 个已加载模型" : "服务在线 · 当前空闲", ModelCount: count);
        }
        catch
        {
            var state = processRunning ? ProviderDataState.ReadFailed : ProviderDataState.NotRunning;
            return ollamaCached = new("Ollama", "本地", processRunning, processRunning ? 1 : 0, state,
                processRunning ? "服务响应失败" : "未运行", ModelCount: processRunning ? null : 0);
        }
    }

    private static ProviderDataState QuotaState(string tier, params QuotaWindow?[] windows)
    {
        var present = windows.Where(x => x is not null).Select(x => x!).ToArray();
        if (present.Length == 0) return tier == "未登录" ? ProviderDataState.NotSignedIn : ProviderDataState.NoQuota;
        var newest = present.Max(x => x.CapturedAt);
        if (newest is { } captured && DateTimeOffset.Now - captured > TimeSpan.FromHours(24))
            return ProviderDataState.Stale;
        return ProviderDataState.Available;
    }

    private static string StateDetail(ProviderDataState state) => state switch
    {
        ProviderDataState.Available => "本地额度数据",
        ProviderDataState.NotSignedIn => "未登录",
        ProviderDataState.NoQuota => "未检测到额度",
        ProviderDataState.ReadFailed => "读取失败",
        ProviderDataState.Stale => "额度数据过期",
        _ => "未运行"
    };

    private static QuotaWindow? Window(JsonElement root, string property, DateTimeOffset? captured, TimeSpan span, string usedName, string resetName) =>
        root.TryGetProperty(property, out var value) ? Window(value, captured, span, usedName, resetName) : null;

    private static QuotaWindow? Window(JsonElement value, DateTimeOffset? captured, TimeSpan span, string usedName, string resetName)
    {
        var used = value.DoubleOrNull(usedName);
        if (used is null) return null;
        return new((int)Math.Round(used.Value), Epoch(value.DoubleOrNull(resetName)), captured, span);
    }

    private static QuotaWindow? RemainingWindow(JsonElement pool, string property, DateTimeOffset? captured, TimeSpan span)
    {
        if (pool.ValueKind != JsonValueKind.Object || !pool.TryGetProperty(property, out var value)) return null;
        var remaining = value.DoubleOrNull("remaining_fraction");
        if (remaining is null) return null;
        return new((int)Math.Round((1 - remaining.Value) * 100), Epoch(value.DoubleOrNull("reset_at")), Epoch(value.DoubleOrNull("recorded_at")) ?? captured, span);
    }

    private static QuotaWindow? CodexWindow(JsonElement rate, string property, DateTimeOffset captured)
    {
        if (!rate.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object) return null;
        var used = window.DoubleOrNull("used_percent");
        if (used is null) return null;
        var minutes = window.DoubleOrNull("window_minutes") ?? 0;
        var span = minutes > 0 ? TimeSpan.FromMinutes(minutes) : property == "secondary" ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5);
        return new((int)Math.Round(used.Value), Epoch(window.DoubleOrNull("resets_at")), captured, span);
    }

    private static DateTimeOffset? Epoch(double? value) => value is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(value.Value * 1000)) : null;

    private static bool TryParseJson(string line, out JsonDocument document)
    {
        try { document = JsonDocument.Parse(line); return true; }
        catch { document = null!; return false; }
    }

    private static IEnumerable<string> SafeFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray(); }
        catch { return []; }
    }

    private static CodexParsed ReadCodexTail(string path, DateTime lastWrite)
    {
        const int maxBytes = 2 * 1024 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var count = (int)Math.Min(length, maxBytes);
        if (count == 0) return new();
        var start = length - count;
        stream.Position = start;
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            stream.ReadExactly(buffer.AsSpan(0, count));
            var newest = new CodexParsed();
            // Keep StreamReader's BOM detection for non-UTF-8 logs.
            var bytes = buffer.AsSpan(0, count);
            if (bytes.Length >= 2 && ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes[0] == 0xfe && bytes[1] == 0xff)) ||
                bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
            {
                using var textStream = new MemoryStream(buffer, 0, count, writable: false);
                using var reader = new StreamReader(textStream, Encoding.UTF8, true);
                if (start > 0) _ = reader.ReadLine();
                while (reader.ReadLine() is { } line) ReadCodexLine(line, lastWrite, newest);
                return newest;
            }
            var offset = 0;
            if (start > 0)
            {
                var newline = bytes.IndexOfAny((byte)'\n', (byte)'\r');
                if (newline < 0) return newest;
                offset = newline + 1;
            }
            else if (bytes.StartsWith("\uFEFF"u8)) offset = 3;
            while (offset < count)
            {
                var remaining = bytes[offset..];
                var newline = remaining.IndexOfAny((byte)'\n', (byte)'\r');
                var line = newline < 0 ? remaining : remaining[..newline];
                if (line.IndexOf("used_percent"u8) >= 0 || line.IndexOf("usage_limit_exceeded"u8) >= 0)
                    ReadCodexLine(Encoding.UTF8.GetString(line), lastWrite, newest);
                if (newline < 0) break;
                offset += newline + 1;
            }
            return newest;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static void ReadCodexLine(string line, DateTime lastWrite, CodexParsed parsed)
    {
        if ((!line.Contains("used_percent", StringComparison.Ordinal) && !line.Contains("usage_limit_exceeded", StringComparison.Ordinal)) || !TryParseJson(line, out var document)) return;
        using (document)
        {
            var captured = Formatting.ParseDate(document.RootElement.StringOrEmpty("timestamp")) ?? new DateTimeOffset(lastWrite, TimeSpan.Zero);
            if (FindLimitMessage(document.RootElement) is { } message && (parsed.Hit is null || captured > parsed.Hit.At))
                parsed.Hit = new(captured, ParseLimitReset(message));
            var newest = parsed.Buckets;
            foreach (var rate in CodexRates(document.RootElement))
            {
                var id = rate.StringOrEmpty("limit_id");
                if (id.Length == 0) id = "codex";
                if (newest.TryGetValue(id, out var previous) && previous.Captured >= captured) continue;
                var five = CodexWindow(rate, "primary", captured);
                var weekly = CodexWindow(rate, "secondary", captured);
                if (five is null && weekly is null) continue;
                if (five?.Window >= TimeSpan.FromDays(1)) (five, weekly) = (weekly, five);
                if (weekly?.Window < TimeSpan.FromDays(1)) (five, weekly) = (weekly, five);
                newest[id] = new(captured, five, weekly, rate.StringOrEmpty("plan_type"), rate.StringOrEmpty("limit_name"));
            }
        }
    }

    private static IEnumerable<JsonElement> CodexRates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("primary", out _) || element.TryGetProperty("secondary", out _))
            {
                yield return element;
                yield break;
            }
            foreach (var property in element.EnumerateObject())
                foreach (var rate in CodexRates(property.Value)) yield return rate;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var rate in CodexRates(item)) yield return rate;
    }

    private readonly record struct CodexFileStamp(long Length, long LastWriteTicks, long CreationTicks);
    private sealed record CodexFileQuota(CodexFileStamp Stamp, CodexParsed Parsed);
    private sealed record CodexBucket(DateTimeOffset Captured, QuotaWindow? Five, QuotaWindow? Weekly, string Plan, string Name);
    private sealed record CodexLimitHit(DateTimeOffset At, DateTimeOffset? Reset);
    private sealed class CodexParsed
    {
        public Dictionary<string, CodexBucket> Buckets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CodexLimitHit? Hit { get; set; }
    }
    private static string? FindLimitMessage(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.StringOrEmpty("codex_error_info") == "usage_limit_exceeded") return element.StringOrEmpty("message");
            foreach (var property in element.EnumerateObject())
                if (FindLimitMessage(property.Value) is { } message) return message;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (FindLimitMessage(item) is { } message) return message;
        return null;
    }
    private static DateTimeOffset? ParseLimitReset(string message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(message,
            @"try again (?:at|on)\s+([A-Za-z]{3,})\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})[,\s]+(\d{1,2}):(\d{2})\s*([AaPp])\.?[Mm]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        if (!match.Success) return null;
        var g = match.Groups;
        var text = $"{g[1].Value[..3]} {g[2].Value}, {g[3].Value} {g[4].Value}:{g[5].Value} {g[6].Value.ToUpperInvariant()}M";
        return DateTimeOffset.TryParseExact(text, "MMM d, yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var reset) ? reset : null;
    }
}
