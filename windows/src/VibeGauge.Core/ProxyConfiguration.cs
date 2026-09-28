using System.Text.Json.Nodes;

namespace VibeGauge.Core;

public sealed record ProxyConfiguration(int Port = 18790, string? Upstream = null, string[]? NoProxy = null, string Error = "")
{
    public static ProxyConfiguration Load(string directory)
    {
        var path = Path.Combine(directory, "proxy.json");
        if (!File.Exists(path)) return new();
        try
        {
            var data = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException();
            var port = data["port"]?.GetValue<int>() ?? 18790;
            if (port is < 1024 or > 65535) throw new FormatException();
            var route = data["upstream"]?.GetValue<string>();
            if (!ValidRoute(route)) return new(port, "direct", Error: "proxy.json 的 upstream 必须是 HTTP 代理地址或 direct");
            var bypass = data["no_proxy"] is JsonArray values
                ? values.OfType<JsonValue>().Where(x => x.TryGetValue<string>(out _)).Select(x => x.GetValue<string>()).ToArray() : [];
            return new(port, route, bypass);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { return new(Upstream: "direct", Error: "proxy.json 格式错误，已禁用上游代理"); }
    }

    public static bool ValidRoute(string? route) => string.IsNullOrWhiteSpace(route) || route == "direct" ||
        Uri.TryCreate(route, UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.Host.Length > 0 &&
        uri.Port is > 0 and <= 65535 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public void Save(string directory)
    {
        if (Port is < 1024 or > 65535 || !ValidRoute(Upstream)) throw new ArgumentException("端口或上游代理地址无效");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "proxy.json");
        var data = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        data["port"] = Port;
        if (string.IsNullOrWhiteSpace(Upstream)) data.Remove("upstream"); else data["upstream"] = Upstream;
        File.WriteAllText(path + ".tmp", data.ToJsonString(new() { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
