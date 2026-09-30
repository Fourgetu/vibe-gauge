using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

internal sealed class ClashControllerCredentials(AppPaths paths, Func<string?>? environmentSecret = null, bool allowLan = false)
{
    internal const string InvalidEndpoint = "请输入本机 HTTP(S) 控制器地址；连接私有 IP 的设备需开启局域网软路由选项。";
    internal const string UnreadableSecret = "已保存的控制器密钥无法读取，请重新输入并保存。";
    internal string FilePath => Path.Combine(paths.LocalDataRoot, "clash-credential.json");
    private sealed record Saved(string Endpoint, string Ciphertext);
    private sealed record Vault(Dictionary<string, string> Secrets);

    internal static Uri NormalizeEndpoint(string? endpoint, bool allowLan = false)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException(InvalidEndpoint);
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            uri = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
        else if (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip) && !(allowLan && IsPrivate(ip)))
            throw new ArgumentException(InvalidEndpoint);
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }
    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        return bytes is [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _] ||
            bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
    }
    internal static bool IsLocal(Uri uri) => IPAddress.IsLoopback(IPAddress.Parse(uri.Host.Trim('[', ']')));
    internal static string NormalizeSourceIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!IPAddress.TryParse(value.Trim(), out var ip)) throw new ArgumentException("来源设备 IP 格式无效；留空显示控制器上的所有设备。");
        return ip.ToString();
    }
    internal static void ValidateSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 2000 || secret.Any(char.IsControl))
            throw new ArgumentException("控制器密钥不能为空、包含控制字符或超过 2000 字符。");
    }
    internal string? ReadSaved(string endpoint)
    {
        var address = NormalizeEndpoint(endpoint, allowLan).AbsoluteUri;
        if (!File.Exists(FilePath)) return null;
        try
        {
            var secrets = ReadVault();
            if (!secrets.TryGetValue(address, out var ciphertext)) return null;
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), Encoding.UTF8.GetBytes(address), DataProtectionScope.CurrentUser);
            try { var value = Encoding.UTF8.GetString(bytes); ValidateSecret(value); return value; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or CryptographicException or ArgumentException)
        { throw new InvalidOperationException(UnreadableSecret); }
    }
    internal string? Resolve(string endpoint) => ReadSaved(endpoint) ?? (IsLocal(NormalizeEndpoint(endpoint, allowLan)) ?
        (environmentSecret ?? (() => Environment.GetEnvironmentVariable("VIBEGAUGE_CLASH_SECRET")))() : null);
    internal void Save(string endpoint, string secret)
    {
        var address = NormalizeEndpoint(endpoint, allowLan).AbsoluteUri;
        ValidateSecret(secret);
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes(address), DataProtectionScope.CurrentUser);
            Dictionary<string, string> secrets;
            try { secrets = ReadVault(); }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException) { secrets = []; }
            secrets[address] = Convert.ToBase64String(encrypted);
            Directory.CreateDirectory(paths.LocalDataRoot);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(new Vault(secrets)));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private Dictionary<string, string> ReadVault()
    {
        if (!File.Exists(FilePath)) return [];
        if (new FileInfo(FilePath).Length > 512 * 1024) throw new InvalidDataException();
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        if (doc.RootElement.TryGetProperty("Endpoint", out _))
        {
            var legacy = doc.RootElement.Deserialize<Saved>() ?? throw new InvalidDataException();
            return new() { [legacy.Endpoint] = legacy.Ciphertext };
        }
        return doc.RootElement.Deserialize<Vault>()?.Secrets ?? throw new InvalidDataException();
    }
    internal void Remove(string endpoint)
    {
        var address = NormalizeEndpoint(endpoint, allowLan).AbsoluteUri;
        var secrets = ReadVault(); secrets.Remove(address);
        if (secrets.Count == 0) { File.Delete(FilePath); return; }
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(new Vault(secrets)));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
