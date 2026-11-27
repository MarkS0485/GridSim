using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.Workflow;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// A shunt fault at bus <paramref name="BusId"/> of type <paramref name="Type"/> through the fault impedance
/// Zf = <paramref name="FaultResistancePu"/> + j<paramref name="FaultReactancePu"/> (pu on the system base), in place until
/// a <see cref="RmsFaultClear"/> on the same bus. It is the standard positive-sequence RMS treatment of an unbalanced fault
/// (the balanced, positive-sequence RMS method of PSS/E and PowerFactory): at every step the positive-sequence network carries, at the fault
/// bus, the equivalent shunt built from the negative- and zero-sequence Thevenin impedances Z2 and Z0 seen there:
/// <list type="bullet">
/// <item>three-phase: Zf (Zf = 0 is the bolted fault, the very shunt <see cref="RmsBusFault"/> stamps);</item>
/// <item>single line to ground (phase a): Z2 + Z0 + 3 Zf;</item>
/// <item>line to line (b-c): Z2 + Zf;</item>
/// <item>double line to ground (b-c-g): Z2 in parallel with (Z0 + 3 Zf).</item>
/// </list>
/// Z2 and Z0 are those of the short-circuit sequence networks (<see cref="FaultAnalysis.Unbalanced"/>), recomputed for every
/// network stage, so a branch trip or a machine trip while the fault is on changes them. The positive-sequence fault
/// current, the negative- and zero-sequence currents and the phase voltages at the fault bus are reported per step
/// (<see cref="RmsTransientStability.StudyEventsWithFaults"/>). The machines see only the positive sequence: the braking of
/// negative-sequence currents in the rotor and every per-phase waveform detail are outside this model (use the EMT engine
/// for those).
/// </summary>
public sealed record RmsSequenceFault(double AtSeconds, int BusId, FaultType Type,
    double FaultResistancePu = 0.0, double FaultReactancePu = 0.0) : RmsEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() =>
        $"{RmsSequenceFaults.TypeText(Type)} fault at bus {BusId}{RmsSequenceFaults.ImpedanceText(FaultResistancePu, FaultReactancePu)} at {Num(AtSeconds)} s";
}

/// <summary>
/// A shunt fault a fraction <paramref name="Fraction"/> of the way along branch <paramref name="BranchIndex"/> (its index in
/// <c>model.Branches</c>, measured from its From bus), of type <paramref name="Type"/> through Zf =
/// <paramref name="FaultResistancePu"/> + j<paramref name="FaultReactancePu"/> pu. The line is split at the fault point
/// exactly as the static fault-along-line study splits it (<see cref="FaultAlongLine.Build"/>: R, X, B, R0 and X0 in the
/// ratio d : 1 - d); in the positive-sequence RMS network the fault point is eliminated (Kron reduction), so the two end
/// buses carry the reduced stamps of the split line with the equivalent fault shunt. Only a line (no tap, phase shift,
/// winding data or zero-sequence mutual coupling) can be faulted along its length, and the fraction must lie strictly
/// between 0 and 1 (at an end, fault the bus). The fault stays in place until a <see cref="RmsBranchFaultClear"/>; while
/// the branch is tripped it carries no current, and a reclose of the branch with the fault still in place recloses onto it.
/// </summary>
public sealed record RmsBranchSequenceFault(double AtSeconds, int BranchIndex, double Fraction, FaultType Type,
    double FaultResistancePu = 0.0, double FaultReactancePu = 0.0) : RmsEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() =>
        $"{RmsSequenceFaults.TypeText(Type)} fault {Num(Fraction)} along branch {BranchIndex}{RmsSequenceFaults.ImpedanceText(FaultResistancePu, FaultReactancePu)} at {Num(AtSeconds)} s";
}

/// <summary>The clearance of the fault along branch <paramref name="BranchIndex"/> (<see cref="RmsBranchSequenceFault"/>):
/// the fault point is removed and the line is whole again (unless it is tripped). Clearing by opening the line is a
/// <see cref="RmsBranchTrip"/>; the fault then carries no current, and this event removes it for good.</summary>
public sealed record RmsBranchFaultClear(double AtSeconds, int BranchIndex) : RmsEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() => $"fault clearance on branch {BranchIndex} at {Num(AtSeconds)} s";
}

