namespace GridSim.Core.Analysis.Reliability;

/// <summary>
/// One series component (feeder section, lateral, transformer) of a radial feeder. It is fed from the downstream end of
/// <paramref name="Parent"/> (null: from the source bus, behind the feeder breaker) and supplies the node at its own
/// downstream end. <paramref name="Fused"/>: a protective device at its upstream end clears its own faults and any fault
/// downstream of it that no nearer device clears (otherwise the next device upstream, ultimately the feeder breaker,
/// clears). <paramref name="Switchable"/>: a disconnector at its upstream end can isolate it after a fault.
/// <paramref name="FailuresPerYear"/> (sustained failures per year) and <paramref name="RepairHours"/> (mean time to
/// repair or replace, hours) may be omitted; the study then reports them missing instead of assuming a value.
/// </summary>
public sealed record ReliabilityComponent(string Id, string? Parent, bool Fused, bool Switchable,
    double? FailuresPerYear = null, double? RepairHours = null);

/// <summary>A load point at the downstream end of <paramref name="Component"/>: <paramref name="Customers"/> customers
/// and an average (annual mean) demand of <paramref name="AverageLoadMw"/> MW.</summary>
public sealed record ReliabilityLoadPoint(string Id, string Component, int Customers, double AverageLoadMw);

/// <summary>
/// The dependency and restoration assumptions, stated before any index is computed. v1 supports exactly:
/// <paramref name="FailureDependency"/> = <c>IndependentFirstOrder</c> (component failures are independent and
/// overlapping outages are neglected, the first-order approximation, valid while each lambda x r is small against the
/// year), <paramref name="AlternateSupply"/> = <c>None</c> (no back-feed: a load downstream of a failed component waits
/// for its repair), and <paramref name="HoursPerYear"/> (the year length used by ASAI, hours, stated explicitly).
/// </summary>
public sealed record ReliabilityAssumptions(string FailureDependency, string AlternateSupply, double HoursPerYear);

/// <summary>A radial-feeder reliability study (RC3 P8). <paramref name="SwitchingHours"/> is the time to locate, isolate a
/// failed component by an upstream disconnector and restore the supply upstream of it (reclose the breaker or replace
/// the fuse), hours; it is needed only where such isolation is possible, and reported missing there if omitted.</summary>
public sealed record ReliabilityRequest(IReadOnlyList<ReliabilityComponent> Components,
    IReadOnlyList<ReliabilityLoadPoint> LoadPoints, ReliabilityAssumptions Assumptions, double? SwitchingHours = null);

/// <summary>One failure mode's effect on one load point: the component, which device clears it, and whether the load
/// point waits for the repair or only for isolation and switching.</summary>
public sealed record ReliabilityExposure(string Component, string ClearedBy, string Restoration,
    double? FailuresPerYear, double? DurationHours, double? UnavailabilityHoursPerYear);

/// <summary>Load-point indices: failure rate (interruptions per year), annual outage time (hours per year), average
/// outage duration (hours per interruption) and energy not supplied (MWh per year). Null where rate data is missing.</summary>
public sealed record LoadPointReliability(string Id, int Customers, double AverageLoadMw,
    double? InterruptionsPerYear, double? OutageHoursPerYear, double? HoursPerInterruption, double? EnergyNotSuppliedMwhPerYear,
    IReadOnlyList<ReliabilityExposure> Exposures);

/// <summary>A system index with its units and definition.</summary>
public sealed record ReliabilityIndex(string Name, double? Value, string Units, string Definition);

/// <summary>A missing input: which field of which component, and the load points whose indices it withholds.</summary>
public sealed record MissingRateData(string Component, string Field, IReadOnlyList<string> AffectedLoadPoints);

