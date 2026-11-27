using System;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.Devices;

namespace GridSim.Core.Protection.Placed;

/// <summary>
/// RC5 WP17: load encroachment. A phase loop whose apparent impedance has |Z| &gt;= the minimum load impedance and lies
/// within the forward angle of 0 degrees or within the reverse angle of 180 degrees is in the load region and is blocked
/// (G. Ziegler, Numerical Distance Protection, 4th ed., ch. 3, load encroachment). Earth loops are not affected.
/// </summary>
public static class LoadBlinder
{
    /// <summary>Whether <paramref name="z"/> (secondary ohms) lies in the load region of <paramref name="setting"/>.</summary>
    public static bool Blocks(Complex z, LoadEncroachmentSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (!(z.Magnitude >= setting.MinLoadOhms)) return false;
        double arg = DetMath.Phase(z) * 180.0 / Math.PI;   // (-180, 180]
        return Math.Abs(arg) <= setting.FwdAngleDeg || 180.0 - Math.Abs(arg) <= setting.RevAngleDeg;
    }

    /// <summary>
    /// The apparent impedance a device's phase loops see at the prefault load point, secondary ohms: V / I at its end
    /// from a solved power flow (positive sequence; a balanced state gives the same value on every phase loop), the
    /// current from the branch's pi stamp. Null when the bus is not in the solution, has no base kV, or the branch carries
    /// no current above <see cref="ProtectionContext.MinApparentPowerPu"/>.
    /// </summary>
    public static Complex? PrefaultImpedance(GridModel model, PowerFlowResult pf, PlacedDevice d)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(pf); ArgumentNullException.ThrowIfNull(d);
        Branch br = model.Branches[d.Location.BranchIndex];
        int fi = -1, ti = -1;
        for (int b = 0; b < pf.BusIds.Count; b++)
        {
            if (pf.BusIds[b] == br.From) fi = b;
            if (pf.BusIds[b] == br.To) ti = b;
        }
        if (fi < 0 || ti < 0) return null;
        double kv = 0.0;
        foreach (Bus bus in model.Buses) if (bus.Id == d.Location.NearBus) { kv = bus.BaseKv; break; }
        if (!(kv > 0.0)) return null;
        Complex vf = DetMath.FromPolar(pf.Vm[fi], pf.VaDeg[fi] * Math.PI / 180.0), vt = DetMath.FromPolar(pf.Vm[ti], pf.VaDeg[ti] * Math.PI / 180.0);
        BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
        Complex i = d.Location.AtFromEnd ? s.Yff * vf + s.Yft * vt : s.Ytf * vf + s.Ytt * vt;
        Complex v = d.Location.AtFromEnd ? vf : vt;
        if (!(i.Magnitude > ProtectionContext.MinApparentPowerPu)) return null;
        return v / i * PlacedElementEvaluator.OhmsPerPu(model.BaseMva, kv, d);
    }
}
