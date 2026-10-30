namespace GridSim.Core.Presentation;

/// <summary>
/// The correlated-trip interconnector corridors for the NESO RP1 view (infeeds that can be lost together, Policy
/// 0021 s.4), and the name matching that maps any source's link name onto a corridor member. Shared by the web RP1
/// projection and the WPF <c>NesoRp1Vm</c> so both flag the same links.
/// <para>
/// Why the matching exists (R053): the corridor members are short ids ("IFA", "ElecLink", "NSL", "Viking") and
/// were compared with exact ordinal equality. That only matched the bridge's raw block. The models name the same
/// links differently: gb-full has "IFA Interconnector interconnector", "ElecLink interconnector", "NS Link
/// interconnector" and "Viking Link Denmark Interconnector interconnector"; the reduced GB cases have "North Sea
/// Link" and "Viking Link". So on gb-full no link was ever flagged correlated and the correlated group always read
/// 0 MW, and the North Sea corridor never fired on any model.
/// </para>
/// </summary>
public static class InterconnectorCorridors
{
    /// <summary>Corridor member id: IFA (Sellindge).</summary>
    public const string Ifa = "IFA";
    /// <summary>Corridor member id: ElecLink (Sellindge, via the Channel Tunnel).</summary>
    public const string ElecLink = "ElecLink";
    /// <summary>Corridor member id: North Sea Link (Blyth).</summary>
    public const string Nsl = "NSL";
    /// <summary>Corridor member id: Viking Link (Bicker Fen).</summary>
    public const string Viking = "Viking";

    /// <summary>The corridors: a display name and the member ids whose live imports sum into one credible group.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Members)> All { get; } = new (string, IReadOnlyList<string>)[]
    {
        ("Sellindge (IFA+ElecLink)", new[] { Ifa, ElecLink }),
        ("North Sea (NSL+Viking)", new[] { Nsl, Viking }),
    };

    /// <summary>
    /// Map a link name from any source (model generator name, bridge raw block, lake interconnector list) onto a
    /// corridor member id, or <c>null</c> when the link is not a corridor member.
    /// <para>
    /// Normalisation: lower-case, keep only letters and digits, then delete every "interconnector" (the gb-full
    /// export repeats it). The result is then matched EXACTLY against a short list of spellings, so "IFA2" stays
    /// distinct from "IFA" and no unrelated link can match by accident:
    /// IFA = "ifa"; ElecLink = "eleclink"; NSL = "nsl", "nslink", "northsealink"; Viking = "viking", "vikinglink",
    /// "vikinglinkdenmark".
    /// </para>
    /// </summary>
    public static string? CanonicalMember(string? linkName)
    {
        if (string.IsNullOrWhiteSpace(linkName)) return null;

        // Letters and digits only, lower-case (invariant: names are ASCII identifiers in every source).
        var sb = new System.Text.StringBuilder(linkName.Length);
        foreach (char c in linkName)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        string key = sb.ToString().Replace("interconnector", "", StringComparison.Ordinal);

        return key switch
        {
            "ifa" => Ifa,
            "eleclink" => ElecLink,
            "nsl" or "nslink" or "northsealink" => Nsl,
            "viking" or "vikinglink" or "vikinglinkdenmark" => Viking,
            _ => null,
        };
    }

    /// <summary>The display name of the corridor <paramref name="linkName"/> belongs to, or <c>null</c>.</summary>
    public static string? CorridorOf(string? linkName)
    {
        string? member = CanonicalMember(linkName);
        if (member is null) return null;
        foreach ((string name, IReadOnlyList<string> members) in All)
            if (members.Contains(member)) return name;
        return null;
    }

    /// <summary>
    /// Fold named live flows (import positive) into a member-id keyed dictionary for
    /// <c>StabilityMetrics.LargestCorrelatedGroup</c>. Links that are not corridor members are ignored. If two
    /// source rows map to the same member (one link split across two model units) their flows are summed, so the
    /// link's net import is what counts. Order of the input does not change the result beyond floating-point
    /// summation order, which follows the input order (deterministic for a given frame).
    /// </summary>
    public static Dictionary<string, double> MemberFlows(IEnumerable<(string Name, double Mw)> flows)
    {
        ArgumentNullException.ThrowIfNull(flows);
        var byMember = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string name, double mw) in flows)
        {
            if (CanonicalMember(name) is not { } member) continue;
            byMember[member] = byMember.TryGetValue(member, out double sum) ? sum + mw : mw;
        }
        return byMember;
    }
}
