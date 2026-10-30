using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Provenance;

/// <summary>One source a provenance manifest cites.</summary>
/// <param name="Id">The id elements cite it by (unique in the manifest).</param>
/// <param name="Title">What it is (a document, a table, a register).</param>
/// <param name="Publisher">Who publishes it.</param>
/// <param name="Url">Where it is published (or where it was obtained).</param>
/// <param name="Licence">The licence it is published under (as the publisher states it).</param>
/// <param name="Published">True only for a source anyone can obtain from its publisher under that licence. False for a
/// private, derived or synthesised source (a model's own assembly step, an analysis product, an estimate): an element
/// that cites one is not public.</param>
public sealed record ProvenanceSource(string Id, string Title, string Publisher, string Url, string Licence, bool Published);

/// <summary>
/// Export pack (2026-10): a <c>gridsim-provenance/1</c> manifest: for each element of a case, the sources its data comes
/// from. Elements are keyed as a case names them: buses by id, branches by name, generators by name, a bus's demand and
/// its fixed shunt by the bus id, an on-load tap changer by its transformer's name. It is the input of
/// <see cref="PublicExportGate"/>, which keeps only elements every one of whose sources is published.
/// </summary>
/// <remarks>
/// <para>The file (JSON, every key required unless marked optional; any other key is refused):</para>
/// <code>
/// {
///   "schema": "gridsim-provenance/1",
///   "case": "&lt;case name&gt;",
///   "sources": [ { "id", "title", "publisher", "url", "licence", "published": true|false } ],
///   "buses":      { "&lt;bus id&gt;": ["&lt;source id&gt;", ...] },
///   "branches":   { "&lt;branch name&gt;": [...] },
///   "generators": { "&lt;generator name&gt;": [...] },
///   "demand":     { "&lt;bus id&gt;": [...] },   (optional)
///   "shunts":     { "&lt;bus id&gt;": [...] },   (optional)
///   "oltcs":      { "&lt;transformer name&gt;": [...] }   (optional)
/// }
/// </code>
/// <para>Fail loud: a missing or unknown key, a source id cited but not listed, a source listed twice, an empty source
/// list for an element, a bus id that is not an integer, or a value of the wrong JSON type is refused naming it. A
/// manifest is evidence, not a default: nothing here makes one up, and an element it does not name is not public.</para>
/// </remarks>
public sealed class ProvenanceManifest
{
    /// <summary>The schema tag this reader reads.</summary>
    public const string Schema = "gridsim-provenance/1";

    /// <summary>The case the manifest describes.</summary>
    public required string Case { get; init; }

    /// <summary>The sources, by id.</summary>
    public required IReadOnlyDictionary<string, ProvenanceSource> Sources { get; init; }

    /// <summary>Bus id to source ids.</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<string>> Buses { get; init; }

    /// <summary>Branch name to source ids.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Branches { get; init; }

    /// <summary>Generator name to source ids.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Generators { get; init; }

    /// <summary>Bus id to the source ids of that bus's demand (Pd, Qd and any load model).</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<string>> Demand { get; init; }

    /// <summary>Bus id to the source ids of that bus's fixed shunt (Gs, Bs).</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<string>> Shunts { get; init; }

    /// <summary>Transformer name to the source ids of its on-load tap changer.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Oltcs { get; init; }

    /// <summary>True when the element's source list is non-empty and every source on it is published.</summary>
    public bool IsPublic(IReadOnlyList<string>? sources) =>
        sources is { Count: > 0 } && sources.All(s => Sources[s].Published);

