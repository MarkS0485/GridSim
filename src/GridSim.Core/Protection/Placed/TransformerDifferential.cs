using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.Workflow;

namespace GridSim.Core.Protection.Placed;

/// <summary>RC5 WP18: the compensation matrix a numerical transformer differential applies to one side's phase currents.
/// <see cref="Identity"/> leaves them as they are; <see cref="ZeroSequenceRemoval"/> is I - J/3 (J the all-ones matrix);
/// <see cref="Plus30"/> is (1/sqrt 3) [[1, -1, 0], [0, 1, -1], [-1, 0, 1]], which turns a positive-sequence set by +30
/// degrees and a negative-sequence set by -30 degrees; <see cref="Minus30"/> is (1/sqrt 3) [[1, 0, -1], [-1, 1, 0], [0, -1, 1]],
/// which turns them by -30 and +30 degrees. Both rotation matrices remove the zero sequence (each row sums to 0).</summary>
public enum Compensation { Identity, ZeroSequenceRemoval, Plus30, Minus30 }

/// <summary>RC5 WP18: one 87T evaluation, per phase (a, b, c) and in pu of the transformer rating: the differential, bias
/// and threshold currents, and the compensated current of each side.</summary>
public sealed record TransformerDifferentialResult(
    double[] IdPu, double[] IbPu, double[] ThresholdPu, Complex[] HvPu, Complex[] LvPu, int DecidingPhase, bool Operates);

/// <summary>
/// RC5 WP18: the biased two-winding transformer differential (ANSI 87T) on fundamental-frequency phasors.
/// <para>Per side: I_pu = I_A / I_rated,side, I_rated,side = S / (sqrt 3 U_side) with U_side the CT bus's base kV; then the
/// side's compensation matrix (<see cref="Compensation"/>). Id = |(M_H I_H) + (M_L I_L)| and Ib = (|M_H I_H| + |M_L I_L|) / 2
/// per phase, both currents into the zone; operate per phase through <see cref="PlacedElementEvaluator.DifferentialThreshold"/>.</para>
/// <para>Compensation is on the star side, the delta side being the reference (the SEL practice: an SEL-387A Dyn1 example
/// sets CTC(1) on the wye winding, "which also compensates for zero sequence currents"). The clock number k says the LV
/// side lags the HV side by k x 30 degrees, so a star LV winding is turned forward by k x 30 degrees (Dyn1: Plus30,
/// Dyn11: Minus30) and a star HV winding back by k x 30 degrees (YNd1: Minus30, Yd11: Plus30). Clock 0: an earthed star
/// (YN) side gets the zero-sequence removal I - J/3, any other side the identity. The matrices are the standard
/// numerical-relay ones (SEL CTC(1) and CTC(11)); their rotation and zero-sequence identities are tested.</para>
/// <para>Fundamental phasors only: no inrush, no 2nd or 5th harmonic restraint, no CT saturation. A tap away from 1 at the
/// solved point shows as a differential current, as it does in a relay set at the nominal ratio.</para>
/// </summary>
public static class TransformerDifferential
{
    private static readonly double InvSqrt3 = 1.0 / Math.Sqrt(3.0);

    /// <summary>The supported vector groups: HV winding, LV winding, clock number.</summary>
    private static readonly (string Group, string Hv, string Lv, int Clock)[] Groups =
    [
        ("YNd1", "YN", "d", 1), ("Yd11", "Y", "d", 11), ("Dyn1", "D", "yn", 1), ("Dyn11", "D", "yn", 11),
        ("YNyn0", "YN", "yn", 0), ("Yy0", "Y", "y", 0), ("Dd0", "D", "d", 0),
    ];

    /// <summary>The supported vector groups, in order, for messages.</summary>
    public static string SupportedGroups => "YNd1, Yd11, Dyn1, Dyn11, YNyn0, Yy0 and Dd0";

