using System.Globalization;
using System.Numerics;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow.Equivalents;

/// <summary>
/// Static network equivalents: Ward, extended Ward and REI. The buses the caller keeps (the internal set, which holds the
/// boundary) stay as they are; every other bus is external and is replaced by elements on the boundary, computed from a
/// solved, converged base case.
///
/// <para><b>Partition.</b> K = the kept buses (given). B = the kept buses with an in-service branch to an external bus
/// (computed, model order). E = every other bus. The slack must be in K.</para>
///
/// <para><b>Ward</b> (Ward 1949; Monticelli 1999, ch. 12). Y^x is built on B and E from the tie branches, the external
/// branches and the external bus shunts only, with the same stamps as <see cref="YBus"/>. Its Kron reduction keeping B
/// (<see cref="SparseKronReduction"/>, unchanged) is Y_eq = Y^x_BB - Y^x_BE (Y^x_EE)^-1 Y^x_EB. The external current
/// injections I_E = (Y V)_E come from the solved state and move to the boundary as dI_B = -Y^x_BE (Y^x_EE)^-1 I_E. The KCL
/// rows of E give V_E = (Y^x_EE)^-1 (I_E - Y^x_EB V_B); put into the rows of B this is
/// I_B + dI_B = (Y^int V)_B + Y_eq V_B. Every reduced equation is an original one rearranged, so the base-case V_K solves
/// the reduced case up to round-off. Y_eq becomes one branch per boundary pair (y = -(Y_ij + Y_ji)/2) and a row-sum shunt
/// per boundary bus; dS_b = V_b conj(dI_b) is a constant-power injection.</para>
///
/// <para><b>Extended Ward</b> (Deckmann, Pizzolante, Monticelli, Stott and Alsac 1980). Ward, plus Y_g, the same reduction
/// with the external PV buses grounded (removed from E: held at their voltage), and d_b = sum_j (Y_g - Y_eq)_bj. Each b with
/// d_b != 0 gets a fictitious PV bus "XW:b" (|V| set-point |V_b| of the base case, P = 0, Q unlimited) joined to b by the
/// admittance d_b. At the base case both ends are at V_b and the branch carries no current. GridSim uses the full complex
/// Y here, not the B'' matrix of the 1980 paper.</para>
///
/// <para><b>REI</b> (Dimo 1975; Tinney and Powell 1977; Savulescu 1981). The external injections S_k of a group move to one
/// fictitious bus R through the zero-power-balance network of <see cref="WardMath.ZeroPowerBalance"/>; then Ward on the
/// extended network with E' = E and the ZPB node 0, and B' = B and R. The generator group's R is a PV bus (|V_R|, P = Re S_R,
/// the summed Q limits); the load group's R is a PQ bus.</para>
///
/// <para>Determinism: every sum runs in a fixed order (model order, then the fixed element order); no unordered iteration
/// reaches a number. The reduced case is a new model; nothing here changes any existing study.</para>
/// </summary>
public static class NetworkReduction
{
    /// <summary>The relative reciprocity tolerance of Y_eq: above it the equivalent is refused (a phase shifter in E).</summary>
    public const double ReciprocityTolerance = 1e-9;

    /// <summary>The smallest aggregated REI current |I_R| (pu) that is aggregated.</summary>
    public const double MinReiCurrentPu = 1e-9;

    /// <summary>The smallest |sum y_k + y_R| (pu) of a ZPB network: below it node 0 has no admittance to ground on.</summary>
    public const double MinZpbAdmittancePu = 1e-12;

    /// <summary>
    /// Reduce <paramref name="model"/> to the buses of <paramref name="spec"/>, from the converged base case
    /// <paramref name="solved"/> (a solve of this model, bus order the model's). Refuses with
    /// <see cref="InvalidOperationException"/> naming the element: a non-converged base, an empty or complete internal
    /// set, an unknown bus id, a slack bus in E, an E not connected to the boundary, a ZIP load in E, an HVDC, FACTS, SVC,
    /// induction machine, OLTC, flow control or boundary touching E, a non-reciprocal equivalent, and an REI group with no
    /// current to aggregate.
    /// </summary>
    public static ReducedNetwork Reduce(GridModel model, PowerFlowResult solved, ReductionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(solved);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.InternalBusIds);
        // RC5 WP31: the short-circuit and dynamic equivalents have their own entry points; refused here by name so they are
        // never built as an REI equivalent. Ward, extended Ward and REI do not reach this line's throw.
        if (spec.Method is EquivalentMethod.ShortCircuit or EquivalentMethod.Dynamic)
            throw new InvalidOperationException($"the {spec.Method} equivalent is not built here: use ShortCircuitEquivalent.Reduce or InertialAggregation.Build.");

