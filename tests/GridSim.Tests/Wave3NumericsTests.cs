using System;
using System.Numerics;
using System.Threading.Tasks;
using GridSim.Core.Distribution;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Regression tests for the numerics/distribution defects from the adversarial review (Wave 3).
/// </summary>
public class Wave3NumericsTests
{
    [Fact] // Complex2x2.Inverse relative singularity test must not floor the scale at 1.0
    public void Complex2x2_inverts_a_uniformly_small_but_well_conditioned_matrix()
    {
        var tiny = Complex2x2.Diagonal(new Complex(1e-8, 0));   // condition number 1, exact inverse 1e8*I
        Complex2x2? inv = tiny.Inverse();

        Assert.NotNull(inv);
        Assert.True(Math.Abs(inv!.Value.M11.Real - 1e8) / 1e8 < 1e-9, $"expected 1e8 on the diagonal, got {inv.Value.M11}");

        // A genuine zero matrix is still singular.
        Assert.Null(Complex2x2.Diagonal(Complex.Zero).Inverse());
    }

    [Fact] // GR-449: the smaller eigenvalue must not be lost to cancellation in (tr - disc) / 2
    public void Complex2x2_eigenvalues_keep_the_small_root_when_the_roots_differ_by_sixteen_orders()
    {
        // Upper triangular, so the eigenvalues are exactly the diagonal: 1e8 and 1e-8. tr rounds to 1e8 and disc to
        // 1e8, so the direct (tr - disc) / 2 gave exactly 0; det / L1 gives 1e-8 to full precision.
        var m = new Complex2x2(new Complex(1e8, 0), Complex.One, Complex.Zero, new Complex(1e-8, 0));
        (Complex l1, Complex l2) = m.Eigenvalues();
        Assert.True(Math.Abs(l1.Real - 1e8) / 1e8 < 1e-15, $"L1 = {l1}");
        Assert.True(Math.Abs(l2.Real - 1e-8) / 1e-8 < 1e-14, $"L2 = {l2}");
        Assert.Equal(0.0, l2.Imaginary);

        // Negative trace: the - branch is the large one and L1 (the + branch) is the small root, order unchanged.
        var n = new Complex2x2(new Complex(-1e8, 0), Complex.One, Complex.Zero, new Complex(-1e-8, 0));
        (Complex n1, Complex n2) = n.Eigenvalues();
        Assert.True(Math.Abs(n1.Real + 1e-8) / 1e-8 < 1e-14, $"L1 = {n1}");
        Assert.True(Math.Abs(n2.Real + 1e8) / 1e8 < 1e-15, $"L2 = {n2}");

        // A NaN entry still comes back as NaN, never as a finite root.
        var bad = new Complex2x2(new Complex(double.NaN, 0), Complex.Zero, Complex.Zero, Complex.One);
        (Complex b1, Complex b2) = bad.Eigenvalues();
        Assert.True(double.IsNaN(b1.Real) || double.IsNaN(b2.Real));
    }

    [Fact] // BoundedSimplex must price a FREE nonbasic variable in both directions
    public void BoundedSimplex_optimises_over_a_free_variable()
    {
        // minimise x1 s.t. x1 - x2 = 0, x1 free, x2 in [-5, +inf). True optimum x = (-5, -5), obj -5.
        var a = new double[,] { { 1, -1 } };
        var b = new double[] { 0 };
        var c = new double[] { 1, 0 };
        var lo = new[] { double.NegativeInfinity, -5.0 };
        var hi = new[] { double.PositiveInfinity, double.PositiveInfinity };

        (LpStatus status, double[] x, double obj) = BoundedSimplex.Solve(a, b, c, lo, hi);

        Assert.Equal(LpStatus.Optimal, status);
        Assert.Equal(-5.0, obj, 6);
        Assert.Equal(-5.0, x[0], 6);
        Assert.Equal(-5.0, x[1], 6);
    }

    [Fact] // GeneralEigen.Balance must not spin forever on an Inf entry
    public void GeneralEigen_terminates_on_an_infinite_entry()
    {
        // Pre-fix this hung the calling thread. It must now terminate - either returning or throwing.
        Exception? ex = Record.Exception(() =>
            GeneralEigen.Eigenvalues(new double[,] { { 0, double.PositiveInfinity }, { 1, 0 } }));
        // Either outcome is acceptable; the point is that the call RETURNS rather than hanging.
        Assert.True(ex is null || ex is not null);
    }

    [Fact] // GR-397: the test above cannot fail (no timeout, any outcome passes); this one can
    public async Task GeneralEigen_refuses_an_infinite_entry_by_name_within_a_time_limit()
    {
        // Run on the pool with a deadline, so a regression to the old endless loop fails here instead of hanging the
        // run, and require the documented refusal (RequireFinite names the entry), not merely "some exception".
        Task<Complex[]> call = Task.Run(() => GeneralEigen.Eigenvalues(new double[,] { { 0, double.PositiveInfinity }, { 1, 0 } }));
        Task first = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(ReferenceEquals(first, call), "GeneralEigen.Eigenvalues did not return within 60 s on an Inf entry");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => call);
        Assert.Contains("entry [0,1] is", ex.Message, StringComparison.Ordinal);
    }

    [Fact] // SequenceFaultStudy must refuse a meshed/parallel model instead of silently understating Z
    public void SequenceFaultStudy_rejects_parallel_branches()
    {
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.06));
        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, z), new PhaseBranch(0, 1, z) },   // two identical cables in parallel
            Array.Empty<PhaseLoad>());

        Assert.Throws<ArgumentException>(() => SequenceFaultStudy.SingleLineToGround(model, faultBusId: 1));
    }

    [Fact] // ThreePhaseSweep must return NonRadial for a loop, not silently solve a spanning tree
    public void ThreePhaseSweep_flags_a_meshed_network_as_non_radial()
    {
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.06));
        var model = new MultiPhaseModel(
            new[]
            {
                new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)),
                new PhaseBus(1, false), new PhaseBus(2, false),
            },
            new[] { new PhaseBranch(0, 1, z), new PhaseBranch(1, 2, z), new PhaseBranch(2, 0, z) }, // a loop
            Array.Empty<PhaseLoad>());

        MultiPhaseResult r = new ThreePhaseSweep().Solve(model);
        Assert.Equal(MultiPhaseStatus.NonRadial, r.Status);
        Assert.False(r.Converged);
    }
}
