using GridSim.Core.Numerics;
using GridSim.Core.Physics.Experimental;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The experimental continuum electromechanical-wave model - the "grid as a damped-wave medium" lens.
/// The load-bearing invariant is that it COLLAPSES to the established small-signal physics: its
/// dominant normal-mode frequency must equal the analytic SMIB oscillation frequency
/// omega_n = sqrt(omega_s * P_s / 2H) that <see cref="GridSim.Core.Dynamics.Distributed.ClassicalMultiMachine"/>
/// is validated against, and the two-mass reduced-mass formula. Also pins the supporting Jacobi
/// eigensolver.
/// </summary>
public class ContinuumWaveModelTests
{
    private const double F0 = 50.0;
    private static double Ws => 2.0 * System.Math.PI * F0;

    [Fact]
    public void Eigensolver_matches_known_eigenvalues()
    {
        // A symmetric 2x2 with eigenvalues 1 and 3.
        var a = new double[,] { { 2, 1 }, { 1, 2 } };
        double[] e = SymmetricEigen.Eigenvalues(a);
        Assert.Equal(1.0, e[0], 12);
        Assert.Equal(3.0, e[1], 12);

        // A diagonal matrix returns its diagonal, sorted.
        var d = new double[,] { { 5, 0, 0 }, { 0, -1, 0 }, { 0, 0, 2 } };
        double[] ed = SymmetricEigen.Eigenvalues(d);
        Assert.Equal(new[] { -1.0, 2.0, 5.0 }, ed, new DoubleComparer(1e-12));
    }

    [Fact]
    public void SMIB_dominant_mode_matches_the_analytic_small_signal_frequency()
    {
        // One finite machine against an "infinite bus" (huge H), coupled by a synchronising coefficient
        // Ps. The analytic small-signal mode is f_n = (1/2pi) sqrt(omega_s * Ps / 2H) - the exact oracle
        // ClassicalMultiMachine is checked against.
        const double h = 3.5, ps = 1.732; // MW/rad synchronising coefficient
        var hArr = new[] { h, 1e8 };
        var k = new double[,] { { 0, ps }, { ps, 0 } };

        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(hArr, k, F0);

        double analytic = System.Math.Sqrt(Ws * ps / (2.0 * h)) / (2.0 * System.Math.PI);
        Assert.Equal(0.0, w.CoiModeHz, 4);                 // the null (COI) mode
        Assert.Equal(analytic, w.DominantInterAreaHz, 3);  // collapses to the SMIB oracle
    }

    [Fact]
    public void Two_equal_masses_match_the_reduced_mass_formula()
    {
        // Two identical machines coupled by K: f_n = (1/2pi) sqrt(K (1/m1 + 1/m2)), m = 2H/ws.
        const double h = 4.0, kc = 2.0;
        var hArr = new[] { h, h };
        var k = new double[,] { { 0, kc }, { kc, 0 } };

        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(hArr, k, F0);

        double m = 2.0 * h / Ws;
        double analytic = System.Math.Sqrt(kc * (1.0 / m + 1.0 / m)) / (2.0 * System.Math.PI);
        Assert.Equal(analytic, w.DominantInterAreaHz, 3);
        Assert.Single(w.InterAreaModesHz); // 2 masses -> 1 COI mode + 1 inter-area mode
    }

    [Fact]
    public void The_dominant_mode_shape_swings_the_two_machines_against_each_other()
    {
        // Two coupled machines: the inter-area mode is anti-phase (opposite signs) - they swing against
        // each other. With ids supplied, the shape is labelled by machine.
        var hArr = new[] { 4.0, 4.0 };
        var k = new double[,] { { 0, 2.0 }, { 2.0, 0 } };
        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(hArr, k, F0, d: null, machineIds: new[] { 11, 22 });

        Assert.Equal(2, w.DominantModeShape.Count);
        double a0 = w.DominantModeShape.Single(s => s.Machine == 11).Amplitude;
        double a1 = w.DominantModeShape.Single(s => s.Machine == 22).Amplitude;
        Assert.True(a0 * a1 < 0, $"the two machines should swing against each other: {a0} vs {a1}");
        // Symmetric masses -> equal-and-opposite amplitudes.
        Assert.Equal(System.Math.Abs(a0), System.Math.Abs(a1), 6);
    }

    [Fact]
    public void The_infinite_bus_barely_moves_in_the_SMIB_mode()
    {
        var hArr = new[] { 3.5, 1e8 };
        var k = new double[,] { { 0, 1.7 }, { 1.7, 0 } };
        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(hArr, k, F0, d: null, machineIds: new[] { 1, 2 });
        double finite = System.Math.Abs(w.DominantModeShape.Single(s => s.Machine == 1).Amplitude);
        double infinite = System.Math.Abs(w.DominantModeShape.Single(s => s.Machine == 2).Amplitude);
        Assert.True(finite > 100 * infinite, $"the infinite bus should barely move: finite {finite}, infinite {infinite}");
    }

    [Fact]
    public void Damping_gives_a_Reynolds_analogue_below_one_when_underdamped()
    {
        var hArr = new[] { 3.5, 1e8 };
        var k = new double[,] { { 0, 1.7 }, { 1.7, 0 } };
        // Light damping -> underdamped (oscillatory / "high Reynolds") -> zeta < 1.
        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(hArr, k, F0, d: new[] { 0.02, 0.0 });
        Assert.InRange(w.DominantDampingRatio, 0.0, 1.0);
        Assert.True(w.DominantDampingRatio > 0);
    }

    private sealed class DoubleComparer : IEqualityComparer<double>
    {
        private readonly double _tol;
        public DoubleComparer(double tol) => _tol = tol;
        public bool Equals(double x, double y) => System.Math.Abs(x - y) <= _tol;
        public int GetHashCode(double obj) => 0;
    }
}
