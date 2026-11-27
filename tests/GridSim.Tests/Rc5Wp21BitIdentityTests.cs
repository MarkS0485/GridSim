using System.Security.Cryptography;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP21: the step schedule leaves every existing full-model run unchanged, and a one-window schedule is the plain
/// fixed-step run, bit for bit.
///
/// <para><b>How the pins were taken.</b> Before any WP21 edit, a scratch console program referenced the 2.1.122 Release
/// GridSim.Core.dll (built from 3ba7ebdd; file SHA-256 12357119E413499197C6C7EE0559D0AD9EFCA11103CABC6AF7971BCA55F2C20B) and
/// ran exactly the four runs below (the same case builders and digest code as this file), printing each digest. The
/// digests cover every sample's time and the six machine states of every machine, the verdict and the converter counter;
/// the observed digests add every bus voltage sample.</para>
/// <para>The plain runs must still give those bits (the kernel's new schedule branch is guarded, and the plain hook is
/// unchanged). The one-window schedules must give them too: their sample times are <c>0 + k dt</c> = <c>k dt</c>, their step
/// is the same dt, and the step-sized governor hook evaluates the same expressions as the fixed-step hook.</para>
/// </summary>
public sealed class Rc5Wp21BitIdentityTests
{
    private const string SmibEvents1ms = "56E1C07270A5FFA37E515825893F8526754168938326E77FA48342112F7B6141";
    private const string SmibObserved1ms = "93BEA97A91DFA04CE63BA017E720798D5FAEC5E68B920ECBA7859F8C403B2AF2";
    private const string Case9GovExcObserved5ms = "DE66E1E3F44FFE627372CCF216D876356EAB1BACF310CB8B4D077461A6525A6D";
    private const string Case9GovNoEvent5ms = "F182E86E3911F604E3195BD8E3FD83DDE0403DA2AAD3F6455D97E5E758D519C8";

    private const double X1 = 0.25, XLine = 0.5, XInf = 0.01, X0Machine = 0.08;

