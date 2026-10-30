using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Protection;

/// <summary>The fault type. <see cref="FaultAnalysis.ThreePhase"/> solves the balanced fault on the positive-sequence
/// network; <see cref="FaultAnalysis.Unbalanced"/> (F21, October 2026) solves the unbalanced faults on the three
/// sequence networks.</summary>
public enum FaultType
{
    /// <summary>Balanced three-phase (or three-phase-to-earth) fault.</summary>
    ThreePhase,
    /// <summary>Single line (phase a) to ground: the three sequence networks in series.</summary>
    SingleLineToGround,
    /// <summary>Line to line (phases b and c), no earth: positive and negative networks in series (F21).</summary>
    LineToLine,
    /// <summary>Double line (phases b and c) to ground: negative and zero networks in parallel, in series with the
    /// positive (F21).</summary>
    DoubleLineToGround,
}

/// <summary>F21: one branch's sequence currents during an unbalanced fault, at each end (pu on the system base,
/// phase a reference, flowing INTO the branch at that end), with the derived phase, residual and negative-sequence
/// magnitudes a relay measures.</summary>
public sealed record BranchSequenceCurrents(
    int Index, string Name, int From, int To,
    Complex I1From, Complex I2From, Complex I0From, Complex I1To, Complex I2To, Complex I0To)
{
    /// <summary>The residual (earth) current |3 I0| at the from end, pu.</summary>
    public double ResidualFromPu => (3.0 * I0From).Magnitude;
    /// <summary>The residual (earth) current |3 I0| at the to end, pu.</summary>
    public double ResidualToPu => (3.0 * I0To).Magnitude;
    /// <summary>The negative-sequence current |I2| at the from end, pu.</summary>
    public double NegativeSequenceFromPu => I2From.Magnitude;
    /// <summary>The negative-sequence current |I2| at the to end, pu.</summary>
    public double NegativeSequenceToPu => I2To.Magnitude;
    /// <summary>The largest phase current magnitude at the from end, pu.</summary>
    public double MaxPhaseFromPu => FaultAnalysis.MaxPhase(I0From, I1From, I2From);
    /// <summary>The largest phase current magnitude at the to end, pu.</summary>
    public double MaxPhaseToPu => FaultAnalysis.MaxPhase(I0To, I1To, I2To);
}

/// <summary>
/// F21 (October 2026): the result of an unbalanced fault study (<see cref="FaultAnalysis.Unbalanced"/>). Sequence
/// quantities are phase-a referenced, pu on the system base. <see cref="PositiveSequenceState"/> is the faulted
/// positive-sequence network state (bus voltages and branch flows); the protection overlay evaluates its phase elements
/// on the PHASE currents and voltages built from the sequence quantities (N117, October 2026: it used to read this
/// positive-sequence state alone) and its earth-fault and negative-sequence elements on <see cref="Branches"/>.
/// </summary>
public sealed record UnbalancedFaultResult(
    FaultType Type,
    int FaultBusId,
    double FaultReactancePu,
    Complex I1, Complex I2, Complex I0,      // sequence currents into the fault
    double PhaseAPu, double PhaseBPu, double PhaseCPu,
    double EarthCurrentPu,                   // |3 I0| into the earth at the fault
    double BaseKv,                           // fault bus nominal kV (0 when unknown)
    bool FaultBusGrounded,                   // the fault bus reaches an earthed neutral in the zero sequence
    Complex Z1Pu, Complex Z2Pu, Complex Z0Pu, // driving-point sequence impedances at the fault bus (Z0 infinite when ungrounded)
    IReadOnlyList<int> BusIds,
    Complex[] V1, Complex[] V2, Complex[] V0, // per-bus sequence voltages during the fault (model bus order)
    IReadOnlyList<BranchSequenceCurrents> Branches,
    PowerFlowResult PositiveSequenceState)
{
    /// <summary>The line zero-sequence multiple of Z1 the study used where a line carries no R0 / X0 (the 21N element
    /// uses the same value for its residual compensation).</summary>
    public double LineZeroSeqRatio { get; init; } = 3.0;

    /// <summary>N118 (October 2026): the converter-interfaced units the study gives no fault infeed. Their negative- and
    /// zero-sequence fault behaviour is not defined in the model (no parameter for it exists), so they are left at their
    /// prefault current in all three sequence networks rather than given an invented response; the fault currents leave
    /// their contribution out. Empty when the model has none. (The balanced study, <see cref="FaultAnalysis.ThreePhase"/>,
    /// models their positive-sequence fast fault current, which the model does define.)</summary>
    public IReadOnlyList<ConverterWithoutFaultInfeed> ConvertersWithoutFaultInfeed { get; init; } = Array.Empty<ConverterWithoutFaultInfeed>();

    /// <summary>Phase current in kA (0 when the bus has no base kV).</summary>
    public double ToKa(double pu, double baseMva) => BaseKv > 0 ? pu * baseMva / (Math.Sqrt(3.0) * BaseKv) : 0.0;

    /// <summary>The faulted bus's phase voltage magnitudes (a, b, c), pu.</summary>
    public (double Va, double Vb, double Vc) FaultBusPhaseVoltages()
    {
        int k = -1;
        for (int i = 0; i < BusIds.Count; i++) if (BusIds[i] == FaultBusId) { k = i; break; }
        if (k < 0) return (double.NaN, double.NaN, double.NaN);
        return FaultAnalysis.PhaseMagnitudes(V0[k], V1[k], V2[k]);
    }
}

