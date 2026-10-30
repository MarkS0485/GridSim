using System.Numerics;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Tranche A (October 2026): the PSS/E RAW reader (revisions 33, 34, 35 here; revision 30 in PsseRev30AndSeqTests) and its writer.
/// <para>The round-trip fixtures case14.raw, case39.raw and case118.raw in tests/GridSim.Tests/fixtures/psse were written
/// by <see cref="PsseRawWriter"/> (revision 33) from the MATPOWER cases (case14 and case118 from data/matpower/lib,
/// case39 from MATPOWER's data folder, kept beside them). Each must read back to the MATPOWER model bit for bit: every
/// Ybus cell and every solved voltage and angle, with the branches in the order the RAW file must hold them (lines, then
/// transformers). t_case9_save2psse.raw (+ .m) and t_psse_case3.raw are MATPOWER's own test files (BSD licence, see the
/// fixture README): a third-party revision 33 file, and PSS/E's own revision 30 sample (read since revision 30 support,
/// checked value by value in PsseRev30AndSeqTests).</para>
/// <para>sample-v35.raw is hand-written in the revision 35 layout and holds every section; its expected values are worked
/// out by hand in the tests below.</para>
/// </summary>
public class PsseRawParserTests
{
    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    private static GridModel MatpowerCase(string name) => name == "case39"
        ? MatpowerParser.ParseFile(Fixture("case39.m"), frequencyHz: 60)
        : MatpowerParser.ParseFile(Path.Combine(CliProcess.Root(), "data", "matpower", "lib", name + ".m"), frequencyHz: 60);

