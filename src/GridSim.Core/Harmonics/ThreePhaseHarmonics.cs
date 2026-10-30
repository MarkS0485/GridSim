using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Diagnostics;
using GridSim.Core.Distribution;
using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics;

/// <summary>
/// A three-phase harmonic current source: the per-phase injection at each order. Harmonics are inherently
/// unbalanced, so this carries a full per-phase spectrum. The <see cref="Balanced"/> factory generates the
/// classic balanced rotation - phase B lags A by <c>h*120°</c>, phase C leads by the same - which reproduces
/// the sequence pattern automatically (triplens collapse to zero sequence, the 5th is negative, etc.).
/// </summary>
public sealed class ThreePhaseHarmonicSource
{
    public int Bus { get; }
    public string SourceType { get; }
    public ProvenanceTag Provenance { get; }
    private readonly IReadOnlyDictionary<int, Complex[]> _byOrder;

    public ThreePhaseHarmonicSource(int bus, string sourceType, IReadOnlyDictionary<int, Complex[]> byOrder,
        ProvenanceTag provenance = ProvenanceTag.CuratedPlaceholder)
    {
        Bus = bus; SourceType = sourceType; Provenance = provenance; _byOrder = byOrder;
    }

    /// <summary>The per-phase (length 3) injection current at <paramref name="order"/>, or zeros.</summary>
    public Complex[] InjectionAt(int order) =>
        _byOrder.TryGetValue(order, out Complex[]? v) ? v : new Complex[3];

    public IEnumerable<int> Orders => _byOrder.Keys;

    /// <summary>A balanced source: a base percent spectrum applied with the harmonic phase rotation.</summary>
    public static ThreePhaseHarmonicSource Balanced(
        int bus, string sourceType, double fundamentalCurrentPu, IReadOnlyList<HarmonicComponent> spectrum,
        ProvenanceTag provenance = ProvenanceTag.CuratedPlaceholder)
    {
        var byOrder = new Dictionary<int, Complex[]>();
        foreach (HarmonicComponent c in spectrum)
        {
            // GR-577: a repeated order is refused, as the spectra file loader refuses it (it was silently the last one).
            if (byOrder.ContainsKey(c.Order))
                throw new ArgumentException($"Balanced harmonic source at bus {bus}: order {c.Order} appears more than once in the spectrum.", nameof(spectrum));
            Complex a = DetMath.FromPolar(
                (c.MagnitudePercent / 100.0) * fundamentalCurrentPu, c.PhaseDeg * Math.PI / 180.0);
            Complex rot = DetMath.FromPolar(1.0, -c.Order * 2.0 * Math.PI / 3.0); // B lags A by h*120°
            byOrder[c.Order] = new[] { a, a * rot, a * Complex.Conjugate(rot) };            // A, B, C
        }
        return new ThreePhaseHarmonicSource(bus, sourceType, byOrder, provenance);
    }
}

/// <summary>Per-bus, per-phase harmonic voltages from a three-phase penetration study, plus the sequence view.</summary>
/// <param name="Orders">The analysed orders.</param>
/// <param name="BusIds">The non-source bus ids (the source is the harmonic reference).</param>
/// <param name="Voltage">[orderIndex][busIndex] = the per-phase complex voltage (length 3).</param>
public sealed record ThreePhasePenetrationResult(
    IReadOnlyList<int> Orders,
    IReadOnlyList<int> BusIds,
    Complex[][][] Voltage)
{
    /// <summary>The [zero, positive, negative]-sequence voltages at a bus/order (Fortescue transform).</summary>
    public Complex[] Sequence(int orderIndex, int busIndex) =>
        SymmetricalComponents.ToSequence(Voltage[orderIndex][busIndex]);
}

