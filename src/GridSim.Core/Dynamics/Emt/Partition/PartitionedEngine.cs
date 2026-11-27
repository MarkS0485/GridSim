using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Emt.Nodal;

namespace GridSim.Core.Dynamics.Emt.Partition;

/// <summary>Options of a partitioned run.</summary>
public sealed record PartitionedOptions
{
    /// <summary>Worker threads for the partitions (1 = serial in partition order). The result does not depend on this number.</summary>
    public int Threads { get; init; } = 1;
    /// <summary>The nodal solver options (rule, sampling). The event rule must be Trapezoidal (see <see cref="PartitionedEngine"/>).</summary>
    public NodalOptions Nodal { get; init; } = new();
}

/// <summary>The result of a partitioned run: the merged solver-style result and the plan that produced it.</summary>
/// <param name="Run">Series in the order of the probes given, times, status; its <c>Stats.Refactorisations</c> is the sum over partitions.</param>
/// <param name="Plan">The partition plan (cut lines, window, exchange schedule in words).</param>
/// <param name="Threads">Worker threads used.</param>
public sealed record PartitionedResult(NodalResult Run, PartitionPlan Plan, int Threads);

/// <summary>
/// Runs a <see cref="PartitionedModel"/> with the partitions in parallel. Contract:
/// <list type="bullet">
/// <item>The run is BIT-IDENTICAL to the single-partition run (the same model built with <c>singlePartition: true</c> and run on the
/// incumbent <see cref="NodalSolver"/>) and to itself at every thread count. Partitions are cut only at Bergeron lines whose travel
/// time is at least one step, so each partition's inputs are history values written by earlier steps; partitions advance
/// <see cref="PartitionPlan.WindowSteps"/> steps between exchanges, each solving its own nodes with the same arithmetic in the
/// same element order as the monolithic matrix (a block of a block-diagonal matrix factorises to the same bits), then wait at a
/// barrier. There is no sum across partitions anywhere, so no result depends on thread order.</item>
/// <item>Each partition runs the incumbent solver's step logic (switch events, switch logic, matrix-varying elements, linear solve)
/// in a copy of its loop, so the incumbent <see cref="NodalSolver"/> is untouched.</item>
/// <item>Refused: nonlinear (Newton) elements, because the incumbent Newton test (residual and step size) is taken over ALL nodes, so
/// a Newton network is not separable bit for bit (run it on <see cref="NodalSolver"/>); and the backward-Euler event rule, because
/// the incumbent applies it to every element when ANY element switches, a global per-step decision.</item>
/// </list>
/// Node names in probes are <c>partition/name</c> (see <see cref="PartitionedModel.Node"/>).
/// </summary>
public static class PartitionedEngine
{
    /// <summary>Run the model.</summary>
    /// <exception cref="ArgumentException">Domain refusal: bad step, a cut line shorter than the step, a nonlinear element, a probe or event that resolves nowhere.</exception>
    public static PartitionedResult Run(PartitionedModel model, double dt, double duration, IReadOnlyList<NodalProbe> probes,
        IReadOnlyList<NodalEvent>? events = null, PartitionedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(probes);
        options ??= new PartitionedOptions();
        NodalOptions no = options.Nodal;
        static Exception Refuse(string m) => DomainRefusal.Mark(new ArgumentException(m));
        if (!(dt > 0 && double.IsFinite(dt))) throw Refuse("The time step must be finite and positive.");
        if (!(duration > 0 && double.IsFinite(duration))) throw Refuse("The run length must be finite and positive.");
        if (no.SampleEvery < 1) throw Refuse("SampleEvery must be at least 1.");
        if (options.Threads < 1) throw Refuse("Threads must be at least 1.");
        if (no.EventRule != NodalEventRule.Trapezoidal)
            throw Refuse("A partitioned run needs the trapezoidal event rule: the backward-Euler event step is decided globally (any element switching changes every element's rule), which partitions cannot reproduce bit for bit. Use the single-partition form.");
        double stepsD = Math.Ceiling(duration / dt - 1e-9);
        if (stepsD > 200_000_000.0) throw Refuse("The run would take more than 2e8 steps.");
        long steps = (long)stepsD;
        PartitionPlan plan = LinePartitioner.Plan(model, dt);

        IReadOnlyList<NodalNetwork> nets = model.Networks;
        var runners = new PartitionRunner[nets.Count];
        for (int p = 0; p < runners.Length; p++) runners[p] = new PartitionRunner(nets[p], p);

        // Probes: each resolves in exactly one network.
        var owner = new int[probes.Count];
        for (int k = 0; k < probes.Count; k++)
        {
            NodalProbe pr = probes[k];
            int found = -1;
            for (int p = 0; p < runners.Length; p++)
            {
                bool has = pr.Kind == NodalProbeKind.NodeVoltage ? nets[p].FindNode(pr.Target) != -2 : nets[p].Find(pr.Target) is not null;
                if (!has) continue;
                if (found >= 0) throw Refuse($"Probe '{pr.Name}': '{pr.Target}' exists in more than one partition.");
                found = p;
            }
            if (found < 0) throw Refuse($"Probe '{pr.Name}': no node or element '{pr.Target}'.");
            owner[k] = found;
            runners[found].AddProbe(k, pr);
        }
        if (events is not null)
            foreach (NodalEvent ev in events)
            {
                if (!(double.IsFinite(ev.AtSeconds) && ev.AtSeconds >= 0)) throw Refuse("An event time must be finite and not negative.");
                int found = -1;
                for (int p = 0; p < runners.Length; p++) if (nets[p].Find(ev.SwitchName) is NodalSwitch) { found = p; break; }
                if (found < 0) throw Refuse($"Event: no switch '{ev.SwitchName}'.");
                runners[found].AddEvent(ev, nets[found]);
            }

        // Validate, reset and configure every partition (serially, in partition order: the first refusal is the lowest partition's).
        foreach (PartitionRunner r in runners) r.Initialise(dt, no);

        int threads = Math.Min(options.Threads, runners.Length);
        int window = plan.WindowSteps;
        for (long s0 = 1; s0 <= steps; s0 += window)
        {
            long s1 = Math.Min(steps, s0 + window - 1);
            long a = s0, b = s1;
            ForEachPartition(runners.Length, threads, p => runners[p].Advance(a, b, dt, no, steps));
            if (runners.Any(r => r.Status != NodalStatus.Completed)) break;
        }

        // Merge. The run is the part every partition completed.
        long done = runners.Min(r => r.Done);
        NodalStatus status = NodalStatus.Completed;
        string message = "completed";
        double failedAt = double.NaN;
        foreach (PartitionRunner r in runners)
            if (r.Status != NodalStatus.Completed && (status == NodalStatus.Completed || r.FailedAt < failedAt))
            {
                status = r.Status; failedAt = r.FailedAt;
                message = (runners.Length > 1 ? $"partition {r.Index}: " : "") + r.Message;
            }
        PartitionRunner first = runners[0];
        int count = first.StepOfSample.Count;
        while (count > 0 && first.StepOfSample[count - 1] > done) count--;
        var times = first.Times.Take(count).ToArray();
        var series = new double[probes.Count][];
        for (int k = 0; k < probes.Count; k++) series[k] = runners[owner[k]].SeriesOf(k).Take(count).ToArray();
        int refac = runners.Sum(r => r.Refactorisations);
        string method = (no.Rule == IntegrationRule.Trapezoidal ? "trapezoidal" : "backward-euler") + $", partitioned ({plan.Partitions} partition(s))";
        var run = new NodalResult(dt, method, status, message, failedAt, probes.Select(p => p.Name).ToArray(), times, series,
            new NodalRunStats(done, refac, 0, 0), no.Newton);
        return new PartitionedResult(run, plan, threads);
    }

