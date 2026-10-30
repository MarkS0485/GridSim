using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// D2 nominal frequency (October 2026; Ruling R1: Mark delegated EE calls 2026-10-04).
///
/// <para>The rule these tests pin: a dynamic study of a model integrates at THAT model's nominal frequency
/// (<see cref="GridModel.FrequencyHz"/>) unless the caller explicitly overrides it, and a 50 Hz model gives exactly the
/// numbers it gave before (the same 50.0 reaches every kernel). Items: R238 (both CCT searches and the RMS study used a
/// hard-coded 50 Hz), R196 remainder (other hard-coded 50 Hz where the model frequency is available), R138 nominal part
/// (EMT sources independent of the engine frequency), N187 (a frequency flag for MATPOWER files) and N017 / R269 (the
/// WSCC 9-bus and the Kundur-structured two-area case are 60 Hz systems).</para>
/// </summary>
public sealed class D2NominalFrequencyTests
{
    // WSCC-9 solved, with the Anderson-Fouad X'd per machine (machine base), as the stability tests use it. The power
    // flow does not read FrequencyHz, so one solve serves every frequency copy of the model.
    private static (GridModel Model, PowerFlowResult Pf, double[] Xdp) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = IeeeCases.Wscc9TransientReactancePu[model.Generators[i].Bus];
        return (model, pf, xdp);
    }

    // Simple GENROU-style data synthesised from X'd, the same shape the CLI and the DOC-12 tests use.
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

    private static void SameBracket(CctBracket expected, CctBracket actual)
    {
        Assert.Equal(expected.StableSteps, actual.StableSteps);
        Assert.Equal(expected.UnstableSteps, actual.UnstableSteps);
        Assert.Equal(expected.UnstableAtZero, actual.UnstableAtZero);
        Assert.Equal(expected.NonMonotone, actual.NonMonotone);
        Assert.Equal(expected.StableSeconds, actual.StableSeconds);
    }

    // ---- R238: the CCT searches and the RMS study take the model's nominal frequency ---------------------------------

    /// <summary>R238: with no f0 the classical CCT search integrates at the model's FrequencyHz, bit for bit the same as
    /// passing that value explicitly, at 50 Hz and at 60 Hz.</summary>
    [Theory]
    [InlineData(50.0)]
    [InlineData(60.0)]
    public void R238_classical_cct_defaults_to_the_model_frequency(double hz)
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        GridModel atHz = model.CopyWith(frequencyHz: hz);
        CctBracket byDefault = ClassicalInit.CriticalClearingBracket(atHz, pf, xdp, 7, holdNonMachines: true);
        CctBracket byValue = ClassicalInit.CriticalClearingBracket(atHz, pf, xdp, 7, f0: hz, holdNonMachines: true);
        SameBracket(byValue, byDefault);
        Assert.Equal(byValue.StableSeconds,
            ClassicalInit.CriticalClearingTimeSeconds(atHz, pf, xdp, 7, holdNonMachines: true));
    }

    /// <summary>R238, the physics: the undamped classical swing 2H/ws d2(delta)/dt2 = Pm - Pe is time-scaled by
    /// sqrt(ws), so raising f0 from 50 to 60 Hz shortens every clearing time by the factor sqrt(1.2) = 1.0954 (Kundur,
    /// Power System Stability and Control, 1994, eq. 3.209 form of the swing equation). Exactly so only when the whole
    /// trial is scaled (step and post-clear window x sqrt(1.2)); then the two brackets agree in steps, bit for bit (C6,
    /// see the comment inside). It proves the frequency actually reaches the kernel (a 60 Hz model no longer gets a
    /// 50 Hz CCT).</summary>
    [Fact]
    public void R238_classical_cct_scales_with_the_square_root_of_the_frequency()
    {
        // Moved 2026-10 (C6, final batch; D2 R238, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): the old
        // check compared the 60 Hz CCT with the 50 Hz CCT / sqrt(1.2) within 3 steps, both at the SAME 5 s post-clear window.
        // That is not what the physics scales: the undamped swing (2H / ws) d2(delta)/dt2 = Pm - Pe depends on time only
        // through t * sqrt(ws), so the whole trial (step AND post-clear window) must be scaled, and then the match is exact.
        // At the same 5 s, 60 Hz holds more swings, which catches a late loss at 276-278 steps (N341): measured 275 steps
        // at 60 Hz against 305 / sqrt(1.2) = 278.4, outside the 3-step allowance. Before: |at60.StableSteps - at50.StableSteps
        // / sqrt(1.2)| <= 3 (failed: 275 vs 278.4). After: the 60 Hz bracket (2 ms, 1 s search window, 5 s post-clear) equals,
        // in steps and exactly, the 50 Hz bracket with step 2 ms x sqrt(1.2), search window 1 s x sqrt(1.2) (the same 500
        // steps) and post-clear window 5 s x sqrt(1.2): stable 275, unstable 276, pocket 279, 39 trials in both (captured
        // 2026-10-10 by the FB1 agent; the post-clear window argument is the additive N341 one, default 5 s).
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        double s = Math.Sqrt(1.2);
        CctBracket at60 = ClassicalInit.CriticalClearingBracket(model.CopyWith(frequencyHz: 60.0), pf, xdp, 7, holdNonMachines: true);
        CctBracket scaled50 = ClassicalInit.CriticalClearingBracket(model.CopyWith(frequencyHz: 50.0), pf, xdp, 7,
            stepSeconds: ClassicalInit.CctDefaultStepSeconds * s, maxSeconds: ClassicalInit.CctDefaultMaxSeconds * s,
            holdNonMachines: true, postClearWindowSeconds: ClassicalInit.CctPostClearWindowSeconds * s);
        Assert.False(at60.UnstableAtZero);
        Assert.NotNull(at60.UnstableSteps);   // the default 1 s window holds the first loss of synchronism
        Assert.Equal(at60.StableSteps, scaled50.StableSteps);
        Assert.Equal(at60.UnstableSteps, scaled50.UnstableSteps);
        Assert.Equal(at60.UnstableAtZero, scaled50.UnstableAtZero);
        Assert.Equal(at60.PocketStableSteps, scaled50.PocketStableSteps);
        Assert.Equal(at60.Trials, scaled50.Trials);
        Assert.Equal((275, (int?)276, (int?)279), (at60.StableSteps, at60.UnstableSteps, at60.PocketStableSteps));
    }

    /// <summary>R238: the RMS study and the RMS CCT default to the model's FrequencyHz, bit for bit the explicit value.</summary>
    [Fact]
    public void R238_rms_study_and_cct_default_to_the_model_frequency()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        GridModel at60 = model.CopyWith(frequencyHz: 60.0);
        List<SubtransientInit.MachineData> data = Genrou(at60, xdp);

        MultiMachineSubtransient.Result byDefault = RmsTransientStability.Study(at60, pf, data, 7, 0.1, 0.08,
            stepSeconds: 0.002, durationSeconds: 1.0);
        MultiMachineSubtransient.Result byValue = RmsTransientStability.Study(at60, pf, data, 7, 0.1, 0.08,
            f0: 60.0, stepSeconds: 0.002, durationSeconds: 1.0);
        Assert.Equal(byValue.StayedInSynchronism, byDefault.StayedInSynchronism);
        Assert.Equal(byValue.Machines.Count, byDefault.Machines.Count);
        for (int i = 0; i < byValue.Machines.Count; i++)
            Assert.Equal(byValue.Machines[i].DeltaRad, byDefault.Machines[i].DeltaRad);

        // The explicit 50 Hz override still reaches the kernel: the same study at 50 Hz swings differently.
        MultiMachineSubtransient.Result at50 = RmsTransientStability.Study(at60, pf, data, 7, 0.1, 0.08,
            f0: 50.0, stepSeconds: 0.002, durationSeconds: 1.0);
        Assert.NotEqual(byValue.Machines[0].DeltaRad[^1], at50.Machines[0].DeltaRad[^1]);

        SameBracket(
            RmsTransientStability.CriticalClearingBracket(at60, pf, data, 7, f0: 60.0, scanSteps: 0),
            RmsTransientStability.CriticalClearingBracket(at60, pf, data, 7, scanSteps: 0));
    }

    /// <summary>R238 fail loud: a nominal frequency that is not finite and above 0 Hz, given or taken from the model, is
    /// refused before any trial runs.</summary>
    [Fact]
    public void R238_bad_nominal_frequency_is_refused()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        List<SubtransientInit.MachineData> data = Genrou(model, xdp);
        foreach (double bad in new[] { 0.0, -50.0, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, f0: bad, holdNonMachines: true));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ClassicalInit.CriticalClearingBracket(model.CopyWith(frequencyHz: bad), pf, xdp, 7, holdNonMachines: true));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => RmsTransientStability.Study(model, pf, data, 7, 0.1, 0.05, f0: bad));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => RmsTransientStability.CriticalClearingBracket(model.CopyWith(frequencyHz: bad), pf, data, 7));
        }
    }

    /// <summary>R196 / R238: a converter integrated by the RMS engine takes the model's nominal frequency (its virtual
    /// speed is turned into an angle rate with it); before, every converter assumed the GB 50 Hz.</summary>
    [Theory]
    [InlineData(50.0)]
    [InlineData(60.0)]
    public void R238_rms_converters_take_the_model_frequency(double hz)
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        // Machine 3 becomes a grid-forming converter; the solved state is reused (the power flow reads neither flag).
        Generator[] gens = model.Generators
            .Select(g => g.Bus == 3 ? g with { ConverterInterfaced = true, GridForming = true } : g).ToArray();
        GridModel conv = model.CopyWith(generators: gens, frequencyHz: hz);
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(conv, pf, Genrou(conv, xdp), 7);
        Assert.Single(p.ConverterUnits);
        Assert.Equal(hz, p.ConverterUnits[0].Parameters.NominalHz);
    }

    // ---- R196 remainder: other hard-coded 50 Hz where the model frequency is available --------------------------------

    /// <summary>R196: an inverter's measurement buffer with no history reads as the owner's nominal frequency (the GB
    /// 50 Hz only by default), and a nonsense nominal is refused.</summary>
    [Fact]
    public void R196_empty_measurement_history_reads_the_owner_nominal()
    {
        Assert.Equal(50.0, new GridSim.Core.Assets.MeasurementDelay().Read(1.0, 0.0).hz);
        Assert.Equal(60.0, new GridSim.Core.Assets.MeasurementDelay(60.0).Read(1.0, 0.0).hz);
        foreach (double bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new GridSim.Core.Assets.MeasurementDelay(bad));
    }

    /// <summary>R196: a speed domain's rpm is 120 f / poles at the model's frequency: 3000 rpm for a two-pole machine at
    /// 50 Hz (exactly the old 6000 / poles), 3600 rpm at 60 Hz.</summary>
    [Fact]
    public void R196_speed_domain_rpm_follows_the_model_frequency()
    {
        GridModel model = IeeeCases.Case9();   // blank type strings with H > 0: one two-pole synchronous domain
        SpeedDomain d50 = DomainAssignment.Assign(model.CopyWith(frequencyHz: 50.0)).domains.Single(d => d.Poles == 2);
        SpeedDomain d60 = DomainAssignment.Assign(model.CopyWith(frequencyHz: 60.0)).domains.Single(d => d.Poles == 2);
        Assert.Equal(BitConverter.DoubleToInt64Bits(6000.0 / 2), BitConverter.DoubleToInt64Bits(d50.Rpm));
        Assert.Equal(3600.0, d60.Rpm);
    }

    /// <summary>R196: a real-network impedance scan must run at the model's own f0; a mismatch (the GB default against a
    /// 60 Hz model) is refused instead of mis-scaling every reactance by 5/6.</summary>
    [Fact]
    public void R196_real_network_impedance_scan_refuses_a_foreign_f0()
    {
        var conv = GridSim.Core.Dynamics.Impedance.GridFollowingImpedance.FromParameters(2,
            new GridSim.Core.Dynamics.Impedance.ConverterOperatingPoint(1.0, 0.0, 1.0, 0.2, 1.0, -0.2),
            GridSim.Core.Dynamics.Impedance.ImpedanceModelDetail.PllOuter, GridSim.Core.Diagnostics.ProvenanceTag.CuratedPlaceholder,
            5.0, 0.707, 0.0);
        GridModel net60 = GridSim.Core.Dynamics.Impedance.GridImpedanceAssembler.CanonicalThevenin(3.0, 10.0, 1.0, 100.0, f0Hz: 60.0);
        var opt = new GridSim.Core.Dynamics.Impedance.ImpedanceStabilityOptions
        {
            FMinHz = 0.1, FMaxHz = 250.0, PointsPerDecade = 20, F0Hz = 50.0,
            UseCanonicalThevenin = false, Scr = 3.0, XOverR = 10.0, PRatedPu = 1.0, ScrSteps = 0,
        };
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => GridSim.Core.Dynamics.Impedance.ImpedanceStabilityScan.Run(opt, conv, net60));
        Assert.Contains("differs from the model's nominal frequency", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>R196: the named defaults keep their GB values (bit-identical renames of the 50.0 literals).</summary>
    [Fact]
    public void R196_named_gb_defaults_keep_their_values()
    {
        Assert.Equal(50.0, GridSim.Core.Dynamics.FrequencyLimits.NominalHz);
        Assert.Equal(50.0, new GridSim.Core.Dynamics.DynamicsConfig().NominalHz);
        Assert.Equal(50.0, new GridModel { BaseMva = 100, Buses = Array.Empty<Bus>(), Generators = Array.Empty<Generator>(), Branches = Array.Empty<Branch>() }.FrequencyHz);
        Assert.Equal(50.0, new GridSim.Core.Estimation.PhaseAngleOptions().NominalHz);
        // E = f0 * |dP| / (2 |RoCoF|): 50 * 1000 / (2 * 0.5) = 50000 MW.s with the default nominal.
        Assert.Equal(50000.0, GridSim.Core.Dynamics.InertiaEstimator.EstimateInertiaMwS(1000.0, 0.5));
    }

    // ---- R138 nominal-frequency part: EMT sources take the engine frequency -------------------------------------------

    // The two-bus coupled-line network of ThreePhaseEmtTests, with the source frequency left to the caller.
    private static GridSim.Core.Dynamics.Emt.ThreePhaseNetwork EmtNet(double? sourceHz) => new(
        BusCount: 2,
        Sources: new[] { new GridSim.Core.Dynamics.Emt.ThreePhaseSource(0, 1.0, sourceHz) },
        Branches: new[] { new GridSim.Core.Dynamics.Emt.ThreePhaseBranch(0, 1, 1.0, 1e-3, 0.2, 0.3e-3) },
        Shunts: new[] { new GridSim.Core.Dynamics.Emt.ThreePhaseShunt(1, G: 0.1) },
        Faults: Array.Empty<GridSim.Core.Dynamics.Emt.ThreePhaseFault>());

    private static double[][] EmtRun(double? sourceHz, double engineHz)
        => new GridSim.Core.Dynamics.Emt.ThreePhaseEmtEngine(EmtNet(sourceHz), 20e-6, engineHz).Simulate(0.04, probeBus: 1).Item2;

    private static void SameBits(double[][] a, double[][] b)
    {
        Assert.Equal(a.Length, b.Length);
        for (int k = 0; k < a.Length; k++)
            for (int p = 0; p < a[k].Length; p++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(a[k][p]), BitConverter.DoubleToInt64Bits(b[k][p]));
    }

    /// <summary>R138: a source with no frequency of its own runs at the engine's: at 50 Hz bit for bit the old explicit
    /// 50 Hz source, at 60 Hz bit for bit an explicit 60 Hz source (and no longer a 50 Hz one).</summary>
    [Fact]
    public void R138_default_emt_source_runs_at_the_engine_frequency()
    {
        SameBits(EmtRun(50.0, 50.0), EmtRun(null, 50.0));
        double[][] at60 = EmtRun(null, 60.0);
        SameBits(EmtRun(60.0, 60.0), at60);
        double[][] stale = EmtRun(50.0, 60.0);   // what a default source did in a 60 Hz engine before R138
        Assert.NotEqual(stale[^1][0], at60[^1][0]);
        foreach (double bad in new[] { 0.0, -60.0, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => EmtRun(null, bad));
    }

    // ---- N187: a nominal frequency for MATPOWER files -----------------------------------------------------------------

    private static string LibCase(string stem) =>
        System.IO.Path.Combine(CliProcess.Root(), "data", "matpower", "lib", stem + ".m");

    /// <summary>N187: ParseFile takes the declared frequency; with it the "assumed 50 Hz" note is not raised, without it
    /// the documented assumption stands and the note names the CLI flag.</summary>
    [Fact]
    public void N187_parse_file_takes_a_declared_frequency()
    {
        string path = LibCase("case14");
        Assert.Equal(50.0, GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(path).FrequencyHz);
        Assert.Equal(60.0, GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(path, frequencyHz: 60.0).FrequencyHz);

        string text = System.IO.File.ReadAllText(path);
        var assumed = GridSim.Core.IO.Matpower.MatpowerParser.ParseWithDiagnostics(text, "case14");
        Assert.Contains(assumed.Notes, n => n.StartsWith("nominal frequency not given", StringComparison.Ordinal)
            && n.EndsWith("(gridsim: --frequency-hz 60).", StringComparison.Ordinal));
        var declared = GridSim.Core.IO.Matpower.MatpowerParser.ParseWithDiagnostics(text, "case14", 60.0);
        Assert.DoesNotContain(declared.Notes, n => n.Contains("nominal frequency", StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(path, frequencyHz: 0.0));
    }

    /// <summary>N187 / N017 proof: the nominal frequency enters no power-flow equation, so the same file solved at a
    /// declared 50 Hz and 60 Hz gives the same state, bit for bit (dense and sparse Newton-Raphson).</summary>
    [Theory]
    [InlineData("case9")]
    [InlineData("case14")]
    [InlineData("case118")]
    public void N187_power_flow_does_not_depend_on_the_nominal_frequency(string stem)
    {
        GridModel at50 = GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(LibCase(stem), frequencyHz: 50.0);
        GridModel at60 = GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(LibCase(stem), frequencyHz: 60.0);
        foreach (IPowerFlowSolver solver in new IPowerFlowSolver[] { new NewtonRaphsonPowerFlow(), new SparseNewtonRaphsonPowerFlow() })
        {
            PowerFlowResult a = solver.Solve(at50), b = solver.Solve(at60);
            Assert.True(a.Converged && b.Converged);
            Assert.Equal(a.Iterations, b.Iterations);
            Assert.Equal(a.Vm.Length, b.Vm.Length);
            for (int i = 0; i < a.Vm.Length; i++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Vm[i]), BitConverter.DoubleToInt64Bits(b.Vm[i]));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.VaDeg[i]), BitConverter.DoubleToInt64Bits(b.VaDeg[i]));
            }
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.TotalLossMw), BitConverter.DoubleToInt64Bits(b.TotalLossMw));
        }
    }

    /// <summary>N187, the CLI: --frequency-hz reaches a .m file (the summary line prints it and the assumption note is
    /// gone), says when it reached nothing, and refuses a value that is not a frequency.</summary>
    [Fact]
    public void N187_cli_frequency_flag()
    {
        CliProcess.Result declared = CliProcess.RunBuilt(LibCase("case14"), "--once", "--frequency-hz", "60");
        Assert.True(declared.ExitCode == 0, declared.ToString());
        Assert.Contains(" 60 Hz", declared.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("nominal frequency not given", declared.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("had no effect", declared.StdErr, StringComparison.Ordinal);

        // The '=' form is the same flag, and it may stand before the case.
        CliProcess.Result equalsForm = CliProcess.RunBuilt("--frequency-hz=60", LibCase("case14"), "--once");
        Assert.True(equalsForm.ExitCode == 0, equalsForm.ToString());
        Assert.Contains(" 60 Hz", equalsForm.StdOut, StringComparison.Ordinal);

        // A built-in name is not a .m file load: the flag reaches nothing, and the run says so.
        CliProcess.Result builtIn = CliProcess.RunBuilt("case14", "--once", "--frequency-hz", "60");
        Assert.True(builtIn.ExitCode == 0, builtIn.ToString());
        Assert.Contains("warning: --frequency-hz 60 had no effect", builtIn.StdErr, StringComparison.Ordinal);

        foreach (string bad in new[] { "0", "-60", "NaN", "abc" })
        {
            CliProcess.Result r = CliProcess.RunBuilt(LibCase("case14"), "--once", "--frequency-hz", bad);
            Assert.True(r.ExitCode == 2, r.ToString());
            Assert.Contains("--frequency-hz expected", r.StdErr, StringComparison.Ordinal);
        }
        CliProcess.Result twice = CliProcess.RunBuilt(LibCase("case14"), "--once", "--frequency-hz", "60", "--frequency-hz", "50");
        Assert.True(twice.ExitCode == 2, twice.ToString());
    }

    // ---- N017 / R269 remainder: the WSCC 9-bus and the two-area case are 60 Hz systems --------------------------------

    /// <summary>N017: the built-in WSCC 9-bus and two-area cases carry their published 60 Hz (Anderson and Fouad 2003,
    /// Example 2.6; Kundur 1994, Example 12.6; IEEE PES TR-18 2015).</summary>
    [Fact]
    public void N017_wscc9_and_two_area_are_60_hz()
    {
        Assert.Equal(60.0, IeeeCases.Wscc9NominalFrequencyHz);
        Assert.Equal(60.0, IeeeCases.TwoAreaNominalFrequencyHz);
        Assert.Equal(60.0, IeeeCases.Case9().FrequencyHz);
        Assert.Equal(60.0, IeeeCases.TwoAreaFourMachine().FrequencyHz);
        Assert.DoesNotContain(IeeeCases.Case9().Validate(), d => d.Severity == ValidationSeverity.Error);
    }

    /// <summary>N017 proof that the power flow did not move: the 60 Hz case9 and a 50 Hz copy solve bit for bit alike
    /// (the frequency enters no load-flow equation), and so do the two-area case's copies.</summary>
    [Fact]
    public void N017_power_flow_is_bit_identical_at_50_and_60_hz()
    {
        foreach (GridModel m60 in new[] { IeeeCases.Case9(), IeeeCases.TwoAreaFourMachine() })
        {
            GridModel m50 = m60.CopyWith(frequencyHz: 50.0);
            PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(m50), b = new NewtonRaphsonPowerFlow().Solve(m60);
            Assert.True(a.Converged && b.Converged);
            Assert.Equal(a.Iterations, b.Iterations);
            for (int i = 0; i < a.Vm.Length; i++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Vm[i]), BitConverter.DoubleToInt64Bits(b.Vm[i]));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.VaDeg[i]), BitConverter.DoubleToInt64Bits(b.VaDeg[i]));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.PInjMw[i]), BitConverter.DoubleToInt64Bits(b.PInjMw[i]));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.QInjMvar[i]), BitConverter.DoubleToInt64Bits(b.QInjMvar[i]));
            }
        }
    }

    /// <summary>N017, the modal physics: with no damping the swing-only state matrix is A = [0, ws I; -M^-1 K, 0], so
    /// every eigenvalue scales with sqrt(ws): the WSCC-9 electromechanical modes at 60 Hz are sqrt(1.2) = 1.0954 times
    /// their 50 Hz values (the same K and M; Kundur 1994 eq. 12.x form). This is how every case9 modal pin moves.</summary>
    [Fact]
    public void N017_wscc9_modal_frequencies_scale_by_the_square_root_of_1_2()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        var m50 = GridSim.Core.Dynamics.SmallSignal.SmallSignalAssembler.FromSolvedCase(model.CopyWith(frequencyHz: 50.0), pf, xdp);
        var m60 = GridSim.Core.Dynamics.SmallSignal.SmallSignalAssembler.FromSolvedCase(model, pf, xdp);
        double[] f50 = GridSim.Core.Dynamics.SmallSignal.ModalAnalysis.Analyze(m50).Modes
            .Where(x => x.IsOscillatory && x.Eigenvalue.Imaginary > 0).Select(x => x.FrequencyHz).OrderBy(x => x).ToArray();
        double[] f60 = GridSim.Core.Dynamics.SmallSignal.ModalAnalysis.Analyze(m60).Modes
            .Where(x => x.IsOscillatory && x.Eigenvalue.Imaginary > 0).Select(x => x.FrequencyHz).OrderBy(x => x).ToArray();
        Assert.Equal(2, f50.Length);   // three machines: two electromechanical modes plus the rigid COI pair
        Assert.Equal(f50.Length, f60.Length);
        for (int i = 0; i < f50.Length; i++)
            Assert.True(Math.Abs(f60[i] / f50[i] - Math.Sqrt(1.2)) < 1e-9,
                $"mode {i}: {f60[i]:R} Hz at 60 Hz vs {f50[i]:R} Hz at 50 Hz, ratio {f60[i] / f50[i]:R}");
    }

    /// <summary>N017 / D2 protection: the G99 81U / 81O pickups are for a 50 Hz system, so on the 60 Hz WSCC-9 steady state
    /// they report not-checkable (naming both frequencies) instead of operating 81O at "60.000 Hz above 52.0 Hz";
    /// settings declared for 60 Hz are evaluated; a 50 Hz model is judged exactly as before.</summary>
    [Fact]
    public void N017_frequency_relays_refuse_settings_for_another_nominal()
    {
        GridModel case9 = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(case9);
        GridSim.Core.Protection.ProtectionReport rep = GridSim.Core.Protection.ProtectionAssessment.Assess(case9, pf);
        foreach (string device in new[] { "81U", "81O" })
        {
            GridSim.Core.Protection.RelayVerdict v = rep.Verdicts.Single(x => x.Device == device);
            Assert.Equal(GridSim.Core.Protection.ProtectionConfidence.NotCheckable, v.Confidence);
            Assert.False(v.Operate);
            Assert.Contains("written for a 50 Hz system and this system is 60 Hz", v.Basis, StringComparison.Ordinal);
        }

        var for60 = new GridSim.Core.Protection.ProtectionSettings { UnderFrequencyHz = 57.0, OverFrequencyHz = 61.8, SettingsNominalHz = 60.0 };
        GridSim.Core.Protection.ProtectionReport rep60 = GridSim.Core.Protection.ProtectionAssessment.Assess(case9, pf, for60);
        GridSim.Core.Protection.RelayVerdict of60 = rep60.Verdicts.Single(x => x.Device == "81O");
        Assert.Equal(GridSim.Core.Protection.ProtectionConfidence.Grounded, of60.Confidence);
        Assert.False(of60.Operate);
        Assert.Equal(60.0, of60.Quantity);

        GridModel case9At50 = case9.CopyWith(frequencyHz: 50.0);
        GridSim.Core.Protection.RelayVerdict of50 = GridSim.Core.Protection.ProtectionAssessment.Assess(case9At50, pf)
            .Verdicts.Single(x => x.Device == "81O");
        Assert.Equal(GridSim.Core.Protection.ProtectionConfidence.Grounded, of50.Confidence);
        Assert.Equal("operating frequency 50.000 Hz (<52.0 Hz)", of50.Basis);

        // The trajectory path takes the run's nominal the same way (the trip verb passes its config's NominalHz).
        var traj = new GridSim.Core.Protection.ProtectionTrajectory(0.3, 59.6, 60.0, double.NaN);
        GridSim.Core.Protection.ProtectionReport tr = GridSim.Core.Protection.ProtectionAssessment.AssessTrajectory(traj, null, 60.0);
        Assert.Equal(GridSim.Core.Protection.ProtectionConfidence.NotCheckable, tr.Verdicts.Single(x => x.Device == "81O").Confidence);
        Assert.Equal(GridSim.Core.Protection.ProtectionConfidence.Grounded, tr.Verdicts.Single(x => x.Device == "81R").Confidence);
        Assert.Throws<ArgumentOutOfRangeException>(() => GridSim.Core.Protection.ProtectionAssessment.AssessTrajectory(traj, null, double.NaN));
    }

    /// <summary>R196 / N017, the trip physics: RoCoF = f0 * deltaP / (2E), so the same loss on the same inertia gives a
    /// peak RoCoF 1.2 times larger at 60 Hz (the post-trip spike is the first step, before any response acts), and the run
    /// starts at its own nominal.</summary>
    [Fact]
    public void R196_trip_rocof_scales_with_the_nominal_frequency()
    {
        GridModel case9 = IeeeCases.Case9();
        double inertia = case9.SystemInertiaGvaS * 1000.0, demand = case9.Buses.Sum(b => b.Pd);
        var ev = new[] { new GridSim.Core.Dynamics.FrequencyEvent(1.0, -85.0) };
        GridSim.Core.Dynamics.FrequencyResponse at50 = GridSim.Core.Dynamics.FrequencyDynamics.Simulate(inertia, demand, ev,
            new GridSim.Core.Dynamics.DynamicsConfig { NominalHz = 50.0 });
        GridSim.Core.Dynamics.FrequencyResponse at60 = GridSim.Core.Dynamics.FrequencyDynamics.Simulate(inertia, demand, ev,
            new GridSim.Core.Dynamics.DynamicsConfig { NominalHz = 60.0 });
        Assert.True(Math.Abs(at60.InitialRocofHzPerS / at50.InitialRocofHzPerS - 1.2) < 1e-12,
            $"peak RoCoF {at60.InitialRocofHzPerS:R} at 60 Hz vs {at50.InitialRocofHzPerS:R} at 50 Hz");
        Assert.Equal(60.0, at60.Points[0].Hz);
        Assert.Equal(50.0, at50.Points[0].Hz);
    }

    /// <summary>N017, the CLI: `trip case9` runs at 60 Hz and reports 81U / 81O as not evaluated (G99's 50 Hz pickups do
    /// not apply), with the exit code unchanged.</summary>
    [Fact]
    public void N017_cli_trip_on_case9_runs_at_60_hz()
    {
        CliProcess.Result r = CliProcess.RunBuilt("trip", "case9");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("81U  Underfrequency       not evaluated: the 47.5 Hz pickup is written for a 50 Hz system and this system is 60 Hz",
            r.StdOut, StringComparison.Ordinal);
        Assert.Contains("81O  Overfrequency        not evaluated: the 52.0 Hz pickup is written for a 50 Hz system", r.StdOut, StringComparison.Ordinal);
    }
}
