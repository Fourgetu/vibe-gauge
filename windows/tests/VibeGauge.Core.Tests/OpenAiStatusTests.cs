using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class OpenAiStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    public static string Page(object[] impacts, string? uptime = "99.95", bool active = false, bool showUptime = true, object[]? affected = null)
    {
        var summary = new
        {
            history_window_days = 3, affected_components = affected ?? [],
            ongoing_incidents = active ? new object[] { new { id = "incident" } } : [],
            structure = new { items = new[] { new { group = new { id = "codex", name = "Codex", hidden = false,
                display_aggregated_uptime = showUptime, components = new[] {
                    new { component_id = "cli", name = "CLI", hidden = false, display_uptime = true, data_available_since = "2026-10-02T00:00:00Z" },
                    new { component_id = "app", name = "App", hidden = false, display_uptime = true, data_available_since = "2026-09-01T00:00:00Z" } } } } } }
        };
        var data = new { component_impacts = impacts, component_uptimes = new[] {
            new { component_id = "$undefined", status_page_component_group_id = "codex", uptime } },
            incident_links = new[] { new { id = "incident", name = "Elevated errors" } } };
        var flight = "5:" + JsonSerializer.Serialize(new object[] { "$", "node", new { summary } }) + "\n1f:" +
            JsonSerializer.Serialize(new object[] { "$", "node", new { data } }) + "\n";
        // The transport can split a JSON record in the middle of a string.
        return string.Join("", new[] { flight[..57], flight[57..] }.Select(chunk =>
            "<script>self.__next_f.push([1," + JsonSerializer.Serialize(chunk) + "])</script>"));
    }
    private static object Impact(string start, string? end, string status = "degraded_performance", string component = "cli") =>
        new { component_id = component, start_at = start, end_at = end, status, status_page_incident_id = "incident" };

    [Fact]
    public void UsesOfficialUptimeAndGreyForComponentBeforeCreation()
    {
        var snapshot = OpenAiStatus.Parse(Page([]), Now);
        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(99.95m, group.Uptime);
        Assert.Equal(ServiceHealth.Operational, group.Health);
        Assert.Equal(3, group.Days.Count);
        Assert.Equal(ServiceHealth.Unknown, group.Components[0].Days[0].Health);
        Assert.Null(group.Components[0].Uptime);
        Assert.Equal(ServiceHealth.Operational, group.Days[0].Health);
    }

    [Fact]
    public void DailyHistoryHandlesMidnightOverlapAndResolvedIncidents()
    {
        var group = Assert.Single(OpenAiStatus.Parse(Page([
            Impact("2026-10-01T23:00:00Z", "2026-10-02T01:00:00Z", component: "app"),
            Impact("2026-10-02T00:00:00Z", "2026-10-03T00:00:00Z", "full_outage")]), Now).Groups);
        Assert.Equal(ServiceHealth.Degraded, group.Days[0].Health);
        Assert.Equal(ServiceHealth.Outage, group.Days[1].Health);
        Assert.Equal(ServiceHealth.Operational, group.Days[2].Health);
        Assert.Single(group.Days[1].Incidents);
        Assert.Equal(ServiceHealth.Operational, group.Health);
    }

    [Fact]
    public void ActiveImpactIsNotHiddenByHealthyHistoricalPercentage()
    {
        var group = Assert.Single(OpenAiStatus.Parse(Page([
            Impact("2026-10-03T10:00:00Z", null, "partial_outage")], active: true), Now).Groups);
        Assert.Equal(ServiceHealth.PartialOutage, group.Health);
        Assert.Equal(ServiceHealth.PartialOutage, group.Days[^1].Health);
        Assert.Equal(ServiceHealth.Operational, group.Components[1].Health);
    }

    [Fact]
    public void MissingOrDisabledPercentageIsNeverFabricated()
    {
        Assert.Null(Assert.Single(OpenAiStatus.Parse(Page([], uptime: null), Now).Groups).Uptime);
        var group = Assert.Single(OpenAiStatus.Parse(Page([], showUptime: false), Now).Groups);
        Assert.Null(group.Uptime); Assert.Empty(group.Days);
    }

    [Fact]
    public void UnrecognizedOngoingIncidentIsUnknownNotGreen()
    {
        Assert.Equal(ServiceHealth.Unknown, Assert.Single(OpenAiStatus.Parse(Page([], active: true), Now).Groups).Health);
    }

    [Fact]
    public void CurrentAffectedComponentWorksBeforeHistoryUpdates()
    {
        var group = Assert.Single(OpenAiStatus.Parse(Page([], active: true,
            affected: [new { component_id = "cli", status = "full_outage" }]), Now).Groups);
        Assert.Equal(ServiceHealth.Outage, group.Health);
        Assert.Equal(ServiceHealth.Outage, group.Components[0].Health);
        Assert.Equal(ServiceHealth.Operational, group.Components[1].Health);
    }

    [Fact]
    public void ChangedOrOversizedPayloadFailsClosed()
    {
        Assert.Throws<InvalidDataException>(() => OpenAiStatus.Parse("<html>maintenance</html>", Now));
        Assert.Throws<InvalidDataException>(() => OpenAiStatus.Parse(new string(' ', OpenAiStatus.MaximumBytes + 1), Now));
    }
}