    /// <summary>The (HV, LV) compensation matrices of a supported vector group; false for any other group.</summary>
    public static bool TryMatrices(string vectorGroup, out Compensation hv, out Compensation lv)
    {
        foreach ((string g, string h, string l, int k) in Groups)
        {
            if (!string.Equals(g, vectorGroup, StringComparison.Ordinal)) continue;
            bool hvStar = h != "D", lvStar = l != "d";
            hv = Compensation.Identity; lv = Compensation.Identity;
            if (k == 0)
            {
                if (h == "YN") hv = Compensation.ZeroSequenceRemoval;
                if (l == "yn") lv = Compensation.ZeroSequenceRemoval;
            }
            else if (hvStar) hv = k == 1 ? Compensation.Minus30 : Compensation.Plus30;
            else if (lvStar) lv = k == 1 ? Compensation.Plus30 : Compensation.Minus30;
            return true;
        }
        hv = lv = Compensation.Identity;
        return false;
    }

    /// <summary>The clock number of a supported vector group (-1 for any other).</summary>
    public static int Clock(string vectorGroup)
    {
        foreach ((string g, _, _, int k) in Groups) if (string.Equals(g, vectorGroup, StringComparison.Ordinal)) return k;
        return -1;
    }

    private static WindingConnection Winding(string w) => w switch
    {
        "YN" or "yn" => WindingConnection.WyeGrounded,
        "Y" or "y" => WindingConnection.WyeUngrounded,
        _ => WindingConnection.Delta,
    };

    /// <summary>Apply a compensation matrix to the phase vector (a, b, c).</summary>
    public static (Complex A, Complex B, Complex C) Apply(Compensation m, Complex a, Complex b, Complex c) => m switch
    {
        Compensation.Identity => (a, b, c),
        Compensation.ZeroSequenceRemoval => ZeroRemoved(a, b, c),
        Compensation.Plus30 => ((a - b) * InvSqrt3, (b - c) * InvSqrt3, (c - a) * InvSqrt3),
        Compensation.Minus30 => ((a - c) * InvSqrt3, (b - a) * InvSqrt3, (c - b) * InvSqrt3),
        _ => throw new ArgumentOutOfRangeException(nameof(m), m, "unknown compensation"),
    };

    private static (Complex, Complex, Complex) ZeroRemoved(Complex a, Complex b, Complex c)
    {
        Complex i0 = (a + b + c) / 3.0;
        return (a - i0, b - i0, c - i0);
    }

