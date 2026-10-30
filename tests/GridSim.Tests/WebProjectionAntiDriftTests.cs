using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using GridSim.Web.Visuals;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// THE anti-drift guarantee. Proves the web projection layer consumes the shared <c>GridSim.Core.Presentation</c>
/// presenters rather than a private copy of the numbers: each test recomputes a projected quantity DIRECTLY from
/// the Core presenter over the same fleet and asserts the projection matches. If a future edit reintroduced a
/// hardcoded carbon table or design-basis figure in the web layer, the recomputed expectation would diverge and
/// the test would fail. (The WPF twin consumes the same presenters by construction - verified in code review;
/// a Windows-only WPF comparison is left as a manual/CI follow-up per the task's no-WPF-reference rule.)
/// </summary>
public sealed class WebProjectionAntiDriftTests
{
    /// <summary>Recompute grid-carbon intensity (kg/kWh) straight from <see cref="CarbonFactors"/> over the
    /// fleet, exactly as the MARKET narrative does - the value the projection must reproduce.</summary>
    private static double ExpectedCarbonKgPerKwh(GridModel model)
    {
        double numer = 0, total = 0;
        foreach (Generator g in model.Generators)
        {
            FuelInfo fi = Fuels.Classify(g.Type);
            double mw = System.Math.Max(0, g.Pg);
            total += mw;
            numer += mw * CarbonFactors.IntensityKgPerMwh(fi.Name, fi.Class == FuelClass.Fossil) / 1000.0;
        }
        return total > 0 ? numer / total : 0;
    }

    [Fact]
    public void FuelBreakdown_Carbon_ComesFromTheSharedCarbonPresenter()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        var fb = new FuelBreakdown(f.Model);

        // The projection's carbon read must equal a fresh computation off CarbonFactors.IntensityKgPerMwh -
        // i.e. it is the SAME source, not a drifted private copy.
        Assert.Equal(ExpectedCarbonKgPerKwh(f.Model), fb.CarbonKgPerKwh, 12);
        // Sanity: a mixed fossil+converter fleet has a positive, finite carbon intensity.
        Assert.True(fb.CarbonKgPerKwh > 0 && double.IsFinite(fb.CarbonKgPerKwh));
    }

    [Fact]
    public void FuelBreakdown_SingleFuelCarbon_EqualsThatFuelsFactor()
    {
        // A coal-only grid collapses to coal's factor (937 kg/MWh -> 0.937 kg/kWh) whatever the exact solved MW.
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.CoalOnlyFleet());
        var fb = new FuelBreakdown(f.Model);
        Assert.Equal(CarbonFactors.IntensityKgPerMwh("Coal", isFossil: true) / 1000.0, fb.CarbonKgPerKwh, 12);
        Assert.Equal(0.937, fb.CarbonKgPerKwh, 6);
    }

    [Fact]
    public void Market_Carbon_MatchesFuelBreakdown()
    {
        // The MARKET tab must not recompute carbon itself - it carries fb.CarbonKgPerKwh through unchanged.
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        var fb = new FuelBreakdown(f.Model);
        MarketDto mkt = TabProjections.Market(f, fb);
        Assert.Equal(fb.CarbonKgPerKwh, mkt.CarbonKgPerKwh, 12);
        Assert.Equal(ExpectedCarbonKgPerKwh(f.Model), mkt.CarbonKgPerKwh, 12);
    }

    [Fact]
    public void Rp1_CredibleLoss_IsTheSqssSecuredLargestLoss()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        var fb = new FuelBreakdown(f.Model);
        Rp1Dto rp1 = TabProjections.Rp1(f, fb);

        // Today's SQSS rules: the largest credible loss is the single largest secured infeed loss (the shared
        // FuelBreakdown largest-online single infeed, any technology since B4 N039). No 3,600 MW design-basis / 1,800 MW "fiction" framing.
        Assert.Equal(fb.LargestUnitMw, rp1.CredibleLossMw);
    }

    [Fact]
    public void Operator_LargestLoss_IsTheFuelBreakdownLargestSecuredInfeed()   // B4 N039: was ..._LargestSynchronousUnit
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        var fb = new FuelBreakdown(f.Model);
        OperatorDto op = TabProjections.Operator(f, fb);

        // OPERATOR's securing quantity is the shared FuelBreakdown largest-online single infeed (never the
        // lake's 1,800 MW convention) - it must read fb directly, not re-derive its own largest.
        Assert.Equal(fb.LargestUnitMw, op.LargestLossMw, 9);
        Assert.Equal(fb.LargestUnitName, op.LargestLossName);
        // Moved 2026-10 (B4 N039, Ruling R1: Mark delegated EE calls 2026-10-04; source NETS SQSS v2.10 para 5.1.2 "a
        // single power park module", NESO FRCR Methodology v3 s.4.1.1 Table 2 "an event that disconnects one BMU", any
        // technology): the 2,000 MW wind farm is a single secured infeed and the biggest one, so it is the credible
        // loss. Before: "Big CCGT", 1500.0 MW (converters excluded). After: "Wind farm", 2000.0 MW (the fixture has no
        // OLTC, so the solver's RegulatedModel is the fixture itself and the wind farm's Pg stays exactly 2000).
        Assert.Equal("Wind farm", op.LargestLossName);
        Assert.Equal(2000.0, op.LargestLossMw, 6);
    }
}
