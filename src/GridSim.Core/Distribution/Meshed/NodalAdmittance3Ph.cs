using System.Numerics;

namespace GridSim.Core.Distribution.Meshed;

/// <summary>One unknown of the phase frame: a wire at a node, or an internal one (a source EMF, an earth grid, a floating
/// star point). Internal unknowns have a node name starting with "@".</summary>
public sealed record FrameVariable(string Node, string Wire, bool Internal);

/// <summary>A branch as stamped: the wire variables at each end, the (possibly Kron-reduced) series admittance and the half shunt,
/// kept so the solver can report the currents.</summary>
public sealed class BranchStamp
{
    /// <summary>The model branch.</summary>
    public required FrameBranch Branch { get; init; }
    /// <summary>The wires actually stamped (the branch's wires less the Kron wire).</summary>
    public required IReadOnlyList<string> StampedWires { get; init; }
    /// <summary>Variable index at the From node of each stamped wire.</summary>
    public required int[] FromVars { get; init; }
    /// <summary>Variable index at the To node of each stamped wire.</summary>
    public required int[] ToVars { get; init; }
    /// <summary>Series admittance (siemens) of the stamped wires.</summary>
    public required Complex[,] Y { get; init; }
    /// <summary>Half of the shunt admittance of the stamped wires (siemens), or null.</summary>
    public Complex[,]? HalfShunt { get; init; }
    /// <summary>For a Kron wire: -Znp/Znn per stamped wire, giving the neutral through-current from the phase currents; else null.</summary>
    public Complex[]? KronNeutralFactor { get; init; }
}

/// <summary>A solved-for source series element (EMF behind an impedance).</summary>
public sealed record SourceStamp(FrameSource Source, int WireIndex, int EmfVar, int NodeVar, Complex Y);

/// <summary>An earthing element as stamped: variable, the grid variable or -1 for remote earth, and its admittance.</summary>
public sealed record EarthingStamp(EarthingElement Element, int Var, int GridVar, Complex Y, bool Solid);

/// <summary>A transformer unit as stamped: its winding terminals and the winding admittance matrix.</summary>
public sealed class UnitStamp
{
    /// <summary>The unit.</summary>
    public required FrameTransformerUnit Unit { get; init; }
    /// <summary>Positive terminal variable of each winding.</summary>
    public required int[] PVars { get; init; }
    /// <summary>Negative terminal variable of each winding, -1 for earth.</summary>
    public required int[] QVars { get; init; }
    /// <summary>The winding admittance matrix (siemens): winding currents from winding voltages.</summary>
    public required Complex[,] Yw { get; init; }
}

/// <summary>A load leg resolved to variables: current flows from <see cref="P"/> to <see cref="Q"/> (-1 earth).</summary>
public sealed record LegStamp(int Load, int Leg, int P, int Q);

/// <summary>Whether a component of the network that no source, earthing or earth-referenced element anchors is refused (the
/// default: INCOMPLETE, named) or anchored explicitly by pinning its first wire at 0 V (recorded in the notes).</summary>
public enum FloatingPolicy
{
    /// <summary>Report INCOMPLETE naming the unreferenced wires.</summary>
    Refuse,
    /// <summary>Pin the first wire of each unreferenced component at 0 V (a gauge choice; only differences are physical there).</summary>
    PinFirstWire,
}

/// <summary>
/// The primitive nodal admittance of a <see cref="PhaseFrameModel"/>, built in element order: branches, then
/// transformer units, then source series elements, then earthing elements, then (in the solver) the loads. Nothing is
/// eliminated except a branch's declared Kron wire. Rows are kept as sorted sparse maps so the order of every sum is fixed.
/// </summary>
public sealed class NodalAdmittance3Ph
{
    private readonly List<FrameVariable> _vars = new();
    private readonly Dictionary<(string, string), int> _index = new();