/// <summary>
/// One step's quantities at the point of a sequence fault (phase-a reference, pu on the system base; currents flow into
/// the fault): the positive-sequence voltage <see cref="V1"/> solved by the RMS network, the sequence currents, the
/// negative- and zero-sequence voltages <c>V2 = -Z2 I2</c> and <c>V0 = -Z0 I0</c>, and the Thevenin impedances of that
/// stage. <see cref="Energised"/> is false while the faulted line is tripped (every quantity is then zero). An unearthed
/// fault point (<see cref="Grounded"/> false) carries no zero-sequence current, and its zero-sequence voltage (the neutral
/// displacement of an unearthed system) is not represented (reported 0), as in the static study.
/// </summary>
public sealed record RmsFaultSample(double TimeSeconds, bool Energised,
    Complex V1, Complex V2, Complex V0, Complex I1, Complex I2, Complex I0,
    Complex Z2Pu, Complex Z0Pu, bool Grounded)
{
    /// <summary>The phase voltage magnitudes (a, b, c) at the fault point, pu.</summary>
    public (double A, double B, double C) PhaseVoltagesPu => FaultAnalysis.PhaseMagnitudes(V0, V1, V2);

    /// <summary>The phase current magnitudes (a, b, c) into the fault, pu.</summary>
    public (double A, double B, double C) PhaseCurrentsPu => FaultAnalysis.PhaseMagnitudes(I0, I1, I2);

    /// <summary>The earth current |3 I0| into the fault, pu.</summary>
    public double EarthCurrentPu => (3.0 * I0).Magnitude;
}

/// <summary>The trace of one sequence fault event: which event, where, and one sample per integration step while it is in
/// place (from the step it is applied at to the step before its clearance).</summary>
public sealed record RmsFaultTrace(int EventIndex, string Description, FaultType Type, int? BusId, int? BranchIndex,
    double? Fraction, Complex FaultImpedancePu, IReadOnlyList<RmsFaultSample> Samples);

/// <summary>An RMS event study with the traces of its sequence faults (<see cref="RmsTransientStability.StudyEventsWithFaults"/>).
/// <see cref="Result"/> is exactly what <see cref="RmsTransientStability.StudyEvents"/> returns for the same inputs.</summary>
public sealed record RmsFaultStudyResult(MultiMachineSubtransient.Result Result, IReadOnlyList<RmsFaultTrace> Faults);

/// <summary>Text helpers and the fault-type arithmetic of the sequence faults.</summary>
public static class RmsSequenceFaults
{
    /// <summary>The zero-sequence multiple of Z1 taken for a line with no R0 / X0 (the static study's default).</summary>
    public const double LineZeroSeqRatio = 3.0;

    /// <summary>The fault type in words ("three-phase", "single line to ground", ...).</summary>
    public static string TypeText(FaultType type) => type switch
    {
        FaultType.ThreePhase => "three-phase",
        FaultType.SingleLineToGround => "single line to ground",
        FaultType.LineToLine => "line to line",
        FaultType.DoubleLineToGround => "double line to ground",
        _ => $"unknown ({(int)type})",
    };

    /// <summary>" through Zf = R + jX pu", or nothing for a bolted fault.</summary>
    internal static string ImpedanceText(double r, double x) => r == 0.0 && x == 0.0 ? ""
        : string.Create(CultureInfo.InvariantCulture, $" through Zf = {r:R} + j{x:R} pu");

    /// <summary>
    /// The equivalent shunt admittance the positive-sequence network carries for a fault of <paramref name="type"/>
    /// through <paramref name="zf"/>, given the negative- and zero-sequence Thevenin impedances. A three-phase fault with
    /// Zf = 0 is the bolted shunt <see cref="SubtransientInit.BoltedFaultShuntPu"/> (Bolted true). An earth fault at an
    /// unearthed point: a single line to ground fault draws nothing (admittance 0) and a double line to ground fault is
    /// the line to line fault (Z2, as the static study takes it).
    /// </summary>
    public static (Complex Admittance, bool Bolted) EquivalentShunt(FaultType type, Complex zf, Complex z2, Complex z0, bool grounded)
    {
        Complex zeq;
        switch (type)
        {
            case FaultType.ThreePhase:
                if (zf == Complex.Zero) return (new Complex(SubtransientInit.BoltedFaultShuntPu, 0.0), true);
                zeq = zf;
                break;
            case FaultType.SingleLineToGround:
                if (!grounded) return (Complex.Zero, false);
                zeq = z2 + z0 + 3.0 * zf;
                break;
            case FaultType.LineToLine:
                zeq = z2 + zf;
                break;
            case FaultType.DoubleLineToGround:
                if (!grounded) { zeq = z2; break; }
                Complex zg = z0 + 3.0 * zf;
                zeq = z2 * zg / (z2 + zg);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "unknown fault type");
        }
        if (zeq == Complex.Zero) return (new Complex(SubtransientInit.BoltedFaultShuntPu, 0.0), true);
        return (Complex.One / zeq, false);
    }

    /// <summary>The sequence currents (I1, I2, I0) into the fault from the positive-sequence current I1 = Y V1, by the
    /// sequence-network connection of each type (the same relations as <see cref="FaultAnalysis.Unbalanced"/>).</summary>
    public static (Complex I1, Complex I2, Complex I0) SequenceCurrents(FaultType type, Complex i1, Complex zf, Complex z2, Complex z0, bool grounded)
    {
        switch (type)
        {
            case FaultType.ThreePhase: return (i1, Complex.Zero, Complex.Zero);
            case FaultType.SingleLineToGround: return grounded ? (i1, i1, i1) : (Complex.Zero, Complex.Zero, Complex.Zero);
            case FaultType.LineToLine: return (i1, -i1, Complex.Zero);
            case FaultType.DoubleLineToGround:
                if (!grounded) return (i1, -i1, Complex.Zero);
                Complex zg = z0 + 3.0 * zf;
                return (i1, -i1 * zg / (z2 + zg), -i1 * z2 / (z2 + zg));
            default: throw new ArgumentOutOfRangeException(nameof(type), type, "unknown fault type");
        }
    }
}

