using GridSim.Core.Diagnostics;
using GridSim.Core.Diagnostics.Incident;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-363: SecurityEventMonitor counted each kind for the life of the process and latched after the first burst, so
/// bursts spread over a long run raised one incident and every later burst raised none. The monitor takes an optional
/// burst window: events are counted inside a sliding window keyed on each entry's own RunTimestampUtc, and the latch is
/// re-armed once the count inside the window falls below the threshold. Pinned: spaced bursts each raise one incident;
/// events too far apart never reach the threshold; a burst inside the window raises exactly one; the no-window default
/// is unchanged (one incident for the same spaced entries); a non-positive window is refused.
/// </summary>
public sealed class Gr363SecurityMonitorWindowTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AuditLogEntry Fdia(int seq, DateTime at)
    {
        string summary = AuditEvent.FormatSummary("actor", SecurityAudit.SecurityAction, Array.Empty<AuditInput>(),
            SecurityAudit.FormatOutcome(SecurityEventKind.SuspectedFdia, SecuritySeverity.Significant, $"event {seq}"));
        return new AuditLogEntry(seq, at, null, ProvenanceTag.DerivedFromRecorded, summary, "GENESIS", "HASH");
    }

    private static (SecurityEventMonitor Monitor, BufferedIncidentSink Sink) NewMonitor(TimeSpan? window)
    {
        var sink = new BufferedIncidentSink();
        return (new SecurityEventMonitor(new IncidentRouter(sink), burstWindow: window), sink);
    }

    // Two bursts of three events (threshold 3), a day apart; the events of one burst are a minute apart.
    private static IEnumerable<AuditLogEntry> TwoSpacedBursts()
    {
        int seq = 0;
        for (int burst = 0; burst < 2; burst++)
            for (int i = 0; i < 3; i++)
                yield return Fdia(seq++, T0.AddDays(burst).AddMinutes(i));
    }

    [Fact]
    public void Gr363_two_spaced_bursts_raise_two_incidents_with_a_window()
    {
        (SecurityEventMonitor monitor, BufferedIncidentSink sink) = NewMonitor(TimeSpan.FromHours(1));
        foreach (AuditLogEntry e in TwoSpacedBursts()) monitor.OnEntry(e);
        Assert.Equal(2, sink.Reports.Count);
        Assert.Equal(6, monitor.CountOf(SecurityEventKind.SuspectedFdia));   // the tally stays cumulative
    }

    [Fact]
    public void Gr363_without_a_window_the_same_entries_raise_one_incident_as_before()
    {
        (SecurityEventMonitor monitor, BufferedIncidentSink sink) = NewMonitor(null);
        foreach (AuditLogEntry e in TwoSpacedBursts()) monitor.OnEntry(e);
        Assert.Single(sink.Reports);
    }

    [Fact]
    public void Gr363_a_burst_inside_the_window_raises_exactly_one_incident()
    {
        (SecurityEventMonitor monitor, BufferedIncidentSink sink) = NewMonitor(TimeSpan.FromHours(1));
        for (int i = 0; i < 5; i++) monitor.OnEntry(Fdia(i, T0.AddMinutes(i)));
        Assert.Single(sink.Reports);
    }

    [Fact]
    public void Gr363_events_further_apart_than_the_window_never_reach_the_threshold()
    {
        (SecurityEventMonitor monitor, BufferedIncidentSink sink) = NewMonitor(TimeSpan.FromMinutes(10));
        for (int i = 0; i < 6; i++) monitor.OnEntry(Fdia(i, T0.AddHours(i)));
        Assert.Empty(sink.Reports);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Gr363_a_non_positive_window_is_refused(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewMonitor(TimeSpan.FromMinutes(minutes)));
    }
}
