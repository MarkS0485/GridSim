using System.Globalization;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Schema;
using GridSim.Ui.Assets.Keys;

namespace GridSim.Ui.Assets.Editing.Templates;

// UI programme Phase 4.4 (2026-10-21): what a v2 case says about its templates, read from the authoring session AS IT IS
// NOW (unsaved edits included), for the template editor and the TEMPLATES node of the Author tree:
//   - the case's own templates (the manifest's "localTemplates", editable);
//   - the library templates it imports (the manifest's "imports", read from the library files under the data root,
//     read only: a library template is fixed; the case changes its values with an override);
//   - the binding rules (the manifest's "bindings": an element whose type matches matchType is bound to the template,
//     unless the element names its own template);
//   - the overrides (the manifest's "overrides": values this case sets in place of a template's own, each with its reason);
//   - every generator of the network files, with the template it names itself and the template it is bound to.
// The template a generator is bound to is decided by GridSim.Core's own binding resolver (AssetBindingSet.Resolve, the
// one every consumer of the loaded model calls: inline first, then the first exact rule, then the first prefix rule), fed
// with the case's rules; nothing here re-implements that precedence. The parameter values a generator ends up with are
// NOT computed here: TemplateResolution loads the case with the v2 loader for that.

/// <summary>One parameter value written in a template (or an override): its name and the value token as written.</summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Literal">The value exactly as written: a bare value's token, or the "v" token of the object form.</param>
/// <param name="ObjectForm">True when it is written in the object form {v, unit, prov} (its unit and provenance are kept
/// when the value is set).</param>
public sealed record TemplateParamLiteral(string Name, string Literal, bool ObjectForm);

/// <summary>One template the case can see: a local template of the manifest, or a library template it imports.</summary>
public sealed class TemplateInfo
{
    internal TemplateInfo(string id, int rev, string kind, string status, string title, string? description, bool isLocal,
        int localIndex, CaseRef? record, string source, IReadOnlyList<TemplateParamLiteral> parameters, IReadOnlyList<string> controllerRefs)
    {
        Id = id;
        Rev = rev;
        Kind = kind;
        Status = status;
        Title = title;
        Description = description;
        IsLocal = isLocal;
        LocalIndex = localIndex;
        Record = record;
        Source = source;
        Params = parameters;
        ControllerRefs = controllerRefs;
    }

    /// <summary>The template id ("gridsim.local.wind-gfl").</summary>
    public string Id { get; }

    /// <summary>The revision (from 1).</summary>
    public int Rev { get; }

    /// <summary>The reference elements, bindings and overrides use: id@rev.</summary>
    public string Ref => Id + "@" + Rev.ToString(CultureInfo.InvariantCulture);

    /// <summary>The template kind ("gridFollowingInverter").</summary>
    public string Kind { get; }

    /// <summary>"active" or "placeholder".</summary>
    public string Status { get; }

    /// <summary>True for a placeholder (every value null; never bound, never overridden).</summary>
    public bool IsPlaceholder => Status == "placeholder";

    /// <summary>The template's title.</summary>
    public string Title { get; }

    /// <summary>The template's description (the asset class's mode text), or null.</summary>
    public string? Description { get; }

    /// <summary>True for a template of the case's own manifest (editable); false for an imported library template.</summary>
    public bool IsLocal { get; }

    /// <summary>The position in the manifest's localTemplates (-1 for a library template).</summary>
    public int LocalIndex { get; }

    /// <summary>The template's record in the manifest (a local template), or null (a library template).</summary>
    public CaseRef? Record { get; }

    /// <summary>Where it is written ("uk.case.json /localTemplates/0", "library library/x.json").</summary>
    public string Source { get; }

    /// <summary>The parameters as written, in the template's own order.</summary>
    public IReadOnlyList<TemplateParamLiteral> Params { get; }

    /// <summary>The controller references the template's controller slots make (exciter, PSS, governor), non-null ones only.</summary>
    public IReadOnlyList<string> ControllerRefs { get; }

