using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Core.Protection;

// RC4 (last candidate): the network side of series and simultaneous faults. PURELY ADDITIVE: nothing in FaultAnalysis.cs or
// Iec60909.cs is changed (the one edit there is the `partial` keyword, which changes no behaviour). This file assembles the same
// operating-point sequence networks FaultAnalysis.Unbalanced assembles (loads as constant-impedance shunts at the prefault voltage,
// synchronous units as X''d / X2 to ground, the zero sequence from the winding connections), factors them with the same
// FaultNetworkSystem, and reads more than one Zbus column: the transfer impedances between several fault buses, and the Thevenin
// impedance across a break in one branch. The fault maths itself is SeriesFault.OpenConductors and SimultaneousFault.Solve, unchanged.
// No new algorithm: every number here is a Zbus entry (one solve y x = e_k per column) or a sum of such entries.

/// <summary>The N-port sequence description of a model at a set of fault buses (the ports): the transfer impedance matrices of the
/// positive, negative and zero sequence networks and the prefault voltage at each port, ready for
/// <see cref="SimultaneousFault.Solve"/> (see <see cref="FaultAnalysis.SequencePorts"/>).</summary>
/// <param name="BusIds">The port buses, in the order asked.</param>
/// <param name="Impedances">Z[p, q] of the three sequences in pu on the system base.</param>
/// <param name="Prefault">The prefault voltage at each port (complex, pu).</param>
/// <param name="ZeroSequenceGrounded">Per port: its zero-sequence component reaches an earthed neutral. Where it does not, no earth
/// current can flow there: its zero-sequence transfer entries are 0 and its zero-sequence self entry is the placeholder 1 pu (the
/// impedance is infinite; any finite non-zero value gives the same answer, because every boundary condition that touches the zero
/// sequence at that port, V0 = 0 of a three-phase fault or I0 = 0 of a line-to-line fault, fixes I0 = 0 whatever the value is). An earth
/// fault (single or double line to ground) at such a port is not defined and the caller must refuse it.</param>
/// <param name="BaseKv">Per port: the bus base kV (0 when not given).</param>
public sealed record SequencePortNetwork(
    IReadOnlyList<int> BusIds, SequencePortImpedances Impedances, IReadOnlyList<Complex> Prefault,
    IReadOnlyList<bool> ZeroSequenceGrounded, IReadOnlyList<double> BaseKv);

/// <summary>The three sequence Thevenin impedances seen across a break in the series element of one branch, and the prefault
/// current through that element (see <see cref="FaultAnalysis.SeriesBreak"/>). The impedances include the element's own series
/// impedance (the break sits in series with it), which is what <see cref="SeriesFault.OpenConductors"/> expects.</summary>
/// <param name="BranchIndex">Index in <see cref="GridModel.Branches"/>.</param>
/// <param name="FromBus">The element's from bus id.</param>
/// <param name="ToBus">The element's to bus id.</param>
/// <param name="Zs1">Positive-sequence impedance across the break (pu).</param>
/// <param name="Zs2">Negative-sequence impedance across the break (pu).</param>
/// <param name="Zs0">Zero-sequence impedance across the break (pu).</param>
/// <param name="IPrePositive">The prefault positive-sequence current through the element, from its from bus to its to bus (pu).</param>
/// <param name="BaseKv">The from bus base kV (0 when not given), for kA.</param>
public sealed record SeriesBreakNetwork(
    int BranchIndex, int FromBus, int ToBus, Complex Zs1, Complex Zs2, Complex Zs0, Complex IPrePositive, double BaseKv);

