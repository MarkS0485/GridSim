using System.Globalization;
using System.Text.Json;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Schema;

namespace GridSim.Ui.Assets.Editing.Templates;

// UI programme Phase 4.4 (2026-10-21): inverter templates in AUTHOR mode. This file is the template KIND SPECS as the
// editor reads them: the very kind files the v2 loader checks a template against (schemas/v2/kinds/*.kind.json,
// gridsim-kind/2), taken from the copies embedded in GridSim.Core (the ones the save gate writes out for its loader
// stage, so the editor, Check and the loader can never read two different contracts). Nothing about a parameter is
// written here: its type, unit, range, requiredness, enum values, help text and the code defaults all come from the
// kind spec through KindSpecRegistry (the loader's own parser of those files). What is added is presentation only:
// the "default" text of a parameter the template leaves out (from the kind spec's codeDefaults, which record what the
// C# consumers do today, disagreements included) and the range text.

/// <summary>
/// One parameter of a template kind as the editor shows it (Phase 4.4): every value comes from the kind spec
/// (<c>schemas/v2/kinds/&lt;kind&gt;.kind.json</c>) exactly as <see cref="KindSpecRegistry"/> parsed it.
/// </summary>
/// <param name="Name">The parameter name (the key written in the template's params).</param>
/// <param name="Type">"number", "integer", "enum", "string" or "boolean".</param>
/// <param name="Unit">The unit (number and integer only), or "".</param>
/// <param name="Min">The inclusive minimum, or null when the kind declares none.</param>
/// <param name="Max">The inclusive maximum, or null when the kind declares none.</param>
/// <param name="Required">True when the kind requires the parameter.</param>
/// <param name="MarkGated">True when the kind marks the parameter as gated (its value needs Mark's sign-off).</param>
/// <param name="Values">An enum's allowed values (empty for every other type).</param>
/// <param name="Help">The kind spec's description of the parameter.</param>
/// <param name="RangeText">The range in words ("0 to 5 Hz", "at least 0 s", "any"), "" for a type with no range.</param>
/// <param name="DefaultText">What a template that leaves the parameter out gets: the code default(s) the kind spec
/// records, per consumer when they disagree, or that it records none.</param>
public sealed record TemplateParamInfo(string Name, string Type, string Unit, double? Min, double? Max, bool Required, bool MarkGated,
    IReadOnlyList<string> Values, string Help, string RangeText, string DefaultText)
{
    /// <summary>"required" or "optional".</summary>
    public string RequiredText => Required ? "required" : "optional";
}

