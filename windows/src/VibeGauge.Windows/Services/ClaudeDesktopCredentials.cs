using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

internal sealed class ClaudeDesktopCredential(ClaudeDesktopQuotaAccount account, string secret)
{
    public ClaudeDesktopQuotaAccount Account { get; } = account;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Secret { get; } = secret;
    public override string ToString() => $"Claude credential ({Account.SourceKind}, redacted)";
}

/// <summary>
/// CodexBar-style clean Claude quota credential reader.
/// Zero DPAPI / AES-GCM decryption of Chromium/Electron Local State.
/// Supports:
/// - "off" (default): no credential read, local read-only logs only.
/// - "web": user-supplied claude.ai sessionKey stored in Windows Credential Manager.
/// - "oauth": plain Claude Code ~/.claude/.credentials.json with user:profile scope.
/// </summary>
internal static class ClaudeDesktopCredentials
{
    private const int MaxCredentialBytes = 512 * 1024;
    private const string SessionKeyTarget = "VibeGauge.ClaudeSessionKey";

    internal static ClaudeDesktopCredential? ReadForMode(AppPaths paths, string mode, string profile, DateTimeOffset now, Func<string?>? readManualSessionKey = null)
    {
        return mode switch
        {
            "web" => ReadManualWebSession(profile, readManualSessionKey ?? ReadSavedSessionKey),
            "oauth" => ReadClaudeCodeOAuth(paths.ClaudeRoot, profile, now),
            _ => null
        };
    }

    internal static ClaudeDesktopCredential? ReadManualWebSession(string profile, Func<string?> readSecret)
    {
        var raw = readSecret();
        var key = ExtractSessionKey(raw);
        if (key is null) return null;
        var hash = Hash(key);
        var scope = Hash("claude-web-manual\n" + hash);
        return new(new(profile, scope, hash, "Web 订阅", "web"), key);
    }

    internal static ClaudeDesktopCredential? ReadClaudeCodeOAuth(string claudeRoot, string profile, DateTimeOffset now)
    {
        var path = Path.Combine(claudeRoot, ".credentials.json");
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > MaxCredentialBytes) throw new IOException("Credentials file is too large");
        using var doc = JsonDocument.Parse(file);
        return SelectClaudeCodeOAuth(doc.RootElement, profile, now);
    }

    internal static ClaudeDesktopCredential? SelectClaudeCodeOAuth(JsonElement root, string profile, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
            return null;
        if (!oauth.TryGetProperty("accessToken", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String)
            return null;
        var token = tokenEl.GetString()?.Trim() ?? "";
        if (token.Length == 0 || token.Length > 4000 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            return null;

        if (oauth.TryGetProperty("expiresAt", out var expEl) && expEl.ValueKind == JsonValueKind.Number &&
            expEl.TryGetDouble(out var expiry) && double.IsFinite(expiry))
        {
            var expiryMs = expiry < 10_000_000_000 ? expiry * 1000 : expiry;
            if (expiryMs <= now.ToUnixTimeMilliseconds() + 30_000) return null;
        }

        // Require user:profile scope when scopes are present (CodexBar rule: inference-only or MCP-only tokens cannot query usage).
        if (oauth.TryGetProperty("scopes", out var scopesEl) && scopesEl.ValueKind == JsonValueKind.Array)
        {
            var hasProfile = scopesEl.EnumerateArray()
                .Any(x => x.ValueKind == JsonValueKind.String && string.Equals(x.GetString(), "user:profile", StringComparison.Ordinal));
            if (!hasProfile) return null;
        }

        var sub = oauth.TryGetProperty("subscriptionType", out var planEl) && planEl.ValueKind == JsonValueKind.String
            ? planEl.GetString()?.Trim().ToLowerInvariant() ?? "" : "";
        var rateTier = oauth.TryGetProperty("rateLimitTier", out var rtEl) && rtEl.ValueKind == JsonValueKind.String
            ? rtEl.GetString()?.Trim().ToLowerInvariant() ?? "" : "";

        var tier = rateTier switch
        {
            var r when r.Contains("max_20x", StringComparison.Ordinal) => "Max 20x",
            var r when r.Contains("max_5x", StringComparison.Ordinal) => "Max 5x",
            _ => sub switch
            {
                "max" => "Max",
                "pro" => "Pro",
                "team" => "Team",
                "enterprise" => "Enterprise",
                "free" => "Free",
                _ => "已登录"
            }
        };

        var revision = Hash(token);
        var scope = Hash("claude-code-oauth\n" + Path.GetFullPath(profile).ToUpperInvariant() + "\n" + tier);
        return new(new(profile, scope, revision, tier, "oauth"), token);
    }

    public static string? ExtractSessionKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 8000) return null;
        var trimmed = raw.Trim();
        // Allow pasting either a raw sessionKey value (sk-ant-...) or a Cookie header containing sessionKey=...
        var match = Regex.Match(trimmed, @"(?:^|[;\s])sessionKey=([^\s;]+)", RegexOptions.CultureInvariant);
        var candidate = match.Success ? match.Groups[1].Value.Trim() : trimmed;
        if (candidate.Length < 16 || candidate.Length > 2000 ||
            !candidate.StartsWith("sk-ant-", StringComparison.Ordinal) ||
            candidate.Any(char.IsWhiteSpace) || candidate.Any(char.IsControl) || candidate.Contains(';'))
            return null;
        return candidate;
    }

    public static string SaveSessionKey(string rawInput)
    {
        var key = ExtractSessionKey(rawInput) ?? throw new ArgumentException("无效的 sessionKey：请粘贴 claude.ai 的 sessionKey（以 sk-ant- 开头）或完整 Cookie 请求头。");
        var bytes = Encoding.Unicode.GetBytes(key);
        var mem = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            var cred = new NativeCredential
            {
                Type = 1,
                TargetName = SessionKeyTarget,
                UserName = "claude.ai",
                Comment = "Claude Web sessionKey (manual)",
                BlobSize = (uint)bytes.Length,
                Blob = mem,
                Persist = 2
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(mem, i, 0);
            Marshal.FreeHGlobal(mem);
            CryptographicOperations.ZeroMemory(bytes);
        }
        return Hash(key)[..8].ToLowerInvariant();
    }

    public static string? ReadSavedSessionKey()
    {
        if (!CredRead(SessionKeyTarget, 1, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 1168) return null;
            throw new Win32Exception(err);
        }
        try
        {
            var entry = Marshal.PtrToStructure<NativeCredential>(ptr);
            return Marshal.PtrToStringUni(entry.Blob, (int)entry.BlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    public static bool HasSavedSessionKey()
    {
        try { return ExtractSessionKey(ReadSavedSessionKey()) is not null; }
        catch (Win32Exception) { return false; }
    }

    public static void DeleteSavedSessionKey()
    {
        if (!CredDelete(SessionKeyTarget, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
}
