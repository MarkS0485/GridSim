using System.Numerics;

namespace GridSim.Core.Distribution;

/// <summary>The supported distribution-transformer winding connections.</summary>
public enum TransformerConnection
{
    /// <summary>Grounded-wye to grounded-wye: all sequences pass, no phase shift.</summary>
    WyeGWyeG,
    /// <summary>Delta (HV) to grounded-wye (LV) step-down (ANSI Dyn1): the LV positive sequence lags the HV by
    /// 30 degrees, the negative sequence leads by 30, and the zero sequence is blocked by the delta.</summary>
    DeltaWyeGStepDown,
}

/// <summary>
/// Transformer winding-connection voltage model in the sequence domain - the clean, exact way to capture a
/// distribution transformer's turns ratio and phase shift. It transforms an HV phase-voltage triple to the LV
/// side via symmetrical components: grounded-wye/grounded-wye passes every sequence scaled by the turns ratio;
/// delta/grounded-wye applies the +/-30 degree ANSI phase shift and blocks the zero sequence. GR-599: the three-phase
/// sweep does not use it (it builds its own phase-domain transformer equivalents); this class is kept for its tests
/// and as a reference model, with no production caller.
/// </summary>
public static class TransformerConnectionModel
{
    /// <summary>Transform the HV phase voltages to the LV side for the given connection and turns ratio
    /// (HV:LV per-phase). Input and output are phase (abc) triples.</summary>
    public static Complex[] TransformVoltage(TransformerConnection connection, Complex[] hvAbc, double turnsRatio)
    {
        Complex[] seq = SymmetricalComponents.ToSequence(hvAbc);
        Complex s0 = seq[0], s1 = seq[1], s2 = seq[2];
        // GR-600: a turns ratio that is not a finite number above 0 used to be replaced by 1 (and NaN let through).
        if (!(turnsRatio > 0) || !double.IsFinite(turnsRatio))
            throw new ArgumentOutOfRangeException(nameof(turnsRatio), turnsRatio, "The turns ratio must be a finite number above 0.");
        double nt = turnsRatio;

        switch (connection)
        {
            case TransformerConnection.WyeGWyeG:
                s0 /= nt; s1 /= nt; s2 /= nt;
                break;
            case TransformerConnection.DeltaWyeGStepDown:
                s0 = Complex.Zero; // delta blocks zero sequence
                s1 = s1 / nt * DetMath.FromPolar(1.0, -Math.PI / 6.0); // -30 deg
                s2 = s2 / nt * DetMath.FromPolar(1.0, +Math.PI / 6.0); // +30 deg
                break;
        }

        return SymmetricalComponents.ToPhase(new[] { s0, s1, s2 });
    }
}
