using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Qsts;

/// <summary>What is being hosted.</summary>
public enum HostedResource
{
    /// <summary>PV (inverter-based generation): a QSTS PV unit at the candidate bus.</summary>
    Pv,
    /// <summary>Added demand (electric vehicle charging, heat pumps): a QSTS load addition at the candidate bus.</summary>
    Load,
}

/// <summary>How each size is judged.</summary>
public enum HostingMethod
{
    /// <summary>Snapshot: one regulated power flow per stated condition (load multiplier, PV availability); the size
    /// passes when every snapshot is within the limits.</summary>
    Snapshot,
    /// <summary>Time series: the whole QSTS study with the resource added; the size passes when the run stays within the
    /// limits for no longer than the allowed time and (if asked) does not raise the tap operations beyond the allowance.</summary>
    Qsts,
}

/// <summary>The constraints a hosting-capacity size can break.</summary>
public enum HostingConstraint
{
    None,
    /// <summary>A bus above its upper voltage limit.</summary>
    Overvoltage,
    /// <summary>A bus below its lower voltage limit.</summary>
    Undervoltage,
    /// <summary>A rated branch above the loading limit.</summary>
    Thermal,
    /// <summary>Active power flowing back through the monitored source branch (the protection proxy: feeder protection
    /// and the source transformer's tap changer are set for forward flow).</summary>
    ReversePower,
    /// <summary>Tap operations above the base run's by more than the allowance (QSTS only).</summary>
    TapOperations,
    /// <summary>A power flow did not converge (voltage collapse or beyond the network's transfer limit).</summary>
    NonConvergence,
}

/// <summary>One snapshot condition: the demand multiplier (all buses with demand) and the hosted resource's output
/// multiplier (PV availability, or the added load's multiplier).</summary>
public readonly record struct HostingSnapshot(double LoadMultiplier, double ResourceMultiplier);

/// <summary>The hosting-capacity study.</summary>
public sealed record HostingStudy
{
    public HostedResource Resource { get; init; } = HostedResource.Pv;
    public HostingMethod Method { get; init; } = HostingMethod.Snapshot;

    /// <summary>The candidate buses (null: every PQ bus).</summary>
    public IReadOnlyList<int>? Candidates { get; init; }

    /// <summary>The upper end of the search (MW). Must be above 0.</summary>
    public required double MaxMw { get; init; }

    /// <summary>Bisection stops when the bracket is at most this wide (MW); the reported capacity is the bracket's
    /// passing end, so it is never above the true limit and at most this far below it (for a monotone limit).</summary>
    public required double ToleranceMw { get; init; }

    /// <summary>Snapshot conditions (Snapshot method). Default for PV: minimum demand (0.3) and full demand with full
    /// sun; for load: full demand.</summary>
    public IReadOnlyList<HostingSnapshot>? Snapshots { get; init; }

    /// <summary>The base QSTS study: the profiles, devices, controls and limits every size runs with. For the snapshot
    /// method only its controls (OLTC, capacitors) and limits are used.</summary>
    public required QstsStudy Base { get; init; }

    /// <summary>QSTS method: the profile driving the hosted resource (PV availability, or the added load's shape); null =
    /// constant 1.</summary>
    public string? ResourceProfile { get; init; }

    /// <summary>PV inverter settings (default unity power factor).</summary>
    public InverterSettings Inverter { get; init; } = new();

    /// <summary>PV apparent power rating as a multiple of the rated MW (at least 1).</summary>
    public double MvaPerMw { get; init; } = 1.0;

    /// <summary>Added load power factor (lagging).</summary>
    public double LoadPowerFactor { get; init; } = 1.0;

    /// <summary>The branch whose reverse active power is the protection proxy (null: no reverse-power check).</summary>
    public string? ReverseFlowBranch { get; init; }

    /// <summary>Reverse power (MW, flowing To-to-From at the From end) allowed before it counts.</summary>
    public double ReverseFlowAllowanceMw { get; init; } = 0.0;

    /// <summary>QSTS method: time (s) a bus or branch may spend outside its limits before it counts.</summary>
    public double AllowedViolationSeconds { get; init; } = 0.0;