    private static void ForEachPartition(int count, int threads, Action<int> body)
    {
        var errors = new Exception?[count];
        if (threads <= 1 || count <= 1)
        {
            for (int i = 0; i < count; i++)
            {
                try { body(i); }
                catch (Exception ex) { errors[i] = ex; }
            }
        }
        else
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
            {
                try { body(i); }
                catch (Exception ex) { errors[i] = ex; }
            });
        }
        foreach (Exception? ex in errors)
            if (ex is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
    }

    /// <summary>One partition: the incumbent solver's per-step logic for a network without nonlinear elements.</summary>
    private sealed class PartitionRunner
    {
        private readonly NodalNetwork _net;
        private readonly int _n;
        private readonly IReadOnlyList<NodalElement> _els;
        private readonly bool[] _fixed;
        private readonly List<NodalElement> _logic = new(), _varying = new();
        private readonly List<(int K, NodalProbe P)> _probes = new();
        private int[] _probeNodes = [];
        private NodalElement?[] _probeEls = [];
        private readonly List<(double T, NodalSwitch Sw, bool Close)> _pending = new();
        private List<(double T, NodalSwitch Sw, bool Close)> _queue = new();
        private int _next;
        private NodalStamper _st = null!;
        private DenseLu _lu = null!;
        private double[] _v = [], _rhs0 = [];
        private IntegrationRule _configured;
        private bool _dirty = true;
        private Dictionary<int, List<double>> _series = new();

        public PartitionRunner(NodalNetwork net, int index)
        {
            _net = net; Index = index; _n = net.NodeCount; _els = net.Elements; _fixed = new bool[_n];
        }

        public int Index { get; }
        public NodalStatus Status { get; private set; } = NodalStatus.Completed;
        public string Message { get; private set; } = "completed";
        public double FailedAt { get; private set; } = double.NaN;
        public long Done { get; private set; }
        public int Refactorisations { get; private set; }
        public List<double> Times { get; } = new();
        public List<long> StepOfSample { get; } = new();

        public void AddProbe(int k, NodalProbe p) => _probes.Add((k, p));
        public void AddEvent(NodalEvent ev, NodalNetwork net) => _pending.Add((ev.AtSeconds, (NodalSwitch)net.Find(ev.SwitchName)!, ev.Close));
        public List<double> SeriesOf(int k) => _series[k];

        public void Initialise(double dt, NodalOptions options)
        {
            static Exception Refuse(string m) => DomainRefusal.Mark(new ArgumentException(m));
            if (_n == 0) throw Refuse("A partition has no node.");
            foreach (NodalElement e in _els)
            {
                if (e.IsNonlinear) throw Refuse($"{e.Name}: a nonlinear (Newton) element cannot be partitioned bit for bit; run the network on the single-partition solver.");
                int fx = e.FixedNode;
                if (fx >= 0)
                {
                    if (_fixed[fx]) throw Refuse($"Node '{_net.NodeNames[fx]}' has two ideal voltage sources.");
                    _fixed[fx] = true;
                }
                try { e.Validate(dt, _n); }
                catch (ArgumentException ex) { throw DomainRefusal.Mark(new ArgumentException($"{e.Name}: {ex.Message}")); }
                if (e.HasSwitchLogic) _logic.Add(e);
                if (e.IsMatrixVarying) _varying.Add(e);
            }
            _probeNodes = new int[_probes.Count];
            _probeEls = new NodalElement?[_probes.Count];
            for (int i = 0; i < _probes.Count; i++)
            {
                NodalProbe p = _probes[i].P;
                if (p.Kind == NodalProbeKind.NodeVoltage) _probeNodes[i] = _net.FindNode(p.Target);
                else
                {
                    NodalElement e = _net.Find(p.Target)!;
                    if (p.Kind == NodalProbeKind.ElementCurrent && (p.Channel < 0 || p.Channel >= e.CurrentChannels))
                        throw Refuse($"Probe '{p.Name}': element '{p.Target}' has no current channel {p.Channel}.");
                    _probeEls[i] = e;
                }
                _series[_probes[i].K] = new List<double>();
            }
            // Stable order by time, ties in the order given.
            var order = Enumerable.Range(0, _pending.Count).OrderBy(i => _pending[i].T).ThenBy(i => i).ToArray();
            _queue = order.Select(i => _pending[i]).ToList();
            _next = 0;

            foreach (NodalElement e in _els) e.Reset();
            _configured = options.Rule;
            foreach (NodalElement e in _els) e.Configure(dt, _configured);
            _st = new NodalStamper(_n, _fixed);
            _lu = new DenseLu(_n);
            _v = new double[_n];
            _rhs0 = new double[_n];
            Record(0.0, 0);
        }

        private void Record(double t, long step)
        {
            Times.Add(t);
            StepOfSample.Add(step);
            for (int i = 0; i < _probes.Count; i++)
            {
                NodalProbe p = _probes[i].P;
                double x = p.Kind switch
                {
                    NodalProbeKind.NodeVoltage => _probeNodes[i] < 0 ? 0.0 : _v[_probeNodes[i]],
                    NodalProbeKind.ElementCurrent => _probeEls[i]!.ProbeCurrent(p.Channel),
                    _ => _probeEls[i]!.ProbeStateAt(p.Channel),
                };
                _series[_probes[i].K].Add(x);
            }
        }

        public void Advance(long s0, long s1, double dt, NodalOptions options, long steps)
        {
            if (Status != NodalStatus.Completed) return;
            IntegrationRule baseRule = options.Rule;
            for (long s = s0; s <= s1; s++)
            {
                double t = s * dt;
                while (_next < _queue.Count && t >= _queue[_next].T - EmtConstants.EventTimeToleranceFraction * dt)
                {
                    var (_, sw, close) = _queue[_next++];
                    if (sw.Closed != close) { sw.Closed = close; _dirty = true; }
                }
                foreach (NodalElement e in _logic) if (e.SwitchLogic(t)) _dirty = true;
                if (baseRule != _configured)
                {
                    _configured = baseRule;
                    foreach (NodalElement e in _els) e.Configure(dt, _configured);
                    _dirty = true;
                }
                if (_varying.Count > 0)
                {
                    foreach (NodalElement e in _varying) e.PrepareStep(t, dt);
                    _dirty = true;
                }
                if (_dirty)
                {
                    _st.Clear();
                    foreach (NodalElement e in _els) e.StampMatrix(_st);
                    for (int k = 0; k < _n; k++) if (_fixed[k]) _st.M[k * _n + k] = 1.0;
                    Refactorisations++;
                    bool ok = _lu.Factor(_st.M);
                    _dirty = false;
                    if (!ok)
                    {
                        Status = NodalStatus.SingularMatrix; FailedAt = t;
                        Message = FormattableString.Invariant($"the nodal matrix is singular at t = {t:R} s (step {s}): a node is floating or the network cannot be solved");
                        return;
                    }
                }
                Array.Clear(_st.Rhs);
                foreach (NodalElement e in _els) e.StampSources(t, _st);
                Array.Copy(_st.Rhs, _rhs0, _n);
                var x = (double[])_rhs0.Clone();
                _lu.Solve(x);
                Array.Copy(x, _v, _n);
                foreach (NodalElement e in _els) e.Accept(t, _v);
                Done = s;
                if (s % options.SampleEvery == 0 || s == steps) Record(t, s);
            }
        }
    }
}