/// <summary>
/// The template kind specs the editor reads (Phase 4.4): the embedded <c>schemas/v2/kinds</c>, loaded once through
/// <see cref="KindSpecRegistry"/> (fail loud), and which of them are INVERTER template kinds (the kinds a generator can
/// be bound to so that it gets an inverter controller).
/// </summary>
public static class TemplateKinds
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The same embeds the save gate writes for the v2 loader (BundledSchemas.WriteV2SchemaFolder), validated against the
    // embedded kind.schema.json: one registry for the life of the process (it is immutable).
    private static readonly Lazy<KindSpecRegistry> Registry = new(() =>
        KindSpecRegistry.FromSources(BundledSchemas.Sources(BundledSchemas.V2KindsPrefix), BundledSchemas.V2()));

    /// <summary>The template kind specs (internal: the editor's operations check parameters against them).</summary>
    internal static KindSpecRegistry Specs => Registry.Value;

    /// <summary>
    /// The inverter template kinds, sorted: the template kinds the v2 loader turns into an asset class of an ACTIVE
    /// (inverter) controller when a generator is bound to one. They are read from the loader's own table of the kinds an
    /// asset class can be today (<c>V2CaseLoader.V1KindOf</c>), keeping those whose asset-class kind is an inverter (the
    /// grid-following and grid-forming inverter); a kind the library ships later that the loader maps to an inverter
    /// joins this list without a change here.
    /// </summary>
    public static IReadOnlyList<string> InverterKinds { get; } = V2CaseLoader.V1KindOf
        .Where(kv => kv.Value is "gridFollowingInverter" or "gridFormingInverter" && Registry.Value.Contains(kv.Key))
        .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>True when <paramref name="kind"/> is an inverter template kind (see <see cref="InverterKinds"/>).</summary>
    public static bool IsInverterKind(string? kind) => kind is not null && InverterKinds.Contains(kind, StringComparer.Ordinal);

    /// <summary>True when the kind spec of <paramref name="kind"/> is loaded.</summary>
    public static bool IsKnown(string? kind) => kind is not null && Registry.Value.Contains(kind);

    /// <summary>The kind's title from its kind spec ("Grid-forming inverter"), or the kind itself when it is unknown.</summary>
    public static string TitleOf(string kind) => Registry.Value.Find(kind)?.Title ?? kind;

    /// <summary>The kind's description from its kind spec, or "" when it is unknown.</summary>
    public static string DescriptionOf(string kind) => Registry.Value.Find(kind)?.Description ?? "";

    /// <summary>The kind spec file a kind is declared in ("gridFormingInverter.kind.json"), or "" when it is unknown.</summary>
    public static string FileOf(string kind) => Registry.Value.Find(kind)?.SourceFile ?? "";

    /// <summary>The parameters of <paramref name="kind"/>, in the kind file's order; empty for an unknown kind.</summary>
    public static IReadOnlyList<TemplateParamInfo> ParamsOf(string kind)
        => Registry.Value.Find(kind) is KindSpec spec ? spec.Params.Values.Select(Info).ToList() : Array.Empty<TemplateParamInfo>();

    /// <summary>One kind parameter in public form (every value from the kind spec).</summary>
    internal static TemplateParamInfo Info(KindParamSpec p)
    {
        ArgumentNullException.ThrowIfNull(p);
        string type = p.Type switch
        {
            KindParamType.Number => "number",
            KindParamType.Integer => "integer",
            KindParamType.Enum => "enum",
            KindParamType.String => "string",
            _ => "boolean",
        };
        return new TemplateParamInfo(p.Name, type, p.Unit ?? "", p.Min, p.Max, p.Required, p.MarkGated, p.Values, p.Description,
            RangeText(p), DefaultText(p));
    }

    // The range in words. Both bounds are inclusive (KindSpecRegistry.CheckParams: "within min and max, both inclusive").
    private static string RangeText(KindParamSpec p)
    {
        if (p.Type is not (KindParamType.Number or KindParamType.Integer)) return "";
        string unit = p.Unit is null or "1" ? "" : " " + p.Unit;
        return (p.Min, p.Max) switch
        {
            (double lo, double hi) => Num(lo) + " to " + Num(hi) + unit,
            (double lo, null) => "at least " + Num(lo) + unit,
            (null, double hi) => "at most " + Num(hi) + unit,
            _ => "any finite value",
        };
    }

    // What a template that leaves the parameter out gets, from the kind spec's codeDefaults (the value each C# consumer
    // uses today). One value shared by every consumer is said once; values that differ are listed per consumer, never
    // merged (the kind spec's own rule).
    private static string DefaultText(KindParamSpec p)
    {
        if (p.CodeDefaults.Count == 0)
            return p.Required ? "none: the kind requires it" : "none recorded: the kind spec names no code default";
        List<string> values = p.CodeDefaults.Select(d => Show(d.Value)).ToList();
        if (values.Distinct(StringComparer.Ordinal).Count() == 1)
            return "code default " + values[0] + " (" + p.CodeDefaults.Count.ToString(Inv) + " consumer" + (p.CodeDefaults.Count == 1 ? "" : "s") + ")";
        return "code defaults differ by consumer: " + string.Join("; ", p.CodeDefaults.Select(d => Show(d.Value) + " in " + d.Consumer));
    }

    private static string Show(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => Num(v.GetDouble()),
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.GetRawText(),
    };

    private static string Num(double d) => d.ToString("R", Inv);
}
