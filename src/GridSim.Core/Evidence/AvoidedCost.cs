using GridSim.Core.Diagnostics;

namespace GridSim.Core.Evidence;

/// <summary>
/// Inputs to an avoided-cost calculation. The Value of Lost Load (VoLL) and the outage duration are POLICY
/// numbers, not physics - both default to <see cref="ProvenanceTag.CuratedPlaceholder"/> and must be replaced
/// with a cited figure ([VERIFY]) before the result is quoted. GB VoLL estimates span roughly £6k-£17k/MWh.
/// <para>D-DOC-033 (October 2026): each input carries its OWN provenance, and the result takes the weaker of the two
/// (<see cref="AvoidedCost.Weakest"/>). <see cref="VollSourceId"/> names the reference the VoLL figure was checked
/// against (<see cref="VollSources"/>), null when none was given.</para>
/// </summary>
public sealed record AvoidedCostInputs(
    double VollPerMwh = 6000.0,
    double LfddDurationHours = 0.5,
    ProvenanceTag VollProvenance = ProvenanceTag.CuratedPlaceholder,
    ProvenanceTag DurationProvenance = ProvenanceTag.CuratedPlaceholder,
    string? VollSourceId = null);

/// <summary>The avoided cost of an averted LFDD event, with its full arithmetic exposed for checking.</summary>
/// <remarks>D-DOC-033: <see cref="Provenance"/> is the weaker of <see cref="VollProvenance"/> and
/// <see cref="DurationProvenance"/>, so citing a VoLL source never promotes a cost whose duration is still a
/// placeholder.</remarks>
public sealed record AvoidedCostResult(
    double LfddMwShedAvoided,
    double VollPerMwh,
    double DurationHours,
    double AvoidedCostGbp,
    ProvenanceTag Provenance,
    string Basis,
    ProvenanceTag VollProvenance = ProvenanceTag.CuratedPlaceholder,
    ProvenanceTag DurationProvenance = ProvenanceTag.CuratedPlaceholder,
    string? VollSourceId = null);

/// <summary>
/// Avoided cost from a <b>replayed</b> event: the recorded demand actually disconnected by LFDD, valued at VoLL
/// over the outage duration. This is grounded in the record (MW shed comes from <c>RecordedOutcome.LfddMwShed</c>),
/// <b>not</b> a marketing figure. The result is only meaningful when the counterfactual device actually keeps the
/// nadir above the LFDD threshold - otherwise the shed would still have happened and nothing is avoided.
/// </summary>
public static class AvoidedCost
{
    /// <summary>
    /// N359 (CF6, technical ch. 8 "trust only moves down"): the tag a VoLL figure starts with. VoLL is a policy number,
    /// not a measurement of the event, so it starts (and, under the downgrade-only rule, stays) at
    /// <see cref="ProvenanceTag.CuratedPlaceholder"/>. Citing a reference (<see cref="AvoidedCostInputs.VollSourceId"/>,
    /// the CLI's <c>--voll-source</c>) records WHERE the figure is stated and is checked against
    /// <see cref="VollSources"/>, but it does not re-source the figure from a primary record of the event, so it cannot
    /// raise the tag. Before N359 the CLI tagged a cited VoLL Recorded on the word of the flag.
    /// </summary>
    public const ProvenanceTag VollBaseProvenance = ProvenanceTag.CuratedPlaceholder;

    /// <summary>The avoided cost of <paramref name="lfddMwShedAvoided"/> MW of LFDD averted, valued at VoLL over the
    /// outage duration. N359 (CF6): a VoLL tag above <see cref="VollBaseProvenance"/> is refused
    /// (<see cref="InvalidDataException"/>, via <see cref="ProvenanceTrust.RequireNotRaised"/>), with or without a cited
    /// source, and an output tag (Counterfactual, UnvalidatedAtScale) on either input is refused as an input. Every
    /// input the CLI has ever built without <c>--voll-source</c> (both tags CuratedPlaceholder) passes and gives the same
    /// numbers and tags as before.</summary>
    public static AvoidedCostResult FromLfddAvoided(double lfddMwShedAvoided, AvoidedCostInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        // N359 (CF6): downgrade-only. The VoLL input may not claim more trust than a policy figure starts with; the
        // duration's tag is checked as an input (no output tag) and keeps whatever the caller states (it has no flag).
        ProvenanceTrust.RequireNotRaised(new[] { VollBaseProvenance }, inputs.VollProvenance,
            inputs.VollSourceId is null
                ? "the VoLL input"
                : $"the VoLL input cited to {inputs.VollSourceId} (a citation records where the figure is stated; it does not re-source it)");
        ProvenanceTrust.RequireInput(inputs.DurationProvenance, "the LFDD duration input");
        // The policy numbers and the shed must be usable: a negative duration gave a negative "avoided cost", and a NaN
        // (Math.Max(0, NaN) is NaN) a NaN one that only failed later, at serialisation.
        if (!(double.IsFinite(inputs.VollPerMwh) && inputs.VollPerMwh > 0))
            throw new ArgumentOutOfRangeException(nameof(inputs), inputs.VollPerMwh, "VollPerMwh must be a finite number above 0.");
        if (!(double.IsFinite(inputs.LfddDurationHours) && inputs.LfddDurationHours > 0))
            throw new ArgumentOutOfRangeException(nameof(inputs), inputs.LfddDurationHours, "LfddDurationHours must be a finite number above 0.");
        if (!double.IsFinite(lfddMwShedAvoided))
            throw new ArgumentOutOfRangeException(nameof(lfddMwShedAvoided), lfddMwShedAvoided, "The LFDD shed avoided must be a finite number of MW.");
        double cost = Math.Max(0, lfddMwShedAvoided) * inputs.VollPerMwh * inputs.LfddDurationHours;
        return new AvoidedCostResult(
            lfddMwShedAvoided, inputs.VollPerMwh, inputs.LfddDurationHours, cost,
            Weakest(inputs.VollProvenance, inputs.DurationProvenance),
            // D-DOC-135 (DOC-17): "GBP/MWh" rather than the pound sign; this text is printed on the console.
            "recorded LFDD energy avoided (MW shed x duration h) x VoLL (GBP/MWh) - grounded in the record, not a marketing figure",
            inputs.VollProvenance, inputs.DurationProvenance, inputs.VollSourceId);
    }

