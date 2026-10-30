using System.Diagnostics;
using GridSim.Core.Numerics;
using GridSim.Core.Simulation;

namespace GridSim.Core.Dynamics.Emt;

/// <summary>
/// The EMT time-domain engine: a Dommel nodal companion-model solver. Each fixed timestep it assembles the
/// real nodal conductance matrix G (constant between switching events) and solves <c>G·v = i_history</c> for
/// the instantaneous node voltages, then advances each reactive element's history. The matrix is factored
/// once and back-substituted every step - the amortization that makes microsecond stepping affordable.
///
/// <para><b>Governing constraint.</b> An EMT run is <i>Solving</i> under <c>UnderstandingRestrictions.md</c>:
/// a forward simulation about the MODEL, not forward inference about reality. Its output is a model
/// trajectory, never a claim about the real grid's future. It sits in the <c>Dynamics/</c> seam beside the
/// swing/harmonic tools and does not engage the estimator's restriction-review gate. Any assumed element
/// parameter (e.g. a future converter model) must be tagged <c>CuratedPlaceholder [VERIFY]</c>; the passive
/// R/L/C/source/switch elements here are grounded in the model's per-unit data.</para>
///
/// <para><b>One engine, one network, one timestep (R077).</b> The network and dt are fixed at construction;
/// <see cref="Simulate"/> refuses an <see cref="EmtCase"/> naming a different network or dt, and refuses a second
/// run on an engine whose state has already advanced unless <see cref="Reset"/> is called first.</para>
///
/// <para><b>Branch models (R078).</b> A series arm may carry an off-nominal tap (an ideal From-side transformer,
/// <see cref="SeriesBranch.Tap"/>) and a series-compensated branch is a series R-C arm
/// (<see cref="SeriesCapacitorBranch"/>); with the tap they reproduce the power flow's branch stamp exactly at
/// the system frequency. Untapped branches take the original stamping code path, unchanged.</para>
/// </summary>
public sealed class EmtEngine
{
    private enum Kind { Resistor, SeriesRl, Capacitor, Inductor, SeriesRc }

    private struct Br
    {
        public int K, M;          // node ids (EmtNetwork.Ground for the reference)
        public Kind Kind;
        public double R, L, C, Gfixed;
        public bool History;
        public int ElementId;

        // R078: an off-nominal transformer tap on the K (From) side of a series arm. When Tapped is false the
        // branch is stamped by the original code path, untouched, so every untapped network stays bit-identical.
        // When Tapped is true, S = 1/Tap: the arm sees S*v_K at its K end, and node K carries S*i.
        public bool Tapped;
        public double S;

        // N031: true for a switch that is OPEN when the engine is built (or reset). Its open conductance 1/Roff
        // (1e-9 S by default) is a numerical stand-in for "not connected", not a real path, so it must not anchor
        // an otherwise floating island in the rest initial-condition solve (see PinFloatingNodes).
        public bool OpenAtRest;
    }

    // A lossless travelling-wave (Bergeron) line: a surge conductance Yc to ground at each end plus a history
    // current source carrying the wave that left the other end N=round(tau/dt) steps ago. The ends couple only
    // through the delay buffers, so the matrix stamp is diagonal-only.
    private sealed class Line
    {
        public int K, M, ElementId, N, Pos;
        public double Yc;
        public double[] BufK = Array.Empty<double>(), BufM = Array.Empty<double>();
        public double LastIkm;
    }

    private readonly int _n;
    private readonly Br[] _branches;
    private readonly Line[] _lines;
    private readonly (int Node, Func<double, double> Inj)[] _injectors;
    private readonly (int Node, Func<double, double> Emf)[] _dirichlet;

    private readonly int[] _freeIndex;   // node -> reduced index, or -1 if Dirichlet
    private readonly int _freeCount;
    private readonly double[] _vDir;      // per-node Dirichlet voltage this step (0 elsewhere)

    private readonly Companion[] _comp;   // per branch, for the current dt/rule
    private readonly double[] _prevV, _prevI, _curIh;
    private readonly double[] _rhs, _vNode;
    private readonly Dictionary<int, int> _branchOfElement = new();
    private readonly Dictionary<int, int> _lineOfElement = new();
    private readonly Dictionary<int, (int Branch, double Ron, double Roff)> _switchMap = new();

    // D-DOC-025: every element by its (unique, D-DOC-024) id, so a probe can be checked against what it names.
    private readonly Dictionary<int, EmtElement> _elementById = new();

    private readonly IntegrationRule _baseRule;
    private readonly bool _useCda;
    private IntegrationRule _rule;
    private double _dt;
    private SparseLuFactorization? _fact;

    // R114: the reverse Cuthill-McKee ordering is a pure function of the sparsity PATTERN of G, which a refactor
    // (a CDA burst, a switch event, a dt change) does not alter: the same branches are stamped every time. Cache the
    // ordering with the pattern it was computed for and reuse it while the pattern is identical (compared by
    // content), exactly as CpuSparseLuSolver does for the Newton loop: bit-identical, without re-deriving RCM.
    private int[]? _orderingRowPtr, _orderingColIdx, _ordering;

    // R114: per-step solve buffers (the solve used to allocate two fresh n-vectors every step).
    private double[] _vFree = Array.Empty<double>(), _solveWork = Array.Empty<double>();

    // R077: the network and timestep this engine was built for. Every companion conductance and every Bergeron
    // delay count N = round(tau/dt) is fixed at construction from these, so Simulate refuses an EmtCase that
    // names a different network or a different dt rather than silently mixing the two.
    private readonly EmtNetwork _network;
    private readonly double _constructedDt;

    // R077: the switch conductances as built, so Reset() can put every switch back to its initial state.
    private readonly double[] _initialGfixed;

    // R077: true once the state has been advanced (by Simulate or a direct Step). A second Simulate on an
    // advanced engine used to continue from the old state while restarting time at zero, so state and time
    // disagreed; it is now refused until Reset() returns the engine to its as-built rest state.
    private bool _advanced;

    /// <summary>Number of matrix (re)factorizations performed (since construction or the last
    /// <see cref="Reset"/>): one when the engine is built (or reset), then for every step at which a switching event
    /// really changes a conductance, two with the Critical Damping Adjustment (one into the backward-Euler half-steps,
    /// one back to the base rule) or one without it (CDA off, or a backward-Euler run).</summary>
    public int Refactorizations { get; private set; }

    /// <summary>
    /// D-DOC-024: every element that is not passive, in plain words (a series arm with a negative resistance, or a shunt
    /// with a negative conductance). Such an element generates energy in proportion to the voltage across it, so a run
    /// that contains one may grow without bound. Network-reduction equivalents carry them on purpose (IEEE 145 has 224
    /// negative-resistance branches and 8 negative-conductance shunts, pglib case588 has 5 negative-resistance
    /// branches), so by default they are accepted and listed here; with <see cref="EmtOptions.RefuseNonPassive"/> the
    /// engine refuses them by name instead. Empty for a passive network.
    /// </summary>
    public IReadOnlyList<string> NonPassiveElements { get; }

    /// <summary>The network this engine was built from (R077). An <see cref="EmtCase"/> passed to
    /// <see cref="Simulate"/> must name this network (or one element-for-element equivalent to it).</summary>
    public EmtNetwork Network => _network;

    /// <summary>The fixed timestep (s) this engine was built for (R077). An <see cref="EmtCase"/> passed to
    /// <see cref="Simulate"/> must carry exactly this <see cref="EmtCase.DtSeconds"/>.</summary>
    public double DtSeconds => _constructedDt;

