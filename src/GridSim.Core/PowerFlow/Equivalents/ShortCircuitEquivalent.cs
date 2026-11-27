using System.Globalization;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Core.PowerFlow.Equivalents;

/// <summary>The Thevenin impedances of the external network at one boundary bus, per sequence (pu on the system base):
/// the diagonal of the inverse of the reduced sequence admittance Y_s,BB. Each is the driving-point impedance of the
/// external network alone (the internal network removed, the other boundary buses open). An external network with no path
/// to earth in a sequence has an infinite impedance there.</summary>
public sealed record SequenceTheveninImpedance(int BusId, Complex Z1Pu, Complex Z2Pu, Complex Z0Pu);

/// <summary>The result of <see cref="ShortCircuitEquivalent.Reduce"/>: the reduced fault-study case (with the equivalent
/// branches, sources and zero-sequence data) and the per-boundary-bus Thevenin report.</summary>
public sealed record ShortCircuitEquivalentResult(ReducedNetwork Network, IReadOnlyList<SequenceTheveninImpedance> Thevenin);

/// <summary>
/// RC5 WP31: the short-circuit equivalent of an external network (<c>reduce --method sc</c>). Every sequence network of the
/// IEC 60909 fault studies (<see cref="Iec60909"/>) is Kron-reduced onto the boundary, and the reduced matrices are written
/// back as ordinary case elements, so the existing studies run on the reduced case unchanged and give the same fault
/// currents at every kept bus.
///
/// <para><b>Partition.</b> As <see cref="NetworkReduction"/>: K = the kept buses (given), B = the kept buses with an
/// in-service branch to an external bus, E = the rest. The slack must be in K.</para>
///
/// <para><b>The sequence networks</b> are built by the fault studies' own code on a sub-case holding B, E, the tie and
/// external branches and the external machines (kept buses' shunts, line charging and machines left out):
/// <list type="bullet">
///   <item>positive sequence: the IEC 60909 series network (<see cref="YBus.BuildSparse"/> with line charging and bus
///   shunts removed, taps kept, as <c>FaultNetworkSystem</c> builds it for the duties) plus each external synchronous
///   machine as 1 / j(X''d + XT) on the system base (<see cref="Generator.FaultSourceReactancePu"/>);</item>
///   <item>negative sequence: the same network with each machine's X2. GridSim's case carries one positive- and
///   negative-sequence impedance per branch, so the reduction is refused when an external machine's X2 differs from its
///   X''d (then Y_2 would differ from Y_1); otherwise Y_2 = Y_1 exactly;</item>
///   <item>zero sequence: the network of <c>Iec60909.BuildZeroSequenceNetwork</c> (line Z0, transformer winding
///   connections and neutral reactances, machine zero-sequence impedances, and the mutual couplings between external or
///   tie lines, stamped from the inverse of each coupled group's impedance matrix), so a mutual inside the external
///   network is captured exactly. External buses whose zero-sequence component does not reach the boundary are left out
///   (no zero-sequence current from the boundary can reach them).</item>
/// </list>
/// Each is Kron-reduced onto B with <see cref="SparseKronReduction"/> (ascending elimination order, as WP9):
/// <c>Y_s,eq = Y_s,BB - Y_s,BE (Y_s,EE)^-1 Y_s,EB</c>.</para>
///
/// <para><b>How the reduced matrices are written.</b> For each boundary pair, an equivalent branch "SC:i-j" with the
/// positive-sequence series admittance <c>y = -(Y_ij + Y_ji) / 2</c> and, as its zero-sequence data, <c>R0 + jX0 =
/// 1 / y0</c>; a pair with no zero-sequence coupling (y0 exactly 0) gets delta/delta windings, which block the zero
/// sequence. For each boundary bus with a nonzero row sum s (positive) or s0 (zero), one fictitious bus "SC:b" joined to b
/// by a branch: the IEC 60909 positive and negative sequences neglect bus shunts, so the positive-sequence row sum is
/// carried as a source, a synchronous machine at the fictitious bus with X''d = Im(1/s)/2 (pu on a machine base equal to
/// the system base) behind the branch R + jX = Re(1/s) + j Im(1/s)/2, so the series path is exactly 1/s. For IEC 60909 use
/// this is the voltage source c Un / sqrt(3) behind the Norton admittance of the eliminated machines (the equivalent voltage
/// source at the fault location drives every source together). The same branch carries the zero-sequence row sum as a
/// grounded-wye (bus b) / delta (fictitious bus) transformer whose zero-sequence impedance is 1/s0: a shunt to earth of
/// exactly s0 at b. Row sums that are zero in exact arithmetic are left out (positive: no external machine and no off-nominal
/// tap; zero: no earth path in the reduced zero-sequence network) rather than written from rounding.</para>
///
/// <para><b>What it is exact for.</b> The driving-point and transfer impedances among the kept buses of every sequence are
/// those of the full network (the Kron reduction is exact; the arithmetic rounds), so the IEC 60909 initial symmetrical
/// currents Ik'' of three-phase, line-to-line, single- and double-line-to-earth faults at the kept buses are the full
/// model's. Quantities that read the network's topology or individual machines rather than the impedances (the meshed flag
/// of the peak factor, the near-to-generator breaking factors mu, Method C) see the equivalent's own topology and machines,
/// so ip and Ib can differ. Loads are not represented (IEC 60909 neglects them) and the reduced case's power flow is not the
/// base case: use the Ward family for that. The IEC 60909 correction factors (K_G, K_T) belong on the FULL case, before
/// it is reduced: applied to the reduced case they would correct the fictitious "SC:" machines and equivalent branches,
/// and the currents would no longer be the full case's.</para>
///
/// <para><b>Written out.</b> <see cref="GridSim.Core.IO.Json.V2CaseWriter.WriteWithFaultSequence"/> writes the reduced
/// case as a JSON v2 case that carries every sequence field (the equivalent branches' windings in the network file's
/// <c>lineWindings</c>, machine zero-sequence impedances and kept mutual couplings), with every number in its shortest
/// round-trip form, so the loaded-back case gives the same fault currents to the bit. MATPOWER holds no X''d and no
/// sequence data, so <c>reduce --method sc</c> refuses a <c>.m</c> output.</para>
///
/// <para><b>Refused</b> (fail loud, naming the element): an empty, complete or unknown internal set, a slack in E, an E not
/// connected to the boundary, a converter-interfaced unit in E (its IEC infeed is a current source weighted by |Z_kj|,
/// which a Kron reduction does not carry), an external machine with X2 different from X''d, an HVDC link, FACTS device, SVC,
/// induction machine, OLTC, flow control or boundary touching E, a mutual coupling between an external or tie line and a
/// kept line, a non-reciprocal equivalent (a phase shifter in E), a zero-sequence coupling with no positive-sequence
/// coupling, a positive-sequence row sum that is not inductive, and every zero-sequence network the earth-fault study
/// itself refuses.</para>
///
/// <para>Determinism: every sum runs in a fixed order (model order, branch order, generator order); no unordered
/// iteration reaches a number. The reduced case is a new model; nothing here changes any existing study.</para>
/// </summary>
public static class ShortCircuitEquivalent
{
    /// <summary>
    /// Reduce <paramref name="model"/> to the buses <paramref name="internalBusIds"/> for fault studies.
    /// <paramref name="subtransientXdPu"/> is the X''d the fault studies give a machine with none of its own and
    /// <paramref name="lineZeroSeqRatio"/> the Z0/Z1 multiple of a line with no zero-sequence impedance; pass the values the
    /// studies on the reduced case will use (their defaults are the studies' defaults). Refuses with
    /// <see cref="InvalidOperationException"/> naming the element (see the class summary).
    /// </summary>
    public static ShortCircuitEquivalentResult Reduce(GridModel model, IReadOnlyList<int> internalBusIds,
        double subtransientXdPu = Iec60909.DefaultSubtransientXdPu, double lineZeroSeqRatio = Iec60909.DefaultLineZeroSeqRatio)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(internalBusIds);
        if (!(subtransientXdPu > 0) || !double.IsFinite(subtransientXdPu))
            throw new ArgumentOutOfRangeException(nameof(subtransientXdPu), subtransientXdPu, "the fallback X''d must be a finite number above 0.");
        if (!(lineZeroSeqRatio > 0) || !double.IsFinite(lineZeroSeqRatio))
            throw new ArgumentOutOfRangeException(nameof(lineZeroSeqRatio), lineZeroSeqRatio, "the line Z0/Z1 ratio must be a finite number above 0.");

