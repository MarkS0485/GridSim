using GridSim.Gda;
using GridSim.Gda.Ltds;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// October 2026 docs-programme findings on GSP selection: a mistyped <c>gda --gsp</c> built an empty model (now refused
/// up front with near names), and <c>gda --all-gsps</c> built two spellings of one group twice and counted name
/// variants that select nothing as failures. These pin the builder helpers the CLI uses for both.
/// </summary>
public class GdaGspSelectionTests
{
    private static LtdsNode Node(string code, string gsp) => new("NGED", code, code, "BSP", gsp, "Energised");

    private static LtdsCircuit Series(string gsp, string n1, string n2, string status = "Energised") =>
        new("NGED", gsp, "BSP", n1, n2, status, 132, "C",
            R1Ohm: 1.0, X1Ohm: 5.0, B1MicroS: 0, R0Ohm: 1, X0Ohm: 5,
            WinterRatingMva: 100, SummerRatingMva: 100, LengthKm: 1.0, Equipment: "Circuit");

    // Three spellings: two of one group (same canonical key) and a variant whose only circuit is out of service.
    private static LtdsDataset Dataset() => new("synthetic", ImpedanceConvention.OhmicReferredToGsp,
        new[] { Node("GSP", "Iron Acton 132 kV"), Node("A", "Iron Acton 132 kV"), Node("B", "Iron Acton 132kV"),
                Node("X", "Exeter 132 kV"), Node("Y", "Exeter 132 kV"), Node("Z", "Exeter 132") },
        new[]
        {
            Series("Iron Acton 132 kV", "GSP", "A"),
            Series("Iron Acton 132kV", "GSP", "B"),
            Series("Exeter 132 kV", "X", "Y"),
            Series("Exeter 132", "Y", "Z", status: "Out of service"),
        },
        Array.Empty<LtdsTransformer2>(), Array.Empty<LtdsTransformer3>(),
        Array.Empty<LtdsDemand>(), Array.Empty<LtdsGeneration>());

    [Fact]
    public void Group_names_are_listed_as_spelt_and_two_spellings_share_one_key()
    {
        LtdsDataset ds = Dataset();
        Assert.Equal(new[] { "Exeter 132", "Exeter 132 kV", "Iron Acton 132 kV", "Iron Acton 132kV" }, GdaModelBuilder.GspGroups(ds));
        Assert.Equal(GdaModelBuilder.GspGroupKey("Iron Acton 132 kV"), GdaModelBuilder.GspGroupKey("Iron Acton 132kV"));
        Assert.NotEqual(GdaModelBuilder.GspGroupKey("Exeter 132 kV"), GdaModelBuilder.GspGroupKey("Exeter 132"));
    }

    [Fact]
    public void A_known_name_in_any_spelling_is_accepted_and_a_typo_is_not()
    {
        LtdsDataset ds = Dataset();
        Assert.True(GdaModelBuilder.IsKnownGspGroup(ds, "iron acton 132KV"));
        Assert.True(GdaModelBuilder.IsKnownGspGroup(ds, "Exeter 132"));
        Assert.False(GdaModelBuilder.IsKnownGspGroup(ds, "Exter 132 kV"));
        Assert.False(GdaModelBuilder.IsKnownGspGroup(ds, "GSP"));   // a name that folds to an empty key never matches
    }

    [Fact]
    public void Suggestions_put_the_nearest_group_first_and_offer_each_key_once()
    {
        IReadOnlyList<string> near = GdaModelBuilder.SuggestGspGroups(Dataset(), "Exter 132 kV");
        Assert.Equal("Exeter 132 kV", near[0]);
        Assert.Equal(3, near.Count);
        Assert.Single(near, n => n.StartsWith("Iron Acton", StringComparison.Ordinal));   // two spellings, one key
    }

    [Fact]
    public void Both_spellings_of_one_group_build_the_same_model_and_a_dead_variant_builds_empty()
    {
        // Why --all-gsps now builds each key once: the two spellings select the same elements.
        LtdsDataset ds = Dataset();
        int a = GdaModelBuilder.Build(ds, new GdaBuildOptions { GspGroup = "Iron Acton 132 kV" }).Model.BusCount;
        int b = GdaModelBuilder.Build(ds, new GdaBuildOptions { GspGroup = "Iron Acton 132kV" }).Model.BusCount;
        Assert.Equal(3, a);
        Assert.Equal(a, b);
        // And why a variant is set aside: its only circuit is out of service, so it keeps no live element.
        Assert.Equal(0, GdaModelBuilder.Build(ds, new GdaBuildOptions { GspGroup = "Exeter 132" }).Model.BusCount);
    }
}
