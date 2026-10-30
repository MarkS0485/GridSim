using System.Numerics;

namespace GridSim.Core.Distribution;

/// <summary>Terminal outcome of a three-phase sweep, mirroring the positive-sequence status doctrine.</summary>
public enum MultiPhaseStatus
{
    /// <summary>Met tolerance with finite voltages.</summary>
    Converged,
    /// <summary>Hit the iteration cap.</summary>
    MaxIterations,
    /// <summary>A NaN/Inf appeared - the solution is invalid. This includes a phase that collapsed to exactly zero voltage
    /// under a constant-power or constant-current load (GR-575).</summary>
    NonFinite,
    /// <summary>The network is not a single radial tree from the source.</summary>
    NonRadial,
}

/// <summary>The solved unbalanced state: per-(bus, phase) complex voltage, and convergence metadata.</summary>
public sealed class MultiPhaseResult
{
    /// <summary>Terminal status; <see cref="Converged"/> is true only for <see cref="MultiPhaseStatus.Converged"/>.</summary>
    public required MultiPhaseStatus Status { get; init; }
    /// <summary>True only when converged.</summary>
    public required bool Converged { get; init; }
    /// <summary>Sweeps performed.</summary>
    public required int Iterations { get; init; }
    /// <summary>Final max per-phase voltage change (per-unit).</summary>
    public required double MaxDelta { get; init; }
    /// <summary>Bus ids, aligned with <see cref="V"/>.</summary>
    public required IReadOnlyList<int> BusIds { get; init; }
    /// <summary>Per-bus complex phase voltages (each length 3, per-unit). A phase the bus does not have (a single- or
    /// two-phase lateral, see <see cref="BusPhases"/>) is exactly zero.</summary>
    public required Complex[][] V { get; init; }

    /// <summary>The phases each bus has, aligned with <see cref="BusIds"/> (N062, October 2026): the source has all
    /// three, every other bus the phases of the branch that feeds it. Null only on a result that never built the tree
    /// (<see cref="MultiPhaseStatus.NonRadial"/>). The unbalance and positive-sequence views are meaningful only on a
    /// three-phase bus.</summary>
    public IReadOnlyList<Phase>? BusPhases { get; init; }

    /// <summary>The series-section current of each branch (per-unit, each length 3), aligned with
    /// <see cref="MultiPhaseModel.Branches"/> (N062): the current through the branch's series impedance toward its To
    /// bus, after any regulator, whichever end of the branch faces the source (GR-108: a branch written with its From end
    /// downstream used to report the parent-to-child current, the opposite sign). Null on a result that never built the
    /// tree.</summary>
    public Complex[][]? BranchCurrents { get; init; }

    /// <summary>Per-phase voltage magnitude (per-unit) at the bus at index <paramref name="busIndex"/>.</summary>
    public double[] Vm(int busIndex)
    {
        Complex[] v = V[busIndex];
        return new[] { v[0].Magnitude, v[1].Magnitude, v[2].Magnitude };
    }

    /// <summary>The voltage unbalance factor at a bus - the ratio of negative- to positive-sequence voltage
    /// magnitude, as a percentage (the standard NEMA/IEC metric; 0 for a perfectly balanced bus). High values
    /// stress motors and inverters, so it is a headline distribution quality metric. A dead bus (zero positive
    /// sequence) has no unbalance factor and returns NaN, never 0 (GR-601: 0 would read as perfectly balanced).</summary>
    public double VoltageUnbalancePercent(int busIndex)
    {
        Complex[] seq = SymmetricalComponents.ToSequence(V[busIndex]);
        double pos = seq[1].Magnitude;
        // !(pos > 0) also catches a NaN positive sequence from a non-finite solution.
        return !(pos > 0) ? double.NaN : 100.0 * seq[2].Magnitude / pos;
    }

    /// <summary>Collapse the unbalanced solution onto its positive-sequence single-line equivalent (per-bus
    /// magnitude and angle), so positive-sequence tooling (export, contingency, UI) can consume a distribution
    /// case at reduced fidelity. For a balanced solution this is exactly the phase-A voltage.</summary>
    public (double[] Vm, double[] VaDeg) PositiveSequence()
    {
        int n = V.Length;
        var vm = new double[n];
        var vaDeg = new double[n];
        for (int i = 0; i < n; i++)
        {
            Complex v1 = SymmetricalComponents.PositiveSequence(V[i]);
            vm[i] = v1.Magnitude;
            vaDeg[i] = DetMath.Phase(v1) * 180.0 / Math.PI;
        }
        return (vm, vaDeg);
    }
}