/// <summary>A sequence fault in place: the event, where it is, its type and impedance.</summary>
internal sealed record ActiveSequenceFault(int EventIndex, RmsEvent Event, FaultType Type, Complex Zf, int BusId, int BranchIndex, double Fraction)
{
    public bool OnBranch => BranchIndex >= 0;
}

/// <summary>A sequence fault as one network stage carries it: its Thevenin impedances, its equivalent shunt and, for a
/// fault along a line, the Kron-reduced stamps of the split line and the coefficients that recover the fault point's
/// voltage from the end buses.</summary>
internal sealed record StageSequenceFault(ActiveSequenceFault Fault, bool Energised, Complex Z2, Complex Z0, bool Grounded,
    Complex Yeq, int BusIndex, int FromIndex, int ToIndex, Complex YFi, Complex YFj, Complex YFF,
    IReadOnlyList<(int Row, int Col, Complex V)> Stamps)
{
    /// <summary>The positive-sequence voltage at the fault point from the solved bus voltages.</summary>
    public Complex FaultPointVoltage(Complex[] v) => !Energised ? Complex.Zero
        : Fault.OnBranch ? -(YFi * v[FromIndex] + YFj * v[ToIndex]) / YFF : v[BusIndex];

    /// <summary>The sample at time <paramref name="t"/> from the solved bus voltages.</summary>
    public RmsFaultSample Sample(double t, Complex[] v)
    {
        if (!Energised)
            return new RmsFaultSample(t, false, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero, Z2, Z0, Grounded);
        Complex v1 = FaultPointVoltage(v);
        (Complex i1, Complex i2, Complex i0) = RmsSequenceFaults.SequenceCurrents(Fault.Type, Yeq * v1, Fault.Zf, Z2, Z0, Grounded);
        Complex v2 = -Z2 * i2;
        Complex v0 = Grounded ? -Z0 * i0 : Complex.Zero;
        return new RmsFaultSample(t, true, v1, v2, v0, i1, i2, i0, Z2, Z0, Grounded);
    }
}

/// <summary>
/// The negative- and zero-sequence networks of an RMS event study, for the Thevenin impedances at a fault point.
/// <list type="bullet">
/// <item><b>Negative sequence</b>: the branches in service at that stage with every phase shift reversed, the base loads
/// and the load steps as the same constant admittances the positive-sequence RMS network carries, each dynamic load by its
/// Norton admittance (as in the positive-sequence network; for an induction motor k / (Ra + jX') plus its compensating
/// shunt, the locked-rotor value that is the motor's negative-sequence impedance to first order), with its share taken
/// out of the bus's constant admittance, and each connected machine as 1 / j(X2 + Xt) on the system base, with X2 its
/// <see cref="Generator.NegativeSeqXPu"/> when given and otherwise its RMS X'' (the round-rotor convention X2 = X''d; the
/// Norton admittance it already has). Converter plant and units held as constant injections are open, as in the static
/// study.</item>
/// <item><b>Zero sequence</b>: the IEC 60909 zero-sequence network the static study uses (winding connections, neutral
/// reactances, line R0 / X0 or <see cref="RmsSequenceFaults.LineZeroSeqRatio"/> times Z1, machine zero-sequence
/// impedances), over the branches in service and the machines still connected.</item>
/// </list>
/// For an unbalanced fault, every balanced fault in place at the same time (a bolted <see cref="RmsBusFault"/>, or a
/// three-phase sequence fault at a bus or along a line) is also in both networks. It is the same shunt Yf from each phase
/// to earth, and the symmetrical-component transform of a balanced diagonal admittance is Yf in each sequence, so the
/// shunt enters the negative and zero sequences exactly as it enters the positive. Two unbalanced faults in place at once
/// (a cross-country fault) couple the sequence networks at two points, which one equivalent shunt per fault cannot
/// represent, so the event list refuses them.
/// Each is solved on the part of the network connected to the fault point (the static study's rule for the zero sequence),
/// on the sparse fault-network engine.
/// </summary>
internal sealed class RmsSequenceNetworks
{
    private readonly GridModel _model;
    private readonly SubtransientInit.SparseNetworkBasis _basis;
    private readonly int[] _generatorOfSlot;
    private readonly Complex[] _y2;

