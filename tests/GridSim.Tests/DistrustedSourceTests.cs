using System;
using System.Collections.Generic;
using GridSim.Core.Estimation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The distrusted-source containment control (NCSC CAF D1 response): a suspect measurement source is forced to
/// pseudo-measurement treatment by inflating its sigma, which drops its weight (1/sigma^2) toward zero without
/// removing the row (observability and the audit record are preserved). It only ever weakens a row: it multiplies
/// sigma, never the value, and returns a trusted row unchanged by reference.
/// </summary>
public sealed class DistrustedSourceTests
{
    [Fact]
    public void A_distrusted_row_has_its_sigma_inflated_but_its_value_and_source_unchanged()
    {
        var control = new DistrustedSources(new[] { "scada" });
        var row = new Measurement(MeasurementKind.VmPu, 1.02, 0.004, Bus: 4, Source: "scada");

        Measurement applied = control.Apply(row);

        Assert.Equal(row.Value, applied.Value);                         // value never changes
        Assert.Equal("scada", applied.Source);                          // source never changes
        Assert.Equal(0.004 * DistrustedSources.DefaultSigmaInflation, applied.Sigma, 15);
        Assert.True(applied.Sigma > row.Sigma);                         // only ever weakened
    }

    [Fact]
    public void A_trusted_row_is_returned_unchanged_by_reference()
    {
        var control = new DistrustedSources(new[] { "scada" });
        var row = new Measurement(MeasurementKind.VmPu, 1.02, 0.004, Bus: 4, Source: "pmu");

        Measurement applied = control.Apply(row);
        Assert.Same(row, applied);
    }

    [Fact]
    public void A_custom_inflation_factor_is_applied_and_is_matched_case_insensitively()
    {
        var control = new DistrustedSources(new[] { "SCADA" }, sigmaInflation: 50.0);
        Assert.True(control.IsDistrusted("scada"));
        var row = new Measurement(MeasurementKind.QInjMvar, -14.0, 1.0, Bus: 32, Source: "scada");

        Measurement applied = control.Apply(row);
        Assert.Equal(50.0, applied.Sigma, 15);
        Assert.Equal(-14.0, applied.Value);
    }

    [Fact]
    public void Apply_on_a_set_rewrites_only_matching_rows()
    {
        var control = new DistrustedSources(new[] { "scada" });
        var rows = new List<Measurement>
        {
            new(MeasurementKind.VmPu, 1.02, 0.004, Bus: 1, Source: "scada"),
            new(MeasurementKind.VmPu, 1.01, 0.004, Bus: 2, Source: "pmu"),
            new(MeasurementKind.PInjMw, 50.0, 1.0, Bus: 3, Source: "scada"),
        };
        var set = new MeasurementSet { Case = "case9", BaseMva = 100.0, Measurements = rows };

        MeasurementSet result = control.Apply(set);

        Assert.NotSame(set, result);
        Assert.Equal(0.004 * DistrustedSources.DefaultSigmaInflation, result.Measurements[0].Sigma, 15); // scada: inflated
        Assert.Same(rows[1], result.Measurements[1]);                                                     // pmu: untouched row
        Assert.Equal(1.0 * DistrustedSources.DefaultSigmaInflation, result.Measurements[2].Sigma, 15);    // scada: inflated
    }

    [Fact]
    public void Apply_on_a_set_with_no_matching_row_returns_the_same_instance()
    {
        var control = new DistrustedSources(new[] { "feed-x" });
        var rows = new List<Measurement>
        {
            new(MeasurementKind.VmPu, 1.02, 0.004, Bus: 1, Source: "scada"),
            new(MeasurementKind.VmPu, 1.01, 0.004, Bus: 2, Source: "pmu"),
        };
        var set = new MeasurementSet { Case = "case9", BaseMva = 100.0, Measurements = rows };

        Assert.Same(set, control.Apply(set));
    }

    [Fact]
    public void An_empty_control_is_a_no_op_and_an_empty_set_is_returned_unchanged()
    {
        var empty = new DistrustedSources(Array.Empty<string>());
        Assert.True(empty.IsEmpty);
        var set = new MeasurementSet { Case = "case9", BaseMva = 100.0, Measurements = new List<Measurement>() };
        Assert.Same(set, empty.Apply(set));
    }

    [Fact]
    public void The_constructor_rejects_a_sigma_inflation_that_does_not_strictly_weaken()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DistrustedSources(new[] { "scada" }, sigmaInflation: 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DistrustedSources(new[] { "scada" }, sigmaInflation: 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DistrustedSources(new[] { "scada" }, sigmaInflation: double.PositiveInfinity));
    }
}