/// <summary>The result. <paramref name="Status"/> is COMPLETE when every index is computed, INCOMPLETE when missing rate
/// data withholds any (every computable load-point value is still reported).</summary>
public sealed record ReliabilityStudyResult(string Status, IReadOnlyList<ReliabilityIndex> SystemIndices,
    IReadOnlyList<LoadPointReliability> LoadPoints, IReadOnlyList<MissingRateData> Missing,
    double TotalCustomers, double? MaxLambdaRepairProductPerYear, IReadOnlyList<string> Scope);

/// <summary>
/// RC3 P8 reliability package: analytical (failure-modes-and-effects) reliability indices for a radial feeder with
/// explicit failure rates, repair and switching times, customers, average load and dependency assumptions. It reads no
/// network model and runs no power flow: it is outside the general power-flow and protection paths.
/// <para>
/// For a failure of component k, the clearing device is the nearest fused component on the path from k to the source
/// (k itself first), otherwise the feeder breaker. Every load point downstream of that device is interrupted. A load
/// point on k's own path to the source (at or downstream of k) is restored only by repair: duration r_k. Any other
/// interrupted load point is restored after the switching time s if some switchable component lies on k's path below
/// the point where it joins the load point's path (that disconnector isolates k), otherwise after r_k. Then, per load
/// point j, lambda_j = sum lambda_k, U_j = sum lambda_k t_kj, r_j = U_j / lambda_j and ENS_j = L_j U_j; and over the
/// customers N_j: SAIFI = sum lambda_j N_j / sum N_j, SAIDI = sum U_j N_j / sum N_j, CAIDI = SAIDI / SAIFI,
/// ASAI = 1 - SAIDI / hoursPerYear, ENS = sum ENS_j and AENS = ENS / sum N_j. Sums run in input order.
/// </para>
/// </summary>
public static class ReliabilityStudy
{
    public const string IndependentFirstOrder = "IndependentFirstOrder";
    public const string NoAlternateSupply = "None";
    private const string Breaker = "FeederBreaker";

    public static readonly IReadOnlyList<string> StepOrder =
    [
        "ValidateRequest", "CheckAssumptions", "ResolveTopology", "FailureModesAndEffects", "LoadPointIndices",
        "SystemIndices", "ReportMissingData"
    ];

    public static readonly IReadOnlyList<string> Scope =
    [
        "Radial feeder only; one source behind a feeder breaker; components in series along each path.",
        "Sustained interruptions only; momentary interruptions and reclosing successes are not modelled.",
        "Independent failures, first-order approximation: overlapping outages neglected.",
        "No alternate supply or back-feed; no transfer-capacity or voltage checks after switching.",
        "Protection operates perfectly and selectively as declared (fused components); no stuck devices.",
        "Load points carry constant average demand and customer counts; no load-duration curve.",
        "No network model or power flow is read; the topology and data are those stated in the request."
    ];

    public static ReliabilityStudyResult Run(ReliabilityRequest request)
    {
        Validate(request);
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < request.Components.Count; k++) byId[request.Components[k].Id] = k;

        // Path from each component up to the source, nearest first (validated acyclic).
        var paths = new List<int>[request.Components.Count];
        for (int k = 0; k < request.Components.Count; k++)
        {
            var path = new List<int>();
            int? c = k;
            while (c is int i) { path.Add(i); c = request.Components[i].Parent is string p ? byId[p] : null; }
            paths[k] = path;
        }

        int?[] clearing = new int?[request.Components.Count];   // fused component that clears k, or null for the breaker
        for (int k = 0; k < request.Components.Count; k++)
            foreach (int c in paths[k]) if (request.Components[c].Fused) { clearing[k] = c; break; }

        var missing = new List<MissingRateData>();
        var missingIndex = new Dictionary<(string, string), List<string>>();
        void Missing(string component, string field, string loadPoint)
        {
            if (!missingIndex.TryGetValue((component, field), out var list))
            {
                list = [];
                missingIndex[(component, field)] = list;
                missing.Add(new MissingRateData(component, field, list));
            }
            if (!list.Contains(loadPoint)) list.Add(loadPoint);
        }

