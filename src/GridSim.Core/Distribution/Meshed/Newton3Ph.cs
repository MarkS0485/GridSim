using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Distribution.Meshed;

/// <summary>Terminal state of a meshed unbalanced solve (the RC3 study-verb vocabulary).</summary>
public enum MeshedStatus
{
    /// <summary>The largest current mismatch at any free unknown is within the tolerance.</summary>
    Converged,
    /// <summary>The fixed iteration limit was reached first; the state reported is the last iterate.</summary>
    MaxIterations,
    /// <summary>A NaN or infinity appeared (for example a constant-power leg at exactly zero voltage).</summary>
    NonFinite,
    /// <summary>The case is not solvable as given: a reference, return path or wire is missing, or the Jacobian is singular. The reasons are named.</summary>
    Incomplete,
}

/// <summary>Options of the meshed solve. The iteration policy is fixed: plain Newton, full step, no damping or line search.</summary>
public sealed record MeshedOptions
{
    /// <summary>Convergence limit on the largest current mismatch (amperes) over the free unknowns. Default 1e-6.</summary>
    public double ToleranceAmps { get; init; } = 1e-6;
    /// <summary>The iteration limit. Default 40.</summary>
    public int MaxIterations { get; init; } = 40;
    /// <summary>Factor on every load's power (1 = as declared).</summary>
    public double GlobalLoadScale { get; init; } = 1.0;
    /// <summary>Per-load factor by load name, multiplied on top of <see cref="GlobalLoadScale"/>.</summary>
    public IReadOnlyDictionary<string, double>? LoadScale { get; init; }
    /// <summary>A previous solution's state (one voltage per unknown, as in <see cref="MeshedResult.State"/>) to start from.
    /// Null (default): the no-load solution, which is the flat per-phase start whenever there are no transformers or shunts.</summary>
    public IReadOnlyList<Complex>? InitialState { get; init; }
    /// <summary>What to do with a component that has no reference (default: refuse).</summary>
    public FloatingPolicy Floating { get; init; } = FloatingPolicy.Refuse;

    /// <summary>Refuse unusable options.</summary>
    public void Validate()
    {
        if (!(ToleranceAmps > 0 && double.IsFinite(ToleranceAmps))) throw new ArgumentException("ToleranceAmps must be finite and above 0.");
        if (MaxIterations < 1) throw new ArgumentException("MaxIterations must be at least 1.");
        if (!(GlobalLoadScale >= 0 && double.IsFinite(GlobalLoadScale))) throw new ArgumentException("GlobalLoadScale must be finite and not negative.");
        if (LoadScale is not null)
            foreach (KeyValuePair<string, double> kv in LoadScale)
                if (!(kv.Value >= 0 && double.IsFinite(kv.Value))) throw new ArgumentException($"load scale for '{kv.Key}' must be finite and not negative.");
    }
}

/// <summary>Voltage of one wire at one node (volts to earth).</summary>
public sealed record WireVoltage(string Node, string Wire, Complex V);

/// <summary>
/// Currents of a branch per conductor, amperes. <see cref="IFrom"/> enters the branch at its From node, <see cref="ITo"/> leaves it
/// at its To node (they differ only by the shunt charging). <see cref="Residual"/> is the sum of the From-end currents of all the
/// branch's conductors, neutral included: the current that must return through earth (for a single-wire earth-return line it
/// is the line current itself). A Kron-reduced neutral's current is the through-current implied by the reduction.
/// </summary>
public sealed record BranchCurrents(string Name, IReadOnlyList<string> Wires, IReadOnlyList<Complex> IFrom,
    IReadOnlyList<Complex> ITo, Complex Residual);

/// <summary>A load's legs: voltage across each, current through each (From to To), and power drawn.</summary>
public sealed record LoadState(string Name, string Node, IReadOnlyList<Complex> Voltage, IReadOnlyList<Complex> Current,
    IReadOnlyList<Complex> Power);

/// <summary>Current a source delivers into the network at one of its wires (the EMF side for a source behind an impedance).</summary>
public sealed record SourceCurrent(string Node, string Wire, Complex Current);

