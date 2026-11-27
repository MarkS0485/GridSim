using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (2.1.122): the t = 0 refusal of 2.1.120 (B28) per stabiliser and for the exciters the 2.1.120 tests left out.
/// <para>
/// A stabiliser is refused with a disturbance in force at t = 0 only when its first step sets a state from a network
/// measurement (<see cref="IRmsStabiliser.ReadsNetworkInputsAtStart"/>): an input on electrical power (IEEEST ICS 3,
/// PSS2A/B/C ICS 3), whose washouts start at that step's value, or the PSS2C output logic, switched on or off from the
/// first step's power. IEEEST on rotor speed with a voltage cut-off (VCU or VCL above 0) reads the terminal voltage on
/// every step, but the cut-off is memoryless (the output is 0 outside [VCL, VCU], else the filtered speed signal) and sets
/// no state, and its states start from speed, which no network change moves at t = 0. 2.1.120 refused it; it now runs.
/// </para>
/// <para>
/// Also covered here: PSS2C on power and with its logic, ESAC8B with TR above 0, and EXST1 with TR above 0. Case data and
/// kernel call as <see cref="Rc5FirstStepInitialisationTests"/>: WSCC 9-bus with the GENROU fixture, the caller's voltage
/// references of 1.0 pu, a fault at bus 7 for 0.08 s.
/// </para>
/// </summary>
public sealed class Rc5ReviewFix2120StabiliserTests
{
    private static string SchemasDir => Path.Combine(CliProcess.Root(), "schemas", "v2");

    private static string Case9Fixture => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynamics", "wscc9-genrou.dynamics.json");

    private static (GridModel Model, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        MachineDynamicsSet set = MachineDynamicsLoader.Load(Case9Fixture, SchemasDir, model);
        return (model, pf, model.Generators.Select((g, k) => set.For(k)!.Data).ToList());
    }

    // IEEEX1 with TR = 0 sets its state at initialisation: the base exciter for the stabiliser cases.
    private static Exdc2 Ieeex1() => Exdc2.Ieeex1(0.0, 300.0, 0.01, 0.0, 0.0, 4.95, -4.95, 1.0, 1.33, 0.0675, 0.6, 2.29, 0.117, 3.05, 0.279);

    private static Ieeest MakeIeeest(int ics, double vcu, double vcl) => new(ics, 0.05, 0.0, 0.0, 0.2, 0.05, 0.3, 0.1, 1.5, 2.0, 10.0, 1.0, -1.0, vcu, vcl);

    private static Pss2c MakePss2c(int ics2, bool logic) => logic
        ? new Pss2c(1, ics2, 0, 0, 2.0, 2.0, 0.02, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.1, 0.05,
            10.0, -10.0, 10.0, -10.0, 10.0, -10.0, 0.2, 0.04, pssOnPu: 0.3, pssOffPu: 0.2)
        : new Pss2c(1, ics2, 0, 0, 2.0, 2.0, 0.02, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.1, 0.05,
            10.0, -10.0, 10.0, -10.0, 10.0, -10.0, 0.2, 0.04);

    private static Esac8b MakeEsac8b(double tr) => new(tr, 5.0, 2.0, 0.5, 0.05, 2.0, 0.05, 50.0, -50.0, 0.8, 1.0, 0.0, 0.0, 0.0, 0.0);

    private static RmsExciter Exciter(string name) => name switch
    {
        "IEEEST speed VCU VCL" => RmsExciter.FromModel(Ieeex1(), null, MakeIeeest(1, 1.5, 0.5)),
        "IEEEST speed VCU" => RmsExciter.FromModel(Ieeex1(), null, MakeIeeest(1, 1.5, 0.0)),
        "IEEEST speed VCL" => RmsExciter.FromModel(Ieeex1(), null, MakeIeeest(1, 0.0, 0.5)),
        "IEEEST speed" => RmsExciter.FromModel(Ieeex1(), null, MakeIeeest(1, 0.0, 0.0)),
        "IEEEST power VCU VCL" => RmsExciter.FromModel(Ieeex1(), null, MakeIeeest(3, 1.5, 0.5)),
        "PSS2C power" => RmsExciter.FromModel(Ieeex1(), null, MakePss2c(3, logic: false)),
        "PSS2C speed logic" => RmsExciter.FromModel(Ieeex1(), null, MakePss2c(1, logic: true)),
        "PSS2C speed" => RmsExciter.FromModel(Ieeex1(), null, MakePss2c(1, logic: false)),
        "ESAC8B TR" => RmsExciter.FromModel(MakeEsac8b(0.02)),
        "ESAC8B" => RmsExciter.FromModel(MakeEsac8b(0.0)),
        "EXST1 TR" => RmsExciter.FromModel(St1Exciter.Exst1(0.02, 0.2, -0.2, 1.0, 10.0, 200.0, 0.02, 7.0, -6.4, 0.05, 0.02, 1.0)),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such case"),
    };

