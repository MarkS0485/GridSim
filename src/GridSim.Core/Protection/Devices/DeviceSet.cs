using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Core.Protection.Devices;

/// <summary>An invalid device set, refused before any study runs (an input error at the CLI).</summary>
public sealed class DeviceSetException(string message) : StudyCaseException(message);

/// <summary>The devices document (<c>gridsim-devices/1</c>): a sidecar bound to a case by branch identity (like the R4-01
/// groups), so the case file itself is untouched. Devices are in file order, which is the device-number order of every
/// result.</summary>
public sealed record DeviceSet(IReadOnlyList<PlacedDevice> Devices)
{
    public const string FormatVersion = "gridsim-devices/1";

    /// <summary>Strict reader: unknown members, missing members and wrong types are refused, naming the path.</summary>
    public static DeviceSet Parse(byte[] bytes)
    {
        try { return ParseCore(bytes); }
        catch (DeviceSetException) { throw; }
        catch (StudyCaseException ex) { throw new DeviceSetException(ex.Message); }
    }

    private static DeviceSet ParseCore(byte[] bytes)
    {
        using JsonDocument doc = StudyCaseJson.Parse(bytes, "The devices document");
        JsonElement root = doc.RootElement;
        StudyCaseJson.Obj(root, "devices document", ["manifestVersion", "devices"], ["manifestVersion", "devices"]);
        string version = StudyCaseJson.Str(root.GetProperty("manifestVersion"), "manifestVersion");
        if (version != FormatVersion) throw new DeviceSetException($"manifestVersion must be \"{FormatVersion}\", not \"{version}\".");
        var list = new List<PlacedDevice>();
        foreach ((JsonElement d, int i) in StudyCaseJson.Arr(root.GetProperty("devices"), "devices"))
        {
            string at = $"devices[{i}]";
            StudyCaseJson.Obj(d, at, ["id", "type", "function", "branch", "ct", "vt", "shots", "instantaneous", "recloseIntervalsS", "fuse", "rating",
                "distance", "directional", "differential", "transformerDifferential", "busDifferential"],
                ["id", "type", "branch"]);
            string id = StudyCaseJson.Id(d.GetProperty("id"), at + ".id");
            string typeText = StudyCaseJson.Str(d.GetProperty("type"), at + ".type");
            DeviceType type = typeText switch
            {
                "relay" => DeviceType.Relay, "fuse" => DeviceType.Fuse, "recloser" => DeviceType.Recloser, "breaker" => DeviceType.Breaker,
                _ => throw new DeviceSetException($"{at}.type must be relay, fuse, recloser or breaker, not \"{typeText}\"."),
            };
            string function = d.TryGetProperty("function", out JsonElement fe) ? StudyCaseJson.Str(fe, at + ".function") : (type is DeviceType.Relay or DeviceType.Recloser ? "51" : "");
            if (function is not ("" or "50" or "51" or "50N" or "51N" or "67" or "21" or "87"))
                throw new DeviceSetException($"{at}.function must be 50, 51, 50N, 51N, 67, 21 or 87, not \"{function}\".");
            // RC5 WP4: the function settings, each accepted only with its own function.
            if (d.TryGetProperty("distance", out _) && function != "21") throw new DeviceSetException($"{at}.distance goes only with function 21, not \"{function}\".");
            if (d.TryGetProperty("directional", out _) && function != "67") throw new DeviceSetException($"{at}.directional goes only with function 67, not \"{function}\".");
            if (d.TryGetProperty("differential", out _) && function != "87") throw new DeviceSetException($"{at}.differential goes only with function 87, not \"{function}\".");
            // RC5 WP18: the transformer and busbar differentials, each only with function 87 and only one per device.
            bool hasT = d.TryGetProperty("transformerDifferential", out JsonElement tdE), hasB = d.TryGetProperty("busDifferential", out JsonElement bdE);
            if ((hasT || hasB) && function != "87")
                throw new DeviceSetException($"{at}.{(hasT ? "transformerDifferential" : "busDifferential")} goes only with function 87, not \"{function}\".");
            if ((hasT ? 1 : 0) + (hasB ? 1 : 0) + (d.TryGetProperty("differential", out _) ? 1 : 0) > 1)
                throw new DeviceSetException($"{at}: give one of differential, transformerDifferential and busDifferential, not more.");

            JsonElement b = d.GetProperty("branch");
            StudyCaseJson.Obj(b, at + ".branch", ["index", "from", "to", "end"], ["index", "from", "to", "end"]);
            string end = StudyCaseJson.Str(b.GetProperty("end"), at + ".branch.end");
            if (end is not ("from" or "to")) throw new DeviceSetException($"{at}.branch.end must be from or to, not \"{end}\".");
            var loc = new DeviceLocation(StudyCaseJson.Int(b.GetProperty("index"), at + ".branch.index"),
                StudyCaseJson.Int(b.GetProperty("from"), at + ".branch.from"), StudyCaseJson.Int(b.GetProperty("to"), at + ".branch.to"), end);

            double ctP = 1.0, ctS = 1.0;
            if (d.TryGetProperty("ct", out JsonElement ct))
            {
                StudyCaseJson.Obj(ct, at + ".ct", ["primaryA", "secondaryA"], ["primaryA", "secondaryA"]);
                ctP = StudyCaseJson.Num(ct.GetProperty("primaryA"), at + ".ct.primaryA");
                ctS = StudyCaseJson.Num(ct.GetProperty("secondaryA"), at + ".ct.secondaryA");
            }
            double vtP = 0.0, vtS = 0.0;
            if (d.TryGetProperty("vt", out JsonElement vt))
            {
                StudyCaseJson.Obj(vt, at + ".vt", ["primaryV", "secondaryV"], ["primaryV", "secondaryV"]);
                vtP = StudyCaseJson.Num(vt.GetProperty("primaryV"), at + ".vt.primaryV");
                vtS = StudyCaseJson.Num(vt.GetProperty("secondaryV"), at + ".vt.secondaryV");
            }
            var shots = new List<ShotSetting>();
            if (d.TryGetProperty("shots", out JsonElement sh))
                foreach ((JsonElement s, int si) in StudyCaseJson.Arr(sh, at + ".shots"))
                {
                    string sa = $"{at}.shots[{si}]";
                    StudyCaseJson.Obj(s, sa, ["curve", "pickupA", "timeDial", "definiteTimeS"], ["curve", "pickupA"]);
                    shots.Add(new ShotSetting(StudyCaseJson.Str(s.GetProperty("curve"), sa + ".curve"), StudyCaseJson.Num(s.GetProperty("pickupA"), sa + ".pickupA"),
                        s.TryGetProperty("timeDial", out JsonElement td) ? StudyCaseJson.Num(td, sa + ".timeDial") : 1.0,
                        s.TryGetProperty("definiteTimeS", out JsonElement dt) ? StudyCaseJson.Num(dt, sa + ".definiteTimeS") : 0.0));
                }
            InstantaneousSetting? inst = null;
            if (d.TryGetProperty("instantaneous", out JsonElement ie))
            {
                StudyCaseJson.Obj(ie, at + ".instantaneous", ["pickupA", "delayS"], ["pickupA"]);
                inst = new InstantaneousSetting(StudyCaseJson.Num(ie.GetProperty("pickupA"), at + ".instantaneous.pickupA"),
                    ie.TryGetProperty("delayS", out JsonElement dl) ? StudyCaseJson.Num(dl, at + ".instantaneous.delayS") : 0.0);
            }
            var reclose = new List<double>();
            if (d.TryGetProperty("recloseIntervalsS", out JsonElement re))
                foreach ((JsonElement r, int ri) in StudyCaseJson.Arr(re, at + ".recloseIntervalsS")) reclose.Add(StudyCaseJson.Num(r, $"{at}.recloseIntervalsS[{ri}]"));
            FuseSetting? fuse = null;
            if (d.TryGetProperty("fuse", out JsonElement fu))
            {
                StudyCaseJson.Obj(fu, at + ".fuse", ["meltCurve", "clearCurve", "ratingA"], ["meltCurve", "clearCurve", "ratingA"]);
                fuse = new FuseSetting(StudyCaseJson.Str(fu.GetProperty("meltCurve"), at + ".fuse.meltCurve"),
                    StudyCaseJson.Str(fu.GetProperty("clearCurve"), at + ".fuse.clearCurve"), StudyCaseJson.Num(fu.GetProperty("ratingA"), at + ".fuse.ratingA"));
            }
            Iec60909.BreakerRating? rating = null;
            if (d.TryGetProperty("rating", out JsonElement ra))
            {
                string rt = at + ".rating";
                StudyCaseJson.Obj(ra, rt, ["ratedVoltageKv", "makingPeakKa", "breakingSymKa", "shortTimeWithstandKa", "shortTimeSeconds"],
                    ["ratedVoltageKv", "makingPeakKa", "breakingSymKa", "shortTimeWithstandKa"]);
                rating = new Iec60909.BreakerRating(StudyCaseJson.Num(ra.GetProperty("ratedVoltageKv"), rt + ".ratedVoltageKv"),
                    StudyCaseJson.Num(ra.GetProperty("makingPeakKa"), rt + ".makingPeakKa"), StudyCaseJson.Num(ra.GetProperty("breakingSymKa"), rt + ".breakingSymKa"),
                    StudyCaseJson.Num(ra.GetProperty("shortTimeWithstandKa"), rt + ".shortTimeWithstandKa"),
                    ra.TryGetProperty("shortTimeSeconds", out JsonElement st) ? StudyCaseJson.Num(st, rt + ".shortTimeSeconds") : 1.0);
            }
            list.Add(new PlacedDevice(id, type, function, loc, ctP, ctS, vtP, vtS, shots, inst, reclose, fuse, rating)
            {
                Distance = d.TryGetProperty("distance", out JsonElement di) ? ParseDistance(di, at + ".distance") : null,
                Directional = d.TryGetProperty("directional", out JsonElement de) ? ParseDirectional(de, at + ".directional") : null,
                Differential = d.TryGetProperty("differential", out JsonElement df) ? ParseDifferential(df, at + ".differential", ctP, ctS) : null,
                TransformerDifferential = hasT ? ParseTransformerDifferential(tdE, at + ".transformerDifferential") : null,
                BusDifferential = hasB ? ParseBusDifferential(bdE, at + ".busDifferential") : null,
            });
        }
        return new DeviceSet(list);
    }

