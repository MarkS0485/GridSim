namespace GridSim.Core.Diagnostics.Incident;

/// <summary>
/// How security events map to incidents: the category and baseline severity for a single event of each kind,
/// and the burst thresholds the <see cref="SecurityEventMonitor"/> uses to escalate a run of events into one
/// raised incident (NCSC CAF C2 proactive discovery / threat hunting). A record so a deployment can tune the
/// thresholds without code; the defaults are deliberately conservative.
/// </summary>
/// <param name="HorizonViolationBurst">How many refused too-fresh inputs in the observed stream escalate from
/// routine (the guard doing its job) to one significant incident (a possible probing or misconfiguration).</param>
/// <param name="SuspectedFdiaBurst">How many suspected-FDIA flags escalate to one critical incident (one is
/// already significant; a rising rate is critical).</param>
/// <param name="AuditChainBrokenBurst">How many audit-chain failures escalate (one is already critical).</param>
public sealed record IncidentPolicy(
    int HorizonViolationBurst = 20,
    int SuspectedFdiaBurst = 3,
    int AuditChainBrokenBurst = 1)
{
    // GR-850: a burst threshold is a count, so a negative one is refused (0 still means "not escalated"), on
    // construction and on a `with` copy alike.
    /// <summary>See the record's parameter notes; never negative.</summary>
    public int HorizonViolationBurst { get; init => field = NonNegative(value, nameof(HorizonViolationBurst)); } = NonNegative(HorizonViolationBurst, nameof(HorizonViolationBurst));

    /// <summary>See the record's parameter notes; never negative.</summary>
    public int SuspectedFdiaBurst { get; init => field = NonNegative(value, nameof(SuspectedFdiaBurst)); } = NonNegative(SuspectedFdiaBurst, nameof(SuspectedFdiaBurst));

    /// <summary>See the record's parameter notes; never negative.</summary>
    public int AuditChainBrokenBurst { get; init => field = NonNegative(value, nameof(AuditChainBrokenBurst)); } = NonNegative(AuditChainBrokenBurst, nameof(AuditChainBrokenBurst));

    private static int NonNegative(int value, string name)
        => value >= 0 ? value : throw new ArgumentOutOfRangeException(name, value, "A burst threshold is a count of events; it cannot be negative.");

    /// <summary>The conservative default policy.</summary>
    public static IncidentPolicy Default { get; } = new();

    /// <summary>The incident category and baseline severity for a single event of <paramref name="kind"/>. The
    /// mapping is fixed (not per-deployment), so it is static; the tunable part is the burst thresholds.</summary>
    public static (IncidentCategory Category, SecuritySeverity Severity) Classify(SecurityEventKind kind) => kind switch
    {
        SecurityEventKind.HorizonViolation => (IncidentCategory.HorizonViolation, SecuritySeverity.Info),
        SecurityEventKind.RestrictionRefused => (IncidentCategory.DataIntegrity, SecuritySeverity.Significant),
        SecurityEventKind.SuspectedFdia => (IncidentCategory.SuspectedFdia, SecuritySeverity.Significant),
        SecurityEventKind.InfeasibleState => (IncidentCategory.InfeasibleState, SecuritySeverity.Low),
        SecurityEventKind.AccessDenied => (IncidentCategory.AccessDenied, SecuritySeverity.Low),
        SecurityEventKind.AuditChainBroken => (IncidentCategory.AuditChainBroken, SecuritySeverity.Critical),
        SecurityEventKind.LiveDataQuarantined => (IncidentCategory.DataIntegrity, SecuritySeverity.Info),
        SecurityEventKind.DistrustedSource => (IncidentCategory.DistrustedSource, SecuritySeverity.Significant),
        SecurityEventKind.DependencyAlert => (IncidentCategory.DependencyAlert, SecuritySeverity.Significant),
        SecurityEventKind.AvailabilityDegraded => (IncidentCategory.AvailabilityDegraded, SecuritySeverity.Significant),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a defined SecurityEventKind value."),
    };

    /// <summary>The burst threshold for a kind, or 0 when a run of that kind is not itself escalated.</summary>
    public int BurstThreshold(SecurityEventKind kind) => kind switch
    {
        SecurityEventKind.HorizonViolation => HorizonViolationBurst,
        SecurityEventKind.SuspectedFdia => SuspectedFdiaBurst,
        SecurityEventKind.AuditChainBroken => AuditChainBrokenBurst,
        _ => 0,
    };
}
