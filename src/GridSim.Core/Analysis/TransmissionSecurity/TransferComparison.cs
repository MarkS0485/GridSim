namespace GridSim.Core.Analysis.TransmissionSecurity;

/// <summary>Frozen inputs of a paid-tool comparison (RC3 "Paid comparator demonstration acceptance").</summary>
public sealed record ComparatorFreeze(string? GridSimRelease, string? CaseSha256, string? StudySha256,
    string? Tool, string? ToolVersion, string? LicenceAndModules, string? IdentityMapping, string? SolverSettings,
    string? ThermalAndVoltageLimits, string? AutomaticControls, string? AllowedActions);

/// <summary>Model parity (one row per compared quantity, with the declared quantity-specific tolerance).</summary>
public sealed record ComparatorParityRow(string Quantity, string Units, double? GridSim, double? Comparator,
    double? Difference, double? Tolerance, string Disposition);

/// <summary>Engineering verdict: headline MW, limiting outage and physical element on each side.</summary>
public sealed record ComparatorVerdictRow(string Item, string? GridSim, string? Comparator, bool? Match, string? Explanation);

/// <summary>Runtime, kept separate from numerical evidence: cold import, solve-only and full study.</summary>
public sealed record ComparatorRuntimeRow(string Phase, double? GridSimSeconds, double? ComparatorSeconds);

/// <summary>
/// The comparison record shape for an AC transfer result against a paid tool (for example a PowerWorld ATC run on
/// identical assumptions). GridSim never claims parity: <see cref="Status"/> is UNVERIFIED unless a comparator was
/// actually run and its outputs recorded, and <see cref="Verified"/> is false whenever any comparator value is absent.
/// Failures and unsupported rows count in <see cref="Denominator"/>.
/// </summary>
public sealed record TransferComparison(ComparatorFreeze Freeze, IReadOnlyList<ComparatorParityRow> ModelParity,
    IReadOnlyList<ComparatorVerdictRow> Verdicts, IReadOnlyList<ComparatorRuntimeRow> Runtime, int Denominator,
    string Status, string Evidence)
{
    public bool Verified => Status == "VERIFIED" && Verdicts.Count > 0 &&
        Verdicts.All(v => v.Comparator is not null && v.Match is not null) &&
        ModelParity.All(p => p.Comparator is not null);

    /// <summary>The GridSim side of a comparison with every comparator field empty and the record UNVERIFIED.</summary>
    public static TransferComparison Unverified(TransferStudyResult result, string? release, string? caseSha256, string? studySha256)
    {
        ArgumentNullException.ThrowIfNull(result);
        string? limit = result.FirstLimit is { } f
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{f.Kind} {f.Index} ({f.Units}) observed {f.Observed:R} vs {f.Side} limit {f.Limit:R}, margin {f.Margin:R}") : null;
        string? outage = result.FirstLimit is { } g ? g.OutageIndex is int o ? "outage " + o : "intact" : null;
        return new(
            new(release, caseSha256, studySha256, null, null, null, null, null, null, null, null),
            [new("Lower feasible transfer", "MW", result.LowerMw, null, null, null, "UNVERIFIED")],
            [
                new("Status", result.Status + " / " + result.Outcome, null, null, null),
                new("Lower feasible transfer MW", result.LowerMw?.ToString("R", System.Globalization.CultureInfo.InvariantCulture), null, null, null),
                new("Limiting outage", outage, null, null, null),
                new("Limiting element and criterion", limit, null, null, null),
            ],
            [new("Cold import", null, null), new("Solve only", null, null), new("Full study", null, null)],
            1, "UNVERIFIED", "No paid comparator was run; comparator fields are empty by design.");
    }
}
