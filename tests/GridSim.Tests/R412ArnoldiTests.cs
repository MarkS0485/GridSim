using System.Numerics;
using System.Security.Cryptography;
using GridSim.Core.Dynamics.SmallSignal.Full;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-12 worked cases 3 and 4 and the determinism and residual-gate contracts of the shift-invert Arnoldi solver.
/// Case 3 oracle: the Toeplitz tridiagonal closed form (no incumbent). Case 4 oracle: the incumbent dense solver on a
/// 300-state assembled system.
/// </summary>
public sealed class R412ArnoldiTests
{
    private static BandedArnoldiOperator Toeplitz(int n, double a, double b, double c)
        => new(n, 1, 1, (i, j) => i == j ? a : i > j ? b : c);

    private static double Closed(int k, int n, double a, double b, double c) => a + 2.0 * Math.Sqrt(b * c) * Math.Cos(k * Math.PI / (n + 1));

    [Fact]
    public void Toeplitz_tridiagonal_n2000_requested_eigenvalues_match_the_closed_form_to_1e9()
    {
        // eigenvalues a + 2 sqrt(bc) cos(k pi / (n+1)); symmetric (b = c) keeps the problem well conditioned at n = 2000
        const int n = 2000; const double a = 0.3, b = 0.7, c = 0.7;
        var all = Enumerable.Range(1, n).Select(k => Closed(k, n, a, b, c)).ToArray();
        const double sigma = 0.5;
        double[] nearest = all.OrderBy(x => Math.Abs(x - sigma)).Take(6).OrderBy(x => x).ToArray();

        ArnoldiResult res = ArnoldiEigen.Compute(Toeplitz(n, a, b, c), new Complex(sigma, 0.0), new ArnoldiOptions(6, 40, 400, 1e-12));
        Assert.True(res.Converged, $"restarts {res.Restarts}");
        double[] got = res.Pairs.Select(p => p.Value.Real).OrderBy(x => x).ToArray();
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(nearest[i], got[i], 9);
        }
        foreach (ArnoldiPair p in res.Pairs)
        {
            Assert.True(p.PassesGate);
            Assert.True(Math.Abs(p.Value.Imaginary) < 1e-9);
        }
    }

    [Fact]
    public void Toeplitz_with_a_complex_shift_finds_the_eigenvalues_nearest_it()
    {
        const int n = 200; const double a = -0.2, b = 0.5, c = 0.5;
        var all = Enumerable.Range(1, n).Select(k => Closed(k, n, a, b, c)).ToArray();
        var sigma = new Complex(0.31, 0.01);
        double[] nearest = all.OrderBy(x => Complex.Abs(new Complex(x, 0) - sigma)).Take(3).OrderBy(x => x).ToArray();
        ArnoldiResult res = ArnoldiEigen.Compute(Toeplitz(n, a, b, c), sigma, new ArnoldiOptions(3, 40, 400, 1e-11));
        Assert.True(res.Converged);
        double[] got = res.Pairs.Select(p => p.Value.Real).OrderBy(x => x).ToArray();
        for (int i = 0; i < 3; i++) Assert.Equal(nearest[i], got[i], 9);
    }

    [Fact]
    public void A_shift_that_is_an_eigenvalue_is_moved_by_the_fixed_rule_and_still_converges()
    {
        const int n = 300; const double a = 0.0, b = 1.0, c = 1.0;
        double lam = Closed(150, n, a, b, c);
        ArnoldiResult res = ArnoldiEigen.Compute(Toeplitz(n, a, b, c), new Complex(lam, 0.0), new ArnoldiOptions(3, 20, 400, 1e-8));
        Assert.True(res.Converged, $"restarts {res.Restarts} residuals {string.Join(", ", res.Pairs.Select(p => p.RelativeResidual.ToString("E2") + "@" + p.Value))} shift {res.Shift}");
        Assert.Contains(res.Pairs, p => Math.Abs(p.Value.Real - lam) < 1e-8);
    }

    [Fact]
    public void Arnoldi_is_bit_identical_across_runs_and_thread_counts()
    {
        const int n = 800;
        BandedArnoldiOperator op = Toeplitz(n, 0.1, 0.8, 0.8);
        string Run()
        {
            ArnoldiResult r = ArnoldiEigen.Compute(op, new Complex(0.4, 0.0), new ArnoldiOptions(5, 24, 400, 1e-12));
            var bytes = new List<byte>();
            foreach (ArnoldiPair p in r.Pairs)
            {
                bytes.AddRange(BitConverter.GetBytes(p.Value.Real));
                bytes.AddRange(BitConverter.GetBytes(p.Value.Imaginary));
                foreach (Complex z in p.Vector) { bytes.AddRange(BitConverter.GetBytes(z.Real)); bytes.AddRange(BitConverter.GetBytes(z.Imaginary)); }
            }
            bytes.AddRange(BitConverter.GetBytes(r.Restarts));
            bytes.AddRange(BitConverter.GetBytes(r.MatVecs));
            return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        }
        string reference = Run();
        Assert.Equal(reference, Run());                                   // run to run
        var results = new string[8];
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => results[i] = Run());   // eight concurrent runs
        Assert.All(results, r => Assert.Equal(reference, r));
        var one = new string[3];
        Parallel.For(0, 3, new ParallelOptions { MaxDegreeOfParallelism = 1 }, i => one[i] = Run());       // one thread
        Assert.All(one, r => Assert.Equal(reference, r));
    }

    [Fact]
    public void A_different_start_seed_gives_a_different_path_but_the_same_eigenvalues_to_the_gate()
    {
        BandedArnoldiOperator op = Toeplitz(500, 0.1, 0.8, 0.8);
        ArnoldiResult r1 = ArnoldiEigen.Compute(op, new Complex(0.4, 0.0), new ArnoldiOptions(4, 20, 400, 1e-12, 1UL));
        ArnoldiResult r2 = ArnoldiEigen.Compute(op, new Complex(0.4, 0.0), new ArnoldiOptions(4, 20, 400, 1e-12, 2UL));
        Assert.True(r1.Converged && r2.Converged);
        for (int i = 0; i < 4; i++) Assert.Equal(r1.Pairs[i].Value.Real, r2.Pairs[i].Value.Real, 9);
    }

    // ---- case 4: dense versus Arnoldi on a 300-state assembled system ---------------------------------------------------------

    private static FullSmallSignalModel Assembled300()
    {
        (GridSim.Core.Model.GridModel model, GridSim.Core.PowerFlow.PowerFlowResult pf) = R412Fixtures.Ring(30);
        var specs = new List<FullMachineSpec>();
        for (int i = 0; i < model.Generators.Count; i++)
            specs.Add(new FullMachineSpec(i, FullMachineModel.FluxDecay, Xdp: 0.3, Xd: 1.6, Xq: 1.5, Tdop: 6.0, DampingSystemPu: 1.0,
                Exciter: GridSim.Core.Dynamics.UserModels.UserModelLibrary.Load("SEXS", new Dictionary<string, double> { ["K"] = 60.0 }),
                Governor: GridSim.Core.Dynamics.UserModels.UserModelLibrary.Load("TGOV1"),
                Stabiliser: GridSim.Core.Dynamics.UserModels.UserModelLibrary.Load("PSS1A")));
        return FullModelAssembler.Assemble(model, pf, specs);
    }

    [Fact]
    public void Dense_and_Arnoldi_agree_on_a_300_state_assembled_system()
    {
        FullSmallSignalModel m = Assembled300();
        Assert.Equal(300, m.Order);
        Assert.True(m.AllControllersLinearised, string.Join("; ", m.NotLinearised.Select(x => x.Reason)));

        // oracle: the incumbent dense Francis-QR eigenvalues
        Complex[] dense = GeneralEigen.Eigenvalues(m.A).ToArray();

        var region = ModalRegion.FromCorners(-3.0, 1.0, 0.5, 14.0);
        FullModalResult r = FullModalAnalysis.AnalyzeMatrix(m.A, m.States, new FullModalOptions(region, Count: 30, ForceMethod: 2, ResidualGate: 1e-9));
        Assert.Equal("shift-invert Arnoldi", r.Method);
        Assert.True(r.PartialSpectrum);
        Assert.NotEmpty(r.Modes);
        foreach (FullMode fm in r.Modes)
        {
            Assert.True(fm.ResidualPassed);
            double best = dense.Min(d => Complex.Abs(d - fm.Mode.Eigenvalue));
            Assert.True(best < 1e-7 * (1.0 + Complex.Abs(fm.Mode.Eigenvalue)), $"{fm.Mode.Eigenvalue} has no dense partner (closest {best:E2})");
        }
        // every dense eigenvalue inside the radius the solver claims complete must have been listed (nothing missed)
        if (r.Centre is Complex centre)
        {
            Complex[] listedAll = r.Modes.Select(x => x.Mode.Eigenvalue).ToArray();
            foreach (Complex d in dense)
            {
                if (Complex.Abs(d - centre) < r.CoverageRadius * 0.999 && region.Contains(d))
                    Assert.Contains(listedAll, z => Complex.Abs(z - d) < 1e-6 * (1.0 + Complex.Abs(d)));
            }
        }
    }

    [Fact]
    public void The_dense_path_below_the_stated_order_and_Arnoldi_above_it_are_chosen_automatically()
    {
        (GridSim.Core.Model.GridModel model, GridSim.Core.PowerFlow.PowerFlowResult pf) = R412Fixtures.Ring(8);
        var specs = Enumerable.Range(0, 8).Select(i => new FullMachineSpec(i, FullMachineModel.FluxDecay, Xdp: 0.3, Xd: 1.6, Xq: 1.5, Tdop: 6.0, DampingSystemPu: 1.0)).ToList();
        FullSmallSignalModel small = FullModelAssembler.Assemble(model, pf, specs);
        Assert.Equal("dense GeneralEigen", FullModalAnalysis.Analyze(small).Method);
        FullSmallSignalModel big = Assembled300();
        Assert.Equal("shift-invert Arnoldi", FullModalAnalysis.Analyze(big).Method);
    }

    [Fact]
    public void An_eigenpair_that_fails_the_residual_gate_makes_the_result_INCOMPLETE_and_is_marked()
    {
        FullSmallSignalModel m = Assembled300();
        // a gate no double-precision eigenpair can meet
        FullModalResult r = FullModalAnalysis.AnalyzeMatrix(m.A, m.States, new FullModalOptions(ModalRegion.FromCorners(-3, 1, 0.5, 14), Count: 6, ForceMethod: 2, ResidualGate: 1e-30));
        Assert.Equal(FullModalStatus.Incomplete, r.Status);
        Assert.Contains(r.IncompleteReasons, s => s.Contains("residual gate", StringComparison.Ordinal) || s.Contains("converge", StringComparison.Ordinal));
        Assert.All(r.Modes, fm => Assert.False(fm.ResidualPassed));
    }

    [Fact]
    public void An_Arnoldi_result_that_converged_inside_a_covering_radius_is_COMPLETE_and_says_what_it_covers()
    {
        FullSmallSignalModel m = Assembled300();
        FullModalResult r = FullModalAnalysis.AnalyzeMatrix(m.A, m.States, new FullModalOptions(ModalRegion.FromCorners(-2, 5, -1, 6), Count: 20, ForceMethod: 2, Ncv: 100));
        Assert.True(r.Status == FullModalStatus.Complete, $"restarts {r.Restarts} matvecs {r.MatVecs} maxres {r.MaxRelativeResidual:E2} reasons {string.Join(" | ", r.IncompleteReasons)} modes {r.Modes.Count}");
        Assert.Contains("complete within a distance", r.Coverage, StringComparison.Ordinal);
        Assert.True(r.MaxRelativeResidual <= r.ResidualGate);
    }
}
