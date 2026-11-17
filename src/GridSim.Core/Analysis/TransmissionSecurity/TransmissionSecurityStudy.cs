using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.TransmissionSecurity;

public sealed record BranchOutage(int Index, int From, int To);
public sealed record TransmissionSecurityRequest(
    IReadOnlyList<BranchOutage> Outages, double MinPu, double MaxPu,
    PostFaultRating PostFaultRating, int MaxRounds = 50, double[]? Costs = null);
/// <summary>One checked quantity. Margins are unrounded (upper: limit - observed; lower: observed - limit).
/// <see cref="Tolerance"/> is the declared numerical tolerance of the observation in its own units: zero for
/// quantities read directly from the solved state, and the Newton bus mismatch times base MVA for unit outputs
/// recovered from converged bus injections. A breach is a margin below minus that tolerance.</summary>
public sealed record LimitEvidence(string Kind, int Index, string Units, double Observed,
    double? Lower, double? Upper, double? LowerMargin, double? UpperMargin, string? Source = null, double Tolerance = 0)
{
    public bool Violated => LowerMargin < -Tolerance || UpperMargin < -Tolerance;
}
public sealed record SecurityState(int? OutageIndex, string Disposition, string? Reason,
    PowerFlowStatus? SolverStatus, double? ResidualPu, double? LossMw,
    IReadOnlyList<LimitEvidence> Limits, IReadOnlyList<BusType>? SolvedBusTypes);
public sealed record TransmissionSecurityResult(
    string Status, string Outcome, ScopfResult? Dc, IReadOnlyList<ScopfDispatch> CandidateDispatch,
    SecurityState BaseCase, IReadOnlyList<SecurityState> Outages,
    string Controls = "Fixed taps; enforced Q limits; incumbent single slack balances losses.",
    string DcApproximation = "Reactance-only, unity voltage; losses, shunts, taps and phase shifts omitted.");

