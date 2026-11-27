using GridSim.Core.Dynamics.Plugins;

namespace GridSim.Core.Dynamics.UserModels;

/// <summary>
/// RC5 WP12: a declarative model of role <c>injection</c> as an <see cref="IUserInjection"/>, so the kernel takes it by the
/// same route as a plug-in. Input port <c>vm</c> is the terminal |V| (pu); output ports <c>ip</c> and <c>iq</c> are the
/// active and the reactive current, exporting positive, on the unit's MBASE. The current is
/// <c>I = (ip - j iq) (v / |v|) MBASE / Sb</c> (the unit phasor <c>v / |v|</c> is <c>e^{j theta}</c> without a
/// transcendental function). The initialisation inputs are <c>p0</c>, <c>q0</c> (the solved export on the MBASE) and
/// <c>v0</c> (|V0|). The current is the held evaluation (<see cref="ModelInstance.EvaluateHeld"/>), so it is pure in v; the
/// states advance once per step in <see cref="Step"/>. A pure current source (no Norton admittance).
/// </summary>
public sealed class DeclarativeInjection : IUserInjection
{
    private readonly ModelInstance _m;
    private readonly double[] _in = new double[1];
    private double _rating = 1.0;
    private double _ip, _iq;

    /// <summary>Wrap an instance of an injection-role model.</summary>
    public DeclarativeInjection(ModelInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Document.Role != "injection")
            throw new ArgumentException($"{instance.Document.Id} has role '{instance.Document.Role}', not 'injection'.", nameof(instance));
        _m = instance;
    }

    /// <inheritdoc />
    public (double Re, double Im) NortonAdmittance => (0.0, 0.0);

    /// <inheritdoc />
    public void Initialize(in InjectionStart s)
    {
        if (!(s.RatingPu > 0.0) || !double.IsFinite(s.RatingPu))
            throw new ArgumentOutOfRangeException(nameof(s), s.RatingPu, $"{_m.Document.Id}: the unit's rating must be above 0.");
        _rating = s.RatingPu;
        double v0 = Magnitude(s.V0Re, s.V0Im);
        Span<double> init = stackalloc double[3];
        init[0] = s.P0 / _rating; init[1] = s.Q0 / _rating; init[2] = v0;
        _m.Initialize(init);
    }

    /// <inheritdoc />
    public (double Re, double Im) Current(double vRe, double vIm)
    {
        double vm = Magnitude(vRe, vIm);
        _in[0] = vm;
        _m.EvaluateHeld(_in);
        double ip = _m.Output(0), iq = _m.Output(1);
        if (!(vm > 0.0)) return (0.0, 0.0);
        double ur = vRe / vm, ui = vIm / vm;
        return ((ip * ur + iq * ui) * _rating, (ip * ui - iq * ur) * _rating);
    }

    /// <inheritdoc />
    public void Commit(double vRe, double vIm)
    {
        _in[0] = Magnitude(vRe, vIm);
        _m.EvaluateHeld(_in);
        _ip = _m.Output(0) * _rating;
        _iq = _m.Output(1) * _rating;
    }

    /// <inheritdoc />
    public void Step(double t, double vRe, double vIm, double dt)
    {
        _in[0] = Magnitude(vRe, vIm);
        _m.Step(_in, dt);
    }

    /// <inheritdoc />
    public double ActiveCurrent => _ip;

    /// <inheritdoc />
    public double ReactiveCurrent => _iq;

    /// <inheritdoc />
    public bool InDip => false;

    internal static double Magnitude(double re, double im) => new System.Numerics.Complex(re, im).Magnitude;
}

/// <summary>
/// RC5 WP12: a declarative model of role <c>load</c> as an <see cref="IUserLoad"/>. Input port <c>vm</c> is the terminal |V|;
/// output ports <c>p</c> and <c>q</c> are the drawn active and reactive power as multiples of the load's PMw and QMvar. The
/// drawn current is <c>I = conj(S / v)</c> with <c>S = p P0 + j q Q0</c> (system pu). Its Norton admittance is the one the
/// constant-admittance bus load would have had at the solved voltage, <c>conj(S0) / max(|V0|^2, floor)</c>, so a model that
/// keeps <c>p = q = (vm / v0)^2</c> leaves the network exactly as the static load did. Initialisation inputs: <c>p0</c>,
/// <c>q0</c> (the drawn power, system pu) and <c>v0</c>.
/// </summary>
public sealed class DeclarativeLoad : IUserLoad
{
    private readonly ModelInstance _m;
    private readonly double[] _in = new double[1];
    private double _p0, _q0;
    private (double, double) _y;
    private double _ip, _iq;

    /// <summary>Wrap an instance of a load-role model.</summary>
    public DeclarativeLoad(ModelInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Document.Role != "load")
            throw new ArgumentException($"{instance.Document.Id} has role '{instance.Document.Role}', not 'load'.", nameof(instance));
        _m = instance;
    }

    /// <inheritdoc />
    public (double Re, double Im) NortonAdmittance => _y;

    /// <inheritdoc />
    public void Initialize(in InjectionStart s)
    {
        _p0 = s.P0;
        _q0 = s.Q0;
        double v0 = DeclarativeInjection.Magnitude(s.V0Re, s.V0Im);
        // The same expression the network builder stamps for a constant-admittance bus load.
        double v2 = Math.Max(GridSim.Core.Dynamics.Models.SubtransientInit.MinVoltageSquaredPu, v0 * v0);
        System.Numerics.Complex y = System.Numerics.Complex.Conjugate(new System.Numerics.Complex(s.P0, s.Q0)) / v2;
        _y = (y.Real, y.Imaginary);
        Span<double> init = stackalloc double[3];
        init[0] = s.P0; init[1] = s.Q0; init[2] = v0;
        _m.Initialize(init);
    }

    /// <inheritdoc />
    public (double Re, double Im) Current(double vRe, double vIm)
    {
        _in[0] = DeclarativeInjection.Magnitude(vRe, vIm);
        _m.EvaluateHeld(_in);
        var sv = new System.Numerics.Complex(_m.Output(0) * _p0, _m.Output(1) * _q0);
        var v = new System.Numerics.Complex(vRe, vIm);
        if (v == System.Numerics.Complex.Zero) return (0.0, 0.0);
        System.Numerics.Complex i = System.Numerics.Complex.Conjugate(sv / v);
        return (i.Real, i.Imaginary);
    }

    /// <inheritdoc />
    public void Commit(double vRe, double vIm)
    {
        (_ip, _iq) = Current(vRe, vIm);
    }

    /// <inheritdoc />
    public void Step(double t, double vRe, double vIm, double dt)
    {
        _in[0] = DeclarativeInjection.Magnitude(vRe, vIm);
        _m.Step(_in, dt);
    }

    /// <inheritdoc />
    public double ActiveCurrent => _ip;

    /// <inheritdoc />
    public double ReactiveCurrent => _iq;

    /// <inheritdoc />
    public bool InDip => false;
}
