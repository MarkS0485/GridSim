using System.Globalization;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Contingency;
using GridSim.Core.Analysis.Sensitivities;
using GridSim.Core.Analysis.VoltageStability;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-03: the verbs <c>sensitivity</c>, <c>qv-curve</c>, <c>pv-contingency</c>, <c>dc-contingency</c> and
/// <c>fault-level-n1</c>. A positional case like <c>ptdf</c> (loaded through the same case resolver), results through the
/// shared result writer (<see cref="CliOutput.Report"/>), strict flag values (a bad one is exit 2, never a default). All the
/// arithmetic is in GridSim.Core (<c>Analysis/Sensitivities</c>, <c>Analysis/VoltageStability</c>,
/// <c>Analysis/Contingency</c>, <c>Protection/ShortCircuit/FaultLevelUnderOutage</c>); this file parses, prints and writes.
/// Exit codes: 0 done; 1 the case does not load or its model cannot be formed; 2 usage; 3 the study ran and its answer is a
/// failure (base case not converged, an out-of-scope case, a not-secure verdict).
/// </summary>
internal static class SensitivityCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string F(double v, string fmt = "F4") => v.ToString(fmt, Inv);
    private static string R(double v) => v.ToString("R", Inv);

    private sealed class UsageException(string message) : Exception(message);

    private sealed record Args(string? Case, Dictionary<string, string> Flags)
    {
        public string? Str(string name) => Flags.TryGetValue(name, out string? v) ? v : null;

        public double Num(string name, double dflt)
        {
            if (!Flags.TryGetValue(name, out string? v)) return dflt;
            if (double.TryParse(v, NumberStyles.Float, Inv, out double d) && double.IsFinite(d)) return d;
            throw new UsageException($"{name} expected a finite number but got '{v}'");
        }

        public int Int(string name, int dflt, int min = int.MinValue)
        {
            if (!Flags.TryGetValue(name, out string? v)) return dflt;
            if (!int.TryParse(v, NumberStyles.Integer, Inv, out int i)) throw new UsageException($"{name} expected an integer but got '{v}'");
            if (i < min) throw new UsageException($"{name} expected a whole number, {min} or more, but got '{v}'");
            return i;
        }

        public int? OptInt(string name) => Flags.ContainsKey(name) ? Int(name, 0) : null;

        public int[]? IntList(string name)
        {
            if (!Flags.TryGetValue(name, out string? v)) return null;
            var list = new List<int>();
            foreach (string part in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, NumberStyles.Integer, Inv, out int i))
                    throw new UsageException($"{name} expected a comma list of whole numbers but got '{v}'");
                list.Add(i);
            }
            if (list.Count == 0) throw new UsageException($"{name} expected a comma list of whole numbers but got '{v}'");
            return list.ToArray();
        }
    }

    private static Args Parse(string verb, List<string> a, params string[] allowed)
    {
        string? caseArg = null;
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (!t.StartsWith("--", StringComparison.Ordinal))
            {
                if (caseArg is not null) throw new UsageException($"{verb} takes one case argument but got: {caseArg} {t} (the extra one would be silently ignored).");
                caseArg = t;
                continue;
            }
            if (Array.IndexOf(allowed, t) < 0)
                throw new UsageException($"{verb} does not use {t}. {verb} accepts: {string.Join(", ", allowed)}.");
            if (i + 1 >= a.Count || (a[i + 1].StartsWith("--", StringComparison.Ordinal)))
                throw new UsageException($"{t} expected a value but none was given");
            if (!flags.TryAdd(t, a[++i])) throw new UsageException($"{t} was given twice");
        }
        return new Args(caseArg, flags);
    }

    /// <summary>The entry point: <paramref name="a"/> is the whole argument list with the verb first.</summary>
    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        string verb = a[0].ToLowerInvariant();
        try
        {
            return verb switch
            {
                "sensitivity" => Sensitivity(Parse(verb, a, "--kind", "--branch", "--bus", "--top"), load),
                "qv-curve" => QvCurveVerb(Parse(verb, a, "--bus", "--vmin", "--vmax", "--step"), load),
                "pv-contingency" => PvContingencyVerb(Parse(verb, a, "--branches", "--step", "--min-step", "--max-steps", "--parallel", "--top"), load),
                "dc-contingency" => DcContingencyVerb(Parse(verb, a, "--branches", "--post-rating", "--top"), load),
                "fault-level-n1" => FaultLevelVerb(Parse(verb, a, "--buses", "--branches", "--objective", "--parallel", "--top"), load),
                _ => throw new UsageException($"unknown verb '{verb}'"),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {verb}: {ex.Message}");
            return CliExit.Usage;
        }
        catch (ArgumentException ex) when (ex is not UnknownCaseException)
        {
            Console.Error.WriteLine($"error: {verb}: {CliExit.UserMessage(ex)}");
            return CliExit.Usage;
        }
    }

    private static GridModel? LoadCase(string verb, Args args, Func<string, GridModel> load, out int exit)
    {
        exit = CliExit.Ok;
        if (args.Case is null)
        {
            Console.Error.WriteLine($"usage: gridsim {verb} <case> ... (see 'gridsim {verb} --help')");
            exit = CliExit.Usage;
            return null;
        }
        try { return load(args.Case); }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            Console.Error.WriteLine($"error loading case '{args.Case}': {ex.Message}");
            exit = CliExit.CaseLoadFailure(ex);
            return null;
        }
    }

    private static void RequireBus(string verb, GridModel m, int bus, string flag)
    {
        if (!m.Buses.Any(b => b.Id == bus)) throw new UsageException($"{flag} {bus}: case '{m.Name}' has no bus {bus}.");
    }

    private static void RequireBranch(GridModel m, int branch)
    {
        if (branch < 0 || branch >= m.Branches.Count)
            throw new UsageException($"--branch {branch}: case '{m.Name}' has branches 0..{m.Branches.Count - 1}.");
        if (!m.Branches[branch].InService) throw new UsageException($"--branch {branch} is out of service in case '{m.Name}'.");
    }

    private static string BranchLabel(GridModel m, int l) => $"{m.Branches[l].From}->{m.Branches[l].To}";

    // ---- sensitivity ----------------------------------------------------------------------------------------------

    private static int Sensitivity(Args args, Func<string, GridModel> load)
    {
        string kind = (args.Str("--kind") ?? throw new UsageException("--kind is required: otdf, psdf, tap, dvdq or effectiveness")).ToLowerInvariant();
        if (kind is not ("otdf" or "psdf" or "tap" or "dvdq" or "effectiveness"))
            throw new UsageException($"--kind expected otdf, psdf, tap, dvdq or effectiveness but got '{kind}'");
        int top = args.Int("--top", 12, 0);
        int? branch = args.OptInt("--branch"), bus = args.OptInt("--bus");
        bool needBranch = kind is "otdf" or "psdf" or "tap";
        bool needBus = kind is "otdf" or "dvdq" or "effectiveness";
        if (needBranch && branch is null) throw new UsageException($"--kind {kind} needs --branch");
        if (needBus && bus is null) throw new UsageException($"--kind {kind} needs --bus");
        if (!needBranch && branch is not null) throw new UsageException($"--kind {kind} does not use --branch");
        if (!needBus && bus is not null) throw new UsageException($"--kind {kind} does not use --bus");

        GridModel? m = LoadCase("sensitivity", args, load, out int exit);
        if (m is null) return exit;
        if (branch is int br) RequireBranch(m, br);
        if (bus is int bs) RequireBus("sensitivity", m, bs, "--bus");

        CliResult? rep = CliOutput.Report;
        rep?.Set("case", m.Name).Set("kind", kind).Set("buses", m.BusCount);
        if (kind is "otdf" or "psdf")
        {
            DistributionFactors df;
            try { df = DistributionFactors.Build(m); }
            catch (InvalidOperationException ex) { Console.Error.WriteLine($"[sensitivity] {ex.Message}"); return CliExit.InputError; }
            if (kind == "otdf")
            {
                OtdfColumn col = OutageTransferFactors.Column(m, df, branch!.Value, bus!.Value);
                rep?.Set("outage_branch", branch.Value).Set("bus", bus.Value).Set("islanded", col.Islanded);
                if (col.Islanded)
                    Console.WriteLine($"OTDF - {m.Name}: outage of branch {branch} ({BranchLabel(m, branch.Value)}) ISLANDS the system (bridge line); the post-outage transfer factors are undefined.");
                else
                {
                    Console.WriteLine($"OTDF - {m.Name} ({m.BusCount} buses): +1 MW at bus {bus} after the outage of branch {branch} ({BranchLabel(m, branch.Value)}) flows on:");
                    PrintRanked(m, col.Factors, top, "OTDF");
                }
                if (rep is not null)
                {
                    CliResultTable t = rep.Table("factors", "branch", "from", "to", "otdf");
                    for (int l = 0; l < col.Factors.Length; l++) t.Row(l, m.Branches[l].From, m.Branches[l].To, col.Factors[l]);
                    rep.Complete();
                }
                return CliExit.Ok;
            }
            double[] s = PhaseShifterSensitivity.FlowSensitivityMwPerRad(m, df, branch!.Value);
            rep?.Set("branch", branch.Value).Set("convention", "positive shift reduces the From-to-To flow (the solver's)");
            Console.WriteLine($"PSDF - {m.Name} ({m.BusCount} buses): MW per radian of phase shift on branch {branch} ({BranchLabel(m, branch.Value)}); positive shift reduces its From-to-To flow:");
            PrintRanked(m, s, top, "MW/rad", "F3");
            if (rep is not null)
            {
                CliResultTable t = rep.Table("factors", "branch", "from", "to", "mw_per_rad", "mw_per_deg");
                for (int l = 0; l < s.Length; l++) t.Row(l, m.Branches[l].From, m.Branches[l].To, s[l], s[l] * (Math.PI / 180.0));
                rep.Complete();
            }
            return CliExit.Ok;
        }

        // The AC kinds: a converged base case with the contingency study solver, then the Jacobian at that state.
        IPowerFlowSolver solver = ContingencyAnalysis.CreateStudySolver(m);
        PowerFlowResult solved = solver.Solve(m, ContingencyAnalysis.StudyOptions);
        if (!solved.Converged)
        {
            Console.Error.WriteLine($"error: sensitivity: the base case of {m.Name} did not converge; AC sensitivities need a converged state.");
            return CliExit.Verdict;
        }
        AcSensitivityEngine engine;
        try { engine = AcSensitivityEngine.Build(m, solved); }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: sensitivity: {ex.Message}");
            return CliExit.Verdict;
        }

        if (kind == "dvdq")
        {
            VoltageSensitivityColumn c = VoltageSensitivity.Column(engine, bus!.Value);
            rep?.Set("bus", bus.Value).Set("regulated_bus", !c.Resolved);
            Console.WriteLine($"dV/dQ - {m.Name}: voltage change (pu) at every bus per +1 MVAr injected at bus {bus}" + (c.Resolved ? "." : " (the bus holds its voltage at the solved state: no reactive response)."));
            var order = Enumerable.Range(0, m.BusCount).OrderByDescending(i => Math.Abs(c.DVmPerMvar[i])).Take(top);
            foreach (int i in order)
                Console.WriteLine($"    bus {m.Buses[i].Id,-6} dV/dQ {F(c.DVmPerMvar[i], "E4"),11} pu/MVAr   dV/dP {F(c.DVmPerMw[i], "E4"),11} pu/MW");
            if (rep is not null)
            {
                CliResultTable t = rep.Table("voltage", "bus", "dv_pu_per_mvar", "dv_pu_per_mw", "dtheta_rad_per_mvar");
                for (int i = 0; i < m.BusCount; i++) t.Row(m.Buses[i].Id, c.DVmPerMvar[i], c.DVmPerMw[i], c.DThetaRadPerMvar[i]);
                rep.Complete();
            }
            return CliExit.Ok;
        }
        if (kind == "tap")
        {
            TapSensitivityColumn c = TapSensitivity.Column(engine, branch!.Value);
            rep?.Set("branch", branch.Value);
            Console.WriteLine($"dV/dtap - {m.Name}: voltage change (pu) at every bus per +1 per cent of the tap ratio of branch {branch} ({BranchLabel(m, branch.Value)}).");
            foreach (int i in Enumerable.Range(0, m.BusCount).OrderByDescending(i => Math.Abs(c.DVmPerUnitRatio[i])).Take(top))
                Console.WriteLine($"    bus {m.Buses[i].Id,-6} dV {F(c.DVmPerUnitRatio[i] * 0.01, "E4"),11} pu per 1% tap");
            if (rep is not null)
            {
                CliResultTable t = rep.Table("voltage", "bus", "dv_pu_per_unit_ratio", "dv_pu_per_percent", "dtheta_rad_per_unit_ratio");
                for (int i = 0; i < m.BusCount; i++) t.Row(m.Buses[i].Id, c.DVmPerUnitRatio[i], c.DVmPerUnitRatio[i] * 0.01, c.DThetaRadPerUnitRatio[i]);
                rep.Complete();
            }
            return CliExit.Ok;
        }
        // effectiveness
        IReadOnlyList<EffectivenessRow> rows;
        try { rows = EffectivenessFactors.ForTarget(m, engine, bus!.Value); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: sensitivity: {CliExit.UserMessage(ex)}");
            return CliExit.Usage;
        }
        rep?.Set("target_bus", bus.Value);
        Console.WriteLine($"Effectiveness - {m.Name}: first-order effect on the voltage of bus {bus} of each control, largest first.");
        var ranked = rows.OrderByDescending(r => Math.Abs(r.EffectPerUnit)).ToList();
        foreach (EffectivenessRow r in ranked.Take(top))
            Console.WriteLine($"    {r.Kind,-18} {r.Label,-22} {F(r.EffectPerUnit, "E4"),11} {r.Unit}");
        if (rep is not null)
        {
            CliResultTable t = rep.Table("effectiveness", "kind", "element", "label", "effect_per_unit", "unit");
            foreach (EffectivenessRow r in ranked) t.Row(r.Kind, r.Element, r.Label, r.EffectPerUnit, r.Unit);
            rep.Complete();
        }
        return CliExit.Ok;
    }

    private static void PrintRanked(GridModel m, double[] values, int top, string name, string fmt = "F4")
    {
        foreach (int l in Enumerable.Range(0, values.Length).Where(l => Math.Abs(values[l]) > CliDefaults.SensitivityDisplayFloor)
                     .OrderByDescending(l => Math.Abs(values[l])).Take(top))
            Console.WriteLine($"    branch {l,-4} {BranchLabel(m, l),-14} {name} {F(values[l], fmt),9}");
    }

    // ---- qv-curve -------------------------------------------------------------------------------------------------

    private static int QvCurveVerb(Args args, Func<string, GridModel> load)
    {
        int bus = args.OptInt("--bus") ?? throw new UsageException("--bus is required");
        double vmin = args.Num("--vmin", 0.4), vmax = args.Num("--vmax", 1.2), step = args.Num("--step", 0.01);
        if (!(step > 0)) throw new UsageException($"--step expected a number above 0 but got '{R(step)}'");
        if (!(vmin > 0) || !(vmax > vmin)) throw new UsageException("--vmin and --vmax expected 0 < vmin < vmax");
        GridModel? m = LoadCase("qv-curve", args, load, out int exit);
        if (m is null) return exit;
        RequireBus("qv-curve", m, bus, "--bus");

        QvCurveResult r = QvCurve.Compute(m, bus, vmin, vmax, step);
        CliResult? rep = CliOutput.Report;
        rep?.Set("case", m.Name).Set("bus", bus).Set("status", r.Status).Set("base_v_pu", r.BaseVPu).Set("step_pu", r.StepPu);
        if (r.Status != "OK")
        {
            Console.Error.WriteLine($"error: qv-curve: {r.Status}.");
            return r.Status.StartsWith("OUT OF SCOPE", StringComparison.Ordinal) ? CliExit.Usage : CliExit.Verdict;
        }
        int bad = r.Points.Count(p => !p.Converged);
        Console.WriteLine($"QV curve - {m.Name}, bus {bus}: operating point V = {F(r.BaseVPu, "F5")} pu; {r.Points.Count} points ({bad} not converged), step {F(r.StepPu, "F4")} pu.");
        Console.WriteLine($"    sweep down ended: {r.DownEnd}; sweep up ended: {r.UpEnd}. Q = reactive output the synthetic unit needs to hold V (MVAr, positive = injecting).");
        Console.WriteLine($"    grid minimum: Q = {F(r.GridMinQMvar, "F3")} MVAr at V = {F(r.GridMinVPu, "F4")} pu" + (r.MinBracketed ? "" : "  (NOT bracketed: the minimum is at the end of the converged run)"));
        if (r.MinBracketed)
            Console.WriteLine($"    nose (parabola through the 3 samples round the minimum): V = {F(r.NoseVPu, "F4")} pu, Q = {F(r.NoseQMvar, "F3")} MVAr");
        Console.WriteLine($"    reactive margin at the operating point: {F(r.MarginMvar, "F3")} MVAr" + (r.MarginMvar < 0 ? "  (NEGATIVE: past the nose)" : ""));
        rep?.Set("down_end", r.DownEnd).Set("up_end", r.UpEnd).Set("grid_min_q_mvar", r.GridMinQMvar).Set("grid_min_v_pu", r.GridMinVPu)
            .Set("min_bracketed", r.MinBracketed).Set("nose_v_pu", r.NoseVPu).Set("nose_q_mvar", r.NoseQMvar)
            .Set("margin_mvar", r.MarginMvar).Set("base_residual_mvar", r.BaseResidualMvar);
        if (rep is not null)
        {
            CliResultTable t = rep.Table("curve", "v_pu", "q_mvar", "converged");
            foreach (QvPoint p in r.Points) t.Row(p.VPu, p.QMvar, p.Converged);
            rep.Complete();
        }
        return r.MinBracketed ? CliExit.Ok : CliExit.Verdict;
    }

    // ---- pv-contingency -------------------------------------------------------------------------------------------

    private static int PvContingencyVerb(Args args, Func<string, GridModel> load)
    {
        int[]? branches = args.IntList("--branches");
        double step = args.Num("--step", 0.25), minStep = args.Num("--min-step", 1e-3);
        int maxSteps = args.Int("--max-steps", 200, 1);
        int parallel = args.Int("--parallel", 1, 0);
        int top = args.Int("--top", 20, 0);
        if (!(step > 0)) throw new UsageException($"--step expected a number above 0 but got '{R(step)}'");
        if (!(minStep > 0) || minStep > step) throw new UsageException($"--min-step expected a value in (0, --step] but got '{R(minStep)}'");
        GridModel? m = LoadCase("pv-contingency", args, load, out int exit);
        if (m is null) return exit;

        PvContingencyResult res = PvPerContingency.Run(m, branches, step, minStep, maxSteps, parallel == 0 ? Environment.ProcessorCount : parallel);
        CliResult? rep = CliOutput.Report;
        rep?.Set("case", m.Name).Set("step", step).Set("min_step", minStep).Set("max_steps", maxSteps)
            .Set("base_status", res.Base.Status).Set("base_max_lambda", res.Base.MaxLambda);
        if (res.Base.Status != "OK")
        {
            Console.Error.WriteLine($"error: pv-contingency: the base case of {m.Name} did not converge; there is no loadability to compare.");
            return CliExit.Verdict;
        }
        Console.WriteLine($"PV per contingency - {m.Name}: base maximum loadability lambda = {R(res.Base.MaxLambda)} (margin {F(res.Base.MarginPct, "F2")} %){(res.Base.Bracketed ? "" : "  NOT bracketed")}.");
        int noSol = res.Rows.Count(r => r.Status == "NO SOLUTION"), isl = res.Rows.Count(r => r.Status == "ISLANDED");
        Console.WriteLine($"    {res.Rows.Count} outages: {res.Rows.Count - noSol - isl} solved, {isl} islanded, {noSol} no solution. Lowest loadability first:");
        foreach (PvContingencyRow r in res.Rows.Where(r => r.Status == "OK").OrderBy(r => r.MaxLambda).Take(top))
            Console.WriteLine($"    branch {r.Outage,-4} {r.Label,-18} lambda {F(r.MaxLambda, "F4")}  margin {F(r.MarginPct, "F2"),8} %{(r.Bracketed ? "" : "  NOT bracketed")}");
        foreach (PvContingencyRow r in res.Rows.Where(r => r.Status != "OK"))
            Console.WriteLine($"    branch {r.Outage,-4} {r.Label,-18} {r.Status}");
        rep?.Set("outages", res.Rows.Count).Set("islanded", isl).Set("no_solution", noSol);
        if (rep is not null)
        {
            CliResultTable t = rep.Table("outages", "branch", "label", "status", "max_lambda", "margin_pct", "bracketed", "solves", "lowest_failed_lambda");
            foreach (PvContingencyRow r in res.Rows) t.Row(r.Outage, r.Label, r.Status, r.MaxLambda, r.MarginPct, r.Bracketed, r.Solves, r.LowestFailedLambda);
            rep.Complete();
        }
        return noSol > 0 ? CliExit.Verdict : CliExit.Ok;
    }

    // ---- dc-contingency -------------------------------------------------------------------------------------------

    private static int DcContingencyVerb(Args args, Func<string, GridModel> load)
    {
        int[]? branches = args.IntList("--branches");
        string ratingText = (args.Str("--post-rating") ?? "A").ToUpperInvariant();
        PostFaultRating rating = ratingText switch
        {
            "A" => PostFaultRating.RateA, "B" => PostFaultRating.RateB, "C" => PostFaultRating.RateC,
            _ => throw new UsageException($"--post-rating expected A, B or C but got '{ratingText}'"),
        };
        int top = args.Int("--top", 20, 0);
        GridModel? m = LoadCase("dc-contingency", args, load, out int exit);
        if (m is null) return exit;

        DcContingencyResult res;
        try { res = DcContingencyScreen.Screen(m, rating, branches); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine($"[dc-contingency] {ex.Message}"); return CliExit.InputError; }

        int violating = res.Rows.Count(r => r.ViolationCount > 0), islanded = res.Rows.Count(r => r.Status == "ISLANDED");
        bool secure = res.Base.ViolationCount == 0 && violating == 0 && islanded == 0;
        Console.WriteLine($"DC N-1 - {m.Name} ({m.BusCount} buses, {res.Rows.Count} outages, DC MW not AC MVA; post-outage rating {ratingText}, {res.UnratedBranches} unrated branches not monitored):");
        Console.WriteLine($"    base case: {res.Base.Status}" + (res.Base.WorstBranch >= 0 ? $", worst branch {res.Base.WorstBranch} at {F(res.Base.WorstLoadingPct, "F1")} %" : ""));
        foreach (DcContingencyRow r in res.Rows.Where(r => r.Status != "SECURE").Take(top))
            Console.WriteLine($"    outage {r.Outage,-4} {r.Label,-18} {r.Status}" + (r.WorstBranch >= 0 ? $"  worst branch {r.WorstBranch} at {F(r.WorstLoadingPct, "F1")} %" : ""));
        Console.WriteLine(secure ? "Verdict: SECURE (exit 0)." : $"Verdict: NOT SECURE, {violating} outage(s) violate, {islanded} islanded" + (res.Base.ViolationCount > 0 ? ", base case violates" : "") + " (exit 3).");
        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("case", m.Name).Set("post_rating", ratingText).Set("outages", res.Rows.Count).Set("violating", violating)
                .Set("islanded", islanded).Set("base_violations", res.Base.ViolationCount).Set("unrated_branches", res.UnratedBranches)
                .Set("dc_islands", res.DcIslands).Set("secure", secure);
            CliResultTable t = rep.Table("outages", "outage", "label", "status", "pre_outage_flow_mw", "violations", "worst_branch", "worst_loading_pct");
            t.Row(-1, res.Base.Label, res.Base.Status, 0.0, res.Base.ViolationCount, res.Base.WorstBranch, res.Base.WorstLoadingPct);
            foreach (DcContingencyRow r in res.Rows) t.Row(r.Outage, r.Label, r.Status, r.PreOutageFlowMw, r.ViolationCount, r.WorstBranch, r.WorstLoadingPct);
            CliResultTable v = rep.Table("violations", "outage", "branch", "label", "flow_mw", "rating_mva", "rating_source", "loading_pct");
            foreach (DcViolation x in res.Base.Violations) v.Row(-1, x.Branch, x.Label, x.FlowMw, x.RatingMva, x.RatingSource, x.LoadingPct);
            foreach (DcContingencyRow r in res.Rows)
                foreach (DcViolation x in r.Violations) v.Row(r.Outage, x.Branch, x.Label, x.FlowMw, x.RatingMva, x.RatingSource, x.LoadingPct);
            rep.Complete();
        }
        return secure ? CliExit.Ok : CliExit.Verdict;
    }

    // ---- fault-level-n1 -------------------------------------------------------------------------------------------

    private static int FaultLevelVerb(Args args, Func<string, GridModel> load)
    {
        int[]? buses = args.IntList("--buses"), branches = args.IntList("--branches");
        string objText = (args.Str("--objective") ?? "max").ToLowerInvariant();
        ShortCircuitObjective objective = objText switch
        {
            "max" => ShortCircuitObjective.Maximum, "min" => ShortCircuitObjective.Minimum,
            _ => throw new UsageException($"--objective expected max or min but got '{objText}'"),
        };
        int parallel = args.Int("--parallel", 1, 0);
        int top = args.Int("--top", 20, 0);
        GridModel? m = LoadCase("fault-level-n1", args, load, out int exit);
        if (m is null) return exit;

        FaultLevelUnderOutageResult res;
        try { res = FaultLevelUnderOutage.Run(m, buses, branches, objective, parallel == 0 ? Environment.ProcessorCount : parallel); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine($"[fault-level-n1] {ex.Message}"); return CliExit.InputError; }

        Console.WriteLine($"Fault level under N-1 - {m.Name}: IEC 60909 three-phase Ik'' ({objText} voltage factor), {res.States.Count - 1} outages plus the intact network.");
        foreach (FaultLevelBusRow r in res.Buses.OrderByDescending(r => r.MaxIkppKa).Take(top))
            Console.WriteLine($"    bus {r.BusId,-6} max {F(r.MaxIkppKa, "F3")} kA ({r.MaxState}); min {F(r.MinIkppKa, "F3")} kA ({r.MinState})" + (r.StatesWithoutSource > 0 ? $"; {r.StatesWithoutSource} state(s) with no source" : ""));
        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("case", m.Name).Set("objective", objText).Set("outages", res.States.Count - 1).Set("fault_buses", res.Buses.Count);
            CliResultTable t = rep.Table("buses", "bus", "intact_ikpp_ka", "intact_ikpp_pu", "max_ikpp_ka", "max_ikpp_pu", "max_state",
                "min_ikpp_ka", "min_ikpp_pu", "min_state", "states_evaluated", "states_without_source");
            foreach (FaultLevelBusRow r in res.Buses)
                t.Row(r.BusId, r.IntactIkppKa, r.IntactIkppPu, r.MaxIkppKa, r.MaxIkppPu, r.MaxState, r.MinIkppKa, r.MinIkppPu, r.MinState,
                    r.StatesEvaluated, r.StatesWithoutSource);
            rep.Complete();
        }
        return CliExit.Ok;
    }
}
