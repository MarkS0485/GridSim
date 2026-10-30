using System.Numerics;
using System.Text.Json;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R265 (October 2026 review): a bus with no usable base kV (0, negative, non-finite or missing) fell into the
/// IEEE 519 "&lt;= 1 kV" row - the MOST lenient tier (8% THD, 5% IHD) - so an imported 400 kV bus with BaseKv = 0
/// passed at 4% THD instead of failing its 1.5% limit. It is now judged at the strictest tier and never
/// certified, in both the IEEE 519 and the G5/5 verdicts, and flagged.
/// </summary>
public class HarmonicComplianceUnknownKvTests
{
    private static (GridModel Model, HarmonicPenetrationResult Pen) OneBus(double baseKv, double v5Pu)
    {
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(7, BusType.Pq, BaseKv: baseKv) },
            Generators = Array.Empty<Generator>(),
            Branches = Array.Empty<Branch>(),
        };
        var pen = new HarmonicPenetrationResult(new[] { 5 }, new[] { 7 }, new[] { new[] { new Complex(v5Pu, 0) } });
        return (model, pen);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-33.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_unknown_voltage_level_is_judged_at_the_strictest_tier_and_never_certified(double kv)
    {
        // 4% THD: compliant under the old (wrong) <= 1 kV row's 8%, a violation at the true 400 kV tier's 1.5%.
        (GridModel m, HarmonicPenetrationResult pen) = OneBus(kv, 0.04);
        BusComplianceResult r = Assert.Single(HarmonicCompliance.AssessIeee519(m, pen));
        Assert.False(r.VoltageLevelKnown);
        Assert.False(r.Compliant);
        Assert.Equal(1.5, r.ThdLimitPct);
        Assert.Equal(1.0, r.IhdLimitPct);
    }

    [Fact]
    public void Even_negligible_distortion_cannot_be_certified_without_a_voltage_level()
    {
        (GridModel m, HarmonicPenetrationResult pen) = OneBus(0.0, 0.0001);   // 0.01% THD
        BusComplianceResult r = Assert.Single(HarmonicCompliance.AssessIeee519(m, pen));
        Assert.False(r.Compliant);
        Assert.False(r.VoltageLevelKnown);
    }

    [Fact]
    public void A_known_voltage_level_is_unchanged()
    {
        (GridModel m, HarmonicPenetrationResult pen) = OneBus(0.4, 0.04);
        BusComplianceResult r = Assert.Single(HarmonicCompliance.AssessIeee519(m, pen));
        Assert.True(r.VoltageLevelKnown);
        Assert.True(r.Compliant);              // 4% under the LV 8% THD; 4% IHD under the LV 5%
        Assert.Equal(8.0, r.ThdLimitPct);
    }

    [Fact]
    public void G55_verdict_follows_the_same_rule()
    {
        (GridModel mk, HarmonicPenetrationResult pk) = OneBus(0.4, 0.036);
        (double levelK, bool okK) = HarmonicCompliance.G55Verdict(Assert.Single(HarmonicCompliance.AssessIeee519(mk, pk)));
        Assert.Equal(5.0, levelK);
        Assert.True(okK);

        (GridModel mu, HarmonicPenetrationResult pu) = OneBus(0.0, 0.0001);
        (double levelU, bool okU) = HarmonicCompliance.G55Verdict(Assert.Single(HarmonicCompliance.AssessIeee519(mu, pu)));
        // Moved 2026-10 (G02, authorised by Mark's 2026-10-02 best-judgement ruling; source ENA EREC G5 Issue 5 (2020),
        // docs/reference S139, Table 1 p.21): the strictest headline THD planning level is 3% (the 66-230 kV and the
        // above-230 kV rows), not the unsourced 2.0% the old table gave 275/400 kV. Before: 2.0; after: 3.0.
        Assert.Equal(3.0, levelU);             // the strictest headline level (G5 Issue 5 Table 1, V > 66 kV)
        Assert.False(okU);
    }

    [Fact]
    public void Export_flags_the_unknown_voltage_level()
    {
        (GridModel m, HarmonicPenetrationResult pen) = OneBus(0.0, 0.04);
        var sources = new HarmonicSourceSet { Case = "one-bus", Sources = Array.Empty<HarmonicSource>() };
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen);
        var meta = new SolveExportMeta("one-bus", "harmonics", "test", "2026-10-02T00:00:00Z", 0.0, "test");
        using JsonDocument doc = JsonDocument.Parse(HarmonicExport.ToJson(m, sources, pen, idx, meta,
            compliance: HarmonicCompliance.AssessIeee519(m, pen)));
        JsonElement row = Assert.Single(doc.RootElement.GetProperty("compliance").EnumerateArray());
        Assert.False(row.GetProperty("voltageLevelKnown").GetBoolean());
        Assert.False(row.GetProperty("compliant").GetBoolean());
    }
}
