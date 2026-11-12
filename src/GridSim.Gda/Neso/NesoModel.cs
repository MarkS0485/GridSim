namespace GridSim.Gda.Neso;

// NESO transmission-overlay records: the real GB transmission-connected fleet
// (TEC register), interconnectors, embedded plant, and per-boundary transfer
// limits. These site generation onto the network by Connection Site (substation)
// and give the ETYS boundaries their real MW limits.

/// <summary>A transmission-connected project from the TEC register (the GB generation fleet).</summary>
public sealed record TecEntry(
    string ProjectName, string Customer, string ConnectionSite, string Stage,
    double MwConnected, double CumulativeMw, string Status,
    string HostTo, string PlantType, string ProjectId,
    string MwEffectiveFrom = "")   // bug #209: the date this row's increase takes effect (blank = already in effect)
{
    /// <summary>
    /// Best available capacity figure. Prefer <see cref="MwConnected"/> (what is physically
    /// connected today) over <see cref="CumulativeMw"/>, which folds in future-dated increases
    /// ("MW Effective From" beyond now) and so overstates the connected fleet. Falls back to the
    /// cumulative planned figure only for projects with nothing connected yet (future/scoping).
    /// </summary>
    /// <remarks>F-038: this is the WHOLE-REGISTER (planning) figure. The connected fleet must use
    /// <see cref="ConnectedCapacityMw"/>, which never falls back to the cumulative figure.</remarks>
    public double CapacityMw => MwConnected > 0 ? MwConnected : CumulativeMw;

    /// <summary>
    /// F-038: the capacity physically connected today, for the CONNECTED fleet. It is the register's "MW Connected"
    /// column and nothing else (0 when that is 0 or less). In the NESO TEC register "Cumulative Total Capacity (MW)" is
    /// the project's running total after the row's increase takes effect on "MW Effective From", so it is
    /// future-inclusive by definition and is never a connected figure. A row that is Built (or says connected) with
    /// 0 MW connected is a closed station whose agreement is still listed (Dungeness B, Hinkley Point B, Hunterston
    /// on the 28 September 2026 register) or a later stage of a project whose earlier stage holds the connected MW
    /// (West Burton B stage 2, 48 MW effective 2034, cumulative 1,881 MW, beside stage 1 at 1,333 MW connected). Counting
    /// such a row at its cumulative figure adds capacity that does not exist today, or counts a project twice.
    /// </summary>
    public double ConnectedCapacityMw => MwConnected > 0 ? MwConnected : 0.0;

    /// <summary>True for live/connected projects (exclude withdrawn/scoping where desired).</summary>
    public bool IsConnected =>
        NesoStatus.SaysConnected(Status) ||
        NesoStatus.SaysConnected(Stage) ||
        MwConnected > 0;
}

/// <summary>An interconnector from the Interconnector Register (import/export MW capability).</summary>
public sealed record InterconnectorEntry(
    string ProjectName, string ConnectionSite,
    double MwImportTotal, double MwExportTotal, string Status, string HostTo,
    double MwImportCurrent = 0, double MwExportCurrent = 0)
{
    /// <summary>Currently-commissioned capability (the "Current" columns), excluding future-dated increases.</summary>
    public double CurrentCapacityMw => Math.Max(MwImportCurrent, MwExportCurrent);

    /// <summary>Ultimate capability including all future-dated increases (the "Total" columns).</summary>
    public double TotalCapacityMw => Math.Max(MwImportTotal, MwExportTotal);

    /// <summary>
    /// True for a link that is actually in service. The register has no "Connected" status
    /// (values are Built / Scoping / Awaiting Consents / Under Construction), so a Built stage
    /// or any commissioned current capacity is the live signal; Scoping/consenting links are not.
    /// </summary>
    public bool IsConnected =>
        Status.Contains("Built", StringComparison.OrdinalIgnoreCase) ||
        NesoStatus.SaysConnected(Status) ||
        CurrentCapacityMw > 0;
}

/// <summary>An embedded (distribution-connected, transmission-visible) project.</summary>
public sealed record EmbeddedEntry(
    string ProjectName, string ConnectionSite,
    double MwConnected, double CumulativeMw, string Status, string HostTo, string PlantType,
    // Bug #209: the stage identity, so whole-register mode can count a multi-stage project once.
    string Stage = "", string ProjectId = "", string MwEffectiveFrom = "")
{
    /// <summary>
    /// Best available capacity figure, with the same rule as <see cref="TecEntry.CapacityMw"/>
    /// (R016, October 2026 review). Prefer <see cref="MwConnected"/> (what is physically
    /// connected today) over <see cref="CumulativeMw"/>, which folds in future-dated increases
    /// and so overstates the connected fleet. Falls back to the cumulative planned figure only
    /// for projects with nothing connected yet. This used to prefer the cumulative figure.
    /// F-038: this is the WHOLE-REGISTER (planning) figure; the connected fleet uses <see cref="ConnectedCapacityMw"/>.
    /// </summary>
    public double CapacityMw => MwConnected > 0 ? MwConnected : CumulativeMw;

    /// <summary>F-038: the capacity physically connected today ("MW Connected" only, 0 when that is 0 or less), with
    /// the same rule as <see cref="TecEntry.ConnectedCapacityMw"/>. A Built row with 0 MW connected (Camilla Farm, a
    /// 49.95 MW storage project at Glenniston GSP on the 28 September 2026 register) adds nothing to the connected
    /// fleet; it used to enter at its cumulative figure.</summary>
    public double ConnectedCapacityMw => MwConnected > 0 ? MwConnected : 0.0;

    /// <summary>
    /// True for a project that is actually connected: a Built or Connected status, or any
    /// connected MW. Scoping, consenting and under-construction projects with nothing
    /// connected are not. Mirrors <see cref="InterconnectorEntry.IsConnected"/> and
    /// <see cref="TecEntry.IsConnected"/>.
    /// </summary>
    public bool IsConnected =>
        Status.Contains("Built", StringComparison.OrdinalIgnoreCase) ||
        NesoStatus.SaysConnected(Status) ||
        MwConnected > 0;
}

/// <summary>
/// GR-312: the "connected" test on a NESO register status. It used to be a substring match on "Connect", which also
/// matched "Not Connected", "Awaiting Connection", "Pre-Connection" and "Disconnected", so a project with nothing
/// connected was counted in the connected fleet at its future cumulative capacity. A status says connected only when it
/// contains "connect" with none of those negating forms.
/// </summary>
internal static class NesoStatus
{
    private static readonly string[] Negations = { "not connect", "awaiting connect", "pre-connect", "preconnect", "disconnect", "pending connect", "unconnect" };

    public static bool SaysConnected(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        string s = status.Trim().ToLowerInvariant();
        if (!s.Contains("connect", StringComparison.Ordinal)) return false;
        foreach (string n in Negations)
            if (s.Contains(n, StringComparison.Ordinal)) return false;
        return true;
    }
}

/// <summary>
/// The transfer-limit envelope of one ETYS boundary, summarised over the published
/// 24-months-ahead weekly limits. <see cref="MinMw"/> is the most binding (the
/// constraint that actually limits transfer); <see cref="MedianMw"/> a typical value.
/// </summary>
public sealed record BoundaryLimit(string Code, double MinMw, double MedianMw, double MaxMw, int Samples);
