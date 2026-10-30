using System;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>Transformer winding-connection voltage model: grounded-wye scales by the turns ratio with no
/// shift; delta-wye applies the ANSI -30 degree positive-sequence shift and blocks the zero sequence.</summary>
public class TransformerConnectionModelTests
{
    [Fact]
    public void WyeG_WyeG_scales_by_turns_ratio_without_phase_shift()
    {
        Complex[] hv = MultiPhaseModel.BalancedNominal(1.0);
        Complex[] lv = TransformerConnectionModel.TransformVoltage(TransformerConnection.WyeGWyeG, hv, turnsRatio: 10.0);

        Assert.Equal(0.1, lv[0].Magnitude, 12);           // 1.0 / 10
        Assert.True(Math.Abs(lv[0].Phase) < 1e-12);       // phase A stays at 0 degrees
    }

    [Fact]
    public void Delta_WyeG_lags_positive_sequence_by_30_degrees_and_blocks_zero_sequence()
    {
        Complex[] hv = MultiPhaseModel.BalancedNominal(1.0); // balanced -> positive sequence only
        Complex[] lv = TransformerConnectionModel.TransformVoltage(TransformerConnection.DeltaWyeGStepDown, hv, turnsRatio: 2.0);

        Assert.Equal(0.5, lv[0].Magnitude, 12);           // 1.0 / 2
        Assert.Equal(-30.0, lv[0].Phase * 180.0 / Math.PI, 9);

        // Add a zero-sequence component to HV; the delta must block it (LV zero sequence = 0).
        var withZero = new[] { hv[0] + 0.2, hv[1] + 0.2, hv[2] + 0.2 };
        Complex[] lvZ = TransformerConnectionModel.TransformVoltage(TransformerConnection.DeltaWyeGStepDown, withZero, 2.0);
        Complex lvZeroSeq = SymmetricalComponents.ToSequence(lvZ)[0];
        Assert.True(lvZeroSeq.Magnitude < 1e-12, $"LV zero sequence {lvZeroSeq} should be blocked");
    }
}