    /// <summary>All unknowns, node wires first (in model order), then source EMFs, grids and star points.</summary>
    public IReadOnlyList<FrameVariable> Variables => _vars;
    /// <summary>Number of unknowns (free and fixed).</summary>
    public int Count => _vars.Count;
    /// <summary>True where the unknown is fixed (source wire, solid earth, EMF, pinned).</summary>
    public bool[] IsFixed { get; private set; } = Array.Empty<bool>();
    /// <summary>The fixed values (volts); meaningful where <see cref="IsFixed"/>.</summary>
    public Complex[] FixedValue { get; private set; } = Array.Empty<Complex>();
    /// <summary>Sparse admittance rows (siemens), column-sorted.</summary>
    public SortedDictionary<int, Complex>[] Y { get; private set; } = Array.Empty<SortedDictionary<int, Complex>>();
    /// <summary>Named reasons the case cannot be solved as given (empty when it can).</summary>
    public List<string> Incomplete { get; } = new();
    /// <summary>Recorded modelling decisions: Kron reductions and any pinned reference.</summary>
    public List<string> Notes { get; } = new();
    /// <summary>Branch stamps in model order.</summary>
    public List<BranchStamp> Branches { get; } = new();
    /// <summary>Transformer unit stamps in model order.</summary>
    public List<UnitStamp> Units { get; } = new();
    /// <summary>Source series stamps.</summary>
    public List<SourceStamp> SourceSeries { get; } = new();
    /// <summary>Earthing stamps.</summary>
    public List<EarthingStamp> EarthingStamps { get; } = new();
    /// <summary>Load legs resolved to variables (zero-power legs are skipped).</summary>
    public List<LegStamp> Legs { get; } = new();
    /// <summary>Variable of each earth grid, by grid name.</summary>
    public Dictionary<string, int> GridVars { get; } = new(StringComparer.Ordinal);
    /// <summary>Variable of each floating star point, by load index.</summary>
    public Dictionary<int, int> StarVars { get; } = new();

    private bool[] _refStamp = Array.Empty<bool>();

    /// <summary>Index of the variable for a wire at a node, or -1.</summary>
    public int Find(string node, string wire) => _index.TryGetValue((node, wire), out int i) ? i : -1;

    private int Add(string node, string wire, bool internalVar)
    {
        int i = _vars.Count;
        _vars.Add(new FrameVariable(node, wire, internalVar));
        _index[(node, wire)] = i;
        return i;
    }

    private void Stamp(int row, int col, Complex v)
    {
        if (row < 0 || col < 0 || v == Complex.Zero) return;
        SortedDictionary<int, Complex> r = Y[row];
        r[col] = r.TryGetValue(col, out Complex old) ? old + v : v;
    }

    /// <summary>Stamp a two-terminal-per-port admittance: terminals (p_k, q_k) with port admittance matrix <paramref name="yw"/>.</summary>
    private void StampPorts(int[] p, int[] q, Complex[,] yw)
    {
        int n = p.Length;
        for (int k = 0; k < n; k++)
            for (int l = 0; l < n; l++)
            {
                Complex v = yw[k, l];
                Stamp(p[k], p[l], v);
                Stamp(p[k], q[l], -v);
                Stamp(q[k], p[l], -v);
                Stamp(q[k], q[l], v);
            }
    }

    /// <summary>Build the primitive admittance of <paramref name="model"/>.</summary>
    public static NodalAdmittance3Ph Build(PhaseFrameModel model, FloatingPolicy floating = FloatingPolicy.Refuse)
    {
        var na = new NodalAdmittance3Ph();
        na.BuildCore(model, floating);
        return na;
    }

