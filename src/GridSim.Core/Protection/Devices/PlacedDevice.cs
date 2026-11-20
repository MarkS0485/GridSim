using System.Collections.Generic;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Core.Protection.Devices;

/// <summary>The kind of placed device.</summary>
public enum DeviceType { Relay, Fuse, Recloser, Breaker }

/// <summary>Where a device sits: the branch by stable identity (index plus From and To bus IDs, the RC3 rule that keeps
/// parallel circuits distinct) and the end it measures at (<c>from</c> or <c>to</c>).</summary>
public sealed record DeviceLocation(int BranchIndex, int From, int To, string End)
{
    public bool AtFromEnd => End == "from";
    /// <summary>The bus the device is connected at.</summary>
    public int NearBus => AtFromEnd ? From : To;
    /// <summary>The bus at the other end of the protected branch.</summary>
    public int FarBus => AtFromEnd ? To : From;
}

/// <summary>One time-current setting. <see cref="CurveId"/> is a built-in family id or a tabulated curve id.
/// <see cref="PickupA"/> is the pickup in CT secondary amps for an equation curve, and the current in the device's own
/// units (CT ratio applied) is compared with it; for a tabulated curve the curve itself is in primary amps and
/// <see cref="PickupA"/> (secondary, may be 0 = none) only gates operation. <see cref="TimeDial"/> is TMS or TD, or a time
/// multiplier on a tabulated operate curve; <see cref="DefiniteTimeSeconds"/> is the delay of a <c>dt</c> curve.</summary>
public sealed record ShotSetting(string CurveId, double PickupA, double TimeDial, double DefiniteTimeSeconds);

/// <summary>An instantaneous element (ANSI 50): operates in <see cref="DelayS"/> at or above <see cref="PickupA"/> (CT secondary amps).</summary>
public sealed record InstantaneousSetting(double PickupA, double DelayS);

/// <summary>A fuse: its minimum-melt and total-clear tabulated curves (primary amps) and its continuous rating.</summary>
public sealed record FuseSetting(string MeltCurveId, string ClearCurveId, double RatingA);

/// <summary>
/// One placed protection device (<c>gridsim-devices/1</c>). A relay has one shot; a recloser has one shot per operation
/// before lockout (<see cref="Shots"/>.Count = <see cref="RecloseIntervalsSeconds"/>.Count + 1); a fuse has a
/// <see cref="Fuse"/> setting; a breaker carries a <see cref="Rating"/> for the <c>CheckBreaker</c> margin. Any device may
/// also carry a rating. <see cref="Function"/> is the ANSI device number: 50 and 51 (phase overcurrent) are evaluated;
/// 50N, 51N, 67 and 21 are accepted and listed as not evaluated (the audit says why).
/// </summary>
public sealed record PlacedDevice(
    string Id,
    DeviceType Type,
    string Function,
    DeviceLocation Location,
    double CtPrimaryA,
    double CtSecondaryA,
    double VtPrimaryV,
    double VtSecondaryV,
    IReadOnlyList<ShotSetting> Shots,
    InstantaneousSetting? Instantaneous,
    IReadOnlyList<double> RecloseIntervalsSeconds,
    FuseSetting? Fuse,
    Iec60909.BreakerRating? Rating)
{
    public double CtRatio => CtPrimaryA / CtSecondaryA;
    public bool IsEvaluatedFunction => Function is "50" or "51" or "";
}
