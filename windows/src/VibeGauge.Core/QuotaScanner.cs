using System.Buffers;
using System.Text;
using System.Text.Json;

namespace VibeGauge.Core;

public sealed class QuotaScanner(AppPaths paths)
{
    private static readonly HttpClient OllamaClient = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromMilliseconds(450), MaxResponseContentBufferSize = 512 * 1024 };
    private DateTimeOffset ollamaMeasuredAt;
    private PlatformStatus? ollamaCached;
    private readonly Dictionary<string, CodexFileQuota> codexFiles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PlatformStatus> Scan(ProcessReport processes, UsageSummary? usage = null, UsageSourceSummary? piDesktopTotal = null)
    {
        return
        [
            ReadClaude(processes.ClaudeSessions),
            ReadCodex(processes.CodexSessions),
            ReadGemini(processes.GeminiSessions),
            ReadPiDesktop(processes.PiDesktopProcesses, usage, piDesktopTotal),
            ReadOllama(processes.OllamaRunning)
        ];
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

        var quotaPath = paths.ResolveOwnDataFile("claude-usage.json");
        if (!File.Exists(quotaPath)) quotaPath = Path.Combine(paths.ClaudeRoot, "claude-usage.json");
        using var quota = JsonSupport.ReadDocument(quotaPath);
        if (quota is null)
        {
            var state = File.Exists(quotaPath) ? ProviderDataState.ReadFailed :
                tier == "未登录" ? ProviderDataState.NotSignedIn : ProviderDataState.NoQuota;
            return new("Claude", tier, running, sessions, state, StateDetail(state));
        }

        var root = quota.RootElement;
        var captured = Epoch(root.DoubleOrNull("_captured_at"));
        var five = Window(root, "five_hour", captured, TimeSpan.FromHours(5), "used_percentage", "resets_at");
        var weekly = Window(root, "seven_day", captured, TimeSpan.FromDays(7), "used_percentage", "resets_at");
        var secondaryName = "";
        QuotaWindow? secondaryFive = null, secondaryWeek = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is "five_hour" or "seven_day" or "_captured_at" || property.Value.ValueKind != JsonValueKind.Object) continue;
            var isFive = property.Name.StartsWith("five_hour_", StringComparison.OrdinalIgnoreCase);
            var isWeek = property.Name.StartsWith("seven_day_", StringComparison.OrdinalIgnoreCase);
            if (!isFive && !isWeek) continue;
            var name = property.Name[(property.Name.IndexOf('_', property.Name.IndexOf('_') + 1) + 1)..];
            name = string.IsNullOrEmpty(name) ? "Extra" : char.ToUpperInvariant(name[0]) + name[1..];
            if (secondaryName.Length > 0 && !secondaryName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            secondaryName = name;
            var window = Window(property.Value, captured, isFive ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7), "used_percentage", "resets_at");
            if (isFive) secondaryFive = window; else secondaryWeek = window;
        }
        var dataState = QuotaState(tier, five, weekly, secondaryFive, secondaryWeek);
        return new("Claude", tier, running, sessions, dataState, StateDetail(dataState), five, weekly, secondaryName, secondaryFive, secondaryWeek);
    }

    private PlatformStatus ReadCodex(int sessionCount)
    {
        var running = sessionCount > 0;
        var tier = ReadCodexTier();
        CodexBucket? newest = null;
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
                    if (cached.Bucket is { } bucket && (newest is null || bucket.Captured > newest.Captured))
                        newest = bucket;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        foreach (var file in codexFiles.Keys.Where(file => !seen.Contains(file)).ToArray()) codexFiles.Remove(file);
        if (newest?.Plan.Length > 0) tier = Formatting.CodexPlanLabel(newest.Plan);
        var state = newest is null
            ? tier == "未登录" ? ProviderDataState.NotSignedIn : ProviderDataState.NoQuota
            : QuotaState(tier, newest.Five, newest.Weekly);
        return new("Codex", tier, running, sessionCount, state, StateDetail(state), newest?.Five, newest?.Weekly);
    }

    private string ReadCodexTier()
    {
        var path = Path.Combine(paths.CodexRoot, "auth.json");
        using var auth = JsonSupport.ReadDocument(path);
        if (auth is null) return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")) ? "未登录" : "API Key";
        var root = auth.RootElement;
        if (root.TryGetProperty("tokens", out var tokens) && tokens.TryGetProperty("id_token", out var token) && token.ValueKind == JsonValueKind.String)
        {
            var plan = ReadJwtPlan(token.GetString() ?? "");
            if (plan.Length > 0) return Formatting.CodexPlanLabel(plan);
        }
        var mode = root.StringOrEmpty("auth_mode");
        if (mode == "api_key" || root.TryGetProperty("OPENAI_API_KEY", out _)) return "API Key";
        return mode == "chatgpt" ? "ChatGPT 登录" : "已登录";
    }

    private static string ReadJwtPlan(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return "";
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth)
                ? auth.StringOrEmpty("chatgpt_plan_type")
                : "";
        }
        catch { return ""; }
    }

    private PlatformStatus ReadGemini(int sessions)
    {
        var running = sessions > 0;
        var bridgePath = paths.ResolveOwnDataFile("agy-quota.json");
        using var quota = JsonSupport.ReadDocument(File.Exists(bridgePath) ? bridgePath : paths.AgyQuota);
        if (quota is null || !quota.RootElement.TryGetProperty("pools", out var pools))
        {
            var missingState = File.Exists(paths.AgyQuota) ? ProviderDataState.ReadFailed :
                Directory.Exists(paths.GeminiRoot) ? ProviderDataState.NoQuota : ProviderDataState.NotSignedIn;
            return new("Gemini", missingState == ProviderDataState.NotSignedIn ? "未登录" : "已检测", running, sessions, missingState, StateDetail(missingState));
        }
        var captured = Epoch(quota.RootElement.DoubleOrNull("updated_at"));
        pools.TryGetProperty("gemini", out var native);
        pools.TryGetProperty("3p", out var thirdParty);
        var five = RemainingWindow(native, "5h", captured, TimeSpan.FromHours(5));
        var weekly = RemainingWindow(native, "weekly", captured, TimeSpan.FromDays(7));
        var secondaryFive = RemainingWindow(thirdParty, "5h", captured, TimeSpan.FromHours(5));
        var secondaryWeekly = RemainingWindow(thirdParty, "weekly", captured, TimeSpan.FromDays(7));
        var state = QuotaState("已登录", five, weekly, secondaryFive, secondaryWeekly);
        return new(
            "Gemini", "已登录", running, sessions, state, StateDetail(state),
            five, weekly,
            "3P",
            secondaryFive, secondaryWeekly);
    }

    private PlatformStatus ReadPiDesktop(int processes, UsageSummary? usage, UsageSourceSummary? total)
    {
        var source = usage?.Sources.FirstOrDefault(x => x.Name == PiDesktopUsage.SourceName);
        var available = total?.State == UsageDataState.Available || source?.State == UsageDataState.Available;
        total ??= source;
        var detail = available
            ? $"今日 {source?.Turns ?? 0} 次 · 总 Token {Formatting.Tokens(source?.TotalTokens ?? 0)}\n今日上下文 {Formatting.Tokens(source?.ContextTokens ?? 0)} · 输出 {Formatting.Tokens(source?.OutputTokens ?? 0)}\n本地累计 {total!.Turns} 次 · 总 Token {Formatting.Tokens(total.TotalTokens)}\n累计上下文 {Formatting.Tokens(total.ContextTokens)} · 输出 {Formatting.Tokens(total.OutputTokens)}\n思考 {Formatting.Tokens(total.ThinkingTokens)} · 缓存读取 {Formatting.Tokens(total.CacheReadTokens)}"
            : Directory.Exists(paths.PiDesktopRoot) ? "本地无今日 token 统计" : "未检测到本地会话日志";
        return new(PiDesktopUsage.SourceName, "", processes > 0, processes,
            available ? ProviderDataState.Available : ProviderDataState.NoQuota, detail);
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

    private static JsonElement? FindObject(JsonElement root, string requiredProperty) => JsonSupport.FindObjectWithProperty(root, requiredProperty);
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

    private static CodexBucket? ReadCodexTail(string path, DateTime lastWrite)
    {
        const int maxBytes = 2 * 1024 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var count = (int)Math.Min(length, maxBytes);
        if (count == 0) return null;
        var start = length - count;
        stream.Position = start;
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            stream.ReadExactly(buffer.AsSpan(0, count));
            CodexBucket? newest = null;
            // Keep StreamReader's BOM detection for non-UTF-8 logs.
            var bytes = buffer.AsSpan(0, count);
            if (bytes.Length >= 2 && ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes[0] == 0xfe && bytes[1] == 0xff)) ||
                bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
            {
                using var textStream = new MemoryStream(buffer, 0, count, writable: false);
                using var reader = new StreamReader(textStream, Encoding.UTF8, true);
                if (start > 0) _ = reader.ReadLine();
                while (reader.ReadLine() is { } line) ReadCodexLine(line, lastWrite, ref newest);
                return newest;
            }
            var offset = 0;
            if (start > 0)
            {
                var newline = bytes.IndexOfAny((byte)'\n', (byte)'\r');
                if (newline < 0) return null;
                offset = newline + 1;
            }
            else if (bytes.StartsWith("\uFEFF"u8)) offset = 3;
            while (offset < count)
            {
                var remaining = bytes[offset..];
                var newline = remaining.IndexOfAny((byte)'\n', (byte)'\r');
                var line = newline < 0 ? remaining : remaining[..newline];
                if (line.IndexOf("used_percent"u8) >= 0)
                    ReadCodexLine(Encoding.UTF8.GetString(line), lastWrite, ref newest);
                if (newline < 0) break;
                offset += newline + 1;
            }
            return newest;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static void ReadCodexLine(string line, DateTime lastWrite, ref CodexBucket? newest)
    {
        if (!line.Contains("used_percent", StringComparison.Ordinal) || !TryParseJson(line, out var document)) return;
        using (document)
        {
            var rate = FindObject(document.RootElement, "primary");
            if (rate is null) return;
            var id = rate.Value.StringOrEmpty("limit_id");
            if (id.Length > 0 && !id.Equals("codex", StringComparison.OrdinalIgnoreCase)) return;
            var captured = Formatting.ParseDate(document.RootElement.StringOrEmpty("timestamp")) ?? new DateTimeOffset(lastWrite, TimeSpan.Zero);
            if (newest is not null && newest.Captured >= captured) return;
            var five = CodexWindow(rate.Value, "primary", captured);
            var weekly = CodexWindow(rate.Value, "secondary", captured);
            if (five?.Window >= TimeSpan.FromDays(1)) (five, weekly) = (weekly, five);
            if (weekly?.Window < TimeSpan.FromDays(1)) (five, weekly) = (weekly, five);
            newest = new(captured, five, weekly, rate.Value.StringOrEmpty("plan_type"));
        }
    }

    private readonly record struct CodexFileStamp(long Length, long LastWriteTicks, long CreationTicks);
    private sealed record CodexFileQuota(CodexFileStamp Stamp, CodexBucket? Bucket);
    private sealed record CodexBucket(DateTimeOffset Captured, QuotaWindow? Five, QuotaWindow? Weekly, string Plan);
}
