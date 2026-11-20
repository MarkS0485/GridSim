namespace GridSim.Core.Dynamics.Emt.Nodal;

/// <summary>The right-hand side and matrix under assembly. Rows of a node whose voltage is imposed (an ideal source) are
/// skipped; the solver puts an identity row there.</summary>
internal sealed class NodalStamper
{
    internal readonly int N;
    internal readonly double[] M;
    internal double[] Rhs;
    private readonly bool[] _fixed;

    internal NodalStamper(int n, bool[] fixedNode)
    {
        N = n;
        M = new double[n * n];
        Rhs = new double[n];
        _fixed = fixedNode;
    }

    internal void Clear()
    {
        Array.Clear(M);
        Array.Clear(Rhs);
    }

    /// <summary>Add <paramref name="value"/> at (row, col); ignored for a ground row or column, or a fixed row.</summary>
    internal void Add(int row, int col, double value)
    {
        if (row < 0 || col < 0 || _fixed[row]) return;
        M[row * N + col] += value;
    }

    /// <summary>A conductance g between nodes a and b (either may be ground).</summary>
    internal void AddConductance(int a, int b, double g)
    {
        Add(a, a, g);
        Add(b, b, g);
        Add(a, b, -g);
        Add(b, a, -g);
    }

    /// <summary>A current <paramref name="i"/> flowing INTO <paramref name="node"/> from outside.</summary>
    internal void AddCurrentInto(int node, double i)
    {
        if (node < 0 || _fixed[node]) return;
        Rhs[node] += i;
    }
}

/// <summary>
/// An element of a <see cref="NodalNetwork"/>. The element set is closed (the members are internal) except for the
/// extension point <see cref="Nonlinear.NonlinearElement"/>. Per step the solver calls, in element order: StampSources
/// (history terms and source values at the END of the step), the nonlinear Newton stamps, then Accept (history update).
/// </summary>
public abstract class NodalElement
{
    internal NodalElement(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An element needs a name.", nameof(name));
        Name = name;
    }

    /// <summary>The unique element name.</summary>
    public string Name { get; }

    internal virtual bool IsNonlinear => false;

    /// <summary>Node ids with an imposed voltage (an ideal source), if any.</summary>
    internal virtual int FixedNode => -2;

    /// <summary>Check the element against the step and the node count before a run; throw ArgumentException to refuse.</summary>
    internal virtual void Validate(double dt, int nodeCount) { }

    /// <summary>Recompute the companion model for a step size and rule.</summary>
    internal abstract void Configure(double dt, IntegrationRule rule);

    /// <summary>Back to the initial (rest) state.</summary>
    internal abstract void Reset();

    /// <summary>Linear conductance stamps (the constant part of the nodal matrix).</summary>
    internal virtual void StampMatrix(NodalStamper s) { }

    /// <summary>History currents and source values for the step ending at <paramref name="t"/>.</summary>
    internal virtual void StampSources(double t, NodalStamper s) { }

    /// <summary>Newton linearisation about the node voltages <paramref name="v"/> (nonlinear elements only).</summary>
    internal virtual void StampNewton(double[] v, NodalStamper s) { }

    /// <summary>Add the exact current this element draws from each node (leaving the node) at <paramref name="v"/> into <paramref name="f"/>.</summary>
    internal virtual void AddNonlinearCurrent(double[] v, double[] f) { }

    /// <summary>Accept the converged step: update the histories.</summary>
    internal abstract void Accept(double t, double[] v);

    /// <summary>The element's current number <paramref name="channel"/> at the last accepted step.</summary>
    internal virtual double ProbeCurrent(int channel) => double.NaN;

    /// <summary>The element's state variable (flux linkage for a saturable inductor), if it has one.</summary>
    internal virtual double ProbeState() => double.NaN;

    /// <summary>RC4 R4-17: state variable number <paramref name="channel"/> (machines: speed, angle, torque ...). The default
    /// is <see cref="ProbeState"/>, so every R4-16 element reads exactly as before.</summary>
    internal virtual double ProbeStateAt(int channel) => ProbeState();

