using System;
using GridSim.Core.Dynamics.Models;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>The recorded response of a governor to an injected frequency: sample k at time k x dt, with the mechanical
/// power (pu, machine base) after k steps (sample 0 is the initial power).</summary>
public sealed record FrequencyInjectionTrace(double[] TimeSeconds, double[] FrequencyHz, double[] MechanicalPowerPu, double InitialPowerPu)
{
    /// <summary>The change of mechanical power from the initial power at sample k (pu, machine base).</summary>
    public double DeltaPu(int k) => MechanicalPowerPu[k] - InitialPowerPu;
}

/// <summary>
/// Frequency injection, the on-site test method for frequency response: a fresh governor from the unit's
/// <see cref="GovernorData"/> is initialised at <c>pm0</c> and driven with the speed deviation (f(t) - fn) / fn, held across
/// each step (f taken at the step start), and its mechanical power is recorded after each step. The governor's own exact
/// discretisation is the only numerics; nothing else in the RMS study is involved.
/// </summary>
public static class FrequencyInjection
{
    /// <summary>Run the injection for <paramref name="tEnd"/> seconds at step <paramref name="dt"/> (tEnd must be a whole
    /// number of steps).</summary>
    public static FrequencyInjectionTrace Run(GovernorData governor, double pm0, Func<double, double> fHz, double fn, double dt, double tEnd)
    {
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(fHz);
        if (!(dt > 0.0) || !double.IsFinite(dt)) throw new ArgumentOutOfRangeException(nameof(dt), dt, "The injection step must be positive and finite.");
        if (!(tEnd > 0.0) || !double.IsFinite(tEnd)) throw new ArgumentOutOfRangeException(nameof(tEnd), tEnd, "The injection duration must be positive and finite.");
        if (!(fn > 0.0) || !double.IsFinite(fn)) throw new ArgumentOutOfRangeException(nameof(fn), fn, "The nominal frequency must be positive and finite.");
        if (!double.IsFinite(pm0)) throw new ArgumentOutOfRangeException(nameof(pm0), pm0, "The initial power must be finite.");
        double steps = Math.Round(tEnd / dt);
        if (Math.Abs(steps * dt - tEnd) > 1e-9 * Math.Max(1.0, tEnd) || steps > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(tEnd), tEnd, "The injection duration must be a whole number of steps (at most 10,000,000).");
        int n = (int)steps;
        IRmsGovernor g = governor.Create();
        g.Initialize(pm0);
        var t = new double[n + 1];
        var f = new double[n + 1];
        var pm = new double[n + 1];
        pm[0] = pm0;
        f[0] = fHz(0.0);
        for (int k = 0; k < n; k++)
        {
            double tk = k * dt;
            double fk = fHz(tk);
            if (!double.IsFinite(fk)) throw new InvalidOperationException("The injected frequency must be finite.");
            f[k] = fk;
            pm[k + 1] = g.Step((fk - fn) / fn, dt);
            t[k + 1] = (k + 1) * dt;
        }
        f[n] = fHz(n * dt);
        return new FrequencyInjectionTrace(t, f, pm, pm0);
    }
}
