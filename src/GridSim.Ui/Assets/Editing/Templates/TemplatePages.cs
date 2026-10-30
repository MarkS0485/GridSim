using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Schema;
using GridSim.Ui.Assets.Keys;

namespace GridSim.Ui.Assets.Editing.Templates;

// UI programme Phase 4.4 (2026-10-21): the page models of the template editor in AUTHOR mode (UI-neutral; the Avalonia
// view binds to them). They read the case through CaseTemplates (the session as it is now) and change it only through
// the TemplateOps operations, each run by CaseAuthoring.Apply (one transaction, one undo step, the Changed event), so a
// page is rebuilt after every change and never holds a stale value. Nothing here is a live value (AUTHOR reads no frame).
//   - TemplateEditorModel: one template (local: editable; library: read only). Its parameter grid has one row per
//     parameter of the template's KIND SPEC, in the kind file's order (unit, range, default, required or optional, enum
//     choices, help: all from the kind spec), through the same FieldGridModel row mechanics as every record (SET, RESET,
//     Enter; each edit one undo step, refused with the kind spec's own message). Below it: the binding rules and the
//     generators that use the template, and its overrides, each against the template's own value
//     ("template 0.05 -> override 0.08").
//   - GeneratorTemplateModel: on a generator's editor, the template it is bound to (and how), a link to that template,
//     the overrides of that template (an override is per template: every generator bound to it gets the value), and the
//     effective parameters as the v2 loader resolves them (TemplateResolution, on request).
//   - TemplatesPage: the TEMPLATES class node's overview: every template, rule, override and generator binding.
//   - TemplateOverridePage: one override (a node under its template), with RESET TO TEMPLATE.

/// <summary>One override of a template parameter, against the template's own value.</summary>
/// <param name="Template">The template reference the override shadows.</param>
/// <param name="Param">The parameter.</param>
/// <param name="Unit">Its unit from the kind spec, or "".</param>
/// <param name="TemplateValue">The template's own value as written, or "absent (...)" with the code default.</param>
/// <param name="OverrideValue">The override's value as written.</param>
/// <param name="Reason">The override entry's reason.</param>
/// <param name="EntryIndex">The override entry's position in the manifest's overrides.</param>
public sealed record TemplateOverrideRow(string Template, string Param, string Unit, string TemplateValue, string OverrideValue, string Reason, int EntryIndex)
{
    /// <summary>The override in words: "template 0.05 -> override 0.08".</summary>
    public string Text => "template " + TemplateValue + " -> override " + OverrideValue;

    /// <summary>The node id of this override in the Author tree.</summary>
    public string NodeId => TemplatePages.OverrideNodeId(Template, Param);
}

/// <summary>Shared helpers of the template pages.</summary>
public static class TemplatePages
{
    /// <summary>The Author tree's TEMPLATES class node id.</summary>
    public const string ClassNodeId = "class:templates";

    /// <summary>The TEMPLATES class node's label.</summary>
    public const string ClassNodeLabel = "TEMPLATES";

    /// <summary>The tree id of a library template's node (a local template's node is its record's, like any record).</summary>
    public static string LibraryNodeId(string reference) => "tpl:lib:" + reference;

    /// <summary>The tree id of an override node.</summary>
    public static string OverrideNodeId(string reference, string param) => "ovr:" + reference + ":" + param;