/// <summary>
/// Additive preventive DC candidate and absolute AC verifier. Every outage starts from
/// the same candidate. Missing coverage dominates a complete violations verdict.
/// </summary>
public static class TransmissionSecurityRunner
{
    public static TransmissionSecurityResult Run(GridModel model, TransmissionSecurityRequest request)
    {
        Validate(model, request);
        string? unsupported = Unsupported(model);
        if (unsupported is not null) return Unevaluated(request, "Incomplete", unsupported);
        var factors = DistributionFactors.Build(model);
        // An islanding outage cannot enter the preventive DC problem (DcScopf refuses it). It is excluded from the
        // DC contingency set, keeps an explicit ISLANDED row, and makes the study Incomplete; the remaining
        // outages are still secured and verified so their evidence is reported.
        var islanding = request.Outages.Where(o => factors.OutageIslands(o.Index)).Select(o => o.Index).ToHashSet();
        ScopfResult dc;
        try
        {
            dc = DcScopf.Solve(model, new ScopfOptions
            {
                Contingencies = request.Outages.Where(o => !islanding.Contains(o.Index)).Select(o => o.Index).ToArray(),
                PostContingencyRating = request.PostFaultRating,
                MaxRounds = request.MaxRounds, CostPerGen = request.Costs
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            // The request is already validated, so a refusal here is the DC formulation's, not the caller's input:
            // report it as a candidate failure with one row per outage rather than as an input error.
            return Unevaluated(request, "CandidateFailure", "DC candidate could not be formed: " + ex.Message, null, islanding);
        }
        if (!dc.Optimal) return Unevaluated(request, "CandidateFailure", dc.Message, dc, islanding);
        if (dc.Dispatch.Count != model.Generators.Count ||
            dc.Dispatch.Select(d => d.Index).Distinct().Count() != model.Generators.Count ||
            dc.Dispatch.Any(d => d.Index < 0 || d.Index >= model.Generators.Count ||
                model.Generators[d.Index].Bus != d.Bus || !double.IsFinite(d.Pmw)))
            return Unevaluated(request, "Incomplete", "Incomplete or inconsistent DC dispatch.", dc, islanding);
        Generator[] generators = model.Generators.ToArray();
        foreach (ScopfDispatch dispatch in dc.Dispatch)
            generators[dispatch.Index] = generators[dispatch.Index] with { Pg = dispatch.Pmw };
        GridModel candidate = model.CopyWith(generators: generators);
        SecurityState intact = Verify(candidate, request, null);
        SecurityState[] outages = request.Outages.Select(o =>
        {
            if (islanding.Contains(o.Index))
                return Missing(o.Index, "Required outage islands the network; excluded from the DC candidate and not verified.", "ISLANDED");
            Branch[] branches = candidate.Branches.ToArray();
            branches[o.Index] = branches[o.Index] with { InService = false };
            return Verify(candidate.CopyWith(branches: branches), request, o.Index);
        }).ToArray();
        SecurityState[] states = [intact, .. outages];
        string outcome = states.Any(s => s.Disposition is "INCOMPLETE" or "NO SOLUTION" or "ISLANDED") ? "Incomplete"
            : states.Any(s => s.Limits.Any(l => l.Violated)) ? "Violations" : "Verified";
        return new(outcome == "Verified" ? "SECURE" : "NOT SECURE", outcome, dc, dc.Dispatch, intact, outages);
    }

    /// <summary>
    /// RC3 P5 seam: the same absolute AC verification <see cref="Run"/> applies to each of its states, for a study
    /// that builds its own sequence of states (the corrective N-1-1 runner). <paramref name="outageIndex"/> null checks
    /// branch MVA against RATE_A (intact); otherwise against <paramref name="rating"/> with the established fallback to A,
    /// and the index is recorded as the row's outage. Behaviour is exactly that of the preventive study's verifier.
    /// </summary>
    public static SecurityState VerifyState(GridModel model, double minPu, double maxPu, PostFaultRating rating, int? outageIndex)
        => Verify(model, new TransmissionSecurityRequest([], minPu, maxPu, rating), outageIndex);

    /// <summary>RC3 P5 seam: why the intact model is outside the preventive study's supported envelope, or null.</summary>
    public static string? EnvelopeRefusal(GridModel model) => Unsupported(model);

    private static void Validate(GridModel model, TransmissionSecurityRequest request)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(request.MinPu) || !double.IsFinite(request.MaxPu) ||
            request.MinPu <= 0 || request.MaxPu < request.MinPu)
            throw new ArgumentException("Voltage bounds must be finite, positive and ordered.");
        if (request.MaxRounds < 1 || !Enum.IsDefined(request.PostFaultRating))
            throw new ArgumentException("Invalid DC round cap or post-fault rating.");
        if (model.Generators.Any(g => double.IsNaN(g.Pmin) || double.IsNaN(g.Pmax) ||
            double.IsNaN(g.Qmin) || double.IsNaN(g.Qmax) || g.Pmin > g.Pmax || g.Qmin > g.Qmax))
            throw new ArgumentException("Generator bounds must be ordered and cannot be NaN.");
        if (request.Outages is null || request.Outages.Count == 0)
            throw new ArgumentException("At least one explicit branch outage is required.");
        var seen = new HashSet<int>();
        foreach (BranchOutage outage in request.Outages)
        {
            if (outage.Index < 0 || outage.Index >= model.Branches.Count || !seen.Add(outage.Index))
                throw new ArgumentException("Outage indices must be unique and within the model.");
            Branch branch = model.Branches[outage.Index];
            if (!branch.InService || branch.From != outage.From || branch.To != outage.To)
                throw new ArgumentException($"Outage {outage.Index} is inactive or its endpoints do not match.");
        }
        if (request.Costs is not null && (request.Costs.Length != model.Generators.Count ||
            request.Costs.Any(c => !double.IsFinite(c))))
            throw new ArgumentException("Costs must contain one finite value per active generator.");
    }