    public RmsSequenceNetworks(GridModel model, SubtransientInit.SparseNetworkBasis basis, int[] slotOfGenerator, int machineCount)
    {
        _model = model;
        _basis = basis;
        _generatorOfSlot = Enumerable.Repeat(-1, machineCount).ToArray();
        for (int g = 0; g < slotOfGenerator.Length; g++) if (slotOfGenerator[g] >= 0) _generatorOfSlot[slotOfGenerator[g]] = g;
        _y2 = new Complex[machineCount];
        for (int m = 0; m < machineCount; m++)
        {
            int g = _generatorOfSlot[m];
            Complex yPos = m < basis.MachineAdmittance.Count ? basis.MachineAdmittance[m] : Complex.Zero;
            if (g < 0 || !(model.Generators[g].NegativeSeqXPu > 0.0)) { _y2[m] = yPos; continue; }
            Generator gen = model.Generators[g];
            double toSys = model.BaseMva / MachineBase.EffectiveMva(gen, model.BaseMva);
            double x2 = (gen.NegativeSeqXPu + Math.Max(0.0, gen.UnitTransformerXPu)) * toSys;
            _y2[m] = Complex.One / new Complex(0.0, x2);
        }
    }

    /// <summary>The model of a stage: the tripped branches out of service (the model itself when none is).</summary>
    public GridModel StageModel(IReadOnlyCollection<int> tripped)
    {
        if (tripped.Count == 0) return _model;
        var set = new HashSet<int>(tripped);
        var branches = new List<Branch>(_model.Branches.Count);
        for (int j = 0; j < _model.Branches.Count; j++)
            branches.Add(set.Contains(j) ? _model.Branches[j] with { InService = false } : _model.Branches[j]);
        return _model.CopyWith(branches: branches);
    }