    public EmtEngine(EmtNetwork network, double dt, EmtOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        if (!(dt > 0) || !double.IsFinite(dt)) throw new ArgumentOutOfRangeException(nameof(dt), "EMT timestep must be positive and finite.");
        options ??= new EmtOptions();
        _baseRule = options.Rule;
        _useCda = options.UseCda;
        _rule = options.Rule;
        _dt = dt;
        _network = network;
        _constructedDt = dt;
        _n = network.NodeCount;

        // D-DOC-024 (docs-session finding): check the whole network before a single element is stamped, and refuse
        // by name what the engine cannot run honestly. These checks only read the elements; they never change a
        // number, so every network that passes them is stamped exactly as before.
        NonPassiveElements = ValidateNetwork(network, options.RefuseNonPassive);
        foreach (EmtElement e in network.Elements) _elementById[e.Id] = e;   // ids are unique (checked above)

        var branches = new List<Br>();
        var lines = new List<Line>();
        var injectors = new List<(int, Func<double, double>)>();
        var dirichlet = new List<(int, Func<double, double>)>();
        var switches = new List<(int Id, int Branch, double Ron, double Roff)>();

        foreach (EmtElement e in network.Elements)
        {
            switch (e)
            {
                case SeriesBranch b:
                    // R078: refuse what the series R-L companion cannot represent instead of letting
                    // EmtCompanionStamp.SeriesRl quietly turn a negative inductance into a plain resistor, or an
                    // R = L = 0 arm into an infinite conductance.
                    RequireFiniteSeries(b.Id, "R", b.R);
                    RequireFiniteSeries(b.Id, "L", b.L);
                    if (b.L < 0)
                        throw new ArgumentException(
                            $"EMT series branch {b.Id} ({b.From}->{b.To}) has negative inductance L = {b.L:R}. A capacitive " +
                            "series arm (series compensation, X < 0) must be a SeriesCapacitorBranch.", nameof(network));
                    if (b.R == 0 && b.L == 0)
                        throw new ArgumentException(
                            $"EMT series branch {b.Id} ({b.From}->{b.To}) has R = L = 0, an infinite conductance that the " +
                            "nodal solve cannot carry. Merge the two nodes or give the branch an impedance.", nameof(network));
                    double tapS = TapScale(b.Id, b.From, b.To, b.Tap);
                    branches.Add(new Br { K = b.From, M = b.To, Kind = Kind.SeriesRl, R = b.R, L = b.L, History = b.L > 0, ElementId = b.Id,
                                          Tapped = b.Tap != 1.0, S = tapS });
                    break;
                case SeriesCapacitorBranch sc:
                    // R078: series R-C (series compensation). Always carries history (the capacitor's charge).
                    RequireFiniteSeries(sc.Id, "R", sc.R);
                    RequireFiniteSeries(sc.Id, "C", sc.C);
                    if (!(sc.C > 0))
                        throw new ArgumentException(
                            $"EMT series capacitor branch {sc.Id} ({sc.From}->{sc.To}) needs a positive capacitance; got C = {sc.C:R}.",
                            nameof(network));
                    double capS = TapScale(sc.Id, sc.From, sc.To, sc.Tap);
                    branches.Add(new Br { K = sc.From, M = sc.To, Kind = Kind.SeriesRc, R = sc.R, C = sc.C, History = true, ElementId = sc.Id,
                                          Tapped = sc.Tap != 1.0, S = capS });
                    break;
                case BergeronLine ln:
                    int nDelay = Math.Max(1, (int)Math.Round(ln.TauSeconds / dt));
                    lines.Add(new Line { K = ln.From, M = ln.To, ElementId = ln.Id, N = nDelay, Yc = 1.0 / ln.SurgeImpedance, BufK = new double[nDelay], BufM = new double[nDelay] });
                    break;
                case SwitchElement sw:
                    switches.Add((sw.Id, branches.Count, sw.Ron, sw.Roff));
                    branches.Add(new Br { K = sw.From, M = sw.To, Kind = Kind.Resistor, Gfixed = sw.InitiallyClosed ? 1.0 / sw.Ron : 1.0 / sw.Roff, ElementId = sw.Id,
                                          OpenAtRest = !sw.InitiallyClosed });
                    break;
                case ShuntElement s:
                    if (s.G != 0) branches.Add(new Br { K = s.Bus, M = EmtNetwork.Ground, Kind = Kind.Resistor, Gfixed = s.G, ElementId = s.Id });
                    if (s.C != 0) branches.Add(new Br { K = s.Bus, M = EmtNetwork.Ground, Kind = Kind.Capacitor, C = s.C, History = true, ElementId = s.Id });
                    if (s.L != 0) branches.Add(new Br { K = s.Bus, M = EmtNetwork.Ground, Kind = Kind.Inductor, L = s.L, History = true, ElementId = s.Id });
                    break;
                case VoltageSource v when v.Rs > 0: // Thévenin = Norton: conductance 1/Rs + current source Emf/Rs
                    branches.Add(new Br { K = v.Bus, M = EmtNetwork.Ground, Kind = Kind.Resistor, Gfixed = 1.0 / v.Rs, ElementId = v.Id });
                    injectors.Add((v.Bus, t => v.Emf(t) / v.Rs));
                    break;
                case VoltageSource v: // ideal source: Dirichlet node
                    dirichlet.Add((v.Bus, v.Emf));
                    break;
                case CurrentSource c:
                    injectors.Add((c.Bus, c.Inj));
                    break;
            }
        }

        _branches = branches.ToArray();
        _lines = lines.ToArray();
        _injectors = injectors.ToArray();
        _dirichlet = dirichlet.ToArray();
        for (int i = 0; i < _branches.Length; i++) _branchOfElement.TryAdd(_branches[i].ElementId, i);
        for (int i = 0; i < _lines.Length; i++) _lineOfElement.TryAdd(_lines[i].ElementId, i);
        foreach (var s in switches) _switchMap[s.Id] = (s.Branch, s.Ron, s.Roff);

        // Free-node mapping: Dirichlet nodes leave the unknown set.
        _freeIndex = new int[_n];
        Array.Fill(_freeIndex, -2); // -2 = free-but-unnumbered yet
        foreach ((int node, _) in _dirichlet) if (node >= 0) _freeIndex[node] = -1;
        int fc = 0;
        for (int i = 0; i < _n; i++) if (_freeIndex[i] == -2) _freeIndex[i] = fc++;
        _freeCount = fc;

        _vDir = new double[_n];
        _comp = new Companion[_branches.Length];
        _prevV = new double[_branches.Length];
        _prevI = new double[_branches.Length];
        _curIh = new double[_branches.Length];
        _rhs = new double[_freeCount];
        _vNode = new double[_n];

        _initialGfixed = new double[_branches.Length];
        for (int i = 0; i < _branches.Length; i++) _initialGfixed[i] = _branches[i].Gfixed;

        Refactor();
        InitializeRest();
    }

    /// <summary>
    /// D-DOC-024 (docs-session finding, technical ch. 21): check a network before it is stamped and refuse, by
    /// element id, what the engine would otherwise run silently wrong. Every check throws
    /// <see cref="ArgumentException"/> listing ALL the offending elements of its kind, so one fix-and-retry covers them.
    /// <list type="bullet">
    /// <item>Duplicate element ids. Probes, switch events and the per-element history are all looked up by id: with a
    /// repeated id a probe read the FIRST element and a switch event toggled the LAST, with no warning.</item>
    /// <item>A node out of range: every node id must be a node of the network (0..NodeCount-1) or, for a two-terminal
    /// element, <see cref="EmtNetwork.Ground"/>. A one-terminal element (shunt, source) on ground stamps nothing at
    /// all and was dropped without a word; a node past the end crashed with an index error.</item>
    /// <item>Two ideal voltage sources on one bus: the bus is a Dirichlet node with ONE imposed voltage, so the last
    /// source silently won.</item>
    /// <item>A voltage source's resistance Rs negative or not finite. Rs = 0 is the documented ideal source; a
    /// negative or NaN Rs used to fall through to the ideal branch as if it were 0.</item>
    /// <item>A switch's Ron or Roff not a positive finite number (a zero gives an infinite conductance, a negative one
    /// a non-passive element).</item>
    /// <item>A shunt with a negative or non-finite capacitance or inductance, or a non-finite conductance. (A
    /// grid build never makes a negative C or L: N029 models negative charging as a reactor.)</item>
    /// <item>A travelling-wave line whose surge impedance or travel time is not a positive finite number.</item>
    /// </list>
    /// A NEGATIVE series resistance or shunt conductance is a real feature of network-reduction equivalents, not a
    /// data error, so it is not refused by default; it is returned as a list (exposed as
    /// <see cref="NonPassiveElements"/>), and refused only when <paramref name="refuseNonPassive"/> is set.
    /// </summary>
    private static IReadOnlyList<string> ValidateNetwork(EmtNetwork network, bool refuseNonPassive)
    {
        int n = network.NodeCount;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Num(double x) => x.ToString("R", inv);

        // 1. Unique ids, reported in id order so the message does not depend on dictionary order.
        var kindsById = new Dictionary<int, List<string>>();
        foreach (EmtElement e in network.Elements)
        {
            if (!kindsById.TryGetValue(e.Id, out List<string>? kinds)) kindsById[e.Id] = kinds = new List<string>();
            kinds.Add(e.GetType().Name);
        }
        var duplicates = kindsById.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key)
            .Select(kv => $"id {kv.Key} is used by {kv.Value.Count} elements ({string.Join(", ", kv.Value)})").ToList();
        if (duplicates.Count > 0)
            throw new ArgumentException(
                "EMT network has duplicate element ids: " + string.Join("; ", duplicates) + ". Probes, switch events and " +
                "histories are found by id, so every element needs its own.", nameof(network));

