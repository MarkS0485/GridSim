using GridSim.Core.Diagnostics;

namespace GridSim.Core.Dynamics.Emt.Nodal;

/// <summary>
/// The fixed-step nodal EMT solver of RC4 R4-16. Order of work per step (the plan's order):
/// <list type="number">
/// <item>apply any switch events due at this step boundary (rule: <see cref="NodalEventRule"/>);</item>
/// <item>history terms and source values at <c>t + dt</c>, in element order;</item>
/// <item>if the network has nonlinear elements, Newton iterations with a fixed iteration limit and a residual test
/// (the step is halved, up to a fixed count, when it would raise the residual); otherwise one linear solve;</item>
/// <item>accept: update every element's history, in element order.</item>
/// </list>
/// The state at t = 0 is REST (zero node voltages, zero branch currents, capacitor and flux initial values as given). A
/// source whose EMF is not zero at t = 0 therefore switches on at the first step (a half-step lag in the response with
/// the trapezoidal rule); the validated cases use sources that start at zero. Nothing here uses threads and every sum
/// has a fixed order, so a run is bit-identical on every machine and at every thread count.
/// </summary>
public static class NodalSolver
{
    /// <summary>Run <paramref name="net"/> for <paramref name="duration"/> seconds in steps of <paramref name="dt"/>.</summary>
    /// <exception cref="ArgumentException">The step, the duration, a probe, an event or an element is unusable (a domain refusal).</exception>
    public static NodalResult Run(NodalNetwork net, double dt, double duration, IReadOnlyList<NodalProbe> probes,
        IReadOnlyList<NodalEvent>? events = null, NodalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(probes);
        options ??= new NodalOptions();
        static Exception Refuse(string m) => DomainRefusal.Mark(new ArgumentException(m));
        if (!(dt > 0 && double.IsFinite(dt))) throw Refuse("The time step must be finite and positive.");
        if (!(duration > 0 && double.IsFinite(duration))) throw Refuse("The run length must be finite and positive.");
        if (options.SampleEvery < 1) throw Refuse("SampleEvery must be at least 1.");
        NewtonPolicy np = options.Newton;
        if (np.MaxIterations < 1 || np.MaxBacktracks < 0 || !(np.VoltageAbsTol > 0) || !(np.VoltageRelTol >= 0) || !(np.ResidualAbsTol > 0) || !(np.ResidualRelTol >= 0))
            throw Refuse("The Newton policy needs MaxIterations >= 1, MaxBacktracks >= 0 and positive tolerances.");
        double stepsD = Math.Ceiling(duration / dt - 1e-9);
        if (stepsD > 200_000_000.0) throw Refuse("The run would take more than 2e8 steps.");
        long steps = (long)stepsD;
        int n = net.NodeCount;
        if (n == 0) throw Refuse("The network has no node.");
        IReadOnlyList<NodalElement> els = net.Elements;

        var fixedNode = new bool[n];
        foreach (NodalElement e in els)
        {
            int fx = e.FixedNode;
            if (fx >= 0)
            {
                if (fixedNode[fx]) throw Refuse($"Node '{net.NodeNames[fx]}' has two ideal voltage sources.");
                fixedNode[fx] = true;
            }
            try { e.Validate(dt, n); }
            catch (ArgumentException ex) { throw DomainRefusal.Mark(new ArgumentException($"{e.Name}: {ex.Message}")); }
        }
        var nl = new List<NodalElement>();
        foreach (NodalElement e in els) if (e.IsNonlinear) nl.Add(e);
        // RC4 R4-17 additive hooks: self-switching devices and matrix-varying machines (empty lists for every R4-16 network).
        var logic = new List<NodalElement>();
        var varying = new List<NodalElement>();
        foreach (NodalElement e in els) { if (e.HasSwitchLogic) logic.Add(e); if (e.IsMatrixVarying) varying.Add(e); }

        // Resolve probes and events.
        var probeNodes = new int[probes.Count];
        var probeEls = new NodalElement?[probes.Count];
        for (int k = 0; k < probes.Count; k++)
        {
            NodalProbe p = probes[k];
            if (p.Kind == NodalProbeKind.NodeVoltage)
            {
                int id = net.FindNode(p.Target);
                if (id == -2) throw Refuse($"Probe '{p.Name}': no node '{p.Target}'.");
                probeNodes[k] = id;
            }
            else
            {
                NodalElement? e = net.Find(p.Target) ?? throw Refuse($"Probe '{p.Name}': no element '{p.Target}'.");
                if (p.Kind == NodalProbeKind.ElementCurrent && (p.Channel < 0 || p.Channel >= e.CurrentChannels))
                    throw Refuse($"Probe '{p.Name}': element '{p.Target}' has no current channel {p.Channel}.");
                probeEls[k] = e;
            }
        }
        var pending = new List<(double T, NodalSwitch Sw, bool Close)>();
        if (events is not null)
            foreach (NodalEvent ev in events)
            {
                if (!(double.IsFinite(ev.AtSeconds) && ev.AtSeconds >= 0)) throw Refuse("An event time must be finite and not negative.");
                if (net.Find(ev.SwitchName) is not NodalSwitch sw) throw Refuse($"Event: no switch '{ev.SwitchName}'.");
                pending.Add((ev.AtSeconds, sw, ev.Close));
            }
        // Stable order by time, ties in the order given.
        var order = Enumerable.Range(0, pending.Count).OrderBy(i => pending[i].T).ThenBy(i => i).ToArray();
        var queue = order.Select(i => pending[i]).ToList();
        int next = 0;

        foreach (NodalElement e in els) e.Reset();
        IntegrationRule baseRule = options.Rule;
        IntegrationRule configured = baseRule;
        foreach (NodalElement e in els) e.Configure(dt, configured);

        var st = new NodalStamper(n, fixedNode);       // base (linear) matrix and sources
        var nst = new NodalStamper(n, fixedNode);      // Newton iteration copy
        var lu = new DenseLu(n);
        var luNewton = new DenseLu(n);
        var v = new double[n];
        var rhs0 = new double[n];
        int refactorisations = 0;
        long newtonIters = 0;
        int maxNewton = 0;
        bool dirty = true;
        bool singular = false;

        long samples = steps / options.SampleEvery + 2;
        var times = new List<double>((int)Math.Min(samples, 1_000_000));
        var series = new List<double>[probes.Count];
        for (int k = 0; k < series.Length; k++) series[k] = new List<double>();
        void Record(double t)
        {
            times.Add(t);
            for (int k = 0; k < probes.Count; k++)
            {
                double x = probes[k].Kind switch
                {
                    NodalProbeKind.NodeVoltage => probeNodes[k] < 0 ? 0.0 : v[probeNodes[k]],
                    NodalProbeKind.ElementCurrent => probeEls[k]!.ProbeCurrent(probes[k].Channel),
                    _ => probeEls[k]!.ProbeStateAt(probes[k].Channel),
                };
                series[k].Add(x);
            }
        }
        Record(0.0);

        NodalStatus status = NodalStatus.Completed;
        string message = "completed";
        double failedAt = double.NaN;
        long done = 0;

        for (long s = 1; s <= steps; s++)
        {
            double t = s * dt;
            bool eventNow = false;
            while (next < queue.Count && t >= queue[next].T - EmtConstants.EventTimeToleranceFraction * dt)
            {
                var (_, sw, close) = queue[next++];
                if (sw.Closed != close) { sw.Closed = close; eventNow = true; dirty = true; }
            }
            foreach (NodalElement e in logic) if (e.SwitchLogic(t)) { eventNow = true; dirty = true; }
            IntegrationRule rule = eventNow && options.EventRule == NodalEventRule.BackwardEulerStep ? IntegrationRule.BackwardEuler : baseRule;
            if (rule != configured)
            {
                configured = rule;
                foreach (NodalElement e in els) e.Configure(dt, configured);
                dirty = true;
            }
            if (varying.Count > 0)
            {
                foreach (NodalElement e in varying) e.PrepareStep(t, dt);
                dirty = true;
            }
            if (dirty)
            {
                st.Clear();
                foreach (NodalElement e in els) e.StampMatrix(st);
                for (int k = 0; k < n; k++) if (fixedNode[k]) st.M[k * n + k] = 1.0;
                refactorisations++;
                if (nl.Count == 0 && !lu.Factor(st.M)) { singular = true; }
                dirty = false;
            }
            if (singular)
            {
                status = NodalStatus.SingularMatrix; failedAt = t;
                message = $"the nodal matrix is singular at t = {t:R} s (step {s}): a node is floating or the network cannot be solved";
                break;
            }

            // History terms and source values, in element order. The matrix part of the stamper is not touched here.
            Array.Clear(st.Rhs);
            foreach (NodalElement e in els) e.StampSources(t, st);
            Array.Copy(st.Rhs, rhs0, n);

            if (nl.Count == 0)
            {
                var x = (double[])rhs0.Clone();
                lu.Solve(x);
                Array.Copy(x, v, n);
            }
            else
            {
                (NodalStatus ns, string nm, int iters) = Newton(nl, st, nst, luNewton, rhs0, v, np, fixedNode, t, s);
                newtonIters += iters;
                maxNewton = Math.Max(maxNewton, iters);
                if (ns != NodalStatus.Completed)
                {
                    status = ns; failedAt = t; message = nm;
                    break;
                }
            }
            foreach (NodalElement e in els) e.Accept(t, v);
            done = s;
            if (s % options.SampleEvery == 0 || s == steps) Record(t);
        }

        string method = (baseRule == IntegrationRule.Trapezoidal ? "trapezoidal" : "backward-euler")
            + (options.EventRule == NodalEventRule.BackwardEulerStep ? " with one backward-Euler step at each switching event" : "");
        double[][] outSeries = series.Select(l => l.ToArray()).ToArray();
        return new NodalResult(dt, method, status, message, failedAt, probes.Select(p => p.Name).ToArray(),
            times.ToArray(), outSeries, new NodalRunStats(done, refactorisations, newtonIters, maxNewton), np);
    }

