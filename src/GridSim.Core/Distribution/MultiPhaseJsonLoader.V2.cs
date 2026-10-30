using System.Numerics;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Schema;
// The phase helper, aliased as in the v1 part of this class (which has its own PhaseIndex(...) method).
using PhaseIx = GridSim.Core.Distribution.PhaseIndex;

namespace GridSim.Core.Distribution;

/// <summary>
/// N339 (October 2026): the provenance carried by a <c>gridsim-dist3ph/2</c> feeder.
/// </summary>
/// <param name="Default">The document's <c>provenance._default</c>: what every value is, unless its element says otherwise.</param>
/// <param name="ByElement">Per-element provenance, keyed <c>bus:&lt;id&gt;</c>, <c>branch:&lt;id&gt;</c>,
/// <c>capacitor:&lt;id&gt;</c>, <c>load:&lt;id&gt;</c> or (N360, CF6) <c>converter:&lt;id&gt;</c>. Empty when no element
/// carries its own.</param>
public sealed record Dist3phProvenance(AssetClassProvenance Default, IReadOnlyDictionary<string, AssetClassProvenance> ByElement)
{
    /// <summary>The provenance of one element: its own entry, else the document default.</summary>
    public AssetClassProvenance For(string key) => ByElement.TryGetValue(key, out AssetClassProvenance? p) ? p : Default;
}

/// <summary>
/// N339 (October 2026): the JSON v2 form of the three-phase feeder, <c>gridsim-dist3ph/2</c>
/// (<c>schemas/v2/dist3ph.schema.json</c>). The v1 reader above is untouched: <see cref="Parse"/> still accepts exactly
/// <c>gridsim-dist3ph/1</c> and refuses every other tag, including this one. The v2 reader is <see cref="ParseV2"/>;
/// <see cref="LoadAny"/> picks the reader by the file's tag (what the <c>dist3ph</c> verb uses).
///
/// <para><b>What the v2 form is.</b> The solver-authoritative per-unit model, written exactly: every per-unit number
/// (series impedance and shunt admittance matrices, regulator and transformer ratios, source voltages, load powers,
/// capacitor ratings) as the double the model holds, so <see cref="MultiPhaseV2Writer"/> followed by this reader gives
/// back the same model bit for bit, and the physical context (BaseMva in MVA, each bus's kV base) beside it. That
/// follows the v2 rules: closed unit vocabulary (pu@system, MVA, kV, dimensionless ratios; nothing is converted at load,
/// so no unit outside the vocabulary is ever needed, unlike the v1 file's ohm/mile, ft and kW), provenance on every
/// value (a document default and optional per-element entries; a CuratedPlaceholder note must carry [VERIFY]), unknown
/// members refused, and a byte-stable writer. The v1 file stays the authoring format in physical units.</para>
///
/// <para><b>Checks.</b> First the bundled schema (every structural error at once, with JSON pointers); then, with the
/// v1 reader's own context and messages (file and JSON path on every error), what the schema subset cannot say: exact
/// vector and matrix sizes, finite numbers, unique ids (branches and open switches share one id space), bus references,
/// exactly one source, the branch kind rules (see the schema's branch description), lines, switches and regulators
/// joining one kV base, and then the v1 reader's own network checks unchanged: one radial tree, the gy-gy
/// three-wire rule, bus phases from the source outward, and loads and capacitors only on phases their bus has.</para>
/// </summary>
public static partial class MultiPhaseJsonLoader
{
    /// <summary>The v2 feeder tag, matched exactly.</summary>
    public const string SchemaV2 = "gridsim-dist3ph/2";

    /// <summary>The v2 schema file (bundled in Core, and under schemas/v2 on disk).</summary>
    public const string SchemaV2File = "dist3ph.schema.json";

    /// <summary>The branch kinds, exactly as <see cref="MultiPhaseFeeder.BranchKinds"/> spells them.</summary>
    private static readonly string[] BranchKindNames = { "line", "transformer", "switch", "regulator" };

