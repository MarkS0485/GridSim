using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N199 (October 2026, Ruling R1 follow-up, Mark delegated 2026-10-04): a built-in case must equal its MATPOWER file.
///
/// <para>The only built-in name that was typed out in code while a <c>data/matpower/lib</c> file of the same case ships is
/// <c>case9</c> (case14, case30, case57, case118, case300, case145, case24 / rts and cigre are parsed from those very
/// files, embedded in GridSim.Core, so they cannot differ). The hand copy carried no per-bus limits, no Pmin, no
/// RATE_B / RATE_C and no gencost. <see cref="IeeeCases.Case9"/> now reads the embedded copy of
/// <c>data/matpower/lib/case9.m</c> and adds only the published WSCC machine data (MBase, H), which no MATPOWER file
/// can carry.</para>
///
/// <para>These tests prove, bit for bit (doubles compared by their 64-bit patterns), that every bus, branch and
/// generator field equals the file except MBase and H, that the power flow, the N-1 screen with per-bus limits and the
/// DC-OPF (fuel merit order and the case's own cost curves) give identical results from the name and from the file, and
/// they pin the machine data so the dynamics studies keep their published inputs.</para>
/// </summary>
public sealed class N199AlignedCase9Tests
{
    // Moved 2026-10 (D2 N017, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad, "Power System
    // Control and Stability", 2nd ed. 2003, Example 2.6, and Sauer and Pai 1998 ch. 7; see IeeeCases.Wscc9NominalFrequencyHz): the
    // built-in case9 now carries the WSCC system's real 60 Hz, which no MATPOWER file can state. The file is read with that
    // same declared frequency (what `--frequency-hz 60` does on the CLI, N187), so "every field equals the file" still
    // compares like with like. Before: ParseFile(path) at the assumed 50 Hz on both sides. After: both sides 60 Hz. Every
    // other field and every power-flow / N-1 / DC-OPF comparison below is unchanged (the frequency enters none of them).
    private static GridModel FromFile() =>
        MatpowerParser.ParseFile(Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m"),
            frequencyHz: IeeeCases.Wscc9NominalFrequencyHz);