    /// <summary>RC4 R4-17: true when the element decides its own switching state from the circuit (a diode, a thyristor or
    /// a gated valve). The solver calls <see cref="SwitchLogic"/> at every step, before it picks the integration rule.</summary>
    internal virtual bool HasSwitchLogic => false;

    /// <summary>RC4 R4-17: decide the element's state for the step ending at <paramref name="t"/> from the LAST ACCEPTED
    /// voltages and currents (events are quantised to the step grid, so a device switches at the first step boundary after
    /// its condition is met: an error of at most one step). Return true when the state, and so the matrix stamp, changed.</summary>
    internal virtual bool SwitchLogic(double t) => false;

    /// <summary>RC4 R4-17: true when the matrix stamp changes at every step (a rotating machine's rotor angle): the solver
    /// restamps and refactorises each step.</summary>
    internal virtual bool IsMatrixVarying => false;

    /// <summary>RC4 R4-17: called each step after the integration rule is configured and before the matrix is stamped, for
    /// elements that are <see cref="IsMatrixVarying"/>: build the step's companion model.</summary>
    internal virtual void PrepareStep(double t, double dt) { }

    /// <summary>The number of current channels the element offers.</summary>
    internal virtual int CurrentChannels => 1;

    /// <summary>Largest source or branch current seen at this element (for the Newton residual scale), amps.</summary>
    internal virtual double CurrentScale => 0.0;

    internal static double V(double[] v, int node) => node < 0 ? 0.0 : v[node];
}

/// <summary>
/// A two-terminal linear branch from node a to node b with a trapezoidal / backward-Euler companion model: resistor,
/// inductor, capacitor, series R-L or series R-C. Positive current is a to b. Built through the static factories.
/// </summary>
public sealed class NodalBranch : NodalElement
{
    private enum Kind { R, L, C, RL, RC }

    private readonly Kind _kind;
    private readonly int _a, _b;
    private readonly double _r, _l, _c, _i0, _v0;
    private Companion _comp;
    private double _vPrev, _iPrev, _hist;

    private NodalBranch(string name, Kind kind, int a, int b, double r, double l, double c, double i0, double v0) : base(name)
    {
        _kind = kind; _a = a; _b = b; _r = r; _l = l; _c = c; _i0 = i0; _v0 = v0;
        if (a == b) throw new ArgumentException($"{name}: both terminals are the same node.");
        double[] vals = { r, l, c, i0, v0 };
        foreach (double x in vals)
            if (!double.IsFinite(x)) throw new ArgumentException($"{name}: a parameter is not finite.");
        Reset();
    }

    /// <summary>A resistor of <paramref name="r"/> ohms (positive).</summary>
    public static NodalBranch Resistor(string name, int a, int b, double r)
    {
        if (!(r > 0)) throw new ArgumentException($"{name}: the resistance must be positive.");
        return new NodalBranch(name, Kind.R, a, b, r, 0, 0, 0, 0);
    }

    /// <summary>An inductor of <paramref name="l"/> henries (positive) with initial current <paramref name="i0"/>.</summary>
    public static NodalBranch Inductor(string name, int a, int b, double l, double i0 = 0.0)
    {
        if (!(l > 0)) throw new ArgumentException($"{name}: the inductance must be positive.");
        return new NodalBranch(name, Kind.L, a, b, 0, l, 0, i0, 0);
    }

    /// <summary>A capacitor of <paramref name="c"/> farads (positive) with initial voltage <paramref name="v0"/> (a minus b).</summary>
    public static NodalBranch Capacitor(string name, int a, int b, double c, double v0 = 0.0)
    {
        if (!(c > 0)) throw new ArgumentException($"{name}: the capacitance must be positive.");
        return new NodalBranch(name, Kind.C, a, b, 0, 0, c, 0, v0);
    }

    /// <summary>A series R-L branch (r not negative, l positive, not both zero) with initial current <paramref name="i0"/>.</summary>
    public static NodalBranch SeriesRl(string name, int a, int b, double r, double l, double i0 = 0.0)
    {
        if (!(r >= 0) || !(l >= 0) || (r == 0 && l == 0)) throw new ArgumentException($"{name}: R and L must not be negative and not both zero.");
        return new NodalBranch(name, Kind.RL, a, b, r, l, 0, i0, 0);
    }