    /// <summary>QSTS method: tap operations may exceed the base run's by this percentage (null: no tap criterion).</summary>
    public double? TapIncreasePercent { get; init; }

    /// <summary>Which constraints are judged (default all).</summary>
    public IReadOnlySet<HostingConstraint>? Criteria { get; init; }

    /// <summary>Candidates solved at once (scenario parallelism; the answer does not depend on it).</summary>
    public int Threads { get; init; } = 1;

    /// <summary>The snapshots in force.</summary>
    public IReadOnlyList<HostingSnapshot> SnapshotsOrDefault => Snapshots ?? (Resource == HostedResource.Pv
        ? new[] { new HostingSnapshot(0.3, 1.0), new HostingSnapshot(1.0, 1.0) }
        : new[] { new HostingSnapshot(1.0, 1.0) });

    public bool Judges(HostingConstraint c) => Criteria is null || Criteria.Contains(c);
}

/// <summary>A constraint broken at a size: what, where (bus id, branch index, or -1) and how far (pu, %, MW, count).</summary>
public sealed record HostingViolation(HostingConstraint Constraint, int Element, double Value, string Detail);

/// <summary>The hosting capacity at one candidate bus.</summary>
/// <param name="Bus">The candidate bus.</param>
/// <param name="HostingMw">The largest size found within the limits (the bracket's passing end).</param>
/// <param name="FailingMw">The smallest size found outside them (the bracket's failing end); +Infinity when MaxMw passed.</param>
/// <param name="Limiting">The constraints broken at <paramref name="FailingMw"/> (empty when MaxMw passed).</param>
/// <param name="Evaluations">Sizes evaluated.</param>
/// <param name="BaseViolations">Constraints the base (size 0) already breaks; they count only where the resource makes
/// them worse. Listed for the report.</param>
public sealed record HostingLocationResult(int Bus, double HostingMw, double FailingMw, IReadOnlyList<HostingViolation> Limiting,
    int Evaluations, IReadOnlyList<HostingViolation> BaseViolations)
{
    /// <summary>The first limiting constraint (None when not limited within the search range).</summary>
    public HostingConstraint Primary => Limiting.Count > 0 ? Limiting[0].Constraint : HostingConstraint.None;
    public bool LimitedWithinRange => !double.IsPositiveInfinity(FailingMw);
}

/// <summary>The study result: one row per candidate, in candidate order.</summary>
public sealed record HostingResult(HostingStudy Study, IReadOnlyList<HostingLocationResult> Locations, double ElapsedSeconds);

/// <summary>
/// Hosting capacity: the largest PV (or added load) at each candidate bus before a constraint is broken, by bisection on
/// the size in MW. Each size is judged by the snapshot method (regulated power flows at stated demand and resource
/// levels) or by the QSTS method (the whole time series). A constraint the base case already breaks counts only where the
/// resource makes it worse (strictly larger excess), so an existing problem does not mask the hosting limit and is not
/// blamed on the resource.
/// <para><b>Search.</b> Size 0 must pass (it is the base, with base violations excluded by construction); MaxMw is tried
/// next: if it passes the capacity is reported as at least MaxMw. Otherwise the bracket [pass, fail] = [0, MaxMw] is
/// halved (mid = lo + (hi - lo) / 2) until hi - lo &lt;= ToleranceMw. Deterministic: the same sizes are evaluated in the
/// same order on every run; for a limit that is monotone in the size the true capacity lies in [HostingMw, FailingMw].
/// Candidates are independent scenarios and may run in parallel, each with its own engine; results land by index, so
/// the answer does not depend on the thread count.</para>
/// </summary>
public static class HostingCapacity
{
    /// <summary>The name the hosted unit carries in the QSTS study.</summary>
    public const string UnitName = "hosting-unit";