/// <summary>N118 (October 2026): a converter-interfaced unit an unbalanced fault study gives no fault infeed (see
/// <see cref="UnbalancedFaultResult.ConvertersWithoutFaultInfeed"/>): its bus, name and rated MVA (its machine base).</summary>
public sealed record ConverterWithoutFaultInfeed(int Bus, string Name, double RatedMva);

/// <summary>The faulted network state: the collapsed voltages and elevated branch currents produced by a
/// fault, plus the fault current itself and the per-bus differential current a busbar relay would see.</summary>
public sealed record FaultResult(
    PowerFlowResult State,          // faulted voltages + recomputed branch flows (a normal PowerFlowResult)
    int FaultBusId,
    double FaultCurrentPu,          // |If| into the fault, pu
    double FaultCurrentKa,          // in kA if the bus base-kV is known, else 0
    double TheveninZPu,             // the Thevenin impedance magnitude seen at the fault bus
    IReadOnlyDictionary<int, double> DifferentialCurrentPuByBus, // fault current appears at the faulted bus
    IReadOnlyList<ConverterFaultInfeed>? Converters = null,       // converter-interfaced units' during-fault currents
    bool ConvertersConverged = true);                              // the converter current fixed point converged

/// <summary>A converter-interfaced unit's state during the fault: its terminal voltage, its injected current
/// (pu on the system base, and as a multiple of its own rated current), the active/reactive split, and whether it
/// sits on its current limit.</summary>
public sealed record ConverterFaultInfeed(
    int Bus, string Name, double TerminalVoltagePu, double CurrentPu, double CurrentPerRated,
    double ActiveCurrentPu, double ReactiveCurrentPu, bool AtCurrentLimit);

/// <summary>
/// Balanced three-phase fault analysis by the classic Thevenin / bus-impedance superposition. From the
/// PREFAULT solved voltages it builds the fault-study admittance (network Ybus + loads as constant-
/// impedance shunts + generators as their subtransient reactance to ground), solves one column of the bus
/// impedance matrix for the fault bus, and superposes the fault: `If = V0_k / (Zth_kk + Zf)`,
/// `V_faulted = V0 - Zbus[:,k].If`. The faulted branch currents then drive the protection overlay, so the
/// current/voltage/distance/differential elements operate on a real fault - the demonstration the passive
/// assessment needed. Generator subtransient reactances are synthesised (a generic X"d, since the model
/// does not carry machine reactances) and provenance-noted; the whole thing is positive-sequence, so only
/// the three-phase fault is represented (an earth fault is reported not-representable).
///
/// Converter-interfaced generators (<see cref="Generator.IsConverterInterfaced"/>: batteries, PV, wind, VSC
/// HVDC) are <b>not</b> given an X"d: they are current-limited injections following the grid-code fast
/// fault-current characteristic (see <c>SolveWithConverters</c>), so their infeed is capped near rated current
/// instead of the several-times-rated an X"d source would claim.
///
/// The fault admittance is factored on the sparse engine (<see cref="FaultNetworkSystem"/>) at every size, so a small
/// case and gb-full run the same arithmetic (N233, October 2026: the old dense path up to 1,500 buses is gone).
/// </summary>
public static class FaultAnalysis
{
    /// <summary>The default synthesised generator subtransient reactance (pu on the machine base) used when
    /// the model carries no machine reactance - a typical X"d for a large synchronous machine.</summary>
    public const double DefaultSubtransientXdPu = 0.20;

    /// <summary>The fast-fault-current-injection gain <c>K = dIq / dV</c> (pu reactive current per pu voltage
    /// dip, on the unit's rating) - the grid-code voltage-support characteristic (ENTSO-E RfG / VDE-AR-N 4120
    /// default K = 2).</summary>
    public const double DefaultConverterReactiveGain = 2.0;

