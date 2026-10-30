using System.Text.Json;
using GridSim.Core.Dynamics;
using GridSim.Core.Scenarios;
using Xunit;

namespace GridSim.Tests;

/// <summary>R178: the H0 export's corner factors are ScenarioCorners' constants (same values as the literals it used to
/// copy) and each corner carries the responsive-fraction cap Apply uses, so a sidecar can reproduce Apply exactly.</summary>
public sealed partial class HHygieneTests
{
    [Fact]
    public void R178_exported_corner_factors_equal_the_old_literals_and_carry_the_cap()
    {
        JsonElement cf = H0StudyExportContractTests.Export().GetProperty("swing").GetProperty("cornerFactors");
        // The exact literals the export used to write (1.5 / 1.5 / 0.7, 1 / 1 / 1, 0.6 / 0.6 / 1.4).
        (string Corner, double D, double R, double G)[] old =
        {
            ("best", 1.5, 1.5, 0.7), ("nominal", 1.0, 1.0, 1.0), ("worst", 0.6, 0.6, 1.4),
        };
        foreach ((string corner, double d, double r, double g) in old)
        {
            JsonElement c = cf.GetProperty(corner);
            Assert.Equal(d, c.GetProperty("damping").GetDouble());
            Assert.Equal(r, c.GetProperty("responsive").GetDouble());
            Assert.Equal(g, c.GetProperty("governor").GetDouble());
        }
        Assert.Equal(1.0, cf.GetProperty("best").GetProperty("responsiveCap").GetDouble());
        Assert.Equal(JsonValueKind.Null, cf.GetProperty("nominal").GetProperty("responsiveCap").ValueKind);
        Assert.Equal(JsonValueKind.Null, cf.GetProperty("worst").GetProperty("responsiveCap").ValueKind);
        Assert.Equal(1.0, ScenarioCorners.MaxResponsiveFraction);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.6)]
    [InlineData(0.8)]   // 0.8 x 1.5 = 1.2: the best corner's cap binds
    [InlineData(1.0)]
    public void R178_exported_factors_reproduce_ScenarioCorners_Apply_bit_for_bit(double baseResponsive)
    {
        JsonElement cf = H0StudyExportContractTests.Export().GetProperty("swing").GetProperty("cornerFactors");
        var cfg = new DynamicsConfig { DampingPctPerHz = 1.3, ResponsiveFraction = baseResponsive, GovernorTimeConstantS = 8.0 };
        foreach ((Corner corner, string key) in new[] { (Corner.Best, "best"), (Corner.Nominal, "nominal"), (Corner.Worst, "worst") })
        {
            DynamicsConfig applied = ScenarioCorners.Apply(cfg, corner);
            JsonElement c = cf.GetProperty(key);
            double responsive = cfg.ResponsiveFraction * c.GetProperty("responsive").GetDouble();
            JsonElement cap = c.GetProperty("responsiveCap");
            if (cap.ValueKind != JsonValueKind.Null) responsive = Math.Min(cap.GetDouble(), responsive);
            // Nominal returns cfg itself (no multiplication), and x * 1.0 == x exactly, so the sidecar recipe agrees.
            AssertBits(applied.DampingPctPerHz, cfg.DampingPctPerHz * c.GetProperty("damping").GetDouble(), $"{key} damping");
            AssertBits(applied.ResponsiveFraction, responsive, $"{key} responsive");
            AssertBits(applied.GovernorTimeConstantS, cfg.GovernorTimeConstantS * c.GetProperty("governor").GetDouble(), $"{key} governor");
        }
    }

    [Fact]
    public void R178_export_source_has_no_copied_factor_literals()
    {
        string src = CoreSrc("IO", "H0StudyExport.cs");
        Assert.DoesNotContain("damping = 1.5", src);
        Assert.DoesNotContain("damping = 0.6", src);
        Assert.Contains("ScenarioCorners.BestDampingScale", src);
        Assert.Contains("ScenarioCorners.MaxResponsiveFraction", src);
    }
}