    /// <summary>True when the HV winding is at the branch's From end (the end with the higher base kV).</summary>
    public static bool HvAtFrom(GridModel model, Branch br)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(br);
        return Kv(model, br.From) > Kv(model, br.To);
    }

    private static double Kv(GridModel model, int bus)
    {
        foreach (Bus b in model.Buses) if (b.Id == bus) return b.BaseKv;
        return 0.0;
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The bind-time checks of an 87T (each refusal names the device and the reason).</summary>
    internal static void Check(string at, PlacedDevice d, TransformerDifferentialSetting s, GridModel model)
    {
        static bool Fin(double v) => double.IsFinite(v);
        int idx = d.Location.BranchIndex;
        Branch br = model.Branches[idx];
        if (!br.IsTransformer) throw new DeviceSetException($"{at}: transformerDifferential needs branch {idx} to be a transformer.");
        List<int> stars = Iec60909.ThreeWindingStarPoints(model);
        // ThreeWindingStarPoints returns bus IDs, not list positions (ANA-01): compare the IDs directly.
        foreach (int starId in stars)
            if (starId == br.From || starId == br.To)
                throw new DeviceSetException($"{at}: branch {idx} is a leg of a three-winding unit (star bus {starId}); the 87T covers two-winding units only.");
        double kvF = Kv(model, br.From), kvT = Kv(model, br.To);
        if (!(kvF > 0.0) || !(kvT > 0.0)) throw new DeviceSetException($"{at}: both buses of transformer {idx} need a base kV.");
        if (kvF == kvT) throw new DeviceSetException($"{at}: both buses of transformer {idx} have base kV {R(kvF)}, so the HV side cannot be told.");
        if (!TryMatrices(s.VectorGroup, out _, out _))
            throw new DeviceSetException($"{at}: vector group \"{s.VectorGroup}\" is not supported; use {SupportedGroups}.");
        bool hvFrom = kvF > kvT;
        string hvW = "", lvW = "";
        foreach ((string g, string h, string l, _) in Groups) if (g == s.VectorGroup) { hvW = h; lvW = l; }
        if (br.FromWinding is { } wf && br.ToWinding is { } wt)
        {
            WindingConnection hvCase = hvFrom ? wf : wt, lvCase = hvFrom ? wt : wf;
            if (hvCase != Winding(hvW) || lvCase != Winding(lvW))
                throw new DeviceSetException($"{at}: vector group {s.VectorGroup} does not match the case's windings of transformer {idx} (HV {hvCase}, LV {lvCase}).");
        }
        // Without both windings the fault study passes zero sequence straight through the unit (YNyn behaviour), so any
        // other group's compensation would see that I0 as differential current on an external earth fault.
        else if (s.VectorGroup != "YNyn0")
            throw new DeviceSetException($"{at}: the case gives no winding data for transformer {idx}, so the fault study treats it as YNyn; the 87T needs the windings to match {s.VectorGroup}.");
        // The case's phase shift (the To bus lags the From bus by PhaseShiftDeg) must be the group's: LV lags HV by k x 30.
        double expected = (hvFrom ? 30.0 : -30.0) * Clock(s.VectorGroup);
        double diff = (br.PhaseShiftDeg - expected) % 360.0;
        if (diff != 0.0)
            throw new DeviceSetException($"{at}: transformer {idx} has a phase shift of {R(br.PhaseShiftDeg)} degrees in the case, but vector group {s.VectorGroup} needs {R(expected)} degrees (modulo 360) with the HV side at the {(hvFrom ? "from" : "to")} end.");
        if (!(s.RatedMva > 0.0) || !Fin(s.RatedMva)) throw new DeviceSetException($"{at}: ratedMva must be finite and > 0.");
        if (!(s.Is1 > 0.0) || !Fin(s.Is1) || !(s.Is2 > 0.0) || !Fin(s.Is2)) throw new DeviceSetException($"{at}: is1 and is2 must be finite and > 0 pu.");
        if (!(s.K1 >= 0.0) || !Fin(s.K1) || !Fin(s.K2)) throw new DeviceSetException($"{at}: the slopes must be finite and k1 >= 0.");
        if (s.K2 < s.K1) throw new DeviceSetException($"{at}: the second slope k2 must not be below the first slope k1.");
        CheckCt(at, "hvCt", s.HvCt, hvFrom ? br.From : br.To, idx, model);
        CheckCt(at, "lvCt", s.LvCt, hvFrom ? br.To : br.From, idx, model);
    }

    private static void CheckCt(string at, string name, CtLocation? c, int terminal, int transformer, GridModel model)
    {
        if (c is null) return;
        if (c.BranchIndex < 0 || c.BranchIndex >= model.Branches.Count)
            throw new DeviceSetException($"{at}: {name} branch {c.BranchIndex} is outside the case (0..{model.Branches.Count - 1}).");
        Branch cb = model.Branches[c.BranchIndex];
        if (cb.From != c.From || cb.To != c.To)
            throw new DeviceSetException($"{at}: {name} branch {c.BranchIndex} joins {cb.From}-{cb.To} in the case, not {c.From}-{c.To}.");
        if (c.BranchIndex == transformer) throw new DeviceSetException($"{at}: {name} names the transformer itself; leave {name} out to use its own end.");
        int other = cb.From == terminal ? cb.To : cb.To == terminal ? cb.From : int.MinValue;
        if (other == int.MinValue || c.Bus != other)
            throw new DeviceSetException($"{at}: {name} must sit on a branch from the transformer's bus {terminal}, at that branch's other end.");
        for (int l = 0; l < model.Branches.Count; l++)
        {
            Branch b = model.Branches[l];
            if (l == transformer || l == c.BranchIndex || !b.InService) continue;
            if (b.From == terminal || b.To == terminal)
                throw new DeviceSetException($"{at}: with {name} on branch {c.BranchIndex}, bus {terminal} is inside the zone, but branch {l} ({b.From}-{b.To}) also leaves it without a CT.");
        }
        // A load, shunt or unit at the inside bus draws current that no CT measures, so it would read as differential current.
        foreach (Bus bus in model.Buses)
            if (bus.Id == terminal && BusDifferential.HasLocalElements(model, bus))
                throw new DeviceSetException($"{at}: with {name} on branch {c.BranchIndex}, bus {terminal} is inside the zone, but it has a load, shunt or unit whose current no CT measures.");
        // RC5 (2.1.118): so does an HVDC converter, a static var compensator or a FACTS device there, which the prefault
        // power flow injects at the bus (PowerFlow.HvdcCoupling).
        if (ConverterOrDeviceAt(model, terminal) is { } what)
            throw new DeviceSetException($"{at}: with {name} on branch {c.BranchIndex}, bus {terminal} is inside the zone, but it has {what} whose current no CT measures.");
    }

    /// <summary>The first HVDC converter (LCC, VSC or multi-terminal), static var compensator or FACTS device that injects
    /// at <paramref name="bus"/>, described for a message; null when there is none. A FACTS device injects at its sending
    /// end bus only when it has a shunt element (ShuntMaxMva above 0, as the power flow's coupling reads it), and a UPFC's
    /// series element also at its terminal end bus (a bypassed or constant-impedance series element is an ordinary branch
    /// of the model, which the CT check above already covers). A FACTS device with no shunt element and no UPFC series
    /// element injects nothing, so it does not stop the zone.</summary>
    internal static string? ConverterOrDeviceAt(GridModel model, int bus)
    {
        foreach (LccDcLink l in model.LccLinks)
            if (l.Rectifier.Bus == bus || l.Inverter.Bus == bus) return $"a converter of LCC HVDC link {l.Name}";
        foreach (VscDcLink l in model.VscLinks)
            if (l.First.Bus == bus || l.Second.Bus == bus) return $"a converter of VSC HVDC link {l.Name}";
        foreach (MtdcLink l in model.MtdcLinks)
            foreach (MtdcConverter c in l.Converters)
                if (c.Bus == bus) return $"a converter of multi-terminal DC link {l.Name}";
        foreach (StaticVarCompensator s in model.StaticVarCompensators)
            if (s.Bus == bus) return $"static var compensator {s.Name}";
        foreach (FactsDevice f in model.FactsDevices)
            if ((f.ShuntMaxMva > 0 && f.SendingBus == bus) || (f.Mode == FactsMode.Upfc && f.TerminalBus == bus)) return $"FACTS device {f.Name}";
        return null;
    }

    /// <summary>The bus IDs inside the 87T zone: a transformer terminal whose CT sits on another branch.</summary>
    public static IReadOnlyList<int> InsideBuses(GridModel model, PlacedDevice d)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(d);
        TransformerDifferentialSetting s = d.TransformerDifferential ?? throw new ArgumentException("the device has no transformerDifferential", nameof(d));
        Branch br = model.Branches[d.Location.BranchIndex];
        bool hvFrom = HvAtFrom(model, br);
        var inside = new List<int>(2);
        if (s.HvCt is not null) inside.Add(hvFrom ? br.From : br.To);
        if (s.LvCt is not null) inside.Add(hvFrom ? br.To : br.From);
        return inside;
    }

    /// <summary>Whether a fault lies inside the 87T zone: at an inside bus, or along a branch that carries one of its CTs.</summary>
    public static bool IsInternal(GridModel model, PlacedDevice d, FaultSpec fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (BusDifferential.FaultBus(model, fault) is int b) return InsideBuses(model, d).Contains(b);
        TransformerDifferentialSetting s = d.TransformerDifferential!;
        int br = fault.Branch!.Value;
        return (s.HvCt?.BranchIndex == br || s.LvCt?.BranchIndex == br) && fault.Fraction > 0.0 && fault.Fraction < 1.0;
    }

    /// <summary>The phase currents into the zone at one CT, in pu of the rating, and false when its bus is not usable.</summary>
    private static bool SideCurrents(FaultPhasors f, FaultNetwork net, int branch, bool fromEnd, double ratedMva, out Complex a, out Complex b, out Complex c, out string? why)
    {
        int seg = fromEnd ? FaultNetwork.FromSegment(branch) : net.ToSegment(branch);
        Branch sb = net.Model.Branches[seg];
        int bus = fromEnd ? sb.From : sb.To;
        a = b = c = Complex.Zero; why = null;
        if (!f.TryBus(bus, out int bi)) { why = $"bus {bus} is not in the fault study"; return false; }
        double kv = net.Model.Buses[bi].BaseKv;
        if (!(kv > 0.0)) { why = $"bus {bus} has no base kV"; return false; }
        double ibaseA = PlacedElementEvaluator.IBaseA(net.Model.BaseMva, kv);
        double iratedA = ratedMva / (Math.Sqrt(3.0) * kv) * 1000.0;
        (Complex pa, Complex pb, Complex pc) = f.PhaseCurrents(seg, fromEnd);
        a = pa * ibaseA / iratedA; b = pb * ibaseA / iratedA; c = pc * ibaseA / iratedA;
        return true;
    }

    /// <summary>Evaluate one 87T at one fault. Null (with the reason) when it cannot be evaluated.</summary>
    public static TransformerDifferentialResult? Evaluate(FaultPhasors fault, PlacedDevice d, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(fault); ArgumentNullException.ThrowIfNull(d);
        TransformerDifferentialSetting s = d.TransformerDifferential ?? throw new ArgumentException("the device has no transformerDifferential", nameof(d));
        reason = fault.NotEvaluated;
        if (reason is not null) return null;
        FaultNetwork net = fault.Network!;
        int idx = d.Location.BranchIndex;
        Branch br = net.Model.Branches[idx];
        bool hvFrom = HvAtFrom(net.Model, br);
        TryMatrices(s.VectorGroup, out Compensation mh, out Compensation ml);
        (int Branch, bool FromEnd) hvAt = s.HvCt is { } hc ? (hc.BranchIndex, hc.AtFromEnd) : (idx, hvFrom);
        (int Branch, bool FromEnd) lvAt = s.LvCt is { } lc ? (lc.BranchIndex, lc.AtFromEnd) : (idx, !hvFrom);
        if (!SideCurrents(fault, net, hvAt.Branch, hvAt.FromEnd, s.RatedMva, out Complex ha, out Complex hb, out Complex hcc, out reason)) return null;
        if (!SideCurrents(fault, net, lvAt.Branch, lvAt.FromEnd, s.RatedMva, out Complex la, out Complex lb, out Complex lcc, out reason)) return null;
        // A CT on another branch measures into that branch, which is into the zone (the zone lies on the branch side).
        (Complex A, Complex B, Complex C) h = Apply(mh, ha, hb, hcc), l = Apply(ml, la, lb, lcc);
        Complex[] hv = [h.A, h.B, h.C], lv = [l.A, l.B, l.C];
        var setting = new DifferentialSetting(s.Is1, s.K1, s.Is2, s.K2, 1.0, 1.0);
        var id = new double[3]; var ib = new double[3]; var thr = new double[3];
        int best = 0; double bestExcess = double.NegativeInfinity;
        for (int p = 0; p < 3; p++)
        {
            (id[p], ib[p], thr[p], _) = PlacedElementEvaluator.DifferentialPhase(hv[p], lv[p], setting);
            double excess = id[p] - thr[p];
            if (excess > bestExcess) { bestExcess = excess; best = p; }
        }
        return new TransformerDifferentialResult(id, ib, thr, hv, lv, best, id[best] > thr[best]);
    }
}