/// <summary>The three-phase power-flow solver seam (parallel to the positive-sequence IPowerFlowSolver),
/// so alternative unbalanced solvers can slot in behind one contract.</summary>
public interface IMultiPhaseSolver
{
    /// <summary>Solve the unbalanced network for its per-phase voltages.</summary>
    MultiPhaseResult Solve(MultiPhaseModel model, ThreePhaseSweepOptions? options = null);
}

/// <summary>Knobs for the three-phase sweep.</summary>
public sealed record ThreePhaseSweepOptions
{
    /// <summary>Convergence threshold on the max per-phase voltage change (per-unit).</summary>
    public double Tolerance { get; init; } = 1e-10;
    /// <summary>Sweep cap.</summary>
    public int MaxIterations { get; init; } = 200;

    /// <summary>GR-598: a tolerance that is not a finite number above 0 (+Infinity "converged" after one sweep) or a cap
    /// below 1 (which returned the flat start) is refused.</summary>
    public void Validate()
    {
        if (!(Tolerance > 0) || !double.IsFinite(Tolerance))
            throw new ArgumentOutOfRangeException(nameof(Tolerance), Tolerance, "The sweep tolerance must be a finite number above 0.");
        if (MaxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxIterations), MaxIterations, "The sweep needs at least one iteration.");
    }
}

/// <summary>
/// A three-phase backward/forward-sweep power flow for radial distribution networks - the matricial
/// generalisation of <c>PowerFlow/BackwardForwardSweep.cs</c>. Backward sweep accumulates per-phase branch
/// currents from the constant-power wye loads (I = conj(S/V) per phase); forward sweep drops voltage across
/// each 3x3 series impedance (V_child = V_parent - Z * I_branch). On a balanced diagonal model it reduces to
/// three decoupled positive-sequence sweeps - the non-regression bridge to the transmission engine.
///
/// <para>N062 (October 2026) elements, each added only where the model has it, so a v1 model is solved with exactly the
/// arithmetic it always was: single- and two-phase laterals (only the phases a bus has are solved; the others are
/// exactly zero); line charging as a pi section (half the shunt admittance at each end, a constant-admittance node
/// current); ideal step-voltage regulators at a branch's From end (output voltage ratio times input, input current
/// ratio times output); constant-current and constant-impedance loads, wye or delta (defined at nominal voltage); and
/// wye shunt capacitors (constant susceptance). A closed switch is a branch with zero series impedance. Validated
/// against the IEEE 123 Node Test Feeder's published results (docs/reference S289; see MultiPhaseIeee123Tests).</para>
/// </summary>
public sealed class ThreePhaseSweep : IMultiPhaseSolver
{
    /// <summary>Solve the radial three-phase network.</summary>
    public MultiPhaseResult Solve(MultiPhaseModel model, ThreePhaseSweepOptions? options = null)
    {
        ThreePhaseSweepOptions opt = options ?? new ThreePhaseSweepOptions();
        opt.Validate();   // GR-598
        int n = model.Buses.Count;

        var idToIndex = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idToIndex[model.Buses[i].Id] = i;
        int source = -1;
        for (int i = 0; i < n; i++) if (model.Buses[i].IsSource) { source = i; break; }

        // Build the radial tree by BFS from the source; each non-source bus gets exactly one parent branch.
        var parentBus = new int[n];
        var parentBranch = new int[n];
        var childBranches = new List<int>[n];
        for (int i = 0; i < n; i++) { parentBus[i] = -1; parentBranch[i] = -1; childBranches[i] = new List<int>(); }
        var adjacency = new List<(int Other, int Branch)>[n];
        for (int i = 0; i < n; i++) adjacency[i] = new List<(int, int)>();
        for (int b = 0; b < model.Branches.Count; b++)
        {
            PhaseBranch br = model.Branches[b];
            if (!idToIndex.TryGetValue(br.From, out int f) || !idToIndex.TryGetValue(br.To, out int t))
                return NonRadial(model, n);
            adjacency[f].Add((t, b));
            adjacency[t].Add((f, b));
        }

        var order = new List<int>(n);      // BFS order from source (parents before children)
        var visited = new bool[n];
        var queue = new Queue<int>();
        queue.Enqueue(source);
        visited[source] = true;
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            order.Add(u);
            foreach ((int other, int branch) in adjacency[u])
            {
                if (visited[other]) continue;
                visited[other] = true;
                parentBus[other] = u;
                parentBranch[other] = branch;
                childBranches[u].Add(branch);
                queue.Enqueue(other);
            }
        }
        // Radial + connected: every bus visited, AND exactly n-1 tree branches. The visited half
        // alone lets a meshed network (a loop-closing branch, or parallel branches between the same
        // pair) pass: the extra branch is simply never chosen as a parentBranch and is silently
        // dropped from both sweeps, so the solver would converge on a fictitious spanning tree.
        for (int i = 0; i < n; i++) if (!visited[i]) return NonRadial(model, n);
        if (model.Branches.Count != n - 1) return NonRadial(model, n);

