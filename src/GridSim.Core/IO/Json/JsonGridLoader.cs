using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dispatch;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Json;

/// <summary>
/// Builds a <see cref="GridModel"/> from a folder of human-editable JSON files:
/// <c>system.json</c>, <c>buses.json</c>, <c>generation.json</c>, <c>demand.json</c>,
/// <c>transmission.json</c>, <c>transformer.json</c>, <c>onloadtapchanger.json</c>.
/// Only system/buses/generation are required; the rest are optional.
///
/// Generation is then dispatched from its capacities (see <see cref="Dispatcher"/>)
/// so the model arrives in a balanced, ready-to-solve state.
/// </summary>
public static class JsonGridLoader
{
    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated JsonGridLoaderJsonContext (below) so no runtime reflection is needed.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly JsonGridLoaderJsonContext Json = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // R268: an unknown or misspelt key is an error, not a silent no-op. Without this a hand-edited
        // 'rating_MVA' was dropped and the DTO default applied (RatingMva 0 = an unrated branch that is never
        // overloaded, so N-1 reported "secure" everywhere; a misspelt baseKv became 400 kV). Comments are still
        // allowed for annotation, and key case is still free.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        // A key given twice (or twice in different case, since key case is free) is refused: the last one used to
        // win silently, so a second "ratingMva" replaced the first without a word.
        AllowDuplicateProperties = false,
    });

    // The same options object, under its historical name. Phase 0.1 keeps a field called Opt because tests read it
    // by reflection (the v1 projector reads a case with "the loader's own options"); it is now the options the
    // source-generated context above is bound to, so a read through it is the loader's read exactly.
    internal static readonly JsonSerializerOptions Opt = Json.Options;

    public static GridModel LoadDirectory(string dir, bool dispatch = true)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Case directory not found: {dir}");

        // N065 (October 2026): the files are read into the shared DTO bundle and built by Build, which the JSON v2 case
        // loader (V2CaseLoader) shares, so a v2 case and its v1 equivalent go through one model builder. The files are
        // read in the same order as before, and boundaries.json still only after the branches are built (Boundaries is
        // read lazily), so even the order in which problems are reported is unchanged.
        var dtos = new CaseDtos
        {
            System = ReadOne(dir, "system.json", Json.SystemDto),
            Buses = ReadArray(dir, "buses.json", Json.ListBusDto),
            Generators = ReadArray(dir, "generation.json", Json.ListGenDto),
            Demands = ReadArray(dir, "demand.json", Json.ListDemandDto),
            Lines = ReadArray(dir, "transmission.json", Json.ListLineDto),
            Transformers = ReadArray(dir, "transformer.json", Json.ListTransformerDto),
            Oltcs = ReadArray(dir, "onloadtapchanger.json", Json.ListOltcDto),
            AssetClasses = ReadArray(dir, "asset-classes.json", Json.ListAssetClassDto),
            AssetBindings = ReadArray(dir, "asset-bindings.json", Json.ListAssetBindingDto),
            Protections = ReadArray(dir, "protections.json", Json.ListProtectionInstanceDto),
            Boundaries = () => ReadArray(dir, "boundaries.json", Json.ListBoundaryDto),
        };
        return Build(dtos, Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)), dispatch);
    }

    /// <summary>
    /// N065 (October 2026): build the model from a case's v1 element DTOs. This is the body LoadDirectory always had,
    /// moved here unchanged so that the JSON v2 case loader (<see cref="V2CaseLoader"/>) builds through exactly the same
    /// code: a v2 case whose elements equal a v1 case's DTOs gives the same model, bit for bit. Problems name the v1
    /// file each rule belongs to (buses.json, generation.json, ...); the v2 loader says so in its own message.
    /// <paramref name="fallbackName"/> is the model name when the system block gives none (v1: the folder name).
    /// </summary>
    internal static GridModel Build(CaseDtos dtos, string fallbackName, bool dispatch)
    {
        ArgumentNullException.ThrowIfNull(dtos);
        SystemDto sys = dtos.System ?? new SystemDto();
        List<BusDto> busDtos = dtos.Buses;
        List<GenDto> genDtos = dtos.Generators;
        List<DemandDto> demDtos = dtos.Demands;
        List<LineDto> lineDtos = dtos.Lines;
        List<TransformerDto> txDtos = dtos.Transformers;
        List<OltcDto> oltcDtos = dtos.Oltcs;
        List<AssetClassDto> assetClassDtos = dtos.AssetClasses;
        List<AssetBindingDto> assetBindingDtos = dtos.AssetBindings;
        List<ProtectionInstanceDto> protectionDtos = dtos.Protections;

        if (busDtos.Count == 0) throw new InvalidDataException("buses.json is empty or missing.");

        // R179 / Q13 (October 2026): baseKv is required on every bus. It used to default silently to 400 kV,
        // which scales every kA figure of a fault study, so a forgotten key gave wrong currents with no warning.
        // Every bus without it is named (up to twenty), so a hand-edited file can be fixed in one go.
        List<string> noBaseKv = busDtos.Where(b => b.BaseKv is null)
            .Select(b => b.Name is { Length: > 0 } nm ? $"{b.Id} ('{nm}')" : b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        if (noBaseKv.Count > 0)
            throw new InvalidDataException(
                $"buses.json: {noBaseKv.Count} bus(es) have no baseKv, which is required (the base voltage in kV; it no " +
                $"longer defaults to 400 kV): {string.Join(", ", noBaseKv.Take(20))}{(noBaseKv.Count > 20 ? ", ..." : "")}.");

        // Resolve bus types: file default, raised to PV by any generator, set to Slack by the slack generator.
        var type = new Dictionary<int, BusType>();
        foreach (BusDto b in busDtos) type[b.Id] = ParseBusType(b);

        // Multiple slack generators are allowed  -  a multi-island model (e.g. a whole
        // DNO footprint materialised from the GDA archive) needs one reference per
        // island. A slack may also come straight from buses.json's type field.
        foreach (GenDto g in genDtos)
        {
            if (!type.ContainsKey(g.Bus)) throw new InvalidDataException($"Generator '{g.Name}' references unknown bus {g.Bus}.");
            // N096 / N098 (October 2026): the role must be one of the three words the loader understands. A JSON null
            // used to reach g.Role.Equals and crash with a NullReferenceException (a defect report instead of a named
            // refusal), and any other word ("slak", "PV1") was silently read as pv, so a misspelt slack quietly
            // became a voltage-controlled unit. Checked for every unit, in service or not, so a bad file is refused
            // whatever the status column says.
            CheckGeneratorRole(g);
            // F16: an out-of-service unit holds no voltage, so it neither raises its bus to PV nor makes it the slack
            // (a case whose only slack unit is out of service is then refused below: "No slack bus").
            if (!g.InService) continue;
            if (g.Role.Equals("slack", StringComparison.OrdinalIgnoreCase)) type[g.Bus] = BusType.Slack;
            else if (g.Role.Equals("pq", StringComparison.OrdinalIgnoreCase)) { /* fixed injection  -  bus keeps its type */ }
            else if (type[g.Bus] != BusType.Slack) type[g.Bus] = BusType.Pv;
        }
        if (!type.Values.Any(t => t == BusType.Slack))
            throw new InvalidDataException("No slack bus. Mark a generator role \"slack\" or a bus type \"slack\".");

        // Aggregate demand onto buses.
        var pd = new Dictionary<int, double>();
        var qd = new Dictionary<int, double>();
        foreach (DemandDto d in demDtos)
        {
            // Mirror the generator check above: a demand row on an unknown bus
            // used to be silently discarded (MW vanished from the case).
            if (!type.ContainsKey(d.Bus))
                throw new InvalidDataException($"Demand '{d.Name}' references unknown bus {d.Bus}.");
            // A power factor outside [MinPowerFactor, 1] is refused, not clamped: a negative (leading) or zero factor
            // used to become 0.01, a Q of about 100 x P. A leading load is given with a negative qMvar.
            if (!(d.PowerFactor >= JsonDefaults.MinPowerFactor && d.PowerFactor <= 1.0))
                throw new InvalidDataException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Demand '{d.Name}' on bus {d.Bus} has power factor {d.PowerFactor}; it must be between {JsonDefaults.MinPowerFactor} and 1 " +
                    $"(give a leading load as a negative qMvar)."));
            pd[d.Bus] = pd.GetValueOrDefault(d.Bus) + d.PMw;
            double q = d.QMvar ?? d.PMw * DetMath.Tan(DetMath.Acos(d.PowerFactor));
            qd[d.Bus] = qd.GetValueOrDefault(d.Bus) + q;
        }

        var buses = busDtos.Select(b => new Bus(
            b.Id, type[b.Id],
            Pd: pd.GetValueOrDefault(b.Id), Qd: qd.GetValueOrDefault(b.Id),
            Gs: b.Gs, Bs: b.Bs, BaseKv: b.BaseKv!.Value, Vm: b.Vm, VaDeg: b.VaDeg, Name: b.Name,
            X: b.X, Y: b.Y, Zip: ParseZip(b), VminPu: b.VminPu, VmaxPu: b.VmaxPu)).ToList();
        CheckBusVoltageLimits(busDtos);

        CheckGeneratorLimits(genDtos);
        var gens = genDtos.Select(g =>
        {
            double cap = g.CapacityMva;
            double output = g.OutputMw ?? cap * (g.CapacityFactor ?? Dispatcher.DefaultCapacityFactor(g.Type));
            // F24: pMinMw / pMaxMw / qOutputMvar, when written, replace the historical 0 / capacityMva / 0.
            return new Generator(
                g.Bus, Pg: output, Qg: g.QOutputMvar ?? 0, Vg: g.VoltageSetpoint,
                Qmin: g.QMinMvar ?? -cap, Qmax: g.QMaxMvar ?? cap,
                Pmin: g.PMinMw ?? 0, Pmax: g.PMaxMw ?? cap, MBase: cap, H: g.InertiaH,
                Name: g.Name, Type: g.Type, DynamicsClass: g.DynamicsClass,
                ConverterInterfaced: g.ConverterInterfaced, ConverterFaultCurrentPu: g.ConverterFaultCurrentPu,
                PowerFlowOnly: g.PowerFlowOnly, UnitTransformerXPu: g.UnitTransformerXPu, GridForming: g.GridForming,
                InService: g.InService);
        }).ToList();

        var branches = new List<Branch>();
        foreach (LineDto l in lineDtos)
        {
            // A line has at least one circuit. 0 or a negative count is not "one circuit": it used to load as one
            // circuit in service. An out-of-service line is said with inService, not circuits 0.
            if (!(l.TapRatio > 0) || !double.IsFinite(l.TapRatio))
                throw new InvalidDataException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Line {l.From}-{l.To}{(l.Name is null ? "" : $" ('{l.Name}')")} has tapRatio {l.TapRatio}; it must be a finite number above 0."));
            if (l.Circuits < 1)
                throw new InvalidDataException($"Line {l.From}-{l.To}{(l.Name is null ? "" : $" ('{l.Name}')")} has circuits {l.Circuits}; it must be 1 or more.");
            int c = l.Circuits;
            branches.Add(new Branch(l.From, l.To, l.RPu / c, l.XPu / c, l.BPu * c,
                TapRatio: l.TapRatio, PhaseShiftDeg: l.PhaseShiftDeg,   // F24: 1 and 0 when absent, the Branch defaults
                Name: l.Name, RateAMva: l.RatingMva * c, IsTransformer: l.IsTransformer, DynamicsClass: l.DynamicsClass,
                LengthKm: l.LengthKm, Equipment: l.Equipment, R0: l.RPu0 / c, X0: l.XPu0 / c,
                RateBMva: l.RatingBMva * c, RateCMva: l.RatingCMva * c));   // F20: 0 when absent, the Branch default
        }
        foreach (TransformerDto t in txDtos)
        {
            if (!(t.TapRatio > 0) || !double.IsFinite(t.TapRatio))
                throw new InvalidDataException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Transformer {t.From}-{t.To}{(t.Name is null ? "" : $" ('{t.Name}')")} has tapRatio {t.TapRatio}; it must be a finite number above 0."));
            branches.Add(new Branch(t.From, t.To, t.RPu, t.XPu, 0, t.TapRatio, t.PhaseShiftDeg,
                Name: t.Name, IsTransformer: true, RateAMva: t.RatingMva, DynamicsClass: t.DynamicsClass,
                RateBMva: t.RatingBMva, RateCMva: t.RatingCMva));   // N119: 0 when absent, the Branch default
        }

        // An OLTC must name its transformer (an empty name loaded with only a warning and never regulated) and have a
        // usable tap range, step and deadband.
        foreach (OltcDto o in oltcDtos)
        {
            string who = string.IsNullOrWhiteSpace(o.Transformer) ? $"An OLTC controlling bus {o.ControlsBus}" : $"OLTC on '{o.Transformer}'";
            if (string.IsNullOrWhiteSpace(o.Transformer))
                throw new InvalidDataException($"{who} names no transformer.");
            if (!(o.MinTap > 0 && o.MinTap <= o.MaxTap) || !double.IsFinite(o.MaxTap))
                throw new InvalidDataException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{who} has minTap {o.MinTap} and maxTap {o.MaxTap}; it needs 0 < minTap <= maxTap."));
            if (!(o.StepPu > 0) || !double.IsFinite(o.StepPu))
                throw new InvalidDataException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{who} has stepPu {o.StepPu}; it must be a finite number above 0."));
            if (!(o.DeadbandPu >= 0) || !double.IsFinite(o.DeadbandPu))
                throw new InvalidDataException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{who} has deadbandPu {o.DeadbandPu}; it must be a finite number, 0 or above."));
            if (!(o.TargetVpu > 0) || !double.IsFinite(o.TargetVpu))
                throw new InvalidDataException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{who} has targetVpu {o.TargetVpu}; it must be a finite number above 0."));
        }

        var oltcs = oltcDtos.Select(o => new Oltc(
            o.Transformer, o.ControlsBus, o.TargetVpu, o.MinTap, o.MaxTap, o.StepPu, o.DeadbandPu)).ToList();

        List<BoundaryDto> boundaryDtos = dtos.Boundaries();
        // A boundary branch counts with direction +1 or -1; 0 zeroed its contribution and 7 multiplied it, silently.
        foreach (BoundaryDto b in boundaryDtos)
            foreach (BoundaryBranchDto x in b.Branches ?? new List<BoundaryBranchDto>())
                if (x.Direction is not (1 or -1))
                    throw new InvalidDataException($"Boundary '{b.Name}': branch '{x.Branch}' has direction {x.Direction}; it must be 1 or -1.");
        var boundaries = boundaryDtos.Select(b => new Boundary(
            b.Name, b.LimitMw,
            (b.Branches ?? new List<BoundaryBranchDto>())
                .Select(x => new BoundaryBranch(x.Branch, x.Direction)).ToList())).ToList();
        CheckBoundaryBranches(boundaries, branches);

        AssetDynamicsModel? assetDynamics = BuildAssetDynamics(assetClassDtos, assetBindingDtos);
        Assets.Protection.ProtectionModel? protections = BuildProtections(assetClassDtos, protectionDtos);

        var model = new GridModel
        {
            Name = sys.Name ?? fallbackName,
            BaseMva = sys.BaseMva,
            FrequencyHz = sys.FrequencyHz,
            Buses = buses,
            Generators = gens,
            Branches = branches,
            Oltcs = oltcs,
            Boundaries = boundaries,
            AssetDynamics = assetDynamics,
            Protections = protections,
        };

        ThrowOnValidationErrors(model);
        // N015 (R232 follow-up): check every placed protective device against the network it was loaded with (a
        // recloser's target branch, monitored buses, the bound class's kind), throwing with every problem listed.
        // ValidateAgainst existed since 1.95.033 but nothing called it, so a misplaced device loaded silently.
        model.Protections?.ValidateAgainst(model);
        // F24: system.json "dispatch": false keeps the written outputs (a converted case's solved operating point).
        // The caller's dispatch: false still wins; absent, the case is dispatched exactly as before.
        return dispatch && (sys.Dispatch ?? true) ? Dispatcher.Balance(model) : model;
    }

    /// <summary>
    /// N097 (October 2026): every branch a boundary in boundaries.json names must be exactly one branch of the case.
    /// The boundary flow is the signed sum of its branches' flows looked up BY NAME, and that lookup used to skip a name
    /// that matched nothing (a typo, or a branch renamed in transmission.json) and to take the last of several branches
    /// sharing a name, so a boundary silently measured part of its cut, or the wrong circuit, and its loading and the
    /// N-1 boundary check were wrong with no warning. Both are now refused, naming boundaries.json, the boundary, the
    /// branch name and (for a shared name) how many branches carry it; every problem is listed (up to twenty), so a file
    /// can be fixed in one go. Branch names are compared exactly (ordinal), as the flow lookup compares them. Every
    /// bundled case's boundaries name each branch exactly once (checked), so none of them changes.
    /// </summary>
    internal static void CheckBoundaryBranches(IReadOnlyList<Boundary> boundaries, IReadOnlyList<Branch> branches)
    {
        // N138: the rule itself now lives on Boundary, shared with GridModel.Validate and the boundary readers.
        List<string> bad = Boundary.BranchNameProblems(boundaries, branches);
        if (bad.Count > 0)
            throw new InvalidDataException(
                $"boundaries.json: {bad.Count} boundary branch name(s) do not resolve to exactly one branch: " +
                $"{string.Join("; ", bad.Take(20))}{(bad.Count > 20 ? "; ..." : "")}.");
    }

    /// <summary>
    /// F24: refuse generator limits that cannot be meant, naming generation.json and each unit (up to twenty): a
    /// minimum reactive limit of +Infinity, a maximum reactive limit of -Infinity, a maximum real output of -Infinity,
    /// or a non-finite minimum real output. ("Infinity" on qMaxMvar / pMaxMw and "-Infinity" on qMinMvar are the
    /// unlimited values MATPOWER allows; the converter accepts no other string.)
    /// </summary>
    internal static void CheckGeneratorLimits(List<GenDto> gens)
    {
        var bad = new List<string>();
        foreach (GenDto g in gens)
        {
            string who = g.Name is { Length: > 0 } nm ? $"'{nm}' (bus {g.Bus})" : $"unnamed unit on bus {g.Bus}";
            if (g.QMinMvar is double qmin && double.IsPositiveInfinity(qmin)) bad.Add($"{who} qMinMvar is +Infinity");
            if (g.QMaxMvar is double qmax && double.IsNegativeInfinity(qmax)) bad.Add($"{who} qMaxMvar is -Infinity");
            if (g.PMaxMw is double pmax && double.IsNegativeInfinity(pmax)) bad.Add($"{who} pMaxMw is -Infinity");
            if (g.PMinMw is double pmin && !double.IsFinite(pmin)) bad.Add($"{who} pMinMw is not finite");
        }
        if (bad.Count > 0)
            throw new InvalidDataException(
                $"generation.json: {bad.Count} impossible generator limit(s): {string.Join(", ", bad.Take(20))}{(bad.Count > 20 ? ", ..." : "")}.");
    }

    /// <summary>
    /// Build the authored per-asset dynamics bundle from the two optional files, or null when the case
    /// ships no classes. Parses/validates the class kinds and provenance tags, and enforces the honesty
    /// rule ([VERIFY] on every CuratedPlaceholder) - all fail-loud at load.
    /// </summary>
    private static AssetDynamicsModel? BuildAssetDynamics(
        List<AssetClassDto> classDtos, List<AssetBindingDto> bindingDtos)
    {
        if (classDtos.Count == 0)
        {
            if (bindingDtos.Count > 0)
                throw new InvalidDataException("asset-bindings.json is present but asset-classes.json defines no classes.");
            return null;
        }

        AssetClassLibrary library = BuildLibrary(classDtos);

        var bindings = new AssetBindingSet(
            bindingDtos.Select(b => new AssetBinding(b.MatchType, b.Class)));

        // Fail loud on a rule that points at a class that does not exist.
        foreach (AssetBindingDto b in bindingDtos)
            if (!library.Contains(b.Class))
                throw new InvalidDataException($"asset-bindings.json rule for '{b.MatchType}' references unknown class '{b.Class}'.");

        return new AssetDynamicsModel(library, bindings);
    }

    /// <summary>Parse + validate the shared asset-class library from the class DTOs ([VERIFY] discipline enforced).</summary>
    private static AssetClassLibrary BuildLibrary(List<AssetClassDto> classDtos)
    {
        var classes = classDtos.Select(c =>
        {
            var prov = new Dictionary<string, AssetClassProvenance>(StringComparer.Ordinal);
            foreach ((string key, ProvenanceDto p) in c.Provenance ?? new())
                prov[key] = new AssetClassProvenance(ParseProvenanceTag(p.Tag, c.Name, key), p.Note ?? "");
            return new AssetClass(c.Name, ParseAssetKind(c.Kind, c.Name), c.Mode ?? "",
                c.Params ?? new Dictionary<string, double>(), prov);
        });
        var library = new AssetClassLibrary(classes);
        library.ValidateOrThrow();
        return library;
    }

    /// <summary>
    /// Build the protection bundle from <c>protections.json</c> (placed instances) against the shared class
    /// library. Null when no instances; fail-loud on an instance referencing an unknown or non-protection class.
    /// </summary>
    private static Assets.Protection.ProtectionModel? BuildProtections(
        List<AssetClassDto> classDtos, List<ProtectionInstanceDto> instanceDtos)
    {
        if (instanceDtos.Count == 0) return null;
        if (classDtos.Count == 0)
            throw new InvalidDataException("protections.json is present but asset-classes.json defines no classes.");

        AssetClassLibrary library = BuildLibrary(classDtos);
        var instances = new List<Assets.Protection.ProtectionInstance>(instanceDtos.Count);
        foreach (ProtectionInstanceDto d in instanceDtos)
        {
            AssetClass cls = library.Get(d.Class);   // throws on unknown
            if (cls.Kind != AssetKind.Protection)
                throw new InvalidDataException($"protections.json instance '{d.Name}' references class '{d.Class}' which is kind {cls.Kind}, not protection.");
            instances.Add(new Assets.Protection.ProtectionInstance(d.Name, d.Class, d.MonitorBus, d.CapacityMw, d.TargetBranch));
        }
        return new Assets.Protection.ProtectionModel(library, instances);
    }

    private static AssetKind ParseAssetKind(string s, string className) => s.ToLowerInvariant() switch
    {
        "gridfollowinginverter" or "grid-following" or "gfl" => AssetKind.GridFollowingInverter,
        "gridforminginverter" or "grid-forming" or "gfm" => AssetKind.GridFormingInverter,
        "synchronousmachine" or "machine" => AssetKind.SynchronousMachine,
        "transformer" => AssetKind.Transformer,
        "line" or "cable" => AssetKind.Line,
        "protection" or "relay" or "protective" => AssetKind.Protection,
        _ => throw new InvalidDataException($"asset class '{className}' has unknown kind '{s}'."),
    };

    private static ProvenanceTag ParseProvenanceTag(string s, string className, string key)
        => Enum.TryParse<ProvenanceTag>(s, ignoreCase: true, out ProvenanceTag tag)
            ? tag
            : throw new InvalidDataException($"asset class '{className}' parameter '{key}' has unknown provenance tag '{s}'.");

    /// <summary>How far (absolute) a ZIP coefficient triple may sum from exactly 1 before it is refused. A triple
    /// typed as decimals (0.1 + 0.2 + 0.7) does not sum to 1.0 exactly in binary floating point, so an exact test
    /// would reject honest input; 1e-9 is far below any meaningful load-model error and far above that rounding.</summary>
    internal const double ZipSumTolerance = 1e-9;

    /// <summary>
    /// The bus's optional ZIP (voltage-dependent) load model, validated fail-loud (N014 / R065). Null when the
    /// bus gives none, which is constant power: the historical behaviour, so every case without a zip block
    /// builds exactly the same <see cref="Bus"/> as before. When a zip block is given, all six coefficients must
    /// be present and finite, and each of the P triple (ap, bp, cp) and the Q triple (aq, bq, cq) must sum to 1
    /// (within <see cref="ZipSumTolerance"/>), so the load draws exactly its nominal Pd / Qd at 1.0 pu. Anything
    /// else throws <see cref="InvalidDataException"/> naming buses.json, the bus and the offending value.
    /// </summary>
    internal static ZipLoad? ParseZip(BusDto b)
    {
        if (b.Zip is not BusZipDto z) return null;
        string where = $"buses.json: bus {b.Id}" + (b.Name is null ? "" : $" ('{b.Name}')");

        // Each coefficient must be present (no silent default) and a finite number.
        double Coefficient(double? value, string key)
        {
            if (value is not double v)
                throw new InvalidDataException($"{where}: zip is missing coefficient '{key}' (all of ap, bp, cp, aq, bq, cq are required).");
            if (!double.IsFinite(v))
                throw new InvalidDataException($"{where}: zip coefficient '{key}' is {v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, which is not a finite number.");
            return v;
        }
        double ap = Coefficient(z.Ap, "ap"), bp = Coefficient(z.Bp, "bp"), cp = Coefficient(z.Cp, "cp");
        double aq = Coefficient(z.Aq, "aq"), bq = Coefficient(z.Bq, "bq"), cq = Coefficient(z.Cq, "cq");

        // Each triple must sum to 1, or the bus would not draw its stated Pd / Qd at nominal voltage.
        double pSum = ap + bp + cp;
        if (!(Math.Abs(pSum - 1.0) <= ZipSumTolerance))
            throw new InvalidDataException($"{where}: zip real-power coefficients ap + bp + cp sum to {pSum.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, not 1.");
        double qSum = aq + bq + cq;
        if (!(Math.Abs(qSum - 1.0) <= ZipSumTolerance))
            throw new InvalidDataException($"{where}: zip reactive-power coefficients aq + bq + cq sum to {qSum.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, not 1.");

        return new ZipLoad(Ap: ap, Bp: bp, Cp: cp, Aq: aq, Bq: bq, Cq: cq);
    }

    /// <summary>
    /// F17: refuse unusable per-bus voltage limits, naming buses.json and every offending bus (up to twenty): each
    /// given limit must be a finite voltage above 0 pu, and a bus giving both must not have the lower above the upper
    /// (equal is allowed: a fixed-voltage bus). Absent limits are fine (the global band applies).
    /// </summary>
    internal static void CheckBusVoltageLimits(List<BusDto> buses)
    {
        var bad = new List<string>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (BusDto b in buses)
        {
            string who = b.Name is { Length: > 0 } nm ? $"bus {b.Id} ('{nm}')" : $"bus {b.Id}";
            if (b.VminPu is double lo && !(double.IsFinite(lo) && lo > 0)) bad.Add($"{who} vminPu {lo.ToString("R", inv)}");
            if (b.VmaxPu is double hi && !(double.IsFinite(hi) && hi > 0)) bad.Add($"{who} vmaxPu {hi.ToString("R", inv)}");
            if (b.VminPu is double l && b.VmaxPu is double h && double.IsFinite(l) && double.IsFinite(h) && l > h)
                bad.Add($"{who} vminPu {l.ToString("R", inv)} above vmaxPu {h.ToString("R", inv)}");
        }
        if (bad.Count > 0)
            throw new InvalidDataException(
                $"buses.json: {bad.Count} unusable per-bus voltage limit(s) (each must be a finite voltage above 0 pu, and vminPu not above vmaxPu): " +
                $"{string.Join(", ", bad.Take(20))}{(bad.Count > 20 ? ", ..." : "")}.");
    }

    /// <summary>Fail fast at the entry point on any hard (error-severity) structural fault.</summary>
    private static void ThrowOnValidationErrors(GridModel model)
    {
        var errors = model.Validate()
            .Where(d => d.Severity == ValidationSeverity.Error)
            .Select(d => d.Message)
            .ToList();
        if (errors.Count > 0)
            throw new InvalidDataException("Invalid grid model:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", errors));
    }

    /// <summary>
    /// The bus type written in buses.json. N096 / N098 (October 2026): only the words the v1 schema allows are accepted
    /// (pq; pv and its alias gen; slack and its aliases ref and swing; any letter case, as before). Anything else used
    /// to be read as pq without a word, so a misspelt "slak" quietly lost the reference bus; a JSON null crashed with a
    /// NullReferenceException. Both now throw InvalidDataException naming buses.json, the bus and the value. An ABSENT
    /// key still means pq (the DTO default), exactly as before.
    /// </summary>
    private static BusType ParseBusType(BusDto b)
    {
        string who = b.Name is { Length: > 0 } nm ? $"bus {b.Id} ('{nm}')" : $"bus {b.Id}";
        if (b.Type is null)
            throw new InvalidDataException($"buses.json: {who} has \"type\": null; give pq, pv or slack, or leave the key out (absent means pq).");
        return b.Type.ToLowerInvariant() switch
        {
            "slack" or "ref" or "swing" => BusType.Slack,
            "pv" or "gen" => BusType.Pv,
            "pq" => BusType.Pq,
            _ => throw new InvalidDataException(
                $"buses.json: {who} has unknown type '{b.Type}'; the bus types are pq, pv (alias gen) and slack (aliases ref, swing)."),
        };
    }

    /// <summary>
    /// N096 / N098 (October 2026): refuse a generator role that is null or not one of pv, pq, slack (any letter case),
    /// naming generation.json, the unit and its bus, and the value. An ABSENT key still means pv (the DTO default).
    /// N137: also refuse a null generator <c>type</c> (absent still means ccgt).
    /// </summary>
    internal static void CheckGeneratorRole(GenDto g)
    {
        string who = g.Name is { Length: > 0 } nm ? $"'{nm}' (bus {g.Bus})" : $"unnamed unit on bus {g.Bus}";
        if (g.Role is null)
            throw new InvalidDataException($"generation.json: generator {who} has \"role\": null; give pv, pq or slack, or leave the key out (absent means pv).");
        if (!(g.Role.Equals("pv", StringComparison.OrdinalIgnoreCase) || g.Role.Equals("pq", StringComparison.OrdinalIgnoreCase)
              || g.Role.Equals("slack", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"generation.json: generator {who} has unknown role '{g.Role}'; the roles are pv, pq and slack.");
        // N137: a JSON null in "type" overrode the "ccgt" default with null. Without capacityFactor / outputMw the loader
        // then looked the type up in the default capacity-factor table and threw a bare NullReferenceException
        // ("Object reference not set to an instance of an object", confirmed on a data/uk copy at 1.95.125); with them,
        // the null reached the model, where every fuel reader read it as "unknown". Refused by name, as N098 did for a
        // null bus type or role. An empty string stays allowed (convert writes "" for a MATPOWER unit with no
        // technology, N101 extension), and so does any other string: the type is free text read by the fuel taxonomy.
        if (g.Type is null)
            throw new InvalidDataException($"generation.json: generator {who} has \"type\": null; give the plant type (for example ccgt, wind-onshore, solar), \"\" for none, or leave the key out (absent means ccgt).");
    }

    // Phase 0.1: each reader takes the source-generated metadata for the type it reads (a property of Json, the
    // JsonGridLoaderJsonContext built from the options above), so no case file is read through runtime reflection.
    private static T? ReadOne<T>(string dir, string file, JsonTypeInfo<T> info) where T : class
    {
        string path = Path.Combine(dir, file);
        return File.Exists(path) ? Deserialize(path, file, info) ?? throw NullDocument(file, "an object") : null;
    }

    private static List<T> ReadArray<T>(string dir, string file, JsonTypeInfo<List<T>> info)
    {
        string path = Path.Combine(dir, file);
        if (!File.Exists(path)) return new List<T>();
        // A file that IS present but whose whole content is the JSON literal null used to load as an empty list, so
        // demand.json = null dropped every load and transformer.json = null every transformer, without a word. An
        // absent file still means "none"; an explicit null is a wrong type like any other and is refused by name.
        return Deserialize(path, file, info) ?? throw NullDocument(file, "an array");
    }

    private static InvalidDataException NullDocument(string file, string what) =>
        new($"{file}: the file's content is the JSON literal null; it must be {what}. Delete the file if the case has none.");

    // Deserialise one case file, naming the file in any error (an unknown key, a wrong type, bad JSON) so the
    // author can find it: the serializer's own message names the key and the JSON path but not the file.
    private static T? Deserialize<T>(string path, string file, JsonTypeInfo<T> info)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), info);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{file}: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// N065 (October 2026): the v1 element DTOs of one case, however they were read: from a v1 case folder by