    /// <summary>A library template's JSON as the library file holds it (Duplicate copies it into the case), else null.</summary>
    internal JsonElement? LibraryJson { get; init; }

    /// <summary>True when the kind is an inverter template kind (a generator bound to it gets an inverter controller).</summary>
    public bool IsInverter => TemplateKinds.IsInverterKind(Kind);

    /// <summary>The value written for <paramref name="param"/>, or null when the template leaves it out.</summary>
    public TemplateParamLiteral? Param(string param) => Params.FirstOrDefault(p => string.Equals(p.Name, param, StringComparison.Ordinal));

    /// <inheritdoc />
    public override string ToString() => Ref + " (" + Kind + (IsLocal ? ", local" : ", library") + ")";
}

/// <summary>One binding rule of the manifest: an element of type <paramref name="MatchType"/> (a trailing * makes it a
/// prefix) is bound to <paramref name="Template"/>, unless it names its own template.</summary>
/// <param name="Index">Its position in the manifest's bindings.</param>
public sealed record TemplateBindingRule(int Index, string MatchType, string Template)
{
    /// <summary>The rule in words ("type 'onshore' -> gridsim.local.wind-gfl@1").</summary>
    public string Text => "type '" + MatchType + "' -> " + Template;
}

/// <summary>One override of the manifest: values this case sets in place of <paramref name="Template"/>'s own.</summary>
/// <param name="Index">Its position in the manifest's overrides.</param>
/// <param name="Template">The template reference it shadows.</param>
/// <param name="Params">The values it sets, as written.</param>
/// <param name="Reason">Why (required by the schema).</param>
public sealed record TemplateOverrideEntry(int Index, string Template, IReadOnlyList<TemplateParamLiteral> Params, string Reason);

/// <summary>One generator of the case and the template it is bound to.</summary>
/// <param name="Record">The generator's record.</param>
/// <param name="Name">Its name, or null when it has none.</param>
/// <param name="Type">Its type ("onshore"), or null.</param>
/// <param name="InlineTemplate">The template it names itself (its "template" member), or null.</param>
/// <param name="Template">The template it is bound to (its own, else the binding rule that matches its type), or null
/// when it is unbound (it stays passive).</param>
/// <param name="Route">How it is bound, in words ("its own template member", "binding rule #1 type 'onshore'", "unbound").</param>
public sealed record GeneratorBindingInfo(CaseRef Record, string? Name, string? Type, string? InlineTemplate, string? Template, string Route)
{
    /// <summary>The generator as a list shows it ("'South Solar' (solar)").</summary>
    public string Label => (Name is null ? "#" + Record.Index.ToString(CultureInfo.InvariantCulture) : "'" + Name + "'") + (Type is null ? "" : " (" + Type + ")");
}

