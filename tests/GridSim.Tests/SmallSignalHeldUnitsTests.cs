using GridSim.Core.Assets;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R009: small-signal assembly holds non-machines (H = 0, converter-interfaced, power-flow-only) as constant
/// admittances instead of throwing, and a generator bound to an inverter class is represented ONLY by its
/// inverter control block, never also as a swing machine.
/// </summary>
public class SmallSignalHeldUnitsTests
{
    private static (GridModel Model, PowerFlowResult Pf) Case9WithZeroInertiaUnit(int genIndex)
    {
        GridModel baseModel = IeeeCases.Case9();
        var gens = baseModel.Generators.ToList();
        gens[genIndex] = gens[genIndex] with { H = 0.0 };   // e.g. a wind/solar unit with no rotor inertia
        GridModel model = baseModel.CopyWith(generators: gens);
        // H does not enter the power flow, so the solved state is that of the unmodified case.
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf);
    }

    [Fact]
    public void An_H_zero_generator_is_held_instead_of_failing_the_modal_assembly()
    {
        // The R009 failure: 'modal gb-full' threw "Generator at bus N has no inertia constant" on the first
        // wind or solar unit. Now that unit is held and the remaining machines form the swing model.
        (GridModel model, PowerFlowResult pf) = Case9WithZeroInertiaUnit(2);
        SmallSignalModel ssm = SmallSignalAssembler.FromSolvedCase(model, pf);

        Assert.Equal(2, ssm.MachineCount);
        Assert.Equal(4, ssm.Order);
        Assert.DoesNotContain(model.Generators[2].Bus, ssm.AngleNodeBuses);
        ModalResult modal = ModalAnalysis.Analyze(ssm);
        Assert.NotEmpty(modal.Modes);
    }

    [Fact]
    public void Damping_is_aligned_to_the_generators_and_mapped_onto_the_machines()
    {
        (GridModel model, PowerFlowResult pf) = Case9WithZeroInertiaUnit(1);
        // One entry per generator; the held unit's entry (index 1) is not used.
        double[] damping = { 2.0, 99.0, 3.0 };
        SmallSignalModel ssm = SmallSignalAssembler.FromSolvedCase(model, pf, machineDamping: damping);
        Assert.Equal(2, ssm.MachineCount);

        int g = ssm.MachineCount;
        double baseMva = model.BaseMva;
        // Machine 0 is generator 0, machine 1 is generator 2: A[g+i, g+i] = -D_i / (2 H_i,sys).
        double m0 = 2.0 * model.Generators[0].H * model.Generators[0].MBase / baseMva;
        double m1 = 2.0 * model.Generators[2].H * model.Generators[2].MBase / baseMva;
        Assert.Equal(-2.0 / m0, ssm.A[g + 0, g + 0]);
        Assert.Equal(-3.0 / m1, ssm.A[g + 1, g + 1]);
    }

    [Fact]
    public void Damping_with_the_wrong_length_or_a_non_finite_entry_fails_loud()
    {
        (GridModel model, PowerFlowResult pf) = Case9WithZeroInertiaUnit(1);
        Assert.Throws<ArgumentException>(() => SmallSignalAssembler.FromSolvedCase(model, pf, machineDamping: new[] { 1.0, 1.0 }));
        Assert.Throws<ArgumentException>(() => SmallSignalAssembler.FromSolvedCase(model, pf, machineDamping: new[] { 1.0, 1.0, double.NaN }));
    }

    [Fact]
    public void A_case_with_no_machine_at_all_still_fails_loud()
    {
        GridModel baseModel = IeeeCases.Case9();
        GridModel model = baseModel.CopyWith(generators: baseModel.Generators.Select(x => x with { H = 0.0 }).ToList());
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.Throws<InvalidOperationException>(() => SmallSignalAssembler.FromSolvedCase(model, pf));
    }

    private static GridModel BoundGfmOnGenerator0()
    {
        GridModel baseModel = IeeeCases.Case9();
        var gens = baseModel.Generators.ToList();
        gens[0] = gens[0] with { DynamicsClass = "gfm-test" };   // H > 0 unit declared grid-forming
        var cls = new AssetClass("gfm-test", AssetKind.GridFormingInverter, "gfm",
            new Dictionary<string, double>
            {
                [AssetParams.SynInertiaMwsPerHz] = 0.0,
                [AssetParams.DampingMwPerHz] = 5.0,
                [AssetParams.ActuateTauS] = 0.05,
            },
            new Dictionary<string, AssetClassProvenance>());
        var spec = new AssetDynamicsModel(new AssetClassLibrary(new[] { cls }), new AssetBindingSet(Array.Empty<AssetBinding>()));
        return new GridModel
        {
            BaseMva = baseModel.BaseMva,
            Buses = baseModel.Buses,
            Generators = gens,
            Branches = baseModel.Branches,
            FrequencyHz = baseModel.FrequencyHz,
            Name = baseModel.Name,
            AssetDynamics = spec,
        };
    }

    [Fact]
    public void A_unit_bound_to_an_inverter_class_is_not_also_a_swing_machine()
    {
        // The R009 double count: the bound unit used to carry its 2H and an X'd internal node as a rotor AND a
        // GFM droop block injecting into the COI.
        GridModel model = BoundGfmOnGenerator0();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        int boundBus = model.Generators[0].Bus;

        SmallSignalModel swing = SmallSignalAssembler.FromSolvedCase(model, pf);
        Assert.Equal(2, swing.MachineCount);
        Assert.DoesNotContain(boundBus, swing.AngleNodeBuses);

        SmallSignalModel withInv = SmallSignalAssembler.FromSolvedCaseWithInverters(model, pf);
        Assert.Equal(2, withInv.MachineCount);
        Assert.DoesNotContain(boundBus, withInv.AngleNodeBuses);
        // Exactly one inverter state (the first-order GFM actuator), placed on the bound unit's bus.
        Assert.Equal(2 * 2 + 1, withInv.Order);
        StateLabel inv = withInv.States[withInv.Order - 1];
        Assert.True(inv.IsSynthetic);
        Assert.Equal(boundBus, inv.OwnerId);
        Assert.DoesNotContain(withInv.States.Take(4), s => s.OwnerId == boundBus);
    }

    [Fact]
    public void An_unbound_all_machine_case_is_unchanged_bit_for_bit()
    {
        // Orchestration proof on the canonical case: holding nothing reproduces the swing model exactly as the
        // all-generators-are-machines reduction builds it.
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        SmallSignalModel ssm = SmallSignalAssembler.FromSolvedCase(model, pf);
        GridSim.Core.Dynamics.Distributed.ClassicalInit.Prepared p =
            GridSim.Core.Dynamics.Distributed.ClassicalInit.FromSolvedCase(model, pf);
        Assert.Equal(p.GeneratorBuses, ssm.AngleNodeBuses);
        int g = p.Eq.Length;
        double ws = 2.0 * Math.PI * model.FrequencyHz;
        for (int i = 0; i < g; i++)
        {
            Assert.Equal(ws, ssm.A[i, g + i]);
            double m = 2.0 * p.H[i];
            for (int j = 0; j < g; j++)
                Assert.Equal(-ssm.Kext[i, j] / m, ssm.A[g + i, j]);
        }
    }
}