    /// <summary>Z2 and Z0 (with whether the point reaches an earthed neutral) at bus index <paramref name="k"/> of
    /// <paramref name="network"/> (the stage model, or the stage model split at a fault along a line, whose first
    /// <c>model.BusCount</c> buses are the model's own).</summary>
    public (Complex Z2, Complex Z0, bool Grounded) Thevenin(GridModel network, int k, bool needZero,
        IReadOnlyList<int> loadBuses, IReadOnlyDictionary<int, (double P, double Q)> loadDelta, bool[] machineOut, string what,
        IReadOnlyList<(int Index, Complex Y)>? balancedFaults = null)
    {
        double baseMva = _model.BaseMva;
        int n0 = _model.BusCount;
        // Negative sequence: loads (the RMS network's constant admittances), load steps, connected machines.
        var shunts = new List<(int Index, Complex Y)>();
        for (int i = 0; i < n0; i++)
        {
            Bus bus = _model.Buses[i];
            double pd = bus.Pd, qd = bus.Qd;
            // The part a dynamic load takes is not a constant admittance (as in SubtransientInit; never without dynamic loads).
            if (_basis.DynamicLoadByBus is { } dyn && dyn.TryGetValue(bus.Id, out (double P, double Q) taken)) { pd -= taken.P; qd -= taken.Q; }
            if (pd == 0 && qd == 0) continue;
            Complex v = _basis.InitialVoltageByBus.TryGetValue(bus.Id, out Complex vv) ? vv : Complex.One;
            double v2 = Math.Max(SubtransientInit.MinVoltageSquaredPu, v.Magnitude * v.Magnitude);
            shunts.Add((i, Complex.Conjugate(new Complex(pd, qd) / baseMva) / v2));
        }
        // Each dynamic load's Norton admittance, as the positive-sequence network stamps it.
        if (_basis.DynamicLoadStamps is { } dynStamps)
            foreach ((int i, Complex y) in dynStamps) shunts.Add((i, y));
        foreach (int busId in loadBuses)
        {
            (double dp, double dq) = loadDelta[busId];
            if (dp == 0.0 && dq == 0.0) continue;
            Complex v = _basis.InitialVoltageByBus.TryGetValue(busId, out Complex vv) ? vv : Complex.One;
            double v2 = Math.Max(SubtransientInit.MinVoltageSquaredPu, v.Magnitude * v.Magnitude);
            shunts.Add((_basis.BusIndex[busId], Complex.Conjugate(new Complex(dp, dq) / baseMva) / v2));
        }
        for (int m = 0; m < _y2.Length && m < _basis.MachineTerminal.Count; m++)
            if (!(m < machineOut.Length && machineOut[m]) && _y2[m] != Complex.Zero) shunts.Add((_basis.MachineTerminal[m], _y2[m]));
        // Balanced faults in place: the same shunt in every sequence.
        if (balancedFaults is not null)
            foreach ((int i, Complex y) in balancedFaults) shunts.Add((i, y));

        GridModel negative = ReversedPhaseShifts(network);
        List<(int Row, int Col, Complex V)> negTrip = new();
        foreach (KeyValuePair<(int Row, int Col), Complex> cell in YBus.BuildSparse(negative).Cells)
            negTrip.Add((cell.Key.Row, cell.Key.Col, cell.Value));
        foreach ((int i, Complex y) in shunts) negTrip.Add((i, i, y));
        Complex z2 = DrivingPoint(network.BusCount, negTrip, k, what, "negative");

        if (!needZero) return (z2, new Complex(double.PositiveInfinity, double.PositiveInfinity), false);

        // Zero sequence: the static study's network, with the tripped machines' own zero-sequence paths left out.
        GridModel zeroModel = network;
        bool anyOut = false;
        for (int m = 0; m < machineOut.Length; m++) if (machineOut[m] && m < _generatorOfSlot.Length && _generatorOfSlot[m] >= 0) { anyOut = true; break; }
        if (anyOut)
        {
            var outGen = new HashSet<int>();
            for (int m = 0; m < machineOut.Length; m++) if (machineOut[m] && m < _generatorOfSlot.Length && _generatorOfSlot[m] >= 0) outGen.Add(_generatorOfSlot[m]);
            var gens = new List<Generator>(network.Generators.Count);
            for (int g = 0; g < network.Generators.Count; g++) if (!outGen.Contains(g)) gens.Add(network.Generators[g]);
            zeroModel = network.CopyWith(generators: gens);
        }
        int n = zeroModel.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[zeroModel.Buses[i].Id] = i;
        Iec60909.ZeroSequenceNetwork zs = Iec60909.BuildZeroSequenceNetwork(zeroModel, index, RmsSequenceFaults.LineZeroSeqRatio);
        var comp = new List<int> { k };
        var seen = new bool[n];
        seen[k] = true;
        var queue = new Queue<int>();
        queue.Enqueue(k);
        bool grounded = zs.HasGround[k];
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int w in zs.Adjacency[u])
                if (!seen[w]) { seen[w] = true; comp.Add(w); grounded |= zs.HasGround[w]; queue.Enqueue(w); }
        }
        // A balanced fault to earth in the same zero-sequence island is an earth path.
        if (balancedFaults is not null)
            foreach ((int i, _) in balancedFaults) grounded |= seen[i];
        if (!grounded) return (z2, new Complex(double.PositiveInfinity, double.PositiveInfinity), false);
        var local = new Dictionary<int, int>(comp.Count);
        for (int a = 0; a < comp.Count; a++) local[comp[a]] = a;
        var subTrip = new List<(int Row, int Col, Complex V)>(zs.Trip.Count);
        foreach ((int r, int c, Complex v) in zs.Trip)
            if (local.TryGetValue(r, out int lr) && local.TryGetValue(c, out int lc)) subTrip.Add((lr, lc, v));
        if (balancedFaults is not null)
            foreach ((int i, Complex y) in balancedFaults)
                if (local.TryGetValue(i, out int li)) subTrip.Add((li, li, y));
        var el = new Complex[comp.Count];
        el[local[k]] = Complex.One;
        Complex z0;
        try { z0 = FaultNetworkSystem.FromTriplets(comp.Count, subTrip).Solve(el)[local[k]]; }
        catch (Exception ex) when (ex is InvalidOperationException or ArithmeticException)
        {
            throw new InvalidOperationException($"{what}: the zero-sequence network at the fault point cannot be solved ({ex.Message}).", ex);
        }
        if (!double.IsFinite(z0.Real) || !double.IsFinite(z0.Imaginary))
            throw new InvalidOperationException($"{what}: the zero-sequence Thevenin impedance at the fault point is not finite ({z0}).");
        return (z2, z0, true);
    }

    /// <summary>The driving-point impedance at <paramref name="k"/> of the triplet network, solved on the part of it
    /// connected to <paramref name="k"/> (so an island elsewhere with no path to ground cannot make it singular).</summary>
    private static Complex DrivingPoint(int n, List<(int Row, int Col, Complex V)> trip, int k, string what, string sequence)
    {
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach ((int r, int c, Complex v) in trip) if (r != c && v != Complex.Zero) adj[r].Add(c);
        var comp = new List<int> { k };
        var seen = new bool[n];
        seen[k] = true;
        var queue = new Queue<int>();
        queue.Enqueue(k);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int w in adj[u]) if (!seen[w]) { seen[w] = true; comp.Add(w); queue.Enqueue(w); }
        }
        var local = new Dictionary<int, int>(comp.Count);
        for (int a = 0; a < comp.Count; a++) local[comp[a]] = a;
        var sub = new List<(int Row, int Col, Complex V)>(trip.Count);
        foreach ((int r, int c, Complex v) in trip)
            if (local.TryGetValue(r, out int lr) && local.TryGetValue(c, out int lc)) sub.Add((lr, lc, v));
        var e = new Complex[comp.Count];
        e[local[k]] = Complex.One;
        Complex z;
        try { z = FaultNetworkSystem.FromTriplets(comp.Count, sub).Solve(e)[local[k]]; }
        catch (Exception ex) when (ex is InvalidOperationException or ArithmeticException)
        {
            throw new InvalidOperationException($"{what}: the {sequence}-sequence network at the fault point cannot be solved ({ex.Message}).", ex);
        }
        if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary))
            throw new InvalidOperationException($"{what}: the {sequence}-sequence Thevenin impedance at the fault point is not finite ({z}).");
        return z;
    }

    private static GridModel ReversedPhaseShifts(GridModel model)
    {
        bool any = false;
        foreach (Branch br in model.Branches) if (br.PhaseShiftDeg != 0.0) { any = true; break; }
        if (!any) return model;
        var branches = new List<Branch>(model.Branches.Count);
        foreach (Branch br in model.Branches) branches.Add(br.PhaseShiftDeg == 0.0 ? br : br with { PhaseShiftDeg = -br.PhaseShiftDeg });
        return model.CopyWith(branches: branches);
    }

    /// <summary>The balanced faults in place other than <paramref name="f"/>, as shunts for the sequence networks of the
    /// unbalanced fault <paramref name="f"/>: a bolted bus fault (<see cref="SubtransientInit.BoltedFaultShuntPu"/>), a
    /// three-phase sequence fault at a bus (1 / Zf, or the bolted shunt for Zf = 0), and a three-phase sequence fault along a
    /// line in service, for which the line is split at its fault point (the bus appended to the model) and the shunt put
    /// there. Bolted bus faults come first in their list order, then the sequence faults in event order. With none, the
    /// model is <paramref name="stageModel"/> itself and the list is null.</summary>
    private (GridModel Model, List<(int Index, Complex Y)>? Shunts) BalancedFaults(GridModel stageModel, ActiveSequenceFault f,
        IReadOnlyList<ActiveSequenceFault> active, List<int> trippedBranches, IReadOnlyList<int>? boltedBuses)
    {
        GridModel net = stageModel;
        List<(int Index, Complex Y)>? shunts = null;
        if (boltedBuses is not null)
            foreach (int b in boltedBuses)
                (shunts ??= new()).Add((_basis.BusIndex[b], new Complex(SubtransientInit.BoltedFaultShuntPu, 0.0)));
        foreach (ActiveSequenceFault g in active)
        {
            if (ReferenceEquals(g, f) || g.Type != FaultType.ThreePhase) continue;
            Complex y = RmsSequenceFaults.EquivalentShunt(FaultType.ThreePhase, g.Zf, Complex.Zero, Complex.Zero, false).Admittance;
            if (!g.OnBranch) { (shunts ??= new()).Add((_basis.BusIndex[g.BusId], y)); continue; }
            if (trippedBranches.Contains(g.BranchIndex)) continue;   // a fault on a tripped line carries nothing
            net = FaultAlongLine.Build(net, g.BranchIndex, g.Fraction).Model;
            (shunts ??= new()).Add((net.BusCount - 1, y));
        }
        return (net, shunts);
    }

    /// <summary>The stage form of every sequence fault in place (see <see cref="StageSequenceFault"/>).</summary>
    public List<StageSequenceFault> Stage(IReadOnlyList<ActiveSequenceFault> active, List<int> trippedBranches,
        List<int> loadBuses, Dictionary<int, (double P, double Q)> loadDelta, bool[] machineOut,
        IReadOnlyList<int>? boltedBuses = null)
    {
        var list = new List<StageSequenceFault>(active.Count);
        if (active.Count == 0) return list;
        GridModel stageModel = StageModel(trippedBranches);
        foreach (ActiveSequenceFault f in active)
        {
            string what = $"events[{f.EventIndex}] ({f.Event.Describe()})";
            bool needZero = f.Type is FaultType.SingleLineToGround or FaultType.DoubleLineToGround;
            // An unbalanced fault sees every balanced fault in place in its negative- and zero-sequence networks (none: the
            // network is the stage model itself and the list is null).
            (GridModel faultModel, List<(int Index, Complex Y)>? balanced) = f.Type == FaultType.ThreePhase
                ? (stageModel, null) : BalancedFaults(stageModel, f, active, trippedBranches, boltedBuses);
            if (!f.OnBranch)
            {
                int k = _basis.BusIndex[f.BusId];
                (Complex z2, Complex z0, bool grounded) = Thevenin(faultModel, k, needZero, loadBuses, loadDelta, machineOut, what, balanced);
                (Complex y, _) = RmsSequenceFaults.EquivalentShunt(f.Type, f.Zf, z2, z0, grounded);
                list.Add(new StageSequenceFault(f, true, z2, z0, grounded, y, k, -1, -1, Complex.Zero, Complex.Zero, Complex.Zero,
                    new[] { (k, k, y) }));
                continue;
            }
            Branch br = _model.Branches[f.BranchIndex];
            int fi = _basis.BusIndex[br.From], ti = _basis.BusIndex[br.To];
            if (trippedBranches.Contains(f.BranchIndex))
            {
                list.Add(new StageSequenceFault(f, false, Complex.Zero, Complex.Zero, false, Complex.Zero, -1, fi, ti,
                    Complex.Zero, Complex.Zero, Complex.Zero, Array.Empty<(int, int, Complex)>()));
                continue;
            }
            FaultNetwork split = FaultAlongLine.Build(faultModel, f.BranchIndex, f.Fraction);
            int kf = split.Model.BusCount - 1;   // the inserted fault-point bus is appended last
            (Complex bz2, Complex bz0, bool bg) = Thevenin(split.Model, kf, needZero, loadBuses, loadDelta, machineOut, what, balanced);
            (Complex by, _) = RmsSequenceFaults.EquivalentShunt(f.Type, f.Zf, bz2, bz0, bg);
            Branch a = split.Model.Branches[split.SplitBranch], b = split.Model.Branches[split.SecondSegmentIndex];
            BranchStamp sa = AdmittanceStamp.Branch(a.R, a.X, a.B, a.TapRatio, a.PhaseShiftDeg);
            BranchStamp sb = AdmittanceStamp.Branch(b.R, b.X, b.B, b.TapRatio, b.PhaseShiftDeg);
            // Node F joins From (segment a, F its to end) and To (segment b, F its from end), with the fault shunt.
            Complex yff = sa.Ytt + sb.Yff + by;
            var stamps = new (int, int, Complex)[]
            {
                (fi, fi, sa.Yff - sa.Yft * sa.Ytf / yff),
                (ti, ti, sb.Ytt - sb.Ytf * sb.Yft / yff),
                (fi, ti, -(sa.Yft * sb.Yft) / yff),
                (ti, fi, -(sb.Ytf * sa.Ytf) / yff),
            };
            list.Add(new StageSequenceFault(f, true, bz2, bz0, bg, by, -1, fi, ti, sa.Ytf, sb.Yft, yff, stamps));
        }
        return list;
    }
}

