using System.Globalization;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Core.Storage;

namespace GridSim.Cli;

/// <summary>
/// Storage in the <c>chain</c> verb: battery sites carried through the time series (SoC, temperature and ageing
/// persist step to step) with a dispatch policy.
/// <code>
///   gridsim chain &lt;case&gt; --mode daily --steps 48 --bess lfp:35:100:200[:soc] [--bess ...]
///        [--policy idle|peak-shave|price|hold|lp] [--prices synthetic|file.csv]
///        [--shave-high MW --shave-low MW] [--buy-below £ --sell-above £] [--degradation-gbp-per-mwh W]
/// </code>
/// <c>--bess chemistry:bus:MW:MWh[:initial SoC]</c>, chemistry lfp/nmc/lto/na-ion/vrfb/lead-acid. Prices: one
/// £/MWh value per line (per step), or <c>synthetic</c> for the labelled synthetic GB day.
/// </summary>
internal static class StorageCli
{
    // R152: the storage defaults, named once (values unchanged). Program.cs (gda-replay) uses the same constants.

    /// <summary>Time step (h) for the non-daily chain modes and for gda-replay storage: half-hourly settlement.</summary>
    public const double DefaultStepHours = 0.5;

    /// <summary>State of charge (0..1) a <c>--bess</c> site starts at when the spec gives none.</summary>
    public const double DefaultInitialSoc = 0.5;

    /// <summary>Pack DC bus voltage (V) used to size the series string (<see cref="PackModel.Sized"/>): series count =
    /// round(1,000 V / cell nominal voltage). It is a voltage, not a cell or module count.</summary>
    public const double DefaultPackDcVoltage = 1000.0;

    /// <summary>One-way power-conversion efficiency assumed in the LP schedule's storage envelope.</summary>
    public const double DefaultConverterEfficiency = 0.98;

    /// <summary>Peak-shave thresholds as fractions of the peak demand: discharge above the high one, charge below the
    /// low one (when <c>--shave-high</c> / <c>--shave-low</c> are not given).</summary>
    public const double PeakShaveHighFraction = 0.9, PeakShaveLowFraction = 0.7;

    /// <summary>Price-threshold policy defaults (GBP/MWh): charge below, discharge above.</summary>
    public const double DefaultBuyBelowGbpPerMwh = 40, DefaultSellAboveGbpPerMwh = 90;

    /// <summary>Remove every <c>--bess VALUE</c> pair from <paramref name="a"/> and return the values.</summary>
    public static List<string> TakeBessSpecs(List<string> a)
    {
        var specs = new List<string>();
        int i;
        while ((i = a.IndexOf("--bess")) >= 0)
        {
            if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("--bess expects chemistry:bus:MW:MWh[:soc]");
            specs.Add(a[i + 1]);
            a.RemoveRange(i, 2);
        }
        return specs;
    }

