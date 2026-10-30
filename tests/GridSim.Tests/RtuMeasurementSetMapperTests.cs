using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using Xunit;

namespace GridSim.Tests;

/// <summary>The RTU window -> MeasurementSet mapper: crosswalk-driven placement onto buses/branches, quality
/// folded into sigma, angles re-referenced, and the horizon guard enforced first (the RTU analogue of the
/// PMU mapper).</summary>
public class RtuMeasurementSetMapperTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Historical = Now.AddHours(-200); // well behind the 169 h horizon

    private static RtuPointMap Crosswalk() => new()
    {
        Case = "case14",
        BaseMva = 100.0,
        Bindings = new List<RtuPointBinding>
        {
            new() { Address = "IOA:1001", Kind = MeasurementKind.VmPu, Bus = 4, Sigma = 0.01 },
            new() { Address = "IOA:2001", Kind = MeasurementKind.PFlowMw, From = 1, To = 2, Sigma = 2.0 },
            new() { Address = "IOA:3001", Kind = MeasurementKind.VaDeg, Bus = 4, Sigma = 0.5, DatumAngleDeg = 10.0 },
        },
    };

    [Fact]
    public void Maps_points_onto_buses_and_branches_via_the_crosswalk()
    {
        var points = new List<RtuPoint>
        {
            new("IOA:1001", RtuPointType.Analog, 1.05, RtuQuality.Good, null),
            new("IOA:2001", RtuPointType.Analog, 55.0, RtuQuality.Good, null),
            new("IOA:9999", RtuPointType.Analog, 1.0, RtuQuality.Good, null), // unmapped -> skipped
        };

        MeasurementSet set = RtuMeasurementSetMapper.Map(points, Crosswalk(), Historical, utcNow: Now);

        Assert.Equal(100.0, set.BaseMva);
        Assert.Equal(Historical, set.TakenUtc);
        Assert.Equal(2, set.Measurements.Count);
        Measurement vm = set.Measurements.Single(m => m.Kind == MeasurementKind.VmPu);
        Assert.Equal(4, vm.Bus);
        Assert.Equal(1.05, vm.Value, 6);
        Assert.Equal("rtu", vm.Source);
        Measurement pf = set.Measurements.Single(m => m.Kind == MeasurementKind.PFlowMw);
        Assert.Equal(1, pf.From);
        Assert.Equal(2, pf.To);
        Assert.Equal(55.0, pf.Value, 6);
    }

    [Fact]
    public void Angle_is_re_referenced_against_the_datum()
    {
        var points = new List<RtuPoint> { new("IOA:3001", RtuPointType.Analog, 30.0, RtuQuality.Good, null) };
        MeasurementSet set = RtuMeasurementSetMapper.Map(points, Crosswalk(), Historical, utcNow: Now);
        Assert.Equal(20.0, set.Measurements.Single().Value, 6); // 30 - datum 10
    }

    [Fact]
    public void Not_topical_widens_sigma_and_invalid_is_dropped()
    {
        var points = new List<RtuPoint>
        {
            new("IOA:1001", RtuPointType.Analog, 1.0, RtuQuality.NonTopical, null),
            new("IOA:2001", RtuPointType.Analog, 55.0, RtuQuality.Invalid, null), // dropped
        };

        MeasurementSet set = RtuMeasurementSetMapper.Map(points, Crosswalk(), Historical, utcNow: Now);

        Measurement m = Assert.Single(set.Measurements);
        Assert.Equal(MeasurementKind.VmPu, m.Kind);
        Assert.Equal(0.04, m.Sigma, 6); // 0.01 * NonTopicalFactor(4)
    }

    [Fact]
    public void Present_time_window_is_rejected_by_the_horizon_guard()
    {
        var points = new List<RtuPoint> { new("IOA:1001", RtuPointType.Analog, 1.0, RtuQuality.Good, null) };
        Assert.Throws<ForwardInferenceException>(() =>
            RtuMeasurementSetMapper.Map(points, Crosswalk(), Now, utcNow: Now));
    }

    [Fact]
    public void Live_path_defers_the_guard_with_maxvalue_now()
    {
        var points = new List<RtuPoint> { new("IOA:1001", RtuPointType.Analog, 1.0, RtuQuality.Good, null) };
        // A present-time window is admitted only because the guard is deferred to the consuming engine.
        MeasurementSet set = RtuMeasurementSetMapper.Map(points, Crosswalk(), Now, utcNow: DateTime.MaxValue);
        Assert.Single(set.Measurements);
    }
}
