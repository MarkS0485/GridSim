using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Property-based / invariant tests for the power-flow solvers. Rather than pin exact
/// bus voltages (that is <see cref="IeeeCasesTests"/>'s job), these assert the physical
/// and numerical laws that <em>any</em> converged solution must obey, across a spread of
/// built-in IEEE cases and MATPOWER files parsed from <c>data/matpower</c>:
///
/// <list type="number">
///   <item>Power balance: net injections sum to the losses, and generation = load + losses.</item>
///   <item>Losses are non-negative (real) and reactive book-keeping is self-consistent.</item>
///   <item>The single slack bus absorbs exactly the balance mismatch.</item>
///   <item>The dense and sparse solvers agree on cases both can solve.</item>
///   <item>Solving the same model twice is bit-for-bit deterministic.</item>
///   <item>Converged voltages sit in a physical band on the well-behaved IEEE cases.</item>
///   <item>FlatStart and warm start reach the same solution; enforcing Q-limits keeps
///         generator reactive output inside its limits.</item>
/// </list>
/// </summary>
public class SolverPropertyTests
{
    // ---- Case catalogue ------------------------------------------------------------------------

    // The built-in IEEE / benchmark systems that both solvers handle from a flat-ish start, INCLUDING the
    // stiffer stress cases (RTS-79 24-bus with 33 generators, and the 145-bus system) which were wired into
    // IeeeModel but never actually exercised by any [MemberData].
    public static IEnumerable<object[]> IeeeCaseNames =>
        new[] { "9", "14", "30", "57", "118", "300", "24", "145" }.Select(n => new object[] { n });

    // The "well-behaved" subset whose converged voltages stay inside normal operating limits - a GENUINE
    // subset of the above (excludes the stress cases), not a byte-identical copy of it.
    public static IEnumerable<object[]> WellBehavedCaseNames =>
        new[] { "9", "14", "30", "57", "118", "300" }.Select(n => new object[] { n });

    private static GridModel IeeeModel(string name) => name switch
    {
        "9" => IeeeCases.Case9(),
        "14" => IeeeCases.Case14(),
        "30" => IeeeCases.Case30(),
        "57" => IeeeCases.Case57(),
        "118" => IeeeCases.Case118(),
        "300" => IeeeCases.Case300(),
        "24" => IeeeCases.Case24Rts(),
        "145" => IeeeCases.Case145(),
        _ => throw new ArgumentException($"unknown case {name}", nameof(name)),
    };

    private static string CaseDir => Path.Combine(AppContext.BaseDirectory, "data", "matpower");

    // ============================================================================================
    // 1. Power balance
    // ============================================================================================

    [Theory]
    [MemberData(nameof(IeeeCaseNames))]
    // GR-407: renamed from Net_injections_sum_to_total_losses. NewtonShell DEFINES TotalLossMw as the sum of the net
    // injections, so this checks that definition, not Kirchhoff; the independent check is
    // Solved_injections_recomputed_from_the_ybus_meet_the_specified_injections below.
    public void Reported_total_loss_is_the_sum_of_the_net_injections(string name)
    {
        var m = IeeeModel(name);
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"case{name} did not converge");

