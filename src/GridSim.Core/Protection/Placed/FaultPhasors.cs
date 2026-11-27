using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.Workflow;

namespace GridSim.Core.Protection.Placed;

/// <summary>
/// The phasors a placed relay measures for one fault (RC5 WP4): per bus the sequence voltages, per branch the sequence
/// currents at each end (pu on the system base, phase-a reference, flowing INTO the branch at that end), on the fault
/// network (<see cref="FaultAlongLine.Build"/>: the case itself for a bus fault, the split copy for a fault along a line).
/// A three-phase fault comes from <see cref="FaultAnalysis.ThreePhase"/> (I0 = I2 = 0, each branch current from its own
/// pi stamp on the faulted voltages); the unbalanced faults come from <see cref="FaultAnalysis.Unbalanced"/>.
/// <see cref="NotEvaluated"/> is set (and every array is empty) when the fault could not be applied.
/// </summary>
public sealed class FaultPhasors
{
    private readonly Dictionary<int, int> _bus;

    private FaultPhasors(FaultSpec fault, FaultType type, double xf, FaultNetwork? net, string? notEvaluated, IReadOnlyList<int> busIds,
        Complex[] v0, Complex[] v1, Complex[] v2, IReadOnlyList<BranchSequenceCurrents> branches, IReadOnlyList<ConverterWithoutFaultInfeed> converters)
    {
        Fault = fault; Type = type; FaultReactancePu = xf; Network = net; NotEvaluated = notEvaluated; BusIds = busIds;
        V0 = v0; V1 = v1; V2 = v2; Branches = branches; ConvertersWithoutFaultInfeed = converters;
        _bus = new Dictionary<int, int>(busIds.Count);
        for (int i = 0; i < busIds.Count; i++) _bus.TryAdd(busIds[i], i);
    }

    public FaultSpec Fault { get; }
    public FaultType Type { get; }
    public double FaultReactancePu { get; }
    /// <summary>The fault network (null when the fault could not be applied).</summary>
    public FaultNetwork? Network { get; }
    public string? NotEvaluated { get; }
    public IReadOnlyList<int> BusIds { get; }
    public Complex[] V0 { get; }
    public Complex[] V1 { get; }
    public Complex[] V2 { get; }
    /// <summary>Per branch of the fault network's model, in its order.</summary>
    public IReadOnlyList<BranchSequenceCurrents> Branches { get; }
    /// <summary>Converter-interfaced units an unbalanced study leaves at their prefault current (N118).</summary>
    public IReadOnlyList<ConverterWithoutFaultInfeed> ConvertersWithoutFaultInfeed { get; }

    /// <summary>RC5 WP18: the sequence currents (0, 1, 2) into the fault at <see cref="FaultNetwork.FaultBusId"/>, pu on the
    /// system base (I0 = I2 = 0 for a three-phase fault; all zero when the fault could not be applied). Read by the busbar
    /// differential for the units and loads at a faulted zone bus.</summary>
    public (Complex I0, Complex I1, Complex I2) FaultSequenceCurrents { get; private set; }

    /// <summary>ANA-02: the prefault positive-sequence voltage of each bus (aligned with <see cref="BusIds"/>, zero where the
    /// prefault solve has no voltage): the memory voltage a distance relay polarises with when its loop voltage collapses.</summary>
    public Complex[] PrefaultV1 { get; private set; } = Array.Empty<Complex>();

    private static Complex[] PrefaultAt(IReadOnlyList<int> busIds, PowerFlowResult pf)
    {
        var pos = new Dictionary<int, int>(pf.BusIds.Count);
        for (int i = 0; i < pf.BusIds.Count; i++) pos.TryAdd(pf.BusIds[i], i);
        var v = new Complex[busIds.Count];
        for (int i = 0; i < busIds.Count; i++)
            if (pos.TryGetValue(busIds[i], out int p)) v[i] = DetMath.FromPolar(pf.Vm[p], pf.VaDeg[p] * Math.PI / 180.0);
        return v;
    }