    /// <summary>Read a manifest file.</summary>
    public static ProvenanceManifest Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"provenance manifest '{path}' cannot be read: {ex.Message}", ex);
        }
        return Parse(text, path);
    }

    /// <summary>Parse a manifest from its JSON text; <paramref name="where"/> names it in messages.</summary>
    public static ProvenanceManifest Parse(string json, string where = "provenance manifest")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException($"{where}: not JSON: {ex.Message}"); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where}: the top level must be an object.");
            var known = new HashSet<string>(StringComparer.Ordinal) { "schema", "case", "sources", "buses", "branches", "generators", "demand", "shunts", "oltcs" };
            foreach (JsonProperty p in root.EnumerateObject())
                if (!known.Contains(p.Name)) throw new InvalidDataException($"{where}: unknown key '{p.Name}'.");
            string schema = Str(root, "schema", where);
            if (schema != Schema) throw new InvalidDataException($"{where}: schema is '{schema}'; this reader reads '{Schema}'.");
            string caseName = Str(root, "case", where);

            var sources = new Dictionary<string, ProvenanceSource>(StringComparer.Ordinal);
            JsonElement srcs = Required(root, "sources", JsonValueKind.Array, where);
            int k = 0;
            foreach (JsonElement s in srcs.EnumerateArray())
            {
                string at = $"{where}: sources[{k++}]";
                if (s.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{at} must be an object.");
                var srcKnown = new HashSet<string>(StringComparer.Ordinal) { "id", "title", "publisher", "url", "licence", "published" };
                foreach (JsonProperty p in s.EnumerateObject())
                    if (!srcKnown.Contains(p.Name)) throw new InvalidDataException($"{at}: unknown key '{p.Name}'.");
                JsonElement pub = Required(s, "published", null, at);
                if (pub.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException($"{at}: 'published' must be true or false.");
                var src = new ProvenanceSource(Str(s, "id", at), Str(s, "title", at), Str(s, "publisher", at), Str(s, "url", at),
                    Str(s, "licence", at), pub.ValueKind == JsonValueKind.True);
                if (!sources.TryAdd(src.Id, src)) throw new InvalidDataException($"{at}: source id '{src.Id}' is listed twice.");
            }

            IReadOnlyList<string> Cites(JsonElement list, string at)
            {
                if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{at} must be an array of source ids.");
                var ids = new List<string>();
                foreach (JsonElement e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{at}: a source id must be a string.");
                    string id = e.GetString()!;
                    if (!sources.ContainsKey(id)) throw new InvalidDataException($"{at} cites source '{id}', which the manifest does not list.");
                    ids.Add(id);
                }
                if (ids.Count == 0) throw new InvalidDataException($"{at} cites no source; leave the element out instead (an element not named is not public).");
                return ids;
            }
            Dictionary<string, IReadOnlyList<string>> ByName(string key, bool required)
            {
                var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                if (!root.TryGetProperty(key, out JsonElement obj))
                {
                    if (required) throw new InvalidDataException($"{where}: '{key}' is missing.");
                    return map;
                }
                if (obj.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where}: '{key}' must be an object.");
                foreach (JsonProperty p in obj.EnumerateObject())
                    if (!map.TryAdd(p.Name, Cites(p.Value, $"{where}: {key}['{p.Name}']")))
                        throw new InvalidDataException($"{where}: {key} names '{p.Name}' twice.");
                return map;
            }
            Dictionary<int, IReadOnlyList<string>> ById(string key, bool required)
            {
                var map = new Dictionary<int, IReadOnlyList<string>>();
                foreach ((string name, IReadOnlyList<string> ids) in ByName(key, required))
                {
                    if (!int.TryParse(name, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int id))
                        throw new InvalidDataException($"{where}: {key} key '{name}' is not a bus id (an integer).");
                    if (!map.TryAdd(id, ids)) throw new InvalidDataException($"{where}: {key} names bus {id} twice.");
                }
                return map;
            }

            return new ProvenanceManifest
            {
                Case = caseName,
                Sources = sources,
                Buses = ById("buses", true),
                Branches = ByName("branches", true),
                Generators = ByName("generators", true),
                Demand = ById("demand", false),
                Shunts = ById("shunts", false),
                Oltcs = ByName("oltcs", false),
            };
        }
    }

    private static JsonElement Required(JsonElement obj, string key, JsonValueKind? kind, string where)
    {
        if (!obj.TryGetProperty(key, out JsonElement v)) throw new InvalidDataException($"{where}: '{key}' is missing.");
        if (kind is JsonValueKind k && v.ValueKind != k) throw new InvalidDataException($"{where}: '{key}' must be {(k == JsonValueKind.Array ? "an array" : k.ToString().ToLowerInvariant())}.");
        return v;
    }

    private static string Str(JsonElement obj, string key, string where)
    {
        JsonElement v = Required(obj, key, JsonValueKind.String, where);
        string s = v.GetString()!;
        if (s.Trim().Length == 0) throw new InvalidDataException($"{where}: '{key}' is empty.");
        return s;
    }
}

/// <summary>What <see cref="PublicExportGate.Apply"/> kept and stripped.</summary>
/// <param name="Model">The public model: only elements every one of whose sources is published.</param>
/// <param name="Stripped">Every element (or part of one) left out, one line each, naming it and why.</param>
public sealed record PublicExportResult(GridModel Model, IReadOnlyList<string> Stripped);