    private static MultiMachineSubtransient.Result Run(string name, double onset)
    {
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9();
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, pf, data, 7);
        var controllers = new IRmsFieldController?[p.Machines.Count];
        for (int i = 0; i < controllers.Length; i++)
        {
            RmsExciter ex = Exciter(name);
            ex.BindMachineBase(MachineBase.EffectiveMva(model.Generators[i], model.BaseMva) / model.BaseMva);
            controllers[i] = ex;
        }
        var stages = new List<RmsNetworkStage> { new(onset, p.NetworkFaulted!), new(onset + 0.08, p.Network!) };
        return MultiMachineSubtransient.Simulate(p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0,
            p.Network!, 60.0, 0.005, 0.2, stages, voltageReferencePu: Enumerable.Repeat(1.0, controllers.Length).ToArray(),
            fieldControllers: controllers);
    }

    [Theory]
    [InlineData("IEEEST power VCU VCL", "IEEEX1")]
    [InlineData("PSS2C power", "IEEEX1")]
    [InlineData("PSS2C speed logic", "IEEEX1")]
    [InlineData("ESAC8B TR", "ESAC8B")]
    [InlineData("EXST1 TR", "EXST1")]
    public void A_model_that_sets_a_state_from_the_first_step_is_refused_at_t_0_and_runs_after_it(string name, string kind)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Run(name, 0.0));
        Assert.Equal($"{kind} on machine 0 finishes its initialisation on the first step's terminal measurements, which must come "
            + "from the undisturbed base network; schedule the disturbance after t = 0.", ex.Message);
        MultiMachineSubtransient.Result later = Run(name, 0.1);
        Assert.Equal(41, later.Machines[0].SpeedPu.Count);
        Assert.All(later.Machines, m => Assert.All(m.SpeedPu, w => Assert.True(double.IsFinite(w))));
    }

    [Theory]
    [InlineData("IEEEST speed VCU VCL")]
    [InlineData("IEEEST speed VCU")]
    [InlineData("IEEEST speed VCL")]
    [InlineData("PSS2C speed")]
    [InlineData("ESAC8B")]
    public void A_stabiliser_on_speed_or_an_exciter_without_a_transducer_runs_with_a_fault_at_t_0(string name)
    {
        MultiMachineSubtransient.Result r = Run(name, 0.0);
        Assert.Equal(41, r.Machines[0].SpeedPu.Count);
        Assert.All(r.Machines, m => Assert.All(m.SpeedPu, w => Assert.True(double.IsFinite(w))));
    }

    /// <summary>2.1.126 (review of 2.1.122): with VCU 1.5 and VCL 0 the cut-off acts only when Vt exceeds 1.5 pu, which it
    /// never does here (voltage references of 1.0 pu, a fault that lowers the voltage), so the run with the cut-off is the
    /// run without it, bit for bit, with the fault at t = 0 and after it.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    public void Ieeest_on_speed_with_a_cut_off_that_never_acts_is_the_run_without_it_bit_for_bit(double onset)
    {
        MultiMachineSubtransient.Result cut = Run("IEEEST speed VCU", onset), plain = Run("IEEEST speed", onset);
        Assert.Equal(plain.Machines.Count, cut.Machines.Count);
        for (int i = 0; i < plain.Machines.Count; i++)
        {
            MultiMachineSubtransient.MachineTrace a = plain.Machines[i], b = cut.Machines[i];
            Assert.Equal(Bits(a.DeltaRad), Bits(b.DeltaRad));
            Assert.Equal(Bits(a.SpeedPu), Bits(b.SpeedPu));
            Assert.Equal(Bits(a.EqpPu), Bits(b.EqpPu));
            Assert.Equal(Bits(a.EdpPu), Bits(b.EdpPu));
            Assert.Equal(Bits(a.EqppPu), Bits(b.EqppPu));
            Assert.Equal(Bits(a.EddpPu), Bits(b.EddpPu));
        }
    }

    private static long[] Bits(IReadOnlyList<double> x) => x.Select(BitConverter.DoubleToInt64Bits).ToArray();

    [Fact]
    public void Each_stabiliser_says_whether_it_reads_network_inputs_at_the_start()
    {
        // IEEEST: the cut-off reads Vt on every step (NeedsTerminalQuantities), but only ICS 3 sets a state from it.
        Ieeest speedCut = MakeIeeest(1, 1.5, 0.5);
        Assert.True(speedCut.NeedsTerminalQuantities);
        Assert.False(speedCut.ReadsNetworkInputsAtStart);
        Assert.False(MakeIeeest(1, 0.0, 0.0).ReadsNetworkInputsAtStart);
        Assert.True(MakeIeeest(3, 0.0, 0.0).ReadsNetworkInputsAtStart);
        Assert.True(MakeIeeest(3, 1.5, 0.5).ReadsNetworkInputsAtStart);
        // PSS2A/PSS2B: either input on power.
        Assert.True(Pss2.Pss2a(1, 3, 5, 1, 2.0, 2.0, 0.0, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.1, -0.1).ReadsNetworkInputsAtStart);
        Assert.False(Pss2.Pss2a(1, 1, 5, 1, 2.0, 2.0, 0.0, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.1, -0.1).ReadsNetworkInputsAtStart);
        // PSS2C: an input on power, or the output logic.
        Assert.True(MakePss2c(3, logic: false).ReadsNetworkInputsAtStart);
        Assert.True(MakePss2c(1, logic: true).ReadsNetworkInputsAtStart);
        Assert.False(MakePss2c(1, logic: false).ReadsNetworkInputsAtStart);
        // Through the interface (the kernel's view), the same answers.
        IRmsStabiliser s = speedCut;
        Assert.False(s.ReadsNetworkInputsAtStart);
    }
}
