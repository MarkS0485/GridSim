using System;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// IEEET1 - the IEEE Type-1 (DC rotating) excitation system, the standard exciter above the compact
/// <see cref="Sexs"/>. It carries the features SEXS omits: the exciter's own self-excitation constant
/// <c>Ke</c> and open-circuit saturation <c>Se(Efd)</c>, and the rate (stabilising) feedback loop. Structure:
/// <code>
///   Verr = (Vref - Vt + Vs) - Vf                           (regulator error less the rate feedback)
///   Vr   = lag_{Ka, Ta, [Vrmin,Vrmax]}(Verr)               (voltage regulator)
///   Te dEfd/dt = Vr - (Ke + Se(Efd)) * Efd                 (exciter, Se = Aex*exp(Bex*Efd))
///   Vf   = (Kf/Tf) * washout_{Tf}(Efd)  =  sKf/(1+sTf) * Efd
/// </code>
/// All quantities are per-unit. The exciter ODE advances by the exact zero-order hold of its (start-of-step
/// linearised) pole - <c>Se</c> frozen across the step, the same operator splitting used for machine
/// saturation - so it is A-stable and, with <c>Se = 0, Ke = 1, Kf = 0</c>, collapses to a two-lag AVR whose
/// steady field is <c>Efd = Ka*Verr</c> (the SEXS behaviour). Saturation (<c>Aex &gt; 0</c>) raises the
/// regulator voltage needed to hold a given field; the rate feedback adds transient damping and washes out in
/// steady state (<c>Vf -&gt; 0</c>), so it does not bias the setpoint.
/// </summary>
public sealed class Ieeet1
{
    /// <summary>When |Ke + Se| (dimensionless) is at or below this, the exciter pole sits at the origin and the
    /// exciter is integrated as a pure integrator instead of the exact exponential (which would divide by ~0).
    /// R165: this used to borrow <see cref="ControlBlocks.InstantThresholdSeconds"/>, a time constant in
    /// seconds; it is now its own dimensionless tolerance with the same value (1e-12), so nothing moves.</summary>
    public const double ExciterPoleAtOriginTolerance = 1e-12;

    private readonly double _ka, _ta, _ke, _te, _aex, _bex, _kf, _tf;
    private readonly FirstOrderLag _regulator;
    private readonly Washout _feedback;
    private double _efd;

    /// <param name="regulatorGain">Regulator gain Ka; must be &gt; 0.</param>
    /// <param name="regulatorTimeSeconds">Regulator lag Ta (s).</param>
    /// <param name="exciterConstant">Exciter self-excitation constant Ke.</param>
    /// <param name="exciterTimeSeconds">Exciter time constant Te (s); must be &gt; 0.</param>
    /// <param name="feedbackGain">Rate-feedback gain Kf.</param>
    /// <param name="feedbackTimeSeconds">Rate-feedback time constant Tf (s); must be &gt; 0.</param>
    /// <param name="regulatorMinPu">Regulator floor Vrmin (pu).</param>
    /// <param name="regulatorMaxPu">Regulator ceiling Vrmax (pu).</param>
    /// <param name="saturationA">Exciter saturation coefficient Aex in Se = Aex*exp(Bex*Efd); 0 = unsaturated.</param>
    /// <param name="saturationB">Exciter saturation coefficient Bex.</param>
    public Ieeet1(double regulatorGain, double regulatorTimeSeconds, double exciterConstant, double exciterTimeSeconds,
        double feedbackGain, double feedbackTimeSeconds,
        double regulatorMinPu = double.NegativeInfinity, double regulatorMaxPu = double.PositiveInfinity,
        double saturationA = 0.0, double saturationB = 0.0)
    {
        if (regulatorGain <= 0.0) throw new ArgumentOutOfRangeException(nameof(regulatorGain), "Ka must be > 0.");
        if (exciterTimeSeconds <= 0.0) throw new ArgumentOutOfRangeException(nameof(exciterTimeSeconds), "Te must be > 0.");
        if (feedbackTimeSeconds <= 0.0) throw new ArgumentOutOfRangeException(nameof(feedbackTimeSeconds), "Tf must be > 0.");
        if (saturationA < 0.0) throw new ArgumentOutOfRangeException(nameof(saturationA), "Aex must be >= 0.");
        _ka = regulatorGain; _ta = regulatorTimeSeconds; _ke = exciterConstant; _te = exciterTimeSeconds;
        _aex = saturationA; _bex = saturationB; _kf = feedbackGain; _tf = feedbackTimeSeconds;
        _regulator = new FirstOrderLag(regulatorGain, regulatorTimeSeconds, regulatorMinPu, regulatorMaxPu);
        _feedback = new Washout(feedbackTimeSeconds);
    }

    /// <summary>Field voltage Efd (pu) after the last step.</summary>
    public double FieldVoltage => _efd;

    private double Se(double efd) => _aex * DetMath.Exp(_bex * efd);

    /// <summary>Pre-load every state so the field voltage sits at <paramref name="fieldVoltagePu"/> in
    /// equilibrium (rate feedback washed out, regulator holding the required exciter input).</summary>
    public void Initialize(double fieldVoltagePu)
    {
        // R252 (October 2026, stream B3): the regulator must hold Vr = (Ke + Se)Efd at equilibrium, so that value must
        // lie inside [Vrmin, Vrmax]; out of limits is refused instead of clamped silently (Efd was preset unclamped).
        double vr = (_ke + Se(fieldVoltagePu)) * fieldVoltagePu;
        ControlBlocks.RequireEquilibriumWithinLimits("IEEET1", "regulator output Vr = (Ke + Se)*Efd", vr,
            _regulator.Min, _regulator.Max, nameof(fieldVoltagePu));
        _efd = fieldVoltagePu;
        _regulator.Initialize(vr);                                             // Vr = (Ke + Se)Efd
        _feedback.Initialize(fieldVoltagePu);                                  // Vf = 0
    }

    /// <summary>Advance one step of <paramref name="dtSeconds"/> with regulator error
    /// <paramref name="voltageErrorPu"/> (= Vref - Vt + Vs, held across the step) and return Efd (pu).</summary>
    public double Step(double voltageErrorPu, double dtSeconds)
    {
        double vf = (_kf / _tf) * _feedback.OutputFor(_efd);   // sKf/(1+sTf) of Efd, at the current state
        double vr = _regulator.Step(voltageErrorPu - vf, dtSeconds);

        // Exciter: Te dEfd/dt = Vr - (Ke + Se)Efd, Se frozen -> exact ZOH of the linearised pole.
        double keSe = _ke + Se(_efd);
        if (Math.Abs(keSe) <= ExciterPoleAtOriginTolerance)
            _efd += vr / _te * dtSeconds;                       // pole at origin -> integrator (exact for held Vr)
        else
        {
            double a = DetMath.Exp(-dtSeconds * keSe / _te);
            _efd = a * _efd + (1.0 - a) * vr / keSe;
        }

        _feedback.Step(_efd, dtSeconds);                        // advance the feedback washout state
        return _efd;
    }
}