public static partial class RmsTransientStability
{
    /// <summary>
    /// An RMS event study (<see cref="StudyEvents"/>, the same inputs and the same refusals) that also records, at every
    /// step, the quantities at the point of each sequence fault (<see cref="RmsSequenceFault"/>,
    /// <see cref="RmsBranchSequenceFault"/>): the sequence currents into the fault, the sequence voltages, the phase
    /// voltages and currents, and the Thevenin impedances of the stage (<see cref="RmsFaultSample"/>). The fault-point
    /// voltage is a second, read-only solve of the network the step was committed against (the mechanism of
    /// <see cref="StudyEventsObserved"/>), so <see cref="RmsFaultStudyResult.Result"/> is the same bits as
    /// <see cref="StudyEvents"/> gives. Bolted faults (<see cref="RmsBusFault"/>) are not traced.
    /// </summary>
    public static RmsFaultStudyResult StudyEventsWithFaults(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsEvent> events,
        double? f0 = null, double stepSeconds = StudyDefaultStepSeconds,
        double durationSeconds = StudyDefaultDurationSeconds,
        double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null)
    {
        var stageFaults = new Dictionary<IRmsNetwork, List<StageSequenceFault>>(ReferenceEqualityComparer.Instance);
        var samples = new SortedDictionary<int, (ActiveSequenceFault Fault, List<RmsFaultSample> Samples)>();
        void OnStage(IRmsNetwork net, List<StageSequenceFault> faults)
        {
            stageFaults[net] = faults;
            foreach (StageSequenceFault s in faults)
                if (!samples.ContainsKey(s.Fault.EventIndex)) samples[s.Fault.EventIndex] = (s.Fault, new List<RmsFaultSample>());
        }
        Action<double, IRmsNetwork, Complex[], Complex[]> Observer(SubtransientInit.Prepared p)
        {
            SubtransientInit.SparseNetworkBasis basis = p.Basis
                ?? throw new InvalidOperationException("The fault trace needs the sparse network basis.");
            var v = new Complex[basis.BusCount];
            return (t, net, e, ic) =>
            {
                if (!stageFaults.TryGetValue(net, out List<StageSequenceFault>? faults) || faults.Count == 0) return;
                if (net is not SparseRmsNetwork sparse)
                    throw new InvalidOperationException("The fault trace needs the sparse RMS network at every stage.");
                sparse.SolveBusVoltagesInto(e, ic, v);
                foreach (StageSequenceFault s in faults) samples[s.Fault.EventIndex].Samples.Add(s.Sample(t, v));
            };
        }
        MultiMachineSubtransient.Result run = StudyEventsCore(model, result, data, events, f0, stepSeconds, durationSeconds,
            slipLimitRad, exciters, Observer, unitRelaysOut: null, stageFaultsOut: OnStage);
        var traces = new List<RmsFaultTrace>(samples.Count);
        foreach (KeyValuePair<int, (ActiveSequenceFault Fault, List<RmsFaultSample> Samples)> kv in samples)
        {
            ActiveSequenceFault f = kv.Value.Fault;
            traces.Add(new RmsFaultTrace(kv.Key, f.Event.Describe(), f.Type, f.OnBranch ? null : f.BusId,
                f.OnBranch ? f.BranchIndex : null, f.OnBranch ? f.Fraction : null, f.Zf, kv.Value.Samples));
        }
        return new RmsFaultStudyResult(run, traces);
    }