/// <summary>Current an earthing element passes to earth (or to its grid), and the voltage of the wire it grounds.</summary>
public sealed record EarthingCurrent(string Node, string Wire, string? Grid, Complex WireVoltage, Complex Current);

/// <summary>A grid: its potential above remote earth (ground potential rise) and the current it returns to remote earth.</summary>
public sealed record GridState(string Name, Complex Voltage, Complex Current);

/// <summary>A transformer unit's winding currents (into each winding's positive terminal).</summary>
public sealed record UnitCurrents(string Name, IReadOnlyList<Complex> WindingVoltage, IReadOnlyList<Complex> WindingCurrent);

/// <summary>The solved phase-frame state and its reporting.</summary>
public sealed class MeshedResult
{
    /// <summary>Terminal state.</summary>
    public required MeshedStatus Status { get; init; }
    /// <summary>Newton steps taken.</summary>
    public required int Iterations { get; init; }
    /// <summary>Largest current mismatch (amperes) at the reported state, over the free unknowns.</summary>
    public required double MismatchAmps { get; init; }
    /// <summary>Why the case is INCOMPLETE (empty otherwise).</summary>
    public required IReadOnlyList<string> IncompleteReasons { get; init; }
    /// <summary>Modelling decisions recorded while building (Kron reductions, pinned references, start used).</summary>
    public required IReadOnlyList<string> Notes { get; init; }
    /// <summary>Every unknown in order (node wires first, then internal ones).</summary>
    public required IReadOnlyList<FrameVariable> Variables { get; init; }
    /// <summary>One voltage per unknown: feed this back as <see cref="MeshedOptions.InitialState"/> to warm-start.</summary>
    public required IReadOnlyList<Complex> State { get; init; }
    /// <summary>Voltages of every wire at every node, in model order.</summary>
    public required IReadOnlyList<WireVoltage> Voltages { get; init; }
    /// <summary>Per branch currents including the neutral and the earth-return residual.</summary>
    public required IReadOnlyList<BranchCurrents> Branches { get; init; }
    /// <summary>Per load leg voltages, currents and powers.</summary>
    public required IReadOnlyList<LoadState> Loads { get; init; }
    /// <summary>Source currents.</summary>
    public required IReadOnlyList<SourceCurrent> Sources { get; init; }
    /// <summary>Earthing element currents.</summary>
    public required IReadOnlyList<EarthingCurrent> Earthing { get; init; }
    /// <summary>Earth grid potentials and currents.</summary>
    public required IReadOnlyList<GridState> Grids { get; init; }
    /// <summary>Transformer unit winding currents.</summary>
    public required IReadOnlyList<UnitCurrents> Units { get; init; }

    /// <summary>The voltage of a wire at a node (throws if it does not exist).</summary>
    public Complex V(string node, string wire)
    {
        foreach (WireVoltage w in Voltages) if (w.Node == node && w.Wire == wire) return w.V;
        throw new KeyNotFoundException($"no wire '{wire}' at node '{node}'.");
    }

    /// <summary>The currents of a branch by name (throws if unknown).</summary>
    public BranchCurrents Branch(string name)
    {
        foreach (BranchCurrents b in Branches) if (b.Name == name) return b;
        throw new KeyNotFoundException($"no branch '{name}'.");
    }

    /// <summary>The state of a load by name (throws if unknown).</summary>
    public LoadState Load(string name)
    {
        foreach (LoadState l in Loads) if (l.Name == name) return l;
        throw new KeyNotFoundException($"no load '{name}'.");
    }
}

