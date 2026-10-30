using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Qualitative weak-grid invariants of the SCR sweep. The ABSOLUTE critical-SCR value is [VERIFY]-pending an
/// EMT-fitted number (Workstream E) and a published GFL benchmark (Sun 2011; Wen/Boroyevich 2016), so it is
/// NOT asserted here; what is load-bearing is the DIRECTION - a STRONG grid is always stable while a weak grid
/// carries an instability region - together with the decisive Routh agreement in
/// <see cref="ImpedanceModalAgreementTests"/>. (The instability need not be a single monotone crossing: this
/// converter model exhibits a weak-grid instability WINDOW, which is a physically legitimate signature.)
/// </summary>
public class ImpedanceScrReferenceTests
{
    private static ImpedanceStabilityResult Sweep(double id0, double iq0, double fbwHz)
    {
        var op = new ConverterOperatingPoint(1.0, 0.0, id0, iq0, id0, -iq0);
        var conv = GridFollowingImpedance.FromParameters(2, op, ImpedanceModelDetail.PllOuter,
            ProvenanceTag.CuratedPlaceholder, fbwHz, 0.707, 0.0);
        return ImpedanceStabilityScan.Run(new ImpedanceStabilityOptions
        {
            FMinHz = 0.1, FMaxHz = 250, PointsPerDecade = 60, F0Hz = 50, Criterion = StabilityCriterion.Mimo,
            UseCanonicalThevenin = true, Scr = 1.0, XOverR = 10.0, PRatedPu = 1.0,
            ScrMin = 0.15, ScrMax = 6.0, ScrSteps = 60,
        }, conv);
    }

    [Fact]
    public void Strong_grid_is_stable_and_a_weak_grid_instability_region_exists()
    {
        ImpedanceStabilityResult r = Sweep(id0: 1.0, iq0: 0.3, fbwHz: 30.0);

        // A strong grid (SCR >= 1.5) is unconditionally stable - the robust, direction-of-effect invariant.
        foreach (ScrSample s in r.ScrSweep)
            if (s.Scr >= 1.5) Assert.True(s.Stable, $"strong grid (SCR {s.Scr:F2}) must be stable");

        // A weak-grid instability region exists, and the boundary crossing is reported.
        Assert.Contains(r.ScrSweep, s => !s.Stable && s.Scr < 1.5);
        Assert.True(r.BoundaryFound, "expected an unstable->stable SCR boundary crossing");
        Assert.InRange(r.CriticalScr, 0.15, 6.0);   // absolute value is [VERIFY]-pending EMT calibration
    }
}