        // N062: the phases of each bus (the source has all three; every other bus has the phases of the branch that feeds
        // it), checked against the tree: a branch cannot carry a phase its upstream bus does not have, and a regulator
        // must face away from the source (it sits at its branch's From end).
        var busPhases = new Phase[n];
        busPhases[source] = Phase.Abc;
        // 1.95.253 (N168): the buses fed through a delta secondary (wye / delta, delta / delta) form a THREE-WIRE system
        // until a delta / grounded-wye transformer grounds it again. No element there may draw current to neutral or earth.
        var threeWire = new bool[n];
        foreach (int bus in order)
        {
            if (bus == source) continue;
            PhaseBranch br = model.Branches[parentBranch[bus]];
            int flags = br.TransformerFlagCount;   // N334 (CF5): the open banks count too
            if (br.HasDeltaSecondary && (flags > 1 || br.Phases != Phase.Abc || br.ShuntY is not null || br.RegulatorRatio is not null))
                throw new ArgumentException(
                    $"three-phase sweep: the transformer on branch {br.From}-{br.To} must be a plain three-phase branch (no shunt, no regulator) of exactly one connection.");
            if (br.HasDeltaSecondary && idToIndex[br.From] != parentBus[bus])
                throw new ArgumentException(
                    $"three-phase sweep: the transformer on branch {br.From}-{br.To} faces the source (its From side, bus {br.From}, is downstream); " +
                    "its From side must be the one nearer the source.");
            // GR-585: in Core an off-nominal gy-gy transformer is its ideal ratio (RegulatorRatio) and its leakage impedance,
            // so this branch cannot tell it from a wye regulator; the message names both, so a gy-gy unit is not refused
            // under the regulator's name alone. (A rated gy-gy unit is a plain series impedance and cannot be seen at all:
            // the loader refuses it, technical ch. 36.)
            if (threeWire[parentBus[bus]] && !br.IsTransformerElement && (br.ShuntY is not null || br.RegulatorRatio is not null))
                throw new ArgumentException(
                    $"three-phase sweep: branch {br.From}-{br.To} is on a three-wire system (fed through a delta secondary) and has " +
                    $"{(br.ShuntY is not null ? "line charging (a path to earth)" : "a wye step-voltage regulator or off-nominal grounded-wye (gy-gy) transformer ratio, both earthed at the neutral")}, which a three-wire system cannot hold.");
            // N334 (CF5): an open wye / open delta bank's primaries are phase-to-earth units on phases A and B: its From
            // bus needs phases a and b (c is not used) and an earthed system (not a three-wire one).
            if (br.OpenWyeOpenDelta)
            {
                if (threeWire[parentBus[bus]])
                    throw new ArgumentException(
                        $"three-phase sweep: the open wye / open delta bank on branch {br.From}-{br.To} is fed from a three-wire system (downstream of a delta secondary), which has no earth for its phase-to-earth primaries.");
                if ((busPhases[parentBus[bus]] & (Phase.A | Phase.B)) != (Phase.A | Phase.B))
                    throw new ArgumentException(
                        $"three-phase sweep: the open wye / open delta bank on branch {br.From}-{br.To} needs phases a and b on bus " +
                        $"{model.Buses[parentBus[bus]].Id}, which has only {PhaseIndex.Name(busPhases[parentBus[bus]])}.");
            }
            threeWire[bus] = br.HasDeltaSecondary || (threeWire[parentBus[bus]] && !br.IsDeltaWye);
            if (!br.OpenWyeOpenDelta && (br.Phases & ~busPhases[parentBus[bus]]) != 0)
                throw new ArgumentException(
                    $"three-phase sweep: branch {br.From}-{br.To} carries phase(s) {PhaseIndex.Name(br.Phases)} but bus " +
                    $"{model.Buses[parentBus[bus]].Id}, which feeds it, has only {PhaseIndex.Name(busPhases[parentBus[bus]])}.");
            if (br.IsDeltaWye && (br.DeltaWyeStepDown && br.DeltaWyeStepUp || br.Phases != Phase.Abc || br.ShuntY is not null || br.RegulatorRatio is not null))
                throw new ArgumentException(
                    $"three-phase sweep: the delta / grounded-wye transformer on branch {br.From}-{br.To} must be a plain three-phase branch (no shunt, no regulator) and either step-down or step-up, not both.");
            if (br.IsDeltaWye && idToIndex[br.From] != parentBus[bus])
                throw new ArgumentException(
                    $"three-phase sweep: the delta / grounded-wye transformer on branch {br.From}-{br.To} faces the source (its delta side, bus {br.From}, " +
                    "is downstream); its delta (From) side must be the one nearer the source.");
            if (br.RegulatorRatio is not null && idToIndex[br.From] != parentBus[bus])
                throw new ArgumentException(
                    $"three-phase sweep: the regulator on branch {br.From}-{br.To} faces the source (its From end, bus {br.From}, " +
                    "is downstream); a regulator's From bus must be the one nearer the source.");
            busPhases[bus] = br.Phases;
        }