/// <summary>
/// The templates of a v2 case as the authoring session holds them now (Phase 4.4): local and imported templates, binding
/// rules, overrides and every generator's binding. Read again after every change (it is a snapshot).
/// </summary>
public sealed class CaseTemplates
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The data root the v2 run pipeline resolves library imports against: the <c>data</c> folder beside the
    /// app (SimHost.LoadV2's rule), used when the caller names none.</summary>
    public static string DefaultDataRoot => Path.Combine(AppContext.BaseDirectory, "data");

    private CaseTemplates(IReadOnlyList<TemplateInfo> local, IReadOnlyList<TemplateInfo> library, IReadOnlyList<TemplateBindingRule> bindings,
        IReadOnlyList<TemplateOverrideEntry> overrides, IReadOnlyList<GeneratorBindingInfo> generators, IReadOnlyList<string> notes)
    {
        Local = local;
        Library = library;
        Bindings = bindings;
        Overrides = overrides;
        Generators = generators;
        Notes = notes;
    }

    /// <summary>The case's own templates (manifest order).</summary>
    public IReadOnlyList<TemplateInfo> Local { get; }

    /// <summary>The library templates the case imports (import order; read only).</summary>
    public IReadOnlyList<TemplateInfo> Library { get; }

    /// <summary>Every template the case can see: local first, then imported (the loader's order).</summary>
    public IEnumerable<TemplateInfo> All => Local.Concat(Library);

    /// <summary>The binding rules (manifest order).</summary>
    public IReadOnlyList<TemplateBindingRule> Bindings { get; }

    /// <summary>The overrides (manifest order).</summary>
    public IReadOnlyList<TemplateOverrideEntry> Overrides { get; }

    /// <summary>Every generator of the case's network files (file order) and its binding.</summary>
    public IReadOnlyList<GeneratorBindingInfo> Generators { get; }

    /// <summary>What could not be read (a library file that is missing, an import pattern that matches nothing).</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>The template a reference names (the first one, as the loader keeps the first), or null.</summary>
    public TemplateInfo? Find(string reference) => All.FirstOrDefault(t => string.Equals(t.Ref, reference, StringComparison.Ordinal));

    /// <summary>The generators bound to <paramref name="reference"/> (by their own template member or by a rule).</summary>
    public IReadOnlyList<GeneratorBindingInfo> GeneratorsBoundTo(string reference)
        => Generators.Where(g => string.Equals(g.Template, reference, StringComparison.Ordinal)).ToList();

    /// <summary>The binding rules that name <paramref name="reference"/>.</summary>
    public IReadOnlyList<TemplateBindingRule> RulesFor(string reference)
        => Bindings.Where(b => string.Equals(b.Template, reference, StringComparison.Ordinal)).ToList();

    /// <summary>The override entries that shadow <paramref name="reference"/>.</summary>
    public IReadOnlyList<TemplateOverrideEntry> OverridesOf(string reference)
        => Overrides.Where(o => string.Equals(o.Template, reference, StringComparison.Ordinal)).ToList();

    /// <summary>The override of <paramref name="param"/> of <paramref name="reference"/> (the first entry that sets it), or
    /// null when the case does not override it.</summary>
    public (TemplateOverrideEntry Entry, TemplateParamLiteral Value)? OverrideOf(string reference, string param)
    {
        foreach (TemplateOverrideEntry o in OverridesOf(reference))
            if (o.Params.FirstOrDefault(p => string.Equals(p.Name, param, StringComparison.Ordinal)) is TemplateParamLiteral v) return (o, v);
        return null;
    }

    /// <summary>The template a generator is bound to by the case's binding rules and its own template member, decided by
    /// GridSim.Core's resolver (<see cref="AssetBindingSet.Resolve"/>), with how (see <see cref="GeneratorBindingInfo.Route"/>).</summary>
    internal static (string? Template, string Route) Resolve(IReadOnlyList<TemplateBindingRule> rules, string? inlineTemplate, string? type)
    {
        if (!string.IsNullOrWhiteSpace(inlineTemplate)) return (inlineTemplate, "its own template member");
        // The rule each binding stands for is told by its position: the resolver returns the "class" of the winning rule,
        // and here each rule's class is its index, so the winner is named without restating the precedence.
        var byIndex = new AssetBindingSet(rules.Select(r => new AssetBinding(r.MatchType, r.Index.ToString(Inv))));
        string? hit = byIndex.Resolve(null, type);
        if (hit is null) return (null, "unbound: no binding rule matches its type" + (type is null ? " (it has no type)" : " '" + type + "'") + ", so it stays passive");
        TemplateBindingRule rule = rules.First(r => r.Index.ToString(Inv) == hit);
        return (rule.Template, "binding rule #" + rule.Index.ToString(Inv) + " type '" + rule.MatchType + "'");
    }

    /// <summary>
    /// Everything in the case that names <paramref name="reference"/>: binding rules, overrides, other templates'
    /// controller slots and network elements' template members, each in words with its file and pointer. A template with
    /// any of these cannot be deleted (the case would no longer load).
    /// </summary>
    internal static IReadOnlyList<string> ReferrersOf(CaseSession s, CaseTemplates t, string reference)
    {
        var list = new List<string>();
        CaseDocument manifest = s.Documents[0];
        foreach (TemplateBindingRule b in t.RulesFor(reference))
            list.Add(manifest.FileName + " /bindings/" + b.Index.ToString(Inv) + " (binding rule for type '" + b.MatchType + "')");
        foreach (TemplateOverrideEntry o in t.OverridesOf(reference))
            list.Add(manifest.FileName + " /overrides/" + o.Index.ToString(Inv) + " (override: " + o.Reason + ")");
        foreach (TemplateInfo other in t.Local.Where(x => x.ControllerRefs.Contains(reference, StringComparer.Ordinal)))
            list.Add(manifest.FileName + " /localTemplates/" + other.LocalIndex.ToString(Inv) + " (controller slot of " + other.Ref + ")");
        foreach (CaseDocument d in s.Documents.Where(d => d.SchemaFile == CaseSession.V2NetworkSchema && d.Exists))
            foreach (JsonSourceMember array in d.Text.Root.Kind == JsonSourceKind.Object ? d.Text.Root.Members : Array.Empty<JsonSourceMember>())
            {
                if (array.Value.Kind != JsonSourceKind.Array) continue;
                for (int i = 0; i < array.Value.Elements.Count; i++)
                {
                    JsonSourceNode e = array.Value.Elements[i];
                    if (e.Kind == JsonSourceKind.Object && string.Equals(RecordRead.Str(e, "template", d.KeyComparer), reference, StringComparison.Ordinal))
                        list.Add(d.FileName + " /" + array.Name + "/" + i.ToString(Inv) + " " + RecordRead.Describe(d, e, i, "/" + array.Name) + " (its own template member)");
                }
            }
        return list;
    }

    /// <summary>
    /// Read the templates of the case <paramref name="a"/> holds now. <paramref name="dataRoot"/> is the folder library
    /// imports are relative to (<see cref="DefaultDataRoot"/> when null). A case that is not a v2 case manifest has none.
    /// </summary>
    public static CaseTemplates Read(CaseAuthoring a, string? dataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        var notes = new List<string>();
        CaseSession s = a.Session;
        if (a.Format != AuthoringFormat.V2Case || !s.Documents[0].Exists)
            return new CaseTemplates(Array.Empty<TemplateInfo>(), Array.Empty<TemplateInfo>(), Array.Empty<TemplateBindingRule>(),
                Array.Empty<TemplateOverrideEntry>(), Array.Empty<GeneratorBindingInfo>(), notes);

        CaseDocument d = s.Documents[0];
        JsonSourceText text = d.Text;
        JsonSourceNode root = text.Root;
        StringComparer cmp = d.KeyComparer;

        // ---- local templates (from the session's text, so an unsaved edit shows at once) ----
        var local = new List<TemplateInfo>();
        if (root.Kind == JsonSourceKind.Object && RecordRead.Member(root, "localTemplates", cmp) is { Kind: JsonSourceKind.Array } locals)
            for (int i = 0; i < locals.Elements.Count; i++)
            {
                JsonSourceNode t = locals.Elements[i];
                if (t.Kind != JsonSourceKind.Object) continue;   // a schema error, which Check reports
                var at = new CaseRef(d.Id, "/localTemplates", i, d.FileName);
                local.Add(new TemplateInfo(
                    RecordRead.Str(t, "id", cmp) ?? "(no id)",
                    RecordRead.Num(t, "rev", cmp) is double rv ? (int)rv : 0,
                    RecordRead.Str(t, "kind", cmp) ?? "",
                    RecordRead.Str(t, "status", cmp) ?? "",
                    RecordRead.Str(t, "title", cmp) ?? "",
                    RecordRead.Str(t, "description", cmp),
                    isLocal: true, i, at, d.FileName + " /localTemplates/" + i.ToString(Inv),
                    ParamsOf(text, RecordRead.Member(t, "params", cmp), cmp),
                    ControllersOf(RecordRead.Member(t, "controllers", cmp))));
            }

        // ---- imported library templates (read only) ----
        var library = new List<TemplateInfo>();
        if (root.Kind == JsonSourceKind.Object && RecordRead.Member(root, "imports", cmp) is { Kind: JsonSourceKind.Array } imports && imports.Elements.Count > 0)
            ReadImports(text.ElementOf(imports), dataRoot ?? DefaultDataRoot, library, notes);

        // ---- binding rules and overrides ----
        var bindings = new List<TemplateBindingRule>();
        if (root.Kind == JsonSourceKind.Object && RecordRead.Member(root, "bindings", cmp) is { Kind: JsonSourceKind.Array } bs)
            for (int i = 0; i < bs.Elements.Count; i++)
                if (bs.Elements[i] is { Kind: JsonSourceKind.Object } b && RecordRead.Str(b, "matchType", cmp) is string m && RecordRead.Str(b, "template", cmp) is string tr)
                    bindings.Add(new TemplateBindingRule(i, m, tr));
        var overrides = new List<TemplateOverrideEntry>();
        if (root.Kind == JsonSourceKind.Object && RecordRead.Member(root, "overrides", cmp) is { Kind: JsonSourceKind.Array } os)
            for (int i = 0; i < os.Elements.Count; i++)
                if (os.Elements[i] is { Kind: JsonSourceKind.Object } o && RecordRead.Str(o, "template", cmp) is string ot)
                    overrides.Add(new TemplateOverrideEntry(i, ot, ParamsOf(text, RecordRead.Member(o, "params", cmp), cmp), RecordRead.Str(o, "reason", cmp) ?? ""));

        // ---- every generator and its binding (Core's resolver over the rules above) ----
        var generators = new List<GeneratorBindingInfo>();
        foreach (AuthoringRecordSet set in a.Collections().Where(c => c.Role == CaseRoles.Generators))
        {
            CaseDocument gd = s.Documents[set.Document];
            if (!gd.Exists || gd.Text.Resolve(set.ArrayPointer) is not { Kind: JsonSourceKind.Array } arr) continue;
            for (int i = 0; i < arr.Elements.Count; i++)
            {
                if (arr.Elements[i] is not { Kind: JsonSourceKind.Object } g) continue;
                string? inline = RecordRead.Str(g, "template", gd.KeyComparer);
                string? type = RecordRead.Str(g, "type", gd.KeyComparer);
                (string? bound, string route) = Resolve(bindings, inline, type);
                generators.Add(new GeneratorBindingInfo(new CaseRef(gd.Id, set.ArrayPointer, i, gd.FileName),
                    RecordRead.Str(g, "name", gd.KeyComparer), type, inline, bound, route));
            }
        }
        return new CaseTemplates(local, library, bindings, overrides, generators, notes);
    }

    // The parameters of a params object as written: a bare value's token, or the object form's v token.
    private static List<TemplateParamLiteral> ParamsOf(JsonSourceText text, JsonSourceNode? ps, StringComparer cmp)
    {
        var list = new List<TemplateParamLiteral>();
        if (ps is not { Kind: JsonSourceKind.Object }) return list;
        foreach (JsonSourceMember m in ps.Members)
        {
            bool obj = m.Value.Kind == JsonSourceKind.Object;
            JsonSourceNode v = obj && RecordRead.Member(m.Value, "v", cmp) is JsonSourceNode inner ? inner : m.Value;
            list.Add(new TemplateParamLiteral(m.Name, text.RawText(v), obj));
        }
        return list;
    }

    private static List<string> ControllersOf(JsonSourceNode? controllers)
    {
        var list = new List<string>();
        if (controllers is not { Kind: JsonSourceKind.Object }) return list;
        foreach (JsonSourceMember m in controllers.Members)
            if (m.Value.Kind == JsonSourceKind.String) list.Add(m.Value.StringValue!);
        return list;
    }

    // The library templates the imports take. The library files are read from the data root as the run pipeline reads
    // them; each pattern takes templates as case.schema.json's templatePattern describes (an exact id@rev, an unpinned id
    // at its highest revision, an id prefix ending ".*", or gridsim.*). This is a READ-ONLY view for the editor: whether
    // the case loads with these imports is the loader's verdict (Check), never this list's.
    private static void ReadImports(JsonElement imports, string dataRoot, List<TemplateInfo> into, List<string> notes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement imp in imports.EnumerateArray())
        {
            if (imp.ValueKind != JsonValueKind.Object || !imp.TryGetProperty("library", out JsonElement lib) || lib.ValueKind != JsonValueKind.String) continue;
            string rel = lib.GetString()!;
            string full = Path.GetFullPath(Path.Combine(dataRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(full))
            {
                notes.Add("library '" + rel + "' was not found under the data root " + dataRoot + "; its templates are not listed.");
                continue;
            }
            List<JsonElement> available;
            try
            {
                using JsonDocument doc = JsonSchemaSubset.ParseFile(full);
                available = doc.RootElement.TryGetProperty("templates", out JsonElement ts) && ts.ValueKind == JsonValueKind.Array
                    ? ts.EnumerateArray().Select(t => t.Clone()).ToList() : new List<JsonElement>();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                notes.Add("library '" + rel + "' could not be read: " + ex.Message);
                continue;
            }
            if (!imp.TryGetProperty("templates", out JsonElement patterns) || patterns.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement pat in patterns.EnumerateArray())
            {
                if (pat.ValueKind != JsonValueKind.String) continue;
                List<JsonElement> hits = Match(available, pat.GetString()!);
                if (hits.Count == 0) notes.Add("import pattern '" + pat.GetString() + "' matches no template in '" + rel + "'.");
                foreach (JsonElement t in hits)
                {
                    TemplateInfo info = FromElement(t, "library " + rel);
                    if (seen.Add(info.Ref)) into.Add(info);
                }
            }
        }
    }

    private static List<JsonElement> Match(List<JsonElement> available, string pattern)
    {
        static string Id(JsonElement t) => t.TryGetProperty("id", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        static int Rev(JsonElement t) => t.TryGetProperty("rev", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
        if (pattern == "gridsim.*") return available.ToList();
        if (pattern.EndsWith(".*", StringComparison.Ordinal)) return available.Where(t => Id(t).StartsWith(pattern[..^1], StringComparison.Ordinal)).ToList();
        if (pattern.Contains('@', StringComparison.Ordinal)) return available.Where(t => Id(t) + "@" + Rev(t).ToString(Inv) == pattern).ToList();
        List<JsonElement> same = available.Where(t => Id(t) == pattern).ToList();
        return same.Count == 0 ? same : new List<JsonElement> { same.OrderByDescending(Rev).First() };
    }

    private static TemplateInfo FromElement(JsonElement t, string source)
    {
        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var ps = new List<TemplateParamLiteral>();
        if (t.TryGetProperty("params", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty m in p.EnumerateObject())
            {
                bool obj = m.Value.ValueKind == JsonValueKind.Object;
                JsonElement v = obj && m.Value.TryGetProperty("v", out JsonElement inner) ? inner : m.Value;
                ps.Add(new TemplateParamLiteral(m.Name, v.GetRawText(), obj));
            }
        var ctl = new List<string>();
        if (t.TryGetProperty("controllers", out JsonElement c) && c.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty m in c.EnumerateObject())
                if (m.Value.ValueKind == JsonValueKind.String) ctl.Add(m.Value.GetString()!);
        int rev = t.TryGetProperty("rev", out JsonElement r) && r.ValueKind == JsonValueKind.Number ? (int)r.GetDouble() : 0;
        string? description = t.TryGetProperty("description", out JsonElement de) && de.ValueKind == JsonValueKind.String ? de.GetString() : null;
        return new TemplateInfo(Str(t, "id"), rev, Str(t, "kind"), Str(t, "status"), Str(t, "title"), description, isLocal: false, -1, null, source, ps, ctl)
        {
            LibraryJson = t.Clone(),
        };
    }
}
