using System.Text.Json;
using GridSim.Core.Analysis.Restoration;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P8 restoration package (<see cref="RestorationStudy"/>) on an independent five-bus graph whose admissible orders
/// and optimum are derived by hand and re-derived here by brute force over all 4! orders, independently of the runner.
/// <para>
/// Graph: slack bus 1 (the black-start source); bus 5 (10 MW) is permanently fed by E (1-5). Candidates, all starting
/// open: A = 1-2 (RATE_A 60 MVA, 10 min), B = 2-3 (100 MVA, 10 min), C = 1-3 (100 MVA, 20 min), D = 3-4 (100 MVA, 5 min).
/// Loads: bus 2 40 MW, bus 3 30 MW, bus 4 20 MW, unity power factor. r/x = 0.1, so the AC flows are close to the DC ones.
/// Connectivity: bus 2 live iff A or (B and C); bus 3 live iff C or (A and B); bus 4 live iff D and bus 3 live.
/// Feasibility (hand): A carries 70 or 90 MW radially when A and B are closed without C (above its 60 MVA); with C
/// closed too the loop splits the flow (DC: A carries 36.7 MW, or 43.3 with D), and C alone carries at most 90 of its
/// 100. So a state is admissible iff NOT (A and B closed and C open).
/// Objective: sum over steps of unserved MW before the action times its minutes / 60. Hand optimum A, C, D, B:
/// (90 x 10 + 50 x 20 + 20 x 5 + 0 x 10) / 60 = 2000 / 60 MWh. Without the AC constraint A, B, D, C would give
/// (900 + 500 + 100 + 0) / 60 = 1500 / 60 MWh, the relaxed lower bound; its second state (A and B, no C) overloads A.
/// </para>
/// </summary>
public sealed class RestorationStudyTests
{
    private static readonly string[] Ids = ["A", "B", "C", "D"];
    private static readonly double[] Minutes = [10, 10, 20, 5];

