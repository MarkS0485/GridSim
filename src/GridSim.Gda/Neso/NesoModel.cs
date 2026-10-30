namespace GridSim.Gda.Neso;

// NESO transmission-overlay records: the real GB transmission-connected fleet
// (TEC register), interconnectors, embedded plant, and per-boundary transfer
// limits. These site generation onto the network by Connection Site (substation)
// and give the ETYS boundaries their real MW limits.

/// <summary>A transmission-connected project from the TEC register (the GB generation fleet).</summary>
public sealed record TecEntry(
    string ProjectName, string Customer, string ConnectionSite, string Stage,
    double MwConnected, double CumulativeMw, string Status,
    string HostTo, string PlantType, string ProjectId)
{
    /// <summary>
    /// Best available capacity figure. Prefer <see cref="MwConnected"/> (what is physically
    /// connected today) over <see cref="CumulativeMw"/>, which folds in future-dated increases
    /// ("MW Effective From" beyond now) and so overstates the connected fleet. Falls back to the
    /// cumulative planned figure only for projects with nothing connected yet (future/scoping).
    /// </summary>
    public double CapacityMw => MwConnected > 0 ? MwConnected : CumulativeMw;

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
    double MwConnected, double CumulativeMw, string Status, string HostTo, string PlantType)
{
    /// <summary>
    /// Best available capacity figure, with the same rule as <see cref="TecEntry.CapacityMw"/>
    /// (R016, October 2026 review). Prefer <see cref="MwConnected"/> (what is physically
    /// connected today) over <see cref="CumulativeMw"/>, which folds in future-dated increases
    /// and so overstates the connected fleet. Falls back to the cumulative planned figure only
    /// for projects with nothing connected yet. This used to prefer the cumulative figure.
    /// </summary>
    public double CapacityMw => MwConnected > 0 ? MwConnected : CumulativeMw;

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
