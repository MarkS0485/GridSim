using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Tranche A (October 2026): PSS/E switched shunts with discrete local voltage control (MODSW 1), applied only on request
/// (<see cref="PsseReadOptions.ApplyShuntControl"/>, gridsim <c>--shunt-control</c>) as <see cref="SwitchedShunt"/> banks
/// that <see cref="SwitchedShuntPowerFlow"/> switches in whole blocks. The model keeps BINIT in the bus shunt and a bank's
/// steps count blocks from BINIT, so without the option (or without the bank) the case is the fixed-at-BINIT case.
/// </summary>
public class PsseShuntControlTests
{
    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    private static readonly PsseReadOptions WithShunts = new() { FrequencyHz = 50, ApplyShuntControl = true };
    private static readonly PsseReadOptions Plain = new() { FrequencyHz = 50 };

    /// <summary>A revision 33 two-bus case: slack bus 1, a fixed-ratio transformer (X 0.1 pu, COD 0) to bus 2, a load of
    /// 100 MW + 50 MVAr at bus 2, and one switched shunt at bus 2 written as <paramref name="shunt"/> (the fields after I).
    /// The switched shunt record is line 28.</summary>
    internal static string TwoBus(string shunt) => ($"""
        0, 100, 33, 0, 0, 50     / PSS/E-33 RAW, two-bus switched shunt check
        GridSim switched shunt check
        Two buses, one switched shunt at bus 2
        1, 'SRC', 132, 3, 1, 1, 1, 1, 0
        2, 'LOAD', 33, 1, 1, 1, 1, 1, 0
        0 / END OF BUS DATA, BEGIN LOAD DATA
        2, '1', 1, 1, 1, 100, 50, 0, 0, 0, 0, 1, 1, 0
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 999, -999, 1, 0, 100, 0, 0.01, 0, 0, 1, 1, 100, 999, -999, 1, 1
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
        1, 2, 0, '1', 1, 1, 1, 0, 0, 2, 'T1', 1, 1, 1
        0, 0.1, 100
        1, 0, 0, 200, 200, 200, 0, 0, 1.1, 0.9, 1.1, 0.9, 33, 0, 0, 0, 0
        1, 0
        0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
        0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
        0 / END OF TWO-TERMINAL DC DATA, BEGIN VSC DC DATA
        0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA
        0 / END OF IMPEDANCE CORRECTION DATA, BEGIN MULTI-TERMINAL DC DATA
        0 / END OF MULTI-TERMINAL DC DATA, BEGIN MULTI-SECTION LINE DATA
        0 / END OF MULTI-SECTION LINE DATA, BEGIN ZONE DATA
        0 / END OF ZONE DATA, BEGIN INTER-AREA TRANSFER DATA
        0 / END OF INTER-AREA TRANSFER DATA, BEGIN OWNER DATA
        0 / END OF OWNER DATA, BEGIN FACTS DEVICE DATA
        0 / END OF FACTS DEVICE DATA, BEGIN SWITCHED SHUNT DATA
        2, {shunt}
        0 / END OF SWITCHED SHUNT DATA, BEGIN GNE DEVICE DATA
        0 / END OF GNE DEVICE DATA, BEGIN INDUCTION MACHINE DATA
        0 / END OF INDUCTION MACHINE DATA
        Q
        """).Replace("\r\n", "\n");

    // MODSW, ADJM, STAT, VSWHI, VSWLO, SWREM, RMPCT, RMIDNT, BINIT, N1, B1, ...
    private const string Eight = "1, 0, 1, 1.03, 0.97, 0, 100, '', 0, 8, 10";

    private static PsseParseResult Read(string shunt, PsseReadOptions o) => PsseRawParser.ParseWithDiagnostics(TwoBus(shunt), "shunt", o);

    private static void AssertSameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    [Fact]
    public void Without_shunt_control_every_switched_shunt_is_fixed_at_binit_as_before()
    {
        PsseParseResult plain = Read("1, 0, 1, 1.03, 0.97, 0, 100, '', 20, 8, 10", Plain);
        PsseParseResult with = Read("1, 0, 1, 1.03, 0.97, 0, 100, '', 20, 8, 10", WithShunts);
        Assert.Empty(plain.SwitchedShunts);
        Assert.Contains(plain.Notes, n => n.StartsWith("1 switched shunt(s) are fixed at their initial susceptance BINIT; GridSim does not switch shunt blocks", StringComparison.Ordinal));
        // The model is the same either way: BINIT (20 MVAr) is in the bus shunt.
        Assert.Equal(20.0, plain.Model.Buses[1].Bs);
        Assert.Equal(plain.Model.Buses, with.Model.Buses);
        Assert.Equal(plain.Model.Branches, with.Model.Branches);
    }

