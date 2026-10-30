namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// SEXS - the Simplified Excitation System, the IEEE compact AVR and the first entry in GridSim's standard
/// excitation library. It maps a voltage error to a field voltage and, like <see cref="Tgov1"/>, is a
/// direct wiring of the common control blocks: a transient-gain-shaping lead-lag <c>(1+sTa)/(1+sTb)</c>
/// followed by the exciter gain-lag <c>K/(1+sTe)</c> with non-windup ceiling/floor limits
/// <c>[Emin, Emax]</c>:
/// <code>
///   Verr = Vref - Vc + Vs          (regulator error: reference - measured terminal + PSS signal)
///   v1   = leadlag_{Ta/Tb}(Verr)
///   Efd  = lag_{K, Te, [Emin,Emax]}(v1)
/// </code>
/// All quantities are per-unit. The steady field voltage is <c>Efd = clamp(K*Verr, Emin, Emax)</c>, so the
/// high open-loop gain K holds the terminal-voltage error small - the defining behaviour of an AVR. The
/// model carries no numerics of its own beyond routing the signal: the dynamics are the exact-discretised
/// <see cref="LeadLag"/> and <see cref="FirstOrderLag"/> blocks.
/// </summary>
public sealed class Sexs
{
    private readonly double _k;
    private readonly LeadLag _shaping;
    private readonly FirstOrderLag _exciter;

    /// <param name="leadSeconds">Lead-lag numerator Ta (s), &gt;= 0.</param>
    /// <param name="lagSeconds">Lead-lag denominator Tb (s), &gt; 0.</param>
    /// <param name="gain">Exciter gain K; must be &gt; 0 for a well-posed equilibrium initialisation.</param>
    /// <param name="exciterTimeSeconds">Exciter lag Te (s), 0 or above; 0 makes the exciter gain instantaneous. A negative
    /// value is refused (bug hunt 2026-10-29, finding 26: the doc used to promise that a negative value was instant too, but the lag has refused negatives since GR-557).</param>
    /// <param name="fieldMinPu">Field-voltage floor Emin (pu).</param>
    /// <param name="fieldMaxPu">Field-voltage ceiling Emax (pu).</param>
    public Sexs(double leadSeconds, double lagSeconds, double gain, double exciterTimeSeconds,
        double fieldMinPu = double.NegativeInfinity, double fieldMaxPu = double.PositiveInfinity)
    {
        if (gain <= 0.0) throw new ArgumentOutOfRangeException(nameof(gain), "Exciter gain K must be > 0.");
        _k = gain;
        _shaping = new LeadLag(leadSeconds, lagSeconds);
        if (!(exciterTimeSeconds >= 0.0) || !double.IsFinite(exciterTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(exciterTimeSeconds), exciterTimeSeconds, "Te must be a finite number of seconds, 0 or above; 0 makes the block instantaneous (a negative \"disabled\" marker is not accepted).");
        _exciter = new FirstOrderLag(gain, exciterTimeSeconds, fieldMinPu, fieldMaxPu);
    }

    /// <summary>Field voltage Efd (pu) after the last step.</summary>
    public double FieldVoltage => _exciter.Output;

    /// <summary>
    /// Pre-load every state so that the field voltage sits at <paramref name="fieldVoltagePu"/> in
    /// equilibrium, with the regulator error <c>Verr = Efd / K</c> that produces it.
    /// </summary>
    public void Initialize(double fieldVoltagePu)
    {
        // R252 (October 2026, stream B3): the exciter lag holds Efd itself, so Efd must lie inside [Emin, Emax]; out of
        // limits is refused instead of clamped silently (the lead-lag was preset at the unclamped error).
        ControlBlocks.RequireEquilibriumWithinLimits("SEXS", "field voltage Efd", fieldVoltagePu,
            _exciter.Min, _exciter.Max, nameof(fieldVoltagePu));
        double verr = fieldVoltagePu / _k;
        _shaping.Initialize(verr);
        _exciter.Initialize(fieldVoltagePu);
    }

    /// <summary>
    /// Advance one step of <paramref name="dtSeconds"/> with regulator error <paramref name="voltageErrorPu"/>
    /// (pu, held across the step) and return the field voltage Efd (pu).
    /// </summary>
    public double Step(double voltageErrorPu, double dtSeconds)
    {
        double v1 = _shaping.Step(voltageErrorPu, dtSeconds);
        return _exciter.Step(v1, dtSeconds);
    }
}