    private void BuildCore(PhaseFrameModel m, FloatingPolicy floating)
    {
        foreach (FrameNode n in m.Nodes)
            foreach (string w in n.Wires) Add(n.Name, w, false);
        // Source EMFs, grids, star points get unknowns after the node wires, in model order.
        for (int s = 0; s < m.Sources.Count; s++)
            if (m.Sources[s].SeriesZ is not null)
                foreach (string w in m.Sources[s].Wires) Add("@emf:" + s.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + m.Sources[s].Node, w, true);
        foreach (EarthGrid g in m.Grids) GridVars[g.Name] = Add("@grid", g.Name, true);
        for (int l = 0; l < m.Loads.Count; l++)
            if (m.Loads[l].Connection == FrameConnection.WyeFloating) StarVars[l] = Add("@star", m.Loads[l].Name, true);

        int count = _vars.Count;
        IsFixed = new bool[count];
        FixedValue = new Complex[count];
        _refStamp = new bool[count];
        Y = new SortedDictionary<int, Complex>[count];
        for (int i = 0; i < count; i++) Y[i] = new SortedDictionary<int, Complex>();
        var uf = new int[count];
        for (int i = 0; i < count; i++) uf[i] = i;
        int Root(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
        void Union(int a, int b) { if (a < 0 || b < 0) return; int ra = Root(a), rb = Root(b); if (ra != rb) { if (ra < rb) uf[rb] = ra; else uf[ra] = rb; } }
        void Fix(int v, Complex value, string what)
        {
            if (IsFixed[v] && FixedValue[v] != value)
                throw new ArgumentException($"{what} fixes {Describe(v)} to a value that conflicts with another fixed value.");
            IsFixed[v] = true;
            FixedValue[v] = value;
        }

        // 1. Branches.
        foreach (FrameBranch b in m.Branches)
        {
            int k = b.Wires.Count;
            int kron = b.KronWire is null ? -1 : IndexOf(b.Wires, b.KronWire);
            var keep = new List<int>();
            for (int i = 0; i < k; i++) if (i != kron) keep.Add(i);
            int q = keep.Count;
            var z = new Complex[q, q];
            Complex[]? kronFactor = null;
            if (kron >= 0)
            {
                Complex znn = b.Z[kron, kron];
                if (znn == Complex.Zero)
                {
                    Incomplete.Add($"branch '{b.Name}': Kron wire '{b.KronWire}' has zero self impedance, so it cannot be reduced.");
                    continue;
                }
                kronFactor = new Complex[q];
                for (int i = 0; i < q; i++)
                {
                    kronFactor[i] = -b.Z[kron, keep[i]] / znn;
                    for (int j = 0; j < q; j++) z[i, j] = b.Z[keep[i], keep[j]] - b.Z[keep[i], kron] * b.Z[kron, keep[j]] / znn;
                }
                Notes.Add($"Kron reduction: wire '{b.KronWire}' of branch '{b.Name}' eliminated (declared multi-grounded neutral, taken at earth potential along the branch).");
            }
            else
                for (int i = 0; i < q; i++) for (int j = 0; j < q; j++) z[i, j] = b.Z[keep[i], keep[j]];

            Complex[,]? y = SmallComplexMatrix.Invert(z, q);
            if (y is null)
            {
                Incomplete.Add($"branch '{b.Name}': the impedance matrix is singular (a zero-impedance switch or identical conductors); give it a small non-zero impedance.");
                continue;
            }
            Complex[,]? half = null;
            if (b.ShuntY is { } sh)
            {
                half = new Complex[q, q];
                for (int i = 0; i < q; i++) for (int j = 0; j < q; j++) half[i, j] = sh[keep[i], keep[j]] * 0.5;
            }
            var wires = new string[q];
            var fv = new int[q];
            var tv = new int[q];
            for (int i = 0; i < q; i++)
            {
                wires[i] = b.Wires[keep[i]];
                fv[i] = Find(b.From, wires[i]);
                tv[i] = Find(b.To, wires[i]);
                Union(fv[i], tv[i]);
            }
            for (int i = 0; i < q; i++)
                for (int j = 0; j < q; j++)
                {
                    Complex v = y[i, j];
                    Stamp(fv[i], fv[j], v); Stamp(fv[i], tv[j], -v); Stamp(tv[i], fv[j], -v); Stamp(tv[i], tv[j], v);
                    if (half is not null)
                    {
                        Complex h = half[i, j];
                        Stamp(fv[i], fv[j], h); Stamp(tv[i], tv[j], h);
                    }
                }
            if (half is not null)
                for (int i = 0; i < q; i++)
                {
                    bool any = false;
                    for (int j = 0; j < q; j++) if (half[i, j] != Complex.Zero) any = true;
                    if (any) { _refStamp[fv[i]] = true; _refStamp[tv[i]] = true; }
                }
            Branches.Add(new BranchStamp { Branch = b, StampedWires = wires, FromVars = fv, ToVars = tv, Y = y, HalfShunt = half, KronNeutralFactor = kronFactor });
        }

        // 2. Transformer units.
        foreach (FrameTransformerUnit u in m.Transformers)
        {
            int nw = u.Windings.Count;
            var pv = new int[nw];
            var qv = new int[nw];
            bool ok = true;
            for (int k = 0; k < nw; k++)
            {
                FrameWinding w = u.Windings[k];
                pv[k] = Find(w.Node, w.Wire);
                string retNode = w.ReturnNode ?? w.Node;
                qv[k] = w.ReturnWire is null ? -1 : Find(retNode, w.ReturnWire);
                if (pv[k] < 0 || (w.ReturnWire is not null && qv[k] < 0))
                {
                    Incomplete.Add(pv[k] < 0
                        ? $"transformer unit '{u.Name}': winding {k + 1} needs wire '{w.Wire}' at node '{w.Node}', which has no such wire (no return path for the winding)."
                        : $"transformer unit '{u.Name}': winding {k + 1} needs wire '{w.ReturnWire}' at node '{retNode}', which has no such wire (no return path for the winding).");
                    ok = false;
                }
            }
            if (!ok) continue;
            // v_k = t_k E + z_k i_k and sum(t_k i_k) = 0  =>  i = (D - (D t)(D t)^T / (t^T D t)) v with D = diag(1/z).
            var d = new Complex[nw];
            var dt = new Complex[nw];
            Complex tdt = Complex.Zero;
            for (int k = 0; k < nw; k++)
            {
                d[k] = Complex.One / u.Windings[k].Z;
                dt[k] = d[k] * u.Windings[k].Turns;
                tdt += dt[k] * u.Windings[k].Turns;
            }
            // R4-15b: a magnetising branch adds MagnetisingY t1^2 to the flux denominator (zero by default: unchanged arithmetic).
            Complex den = tdt;
            if (u.MagnetisingY != Complex.Zero) den = tdt + u.MagnetisingY * (u.Windings[0].Turns * u.Windings[0].Turns);
            var yw = new Complex[nw, nw];
            for (int k = 0; k < nw; k++)
                for (int l = 0; l < nw; l++)
                    yw[k, l] = (k == l ? d[k] : Complex.Zero) - dt[k] * dt[l] / den;
            StampPorts(pv, qv, yw);
            for (int k = 0; k < nw; k++) { Union(pv[k], qv[k]); if (qv[k] < 0) _refStamp[pv[k]] = true; }
            Units.Add(new UnitStamp { Unit = u, PVars = pv, QVars = qv, Yw = yw });
        }

        // 3. Sources: an ideal source fixes its wires; a source behind an impedance fixes an EMF and stamps the impedance.
        for (int s = 0; s < m.Sources.Count; s++)
        {
            FrameSource src = m.Sources[s];
            for (int w = 0; w < src.Wires.Count; w++)
            {
                int nv = Find(src.Node, src.Wires[w]);
                if (nv < 0)
                {
                    Incomplete.Add($"source at node '{src.Node}' names wire '{src.Wires[w]}', which the node does not have.");
                    continue;
                }
                if (src.SeriesZ is null || src.SeriesZ[w] == Complex.Zero)
                {
                    Fix(nv, src.Voltage[w], $"the source at '{src.Node}'");
                    continue;
                }
                int ev = Find("@emf:" + s.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + src.Node, src.Wires[w]);
                Fix(ev, src.Voltage[w], $"the source at '{src.Node}'");
                Complex y = Complex.One / src.SeriesZ[w];
                Stamp(nv, nv, y); Stamp(nv, ev, -y); Stamp(ev, nv, -y); Stamp(ev, ev, y);
                Union(nv, ev);
                SourceSeries.Add(new SourceStamp(src, w, ev, nv, y));
            }
            if (src.SeriesZ is not null)
                for (int w = 0; w < src.Wires.Count; w++)
                    if (src.SeriesZ[w] == Complex.Zero) { /* already fixed directly; the unused EMF is fixed too so it is not free */ int ev = Find("@emf:" + s.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + src.Node, src.Wires[w]); if (ev >= 0 && !IsFixed[ev]) Fix(ev, src.Voltage[w], "the source"); }
        }

        // 4. Earthing and grids.
        foreach (EarthGrid g in m.Grids)
        {
            int gv = GridVars[g.Name];
            Complex y = Complex.One / g.Z;
            Stamp(gv, gv, y);
            _refStamp[gv] = true;
        }
        foreach (EarthingElement e in m.Earthing)
        {
            int v = Find(e.Node, e.Wire);
            if (e.Grid is not null)
            {
                int gv = GridVars[e.Grid];
                Complex y = Complex.One / e.Z;
                Stamp(v, v, y); Stamp(v, gv, -y); Stamp(gv, v, -y); Stamp(gv, gv, y);
                Union(v, gv);
                EarthingStamps.Add(new EarthingStamp(e, v, gv, y, false));
            }
            else if (e.Z == Complex.Zero)
            {
                Fix(v, Complex.Zero, $"solid earthing of '{e.Node}'/'{e.Wire}'");
                EarthingStamps.Add(new EarthingStamp(e, v, -1, Complex.Zero, true));
            }
            else
            {
                Complex y = Complex.One / e.Z;
                Stamp(v, v, y);
                _refStamp[v] = true;
                EarthingStamps.Add(new EarthingStamp(e, v, -1, y, false));
            }
        }

        // 5. Load legs (the loads themselves are evaluated by the solver; only the topology is resolved here).
        for (int l = 0; l < m.Loads.Count; l++)
        {
            FrameLoad ld = m.Loads[l];
            for (int k = 0; k < ld.Legs.Count; k++)
            {
                if (ld.S[k] == Complex.Zero) continue;
                FrameLeg leg = ld.Legs[k];
                int p = Find(ld.Node, leg.From);
                if (p < 0)
                {
                    Incomplete.Add(LegMessage(ld, leg.From));
                    continue;
                }
                int qv;
                if (leg.To is not null)
                {
                    qv = Find(ld.Node, leg.To);
                    if (qv < 0) { Incomplete.Add(LegMessage(ld, leg.To)); continue; }
                }
                else qv = ld.Connection == FrameConnection.WyeFloating ? StarVars[l] : -1;
                Legs.Add(new LegStamp(l, k, p, qv));
                Union(p, qv);
                if (qv < 0) _refStamp[p] = true;
            }
        }

        // 6. Every connected component needs a reference: a fixed unknown, a path to earth, or an earth-referenced leg.
        var referenced = new bool[count];
        for (int i = 0; i < count; i++)
            if (IsFixed[i] || _refStamp[i]) referenced[Root(i)] = true;
        var named = new SortedDictionary<int, List<int>>();
        for (int i = 0; i < count; i++)
        {
            int r = Root(i);
            if (referenced[r]) continue;
            if (!named.TryGetValue(r, out List<int>? list)) named[r] = list = new List<int>();
            list.Add(i);
        }
        foreach (KeyValuePair<int, List<int>> kv in named)
        {
            string names = string.Join(", ", kv.Value.Select(Describe));
            if (floating == FloatingPolicy.PinFirstWire)
            {
                int pin = kv.Value[0];
                IsFixed[pin] = true;
                FixedValue[pin] = Complex.Zero;
                Notes.Add($"reference pinned: {Describe(pin)} held at 0 V because the connected wires [{names}] have no reference to earth (only differences are physical there).");
            }
            else
                Incomplete.Add($"no reference: the wires [{names}] are not fixed by a source, not earthed and not tied to earth by a load, so their potential is undefined (a neutral without a reference); earth the neutral, or accept a pinned reference.");
        }
    }

    private static int IndexOf(IReadOnlyList<string> list, string v)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == v) return i;
        return -1;
    }

    private string Describe(int v) => _vars[v].Internal ? $"{_vars[v].Node}/{_vars[v].Wire}" : $"{_vars[v].Node}.{_vars[v].Wire}";

    private static string LegMessage(FrameLoad ld, string missing) => ld.Connection switch
    {
        FrameConnection.Delta => $"delta load '{ld.Name}' at node '{ld.Node}' has no return path: wire '{missing}' does not exist there.",
        FrameConnection.Wye => $"wye load '{ld.Name}' at node '{ld.Node}' needs wire '{missing}': the neutral has no reference at that node.",
        _ => $"load '{ld.Name}' at node '{ld.Node}' needs wire '{missing}', which does not exist there.",
    };
}

