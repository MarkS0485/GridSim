namespace GridSim.Core.Analysis.Sensitivities;

/// <summary>Voltage response of every bus to the tap ratio of one branch: pu per unit ratio, and the angle response.</summary>
public sealed record TapSensitivityColumn(int Branch, double[] DVmPerUnitRatio, double[] DThetaRadPerUnitRatio);

/// <summary>Tap-changer sensitivities dV/d(tap) (R4-03, report row 2.5) from <see cref="AcSensitivityEngine"/>: the exact
/// derivative of the solved state with respect to the off-nominal ratio of one transformer, with the regulated set
/// points and the reactive-limit pins held as they ended the solve.</summary>
public static class TapSensitivity
{
    /// <summary>The response to a unit increase of branch <paramref name="branch"/>'s tap ratio.</summary>
    public static TapSensitivityColumn Column(AcSensitivityEngine engine, int branch)
    {
        ArgumentNullException.ThrowIfNull(engine);
        (double[] dth, double[] dv) = engine.TapResponse(branch);
        return new TapSensitivityColumn(branch, dv, dth);
    }
}
