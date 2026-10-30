using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-12 (October 2026): stability and dynamics, docs-session findings D-DOC-051, 052, 054-056, 131-134, 191, 192 and
/// 213 (review-progress/2026-10/DOCS-FINDINGS-TRIAGE.md, stream 12; Ruling R1: Mark delegated 2026-10-04). The library
/// rules are pinned in process with exact Assert.Equal; the CLI behaviour runs the real gridsim binary
/// (CliProcess.RunBuilt) and checks what a script sees: the exit code and the line printed.
/// </summary>
public sealed class Doc12StabilityDynamicsTests
{
    /// <summary>The crash reporter's first line (CrashReport.Write): it means a defect, never bad input.</summary>
    private const string InternalError = "internal error";

    private static void AssertExit(int expected, CliProcess.Result r)
    {
        Assert.True(r.ExitCode == expected, $"expected exit {expected}\n{r}");
        Assert.DoesNotContain(InternalError, r.StdErr, StringComparison.Ordinal);
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-doc12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Program() =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");

    // WSCC-9 solved, with the CLI's Anderson-Fouad X'd per machine (machine base), as the stability tests use it.
    private static (GridModel Model, PowerFlowResult Pf, double[] Xdp) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = IeeeCases.Wscc9TransientReactancePu[model.Generators[i].Bus];
        return (model, pf, xdp);
    }

    // The same machines with every MBase replaced by `mBase` (the power flow does not read MBase, so the solve is
    // reused as it is).
    private static GridModel WithMBase(GridModel model, double mBase)
        => model.CopyWith(generators: model.Generators.Select(g => g with { MBase = mBase }).ToArray());

    private static List<SubtransientInit.MachineData> Genrou(GridModel model, double[] xdp)
    {
        var data = new List<SubtransientInit.MachineData>();
        for (int i = 0; i < model.Generators.Count; i++)
        {
            double x = xdp[i];
            data.Add(new SubtransientInit.MachineData(H: model.Generators[i].H, Damping: 0.0,
                Xd: 3.0 * x, Xdp: x, Xdpp: 0.8 * x, Xq: 2.5 * x, Xqp: 1.5 * x, Xqpp: 0.8 * x,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05));
        }
        return data;
    }

