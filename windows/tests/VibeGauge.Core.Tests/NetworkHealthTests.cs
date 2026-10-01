using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class NetworkHealthTests
{
    [Theory]
    [InlineData("198.18.0.2,198.19.0.1", "managed")]
    [InlineData("198.18.0.2,223.5.5.5", "warning")]
    [InlineData("223.6.6.6", "warning")]
    [InlineData("127.0.0.1,::1", "unknown")]
    [InlineData("192.168.1.1", "unknown")]
    [InlineData("198.18.0.2,8.8.8.8", "unknown")]
    [InlineData("invalid", "unknown")]
    [InlineData("", "unknown")]
    public void DnsVerdictDoesNotTreatLocalResolverAsProof(string servers, string expected) =>
        Assert.Equal(expected, NetworkHealth.Dns(servers.Split(',')).Verdict);

    [Theory]
    [InlineData(true, false, false, "warning")]
    [InlineData(true, true, false, "warning")]
    [InlineData(false, true, true, "unavailable")]
    [InlineData(false, false, true, "unknown")]
    [InlineData(false, true, false, "unknown")]
    [InlineData(false, false, false, "unknown")]
    public void Ipv6OnlyReportsUnavailableWithReachableControl(bool response, bool control, bool connectionFailure, string verdict) =>
        Assert.Equal(verdict, NetworkHealth.Ipv6(response, control, connectionFailure).Verdict);
}
