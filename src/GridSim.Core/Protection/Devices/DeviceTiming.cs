using System;
using GridSim.Core.Protection.Coordination;

namespace GridSim.Core.Protection.Devices;

/// <summary>Which time of a device a check needs: the <see cref="Clearing"/> time when it is the primary (a fuse's total
/// clear), the <see cref="Earliest"/> time when it is the backup (a fuse's minimum melt). Relays and reclosers have one
/// characteristic, so both are the same.</summary>
public enum TimingRole { Clearing, Earliest }

/// <summary>The operating time of a placed device at a primary fault current, through its CT ratio.</summary>
public static class DeviceTiming
{
    /// <summary>The time (s) of shot <paramref name="shot"/> of <paramref name="d"/> at <paramref name="primaryCurrentA"/>;
    /// +infinity when the device does not operate. <paramref name="aboveCurveRange"/> reports a tabulated curve held at its
    /// last point.</summary>
    public static double Seconds(PlacedDevice d, int shot, TimingRole role, double primaryCurrentA, CurveLibrary lib, out bool aboveCurveRange)
    {
        ArgumentNullException.ThrowIfNull(d); ArgumentNullException.ThrowIfNull(lib);
        aboveCurveRange = false;
        double secondary = primaryCurrentA / d.CtRatio;
        if (d.Type == DeviceType.Fuse)
        {
            string id = role == TimingRole.Clearing ? d.Fuse!.ClearCurveId : d.Fuse!.MeltCurveId;
            return lib.TryGetTabulated(id, out TabulatedCurve fc) ? fc.TimeAt(primaryCurrentA, out aboveCurveRange) : double.PositiveInfinity;
        }
        double t = double.PositiveInfinity;
        if (d.Shots.Count > 0)
        {
            ShotSetting s = d.Shots[Math.Min(Math.Max(shot, 0), d.Shots.Count - 1)];
            if (CurveLibrary.Standard(s.CurveId) is OvercurrentCurve c)
                t = OvercurrentCoordination.OperateTimeSeconds(new OvercurrentElement(c, s.PickupA, s.TimeDial, s.DefiniteTimeSeconds), secondary);
            else if (lib.TryGetTabulated(s.CurveId, out TabulatedCurve tc) && !(s.PickupA > 0.0 && secondary <= s.PickupA))
            {
                double raw = tc.TimeAt(primaryCurrentA, out aboveCurveRange);
                t = double.IsInfinity(raw) ? raw : raw * s.TimeDial;
            }
        }
        if (d.Instantaneous is { } inst && secondary >= inst.PickupA) t = Math.Min(t, inst.DelayS);
        return t;
    }
}