        // Per-bus load S (summed if multiple loads share a bus) of the v1 constant-power wye loads, exactly as before.
        // N062: every other load (constant-current, constant-impedance, delta) is kept per bus and evaluated each sweep.
        var loadS = new Complex[n][];
        for (int i = 0; i < n; i++) loadS[i] = new Complex[3];
        var extendedLoads = new List<PhaseLoad>?[n];
        var capB = new double[n][];   // capacitor susceptance per phase (per-unit), null where a bus has none
        foreach (PhaseLoad load in model.Loads)
        {
            // MultiPhaseModel's constructor guarantees the bus exists (R235); index it directly so a broken
            // invariant would throw here rather than drop the load.
            int bi = idToIndex[load.Bus];
            RequireLoadPhases(load, busPhases[bi]);
            if (!load.IsExtended)
            {
                for (int p = 0; p < 3; p++) loadS[bi][p] += load.SPerPhase[p];
            }
            else (extendedLoads[bi] ??= new List<PhaseLoad>()).Add(load);
        }
        // 1.95.253 (N168): on a three-wire system a wye load and a (wye) capacitor have no neutral to return to.
        foreach (PhaseLoad load in model.Loads)
            if (threeWire[idToIndex[load.Bus]] && load.Connection == PhaseLoadConnection.Wye && load.SPerPhase.Any(x => x != Complex.Zero))
                throw new ArgumentException(
                    $"three-phase sweep: the wye load at bus {load.Bus} is on a three-wire system (fed through a delta secondary), which has no neutral; connect it in delta.");
        foreach (PhaseCapacitor cap in model.Capacitors)
            if (threeWire[idToIndex[cap.Bus]])
                throw new ArgumentException(
                    $"three-phase sweep: the capacitor at bus {cap.Bus} is on a three-wire system (fed through a delta secondary); a wye capacitor bank needs a neutral.");
        foreach (PhaseCapacitor cap in model.Capacitors)
        {
            int bi = idToIndex[cap.Bus];
            for (int p = 0; p < 3; p++)
                if (cap.QPerPhase[p] != 0 && !PhaseIndex.Has(busPhases[bi], p))
                    throw new ArgumentException($"three-phase sweep: a capacitor at bus {cap.Bus} has a unit on phase {"abc"[p]}, which the bus does not have.");
            capB[bi] ??= new double[3];
            for (int p = 0; p < 3; p++) capB[bi][p] += cap.QPerPhase[p];
        }

