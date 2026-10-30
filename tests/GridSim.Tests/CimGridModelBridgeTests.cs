using System;
using System.Linq;
using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>The CIM->GridModel bridge: a parsed CIM network becomes a structurally-valid, solvable GridModel -
/// generator bus as slack, load on its bus, line as a branch.</summary>
public class CimGridModelBridgeTests
{
    private static CimNetwork TwoBusPu() => new(
        new[] { new CimBus("n1", "Slack"), new CimBus("n2", "Load") },
        new[] { new CimBranch("l1", "Line1", "n1", "n2", 0.01, 0.05, 0.0) },
        new[] { new CimLoad("ld1", "Load1", "n2", 25.0, 8.0) },
        new[] { new CimGenerator("g1", "Gen1", "n1", 25.0) },
        System.Array.Empty<CimTransformer>());

    [Fact]
    public void Bridges_to_a_valid_solvable_grid_model()
    {
        // Moved 2026-10 (D-DOC-101, docs-session finding, Ruling R1: Mark delegated 2026-10-04): this fixture's
        // buses carry no base voltage and its line values are already per-unit. The bridge used to take an unbased
        // impedance as per-unit without a word, which is the defect (for ohm data it is wrong by a factor of Zbase);
        // it now refuses one unless the caller declares per-unit data. Before: ToGridModel(TwoBusPu()). After:
        // ToGridModel(TwoBusPu(), unbasedImpedancesArePerUnit: true). Every number is unchanged: zBase = 1.0 on both
        // paths, so the line stays r = 0.01, x = 0.05 pu bit for bit, and the solve below is the same solve.
        GridModel model = CimGridModelBridge.ToGridModel(TwoBusPu(), unbasedImpedancesArePerUnit: true);

        Assert.Equal(2, model.BusCount);
        Assert.Single(model.Branches);
        Assert.Single(model.Generators);
        Assert.Equal(BusType.Slack, model.Buses[0].Type); // generator bus is the reference
        Assert.Equal(25.0, model.Buses[1].Pd, 9);         // load landed on n2

        Assert.DoesNotContain(model.Validate(), d => d.Severity == ValidationSeverity.Error);

        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(r.Converged, $"CIM-imported model did not solve ({r.Status})");
    }

    [Fact]
    public void Ohm_impedances_are_per_unitised_against_base_voltage()
    {
        // 132 kV base, 100 MVA -> Zbase = 132^2/100 = 174.24 ohm. A 17.424 ohm line is 0.1 pu.
        var net = new CimNetwork(
            new[] { new CimBus("n1", "Slack", 132.0), new CimBus("n2", "Load", 132.0) },
            new[] { new CimBranch("l1", "Line1", "n1", "n2", 17.424, 87.12, 0.0) },
            new[] { new CimLoad("ld1", "Load1", "n2", 20.0, 5.0) },
            new[] { new CimGenerator("g1", "Gen1", "n1", 20.0) },
            System.Array.Empty<CimTransformer>());

        GridModel model = CimGridModelBridge.ToGridModel(net);
        Branch line = model.Branches[0];
        Assert.Equal(0.1, line.R, 6);   // 17.424 / 174.24
        Assert.Equal(0.5, line.X, 6);   // 87.12 / 174.24
        Assert.Equal(132.0, model.Buses[0].BaseKv, 6);
        Assert.True(new NewtonRaphsonPowerFlow().Solve(model).Converged);
    }

    [Theory]
    [InlineData("n2", 0.01, 0.05)]   // both terminals on one node: a self-loop
    [InlineData("n1", 0.0, 0.0)]     // a zero-impedance line between the two buses
    public void A_structurally_invalid_line_is_refused_at_the_bridge(string secondEnd, double r, double x)
    {
        // GR-150: the bridge now runs GridModel.Validate, as the JSON and MATPOWER loaders do, so these no longer reach a
        // solver unchecked.
        var net = new CimNetwork(
            new[] { new CimBus("n1", "Slack"), new CimBus("n2", "Load") },
            new[]
            {
                new CimBranch("l1", "Line1", "n1", "n2", 0.01, 0.05, 0.0),
                new CimBranch("l2", "Line2", "n2", secondEnd, r, x, 0.0),
            },
            new[] { new CimLoad("ld1", "Load1", "n2", 25.0, 8.0) },
            new[] { new CimGenerator("g1", "Gen1", "n1", 25.0) },
            System.Array.Empty<CimTransformer>());
        var ex = Assert.Throws<System.IO.InvalidDataException>(() => CimGridModelBridge.ToGridModel(net, unbasedImpedancesArePerUnit: true));
        Assert.Contains("is invalid", ex.Message, StringComparison.Ordinal);
    }
}
