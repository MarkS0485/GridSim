namespace GridSim.Core.Assets;

/// <summary>
/// One bulk-binding rule from <c>asset-bindings.json</c>: bind every asset whose fuel/technology
/// <see cref="Model.Generator.Type"/> (or transformer/line) matches <see cref="MatchType"/> to the
/// class <see cref="ClassName"/>. Inline <c>dynamicsClass</c> on an individual asset always wins over
/// a rule. Match is case-insensitive and exact by default; a trailing <c>*</c> makes it a PREFIX
/// wildcard (e.g. <c>"wind*"</c> matches both <c>wind-onshore</c> and <c>wind-offshore</c>), which
/// guards against fuel-vocabulary drift silently dropping assets. An unmatched asset stays passive.
/// </summary>
public sealed record AssetBinding(string MatchType, string ClassName);

/// <summary>Resolves which authored class (if any) an asset is bound to: inline wins, then exact rules, then prefix wildcards.</summary>
public sealed class AssetBindingSet
{
    private readonly IReadOnlyList<AssetBinding> _rules;

    public AssetBindingSet(IEnumerable<AssetBinding> rules) => _rules = rules.ToList();

    public int RuleCount => _rules.Count;

    /// <summary>
    /// The rules in the order they were given (read-only). N321 (LF7, October 2026): the JSON v2 case writer
    /// (<see cref="IO.Json.V2CaseWriter"/>) writes them back as the case's <c>bindings</c>, in this order, so a written
    /// case resolves every asset exactly as this set does (the first exact rule, then the first prefix rule, both in
    /// this order). Exposing the list changes no resolution.
    /// </summary>
    public IReadOnlyList<AssetBinding> Rules => _rules;

    /// <summary>
    /// The class name for an asset, or null if unbound. <paramref name="inlineClass"/> (the asset's own
    /// <c>dynamicsClass</c>) takes precedence; otherwise the first EXACT-matching rule, then the first
    /// PREFIX-wildcard rule (<c>MatchType</c> ending in <c>*</c>). Exact always beats a wildcard, so a
    /// specific rule can override a broad one regardless of order.
    /// </summary>
    public string? Resolve(string? inlineClass, string? assetType)
    {
        if (!string.IsNullOrWhiteSpace(inlineClass)) return inlineClass;
        if (string.IsNullOrWhiteSpace(assetType)) return null;

        // Exact match first (predictable, specific-beats-broad).
        foreach (AssetBinding r in _rules)
            if (!r.MatchType.EndsWith('*') && string.Equals(r.MatchType, assetType, StringComparison.OrdinalIgnoreCase))
                return r.ClassName;

        // Then prefix wildcards ("wind*" -> wind-onshore, wind-offshore).
        foreach (AssetBinding r in _rules)
            if (r.MatchType.EndsWith('*'))
            {
                string prefix = r.MatchType[..^1];
                if (assetType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return r.ClassName;
            }
        return null;
    }
}

/// <summary>Builds the concrete controller for an authored class bound to a specific asset.</summary>
public static class AssetControllerFactory
{
    /// <summary>
    /// Create the active controller (inverter) for <paramref name="cls"/> bound to a generator at
    /// <paramref name="bus"/> dispatching <paramref name="pgMw"/> with rating <paramref name="ratedMw"/>.
    /// Returns null for passive kinds (they are not swing-power contributors).
    /// </summary>
    public static IActiveAssetDynamics? CreateActive(AssetClass cls, int bus, double pgMw, double ratedMw, double f0)
        => cls.Kind switch
        {
            AssetKind.GridFollowingInverter => new GridFollowingInverter(cls, bus, pgMw, ratedMw, f0),
            AssetKind.GridFormingInverter => new GridFormingInverter(cls, bus, pgMw, ratedMw, f0),
            _ => null,
        };

    /// <summary>Create the passive-plant controller (transformer/line) for a class bound to a branch, or null for active kinds.</summary>
    public static IPassiveAssetDynamics? CreatePassive(AssetClass cls, string branchName)
        => cls.Kind switch
        {
            AssetKind.Transformer => new TransformerDynamics(cls, branchName),
            AssetKind.Line => new LineThermalDynamics(cls, branchName),
            _ => null,
        };
}