    /// <summary>
    /// The step-quantised critical-clearing bracket of a sequence fault (<see cref="RmsSequenceFault"/> of
    /// <paramref name="type"/> through <paramref name="faultResistancePu"/> + j<paramref name="faultReactancePu"/>) at
    /// <paramref name="faultBusId"/>: the search of <see cref="CriticalClearingBracketEvents"/> (the same trials, window,
    /// clearing actions, background events and refusals) with this fault in place of the bolted one.
    /// </summary>
    public static CctBracket CriticalClearingBracketSequenceFault(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data, int faultBusId,
        FaultType type, double faultResistancePu, double faultReactancePu,
        IReadOnlyList<RmsEvent> clearingActions, IReadOnlyList<RmsEvent>? background = null,
        double? f0 = null, double stepSeconds = CctDefaultStepSeconds,
        double maxSeconds = CctDefaultMaxSeconds, int iterations = CctDefaultBisectionIterations,
        double onsetSeconds = CctDefaultOnsetSeconds, double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null, int scanSteps = CctSearch.DefaultScanSteps)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(clearingActions);
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds))
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), $"The CCT step must be positive and finite (got {stepSeconds}).");
        if (!(maxSeconds > 0.0) || !double.IsFinite(maxSeconds))
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window must be positive and finite (got {maxSeconds}).");
        if (iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(iterations), $"The CCT bisection needs at least one halving (got {iterations}).");
        if (scanSteps < 0)
            throw new ArgumentOutOfRangeException(nameof(scanSteps), $"The CCT scan depth must be 0 or more steps (got {scanSteps}).");
        if (!(onsetSeconds > 0.0) || !double.IsFinite(onsetSeconds))
            throw new ArgumentOutOfRangeException(nameof(onsetSeconds), $"The fault onset must be positive and finite (got {onsetSeconds}).");
        for (int i = 0; i < clearingActions.Count; i++)
        {
            RmsEvent? a = clearingActions[i] ?? throw new ArgumentException($"clearingActions[{i}] is null.", nameof(clearingActions));
            if (a is RmsBusFault or RmsFaultClear or RmsSequenceFault or RmsBranchSequenceFault or RmsBranchFaultClear)
                throw new ArgumentException($"clearingActions[{i}] ({a.Describe()}): the search places the fault and its clearance itself; "
                    + "a clearing action is what else happens at the clearing instant (a branch trip, for example).", nameof(clearingActions));
        }
        if (background is not null)
            for (int i = 0; i < background.Count; i++)
                if (background[i] is null) throw new ArgumentException($"background[{i}] is null.", nameof(background));

        double stepsInWindow = maxSeconds / stepSeconds + MultiMachineSubtransient.StageTimeToleranceSteps;
        if (stepsInWindow < 1.0)
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window {maxSeconds} s is shorter than "
                + $"one integration step ({stepSeconds} s); there is no clearing time to search.");
        if (stepsInWindow > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window {maxSeconds} s holds more than {int.MaxValue} steps.");
        int maxSteps = (int)Math.Floor(stepsInWindow);

        int unsettledTrials = 0;
        bool Survives(int steps)
        {
            double clear = steps * stepSeconds;
            double at = onsetSeconds + clear;
            var events = new List<RmsEvent>(2 + clearingActions.Count + (background?.Count ?? 0))
            {
                new RmsSequenceFault(onsetSeconds, faultBusId, type, faultResistancePu, faultReactancePu),
                new RmsFaultClear(at, faultBusId),
            };
            foreach (RmsEvent a in clearingActions) events.Add(a with { AtSeconds = at });
            if (background is not null) events.AddRange(background);
            double duration = at + PostClearWindowSeconds;
            foreach (RmsEvent e in events) duration = Math.Max(duration, e.AtSeconds);
            MultiMachineSubtransient.Result trial = StudyEvents(model, result, data, events, f0, stepSeconds, duration, slipLimitRad, exciters);
            if (trial.ConverterIterationFailures > 0) unsettledTrials++;
            return trial.StayedInSynchronism;
        }

        return CctSearch.Run(Survives, maxSteps, stepSeconds, iterations, scanSteps) with { TrialsWithConverterIterationFailures = unsettledTrials };
    }

    /// <summary>Validation of a sequence fault's type and impedance (refused, naming the event).</summary>
    private static Complex SequenceFaultImpedance(FaultType type, double r, double x, string what, string paramName)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentException($"{what}: the fault type {(int)type} is not one of ThreePhase, SingleLineToGround, LineToLine or DoubleLineToGround.", paramName);
        if (!double.IsFinite(r) || !double.IsFinite(x))
            throw new ArgumentOutOfRangeException(paramName, $"{what}: the fault impedance must be finite.");
        if (r < 0.0)
            throw new ArgumentOutOfRangeException(paramName, r, $"{what}: the fault resistance must be 0 or more.");
        return new Complex(r, x);
    }
}
