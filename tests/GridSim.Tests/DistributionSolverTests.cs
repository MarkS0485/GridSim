using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validates the backward/forward-sweep distribution solver against the canonical Baran &amp; Wu
/// 33-bus radial feeder, and the MATPOWER parser's arithmetic-expression handling.
/// </summary>
public class DistributionSolverTests
{
    private static string Dist(string f) =>
        Path.Combine(AppContext.BaseDirectory, "data", "matpower", "dist", f);

    [Fact]
    public void Bfs_matches_dense_solver_on_cigre_mv()
    {
        // CIGRE MV is a correctly per-unit, radial feeder. BFS must reproduce the dense
        // Newton-Raphson voltage solution - the proof the sweep solves the same network.
        GridModel m = GridSim.Core.IO.IeeeCases.CigreMv();
        var bfs = new BackwardForwardSweep().Solve(m);
        var nr = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(bfs.Converged, $"BFS did not converge (mismatch {bfs.MaxMismatch:E2})");
        Assert.True(nr.Converged);
        for (int i = 0; i < bfs.Vm.Length; i++)
            Assert.True(Math.Abs(bfs.Vm[i] - nr.Vm[i]) < 1e-3,
                $"bus {m.Buses[i].Id}: BFS Vm {bfs.Vm[i]:F4} vs NR {nr.Vm[i]:F4}");
    }

    [Fact]
    public void Bfs_solves_case33bw_to_published_solution()
    {
        string path = Dist("case33bw.m");
        Assert.True(File.Exists(path), $"missing {path}");
        // Moved 2026-10 (N165, authorised by Mark's 2026-10-02 best-judgement ruling): ParseFile now normalises a raw
        // feeder by default; this test does the per-unit reduction by hand below, so it asks for the numbers as written.
        // Before: MatpowerParser.ParseFile(path). After: ParseFile(path, MatpowerUnitHandling.AsWritten). Expectations unchanged.
        GridModel raw = MatpowerParser.ParseFile(path, MatpowerUnitHandling.AsWritten);

        // case33bw ships in RAW units: branch impedances in ohms and loads in kW, rather than the
        // MATPOWER per-unit / MW conventions. Refer both to the system base before solving
        // (Zbase = kV^2 / MVA; kW -> MW).
        double zbase = raw.Buses[0].BaseKv * raw.Buses[0].BaseKv / raw.BaseMva;
        var m = new GridModel
        {
            Name = raw.Name, BaseMva = raw.BaseMva, FrequencyHz = raw.FrequencyHz,
            Generators = raw.Generators,
            Buses = raw.Buses.Select(b => b with { Pd = b.Pd / 1000.0, Qd = b.Qd / 1000.0 }).ToList(),
            Branches = raw.Branches.Select(b => b with { R = b.R / zbase, X = b.X / zbase }).ToList(),
        };

        var r = new BackwardForwardSweep().Solve(m);
        Assert.True(r.Converged, $"BFS did not converge (mismatch {r.MaxMismatch:E2})");

        // Baran & Wu (1989): minimum voltage ~0.9131 p.u.; total real loss ~202.7 kW (baseMVA = 10).
        Assert.InRange(r.Vm.Min(), 0.908, 0.918);
        Assert.InRange(r.TotalLossMw, 0.18, 0.22);
    }

    [Fact]
    public void Bfs_reports_nonconvergence_on_a_meshed_transmission_case()
    {
        // The IEEE 14-bus is meshed, not radial - BFS should decline rather than return nonsense.
        GridModel m = GridSim.Core.IO.IeeeCases.Case14();
        var r = new BackwardForwardSweep().Solve(m);
        Assert.False(r.Converged);
    }

    [Fact]
    public void Parser_evaluates_arithmetic_expressions()
    {
        // 135/sqrt(3) = line-to-neutral base kV, used verbatim in the ACTIVSg case files.
        const string s = @"function mpc = expr
mpc.baseMVA = 100;
mpc.bus = [ 1 3 0 0 0 0 1 1 0 135/sqrt(3) 1 1.1 0.9; 2 1 0 0 0 0 1 1 0 2*sqrt(4) 1 1.1 0.9; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 100 0; ];
mpc.branch = [ 1 2 0 0.1 0 0 0 0 0 0 1 -360 360; ];";
        GridModel m = MatpowerParser.Parse(s);
        Assert.Equal(135.0 / Math.Sqrt(3.0), m.Buses[0].BaseKv, 6);   // ~77.94
        Assert.Equal(4.0, m.Buses[1].BaseKv, 9);                       // 2*sqrt(4)
    }

    [Fact]
    public void Parser_loads_case533mt_with_sqrt_base_voltages()
    {
        string path = Dist("case533mt_hi.m");
        Assert.True(File.Exists(path), $"bundled file missing: {path}");   // GR-404: the file is committed (BundledDataPresenceTests checks it), so a missing one fails instead of passing silently
        GridModel m = MatpowerParser.ParseFile(path);
        Assert.True(m.BusCount > 500);
        Assert.All(m.Buses, b => Assert.True(b.BaseKv > 0));
    }
}
