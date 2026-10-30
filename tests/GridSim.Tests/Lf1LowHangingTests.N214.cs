using System.Text.Json;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

public partial class Lf1LowHangingTests
{
    // =================================================================================================================
    // N214: the GDA bridge's estimate block can say "not run" (optional chiTest); without it the text is the old one.
    // (The HandleState tests are in BridgeProtocolTests, which owns the wire fixtures.)
    // =================================================================================================================

    [Fact]
    public void N214_without_chiTest_the_quality_text_is_exactly_the_old_one()
    {
        var pass = new BridgeProtocol.EstimateBlock { ChiPassed = true, ObjectiveJ = 41.26, MeasurementsN = 120 };
        var fail = new BridgeProtocol.EstimateBlock { ChiPassed = false, ObjectiveJ = 900.04, MeasurementsN = 98, RemovedN = 2 };
        Assert.Equal("chi-square PASS  *  J=41.3  n=120", pass.QualitySummary());
        Assert.Equal("chi-square FAIL  *  J=900.0  n=98  removed=2", fail.QualitySummary());
        pass.RequireConsistentChiTest();   // absent is fine
        fail.RequireConsistentChiTest();
    }

    [Theory]
    [InlineData("passed", true, "chi-square PASS", true)]
    [InlineData("failed", false, "chi-square FAIL", false)]
    [InlineData("not-run", false, "chi-square not run", false)]
    public void N214_each_chiTest_value_reads_as_its_own_verdict(string chiTest, bool chiPassed, string verdict, bool ok)
    {
        var est = new BridgeProtocol.EstimateBlock { ChiPassed = chiPassed, ChiTest = chiTest, ObjectiveJ = 1.0, MeasurementsN = 3 };
        est.RequireConsistentChiTest();
        Assert.Equal($"{verdict}  *  J=1.0  n=3", est.QualitySummary());
        Assert.Equal(ok, est.QualityOk);
    }

    [Fact]
    public void N214_chiTest_reads_from_the_camelCase_wire_field_and_is_null_when_absent()
    {
        var absent = JsonSerializer.Deserialize<BridgeProtocol.EstimateBlock>("{\"chiPassed\":false}", BridgeProtocol.JsonOptions)!;
        Assert.Null(absent.ChiTest);
        var present = JsonSerializer.Deserialize<BridgeProtocol.EstimateBlock>("{\"chiPassed\":false,\"chiTest\":\"not-run\"}", BridgeProtocol.JsonOptions)!;
        Assert.Equal(BridgeProtocol.EstimateBlock.ChiTestNotRun, present.ChiTest);
    }

    [Theory]
    [InlineData("NOT-RUN", false)]
    [InlineData("pass", true)]
    [InlineData("passed", false)]
    [InlineData("not-run", true)]
    public void N214_an_unknown_or_contradictory_chiTest_is_refused_by_name(string chiTest, bool chiPassed)
    {
        var est = new BridgeProtocol.EstimateBlock { ChiPassed = chiPassed, ChiTest = chiTest };
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => est.RequireConsistentChiTest());
        Assert.Contains($"'{chiTest}'", ex.Message, StringComparison.Ordinal);
    }
}
