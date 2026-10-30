using System.Linq;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The passive protection-assessment overlay (<see cref="ProtectionAssessment"/>): it reads a solved state
/// and reports which relays WOULD operate, never altering the solve. Pins the threshold logic per element,
/// the inverse-time behaviour, the trajectory gating of the frequency-derivative relays, and the honest
/// not-checkable verdict for earth/negative-sequence elements.
/// </summary>
public class ProtectionAssessmentTests
{
    private static PowerFlowResult Solve(GridModel m) => new NewtonRaphsonPowerFlow().Solve(m);

    private static GridModel WithBranches(GridModel m, System.Collections.Generic.IReadOnlyList<Branch> branches) => new()
    {
        BaseMva = m.BaseMva,
        Buses = m.Buses,
        Generators = m.Generators,
        Branches = branches,
        Oltcs = m.Oltcs,
        Boundaries = m.Boundaries,
        FrequencyHz = m.FrequencyHz,
        Name = m.Name,
        AssetDynamics = m.AssetDynamics,
        Protections = m.Protections,
    };

    [Fact]
    public void Healthy_case_in_band_has_no_voltage_or_differential_operations()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult r = Solve(m);
        ProtectionReport rep = ProtectionAssessment.Assess(m, r);

        Assert.DoesNotContain(rep.Operating, v => v.Device == "27"); // no undervoltage
        Assert.DoesNotContain(rep.Operating, v => v.Device == "59"); // no overvoltage
        Assert.DoesNotContain(rep.Operating, v => v.Device == "87"); // KCL balances -> no differential
    }

    [Fact]
    public void Undervoltage_and_overvoltage_pick_up_when_the_band_is_tightened_around_the_state()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult r = Solve(m);

        // Squeeze the band inside the real voltage spread: every bus is now "out of band" one way or another.
        var tight = new ProtectionSettings { UnderVoltagePu = 1.06, OverVoltagePu = 0.94 };
        ProtectionReport rep = ProtectionAssessment.Assess(m, r, tight);

        Assert.Contains(rep.Operating, v => v.Device == "27");
        Assert.Contains(rep.Operating, v => v.Device == "59");
        // The basis names the bus and the measured value.
        RelayVerdict uv = rep.Operating.First(v => v.Device == "27");
        Assert.Equal(ProtectionLocation.Bus, uv.LocationKind);
        Assert.True(uv.Quantity < tight.UnderVoltagePu);
    }

    [Fact]
    public void Thermal_and_overcurrent_operate_on_a_branch_rated_below_its_flow()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult probe = Solve(m);
        // Busiest branch by apparent power.
        LineFlow busiest = probe.LineFlows.OrderByDescending(
            l => l.PFromMw * l.PFromMw + l.QFromMvar * l.QFromMvar).First();
        double sMva = System.Math.Sqrt(busiest.PFromMw * busiest.PFromMw + busiest.QFromMvar * busiest.QFromMvar);

        // Rate it well below its flow -> heavy overload, ~2.7x rated current (past the 2.5x 50 pickup).
        var branches = m.Branches.Select(br =>
            br.From == busiest.From && br.To == busiest.To ? br with { RateAMva = 0.35 * sMva } : br).ToList();
        GridModel stressed = WithBranches(m, branches);
        PowerFlowResult r = Solve(stressed);
        ProtectionReport rep = ProtectionAssessment.Assess(stressed, r);

        RelayVerdict thermal = rep.Operating.First(v => v.Device == "49");
        Assert.True(thermal.Quantity > 1.0, "loading fraction should exceed 1.0");
        Assert.True(thermal.TimeToTripS is > 0, "thermal replica gives a finite operating time");

        Assert.Contains(rep.Operating, v => v.Device == "50"); // instantaneous overcurrent
        RelayVerdict inv = rep.Operating.First(v => v.Device == "51");
        Assert.True(inv.TimeToTripS is > 0, "IEC inverse-time gives a finite operating time");
    }

    [Fact]
    public void Time_overcurrent_is_faster_at_higher_current()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult probe = Solve(m);
        LineFlow busiest = probe.LineFlows.OrderByDescending(
            l => l.PFromMw * l.PFromMw + l.QFromMvar * l.QFromMvar).First();
        double sMva = System.Math.Sqrt(busiest.PFromMw * busiest.PFromMw + busiest.QFromMvar * busiest.QFromMvar);

        double TripTime(double rateFraction)
        {
            var branches = m.Branches.Select(br =>
                br.From == busiest.From && br.To == busiest.To ? br with { RateAMva = rateFraction * sMva } : br).ToList();
            GridModel s = WithBranches(m, branches);
            ProtectionReport rep = ProtectionAssessment.Assess(s, Solve(s));
            return rep.Operating.First(v => v.Device == "51").TimeToTripS!.Value;
        }

        double tHarder = TripTime(0.30); // ~3.3x rated
        double tSofter = TripTime(0.60); // ~1.7x rated
        Assert.True(tHarder < tSofter, $"more current must trip sooner: {tHarder} vs {tSofter}");
    }

    [Fact] // Wave 3: IEC 51 curve argument is M = I/pickup (not I/rated)
    public void Time_overcurrent_uses_pickup_multiple_not_rated_multiple()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult probe = Solve(m);
        LineFlow busiest = probe.LineFlows.OrderByDescending(
            l => l.PFromMw * l.PFromMw + l.QFromMvar * l.QFromMvar).First();
        double sMva = System.Math.Sqrt(busiest.PFromMw * busiest.PFromMw + busiest.QFromMvar * busiest.QFromMvar);

        // Load the busiest branch hard (~3.3x rated) so the 51 element definitely operates.
        var branches = m.Branches.Select(br =>
            br.From == busiest.From && br.To == busiest.To ? br with { RateAMva = 0.30 * sMva } : br).ToList();
        GridModel s = WithBranches(m, branches);
        RelayVerdict oc = ProtectionAssessment.Assess(s, Solve(s)).Operating.First(v => v.Device == "51");

        // The verdict reports the multiple-of-rated (Quantity) and the pickup (Pickup). The IEC
        // standard-inverse time must use M = Quantity/Pickup, not Quantity, with the default TMS 0.10.
        double mult = oc.Pickup, mm = oc.Quantity, tms = 0.10;
        double expectedCorrect = tms * 0.14 / (System.Math.Pow(mm / mult, 0.02) - 1.0);
        double oldWrong = tms * 0.14 / (System.Math.Pow(mm, 0.02) - 1.0);

        Assert.NotNull(oc.TimeToTripS);
        Assert.Equal(expectedCorrect, oc.TimeToTripS!.Value, 3);
        Assert.True(System.Math.Abs(oc.TimeToTripS.Value - oldWrong) > 1e-3,
            "operating time must follow the I/pickup curve, not the old I/rated curve");
    }

    [Fact]
    public void Frequency_and_rocof_relays_need_a_trajectory_and_then_operate()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult r = Solve(m);

        // No trajectory: RoCoF and loss-of-mains are honestly not-checkable, and do not operate.
        ProtectionReport steady = ProtectionAssessment.Assess(m, r);
        Assert.All(steady.Verdicts.Where(v => v.Device is "81R" or "78"),
            v => Assert.Equal(ProtectionConfidence.NotCheckable, v.Confidence));
        Assert.DoesNotContain(steady.Operating, v => v.Device is "81R" or "78");

        // A dynamic run that dipped to 47.0 Hz at 2.0 Hz/s trips 81U and 81R.
        var traj = new ProtectionTrajectory(MaxAbsRocofHzPerS: 2.0, MinFrequencyHz: 47.0, MaxFrequencyHz: 50.1, MaxAbsVectorShiftDeg: 15.0);
        ProtectionReport dyn = ProtectionAssessment.Assess(m, r, null, traj);
        Assert.Contains(dyn.Operating, v => v.Device == "81U");
        Assert.Contains(dyn.Operating, v => v.Device == "81R");
        Assert.Contains(dyn.Operating, v => v.Device == "78");
    }

    [Fact]
    public void Reverse_power_flags_a_motoring_generator()
    {
        GridModel m = IeeeCases.Case14();
        // Force one generator negative (absorbing).
        var gens = m.Generators.Select((g, i) => i == 1 ? g with { Pg = -20.0 } : g).ToList();
        GridModel motoring = new()
        {
            BaseMva = m.BaseMva, Buses = m.Buses, Generators = gens, Branches = m.Branches,
            Oltcs = m.Oltcs, Boundaries = m.Boundaries, FrequencyHz = m.FrequencyHz, Name = m.Name,
            AssetDynamics = m.AssetDynamics, Protections = m.Protections,
        };
        PowerFlowResult r = Solve(motoring);
        ProtectionReport rep = ProtectionAssessment.Assess(motoring, r);
        Assert.Contains(rep.Operating, v => v.Device == "32");
    }

    [Fact]
    public void Earth_and_negative_sequence_are_reported_not_checkable()
    {
        GridModel m = IeeeCases.Case14();
        ProtectionReport rep = ProtectionAssessment.Assess(m, Solve(m));
        RelayVerdict ground = Assert.Single(rep.Verdicts, v => v.Function.Contains("Ground"));
        Assert.Equal(ProtectionConfidence.NotCheckable, ground.Confidence);
        Assert.False(ground.Operate);
    }

    [Fact]
    public void Assessment_does_not_mutate_the_solved_result()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult r = Solve(m);
        double[] vmBefore = (double[])r.Vm.Clone();
        _ = ProtectionAssessment.Assess(m, r, new ProtectionSettings { UnderVoltagePu = 1.1 });
        Assert.Equal(vmBefore, r.Vm);
    }
}