    /// <summary>A series R-C branch (r not negative, c positive) with initial capacitor voltage <paramref name="v0"/>.</summary>
    public static NodalBranch SeriesRc(string name, int a, int b, double r, double c, double v0 = 0.0)
    {
        if (!(r >= 0) || !(c > 0)) throw new ArgumentException($"{name}: R must not be negative and C must be positive.");
        return new NodalBranch(name, Kind.RC, a, b, r, 0, c, 0, v0);
    }

    internal override void Configure(double dt, IntegrationRule rule)
    {
        _comp = _kind switch
        {
            Kind.R => EmtCompanionStamp.Resistor(_r),
            Kind.L => EmtCompanionStamp.Inductor(_l, dt, rule),
            Kind.C => EmtCompanionStamp.Capacitor(_c, dt, rule),
            Kind.RL => EmtCompanionStamp.SeriesRl(_r, _l, dt, rule),
            _ => EmtCompanionStamp.SeriesRc(_r, _c, dt, rule),
        };
    }

    internal override void Reset()
    {
        _vPrev = _kind is Kind.C or Kind.RC ? _v0 : 0.0;
        _iPrev = _kind is Kind.L or Kind.RL ? _i0 : 0.0;
        _hist = 0.0;
    }

    internal override void StampMatrix(NodalStamper s) => s.AddConductance(_a, _b, _comp.Geq);

    internal override void StampSources(double t, NodalStamper s)
    {
        _hist = _comp.History(_vPrev, _iPrev);
        // The history source drives current a to b: it leaves node a and enters node b.
        s.AddCurrentInto(_a, -_hist);
        s.AddCurrentInto(_b, _hist);
    }

    internal override void Accept(double t, double[] v)
    {
        double vb = V(v, _a) - V(v, _b);
        _iPrev = _comp.Geq * vb + _hist;
        _vPrev = vb;
    }

    internal override double ProbeCurrent(int channel) => _iPrev;
}

/// <summary>
/// An n-phase series R-L branch with mutual coupling, from nodes a[k] to b[k]: <c>v = R i + L di/dt</c> with symmetric
/// n by n matrices R (ohm) and L (henry). Trapezoidal companion <c>i(n) = G v(n) + G (v(n-1) + (2L/dt - R) i(n-1))</c>,
/// <c>G = (R + 2L/dt)^-1</c>; backward Euler <c>G = (R + L/dt)^-1</c>, history <c>G (L/dt) i(n-1)</c>.
/// </summary>
public sealed class NodalCoupledRl : NodalElement
{
    private readonly int[] _a, _b;
    private readonly double[,] _r, _l;
    private readonly int _n;
    private double[,] _g;
    private double[,] _hv, _hi;
    private double[] _vPrev, _iPrev, _hist, _i0;