        // ---- The base case ----
        if (!solved.Converged || solved.Status != PowerFlowStatus.Converged)
            throw new InvalidOperationException($"the base case is not converged (status {solved.Status}); a network equivalent needs a solved, converged base.");
        int n = model.BusCount;
        if (solved.BusIds.Count != n || solved.Vm.Length != n || solved.VaDeg.Length != n)
            throw new InvalidOperationException($"the base case has {solved.BusIds.Count} buses but the model has {n}; give the solve of this model.");
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
        {
            if (solved.BusIds[i] != model.Buses[i].Id)
                throw new InvalidOperationException($"the base case's bus {i} is {solved.BusIds[i]} but the model's is {model.Buses[i].Id}; give the solve of this model.");
            index[model.Buses[i].Id] = i;
        }

        // ---- The partition ----
        if (spec.InternalBusIds.Count == 0) throw new InvalidOperationException("the internal bus set is empty; name the buses to keep.");
        var kept = new bool[n];
        foreach (int id in spec.InternalBusIds)
        {
            if (!index.TryGetValue(id, out int i)) throw new InvalidOperationException($"internal bus {id} is not a bus of model '{model.Name}'.");
            if (kept[i]) throw new InvalidOperationException($"internal bus {id} is named twice.");
            kept[i] = true;
        }
        if (spec.InternalBusIds.Count == n) throw new InvalidOperationException("the internal bus set covers every bus; there is no external network to reduce.");

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

        RefuseExternalElements(model, index, kept);
        RefuseDisconnectedExternal(model, index, kept, external);

        // ---- The solved state: V and the injected currents I = (Y V) at the external buses ----
        var v = new Complex[n];
        for (int i = 0; i < n; i++) v[i] = DetMath.FromPolar(solved.Vm[i], solved.VaDeg[i] * Math.PI / 180.0);
        Complex[] current = ExternalCurrents(model, kept, v);
        double baseMva = model.BaseMva;

        // ---- Y^x on B and E (local index: model order over B and E) ----
        var bxNodes = new List<int>();
        for (int i = 0; i < n; i++) if (isBoundary[i] || !kept[i]) bxNodes.Add(i);
        var notes = new List<string>();
        var elements = new List<EquivalentElement>();

        int maxId = 0;
        foreach (Bus b in model.Buses) maxId = Math.Max(maxId, b.Id);
        int nextId = maxId + 1;

        var fictBuses = new List<Bus>();
        var fictGens = new List<Generator>();
        var fictBranches = new List<Branch>();

        // Per kept-bus additions: shunt (pu) and injection (pu), keyed by model index.
        var addShunt = new Complex[n];
        var addInjection = new Complex[n];