    /// <summary>
    /// D-DOC-033: the weaker of two provenance tags, for a figure computed from both. Recorded is the strongest, then
    /// DerivedFromRecorded; any other tag is weaker than both. Two different weak tags (say Synthetic and
    /// CuratedPlaceholder) give CuratedPlaceholder, the tag that always carries [VERIFY].
    /// </summary>
    public static ProvenanceTag Weakest(ProvenanceTag a, ProvenanceTag b)
    {
        if (a == b) return a;
        if (a == ProvenanceTag.Recorded) return b;
        if (b == ProvenanceTag.Recorded) return a;
        if (a == ProvenanceTag.DerivedFromRecorded) return b;
        if (b == ProvenanceTag.DerivedFromRecorded) return a;
        return ProvenanceTag.CuratedPlaceholder;
    }
}

/// <summary>One published GB Value of Lost Load figure: the reference that states it and the figure itself.</summary>
public sealed record VollSource(string Id, string Title, double VollPerMwh, string Where);

/// <summary>
/// D-DOC-033 (October 2026, docs-session finding): the GB VoLL figures held in <c>docs/reference/voll</c>, by reference id
/// (docs/reference/README.md). <c>evidence --voll-source</c> used to accept ANY text and tag the VoLL Recorded on the
/// word of the flag; it now has to name one of these references, and <c>--voll</c> has to equal a figure that
/// reference states. The VoLL input is still not tagged Recorded: <see cref="AvoidedCost.FromLfddAvoided"/> refuses a
/// VoLL tag above CuratedPlaceholder (N359; GR-820 corrected this comment). Read from the PDFs themselves (2026-10-05):
/// <list type="bullet">
/// <item>S324, Ofgem decision on BSC modification P305 (2 April 2015): the administrative VoLL used in cash-out,
/// GBP 3,000/MWh from 5 November 2015 and GBP 6,000/MWh from 1 November 2018 (summary, "VoLL pricing" bullet). GridSim's
/// default 6,000 is this figure.</item>
/// <item>S322, London Economics for Ofgem / DECC, "The Value of Lost Load (VoLL) for Electricity in Great Britain"
/// (July 2013): headline weighted-average VoLL GBP 16,940/MWh for domestic and SME customers on peak winter workdays.</item>
/// <item>S323, DECC EMR consultation annex C, reliability standard methodology (July 2013): uses the London Economics
/// VoLL of GBP 16,940/MWh for the reliability standard (its VoLL / CONE table; 10,290 and 35,490 are that table's other
/// rows, not the chosen VoLL).</item>
/// </list>
/// </summary>
public static class VollSources
{
    /// <summary>Every accepted (reference, figure) pair. A reference may state more than one figure (S324: two dates).</summary>
    public static IReadOnlyList<VollSource> All { get; } =
    [
        new("S324", "Ofgem BSC P305 decision (2015-04-02)", 3000.0, "administrative VoLL from 5 November 2015"),
        new("S324", "Ofgem BSC P305 decision (2015-04-02)", 6000.0, "administrative VoLL from 1 November 2018"),
        new("S322", "London Economics, The Value of Lost Load for Electricity in GB (2013-07)", 16940.0,
            "headline weighted-average VoLL, domestic and SME, peak winter workday"),
        new("S323", "DECC EMR consultation annex C, reliability standard methodology (2013-07)", 16940.0,
            "VoLL used for the reliability standard (London Economics figure)"),
    ];

    /// <summary>The reference ids that state a VoLL figure, in a stable order (for messages).</summary>
    public static IReadOnlyList<string> Ids { get; } = All.Select(s => s.Id).Distinct().ToList();

    /// <summary>
    /// The entry matching <paramref name="id"/> (case-insensitive) and the exact figure <paramref name="vollPerMwh"/>, or
    /// null. Exact equality is intended: the figures are whole pounds and the flag is parsed from decimal text, so a
    /// cited figure arrives as exactly the same double.
    /// </summary>
    public static VollSource? Find(string id, double vollPerMwh) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase) && s.VollPerMwh == vollPerMwh);

    /// <summary>The figures a reference states (empty when the id is not one of <see cref="Ids"/>).</summary>
    public static IReadOnlyList<double> FiguresOf(string id) =>
        All.Where(s => string.Equals(s.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)).Select(s => s.VollPerMwh).ToList();
}