/// <see cref="JsonGridLoader.LoadDirectory"/>, or mapped from a JSON v2 case (manifest plus network files) by
/// <see cref="V2CaseLoader"/>. <see cref="JsonGridLoader.Build"/> turns either into the same <see cref="GridSim.Core.Model.GridModel"/>.
/// An absent v1 file is an empty list (or a null system), exactly as the folder reader gives it.
/// </summary>
internal sealed class CaseDtos
{
    public SystemDto? System { get; init; }
    public List<BusDto> Buses { get; init; } = new();
    public List<GenDto> Generators { get; init; } = new();
    public List<DemandDto> Demands { get; init; } = new();
    public List<LineDto> Lines { get; init; } = new();
    public List<TransformerDto> Transformers { get; init; } = new();
    public List<OltcDto> Oltcs { get; init; } = new();
    public List<AssetClassDto> AssetClasses { get; init; } = new();
    public List<AssetBindingDto> AssetBindings { get; init; } = new();
    public List<ProtectionInstanceDto> Protections { get; init; } = new();

    /// <summary>The boundaries, produced on demand: the folder reader reads boundaries.json only after the branches are
    /// built (as it always did), so a case with several problems still reports them in the same order.</summary>
    public Func<List<BoundaryDto>> Boundaries { get; init; } = () => new List<BoundaryDto>();
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the v1 case folder files (system.json, buses.json and the rest) and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SystemDto))]
[JsonSerializable(typeof(List<BusDto>))]
[JsonSerializable(typeof(List<GenDto>))]
[JsonSerializable(typeof(List<DemandDto>))]
[JsonSerializable(typeof(List<LineDto>))]
[JsonSerializable(typeof(List<TransformerDto>))]
[JsonSerializable(typeof(List<OltcDto>))]
[JsonSerializable(typeof(List<AssetClassDto>))]
[JsonSerializable(typeof(List<AssetBindingDto>))]
[JsonSerializable(typeof(List<ProtectionInstanceDto>))]
[JsonSerializable(typeof(List<BoundaryDto>))]
internal sealed partial class JsonGridLoaderJsonContext : JsonSerializerContext;
