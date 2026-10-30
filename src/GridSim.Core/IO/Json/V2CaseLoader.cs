using System.Buffers;
using System.Globalization;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Json;

/// <summary>The outcome of <see cref="V2CaseLoader.LoadWithNotes"/>: the model and the notes the load produced (for
/// example which revision an unpinned import resolved to). A note is information, never a problem: a problem throws.</summary>
public sealed record V2CaseLoadResult(GridModel Model, IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Phase 5a (2026-10-20): the case's asset overlay (its manifest's <c>assets</c> files), checked and carried BESIDE
    /// the model; null when the manifest lists none. It is read after <see cref="Model"/> is complete and is never an
    /// input to it or to any solver, so <see cref="Model"/> is the same with or without it (AssetOverlayPinTests).
    /// </summary>
    public CaseAssetOverlay? Overlay { get; init; }
}

/// <summary>
/// N065 (October 2026), JSON v2 Phase 2: loads a <c>gridsim-case/2</c> case (a manifest plus the network files it
/// lists, and the library files it imports) into a <see cref="GridModel"/>. v1 case folders stay the default and are
/// untouched: this is a second door into the SAME model builder (<see cref="JsonGridLoader.Build"/>), so a v2 case whose
/// elements equal a v1 case's gives the same model, bit for bit (proved on every bundled case through its v2 projection,
/// V2CaseLoaderTests).
///
/// <para>What the load does, in order, failing loud with EVERY problem it finds (never only the first):</para>
/// <list type="number">
/// <item>The manifest and each network file are validated by <see cref="V2DocumentValidator"/> (case.schema.json and
/// network.schema.json): the same checks the <c>validate</c> verb runs. A network file must lie inside the manifest's
/// folder and may be listed once.</item>
/// <item>Each import names a library file relative to the data root (the shipped <c>data</c> folder). The library files
/// are validated together by <see cref="V2DocumentValidator"/> (library schema, kind specs, unique <c>id@rev</c>,
/// controller references). Each import pattern must match at least one template: a pinned <c>id@rev</c>, an unpinned
/// id (resolved to its highest revision in that file, and reported in the notes), an id prefix ending <c>.*</c>, or
/// <c>gridsim.*</c>.</item>
/// <item>Kind-spec enforcement: every local template's parameters are checked against its kind
/// (<see cref="KindSpecRegistry.CheckParams"/>: unknown key, type, unit, range, null rules), as library templates are.
/// A local template id may not shadow an imported id (any revision).</item>
/// <item>Overrides: each names a template the case can see, sets at least one parameter, says why (schema) and
/// acknowledges the shadowing (schema); a parameter may be overridden once. The merged parameters are checked against
/// the kind again. An overridden value takes the override's provenance, or, when it gives none, CuratedPlaceholder
/// with a [VERIFY] note quoting the reason (its source is not stated).</item>
/// <item>Every template an element or binding names must resolve, be active (a placeholder is never bound) and be of a
/// kind GridSim's asset classes model today (grid-following / grid-forming inverter, synchronous machine, oltc, line
/// type, protection).</item>
/// <item>The elements become the v1 DTOs and the model is built by <see cref="JsonGridLoader.Build"/>. Every active
/// template of a modelled kind becomes an asset class (local templates first, in manifest order, then imported ones
/// in import order). The class name of a local template <c>gridsim.local.NAME@1</c> is <c>NAME</c> (the v1
/// projection's rule, so a projected v1 case keeps its class names); every other template's class name is its
/// <c>id@rev</c>.</item>
/// </list>
///
/// <para>N234 (October 2026, LF2; JSON v2 phase P5 opened for fault sequence data ONLY, ruled by main under Mark's
/// delegation): the fault sequence data reaches the model. A generator's <c>subtransientXdPu</c> and
/// <c>negativeSeqXPu</c> become <see cref="Generator.SubtransientXdPu"/> / <see cref="Generator.NegativeSeqXPu"/>; a
/// transformer's <c>windings</c>, <c>r0Pu</c> / <c>x0Pu</c> and <c>neutralXPu</c> become
/// <see cref="Branch.FromWinding"/> / <see cref="Branch.ToWinding"/>, <see cref="Branch.R0"/> / <see cref="Branch.X0"/>
/// and <see cref="Branch.FromNeutralXPu"/> / <see cref="Branch.ToNeutralXPu"/>. They are checked for consistency (see
/// <c>CheckGeneratorSequence</c> / <c>CheckTransformerSequence</c>) and applied to the built model before dispatch;
/// a case that gives none of them builds exactly as before (the same object), so every existing number is unchanged.
/// v1 is unchanged: it has no keys for them, and the v1 writer reports them as content v1 cannot carry.</para>
///
/// <para>Phase 5a (2026-10-20): the asset overlay. A manifest may list <c>assets</c> files (gridsim-assets/2). Step 1
/// checks them as one set (<see cref="V2AssetChecker"/>, run by <see cref="V2DocumentValidator"/>: schema, overlay kind
/// specs, unique ids, parents, every reference resolving against the network files), so a bad overlay refuses the case
/// with every problem listed. The overlay is read only AFTER the model is complete, into
/// <see cref="V2CaseLoadResult.Overlay"/>, with the note "overlay: N records carried, not used by the solver". It never
/// reaches <see cref="JsonGridLoader.Build"/>: a case with an overlay gives exactly the model it gives without one. A
/// case without <c>assets</c> loads exactly as before (same model, same notes, Overlay null).</para>
///
/// <para>v2 content the model cannot carry yet is REFUSED, naming it, never dropped: line and transformer
/// <c>type</c>, an out-of-service line or transformer (later phases of the JSON v2 plan, held for Mark), a grid-code or
/// solver profile (likewise), controller references and non-numeric parameters on a template that becomes an asset
/// class (asset classes hold numbers only until templates are wired into their consumers). Descriptions are text and
/// are accepted.</para>
/// </summary>
public static class V2CaseLoader
{
    /// <summary>The manifest tag, matched exactly.</summary>
    public const string CaseSchemaTag = "gridsim-case/2";

    /// <summary>The network file tag, matched exactly.</summary>
    public const string NetworkSchemaTag = "gridsim-network/2";

    /// <summary>The id prefix of a case-local template whose revision-1 class name drops the prefix (the v1 projection's
    /// convention: a v1 class NAME projects to <c>gridsim.local.NAME@1</c>).</summary>
    public const string LocalIdPrefix = "gridsim.local.";

    /// <summary>The most problems listed in one refusal; the count of the rest is given.</summary>
    internal const int MaxListedProblems = 50;

