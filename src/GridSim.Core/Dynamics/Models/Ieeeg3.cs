namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// IEEEG3, the IEEE 1973 hydro turbine-governor (PSS/E IEEEG3, data TG TP Uo Uc PMAX PMIN sigma delta TR TW a11 a13 a21
/// a23): a mechanical-hydraulic governor with permanent and transient (dashpot) droop driving the linear water-column
/// turbine of the a-coefficients. Structure (pu on the machine base; dw the speed deviation):
/// <code>
///   err  = P0 - dw - sigma g - delta sTR/(1 + sTR) g          (permanent droop sigma, transient droop delta over TR)
///   v    = 1 / (TG (1 + sTP)) err, held in [Uc, Uo]           (pilot valve and gate servo: the gate velocity)
///   g    = integral of v, held in [PMIN, PMAX]                (gate position)
///   Pm   = (a23 + (a11 a23 - a13 a21) TW s) / (1 + a11 TW s) g   (the water column)
/// </code>
/// With the classical ideal-turbine values a11 = 0.5, a13 = 1, a21 = 1.5, a23 = 1 the turbine is (1 - TW s)/(1 + 0.5 TW s).
/// The turbine is a direct feedthrough c g plus a lag: Pm = c g + lag_{a11 TW}((a23 - c) g) with c = (a11 a23 - a13 a21) / a11,
/// so its exact zero-order hold reproduces the continuous response at every sample for a held gate (the water-hammer dip
/// included). The velocity lag is the exact hold of its pole; the gate integrates the new velocity over the step (exact for
/// a held velocity) and the error reads the start-of-step gate and dashpot, so the droop loop is explicit. That loop is
/// refused when a step would flip the sign of a gate perturbation, (sigma + delta) dt / TG above 1.
/// <para>Initialisation: the gate g0 = Pm0 / a23 (inside [PMIN, PMAX], else refused), the velocity 0, the dashpot washed
/// out, and the reference P0 = sigma g0. Changing the reference moves the steady gate by dP0 / sigma.</para>
/// <para>Not modelled: deadbands (PowerWorld's IEEEG3D), and a second turbine (IEEEG3 has one).</para>
/// </summary>
public sealed class Ieeeg3 : IRmsGovernor
{
    private const string Who = "IEEEG3";
    private readonly double _tg, _uo, _uc, _pMax, _pMin, _sigma, _delta, _a23, _c;
    private readonly FirstOrderLag _velocity;
    private readonly Washout _dashpot;
    private readonly FirstOrderLag _water;
    private double _g, _p0;

    /// <summary>The IEEEG3 data in PSS/E order.</summary>
    public Ieeeg3(double tg, double tp, double uo, double uc, double pMax, double pMin, double sigma, double delta, double tr, double tw,
        double a11, double a13, double a21, double a23)
    {
        LibraryChecks.Positive(Who, "TG", tg);
        LibraryChecks.NonNegative(Who, "TP", tp);
        LibraryChecks.Positive(Who, "Uo", uo);
        if (!(uc < 0) || !double.IsFinite(uc)) throw new ArgumentOutOfRangeException(nameof(uc), uc, "IEEEG3: Uc (the closing rate) must be a finite number below 0.");
        LibraryChecks.Limits(Who, "PMIN", pMin, "PMAX", pMax);
        LibraryChecks.Positive(Who, "sigma", sigma);
        LibraryChecks.NonNegative(Who, "delta", delta);
        LibraryChecks.Positive(Who, "TR", tr);
        LibraryChecks.Positive(Who, "TW", tw);
        LibraryChecks.Positive(Who, "a11", a11);
        LibraryChecks.Positive(Who, "a23", a23);
        foreach ((double v, string n) in new[] { (a13, "a13"), (a21, "a21") })
            if (!double.IsFinite(v)) throw new ArgumentOutOfRangeException(n, v, $"IEEEG3: {n} must be finite.");
        _tg = tg; _uo = uo; _uc = uc; _pMax = pMax; _pMin = pMin; _sigma = sigma; _delta = delta; _a23 = a23;
        _c = (a11 * a23 - a13 * a21) / a11;
        _velocity = new FirstOrderLag(1.0 / tg, tp, uc, uo);
        _dashpot = new Washout(tr);
        _water = new FirstOrderLag(a23 - _c, a11 * tw);
    }

