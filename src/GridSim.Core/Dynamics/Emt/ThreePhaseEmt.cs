using System.Diagnostics;
using GridSim.Core.Numerics;
using GridSim.Core.Simulation;

namespace GridSim.Core.Dynamics.Emt;

/// <summary>An ideal balanced three-phase voltage source at a bus: phase p (a,b,c = 0,1,2) imposes
/// <c>Amplitude·cos(2π·FreqHz·t − p·120°)</c>. Its three phase nodes are Dirichlet (the network reference).</summary>
/// <remarks>R138 nominal-frequency part (D2, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the source
/// frequency used to default to the GB 50 Hz independently of the engine, so a source in an engine built at 60 Hz
/// still ran at 50 Hz unless FreqHz was given. <see cref="FreqHz"/> is now nullable: null (the default) means "the
/// engine's own frequency" (<see cref="ThreePhaseEmtEngine"/>'s <c>frequencyHz</c>, itself the GB nominal by default),
/// and an explicit value (an off-nominal source, a frequency scan) still wins. An engine built at 50 Hz with default
/// sources therefore runs exactly as before, bit for bit (the same 50.0 reaches the cosine).</remarks>
public sealed record ThreePhaseSource(int Bus, double AmplitudePu = 1.0, double? FreqHz = null);

/// <summary>A symmetric three-phase coupled R-L branch between two buses: equal self impedance (Rs, Ls) on the
/// diagonal and equal mutual impedance (Rm, Lm) off-diagonal - the standard transposed-line model. The
/// positive-/negative-sequence impedance is (Rs−Rm)+jω(Ls−Lm); the zero-sequence is (Rs+2Rm)+jω(Ls+2Lm).</summary>
public sealed record ThreePhaseBranch(int FromBus, int ToBus, double Rs, double Ls, double Rm = 0, double Lm = 0);

/// <summary>A per-phase (uncoupled) shunt to ground at a bus: conductance G, capacitance C, inductance L on
/// each of the three phases - a balanced load or shunt.</summary>
public sealed record ThreePhaseShunt(int Bus, double G = 0, double C = 0, double L = 0);

/// <summary>A shunt fault: a resistance <paramref name="Rf"/> from the given phases of a bus to ground, applied
/// at <paramref name="AtSeconds"/> and cleared at <paramref name="ClearSeconds"/>. A single-line-to-ground
/// fault is <c>Phases = new[]{0}</c>.</summary>
public sealed record ThreePhaseFault(int Bus, int[] Phases, double Rf, double AtSeconds, double ClearSeconds);

/// <summary>
/// A grid-following converter at a bus, as an <b>average-value</b> model: a balanced three-phase current source
/// whose phasor comes from the same control law as the RMS engine (<see cref="Models.RmsConverterState"/>:
/// K-factor reactive boost in a dip, current limit with reactive priority, LVACM, active-current freeze and
/// ramp-limited recovery, or blocking for a line-commutated terminal), synchronised by a synchronous-reference-
/// frame PLL on the terminal voltage. Voltages and currents are per-unit peak phase quantities (a phasor's
/// magnitude is the waveform amplitude), so the RMS and EMT models share one per-unit system.
/// </summary>
/// <param name="Bus">Bus the converter connects to.</param>
/// <param name="Unit">Rating, operating point (P0, Q0, V0) and control parameters.</param>
/// <param name="PllBandwidthHz">SRF-PLL natural frequency (Hz), damping <see cref="ConverterDefaults.PllDampingRatio"/>. The
/// default is the one grid-following PLL default the impedance and small-signal models also use (R138: the same 20 Hz
/// this record always defaulted to, so no EMT number moves).</param>
/// <param name="FilterSusceptancePu">AC filter capacitor per phase, as susceptance at nominal frequency in pu of
/// the unit's rating. A current source feeding a purely inductive node rings numerically under trapezoidal
/// integration; the filter a real converter has anyway gives the node a capacitive path. Its reactive power is
/// part of the circuit - include it in any phasor comparison.</param>
/// <param name="StartupRampSeconds">The injected current ramps from zero over this time from t = 0, so a
/// network started from rest is not hit by a current step.</param>
/// <remarks>A grid-forming unit (<c>Unit.Parameters.GridForming</c>) is an EMF behind its virtual reactance: a
/// per-phase shunt inductor <c>Xv / w0</c> plus the Norton current, with the same swing, voltage loop and current
/// limit as the RMS model.</remarks>
public sealed record ThreePhaseConverter(int Bus, Models.RmsConverterUnit Unit, double PllBandwidthHz = ConverterDefaults.PllBandwidthHz,
    double FilterSusceptancePu = 0.05, double StartupRampSeconds = 0.02);

