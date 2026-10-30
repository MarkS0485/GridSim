using System;
using System.IO;
using System.Linq;
using GridSim.Core.Harmonics;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N006 (October 2026): the gb-full harmonic study's worst bus, 4992 "SILSDEN T2" (132 kV), shows a driving-point |Z| of
/// 157.445 pu at h = 4.9818 (fine sweep). Diagnosis: a GENUINE parallel (LC) resonance of the model data as it stands,
/// computed correctly by the engine, NOT an engine defect. The data around it is a model-build artefact (recorded for
/// main as a new finding), so no code changed.
///
/// <para>The pocket: bus 33 "Bradford West" 275 kV -- the synthesised SGT share "SGT BRAW_1 NPGY:Bradford West x3/8@4720"
/// (r 0, x 0.2222 pu on 100 MVA) -- 4720 "BINGLEY GT2" 132 kV -- line (x 0.01652, b 0.10761) -- 4740 "BRADFORD WEST
/// SGT1" -- two lines to OPEN ENDS 4843 "HOLMFIELD T2" (b 0.02647) and 4992 "SILSDEN T2" (b 0.03813). None of the four
/// 132 kV buses carries load, a shunt or a stamped machine (the only unit, at 4720, is a power-flow-only GSP voltage
/// control), so nothing damps the tank. Lossless estimate h_r = 1 / sqrt(X * B), B = 0.17221 pu: 5.112 with the
/// transformer alone, 4.932 with the 4720-4740 line's series reactance added; the engine's 4.9818 lies between (the
/// 275 kV network adds a little source reactance). The circuit-end buses "SILSDEN T2" / "HOLMFIELD T2" are not joined to
/// the "SILSDEN GT2" / "HOLMFIELD GT2" transformer buses that carry the substations' demand: that missing join is why the
/// pocket is unloaded. These tests pin the topology facts and the physical bracket, so a data fix (or an engine change)
/// that moves the resonance is seen.</para>
/// </summary>
public sealed class N006GbFullSilsdenResonanceTests
{
    private static readonly Lazy<GridModel> GbFull = new(() =>
        JsonGridLoader.LoadDirectory(Path.Combine(S10TestSupport.DataDir, "gb-full")));

    private static Branch[] BranchesAt(GridModel m, int bus) =>
        m.Branches.Where(b => b.InService && (b.From == bus || b.To == bus)).ToArray();

    private static void AssertUnloaded(GridModel m, int busId)
    {
        Bus b = m.Buses.Single(x => x.Id == busId);
        Assert.Equal(132.0, b.BaseKv);
        Assert.Equal((0.0, 0.0, 0.0, 0.0), (b.Pd, b.Qd, b.Gs, b.Bs));
        Assert.All(m.Generators.Where(g => g.Bus == busId), g => Assert.True(g.PowerFlowOnly, $"unit {g.Name} at {busId} is stamped"));
    }

    [Fact]
    public void The_resonant_pocket_is_an_unloaded_radial_132kV_chain_with_two_open_ends()
    {
        GridModel m = GbFull.Value;
        foreach (int id in new[] { 4720, 4740, 4843, 4992 }) AssertUnloaded(m, id);

        // 4992 and 4843: open ends, one line each, to 4740.
        Branch s = Assert.Single(BranchesAt(m, 4992));
        Assert.False(s.IsTransformer);
        Assert.Equal(4740, s.From);
        Assert.Equal(0.038130000000000004, s.B);
        Branch h = Assert.Single(BranchesAt(m, 4843));
        Assert.Equal(4740, h.From);

        // 4740: only those two and the line to 4720.
        Assert.Equal(new[] { 4720, 4843, 4992 },
            BranchesAt(m, 4740).Select(b => b.From == 4740 ? b.To : b.From).OrderBy(x => x).ToArray());

        // 4720: the line to 4740 and the synthesised SGT share to the 275 kV bus 33, x = 0.2222 pu, r = 0.
        Branch[] at4720 = BranchesAt(m, 4720);
        Assert.Equal(2, at4720.Length);
        Branch sgt = Assert.Single(at4720, b => b.IsTransformer);
        Assert.Equal((33, 0.0, 0.2222222222222222), (sgt.From, sgt.R, sgt.X));
        Assert.Equal(275.0, m.Buses.Single(x => x.Id == 33).BaseKv);
    }

    [Fact]
    public void The_fine_sweep_peak_lies_inside_the_lossless_lc_bracket()
    {
        GridModel m = GbFull.Value;
        FrequencySweepResult sweep = HarmonicFrequencySweep.DrivingPoint(m, 4992, HarmonicModelOptions.Study);
        double b = 0.10761 + 0.026469999999999997 + 0.038130000000000004;
        double hHigh = 1.0 / Math.Sqrt(0.2222222222222222 * b);              // transformer only: 5.112
        double hLow = 1.0 / Math.Sqrt((0.2222222222222222 + 0.01652) * b);    // plus the line's series X: 4.932
        Assert.InRange(sweep.PeakOrder, hLow, hHigh);
        Assert.True(sweep.PeakMagnitude > 100.0, $"|Z| peak {sweep.PeakMagnitude:R} pu");
    }
}