    /// <summary>The overrides of <paramref name="reference"/> in the case, one row per parameter, each against the
    /// template's own value (entry order, then parameter order).</summary>
    public static IReadOnlyList<TemplateOverrideRow> OverridesOf(CaseTemplates t, string reference)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(reference);
        TemplateInfo? tpl = t.Find(reference);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<TemplateOverrideRow>();
        foreach (TemplateOverrideEntry o in t.OverridesOf(reference))
            foreach (TemplateParamLiteral p in o.Params)
            {
                if (!seen.Add(p.Name)) continue;   // overridden twice: the loader refuses it; the first is shown (Check says why)
                string unit = tpl is null ? "" : TemplateKinds.ParamsOf(tpl.Kind).FirstOrDefault(x => x.Name == p.Name)?.Unit ?? "";
                rows.Add(new TemplateOverrideRow(reference, p.Name, unit, tpl is null ? "(template not found)" : TemplateOps.TemplateValueText(tpl, p.Name),
                    p.Literal, o.Reason, o.Index));
            }
        return rows;
    }

    /// <summary>The templates a generator can be bound to: every active inverter template the case can see, by reference
    /// (local first, then imported).</summary>
    public static IReadOnlyList<string> BindableTemplates(CaseTemplates t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return t.All.Where(x => x.IsInverter && !x.IsPlaceholder).Select(x => x.Ref).Distinct(StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// One template in AUTHOR mode (Phase 4.4): its identity, its kind-spec parameter grid (a local template's is editable,
/// a library template's is read only), the bindings that use it and its overrides.
/// </summary>
public sealed class TemplateEditorModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Why a library template's values are not edited in the case.</summary>
    public const string LibraryReadOnlyReason = "a library template is fixed (its id@rev is immutable); override its values for this case below, or Duplicate it as a local template to edit it.";

    internal TemplateEditorModel(CaseAuthoring authoring, CaseTemplates templates, TemplateInfo info, string? dataRoot)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(templates);
        Info = info ?? throw new ArgumentNullException(nameof(info));
        ParamSpecs = TemplateKinds.ParamsOf(info.Kind);
        CaseRef record = info.Record ?? new CaseRef(0, "/localTemplates", -1, authoring.Session.Documents[0].FileName);
        string kindFile = TemplateKinds.FileOf(info.Kind);
        Params = new FieldGridModel(authoring, record, info.Ref, "Parameters of a " + TemplateKinds.TitleOf(info.Kind).ToLowerInvariant() + " template (from " + (kindFile.Length > 0 ? kindFile : "no kind spec") + ")",
            TemplateKinds.DescriptionOf(info.Kind), kindFile, "/params",
            grid => Rows(grid, info),
            (field, typed) => info.IsLocal
                ? authoring.Apply(s => TemplateOps.SetParam(s, CaseTemplates.Read(authoring, dataRoot), info.LocalIndex, field, typed))
                : AuthoringResult.Refused(field + ": refused, " + LibraryReadOnlyReason),
            field => info.IsLocal
                ? authoring.Apply(s => TemplateOps.ResetParam(s, CaseTemplates.Read(authoring, dataRoot), info.LocalIndex, field))
                : AuthoringResult.Refused(field + ": refused, " + LibraryReadOnlyReason));
        Rules = templates.RulesFor(info.Ref);
        BoundGenerators = templates.GeneratorsBoundTo(info.Ref);
        Overrides = TemplatePages.OverridesOf(templates, info.Ref);
        OverrideChoices = ParamSpecs.Select(p => p.Name).ToList();
    }

    /// <summary>The template.</summary>
    public TemplateInfo Info { get; }

    /// <summary>Its reference (id@rev).</summary>
    public string Ref => Info.Ref;

    /// <summary>The heading: the reference and the title.</summary>
    public string Heading => Info.Ref + "  " + Info.Title;

    /// <summary>The kind and its title ("gridFormingInverter: Grid-forming inverter").</summary>
    public string KindText => Info.Kind + ": " + TemplateKinds.TitleOf(Info.Kind);

    /// <summary>Where the template is written and whether it can be edited here.</summary>
    public string SourceText => Info.Source + (Info.IsLocal ? " (a template of this case: editable)" : " (read only: " + LibraryReadOnlyReason + ")");

    /// <summary>The template's status in words.</summary>
    public string StatusText => Info.IsPlaceholder
        ? "placeholder: every value is null (a named slot for something not yet modelled); it cannot be bound or overridden."
        : "active" + (Info.Description is { Length: > 0 } d ? "; description (the asset class's mode): " + d : "");

    /// <summary>True for a local template (its parameters are edited in <see cref="Params"/>).</summary>
    public bool IsEditable => Info.IsLocal && !Info.IsPlaceholder;

    /// <summary>The kind spec's parameters, in the kind file's order (the grid has one row for each, in this order).</summary>
    public IReadOnlyList<TemplateParamInfo> ParamSpecs { get; }

    /// <summary>The parameter grid: one row per kind-spec parameter (then any written parameter the kind does not declare).</summary>
    public FieldGridModel Params { get; }

    /// <summary>The binding rules that name this template.</summary>
    public IReadOnlyList<TemplateBindingRule> Rules { get; }

    /// <summary>True when a rule names it.</summary>
    public bool HasRules => Rules.Count > 0;

    /// <summary>The generators bound to it (by their own template member or by a rule).</summary>
    public IReadOnlyList<GeneratorBindingInfo> BoundGenerators { get; }

    /// <summary>The bound generators in words ("3 generator(s) use it").</summary>
    public string BoundText => BoundGenerators.Count == 0 ? "No generator is bound to this template." : BoundGenerators.Count.ToString(Inv) + " generator(s) are bound to it:";

    /// <summary>The template's overrides in this case, each against the template's own value.</summary>
    public IReadOnlyList<TemplateOverrideRow> Overrides { get; }

    /// <summary>True when the case overrides any of its values.</summary>
    public bool HasOverrides => Overrides.Count > 0;

    /// <summary>The parameters an override can be set for (the kind's, in order).</summary>
    public IReadOnlyList<string> OverrideChoices { get; }

    /// <summary>The parameter an override is being typed for (the view's picker), or null.</summary>
    public string? OverrideParam { get; set; }

    /// <summary>The override value being typed.</summary>
    public string OverrideText { get; set; } = "";

    /// <summary>The reason a NEW override entry is written with (required by the schema).</summary>
    public string OverrideReason { get; set; } = "";

    /// <summary>The match type a new binding rule is typed with.</summary>
    public string NewRuleMatchType { get; set; } = "";

    // The grid rows: the kind spec's parameters in order (every value from the kind spec), then written members the kind
    // does not declare (shown, not editable: the loader refuses them, reset removes them).
    private static IReadOnlyList<FieldRow> Rows(FieldGridModel grid, TemplateInfo info)
    {
        var rows = new List<FieldRow>();
        KindSpec? spec = TemplateKinds.Specs.Find(info.Kind);
        string? locked = info.IsLocal ? info.IsPlaceholder ? "a placeholder template holds no values (every one is null by the placeholder rule)." : null : LibraryReadOnlyReason;
        foreach (KindParamSpec p in spec?.Params.Values ?? Enumerable.Empty<KindParamSpec>())
        {
            TemplateParamInfo pi = TemplateKinds.Info(p);
            TemplateParamLiteral? v = info.Param(p.Name);
            string literal = v?.Literal ?? "";
            FieldState state = locked is not null ? FieldState.Locked : v is null ? FieldState.Absent : FieldState.Set;
            string help = p.Description + (pi.RangeText.Length > 0 ? " Range: " + pi.RangeText + "." : "") + (p.MarkGated ? " (A gated parameter: the kind spec marks its value as awaiting sign-off.)" : "");
            rows.Add(new FieldRow(grid, p.Name, p.Name, p.Unit ?? "", help, OverlayOps.ParamField(p).Kind.ToString(), p.Required, state, literal,
                pi.DefaultText, null, locked, p.Values, v is { ObjectForm: true } ? "object form: its unit and provenance are kept when the value is set" : null));
        }
        foreach (TemplateParamLiteral extra in info.Params.Where(x => spec is null || !spec.Params.ContainsKey(x.Name)))
            rows.Add(new FieldRow(grid, extra.Name, extra.Name, "", "Not a parameter of kind '" + info.Kind + "' (the loader refuses it; RESET removes it).", "Unknown", false,
                FieldState.Unknown, extra.Literal, "none", null, null, Array.Empty<string>()));
        return rows;
    }
}