/// <summary>One converter's EMT trajectory, sampled every step: the PLL-measured terminal voltage magnitude, the
/// fundamental active/reactive power and current, and the dip state.</summary>
public sealed record EmtConverterTrace(
    int Bus,
    IReadOnlyList<double> TimeSeconds,
    IReadOnlyList<double> TerminalVoltagePu,
    IReadOnlyList<double> ActivePowerPu,
    IReadOnlyList<double> ReactivePowerPu,
    IReadOnlyList<double> ActiveCurrentPu,
    IReadOnlyList<double> ReactiveCurrentPu,
    IReadOnlyList<bool> InDipMode);

/// <summary>A three-phase EMT network: buses (each three nodes a,b,c), coupled branches, per-phase shunts,
/// ideal three-phase sources, shunt faults and (optionally) grid-following converters.</summary>
public sealed record ThreePhaseNetwork(
    int BusCount,
    IReadOnlyList<ThreePhaseSource> Sources,
    IReadOnlyList<ThreePhaseBranch> Branches,
    IReadOnlyList<ThreePhaseShunt> Shunts,
    IReadOnlyList<ThreePhaseFault> Faults,
    IReadOnlyList<ThreePhaseConverter>? Converters = null);

/// <summary>
/// A three-phase (abc) EMT engine. Each bus is three phase nodes; a coupled branch stamps the 3×3 companion of
/// its (R,L) matrices, so inter-phase mutual coupling is handled exactly - the difference between this and three
/// independent single-phase circuits, and what lets it reproduce unbalanced faults. Method, guard and CDA-free
/// trapezoidal integration mirror the single-phase <see cref="EmtEngine"/>; this is the abc generalization the
/// scalar engine's node model was designed to allow. Same governing constraint: forward simulation about the
/// model, not inference.
///
/// <para><b>Status (R077 decision, October 2026): programmatic and test-only.</b> No CLI verb, web page or
/// desktop view drives this engine, and none is planned in the current programme; it is reached from code
/// (the validation tests EmtConverterTests and ThreePhaseEmtTests are its only callers). It has no
/// <see cref="GridSim.Core.Model.GridModel"/> builder, so it cannot be pointed at a bundled case. Exposing it
/// would need a three-phase network builder from a model (winding connections, zero-sequence data) and a
/// verb with its own validation; until that is specified it stays a library engine.</para>
///
/// <para>One engine is one run (R077): <see cref="Simulate"/> may be called once. A second call would continue
/// from the end state while restarting time at zero, so it throws <see cref="InvalidOperationException"/>;
/// build a new engine for a new run.</para>
/// </summary>
public sealed class ThreePhaseEmtEngine
{
    private readonly int _n;                       // total phase nodes = 3 * BusCount
    private readonly double _dt, _omega;

    // R077: set by the first Simulate; a second Simulate on the same engine is refused (see the class doc).
    private bool _simulated;
    private readonly int[] _freeIndex;
    private readonly int _freeCount;
    private readonly double[] _vDir, _vNode, _rhs;

    // Coupled branch state: phase node ids at each end, the 3x3 companion Y and alpha, and per-phase history.
    private sealed class Cb
    {
        public int[] K = new int[3], M = new int[3];
        public double[,] Y = new double[3, 3], Alpha = new double[3, 3];
        public double[] Vprev = new double[3], Iprev = new double[3], Ih = new double[3];
    }
    private readonly Cb[] _cbs;

    // Per-phase scalar shunts (companion) and faults (toggled conductance to ground).
    private readonly List<(int Node, Companion Comp, bool History)> _shunts = new();
    private readonly double[] _shV, _shI, _shIh;
    private readonly (int Node, Func<double, double> Emf)[] _dir;
    private readonly List<(int Node, double Gon, double AtOn, double AtOff)> _faults = new();
    private readonly double[] _faultG;             // current fault conductance per fault
    private SparseLuFactorization? _fact;

    // R114 (as in EmtEngine): cache the RCM ordering with the CSR pattern it was computed for (refactors do not
    // change the pattern; compared by content, so bit-identical), and solve into reused per-step buffers.
    private int[]? _orderingRowPtr, _orderingColIdx, _ordering;
    private double[] _vFree = Array.Empty<double>(), _solveWork = Array.Empty<double>();

