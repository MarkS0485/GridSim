using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Sensitivities;

/// <summary>The AC voltage sensitivity to an injection at <see cref="BusId"/>: per MVAr and per MW, every bus (model
/// order). <see cref="Resolved"/> is false when the bus has no reactive row at the solved state (a slack or a regulating
/// bus holds its voltage, so the reactive column is zero).</summary>
public sealed record VoltageSensitivityColumn(int BusId, bool Resolved, double[] DVmPerMvar, double[] DVmPerMw, double[] DThetaRadPerMvar);

/// <summary>Voltage sensitivities dV/dQ and dV/dP (R4-03, report row 2.6) from <see cref="AcSensitivityEngine"/>.</summary>
public static class VoltageSensitivity
{
    /// <summary>dV/dQ and dV/dP columns for an injection at <paramref name="busId"/> at the solved state.</summary>
    public static VoltageSensitivityColumn Column(AcSensitivityEngine engine, int busId)
    {
        ArgumentNullException.ThrowIfNull(engine);
        (double[] dth, double[] dvq) = engine.InjectionResponse(busId, 0.0, 1.0);
        double[] dvp = engine.InjectionResponse(busId, 1.0, 0.0).DVm;
        int i = -1;
        for (int k = 0; k < engine.BusIds.Count; k++) if (engine.BusIds[k] == busId) { i = k; break; }
        return new VoltageSensitivityColumn(busId, engine.Types[i] == BusType.Pq, dvq, dvp, dth);
    }
}