/// <summary>
/// N348 / N149 rest (CF4, October 2026; Ruling R1): an active-front-end converter on a three-phase feeder, as resonant
/// plant through its internal impedance (ENA EREC G5 Issue 5 s.3.29 p.17, docs/reference S139; the positive-sequence
/// study's <see cref="HarmonicModelOptions.ActiveFrontEndReactancePu"/>). <paramref name="ReactancePu"/> (at the
/// fundamental) and <paramref name="ResistancePu"/> are per-unit on the unit's own three-phase rating
/// <paramref name="RatingMva"/>; the study converts them to the feeder's base (<see cref="MultiPhaseModel.BaseMva"/>) by
/// <c>BaseMva / RatingMva</c>, as the positive-sequence study converts from MBase. No default value exists on disk
/// [VERIFY]: the caller states it (the converter's coupling reactor or filter, from the manufacturer).
/// </summary>
/// <param name="Bus">The feeder bus the converter is connected at (not the source bus, the harmonic reference).</param>
/// <param name="RatingMva">The converter's three-phase rating, MVA (finite, above 0).</param>
/// <param name="ReactancePu">Its internal reactance at the fundamental, pu on its rating (finite, above 0).</param>
/// <param name="ResistancePu">Its internal resistance, pu on its rating (finite, 0 or more; 0 = no damping).</param>
public sealed record ThreePhaseActiveFrontEnd(int Bus, double RatingMva, double ReactancePu, double ResistancePu = 0.0);

