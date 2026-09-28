using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed record UsageKeyIdentity(string Host, string Fingerprint, string Label = "")
{
    public bool IsCustom => Host.Contains("://", StringComparison.Ordinal);
    public string DisplayName => IsCustom ? (Label.Length > 0 ? Label : new Uri(Host).Authority) : Host;
}

public static class UsageKeyVault
{
    private const string Prefix = "VibeGauge.UsageKey:";
    public static readonly string[] Hosts = ["open.bigmodel.cn", "api.z.ai", "api.minimaxi.com", "api.deepseek.com", "openrouter.ai", "api.moonshot.cn"];

    public static UsageKeyIdentity Save(string host, string key)
    {
        if (!Hosts.Contains(host)) throw new ArgumentException("厂商无效");
        return Store(host, key, "");
    }

    public static UsageKeyIdentity SaveCustom(string endpoint, string key, string label) =>
        Store(Sub2ApiEndpoint.Normalize(endpoint), key, label.Trim());

    public static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 2000 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
            throw new ArgumentException("API key 不能为空、包含空白字符或超过 2000 字符");
    }

    private static UsageKeyIdentity Store(string host, string key, string label)
    {
        ValidateKey(key);
        if (label.Length > 40 || label.Any(char.IsControl)) throw new ArgumentException("站点名称最多 40 字符，不能包含控制字符");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + key)))[..8].ToLowerInvariant();
        var bytes = Encoding.Unicode.GetBytes(key);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var credential = new Credential
            {
                Type = 1,
                TargetName = Prefix + host + "#" + fingerprint,
                UserName = host,
                Comment = label,
                BlobSize = (uint)bytes.Length,
                Blob = memory,
                Persist = 2
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(memory, i, 0); Marshal.FreeHGlobal(memory); CryptographicOperations.ZeroMemory(bytes); }
        return new(host, fingerprint, label);
    }

    public static IReadOnlyList<UsageKeyIdentity> List()
    {
        if (!CredEnumerate(Prefix + "*", 0, out var count, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return [];
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var result = new List<UsageKeyIdentity>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(pointer, i * IntPtr.Size));
                var parts = item.TargetName[Prefix.Length..].Split('#', 2);
                if (parts.Length != 2 || parts[1].Length != 8 || !parts[1].All(Uri.IsHexDigit)) continue;
                if (Hosts.Contains(parts[0])) result.Add(new(parts[0], parts[1]));
                else
                {
                    try
                    {
                        if (Sub2ApiEndpoint.Normalize(parts[0]) == parts[0])
                            result.Add(new(parts[0], parts[1], item.Comment is { Length: <= 40 } label && !label.Any(char.IsControl) ? label : ""));
                    }
                    catch (ArgumentException) { }
                }
            }
            return result;
        }
        finally { CredFree(pointer); }
    }

    public static string Read(UsageKeyIdentity identity)
    {
        if (!CredRead(Prefix + identity.Host + "#" + identity.Fingerprint, 1, 0, out var pointer))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(entry.Blob, (int)entry.BlobSize / 2) ?? "";
        }
        finally { CredFree(pointer); }
    }
    public static void Remove(UsageKeyIdentity identity)
    {
        if (!CredDelete(Prefix + identity.Host + "#" + identity.Fingerprint, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
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
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredEnumerate(string filter, uint flags, out int count, out IntPtr credentials);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
}
