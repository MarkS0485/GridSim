using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// The builder must honour each DNO's impedance convention. This drives the
/// PercentOn100Mva path (UKPN-style): circuit percents are p.u. directly on the
/// 100 MVA base, transformer percents are on the transformer rating.
/// </summary>
public class PercentConventionTests
{
    private static LtdsDataset PercentDataset() => new(
        "Test/Percent", ImpedanceConvention.PercentOn100Mva,
        Nodes: new[]
        {
            new LtdsNode("UKPN", "GSP_1", "Grid", "", "T 132kV", "Energised"),
            new LtdsNode("UKPN", "BB", "Primary", "", "T 132kV", "Energised"),
            new LtdsNode("UKPN", "LOAD", "LoadSub", "", "T 132kV", "Energised"),
        },
        Circuits: new[]
        {
            new LtdsCircuit("UKPN", "T 132kV", "", "BB", "LOAD", "Energised", 33, "C1",
                R1Ohm: 5, X1Ohm: 10, B1MicroS: 0, R0Ohm: 0, X0Ohm: 0,
                WinterRatingMva: 100, SummerRatingMva: 100, LengthKm: 3, Equipment: "Circuit"),
        },
        Transformers2: new[]
        {
            new LtdsTransformer2("UKPN", "T 132kV", "GSP_1", 132, "BB", 33, "Energised", "Dyn11",
                R1Ohm: 1, X1Ohm: 10, X0Ohm: 9, MinTap: -10, MaxTap: 10,
                NominalRatingMva: 90, EmergencyRatingMva: 100),
        },
        Transformers3: Array.Empty<LtdsTransformer3>(),
        Demand: new[]
        {
            new LtdsDemand("T 132kV", "", "LoadSub", "LOAD", 33, MaxDemandMw: 30, PowerFactor: 0.95, FirmCapacityMva: 40),
        },
        Generation: Array.Empty<LtdsGeneration>());

    [Fact]
    public void Circuit_percent_is_pu_on_100mva()
    {
        GdaBuildResult r = GdaModelBuilder.Build(PercentDataset(), new GdaBuildOptions { BaseMva = 100 });
        Branch cable = r.Model.Branches.Single(b => !b.IsTransformer);
        Assert.Equal(0.05, cable.R, 6);   // 5% on 100 MVA
        Assert.Equal(0.10, cable.X, 6);   // 10%
    }

    [Fact]
    public void Transformer_percent_is_on_rating()
    {
        GdaBuildResult r = GdaModelBuilder.Build(PercentDataset(), new GdaBuildOptions { BaseMva = 100 });
        Branch tx = r.Model.Branches.Single(b => b.IsTransformer);
        // 10% on a 90 MVA rating, referred to the 100 MVA system base: 0.10 * 100/90
        Assert.Equal(0.10 * 100.0 / 90.0, tx.X, 5);
        Assert.Equal(1.0, tx.TapRatio, 6);
    }

    [Fact]
    public void Percent_model_solves_in_band()
    {
        GdaBuildResult r = GdaModelBuilder.Build(PercentDataset(), new GdaBuildOptions { BaseMva = 100 });
        Assert.Equal(30.0, r.Diagnostics.TotalDemandMw, 1);
        PowerFlowResult pf = new SteadyStateSolver().Solve(r.Model);
        Assert.True(pf.Converged, $"mismatch {pf.MaxMismatch:E2}");
        Assert.InRange(pf.Vm.Min(), 0.9, 1.1);
    }
}
