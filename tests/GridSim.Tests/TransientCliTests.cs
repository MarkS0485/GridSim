using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The `transient` command's engine: LiveSim's fine-slice, trace-capturing time-stepping. The 3-arg
/// <see cref="LiveSim.Step(int,double,SolveTrace?)"/> must warm-solve every slice, capture the exact math
/// on demand (even on small cases where the internal options are null), stay bit-identical to the two-arg
/// path when no trace is asked for, and play out a trip transient at 1 ms.
/// </summary>
public class TransientCliTests
{
    private const double MsStep = 1.0 / 60000.0;   // 1 ms expressed in minutes

    /// <summary>The nominal frequency LiveSim runs case9 at: its own, 60 Hz since D2 (N273).</summary>
    private static readonly double Case9NominalHz = IeeeCases.Case9().FrequencyHz;

    [Fact]
    public void Fine_1ms_slices_hold_steady_and_converge()
    {
        var sim = new LiveSim(IeeeCases.Case9(), holdSteady: true);
        for (int t = 1; t <= 1000; t++)
        {
            SimFrame f = sim.Step(t, MsStep);
            Assert.True(f.Result.Converged, $"slice {t} did not converge");
            // Slice 1 is a cold start; from slice 2 the warm-start should converge in <=2 iterations.
            Assert.True(f.Result.Iterations <= (t == 1 ? 8 : 2), $"slice {t}: {f.Result.Iterations} iters");
            // Moved 2026-10 (B2 N273, Ruling R1: Mark delegated EE calls 2026-10-04; source: D2 / N017, the WSCC 9-bus case is a
            // 60 Hz system (Anderson and Fouad, ch. 2), and N273, the live loop runs at the model's nominal with the GB bands
            // about it): the live loop used to run case9 at the GB 50 Hz, so this pinned 50 Hz values. Before:
            // InRange(49.5, 50.5); after: the same +/-0.5 Hz about the case's nominal, InRange(59.5, 60.5).
            Assert.InRange(f.FrequencyHz, Case9NominalHz - 0.5, Case9NominalHz + 0.5);
        }
    }

    [Fact]
    public void Trace_captures_the_math_even_on_a_small_case()
    {
        // case9 is small, so LiveSim's internal PowerFlowOptions is null; the 3-arg Step must still
        // materialise options with the trace and capture - this is exactly what --enableExport needs.
        var sim = new LiveSim(IeeeCases.Case9(), holdSteady: true);
        sim.Step(1, MsStep);                       // warm up
        var trace = new SolveTrace();
        SimFrame f = sim.Step(2, MsStep, trace);

        Assert.Equal("dense-newton", trace.SolverName);
        Assert.NotEmpty(trace.YBus);

        var meta = new SolveExportMeta("case9", "cpu", "test", "2026-07-02T00:00:00Z", 1.0, "host");
        using JsonDocument doc = JsonDocument.Parse(SolveExport.ToJson(f.Model, f.Result, trace, meta));
        Assert.Equal("gridsim-solve-export/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.True(doc.RootElement.GetProperty("solve").GetProperty("ybus").GetArrayLength() > 0);
    }

    [Fact]
    public void Null_trace_is_bit_identical_to_the_two_arg_step()
    {
        var a = new LiveSim(IeeeCases.Case9(), holdSteady: true);
        var b = new LiveSim(IeeeCases.Case9(), holdSteady: true);
        for (int t = 1; t <= 50; t++)
        {
            SimFrame fa = a.Step(t, MsStep);           // 2-arg
            SimFrame fb = b.Step(t, MsStep, null);     // 3-arg with null trace
            for (int i = 0; i < fa.Result.Vm.Length; i++)
            {
                Assert.Equal(fa.Result.Vm[i], fb.Result.Vm[i]);
                Assert.Equal(fa.Result.VaDeg[i], fb.Result.VaDeg[i]);
            }
        }
    }

    [Fact]
    public void A_trip_dips_the_frequency_during_a_fine_run()
    {
        var sim = new LiveSim(IeeeCases.Case9(), holdSteady: true);
        for (int t = 1; t <= 50; t++) sim.Step(t, MsStep);   // settle
        sim.TripInfeed(50);                                   // lose 50 MW of infeed
        // Moved 2026-10 (B2 N273, Ruling R1: Mark delegated EE calls 2026-10-04; source: D2 / N017, the WSCC 9-bus case is a
        // 60 Hz system (Anderson and Fouad, ch. 2), and N273, the live loop runs at the model's nominal with the GB bands
        // about it): the live loop used to run case9 at the GB 50 Hz, so this pinned 50 Hz values. Before: the nadir started at
        // 50.0 and had to fall below 49.9 (with case9 at 60 Hz the old start would have pinned the nadir at 50.0 for good);
        // after: start at the case's nominal and fall more than 0.1 Hz below it.
        double nadir = Case9NominalHz;
        for (int t = 51; t <= 15050; t++) nadir = Math.Min(nadir, sim.Step(t, MsStep).FrequencyHz);
        Assert.True(nadir < Case9NominalHz - 0.1, $"a trip should dip the frequency at 1 ms (nadir {nadir:F3} Hz)");
    }
}