    private static DistanceSetting ParseDistance(JsonElement e, string at)
    {
        StudyCaseJson.Obj(e, at, ["characteristic", "loops", "k0", "zones", "directionalLowDeg", "directionalHighDeg",
            "scheme", "powerSwing", "loadEncroachment", "sotf", "mutual", "onTransformer"], ["characteristic", "zones"]);
        string ch = StudyCaseJson.Str(e.GetProperty("characteristic"), at + ".characteristic");
        DistanceCharacteristic c = ch switch
        {
            "mho" => DistanceCharacteristic.Mho, "quad" => DistanceCharacteristic.Quad,
            _ => throw new DeviceSetException($"{at}.characteristic must be mho or quad, not \"{ch}\"."),
        };
        DistanceLoops loops = DistanceLoops.Both;
        if (e.TryGetProperty("loops", out JsonElement le))
        {
            string l = StudyCaseJson.Str(le, at + ".loops");
            loops = l switch
            {
                "phase" => DistanceLoops.Phase, "earth" => DistanceLoops.Earth, "both" => DistanceLoops.Both,
                _ => throw new DeviceSetException($"{at}.loops must be phase, earth or both, not \"{l}\"."),
            };
        }
        (double, double)? k0 = null;
        if (e.TryGetProperty("k0", out JsonElement ke))
        {
            StudyCaseJson.Obj(ke, at + ".k0", ["magnitude", "angleDeg"], ["magnitude", "angleDeg"]);
            k0 = (StudyCaseJson.Num(ke.GetProperty("magnitude"), at + ".k0.magnitude"), StudyCaseJson.Num(ke.GetProperty("angleDeg"), at + ".k0.angleDeg"));
        }
        var zones = new List<DistanceZone>();
        foreach ((JsonElement z, int i) in StudyCaseJson.Arr(e.GetProperty("zones"), at + ".zones"))
        {
            string za = $"{at}.zones[{i}]";
            StudyCaseJson.Obj(z, za, ["reachOhms", "angleDeg", "timeS", "resistiveReachOhms", "leftReachOhms", "reverse"], ["reachOhms", "angleDeg", "timeS"]);
            bool hasR = z.TryGetProperty("resistiveReachOhms", out JsonElement re);
            bool hasL = z.TryGetProperty("leftReachOhms", out JsonElement rl);
            if (c == DistanceCharacteristic.Mho && (hasR || hasL)) throw new DeviceSetException($"{za}: a mho zone takes no resistive reach.");
            if (c == DistanceCharacteristic.Quad && !hasR) throw new DeviceSetException($"{za}.resistiveReachOhms is required for a quad zone.");
            double r = hasR ? StudyCaseJson.Num(re, za + ".resistiveReachOhms") : 0.0;
            double left = hasL ? StudyCaseJson.Num(rl, za + ".leftReachOhms") : r;
            var zone = new DistanceZone(StudyCaseJson.Num(z.GetProperty("reachOhms"), za + ".reachOhms"), StudyCaseJson.Num(z.GetProperty("angleDeg"), za + ".angleDeg"),
                StudyCaseJson.Num(z.GetProperty("timeS"), za + ".timeS"), r, left);
            // RC5 WP17: an optional reverse flag; absent, the zone is exactly as before.
            if (z.TryGetProperty("reverse", out JsonElement rv)) zone = zone with { Reverse = Bool(rv, za + ".reverse") };
            zones.Add(zone);
        }
        double lo = e.TryGetProperty("directionalLowDeg", out JsonElement lw) ? StudyCaseJson.Num(lw, at + ".directionalLowDeg") : -30.0;
        double hi = e.TryGetProperty("directionalHighDeg", out JsonElement hw) ? StudyCaseJson.Num(hw, at + ".directionalHighDeg") : 120.0;
        // RC5 WP17: the optional scheme, power-swing, load-blinder, switch-onto-fault and mutual members. Each absent member
        // leaves the setting exactly as before.
        return new DistanceSetting(c, loops, k0, zones, lo, hi)
        {
            Scheme = e.TryGetProperty("scheme", out JsonElement se) ? ParseScheme(se, at + ".scheme") : null,
            PowerSwing = e.TryGetProperty("powerSwing", out JsonElement pe) ? ParsePowerSwing(pe, at + ".powerSwing") : null,
            LoadEncroachment = e.TryGetProperty("loadEncroachment", out JsonElement le2) ? ParseLoad(le2, at + ".loadEncroachment") : null,
            Sotf = e.TryGetProperty("sotf", out JsonElement so) ? ParseSotf(so, at + ".sotf") : null,
            Mutual = e.TryGetProperty("mutual", out JsonElement mu) ? ParseMutual(mu, at + ".mutual") : null,
            OnTransformer = e.TryGetProperty("onTransformer", out JsonElement ot) && Bool(ot, at + ".onTransformer"),
        };
    }