public static partial class FaultAnalysis
{
    /// <summary>
    /// The operating-point shunts (bus index, admittance) of the positive and negative sequence networks, the prefault voltages and
    /// the de-energised flags, built exactly as <see cref="Unbalanced"/> builds them. The checks that name an isolated or
    /// de-energised fault bus run once per port.
    /// </summary>
    private static (List<(int Index, Complex Y)> Positive, List<(int Index, Complex Y)> Negative, Complex[] V0, bool[] Dead, Dictionary<int, int> Index)
        OperatingNetworks(GridModel model, PowerFlowResult prefault, IReadOnlyList<int> faultBusIds, double subtransientXdPu)
    {
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        var ports = new List<int>(faultBusIds.Count);
        foreach (int id in faultBusIds)
        {
            if (!index.TryGetValue(id, out int k)) throw new ArgumentException($"fault bus {id} is not in the model", nameof(faultBusIds));
            ports.Add(k);
        }
        double baseMva = model.BaseMva;
        var v0 = new Complex[n];
        bool[] dead = PrefaultVoltages(model, prefault, v0, ports[0], faultBusIds[0]);
        for (int p = 1; p < ports.Count; p++)
            if (dead[ports[p]])
                throw new InvalidOperationException($"Fault bus {faultBusIds[p]} is not in the prefault result (a de-energised bus), so there is no prefault voltage to fault.");

        List<(int Index, Complex Y)> Shunts(Func<Generator, double> reactancePu)
        {
            var shunts = new List<(int Index, Complex Y)>();
            foreach (Bus b in model.Buses)
            {
                if (b.Pd == 0 && b.Qd == 0) continue;
                int i = index[b.Id];
                if (dead[i]) continue;
                double vm2 = Math.Max(v0[i].Magnitude * v0[i].Magnitude, 1e-6);
                var sLoadPu = new Complex(b.Pd, b.Qd) / baseMva;
                shunts.Add((i, Complex.Conjugate(sLoadPu) / vm2));
            }
            foreach (Generator g in model.Generators)
            {
                if (!index.TryGetValue(g.Bus, out int i)) continue;
                if (dead[i]) continue;
                if (g.PowerFlowOnly || g.IsConverterInterfaced) continue;
                double xSys = reactancePu(g) * baseMva / RatedMva(g);
                if (xSys <= 0) continue;
                shunts.Add((i, Complex.One / new Complex(0, xSys)));
            }
            // The isolated-bus checks and grounding: run for the first port on the real list; the other ports only need the check.
            var probe = new List<(int Index, Complex Y)>(shunts);
            for (int p = 1; p < ports.Count; p++) GroundIsolatedBuses(model, index, new List<(int Index, Complex Y)>(probe), ports[p], faultBusIds[p], dead);
            GroundIsolatedBuses(model, index, shunts, ports[0], faultBusIds[0], dead);
            return shunts;
        }
        var pos = Shunts(g => g.FaultSourceReactancePu(subtransientXdPu));
        var neg = Shunts(g => g.FaultSourceNegativeSeqReactancePu(subtransientXdPu));
        return (pos, neg, v0, dead, index);
    }

