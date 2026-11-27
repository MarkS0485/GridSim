using System.Globalization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Stochastic;

namespace GridSim.Core.Analysis.Probabilistic;

/// <summary>What an uncertain input drives.</summary>
public enum UncertainTarget
{
    /// <summary>The real power demand Pd of a bus (MW).</summary>
    BusLoad,
    /// <summary>The real power output Pg of an in-service generator (MW).</summary>
    GeneratorPg,
}

/// <summary>The power-flow evaluator of a probabilistic load flow.</summary>
public enum PlfEvaluator
{
    /// <summary>DC power flow (linear in the injections).</summary>
    Dc,
    /// <summary>Full AC Newton power flow (nonlinear; sampled methods only).</summary>
    Ac,
}

/// <summary>
/// One uncertain input, by stable identity: a bus id (load), or a bus id plus the zero-based position among the
/// in-service generators at that bus in model order (generator). <paramref name="Scale"/>: the law is a multiplier of the
/// case's value (1.0 = the case value); otherwise it is the value itself in MW.
/// </summary>
public sealed record UncertainInput(string Id, UncertainTarget Target, int Bus, int Unit, UncertainDistribution Distribution, bool Scale = false);

/// <summary>A correlation between two inputs (positions in the input list), as the Gaussian-copula correlation.</summary>
public sealed record InputCorrelation(int A, int B, double Rho);

/// <summary>A probabilistic load flow request.</summary>
/// <param name="Quantiles">Probabilities of the reported quantiles (type 7 estimator).</param>
/// <param name="LoadingLimitPercent">A branch exceeds when its loading (|flow| or MVA over RateA) is above this percentage.</param>
/// <param name="MonitorBranches">Branch indices to report (null: every branch).</param>
/// <param name="MonitorBuses">Bus ids to report voltages for (null: every bus; AC only).</param>
/// <param name="CellsPerSd">Convolution lattice cells per standard deviation of the monitored flow (default 100).</param>
/// <param name="ConvolutionStepMw">An explicit convolution lattice step, overriding CellsPerSd (use for exact discrete cases).</param>
public sealed record PlfRequest(
    IReadOnlyList<UncertainInput> Inputs, IReadOnlyList<InputCorrelation>? Correlation, PlfEvaluator Evaluator,
    ProbabilisticMethod Method, int SamplesPerReplicate, int? Replicates, ulong? Seed, string Domain = "plf",
    IReadOnlyList<double>? Quantiles = null, double LoadingLimitPercent = 100.0, double VminPu = 0.95, double VmaxPu = 1.05,
    IReadOnlyList<int>? MonitorBranches = null, IReadOnlyList<int>? MonitorBuses = null,
    int CellsPerSd = 100, double? ConvolutionStepMw = null, bool QFollowsP = true, int MaxThreads = 0);

/// <summary>Statistics of one monitored branch flow (MW at the from end; DC flow or AC real power at the from end).</summary>
public sealed record BranchStatistics(
    int Index, int From, int To, string Name, double RateMva, double Mean, double? MeanStdError, double StdDev,
    IReadOnlyList<double> QuantileValues, double? ExceedProbability, double? ExceedStdError, double? LatticeStepMw);

/// <summary>Statistics of one monitored bus voltage magnitude (pu); AC only.</summary>
public sealed record BusVoltageStatistics(
    int BusId, double Mean, double? MeanStdError, double StdDev, IReadOnlyList<double> QuantileValues,
    double BelowMinProbability, double AboveMaxProbability);

/// <summary>A sample that did not converge, kept as a row (never dropped).</summary>
public sealed record FailedSample(int Index, int Replicate, string Status, IReadOnlyList<double> InputValues);

/// <summary>The probabilistic load flow result.</summary>
public sealed record PlfResult(
    string Status, string? Reason, string Method, string Evaluator, long Samples, long Converged, long NonConverged,
    IReadOnlyList<double> QuantileProbabilities, string ErrorModel,
    IReadOnlyList<BranchStatistics> Branches, IReadOnlyList<BusVoltageStatistics> Buses,
    IReadOnlyList<FailedSample> FailedSamples, ProbabilisticProvenance? Provenance);