    /// <summary>The WP2 single machine against a stiff source (Rc5Wp2RmsSequenceFaultTests.Smib, same data).</summary>
    internal static (GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) Smib()
    {
        var m = new GridModel
        {
            Name = "smib",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Pv, BaseKv: 132), new Bus(2, BusType.Slack, BaseKv: 132) },
            Generators = new[]
            {
                new Generator(1, Pg: 100, Vg: 1.0, MBase: 100, H: 3.5, Name: "machine", SubtransientXdPu: X1)
                    { ZeroSequenceZPu = new MachineZeroSequence(0.0, X0Machine) },
                new Generator(2, Pg: 0, Vg: 1.0, MBase: 100, H: 1e4, Name: "infinite", SubtransientXdPu: XInf),
            },
            Branches = new[] { new Branch(1, 2, 0.0, XLine, Name: "line") },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12 });
        Assert.True(pf.Converged);
        var data = new List<SubtransientInit.MachineData>
        {
            new(H: 3.5, Damping: 0.0, Xd: 1.8, Xdp: 0.30, Xdpp: X1, Xq: 1.7, Xqp: 0.55, Xqpp: X1, Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05),
            new(H: 1e4, Damping: 0.0, Xd: XInf, Xdp: XInf, Xdpp: XInf, Xq: XInf, Xqp: XInf, Xqpp: XInf, Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05),
        };
        return (m, pf, data);
    }

    internal static RmsEvent[] SmibFault() => new RmsEvent[] { new RmsBusFault(0.1, 1), new RmsFaultClear(0.2, 1) };

    internal static string Digest(MultiMachineSubtransient.Result r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(r.StayedInSynchronism);
            w.Write(r.ConverterIterationFailures);
            w.Write(r.Machines.Count);
            foreach (MultiMachineSubtransient.MachineTrace mt in r.Machines)
            {
                w.Write(mt.TimeSeconds.Count);
                for (int k = 0; k < mt.TimeSeconds.Count; k++)
                {
                    w.Write(BitConverter.DoubleToInt64Bits(mt.TimeSeconds[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(mt.DeltaRad[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(mt.SpeedPu[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(mt.EqpPu[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(mt.EdpPu[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(mt.EqppPu[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(mt.EddpPu[k]));
                }
            }
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    internal static string Digest(RmsTransientStability.ObservedStudy o)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Digest(o.Result));
            w.Write(o.Buses.TimeSeconds.Count);
            foreach (double t in o.Buses.TimeSeconds) w.Write(BitConverter.DoubleToInt64Bits(t));
            for (int i = 0; i < o.Buses.BusIds.Count; i++)
            {
                foreach (double v in o.Buses.MagnitudePu[i]) w.Write(BitConverter.DoubleToInt64Bits(v));
                foreach (double v in o.Buses.AngleRad[i]) w.Write(BitConverter.DoubleToInt64Bits(v));
            }
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    internal static IReadOnlyList<SubtransientInit.MachineData> Genrou(GridModel model, GovernorData? gov) =>
        model.Generators.Select(g => new SubtransientInit.MachineData(H: g.H, Damping: 0.0, Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65,
            Xqp: 0.40, Xqpp: 0.22, Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05, Governor: gov)).ToList();

    internal static int BranchBetween(GridModel m, int a, int b)
    {
        for (int i = 0; i < m.Branches.Count; i++)
            if ((m.Branches[i].From == a && m.Branches[i].To == b) || (m.Branches[i].From == b && m.Branches[i].To == a)) return i;
        throw new InvalidOperationException($"no branch {a}-{b}");
    }

    private static (GridModel, PowerFlowResult, RmsEvent[]) Case9()
    {
        GridModel c9 = IeeeCases.Case9();
        PowerFlowResult pf9 = new NewtonRaphsonPowerFlow().Solve(c9);
        Assert.True(pf9.Converged);
        return (c9, pf9, new RmsEvent[] { new RmsBusFault(0.1, 7), new RmsFaultClear(0.18, 7), new RmsBranchTrip(0.18, BranchBetween(c9, 7, 8)) });
    }

    private static List<RmsExciter?> Sexs(GridModel m) => m.Generators.Select(_ => (RmsExciter?)RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0)).ToList();

    private static void Pin(string expected, string actual) => Assert.True(expected == actual, "pinned " + expected + ", got " + actual);

    [Fact]
    public void Smib_plain_event_run_keeps_the_pinned_bits()
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Smib();
        Pin(SmibEvents1ms, Digest(RmsTransientStability.StudyEvents(m, pf, data, SmibFault(), stepSeconds: 0.001, durationSeconds: 1.0)));
    }

    [Fact]
    public void Smib_one_window_schedule_is_the_plain_run_bit_for_bit()
    {
        // Spec test 6: a one-window schedule at dt = 0.001 is the plain fixed-step run (WP2 SMIB fault case).
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Smib();
        Pin(SmibEvents1ms, Digest(RmsTransientStability.StudyEventsScheduled(m, pf, data, SmibFault(), RmsStepSchedule.Fixed(0.001), durationSeconds: 1.0)));
    }

    [Fact]
    public void Smib_observed_plain_and_one_window_runs_keep_the_pinned_bits()
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Smib();
        Pin(SmibObserved1ms, Digest(RmsTransientStability.StudyEventsObserved(m, pf, data, SmibFault(), new[] { 1, 2 }, stepSeconds: 0.001, durationSeconds: 1.0)));
        Pin(SmibObserved1ms, Digest(RmsTransientStability.StudyEventsObservedScheduled(m, pf, data, SmibFault(), new[] { 1, 2 },
            RmsStepSchedule.Fixed(0.001), durationSeconds: 1.0)));
    }

    [Fact]
    public void Case9_with_governors_and_exciters_plain_and_one_window_runs_keep_the_pinned_bits()
    {
        // TGOV1 on every machine (the step-sized hook on the schedule path) and SEXS on every machine (dt per call).
        (GridModel c9, PowerFlowResult pf9, RmsEvent[] ev) = Case9();
        var buses = c9.Buses.Select(b => b.Id).ToList();
        var gov = new Tgov1Data(0.05, 0.5, 1.0, 5.0);
        Pin(Case9GovExcObserved5ms, Digest(RmsTransientStability.StudyEventsObserved(c9, pf9, Genrou(c9, gov), ev, buses,
            stepSeconds: 0.005, durationSeconds: 2.0, exciters: Sexs(c9))));
        Pin(Case9GovExcObserved5ms, Digest(RmsTransientStability.StudyEventsObservedScheduled(c9, pf9, Genrou(c9, gov), ev, buses,
            RmsStepSchedule.Fixed(0.005), durationSeconds: 2.0, exciters: Sexs(c9))));
    }

    [Fact]
    public void Case9_no_event_run_plain_and_one_window_keep_the_pinned_bits()
    {
        (GridModel c9, PowerFlowResult pf9, _) = Case9();
        var gov = new Tgov1Data(0.05, 0.5, 1.0, 5.0);
        Pin(Case9GovNoEvent5ms, Digest(RmsTransientStability.StudyEventsObserved(c9, pf9, Genrou(c9, gov), Array.Empty<RmsEvent>(), new[] { 5 },
            stepSeconds: 0.005, durationSeconds: 1.0)));
        Pin(Case9GovNoEvent5ms, Digest(RmsTransientStability.StudyEventsObservedScheduled(c9, pf9, Genrou(c9, gov), Array.Empty<RmsEvent>(), new[] { 5 },
            RmsStepSchedule.Fixed(0.005), durationSeconds: 1.0)));
    }

    [Fact]
    public void A_schedule_of_several_windows_with_one_step_matches_the_plain_run_through_its_first_window()
    {
        // Windows at 0, 0.25 and 0.5, all dt = 0.001. Each window's samples are from + j dt, which can differ from the plain
        // k dt in the last bits, so this is NOT expected to match the pin in general. It is checked against the plain run
        // only where the times agree exactly: in the first window (from = 0) every sample is the plain one.
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Smib();
        MultiMachineSubtransient.Result plain = RmsTransientStability.StudyEvents(m, pf, data, SmibFault(), stepSeconds: 0.001, durationSeconds: 1.0);
        var sched = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.001), new RmsStepWindow(0.25, 0.001), new RmsStepWindow(0.5, 0.001) });
        MultiMachineSubtransient.Result s = RmsTransientStability.StudyEventsScheduled(m, pf, data, SmibFault(), sched, durationSeconds: 1.0);
        // 250 + 250 + 500 steps: the sample count is the plain one.
        Assert.Equal(plain.Machines[0].TimeSeconds.Count, s.Machines[0].TimeSeconds.Count);
        for (int k = 0; k <= 250; k++)
        {
            Assert.Equal(plain.Machines[0].TimeSeconds[k], s.Machines[0].TimeSeconds[k]);
            Assert.Equal(plain.Machines[0].DeltaRad[k], s.Machines[0].DeltaRad[k]);
            Assert.Equal(plain.Machines[0].SpeedPu[k], s.Machines[0].SpeedPu[k]);
            Assert.Equal(plain.Machines[0].EqppPu[k], s.Machines[0].EqppPu[k]);
        }
    }
}
