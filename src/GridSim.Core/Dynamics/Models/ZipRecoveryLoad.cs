using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP3b (RC5): a static voltage characteristic P = P0 (C1 u^E1 + C2 u^E2 + (1 - C1 - C2)), u = |V| / |V0|: the ZIP load
/// (<see cref="Zip"/>: E1 = 2 constant impedance, E2 = 1 constant current, the rest constant power) and the WECC CMLD
/// static load (two exponential terms and a constant).
/// </summary>
public sealed record StaticLoadCharacteristic(double C1, double E1, double C2, double E2)
{
    /// <summary>Constant impedance <paramref name="constantImpedance"/>, constant current <paramref name="constantCurrent"/>,
    /// constant power the rest.</summary>
    public static StaticLoadCharacteristic Zip(double constantImpedance, double constantCurrent) => new(constantImpedance, 2.0, constantCurrent, 1.0);

    /// <summary>All constant power.</summary>
    public static StaticLoadCharacteristic ConstantPower { get; } = new(0.0, 0.0, 0.0, 0.0);

    internal void Check(string who)
    {
        LoadChecks.Finite(who, "C1", C1);
        LoadChecks.Finite(who, "E1", E1);
        LoadChecks.Finite(who, "C2", C2);
        LoadChecks.Finite(who, "E2", E2);
    }

    /// <summary>The power per unit of P0 at u.</summary>
    public double Factor(double u) => C1 * Term(u, E1) + C2 * Term(u, E2) + (1.0 - C1 - C2);

    private static double Term(double u, double e) => e == 0.0 ? 1.0 : e == 1.0 ? u : e == 2.0 ? u * u : DetMath.Pow(u, e);
}

