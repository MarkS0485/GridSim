using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N117 / N118 (October 2026). N117: in an unbalanced fault study the phase relay elements measure PHASE quantities
/// built from the sequence study (the largest phase current at each branch end, the lowest / highest phase voltage at
/// each bus, the phase-to-phase distance loops), not the faulted positive-sequence state. N118: converter-interfaced
/// units, whose negative- and zero-sequence fault behaviour the model does not define, add no fault current in the
/// unbalanced study, and the result names them so the omission is said, not hidden.
/// The radial case is the F21 tests' one (EarthFaultAndIec60909CompletionTests): with no load and no charging the
/// prefault state is 1.0 pu everywhere, so every expected value below is a closed form.
/// </summary>
public class EarthFaultPhaseElementsTests
{
    // Bus 1: a generator (X''d 0.2 on 100 MVA). Transformer 1-2 delta (bus 1) / grounded wye (bus 2), x 0.1. Line 2-3,
    // x 0.2. Z1 = Z2 = j0.5 to bus 3; Z0 = j0.7 (transformer shunt j0.1 + 3 x line).
    private static GridModel Radial(params Generator[] extra) => new()
    {
        Name = "radial",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 20), new Bus(2, BusType.Pq, BaseKv: 132), new Bus(3, BusType.Pq, BaseKv: 132) },
        Generators = new[] { new Generator(1, 0, Vg: 1.0, MBase: 100, SubtransientXdPu: 0.2) }.Concat(extra).ToArray(),
        Branches = new[]
        {
            new Branch(1, 2, 0, 0.1, Name: "T1", IsTransformer: true, RateAMva: 100,
                FromWinding: WindingConnection.Delta, ToWinding: WindingConnection.WyeGrounded),
            new Branch(2, 3, 0, 0.2, Name: "L23", RateAMva: 100),
        },
    };

    private static PowerFlowResult Prefault(GridModel m)
    {
        PowerFlowResult r = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(r.Converged);
        return r;
    }

    [Fact]
    public void An_earth_fault_drives_the_phase_overcurrent_element_with_the_faulted_phase_current()
    {
        // SLG at bus 3: I1 = I2 = I0 = 1 / j1.7, so L23 carries Ia = 3 |I1| = 1.7647 pu (= 1.7647x its 1 pu rating) and
        // Ib = Ic = 0. The positive-sequence state alone shows |I1| = 0.588 pu, below the 1.2x pickup of 51.
        GridModel m = Radial();
        (UnbalancedFaultResult _, ProtectionReport rep) = ProtectionAssessment.AssessUnbalancedFault(m, Prefault(m), 3, FaultType.SingleLineToGround);
        RelayVerdict v51 = Assert.Single(rep.Operating, v => v.Device == "51" && v.LocationName == "L23");
        Assert.Equal(3.0 / 1.7, v51.Quantity, 12);
        // The faulted phase at the fault bus is held at 0 by the bolted earth fault: 27 measures the lowest phase voltage.
        RelayVerdict v27 = Assert.Single(rep.Operating, v => v.Device == "27" && v.LocationId == 3);
        Assert.Equal(0.0, v27.Quantity, 12);
    }

    [Fact]
    public void A_line_to_line_fault_drives_the_phase_elements_with_root_3_times_I1_and_the_b_c_distance_loop()
    {
        // LL at bus 3 with Zf = j0.02: I1 = -I2 = 1 / j1.02; phases b and c carry sqrt(3) |I1| = 1.6981 pu in L23. The
        // relay at bus 2 sees, on its b-c loop, the line plus half the fault reactance (the phase-to-phase loop's rule):
        // j0.21 = 1.05 x the line's 0.2 pu, inside
        // zone 2 (1.25 x). The positive-sequence apparent impedance there is 0.72 pu, outside every zone.
        GridModel m = Radial();
        (UnbalancedFaultResult _, ProtectionReport rep) = ProtectionAssessment.AssessUnbalancedFault(m, Prefault(m), 3, FaultType.LineToLine, faultReactancePu: 0.02);
        RelayVerdict v51 = Assert.Single(rep.Operating, v => v.Device == "51" && v.LocationName == "L23");
        Assert.Equal(Math.Sqrt(3.0) / 1.02, v51.Quantity, 12);
        RelayVerdict v21 = Assert.Single(rep.Operating, v => v.Device == "21-Z2" && v.LocationName == "L23");
        Assert.Equal(0.21, v21.Quantity, 12);
        Assert.Contains("[b-c loop]", v21.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void A_power_flow_and_a_balanced_fault_are_assessed_exactly_as_before()
    {
        // Without a sequence study the phase quantities ARE the positive-sequence ones: the phase-voltage arrays hold
        // Vm's values and the currents are S / |V|.
        GridModel m = Radial();
        PowerFlowResult pre = Prefault(m);
        var ctx = new ProtectionContext(m, pre, new ProtectionSettings(), null);
        for (int i = 0; i < ctx.Vm.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(ctx.Vm[i]), BitConverter.DoubleToInt64Bits(ctx.VmPhaseMinPu[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(ctx.Vm[i]), BitConverter.DoubleToInt64Bits(ctx.VmPhaseMaxPu[i]));
        }
        (FaultResult f, ProtectionReport rep) = ProtectionAssessment.AssessThreePhaseFault(m, pre, 3);
        var ctx3 = new ProtectionContext(m, f.State, new ProtectionSettings(), null, f.DifferentialCurrentPuByBus);
        Assert.Equal(ctx3.Vm, ctx3.VmPhaseMinPu);
        Assert.DoesNotContain(rep.Verdicts, v => v.Basis.Contains(" loop]", StringComparison.Ordinal));
    }

    [Fact]
    public void Converter_units_add_no_fault_current_in_the_unbalanced_study_and_are_named()
    {
        // A 50 MVA converter unit at bus 3 with no output: the prefault state is unchanged, and the unbalanced study,
        // which has no defined negative- or zero-sequence behaviour for it, leaves it open in all three networks.
        var battery = new Generator(3, 0, MBase: 50, Name: "BESS", Type: "storage", ConverterInterfaced: true);
        GridModel with = Radial(battery);
        GridModel without = Radial();
        UnbalancedFaultResult a = FaultAnalysis.Unbalanced(with, Prefault(with), 3, FaultType.SingleLineToGround);
        UnbalancedFaultResult b = FaultAnalysis.Unbalanced(without, Prefault(without), 3, FaultType.SingleLineToGround);
        Assert.Equal(b.I1, a.I1);
        Assert.Equal(b.EarthCurrentPu, a.EarthCurrentPu);
        ConverterWithoutFaultInfeed c = Assert.Single(a.ConvertersWithoutFaultInfeed);
        Assert.Equal((3, "BESS", 50.0), (c.Bus, c.Name, c.RatedMva));
        Assert.Empty(b.ConvertersWithoutFaultInfeed);
    }

    [Fact]
    public void The_cli_says_so_on_both_unbalanced_paths()
    {
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("ConvertersWithoutInfeedNote(ufault);", src, StringComparison.Ordinal);   // protection-check
        Assert.Contains("ConvertersWithoutInfeedNote(uf);", src, StringComparison.Ordinal);       // fault --type slg|ll|dlg
        Assert.Contains("the model defines no negative- or zero-sequence fault behaviour for them", src, StringComparison.Ordinal);
    }
}