    /// <summary>
    /// GR-135: an isolated bus (no in-service branch, no load, no bus shunt and no machine admittance) has an empty row
    /// and column, so the whole fault admittance was singular and a fault ANYWHERE in the model failed (IEC 60909 cuts to
    /// the fault's island and did not). Such a bus is decoupled from every other one, so it is given a unit admittance to
    /// ground here: its Zbus row and column stay zero off the diagonal, the fault current and every other bus's faulted
    /// voltage are those of the connected network, and the isolated bus keeps its pre-fault voltage. A model with no
    /// isolated bus adds nothing (unchanged, bit for bit). A fault ON an isolated bus has no source and is refused by name.
    /// </summary>
    private static void GroundIsolatedBuses(GridModel model, Dictionary<int, int> index, List<(int Index, Complex Y)> shunts,
        int faultIndex, int faultBusId)
    {
        int n = model.BusCount;
        var touched = new bool[n];
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            if (index.TryGetValue(br.From, out int fi)) touched[fi] = true;
            if (index.TryGetValue(br.To, out int ti)) touched[ti] = true;
        }
        for (int i = 0; i < n; i++)
            if (model.Buses[i].Gs != 0.0 || model.Buses[i].Bs != 0.0) touched[i] = true;
        foreach ((int si, Complex _) in shunts) touched[si] = true;
        if (!touched[faultIndex])
            throw new InvalidOperationException(
                $"Fault bus {faultBusId} is isolated (no in-service branch, load, shunt or machine), so no current can flow into a fault there.");
        for (int i = 0; i < n; i++)
            if (!touched[i]) shunts.Add((i, Complex.One));
    }

    /// <summary>Apply a balanced three-phase fault at <paramref name="faultBusId"/> with fault impedance
    /// <paramref name="faultReactancePu"/> (0 = bolted) on top of the prefault state.</summary>
    public static FaultResult ThreePhase(
        GridModel model, PowerFlowResult prefault, int faultBusId,
        double faultReactancePu = 0.0, double subtransientXdPu = DefaultSubtransientXdPu,
        double converterReactiveGain = DefaultConverterReactiveGain)
    {
        prefault.RequireConverged("Fault analysis");   // the convergence contract (October 2026, WS4)
        int n = model.BusCount;

        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        if (!index.TryGetValue(faultBusId, out int k))
            throw new ArgumentException($"fault bus {faultBusId} is not in the model", nameof(faultBusId));

        double baseMva = model.BaseMva;

        // Prefault voltages (complex, pu), keyed to the model bus order.
        var v0 = new Complex[n];
        var vmById = new Dictionary<int, (double Vm, double VaDeg)>(n);
        for (int i = 0; i < prefault.BusIds.Count; i++)
            vmById[prefault.BusIds[i]] = (prefault.Vm[i], prefault.VaDeg[i]);
        for (int i = 0; i < n; i++)
        {
            (double vm, double vaDeg) = vmById.GetValueOrDefault(model.Buses[i].Id, (model.Buses[i].Vm, model.Buses[i].VaDeg));
            v0[i] = DetMath.FromPolar(vm, vaDeg * Math.PI / 180.0);
        }

        // Fault-study admittance: network Ybus (branches + bus shunts) plus the diagonal stamps below, in
        // order, factored once by FaultNetworkSystem (the sparse engine at every size, N233). The pristine
        // network admittance needed for the post-fault nodal injections is recovered from the sparse cells.
        var shunts = new List<(int Index, Complex Y)>();
        // ... each load as a constant-impedance shunt (Y = conj(Sload)/|V0|^2) ...
        foreach (Bus b in model.Buses)
        {
            if (b.Pd == 0 && b.Qd == 0) continue;
            int i = index[b.Id];
            double vm2 = Math.Max(v0[i].Magnitude * v0[i].Magnitude, 1e-6);
            var sLoadPu = new Complex(b.Pd, b.Qd) / baseMva;
            shunts.Add((i, Complex.Conjugate(sLoadPu) / vm2));
        }
        // ... plus each synchronous generator as its subtransient reactance to ground (the source, shorted for
        // the superposition, leaves its internal admittance behind). A converter-interfaced unit has no EMF
        // behind a reactance: it is left open here and enters below as a current-limited injection.
        var converters = new List<(Generator G, int I)>();
        foreach (Generator g in model.Generators)
        {
            if (!index.TryGetValue(g.Bus, out int i)) continue;
            if (g.PowerFlowOnly) continue;          // a power-flow device: its prefault injection simply persists
            if (g.IsConverterInterfaced) { converters.Add((g, i)); continue; }
            double xdSys = g.FaultSourceReactancePu(subtransientXdPu) * baseMva / (g.MBase <= 0 ? baseMva : g.MBase);
            if (xdSys <= 0) continue;
            shunts.Add((i, Complex.One / new Complex(0, xdSys)));
        }
        GroundIsolatedBuses(model, index, shunts, k, faultBusId);   // GR-135
        FaultNetworkSystem lu = FaultNetworkSystem.ForModel(model, shunts);

        // One column of Zbus: solve y . x = e_k.
        var e = new Complex[n];
        e[k] = Complex.One;
        Complex zf = new Complex(0, faultReactancePu);
        Complex[] zColumn;
        Complex ifault;
        var vf = new Complex[n];
        List<ConverterFaultInfeed>? infeeds = null;
        bool convConverged = true;
        zColumn = lu.Solve(e);
        if (converters.Count == 0)
        {
            ifault = v0[k] / (zColumn[k] + zf);
            // Superpose the fault: faulted voltages.
            for (int i = 0; i < n; i++) vf[i] = v0[i] - zColumn[i] * ifault;
        }
        else
        {
            (ifault, infeeds, convConverged) = SolveWithConverters(
                converters, lu, zColumn, k, zf, v0, vf, baseMva, converterReactiveGain);
        }
        Complex zth = zColumn[k];

        // Recompute branch flows and nodal injections from the faulted voltages (original network).
        (List<LineFlow> flows, double lossMw, double lossMvar) = FaultedFlows(model, index, vf, baseMva);

        // Nodal injections I = Y_network V over the pristine (unfaulted) admittance. The old code
        // rebuilt the full dense Y and summed all n columns per row (O(n^2), ~99.7% of them zero at
        // n=1500). Walk the sparse cells instead - they are bit-for-bit the dense build (pinned by
        // YBusSparseEquivalencePinTests). Per row the cells are summed in ASCENDING column order, so the
        // running sum hits the same nonzero terms in the same order as the dense j=0..n-1 loop; the
        // skipped structural zeros contribute nothing (x + 0.0 == x), so the result is bit-identical.
        SparseYBus yNet = YBus.BuildSparse(model);
        var rowCells = new List<(int Col, Complex V)>[n];
        for (int i = 0; i < n; i++) rowCells[i] = new List<(int, Complex)>(8);
        foreach (KeyValuePair<(int Row, int Col), Complex> cell in yNet.Cells)
            rowCells[cell.Key.Row].Add((cell.Key.Col, cell.Value));

        var pInj = new double[n];
        var qInj = new double[n];
        for (int i = 0; i < n; i++)
        {
            List<(int Col, Complex V)> row = rowCells[i];
            row.Sort(static (a, b) => a.Col.CompareTo(b.Col));
            Complex inj = Complex.Zero;
            for (int p = 0; p < row.Count; p++) inj += row[p].V * vf[row[p].Col];
            Complex s = vf[i] * Complex.Conjugate(inj) * baseMva;
            pInj[i] = s.Real; qInj[i] = s.Imaginary;
        }

        var busIds = model.Buses.Select(b => b.Id).ToArray();
        var vmArr = new double[n];
        var vaArr = new double[n];
        for (int i = 0; i < n; i++) { vmArr[i] = vf[i].Magnitude; vaArr[i] = DetMath.Phase(vf[i]) * 180.0 / Math.PI; }

        // The prefault state was required to converge (RequireConverged above), and without converters the faulted
        // state is a direct linear solve. With converters it rests on their current fixed point: when that did not
        // converge the state is NOT a converged result (it used to be reported Converged and assessed regardless), so
        // every study guarded by RequireConverged refuses it and the result says why (ConvertersConverged = false).
        var faulted = new PowerFlowResult
        {
            Converged = convConverged,
            Iterations = 0,
            MaxMismatch = 0.0,
            Status = convConverged ? PowerFlowStatus.Converged : PowerFlowStatus.MaxIterations,
            BusIds = busIds,
            Vm = vmArr,
            VaDeg = vaArr,
            PInjMw = pInj,
            QInjMvar = qInj,
            TotalLossMw = lossMw,
            TotalLossMvar = lossMvar,
            LineFlows = flows,
        };

        double ifPu = ifault.Magnitude;
        double baseKv = model.Buses[k].BaseKv;
        double ifKa = baseKv > 0 ? ifPu * baseMva / (Math.Sqrt(3.0) * baseKv) : 0.0;
        var diff = new Dictionary<int, double> { [faultBusId] = ifPu };

        return new FaultResult(faulted, faultBusId, ifPu, ifKa, zth.Magnitude, diff, infeeds, convConverged);
    }

    // =================================================================================================================
    // F21 (October 2026): unbalanced faults on the three sequence networks, from the solved prefault state.
    // =================================================================================================================

    /// <summary>The operator a = e^(j 120 deg) of the symmetrical-component transform.</summary>
    private static readonly Complex A = DetMath.FromPolar(1.0, 2.0 * Math.PI / 3.0);

    /// <summary>The phase phasors (a, b, c) from the sequence phasors (0, 1, 2): Ia = I0 + I1 + I2,
    /// Ib = I0 + a^2 I1 + a I2, Ic = I0 + a I1 + a^2 I2.</summary>
    internal static (Complex Pa, Complex Pb, Complex Pc) Phases(Complex s0, Complex s1, Complex s2)
        => (s0 + s1 + s2, s0 + A * A * s1 + A * s2, s0 + A * s1 + A * A * s2);

    /// <summary>The phase magnitudes from the sequence phasors.</summary>
    internal static (double Ma, double Mb, double Mc) PhaseMagnitudes(Complex s0, Complex s1, Complex s2)
    {
        (Complex a, Complex b, Complex c) = Phases(s0, s1, s2);
        return (a.Magnitude, b.Magnitude, c.Magnitude);
    }

    /// <summary>The largest phase magnitude from the sequence phasors.</summary>
    internal static double MaxPhase(Complex s0, Complex s1, Complex s2)
    {
        (double a, double b, double c) = PhaseMagnitudes(s0, s1, s2);
        return Math.Max(a, Math.Max(b, c));
    }

    /// <summary>
    /// An unbalanced fault (F21, October 2026) at <paramref name="faultBusId"/> on top of the solved prefault state, by
    /// the symmetrical-component superposition the balanced study uses (the classic Thevenin method of the textbooks,
    /// e.g. Grainger and Stevenson, Power System Analysis, ch. 12):
    /// <list type="bullet">
    /// <item><b>Positive sequence</b>: exactly the balanced study's network (network Ybus with line charging and bus
    /// shunts, each load as its constant-impedance shunt, each synchronous unit as its X''d plus unit transformer).</item>
    /// <item><b>Negative sequence</b>: the same passive network and loads, each unit as its X2
    /// (<see cref="Generator.EffectiveNegativeSeqXPu"/>, default X''d) plus unit transformer, and every phase shift
    /// REVERSED (a transformer that shifts the positive sequence by +phi shifts the negative by -phi).</item>
    /// <item><b>Zero sequence</b>: the IEC 60909 zero-sequence network (<see cref="ShortCircuit.Iec60909.ZeroSequenceThevenin"/>:
    /// winding connections and neutral reactances, line R0 / X0 or <paramref name="lineZeroSeqRatio"/> times Z1), with
    /// loads neglected (taken as delta or unearthed) and no line charging; only the part of the network connected to the
    /// fault bus through zero-sequence paths carries zero-sequence current.</item>
    /// </list>
    /// With V the prefault voltage at the fault bus, Z1 / Z2 / Z0 the driving-point impedances and Zf = j
    /// <paramref name="faultReactancePu"/> (the balanced study's convention): single line to ground (phase a)
    /// <c>I1 = I2 = I0 = V / (Z1 + Z2 + Z0 + 3 Zf)</c>; line to line (b-c) <c>I1 = -I2 = V / (Z1 + Z2 + Zf)</c>, I0 = 0;
    /// double line to ground (b-c-g) <c>I1 = V / (Z1 + Z2 (Z0 + 3Zf) / (Z2 + Z0 + 3Zf))</c>,
    /// <c>I2 = -I1 (Z0 + 3Zf) / (Z2 + Z0 + 3Zf)</c>, <c>I0 = -I1 Z2 / (Z2 + Z0 + 3Zf)</c>. Bus sequence voltages are
    /// <c>V1 = V0 - Z1[:,k] I1</c>, <c>V2 = -Z2[:,k] I2</c>, <c>V0seq = -Z0[:,k] I0</c>, and each branch's sequence
    /// currents follow from its sequence admittance. A fault bus with no earth path in the zero sequence carries no
    /// earth current: a single line to ground fault gives zero current and a double line to ground fault is a line to
    /// line fault (<see cref="UnbalancedFaultResult.FaultBusGrounded"/> false).
    /// <para>Limits, stated rather than hidden: converter-interfaced units keep their prefault current (open in all
    /// three networks; the balanced study's fast-fault-current fixed point is not extended to the sequence networks,
    /// because the model defines no negative- or zero-sequence fault behaviour for them; N118: they are listed in
    /// <see cref="UnbalancedFaultResult.ConvertersWithoutFaultInfeed"/> so a caller can say so), and
    /// generators have no direct zero-sequence path (a unit reaches it only through a grounded-wye transformer neutral).
    /// Every network is factored on the sparse engine (the same rule as the balanced study, N233).</para>
    /// </summary>
    public static UnbalancedFaultResult Unbalanced(
        GridModel model, PowerFlowResult prefault, int faultBusId, FaultType type,
        double faultReactancePu = 0.0, double subtransientXdPu = DefaultSubtransientXdPu, double lineZeroSeqRatio = 3.0)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(prefault);
        if (type == FaultType.ThreePhase)
            throw new ArgumentException("Unbalanced: a three-phase fault is solved by FaultAnalysis.ThreePhase.", nameof(type));
        prefault.RequireConverged("Fault analysis");
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        if (!index.TryGetValue(faultBusId, out int k))
            throw new ArgumentException($"fault bus {faultBusId} is not in the model", nameof(faultBusId));
        double baseMva = model.BaseMva;

        // Prefault voltages (as the balanced study reads them).
        var v0 = new Complex[n];
        var vmById = new Dictionary<int, (double Vm, double VaDeg)>(n);
        for (int i = 0; i < prefault.BusIds.Count; i++) vmById[prefault.BusIds[i]] = (prefault.Vm[i], prefault.VaDeg[i]);
        for (int i = 0; i < n; i++)
        {
            (double vm, double vaDeg) = vmById.GetValueOrDefault(model.Buses[i].Id, (model.Buses[i].Vm, model.Buses[i].VaDeg));
            v0[i] = DetMath.FromPolar(vm, vaDeg * Math.PI / 180.0);
        }

        // Positive and negative networks: loads as constant-impedance shunts, synchronous units as reactances.
        List<(int Index, Complex Y)> Shunts(Func<Generator, double> reactancePu)
        {
            var shunts = new List<(int Index, Complex Y)>();
            foreach (Bus b in model.Buses)
            {
                if (b.Pd == 0 && b.Qd == 0) continue;
                int i = index[b.Id];
                double vm2 = Math.Max(v0[i].Magnitude * v0[i].Magnitude, 1e-6);
                var sLoadPu = new Complex(b.Pd, b.Qd) / baseMva;
                shunts.Add((i, Complex.Conjugate(sLoadPu) / vm2));
            }
            foreach (Generator g in model.Generators)
            {
                if (!index.TryGetValue(g.Bus, out int i)) continue;
                if (g.PowerFlowOnly || g.IsConverterInterfaced) continue;
                double xSys = reactancePu(g) * baseMva / (g.MBase <= 0 ? baseMva : g.MBase);
                if (xSys <= 0) continue;
                shunts.Add((i, Complex.One / new Complex(0, xSys)));
            }
            GroundIsolatedBuses(model, index, shunts, k, faultBusId);   // GR-135
            return shunts;
        }
        var e = new Complex[n];
        e[k] = Complex.One;
        Complex[] z1col = FaultNetworkSystem.ForModel(model, Shunts(g => g.FaultSourceReactancePu(subtransientXdPu))).Solve(e);
        GridModel negModel = ReversedPhaseShifts(model);
        Complex[] z2col = FaultNetworkSystem.ForModel(negModel, Shunts(g => g.FaultSourceNegativeSeqReactancePu(subtransientXdPu))).Solve(e);

        // Zero sequence: the component of the fault bus, when it reaches an earthed neutral.
        ShortCircuit.Iec60909.ZeroSequenceNetwork zs = ShortCircuit.Iec60909.BuildZeroSequenceNetwork(model, index, lineZeroSeqRatio);
        var z0col = new Complex[n];
        bool grounded = false;
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
            if (grounded)
            {
                var local = new Dictionary<int, int>(comp.Count);
                for (int a = 0; a < comp.Count; a++) local[comp[a]] = a;
                var subTrip = new List<(int Row, int Col, Complex V)>(zs.Trip.Count);
                foreach ((int r, int c, Complex v) in zs.Trip)
                    if (local.TryGetValue(r, out int lr) && local.TryGetValue(c, out int lc)) subTrip.Add((lr, lc, v));
                var el = new Complex[comp.Count];
                el[local[k]] = Complex.One;
                Complex[] sub = FaultNetworkSystem.FromTriplets(comp.Count, subTrip).Solve(el);
                for (int a = 0; a < comp.Count; a++) z0col[comp[a]] = sub[a];
            }
        }

        Complex z1 = z1col[k], z2 = z2col[k], z0 = z0col[k];
        Complex zf = new Complex(0, faultReactancePu);
        Complex vk = v0[k];
        Complex i1, i2, i0;
        switch (type)
        {
            case FaultType.SingleLineToGround:
                if (grounded) { i1 = vk / (z1 + z2 + z0 + 3.0 * zf); i2 = i1; i0 = i1; }
                else { i1 = Complex.Zero; i2 = Complex.Zero; i0 = Complex.Zero; }   // no earth return: no current
                break;
            case FaultType.LineToLine:
                i1 = vk / (z1 + z2 + zf); i2 = -i1; i0 = Complex.Zero;
                break;
            case FaultType.DoubleLineToGround:
                if (grounded)
                {
                    Complex zg = z0 + 3.0 * zf;
                    i1 = vk / (z1 + z2 * zg / (z2 + zg));
                    i2 = -i1 * zg / (z2 + zg);
                    i0 = -i1 * z2 / (z2 + zg);
                }
                else { i1 = vk / (z1 + z2); i2 = -i1; i0 = Complex.Zero; }   // no earth return: the line-to-line fault
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "unknown fault type");
        }

        var v1 = new Complex[n];
        var v2 = new Complex[n];
        var vz = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            v1[i] = v0[i] - z1col[i] * i1;
            v2[i] = -z2col[i] * i2;
            vz[i] = -z0col[i] * i0;
        }

        // Branch sequence currents (into the branch at each end).
        var branches = new List<BranchSequenceCurrents>(model.Branches.Count);
        for (int l = 0; l < model.Branches.Count; l++)
        {
            Branch br = model.Branches[l];
            string name = string.IsNullOrEmpty(br.Name) ? $"branch {br.From}-{br.To}" : br.Name!;
            if (!br.InService || !index.TryGetValue(br.From, out int f) || !index.TryGetValue(br.To, out int t))
            {
                branches.Add(new BranchSequenceCurrents(l, name, br.From, br.To, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero));
                continue;
            }
            BranchStamp s1 = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
            BranchStamp s2 = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, -br.PhaseShiftDeg);
            Complex i1f = s1.Yff * v1[f] + s1.Yft * v1[t], i1t = s1.Ytf * v1[f] + s1.Ytt * v1[t];
            Complex i2f = s2.Yff * v2[f] + s2.Yft * v2[t], i2t = s2.Ytf * v2[f] + s2.Ytt * v2[t];
            Complex i0f = Complex.Zero, i0t = Complex.Zero;
            switch (zs.Kind[l])
            {
                case ShortCircuit.Iec60909.ZeroSeqBranchKind.Series:
                    i0f = zs.Admittance[l] * (vz[f] - vz[t]); i0t = -i0f; break;
                case ShortCircuit.Iec60909.ZeroSeqBranchKind.ShuntFrom:
                    i0f = zs.Admittance[l] * vz[f]; break;
                case ShortCircuit.Iec60909.ZeroSeqBranchKind.ShuntTo:
                    i0t = zs.Admittance[l] * vz[t]; break;
                case ShortCircuit.Iec60909.ZeroSeqBranchKind.Coupled:
                {
                    // A mutually coupled line: its group's inverse impedance row times each line's voltage difference.
                    (int[] group, Complex[] row) = zs.Coupled[l];
                    for (int b = 0; b < group.Length; b++)
                    {
                        Branch gb = model.Branches[group[b]];
                        i0f += row[b] * (vz[index[gb.From]] - vz[index[gb.To]]);
                    }
                    i0t = -i0f;
                    break;
                }
            }
            branches.Add(new BranchSequenceCurrents(l, name, br.From, br.To, i1f, i2f, i0f, i1t, i2t, i0t));
        }

        // The faulted positive-sequence state, for the phase elements of the protection overlay: bus voltages, branch
        // flows and nodal injections over the pristine network admittance, exactly as the balanced study forms them.
        (List<LineFlow> flows, double lossMw, double lossMvar) = FaultedFlows(model, index, v1, baseMva);
        SparseYBus yNet = YBus.BuildSparse(model);
        var rowCells = new List<(int Col, Complex V)>[n];
        for (int i = 0; i < n; i++) rowCells[i] = new List<(int, Complex)>(8);
        foreach (KeyValuePair<(int Row, int Col), Complex> cell in yNet.Cells)
            rowCells[cell.Key.Row].Add((cell.Key.Col, cell.Value));
        var pInj = new double[n];
        var qInj = new double[n];
        for (int i = 0; i < n; i++)
        {
            List<(int Col, Complex V)> row = rowCells[i];
            row.Sort(static (a, b) => a.Col.CompareTo(b.Col));
            Complex inj = Complex.Zero;
            for (int p = 0; p < row.Count; p++) inj += row[p].V * v1[row[p].Col];
            Complex s = v1[i] * Complex.Conjugate(inj) * baseMva;
            pInj[i] = s.Real; qInj[i] = s.Imaginary;
        }
        var vmArr = new double[n];
        var vaArr = new double[n];
        for (int i = 0; i < n; i++) { vmArr[i] = v1[i].Magnitude; vaArr[i] = DetMath.Phase(v1[i]) * 180.0 / Math.PI; }
        var state = new PowerFlowResult
        {
            Converged = true, Iterations = 0, MaxMismatch = 0.0, Status = PowerFlowStatus.Converged,
            BusIds = model.Buses.Select(b => b.Id).ToArray(), Vm = vmArr, VaDeg = vaArr,
            PInjMw = pInj, QInjMvar = qInj, TotalLossMw = lossMw, TotalLossMvar = lossMvar, LineFlows = flows,
        };

        // N118: the converter-interfaced units left at their prefault current (no defined negative / zero-sequence fault
        // behaviour), named so a caller can say the fault currents leave them out. Informational: no number depends on it.
        var noInfeed = new List<ConverterWithoutFaultInfeed>();
        foreach (Generator g in model.Generators)
            if (index.ContainsKey(g.Bus) && !g.PowerFlowOnly && g.IsConverterInterfaced)
                noInfeed.Add(new ConverterWithoutFaultInfeed(g.Bus, g.Name ?? $"gen@bus {g.Bus}", g.MBase));

        (double pa, double pb, double pc) = PhaseMagnitudes(i0, i1, i2);
        return new UnbalancedFaultResult(type, faultBusId, faultReactancePu, i1, i2, i0, pa, pb, pc, (3.0 * i0).Magnitude,
            model.Buses[k].BaseKv, grounded, z1, z2, grounded ? z0 : new Complex(double.PositiveInfinity, double.PositiveInfinity),
            state.BusIds, v1, v2, vz, branches, state) { LineZeroSeqRatio = lineZeroSeqRatio, ConvertersWithoutFaultInfeed = noInfeed };
    }

    /// <summary>The model with every branch's phase shift reversed (the negative-sequence network, F21).</summary>
    private static GridModel ReversedPhaseShifts(GridModel model)
    {
        bool any = false;
        foreach (Branch br in model.Branches) if (br.PhaseShiftDeg != 0.0) { any = true; break; }
        if (!any) return model;
        var branches = new List<Branch>(model.Branches.Count);
        foreach (Branch br in model.Branches) branches.Add(br.PhaseShiftDeg == 0.0 ? br : br with { PhaseShiftDeg = -br.PhaseShiftDeg });
        return model.CopyWith(branches: branches);
    }

    /// <summary>
    /// The fault with converter-interfaced units as <b>current-limited, voltage-dependent</b> injections rather
    /// than EMFs behind X''d. Each unit follows the grid-code fast-fault-current characteristic: reactive current
    /// rises by <c>K * (|V0| - |V|) * I_r</c> above its prefault value, the total is capped at <c>k * I_r</c> with
    /// <b>reactive priority</b> (active current gives way first), and the current is phased on the unit's own
    /// terminal voltage. Because that depends on the during-fault voltage, the network and converter currents are
    /// solved as an adaptively damped fixed point on one LU factorization: with <c>dI</c> the change of each converter's
    /// current from its prefault value, <c>x = Z dI</c>, <c>If = (V0_k + x_k) / (Z_kk + Zf)</c> and
    /// <c>V = V0 + x - Z[:,k] If</c>. Fills <paramref name="vf"/>; returns the fault current, the per-unit
    /// infeeds and whether the fixed point converged.
    /// </summary>
    private static (Complex IFault, List<ConverterFaultInfeed> Infeeds, bool Converged) SolveWithConverters(
        List<(Generator G, int I)> converters, FaultNetworkSystem lu, Complex[] zColumn,
        int k, Complex zf, Complex[] v0, Complex[] vf, double baseMva, double reactiveGain)
    {
        int n = v0.Length;
        int m = converters.Count;
        var ir = new double[m];           // rated current, system pu
        var imax = new double[m];         // current limit k * Ir
        var ip0 = new double[m];          // prefault active current (signed: a charging battery is negative)
        var iq0 = new double[m];          // prefault reactive current (exporting Q positive)
        var i0 = new Complex[m];          // prefault current phasor
        for (int c = 0; c < m; c++)
        {
            (Generator g, int bi) = converters[c];
            double mBase = g.MBase <= 0 ? baseMva : g.MBase;
            ir[c] = mBase / baseMva;
            imax[c] = g.EffectiveConverterFaultCurrentPu * ir[c];
            double vm0 = Math.Max(v0[bi].Magnitude, 1e-6);
            ip0[c] = g.Pg / baseMva / vm0;
            iq0[c] = g.Qg / baseMva / vm0;
            // S = V conj(I) with I = (Ip - j Iq) e^{j theta}  =>  S = |V| (Ip + j Iq).
            i0[c] = new Complex(ip0[c], -iq0[c]) * DetMath.FromPolar(1.0, DetMath.Phase(v0[bi]));
        }

        var iNow = (Complex[])i0.Clone();   // start from the prefault currents (dI = 0)
        var ip = new double[m];
        var iq = new double[m];
        var limited = new bool[m];
        Complex ifault = Complex.Zero;
        bool converged = false;
        const int maxIter = 1000;
        const double tol = 1e-9;
        double damping = 0.5, prevChange = double.PositiveInfinity;

        void Network()
        {
            var dI = new Complex[n];
            for (int c = 0; c < m; c++) dI[converters[c].I] += iNow[c] - i0[c];
            Complex[] x = lu.Solve(dI);
            ifault = (v0[k] + x[k]) / (zColumn[k] + zf);
            for (int i = 0; i < n; i++) vf[i] = v0[i] + x[i] - zColumn[i] * ifault;
        }

        for (int it = 0; it < maxIter; it++)
        {
            Network();
            double change = 0.0;
            for (int c = 0; c < m; c++)
            {
                int bi = converters[c].I;
                double vm = vf[bi].Magnitude;
                double theta = vm > 1e-3 ? DetMath.Phase(vf[bi]) : DetMath.Phase(v0[bi]);   // a collapsed terminal keeps its prefault angle
                double dip = v0[bi].Magnitude - vm;
                double q = Math.Clamp(iq0[c] + reactiveGain * dip * ir[c], -imax[c], imax[c]);
                double pRoom = Math.Sqrt(Math.Max(0.0, imax[c] * imax[c] - q * q));
                double p = Math.Sign(ip0[c]) * Math.Min(Math.Abs(ip0[c]), pRoom);
                limited[c] = Math.Abs(q) >= imax[c] - 1e-12 || Math.Abs(ip0[c]) > pRoom;
                Complex target = new Complex(p, -q) * DetMath.FromPolar(1.0, theta);
                Complex next = iNow[c] + damping * (target - iNow[c]);
                change = Math.Max(change, (next - iNow[c]).Magnitude);
                iNow[c] = next;
                ip[c] = p; iq[c] = q;
            }
            if (change < tol) { converged = true; break; }
            // Adaptive damping: many voltage-dependent converters on a weak network can make the plain
            // fixed point oscillate; back the step off whenever the residual grows.
            if (change > prevChange) damping = Math.Max(0.02, damping * 0.5);
            prevChange = change;
        }
        Network();   // final network state consistent with the settled currents

        var infeeds = new List<ConverterFaultInfeed>(m);
        for (int c = 0; c < m; c++)
        {
            (Generator g, int bi) = converters[c];
            double mag = iNow[c].Magnitude;
            infeeds.Add(new ConverterFaultInfeed(g.Bus, g.Name ?? $"gen@bus {g.Bus}", vf[bi].Magnitude, mag,
                ir[c] > 0 ? mag / ir[c] : 0.0, ip[c], iq[c], limited[c]));
        }
        return (ifault, infeeds, converged);
    }

    /// <summary>Branch flows from a given complex voltage vector, using the same pi-model as YBus.</summary>
    private static (List<LineFlow> Flows, double LossMw, double LossMvar) FaultedFlows(
        GridModel model, Dictionary<int, int> index, Complex[] v, double baseMva)
    {
        var flows = new List<LineFlow>(model.Branches.Count);
        double lossMw = 0, lossMvar = 0;
        foreach (Branch br in model.Branches)
        {
            if (!br.InService)
            {
                // Out of service: carries no flow, but keep the entry so LineFlows stays index-aligned
                // with Branches (same contract as NewtonShell.ComputeLineFlows). Dropping it shifts every
                // later flow left of its branch, so relay LocationIds and parallel-circuit impedance
                // lookups would point one branch off.
                flows.Add(new LineFlow(br.From, br.To, 0, 0, 0, 0, 0, 0, br.Name ?? "", br.RateAMva, 0));
                continue;
            }
            int f = index[br.From], t = index[br.To];
            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);

            Complex iFrom = s.Yff * v[f] + s.Yft * v[t];
            Complex iTo = s.Ytf * v[f] + s.Ytt * v[t];
            Complex sFrom = v[f] * Complex.Conjugate(iFrom) * baseMva;
            Complex sTo = v[t] * Complex.Conjugate(iTo) * baseMva;
            double pLoss = sFrom.Real + sTo.Real;
            double qLoss = sFrom.Imaginary + sTo.Imaginary;
            lossMw += pLoss; lossMvar += qLoss;

            // D-DOC-090 (October 2026): the loading is taken on the HEAVIER end, max(|S_from|, |S_to|) / RateA, the
            // contract every power-flow engine follows for LineFlow.LoadingPercent (R041). The fault states used the from
            // end alone, so a faulted line whose to end carries more MVA (the fault is at that end, and the series loss
            // makes |S_to| the larger) read as less loaded than it is, and the figure depended on which way round the
            // branch was stored. The protection elements are not affected (ProtectionContext forms its own loading from
            // both ends); this is the LineFlow the fault result hands to every other reader.
            double loading = br.RateAMva > 0 ? Math.Max(sFrom.Magnitude, sTo.Magnitude) / br.RateAMva * 100.0 : 0.0;
            flows.Add(new LineFlow(br.From, br.To, sFrom.Real, sFrom.Imaginary, sTo.Real, sTo.Imaginary,
                pLoss, qLoss, br.Name ?? "", br.RateAMva, loading));
        }
        return (flows, lossMw, lossMvar);
    }
}
