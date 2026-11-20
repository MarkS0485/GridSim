using GridSim.Core.Diagnostics;
using GridSim.Core.Numerics;
using GridSim.Core.Stochastic;

namespace GridSim.Core.Analysis.Probabilistic;

/// <summary>A generating unit: an identifier and a discrete law of its available capacity (MW).</summary>
public sealed record AdequacyUnit(string Id, UncertainDistribution Capacity)
{
    /// <summary>A two-state unit: rated capacity with the given availability (1 minus the forced outage rate).</summary>
    public static AdequacyUnit TwoState(string id, double capacityMw, double availability) =>
        new(id, new TwoStateDistribution(capacityMw, availability));
}

/// <summary>A generation adequacy request. <paramref name="HourlyLoadMw"/>: one load per hour of the study period.</summary>
public sealed record AdequacyRequest(
    IReadOnlyList<AdequacyUnit> Units, IReadOnlyList<double> HourlyLoadMw, ProbabilisticMethod Method,
    int SamplesPerReplicate, int? Replicates, ulong? Seed, string Domain = "adequacy", double? StepMw = null, int MaxThreads = 0);

/// <summary>
/// The adequacy indices over the period. <c>Lole</c> is in HOURS per period (the sum over hours of the loss-of-load
/// probability), <c>Eens</c> in MWh per period, <c>Lolp</c> the period average of the hourly probability
/// (<c>Lole / hours</c>). Standard errors are the method's (null where the method has none, see <see cref="ErrorModel"/>).
/// </summary>
public sealed record AdequacyResult(
    string Status, string? Reason, string Method, int Hours, double InstalledMw,
    double Lole, double? LoleStdError, double Lolp, double? LolpStdError, double Eens, double? EensStdError,
    string ErrorModel, double? CapacityStepMw, ProbabilisticProvenance? Provenance);

