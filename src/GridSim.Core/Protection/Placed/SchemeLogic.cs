using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Protection.Devices;

namespace GridSim.Core.Protection.Placed;

/// <summary>What one end of a scheme sees at one fault: its zone-1 operating time (+infinity when zone 1 does not pick
/// up), whether its overreaching element (zone 1 or zone 2) picks up, whether its reverse zone picks up, and its time
/// without the scheme (the fastest zone timer, as the evaluation reports it).</summary>
public sealed record SchemeEnd(double Z1TimeS, bool OverreachPickup, bool ReversePickup, double PlainTimeS)
{
    /// <summary>RC5 (2.1.122): whether a loop of the faulted phases picks up in the overreaching element (null: take
    /// <see cref="OverreachPickup"/>). The DCB carrier stop and the POTT permissive (2.1.126) read it.</summary>
    public bool? FaultLoopOverreachPickup { get; init; }
}

/// <summary>The aided result of one scheme end at one fault. <see cref="AidedTripS"/> is min(plain time, scheme time);
/// <see cref="Aided"/> is true when the scheme time is the faster. A row that could not be evaluated carries the reason
/// and +infinity times.</summary>
public sealed record SchemeRow(string DeviceId, SchemeType Type, string RemoteId, bool Evaluated, string? Reason, double PlainTimeS, double AidedTripS, bool Aided);

/// <summary>
/// RC5 WP17: communication-aided distance schemes on the static fault phasors (Horowitz and Phadke, Power System Relaying,
/// 4th ed., ch. 6; the SEL and GE application notes on communication-assisted schemes give the same definitions). With
/// t_Z1 the zone-1 time at an end (+infinity when it does not pick up), p_Z2 the pickup time of its overreaching element
/// (0 when it picks up, since a static fault has no element pickup delay, +infinity when not), and the remote end's
/// quantities marked r:
/// <list type="bullet">
/// <item>DUTT: scheme time t_Z1,r + channel.</item>
/// <item>PUTT: max(p_Z2, t_Z1,r + channel).</item>
/// <item>POTT: max(p_Z2, p_Z2,r + channel), with p_Z2,r from the remote end's faulted-phase loops (see DCB below). No
/// current-reversal guard, echo or weak-infeed logic.</item>
/// <item>DCB: p_Z2 + coordination unless a block is received, else +infinity. The remote end sends the block when its
/// reverse zone picks up and its overreaching element (zone 1 or zone 2) does not: the forward pickup stops the carrier
/// (the textbook DCB of Ziegler, Numerical Distance Protection, and the Alstom Network Protection and Automation Guide). So a reverse zone that also covers a close-in internal
/// fault, as an offset or quadrilateral reverse zone can, does not block the trip. The forward pickup that stops the
/// carrier is that of a loop of the faulted phases (<see cref="PlacedElementEvaluator.IsFaultLoop"/>), as a relay's phase
/// selection gives it: under heavy prefault load a healthy-phase loop can sit inside the forward zone 2 during an external
/// fault, and it must not stop the block.</item>
/// </list>
/// The end trips at min(plain time, scheme time), so the zone timers still apply as backup. The channel never fails.
/// </summary>
public static class SchemeLogic
{
    /// <summary>The scheme's name as the device file writes it.</summary>
    public static string Name(SchemeType type) => type switch
    {
        SchemeType.Dutt => "DUTT", SchemeType.Putt => "PUTT", SchemeType.Pott => "POTT", SchemeType.Dcb => "DCB",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown scheme type"),
    };

    /// <summary>The scheme time of the local end (+infinity when the scheme gives no trip).</summary>
    public static double SchemeTimeS(SchemeType type, double channelS, double coordinationS, SchemeEnd local, SchemeEnd remote)
    {
        ArgumentNullException.ThrowIfNull(local); ArgumentNullException.ThrowIfNull(remote);
        double pZ2 = local.OverreachPickup ? 0.0 : double.PositiveInfinity;
        // The permissive (POTT) is keyed by the remote end's faulted-phase loops, as the DCB carrier stop is: a healthy-phase
        // loop inside zone 2 under heavy load must not send it on an external fault.
        double pZ2r = (remote.FaultLoopOverreachPickup ?? remote.OverreachPickup) ? 0.0 : double.PositiveInfinity;
        return type switch
        {
            SchemeType.Dutt => remote.Z1TimeS + channelS,
            SchemeType.Putt => Math.Max(pZ2, remote.Z1TimeS + channelS),
            SchemeType.Pott => Math.Max(pZ2, pZ2r + channelS),
            SchemeType.Dcb => remote.ReversePickup && !(remote.FaultLoopOverreachPickup ?? remote.OverreachPickup) ? double.PositiveInfinity : pZ2 + coordinationS,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown scheme type"),
        };
    }