    private static bool Bool(JsonElement e, string at) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new DeviceSetException($"{at} must be true or false."),
    };

    private static SchemeSetting ParseScheme(JsonElement e, string at)
    {
        StudyCaseJson.Obj(e, at, ["type", "remote", "channelS", "coordinationS"], ["type", "remote"]);
        string t = StudyCaseJson.Str(e.GetProperty("type"), at + ".type");
        SchemeType type = t switch
        {
            "DUTT" => SchemeType.Dutt, "PUTT" => SchemeType.Putt, "POTT" => SchemeType.Pott, "DCB" => SchemeType.Dcb,
            _ => throw new DeviceSetException($"{at}.type must be DUTT, PUTT, POTT or DCB, not \"{t}\"."),
        };
        return new SchemeSetting(type, StudyCaseJson.Id(e.GetProperty("remote"), at + ".remote"),
            e.TryGetProperty("channelS", out JsonElement ch) ? StudyCaseJson.Num(ch, at + ".channelS") : 0.0,
            e.TryGetProperty("coordinationS", out JsonElement co) ? StudyCaseJson.Num(co, at + ".coordinationS") : 0.0);
    }

    private static PowerSwingSetting ParsePowerSwing(JsonElement e, string at)
    {
        StudyCaseJson.Obj(e, at, ["outerR", "innerR", "outerX", "innerX", "deltaTS", "blockZones", "unblockS"],
            ["outerR", "innerR", "outerX", "innerX", "deltaTS", "blockZones"]);
        var zones = new List<int>();
        foreach ((JsonElement z, int i) in StudyCaseJson.Arr(e.GetProperty("blockZones"), at + ".blockZones")) zones.Add(StudyCaseJson.Int(z, $"{at}.blockZones[{i}]"));
        return new PowerSwingSetting(StudyCaseJson.Num(e.GetProperty("outerR"), at + ".outerR"), StudyCaseJson.Num(e.GetProperty("innerR"), at + ".innerR"),
            StudyCaseJson.Num(e.GetProperty("outerX"), at + ".outerX"), StudyCaseJson.Num(e.GetProperty("innerX"), at + ".innerX"),
            StudyCaseJson.Num(e.GetProperty("deltaTS"), at + ".deltaTS"), zones,
            e.TryGetProperty("unblockS", out JsonElement u) ? StudyCaseJson.Num(u, at + ".unblockS") : 2.0);
    }

    private static LoadEncroachmentSetting ParseLoad(JsonElement e, string at)
    {
        StudyCaseJson.Obj(e, at, ["fwdAngleDeg", "revAngleDeg", "minLoadOhms"], ["fwdAngleDeg", "revAngleDeg", "minLoadOhms"]);
        return new LoadEncroachmentSetting(StudyCaseJson.Num(e.GetProperty("fwdAngleDeg"), at + ".fwdAngleDeg"),
            StudyCaseJson.Num(e.GetProperty("revAngleDeg"), at + ".revAngleDeg"), StudyCaseJson.Num(e.GetProperty("minLoadOhms"), at + ".minLoadOhms"));
    }

    private static SotfSetting ParseSotf(JsonElement e, string at)
    {
        StudyCaseJson.Obj(e, at, ["windowS", "mode"], ["windowS", "mode"]);
        string m = StudyCaseJson.Str(e.GetProperty("mode"), at + ".mode");
        SotfMode mode = m switch
        {
            "Z2" => SotfMode.Z2, "nondirectional" => SotfMode.Nondirectional,
            _ => throw new DeviceSetException($"{at}.mode must be Z2 or nondirectional, not \"{m}\"."),
        };
        return new SotfSetting(StudyCaseJson.Num(e.GetProperty("windowS"), at + ".windowS"), mode);
    }

    private static MutualSetting ParseMutual(JsonElement e, string at)
    {
        StudyCaseJson.Obj(e, at, ["branch", "k0m"], ["branch"]);
        (double, double)? k0m = null;
        if (e.TryGetProperty("k0m", out JsonElement ke) && ke.ValueKind != JsonValueKind.Null)
        {
            StudyCaseJson.Obj(ke, at + ".k0m", ["magnitude", "angleDeg"], ["magnitude", "angleDeg"]);
            k0m = (StudyCaseJson.Num(ke.GetProperty("magnitude"), at + ".k0m.magnitude"), StudyCaseJson.Num(ke.GetProperty("angleDeg"), at + ".k0m.angleDeg"));
        }
        return new MutualSetting(StudyCaseJson.Int(e.GetProperty("branch"), at + ".branch"), k0m);
    }

    private static DirectionalSetting ParseDirectional(JsonElement e, string at)
    {
        StudyCaseJson.Obj(e, at, ["polarisation", "characteristicAngleDeg", "direction", "minPolarisingV"], ["polarisation", "characteristicAngleDeg"]);
        string p = StudyCaseJson.Str(e.GetProperty("polarisation"), at + ".polarisation");
        Polarisation pol = p switch
        {
            "quadrature" => Polarisation.Quadrature, "zero-sequence" => Polarisation.ZeroSequence, "negative-sequence" => Polarisation.NegativeSequence,
            _ => throw new DeviceSetException($"{at}.polarisation must be quadrature, zero-sequence or negative-sequence, not \"{p}\"."),
        };
        bool forward = true;
        if (e.TryGetProperty("direction", out JsonElement de))
        {
            string dir = StudyCaseJson.Str(de, at + ".direction");
            forward = dir switch
            {
                "forward" => true, "reverse" => false,
                _ => throw new DeviceSetException($"{at}.direction must be forward or reverse, not \"{dir}\"."),
            };
        }
        double minV = e.TryGetProperty("minPolarisingV", out JsonElement mv) ? StudyCaseJson.Num(mv, at + ".minPolarisingV") : 0.01;
        return new DirectionalSetting(pol, StudyCaseJson.Num(e.GetProperty("characteristicAngleDeg"), at + ".characteristicAngleDeg"), forward, minV);
    }

    private static CtLocation ParseCtLocation(JsonElement b, string at)
    {
        StudyCaseJson.Obj(b, at, ["index", "from", "to", "end"], ["index", "from", "to", "end"]);
        string end = StudyCaseJson.Str(b.GetProperty("end"), at + ".end");
        if (end is not ("from" or "to")) throw new DeviceSetException($"{at}.end must be from or to, not \"{end}\".");
        return new CtLocation(StudyCaseJson.Int(b.GetProperty("index"), at + ".index"), StudyCaseJson.Int(b.GetProperty("from"), at + ".from"),
            StudyCaseJson.Int(b.GetProperty("to"), at + ".to"), end);
    }

    /// <summary>RC5 WP18: the 87T member. hvCt and lvCt are optional and only place a CT on another branch: with the settings
    /// in pu of the rating, the CT ratios cancel, so none is asked for.</summary>
    private static TransformerDifferentialSetting ParseTransformerDifferential(JsonElement e, string at)
    {
        StudyCaseJson.Obj(e, at, ["vectorGroup", "ratedMva", "hvCt", "lvCt", "is1", "k1", "is2", "k2"], ["vectorGroup", "ratedMva", "is1", "k1", "is2", "k2"]);
        CtLocation? Side(string name)
        {
            if (!e.TryGetProperty(name, out JsonElement c)) return null;
            StudyCaseJson.Obj(c, $"{at}.{name}", ["branch"], ["branch"]);
            return ParseCtLocation(c.GetProperty("branch"), $"{at}.{name}.branch");
        }
        return new TransformerDifferentialSetting(StudyCaseJson.Str(e.GetProperty("vectorGroup"), at + ".vectorGroup"),
            StudyCaseJson.Num(e.GetProperty("ratedMva"), at + ".ratedMva"), StudyCaseJson.Num(e.GetProperty("is1"), at + ".is1"),
            StudyCaseJson.Num(e.GetProperty("k1"), at + ".k1"), StudyCaseJson.Num(e.GetProperty("is2"), at + ".is2"), StudyCaseJson.Num(e.GetProperty("k2"), at + ".k2"))
        {
            HvCt = Side("hvCt"),
            LvCt = Side("lvCt"),
        };
    }

    /// <summary>RC5 WP18: the 87B member.</summary>
    private static BusDifferentialSetting ParseBusDifferential(JsonElement e, string at)
    {
        StudyCaseJson.Obj(e, at, ["zone", "cts", "is1A", "k1", "is2A", "k2", "bias", "checkZone"], ["zone", "cts", "is1A", "k1"]);
        static List<int> Ids(JsonElement a, string where)
        {
            var ids = new List<int>();
            foreach ((JsonElement z, int i) in StudyCaseJson.Arr(a, where)) ids.Add(StudyCaseJson.Int(z, $"{where}[{i}]"));
            return ids;
        }
        var cts = new List<BusCt>();
        foreach ((JsonElement c, int i) in StudyCaseJson.Arr(e.GetProperty("cts"), at + ".cts"))
        {
            string ca = $"{at}.cts[{i}]";
            StudyCaseJson.Obj(c, ca, ["branch", "ct"], ["branch", "ct"]);
            JsonElement ct = c.GetProperty("ct");
            StudyCaseJson.Obj(ct, ca + ".ct", ["primaryA", "secondaryA"], ["primaryA", "secondaryA"]);
            cts.Add(new BusCt(ParseCtLocation(c.GetProperty("branch"), ca + ".branch"), StudyCaseJson.Num(ct.GetProperty("primaryA"), ca + ".ct.primaryA"),
                StudyCaseJson.Num(ct.GetProperty("secondaryA"), ca + ".ct.secondaryA")));
        }
        BusBias bias = BusBias.Sum;
        if (e.TryGetProperty("bias", out JsonElement be))
        {
            string b = StudyCaseJson.Str(be, at + ".bias");
            bias = b switch
            {
                "sum" => BusBias.Sum, "max" => BusBias.Max,
                _ => throw new DeviceSetException($"{at}.bias must be sum or max, not \"{b}\"."),
            };
        }
        bool hasIs2 = e.TryGetProperty("is2A", out JsonElement i2), hasK2 = e.TryGetProperty("k2", out JsonElement k2);
        if (hasIs2 != hasK2) throw new DeviceSetException($"{at}: give is2A and k2 together for a dual slope, or neither.");
        return new BusDifferentialSetting(Ids(e.GetProperty("zone"), at + ".zone"), cts, StudyCaseJson.Num(e.GetProperty("is1A"), at + ".is1A"),
            StudyCaseJson.Num(e.GetProperty("k1"), at + ".k1"), bias)
        {
            Is2A = hasIs2 ? StudyCaseJson.Num(i2, at + ".is2A") : null,
            K2 = hasK2 ? StudyCaseJson.Num(k2, at + ".k2") : null,
            CheckZone = e.TryGetProperty("checkZone", out JsonElement cz) ? Ids(cz, at + ".checkZone") : null,
        };
    }

    private static DifferentialSetting ParseDifferential(JsonElement e, string at, double ctP, double ctS)
    {
        StudyCaseJson.Obj(e, at, ["is1A", "k1", "is2A", "k2", "remoteCt"], ["is1A", "k1", "is2A", "k2"]);
        double rp = ctP, rs = ctS;
        if (e.TryGetProperty("remoteCt", out JsonElement rc))
        {
            StudyCaseJson.Obj(rc, at + ".remoteCt", ["primaryA", "secondaryA"], ["primaryA", "secondaryA"]);
            rp = StudyCaseJson.Num(rc.GetProperty("primaryA"), at + ".remoteCt.primaryA");
            rs = StudyCaseJson.Num(rc.GetProperty("secondaryA"), at + ".remoteCt.secondaryA");
        }
        return new DifferentialSetting(StudyCaseJson.Num(e.GetProperty("is1A"), at + ".is1A"), StudyCaseJson.Num(e.GetProperty("k1"), at + ".k1"),
            StudyCaseJson.Num(e.GetProperty("is2A"), at + ".is2A"), StudyCaseJson.Num(e.GetProperty("k2"), at + ".k2"), rp, rs);
    }
}