    /// <summary>Run the storage chain. <paramref name="options"/> are the power-flow options of every step (N084: the
    /// CLI passes <c>ContingencyAnalysis.StudyOptions</c>, with <paramref name="solver"/> wrapped in the OLTC loop, so
    /// the storage chain solves exactly as plain <c>chain</c> does).</summary>
    public static int Run(List<string> a, List<string> bessSpecs, GridModel baseModel, string caseName, int steps,
        string mode, double rampPct, string? outPath, IPowerFlowSolver solver, PowerFlowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        double stepHours = Flag(a, "--step-hours") ?? (mode == "daily" ? 24.0 / steps : DefaultStepHours);
        if (!(stepHours > 0))
            throw new ArgumentException($"--step-hours must be above 0 (got {stepHours.ToString(CultureInfo.InvariantCulture)}).");
        double[] factors = Enumerable.Range(0, steps).Select(s => mode switch
        {
            "ramp" => 1.0 + rampPct / 100.0 * s,
            "daily" => LoadProfile.DailyFactor((s * stepHours) % 24.0),
            _ => 1.0,
        }).ToArray();

        // Prices.
        // GR-205 made FlagS refuse a bare flag with its generic "needs a value"; --prices keeps its own message, which
        // names the two accepted forms (D-DOC-049).
        string? priceArg;
        try { priceArg = FlagS(a, "--prices"); }
        catch (ArgumentException) { priceArg = null; }
        double[]? prices = null;
        string priceNote = "none";
        // D-DOC-049: --prices is given a value (synthetic or a file). A bare --prices (no value, or another flag next) used
        // to read as "no prices" and the run went on without them; refused instead.
        if (priceArg is null && a.Contains("--prices"))
            throw new ArgumentException("--prices expects a value: 'synthetic' or a price file (one GBP/MWh value per step).");
        if (priceArg is not null)
        {
            if (priceArg.Equals("synthetic", StringComparison.OrdinalIgnoreCase))
            {
                prices = SyntheticPriceSeries(steps, stepHours);
                priceNote = SyntheticPrices.ProvenanceNote;
            }
            else
            {
                prices = ReadPriceFile(priceArg, steps, out int extra);
                if (extra > 0)
                    Console.Error.WriteLine($"note: --prices {Path.GetFileName(priceArg)} has {steps + extra} values; the first {steps} are used (one per step), the last {extra} are not.");
                priceNote = $"file {Path.GetFileName(priceArg)}";
            }
        }

        double peakDemand = baseModel.Buses.Sum(b => b.Pd) * factors.Max();
        string policyName = (FlagS(a, "--policy") ?? (prices is not null ? "lp" : "peak-shave")).ToLowerInvariant();
        double degradation = Flag(a, "--degradation-gbp-per-mwh") ?? 0.0;
        if (degradation < 0)
            throw new ArgumentException($"--degradation-gbp-per-mwh must be 0 or above (got {degradation.ToString(CultureInfo.InvariantCulture)}).");

        List<StorageSite> sites = BuildSites(a, bessSpecs, policyName, peakDemand, prices, stepHours, degradation);

        var spec2 = new StorageTimeSeriesSpec(steps, stepHours, s => factors[s], prices is null ? null : s => prices[s]);
        StorageTimeSeriesResult r = StorageTimeSeries.Run(baseModel, sites, spec2, solver, options);

        outPath ??= Path.Combine(Directory.GetCurrentDirectory(), "results", $"chain-storage-{caseName}.md");
        // D-DOC-072: the report and its CSV through the one output policy (CliFiles creates the folder and never lets an
        // output replace an input of the run).
        using var md = new StreamWriter(CliFiles.Output(outPath, "storage report")) { AutoFlush = true };
        using var csv = new StreamWriter(CliFiles.Output(Path.ChangeExtension(outPath, ".csv"), "storage report CSV")) { AutoFlush = true };
        // GR-692: the report's <report>.run-metadata.json sidecar, as the other report-file verbs write it.
        RunInfo.WriteJson(CliFiles.Output(RunInfo.MetadataPathFor(outPath), "run metadata"), $"chain {caseName} (storage)");
        void Emit(string s) { Console.WriteLine(s); md.WriteLine(s); }

        Emit($"GridSim - storage time series  *  {caseName}  *  {steps} x {stepHours:G3} h  *  mode={mode}  *  policy={policyName}");
        Emit($"prices: {priceNote}.  Chemistry presets are CuratedPlaceholder [VERIFY] (representative cells).");
        Emit("");
        // D-DOC-135 (DOC-17): "T deg C" in place of the degree sign, which the console printed as a stray byte.
        Emit("| Step | Hour | Load x | Demand MW | Price | " + string.Join(" | ", sites.Select(s => $"{s.Name}: MW / SoC / T deg C")) + " | Slack MW | Vmin | Conv |");
        Emit("|---:|---:|---:|---:|---:|" + string.Concat(sites.Select(_ => "---|")) + "---:|---:|:-:|");
        csv.WriteLine("step,hour,load_factor,demand_mw,price," + string.Join(",", sites.Select((s, k) =>
            $"s{k}_request_mw,s{k}_delivered_mw,s{k}_aux_mw,s{k}_soc,s{k}_temp_c,s{k}_fade,s{k}_limited")) + ",slack_mw,vmin,vmax,loss_mw,converged");
        foreach (StorageStepRecord st in r.Steps)
        {
            string siteCols = string.Join(" | ", st.Sites.Select(x => $"{x.DeliveredMw,7:F1} / {x.Soc:F3} / {x.TemperatureC:F1}{(x.Limited ? $" ({x.LimitReason})" : "")}"));
            // D-DOC-137: a step whose power flow did not converge has no slack output and no voltages; they print "-" (the
            // CSV leaves them empty), where the non-converged iterate's numbers used to be printed as if they were a state.
            string slack = st.Converged && !double.IsNaN(st.SlackMw) ? st.SlackMw.ToString("F0", CultureInfo.InvariantCulture) : "-";
            string vmin = st.Converged && !double.IsNaN(st.VminPu) ? st.VminPu.ToString("F3", CultureInfo.InvariantCulture) : "-";
            Emit($"| {st.Step} | {st.Hour:F1} | {st.LoadFactor:F3} | {st.DemandMw:F0} | {(double.IsNaN(st.PriceGbpPerMwh) ? "-" : st.PriceGbpPerMwh.ToString("F0", CultureInfo.InvariantCulture))} | {siteCols} | {slack} | {vmin} | {(st.Converged ? "y" : "N")} |");
            csv.WriteLine(string.Join(",", new[]
            {
                st.Step.ToString(CultureInfo.InvariantCulture), F(st.Hour), F(st.LoadFactor), F(st.DemandMw), F(st.PriceGbpPerMwh),
            }.Concat(st.Sites.SelectMany(x => new[] { F(x.RequestedMw), F(x.DeliveredMw), F(x.AuxiliaryMw), F(x.Soc), F(x.TemperatureC), F(x.CapacityFade), x.Limited ? "1" : "0" }))
             .Concat(new[] { F(st.SlackMw), F(st.VminPu), F(st.VmaxPu), F(st.LossMw), st.Converged ? "1" : "0" })));
        }
        Emit("");
        foreach (string line in SummaryLines(r.Sites, prices is not null)) Emit(line);
        int failed = r.Steps.Count(s => !s.Converged);
        if (failed > 0) Emit($"WARNING: {failed} step(s) did not converge.");
        Console.Error.WriteLine($"[chain] done -> {Path.GetFullPath(outPath)}");
        return failed > 0 ? 3 : 0;
    }