/// <summary>
/// WP3b (RC5): a dynamic ZIP load with exponential recovery. Of P0 = <see cref="DynamicLoadData.PMw"/>, the share
/// 1 - fr follows the static characteristic <paramref name="StaticP"/> and the share fr recovers (Karlsson and Hill's
/// exponential recovery load, IEEE Trans. Power Systems 9(1), 1994):
/// <code>
///   P = P0 ((1 - fr) StaticP(u) + xp + fr u^at),   Tp dxp/dt = -xp + fr (u^as - u^at),   u = |V| / |V0|
/// </code>
/// and likewise for Q with fq, bs, bt, Tq. After a voltage step the recovering share jumps by the transient exponent at and
/// settles, with time constant Tp, at the steady-state exponent as. Below <paramref name="LowVoltagePu"/> every term is
/// evaluated at that voltage and scaled by (|V| / VLOW)^2 (a constant impedance, as PSS/E's PQBRAK does), so the current
/// falls to 0 with the voltage. The initial admittance is stamped in the network; the load injects the difference.
/// </summary>
public sealed record ZipRecoveryLoadData(int BusId, double PMw, double QMvar,
    StaticLoadCharacteristic StaticP, double RecoveryFractionP, double AlphaS, double AlphaT, double TpSeconds,
    StaticLoadCharacteristic StaticQ, double RecoveryFractionQ, double BetaS, double BetaT, double TqSeconds,
    double LowVoltagePu = 0.7) : DynamicLoadData(BusId, PMw, QMvar)
{
    /// <inheritdoc/>
    public override string Model => "ZIP with exponential recovery";

    internal override DynamicLoadPlan Plan(Complex v0, double baseMva, double nominalHz)
    {
        string who = Label;
        ArgumentNullException.ThrowIfNull(StaticP);
        ArgumentNullException.ThrowIfNull(StaticQ);
        StaticP.Check(who);
        StaticQ.Check(who);
        LoadChecks.Finite(who, "PMw", PMw);
        LoadChecks.Finite(who, "QMvar", QMvar);
        foreach ((string n, double f) in new[] { ("fr", RecoveryFractionP), ("fq", RecoveryFractionQ) })
            if (!(f >= 0.0 && f <= 1.0)) throw new ArgumentOutOfRangeException(n, f, $"{who}: the recovering share {n} must lie in [0, 1].");
        foreach ((string n, double x) in new[] { ("as", AlphaS), ("at", AlphaT), ("bs", BetaS), ("bt", BetaT) }) LoadChecks.Finite(who, n, x);
        LibraryChecks.NonNegative(who, "Tp", TpSeconds);
        LibraryChecks.NonNegative(who, "Tq", TqSeconds);
        LibraryChecks.NonNegative(who, "VLOW", LowVoltagePu);
        double vm = v0.Magnitude;
        if (!(vm >= LowVoltagePu) || !(vm > 0.0))
            throw LoadChecks.OutOfRange("LowVoltagePu", LowVoltagePu, $"{who}: the solved voltage {ControlBlocks.Num(vm)} pu is below VLOW, so the load would start on its low-voltage branch.");
        double p0 = PMw / baseMva, q0 = QMvar / baseMva;
        Complex y0 = Complex.Conjugate(new Complex(p0, q0)) / (vm * vm);
        return new Plan_(this, p0, q0, vm, y0);
    }

    private sealed class Plan_(ZipRecoveryLoadData d, double p0, double q0, double v0, Complex y0) : DynamicLoadPlan
    {
        internal override Complex NortonAdmittance => y0;

        internal override IRmsConverterModel CreateModel() => new State(d, p0, q0, v0, y0);
    }

    private static double Pw(double u, double e) => e == 0.0 ? 1.0 : e == 1.0 ? u : e == 2.0 ? u * u : DetMath.Pow(u, e);

    private sealed class State : DynamicLoadState
    {
        private readonly ZipRecoveryLoadData _d;
        private readonly double _p0, _q0, _v0;
        private readonly FirstOrderLag _xp, _xq;

        internal State(ZipRecoveryLoadData d, double p0, double q0, double v0, Complex y0) : base(y0)
        {
            _d = d; _p0 = p0; _q0 = q0; _v0 = v0;
            _xp = new FirstOrderLag(1.0, d.TpSeconds);
            _xq = new FirstOrderLag(1.0, d.TqSeconds);
            _xp.Initialize(0.0);
            _xq.Initialize(0.0);
        }

        /// <summary>The power drawn at |V| = <paramref name="vm"/> (system pu), the recovery states held.</summary>
        internal (double P, double Q) Power(double vm)
        {
            (double ve, double scale) = LoadChecks.LowVoltage(vm, _d.LowVoltagePu);
            double u = ve / _v0;
            double p = _p0 * ((1.0 - _d.RecoveryFractionP) * _d.StaticP.Factor(u) + _xp.Output + _d.RecoveryFractionP * Pw(u, _d.AlphaT));
            double q = _q0 * ((1.0 - _d.RecoveryFractionQ) * _d.StaticQ.Factor(u) + _xq.Output + _d.RecoveryFractionQ * Pw(u, _d.BetaT));
            return scale == 1.0 ? (p, q) : (p * scale, q * scale);
        }

        protected override Complex Drawn(Complex v)
        {
            double vm = v.Magnitude;
            if (!(vm > 0.0)) return Complex.Zero;
            (double p, double q) = Power(vm);
            return Complex.Conjugate(new Complex(p, q) / v);
        }

        protected override void Advance(Complex v, double dt)
        {
            (double ve, _) = LoadChecks.LowVoltage(v.Magnitude, _d.LowVoltagePu);
            double u = ve / _v0;
            _xp.Step(_d.RecoveryFractionP * (Pw(u, _d.AlphaS) - Pw(u, _d.AlphaT)), dt);
            _xq.Step(_d.RecoveryFractionQ * (Pw(u, _d.BetaS) - Pw(u, _d.BetaT)), dt);
        }

        internal double RecoveryP => _xp.Output;
    }
}
