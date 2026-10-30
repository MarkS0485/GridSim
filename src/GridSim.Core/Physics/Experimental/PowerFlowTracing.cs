using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Physics.Experimental;

/// <summary>
/// EXPERIMENTAL: power-flow tracing - "where did each generator's power actually go?" Given a SOLVED
/// power flow, it decomposes every line flow and every load into the contributions of each generator,
/// by Bialek's proportional-sharing method: at a node, the incoming power is assumed to distribute
/// among the outflows in proportion to those outflows. That single assumption makes the trace a linear
/// system - the upstream distribution matrix A_u built from the ACTUAL solved flow directions, solved
/// once per generator against the sparse LU. The "path of least resistance" is not modelled here; it is
/// already baked into the power-flow solution the trace reads.
///
/// PROVENANCE: tracing is an accounting CONVENTION, not a physical fact - electrons are indistinguishable,
/// so "which generator's power reached which load" has no unique answer at the electron level. Proportional
/// sharing (Bialek 1996 / Kirschen) is the established, regulator-used convention for loss allocation and
/// transmission-use pricing; this reproduces it. Honest scope: it is a GROSS trace (line flow taken at the
/// sending end, so each generator is allocated its share of losses as the remainder) and assumes the solved
/// flow graph is ACYCLIC in its flow directions (true for the dominant pattern of almost every solved case).
/// GR-497: only a loop that makes A_u singular is detected and reported; a directed loop with gain below one leaves
/// A_u regular, is traced, and still reads <c>FlowGraphAcyclic: true</c>, so that flag means "A_u was factorised". Opt-in via
/// <c>experimental trace</c>; never on a validated path. With real GB generation/demand/flow data the trace
/// becomes physically meaningful (which northern generator's output reached which southern load).
/// </summary>
public static class PowerFlowTracing
{
    // ---- Named thresholds (October 2026 review, WS13 R186). All in MW; values unchanged.

    /// <summary>A generation, through-flow or branch flow at or below this (MW) is treated as zero: it neither
    /// sources nor carries traced power, and is never used as a divisor.</summary>
    public const double TraceZeroMw = 1e-9;

    /// <summary>Reporting floor (MW) of an individual traced allocation (generator to demand, or generator to
    /// branch). Allocations below it are dropped from the report, so the reported allocations sum exactly to the
    /// traced totals only down to this granularity.</summary>
    public const double AllocationFloorMw = 1e-6;

    /// <summary>Reporting floor (MW) of a generator's share of a boundary transfer.</summary>
    public const double ShareFloorMw = 1e-3;

    /// <summary>How much of a generator's output (MW) reached a particular load bus.</summary>
    public sealed record GenLoadFlow(int GenBus, int LoadBus, double Mw);

    /// <summary>The trace of a solved state.</summary>
    /// <param name="Allocations">Per (generator bus, load bus) the traced MW, nonzero only.</param>
    /// <param name="GenerationMw">Each generator bus's total output traced (MW).</param>
    /// <param name="GenToLossMw">Each generator bus's output allocated to network losses (MW).</param>
    /// <param name="FlowGraphAcyclic">False when A_u is singular (a flow loop that prevents a strict proportional-
    /// sharing trace); Allocations is empty then. True means A_u was factorised, not that no directed loop exists
    /// (GR-497).</param>
    public sealed record TraceResult(
        IReadOnlyList<GenLoadFlow> Allocations,
        IReadOnlyDictionary<int, double> GenerationMw,
        IReadOnlyDictionary<int, double> GenToLossMw,
        bool FlowGraphAcyclic);

    /// <summary>Trace <paramref name="result"/> (the solved state of <paramref name="model"/>).</summary>
    /// <summary>Generator k's contribution (MW, in the branch's from-&gt;to sign) to a branch's flow.</summary>
    public sealed record GenBranchFlow(int GenBus, int BranchIndex, string BranchName, double MwFromTo);

    /// <summary>How much of a boundary's net transfer each generator supplies (signed MW in the
    /// boundary's defined direction).</summary>
    public sealed record BoundaryTransfer(string Boundary, double NetMw, double LimitMw,
        IReadOnlyList<GenShare> ByGenerator);

    /// <summary>A generator's signed share of a boundary transfer (MW).</summary>
    public sealed record GenShare(int GenBus, double Mw);