        // Half of each branch's shunt admittance (the pi model), and whether a branch needs the extended arithmetic.
        var halfShunt = new PhaseImpedance?[model.Branches.Count];
        for (int b = 0; b < model.Branches.Count; b++)
            if (model.Branches[b].ShuntY is { } y) halfShunt[b] = Scale(y, 0.5);

        // Initialise all voltages to the source voltage (balanced flat start); a phase a bus does not have is zero.
        Complex[] sourceV = model.Buses[source].SourceV ?? MultiPhaseModel.BalancedNominal();
        var v = new Complex[n][];
        for (int i = 0; i < n; i++)
        {
            v[i] = (Complex[])sourceV.Clone();
            for (int p = 0; p < 3; p++) if (!PhaseIndex.Has(busPhases[i], p)) v[i][p] = Complex.Zero;
        }

        var iBranch = new Complex[model.Branches.Count][];
        for (int b = 0; b < iBranch.Length; b++) iBranch[b] = new Complex[3];

        MultiPhaseStatus status = MultiPhaseStatus.MaxIterations;
        int iter = 0;
        double maxDelta = double.PositiveInfinity;
        for (; iter < opt.MaxIterations; iter++)
        {
            // Backward sweep: node current I = conj(S/V) per phase, then accumulate up the tree.
            for (int b = 0; b < iBranch.Length; b++) { iBranch[b][0] = iBranch[b][1] = iBranch[b][2] = Complex.Zero; }
            for (int oi = order.Count - 1; oi >= 0; oi--)
            {
                int bus = order[oi];
                if (bus == source) continue;
                var acc = new Complex[3];
                for (int p = 0; p < 3; p++)
                    // GR-575: a constant-power load at exactly zero voltage has no defined current (S / 0). It used to
                    // draw nothing, so a collapsed phase read as a lightly loaded one; it now injects NaN, which the forward
                    // sweep carries into the voltages and the run stops NonFinite. A phase with no load still draws 0.
                    acc[p] = loadS[bus][p] == Complex.Zero ? Complex.Zero
                           : v[bus][p] == Complex.Zero ? new Complex(double.NaN, double.NaN)
                           : Complex.Conjugate(loadS[bus][p] / v[bus][p]);

                // N062 node currents, added only where the elements exist (a v1 model takes none of these branches, so
                // its arithmetic is exactly what it was).
                if (extendedLoads[bus] is { } ext) foreach (PhaseLoad load in ext) AddLoadCurrent(load, v[bus], acc);
                if (capB[bus] is { } bc)
                    for (int p = 0; p < 3; p++) if (bc[p] != 0) acc[p] += new Complex(0.0, bc[p]) * v[bus][p];
                // The To-end half of the feeding branch's shunt draws its current through that branch's series impedance.
                if (halfShunt[parentBranch[bus]] is { } yTo)
                {
                    Complex[] ish = yTo.Multiply(v[bus]);
                    for (int p = 0; p < 3; p++) acc[p] += ish[p];
                }

                foreach (int cb in childBranches[bus])
                {
                    PhaseBranch child = model.Branches[cb];
                    if (child.IsTransformerElement)
                    {
                        // 1.95.251-1.95.253 (N168): a transformer element draws the From-side line currents of its To-side
                        // series current (PhaseBranch.TransformerInput; for delta / grounded wye exactly DeltaWyeInput).
                        Complex[] iHv = child.TransformerInput(iBranch[cb]);
                        for (int p = 0; p < 3; p++) acc[p] += iHv[p];
                        continue;
                    }
                    if (child.RegulatorRatio is null && halfShunt[cb] is null)
                    {
                        for (int p = 0; p < 3; p++) acc[p] += iBranch[cb][p];   // the v1 line, unchanged
                        continue;
                    }
                    // N062: what the child branch draws from this bus. After the regulator (if any) it feeds its series
                    // section and the From-end half of its shunt, both at the regulator's output voltage; the ideal
                    // regulator then draws ratio times that current from this bus.
                    var outV = RegulatorOutput(child, v[bus]);
                    var drawn = (Complex[])iBranch[cb].Clone();
                    if (halfShunt[cb] is { } yFrom)
                    {
                        Complex[] ish = yFrom.Multiply(outV);
                        for (int p = 0; p < 3; p++) drawn[p] += ish[p];
                    }
                    for (int p = 0; p < 3; p++)
                        acc[p] += child.RegulatorRatio is { } a ? drawn[p] * a[p] : drawn[p];
                }
                Complex[] pb = iBranch[parentBranch[bus]];
                for (int p = 0; p < 3; p++) pb[p] = acc[p];
            }

            // Forward sweep: V_child = V_parent - Z * I_branch (N062: V_parent after the regulator, when there is one;
            // only the phases the branch carries; the others stay exactly zero).
            maxDelta = 0;
            bool finite = true;
            for (int oi = 0; oi < order.Count; oi++)
            {
                int bus = order[oi];
                if (bus == source) continue;
                int pbIdx = parentBranch[bus];
                PhaseBranch br = model.Branches[pbIdx];
                Complex[] drop = br.Z.Multiply(iBranch[pbIdx]);
                // 1.95.251-1.95.253 (N168): behind a transformer element the series drop starts from its To-side
                // open-circuit voltage (PhaseBranch.TransformerOutput; for delta / grounded wye exactly DeltaWyeOutput).
                Complex[] vIn = br.IsTransformerElement ? br.TransformerOutput(v[parentBus[bus]])
                    : br.RegulatorRatio is null ? v[parentBus[bus]] : RegulatorOutput(br, v[parentBus[bus]]);
                for (int p = 0; p < 3; p++)
                {
                    if (!PhaseIndex.Has(br.Phases, p)) continue;
                    Complex nv = vIn[p] - drop[p];
                    double d = (nv - v[bus][p]).Magnitude;
                    if (d > maxDelta) maxDelta = d;
                    finite &= double.IsFinite(nv.Real) && double.IsFinite(nv.Imaginary);
                    v[bus][p] = nv;
                }
            }

            if (!finite) { status = MultiPhaseStatus.NonFinite; break; }
            if (maxDelta < opt.Tolerance) { status = MultiPhaseStatus.Converged; iter++; break; }
        }

