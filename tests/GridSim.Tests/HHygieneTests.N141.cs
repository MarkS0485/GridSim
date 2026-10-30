using GridSim.Core.Dynamics;
using GridSim.Core.Fleet;
using Xunit;

namespace GridSim.Tests;

/// <summary>N141: the field called InitialRocofHzPerS holds the PEAK RoCoF of the run (the largest-magnitude step value,
/// sign kept). The name is kept (public API and the export key the crossval sidecars read) and every carrier now says
/// so; these tests pin the meaning, so a future change cannot quietly turn it into the first-step value.</summary>
public sealed partial class HHygieneTests
{
    /// <summary>The signed step RoCoF with the largest magnitude over a trajectory, first one on ties (the integrators'
    /// strict ">" rule).</summary>
    private static double PeakOfPoints(FrequencyResponse r)
    {
        double peak = 0.0;
        foreach (var p in r.Points) if (Math.Abs(p.RocofHzPerS) > Math.Abs(peak)) peak = p.RocofHzPerS;
        return peak;
    }

    [Fact]
    public void N141_the_field_is_the_peak_not_the_first_event_rocof()
    {
        // A small loss at 1 s, then a much larger one at 6 s: the peak comes with the second loss, so a field that held
        // the "initial" (first-event) RoCoF would differ from it.
        var events = new[] { new FrequencyEvent(1.0, -200), new FrequencyEvent(6.0, -1_800) };
        FrequencyResponse r = FrequencyDynamics.Simulate(150_000, 28_000, events);
        AssertBits(PeakOfPoints(r), r.InitialRocofHzPerS, "lumped peak");
        double firstEventRocof = r.Points[(int)Math.Round(1.0 / 0.02)].RocofHzPerS;
        Assert.True(Math.Abs(r.InitialRocofHzPerS) > 2.0 * Math.Abs(firstEventRocof),
            $"peak {r.InitialRocofHzPerS:R} should be the second, larger loss's spike, not the first's {firstEventRocof:R}");

        FleetResult fleet = FleetDynamics.Simulate(150_000, 28_000, events, FleetSpec.Uniform(3, 6_000, 300, 0.5, 0.015, 0.2));
        AssertBits(PeakOfPoints(fleet.Response), fleet.Response.InitialRocofHzPerS, "fleet peak");

        // Screening and the assessment carry the same peak value through.
        FrequencyAssessment a = FrequencyLimits.Assess(r);
        AssertBits(r.InitialRocofHzPerS, a.InitialRocofHzPerS, "assessment");
    }

    [Fact]
    public void N141_every_carrier_documents_the_peak_meaning()
    {
        Assert.Contains("PEAK RoCoF", CoreSrc("Dynamics", "FrequencyDynamics.cs"));
        Assert.Contains("PEAK RoCoF", CoreSrc("Dynamics", "FrequencyLimits.cs"));
        Assert.Contains("PEAK RoCoF", CoreSrc("Dynamics", "RocofScreening.cs"));
        Assert.Contains("PEAK RoCoF", CoreSrc("Dynamics", "Distributed", "DomainTrace.cs"));
        Assert.Contains("PEAK RoCoF", CoreSrc("Assets", "AssetEnvelope.cs"));
        Assert.Contains("PEAK RoCoF", CoreSrc("IO", "FrequencyExport.cs"));
    }
}
