using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>R158: ClassicalInit's literals (default X'd, |V|^2 floor, scheduled-share threshold, bolted-fault shunt,
/// CCT bisection cap) are named. Consts fold to the same IL literal, so no number moves; the tests pin each name to its
/// old literal, tie the shared values to their RMS twins, and guard the source.</summary>
public sealed partial class HHygieneTests
{
    [Fact]
    public void R158_classical_constants_equal_the_literals_they_replaced()
    {
        Assert.Equal(0.30, ClassicalInit.DefaultTransientReactancePu);
        Assert.Equal(1e-9, ClassicalInit.MinVoltageSquaredPu);
        Assert.Equal(1e-9, ClassicalInit.MinScheduledShareMw);
        Assert.Equal(1e7, ClassicalInit.BoltedFaultAdmittancePu);
        Assert.Equal(18, ClassicalInit.CctDefaultBisectionIterations);
        // The same physical conventions as the RMS initialisation: one value each.
        Assert.Equal(SubtransientInit.MinVoltageSquaredPu, ClassicalInit.MinVoltageSquaredPu);
        Assert.Equal(SubtransientInit.MinScheduledPowerMw, ClassicalInit.MinScheduledShareMw);
        Assert.Equal(SubtransientInit.BoltedFaultShuntPu, ClassicalInit.BoltedFaultAdmittancePu);
        Assert.Equal(RmsTransientStability.CctDefaultBisectionIterations, ClassicalInit.CctDefaultBisectionIterations);
    }

    [Fact]
    public void R158_cli_synthetic_xdp_is_the_classical_default()
    {
        // CliDefaults is internal to the CLI (not visible to the tests), and its literal is cited by the v2 kind spec,
        // so it stays a literal; the source text proves it is the same 0.30.
        string cli = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Cli", "CliDefaults.cs");
        Assert.Contains("public const double SyntheticXdpPu = 0.30;", cli);
        Assert.Equal(0.30, ClassicalInit.DefaultTransientReactancePu);
    }

    [Fact]
    public void R158_classical_init_source_keeps_no_bare_copies()
    {
        string src = CoreSrc("Dynamics", "Distributed", "ClassicalInit.cs");
        Assert.DoesNotContain("Math.Max(1e-9,", src);
        Assert.DoesNotContain("> 1e-9 ?", src);
        Assert.DoesNotContain("?? 0.30;", src);
        Assert.DoesNotContain("new Complex(1e7, 0.0)", src);
        Assert.DoesNotContain("int iterations = 18", src);
    }
}
