using System;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N062 / R059 (October 2026): the GridModel adapter. A balanced lift of a radial positive-sequence case must solve to
/// the same voltages as GridSim's positive-sequence Newton power flow (on phase a; phases b and c are phase a rotated
/// by -120 and +120 degrees), because in per-unit the per-phase equations are the positive-sequence equations. Checked
/// on the Baran and Wu 33-bus feeder (data/matpower/dist/case33bw.m), with and without ZIP loads. What the sweep
/// cannot model is refused.
/// </summary>
public class MultiPhaseGridModelAdapterTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    // The file gives branch impedances in ohms and loads in kW (its MATLAB tail converts them); the unit normaliser does
    // that conversion, exactly as every product load path does (Gated.Matpower).
    private static GridModel Case33() =>
        MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(Path.Combine(Root(), "data", "matpower", "dist", "case33bw.m")));

    // Both solves converge far below the comparison tolerance (Newton to 1e-12 pu mismatch, the sweep to 1e-13 pu).
    private const double Tol = 1e-9;

    private static void AgreesWithNewton(GridModel m)
    {
        MultiPhaseFeeder f = MultiPhaseGridModelAdapter.FromGridModel(m, m.Name);
        MultiPhaseResult r = new ThreePhaseSweep().Solve(f.Model, new ThreePhaseSweepOptions { Tolerance = 1e-13, MaxIterations = 2000 });
        Assert.True(r.Converged, $"sweep: {r.Status} after {r.Iterations}");
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12, MaxIterations = 50 });
        Assert.True(pf.Converged);
        double worst = 0;
        for (int i = 0; i < pf.BusIds.Count; i++)
        {
            int k = r.BusIds.ToList().IndexOf(pf.BusIds[i]);
            Complex expected = Complex.FromPolarCoordinates(pf.Vm[i], pf.VaDeg[i] * Math.PI / 180.0);
            worst = Math.Max(worst, (r.V[k][0] - expected).Magnitude);
            // Balanced: b and c are a rotated by -120 and +120 degrees.
            Complex rot = Complex.FromPolarCoordinates(1.0, -2.0 * Math.PI / 3.0);
            Assert.True((r.V[k][1] - r.V[k][0] * rot).Magnitude < 1e-12);
            Assert.True((r.V[k][2] - r.V[k][0] * Complex.Conjugate(rot)).Magnitude < 1e-12);
        }
        Assert.True(worst < Tol, $"largest difference from the Newton solution {worst:R} pu");
    }

    [Fact]
    public void The_balanced_lift_of_case33bw_solves_to_the_newton_voltages()
    {
        GridModel m = Case33();
        AgreesWithNewton(m);
        MultiPhaseFeeder f = MultiPhaseGridModelAdapter.FromGridModel(m, "case33bw");
        Assert.Equal(33, f.Model.Buses.Count);
        Assert.Equal(32, f.Model.Branches.Count);   // the five tie lines have status 0 in the file and are not loaded
    }

    [Fact]
    public void A_zip_load_is_lifted_exactly()
    {
        GridModel m = Case33();
        var zip = new ZipLoad(Ap: 0.3, Bp: 0.3, Cp: 0.4, Aq: 0.5, Bq: 0.2, Cq: 0.3);
        GridModel z = m.CopyWith(buses: m.Buses.Select(b => b.Pd > 0 && b.Id % 3 == 0 ? b with { Zip = zip } : b).ToList());
        MultiPhaseFeeder f = MultiPhaseGridModelAdapter.FromGridModel(z, "zip");
        Assert.Contains(f.Model.Loads, l => l.Model == PhaseLoadModel.ConstantCurrent);
        Assert.Contains(f.Model.Loads, l => l.Model == PhaseLoadModel.ConstantImpedance);
        AgreesWithNewton(z);
    }

    [Fact]
    public void A_capacitor_and_line_charging_are_lifted_exactly()
    {
        GridModel m = Case33();
        GridModel c = m.CopyWith(
            buses: m.Buses.Select(b => b.Id == 18 ? b with { Bs = 0.3 } : b).ToList(),
            branches: m.Branches.Select(br => br.InService ? br with { B = 0.002 } : br).ToList());
        MultiPhaseFeeder f = MultiPhaseGridModelAdapter.FromGridModel(c, "cap");
        Assert.Single(f.Model.Capacitors);
        Assert.All(f.Model.Branches, b => Assert.NotNull(b.ShuntY));
        AgreesWithNewton(c);
    }

    [Fact]
    public void What_the_sweep_cannot_model_is_refused()
    {
        // case9 has voltage-controlled buses.
        Assert.Contains("PV", Assert.Throws<ArgumentException>(() => MultiPhaseGridModelAdapter.FromGridModel(IeeeCases.Case9(), "case9")).Message, StringComparison.Ordinal);

        GridModel m = Case33();
        GridModel tapped = m.CopyWith(branches: m.Branches.Select((br, k) => k == 3 ? br with { TapRatio = 1.05 } : br).ToList());
        Assert.Contains("tap", Assert.Throws<ArgumentException>(() => MultiPhaseGridModelAdapter.FromGridModel(tapped, "t")).Message, StringComparison.Ordinal);

        GridModel reactor = m.CopyWith(buses: m.Buses.Select(b => b.Id == 5 ? b with { Bs = -0.1 } : b).ToList());
        Assert.Contains("reactor", Assert.Throws<ArgumentException>(() => MultiPhaseGridModelAdapter.FromGridModel(reactor, "r")).Message, StringComparison.Ordinal);

        // Close one of the file's tie lines (18-33, 0.5 + j0.5 ohm), which makes a loop.
        GridModel meshed = m.CopyWith(branches: m.Branches.Append(new Branch(18, 33, 0.0312, 0.0312)).ToList());
        Assert.Contains("radial", Assert.Throws<ArgumentException>(() => MultiPhaseGridModelAdapter.FromGridModel(meshed, "m")).Message, StringComparison.Ordinal);
    }
}
