using GridSim.Core.Numerics;
using GridSim.Core.Stochastic;

namespace GridSim.Core.Analysis.Reliability.Network;

/// <summary>
/// RC4 R4-13 sequential-free Monte Carlo of the same first-order event model the enumeration evaluates: in each simulated
/// year every component draws its number of failures (Poisson, inversion of one uniform) and, for each failure, a repair
/// time (exponential with the stated mean, or the mean itself); the cached event plan says which load points that failure
/// interrupts and whether each waits the repair or the switching time. Its expectation is therefore the enumeration's
/// figure; the standard error says how close a finite run is.
/// <para>
/// Determinism: every draw is a pure function of (seed, domain, component, year, draw) through the R4-02 counter streams;
/// years are cut into the fixed blocks of <see cref="FixedOrderReduction"/>, each block summed left to right and the block
/// sums combined by the fixed tree, so one thread and sixteen give the same bits.
/// </para>
/// </summary>
internal static class NetworkMonteCarloRunner
{
    public const long MaxYears = 2_000_000;

    public static (IReadOnlyList<LoadPointReliability> Points, NetworkMonteCarloSummary Summary, IReadOnlyList<ReliabilityIndex> Indices, double Customers)
        Run(PreparedNetworkStudy p, NetworkMonteCarlo mc)
    {
        if (mc.Seed is null)
            throw new ArgumentException("No seed given: there is no default seed, because a silent default would make the result unrepeatable from its inputs.");
        if (mc.Years < 2 || mc.Years > MaxYears) throw new ArgumentException($"years must be between 2 and {MaxYears}.");
        if (mc.RepairDistribution is not ("exponential" or "deterministic"))
            throw new ArgumentException("repairDistribution must be 'exponential' or 'deterministic'.");
        if (string.IsNullOrWhiteSpace(mc.Domain)) throw new ArgumentException("domain must not be empty.");
        int years = (int)mc.Years;
        int nc = p.Components.Count, nl = p.LoadPoints.Count;
        bool exponential = mc.RepairDistribution == "exponential";

        var lambda = new double[nc];
        var repair = new double[nc];
        var switching = new double[nc];
        var interrupted = new (int Load, bool Repair)[nc][];
        for (int ci = 0; ci < nc; ci++)
        {
            FailureComponent c = p.Components[ci];
            lambda[ci] = c.FailuresPerYear ?? throw new InvalidOperationException("Rate data must be complete before sampling.");
            repair[ci] = c.RepairHours ?? 0.0;
            switching[ci] = c.SwitchingHours ?? p.DefaultSwitchingHours ?? 0.0;
            var list = new List<(int, bool)>();
            for (int j = 0; j < nl; j++)
                if (p.Plans[ci].PerLoad[j] is { } lr) list.Add((j, lr.Class == RestorationClass.Repair));
            interrupted[ci] = list.ToArray();
        }
        double[] customers = p.LoadPoints.Select(l => (double)l.Customers).ToArray();
        double totalCustomers = 0;
        foreach (double x in customers) totalCustomers += x;

        var saifiY = new double[years];
        var saidiY = new double[years];
        var ensY = new double[years];
        int blocks = (years + FixedOrderReduction.BlockSize - 1) / FixedOrderReduction.BlockSize;
        var countBlock = new double[nl][];
        var hoursBlock = new double[nl][];
        for (int j = 0; j < nl; j++) { countBlock[j] = new double[blocks]; hoursBlock[j] = new double[blocks]; }
        ulong seed = mc.Seed.Value;
        string countDomain = mc.Domain + "/count/";
        string repairDomain = mc.Domain + "/repair/";

        FixedOrderReduction.ForBlocks(years, mc.Threads, (lo, hi) =>
        {
            var cnt = new double[nl];
            var hrs = new double[nl];
            var blockCount = new double[nl];
            var blockHours = new double[nl];
            for (int y = lo; y < hi; y++)
            {
                Array.Clear(cnt); Array.Clear(hrs);
                for (int ci = 0; ci < nc; ci++)
                {
                    if (lambda[ci] <= 0 || interrupted[ci].Length == 0) continue;
                    double u = new CounterStream(new StreamKey(seed, countDomain + ci, (ulong)y)).Uniform(0);
                    int n = Poisson(lambda[ci], u);
                    if (n == 0) continue;
                    var rs = new CounterStream(new StreamKey(seed, repairDomain + ci, (ulong)y));
                    for (int i = 0; i < n; i++)
                    {
                        double r = exponential ? -repair[ci] * DetMath.Log(1.0 - rs.Uniform((ulong)i)) : repair[ci];
                        foreach ((int load, bool waitsRepair) in interrupted[ci])
                        {
                            cnt[load] += 1.0;
                            hrs[load] += waitsRepair ? r : switching[ci];
                        }
                    }
                }
                double sf = 0, sd = 0, en = 0;
                for (int j = 0; j < nl; j++)
                {
                    sf += cnt[j] * customers[j];
                    sd += hrs[j] * customers[j];
                    en += p.LoadPoints[j].AverageLoadMw * hrs[j];
                    blockCount[j] += cnt[j];
                    blockHours[j] += hrs[j];
                }
                saifiY[y] = sf / totalCustomers;
                saidiY[y] = sd / totalCustomers;
                ensY[y] = en;
            }
            int b = lo / FixedOrderReduction.BlockSize;
            for (int j = 0; j < nl; j++) { countBlock[j][b] = blockCount[j]; hoursBlock[j][b] = blockHours[j]; }
        });

        var points = new List<LoadPointReliability>(nl);
        var digest = new List<double>();
        for (int j = 0; j < nl; j++)
        {
            ResolvedLoadPoint lp = p.LoadPoints[j];
            double lam = FixedOrderReduction.TreeSum(countBlock[j]) / years;
            double un = FixedOrderReduction.TreeSum(hoursBlock[j]) / years;
            double? perInterruption = lam > 0 ? un / lam : null;
            points.Add(new LoadPointReliability(lp.Id, lp.Customers, lp.AverageLoadMw, lam, un, perInterruption, lp.AverageLoadMw * un, []));
            digest.Add(lam); digest.Add(un);
        }
        IReadOnlyList<ReliabilityIndex> indices = CustomerIndices.System(points, p.HoursPerYear, out double total);
        double seSaifi = StandardError(saifiY, mc.Threads), seSaidi = StandardError(saidiY, mc.Threads), seEns = StandardError(ensY, mc.Threads);
        var errors = new List<IndexStandardError>
        {
            new("SAIFI", seSaifi), new("SAIDI", seSaidi), new("CAIDI", null),
            new("ASAI", seSaidi / p.HoursPerYear), new("ENS", seEns), new("AENS", seEns / total),
        };
        foreach (ReliabilityIndex i in indices) if (i.Value is double v) digest.Add(v);
        StochasticProvenance prov = StochasticProvenance.Create(seed, [countDomain + "*", repairDomain + "*"], SamplingMethod.MonteCarlo, years, digest);
        return (points, new NetworkMonteCarloSummary(seed, years, mc.RepairDistribution, prov.GeneratorVersion, prov.Domains, prov.ResultDigest, errors),
            indices, total);
    }

    /// <summary>Poisson count by inversion of the cumulative distribution at <paramref name="u"/> in [0, 1).</summary>
    internal static int Poisson(double lambda, double u)
    {
        double p = DetMath.Exp(-lambda), cum = p;
        int n = 0;
        while (u >= cum && n < 10_000)
        {
            n++;
            p *= lambda / n;
            if (p == 0.0) break;
            cum += p;
        }
        return n;
    }

    /// <summary>Standard error of the mean of per-year values: sqrt(sum (x - mean)^2 / (N - 1)) / sqrt(N), fixed-order sums.</summary>
    private static double StandardError(double[] values, int threads)
    {
        double mean = FixedOrderReduction.Sum(values, threads) / values.Length;
        double ss = FixedOrderReduction.Sum(values.Length, i => (values[i] - mean) * (values[i] - mean), threads);
        return Math.Sqrt(ss / (values.Length - 1.0)) / Math.Sqrt(values.Length);
    }
}
