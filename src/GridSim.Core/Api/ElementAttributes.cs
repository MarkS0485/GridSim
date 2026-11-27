using GridSim.Core.Model;

namespace GridSim.Core.Api;

/// <summary>One attribute of an element kind: its name, whether it may be written, and its meaning (unit).</summary>
public sealed record ElementField(string Name, bool ReadOnly, string Meaning);

/// <summary>RC5 WP15: one attribute <see cref="CaseEditor.Add"/> takes: its name, whether it must be given, and the value an
/// omitted one takes (null when the element then carries none).</summary>
public sealed record AddField(string Name, bool Required, double? Default);

/// <summary>
/// The attribute tables of the object model (RC5 WP11). Values are SI and per unit as each name says. A read gives a
/// <see cref="double"/> (an unlimited limit is an infinity), a <see cref="string"/> (bus <c>name</c>) or null (a value the
/// element does not carry, such as a bus with no own voltage limits). A write takes a finite number.
/// <list type="bullet">
/// <item>bus: <c>type</c> (1 PQ, 2 PV, 3 slack: the PSS/E and MATPOWER codes), <c>vm</c> (pu, the initial or held
/// magnitude), <c>va_deg</c>, <c>base_kv</c> (read only), <c>name</c> (read only), <c>vmin</c>, <c>vmax</c> (pu, the bus's
/// own limits).</item>
/// <item>generator: <c>pg</c>, <c>qg</c> (MW, MVAr), <c>vg</c> (pu), <c>qmin</c>, <c>qmax</c>, <c>pmin</c>, <c>pmax</c>,
/// <c>mbase</c> (MVA), <c>status</c> (1 in service, 0 out).</item>
/// <item>load: <c>pd</c>, <c>qd</c> (MW, MVAr: the bus's aggregated demand), <c>status</c>. Status 0 keeps the set-point
/// for re-energising, as the scripting session does: the bus draws nothing, and <c>pd</c> / <c>qd</c> read and write the
/// kept set-point.</item>
/// <item>branch: <c>r</c>, <c>x</c>, <c>b</c> (pu on the system base), <c>tap</c>, <c>shift_deg</c>, <c>rate_a</c>,
/// <c>rate_b</c>, <c>rate_c</c> (MVA), <c>status</c>.</item>
/// <item>shunt: <c>gs</c>, <c>bs</c> (MW, MVAr drawn at 1 pu).</item>
/// </list>
/// </summary>
public static class ElementAttributes
{
    private static readonly IReadOnlyDictionary<ElementKind, IReadOnlyList<ElementField>> Tables = new Dictionary<ElementKind, IReadOnlyList<ElementField>>
    {
        [ElementKind.Bus] =
        [
            new("type", false, "1 PQ, 2 PV, 3 slack"), new("vm", false, "pu"), new("va_deg", false, "degrees"), new("base_kv", true, "kV"),
            new("name", true, "text"), new("vmin", false, "pu"), new("vmax", false, "pu"),
        ],
        [ElementKind.Generator] =
        [
            new("pg", false, "MW"), new("qg", false, "MVAr"), new("vg", false, "pu"), new("qmin", false, "MVAr"), new("qmax", false, "MVAr"),
            new("pmin", false, "MW"), new("pmax", false, "MW"), new("mbase", false, "MVA"), new("status", false, "1 in service, 0 out"),
        ],
        [ElementKind.Load] = [new("pd", false, "MW"), new("qd", false, "MVAr"), new("status", false, "1 in service, 0 out")],
        [ElementKind.Branch] =
        [
            new("r", false, "pu"), new("x", false, "pu"), new("b", false, "pu"), new("tap", false, "ratio"), new("shift_deg", false, "degrees"),
            new("rate_a", false, "MVA"), new("rate_b", false, "MVA"), new("rate_c", false, "MVA"), new("status", false, "1 in service, 0 out"),
        ],
        [ElementKind.Shunt] = [new("gs", false, "MW at 1 pu"), new("bs", false, "MVAr at 1 pu")],
    };

    /// <summary>The attributes of a kind, in their fixed order.</summary>
    public static IReadOnlyList<ElementField> Of(ElementKind kind) => Tables[kind];