/// <summary>
/// Probabilistic load flow over uncertain loads and generation. Defined order: validate, derive streams, draw every
/// sample in index order, transform to the input space (Cholesky in fixed row order), evaluate each sample independently
/// on its own model copy, collect in index order, reduce by the fixed tree, quantiles by the type 7 estimator on the
/// frozen-sorted sample. Samples that fail to converge keep their row with a status (<see cref="PlfResult.FailedSamples"/>);
/// every statistic, the probabilities included, is over the converged samples only, so it is conditional on convergence
/// (ANA-20: a failed sample used to stay in the denominator of every probability as "no violation", and the failed draws
/// are the heaviest, so the probabilities were biased low). A run with any failed sample is INCOMPLETE, with the failed
/// count in its reason; one where every sample converged is COMPLETE, with the same numbers as before.
///
/// <para><b>Convolution</b> applies only where the quantity is a sum of independent terms: a DC flow with independent
/// inputs. An AC evaluator or correlated inputs make it OUT OF SCOPE, with the reason; nothing is approximated.</para>
/// </summary>
public static class ProbabilisticLoadFlow
{
    /// <summary>Cap on stored sample values (samples x monitored quantities), refusal above.</summary>
    public const long MaxStoredValues = 40_000_000;

    private sealed record Resolved(UncertainInput Input, int BusIndex, int GenIndex, double BaseValue);

    /// <summary>Run the study.</summary>
    public static PlfResult Run(GridModel model, PlfRequest req)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(req);
        if (!req.Seed.HasValue)
            throw DomainRefusal.Mark(new ArgumentException("No seed given: there is no default seed, because a silent default would make the result unrepeatable from its inputs."));
        if (req.Inputs is null || req.Inputs.Count == 0) throw DomainRefusal.Mark(new ArgumentException("At least one uncertain input is needed."));
        double[] qp = (req.Quantiles ?? new[] { 0.05, 0.5, 0.95 }).ToArray();
        foreach (double q in qp)
            if (!(q >= 0.0 && q <= 1.0)) throw DomainRefusal.Mark(new ArgumentException("Quantile probabilities must lie in [0, 1]."));
        if (!(req.LoadingLimitPercent > 0.0) || !double.IsFinite(req.LoadingLimitPercent))
            throw DomainRefusal.Mark(new ArgumentException("The loading limit must be a finite percentage above 0."));
        if (!(req.VminPu < req.VmaxPu) || !double.IsFinite(req.VminPu) || !double.IsFinite(req.VmaxPu))
            throw DomainRefusal.Mark(new ArgumentException("The voltage band must be finite with minPu below maxPu."));
        Resolved[] inputs = Resolve(model, req.Inputs);
        double[,]? corr = BuildCorrelation(req.Correlation, inputs.Length);
        var transform = new InputTransform(inputs.Select(i => i.Input.Distribution).ToArray(), corr);
        int[] branches = MonitoredBranches(model, req.MonitorBranches);
        int[] buses = req.Evaluator == PlfEvaluator.Ac ? MonitoredBuses(model, req.MonitorBuses) : Array.Empty<int>();
        string evaluator = req.Evaluator == PlfEvaluator.Dc ? "dc" : "ac";
        string method = ProbabilisticMethods.Name(req.Method);

