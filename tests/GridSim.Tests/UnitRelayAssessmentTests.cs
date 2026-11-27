using System.Numerics;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 per-unit protection, stage 2 (passive assessment): each unit's own relays judged with its own resolved settings,
/// every verdict naming the unit, the relay stage and the grade and source of its settings, and a model without unit
/// protection giving exactly the verdicts it gave before.
/// </summary>
public sealed class UnitRelayAssessmentTests
{
    private static string CaseDir => Path.Combine(RepoRoot(), "tests", "GridSim.Tests", "fixtures", "unit-protection");
    private static readonly PowerFlowOptions Opt = new() { MaxIterations = 100, EnforceQLimits = true };

    private static (GridModel Model, PowerFlowResult Result) Solved(string manifest)
    {
        GridModel m = V2CaseLoader.Load(Path.Combine(CaseDir, manifest), SchemasDir);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, Opt);
        Assert.True(r.Converged);
        return (m, r);
    }

    private static RelayVerdict Unit(ProtectionReport rep, string unit, string device) =>
        rep.Verdicts.Single(v => v.UnitStage is not null && v.LocationName == unit && v.Device == device);

    [Fact]
    public void Without_unit_protection_the_report_is_the_network_report_and_with_it_the_unit_verdicts_follow_unchanged_network_rows()
    {
        (GridModel plain, PowerFlowResult pr) = Solved("plain.case.json");
        (GridModel withUp, PowerFlowResult ur) = Solved("three-unit.case.json");
        ProtectionReport a = ProtectionAssessment.Assess(plain, pr);
        ProtectionReport b = ProtectionAssessment.Assess(withUp, ur);
        Assert.All(a.Verdicts, v => Assert.Null(v.UnitStage));
        Assert.Equal(a.Verdicts, b.Verdicts.Take(a.Verdicts.Count));
        Assert.All(b.Verdicts.Skip(a.Verdicts.Count), v => Assert.NotNull(v.UnitStage));
        Assert.Equal(15, b.Verdicts.Count - a.Verdicts.Count);   // CCGT B: 7 stages, Embedded C: 8
    }

    [Fact]
    public void A_bundled_case_has_no_unit_protection_and_no_unit_verdict()
    {
        GridModel m = GridSim.Core.IO.IeeeCases.Case9();
        Assert.Null(m.UnitProtection);
        ProtectionReport rep = ProtectionAssessment.Assess(m, new NewtonRaphsonPowerFlow().Solve(m, Opt));
        Assert.All(rep.Verdicts, v => Assert.Null(v.UnitStage));
    }

    [Fact]
    public void A_motoring_unit_trips_on_its_own_reverse_power_setting_and_the_verdict_names_unit_relay_and_sources()
    {
        (GridModel m, PowerFlowResult r) = Solved("three-unit.case.json");
        ProtectionReport rep = ProtectionAssessment.Assess(m, r);
        RelayVerdict v = Unit(rep, "Embedded C", "32");
        Assert.True(v.Operate);
        Assert.Equal(ProtectionLocation.Generator, v.LocationKind);
        Assert.Equal(-20.0, v.Quantity);
        Assert.Equal(-2.5, v.Pickup);          // 0.05 pu of 50 MVA
        Assert.Equal(17.5, v.Margin);
        Assert.Equal(2.0, v.TimeToTripS);
        Assert.Equal(ProtectionConfidence.Grounded, v.Confidence);   // both settings the unit's own (Synthetic, not a placeholder)
        Assert.StartsWith("unit Embedded C relay 32: the unit absorbs 20 MW", v.Basis, StringComparison.Ordinal);
        Assert.Contains("settings: pickup 0.05 pu of rating (sourced, unit, Synthetic); delay 2 s (sourced, unit, Synthetic)", v.Basis, StringComparison.Ordinal);
        Assert.Contains(v, rep.Operating);

        // CCGT B exports, so its own 32 (class default, a placeholder) holds and is graded Generic.
        RelayVerdict b = Unit(rep, "CCGT B", "32");
        Assert.False(b.Operate);
        Assert.Equal(ProtectionConfidence.Generic, b.Confidence);
        Assert.Contains("(placeholder, template 'ccgt-placeholder', CuratedPlaceholder)", b.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_steady_state_frequency_holds_and_rocof_and_vector_shift_are_not_checkable()
    {
        (GridModel m, PowerFlowResult r) = Solved("three-unit.case.json");
        ProtectionReport rep = ProtectionAssessment.Assess(m, r);
        RelayVerdict uf = Unit(rep, "CCGT B", "81U-1");
        Assert.False(uf.Operate);
        Assert.Equal(50.0, uf.Quantity);
        Assert.Equal(47.5, uf.Pickup);
        Assert.Contains("pickup 47.5 Hz (sourced, unit, Synthetic); delay 2 s (placeholder, template 'ccgt-placeholder', CuratedPlaceholder)", uf.Basis, StringComparison.Ordinal);
        RelayVerdict rocof = Unit(rep, "CCGT B", "81R");
        Assert.Equal(ProtectionConfidence.NotCheckable, rocof.Confidence);
        Assert.Contains("(sourced, unit, preset rocof 'modern', Recorded)", rocof.Basis, StringComparison.Ordinal);
        RelayVerdict iface = Unit(rep, "Embedded C", "81R");
        Assert.Contains("(sourced, template 'g99-embedded', preset interface 'g99-hv', Recorded)", iface.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void A_voltage_stage_reads_the_unit_terminal_bus()
    {
        (GridModel m, PowerFlowResult r) = Solved("three-unit.case.json");
        ProtectionReport rep = ProtectionAssessment.Assess(m, r);
        int bus3 = r.BusIds.ToList().IndexOf(3);
        RelayVerdict v = Unit(rep, "Embedded C", "27-1");
        Assert.Equal(r.Vm[bus3], v.Quantity);
        Assert.Equal(0.8, v.Pickup);
        Assert.Equal(v.Quantity < 0.8, v.Operate);
    }

    [Fact]
    public void Loss_of_excitation_judges_the_apparent_impedance_in_the_unit_base_against_the_offset_mho_circle()
    {
        (GridModel m, PowerFlowResult r) = Solved("three-unit.case.json");
        ProtectionReport rep = ProtectionAssessment.Assess(m, r);
        int g = m.Generators.ToList().FindIndex(x => x.Name == "CCGT B");
        (double p, double q) = SolvedGeneration.UnitOutput(m, r, g);
        double vm = r.Vm[r.BusIds.ToList().IndexOf(2)];
        double k = vm * vm * 150.0 / (p * p + q * q);
        double d = (new Complex(k * p, k * q) - new Complex(0.0, -(0.1 + 0.5))).Magnitude;
        RelayVerdict v = Unit(rep, "CCGT B", "40-1");
        Assert.Equal(d, v.Quantity);
        Assert.Equal(0.5, v.Pickup);
        Assert.Equal(d < 0.5, v.Operate);
        Assert.Contains("a steady operating point, not a field-failure transient", v.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fault_snapshot_reports_a_delayed_stage_beyond_pickup_as_not_checkable_never_as_a_trip()
    {
        (GridModel m, PowerFlowResult r) = Solved("three-unit.case.json");
        (FaultResult _, ProtectionReport rep) = ProtectionAssessment.AssessThreePhaseFault(m, r, 3);
        RelayVerdict v = Unit(rep, "Embedded C", "27-1");
        Assert.False(v.Operate);
        Assert.Equal(ProtectionConfidence.NotCheckable, v.Confidence);
        Assert.Contains("is below the 0.8 pu pickup, but a fault snapshot carries no duration, so whether it persists for the 2.5 s delay cannot be judged", v.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_trajectory_an_instantaneous_stage_operates_and_a_delayed_one_beyond_pickup_is_not_checkable()
    {
        GridModel m = V2CaseLoader.Load(Path.Combine(CaseDir, "three-unit.case.json"), SchemasDir);
        var traj = new ProtectionTrajectory(MaxAbsRocofHzPerS: 1.2, MinFrequencyHz: 47.4, MaxFrequencyHz: 50.0, MaxAbsVectorShiftDeg: double.NaN);
        IReadOnlyList<RelayVerdict> vs = UnitRelayAssessment.AssessTrajectory(m.UnitProtection!, traj);
        RelayVerdict uf = vs.Single(v => v.LocationName == "CCGT B" && v.Device == "81U-1");
        Assert.Equal(ProtectionConfidence.NotCheckable, uf.Confidence);
        Assert.Contains("run minimum frequency 47.4 Hz is below the 47.5 Hz pickup, but a run summary (its extremes) carries no duration", uf.Basis, StringComparison.Ordinal);
        RelayVerdict of = vs.Single(v => v.LocationName == "CCGT B" && v.Device == "81O-1");
        Assert.False(of.Operate);
        Assert.NotEqual(ProtectionConfidence.NotCheckable, of.Confidence);
        Assert.Contains(vs, v => v.Device == "27-1" && v.Confidence == ProtectionConfidence.NotCheckable && v.Basis.Contains("no solved network", StringComparison.Ordinal));

        // A unit with an instantaneous RoCoF stage of its own (0.125 Hz/s, no intentional delay) trips at once on the same run.
        var legacy = new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[]
        {
            new UnitProtectionAssignment("Old", null, new UnitProtectionLayer(
                new Dictionary<string, double> { ["rocofHzPerS"] = 0.125, ["rocofDelayS"] = 0.0 },
                new Dictionary<string, GridSim.Core.Assets.AssetClassProvenance>
                {
                    ["rocofHzPerS"] = new(GridSim.Core.Diagnostics.ProvenanceTag.Synthetic, "test"),
                    ["rocofDelayS"] = new(GridSim.Core.Diagnostics.ProvenanceTag.Synthetic, "test"),
                })),
        });
        RelayVerdict lr = UnitRelayAssessment.AssessTrajectory(legacy, traj).Single();
        Assert.True(lr.Operate);
        Assert.Equal(1.2 - 0.125, lr.Margin);
    }

    [Fact]
    public void A_unit_whose_settings_are_for_another_nominal_frequency_is_not_checkable_on_frequency()
    {
        var set = new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[]
        {
            new UnitProtectionAssignment("U60", null, new UnitProtectionLayer(
                new Dictionary<string, double> { ["uf1Hz"] = 57.0, ["uf1DelayS"] = 1.0, ["relayNominalHz"] = 60.0 },
                new Dictionary<string, GridSim.Core.Assets.AssetClassProvenance>
                {
                    ["uf1Hz"] = new(GridSim.Core.Diagnostics.ProvenanceTag.Synthetic, "test"),
                    ["uf1DelayS"] = new(GridSim.Core.Diagnostics.ProvenanceTag.Synthetic, "test"),
                    ["relayNominalHz"] = new(GridSim.Core.Diagnostics.ProvenanceTag.Synthetic, "test"),
                })),
        });
        RelayVerdict v = UnitRelayAssessment.AssessTrajectory(set, new ProtectionTrajectory(0.1, 49.0, 50.0)).Single();
        Assert.Equal(ProtectionConfidence.NotCheckable, v.Confidence);
        Assert.Contains("written for a 60 Hz system and this system is 50 Hz", v.Basis, StringComparison.Ordinal);
    }
}
