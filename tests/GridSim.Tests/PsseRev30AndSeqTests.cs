using System.Numerics;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// Tranche A (October 2026): PSS/E RAW revision 30 (the layout of the UKGDS distribution cases and of PSS/E 30's own
/// sample) and the SEQ reader (revision 30 sequence layout) that gives the fault studies their sequence data.
/// <para>ukgds-style-v30.raw / .seq are hand-written the way the UKGDS files are laid out (tabs, @! headings, padding,
/// '/' fillers, no REV field, Q at the end, a cp1252 curly quote in a heading); ukgds-style-v33.raw is the same network in
/// the revision 33 layout. Every expected value below is worked out by hand from those files. The UKGDS cases themselves
/// carry no licence, so they are not committed: the last test reads them from GRIDSIM_UKGDS_DIR when it is set.</para>
/// </summary>
public class PsseRev30AndSeqTests
{
    private readonly ITestOutputHelper _out;

    public PsseRev30AndSeqTests(ITestOutputHelper output) => _out = output;

    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    private static readonly PsseReadOptions V30At50 = new() { Revision = 30, FrequencyHz = 50 };

    private static PsseParseResult Ukgds30() => PsseRawParser.ParseFile(Fixture("ukgds-style-v30.raw"), V30At50);

    private static string Seq() => File.ReadAllText(Fixture("ukgds-style-v30.seq"));

    private static void AssertSameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    // ---- RAW revision 30 -------------------------------------------------------------------------------------------

    [Fact]
    public void Revision_30_file_in_the_ukgds_layout_reads_to_the_same_network_as_its_revision_33_twin_bit_for_bit()
    {
        PsseParseResult v30 = Ukgds30();
        PsseParseResult v33 = PsseRawParser.ParseFile(Fixture("ukgds-style-v33.raw"), new PsseReadOptions { FrequencyHz = 50 });
        Assert.Equal((30, 33), (v30.Revision, v33.Revision));
        Assert.Equal(new[] { "GridSim UKGDS-style test network", "Revision 30 layout as the UKGDS files write it, written for GridSim" }, v30.Titles);
        GridModel a = v33.Model, b = v30.Model;
        Assert.Equal((a.BaseMva, a.FrequencyHz, a.Buses.Count, a.AllGenerators.Count, a.Branches.Count),
            (b.BaseMva, b.FrequencyHz, b.Buses.Count, b.AllGenerators.Count, b.Branches.Count));
        for (int i = 0; i < a.Buses.Count; i++)
        {
            Bus e = a.Buses[i], g = b.Buses[i];
            Assert.Equal((e.Id, e.Type, e.Name), (g.Id, g.Type, g.Name));
            foreach ((double x, double y, string f) in new[] { (e.Pd, g.Pd, "Pd"), (e.Qd, g.Qd, "Qd"), (e.Gs, g.Gs, "Gs"), (e.Bs, g.Bs, "Bs"),
                         (e.BaseKv, g.BaseKv, "BaseKv"), (e.Vm, g.Vm, "Vm"), (e.VaDeg, g.VaDeg, "Va") })
                AssertSameBits(x, y, $"bus {e.Id} {f}");
        }
        Assert.Equal(a.AllGenerators, b.AllGenerators);
        Assert.Equal(a.Branches, b.Branches);
        Assert.Equal(v33.BusMeta.OrderBy(kv => kv.Key), v30.BusMeta.OrderBy(kv => kv.Key));
        Assert.Equal(v33.BranchKeys, v30.BranchKeys);

        Complex[,] ya = YBus.Build(a).Y, yb = YBus.Build(b).Y;
        for (int i = 0; i < ya.GetLength(0); i++)
            for (int j = 0; j < ya.GetLength(1); j++)
            {
                AssertSameBits(ya[i, j].Real, yb[i, j].Real, $"G[{i},{j}]");
                AssertSameBits(ya[i, j].Imaginary, yb[i, j].Imaginary, $"B[{i},{j}]");
            }
        PowerFlowResult ra = new NewtonRaphsonPowerFlow().Solve(a), rb = new NewtonRaphsonPowerFlow().Solve(b);
        Assert.True(ra.Converged && rb.Converged, "both solves must converge");
        for (int i = 0; i < ra.Vm.Length; i++)
        {
            AssertSameBits(ra.Vm[i], rb.Vm[i], $"Vm bus {ra.BusIds[i]}");
            AssertSameBits(ra.VaDeg[i], rb.VaDeg[i], $"Va bus {ra.BusIds[i]}");
        }
    }