    /// <summary>Run the study. <paramref name="engineFactory"/> makes one engine per scenario thread.</summary>
    public static HostingResult Run(GridModel model, HostingStudy study, Func<IPowerFlowSolver> engineFactory, PowerFlowOptions? options = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(study);
        ArgumentNullException.ThrowIfNull(engineFactory);
        Validate(model, study);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int[] candidates = (study.Candidates ?? model.Buses.Where(b => b.Type == BusType.Pq).Select(b => b.Id).ToArray()).ToArray();
        var results = new HostingLocationResult[candidates.Length];
        // The base (size 0) does not depend on the candidate: run it once.
        BaseRun baseRun = RunBase(model, study, engineFactory(), options);
        Parallel.For(0, candidates.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, study.Threads), CancellationToken = cancellation },
            () => engineFactory(),
            (i, _, engine) =>
            {
                results[i] = Locate(model, study, candidates[i], engine, options, baseRun);
                return engine;
            },
            _ => { });
        return new HostingResult(study, results, sw.Elapsed.TotalSeconds);
    }

    private static void Validate(GridModel model, HostingStudy s)
    {
        var p = new List<string>();
        if (!(s.MaxMw > 0) || !double.IsFinite(s.MaxMw)) p.Add($"maxMw must be a finite number above 0 (got {s.MaxMw}).");
        if (!(s.ToleranceMw > 0) || !(s.ToleranceMw < s.MaxMw)) p.Add($"toleranceMw must be above 0 and below maxMw (got {s.ToleranceMw}).");
        if (!(s.MvaPerMw >= 1) || !double.IsFinite(s.MvaPerMw)) p.Add("mvaPerMw must be at least 1.");
        if (!(s.LoadPowerFactor > 0 && s.LoadPowerFactor <= 1)) p.Add("the load power factor must be in (0, 1].");
        if (!(s.AllowedViolationSeconds >= 0)) p.Add("allowedViolationSeconds must be 0 or more.");
        if (s.TapIncreasePercent is double t && !(t >= 0)) p.Add("tapIncreasePercent must be 0 or more.");
        if (s.Threads < 1) p.Add("threads must be at least 1.");
        if (s.Method == HostingMethod.Snapshot && s.TapIncreasePercent is not null) p.Add("the tap-operation criterion needs the QSTS method (a snapshot has no time).");
        var busIds = model.Buses.Select(b => b.Id).ToHashSet();
        var types = new Dictionary<int, BusType>();
        foreach (Bus b in model.Buses) types.TryAdd(b.Id, b.Type);
        if (s.Candidates is { } c)
        {
            if (c.Count == 0) p.Add("no candidate buses.");
            if (c.Distinct().Count() != c.Count) p.Add("a candidate bus is listed twice.");
            foreach (int b in c)
                if (!busIds.Contains(b)) p.Add($"candidate bus {b} is not in the case.");
                else if (s.Resource == HostedResource.Pv && s.Inverter.Function != InverterFunction.Unity && types[b] != BusType.Pq)
                    p.Add($"candidate bus {b} is a {types[b]} bus: a reactive power function there means nothing.");
        }
        foreach (HostingSnapshot sn in s.SnapshotsOrDefault)
            if (!(sn.LoadMultiplier >= 0) || !(sn.ResourceMultiplier >= 0) || !double.IsFinite(sn.LoadMultiplier) || !double.IsFinite(sn.ResourceMultiplier))
                p.Add("snapshot multipliers must be finite and 0 or more.");
        if (s.ReverseFlowBranch is { } rb && !model.Branches.Any(b => b.Name == rb)) p.Add($"reverse-flow branch '{rb}' is not in the case.");
        if (s.Method == HostingMethod.Qsts && s.ResourceProfile is { } rp && s.Base.Profile(rp) is null) p.Add($"resource profile '{rp}' is not defined in the base study.");
        if (p.Count > 0)
            throw new InvalidDataException($"hosting-capacity study refused ({p.Count} problem{(p.Count == 1 ? "" : "s")}):\n  - " + string.Join("\n  - ", p));
        s.Base.Validate(model);
    }

    /// <summary>The QSTS study that judges <paramref name="mw"/> at <paramref name="bus"/> (for the snapshot method, one per
    /// snapshot).</summary>
    internal static IEnumerable<QstsStudy> StudiesFor(HostingStudy s, int bus, double mw)
    {
        QstsStudy b = s.Base with { MonitorBranches = s.ReverseFlowBranch is null ? Array.Empty<string>() : new[] { s.ReverseFlowBranch } };
        if (s.Method == HostingMethod.Qsts)
        {
            yield return WithResource(b, s, bus, mw, s.ResourceProfile);
            yield break;
        }
        foreach (HostingSnapshot sn in s.SnapshotsOrDefault)
        {
            // One step: demand scaled by the snapshot's multiplier, the resource at its multiplier; the base study's
            // controls and limits, none of its profiles or time-varying devices. A snapshot is a regulated steady state, so
            // every delay and dead time is zero (a one-step run with a 30 s tap delay would never let the tap act).
            var profiles = new List<QstsProfile> { new("hosting-load", 1, new[] { sn.LoadMultiplier }), new("hosting-resource", 1, new[] { sn.ResourceMultiplier }) };
            QstsStudy one = b with
            {
                StepSeconds = 1, Steps = 1, Profiles = profiles,
                Loads = new[] { new QstsLoadBinding(null, "hosting-load") },
                Generators = Array.Empty<QstsGeneratorBinding>(), LoadAdditions = Array.Empty<QstsLoadAddition>(),
                Pv = Array.Empty<QstsPvUnit>(), Storage = Array.Empty<QstsStorageUnit>(),
                OltcDefault = Instant(b.OltcDefault),
                OltcSettings = b.OltcSettings.ToDictionary(kv => kv.Key, kv => Instant(kv.Value), StringComparer.Ordinal),
                Capacitors = b.Capacitors.Select(c => c with { DelaySeconds = 0, OffDelaySeconds = 0, DeadTimeSeconds = 0 }).ToArray(),
            };
            yield return WithResource(one, s, bus, mw, "hosting-resource");
        }
    }

    private static OltcTiming Instant(OltcTiming t) => t with
    {
        DelaySeconds = 0, InterTapSeconds = 0, MinDelaySeconds = 0, Characteristic = OltcCharacteristic.Definite,
    };

    private static QstsStudy WithResource(QstsStudy b, HostingStudy s, int bus, double mw, string? profile)
    {
        if (mw == 0) return b;
        if (s.Resource == HostedResource.Pv)
            return b with { Pv = b.Pv.Append(new QstsPvUnit(UnitName, bus, mw, mw * s.MvaPerMw, profile, s.Inverter)).ToArray() };
        return b with { LoadAdditions = b.LoadAdditions.Append(new QstsLoadAddition(UnitName, bus, mw, profile, s.LoadPowerFactor)).ToArray() };
    }

    /// <summary>The base runs (size 0): one per snapshot, or the one time series.</summary>
    private sealed record BaseRun(IReadOnlyList<QstsResult> Results, IReadOnlyList<HostingViolation> Violations);

    private static BaseRun RunBase(GridModel model, HostingStudy s, IPowerFlowSolver engine, PowerFlowOptions? options)
    {
        var results = new List<QstsResult>();
        foreach (QstsStudy q in StudiesFor(s, -1, 0.0)) results.Add(new QstsEngine(engine, options).Run(model, q));
        if (results.Any(r => !r.Completed))
            throw new InvalidDataException($"hosting-capacity study refused: the base case (no added resource) does not solve: {results.First(r => !r.Completed).FailureReason}.");
        var all = new List<HostingViolation>();
        for (int i = 0; i < results.Count; i++) all.AddRange(Violations(s, results[i], null));
        return new BaseRun(results, all);
    }

    private static HostingLocationResult Locate(GridModel model, HostingStudy s, int bus, IPowerFlowSolver engine, PowerFlowOptions? options, BaseRun baseRun)
    {
        int evaluations = 0;
        List<HostingViolation> Judge(double mw)
        {
            evaluations++;
            var found = new List<HostingViolation>();
            int i = 0;
            foreach (QstsStudy q in StudiesFor(s, bus, mw))
            {
                QstsResult r = new QstsEngine(engine, options).Run(model, q);
                if (!r.Completed)
                {
                    if (s.Judges(HostingConstraint.NonConvergence)) found.Add(new HostingViolation(HostingConstraint.NonConvergence, -1, double.NaN, r.FailureReason ?? "did not converge"));
                }
                else found.AddRange(Violations(s, r, baseRun.Results[i]));
                i++;
            }
            return Order(found);
        }

        List<HostingViolation> atMax = Judge(s.MaxMw);
        if (atMax.Count == 0) return new HostingLocationResult(bus, s.MaxMw, double.PositiveInfinity, Array.Empty<HostingViolation>(), evaluations, baseRun.Violations);
        double lo = 0.0, hi = s.MaxMw;
        List<HostingViolation> atHi = atMax;
        while (hi - lo > s.ToleranceMw)
        {
            double mid = lo + (hi - lo) / 2;
            List<HostingViolation> v = Judge(mid);
            if (v.Count == 0) lo = mid;
            else { hi = mid; atHi = v; }
        }
        return new HostingLocationResult(bus, lo, hi, atHi, evaluations, baseRun.Violations);
    }

    private static List<HostingViolation> Order(List<HostingViolation> v) =>
        v.OrderBy(x => (int)x.Constraint).ThenBy(x => x.Element).ToList();

    /// <summary>The constraints <paramref name="r"/> breaks. With a base run given, a bus / branch / criterion that the
    /// base already breaks counts only when its excess is strictly larger here.</summary>
    private static List<HostingViolation> Violations(HostingStudy s, QstsResult r, QstsResult? baseRun)
    {
        var found = new List<HostingViolation>();
        double allowed = s.Method == HostingMethod.Qsts ? s.AllowedViolationSeconds : 0.0;
        var baseBus = baseRun?.BusViolations.ToDictionary(v => v.Bus) ?? new Dictionary<int, QstsBusViolation>();
        var baseBr = baseRun?.BranchViolations.ToDictionary(v => v.Branch) ?? new Dictionary<int, QstsBranchViolation>();
        foreach (QstsBusViolation v in r.BusViolations)
        {
            baseBus.TryGetValue(v.Bus, out QstsBusViolation? b0);
            if (s.Judges(HostingConstraint.Overvoltage) && v.SecondsAbove > allowed && (b0 is null || v.PuSecondsAbove > b0.PuSecondsAbove))
                found.Add(new HostingViolation(HostingConstraint.Overvoltage, v.Bus, v.MaxPu, $"bus {v.Bus} at {v.MaxPu:F4} pu for {v.SecondsAbove} s"));
            if (s.Judges(HostingConstraint.Undervoltage) && v.SecondsBelow > allowed && (b0 is null || v.PuSecondsBelow > b0.PuSecondsBelow))
                found.Add(new HostingViolation(HostingConstraint.Undervoltage, v.Bus, v.MinPu, $"bus {v.Bus} at {v.MinPu:F4} pu for {v.SecondsBelow} s"));
        }
        if (s.Judges(HostingConstraint.Thermal))
            foreach (QstsBranchViolation v in r.BranchViolations)
            {
                baseBr.TryGetValue(v.Branch, out QstsBranchViolation? b0);
                if (v.SecondsOver > allowed && (b0 is null || v.PercentSecondsOver > b0.PercentSecondsOver))
                    found.Add(new HostingViolation(HostingConstraint.Thermal, v.Branch, v.MaxPercent, $"branch {v.Name} at {v.MaxPercent:F1} % for {v.SecondsOver} s"));
            }
        if (s.Judges(HostingConstraint.ReversePower) && s.ReverseFlowBranch is not null)
        {
            double worst = r.Steps.Min(st => st.MonitoredPMw[0]);
            double seconds = r.Steps.Count(st => st.MonitoredPMw[0] < -s.ReverseFlowAllowanceMw) * (double)r.StepSeconds;
            double baseWorst = baseRun is null ? double.PositiveInfinity : baseRun.Steps.Min(st => st.MonitoredPMw[0]);
            if (seconds > allowed && worst < Math.Min(baseWorst, -s.ReverseFlowAllowanceMw))
                found.Add(new HostingViolation(HostingConstraint.ReversePower, -1, -worst, $"{-worst:F3} MW back through {s.ReverseFlowBranch} for {seconds} s"));
        }
        if (s.Judges(HostingConstraint.TapOperations) && s.TapIncreasePercent is double pct && baseRun is not null)
        {
            int ops = r.TotalTapOperations, b = baseRun.TotalTapOperations;
            if (ops > b + b * pct / 100.0)
                found.Add(new HostingViolation(HostingConstraint.TapOperations, -1, ops, $"{ops} tap operations against {b} in the base (allowance {pct} %)"));
        }
        return found;
    }
}
