using System.Numerics;
using System.Text;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow.Equivalents;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 (B3, WP31 follow-up): a short-circuit equivalent written as a JSON v2 case and read back gives the in-memory
/// equivalent's fault currents. <see cref="V2CaseWriter.WriteWithFaultSequence"/> carries what the elements cannot hold
/// (the equivalent branches' and kept transformers' windings in <c>lineWindings</c>, machine zero-sequence impedances,
/// kept mutual couplings), and <see cref="V2CaseLoader"/> reads them back.
///
/// <para><b>The bound is 0 (bit for bit), derived.</b> Every number is written by <c>Utf8JsonWriter.WriteNumber(double)</c>,
/// which writes the shortest text that parses back to the same double, and read by <c>JsonElement.GetDouble</c>, which
/// parses it back correctly rounded: the loaded value is the written one to the bit. The elements keep the model's order
/// (every branch of the equivalent is written in place as a line), so each fault network is assembled from the same
/// numbers in the same order and solved by the same deterministic code. Text precision therefore limits nothing, and the
/// currents are compared by their bits. The comparison of the data itself (<see cref="Reloaded_case_holds_every_fault_field_matched_by_ends_not_by_position"/>)
/// matches branches by their end buses and generators by their bus, so it does not depend on the order the writer
/// chose.</para>
///
/// <para>Test data: MATPOWER case14 (data/matpower/lib/case14.m) with base kV 132 and the zero-sequence data of
/// Rc5Wp31EquivalentsTests (transformers 4-7 dYN, 4-9 YNyn, 5-6 YNd, 7-9 YNyn; machine zero-sequence impedances at buses
/// 1, 2 and 6), the external mutual j0.1 between lines 6-12 and 6-13 and, so the kept part carries one too, a mutual
/// j0.05 between the kept lines 1-2 and 1-5. Kept set {1, 2, 3, 4, 5, 7, 8}.</para>
/// </summary>
public sealed class Rc5Wp31ScWriterTests
{
    private static readonly int[] Kept = { 1, 2, 3, 4, 5, 7, 8 };

    private static GridModel Case14Sequence(bool keptMachineZ0 = true, bool keptMutual = true,
        WindingConnection from56 = WindingConnection.WyeGrounded, WindingConnection to56 = WindingConnection.Delta)
    {
        GridModel m = IeeeCases.Case14();
        var buses = m.Buses.Select(b => b with { BaseKv = 132 }).ToList();
        var branches = m.Branches.ToList();
        Assert.Equal((1, 2), (branches[0].From, branches[0].To));
        Assert.Equal((1, 5), (branches[1].From, branches[1].To));
        Assert.Equal((4, 7), (branches[7].From, branches[7].To));
        Assert.Equal((4, 9), (branches[8].From, branches[8].To));
        Assert.Equal((5, 6), (branches[9].From, branches[9].To));
        Assert.Equal((6, 12), (branches[11].From, branches[11].To));
        Assert.Equal((6, 13), (branches[12].From, branches[12].To));
        Assert.Equal((7, 9), (branches[14].From, branches[14].To));
        branches[7] = branches[7] with { FromWinding = WindingConnection.Delta, ToWinding = WindingConnection.WyeGrounded };
        branches[8] = branches[8] with { FromWinding = WindingConnection.WyeGrounded, ToWinding = WindingConnection.WyeGrounded };
        branches[9] = branches[9] with { FromWinding = from56, ToWinding = to56 };
        branches[14] = branches[14] with { FromWinding = WindingConnection.WyeGrounded, ToWinding = WindingConnection.WyeGrounded };
        var gens = m.Generators.Select(g => g.Bus switch
        {
            1 when keptMachineZ0 => g with { ZeroSequenceZPu = new MachineZeroSequence(0.0, 0.05) },
            2 when keptMachineZ0 => g with { ZeroSequenceZPu = new MachineZeroSequence(0.0, 0.06) },
            6 => g with { ZeroSequenceZPu = new MachineZeroSequence(0.0, 0.08) },
            _ => g,
        }).ToList();
        var mutuals = new List<ZeroSequenceMutual> { new(11, 6, 12, 12, 6, 13, 0.0, 0.1) };
        if (keptMutual) mutuals.Add(new ZeroSequenceMutual(0, 1, 2, 1, 1, 5, 0.0, 0.05));
        return new GridModel
        {
            BaseMva = m.BaseMva, Name = "case14-seq", FrequencyHz = m.FrequencyHz,
            Buses = buses, Generators = gens, Branches = branches, ZeroSequenceMutuals = mutuals,
        };
    }