    public static TraceResult Trace(GridModel model, PowerFlowResult result)
    {
        Basis b = BuildBasis(model, result);
        if (b.Lu is null)
            return new TraceResult(Array.Empty<GenLoadFlow>(),
                Dictionary(b.Gen, result.BusIds), new Dictionary<int, double>(), FlowGraphAcyclic: false);

        var allocations = new List<GenLoadFlow>();
        var genToLoss = new Dictionary<int, double>();
        var e = new double[b.N];
        for (int k = 0; k < b.N; k++)
        {
            if (b.Gen[k] <= TraceZeroMw) continue;
            e[k] = 1.0;
            double[] x = b.Lu.Solve(e); // x_i = [A_u^{-1}]_ik : gen k's per-unit contribution to node i
            e[k] = 0.0;

            double gk = b.Gen[k];
            double toLoads = 0.0;
            for (int i = 0; i < b.N; i++)
            {
                if (b.Demand[i] <= TraceZeroMw || b.Through[i] <= TraceZeroMw) continue;
                double mw = b.Demand[i] / b.Through[i] * x[i] * gk;
                if (mw <= AllocationFloorMw) continue;
                allocations.Add(new GenLoadFlow(result.BusIds[k], result.BusIds[i], mw));
                toLoads += mw;
            }
            genToLoss[result.BusIds[k]] = Math.Max(0.0, gk - toLoads);
        }
        return new TraceResult(allocations, Dictionary(b.Gen, result.BusIds), genToLoss, FlowGraphAcyclic: true);
    }

    /// <summary>Per (generator, branch) contribution to that branch's flow (MW, from-&gt;to sign). The
    /// contributions on any branch sum to its full flow (the trace decomposes it exactly).</summary>
    public static IReadOnlyList<GenBranchFlow> BranchContributions(GridModel model, PowerFlowResult result)
    {
        Basis b = BuildBasis(model, result);
        var outp = new List<GenBranchFlow>();
        if (b.Lu is null) return outp;
        var e = new double[b.N];
        for (int k = 0; k < b.N; k++)
        {
            if (b.Gen[k] <= TraceZeroMw) continue;
            e[k] = 1.0;
            double[] x = b.Lu.Solve(e);
            e[k] = 0.0;
            double gk = b.Gen[k];
            foreach (Flow fl in b.Flows)
            {
                if (b.Through[fl.Sender] <= TraceZeroMw) continue;
                double c = fl.Mw / b.Through[fl.Sender] * x[fl.Sender] * gk; // gen k's MW on sender->receiver
                double fromTo = fl.Sender == fl.FromIdx ? c : -c;            // re-sign to the branch's from->to
                if (Math.Abs(fromTo) <= AllocationFloorMw) continue;
                outp.Add(new GenBranchFlow(result.BusIds[k], fl.BranchIndex, model.Branches[fl.BranchIndex].Name ?? "", fromTo));
            }
        }
        return outp;
    }

    /// <summary>Per-boundary, which generators supply its transfer: sum each boundary branch's
    /// Direction-signed per-generator contribution. The per-generator shares sum to the boundary's net
    /// flow. Empty when the model defines no boundaries.</summary>
    public static IReadOnlyList<BoundaryTransfer> BoundaryTransfers(GridModel model, PowerFlowResult result)
    {
        var transfers = new List<BoundaryTransfer>();
        if (model.Boundaries.Count == 0) return transfers;
        // N138: every boundary branch name must name exactly one branch (an unknown name used to be skipped).
        Boundary.RequireResolvedBranchNames(model);

        // branch name -> per-generator from->to contribution.
        var byName = new Dictionary<string, List<GenBranchFlow>>();
        foreach (GenBranchFlow gbf in BranchContributions(model, result))
        {
            if (string.IsNullOrEmpty(gbf.BranchName)) continue;
            if (!byName.TryGetValue(gbf.BranchName, out List<GenBranchFlow>? list)) byName[gbf.BranchName] = list = new();
            list.Add(gbf);
        }

        foreach (Boundary bd in model.Boundaries)
        {
            var perGen = new Dictionary<int, double>();
            foreach (BoundaryBranch bb in bd.Branches)
                if (byName.TryGetValue(bb.Branch, out List<GenBranchFlow>? list))
                    foreach (GenBranchFlow gbf in list)
                        perGen[gbf.GenBus] = perGen.GetValueOrDefault(gbf.GenBus) + bb.Direction * gbf.MwFromTo;

            double net = perGen.Values.Sum();
            var shares = perGen.Where(kv => Math.Abs(kv.Value) > ShareFloorMw)
                .OrderByDescending(kv => Math.Abs(kv.Value))
                .Select(kv => new GenShare(kv.Key, kv.Value)).ToList();
            transfers.Add(new BoundaryTransfer(bd.Name, net, bd.LimitMw, shares));
        }
        return transfers;
    }