    /// <summary>The Zbus column of bus index <paramref name="k"/> of a zero-sequence network: the entries of the component that
    /// reaches an earthed neutral, 0 elsewhere. <paramref name="grounded"/> says whether the bus's component is earthed.</summary>
    private static Complex[] ZeroSequenceColumn(Iec60909.ZeroSequenceNetwork zs, int n, int k, out bool grounded)
    {
        var comp = new List<int> { k };
        var seen = new bool[n];
        seen[k] = true;
        var queue = new Queue<int>();
        queue.Enqueue(k);
        grounded = zs.HasGround[k];
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int w in zs.Adjacency[u])
                if (!seen[w]) { seen[w] = true; comp.Add(w); grounded |= zs.HasGround[w]; queue.Enqueue(w); }
        }
        var col = new Complex[n];
        if (!grounded) return col;
        var local = new Dictionary<int, int>(comp.Count);
        for (int a = 0; a < comp.Count; a++) local[comp[a]] = a;
        var subTrip = new List<(int Row, int Col, Complex V)>(zs.Trip.Count);
        foreach ((int r, int c, Complex v) in zs.Trip)
            if (local.TryGetValue(r, out int lr) && local.TryGetValue(c, out int lc)) subTrip.Add((lr, lc, v));
        var el = new Complex[comp.Count];
        el[local[k]] = Complex.One;
        Complex[] sub = FaultNetworkSystem.FromTriplets(comp.Count, subTrip).Solve(el);
        for (int a = 0; a < comp.Count; a++) col[comp[a]] = sub[a];
        return col;
    }

    /// <summary>
    /// The N-port sequence network of the operating point at <paramref name="faultBusIds"/>: for each of the positive, negative and
    /// zero sequence networks the matrix Z[p, q] = Zbus[bus p, bus q] (one column solved per port on the same factorisation
    /// <see cref="Unbalanced"/> uses), and the prefault voltage at each port. A single port gives exactly the driving-point
    /// impedances <see cref="Unbalanced"/> reports. The buses must be distinct. Conventions are those of <see cref="Unbalanced"/>:
    /// positive sequence with each unit as X''d, negative with X2 and every phase shift reversed, zero sequence from the winding
    /// connections with loads neglected; converter-interfaced units are open in all three.
    /// </summary>
    public static SequencePortNetwork SequencePorts(
        GridModel model, PowerFlowResult prefault, IReadOnlyList<int> faultBusIds,
        double subtransientXdPu = DefaultSubtransientXdPu, double lineZeroSeqRatio = 3.0)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(prefault);
        ArgumentNullException.ThrowIfNull(faultBusIds);
        if (faultBusIds.Count == 0) throw new ArgumentException("at least one fault port is needed.", nameof(faultBusIds));
        if (faultBusIds.Distinct().Count() != faultBusIds.Count) throw new ArgumentException("the fault ports must be distinct buses.", nameof(faultBusIds));
        Iec60909.RequireRepresentableZeroSequence(model);
        prefault.RequireConverged("Fault analysis");
        (List<(int Index, Complex Y)> pos, List<(int Index, Complex Y)> neg, Complex[] v0, _, Dictionary<int, int> index) =
            OperatingNetworks(model, prefault, faultBusIds, subtransientXdPu);
        int n = model.BusCount, m = faultBusIds.Count;
        int[] k = faultBusIds.Select(id => index[id]).ToArray();

        FaultNetworkSystem sys1 = FaultNetworkSystem.ForModel(model, pos);
        FaultNetworkSystem sys2 = FaultNetworkSystem.ForModel(ReversedPhaseShifts(model), neg);
        Iec60909.ZeroSequenceNetwork zs = Iec60909.BuildZeroSequenceNetwork(model, index, lineZeroSeqRatio);

        var z1 = new Complex[m, m];
        var z2 = new Complex[m, m];
        var z0 = new Complex[m, m];
        var grounded = new bool[m];
        for (int q = 0; q < m; q++)
        {
            var e = new Complex[n];
            e[k[q]] = Complex.One;
            Complex[] c1 = sys1.Solve(e);
            Complex[] c2 = sys2.Solve(e);
            Complex[] c0 = ZeroSequenceColumn(zs, n, k[q], out grounded[q]);
            for (int p = 0; p < m; p++) { z1[p, q] = c1[k[p]]; z2[p, q] = c2[k[p]]; z0[p, q] = c0[k[p]]; }
            if (!grounded[q]) z0[q, q] = Complex.One;   // infinite in fact; a finite non-zero placeholder keeps the phase-domain system regular and I0 = 0
        }
        return new SequencePortNetwork(faultBusIds.ToArray(), new SequencePortImpedances(z1, z2, z0), k.Select(i => v0[i]).ToArray(),
            grounded, k.Select(i => model.Buses[i].BaseKv).ToArray());
    }

    /// <summary>
    /// The sequence Thevenin impedances across a break in the series element of branch <paramref name="branchIndex"/>, and the
    /// prefault current through it, for <see cref="SeriesFault.OpenConductors"/>. The break sits in series with the element, in the
    /// middle of its pi section: the branch's series impedance is taken out of the network (its line-charging halves stay at its
    /// two buses), the Thevenin impedance between its two buses is <c>Zii + Zjj - 2 Zij</c> of that reduced network (the same Zbus
    /// columns as <see cref="SequencePorts"/>), and the element's own series impedance is added back, so <c>Zs_k = Zth_k + Zb_k</c>
    /// and, with the prefault current <c>I = (V_from - V_to) / Zb</c> through the element, the open-circuit voltage across the break is
    /// <c>Zs_1 I</c>. Zb is <c>R + jX</c> in the positive and negative sequence and the series zero-sequence impedance in the zero.
    /// Defined for a branch with no tap (ratio 0 or 1) and no phase shift whose zero-sequence model is a plain series impedance (a
    /// line, or a grounded-wye / grounded-wye transformer without mutual coupling); anything else is refused by name, and so is a
    /// break whose zero-sequence network is not earthed on both sides (an infinite Zs0, which
    /// <see cref="SeriesFault.OpenConductors"/> does not take) or that leaves a side with no source or load.
    /// </summary>
    public static SeriesBreakNetwork SeriesBreak(
        GridModel model, PowerFlowResult prefault, int branchIndex,
        double subtransientXdPu = DefaultSubtransientXdPu, double lineZeroSeqRatio = 3.0)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(prefault);
        if (branchIndex < 0 || branchIndex >= model.Branches.Count)
            throw new ArgumentOutOfRangeException(nameof(branchIndex), branchIndex, $"the model has {model.Branches.Count} branches (0 to {model.Branches.Count - 1}).");
        Branch br = model.Branches[branchIndex];
        string label = $"branch {branchIndex} ({br.From}-{br.To})";
        if (!br.InService) throw new InvalidOperationException($"series fault: {label} is out of service.");
        if (br.TapRatio != 0.0 && br.TapRatio != 1.0 || br.PhaseShiftDeg != 0.0)
            throw new InvalidOperationException($"series fault: {label} has a tap ratio or phase shift; a break in the series element of an off-nominal transformer or phase shifter is not defined (only lines and unit-ratio transformers).");
        if (br.R == 0.0 && br.X == 0.0) throw new InvalidOperationException($"series fault: {label} has no series impedance to break.");
        Iec60909.RequireRepresentableZeroSequence(model);
        prefault.RequireConverged("Fault analysis");

        (List<(int Index, Complex Y)> pos, List<(int Index, Complex Y)> neg, Complex[] v0, bool[] dead, Dictionary<int, int> index) =
            OperatingNetworks(model, prefault, new[] { br.From, br.To }, subtransientXdPu);
        int n = model.BusCount;
        int f = index[br.From], t = index[br.To];
        if (dead[f] || dead[t]) throw new InvalidOperationException($"series fault: {label} joins a de-energised bus.");

        // The zero-sequence view of the element, from the network as it is.
        Iec60909.ZeroSequenceNetwork zsFull = Iec60909.BuildZeroSequenceNetwork(model, index, lineZeroSeqRatio);
        if (zsFull.Kind[branchIndex] != Iec60909.ZeroSeqBranchKind.Series)
            throw new InvalidOperationException($"series fault: {label} has no plain series zero-sequence impedance (a delta or ungrounded winding, a shunt-type zero-sequence model or mutual coupling), so a break in it has no defined zero-sequence impedance.");
        Complex zb1 = new(br.R, br.X);
        Complex zb0 = Complex.One / zsFull.Admittance[branchIndex];

        // The network with the series element taken out; its charging halves stay as bus shunts.
        var buses = new List<Bus>(model.Buses);
        double half = br.B / 2.0 * model.BaseMva;
        if (half != 0.0)
        {
            buses[f] = buses[f] with { Bs = buses[f].Bs + half };
            buses[t] = buses[t] with { Bs = buses[t].Bs + half };
        }
        var branches = new List<Branch>(model.Branches);
        branches[branchIndex] = br with { InService = false };
        GridModel broken = model.CopyWith(buses: buses, branches: branches);

        Complex Thevenin(Func<FaultNetworkSystem> build, string seq)
        {
            var ef = new Complex[n]; ef[f] = Complex.One;
            var et = new Complex[n]; et[t] = Complex.One;
            try
            {
                FaultNetworkSystem sys = build();
                Complex[] cf = sys.Solve(ef), ct = sys.Solve(et);
                Complex z = cf[f] + ct[t] - cf[t] - ct[f];
                if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary)) throw new InvalidOperationException("not finite");
                return z;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArithmeticException or InvalidDataException)
            {
                throw new InvalidOperationException($"series fault: with {label} broken, the {seq}-sequence network has a side with no source, load or shunt to earth, so the impedance across the break is undefined.", ex);
            }
        }

        // Isolation check: a terminal bus left with nothing at all (no branch, load, shunt or machine) would be given a unit shunt by
        // GroundIsolatedBuses, which would invent an impedance, so it is refused here instead.
        foreach ((int bi, int bid) in new[] { (f, br.From), (t, br.To) })
        {
            bool touched = broken.Buses[bi].Gs != 0.0 || broken.Buses[bi].Bs != 0.0 || pos.Any(s => s.Index == bi)
                || broken.Branches.Any(b => b.InService && (b.From == bid || b.To == bid));
            if (!touched) throw new InvalidOperationException($"series fault: with {label} broken, bus {bid} has no branch, load, shunt or machine left, so there is no source on that side of the break.");
        }
        Complex zth1 = Thevenin(() => FaultNetworkSystem.ForModel(broken, pos), "positive");
        Complex zth2 = Thevenin(() => FaultNetworkSystem.ForModel(ReversedPhaseShifts(broken), neg), "negative");

        Iec60909.ZeroSequenceNetwork zs = Iec60909.BuildZeroSequenceNetwork(broken, index, lineZeroSeqRatio);
        Complex[] col0f = ZeroSequenceColumn(zs, n, f, out bool gf);
        Complex[] col0t = ZeroSequenceColumn(zs, n, t, out bool gt);
        if (!gf || !gt)
            throw new InvalidOperationException($"series fault: with {label} broken, the {(gf ? $"to-side (bus {br.To})" : $"from-side (bus {br.From})")} of the zero-sequence network reaches no earthed neutral, so the zero-sequence impedance across the break is infinite and SeriesFault.OpenConductors (which needs it finite) does not apply.");
        // Both sides earthed: Zij is nonzero only when the two buses share a component.
        Complex zth0 = col0f[f] + col0t[t] - col0f[t] - col0t[f];

        Complex iPre = (v0[f] - v0[t]) / zb1;
        return new SeriesBreakNetwork(branchIndex, br.From, br.To, zth1 + zb1, zth2 + zb1, zth0 + zb0, iPre, model.Buses[f].BaseKv);
    }
}
