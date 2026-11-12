using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-505: GridModel.Validate judged an OLTC record against the FIRST branch carrying its transformer name, while the
/// OLTC loop drives the same-named branch that touches the controlled bus. With a repeated transformer name the HV-side
/// and not-an-endpoint warnings could describe a different unit from the one the loop steps. Validate now uses the loop's
/// own mapping (SteadyStateSolver.MapOltcRecordsToBranches). This had no regression test; these pin it.
/// </summary>
public class Gr505OltcValidateMatchesLoopTests
{
    /// <summary>Two transformers both named "T1": the first (10-11) does not touch the controlled bus, the second (20-21)
    /// does. Buses 10 and 20 are 132 kV, 11 and 21 are 33 kV, so 21 is the LV winding of the second unit.</summary>
    private static GridModel Namesakes(int controlsBus) => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132),
            new Bus(10, BusType.Pq, BaseKv: 132), new Bus(11, BusType.Pq, Pd: 5, BaseKv: 33),
            new Bus(20, BusType.Pq, BaseKv: 132), new Bus(21, BusType.Pq, Pd: 5, BaseKv: 33),
        },
        Branches = new[]
        {
            new Branch(1, 10, 0.01, 0.1), new Branch(1, 20, 0.01, 0.1),
            new Branch(10, 11, 0.005, 0.08, Name: "T1", IsTransformer: true),
            new Branch(20, 21, 0.005, 0.08, Name: "T1", IsTransformer: true),
        },
        Generators = new[] { new Generator(1, 10) },
        Oltcs = new[] { new Oltc("T1", controlsBus) },
    };

    private static IEnumerable<string> OltcWarnings(GridModel m) =>
        m.Validate().Where(d => d.Severity == ValidationSeverity.Warning && d.Message.StartsWith("OLTC", StringComparison.Ordinal))
            .Select(d => d.Message);

    [Fact]
    public void Gr505_the_lv_bus_of_the_second_namesake_is_not_reported_as_off_the_transformer()
    {
        // Judged against the first "T1" (10-11), bus 21 is not an endpoint: the old warning. The loop drives 20-21, whose
        // LV winding is bus 21: the usual arrangement, so no OLTC warning at all.
        Assert.Empty(OltcWarnings(Namesakes(controlsBus: 21)));
    }

    [Fact]
    public void Gr505_the_hv_bus_of_the_second_namesake_is_reported_against_that_namesake()
    {
        string w = Assert.Single(OltcWarnings(Namesakes(controlsBus: 20)));
        Assert.Contains("controls the HV-side bus 20", w);
        Assert.Contains("expected the LV (lower-kV) side bus 21", w);
    }
}
