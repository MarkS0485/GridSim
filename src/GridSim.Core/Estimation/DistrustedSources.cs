namespace GridSim.Core.Estimation;

/// <summary>
/// A response control that marks one or more measurement sources distrusted, so their rows are forced to
/// pseudo-measurement treatment: their sigma is inflated, driving their weight (1/sigma^2) toward zero, so a
/// poisoned or suspect feed can no longer pull the estimate while the system stays observable. This is the
/// containment step the incident runbook calls on when a source is suspected of carrying bad or injected data
/// (NCSC CAF D1 response, feeding the FDIA screen's C1 story).
///
/// <para><b>Pure and deterministic.</b> It is a side-effect-free transform over a measurement or a set: same
/// input, same output, so a quarantined re-run is still bit-reproducible. It does not log or decide policy; the
/// hosting layer records a <c>SecurityEventKind.DistrustedSource</c> audit entry when it applies one.</para>
///
/// <para><b>It only ever weakens a row, never strengthens one.</b> It inflates sigma, never shrinks it, and never
/// changes a value, so it cannot be used to forge confidence in a source; and it leaves a distrusted row in the
/// set (down-weighted) rather than deleting it, so observability and the audit record are preserved.</para>
/// </summary>
public sealed class DistrustedSources
{
    /// <summary>The default factor a distrusted row's sigma is multiplied by. 1e4 drops its weight by 1e8, which
    /// is pseudo-measurement territory (present for observability, negligible for the fit).</summary>
    public const double DefaultSigmaInflation = 1.0e4;

    private readonly HashSet<string> _sources;
    private readonly double _inflation;

    /// <summary>
    /// Create the control for a set of distrusted source names (matched case-insensitively against
    /// <see cref="Measurement.Source"/>), with an optional sigma-inflation factor.
    /// </summary>
    /// <param name="distrustedSourceNames">The source names to distrust (for example <c>scada</c>, <c>pmu</c>).</param>
    /// <param name="sigmaInflation">The factor to multiply a distrusted row's sigma by; must be greater than 1.</param>
    public DistrustedSources(IEnumerable<string> distrustedSourceNames, double sigmaInflation = DefaultSigmaInflation)
    {
        ArgumentNullException.ThrowIfNull(distrustedSourceNames);
        if (!(sigmaInflation > 1.0) || !double.IsFinite(sigmaInflation))
            throw new ArgumentOutOfRangeException(nameof(sigmaInflation), sigmaInflation,
                "The sigma-inflation factor must be a finite number greater than 1 (a distrusted row may only be weakened).");
        _sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in distrustedSourceNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(distrustedSourceNames));
            _sources.Add(name.Trim());
        }
        _inflation = sigmaInflation;
    }

    /// <summary>The distrusted source names, as given.</summary>
    public IReadOnlyCollection<string> Sources => _sources;

    /// <summary>True when no source is distrusted (so <see cref="Apply(MeasurementSet)"/> is a no-op).</summary>
    public bool IsEmpty => _sources.Count == 0;

    /// <summary>True when <paramref name="source"/> is one of the distrusted sources.</summary>
    public bool IsDistrusted(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _sources.Contains(source.Trim());
    }

    /// <summary>Return <paramref name="m"/> unchanged, or a copy with its sigma inflated when its source is
    /// distrusted. Never changes the value.</summary>
    public Measurement Apply(Measurement m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return IsDistrusted(m.Source) ? m with { Sigma = m.Sigma * _inflation } : m;
    }

    /// <summary>Return a copy of <paramref name="set"/> with every distrusted row down-weighted. A no-op (the same
    /// set) when nothing is distrusted or no row matches, so it is cheap to apply unconditionally.</summary>
    public MeasurementSet Apply(MeasurementSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (IsEmpty || set.Measurements.Count == 0)
            return set;

        Measurement[]? rewritten = null;
        for (int i = 0; i < set.Measurements.Count; i++)
        {
            Measurement original = set.Measurements[i];
            Measurement applied = Apply(original);
            if (!ReferenceEquals(applied, original))
            {
                rewritten ??= set.Measurements.ToArray();
                rewritten[i] = applied;
            }
        }
        return rewritten is null ? set : set with { Measurements = rewritten };
    }
}
