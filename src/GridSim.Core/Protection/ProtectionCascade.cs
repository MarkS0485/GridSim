using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Protection;

/// <summary>One rung of a protection cascade: the relay that operates, the branch it opens, and the state
/// of the network after that branch is removed and the flow re-solved.</summary>
public sealed record CascadeStep(
    int Order,
    string Device,
    string Function,
    string BranchName,
    int From,
    int To,
    double? TripTimeS,
    string Reason,
    bool ResolveConverged,
    int OverloadedBranchesAfter);

/// <summary>The propagation of a protection event through the network: the ordered trip sequence and why
/// it stopped.</summary>
public sealed record CascadeResult(IReadOnlyList<CascadeStep> Steps, bool FinalConverged, string StopReason)
{
    public bool Cascaded => Steps.Count > 0;
}

/// <summary>
/// A passive, read-only "what-if" cascade: it does NOT touch the caller's solve. Starting from a prefault
/// state it repeatedly assesses the protection overlay, trips the branch element that would operate FIRST
/// (shortest inverse-time), opens that branch in a COPY of the model, re-solves, and re-assesses - so a
/// single overload can be followed as it propagates ("relay 51 on A opens it -> B overloads -> relay 49 on
/// B opens it -> the system splits"). It stops when nothing else would trip, the re-solve fails (a split /
/// blackout - the honest terminal state), or a step cap is hit.
///
/// This is the propagation counterpart of the single-state assessment: same relay library, chained through
/// topology. Branch-tripping elements only (thermal 49, overcurrent 50/51, distance 21); voltage/frequency
/// elements shed or trip infeed rather than open a line and are left to the active layer.
/// </summary>
public static class ProtectionCascade
{
    /// <summary>Default cap on cascade steps (trip, re-solve, re-evaluate); reaching it is reported as
    /// "still cascading", never as a settled outcome (R193).</summary>
    public const int DefaultMaxSteps = 12;

    /// <summary>Newton iteration cap of each post-trip re-solve (generous: a post-trip network can start far
    /// from its new operating point) (R193).</summary>
    public const int ResolveMaxIterations = 100;

    public static CascadeResult Simulate(
        GridModel model, PowerFlowResult prefault, ProtectionSettings? settings = null, int maxSteps = DefaultMaxSteps)
    {
        prefault.RequireConverged("Protection cascade");   // the convergence contract (October 2026, WS4)
        ProtectionSettings s = settings ?? new ProtectionSettings();
        var steps = new List<CascadeStep>();
        GridModel current = model;
        PowerFlowResult state = prefault;

        for (int step = 0; step < maxSteps; step++)
        {
            ProtectionReport rep = ProtectionAssessment.Assess(current, state, s);

            // The first branch element to operate in real time (shortest trip time; instantaneous first).
            RelayVerdict? next = NextBranchTrip(rep);

            if (next is null)
                return new CascadeResult(steps, state.Converged, steps.Count == 0 ? "stable - no branch relay would operate" : "settled - no further branch trips");

            // Resolve which branch this verdict sits on and open it in a copy of the model.
            int branchIdx = FindBranch(current, next);
            if (branchIdx < 0)
                return new CascadeResult(steps, state.Converged, "internal: could not resolve the tripped branch");

            Branch br = current.Branches[branchIdx];
            var opened = current.Branches.Select((b, i) => i == branchIdx ? b with { InService = false } : b).ToList();
            GridModel afterTrip = WithBranches(current, opened);

            PowerFlowResult resolved = Resolve(afterTrip);
            int overloadsAfter = resolved.Converged
                ? resolved.LineFlows.Count(f => f.RatingMva > 0 && f.LoadingPercent > s.ThermalPickupFraction * 100.0)
                : 0;

            steps.Add(new CascadeStep(step + 1, next.Device, next.Function,
                br.Name ?? $"branch {br.From}-{br.To}", br.From, br.To, next.TimeToTripS, next.Basis,
                resolved.Converged, overloadsAfter));

            if (!resolved.Converged)
                return new CascadeResult(steps, false,
                    $"non-convergent after opening {br.Name ?? $"{br.From}-{br.To}"} - the network has split / collapsed (blackout)");

            current = afterTrip;
            state = resolved;
        }

        return new CascadeResult(steps, state.Converged, $"reached the {maxSteps}-step cap (still cascading)");
    }