    // Converters: control state, PLL, and the current phasor injected over the next step (one-step explicit).
    private sealed class Conv
    {
        public int Bus;
        public Models.RmsConverterState State = null!;
        public double Theta, PllInt, Kp, Ki;
        public System.Numerics.Complex Phasor;
        public double Vmag, Ramp, VFilt;
        // Grid-forming: the per-phase indices of its virtual-reactance inductor shunts, that inductance, and the
        // limited terminal-current phasor when current-limited.
        public int[]? LShunts;
        public double Lv;
        public bool Limited;
        public System.Numerics.Complex LimitedCurrent;
        public double[] LastInj = new double[3];
        public double PFilt, QFilt, IFilt;
        public System.Numerics.Complex VPhasor;
        public List<double> T = new(), V = new(), P = new(), Q = new(), Ip = new(), Iq = new();
        public List<bool> Dip = new();
    }
    private readonly Conv[] _convs;

    public int Refactorizations { get; private set; }

    /// <summary>Converter terminal-voltage measurement filter (s): extracts the fundamental magnitude the control
    /// law acts on from the PLL-frame voltage.</summary>
    public const double MeasurementFilterSeconds = 0.005;

    /// <summary>Per-converter trajectories recorded by <see cref="Simulate"/>.</summary>
    public IReadOnlyList<EmtConverterTrace> ConverterTraces =>
        _convs.Select(c => new EmtConverterTrace(c.Bus, c.T, c.V, c.P, c.Q, c.Ip, c.Iq, c.Dip)).ToList();

    /// <summary>
    /// Bug hunt 2026-10-29, finding 17: the network record is checked before anything is stamped, as the single-phase
    /// engine's ValidateNetwork does. A node is <c>3 * bus + phase</c>, so a bus outside 0..BusCount-1 or a fault phase
    /// outside 0..2 used to land silently on ANOTHER bus's node (the fault on the wrong phase or bus), and a fault
    /// resistance of 0 or below gave an infinite or negative conductance that reached the factorisation unnamed. Each
    /// is refused here, naming the element.
    /// </summary>
    private static void ValidateNetwork(ThreePhaseNetwork net)
    {
        ArgumentNullException.ThrowIfNull(net);
        if (net.BusCount < 1)
            throw new ArgumentException($"The three-phase network needs at least one bus (BusCount = {net.BusCount}).", nameof(net));
        void Bus(int bus, string what)
        {
            if (bus < 0 || bus >= net.BusCount)
                throw new ArgumentException($"{what}: bus {bus} is outside 0 to {net.BusCount - 1}.", nameof(net));
        }
        for (int i = 0; i < net.Sources.Count; i++) Bus(net.Sources[i].Bus, $"Source #{i}");
        for (int i = 0; i < net.Branches.Count; i++)
        {
            Bus(net.Branches[i].FromBus, $"Branch #{i} (from end)");
            Bus(net.Branches[i].ToBus, $"Branch #{i} (to end)");
        }
        for (int i = 0; i < net.Shunts.Count; i++) Bus(net.Shunts[i].Bus, $"Shunt #{i}");
        IReadOnlyList<ThreePhaseConverter> convs = net.Converters ?? Array.Empty<ThreePhaseConverter>();
        for (int i = 0; i < convs.Count; i++) Bus(convs[i].Bus, $"Converter #{i}");
        for (int i = 0; i < net.Faults.Count; i++)
        {
            ThreePhaseFault fl = net.Faults[i];
            string who = $"Fault #{i}";
            Bus(fl.Bus, who);
            if (fl.Phases is null || fl.Phases.Length == 0)
                throw new ArgumentException($"{who} names no phase; give one or more of 0 (a), 1 (b), 2 (c).", nameof(net));
            var seen = new HashSet<int>();
            foreach (int p in fl.Phases)
            {
                if (p < 0 || p > 2)
                    throw new ArgumentException($"{who}: phase {p} is not 0 (a), 1 (b) or 2 (c).", nameof(net));
                if (!seen.Add(p))
                    throw new ArgumentException($"{who} names phase {p} twice.", nameof(net));
            }
            if (!(fl.Rf > 0.0) || !double.IsFinite(fl.Rf))
                throw new ArgumentException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{who}: fault resistance {fl.Rf:R} pu must be a finite number above 0 (a bolted fault is a small Rf, not 0)."), nameof(net));
            if (!double.IsFinite(fl.AtSeconds) || double.IsNaN(fl.ClearSeconds) || !(fl.ClearSeconds > fl.AtSeconds))
                throw new ArgumentException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{who}: it must start at a finite time and clear after it starts (AtSeconds {fl.AtSeconds:R}, ClearSeconds {fl.ClearSeconds:R})."), nameof(net));
        }
    }

