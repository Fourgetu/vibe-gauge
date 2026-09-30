using System.IO;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

internal static class CodexAppServerProtocol
{
    internal static async Task<CodexQuotaResult> ReadAsync(TextReader reader, TextWriter writer,
        CodexQuotaRequest account, DateTimeOffset captured, CancellationToken token)
    {
        try
        {
            await Send(writer, new { id = 1, method = "initialize", @params = new
            { clientInfo = new { name = "vibegauge_quota", version = "1.0" } } }, token);
            using var initialized = await Response(reader, 1, token);
            await Send(writer, new { method = "initialized" }, token);
            await Send(writer, new { id = 2, method = "account/read", @params = new { refreshToken = false } }, token);
            using var login = await Response(reader, 2, token);
            if (!login.RootElement.TryGetProperty("account", out var current) ||
                current.ValueKind != JsonValueKind.Object || Text(current, "type") != "chatgpt")
                return new(Error: "Codex CLI 未使用 ChatGPT 订阅登录，请重新登录后刷新");
            if (!MatchesPlan(current, account)) return new(Error: "账号套餐已变化，等待重新查询");
            await Send(writer, new { id = 3, method = "account/rateLimits/read" }, token);
            using var response = await Response(reader, 3, token);
            return Parse(response.RootElement, account, captured);
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or ArgumentOutOfRangeException)
        { return new(Error: "Codex 官方额度查询失败，请检查登录、网络或更新 Codex 客户端"); }
    }

    private static async Task Send(TextWriter writer, object request, CancellationToken token)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
        await writer.FlushAsync(token);
    }

    private static async Task<JsonDocument> Response(TextReader reader, int id, CancellationToken token)
    {
        for (var i = 0; i < 128; i++)
        {
            var line = await reader.ReadLineAsync(token) ?? throw new IOException("Codex closed its output");
            if (line.Length > 1024 * 1024) throw new IOException("Codex response is too large");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("method", out _) ||
                !root.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var number) || number != id) continue;
            // Never include raw RPC errors: they can contain account or endpoint details.
            if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object) throw new IOException("Codex RPC failed");
            return JsonDocument.Parse(result.GetRawText());
        }
        throw new IOException("Codex did not return the requested response");
    }

    private static bool MatchesPlan(JsonElement bucket, CodexQuotaRequest account) =>
        !account.KnownPlan || Text(bucket, "planType") is not { Length: > 0 } plan || Formatting.CodexPlanLabel(plan) == account.Tier;

    private static CodexQuotaResult Parse(JsonElement root, CodexQuotaRequest account, DateTimeOffset captured)
    {
        JsonElement main = default;
        var extras = new List<NamedQuota>();
        if (root.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            map.TryGetProperty("codex", out main);
            foreach (var property in map.EnumerateObject())
            {
                if (property.Name == "codex" || property.Value.ValueKind != JsonValueKind.Object) continue;
                var label = Text(property.Value, "limitName");
                AddExtras(extras, property.Value, label.Length > 0 ? label : property.Name, captured);
            }
        }
        else root.TryGetProperty("rateLimits", out main);
        QuotaWindow? five = null, weekly = null;
        if (main.ValueKind == JsonValueKind.Object)
        {
            if (!MatchesPlan(main, account)) return new(Error: "官方额度套餐与当前账号不一致，等待重新查询");
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (Window(main, key, captured) is not { } window) continue;
                // Pro can place its only weekly window in primary. Slot order is not duration.
                if (window.Window == TimeSpan.FromDays(7)) weekly = window;
                else if (window.Window == TimeSpan.FromHours(5)) five = window;
                else extras.Add(new("Codex · " + Label(window), window));
            }
        }
        return five is null && weekly is null && extras.Count == 0
            ? new(Error: "官方尚未返回可用额度窗口") : new(five, weekly, extras);
    }

    private static void AddExtras(List<NamedQuota> output, JsonElement bucket, string label, DateTimeOffset captured)
    {
        foreach (var key in new[] { "primary", "secondary" })
            if (Window(bucket, key, captured) is { } window) output.Add(new(label + " · " + Label(window), window));
    }

    private static string Label(QuotaWindow window) => window.Window.TotalDays >= 1
        ? $"{window.Window.TotalDays:0.#} 天" : $"{window.Window.TotalHours:0.#} 小时";

    private static QuotaWindow? Window(JsonElement root, string key, DateTimeOffset captured)
    {
        if (!root.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
            Number(window, "usedPercent") is not { } used || !double.IsFinite(used) || used < 0 ||
            Number(window, "windowDurationMins") is not { } minutes || !double.IsFinite(minutes) || minutes <= 0 || minutes > 5256000) return null;
        var reset = Number(window, "resetsAt");
        var at = reset is >= 0 and <= 253402300799d ? DateTimeOffset.FromUnixTimeSeconds((long)reset.Value) : (DateTimeOffset?)null;
        return new((int)Math.Round(Math.Clamp(used, 0, 100)), at, captured, TimeSpan.FromMinutes(minutes));
    }

    private static string Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double? Number(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}
