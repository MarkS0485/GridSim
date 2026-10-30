using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// FB1 exact pins (final-batch follow-ups, October 2026), new tests only.
///
/// <para>1. The 60 Hz WSCC 9-bus. D2 (N017) made case9 the WSCC system's 60 Hz and kept the existing CCT, modal and EMT pins
/// on the 50 Hz copy of the network (they pin the algorithms on the network their numbers were captured on), so the 60 Hz
/// case9 was unpinned on those paths; D2's notes asked for these pins at the batch. Every value below was captured by
/// running this code at 1.95.259 (Windows, Release) and is asserted exactly: the classical CCT bracket at every bus (the
/// CLI screen's setup: Anderson-Fouad X'd, non-machines held, 2 ms, 1 s search window, 5 s post-clear window), the RMS
/// CCT bracket at the generator buses and bus 7 (the CLI's synthesised GENROU data, no AVR, as RmsCctStepQuantisedTests
/// uses at 50 Hz), the two case9 modal digests of N186ParticipationFixtures and the three case9 EMT digests of
/// EmtUntappedBitIdentityTests, each with the same procedure at 60 Hz.</para>
///
/// <para>2. <c>acopf gb-synthetic</c> (LF7 N316). Since N316 the name is the shipped data/gb-synthetic folder; LF7 left the
/// folder's figures to be captured. Captured: exactly the builder's figures (6 passes, losses 1235.507 MW, cost 675,277
/// GBP/h, 5 rated lines left out at the final pass), byte for byte the same as <c>acopf data/gb-synthetic</c>.</para>
/// </summary>
public sealed partial class Fb1FinalBatchFollowUpsTests
{
    // ---- 60 Hz case9: classical CCT per bus ------------------------------------------------------------------------

    /// <summary>The classical CCT bracket of WSCC-9 at 60 Hz for a fault at every bus, exactly: stable and unstable step
    /// counts (2 ms grid), the stable pocket above the first loss when the N048 scan saw one, and the reported seconds
    /// (StableSteps x 0.002, as computed). Bus 5 no longer survives the whole 1 s window at 60 Hz (at 50 Hz it did: 500).
    /// D2's estimates (old continuous value / sqrt(1.2)) were 251 / 156 / 151 / 236 / 152 / 278 / 162 / 347 at buses 1-4,
    /// 6-9; the measured values differ where the fixed 5 s window catches a later swing (N341).</summary>
    [Theory]
    [InlineData(1, 249, 251, 0.498)]
    [InlineData(2, 156, 160, 0.312)]
    [InlineData(3, 151, -1, 0.302)]
    [InlineData(4, 235, -1, 0.47000000000000003)]
    [InlineData(5, 478, 482, 0.9560000000000001)]
    [InlineData(6, 152, -1, 0.304)]
    [InlineData(7, 275, 279, 0.55)]
    [InlineData(8, 159, -1, 0.318)]
    [InlineData(9, 333, 347, 0.666)]
    public void Pin_case9_at_60_hz_classical_cct_per_bus(int bus, int stableSteps, int pocketSteps, double stableSeconds)
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9At60();
        CctBracket b = ClassicalInit.CriticalClearingBracket(model, pf, xdp, bus, holdNonMachines: true);
        Assert.False(b.UnstableAtZero);
        Assert.Equal(stableSteps, b.StableSteps);
        Assert.Equal(stableSteps + 1, b.UnstableSteps);
        Assert.Equal(pocketSteps < 0 ? (int?)null : pocketSteps, b.PocketStableSteps);
        Assert.Equal(16, b.ScanSteps);
        Assert.Equal(39, b.Trials);
        Assert.Equal(BitConverter.DoubleToInt64Bits(stableSeconds), BitConverter.DoubleToInt64Bits(b.StableSeconds));