/// <summary>
/// Export pack (2026-10): the PUBLIC export gate. Given a case and its <see cref="ProvenanceManifest"/>, it keeps only
/// what is traceable to published sources and lists everything it strips; it never makes provenance up. With no
/// manifest there is no public export at all (the caller refuses; see <c>gridsim convert --public</c>).
/// </summary>
/// <remarks>
/// <para>Rules. A bus is kept when the manifest names it and every source it cites is published. A branch, a generator
/// (in service or not) or an on-load tap changer is kept when it has a name the manifest gives published sources for,
/// that name is unique in the case (a repeated name cannot say which element it means), and every bus it touches is kept.
/// A kept bus keeps its demand (and load model) and its fixed shunt only when the manifest gives published sources for
/// them; otherwise they are zeroed and listed. What the manifest has no section for is stripped whole and listed: HVDC
/// links, FACTS devices, static var compensators, induction machines, transformer flow controls, zero-sequence mutual
/// couplings, boundaries, the asset-dynamics and protection bundles, and layout coordinates.</para>
/// <para>A kept PV bus all of whose in-service generators were stripped is made a PQ bus (listed); a kept slack bus all of
/// whose in-service generators were stripped is refused.</para>
/// <para>Fail loud: a manifest for another case, or a public model with no slack bus (or one that is otherwise invalid),
/// is refused (<see cref="InvalidDataException"/>), since there is then nothing correct to write.</para>
/// </remarks>
public static class PublicExportGate
{
    /// <summary>How many names each stripped-element line shows before "and N more".</summary>
    public const int ShownPerLine = 10;