    /// <summary>The local end's trip time: min(t_Z1, plain time, scheme time).</summary>
    public static double TripS(SchemeType type, double channelS, double coordinationS, SchemeEnd local, SchemeEnd remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        return Math.Min(Math.Min(local.Z1TimeS, local.PlainTimeS), SchemeTimeS(type, channelS, coordinationS, local, remote));
    }

    /// <summary>The scheme view of a distance row: zone 1 is the first zone, the overreaching element is zone 1 or zone 2
    /// among the forward zones, and the reverse pickup is any reverse zone's. The faulted-phase overreach pickup comes from
    /// <see cref="PlacedRow.FaultLoopZonesPicked"/>.</summary>
    public static SchemeEnd EndOf(PlacedRow row, DistanceSetting setting)
    {
        ArgumentNullException.ThrowIfNull(row); ArgumentNullException.ThrowIfNull(setting);
        int picked = row.ZonesPicked;
        int forward = setting.Zones.Count(z => !z.Reverse);
        double z1 = (picked & 1) != 0 ? setting.Zones[0].TimeS : double.PositiveInfinity;
        bool over = false, rev = false, faultOver = false;
        for (int z = 0; z < setting.Zones.Count; z++)
        {
            if ((picked & (1 << z)) == 0) continue;
            if (setting.Zones[z].Reverse) rev = true;
            else if (z < Math.Min(2, forward))
            {
                over = true;
                if ((row.FaultLoopZonesPicked & (1 << z)) != 0) faultOver = true;
            }
        }
        return new SchemeEnd(z1, over, rev, row.Operates ? row.TimeS : double.PositiveInfinity) { FaultLoopOverreachPickup = faultOver };
    }

    /// <summary>Every device's scheme row at one fault, in device order: null for a device with no scheme.
    /// <paramref name="rows"/> is <see cref="PlacedElementEvaluator.Evaluate"/>'s result for the same device set.</summary>
    public static IReadOnlyList<SchemeRow?> Apply(BoundDeviceSet bound, IReadOnlyList<PlacedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(bound); ArgumentNullException.ThrowIfNull(rows);
        IReadOnlyList<PlacedDevice> devs = bound.Devices;
        if (rows.Count != devs.Count) throw new ArgumentException("one row per device is needed", nameof(rows));
        var result = new SchemeRow?[devs.Count];
        for (int i = 0; i < devs.Count; i++)
        {
            PlacedDevice d = devs[i];
            if (d.Distance?.Scheme is not { } sc) continue;
            int ri = -1;
            for (int k = 0; k < devs.Count; k++) if (devs[k].Id == sc.RemoteId) { ri = k; break; }
            PlacedRow local = rows[i], remote = rows[ri];
            double plain = local.Operates ? local.TimeS : double.PositiveInfinity;
            if (!local.Evaluated || !remote.Evaluated)
            {
                string why = !local.Evaluated ? "this end is not evaluated: " + local.Reason : $"the remote end '{sc.RemoteId}' is not evaluated: " + remote.Reason;
                result[i] = new SchemeRow(d.Id, sc.Type, sc.RemoteId, false, why, plain, double.PositiveInfinity, false);
                continue;
            }
            SchemeEnd le = EndOf(local, d.Distance), re = EndOf(remote, devs[ri].Distance!);
            double term = SchemeTimeS(sc.Type, sc.ChannelS, sc.CoordinationS, le, re);
            double trip = Math.Min(Math.Min(le.Z1TimeS, le.PlainTimeS), term);
            result[i] = new SchemeRow(d.Id, sc.Type, sc.RemoteId, true, null, plain, trip, term < Math.Min(le.Z1TimeS, le.PlainTimeS));
        }
        return result;
    }
}