    public ThreePhaseEmtEngine(ThreePhaseNetwork net, double dt, double frequencyHz = FrequencyLimits.NominalHz)
    {
        // R138 (D2): the engine frequency is also every default source's frequency, so it must be a real frequency.
        if (!(frequencyHz > 0.0) || !double.IsFinite(frequencyHz))
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), $"The EMT engine frequency must be positive and finite (got {frequencyHz} Hz).");
        // GR-571: the time step must be a finite number of seconds above 0 (zero, negative or NaN were used as given).
        if (!(dt > 0.0) || !double.IsFinite(dt))
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "The EMT time step must be a finite number of seconds above 0.");
        ValidateNetwork(net);
        _dt = dt;
        _omega = 2.0 * Math.PI * frequencyHz;
        _n = 3 * net.BusCount;

        // Dirichlet nodes from ideal sources; emf per phase.
        var dir = new List<(int, Func<double, double>)>();
        foreach (ThreePhaseSource s in net.Sources)
            for (int p = 0; p < 3; p++)
            {
                // R138: a source with no frequency of its own runs at the engine's (see ThreePhaseSource).
                double amp = s.AmplitudePu, f = s.FreqHz ?? frequencyHz, ph = p * 2.0 * Math.PI / 3.0;
                int node = 3 * s.Bus + p;
                dir.Add((node, t => amp * DetMath.Cos(2.0 * Math.PI * f * t - ph)));
            }
        _dir = dir.ToArray();

        _freeIndex = new int[_n];
        Array.Fill(_freeIndex, -2);
        foreach ((int node, _) in _dir) if (node >= 0) _freeIndex[node] = -1;
        int fc = 0;
        for (int i = 0; i < _n; i++) if (_freeIndex[i] == -2) _freeIndex[i] = fc++;
        _freeCount = fc;

        // Coupled branches.
        _cbs = net.Branches.Select(b => BuildCb(b)).ToArray();

        // Per-phase shunts.
        foreach (ThreePhaseShunt sh in net.Shunts)
            for (int p = 0; p < 3; p++)
            {
                int node = 3 * sh.Bus + p;
                if (sh.G != 0) _shunts.Add((node, new Companion(sh.G, 0, 0), false));
                if (sh.C != 0) _shunts.Add((node, EmtCompanionStamp.Capacitor(sh.C, dt, IntegrationRule.Trapezoidal), true));
                if (sh.L != 0) _shunts.Add((node, EmtCompanionStamp.Inductor(sh.L, dt, IntegrationRule.Trapezoidal), true));
            }

        // Converter AC filter capacitors (per phase).
        var gfmShunts = new Dictionary<int, (int[] Idx, double L)>();
        int convIndex = -1;
        foreach (ThreePhaseConverter cv in net.Converters ?? Array.Empty<ThreePhaseConverter>())
        {
            convIndex++;
            double cap = cv.FilterSusceptancePu * cv.Unit.RatingPu / _omega;
            if (cap > 0)
                for (int p = 0; p < 3; p++)
                    _shunts.Add((3 * cv.Bus + p, EmtCompanionStamp.Capacitor(cap, dt, IntegrationRule.Trapezoidal), true));
            // A grid-forming unit is an EMF behind its virtual reactance: in the time domain that is exactly a shunt
            // inductor L = Xv/w0 (system base) plus a current source E/(jXv) - the Norton pair the RMS model uses.
            if (cv.Unit.Parameters.GridForming)
            {
                double lv = cv.Unit.Parameters.VirtualReactancePu / cv.Unit.RatingPu / _omega;
                var idx = new int[3];
                for (int p = 0; p < 3; p++)
                {
                    idx[p] = _shunts.Count;
                    _shunts.Add((3 * cv.Bus + p, EmtCompanionStamp.Inductor(lv, dt, IntegrationRule.Trapezoidal), true));
                }
                gfmShunts[convIndex] = (idx, lv);
            }
        }
        _shV = new double[_shunts.Count];
        _shI = new double[_shunts.Count];
        _shIh = new double[_shunts.Count];

        // Faults (start open at a tiny conductance so they sit in the pattern).
        foreach (ThreePhaseFault fl in net.Faults)
            foreach (int p in fl.Phases)
                _faults.Add((3 * fl.Bus + p, 1.0 / fl.Rf, fl.AtSeconds, fl.ClearSeconds));
        _faultG = new double[_faults.Count];
        for (int i = 0; i < _faults.Count; i++) _faultG[i] = EmtConstants.OpenFaultConductance;

        _vDir = new double[_n];
        _vNode = new double[_n];
        _rhs = new double[_freeCount];

        _convs = (net.Converters ?? Array.Empty<ThreePhaseConverter>()).Select((c, ci) =>
        {
            double wn = 2.0 * Math.PI * c.PllBandwidthHz;
            double v0 = Math.Max(c.Unit.V0.Magnitude, EmtConstants.MinVoltageForControlPu);
            var cv = new Conv
            {
                Bus = c.Bus, State = new Models.RmsConverterState(c.Unit), Ramp = c.StartupRampSeconds,
                Theta = DetMath.Phase(c.Unit.V0), Kp = 2.0 * ConverterDefaults.PllDampingRatio * wn / v0, Ki = wn * wn / v0, Vmag = v0, VFilt = v0,
            };
            cv.Phasor = cv.State.Current(v0, DetMath.Phase(c.Unit.V0), lockAngle: true);
            if (gfmShunts.TryGetValue(ci, out var gs)) { cv.LShunts = gs.Idx; cv.Lv = gs.L; }
            return cv;
        }).ToArray();
        Refactor();
    }

    private Cb BuildCb(ThreePhaseBranch b)
    {
        var cb = new Cb();
        for (int p = 0; p < 3; p++) { cb.K[p] = 3 * b.FromBus + p; cb.M[p] = 3 * b.ToBus + p; }
        // A = R + (2/dt) L  (3x3 symmetric: self on diagonal, mutual off-diagonal)
        var A = new double[3, 3];
        var Lm2 = new double[3, 3]; // (2/dt)L - R
        double c = 2.0 / _dt;
        for (int p = 0; p < 3; p++)
            for (int q = 0; q < 3; q++)
            {
                double rr = p == q ? b.Rs : b.Rm, ll = p == q ? b.Ls : b.Lm;
                A[p, q] = rr + c * ll;
                Lm2[p, q] = c * ll - rr;
            }
        cb.Y = Inv3(A);
        cb.Alpha = Mul3(cb.Y, Lm2);
        return cb;
    }

    private bool IsFree(int node) => node >= 0 && _freeIndex[node] >= 0;

    private void Refactor()
    {
        var builder = new CsrMatrix.Builder(_freeCount);
        foreach (Cb cb in _cbs)
            for (int p = 0; p < 3; p++)
                for (int q = 0; q < 3; q++)
                {
                    double g = cb.Y[p, q];
                    if (g == 0) continue;
                    Stamp(builder, cb.K[p], cb.K[q], g);
                    Stamp(builder, cb.M[p], cb.M[q], g);
                    Stamp(builder, cb.K[p], cb.M[q], -g);
                    Stamp(builder, cb.M[p], cb.K[q], -g);
                }
        foreach ((int node, Companion comp, _) in _shunts) if (IsFree(node)) builder.Add(_freeIndex[node], _freeIndex[node], comp.Geq);
        for (int i = 0; i < _faults.Count; i++) if (IsFree(_faults[i].Node)) builder.Add(_freeIndex[_faults[i].Node], _freeIndex[_faults[i].Node], _faultG[i]);
        CsrMatrix gMat = builder.Build();
        if (_ordering is null || !gMat.RowPtr.AsSpan().SequenceEqual(_orderingRowPtr) || !gMat.ColIdx.AsSpan().SequenceEqual(_orderingColIdx))
        {
            _ordering = SparseLu.Ordering(gMat);
            _orderingRowPtr = (int[])gMat.RowPtr.Clone();
            _orderingColIdx = (int[])gMat.ColIdx.Clone();
        }
        _fact = SparseLu.FactorWithOrdering(gMat, _ordering) ?? throw new InvalidOperationException("three-phase EMT matrix is singular.");
        if (_vFree.Length != _freeCount) { _vFree = new double[_freeCount]; _solveWork = new double[_freeCount]; }
        Refactorizations++;
    }

    private void Stamp(CsrMatrix.Builder b, int r, int c, double g)
    {
        if (IsFree(r) && IsFree(c)) b.Add(_freeIndex[r], _freeIndex[c], g);
    }

    private void Step(double t)
    {
        foreach ((int node, Func<double, double> emf) in _dir) if (node >= 0) _vDir[node] = emf(t);
        Array.Clear(_rhs);

        // Coupled-branch history (3-vector) into the RHS, with Dirichlet coupling.
        foreach (Cb cb in _cbs)
        {
            for (int p = 0; p < 3; p++)
            {
                double ih = 0;
                for (int q = 0; q < 3; q++) ih += cb.Y[p, q] * cb.Vprev[q] + cb.Alpha[p, q] * cb.Iprev[q];
                cb.Ih[p] = ih;
            }
            for (int p = 0; p < 3; p++)
            {
                if (IsFree(cb.K[p])) _rhs[_freeIndex[cb.K[p]]] -= cb.Ih[p];
                if (IsFree(cb.M[p])) _rhs[_freeIndex[cb.M[p]]] += cb.Ih[p];
                for (int q = 0; q < 3; q++)
                {
                    double g = cb.Y[p, q];
                    if (IsFree(cb.K[p]) && !IsFree(cb.M[q]) && cb.M[q] >= 0) _rhs[_freeIndex[cb.K[p]]] += g * _vDir[cb.M[q]];
                    if (IsFree(cb.M[p]) && !IsFree(cb.K[q]) && cb.K[q] >= 0) _rhs[_freeIndex[cb.M[p]]] += g * _vDir[cb.K[q]];
                }
            }
        }
        for (int i = 0; i < _shunts.Count; i++)
        {
            (int node, Companion comp, bool hist) = _shunts[i];
            double ih = hist ? comp.History(_shV[i], _shI[i]) : 0.0;
            _shIh[i] = ih;
            if (IsFree(node)) _rhs[_freeIndex[node]] -= ih;
        }

        // Converter injections: each converter's current phasor (from the previous step's controls) as a
        // balanced abc waveform at this instant, i_p = Re(I e^{j(w0 t - p 120deg)}).
        foreach (Conv c in _convs)
        {
            double ramp = c.Ramp > 0 ? Math.Min(1.0, t / c.Ramp) : 1.0;
            for (int p = 0; p < 3; p++)
            {
                int node = 3 * c.Bus + p;
                if (!IsFree(node)) continue;
                double ang = _omega * t - p * 2.0 * Math.PI / 3.0;
                if (c.LShunts is { } ls && c.Limited)
                {
                    // Grid-forming, current-limited: inject the limited current plus the virtual inductor's own
                    // current (its last actual state), so the limited current reaches the network. The inductor's
                    // actual state is used - not a delayed V/X estimate, which through the large virtual admittance is
                    // an unstable high-gain loop; an explicit one-step predictor (i_L + dt/L v) is unstable too. The
                    // one-step lag acts on the fundamental as a conductance dt/2L, which is cancelled from the
                    // filtered voltage phasor (fundamental only, low bandwidth).
                    int si = ls[p];
                    double iLpred = _shI[si];
                    System.Numerics.Complex lim = c.LimitedCurrent + (_dt / c.Lv) * c.VPhasor;
                    double iLim = lim.Real * DetMath.Cos(ang) - lim.Imaginary * DetMath.Sin(ang);
                    c.LastInj[p] = ramp * iLim + iLpred;
                }
                else
                {
                    c.LastInj[p] = ramp * (c.Phasor.Real * DetMath.Cos(ang) - c.Phasor.Imaginary * DetMath.Sin(ang));
                }
                _rhs[_freeIndex[node]] += c.LastInj[p];
            }
        }

        _fact!.SolveInto(_rhs, _vFree, _solveWork);
        double[] vFree = _vFree;
        for (int node = 0; node < _n; node++) _vNode[node] = _freeIndex[node] >= 0 ? vFree[_freeIndex[node]] : _vDir[node];

        foreach (Cb cb in _cbs)
            for (int p = 0; p < 3; p++)
            {
                double vB = NodeV(cb.K[p]) - NodeV(cb.M[p]);
                double i = cb.Ih[p];
                for (int q = 0; q < 3; q++) i += cb.Y[p, q] * (NodeV(cb.K[q]) - NodeV(cb.M[q]));
                cb.Vprev[p] = vB; cb.Iprev[p] = i;
            }
        for (int i = 0; i < _shunts.Count; i++)
        {
            (int node, Companion comp, _) = _shunts[i];
            double vB = NodeV(node);
            _shV[i] = vB; _shI[i] = comp.Geq * vB + _shIh[i];
        }

        foreach (Conv c in _convs) UpdateConverter(c, t);
    }

    /// <summary>One converter's controls after the network solve at <paramref name="t"/>: the SRF-PLL reads the
    /// terminal abc voltage (Park transform on its own angle; a PI drives vq to zero), the measured magnitude
    /// and phase feed the shared RMS control law, and the resulting current phasor is injected next step.</summary>
    private void UpdateConverter(Conv c, double t)
    {
        double va = NodeV(3 * c.Bus), vb = NodeV(3 * c.Bus + 1), vc = NodeV(3 * c.Bus + 2);
        const double third = 2.0 * Math.PI / 3.0;
        double th = c.Theta;
        double vd = 2.0 / 3.0 * (va * DetMath.Cos(th) + vb * DetMath.Cos(th - third) + vc * DetMath.Cos(th + third));
        double vq = -2.0 / 3.0 * (va * DetMath.Sin(th) + vb * DetMath.Sin(th - third) + vc * DetMath.Sin(th + third));

        // PLL: dtheta/dt = w0 + Kp vq + Ki int(vq). (vq > 0 means the voltage leads the PLL frame.)
        c.PllInt += c.Ki * vq * _dt;
        c.Theta += (_omega + c.Kp * vq + c.PllInt) * _dt;

        // The converter synchronises through the PLL: its current is phased on the PLL angle (bandwidth-limited),
        // never on the instantaneous measured angle - locking to the raw angle feeds the filter/line resonance
        // straight back into the injection. The measured magnitude still drives the dip logic and LVACM.
        // The control law acts on the FUNDAMENTAL magnitude. The raw Park magnitude carries every network
        // oscillation (e.g. the filter/line LC resonance); feeding it to LVACM modulates the injection at that
        // frequency and sustains it. A short measurement filter extracts the fundamental.
        double vmagRaw = Math.Sqrt(vd * vd + vq * vq);
        c.VFilt += (vmagRaw - c.VFilt) * (1.0 - DetMath.Exp(-_dt / MeasurementFilterSeconds));
        double vmag = c.VFilt;
        double phase = c.Theta - _omega * (t + _dt);               // PLL angle over the next step, vs the w0 reference
        c.Vmag = vmag;
        System.Numerics.Complex v = DetMath.FromPolar(vmag, phase);
        if (c.LShunts is { } ls)
        {
            UpdateGridForming(c, t, th, vd, vq, vmag, v, ls);
            return;
        }
        c.State.Step(t, vmag, _dt);
        c.Phasor = c.State.Current(vmag, phase, lockAngle: true);

        System.Numerics.Complex sC = v * System.Numerics.Complex.Conjugate(c.State.TerminalCurrent(c.Phasor, v));
        c.T.Add(t); c.V.Add(vmag); c.P.Add(sC.Real); c.Q.Add(sC.Imaginary);
        c.Ip.Add(c.State.ActiveCurrent); c.Iq.Add(c.State.ReactiveCurrent); c.Dip.Add(c.State.InDip);
    }

    private double NodeV(int node) => node < 0 ? 0.0 : _vNode[node];

    /// <summary>Run to <paramref name="duration"/> at the fixed step, recording the three phase voltages of
    /// <paramref name="probeBus"/> each step. Returns [step][phase] volts and the phase-a branch is untouched.</summary>
    public (double[] T, double[][] Vabc) Simulate(double duration, int probeBus)
    {
        // R077: one engine, one run. The state (histories, converter controls, recorded traces) carries over,
        // but time would restart at zero, so a second call cannot be a meaningful continuation.
        if (_simulated)
            throw new InvalidOperationException(
                "ThreePhaseEmtEngine.Simulate has already run on this engine; a second call would continue the old state " +
                "while restarting time at zero. Build a new engine for a new run.");
        _simulated = true;
        // GR-571: a finite, non-negative duration (NaN rounded to 0 steps) and a probe bus of the network.
        if (!(duration >= 0.0) || !double.IsFinite(duration))
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "The EMT duration must be a finite number of seconds, 0 or above.");
        if (probeBus < 0 || 3 * probeBus + 2 >= _n)
            throw new ArgumentOutOfRangeException(nameof(probeBus), probeBus, $"The probe bus index must be 0 to {_n / 3 - 1}.");
        int steps = (int)Math.Round(duration / _dt);
        var ts = new double[steps + 1];
        var vabc = new double[steps + 1][];
        vabc[0] = new[] { NodeV(3 * probeBus), NodeV(3 * probeBus + 1), NodeV(3 * probeBus + 2) };

        using (GcScope.LowLatency())
            for (int step = 1; step <= steps; step++)
            {
                double t = step * _dt;
                // Apply/clear faults at their instants (numeric refactor: conductance toggles, pattern fixed).
                bool changed = false;
                for (int i = 0; i < _faults.Count; i++)
                {
                    double tol = _dt * EmtConstants.EventTimeToleranceFraction;
                    double target = t >= _faults[i].AtOn - tol && t < _faults[i].AtOff - tol
                        ? _faults[i].Gon : EmtConstants.OpenFaultConductance;
                    if (target != _faultG[i]) { _faultG[i] = target; changed = true; }
                }
                if (changed) Refactor();
                Step(t);
                // Bug hunt 2026-10-29, finding 16: fail loud at the first non-finite node voltage, as the single-phase
                // engine does at its first non-finite probe (EmtEngine.RequireFiniteProbe). Without this a numerically
                // unstable converter loop or a near-singular fault stamp returned a NaN-filled Vabc and converter traces
                // as a normal result. Every node is checked (not only the probe), so a divergence elsewhere in the
                // network is not hidden behind a finite probe bus. Reading only, so a finite run is unchanged.
                for (int node = 0; node < _n; node++)
                    if (!double.IsFinite(_vNode[node]))
                        throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                            $"Three-phase EMT diverged at t = {t:R} s: bus {node / 3} phase {"abc"[node % 3]} voltage is {_vNode[node]:R}; no result."));
                ts[step] = t;
                vabc[step] = new[] { NodeV(3 * probeBus), NodeV(3 * probeBus + 1), NodeV(3 * probeBus + 2) };
            }
        return (ts, vabc);
    }

    /// <summary>
    /// A grid-forming converter after the solve at <paramref name="t"/>. Its actual terminal current is known
    /// exactly: the injection this step minus its virtual inductor's current. Power and current magnitude are
    /// measured from it in the PLL frame (filtered like a real controller's power measurement), which drives the
    /// shared swing and voltage loop. The current limit enters on the measured magnitude and exits, with
    /// hysteresis, when the unlimited current would fall back inside it. The next step injects the Norton
    /// current E y (a voltage source behind the virtual reactance), or the limited current plus the inductor's own.
    /// </summary>
    private void UpdateGridForming(Conv c, double t, double th, double vd, double vq, double vmag,
        System.Numerics.Complex v, int[] ls)
    {
        const double third = 2.0 * Math.PI / 3.0;
        double ia = c.LastInj[0] - _shI[ls[0]], ib = c.LastInj[1] - _shI[ls[1]], ic = c.LastInj[2] - _shI[ls[2]];
        double id = 2.0 / 3.0 * (ia * DetMath.Cos(th) + ib * DetMath.Cos(th - third) + ic * DetMath.Cos(th + third));
        double iq = -2.0 / 3.0 * (ia * DetMath.Sin(th) + ib * DetMath.Sin(th - third) + ic * DetMath.Sin(th + third));
        double pInst = vd * id + vq * iq;                  // per-unit peak-phasor convention (S = V conj(I))
        double qInst = vq * id - vd * iq;
        double a = 1.0 - DetMath.Exp(-_dt / MeasurementFilterSeconds);
        c.PFilt += (pInst - c.PFilt) * a;
        c.QFilt += (qInst - c.QFilt) * a;
        c.IFilt += (Math.Sqrt(id * id + iq * iq) - c.IFilt) * a;

        Models.RmsConverterUnit u = c.State.Unit;
        double imax = u.Parameters.Imax * u.RatingPu;
        bool armed = t >= EmtConstants.LimitArmingRampMultiple * c.Ramp + EmtConstants.LimitArmingMarginSeconds;              // no limiting during the start-up transient
        System.Numerics.Complex y = c.State.NortonAdmittance;
        c.VPhasor = v;
        System.Numerics.Complex iu = (c.State.Emf - v) * y;   // the current the EMF would drive, unlimited
        if (!c.Limited && armed && c.IFilt > imax) c.Limited = true;
        else if (c.Limited && iu.Magnitude < EmtConstants.GfmLimitReleaseFraction * imax) c.Limited = false;
        if (c.Limited) c.LimitedCurrent = iu.Magnitude > EmtConstants.ZeroCurrentMagnitudePu ? iu * (imax / iu.Magnitude) : System.Numerics.Complex.Zero;

        c.State.AdvanceGridForming(c.PFilt, vmag, c.Limited, _dt);
        c.Phasor = c.State.Emf * y;                          // Norton source for the unlimited mode

        double vDen = Math.Max(vmag, EmtConstants.MinVoltageForControlPu);
        c.State.RecordMeasured(c.PFilt / vDen, c.QFilt / vDen, c.Limited);
        c.T.Add(t); c.V.Add(vmag); c.P.Add(c.PFilt); c.Q.Add(c.QFilt);
        c.Ip.Add(c.PFilt / vDen); c.Iq.Add(c.QFilt / vDen); c.Dip.Add(c.Limited);
    }

    // ---- 3x3 linear algebra ----
    private static double[,] Inv3(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2], d = m[1, 0], e = m[1, 1], f = m[1, 2], g = m[2, 0], h = m[2, 1], i = m[2, 2];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-300) throw new InvalidOperationException("singular 3x3 branch matrix.");
        double inv = 1.0 / det;
        return new double[3, 3]
        {
            { (e * i - f * h) * inv, (c * h - b * i) * inv, (b * f - c * e) * inv },
            { (f * g - d * i) * inv, (a * i - c * g) * inv, (c * d - a * f) * inv },
            { (d * h - e * g) * inv, (b * g - a * h) * inv, (a * e - b * d) * inv },
        };
    }

    private static double[,] Mul3(double[,] x, double[,] y)
    {
        var r = new double[3, 3];
        for (int p = 0; p < 3; p++)
            for (int q = 0; q < 3; q++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += x[p, k] * y[k, q];
                r[p, q] = s;
            }
        return r;
    }
}
