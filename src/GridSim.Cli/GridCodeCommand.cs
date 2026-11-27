using System.Text;
using System.Text.Json;
using GridSim.Core.Compliance.GridCode;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Protection;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP8: <c>gridsim grid-code --list | &lt;study.json&gt; [--out &lt;result.json&gt;]</c>, the grid-code compliance suite
/// (GB Grid Code ECC Issue 6 Revision 33, Regulation (EU) 2016/631) over <see cref="GridCodeStudy"/>. The study file is strict
/// JSON (<c>gridsim-gridcode-study/1</c>, schemas/gridcode); the result is <c>gridsim-gridcode/1</c>. Exit 0 COMPLIANT, 3
/// NON-COMPLIANT or INCOMPLETE, 1 input it cannot accept, 2 usage. The verb writes its own document with <c>--out</c>; the
/// shared <c>--json</c>/<c>--csv</c> writer (RC5 WP11) writes the same document through CliResultFromJson.
/// </summary>
internal static class GridCodeCommand
{
    private const string Verb = "grid-code";
    private const string Usage = "usage: gridsim grid-code <study.json> [--out <result.json>]   |   gridsim grid-code --list";
    public const string StudySchema = "gridsim-gridcode-study/1";

    private static readonly string[] CheckIds =
        { "frt-envelope", "frt-retained-voltage", "frt-recovery", "frt-uv-settings", "q-capability", "lfsm-o", "lfsm-u", "fsm-step", "rocof-settings", "rocof-event", "frequency-relays" };

    // RC5 WP22: the GB extension checks (gb-ecc only, run only when named; requirements gb-ecc-issue6-rev33-ext).
    private static readonly string[] ExtCheckIds = { FfciCheck.Id, LongFaultCheck.Id, StepResponseChecks.AvrId, StepResponseChecks.VctrlId };

    public static int Run(List<string> a, Func<string, GridModel> load, string root)
    {
        if (a.Count == 2 && a[1] == "--list")
        {
            foreach (string id in GridCodeRequirements.BuiltInIds) Console.WriteLine(id + "  " + GridCodeRequirements.Load(id).Title);
            Console.WriteLine("checks: " + string.Join(", ", CheckIds));
            return CliExit.Ok;
        }
        // 2.1.126: the extension checks are listed by their own flag, so the --list output of the tagged releases is unchanged.
        if (a.Count == 2 && a[1] == "--list-ext")
        {
            Console.WriteLine(GridCodeExtRequirements.GbId + "  " + GridCodeExtRequirements.LoadGb().Title);
            Console.WriteLine("gb-ecc extension checks (run only when named): " + string.Join(", ", ExtCheckIds));
            return CliExit.Ok;
        }
        string? input = null, output = null;
        for (int i = 1; i < a.Count; i++)
        {
            if (a[i] == "--out" && i + 1 < a.Count && output is null && !a[i + 1].StartsWith('-')) output = a[++i];
            else if (!a[i].StartsWith('-') && input is null) input = a[i];
            else { Console.Error.WriteLine(Usage); return CliExit.Usage; }
        }
        if (input is null) { Console.Error.WriteLine(Usage); return CliExit.Usage; }
        string? temporary = null;
        try
        {
            string? outFull = output is null ? null : Path.GetFullPath(output);
            if (outFull is not null && (File.Exists(outFull) || Directory.Exists(outFull)))
                throw new IOException("Result path already exists; use a new path for this invocation.");
            string studyPath = Path.GetFullPath(input);
            byte[] bytes = File.ReadAllBytes(studyPath);
            string dir = Path.GetDirectoryName(studyPath)!;
            using JsonDocument doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes));
            JsonElement r = doc.RootElement;
            TransmissionSecurityCommand.CheckDuplicates(r);
            Keys(r, "study", new[] { "schema", "label", "case", "unit", "pccBus", "code", "moduleType", "technology", "connectionKv", "checks" },
                new[] { "dynData", "plantBuses", "extendedClearingTime", "frtProfile", "area", "fsmMode", "stepSeconds" });
            if (Str(r, "schema") != StudySchema) throw new InvalidDataException($"schema must be \"{StudySchema}\".");
            string label = Str(r, "label");
            if (string.IsNullOrWhiteSpace(label)) throw new InvalidDataException("label must not be empty.");

