using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Docs-programme findings on the harmonic study: the CLI divided THD by a nominal 1.0 pu while the web tab divided by
/// the solved fundamental (now both call <see cref="HarmonicIndices.SolvedFundamentalVm"/>), and a source component at
/// an order outside the analysed set was dropped silently (now listed by
/// <see cref="HarmonicPenetration.ComponentsOutsideOrders"/>, which the CLI refuses on).
/// </summary>
public class HarmonicFundamentalAndOrderTests
{
    private static (GridModel m, HarmonicPenetrationResult pen, PowerFlowResult pf) Case9Study()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        int bus = m.Buses.OrderByDescending(b => b.Pd).First().Id;
        var src = new[] { HarmonicSourceLibrary.Make("6-pulse", bus, fundamentalCurrentPu: 0.5) };
        return (m, HarmonicPenetration.Solve(m, src, HarmonicModelOptions.Study), pf);
    }

    /// <summary>A copy of a result with its bus order, voltages or status replaced (PowerFlowResult is a class).</summary>
    private static PowerFlowResult Copy(PowerFlowResult r, IReadOnlyList<int>? busIds = null, double[]? vm = null,
        bool? converged = null, PowerFlowStatus? status = null) => new()
    {
        Converged = converged ?? r.Converged, Status = status ?? r.Status, Iterations = r.Iterations,
        MaxMismatch = r.MaxMismatch, BusIds = busIds ?? r.BusIds, Vm = vm ?? r.Vm, VaDeg = r.VaDeg, PInjMw = r.PInjMw,
        QInjMvar = r.QInjMvar, TotalLossMw = r.TotalLossMw, TotalLossMvar = r.TotalLossMvar, LineFlows = r.LineFlows,
    };

    [Fact]
    public void The_solved_fundamental_is_matched_by_bus_id()
    {
        (GridModel _, HarmonicPenetrationResult pen, PowerFlowResult pf) = Case9Study();
        double[] vm = HarmonicIndices.SolvedFundamentalVm(pen, pf);
        Assert.Equal(pen.BusIds.Count, vm.Length);
        for (int bi = 0; bi < pen.BusIds.Count; bi++)
        {
            int k = pf.BusIds.ToList().IndexOf(pen.BusIds[bi]);
            Assert.Equal(pf.Vm[k], vm[bi]);   // exact: a copy, not a recomputation
        }

        // A reversed result (same values, bus order reversed) gives the identical aligned vector.
        PowerFlowResult reversed = Copy(pf, busIds: pf.BusIds.Reverse().ToArray(), vm: pf.Vm.Reverse().ToArray());
        Assert.Equal(vm, HarmonicIndices.SolvedFundamentalVm(pen, reversed));
    }

    [Fact]
    public void Thd_over_the_solved_fundamental_is_the_nominal_thd_divided_by_vm()
    {
        (GridModel _, HarmonicPenetrationResult pen, PowerFlowResult pf) = Case9Study();
        double[] vm = HarmonicIndices.SolvedFundamentalVm(pen, pf);
        HarmonicIndicesResult nominal = HarmonicIndices.FromPenetration(pen);
        HarmonicIndicesResult solved = HarmonicIndices.FromPenetration(pen, vm);
        for (int bi = 0; bi < vm.Length; bi++)
            Assert.Equal(nominal.ThdVoltagePct[bi] / vm[bi], solved.ThdVoltagePct[bi], 12);
    }

    [Fact]
    public void A_non_converged_fundamental_is_refused()
    {
        (GridModel _, HarmonicPenetrationResult pen, PowerFlowResult pf) = Case9Study();
        PowerFlowResult failed = Copy(pf, converged: false, status: PowerFlowStatus.MaxIterations);
        var ex = Assert.Throws<InvalidOperationException>(() => HarmonicIndices.SolvedFundamentalVm(pen, failed));
        Assert.Contains("did not converge", ex.Message);
    }

    [Fact]
    public void Components_outside_the_analysed_orders_are_listed_and_analysed_ones_are_not()
    {
        var src = new HarmonicSource(9, "6-pulse", 0.5, new[]
        {
            new HarmonicComponent(1, 100.0), new HarmonicComponent(5, 18.0), new HarmonicComponent(2, 11.0),
            new HarmonicComponent(51, 3.0), new HarmonicComponent(4, 0.0),
        }, GridSim.Core.Diagnostics.ProvenanceTag.Recorded);
        IReadOnlyList<string> outside = HarmonicPenetration.ComponentsOutsideOrders(new[] { src }, HarmonicModelOptions.Study);
        Assert.Equal(2, outside.Count);   // order 1 is the reference, 5 is analysed, 4 is zero
        Assert.Contains("order 2", outside[0]);
        Assert.Contains("order 51", outside[1]);

        // The library sources the CLI and web use by default lie inside the analysed set.
        Assert.Empty(HarmonicPenetration.ComponentsOutsideOrders(
            new[] { HarmonicSourceLibrary.Make("6-pulse", 9, 0.5) }, HarmonicModelOptions.Study));
    }
}