    private static void SameBits(double expected, double actual, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    private static void SameBits(double? expected, double? actual, string what)
    {
        Assert.True(expected.HasValue == actual.HasValue, $"{what}: one side is null");
        if (expected.HasValue) SameBits(expected.Value, actual!.Value, what);
    }

    [Fact]
    public void Every_bus_and_branch_field_equals_the_file()
    {
        GridModel b = IeeeCases.Case9(), f = FromFile();
        SameBits(f.BaseMva, b.BaseMva, "BaseMva");
        SameBits(f.FrequencyHz, b.FrequencyHz, "FrequencyHz");
        Assert.Equal(f.Buses.Count, b.Buses.Count);
        for (int i = 0; i < f.Buses.Count; i++)
        {
            Bus x = f.Buses[i], y = b.Buses[i];
            // Bus is a record of scalars (Zip null on both): record equality compares every field, doubles by value.
            Assert.Equal(x, y);
            SameBits(x.VminPu, y.VminPu, $"bus {x.Id} Vmin");
            SameBits(x.VmaxPu, y.VmaxPu, $"bus {x.Id} Vmax");
        }
        Assert.Equal(f.Branches.Count, b.Branches.Count);
        for (int i = 0; i < f.Branches.Count; i++)
            Assert.Equal(f.Branches[i], b.Branches[i]);   // a record of scalars: every field, RATE_B / RATE_C included
        Assert.Equal(f.Oltcs.Count, b.Oltcs.Count);
        Assert.Equal(f.Boundaries.Count, b.Boundaries.Count);

        // The fields the hand copy lacked (before N199: Vmin / Vmax null, RATE_B / RATE_C 0), pinned to the file.
        foreach (Bus bus in b.Buses)
        {
            Assert.Equal(0.9, bus.VminPu);
            Assert.Equal(1.1, bus.VmaxPu);
        }
        Assert.Equal(new[] { 250.0, 250, 150, 300, 150, 250, 250, 250, 250 }, b.Branches.Select(r => r.RateBMva).ToArray());
        Assert.Equal(new[] { 250.0, 250, 150, 300, 150, 250, 250, 250, 250 }, b.Branches.Select(r => r.RateCMva).ToArray());
    }

    [Fact]
    public void Every_generator_field_except_the_machine_data_equals_the_file()
    {
        GridModel b = IeeeCases.Case9(), f = FromFile();
        Assert.Equal(f.AllGenerators.Count, b.AllGenerators.Count);
        for (int i = 0; i < f.AllGenerators.Count; i++)
        {
            Generator x = f.AllGenerators[i], y = b.AllGenerators[i];
            // Cost holds a list (compared by reference in record equality), so compare it apart and blank it here.
            Assert.Equal(x with { MBase = y.MBase, H = y.H, Cost = null, ReactiveCost = null },
                         y with { Cost = null, ReactiveCost = null });
            Assert.NotNull(x.Cost);
            Assert.NotNull(y.Cost);
            Assert.Equal(x.Cost!.Model, y.Cost!.Model);
            SameBits(x.Cost.StartupCost, y.Cost.StartupCost, $"gen {x.Bus} startup");
            SameBits(x.Cost.ShutdownCost, y.Cost.ShutdownCost, $"gen {x.Bus} shutdown");
            Assert.Equal(x.Cost.Coefficients.Count, y.Cost.Coefficients.Count);
            for (int k = 0; k < x.Cost.Coefficients.Count; k++)
                SameBits(x.Cost.Coefficients[k], y.Cost.Coefficients[k], $"gen {x.Bus} cost[{k}]");
            Assert.Equal(x.ReactiveCost is null, y.ReactiveCost is null);
            // The file's Pmin (10 MW per unit), which the hand copy lacked (before N199: 0).
            Assert.Equal(10.0, y.Pmin);
        }
    }

    [Fact]
    public void The_machine_data_is_the_published_wscc_set_unchanged()
    {
        // Anderson and Fouad's WSCC 3-machine data, as the hand-coded case carried it before N199 (unchanged): the
        // machine rating as MBase and H on that base. The file's mBase is a 100 MVA placeholder and it has no H.
        GridModel b = IeeeCases.Case9(), f = FromFile();
        var expected = new Dictionary<int, (double MBase, double H)> { [1] = (247.5, 23.64), [2] = (192.0, 6.40), [3] = (128.0, 3.01) };
        foreach (Generator g in b.AllGenerators)
        {
            SameBits(expected[g.Bus].MBase, g.MBase, $"gen {g.Bus} MBase");
            SameBits(expected[g.Bus].H, g.H, $"gen {g.Bus} H");
        }
        foreach (Generator g in f.AllGenerators)
        {
            Assert.Equal(100.0, g.MBase);
            Assert.Equal(0.0, g.H);
        }
        Assert.Equal("IEEE 9-bus (WSCC)", b.Name);
    }

    [Fact]
    public void The_power_flow_from_the_name_and_from_the_file_is_bit_identical()
    {
        GridModel b = IeeeCases.Case9(), f = FromFile();
        foreach (PowerFlowOptions opt in new[] { new PowerFlowOptions(), ContingencyAnalysis.StudyOptions })
        {
            PowerFlowResult x = new SteadyStateSolver().Solve(f, opt), y = new SteadyStateSolver().Solve(b, opt);
            Assert.True(x.Converged && y.Converged);
            Assert.Equal(x.Iterations, y.Iterations);
            for (int i = 0; i < f.BusCount; i++)
            {
                SameBits(x.Vm[i], y.Vm[i], $"Vm[{i}]");
                SameBits(x.VaDeg[i], y.VaDeg[i], $"VaDeg[{i}]");
            }
        }
    }

    [Fact]
    public void The_n_minus_1_screen_with_per_bus_limits_is_identical_and_finds_4_insecure_outages()
    {
        // Before N199 the name gave 5 insecure outages (branch 8-9 flagged at 0.938 pu against the 0.94 band) and the
        // file 4 (0.938 is inside the file's 0.9 pu limit). Now both give 4.
        GridModel b = IeeeCases.Case9(), f = FromFile();
        PowerFlowOptions opt = new() { MaxIterations = ContingencyAnalysis.StudyOptions.MaxIterations, EnforceQLimits = true, PerBusVoltageLimits = true };
        IReadOnlyList<ContingencyResult> x = ContingencyAnalysis.ScreenBranches(f, ContingencyAnalysis.CreateStudySolver(f), opt);
        IReadOnlyList<ContingencyResult> y = ContingencyAnalysis.ScreenBranches(b, ContingencyAnalysis.CreateStudySolver(b), opt);
        Assert.Equal(x.Count, y.Count);
        int insecure = 0;
        for (int i = 0; i < x.Count; i++)
        {
            Assert.Equal(x[i].Outage, y[i].Outage);
            Assert.Equal(x[i].Converged, y[i].Converged);
            SameBits(x[i].MaxBranchLoadingPercent, y[i].MaxBranchLoadingPercent, $"{x[i].Outage} loading");
            SameBits(x[i].MinVm, y[i].MinVm, $"{x[i].Outage} MinVm");
            SameBits(x[i].MaxVm, y[i].MaxVm, $"{x[i].Outage} MaxVm");
            Assert.Equal(x[i].Violations, y[i].Violations);
            if (!y[i].Secure) insecure++;
        }
        Assert.Equal(4, insecure);
    }

    [Fact]
    public void The_dc_opf_from_the_name_and_from_the_file_is_bit_identical_with_both_cost_sources()
    {
        GridModel b = IeeeCases.Case9(), f = FromFile();
        foreach (DcOptimalPowerFlow.DcOpfOptions opt in new[] { new DcOptimalPowerFlow.DcOpfOptions(), new DcOptimalPowerFlow.DcOpfOptions { UseGeneratorCosts = true } })
        {
            DcOptimalPowerFlow.DcOpfResult x = DcOptimalPowerFlow.Solve(f, opt), y = DcOptimalPowerFlow.Solve(b, opt);
            Assert.True(x.Feasible && y.Feasible);
            SameBits(x.TotalCost, y.TotalCost, "TotalCost");
            SameBits(x.TotalGenMw, y.TotalGenMw, "TotalGenMw");
            Assert.Equal(x.Dispatch.Count, y.Dispatch.Count);
            for (int i = 0; i < x.Dispatch.Count; i++)
            {
                Assert.Equal(x.Dispatch[i].GenBus, y.Dispatch[i].GenBus);
                SameBits(x.Dispatch[i].Pg, y.Dispatch[i].Pg, $"gen {x.Dispatch[i].GenBus} Pg");
                SameBits(x.Dispatch[i].CostPerMwh, y.Dispatch[i].CostPerMwh, $"gen {x.Dispatch[i].GenBus} cost");
            }
        }
        // The fuel merit-order dispatch now respects the file's 10 MW Pmin: before N199 the name dispatched bus 3 at
        // 270 MW and bus 2 at 45 MW with bus 1 at 0; now bus 3 270, bus 2 35, bus 1 10 (total 315 MW, cost 15,750 GBP/h
        // unchanged: every unit carries the same 50 GBP/MWh fuel price).
        DcOptimalPowerFlow.DcOpfResult merit = DcOptimalPowerFlow.Solve(b, new DcOptimalPowerFlow.DcOpfOptions());
        var pg = new Dictionary<int, double>();
        foreach (var d in merit.Dispatch) pg[d.GenBus] = d.Pg;
        Assert.Equal(10.0, pg[1], 9);
        Assert.Equal(35.0, pg[2], 9);
        Assert.Equal(270.0, pg[3], 9);
    }

    [Fact]
    public void The_retired_hand_typed_table_still_agrees_with_the_parsed_file_field_for_field()
    {
        // Before N199, MatpowerParserTests' case9 round trip compared the parser against an INDEPENDENT hand-typed copy.
        // Now that Case9() reads the file, that test compares the file with itself, so the independent check would be
        // lost. Kept here instead: the retired hand-typed values (IeeeCases.cs at 1.95.146, every field it set), checked
        // exactly against the parse of data/matpower/lib/case9.m.
        GridModel f = FromFile();
        double[,] bus = { { 1, 0, 0 }, { 2, 0, 0 }, { 3, 0, 0 }, { 4, 0, 0 }, { 5, 90, 30 }, { 6, 0, 0 }, { 7, 100, 35 }, { 8, 0, 0 }, { 9, 125, 50 } };
        BusType[] types = { BusType.Slack, BusType.Pv, BusType.Pv, BusType.Pq, BusType.Pq, BusType.Pq, BusType.Pq, BusType.Pq, BusType.Pq };
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal((int)bus[i, 0], f.Buses[i].Id);
            Assert.Equal(types[i], f.Buses[i].Type);
            Assert.Equal(bus[i, 1], f.Buses[i].Pd);
            Assert.Equal(bus[i, 2], f.Buses[i].Qd);
            Assert.Equal(345.0, f.Buses[i].BaseKv);
        }
        // bus, Pg, Qg, Vg, Qmin, Qmax, Pmax
        double[,] gen = { { 1, 72.3, 27.03, 1.04, -300, 300, 250 }, { 2, 163, 6.54, 1.025, -300, 300, 300 }, { 3, 85, -10.95, 1.025, -300, 300, 270 } };
        for (int i = 0; i < 3; i++)
        {
            Generator g = f.AllGenerators[i];
            Assert.Equal((int)gen[i, 0], g.Bus);
            Assert.Equal(gen[i, 1], g.Pg);
            Assert.Equal(gen[i, 2], g.Qg);
            Assert.Equal(gen[i, 3], g.Vg);
            Assert.Equal(gen[i, 4], g.Qmin);
            Assert.Equal(gen[i, 5], g.Qmax);
            Assert.Equal(gen[i, 6], g.Pmax);
        }
        // from, to, r, x, b, rateA
        double[,] br =
        {
            { 1, 4, 0.0000, 0.0576, 0.000, 250 }, { 4, 5, 0.0170, 0.0920, 0.158, 250 }, { 5, 6, 0.0390, 0.1700, 0.358, 150 },
            { 3, 6, 0.0000, 0.0586, 0.000, 300 }, { 6, 7, 0.0119, 0.1008, 0.209, 150 }, { 7, 8, 0.0085, 0.0720, 0.149, 250 },
            { 8, 2, 0.0000, 0.0625, 0.000, 250 }, { 8, 9, 0.0320, 0.1610, 0.306, 250 }, { 9, 4, 0.0100, 0.0850, 0.176, 250 },
        };
        for (int i = 0; i < 9; i++)
        {
            Branch b = f.Branches[i];
            Assert.Equal((int)br[i, 0], b.From);
            Assert.Equal((int)br[i, 1], b.To);
            Assert.Equal(br[i, 2], b.R);
            Assert.Equal(br[i, 3], b.X);
            Assert.Equal(br[i, 4], b.B);
            Assert.Equal(br[i, 5], b.RateAMva);
            Assert.Equal(1.0, b.TapRatio);
            Assert.Equal(0.0, b.PhaseShiftDeg);
        }
    }

    [Fact]
    public void Only_case9_was_typed_out_and_every_other_ieee_name_reads_its_embedded_file()
    {
        // Source pin: the other IEEE / CIGRE factories go through Mpc (the embedded .m), and Case9 now does too, with no
        // hand-typed bus table left.
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "IO", "IeeeCases.cs"));
        // Moved 2026-10 (A9, final batch; D2 N017, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): case9
        // still reads its embedded file, now declared at its published 60 Hz. Before -> after: the pinned line
        // "GridModel file = Mpc(case9, IEEE 9-bus (WSCC));" gains the argument Wscc9NominalFrequencyHz.
        Assert.Contains("GridModel file = Mpc(\"case9\", \"IEEE 9-bus (WSCC)\", Wscc9NominalFrequencyHz);", src, StringComparison.Ordinal);
        Assert.DoesNotContain("new Bus(5, BusType.Pq, Pd: 90", src, StringComparison.Ordinal);
        foreach (string res in new[] { "case14", "case30", "case57", "case118", "case300", "case145", "case24_ieee_rts", "cigre_mv" })
            Assert.Contains($"Mpc(\"{res}\"", src, StringComparison.Ordinal);
    }
}