        if (spec.Method is EquivalentMethod.Ward or EquivalentMethod.ExtendedWard)
        {
            var local = LocalIndex(n, bxNodes);
            Dictionary<(int Row, int Col), Complex> yx = ExternalCells(model, index, kept, local);
            List<int> keepLocal = boundary.Select(b => local[b]).ToList();
            Complex[,] yEq = WardMath.EquivalentAdmittance(bxNodes.Count, yx, keepLocal);
            RefuseNonReciprocal(yEq, boundary.Select(b => model.Buses[b].Id).ToList());
            var inj = new Complex[bxNodes.Count];
            for (int a = 0; a < bxNodes.Count; a++) if (!kept[bxNodes[a]]) inj[a] = current[bxNodes[a]];
            Complex[] dI = WardMath.TransferredCurrents(bxNodes.Count, yx, keepLocal, inj);

            var keepIds = boundary.Select(b => model.Buses[b].Id).ToList();
            AddWardElements(yEq, dI, boundary.Select(b => v[b]).ToList(), keepIds, boundary, addShunt, addInjection, fictBranches, elements);

            if (spec.Method == EquivalentMethod.ExtendedWard)
            {
                // Y_g: the same cells with the external PV buses grounded (their rows and columns dropped).
                var pvExt = ExternalPvBuses(model, solved, kept);
                var gNodes = bxNodes.Where(i => !pvExt.Contains(i)).ToList();
                var gLocal = LocalIndex(n, gNodes);
                Dictionary<(int Row, int Col), Complex> yg = ExternalCells(model, index, kept, gLocal);
                Complex[,] yG = WardMath.EquivalentAdmittance(gNodes.Count, yg, boundary.Select(b => gLocal[b]).ToList());
                for (int a = 0; a < boundary.Count; a++)
                {
                    Complex d = Complex.Zero;
                    for (int j = 0; j < boundary.Count; j++) d += yG[a, j] - yEq[a, j];
                    if (d == Complex.Zero) continue;
                    int b = boundary[a];
                    Bus bb = model.Buses[b];
                    int xw = nextId++;
                    string name = "XW:" + bb.Id.ToString(CultureInfo.InvariantCulture);
                    fictBuses.Add(new Bus(xw, BusType.Pv, BaseKv: bb.BaseKv, Vm: solved.Vm[b], VaDeg: solved.VaDeg[b], Name: name));
                    fictGens.Add(new Generator(xw, Pg: 0.0, Qg: 0.0, Vg: solved.Vm[b], Pmin: double.NegativeInfinity, MBase: baseMva,
                        Name: name, PowerFlowOnly: true));
                    Complex z = Complex.One / d;
                    fictBranches.Add(new Branch(bb.Id, xw, z.Real, z.Imaginary, Name: name));
                    elements.Add(new EquivalentElement(EquivalentElementKind.ExtendedWardSource, name, new[] { bb.Id, xw }, d));
                }
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"extended Ward: {pvExt.Count} external PV bus(es) grounded for Y_g; {fictBuses.Count} XW source(s) added (unlimited Q, P = 0)."));
            }
        }
        else
        {
            // ---- REI: the groups, their ZPB networks, then Ward on the extended network ----
            var groups = ReiGroups(model, spec.Grouping, kept, v, current, baseMva);
            var zpbs = new List<(string Name, bool Generators, List<int> Buses, List<Complex> S, WardMath.Zpb Z)>();
            foreach ((string gname, bool gens, List<int> buses, List<Complex> s) in groups)
            {
                if (buses.Count == 0) { notes.Add($"REI group '{gname}' has no external element; no REI bus for it."); continue; }
                WardMath.Zpb z = WardMath.ZeroPowerBalance(s, buses.Select(k => v[k]).ToList());
                if (z.Ir.Magnitude < MinReiCurrentPu)
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"REI group '{gname}' has an aggregated current of {z.Ir.Magnitude:R} pu, below {MinReiCurrentPu:R}; there is no current to aggregate."));
                Complex den = z.Yr;
                foreach (Complex y in z.Yk) den += y;
                if (den.Magnitude < MinZpbAdmittancePu)
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"REI group '{gname}': the ZPB node admittance |sum y_k + y_R| is {den.Magnitude:R} pu, below {MinZpbAdmittancePu:R}."));
                zpbs.Add((gname, gens, buses, s, z));
            }

            // Extended nodes: B and E (model order), then each group's node 0, then each group's R.
            int nb = bxNodes.Count, ng = zpbs.Count;
            var local = LocalIndex(n, bxNodes);
            Dictionary<(int Row, int Col), Complex> cells = ExternalCells(model, index, kept, local);
            int nExt = nb + 2 * ng;
            var inj = new Complex[nExt];
            for (int a = 0; a < nb; a++) if (!kept[bxNodes[a]]) inj[a] = current[bxNodes[a]];
            for (int g = 0; g < ng; g++)
            {
                int zero = nb + g, r = nb + ng + g;
                var (_, _, buses, s, z) = zpbs[g];
                for (int q = 0; q < buses.Count; q++)
                {
                    int k = local[buses[q]];
                    StampSeries(cells, k, zero, z.Yk[q]);
                    // The group's element current leaves bus k for R: I_k' = I_k - conj(S_k / V_k).
                    inj[k] -= Complex.Conjugate(s[q] / v[buses[q]]);
                }
                StampSeries(cells, zero, r, z.Yr);
            }
            var keepLocal = boundary.Select(b => local[b]).ToList();
            for (int g = 0; g < ng; g++) keepLocal.Add(nb + ng + g);
            Complex[,] yEq = WardMath.EquivalentAdmittance(nExt, cells, keepLocal);

            // The REI bus ids, in group order after any other fictitious bus.
            var keepIds = boundary.Select(b => model.Buses[b].Id).ToList();
            var rIds = new List<int>();
            for (int g = 0; g < ng; g++) { rIds.Add(nextId++); keepIds.Add(rIds[g]); }
            RefuseNonReciprocal(yEq, keepIds);
            Complex[] dI = WardMath.TransferredCurrents(nExt, cells, keepLocal, inj);

            var keepV = boundary.Select(b => v[b]).ToList();
            foreach (var zp in zpbs) keepV.Add(zp.Z.Vr);
            // Kept model indices for the boundary; the R buses carry their additions on their own records (index -1).
            var keepModel = new List<int>(boundary);
            for (int g = 0; g < ng; g++) keepModel.Add(-1);
            var rShunt = new Complex[ng];
            var rInjection = new Complex[ng];
            AddWardElements(yEq, dI, keepV, keepIds, keepModel, addShunt, addInjection, fictBranches, elements, rShunt, rInjection, boundary.Count);

            for (int g = 0; g < ng; g++)
            {
                var (gname, gens, buses, _, z) = zpbs[g];
                string name = "REI:" + gname;
                double vmR = Math.Sqrt(WardMath.Norm(z.Vr));
                double vaR = DetMath.Phase(z.Vr) * 180.0 / Math.PI;
                double baseKv = model.Buses[buses[0]].BaseKv;
                Complex shunt = rShunt[g] * baseMva;
                Complex dS = rInjection[g] * baseMva;
                if (gens)
                {
                    // The generator group: a PV bus holding |V_R|, P = Re S_R, Q limits summed (unlimited when any is).
                    double qmin = 0, qmax = 0, pmin = 0, pmax = 0, mbase = 0;
                    foreach (Generator gen in model.Generators)
                    {
                        if (kept[index[gen.Bus]]) continue;
                        qmin += gen.Qmin; qmax += gen.Qmax; pmin += gen.Pmin; pmax += gen.Pmax; mbase += gen.MBase;
                    }
                    fictBuses.Add(new Bus(rIds[g], BusType.Pv, Pd: -dS.Real, Qd: -dS.Imaginary, Gs: shunt.Real, Bs: shunt.Imaginary,
                        BaseKv: baseKv, Vm: vmR, VaDeg: vaR, Name: name));
                    fictGens.Add(new Generator(rIds[g], Pg: z.Sr.Real * baseMva, Qg: z.Sr.Imaginary * baseMva, Vg: vmR,
                        Qmin: qmin, Qmax: qmax, Pmin: pmin, Pmax: pmax, MBase: mbase > 0 ? mbase : baseMva, Name: name, PowerFlowOnly: true));
                }
                else
                {
                    // The load group: a PQ bus drawing the aggregated load plus its Ward injection.
                    Complex sTot = (z.Sr + rInjection[g]) * baseMva;
                    fictBuses.Add(new Bus(rIds[g], BusType.Pq, Pd: -sTot.Real, Qd: -sTot.Imaginary, Gs: shunt.Real, Bs: shunt.Imaginary,
                        BaseKv: baseKv, Vm: vmR, VaDeg: vaR, Name: name));
                }
                elements.Add(new EquivalentElement(EquivalentElementKind.ReiBus, name, new[] { rIds[g] }, z.Sr));
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"REI group '{gname}': {buses.Count} external bus(es) aggregated at bus {rIds[g]} (S_R = {z.Sr.Real * baseMva:R} + j{z.Sr.Imaginary * baseMva:R} MVA, |V_R| = {vmR:R} pu, angle {vaR:R} deg)."));
            }
        }

        // ---- The reduced case ----
        var buses2 = new List<Bus>();
        for (int i = 0; i < n; i++)
        {
            if (!kept[i]) continue;
            Bus b = model.Buses[i];
            Complex sh = addShunt[i] * baseMva, inj = addInjection[i] * baseMva;
            buses2.Add(b with
            {
                Gs = b.Gs + sh.Real, Bs = b.Bs + sh.Imaginary,
                Pd = b.Pd - inj.Real, Qd = b.Qd - inj.Imaginary,
                Vm = solved.Vm[i], VaDeg = solved.VaDeg[i],
            });
        }
        buses2.AddRange(fictBuses);

        var branchMap = new Dictionary<int, int>();
        var branches2 = new List<Branch>();
        int extBranches = 0;
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch br = model.Branches[i];
            if (kept[index[br.From]] && kept[index[br.To]]) { branchMap[i] = branches2.Count; branches2.Add(br); }
            else extBranches++;
        }
        branches2.AddRange(fictBranches);

        var gens2 = new List<Generator>();
        int extGens = 0;
        foreach (Generator g in model.AllGenerators)
        {
            if (kept[index[g.Bus]]) gens2.Add(g);
            else extGens++;
        }
        gens2.AddRange(fictGens);

        var mutuals = new List<ZeroSequenceMutual>();
        foreach (ZeroSequenceMutual zm in model.ZeroSequenceMutuals)
            if (branchMap.TryGetValue(zm.BranchA, out int a2) && branchMap.TryGetValue(zm.BranchB, out int b2))
                mutuals.Add(zm with { BranchA = a2, BranchB = b2 });

        int extLoads = external.Count(i => model.Buses[i].Pd != 0 || model.Buses[i].Qd != 0);
        notes.Insert(0, string.Create(CultureInfo.InvariantCulture,
            $"removed {external.Count} external bus(es), {extBranches} branch(es) (external and tie), {extGens} generator(s) and {extLoads} load bus(es)."));
        if (model.AssetDynamics is not null || model.Protections is not null || model.UnitProtection is not null)
            notes.Add("authored asset dynamics, protections and unit protection are not carried to the reduced case.");
        notes.Add("the sequence networks are not reduced: the equivalent elements carry no zero- or negative-sequence data, so fault studies on the reduced case do not represent the external network.");

        string methodName = spec.Method switch { EquivalentMethod.Ward => "ward", EquivalentMethod.ExtendedWard => "xward", _ => "rei" };
        var reduced = new GridModel
        {
            BaseMva = model.BaseMva,
            Name = model.Name + "_" + methodName,
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
            ZeroSequenceMutuals = mutuals,
        };
        return new ReducedNetwork(reduced, boundary.Select(b => model.Buses[b].Id).ToList(),
            external.Select(e => model.Buses[e].Id).ToList(), elements, notes);
    }

    /// <summary>
    /// The base-case check: the largest |V_reduced - V_full| over the given buses (pu, complex difference), from a solve of
    /// the full model and a solve of the reduced one.
    /// </summary>
    public static double MaxVoltageDifference(PowerFlowResult full, PowerFlowResult reduced, IReadOnlyList<int> busIds)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(reduced);
        ArgumentNullException.ThrowIfNull(busIds);
        double worst = 0;
        foreach (int id in busIds)
        {
            int i = IndexOf(full.BusIds, id), j = IndexOf(reduced.BusIds, id);
            Complex a = DetMath.FromPolar(full.Vm[i], full.VaDeg[i] * Math.PI / 180.0);
            Complex b = DetMath.FromPolar(reduced.Vm[j], reduced.VaDeg[j] * Math.PI / 180.0);
            double d = (b - a).Magnitude;
            if (d > worst || double.IsNaN(d)) worst = d;
        }
        return worst;
    }

    private static int IndexOf(IReadOnlyList<int> ids, int id)
    {
        for (int i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
        throw new InvalidOperationException($"bus {id} is not in the result.");
    }

    // ------------------------------------------------------------------------------------------------------------------

    private static int[] LocalIndex(int n, List<int> nodes)
    {
        var local = new int[n];
        Array.Fill(local, -1);
        for (int a = 0; a < nodes.Count; a++) local[nodes[a]] = a;
        return local;
    }

    /// <summary>
    /// The cells of Y^x on the nodes with a local index: every in-service tie and external branch (the stamps of
    /// <see cref="YBus"/>, in branch order) and the external bus shunts, accumulated as <see cref="YBus.BuildSparse"/> does
    /// (the first write is 0 + v). A cell with a node that has no local index (a grounded node) is dropped.
    /// </summary>
    private static Dictionary<(int Row, int Col), Complex> ExternalCells(GridModel model, Dictionary<int, int> index, bool[] kept,
        int[] local)
    {
        var cells = new Dictionary<(int Row, int Col), Complex>();
        void Add(int r, int c, Complex val)
        {
            if (r < 0 || c < 0) return;
            cells[(r, c)] = cells.TryGetValue((r, c), out Complex cur) ? cur + val : Complex.Zero + val;
        }
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            int f = index[br.From], t = index[br.To];
            if (kept[f] && kept[t]) continue;   // internal and boundary branches stay as they are
            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
            int lf = local[f], lt = local[t];
            Add(lf, lf, s.Yff);
            Add(lt, lt, s.Ytt);
            Add(lf, lt, s.Yft);
            Add(lt, lf, s.Ytf);
        }
        for (int i = 0; i < model.BusCount; i++)
        {
            Bus b = model.Buses[i];
            if (kept[i] || (b.Gs == 0 && b.Bs == 0)) continue;
            Add(local[i], local[i], new Complex(b.Gs, b.Bs) / model.BaseMva);
        }
        return cells;
    }

    private static void StampSeries(Dictionary<(int Row, int Col), Complex> cells, int a, int b, Complex y)
    {
        void Add(int r, int c, Complex val) =>
            cells[(r, c)] = cells.TryGetValue((r, c), out Complex cur) ? cur + val : Complex.Zero + val;
        Add(a, a, y);
        Add(b, b, y);
        Add(a, b, -y);
        Add(b, a, -y);
    }

    /// <summary>The injected current (Y V)_i at each external bus (rows in column order), zero elsewhere. This is
    /// conj(S_i / V_i) of the spec without the round trip through S.</summary>
    private static Complex[] ExternalCurrents(GridModel model, bool[] kept, Complex[] v)
    {
        SparseYBus y = YBus.BuildSparse(model);
        int n = model.BusCount;
        var rows = new List<(int Col, Complex Val)>[n];
        foreach (KeyValuePair<(int Row, int Col), Complex> c in y.Cells)
        {
            if (kept[c.Key.Row]) continue;
            (rows[c.Key.Row] ??= new List<(int, Complex)>()).Add((c.Key.Col, c.Value));
        }
        var current = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            if (kept[i] || rows[i] is null) continue;
            rows[i].Sort((p, q) => p.Col.CompareTo(q.Col));
            Complex s = Complex.Zero;
            foreach ((int col, Complex val) in rows[i]) s += val * v[col];
            current[i] = s;
        }
        return current;
    }

    /// <summary>The external buses the base case solved as PV (holding their voltage): the solve's own bus types when it
    /// reports them, else the model's PV buses with an in-service generator.</summary>
    private static HashSet<int> ExternalPvBuses(GridModel model, PowerFlowResult solved, bool[] kept)
    {
        var hasGen = new bool[model.BusCount];
        var idx = new Dictionary<int, int>(model.BusCount);
        for (int i = 0; i < model.BusCount; i++) idx[model.Buses[i].Id] = i;
        foreach (Generator g in model.Generators) hasGen[idx[g.Bus]] = true;
        var pv = new HashSet<int>();
        for (int i = 0; i < model.BusCount; i++)
        {
            if (kept[i]) continue;
            BusType t = solved.SolvedBusTypes is { } st ? st[i] : (model.Buses[i].Type == BusType.Pv && hasGen[i] ? BusType.Pv : BusType.Pq);
            if (t is BusType.Pv or BusType.Slack) pv.Add(i);
        }
        return pv;
    }

    /// <summary>The REI groups: (name, generator group?, external bus model indices in model order, S_k pu).</summary>
    private static List<(string Name, bool Generators, List<int> Buses, List<Complex> S)> ReiGroups(GridModel model, ReiGrouping grouping,
        bool[] kept, Complex[] v, Complex[] current, double baseMva)
    {
        int n = model.BusCount;
        var idx = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idx[model.Buses[i].Id] = i;
        var hasGen = new bool[n];
        foreach (Generator g in model.Generators) hasGen[idx[g.Bus]] = true;

        var genBuses = new List<int>();
        var genS = new List<Complex>();
        var loadBuses = new List<int>();
        var loadS = new List<Complex>();
        for (int i = 0; i < n; i++)
        {
            if (kept[i]) continue;
            Bus b = model.Buses[i];
            var load = new Complex(b.Pd, b.Qd) / baseMva;
            if (hasGen[i])
            {
                // The generators' own output: the net injection V conj(I) at the solved state plus the bus load.
                Complex s = v[i] * Complex.Conjugate(current[i]) + load;
                if (s != Complex.Zero) { genBuses.Add(i); genS.Add(s); }
            }
            if (grouping == ReiGrouping.GeneratorsAndLoads && load != Complex.Zero)
            {
                loadBuses.Add(i);
                loadS.Add(-load);
            }
        }
        var groups = new List<(string, bool, List<int>, List<Complex>)> { ("gen", true, genBuses, genS) };
        if (grouping == ReiGrouping.GeneratorsAndLoads) groups.Add(("load", false, loadBuses, loadS));
        return groups;
    }

    /// <summary>
    /// Turn Y_eq and dI over the kept nodes into elements: a branch per pair with y != 0, a row-sum shunt and a
    /// constant-power injection dS = V conj(dI) per node. A kept node with model index -1 (an REI bus) takes its shunt and
    /// injection into <paramref name="extraShunt"/> / <paramref name="extraInjection"/> at its position minus
    /// <paramref name="extraOffset"/>.
    /// </summary>
    private static void AddWardElements(Complex[,] yEq, Complex[] dI, List<Complex> vKeep, List<int> keepIds, List<int> keepModel,
        Complex[] addShunt, Complex[] addInjection, List<Branch> branches, List<EquivalentElement> elements,
        Complex[]? extraShunt = null, Complex[]? extraInjection = null, int extraOffset = 0)
    {
        int m = keepIds.Count;
        for (int a = 0; a < m; a++)
            for (int b = a + 1; b < m; b++)
            {
                Complex y = WardMath.BranchAdmittance(yEq, a, b);
                if (y == Complex.Zero) continue;   // an exact zero only: a small value is kept, or exactness breaks
                Complex z = Complex.One / y;
                string name = "EQ:" + keepIds[a].ToString(CultureInfo.InvariantCulture) + "-" + keepIds[b].ToString(CultureInfo.InvariantCulture);
                branches.Add(new Branch(keepIds[a], keepIds[b], z.Real, z.Imaginary, Name: name));
                elements.Add(new EquivalentElement(EquivalentElementKind.Branch, name, new[] { keepIds[a], keepIds[b] }, y));
            }
        for (int a = 0; a < m; a++)
        {
            Complex s = WardMath.RowSum(yEq, a);
            Complex dS = vKeep[a] * Complex.Conjugate(dI[a]);
            string id = keepIds[a].ToString(CultureInfo.InvariantCulture);
            if (keepModel[a] >= 0) { addShunt[keepModel[a]] += s; addInjection[keepModel[a]] += dS; }
            else { extraShunt![a - extraOffset] += s; extraInjection![a - extraOffset] += dS; }
            if (s != Complex.Zero) elements.Add(new EquivalentElement(EquivalentElementKind.Shunt, "EQ-SH:" + id, new[] { keepIds[a] }, s));
            if (dS != Complex.Zero) elements.Add(new EquivalentElement(EquivalentElementKind.Injection, "EQ-S:" + id, new[] { keepIds[a] }, dS));
        }
    }

    private static void RefuseNonReciprocal(Complex[,] yEq, IReadOnlyList<int> ids)
    {
        if (!WardMath.IsReciprocal(yEq, ReciprocityTolerance, out (int I, int J) p))
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"the equivalent is not reciprocal between buses {ids[p.I]} and {ids[p.J]} (|Y_ij - Y_ji| > {ReciprocityTolerance:R} |Y_ij|): a phase shifter in the external network; out of scope."));
    }

    private static void RefuseExternalElements(GridModel model, Dictionary<int, int> index, bool[] kept)
    {
        bool Ext(int bus) => index.TryGetValue(bus, out int i) && !kept[i];
        var problems = new List<string>();
        for (int i = 0; i < model.BusCount; i++)
            if (!kept[i] && model.Buses[i].Zip is { VariesWithVoltage: true })
                problems.Add($"bus {model.Buses[i].Id} has a ZIP (voltage-dependent) load in the external network; the equivalent is constant power");
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
        foreach (InductionMachine m in model.InductionMachines)
            if (Ext(m.Bus)) problems.Add($"induction machine '{m.Name}' at bus {m.Bus} is in the external network");

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
            throw new InvalidOperationException("the external network holds elements a static equivalent cannot represent: " + string.Join("; ", problems) + ".");
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