    internal static string? Unsupported(GridModel model)
    {
        if (model.HasCoupledDevices || model.TransformerFlowControls.Count > 0 || model.Boundaries.Count > 0)
            return "Coupled devices, transformer flow controls or boundary limits require additional evidence.";
        if (model.Buses.Any(b => b.Zip is not null))
            return "ZIP demand is outside the initial DC candidate envelope.";
        if (DistributionFactors.FindIslands(model).Count != 1)
            return "The intact model must be connected.";
        if (model.Buses.Count(b => b.Type == BusType.Slack) != 1)
            return "Exactly one explicit slack bus is required.";
        if (model.Branches.Any(b => b.InService && (!double.IsFinite(b.RateAMva) || b.RateAMva <= 0)))
            return "Every live branch needs a positive finite RateA for intact and DC coverage.";
        return null;
    }

    /// <summary>The absolute AC verifier for one state. <paramref name="observe"/> (optional, used by the transfer
    /// study) receives the raw solved result of this same solve, so no state is solved twice.</summary>
    internal static SecurityState Verify(GridModel model, TransmissionSecurityRequest request, int? outage,
        Action<PowerFlowResult>? observe = null)
    {
        PowerFlowResult solved;
        try
        {
            solved = SolverSelector.CreateNewton(model).Solve(model, ContingencyAnalysis.StudyOptions);
        }
        catch (Exception ex) when (ex is ArithmeticException or InvalidOperationException or ArgumentException)
        {
            return Missing(outage, ex.Message, "NO SOLUTION");
        }
        observe?.Invoke(solved);
        int count = model.Buses.Count;
        double unitTolerance = ContingencyAnalysis.StudyOptions.Tolerance * model.BaseMva;
        bool complete = solved.BusIds.SequenceEqual(model.Buses.Select(b => b.Id)) &&
            solved.Vm.Length == count && solved.VaDeg.Length == count &&
            solved.PInjMw.Length == count && solved.QInjMvar.Length == count &&
            solved.LineFlows.Count == model.Branches.Count &&
            solved.SolvedBusTypes?.Count == count;
        bool finite = solved.Vm.All(double.IsFinite) && solved.VaDeg.All(double.IsFinite) &&
            solved.PInjMw.All(double.IsFinite) && solved.QInjMvar.All(double.IsFinite) &&
            double.IsFinite(solved.MaxMismatch) && double.IsFinite(solved.TotalLossMw);
        if (solved.MaxMismatch > ContingencyAnalysis.StudyOptions.Tolerance || !solved.Converged || solved.Status != PowerFlowStatus.Converged || !complete || !finite)
            return new(outage, "NO SOLUTION", "Convergence or complete finite AC evidence unavailable.",
                solved.Status, Finite(solved.MaxMismatch), Finite(solved.TotalLossMw), [], solved.SolvedBusTypes);

        var limits = new List<LimitEvidence>();
        var missing = new List<string>();
        for (int i = 0; i < count; i++)
        {
            Bus bus = model.Buses[i];
            limits.Add(Limit("Voltage", bus.Id, "pu", solved.Vm[i], request.MinPu, request.MaxPu));
            int[] units = Enumerable.Range(0, model.Generators.Count).Where(g => model.Generators[g].Bus == bus.Id).ToArray();
            if (units.Length > 1)
            {
                missing.Add($"Bus {bus.Id}: individual realised generator allocation is unavailable.");
                continue;
            }
            if (units.Length == 0) continue;
            int index = units[0];
            Generator generator = model.Generators[index];
            // At convergence, the returned Y-bus injection equals generation minus modeled demand.
            // The bus-shunt admittance is already in YBus and therefore in this injection; adding
            // Gs/Bs again would double count it. With one unit and no ZIP/coupled devices, adding
            // constant demand recovers that unit's output exactly.
            limits.Add(Limit("GeneratorP", index, "MW", solved.PInjMw[i] + bus.Pd, generator.Pmin, generator.Pmax)
                with { Tolerance = unitTolerance });
            limits.Add(Limit("GeneratorPAdjustment", index, "MW",
                solved.PInjMw[i] + bus.Pd - generator.Pg, null, null));
            limits.Add(Limit("GeneratorQ", index, "MVAr", solved.QInjMvar[i] + bus.Qd, generator.Qmin, generator.Qmax)
                with { Tolerance = unitTolerance });
        }
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch branch = model.Branches[i];
            LineFlow flow = solved.LineFlows[i];
            if (flow.From != branch.From || flow.To != branch.To)
            {
                missing.Add($"Branch {i}: solved identity mismatch.");
                continue;
            }
            if (!branch.InService) continue;
            double rating = outage is null ? branch.RateAMva : DcScopf.PostRating(branch, request.PostFaultRating);
            double from = Math.Sqrt(flow.PFromMw * flow.PFromMw + flow.QFromMvar * flow.QFromMvar);   // DetMath rule: no Hypot
            double to = Math.Sqrt(flow.PToMw * flow.PToMw + flow.QToMvar * flow.QToMvar);
            if (!double.IsFinite(from) || !double.IsFinite(to) || !double.IsFinite(rating) || rating <= 0)
            {
                missing.Add($"Branch {i}: finite terminal flows or rating unavailable.");
                continue;
            }
            string source = outage is null ? "A" : RatingSource(branch, request.PostFaultRating);
            limits.Add(Limit("BranchFrom", i, "MVA", from, null, rating) with { Source = source });
            limits.Add(Limit("BranchTo", i, "MVA", to, null, rating) with { Source = source });
        }
        if (limits.Any(l => !double.IsFinite(l.Observed)))
            return new(outage, "INCOMPLETE", "Non-finite realised measurement.", solved.Status,
                solved.MaxMismatch, solved.TotalLossMw, limits.Where(l => double.IsFinite(l.Observed)).ToArray(), solved.SolvedBusTypes);
        string disposition = missing.Count > 0 ? "INCOMPLETE"
            : limits.Any(l => l.Violated) ? $"VIOLATIONx{limits.Count(l => l.Violated)}" : "SECURE";
        return new(outage, disposition, missing.Count == 0 ? null : string.Join(" ", missing),
            solved.Status, solved.MaxMismatch, solved.TotalLossMw, limits, solved.SolvedBusTypes);
    }

    /// <summary>The label of the rating <see cref="DcScopf.PostRating"/> selects for <paramref name="rating"/>: A, B or C
    /// when that rating is given (positive), otherwise "fallback A". The single definition every RC3 study reports.</summary>
    internal static string RatingSource(Branch branch, PostFaultRating rating) =>
        rating == PostFaultRating.RateA ? "A"
        : rating == PostFaultRating.RateB && branch.RateBMva > 0 ? "B"
        : rating == PostFaultRating.RateC && branch.RateCMva > 0 ? "C" : "fallback A";

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
    private static LimitEvidence Limit(string kind, int index, string units, double value, double? lower, double? upper)
    {
        double? lo = lower.HasValue ? Finite(lower.Value) : null;
        double? hi = upper.HasValue ? Finite(upper.Value) : null;
        return new(kind, index, units, value, lo, hi, lo.HasValue ? value - lo : null, hi.HasValue ? hi - value : null);
    }
    internal static SecurityState Missing(int? index, string reason, string disposition = "UNEVALUATED")
        => new(index, disposition, reason, null, null, null, [], null);
    private static TransmissionSecurityResult Unevaluated(TransmissionSecurityRequest request, string outcome,
        string reason, ScopfResult? dc = null, IReadOnlySet<int>? islanding = null) => new("NOT SECURE", outcome, dc, [],
            Missing(null, reason), request.Outages.Select(o => islanding?.Contains(o.Index) == true
                ? Missing(o.Index, "Required outage islands the network; excluded from the DC candidate and not verified.", "ISLANDED")
                : Missing(o.Index, reason)).ToArray());
}
