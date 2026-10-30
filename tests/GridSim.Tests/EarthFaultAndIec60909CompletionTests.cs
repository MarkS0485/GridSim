using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F21 (October 2026): unbalanced faults in the operating-point fault study (single line to ground, line to line,
/// double line to ground on the three sequence networks), the earth-fault and negative-sequence relay elements (50N,
/// 51N, 21N, 46), and the IEC 60909 completions (K_S / K_SO, the opt-in application of K_G / K_S / K_T).
/// The closed forms checked are the textbook sequence-network connections (Grainger and Stevenson, Power System
/// Analysis, ch. 12) and the IEC 60909-0 correction formulas as written in the code comments (IEC 60909-0 is not in
/// docs/reference; the K_S / K_SO forms carry a [VERIFY] note).
/// </summary>
public class EarthFaultAndIec60909CompletionTests
{
    // Bus 1: a generator (X''d 0.2 on 100 MVA). Transformer 1-2 delta (bus 1) / grounded wye (bus 2), x 0.1. Line 2-3,
    // x 0.2, no charging, no resistance. No load: the prefault state is 1.0 pu everywhere, so every closed form below is
    // exact. Zero sequence: the transformer shunts bus 2 to earth through x 0.1; the line's Z0 = 3 x 0.2.
    private static GridModel Radial(bool grounded = true) => new()
    {
        Name = "radial",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 20), new Bus(2, BusType.Pq, BaseKv: 132), new Bus(3, BusType.Pq, BaseKv: 132) },
        Generators = new[] { new Generator(1, 0, Vg: 1.0, MBase: 100, SubtransientXdPu: 0.2) },
        Branches = new[]
        {
            new Branch(1, 2, 0, 0.1, Name: "T1", IsTransformer: true, RateAMva: 100,
                FromWinding: WindingConnection.Delta, ToWinding: grounded ? WindingConnection.WyeGrounded : WindingConnection.WyeUngrounded),
            new Branch(2, 3, 0, 0.2, Name: "L23", RateAMva: 100),
        },
    };

    private static PowerFlowResult Prefault(GridModel m)
    {
        PowerFlowResult r = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(r.Converged);
        return r;
    }

    private static readonly Complex Z1 = new(0, 0.2 + 0.1 + 0.2);   // generator + transformer + line, positive (= negative)
    private static readonly Complex Z0 = new(0, 0.1 + 3 * 0.2);     // transformer shunt + 3 x line

    [Fact]
    public void Single_line_to_ground_matches_the_series_connection_closed_form()
    {
        GridModel m = Radial();
        UnbalancedFaultResult r = FaultAnalysis.Unbalanced(m, Prefault(m), 3, FaultType.SingleLineToGround);
        Assert.True(r.FaultBusGrounded);
        Complex i = 1.0 / (Z1 + Z1 + Z0);
        Assert.Equal((3.0 * i).Magnitude, r.PhaseAPu, 12);
        Assert.Equal(0.0, r.PhaseBPu, 12);
        Assert.Equal(0.0, r.PhaseCPu, 12);
        Assert.Equal((3.0 * i).Magnitude, r.EarthCurrentPu, 12);
        Assert.Equal(Z0.Imaginary, r.Z0Pu.Imaginary, 12);
        (double va, _, _) = r.FaultBusPhaseVoltages();
        Assert.Equal(0.0, va, 12);   // a bolted earth fault holds the faulted phase at 0
        // The line carries the whole residual 3 I0, the generator side of the delta none.
        BranchSequenceCurrents line = r.Branches[1];
        Assert.Equal((3.0 * i).Magnitude, line.ResidualFromPu, 12);
        Assert.Equal(0.0, r.Branches[0].ResidualFromPu, 12);
    }

    [Fact]
    public void Line_to_line_and_double_line_to_ground_match_their_closed_forms()
    {
        GridModel m = Radial();
        PowerFlowResult pre = Prefault(m);
        UnbalancedFaultResult ll = FaultAnalysis.Unbalanced(m, pre, 3, FaultType.LineToLine);
        Complex i1 = 1.0 / (Z1 + Z1);
        Assert.Equal(Math.Sqrt(3.0) * i1.Magnitude, ll.PhaseBPu, 12);
        Assert.Equal(Math.Sqrt(3.0) * i1.Magnitude, ll.PhaseCPu, 12);
        Assert.Equal(0.0, ll.EarthCurrentPu, 12);

        UnbalancedFaultResult dlg = FaultAnalysis.Unbalanced(m, pre, 3, FaultType.DoubleLineToGround);
        Complex d1 = 1.0 / (Z1 + Z1 * Z0 / (Z1 + Z0));
        Complex d0 = -d1 * Z1 / (Z1 + Z0);
        Assert.Equal((3.0 * d0).Magnitude, dlg.EarthCurrentPu, 12);
        Assert.Equal(d1.Magnitude, dlg.I1.Magnitude, 12);
    }

    [Fact]
    public void Without_an_earth_path_there_is_no_earth_current_and_dlg_is_line_to_line()
    {
        GridModel m = Radial(grounded: false);
        PowerFlowResult pre = Prefault(m);
        UnbalancedFaultResult slg = FaultAnalysis.Unbalanced(m, pre, 3, FaultType.SingleLineToGround);
        Assert.False(slg.FaultBusGrounded);
        Assert.Equal(0.0, slg.PhaseAPu);
        UnbalancedFaultResult dlg = FaultAnalysis.Unbalanced(m, pre, 3, FaultType.DoubleLineToGround);
        UnbalancedFaultResult ll = FaultAnalysis.Unbalanced(m, pre, 3, FaultType.LineToLine);
        Assert.Equal(ll.PhaseBPu, dlg.PhaseBPu);
        Assert.Equal(0.0, dlg.EarthCurrentPu);
    }

    [Fact]
    public void With_no_load_or_charging_the_operating_point_slg_current_times_c_is_the_iec_60909_initial_current()
    {
        // Same networks (60909 drops charging and loads; this case has neither), source c x 1 pu against 1 pu prefault.
        GridModel m = Radial();
        UnbalancedFaultResult r = FaultAnalysis.Unbalanced(m, Prefault(m), 3, FaultType.SingleLineToGround);
        Iec60909.Duty d = Iec60909.SingleLineToGround(m, 3);
        Assert.Equal(d.IkppPu, 1.1 * r.PhaseAPu, 12);
        Iec60909.Duty dl = Iec60909.DoubleLineToGround(m, 3);
        UnbalancedFaultResult rl = FaultAnalysis.Unbalanced(m, Prefault(m), 3, FaultType.DoubleLineToGround);
        Assert.Equal(dl.IkppPu, 1.1 * rl.EarthCurrentPu, 12);
    }

    [Fact]
    public void The_earth_fault_elements_operate_on_an_earth_fault_and_stay_not_checkable_on_a_power_flow()
    {
        GridModel m = Radial();
        PowerFlowResult pre = Prefault(m);
        (UnbalancedFaultResult _, ProtectionReport rep) = ProtectionAssessment.AssessUnbalancedFault(m, pre, 3, FaultType.SingleLineToGround);
        Assert.Contains(rep.Operating, v => v.Device == "50N" && v.LocationName == "L23");
        Assert.Contains(rep.Operating, v => v.Device == "51N" && v.LocationName == "L23" && v.TimeToTripS > 0);
        Assert.Contains(rep.Operating, v => v.Device == "46" && v.LocationName == "L23");
        Assert.Contains(rep.Operating, v => v.Device.StartsWith("21N-Z", StringComparison.Ordinal) && v.LocationName == "L23");
        Assert.DoesNotContain(rep.Verdicts, v => v.Confidence == ProtectionConfidence.NotCheckable && v.Device == "50N/51N/21N/46");

        // A line-to-line fault has no residual: 50N / 51N do not operate, 46 does.
        (_, ProtectionReport llRep) = ProtectionAssessment.AssessUnbalancedFault(m, pre, 3, FaultType.LineToLine);
        Assert.DoesNotContain(llRep.Operating, v => v.Device is "50N" or "51N");
        Assert.Contains(llRep.Operating, v => v.Device == "46");

        // On the power flow itself the element is reported not-checkable, exactly as before.
        ProtectionReport steady = ProtectionAssessment.Assess(m, pre);
        RelayVerdict ground = Assert.Single(steady.Verdicts, v => v.Device == "50N/51N/21N/46");
        Assert.Equal(ProtectionConfidence.NotCheckable, ground.Confidence);
    }

    [Fact]
    public void A_three_phase_type_is_refused_by_the_unbalanced_entry_point()
    {
        GridModel m = Radial();
        Assert.Throws<ArgumentException>(() => FaultAnalysis.Unbalanced(m, Prefault(m), 3, FaultType.ThreePhase));
    }

    // ---- IEC 60909 correction factors -------------------------------------------------------------------------------

    [Fact]
    public void Ks_and_kso_follow_their_closed_forms()
    {
        double sin = Math.Sqrt(1 - 0.85 * 0.85);
        Assert.Equal(1.1 / (1 + Math.Abs(0.2 - 0.12) * sin), Iec60909.PowerStationUnitCorrectionKs(1, 1, 1, 1, 0.2, 0.12, 0.85, 1.1), 15);
        // With voltage ratios: (UnQ/UrG)^2 (UrTLV/UrTHV)^2.
        Assert.Equal((400.0 / 21.0) * (400.0 / 21.0) * (21.0 / 420.0) * (21.0 / 420.0) * 1.1 / (1 + 0.08 * sin),
            Iec60909.PowerStationUnitCorrectionKs(400, 21, 21, 420, 0.2, 0.12, 0.85, 1.1), 12);
        Assert.Equal(400.0 / (21.0 * 1.05) * (21.0 / 420.0) * 0.975 * 1.1 / (1 + 0.2 * sin),
            Iec60909.PowerStationUnitCorrectionKso(400, 21, 0.05, 21, 420, 0.975, 0.2, 0.85, 1.1), 12);
    }

    [Fact]
    public void Applying_the_corrections_scales_the_generator_and_transformer_impedances_and_leaves_the_input_untouched()
    {
        GridModel m = Radial();
        Iec60909.CorrectionReport cr = Iec60909.ApplyCorrectionFactors(m, 0.85);
        double sin = Math.Sqrt(1 - 0.85 * 0.85);
        double kg = 1.1 / (1 + 0.2 * sin);                 // bus 1 is 20 kV: c_max 1.10
        double kt = 0.95 * 1.1 / (1 + 0.6 * 0.1);          // x_T = 0.1 x 100 MVA / 100 MVA
        Assert.Equal(kg * 0.2, cr.Model.Generators[0].SubtransientXdPu, 15);
        Assert.Equal(kg * 0.2, cr.Model.Generators[0].NegativeSeqXPu, 15);
        Assert.Equal(kt * 0.1, cr.Model.Branches[0].X, 15);
        Assert.Equal(0.2, cr.Model.Branches[1].X);         // a line is not corrected
        Assert.Equal(0.2, m.Generators[0].SubtransientXdPu);   // the input model is unchanged
        Assert.Contains(cr.Applied, x => x.Factor == "K_G");
        Assert.Contains(cr.Applied, x => x.Factor == "K_T");
        Assert.Empty(cr.NotApplied);

        // A power-station unit (unit transformer modelled) takes K_S on the sum.
        GridModel unit = m.CopyWith(generators: new[] { m.Generators[0] with { UnitTransformerXPu = 0.12 } });
        Iec60909.CorrectionReport cu = Iec60909.ApplyCorrectionFactors(unit, 0.85);
        double ks = 1.1 / (1 + Math.Abs(0.2 - 0.12) * sin);
        Assert.Equal(ks * 0.12, cu.Model.Generators[0].UnitTransformerXPu, 15);
        Assert.Contains(cu.Applied, x => x.Factor == "K_S");
        Iec60909.CorrectionReport cso = Iec60909.ApplyCorrectionFactors(unit, 0.85, unitTransformersHaveOltc: false);
        Assert.Contains(cso.Applied, x => x.Factor == "K_SO");

        // A transformer with no rating cannot be corrected: listed, not guessed.
        GridModel unrated = m.CopyWith(branches: new[] { m.Branches[0] with { RateAMva = 0 }, m.Branches[1] });
        Assert.Single(Iec60909.ApplyCorrectionFactors(unrated, 0.85).NotApplied);
        Assert.Throws<ArgumentOutOfRangeException>(() => Iec60909.ApplyCorrectionFactors(m, 0.0));
    }

    [Fact]
    public void The_zero_sequence_thevenin_is_unchanged_by_the_shared_builder()
    {
        // ZeroSequenceThevenin now builds its network through BuildZeroSequenceNetwork; on the radial case the
        // driving-point Z0 at bus 3 is the closed form j(0.1 + 0.6) exactly as before.
        (Complex z0, double kv, bool grounded) = Iec60909.ZeroSequenceThevenin(Radial(), 3);
        Assert.True(grounded);
        Assert.Equal(132.0, kv);
        Assert.Equal(0.7, z0.Imaginary, 15);
        Assert.Equal(0.0, z0.Real, 15);
    }

    // ---- CLI wiring (source level) ---------------------------------------------------------------------------------

    [Fact]
    public void The_fault_iec60909_and_protection_check_flags_are_known_and_documented()
    {
        string root = CliProcess.Root();
        string src = File.ReadAllText(Path.Combine(root, "src", "GridSim.Cli", "Program.cs"));
        foreach (string flag in new[] { "--fault-type", "--breaker", "--branches", "--all-buses", "--corrections", "--rated-pf", "--no-unit-oltc" })
            Assert.Contains($"\"{flag}\",", src, StringComparison.Ordinal);
        Assert.Contains("\"3ph\" or \"slg\" or \"ll\" or \"dlg\"", src, StringComparison.Ordinal);
        string ch19 = File.ReadAllText(Path.Combine(root, "docs", "manual", "0019 CLI reference.md"));
        foreach (string flag in new[] { "`--fault-type`", "`--breaker`", "`--branches`", "`--all-buses`", "`--corrections`", "`--rated-pf`", "`--no-unit-oltc`" })
            Assert.Contains(flag, ch19, StringComparison.Ordinal);
        Assert.Contains("[--type 3ph|slg|ll|dlg]", ch19, StringComparison.Ordinal);
    }
}
