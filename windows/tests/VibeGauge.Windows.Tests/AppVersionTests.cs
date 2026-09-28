using System.Reflection;
using VibeGauge.Windows.Services;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("1.5.2+commit123", "v1.5.2")]
    [InlineData("1.6.0-rc.1+commit456", "v1.6.0-rc.1")]
    [InlineData("1.5.2", "v1.5.2")]
    [InlineData(null, "v2.3.4")]
    [InlineData("", "v2.3.4")]
    public void DisplayUsesBuildVersionWithoutCommitHash(string? informationalVersion, string expected) =>
        Assert.Equal(expected, AppVersionInfo.Format(informationalVersion, new Version(2, 3, 4, 0)));

    [Fact]
    public void VersionBelongsToTheApplicationNotTheTestRunner()
    {
        var app = typeof(App).Assembly;
        var expected = AppVersionInfo.Format(
            app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            app.GetName().Version);
        Assert.Equal(expected, AppVersionInfo.DisplayVersion);
    }
}
