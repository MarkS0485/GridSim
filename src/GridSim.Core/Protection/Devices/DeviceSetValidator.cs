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
            if (d.Type is DeviceType.Fuse or DeviceType.Breaker && (d.Distance is not null || d.Directional is not null || d.Differential is not null
                || d.TransformerDifferential is not null || d.BusDifferential is not null))
                throw new DeviceSetException($"{at}: a {d.Type.ToString().ToLowerInvariant()} takes no distance, directional or differential setting.");
            switch (d.Type)
            {
                case DeviceType.Relay:
                case DeviceType.Recloser:
                    if (d.Fuse is not null) throw new DeviceSetException($"{at}: a {d.Type.ToString().ToLowerInvariant()} does not take a fuse setting.");
                    // RC5 WP4: a 21 with distance zones or an 87 with a differential needs no time-current curve.
                    if ((d.IsEvaluatedFunction || d.Function is "50N" or "51N" or "67" or "21") && d.Distance is null && d.Differential is null)
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
                    CheckFunctionSettings(at, d, br);
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
        foreach (PlacedDevice d in set.Devices)
            if (d.Distance is { } ds) CheckDistanceExtensions($"Device '{d.Id}'", d, ds, set, model);
        // RC5 WP18: the transformer and busbar differentials, checked only when a device carries them.
        foreach (PlacedDevice d in set.Devices)
        {
            if (d.TransformerDifferential is { } td) Placed.TransformerDifferential.Check($"Device '{d.Id}'", d, td, model);
            if (d.BusDifferential is { } bd) Placed.BusDifferential.Check($"Device '{d.Id}'", bd, model);
        }
        return new BoundDeviceSet(model, set, library);
    }

    /// <summary>RC5 WP17: a 21 on a two-winding transformer. Phase loops need a phase shift that is a whole multiple of
    /// 30 degrees (a vector group); earth loops need no phase shift and an explicit k0, because the zero-sequence path
    /// through a transformer depends on its windings and earthing.</summary>
    private static void CheckTransformerDistance(string at, DistanceSetting ds, Branch br)
    {
        double shift = br.PhaseShiftDeg;
        double steps = shift / 30.0;
        if (steps != Math.Round(steps))
            throw new DeviceSetException($"{at}: a 21 on a transformer with a phase shift of {shift.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} degrees is refused; the shift must be a whole multiple of 30 degrees.");
        if (ds.Loops != DistanceLoops.Phase && shift != 0.0)
            throw new DeviceSetException($"{at}: earth loops through a phase-shifting transformer are refused (the zero-sequence path depends on the vector group); set loops to phase.");
        if (ds.Loops != DistanceLoops.Phase && ds.K0 is null)
            throw new DeviceSetException($"{at}: earth loops on a transformer need an explicit k0.");
        if (ds.Scheme is not null || ds.Mutual is not null)
            throw new DeviceSetException($"{at}: schemes and mutual compensation are for lines, not transformers.");
    }

    /// <summary>RC5 WP17: the scheme, power-swing, load-blinder, switch-onto-fault and mutual settings, checked only when a
    /// device carries them.</summary>
    private static void CheckDistanceExtensions(string at, PlacedDevice d, DistanceSetting ds, DeviceSet set, GridModel model)
    {
        static bool Fin(double v) => double.IsFinite(v);
        int forward = ds.Zones.Count(z => !z.Reverse);
        if (ds.Scheme is { } sc)
        {
            if (!(sc.ChannelS >= 0.0) || !Fin(sc.ChannelS) || !(sc.CoordinationS >= 0.0) || !Fin(sc.CoordinationS))
                throw new DeviceSetException($"{at}: the scheme's channelS and coordinationS must be finite and >= 0 s.");
            PlacedDevice? remote = set.Devices.FirstOrDefault(x => x.Id == sc.RemoteId);
            if (remote is null) throw new DeviceSetException($"{at}: the scheme's remote device '{sc.RemoteId}' is not in the device file.");
            if (remote.Id == d.Id) throw new DeviceSetException($"{at}: the scheme's remote device is the device itself.");
            if (remote.Function != "21" || remote.Distance is null)
                throw new DeviceSetException($"{at}: the scheme's remote device '{sc.RemoteId}' must be a 21 with distance zones.");
            if (remote.Location.BranchIndex != d.Location.BranchIndex || remote.Location.End == d.Location.End)
                throw new DeviceSetException($"{at}: the scheme's remote device '{sc.RemoteId}' must sit at the other end of branch {d.Location.BranchIndex}.");
            int remoteForward = remote.Distance.Zones.Count(z => !z.Reverse);
            if (sc.Type != SchemeType.Dutt && forward < 2)
                throw new DeviceSetException($"{at}: a {sc.Type.ToString().ToUpperInvariant()} scheme needs a zone 2 at this end.");
            if (sc.Type == SchemeType.Pott && remoteForward < 2)
                throw new DeviceSetException($"{at}: a POTT scheme needs a zone 2 at the remote end '{sc.RemoteId}'.");
            if (sc.Type == SchemeType.Dcb && remote.Distance.Zones.All(z => !z.Reverse))
                throw new DeviceSetException($"{at}: a DCB scheme needs a reverse zone at the remote end '{sc.RemoteId}' to send the block.");
        }
        if (ds.PowerSwing is { } ps)
        {
            if (!(ps.InnerR > 0.0) || !(ps.OuterR > ps.InnerR) || !(ps.InnerX > 0.0) || !(ps.OuterX > ps.InnerX)
                || !Fin(ps.OuterR) || !Fin(ps.OuterX))
                throw new DeviceSetException($"{at}: the power-swing blinders need 0 < innerR < outerR and 0 < innerX < outerX, all finite.");
            if (!(ps.DeltaTS > 0.0) || !Fin(ps.DeltaTS) || !(ps.UnblockS > 0.0) || !Fin(ps.UnblockS))
                throw new DeviceSetException($"{at}: the power-swing deltaTS and unblockS must be finite and > 0 s.");
            if (ps.BlockZones.Count == 0) throw new DeviceSetException($"{at}: the power-swing blockZones list is empty.");
            foreach (int z in ps.BlockZones)
                if (z < 1 || z > ds.Zones.Count) throw new DeviceSetException($"{at}: the power-swing blockZones names zone {z}, which the element does not have.");
        }
        if (ds.LoadEncroachment is { } le)
        {
            if (ds.Loops == DistanceLoops.Earth) throw new DeviceSetException($"{at}: the load blinder acts on phase loops; this element measures earth loops only.");
            if (!(le.FwdAngleDeg >= 0.0 && le.FwdAngleDeg < 90.0) || !(le.RevAngleDeg >= 0.0 && le.RevAngleDeg < 90.0))
                throw new DeviceSetException($"{at}: the load blinder angles must be in [0, 90) degrees.");
            if (!(le.MinLoadOhms > 0.0) || !Fin(le.MinLoadOhms)) throw new DeviceSetException($"{at}: the load blinder minLoadOhms must be finite and > 0.");
        }
        if (ds.Sotf is { } so)
        {
            if (!(so.WindowS > 0.0) || !Fin(so.WindowS)) throw new DeviceSetException($"{at}: the switch-onto-fault windowS must be finite and > 0 s.");
            if (so.Mode == SotfMode.Z2 && forward < 2) throw new DeviceSetException($"{at}: switch-onto-fault mode Z2 needs a zone 2.");
        }
        if (ds.Mutual is { } mu)
        {
            if (ds.Loops == DistanceLoops.Phase) throw new DeviceSetException($"{at}: mutual compensation acts on earth loops; this element measures phase loops only.");
            int p = mu.ParallelBranch;
            if (p < 0 || p >= model.Branches.Count) throw new DeviceSetException($"{at}: the mutual branch {p} is outside the case (0..{model.Branches.Count - 1}).");
            if (p == d.Location.BranchIndex) throw new DeviceSetException($"{at}: the mutual branch is the protected branch itself.");
            Branch own = model.Branches[d.Location.BranchIndex], par = model.Branches[p];
            bool same = par.From == own.From && par.To == own.To, reversed = par.From == own.To && par.To == own.From;
            if (!same && !reversed)
                throw new DeviceSetException($"{at}: the mutual branch {p} ({par.From}-{par.To}) does not end at the protected line's buses ({own.From}-{own.To}); only parallel lines between the same buses are compensated.");
            if (mu.K0m is { } k)
            {
                if (!Fin(k.Magnitude) || !(k.Magnitude >= 0.0) || !Fin(k.AngleDeg)) throw new DeviceSetException($"{at}: k0m must have a finite magnitude >= 0 and a finite angle.");
            }
            else if (!model.ZeroSequenceMutuals.Any(m => (m.BranchA == d.Location.BranchIndex && m.BranchB == p) || (m.BranchB == d.Location.BranchIndex && m.BranchA == p)))
                throw new DeviceSetException($"{at}: no k0m is given and the case has no zero-sequence mutual coupling between branches {d.Location.BranchIndex} and {p}.");
        }
    }

    /// <summary>RC5 WP4: the checks on the distance, directional and differential settings, made only when a device carries
    /// them, so every device set that bound before binds exactly as before.</summary>
    private static void CheckFunctionSettings(string at, PlacedDevice d, Branch br)
    {
        static bool Fin(double v) => double.IsFinite(v);
        if (d.Function == "87" && d.Differential is null && d.TransformerDifferential is null && d.BusDifferential is null)
            throw new DeviceSetException($"{at}: an 87 needs a differential setting.");
        if ((d.Distance is not null || d.Directional is not null) && (!(d.VtPrimaryV > 0.0) || !(d.VtSecondaryV > 0.0) || !Fin(d.VtPrimaryV) || !Fin(d.VtSecondaryV)))
            throw new DeviceSetException($"{at}: a {(d.Distance is not null ? "distance" : "directional")} element needs a VT (primaryV and secondaryV finite and > 0).");
        if (d.Differential is not null && br.IsTransformer)
            throw new DeviceSetException($"{at}: a 87 on a transformer needs vector-group compensation; out of scope.");
        // RC5 WP17: a 21 on a transformer only with the opt-in onTransformer member; without it, refused word for word as before.
        if (d.Distance is { OnTransformer: false } && br.IsTransformer)
            throw new DeviceSetException($"{at}: a 21 on a transformer needs vector-group compensation; out of scope.");
        if (d.Distance is { OnTransformer: true })
        {
            if (!br.IsTransformer) throw new DeviceSetException($"{at}: onTransformer is set but branch {d.Location.BranchIndex} is not a transformer.");
            CheckTransformerDistance(at, d.Distance, br);
        }
        if (d.Distance is { } ds)
        {
            // RC5 WP17: reverse zones come after the forward zones; there are one to three forward zones and at most one
            // reverse zone. A file without reverse zones is checked exactly as before.
            int forward = ds.Zones.Count(z => !z.Reverse);
            if (forward == ds.Zones.Count)
            {
                if (ds.Zones.Count is < 1 or > 3) throw new DeviceSetException($"{at}: a distance element has 1 to 3 zones, not {ds.Zones.Count}.");
            }
            else if (forward is < 1 or > 3 || ds.Zones.Count - forward > 1)
                throw new DeviceSetException($"{at}: a distance element has 1 to 3 forward zones and at most one reverse zone, not {forward} and {ds.Zones.Count - forward}.");
            for (int z = 0; z < ds.Zones.Count; z++)
                if (ds.Zones[z].Reverse && z < forward) throw new DeviceSetException($"{at}: zone {z + 1} is reverse; reverse zones follow the forward zones.");
            if (ds.K0 is { } k && (!Fin(k.Magnitude) || !(k.Magnitude >= 0.0) || !Fin(k.AngleDeg)))
                throw new DeviceSetException($"{at}: k0 must have a finite magnitude >= 0 and a finite angle.");
            if (ds.Characteristic == DistanceCharacteristic.Quad && !(Fin(ds.DirectionalLowDeg) && Fin(ds.DirectionalHighDeg) && ds.DirectionalLowDeg < ds.DirectionalHighDeg
                && ds.DirectionalLowDeg >= -180.0 && ds.DirectionalHighDeg <= 180.0))
                throw new DeviceSetException($"{at}: the directional limits must be finite, within -180..180 degrees and low < high.");
            for (int z = 0; z < ds.Zones.Count; z++)
            {
                DistanceZone zn = ds.Zones[z];
                string za = $"{at}: zone {z + 1}";
                if (!(zn.ReachOhms > 0.0) || !Fin(zn.ReachOhms)) throw new DeviceSetException($"{za} reach must be finite and > 0 ohms.");
                if (!Fin(zn.AngleDeg) || !(zn.AngleDeg > -180.0 && zn.AngleDeg <= 180.0)) throw new DeviceSetException($"{za} angle must be in (-180, 180] degrees.");
                if (!(zn.TimeS >= 0.0) || !Fin(zn.TimeS)) throw new DeviceSetException($"{za} time must be finite and >= 0 s.");
                if (ds.Characteristic == DistanceCharacteristic.Quad)
                {
                    if (!(zn.AngleDeg > 0.0 && zn.AngleDeg < 180.0)) throw new DeviceSetException($"{za}: a quad zone angle must be in (0, 180) degrees.");
                    if (!(zn.ResistiveReachOhms > 0.0) || !Fin(zn.ResistiveReachOhms) || !(zn.LeftReachOhms > 0.0) || !Fin(zn.LeftReachOhms))
                        throw new DeviceSetException($"{za} resistive reaches must be finite and > 0 ohms.");
                }
                if (z > 0 && !zn.Reverse)
                {
                    DistanceZone prev = ds.Zones[z - 1];
                    if (zn.ReachOhms < prev.ReachOhms) throw new DeviceSetException($"{za} reach is shorter than zone {z}'s.");
                    if (zn.TimeS < prev.TimeS) throw new DeviceSetException($"{za} time is shorter than zone {z}'s.");
                }
            }
        }
        if (d.Directional is { } dr)
        {
            if (!Fin(dr.CharacteristicAngleDeg) || !(dr.CharacteristicAngleDeg >= -180.0 && dr.CharacteristicAngleDeg <= 180.0))
                throw new DeviceSetException($"{at}: the characteristic angle must be within -180..180 degrees.");
            if (!(dr.MinPolarisingVPu >= 0.0) || !Fin(dr.MinPolarisingVPu)) throw new DeviceSetException($"{at}: minPolarisingV must be finite and >= 0 pu.");
        }
        if (d.Differential is { } df)
        {
            if (!(df.Is1A > 0.0) || !Fin(df.Is1A) || !(df.Is2A > 0.0) || !Fin(df.Is2A)) throw new DeviceSetException($"{at}: is1A and is2A must be finite and > 0 A.");
            if (!(df.K1 >= 0.0) || !Fin(df.K1) || !Fin(df.K2)) throw new DeviceSetException($"{at}: the slopes must be finite and k1 >= 0.");
            if (df.K2 < df.K1) throw new DeviceSetException($"{at}: the second slope k2 must not be below the first slope k1.");
            if (!(df.RemoteCtPrimaryA > 0.0) || !(df.RemoteCtSecondaryA > 0.0) || !Fin(df.RemoteCtPrimaryA) || !Fin(df.RemoteCtSecondaryA))
                throw new DeviceSetException($"{at}: the remote CT primary and secondary amps must be finite and > 0.");
        }
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
