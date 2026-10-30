using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Protection;

/// <summary>
/// The operating quantities every relay reads, computed ONCE from the solved state - so the relays are
/// pure comparisons and the whole assessment is a read-only overlay on the solve (it never calls the
/// solver or mutates anything). All electrical quantities are positive-sequence phasor values, in per-unit
/// on <see cref="GridModel.BaseMva"/> unless a base-kV is present (then a kV/amp form is offered too).
/// </summary>
public sealed class ProtectionContext
{
    // ---- Named floors (October 2026 review, WS13 R193). Values unchanged.

    /// <summary>A bus voltage at or below this (pu) is collapsed: currents are not derived as S/|V| from it.</summary>
    public const double MinVoltageForCurrentPu = 1e-9;

    /// <summary>An apparent power at or below this (pu) is no measurable flow: the apparent impedance a
    /// distance relay sees is infinite.</summary>
    public const double MinApparentPowerPu = 1e-12;

    public GridModel Model { get; }
    public PowerFlowResult Result { get; }
    public ProtectionSettings Settings { get; }
    public ProtectionTrajectory? Trajectory { get; }
    public double FrequencyHz { get; }

    /// <summary>F21: the unbalanced fault study the state came from, when there is one; the earth-fault and
    /// negative-sequence elements read its sequence currents. Null for a power flow or a balanced fault.</summary>
    public UnbalancedFaultResult? Sequence { get; }

    // Per-bus (indexed as result.BusIds).
    public IReadOnlyList<int> BusIds { get; }
    public double[] Vm { get; }         // pu
    /// <summary>N117 (October 2026): the lowest phase-voltage magnitude at each bus (pu). Equal to <see cref="Vm"/>
    /// (the same array values) for a power flow or a balanced fault; in an unbalanced study, the smallest of |Va|, |Vb|,
    /// |Vc| from the bus's sequence voltages, which the undervoltage element measures.</summary>
    public double[] VmPhaseMinPu { get; }
    /// <summary>N117: the highest phase-voltage magnitude at each bus (pu); the overvoltage element measures it.</summary>
    public double[] VmPhaseMaxPu { get; }
    public double[] BaseKv { get; }     // kV (0 if unknown)
    public double[] DiffCurrentPu { get; } // |injection - sum of incident branch currents| (busbar differential)

    // Per-branch (indexed as result.LineFlows).
    public IReadOnlyList<LineFlow> Flows { get; }
    public double[] IFromPu { get; }     // |I| at the from end, pu (N117: the largest PHASE current in an unbalanced study)
    public double[] IToPu { get; }       // |I| at the to end, pu (N117: likewise)
    public double[] IRatedPu { get; }    // rated current, pu (0 if unrated)
    public double[] LoadingFraction { get; } // max(|S_from|, |S_to|) / RateA (0 if unrated)
    public double[] ZAppMagPu { get; }   // apparent impedance |Z| seen by the relay at the from end, pu
    public double[] ZAppAngleDeg { get; }// its angle
    public double[] ZAppToMagPu { get; } // apparent impedance |Z| seen by the relay at the to end, pu
    public double[] ZAppToAngleDeg { get; } // its angle
    public double[] ZLineMagPu { get; }  // the branch's own series |z|, pu
    public double[] ZLineAngleDeg { get; }
    /// <summary>D-DOC-224 (October 2026): the signed real power into the from end of each branch, MW (in an unbalanced
    /// study, the from end's three-phase total). Diagnostic only: NO relay reads it. The reverse-power element (32) is a
    /// generator element and reads <see cref="Generator.Pg"/>; this array is kept for callers that inspect a context (it
    /// used to be described as the directional element's input, which it never was).</summary>
    public double[] PFromMw { get; }
    /// <summary>D-DOC-091 (October 2026): whether each branch is in service in the model (index-aligned with
    /// <see cref="Flows"/>). An out-of-service branch keeps a zero-flow entry in the line flows so the indices stay
    /// aligned, and the undercurrent element (37) used to read that zero as "a dead circuit still in service" and
    /// operate on every switched-out branch. The branch elements consult this array.</summary>
    public bool[] BranchInService { get; }

    private readonly Dictionary<int, int> _busIndex;

    // R128: bus id -> display name, built once. BusName used to scan Model.Buses on every call and is called for every
    // bus while building verdicts, so an assessment was O(n^2) in buses (about 19M record comparisons on gb-full).
    // First match wins, exactly as the old scan returned the first bus with the id.
    private readonly Dictionary<int, string> _busName;