    /// <summary>
    /// The branch-tripping element that operates FIRST on this assessed state, or null when none operates.
    /// <list type="number">
    /// <item>Only operating elements on a branch whose device opens that branch (49, 50, 51, the 21 zones).</item>
    /// <item>Shortest operating time first (instantaneous = 0). An element without a time is treated as
    /// instantaneous, exactly as before this change (none of the four branch-tripping devices reports an
    /// operating element without a time in practice; the rule is kept so no existing case moves).</item>
    /// <item>N277 (October 2026, LF2; the N270 determinism rule, Ruling R1): elements with EQUAL operating times are
    /// taken in ROW INDEX order, their position in <see cref="ProtectionReport.Verdicts"/> (relay registry order,
    /// then each relay's own branch order). This is the same tie-break <see cref="ProtectionReport.Operating"/> uses
    /// since N270.</item>
    /// </list>
    /// Why not the margin (the old tie-break, largest raw margin first): two elements with the same operating time
    /// open their breakers at the same instant, so the margin carries no physical "who is first" information between
    /// them. It is also in each element's own unit (multiples of rated current for 50 / 51, fraction of RateA for 49,
    /// pu impedance for 21), so a 50 against a 21-Z1 compared numbers of different kinds; and for a bolted fault the
    /// distance margins of several branches are the SAME number in exact arithmetic, so the pick rested on the last
    /// bit of each margin and could move whenever upstream rounding moved (the N233 fault-case14 lesson). A real
    /// cascade engine needs a deterministic, rounding-free rule for simultaneous trips; PSS/E and PowerFactory process
    /// simultaneous events in their event-list (input) order, which is what the row index is here. Elements whose
    /// operating times differ are picked exactly as before. Only WHICH of several simultaneous trips is taken first can
    /// change, and with it the cascade's later re-solves.
    /// </summary>
    internal static RelayVerdict? NextBranchTrip(ProtectionReport rep)
    {
        ArgumentNullException.ThrowIfNull(rep);
        RelayVerdict? best = null;
        double bestT = 0.0;
        // One forward pass in row order. A later row replaces the current best ONLY when it is STRICTLY faster, so
        // among equal times the earliest row is kept: that is the N277 tie-break by row index, with no sort needed.
        // The time comparison is exact (no tolerance): two times that differ in the last bit are different times,
        // which is the same rule ProtectionReport.Operating applies. It uses double.CompareTo, the comparer the old
        // OrderBy used, so even a non-finite time (none is produced today) orders exactly as before.
        for (int row = 0; row < rep.Verdicts.Count; row++)
        {
            RelayVerdict v = rep.Verdicts[row];
            if (!v.Operate) continue;   // ProtectionReport.Operating holds exactly the operating rows
            if (v.LocationKind != ProtectionLocation.Branch || !IsBranchTrip(v.Device)) continue;
            double t = v.TimeToTripS ?? 0.0;   // untimed counts as instantaneous, unchanged from before N277
            if (best is null || t.CompareTo(bestT) < 0)
            {
                best = v;
                bestT = t;
            }
        }
        return best;
    }

    private static bool IsBranchTrip(string device) =>
        device is "49" or "50" or "51" || device.StartsWith("21", StringComparison.Ordinal);