        var busIds = new int[n];
        for (int i = 0; i < n; i++) busIds[i] = model.Buses[i].Id;

        // GR-108: the sweep accumulates each branch's series current in the parent-to-child direction (what the child
        // subtree draws). The published BranchCurrents promise the current toward the branch's To bus, so a branch written
        // with its From end downstream (only a line or a switch can be: transformers and regulators are refused facing
        // the source above) is published negated. Negation is exact, and a branch written From the source side (every
        // shipped feeder) is the very same array as before.
        var published = new Complex[iBranch.Length][];
        for (int b = 0; b < iBranch.Length; b++) published[b] = iBranch[b];
        foreach (int bus in order)
        {
            if (bus == source) continue;
            int b = parentBranch[bus];
            if (idToIndex[model.Branches[b].From] == parentBus[bus]) continue;   // From is upstream: already toward To
            published[b] = new[] { -iBranch[b][0], -iBranch[b][1], -iBranch[b][2] };
        }

        return new MultiPhaseResult
        {
            Status = status,
            Converged = status == MultiPhaseStatus.Converged,
            Iterations = iter,
            MaxDelta = maxDelta,
            BusIds = busIds,
            V = v,
            BusPhases = busPhases,
            BranchCurrents = published,
        };
    }

    // The regulator's output voltage on each phase (ratio times the From bus voltage), or the bus voltage itself.
    private static Complex[] RegulatorOutput(PhaseBranch br, Complex[] vFrom)
    {
        if (br.RegulatorRatio is not { } a) return vFrom;
        return new[] { vFrom[0] * a[0], vFrom[1] * a[1], vFrom[2] * a[2] };
    }

    private static PhaseImpedance Scale(PhaseImpedance m, double k)
    {
        var r = new Complex[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i, j] = m[i, j] * k;
        return new PhaseImpedance(r);
    }

    // A load may draw only on phases its bus has: a wye load on each phase with a non-zero S, a delta load on both
    // phases of each pair with a non-zero S. Anything else would be silently unsupplied, so it is refused.
    private static void RequireLoadPhases(PhaseLoad load, Phase busPhases)
    {
        for (int p = 0; p < 3; p++)
        {
            if (load.SPerPhase[p] == Complex.Zero) continue;
            bool ok = load.Connection == PhaseLoadConnection.Wye
                ? PhaseIndex.Has(busPhases, p)
                : PhaseIndex.Has(busPhases, p) && PhaseIndex.Has(busPhases, (p + 1) % 3);
            if (!ok)
            {
                string where = load.Connection == PhaseLoadConnection.Wye ? $"phase {"abc"[p]}" : $"phases {"abc"[p]}-{"abc"[(p + 1) % 3]}";
                throw new ArgumentException($"three-phase sweep: a load at bus {load.Bus} draws on {where}, which the bus " +
                                            $"(phases {PhaseIndex.Name(busPhases)}) does not have.");
            }
        }
    }

    // The nominal voltage magnitudes (per-unit of the line-to-neutral base) the constant-current and constant-impedance
    // loads are defined at: 1 pu line to neutral for wye, sqrt(3) pu line to line for delta.
    private static readonly double DeltaNominal = Math.Sqrt(3.0);

    // The line currents a non-v1 load draws at the bus voltages v, added into acc (N062). See PhaseLoadModel and
    // PhaseLoadConnection for the definitions.
    private static void AddLoadCurrent(PhaseLoad load, Complex[] v, Complex[] acc)
    {
        if (load.Connection == PhaseLoadConnection.Wye)
        {
            for (int p = 0; p < 3; p++)
                acc[p] += ElementCurrent(load.Model, load.SPerPhase[p], v[p], 1.0);
            return;
        }
        // Delta: branch currents on a-b, b-c, c-a at the line-to-line voltages, then the line currents by KCL.
        var vll = new[] { v[0] - v[1], v[1] - v[2], v[2] - v[0] };
        Complex iab = ElementCurrent(load.Model, load.SPerPhase[0], vll[0], DeltaNominal);
        Complex ibc = ElementCurrent(load.Model, load.SPerPhase[1], vll[1], DeltaNominal);
        Complex ica = ElementCurrent(load.Model, load.SPerPhase[2], vll[2], DeltaNominal);
        acc[0] += iab - ica;
        acc[1] += ibc - iab;
        acc[2] += ica - ibc;
    }

    // The current one load element (S at nominal voltage vNom) draws at voltage u.
    private static Complex ElementCurrent(PhaseLoadModel model, Complex s, Complex u, double vNom)
    {
        if (s == Complex.Zero) return Complex.Zero;
        // GR-575: a constant-power or constant-current element at exactly zero voltage has no defined current; NaN
        // stops the sweep NonFinite instead of the element silently drawing nothing. A constant impedance draws 0 there.
        if (u == Complex.Zero && model != PhaseLoadModel.ConstantImpedance) return new Complex(double.NaN, double.NaN);
        return model switch
        {
            PhaseLoadModel.ConstantPower => Complex.Conjugate(s / u),
            // |I| = |S| / |V_nom|, at the load's power-factor angle behind its own voltage.
            PhaseLoadModel.ConstantCurrent => DetMath.FromPolar(s.Magnitude / vNom, DetMath.Phase(u) - DetMath.Phase(s)),
            // Z = |V_nom|^2 / conj(S), so I = u / Z = u conj(S) / |V_nom|^2.
            PhaseLoadModel.ConstantImpedance => u * Complex.Conjugate(s) / (vNom * vNom),
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "unknown load model."),
        };
    }
    private static MultiPhaseResult NonRadial(MultiPhaseModel model, int n)
    {
        var ids = new int[n];
        var v = new Complex[n][];
        for (int i = 0; i < n; i++) { ids[i] = model.Buses[i].Id; v[i] = new Complex[3]; }
        return new MultiPhaseResult
        {
            Status = MultiPhaseStatus.NonRadial,
            Converged = false,
            Iterations = 0,
            MaxDelta = double.NaN,
            BusIds = ids,
            V = v,
        };
    }
}
