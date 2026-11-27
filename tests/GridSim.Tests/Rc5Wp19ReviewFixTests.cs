using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 review fix to WP19 (2.1.118). ESST3A and EXDC2 finish their initialisation on the first step's terminal
/// measurements (ESST3A sets VB0 and its steady input from them; EXDC2 checks VR0 against Vt VRMIN .. Vt VRMAX). A
/// disturbance already in force at t = 0 would fold a disturbed Vt and It into the steady state. The kernel refuses that:
/// with automatic voltage references it always did (the references need the base network at t = 0); with the caller's
/// references it now refuses it for these two models by name. The check is exact (the network in force at the first step
/// is the base network object or it is not), so no tolerance is involved. IEEEX1, ESAC8B and the older models set their
/// state at initialisation and are not affected. Case data: WSCC 9-bus with the GENROU fixture, as
/// <see cref="Rc5Wp19ControllerLibraryTests"/>.
/// </summary>
public sealed class Rc5Wp19ReviewFixTests
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

    private static RmsExciter Exciter(string name) => RmsExciter.FromModel(name switch
    {
        "EXDC2" => Exdc2.Exdc2Model(0.0, 300.0, 0.01, 0.0, 0.0, 4.95, -4.95, 1.0, 1.33, 0.0675, 0.6, 2.29, 0.117, 3.05, 0.279),
        "IEEEX1" => Exdc2.Ieeex1(0.0, 300.0, 0.01, 0.0, 0.0, 4.95, -4.95, 1.0, 1.33, 0.0675, 0.6, 2.29, 0.117, 3.05, 0.279),
        _ => new Esst3a(0.0, 0.2, -0.2, 7.93, 1.0, 6.67, 20.0, 0.0, 10.0, -10.0, 1.0, 6.15, 0.0, 6.9, 0.2, 0.081, 5.8, 20.0, 0.4, 1.0, 0.0),
    });

    [Theory]
    [InlineData("ESST3A")]
    [InlineData("EXDC2")]
    public void A_fault_at_t_0_is_refused_with_automatic_references(string name)
    {
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9();
        List<RmsExciter?> fleet = model.Generators.Select(_ => (RmsExciter?)Exciter(name)).ToList();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => RmsTransientStability.Study(model, pf, data, 7,
            onsetSeconds: 0.0, clearingSeconds: 0.08, stepSeconds: 0.005, durationSeconds: 0.2, exciters: fleet));
        Assert.Contains("schedule the disturbance after t = 0", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The kernel with the caller's voltage references (1.0 pu each) and the case's fault at bus 7, on at
    /// <paramref name="onset"/> and off 0.08 s later.</summary>
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
    [InlineData("ESST3A")]
    [InlineData("EXDC2")]
    public void A_fault_at_t_0_is_refused_with_the_callers_references_and_one_after_t_0_runs(string name)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Run(name, 0.0));
        Assert.Equal($"{name} on machine 0 finishes its initialisation on the first step's terminal measurements, which must come "
            + "from the undisturbed base network; schedule the disturbance after t = 0.", ex.Message);
        MultiMachineSubtransient.Result later = Run(name, 0.1);
        Assert.Equal(41, later.Machines[0].SpeedPu.Count);
    }

    [Fact]
    public void Ieeex1_sets_its_state_at_initialisation_and_is_not_refused()
    {
        MultiMachineSubtransient.Result r = Run("IEEEX1", 0.0);
        Assert.Equal(41, r.Machines[0].SpeedPu.Count);
    }
}