    /// <summary>Apply the gate. See the class remarks for the rules.</summary>
    public static PublicExportResult Apply(GridModel model, ProvenanceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Case, model.Name, StringComparison.Ordinal))
            throw new InvalidDataException($"the provenance manifest is for case '{manifest.Case}', not '{model.Name}'.");
        var stripped = new List<string>();
        void Strip(string what, IReadOnlyList<string> names, string why)
        {
            if (names.Count == 0) return;
            string shown = string.Join(", ", names.Take(ShownPerLine));
            stripped.Add($"{names.Count} {what} stripped ({why}): {shown}{(names.Count > ShownPerLine ? $" and {names.Count - ShownPerLine} more" : "")}.");
        }

        // Buses.
        var keptBus = new HashSet<int>();
        var busOut = new List<string>();
        foreach (Bus b in model.Buses)
            if (manifest.IsPublic(manifest.Buses.GetValueOrDefault(b.Id))) keptBus.Add(b.Id);
            else busOut.Add(b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Strip("bus(es)", busOut, "no published source");

        var demandOut = new List<string>();
        var shuntOut = new List<string>();
        int layout = 0;
        var buses = new List<Bus>();
        foreach (Bus b in model.Buses)
        {
            if (!keptBus.Contains(b.Id)) continue;
            Bus nb = b;
            if ((b.Pd != 0 || b.Qd != 0 || b.Zip is not null) && !manifest.IsPublic(manifest.Demand.GetValueOrDefault(b.Id)))
            {
                nb = nb with { Pd = 0, Qd = 0, Zip = null };
                demandOut.Add(b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if ((b.Gs != 0 || b.Bs != 0) && !manifest.IsPublic(manifest.Shunts.GetValueOrDefault(b.Id)))
            {
                nb = nb with { Gs = 0, Bs = 0 };
                shuntOut.Add(b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (b.X != 0 || b.Y != 0) { nb = nb with { X = 0, Y = 0 }; layout++; }
            buses.Add(nb);
        }
        Strip("bus demand(s)", demandOut, "no published source for the demand; zeroed");
        Strip("bus shunt(s)", shuntOut, "no published source for the shunt; zeroed");
        if (layout > 0) stripped.Add($"{layout} bus layout coordinate pair(s) zeroed (the manifest has no section for layout).");

        // Branches.
        var branchCount = model.Branches.Where(b => b.Name is not null).GroupBy(b => b.Name!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var branches = new List<Branch>();
        var branchOut = new List<string>();
        foreach (Branch br in model.Branches)
        {
            string label = br.Name ?? $"{br.From}-{br.To} (unnamed)";
            bool ok = br.Name is string n && branchCount[n] == 1 && manifest.IsPublic(manifest.Branches.GetValueOrDefault(n))
                      && keptBus.Contains(br.From) && keptBus.Contains(br.To);
            if (ok) branches.Add(br); else branchOut.Add(label);
        }
        Strip("branch(es)", branchOut, "unnamed, a repeated name, no published source, or an end bus stripped");

        // Generators (the whole fleet, out-of-service units included).
        var genCount = model.AllGenerators.Where(g => g.Name is not null).GroupBy(g => g.Name!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var gens = new List<Generator>();
        var genOut = new List<string>();
        foreach (Generator g in model.AllGenerators)
        {
            bool ok = g.Name is string n && genCount[n] == 1 && manifest.IsPublic(manifest.Generators.GetValueOrDefault(n)) && keptBus.Contains(g.Bus);
            if (ok) gens.Add(g); else genOut.Add(g.Name ?? $"unnamed at bus {g.Bus}");
        }
        Strip("generator(s)", genOut, "unnamed, a repeated name, no published source, or its bus stripped");

        // A voltage-controlled bus whose machines were all stripped holds no voltage any more: a PV bus becomes a PQ bus
        // (listed; its demand and shunt stay as the manifest allows), and a slack bus is refused, since choosing another
        // angle reference would be a modelling decision the manifest does not make.
        var holding = new HashSet<int>(gens.Where(g => g.InService).Select(g => g.Bus));
        var pvToPq = new List<string>();
        for (int i = 0; i < buses.Count; i++)
        {
            Bus b = buses[i];
            if (holding.Contains(b.Id)) continue;
            if (b.Type == BusType.Slack)
                throw new InvalidDataException($"the public model of '{model.Name}' cannot be written: slack bus {b.Id} is kept but none of its in-service generators is (no published source), so it has no angle reference.");
            if (b.Type == BusType.Pv)
            {
                buses[i] = b with { Type = BusType.Pq };
                pvToPq.Add(b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        if (pvToPq.Count > 0)
            stripped.Add($"{pvToPq.Count} PV bus(es) made PQ because every in-service generator on them was stripped: {string.Join(", ", pvToPq.Take(ShownPerLine))}{(pvToPq.Count > ShownPerLine ? $" and {pvToPq.Count - ShownPerLine} more" : "")}.");

        // On-load tap changers.
        var keptBranchNames = new HashSet<string>(branches.Select(b => b.Name!), StringComparer.Ordinal);
        var oltcs = new List<Oltc>();
        var oltcOut = new List<string>();
        foreach (Oltc o in model.Oltcs)
        {
            bool ok = keptBranchNames.Contains(o.Transformer) && keptBus.Contains(o.ControlsBus) && manifest.IsPublic(manifest.Oltcs.GetValueOrDefault(o.Transformer));
            if (ok) oltcs.Add(o); else oltcOut.Add(o.Transformer);
        }
        Strip("on-load tap changer(s)", oltcOut, "no published source, or its transformer or controlled bus stripped");

        // What the manifest has no section for.
        void Whole(int count, string what) { if (count > 0) stripped.Add($"{count} {what} stripped (the manifest has no section for them)."); }
        Whole(model.LccLinks.Count + model.VscLinks.Count + model.MtdcLinks.Count, "HVDC link(s)");
        Whole(model.FactsDevices.Count, "FACTS device(s)");
        Whole(model.StaticVarCompensators.Count, "static var compensator(s)");
        Whole(model.InductionMachines.Count, "induction machine(s)");
        Whole(model.TransformerFlowControls.Count, "transformer flow control(s)");
        Whole(model.ZeroSequenceMutuals.Count, "zero-sequence mutual coupling(s)");
        Whole(model.Boundaries.Count, "boundary(ies)");
        if (model.AssetDynamics is not null || model.Protections is not null)
            stripped.Add("the asset-dynamics and protection bundles stripped (the manifest has no section for them).");

        var pub = new GridModel
        {
            BaseMva = model.BaseMva,
            Buses = buses,
            Generators = gens,
            Branches = branches,
            Oltcs = oltcs,
            FrequencyHz = model.FrequencyHz,
            Name = model.Name,
        };
        if (!pub.Buses.Any(b => b.Type == BusType.Slack))
            throw new InvalidDataException($"the public model of '{model.Name}' has no slack bus (every slack bus was stripped), so it cannot be solved or written.");
        List<string> errors = pub.Validate().Where(d => d.Severity == ValidationSeverity.Error).Select(d => d.Message).ToList();
        if (errors.Count > 0)
            throw new InvalidDataException($"the public model of '{model.Name}' is invalid:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", errors));
        return new PublicExportResult(pub, stripped);
    }
}