    internal static GridModel Model(bool twoUnitsAtSource = false, bool isolatedBus = false, bool storeDOpen = false)
    {
        var buses = new List<Bus>
        {
            new(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
            new(2, BusType.Pq, Pd: 40, BaseKv: 132),
            new(3, BusType.Pq, Pd: 30, BaseKv: 132),
            new(4, BusType.Pq, Pd: 20, BaseKv: 132),
            new(5, BusType.Pq, Pd: 10, BaseKv: 132),
        };
        if (isolatedBus) buses.Add(new Bus(6, BusType.Pq, Pd: 1, BaseKv: 132));
        var generators = new List<Generator> { new(1, Pg: 0, Vg: 1.0, Qmin: -300, Qmax: 300, Pmin: 0, Pmax: 500) };
        if (twoUnitsAtSource) generators.Add(new Generator(1, Pg: 0, Vg: 1.0, Qmin: -300, Qmax: 300, Pmin: 0, Pmax: 500));
        return new GridModel
        {
            BaseMva = 100,
            Name = "restoration-study",
            Buses = buses,
            Generators = generators,
            Branches =
            [
                new Branch(1, 2, 0.005, 0.05, RateAMva: 60, Name: "A"),
                new Branch(2, 3, 0.005, 0.05, RateAMva: 100, Name: "B"),
                new Branch(1, 3, 0.005, 0.05, RateAMva: 100, Name: "C"),
                new Branch(3, 4, 0.005, 0.05, RateAMva: 100, Name: "D", InService: !storeDOpen),
                new Branch(1, 5, 0.005, 0.05, RateAMva: 100, Name: "E"),
            ],
        };
    }

    internal static RestorationAction[] Actions() =>
    [
        new("A", new RestorationBranch(0, 1, 2), 10),
        new("B", new RestorationBranch(1, 2, 3), 10),
        new("C", new RestorationBranch(2, 1, 3), 20),
        new("D", new RestorationBranch(3, 3, 4), 5),
    ];

    private static RestorationRequest Request(IReadOnlyList<PrecedenceConstraint>? precedence = null, long maxNodes = RestorationStudy.DefaultMaxNodes,
        double minPu = 0.9) => new(Actions(), minPu, 1.1, precedence, maxNodes);

    // ---- The independent oracle: hand connectivity, hand feasibility rule, brute force over every order. ----
    private static bool Has(int mask, int a) => (mask & (1 << a)) != 0;
    private static double HandUnserved(int mask)
    {
        bool a = Has(mask, 0), b = Has(mask, 1), c = Has(mask, 2), d = Has(mask, 3);
        bool bus2 = a || (b && c), bus3 = c || (a && b), bus4 = d && bus3;
        return (bus2 ? 0 : 40) + (bus3 ? 0 : 30) + (bus4 ? 0 : 20);
    }
    private static bool HandAdmissible(int mask) => !(Has(mask, 0) && Has(mask, 1) && !Has(mask, 2));

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length == 0) { yield return []; yield break; }
        for (int i = 0; i < items.Length; i++)
            foreach (int[] rest in Permutations(items.Where((_, k) => k != i).ToArray()))
                yield return [items[i], .. rest];
    }

    private static (string[]? Order, double Cost) BruteForce(bool constrained, (int Before, int After)[] precedence)
    {
        string[]? best = null;
        double bestCost = double.PositiveInfinity;
        foreach (int[] order in Permutations([0, 1, 2, 3]))   // lexicographic: first optimum kept on ties
        {
            if (precedence.Any(p => Array.IndexOf(order, p.Before) > Array.IndexOf(order, p.After))) continue;
            int mask = 0;
            double cost = 0;
            bool ok = !constrained || HandAdmissible(0);
            foreach (int a in order)
            {
                cost += HandUnserved(mask) * Minutes[a] / 60.0;
                mask |= 1 << a;
                if (constrained && !HandAdmissible(mask)) { ok = false; break; }
            }
            if (ok && cost < bestCost) { bestCost = cost; best = order.Select(a => Ids[a]).ToArray(); }
        }
        return (best, bestCost);
    }

    [Fact]
    public void Proven_optimum_matches_the_hand_derived_and_brute_force_order()
    {
        RestorationStudyResult result = RestorationStudy.Run(Model(), Request());
        (string[]? order, double cost) = BruteForce(true, []);
        (_, double relaxed) = BruteForce(false, []);

        Assert.Equal(["A", "C", "D", "B"], order!);                      // the hand derivation
        Assert.Equal(2000.0 / 60.0, cost, 12);
        Assert.Equal(1500.0 / 60.0, relaxed, 12);

        Assert.Equal("OPTIMAL", result.Status);
        Assert.Equal("ProvenOptimal", result.Outcome);
        Assert.Equal(order, result.Sequence.Select(s => s.Action).ToArray());
        Assert.Equal(cost, result.ObjectiveMwh!.Value, 9);
        Assert.Equal(result.ObjectiveMwh, result.LowerBoundMwh);
        Assert.Equal(result.ObjectiveMwh, result.UpperBoundMwh);
        Assert.Equal(relaxed, result.RelaxedLowerBoundMwh!.Value, 9);
        Assert.Equal(100, result.TotalLoadMw);
        Assert.Equal(90, result.InitialUnservedMw);
        Assert.Equal(0, result.FinalUnservedMw);
        Assert.False(result.Search.BudgetExhausted);
        Assert.Equal(0, result.Search.UnverifiedStates);

        // Step accounting: start and end minutes, unserved before and after, energised buses.
        double[] before = [90, 50, 20, 0], after = [50, 20, 0, 0], start = [0, 10, 30, 35];
        for (int k = 0; k < 4; k++)
        {
            RestorationStep step = result.Sequence[k];
            Assert.Equal(k + 1, step.Step);
            Assert.Equal(before[k], step.UnservedBeforeMw);
            Assert.Equal(after[k], step.UnservedAfterMw);
            Assert.Equal(start[k], step.StartMinute);
            Assert.Equal(start[k] + Minutes[Array.IndexOf(Ids, step.Action)], step.EndMinute);
            Assert.Equal("SECURE", step.State.Disposition);
        }
        Assert.Equal([1, 2, 3, 4, 5], result.Sequence[^1].EnergisedBuses);
    }

    [Fact]
    public void Every_evaluated_state_agrees_with_the_hand_feasibility_rule()
    {
        RestorationStudyResult result = RestorationStudy.Run(Model(), Request());
        Assert.NotEmpty(result.States);
        foreach (RestorationStateSummary s in result.States)
        {
            int mask = s.Closed.Aggregate(0, (m, id) => m | 1 << Array.IndexOf(Ids, id));
            Assert.Equal(HandAdmissible(mask), s.Admissible);
            Assert.Equal(HandUnserved(mask), s.UnservedMw);
            if (!s.Admissible) Assert.StartsWith("VIOLATION", s.Disposition);
        }
        // The relaxed optimum's second state (A and B, C open) was met and rejected: A (branch 0) over 60 MVA.
        Assert.Contains(result.States, s => s.Closed.SequenceEqual(["A", "B"]) && !s.Admissible);
    }

    [Fact]
    public void Inadmissible_state_reports_the_overloaded_branch_with_absolute_evidence()
    {
        // Precedence A before B and B before C forces the inadmissible state {A, B}: every order fails there.
        RestorationStudyResult result = RestorationStudy.Run(Model(),
            Request([new("A", "B"), new("B", "C")]));
        Assert.Equal("INFEASIBLE", result.Status);
        Assert.Equal("ProvenInfeasible", result.Outcome);
        Assert.Null(result.ObjectiveMwh);
        Assert.Empty(result.Sequence);
        RestorationStateSummary ab = result.States.Single(s => s.Closed.SequenceEqual(["A", "B"]));
        Assert.False(ab.Admissible);
        Assert.True(ab.Violations >= 1);
        (string[]? order, _) = BruteForce(true, [(0, 1), (1, 2)]);
        Assert.Null(order);
    }

    [Fact]
    public void Violation_evidence_names_branch_A_against_its_rating()
    {
        // Candidates A and B only, with C stored out of service (a non-candidate keeps its stored state): the final
        // state {A, B} feeds buses 2, 3 and 4 (D is closed, not a candidate) radially through A: 90 MW plus losses
        // against 60 MVA.
        GridModel model = Model();
        Branch[] branches = model.Branches.ToArray();
        branches[2] = branches[2] with { InService = false };
        RestorationAction[] actions = [Actions()[0], Actions()[1]];
        RestorationStudyResult result = RestorationStudy.Run(model.CopyWith(branches: branches), new RestorationRequest(actions, 0.9, 1.1));
        Assert.Equal("INFEASIBLE", result.Status);
        RestorationStateSummary ab = result.States.Single(s => s.Closed.SequenceEqual(["A", "B"]));
        LimitEvidence from = ab.ViolatedLimits.Single(l => l.Kind == "BranchFrom");
        Assert.Equal(0, from.Index);                                     // original branch index of A
        Assert.Equal(60, from.Upper);
        Assert.InRange(from.Observed, 90.0, 92.0);                       // 90 MW of load plus series losses
        Assert.All(ab.ViolatedLimits, l => Assert.Equal(0, l.Index));

        // With C closed and not a candidate, the loop exists in every state, so {A, B} is admissible.
        RestorationStudyResult meshed = RestorationStudy.Run(Model(), new RestorationRequest(actions, 0.9, 1.1));
        Assert.Equal("OPTIMAL", meshed.Status);
        Assert.Equal(["A", "B"], meshed.Sequence.Select(s => s.Action).ToArray());
    }

    [Fact]
    public void Precedence_constraints_change_the_optimum_exactly_as_brute_force_does()
    {
        // B before A: the hand optimum (A first) is excluded.
        RestorationStudyResult result = RestorationStudy.Run(Model(), Request([new("B", "A")]));
        (string[]? order, double cost) = BruteForce(true, [(1, 0)]);
        (_, double relaxed) = BruteForce(false, [(1, 0)]);
        Assert.NotNull(order);
        Assert.Equal("OPTIMAL", result.Status);
        Assert.Equal(order, result.Sequence.Select(s => s.Action).ToArray());
        Assert.Equal(cost, result.ObjectiveMwh!.Value, 9);
        Assert.Equal(relaxed, result.RelaxedLowerBoundMwh!.Value, 9);
        Assert.True(cost > 2000.0 / 60.0);
    }

    [Fact]
    public void Exhausted_budget_gives_a_feasible_sequence_with_bounds_but_no_proof()
    {
        // Hand trace of the depth-first search: nodes 1-5 are A, AB (rejected), AC, ACB, ACBD (complete, 2200/60 MWh);
        // node 6 would exceed the budget, so the incumbent A, C, B, D stands unproven.
        RestorationStudyResult result = RestorationStudy.Run(Model(), Request(maxNodes: 5));
        Assert.Equal("FEASIBLE", result.Status);
        Assert.Equal("FeasibleNotProven", result.Outcome);
        Assert.True(result.Search.BudgetExhausted);
        Assert.Equal(["A", "C", "B", "D"], result.Sequence.Select(s => s.Action).ToArray());
        Assert.Equal(2200.0 / 60.0, result.UpperBoundMwh!.Value, 12);
        Assert.Equal(1500.0 / 60.0, result.LowerBoundMwh!.Value, 12);
        Assert.All(result.Sequence, s => Assert.Equal("SECURE", s.State.Disposition));

        RestorationStudyResult tiny = RestorationStudy.Run(Model(), Request(maxNodes: 2));
        Assert.Equal("UNRESOLVED", tiny.Status);
        Assert.Empty(tiny.Sequence);
        Assert.Equal(1500.0 / 60.0, tiny.LowerBoundMwh!.Value, 12);
    }

    [Fact]
    public void Failing_initial_state_is_proven_infeasible()
    {
        // The source holds 1.00 pu: a band starting at 1.01 pu fails before any action.
        RestorationStudyResult result = RestorationStudy.Run(Model(), Request(minPu: 1.01));
        Assert.Equal("INFEASIBLE", result.Status);
        Assert.StartsWith("VIOLATION", result.InitialState!.Disposition);
        Assert.Empty(result.Sequence);
        Assert.Equal(1, result.Search.StatesEvaluated);
    }

    [Fact]
    public void Unverifiable_states_never_count_as_admissible_or_as_a_proof()
    {
        // Two units at the source bus: the verifier cannot observe their individual outputs, so every state is INCOMPLETE.
        RestorationStudyResult result = RestorationStudy.Run(Model(twoUnitsAtSource: true), Request());
        Assert.Equal("UNRESOLVED", result.Status);
        Assert.Equal("INCOMPLETE", result.InitialState!.Disposition);
        Assert.Equal(1, result.Search.UnverifiedStates);
        Assert.Empty(result.Sequence);
    }

    [Fact]
    public void Target_outside_the_verifier_envelope_is_unresolved_with_its_reason()
    {
        RestorationStudyResult result = RestorationStudy.Run(Model(isolatedBus: true), Request());
        Assert.Equal("UNRESOLVED", result.Status);
        Assert.Equal("Unsupported", result.Outcome);
        Assert.Contains("connected", result.Reason);
        Assert.Null(result.InitialState);
    }

    [Fact]
    public void Stored_open_candidate_is_handled_as_open_then_closed()
    {
        RestorationStudyResult stored = RestorationStudy.Run(Model(storeDOpen: true), Request());
        RestorationStudyResult plain = RestorationStudy.Run(Model(), Request());
        Assert.Equal(plain.Sequence.Select(s => s.Action), stored.Sequence.Select(s => s.Action));
        Assert.Equal(plain.ObjectiveMwh, stored.ObjectiveMwh);
    }

    [Fact]
    public void Repeat_runs_are_bit_identical_and_the_input_model_is_unchanged()
    {
        GridModel model = Model();
        Branch[] before = model.Branches.ToArray();
        string first = JsonSerializer.Serialize(RestorationStudy.Run(model, Request()));
        string second = JsonSerializer.Serialize(RestorationStudy.Run(model, Request()));
        Assert.Equal(first, second);
        Assert.Equal(before, model.Branches.ToArray());
    }

    [Fact]
    public void Invalid_requests_are_refused_before_any_solve()
    {
        GridModel model = Model();
        RestorationAction[] a = Actions();
        Assert.Throws<ArgumentException>(() => RestorationStudy.Run(model, new([a[0], a[0] with { Branch = a[1].Branch }], 0.9, 1.1)));
        Assert.Throws<ArgumentException>(() => RestorationStudy.Run(model, new([a[0] with { Branch = new(0, 2, 1) }], 0.9, 1.1)));
        Assert.Throws<ArgumentException>(() => RestorationStudy.Run(model, new([a[0], a[1] with { Branch = a[0].Branch }], 0.9, 1.1)));
        Assert.Throws<ArgumentException>(() => RestorationStudy.Run(model, new([a[0] with { Minutes = 0 }], 0.9, 1.1)));
        Assert.Throws<ArgumentException>(() => RestorationStudy.Run(model, new([a[0]], 1.1, 0.9)));
        Assert.Throws<ArgumentException>(() => RestorationStudy.Run(model, new(a, 0.9, 1.1, [new("A", "B"), new("B", "A")])));
        Assert.Throws<ArgumentException>(() => RestorationStudy.Run(model, new(a, 0.9, 1.1, [new("A", "Z")])));
        Assert.Throws<ArgumentException>(() => RestorationStudy.Run(model, new(a, 0.9, 1.1, null, 0)));
        Assert.Throws<ArgumentException>(() => RestorationStudy.Run(model, new([], 0.9, 1.1)));
        RestorationAction[] many = Enumerable.Range(0, RestorationStudy.MaxActions + 1)
            .Select(i => new RestorationAction("X" + i, new RestorationBranch(0, 1, 2), 1)).ToArray();
        Assert.Throws<ArgumentException>(() => RestorationStudy.Run(model, new(many, 0.9, 1.1)));
    }
}
