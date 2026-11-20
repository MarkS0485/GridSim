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
            StudyCaseJson.Obj(d, at, ["id", "type", "function", "branch", "ct", "vt", "shots", "instantaneous", "recloseIntervalsS", "fuse", "rating"],
                ["id", "type", "branch"]);
            string id = StudyCaseJson.Id(d.GetProperty("id"), at + ".id");
            string typeText = StudyCaseJson.Str(d.GetProperty("type"), at + ".type");
            DeviceType type = typeText switch
            {
                "relay" => DeviceType.Relay, "fuse" => DeviceType.Fuse, "recloser" => DeviceType.Recloser, "breaker" => DeviceType.Breaker,
                _ => throw new DeviceSetException($"{at}.type must be relay, fuse, recloser or breaker, not \"{typeText}\"."),
            };
            string function = d.TryGetProperty("function", out JsonElement fe) ? StudyCaseJson.Str(fe, at + ".function") : (type is DeviceType.Relay or DeviceType.Recloser ? "51" : "");
            if (function is not ("" or "50" or "51" or "50N" or "51N" or "67" or "21"))
                throw new DeviceSetException($"{at}.function must be 50, 51, 50N, 51N, 67 or 21, not \"{function}\".");

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
            list.Add(new PlacedDevice(id, type, function, loc, ctP, ctS, vtP, vtS, shots, inst, reclose, fuse, rating));
        }
        return new DeviceSet(list);
    }
}