        // ---- The partition (the rules of NetworkReduction) ----
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        if (internalBusIds.Count == 0) throw new InvalidOperationException("the internal bus set is empty; name the buses to keep.");
        var kept = new bool[n];
        foreach (int id in internalBusIds)
        {
            if (!index.TryGetValue(id, out int i)) throw new InvalidOperationException($"internal bus {id} is not a bus of model '{model.Name}'.");
            if (kept[i]) throw new InvalidOperationException($"internal bus {id} is named twice.");
            kept[i] = true;
        }
        if (internalBusIds.Count == n) throw new InvalidOperationException("the internal bus set covers every bus; there is no external network to reduce.");
        for (int i = 0; i < n; i++)
            if (!kept[i] && model.Buses[i].Type == BusType.Slack)
                throw new InvalidOperationException($"slack bus {model.Buses[i].Id} is external; the slack must be an internal bus.");

        var isBoundary = new bool[n];
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            int f = index[br.From], t = index[br.To];
            if (kept[f] && !kept[t]) isBoundary[f] = true;
            if (kept[t] && !kept[f]) isBoundary[t] = true;
        }
        var boundary = new List<int>();   // model indices, model order
        var external = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (isBoundary[i]) boundary.Add(i);
            if (!kept[i]) external.Add(i);
        }

        RefuseExternalElements(model, index, kept, subtransientXdPu);
        RefuseDisconnectedExternal(model, index, kept, external);
        try { Iec60909.RequireRepresentableZeroSequence(model); }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("the zero-sequence network cannot be reduced: " + ex.Message, ex);
        }

        // ---- The sub-case: B and E (model order), the tie and external branches (series only), the external machines ----
        var subBuses = new List<Bus>();
        for (int i = 0; i < n; i++)
            if (isBoundary[i] || !kept[i]) subBuses.Add(model.Buses[i] with { Gs = 0, Bs = 0 });
        var subBranches = new List<Branch>();
        var subBranchOf = new Dictionary<int, int>();       // model branch index -> sub-case branch index
        var reducedBranchOf = new Dictionary<int, int>();   // model branch index -> reduced-case branch index (kept-kept)
        var branches2 = new List<Branch>();
        int extBranches = 0;
        for (int l = 0; l < model.Branches.Count; l++)
        {
            Branch br = model.Branches[l];
            if (kept[index[br.From]] && kept[index[br.To]]) { reducedBranchOf[l] = branches2.Count; branches2.Add(br); continue; }
            extBranches++;
            subBranchOf[l] = subBranches.Count;
            subBranches.Add(br.B == 0 ? br : br with { B = 0 });
        }
        var subGens = new List<Generator>();
        foreach (Generator g in model.Generators)
            if (!kept[index[g.Bus]]) subGens.Add(g);

        var subMutuals = new List<ZeroSequenceMutual>();
        var mutuals2 = new List<ZeroSequenceMutual>();
        foreach (ZeroSequenceMutual zm in model.ZeroSequenceMutuals)
        {
            bool aKept = reducedBranchOf.TryGetValue(zm.BranchA, out int ra), bKept = reducedBranchOf.TryGetValue(zm.BranchB, out int rb);
            bool aSub = subBranchOf.TryGetValue(zm.BranchA, out int sa), bSub = subBranchOf.TryGetValue(zm.BranchB, out int sb);
            if (aKept && bKept) mutuals2.Add(zm with { BranchA = ra, BranchB = rb });
            else if (aSub && bSub) subMutuals.Add(zm with { BranchA = sa, BranchB = sb });
            else
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"the zero-sequence mutual coupling between lines {zm.FromA}-{zm.ToA} and {zm.FromB}-{zm.ToB} joins a kept line to an external or tie line; a short-circuit equivalent cannot carry a coupling across the boundary."));
        }

        var sub = new GridModel
        {
            BaseMva = model.BaseMva,
            Name = model.Name + " (external network)",
            FrequencyHz = model.FrequencyHz,
            Buses = subBuses,
            Generators = subGens,
            Branches = subBranches,
            ZeroSequenceMutuals = subMutuals,
        };
        int m = subBuses.Count;
        var subIndex = new Dictionary<int, int>(m);
        for (int a = 0; a < m; a++) subIndex[subBuses[a].Id] = a;
        var keepLocal = new List<int>(boundary.Count);
        foreach (int b in boundary) keepLocal.Add(subIndex[model.Buses[b].Id]);
        var boundaryIds = boundary.Select(b => model.Buses[b].Id).ToList();
        double baseMva = model.BaseMva;

        // ---- Positive sequence: the series network and the external machines' X''d (+ XT) shunts ----
        SparseYBus y1 = YBus.BuildSparse(sub);
        var cells1 = new Dictionary<(int Row, int Col), Complex>(y1.Cells);
        int machines = 0;
        foreach (Generator g in subGens)
        {
            if (g.PowerFlowOnly) continue;   // a power-flow device: no source in the fault studies
            double xSys = g.FaultSourceReactancePu(subtransientXdPu) * baseMva / g.MBase;
            if (xSys <= 0) continue;
            int i = y1.IndexOf(g.Bus);
            Complex ys = Complex.One / new Complex(0.0, xSys);
            cells1[(i, i)] = cells1.TryGetValue((i, i), out Complex cur) ? cur + ys : Complex.Zero + ys;
            machines++;
        }
        bool tapped = false;
        foreach (Branch br in subBranches)
            if (br.InService && ((br.TapRatio != 0 && br.TapRatio != 1) || br.PhaseShiftDeg != 0)) { tapped = true; break; }
        Complex[,] y1Eq = SparseKronReduction.Reduce(m, cells1, keepLocal);
        RefuseNonReciprocal(y1Eq, boundaryIds, "positive");
        bool positiveRowSums = machines > 0 || tapped;

        // ---- Zero sequence: the fault studies' zero-sequence network, on the part that reaches the boundary ----
        Iec60909.ZeroSequenceNetwork zs = Iec60909.BuildZeroSequenceNetwork(sub, subIndex, lineZeroSeqRatio);
        var reach = new bool[m];
        var queue = new Queue<int>();
        foreach (int k in keepLocal) { reach[k] = true; queue.Enqueue(k); }
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int w in zs.Adjacency[u]) if (!reach[w]) { reach[w] = true; queue.Enqueue(w); }
        }
        var zLocal = new int[m];
        int mz = 0;
        for (int a = 0; a < m; a++) zLocal[a] = reach[a] ? mz++ : -1;
        var cells0 = new Dictionary<(int Row, int Col), Complex>();
        foreach ((int r, int c, Complex v) in zs.Trip)
        {
            int lr = zLocal[r], lc = zLocal[c];
            if (lr < 0 || lc < 0) continue;
            cells0[(lr, lc)] = cells0.TryGetValue((lr, lc), out Complex cur) ? cur + v : Complex.Zero + v;
        }
        bool earthed = false;
        for (int a = 0; a < m; a++) if (reach[a] && zs.HasGround[a]) { earthed = true; break; }
        Complex[,] y0Eq = SparseKronReduction.Reduce(mz, cells0, keepLocal.Select(k => zLocal[k]).ToList());
        RefuseNonReciprocal(y0Eq, boundaryIds, "zero");
        int unreached = 0;
        for (int a = 0; a < m; a++) if (!reach[a]) unreached++;

        // ---- The equivalent elements ----
        var elements = new List<EquivalentElement>();
        var notes = new List<string>();
        int maxId = 0;
        foreach (Bus b in model.Buses) maxId = Math.Max(maxId, b.Id);
        int nextId = maxId + 1;
        var fictBuses = new List<Bus>();
        var fictGens = new List<Generator>();
        int nb = boundary.Count;
        for (int a = 0; a < nb; a++)
            for (int b = a + 1; b < nb; b++)
            {
                Complex y = WardMath.BranchAdmittance(y1Eq, a, b);
                Complex y0 = WardMath.BranchAdmittance(y0Eq, a, b);
                if (y == Complex.Zero && y0 == Complex.Zero) continue;
                string name = "SC:" + boundaryIds[a].ToString(CultureInfo.InvariantCulture) + "-" + boundaryIds[b].ToString(CultureInfo.InvariantCulture);
                if (y == Complex.Zero)
                    throw new InvalidOperationException(
                        $"the zero-sequence equivalent couples boundary buses {boundaryIds[a]} and {boundaryIds[b]} but the positive sequence does not; one branch cannot carry it.");
                Complex z = Complex.One / y;
                Branch eq = new(boundaryIds[a], boundaryIds[b], z.Real, z.Imaginary, Name: name);
                if (y0 == Complex.Zero) eq = eq with { FromWinding = WindingConnection.Delta, ToWinding = WindingConnection.Delta };
                else
                {
                    Complex z0 = Complex.One / y0;
                    eq = eq with { R0 = z0.Real, X0 = z0.Imaginary };
                    elements.Add(new EquivalentElement(EquivalentElementKind.Branch, "SC0:" + name[3..], new[] { boundaryIds[a], boundaryIds[b] }, y0));
                }
                branches2.Add(eq);
                elements.Add(new EquivalentElement(EquivalentElementKind.Branch, name, new[] { boundaryIds[a], boundaryIds[b] }, y));
            }

        for (int a = 0; a < nb; a++)
        {
            Complex s1 = positiveRowSums ? WardMath.RowSum(y1Eq, a) : Complex.Zero;
            Complex s0 = earthed ? WardMath.RowSum(y0Eq, a) : Complex.Zero;
            if (s1 == Complex.Zero && s0 == Complex.Zero) continue;
            Bus bb = model.Buses[boundary[a]];
            string id = bb.Id.ToString(CultureInfo.InvariantCulture);
            string name = "SC:" + id;
            int fb = nextId++;
            Branch link;
            if (s1 != Complex.Zero)
            {
                Complex z = Complex.One / s1;
                if (!(z.Imaginary > 0) || !double.IsFinite(z.Imaginary) || !double.IsFinite(z.Real))
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"the positive-sequence equivalent at boundary bus {id} is not inductive (1/s = {z.Real:R} + j{z.Imaginary:R} pu); a synchronous source cannot carry it."));
                double xF = z.Imaginary / 2.0;
                link = new Branch(bb.Id, fb, z.Real, z.Imaginary - xF, Name: name);
                fictBuses.Add(new Bus(fb, BusType.Pv, BaseKv: bb.BaseKv, Name: name));
                fictGens.Add(new Generator(fb, Pg: 0.0, Qg: 0.0, Vg: 1.0, MBase: baseMva, Name: name, SubtransientXdPu: xF));
                elements.Add(new EquivalentElement(EquivalentElementKind.Shunt, "SC-SH:" + id, new[] { bb.Id }, s1));
            }
            else
            {
                // No positive-sequence source (no external machine reaches here), but a zero-sequence path to earth: the
                // branch only carries the zero-sequence shunt; its far bus has no source.
                Complex z = Complex.One / s0;
                link = new Branch(bb.Id, fb, z.Real, z.Imaginary, Name: name);
                fictBuses.Add(new Bus(fb, BusType.Pq, BaseKv: bb.BaseKv, Name: name));
                notes.Add($"boundary bus {id}: zero-sequence earth path with no positive-sequence source; bus {fb} carries no source.");
            }
            if (s0 != Complex.Zero)
            {
                Complex z0 = Complex.One / s0;
                link = link with { R0 = z0.Real, X0 = z0.Imaginary, FromWinding = WindingConnection.WyeGrounded, ToWinding = WindingConnection.Delta };
                elements.Add(new EquivalentElement(EquivalentElementKind.Shunt, "SC0-SH:" + id, new[] { bb.Id }, s0));
            }
            else link = link with { FromWinding = WindingConnection.Delta, ToWinding = WindingConnection.Delta };
            branches2.Add(link);
        }

        // ---- The Thevenin report: diag((Y_s,eq)^-1) per bus, infinite at a bus the external network gives no path ----
        Complex[]? z1Diag = positiveRowSums ? PerBusInverseDiagonal(y1Eq) : null;
        Complex[]? z0Diag = earthed ? PerBusInverseDiagonal(y0Eq) : null;
        var thevenin = new List<SequenceTheveninImpedance>(nb);
        var inf = new Complex(double.PositiveInfinity, double.PositiveInfinity);
        for (int a = 0; a < nb; a++)
        {
            Complex z1 = z1Diag?[a] ?? inf, z0 = z0Diag?[a] ?? inf;
            thevenin.Add(new SequenceTheveninImpedance(boundaryIds[a], z1, z1, z0));
        }

        // ---- The reduced case ----
        var buses2 = new List<Bus>();
        for (int i = 0; i < n; i++) if (kept[i]) buses2.Add(model.Buses[i]);
        buses2.AddRange(fictBuses);
        var gens2 = new List<Generator>();
        int extGens = 0;
        foreach (Generator g in model.AllGenerators)
        {
            if (kept[index[g.Bus]]) gens2.Add(g);
            else extGens++;
        }
        gens2.AddRange(fictGens);

        notes.Insert(0, string.Create(CultureInfo.InvariantCulture,
            $"removed {external.Count} external bus(es), {extBranches} branch(es) (external and tie) and {extGens} generator(s); external loads are not represented (IEC 60909 neglects loads)."));
        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"sequence data: X''d {subtransientXdPu:R} pu for a machine with none of its own, line Z0 = {lineZeroSeqRatio:R} x Z1 for a line with none; fault studies on the reduced case must use the same values."));
        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"{machines} external synchronous machine(s) carried as {fictGens.Count} equivalent source(s); negative sequence equal to the positive sequence."));
        if (!positiveRowSums) notes.Add("no external machine and no off-nominal tap: the positive-sequence equivalent has no shunt part.");
        if (!earthed) notes.Add("the external zero-sequence network has no path to earth: no zero-sequence shunt.");
        if (unreached > 0) notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"{unreached} external bus(es) have no zero-sequence path to the boundary and take no part in the zero-sequence equivalent."));
        notes.Add("exact for the IEC 60909 initial currents Ik'' at the kept buses; the meshed-network peak factor, the near-to-generator breaking factors and Method C read the equivalent's own topology and machines, and the reduced case's power flow is not the base case.");
        if (model.AssetDynamics is not null || model.Protections is not null || model.UnitProtection is not null)
            notes.Add("authored asset dynamics, protections and unit protection are not carried to the reduced case.");

        var reduced = new GridModel
        {
            BaseMva = model.BaseMva,
            Name = model.Name + "_sc",
            FrequencyHz = model.FrequencyHz,
            Buses = buses2,
            Generators = gens2,
            Branches = branches2,
            Oltcs = model.Oltcs,
            Boundaries = model.Boundaries,
            LccLinks = model.LccLinks,
            VscLinks = model.VscLinks,
            MtdcLinks = model.MtdcLinks,
            FactsDevices = model.FactsDevices,
            StaticVarCompensators = model.StaticVarCompensators,
            InductionMachines = model.InductionMachines,
            TransformerFlowControls = model.TransformerFlowControls,
            ZeroSequenceMutuals = mutuals2,
        };
        var network = new ReducedNetwork(reduced, boundaryIds, external.Select(e => model.Buses[e].Id).ToList(), elements, notes);
        return new ShortCircuitEquivalentResult(network, thevenin);
    }

    /// <summary>
    /// RC5 review R1 finding 2: the Thevenin diagonal per boundary bus. A bus whose row and column of
    /// <paramref name="y"/> are identically zero (the external network gives it no path in this sequence, for example a
    /// boundary bus whose only tie is the delta side of a Dyn transformer, in the zero sequence) has an infinite
    /// impedance; the diagonal of the others is the inverse of the block of the rows and columns that are not zero. With
    /// no zero row this is exactly <see cref="InverseDiagonal"/> of the whole matrix (the same operations, so the same
    /// bits); before, one zero row made the matrix singular and every bus was reported infinite.
    /// </summary>
    internal static Complex[] PerBusInverseDiagonal(Complex[,] y)
    {
        int m = y.GetLength(0);
        var active = new List<int>(m);
        for (int i = 0; i < m; i++)
        {
            bool any = false;
            for (int j = 0; j < m && !any; j++) any = y[i, j] != Complex.Zero || y[j, i] != Complex.Zero;
            if (any) active.Add(i);
        }
        if (active.Count == m) return InverseDiagonal(y);
        var inf = new Complex(double.PositiveInfinity, double.PositiveInfinity);
        var diag = new Complex[m];
        for (int i = 0; i < m; i++) diag[i] = inf;
        if (active.Count == 0) return diag;
        var sub = new Complex[active.Count, active.Count];
        for (int a = 0; a < active.Count; a++)
            for (int b = 0; b < active.Count; b++) sub[a, b] = y[active[a], active[b]];
        Complex[] d = InverseDiagonal(sub);
        for (int a = 0; a < active.Count; a++) diag[active[a]] = d[a];
        return diag;
    }

    /// <summary>The diagonal of the inverse of the small dense matrix <paramref name="y"/> by Gauss-Jordan elimination with
    /// partial pivoting (largest magnitude, first on ties), in a fixed order. A singular matrix gives infinite entries.</summary>
    internal static Complex[] InverseDiagonal(Complex[,] y)
    {
        int m = y.GetLength(0);
        var w = new Complex[m, 2 * m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++) w[i, j] = y[i, j];
            w[i, m + i] = Complex.One;
        }
        var diag = new Complex[m];
        for (int c = 0; c < m; c++)
        {
            int p = c;
            double best = w[c, c].Magnitude;
            for (int r = c + 1; r < m; r++) if (w[r, c].Magnitude > best) { best = w[r, c].Magnitude; p = r; }
            if (!(best > 0))
            {
                for (int i = 0; i < m; i++) diag[i] = new Complex(double.PositiveInfinity, double.PositiveInfinity);
                return diag;
            }
            if (p != c) for (int j = 0; j < 2 * m; j++) (w[c, j], w[p, j]) = (w[p, j], w[c, j]);
            Complex piv = w[c, c];
            for (int j = 0; j < 2 * m; j++) w[c, j] /= piv;
            for (int r = 0; r < m; r++)
            {
                if (r == c || w[r, c] == Complex.Zero) continue;
                Complex f = w[r, c];
                for (int j = 0; j < 2 * m; j++) w[r, j] -= f * w[c, j];
            }
        }
        for (int i = 0; i < m; i++) diag[i] = w[i, m + i];
        return diag;
    }

    private static void RefuseNonReciprocal(Complex[,] yEq, IReadOnlyList<int> ids, string sequence)
    {
        if (!WardMath.IsReciprocal(yEq, NetworkReduction.ReciprocityTolerance, out (int I, int J) p))
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"the {sequence}-sequence equivalent is not reciprocal between buses {ids[p.I]} and {ids[p.J]} (|Y_ij - Y_ji| > {NetworkReduction.ReciprocityTolerance:R} |Y_ij|): a phase shifter in the external network; out of scope."));
    }

    private static void RefuseExternalElements(GridModel model, Dictionary<int, int> index, bool[] kept, double subtransientXdPu)
    {
        bool Ext(int bus) => index.TryGetValue(bus, out int i) && !kept[i];
        var problems = new List<string>();
        foreach (Generator g in model.Generators)
        {
            if (!Ext(g.Bus) || g.PowerFlowOnly) continue;
            string name = g.Name ?? $"gen@bus {g.Bus}";
            if (g.IsConverterInterfaced)
            {
                problems.Add($"converter-interfaced unit '{name}' at bus {g.Bus} is in the external network (its IEC 60909 infeed is a current source weighted by |Z_kj|, which a Kron reduction does not carry)");
                continue;
            }
            if (!(g.MBase > 0.0) || !double.IsFinite(g.MBase))
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"machine '{name}' at bus {g.Bus} has MBase = {g.MBase:R}; a fault source needs its rating"));
            else if (g.FaultSourceNegativeSeqReactancePu(subtransientXdPu) != g.FaultSourceReactancePu(subtransientXdPu))
                problems.Add($"machine '{name}' at bus {g.Bus} has X2 different from X''d, so the negative-sequence equivalent differs from the positive one and one branch per pair cannot carry both");
        }
        foreach (LccDcLink l in model.LccLinks)
            if (Ext(l.Rectifier.Bus) || Ext(l.Inverter.Bus)) problems.Add($"LCC HVDC link '{l.Name}' touches the external network");
        foreach (VscDcLink l in model.VscLinks)
            if (Ext(l.First.Bus) || Ext(l.Second.Bus)) problems.Add($"VSC HVDC link '{l.Name}' touches the external network");
        foreach (MtdcLink l in model.MtdcLinks)
            if (l.Converters.Any(c => Ext(c.Bus)) || l.DcBuses.Any(d => Ext(d.AcBus))) problems.Add($"multi-terminal DC link '{l.Name}' touches the external network");
        foreach (FactsDevice f in model.FactsDevices)
            if (Ext(f.SendingBus) || Ext(f.TerminalBus)) problems.Add($"FACTS device '{f.Name}' touches the external network");
        foreach (StaticVarCompensator s in model.StaticVarCompensators)
            if (Ext(s.Bus)) problems.Add($"SVC '{s.Name}' at bus {s.Bus} is in the external network");
        foreach (InductionMachine im in model.InductionMachines)
            if (Ext(im.Bus)) problems.Add($"induction machine '{im.Name}' at bus {im.Bus} is in the external network");

        bool BranchInside(string? name)
        {
            foreach (Branch br in model.Branches)
                if (br.Name is not null && br.Name == name) return !Ext(br.From) && !Ext(br.To);
            return false;
        }
        foreach (Oltc o in model.Oltcs)
            if (!BranchInside(o.Transformer) || Ext(o.ControlsBus)) problems.Add($"OLTC on '{o.Transformer}' touches the external network");
        foreach (TransformerFlowControl c in model.TransformerFlowControls)
            if (!BranchInside(c.Transformer) || Ext(c.FromBus)) problems.Add($"flow control on '{c.Transformer}' touches the external network");
        foreach (Boundary b in model.Boundaries)
            if (b.Branches.Any(x => !BranchInside(x.Branch))) problems.Add($"boundary '{b.Name}' has a branch in the external network");
        if (problems.Count > 0)
            throw new InvalidOperationException("the external network holds elements a short-circuit equivalent cannot represent: " + string.Join("; ", problems) + ".");
    }

    private static void RefuseDisconnectedExternal(GridModel model, Dictionary<int, int> index, bool[] kept, List<int> external)
    {
        int n = model.BusCount;
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            int f = index[br.From], t = index[br.To];
            if (kept[f] && kept[t]) continue;
            adj[f].Add(t);
            adj[t].Add(f);
        }
        var reached = new bool[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++) if (kept[i]) { reached[i] = true; queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int w in adj[u]) if (!reached[w]) { reached[w] = true; queue.Enqueue(w); }
        }
        foreach (int e in external)
            if (!reached[e])
                throw new InvalidOperationException($"external bus {model.Buses[e].Id} is not connected to the boundary; the external network must be connected to it.");
    }
}