        if (req.Method == ProbabilisticMethod.Convolution)
        {
            if (req.Evaluator != PlfEvaluator.Dc)
                return OutOfScope(method, evaluator, "Convolution needs a quantity that is a sum of independent terms: the AC power flow is nonlinear in the inputs, so it is out of scope. Use monte-carlo, qmc or qmc-shifted.");
            if (transform.Correlated)
                return OutOfScope(method, evaluator, "Convolution needs independent terms: correlated inputs are out of scope. Use monte-carlo, qmc or qmc-shifted.");
            return Convolve(model, req, inputs, branches, qp, method, evaluator);
        }
        return Sample(model, req, inputs, transform, branches, buses, qp, method, evaluator);
    }

    private static PlfResult OutOfScope(string method, string evaluator, string reason) =>
        new(ProbabilisticStatus.OutOfScope, reason, method, evaluator, 0, 0, 0, Array.Empty<double>(), ErrorModels.For(ProbabilisticMethods.Parse(method)!.Value),
            Array.Empty<BranchStatistics>(), Array.Empty<BusVoltageStatistics>(), Array.Empty<FailedSample>(), null);

    // ---------------------------------------------------------------------------------------------------------------
    // Resolution of identities
    // ---------------------------------------------------------------------------------------------------------------

    private static Resolved[] Resolve(GridModel model, IReadOnlyList<UncertainInput> inputs)
    {
        var seen = new HashSet<(UncertainTarget, int, int)>();
        var list = new Resolved[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
        {
            UncertainInput u = inputs[i];
            if (u is null || u.Distribution is null) throw DomainRefusal.Mark(new ArgumentException($"Input {i} is missing its distribution."));
            int key2 = u.Target == UncertainTarget.GeneratorPg ? u.Unit : 0;
            if (!seen.Add((u.Target, u.Bus, key2)))
                throw DomainRefusal.Mark(new ArgumentException($"Input '{u.Id}' names a load or generator that another input already drives."));
            int bi = -1;
            for (int b = 0; b < model.Buses.Count; b++) if (model.Buses[b].Id == u.Bus) { bi = b; break; }
            if (bi < 0) throw DomainRefusal.Mark(new ArgumentException($"Input '{u.Id}': bus {u.Bus} is not in the case."));
            if (u.Target == UncertainTarget.BusLoad)
                list[i] = new Resolved(u, bi, -1, model.Buses[bi].Pd);
            else
            {
                int count = 0, gi = -1;
                for (int g = 0; g < model.Generators.Count; g++)
                    if (model.Generators[g].Bus == u.Bus) { if (count == u.Unit) { gi = g; break; } count++; }
                if (gi < 0) throw DomainRefusal.Mark(new ArgumentException($"Input '{u.Id}': bus {u.Bus} has no in-service generator at position {u.Unit}."));
                list[i] = new Resolved(u, bi, gi, model.Generators[gi].Pg);
            }
        }
        return list;
    }

    private static double[,]? BuildCorrelation(IReadOnlyList<InputCorrelation>? entries, int n)
    {
        if (entries is null || entries.Count == 0) return null;
        var m = new double[n, n];
        for (int i = 0; i < n; i++) m[i, i] = 1.0;
        var seen = new HashSet<(int, int)>();
        foreach (InputCorrelation c in entries)
        {
            if (c.A < 0 || c.A >= n || c.B < 0 || c.B >= n || c.A == c.B)
                throw DomainRefusal.Mark(new ArgumentException("A correlation must name two different inputs by position."));
            if (!double.IsFinite(c.Rho) || Math.Abs(c.Rho) > 1.0)
                throw DomainRefusal.Mark(new ArgumentException("A correlation must be a finite number in [-1, 1]."));
            if (!seen.Add((Math.Min(c.A, c.B), Math.Max(c.A, c.B))))
                throw DomainRefusal.Mark(new ArgumentException("A pair of inputs is given two correlations."));
            m[c.A, c.B] = c.Rho; m[c.B, c.A] = c.Rho;
        }
        return m;
    }

    private static int[] MonitoredBranches(GridModel model, IReadOnlyList<int>? list)
    {
        if (list is null) return Enumerable.Range(0, model.Branches.Count).ToArray();
        foreach (int i in list)
            if (i < 0 || i >= model.Branches.Count) throw DomainRefusal.Mark(new ArgumentException($"Monitored branch {i} is not in the case."));
        return list.Distinct().OrderBy(x => x).ToArray();
    }

    private static int[] MonitoredBuses(GridModel model, IReadOnlyList<int>? list)
    {
        if (list is null) return Enumerable.Range(0, model.Buses.Count).ToArray();
        var idx = new List<int>();
        foreach (int id in list.Distinct())
        {
            int bi = -1;
            for (int b = 0; b < model.Buses.Count; b++) if (model.Buses[b].Id == id) { bi = b; break; }
            if (bi < 0) throw DomainRefusal.Mark(new ArgumentException($"Monitored bus {id} is not in the case."));
            idx.Add(bi);
        }
        idx.Sort();
        return idx.ToArray();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Sampled methods
    // ---------------------------------------------------------------------------------------------------------------

    private static PlfResult Sample(GridModel model, PlfRequest req, Resolved[] inputs, InputTransform transform, int[] branches, int[] buses,
        double[] qp, string method, string evaluator)
    {
        SampleLayout layout = SampleLayout.Create(req.Method, req.SamplesPerReplicate, req.Replicates);
        int total = (int)layout.Total;
        int k = inputs.Length;
        int nb = branches.Length, nv = buses.Length;
        long quantities = nb + nv;
        if (total * quantities > MaxStoredValues)
            throw DomainRefusal.Mark(new ArgumentException(
                $"{total} samples of {quantities} monitored quantities exceed the {MaxStoredValues} stored-value limit: lower the samples or monitor fewer branches and buses."));

        var sources = new UniformSource[layout.Replicates];
        for (int r = 0; r < layout.Replicates; r++) sources[r] = new UniformSource(req.Method, k, req.Seed!.Value, req.Domain, r);

        var x = new double[(long)total * k];
        var flow = new double[nb * (long)total];     // [b * total + i]
        var volt = new double[nv * (long)total];     // [v * total + i]
        var exceed = new byte[nb * (long)total];
        var vlow = new byte[nv * (long)total];
        var vhigh = new byte[nv * (long)total];
        var status = new string?[total];             // null = converged

        DistributionFactors? df = null;
        Dictionary<int, double>? baseNet = null;
        if (req.Evaluator == PlfEvaluator.Dc)
        {
            df = DistributionFactors.Build(model);
            baseNet = new Dictionary<int, double>(model.Buses.Count);
            foreach (Bus b in model.Buses) baseNet[b.Id] = baseNet.GetValueOrDefault(b.Id) - b.Pd;
            foreach (Generator g in model.Generators) if (baseNet.ContainsKey(g.Bus)) baseNet[g.Bus] += g.Pg;
        }
        double loadingLimit = req.LoadingLimitPercent / 100.0;

        FixedOrderReduction.ForBlocks(total, req.MaxThreads, (lo, hi) =>
        {
            var u = new double[k];
            var xs = new double[k];
            for (int i = lo; i < hi; i++)
            {
                UniformSource s = sources[layout.ReplicateOf(i)];
                int point = layout.PointOf(i);
                for (int d = 0; d < k; d++) u[d] = s.U(point, d);
                transform.Transform(u, xs);
                var values = new double[k];
                for (int d = 0; d < k; d++)
                {
                    values[d] = inputs[d].Input.Scale ? inputs[d].BaseValue * xs[d] : xs[d];
                    x[(long)i * k + d] = values[d];
                }
                if (req.Evaluator == PlfEvaluator.Dc)
                {
                    var inj = new Dictionary<int, double>(baseNet!);
                    for (int d = 0; d < k; d++)
                    {
                        double delta = values[d] - inputs[d].BaseValue;
                        int busId = inputs[d].Input.Bus;
                        if (!inj.ContainsKey(busId)) continue;
                        inj[busId] += inputs[d].Input.Target == UncertainTarget.BusLoad ? -delta : delta;
                    }
                    double[] f = df!.DcFlows(inj, model.BaseMva);
                    for (int b = 0; b < nb; b++)
                    {
                        int bi = branches[b];
                        double fv = f[bi];
                        flow[b * (long)total + i] = fv;
                        double rate = model.Branches[bi].RateAMva;
                        exceed[b * (long)total + i] = (byte)(rate > 0 && Math.Abs(fv) > loadingLimit * rate ? 1 : 0);
                    }
                }
                else
                {
                    GridModel copy = AcCopy(model, inputs, values, req.QFollowsP);
                    PowerFlowResult? pf = null;
                    string? st = null;
                    try { pf = ContingencyAnalysis.CreateStudySolver(copy).Solve(copy, ContingencyAnalysis.StudyOptions); }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DivideByZeroException)
                    { st = "SolverError: " + ex.Message; }
                    if (st is null && pf is not null && !(pf.Converged && AllFinite(pf))) st = pf.Converged ? "NonFinite" : "NotConverged:" + pf.Status;
                    if (st is not null || pf is null)
                    {
                        status[i] = st ?? "NotConverged";
                        for (int b = 0; b < nb; b++) flow[b * (long)total + i] = double.NaN;
                        for (int v = 0; v < nv; v++) volt[v * (long)total + i] = double.NaN;
                        continue;
                    }
                    for (int b = 0; b < nb; b++)
                    {
                        LineFlow lf = pf.LineFlows[branches[b]];
                        flow[b * (long)total + i] = lf.PFromMw;
                        exceed[b * (long)total + i] = (byte)(lf.RatingMva > 0 && lf.LoadingPercent > req.LoadingLimitPercent ? 1 : 0);
                    }
                    for (int v = 0; v < nv; v++)
                    {
                        double vm = pf.Vm[buses[v]];
                        volt[v * (long)total + i] = vm;
                        vlow[v * (long)total + i] = (byte)(vm < req.VminPu ? 1 : 0);
                        vhigh[v * (long)total + i] = (byte)(vm > req.VmaxPu ? 1 : 0);
                    }
                }
            }
        });

        int[] ok = Enumerable.Range(0, total).Where(i => status[i] is null).ToArray();
        var failed = new List<FailedSample>();
        for (int i = 0; i < total; i++)
            if (status[i] is not null)
            {
                var iv = new double[k];
                Array.Copy(x, (long)i * k, iv, 0, k);
                failed.Add(new FailedSample(i, layout.ReplicateOf(i), status[i]!, iv));
            }
        string errorModel = ErrorModels.For(req.Method);
        if (ok.Length == 0)
        {
            ProbabilisticProvenance none = ProbabilisticProvenance.ForSampled(req.Seed, req.Domain, req.Method, layout, x, new double[] { failed.Count });
            return new PlfResult(ProbabilisticStatus.Incomplete, "No sample converged.", method, evaluator, total, 0, failed.Count, qp, errorModel,
                Array.Empty<BranchStatistics>(), Array.Empty<BusVoltageStatistics>(), failed, none);
        }
        var digestValues = new List<double>();
        var branchRows = new List<BranchStatistics>(nb);
        for (int b = 0; b < nb; b++)
        {
            Branch br = model.Branches[branches[b]];
            double[] vals = ok.Select(i => flow[b * (long)total + i]).ToArray();
            (double mean, double? mse, double sd, double[] quant) = Summarise(vals, ok, layout, req.Method, qp, req.MaxThreads);
            double? ex = null, exSe = null;
            if (br.RateAMva > 0)
            {
                (ex, exSe) = Proportion(exceed, b * (long)total, ok, layout, req.Method);
            }
            branchRows.Add(new BranchStatistics(branches[b], br.From, br.To, br.Name ?? "", br.RateAMva, mean, mse, sd, quant, ex, exSe, null));
            AddDigest(digestValues, mean, mse, sd, quant, ex);
        }
        var busRows = new List<BusVoltageStatistics>(nv);
        for (int v = 0; v < nv; v++)
        {
            double[] vals = ok.Select(i => volt[v * (long)total + i]).ToArray();
            (double mean, double? mse, double sd, double[] quant) = Summarise(vals, ok, layout, req.Method, qp, req.MaxThreads);
            (double? lowP, _) = Proportion(vlow, v * (long)total, ok, layout, req.Method);
            (double? highP, _) = Proportion(vhigh, v * (long)total, ok, layout, req.Method);
            busRows.Add(new BusVoltageStatistics(model.Buses[buses[v]].Id, mean, mse, sd, quant, lowP ?? double.NaN, highP ?? double.NaN));
            AddDigest(digestValues, mean, mse, sd, quant, lowP);
            digestValues.Add(highP ?? double.NaN);
        }
        digestValues.Add(failed.Count);
        ProbabilisticProvenance prov = ProbabilisticProvenance.ForSampled(req.Seed, req.Domain, req.Method, layout, x, digestValues);
        // ANA-20: a run with failed samples is not complete; its statistics are conditional on convergence.
        string plfStatus = failed.Count == 0 ? ProbabilisticStatus.Complete : ProbabilisticStatus.Incomplete;
        string? reason = failed.Count == 0 ? null : string.Create(CultureInfo.InvariantCulture,
            $"{failed.Count} of {total} samples did not converge; every statistic and probability is over the {ok.Length} converged samples (conditional on convergence).");
        return new PlfResult(plfStatus, reason, method, evaluator, total, ok.Length, failed.Count, qp, errorModel,
            branchRows, busRows, failed, prov);
    }

    private static void AddDigest(List<double> d, double mean, double? mse, double sd, double[] quant, double? exceed)
    {
        d.Add(mean); d.Add(mse ?? double.NaN); d.Add(sd); d.AddRange(quant); d.Add(exceed ?? double.NaN);
    }

    private static bool AllFinite(PowerFlowResult pf)
    {
        foreach (double v in pf.Vm) if (!double.IsFinite(v)) return false;
        foreach (LineFlow lf in pf.LineFlows) if (!double.IsFinite(lf.PFromMw) || !double.IsFinite(lf.LoadingPercent)) return false;
        return true;
    }

    private static GridModel AcCopy(GridModel model, Resolved[] inputs, double[] values, bool qFollowsP)
    {
        var buses = model.Buses.ToList();
        var gens = model.Generators.ToList();
        for (int d = 0; d < inputs.Length; d++)
        {
            if (inputs[d].Input.Target == UncertainTarget.BusLoad)
            {
                Bus b = buses[inputs[d].BusIndex];
                double qd = qFollowsP && b.Pd != 0.0 ? b.Qd * values[d] / b.Pd : b.Qd;
                buses[inputs[d].BusIndex] = b with { Pd = values[d], Qd = qd };
            }
            else gens[inputs[d].GenIndex] = gens[inputs[d].GenIndex] with { Pg = values[d] };
        }
        return model.CopyWith(buses: buses, generators: gens);
    }

    // Mean, its standard error, standard deviation and quantiles of the converged values.
    private static (double Mean, double? MeanSe, double Sd, double[] Quantiles) Summarise(double[] vals, int[] okIndex, SampleLayout layout,
        ProbabilisticMethod method, double[] qp, int maxThreads)
    {
        double mean = SampleStatistics.Mean(vals, maxThreads);
        double sd = Math.Sqrt(SampleStatistics.Variance(vals, mean, maxThreads));
        double? se = null;
        if (method == ProbabilisticMethod.MonteCarlo) se = vals.Length > 1 ? sd / Math.Sqrt(vals.Length) : null;
        else if (method == ProbabilisticMethod.QuasiMonteCarloShifted)
        {
            var reps = new List<double>();
            for (int r = 0; r < layout.Replicates; r++)
            {
                var slice = new List<double>();
                for (int j = 0; j < okIndex.Length; j++) if (layout.ReplicateOf(okIndex[j]) == r) slice.Add(vals[j]);
                if (slice.Count == 0) { reps.Clear(); break; }
                reps.Add(SampleStatistics.Mean(slice.ToArray(), 1));
            }
            if (reps.Count >= 2)
            {
                double rm = SampleStatistics.Mean(reps.ToArray(), 1);
                se = Math.Sqrt(SampleStatistics.Variance(reps.ToArray(), rm, 1) / reps.Count);
            }
        }
        double[] sorted = SampleStatistics.SortedCopy(vals);
        double[] quant = qp.Select(p => SampleStatistics.QuantileType7(sorted, p)).ToArray();
        return (mean, se, sd, quant);
    }

    // ANA-20: the proportion of the CONVERGED samples (okIndex, ascending) with the flag set, and its error; each replicate's
    // proportion over its own converged samples (no error when a replicate has none). With every sample converged the
    // sums, divisors and order are those of the old all-sample form, so the numbers are unchanged bit for bit.
    private static (double? P, double? Se) Proportion(byte[] flags, long offset, int[] okIndex, SampleLayout layout, ProbabilisticMethod method)
    {
        int n = okIndex.Length;
        double count = 0;
        for (int j = 0; j < n; j++) count += flags[offset + okIndex[j]];
        double p = count / n;
        double? se = null;
        if (method == ProbabilisticMethod.MonteCarlo) se = Math.Sqrt(p * (1.0 - p) / n);
        else if (method == ProbabilisticMethod.QuasiMonteCarloShifted)
        {
            var c = new double[layout.Replicates];
            var m = new int[layout.Replicates];
            for (int j = 0; j < n; j++) { int r = layout.ReplicateOf(okIndex[j]); c[r] += flags[offset + okIndex[j]]; m[r]++; }
            var reps = new double[layout.Replicates];
            bool all = true;
            for (int r = 0; r < layout.Replicates; r++) { if (m[r] == 0) { all = false; break; } reps[r] = c[r] / m[r]; }
            if (all)
            {
                double rm = SampleStatistics.Mean(reps, 1);
                se = Math.Sqrt(SampleStatistics.Variance(reps, rm, 1) / layout.Replicates);
            }
        }
        return (p, se);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Convolution (DC, independent inputs)
    // ---------------------------------------------------------------------------------------------------------------

    private static PlfResult Convolve(GridModel model, PlfRequest req, Resolved[] inputs, int[] branches, double[] qp, string method, string evaluator)
    {
        if (req.CellsPerSd < 4) throw DomainRefusal.Mark(new ArgumentException("CellsPerSd must be at least 4."));
        if (req.ConvolutionStepMw.HasValue && !(req.ConvolutionStepMw.Value > 0.0 && double.IsFinite(req.ConvolutionStepMw.Value)))
            throw DomainRefusal.Mark(new ArgumentException("The convolution step must be a finite number above 0."));
        DistributionFactors df = DistributionFactors.Build(model);
        var baseNet = new Dictionary<int, double>(model.Buses.Count);
        foreach (Bus b in model.Buses) baseNet[b.Id] = baseNet.GetValueOrDefault(b.Id) - b.Pd;
        foreach (Generator g in model.Generators) if (baseNet.ContainsKey(g.Bus)) baseNet[g.Bus] += g.Pg;
        double[] f0 = df.DcFlows(baseNet, model.BaseMva);
        int k = inputs.Length;
        // PTDF column per input bus (+1 MW injected there, withdrawn at the island reference).
        var ptdf = new double[k][];
        for (int d = 0; d < k; d++) ptdf[d] = df.PtdfColumn(inputs[d].Input.Bus);

        var rows = new BranchStatistics?[branches.Length];
        var errors = new Exception?[branches.Length];
        var opt = new ParallelOptions();
        if (req.MaxThreads > 0) opt.MaxDegreeOfParallelism = req.MaxThreads;
        Parallel.For(0, branches.Length, opt, bidx =>
        {
            try
            {
                int bi = branches[bidx];
                var coef = new double[k];       // coefficient of X_d (the law's variable) in the flow
                double constant = f0[bi];
                double variance = 0.0;
                for (int d = 0; d < k; d++)
                {
                    double sign = inputs[d].Input.Target == UncertainTarget.BusLoad ? -1.0 : 1.0;
                    double m = inputs[d].Input.Scale ? inputs[d].BaseValue : 1.0;
                    coef[d] = sign * ptdf[d][bi] * m;
                    constant -= sign * ptdf[d][bi] * inputs[d].BaseValue;
                    variance += coef[d] * coef[d] * inputs[d].Input.Distribution.Variance;
                }
                double sdTot = Math.Sqrt(variance);
                double step = req.ConvolutionStepMw ?? (sdTot > 1e-12 ? sdTot / req.CellsPerSd : 1.0);
                LatticePmf acc = LatticePmf.PointMass(0.0, step);
                for (int d = 0; d < k; d++)
                {
                    if (coef[d] == 0.0) continue;
                    acc = acc.Convolve(LatticePmf.OfTerm(inputs[d].Input.Distribution, coef[d], step));
                }
                acc = acc.Shift(constant);
                Branch br = model.Branches[bi];
                double? ex = null;
                if (br.RateAMva > 0)
                {
                    double lim = req.LoadingLimitPercent / 100.0 * br.RateAMva;
                    ex = Math.Min(1.0, acc.Exceedance(lim) + acc.Cdf(-lim) - (acc.AtomsExact ? AtomAt(acc, -lim) : 0.0));
                    if (ex < 0) ex = 0;
                }
                rows[bidx] = new BranchStatistics(bi, br.From, br.To, br.Name ?? "", br.RateAMva, acc.Mean, null,
                    Math.Sqrt(acc.Variance), qp.Select(acc.Quantile).ToArray(), ex, null, step);
            }
            catch (Exception ex) { errors[bidx] = ex; }
        });
        foreach (Exception? e in errors)
            if (e is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
        var list = rows.Select(r => r!).ToList();
        var digest = new List<double>();
        foreach (BranchStatistics r in list) AddDigest(digest, r.Mean, null, r.StdDev, r.QuantileValues.ToArray(), r.ExceedProbability);
        ProbabilisticProvenance prov = ProbabilisticProvenance.ForConvolution(req.Seed, req.Domain, digest);
        return new PlfResult(ProbabilisticStatus.Complete, null, method, evaluator, 0, 0, 0, qp, ErrorModels.Convolution + " (step per branch in latticeStepMw)",
            list, Array.Empty<BusVoltageStatistics>(), Array.Empty<FailedSample>(), prov);
    }

    // P(Y = x) on an exact-atom lattice: the mass of the atom at x (0 when x is off the lattice).
    private static double AtomAt(LatticePmf pmf, double x)
    {
        double pos = (x - pmf.Constant) / pmf.Step;
        long k = (long)Math.Round(pos);
        if (Math.Abs(pos - k) > 1e-9) return 0.0;
        long i = k - pmf.FirstIndex;
        return i >= 0 && i < pmf.Mass.Length ? pmf.Mass[i] : 0.0;
    }
}