    /// <summary>D-DOC-197: the synthetic GB price day sampled at the start of each step, step s at hour s * stepHours
    /// (wrapping at 24 h), exactly the series the run uses.</summary>
    internal static double[] SyntheticPriceSeries(int steps, double stepHours) =>
        Enumerable.Range(0, steps).Select(s => SyntheticPrices.GbDay(s * stepHours)).ToArray();

    /// <summary>
    /// D-DOC-197 / D-DOC-049 (October 2026): read a <c>--prices</c> file. One price (GBP/MWh) per step: one value per
    /// line, or a CSV line whose LAST field is the price (so "time,price" rows work); blank lines and lines starting with
    /// '#' are skipped. Every value must be a finite number in invariant culture (negative prices are allowed: they
    /// happen). Fewer values than steps is refused; more is allowed, the first <paramref name="steps"/> are used and
    /// <paramref name="extra"/> says how many were not. Anything wrong (missing file, unreadable file, a value that is not
    /// a finite number) throws <see cref="PriceFileException"/> naming the file and, for a value, its line number. Before,
    /// a bad value surfaced as a raw FormatException with no file or line, and a missing file as exit 2.
    /// </summary>
    internal static double[] ReadPriceFile(string path, int steps, out int extra)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new PriceFileException($"--prices {path}: cannot read the price file ({ex.Message})"); }
        var values = new List<double>();
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            string field = t.Split(',')[^1].Trim();
            if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                throw new PriceFileException($"--prices {path}: line {i + 1}: '{field}' is not a price (a finite number in GBP/MWh, '.' as the decimal point)");
            values.Add(v);
        }
        if (values.Count < steps)
            throw new PriceFileException($"--prices {path} has {values.Count} values; {steps} steps need one each.");
        extra = values.Count - steps;
        return values.Take(steps).ToArray();
    }

    /// <summary>
    /// D-DOC-065 (October 2026): one <c>--bess chemistry:bus:MW:MWh[:soc]</c> spec, checked field by field. Four or five
    /// fields (a sixth used to be ignored silently); a known chemistry; an integer bus; MW and MWh finite and above 0 (0 MW
    /// and 0 MWh were accepted, and a negative MW failed later in a Math.Clamp message that did not name the flag); the
    /// starting state of charge, when given, a finite number in [0, 1] (1.5 was clamped to 1 without a word). Every
    /// refusal is an ArgumentException naming the spec and the field (exit 2 at the CLI).
    /// </summary>
    internal static (ChemistryParameters Chemistry, int Bus, double Mw, double Mwh, double Soc) ParseBessSpec(string spec)
    {
        string[] p = spec.Split(':');
        if (p.Length is < 4 or > 5)
            throw new ArgumentException($"--bess '{spec}': expected chemistry:bus:MW:MWh[:soc] (4 or 5 fields separated by ':'), got {p.Length}.");
        ChemistryParameters chem;
        try { chem = BatteryChemistryLibrary.ByName(p[0]); }
        catch (ArgumentException) { throw new ArgumentException($"--bess '{spec}': unknown chemistry '{p[0]}' (lfp, nmc, lto, na-ion, vrfb, lead-acid)."); }
        if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bus))
            throw new ArgumentException($"--bess '{spec}': bus '{p[1]}' is not a bus number.");
        double Positive(string field, string what)
        {
            if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || !(v > 0))
                throw new ArgumentException($"--bess '{spec}': {what} '{field}' must be a number above 0.");
            return v;
        }
        double mw = Positive(p[2], "rated power (MW)");
        double mwh = Positive(p[3], "energy (MWh)");
        double soc = DefaultInitialSoc;
        if (p.Length == 5)
        {
            if (!double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out soc) || !double.IsFinite(soc) || soc < 0 || soc > 1)
                throw new ArgumentException($"--bess '{spec}': starting state of charge '{p[4]}' must be a number from 0 to 1.");
        }
        return (chem, bus, mw, mwh, soc);
    }

    /// <summary>Build the sites from <c>--bess chemistry:bus:MW:MWh[:soc]</c> specs with the named policy.
    /// Peak-shave defaults to 90 % / 70 % of <paramref name="peakDemandMw"/>; <c>lp</c> needs prices.</summary>
    public static List<StorageSite> BuildSites(List<string> a, List<string> bessSpecs, string policyName, double peakDemandMw,
        double[]? prices, double stepHours, double degradationGbpPerMwh = 0.0)
    {
        // D-DOC-049 (ruled by main): the price policy acts on prices only; with no price series it would hold every
        // battery idle for the whole run and report that as a dispatch. Refused, as lp is (gda-replay takes no prices,
        // so it refuses both).
        if (policyName == "price" && prices is null)
            throw new ArgumentException("--policy price needs --prices (a price series): without one it never charges or discharges.");
        var sites = new List<StorageSite>();
        foreach (string spec in bessSpecs)
        {
            (ChemistryParameters chem, int bus, double mw, double mwh, double soc) = ParseBessSpec(spec);   // D-DOC-065
            PackModel pack = PackModel.Sized(chem, mwh * 1e6, DefaultPackDcVoltage, soc, ratedPowerW: mw * 1e6);
            IStorageDispatchPolicy policy = policyName switch
            {
                "idle" => new IdlePolicy(),
                "peak-shave" => new PeakShavePolicy(Flag(a, "--shave-high") ?? PeakShaveHighFraction * peakDemandMw, Flag(a, "--shave-low") ?? PeakShaveLowFraction * peakDemandMw),
                "price" => new PriceThresholdPolicy(Flag(a, "--buy-below") ?? DefaultBuyBelowGbpPerMwh, Flag(a, "--sell-above") ?? DefaultSellAboveGbpPerMwh),
                "hold" => new ResponseHoldingPolicy(),
                "lp" => prices is null
                    ? throw new ArgumentException("--policy lp needs --prices")
                    : new SchedulePolicy(OptimalLpPlan(StorageLpScheduler.Solve(StorageEnvelope.FromPack(pack, mw, DefaultConverterEfficiency, stepHours),
                        prices, stepHours, degradationGbpPerMwh)).NetMw, "lp"),
                _ => throw new ArgumentException($"--policy '{policyName}': expected idle, peak-shave, price, hold or lp"),
            };
            sites.Add(new StorageSite($"{chem.Name} {mw:F0}MW/{mwh:F0}MWh @{bus}", bus, mw, pack, policy));
        }
        return sites;
    }

    /// <summary>The site summary lines shared by chain and gda-replay.</summary>
    public static IEnumerable<string> SummaryLines(IReadOnlyList<SiteSummary> summaries, bool withRevenue)
    {
        foreach (SiteSummary s in summaries)
        {
            yield return $"{s.Name}: out {s.EnergyOutMwh:F1} MWh, in {s.EnergyInMwh:F1} MWh, aux {s.AuxiliaryMwh:F2} MWh, stored change {SignedOneDecimal(s.StoredChangeMwh)} MWh, "
                + $"round trip {(double.IsNaN(s.RoundTripEfficiency) ? "-" : (s.RoundTripEfficiency * 100).ToString("F1", CultureInfo.InvariantCulture) + " %")}, "
                + $"{s.EquivalentFullCycles:F2} equivalent full cycles"
                // D-DOC-135 (DOC-17): "GBP" in place of the pound sign (printed as '?' on the Windows console).
                + (withRevenue ? $", revenue GBP {s.RevenueGbp:F0}" : "");
            yield return $"    ageing over the run: calendar {s.CalendarFade * 100:F4} %, cycle {s.CycleFade * 100:F4} % (rainflow, depth-aware: {s.RainflowCycleFade * 100:F4} %), "
                + $"deepest cycle {(s.Cycles.Count > 0 ? s.Cycles.Max(c => c.Depth) * 100 : 0):F0} % SoC";
        }
    }

    /// <summary>
    /// D-DOC-137 (October 2026): a signed value to one decimal, "+1.2", "-1.2", and "+0.0" for anything that rounds to
    /// zero. The section format "+0.0;-0.0" printed "-+0.0" for a small negative value (one that rounds to zero is
    /// formatted by the first section, and .NET still prefixes the minus sign of the negative number). The magnitude is
    /// formatted with "0.0" (the same rounding the old format applied), a magnitude that rounds to "0.0" prints "+0.0"
    /// (the text the old format gave an exact zero), and every other value prints its sign and those digits, exactly as
    /// before.
    /// </summary>
    internal static string SignedOneDecimal(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        string digits = Math.Abs(v).ToString("0.0", CultureInfo.InvariantCulture);
        if (digits == "0.0") return "+0.0";
        return (v > 0 ? "+" : "-") + digits;
    }

    private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("G6", CultureInfo.InvariantCulture);

    // GR-205: a flag given with no value (the last token, an empty value, or followed by another flag) is a usage error
    // (ArgumentException, exit 2 in the chain handler); it used to read as absent and take the default.
    private static string? FlagS(List<string> a, string name)
    {
        int i = a.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(a[i + 1]))
            throw new ArgumentException($"{name} needs a value.");
        return a[i + 1];
    }

    // GR-205: a number flag is a finite number (double.Parse accepted NaN and Infinity, and threw a bare FormatException
    // on text).
    internal static double? Flag(List<string> a, string name)
    {
        if (FlagS(a, name) is not string s) return null;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new ArgumentException($"{name} expected a finite number but got '{s}'.");
        return v;
    }

    /// <summary>R292: the LP schedule is only a schedule when the LP solved to Optimal. An infeasible, unbounded or
    /// stalled LP used to hand its (meaningless) vector to the time series as if it were the plan.</summary>
    private static StorageLpScheduler.Plan OptimalLpPlan(StorageLpScheduler.Plan plan)
    {
        if (plan.Status != GridSim.Core.Numerics.LpStatus.Optimal)
            throw new InvalidOperationException($"--policy lp: the storage schedule LP did not solve (status {plan.Status}); no schedule to run.");
        return plan;
    }
}

/// <summary>D-DOC-197 / D-DOC-049 (October 2026): a --prices file that is missing, unreadable or malformed. The chain
/// handler maps it to exit 1 (an input error, as a case file that cannot be loaded); the message names the file and,
/// for a bad value, its line.</summary>
internal sealed class PriceFileException : Exception
{
    public PriceFileException(string message) : base(message) { }
}