    /// <summary>Create the branch; <paramref name="i0"/> is the optional initial current vector.</summary>
    public NodalCoupledRl(string name, int[] a, int[] b, double[,] r, double[,] l, double[]? i0 = null) : base(name)
    {
        _n = a.Length;
        if (_n == 0 || b.Length != _n || r.GetLength(0) != _n || r.GetLength(1) != _n || l.GetLength(0) != _n || l.GetLength(1) != _n)
            throw new ArgumentException($"{name}: the node lists and the R and L matrices must all have the same size.");
        for (int k = 0; k < _n; k++)
            if (a[k] == b[k]) throw new ArgumentException($"{name}: phase {k} has both terminals on the same node.");
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++)
            {
                if (!double.IsFinite(r[i, j]) || !double.IsFinite(l[i, j])) throw new ArgumentException($"{name}: a matrix entry is not finite.");
                if (Math.Abs(r[i, j] - r[j, i]) > 1e-12 * (1.0 + Math.Abs(r[i, j])) || Math.Abs(l[i, j] - l[j, i]) > 1e-12 * (1.0 + Math.Abs(l[i, j])))
                    throw new ArgumentException($"{name}: R and L must be symmetric.");
            }
        for (int i = 0; i < _n; i++)
            if (!(l[i, i] > 0)) throw new ArgumentException($"{name}: the self inductance of every phase must be positive.");
        _a = (int[])a.Clone(); _b = (int[])b.Clone();
        _r = (double[,])r.Clone(); _l = (double[,])l.Clone();
        _g = new double[_n, _n]; _hv = new double[_n, _n]; _hi = new double[_n, _n];
        _vPrev = new double[_n]; _iPrev = new double[_n]; _hist = new double[_n];
        _i0 = i0 is null ? new double[_n] : (double[])i0.Clone();
        Reset();
    }

    internal override int CurrentChannels => _n;

    internal override void Configure(double dt, IntegrationRule rule)
    {
        var m = new double[_n * _n];
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++)
                m[i * _n + j] = rule == IntegrationRule.BackwardEuler ? _r[i, j] + _l[i, j] / dt : _r[i, j] + 2.0 * _l[i, j] / dt;
        var inv = Invert(m, _n);
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++) _g[i, j] = inv[i * _n + j];
        // history = G (cv v(n-1) + ci i(n-1)): cv = I (trapezoidal) or 0 (backward Euler); ci = 2L/dt - R or L/dt.
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++)
            {
                double ci = rule == IntegrationRule.BackwardEuler ? _l[i, j] / dt : 2.0 * _l[i, j] / dt - _r[i, j];
                _hi[i, j] = ci;
                _hv[i, j] = rule == IntegrationRule.BackwardEuler ? 0.0 : (i == j ? 1.0 : 0.0);
            }
    }

    private static double[] Invert(double[] a, int n)
    {
        var lu = new DenseLu(n);
        if (!lu.Factor(a)) throw new ArgumentException("A coupled branch has a singular R + L/dt matrix.");
        var inv = new double[n * n];
        var col = new double[n];
        for (int j = 0; j < n; j++)
        {
            Array.Clear(col);
            col[j] = 1.0;
            lu.Solve(col);
            for (int i = 0; i < n; i++) inv[i * n + j] = col[i];
        }
        return inv;
    }

    internal override void Reset()
    {
        Array.Clear(_vPrev);
        Array.Copy(_i0, _iPrev, _n);
        Array.Clear(_hist);
    }

    internal override void StampMatrix(NodalStamper s)
    {
        for (int k = 0; k < _n; k++)
            for (int j = 0; j < _n; j++)
            {
                double g = _g[k, j];
                s.Add(_a[k], _a[j], g);
                s.Add(_a[k], _b[j], -g);
                s.Add(_b[k], _a[j], -g);
                s.Add(_b[k], _b[j], g);
            }
    }

    internal override void StampSources(double t, NodalStamper s)
    {
        var rhs = new double[_n];
        for (int j = 0; j < _n; j++)
        {
            double x = 0.0;
            for (int m = 0; m < _n; m++) x += _hv[j, m] * _vPrev[m] + _hi[j, m] * _iPrev[m];
            rhs[j] = x;
        }
        for (int k = 0; k < _n; k++)
        {
            double h = 0.0;
            for (int j = 0; j < _n; j++) h += _g[k, j] * rhs[j];
            _hist[k] = h;
            s.AddCurrentInto(_a[k], -h);
            s.AddCurrentInto(_b[k], h);
        }
    }

    internal override void Accept(double t, double[] v)
    {
        var vb = new double[_n];
        for (int k = 0; k < _n; k++) vb[k] = V(v, _a[k]) - V(v, _b[k]);
        for (int k = 0; k < _n; k++)
        {
            double i = _hist[k];
            for (int j = 0; j < _n; j++) i += _g[k, j] * vb[j];
            _iPrev[k] = i;
        }
        Array.Copy(vb, _vPrev, _n);
    }

    internal override double ProbeCurrent(int channel) => _iPrev[channel];
}

/// <summary>An ideal switch between two nodes: a conductance of 1/Ron closed or 1/Roff open. A fault is a switch to ground.
/// Its state changes only through a <see cref="NodalEvent"/> at a step boundary.</summary>
public sealed class NodalSwitch : NodalElement
{
    private readonly int _a, _b;
    private double _vb;

