using GridSim.Core.Analysis;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N121 (October 2026): <c>acopf --costs</c> had no price output. <see cref="AcOptimalPowerFlow.Solve"/> now returns,
/// when asked, the nodal and line prices of its FINAL DC pass (lossless DC prices at the loss-inflated demand that pass
/// dispatched), and asking leaves the dispatch exactly as it is without them.
/// </summary>
public class AcOpfPricesTests
{
    private static GridModel Case(string rel) => S10TestSupport.LoadMatpower(rel).Model;

    [Theory]
    [InlineData("nesta/pglib_opf_case5_pjm.m", true)]
    [InlineData("lib/case9.m", true)]
    [InlineData("lib/case9.m", false)]   // the fuel-type merit order path
    public void Asking_for_prices_leaves_the_dispatch_bit_identical(string rel, bool curves)
    {
        GridModel m = Case(rel);
        AcOpfResult plain = AcOptimalPowerFlow.Solve(m, useGeneratorCosts: curves);
        AcOpfResult priced = AcOptimalPowerFlow.Solve(m, useGeneratorCosts: curves, computePrices: true);
        Assert.True(plain.Converged && priced.Converged);
        Assert.Null(plain.Prices);
        Assert.Equal(plain.Iterations, priced.Iterations);
        Assert.Equal(BitConverter.DoubleToInt64Bits(plain.TotalLossMw), BitConverter.DoubleToInt64Bits(priced.TotalLossMw));
        Assert.Equal(BitConverter.DoubleToInt64Bits(plain.TotalCostPerHour), BitConverter.DoubleToInt64Bits(priced.TotalCostPerHour));
        Assert.Equal(plain.Dispatch.Select(d => BitConverter.DoubleToInt64Bits(d.Pg)), priced.Dispatch.Select(d => BitConverter.DoubleToInt64Bits(d.Pg)));

        DcOptimalPowerFlow.DcOpfPrices pr = Assert.IsType<DcOptimalPowerFlow.DcOpfPrices>(priced.Prices);
        Assert.Equal(m.Buses.Count, pr.Buses.Count);
        foreach (DcOptimalPowerFlow.BusPrice p in pr.Buses)
        {
            Assert.True(double.IsFinite(p.Lmp));
            Assert.Equal(p.EnergyComponent + p.CongestionComponent, p.Lmp, 9);
        }
    }

    [Fact]
    public void Pjm_5_bus_final_pass_prices_are_the_loss_inflated_dc_prices()
    {
        // The PJM 5-bus line 4-5 binds in the DC-OPF (F19: -240 MW, shadow price 62.3220 lossless). The loss loop raises
        // demand by the AC losses (5.007 MW here), so its final pass still binds 4-5 and the prices keep the congestion
        // pattern: the LMPs differ across buses (a flat price would mean no congestion).
        AcOpfResult r = AcOptimalPowerFlow.Solve(Case("nesta/pglib_opf_case5_pjm.m"), useGeneratorCosts: true, computePrices: true);
        DcOptimalPowerFlow.DcOpfPrices pr = r.Prices!;
        Assert.Contains(pr.Lines, l => l.ShadowPrice != 0);
        Assert.True(pr.Buses.Max(p => p.Lmp) - pr.Buses.Min(p => p.Lmp) > 1.0);
    }

    [Fact]
    public void Prices_and_a_cost_vector_are_refused_together()
        => Assert.Throws<ArgumentException>(() => AcOptimalPowerFlow.Solve(Case("lib/case9.m"), costPerGen: new[] { 1.0, 2.0, 3.0 }, computePrices: true));
}
