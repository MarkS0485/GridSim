using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using Xunit.Abstractions;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 stage 6 baseline: SHA-256 digests of full-model (MultiMachineSubtransient, fixed-step RK4) runs, taken on the kernel
/// at d7655da6 BEFORE unit relays were added to it. Each digest covers every sample's time, rotor angle, speed and the four
/// flux states of every machine, every step's field voltage (read through a controller that only forwards to the real
/// SEXS), the synchronism verdict and the converter counters, for an event list with a fault and its clearance, a branch
/// trip, a load step and a generator trip, on a held-field fleet and on a fleet with SEXS exciters and TGOV1 governors; and
/// the dynamics-study (case14 RAW + DYR) result rows. With unit relays off every one of these stays the same bits.
/// </summary>
public sealed class FullModelKernelDigestTests(ITestOutputHelper output)
{
    private const string HeldFieldDigest = "F9DCB7B3F7372EAEEEDC756542B42BD7EFC481C5EE18C14FA157573753023382";
    private const string ExcitedGovernedDigest = "C85C068D2CAC172EBC8C0D26B5552A694474E73048803F890C38758BF130F62D";
    private const string DynamicsStudyDigest = "3371237C7599B199E0E258ADB476258467CFC3A9722B1305D5AF6971C823D026";

    private const double Step = 0.005;

    /// <summary>A controller that forwards every call to the real exciter and records the field voltage it returns.</summary>
    private sealed class Recording(RmsExciter inner) : IRmsFieldController
    {
        public List<double> Field { get; } = new();
        public void Initialize(double fieldVoltagePu) { Field.Clear(); inner.Initialize(fieldVoltagePu); }
        public double EquilibriumErrorPu(double fieldVoltagePu) => inner.EquilibriumErrorPu(fieldVoltagePu);
        public double Step(double voltageErrorPu, double speedDeviationPu, double dtSeconds)
        {
            double e = inner.Step(voltageErrorPu, speedDeviationPu, dtSeconds);
            Field.Add(e);
            return e;
        }
    }

    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model, GovernorData? governor = null)
    {
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
            data.Add(new SubtransientInit.MachineData(
                H: gen.H, Damping: 0.0,
                Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05, Governor: governor));
        return data;
    }

    private static int BranchBetween(GridModel m, int a, int b)
    {
        for (int i = 0; i < m.Branches.Count; i++)
        {
            Branch br = m.Branches[i];
            if (br.InService && ((br.From == a && br.To == b) || (br.From == b && br.To == a))) return i;
        }
        throw new InvalidOperationException($"case9 has no branch {a}-{b}");
    }

    private static int GeneratorAt(GridModel m, int bus)
    {
        for (int i = 0; i < m.Generators.Count; i++) if (m.Generators[i].Bus == bus) return i;
        throw new InvalidOperationException($"case9 has no generator at bus {bus}");
    }

    internal static RmsEvent[] Events(GridModel m) => new RmsEvent[]
    {
        new RmsBusFault(0.1, 7),
        new RmsFaultClear(0.18, 7),
        new RmsBranchTrip(0.18, BranchBetween(m, 7, 8)),
        new RmsLoadStep(0.5, 5, 20.0, 5.0),
        new RmsGeneratorTrip(1.0, GeneratorAt(m, 3)),
    };

    /// <summary>The digest of a run: every sample of every machine, every recorded field voltage, the verdict, the events.</summary>
    internal static string Digest(MultiMachineSubtransient.Result r, IReadOnlyList<RmsEvent> events, IEnumerable<IReadOnlyList<double>>? fields = null)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (RmsEvent e in events) w.Write(e.Describe());
            w.Write(r.StayedInSynchronism);
            w.Write(r.ConverterIterationFailures);
            w.Write(r.ConverterRescuedSteps);
            w.Write(r.Machines.Count);
            foreach (MultiMachineSubtransient.MachineTrace m in r.Machines)
            {
                w.Write(m.TimeSeconds.Count);
                for (int k = 0; k < m.TimeSeconds.Count; k++)
                {
                    w.Write(BitConverter.DoubleToInt64Bits(m.TimeSeconds[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(m.DeltaRad[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(m.SpeedPu[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(m.EqpPu[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(m.EdpPu[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(m.EqppPu[k]));
                    w.Write(BitConverter.DoubleToInt64Bits(m.EddpPu[k]));
                }
            }
            if (fields is not null)
                foreach (IReadOnlyList<double> f in fields)
                {
                    w.Write(f.Count);
                    foreach (double v in f) w.Write(BitConverter.DoubleToInt64Bits(v));
                }
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    private static (GridModel Model, PowerFlowResult Pf) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf);
    }

    internal static string HeldFieldRun()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        RmsEvent[] ev = Events(m);
        MultiMachineSubtransient.Result r = RmsTransientStability.StudyEvents(m, pf, GenrouData(m), ev, stepSeconds: Step, durationSeconds: 2.0);
        return Digest(r, ev);
    }

    internal static string ExcitedGovernedRun()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        RmsEvent[] ev = Events(m);
        var rec = new List<Recording>();
        var exciters = new List<RmsExciter?>();
        foreach (Generator _ in m.Generators)
        {
            var r0 = new Recording(RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0));
            rec.Add(r0);
            exciters.Add(RmsExciter.FromUser(r0, "SEXS"));
        }
        MultiMachineSubtransient.Result r = RmsTransientStability.StudyEvents(m, pf, GenrouData(m, new Tgov1Data(0.05, 0.5, 1.0, 5.0)), ev,
            stepSeconds: Step, durationSeconds: 2.0, exciters: exciters);
        return Digest(r, ev, rec.Select(x => (IReadOnlyList<double>)x.Field));
    }

    internal static string DynamicsStudyRun()
    {
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(UnitRelayInLoopTests.BaselineCases(), duration: 2.0));
        string json = JsonSerializer.Serialize(new { r.Status, r.Cases, r.Counts, r.Initialisation });
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void The_held_field_event_run_is_the_baseline_kernels_bits()
    {
        string d = HeldFieldRun();
        output.WriteLine("held-field digest: " + d);
        Assert.Equal(HeldFieldDigest, d);
    }

    [Fact]
    public void The_excited_governed_event_run_with_its_field_voltages_is_the_baseline_kernels_bits()
    {
        string d = ExcitedGovernedRun();
        output.WriteLine("excited-governed digest: " + d);
        Assert.Equal(ExcitedGovernedDigest, d);
    }

    [Fact]
    public void The_dynamics_study_rows_are_the_baseline_kernels_bits()
    {
        string d = DynamicsStudyRun();
        output.WriteLine("dynamics-study digest: " + d);
        Assert.Equal(DynamicsStudyDigest, d);
    }
}
