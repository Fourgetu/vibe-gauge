using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibeGauge.Core;

public enum ServiceHealth { Unknown, Operational, Maintenance, Degraded, PartialOutage, Outage }
public sealed record ServiceDay(DateOnly Date, ServiceHealth Health, IReadOnlyList<string> Incidents);
public sealed record ServiceStatusRow(string Id, string Name, ServiceHealth Health, decimal? Uptime,
    IReadOnlyList<ServiceDay> Days, IReadOnlyList<ServiceStatusRow> Components);
public sealed record OpenAiStatusSnapshot(DateTimeOffset CapturedAt, int HistoryDays, IReadOnlyList<ServiceStatusRow> Groups);

/// <summary>Reads the same public incident.io payload used by status.openai.com.
/// The compatibility summary API omits some newer groups and their uptime history.
/// Fail closed on page schema changes instead of inventing healthy history.</summary>
public static class OpenAiStatus
{
    public const string Url = "https://status.openai.com/";
    public const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Regex Flight = new("self\\.__next_f\\.push\\(\\[1,(\"(?:[^\"\\\\]|\\\\.)*\")\\]\\)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static async Task<OpenAiStatusSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("状态页响应过大");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaximumBytes) throw new InvalidDataException("状态页响应过大");
            output.Write(buffer, 0, read);
        }
        var html = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        return await Task.Run(() => Parse(html, DateTimeOffset.UtcNow), timeout.Token).ConfigureAwait(false);
    }

    public static OpenAiStatusSnapshot Parse(string html, DateTimeOffset now)
    {
        if (html.Length > MaximumBytes) throw new InvalidDataException("状态页响应过大");
        var flight = new StringBuilder();
        foreach (Match match in Flight.Matches(html)) flight.Append(JsonSerializer.Deserialize<string>(match.Groups[1].Value));
        JsonElement summary = default, history = default;
        foreach (var line in flight.ToString().Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 1 || line.Length <= colon + 1 || line[colon + 1] is not ('[' or '{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.AsMemory(colon + 1));
                Visit(doc.RootElement);
            }
            catch (JsonException) { /* Flight also contains non-JSON records. */ }
        }
        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("structure", out var structure) && structure.ValueKind == JsonValueKind.Object &&
                    node.TryGetProperty("history_window_days", out _)) summary = node.Clone();
                if (node.TryGetProperty("component_impacts", out var impacts) && impacts.ValueKind == JsonValueKind.Array &&
                    node.TryGetProperty("component_uptimes", out var uptimes) && uptimes.ValueKind == JsonValueKind.Array) history = node.Clone();
                foreach (var property in node.EnumerateObject()) Visit(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) Visit(child);
        }
        if (summary.ValueKind != JsonValueKind.Object || history.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("官方状态页数据格式已变化，暂时无法读取历史");
        return Build(summary, history, now);
    }

    private sealed record Impact(string Component, DateTimeOffset Start, DateTimeOffset? End, ServiceHealth Health, string Incident);
    private static OpenAiStatusSnapshot Build(JsonElement summary, JsonElement history, DateTimeOffset now)
    {
        foreach (var field in new[] { "affected_components", "ongoing_incidents" })
            if (!summary.TryGetProperty(field, out var required) || required.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("官方当前状态字段缺失");
        var count = summary.GetProperty("history_window_days").GetInt32();
        if (count is < 1 or > 366) throw new InvalidDataException("状态历史范围无效");
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var links = Array(history, "incident_links").GroupBy(x => Text(x, "id"))
            .ToDictionary(x => x.Key, x => Text(x.First(), "name"));
        var impacts = Array(history, "component_impacts").Select(x => new Impact(Text(x, "component_id"),
            Date(x, "start_at") ?? throw new InvalidDataException("故障开始时间缺失"), Date(x, "end_at"),
            Health(Text(x, "status")), links.GetValueOrDefault(Text(x, "status_page_incident_id"), "官方故障记录"))).ToArray();
        var uptimes = Array(history, "component_uptimes");
        var affected = Array(summary, "affected_components");
        var hasActive = Array(summary, "ongoing_incidents").Length > 0 || affected.Length > 0;
        var current = affected.Where(x => Text(x, "component_id").Length > 0).GroupBy(x => Text(x, "component_id"))
            .ToDictionary(x => x.Key, x => Worst(x.Select(c => Health(Text(c, "status")))));
        var unknownActive = hasActive && (affected.Any(x => Text(x, "component_id").Length == 0) ||
            current.Count == 0 && !impacts.Any(x => x.Start <= now && (x.End is null || x.End > now)));
        var groups = new List<ServiceStatusRow>();
        foreach (var item in Array(summary.GetProperty("structure"), "items"))
        {
            if (!item.TryGetProperty("group", out var group) || group.ValueKind != JsonValueKind.Object || Flag(group, "hidden")) continue;
            var components = Array(group, "components").Where(x => !Flag(x, "hidden")).ToArray();
            var rows = components.Select(c => Make(Text(c, "component_id"), Text(c, "name"), [c], Flag(c, "display_uptime"), false)).ToArray();
            if (rows.Length == 0) continue;
            var row = Make(Text(group, "id"), Text(group, "name"), components, Flag(group, "display_aggregated_uptime"), true);
            groups.Add(row with { Components = rows, Health = Worst(rows.Select(x => x.Health)) });
        }
        if (groups.Count == 0) throw new InvalidDataException("未读取到官方服务分组");
        return new(now, count, groups);

        ServiceStatusRow Make(string id, string name, JsonElement[] components, bool showUptime, bool isGroup)
        {
            var ids = components.Select(c => Text(c, "component_id")).ToHashSet(StringComparer.Ordinal);
            var relevant = impacts.Where(i => ids.Contains(i.Component)).ToArray();
            var active = relevant.Where(i => i.Start <= now && (i.End is null || i.End > now)).ToArray();
            var reported = ids.Where(current.ContainsKey).Select(id => current[id]).ToArray();
            // Current affected components are authoritative; history can lag a new incident.
            var health = reported.Length > 0 ? Worst(reported) : active.Length > 0 ? Worst(active.Select(x => x.Health)) :
                unknownActive ? ServiceHealth.Unknown : ServiceHealth.Operational;
            var uptime = uptimes.FirstOrDefault(x => Text(x, isGroup ? "status_page_component_group_id" : "component_id") == id &&
                (!isGroup || Text(x, "component_id").Length == 0));
            decimal? percent = showUptime && decimal.TryParse(Text(uptime, "uptime"), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100 ? value : null;
            var since = components.Select(c => Date(c, "data_available_since")).Where(x => x is not null).Min();
            var days = new List<ServiceDay>();
            if (showUptime)
            for (var i = count - 1; i >= 0; i--)
            {
                var day = today.AddDays(-i);
                var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                var end = start.AddDays(1);
                var matches = relevant.Where(x => x.Start < end && x.Start <= now && (x.End ?? now) > start).ToArray();
                var state = since is null || since >= end ? ServiceHealth.Unknown : matches.Length > 0
                    ? Worst(matches.Select(x => x.Health)) : ServiceHealth.Operational;
                days.Add(new(day, state, matches.Select(x => x.Incident).Distinct().ToArray()));
            }
            return new(id, name, health, percent, days, []);
        }
    }

    private static JsonElement[] Array(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    private static string Text(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
        !text.StartsWith('$') ? text : "";
    private static bool Flag(JsonElement node, string key) => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static DateTimeOffset? Date(JsonElement node, string key) => DateTimeOffset.TryParse(Text(node, key),
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    private static ServiceHealth Worst(IEnumerable<ServiceHealth> values)
    {
        var all = values.ToArray();
        var worst = all.DefaultIfEmpty(ServiceHealth.Unknown).Max();
        return worst <= ServiceHealth.Operational && all.Contains(ServiceHealth.Unknown) ? ServiceHealth.Unknown : worst;
    }
    public static ServiceHealth Health(string status) => status switch
    {
        "operational" => ServiceHealth.Operational,
        "under_maintenance" or "maintenance" => ServiceHealth.Maintenance,
        "degraded_performance" => ServiceHealth.Degraded,
        "partial_outage" => ServiceHealth.PartialOutage,
        "full_outage" or "major_outage" => ServiceHealth.Outage,
        _ => ServiceHealth.Unknown
    };
}