/// <summary>
/// The template link on a generator's editor (Phase 4.4): the template the generator is bound to and how, the overrides
/// of that template, and (on request) the effective parameters as the v2 loader resolves them.
/// </summary>
public sealed class GeneratorTemplateModel
{
    internal GeneratorTemplateModel(CaseTemplates templates, GeneratorBindingInfo generator)
    {
        ArgumentNullException.ThrowIfNull(templates);
        Generator = generator ?? throw new ArgumentNullException(nameof(generator));
        Template = generator.Template is null ? null : templates.Find(generator.Template);
        Overrides = generator.Template is null ? Array.Empty<TemplateOverrideRow>() : TemplatePages.OverridesOf(templates, generator.Template);
        Choices = TemplatePages.BindableTemplates(templates);
    }

    /// <summary>The generator and its binding.</summary>
    public GeneratorBindingInfo Generator { get; }

    /// <summary>The template it is bound to, or null (unbound, or a reference that resolves to nothing: Check says why).</summary>
    public TemplateInfo? Template { get; }

    /// <summary>True when it is bound to a template the case can see.</summary>
    public bool HasTemplate => Template is not null;

    /// <summary>The binding in words ("gridsim.local.wind-gfl@1, by binding rule #0 type 'onshore'").</summary>
    public string BindingText => Generator.Template is null ? "Not bound to a template: " + Generator.Route + "."
        : Generator.Template + (Template is null ? " (no such template in the case: Check reports it)" : " (" + Template.Kind + ")") + ", by " + Generator.Route + ".";