/// <summary>
/// The current-injection Newton solver of the phase frame. Unknowns are the free wire voltages (rectangular, real and imaginary
/// parts); the equations are Kirchhoff's current law at every free unknown, written in amperes: the current the network draws
/// from the unknown (the primitive admittance times the voltages) plus the load legs' currents is zero. Order: validate wires and
/// earthing -> build the primitive admittance in element order -> eliminate nothing but declared Kron wires -> initial voltages
/// (the no-load solution, flat per phase when there are no transformers or shunts; flat by wire name if that is singular) ->
/// Newton with the fixed policy of <see cref="MeshedOptions"/> -> residual in amperes -> report. Every sum runs in a fixed order,
/// so a repeat run is bit-identical.
/// </summary>
public static class Newton3Ph
{
    /// <summary>Solve <paramref name="model"/>.</summary>
    public static MeshedResult Solve(PhaseFrameModel model, MeshedOptions? options = null)
    {
        MeshedOptions opt = options ?? new MeshedOptions();
        opt.Validate();
        NodalAdmittance3Ph na = NodalAdmittance3Ph.Build(model, opt.Floating);
        if (na.Incomplete.Count > 0)
            return Report(model, na, opt, new Complex[na.Count], MeshedStatus.Incomplete, 0, double.NaN, na.Incomplete, na.Notes.ToList());

        int n = na.Count;
        var notes = na.Notes.ToList();
        var free = new int[n];
        var freeList = new List<int>();
        for (int i = 0; i < n; i++) { free[i] = -1; if (!na.IsFixed[i]) { free[i] = freeList.Count; freeList.Add(i); } }
        int m = freeList.Count;

        // Load power scales (declared order).
        var scale = new double[model.Loads.Count];
        for (int l = 0; l < scale.Length; l++)
        {
            double s = opt.GlobalLoadScale;
            if (opt.LoadScale is not null && opt.LoadScale.TryGetValue(model.Loads[l].Name, out double f)) s *= f;
            scale[l] = s;
        }
        if (opt.LoadScale is not null)
            foreach (string name in opt.LoadScale.Keys)
                if (!model.Loads.Any(x => x.Name == name)) throw new ArgumentException($"load scale names load '{name}', which the model does not have.");

        var v = new Complex[n];
        for (int i = 0; i < n; i++) if (na.IsFixed[i]) v[i] = na.FixedValue[i];

        if (opt.InitialState is { } init)
        {
            if (init.Count != n) throw new ArgumentException($"InitialState has {init.Count} values but the model has {n} unknowns.");
            for (int i = 0; i < n; i++) if (!na.IsFixed[i]) v[i] = init[i];
            notes.Add("start: previous state supplied by the caller.");
        }
        else if (!NoLoadStart(na, v, free, m))
        {
            FlatStart(model, na, v);
            notes.Add("start: flat per phase (the no-load linear system is singular).");
        }
        else notes.Add("start: no-load solution (flat per phase when there are no transformers or shunts).");

        MeshedStatus status = MeshedStatus.MaxIterations;
        int iter = 0;
        double mismatch = double.NaN;
        var reasons = new List<string>();
        var legCurrent = new Complex[na.Legs.Count];
        var rFull = new Complex[n];
        for (; ; iter++)
        {
            mismatch = Residual(model, na, v, scale, free, legCurrent, rFull);
            if (!double.IsFinite(mismatch)) { status = MeshedStatus.NonFinite; break; }
            if (mismatch <= opt.ToleranceAmps) { status = MeshedStatus.Converged; break; }
            if (iter >= opt.MaxIterations) { status = MeshedStatus.MaxIterations; break; }

            CsrMatrix j = Jacobian(model, na, v, scale, free, m);
            var rhs = new double[2 * m];
            for (int k = 0; k < m; k++)
            {
                Complex r = rFull[freeList[k]];
                rhs[2 * k] = -r.Real;
                rhs[2 * k + 1] = -r.Imaginary;
            }
            double[]? dx = m == 0 ? Array.Empty<double>() : SparseLu.Solve(j, rhs);
            if (dx is null)
            {
                status = MeshedStatus.Incomplete;
                reasons.Add("the Newton Jacobian is singular at iteration " + (iter + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ": some wire potential is undetermined (a neutral or star point with no reference) or a load leg sits at zero voltage.");
                break;
            }
            for (int k = 0; k < m; k++) v[freeList[k]] += new Complex(dx[2 * k], dx[2 * k + 1]);
        }
        return Report(model, na, opt, v, status, iter, mismatch, reasons, notes, scale);
    }

    // ---- start -------------------------------------------------------------------------------------------------

    private static bool NoLoadStart(NodalAdmittance3Ph na, Complex[] v, int[] free, int m)
    {
        if (m == 0) return true;
        var b = new CsrMatrix.Builder(2 * m);
        var rhs = new double[2 * m];
        for (int i = 0; i < na.Count; i++)
        {
            int fi = free[i];
            if (fi < 0) continue;
            Complex fixedSum = Complex.Zero;
            foreach (KeyValuePair<int, Complex> e in na.Y[i])
            {
                int fj = free[e.Key];
                if (fj >= 0) AddBlock(b, fi, fj, e.Value, Complex.Zero);
                else fixedSum += e.Value * v[e.Key];
            }
            rhs[2 * fi] = -fixedSum.Real;
            rhs[2 * fi + 1] = -fixedSum.Imaginary;
        }
        double[]? x = SparseLu.Solve(b.Build(), rhs);
        if (x is null) return false;
        for (int k = 0; k < x.Length; k++) if (!double.IsFinite(x[k])) return false;
        for (int i = 0; i < na.Count; i++) if (free[i] >= 0) v[i] = new Complex(x[2 * free[i]], x[2 * free[i] + 1]);
        return true;
    }

    private static void FlatStart(PhaseFrameModel model, NodalAdmittance3Ph na, Complex[] v)
    {
        var byWire = new Dictionary<string, Complex>(StringComparer.Ordinal);
        foreach (FrameSource s in model.Sources)
            for (int w = 0; w < s.Wires.Count; w++) byWire.TryAdd(s.Wires[w], s.Voltage[w]);
        for (int i = 0; i < na.Count; i++)
        {
            if (na.IsFixed[i]) continue;
            FrameVariable fv = na.Variables[i];
            v[i] = !fv.Internal && byWire.TryGetValue(fv.Wire, out Complex c) ? c : Complex.Zero;
        }
    }

    // ---- residual and Jacobian ----------------------------------------------------------------------------------

    private static Complex LegVoltage(NodalAdmittance3Ph na, Complex[] v, LegStamp g)
        => g.Q >= 0 ? v[g.P] - v[g.Q] : v[g.P];

    /// <summary>The ZIP current of a leg and its derivative dI = A du + B conj(du) with respect to the leg voltage u.</summary>
    internal static Complex LegCurrent(Complex s, double v0, Zip zip, Complex u, out Complex a, out Complex b)
    {
        Complex i = Complex.Zero;
        a = Complex.Zero;
        b = Complex.Zero;
        double um2 = u.Real * u.Real + u.Imaginary * u.Imaginary;
        if (zip.P != 0)
        {
            Complex sp = s * zip.P;
            if (um2 == 0) { double nan = double.NaN; return new Complex(nan, nan); }
            Complex cu = Complex.Conjugate(u);
            i += Complex.Conjugate(sp) / cu;
            b += -Complex.Conjugate(sp) / (cu * cu);
        }
        if (zip.I != 0)
        {
            Complex k = Complex.Conjugate(s * zip.I) / v0;
            if (um2 == 0) { double nan = double.NaN; return new Complex(nan, nan); }
            double um = Math.Sqrt(um2);
            i += k * u / um;
            a += k / (2.0 * um);
            b += -(k * u * u) / (2.0 * um * um2);
        }
        if (zip.Z != 0)
        {
            Complex y = Complex.Conjugate(s * zip.Z) / (v0 * v0);
            i += y * u;
            a += y;
        }
        return i;
    }

    private static double Residual(PhaseFrameModel model, NodalAdmittance3Ph na, Complex[] v, double[] scale, int[] free,
        Complex[] legCurrent, Complex[] rFull)
    {
        int n = na.Count;
        for (int i = 0; i < n; i++)
        {
            Complex s = Complex.Zero;
            foreach (KeyValuePair<int, Complex> e in na.Y[i]) s += e.Value * v[e.Key];
            rFull[i] = s;
        }
        for (int t = 0; t < na.Legs.Count; t++)
        {
            LegStamp g = na.Legs[t];
            FrameLoad ld = model.Loads[g.Load];
            Complex u = LegVoltage(na, v, g);
            Complex cur = LegCurrent(ld.S[g.Leg] * scale[g.Load], ld.NominalV, ld.Zip, u, out _, out _);
            legCurrent[t] = cur;
            rFull[g.P] += cur;
            if (g.Q >= 0) rFull[g.Q] -= cur;
        }
        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            if (free[i] < 0) continue;
            Complex r = rFull[i];
            double mag = Math.Sqrt(r.Real * r.Real + r.Imaginary * r.Imaginary);
            if (!double.IsFinite(mag)) return double.NaN;
            if (mag > worst) worst = mag;
        }
        return worst;
    }

