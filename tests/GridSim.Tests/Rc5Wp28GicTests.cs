using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis.Gic;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP28: geomagnetically induced currents (uniform field, DC nodal network).
/// Sources:
/// [G] NERC, "Application Guide: Computing Geomagnetically-Induced Current in the Bulk-Power System", December 2013:
///     Appendix I (distances, eqs. A.3 and A.7) and Appendix II (the six-bus, three-substation example, Tables B-1 to B-7).
/// [T] NERC, "Transformer Thermal Impact Assessment White Paper", Project 2013-03, October 2017, eq. 1:
///     I_dc,eq = I_H + (I_N/3 - I_H) V_X / V_H.
/// Notes on [G] found while transcribing:
/// - Appendix II is the guide's worked example (6 buses, 3 substations). The 20-bus Horton et al. test case is not
///   reproduced in the guide, so it is not used here.
/// - Table B-4 does not follow eqs. A.3/A.7 (they give 77.306 km for line 1, the table 77.499 km); it follows a sphere at
///   about 111.2 km per degree, GridSim's <see cref="GicDistanceModel.Spherical"/>. That constant is inferred from the
///   example, not stated by the guide.
/// - GridSim's currents for the example are asserted against an independent re-derivation (T5e: exact rational nodal
///   analysis in Python, cross-checked by a mesh analysis), to rounding. The printed Tables B-6 and B-7 are reference
///   checks only (T5a, T5b): B-6 agrees to its printed precision; eight cells of B-7 differ from the correctly rounded value
///   by one printed unit (about 0.006 A from the exact figure). The cause of that residual in the guide's figures is
///   unexplained: no single km-per-degree constant fits B-6 and B-7 together to half a unit.
/// - Tables B-4 and B-5 give each line's distance and source as from-end minus to-end (an eastward line in an eastward field
///   has -929.6 V), and Table B-6 follows that sign. Table B-7 follows the physical sign (the source along the path from
///   the from end to the to end), which is GridSim's convention. So B-6 is compared with the negated GridSim currents
///   and B-7 with them as they are.
/// - Ohms are per phase throughout; the three phases of a line or winding are in parallel (R/3).
/// </summary>
public sealed class Rc5Wp28GicTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("gic");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private static GicFile File(string json) => GicFile.Parse(Encoding.UTF8.GetBytes(json), "test.json");

    // ------------------------------------------------------------------ test 1: two-substation loop, closed form

    // A (HV bus 1, LV bus 3) and B (HV bus 2, LV bus 4); line 1-2; generator step-ups 3-1 and 4-2.
    private static GridModel TwoSub() => new()
    {
        Name = "gic-two",
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, BaseKv: 400),
            new Bus(3, BusType.Pq, BaseKv: 20), new Bus(4, BusType.Pq, BaseKv: 20, Vm: 1.02),
        },
        Generators = Array.Empty<Generator>(),
        Branches = new[]
        {
            new Branch(1, 2, 0.001, 0.01),
            new Branch(3, 1, 0, 0.1, IsTransformer: true),
            new Branch(4, 2, 0, 0.1, IsTransformer: true),
        },
    };

    private static string TwoSubFile(bool blockA, string k = "") => """
        { "schema": "gridsim-gic/1",
          "substations": [ { "name": "A", "lat": 50, "lon": 0, "groundOhms": 0.5, "buses": [1, 3] },
                           { "name": "B", "lat": 51, "lon": 0, "groundOhms": 0.5, "buses": [2, 4] } ],
          "transformers": [ { "branch": "3-1", "type": "gsu", "hvWindingOhms": 0.6, "blocked":
        """ + (blockA ? "true" : "false") + """
         },
                            { "branch": "4-2", "type": "gsu", "hvWindingOhms": 0.6
        """ + k + """
         } ],
          "lineOhmsOverride": { "1-2": 3 } }
        """;

    // The line is 100 km due north by construction (the distance hook), so a 1 V/km northward field gives 100 V.
    private static readonly Dictionary<string, (double, double)> Hundred = new() { ["1-2"] = (100.0, 0.0) };

    [Fact]
    public void T1_two_substation_loop_matches_the_closed_form()
    {
        GicResult r = GicSolver.Solve(GicNetwork.Build(TwoSub(), File(TwoSubFile(false)), Hundred), 1.0, 0.0);
        // Loop R = line 3/3 + winding 0.6/3 + ground 0.5 + winding 0.6/3 + ground 0.5 = 2.4 ohm; I = 100 / 2.4.
        // Bound: the network has 4 unknowns and the 1-norm condition number of its Y is 14.4 (computed with NumPy), so LU
        // with partial pivoting is good to about 3 n u kappa = 2e-14 relative; 1e-12 leaves a factor of 50.
        double total = 100.0 / 2.4;
        // 100 / 2.4 rounds to 41.66666666666667 (0x1.4d55555555556p+5); 41.666666666666664 is 125/3, one ulp lower.
        Assert.Equal(41.66666666666667, total);
        Assert.Single(r.Lines);
        Assert.Equal(total, r.Lines[0].CurrentA, total * 1e-12);
        Assert.Equal(100.0, r.Lines[0].SourceV);
        // Current flows north along the line (1 -> 2), down through B's winding into the ground, up at A.
        GicTransformerResult a = r.Transformers.Single(t => t.Branch == "3-1"), b = r.Transformers.Single(t => t.Branch == "4-2");
        double perPhase = total / 3.0;   // 13.888... A per phase
        Assert.Equal(-perPhase, a.IhA, perPhase * 1e-12);
        Assert.Equal(perPhase, b.IhA, perPhase * 1e-12);
        Assert.Equal(perPhase, a.EffectiveA, perPhase * 1e-12);
        Assert.Equal(perPhase, b.EffectiveA, perPhase * 1e-12);
        Assert.Equal(0.0, a.IxA);
        Assert.Equal(-total, r.Substations.Single(s => s.Name == "A").NeutralA, total * 1e-12);
        Assert.Equal(total, r.Substations.Single(s => s.Name == "B").NeutralA, total * 1e-12);
    }

    // ------------------------------------------------------------------ test 3: blocking device

    [Fact]
    public void T3_a_blocked_neutral_gives_exactly_zero_current()
    {
        GicResult r = GicSolver.Solve(GicNetwork.Build(TwoSub(), File(TwoSubFile(true)), Hundred), 1.0, 0.0);
        Assert.Equal(0.0, r.Lines[0].CurrentA);
        Assert.All(r.Transformers, t => Assert.Equal(0.0, t.IhA));
        Assert.All(r.Transformers, t => Assert.Equal(0.0, t.EffectiveA));
        Assert.All(r.Substations, s => Assert.Equal(0.0, s.NeutralA));
    }

    // ------------------------------------------------------------------ test 4: effective GIC of an autotransformer

    [Fact]
    public void T4_autotransformer_effective_current_follows_the_white_paper()
    {
        // [T] eq. 1 with a = V_H/V_X = 2, I_H = 10 A and I_X = 30 A per phase, both into the transformer, so the common
        // winding carries I_N/3 = 40 A: I_eff = 10 + (40 - 10) / 2 = 25 A.
        Assert.Equal(25.0, GicSolver.EffectiveCurrent(10.0, 30.0, 2.0));
        // Opposed currents: I_X = -30 A, I_N/3 = -20 A: I_eff = |10 + (-20 - 10) / 2| = 5 A.
        Assert.Equal(5.0, GicSolver.EffectiveCurrent(10.0, -30.0, 2.0));
        // A grounded-wye / delta unit has I_X = 0: |I_H|.
        Assert.Equal(7.5, GicSolver.EffectiveCurrent(-7.5, 0.0, 20.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GicSolver.EffectiveCurrent(1, 1, 0));
    }

    // ------------------------------------------------------------------ test 6: reactive loss

    [Fact]
    public void T6_reactive_loss_is_k_v_ieff()
    {
        // k is the file's value: GridSim ships no default table, as [G] gives none. V is the case voltage of the HV bus: for
        // 4-2 that is bus 2 (400 kV, 1.0 pu), not the LV bus 4 (1.02 pu).
        GicResult r = GicSolver.Solve(GicNetwork.Build(TwoSub(), File(TwoSubFile(false, ", \"kMvarPerA\": 0.5")), Hundred), 1.0, 0.0);
        GicTransformerResult b = r.Transformers.Single(t => t.Branch == "4-2");
        Assert.Equal(1.0, b.VoltagePu);
        double ieff = 100.0 / 2.4 / 3.0;
        Assert.NotNull(b.QMvar);
        Assert.Equal(0.5 * 1.0 * ieff, b.QMvar!.Value, 0.5 * ieff * 1e-12);
        Assert.Null(r.Transformers.Single(t => t.Branch == "3-1").QMvar);
        Assert.Equal(0.5 * 1.02 * 13.0, GicSolver.ReactiveLossMvar(0.5, 1.02, 13.0));
    }

    // ------------------------------------------------------------------ the guide's example (Appendix II)

    // Buses: 1 G1 (20 kV), 2 Sub 1 (345 kV), 3 Sub 2 X side (345 kV), 4 Sub 2 H side (500 kV), 5 Sub 3 (500 kV), 6 G3.
    // Lines 2-3 and 4-5 (Table B-2); T1 1-2 and T3 5-6 grounded-wye/delta, T2 3-4 autotransformer (Table B-3). Line 1 lands
    // on the autotransformer's X node and line 2 leaves its H node (Figure B-2: node 2 has Rc/3 to ground, node 3 none).
    private static GridModel Nerc() => new()
    {
        Name = "nerc-appendix-ii",
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 20), new Bus(2, BusType.Pq, BaseKv: 345), new Bus(3, BusType.Pq, BaseKv: 345),
            new Bus(4, BusType.Pq, BaseKv: 500), new Bus(5, BusType.Pq, BaseKv: 500), new Bus(6, BusType.Pv, BaseKv: 20),
        },
        Generators = Array.Empty<Generator>(),
        Branches = new[]
        {
            new Branch(1, 2, 0, 0.1, IsTransformer: true),
            new Branch(2, 3, 0.001, 0.01),
            new Branch(3, 4, 0, 0.1, IsTransformer: true),
            new Branch(4, 5, 0.001, 0.01),
            new Branch(5, 6, 0, 0.1, IsTransformer: true),
        },
    };

    private static string NercFile(string sub2GroundOhms, string model = "spherical") => """
        { "schema": "gridsim-gic/1", "distanceModel": "
        """ + model + """
        ",
          "substations": [
            { "name": "Sub 1", "lat": 33.613499, "lon": -87.373673, "groundOhms": 0.2, "buses": [1, 2] },
            { "name": "Sub 2", "lat": 34.310437, "lon": -86.365765, "groundOhms":
        """ + sub2GroundOhms + """
        , "buses": [3, 4] },
            { "name": "Sub 3", "lat": 33.955058, "lon": -84.679354, "groundOhms": 0.2, "buses": [5, 6] } ],
          "transformers": [
            { "branch": "1-2", "type": "gsu", "hvWindingOhms": 0.5 },
            { "branch": "3-4", "type": "auto", "hvWindingOhms": 0.2, "lvWindingOhms": 0.2 },
            { "branch": "5-6", "type": "gsu", "hvWindingOhms": 0.5 } ],
          "lineOhmsOverride": { "2-3": 3.525, "4-5": 4.665 } }
        """;

    // [G] Table B-6 (no blocking device) and Table B-7 (1 Mohm in Sub 2's ground), 10 V/km, orientation from north
    // towards east; columns IT1, I12, Is, Ic, I34, IT3, amperes, all three phases. (The PDF's text layer prints each row's
    // numbers one line above its orientation label; the 180-degree row is the negated 0-degree row, which fixes the
    // alignment.)
    private static readonly double[][] B6 =
    [
        [409.87, -409.87, 126.78, -536.65, 126.78, 126.78],
        [668.21, -668.21, -271.43, -396.78, -271.43, -271.43],
        [747.50, -747.50, -596.91, -150.59, -596.91, -596.91],
        [626.50, -626.50, -762.45, 135.95, -762.45, -762.45],
        [337.63, -337.63, -723.69, 386.06, -723.69, -723.69],
        [-41.71, 41.71, -491.02, 532.73, -491.02, -491.02],
        [-409.87, 409.87, -126.78, 536.65, -126.78, -126.78],
    ];

    private static readonly double[][] B7 =
    [
        [-107.60, 107.60, 107.59, 0.00, 107.59, 107.59],
        [-444.72, 444.72, 444.72, 0.00, 444.72, 444.72],
        [-662.68, 662.68, 662.68, 0.00, 662.68, 662.68],
        [-703.07, 703.07, 703.07, 0.00, 703.07, 703.07],
        [-555.08, 555.08, 555.08, 0.00, 555.08, 555.08],
        [-258.35, 258.35, 258.36, 0.00, 258.36, 258.36],
        [107.60, -107.60, -107.59, 0.00, -107.59, -107.59],
    ];

    // The guide's six currents from a GridSim result: IT1 = T1's winding current to its neutral (3 I_H), I12 = line 2-3,
    // Is = the series winding current from X to H (-3 I_H of T2), Ic = T2's common winding to ground (its neutral
    // current), I34 = line 4-5, IT3 = T3's winding current (3 I_H).
    private static double[] Columns(GicResult r)
    {
        GicTransformerResult t1 = r.Transformers.Single(t => t.Branch == "1-2"), t2 = r.Transformers.Single(t => t.Branch == "3-4"),
            t3 = r.Transformers.Single(t => t.Branch == "5-6");
        return [3 * t1.IhA, r.Lines.Single(l => l.Branch == "2-3").CurrentA, -3 * t2.IhA, t2.NeutralA, r.Lines.Single(l => l.Branch == "4-5").CurrentA, 3 * t3.IhA];
    }

    private static (double N, double E) Field(int row) => (10.0 * Math.Cos(row * 30.0 * Math.PI / 180.0), 10.0 * Math.Sin(row * 30.0 * Math.PI / 180.0));

    // The independent re-derivation of Tables B-6 and B-7 (GridSim's sign convention, so B-6 is not negated here). Written
    // from the guide's Figure B-2, not from GridSim's code: the 7-node network (three neutrals, buses 2 to 5) solved in
    // exact rational arithmetic (Python fractions) from the same double inputs (spherical distances at 111.2 km per degree,
    // the fields 10 cos/sin of row x 30 degrees, the per-phase ohms over 3), and each current rounded once to a double.
    // A two-mesh loop analysis in NumPy agrees with it to 2.2e-16 (B-6) and 7.1e-12 (B-7, where the mesh form subtracts
    // terms near 1e6 ohms) relative to the largest current.
    private static readonly double[][] B6Ref =
    [
        [-409.8734873026666, 409.8734873026666, -126.77687422880476, 536.6503615314714, -126.77687422880476, -126.77687422880476],
        [-668.2092384454867, 668.2092384454867, 271.4313120678576, 396.777926377629, 271.4313120678576, 271.4313120678576],
        [-747.498863771823, 747.498863771823, 596.9096974954175, 150.5891662764055, 596.9096974954175, 596.9096974954175],
        [-626.4967722073177, 626.4967722073177, 762.4466115247747, -135.94983931745693, 762.4466115247747, 762.4466115247747],
        [-337.6253764691566, 337.6253764691566, 723.6865717242224, -386.0611952550658, 723.6865717242224, 723.6865717242224],
        [41.712466238169064, -41.712466238169064, 491.0152994569169, -532.727765695086, 491.0152994569169, 491.0152994569169],
        [409.8734873026665, -409.8734873026665, 126.77687422880486, -536.6503615314714, 126.77687422880486, 126.77687422880486],
    ];

    private static readonly double[][] B7Ref =
    [
        [-107.59626604207116, 107.59626604207116, 107.59565692463258, 0.0006091174385773144, 107.59565692463258, 107.59565692463258],
        [-444.71748731594374, 444.71748731594374, 444.71703695874373, 0.00045035720000159967, 444.71703695874373, 444.71703695874373],
        [-662.6770170035112, 662.6770170035112, 662.6768460793978, 0.00017092411337991467, 662.6768460793978, 662.6768460793978],
        [-703.0727751423225, 703.0727751423225, 703.0729294502738, -0.00015430795138892412, 703.0729294502738, 703.0729294502738],
        [-555.0807509614401, 555.0807509614401, 555.0811891547653, -0.0004381933251973997, 555.0811891547653, 555.0811891547653],
        [-258.3552878263786, 258.3552878263786, 258.35589249152997, -0.0006046651513905238, 258.35589249152997, 258.35589249152997],
        [107.59626604207108, -107.59626604207108, -107.5956569246325, -0.0006091174385773144, -107.5956569246325, -107.5956569246325],
    ];

    [Theory]
    [InlineData("0.2", 2e-12)]
    [InlineData("1e6", 1e-10)]
    public void T5e_the_guide_example_matches_an_independent_rederivation_to_rounding(string sub2GroundOhms, double relBound)
    {
        // Bound, derived (NumPy, per row): the reference is exact to one rounding, so the gap is GridSim's LU rounding. With
        // partial pivoting the node voltages are good to 3 n u kappa_inf |V|inf (n = 7), and each current g (Va - Vb + s)
        // to twice g_max times that, plus a few u of the current for the inputs (cosines from different libraries may
        // differ by an ulp). Over the seven rows that is at most 1.2e-12 of the row's largest current with Sub 2 grounded
        // (kappa 46) and 7.8e-11 with its 1 Mohm ground (kappa 198, the floating neutral near the line sources). The
        // asserts use 2e-12 and 1e-10 of the row's largest current.
        double[][] reference = sub2GroundOhms == "0.2" ? B6Ref : B7Ref;
        GicNetwork net = GicNetwork.Build(Nerc(), File(NercFile(sub2GroundOhms)));
        for (int row = 0; row < reference.Length; row++)
        {
            (double en, double ee) = Field(row);
            double[] got = Columns(GicSolver.Solve(net, en, ee));
            double bound = relBound * reference[row].Max(Math.Abs);
            for (int c = 0; c < 6; c++)
                Assert.True(Math.Abs(got[c] - reference[row][c]) <= bound, $"ground {sub2GroundOhms} row {row * 30} deg column {c}: {got[c]:R} vs {reference[row][c]:R}");
        }
    }

    [Fact]
    public void T5a_reference_check_the_guide_table_B6_agrees_to_its_printed_precision()
    {
        // A documented reference check, not GridSim's oracle (that is T5e). Half a unit in the printed last digit (0.005 A)
        // is the printing's own rounding. It holds with the spherical distances at the inferred 111.2 km per degree; the
        // worst cell is 0.0047 A from its printed value, a margin of 0.0003 A. 1e-9 A covers the solve's rounding.
        GicNetwork net = GicNetwork.Build(Nerc(), File(NercFile("0.2")));
        for (int row = 0; row < B6.Length; row++)
        {
            (double en, double ee) = Field(row);
            double[] got = Columns(GicSolver.Solve(net, en, ee));
            for (int c = 0; c < 6; c++)
                Assert.True(Math.Abs(-got[c] - B6[row][c]) <= 0.005 + 1e-9, $"B-6 row {row * 30} deg column {c}: {-got[c]} vs {B6[row][c]}");
        }
    }

    // The cells of Table B-7 (row, column) whose printed value is one printed unit from the correctly rounded value of both
    // GridSim and the independent re-derivation (T5e): IT1, I12 at 150 degrees; Is, I34, IT3 at 0 and 180 degrees. The
    // largest gap from the exact figure is 0.00566 A. Its cause in the guide's figures is unexplained (no km-per-degree
    // constant fits B-6 and B-7 together to half a unit; the best, 111.19985, still leaves 0.0055 A).
    private static readonly HashSet<(int Row, int Column)> B7Unexplained = [(0, 2), (0, 4), (0, 5), (5, 0), (5, 1), (6, 2), (6, 4), (6, 5)];

    [Fact]
    public void T5b_reference_check_the_guide_table_B7_and_its_unexplained_cells()
    {
        // A documented reference check, not GridSim's oracle (that is T5e). The guide blocks by setting Sub 2's ground to
        // 1 Mohm. Every cell is within half a printed unit (0.005 A) of its printed value, except the eight listed above,
        // where the printed value is the neighbour of the correctly rounded one (exactly one printed unit away). Asserting
        // that set exactly keeps the discrepancy visible: a change in either direction fails here.
        GicNetwork net = GicNetwork.Build(Nerc(), File(NercFile("1e6")));
        for (int row = 0; row < B7.Length; row++)
        {
            (double en, double ee) = Field(row);
            double[] got = Columns(GicSolver.Solve(net, en, ee));
            for (int c = 0; c < 6; c++)
            {
                if (B7Unexplained.Contains((row, c)))
                {
                    double rounded = Math.Round(got[c], 2, MidpointRounding.AwayFromZero);
                    Assert.True(Math.Abs(got[c] - B7[row][c]) > 0.005, $"B-7 row {row * 30} deg column {c} now agrees: {got[c]} vs {B7[row][c]}");
                    Assert.True(Math.Abs(Math.Abs(rounded - B7[row][c]) - 0.01) <= 1e-9, $"B-7 row {row * 30} deg column {c}: {got[c]} vs {B7[row][c]}");
                }
                else
                    Assert.True(Math.Abs(got[c] - B7[row][c]) <= 0.005 + 1e-9, $"B-7 row {row * 30} deg column {c}: {got[c]} vs {B7[row][c]}");
            }
        }
    }

    [Fact]
    public void T5c_the_guide_table_B5_sources_for_an_eastward_field()
    {
        // [G] Table B-5: 10 V/km eastward gives -929.6 V and -1552.3 V (from-end minus to-end); GridSim's sources along the
        // path are their negatives, to the printed 0.1 V (half unit 0.05 V).
        GicResult r = GicSolver.Solve(GicNetwork.Build(Nerc(), File(NercFile("0.2"))), 0.0, 10.0);
        Assert.True(Math.Abs(r.Lines.Single(l => l.Branch == "2-3").SourceV - 929.6) <= 0.05);
        Assert.True(Math.Abs(r.Lines.Single(l => l.Branch == "4-5").SourceV - 1552.3) <= 0.05);
    }

    [Fact]
    public void T5d_distances_wgs84_follow_eqs_A3_A7_and_spherical_the_table_B4()
    {
        // Eqs. A.3 and A.7 evaluated independently (Python, libm cos) for the two lines of Table B-1's coordinates. Bound:
        // a few operations on values near 100, so 1e-12 relative is far above the rounding.
        (double n1, double e1) = GicDistances.Between(33.613499, -87.373673, 34.310437, -86.365765, GicDistanceModel.Wgs84);
        (double n2, double e2) = GicDistances.Between(34.310437, -86.365765, 33.955058, -84.679354, GicDistanceModel.Wgs84);
        Assert.Equal(77.30612704205039, n1, 77.4 * 1e-12);
        Assert.Equal(93.15699811883968, e1, 93.2 * 1e-12);
        Assert.Equal(-39.42063893311914, n2, 39.5 * 1e-12);
        Assert.Equal(155.55621025546998, e2, 155.6 * 1e-12);
        // Table B-4 (from-end minus to-end, so negated here) against the spherical model, to one unit in the printed last
        // digit. Any constant from 111.19856 to 111.1999 km per degree (with the mean latitude) matches all four printed
        // values to half a unit, but 111.2 does not: line 1 north is 77.49951 against 77.499 and line 2 east 155.2251
        // against 155.22 (Python check). GridSim keeps 111.2 because Table B-6 is reproduced with it; hence one unit here.
        (double s1n, double s1e) = GicDistances.Between(33.613499, -87.373673, 34.310437, -86.365765, GicDistanceModel.Spherical);
        (double s2n, double s2e) = GicDistances.Between(34.310437, -86.365765, 33.955058, -84.679354, GicDistanceModel.Spherical);
        Assert.True(Math.Abs(-s1n - -77.499) <= 0.001, s1n.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Math.Abs(-s1e - -92.96) <= 0.01, s1e.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Math.Abs(-s2n - 39.518) <= 0.001, s2n.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Math.Abs(-s2e - -155.22) <= 0.01, s2e.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ test 2: superposition

    [Fact]
    public void T2_superposition_holds_to_1e_12()
    {
        // The network is linear in the field. Bound: Y here has 7 unknowns and a 1-norm condition number of 46 (NumPy), so
        // each solve is good to about 3 n u kappa = 1.1e-13 relative to the largest current; three solves and one
        // combination stay under 4e-13; 1e-12 of the largest current leaves a margin above 2.
        GicNetwork net = GicNetwork.Build(Nerc(), File(NercFile("0.2")));
        double[] both = Columns(GicSolver.Solve(net, 3.0, -7.0));
        double[] north = Columns(GicSolver.Solve(net, 1.0, 0.0));
        double[] east = Columns(GicSolver.Solve(net, 0.0, 1.0));
        double scale = both.Max(Math.Abs);
        for (int c = 0; c < 6; c++)
            Assert.True(Math.Abs(both[c] - (3.0 * north[c] - 7.0 * east[c])) <= 1e-12 * scale, $"column {c}");
    }

    // ------------------------------------------------------------------ test 7: determinism

    [Fact]
    public void T7_two_runs_give_the_same_bits()
    {
        GicNetwork net = GicNetwork.Build(Nerc(), File(NercFile("0.2")));
        double[] a = Columns(GicSolver.Solve(net, 2.5, -1.25)), b = Columns(GicSolver.Solve(GicNetwork.Build(Nerc(), File(NercFile("0.2"))), 2.5, -1.25));
        for (int c = 0; c < 6; c++) Assert.Equal(BitConverter.DoubleToInt64Bits(a[c]), BitConverter.DoubleToInt64Bits(b[c]));
        IReadOnlyList<GicSweepEntry> s1 = GicSolver.Sweep(net, 10), s2 = GicSolver.Sweep(net, 10);
        Assert.Equal(3, s1.Count);
        for (int u = 0; u < s1.Count; u++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(s1[u].MaxEffectiveA), BitConverter.DoubleToInt64Bits(s2[u].MaxEffectiveA));
            Assert.Equal(s1[u].DirectionDeg, s2[u].DirectionDeg);
        }
    }

    [Fact]
    public void Sweep_maximum_is_at_least_every_sampled_direction()
    {
        // The sweep at 30-degree steps visits the field directions of Table B-6 at 1 V/km; each transformer's maximum is
        // the largest of those twelve solves, each 1/10 of the table's 10 V/km values by linearity.
        GicNetwork net = GicNetwork.Build(Nerc(), File(NercFile("0.2")));
        IReadOnlyList<GicSweepEntry> s = GicSolver.Sweep(net, 30);
        for (int u = 0; u < 3; u++)
        {
            double best = -1;
            for (int k = 0; k < 12; k++)
            {
                double th = k * 30.0 * Math.PI / 180.0;
                best = Math.Max(best, GicSolver.Solve(net, GridSim.Core.Numerics.DetMath.Cos(th), GridSim.Core.Numerics.DetMath.Sin(th)).Transformers[u].EffectiveA);
            }
            Assert.Equal(best, s[u].MaxEffectiveA);
        }
        // T1 at 60 degrees: Table B-6 IT1 = 747.50 A at 10 V/km, all three phases, so 24.92 A per phase at 1 V/km is in
        // the sweep's reach: its maximum is at least that, to the table's half unit scaled (0.005 / 30).
        Assert.True(s[0].MaxEffectiveA >= 747.50 / 30.0 - 0.005 / 30.0);
    }

    // ------------------------------------------------------------------ file and network refusals

    [Theory]
    [InlineData("{ \"schema\": \"gridsim-gic/1\", \"substations\": [], \"extra\": 1 }", "unknown key 'extra'")]
    [InlineData("{ \"schema\": \"gridsim-gic/2\", \"substations\": [] }", "schema")]
    [InlineData("{ \"schema\": \"gridsim-gic/1\", \"substations\": [ { \"name\": \"A\", \"lat\": 0, \"lon\": 0, \"groundOhms\": 0, \"buses\": [] } ] }", "greater than 0")]
    [InlineData("{ \"schema\": \"gridsim-gic/1\", \"substations\": [ { \"name\": \"A\", \"lat\": 0, \"lon\": 0, \"groundOhms\": 1, \"buses\": [1] }, { \"name\": \"B\", \"lat\": 0, \"lon\": 0, \"groundOhms\": 1, \"buses\": [1] } ] }", "already in substation 'A'")]
    [InlineData("{ \"schema\": \"gridsim-gic/1\", \"substations\": [], \"transformers\": [ { \"branch\": \"1-2\", \"type\": \"auto\", \"hvWindingOhms\": 1 } ] }", "lvWindingOhms is required")]
    [InlineData("{ \"schema\": \"gridsim-gic/1\", \"substations\": [], \"transformers\": [ { \"branch\": \"1-2\", \"type\": \"yy\" } ] }", "is not gsu")]
    [InlineData("{ \"schema\": \"gridsim-gic/1\", \"distanceModel\": \"flat\", \"substations\": [] }", "not wgs84 or spherical")]
    public void A_bad_file_is_refused_by_name(string json, string message)
    {
        var ex = Assert.Throws<InvalidDataException>(() => File(json));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transformer_the_file_does_not_describe_is_refused()
    {
        string json = "{ \"schema\": \"gridsim-gic/1\", \"substations\": [ { \"name\": \"A\", \"lat\": 50, \"lon\": 0, \"groundOhms\": 0.5, \"buses\": [1, 3] }, " +
            "{ \"name\": \"B\", \"lat\": 51, \"lon\": 0, \"groundOhms\": 0.5, \"buses\": [2, 4] } ], " +
            "\"transformers\": [ { \"branch\": \"3-1\", \"type\": \"gsu\", \"hvWindingOhms\": 0.6 } ] }";
        var ex = Assert.Throws<InvalidDataException>(() => GicNetwork.Build(TwoSub(), File(json)));
        Assert.Contains("'4-2' is a transformer", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the verb

    private const string NercM = """
        function mpc = nerc
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        	1	3	0	0	0	0	1	1	0	20	1	1.1	0.9;
        	2	1	0	0	0	0	1	1	0	345	1	1.1	0.9;
        	3	1	0	0	0	0	1	1	0	345	1	1.1	0.9;
        	4	1	0	0	0	0	1	1	0	500	1	1.1	0.9;
        	5	1	0	0	0	0	1	1	0	500	1	1.1	0.9;
        	6	1	0	0	0	0	1	1	0	20	1	1.1	0.9;
        ];
        mpc.gen = [
        	1	0	0	999	-999	1	100	1	999	0	0	0	0	0	0	0	0	0	0	0	0;
        ];
        mpc.branch = [
        	1	2	0	0.1	0	0	0	0	1	0	1	-360	360;
        	2	3	0.001	0.01	0	0	0	0	0	0	1	-360	360;
        	3	4	0	0.1	0	0	0	0	1	0	1	-360	360;
        	4	5	0.001	0.01	0	0	0	0	0	0	1	-360	360;
        	5	6	0	0.1	0	0	0	0	1	0	1	-360	360;
        ];
        """;

    [Fact]
    public void The_gic_verb_writes_its_result_with_the_file_hash_and_matches_the_library()
    {
        System.IO.File.WriteAllText(Path.Combine(_dir, "nerc.m"), NercM);
        string gic = NercFile("0.2");
        System.IO.File.WriteAllText(Path.Combine(_dir, "g.json"), gic);
        CliProcess.Result r = JournalTestSupport.Run(_dir, "gic", "nerc.m", "--gic", "g.json", "--field", "0,10", "--direction-sweep", "30", "--out", "o.json");
        Assert.True(r.ExitCode == 0, r.ToString());
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(Path.Combine(_dir, "o.json")));
        JsonElement root = doc.RootElement;
        Assert.Equal("gridsim-gic-result/1", root.GetProperty("schema").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(System.IO.File.ReadAllBytes(Path.Combine(_dir, "g.json")))), root.GetProperty("gicFileSha256").GetString());
        // The verb's numbers are the library's, bit for bit (JSON numbers round-trip).
        GicResult lib = GicSolver.Solve(GicNetwork.Build(Nerc(), File(gic)), 0.0, 10.0);
        JsonElement[] tr = root.GetProperty("transformers").EnumerateArray().ToArray();
        Assert.Equal(3, tr.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(lib.Transformers[i].Branch, tr[i].GetProperty("branch").GetString());
            Assert.Equal(lib.Transformers[i].IhA, tr[i].GetProperty("ihA").GetDouble());
            Assert.Equal(lib.Transformers[i].EffectiveA, tr[i].GetProperty("effectiveA").GetDouble());
        }
        Assert.Equal(3, root.GetProperty("sweep").GetArrayLength());
    }

    [Theory]
    [InlineData("--field", "1")]
    [InlineData("--direction-sweep", "0")]
    public void The_gic_verb_refuses_bad_usage_with_exit_2(string flag, string value)
    {
        System.IO.File.WriteAllText(Path.Combine(_dir, "nerc.m"), NercM);
        System.IO.File.WriteAllText(Path.Combine(_dir, "g.json"), NercFile("0.2"));
        CliProcess.Result r = JournalTestSupport.Run(_dir, "gic", "nerc.m", "--gic", "g.json", flag, value);
        Assert.True(r.ExitCode == 2, r.ToString());
    }

    [Fact]
    public void The_gic_verb_refuses_with_pf_by_name()
    {
        // A valid field is given, so the only reason left to refuse is --with-pf itself (the reactive loss is not fed back
        // into a power flow); the message names that flag.
        System.IO.File.WriteAllText(Path.Combine(_dir, "nerc.m"), NercM);
        System.IO.File.WriteAllText(Path.Combine(_dir, "g.json"), NercFile("0.2"));
        CliProcess.Result r = JournalTestSupport.Run(_dir, "gic", "nerc.m", "--gic", "g.json", "--field", "0,10", "--with-pf");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("unknown flag '--with-pf'", r.StdErr, StringComparison.Ordinal);
    }
}
