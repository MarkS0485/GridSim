using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Core.Protection.Coordination;

namespace GridSim.Core.Protection.Devices;

/// <summary>A device set resolved against one model and one curve library.</summary>
public sealed class BoundDeviceSet
{
    internal BoundDeviceSet(GridModel model, DeviceSet set, CurveLibrary library) { Model = model; Set = set; Library = library; }
    public GridModel Model { get; }
    public DeviceSet Set { get; }
    public CurveLibrary Library { get; }
    public IReadOnlyList<PlacedDevice> Devices => Set.Devices;
    /// <summary>A device on an out-of-service branch is bound but reported unevaluated, never silently passing.</summary>
    public bool OutOfService(PlacedDevice d) => !Model.Branches[d.Location.BranchIndex].InService;
}

/// <summary>
/// Validate a <see cref="DeviceSet"/> and bind it to a case. Order: unique IDs; branch identity (index in range, From and To
/// as in the case); CT ratio; per type the settings that type needs; every curve ID known and of the right kind. The first
/// failure refuses the whole set, naming the device.
/// </summary>
public static class DeviceSetValidator
{
    public static BoundDeviceSet Bind(DeviceSet set, GridModel model, CurveLibrary library)
    {
        ArgumentNullException.ThrowIfNull(set); ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(library);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (PlacedDevice d in set.Devices)
        {
            string at = $"Device '{d.Id}'";
            if (!ids.Add(d.Id)) throw new DeviceSetException($"Duplicate device ID '{d.Id}'.");
            DeviceLocation l = d.Location;
            if (l.BranchIndex < 0 || l.BranchIndex >= model.Branches.Count)
                throw new DeviceSetException($"{at}: branch index {l.BranchIndex} is outside the case (0..{model.Branches.Count - 1}).");
            Branch br = model.Branches[l.BranchIndex];
            if (br.From != l.From || br.To != l.To)
                throw new DeviceSetException($"{at}: branch {l.BranchIndex} joins {br.From}-{br.To} in the case, not {l.From}-{l.To}.");
            if (!(d.CtPrimaryA > 0.0) || !(d.CtSecondaryA > 0.0) || !double.IsFinite(d.CtPrimaryA) || !double.IsFinite(d.CtSecondaryA))
                throw new DeviceSetException($"{at}: the CT primary and secondary amps must be finite and > 0.");
            switch (d.Type)
            {
                case DeviceType.Relay:
                case DeviceType.Recloser:
                    if (d.Fuse is not null) throw new DeviceSetException($"{at}: a {d.Type.ToString().ToLowerInvariant()} does not take a fuse setting.");
                    if (d.IsEvaluatedFunction || d.Function is "50N" or "51N" or "67" or "21")
                    {
                        if (d.Shots.Count == 0 && d.Instantaneous is null) throw new DeviceSetException($"{at}: needs at least one shot or an instantaneous setting.");
                    }
                    if (d.Type == DeviceType.Relay && d.Shots.Count > 1) throw new DeviceSetException($"{at}: a relay has one shot; use a recloser for a sequence.");
                    if (d.Type == DeviceType.Recloser)
                    {
                        if (d.Shots.Count < 1) throw new DeviceSetException($"{at}: a recloser needs at least one shot.");
                        if (d.RecloseIntervalsSeconds.Count != d.Shots.Count - 1)
                            throw new DeviceSetException($"{at}: {d.Shots.Count} shots need {d.Shots.Count - 1} reclose intervals, not {d.RecloseIntervalsSeconds.Count}.");
                        foreach (double t in d.RecloseIntervalsSeconds)
                            if (!(t >= 0.0) || !double.IsFinite(t)) throw new DeviceSetException($"{at}: reclose intervals must be finite and >= 0 s.");
                    }
                    else if (d.RecloseIntervalsSeconds.Count > 0) throw new DeviceSetException($"{at}: only a recloser has reclose intervals.");
                    foreach (ShotSetting s in d.Shots) CheckShot(at, s, library);
                    if (d.Instantaneous is { } i && (!(i.PickupA > 0.0) || !double.IsFinite(i.PickupA) || !(i.DelayS >= 0.0) || !double.IsFinite(i.DelayS)))
                        throw new DeviceSetException($"{at}: the instantaneous pickup must be > 0 and its delay >= 0.");
                    break;
                case DeviceType.Fuse:
                    if (d.Fuse is null) throw new DeviceSetException($"{at}: a fuse needs a fuse setting (meltCurve, clearCurve, ratingA).");
                    if (d.Shots.Count > 0 || d.Instantaneous is not null) throw new DeviceSetException($"{at}: a fuse takes no relay shots or instantaneous setting.");
                    if (!(d.Fuse.RatingA > 0.0) || !double.IsFinite(d.Fuse.RatingA)) throw new DeviceSetException($"{at}: the fuse rating must be finite and > 0 A.");
                    CheckTab(at, d.Fuse.MeltCurveId, CurveKind.MinimumMelt, library);
                    CheckTab(at, d.Fuse.ClearCurveId, CurveKind.TotalClear, library);
                    break;
                case DeviceType.Breaker:
                    if (d.Rating is null) throw new DeviceSetException($"{at}: a breaker needs a rating.");
                    break;
            }
        }
        return new BoundDeviceSet(model, set, library);
    }

    private static void CheckShot(string at, ShotSetting s, CurveLibrary lib)
    {
        if (!lib.Knows(s.CurveId)) throw new DeviceSetException($"{at}: curve '{s.CurveId}' is neither a built-in family nor in the curve library.");
        if (lib.TryGetTabulated(s.CurveId, out TabulatedCurve t) && t.Kind != CurveKind.Operate)
            throw new DeviceSetException($"{at}: curve '{s.CurveId}' is a {t.Kind} curve, which a relay or recloser shot cannot use.");
        if (!(s.PickupA >= 0.0) || !double.IsFinite(s.PickupA) || (CurveLibrary.Standard(s.CurveId) is not null && !(s.PickupA > 0.0)))
            throw new DeviceSetException($"{at}: the pickup must be finite and > 0 A.");
        // The equation checks themselves are the incumbent's: a bad time dial or delay is refused with its message.
        if (CurveLibrary.Standard(s.CurveId) is OvercurrentCurve c)
        {
            try { OvercurrentCoordination.OperateTimeSeconds(new OvercurrentElement(c, s.PickupA, s.TimeDial, s.DefiniteTimeSeconds), s.PickupA * 2.0); }
            catch (ArgumentOutOfRangeException ex) { throw new DeviceSetException($"{at}: {ex.Message}"); }
        }
        else if (!(s.TimeDial > 0.0) || !double.IsFinite(s.TimeDial)) throw new DeviceSetException($"{at}: the time multiplier must be finite and > 0.");
    }

    private static void CheckTab(string at, string id, CurveKind kind, CurveLibrary lib)
    {
        if (!lib.TryGetTabulated(id, out TabulatedCurve t)) throw new DeviceSetException($"{at}: curve '{id}' is not in the curve library.");
        if (t.Kind != kind) throw new DeviceSetException($"{at}: curve '{id}' is a {t.Kind} curve, expected {kind}.");
    }
}