    /// <summary>The overrides of the bound template, each against the template's value.</summary>
    public IReadOnlyList<TemplateOverrideRow> Overrides { get; }

    /// <summary>True when the bound template has overrides in this case.</summary>
    public bool HasOverrides => Overrides.Count > 0;

    /// <summary>What an override here means (v2 overrides are per template, never per element).</summary>
    public string OverridesNote => Generator.Template is null ? ""
        : "An override is written per TEMPLATE (gridsim-case/2 overrides name a template, not an element), so it applies to every generator bound to "
          + Generator.Template + ". To give this generator values of its own, Duplicate the template and bind this generator to the copy.";

    /// <summary>The templates the generator can be bound to (active inverter templates), for the picker.</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>True when the generator names its own template (Unbind removes it).</summary>
    public bool HasOwnTemplate => Generator.InlineTemplate is not null;

    /// <summary>The effective parameters as the loader resolves them (after "show effective parameters"), or null.</summary>
    public EffectiveBinding? Effective { get; internal set; }

    /// <summary>The loader's refusal when the effective parameters were asked for and the case does not load, or null.</summary>
    public string? EffectiveError { get; internal set; }

    /// <summary>The effective parameters' heading, once asked for.</summary>
    public string EffectiveText => EffectiveError is string e ? "The v2 loader refuses the case as it is now, so there are no effective parameters: " + e
        : Effective is null ? "Not loaded yet: SHOW EFFECTIVE PARAMETERS loads the case as it is now with the v2 loader (nothing is written)."
        : !Effective.IsBound ? "The v2 loader binds this generator to no asset class: it stays passive."
        : "As the v2 loader resolves them: asset class '" + Effective.ClassName + "' (" + Effective.ClassKind + "), "
          + Effective.Params.Count.ToString(CultureInfo.InvariantCulture) + " parameter(s) set; every other parameter takes its consumer's code default.";
}

/// <summary>The TEMPLATES class node's OVERVIEW: every template, binding rule, override and generator binding.</summary>
/// <param name="Note">What the node holds and how to act on it.</param>
/// <param name="Templates">The template nodes (local first, then library), each row selects its node.</param>
/// <param name="Rules">The binding rules.</param>
/// <param name="Overrides">Every override row.</param>
/// <param name="Generators">Every generator and the template it is bound to.</param>
/// <param name="Notes">What could not be read (a missing library file).</param>
public sealed record TemplatesPage(string Note, IReadOnlyList<AuthorNode> Templates, IReadOnlyList<TemplateBindingRule> Rules,
    IReadOnlyList<TemplateOverrideRow> Overrides, IReadOnlyList<GeneratorBindingInfo> Generators, IReadOnlyList<string> Notes)
{
    /// <summary>True when there are binding rules.</summary>
    public bool HasRules => Rules.Count > 0;

    /// <summary>True when there are overrides.</summary>
    public bool HasOverrides => Overrides.Count > 0;

    /// <summary>True when something could not be read.</summary>
    public bool HasNotes => Notes.Count > 0;
}

/// <summary>One override node's page: the override against the template's value, and its reason.</summary>
public sealed record TemplateOverridePage(TemplateOverrideRow Row, string TemplateSource)
{
    /// <summary>The heading ("gridsim.local.wind-gfl@1 deadbandHz").</summary>
    public string Heading => Row.Template + "  " + Row.Param;
}