            string casePath = Path.GetFullPath(Str(r, "case"), dir);
            GridModel model = load(casePath);
            IReadOnlyList<SubtransientInit.MachineData>? data = null;
            string? dynName = null;
            byte[]? dynBytes = null;
            MachineDynamicsSet? dynSet = null;
            if (r.TryGetProperty("dynData", out JsonElement dd))
            {
                string dynPath = Path.GetFullPath(Str(dd, "dynData"), dir);
                string schemas = Gated.FindV2Schemas(root) ?? throw new InvalidDataException("grid-code: dynData needs the v2 schemas (schemas/v2 beside the program or above it), and none was found.");
                dynBytes = File.ReadAllBytes(dynPath);
                dynName = Path.GetFileName(dynPath);
                MachineDynamicsSet dyn = dynPath.EndsWith(".dyr", StringComparison.OrdinalIgnoreCase)
                    ? Gated.PsseDynamics(dynPath, schemas, model) : MachineDynamicsLoader.Load(dynPath, schemas, model);
                data = MachineData(model, dyn);
                dynSet = dyn;
            }

            JsonElement u = r.GetProperty("unit");
            Keys(u, "unit", new[] { "bus" }, new[] { "name" });
            int unitBus = Int(u.GetProperty("bus"), "unit.bus");
            string? unitName = u.TryGetProperty("name", out JsonElement un) ? Str(un, "unit.name") : null;
            var matches = Enumerable.Range(0, model.Generators.Count)
                .Where(k => model.Generators[k].Bus == unitBus && (unitName is null || model.Generators[k].Name == unitName)).ToList();
            if (matches.Count != 1)
                throw new InvalidDataException(matches.Count == 0 ? $"unit: no in-service generator at bus {unitBus}{(unitName is null ? "" : $" named '{unitName}'")}."
                    : $"unit: {matches.Count} generators at bus {unitBus}; give unit.name.");