    private static GridModel Reduced() => ShortCircuitEquivalent.Reduce(Case14Sequence(), Kept).Network.Model;

    /// <summary>Write <paramref name="red"/> with every sequence field and read it back.</summary>
    private static GridModel WriteAndReload(GridModel red, TrackedTempDir dir)
    {
        V2CaseWriteResult w = V2CaseWriter.WriteWithFaultSequence(red, dir.File("sc.case.json"));
        return V2CaseLoader.Load(w.ManifestPath, SchemasDir);
    }

    private static void AssertSameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            FormattableString.Invariant($"{what}: reloaded {actual:R} vs in-memory {expected:R}"));

    // ---- The four fault types, bit for bit, at every kept bus ----

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(7)] [InlineData(8)]
    public void Reloaded_sc_equivalent_gives_the_same_three_phase_current(int bus)
    {
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        GridModel red = Reduced();
        GridModel back = WriteAndReload(red, dir);
        AssertSameBits(Iec60909.ThreePhase(red, bus).IkppPu, Iec60909.ThreePhase(back, bus).IkppPu, $"3ph Ik'' at bus {bus}");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(7)] [InlineData(8)]
    public void Reloaded_sc_equivalent_gives_the_same_line_to_line_current(int bus)
    {
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        GridModel red = Reduced();
        GridModel back = WriteAndReload(red, dir);
        AssertSameBits(Iec60909.LineToLine(red, bus).IkppPu, Iec60909.LineToLine(back, bus).IkppPu, $"LL Ik'' at bus {bus}");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(7)] [InlineData(8)]
    public void Reloaded_sc_equivalent_gives_the_same_single_line_to_earth_current(int bus)
    {
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        GridModel red = Reduced();
        GridModel back = WriteAndReload(red, dir);
        AssertSameBits(Iec60909.SingleLineToGround(red, bus).IkppPu, Iec60909.SingleLineToGround(back, bus).IkppPu, $"SLG Ik'' at bus {bus}");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(7)] [InlineData(8)]
    public void Reloaded_sc_equivalent_gives_the_same_double_line_to_earth_current(int bus)
    {
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        GridModel red = Reduced();
        GridModel back = WriteAndReload(red, dir);
        AssertSameBits(Iec60909.DoubleLineToGround(red, bus).IkppPu, Iec60909.DoubleLineToGround(back, bus).IkppPu, $"DLG Ik'' at bus {bus}");
    }

    /// <summary>Review R1 finding 7: the in-memory equivalent's double-line-to-earth Ik'' against the full case, at every
    /// kept bus. Tolerance: Rc5Wp31EquivalentsTests.ExactRel (1e-12, an observed-margin bound on the n kappa u estimate;
    /// the DLG current is formed from the same Z1, Z2 and Z0 as the SLG one, so the same estimate applies).</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(7)] [InlineData(8)]
    public void Sc_equivalent_gives_the_full_double_line_to_earth_current_at_every_kept_bus(int bus)
    {
        GridModel full = Case14Sequence();
        GridModel red = ShortCircuitEquivalent.Reduce(full, Kept).Network.Model;
        double f = Iec60909.DoubleLineToGround(full, bus).IkppPu, r = Iec60909.DoubleLineToGround(red, bus).IkppPu;
        Assert.True(f > 0, $"bus {bus}: the full case is earthed, so the DLG earth current is not zero");
        Assert.True(Math.Abs(r - f) <= 1e-12 * f, FormattableString.Invariant($"DLG Ik'' at bus {bus}: {r:R} vs {f:R}"));
    }

    // ---- The data itself, matched independently of the writer's order ----

    [Fact]
    public void Reloaded_case_holds_every_fault_field_matched_by_ends_not_by_position()
    {
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        GridModel red = Reduced();
        GridModel back = WriteAndReload(red, dir);

        // Key: the end buses and whether the branch is an equivalent one ("SC:" name). Kept case14 branches are unnamed
        // (the writer names them branch-<n>), and an equivalent branch can share its ends with a kept one (4-5, 4-7).
        static Dictionary<(int, int, bool), Branch> ByEnds(GridModel m) => m.Branches.Where(b => b.InService)
            .ToDictionary(b => (b.From, b.To, b.Name is not null && b.Name.StartsWith("SC:", StringComparison.Ordinal)));
        Dictionary<(int, int, bool), Branch> want = ByEnds(red), got = ByEnds(back);
        Assert.Equal(want.Keys.OrderBy(k => k), got.Keys.OrderBy(k => k));
        foreach (((int f, int t, bool sc), Branch a) in want)
        {
            Branch b = got[(f, t, sc)];
            string at = $"branch {f}-{t}{(sc ? " (equivalent)" : "")}";
            foreach ((string what, double x, double y) in new[]
            {
                ("R", a.R, b.R), ("X", a.X, b.X), ("B", a.B, b.B), ("tap", a.TapRatio, b.TapRatio), ("shift", a.PhaseShiftDeg, b.PhaseShiftDeg),
                ("R0", a.R0, b.R0), ("X0", a.X0, b.X0), ("from neutral", a.FromNeutralXPu, b.FromNeutralXPu), ("to neutral", a.ToNeutralXPu, b.ToNeutralXPu),
            })
                AssertSameBits(x, y, $"{at} {what}");
            Assert.True((a.FromWinding, a.ToWinding, a.IsTransformer) == (b.FromWinding, b.ToWinding, b.IsTransformer),
                $"{at}: windings {a.FromWinding}/{a.ToWinding} transformer {a.IsTransformer} vs {b.FromWinding}/{b.ToWinding} {b.IsTransformer}");
        }

        Dictionary<int, Generator> gWant = red.AllGenerators.ToDictionary(g => g.Bus), gGot = back.AllGenerators.ToDictionary(g => g.Bus);
        Assert.Equal(gWant.Keys.OrderBy(k => k), gGot.Keys.OrderBy(k => k));
        foreach ((int bus, Generator a) in gWant)
        {
            Generator b = gGot[bus];
            AssertSameBits(a.MBase, b.MBase, $"generator at {bus} MBase");
            AssertSameBits(a.SubtransientXdPu, b.SubtransientXdPu, $"generator at {bus} X''d");
            AssertSameBits(a.NegativeSeqXPu, b.NegativeSeqXPu, $"generator at {bus} X2");
            AssertSameBits(a.UnitTransformerXPu, b.UnitTransformerXPu, $"generator at {bus} XT");
            Assert.Equal(a.ZeroSequenceZPu is null, b.ZeroSequenceZPu is null);
            if (a.ZeroSequenceZPu is MachineZeroSequence za)
            {
                AssertSameBits(za.RPu, b.ZeroSequenceZPu!.Value.RPu, $"generator at {bus} R0");
                AssertSameBits(za.XPu, b.ZeroSequenceZPu!.Value.XPu, $"generator at {bus} X0");
            }
        }

        // The kept mutual (lines 1-2 and 1-5), by the ends of the lines it names.
        static (int, int, int, int, long, long) Key(GridModel m, ZeroSequenceMutual z) =>
            (m.Branches[z.BranchA].From, m.Branches[z.BranchA].To, m.Branches[z.BranchB].From, m.Branches[z.BranchB].To,
             BitConverter.DoubleToInt64Bits(z.RPu), BitConverter.DoubleToInt64Bits(z.XPu));
        Assert.Single(red.ZeroSequenceMutuals);
        Assert.Equal(red.ZeroSequenceMutuals.Select(z => Key(red, z)), back.ZeroSequenceMutuals.Select(z => Key(back, z)));
    }

    // ---- Not vacuous: without the tables the windings are lost and the earth-fault current changes ----

    [Fact]
    public void The_default_writer_refuses_the_equivalent_because_it_cannot_hold_machine_zero_sequence_data()
    {
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        var ex = Assert.Throws<InvalidDataException>(() => V2CaseWriter.Write(Reduced(), dir.File("sc.case.json")));
        Assert.Contains("machine zero-sequence impedances", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_writer_loses_the_windings_and_the_earth_fault_current_changes()
    {
        // Without the kept machine impedances and the kept mutual, the default writer accepts the equivalent, but writes
        // every winding-carrying branch as a plain line (none of them can trail as a transformer element).
        GridModel red = ShortCircuitEquivalent.Reduce(Case14Sequence(keptMachineZ0: false, keptMutual: false), Kept).Network.Model;
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        V2CaseWriteResult w = V2CaseWriter.Write(red, dir.File("plain.case.json"));
        Assert.Contains(w.Notes, n => n.Contains("winding", StringComparison.Ordinal));
        GridModel lost = V2CaseLoader.Load(w.ManifestPath, SchemasDir);
        Assert.NotEqual(Iec60909.SingleLineToGround(red, 7).IkppPu, Iec60909.SingleLineToGround(lost, 7).IkppPu);

        using TrackedTempDir dir2 = TestTemp.NewDir("gridsim-wp31w-");
        GridModel kept = WriteAndReload(red, dir2);
        AssertSameBits(Iec60909.SingleLineToGround(red, 7).IkppPu, Iec60909.SingleLineToGround(kept, 7).IkppPu, "SLG Ik'' at bus 7 with the tables");
    }

    // ---- Byte identity of the default output ----

    [Fact]
    public void A_model_without_the_new_data_is_written_to_the_same_bytes_by_both_writes()
    {
        GridModel m = IeeeCases.Case14();
        GridModel plain = new()
        {
            BaseMva = m.BaseMva, Name = "case14-plain", FrequencyHz = m.FrequencyHz,
            Buses = m.Buses.Select(b => b with { BaseKv = 132 }).ToList(), Generators = m.AllGenerators.ToList(), Branches = m.Branches.ToList(),
        };
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        // Same file name in two folders: the manifest names its network file.
        V2CaseWriteResult a = V2CaseWriter.Write(plain, Path.Combine(dir.Path, "a", "case.case.json"));
        V2CaseWriteResult b = V2CaseWriter.WriteWithFaultSequence(plain, Path.Combine(dir.Path, "b", "case.case.json"));
        byte[] na = File.ReadAllBytes(a.NetworkPath), nb = File.ReadAllBytes(b.NetworkPath);
        Assert.Equal(na, nb);
        Assert.Equal(File.ReadAllBytes(a.ManifestPath), File.ReadAllBytes(b.ManifestPath));
        string text = Encoding.UTF8.GetString(na);
        Assert.DoesNotContain("lineWindings", text, StringComparison.Ordinal);
        Assert.DoesNotContain("machineZeroSequence", text, StringComparison.Ordinal);
        Assert.DoesNotContain("zeroSequenceMutuals", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fault_sequence_write_adds_the_three_tables_after_every_element()
    {
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        V2CaseWriteResult w = V2CaseWriter.WriteWithFaultSequence(Reduced(), dir.File("sc.case.json"));
        Assert.DoesNotContain(w.Notes, n => n.Contains("winding", StringComparison.Ordinal));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(w.NetworkPath));
        int lines = text.IndexOf("\"lines\"", StringComparison.Ordinal);
        int lw = text.IndexOf("\"lineWindings\"", StringComparison.Ordinal);
        int mz = text.IndexOf("\"machineZeroSequence\"", StringComparison.Ordinal);
        int mu = text.IndexOf("\"zeroSequenceMutuals\"", StringComparison.Ordinal);
        Assert.True(lines >= 0 && lw > lines && mz > lw && mu > mz, $"order: lines {lines}, lineWindings {lw}, machineZeroSequence {mz}, zeroSequenceMutuals {mu}");
        Assert.DoesNotContain("\"transformers\"", text, StringComparison.Ordinal);
    }

    // ---- Refusals by name ----

    [Fact]
    public void The_loader_refuses_a_line_windings_entry_naming_no_line()
    {
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        V2CaseWriteResult w = V2CaseWriter.WriteWithFaultSequence(Reduced(), dir.File("sc.case.json"));
        string text = File.ReadAllText(w.NetworkPath);
        const string Named = "\"line\": \"SC:4-5\"";
        Assert.Contains(Named, text, StringComparison.Ordinal);
        File.WriteAllText(w.NetworkPath, text.Replace(Named, "\"line\": \"no-such-line\"", StringComparison.Ordinal));
        var ex = Assert.Throws<InvalidDataException>(() => V2CaseLoader.Load(w.ManifestPath, SchemasDir));
        Assert.Contains("no line named 'no-such-line'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_writer_refuses_a_one_sided_winding_by_name()
    {
        GridModel red = Reduced();
        var branches = red.Branches.Select(b => b.Name == "SC:4-5" ? b with { ToWinding = null } : b).ToList();
        GridModel bad = red.CopyWith(branches: branches);
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-wp31w-");
        var ex = Assert.Throws<InvalidDataException>(() => V2CaseWriter.WriteWithFaultSequence(bad, dir.File("bad.case.json")));
        Assert.Contains("has only the from winding", ex.Message, StringComparison.Ordinal);
    }

    // ---- The Thevenin report, per bus ----

    /// <summary>Review R1 findings 2 and 3. Bus 5's zero-sequence row and column of Y_0,eq are exactly zero off the diagonal
    /// (its only external tie, 5-6, is a YNd shunt at bus 5), so its block of the inverse is 1 / Y_0,eq(5,5), and Y_0,eq(5,5)
    /// is its row sum s0, written as the link's Z0 = 1/s0. Gauss-Jordan never pivots on, swaps or updates that row (its other
    /// entries are exact zeros and are skipped), so the reported Z0 is the same division, to the bit.</summary>
    [Fact]
    public void Sc_thevenin_z0_at_a_decoupled_bus_is_the_inverse_of_its_row_sum()
    {
        ShortCircuitEquivalentResult r = ShortCircuitEquivalent.Reduce(Case14Sequence(), Kept);
        EquivalentElement s0 = r.Network.Elements.Single(e => e.Name == "SC0-SH:5");
        Complex z = Complex.One / s0.Value;
        SequenceTheveninImpedance t5 = r.Thevenin.Single(t => t.BusId == 5);
        AssertSameBits(z.Real, t5.Z0Pu.Real, "bus 5 Thevenin R0");
        AssertSameBits(z.Imaginary, t5.Z0Pu.Imaginary, "bus 5 Thevenin X0");
    }

    /// <summary>Review R1 finding 2: with 5-6 a dYN (delta at the boundary bus 5, earthed at 6), bus 5 has no
    /// zero-sequence path to the external network: its row of Y_0,eq is zero. Its Z0 is infinite, and buses 4 and 7, which
    /// reach the earthed external network through 4-9 YNyn and 7-9 YNyn, keep a finite Z0 (before, all three were reported
    /// infinite).</summary>
    [Fact]
    public void Sc_thevenin_z0_is_infinite_only_at_the_bus_with_no_zero_sequence_path()
    {
        GridModel m = Case14Sequence(from56: WindingConnection.Delta, to56: WindingConnection.WyeGrounded);
        ShortCircuitEquivalentResult r = ShortCircuitEquivalent.Reduce(m, Kept);
        SequenceTheveninImpedance t5 = r.Thevenin.Single(t => t.BusId == 5);
        Assert.True(double.IsPositiveInfinity(t5.Z0Pu.Real) && double.IsPositiveInfinity(t5.Z0Pu.Imaginary), $"bus 5 Z0 {t5.Z0Pu}");
        foreach (int bus in new[] { 4, 7 })
        {
            SequenceTheveninImpedance t = r.Thevenin.Single(x => x.BusId == bus);
            Assert.True(double.IsFinite(t.Z0Pu.Real) && double.IsFinite(t.Z0Pu.Imaginary) && t.Z0Pu.Imaginary > 0, $"bus {bus} Z0 {t.Z0Pu}");
        }
    }

    [Fact]
    public void Per_bus_inverse_diagonal_is_the_whole_inverse_when_no_row_is_zero()
    {
        var y = new Complex[,]
        {
            { new(1.0, -10.0), new(-0.5, 5.0) },
            { new(-0.5, 5.0), new(0.7, -8.0) },
        };
        Complex[] whole = ShortCircuitEquivalent.InverseDiagonal(y), per = ShortCircuitEquivalent.PerBusInverseDiagonal(y);
        for (int i = 0; i < 2; i++)
        {
            AssertSameBits(whole[i].Real, per[i].Real, $"entry {i} real");
            AssertSameBits(whole[i].Imaginary, per[i].Imaginary, $"entry {i} imaginary");
        }
    }
}