    /// <summary>Add the real 2x2 block of dI = A du + B conj(du) at free row/column indices (interleaved re, im).</summary>
    private static void AddBlock(CsrMatrix.Builder b, int fi, int fj, Complex a, Complex bb, double sign = 1.0)
    {
        double r00 = a.Real + bb.Real, r01 = -a.Imaginary + bb.Imaginary, r10 = a.Imaginary + bb.Imaginary, r11 = a.Real - bb.Real;
        b.Add(2 * fi, 2 * fj, sign * r00);
        b.Add(2 * fi, 2 * fj + 1, sign * r01);
        b.Add(2 * fi + 1, 2 * fj, sign * r10);
        b.Add(2 * fi + 1, 2 * fj + 1, sign * r11);
    }

    private static CsrMatrix Jacobian(PhaseFrameModel model, NodalAdmittance3Ph na, Complex[] v, double[] scale, int[] free, int m)
    {
        var b = new CsrMatrix.Builder(2 * m);
        for (int i = 0; i < na.Count; i++)
        {
            int fi = free[i];
            if (fi < 0) continue;
            foreach (KeyValuePair<int, Complex> e in na.Y[i])
            {
                int fj = free[e.Key];
                if (fj >= 0) AddBlock(b, fi, fj, e.Value, Complex.Zero);
            }
        }
        for (int t = 0; t < na.Legs.Count; t++)
        {
            LegStamp g = na.Legs[t];
            FrameLoad ld = model.Loads[g.Load];
            Complex u = LegVoltage(na, v, g);
            LegCurrent(ld.S[g.Leg] * scale[g.Load], ld.NominalV, ld.Zip, u, out Complex a, out Complex bb);
            int fp = free[g.P], fq = g.Q >= 0 ? free[g.Q] : -1;
            if (fp >= 0) AddBlock(b, fp, fp, a, bb);
            if (fp >= 0 && fq >= 0) AddBlock(b, fp, fq, a, bb, -1.0);
            if (fq >= 0 && fp >= 0) AddBlock(b, fq, fp, a, bb, -1.0);
            if (fq >= 0) AddBlock(b, fq, fq, a, bb);
        }
        return b.Build();
    }