        double? maxLambdaR = null;
        foreach (ReliabilityComponent c in request.Components)
            if (c.FailuresPerYear is double l && c.RepairHours is double r)
                maxLambdaR = Math.Max(maxLambdaR ?? 0, l * r);

        var points = new List<LoadPointReliability>(request.LoadPoints.Count);
        foreach (ReliabilityLoadPoint lp in request.LoadPoints)
        {
            List<int> jPath = paths[byId[lp.Component]];
            var onPath = new HashSet<int>(jPath);
            var exposures = new List<ReliabilityExposure>();
            double lambda = 0, unavailability = 0;
            bool lambdaKnown = true, uKnown = true;
            for (int k = 0; k < request.Components.Count; k++)
            {
                ReliabilityComponent comp = request.Components[k];
                // Interrupted when the clearing device lies on the load point's path (the breaker always does).
                if (clearing[k] is int f && !onPath.Contains(f)) continue;
                string clearedBy = clearing[k] is int fc ? request.Components[fc].Id : Breaker;
                string restoration;
                double? duration;
                if (onPath.Contains(k))
                {
                    restoration = "Repair";
                    duration = comp.RepairHours;
                    if (duration is null) Missing(comp.Id, "repairHours", lp.Id);
                }
                else
                {
                    // A disconnector on k's path below the junction with the load point's path isolates k.
                    bool isolable = false;
                    foreach (int c in paths[k])
                    {
                        if (onPath.Contains(c)) break;
                        if (request.Components[c].Switchable) { isolable = true; break; }
                    }
                    if (isolable)
                    {
                        restoration = "Switching";
                        duration = request.SwitchingHours;
                        if (duration is null) Missing("(study)", "switchingHours", lp.Id);
                    }
                    else
                    {
                        restoration = "Repair";
                        duration = comp.RepairHours;
                        if (duration is null) Missing(comp.Id, "repairHours", lp.Id);
                    }
                }
                double? rate = comp.FailuresPerYear;
                if (rate is null) Missing(comp.Id, "failuresPerYear", lp.Id);
                double? u = rate is double lr && duration is double d ? lr * d : null;
                exposures.Add(new ReliabilityExposure(comp.Id, clearedBy, restoration, rate, duration, u));
                if (rate is double rr) lambda += rr; else lambdaKnown = false;
                if (u is double uu) unavailability += uu; else uKnown = false;
            }
            double? lam = lambdaKnown ? lambda : null;
            double? un = uKnown && lambdaKnown ? unavailability : null;
            double? perInterruption = lam is double l2 && un is double u2 && l2 > 0 ? u2 / l2 : null;
            double? ens = un is double u3 ? lp.AverageLoadMw * u3 : null;
            points.Add(new LoadPointReliability(lp.Id, lp.Customers, lp.AverageLoadMw, lam, un, perInterruption, ens, exposures));
        }

