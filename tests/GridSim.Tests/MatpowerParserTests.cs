using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Unit tests for the MATPOWER .m parser and integration tests over the committed case
/// libraries. Library tests are guarded on file existence so a lean checkout stays green.
/// </summary>
public class MatpowerParserTests
{
    private static string CaseDir => Path.Combine(AppContext.BaseDirectory, "data", "matpower");

    // ---- Tokenizer / mapping units (inline .m fragments) --------------------------------------

    private const string Tiny = @"
function mpc = tiny
mpc.baseMVA = 100;   % system base
mpc.bus = [
    1   3   0   0   0   0   1   1.0   0   345   1   1.1   0.9;   % slack
    2   1   50  20  0   0   1   1.0   0   345   1   1.1   0.9;
];
mpc.gen = [
    1   0   0   Inf   -Inf   1.02   100   1   Inf   0;
];
mpc.branch = [
    1   2   0.01   0.1   0.02   250   0   0   0   0   1   -360   360;
];
";

    [Fact]
    public void Parses_scalars_buses_gens_branches()
    {
        GridModel m = MatpowerParser.Parse(Tiny);
        Assert.Equal("tiny", m.Name);
        Assert.Equal(100, m.BaseMva);
        Assert.Equal(2, m.BusCount);
        Assert.Single(m.Generators);
        Assert.Single(m.Branches);
        Assert.Equal(BusType.Slack, m.Buses[0].Type);
        Assert.Equal(BusType.Pq, m.Buses[1].Type);
        Assert.Equal(50, m.Buses[1].Pd);
    }

    [Fact]
    public void Maps_Inf_generator_limits_to_infinity()
    {
        Generator g = MatpowerParser.Parse(Tiny).Generators[0];
        Assert.True(double.IsPositiveInfinity(g.Qmax));
        Assert.True(double.IsNegativeInfinity(g.Qmin));
        Assert.True(double.IsPositiveInfinity(g.Pmax));
    }

    [Fact]
    public void Line_vs_transformer_by_ratio()
    {
        const string s = @"function mpc = t
mpc.baseMVA = 100;
mpc.bus = [ 1 3 0 0 0 0 1 1 0 138 1 1.1 0.9; 2 1 0 0 0 0 1 1 0 138 1 1.1 0.9; 3 1 0 0 0 0 1 1 0 138 1 1.1 0.9; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 100 0; ];
mpc.branch = [
    1 2 0 0.1 0 0 0 0 0    0  1 -360 360;
    2 3 0 0.1 0 0 0 0 0.98 5  1 -360 360;
];";
        GridModel m = MatpowerParser.Parse(s);
        Branch line = m.Branches[0], xfmr = m.Branches[1];
        Assert.False(line.IsTransformer);
        Assert.Equal(1.0, line.TapRatio);
        Assert.True(xfmr.IsTransformer);
        Assert.Equal(0.98, xfmr.TapRatio, 6);
        Assert.Equal(5.0, xfmr.PhaseShiftDeg, 6);
    }

    [Fact]
    public void Drops_isolated_buses_and_incident_elements()
    {
        const string s = @"function mpc = iso
mpc.baseMVA = 100;
mpc.bus = [ 1 3 0 0 0 0 1 1 0 138 1 1.1 0.9; 2 1 0 0 0 0 1 1 0 138 1 1.1 0.9; 9 4 0 0 0 0 1 1 0 138 1 1.1 0.9; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 100 0; 9 10 0 100 -100 1 100 1 100 0; ];
mpc.branch = [ 1 2 0 0.1 0 0 0 0 0 0 1 -360 360; 2 9 0 0.1 0 0 0 0 0 0 1 -360 360; ];";
        MatpowerParseResult res = MatpowerParser.ParseWithDiagnostics(s);
        Assert.Equal(2, res.Model.BusCount);                 // bus 9 (type 4) dropped
        Assert.Single(res.Model.Generators);                 // gen on bus 9 dropped
        Assert.Single(res.Model.Branches);                   // branch 2-9 dropped
        Assert.Contains(res.Notes, n => n.Contains("isolated"));
    }

    [Fact]
    public void Skips_out_of_service_elements()
    {
        const string s = @"function mpc = off
mpc.baseMVA = 100;
mpc.bus = [ 1 3 0 0 0 0 1 1 0 138 1 1.1 0.9; 2 1 10 0 0 0 1 1 0 138 1 1.1 0.9; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 100 0; 2 5 0 100 -100 1 100 0 100 0; ];
mpc.branch = [ 1 2 0 0.1 0 0 0 0 0 0 1 -360 360; 1 2 0 0.2 0 0 0 0 0 0 0 -360 360; ];";
        GridModel m = MatpowerParser.Parse(s);
        Assert.Single(m.Generators);   // status-0 gen skipped
        Assert.Single(m.Branches);     // status-0 branch skipped
    }

