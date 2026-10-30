namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// PSS1A - the IEEE single-input power-system stabiliser, the first entry in GridSim's PSS library. It adds
/// damping to the rotor oscillation by feeding a phase-compensated speed (or power) signal into the AVR
/// summing junction. Like <see cref="Tgov1"/> and <see cref="Sexs"/> it is a direct wiring of the common
/// control blocks: a stabiliser gain <c>Ks</c>, a washout <c>sTw/(1+sTw)</c> that blocks any steady signal,
/// two phase-lead stages <c>(1+sT1)/(1+sT2)</c> and <c>(1+sT3)/(1+sT4)</c>, and output limits
/// <c>[Vsmin, Vsmax]</c>:
/// <code>
///   g   = Ks * u                                     (u = speed deviation or power input)
///   w   = washout_{Tw}(g)                             (no output for a sustained input)
///   v1  = leadlag_{T1/T2}(w)
///   Vs  = clamp( leadlag_{T3/T4}(v1), Vsmin, Vsmax )  (added to the AVR error)
/// </code>
/// All quantities are per-unit. Its defining property is that it contributes <b>nothing in steady state</b>:
/// the washout drives any DC input to zero output, so a PSS never shifts the regulated voltage - it acts only
/// on the swing. The model owns no numerics of its own beyond routing the signal: the dynamics are the
/// exact-discretised <see cref="Washout"/> and <see cref="LeadLag"/> blocks.
/// <para><b>A subset of the IEEE 421.5 PSS1A it is named after (R083, October 2026).</b> The standard's block diagram
/// also has an input transducer lag 1/(1 + sT6) ahead of the gain, and a second-order low-pass filter in A1 and
/// A2 after the washout that stands for the low-frequency effect of the torsional filters. The
/// filter has the two coefficients A1 and A2 only (an earlier note here said "A1 to A6", which is wrong for PSS1A).
/// Order: transducer T6, gain Ks, washout T5 (Tw here), filter A1 / A2, lead-lags T1 / T2 and T3 / T4, limits.
/// Sources (public implementations of 421.5-2005 / 2016, checked 2026-10-11, N338): CGMES 2.4.15 PssIEEE1A
/// ("IEEE 1A 421.5-2005 Section 8.1"; attributes a1, a2, t1 to t6, ks, vrmax, vrmin; typical T6 0.01 s, A1 0.061,
/// A2 0.0017), MathWorks Simscape PSS1A (421.5-2016: "Second-Order Low-Pass Filter" with "denominator constants A1
/// and A2", T6 "transducer time constant"), and PSCAD's 2016 stabiliser models. This model has neither block, so it
/// cannot reproduce data whose response depends on them (a non-zero T6, A1 or A2). The exact filter polynomial and a
/// response-level check still need IEEE 421.5 itself, which is not in docs/reference [VERIFY]; the two blocks can be
/// added around the gain and washout in a new constructor, leaving this one unchanged.</para>
/// <para>A lead-lag stage with BOTH time constants zero is the unity transfer function (1 + 0s)/(1 + 0s) = 1, the way
/// published PSS1A data switches a stage off (T3 = T4 = 0 for a single-stage stabiliser); since R083 such a stage is
/// bypassed exactly instead of being refused. A stage with a zero lag but a non-zero lead is a pure derivative
/// (improper) and is still refused, loudly.</para>
/// </summary>
public sealed class Pss1a
{
    private readonly double _ks;
    private readonly Washout _washout;
    private readonly LeadLag? _stage1;   // null: the stage is switched off (lead = lag = 0), the unity transfer function
    private readonly LeadLag? _stage2;
    private readonly double _min;
    private readonly double _max;

    /// <param name="gain">Stabiliser gain Ks.</param>
    /// <param name="washoutSeconds">Washout time constant Tw (s), &gt; 0.</param>
    /// <param name="lead1Seconds">First lead time constant T1 (s), &gt;= 0.</param>
    /// <param name="lag1Seconds">First lag time constant T2 (s), &gt; 0; or 0 together with T1 = 0 to switch the stage off.</param>
    /// <param name="lead2Seconds">Second lead time constant T3 (s), &gt;= 0.</param>
    /// <param name="lag2Seconds">Second lag time constant T4 (s), &gt; 0; or 0 together with T3 = 0 to switch the stage off.</param>
    /// <param name="outputMinPu">Output floor Vsmin (pu).</param>
    /// <param name="outputMaxPu">Output ceiling Vsmax (pu).</param>
    public Pss1a(double gain, double washoutSeconds,
        double lead1Seconds, double lag1Seconds, double lead2Seconds, double lag2Seconds,
        double outputMinPu = double.NegativeInfinity, double outputMaxPu = double.PositiveInfinity)
    {
        if (outputMaxPu < outputMinPu) throw new System.ArgumentException("Vsmax must be >= Vsmin.", nameof(outputMaxPu));
        _ks = gain;
        _washout = new Washout(washoutSeconds);
        _stage1 = Stage(lead1Seconds, lag1Seconds, "T1", "T2");
        _stage2 = Stage(lead2Seconds, lag2Seconds, "T3", "T4");
        _min = outputMinPu;
        _max = outputMaxPu;
    }

    // R083: a stage with both time constants zero is exactly unity (bypassed, null); a zero lag with a non-zero lead is an
    // improper pure derivative and is refused naming the PSS1A parameters; anything else is the ordinary lead-lag block
    // (which validates its own arguments exactly as before).
    private static LeadLag? Stage(double lead, double lag, string leadName, string lagName)
    {
        if (lead == 0.0 && lag == 0.0) return null;
        if (lag == 0.0)
            throw new System.ArgumentOutOfRangeException(lagName,
                $"PSS1A {lagName} = 0 with {leadName} = {lead} is a pure derivative (improper); set both to 0 to switch the stage off.");
        return new LeadLag(lead, lag);
    }

    /// <summary>Stabiliser output Vs (pu) after the last step.</summary>
    public double Output { get; private set; }

    /// <summary>Pre-load every state so a sustained input <paramref name="steadyInputPu"/> gives Vs = 0 (the
    /// washout blocks it and the downstream stages settle to zero).</summary>
    public void Initialize(double steadyInputPu)
    {
        _washout.Initialize(_ks * steadyInputPu);   // washout output 0
        _stage1?.Initialize(0.0);
        _stage2?.Initialize(0.0);
        Output = 0.0;
    }

    /// <summary>Advance one step of <paramref name="dtSeconds"/> with input <paramref name="inputPu"/> (speed
    /// deviation or power, held across the step) and return the stabiliser output Vs (pu).</summary>
    public double Step(double inputPu, double dtSeconds)
    {
        double w = _washout.Step(_ks * inputPu, dtSeconds);
        double v1 = _stage1 is null ? w : _stage1.Step(w, dtSeconds);     // a switched-off stage passes its input through
        double v2 = _stage2 is null ? v1 : _stage2.Step(v1, dtSeconds);
        Output = System.Math.Clamp(v2, _min, _max);
        return Output;
    }
}
