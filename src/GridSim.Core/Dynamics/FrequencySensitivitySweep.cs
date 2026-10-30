namespace GridSim.Core.Dynamics;

/// <summary>How one uncertain parameter moves the frequency nadir when swept +/- a fractional range about
/// its nominal value, holding the others fixed (one-at-a-time).</summary>
/// <param name="Parameter">The swept parameter.</param>
/// <param name="LowValue">Nominal * (1 - range).</param>
/// <param name="HighValue">Nominal * (1 + range).</param>
/// <param name="NadirAtLow">Nadir (Hz) at the low value.</param>
/// <param name="NadirAtHigh">Nadir (Hz) at the high value.</param>
/// <param name="NadirSwingHz">|NadirAtHigh - NadirAtLow| - how much this parameter's uncertainty alone moves the nadir.</param>
public sealed record ParameterSensitivity(
    FrequencyFitParam Parameter,
    double LowValue, double HighValue,
    double NadirAtLow, double NadirAtHigh,
    double NadirSwingHz);

/// <summary>The uncertainty in the frequency response attributable to the UNFITTED parameters: a per-parameter
/// one-at-a-time swing (which parameter the answer is most sensitive to) plus the combined nadir band over the
/// joint uncertainty box.</summary>
public sealed record FrequencySensitivityResult(
    FrequencyResponse Nominal,
    IReadOnlyList<ParameterSensitivity> PerParameter,
    double NadirMinHz, double NadirMaxHz,
    double NadirBandHz,
    double InitialRocofBandHzPerS)
{
    /// <summary>The parameter the nadir is most sensitive to (largest one-at-a-time swing), or null if none swept.</summary>
    public ParameterSensitivity? Dominant => PerParameter.Count == 0 ? null : PerParameter[0];
}

/// <summary>
/// Turns the single-point frequency estimate into an uncertainty band by sweeping the UNFITTED swing
/// constants (<see cref="DynamicsConfig.DampingPctPerHz"/>, <see cref="DynamicsConfig.ResponsiveFraction"/>,
/// <see cref="DynamicsConfig.GovernorTimeConstantS"/>) about their nominal values. It answers "if
/// DampingPctPerHz is wrong by 20%, how much does the nadir move?" directly (the per-parameter swing) and
/// "how wrong could the nadir be given all three are uncertain?" (the combined band over the box corners).
///
/// <para>The initial RoCoF depends only on inertia and the loss, so its band across the sweep is ~0 - a
/// built-in sanity check that these constants shape only the nadir/recovery, exactly as documented.</para>
/// </summary>
public static class FrequencySensitivitySweep
{
    public static FrequencySensitivityResult Sweep(
        double systemInertiaMwS,
        double baseDemandMw,
        IReadOnlyList<FrequencyEvent> events,
        DynamicsConfig? nominal = null,
        double fractionalRange = 0.20,
        IReadOnlyList<FrequencyFitParam>? parameters = null)
    {
        // R242: the one-at-a-time sweep scales each parameter by (1 - range) and (1 + range). A range of 1 or more sends
        // the low side to zero or below (a zero or negative governor time constant, damping or headroom), which is not a
        // sensitivity but a different model; a range at or below 0 sweeps nothing. Refuse both, naming the value.
        if (!(fractionalRange > 0.0 && fractionalRange < 1.0))
            throw new ArgumentOutOfRangeException(nameof(fractionalRange),
                $"The sensitivity range must be a fraction strictly between 0 and 1 (got {fractionalRange}).");
        DynamicsConfig baseCfg = nominal ?? new DynamicsConfig();
        FrequencyFitParam[] which = (parameters is { Count: > 0 } ? parameters
            : new[] { FrequencyFitParam.Damping, FrequencyFitParam.ResponsiveFraction, FrequencyFitParam.GovernorTimeConstant })
            .Distinct().ToArray();

        FrequencyResponse Run(DynamicsConfig c) => FrequencyDynamics.Simulate(systemInertiaMwS, baseDemandMw, events, c);
        FrequencyResponse nom = Run(baseCfg);

        // One-at-a-time: sweep each parameter +/- range, hold the others at nominal.
        var per = new List<ParameterSensitivity>(which.Length);
        foreach (FrequencyFitParam p in which)
        {
            double v0 = Get(baseCfg, p);
            double lo = v0 * (1 - fractionalRange), hi = v0 * (1 + fractionalRange);
            double nadirLo = Run(Set(baseCfg, p, lo)).NadirHz;
            double nadirHi = Run(Set(baseCfg, p, hi)).NadirHz;
            per.Add(new ParameterSensitivity(p, lo, hi, nadirLo, nadirHi, System.Math.Abs(nadirHi - nadirLo)));
        }
        GridSim.Core.Numerics.FrozenIntroSort.Sort(per, (a, b) => b.NadirSwingHz.CompareTo(a.NadirSwingHz)); // most sensitive first; GR-867: frozen .NET 10 introsort, so a tie keeps one order on every runtime (bit-identical to List.Sort today)

        // Combined band: every corner of the joint +/- box (2^k corners). k is small (<= 3), so exhaustive.
        double nadirMin = double.PositiveInfinity, nadirMax = double.NegativeInfinity;
        double rocofMin = double.PositiveInfinity, rocofMax = double.NegativeInfinity;
        int corners = 1 << which.Length;
        for (int mask = 0; mask < corners; mask++)
        {
            DynamicsConfig c = baseCfg;
            for (int j = 0; j < which.Length; j++)
            {
                double v0 = Get(baseCfg, which[j]);
                double v = (mask & (1 << j)) != 0 ? v0 * (1 + fractionalRange) : v0 * (1 - fractionalRange);
                c = Set(c, which[j], v);
            }
            FrequencyResponse r = Run(c);
            nadirMin = System.Math.Min(nadirMin, r.NadirHz);
            nadirMax = System.Math.Max(nadirMax, r.NadirHz);
            rocofMin = System.Math.Min(rocofMin, r.InitialRocofHzPerS);
            rocofMax = System.Math.Max(rocofMax, r.InitialRocofHzPerS);
        }

        return new FrequencySensitivityResult(
            nom, per, nadirMin, nadirMax, nadirMax - nadirMin, System.Math.Abs(rocofMax - rocofMin));
    }

    private static double Get(DynamicsConfig c, FrequencyFitParam p) => p switch
    {
        FrequencyFitParam.Damping => c.DampingPctPerHz,
        FrequencyFitParam.ResponsiveFraction => c.ResponsiveFraction,
        FrequencyFitParam.GovernorTimeConstant => c.GovernorTimeConstantS,
        _ => throw new System.ArgumentOutOfRangeException(nameof(p)),
    };

    private static DynamicsConfig Set(DynamicsConfig c, FrequencyFitParam p, double v) => p switch
    {
        FrequencyFitParam.Damping => c with { DampingPctPerHz = v },
        FrequencyFitParam.ResponsiveFraction => c with { ResponsiveFraction = v },
        FrequencyFitParam.GovernorTimeConstant => c with { GovernorTimeConstantS = v },
        _ => throw new System.ArgumentOutOfRangeException(nameof(p)),
    };
}
