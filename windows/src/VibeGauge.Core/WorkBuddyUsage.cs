using System.Text.Json;

namespace VibeGauge.Core;

public static class WorkBuddyUsage
{
    public const string SourceName = "WorkBuddy";

    public static InteractionRecord? Parse(JsonElement root)
    {
        var type = root.StringOrEmpty("type");
        if (type != "function_call" && (type != "message" || root.StringOrEmpty("role") != "assistant")) return null;
        if (root.StringOrEmpty("status") is "streaming" or "pending" ||
            !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
            !Read(usage, "input_tokens", out var input) || !Read(usage, "output_tokens", out var output)) return null;
        root.TryGetProperty("providerData", out var provider);
        var id = Text(provider, "messageId");
        if (id.Length == 0) id = root.StringOrEmpty("id");
        var session = root.StringOrEmpty("sessionId");
        if (id.Length == 0 || !root.TryGetProperty("timestamp", out var time)) return null;
        var at = time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out var milliseconds) &&
                 milliseconds is >= -62135596800000 and <= 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : time.ValueKind == JsonValueKind.String ? Formatting.ParseDate(time.GetString()!) : null;
        if (at is null) return null;
        Read(usage, "cache_read_input_tokens", out var read);
        Read(usage, "cache_creation_input_tokens", out var write);
        long thinking = 0;
        if (provider.ValueKind == JsonValueKind.Object && provider.TryGetProperty("rawUsage", out var raw) && raw.ValueKind == JsonValueKind.Object)
        {
            if (raw.TryGetProperty("completion_tokens_details", out var details)) Read(details, "reasoning_tokens", out thinking);
            if (thinking == 0) Read(raw, "completion_thinking_tokens", out thinking);
        }
        var model = Text(provider, "model");
        if (model.Length == 0) model = Text(provider, "requestModelId");
        try
        {
            // WorkBuddy's normalized message.usage input includes cache traffic.
            // Some Gemini gateways exclude reasoning from completion_tokens;
            // only add it when the explicitly reported total confirms that split.
            if (thinking > 0 && Read(usage, "total_tokens", out var total) && total == checked(input + output + thinking))
                output = checked(output + thinking);
            if (checked(input + output) == 0) return null;
            return new(session.Length > 0 ? session + ":" + id : id, SourceName,
                model.Length > 0 ? model : SourceName, at.Value, input, read, write, output, thinking);
        }
        catch (OverflowException) { return null; }
    }

    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object ? value.StringOrEmpty(key) : "";
    private static bool Read(JsonElement value, string key, out long result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var item) ||
            item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var number) || number < 0) return false;
        result = number;
        return true;
    }
}