    // ---- report -------------------------------------------------------------------------------------------------

    private static MeshedResult Report(PhaseFrameModel model, NodalAdmittance3Ph na, MeshedOptions opt, Complex[] v, MeshedStatus status,
        int iter, double mismatch, IReadOnlyList<string> reasons, List<string> notes, double[]? scaleIn = null)
    {
        var voltages = new List<WireVoltage>();
        var branches = new List<BranchCurrents>();
        var loads = new List<LoadState>();
        var sources = new List<SourceCurrent>();
        var earthing = new List<EarthingCurrent>();
        var grids = new List<GridState>();
        var units = new List<UnitCurrents>();

        if (status != MeshedStatus.Incomplete || na.Incomplete.Count == 0)
        {
            double[] scale = scaleIn ?? Enumerable.Repeat(1.0, model.Loads.Count).ToArray();
            for (int i = 0; i < na.Count; i++)
                if (!na.Variables[i].Internal) voltages.Add(new WireVoltage(na.Variables[i].Node, na.Variables[i].Wire, v[i]));

            foreach (BranchStamp bs in na.Branches)
            {
                int q = bs.StampedWires.Count;
                var iFrom = new Complex[q];
                var iTo = new Complex[q];
                for (int i = 0; i < q; i++)
                {
                    Complex s = Complex.Zero;
                    for (int j = 0; j < q; j++) s += bs.Y[i, j] * (v[bs.FromVars[j]] - v[bs.ToVars[j]]);
                    Complex hf = Complex.Zero, ht = Complex.Zero;
                    if (bs.HalfShunt is { } h)
                        for (int j = 0; j < q; j++) { hf += h[i, j] * v[bs.FromVars[j]]; ht += h[i, j] * v[bs.ToVars[j]]; }
                    iFrom[i] = s + hf;
                    iTo[i] = s - ht;
                }
                var wires = bs.StampedWires.ToList();
                var cf = iFrom.ToList();
                var ct = iTo.ToList();
                if (bs.KronNeutralFactor is { } kf)
                {
                    Complex inF = Complex.Zero, inT = Complex.Zero;
                    for (int i = 0; i < q; i++) { inF += kf[i] * iFrom[i]; inT += kf[i] * iTo[i]; }
                    wires.Add(bs.Branch.KronWire!);
                    cf.Add(inF);
                    ct.Add(inT);
                }
                Complex residual = Complex.Zero;
                foreach (Complex c in cf) residual += c;
                branches.Add(new BranchCurrents(bs.Branch.Name, wires, cf, ct, residual));
            }

            var rFull = new Complex[na.Count];
            var legCurrent = new Complex[na.Legs.Count];
            Residual(model, na, v, scale, Enumerable.Repeat(-1, na.Count).ToArray(), legCurrent, rFull);

            for (int l = 0; l < model.Loads.Count; l++)
            {
                var uu = new List<Complex>();
                var ii = new List<Complex>();
                var pp = new List<Complex>();
                for (int t = 0; t < na.Legs.Count; t++)
                {
                    LegStamp g = na.Legs[t];
                    if (g.Load != l) continue;
                    Complex u = LegVoltage(na, v, g);
                    uu.Add(u); ii.Add(legCurrent[t]); pp.Add(u * Complex.Conjugate(legCurrent[t]));
                }
                loads.Add(new LoadState(model.Loads[l].Name, model.Loads[l].Node, uu, ii, pp));
            }

            foreach (FrameSource s in model.Sources)
            {
                for (int w = 0; w < s.Wires.Count; w++)
                {
                    int nv = na.Find(s.Node, s.Wires[w]);
                    if (nv < 0) continue;
                    SourceStamp? ser = na.SourceSeries.FirstOrDefault(x => ReferenceEquals(x.Source, s) && x.WireIndex == w);
                    sources.Add(new SourceCurrent(s.Node, s.Wires[w], ser is null ? rFull[nv] : ser.Y * (v[ser.EmfVar] - v[ser.NodeVar])));
                }
            }
            foreach (EarthingStamp e in na.EarthingStamps)
            {
                Complex cur = e.Solid ? -rFull[e.Var] : e.GridVar >= 0 ? e.Y * (v[e.Var] - v[e.GridVar]) : e.Y * v[e.Var];
                earthing.Add(new EarthingCurrent(e.Element.Node, e.Element.Wire, e.Element.Grid, v[e.Var], cur));
            }
            foreach (EarthGrid g in model.Grids)
            {
                int gv = na.GridVars[g.Name];
                grids.Add(new GridState(g.Name, v[gv], v[gv] / g.Z));
            }
            foreach (UnitStamp u in na.Units)
            {
                int nw = u.PVars.Length;
                var wv = new Complex[nw];
                var wi = new Complex[nw];
                for (int k = 0; k < nw; k++) wv[k] = v[u.PVars[k]] - (u.QVars[k] >= 0 ? v[u.QVars[k]] : Complex.Zero);
                for (int k = 0; k < nw; k++)
                {
                    Complex s = Complex.Zero;
                    for (int l = 0; l < nw; l++) s += u.Yw[k, l] * wv[l];
                    wi[k] = s;
                }
                units.Add(new UnitCurrents(u.Unit.Name, wv, wi));
            }
        }

        return new MeshedResult
        {
            Status = status,
            Iterations = iter,
            MismatchAmps = mismatch,
            IncompleteReasons = reasons.ToList(),
            Notes = notes,
            Variables = na.Variables,
            State = v.ToList(),
            Voltages = voltages,
            Branches = branches,
            Loads = loads,
            Sources = sources,
            Earthing = earthing,
            Grids = grids,
            Units = units,
        };
    }
}