    public ProtectionContext(GridModel model, PowerFlowResult result, ProtectionSettings settings,
        ProtectionTrajectory? trajectory, IReadOnlyDictionary<int, double>? faultCurrentPuByBus = null,
        UnbalancedFaultResult? sequence = null)
    {
        Model = model;
        Result = result;
        Settings = settings;
        Trajectory = trajectory;
        FrequencyHz = model.FrequencyHz;
        Sequence = sequence;

        BusIds = result.BusIds;
        int n = BusIds.Count;
        _busIndex = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) _busIndex[BusIds[i]] = i;
        _busName = new Dictionary<int, string>(Model.Buses.Count);
        foreach (Bus b in Model.Buses) _busName.TryAdd(b.Id, b.Name ?? $"bus {b.Id}");

        var kvById = new Dictionary<int, double>(model.Buses.Count);
        foreach (Bus b in model.Buses) kvById[b.Id] = b.BaseKv;

        Vm = new double[n];
        BaseKv = new double[n];
        for (int i = 0; i < n; i++)
        {
            Vm[i] = result.Vm[i];
            BaseKv[i] = kvById.GetValueOrDefault(BusIds[i], 0.0);
        }
        // N117: phase voltages. Without a sequence study they ARE the positive-sequence magnitudes (copies of Vm), so a
        // power flow or a balanced fault is assessed exactly as before.
        VmPhaseMinPu = (double[])Vm.Clone();
        VmPhaseMaxPu = (double[])Vm.Clone();

        double baseMva = model.BaseMva;
        Flows = result.LineFlows;
        int m = Flows.Count;
        IFromPu = new double[m];
        IToPu = new double[m];
        IRatedPu = new double[m];
        LoadingFraction = new double[m];
        ZAppMagPu = new double[m];
        ZAppAngleDeg = new double[m];
        ZAppToMagPu = new double[m];
        ZAppToAngleDeg = new double[m];
        ZLineMagPu = new double[m];
        ZLineAngleDeg = new double[m];
        PFromMw = new double[m];
        BranchInService = new bool[m];

        // Line series impedance per branch, matched to the flow list by (from,to) - the LineFlow order is
        // the branch order, but resolve defensively by endpoints.
        for (int l = 0; l < m; l++)
        {
            LineFlow lf = Flows[l];
            double vmFrom = VmOf(lf.From), vmTo = VmOf(lf.To);
            double sFrom = Math.Sqrt(lf.PFromMw * lf.PFromMw + lf.QFromMvar * lf.QFromMvar);
            double sTo = Math.Sqrt(lf.PToMw * lf.PToMw + lf.QToMvar * lf.QToMvar);
            double sFromPu = sFrom / baseMva, sToPu = sTo / baseMva;
            IFromPu[l] = vmFrom > MinVoltageForCurrentPu ? sFromPu / vmFrom : 0.0;
            IToPu[l] = vmTo > MinVoltageForCurrentPu ? sToPu / vmTo : 0.0;
            PFromMw[l] = lf.PFromMw;

            double ratePu = lf.RatingMva > 0 ? lf.RatingMva / baseMva : 0.0;
            IRatedPu[l] = ratePu; // rated current ~ rated MVA at nominal 1 pu voltage
            // Thermal loading on the HEAVIER end (R041, October 2026): the same max(|S_from|, |S_to|) the solvers put
            // in LineFlow.LoadingPercent. The From end alone under-assessed a line whose To end carries more MVA
            // (charging, losses), and made the 49 verdict depend on which way round the branch was stored.
            LoadingFraction[l] = lf.RatingMva > 0 ? Math.Max(sFrom, sTo) / lf.RatingMva : 0.0;

            // Apparent impedance seen by the distance relay at EACH end, looking into the line: Z = |V|^2 / conj(S),
            // with S the power flowing from that end's bus into the branch (R041). Every line has a relay at both
            // ends; evaluating only the From end made a fault next to the From bus invisible (S_from ~ 0 there)
            // while the To-end relay, which sees it as a forward fault, was never asked.
            (ZAppMagPu[l], ZAppAngleDeg[l]) = ApparentImpedance(vmFrom, sFromPu, lf.PFromMw / baseMva, lf.QFromMvar / baseMva);
            (ZAppToMagPu[l], ZAppToAngleDeg[l]) = ApparentImpedance(vmTo, sToPu, lf.PToMw / baseMva, lf.QToMvar / baseMva);

            Branch br = BranchFor(model, lf, l);
            BranchInService[l] = br.InService;   // D-DOC-091: read by the 37 element
            double zMag = Math.Sqrt(br.R * br.R + br.X * br.X);
            ZLineMagPu[l] = zMag;
            ZLineAngleDeg[l] = DetMath.Atan2(br.X, br.R) * 180.0 / Math.PI;
        }

