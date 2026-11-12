using System;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-144 (b): the zero-sequence network pairs a transformer's windings two at a time, so a three-winding unit (a
/// star-point bus with one transformer branch per winding) was read winding by winding against the fictitious star point,
/// and an unknown or one-sided winding connection was read as "blocks" or as a line, all without a word. An earth-fault
/// study (single or double line to ground) on such a model is now refused, naming the units; three-phase and
/// line-to-line studies are not refused, and a model with no earthing data at all is left to the CLI's missing-data
/// report (exit 3) as before.
/// </summary>
public class Gr144EarthFaultUnrepresentableZeroSequenceTests
{
    /// <summary>A 132/33/11 kV three-winding unit as every reader builds it: buses 1 (HV, the slack machine), 2 (MV) and
    /// 3 (LV) joined through star point 4 by three transformer legs. With <paramref name="windings"/> the legs carry
    /// YNyn on HV and MV and a delta LV winding; without, they are transformers with no winding data.</summary>
    private static GridModel ThreeWinding(bool windings) => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132),
            new Bus(2, BusType.Pq, Pd: 10, Qd: 2, BaseKv: 33),
            new Bus(3, BusType.Pq, Pd: 5, Qd: 1, BaseKv: 11),
            new Bus(4, BusType.Pq, BaseKv: 132, Name: "T1 star point"),
        },
        Branches = new[]
        {
            new Branch(1, 4, 0.001, 0.05, IsTransformer: true, Name: "T1 winding 1",
                FromWinding: windings ? WindingConnection.WyeGrounded : null, ToWinding: windings ? WindingConnection.WyeGrounded : null),
            new Branch(2, 4, 0.001, 0.03, IsTransformer: true, Name: "T1 winding 2",
                FromWinding: windings ? WindingConnection.WyeGrounded : null, ToWinding: windings ? WindingConnection.WyeGrounded : null),
            new Branch(3, 4, 0.002, 0.08, IsTransformer: true, Name: "T1 winding 3",
                FromWinding: windings ? WindingConnection.Delta : null, ToWinding: windings ? WindingConnection.WyeGrounded : null),
        },
        Generators = new[] { new Generator(1, 15, MBase: 100, Name: "G1") },
    };

    /// <summary>A two-bus case with one Dyn transformer whose windings are given by the caller.</summary>
    private static GridModel TwoWinding(WindingConnection? from, WindingConnection? to) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, Pd: 10, BaseKv: 132) },
        Branches = new[] { new Branch(1, 2, 0.001, 0.1, IsTransformer: true, Name: "SGT1", FromWinding: from, ToWinding: to) },
        Generators = new[] { new Generator(1, 10, MBase: 100, Name: "G1") },
    };

    [Fact]
    public void Gr144_the_star_point_of_a_three_winding_unit_is_found_by_its_shape()
    {
        Assert.Equal(new[] { 4 }, Iec60909.ThreeWindingStarPoints(ThreeWinding(windings: true)));
        Assert.Equal(new[] { 4 }, Iec60909.ThreeWindingStarPoints(ThreeWinding(windings: false)));
        Assert.Empty(Iec60909.ThreeWindingStarPoints(TwoWinding(WindingConnection.Delta, WindingConnection.WyeGrounded)));
    }

    [Fact]
    public void Gr144_iec60909_earth_faults_on_a_three_winding_unit_are_refused_naming_the_unit()
    {
        GridModel m = ThreeWinding(windings: true);
        foreach (Func<Iec60909.Duty> study in new Func<Iec60909.Duty>[]
                 { () => Iec60909.SingleLineToGround(m, 2), () => Iec60909.DoubleLineToGround(m, 2) })
        {
            var ex = Assert.Throws<InvalidOperationException>(study);
            Assert.StartsWith("Earth-fault study refused: the model holds 1 three-winding transformer(s)", ex.Message, StringComparison.Ordinal);
            Assert.Contains("bus 4 ('T1 star point') joining windings 1-4 ('T1 winding 1'), 2-4 ('T1 winding 2'), 3-4 ('T1 winding 3')",
                ex.Message, StringComparison.Ordinal);
            Assert.Contains("Three-phase and line-to-line studies are not affected", ex.Message, StringComparison.Ordinal);
        }
        var z0 = Assert.Throws<InvalidOperationException>(() => Iec60909.ZeroSequenceThevenin(m, 3));
        Assert.Contains("three-winding", z0.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr144_three_phase_and_line_to_line_studies_on_a_three_winding_unit_still_run()
    {
        GridModel m = ThreeWinding(windings: true);
        Iec60909.Duty ll = Iec60909.LineToLine(m, 2);
        Assert.True(ll.IkppKa > 0.0 && double.IsFinite(ll.IkppKa));
        Iec60909.Duty three = Iec60909.ThreePhase(m, 2);
        Assert.True(three.IkppKa > 0.0 && double.IsFinite(three.IkppKa));
    }

    [Fact]
    public void Gr144_the_operating_point_study_refuses_earth_faults_but_not_line_to_line()
    {
        GridModel m = ThreeWinding(windings: true);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        var slg = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.Unbalanced(m, pf, 2, FaultType.SingleLineToGround));
        Assert.Contains("bus 4 ('T1 star point')", slg.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => FaultAnalysis.Unbalanced(m, pf, 2, FaultType.DoubleLineToGround));
        UnbalancedFaultResult ll = FaultAnalysis.Unbalanced(m, pf, 2, FaultType.LineToLine);
        Assert.NotNull(ll);
    }

    [Fact]
    public void Gr144_a_model_with_no_earthing_data_is_not_refused_and_stays_ungrounded()
    {
        // No winding connection and no machine zero-sequence impedance anywhere: there is no earthed neutral, so the
        // three-winding unit cannot change the (zero) earth-fault current; the CLI reports such a model as missing data.
        (_, _, bool grounded) = Iec60909.ZeroSequenceThevenin(ThreeWinding(windings: false), 2);
        Assert.False(grounded);
    }

    [Fact]
    public void Gr144_a_machine_zero_sequence_impedance_is_earthing_data_so_the_unit_is_refused()
    {
        GridModel plain = ThreeWinding(windings: false);
        var m = new GridModel
        {
            BaseMva = plain.BaseMva,
            Buses = plain.Buses,
            Branches = plain.Branches,
            Generators = new[] { new Generator(1, 15, MBase: 100, Name: "G1") { ZeroSequenceZPu = new MachineZeroSequence(0.01, 0.05) } },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => Iec60909.SingleLineToGround(m, 2));
        Assert.Contains("bus 4 ('T1 star point')", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr144_an_unknown_winding_connection_code_is_refused_naming_the_transformer()
    {
        GridModel m = TwoWinding((WindingConnection)7, WindingConnection.WyeGrounded);
        var ex = Assert.Throws<InvalidOperationException>(() => Iec60909.SingleLineToGround(m, 2));
        Assert.Contains("1-2 ('SGT1'): winding connection codes 7 / 1 are not Delta, WyeGrounded or WyeUngrounded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr144_a_one_sided_winding_connection_is_refused_naming_the_side_given()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Iec60909.DoubleLineToGround(TwoWinding(WindingConnection.Delta, null), 2));
        Assert.Contains("1-2 ('SGT1'): only the from winding is given", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr144_a_representable_two_winding_unit_still_gives_its_grounded_zero_sequence_impedance()
    {
        // Dyn: the delta HV side is isolated, the earthed LV star is a shunt Z0T = Z1 = 0.001 + j0.1 at bus 2.
        (System.Numerics.Complex z0, _, bool grounded) =
            Iec60909.ZeroSequenceThevenin(TwoWinding(WindingConnection.Delta, WindingConnection.WyeGrounded), 2);
        Assert.True(grounded);
        Assert.Equal(new System.Numerics.Complex(0.001, 0.1), z0);
    }
}