/// <summary>
/// Static generation adequacy (loss of load by capacity shortfall against an hourly load, no transmission, no chronology).
/// Three methods on one model. <b>Convolution</b>: the exact capacity outage probability table
/// (<see cref="CapacityOutageTable"/>), then per hour <c>LOLP_h = P(capacity &lt; load_h)</c> and the expected shortfall,
/// summed over the hours. <b>Monte Carlo / QMC</b>: each sample draws every unit's state once (one uniform per unit) and
/// evaluates ALL hours against that capacity, so the sample mean converges to the same sum over hours; the loss hours and
/// the shortfall energy of a sample come from the hourly loads sorted once, with suffix sums.
/// </summary>
public static class GenerationAdequacy
{
    /// <summary>Run the study; a method outside its domain returns OUT OF SCOPE, a bad request throws a domain refusal.</summary>
    public static AdequacyResult Run(AdequacyRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (!req.Seed.HasValue)
            throw DomainRefusal.Mark(new ArgumentException("No seed given: there is no default seed, because a silent default would make the result unrepeatable from its inputs."));
        if (req.Units is null || req.Units.Count == 0) throw DomainRefusal.Mark(new ArgumentException("At least one unit is needed."));
        if (req.HourlyLoadMw is null || req.HourlyLoadMw.Count == 0) throw DomainRefusal.Mark(new ArgumentException("At least one hourly load is needed."));
        foreach (double l in req.HourlyLoadMw)
            if (!double.IsFinite(l) || l < 0.0) throw DomainRefusal.Mark(new ArgumentException("Every hourly load must be finite and not negative."));
        foreach (AdequacyUnit u in req.Units)
            if (!u.Capacity.IsDiscrete) throw DomainRefusal.Mark(new ArgumentException($"Unit '{u.Id}': the capacity law must be discrete."));
        var laws = req.Units.Select(u => u.Capacity).ToArray();
        double installed = 0.0;
        foreach (UncertainDistribution d in laws) installed += d.Atoms[^1].Value;
        int hours = req.HourlyLoadMw.Count;
        string name = ProbabilisticMethods.Name(req.Method);

        if (req.Method == ProbabilisticMethod.Convolution) return Convolution(req, laws, installed, hours, name);

        SampleLayout layout = SampleLayout.Create(req.Method, req.SamplesPerReplicate, req.Replicates);
        double[] sorted = req.HourlyLoadMw.ToArray();
        FrozenIntroSort.Sort(sorted, 0, sorted.Length, (a, b) => a.CompareTo(b));
        var suffix = new double[hours + 1];
        for (int i = hours - 1; i >= 0; i--) suffix[i] = suffix[i + 1] + sorted[i];

        var sources = new UniformSource[layout.Replicates];
        for (int r = 0; r < layout.Replicates; r++) sources[r] = new UniformSource(req.Method, laws.Length, req.Seed.Value, req.Domain, r);
        int total = (int)layout.Total;
        var lossHours = new double[total];
        var eens = new double[total];
        var capacity = new double[total];
        FixedOrderReduction.ForBlocks(total, req.MaxThreads, (lo, hi) =>
        {
            for (int i = lo; i < hi; i++)
            {
                UniformSource s = sources[layout.ReplicateOf(i)];
                int point = layout.PointOf(i);
                double cap = 0.0;
                for (int d = 0; d < laws.Length; d++) cap += laws[d].Quantile(s.U(point, d));
                capacity[i] = cap;
                // First sorted index whose load exceeds the capacity: those hours are short (strict, as in the table).
                int a = 0, b = hours;
                while (a < b) { int mid = (a + b) >> 1; if (sorted[mid] > cap) b = mid; else a = mid + 1; }
                int short_ = hours - a;
                lossHours[i] = short_;
                eens[i] = short_ == 0 ? 0.0 : suffix[a] - short_ * cap;
            }
        });
        (double lole, double? loleSe, _) = SampleStatistics.Estimate(lossHours, layout, req.Method, req.MaxThreads);
        (double e, double? eSe, _) = SampleStatistics.Estimate(eens, layout, req.Method, req.MaxThreads);
        double h = hours;
        var results = new[] { lole, loleSe ?? double.NaN, lole / h, e, eSe ?? double.NaN };
        ProbabilisticProvenance prov = ProbabilisticProvenance.ForSampled(req.Seed, req.Domain, req.Method, layout, capacity, results);
        return new AdequacyResult(ProbabilisticStatus.Complete, null, name, hours, installed, lole, loleSe, lole / h,
            loleSe.HasValue ? loleSe / h : null, e, eSe, ErrorModels.For(req.Method), null, prov);
    }

    private static AdequacyResult Convolution(AdequacyRequest req, UncertainDistribution[] laws, double installed, int hours, string name)
    {
        CapacityOutageTable table;
        try { table = CapacityOutageTable.Build(laws, req.StepMw); }
        catch (InvalidOperationException ex) when (DomainRefusal.IsMarked(ex))
        {
            return new AdequacyResult(ProbabilisticStatus.OutOfScope, ex.Message, name, hours, installed, double.NaN, null, double.NaN, null,
                double.NaN, null, ErrorModels.Convolution, null, null);
        }
        double[] loads = req.HourlyLoadMw.ToArray();
        double lole = FixedOrderReduction.Sum(hours, i => table.LossOfLoadProbability(loads[i]), req.MaxThreads);
        double eens = FixedOrderReduction.Sum(hours, i => table.ExpectedShortfallMw(loads[i]), req.MaxThreads);
        ProbabilisticProvenance prov = ProbabilisticProvenance.ForConvolution(req.Seed, req.Domain, new[] { lole, lole / hours, eens });
        return new AdequacyResult(ProbabilisticStatus.Complete, null, name, hours, installed, lole, null, lole / hours, null, eens, null,
            ErrorModels.Convolution + " (exact on the capacity lattice)", table.StepMw, prov);
    }
}