    /// <summary>The fault type's short name: 3ph, slg, ll or llg.</summary>
    public static string TypeName(FaultType t) => t switch
    {
        FaultType.ThreePhase => "3ph", FaultType.SingleLineToGround => "slg", FaultType.LineToLine => "ll", FaultType.DoubleLineToGround => "llg",
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "unknown fault type"),
    };

    /// <summary>The fault type from its short name (3ph, slg, ll, llg; dlg is accepted for llg).</summary>
    public static FaultType ParseType(string s) => s.ToLowerInvariant() switch
    {
        "3ph" => FaultType.ThreePhase, "slg" => FaultType.SingleLineToGround, "ll" => FaultType.LineToLine, "llg" or "dlg" => FaultType.DoubleLineToGround,
        _ => throw new ArgumentException($"fault type must be 3ph, slg, ll or llg, not '{s}'."),
    };

    public bool TryBus(int busId, out int index) => _bus.TryGetValue(busId, out index);

    /// <summary>The phase voltages (a, b, c) of a bus, pu.</summary>
    public (Complex A, Complex B, Complex C) PhaseVoltages(int busIndex) => FaultAnalysis.Phases(V0[busIndex], V1[busIndex], V2[busIndex]);

    /// <summary>The sequence currents (0, 1, 2) into branch <paramref name="branch"/> at one end, pu.</summary>
    public (Complex I0, Complex I1, Complex I2) Sequence(int branch, bool fromEnd)
    {
        BranchSequenceCurrents b = Branches[branch];
        return fromEnd ? (b.I0From, b.I1From, b.I2From) : (b.I0To, b.I1To, b.I2To);
    }

    /// <summary>RC5 WP17: the zero-sequence current into branch <paramref name="branch"/> at one end, pu (read only).</summary>
    public Complex ZeroSequenceAt(int branch, bool fromEnd) => fromEnd ? Branches[branch].I0From : Branches[branch].I0To;

    /// <summary>The phase currents (a, b, c) into branch <paramref name="branch"/> at one end, pu.</summary>
    public (Complex A, Complex B, Complex C) PhaseCurrents(int branch, bool fromEnd)
    {
        (Complex i0, Complex i1, Complex i2) = Sequence(branch, fromEnd);
        return FaultAnalysis.Phases(i0, i1, i2);
    }

    /// <summary>The default prefault solve: the Newton solver the case selects (<see cref="SolverSelector.CreateNewton"/>).</summary>
    public static PowerFlowResult DefaultPrefault(GridModel m) => SolverSelector.CreateNewton(m).Solve(m);

    /// <summary>
    /// Apply <paramref name="fault"/> of type <paramref name="type"/> through fault reactance <paramref name="faultReactancePu"/>
    /// (Zf = j xf, the static engines' convention). The prefault state is solved on the fault network (the split copy for a
    /// fault along a line) by <paramref name="prefault"/> (default <see cref="DefaultPrefault"/>) and must converge.
    /// </summary>
    public static FaultPhasors Build(GridModel model, FaultSpec fault, FaultType type, double faultReactancePu = 0.0, Func<GridModel, PowerFlowResult>? prefault = null)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(fault);
        if (!(faultReactancePu >= 0.0) || !double.IsFinite(faultReactancePu))
            throw new ArgumentOutOfRangeException(nameof(faultReactancePu), faultReactancePu, "The fault reactance must be finite and >= 0 pu.");
        prefault ??= DefaultPrefault;
        FaultNetwork net;
        try
        {
            if (fault.Bus is int b)
            {
                if (!model.Buses.Any(x => x.Id == b)) throw new ArgumentException($"fault bus {b} is not in the case.", nameof(fault));
                net = new FaultNetwork(model, b, false, -1, -1);
            }
            else net = FaultAlongLine.Build(model, fault.Branch!.Value, fault.Fraction);
        }
        catch (InvalidOperationException ex) { return Empty(fault, type, faultReactancePu, ex.Message); }
        try
        {
            GridModel m = net.Model;
            PowerFlowResult pf = prefault(m).RequireConverged("Placed protection evaluation");
            if (type == FaultType.ThreePhase)
            {
                FaultPhasors three = FromThreePhase(fault, faultReactancePu, net, pf);
                three.PrefaultV1 = PrefaultAt(three.BusIds, pf);
                return three;
            }
            UnbalancedFaultResult u = FaultAnalysis.Unbalanced(m, pf, net.FaultBusId, type, faultReactancePu);
            return new FaultPhasors(fault, type, faultReactancePu, net, null, u.BusIds, u.V0, u.V1, u.V2, u.Branches, u.ConvertersWithoutFaultInfeed)
            {
                FaultSequenceCurrents = (u.I0, u.I1, u.I2),
                PrefaultV1 = PrefaultAt(u.BusIds, pf),
            };
        }
        catch (InvalidOperationException ex) { return Empty(fault, type, faultReactancePu, ex.Message); }
    }

    private static FaultPhasors FromThreePhase(FaultSpec fault, double xf, FaultNetwork net, PowerFlowResult pf)
    {
        GridModel m = net.Model;
        FaultResult r = FaultAnalysis.ThreePhase(m, pf, net.FaultBusId, xf);
        if (!r.ConvertersConverged) throw new InvalidOperationException("the converter current fixed point of the three-phase fault did not converge.");
        PowerFlowResult st = r.State;
        int n = m.BusCount;
        var ids = new int[n];
        var v1 = new Complex[n];
        var pos = new Dictionary<int, int>(st.BusIds.Count);
        for (int i = 0; i < st.BusIds.Count; i++) pos.TryAdd(st.BusIds[i], i);
        var live = new bool[n];
        for (int i = 0; i < n; i++)
        {
            ids[i] = m.Buses[i].Id;
            if (pos.TryGetValue(ids[i], out int p)) { v1[i] = DetMath.FromPolar(st.Vm[p], st.VaDeg[p] * Math.PI / 180.0); live[i] = true; }
        }
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index.TryAdd(ids[i], i);
        var branches = new List<BranchSequenceCurrents>(m.Branches.Count);
        for (int l = 0; l < m.Branches.Count; l++)
        {
            Branch br = m.Branches[l];
            string name = string.IsNullOrEmpty(br.Name) ? $"branch {br.From}-{br.To}" : br.Name!;
            if (!br.InService || !index.TryGetValue(br.From, out int f) || !index.TryGetValue(br.To, out int t) || !live[f] || !live[t])
            {
                branches.Add(new BranchSequenceCurrents(l, name, br.From, br.To, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero));
                continue;
            }
            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
            Complex i1f = s.Yff * v1[f] + s.Yft * v1[t], i1t = s.Ytf * v1[f] + s.Ytt * v1[t];
            branches.Add(new BranchSequenceCurrents(l, name, br.From, br.To, i1f, Complex.Zero, Complex.Zero, i1t, Complex.Zero, Complex.Zero));
        }
        return new FaultPhasors(fault, FaultType.ThreePhase, xf, net, null, ids, new Complex[n], v1, new Complex[n], branches, Array.Empty<ConverterWithoutFaultInfeed>())
        {
            FaultSequenceCurrents = (Complex.Zero, r.FaultCurrentPhasorPu, Complex.Zero),
        };
    }

    private static FaultPhasors Empty(FaultSpec f, FaultType t, double xf, string reason) =>
        new(f, t, xf, null, reason, Array.Empty<int>(), Array.Empty<Complex>(), Array.Empty<Complex>(), Array.Empty<Complex>(),
            Array.Empty<BranchSequenceCurrents>(), Array.Empty<ConverterWithoutFaultInfeed>());
}