    /// <summary>Create the switch (Ron and Roff positive, Ron below Roff).</summary>
    public NodalSwitch(string name, int a, int b, double ron, double roff, bool initiallyClosed) : base(name)
    {
        if (a == b) throw new ArgumentException($"{name}: both terminals are the same node.");
        if (!(ron > 0) || !(roff > ron) || !double.IsFinite(roff)) throw new ArgumentException($"{name}: need 0 < Ron < Roff, both finite.");
        _a = a; _b = b; Ron = ron; Roff = roff; InitiallyClosed = initiallyClosed; Closed = initiallyClosed;
    }

    /// <summary>Closed resistance, ohms.</summary>
    public double Ron { get; }
    /// <summary>Open resistance, ohms.</summary>
    public double Roff { get; }
    /// <summary>The state the run starts in.</summary>
    public bool InitiallyClosed { get; }
    /// <summary>The present state.</summary>
    public bool Closed { get; internal set; }

    internal override void Configure(double dt, IntegrationRule rule) { }
    internal override void Reset() { Closed = InitiallyClosed; _vb = 0.0; }
    internal override void StampMatrix(NodalStamper s) => s.AddConductance(_a, _b, 1.0 / (Closed ? Ron : Roff));
    internal override void Accept(double t, double[] v) => _vb = V(v, _a) - V(v, _b);
    internal override double ProbeCurrent(int channel) => _vb / (Closed ? Ron : Roff);
}

/// <summary>A voltage source from a node to ground with EMF <c>Emf(t)</c>. With a series resistance <c>Rs &gt; 0</c> it is a
/// Thevenin source (stamped as its Norton equivalent); with <c>Rs = 0</c> it is ideal and imposes the node voltage.</summary>
public sealed class NodalVoltageSource : NodalElement
{
    private readonly int _node;
    private readonly Func<double, double> _emf;
    private double _eNow, _vNode;

    /// <summary>Create the source at <paramref name="node"/> (not ground).</summary>
    public NodalVoltageSource(string name, int node, double rs, Func<double, double> emf) : base(name)
    {
        if (node < 0) throw new ArgumentException($"{name}: a source needs a node, not ground.");
        if (!(rs >= 0) || !double.IsFinite(rs)) throw new ArgumentException($"{name}: the source resistance must be finite and not negative.");
        _node = node; Rs = rs; _emf = emf ?? throw new ArgumentNullException(nameof(emf));
    }

    /// <summary>Series resistance, ohms (0 = ideal).</summary>
    public double Rs { get; }

    internal override int FixedNode => Rs == 0.0 ? _node : -2;
    internal override void Configure(double dt, IntegrationRule rule) { }
    internal override void Reset() { _eNow = 0.0; _vNode = 0.0; }
    internal override void StampMatrix(NodalStamper s) { if (Rs > 0.0) s.Add(_node, _node, 1.0 / Rs); }

    internal override void StampSources(double t, NodalStamper s)
    {
        _eNow = _emf(t);
        if (Rs > 0.0) s.AddCurrentInto(_node, _eNow / Rs);
        else s.Rhs[_node] = _eNow;   // the solver's identity row
    }

    internal override void Accept(double t, double[] v) => _vNode = v[_node];

    /// <summary>The current the source delivers into the network (NaN for an ideal source: not measured).</summary>
    internal override double ProbeCurrent(int channel) => Rs > 0.0 ? (_eNow - _vNode) / Rs : double.NaN;
}

/// <summary>An independent current source injecting <c>Inj(t)</c> amperes into a node (returned from ground).</summary>
public sealed class NodalCurrentSource : NodalElement
{
    private readonly int _node;
    private readonly Func<double, double> _inj;
    private double _iNow;

    /// <summary>Create the source.</summary>
    public NodalCurrentSource(string name, int node, Func<double, double> inj) : base(name)
    {
        if (node < 0) throw new ArgumentException($"{name}: a source needs a node, not ground.");
        _node = node; _inj = inj ?? throw new ArgumentNullException(nameof(inj));
    }

    internal override void Configure(double dt, IntegrationRule rule) { }
    internal override void Reset() => _iNow = 0.0;
    internal override void StampSources(double t, NodalStamper s) { _iNow = _inj(t); s.AddCurrentInto(_node, _iNow); }
    internal override void Accept(double t, double[] v) { }
    internal override double ProbeCurrent(int channel) => _iNow;
}
