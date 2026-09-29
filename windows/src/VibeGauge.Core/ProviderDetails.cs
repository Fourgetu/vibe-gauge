using System.Text.Json;

namespace VibeGauge.Core;

public sealed record ProviderInfoRow(string Label, string Value);
public sealed record ProviderMetadata(IReadOnlyList<ProviderInfoRow> Rows, IReadOnlyList<string> Sources);
public sealed record NamedQuota(string Label, QuotaWindow Window);
public sealed record ProviderProcess(string Provider, int ProcessId, DateTimeOffset? StartedAt, double MemoryMb, string Executable);

public static class ProviderDetails
{
    public static bool Matches(string provider, string source) => provider == source || provider == "Claude" && source == "Claude Code";
    public static bool CanOpen(PlatformStatus p) => Windows(p).Any() || p.ExtraQuotas?.Count > 0 || p.Metadata?.Rows.Count > 0 ||
        p.IsRunning || p.DesktopTokens?.Total?.Turns > 0 || p.DesktopTokens?.Today?.Turns > 0 ||
        p.ReportedTokens?.HasValues == true || p.AlwaysShowDetail || p.DataState == ProviderDataState.Available;

    public static IEnumerable<NamedQuota> Windows(PlatformStatus p)
    {
        if (p.FiveHour is { } five) yield return new("5 小时窗口", five);
        if (p.Weekly is { } week) yield return new("7 天窗口", week);
        if (p.Monthly is { } month) yield return new("月窗口", month);
        if (p.Daily is { } day) yield return new("日窗口", day);
        var pool = p.SecondaryPoolName.Length > 0 ? p.SecondaryPoolName : "第二池";
        if (p.SecondaryFiveHour is { } secondaryFive) yield return new(pool + " · 5 小时", secondaryFive);
        if (p.SecondaryWeekly is { } secondaryWeek) yield return new(pool + " · 周窗口", secondaryWeek);
    }

    public static ProviderMetadata ReadMetadata(AppPaths paths, PlatformStatus p)
    {
        var rows = new List<ProviderInfoRow>();
        void Add(JsonElement root, string field, string label)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(field, out var value)) return;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) rows.Add(new(label, text));
            else if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) rows.Add(new(label, value.GetBoolean() ? "已开启" : "未开启"));
        }
        if (p.Name == "Claude")
        {
            using var doc = JsonSupport.ReadDocument(paths.ClaudeSettings);
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("oauthAccount", out var account))
            {
                Add(account, "organizationRateLimitTier", "限速档位");
                Add(account, "organizationType", "组织类型");
                Add(account, "billingType", "计费方式");
                Add(account, "organizationRole", "角色");
                Add(account, "hasExtraUsageEnabled", "额外用量");
                Add(account, "subscriptionCreatedAt", "订阅开始");
            }
        }
        else if (p.Name == "Codex")
        {
            using var doc = JsonSupport.ReadDocument(Path.Combine(paths.CodexRoot, "auth.json"));
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                Add(doc.RootElement, "auth_mode", "鉴权方式");
                // Copy only allowlisted account claims, never token strings or IDs.
                if (doc.RootElement.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object &&
                    tokens.TryGetProperty("id_token", out var token) && token.ValueKind == JsonValueKind.String)
                    try
                    {
                        var pieces = token.GetString()!.Split('.');
                        if (pieces.Length >= 2)
                        {
                            var payload = pieces[1].Replace('-', '+').Replace('_', '/');
                            payload += new string('=', (4 - payload.Length % 4) % 4);
                            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
                            if (claims.RootElement.ValueKind == JsonValueKind.Object && claims.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth))
                            {
                                Add(auth, "chatgpt_plan_type", "套餐字段");
                                Add(auth, "chatgpt_subscription_active_until", "订阅有效期至");
                            }
                        }
                    }
                    catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException) { }
            }
        }
        else if (p.Name == "Gemini")
        {
            using var doc = JsonSupport.ReadDocument(Path.Combine(paths.GeminiRoot, "antigravity-cli", "settings.json"));
            if (doc is not null) Add(doc.RootElement, "model", "默认模型");
            using var auth = JsonSupport.ReadDocument(Path.Combine(paths.GeminiRoot, "antigravity-cli", "antigravity-oauth-token"));
            if (auth is not null) Add(auth.RootElement, "auth_method", "鉴权方式");
            if (p.SecondaryFiveHour is not null || p.SecondaryWeekly is not null)
                rows.Add(new("三方池说明", "运行 Claude/GPT 模型时刷新；耗尽后等待重置。"));
        }
        return new(rows, Sources(paths, p));
    }

    public static IReadOnlyList<string> Sources(AppPaths paths, PlatformStatus p) => p.Name switch
    {
        "Claude" => [paths.ClaudeSettings, File.Exists(paths.ResolveOwnDataFile("claude-usage.json")) ? paths.ResolveOwnDataFile("claude-usage.json") : Path.Combine(paths.ClaudeRoot, "claude-usage.json"), Path.Combine(paths.ClaudeRoot, "projects", "**", "*.jsonl")],
        "Codex" => [Path.Combine(paths.CodexRoot, "auth.json"), Path.Combine(paths.CodexRoot, "sessions", "**", "*.jsonl"), Path.Combine(paths.CodexRoot, "archived_sessions", "**", "*.jsonl")],
        "Gemini" => [paths.ResolveOwnDataFile("agy-quota.json"), paths.AgyQuota, Path.Combine(paths.GeminiRoot, "antigravity-cli", "settings.json"), Path.Combine(paths.GeminiRoot, "antigravity-cli", "antigravity-oauth-token")],
        PiDesktopUsage.SourceName => [Path.Combine(paths.PiDesktopSessions, "**", "*.jsonl")],
        ZCodeUsage.SourceName => [paths.ZCodeDatabase],
        WorkBuddyUsage.SourceName => [Path.Combine(paths.WorkBuddyRoot, "projects", "**", "*.jsonl"), Path.Combine(paths.WorkBuddyAiRoot, "projects", "**", "*.jsonl")],
        DshUsage.SourceName => [Path.Combine(paths.DshSessions, "**", "session.v4.jsonl[.zstd]")],
        "Ollama" => ["本机只读接口：127.0.0.1:11434/api/ps"],
        "LM Studio" => ["本机只读接口：127.0.0.1:1234/api/v1/models（回退 /api/v0/models）；/v1/models 仅证明在线"],
        _ when p.AlwaysShowDetail => ["已配置供应商的只读额度/余额接口（密钥不展示）"],
        _ => []
    };
}
