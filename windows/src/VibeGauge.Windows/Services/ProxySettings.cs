using Microsoft.Win32;

namespace VibeGauge.Windows.Services;

public sealed class ProxySettings
{
    private const string KeyPath = @"Software\VibeGauge";
    private const string AutoStartName = "AutoStartProxy";

    public bool AutoStart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return Convert.ToInt32(key?.GetValue(AutoStartName, 0) ?? 0) == 1;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(AutoStartName, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
