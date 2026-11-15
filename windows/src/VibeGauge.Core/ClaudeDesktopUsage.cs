using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibeGauge.Core;

public sealed record ClaudeDesktopUsageLocation(bool Detected, string Root = "", string Scope = "",
    string Note = "未检测到桌面用量账本");

/// <summary>Reads only Claude Desktop's numeric usage ledger, never chat text or cookies.</summary>
public static class ClaudeDesktopUsage
{
    public const string MissingUsage = "桌面端尚未写入真实 Token 用量；普通聊天可能不提供本地用量";
    private static readonly Regex DayFile = new(@"^\d{4}-\d{2}-\d{2}(?:\.\d+)?\.ndjson$", RegexOptions.CultureInvariant);
    private static bool IsId(string name) => Guid.TryParseExact(name, "D", out _) ||
        Regex.IsMatch(name, "^[0-9a-fA-F]{8}$", RegexOptions.CultureInvariant);
    private static bool PlainDirectory(string path) => Directory.Exists(path) &&
        !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    public static ClaudeDesktopUsageLocation Locate(AppPaths paths, ProcessReport processes)
    {
        var status = ClaudeDesktop.Read(paths, processes);
        if (status is null) return new(false);
        if (status.Metadata?.Sources is not { Count: 1 } configs)
            return new(true, Note: "无法确定当前桌面配置，未读取用量");
        using var config = JsonSupport.ReadDocument(configs[0]);
        if (config?.RootElement.ValueKind != JsonValueKind.Object ||
            !config.RootElement.TryGetProperty("windowSizeWasSignedIn", out var signedIn) || signedIn.ValueKind != JsonValueKind.True ||
            !Guid.TryParseExact(config.RootElement.StringOrEmpty("lastKnownAccountUuid"), "D", out var account))
            return new(true, Note: "无法确定当前桌面账号，未读取用量");
        var profile = Path.GetDirectoryName(configs[0])!;
        var parent = Path.Combine(profile, "local-agent-mode-sessions");
        var full = account.ToString("D");
        var shortId = full[..8];
        try
        {
            if (!PlainDirectory(profile) || !PlainDirectory(parent)) return new(true, Note: MissingUsage);
            // Do not guess when both storage layouts or multiple organizations exist.
            var accounts = new[] { Path.Combine(parent, full), Path.Combine(parent, shortId) }.Where(PlainDirectory).ToArray();
            if (accounts.Length != 1) return new(true, Note: accounts.Length == 0 ? MissingUsage : "检测到多个桌面账号目录，未合并用量");
            var orgs = Directory.EnumerateDirectories(accounts[0]).Where(p => IsId(Path.GetFileName(p)) && PlainDirectory(p)).ToArray();
            if (orgs.Length != 1) return new(true, Note: orgs.Length == 0 ? MissingUsage : "检测到多个桌面组织，未合并用量");
            var ledger = Path.Combine(orgs[0], "usage-ledger");
            if (Directory.Exists(ledger) && !PlainDirectory(ledger)) return new(true, Note: "桌面用量目录不可读取");
            return new(true, ledger, Hash(Path.GetFullPath(profile).ToUpperInvariant() + "\n" + full + "\n" + Path.GetFileName(orgs[0])), MissingUsage);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(true, Note: "桌面用量目录暂时无法读取"); }
    }

    public static IEnumerable<string> Files(ClaudeDesktopUsageLocation location) =>
        location.Root.Length > 0 && Directory.Exists(location.Root)
            ? Directory.EnumerateFiles(location.Root, "*.ndjson", SearchOption.TopDirectoryOnly)
                .Where(p => DayFile.IsMatch(Path.GetFileName(p)) && !File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))
            : [];

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static InteractionRecord? Parse(JsonElement root, string scope)
    {
        if (root.ValueKind != JsonValueKind.Object || !Number(root, "v", out var version) || version != 1 ||
            !Number(root, "ts", out var milliseconds) || milliseconds <= 0 || milliseconds > 253402300799999 ||
            root.StringOrEmpty("surface") is not ("chat" or "cowork" or "code") ||
            !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object) return null;
        var surface = root.StringOrEmpty("surface");
        var origin = root.StringOrEmpty("origin");
        var session = root.StringOrEmpty("sessionId");
        if (origin.Length == 0 || origin.Length > 128 || session.Length > 256) return null;
        var names = new List<string>();
        long input = 0, output = 0, read = 0, write = 0;
        try
        {
            foreach (var entry in models.EnumerateObject())
            {
                if (names.Count >= 64 || entry.Name.Length is 0 or > 200 || names.Contains(entry.Name) ||
                    !Number(entry.Value, "inputTokens", out var i) || !Number(entry.Value, "outputTokens", out var o) ||
                    !Number(entry.Value, "cacheReadTokens", out var r) || !Number(entry.Value, "cacheWriteTokens", out var w)) return null;
                names.Add(entry.Name);
                checked { input += i; output += o; read += r; write += w; }
            }
            if (names.Count == 0 || checked(input + output + read + write) == 0) return null;
            var model = string.Join(" / ", names.Order(StringComparer.Ordinal));
            // No estimated cost, message text, account IDs or raw session IDs are retained.
            var id = Hash(JsonSerializer.Serialize(new { scope, milliseconds, surface, origin, session, model, input, output, read, write }));
            return new(id, ClaudeDesktop.Name, model, DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
                checked(input + read + write), read, write, output, 0,
                UsageKnown: true, SessionId: session.Length == 0 ? "" : Hash(session), UsageSurface: surface);
        }
        catch (OverflowException) { return null; }
    }

    private static bool Number(JsonElement root, string key, out long result)
    {
        result = 0;
        // Integer fields are required. Missing, string, negative and unbounded values are not zero usage.
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result) && result is >= 0 and <= 9_007_199_254_740_991;
    }
}