    private static void AssertSameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    private static void AssertSameYbus(GridModel expected, GridModel actual)
    {
        Complex[,] a = YBus.Build(expected).Y, b = YBus.Build(actual).Y;
        Assert.Equal(a.GetLength(0), b.GetLength(0));
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++)
            {
                AssertSameBits(a[i, j].Real, b[i, j].Real, $"G[{i},{j}]");
                AssertSameBits(a[i, j].Imaginary, b[i, j].Imaginary, $"B[{i},{j}]");
            }
    }

    private static void AssertSameSolve(GridModel expected, GridModel actual)
    {
        PowerFlowResult r1 = new NewtonRaphsonPowerFlow().Solve(expected), r2 = new NewtonRaphsonPowerFlow().Solve(actual);
        Assert.True(r1.Converged && r2.Converged, "both solves must converge");
        Assert.Equal(r1.BusIds, r2.BusIds);
        for (int i = 0; i < r1.Vm.Length; i++)
        {
            AssertSameBits(r1.Vm[i], r2.Vm[i], $"Vm bus {r1.BusIds[i]}");
            AssertSameBits(r1.VaDeg[i], r2.VaDeg[i], $"Va bus {r1.BusIds[i]}");
        }
    }

    // ---- Round trip against the MATPOWER path --------------------------------------------------------------------

    [Theory]
    [InlineData("case14")]
    [InlineData("case39")]
    [InlineData("case118")]
    public void Raw_fixture_reads_back_to_the_matpower_case_bit_for_bit(string name)
    {
        GridModel mp = MatpowerCase(name);
        PsseWriteResult w = PsseRawWriter.Write(mp);
        PsseParseResult raw = PsseRawParser.ParseFile(Fixture(name + ".raw"));
        GridModel reordered = mp.CopyWith(branches: w.BranchOrder.Select(k => mp.Branches[k]).ToList());

        Assert.Equal(33, raw.Revision);
        Assert.Equal(mp.BaseMva, raw.Model.BaseMva);
        Assert.Equal(60.0, raw.Model.FrequencyHz);
        Assert.Equal(mp.Buses.Count, raw.Model.Buses.Count);
        for (int i = 0; i < mp.Buses.Count; i++)
        {
            Bus e = mp.Buses[i], a = raw.Model.Buses[i];
            Assert.Equal((e.Id, e.Type, e.Name, e.Zip), (a.Id, a.Type, a.Name, a.Zip));
            foreach ((double x, double y, string f) in new[] { (e.Pd, a.Pd, "Pd"), (e.Qd, a.Qd, "Qd"), (e.Gs, a.Gs, "Gs"), (e.Bs, a.Bs, "Bs"),
                         (e.BaseKv, a.BaseKv, "BaseKv"), (e.Vm, a.Vm, "Vm"), (e.VaDeg, a.VaDeg, "Va") })
                AssertSameBits(x, y, $"bus {e.Id} {f}");
            Assert.Equal(e.VminPu, a.VminPu);
            Assert.Equal(e.VmaxPu, a.VmaxPu);
        }
        Assert.Equal(mp.AllGenerators.Count, raw.Model.AllGenerators.Count);
        for (int k = 0; k < mp.AllGenerators.Count; k++)
        {
            Generator e = mp.AllGenerators[k], a = raw.Model.AllGenerators[k];
            Assert.Equal((e.Bus, e.InService), (a.Bus, a.InService));
            foreach ((double x, double y, string f) in new[] { (e.Pg, a.Pg, "Pg"), (e.Qg, a.Qg, "Qg"), (e.Vg, a.Vg, "Vg"), (e.Qmin, a.Qmin, "Qmin"),
                         (e.Qmax, a.Qmax, "Qmax"), (e.Pmin, a.Pmin, "Pmin"), (e.Pmax, a.Pmax, "Pmax"), (e.MBase, a.MBase, "MBase") })
                AssertSameBits(x, y, $"generator {k + 1} {f}");
        }
        Assert.Equal(reordered.Branches.Count, raw.Model.Branches.Count);
        for (int k = 0; k < reordered.Branches.Count; k++)
        {
            Branch e = reordered.Branches[k], a = raw.Model.Branches[k];
            Assert.Equal((e.From, e.To, e.InService), (a.From, a.To, a.InService));
            foreach ((double x, double y, string f) in new[] { (e.R, a.R, "R"), (e.X, a.X, "X"), (e.B, a.B, "B"), (e.TapRatio == 0 ? 1.0 : e.TapRatio, a.TapRatio, "tap"),
                         (e.PhaseShiftDeg, a.PhaseShiftDeg, "shift"), (e.RateAMva, a.RateAMva, "rate A"), (e.RateBMva, a.RateBMva, "rate B"), (e.RateCMva, a.RateCMva, "rate C") })
                AssertSameBits(x, y, $"branch {k + 1} {f}");
        }
        AssertSameYbus(reordered, raw.Model);
        AssertSameSolve(reordered, raw.Model);
    }

    [Theory]
    [InlineData("case14")]
    [InlineData("case39")]
    [InlineData("case118")]
    public void Writer_reproduces_the_committed_fixture(string name)
    {
        string written = PsseRawWriter.Write(MatpowerCase(name)).Text;
        string committed = File.ReadAllText(Fixture(name + ".raw")).Replace("\r\n", "\n");
        Assert.Equal(committed, written);
    }

    [Fact]
    public void Case118_charged_ratio_one_transformers_are_written_as_lines_with_a_note()
    {
        // case118 has two transformers (86-87 and 68-116) with ratio 1, no shift and line charging; RAW transformers have no
        // line charging, so they are written as lines (same admittance stamp) and the writer says so.
        GridModel mp = MatpowerCase("case118");
        PsseWriteResult w = PsseRawWriter.Write(mp);
        Assert.Contains(w.Notes, n => n.StartsWith("2 transformer(s) with ratio 1", StringComparison.Ordinal));
        PsseParseResult raw = PsseRawParser.ParseFile(Fixture("case118.raw"));
        Assert.Equal(2, raw.Model.Branches.Count(b => !b.IsTransformer && ((b.From, b.To) is (86, 87) or (68, 116))));
    }

    [Theory]
    [InlineData("case14", 33)]
    [InlineData("case14", 34)]
    [InlineData("case14", 35)]
    [InlineData("case118", 34)]
    [InlineData("case118", 35)]
    public void Every_revision_layout_round_trips(string name, int revision)
    {
        GridModel mp = MatpowerCase(name);
        PsseWriteResult w = PsseRawWriter.Write(mp, revision);
        PsseParseResult raw = PsseRawParser.ParseWithDiagnostics(w.Text, name);
        Assert.Equal(revision, raw.Revision);
        GridModel reordered = mp.CopyWith(branches: w.BranchOrder.Select(k => mp.Branches[k]).ToList());
        AssertSameYbus(reordered, raw.Model);
        AssertSameSolve(reordered, raw.Model);
    }

    // ---- MATPOWER's own PSS/E test files (third party) ---------------------------------------------------------------

    [Fact]
    public void Matpower_save2psse_file_with_a_zero_winding_voltage_is_refused_naming_the_line()
    {
        // MATPOWER's save2psse writes a branch's TAP column as WINDV1, so its phase shifter 9-4 (TAP 0, "no ratio" in
        // MATPOWER) arrives as WINDV1 = 0, which is not a PSS/E winding voltage.
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseFile(Fixture("t_case9_save2psse.raw"),
            new PsseReadOptions { AllowUnmodelled = true }));
        Assert.Contains("line 40 (TRANSFORMER data)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WINDV1 is 0", ex.Message, StringComparison.Ordinal);
    }

    // Test changed 2026-10-25 (device item B, two-terminal LCC links modelled; under Mark's delegation): this test pinned
    // the refusal of the file's two in-service DC links. They are modelled now, so it pins that they are read as links
    // (MATPOWER's dcline rows written as PSS/E records: 4 and 3 MW power orders at 500 kV, no line or commutating
    // resistance, so each delivers its order with no loss, as MATPOWER's lossless dcline does).
    [Fact]
    public void Matpower_save2psse_file_holds_in_service_dc_links_which_are_read_as_lcc_links()
    {
        string text = File.ReadAllText(Fixture("t_case9_save2psse.raw")).Replace("    0, 0,     5,", "    1, 0,     5,");
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(text, "t_case9_save2psse");
        Assert.Equal(new[] { ("DCLINE 1", 3, 4, 4.0), ("DCLINE 2", 4, 7, 3.0) },
            r.Model.LccLinks.Select(l => (l.Name, l.Rectifier.Bus, l.Inverter.Bus, l.Setpoint)));
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(r.Model);
        Assert.True(pf.Converged, pf.LccNote);
        foreach (LccLinkResult d in pf.LccLinks!)
            Assert.Equal(d.RectifierMw, d.InverterMw, 12);
    }

    [Fact]
    public void Matpower_save2psse_file_matches_its_matpower_case_bit_for_bit()
    {
        // The one field corrected (WINDV1 0 -> 1, which MATPOWER's own reader treats as ratio 1) and DC links allowed: the
        // network must be the MATPOWER case's, cell for cell, and solve to the same bits.
        string text = File.ReadAllText(Fixture("t_case9_save2psse.raw")).Replace("    0, 0,     5,", "    1, 0,     5,");
        PsseParseResult raw = PsseRawParser.ParseWithDiagnostics(text, "t_case9_save2psse", new PsseReadOptions { AllowUnmodelled = true });
        GridModel mp = MatpowerParser.ParseFile(Fixture("t_case9_save2psse.m"), frequencyHz: 60);
        Assert.Equal(33, raw.Revision);
        Assert.Equal(60.0, raw.Model.FrequencyHz);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, raw.Model.Buses.Select(b => b.Id));
        Assert.Equal("bus 5", raw.Model.Buses[4].Name);
        // Test changed 2026-10-25 (device item B): the two DC links are modelled now (MatpowerParser does not read
        // mpc.dcline), so the network identity is checked with the links taken out of the PSS/E model; the links
        // themselves are pinned in Matpower_save2psse_file_holds_in_service_dc_links_which_are_read_as_lcc_links.
        Assert.Equal(2, raw.Model.LccLinks.Count);
        GridModel network = raw.Model.CopyWith(lccLinks: Array.Empty<LccDcLink>());
        AssertSameYbus(mp, network);
        AssertSameSolve(mp, network);
    }

    // Test changed 2026-10-24 (Tranche A, revision 30 support; under Mark's delegation): this test pinned the refusal of
    // revision 30 ("PSS/E revision 30 is not supported; supported: 33, 34, 35", line 1), a scope limit that this commit
    // lifts on purpose. It now pins what the same file must do instead: it is read as revision 30 and refused for the
    // devices it holds that GridSim does not model, each named, and with them allowed it reads. The refusal of a revision
    // the reader does not support is still pinned, for 31 and 32, in PsseRev30AndSeqTests, with the values of this file.
    [Fact]
    public void Revision_30_file_is_read_and_refused_only_for_its_unmodelled_devices()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseFile(Fixture("t_psse_case3.raw")));
        Assert.Contains("holds in-service devices GridSim does not model", ex.Message, StringComparison.Ordinal);
        // 2026-10-25: its two-terminal links are capacitor-commutated (XCAP not 0), which is not modelled.
        Assert.Contains("2 capacitor-commutated two-terminal DC link(s)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is not supported", ex.Message, StringComparison.Ordinal);
        Assert.Equal(30, PsseRawParser.ParseFile(Fixture("t_psse_case3.raw"), new PsseReadOptions { AllowUnmodelled = true }).Revision);
    }

    // ---- The hand-written revision 35 sample -----------------------------------------------------------------------

    private static PsseParseResult Sample() => PsseRawParser.ParseFile(Fixture("sample-v35.raw"));

    [Fact]
    public void Sample_v35_header_buses_and_metadata()
    {
        PsseParseResult r = Sample();
        Assert.Equal(35, r.Revision);
        Assert.Equal(50.0, r.Model.FrequencyHz);
        Assert.Equal(100.0, r.Model.BaseMva);
        Assert.Equal(new[] { "GRIDSIM PSS/E READER SAMPLE", "REVISION 35 LAYOUT, EVERY SECTION, HAND WRITTEN" }, r.Titles);
        Assert.Equal("sample-v35", r.Model.Name);
        // Bus 5 is isolated (IDE 4) and dropped; star point 11 follows the file's buses (next power of ten above 6, plus 1).
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 11 }, r.Model.Buses.Select(b => b.Id));
        Assert.Equal(new[] { 11 }, r.StarPointBuses);
        Bus b1 = r.Model.Buses[0], b2 = r.Model.Buses[1], b3 = r.Model.Buses[2], b4 = r.Model.Buses[3];
        Assert.Equal(("GRID 400", BusType.Slack, 400.0, 1.02, 0.0, (double?)0.95, (double?)1.05), (b1.Name, b1.Type, b1.BaseKv, b1.Vm, b1.VaDeg, b1.VminPu, b1.VmaxPu));
        Assert.Equal(((double?)0.94, (double?)1.06), (b2.VminPu, b2.VmaxPu));
        Assert.Equal((BusType.Pv, (double?)null, (double?)null), (b3.Type, b3.VminPu, b3.VmaxPu));   // limits not written
        Assert.Equal((BusType.Pq, 1.0, 0.0), (b4.Type, b4.Vm, b4.VaDeg));                              // VM, VA defaults
        Assert.Equal(new PsseBusMeta(2, 2, 2), r.BusMeta[3]);
        Assert.Equal(new PsseBusMeta(1, 1, 1), r.BusMeta[5]);    // dropped bus still has its metadata
        Assert.Equal(new PsseBusMeta(1, 1, 1), r.BusMeta[11]);   // star point takes winding 1's (bus 2)
        Assert.Equal("NORTH", r.AreaNames[1]);
        Assert.Equal("ZONE TWO", r.ZoneNames[2]);
        Assert.Equal("OWNER ONE", r.OwnerNames[1]);
    }

    [Fact]
    public void Sample_v35_loads_shunts_and_zip()
    {
        PsseParseResult r = Sample();
        Bus b2 = r.Model.Buses.Single(b => b.Id == 2), b3 = r.Model.Buses.Single(b => b.Id == 3), b6 = r.Model.Buses.Single(b => b.Id == 6),
            b1 = r.Model.Buses.Single(b => b.Id == 1);
        // Bus 2: PL 50 + IP 10, QL 20 + IQ 5, a ZIP load; YP 4 / YQ -2 a shunt, plus the fixed shunt 0.5 / 15 and the line-end
        // susceptance BI 0.01 pu of branch 2-6 (1 MVAr at 1 pu).
        Assert.Equal((60.0, 25.0), (b2.Pd, b2.Qd));
        Assert.NotNull(b2.Zip);
        Assert.Equal(0.0, b2.Zip!.Ap);
        Assert.Equal(10.0 / 60.0, b2.Zip.Bp);
        Assert.Equal(50.0 / 60.0, b2.Zip.Cp);
        Assert.Equal(5.0 / 25.0, b2.Zip.Bq);
        Assert.Equal(20.0 / 25.0, b2.Zip.Cq);
        Assert.Equal(4.0 + 0.5, b2.Gs);
        Assert.Equal(-2.0 + 15.0 + 0.01 * 100.0, b2.Bs);
        // Bus 3: PL 30 less distributed generation 6 (DGENF 1), QL 10 less 1; the out-of-service load and fixed shunt are
        // skipped; the switched shunt is fixed at BINIT 7.5.
        Assert.Equal((24.0, 9.0, 0.0, 7.5), (b3.Pd, b3.Qd, b3.Gs, b3.Bs));
        Assert.Null(b3.Zip);
        Assert.Equal(0.02 * 100.0, b6.Bs);
        // Bus 1: the magnetising admittance of the two-winding transformer (CM 1, pu on the system base).
        Assert.Equal((1e-3 * 100.0, -5e-3 * 100.0), (b1.Gs, b1.Bs));
        Assert.Contains(r.Notes, n => n.StartsWith("1 load(s) have a constant-admittance part", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 load(s) have a constant-current part", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 in-service load(s) carry distributed generation", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("skipped 1 out-of-service load(s)", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("skipped 1 load(s) on dropped isolated", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 switched shunt(s) are fixed at their initial susceptance BINIT", StringComparison.Ordinal));
    }

    [Fact]
    public void Sample_v35_generators()
    {
        PsseParseResult r = Sample();
        Assert.Equal(new[] { new PsseMachineKey(1, "1"), new PsseMachineKey(3, "1"), new PsseMachineKey(3, "2") }, r.MachineKeys);
        Generator g1 = r.Model.AllGenerators[0], g31 = r.Model.AllGenerators[1], g32 = r.Model.AllGenerators[2];
        Assert.Equal((0.0, 1.02, -500.0, 500.0, 0.0, 900.0, 1000.0, true), (g1.Pg, g1.Vg, g1.Qmin, g1.Qmax, g1.Pmin, g1.Pmax, g1.MBase, g1.InService));
        Assert.Equal((40.0, 5.0, 100.0), (g31.Pg, g31.Qg, g31.MBase));   // MBASE 0: the system base
        Assert.False(g32.InService);                                       // STAT 0: kept, out of service
        Assert.Equal(2, r.Model.Generators.Count);
        Assert.Contains(r.Notes, n => n.StartsWith("1 generator(s) give MBASE 0", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 generator(s) regulate a remote bus (IREG)", StringComparison.Ordinal));
    }

    [Fact]
    public void Sample_v35_branches_switching_device_and_transformers()
    {
        PsseParseResult r = Sample();
        // Order: the in-service line 2-6 (line 2-6 circuit 2 is out of service, 2-5 reaches the isolated bus), the switching
        // device, the two-winding transformer, then the three windings.
        Assert.Equal(new[]
        {
            new PsseBranchKey("branch", 2, 6, 0, "1", 0),
            new PsseBranchKey("switching device", 6, 2, 0, "S1", 0),
            new PsseBranchKey("transformer", 1, 2, 0, "1", 0),
            new PsseBranchKey("transformer3", 2, 3, 4, "1", 1),
            new PsseBranchKey("transformer3", 2, 3, 4, "1", 2),
            new PsseBranchKey("transformer3", 2, 3, 4, "1", 3),
        }, r.BranchKeys);
        IReadOnlyList<Branch> br = r.Model.Branches;
        Assert.Equal((2, 6, 0.01, 0.05, 0.1, 200.0, 220.0, 240.0, "LINE 2-6", false), (br[0].From, br[0].To, br[0].R, br[0].X, br[0].B, br[0].RateAMva, br[0].RateBMva, br[0].RateCMva, br[0].Name, br[0].IsTransformer));
        // The switching device is written with X = 0: it gets PSS/E's zero-impedance threshold.
        Assert.Equal((6, 2, 0.0, PsseRawParser.ZeroImpedanceXPu, 100.0, "BUS COUPLER"), (br[1].From, br[1].To, br[1].R, br[1].X, br[1].RateAMva, br[1].Name));
        // Two-winding: CW 2 (kV), so ratio = (410 / 400) / (132 / 132) = 1.025; CZ 2, so Z is moved from the winding base
        // (240 MVA, NOMV1 400 kV) to the system base (100 MVA, bus 1 base 400 kV): Z x 100 / 240.
        Assert.True(br[2].IsTransformer);
        Assert.Equal(1.025, br[2].TapRatio, 1e-15);
        Assert.Equal(0.002 * 100 / 240, br[2].R, 1e-17);
        Assert.Equal(0.12 * 100 / 240, br[2].X, 1e-16);
        Assert.Equal((240.0, 260.0, 280.0, "GSP T1"), (br[2].RateAMva, br[2].RateBMva, br[2].RateCMva, br[2].Name));
        // Three-winding: Z12 = 0.01 + j0.10, Z23 = 0.02 + j0.20, Z31 = 0.03 + j0.24 (CZ 1), so the star impedances are
        // Z1 = 0.01 + j0.07, Z2 = 0 + j0.03, Z3 = 0.02 + j0.17; ratios 1.05, 1.00, 0.98; winding 2 shifted 5 degrees.
        (double R, double X, double Tap, double Shift, double Rate)[] expect =
            { (0.01, 0.07, 1.05, 0, 90), (0.0, 0.03, 1.0, 5, 60), (0.02, 0.17, 0.98, 0, 30) };
        for (int n = 0; n < 3; n++)
        {
            Branch w = br[3 + n];
            Assert.Equal(new[] { 2, 3, 4 }[n], w.From);
            Assert.Equal(11, w.To);
            Assert.Equal(expect[n].R, w.R, 1e-15);
            Assert.Equal(expect[n].X, w.X, 1e-15);
            Assert.Equal((expect[n].Tap, expect[n].Shift, expect[n].Rate, $"T3W winding {n + 1}"), (w.TapRatio, w.PhaseShiftDeg, w.RateAMva, w.Name));
        }
        Bus star = r.Model.Buses.Single(b => b.Id == 11);
        Assert.Equal((BusType.Pq, 1.01, -3.0, 132.0, "T3W star point"), (star.Type, star.Vm, star.VaDeg, star.BaseKv, star.Name));
        Assert.Contains(r.Notes, n => n.StartsWith("skipped 1 out-of-service branch(es)", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 branch(es) or switching device(s) have R = X = 0", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 transformer(s) have automatic tap or phase control (COD)", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 impedance correction table(s) read past; no in-service transformer uses one", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 out-of-service FACTS device(s) read past", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 node-breaker substation(s) read past", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("3 system-wide data record(s) read past", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 inter-area transfer(s) read past", StringComparison.Ordinal));
    }

    [Fact]
    public void Sample_v35_solves()
    {
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Sample().Model);
        Assert.True(pf.Converged);
    }

    [Fact]
    public void A_transformer_using_an_impedance_correction_table_has_its_impedance_corrected()
    {
        // 2026-10-25: impedance correction tables are modelled, so this file is no longer refused (the test used to pin
        // that refusal). Winding 1 of T3W (ratio 1.05, COD 0) uses table 1 (revision 35 triples: T -30, 0, 30 with
        // Re F 1.1, 1.0, 1.1): F = 1.0 + (1.05 - 0) / (30 - 0) x (1.1 - 1.0), applied to winding 1's star impedance
        // Z1 = 0.01 + j0.07 (the other windings unchanged).
        string text = File.ReadAllText(Fixture("sample-v35.raw")).Replace(" 1.05000,   0.000,   0.000,   90.00, 0,0,0,0,0,0,0,0,0,0,0,  0,     0,     0, 1.10000, 0.90000, 1.10000, 0.90000,  33,    0,",
            " 1.05000,   0.000,   0.000,   90.00, 0,0,0,0,0,0,0,0,0,0,0,  0,     0,     0, 1.10000, 0.90000, 1.10000, 0.90000,  33,    1,");
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(text, "s");
        double f = 1.0 + (1.05 - 0.0) / (30.0 - 0.0) * (1.1 - 1.0);
        IReadOnlyList<Branch> br = r.Model.Branches;
        Assert.Equal(0.01 * f, br[3].R, 1e-15);
        Assert.Equal(0.07 * f, br[3].X, 1e-15);
        Assert.Equal(0.03, br[4].X, 1e-15);
        Assert.Equal(0.17, br[5].X, 1e-15);
        Assert.Contains(r.Notes, n => n.StartsWith("1 transformer winding(s) use an impedance correction table (TAB)", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n => n.Contains("NOT modelled", StringComparison.Ordinal));
    }

    // ---- Malformed input: refused, naming the line ---------------------------------------------------------------

    private static readonly string Tiny = TinyRaw.Replace("\r\n", "\n", StringComparison.Ordinal);

    private const string TinyRaw = """
        0, 100, 33, 0, 0, 50 / tiny
        TITLE 1
        TITLE 2
        1, 'A', 132, 3
        2, 'B', 132, 1
        0 / END OF BUS DATA, BEGIN LOAD DATA
        2, '1', 1, 1, 1, 10, 5
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 10, 0, 100, -100, 1.0
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        1, 2, '1', 0.01, 0.1, 0.02
        0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
        0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
        Q
        """;

    [Fact]
    public void Tiny_case_reads_and_solves()
    {
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(Tiny, "tiny");
        Assert.Equal(2, r.Model.Buses.Count);
        Assert.Equal((10.0, 5.0), (r.Model.Buses[1].Pd, r.Model.Buses[1].Qd));
        Assert.Equal(-100.0, r.Model.Generators[0].Qmin);
        Assert.Equal(9999.0, r.Model.Generators[0].Pmax);   // PT left off: PSS/E's default
        Assert.True(new NewtonRaphsonPowerFlow().Solve(r.Model).Converged);
    }

    [Theory]
    [InlineData("1, 2, '1', 0.01, 0.1, 0.02", "1, 2, '1', 0.01", "line 12 (BRANCH data): field 5 (X) is missing and has no default")]
    [InlineData("1, 2, '1', 0.01, 0.1, 0.02", "1, 2, '1', 0.01, ten, 0.02", "line 12 (BRANCH data): field 5 (X) is 'ten'; it must be a finite number")]
    [InlineData("1, 2, '1', 0.01, 0.1, 0.02", "1, 2, '1, 0.01, 0.1, 0.02", "line 12 (BRANCH data): a single quote is never closed")]
    [InlineData("1, 2, '1', 0.01, 0.1, 0.02", "1, 3, '1', 0.01, 0.1, 0.02", "line 12 (BRANCH data): bus J 3 is not a bus of this file")]
    [InlineData("1, 2, '1', 0.01, 0.1, 0.02", "1, 1, '1', 0.01, 0.1, 0.02", "line 12 (BRANCH data): both ends are bus 1")]
    [InlineData("1, 2, '1', 0.01, 0.1, 0.02", "1, 2, '1', 0.01, 0.1, 0.02, 0, 0, 0, 0, 0, 0, 0, 2", "line 12 (BRANCH data): field 14 (ST) is 2; it must be one of 0, 1")]
    [InlineData("2, '1', 1, 1, 1, 10, 5", "7, '1', 1, 1, 1, 10, 5", "line 7 (LOAD data): load bus 7 is not a bus of this file")]
    [InlineData("2, 'B', 132, 1", "1, 'B', 132, 1", "line 5 (BUS data): bus 1 is defined twice (also at line 4)")]
    [InlineData("2, 'B', 132, 1", "2, 'B', 132, 5", "line 5 (BUS data): field 4 (IDE) is 5; it must be one of 1, 2, 3, 4")]
    [InlineData("2, 'B', 132, 1", "2.5, 'B', 132, 1", "line 5 (BUS data): field 1 (I) is '2.5'; it must be a whole number")]
    [InlineData("1, '1', 10, 0, 100, -100, 1.0", "1, '1', 10, 0, -100, 100, 1.0", "line 10 (GENERATOR data): QB 100 is above QT -100")]
    [InlineData("0, 100, 33, 0, 0, 50 / tiny", "1, 100, 33, 0, 0, 50 / tiny", "line 1 (CASE IDENTIFICATION data): IC is 1: this is a change case")]
    [InlineData("0, 100, 33, 0, 0, 50 / tiny", "0, 100, 32, 0, 0, 50 / tiny", "PSS/E revision 32 is not supported")]
    [InlineData("0, 100, 33, 0, 0, 50 / tiny", "0, 100 / tiny", "the file does not say its PSS/E revision")]
    [InlineData("0, 100, 33, 0, 0, 50 / tiny", "0, -100, 33, 0, 0, 50 / tiny", "SBASE is -100; the system base must be above 0 MVA")]
    [InlineData("0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA", "0 / END OF GENERATOR DATA, BEGIN BRANCH DATA", "line 8 (LOAD data): this terminator says it ends GENERATOR data")]
    [InlineData("0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA", "0 / END OF LOAD DATA, BEGIN GENERATOR DATA", "line 8 (LOAD data): this terminator says GENERATOR data comes next, but the file's revision puts FIXED SHUNT next")]
    [InlineData("1, 'A', 132, 3", "1, 'A', 132, 1", "has no swing (IDE 3) bus")]
    public void Malformed_input_is_refused_naming_the_line(string find, string replace, string expected)
    {
        string text = Tiny.Replace(find, replace, StringComparison.Ordinal);
        Assert.NotEqual(Tiny, text);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(text, "tiny"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transformer_cut_short_by_a_terminator_is_refused()
    {
        string text = Tiny.Replace("0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA\n",
            "0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA\n1, 2, 0, '1', 1, 1, 1, 0, 0, 2, 'T', 1\n0.01, 0.1, 100\n", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(text, "tiny"));
        Assert.Contains("line 14 (TRANSFORMER data): the section ends at line 16 inside this record (the winding 1 line is missing)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gne_device_is_refused_even_when_unmodelled_devices_are_allowed()
    {
        string text = Tiny.Replace("0 / END OF TRANSFORMER DATA, BEGIN AREA DATA\nQ", string.Join("\n",
            "0 / END OF TRANSFORMER DATA, BEGIN AREA DATA", "0 / END OF AREA DATA", "0 / END OF TWO-TERMINAL DC DATA", "0 / END OF VSC DATA",
            "0 / END OF IMPEDANCE CORRECTION DATA", "0 / END OF MULTI-TERMINAL DC DATA", "0 / END OF MULTI-SECTION LINE DATA", "0 / END OF ZONE DATA",
            "0 / END OF INTER-AREA TRANSFER DATA", "0 / END OF OWNER DATA", "0 / END OF FACTS DEVICE DATA", "0 / END OF SWITCHED SHUNT DATA",
            "'GNE 1', 'MYMODEL', 1, 2, 0, 0, 0", "0 / END OF GNE DEVICE DATA", "Q"), StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(text, "tiny", new PsseReadOptions { AllowUnmodelled = true }));
        Assert.Contains("line 26 (GNE DEVICE data): GNE devices are user-written models", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_after_the_last_section_is_refused()
    {
        GridModel mp = MatpowerCase("case14");
        string text = PsseRawWriter.Write(mp).Text.Replace("0 / END OF INDUCTION MACHINE DATA\nQ\n", "0 / END OF INDUCTION MACHINE DATA\n5, 6\nQ\n", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(text, "c14"));
        Assert.Contains("a record follows the last section; the file should end with Q", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Revision_and_frequency_given_twice_must_agree()
    {
        var rev = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Tiny, "t", new PsseReadOptions { Revision = 34 }));
        Assert.Contains("the file says revision 33 but revision 34 was asked for", rev.Message, StringComparison.Ordinal);
        var f = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Tiny, "t", new PsseReadOptions { FrequencyHz = 60 }));
        Assert.Contains("the file's base frequency is 50 Hz but 60 Hz was asked for", f.Message, StringComparison.Ordinal);
        // No REV and no PSS/E-NN in the comment: the caller's revision is used; no BASFRQ: the caller's frequency.
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(Tiny.Replace("0, 100, 33, 0, 0, 50 / tiny", "0, 100", StringComparison.Ordinal), "t",
            new PsseReadOptions { Revision = 33, FrequencyHz = 50 });
        Assert.Equal((33, 50.0), (r.Revision, r.Model.FrequencyHz));
        // The revision from a PSS/E-NN comment; with no BASFRQ the assumed 60 Hz is noted.
        PsseParseResult c = PsseRawParser.ParseWithDiagnostics(Tiny.Replace("0, 100, 33, 0, 0, 50 / tiny", "0, 100 / PSS/E-33.5 RAW", StringComparison.Ordinal), "t");
        Assert.Equal((33, 60.0), (c.Revision, c.Model.FrequencyHz));
        Assert.Contains(c.Notes, n => n.StartsWith("base frequency not given (BASFRQ 0 or missing); assumed 60 Hz", StringComparison.Ordinal));
    }

    [Fact]
    public void The_revision_is_read_from_a_registered_sign_comment()
    {
        // PSS/E 35 writes its name with the registered sign (PSS(R)E-35); the reader spells the sign as an escape so the
        // source stays plain ASCII (Doc17TextHygieneTests).
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(Tiny.Replace("0, 100, 33, 0, 0, 50 / tiny", "0, 100, 0, 0, 0, 50 / PSS\u00AEE-33.4 RAW", StringComparison.Ordinal), "t");
        Assert.Equal(33, r.Revision);
    }

    // ---- Fields ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1, 'A B', 3", new[] { "1", "A B", "3" }, null)]
    [InlineData("1 'A B'   3", new[] { "1", "A B", "3" }, null)]
    [InlineData("1 , 2 ,3", new[] { "1", "2", "3" }, null)]
    [InlineData("1,,3", new[] { "1", "", "3" }, null)]
    [InlineData(",2", new[] { "", "2" }, null)]
    [InlineData("1, 'x/y, z', 2 / comment, here", new[] { "1", "x/y, z", "2" }, "comment, here")]
    [InlineData("RATING, 1, \"R1 \", \"SET 1\"", new[] { "RATING", "1", "R1 ", "SET 1" }, null)]
    [InlineData("1\t2\t'N'", new[] { "1", "2", "N" }, null)]
    [InlineData("1, ''", new[] { "1", "" }, null)]
    public void Fields_split_as_pss_e_writes_them(string line, string[] fields, string? comment)
    {
        (List<string?> f, string? c) = PsseText.Split(line, 1, "TEST");
        Assert.Equal(fields, f);
        Assert.Equal(comment, c);
    }
}