    /// <summary>
    /// v2 template kind to the v1 asset-class kind spelling (the inverse of the v1 projection's table, plus nothing
    /// else): the kinds an asset class can be today. A template of any other kind (exciter, governor, storage, site,
    /// transformerType, ...) is validated but is not an asset class, and binding an element to it is refused.
    /// <para>N268 (October 2026), the v2 twin of D-DOC-149: <c>synchronousMachine</c> is no longer in this table. The v1
    /// asset-class library refuses a class of that kind (no controller exists for an authored machine class, see
    /// <see cref="GridSim.Core.Assets.AssetClassSupport.IsSupportedKind"/>), so mapping it here made every ACTIVE
    /// synchronousMachine template in a case (bound or not, local or imported) a refused asset class, while the v2
    /// schema and kind spec admit the template. It is now treated like exciter, governor and the other kinds no asset
    /// class models: the template is validated against its kind spec (so the schema, the editor and the loader agree
    /// that it is valid content), it does not become an asset class, and binding an element to it is refused at the
    /// binding, by name (<see cref="ClassNameFor"/>). A machine's inertia still comes from its generator's H and
    /// MBase.</para>
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> V1KindOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gridFollowingInverter"] = "gridFollowingInverter",
        ["gridFormingInverter"] = "gridFormingInverter",
        ["oltc"] = "transformer",
        ["lineType"] = "line",
        ["protection"] = "protection",
    };

    /// <summary>The provenance v1 gives a class parameter with neither its own entry nor a <c>_default</c>
    /// (<see cref="AssetClass.ProvenanceFor"/>). The v1 projection writes it as the required <c>_default</c> of a class
    /// that had none; a <c>_default</c> exactly equal to it is dropped again, so the asset class is the v1 one.</summary>
    /// <remarks>N321 (LF7): internal so the v2 writer writes exactly this entry as the required <c>_default</c> of a
    /// class that has none (and this loader drops it again on reading).</remarks>
    internal static readonly AssetClassProvenance V1Fallback =
        new AssetClass("fallback", AssetKind.Protection, "", new Dictionary<string, double>(), new Dictionary<string, AssetClassProvenance>())
            .ProvenanceFor("any");

    /// <summary>Load a v2 case; see the class summary. <paramref name="schemasDir"/> is the folder holding
    /// case.schema.json and the other v2 schema files, with the kind specs in its <c>kinds</c> subfolder.
    /// <paramref name="dataRoot"/> is the folder library imports are relative to (required only when the case imports).
    /// <paramref name="dispatch"/> as for <see cref="JsonGridLoader.LoadDirectory"/>.</summary>
    public static GridModel Load(string manifestPath, string schemasDir, string? dataRoot = null, bool dispatch = true)
        => LoadWithNotes(manifestPath, schemasDir, dataRoot, dispatch).Model;

    /// <summary><see cref="Load"/>, also returning the load's notes.</summary>
    public static V2CaseLoadResult LoadWithNotes(string manifestPath, string schemasDir, string? dataRoot = null, bool dispatch = true)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);
        ArgumentNullException.ThrowIfNull(schemasDir);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException($"JSON v2 case manifest not found: {manifestPath}", manifestPath);
        if (!Directory.Exists(schemasDir)) throw new DirectoryNotFoundException($"JSON v2 schema folder not found: {schemasDir}");
        string manifestFull = Path.GetFullPath(manifestPath);
        string caseDir = Path.GetDirectoryName(manifestFull)!;
        string shown = Path.GetFileName(manifestFull);
        var notes = new List<string>();

        // ---- The manifest must BE a v2 case manifest (a v1 case is a folder; any other tag is not a case). ----
        using (JsonDocument peek = JsonSchemaSubset.ParseFile(manifestFull))
        {
            JsonElement r = peek.RootElement;
            string? tag = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("schema", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (tag != CaseSchemaTag)
                throw new InvalidDataException(tag is null
                    ? $"{shown}: not a JSON v2 case manifest: it has no \"schema\": \"{CaseSchemaTag}\" tag (a v1 case is a folder of JSON files; give the folder)."
                    : $"{shown}: not a JSON v2 case manifest: its \"schema\" is '{tag}', not '{CaseSchemaTag}'.");
        }

        // ---- Step 0: every listed network file must lie inside the case folder BEFORE any of them is read. ----
        // Schema validation reads each listed file and quotes values from it in its errors, so a manifest listing
        // "../../somewhere/secrets.json" printed parts of that file before the inside-the-folder check (which ran
        // second) refused it. The paths are checked first, from the manifest alone; a manifest whose "network" is
        // not an array of strings is left to the schema check, which reports it.
        using (JsonDocument pre = JsonSchemaSubset.ParseFile(manifestFull))
        {
            if (pre.RootElement.ValueKind == JsonValueKind.Object
                && pre.RootElement.TryGetProperty("network", out JsonElement preNetwork)
                && preNetwork.ValueKind == JsonValueKind.Array)
            {
                var outside = new List<string>();
                int pi = 0;
                foreach (JsonElement n in preNetwork.EnumerateArray())
                {
                    if (n.ValueKind == JsonValueKind.String)
                    {
                        string rel = n.GetString()!;
                        string full = Path.GetFullPath(Path.Combine(caseDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!IsInside(caseDir, full))
                            outside.Add($"{shown}: /network/{pi}: network file '{rel}' lies outside the case folder ({caseDir}).");
                    }
                    pi++;
                }
                ThrowIfAny(shown, "the network file list", outside);
            }
            // Phase 5a: the asset overlay files likewise, before any of them is read.
            if (pre.RootElement.ValueKind == JsonValueKind.Object
                && pre.RootElement.TryGetProperty("assets", out JsonElement preAssets)
                && preAssets.ValueKind == JsonValueKind.Array)
            {
                var outside = new List<string>();
                int ai = 0;
                foreach (JsonElement n in preAssets.EnumerateArray())
                {
                    if (n.ValueKind == JsonValueKind.String)
                    {
                        string rel = n.GetString()!;
                        string full = Path.GetFullPath(Path.Combine(caseDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!IsInside(caseDir, full))
                            outside.Add($"{shown}: /assets/{ai}: asset overlay file '{rel}' lies outside the case folder ({caseDir}).");
                    }
                    ai++;
                }
                ThrowIfAny(shown, "the asset overlay file list", outside);
            }
        }

        // ---- Step 1: the manifest and its network files against the schemas (the validate verb's checks). ----
        var problems = new List<string>();
        foreach (V2DocumentReport rep in V2DocumentValidator.ValidateFiles(new[] { manifestFull }, schemasDir))
            foreach (SchemaError e in rep.Errors) problems.Add($"{Path.GetFileName(rep.Path)}: {e}");
        ThrowIfAny(shown, "the schema checks", problems);

        using JsonDocument manifestDoc = JsonSchemaSubset.ParseFile(manifestFull);
        JsonElement manifest = manifestDoc.RootElement;
        string caseName = manifest.GetProperty("name").GetString()!;

        // Network files: inside the case folder, each listed once.
        var networkPaths = new List<(string Rel, string Full)>();
        int ni = 0;
        foreach (JsonElement n in manifest.GetProperty("network").EnumerateArray())
        {
            string rel = n.GetString()!;
            string full = Path.GetFullPath(Path.Combine(caseDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(caseDir, full)) problems.Add($"{shown}: /network/{ni}: network file '{rel}' lies outside the case folder ({caseDir}).");
            else if (networkPaths.Any(p => string.Equals(p.Full, full, FilePathComparison.Comparison)))
                problems.Add($"{shown}: /network/{ni}: network file '{rel}' is listed more than once.");
            else networkPaths.Add((rel, full));
            ni++;
        }
        ThrowIfAny(shown, "the network file list", problems);

        JsonSchemaSubset schemas = JsonSchemaSubset.LoadDirectory(schemasDir);
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(Path.Combine(schemasDir, "kinds"), schemas);

        // ---- Step 2: imports. ----
        var templates = new TemplateIndex();
        if (manifest.TryGetProperty("imports", out JsonElement imports) && imports.GetArrayLength() > 0)
        {
            if (dataRoot is null)
                throw new InvalidDataException($"{shown}: the case imports library templates, whose files are named relative to the data root, but no data root was given.");
            if (!Directory.Exists(dataRoot)) throw new DirectoryNotFoundException($"JSON v2 data root not found: {dataRoot}");
            string rootFull = Path.GetFullPath(dataRoot);
            var libraryFiles = new List<(string Rel, string Full)>();
            int ii = 0;
            foreach (JsonElement imp in imports.EnumerateArray())
            {
                string rel = imp.GetProperty("library").GetString()!;
                string full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsInside(rootFull, full)) problems.Add($"{shown}: /imports/{ii}/library: '{rel}' lies outside the data root ({rootFull}).");
                else if (!File.Exists(full)) problems.Add($"{shown}: /imports/{ii}/library: library file '{rel}' does not exist (looked for {full}).");
                else if (!libraryFiles.Any(l => string.Equals(l.Full, full, FilePathComparison.Comparison))) libraryFiles.Add((rel, full));
                ii++;
            }
            ThrowIfAny(shown, "the import list", problems);

            // The library files as one set: schema, kind specs, unique id@rev, controller references (the validator's rules).
            foreach (V2DocumentReport rep in V2DocumentValidator.ValidateFiles(libraryFiles.Select(l => l.Full).ToList(), schemasDir))
            {
                if (rep.SchemaTag != V2LibraryChecker.SchemaTag)
                    problems.Add($"{Path.GetFileName(rep.Path)}: imported as a library, but its \"schema\" is '{rep.SchemaTag ?? "(none)"}', not '{V2LibraryChecker.SchemaTag}'.");
                foreach (SchemaError e in rep.Errors) problems.Add($"{Path.GetFileName(rep.Path)}: {e}");
            }
            ThrowIfAny(shown, "the imported library files", problems);

            // Every template of every library file, then the patterns of each import.
            var byFile = new Dictionary<string, List<Template>>(FilePathComparison.Comparer);
            foreach ((string rel, string full) in libraryFiles)
            {
                using JsonDocument lib = JsonSchemaSubset.ParseFile(full);
                int ti = 0;
                var list = new List<Template>();
                foreach (JsonElement t in lib.RootElement.GetProperty("templates").EnumerateArray())
                    list.Add(Template.From(t.Clone(), $"library {rel}, /templates/{ti++}", local: false));
                byFile[full] = list;
            }
            ii = 0;
            foreach (JsonElement imp in imports.EnumerateArray())
            {
                string rel = imp.GetProperty("library").GetString()!;
                string full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
                List<Template> available = byFile[full];
                int pi = 0;
                foreach (JsonElement pat in imp.GetProperty("templates").EnumerateArray())
                {
                    string pattern = pat.GetString()!;
                    List<Template> hits = Match(available, pattern, out string? resolvedNote);
                    if (hits.Count == 0) problems.Add($"{shown}: /imports/{ii}/templates/{pi}: pattern '{pattern}' matches no template in '{rel}'.");
                    if (resolvedNote is not null) notes.Add($"import '{pattern}' from '{rel}' resolved to {resolvedNote} (the highest revision there).");
                    foreach (Template t in hits) templates.AddImported(t);
                    pi++;
                }
                ii++;
            }
            ThrowIfAny(shown, "the import patterns", problems);
        }

        // ---- Step 3: local templates (kind specs, provenance keys, no shadowing, unique id@rev). ----
        if (manifest.TryGetProperty("localTemplates", out JsonElement locals))
        {
            int li = 0;
            foreach (JsonElement t in locals.EnumerateArray())
            {
                string at = $"/localTemplates/{li++}";
                var tpl = Template.From(t.Clone(), $"{shown}, {at}", local: true);
                foreach (SchemaError e in kinds.CheckParams(tpl.Kind, tpl.Params, tpl.Placeholder, at + "/params")) problems.Add($"{shown}: {e}");
                foreach (JsonProperty prov in tpl.Provenance.EnumerateObject())
                    if (prov.Name != AssetClass.DefaultKey && !tpl.Params.TryGetProperty(prov.Name, out _))
                        problems.Add($"{shown}: {at}/provenance/{JsonSchemaSubset.EscapeToken(prov.Name)}: provenance is given for '{prov.Name}', which is not a parameter of this template.");
                if (templates.ImportedIds.Contains(tpl.Id))
                    problems.Add($"{shown}: {at}/id: local template '{tpl.Id}' has the id of an imported template; a local template may not shadow an import (use an override to change an imported value).");
                else if (!templates.AddLocal(tpl))
                    problems.Add($"{shown}: {at}: local template {tpl.Ref} is defined twice.");
            }
        }
        ThrowIfAny(shown, "the local templates", problems);

        // Controller references of local templates resolve like the library checker's (library templates were checked
        // within their own set by the validator above).
        foreach (Template tpl in templates.All.Where(t => t.Local && t.Controllers is not null))
        {
            foreach (JsonProperty slot in tpl.Controllers!.Value.EnumerateObject())
            {
                if (slot.Value.ValueKind == JsonValueKind.Null) continue;
                string target = slot.Value.GetString()!;
                Template? hit = templates.Find(target);
                if (hit is null) problems.Add($"{tpl.Where}: controller reference {target} does not resolve to a template of the case (local or imported).");
                else if (hit.Placeholder) problems.Add($"{tpl.Where}: controller reference {target} is a placeholder template; a placeholder can never be a controller target.");
                else if (hit.Kind != slot.Name) problems.Add($"{tpl.Where}: the '{slot.Name}' slot must reference a '{slot.Name}' template, but {target} is kind '{hit.Kind}'.");
            }
        }
        ThrowIfAny(shown, "the template controller references", problems);

        // ---- Step 4: overrides. ----
        if (manifest.TryGetProperty("overrides", out JsonElement overrides))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int oi = 0;
            foreach (JsonElement ov in overrides.EnumerateArray())
            {
                string at = $"/overrides/{oi++}";
                string target = ov.GetProperty("template").GetString()!;
                string reason = ov.GetProperty("reason").GetString()!;
                Template? tpl = templates.Find(target);
                if (tpl is null) { problems.Add($"{shown}: {at}/template: {target} is neither imported nor a local template of the case."); continue; }
                if (tpl.Placeholder) { problems.Add($"{shown}: {at}/template: {target} is a placeholder template; a placeholder holds no values, so none can be overridden."); continue; }
                JsonElement ps = ov.GetProperty("params");
                if (!ps.EnumerateObject().Any()) { problems.Add($"{shown}: {at}/params: an override must set at least one parameter."); continue; }
                bool clash = false;
                foreach (JsonProperty p in ps.EnumerateObject())
                    if (!seen.Add(target + "\n" + p.Name))
                    {
                        problems.Add($"{shown}: {at}/params/{JsonSchemaSubset.EscapeToken(p.Name)}: '{p.Name}' of {target} is overridden more than once.");
                        clash = true;
                    }
                if (clash) continue;
                JsonElement? prov = ov.TryGetProperty("provenance", out JsonElement pv) ? pv.Clone() : (JsonElement?)null;
                JsonElement merged = tpl.WithOverrides(ps);
                List<SchemaError> errs = kinds.CheckParams(tpl.Kind, merged, placeholder: false, at + "/params").ToList();
                foreach (SchemaError e in errs) problems.Add($"{shown}: {e} (after applying the override to {target})");
                if (errs.Count == 0) tpl.ApplyOverride(merged, ps, prov, caseName, reason);
            }
        }
        ThrowIfAny(shown, "the overrides", problems);

        // ---- Step 5 / 6: map the network files to v1 DTOs, resolving every template reference. ----
        var dtos = new MappedCase();
        foreach ((string rel, string full) in networkPaths)
        {
            using JsonDocument net = JsonSchemaSubset.ParseFile(full);
            MapNetwork(net.RootElement, rel, dtos, templates, problems);
        }
        if (manifest.TryGetProperty("bindings", out JsonElement bindings))
        {
            int bi = 0;
            foreach (JsonElement b in bindings.EnumerateArray())
            {
                string at = $"{shown}: /bindings/{bi++}/template";
                string? cls = ClassNameFor(b.GetProperty("template").GetString()!, at, templates, problems);
                if (cls is not null) dtos.AssetBindings.Add(new AssetBindingDto { MatchType = b.GetProperty("matchType").GetString()!, Class = cls });
            }
        }
        // Unique bus ids across every network file (the v1 builder keys buses by id and would quietly keep the last).
        foreach (IGrouping<int, BusDto> g in dtos.Buses.GroupBy(b => b.Id).Where(g => g.Count() > 1))
            problems.Add($"{shown}: bus id {g.Key.ToString(CultureInfo.InvariantCulture)} is used by {g.Count().ToString(CultureInfo.InvariantCulture)} buses; ids must be unique across every network file.");
        foreach (IGrouping<string, BoundaryDto> g in dtos.Boundaries.GroupBy(b => b.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"{shown}: {g.Count().ToString(CultureInfo.InvariantCulture)} boundaries are named '{g.Key}'; boundary names must be unique.");

        // Placeholder / unmodelled-kind refusals and other v2-only content were collected by the mapper above.
        ThrowIfAny(shown, "the network elements", problems);

        // Every active template of a modelled kind becomes an asset class: local ones first (manifest order), then
        // the imported ones (import order).
        foreach (Template tpl in templates.All.Where(t => !t.Placeholder && V1KindOf.ContainsKey(t.Kind)))
        {
            AssetClassDto? c = tpl.ToAssetClass(problems);
            if (c is not null) dtos.AssetClasses.Add(c);
        }
        ThrowIfAny(shown, "the templates that become asset classes", problems);

        // gridCodeProfile / solverProfile: not bound from a case yet (a later phase of the JSON v2 plan, P6, held for
        // Mark); refused rather than silently ignored. D-DOC-162 (DOC-17): the message used to quote the plan's wording
        // ("a later, Mark-gated phase of JSON v2 (P6)"); it now says what a user can act on.
        foreach (string profile in new[] { "gridCodeProfile", "solverProfile" })
            if (manifest.TryGetProperty(profile, out _))
                problems.Add($"{shown}: /{profile}: GridSim does not yet bind a {profile} from a case (the built-in settings apply); remove it to load this case today.");
        ThrowIfAny(shown, "the case profiles", problems);

        JsonElement system = manifest.GetProperty("system");
        var caseDtos = new CaseDtos
        {
            System = new SystemDto
            {
                Name = caseName,
                BaseMva = system.GetProperty("baseMva").GetDouble(),
                FrequencyHz = system.GetProperty("frequencyHz").GetDouble(),
                Dispatch = system.TryGetProperty("dispatch", out JsonElement d) ? d.GetBoolean() : null,
            },
            Buses = dtos.Buses,
            Generators = dtos.Generators,
            Demands = dtos.Demands,
            Lines = dtos.Lines,
            Transformers = dtos.Transformers,
            Oltcs = dtos.Oltcs,
            AssetClasses = dtos.AssetClasses,
            AssetBindings = dtos.AssetBindings,
            Protections = dtos.Protections,
            Boundaries = () => dtos.Boundaries,
        };
        GridModel model;
        try
        {
            // N234: build WITHOUT dispatch, put the fault sequence data in, then dispatch exactly as the builder would
            // have (JsonGridLoader.Build ends with "dispatch && (system dispatch ?? true) ? Dispatcher.Balance(model) :
            // model"). With no sequence data ApplySequenceData returns the built model itself, so the result is the very
            // model the single Build call gave before N234, bit for bit. Applying the data before the balance keeps the
            // generators' order (the balance works on the in-service fleet and re-appends any out-of-service unit).
            GridModel built = JsonGridLoader.Build(caseDtos, Path.GetFileNameWithoutExtension(manifestFull), dispatch: false);
            built = ApplySequenceData(built, dtos);
            model = dispatch && (caseDtos.System?.Dispatch ?? true) ?GridSim.Core.Dispatch.Dispatcher.Balance(built) : built;
        }
        catch (InvalidDataException ex)
        {
            // The shared builder names the v1 file each rule comes from; say so, so a v2 author can map it back.
            throw new InvalidDataException(
                $"{shown}: the case fails a check of the model builder it shares with v1 cases (file names in the message are the v1 " +
                "equivalents: buses.json = network buses, generation.json = generators, demand.json = demands, transmission.json = " +
                "lines, transformer.json = transformers, onloadtapchanger.json = oltcs, boundaries.json = boundaries, protections.json = " +
                "protections, asset-classes.json = templates, asset-bindings.json = bindings): " + ex.Message, ex);
        }

        // ---- Phase 5a: the asset overlay, read only now that the model is complete. ----
        // Step 1 has already checked the overlay files as one set against the network files (V2DocumentValidator runs
        // V2AssetChecker on every file the manifest lists in "assets"), so they are read here as checked documents.
        // Nothing below touches the model: the overlay is carried beside it on the load result and is never an input
        // to the builder or to any solver. Without "assets" the result is exactly the pre-5a result (Overlay null, no
        // extra note).
        if (!manifest.TryGetProperty("assets", out JsonElement assetList))
            return new V2CaseLoadResult(model, notes);
        var assetDocs = new List<(string Name, JsonElement Root)>();
        foreach (JsonElement a in assetList.EnumerateArray())
        {
            string rel = a.GetString()!;
            using JsonDocument doc = JsonSchemaSubset.ParseFile(Path.GetFullPath(Path.Combine(caseDir, rel.Replace('/', Path.DirectorySeparatorChar))));
            assetDocs.Add((rel, doc.RootElement.Clone()));
        }
        CaseAssetOverlay overlay = CaseAssetOverlay.FromCheckedDocuments(assetDocs);
        int count = overlay.Records.Count;
        notes.Add($"overlay: {count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "record" : "records")} carried, not used by the solver");
        return new V2CaseLoadResult(model, notes) { Overlay = overlay };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Templates
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>One template the case can see (local or imported), with its parameters after any override.</summary>
    private sealed class Template
    {
        public required string Id { get; init; }
        public required int Rev { get; init; }
        public required string Kind { get; init; }
        public required bool Placeholder { get; init; }
        public required bool Local { get; init; }
        public required string Where { get; init; }
        public string? Description { get; init; }
        public JsonElement Params { get; private set; }
        public JsonElement Provenance { get; init; }
        public JsonElement? Controllers { get; init; }

        /// <summary>Per-parameter provenance set by an override (wins over the template's own entry).</summary>
        public Dictionary<string, ProvenanceDto> OverrideProvenance { get; } = new(StringComparer.Ordinal);

        public string Ref => Id + "@" + Rev.ToString(CultureInfo.InvariantCulture);

        /// <summary>The asset-class name: NAME for a local gridsim.local.NAME@1 (the v1 projection's rule), else id@rev.</summary>
        public string ClassName => Local && Rev == 1 && Id.StartsWith(LocalIdPrefix, StringComparison.Ordinal) ? Id[LocalIdPrefix.Length..] : Ref;

        public static Template From(JsonElement t, string where, bool local) => new()
        {
            Id = t.GetProperty("id").GetString()!,
            // The schema's integer type also admits a written 1.0; the revision is its whole-number value.
            Rev = (int)t.GetProperty("rev").GetDouble(),
            Kind = t.GetProperty("kind").GetString()!,
            Placeholder = t.GetProperty("status").GetString() == "placeholder",
            Local = local,
            Where = where,
            Description = t.TryGetProperty("description", out JsonElement d) ? d.GetString() : null,
            Params = t.GetProperty("params"),
            Provenance = t.GetProperty("provenance"),
            Controllers = t.TryGetProperty("controllers", out JsonElement c) ? c : null,
        };

        /// <summary>The parameters with <paramref name="overrides"/> applied (template order, new keys appended).</summary>
        public JsonElement WithOverrides(JsonElement overrides)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                foreach (JsonProperty p in Params.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    (overrides.TryGetProperty(p.Name, out JsonElement o) ? o : p.Value).WriteTo(w);
                }
                foreach (JsonProperty o in overrides.EnumerateObject())
                    if (!Params.TryGetProperty(o.Name, out _)) { w.WritePropertyName(o.Name); o.Value.WriteTo(w); }
                w.WriteEndObject();
            }
            using JsonDocument doc = JsonDocument.Parse(buffer.WrittenMemory);
            return doc.RootElement.Clone();
        }

        /// <summary>Adopt the merged parameters and record the provenance of each overridden one.</summary>
        public void ApplyOverride(JsonElement merged, JsonElement overrides, JsonElement? provenance, string caseName, string reason)
        {
            Params = merged;
            foreach (JsonProperty o in overrides.EnumerateObject())
                OverrideProvenance[o.Name] = provenance is JsonElement p
                    ? new ProvenanceDto { Tag = p.GetProperty("tag").GetString()!, Note = p.GetProperty("note").GetString()! }
                    // No provenance given: the value's source is not stated, so it is a curated placeholder by the
                    // honesty rule, with the reason the case gave.
                    : new ProvenanceDto { Tag = nameof(Diagnostics.ProvenanceTag.CuratedPlaceholder), Note = $"[VERIFY] set by an override in case '{caseName}': {reason}" };
        }

        /// <summary>The v1 asset class of an active template of a modelled kind, or null (with problems added) when
        /// it holds something an asset class cannot (a non-numeric value, controller references).</summary>
        public AssetClassDto? ToAssetClass(List<string> problems)
        {
            bool ok = true;
            if (Controllers is JsonElement ctl && ctl.EnumerateObject().Any(s => s.Value.ValueKind != JsonValueKind.Null))
            {
                problems.Add($"{Where}: template {Ref} has controller references; an asset class cannot carry them, so they would be dropped.");   // D-DOC-162: no plan wording
                ok = false;
            }
            var ps = new Dictionary<string, double>(StringComparer.Ordinal);
            var prov = new Dictionary<string, ProvenanceDto>(StringComparer.Ordinal);
            foreach (JsonProperty p in Params.EnumerateObject())
            {
                JsonElement v = p.Value;
                if (v.ValueKind == JsonValueKind.Object)
                {
                    if (v.TryGetProperty("prov", out JsonElement pv))
                        prov[p.Name] = new ProvenanceDto { Tag = pv.GetProperty("tag").GetString()!, Note = pv.GetProperty("note").GetString()! };
                    v = v.GetProperty("v");
                }
                if (v.ValueKind != JsonValueKind.Number)
                {
                    problems.Add($"{Where}: template {Ref} parameter '{p.Name}' is {v.ValueKind.ToString().ToLowerInvariant()} {v.GetRawText()}; an asset class holds numbers only.");   // D-DOC-162: no plan wording
                    ok = false;
                    continue;
                }
                ps[p.Name] = v.GetDouble();
            }
            if (!ok) return null;

            // Provenance: the template's own map (dropping a _default that is exactly v1's fallback, so a projected v1
            // class is the v1 class), then the object-form and override entries on top.
            var map = new Dictionary<string, ProvenanceDto>(StringComparer.Ordinal);
            foreach (JsonProperty e in Provenance.EnumerateObject())
            {
                string tag = e.Value.GetProperty("tag").GetString()!, note = e.Value.GetProperty("note").GetString()!;
                if (e.Name == AssetClass.DefaultKey && tag == V1Fallback.Tag.ToString() && note == V1Fallback.Note) continue;
                map[e.Name] = new ProvenanceDto { Tag = tag, Note = note };
            }
            foreach ((string k, ProvenanceDto v) in prov) map[k] = v;
            foreach ((string k, ProvenanceDto v) in OverrideProvenance) map[k] = v;

            return new AssetClassDto { Name = ClassName, Kind = V1KindOf[Kind], Mode = Description ?? "", Params = ps, Provenance = map };
        }
    }

    /// <summary>The templates the case can see, in order: local ones (manifest order) and imported ones (import order).</summary>
    private sealed class TemplateIndex
    {
        private readonly Dictionary<string, Template> _byRef = new(StringComparer.Ordinal);
        private readonly List<Template> _locals = new();
        private readonly List<Template> _imported = new();
        public HashSet<string> ImportedIds { get; } = new(StringComparer.Ordinal);

        public IEnumerable<Template> All => _locals.Concat(_imported);

        public void AddImported(Template t)
        {
            if (_byRef.ContainsKey(t.Ref)) return;   // the same template matched by two patterns: imported once
            _byRef[t.Ref] = t;
            _imported.Add(t);
            ImportedIds.Add(t.Id);
        }

        public bool AddLocal(Template t)
        {
            if (!_byRef.TryAdd(t.Ref, t)) return false;
            _locals.Add(t);
            return true;
        }

        public Template? Find(string reference) => _byRef.GetValueOrDefault(reference);
    }

    /// <summary>The templates of one library file an import pattern takes (see case.schema.json templatePattern).
    /// <paramref name="resolved"/> names the revision an unpinned id resolved to, for the notes.</summary>
    private static List<Template> Match(List<Template> available, string pattern, out string? resolved)
    {
        resolved = null;
        if (pattern == "gridsim.*") return available.ToList();
        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            string prefix = pattern[..^1];   // keeps the trailing dot: gridsim.exciter.* takes gridsim.exciter.<anything>
            return available.Where(t => t.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }
        if (pattern.Contains('@', StringComparison.Ordinal)) return available.Where(t => t.Ref == pattern).ToList();
        List<Template> sameId = available.Where(t => t.Id == pattern).ToList();
        if (sameId.Count == 0) return sameId;
        Template top = sameId.OrderByDescending(t => t.Rev).First();
        resolved = top.Ref;
        return new List<Template> { top };
    }

    /// <summary>The asset-class name for a template reference an element or binding makes, or null (problem added)
    /// when it does not resolve, is a placeholder, or is of a kind no asset class models.</summary>
    private static string? ClassNameFor(string reference, string at, TemplateIndex templates, List<string> problems)
    {
        Template? t = templates.Find(reference);
        if (t is null) { problems.Add($"{at}: template {reference} is neither imported nor a local template of the case."); return null; }
        if (t.Placeholder) { problems.Add($"{at}: template {reference} is a placeholder; a placeholder holds no values and can never be bound to an element."); return null; }
        if (!V1KindOf.ContainsKey(t.Kind))
        {
            problems.Add($"{at}: template {reference} is kind '{t.Kind}'; an element or binding can be bound only to a template of a kind GridSim's asset classes model today ({string.Join(", ", V1KindOf.Keys)}).");
            return null;
        }
        return t.ClassName;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Network elements
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The v1 DTO lists the network files map to (their union, in file order).</summary>
    private sealed class MappedCase
    {
        public List<BusDto> Buses { get; } = new();
        public List<GenDto> Generators { get; } = new();
        public List<DemandDto> Demands { get; } = new();
        public List<LineDto> Lines { get; } = new();
        public List<TransformerDto> Transformers { get; } = new();
        public List<OltcDto> Oltcs { get; } = new();
        public List<BoundaryDto> Boundaries { get; } = new();
        public List<ProtectionInstanceDto> Protections { get; } = new();
        public List<AssetClassDto> AssetClasses { get; } = new();
        public List<AssetBindingDto> AssetBindings { get; } = new();

        /// <summary>N234: the fault sequence data of each generator, index-aligned with <see cref="Generators"/> (null
        /// where the element gives none).</summary>
        public List<GeneratorSequenceData?> GeneratorSequence { get; } = new();

        /// <summary>N234: the fault sequence data of each transformer, index-aligned with <see cref="Transformers"/>
        /// (null where the element gives none).</summary>
        public List<TransformerSequenceData?> TransformerSequence { get; } = new();
    }

    /// <summary>N234: a generator's fault sequence data as written (pu on the unit rating, MBase); null = not given.</summary>
    private sealed record GeneratorSequenceData(double? SubtransientXdPu, double? NegativeSeqXPu);

    /// <summary>N234: a transformer's fault sequence data as written (impedances pu on the case base); null = not given.</summary>
    private sealed record TransformerSequenceData(
        double? R0Pu, double? X0Pu, WindingConnection? FromWinding, WindingConnection? ToWinding,
        double? FromNeutralXPu, double? ToNeutralXPu);

    // A refusal of v2 content the model cannot carry yet, naming the file, the element and the phase that adds it.
    private static string NotYet(string at, string what, string when)
        => $"{at}: {what} is valid JSON v2, but GridSim's model does not carry it yet ({when}); remove it to load this case today.";

    // N234 (October 2026): P5 is open for fault sequence data only (generator subtransientXdPu / negativeSeqXPu,
    // transformer windings / r0Pu / x0Pu / neutralXPu); every other network field it covers is still to come (later
    // phases of the JSON v2 plan, P3 to P6). D-DOC-162 (DOC-17): the reasons used to quote that plan wording ("a later,
    // Mark-gated phase of JSON v2 (P5 is open for sequence data only)", "(P3 / P4)"), which means nothing to a user;
    // they now say what the loader reads today.
    private const string LaterNetworkPhase =
        "of the network fields v1 cannot hold, the v2 loader reads only fault sequence data so far";
    private const string LaterTypePhase = "the v2 loader does not bind line or transformer types yet";

    private static void MapNetwork(JsonElement root, string file, MappedCase into, TemplateIndex templates, List<string> problems)
    {
        foreach (JsonProperty arr in root.EnumerateObject())
        {
            if (arr.Name is "schema" or "description") continue;   // the tag (validated) and free text
            int i = 0;
            foreach (JsonElement e in arr.Value.EnumerateArray())
            {
                string at = $"{file}: /{arr.Name}/{i++}";
                switch (arr.Name)
                {
                    case "buses": into.Buses.Add(MapBus(e)); break;
                    case "generators":
                        into.Generators.Add(MapGenerator(e, at, templates, problems, out GeneratorSequenceData? gs));
                        into.GeneratorSequence.Add(gs);
                        break;
                    case "demands": into.Demands.Add(MapDemand(e)); break;
                    case "lines": into.Lines.Add(MapLine(e, at, templates, problems)); break;
                    case "transformers":
                        into.Transformers.Add(MapTransformer(e, at, templates, problems, out TransformerSequenceData? ts));
                        into.TransformerSequence.Add(ts);
                        break;
                    case "oltcs": into.Oltcs.Add(MapOltc(e)); break;
                    case "boundaries": into.Boundaries.Add(MapBoundary(e)); break;
                    case "protections": into.Protections.Add(MapProtection(e, at, templates, problems)); break;
                    // The schema refuses any other member; this is a second, independent net.
                    default: throw new InvalidDataException($"{file}: unexpected network member '{arr.Name}'.");
                }
            }
        }
    }

    // A v2 limit may be a number or the string "Infinity" / "-Infinity" (N101), as in v1.
    private static double Limit(JsonElement v) => v.ValueKind == JsonValueKind.String
        ? v.GetString() switch
        {
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            var s => throw new InvalidDataException($"limit '{s}' is not a number, \"Infinity\" or \"-Infinity\"."),
        }
        : v.GetDouble();

    private static BusDto MapBus(JsonElement e)
    {
        var b = new BusDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "id": b.Id = JsonWhole.Int(p); break;
                case "name": b.Name = p.Value.GetString(); break;
                case "baseKv": b.BaseKv = p.Value.GetDouble(); break;
                case "type": b.Type = p.Value.GetString()!; break;
                case "vm": b.Vm = p.Value.GetDouble(); break;
                case "vaDeg": b.VaDeg = p.Value.GetDouble(); break;
                case "gs": b.Gs = p.Value.GetDouble(); break;
                case "bs": b.Bs = p.Value.GetDouble(); break;
                case "x": b.X = p.Value.GetDouble(); break;
                case "y": b.Y = p.Value.GetDouble(); break;
                case "vminPu": b.VminPu = p.Value.GetDouble(); break;
                case "vmaxPu": b.VmaxPu = p.Value.GetDouble(); break;
                case "zip":
                    var z = new BusZipDto();
                    foreach (JsonProperty c in p.Value.EnumerateObject())
                    {
                        double v = c.Value.GetDouble();
                        switch (c.Name)
                        {
                            case "ap": z.Ap = v; break;
                            case "bp": z.Bp = v; break;
                            case "cp": z.Cp = v; break;
                            case "aq": z.Aq = v; break;
                            case "bq": z.Bq = v; break;
                            case "cq": z.Cq = v; break;
                            default: throw new InvalidDataException($"zip: unexpected member '{c.Name}'.");
                        }
                    }
                    b.Zip = z;
                    break;
                default: throw new InvalidDataException($"bus: unexpected member '{p.Name}'.");
            }
        }
        return b;
    }

    private static GenDto MapGenerator(JsonElement e, string at, TemplateIndex templates, List<string> problems,
        out GeneratorSequenceData? sequence)
    {
        var g = new GenDto();
        double? xdpp = null, x2 = null;   // N234: the fault sequence data, when given
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": g.Name = p.Value.GetString(); break;
                case "bus": g.Bus = JsonWhole.Int(p); break;
                case "type": g.Type = p.Value.GetString()!; break;
                case "template": g.DynamicsClass = ClassNameFor(p.Value.GetString()!, at + "/template", templates, problems); break;
                case "capacityMva": g.CapacityMva = p.Value.GetDouble(); break;
                case "outputMw": g.OutputMw = p.Value.GetDouble(); break;
                case "capacityFactor": g.CapacityFactor = p.Value.GetDouble(); break;
                case "voltageSetpoint": g.VoltageSetpoint = p.Value.GetDouble(); break;
                case "qMinMvar": g.QMinMvar = Limit(p.Value); break;
                case "qMaxMvar": g.QMaxMvar = Limit(p.Value); break;
                case "inertiaH": g.InertiaH = p.Value.GetDouble(); break;
                case "role": g.Role = p.Value.GetString()!; break;
                case "powerFlowOnly": g.PowerFlowOnly = p.Value.GetBoolean(); break;
                case "unitTransformerXPu": g.UnitTransformerXPu = p.Value.GetDouble(); break;
                case "inService": g.InService = p.Value.GetBoolean(); break;
                case "pMinMw": g.PMinMw = p.Value.GetDouble(); break;
                case "pMaxMw": g.PMaxMw = Limit(p.Value); break;
                case "qOutputMvar": g.QOutputMvar = p.Value.GetDouble(); break;
                case "converter":
                    foreach (JsonProperty c in p.Value.EnumerateObject())
                    {
                        switch (c.Name)
                        {
                            case "interfaced": g.ConverterInterfaced = c.Value.GetBoolean(); break;
                            case "faultCurrentPu": g.ConverterFaultCurrentPu = c.Value.GetDouble(); break;
                            case "gridForming": g.GridForming = c.Value.GetBoolean(); break;
                            default: throw new InvalidDataException($"converter: unexpected member '{c.Name}'.");
                        }
                    }
                    break;
                // N234: P5 (sequence data) is open; these reach the model (applied after the build).
                case "subtransientXdPu": xdpp = p.Value.GetDouble(); break;
                case "negativeSeqXPu": x2 = p.Value.GetDouble(); break;
                default: throw new InvalidDataException($"generator: unexpected member '{p.Name}'.");
            }
        }
        sequence = xdpp is null && x2 is null ? null : new GeneratorSequenceData(xdpp, x2);
        if (sequence is not null) CheckGeneratorSequence(g, sequence, at, problems);
        return g;
    }

    /// <summary>
    /// N234: refuse generator sequence data that the fault studies would not use or could not mean, naming the element
    /// (fail loud: a value that is silently ignored is a wrong answer waiting to happen).
    /// <list type="bullet">
    /// <item>Each value must be finite and positive (the schema's exclusiveMinimum 0 says so too; checked again here so
    /// the loader does not rest on the schema alone). 0 is the model's "not supplied", so it is not a value one can
    /// write.</item>
    /// <item>A converter-interfaced unit (converter.interfaced true, or not given and the unit's type is a converter
    /// type) has no subtransient reactance: IEC 60909-0:2016 models it as a current source of k x its rated current
    /// (converter.faultCurrentPu), and GridSim's fault studies do exactly that, so an X''d or X2 on it would never be
    /// read. Refused, naming the converter field to use instead.</item>
    /// </list>
    /// </summary>
    private static void CheckGeneratorSequence(GenDto g, GeneratorSequenceData s, string at, List<string> problems)
    {
        foreach ((string key, double? v) in new[] { ("subtransientXdPu", s.SubtransientXdPu), ("negativeSeqXPu", s.NegativeSeqXPu) })
            if (v is double x && !(double.IsFinite(x) && x > 0.0))
                problems.Add($"{at}/{key}: {x.ToString("R", CultureInfo.InvariantCulture)} is not a positive finite reactance (pu on the unit rating).");
        bool converter = g.ConverterInterfaced ?? ConverterPlant.IsConverterType(g.Type);
        if (converter)
            problems.Add($"{at}: unit '{g.Name}' is converter-interfaced"
                + (g.ConverterInterfaced is null ? $" (its type '{g.Type}' is a converter type)" : "")
                + ", so the fault studies model it as a current source (converter.faultCurrentPu, IEC 60909-0:2016) and never read a "
                + "subtransientXdPu or negativeSeqXPu; remove them, or set converter.interfaced to false if it is a synchronous machine.");
    }

    private static DemandDto MapDemand(JsonElement e)
    {
        var d = new DemandDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": d.Name = p.Value.GetString(); break;
                case "bus": d.Bus = JsonWhole.Int(p); break;
                case "pMw": d.PMw = p.Value.GetDouble(); break;
                case "qMvar": d.QMvar = p.Value.GetDouble(); break;
                case "powerFactor": d.PowerFactor = p.Value.GetDouble(); break;
                default: throw new InvalidDataException($"demand: unexpected member '{p.Name}'.");
            }
        }
        return d;
    }

    private static LineDto MapLine(JsonElement e, string at, TemplateIndex templates, List<string> problems)
    {
        var l = new LineDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": l.Name = p.Value.GetString(); break;
                case "from": l.From = JsonWhole.Int(p); break;
                case "to": l.To = JsonWhole.Int(p); break;
                case "template": l.DynamicsClass = ClassNameFor(p.Value.GetString()!, at + "/template", templates, problems); break;
                case "rPu": l.RPu = p.Value.GetDouble(); break;
                case "xPu": l.XPu = p.Value.GetDouble(); break;
                case "bPu": l.BPu = p.Value.GetDouble(); break;
                case "ratingMva": l.RatingMva = p.Value.GetDouble(); break;
                case "circuits": l.Circuits = JsonWhole.Int(p); break;
                case "lengthKm": l.LengthKm = p.Value.GetDouble(); break;
                case "equipment": l.Equipment = p.Value.GetString(); break;
                case "r0Pu": l.RPu0 = p.Value.GetDouble(); break;
                case "x0Pu": l.XPu0 = p.Value.GetDouble(); break;
                case "tapRatio": l.TapRatio = p.Value.GetDouble(); break;
                case "phaseShiftDeg": l.PhaseShiftDeg = p.Value.GetDouble(); break;
                case "isTransformer": l.IsTransformer = p.Value.GetBoolean(); break;
                case "ratingBMva": l.RatingBMva = p.Value.GetDouble(); break;
                case "ratingCMva": l.RatingCMva = p.Value.GetDouble(); break;
                case "type": problems.Add(NotYet(at + "/type", "a line type", LaterTypePhase)); break;
                case "inService": if (!p.Value.GetBoolean()) problems.Add(NotYet(at + "/inService", "an out-of-service line", LaterNetworkPhase)); break;
                default: throw new InvalidDataException($"line: unexpected member '{p.Name}'.");
            }
        }
        return l;
    }

    private static TransformerDto MapTransformer(JsonElement e, string at, TemplateIndex templates, List<string> problems,
        out TransformerSequenceData? sequence)
    {
        var t = new TransformerDto();
        // N234: the fault sequence data, when given.
        double? r0 = null, x0 = null, nFrom = null, nTo = null;
        WindingConnection? wFrom = null, wTo = null;
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": t.Name = p.Value.GetString(); break;
                case "from": t.From = JsonWhole.Int(p); break;
                case "to": t.To = JsonWhole.Int(p); break;
                case "template": t.DynamicsClass = ClassNameFor(p.Value.GetString()!, at + "/template", templates, problems); break;
                case "rPu": t.RPu = p.Value.GetDouble(); break;
                case "xPu": t.XPu = p.Value.GetDouble(); break;
                case "tapRatio": t.TapRatio = p.Value.GetDouble(); break;
                case "phaseShiftDeg": t.PhaseShiftDeg = p.Value.GetDouble(); break;
                case "ratingMva": t.RatingMva = p.Value.GetDouble(); break;
                case "ratingBMva": t.RatingBMva = p.Value.GetDouble(); break;
                case "ratingCMva": t.RatingCMva = p.Value.GetDouble(); break;
                case "type": problems.Add(NotYet(at + "/type", "a transformer type", LaterTypePhase)); break;
                // N234: P5 (sequence data) is open; these reach the model (applied after the build).
                case "r0Pu": r0 = p.Value.GetDouble(); break;
                case "x0Pu": x0 = p.Value.GetDouble(); break;
                case "windings":
                    foreach (JsonProperty w in p.Value.EnumerateObject())
                    {
                        WindingConnection c = ParseWinding(w.Value, at + "/windings/" + w.Name);
                        switch (w.Name)
                        {
                            case "from": wFrom = c; break;
                            case "to": wTo = c; break;
                            default: throw new InvalidDataException($"windings: unexpected member '{w.Name}'.");
                        }
                    }
                    break;
                case "neutralXPu":
                    foreach (JsonProperty w in p.Value.EnumerateObject())
                    {
                        switch (w.Name)
                        {
                            case "from": nFrom = w.Value.GetDouble(); break;
                            case "to": nTo = w.Value.GetDouble(); break;
                            default: throw new InvalidDataException($"neutralXPu: unexpected member '{w.Name}'.");
                        }
                    }
                    break;
                case "inService": if (!p.Value.GetBoolean()) problems.Add(NotYet(at + "/inService", "an out-of-service transformer", LaterNetworkPhase)); break;
                default: throw new InvalidDataException($"transformer: unexpected member '{p.Name}'.");
            }
        }
        bool any = r0 is not null || x0 is not null || wFrom is not null || wTo is not null || nFrom is not null || nTo is not null;
        sequence = any ? new TransformerSequenceData(r0, x0, wFrom, wTo, nFrom, nTo) : null;
        if (sequence is not null) CheckTransformerSequence(sequence, at, problems);
        return t;
    }

    /// <summary>N234: a winding connection, exactly one of <see cref="WindingConnection"/>'s member names (the schema's
    /// enum is pinned to them; case-sensitive, no aliases).</summary>
    private static WindingConnection ParseWinding(JsonElement v, string at)
    {
        string s = v.GetString() ?? "";
        return s switch
        {
            nameof(WindingConnection.Delta) => WindingConnection.Delta,
            nameof(WindingConnection.WyeGrounded) => WindingConnection.WyeGrounded,
            nameof(WindingConnection.WyeUngrounded) => WindingConnection.WyeUngrounded,
            _ => throw new InvalidDataException($"{at}: '{s}' is not a winding connection (Delta, WyeGrounded or WyeUngrounded)."),
        };
    }

    /// <summary>
    /// N234: refuse transformer sequence data the zero-sequence network would ignore or misread, naming the element. The
    /// zero-sequence network (Iec60909.BuildZeroSequenceNetwork) treats a branch WITHOUT winding connections as a line, so:
    /// <list type="bullet">
    /// <item><c>r0Pu</c>, <c>x0Pu</c> or <c>neutralXPu</c> without <c>windings</c> is refused: a transformer's
    /// zero-sequence path is decided by its windings (its vector group, IEC 60909-0:2016), and without them the impedance
    /// would be used as a line's series path.</item>
    /// <item><c>windings</c> must give both sides (the schema requires both; checked again).</item>
    /// <item>A neutral reactance on a side whose winding is not <c>WyeGrounded</c> is refused: only an earthed star point
    /// has a neutral for it to sit in, so it would never be read. Each value must be finite and not negative.</item>
    /// <item><c>r0Pu</c> needs <c>x0Pu</c>: the model reads Z0 = r0 + j x0 as soon as either is non-zero, so an r0 alone
    /// would be a purely resistive zero-sequence leakage impedance, which no transformer has. <c>x0Pu</c> must be finite
    /// and positive (0 is the model's "not supplied", for which Z0 = Z1 is used, the usual assumption for a unit with
    /// no measured zero-sequence data); <c>r0Pu</c> finite and not negative.</item>
    /// </list>
    /// </summary>
    private static void CheckTransformerSequence(TransformerSequenceData s, string at, List<string> problems)
    {
        static string R(double x) => x.ToString("R", CultureInfo.InvariantCulture);
        bool windings = s.FromWinding is not null || s.ToWinding is not null;
        if (windings && (s.FromWinding is null || s.ToWinding is null))
            problems.Add($"{at}/windings: both sides must be given (from and to).");
        if (!windings && (s.R0Pu is not null || s.X0Pu is not null || s.FromNeutralXPu is not null || s.ToNeutralXPu is not null))
            problems.Add($"{at}: r0Pu / x0Pu / neutralXPu are given without windings; a transformer's zero-sequence path is set by its "
                + "winding connections, and without them the zero-sequence network would treat it as a line. Give windings (from and to).");
        if (s.R0Pu is not null && s.X0Pu is null)
            problems.Add($"{at}/r0Pu: given without x0Pu; a zero-sequence impedance of r0 + j0 is not a transformer's. Give x0Pu as well.");
        if (s.R0Pu is double r && !(double.IsFinite(r) && r >= 0.0))
            problems.Add($"{at}/r0Pu: {R(r)} is not a finite, non-negative resistance (pu on the case base).");
        if (s.X0Pu is double x && !(double.IsFinite(x) && x > 0.0))
            problems.Add($"{at}/x0Pu: {R(x)} is not a finite, positive reactance (pu on the case base); omit it to use Z0 = Z1.");
        foreach ((string side, double? xn, WindingConnection? w) in new[] { ("from", s.FromNeutralXPu, s.FromWinding), ("to", s.ToNeutralXPu, s.ToWinding) })
        {
            if (xn is not double v) continue;
            if (!(double.IsFinite(v) && v >= 0.0))
                problems.Add($"{at}/neutralXPu/{side}: {R(v)} is not a finite, non-negative reactance (pu on the case base).");
            if (w is WindingConnection wc && wc != WindingConnection.WyeGrounded)
                problems.Add($"{at}/neutralXPu/{side}: the {side} winding is {wc}, which has no earthed neutral, so a neutral reactance there would never be read.");
        }
    }

    /// <summary>
    /// N234: put the generators' and transformers' fault sequence data into the built model. The v1 builder keeps the
    /// element order (every generator in <see cref="GridModel.AllGenerators"/> in network-file order before any dispatch;
    /// the branches are the lines, one each, then the transformers, in network-file order), so the data is matched by
    /// index, and each match is checked by name (an internal error otherwise, never a silent mismatch). Returns
    /// <paramref name="model"/> itself when no element gives any sequence data, so such a case is exactly the model it
    /// was before N234.
    /// </summary>
    private static GridModel ApplySequenceData(GridModel model, MappedCase dtos)
    {
        bool anyGen = dtos.GeneratorSequence.Any(s => s is not null);
        bool anyTx = dtos.TransformerSequence.Any(s => s is not null);
        if (!anyGen && !anyTx) return model;

        IReadOnlyList<Generator>? fleet = null;
        if (anyGen)
        {
            IReadOnlyList<Generator> all = model.AllGenerators;
            if (all.Count != dtos.Generators.Count)
                throw new InvalidOperationException($"N234: the built model holds {all.Count} generators for {dtos.Generators.Count} written (internal error).");
            var list = new List<Generator>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                Generator g = all[i];
                if (!string.Equals(g.Name, dtos.Generators[i].Name, StringComparison.Ordinal))
                    throw new InvalidOperationException($"N234: generator {i} is '{g.Name}' in the model but '{dtos.Generators[i].Name}' in the network file (internal error).");
                GeneratorSequenceData? s = dtos.GeneratorSequence[i];
                list.Add(s is null ? g : g with
                {
                    SubtransientXdPu = s.SubtransientXdPu ?? g.SubtransientXdPu,
                    NegativeSeqXPu = s.NegativeSeqXPu ?? g.NegativeSeqXPu,
                });
            }
            fleet = list;
        }

        IReadOnlyList<Branch>? branches = null;
        if (anyTx)
        {
            IReadOnlyList<Branch> all = model.Branches;
            int first = dtos.Lines.Count;
            if (all.Count != first + dtos.Transformers.Count)
                throw new InvalidOperationException($"N234: the built model holds {all.Count} branches for {first} lines and {dtos.Transformers.Count} transformers written (internal error).");
            var list = new List<Branch>(all);
            for (int j = 0; j < dtos.Transformers.Count; j++)
            {
                TransformerSequenceData? s = dtos.TransformerSequence[j];
                if (s is null) continue;
                Branch b = all[first + j];
                if (!b.IsTransformer || !string.Equals(b.Name, dtos.Transformers[j].Name, StringComparison.Ordinal))
                    throw new InvalidOperationException($"N234: branch {first + j} is not transformer '{dtos.Transformers[j].Name}' (internal error).");
                list[first + j] = b with
                {
                    R0 = s.R0Pu ?? b.R0,
                    X0 = s.X0Pu ?? b.X0,
                    FromWinding = s.FromWinding ?? b.FromWinding,
                    ToWinding = s.ToWinding ?? b.ToWinding,
                    FromNeutralXPu = s.FromNeutralXPu ?? b.FromNeutralXPu,
                    ToNeutralXPu = s.ToNeutralXPu ?? b.ToNeutralXPu,
                };
            }
            branches = list;
        }
        // GridModel.CopyWith keeps every other part of the model (buses, OLTCs, boundaries, asset dynamics, protections,
        // name, base, frequency); given the whole fleet it keeps the fleet's order, out-of-service units included.
        return model.CopyWith(generators: fleet, branches: branches);
    }

    private static OltcDto MapOltc(JsonElement e)
    {
        var o = new OltcDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "transformer": o.Transformer = p.Value.GetString()!; break;
                case "controlsBus": o.ControlsBus = JsonWhole.Int(p); break;
                case "targetVpu": o.TargetVpu = p.Value.GetDouble(); break;
                case "minTap": o.MinTap = p.Value.GetDouble(); break;
                case "maxTap": o.MaxTap = p.Value.GetDouble(); break;
                case "stepPu": o.StepPu = p.Value.GetDouble(); break;
                case "deadbandPu": o.DeadbandPu = p.Value.GetDouble(); break;
                default: throw new InvalidDataException($"oltc: unexpected member '{p.Name}'.");
            }
        }
        return o;
    }

    private static BoundaryDto MapBoundary(JsonElement e)
    {
        var b = new BoundaryDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": b.Name = p.Value.GetString()!; break;
                case "limitMw": b.LimitMw = p.Value.GetDouble(); break;
                case "branches":
                    b.Branches = new List<BoundaryBranchDto>();
                    foreach (JsonElement x in p.Value.EnumerateArray())
                    {
                        var bb = new BoundaryBranchDto();
                        foreach (JsonProperty q in x.EnumerateObject())
                        {
                            switch (q.Name)
                            {
                                case "branch": bb.Branch = q.Value.GetString()!; break;
                                case "direction": bb.Direction = JsonWhole.Int(q); break;
                                default: throw new InvalidDataException($"boundary branch: unexpected member '{q.Name}'.");
                            }
                        }
                        b.Branches.Add(bb);
                    }
                    break;
                default: throw new InvalidDataException($"boundary: unexpected member '{p.Name}'.");
            }
        }
        return b;
    }

    private static ProtectionInstanceDto MapProtection(JsonElement e, string at, TemplateIndex templates, List<string> problems)
    {
        var d = new ProtectionInstanceDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": d.Name = p.Value.GetString()!; break;
                case "template": d.Class = ClassNameFor(p.Value.GetString()!, at + "/template", templates, problems) ?? ""; break;
                case "monitorBus": d.MonitorBus = JsonWhole.Int(p); break;
                case "capacityMw": d.CapacityMw = p.Value.GetDouble(); break;
                case "targetBranch": d.TargetBranch = p.Value.GetString(); break;
                default: throw new InvalidDataException($"protection: unexpected member '{p.Name}'.");
            }
        }
        return d;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>True when <paramref name="full"/> lies inside the folder <paramref name="dir"/> (both full paths). GR-655:
    /// compared by the operating system's rule (case-insensitive on Windows, exact elsewhere), as the case editor's
    /// IsInside does; a case-insensitive test on Linux treated two different folders as one. Links are not resolved.</summary>
    private static bool IsInside(string dir, string full)
    {
        string d = Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar;
        return full.StartsWith(d, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Throw one <see cref="InvalidDataException"/> listing every problem (up to <see cref="MaxListedProblems"/>).</summary>
    private static void ThrowIfAny(string manifest, string stage, List<string> problems)
    {
        if (problems.Count == 0) return;
        string list = string.Join(Environment.NewLine + "  ", problems.Take(MaxListedProblems));
        string more = problems.Count > MaxListedProblems ? $"{Environment.NewLine}  ... and {problems.Count - MaxListedProblems} more" : "";
        throw new InvalidDataException(
            $"{manifest}: JSON v2 case refused at {stage}: {problems.Count} problem(s):{Environment.NewLine}  {list}{more}");
    }
}