    private static (NodalStatus, string, int) Newton(List<NodalElement> nl, NodalStamper st, NodalStamper nst, DenseLu lu,
        double[] rhs0, double[] v, NewtonPolicy np, bool[] fixedNode, double t, long step)
    {
        int n = st.N;
        double rhsScale = 0.0;
        for (int i = 0; i < n; i++) if (!fixedNode[i]) rhsScale = Math.Max(rhsScale, Math.Abs(rhs0[i]));
        // Fixed (ideal-source) nodes take their imposed value at once.
        for (int i = 0; i < n; i++) if (fixedNode[i]) v[i] = rhs0[i];

        var f = new double[n];
        var vt = new double[n];
        var dv = new double[n];
        double Resid(double[] x)
        {
            Array.Clear(f);
            for (int i = 0; i < n; i++)
            {
                if (fixedNode[i]) continue;
                double a = -rhs0[i];
                int row = i * n;
                for (int j = 0; j < n; j++) a += st.M[row + j] * x[j];
                f[i] = a;
            }
            foreach (NodalElement e in nl) e.AddNonlinearCurrent(x, f);
            double m = 0.0;
            for (int i = 0; i < n; i++)
            {
                if (fixedNode[i]) continue;
                double a = Math.Abs(f[i]);
                if (double.IsNaN(a)) return double.PositiveInfinity;
                m = Math.Max(m, a);
            }
            return m;
        }

        double r0 = Resid(v);
        int it = 0;
        while (it < np.MaxIterations)
        {
            it++;
            Array.Copy(st.M, nst.M, n * n);
            Array.Copy(rhs0, nst.Rhs, n);
            foreach (NodalElement e in nl) e.StampNewton(v, nst);
            if (!lu.Factor(nst.M))
                return (NodalStatus.SingularMatrix, $"the Newton iteration matrix is singular at t = {t:R} s (step {step}, iteration {it})", it);
            Array.Copy(nst.Rhs, dv, n);
            lu.Solve(dv);
            for (int i = 0; i < n; i++) dv[i] -= v[i];     // the full Newton step

            double lam = 1.0;
            double rt = double.PositiveInfinity;
            for (int bt = 0; bt <= np.MaxBacktracks; bt++)
            {
                for (int i = 0; i < n; i++) vt[i] = v[i] + lam * dv[i];
                rt = Resid(vt);
                if (rt <= r0) break;
                lam *= 0.5;
            }
            bool small = true;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(lam * dv[i]) > np.VoltageAbsTol + np.VoltageRelTol * Math.Abs(vt[i])) { small = false; break; }
            }
            Array.Copy(vt, v, n);
            r0 = rt;
            double scale = rhsScale;
            foreach (NodalElement e in nl) scale = Math.Max(scale, e.CurrentScale);
            if (small && r0 <= np.ResidualAbsTol + np.ResidualRelTol * scale) return (NodalStatus.Completed, "", it);
        }
        return (NodalStatus.NewtonNotConverged,
            $"the Newton iteration did not converge in {np.MaxIterations} iterations at t = {t:R} s (step {step}); the last residual was {r0:R} A", it);
    }
}