/// <summary>Small dense complex matrix helpers for primitive branch and winding matrices (at most a few rows).</summary>
internal static class SmallComplexMatrix
{
    private static double Mag(Complex c) => Math.Sqrt(c.Real * c.Real + c.Imaginary * c.Imaginary);

    /// <summary>The inverse by Gauss-Jordan elimination with partial pivoting, or null when singular (a pivot below 1e-14 of the largest entry).</summary>
    public static Complex[,]? Invert(Complex[,] a, int n)
    {
        var m = new Complex[n, 2 * n];
        double scale = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) { m[i, j] = a[i, j]; scale = Math.Max(scale, Mag(a[i, j])); }
            m[i, n + i] = Complex.One;
        }
        if (scale == 0) return null;
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            double best = Mag(m[c, c]);
            for (int r = c + 1; r < n; r++) { double v = Mag(m[r, c]); if (v > best) { best = v; piv = r; } }
            if (best <= 1e-14 * scale) return null;
            if (piv != c)
                for (int j = 0; j < 2 * n; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
            Complex d = m[c, c];
            for (int j = 0; j < 2 * n; j++) m[c, j] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                Complex f = m[r, c];
                if (f == Complex.Zero) continue;
                for (int j = 0; j < 2 * n; j++) m[r, j] -= f * m[c, j];
            }
        }
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }
}