    [Fact]
    public void A_modsw_1_shunt_with_one_block_size_becomes_a_bank_counted_from_binit()
    {
        PsseParseResult r = Read(Eight, WithShunts);
        Assert.Equal(new SwitchedShunt(2, 10, 0, 8, 0, 0.97, 1.03, "switched shunt 2"), Assert.Single(r.SwitchedShunts));
        Assert.Contains(r.Notes, n => n == "1 switched shunt(s) with discrete voltage control (MODSW 1) switch whole blocks from BINIT to hold their bus in VSWLO..VSWHI.");
        // Reactor and capacitor blocks of one size, BINIT 2 blocks in: steps from -(4 + 2) to 8 - 2.
        PsseParseResult mixed = Read("1, 0, 1, 1.03, 0.97, 2, 100, '', 20, 4, -10, 8, 10", WithShunts);
        Assert.Equal(new SwitchedShunt(2, 10, -6, 6, 0, 0.97, 1.03, "switched shunt 2"), Assert.Single(mixed.SwitchedShunts));
        Assert.Equal(20.0, mixed.Model.Buses[1].Bs);
    }

    [Theory]
    // 2026-10-25: MODSW 2 (continuous voltage control) is now modelled as a static var compensator (PsseFactsTests), so this
    // row takes MODSW 3, the first mode still not switched, and the reason names MODSW 3 to 6.
    [InlineData("3, 0, 1, 1.03, 0.97, 0, 100, '', 0, 8, 10", "have a control mode other than voltage control (MODSW 3 to 6)")]
    [InlineData("1, 0, 1, 1.03, 0.97, 1, 100, '', 0, 8, 10", "regulate a remote bus (SWREM)")]
    // 2026-10-25: blocks of different sizes are switched now (one block at a time in the order listed; PsseFlowControlTests),
    // so this row keeps the mixed blocks with a BINIT (15) that is none of their levels (0, 10, ..., 40, 60, 80).
    [InlineData("1, 0, 1, 1.03, 0.97, 0, 100, '', 15, 4, 10, 2, 20", "have a BINIT that is not a whole number of blocks")]
    [InlineData("1, 0, 1, 1.03, 0.97, 0, 100, '', 15, 8, 10", "have a BINIT that is not a whole number of blocks")]
    [InlineData("1, 0, 1, 1.03, 0.97, 0, 100, '', 100, 8, 10", "have a BINIT outside the range their blocks reach")]
    [InlineData("1, 0, 1, 1.03, 0.97, 0, 100, '', 0", "have no switchable blocks")]
    public void A_shunt_the_switching_cannot_represent_stays_fixed_and_is_noted(string shunt, string reason)
    {
        PsseParseResult r = Read(shunt, WithShunts);
        Assert.Empty(r.SwitchedShunts);
        Assert.Contains(r.Notes, n => n.StartsWith("1 switched shunt(s) " + reason, StringComparison.Ordinal) && n.EndsWith(": they stay fixed at BINIT.", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("1 switched shunt(s) are fixed at their initial susceptance BINIT; a solve", StringComparison.Ordinal));
    }

    [Fact]
    public void A_locked_shunt_stays_fixed_without_a_reason_note()
    {
        PsseParseResult r = Read("0, 0, 1, 1.03, 0.97, 0, 100, '', 10, 8, 10", WithShunts);
        Assert.Empty(r.SwitchedShunts);
        Assert.DoesNotContain(r.Notes, n => n.Contains("they stay fixed at BINIT", StringComparison.Ordinal));
    }

    [Fact]
    public void A_band_that_cannot_make_voltage_control_is_refused_with_its_line()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Read("1, 0, 1, 0.97, 0.97, 0, 100, '', 0, 8, 10", WithShunts));
        Assert.Contains("line 28 (SWITCHED SHUNT data)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("VSWHI must be above VSWLO", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Read("1, 0, 1, 0.97, 0.97, 0, 100, '', 0, 8, 10", Plain).SwitchedShunts);   // not read without the option
    }

    [Fact]
    public void The_revision_35_sample_shunt_with_binit_off_its_block_grid_stays_fixed()
    {
        // sample-v35.raw: bus 3, MODSW 1, band 0.95..1.05, BINIT 7.5, one block of 2 x 5 MVAr: 7.5 is 1.5 blocks.
        PsseParseResult r = PsseRawParser.ParseFile(Fixture("sample-v35.raw"), new PsseReadOptions { ApplyShuntControl = true });
        Assert.Empty(r.SwitchedShunts);
        Assert.Contains(r.Notes, n => n.StartsWith("1 switched shunt(s) have a BINIT that is not a whole number of blocks", StringComparison.Ordinal));
    }

