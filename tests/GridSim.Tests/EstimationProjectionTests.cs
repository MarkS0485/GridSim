using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R108 (October 2026 review): <see cref="EstimationResult.ToPowerFlowResult"/> built a dense n x n Y plus dense G and B
/// and summed injections over all n^2 pairs (about 600 MB at gb-full, once per real-time window). It now uses the
/// sparse Y and the sparse solver's injection routine. These tests prove the projection is BIT-IDENTICAL to the old
/// dense form, which is reproduced here verbatim as the reference.
/// </summary>
public class EstimationProjectionTests
{
    private static string DataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "data");
    }

    /// <summary>The pre-R108 projection, verbatim: dense Y, dense G/B, the dense O(n^2) injection sum.</summary>
    private static PowerFlowResult DenseReference(EstimationResult est, GridModel model)
    {
        YBus ybus = YBus.Build(model);
        int n = ybus.N;
        var g = new double[n, n];
        var b = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) { g[i, j] = ybus.Y[i, j].Real; b[i, j] = ybus.Y[i, j].Imaginary; }
        var vm = new double[n];
        var va = new double[n];
        for (int i = 0; i < n; i++)
        {
            int idx = ybus.IndexOf(est.BusIds[i]);
            vm[idx] = est.Vm[i];
            va[idx] = est.VaDeg[i] * Math.PI / 180.0;
        }
        var p = new double[n];
        var q = new double[n];
        NewtonRaphsonPowerFlow.ComputeInjections(n, g, b, vm, va, p, q);
        return NewtonShell.AssembleResult(model, n, vm, va, p, q, model.BaseMva,
            converged: est.Status == EstimationStatus.Converged, totalIters: est.Iterations, maxMis: est.MaxStateUpdate,
            ybus.IndexOf);
    }

    private static void AssertBitIdentical(GridModel model)
    {
        PowerFlowResult truth = new SparseNewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged, "ground-truth solve did not converge");
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);
        EstimationResult est = new WlsStateEstimator().Estimate(model, set, new EstimationOptions());

        PowerFlowResult got = est.ToPowerFlowResult(model);
        PowerFlowResult want = DenseReference(est, model);

        Assert.Equal(want.Converged, got.Converged);
        Assert.Equal(want.Iterations, got.Iterations);
        Assert.Equal(want.MaxMismatch, got.MaxMismatch);
        Assert.Equal(want.BusIds, got.BusIds);
        Assert.Equal(want.Vm, got.Vm);
        Assert.Equal(want.VaDeg, got.VaDeg);
        Assert.Equal(want.PInjMw, got.PInjMw);       // exact: double[] equality is bit-for-bit
        Assert.Equal(want.QInjMvar, got.QInjMvar);
        Assert.Equal(want.TotalLossMw, got.TotalLossMw);
        Assert.Equal(want.TotalLossMvar, got.TotalLossMvar);
        Assert.Equal(want.LineFlows.Count, got.LineFlows.Count);
        for (int k = 0; k < want.LineFlows.Count; k++) Assert.Equal(want.LineFlows[k], got.LineFlows[k]);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("14")]
    [InlineData("118")]
    [InlineData("300")]
    public void Projection_is_bit_identical_to_the_dense_form_on_built_in_cases(string name)
    {
        AssertBitIdentical(name switch
        {
            "9" => IeeeCases.Case9(),
            "14" => IeeeCases.Case14(),
            "118" => IeeeCases.Case118(),
            "300" => IeeeCases.Case300(),
            _ => throw new ArgumentException(name),
        });
    }

    [Fact]
    public void Projection_is_bit_identical_to_the_dense_form_on_pegase1354()
    {
        string path = Path.Combine(DataDir(), "matpower", "pegase", "case1354pegase.m");
        Assert.True(File.Exists(path), path);
        AssertBitIdentical(MatpowerParser.ParseFile(path));
    }

    [Fact]
    public void Projection_no_longer_builds_the_dense_admittance_matrix()
    {
        // Anti-drift: the dense build (and its n x n G/B copies) must not come back into the projection.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "Estimation", "EstimationResult.cs"));
        int i = src.IndexOf("public PowerFlowResult ToPowerFlowResult(", StringComparison.Ordinal);
        Assert.True(i > 0);
        string body = src[i..];
        Assert.DoesNotContain("YBus.Build(model)", body);
        Assert.DoesNotContain("new double[n, n]", body);
        Assert.Contains("SparseNewtonRaphsonPowerFlow.ComputeInjections", body);
    }
}