    /// <summary>The gate position g after the last step.</summary>
    public double GatePosition => _g;

    /// <summary>The turbine block alone: the mechanical power for gate <paramref name="gate"/> held over one step (no governor).
    /// Advances the water-column state; used by <see cref="Step"/> and by tests that drive the turbine directly.</summary>
    internal double TurbineStep(double gate, double dtSeconds) => _c * gate + _water.Step(gate, dtSeconds);

    /// <summary>Pre-load the turbine at a steady gate (the water-column state at rest). For tests of the turbine alone.</summary>
    internal void InitializeTurbine(double gate) => _water.Initialize((_a23 - _c) * gate);

    /// <inheritdoc/>
    public void Initialize(double mechanicalPowerPu)
    {
        if (!double.IsFinite(mechanicalPowerPu)) throw new ArgumentOutOfRangeException(nameof(mechanicalPowerPu), mechanicalPowerPu, "IEEEG3: the initial mechanical power is not finite.");
        double g0 = mechanicalPowerPu / _a23;
        ControlBlocks.RequireEquilibriumWithinLimits(Who, "gate position g = Pm / a23", g0, _pMin, _pMax, nameof(mechanicalPowerPu));
        _g = g0;
        _p0 = _sigma * g0;
        _velocity.Initialize(0.0);
        _dashpot.Initialize(g0);
        InitializeTurbine(g0);
    }

    /// <summary>Set the reference to the mechanical power <paramref name="referencePu"/>, the quantity
    /// <see cref="Initialize"/> takes (the steady gate is Pm / a23 and P0 = sigma times it), without touching a state.
    /// DYN-13: it took its argument as a gate, so with a23 other than 1 SetReference(Pm0) right after Initialize(Pm0) moved
    /// the equilibrium (GovernedMultiMachine passes a power, as to every other governor). With a23 = 1 it is the same
    /// number; setting the initial power again is an exact no-op.</summary>
    public void SetReference(double referencePu)
    {
        if (!double.IsFinite(referencePu)) throw new ArgumentOutOfRangeException(nameof(referencePu), referencePu, "IEEEG3: the reference must be finite.");
        _p0 = _sigma * (referencePu / _a23);
    }

    /// <inheritdoc/>
    public double Step(double speedDeviationPu, double dtSeconds)
    {
        double factor = (_sigma + _delta) * dtSeconds / _tg;
        if (factor > 1.0)
            throw new ArgumentOutOfRangeException(nameof(dtSeconds), dtSeconds, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"IEEEG3: a {dtSeconds:R} s step with TG = {_tg:R} s and sigma + delta = {_sigma + _delta:R} would flip the sign of a gate perturbation each step; use a step of at most {_tg / (_sigma + _delta):G4} s."));
        double err = _p0 - speedDeviationPu - _sigma * _g - _delta * _dashpot.OutputFor(_g);
        double v = _velocity.Step(err, dtSeconds);
        _g = Math.Clamp(_g + v * dtSeconds, _pMin, _pMax);
        _dashpot.Step(_g, dtSeconds);
        return TurbineStep(_g, dtSeconds);
    }
}

/// <summary>WP19 (RC5): IEEEG3 parameters (see <see cref="Ieeeg3"/>'s constructor, PSS/E order; pu on the machine base).</summary>
public sealed record Ieeeg3Data(double Tg, double Tp, double Uo, double Uc, double Pmax, double Pmin, double Sigma, double Delta,
    double Tr, double Tw, double A11, double A13, double A21, double A23) : GovernorData
{
    /// <inheritdoc/>
    public override string Model => "IEEEG3";

    /// <inheritdoc/>
    public override IRmsGovernor Create() => new Ieeeg3(Tg, Tp, Uo, Uc, Pmax, Pmin, Sigma, Delta, Tr, Tw, A11, A13, A21, A23);
}
