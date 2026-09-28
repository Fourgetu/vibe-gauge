using System.Text.RegularExpressions;

namespace VibeGauge.Proxy.Security;

public static partial class SecretRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var result = QueryRegex().Replace(value, match => match.Groups[1].Value + "[REDACTED]");
        result = BearerRegex().Replace(result, "$1[REDACTED]");
        result = AssignmentRegex().Replace(result, "$1[REDACTED]");
        return LongSecretRegex().Replace(result, "[REDACTED]");
    }

    public static string PathOnly(Uri uri) => string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;

    [GeneratedRegex(@"([?&](?:key|api_key|access_token|token)=)[^&#\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex QueryRegex();

    [GeneratedRegex(@"\b(Bearer\s+)[^\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b((?:x-api-key|api-key|key|token)\s*[:=]\s*)[^\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex AssignmentRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_+/-])[A-Za-z0-9_+./=-]{32,}")]
    private static partial Regex LongSecretRegex();
}
