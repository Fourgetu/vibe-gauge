using Microsoft.Win32;
using System.Diagnostics;

namespace VibeGauge.Windows.Services;

public sealed class StartupRegistrar
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VibeGauge";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName)?.ToString() is { Length: > 0 };
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("找不到当前程序路径");
            key.SetValue(ValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
