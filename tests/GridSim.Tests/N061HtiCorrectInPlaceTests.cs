using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N061 (October 2026): HTI correct-in-place (design robust-gm-leverage.md section 6.2). An identified measurement is
/// corrected to z - e^ and kept, the estimate re-run and chi-square re-checked; a row identified again after its
/// correction is removed. Removal stays the default action, so every existing HTI result is unchanged.
/// </summary>
public class N061HtiCorrectInPlaceTests
{
    private static (GridModel Model, PowerFlowResult Truth, MeasurementSet Exact) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        return (model, truth, WlsEstimatorTests.ExactFullSet(model, truth));
    }

    private static (MeasurementSet Set, int Index, double TrueValue) WithGrossError(MeasurementSet exact, MeasurementKind kind, double error)
    {
        var rows = new List<Measurement>(exact.Measurements);
        // A measurement away from the slack bus (bus 1): the slack's injection is the least redundant row.
        int bad = rows.FindIndex(m => m.Kind == kind && m.Bus != 1 && m.From != 1 && m.To != 1);
        double trueValue = rows[bad].Value;
        rows[bad] = rows[bad] with { Value = trueValue + error };
        return (exact with { Measurements = rows }, bad, trueValue);
    }

    [Theory]
    [InlineData(MeasurementKind.VmPu, 0.25)]
    [InlineData(MeasurementKind.PInjMw, 40.0)]
    [InlineData(MeasurementKind.PFlowMw, 30.0)]
    public void A_gross_error_is_corrected_back_to_the_true_value_and_the_row_is_kept(MeasurementKind kind, double error)
    {
        (GridModel model, PowerFlowResult truth, MeasurementSet exact) = Case9();
        (MeasurementSet set, int bad, double trueValue) = WithGrossError(exact, kind, error);

        HtiEstimationResult hti = HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set, new EstimationOptions(),
            action: HtiAction.CorrectThenReverify);

        Assert.True(hti.Estimate.Converged);
        Assert.Equal(HtiStopReason.ChiSquarePassed, hti.StopReason);
        Assert.Empty(hti.Removed);                                            // nothing removed: corrected instead
        HtiCorrection c = Assert.Single(hti.Corrected);
        Assert.Same(set.Measurements[bad], c.Identified.Measurement);         // the verdict names the original row
        // On an exact set the joint error estimate recovers the injected error up to the linearisation of r = S e about
        // the biased estimate. Measured (1.95.127 code, case9 exact set): VmPu bus 2, 1.275 -> 1.0252338 (true 1.025,
        // 0.09 % of the error left); PFlowMw 4-5, 60.7037 -> 30.7024 MW (true 30.7037, 0.004 %); PInjMw bus 2 (a
        // generator bus), 203.0 -> 163.918 MW (true 163.0, 2.3 %). The bound is 3 % of the injected error.
        Assert.True(Math.Abs(c.CorrectedValue - trueValue) < 0.03 * Math.Abs(error),
            $"corrected {c.CorrectedValue:R} against true {trueValue:R} (injected error {error})");
        Assert.Equal(set.Measurements.Count, hti.Estimate.Residuals.Count);  // every row still in the fit
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(hti.Estimate.Vm[i] - truth.Vm[i]) < 1e-3, $"bus {truth.BusIds[i]}: Vm {hti.Estimate.Vm[i]} vs {truth.Vm[i]}");
    }

    [Fact]
    public void Removal_stays_the_default_and_is_bit_identical_when_named_explicitly()
    {
        (GridModel model, _, MeasurementSet exact) = Case9();
        (MeasurementSet set, _, _) = WithGrossError(exact, MeasurementKind.VmPu, 0.25);
        HtiEstimationResult dflt = HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set, new EstimationOptions());
        HtiEstimationResult named = HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set, new EstimationOptions(), action: HtiAction.Remove);
        Assert.Empty(dflt.Corrected);
        Assert.Equal(dflt.Removed.Count, named.Removed.Count);
        Assert.Equal(dflt.Passes, named.Passes);
        Assert.Equal(dflt.StopReason, named.StopReason);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(dflt.Estimate.Vm[i]), BitConverter.DoubleToInt64Bits(named.Estimate.Vm[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(dflt.Estimate.VaDeg[i]), BitConverter.DoubleToInt64Bits(named.Estimate.VaDeg[i]));
        }
    }

    [Fact]
    public void The_cap_counts_corrections_and_removals_together()
    {
        (GridModel model, _, MeasurementSet exact) = Case9();
        (MeasurementSet set, _, _) = WithGrossError(exact, MeasurementKind.VmPu, 0.25);
        HtiEstimationResult hti = HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set,
            new EstimationOptions { MaxBadDataRemovals = 0 }, action: HtiAction.CorrectThenReverify);
        Assert.Empty(hti.Corrected);
        Assert.Empty(hti.Removed);
        Assert.Equal(HtiStopReason.RemovalCapReached, hti.StopReason);
    }

    [Theory]
    [InlineData(MeasurementKind.VmPu, 0.01, 0.01)]
    [InlineData(MeasurementKind.IMagPu, 0.02, 0.02)]
    [InlineData(MeasurementKind.VaDeg, Math.PI, 180.0)]
    [InlineData(MeasurementKind.IAngDeg, Math.PI / 2, 90.0)]
    [InlineData(MeasurementKind.PInjMw, 0.4, 40.0)]
    [InlineData(MeasurementKind.QFlowMvar, 0.1, 10.0)]
    public void An_internal_error_converts_back_to_the_measurement_units(MeasurementKind kind, double internalUnits, double expected)
    {
        Assert.Equal(expected, HtiBadDataEstimation.ToMeasurementUnits(kind, internalUnits, 100.0), 12);
    }
}