        // Kirchhoff over the whole network: every net bus injection, summed, is exactly the
        // real power dissipated in the branches. Same for reactive.
        Assert.Equal(r.TotalLossMw, r.PInjMw.Sum(), 6);
        Assert.Equal(r.TotalLossMvar, r.QInjMvar.Sum(), 6);
    }

    [Theory]
    [MemberData(nameof(IeeeCaseNames))]
    // GR-407: renamed from Total_generation_equals_load_plus_losses: with genP built from PInj + Pd, this is the same
    // definition as the test above, rearranged.
    public void Generation_less_load_is_the_reported_total_loss(string name)
    {
        var m = IeeeModel(name);
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"case{name} did not converge");

        // Generation at a bus is its net injection plus the demand folded onto it.
        double genP = 0, genQ = 0;
        for (int i = 0; i < r.BusIds.Count; i++)
        {
            genP += r.PInjMw[i] + m.Buses[i].Pd;
            genQ += r.QInjMvar[i] + m.Buses[i].Qd;
        }
        double loadP = m.Buses.Sum(b => b.Pd);
        double loadQ = m.Buses.Sum(b => b.Qd);

        Assert.Equal(loadP + r.TotalLossMw, genP, 6);
        Assert.Equal(loadQ + r.TotalLossMvar, genQ, 6);
    }

    /// <summary>
    /// GR-407 (new, independent of how the solver books its totals): rebuild the admittance matrix here, recompute every
    /// bus's complex injection S = V conj(Y V) from the solved voltages alone, and require it to meet what the CASE
    /// specifies: P = sum(Pg) - Pd at every non-slack bus (PV and PQ both fix P, and a Q-limited PV bus switched to PQ
    /// keeps its P), and Q = -Qd at every bus solved as PQ that carries no generator. A wrong Y-bus stamp or transformer
    /// model in the solver gives a self-consistent but wrong state that the definition checks above cannot see; here it
    /// fails. Bound: the solver stops when its worst mismatch is below its tolerance (1e-8 pu by default, 1e-6 MW at
    /// 100 MVA); 1e-4 MW leaves a factor of 100 for the different summation order. ZIP buses are left out (their demand
    /// moves with V by design); the built-in IEEE cases have none.
    /// </summary>
    [Theory]
    [MemberData(nameof(IeeeCaseNames))]
    public void Solved_injections_recomputed_from_the_ybus_meet_the_specified_injections(string name)
    {
        var m = IeeeModel(name);
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"case{name} did not converge");

        YBus y = YBus.Build(m);
        int n = m.BusCount;
        var v = new System.Numerics.Complex[n];
        for (int i = 0; i < n; i++) v[i] = System.Numerics.Complex.FromPolarCoordinates(r.Vm[i], r.VaDeg[i] * Math.PI / 180.0);

        int checkedP = 0, checkedQ = 0;
        for (int i = 0; i < n; i++)
        {
            Bus b = m.Buses[i];
            if (b.Zip is not null) continue;
            System.Numerics.Complex current = System.Numerics.Complex.Zero;
            for (int j = 0; j < n; j++) current += y.Y[i, j] * v[j];
            System.Numerics.Complex s = v[i] * System.Numerics.Complex.Conjugate(current) * m.BaseMva;

            BusType solvedAs = r.SolvedBusTypes is null ? b.Type : r.SolvedBusTypes[i];
            Generator[] here = m.Generators.Where(g => g.Bus == b.Id).ToArray();
            if (b.Type != BusType.Slack)
            {
                double pSpec = here.Sum(g => g.Pg) - b.Pd;
                Assert.True(Math.Abs(s.Real - pSpec) < 1e-4,
                    $"case{name} bus {b.Id}: P from V and Y {s.Real:F8} MW vs specified {pSpec:F8} MW");
                checkedP++;
            }
            if (solvedAs == BusType.Pq && here.Length == 0)
            {
                Assert.True(Math.Abs(s.Imaginary - (-b.Qd)) < 1e-4,
                    $"case{name} bus {b.Id}: Q from V and Y {s.Imaginary:F8} MVAr vs specified {-b.Qd:F8} MVAr");
                checkedQ++;
            }
        }
        Assert.True(checkedP > 0 && checkedQ > 0, $"case{name}: nothing was checked (P {checkedP}, Q {checkedQ})");
    }

    [Theory]
    [MemberData(nameof(IeeeCaseNames))]
    public void Line_flow_losses_plus_shunts_reconcile_with_the_total(string name)
    {
        var m = IeeeModel(name);
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"case{name} did not converge");

        // The per-branch losses are computed from the branch pi-models and the solved voltages,
        // independently of the nodal injection sum. They reconcile with the nodal total once the
        // bus shunt admittances (Gs/Bs, e.g. capacitor banks) - which are nodal, not branch -
        // are added in: a shunt (Gs + jBs) at Vm draws Vm^2*Gs MW and -Vm^2*Bs MVAr.
        double lineP = r.LineFlows.Sum(f => f.PLossMw);
        double lineQ = r.LineFlows.Sum(f => f.QLossMvar);
        double shuntP = 0, shuntQ = 0;
        for (int i = 0; i < r.Vm.Length; i++)
        {
            double vm2 = r.Vm[i] * r.Vm[i];
            shuntP += vm2 * m.Buses[i].Gs;
            shuntQ += -vm2 * m.Buses[i].Bs;
        }
        double scale = Math.Max(1.0, Math.Abs(r.TotalLossMw));
        Assert.True(Math.Abs((lineP + shuntP) - r.TotalLossMw) < 1e-3 * scale,
            $"case{name}: line+shunt P-loss {lineP + shuntP:F6} vs total {r.TotalLossMw:F6}");
        Assert.True(Math.Abs((lineQ + shuntQ) - r.TotalLossMvar) < 1e-2 * scale,
            $"case{name}: line+shunt Q-loss {lineQ + shuntQ:F4} vs total {r.TotalLossMvar:F4}");
    }

    // ============================================================================================
    // 2. Losses non-negative and reactive consistency
    // ============================================================================================

    [Theory]
    // Per-branch loss non-negativity is a WELL-BEHAVED-network property: reduced equivalents like the
    // 145-bus system legitimately carry negative-resistance equivalent branches (negative branch loss).
    [MemberData(nameof(WellBehavedCaseNames))]
    public void Real_losses_are_non_negative(string name)
    {
        var m = IeeeModel(name);
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"case{name} did not converge");

        // A passive network of positive-resistance branches can only dissipate real power.
        Assert.True(r.TotalLossMw >= -1e-6, $"case{name}: negative total loss {r.TotalLossMw:E3}");
        foreach (var f in r.LineFlows)
            Assert.True(f.PLossMw >= -1e-6,
                $"case{name}: branch {f.From}->{f.To} has negative loss {f.PLossMw:E3}");
    }

    // ============================================================================================
    // 3. Slack absorbs the mismatch
    // ============================================================================================

    [Theory]
    [MemberData(nameof(IeeeCaseNames))]
    public void Exactly_one_slack_and_it_closes_the_balance(string name)
    {
        var m = IeeeModel(name);
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"case{name} did not converge");

        // Exactly one reference bus.
        var slackBuses = m.Buses.Where(b => b.Type == BusType.Slack).ToList();
        Assert.Single(slackBuses);
        int slackId = slackBuses[0].Id;
        int slackIdx = r.BusIds.ToList().IndexOf(slackId);
        Assert.True(slackIdx >= 0, "slack bus id not present in the result");

        // The non-slack real generation is fixed at each PV/PQ bus's specified Pg; the slack
        // makes up whatever is left over to meet load + losses. Predict its output from the
        // specified quantities and check the solved slack injection matches.
        double nonSlackGen = m.Generators.Where(g => g.Bus != slackId).Sum(g => g.Pg);
        double loadP = m.Buses.Sum(b => b.Pd);
        double expectedSlackGen = loadP + r.TotalLossMw - nonSlackGen;

        double solvedSlackGen = r.PInjMw[slackIdx] + m.Buses[slackIdx].Pd;
        Assert.Equal(expectedSlackGen, solvedSlackGen, 4);
    }

    // ============================================================================================
    // 4. Dense == sparse on cases both can solve
    // ============================================================================================

    [Theory]
    [MemberData(nameof(IeeeCaseNames))]
    public void Dense_and_sparse_solvers_agree(string name)
    {
        // GR-879: a smoke test older than the bit-identity suite. Its tolerances are NOT the dense/sparse contract;
        // DenseSparseParityTests holds that, bit for bit, and this test is kept as it was (tests are never loosened or
        // re-baselined, and it still catches a gross disagreement in a run that skips the parity suite).
        var m = IeeeModel(name);
        var dense = new NewtonRaphsonPowerFlow().Solve(m);
        var sparse = new SparseNewtonRaphsonPowerFlow().Solve(m);

        Assert.True(dense.Converged, $"case{name}: dense did not converge");
        Assert.Equal(dense.Converged, sparse.Converged);

        var dVm = ById(dense.BusIds, dense.Vm);
        var sVm = ById(sparse.BusIds, sparse.Vm);
        var dVa = ById(dense.BusIds, dense.VaDeg);
        var sVa = ById(sparse.BusIds, sparse.VaDeg);

        foreach (int id in dVm.Keys)
        {
            Assert.True(Math.Abs(dVm[id] - sVm[id]) < 1e-5,
                $"case{name} bus {id}: Vm dense {dVm[id]:F8} vs sparse {sVm[id]:F8}");
            // Both solvers pin the slack to the same specified angle, so the angle reference
            // is shared; compare directly with a small degree tolerance.
            Assert.True(Math.Abs(dVa[id] - sVa[id]) < 1e-3,
                $"case{name} bus {id}: Va dense {dVa[id]:F5} vs sparse {sVa[id]:F5}");
        }

        Assert.True(Math.Abs(dense.TotalLossMw - sparse.TotalLossMw) < 1e-3,
            $"case{name}: loss dense {dense.TotalLossMw:F5} vs sparse {sparse.TotalLossMw:F5}");
    }

    // ============================================================================================
    // 5. Determinism
    // ============================================================================================

    [Theory]
    [MemberData(nameof(IeeeCaseNames))]
    public void Solving_twice_is_bit_for_bit_identical(string name)
    {
        var m = IeeeModel(name);
        var a = new NewtonRaphsonPowerFlow().Solve(m);
        var b = new NewtonRaphsonPowerFlow().Solve(m);

        Assert.Equal(a.Converged, b.Converged);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(a.MaxMismatch, b.MaxMismatch);          // exact
        Assert.Equal(a.TotalLossMw, b.TotalLossMw);          // exact
        Assert.Equal(a.TotalLossMvar, b.TotalLossMvar);      // exact
        for (int i = 0; i < a.Vm.Length; i++)
        {
            Assert.Equal(a.Vm[i], b.Vm[i]);                  // exact
            Assert.Equal(a.VaDeg[i], b.VaDeg[i]);            // exact
            Assert.Equal(a.PInjMw[i], b.PInjMw[i]);          // exact
            Assert.Equal(a.QInjMvar[i], b.QInjMvar[i]);      // exact
        }
    }

    // ============================================================================================
    // 6. Voltage sanity
    // ============================================================================================

    [Theory]
    [MemberData(nameof(WellBehavedCaseNames))]
    public void Converged_voltages_sit_in_a_physical_band(string name)
    {
        var m = IeeeModel(name);
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"case{name} did not converge");

        // Standard-solution IEEE cases keep every bus within normal operating limits.
        for (int i = 0; i < r.Vm.Length; i++)
            Assert.InRange(r.Vm[i], 0.9, 1.1);
    }

    // ============================================================================================
    // 7. Options behaviour
    // ============================================================================================

    [Theory]
    [MemberData(nameof(IeeeCaseNames))]
    public void Flat_start_and_warm_start_reach_the_same_solution(string name)
    {
        var m = IeeeModel(name);
        var warm = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { FlatStart = false });
        var flat = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { FlatStart = true });

        Assert.True(warm.Converged && flat.Converged, $"case{name}: a start point did not converge");

        var wVm = ById(warm.BusIds, warm.Vm);
        var fVm = ById(flat.BusIds, flat.Vm);
        var wVa = ById(warm.BusIds, warm.VaDeg);
        var fVa = ById(flat.BusIds, flat.VaDeg);
        foreach (int id in wVm.Keys)
        {
            Assert.True(Math.Abs(wVm[id] - fVm[id]) < 1e-6,
                $"case{name} bus {id}: Vm warm {wVm[id]:F8} vs flat {fVm[id]:F8}");
            Assert.True(Math.Abs(wVa[id] - fVa[id]) < 1e-4,
                $"case{name} bus {id}: Va warm {wVa[id]:F6} vs flat {fVa[id]:F6}");
        }
    }

    [Theory]
    [MemberData(nameof(IeeeCaseNames))]
    public void Enforcing_q_limits_keeps_generator_reactive_output_within_limits(string name)
    {
        var m = IeeeModel(name);
        var r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(r.Converged, $"case{name} did not converge");

        // Aggregate each bus's generator reactive limits and its reactive demand.
        var qMin = new Dictionary<int, double>();
        var qMax = new Dictionary<int, double>();
        foreach (var g in m.Generators)
        {
            qMin[g.Bus] = qMin.GetValueOrDefault(g.Bus) + g.Qmin;
            qMax[g.Bus] = qMax.GetValueOrDefault(g.Bus) + g.Qmax;
        }
        var qd = m.Buses.ToDictionary(b => b.Id, b => b.Qd);
        var slackId = m.Buses.First(b => b.Type == BusType.Slack).Id;

        // For every generator bus except the slack (whose reactive output is unconstrained by
        // design), the reactive the generators are producing must sit inside their limits.
        for (int i = 0; i < r.BusIds.Count; i++)
        {
            int id = r.BusIds[i];
            if (id == slackId || !qMax.ContainsKey(id)) continue;

            double qGen = r.QInjMvar[i] + qd[id]; // net injection + demand = generator output
            Assert.True(qGen <= qMax[id] + 0.5,
                $"case{name} bus {id}: Qg {qGen:F3} exceeds Qmax {qMax[id]:F3}");
            Assert.True(qGen >= qMin[id] - 0.5,
                $"case{name} bus {id}: Qg {qGen:F3} below Qmin {qMin[id]:F3}");
        }
    }

    [Theory]
    [MemberData(nameof(IeeeCaseNames))]
    public void Disabling_q_limits_still_converges_and_balances(string name)
    {
        // The toggle must be safe both ways: with enforcement off no PV->PQ switching happens,
        // yet the solve must still converge and satisfy the balance identity.
        var m = IeeeModel(name);
        var r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = false });
        Assert.True(r.Converged, $"case{name} did not converge with Q-limits disabled");
        Assert.Equal(r.TotalLossMw, r.PInjMw.Sum(), 6);
        Assert.True(r.TotalLossMw >= -1e-6, $"case{name}: negative loss");
    }

    // ============================================================================================
    // MATPOWER-parsed cases: run the conservation invariants on files loaded from disk.
    // ============================================================================================

    [Theory]
    [InlineData("lib/case14.m")]
    [InlineData("lib/case30.m")]
    [InlineData("lib/case57.m")]
    [InlineData("lib/case118.m")]
    [InlineData("lib/case300.m")]
    public void Parsed_lib_case_obeys_conservation(string rel)
    {
        string path = Path.Combine(CaseDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"bundled file missing: {path}");   // GR-404: the file is committed (BundledDataPresenceTests checks it), so a missing one fails instead of passing silently

        GridModel m = MatpowerParser.ParseFile(path);
        var r = new NewtonRaphsonPowerFlow().Solve(m);

        Assert.True(r.Converged, $"{rel} did not converge (mismatch {r.MaxMismatch:E2})");
        AssertConservation(rel, m, r);
    }

    [Theory]
    [InlineData("pegase/case1354pegase.m", 1354)]
    [InlineData("pegase/case2869pegase.m", 2869)]
    [LargeCaseInlineData(CaseSizes.Pegase9241, Engine.Sparse, "pegase/case9241pegase.m", 9241)]
    public void Parsed_pegase_case_obeys_conservation(string rel, int expectedBuses)
    {
        string path = Path.Combine(CaseDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"bundled file missing: {path}");   // GR-404: the file is committed (BundledDataPresenceTests checks it), so a missing one fails instead of passing silently

        GridModel m = MatpowerParser.ParseFile(path);
        Assert.Equal(expectedBuses, m.BusCount);

        // Large meshed networks: sparse solver, generous iteration cap, Q-limits off (matching the
        // parser integration tests) so we exercise the conservation laws, not the switching loop.
        var opt = new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = false };
        var r = new SparseNewtonRaphsonPowerFlow().Solve(m, opt);

        Assert.True(r.Converged, $"{rel} did not converge (mismatch {r.MaxMismatch:E2})");
        AssertConservation(rel, m, r);
    }

    /// <summary>The solver-agnostic conservation laws: single slack, balance closes, losses >= 0.</summary>
    private static void AssertConservation(string label, GridModel m, PowerFlowResult r)
    {
        Assert.Single(m.Buses, b => b.Type == BusType.Slack);

        // Net injections sum to total losses (both P and Q).
        Assert.Equal(r.TotalLossMw, r.PInjMw.Sum(), 5);
        Assert.Equal(r.TotalLossMvar, r.QInjMvar.Sum(), 5);

        // Generation = load + losses.
        double genP = 0;
        for (int i = 0; i < r.BusIds.Count; i++) genP += r.PInjMw[i] + m.Buses[i].Pd;
        double loadP = m.Buses.Sum(b => b.Pd);
        Assert.True(Math.Abs((loadP + r.TotalLossMw) - genP) < 1e-3,
            $"{label}: generation {genP:F3} vs load+losses {loadP + r.TotalLossMw:F3}");

        // Real losses are non-negative.
        Assert.True(r.TotalLossMw >= -1e-6, $"{label}: negative total loss {r.TotalLossMw:E3}");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static Dictionary<int, double> ById(IReadOnlyList<int> ids, double[] values)
    {
        var d = new Dictionary<int, double>(ids.Count);
        for (int i = 0; i < ids.Count; i++) d[ids[i]] = values[i];
        return d;
    }
}
