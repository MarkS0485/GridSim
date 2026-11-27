using System;
using System.Collections.Generic;

namespace GridSim.Core.Dynamics.LongTerm;

/// <summary>
/// WP21 (RC5): the automatic generation control of a long-term run, single area.
/// </summary>
/// <param name="IntegralGainPerSecond">Ki (1/s), above 0.</param>
/// <param name="BiasMwPerHz">The frequency bias B (MW/Hz, above 0); null takes the area's natural response beta.</param>
/// <param name="RateLimitMwPerSecond">The largest change of the total AGC set point per second (MW/s), or null for none.</param>
/// <param name="Units">The AGC units (generator indices in <c>model.Generators</c>) and their participation factors
/// (at least one above 0; normalised to sum 1).</param>
public sealed record AgcSpec(double IntegralGainPerSecond, double? BiasMwPerHz, double? RateLimitMwPerSecond,
    IReadOnlyList<AgcUnit> Units);

/// <summary>WP21: one AGC unit and its participation factor.</summary>
public sealed record AgcUnit(int Generator, double Participation);

/// <summary>
/// WP21 (RC5): single-area AGC in a quasi-steady-state run. Each QSS step h, with the step's frequency deviation df:
/// <code>
///   ACE   = dP_tie + B df        (dP_tie = 0 for a single area)
///   P_agc = P_agc - Ki ACE h     (forward Euler of dP_agc/dt = -Ki ACE)
/// </code>
/// and each AGC unit's set point moves by its participation factor times P_agc. With the optional rate limit the change of
/// P_agc in a step is clamped to +/-rate x h.
/// <para>Closed form (the tests' source). After a load step dPL with the primary response at equilibrium,
/// df = -(dPL - P_agc) / beta; with B = beta, ACE = -(dPL - P_agc), so
/// <c>P_agc(n+1) = P_agc(n) + Ki h (dPL - P_agc(n))</c> and <c>P_agc(n) = dPL (1 - (1 - Ki h)^n)</c>.</para>
/// </summary>
public sealed class AgcModel
{
    private readonly double _ki, _bias;
    private readonly double? _rate;
    private readonly double[] _share;

    /// <summary>Build the controller. <paramref name="participation"/> is normalised to sum 1.</summary>
    public AgcModel(double integralGainPerSecond, double biasMwPerHz, double? rateLimitMwPerSecond, IReadOnlyList<double> participation)
    {
        ArgumentNullException.ThrowIfNull(participation);
        if (!(integralGainPerSecond > 0.0) || !double.IsFinite(integralGainPerSecond))
            throw new ArgumentOutOfRangeException(nameof(integralGainPerSecond), integralGainPerSecond, "AGC: Ki must be a finite number above 0.");
        if (!(biasMwPerHz > 0.0) || !double.IsFinite(biasMwPerHz))
            throw new ArgumentOutOfRangeException(nameof(biasMwPerHz), biasMwPerHz, "AGC: the frequency bias must be a finite number of MW/Hz above 0.");
        if (rateLimitMwPerSecond is double r && (!(r > 0.0) || !double.IsFinite(r)))
            throw new ArgumentOutOfRangeException(nameof(rateLimitMwPerSecond), r, "AGC: the rate limit must be a finite number of MW/s above 0.");
        double sum = 0.0;
        foreach (double a in participation)
        {
            if (!(a >= 0.0) || !double.IsFinite(a))
                throw new ArgumentOutOfRangeException(nameof(participation), a, "AGC: a participation factor must be finite and 0 or more.");
            sum += a;
        }
        if (!(sum > 0.0)) throw new ArgumentException("AGC: at least one participation factor must be above 0.", nameof(participation));
        _share = new double[participation.Count];
        for (int k = 0; k < _share.Length; k++) _share[k] = participation[k] / sum;
        _ki = integralGainPerSecond;
        _bias = biasMwPerHz;
        _rate = rateLimitMwPerSecond;
    }

    /// <summary>The total AGC set point change P_agc (MW).</summary>
    public double TotalMw { get; private set; }

    /// <summary>The frequency bias B (MW/Hz).</summary>
    public double BiasMwPerHz => _bias;

    /// <summary>The area control error (MW) at frequency deviation <paramref name="dfHz"/> and tie-line deviation
    /// <paramref name="tieDeviationMw"/> (0 for a single area).</summary>
    public double AreaControlErrorMw(double dfHz, double tieDeviationMw = 0.0) => tieDeviationMw + _bias * dfHz;

    /// <summary>AGC unit <paramref name="k"/>'s share of P_agc (MW).</summary>
    public double UnitMw(int k) => _share[k] * TotalMw;

    /// <summary>The number of AGC units.</summary>
    public int UnitCount => _share.Length;

    /// <summary>Advance one QSS step <paramref name="h"/> at frequency deviation <paramref name="dfHz"/> (single area).
    /// Anti-windup (2.1.126, conditional integration): when every AGC unit still in service is held at its upper limit
    /// (<paramref name="allAtUpper"/>) a raise is not integrated, and when every one is held at its lower limit
    /// (<paramref name="allAtLower"/>) a lower is not, so P_agc does not wind up while the units cannot follow it and the
    /// control acts at once when the error changes sign. With no unit at a limit the step is the plain forward Euler above,
    /// bit for bit.</summary>
    public void Advance(double dfHz, double h, bool allAtUpper = false, bool allAtLower = false)
    {
        double d = -_ki * AreaControlErrorMw(dfHz) * h;
        if (_rate is double r) d = Math.Clamp(d, -r * h, r * h);
        if ((d > 0.0 && allAtUpper) || (d < 0.0 && allAtLower)) return;
        TotalMw += d;
    }
}