            ModuleType mt = Str(r, "moduleType") switch
            {
                "B" => ModuleType.B, "C" => ModuleType.C, "D" => ModuleType.D,
                _ => throw new InvalidDataException("moduleType must be B, C or D."),
            };
            var request = new GridCodeRequest
            {
                Label = label, Code = Str(r, "code"), ModuleType = mt, Technology = Str(r, "technology"),
                ConnectionKv = Num(r.GetProperty("connectionKv"), "connectionKv"), Model = model, Data = data, UnitIndex = matches[0],
                PccBus = Int(r.GetProperty("pccBus"), "pccBus"),
                PlantBuses = r.TryGetProperty("plantBuses", out JsonElement pb) ? Arr(pb, "plantBuses").Select((e, i) => Int(e, $"plantBuses[{i}]")).ToArray() : Array.Empty<int>(),
                ExtendedClearingTime = r.TryGetProperty("extendedClearingTime", out JsonElement ec) && Bool(ec, "extendedClearingTime"),
                RfgProfile = r.TryGetProperty("frtProfile", out JsonElement fp) ? Profile(fp) : null,
                Area = r.TryGetProperty("area", out JsonElement ar) ? Str(ar, "area") : null,
                FsmMode = r.TryGetProperty("fsmMode", out JsonElement fm) && Bool(fm, "fsmMode"),
                StepSeconds = r.TryGetProperty("stepSeconds", out JsonElement st) ? Positive(st, "stepSeconds") : 0.01,
                Checks = Arr(r.GetProperty("checks"), "checks").Select((c, i) => Check(c, $"checks[{i}]")).ToArray(),
            };
            // RC5 WP22: the exciters are built only for avr-step, the one check that reads them.
            if (dynSet is not null && request.Checks.Any(c => c is AvrStepRequest))
                request = request with { Exciters = Enumerable.Range(0, model.Generators.Count).Select(k => dynSet.For(k)?.Exciter?.Create()).ToArray() };
            GridCodeResult result = GridCodeStudy.Run(request);
            var provenance = new List<KeyValuePair<string, string>>
            {
                new("studySha256", SpecialistStudyCli.Hash(bytes)),
                new("case", Path.GetFileName(casePath)),
                new("requirementsSha256", SpecialistStudyCli.Hash(GridCodeRequirements.Bytes(result.Requirements.Id))),
                new("engine", GridSim.Core.BuildInfo.InformationalVersion),
            };
            // RC5 WP22: the extension requirements' hash, only when an extension check ran (other results are unchanged).
            if (request.Checks.Any(c => ExtCheckIds.Contains(c.Id, StringComparer.Ordinal)))
                provenance.Insert(provenance.Count - 1, new("requirementsExtSha256", SpecialistStudyCli.Hash(GridCodeExtRequirements.Bytes(GridCodeExtRequirements.GbId))));
            if (dynName is not null) provenance.Insert(2, new("dynData", dynName + " sha256 " + SpecialistStudyCli.Hash(dynBytes!)));
            string json = GridCodeStudy.Write(result, provenance);
            if (outFull is not null)
            {
                temporary = outFull + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
                File.Move(temporary, outFull, overwrite: false);
                temporary = null;
            }
            CliResultFromJson.Fill(json);   // RC5 WP11: the shared --json / --csv result is this very document (nothing when not asked for)
            Console.WriteLine(json);
            return result.Verdict == GridSim.Core.Harmonics.Planning.RuleSetEngine.Compliant ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Console.Error.WriteLine(Verb + ": " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The machine data of every generator from a bound dynamics file, with its WECC, FACTS, load and device models
    /// as the RMS study verbs attach them. A generator the file does not bind is refused by name.</summary>
    private static IReadOnlyList<SubtransientInit.MachineData> MachineData(GridModel model, MachineDynamicsSet dyn)
    {
        var list = new List<SubtransientInit.MachineData>(model.Generators.Count);
        for (int k = 0; k < model.Generators.Count; k++)
            list.Add((dyn.For(k) ?? throw new InvalidDataException($"dynData does not bind generator {k} at bus {model.Generators[k].Bus}; every in-service generator needs its data.")).Data);
        foreach (var kv in dyn.Renewables) list[kv.Key] = list[kv.Key] with { Renewable = kv.Value };
        foreach (var kv in dyn.Facts) list[kv.Key] = list[kv.Key] with { Facts = kv.Value };
        if (dyn.DynamicLoads.Count > 0 || dyn.NetworkDevices.Count > 0) return new RmsLoadedMachineData(list, dyn.DynamicLoads, dyn.NetworkDevices);
        return list;
    }

    private static FrtProfile Profile(JsonElement e)
    {
        Keys(e, "frtProfile", new[] { "uret", "uclear", "urec1", "urec2", "tclear", "trec1", "trec2", "trec3" }, Array.Empty<string>());
        double N(string k) => Num(e.GetProperty(k), "frtProfile." + k);
        try { return new FrtProfile(N("uret"), N("uclear"), N("urec1"), N("urec2"), N("tclear"), N("trec1"), N("trec2"), N("trec3"), "frtProfile"); }
        catch (ArgumentException ex) { throw new InvalidDataException(ex.Message); }
    }

    private static GridCodeCheckRequest Check(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out JsonElement idEl)) throw new InvalidDataException($"{what} needs an id.");
        string id = Str(idEl, what + ".id");
        double? O(string k) => e.TryGetProperty(k, out JsonElement v) ? Num(v, $"{what}.{k}") : null;
        string? S(string k) => e.TryGetProperty(k, out JsonElement v) ? Str(v, $"{what}.{k}") : null;
        switch (id)
        {
            case "frt-envelope":
            case "frt-recovery":
            {
                Keys(e, what, new[] { "id" }, new[] { "fault", "durationSeconds" });
                FrtFault fault = e.TryGetProperty("fault", out JsonElement f) ? Fault(f, what + ".fault") : new FrtFault();
                return id == "frt-envelope" ? new FrtEnvelopeRequest(fault, O("durationSeconds")) : new FrtRecoveryRequest(fault, O("durationSeconds"));
            }
            case "frt-retained-voltage":
                Keys(e, what, new[] { "id" }, Array.Empty<string>());
                return new FrtRetainedVoltageRequest();
            case "frt-uv-settings":
                Keys(e, what, new[] { "id", "stages" }, Array.Empty<string>());
                return new FrtUvSettingsRequest(Stages(e.GetProperty("stages"), what + ".stages"));
            case "frequency-relays":
                Keys(e, what, new[] { "id", "stages" }, Array.Empty<string>());
                return new FrequencyRelaysRequest(Stages(e.GetProperty("stages"), what + ".stages"));
            case "rocof-settings":
                Keys(e, what, new[] { "id", "stages" }, new[] { "limitHzPerSecond", "windowSeconds", "source" });
                return new RocofSettingsRequest(Stages(e.GetProperty("stages"), what + ".stages"), O("limitHzPerSecond"), O("windowSeconds"), S("source"));
            case "rocof-event":
                Keys(e, what, new[] { "id", "events", "durationSeconds" }, new[] { "limitHzPerSecond", "windowSeconds", "source" });
                return new RocofEventRequest(Events(e.GetProperty("events"), what + ".events"), O("durationSeconds")!.Value, O("limitHzPerSecond"), O("windowSeconds"), S("source"));
            case "q-capability":
            {
                Keys(e, what, new[] { "id" }, new[] { "pmaxMw", "qmaxMvar", "qminMvar", "uqProfile" });
                UqProfile? up = null;
                if (e.TryGetProperty("uqProfile", out JsonElement upe))
                {
                    Keys(upe, what + ".uqProfile", new[] { "citation", "corners" }, Array.Empty<string>());
                    string citation = Str(upe.GetProperty("citation"), what + ".uqProfile.citation");
                    if (string.IsNullOrWhiteSpace(citation)) throw new InvalidDataException($"{what}.uqProfile.citation must not be empty.");
                    var corners = Arr(upe.GetProperty("corners"), what + ".uqProfile.corners").Select((c, i) =>
                    {
                        string w = $"{what}.uqProfile.corners[{i}]";
                        Keys(c, w, new[] { "voltagePu", "qMinPerPmax", "qMaxPerPmax" }, Array.Empty<string>());
                        return new UqCorner(Num(c.GetProperty("voltagePu"), w + ".voltagePu"), Num(c.GetProperty("qMinPerPmax"), w + ".qMinPerPmax"), Num(c.GetProperty("qMaxPerPmax"), w + ".qMaxPerPmax"));
                    }).ToArray();
                    up = new UqProfile(corners, citation);
                }
                return new QCapabilityRequest(O("pmaxMw"), O("qmaxMvar"), O("qminMvar"), up);
            }
            case "lfsm-o":
            case "lfsm-u":
            {
                Keys(e, what, new[] { "id", "frequencyHz" }, new[] { "tEndSeconds", "stepSeconds", "thresholdHz", "droop", "pref", "pmaxMw", "pm0Pu", "events" });
                IReadOnlyList<RmsEvent>? events = e.TryGetProperty("events", out JsonElement ev) ? Events(ev, what + ".events") : null;
                return new LfsmRequest(id == "lfsm-o", O("frequencyHz")!.Value, O("tEndSeconds") ?? 10.0, O("stepSeconds") ?? 0.01, O("thresholdHz"), O("droop"), S("pref"),
                    O("pmaxMw"), O("pm0Pu"), events);
            }
            case "fsm-step":
            {
                Keys(e, what, new[] { "id", "frequencyHz" }, new[] { "t1Seconds", "t2Seconds", "dp1PerPmax", "withInertia", "tEndSeconds", "stepSeconds", "pmaxMw", "pm0Pu", "insensitivityHz", "deadbandHz", "droop" });
                bool inertia = !e.TryGetProperty("withInertia", out JsonElement wi) || Bool(wi, what + ".withInertia");
                return new FsmStepRequest(O("frequencyHz")!.Value, O("t1Seconds"), O("t2Seconds"), O("dp1PerPmax"), inertia, O("tEndSeconds"), O("stepSeconds") ?? 0.01,
                    O("pmaxMw"), O("pm0Pu"), O("insensitivityHz"), O("deadbandHz"), O("droop"));
            }
            case "ffci-envelope":
            {
                Keys(e, what, new[] { "id" }, new[] { "fault", "durationSeconds" });
                return new FfciRequest(e.TryGetProperty("fault", out JsonElement f) ? Fault(f, what + ".fault") : new FrtFault(), O("durationSeconds"));
            }
            case "frt-long":
            {
                Keys(e, what, new[] { "id" }, new[] { "durationsSeconds", "stageSeconds", "afterSeconds", "maxPasses", "bus", "atSeconds" });
                IReadOnlyList<double>? durations = e.TryGetProperty("durationsSeconds", out JsonElement ds)
                    ? Arr(ds, what + ".durationsSeconds").Select((x, i) => Positive(x, $"{what}.durationsSeconds[{i}]")).ToArray() : null;
                return new LongFaultRequest(durations, e.TryGetProperty("stageSeconds", out JsonElement ss) ? Positive(ss, what + ".stageSeconds") : 0.05,
                    e.TryGetProperty("afterSeconds", out JsonElement af) ? Positive(af, what + ".afterSeconds") : 1.0,
                    e.TryGetProperty("maxPasses", out JsonElement mp) ? Int(mp, what + ".maxPasses") : 8,
                    e.TryGetProperty("bus", out JsonElement b) ? Int(b, what + ".bus") : null,
                    e.TryGetProperty("atSeconds", out JsonElement at) ? Positive(at, what + ".atSeconds") : 0.1);
            }
            case "avr-step":
            {
                Keys(e, what, new[] { "id" }, new[] { "stepSeconds", "durationSeconds" });
                return new AvrStepRequest(e.TryGetProperty("stepSeconds", out JsonElement st) ? Positive(st, what + ".stepSeconds") : null,
                    e.TryGetProperty("durationSeconds", out JsonElement du) ? Positive(du, what + ".durationSeconds") : 10.0);
            }
            case "vctrl-step":
            {
                Keys(e, what, new[] { "id", "events", "durationSeconds" }, new[] { "leadToLag", "qmaxMvar", "qminMvar" });
                bool leadToLag = e.TryGetProperty("leadToLag", out JsonElement ll) && Bool(ll, what + ".leadToLag");
                return new VctrlStepRequest(Events(e.GetProperty("events"), what + ".events"), Positive(e.GetProperty("durationSeconds"), what + ".durationSeconds"), leadToLag,
                    O("qmaxMvar"), O("qminMvar"));
            }
            default:
                throw new InvalidDataException($"{what}: unknown check '{id}' (checks: {string.Join(", ", CheckIds)}).");
        }
    }

    private static FrtFault Fault(JsonElement e, string what)
    {
        Keys(e, what, Array.Empty<string>(), new[] { "type", "resistancePu", "reactancePu", "bus", "atSeconds" });
        FaultType type = !e.TryGetProperty("type", out JsonElement t) ? FaultType.ThreePhase : Str(t, what + ".type") switch
        {
            "3ph" => FaultType.ThreePhase, "slg" => FaultType.SingleLineToGround, "ll" => FaultType.LineToLine, "llg" => FaultType.DoubleLineToGround,
            _ => throw new InvalidDataException($"{what}.type must be 3ph, slg, ll or llg."),
        };
        double? O(string k) => e.TryGetProperty(k, out JsonElement v) ? Num(v, $"{what}.{k}") : null;
        int? bus = e.TryGetProperty("bus", out JsonElement b) ? Int(b, what + ".bus") : null;
        return new FrtFault(type, O("resistancePu"), O("reactancePu"), bus, O("atSeconds") ?? 0.1);
    }

    private static IReadOnlyList<GridCodeRelayStage> Stages(JsonElement e, string what) =>
        Arr(e, what).Select((s, i) =>
        {
            string w = $"{what}[{i}]";
            Keys(s, w, new[] { "device", "function", "pickup", "delaySeconds" }, new[] { "windowSeconds" });
            string f = Str(s.GetProperty("function"), w + ".function");
            if (f is not ("27" or "59" or "81U" or "81O" or "81R")) throw new InvalidDataException($"{w}.function must be 27, 59, 81U, 81O or 81R.");
            double delay = Num(s.GetProperty("delaySeconds"), w + ".delaySeconds");
            if (delay < 0.0) throw new InvalidDataException($"{w}.delaySeconds must be 0 or more.");
            return new GridCodeRelayStage(Str(s.GetProperty("device"), w + ".device"), f, Num(s.GetProperty("pickup"), w + ".pickup"), delay,
                s.TryGetProperty("windowSeconds", out JsonElement ws) ? Positive(ws, w + ".windowSeconds") : null);
        }).ToArray();

    private static IReadOnlyList<RmsEvent> Events(JsonElement e, string what) =>
        Arr(e, what).Select((v, i) =>
        {
            string w = $"{what}[{i}]";
            string kind = v.TryGetProperty("kind", out JsonElement k) ? Str(k, w + ".kind") : throw new InvalidDataException($"{w} needs a kind.");
            switch (kind)
            {
                case "generatorTrip":
                    Keys(v, w, new[] { "kind", "atSeconds", "generatorIndex" }, Array.Empty<string>());
                    return (RmsEvent)new RmsGeneratorTrip(Num(v.GetProperty("atSeconds"), w + ".atSeconds"), Int(v.GetProperty("generatorIndex"), w + ".generatorIndex"));
                case "loadStep":
                    Keys(v, w, new[] { "kind", "atSeconds", "bus", "deltaPMw", "deltaQMvar" }, Array.Empty<string>());
                    return new RmsLoadStep(Num(v.GetProperty("atSeconds"), w + ".atSeconds"), Int(v.GetProperty("bus"), w + ".bus"),
                        Num(v.GetProperty("deltaPMw"), w + ".deltaPMw"), Num(v.GetProperty("deltaQMvar"), w + ".deltaQMvar"));
                default:
                    throw new InvalidDataException($"{w}.kind must be generatorTrip or loadStep.");
            }
        }).ToArray();

    // ------------------------------------------------------------------------------------------------ strict JSON

    private static void Keys(JsonElement e, string what, string[] required, string[] optional)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{what} must be an object.");
        foreach (string k in required) if (!e.TryGetProperty(k, out _)) throw new InvalidDataException($"{what}: '{k}' is required.");
        foreach (JsonProperty p in e.EnumerateObject())
            if (!required.Contains(p.Name, StringComparer.Ordinal) && !optional.Contains(p.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"{what}: unknown key '{p.Name}'.");
    }

    private static string Str(JsonElement e, string key)
    {
        JsonElement v = e;
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (!e.TryGetProperty(key, out v)) throw new InvalidDataException($"'{key}' is required.");
        }
        if (v.ValueKind != JsonValueKind.String) throw new InvalidDataException($"'{key}' must be a string.");
        return v.GetString()!;
    }

    private static double Num(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Number || !double.IsFinite(e.GetDouble())) throw new InvalidDataException($"{what} must be a finite number.");
        return e.GetDouble();
    }

    private static double Positive(JsonElement e, string what)
    {
        double v = Num(e, what);
        if (!(v > 0.0)) throw new InvalidDataException($"{what} must be above 0.");
        return v;
    }

    private static int Int(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int v)) throw new InvalidDataException($"{what} must be an integer.");
        return v;
    }

    private static bool Bool(JsonElement e, string what) => e.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException($"{what} must be true or false."),
    };

    private static List<JsonElement> Arr(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{what} must be an array.");
        return e.EnumerateArray().ToList();
    }

}
