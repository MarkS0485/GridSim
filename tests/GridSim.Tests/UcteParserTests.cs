using GridSim.Core.IO.Ucte;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Tranche A (October 2026): the UCTE-DEF reader. small.uct (tests/GridSim.Tests/fixtures/ucte) is hand-written to the
/// version 02 column layout: two countries, an X-node, a busbar coupler, a line out of operation, a 380/220 kV
/// transformer at tap +2 of 16 (1.25 % per step) with its magnetising admittance, and an exchange schedule. Every expected
/// value below is worked out by hand from the file, with the per-unit conversion written out as the reader documents it.
/// </summary>
public class UcteParserTests
{
    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "ucte", name);

    private static string Small() => File.ReadAllText(Fixture("small.uct"));

    private static void AssertSameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    [Fact]
    public void Nodes_become_buses_with_their_loads_voltages_and_metadata()
    {
        UcteParseResult r = UcteParser.ParseFile(Fixture("small.uct"));
        GridModel m = r.Model;
        Assert.Equal(("small", 100.0, 50.0), (m.Name, m.BaseMva, m.FrequencyHz));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, m.Buses.Select(b => b.Id));
        Assert.Equal(new[] { "FAAAAA11", "FBBBBB11", "FCCCCC21", "FCCCCC22", "XFRDE111" }, m.Buses.Select(b => b.Name));
        Assert.Equal(new[] { BusType.Slack, BusType.Pq, BusType.Pv, BusType.Pq, BusType.Pq }, m.Buses.Select(b => b.Type));
        Assert.Equal(new[] { 380.0, 380.0, 220.0, 220.0, 380.0 }, m.Buses.Select(b => b.BaseKv));
        AssertSameBits(400.0 / 380.0, m.Buses[0].Vm, "FAAAAA11 Vm");
        AssertSameBits(225.0 / 220.0, m.Buses[2].Vm, "FCCCCC21 Vm");
        Assert.Equal((300.0, 100.0), (m.Buses[1].Pd, m.Buses[1].Qd));
        Assert.Equal((50.0, 0.0), (m.Buses[4].Pd, m.Buses[4].Qd));   // the X-node's written load, a fixed injection
        Assert.Equal(new UcteNodeMeta("FCCCCC22", "CHARLIE B", "FR", true, false), r.Nodes[4]);
        Assert.Equal(new UcteNodeMeta("XFRDE111", "BORDER", "XX", false, true), r.Nodes[5]);
        Assert.Contains(r.Notes, n => n.StartsWith("1 X-node(s) (boundary nodes) are buses", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 equivalent node(s) (status 1)", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("UCTE-DEF carries no system frequency; assumed 50 Hz", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n == "1 exchange schedule record(s) (##E) read past (scheduling data, not network).");
    }

    [Fact]
    public void Generation_is_read_with_the_load_sign_turned_round()
    {
        GridModel m = UcteParser.Parse(Small(), "small");
        Generator a = m.AllGenerators[0], c = m.AllGenerators[1];
        // FAAAAA11: generation -500 MW (producing 500), permissible 0 .. -1000 MW, reactive 500 .. -500 MVAr.
        Assert.Equal((1, 500.0, 0.0, 0.0, 1000.0, -500.0, 500.0), (a.Bus, a.Pg, a.Qg, a.Pmin, a.Pmax, a.Qmin, a.Qmax));
        AssertSameBits(0.0, a.Pmin, "a written 0 is +0");
        AssertSameBits(400.0 / 380.0, a.Vg, "FAAAAA11 Vg");
        // FCCCCC21: -100 MW, permissible -10 .. -400 MW, reactive 200 .. -200 MVAr.
        Assert.Equal((3, 100.0, 10.0, 400.0, -200.0, 200.0), (c.Bus, c.Pg, c.Pmin, c.Pmax, c.Qmin, c.Qmax));
        Assert.Equal(2, m.AllGenerators.Count);
    }

    [Fact]
    public void Lines_couplers_and_the_transformer_are_converted_to_per_unit_as_documented()
    {
        UcteParseResult r = UcteParser.ParseFile(Fixture("small.uct"));
        GridModel m = r.Model;
        Assert.Equal(new[] { "line", "line", "coupler", "transformer" }, r.BranchKeys.Select(k => k.Kind));
        Assert.Equal(new UcteBranchKey("transformer", "FBBBBB11", "FCCCCC21", "1"), r.BranchKeys[3]);

        double zb380 = 380.0 * 380.0 / 100.0;
        Branch ab = m.Branches[0];
        Assert.Equal((1, 2, "LINE-AB"), (ab.From, ab.To, ab.Name));
        AssertSameBits(1.444 / zb380, ab.R, "A-B R");
        AssertSameBits(14.44 / zb380, ab.X, "A-B X");
        AssertSameBits(120.0 * 1e-6 * zb380, ab.B, "A-B B");
        AssertSameBits(Math.Sqrt(3.0) * 380.0 * 2000.0 / 1000.0, ab.RateAMva, "A-B rating");
        // The second A-B circuit (status 8) is out of operation and skipped.
        Assert.Equal((2, 5), (m.Branches[1].From, m.Branches[1].To));
        Branch coupler = m.Branches[2];
        Assert.Equal((3, 4, 0.0, 0.0001, 0.0), (coupler.From, coupler.To, coupler.R, coupler.X, coupler.B));

        // Transformer B (380 kV, node 1, non-regulated) - C (220 kV, node 2, regulated, tap +2 x 1.25 %): the branch runs
        // from the regulated winding (bus 3) to bus 2, impedance on the 380 kV side's base.
        Branch t = m.Branches[3];
        Assert.Equal((3, 2, true, "TR-BC", 0.0), (t.From, t.To, t.IsTransformer, t.Name, t.PhaseShiftDeg));
        AssertSameBits(0.722 / zb380, t.R, "B-C R");
        AssertSameBits(36.1 / zb380, t.X, "B-C X");
        AssertSameBits((220.0 * (1.0 + 2 * 1.25 / 100.0) / 220.0) / (380.0 / 380.0), t.TapRatio, "B-C ratio");
        AssertSameBits(Math.Sqrt(3.0) * 380.0 * 900.0 / 1000.0, t.RateAMva, "B-C rating");
        // Magnetising G 0.5 and B -2.5 microsiemens at node 1 (bus 2), as MW and MVAr at 1 pu: G x U^2.
        AssertSameBits(0.5 * 1e-6 * 380.0 * 380.0, m.Buses[1].Gs, "bus 2 Gs");
        AssertSameBits(-2.5 * 1e-6 * 380.0 * 380.0, m.Buses[1].Bs, "bus 2 Bs");

        Assert.Contains(r.Notes, n => n == "skipped 1 line(s) or coupler(s) and 0 transformer(s) out of operation.");
        Assert.Contains(r.Notes, n => n.StartsWith("1 busbar coupler(s) in operation are branches of X = 0.0001 pu.", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 transformer(s) are at a tap off neutral (##R)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_case_solves()
    {
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(UcteParser.Parse(Small(), "small"));
        Assert.True(pf.Converged, "small.uct must solve");
    }

    private static string WithRegulation(string record)
        => Small().Replace("FBBBBB11 FCCCCC21 1 1.250 16   2", record, StringComparison.Ordinal);

    // A regulation record for transformer B-C with each value right-aligned in its 1-based columns.
    private static string Reg(string du1 = "", string n1 = "", string p1 = "", string du2 = "", string theta = "", string n2 = "",
        string p2 = "", string type = "")
    {
        var line = new System.Text.StringBuilder("FBBBBB11 FCCCCC21 1".PadRight(68));
        void Put(int from, int to, string v, bool left = false)
        {
            string t = left ? v.PadRight(to - from + 1) : v.PadLeft(to - from + 1);
            for (int k = 0; k < t.Length; k++) line[from - 1 + k] = t[k];
        }
        Put(21, 25, du1); Put(27, 28, n1); Put(30, 32, p1); Put(40, 44, du2); Put(46, 50, theta); Put(52, 53, n2); Put(55, 57, p2);
        Put(65, 68, type, left: true);
        return line.ToString().TrimEnd();
    }

    [Fact]
    public void Angle_regulation_gives_the_ratio_and_phase_shift_of_its_step()
    {
        // Asymmetric: du 1.5 %, theta 60 degrees, tap -3 of 10: dx = -0.045 cos 60, dy = -0.045 sin 60.
        string asym = WithRegulation(Reg(du2: "1.500", theta: "60.00", n2: "10", p2: "-3", type: "ASYM"));
        Branch t = UcteParser.Parse(asym, "a").Branches[3];
        double step = -3 * 1.5 / 100.0, rad = 60.0 * Math.PI / 180.0;
        double dx = step * DetMath.Cos(rad), dy = step * DetMath.Sin(rad);
        AssertSameBits((220.0 * (1.0 * Math.Sqrt(dy * dy + (1.0 + dx) * (1.0 + dx))) / 220.0) / (380.0 / 380.0), t.TapRatio, "asym ratio");
        AssertSameBits(DetMath.Atan2(dy, 1.0 + dx) * 180.0 / Math.PI, t.PhaseShiftDeg, "asym angle");

        // Symmetric: theta 90, tap +4 of 10 at 1 %: ratio 1, angle 2 atan2(dy, 2 (1 + dx)).
        string symm = WithRegulation(Reg(du2: "1.000", theta: "90.00", n2: "10", p2: "4", type: "SYMM"));
        Branch s = UcteParser.Parse(symm, "s").Branches[3];
        double st = 4 * 1.0 / 100.0, r90 = 90.0 * Math.PI / 180.0;
        double sx = st * DetMath.Cos(r90), sy = st * DetMath.Sin(r90);
        AssertSameBits((220.0 * 1.0 / 220.0) / (380.0 / 380.0), s.TapRatio, "symm ratio");
        AssertSameBits(2.0 * DetMath.Atan2(sy, 2.0 * (1.0 + sx)) * 180.0 / Math.PI, s.PhaseShiftDeg, "symm angle");
    }

    private static void Refused(string text, string expected, UcteReadOptions? options = null)
    {
        var ex = Assert.Throws<InvalidDataException>(() => UcteParser.ParseWithDiagnostics(text, "t", options));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    private static string Edit(string from, string to)
    {
        string s = Small();
        Assert.Contains(from, s, StringComparison.Ordinal);
        return s.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_or_inconsistent_records_are_refused_naming_line_block_and_field()
    {
        Refused(Edit("FBBBBB11 BRAVO        0 0        300.000", "FBBBBB11 BRAVO        0 0        3x0.000"),
            "UCTE-DEF line 6 (##N block): active load (columns 34-40) is '3x0.000'; it must be a finite number");
        Refused(Edit("FBBBBB11 BRAVO        0 0", "FBBBBB11 BRAVO        0 1"),
            "line 6 (##N block): node type 1 (Q and angle held) has no GridSim equivalent");
        Refused(Edit("FBBBBB11 BRAVO", "FBBBBBA1 BRAVO"),
            "line 6 (##N block): node 'FBBBBBA1': voltage level code 'A' (the 7th character) is not one of 0-9");
        Refused(Edit("FCCCCC22 CHARLIE B", "FCCCCC21 CHARLIE B"), "line 8 (##N block): node 'FCCCCC21' is defined twice");
        Refused(Edit("FAAAAA11 FBBBBB11 1 0 1.4440", "FAAAAA11 FZZZZZ11 1 0 1.4440"),
            "line 12 (##L block): node 2 'FZZZZZ11' is not a node of this file");
        Refused(Edit("FBBBBB11 XFRDE111 1 0", "FBBBBB11 FCCCCC21 1 0"),
            "line 14 (##L block): a line joins nodes of different nominal voltages (380 kV and 220 kV)");
        Refused(Edit("FAAAAA11 FBBBBB11 2 8", "FAAAAA11 FBBBBB11 1 8"),
            "line 13 (##L block): element FAAAAA11 - FBBBBB11 order '1' is defined twice (also at line 12)");
        Refused(Edit("FAAAAA11 FBBBBB11 1 0", "FAAAAA11 FBBBBB11 1 5"), "line 12 (##L block): status is 5");
        Refused(WithRegulation(Reg("1.250", "16", "17")), "line 19 (##R block): phase regulation tap n' 17 is outside -n..n (n 16)");
        Refused(WithRegulation(Reg("1.250", "16", "2", "1.000", "90.00", "10", "4")),
            "line 19 (##R block): both phase and angle regulation are stepped off neutral");
        Refused(WithRegulation("FBBBBB11 FAAAAA11 1 1.250 16   2"),
            "line 19 (##R block): the regulation record names transformer FBBBBB11 - FAAAAA11 order '1', which ##T does not define");
        Refused(Edit("FAAAAA11 ALPHA        0 3", "FAAAAA11 ALPHA        0 0"), "UCTE-DEF case 't' has no slack (type 3) node");
        Refused(Edit("##R", "##Q"), "line 18 (##T block): '##Q' is not a UCTE-DEF block");
        Refused(Edit("##E", "##L"), "line 20 (##L block): block ##L comes after ##R");
        Refused("FAAAAA11 ALPHA        0 3 400.00\n", "line 1 (header block): data before the first block header");
    }

    [Fact]
    public void Special_transformer_tables_are_refused_unless_allowed()
    {
        string tt = Edit("##E", "##TT\nFBBBBB11 FCCCCC21 1   2 1.0000 36.100\n##E");
        Refused(tt, "has 1 transformer(s) in operation with special transformer tables (##TT, first at line 17)");
        UcteParseResult r = UcteParser.ParseWithDiagnostics(tt, "t", new UcteReadOptions { AllowUnmodelled = true });
        Assert.Contains(r.Notes, n => n.StartsWith("1 transformer(s) have special transformer tables (##TT) that are read past and NOT modelled", StringComparison.Ordinal));
    }

    [Fact]
    public void A_declared_frequency_replaces_the_assumed_one()
    {
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Small(), "t", new UcteReadOptions { FrequencyHz = 60 });
        Assert.Equal(60.0, r.Model.FrequencyHz);
        Assert.DoesNotContain(r.Notes, n => n.StartsWith("UCTE-DEF carries no system frequency", StringComparison.Ordinal));
    }
}
