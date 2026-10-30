using System.Text.Json;
using GridSim.Core.Abstractions;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The <c>--enableExport</c> path: a <see cref="SolveTrace"/> captures everything a solve computes,
/// and <see cref="SolveExport"/> serialises it. These lock the contract (captures the Y-bus, per-unit
/// spec, per-iteration mismatch/correction/Jacobian, and the converged state) and - critically - that
/// attaching a trace does not perturb the solution.
/// </summary>
public class SolveExportTests
{
    private static readonly SolveExportMeta Meta =
        new("test", "cpu", "deadbeef", "2026-07-02T00:00:00Z", 1.0, "test-host");

    // A 4-bus radial feeder (for the BFS path).
    private static GridModel RadialFeeder() => new()
    {
        Name = "radial", BaseMva = 1,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 11),
            new Bus(2, BusType.Pq, Pd: 0.10, Qd: 0.05, BaseKv: 11),
            new Bus(3, BusType.Pq, Pd: 0.10, Qd: 0.05, BaseKv: 11),
            new Bus(4, BusType.Pq, Pd: 0.10, Qd: 0.05, BaseKv: 11),
        },
        Generators = new[] { new Generator(1, Pg: 0, Qg: 0, Vg: 1.0, Qmin: -100, Qmax: 100, Pmax: 100, MBase: 1, H: 0) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.02, 0.0), new Branch(2, 3, 0.01, 0.02, 0.0), new Branch(3, 4, 0.01, 0.02, 0.0) },
    };

    [Fact]
    public void Dense_trace_captures_ybus_spec_iterations_and_jacobian()
    {
        var trace = new SolveTrace();
        var r = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), new PowerFlowOptions { Trace = trace });

        Assert.True(r.Converged);
        Assert.Equal("dense-newton", trace.SolverName);
        Assert.Equal(9, trace.N);
        Assert.NotEmpty(trace.YBus);                       // admittance matrix captured
        Assert.Equal(9, trace.Spec.Length);                // one spec row per bus
        Assert.NotEmpty(trace.Passes);
        var iters = trace.Passes[0].Iterations;
        Assert.NotEmpty(iters);
        foreach (var it in iters)
        {
            Assert.Equal(it.Mismatch.Length, it.Correction.Length);
            Assert.NotNull(it.Jacobian);                   // n=9 <= gate => Jacobian present
            Assert.Equal(it.Mismatch.Length, it.Jacobian!.Length);   // square, matching the residual
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Attaching_a_trace_does_not_change_the_solution(bool sparse)
    {
        GridModel m = IeeeCases.Case14();
        IPowerFlowSolver S() => sparse ? new SparseNewtonRaphsonPowerFlow() : new NewtonRaphsonPowerFlow();
        PowerFlowResult plain = S().Solve(m, new PowerFlowOptions());
        PowerFlowResult traced = S().Solve(m, new PowerFlowOptions { Trace = new SolveTrace() });

        Assert.Equal(plain.Iterations, traced.Iterations);
        for (int i = 0; i < plain.Vm.Length; i++)
        {
            Assert.Equal(plain.Vm[i], traced.Vm[i]);       // bit-identical - the recorder only observes
            Assert.Equal(plain.VaDeg[i], traced.VaDeg[i]);
        }
    }

    [Fact]
    public void Jacobian_is_gated_by_size_and_forceable()
    {
        // n = 9 with a tiny gate => omitted (and flagged)...
        var gated = new SolveTrace { JacobianMaxN = 5 };
        new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), new PowerFlowOptions { Trace = gated });
        Assert.True(gated.JacobianOmittedForSize);
        Assert.All(gated.Passes[0].Iterations, it => Assert.Null(it.Jacobian));

        // ...unless forced.
        var forced = new SolveTrace { JacobianMaxN = 5, CaptureJacobian = true };
        new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), new PowerFlowOptions { Trace = forced });
        Assert.False(forced.JacobianOmittedForSize);
        Assert.All(forced.Passes[0].Iterations, it => Assert.NotNull(it.Jacobian));
    }

    [Fact]
    public void Bfs_trace_has_no_ybus_but_records_sweeps()
    {
        var trace = new SolveTrace();
        var r = new BackwardForwardSweep().Solve(RadialFeeder(), new PowerFlowOptions { Trace = trace });

        Assert.True(r.Converged);
        Assert.Equal("bfs", trace.SolverName);
        Assert.Empty(trace.YBus);                          // BFS forms no admittance matrix
        Assert.Equal(4, trace.Spec.Length);
        Assert.NotEmpty(trace.Passes[0].Iterations);       // per-sweep |deltaV| recorded
    }

    [Fact]
    public void Export_json_has_all_sections_and_roundtrips()
    {
        var trace = new SolveTrace();
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = trace });

        string json = SolveExport.ToJson(m, r, trace, Meta);
        using JsonDocument doc = JsonDocument.Parse(json);   // valid JSON
        JsonElement root = doc.RootElement;

        Assert.Equal("gridsim-solve-export/1", root.GetProperty("schema").GetString());
        foreach (string key in new[] { "meta", "model", "solve", "result" })
            Assert.True(root.TryGetProperty(key, out _), $"missing section: {key}");

        JsonElement solve = root.GetProperty("solve");
        Assert.Equal("dense-newton", solve.GetProperty("solver").GetString());
        Assert.True(solve.GetProperty("ybus").GetArrayLength() > 0);
        Assert.True(solve.GetProperty("busSpec").GetArrayLength() == 9);
        Assert.True(solve.GetProperty("passes")[0].GetProperty("iterations").GetArrayLength() > 0);
        Assert.Equal(9, root.GetProperty("result").GetProperty("buses").GetArrayLength());
        Assert.Equal(9, root.GetProperty("model").GetProperty("busCount").GetInt32());
    }

    [Fact]
    public void Export_is_deterministic()
    {
        GridModel m = IeeeCases.Case9();
        string A() { var t = new SolveTrace(); var r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = t }); return SolveExport.ToJson(m, r, t, Meta); }
        Assert.Equal(A(), A());
    }
}
