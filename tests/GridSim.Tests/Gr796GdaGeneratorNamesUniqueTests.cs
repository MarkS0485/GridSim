using GridSim.Core.Model;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-796 (November 2026): the GDA builder names an embedded generator "primary fuel-type", so two rows of the same
/// fuel at one primary used to share a name. They now get a stable " #2", " #3" suffix; first rows keep their name.
/// </summary>
public class Gr796GdaGeneratorNamesUniqueTests
{
    private const string G = "Test 132kV";

    private static LtdsDataset Fixture(params LtdsGeneration[] gen) => new(
        "synthetic", ImpedanceConvention.OhmicReferredToGsp,
        new[]
        {
            new LtdsNode("NGED", "GSP", "GSP", "BSP", G, "Energised"),
            new LtdsNode("NGED", "A", "A", "BSP", G, "Energised"),
        },
        new[]
        {
            new LtdsCircuit("NGED", G, "BSP", "GSP", "A", "Energised", 132, "C",
                R1Ohm: 1.0, X1Ohm: 5.0, B1MicroS: 0, R0Ohm: 1, X0Ohm: 5,
                WinterRatingMva: 100, SummerRatingMva: 100, LengthKm: 1.0, Equipment: "Circuit"),
        },
        Array.Empty<LtdsTransformer2>(),
        Array.Empty<LtdsTransformer3>(),
        new[] { new LtdsDemand(G, "BSP", "Substation A", "A", Kv: 33, MaxDemandMw: 100, PowerFactor: 0.95, FirmCapacityMva: 60) },
        gen);

    private static LtdsGeneration Gen(double mw, string fuel) =>
        new(G, "BSP", "Substation A", Kv: 33, ExportCapacityMw: mw, FuelType: fuel, Status: "Connected");

    [Fact]
    public void Two_rows_of_one_fuel_at_one_primary_get_distinct_names_and_the_first_keeps_its_name()
    {
        GdaBuildResult r = GdaModelBuilder.Build(Fixture(Gen(10, "CCGT"), Gen(20, "CCGT"), Gen(30, "CCGT")));
        string[] names = r.Model.Generators.Select(g => g.Name ?? "").ToArray();
        Assert.Equal(3, names.Length);
        Assert.Equal(3, names.Distinct(StringComparer.Ordinal).Count());
        Assert.EndsWith(" #2", names[1], StringComparison.Ordinal);
        Assert.EndsWith(" #3", names[2], StringComparison.Ordinal);
        Assert.Equal(names[0] + " #2", names[1]);
        Assert.Equal(names[0] + " #3", names[2]);
        // The suffix is a label only: the capacities are untouched and in row order.
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, r.Model.Generators.Select(g => g.MBase).ToArray());
    }

    [Fact]
    public void A_name_that_is_already_unique_is_left_exactly_as_it_was()
    {
        GdaBuildResult r = GdaModelBuilder.Build(Fixture(Gen(10, "CCGT")));
        Assert.DoesNotContain("#", r.Model.Generators.Single().Name!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_naming_helper_never_returns_a_name_already_taken()
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        Assert.Equal("X Solar", GdaModelBuilder.UniqueGeneratorName("X Solar", used));
        Assert.Equal("X Solar #2", GdaModelBuilder.UniqueGeneratorName("X Solar", used));
        // A real primary literally called "X Solar #3" must not collide with the third "X Solar".
        Assert.Equal("X Solar #3", GdaModelBuilder.UniqueGeneratorName("X Solar #3", used));
        string fourth = GdaModelBuilder.UniqueGeneratorName("X Solar", used);
        Assert.NotEqual("X Solar #3", fourth);
        Assert.Equal("X Solar #4", fourth);
    }
}
