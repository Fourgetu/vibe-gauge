using System.Reflection;

namespace VibeGauge.Windows.Services;

internal static class AppVersionInfo
{
    public static string DisplayVersion { get; } = Format(
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(App).Assembly.GetName().Version);

    internal static string Format(string? informationalVersion, Version? assemblyVersion)
    {
        var version = informationalVersion?.Split('+', 2)[0].Trim();
        if (string.IsNullOrEmpty(version))
            version = assemblyVersion is null ? "unknown" :
                $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";
        return "v" + version;
    }
}
