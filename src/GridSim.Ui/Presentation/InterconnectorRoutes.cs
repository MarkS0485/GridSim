namespace GridSim.Ui.Presentation;

/// <summary>
/// Display catalogue for the GB interconnector fleet  -  what each link joins and where it
/// lands. Pure presentation metadata: the electrical model carries the links as
/// <c>type: "interconnector"</c> generators; this just labels the dials.
/// <para>Moved here from the WPF <c>Visuals/Interconnectors.cs</c> (UI programme Phase 0.2, renamed from
/// <c>Interconnectors</c> so it cannot be confused with the market tabs' interconnector lists): the dashboard's
/// IMPORT LINKS rows and the WPF asset pages read the same table. The table, the key rule and the lookup are
/// unchanged.</para>
/// </summary>
public static class InterconnectorRoutes
{
    // Keyed by the NORMALISED link name (see Normalise). The models spell the same link differently: the reduced GB
    // cases say "North Sea Link", gb-full says "NS Link interconnector" or "IFA Interconnector interconnector". An exact
    // lookup on the short name missed every gb-full spelling, so IMPORT LINKS and the interconnector pages showed no
    // route on gb-full. Each link lists the spellings seen in the shipped models and the bridge, matched exactly after
    // normalising, so "IFA2" stays distinct from "IFA" and an unrelated name cannot match by accident (the same rule
    // as Core's InterconnectorCorridors.CanonicalMember).
    private static readonly Dictionary<string, string> Route = Build(
        // gb-full names the Moyle link by its GB landing: "Auchencrosh (interconnector CCT) interconnector".
        ("N. Ireland  Auchencrosh", new[] { "moyle", "auchencrosh", "auchencroshcct" }),
        ("Isle of Man  Bispham", new[] { "isleofman" }),
        ("Norway  Blyth", new[] { "northsealink", "nslink", "nsl" }),
        ("Ireland  Deeside", new[] { "ewic", "eastwest" }),
        ("Denmark  Bicker Fen", new[] { "vikinglink", "vikinglinkdenmark", "viking" }),
        ("France  Sellindge", new[] { "ifa" }),
        ("France  Folkestone", new[] { "eleclink" }),
        ("Belgium  Richborough", new[] { "nemolink", "nemo" }),
        ("Netherlands  Grain", new[] { "britned" }),
        ("France  Chilling", new[] { "ifa2" }),
        ("Ireland  Pembroke", new[] { "greenlink" }));

    private static Dictionary<string, string> Build(params (string Route, string[] Keys)[] links)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string route, string[] keys) in links)
            foreach (string k in keys) map.Add(k, route);   // Add, not indexer: a key listed twice is a defect here
        return map;
    }

    /// <summary>
    /// The lookup key of a link name: lower case (invariant), letters and digits only, with every "interconnector"
    /// removed (the gb-full export repeats it).
    /// </summary>
    internal static string Normalise(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Replace("interconnector", "", StringComparison.Ordinal);
    }

    /// <summary>"Country  landing substation" for a known link; empty for anything else.</summary>
    public static string RouteOf(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Route.TryGetValue(Normalise(name), out string? r) ? r : "";
}