    private readonly record struct Flow(int Sender, int Receiver, double Mw, int BranchIndex, int FromIdx);

    private sealed class Basis
    {
        public required int N { get; init; }
        public required double[] Gen { get; init; }
        public required double[] Demand { get; init; }
        public required double[] Through { get; init; }
        public required List<Flow> Flows { get; init; }
        public required SparseLuFactorization? Lu { get; init; }
    }

    /// <summary>Build the upstream tracing basis: generation, demand, through-power, directed sending-end
    /// flows, and the factored A_u (null when the flow graph has a loop).</summary>
    private static Basis BuildBasis(GridModel model, PowerFlowResult result)
    {
        // D-DOC-087 (October 2026): every public entry (Trace, BranchContributions, BoundaryTransfers) comes through here,
        // so this is the one place the convergence contract is applied. A non-converged iterate's flows are not a network
        // state: tracing them used to report generator-to-load allocations that meant nothing.
        ArgumentNullException.ThrowIfNull(result);
        PowerFlow.ConvergenceContract.RequireConverged(result, "Power-flow tracing");
        int n = result.BusIds.Count;
        var idx = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idx[result.BusIds[i]] = i;

        // Demand is the real load each bus ACTUALLY draws at its solved voltage (N014 / R065), through the one
        // shared rule LoadAtVoltage: a bus with no voltage-dependent ZIP gets its nominal Pd back untouched (so
        // every constant-power case traces bit-for-bit as before), a ZIP bus gets Pd * PMultiplier(Vm). Using the
        // nominal Pd on a solved ZIP case was wrong twice over: the traced demand was off by Pd * (PMultiplier - 1),
        // and the generation recovered below as PInj + demand came out non-zero at a pure load bus (a phantom
        // generator) or short at a generator bus.
        var demand = new double[n];
        foreach (Bus b in model.Buses)
            if (idx.TryGetValue(b.Id, out int i)) demand[i] = LoadAtVoltage.Mw(b, result.Vm[i]).PMw;
        var gen = new double[n];
        for (int i = 0; i < n; i++) gen[i] = Math.Max(0.0, result.PInjMw[i] + demand[i]);

        var flows = new List<Flow>(result.LineFlows.Count);
        var through = (double[])gen.Clone();
        for (int l = 0; l < result.LineFlows.Count; l++)
        {
            LineFlow lf = result.LineFlows[l];
            if (!idx.TryGetValue(lf.From, out int f) || !idx.TryGetValue(lf.To, out int t)) continue;
            int a, b; double flow;
            if (lf.PFromMw >= 0) { a = f; b = t; flow = lf.PFromMw; }
            else { a = t; b = f; flow = lf.PToMw; }
            if (flow <= TraceZeroMw) continue;
            flows.Add(new Flow(a, b, flow, l, f));
            through[b] += flow;
        }

        var builder = new CsrMatrix.Builder(n);
        for (int i = 0; i < n; i++) builder.Add(i, i, 1.0);
        foreach (Flow fl in flows)
            if (through[fl.Sender] > TraceZeroMw) builder.Add(fl.Receiver, fl.Sender, -fl.Mw / through[fl.Sender]);
        SparseLuFactorization? lu = SparseLu.Factor(builder.Build());

        return new Basis { N = n, Gen = gen, Demand = demand, Through = through, Flows = flows, Lu = lu };
    }

    private static Dictionary<int, double> Dictionary(double[] gen, IReadOnlyList<int> busIds)
    {
        var d = new Dictionary<int, double>();
        for (int i = 0; i < gen.Length; i++) if (gen[i] > TraceZeroMw) d[busIds[i]] = gen[i];
        return d;
    }
}