    private static int FindBranch(GridModel model, RelayVerdict v)
    {
        // The verdict already carries the EXACT branch index (relays set
        // LocationId = the flow index, kept aligned with Branches). Matching
        // by display name opened the WRONG parallel circuit whenever
        // Branch.Name is null (MATPOWER: both circuits render "branch F-T").
        if (v.LocationKind == ProtectionLocation.Branch
            && v.LocationId >= 0 && v.LocationId < model.Branches.Count
            && model.Branches[v.LocationId].InService)
            return v.LocationId;
        // Fallback for verdicts without a usable index: match by name.
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch b = model.Branches[i];
            if (!b.InService) continue;
            if ((b.Name ?? $"branch {b.From}-{b.To}") == v.LocationName) return i;
        }
        return -1;
    }

    /// <summary>
    /// N299 (October 2026, stream P): TEST ORACLE ONLY. While a test holds <see cref="FullSolveReferenceScope"/> on its
    /// own async flow, every post-trip re-solve on that flow is handed to the Newton engine even when the tripped network
    /// provably cannot converge (the pre-N299 path), so a test can compare the cascade with and without the N232 short
    /// cut, field for field. An AsyncLocal, so tests running in parallel never see each other's setting. Production code
    /// never sets it (default false: the short cut is taken).
    /// </summary>
    private static readonly AsyncLocal<bool> FullSolveReferenceFlow = new();

    /// <summary>N299: re-solve every post-trip network in full on the current async flow until the returned scope is
    /// disposed (tests only; see <see cref="FullSolveReferenceFlow"/>). The previous setting is restored on dispose.</summary>
    internal static IDisposable FullSolveReferenceScope()
    {
        bool previous = FullSolveReferenceFlow.Value;
        FullSolveReferenceFlow.Value = true;
        return new RestoreFullSolveFlow(previous);
    }

    private sealed class RestoreFullSolveFlow(bool previous) : IDisposable
    {
        public void Dispose() => FullSolveReferenceFlow.Value = previous;
    }

    private static PowerFlowResult Resolve(GridModel m)
    {
        IPowerFlowSolver solver = SolverSelector.CreateNewton(m);   // the shared dense/sparse rule (was a local 3,000)
        var options = new PowerFlowOptions { MaxIterations = ResolveMaxIterations, EnforceQLimits = true };

        // N299 (October 2026, stream P; the N232 short cut, LF2, applied to the cascade): a trip that leaves a part of the
        // network with no reference bus and a real-power balance that part can never meet cannot converge, whatever the
        // engine does (the proof is on ContingencyAnalysis.ProvablyUnsolvablePart: Q-limit passes change Q equations,
        // never a P specification or a resistance, so EnforceQLimits does not touch it). Before N299 the solve ran anyway:
        // the direct LU met the singular Jacobian and the ILU0 / BiCGSTAB fallback ran to its iteration cap (about 6 s per
        // such state on SyntheticGb) before the engine gave up.
        //
        // Bit-identity of the cascade: Simulate reads only resolved.Converged from a re-solve that did not converge (it
        // records ResolveConverged = false, 0 overloads, and stops with the fixed "non-convergent after opening ..."
        // reason; the result object itself is dropped). A thrown solve already maps to NonConverged(m) below. So handing
        // back NonConverged(m) for a provably unsolvable state gives every step and the stop reason exactly as the full
        // solve did. Every other state takes the unchanged path (the check only reads the model). Only GridSim's own
        // Newton engines are covered (always true here: SolverSelector.CreateNewton builds one), as in the screens.
        if (!FullSolveReferenceFlow.Value
            && ContingencyAnalysis.IsBuiltInNewton(solver)
            && ContingencyAnalysis.ProvablyUnsolvablePart(m, options.Tolerance) is not null)
            return NonConverged(m);

        // Only a NUMERICAL failure of the re-solve (a singular or non-finite system, raised as InvalidOperationException
        // or ArithmeticException) is a non-convergent state; any other exception is a defect and is not dressed up as a
        // physical "split / blackout" verdict.
        try { return solver.Solve(m, options); }
        catch (Exception ex) when (ex is InvalidOperationException or ArithmeticException) { return NonConverged(m); }
    }

    private static PowerFlowResult NonConverged(GridModel m) => new()
    {
        Converged = false,
        Iterations = 0,
        MaxMismatch = double.NaN,
        Status = PowerFlowStatus.NonFinite,
        BusIds = m.Buses.Select(b => b.Id).ToArray(),
        Vm = new double[m.BusCount],
        VaDeg = new double[m.BusCount],
        PInjMw = new double[m.BusCount],
        QInjMvar = new double[m.BusCount],
        TotalLossMw = 0,
        TotalLossMvar = 0,
        LineFlows = new List<LineFlow>(),
    };

    // D-DOC-206 (October 2026): through GridModel.CopyWith, so the copy carries the WHOLE fleet. It listed
    // m.Generators (the in-service units only, F16), so every out-of-service unit vanished from the post-trip model.
    // The engines read Generators, which is the very same list as before, so no number of the cascade changes.
    private static GridModel WithBranches(GridModel m, IReadOnlyList<Branch> branches) => m.CopyWith(branches: branches);
}
