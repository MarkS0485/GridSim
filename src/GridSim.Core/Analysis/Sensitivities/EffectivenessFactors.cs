using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Sensitivities;

/// <summary>One control's effect on the target voltage. <see cref="Kind"/> is "reactive-injection" (dV per MVAr at a bus),
/// "tap" (dV per unit tap ratio of a branch) or "voltage-setpoint" (dV per pu of a regulated bus's set point).</summary>
public sealed record EffectivenessRow(string Kind, int Element, string Label, double EffectPerUnit, string Unit);

/// <summary>
/// Control effectiveness factors (R4-03, report row 4.12): for one target bus, the first-order effect on its voltage of
/// each candidate control, all from ONE adjoint solve of the transposed Jacobian (<see cref="AcSensitivityEngine.Adjoint"/>).
/// Rows come back in a fixed order (reactive injections by bus, taps by branch, set points by bus); a caller ranks them.
/// </summary>
public static class EffectivenessFactors
{
    /// <summary>The effect of every control on the voltage magnitude of <paramref name="targetBusId"/>.</summary>
    public static IReadOnlyList<EffectivenessRow> ForTarget(GridModel model, AcSensitivityEngine engine, int targetBusId)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(engine);
        double[] y = engine.Adjoint(targetBusId);
        var rows = new List<EffectivenessRow>();
        for (int i = 0; i < model.BusCount; i++)
        {
            if (engine.Types[i] != BusType.Pq) continue;
            int id = model.Buses[i].Id;
            rows.Add(new EffectivenessRow("reactive-injection", id, $"bus {id}", engine.EffectOfReactiveInjection(y, id), "pu/MVAr"));
        }
        for (int k = 0; k < model.Branches.Count; k++)
        {
            Branch br = model.Branches[k];
            if (!br.InService || !(br.IsTransformer || (br.TapRatio != 1.0 && br.TapRatio != 0.0))) continue;
            rows.Add(new EffectivenessRow("tap", k, $"branch {k} {br.From}->{br.To}", engine.EffectOfTap(y, k), "pu/unit ratio"));
        }
        for (int i = 0; i < model.BusCount; i++)
        {
            if (engine.Types[i] != BusType.Pv) continue;
            int id = model.Buses[i].Id;
            rows.Add(new EffectivenessRow("voltage-setpoint", id, $"bus {id}", engine.EffectOfSetpoint(y, id), "pu/pu"));
        }
        return rows;
    }
}
