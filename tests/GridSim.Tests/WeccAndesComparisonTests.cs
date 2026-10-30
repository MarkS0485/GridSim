using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the WECC REGC_A + REEC_A chain against ANDES 2.0.0 (the open-source reference implementation of REGCA1 and
/// REECA1) on one small network both tools can represent: an infinite bus (bus 1) - line r 0.01 x 0.15 - bus 3 - line
/// r 0.01 x 0.15 - bus 2, the converter (0.5 pu at 1.0 pu, MBASE 100 MVA), and a fault (xf 0.05) on bus 3 from 1.0 s to 1.1 s.
/// GridSim's infinite bus is a 1,000,000 MVA machine with H = 10 s (X'' = 2e-5 pu on the system base); ANDES's is its Slack with
/// no dynamic model. The ANDES numbers are recorded from its run (script and exact differences in the WECC expansion records);
/// this test computes GridSim's run and checks it against them.
/// </summary>
public class WeccAndesComparisonTests
{
    internal static GridModel Case()
    {
        const string text = "function mpc = andescmp\nmpc.version = '2';\nmpc.baseMVA = 100;\nmpc.bus = [\n"
            + "1 3 0 0 0 0 1 1.0 0 110 1 1.5 0.5;\n2 2 0 0 0 0 1 1.0 0 110 1 1.5 0.5;\n3 1 0 0 0 0 1 1.0 0 110 1 1.5 0.5;\n];\n"
            + "mpc.gen = [\n1 0 0 9999 -9999 1.0 1000000 1 9999 -9999;\n2 50 0 999 -999 1.0 100 1 999 -999;\n];\n"
            + "mpc.branch = [\n1 3 0.01 0.15 0 0 0 0 0 0 1 -360 360;\n3 2 0.01 0.15 0 0 0 0 0 0 1 -360 360;\n];\n";
        GridModel m = MatpowerParser.Parse(text, "andes-cmp", 60.0);
        return m.CopyWith(generators: m.Generators.Select(g => g.Bus == 1 ? g with { MBase = 1e6, H = 10.0 } : g with { MBase = 100.0 }).ToList());
    }

    internal static WeccPlant Plant(string flags)
    {
        var regc = new RegcaParameters { Tg = 0.02, Rrpwr = 10.0, Brkpt = 0.9, Zerox = 0.4, Lvplsw = 0, Lvpl1 = 1.22, Volim = 1.2, Lvpnt1 = 0.8,
            Lvpnt0 = 0.4, Iolim = -1.3, Tfltr = 0.02, Khv = 0.7, Iqrmax = 99.0, Iqrmin = -99.0, Accel = 0.0 };
        double trv = flags.EndsWith("_trv0", StringComparison.Ordinal) ? 0.0 : 0.02;
        (int pf, int vf, int qf) = flags.Replace("_trv0", "", StringComparison.Ordinal) switch { "gfl_q" => (0, 0, 0), "gfl_pf" => (1, 0, 0), _ => throw new ArgumentException(flags) };
        var reec = new ReecParameters { PfFlag = pf, VFlag = vf, QFlag = qf, PFlag = 0, PqFlag = 0, Vdip = 0.9, Vup = 1.1, Trv = trv, Dbd1 = -0.05,
            Dbd2 = 0.05, Kqv = 2.0, Iqh1 = 1.1, Iql1 = -1.1, Vref0 = 1.0, Iqfrz = 0.0, Thld = 0.0, Thld2 = 0.0, Tp = 0.02, Qmax = 0.5, Qmin = -0.5,
            Vmax = 999.0, Vmin = -999.0, Kqp = 1.0, Kqi = 5.0, Kvp = 1.0, Kvi = 20.0, Vref1 = 1.0, Tiq = 0.02, DPmax = 99.0, DPmin = -99.0, Pmax = 1.0,
            Pmin = 0.0, Imax = 1.2, Tpord = 0.02,
            Vq = new[] { 0.2, 0.4, 0.8, 1.0 }, Iq = new[] { 1.1, 1.1, 1.1, 1.1 }, Vp = new[] { 0.2, 0.4, 0.8, 1.0 }, Ip = new[] { 1.1, 1.1, 1.1, 1.1 } };
        return new WeccPlant(regc, reec);
    }

    internal static RmsConverterTrace Run(string flags, double dt, out int failures)
    {
        GridModel m = Case();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        double v3 = pf.Vm[pf.BusIds.ToList().IndexOf(3)];
        double q = v3 * v3 / 0.05 * 100.0;   // a shunt reactance of exactly 0.05 pu
        var events = new RmsEvent[] { new RmsLoadStep(1.0, 3, 0.0, q), new RmsLoadStep(1.1, 3, 0.0, -q) };
        var data = m.Generators.Select(g => g.Bus == 1
            ? new SubtransientInit.MachineData(10.0, 0.0, 1.8, 0.3, 0.2, 1.7, 0.5, 0.2, 7.0, 0.03, 0.5, 0.05)
            : new SubtransientInit.MachineData(1.0, 0.0, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2, 7.0, 0.03, 0.5, 0.05) { Renewable = Plant(flags) }).ToList();
        MultiMachineSubtransient.Result r = RmsTransientStability.StudyEvents(m, pf, data, events, stepSeconds: dt, durationSeconds: 4.0);
        failures = r.ConverterIterationFailures;
        return r.Converters![0];
    }

    // ANDES 2.0.0 (REGCA1 + REECA1, fixed step 1 ms, power-flow tolerance 1e-12) at GridSim's times: t, V2, P, Q (system pu). ANDES
    // takes a 1e-4 s sub-step at each event; its post-event sample is placed at the event time and the trace interpolated
    // linearly onto the 1 ms grid. Script and full differences: WECC expansion records (DECISIONS D39).
    private static readonly Dictionary<string, (double T, double V, double P, double Q)[]> Andes = new()
    {
        ["gfl_q"] = new[]
        {
            (0.5, 1.0, 0.5, 0.004336137893161227),
            (1.05, 0.4127114329333872, 0.008777955765324873, 0.3584001977126064),
            (1.099, 0.442923079228448, 0.028592134447906977, 0.4547818069312141),
            (1.2, 1.0034266833430427, 0.4948288784742646, 0.015127523289087126),
            (1.5, 1.0000000005196095, 0.499999990383066, 0.004336138759140449),
            (3.0, 1.0000000000401852, 0.499999997963647, 0.004336137903843354),
            (4.0, 1.0000000000401852, 0.499999997963647, 0.004336137903843354),
        },
        ["gfl_pf"] = new[]
        {
            (0.5, 1.0, 0.5, 0.004336137893161227),
            (1.05, 0.4127114329333872, 0.008777955765324873, 0.3584001977126064),
            (1.099, 0.442923079228448, 0.028592134447906977, 0.4547818069312141),
            (1.2, 1.003391337511834, 0.49482665981742957, 0.015011180640322037),
            (1.5, 0.999999994899972, 0.4999999986194458, 0.0043361211412523935),
            (3.0, 0.9999999987081472, 0.5000000004402698, 0.0043361339218187306),
            (4.0, 0.9999999987081472, 0.5000000004402698, 0.0043361339218187306),
        },
        ["gfl_q_trv0"] = new[]
        {
            (0.5, 1.0, 0.5, 0.004336137893161227),
            (1.05, 0.4337478882927341, 0.019905650464279397, 0.42460618533074174),
            (1.099, 0.4415277722560426, 0.028412168534784045, 0.4500695593147653),
            (1.2, 1.0015612944342767, 0.49786182304586335, 0.009259302268154107),
            (1.5, 1.0000000001640084, 0.4999999957624826, 0.004336138000976748),
            (3.0, 1.0000000000633464, 0.4999999979680493, 0.004336138000976748),
            (4.0, 1.0000000000633464, 0.4999999979680493, 0.004336138000976748),
        },
    };

    [Theory]
    [InlineData("gfl_q")]
    [InlineData("gfl_pf")]
    [InlineData("gfl_q_trv0")]
    public void Regc_a_reec_a_agrees_with_andes_within_the_measured_differences(string flags)
    {
        // Bounds per window, from the measured differences (records, D39), not loosened to pass:
        //  - at rest (0.5 s): 1e-9 in V and P, 1e-7 in Q (ANDES's own tolerances);
        //  - in the fault (1.05, 1.099 s): 1e-3 in V and P, 2e-3 in Q (implicit trapezoid against GridSim's explicit step);
        //  - 100 ms after clearing (1.2 s): 5e-4 with Trv = 0; 3e-3 in P with Trv = 0.02, where ANDES divides Pord by the RAW
        //    voltage and GridSim, as the EPRI figure and OpenIPSL, by Vt_filt (the difference is 0.184 pu at 1.115 s and
        //    vanishes with Trv = 0), and 1e-3 in Q (power-factor control carries that P difference into Q: 6.2e-4 measured);
        //  - settled (1.5, 3, 4 s): 2e-5 in V (GridSim's infinite bus is a 1,000,000 MVA machine, ANDES's an ideal source),
        //    1e-7 in P and Q.
        const double dt = 0.001;
        RmsConverterTrace c = Run(flags, dt, out int failures);
        Assert.Equal(0, failures);
        bool trv0 = flags.EndsWith("_trv0", StringComparison.Ordinal);
        foreach ((double t, double v, double p, double q) in Andes[flags])
        {
            int k = (int)Math.Round(t / dt);
            (double tv, double tp, double tq) = t switch
            {
                0.5 => (1e-9, 1e-9, 1e-7),
                1.05 or 1.099 => (1e-3, 1e-3, 2e-3),
                1.2 => trv0 ? (5e-4, 5e-4, 5e-4) : (5e-4, 3e-3, 1e-3),
                _ => (2e-5, 1e-7, 1e-7),
            };
            string at = FormattableString.Invariant($"{flags} t={t}: GridSim V {c.TerminalVoltagePu[k]:R} P {c.ActivePowerPu[k]:R} Q {c.ReactivePowerPu[k]:R}; ANDES V {v:R} P {p:R} Q {q:R}");
            Assert.True(Math.Abs(c.TerminalVoltagePu[k] - v) <= tv, at);
            Assert.True(Math.Abs(c.ActivePowerPu[k] - p) <= tp, at);
            Assert.True(Math.Abs(c.ReactivePowerPu[k] - q) <= tq, at);
        }
    }
}
