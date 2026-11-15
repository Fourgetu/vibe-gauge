using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

internal sealed class ClaudeDesktopCredential(ClaudeDesktopQuotaAccount account, string token)
{
    public ClaudeDesktopQuotaAccount Account { get; } = account;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Token { get; } = token;
    public override string ToString() => "Claude Desktop credential (redacted)";
}

internal static class ClaudeDesktopCredentials
{
    private const int MaxConfigBytes = 2 * 1024 * 1024;

    internal static ClaudeDesktopCredential? Read(string profile, DateTimeOffset now)
    {
        using var config = ReadJson(Path.Combine(profile, "config.json"));
        var root = config.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("windowSizeWasSignedIn", out var signedIn) ||
            signedIn.ValueKind != JsonValueKind.True || !root.TryGetProperty("lastKnownAccountUuid", out var account) ||
            account.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(account.GetString()) ||
            !root.TryGetProperty("oauth:tokenCacheV2", out var encrypted) || encrypted.ValueKind != JsonValueKind.String) return null;
        using var localState = ReadJson(Path.Combine(profile, "Local State"));
        var wrappedKey = Convert.FromBase64String(localState.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!);
        if (wrappedKey.Length <= 5 || !wrappedKey.AsSpan(0, 5).SequenceEqual("DPAPI"u8)) return null;
        var key = ProtectedData.Unprotect(wrappedKey.AsSpan(5).ToArray(), null, DataProtectionScope.CurrentUser);
        byte[]? plaintext = null;
        try
        {
            var cipher = Convert.FromBase64String(encrypted.GetString()!);
            if (cipher.Length <= 31 || !cipher.AsSpan(0, 3).SequenceEqual("v10"u8)) return null;
            plaintext = new byte[cipher.Length - 31];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(cipher.AsSpan(3, 12), cipher.AsSpan(15, plaintext.Length), cipher.AsSpan(cipher.Length - 16), plaintext);
            using var cache = JsonDocument.Parse(plaintext);
            return Select(cache.RootElement, account.GetString()!, profile, now);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static JsonDocument ReadJson(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > MaxConfigBytes) throw new IOException("Desktop configuration is too large");
        return JsonDocument.Parse(file);
    }

    internal static ClaudeDesktopCredential? Select(JsonElement cache, string account, string profile, DateTimeOffset now)
    {
        if (cache.ValueKind != JsonValueKind.Object) return null;
        var candidates = new List<(string Org, string Token, string Tier, double Expires, bool Inference)>();
        foreach (var entry in cache.EnumerateObject())
        {
            // Anchor the origin and current account. Other providers and previous accounts are excluded.
            var match = Regex.Match(entry.Name, @"^acct:([^|]+)\|[^:]+:([^:]+):https://api\.anthropic\.com:(.+)$");
            if (!match.Success || match.Groups[1].Value != account) continue;
            var scopes = match.Groups[3].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!scopes.Contains("user:profile", StringComparer.Ordinal)) continue;
            var value = entry.Value;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("identityChecked", out var checkedIdentity) ||
                checkedIdentity.ValueKind != JsonValueKind.True || !value.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(token.GetString()) ||
                !value.TryGetProperty("expiresAt", out var expires) || expires.ValueKind != JsonValueKind.Number ||
                !expires.TryGetDouble(out var expiry) || !double.IsFinite(expiry) || expiry <= now.ToUnixTimeMilliseconds() + 30000) continue;
            var subscription = value.TryGetProperty("subscriptionType", out var plan) && plan.ValueKind == JsonValueKind.String ? plan.GetString() : "";
            var tier = subscription switch { "pro" => "Pro", "max" => "Max", "team" => "Team", "enterprise" => "Enterprise", "free" => "Free", _ => "已登录" };
            candidates.Add((match.Groups[2].Value, token.GetString()!, tier, expiry, scopes.Contains("user:inference", StringComparer.Ordinal)));
        }
        if (candidates.Count == 0 || candidates.Select(x => x.Org).Distinct(StringComparer.Ordinal).Count() != 1) return null;
        var selected = candidates.OrderByDescending(x => x.Inference).ThenByDescending(x => x.Expires).First();
        var scope = Hash(Path.GetFullPath(profile).ToUpperInvariant() + "\n" + account + "\n" + selected.Org);
        var revision = Hash(selected.Token);
        return new(new(profile, scope, revision, selected.Tier), selected.Token);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
