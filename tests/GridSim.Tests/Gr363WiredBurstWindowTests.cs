using GridSim.Core.Diagnostics;
using GridSim.Core.Diagnostics.Incident;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-363 (wiring): the product paths (CLI --incident-log, web Audit:IncidentLogPath) build their incident sinks with
/// IncidentWiring.AuditSinksFor, which now gives the burst monitor a one-hour sliding window. Pinned: the default window
/// is one hour; two bursts a day apart through the wired sinks raise two burst incidents (before the wiring they raised
/// one); a burst within the hour still raises exactly one; an explicit window is honoured and a non-positive one is
/// refused.
/// </summary>
public sealed class Gr363WiredBurstWindowTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // HorizonViolation is routine (Info baseline): the single-event sink raises nothing for it, so every incident
    // seen here comes from the burst monitor alone.
    private static AuditLogEntry Horizon(int seq, DateTime at)
    {
        string summary = AuditEvent.FormatSummary("actor", SecurityAudit.SecurityAction, Array.Empty<AuditInput>(),
            SecurityAudit.FormatOutcome(SecurityEventKind.HorizonViolation, SecuritySeverity.Info, $"event {seq}"));
        return new AuditLogEntry(seq, at, null, ProvenanceTag.DerivedFromRecorded, summary, "GENESIS", "HASH");
    }

    private static int Feed(IReadOnlyList<IAuditSink> sinks, IEnumerable<AuditLogEntry> entries)
    {
        int n = 0;
        foreach (AuditLogEntry e in entries)
        {
            foreach (IAuditSink s in sinks) s.OnEntry(e);
            n++;
        }
        return n;
    }

    private static IEnumerable<AuditLogEntry> Bursts(int bursts, TimeSpan apart)
    {
        int burst = IncidentPolicy.Default.HorizonViolationBurst;
        int seq = 0;
        for (int b = 0; b < bursts; b++)
            for (int i = 0; i < burst; i++)
                yield return Horizon(seq++, T0 + (apart * b) + TimeSpan.FromSeconds(i));
    }

    [Fact]
    public void Gr363_the_wired_default_window_is_one_hour()
    {
        Assert.Equal(TimeSpan.FromHours(1), IncidentWiring.DefaultBurstWindow);
    }

    [Fact]
    public void Gr363_wired_sinks_raise_one_incident_per_burst_a_day_apart()
    {
        Assert.True(IncidentPolicy.Default.HorizonViolationBurst > 0);
        var incidents = new BufferedIncidentSink();
        Feed(IncidentWiring.AuditSinksFor(incidents), Bursts(2, TimeSpan.FromDays(1)));
        Assert.Equal(2, incidents.Reports.Count);
        Assert.All(incidents.Reports, r => Assert.Equal(IncidentCategory.HorizonViolation, r.Category));
        Assert.Equal(T0 + TimeSpan.FromDays(1) + TimeSpan.FromSeconds(IncidentPolicy.Default.HorizonViolationBurst - 1),
            incidents.Reports[1].RaisedUtc);   // the second incident is stamped by the entry that completed the second burst
    }

    [Fact]
    public void Gr363_wired_sinks_raise_exactly_one_incident_for_one_long_burst_within_the_hour()
    {
        var incidents = new BufferedIncidentSink();
        int burst = IncidentPolicy.Default.HorizonViolationBurst;
        var entries = new List<AuditLogEntry>();
        for (int i = 0; i < burst + 5; i++) entries.Add(Horizon(i, T0.AddSeconds(i)));
        Feed(IncidentWiring.AuditSinksFor(incidents), entries);
        Assert.Single(incidents.Reports);
    }

    [Fact]
    public void Gr363_an_explicit_window_is_honoured()
    {
        // Bursts ten minutes apart: inside a one-hour window they merge into one latched burst, inside a one-minute
        // window each raises its own incident.
        var hour = new BufferedIncidentSink();
        Feed(IncidentWiring.AuditSinksFor(hour), Bursts(3, TimeSpan.FromMinutes(10)));
        Assert.Single(hour.Reports);

        var minute = new BufferedIncidentSink();
        Feed(IncidentWiring.AuditSinksFor(minute, burstWindow: TimeSpan.FromMinutes(1)), Bursts(3, TimeSpan.FromMinutes(10)));
        Assert.Equal(3, minute.Reports.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Gr363_a_non_positive_wired_window_is_refused(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IncidentWiring.AuditSinksFor(new BufferedIncidentSink(), burstWindow: TimeSpan.FromMinutes(minutes)));
    }
}