    [Fact]
    public void Handles_noncontiguous_bus_ids()
    {
        const string s = @"function mpc = gaps
mpc.baseMVA = 100;
mpc.bus = [ 100 3 0 0 0 0 1 1 0 138 1 1.1 0.9; 205 1 30 10 0 0 1 1 0 138 1 1.1 0.9; 9999 1 20 5 0 0 1 1 0 138 1 1.1 0.9; ];
mpc.gen = [ 100 0 0 500 -500 1.0 100 1 500 0; ];
mpc.branch = [ 100 205 0.01 0.1 0 0 0 0 0 0 1 -360 360; 205 9999 0.01 0.1 0 0 0 0 0 0 1 -360 360; ];";
        GridModel m = MatpowerParser.Parse(s);
        Assert.Equal(new[] { 100, 205, 9999 }, m.Buses.Select(b => b.Id).ToArray());
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        Assert.Equal(new[] { 100, 205, 9999 }, r.BusIds.ToArray());   // file order preserved
    }

    [Fact]
    public void Throws_when_no_reference_bus()
    {
        const string s = @"function mpc = noref
mpc.baseMVA = 100;
mpc.bus = [ 1 1 0 0 0 0 1 1 0 138 1 1.1 0.9; 2 1 0 0 0 0 1 1 0 138 1 1.1 0.9; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 100 0; ];
mpc.branch = [ 1 2 0 0.1 0 0 0 0 0 0 1 -360 360; ];";
        Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(s));
    }

    // ---- Round-trip: parsed case9.m vs the hand-coded IeeeCases.Case9() -----------------------

    [Fact]
    public void Case9_from_file_matches_handcoded_structure_and_solution()
    {
        string path = Path.Combine(CaseDir, "lib", "case9.m");
        Assert.True(File.Exists(path), $"missing {path}");
        GridModel parsed = MatpowerParser.ParseFile(path);
        GridModel hand = IeeeCases.Case9();

        Assert.Equal(hand.BusCount, parsed.BusCount);
        Assert.Equal(hand.Branches.Count, parsed.Branches.Count);
        Assert.Equal(hand.Generators.Count, parsed.Generators.Count);

        var rp = new NewtonRaphsonPowerFlow().Solve(parsed);
        var rh = new NewtonRaphsonPowerFlow().Solve(hand);
        Assert.True(rp.Converged && rh.Converged);

        var vmP = new Dictionary<int, double>(); var vaP = new Dictionary<int, double>();
        var vmH = new Dictionary<int, double>(); var vaH = new Dictionary<int, double>();
        for (int i = 0; i < rp.BusIds.Count; i++) { vmP[rp.BusIds[i]] = rp.Vm[i]; vaP[rp.BusIds[i]] = rp.VaDeg[i]; }
        for (int i = 0; i < rh.BusIds.Count; i++) { vmH[rh.BusIds[i]] = rh.Vm[i]; vaH[rh.BusIds[i]] = rh.VaDeg[i]; }
        foreach (int id in vmH.Keys)
        {
            Assert.Equal(vmH[id], vmP[id], 6);
            Assert.Equal(vaH[id], vaP[id], 4);
        }
    }

    // ---- Library integration (guarded on committed files) -------------------------------------

    [Theory]
    [InlineData("pegase/case1354pegase.m", 1354)]
    [InlineData("pegase/case2869pegase.m", 2869)]
    [LargeCaseInlineData(CaseSizes.Pegase9241, Engine.Sparse, "pegase/case9241pegase.m", 9241)]
    [InlineData("rts/pglib_opf_case73_ieee_rts.m", 73)]
    public void Large_case_parses_and_converges(string rel, int expectedBuses)
    {
        string path = Path.Combine(CaseDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"bundled file missing: {path}");   // GR-404: the file is committed (BundledDataPresenceTests checks it), so a missing one fails instead of passing silently

        GridModel m = MatpowerParser.ParseFile(path);
        Assert.Equal(expectedBuses, m.BusCount);

        var opt = new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = false };
        var r = new SparseNewtonRaphsonPowerFlow().Solve(m, opt);
        Assert.True(r.Converged, $"{rel} did not converge (mismatch {r.MaxMismatch:E2})");
        Assert.True(r.MaxMismatch < 1e-4, $"{rel} mismatch {r.MaxMismatch:E2}");
    }

    [Fact]
    public void CigreMv_parses_and_converges()
    {
        GridModel m = IeeeCases.CigreMv();
        Assert.Equal(15, m.BusCount);
        Assert.Equal(14, m.Branches.Count);
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"CIGRE MV did not converge (mismatch {r.MaxMismatch:E2})");
    }
}
