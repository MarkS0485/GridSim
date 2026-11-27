using System.Globalization;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Opf;
using GridSim.Core.Analysis.Reconfiguration;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP7: the optimisation verbs <c>acopf-opt</c> (cost, loss and reactive power objectives with continuous taps and
/// shunts and the discrete rounding rule), <c>acscopf</c> (preventive or corrective AC-SCOPF by constraint generation)
/// and <c>reconfig</c> (feeder reconfiguration by branch exchange). The CLI reads flags and prints; all arithmetic is in
/// Core (GridSim.Core.Analysis.Opf and .Reconfiguration). The verbs print to the console; <c>--out</c> writes the result
/// table (CSV or JSON by the file extension, <see cref="OptimisationReport"/>). They are not on the shared --json/--csv
/// writer list. Exit codes: 0 done; 1 the case or a file cannot be loaded or is refused; 2 usage; 3 not converged, no
/// secured optimum, or the rounded point infeasible (the continuous optimum is still printed).
/// </summary>
internal static class OptimisationCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v, string fmt = "F4") => v.ToString(fmt, Inv);

    private sealed class UsageException(string message) : Exception(message);

    private sealed record Args(string? Positional, Dictionary<string, string> Flags, HashSet<string> Switches)
    {
        public string? Str(string n) => Flags.TryGetValue(n, out string? v) ? v : null;
        public double Num(string n, double d)
        {
            if (!Flags.TryGetValue(n, out string? v)) return d;
            if (double.TryParse(v, NumberStyles.Float, Inv, out double x) && double.IsFinite(x)) return x;
            throw new UsageException($"{n} expected a finite number but got '{v}'");
        }
        public int Int(string n, int d, int min)
        {
            if (!Flags.TryGetValue(n, out string? v)) return d;
            if (int.TryParse(v, NumberStyles.Integer, Inv, out int x) && x >= min) return x;
            throw new UsageException($"{n} expected a whole number of at least {min} but got '{v}'");
        }
    }

    private static Args Parse(string verb, List<string> a, string[] valued, string[] switches)
    {
        string? pos = null;
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var sw = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (!t.StartsWith("--", StringComparison.Ordinal))
            {
                if (pos is not null) throw new UsageException($"{verb} takes one positional argument but got: {pos} {t}.");
                pos = t; continue;
            }
            if (Array.IndexOf(switches, t) >= 0)
            {
                if (!sw.Add(t)) throw new UsageException($"{t} was given twice");
                continue;
            }
            if (Array.IndexOf(valued, t) < 0)
                throw new UsageException($"{verb} does not use {t}. {verb} accepts: {string.Join(", ", valued.Concat(switches))}.");
            if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"{t} expected a value but none was given");
            if (!flags.TryAdd(t, a[++i])) throw new UsageException($"{t} was given twice");
        }
        return new Args(pos, flags, sw);
    }

    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        string verb = a[0].ToLowerInvariant();
        try
        {
            return verb switch
            {
                "acopf-opt" => AcOpfOpt(Parse(verb, a, new[] { "--objective", "--controls", "--max-iter", "--out", "--rounding" }, Array.Empty<string>()), load),
                "acscopf" => AcScopfVerb(Parse(verb, a, new[] { "--mode", "--ramp-mw", "--ramp-file", "--contingencies", "--post-rating", "--max-rounds", "--objective", "--max-iter", "--out", "--outages", "--n2", "--max-outages" }, Array.Empty<string>()), load),
                "reconfig" => Reconfig(Parse(verb, a, new[] { "--switchable", "--min-gain-mw", "--max-swaps", "--out" }, new[] { "--respect-ratings" }), load),
                _ => throw new UsageException($"unknown verb '{verb}'"),
            };
        }
        catch (UsageException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.Usage; }
        catch (InvalidDataException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }
        catch (ArgumentException ex) when (ex is not UnknownCaseException) { Console.Error.WriteLine($"error: {verb}: {CliExit.UserMessage(ex)}"); return CliExit.InputError; }
    }

    private static GridModel? LoadCase(string verb, string? path, Func<string, GridModel> load, out int exit)
    {
        exit = CliExit.Ok;
        if (path is null) { Console.Error.WriteLine($"usage: gridsim {verb} <case> ... (see 'gridsim {verb} --help')"); exit = CliExit.Usage; return null; }
        try { return load(path); }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            Console.Error.WriteLine($"error loading case '{path}': {ex.Message}");
            exit = CliExit.CaseLoadFailure(ex);
            return null;
        }
    }

    private static string? OutPath(Args a)
    {
        string? o = a.Str("--out");
        if (o is null) return null;
        string ext = Path.GetExtension(o).ToLowerInvariant();
        if (ext != ".csv" && ext != ".json") throw new UsageException($"--out expected a .csv or .json file but got '{o}'");
        return o;
    }

    private static void WriteOut(string? path, string verb, IReadOnlyList<OptimisationReport.Row> rows)
    {
        FillReport(rows);
        if (path is null) return;
        string text = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? OptimisationReport.Json(verb, rows) : OptimisationReport.Csv(rows);
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"  result written to {path}");
    }

    /// <summary>
    /// RC5 WP11: the shared --json / --csv result (when asked for) from the very rows of the verb's own result file: the
    /// <c>summary</c> rows as summary values (field, value) and every row in the table <c>rows</c> (record, element, field,
    /// value). Nothing is computed for it; with no result flag it does nothing.
    /// </summary>
    private static void FillReport(IReadOnlyList<OptimisationReport.Row> rows)
    {
        CliResult? rep = CliOutput.Report;
        if (rep is null) return;
        foreach (OptimisationReport.Row r in rows)
            if (r.Record == "summary" && r.Element.Length == 0) rep.Set(r.Field, r.Value);
        CliResultTable t = rep.Table("rows", "record", "element", "field", "value");
        foreach (OptimisationReport.Row r in rows) t.Row(r.Record, r.Element, r.Field, r.Value);
        rep.Complete();
    }

    private static AcOpfIpmOptions Ipm(Args a, AcOpfObjective obj) => AcOpfExtended.DefaultIpm(obj) with { MaxIterations = a.Int("--max-iter", 150, 1) };

    private static void PrintLog(string verb, AcOpfIpmResult r)
    {
        Console.Error.WriteLine($"{verb}: {r.Status switch { AcOpfIpmStatus.IterationCap => "not converged", AcOpfIpmStatus.Infeasible => "no solution", _ => "numerical failure" }}: {r.Message}.");
        Console.Error.WriteLine($"{verb}: iteration log: it, objective, feasibility, gradient, complementarity, cost change, barrier, primal step, dual step");
        foreach (AcOpfIpmIteration l in r.Log)
            Console.Error.WriteLine($"  {l.Iteration,4} {l.Objective.ToString("R", Inv),22} {l.Feasibility.ToString("E3", Inv)} {l.Gradient.ToString("E3", Inv)} " +
                $"{l.Complementarity.ToString("E3", Inv)} {l.CostChange.ToString("E3", Inv)} {l.Barrier.ToString("E3", Inv)} {l.StepPrimal.ToString("F6", Inv)} {l.StepDual.ToString("F6", Inv)}");
    }

    private static void PrintOpf(string label, AcOpfExtendedResult r)
    {
        AcOpfIpmResult b = r.Base;
        Console.WriteLine($"  {label}: converged in {b.Iterations} iteration(s)  objective {F(b.ObjectivePerHour)}  generation {F(b.TotalGenerationMw)} MW  " +
            $"load {F(b.TotalLoadMw)} MW  losses {F(b.LossesMw, "F6")} MW");
        foreach (AcOpfGenResult g in b.Generators)
            Console.WriteLine($"      unit {g.Index,-4} bus {g.Bus,-6} {F(g.PgMw),12} MW {F(g.QgMvar),12} MVAr");
        foreach (OpfControlValue c in r.Controls)
            Console.WriteLine($"      {c.Kind,-5} {c.Element,-24} {F(c.Value, "F6"),12} (case {F(c.CaseValue, "F6")}, range {F(c.Min, "F6")} .. {F(c.Max, "F6")})");
        Console.WriteLine($"      binding constraints: {b.Binding.Count}");
        foreach (AcOpfBinding bd in b.Binding)
            Console.WriteLine($"        {bd.Kind,-4} {bd.Element,-32} shadow price {F(bd.ShadowPrice)}");
    }

    // ---- acopf-opt -------------------------------------------------------------------------------------------------

    private static int AcOpfOpt(Args a, Func<string, GridModel> load)
    {
        AcOpfObjective obj = (a.Str("--objective") ?? "cost").ToLowerInvariant() switch
        {
            "cost" => AcOpfObjective.Cost,
            "losses" => AcOpfObjective.Losses,
            "rpo" => AcOpfObjective.ReactivePower,
            var o => throw new UsageException($"--objective expected cost, losses or rpo but got '{o}'"),
        };
        OpfRoundingMode rounding = (a.Str("--rounding") ?? "flip").ToLowerInvariant() switch
        {
            "flip" => OpfRoundingMode.Flip,
            "neighbours" => OpfRoundingMode.Neighbours,
            var o => throw new UsageException($"--rounding expected flip or neighbours but got '{o}'"),
        };
        AcOpfIpmOptions ipm = Ipm(a, obj);
        string? outPath = OutPath(a);
        GridModel? m = LoadCase("acopf-opt", a.Positional, load, out int exit);
        if (m is null) return exit;
        OpfControlSet? controls = null;
        if (a.Str("--controls") is { } cf)
        {
            string text;
            try { text = File.ReadAllText(cf); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"error: acopf-opt: cannot read --controls '{cf}': {ex.Message}"); return CliExit.InputError; }
            controls = OpfControlSet.Parse(text, m);
        }
        OpfRoundingResult r = OpfRounding.Solve(m, new AcOpfExtendedOptions { Objective = obj, Ipm = ipm, Controls = controls }, rounding);
        int psts = controls?.PhaseShifts.Count ?? 0;
        Console.WriteLine($"acopf-opt - {m.Name} ({m.BusCount} buses, {m.Generators.Count} units): objective {obj.ToString().ToLowerInvariant()}, " +
            $"{controls?.Taps.Count ?? 0} tap(s), {controls?.Shunts.Count ?? 0} shunt(s)" + (psts > 0 ? $", {psts} phase shifter(s)" : ""));
        if (!r.Continuous.Converged) { PrintLog("acopf-opt", r.Continuous.Base); return CliExit.Verdict; }
        PrintOpf("continuous optimum", r.Continuous);
        if (r.Controls.Count > 0)
        {
            Console.WriteLine(r.Mode == OpfRoundingMode.Neighbours
                ? $"  rounding (best of the {r.Combinations} floor/ceil combination(s) around the continuous optimum):"
                : "  rounding (nearest grid point; a tie to the case value, then the lower):");
            foreach (OpfRoundedControl rc in r.Controls)
                Console.WriteLine($"      {rc.Kind,-5} {rc.Element,-24} {F(rc.Continuous, "F6")} -> step {rc.Step} = {F(rc.Rounded, "F6")}");
            Console.WriteLine($"  rounding: {r.Status}: {r.Message}");
            if (r.Final is not null && r.Status is OpfRoundingStatus.Rounded or OpfRoundingStatus.Flipped or OpfRoundingStatus.BestNeighbour) PrintOpf("discrete re-solve", r.Final);
        }
        WriteOut(outPath, "acopf-opt", OptimisationReport.Rows(r));
        return r.Status is OpfRoundingStatus.RoundedPointInfeasible or OpfRoundingStatus.NoNeighbourFeasible ? CliExit.Verdict : CliExit.Ok;
    }

    // ---- acscopf ---------------------------------------------------------------------------------------------------

    private static int AcScopfVerb(Args a, Func<string, GridModel> load)
    {
        AcScopfMode mode = (a.Str("--mode") ?? "preventive").ToLowerInvariant() switch
        {
            "preventive" => AcScopfMode.Preventive,
            "corrective" => AcScopfMode.Corrective,
            var o => throw new UsageException($"--mode expected preventive or corrective but got '{o}'"),
        };
        AcOpfObjective obj = (a.Str("--objective") ?? "cost").ToLowerInvariant() switch
        {
            "cost" => AcOpfObjective.Cost,
            "losses" => AcOpfObjective.Losses,
            var o => throw new UsageException($"--objective expected cost or losses but got '{o}'"),
        };
        PostFaultRating rating = (a.Str("--post-rating") ?? "a").ToLowerInvariant() switch
        {
            "a" => PostFaultRating.RateA,
            "b" => PostFaultRating.RateB,
            "c" => PostFaultRating.RateC,
            var o => throw new UsageException($"--post-rating expected A, B or C but got '{o}'"),
        };
        if (a.Str("--ramp-mw") is not null && a.Str("--ramp-file") is not null) throw new UsageException("give --ramp-mw or --ramp-file, not both");
        if (mode == AcScopfMode.Preventive && (a.Str("--ramp-mw") is not null || a.Str("--ramp-file") is not null))
            throw new UsageException("--ramp-mw and --ramp-file go with --mode corrective");
        if (mode == AcScopfMode.Corrective && a.Str("--ramp-mw") is null && a.Str("--ramp-file") is null)
            throw new UsageException("--mode corrective needs --ramp-mw <MW> or --ramp-file <file.json>");
        double ramp = a.Num("--ramp-mw", double.NaN);
        if (a.Str("--ramp-mw") is not null && ramp < 0) throw new UsageException($"--ramp-mw expected 0 MW or more but got '{a.Str("--ramp-mw")}'");
        int maxRounds = a.Int("--max-rounds", 20, 1);
        AcOpfIpmOptions ipm = Ipm(a, obj);
        string? outPath = OutPath(a);
        if (a.Str("--outages") is not null && a.Str("--n2") is not null) throw new UsageException("give --outages or --n2, not both");
        if (a.Str("--outages") is not null && a.Str("--contingencies") is not null) throw new UsageException("give --outages or --contingencies, not both");
        if (a.Str("--max-outages") is not null && a.Str("--n2") is null) throw new UsageException("--max-outages goes with --n2");
        int maxOutages = a.Int("--max-outages", ScopfOutageFile.DefaultMaxOutages, 1);
        IReadOnlyList<int>? cont = null;
        if (a.Str("--contingencies") is { } cs && !cs.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var list = new List<int>();
            foreach (string t in cs.Split(','))
            {
                if (!int.TryParse(t.Trim(), NumberStyles.Integer, Inv, out int k)) throw new UsageException($"--contingencies expected all or a comma list of branch indices but got '{cs}'");
                list.Add(k);
            }
            cont = list;
        }
        GridModel? m = LoadCase("acscopf", a.Positional, load, out int exit);
        if (m is null) return exit;
        IReadOnlyList<double>? perGen = null;
        if (a.Str("--ramp-file") is { } rf) perGen = ReadRamps(rf, m);
        // General outages: a gridsim-scopf-outages/1 file, or N-2 (every pair of the listed branches, or of every in-service
        // branch, or the branch pairs of a file).
        IReadOnlyList<ScopfOutage>? outages = null;
        if (a.Str("--outages") is { } of) outages = ScopfOutageFile.Parse(ReadText("--outages", of));
        if (a.Str("--n2") is { } n2)
        {
            if (n2.Equals("pairs", StringComparison.OrdinalIgnoreCase))
                outages = ScopfOutageFile.Pairs(cont ?? ContingencyAnalysis.InServiceBranchIndices(m), maxOutages);
            else
            {
                outages = ScopfOutageFile.Parse(ReadText("--n2", n2));
                foreach (ScopfOutage o in outages)
                    if (o.Branches.Count != 2 || o.Generators.Count != 0)
                        throw new InvalidDataException($"--n2 file: outage '{o.Label}' is not a pair of branches (use --outages for other outages).");
                if (outages.Count > maxOutages)
                    throw new InvalidDataException($"--n2 file: {outages.Count} pairs, above the cap of {maxOutages}; raise the cap (--max-outages).");
            }
            cont = null;
        }
        AcScopfResult r = AcScopf.Solve(m, new AcScopfOptions
        {
            Mode = mode, Contingencies = outages is null ? cont : null, Outages = outages, RampMw = ramp, RampPerGenMw = perGen,
            PostContingencyRating = rating, MaxRounds = maxRounds, Objective = obj, Ipm = ipm,
        });
        Console.WriteLine($"acscopf - {m.Name} ({m.BusCount} buses, {m.Generators.Count} units): {mode.ToString().ToLowerInvariant()}, objective {obj.ToString().ToLowerInvariant()}, post-outage rating {rating}");
        foreach (AcScopfRound rd in r.Rounds)
            Console.WriteLine($"  round {rd.Round}: {rd.Secured.Count} outage(s) in the problem, {rd.Iterations} iteration(s), objective {F(rd.Objective)}, added [{string.Join(",", rd.Added)}]");
        Console.WriteLine($"  secured [{string.Join(",", r.Secured)}]  screened ok [{string.Join(",", r.ScreenedOk)}]  islanding (not secured) [{string.Join(",", r.Islanding)}]");
        if (!r.Optimal)
        {
            Console.Error.WriteLine($"acscopf: {r.Status}: {r.Message}.");
            if (r.Last is { Converged: false } bad) PrintLog("acscopf", bad.Base);
            return CliExit.Verdict;
        }
        PrintOpf("secured optimum (base case)", r.Last!);
        foreach (AcOpfBlockResult b in r.Last!.Outages)
        {
            string after = b.OutageLabel is null ? $"after outage of branch {b.Outage}" : $"after outage {b.Outage} ({b.OutageLabel})";
            Console.WriteLine($"    {after}: generation {F(b.TotalGenerationMw)} MW; {string.Join(", ", b.Generators.Select(g => $"unit {g.Index} {F(g.PgMw)} MW"))}");
            foreach (AcOpfBinding bd in b.Binding) Console.WriteLine($"        {bd.Kind,-4} {bd.Element,-32} shadow price {F(bd.ShadowPrice)}");
        }
        foreach (AcOpfBinding bd in r.Last.CouplingBinding) Console.WriteLine($"    {bd.Kind} {bd.Element}: shadow price {F(bd.ShadowPrice)}");
        WriteOut(outPath, "acscopf", OptimisationReport.Rows(r));
        return CliExit.Ok;
    }

    private static string ReadText(string flag, string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidDataException($"cannot read {flag} '{path}': {ex.Message}"); }
    }

    /// <summary>A ramp file: <c>{"format": "gridsim-opf-ramps/1", "rampMw": [r0, r1, ...]}</c>, one ramp per in-service
    /// unit in model order.</summary>
    private static IReadOnlyList<double> ReadRamps(string path, GridModel m)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidDataException($"cannot read --ramp-file '{path}': {ex.Message}"); }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("ramp file: the file must be a JSON object.");
            double[]? ramps = null;
            foreach (JsonProperty p in root.EnumerateObject())
            {
                if (p.Name == "format")
                {
                    if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() != "gridsim-opf-ramps/1")
                        throw new InvalidDataException("ramp file: format must be \"gridsim-opf-ramps/1\".");
                }
                else if (p.Name == "rampMw")
                {
                    if (p.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ramp file: rampMw must be a list of numbers.");
                    ramps = p.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double v) && double.IsFinite(v) && v >= 0
                        ? v : throw new InvalidDataException($"ramp file: {e.GetRawText()} is not a ramp of 0 MW or more.")).ToArray();
                }
                else throw new InvalidDataException($"ramp file: unknown member '{p.Name}' (known: format, rampMw).");
            }
            if (ramps is null) throw new InvalidDataException("ramp file: rampMw is missing.");
            if (ramps.Length != m.Generators.Count) throw new InvalidDataException($"ramp file: {ramps.Length} ramp(s) for {m.Generators.Count} in-service unit(s).");
            return ramps;
        }
        catch (JsonException ex) { throw new InvalidDataException($"ramp file: not valid JSON ({ex.Message})."); }
    }

    // ---- reconfig --------------------------------------------------------------------------------------------------

    private static int Reconfig(Args a, Func<string, GridModel> load)
    {
        double minGain = a.Num("--min-gain-mw", 1e-6);
        if (minGain < 0) throw new UsageException($"--min-gain-mw expected 0 MW or more but got '{a.Str("--min-gain-mw")}'");
        int maxSwaps = a.Int("--max-swaps", 100, 0);
        string? outPath = OutPath(a);
        IReadOnlyList<int>? sw = null;
        if (a.Str("--switchable") is { } s)
        {
            var list = new List<int>();
            foreach (string t in s.Split(','))
            {
                if (!int.TryParse(t.Trim(), NumberStyles.Integer, Inv, out int k)) throw new UsageException($"--switchable expected a comma list of branch indices but got '{s}'");
                list.Add(k);
            }
            sw = list;
        }
        GridModel? m = LoadCase("reconfig", a.Positional, load, out int exit);
        if (m is null) return exit;
        // A MATPOWER file drops its open (status 0) branches when read for every other study; the tie switches are what
        // reconfiguration moves, so the file is read again keeping them out of service (after the normal, gated load).
        if (a.Positional!.EndsWith(".m", StringComparison.OrdinalIgnoreCase))
        {
            string path = Gated.ResolveMatpower(a.Positional);
            MatpowerParseResult withOpen = MatpowerParser.ParseWithOpenBranches(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), frequencyHz: m.FrequencyHz);
            GridModel reread = MatpowerUnitNormalizer.Normalize(withOpen.Model);
            // CLI-20: the second read is studied, so it must be the network the gate admitted: the same buses (a file
            // swapped between the two reads is refused), and the gated load's frequency.
            if (reread.BusCount != m.BusCount || !reread.Buses.Select(b => b.Id).SequenceEqual(m.Buses.Select(b => b.Id)))
            {
                Console.Error.WriteLine($"error: reconfig: '{path}' changed while it was read (its buses differ from the case that was loaded); run it again.");
                return CliExit.InputError;
            }
            m = reread;
        }
        ReconfigResult r = FeederReconfiguration.Run(m, new ReconfigOptions
        {
            Switchable = sw, RespectRatings = a.Switches.Contains("--respect-ratings"), MinGainMw = minGain, MaxSwaps = maxSwaps,
        });
        Console.WriteLine($"reconfig - {m.Name} ({m.BusCount} buses, {m.Branches.Count} branches): branch exchange, best improvement");
        Console.WriteLine($"  base losses {F(r.BaseLossMw * 1000.0, "F6")} kW");
        for (int i = 0; i < r.Swaps.Count; i++)
            Console.WriteLine($"  swap {i + 1}: close {r.Swaps[i].Closed}, open {r.Swaps[i].Opened}: losses {F(r.Swaps[i].LossMw * 1000.0, "F6")} kW");
        Console.WriteLine($"  final open branches [{string.Join(",", r.OpenBranches)}]  losses {F(r.LossMw * 1000.0, "F6")} kW  minimum voltage {F(r.VminPu, "F5")} pu");
        Console.WriteLine($"  {r.Status}: {r.Message}");
        WriteOut(outPath, "reconfig", OptimisationReport.Rows(r));
        return CliExit.Ok;
    }
}