    private static void AssertSameMatrix(Complex[,] a, Complex[,] b)
    {
        Assert.Equal(a.GetLength(0), b.GetLength(0));
        Assert.Equal(a.GetLength(1), b.GetLength(1));
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++)
                Assert.Equal(a[i, j], b[i, j]);
    }

    // ---- D-DOC-055: one machine-base rule ----------------------------------------------------------------------------

    /// <summary>D-DOC-055: a positive MBase is the base; zero or negative means "not given" and the system base is used;
    /// NaN or infinite is refused naming the unit.</summary>
    [Fact]
    public void D055_machine_base_rule()
    {
        Assert.Equal(247.5, MachineBase.EffectiveMva(new Generator(1, 0.0, MBase: 247.5), 100.0));
        Assert.Equal(1e-7, MachineBase.EffectiveMva(new Generator(1, 0.0, MBase: 1e-7), 100.0));   // tiny but positive: kept
        Assert.Equal(100.0, MachineBase.EffectiveMva(new Generator(1, 0.0, MBase: 0.0), 100.0));
        Assert.Equal(100.0, MachineBase.EffectiveMva(new Generator(1, 0.0, MBase: -5.0), 100.0));
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => MachineBase.EffectiveMva(new Generator(7, 0.0, MBase: bad, Name: "G7"), 100.0));
            Assert.Contains("G7 at bus 7", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>D-DOC-055: ClassicalInit with MBase 0 now prepares exactly what it prepares with MBase = BaseMva (the
    /// SubtransientInit rule). Before, X'd on the system base was divided by a 1e-6 MVA floor (1e8 times too large) and
    /// H on the system base was H * 0 / BaseMva = 0.</summary>
    [Fact]
    public void D055_classical_zero_mbase_falls_back_to_the_system_base_exactly()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        ClassicalInit.Prepared zero = ClassicalInit.FromSolvedCase(WithMBase(model, 0.0), pf, xdp, 7, holdNonMachines: true);
        ClassicalInit.Prepared sys = ClassicalInit.FromSolvedCase(WithMBase(model, model.BaseMva), pf, xdp, 7, holdNonMachines: true);
        Assert.Equal(sys.H, zero.H);
        Assert.Equal(sys.Pm, zero.Pm);
        Assert.Equal(sys.Eq, zero.Eq);
        Assert.Equal(sys.Delta0, zero.Delta0);
        AssertSameMatrix(sys.YReduced, zero.YReduced);
        AssertSameMatrix(sys.YReducedFaulted!, zero.YReducedFaulted!);
        // H on the system base is the machine's H itself (the base ratio is 1), not 0.
        for (int i = 0; i < zero.H.Length; i++) Assert.Equal(model.Generators[i].H, zero.H[i]);
    }

    /// <summary>D-DOC-055: a positive MBase is untouched: H on the system base is still H * MBase / BaseMva, bit for bit,
    /// on the bundled WSCC-9 machines (247.5 / 192 / 128 MVA).</summary>
    [Fact]
    public void D055_classical_positive_mbase_unchanged()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        ClassicalInit.Prepared p = ClassicalInit.FromSolvedCase(model, pf, xdp, 7, holdNonMachines: true);
        for (int i = 0; i < p.H.Length; i++)
            Assert.Equal(model.Generators[i].H * model.Generators[i].MBase / model.BaseMva, p.H[i]);
    }

    /// <summary>D-DOC-055: SubtransientInit keeps its fallback (MBase 0 = MBase BaseMva, exactly) and now refuses a NaN
    /// MBase instead of producing NaN machine data.</summary>
    [Fact]
    public void D055_subtransient_shares_the_rule()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        List<SubtransientInit.MachineData> data = Genrou(model, xdp);
        SubtransientInit.Prepared zero, sys;
        // N271: prepared on the dense Kron test oracle so the reduced-matrix comparison below keeps its strength (on the
        // production sparse path YReduced is empty and the comparison would be vacuous). Same matrices as before.
        using (SubtransientInit.DenseKronReferenceScope())
        {
            zero = SubtransientInit.FromSolvedCase(WithMBase(model, 0.0), pf, data, 7);
            sys = SubtransientInit.FromSolvedCase(WithMBase(model, model.BaseMva), pf, data, 7);
        }
        Assert.Equal(sys.H, zero.H);
        Assert.Equal(sys.Pm, zero.Pm);
        Assert.Equal(sys.Efd, zero.Efd);
        Assert.Equal(sys.Delta0, zero.Delta0);
        AssertSameMatrix(sys.YReduced, zero.YReduced);
        Assert.Throws<ArgumentException>(() => SubtransientInit.FromSolvedCase(WithMBase(model, double.NaN), pf, data, 7));
        Assert.Throws<ArgumentException>(() => ClassicalInit.FromSolvedCase(WithMBase(model, double.NaN), pf, xdp, 7));
    }

    // ---- D-DOC-213: the one convergence check ------------------------------------------------------------------------

    /// <summary>D-DOC-213: SubtransientInit refuses a non-converged state through the convergence contract alone (the
    /// dead second check is gone); the exception is the contract's NonConvergedStateException.</summary>
    [Fact]
    public void D213_subtransient_init_refuses_a_non_converged_state_through_the_contract()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        var bad = new PowerFlowResult
        {
            Converged = false, Status = PowerFlowStatus.MaxIterations, Iterations = pf.Iterations, MaxMismatch = 1.0,
            BusIds = pf.BusIds, Vm = pf.Vm, VaDeg = pf.VaDeg, PInjMw = pf.PInjMw, QInjMvar = pf.QInjMvar,
            TotalLossMw = pf.TotalLossMw, TotalLossMvar = pf.TotalLossMvar, LineFlows = pf.LineFlows,
        };
        NonConvergedStateException ex = Assert.Throws<NonConvergedStateException>(
            () => SubtransientInit.FromSolvedCase(model, bad, Genrou(model, xdp)));
        Assert.Equal("Subtransient initialisation", ex.Study);
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "Dynamics", "Models", "SubtransientInit.cs"));
        Assert.DoesNotContain("Subtransient init needs a converged power flow.", src, StringComparison.Ordinal);
    }

    // ---- D-DOC-056: SubtransientMachine validates Xd / Xq -------------------------------------------------------------

    private static SubtransientMachine.Parameters Machine(double xd, double xq) => new(
        InertiaSeconds: 4.0, DampingPu: 0.0,
        Xd: xd, Xdp: 0.3, Xdpp: 0.22, Xq: xq, Xqp: 0.55, Xqpp: 0.25,
        Tdop: 6.0, Tdopp: 0.04, Tqop: 0.4, Tqopp: 0.06,
        ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

    /// <summary>D-DOC-056: a zero, negative, NaN or infinite Xd / Xq, or one below its transient reactance, is refused
    /// (it used to build a machine whose flux-decay drivers had the wrong sign, with no error).</summary>
    [Theory]
    [InlineData(0.0, 1.7, "Xd")]
    [InlineData(-1.8, 1.7, "Xd")]
    [InlineData(double.NaN, 1.7, "Xd")]
    [InlineData(double.PositiveInfinity, 1.7, "Xd")]
    [InlineData(0.29, 1.7, "Xd")]     // below X'd = 0.3
    [InlineData(1.8, 0.0, "Xq")]
    [InlineData(1.8, -1.7, "Xq")]
    [InlineData(1.8, double.NaN, "Xq")]
    [InlineData(1.8, 0.54, "Xq")]     // below X'q = 0.55
    public void D056_xd_and_xq_are_validated(double xd, double xq, string param)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => Machine(xd, xq));
        Assert.Equal(param, ex.ParamName);
    }

    /// <summary>D-DOC-056: the limits themselves are allowed (Xd = X'd, Xq = X'q: the reduction identities the existing
    /// tests use), and a valid machine keeps its values.</summary>
    [Fact]
    public void D056_equal_to_transient_is_allowed()
    {
        SubtransientMachine.Parameters p = Machine(0.3, 0.55);
        Assert.Equal(0.3, p.Xd);
        Assert.Equal(0.55, p.Xq);
        SubtransientMachine.Parameters q = Machine(1.8, 1.7);
        Assert.Equal(1.8, q.Xd);
        Assert.Equal(1.7, q.Xq);
    }

    // ---- D-DOC-133: one machine classification -----------------------------------------------------------------------

    /// <summary>D-DOC-133: IsNonMachine is the classical study's split: converter, power-flow-only, or no inertia.</summary>
    [Fact]
    public void D133_is_non_machine_classification()
    {
        Assert.False(ClassicalInit.IsNonMachine(new Generator(1, 100.0, H: 5.0, Type: "ccgt")));
        Assert.True(ClassicalInit.IsNonMachine(new Generator(1, 100.0, H: 5.0, Type: "battery")));
        Assert.True(ClassicalInit.IsNonMachine(new Generator(1, 100.0, H: 5.0, PowerFlowOnly: true)));
        Assert.True(ClassicalInit.IsNonMachine(new Generator(1, 100.0, H: 0.0)));
        Assert.True(ClassicalInit.IsNonMachine(new Generator(1, 100.0, H: double.NaN)));
    }

    // ---- D-DOC-054: the solved operating point -----------------------------------------------------------------------

    /// <summary>D-DOC-054: a lone unit's solved output is its bus's solved generation (net injection plus load),
    /// bit for bit; for WSCC-9 bus 2 (a PV bus with no load) that is the solved injection itself, and its Q is the
    /// solve's, not the scheduled Qg.</summary>
    [Fact]
    public void D054_unit_output_is_the_solved_bus_generation()
    {
        (GridModel model, PowerFlowResult pf, _) = Case9();
        for (int k = 0; k < model.Generators.Count; k++)
        {
            Generator g = model.Generators[k];
            int row = pf.BusIds.ToList().IndexOf(g.Bus);
            Bus bus = model.Buses.Single(b => b.Id == g.Bus);
            (double p, double q) = SolvedGeneration.UnitOutput(model, pf, k);
            Assert.Equal(pf.PInjMw[row] + bus.Pd, p);
            Assert.Equal(pf.QInjMvar[row] + bus.Qd, q);
        }
        int bus2 = model.Generators.ToList().FindIndex(g => g.Bus == 2);
        (_, double q2) = SolvedGeneration.UnitOutput(model, pf, bus2);
        Assert.True(Math.Abs(q2 - model.Generators[bus2].Qg) > 1e-2, $"solved Q {q2} MVAr vs scheduled {model.Generators[bus2].Qg} MVAr");
    }

    /// <summary>D-DOC-054: co-located units split the bus by scheduled P (active) and by reactive range (reactive),
    /// the SubtransientInit rule, and the shares are exact.</summary>
    [Fact]
    public void D054_co_located_units_split_by_the_subtransient_rule()
    {
        (GridModel model, _, _) = Case9();
        // Split WSCC-9's bus-2 unit (163 MW) into two units of 97.8 and 65.2 MW with reactive ranges 300 and 100 MVAr.
        Generator g2 = model.Generators.Single(g => g.Bus == 2);
        var fleet = new List<Generator>();
        foreach (Generator g in model.Generators)
        {
            if (g.Bus != 2) { fleet.Add(g); continue; }
            fleet.Add(g with { Pg = 97.8, Qmin = -150.0, Qmax = 150.0, Name = "A" });
            fleet.Add(g with { Pg = 65.2, Qmin = -50.0, Qmax = 50.0, Name = "B" });
        }
        GridModel split = model.CopyWith(generators: fleet);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(split);
        Assert.True(pf.Converged);
        int row = pf.BusIds.ToList().IndexOf(2);
        double busP = pf.PInjMw[row], busQ = pf.QInjMvar[row];   // bus 2 carries no load
        int ia = fleet.FindIndex(g => g.Name == "A"), ib = fleet.FindIndex(g => g.Name == "B");
        (double pa, double qa) = SolvedGeneration.UnitOutput(split, pf, ia);
        (double pb, double qb) = SolvedGeneration.UnitOutput(split, pf, ib);
        Assert.Equal(busP * (97.8 / (97.8 + 65.2)), pa);
        Assert.Equal(busP * (65.2 / (97.8 + 65.2)), pb);
        Assert.Equal(busQ * (300.0 / 400.0), qa);
        Assert.Equal(busQ * (100.0 / 400.0), qb);
        _ = g2;
    }

    /// <summary>D-DOC-054: fail loud on a non-converged state, an index outside the fleet, or a result of another model.</summary>
    [Fact]
    public void D054_unit_output_fails_loud()
    {
        (GridModel model, PowerFlowResult pf, _) = Case9();
        var bad = new PowerFlowResult
        {
            Converged = false, Status = PowerFlowStatus.MaxIterations, Iterations = pf.Iterations, MaxMismatch = 1.0,
            BusIds = pf.BusIds, Vm = pf.Vm, VaDeg = pf.VaDeg, PInjMw = pf.PInjMw, QInjMvar = pf.QInjMvar,
            TotalLossMw = pf.TotalLossMw, TotalLossMvar = pf.TotalLossMvar, LineFlows = pf.LineFlows,
        };
        Assert.Throws<NonConvergedStateException>(() => SolvedGeneration.UnitOutput(model, bad, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolvedGeneration.UnitOutput(model, pf, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolvedGeneration.UnitOutput(model, pf, model.Generators.Count));
        Assert.Throws<ArgumentException>(() => SolvedGeneration.UnitOutput(IeeeCases.Case14(), pf, 0));
    }

    /// <summary>D-DOC-054, end to end: the impedance-stability export's operating point carries the solved P and Q of
    /// case9 bus 2 (to the solver's convergence, since the CLI's backend solves its own copy), not the scheduled ones
    /// (before: pPu 163 / 192 = 0.8489583333333334 and qPu 6.54 / 192 = 0.0340625).</summary>
    [Fact]
    public void D054_impedance_stability_uses_the_solved_operating_point()
    {
        (GridModel model, PowerFlowResult pf, _) = Case9();
        int k = model.Generators.ToList().FindIndex(g => g.Bus == 2);
        (double pMw, double qMvar) = SolvedGeneration.UnitOutput(model, pf, k);
        double mBase = model.Generators[k].MBase;
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "imp.json");
            CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", $"--enableExport={json}");
            AssertExit(0, r);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement op = doc.RootElement.GetProperty("operatingPoint");
            double pPu = op.GetProperty("pPu").GetDouble(), qPu = op.GetProperty("qPu").GetDouble();
            Assert.True(Math.Abs(pPu - pMw / mBase) < 1e-9, $"pPu {pPu} vs solved {pMw / mBase}");
            Assert.True(Math.Abs(qPu - qMvar / mBase) < 1e-9, $"qPu {qPu} vs solved {qMvar / mBase}");
            Assert.True(Math.Abs(qPu - model.Generators[k].Qg / mBase) > 1e-4, $"qPu {qPu} still the scheduled value");
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-051: --clear range ------------------------------------------------------------------------------------

    /// <summary>D-DOC-051: a negative, NaN or infinite --clear is a usage error (exit 2) on both engines; it used to run
    /// a study with no fault in it and print STABLE with exit 0.</summary>
    [Theory]
    [InlineData("-50")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-0.001")]
    public void D051_negative_or_non_finite_clear_exits_2(string value)
    {
        foreach (string[] args in new[]
        {
            new[] { "stability", "case9", "--clear", value },
            new[] { "stability", "case9", "--rms", "--clear", value },
        })
        {
            CliProcess.Result r = CliProcess.RunBuilt(args);
            AssertExit(2, r);
            Assert.Contains($"error: --clear expected a finite clearing time in ms, 0 or more but got '{value}'", r.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("STABLE", r.StdOut, StringComparison.Ordinal);
        }
    }

    /// <summary>D-DOC-051: 0 ms (an instantaneous clear) and an ordinary value still run.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    public void D051_zero_and_positive_clear_still_run(string value)
    {
        CliProcess.Result r = CliProcess.RunBuilt("stability", "case9", "--clear", value);
        AssertExit(0, r);
        Assert.Contains($"cleared after {value} ms", r.StdOut, StringComparison.Ordinal);
    }

    // ---- D-DOC-052: --export with --rms / --screen ---------------------------------------------------------------------

    /// <summary>D-DOC-052: --export with --rms or --screen is refused (exit 2, no file); it used to be accepted with no
    /// file written and exit 0.</summary>
    [Theory]
    [InlineData("--rms")]
    [InlineData("--screen")]
    public void D052_export_with_rms_or_screen_exits_2_and_writes_nothing(string mode)
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "x.json");
            CliProcess.Result r = CliProcess.RunBuilt("stability", "case9", mode, "--export", json);
            AssertExit(2, r);
            Assert.Contains($"error: stability: --export writes the classical single-bus trajectory and is not available with {mode} (no file would be written).",
                r.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(json));
            Assert.Equal("", r.StdOut);   // refused before any load or solve
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-132 / 133: headers ------------------------------------------------------------------------------------

    /// <summary>D-DOC-132: the classical table header has a gap between "|E'| pu" and "delta0 (deg)", and the rows keep
    /// the case9 values.</summary>
    [Fact]
    public void D132_classical_header_has_a_gap()
    {
        CliProcess.Result r = CliProcess.RunBuilt("stability", "case9");
        AssertExit(0, r);
        Assert.Contains("  Bus   H sys (s)   |E'| pu  delta0 (deg)\n", r.StdOut.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("pudelta0", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("    1      58.509     1.047           0.9\n", r.StdOut.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    /// <summary>D-DOC-133: the screen header counts the integrated machines and the held units (data/uk: 13 machines and
    /// 16 held, the single-bus run's own split; it used to print "29 machines"); case9 has no held unit.</summary>
    [Fact]
    public void D133_screen_header_counts_machines_not_all_generators()
    {
        CliProcess.Result c9 = CliProcess.RunBuilt("stability", "case9", "--screen");
        AssertExit(0, c9);
        Assert.Contains("3 machines (0 converter / power-flow-only / zero-inertia units held)  *  3 generator-bus faults  *  X'd = Anderson-Fouad",
            c9.StdOut, StringComparison.Ordinal);

        string uk = Path.Combine(CliProcess.Root(), "data", "uk");
        CliProcess.Result screen = CliProcess.RunBuilt("stability", "--dir", uk, "--screen");
        AssertExit(0, screen);
        Assert.Contains("13 machines (16 converter / power-flow-only / zero-inertia units held)  *  10 generator-bus faults",
            screen.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("29 machines", screen.StdOut, StringComparison.Ordinal);
    }

    // ---- D-DOC-131 / 134: impedance-stability lines ------------------------------------------------------------------

    /// <summary>D-DOC-131: with --grid real no SCR sweep runs, and the boundary line says so instead of "stable across the
    /// swept SCR range".</summary>
    [Fact]
    public void D131_grid_real_reports_no_sweep()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--grid", "real");
        AssertExit(0, r);
        Assert.Contains("  SCR boundary    : n/a (--grid real: the network's own impedance is used, no SCR sweep runs)", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("stable across the swept SCR range", r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-131: when the Nyquist verdict is UNSTABLE at every swept SCR (case9 --outer-loop-only), the line
    /// no longer claims stability across the range.</summary>
    [Fact]
    public void D131_all_unstable_sweep_is_not_called_stable()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--outer-loop-only");
        AssertExit(0, r);
        Assert.Contains("-> UNSTABLE", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("  SCR boundary    : none in [1.0, 10.0]  (UNSTABLE at every swept SCR: any boundary lies above 10.0)", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("stable across the swept SCR range", r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-131: the stable case keeps its old line.</summary>
    [Fact]
    public void D131_all_stable_sweep_keeps_its_line()
    {
        // Moved 2026-10 (B5 R138, Ruling R1: Mark delegated EE calls 2026-10-04; source: R138 in FULL-REVIEW-2026-10.md, one
        // PLL default for the three converter models): the flag-built grid-following PLL placeholder moved 5 Hz -> 20 Hz,
        // and a faster PLL is the destabilising direction on a weak grid, so case9's unflagged sweep may no longer be
        // stable at SCR 1 (to be seen at the batch). This test pins the TEXT of the all-stable line, not the default, so it
        // now asks for the old 5 Hz explicitly, which reproduces the pre-R138 model bit for bit (same bandwidth, the same
        // 0.707 damping). Before: "impedance-stability case9". After: "impedance-stability case9 --pll-bw 5".
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--pll-bw", "5");
        AssertExit(0, r);
        Assert.Contains("  SCR boundary    : none in [1.0, 10.0]  (stable across the swept SCR range)", r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-134: the modal cross-check names its swing-model basis: Anderson-Fouad X'd on case9, the synthetic
    /// 0.30 pu elsewhere, and zero damping on both.</summary>
    [Fact]
    public void D134_cross_check_names_its_basis()
    {
        CliProcess.Result c9 = CliProcess.RunBuilt("impedance-stability", "case9");
        AssertExit(0, c9);
        Assert.Contains("[swing model: X'd Anderson-Fouad, zero damping (no damping data)]", c9.StdOut, StringComparison.Ordinal);

        string uk = Path.Combine(CliProcess.Root(), "data", "uk");
        CliProcess.Result u = CliProcess.RunBuilt("impedance-stability", "--dir", uk);
        AssertExit(0, u);
        Assert.Contains("[swing model: X'd synthetic 0.30 pu, zero damping (no damping data); INDICATIVE]", u.StdOut, StringComparison.Ordinal);
    }

    // ---- D-DOC-192: --current-bw ---------------------------------------------------------------------------------------

    /// <summary>D-DOC-192: the unreachable 400 default is gone from the code, and a zero or negative bandwidth is a
    /// usage error (it used to fall back silently to the stand-in roll-off on the flag-built model).</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void D192_non_positive_current_bandwidth_exits_2(string value)
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--current-bw", value);
        AssertExit(2, r);
        Assert.Contains("error: --current-bw expected a current-loop bandwidth in Hz above 0", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void D192_no_unreachable_default_and_a_value_still_runs()
    {
        Assert.DoesNotContain("FlagDouble(a, \"--current-bw\", 400.0)", Program(), StringComparison.Ordinal);
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--current-bw", "400");
        AssertExit(0, r);
        Assert.Contains("detail PllCurrentOuter", r.StdOut, StringComparison.Ordinal);
        CliProcess.Result bare = CliProcess.RunBuilt("impedance-stability", "case9", "--current-bw");
        AssertExit(2, bare);
    }

    // ---- D-DOC-191 / 213: comments say what the code does -------------------------------------------------------------

    /// <summary>D-DOC-191: the CctSearch comment states the measured WSCC-9 bus 2 pocket (175 steps, 350 ms), which the
    /// screen reports.</summary>
    [Fact]
    public void D191_cct_search_comment_matches_the_run()
    {
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "Dynamics", "Models", "CctSearch.cs"));
        Assert.Contains("175 (350 ms) holds again", src, StringComparison.Ordinal);
        Assert.DoesNotContain("174 (348 ms) holds again", src, StringComparison.Ordinal);
        CliProcess.Result r = CliProcess.RunBuilt("stability", "case9", "--screen");
        AssertExit(0, r);
        // Moved 2026-10 (A6, final batch; D2 N017, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): case9 is
        // 60 Hz, so the bus 2 CCT scales by 1 / sqrt(1.2) (D2 predicted 342 / sqrt(1.2) = 312.2 ms); the non-monotone
        // pocket remains. Before -> after: "342   NON-MONOTONE: holds again at 350 ms" -> "312   NON-MONOTONE: holds
        // again at 320 ms". The source-comment checks above (the 50 Hz CctSearch measurement) are unchanged.
        Assert.Contains("312   NON-MONOTONE: holds again at 320 ms", r.StdOut, StringComparison.Ordinal);
    }
}