    [Fact]
    public void Revision_30_bus_shunt_metadata_transformers_and_the_skipped_line_are_as_written()
    {
        PsseParseResult r = Ukgds30();
        GridModel m = r.Model;
        Bus pri = m.Buses.Single(b => b.Id == 3);
        Assert.Equal((0.0, 2.5, 4.2, 1.4, 11.0), (pri.Gs, pri.Bs, pri.Pd, pri.Qd, pri.BaseKv));   // GL, BL on the bus record
        Assert.Equal(new PsseBusMeta(1, 2, 1), r.BusMeta[3]);                                     // AREA 7th, ZONE 8th, owner left off (1)
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, m.Buses.Select(b => b.Id));
        // Lines 3-4 and 4-5; the third (ST 0) is skipped and listed. Then the three transformers.
        Assert.Equal(new[] { (3, 4), (4, 5), (1, 2), (2, 3), (2, 4) }, m.Branches.Select(b => (b.From, b.To)));
        Assert.Equal(new PsseBranchKey("branch", 3, 5, 0, "1", 0), Assert.Single(r.SkippedBranchKeys));
        Branch t23 = m.Branches[3];
        Assert.Equal((0.05, 0.9, 1.0, -30.0, 24.0, true), (t23.R, t23.X, t23.TapRatio, t23.PhaseShiftDeg, t23.RateAMva, t23.IsTransformer));
        Assert.Equal(1.025, m.Branches[4].TapRatio);
        Assert.Contains(r.Notes, n => n.StartsWith("1 bus record(s) carry a shunt (GL, BL; revision 30)", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("2 transformer(s) have automatic tap or phase control (COD)", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("skipped 1 out-of-service branch(es).", StringComparison.Ordinal));
    }

    [Fact]
    public void A_revision_30_file_that_does_not_say_its_revision_needs_it_given()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseFile(Fixture("ukgds-style-v30.raw")));
        Assert.Contains("PSS/E RAW line 2 (CASE IDENTIFICATION data): the file does not say its PSS/E revision", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PSSE_30_sample_case_reads_its_bus_shunts_switched_shunts_and_metadata_from_the_revision_30_fields()
    {
        // PSS/E's own revision 30 sample (via MATPOWER's tests, BSD): DC links, FACTS and impedance tables are in service,
        // so it is read with unmodelled devices allowed. Hand-worked values from t_psse_case3.raw.
        PsseParseResult r = PsseRawParser.ParseFile(Fixture("t_psse_case3.raw"), new PsseReadOptions { AllowUnmodelled = true });
        Assert.Equal(30, r.Revision);
        Assert.Equal(new[] { "PSS(TM)E SAMPLE CASE", "ALL DATA CATEGORIES WITH SEQUENCE DATA" }, r.Titles);
        Assert.Equal(42, r.Model.Buses.Count - r.StarPointBuses.Count);
        Bus b151 = r.Model.Buses.Single(b => b.Id == 151), b154 = r.Model.Buses.Single(b => b.Id == 154), b201 = r.Model.Buses.Single(b => b.Id == 201);
        // Bus 151: its own GL 11.12, BL -600, then the I-end shunts of branches 151-152 '1' (0.01, -0.25) and '2'
        // (0.013, -0.251), in pu on the 100 MVA base.
        AssertSameBits(11.12 + 0.01 * 100 + 0.013 * 100, b151.Gs, "bus 151 Gs");
        AssertSameBits(-600.0 + -0.25 * 100 + -0.251 * 100, b151.Bs, "bus 151 Bs");
        Assert.Equal((1.00219, -13.5685, "NUCPLNT"), (b151.Vm, b151.VaDeg, b151.Name));
        // Bus 154: GL 2.45, BL 200 plus the switched shunt's BINIT 124 (field 8 in revision 30, always in service).
        Assert.Equal((2.45, 324.0), (b154.Gs, b154.Bs));
        // Bus 201: BL -500 on the record, then the J-end BJ -1.0 of branch 151-201, then transformer T6's magnetising
        // susceptance MAG2 -0.02084 (CM 1, pu on the system base) at its winding 1 bus.
        AssertSameBits(-500.0 + -1.0 * 100 + -0.02084 * 100, b201.Bs, "bus 201 Bs");
        AssertSameBits(3.67 + 0.00262 * 100, b201.Gs, "bus 201 Gs");
        Assert.Equal(new PsseBusMeta(2, 7, 2), r.BusMeta[201]);
        Assert.Equal(new PsseBusMeta(5, 6, 5), r.BusMeta[93002]);
        Assert.Equal("EAST", r.AreaNames[2]);
        Assert.Equal("ALL_A4_A6", r.ZoneNames[9]);
        Assert.Contains(r.Notes, n => n.StartsWith("6 switched shunt(s) are fixed at their initial susceptance BINIT", StringComparison.Ordinal));
        // 2026-10-25: two-terminal links are modelled, but this sample's are capacitor-commutated, which is not.
        Assert.Contains(r.Notes, n => n.StartsWith("2 in-service capacitor-commutated two-terminal DC link(s) read past and NOT modelled", StringComparison.Ordinal));
        // 2026-10-25: its FACTS devices (a STATCOM and a UPFC, modes 1) are modelled now (device item E), so they are read
        // into the model instead of being read past (PsseFactsTests checks their fields).
        Assert.Contains(r.Notes, n => n.StartsWith("2 FACTS device(s) are solved with the AC power flow", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n => n.Contains("FACTS device(s) read past", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n == "the file has no Q end-of-data record; read to the end of the text.");
    }

    [Fact]
    public void PSSE_30_sample_case_is_refused_without_allowing_its_unmodelled_devices_naming_each_kind()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseFile(Fixture("t_psse_case3.raw")));
        // 2026-10-25: its two VSC links and its multi-terminal link are modelled now (device items C and D), so they left
        // this list.
        // 2026-10-25: its two FACTS devices are modelled now (device item E), so "2 FACTS device(s) (first at line 262)"
        // left this list too.
        foreach (string kind in new[] { "2 capacitor-commutated two-terminal DC link(s) (first at line 199)" })
            Assert.Contains(kind, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("FACTS", ex.Message, StringComparison.Ordinal);
        // 2026-10-25: its three transformers using impedance correction tables are modelled now, so they are no longer
        // among the refused kinds (this list used to include "3 transformer using an impedance correction table(s)").
        Assert.DoesNotContain("impedance correction", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("VSC", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("multi-terminal", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    public void Revisions_31_and_32_are_refused_with_the_reason(int revision)
    {
        string text = File.ReadAllText(Fixture("ukgds-style-v33.raw")).Replace("0, 100, 33, 0, 0, 0", $"0, 100, {revision}, 0, 0, 0", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(text, "t"));
        Assert.Contains($"line 1 (CASE IDENTIFICATION data): PSS/E revision {revision} is not supported; supported: 30, 33, 34, 35 (revisions 31 and 32 lie between the 30 and 33 layouts and are not read", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => PsseRawParser.ParseWithDiagnostics(text, "t", new PsseReadOptions { Revision = revision }));
    }

    [Fact]
    public void The_writer_still_writes_only_revisions_33_34_and_35()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PsseRawWriter.Write(Ukgds30().Model, 30));
        Assert.Contains("PSS/E revision 30 is not supported; supported: 33, 34, 35.", ex.Message, StringComparison.Ordinal);
    }

    // ---- SEQ -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Seq_data_sets_machine_reactances_line_and_transformer_zero_sequence_and_winding_connections()
    {
        PsseParseResult raw = Ukgds30();
        PsseSeqResult s = PsseSeqReader.Apply(Seq(), raw, new PsseSeqOptions { AllowUnmodelled = true });
        GridModel m = s.Model;
        Assert.Equal((2, 2, 2, 3), (s.PositiveSequenceMachines, s.NegativeSequenceMachines, s.ZeroSequenceLines, s.ZeroSequenceTransformers));
        Assert.Equal((0.01, 0.01), (m.AllGenerators[0].SubtransientXdPu, m.AllGenerators[0].NegativeSeqXPu));
        Assert.Equal((0.18, 0.2), (m.AllGenerators[1].SubtransientXdPu, m.AllGenerators[1].NegativeSeqXPu));
        // Lines: 3-4 as written; 4-5 written 5-4 (the record may run either way).
        Assert.Equal((0.15, 0.24, (WindingConnection?)null), (m.Branches[0].R0, m.Branches[0].X0, m.Branches[0].FromWinding));
        Assert.Equal((0.12, 0.18), (m.Branches[1].R0, m.Branches[1].X0));
        // 1-2 CC 2: grounded wye at 132 kV (winding 1 = bus 1, the from end), delta at 33 kV.
        Branch t12 = m.Branches[2];
        Assert.Equal((0.0018, 0.075, WindingConnection.WyeGrounded, WindingConnection.Delta, 0.0, 0.0),
            (t12.R0, t12.X0, t12.FromWinding, t12.ToWinding, t12.FromNeutralXPu, t12.ToNeutralXPu));
        // 2-3 written 3-2 with CC 2: winding 1 is bus 3 (grounded through RG 5.25), so in the model's 2 -> 3 order the
        // from end is delta and the to end grounded; Z0 = (R1 + 3 RG) + j X1.
        Branch t23 = m.Branches[3];
        Assert.Equal((WindingConnection.Delta, WindingConnection.WyeGrounded), (t23.FromWinding, t23.ToWinding));
        AssertSameBits(0.045 + 3.0 * 5.25, t23.R0, "2-3 R0");
        AssertSameBits(0.88, t23.X0, "2-3 X0");
        // 2-4 CC 3: delta at bus 2, grounded at bus 4 through XG 1.5; Z0 = R1 + j(X1 + 3 XG).
        Branch t24 = m.Branches[4];
        Assert.Equal((WindingConnection.Delta, WindingConnection.WyeGrounded), (t24.FromWinding, t24.ToWinding));
        AssertSameBits(0.055, t24.R0, "2-4 R0");
        AssertSameBits(0.95 + 3.0 * 1.5, t24.X0, "2-4 X0");
        // Everything else is the RAW model untouched.
        GridModel rm = raw.Model;
        Assert.Equal(rm.Buses, m.Buses);
        for (int k = 0; k < rm.Branches.Count; k++)
            Assert.Equal(rm.Branches[k], m.Branches[k] with { R0 = 0, X0 = 0, FromWinding = null, ToWinding = null });
        // 2026-10-25: a machine's zero-sequence impedance is modelled now (device item H), so the comparison also clears it.
        for (int k = 0; k < rm.AllGenerators.Count; k++)
            Assert.Equal(rm.AllGenerators[k], m.AllGenerators[k] with { SubtransientXdPu = 0, NegativeSeqXPu = 0, ZeroSequenceZPu = null });
        Assert.Equal(new MachineZeroSequence(0, 0.01), m.AllGenerators[0].ZeroSequenceZPu);
        Assert.Null(m.AllGenerators[1].ZeroSequenceZPu);   // its record is all zero
        Assert.Contains(s.Notes, n => n.StartsWith("1 positive- and 0 negative-sequence machine resistance(s) are not 0", StringComparison.Ordinal));
        Assert.Contains(s.Notes, n => n.StartsWith("1 line(s) carry zero-sequence charging or line-end shunts", StringComparison.Ordinal));
        Assert.Contains(s.Notes, n => n.StartsWith("2 transformer(s) have a neutral grounding impedance (RG, XG); it is folded", StringComparison.Ordinal));
        // 2026-10-25: the grid infeed's zero-sequence impedance (line 17) is applied now instead of read past (device item H).
        Assert.Contains(s.Notes, n => n.StartsWith("1 machine(s) have a zero-sequence impedance (RZERO, XZERO)", StringComparison.Ordinal));
        Assert.DoesNotContain(s.Notes, n => n.Contains("NOT modelled", StringComparison.Ordinal));
        Assert.Contains(s.Notes, n => n.StartsWith("1 machine zero-sequence record(s) are all zero", StringComparison.Ordinal));
        Assert.Contains(s.Notes, n => n.StartsWith("skipped 1 record(s) for elements the RAW file holds outside the model", StringComparison.Ordinal));
    }

    [Fact]
    public void Seq_zero_sequence_network_gives_the_hand_worked_driving_point_impedance()
    {
        GridModel m = PsseSeqReader.Apply(Seq(), Ukgds30(), new PsseSeqOptions { AllowUnmodelled = true }).Model;
        // Bus 3's zero-sequence network: its own path to earth through the 33/11 kV unit (Y3 = 1/(15.795 + j0.88)),
        // in parallel with line 3-4 (0.15 + j0.24) to bus 4's path to earth (Y4 = 1/(0.055 + j5.45)); bus 5 hangs off
        // bus 4 on line 4-5 with no earth, so it carries nothing. The 132/33 kV unit earths bus 1, cut off by the
        // deltas. Hand-worked: Z = 1 / (Y3 + 1 / (Z34 + 1 / Y4)).
        Complex y3 = Complex.One / new Complex(0.045 + 3.0 * 5.25, 0.88), y4 = Complex.One / new Complex(0.055, 0.95 + 3.0 * 1.5);
        Complex expected = Complex.One / (y3 + Complex.One / (new Complex(0.15, 0.24) + Complex.One / y4));
        (Complex z0, double kv, bool grounded) = Iec60909.ZeroSequenceThevenin(m, 3);
        Assert.True(grounded);
        Assert.Equal(11.0, kv);
        Assert.True(Complex.Abs(z0 - expected) <= 1e-12 * Complex.Abs(expected), $"Z0 {z0} vs hand-worked {expected}");
        // Bus 1 (132 kV) earths through the 132/33 kV unit's grounded winding (0.0018 + j0.075) and, since 2026-10-25
        // (device item H: machine zero-sequence impedances are modelled), through the grid infeed's own zero-sequence
        // impedance j0.01 on its MBASE, moved to the 100 MVA system base; in parallel (the solve inverts the admittance
        // back, so the agreement is to rounding, not to the bit). Before item H the infeed's impedance was read past (it
        // was refused unless allowed), so this check used the transformer path alone.
        (Complex z1, _, bool g1) = Iec60909.ZeroSequenceThevenin(m, 1);
        Assert.True(g1);
        Complex t12 = new(0.0018, 0.075), zg = new Complex(0, 0.01) * (100.0 / m.AllGenerators[0].MBase);
        Complex bus1 = Complex.One / (Complex.One / t12 + Complex.One / zg);
        Assert.True(Complex.Abs(z1 - bus1) <= 1e-12 * Complex.Abs(bus1), $"bus 1 Z0 {z1} vs {bus1}");
        // Bus 2 (33 kV) sees only deltas: no zero-sequence path, no earth-fault current.
        Assert.False(Iec60909.ZeroSequenceThevenin(m, 2).Grounded);
    }

    [Fact]
    public void Seq_unmodelled_data_is_refused_unless_allowed()
    {
        // 2026-10-25: the fixture's machine zero-sequence impedance is modelled now (device item H), so the fixture reads
        // without the allowance; the refusal is checked with a zero-sequence shunt load added (still unmodelled).
        Assert.DoesNotContain(PsseSeqReader.Apply(Seq(), Ukgds30()).Notes, n => n.Contains("NOT modelled", StringComparison.Ordinal));
        string load = Seq().Replace("@!I\tGZERO\tBZERO\t\t\t\t\t\t\t\r\n", "@!I\tGZERO\tBZERO\t\t\t\t\t\t\t\r\n3\t0.1\t0.2\r\n", StringComparison.Ordinal);
        Assert.NotEqual(Seq(), load);
        var ex = Assert.Throws<InvalidDataException>(() => PsseSeqReader.Apply(load, Ukgds30()));
        Assert.Contains("1 zero-sequence shunt load(s) (first at line 25)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--allow-unmodelled", ex.Message, StringComparison.Ordinal);
    }

    private static string Edit(string from, string to)
    {
        string s = Seq();
        Assert.Contains(from, s, StringComparison.Ordinal);
        return s.Replace(from, to, StringComparison.Ordinal);
    }

    private static void Refused(string text, string expected, PsseParseResult? raw = null)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseSeqReader.Apply(text, raw ?? Ukgds30(), new PsseSeqOptions { AllowUnmodelled = true }));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seq_malformed_or_mismatched_records_are_refused_naming_line_and_field()
    {
        Refused(Edit("2\t4\t0\t1\t3\t0\t1.5", "2\t4\t0\t1\t5\t0\t1.5"),
            "PSS/E SEQ line 39 (ZERO SEQUENCE TRANSFORMER data): connection code CC 5 is not modelled");
        Refused(Edit("2\t4\t0\t1\t3\t0\t1.5", "2\t4\t1\t1\t3\t0\t1.5"),
            "line 39 (ZERO SEQUENCE TRANSFORMER data): a three-winding transformer's zero-sequence record");
        Refused(Edit("1\t2\t0\t1\t2\t0\t0\t", "1\t2\t0\t1\t1\t0.5\t0\t"),
            "line 37 (ZERO SEQUENCE TRANSFORMER data): CC 1 (grounded wye on both windings) with a grounding impedance");
        Refused(Edit("3\t4\t1\t0.15", "3\t4\t2\t0.15"),
            "line 28 (ZERO SEQUENCE NON-TRANSFORMER BRANCH data): the RAW case has no non-transformer branch 3-4 circuit '2'");
        Refused(Edit("5\t4\t1\t0.12", "3\t4\t1\t0.12"),
            "line 29 (ZERO SEQUENCE NON-TRANSFORMER BRANCH data): branch 3-4 circuit '1' has a second zero-sequence record");
        Refused(Edit("5\t1\t0.01\t0.18", "5\t2\t0.01\t0.18"),
            "line 8 (POSITIVE SEQUENCE GENERATOR data): the RAW case has no machine '2' at bus 5");
        Refused(Edit("5\t1\t0.01\t0.18", "5\t1\t0.01\t0"),
            "line 8 (POSITIVE SEQUENCE GENERATOR data): ZXPOS is 0; a machine's subtransient reactance must be above 0");
        Refused(Edit("5\t1\t0\t0.2", "5\t1\t0\tx"),
            "line 13 (NEGATIVE SEQUENCE GENERATOR data): field 4 (ZXNEG) is 'x'; it must be a finite number");
        Refused(Edit("@!IC\t\t\t\r\n0\t", "@!IC\t\t\t\r\n1\t"), "line 4 (CHANGE CODE data): IC is 1: this is a change case");
        Refused(Edit("@!IC\t\t\t\r\n0\t", "@!IC\t\t\t\r\n0\t33"), "line 4 (CHANGE CODE data): the file says revision 33");
        Refused(Seq() + "7\t7\r\n", "a record follows the last section (zero-sequence switched shunts)");
        // A revision 33 case with a SEQ file that does not state its revision: the 30 layout cannot be assumed.
        PsseParseResult v33 = PsseRawParser.ParseFile(Fixture("ukgds-style-v33.raw"), new PsseReadOptions { FrequencyHz = 50 });
        Refused(Seq(), "the file does not say its revision and the RAW case is revision 33", v33);
        // A mutual coupling over part of a line is unmodelled: refused without the allowance, naming it. (2026-10-25: a
        // coupling over the whole of both lines is modelled now, device item H, so this check uses BIJ2 0.5.)
        string mutual = Edit("@!I\tJ\tICKT1\tK\tL\tICKT2\tRM\tXM\tBIJ1\tBIJ2\tBKL1\tBKL2\t\r\n", "@!I\tJ\tICKT1\tK\tL\tICKT2\tRM\tXM\tBIJ1\tBIJ2\tBKL1\tBKL2\t\r\n3\t4\t1\t4\t5\t1\t0.01\t0.05\t0\t0.5\t0\t1\r\n");
        var ex = Assert.Throws<InvalidDataException>(() => PsseSeqReader.Apply(mutual, Ukgds30()));
        Assert.Contains("1 zero-sequence mutual coupling over part of a line(s) (first at line 34)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seq_file_that_stops_early_is_read_with_a_note()
    {
        string s = Seq();
        string cut = s[..s.IndexOf("@!Zero Sequence Generator Impedance Data", StringComparison.Ordinal)] + "Q\r\n";
        PsseSeqResult r = PsseSeqReader.Apply(cut, Ukgds30());
        Assert.Equal((2, 2, 0, 0), (r.PositiveSequenceMachines, r.NegativeSequenceMachines, r.ZeroSequenceLines, r.ZeroSequenceTransformers));
        Assert.Contains(r.Notes, n => n == "the file stops after 2 of the 9 sections; the rest are taken as empty.");
        Assert.Contains(r.Notes, n => n == "2 line(s) have no zero-sequence record; GridSim uses 3 x Z1 for them.");
    }

    // ---- The UKGDS cases themselves (opt-in: no licence, so not committed) ------------------------------------------

    [EnvironmentFact("reads the UKGDS RAW/SEQ cases (github.com/sedg/ukgds raw folder), which carry no licence and are not committed",
        "GRIDSIM_UKGDS_DIR")]
    public void Every_ukgds_case_loads_with_its_sequence_data_and_the_ones_that_need_no_tap_action_solve()
    {
        // Measured 2026-10-24 on the github.com/sedg/ukgds raw folder (12 cases): all 12 load with their sequence data.
        // The 7 HV cases and EHV5 solve as written. EHV2, 3, 4 and 6 do not: at the file's taps (every WINDV 1) they have
        // no power-flow solution at full load (checked independently: EHV2 with its phase shifts set to 0 solves at 80 %
        // load with a lowest voltage of 0.68 pu, and at full load only with its 33/11 and 132/33 kV taps moved to 0.9).
        // PSS/E solves them by moving the taps under their automatic control (COD 1, RMA 1.05, RMI 0.85), which the reader
        // does not apply (it says so in a note). This pins both halves, so a change in either is seen.
        string[] needTapAction = { "ehv2.raw", "ehv3.raw", "ehv4.raw", "ehv6.raw" };
        string dir = Environment.GetEnvironmentVariable("GRIDSIM_UKGDS_DIR")!;
        string[] raws = Directory.GetFiles(dir, "*.raw").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.True(raws.Length > 0, $"no .raw files in {dir}");
        int solved = 0;
        foreach (string path in raws)
        {
            PsseParseResult raw = PsseRawParser.ParseFile(path, V30At50);
            PsseSeqResult seq = PsseSeqReader.ReadFile(Path.ChangeExtension(path, ".seq"), raw, new PsseSeqOptions { AllowUnmodelled = true });
            PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(seq.Model);
            _out.WriteLine($"{Path.GetFileName(path)}: {seq.Model.Buses.Count} buses, {seq.Model.Branches.Count} branches; X''d {seq.PositiveSequenceMachines}, " +
                $"line Z0 {seq.ZeroSequenceLines}, transformer Z0 {seq.ZeroSequenceTransformers}; solved {pf.Converged} in {pf.Iterations} iterations");
            Assert.True(pf.Converged != needTapAction.Contains(Path.GetFileName(path)),
                $"{Path.GetFileName(path)}: converged {pf.Converged}, expected {!needTapAction.Contains(Path.GetFileName(path))}");
            if (pf.Converged) solved++;
        }
        Assert.Equal(12, raws.Length);
        Assert.Equal(8, solved);
    }
}
