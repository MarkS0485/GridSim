using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F-025: the real-network impedance scan (UseCanonicalThevenin = false) used to throw on its first winding-grid point,
/// because the winding grid started at 1e-3 Hz, inside the driving-point solve's DC-arm guard, so it could never
/// complete. R245 moved the real-network winding floor to 2 x the guard and drops winding points on the dq DC arm. The
/// existing tests prove completion only on the 2-bus canonical Thevenin network; these prove it on REAL networks: the
/// IEEE 9- and 118-bus cases (small, run anywhere) and gb-full (3,539 buses, about 840 driving-point solves per
/// criterion; gated to the big box by the RAM cap and tagged Machine=BigBox).
///
/// <para>Each run must: complete without an exception; report every frequency's ZGrid as the real network's own
/// driving point Z(f + f0), bit for bit (never a canonical substitute); return a finite ZGrid and a finite loop gain at
/// every reporting point; and give the same verdict and samples, bit for bit, on a second run.</para>
/// </summary>
public class F025RealNetworkImpedanceScanTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    private static GridFollowingImpedance Gfl(int bus) =>
        GridFollowingImpedance.FromParameters(bus, new ConverterOperatingPoint(1.0, 0.0, 1.0, 0.2, 1.0, -0.2),
            ImpedanceModelDetail.PllOuter, ProvenanceTag.CuratedPlaceholder, 5.0, 0.707, 0.0);

    /// <summary>The real-network options at the model's own nominal frequency (Run refuses any other f0). The band
    /// 0.1 to 250 Hz at 20 points a decade keeps every reporting point at least 0.13 % away from 50 Hz and 4.7 % from
    /// 60 Hz, well clear of the 1e-4 dq DC-arm guard.</summary>
    private static ImpedanceStabilityOptions Real(GridModel m, StabilityCriterion crit) => new()
    {
        FMinHz = 0.1, FMaxHz = 250.0, PointsPerDecade = 20, F0Hz = m.FrequencyHz, Criterion = crit,
        UseCanonicalThevenin = false, Scr = 3.0, XOverR = 10.0, PRatedPu = 1.0, ScrSteps = 0,
    };

    private static void AssertCompletesOnTheRealNetwork(string label, GridModel m, int bus, StabilityCriterion crit)
    {
        ImpedanceStabilityOptions opt = Real(m, crit);
        var solver = new RealExpandedComplexSolver();

        ImpedanceStabilityResult r = ImpedanceStabilityScan.Run(opt, Gfl(bus), m, solver);   // F-025: must not throw

        Assert.False(r.Params!.Canonical, $"{label}: the scan reported the canonical Thevenin");
        Assert.NotEmpty(r.Frequency);
        Assert.True(r.Nyquist.UnstableModeCount >= 0, $"{label}: negative unstable-mode count");
        foreach (FreqSample s in r.Frequency)
        {
            Assert.True(double.IsFinite(s.ZGrid.Real) && double.IsFinite(s.ZGrid.Imaginary),
                $"{label}: ZGrid not finite at {s.FHz} Hz");
            Assert.True(double.IsFinite(s.L.Real) && double.IsFinite(s.L.Imaginary), $"{label}: L not finite at {s.FHz} Hz");
            // The real network's own positive-sequence entry Z_pp(f) = Z(f + f0), never a substitute.
            Complex real = GridImpedanceAssembler.PositiveSequence(m, bus, s.FHz + opt.F0Hz, opt.F0Hz, opt.Harmonic, solver);
            Assert.Equal(real, s.ZGrid);
        }

        // Determinism: a second run gives the same verdict and samples, bit for bit.
        ImpedanceStabilityResult again = ImpedanceStabilityScan.Run(opt, Gfl(bus), m, new RealExpandedComplexSolver());
        Assert.Equal(r.Nyquist, again.Nyquist);
        Assert.Equal(r.Frequency.Count, again.Frequency.Count);
        for (int i = 0; i < r.Frequency.Count; i++) Assert.Equal(r.Frequency[i], again.Frequency[i]);
    }

    [Theory]
    [InlineData(StabilityCriterion.Siso)]
    [InlineData(StabilityCriterion.Mimo)]
    public void F025_real_network_scan_completes_on_ieee9(StabilityCriterion crit)
        => AssertCompletesOnTheRealNetwork($"case9 {crit}", IeeeCases.Case9(), bus: 5, crit);

    [Theory]
    [InlineData(StabilityCriterion.Siso)]
    [InlineData(StabilityCriterion.Mimo)]
    public void F025_real_network_scan_completes_on_ieee118(StabilityCriterion crit)
        => AssertCompletesOnTheRealNetwork($"case118 {crit}", IeeeCases.Case118(), bus: 59, crit);

    /// <summary>gb-full. The converter sits at the lowest-numbered non-slack bus of 275 kV or above (a fixed, data-driven
    /// choice, so the test does not hard-code a bus id that a data refresh could remove).</summary>
    [LargeCaseTheory(CaseSizes.GbFull, Engine.Sparse)]
    [Trait("Machine", "BigBox")]
    [InlineData(StabilityCriterion.Siso)]
    [InlineData(StabilityCriterion.Mimo)]
    public void F025_real_network_scan_completes_on_gb_full(StabilityCriterion crit)
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(DataDir, "gb-full"));
        Assert.Equal(CaseSizes.GbFull, m.BusCount);
        Bus? pcc = m.Buses.Where(b => b.Type != BusType.Slack && b.BaseKv >= 275.0).OrderBy(b => b.Id).FirstOrDefault();
        Assert.True(pcc is not null, "gb-full has no non-slack bus at 275 kV or above");
        AssertCompletesOnTheRealNetwork($"gb-full bus {pcc!.Id} {crit}", m, pcc.Id, crit);
    }
}
