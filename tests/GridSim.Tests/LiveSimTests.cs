using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// LiveSim now lives in Core (uses only Core types), so the live day-simulation is unit-testable
/// without the WPF app - previously it had zero coverage.
/// </summary>
public class LiveSimTests
{
    /// <summary>The nominal frequency the live loop runs case9 at: its own, 60 Hz since D2 (N273).</summary>
    private static readonly double Case9NominalHz = IeeeCases.Case9().FrequencyHz;

    [Fact]
    public void Step_produces_a_converged_frame_and_advances_the_tick()
    {
        var sim = new LiveSim(IeeeCases.Case9());
        SimFrame f = sim.Step(1);
        // Moved 2026-10 (B2 N273, Ruling R1: Mark delegated EE calls 2026-10-04; source: D2 / N017, the WSCC 9-bus case is a
        // 60 Hz system (Anderson and Fouad, ch. 2), and N273, the live loop runs at the model's nominal with the GB bands
        // about it): the live loop used to run case9 at the GB 50 Hz, so this pinned 50 Hz values. Before: InRange(49.7, 50.3);
        // after: the same +/-0.3 Hz about the case's nominal, InRange(59.7, 60.3).
        Assert.Equal(1, f.Tick);
        Assert.True(f.Result.Converged);
        Assert.Equal(60.0, Case9NominalHz);
        Assert.InRange(f.FrequencyHz, Case9NominalHz - 0.3, Case9NominalHz + 0.3);
        Assert.True(f.DemandMw > 0);
        Assert.True(f.GenerationMw > 0);
        Assert.NotNull(f.Model);
    }

    [Fact]
    public void Demand_follows_the_daily_curve_across_a_day()
    {
        var sim = new LiveSim(IeeeCases.Case9());
        var demand = new List<double>();
        for (int t = 1; t <= 72; t++) demand.Add(sim.Step(t, stepMinutes: 20).DemandMw);
        // Over a simulated day the demand must vary (day/night curve), not sit flat.
        Assert.True(demand.Max() - demand.Min() > 1.0, "demand should follow the daily load profile");
        Assert.All(demand, d => Assert.True(d > 0));
    }

    [Fact]
    public void Frequency_stays_within_the_live_band_over_many_ticks()
    {
        var sim = new LiveSim(IeeeCases.Case9());
        for (int t = 1; t <= 200; t++)
        {
            SimFrame f = sim.Step(t);
            // Moved 2026-10 (B2 N273, Ruling R1: Mark delegated EE calls 2026-10-04; source: D2 / N017, the WSCC 9-bus case is a
            // 60 Hz system (Anderson and Fouad, ch. 2), and N273, the live loop runs at the model's nominal with the GB bands
            // about it): the live loop used to run case9 at the GB 50 Hz, so this pinned 50 Hz values. Before:
            // InRange(49.7, 50.3); after: the same +/-0.3 Hz about the case's nominal, InRange(59.7, 60.3).
            Assert.InRange(f.FrequencyHz, Case9NominalHz - 0.3, Case9NominalHz + 0.3);
        }
    }

    [Fact]
    public void Tripping_infeed_dips_the_frequency_then_it_recovers()
    {
        var sim = new LiveSim(IeeeCases.Case9());   // ~315 MW, real WSCC inertia
        for (int t = 1; t <= 5; t++) sim.Step(t, stepMinutes: 1.0 / 60.0);   // settle at 1 s steps

        sim.TripInfeed(60);   // lose 60 MW of infeed
        // Moved 2026-10 (B2 N273, Ruling R1: Mark delegated EE calls 2026-10-04; source: D2 / N017, the WSCC 9-bus case is a
        // 60 Hz system (Anderson and Fouad, ch. 2), and N273, the live loop runs at the model's nominal with the GB bands
        // about it): the live loop used to run case9 at the GB 50 Hz, so this pinned 50 Hz values. Before: nadir started at 50.0 and
        // had to fall below 49.95, and the recovered frequency had to sit in 49.9..50.1; after: the same offsets about the
        // case's nominal (start 60.0, below 59.95, recovered in 59.9..60.1).
        double nadir = Case9NominalHz;
        for (int t = 6; t <= 120; t++) nadir = Math.Min(nadir, sim.Step(t, 0.5 / 60.0).FrequencyHz);   // 0.5 s steps
        Assert.True(nadir < Case9NominalHz - 0.05, $"a trip should visibly dip the frequency (nadir {nadir:F3})");

        // Reserve/redispatch restores it over minutes.
        double f = Case9NominalHz;
        for (int t = 121; t <= 140; t++) f = sim.Step(t, 20.0).FrequencyHz;   // coarse steps advance minutes
        Assert.InRange(f, Case9NominalHz - 0.1, Case9NominalHz + 0.1);
    }

    [Fact]
    public void Frame_carries_the_regulated_model_on_the_uk_oltc_case()
    {
        // The reduced UK model has OLTCs; the frame must carry the post-solve regulated model
        // (solved tap ratios), not the pre-solve one - the dashboard/SCADA read taps from here.
        string uk = Path.Combine(AppContext.BaseDirectory, "data", "uk");
        Assert.True(File.Exists(Path.Combine(uk, "system.json")), $"bundled file missing: {Path.Combine(uk, "system.json")}");   // GR-404: the file is committed (BundledDataPresenceTests checks it), so a missing one fails instead of passing silently
        var sim = new LiveSim(GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(uk));
        SimFrame f = sim.Step(1);
        Assert.True(f.Result.Converged);
        Assert.NotNull(f.Model);
        Assert.True(f.Model.BusCount > 0);
    }
}
