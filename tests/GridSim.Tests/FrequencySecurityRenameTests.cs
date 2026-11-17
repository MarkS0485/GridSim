using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P0: the NESO RP1 panel is now labelled FREQUENCY SECURITY. A --tab value that opened it under the old label
/// still opens it (existing shortcuts and scripts), and the new label opens it directly.
/// </summary>
public sealed class FrequencySecurityRenameTests
{
    private static readonly string[] Tabs =
    {
        "NETWORK", "GENERATION & INERTIA", "THE MACHINE", "SCADA", "OPERATOR", "MARKET", "FREQUENCY SECURITY", "MECHANICAL",
        "HUMAN SCALE", "SPEED DOMAINS", "INVERTER FLEET", "EXPERIMENTAL", "HARMONICS", "CYCLE", "ASSETS", "ABOUT",
    };

    private static DesktopLaunchPlan Plan(string tab) =>
        DesktopLaunchArgs.Parse(new[] { "--tab", tab }).Resolve(
            new[] { "gb-full" }, new[] { 9 }, new[] { "presolved" }, Tabs, new[] { "GB reduced (data/uk)" });

    [Theory]
    [InlineData("FREQUENCY SECURITY")]
    [InlineData("frequency")]
    [InlineData("NESO RP1")]
    [InlineData("neso rp1")]
    [InlineData("rp1")]
    [InlineData("neso")]
    public void Old_and_new_labels_open_the_frequency_security_tab(string value)
    {
        DesktopLaunchPlan plan = Plan(value);
        Assert.Empty(plan.Errors);
        Assert.Equal(Array.IndexOf(Tabs, "FREQUENCY SECURITY"), plan.TabIndex);
    }

    [Fact]
    public void A_value_that_matches_neither_label_is_still_refused()
    {
        DesktopLaunchPlan plan = Plan("rp2");
        Assert.Null(plan.TabIndex);
        Assert.Contains(plan.Errors, e => e.Contains("--tab 'rp2' matches no tab"));
    }

    [Fact]
    public void A_value_that_matches_a_current_tab_is_not_redirected()
    {
        // "E" matched NETWORK first before the rename and still does; the legacy fallback only applies to no-match.
        Assert.Equal(0, Plan("E").TabIndex);
    }
}