        // N117 (October 2026): in an unbalanced study the phase elements measure PHASE quantities, not the positive
        // sequence. Each branch end's current becomes the largest of its three phase currents (Ia, Ib, Ic from the end's
        // sequence currents, BranchSequenceCurrents.MaxPhaseFromPu / MaxPhaseToPu), the thermal element's loading the
        // larger end's phase current over the rated current, the from-end power (PFromMw) the from end's total
        // three-phase power (the sum of its positive-, negative- and zero-sequence powers), and each bus's undervoltage
        // and overvoltage quantities the smallest and largest phase-voltage magnitudes. The distance element measures the
        // phase-to-phase loops (DistanceRelay). They used to read the faulted positive-sequence state alone, so a line to
        // line fault's phase currents (sqrt(3) |I1| on phases b and c) and the collapsed faulted phase of an earth fault
        // were never seen. Nothing here runs without a sequence study.
        if (sequence is not null) ApplyPhaseQuantities(sequence, model, baseMva);

        // Busbar differential: the KCL current residual at each bus (|injection - sum of branch currents -
        // shunt current|). A converged solve balances power, so this is ~machine-epsilon - the element is a
        // consistency check that only bites under an injected internal fault. The bus shunt (Gs/Bs) draws
        // S_shunt = Vm^2 (Gs - jBs) and must be netted off, or a capacitor bank reads as a false internal
        // fault.
        var gsById = new Dictionary<int, double>(model.Buses.Count);
        var bsById = new Dictionary<int, double>(model.Buses.Count);
        foreach (Bus b in model.Buses) { gsById[b.Id] = b.Gs; bsById[b.Id] = b.Bs; }

        DiffCurrentPu = new double[n];
        var netP = new double[n];
        var netQ = new double[n];
        for (int i = 0; i < n; i++)
        {
            netP[i] = result.PInjMw[i] / baseMva;
            netQ[i] = result.QInjMvar[i] / baseMva;
            double vm2 = Vm[i] * Vm[i];
            netP[i] -= gsById.GetValueOrDefault(BusIds[i], 0.0) / baseMva * vm2;     // shunt real draw
            netQ[i] += bsById.GetValueOrDefault(BusIds[i], 0.0) / baseMva * vm2;     // shunt reactive (Bs>0 supplies vars)
        }
        foreach (LineFlow lf in Flows)
        {
            if (_busIndex.TryGetValue(lf.From, out int fi)) { netP[fi] -= lf.PFromMw / baseMva; netQ[fi] -= lf.QFromMvar / baseMva; }
            if (_busIndex.TryGetValue(lf.To, out int ti)) { netP[ti] -= lf.PToMw / baseMva; netQ[ti] -= lf.QToMvar / baseMva; }
        }
        for (int i = 0; i < n; i++)
        {
            double sRes = Math.Sqrt(netP[i] * netP[i] + netQ[i] * netQ[i]);
            DiffCurrentPu[i] = Vm[i] > MinVoltageForCurrentPu ? sRes / Vm[i] : sRes;
        }