        // 2-7. Per element: nodes in range, then the parameter checks of its kind.
        var bad = new List<string>();
        var nonPassive = new List<string>();
        var idealSourceAt = new Dictionary<int, int>();   // bus -> id of the first ideal source there
        bool TwoTerminalNode(int node) => node == EmtNetwork.Ground || (node >= 0 && node < n);
        bool OneTerminalNode(int node) => node >= 0 && node < n;
        void Ends(EmtElement e, int from, int to)
        {
            if (!TwoTerminalNode(from) || !TwoTerminalNode(to))
                bad.Add($"{e.GetType().Name} {e.Id} joins node {from} to node {to}, but the network's nodes are 0..{n - 1} (or {EmtNetwork.Ground} for ground)");
        }
        void At(EmtElement e, int bus)
        {
            if (!OneTerminalNode(bus))
                bad.Add($"{e.GetType().Name} {e.Id} is at node {bus}, but the network's nodes are 0..{n - 1} (a shunt or source on ground does nothing)");
        }

        foreach (EmtElement e in network.Elements)
        {
            switch (e)
            {
                case SeriesBranch b:
                    Ends(e, b.From, b.To);
                    if (b.R < 0) nonPassive.Add($"series branch {b.Id} ({b.From}->{b.To}) has negative resistance R = {Num(b.R)}");
                    break;
                case SeriesCapacitorBranch sc:
                    Ends(e, sc.From, sc.To);
                    if (sc.R < 0) nonPassive.Add($"series capacitor branch {sc.Id} ({sc.From}->{sc.To}) has negative resistance R = {Num(sc.R)}");
                    break;
                case BergeronLine ln:
                    Ends(e, ln.From, ln.To);
                    if (!(ln.SurgeImpedance > 0) || !double.IsFinite(ln.SurgeImpedance))
                        bad.Add($"travelling-wave line {ln.Id} ({ln.From}->{ln.To}) has surge impedance {Num(ln.SurgeImpedance)}; it must be a positive finite number");
                    if (!(ln.TauSeconds > 0) || !double.IsFinite(ln.TauSeconds))
                        bad.Add($"travelling-wave line {ln.Id} ({ln.From}->{ln.To}) has travel time {Num(ln.TauSeconds)} s; it must be a positive finite number");
                    break;
                case SwitchElement sw:
                    Ends(e, sw.From, sw.To);
                    if (!(sw.Ron > 0) || !double.IsFinite(sw.Ron))
                        bad.Add($"switch {sw.Id} ({sw.From}->{sw.To}) has closed resistance Ron = {Num(sw.Ron)}; it must be a positive finite number");
                    if (!(sw.Roff > 0) || !double.IsFinite(sw.Roff))
                        bad.Add($"switch {sw.Id} ({sw.From}->{sw.To}) has open resistance Roff = {Num(sw.Roff)}; it must be a positive finite number");
                    break;
                case ShuntElement s:
                    At(e, s.Bus);
                    if (!double.IsFinite(s.G)) bad.Add($"shunt {s.Id} at node {s.Bus} has a non-finite conductance G = {Num(s.G)}");
                    else if (s.G < 0) nonPassive.Add($"shunt {s.Id} at node {s.Bus} has negative conductance G = {Num(s.G)}");
                    if (!(s.C >= 0) || !double.IsFinite(s.C)) bad.Add($"shunt {s.Id} at node {s.Bus} has capacitance C = {Num(s.C)}; it must be zero or a positive finite number");
                    if (!(s.L >= 0) || !double.IsFinite(s.L)) bad.Add($"shunt {s.Id} at node {s.Bus} has inductance L = {Num(s.L)}; it must be zero or a positive finite number");
                    break;
                case VoltageSource v:
                    At(e, v.Bus);
                    if (!(v.Rs >= 0) || !double.IsFinite(v.Rs))
                        bad.Add($"voltage source {v.Id} at node {v.Bus} has source resistance Rs = {Num(v.Rs)}; it must be 0 (an ideal source) or a positive finite number");
                    else if (v.Rs == 0)
                    {
                        if (idealSourceAt.TryGetValue(v.Bus, out int first))
                            bad.Add($"ideal voltage sources {first} and {v.Id} are both at node {v.Bus}; an ideal source fixes its node's voltage, so only one can (give one a source resistance Rs > 0)");
                        else idealSourceAt[v.Bus] = v.Id;
                    }
                    break;
                case CurrentSource c:
                    At(e, c.Bus);
                    break;
            }
        }
        if (bad.Count > 0)
            throw new ArgumentException("EMT network cannot be run: " + string.Join("; ", bad) + ".", nameof(network));
        if (refuseNonPassive && nonPassive.Count > 0)
            throw new EmtNonPassiveNetworkException(
                $"EMT network has {nonPassive.Count} non-passive element(s), which generate energy and can make the run grow " +
                "without bound: " + string.Join("; ", nonPassive.Take(MaxListedNonPassive)) +
                (nonPassive.Count > MaxListedNonPassive ? $"; and {nonPassive.Count - MaxListedNonPassive} more" : "") +
                ". They come from network-reduction equivalents; run them only if that is intended.", nonPassive, nameof(network));
        return nonPassive;
    }

    /// <summary>D-DOC-024: how many non-passive elements a refusal names before it says "and N more".</summary>
    private const int MaxListedNonPassive = 10;

    /// <summary>R078: a series element parameter must be a finite number (NaN or infinity cannot be stamped).</summary>
    private static void RequireFiniteSeries(int id, string what, double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException($"EMT series element {id} has a non-finite {what} ({value:R}).");
    }

    /// <summary>
    /// R078: the From-side scale <c>S = 1/Tap</c> of a series arm's ideal transformer. The tap must be a positive
    /// finite ratio; anything else (zero, negative, NaN, infinity) cannot be an ideal transformer and is refused.
    /// (<see cref="EmtNetwork.FromGridModel"/> maps the data's "tap &lt;= 0 means a plain line" convention to 1
    /// before it gets here, exactly as <see cref="PowerFlow.AdmittanceStamp"/> does.)
    /// </summary>
    private static double TapScale(int id, int from, int to, double tap)
    {
        if (!(tap > 0) || !double.IsFinite(tap))
            throw new ArgumentException(
                $"EMT series element {id} ({from}->{to}) has tap ratio {tap:R}; an ideal transformer needs a positive finite ratio.");
        return 1.0 / tap;
    }

    /// <summary>
    /// R077: return the engine to the exact state it was in straight after construction - every switch at its
    /// initial position, every travelling-wave delay line empty, the base timestep and integration rule, the
    /// matrix re-factored and the rest initial condition re-established - so a following <see cref="Simulate"/>
    /// reproduces a fresh engine's trajectory bit for bit (pinned by a test). Without a reset a second
    /// <see cref="Simulate"/> is refused, because it would continue from the end state while restarting time at
    /// zero.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _branches.Length; i++) _branches[i].Gfixed = _initialGfixed[i];
        foreach (Line ln in _lines)
        {
            Array.Clear(ln.BufK);
            Array.Clear(ln.BufM);
            ln.Pos = 0;
            ln.LastIkm = 0.0;
        }
        _dt = _constructedDt;
        _rule = _baseRule;
        Array.Clear(_prevV);
        Array.Clear(_prevI);
        Array.Clear(_curIh);
        Array.Clear(_rhs);
        Array.Clear(_vNode);
        Array.Clear(_vDir);
        Refactorizations = 0;
        Refactor();
        InitializeRest();
        _advanced = false;
    }

    /// <summary>Establish the rest initial condition (every reactive element at zero current and zero voltage:
    /// <c>i_L = 0</c>, <c>v_C = 0</c>) so a source that is already on at t=0 gives the correct step response.
    /// Without this, an all-zero history makes the source appear to ramp 0-&gt;V over the first step (half the
    /// initial di/dt - the standard trapezoidal step-from-rest artifact). The initial node voltages come from
    /// an IC solve: capacitors hold their nodes at 0, inductors are open, sources sit at emf(0), resistors
    /// carry the resulting DC. Reactive branch histories are then seeded from those voltages with zero current.</summary>
    private void InitializeRest()
    {
        var fixedNode = new bool[_n];
        var fixedVal = new double[_n];
        foreach ((int node, Func<double, double> emf) in _dirichlet) if (node >= 0) { fixedNode[node] = true; fixedVal[node] = emf(0.0); }
        // N032: remember which nodes are held at 0 V by a shunt capacitor (not by a source), so their capacitors can
        // be given the current that flows into the node at t = 0+ (see SeedCapacitorCurrentsAtRest).
        var capHeld = new bool[_n];
        for (int i = 0; i < _branches.Length; i++)
        {
            Br b = _branches[i];
            if (b.Kind == Kind.Capacitor && b.M == EmtNetwork.Ground && b.K >= 0 && !fixedNode[b.K]) { fixedNode[b.K] = true; fixedVal[b.K] = 0.0; capHeld[b.K] = true; }
        }

        // R026: a node whose only resistive connections lead nowhere (no path through IC-conducting branches
        // to ground or to a fixed node) has no defined rest voltage: every branch at it is an inductor (open
        // at rest) or it sits on a resistive island that floats. Such nodes used to leave empty or singular
        // rows, the IC factorisation returned null and the WHOLE initial condition was silently skipped (every
        // history left at 0, reintroducing the step-from-rest artefact on every node, e.g. on every gb-full run
        // through its transformer-only buses). They are now pinned at the rest value 0 V, the rest of the
        // network gets its genuine IC, and the count is reported on InitialConditionFloatingNodes.
        int floating = PinFloatingNodes(fixedNode, fixedVal);
        InitialConditionFloatingNodes = floating;

        var icIdx = new int[_n];
        Array.Fill(icIdx, -1);
        int icFree = 0;
        for (int i = 0; i < _n; i++) if (!fixedNode[i]) icIdx[i] = icFree++;

        var vic = new double[_n];
        for (int i = 0; i < _n; i++) if (fixedNode[i]) vic[i] = fixedVal[i];

        if (icFree > 0)
        {
            var builder = new CsrMatrix.Builder(icFree);
            for (int i = 0; i < _branches.Length; i++)
            {
                if (!ConductsAtRest(_branches[i])) continue; // inductors open, caps fixed
                double g = RestConductance(i);   // the companion conductance for every pre-R078 kind
                int k = _branches[i].K, m = _branches[i].M;
                bool kf = k >= 0 && icIdx[k] >= 0, mf = m >= 0 && icIdx[m] >= 0;
                if (!_branches[i].Tapped)
                {
                    if (kf) builder.Add(icIdx[k], icIdx[k], g);
                    if (mf) builder.Add(icIdx[m], icIdx[m], g);
                    if (kf && mf) { builder.Add(icIdx[k], icIdx[m], -g); builder.Add(icIdx[m], icIdx[k], -g); }
                }
                else
                {
                    // R078: a tapped conducting arm (pure R, or a series R-C at rest): g*S^2 at K, g at M, -g*S off the diagonal.
                    double s = _branches[i].S;
                    if (kf) builder.Add(icIdx[k], icIdx[k], g * s * s);
                    if (mf) builder.Add(icIdx[m], icIdx[m], g);
                    if (kf && mf) { builder.Add(icIdx[k], icIdx[m], -g * s); builder.Add(icIdx[m], icIdx[k], -g * s); }
                }
            }
            var rhs = new double[icFree];
            foreach ((int node, Func<double, double> inj) in _injectors) if (node >= 0 && icIdx[node] >= 0) rhs[icIdx[node]] += inj(0.0);
            for (int i = 0; i < _branches.Length; i++)
            {
                if (!ConductsAtRest(_branches[i])) continue;
                double g = RestConductance(i);
                int k = _branches[i].K, m = _branches[i].M;
                bool kf = k >= 0 && icIdx[k] >= 0, mf = m >= 0 && icIdx[m] >= 0;
                double gc = _branches[i].Tapped ? g * _branches[i].S : g; // R078: the off-diagonal coupling
                if (kf && !mf && m != EmtNetwork.Ground) rhs[icIdx[k]] += gc * vic[m];
                if (mf && !kf && k != EmtNetwork.Ground) rhs[icIdx[m]] += gc * vic[k];
            }
            // Every remaining free node now has a conducting path to a reference, so the IC matrix is
            // non-singular by construction. A failure here is a genuine defect (e.g. a zero or negative
            // conductance) and must never again be skipped silently.
            SparseLuFactorization? icFact = SparseLu.Factor(builder.Build())
                ?? throw new InvalidOperationException(
                    $"EMT initial-condition matrix is singular over {icFree} node(s) even after pinning {floating} floating node(s); " +
                    "check for zero or negative branch resistances. The rest initial condition cannot be established.");
            double[] sol = icFact.Solve(rhs);
            for (int i = 0; i < _n; i++) if (icIdx[i] >= 0) vic[i] = sol[icIdx[i]];
        }

        for (int i = 0; i < _branches.Length; i++)
        {
            if (!_branches[i].History) continue;
            if (_branches[i].Kind == Kind.SeriesRc)
            {
                // R078: a series capacitor at rest is UNCHARGED (v_C = 0) and carries whatever current the rest
                // solve above put through it (with R != 0 it conducted there as 1/R; with R = 0 it was open, so
                // none). Its companion's memory is v_C = v - R*i, so seed i = g_rest * (arm voltage) and v = R*i,
                // which makes v_C exactly 0.
                double vkc = _branches[i].K < 0 ? 0.0 : vic[_branches[i].K];
                double vmc = _branches[i].M < 0 ? 0.0 : vic[_branches[i].M];
                double armV = _branches[i].Tapped ? vkc * _branches[i].S - vmc : vkc - vmc;
                double iRest = ConductsAtRest(_branches[i]) ? RestConductance(i) * armV : 0.0; // ideal (R = 0): open at rest
                _prevI[i] = iRest;
                _prevV[i] = _branches[i].R * iRest;
                continue;
            }
            double vk = _branches[i].K < 0 ? 0.0 : vic[_branches[i].K];
            double vm = _branches[i].M < 0 ? 0.0 : vic[_branches[i].M];
            _prevV[i] = _branches[i].Tapped ? vk * _branches[i].S - vm : vk - vm; // R078: arm sees S*v_K
            _prevI[i] = 0.0; // rest: zero initial reactive current
        }

        SeedCapacitorCurrentsAtRest(capHeld, vic);
    }

    /// <summary>
    /// N032: give each shunt capacitor that holds a node at 0 V the current that actually flows into that node at
    /// t = 0+, instead of zero.
    /// <para>Why: at t = 0+ every source is on (an ideal source at emf(0), a current source or a Thevenin source's
    /// Norton current at inj(0)) while every reactive element is at rest (inductor currents 0, capacitor voltages
    /// 0). A node held at 0 V by a capacitor still obeys Kirchhoff's current law: whatever flows into it (a current
    /// source's injection, or a resistive branch's current from a node the rest solve put at a non-zero voltage)
    /// can only go into its capacitor(s). The trapezoidal capacitor history <c>-Geq*v(n-1) - i(n-1)</c> reads that
    /// current. It used to be seeded with 0, so the injection was taken as starting from zero and ramping over the
    /// first step: the trapezoidal rule then integrates half a step of charge too little, a lasting offset of
    /// <c>-I*dt/(2C)</c> on the node (the step-from-rest artefact the rest solve exists to prevent, here for
    /// injections). This is what N032 reported as "the rest solve ignores current-source injections": the solve adds
    /// them on free nodes, but on a capacitor-held node (every bus with line charging in a grid build) they were
    /// dropped.</para>
    /// <para>Parallel capacitors at one node share the current in proportion to their capacitance (they all see the
    /// same dv/dt). Backward Euler's capacitor history does not read the previous current, so only the
    /// trapezoidal rule is affected. A node whose inflow is exactly zero is left untouched (its capacitors keep
    /// the +0.0 seed), so a network with no injection or resistive current into a capacitor-held node is
    /// bit-identical to before.</para>
    /// <para>Not covered (documented, unchanged): a node pinned at 0 V because it floats (R026) has no conducting
    /// path, so an injection there has nowhere defined to go at t = 0+; and an ideal series capacitor (R = 0) is
    /// open in the rest solve, so it carries no seed current.</para>
    /// </summary>
    private void SeedCapacitorCurrentsAtRest(bool[] capHeld, double[] vic)
    {
        // Net current flowing INTO each capacitor-held node at t = 0+ from sources and conducting branches.
        var inflow = new double[_n];
        bool any = false;
        foreach ((int node, Func<double, double> inj) in _injectors)
            if (node >= 0 && capHeld[node]) { inflow[node] += inj(0.0); any = true; }
        for (int i = 0; i < _branches.Length; i++)
        {
            Br b = _branches[i];
            if (!ConductsAtRest(b)) continue;
            bool kHeld = b.K >= 0 && capHeld[b.K], mHeld = b.M >= 0 && capHeld[b.M];
            if (!kHeld && !mHeld) continue;
            double vk = b.K < 0 ? 0.0 : vic[b.K];
            double vm = b.M < 0 ? 0.0 : vic[b.M];
            double g = RestConductance(i);
            // Arm current K -> M: g*(v_K - v_M), or with the R078 From-side tap g*(S*v_K - v_M); node K then gives
            // up S times the arm current and node M receives the arm current.
            double arm = b.Tapped ? g * (vk * b.S - vm) : g * (vk - vm);
            if (kHeld) inflow[b.K] -= b.Tapped ? arm * b.S : arm;
            if (mHeld) inflow[b.M] += arm;
            any = true;
        }
        if (!any) return;

        // Total shunt capacitance at each held node, for the proportional share.
        var cTotal = new double[_n];
        for (int i = 0; i < _branches.Length; i++)
        {
            Br b = _branches[i];
            if (b.Kind == Kind.Capacitor && b.M == EmtNetwork.Ground && b.K >= 0 && capHeld[b.K]) cTotal[b.K] += b.C;
        }
        for (int i = 0; i < _branches.Length; i++)
        {
            Br b = _branches[i];
            if (b.Kind != Kind.Capacitor || b.M != EmtNetwork.Ground || b.K < 0 || !capHeld[b.K]) continue;
            double into = inflow[b.K];
            if (into == 0.0) continue;   // nothing flows in: keep the +0.0 seed (bit-identity for such nodes)
            // One capacitor at the node: C / C is exactly 1, so it takes the whole inflow to the bit.
            _prevI[i] = into * (b.C / cTotal[b.K]);
        }
    }

    /// <summary>Nodes the rest initial-condition solve pinned at 0 V because no IC-conducting path (resistor or
    /// pure-resistance series arm) joins them to ground, a source or a capacitor-held node (R026). Zero for a
    /// network where every node has a resistive path to a reference.</summary>
    public int InitialConditionFloatingNodes { get; private set; }

    /// <summary>True when a branch conducts in the rest initial-condition solve: a resistor, or a series arm
    /// with no inductance (a pure-R coupler, R026 - it was treated as open). Inductive arms are open at rest
    /// (zero current) and capacitors hold their node at 0 V.
    /// <para>R078: a series R-C arm at rest is an uncharged capacitor (a short) in series with R, so with R != 0 it
    /// conducts as the resistor R. With R = 0 it would be an ideal short, which a nodal solve cannot hold (a
    /// stand-in conductance of order 2C/dt made the rest matrix numerically singular on pglib case240 with a
    /// fault switch), so an ideal series capacitor is taken as OPEN in the rest solve, like an inductor, and
    /// seeded uncharged with zero current. The only cost is that an inductor in series with an ideal series
    /// capacitor starts from a zero-voltage seed (the R026 first-step artefact, for that inductor only).</para></summary>
    private static bool ConductsAtRest(in Br b) =>
        b.Kind == Kind.Resistor || (b.Kind == Kind.SeriesRl && !(b.L > 0)) || (b.Kind == Kind.SeriesRc && b.R != 0);

    /// <summary>
    /// The conductance a conducting branch has in the rest initial-condition solve. For every pre-R078 kind it is
    /// the companion conductance, exactly as before (a resistor's companion is its conductance). R078: a series
    /// R-C arm with R != 0 conducts as 1/R (its uncharged capacitor is a short at rest).
    /// </summary>
    private double RestConductance(int i)
    {
        Br b = _branches[i];
        if (b.Kind == Kind.SeriesRc) return 1.0 / b.R;   // only reached for R != 0 (see ConductsAtRest)
        return _comp[i].Geq;
    }

    /// <summary>
    /// Find every non-fixed node with no IC-conducting path to ground or to a fixed node and pin it at 0 V
    /// (the rest value), returning how many were pinned. Union-find over the IC-conducting branches between
    /// two free nodes; a component is anchored when any of its nodes has a conducting branch to ground or to
    /// a fixed node.
    /// </summary>
    private int PinFloatingNodes(bool[] fixedNode, double[] fixedVal)
    {
        var parent = new int[_n];
        for (int i = 0; i < _n; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        var anchored = new bool[_n];
        foreach (Br b in _branches)
        {
            if (!ConductsAtRest(b)) continue;
            // N031: an OPEN switch neither joins two parts nor anchors one. Its open conductance (1e-9 S by default)
            // is a stand-in for "not connected". It used to anchor an island whose only tie to ground was an open
            // fault switch, so the island entered the IC matrix held by 1e-9 S while the same matrix carries 1e5 S
            // elsewhere (pglib case240, fault on bus 8034: bus 8034 joins the network only through an inductive arm,
            // and the other free nodes include series capacitors with R = 1e-5 conducting as 1/R). The sparse LU's
            // pivot floor is relative to the largest entry, so the whole run failed with "initial-condition matrix
            // is singular over 8 node(s)". Such an island has no defined rest voltage (with no source in it the exact
            // answer is 0 V anyway), so it is now pinned at 0 V and counted like any other floating node. An island
            // anchored some other way keeps the open switch's stamp in the IC matrix exactly as before.
            if (b.OpenAtRest) continue;
            bool kFree = b.K >= 0 && !fixedNode[b.K];
            bool mFree = b.M >= 0 && !fixedNode[b.M];
            if (kFree && mFree) { int rk = Find(b.K), rm = Find(b.M); if (rk != rm) parent[rk] = rm; }
            else if (kFree) anchored[b.K] = true;   // other end is ground or a fixed node
            else if (mFree) anchored[b.M] = true;
        }

        var rootAnchored = new bool[_n];
        for (int i = 0; i < _n; i++) if (!fixedNode[i] && anchored[i]) rootAnchored[Find(i)] = true;

        int pinned = 0;
        for (int i = 0; i < _n; i++)
        {
            if (fixedNode[i] || rootAnchored[Find(i)]) continue;
            fixedNode[i] = true;
            fixedVal[i] = 0.0;
            pinned++;
        }
        return pinned;
    }

    private Companion CompanionFor(in Br b) => b.Kind switch
    {
        Kind.Resistor => new Companion(b.Gfixed, 0, 0),
        Kind.SeriesRl => EmtCompanionStamp.SeriesRl(b.R, b.L, _dt, _rule),
        Kind.Capacitor => EmtCompanionStamp.Capacitor(b.C, _dt, _rule),
        Kind.Inductor => EmtCompanionStamp.Inductor(b.L, _dt, _rule),
        Kind.SeriesRc => EmtCompanionStamp.SeriesRc(b.R, b.C, _dt, _rule),
        _ => throw new InvalidOperationException(),
    };

    private bool IsFree(int node) => node >= 0 && _freeIndex[node] >= 0;

    /// <summary>Recompute companions for the current dt/rule, reassemble G over the free nodes, and factor it.</summary>
    private void Refactor()
    {
        for (int i = 0; i < _branches.Length; i++)
        {
            _comp[i] = CompanionFor(_branches[i]);
            // R078: a non-finite companion conductance (a zero resistance, or a negative series resistance that
            // exactly cancels the companion resistance at this dt) cannot be stamped. It used to surface only
            // later as a singular matrix or a NaN trajectory; name the element instead.
            // D-DOC-122: during a Critical Damping Adjustment burst the companions are rebuilt at HALF the run's
            // timestep, and the message used to quote only that half-step dt, which the user never asked for. Say
            // which step it is, and the run's own dt.
            if (!double.IsFinite(_comp[i].Geq))
                throw new InvalidOperationException(
                    $"EMT element {_branches[i].ElementId} ({_branches[i].K}->{_branches[i].M}) has a non-finite companion " +
                    $"conductance ({_comp[i].Geq:R}) at dt = {_dt:R} s" +
                    (_dt.Equals(_constructedDt) ? "" : $" (a Critical Damping Adjustment half-step of the run's dt = {_constructedDt:R} s)") +
                    ": its resistance is zero or cancels its companion resistance.");
        }

        var builder = new CsrMatrix.Builder(_freeCount);
        for (int i = 0; i < _branches.Length; i++)
        {
            double g = _comp[i].Geq;
            int k = _branches[i].K, m = _branches[i].M;
            bool kf = IsFree(k), mf = IsFree(m);
            if (!_branches[i].Tapped)
            {
                if (kf) builder.Add(_freeIndex[k], _freeIndex[k], g);
                if (mf) builder.Add(_freeIndex[m], _freeIndex[m], g);
                if (kf && mf) { builder.Add(_freeIndex[k], _freeIndex[m], -g); builder.Add(_freeIndex[m], _freeIndex[k], -g); }
            }
            else
            {
                // R078: an ideal From-side transformer (S = 1/Tap) in front of the series arm. The arm current is
                // i = g*(S*v_K - v_M) + I_hist; node K carries S*i and node M receives i, so the stamp is
                // g*S^2 at (K,K), g at (M,M) and -g*S on both off-diagonals (still symmetric).
                double s = _branches[i].S;
                if (kf) builder.Add(_freeIndex[k], _freeIndex[k], g * s * s);
                if (mf) builder.Add(_freeIndex[m], _freeIndex[m], g);
                if (kf && mf) { builder.Add(_freeIndex[k], _freeIndex[m], -g * s); builder.Add(_freeIndex[m], _freeIndex[k], -g * s); }
            }
        }
        foreach (Line ln in _lines)   // travelling-wave line: a surge conductance to ground at each end (diagonal only)
        {
            if (IsFree(ln.K)) builder.Add(_freeIndex[ln.K], _freeIndex[ln.K], ln.Yc);
            if (IsFree(ln.M)) builder.Add(_freeIndex[ln.M], _freeIndex[ln.M], ln.Yc);
        }
        CsrMatrix gMat = builder.Build();
        if (_ordering is null || !gMat.RowPtr.AsSpan().SequenceEqual(_orderingRowPtr) || !gMat.ColIdx.AsSpan().SequenceEqual(_orderingColIdx))
        {
            _ordering = SparseLu.Ordering(gMat);
            _orderingRowPtr = (int[])gMat.RowPtr.Clone();
            _orderingColIdx = (int[])gMat.ColIdx.Clone();
        }
        _fact = SparseLu.FactorWithOrdering(gMat, _ordering) ?? throw new InvalidOperationException("EMT nodal matrix G is singular (check for a floating subnetwork without a source/ground reference).");
        if (_vFree.Length != _freeCount) { _vFree = new double[_freeCount]; _solveWork = new double[_freeCount]; }
        Refactorizations++;
    }

    /// <summary>Advance one timestep to time <paramref name="t"/> (sources evaluated at t). The history from the
    /// previous step drives the right-hand side; after the solve each element's history is updated. Stepping by
    /// hand advances the state, so a later <see cref="Simulate"/> needs a <see cref="Reset"/> first (R077).</summary>
    public void Step(double t)
    {
        _advanced = true;
        Step(t, advanceLines: true);
    }

    private void Step(double t, bool advanceLines)
    {
        // Dirichlet voltages at this instant.
        foreach ((int node, Func<double, double> emf) in _dirichlet) if (node >= 0) _vDir[node] = emf(t);

        Array.Clear(_rhs);
        foreach ((int node, Func<double, double> inj) in _injectors) if (IsFree(node)) _rhs[_freeIndex[node]] += inj(t);

        // Travelling-wave line history: inject the wave that left the far end N steps ago (I_km = Yc*v_k + I_k
        // leaves node k, so the known I_k moves to the RHS with a minus sign).
        foreach (Line ln in _lines)
        {
            if (IsFree(ln.K)) _rhs[_freeIndex[ln.K]] -= ln.BufK[ln.Pos];
            if (IsFree(ln.M)) _rhs[_freeIndex[ln.M]] -= ln.BufM[ln.Pos];
        }

        for (int i = 0; i < _branches.Length; i++)
        {
            double ih = _branches[i].History ? _comp[i].History(_prevV[i], _prevI[i]) : 0.0;
            _curIh[i] = ih;
            int k = _branches[i].K, m = _branches[i].M;
            double g = _comp[i].Geq;
            bool kf = IsFree(k), mf = IsFree(m);
            if (!_branches[i].Tapped)
            {
                if (kf) { _rhs[_freeIndex[k]] -= ih; if (!mf && m != EmtNetwork.Ground) _rhs[_freeIndex[k]] += g * _vDir[m]; }
                if (mf) { _rhs[_freeIndex[m]] += ih; if (!kf && k != EmtNetwork.Ground) _rhs[_freeIndex[m]] += g * _vDir[k]; }
            }
            else
            {
                // R078: tapped arm. Node K sees S times the arm's history current and the -g*S coupling to a
                // Dirichlet node M; node M sees the arm's history current and the -g*S coupling to a Dirichlet K.
                double s = _branches[i].S, gs = g * s;
                if (kf) { _rhs[_freeIndex[k]] -= ih * s; if (!mf && m != EmtNetwork.Ground) _rhs[_freeIndex[k]] += gs * _vDir[m]; }
                if (mf) { _rhs[_freeIndex[m]] += ih; if (!kf && k != EmtNetwork.Ground) _rhs[_freeIndex[m]] += gs * _vDir[k]; }
            }
        }

        _fact!.SolveInto(_rhs, _vFree, _solveWork);
        double[] vFree = _vFree;

        for (int node = 0; node < _n; node++)
            _vNode[node] = _freeIndex[node] >= 0 ? vFree[_freeIndex[node]] : _vDir[node];

        for (int i = 0; i < _branches.Length; i++)
        {
            // R078: a tapped arm's own voltage is S*v_K - v_M (the K end sits behind the ideal transformer).
            double vB = _branches[i].Tapped
                ? NodeV(_branches[i].K) * _branches[i].S - NodeV(_branches[i].M)
                : NodeV(_branches[i].K) - NodeV(_branches[i].M);
            _prevV[i] = vB;
            _prevI[i] = _comp[i].Geq * vB + _curIh[i];
        }

        // Update each line's history buffer with the wave leaving now, to be read N steps later. During a CDA
        // half-step (advanceLines=false) the line history is frozen so the line advances exactly one slot per
        // full timestep regardless of the burst.
        foreach (Line ln in _lines)
        {
            double vk = NodeV(ln.K), vm = NodeV(ln.M);
            double iKm = ln.Yc * vk + ln.BufK[ln.Pos];   // current from k into the line
            double iMk = ln.Yc * vm + ln.BufM[ln.Pos];
            ln.LastIkm = iKm;
            if (advanceLines)
            {
                ln.BufK[ln.Pos] = -ln.Yc * vm - iMk;      // I_k to be read one travel-time from now
                ln.BufM[ln.Pos] = -ln.Yc * vk - iKm;      // I_m to be read one travel-time from now
                ln.Pos = (ln.Pos + 1) % ln.N;
            }
        }
    }

    private double NodeV(int node) => node < 0 ? 0.0 : _vNode[node];

    /// <summary>Change the timestep and/or integration rule and re-factor (companions and G depend on both).</summary>
    private void SetIntegration(double dt, IntegrationRule rule)
    {
        _dt = dt;
        _rule = rule;
        Refactor();
    }

    /// <summary>Apply a switching event: toggle the switch's conductance. The matrix pattern is unchanged (the
    /// switch stays present as a small conductance when open), so only a numeric re-factor is needed - done by
    /// the caller after all events at this instant are applied.</summary>
    /// <returns>True if the event actually changed a conductance (R241: only then is there a discontinuity
    /// that needs a re-factor or the CDA burst). Re-closing a closed switch, or re-opening an open one, is a
    /// no-op and must not perturb the trajectory with spurious backward-Euler damping.</returns>
    private bool ApplyEvent(EmtEvent ev)
    {
        // Simulate validated every event up front, so the lookup cannot miss here.
        var se = (SwitchEvent)ev;
        var s = _switchMap[se.ElementId];
        double g = se.Close ? 1.0 / s.Ron : 1.0 / s.Roff;
        if (_branches[s.Branch].Gfixed == g) return false;
        _branches[s.Branch].Gfixed = g;
        return true;
    }

    /// <summary>
    /// Refuse a case whose events cannot act (R241). A switch event naming an element id that is not a switch
    /// (an unknown id, or a series branch id passed to <c>switch:&lt;id&gt;@t:open</c>) used to be ignored
    /// without a word while the run still applied the CDA damping burst and a re-factor at that instant, so the
    /// user believed a branch had opened when nothing had. Every event must now name a switch; anything else
    /// throws <see cref="ArgumentException"/> listing the offending events and the valid switch ids.
    /// </summary>
    private void ValidateEvents(IReadOnlyList<EmtEvent> events)
    {
        var bad = new List<string>();
        foreach (EmtEvent ev in events)
        {
            if (ev is not SwitchEvent se)
                bad.Add($"{ev.GetType().Name} at {ev.AtSeconds} s (this engine applies only switch events)");
            else if (!_switchMap.ContainsKey(se.ElementId))
                bad.Add($"switch event at {se.AtSeconds} s names element {se.ElementId}, which is not a switch");
        }
        if (bad.Count > 0)
            throw new ArgumentException(
                "EMT case has events that cannot act: " + string.Join("; ", bad) +
                $". Valid switch ids: {(_switchMap.Count == 0 ? "none" : string.Join(", ", _switchMap.Keys.OrderBy(k => k)))}.");
    }

    /// <summary>
    /// D-DOC-022: the number of steps a run of <paramref name="duration"/> seconds takes at <paramref name="dt"/>,
    /// <c>round(duration / dt)</c> exactly as before, refusing a run with no steps. A duration that is negative, zero,
    /// not a number, or so short that it rounds to no step at all used to give a "run" with only the t = 0 sample (and
    /// exit 0 from the CLI), and a larger negative duration failed with a raw list-capacity message. A step count past
    /// what one trajectory list can hold is refused too, rather than overflowing the integer conversion.
    /// </summary>
    private static int StepCount(double duration, double dt)
    {
        if (!double.IsFinite(duration) || !(duration > 0))
            throw new ArgumentException(
                $"EmtCase.DurationSeconds = {duration:R} s; an EMT run needs a positive, finite duration.", nameof(duration));
        double exact = Math.Round(duration / dt);
        if (exact < 1)
            throw new ArgumentException(
                $"EmtCase.DurationSeconds = {duration:R} s is less than half of one timestep (dt = {dt:R} s), so the run " +
                "would take no step at all; use a longer duration or a smaller timestep.", nameof(duration));
        if (exact > int.MaxValue - 1)
            throw new ArgumentException(
                $"EmtCase.DurationSeconds = {duration:R} s at dt = {dt:R} s is {exact:R} steps, more than one run can record " +
                $"({int.MaxValue - 1}).", nameof(duration));
        return (int)exact;   // the same value (int)Math.Round(duration / dt) always gave
    }

    /// <summary>
    /// D-DOC-019: every event time must be a finite number in [0, end of run]. A NEGATIVE time used to be applied at
    /// the first step as if it were 0 (the docs-session run with an event at t = -1 then diverged and the CLI exited
    /// 0), a NaN time never fired, and a time after the last step was silently never applied, so the trajectory
    /// showed a run without the event the user asked for. <paramref name="lastFiringTime"/> is the latest time the
    /// step loop can still apply (the last step's time plus the event-time tolerance), computed exactly as the loop
    /// compares it. An event at 0 &lt;= t &lt; dt is applied at the first step, as before.
    /// </summary>
    private static void ValidateEventTimes(IReadOnlyList<EmtEvent> events, double lastFiringTime)
    {
        var bad = new List<string>();
        foreach (EmtEvent ev in events)
        {
            ArgumentNullException.ThrowIfNull(ev, nameof(events));
            string what = ev is SwitchEvent se ? $"switch event on element {se.ElementId}" : ev.GetType().Name;
            if (!double.IsFinite(ev.AtSeconds) || ev.AtSeconds < 0)
                bad.Add($"{what} at {ev.AtSeconds:R} s (an event time must be a finite number of seconds, 0 or later)");
            else if (!(ev.AtSeconds <= lastFiringTime))
                bad.Add($"{what} at {ev.AtSeconds:R} s (after the run's last step, so it would never be applied)");
        }
        if (bad.Count > 0)
            throw new ArgumentException("EMT case has events at times the run cannot apply: " + string.Join("; ", bad) + ".", nameof(events));
    }

    /// <summary>
    /// D-DOC-025: refuse a probe that names nothing, instead of recording a column of NaN (an unknown branch id) or
    /// crashing with an index error (a node past the end). A node-voltage probe must name a node of the network
    /// (0..NodeCount-1). A branch-current probe must name an element that carries one current: a series R-L or R-C
    /// arm, a switch, a travelling-wave line, or a shunt with exactly ONE part (G, C or L). A shunt with several parts
    /// is refused because the engine stamps each part as its own branch and the probe read only the first; a voltage
    /// or current source is refused because its stamped branch is not the source current (probe the arm it feeds).
    /// </summary>
    private void ValidateProbes(IReadOnlyList<EmtProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        var bad = new List<string>();
        foreach (EmtProbe p in probes)
        {
            ArgumentNullException.ThrowIfNull(p, nameof(probes));
            if (p.Kind == EmtProbeKind.NodeVoltage)
            {
                if (p.Target < 0 || p.Target >= _n)
                    bad.Add($"'{p.Name}' reads node {p.Target}, but the network's nodes are 0..{_n - 1}");
                continue;
            }
            if (p.Kind != EmtProbeKind.BranchCurrent) { bad.Add($"'{p.Name}' has an unknown probe kind {p.Kind}"); continue; }
            if (!_elementById.TryGetValue(p.Target, out EmtElement? e))
            {
                bad.Add($"'{p.Name}' reads the current of element {p.Target}, which is not in the network");
                continue;
            }
            bool oneCurrent = e switch
            {
                SeriesBranch or SeriesCapacitorBranch or SwitchElement or BergeronLine => true,
                ShuntElement s => (s.G != 0 ? 1 : 0) + (s.C != 0 ? 1 : 0) + (s.L != 0 ? 1 : 0) == 1,
                _ => false,
            };
            if (!oneCurrent)
                bad.Add($"'{p.Name}' reads the current of {e.GetType().Name} {e.Id}, which does not carry one branch current " +
                        "(probe a series arm, a switch, a travelling-wave line or a single-part shunt)");
        }
        if (bad.Count > 0)
            throw new ArgumentException("EMT case has probes that name nothing it can record: " + string.Join("; ", bad) + ".", nameof(probes));
    }

    /// <summary>
    /// R077: refuse a case that does not describe this engine's run. The timestep must equal the construction dt
    /// bit for bit. The network must be the same object, or the same node count and the same elements in the same
    /// order, compared by value; a source's EMF / injection function cannot be compared (two lambdas with the same
    /// body are different objects), so for a <see cref="VoltageSource"/> or <see cref="CurrentSource"/> the id,
    /// bus and source resistance are compared and the ENGINE's function is the one that runs.
    /// </summary>
    private void ValidateCase(EmtCase c)
    {
        if (!c.DtSeconds.Equals(_constructedDt))
            throw new ArgumentException(
                $"EmtCase.DtSeconds = {c.DtSeconds:R} s but this engine was built with dt = {_constructedDt:R} s. The companions " +
                "and travelling-wave delays are fixed at construction; build the engine with the case's dt.", nameof(c));
        ArgumentNullException.ThrowIfNull(c.Network, "c.Network");
        string? diff = NetworkDifference(_network, c.Network);
        if (diff is not null)
            throw new ArgumentException(
                $"EmtCase.Network is not the network this engine was built from ({diff}). The engine runs the network " +
                "it was constructed with; pass that network in the case.", nameof(c));
    }

    /// <summary>R077: null when the two networks are equivalent, else a plain-English description of the first
    /// difference.</summary>
    private static string? NetworkDifference(EmtNetwork built, EmtNetwork given)
    {
        if (ReferenceEquals(built, given)) return null;
        if (built.NodeCount != given.NodeCount) return $"node count {given.NodeCount} versus {built.NodeCount}";
        if (built.Elements.Count != given.Elements.Count) return $"element count {given.Elements.Count} versus {built.Elements.Count}";
        for (int i = 0; i < built.Elements.Count; i++)
        {
            EmtElement a = built.Elements[i], b = given.Elements[i];
            bool same = (a, b) switch
            {
                (VoltageSource x, VoltageSource y) => x.Id == y.Id && x.Bus == y.Bus && x.Rs.Equals(y.Rs),
                (CurrentSource x, CurrentSource y) => x.Id == y.Id && x.Bus == y.Bus,
                _ => a.Equals(b),
            };
            if (!same) return $"element {i} is {b} versus {a}";
        }
        return null;
    }

    /// <summary>Read a probe's value from the just-completed step.</summary>
    private double Read(EmtProbe p) => p.Kind switch
    {
        EmtProbeKind.NodeVoltage => NodeV(p.Target),
        EmtProbeKind.BranchCurrent => _branchOfElement.TryGetValue(p.Target, out int bi) ? _prevI[bi]
            : _lineOfElement.TryGetValue(p.Target, out int li) ? _lines[li].LastIkm : double.NaN,
        _ => double.NaN,
    };

    /// <summary>Run a full fixed-step batch simulation, returning the recorded trajectory. The loop is
    /// allocation-free per step apart from the solver's solution vector, and runs under low-latency GC.
    /// <para>R077: the case must describe THIS engine's run. Its <see cref="EmtCase.Network"/> must be the network
    /// the engine was built from (or element-for-element equivalent to it) and its <see cref="EmtCase.DtSeconds"/>
    /// must equal the construction timestep exactly, otherwise <see cref="ArgumentException"/>: the companions and
    /// the travelling-wave delays were fixed at construction, so a different dt would silently change step at the
    /// first switching event and a different network would simply be ignored. A second call on the same engine
    /// throws <see cref="InvalidOperationException"/> unless <see cref="Reset"/> was called in between, because it
    /// would continue the old state while restarting time at zero.</para>
    /// <para>GR-570: a probe value that is NaN or infinite (a numerical failure, typically a non-passive network let
    /// through with <see cref="EmtOptions.RefuseNonPassive"/> off) throws <see cref="InvalidOperationException"/> at
    /// the first step it appears, instead of being returned as a normal trajectory.</para></summary>
    public EmtResult Simulate(EmtCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        ValidateCase(c);          // R077: the case must match the engine it is run on
        if (_advanced)
            throw new InvalidOperationException(
                "This EmtEngine has already been advanced (by Simulate or Step). A second Simulate would continue from " +
                "the end state while restarting time at zero; call Reset() first, or build a new engine.");

        double dt = c.DtSeconds;
        int steps = StepCount(c.DurationSeconds, dt);   // D-DOC-022: refuses a run of no steps
        double eps = dt * EmtConstants.EventTimeToleranceFraction;
        // D-DOC-019: refuse an event time that is not a number, negative, or after the last step, BEFORE sorting (a
        // NaN time sorts unpredictably and then never fires).
        ValidateEventTimes(c.Events ?? Array.Empty<EmtEvent>(), steps * dt + eps);
        var events = (c.Events ?? Array.Empty<EmtEvent>()).OrderBy(e => e.AtSeconds).ToList();
        ValidateEvents(events);   // fail loud before any step (R241)
        ValidateProbes(c.Probes); // D-DOC-025: every probe must name something that exists
        _advanced = true;         // R077: from here on the state belongs to this run
        int ei = 0;

        var points = new List<EmtPoint>(steps + 1);
        double maxAbs = 0.0;

        var p0 = new double[c.Probes.Count];
        for (int j = 0; j < c.Probes.Count; j++) { p0[j] = Read(c.Probes[j]); RequireFiniteProbe(p0[j], c.Probes[j].Name, 0.0); }
        points.Add(new EmtPoint(0.0, p0));

        var sw = Stopwatch.StartNew();
        using (GcScope.LowLatency())
        {
            for (int step = 1; step <= steps; step++)
            {
                double t = step * dt;

                bool fired = false;
                // 'fired' only when some event really changed a conductance (R241): a no-op event leaves the
                // network, and therefore the trajectory, exactly as if it had not been scheduled.
                while (ei < events.Count && events[ei].AtSeconds <= t + eps) { fired |= ApplyEvent(events[ei]); ei++; }

                if (fired && _useCda && _baseRule == IntegrationRule.Trapezoidal)
                {
                    // Critical Damping Adjustment: two backward-Euler half-steps across the discontinuity
                    // annihilate the spurious trapezoidal Nyquist oscillation, then resume trapezoidal.
                    SetIntegration(dt / 2.0, IntegrationRule.BackwardEuler);
                    Step(t - dt / 2.0, advanceLines: false);
                    Step(t, advanceLines: true);
                    SetIntegration(dt, _baseRule);
                }
                else
                {
                    if (fired) Refactor(); // pick up the toggled conductance at the current dt/rule
                    Step(t);
                }

                var probe = new double[c.Probes.Count];
                for (int j = 0; j < c.Probes.Count; j++)
                {
                    double val = Read(c.Probes[j]);
                    RequireFiniteProbe(val, c.Probes[j].Name, t);
                    probe[j] = val;
                    double a = Math.Abs(val);
                    if (a > maxAbs) maxAbs = a;
                }
                points.Add(new EmtPoint(t, probe));
            }
        }
        sw.Stop();

        var names = c.Probes.Select(p => p.Name).ToArray();
        return new EmtResult(names, points, new EmtRunStats(steps, Refactorizations, maxAbs, sw.Elapsed.TotalMilliseconds));
    }

    // GR-570: a NaN or infinite probe value is a numerical failure, never a result. The single-phase engine used to
    // return such a trajectory as normal and leave the check to the caller (the CLI did it; a library caller might not).
    private static void RequireFiniteProbe(double value, string probeName, double tSeconds)
    {
        if (double.IsFinite(value)) return;
        throw new InvalidOperationException(
            $"non-finite value in trajectory (numerical failure): probe {probeName} reads " +
            $"{value.ToString(System.Globalization.CultureInfo.InvariantCulture)} at t = " +
            $"{tSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} s.");
    }
}