/// <summary>
/// Three-phase / unbalanced harmonic penetration. It assembles the reduced 3n complex admittance Y(h) of the
/// unbalanced feeder (each line branch's 3x3 series impedance with reactance scaled to order h, inverted to a
/// 3x3 admittance and stamped block-wise), with the source substation as the harmonic ground, and solves
/// <c>Y(h) V(h) = I(h)</c> for the per-phase bus voltages via the complex-solver seam. A balanced feeder with
/// balanced injection reduces to the single-phase penetration (the non-regression bridge); an unbalanced one
/// produces the negative/zero-sequence content the positive-sequence engine cannot see.
///
/// <para><b>Experimental</b> (R062, October 2026). On the CLI since N062 as <c>dist3ph &lt;feeder&gt; harmonics</c> (one
/// balanced library source on a gridsim-dist3ph/1 feeder). Still needs validation of the sequence content against an
/// independent three-phase reference [VERIFY]. N167 (October 2026): shunt capacitors (j h B per phase), line charging
/// (half the pi shunt at each end, G + j h B) and closed switches (their buses merged into one node) are modelled; loads
/// of every kind stay outside Y(h); a single- or two-phase branch or a step-voltage regulator is refused by name
/// (<see cref="MultiPhaseModel.RequireSupportedElements"/>). It used to refuse every N062 element.</para>
/// </summary>
public static class ThreePhaseHarmonicPenetration
{
    private static PhaseImpedance AtOrder(PhaseImpedance z, double h)
    {
        var m = new Complex[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[i, j] = new Complex(z[i, j].Real, z[i, j].Imaginary * h); // R + j h X per element
        return new PhaseImpedance(m);
    }

    /// <summary>Run the three-phase penetration over the given orders.
    /// <para>N348 / N149 rest (CF4): <paramref name="activeFrontEnds"/>, when given, stamps each active-front-end
    /// converter's internal impedance (see <see cref="ThreePhaseActiveFrontEnd"/>) at its bus as a BALANCED THREE-WIRE
    /// shunt: each phase reactor <c>z = R + j h X</c> (feeder base) between its phase and the converter's floating star
    /// point, so the converter takes positive- and negative-sequence harmonic current through <c>y = 1 / z</c> and no
    /// zero-sequence current (a three-wire converter has no neutral path). Its 3x3 admittance is
    /// <c>(y / 3) [[2, -1, -1], [-1, 2, -1], [-1, -1, 2]]</c>. A balanced feeder with a balanced (non-triplen) injection
    /// therefore sees exactly the shunt <c>y</c> of the positive-sequence study (<see cref="FrequencyDependentYBus"/>).
    /// Null or empty (the default) stamps nothing: every study is bit for bit as before.</para></summary>
    public static ThreePhasePenetrationResult Solve(
        MultiPhaseModel model,
        IReadOnlyList<ThreePhaseHarmonicSource> sources,
        IReadOnlyList<int> orders,
        IComplexLinearSolver? solver = null,
        IReadOnlyList<ThreePhaseActiveFrontEnd>? activeFrontEnds = null)
    {
        HarmonicModelOptions.ValidateOrders(orders, nameof(orders));   // no empty, non-positive or duplicate order
        // N167 (October 2026): shunt capacitors (j h B per phase), line charging (half the pi shunt at each end, its
        // susceptance scaled by h) and closed switches (their two buses are one electrical node) are modelled; loads of
        // every kind stay outside Y(h), as the constant-power loads always were (network only). A single- or two-phase
        // branch (no admittance on its absent phases) or a step-voltage regulator is still refused, by name.
        model.RequireSupportedElements("the three-phase harmonic penetration",
            MultiPhaseElement.Capacitor | MultiPhaseElement.LineCharging | MultiPhaseElement.ClosedSwitch
            | MultiPhaseElement.VoltageDependentLoad | MultiPhaseElement.DeltaLoad,
            "capacitors and line charging as susceptances scaled by the order, closed switches as one node; loads of every kind, " +
            "constant-power wye included, are accepted and left out of Y(h): the study is network only");   // D-DOC-156: the text was "loads are not in Y(h)", after a sentence that said the study supports loads
        solver ??= new RealExpandedComplexSolver();

        // N167: closed switches join their two buses into one node (union-find over the zero-impedance branches). With no
        // closed switch every bus is its own node and everything below is exactly the pre-N167 arithmetic.
        var nodeOf = SwitchNodes(model);

        // Non-source buses carry the unknowns (the source is the harmonic reference / ground). A bus switched onto the
        // source is part of the reference (zero harmonic voltage); buses switched together share one set of unknowns.
        var reduced = new Dictionary<int, int>();
        var busIds = new List<int>();
        int sourceId = model.Buses.First(b => b.IsSource).Id;
        int sourceNode = nodeOf[sourceId];
        var unknownOfNode = new Dictionary<int, int>();
        int m = 0;
        foreach (PhaseBus b in model.Buses)
        {
            if (b.IsSource) continue;
            busIds.Add(b.Id);
            int node = nodeOf[b.Id];
            if (node == sourceNode) continue;                                   // switched onto the reference
            if (!unknownOfNode.TryGetValue(node, out int u)) { u = m++; unknownOfNode[node] = u; }
            reduced[b.Id] = u;
        }
        bool IsSource(int id) => !reduced.ContainsKey(id);

        // N167: the shunt admittance at fundamental frequency per unknown (capacitors and the pi halves of line
        // charging), scaled by the order below. Null where a node has none, so a model without shunts adds nothing.
        Complex[,]?[] shunt = BuildShunts(model, reduced, m);

        // N348 / N149 rest (CF4): the active-front-end converters, checked before any order is solved. Each entry is
        // (unknown index, R and X on the feeder base). Null when none were given, so nothing below runs.
        List<(int Unknown, double R, double X)>? afe = ActiveFrontEndUnknowns(model, activeFrontEnds, reduced, sourceId, busIds);

        var voltage = new Complex[orders.Count][][];

        for (int oi = 0; oi < orders.Count; oi++)
        {
            int h = orders[oi];
            var builder = new ComplexCsr.Builder(3 * m);

            void Stamp(int busR, int busC, PhaseImpedance blk, double sign)
            {
                for (int p = 0; p < 3; p++)
                    for (int q = 0; q < 3; q++)
                        builder.Add(3 * busR + p, 3 * busC + q, sign * blk[p, q].Real, sign * blk[p, q].Imaginary);
            }

            foreach (PhaseBranch br in model.Branches)
            {
                // N167: a closed switch has no admittance to stamp: its two buses are already one node (SwitchNodes).
                if (MultiPhaseModel.IsZeroImpedance(br.Z)) continue;
                PhaseImpedance ys = AtOrder(br.Z, h).Invert(); // 3x3 series admittance at order h
                bool fSrc = IsSource(br.From), tSrc = IsSource(br.To);
                if (fSrc && tSrc) continue;
                if (!fSrc && !tSrc)
                {
                    int f = reduced[br.From], t = reduced[br.To];
                    Stamp(f, f, ys, +1); Stamp(t, t, ys, +1);
                    Stamp(f, t, ys, -1); Stamp(t, f, ys, -1);
                }
                else if (fSrc) { int t = reduced[br.To]; Stamp(t, t, ys, +1); }   // source col dropped (V_src = 0)
                else { int f = reduced[br.From]; Stamp(f, f, ys, +1); }
            }

            // N167: the shunts, at order h: G + j h B per entry (a capacitive susceptance grows with frequency), on the
            // diagonal block of their node. Nothing is added where a node has no shunt.
            for (int u = 0; u < m; u++)
            {
                if (shunt[u] is not { } y) continue;
                for (int p = 0; p < 3; p++)
                    for (int q = 0; q < 3; q++)
                        if (y[p, q] != Complex.Zero)
                            builder.Add(3 * u + p, 3 * u + q, y[p, q].Real, y[p, q].Imaginary * h);
            }

            // N348 / N149 rest (CF4): each active-front-end converter as a balanced three-wire shunt at order h:
            // y = 1 / (R + j h X) = (R - j h X) / (R^2 + (h X)^2) (the positive-sequence stamp's arithmetic), spread over
            // its node's 3x3 block as (y / 3) [[2, -1, -1], [-1, 2, -1], [-1, -1, 2]].
            if (afe is not null)
            {
                foreach ((int u, double r, double xAfe) in afe)
                {
                    double hx = h * xAfe;
                    double den = r * r + hx * hx;
                    var y = new Complex(r / den, -hx / den);
                    Complex diag = y * (2.0 / 3.0), off = -y / 3.0;
                    for (int p = 0; p < 3; p++)
                        for (int q = 0; q < 3; q++)
                        {
                            Complex v = p == q ? diag : off;
                            builder.Add(3 * u + p, 3 * u + q, v.Real, v.Imaginary);
                        }
                }
            }

            var inj = new Complex[3 * m];
            foreach (ThreePhaseHarmonicSource src in sources)
            {
                // N167: a source on a bus that a closed switch ties to the source substation would vanish into the
                // harmonic reference: refuse it rather than drop it.
                if (!reduced.ContainsKey(src.Bus) && src.Bus != sourceId && busIds.Contains(src.Bus))
                    throw new ArgumentException(
                        $"three-phase harmonic penetration: the harmonic source at bus {src.Bus} is tied to the source bus by closed " +
                        "switches, so it sits on the harmonic reference (zero harmonic voltage) and would be lost; inject at another bus.");
                // A source on the source bus itself, or on a bus that is not in the feeder, would be dropped: refuse it.
                if (src.Bus == sourceId)
                    throw new ArgumentException(
                        $"three-phase harmonic penetration: the harmonic source at bus {src.Bus} is on the source bus, which is the " +
                        "harmonic reference (zero harmonic voltage); inject at another bus.");
                if (!reduced.TryGetValue(src.Bus, out int bi))
                    throw new ArgumentException($"three-phase harmonic penetration: the harmonic source at bus {src.Bus} is not on a bus of the feeder.");
                Complex[] ip = src.InjectionAt(h);
                for (int p = 0; p < 3; p++) inj[3 * bi + p] += ip[p];
            }

            // A singular three-phase Y(h) used to become a zero voltage vector (R001); fail loud instead.
            Complex[] x = solver.Solve(builder.Build(), inj)
                ?? throw new SingularHarmonicNetworkException("Three-phase harmonic penetration", h);
            // One row per non-source bus, in model order: its node's unknowns (shared by buses switched together), or
            // zero for a bus switched onto the reference. Without switches bus k is unknown k, as before.
            var vOrder = new Complex[busIds.Count][];
            for (int bi = 0; bi < busIds.Count; bi++)
                vOrder[bi] = reduced.TryGetValue(busIds[bi], out int u)
                    ? new[] { x[3 * u], x[3 * u + 1], x[3 * u + 2] }
                    : new Complex[3];
            voltage[oi] = vOrder;
        }

        return new ThreePhasePenetrationResult(orders, busIds, voltage);
    }

    /// <summary>N348 / N149 rest (CF4): check the active-front-end list and resolve each to its unknown and its impedance on
    /// the feeder base. Fail loud: a null entry, a bus not in the feeder, a bus on the harmonic reference (the source bus or
    /// one switched onto it: the shunt would vanish), a rating that is not finite and positive, or an impedance the
    /// positive-sequence study would refuse (<see cref="FrequencyDependentYBus.ValidateActiveFrontEndImpedance"/>). Returns
    /// null for a null or empty list.</summary>
    private static List<(int Unknown, double R, double X)>? ActiveFrontEndUnknowns(
        MultiPhaseModel model, IReadOnlyList<ThreePhaseActiveFrontEnd>? activeFrontEnds, Dictionary<int, int> reduced,
        int sourceId, List<int> busIds)
    {
        if (activeFrontEnds is null || activeFrontEnds.Count == 0) return null;
        var list = new List<(int, double, double)>(activeFrontEnds.Count);
        for (int i = 0; i < activeFrontEnds.Count; i++)
        {
            ThreePhaseActiveFrontEnd? a = activeFrontEnds[i]
                ?? throw new ArgumentException($"three-phase harmonic penetration: activeFrontEnds[{i}] is null.", nameof(activeFrontEnds));
            if (a.Bus == sourceId)
                throw new ArgumentException($"three-phase harmonic penetration: the active-front-end converter at bus {a.Bus} is on the "
                    + "source bus, the harmonic reference (zero harmonic voltage), so its impedance would carry nothing; place it at another bus.",
                    nameof(activeFrontEnds));
            if (!busIds.Contains(a.Bus))
                throw new ArgumentException($"three-phase harmonic penetration: the active-front-end converter's bus {a.Bus} is not in the feeder.",
                    nameof(activeFrontEnds));
            if (!reduced.TryGetValue(a.Bus, out int u))
                throw new ArgumentException($"three-phase harmonic penetration: the active-front-end converter at bus {a.Bus} is tied to the "
                    + "source bus by closed switches, so it sits on the harmonic reference and would be lost; place it at another bus.",
                    nameof(activeFrontEnds));
            if (!double.IsFinite(a.RatingMva) || !(a.RatingMva > 0.0))
                throw new ArgumentException($"three-phase harmonic penetration: the active-front-end converter at bus {a.Bus} has rating "
                    + $"{a.RatingMva:R} MVA; its impedance is on its rating, so the rating must be finite and above 0.", nameof(activeFrontEnds));
            FrequencyDependentYBus.ValidateActiveFrontEndImpedance(a.ResistancePu, a.ReactancePu);
            double scale = model.BaseMva / a.RatingMva;
            list.Add((u, a.ResistancePu * scale, a.ReactancePu * scale));
        }
        return list;
    }

    /// <summary>N167: the node of every bus once closed switches (zero-impedance branches) join their ends: each bus maps
    /// to the smallest bus id of its switch-connected group (deterministic). Without switches every bus is its own node.</summary>
    private static Dictionary<int, int> SwitchNodes(MultiPhaseModel model)
    {
        var parent = new Dictionary<int, int>(model.Buses.Count);
        foreach (PhaseBus b in model.Buses) parent[b.Id] = b.Id;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (PhaseBranch br in model.Branches)
        {
            if (!MultiPhaseModel.IsZeroImpedance(br.Z)) continue;
            if (!parent.ContainsKey(br.From) || !parent.ContainsKey(br.To))
                throw new ArgumentException($"three-phase harmonic penetration: switch {br.From}-{br.To} joins a bus that is not in the model.");
            int a = Find(br.From), c = Find(br.To);
            if (a == c) continue;
            if (a < c) parent[c] = a; else parent[a] = c;   // the smaller id is the group's name
        }
        var node = new Dictionary<int, int>(parent.Count);
        foreach (PhaseBus b in model.Buses) node[b.Id] = Find(b.Id);
        return node;
    }

    /// <summary>N167: the fundamental-frequency shunt admittance (3x3, per-unit) of every unknown node: wye capacitor
    /// susceptances j QPerPhase on the diagonal, and half of each branch's line-charging ShuntY at each end (the pi
    /// model the sweep uses). Shunts on a bus switched onto the source sit on the reference and are dropped (zero
    /// harmonic voltage there). Null for a node with no shunt.</summary>
    private static Complex[,]?[] BuildShunts(MultiPhaseModel model, Dictionary<int, int> reduced, int m)
    {
        var shunt = new Complex[,]?[m];
        void Add(int busId, int p, int q, Complex y)
        {
            if (y == Complex.Zero || !reduced.TryGetValue(busId, out int u)) return;
            (shunt[u] ??= new Complex[3, 3])[p, q] += y;
        }
        foreach (PhaseCapacitor cap in model.Capacitors)
            for (int p = 0; p < 3; p++) Add(cap.Bus, p, p, new Complex(0.0, cap.QPerPhase[p]));
        foreach (PhaseBranch br in model.Branches)
        {
            if (br.ShuntY is not { } y) continue;
            for (int p = 0; p < 3; p++)
                for (int q = 0; q < 3; q++)
                {
                    Complex half = y[p, q] * 0.5;
                    Add(br.From, p, q, half);
                    Add(br.To, p, q, half);
                }
        }
        return shunt;
    }
}