    [Fact]
    public void The_bank_switches_in_until_its_bus_is_in_band_giving_exactly_the_fixed_shunt_solve_at_that_step()
    {
        PsseParseResult r = Read(Eight, WithShunts);
        GridModel m = r.Model;
        // The answer by hand: the first block count k whose plain solve, with k x 10 MVAr added to the bus shunt, puts
        // bus 2 at or above 0.97 pu.
        PowerFlowResult Fixed(int k) => new NewtonRaphsonPowerFlow().Solve(m.CopyWith(buses: m.Buses.Select(b => b.Id == 2 ? b with { Bs = b.Bs + k * 10.0 } : b).ToArray()));
        int expected = 0;
        while (Fixed(expected).Vm[1] < 0.97) expected++;
        Assert.InRange(expected, 1, 8);
        Assert.True(Fixed(0).Vm[1] < 0.97);   // control: the case as read (BINIT 0) is out of band

        var loop = new SwitchedShuntPowerFlow(new NewtonRaphsonPowerFlow(), r.SwitchedShunts);
        var solver = new SteadyStateSolver(loop);
        PowerFlowResult pf = solver.Solve(m);
        Assert.True(pf.Converged && loop.ShuntsSettled, loop.LastNote);
        Assert.Equal(expected, Assert.Single(loop.LastSteps));
        PowerFlowResult want = Fixed(expected);
        for (int i = 0; i < pf.Vm.Length; i++)
        {
            AssertSameBits(want.Vm[i], pf.Vm[i], $"Vm {i}");
            AssertSameBits(want.VaDeg[i], pf.VaDeg[i], $"Va {i}");
        }
    }

    // ---- Command line (runs the built CLI) ------------------------------------------------------------------------------

    [Fact]
    public void Shunt_control_on_the_command_line_switches_the_bank_in_the_case_solve_and_in_pf()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-shunts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string raw = Path.Combine(dir, "shunt.raw");
            File.WriteAllText(raw, TwoBus(Eight));

            CliProcess.Result plain = CliProcess.RunBuilt(raw, "--once");
            Assert.True(plain.ExitCode == 0, plain.ToString());
            Assert.DoesNotContain("Switched shunts:", plain.StdOut, StringComparison.Ordinal);
            Assert.Contains("GridSim does not switch shunt blocks", plain.StdErr, StringComparison.Ordinal);

            CliProcess.Result run = CliProcess.RunBuilt(raw, "--once", "--shunt-control");
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Contains("Switched shunts: 1 bank(s), ", run.StdOut, StringComparison.Ordinal);
            Assert.Contains("settled.", run.StdOut, StringComparison.Ordinal);
            Assert.Contains("note: shunt.raw: 1 switched shunt(s) with discrete voltage control (MODSW 1)", run.StdErr, StringComparison.Ordinal);

            CliProcess.Result pf = CliProcess.RunBuilt("pf", raw, "--shunt-control");
            Assert.True(pf.ExitCode == 0, pf.ToString());
            Assert.Contains("[1 switched shunt(s)]", pf.StdOut, StringComparison.Ordinal);
            Assert.Contains("switched shunts: ", pf.StdOut, StringComparison.Ordinal);

            string file = Path.Combine(dir, "banks.json");
            File.WriteAllText(file, """{ "format": "gridsim-switched-shunts/1", "shunts": [ { "bus": 2, "stepMvar": 10, "maxSteps": 4, "vminPu": 0.97, "vmaxPu": 1.03 } ] }""");
            CliProcess.Result both = CliProcess.RunBuilt("pf", raw, "--shunt-control", "--switched-shunts", file);
            Assert.True(both.ExitCode == 2, both.ToString());
            Assert.Contains("pf: --switched-shunts and --shunt-control both give switched shunts; give one.", both.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Shunt_control_with_no_raw_file_says_it_had_no_effect_and_twice_is_refused()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case14", "--once", "--shunt-control");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("warning: --shunt-control had no effect: this run loaded no PSS/E .raw file.", r.StdErr, StringComparison.Ordinal);
        CliProcess.Result twice = CliProcess.RunBuilt("case14", "--once", "--shunt-control", "--shunt-control");
        Assert.True(twice.ExitCode == 2, twice.ToString());
        Assert.Contains("error: --shunt-control given more than once.", twice.StdErr, StringComparison.Ordinal);
    }
}
