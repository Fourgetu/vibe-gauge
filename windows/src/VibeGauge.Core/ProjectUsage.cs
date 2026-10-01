namespace VibeGauge.Core;

public sealed partial class UsageScanner
{
    internal static IReadOnlyList<ProjectUsage> BuildProjects(IEnumerable<InteractionRecord> records)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        string ProjectPath(string value)
        {
            if (!paths.TryGetValue(value, out var path)) paths[value] = path = NormalizeProjectPath(value);
            return path;
        }
        return records.Where(x => x.Source is "Claude" or "Codex")
        .GroupBy(x => ProjectPath(x.Cwd), ProjectPathComparer.Instance)
        .Select(group => new ProjectUsage(group.Key, group.Count(), group.Sum(x => x.ContextTokens),
            group.Sum(x => x.OutputTokens), group.Sum(x => x.ThinkingTokens),
            group.Select(x => x.Source).Distinct().Order(StringComparer.Ordinal).ToArray()))
        .OrderByDescending(x => x.ContextTokens).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        // Do not resolve relative paths against VibeGauge's own working directory.
        var normalized = path.Trim().Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized.Length == 2 && normalized[1] == ':' ? normalized + "/" : normalized;
    }

    private sealed class ProjectPathComparer : IEqualityComparer<string>
    {
        public static readonly ProjectPathComparer Instance = new();
        private static StringComparer For(string path) => path.StartsWith("//", StringComparison.Ordinal) ||
            path.Length >= 2 && path[1] == ':' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        public bool Equals(string? x, string? y) => x is null ? y is null : y is not null && For(x).Equals(x, y);
        public int GetHashCode(string obj) => For(obj).GetHashCode(obj);
    }
}