        double customers = 0;
        double? saifiSum = 0, saidiSum = 0, ensSum = 0;
        foreach (LoadPointReliability p in points)
        {
            customers += p.Customers;
            saifiSum = p.InterruptionsPerYear is double l ? saifiSum + l * p.Customers : null;
            saidiSum = p.OutageHoursPerYear is double u ? saidiSum + u * p.Customers : null;
            ensSum = p.EnergyNotSuppliedMwhPerYear is double e ? ensSum + e : null;
        }
        double? saifi = saifiSum / customers;
        double? saidi = saidiSum / customers;
        double? caidi = saifi is double f1 && saidi is double d1 && f1 > 0 ? d1 / f1 : null;
        double? asai = saidi is double d2 ? 1.0 - d2 / request.Assumptions.HoursPerYear : null;
        double? aens = ensSum / customers;
        ReliabilityIndex[] indices =
        [
            new("SAIFI", saifi, "interruptions/(customer.yr)", "sum(lambda_j N_j) / sum(N_j)"),
            new("SAIDI", saidi, "h/(customer.yr)", "sum(U_j N_j) / sum(N_j)"),
            new("CAIDI", caidi, "h/interruption", "SAIDI / SAIFI"),
            new("ASAI", asai, "1", "1 - SAIDI / hoursPerYear"),
            new("ENS", ensSum, "MWh/yr", "sum(L_j U_j)"),
            new("AENS", aens, "MWh/(customer.yr)", "ENS / sum(N_j)"),
        ];
        bool complete = missing.Count == 0 && indices.All(i => i.Value is not null || i.Name == "CAIDI");
        return new(complete ? "COMPLETE" : "INCOMPLETE", indices, points, missing, customers, maxLambdaR, Scope);
    }

    private static void Validate(ReliabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Assumptions is null)
            throw new ArgumentException("Dependency assumptions must be stated before any index is computed.");
        if (request.Assumptions.FailureDependency != IndependentFirstOrder)
            throw new ArgumentException($"assumptions.failureDependency must be '{IndependentFirstOrder}' (the only supported model in v1).");
        if (request.Assumptions.AlternateSupply != NoAlternateSupply)
            throw new ArgumentException($"assumptions.alternateSupply must be '{NoAlternateSupply}' (back-feed is not modelled in v1).");
        if (!double.IsFinite(request.Assumptions.HoursPerYear) || request.Assumptions.HoursPerYear <= 0)
            throw new ArgumentException("assumptions.hoursPerYear must be finite and positive.");
        if (request.SwitchingHours is double s && (!double.IsFinite(s) || s < 0))
            throw new ArgumentException("switchingHours must be finite and non-negative.");
        if (request.Components is null || request.Components.Count == 0)
            throw new ArgumentException("At least one component is required.");
        if (request.LoadPoints is null || request.LoadPoints.Count == 0)
            throw new ArgumentException("At least one load point is required.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReliabilityComponent c in request.Components)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.Id)) throw new ArgumentException("Every component needs an id.");
            if (!ids.Add(c.Id)) throw new ArgumentException($"Component id '{c.Id}' is duplicated.");
            if (c.FailuresPerYear is double l && (!double.IsFinite(l) || l < 0))
                throw new ArgumentException($"Component '{c.Id}': failuresPerYear must be finite and non-negative.");
            if (c.RepairHours is double r && (!double.IsFinite(r) || r < 0))
                throw new ArgumentException($"Component '{c.Id}': repairHours must be finite and non-negative.");
        }
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (ReliabilityComponent c in request.Components)
        {
            if (c.Parent is string p && !ids.Contains(p))
                throw new ArgumentException($"Component '{c.Id}': parent '{p}' is not a declared component.");
            parents[c.Id] = c.Parent;
        }
        foreach (ReliabilityComponent c in request.Components)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (string? at = c.Id; at is not null; at = parents[at])
                if (!seen.Add(at)) throw new ArgumentException($"Component '{c.Id}' is on a cycle; the feeder must be radial.");
        }
        var points = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReliabilityLoadPoint lp in request.LoadPoints)
        {
            if (lp is null || string.IsNullOrWhiteSpace(lp.Id)) throw new ArgumentException("Every load point needs an id.");
            if (!points.Add(lp.Id)) throw new ArgumentException($"Load point id '{lp.Id}' is duplicated.");
            if (lp.Component is null || !ids.Contains(lp.Component))
                throw new ArgumentException($"Load point '{lp.Id}': component '{lp.Component}' is not declared.");
            if (lp.Customers < 0) throw new ArgumentException($"Load point '{lp.Id}': customers must be non-negative.");
            if (!double.IsFinite(lp.AverageLoadMw) || lp.AverageLoadMw < 0)
                throw new ArgumentException($"Load point '{lp.Id}': averageLoadMw must be finite and non-negative.");
        }
        if (request.LoadPoints.Sum(lp => (long)lp.Customers) <= 0)
            throw new ArgumentException("The load points must serve at least one customer.");
    }
}
