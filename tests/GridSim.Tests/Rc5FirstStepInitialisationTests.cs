using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (2.1.120, B28): every model that sets a state, or checks its start, from the first step's measurements is refused
/// when a disturbance is in force at t = 0, because the measurements then come from the disturbed network and the model
/// would start from a disturbed point. 2.1.118 did this for ESST3A and EXDC2; 2.1.120 widens it to:
/// <list type="bullet">
/// <item>ESST4B (every state from VT, IT and IFD on the first step);</item>
/// <item>SCRX bus fed (x0 = Efd0 / Vt0 and the reference completed on the first step);</item>
/// <item>a library exciter with a voltage transducer, TR above 0 (EXDC2, IEEEX1, ESDC1A, ESAC8B, ESST1A, EXST1): the lag
/// starts at the first measured Vt;</item>
/// <item>a library stabiliser that reads the electrical power or the terminal voltage (IEEEST, PSS2A, PSS2B, PSS2C): its
/// washouts start at the first step's input;</item>
/// <item>GGOV1 reading the electrical power (Rselect 1 or a supervisory load controller): Pref, Pmwset and its power
/// transducer are set from the first step's power.</item>
/// </list>
/// The check is exact: the network in force at the first step is the base network object or it is not. Models that read
/// only states at t = 0 (rotor speed) or set every state at initialisation run as before. A run with no disturbance at
/// t = 0 never reaches the throw, so its numbers are unchanged. Case data: WSCC 9-bus with the GENROU fixture, as
/// <see cref="Rc5Wp19ReviewFixTests"/>.
/// </summary>
public sealed class Rc5FirstStepInitialisationTests
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

    // IEEEX1 with TR = 0 sets its state at initialisation (Rc5Wp19ReviewFixTests): the base for the stabiliser cases.
    private static Exdc2 Ieeex1(double tr = 0.0) => Exdc2.Ieeex1(tr, 300.0, 0.01, 0.0, 0.0, 4.95, -4.95, 1.0, 1.33, 0.0675, 0.6, 2.29, 0.117, 3.05, 0.279);

    private static Ieeest MakeIeeest(int ics) => new(ics, 0.05, 0.0, 0.0, 0.2, 0.05, 0.3, 0.1, 1.5, 2.0, 10.0, 1.0, -1.0, 0.0, 0.0);

    private static RmsExciter Exciter(string name) => name switch
    {
        "ESST4B" => RmsExciter.FromEsst4b(new Esst4b(3.15, 3.15, -0.87, 10.0, 0.01, 1.0, 0.0, -0.87, 10.0, 0.1, 6.5, 0.0, 20.0, 0.113, 0.0, 0.0)),
        "SCRX bus fed" => RmsExciter.FromModel(new Scrx(0.0, 0.0, 200.0, 0.02, 0.0, 6.0, busFed: true, 0.0)),
        "SCRX solid fed" => RmsExciter.FromModel(new Scrx(0.0, 0.0, 200.0, 0.02, 0.0, 6.0, busFed: false, 0.0)),
        "ESDC1A TR" => RmsExciter.FromModel(new Esdc1a(0.02, 46.0, 0.06, 0.1, 0.5, 5.0, -5.0, 1.0, 0.46, 0.1, 1.0, 2.3, 0.1, 3.1, 0.33)),
        "ESDC1A" => RmsExciter.FromModel(new Esdc1a(0.0, 46.0, 0.06, 0.1, 0.5, 5.0, -5.0, 1.0, 0.46, 0.1, 1.0, 2.3, 0.1, 3.1, 0.33)),
        "IEEEX1 TR" => RmsExciter.FromModel(Ieeex1(0.02)),
        "ESST1A TR" => RmsExciter.FromModel(St1Exciter.Esst1a(0.02, 1.0, -1.0, 0.5, 2.0, 0.0, 0.0, 50.0, 0.1, 10.0, -10.0, 10.0, -10.0, 0.0, 0.1, 1.0, 0.0, 0.0)),
        "ESST1A" => RmsExciter.FromModel(St1Exciter.Esst1a(0.0, 1.0, -1.0, 0.5, 2.0, 0.0, 0.0, 50.0, 0.1, 10.0, -10.0, 10.0, -10.0, 0.0, 0.1, 1.0, 0.0, 0.0)),
        "IEEEST power" => RmsExciter.FromModel(Ieeex1(), null, MakeIeeest(3)),
        "IEEEST speed" => RmsExciter.FromModel(Ieeex1(), null, MakeIeeest(1)),
        "PSS2A" => RmsExciter.FromModel(Ieeex1(), null, Pss2.Pss2a(1, 3, 5, 1, 2.0, 2.0, 0.0, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.1, -0.1)),
        "PSS2B" => RmsExciter.FromModel(Ieeex1(), null, Pss2.Pss2b(1, 3, 5, 1, 2.0, 2.0, 0.0, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.0, 0.0, 0.5, -0.5, 2.0, -2.0, 0.1, -0.1)),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such case"),
    };

    /// <summary>The kernel with the caller's voltage references (1.0 pu each) and a fault at bus 7, on at
    /// <paramref name="onset"/> and off 0.08 s later, every machine under <paramref name="name"/>.</summary>
    private static MultiMachineSubtransient.Result Run(string name, double onset)
    {
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9();
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, pf, data, 7);
        Assert.Equal(model.Generators.Count, p.Machines.Count);
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
    [InlineData("ESST4B", "ESST4B")]
    [InlineData("SCRX bus fed", "SCRX")]
    [InlineData("ESDC1A TR", "ESDC1A")]
    [InlineData("IEEEX1 TR", "IEEEX1")]
    [InlineData("ESST1A TR", "ESST1A")]
    [InlineData("IEEEST power", "IEEEX1")]
    [InlineData("PSS2A", "IEEEX1")]
    [InlineData("PSS2B", "IEEEX1")]
    public void A_first_step_initialiser_is_refused_with_a_fault_at_t_0_and_runs_with_one_after_t_0(string name, string kind)
    {
        // Before 2.1.120 the run at t = 0 returned numbers (B28: result to refused); the run at 0.1 s still does.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Run(name, 0.0));
        Assert.Equal($"{kind} on machine 0 finishes its initialisation on the first step's terminal measurements, which must come "
            + "from the undisturbed base network; schedule the disturbance after t = 0.", ex.Message);
        MultiMachineSubtransient.Result later = Run(name, 0.1);
        Assert.Equal(41, later.Machines[0].SpeedPu.Count);
        Assert.All(later.Machines, m => Assert.All(m.SpeedPu, w => Assert.True(double.IsFinite(w))));
    }

    [Theory]
    [InlineData("SCRX solid fed")]
    [InlineData("ESDC1A")]
    [InlineData("ESST1A")]
    [InlineData("IEEEST speed")]
    public void A_model_that_sets_its_state_at_initialisation_or_reads_only_speed_is_not_refused(string name)
    {
        // Solid-fed SCRX, ESDC1A and ESST1A with TR = 0 (ESST1A's first step only checks Efd0 against Vt VRMAX, which moves
        // no number), and IEEEST on rotor speed, a state that no network change moves at t = 0.
        MultiMachineSubtransient.Result r = Run(name, 0.0);
        Assert.Equal(41, r.Machines[0].SpeedPu.Count);
    }

    // GGOV1 of Rc5Wp3bDynamicLibraryTests (the reference script's case 7) on machine 2 only (bus 3, 85 MW), turbine base =
    // machine base. Rselect 1 reads the electrical power; Rselect -1 (valve feedback) with no load controller does not.
    private static Ggov1Data MakeGgov1(int rselect, double mbase) => new(rselect, 0, 0.04, 1.0, 0.05, -0.05, 10.0, 2.0, 0.0, 1.0,
        1.0, 0.15, 0.5, 1.5, 0.2, 0.1, 0.0, 0.0, 3.0, 2.0, 0.67, 1.0, 0.0, 1.0, -1.0, 0.0, 0.1, 0.0, 0.1, 0.0, 0.0, 4.0, 5.0, 99.0, -99.0, mbase);

    private static MultiMachineSubtransient.Result Governed(int rselect, double onset)
    {
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9();
        double mbase = MachineBase.EffectiveMva(model.Generators[2], model.BaseMva);
        List<SubtransientInit.MachineData> governed = data.Select((d, k) => k == 2 ? d with { Governor = MakeGgov1(rselect, mbase) } : d).ToList();
        return RmsTransientStability.Study(model, pf, governed, 7, onsetSeconds: onset, clearingSeconds: 0.08, stepSeconds: 0.005, durationSeconds: 0.2);
    }

    [Fact]
    public void Ggov1_on_electrical_power_is_refused_with_a_fault_at_t_0_and_on_valve_feedback_is_not()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Governed(1, 0.0));
        Assert.Equal("GGOV1 reading the electrical power (Rselect 1 or a supervisory load controller) sets its references from the "
            + "first step's electrical power, which must come from the undisturbed base network; schedule the disturbance after t = 0.", ex.Message);
        Assert.Equal(41, Governed(1, 0.1).Machines[2].SpeedPu.Count);
        Assert.Equal(41, Governed(-1, 0.0).Machines[2].SpeedPu.Count);
    }
}
