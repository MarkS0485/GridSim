using System.Globalization;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Ras;
using GridSim.Core.Api;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP26: <c>nminus1 --ras FILE</c> and <c>nminus2 --ras FILE</c>, the contingency screens with user-authored
/// remedial action schemes (a <c>gridsim-ras/1</c> file). Without <c>--ras</c> these verbs never reach this class, so
/// their output is unchanged. The pre-scheme table is the screen's own table, with the screen's lost load and
/// generation, reference-move and worsened base-case violation lines; a second table gives each contingency's schemes and
/// the state they leave, with the same lines for that state, and the verdict is judged on that final state.
/// </summary>
internal static class RasCli
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Read and resolve the scheme file against the loaded model. A PSS/E RAW case keeps its native keys (<c>bus:id</c>
    /// units, <c>from-to:circuit</c> branches), read from the same file; any other case uses the object model's names.
    /// A file that cannot be read, or that names something the case does not have, is an input error (exit 1).
    /// </summary>
    public static RasSet Load(string verb, string path, GridModel model, string? caseArg)
    {
        try
        {
            RasSet set = RasFile.ReadFile(path);
            IReadOnlyList<ElementKeys.MachineId>? machines = null;
            IReadOnlyList<ElementKeys.BranchId>? branches = null;
            if (caseArg is not null && (caseArg.EndsWith(".raw", StringComparison.OrdinalIgnoreCase) || caseArg.EndsWith(".rawx", StringComparison.OrdinalIgnoreCase))
                && File.Exists(caseArg))
            {
                try
                {
                    PsseParseResult raw = PsseRawParser.ParseFile(caseArg, new PsseReadOptions());
                    if (raw.MachineKeys.Count == model.AllGenerators.Count) machines = raw.MachineKeys.Select(k => new ElementKeys.MachineId(k.Bus, k.Id)).ToList();
                    if (raw.BranchKeys.Count == model.Branches.Count)
                        branches = raw.BranchKeys.Select(k => new ElementKeys.BranchId(k.FromBus, k.ToBus, k.ThirdBus, k.Circuit, k.Winding)).ToList();
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentException) { /* the model's own names */ }
            }
            return set.Resolve(model, machines, branches);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {verb} --ras: {ex.Message}");
            Environment.Exit(CliExit.InputError);
            return null!;   // unreachable
        }
    }

    /// <summary>nminus1 with schemes. Returns the exit code (0 secure, 3 not secure or base case not converged).</summary>
    public static int RunNMinus1(GridModel model, RasSet ras, bool outBranch, bool outGen, bool outBus, PostFaultRating rating,
        IPowerFlowSolver solver, PowerFlowOptions options)
    {
        bool typesDefault = outBranch && !outGen && !outBus;
        string kinds = string.Join(", ", new[] { outBranch ? "branch" : null, outGen ? "generator" : null, outBus ? "bus" : null }.Where(k => k is not null));
        Console.WriteLine($"GridSim  -  N-1 screening ({kinds} outages) with remedial action schemes: {model.Name}");
        Header(ras, rating);
        var outages = new List<ElementOutage>();
        if (outBranch) outages.AddRange(ContingencyAnalysis.InServiceBranchIndices(model).Select(i => new ElementOutage(OutageKind.Branch, i)));
        if (outGen || outBus) outages.AddRange(ContingencyAnalysis.SingleOutages(model, false, outGen, outBus));
        RasScreenReport report;
        try { report = ContingencyAnalysis.ScreenWithRas(model, outages, ras, solver, options); }
        catch (RasBaseCaseException ex) { return BaseFailed("nminus1", model, ex); }
        // The screen's order: unsolved first, then by the number of new violations (stable).
        List<RasContingencyResult> ordered = report.Results
            .OrderByDescending(r => r.PreRas.Converged ? 0 : 2).ThenByDescending(r => r.PreRas.Violations.Count).ToList();
        PrintBase(report.BaseCase);
        PreTable(ordered.Select(r => r.PreRas).ToList(), int.MaxValue);
        int failing = PostTable(ordered, int.MaxValue);
        Report(model.Name, typesDefault ? "every branch" : $"every {kinds}", ras, report, ordered,
            typesDefault && rating == PostFaultRating.RateA ? null : rating.ToString());
        return Verdict(report.BaseCase, failing + report.ExcludedSingleOutages.Count);
    }

    /// <summary>nminus2 with schemes. Returns the exit code.</summary>
    public static int RunNMinus2(GridModel model, RasSet ras, int candidates, bool exhaustive, int top, PostFaultRating rating,
        Func<IPowerFlowSolver> solverFactory, PowerFlowOptions options)
    {
        string scope = exhaustive ? "exhaustive (every pair)" : $"top-{candidates} candidate pairs";
        Console.WriteLine($"GridSim  -  N-2 branch screening with remedial action schemes: {model.Name}  [{scope}]");
        Header(ras, rating);
        RasScreenReport report;
        try { report = ContingencyAnalysis.ScreenNMinusKWithRas(model, 2, ras, solverFactory, options, candidates, exhaustive); }
        catch (RasBaseCaseException ex) { return BaseFailed("nminus2", model, ex); }
        PrintBase(report.BaseCase);
        if (!exhaustive) Console.WriteLine($"Screening N-1 first, then pairing the {candidates} most-stressed single outages that solve.");
        PreTable(report.Results.Select(r => r.PreRas).ToList(), top);
        if (report.Note is not null) Console.WriteLine($"No pairs screened: {report.Note}");
        if (report.ExcludedSingleOutages.Count > 0)
        {
            Console.WriteLine($"{report.ExcludedSingleOutages.Count} single outage(s) left out of the pairing because they already fail at N-1 (islanded or no solution):");
            foreach (ContingencyResult c in report.ExcludedSingleOutages) Console.WriteLine($"  {Trunc(c.Outage, 30),-30}  {c.Violations[0]}");
        }
        int failing = PostTable(report.Results, top);
        Report(model.Name, scope, ras, report, report.Results, rating == PostFaultRating.RateA ? null : rating.ToString());
        return Verdict(report.BaseCase, failing + report.ExcludedSingleOutages.Count);
    }

    private static void Header(RasSet ras, PostFaultRating rating)
    {
        Console.WriteLine(string.Create(Inv, $"Remedial action schemes: {ras.Schemes.Count}, at most {ras.MaxLevels} level(s) per contingency (steady state, no time delays)."));
        if (rating != PostFaultRating.RateA)
            Console.WriteLine($"Post-fault loading judged against rating {(rating == PostFaultRating.RateB ? "B (short-term)" : "C (emergency)")}, "
                + "rating A where a branch has none; the base case against rating A (NETS SQSS v2.10 2.9.1 / 5.1.8).");
        Console.WriteLine(new string('=', 74));
    }

    private static int BaseFailed(string verb, GridModel model, RasBaseCaseException ex)
    {
        Console.Error.WriteLine($"error: {verb}: the base case of {model.Name} did not converge ({ex.Message}), so no outage can be screened against it. Exit 3.");
        return CliExit.Verdict;
    }

    private static void PrintBase(ContingencyResult b)
    {
        if (b.Violations.Count == 0)
            Console.WriteLine("Base case: secure (no violation before any outage).");
        else
            Console.WriteLine($"Base case: {b.Violations.Count} pre-existing violation(s), reported once here and not counted against " +
                $"any outage: {string.Join("; ", b.Violations)}");
    }

    // The screen's own table, before any scheme.
    private static void PreTable(IReadOnlyList<ContingencyResult> rows, int top)
    {
        Console.WriteLine("Before schemes:");
        Console.WriteLine("Outage                          Status        MaxLoad  WorstBnd  Vmin   Vmax");
        Console.WriteLine(new string('-', 74));
        int insecure = 0, islanded = 0, notSolved = 0, shown = 0;
        foreach (ContingencyResult c in rows)
        {
            if (!c.Converged) { if (c.Islanded) islanded++; else notSolved++; }
            else if (!c.Secure) insecure++;
            if (shown++ >= top) continue;
            Console.WriteLine(Row(c));
            Lost(c);
        }
        Console.WriteLine(new string('-', 74));
        if (rows.Count > top) Console.WriteLine(string.Create(Inv, $"(showing worst {top} of {rows.Count})"));
        Console.WriteLine(string.Create(Inv, $"{rows.Count} outages screened  {rows.Count - insecure - islanded - notSolved} secure  {insecure} with violations  {islanded} islanded")
            + (notSolved > 0 ? string.Create(Inv, $"  {notSolved} not solved") : ""));
        BaseOnlyNote(rows, "");
    }

    // F20, as the screen prints it: what a generator or bus outage took with it, and any reference move the re-solve
    // needed. --ras runs with the reference dispatch only, so lost generation is picked up by the reference.
    private static void Lost(ContingencyResult c)
    {
        if (c.Kind != OutageKind.Branch && (c.LostLoadMw != 0 || c.LostGenerationMw != 0 || c.Note is not null))
        {
            string load = c.LostLoadMw != 0 ? string.Create(Inv, $"lost load {c.LostLoadMw:F1} MW  ") : "";
            string gen = c.LostGenerationMw != 0 ? string.Create(Inv, $"lost generation {c.LostGenerationMw:F1} MW (picked up by the reference)  ") : "";
            Console.WriteLine(("    " + load + gen + (c.Note ?? "")).TrimEnd());
        }
    }

    // D03 and GR-125, as the screen prints them: the 'base only' count, and every secure row that deepened a base-case
    // violation, so SECURE (before or after schemes) never hides a worsened overload.
    private static void BaseOnlyNote(IReadOnlyList<ContingencyResult> rows, string when)
    {
        int baseOnly = rows.Count(c => c.Secure && c.PreExistingViolations.Count > 0);
        if (baseOnly > 0)
            Console.WriteLine(string.Create(Inv, $"({baseOnly} of the secure outages{when} are 'base only': they add no violation, but the base case's own ") +
                "violations remain in their state)");
        foreach (ContingencyResult c in rows.Where(c => c.Secure && c.WorsenedPreExistingViolations.Count > 0))
            Console.WriteLine($"  {c.Outage}: makes base-case violation(s) worse{when}: {string.Join("; ", c.WorsenedPreExistingViolations)}");
    }

    // The schemes per contingency and the state they leave. Returns the number of contingencies not secure after them.
    private static int PostTable(IReadOnlyList<RasContingencyResult> rows, int top)
    {
        Console.WriteLine("After schemes:");
        Console.WriteLine("Outage                          Level  Status after schemes      MaxLoad  Vmin   Vmax   Schemes operated");
        Console.WriteLine(new string('-', 100));
        int shown = 0;
        var counts = new int[4];
        foreach (RasContingencyResult r in rows)
        {
            counts[(int)r.Status]++;
            if (shown++ >= top) continue;
            ContingencyResult p = r.PostRas;
            string numbers = p.Converged ? string.Create(Inv, $"{p.MaxBranchLoadingPercent,5:F0}%  {p.MinVm:F3}  {p.MaxVm:F3}") : $"{"-",6}  {"-",5}  {"-",5}";
            Console.WriteLine(string.Create(Inv, $"{Trunc(r.PreRas.Outage, 30),-30}  {r.LastLevel,5}  {RasStatusText.Of(r.Status),-24}  {numbers}  {(r.Levels.Count == 0 ? "none" : string.Join(", ", r.Fired))}"));
            foreach (RasActionRecord a in r.Actions)
                Console.WriteLine(string.Create(Inv, $"    level {a.Level} {a.Scheme} action {a.Action} {RasStatusText.Of(a.Kind)} {a.Element}: {a.Detail}"));
            if (r.Note is not null) Console.WriteLine($"    {r.Note}");
            Lost(p);
            if (r.Status == RasStatus.ViolationsAfterRas) Console.WriteLine($"    violations after schemes: {string.Join("; ", p.Violations)}");
        }
        Console.WriteLine(new string('-', 100));
        if (rows.Count > top) Console.WriteLine(string.Create(Inv, $"(showing worst {top} of {rows.Count})"));
        Console.WriteLine(string.Create(Inv,
            $"{rows.Count} outages: {counts[(int)RasStatus.SecureAfterRas]} secure after schemes  {counts[(int)RasStatus.ViolationsAfterRas]} with violations  "
            + $"{counts[(int)RasStatus.NotConvergedAfterRas]} not converged  {counts[(int)RasStatus.RasLoop]} RAS loop"));
        BaseOnlyNote(rows.Where(r => r.Status == RasStatus.SecureAfterRas).Select(r => r.PostRas).ToList(), " after schemes");
        return rows.Count - counts[(int)RasStatus.SecureAfterRas];
    }

    private static int Verdict(ContingencyResult b, int failing)
    {
        if (b.Violations.Count == 0 && failing == 0)
        {
            Console.WriteLine("Verdict: SECURE after remedial action schemes (exit 0).");
            return CliExit.Ok;
        }
        string why = b.Violations.Count > 0 && failing > 0 ? $"{b.Violations.Count} base-case violation(s) and {failing} contingency(ies) not secure after schemes"
            : b.Violations.Count > 0 ? $"{b.Violations.Count} base-case violation(s)"
            : $"{failing} contingency(ies) not secure after schemes";
        Console.WriteLine($"Verdict: NOT SECURE, {why} (exit 3).");
        return CliExit.Verdict;
    }

    private static string Row(ContingencyResult c) => c.Converged
        ? string.Create(Inv, $"{Trunc(c.Outage, 30),-30}  {Status(c),-12}  {c.MaxBranchLoadingPercent,5:F0}%   {c.WorstBoundaryLoadingPercent,5:F0}%  {c.MinVm:F3}  {c.MaxVm:F3}")
        : $"{Trunc(c.Outage, 30),-30}  {Status(c),-12}  {c.Violations[0]}";

    // The status words of the contingency tables (as the screen prints them).
    private static string Status(ContingencyResult c) =>
        !c.Converged ? (c.Islanded ? "ISLANDED" : "NO SOLUTION")
        : !c.Secure ? string.Create(Inv, $"VIOLATIONx{c.Violations.Count}")
        : c.PreExistingViolations.Count > 0 ? "base only"
        : "secure";

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "...";

    // The result file (--json / --csv / --out): the screen's fields, the scheme columns and one row per action.
    private static void Report(string caseName, string scope, RasSet ras, RasScreenReport report, IReadOnlyList<RasContingencyResult> rows,
        string? postFaultRating)
    {
        CliResult? rep = CliOutput.Report;
        if (rep is null) return;
        ContingencyResult b = report.BaseCase;
        var pre = rows.Select(r => r.PreRas).ToList();
        int failing = rows.Count(r => r.Status != RasStatus.SecureAfterRas) + report.ExcludedSingleOutages.Count;
        if (postFaultRating is not null) rep.Set("post_fault_rating", postFaultRating);
        rep.Set("case", caseName).Set("scope", scope)
            .Set("base_case_violations", b.Violations.Count).Set("base_case_violation_list", string.Join("; ", b.Violations))
            .Set("screened", pre.Count)
            .Set("secure", pre.Count(c => c.Secure)).Set("with_violations", pre.Count(c => c.Converged && !c.Secure))
            .Set("islanded", pre.Count(c => !c.Converged && c.Islanded)).Set("not_solved", pre.Count(c => !c.Converged && !c.Islanded))
            .Set("base_only", pre.Count(c => c.Secure && c.PreExistingViolations.Count > 0))
            .Set("excluded_single_outages", report.ExcludedSingleOutages.Count)
            .Set("ras_schemes", ras.Schemes.Count).Set("ras_max_levels", ras.MaxLevels)
            .Set("secure_after_ras", rows.Count(r => r.Status == RasStatus.SecureAfterRas))
            .Set("violations_after_ras", rows.Count(r => r.Status == RasStatus.ViolationsAfterRas))
            .Set("not_converged_after_ras", rows.Count(r => r.Status == RasStatus.NotConvergedAfterRas))
            .Set("ras_loop", rows.Count(r => r.Status == RasStatus.RasLoop))
            .Set("verdict", b.Violations.Count == 0 && failing == 0 ? "SECURE" : "NOT SECURE");
        CliResultTable outages = rep.Table("outages", "outage", "status", "converged", "max_loading_pct", "worst_boundary_pct", "vmin_pu", "vmax_pu",
            "new_violations", "violations", "pre_existing_violations",
            "ras_fired", "ras_level", "post_ras_status", "post_ras_max_loading_pct", "post_ras_vmin_pu", "post_ras_vmax_pu", "post_ras_violations");
        foreach (RasContingencyResult r in rows)
        {
            ContingencyResult c = r.PreRas, p = r.PostRas;
            outages.Row(c.Outage, Status(c), c.Converged,
                c.Converged ? c.MaxBranchLoadingPercent : null, c.Converged ? c.WorstBoundaryLoadingPercent : null,
                c.Converged ? c.MinVm : null, c.Converged ? c.MaxVm : null,
                c.Converged ? c.Violations.Count : null, string.Join("; ", c.Violations), string.Join("; ", c.PreExistingViolations),
                string.Join("; ", r.Fired), r.LastLevel, RasStatusText.Of(r.Status),
                p.Converged ? p.MaxBranchLoadingPercent : null, p.Converged ? p.MinVm : null, p.Converged ? p.MaxVm : null,
                string.Join("; ", p.Violations));
        }
        if (postFaultRating is not null)
        {
            CliResultTable kinds = rep.Table("outage_details", "outage", "kind", "lost_load_mw", "lost_generation_mw", "note");
            foreach (ContingencyResult c in pre) kinds.Row(c.Outage, c.Kind.ToString(), c.LostLoadMw, c.LostGenerationMw, c.Note ?? "");
        }
        CliResultTable ex = rep.Table("excluded_single_outages", "outage", "reason");
        foreach (ContingencyResult c in report.ExcludedSingleOutages) ex.Row(c.Outage, c.Violations.Count > 0 ? c.Violations[0] : "");
        CliResultTable acts = rep.Table("ras_actions", "outage", "level", "scheme", "action", "kind", "element", "applied", "detail");
        foreach (RasContingencyResult r in rows)
            foreach (RasActionRecord a in r.Actions)
                acts.Row(r.PreRas.Outage, a.Level, a.Scheme, a.Action, RasStatusText.Of(a.Kind), a.Element, a.Applied, a.Detail);
        rep.Complete();
    }
}
