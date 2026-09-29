using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibeGauge.Core;

public sealed record UpdateRelease(string Version, string Url, string Asset)
{
    public static UpdateRelease? Parse(JsonElement root, string currentVersion)
    {
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
            root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        var tag = root.StringOrEmpty("tag_name");
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) ||
            !System.Version.TryParse(tag.TrimStart('v'), out var latest) ||
            !System.Version.TryParse(currentVersion.Split('+', '-')[0].TrimStart('v'), out var installed) || latest <= installed) return null;
        var url = root.StringOrEmpty("html_url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/Fourgetu/vibe-gauge/releases/tag/", StringComparison.OrdinalIgnoreCase)) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var asset = assets.EnumerateArray().Select(x => x.StringOrEmpty("name")).FirstOrDefault(x =>
            (x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) &&
            (x.Contains("win", StringComparison.OrdinalIgnoreCase) || x.Contains("setup", StringComparison.OrdinalIgnoreCase)));
        return asset is null ? null : new(tag, url, asset);
    }
}
