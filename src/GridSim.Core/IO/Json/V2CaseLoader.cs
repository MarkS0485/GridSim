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

    /// <summary>
    /// RC3 P8 (motor acceleration): the placed induction motors of the case's network files (their <c>motors</c>
    /// arrays, in network-file order), checked and carried BESIDE the model like <see cref="Overlay"/>. Empty when no
    /// network file has a <c>motors</c> array. They are never an input to <see cref="Model"/> or to any steady-state
    /// solver, so <see cref="Model"/> is the same with or without them; only the motor acceleration study connects one.
    /// </summary>
    public IReadOnlyList<PlacedMotor> Motors { get; init; } = Array.Empty<PlacedMotor>();
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

    /// <summary><see cref="Load"/>, also returning the load's notes.
    /// <para>GR-642 (November 2026): an exception System.Text.Json raises about the input that is not a JsonException
    /// (<see cref="JsonGridLoader.IsRawJsonFailure"/>, for example a value read as the wrong JSON kind) is turned into an
    /// <see cref="InvalidDataException"/> naming the manifest, so the loader refuses bad input with its own type.</para></summary>
    public static V2CaseLoadResult LoadWithNotes(string manifestPath, string schemasDir, string? dataRoot = null, bool dispatch = true)
    {
        try
        {
            return LoadWithNotesCore(manifestPath, schemasDir, dataRoot, dispatch);
        }
        catch (Exception ex) when (JsonGridLoader.IsRawJsonFailure(ex))
        {
            throw new InvalidDataException($"{Path.GetFileName(manifestPath)}: {ex.Message}", ex);
        }
    }

    private static V2CaseLoadResult LoadWithNotesCore(string manifestPath, string schemasDir, string? dataRoot, bool dispatch)
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
        // GR-183 (November 2026): the schema set and the kind registry are read from disk once per case load and passed
        // to every validation stage below (they used to be loaded again for the manifest, again for the library files
        // and again here). Both are read-only after loading, so the checks and their messages are unchanged.
        JsonSchemaSubset schemas = JsonSchemaSubset.LoadDirectory(schemasDir);
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(Path.Combine(schemasDir, "kinds"), schemas);
        var problems = new List<string>();
        foreach (V2DocumentReport rep in V2DocumentValidator.ValidateFiles(new[] { manifestFull }, schemasDir, schemas, kinds))
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
            foreach (V2DocumentReport rep in V2DocumentValidator.ValidateFiles(libraryFiles.Select(l => l.Full).ToList(), schemasDir, schemas, kinds))
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
        var unitOverrides = new List<(string At, string Unit, JsonElement Override)>();
        if (manifest.TryGetProperty("overrides", out JsonElement overrides))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int oi = 0;
            foreach (JsonElement ov in overrides.EnumerateArray())
            {
                string at = $"/overrides/{oi++}";
                // RC5: an override with a unit member sets one unit's own unit protection values; it is applied to that
                // unit after the model is built (WithUnitProtection), never to the template.
                if (ov.TryGetProperty("unit", out JsonElement unitEl)) { unitOverrides.Add((at, unitEl.GetString()!, ov.Clone())); continue; }
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
                string bref = b.GetProperty("template").GetString()!;
                // RC5: a rule whose template is a unitProtection template binds unit protection (by unit type), not dynamics.
                if (templates.Find(bref) is { Kind: UnitProtectionKind })
                {
                    if (UnitProtectionClassFor(bref, at, templates, problems) is string pcls)
                        dtos.ProtectionBindings.Add(new AssetBinding(b.GetProperty("matchType").GetString()!, pcls));
                    continue;
                }
                string? cls = ClassNameFor(bref, at, templates, problems);
                if (cls is not null) dtos.AssetBindings.Add(new AssetBindingDto { MatchType = b.GetProperty("matchType").GetString()!, Class = cls });
            }
        }
        // Unique bus ids across every network file (the v1 builder keys buses by id and would quietly keep the last).
        foreach (IGrouping<int, BusDto> g in dtos.Buses.GroupBy(b => b.Id).Where(g => g.Count() > 1))
            problems.Add($"{shown}: bus id {g.Key.ToString(CultureInfo.InvariantCulture)} is used by {g.Count().ToString(CultureInfo.InvariantCulture)} buses; ids must be unique across every network file.");
        foreach (IGrouping<string, BoundaryDto> g in dtos.Boundaries.GroupBy(b => b.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"{shown}: {g.Count().ToString(CultureInfo.InvariantCulture)} boundaries are named '{g.Key}'; boundary names must be unique.");

        // RC3 P8: the placed motors' rules the schema cannot express (bus reference, unique names, not also a demand).
        CheckMotors(dtos, problems);
        // RC5 (SC equivalent writing): the fault-sequence tables, resolved by name (null when the case has none).
        FaultSequenceTables? faultSequence = ResolveFaultSequence(dtos, problems);

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
            built = ApplyFaultSequence(built, dtos, faultSequence);   // RC5: the model itself when the case has no fault-sequence tables
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
        // RC3 P8: placed motors are carried beside the model (never in it); a case without any keeps the pre-P8 result.
        IReadOnlyList<PlacedMotor> motors = dtos.Motors.Count == 0 ? Array.Empty<PlacedMotor>() : dtos.Motors.Select(m => m.Motor).ToList();
        if (motors.Count > 0)
            notes.Add($"motors: {motors.Count.ToString(CultureInfo.InvariantCulture)} placed motor {(motors.Count == 1 ? "row" : "rows")} carried beside the model for the motor acceleration study; the steady-state power flow does not use them, and a verb that writes the case from the model (convert) does not carry them");
        // RC5: per-unit protection. A case with no unitProtection template, no protectionTemplate and no per-unit override
        // takes none of this (the model is the very object built above, as before).
        if (templates.All.Any(t => t.Kind == UnitProtectionKind) || dtos.ProtectionTemplates.Count > 0 || unitOverrides.Count > 0)
            model = WithUnitProtection(model, templates, dtos, unitOverrides, kinds, shown, caseName, notes);
        if (!manifest.TryGetProperty("assets", out JsonElement assetList))
            return new V2CaseLoadResult(model, notes) { Motors = motors };
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
        return new V2CaseLoadResult(model, notes) { Overlay = overlay } with { Motors = motors };   // AssetOverlayGuardTests pins "{ Overlay = overlay }"
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

        /// <summary>RC5: the unit protection class layer of an active unitProtection template: numbers (bare or object form)
        /// as settings, the two preset strings as presets; provenance from the template's map, then object-form entries,
        /// then override entries (as <see cref="ToAssetClass"/>). The kind spec has already checked every value.</summary>
        public Protection.UnitProtectionLayer ToUnitProtectionLayer()
        {
            var settings = new Dictionary<string, double>(StringComparer.Ordinal);
            var prov = new Dictionary<string, AssetClassProvenance>(StringComparer.Ordinal);
            string? rocof = null, iface = null;
            foreach (JsonProperty e in Provenance.EnumerateObject()) prov[e.Name] = Prov(e.Value);
            foreach (JsonProperty p in Params.EnumerateObject())
            {
                JsonElement v = p.Value;
                if (v.ValueKind == JsonValueKind.Object)
                {
                    if (v.TryGetProperty("prov", out JsonElement pv)) prov[p.Name] = Prov(pv);
                    v = v.GetProperty("v");
                }
                if (p.Name == "rocofPreset") rocof = v.GetString();
                else if (p.Name == "interfacePreset") iface = v.GetString();
                else settings[p.Name] = v.GetDouble();
            }
            foreach ((string k, ProvenanceDto v) in OverrideProvenance)
                prov[k] = new AssetClassProvenance(Enum.Parse<Diagnostics.ProvenanceTag>(v.Tag), v.Note);
            return new Protection.UnitProtectionLayer(settings, prov, rocof, iface);
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
    /// <summary>RC5: the template kind of a unit protection class default.</summary>
    internal const string UnitProtectionKind = "unitProtection";

    /// <summary>RC5: the class name of a unitProtection template a generator or binding names, or null (problem added) when
    /// it does not resolve, is a placeholder or is of another kind.</summary>
    private static string? UnitProtectionClassFor(string reference, string at, TemplateIndex templates, List<string> problems)
    {
        Template? t = templates.Find(reference);
        if (t is null) { problems.Add($"{at}: template {reference} is neither imported nor a local template of the case."); return null; }
        if (t.Placeholder) { problems.Add($"{at}: template {reference} is a placeholder; a placeholder holds no values and can never be bound to a unit."); return null; }
        if (t.Kind != UnitProtectionKind) { problems.Add($"{at}: template {reference} is kind '{t.Kind}'; a unit's protection template must be kind '{UnitProtectionKind}'."); return null; }
        return t.ClassName;
    }

    /// <summary>
    /// RC5 per-unit protection: every active unitProtection template becomes a unit protection class (local templates
    /// first, then imported, as asset classes); each named generator takes its class from its protectionTemplate, else the
    /// first matching unitProtection binding rule (exact before prefix, as for asset bindings); each per-unit override (an
    /// override with a unit member) is checked against the kind spec and laid on that unit alone, its values taking the
    /// object form's provenance, else the override's, else CuratedPlaceholder [VERIFY] quoting the reason (the template
    /// override rule). Every problem is listed in one refusal.
    /// </summary>
    private static GridModel WithUnitProtection(GridModel model, TemplateIndex templates, MappedCase dtos,
        List<(string At, string Unit, JsonElement Override)> unitOverrides, KindSpecRegistry kinds, string shown, string caseName,
        List<string> notes)
    {
        var problems = new List<string>();
        var classes = new List<Protection.UnitProtectionClass>();
        foreach (Template tpl in templates.All.Where(t => !t.Placeholder && t.Kind == UnitProtectionKind))
            classes.Add(new Protection.UnitProtectionClass(tpl.ClassName, tpl.Description, tpl.ToUnitProtectionLayer()));

        // Which class each unit takes: its own protectionTemplate, else a binding rule.
        var own = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string unit, string cls, string at) in dtos.ProtectionTemplates)
            if (!own.TryAdd(unit, cls)) problems.Add($"{shown}: {at}: generator name '{unit}' is shared by more than one unit with a protectionTemplate; give each unit a unique name.");
        var rules = new AssetBindingSet(dtos.ProtectionBindings);
        var classOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (Generator g in model.AllGenerators)
        {
            if (g.Name is not { } name || classOf.ContainsKey(name)) continue;
            if (rules.Resolve(own.GetValueOrDefault(name), g.Type) is string cls) { classOf[name] = cls; order.Add(name); }
        }

        List<Protection.UnitProtectionAssignment> units = UnitAssignments(order, classOf, unitOverrides, templates, kinds, shown,
            $"case '{caseName}'", problems, p => ThrowIfAny(shown, "the unit protection", p));

        Protection.UnitProtectionSet set;
        try
        {
            set = new Protection.UnitProtectionSet(classes, units);
            model = model.WithUnitProtection(set);
        }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{shown}: {ex.Message}", ex); }
        int stages = set.Resolved.Sum(r => r.Stages.Count);
        int placeholders = set.Resolved.Sum(r => r.Stages.Count(st => st.Pickup.IsPlaceholder || st.Delay.IsPlaceholder || (st.Diameter?.IsPlaceholder ?? false)));
        notes.Add($"unit protection: {set.Units.Count.ToString(CultureInfo.InvariantCulture)} {(set.Units.Count == 1 ? "unit" : "units")} with relays of their own "
            + $"({stages.ToString(CultureInfo.InvariantCulture)} relay {(stages == 1 ? "stage" : "stages")}, {placeholders.ToString(CultureInfo.InvariantCulture)} with a placeholder setting), "
            + $"{set.Classes.Count.ToString(CultureInfo.InvariantCulture)} unit protection {(set.Classes.Count == 1 ? "template" : "templates")}; the power flow does not use them");
        return model;
    }

    /// <summary>RC5: each unit's own protection layer, in <paramref name="order"/>: the per-unit overrides (an override with a
    /// unit member in a v2 case, a unit entry with params in a unit protection file) checked against the unitProtection kind
    /// spec after they are laid on the unit's template, each value taking the object form's provenance, else the entry's,
    /// else CuratedPlaceholder [VERIFY] quoting the reason. <paramref name="origin"/> names the document in that note;
    /// <paramref name="refuse"/> throws when problems were found. Shared by the v2 case loader and the unit protection file.</summary>
    private static List<Protection.UnitProtectionAssignment> UnitAssignments(IReadOnlyList<string> order, IReadOnlyDictionary<string, string> classOf,
        List<(string At, string Unit, JsonElement Override)> unitOverrides, TemplateIndex templates, KindSpecRegistry kinds, string shown,
        string origin, List<string> problems, Action<List<string>> refuse)
    {
        // The per-unit overrides, grouped by unit in manifest order.
        var unitParams = new Dictionary<string, List<(JsonProperty Param, JsonElement? Prov, string Reason)>>(StringComparer.Ordinal);
        foreach ((string at, string unit, JsonElement ov) in unitOverrides)
        {
            string target = ov.GetProperty("template").GetString()!;
            Template? tpl = templates.Find(target);
            if (tpl is null) { problems.Add($"{shown}: {at}/template: {target} is neither imported nor a local template of the case."); continue; }
            if (tpl.Kind != UnitProtectionKind) { problems.Add($"{shown}: {at}/unit: a per-unit override applies to a '{UnitProtectionKind}' template, but {target} is kind '{tpl.Kind}'."); continue; }
            if (!classOf.TryGetValue(unit, out string? resolved)) { problems.Add($"{shown}: {at}/unit: unit '{unit}' has no unit protection template (no protectionTemplate and no binding rule matching it), so there is nothing to override."); continue; }
            if (resolved != tpl.ClassName) { problems.Add($"{shown}: {at}/template: unit '{unit}' takes unit protection template '{resolved}', not {target}; a per-unit override names the unit's own template."); continue; }
            JsonElement ps = ov.GetProperty("params");
            if (!ps.EnumerateObject().Any()) { problems.Add($"{shown}: {at}/params: an override must set at least one parameter."); continue; }
            List<SchemaError> errs = kinds.CheckParams(UnitProtectionKind, tpl.WithOverrides(ps), placeholder: false, at + "/params").ToList();
            foreach (SchemaError e in errs) problems.Add($"{shown}: {e} (after applying the per-unit override of '{unit}' to {target})");
            if (errs.Count > 0) continue;
            if (!unitParams.TryGetValue(unit, out var list)) unitParams[unit] = list = new();
            JsonElement? prov = ov.TryGetProperty("provenance", out JsonElement pv) ? pv : null;
            string reason = ov.GetProperty("reason").GetString()!;
            foreach (JsonProperty p in ps.EnumerateObject())
            {
                if (list.Any(x => x.Param.Name == p.Name))
                    problems.Add($"{shown}: {at}/params/{JsonSchemaSubset.EscapeToken(p.Name)}: '{p.Name}' of unit '{unit}' is overridden more than once.");
                else list.Add((p, prov, reason));
            }
        }
        refuse(problems);

        var units = new List<Protection.UnitProtectionAssignment>();
        foreach (string unit in order)
        {
            var settings = new Dictionary<string, double>(StringComparer.Ordinal);
            var provs = new Dictionary<string, AssetClassProvenance>(StringComparer.Ordinal);
            string? rocof = null, iface = null;
            var reasons = new List<string>();
            foreach ((JsonProperty p, JsonElement? prov, string reason) in unitParams.GetValueOrDefault(unit) ?? new())
            {
                if (!reasons.Contains(reason)) reasons.Add(reason);
                JsonElement v = p.Value;
                AssetClassProvenance? pp = null;
                if (v.ValueKind == JsonValueKind.Object)
                {
                    if (v.TryGetProperty("prov", out JsonElement op)) pp = Prov(op);
                    v = v.GetProperty("v");
                }
                pp ??= prov is JsonElement o ? Prov(o)
                    : new AssetClassProvenance(Diagnostics.ProvenanceTag.CuratedPlaceholder, $"[VERIFY] set by a per-unit override in {origin} for unit '{unit}': {reason}");
                if (p.Name == "rocofPreset") rocof = v.GetString();
                else if (p.Name == "interfacePreset") iface = v.GetString();
                else { settings[p.Name] = v.GetDouble(); provs[p.Name] = pp; }
            }
            units.Add(new Protection.UnitProtectionAssignment(unit, classOf[unit], new Protection.UnitProtectionLayer(settings, provs, rocof, iface))
            { Reason = reasons.Count == 0 ? null : string.Join("; ", reasons) });
        }

        return units;
    }

    /// <summary>RC5: a provenance object ({tag, note}, schema-checked) as an asset-class provenance.</summary>
    private static AssetClassProvenance Prov(JsonElement p)
    {
        string tag = p.GetProperty("tag").GetString()!;
        if (!Enum.TryParse(tag, ignoreCase: false, out Diagnostics.ProvenanceTag t) || !Enum.IsDefined(t))
            throw new InvalidDataException($"provenance tag '{tag}' is not a provenance tag.");
        return new AssetClassProvenance(t, p.GetProperty("note").GetString()!);
    }


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

        /// <summary>RC3 P8: the placed motors, in network-file order, each with where it was written.</summary>
        public List<(PlacedMotor Motor, string At)> Motors { get; } = new();

        /// <summary>RC5: each generator's own protectionTemplate (unit name, class name, where), in network-file order.</summary>
        public List<(string Unit, string ClassName, string At)> ProtectionTemplates { get; } = new();

        /// <summary>RC5: the binding rules whose template is a unitProtection template, in manifest order.</summary>
        public List<AssetBinding> ProtectionBindings { get; } = new();

        /// <summary>N234: the fault sequence data of each generator, index-aligned with <see cref="Generators"/> (null
        /// where the element gives none).</summary>
        public List<GeneratorSequenceData?> GeneratorSequence { get; } = new();

        /// <summary>N234: the fault sequence data of each transformer, index-aligned with <see cref="Transformers"/>
        /// (null where the element gives none).</summary>
        public List<TransformerSequenceData?> TransformerSequence { get; } = new();

        /// <summary>RC5 (SC equivalent writing): the lineWindings entries, in network-file order, each with where it was written.</summary>
        public List<(LineWindingData Data, string At)> LineWindings { get; } = new();

        /// <summary>RC5: the machineZeroSequence entries, in network-file order.</summary>
        public List<(string Generator, MachineZeroSequence Z, string At)> MachineZeros { get; } = new();

        /// <summary>RC5: the zeroSequenceMutuals entries, in network-file order.</summary>
        public List<(string LineA, string LineB, double RPu, double XPu, string At)> Mutuals { get; } = new();
    }

    /// <summary>RC5: a lineWindings entry as written (neutral reactances null = not given).</summary>
    private sealed record LineWindingData(string Line, WindingConnection FromWinding, WindingConnection ToWinding,
        double? FromNeutralXPu, double? ToNeutralXPu);

    /// <summary>RC5: the three fault-sequence tables resolved against the lines and generators (indices into the
    /// built model's lines, which come first among its branches, and into its whole fleet). Null when the case has none.</summary>
    private sealed record FaultSequenceTables(
        IReadOnlyList<(int Line, LineWindingData Data)> Windings,
        IReadOnlyList<(int Generator, MachineZeroSequence Z)> Machines,
        IReadOnlyList<(int LineA, int LineB, double RPu, double XPu)> Mutuals);

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
                        GenDto gen = MapGenerator(e, at, templates, problems, out GeneratorSequenceData? gs);
                        into.Generators.Add(gen);
                        into.GeneratorSequence.Add(gs);
                        // RC5: the unit's own unit protection template (absent: a binding rule may give it one).
                        if (e.TryGetProperty("protectionTemplate", out JsonElement upt)
                            && UnitProtectionClassFor(upt.GetString()!, at + "/protectionTemplate", templates, problems) is string upc)
                            into.ProtectionTemplates.Add((gen.Name ?? "", upc, at + "/protectionTemplate"));
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
                    case "motors": into.Motors.Add((MapMotor(e, at, problems), at)); break;
                    case "lineWindings": if (MapLineWinding(e, at, problems) is LineWindingData lw) into.LineWindings.Add((lw, at)); break;
                    case "machineZeroSequence":
                        into.MachineZeros.Add((e.GetProperty("generator").GetString()!,
                            new MachineZeroSequence(e.GetProperty("rPu").GetDouble(), e.GetProperty("xPu").GetDouble()), at));
                        break;
                    case "zeroSequenceMutuals":
                        into.Mutuals.Add((e.GetProperty("lineA").GetString()!, e.GetProperty("lineB").GetString()!,
                            e.GetProperty("rPu").GetDouble(), e.GetProperty("xPu").GetDouble(), at));
                        break;
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
                case "protectionTemplate": break;   // RC5: read by MapNetwork with the unit's name (unit protection)
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
    /// RC5 (SC equivalent writing): one <c>lineWindings</c> entry. The schema requires the line name and both windings;
    /// a neutral reactance must be finite, not negative and on a WyeGrounded side (as for a transformer element), else a
    /// problem is added naming it and null is returned.
    /// </summary>
    private static LineWindingData? MapLineWinding(JsonElement e, string at, List<string> problems)
    {
        string line = e.GetProperty("line").GetString()!;
        JsonElement w = e.GetProperty("windings");
        WindingConnection wf = ParseWinding(w.GetProperty("from"), at + "/windings/from");
        WindingConnection wt = ParseWinding(w.GetProperty("to"), at + "/windings/to");
        double? nFrom = null, nTo = null;
        if (e.TryGetProperty("neutralXPu", out JsonElement n))
        {
            if (n.TryGetProperty("from", out JsonElement f)) nFrom = f.GetDouble();
            if (n.TryGetProperty("to", out JsonElement t)) nTo = t.GetDouble();
        }
        bool ok = true;
        foreach ((string side, double? xn, WindingConnection wc) in new[] { ("from", nFrom, wf), ("to", nTo, wt) })
        {
            if (xn is not double v) continue;
            if (!(double.IsFinite(v) && v >= 0.0))
            {
                problems.Add($"{at}/neutralXPu/{side}: {v.ToString("R", CultureInfo.InvariantCulture)} is not a finite, non-negative reactance (pu on the case base).");
                ok = false;
            }
            if (wc != WindingConnection.WyeGrounded)
            {
                problems.Add($"{at}/neutralXPu/{side}: the {side} winding is {wc}, which has no earthed neutral, so a neutral reactance there would never be read.");
                ok = false;
            }
        }
        return ok ? new LineWindingData(line, wf, wt, nFrom, nTo) : null;
    }

    /// <summary>
    /// RC5 (SC equivalent writing): resolve the <c>lineWindings</c>, <c>machineZeroSequence</c> and
    /// <c>zeroSequenceMutuals</c> entries by name against the lines (in file order, the first branches of the built model)
    /// and the generators (the whole fleet in file order). Every name must name exactly one element and be named by one
    /// entry at most; a machine impedance must not be zero; a coupled line may not also carry windings (only lines are
    /// coupled). Each problem is added, naming the entry. Null when the case has none of the three, so such a case loads
    /// exactly as before.
    /// </summary>
    private static FaultSequenceTables? ResolveFaultSequence(MappedCase dtos, List<string> problems)
    {
        if (dtos.LineWindings.Count == 0 && dtos.MachineZeros.Count == 0 && dtos.Mutuals.Count == 0) return null;
        static Dictionary<string, List<int>> ByName(IEnumerable<string?> names)
        {
            var d = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            int i = 0;
            foreach (string? s in names)
            {
                if (s is not null)
                {
                    if (!d.TryGetValue(s, out List<int>? list)) d[s] = list = new List<int>();
                    list.Add(i);
                }
                i++;
            }
            return d;
        }
        Dictionary<string, List<int>> lines = ByName(dtos.Lines.Select(l => l.Name));
        Dictionary<string, List<int>> gens = ByName(dtos.Generators.Select(g => g.Name));
        int? One(Dictionary<string, List<int>> map, string name, string what, string at)
        {
            if (!map.TryGetValue(name, out List<int>? hits))
            {
                problems.Add($"{at}: there is no {what} named '{name}' in the case's {what}s.");
                return null;
            }
            if (hits.Count > 1)
            {
                problems.Add($"{at}: {hits.Count.ToString(CultureInfo.InvariantCulture)} {what}s are named '{name}'; the entry must name exactly one. Give each a unique name.");
                return null;
            }
            return hits[0];
        }

        var windings = new List<(int Line, LineWindingData Data)>();
        var wound = new HashSet<int>();
        foreach ((LineWindingData data, string at) in dtos.LineWindings)
        {
            if (One(lines, data.Line, "line", at + "/line") is not int i) continue;
            if (!wound.Add(i)) { problems.Add($"{at}/line: line '{data.Line}' is named by more than one lineWindings entry."); continue; }
            windings.Add((i, data));
        }
        var machines = new List<(int Generator, MachineZeroSequence Z)>();
        var earthed = new HashSet<int>();
        foreach ((string name, MachineZeroSequence z, string at) in dtos.MachineZeros)
        {
            if (z.RPu == 0.0 && z.XPu == 0.0)
            {
                problems.Add($"{at}: generator '{name}' has a zero zero-sequence impedance; a shunt to earth of zero impedance is not a machine's. Leave the entry out if the machine has no zero-sequence path.");
                continue;
            }
            if (One(gens, name, "generator", at + "/generator") is not int g) continue;
            if (!earthed.Add(g)) { problems.Add($"{at}/generator: generator '{name}' is named by more than one machineZeroSequence entry."); continue; }
            machines.Add((g, z));
        }
        var mutuals = new List<(int LineA, int LineB, double RPu, double XPu)>();
        foreach ((string a, string b, double r, double x, string at) in dtos.Mutuals)
        {
            int? ia = One(lines, a, "line", at + "/lineA"), ib = One(lines, b, "line", at + "/lineB");
            if (ia is not int la || ib is not int lb) continue;
            if (la == lb) { problems.Add($"{at}: line '{a}' is coupled to itself."); continue; }
            if (wound.Contains(la) || wound.Contains(lb))
            {
                problems.Add($"{at}: line '{(wound.Contains(la) ? a : b)}' has winding connections (lineWindings) and is mutually coupled; only lines without windings are coupled.");
                continue;
            }
            mutuals.Add((la, lb, r, x));
        }
        return new FaultSequenceTables(windings, machines, mutuals);
    }

    /// <summary>
    /// RC5 (SC equivalent writing): put the resolved fault-sequence tables into the built model (after
    /// <see cref="ApplySequenceData"/>, before dispatch). The lines are the first branches of the built model, in file
    /// order, and each match is checked by name (an internal error otherwise). Returns <paramref name="model"/> itself
    /// when <paramref name="tables"/> is null.
    /// </summary>
    private static GridModel ApplyFaultSequence(GridModel model, MappedCase dtos, FaultSequenceTables? tables)
    {
        if (tables is null) return model;
        IReadOnlyList<Branch> all = model.Branches;
        if (all.Count != dtos.Lines.Count + dtos.Transformers.Count)
            throw new InvalidOperationException($"RC5: the built model holds {all.Count} branches for {dtos.Lines.Count} lines and {dtos.Transformers.Count} transformers written (internal error).");
        Branch LineAt(int i)
        {
            Branch b = all[i];
            if (!string.Equals(b.Name, dtos.Lines[i].Name, StringComparison.Ordinal))
                throw new InvalidOperationException($"RC5: branch {i} is '{b.Name}' in the model but line '{dtos.Lines[i].Name}' in the network file (internal error).");
            return b;
        }

        List<Branch>? branches = null;
        if (tables.Windings.Count > 0)
        {
            branches = new List<Branch>(all);
            foreach ((int i, LineWindingData d) in tables.Windings)
                branches[i] = LineAt(i) with
                {
                    FromWinding = d.FromWinding,
                    ToWinding = d.ToWinding,
                    FromNeutralXPu = d.FromNeutralXPu ?? 0.0,
                    ToNeutralXPu = d.ToNeutralXPu ?? 0.0,
                };
        }
        List<Generator>? fleet = null;
        if (tables.Machines.Count > 0)
        {
            IReadOnlyList<Generator> gens = model.AllGenerators;
            if (gens.Count != dtos.Generators.Count)
                throw new InvalidOperationException($"RC5: the built model holds {gens.Count} generators for {dtos.Generators.Count} written (internal error).");
            fleet = new List<Generator>(gens);
            foreach ((int i, MachineZeroSequence z) in tables.Machines)
            {
                if (!string.Equals(gens[i].Name, dtos.Generators[i].Name, StringComparison.Ordinal))
                    throw new InvalidOperationException($"RC5: generator {i} is '{gens[i].Name}' in the model but '{dtos.Generators[i].Name}' in the network file (internal error).");
                fleet[i] = gens[i] with { ZeroSequenceZPu = z };
            }
        }
        List<ZeroSequenceMutual>? mutuals = null;
        if (tables.Mutuals.Count > 0)
        {
            mutuals = new List<ZeroSequenceMutual>(tables.Mutuals.Count);
            foreach ((int a, int b, double r, double x) in tables.Mutuals)
            {
                Branch ba = LineAt(a), bb = LineAt(b);
                mutuals.Add(new ZeroSequenceMutual(a, ba.From, ba.To, b, bb.From, bb.To, r, x));
            }
        }
        return model.CopyWith(generators: fleet, branches: branches, zeroSequenceMutuals: mutuals, generatorsAreWholeFleet: fleet is not null);
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

    /// <summary>RC3 P8: a placed motor row (network.schema.json motor). Every member but description is required by
    /// the schema; each value is checked again here (finite, and positive or not negative as its meaning needs) so the
    /// loader does not rest on the schema alone.</summary>
    private static PlacedMotor MapMotor(JsonElement e, string at, List<string> problems)
    {
        string name = "", description = "";
        bool hasDescription = false;
        int bus = 0;
        double ratedMva = double.NaN, ratedKv = double.NaN, ra = double.NaN, xa = double.NaN, xm = double.NaN, r1 = double.NaN, x1 = double.NaN, h = double.NaN;
        double c0 = 0, c1 = 0, c2 = 0;
        bool hasLoad = false;
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": name = p.Value.GetString()!; break;
                case "bus": bus = JsonWhole.Int(p); break;
                case "description": description = p.Value.GetString()!; hasDescription = true; break;
                case "ratedMva": ratedMva = p.Value.GetDouble(); break;
                case "ratedKv": ratedKv = p.Value.GetDouble(); break;
                case "raPu": ra = p.Value.GetDouble(); break;
                case "xaPu": xa = p.Value.GetDouble(); break;
                case "xmPu": xm = p.Value.GetDouble(); break;
                case "r1Pu": r1 = p.Value.GetDouble(); break;
                case "x1Pu": x1 = p.Value.GetDouble(); break;
                case "inertiaH": h = p.Value.GetDouble(); break;
                case "loadTorque":
                    hasLoad = true;
                    foreach (JsonProperty c in p.Value.EnumerateObject())
                    {
                        double v = c.Value.GetDouble();
                        switch (c.Name)
                        {
                            case "constantPu": c0 = v; break;
                            case "linearPu": c1 = v; break;
                            case "quadraticPu": c2 = v; break;
                            default: throw new InvalidDataException($"motor loadTorque: unexpected member '{c.Name}'.");
                        }
                        if (!double.IsFinite(v)) problems.Add($"{at}/loadTorque/{c.Name}: {v.ToString("R", CultureInfo.InvariantCulture)} is not a finite torque coefficient (pu of the motor torque base).");
                    }
                    break;
                default: throw new InvalidDataException($"motor: unexpected member '{p.Name}'.");
            }
        }
        static string R(double x) => x.ToString("R", CultureInfo.InvariantCulture);
        foreach ((string key, double v, bool strict, string unit) in new[]
        {
            ("ratedMva", ratedMva, true, "MVA"), ("ratedKv", ratedKv, true, "kV"), ("raPu", ra, false, "pu on the motor base"),
            ("xaPu", xa, false, "pu on the motor base"), ("xmPu", xm, true, "pu on the motor base"), ("r1Pu", r1, true, "pu on the motor base"),
            ("x1Pu", x1, false, "pu on the motor base"), ("inertiaH", h, true, "s on ratedMva"),
        })
        {
            if (double.IsNaN(v)) problems.Add($"{at}: motor '{name}' has no {key} ({unit}); every motor parameter is required.");
            else if (!double.IsFinite(v) || (strict ? v <= 0 : v < 0))
                problems.Add($"{at}/{key}: {R(v)} is not a finite {(strict ? "positive" : "non-negative")} value ({unit}).");
        }
        if (!hasLoad) problems.Add($"{at}: motor '{name}' has no loadTorque; give it (an empty object is a no-load start).");
        return new PlacedMotor(name, bus, ratedMva, ratedKv, ra, xa, xm, r1, x1, h, new MotorLoadTorque(c0, c1, c2), hasDescription ? description : null);
    }

    /// <summary>RC3 P8: the motor rules the schema cannot express. Each motor's bus must be a bus of the case; motor
    /// names are unique; and a motor may not share a demand's name, because the motor is a study device connected by
    /// the motor acceleration study and must not also be counted as static demand in the pre-start network.</summary>
    private static void CheckMotors(MappedCase dtos, List<string> problems)
    {
        if (dtos.Motors.Count == 0) return;
        var buses = new HashSet<int>(dtos.Buses.Select(b => b.Id));
        var demands = new HashSet<string>(dtos.Demands.Where(d => d.Name is not null).Select(d => d.Name!), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((PlacedMotor m, string at) in dtos.Motors)
        {
            if (!buses.Contains(m.Bus))
                problems.Add($"{at}/bus: motor '{m.Name}' is placed at bus {m.Bus.ToString(CultureInfo.InvariantCulture)}, which no network file defines.");
            if (!seen.Add(m.Name))
                problems.Add($"{at}/name: motor name '{m.Name}' is used by more than one motor; motor names must be unique.");
            if (demands.Contains(m.Name))
                problems.Add($"{at}/name: motor '{m.Name}' has the name of a demand; a placed motor is a study device, not static demand, so the pre-start network must not also count it as a demand row (rename one, or remove the demand if it is this motor).");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>True when <paramref name="full"/> lies inside the folder <paramref name="dir"/> (both full paths). GR-655:
    /// compared by the operating system's rule (case-insensitive on Windows, exact elsewhere), as the case editor's
    /// IsInside does; a case-insensitive test on Linux treated two different folders as one. A link below the folder is read
    /// one hop (IO-17) and must stay inside it.</summary>
    internal static bool IsInside(string dir, string full)
    {
        string d = Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar;
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(d, cmp)) return false;
        // IO-17: the text test alone let a symbolic link or junction inside the folder lead out of it (to a network file,
        // whose schema errors then quoted its values). Each component below the folder that is a link is read one hop
        // (not followed, so no network call is made) and its target must be a local path inside the folder too.
        string at = Path.TrimEndingDirectorySeparator(dir);
        foreach (string part in full[d.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, part);
            string? target;
            try { target = new FileInfo(at).LinkTarget; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
            if (target is null) continue;
            if (GridSim.Core.Security.SideFile.IsNetworkOrDevice(target)) return false;
            string resolved;
            try { resolved = Path.GetFullPath(target, Path.GetDirectoryName(at)!); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            if (GridSim.Core.Security.SideFile.IsNetworkOrDevice(resolved) || !resolved.StartsWith(d, cmp)) return false;
        }
        return true;
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

    /// <summary>
    /// RC5: a unit protection file (gridsim-unit-protection/1) as a unit protection set, with the v2 rules: each template
    /// is checked as a v2 case's local template is (kind spec, provenance keys, unique id@rev) and must be an active
    /// unitProtection template; each unit takes one template of the file; a unit's own values are laid on its template by
    /// the same code as a v2 per-unit override (<see cref="UnitAssignments"/>). The document is already schema-valid.
    /// Every problem is listed in one <see cref="InvalidDataException"/>.
    /// </summary>
    internal static Protection.UnitProtectionSet UnitProtectionDocument(JsonElement root, string shown, KindSpecRegistry kinds)
    {
        var problems = new List<string>();
        var templates = new TemplateIndex();
        int ti = 0;
        foreach (JsonElement t in root.GetProperty("templates").EnumerateArray())
        {
            string at = $"/templates/{ti++}";
            var tpl = Template.From(t.Clone(), $"{shown}, {at}", local: true);
            if (tpl.Kind != UnitProtectionKind)
            {
                problems.Add($"{shown}: {at}/kind: template {tpl.Ref} is kind '{tpl.Kind}'; a unit protection file holds '{UnitProtectionKind}' templates only.");
                continue;
            }
            if (tpl.Placeholder)
            {
                problems.Add($"{shown}: {at}/status: template {tpl.Ref} is a placeholder; a placeholder holds no values and can never be bound to a unit.");
                continue;
            }
            foreach (SchemaError e in kinds.CheckParams(tpl.Kind, tpl.Params, tpl.Placeholder, at + "/params")) problems.Add($"{shown}: {e}");
            foreach (JsonProperty prov in tpl.Provenance.EnumerateObject())
                if (prov.Name != AssetClass.DefaultKey && !tpl.Params.TryGetProperty(prov.Name, out _))
                    problems.Add($"{shown}: {at}/provenance/{JsonSchemaSubset.EscapeToken(prov.Name)}: provenance is given for '{prov.Name}', which is not a parameter of this template.");
            if (!templates.AddLocal(tpl)) problems.Add($"{shown}: {at}: template {tpl.Ref} is defined twice.");
        }
        RefuseFile(shown, "the templates", problems);

        var classOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        var unitOverrides = new List<(string At, string Unit, JsonElement Override)>();
        int ui = 0;
        foreach (JsonElement u in root.GetProperty("units").EnumerateArray())
        {
            string at = $"/units/{ui++}";
            string unit = u.GetProperty("unit").GetString()!;
            string reference = u.GetProperty("template").GetString()!;
            if (templates.Find(reference) is not Template tpl)
            {
                problems.Add($"{shown}: {at}/template: unit '{unit}' takes template {reference}, which is not a template of this file.");
                continue;
            }
            if (!classOf.TryAdd(unit, tpl.ClassName))
            {
                problems.Add($"{shown}: {at}/unit: unit '{unit}' is listed more than once; give each unit one entry (its template and its own values).");
                continue;
            }
            order.Add(unit);
            bool hasParams = u.TryGetProperty("params", out _);
            if (hasParams && !u.TryGetProperty("reason", out _))
                problems.Add($"{shown}: {at}/reason: unit '{unit}' sets values of its own, so it must say why (reason).");
            else if (!hasParams && (u.TryGetProperty("reason", out _) || u.TryGetProperty("provenance", out _)))
                problems.Add($"{shown}: {at}: unit '{unit}' gives a reason or provenance but no params; leave them out, or give the unit's own values.");
            else if (hasParams) unitOverrides.Add((at, unit, u));
        }
        RefuseFile(shown, "the units", problems);

        List<Protection.UnitProtectionAssignment> units = UnitAssignments(order, classOf, unitOverrides, templates, kinds, shown,
            $"unit protection file '{shown}'", problems, p => RefuseFile(shown, "the units' own values", p));
        var classes = templates.All.Select(t => new Protection.UnitProtectionClass(t.ClassName, t.Description, t.ToUnitProtectionLayer())).ToList();
        try { return new Protection.UnitProtectionSet(classes, units); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{shown}: {ex.Message}", ex); }
    }

    private static void RefuseFile(string shown, string stage, List<string> problems)
    {
        if (problems.Count == 0) return;
        string list = string.Join(Environment.NewLine + "  ", problems.Take(MaxListedProblems));
        string more = problems.Count > MaxListedProblems ? $"{Environment.NewLine}  ... and {(problems.Count - MaxListedProblems).ToString(CultureInfo.InvariantCulture)} more" : "";
        throw new InvalidDataException(
            $"{shown}: unit protection file refused at {stage}: {problems.Count.ToString(CultureInfo.InvariantCulture)} problem(s):{Environment.NewLine}  {list}{more}");
    }
}
