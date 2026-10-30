using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-682: a count flag below its documented minimum is a usage error (exit 2), not raised to the minimum without a
/// word; a negative --parallel is refused (0 still means all cores). GR-697: the licence verb refuses arguments it does
/// not use (it runs before the flag gates). GR-701: transient takes one positional case or --cases, not both.
/// </summary>
public sealed class CliCountAndLicenceArgsProcessTests
{
    [Theory]
    [InlineData("bench", "--runs", "0")]
    [InlineData("bench", "--parallel", "-1")]
    [InlineData("stress", "case9", "--parallel", "-2")]
    [InlineData("nminus1-sweep", "case9", "--parallel", "-1")]
    [InlineData("chain", "case9", "--steps", "0")]
    [InlineData("cycle", "case9", "--cycles", "-3")]
    [InlineData("cycle", "case9", "--workers", "0")]
    [InlineData("transient", "case9", "--log-every", "0")]
    [InlineData("licence", "status", "--bogus")]
    [InlineData("licence", "path", "extra")]
    [InlineData("licence", "import", "a.gridsim", "b.gridsim")]
    [InlineData("transient", "case9", "case14", "--minutes", "0.001")]          // GR-701: a second positional case
    [InlineData("transient", "case14", "--cases", "case9", "--minutes", "0.001")]   // GR-701: positional and --cases
    [InlineData("transient", "--cases", ",", "--minutes", "0.001")]             // GR-701: --cases naming no case
    [InlineData("gda-replay", "--synth", "--policy", "hold")]                   // GR-695: a storage flag without --bess
    [InlineData("gda-replay", "--synth", "--shave-high", "500")]
    [InlineData("replay", "trace.csv", "--sps", "0")]                            // GR-698: a rate outside 0.25..2000
    [InlineData("replay", "trace.csv", "--seconds", "-5")]                       // GR-698: seconds below 1
    public void A_value_below_the_documented_minimum_or_an_unused_argument_exits_2(params string[] args)
    {
        CliProcess.Result r = CliProcess.RunBuilt(args);
        Assert.True(r.ExitCode == 2, $"expected exit 2\n{r}");
        Assert.Contains("error:", r.StdErr, System.StringComparison.Ordinal);
    }
}