        // The CLI screen's path (one FaultScreen for all buses, R007) gives the same bracket, bit for bit.
        ClassicalInit.FaultScreen screen = ClassicalInit.PrepareFaultScreen(model, pf, xdp, holdNonMachines: true);
        Assert.Equal(b, ClassicalInit.CriticalClearingBracket(screen, bus));
    }

    // ---- 60 Hz case9: RMS CCT --------------------------------------------------------------------------------------

    // The CLI's synthesised GENROU data off the Anderson-Fouad X'd (the CliDefaults ratios, as RmsCctStepQuantisedTests).
    private static List<SubtransientInit.MachineData> CliGenrou(GridModel model)
    {
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
        {
            double x = IeeeCases.Wscc9TransientReactancePu[gen.Bus];
            data.Add(new SubtransientInit.MachineData(H: gen.H, Damping: 0.0,
                Xd: 3.0 * x, Xdp: x, Xdpp: 0.8 * x, Xq: 2.5 * x, Xqp: 1.5 * x, Xqpp: 0.8 * x,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05));
        }
        return data;
    }

    /// <summary>The RMS CCT bracket of WSCC-9 at 60 Hz, exactly. Bus 7 (the CLI's default fault bus for case9): 277 steps
    /// (0.554 s), unstable at 278 (D2 estimated 275-276; 302 at 50 Hz). The generator buses too: 242 / 163 / 146.</summary>
    [Theory]
    [InlineData(1, 242, 0.484)]
    [InlineData(2, 163, 0.326)]
    [InlineData(3, 146, 0.292)]
    [InlineData(7, 277, 0.554)]
    public void Pin_case9_at_60_hz_rms_cct(int bus, int stableSteps, double stableSeconds)
    {
        (GridModel model, PowerFlowResult pf, _) = Case9At60();
        CctBracket b = RmsTransientStability.CriticalClearingBracket(model, pf, CliGenrou(model), bus);
        Assert.False(b.UnstableAtZero);
        Assert.Equal(stableSteps, b.StableSteps);
        Assert.Equal(stableSteps + 1, b.UnstableSteps);
        Assert.Null(b.PocketStableSteps);
        Assert.Equal(39, b.Trials);
        Assert.Equal(BitConverter.DoubleToInt64Bits(stableSeconds), BitConverter.DoubleToInt64Bits(b.StableSeconds));
    }

    // ---- 60 Hz case9: modal digests --------------------------------------------------------------------------------

    /// <summary>N186's modal digest procedure (SHA-256 over the raw bits of the RHP count, the minimum damping ratio, the
    /// participation flag and every mode's eigenvalue, damping, frequency, oscillatory flag and participation factors) on
    /// case9 at its own 60 Hz, undamped and with D = 2 on every machine. The modes are 1.1073672160731265 Hz and
    /// 1.989035856686255 Hz undamped (D2 estimated about 1.1074 / 1.9890 Hz: the 50 Hz 1.0109 / 1.8157 x sqrt(1.2)).</summary>
    [Theory]
    [InlineData(false, "FFB387F7D544584423C7F71AFA085B3D1EDFCA4812C80B7E6C27CF42287F0101")]
    [InlineData(true, "E84C855F4F61BCF2919D28AE2172DEBF107EAA0A02313ECE4998316AB90F35DE")]
    public void Pin_case9_at_60_hz_modal_digest(bool damped, string expected)
    {
        (GridModel model, PowerFlowResult pf, _) = Case9At60();
        double[]? d = damped ? Enumerable.Repeat(2.0, model.Generators.Count).ToArray() : null;
        ModalResult r = ModalAnalysis.Analyze(SmallSignalAssembler.FromSolvedCase(model, pf, N186ParticipationFixtures.Wscc9Xdp, d));
        var bytes = new List<byte>();
        void D(double x) => bytes.AddRange(BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(x)));
        void C(Complex z) { D(z.Real); D(z.Imaginary); }
        D(r.RhpEigenvalueCount);
        D(r.MinDampingRatio);
        D(r.ParticipationUnavailable ? 1 : 0);
        foreach (Mode m in r.Modes)
        {
            C(m.Eigenvalue);
            D(m.DampingRatio);
            D(m.FrequencyHz);
            D(m.IsOscillatory ? 1 : 0);
            foreach (double x in m.ParticipationByState) D(x);
        }
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(bytes.ToArray())));
        if (!damped)
        {
            double[] hz = r.Modes.Where(m => m.IsOscillatory).Select(m => m.FrequencyHz).Distinct().OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 1.1073672160731265, 1.989035856686255 }, hz);
        }
    }

    // ---- 60 Hz case9: EMT digests ----------------------------------------------------------------------------------

    /// <summary>EmtUntappedBitIdentityTests' trajectory digest (DC source at the first generator bus, loads on, 20 us,
    /// 40 ms; lumped, travelling-wave lines, and a bus fault 10-20 ms) on case9 at its own 60 Hz.</summary>
    [Theory]
    [InlineData("lumped", "EE340378AF0F941BD04EFA8B5775803B059173DCD5E1C032268FE023EC856552")]
    [InlineData("lines", "E62C4E60DFAD1488773AD9FEB86FB396D56DF151B0E81ACAAB2093915286AA5C")]
    [InlineData("fault", "C726F916DFE3BAE79E6D51B248B921F590AC4CDEE7607ED8FD3F887E6A11E298")]
    public void Pin_case9_at_60_hz_emt_digest(string mode, string expected)
    {
        GridModel model = IeeeCases.Case9();
        Assert.Equal(60.0, model.FrequencyHz);
        Assert.Equal(expected, EmtUntappedBitIdentityTests.TrajectoryDigest(model, mode));
    }

    // ---- acopf gb-synthetic: exact lines ---------------------------------------------------------------------------

    /// <summary>LF7 N316: <c>acopf gb-synthetic</c> (the shipped folder) prints exactly these lines, exit 0, and the run by
    /// the folder's path prints the same stdout byte for byte.</summary>
    [Fact]
    public void Pin_acopf_gb_synthetic_exact_lines()
    {
        CliProcess.Result r = CliProcess.RunBuilt("acopf", "gb-synthetic");
        Assert.Equal(0, r.ExitCode);
        string[] expected =
        {
            "ACOPF (DC dispatch + AC loss loop) - GB synthetic (2227 buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]",
            "  the final DC pass left out 5 rated line(s) no dispatch can bring within rating (still overloaded; 'gridsim dcopf' lists them):",
            "      branch 120   MID SGT 400/275 1            rating 1,000.0 MW, any dispatch gives 1,454.5 .. 1,491.4 MW",
            "      branch 121   MID SGT 400/275 2            rating 1,000.0 MW, any dispatch gives 1,020.7 .. 1,037.6 MW",
            "      branch 125   LDN SGT 400/275 1            rating 1,000.0 MW, any dispatch gives 1,313.4 .. 1,813.0 MW",
            "      branch 817   YRK G8P4 132/33              rating 180.0 MW, any dispatch gives 180.1 .. 180.1 MW",
            "      branch 1082  MID G2P1S3 33/11             rating 30.0 MW, any dispatch gives 30.0 .. 30.0 MW",
            "  converged in 6 iteration(s)  losses 1235.507 MW  cost 675,277 GBP/h",
            "      gen bus 45      8,000.0 MW  @     1 GBP/MWh",
            "      gen bus 4       4,000.0 MW  @     1 GBP/MWh",
            "      gen bus 60      4,000.0 MW  @     1 GBP/MWh",
            "      gen bus 62      3,200.0 MW  @    10 GBP/MWh",
            "      gen bus 2       3,000.0 MW  @     1 GBP/MWh",
            "      gen bus 19      2,600.0 MW  @    55 GBP/MWh",
            "      gen bus 20      2,434.2 MW  @    65 GBP/MWh",
            "      gen bus 10      2,400.0 MW  @    10 GBP/MWh",
            "      gen bus 27      2,000.0 MW  @     1 GBP/MWh",
            "      gen bus 47      2,000.0 MW  @    60 GBP/MWh",
            "      gen bus 1       1,300.0 MW  @     5 GBP/MWh",
            "      gen bus 46      1,250.0 MW  @    10 GBP/MWh",
        };
        string[] lines = r.StdOut.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Assert.Equal(expected, lines);
        Assert.DoesNotContain("no later pass can be feasible", r.StdErr, StringComparison.Ordinal);

        CliProcess.Result byFolder = CliProcess.RunBuilt("acopf",
            Path.Combine(Path.GetDirectoryName(CliProcess.BuiltCliDll())!, "data", "gb-synthetic"));
        Assert.Equal(0, byFolder.ExitCode);
        Assert.Equal(r.StdOut, byFolder.StdOut);
    }
}