    /// <summary>The attribute names of a kind, comma separated, for a refusal.</summary>
    public static string List(ElementKind kind) => string.Join(", ", Of(kind).Select(a => a.Name));

    /// <summary>The attribute of that name, or null.</summary>
    public static ElementField? Find(ElementKind kind, string name) => Of(kind).FirstOrDefault(a => a.Name == name);

    /// <summary>RC5 WP15: the attributes <see cref="CaseEditor.Add"/> takes for a new element of each kind, with whether each
    /// is required and the value an omitted optional one takes (null: the element carries none, as a bus with no own
    /// voltage limits). The names are the read/write names of <see cref="Of"/>.
    /// <list type="bullet">
    /// <item>bus: <c>base_kv</c> (required, above 0), <c>type</c> (1), <c>vm</c> (1), <c>va_deg</c> (0), <c>vmin</c>, <c>vmax</c>.</item>
    /// <item>generator: <c>pg</c> (0), <c>qg</c> (0), <c>vg</c> (1), <c>qmin</c> (no limit), <c>qmax</c> (no limit), <c>pmin</c>
    /// (0), <c>pmax</c> (no limit), <c>mbase</c> (100), <c>status</c> (1).</item>
    /// <item>load: <c>pd</c> (0), <c>qd</c> (0), <c>status</c> (1); pd or qd must not be zero.</item>
    /// <item>branch: <c>x</c> (required), <c>r</c> (0), <c>b</c> (0), <c>tap</c> (1), <c>shift_deg</c> (0), <c>rate_a</c>,
    /// <c>rate_b</c>, <c>rate_c</c> (0, not given), <c>status</c> (1). Giving <c>tap</c> or <c>shift_deg</c> makes it a
    /// transformer.</item>
    /// <item>shunt: <c>gs</c> (0), <c>bs</c> (0); gs or bs must not be zero.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<AddField> AddFieldsOf(ElementKind kind) => AddTables[kind];

    /// <summary>The add attribute names of a kind, comma separated (required ones marked), for a refusal.</summary>
    public static string AddList(ElementKind kind) =>
        string.Join(", ", AddFieldsOf(kind).Select(a => a.Required ? a.Name + " (required)" : a.Name));

    private static readonly IReadOnlyDictionary<ElementKind, IReadOnlyList<AddField>> AddTables = new Dictionary<ElementKind, IReadOnlyList<AddField>>
    {
        [ElementKind.Bus] =
        [
            new("base_kv", true, null), new("type", false, 1), new("vm", false, 1.0), new("va_deg", false, 0.0), new("vmin", false, null),
            new("vmax", false, null),
        ],
        [ElementKind.Generator] =
        [
            new("pg", false, 0.0), new("qg", false, 0.0), new("vg", false, 1.0), new("qmin", false, double.NegativeInfinity),
            new("qmax", false, double.PositiveInfinity), new("pmin", false, 0.0), new("pmax", false, double.PositiveInfinity),
            new("mbase", false, 100.0), new("status", false, 1),
        ],
        [ElementKind.Load] = [new("pd", false, 0.0), new("qd", false, 0.0), new("status", false, 1)],
        [ElementKind.Branch] =
        [
            new("x", true, null), new("r", false, 0.0), new("b", false, 0.0), new("tap", false, 1.0), new("shift_deg", false, 0.0),
            new("rate_a", false, 0.0), new("rate_b", false, 0.0), new("rate_c", false, 0.0), new("status", false, 1),
        ],
        [ElementKind.Shunt] = [new("gs", false, 0.0), new("bs", false, 0.0)],
    };

    internal static int BusTypeCode(BusType t) => t switch { BusType.Pq => 1, BusType.Pv => 2, _ => 3 };

    internal static BusType BusTypeOf(double code) => code switch
    {
        1 => BusType.Pq,
        2 => BusType.Pv,
        3 => BusType.Slack,
        _ => throw new ArgumentException("type is 1 (PQ), 2 (PV) or 3 (slack)"),
    };

    internal static bool StatusOf(double v) => v switch
    {
        1 => true,
        0 => false,
        _ => throw new ArgumentException("status is 1 (in service) or 0 (out of service)"),
    };
}
