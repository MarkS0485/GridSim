using GridSim.Core.Analysis;
using GridSim.Core.Layout;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N094 (October 2026): hand-built copies of a model (derived study models: scaled loads, opened branches, a laid-out
/// copy, a partitioned island set, a warm-started copy, ...) listed only the in-service generators, so a writer given
/// the derived model lost the out-of-service units (F16). They now go through <see cref="GridModel.CopyWith"/> (or
/// hand the setter the whole fleet). Engines read <see cref="GridModel.Generators"/>, which is unchanged.
/// </summary>
public class DerivedModelsKeepOutOfServiceUnitsTests
{
    /// <summary>IEEE 9 with named branches and one extra, out-of-service unit on bus 5.</summary>
    private static GridModel Case9WithOutOfServiceUnit()
    {
        GridModel m = Core.IO.IeeeCases.Case9();
        var named = m.Branches.Select((b, i) => b with { Name = $"B{i}" }).ToList();
        var fleet = m.Generators.Concat(new[] { new Generator(5, Pg: 0, Name: "spare", InService: false) }).ToList();
        GridModel withOos = m.CopyWith(branches: named, generators: fleet);
        Assert.Single(withOos.OutOfServiceGenerators);
        return withOos;
    }

    private static void KeepsTheSpare(GridModel source, GridModel derived)
    {
        Assert.Contains(derived.AllGenerators, g => g.Name == "spare" && !g.InService);
        Assert.DoesNotContain(derived.Generators, g => g.Name == "spare");
        Assert.Equal(source.Generators.Count, derived.Generators.Count);
    }

    [Fact]
    public void Scaled_loads_keep_the_out_of_service_unit_and_the_same_in_service_list()
    {
        GridModel m = Case9WithOutOfServiceUnit();
        GridModel s = ContinuationPowerFlow.ScaleLoads(m, 1.1);
        KeepsTheSpare(m, s);
        Assert.Same(m.Generators, s.Generators);   // the very list the engines read, as before
    }

    [Fact]
    public void A_warm_started_copy_keeps_the_out_of_service_unit()
    {
        GridModel m = Case9WithOutOfServiceUnit();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        GridModel w = GridScenario.WithSolvedVoltages(m, r);
        KeepsTheSpare(m, w);
        Assert.Same(m.Generators, w.Generators);
    }

    [Fact]
    public void A_load_scenario_keeps_the_out_of_service_unit()
        => KeepsTheSpare(Case9WithOutOfServiceUnit(), GridScenario.AtLoad(Case9WithOutOfServiceUnit(), 0.9));

    [Fact]
    public void A_laid_out_copy_keeps_the_out_of_service_unit()
    {
        GridModel m = Case9WithOutOfServiceUnit();
        Assert.True(GraphLayout.HasNoLayout(m));
        GridModel l = GraphLayout.EnsureLayout(m);
        KeepsTheSpare(m, l);
        Assert.Same(m.Generators, l.Generators);
    }

    [Fact]
    public void Opening_branches_keeps_a_live_out_of_service_unit_and_drops_one_on_a_dead_bus()
    {
        GridModel m = Case9WithOutOfServiceUnit();
        // Open both branches at bus 5: it becomes a dead island and is dropped, with the spare that sits on it.
        var atBus5 = m.Branches.Where(b => b.From == 5 || b.To == 5).Select(b => b.Name!).ToHashSet();
        (GridModel dead, _) = TopologyOps.Apply(m, atBus5);
        Assert.DoesNotContain(dead.Buses, b => b.Id == 5);
        Assert.DoesNotContain(dead.AllGenerators, g => g.Name == "spare");
        Assert.DoesNotContain(dead.Validate(), d => d.Severity == ValidationSeverity.Error);

        // Opening a branch elsewhere keeps bus 5 alive, and the spare with it.
        string other = m.Branches.First(b => b.From != 5 && b.To != 5).Name!;
        (GridModel live, _) = TopologyOps.Apply(m, new HashSet<string> { other });
        KeepsTheSpare(m, live);
    }
}
