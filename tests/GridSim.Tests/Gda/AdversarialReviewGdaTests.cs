using System.Linq;
using GridSim.Core.Model;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// Regression tests for the GDA data-correctness defects found in the 2026
/// adversarial review. Each test reproduces the concrete
/// failure scenario on a tiny synthetic <see cref="LtdsDataset"/> (no real grid data).
/// </summary>
public class AdversarialReviewGdaTests
{
    private static LtdsNode Node(string code, string gsp) =>
        new("NGED", code, code, "BSP", gsp, "Energised");

    private static LtdsCircuit Series(string gsp, string n1, string n2, double kv) =>
        new("NGED", gsp, "BSP", n1, n2, "Energised", kv, "C",
            R1Ohm: 1.0, X1Ohm: 5.0, B1MicroS: 0, R0Ohm: 1, X0Ohm: 5,
            WinterRatingMva: 100, SummerRatingMva: 100, LengthKm: 1.0, Equipment: "Circuit");

    private static LtdsDemand Demand(string gsp, string primary, string node, double mw, string status = "Energised") =>
        new(gsp, "BSP", primary, node, Kv: 33, MaxDemandMw: mw, PowerFactor: 0.98, FirmCapacityMva: 50, Status: status);

    private static LtdsGeneration Gen(string gsp, string primary, double capMw) =>
        new(gsp, "BSP", primary, Kv: 33, ExportCapacityMw: capMw, FuelType: "CCGT", Status: "Connected");

    private static LtdsDataset Dataset(
        IReadOnlyList<LtdsNode> nodes, IReadOnlyList<LtdsCircuit> circuits,
        IReadOnlyList<LtdsDemand> demand, IReadOnlyList<LtdsGeneration> gen,
        IReadOnlyList<LtdsTransformer3>? tx3 = null) =>
        new("synthetic", ImpedanceConvention.OhmicReferredToGsp,
            nodes, circuits, Array.Empty<LtdsTransformer2>(), tx3 ?? Array.Empty<LtdsTransformer3>(),
            demand, gen);

    [Fact] // CRITICAL: '-' primary substation must not be a join key
    public void Dash_placeholder_generation_is_unmatched_not_collapsed_onto_one_bus()
    {
        const string gsp = "Test 132kV";
        var ds = Dataset(
            new[] { Node("GSP", gsp), Node("A", gsp), Node("B", gsp) },
            new[] { Series(gsp, "GSP", "A", 132), Series(gsp, "GSP", "B", 132) },
            new[]
            {
                Demand(gsp, "Substation A", "A", 10),
                Demand(gsp, "-", "B", 10),               // NGED null marker in the demand table
            },
            new[]
            {
                Gen(gsp, "Substation A", 50),            // real name -> attaches
                Gen(gsp, "-", 500),                      // null marker -> must NOT attach anywhere
            });

        GdaBuildResult r = GdaModelBuilder.Build(ds);

        Generator g = Assert.Single(r.Model.Generators);
        Assert.Equal(50, g.MBase, 0);                    // only the real-name row landed
        Assert.DoesNotContain(r.Model.Generators, x => x.MBase >= 500);
        Assert.True(r.Diagnostics.UnmatchedGen >= 1, "dash generation row should be counted as unmatched");
    }

    [Fact] // HIGH: generation-table GSP vocabulary must reconcile with the circuit table on a carve
    public void Gsp_carve_matches_generation_tables_different_spelling()
    {
        var ds = Dataset(
            new[] { Node("GSP", "Berkswell 132kV"), Node("A", "Berkswell 132kV") },
            new[] { Series("Berkswell 132kV", "GSP", "A", 132) },
            new[] { Demand("Berkswell 132kV", "Substation A", "A", 10) },
            new[]
            {
                Gen("Berkswell 132 kV S Stn", "Substation A", 50),   // same group, generation spelling
                Gen("Coventry 132 kV S Stn", "Substation A", 70),    // a different group - must be excluded
            });

        // Carve the Berkswell group using the circuit-table name the user would type.
        GdaBuildResult r = GdaModelBuilder.Build(ds, new GdaBuildOptions { GspGroup = "Berkswell 132kV" });

        Generator g = Assert.Single(r.Model.Generators);
        Assert.Equal(50, g.MBase, 0);                    // Berkswell retained, Coventry dropped
    }

    [Fact] // MEDIUM: 'Removed' demand rows must not fold into the model
    public void Removed_demand_status_is_excluded()
    {
        const string gsp = "Test 132kV";
        var ds = Dataset(
            new[] { Node("GSP", gsp), Node("A", gsp), Node("B", gsp) },
            new[] { Series(gsp, "GSP", "A", 132), Series(gsp, "GSP", "B", 132) },
            new[]
            {
                Demand(gsp, "Substation A", "A", 10, status: "Energised"),
                Demand(gsp, "Substation B", "B", 30, status: "Removed"),
            },
            Array.Empty<LtdsGeneration>());

        GdaBuildResult r = GdaModelBuilder.Build(ds);

        Assert.Equal(10.0, r.Diagnostics.TotalDemandMw, 1);   // the 30 MW removed row is not counted
    }

    [Fact] // HIGH: synthesized 3-winding star nodes must never become slack buses
    public void Three_winding_star_node_is_not_a_slack()
    {
        const string gsp = "Test 132kV";
        var tx3 = new[]
        {
            new LtdsTransformer3("NGED", gsp, "GSP", 132, "P", 33, "T", 11, "Energised", "YNyn0d1",
                R1Ohm: 0.1, X1HvLvOhm: 10, X1LvTertOhm: 8, X1HvTertOhm: 12,
                MinTap: -10, MaxTap: 10, NominalRatingMva: 90, EmergencyRatingMva: 100),
        };
        var ds = Dataset(
            new[] { Node("GSP", gsp), Node("P", gsp), Node("T", gsp), Node("L", gsp) },
            new[] { Series(gsp, "P", "L", 33) },        // a feeder off the 33 kV primary so P/L are buses
            new[] { Demand(gsp, "Load", "L", 10) },
            Array.Empty<LtdsGeneration>(),
            tx3);

        GdaBuildResult r = GdaModelBuilder.Build(ds);

        Assert.True(r.Diagnostics.StarNodes >= 1, "the 3-winding transformer should synthesize a star node");
        Assert.DoesNotContain(r.Model.Buses,
            b => b.Type == BusType.Slack && (b.Name ?? "").StartsWith("STAR", StringComparison.Ordinal));
    }
}