        // A fault within a busbar's differential zone is an unmetered current path the relay sees as the
        // sum of its circuit currents: inject the fault current at the faulted bus so 87 operates on it.
        if (faultCurrentPuByBus is not null)
            foreach ((int busId, double ifPu) in faultCurrentPuByBus)
            {
                int i = BusIndex(busId);
                if (i >= 0) DiffCurrentPu[i] = Math.Max(DiffCurrentPu[i], ifPu);
            }
    }

    /// <summary>N117: overwrite the per-branch currents, loadings and directional powers and the per-bus phase-voltage
    /// extremes with the phase quantities of the unbalanced study (see the constructor).</summary>
    private void ApplyPhaseQuantities(UnbalancedFaultResult seq, GridModel model, double baseMva)
    {
        var seqBus = new Dictionary<int, int>(seq.BusIds.Count);
        for (int i = 0; i < seq.BusIds.Count; i++) seqBus.TryAdd(seq.BusIds[i], i);
        for (int i = 0; i < BusIds.Count; i++)
        {
            if (!seqBus.TryGetValue(BusIds[i], out int k)) continue;
            (double a, double b, double c) = FaultAnalysis.PhaseMagnitudes(seq.V0[k], seq.V1[k], seq.V2[k]);
            VmPhaseMinPu[i] = Math.Min(a, Math.Min(b, c));
            VmPhaseMaxPu[i] = Math.Max(a, Math.Max(b, c));
        }
        if (seq.Branches.Count != Flows.Count)
            throw new InvalidOperationException(
                $"Protection: the unbalanced study has {seq.Branches.Count} branch results but the state has {Flows.Count} line flows; they were not solved on the same model '{model.Name}'.");
        for (int l = 0; l < Flows.Count; l++)
        {
            BranchSequenceCurrents bc = seq.Branches[l];
            LineFlow lf = Flows[l];
            if (bc.From != lf.From || bc.To != lf.To)
                throw new InvalidOperationException(
                    $"Protection: branch result {l} of the unbalanced study ({bc.From} -> {bc.To}) does not match line flow {l} ({lf.From} -> {lf.To}).");
            IFromPu[l] = bc.MaxPhaseFromPu;
            IToPu[l] = bc.MaxPhaseToPu;
            LoadingFraction[l] = IRatedPu[l] > 0 ? Math.Max(IFromPu[l], IToPu[l]) / IRatedPu[l] : 0.0;
            if (seqBus.TryGetValue(lf.From, out int f))
            {
                Complex sTotal = seq.V1[f] * Complex.Conjugate(bc.I1From) + seq.V2[f] * Complex.Conjugate(bc.I2From)
                               + seq.V0[f] * Complex.Conjugate(bc.I0From);
                PFromMw[l] = sTotal.Real * baseMva;
            }
        }
    }

    /// <summary>The effective relay settings for branch <paramref name="branchIndex"/>: a site-specific
    /// override if one is registered under the branch's name, otherwise the generic <see cref="Settings"/>.
    /// Fast no-op when no overrides are supplied (the common case).</summary>
    public ProtectionSettings SettingsFor(int branchIndex)
    {
        if (Settings.PerAsset is null) return Settings;
        string nm = branchIndex >= 0 && branchIndex < Flows.Count ? Flows[branchIndex].Name : "";
        return nm.Length > 0 && Settings.PerAsset.TryGetValue(nm, out ProtectionSettings? s) ? s : Settings;
    }

    public int BusIndex(int busId) => _busIndex.TryGetValue(busId, out int i) ? i : -1;
    /// <summary>The solved voltage magnitude (pu) of bus <paramref name="busId"/>. GR-623 (October 2026): a bus the
    /// result does not carry is refused, naming it. It used to read 1.0 pu, so a relay on a line to a bus missing from
    /// the result measured a healthy voltage that was never solved; a flow to such a bus means the result and the model
    /// do not belong together, the same caller error <see cref="BranchFor"/> refuses.</summary>
    public double VmOf(int busId)
    {
        int i = BusIndex(busId);
        if (i < 0)
            throw new InvalidOperationException(
                $"Protection: bus {busId} is not in the solved result ({Vm.Length} bus(es)); the result was not solved on this model.");
        return Vm[i];
    }
    public string BusName(int busId) => _busName.TryGetValue(busId, out string? name) ? name : $"bus {busId}";

    /// <summary>The apparent impedance (|Z| pu, angle deg) a distance relay computes from its bus voltage magnitude
    /// <paramref name="vm"/> and the power <paramref name="pPu"/> + j<paramref name="qPu"/> flowing from its bus into
    /// the line: <c>Z = |V|^2 / conj(S)</c>. No measurable power flow (|S| at or below 1e-12 pu) reads as an
    /// infinite impedance, which no zone reaches.</summary>
    private static (double MagPu, double AngleDeg) ApparentImpedance(double vm, double sPu, double pPu, double qPu)
    {
        if (!(sPu > MinApparentPowerPu)) return (double.PositiveInfinity, 0.0);
        var conjS = new Complex(pPu, -qPu);
        Complex z = vm * vm / conjS;
        return (z.Magnitude, DetMath.Phase(z) * 180.0 / Math.PI);
    }

    /// <summary>
    /// The model branch a solved line flow belongs to: the branch at the same index when its endpoints match (the
    /// solvers emit flows in branch order), otherwise the first branch with the same from and to buses.
    ///
    /// <para>R193 / Q13 (October 2026): a flow that matches NO branch now fails loud. It used to borrow the
    /// impedance of whatever branch sat at index min(l, n-1), or, with no branches at all, fabricate a 0.0001 pu
    /// branch, so a distance relay compared the apparent impedance against a reach computed from the wrong line.
    /// A flow with no branch means the result was solved on a different model than the one passed here, which is
    /// a caller error, not something to paper over.</para>
    /// </summary>
    private static Branch BranchFor(GridModel model, LineFlow lf, int l)
    {
        if (l < model.Branches.Count)
        {
            Branch cand = model.Branches[l];
            if (cand.From == lf.From && cand.To == lf.To) return cand;
        }
        foreach (Branch b in model.Branches) if (b.From == lf.From && b.To == lf.To) return b;
        throw new InvalidOperationException(
            $"Protection: line flow {l} ('{lf.Name}', bus {lf.From} -> {lf.To}) matches no branch of model '{model.Name}' " +
            $"({model.Branches.Count} branches); the power-flow result was not solved on this model.");
    }
}