    /// <summary>
    /// Read a feeder file of either version: a file whose top-level <c>"schema"</c> is exactly <see cref="SchemaV2"/> goes
    /// to <see cref="ParseV2"/>; EVERY other file (v1, a wrong tag, bad JSON) goes to the v1 <see cref="Parse"/>, exactly
    /// as <see cref="Load"/> sends it, so its result and its error messages are the v1 reader's, unchanged.
    /// </summary>
    public static MultiPhaseFeeder LoadAny(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"three-phase feeder file not found: {path}", path);
        string text = File.ReadAllText(path);
        return IsV2Text(text) ? ParseV2(text, path) : Parse(text, path);
    }

    /// <summary>True when <paramref name="json"/> parses and its root object's <c>"schema"</c> is exactly
    /// <see cref="SchemaV2"/>. Anything else (including text that is not JSON) is false.</summary>
    public static bool IsV2Text(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, DocOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("schema", out JsonElement s)
                && s.ValueKind == JsonValueKind.String
                && string.Equals(s.GetString(), SchemaV2, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;   // the v1 reader reports the syntax error, naming the file, as it always did
        }
    }

    /// <summary>Validate and build a feeder from <c>gridsim-dist3ph/2</c> JSON text. Throws
    /// <see cref="InvalidDataException"/> naming <paramref name="sourceName"/> and the place of the first problem (or,
    /// for schema errors, every one of them).</summary>
    public static MultiPhaseFeeder ParseV2(string json, string sourceName)
    {
        using JsonDocument doc = OpenDocument(json, sourceName);
        var ctx = new Ctx(sourceName);
        JsonElement root = doc.RootElement;
        ctx.RequireObject(root, "$");
        // The tag first, so a v1 file (or anything else) handed here says "wrong file type", not a list of schema errors.
        string schema = ctx.Str(root, "$", "schema");
        if (!string.Equals(schema, SchemaV2, StringComparison.Ordinal))
            throw ctx.Fail("$.schema", $"expected exactly \"{SchemaV2}\" but found \"{schema}\".");

        // 1. The bundled schema: structure, member names, types, enums and provenance, every error at once.
        IReadOnlyList<SchemaError> schemaErrors = BundledSchemas.V2Validate.Validate(root, SchemaV2File);
        if (schemaErrors.Count > 0)
            throw new InvalidDataException($"{sourceName}: not a valid {SchemaV2} document ({schemaErrors.Count} schema error(s)): " +
                string.Join("; ", schemaErrors.Select(e => e.ToString())));

        // 2. What the schema subset cannot say, with the v1 reader's accessors (each re-checks the type it reads).
        string name = ctx.Str(root, "$", "name");
        string description = ctx.Str(root, "$", "description");
        double baseMva = ctx.Number(root, "$", "baseMva");
        if (!(baseMva > 0)) throw ctx.Fail("$.baseMva", $"must be above 0 MVA (got {Fmt(baseMva)}).");
        AssetClassProvenance defaultProv = ReadProvenance(ctx, root.GetProperty("provenance").GetProperty("_default"), "$.provenance._default");
        var byElement = new Dictionary<string, AssetClassProvenance>(StringComparer.Ordinal);

        // ---- buses -----------------------------------------------------------------------------------------
        var buses = new List<PhaseBus>();
        var baseKv = new Dictionary<int, double>();
        var busNames = new Dictionary<int, string>();
        int sourceCount = 0, bi = 0;
        foreach (JsonElement b in ctx.Arr(root, "$", "buses").EnumerateArray())
        {
            string p = $"$.buses[{bi}]";
            int id = ctx.Int(b, p, "id");
            if (baseKv.ContainsKey(id)) throw ctx.Fail($"{p}.id", $"duplicate bus id {id}.");
            double kv = ctx.Number(b, p, "baseKvLL");
            if (!(kv > 0)) throw ctx.Fail($"{p}.baseKvLL", $"must be above 0 kV (got {Fmt(kv)}).");
            baseKv[id] = kv;
            if (b.TryGetProperty("name", out _))
            {
                string nm = ctx.Str(b, p, "name");
                if (busNames.ContainsValue(nm)) throw ctx.Fail($"{p}.name", $"duplicate bus name \"{nm}\".");
                busNames[id] = nm;
            }
            if (b.TryGetProperty("sourceVPu", out _))
            {
                sourceCount++;
                Complex[] v = ComplexVector3(ctx, b, p, "sourceVPu");
                for (int ph = 0; ph < 3; ph++)
                    if (!(v[ph].Magnitude > 0)) throw ctx.Fail($"{p}.sourceVPu", $"the source voltage on phase {"abc"[ph]} must have a magnitude above 0 pu.");
                buses.Add(new PhaseBus(id, true, v));
            }
            else buses.Add(new PhaseBus(id, false));
            ElementProvenance(ctx, b, p, $"bus:{id}", byElement);
            bi++;
        }
        if (sourceCount != 1)
            throw ctx.Fail("$.buses", $"exactly one bus must carry \"sourceVPu\" (found {sourceCount}).");

        // ---- branches, in model order ----------------------------------------------------------------------
        var branches = new List<PhaseBranch>();
        var branchIds = new List<string>();
        var branchKinds = new List<string>();
        var seenBranchIds = new HashSet<string>(StringComparer.Ordinal);
        int ki = 0;
        foreach (JsonElement e in ctx.Arr(root, "$", "branches").EnumerateArray())
        {
            string p = $"$.branches[{ki}]";
            string id = BranchId(ctx, e, p, seenBranchIds);
            string kind = ctx.Str(e, p, "kind");
            if (Array.IndexOf(BranchKindNames, kind) < 0)
                throw ctx.Fail($"{p}.kind", $"must be one of {string.Join(", ", BranchKindNames)} (got \"{kind}\").");
            (int from, int to) = Endpoints(ctx, e, p, baseKv);
            Phase phases = Phases(ctx, e, p);
            Complex[,] z = ComplexMatrix3(ctx, e, p, "zPu");
            Complex[,]? shunt = e.TryGetProperty("shuntYPu", out _) ? ComplexMatrix3(ctx, e, p, "shuntYPu") : null;
            double[]? regRatio = e.TryGetProperty("regulatorRatio", out _) ? ctx.Vector3(e, p, "regulatorRatio") : null;
            string? element = e.TryGetProperty("transformerElement", out _) ? ctx.Str(e, p, "transformerElement") : null;
            bool hasRatio = e.TryGetProperty("transformerRatio", out _);
            double tRatio = hasRatio ? ctx.Number(e, p, "transformerRatio") : 1.0;
            if (hasRatio && !(tRatio > 0)) throw ctx.Fail($"{p}.transformerRatio", $"must be above 0 (got {Fmt(tRatio)}).");
            if (regRatio is not null)
                for (int ph = 0; ph < 3; ph++)
                    if (!(regRatio[ph] > 0)) throw ctx.Fail($"{p}.regulatorRatio[{ph}]", $"must be above 0 (got {Fmt(regRatio[ph])}).");

            // Every kind except a transformer joins buses of one kV base (the v1 rule for lines, switches, regulators).
            if (kind != "transformer" && baseKv[from] != baseKv[to])
                throw ctx.Fail(p, $"{kind} \"{id}\" joins bus {from} ({Fmt(baseKv[from])} kV) to bus {to} ({Fmt(baseKv[to])} kV); " +
                                  $"only a transformer joins buses of different kV base.");
            // Members that belong to one kind only.
            if (element is not null && kind != "transformer")
                throw ctx.Fail($"{p}.transformerElement", $"only a transformer carries a transformer element (this branch is a {kind}).");
            if (hasRatio && element is null)
                throw ctx.Fail($"{p}.transformerRatio", "is the ratio of a transformer element and needs \"transformerElement\" (an off-nominal grounded wye / grounded wye transformer is written with \"regulatorRatio\").");

            PhaseBranch branch;
            switch (kind)
            {
                case "line":
                    if (regRatio is not null) throw ctx.Fail($"{p}.regulatorRatio", "a line carries no regulator (write the regulator as its own branch of kind regulator).");
                    // The v1 code rule: absent phases all zero (series and shunt), each present phase with a conductor.
                    CheckCodePhasesV2(ctx, p, id, z, shunt, phases);
                    // The v1 reader's value checks, applied here too (the v2 reader used to skip them, so the same feeder
                    // loaded as v2 but was refused as v1): no negative self resistance, a symmetric shunt with no
                    // negative self susceptance.
                    for (int i = 0; i < 3; i++)
                        if (z[i, i].Real < 0)
                            throw ctx.Fail($"{p}.zPu", $"line \"{id}\": self resistance of phase {"abc"[i]} must not be negative (got {Fmt(z[i, i].Real)}).");
                    if (shunt is not null)
                    {
                        for (int i = 0; i < 3; i++)
                        for (int j = i + 1; j < 3; j++)
                            if (shunt[i, j] != shunt[j, i])
                                throw ctx.Fail($"{p}.shuntYPu", $"line \"{id}\": the shunt admittance matrix must be symmetric (entries [{i}][{j}] and [{j}][{i}] differ).");
                        for (int i = 0; i < 3; i++)
                            if (shunt[i, i].Imaginary < 0)
                                throw ctx.Fail($"{p}.shuntYPu", $"line \"{id}\": self susceptance of phase {"abc"[i]} must not be negative (got {Fmt(shunt[i, i].Imaginary)}).");
                    }
                    branch = new PhaseBranch(from, to, new PhaseImpedance(z), phases, shunt is null ? null : new PhaseImpedance(shunt));
                    break;
                case "switch":
                    RequireAllZero(ctx, $"{p}.zPu", z, "a closed switch has no impedance");
                    if (shunt is not null) throw ctx.Fail($"{p}.shuntYPu", "a switch carries no shunt admittance.");
                    if (regRatio is not null) throw ctx.Fail($"{p}.regulatorRatio", "a switch carries no regulator.");
                    branch = new PhaseBranch(from, to, new PhaseImpedance(z), phases);
                    break;
                case "regulator":
                    RequireAllZero(ctx, $"{p}.zPu", z, "an ideal regulator has no series impedance");
                    if (shunt is not null) throw ctx.Fail($"{p}.shuntYPu", "a regulator carries no shunt admittance.");
                    if (regRatio is null) throw ctx.Fail($"{p}.regulatorRatio", "required member is missing (a regulator is its per-phase ratio).");
                    for (int ph = 0; ph < 3; ph++)
                        if (!PhaseIx.Has(phases, ph) && regRatio[ph] != 1.0)
                            throw ctx.Fail($"{p}.regulatorRatio[{ph}]", $"phase {"abc"[ph]} is not one of the regulator's phases ({PhaseIx.Name(phases)}), so its ratio must be 1 (got {Fmt(regRatio[ph])}).");
                    branch = new PhaseBranch(from, to, new PhaseImpedance(z), phases, RegulatorRatio: regRatio);
                    break;
                default: // transformer
                    if (phases != Phase.Abc) throw ctx.Fail($"{p}.phases", $"a transformer carries all three phases (got \"{PhaseIx.Name(phases)}\").");
                    if (IsAllZero(z)) throw ctx.Fail($"{p}.zPu", $"transformer \"{id}\" has zero impedance.");
                    if (element is not null)
                    {
                        if (shunt is not null) throw ctx.Fail($"{p}.shuntYPu", "a transformer element carries no shunt admittance.");
                        if (regRatio is not null) throw ctx.Fail($"{p}.regulatorRatio", "a transformer element's ratio is \"transformerRatio\", not \"regulatorRatio\".");
                        branch = element switch
                        {
                            "delta-wye-step-down" => new PhaseBranch(from, to, new PhaseImpedance(z), DeltaWyeStepDown: true, TransformerRatio: tRatio),
                            "delta-wye-step-up" => new PhaseBranch(from, to, new PhaseImpedance(z), DeltaWyeStepUp: true, TransformerRatio: tRatio),
                            "wye-delta-step-down" => new PhaseBranch(from, to, new PhaseImpedance(z), WyeDeltaStepDown: true, TransformerRatio: tRatio),
                            "wye-delta-step-up" => new PhaseBranch(from, to, new PhaseImpedance(z), WyeDeltaStepUp: true, TransformerRatio: tRatio),
                            "delta-delta" => new PhaseBranch(from, to, new PhaseImpedance(z), DeltaDelta: true, TransformerRatio: tRatio),
                            // N334 (CF5): the open banks; zPu is their open-delta series matrix as written.
                            "open-wye-open-delta" => new PhaseBranch(from, to, new PhaseImpedance(z), OpenWyeOpenDelta: true, TransformerRatio: tRatio),
                            "open-delta-open-delta" => new PhaseBranch(from, to, new PhaseImpedance(z), OpenDeltaOpenDelta: true, TransformerRatio: tRatio),
                            _ => throw ctx.Fail($"{p}.transformerElement", $"unknown transformer element \"{element}\"."),
                        };
                    }
                    else
                    {
                        // Grounded wye / grounded wye: a series impedance, and for an off-nominal ratio the ideal ratio at
                        // its From end on every phase (exactly how the v1 reader builds it).
                        if (regRatio is not null && !(regRatio[0] == regRatio[1] && regRatio[1] == regRatio[2]))
                            throw ctx.Fail($"{p}.regulatorRatio", "an off-nominal grounded wye / grounded wye transformer has one ratio on all three phases.");
                        branch = new PhaseBranch(from, to, new PhaseImpedance(z), Phase.Abc, shunt is null ? null : new PhaseImpedance(shunt), RegulatorRatio: regRatio);
                    }
                    break;
            }
            branches.Add(branch);
            branchIds.Add(id);
            branchKinds.Add(kind);
            ElementProvenance(ctx, e, p, $"branch:{id}", byElement);
            ki++;
        }

        // ---- open switches (ids only: an open switch is not a branch) --------------------------------------
        var openSwitches = new List<string>();
        if (root.TryGetProperty("openSwitches", out _))
        {
            int oi = 0;
            foreach (JsonElement s in ctx.Arr(root, "$", "openSwitches").EnumerateArray())
            {
                string p = $"$.openSwitches[{oi}]";
                if (s.ValueKind != JsonValueKind.String) throw ctx.Fail(p, $"expected a string but found {s.ValueKind}.");
                string id = s.GetString()!;
                if (!seenBranchIds.Add(id)) throw ctx.Fail(p, $"duplicate branch id \"{id}\" (branches and open switches share one id space).");
                openSwitches.Add(id);
                oi++;
            }
        }

        // ---- capacitors ------------------------------------------------------------------------------------
        var capacitors = new List<PhaseCapacitor>();
        var capacitorIds = new List<string>();
        var capacitorPaths = new List<string>();
        if (root.TryGetProperty("capacitors", out _))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int ci = 0;
            foreach (JsonElement c in ctx.Arr(root, "$", "capacitors").EnumerateArray())
            {
                string p = $"$.capacitors[{ci}]";
                string id = ctx.Str(c, p, "id");
                if (!seen.Add(id)) throw ctx.Fail($"{p}.id", $"duplicate capacitor id \"{id}\".");
                int bus = ctx.Int(c, p, "bus");
                if (!baseKv.ContainsKey(bus)) throw ctx.Fail($"{p}.bus", $"unknown bus {bus} (not in $.buses).");
                double[] q = ctx.Vector3(c, p, "qPu");
                for (int ph = 0; ph < 3; ph++)
                    if (q[ph] < 0) throw ctx.Fail($"{p}.qPu[{ph}]", $"must not be negative (got {Fmt(q[ph])}).");
                if (q.All(x => x == 0)) throw ctx.Fail($"{p}.qPu", "a capacitor bank needs a unit on at least one phase.");
                capacitors.Add(new PhaseCapacitor(bus, q));
                capacitorIds.Add(id);
                capacitorPaths.Add(p);
                ElementProvenance(ctx, c, p, $"capacitor:{id}", byElement);
                ci++;
            }
        }

        // ---- loads -----------------------------------------------------------------------------------------
        var loads = new List<PhaseLoad>();
        var loadIds = new List<string>();
        var loadPaths = new List<string>();
        if (root.TryGetProperty("loads", out _))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int li = 0;
            foreach (JsonElement l in ctx.Arr(root, "$", "loads").EnumerateArray())
            {
                string p = $"$.loads[{li}]";
                string id = ctx.Str(l, p, "id");
                if (!seen.Add(id)) throw ctx.Fail($"{p}.id", $"duplicate load id \"{id}\".");
                int bus = ctx.Int(l, p, "bus");
                if (!baseKv.ContainsKey(bus)) throw ctx.Fail($"{p}.bus", $"unknown bus {bus} (not in $.buses).");
                string conn = ctx.Str(l, p, "connection");
                PhaseLoadConnection connection = conn switch
                {
                    "wye" => PhaseLoadConnection.Wye,
                    "delta" => PhaseLoadConnection.Delta,
                    _ => throw ctx.Fail($"{p}.connection", $"must be \"wye\" or \"delta\" (got \"{conn}\")."),
                };
                string model = ctx.Str(l, p, "model");
                PhaseLoadModel loadModel = model switch
                {
                    "constant-power" => PhaseLoadModel.ConstantPower,
                    "constant-current" => PhaseLoadModel.ConstantCurrent,
                    "constant-impedance" => PhaseLoadModel.ConstantImpedance,
                    _ => throw ctx.Fail($"{p}.model", $"must be \"constant-power\", \"constant-current\" or \"constant-impedance\" (got \"{model}\")."),
                };
                Complex[] s = ComplexVector3(ctx, l, p, "sPu");
                // Built exactly as the v1 reader builds it: a constant-power wye load with the record's defaults.
                loads.Add(loadModel == PhaseLoadModel.ConstantPower && connection == PhaseLoadConnection.Wye
                    ? new PhaseLoad(bus, s)
                    : new PhaseLoad(bus, s, loadModel, connection));
                loadIds.Add(id);
                loadPaths.Add(p);
                ElementProvenance(ctx, l, p, $"load:{id}", byElement);
                li++;
            }
        }

        // ---- converters (N360, CF6) ------------------------------------------------------------------------
        // The v1 "converters" section (N354, CF5) in the v2 form: active-front-end units for the three-phase harmonic
        // study only. Written as the doubles the model holds (ThreePhaseActiveFrontEnd), so the round trip is exact:
        // ratingMva (MVA), xOnRating and optional rOnRating (per unit on the unit's OWN rating, a dimensionless ratio,
        // which is why the members do not end in Pu: Pu members are on the system base). No arithmetic at load: the v1
        // reader's kva / 1000 and percent / 100 happened once, when the v1 file was read; the v2 form holds the results.
        // The same value rules as v1: rating and reactance above 0, resistance 0 or more (absent: exactly 0.0, the v1
        // default). Converter ids are their own id space (as load and capacitor ids are).
        var converters = new List<GridSim.Core.Harmonics.ThreePhaseActiveFrontEnd>();
        var converterIds = new List<string>();
        if (root.TryGetProperty("converters", out _))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int vi = 0;
            foreach (JsonElement c in ctx.Arr(root, "$", "converters").EnumerateArray())
            {
                string p = $"$.converters[{vi}]";
                string id = ctx.Str(c, p, "id");
                if (!seen.Add(id)) throw ctx.Fail($"{p}.id", $"duplicate converter id \"{id}\".");
                int bus = ctx.Int(c, p, "bus");
                if (!baseKv.ContainsKey(bus)) throw ctx.Fail($"{p}.bus", $"unknown bus {bus} (not in $.buses).");
                double rating = ctx.Number(c, p, "ratingMva");
                if (!(rating > 0)) throw ctx.Fail($"{p}.ratingMva", $"must be above 0 MVA (got {Fmt(rating)}).");
                double x = ctx.Number(c, p, "xOnRating");
                if (!(x > 0)) throw ctx.Fail($"{p}.xOnRating", $"must be above 0 (got {Fmt(x)}): the converter's internal reactance.");
                double r = c.TryGetProperty("rOnRating", out _) ? ctx.Number(c, p, "rOnRating") : 0.0;
                if (r < 0) throw ctx.Fail($"{p}.rOnRating", $"must not be negative (got {Fmt(r)}).");
                converters.Add(new GridSim.Core.Harmonics.ThreePhaseActiveFrontEnd(bus, rating, x, r));
                converterIds.Add(id);
                ElementProvenance(ctx, c, p, $"converter:{id}", byElement);
                vi++;
            }
        }

        // 3. The v1 reader's network checks, unchanged.
        CheckRadialTree(ctx, buses, branches);
        CheckThreeWireTransformers(ctx, buses, branches, branchIds, branchKinds, "$.branches");
        Dictionary<int, Phase> busPhases = BusPhases(ctx, buses, branches, branchIds, branchKinds);
        CheckElementPhases(ctx, busPhases, loads, loadPaths, capacitors, capacitorPaths);

        var modelOut = new MultiPhaseModel(buses, branches, loads, baseMva, capacitors);
        return new MultiPhaseFeeder(modelOut, sourceName, name, description, baseMva, baseKv, branchIds, branchKinds, loadIds)
        {
            BusNames = busNames,
            OpenSwitchIds = openSwitches,
            CapacitorIds = capacitorIds,
            Converters = converters,       // N360 (CF6): empty for a document without the section, as before
            ConverterIds = converterIds,
            Provenance = new Dist3phProvenance(defaultProv, byElement),
        };
    }

    // ---- v2 helpers ----------------------------------------------------------------------------------------------

    /// <summary>A {tag, note} provenance object. The schema already checked the tag and the [VERIFY] rule; both are
    /// checked again here so the rule holds even if the schema file were edited.</summary>
    private static AssetClassProvenance ReadProvenance(Ctx ctx, JsonElement e, string p)
    {
        string tag = ctx.Str(e, p, "tag");
        string note = ctx.Str(e, p, "note");
        if (!Enum.TryParse(tag, ignoreCase: false, out ProvenanceTag t) || !Enum.IsDefined(t) || int.TryParse(tag, out _))
            throw ctx.Fail($"{p}.tag", $"\"{tag}\" is not a provenance tag (exactly one of {string.Join(", ", Enum.GetNames<ProvenanceTag>())}).");
        if (t == ProvenanceTag.CuratedPlaceholder && !note.Contains("[VERIFY]", StringComparison.Ordinal))
            throw ctx.Fail($"{p}.note", "a CuratedPlaceholder note must contain the literal token [VERIFY].");
        return new AssetClassProvenance(t, note);
    }

    private static void ElementProvenance(Ctx ctx, JsonElement e, string p, string key, Dictionary<string, AssetClassProvenance> into)
    {
        if (e.TryGetProperty("prov", out JsonElement prov)) into[key] = ReadProvenance(ctx, prov, $"{p}.prov");
    }

    /// <summary>A {re, im} pair of 3-vectors as three complex numbers.</summary>
    private static Complex[] ComplexVector3(Ctx ctx, JsonElement e, string p, string name)
    {
        JsonElement c = e.GetProperty(name);
        ctx.RequireObject(c, $"{p}.{name}");
        double[] re = ctx.Vector3(c, $"{p}.{name}", "re");
        double[] im = ctx.Vector3(c, $"{p}.{name}", "im");
        return new[] { new Complex(re[0], im[0]), new Complex(re[1], im[1]), new Complex(re[2], im[2]) };
    }

    /// <summary>A {re, im} pair of 3x3 matrices as one complex 3x3 matrix.</summary>
    private static Complex[,] ComplexMatrix3(Ctx ctx, JsonElement e, string p, string name)
    {
        JsonElement c = e.GetProperty(name);
        ctx.RequireObject(c, $"{p}.{name}");
        double[,] re = ctx.Matrix3(c, $"{p}.{name}", "re");
        double[,] im = ctx.Matrix3(c, $"{p}.{name}", "im");
        var m = new Complex[3, 3];
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            m[i, j] = new Complex(re[i, j], im[i, j]);
        return m;
    }

    private static bool IsAllZero(Complex[,] m)
    {
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            if (m[i, j] != Complex.Zero) return false;
        return true;
    }

    private static void RequireAllZero(Ctx ctx, string p, Complex[,] m, string why)
    {
        if (!IsAllZero(m)) throw ctx.Fail(p, $"must be all zero: {why}.");
    }

    /// <summary>The v1 line-code phase rule on a v2 line's own per-unit matrices: every entry of an absent phase's row and
    /// column zero (series and shunt), and every present phase with a conductor (a non-zero self impedance).</summary>
    private static void CheckCodePhasesV2(Ctx ctx, string p, string id, Complex[,] z, Complex[,]? y, Phase phases)
    {
        for (int i = 0; i < 3; i++)
        {
            bool present = PhaseIx.Has(phases, i);
            if (present && z[i, i] == Complex.Zero)
                throw ctx.Fail($"{p}.zPu", $"line \"{id}\" carries phase {"abc"[i]} but its self impedance on that phase is zero.");
            for (int j = 0; j < 3; j++)
            {
                if (present && PhaseIx.Has(phases, j)) continue;
                if (z[i, j] != Complex.Zero || (y is not null && y[i, j] != Complex.Zero))
                    throw ctx.Fail($"{p}.phases", $"line \"{id}\" carries phases {PhaseIx.Name(phases)} but has a non-zero entry [{i}][{j}] on another phase.");
            }
        }
    }
}
