using Xunit;

namespace GridSim.Tests;

public partial class Lf1LowHangingTests
{
    // =================================================================================================================
    // N248: the two loose sidecar defaults are recorded with the reason they cannot be tightened without a measurement
    // (not loosened, not moved blind); an unreadable export is exit 3 in the classical and small-signal sidecars.
    // =================================================================================================================

    private static string N248Tool(string name) =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), "tools", "crossval", name)).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void N248_the_classical_default_is_unchanged_and_its_reason_is_recorded()
    {
        string py = N248Tool("crossval_classical.py");
        Assert.Contains("ap.add_argument(\"--tol\", type=float, default=1e-3)", py, StringComparison.Ordinal);
        Assert.Contains("Why the default --tol is still 1e-3 rad (N248", py, StringComparison.Ordinal);
        Assert.Contains("FIXED-step RK4", py, StringComparison.Ordinal);
    }

    [Fact]
    public void N248_the_estimation_defaults_are_unchanged_and_their_reason_is_recorded()
    {
        string py = N248Tool("crossval_estimation.py");
        Assert.Contains("ap.add_argument(\"--tol-vm\", type=float, default=1e-6)", py, StringComparison.Ordinal);
        Assert.Contains("ap.add_argument(\"--tol-va\", type=float, default=1e-5)", py, StringComparison.Ordinal);
        Assert.Contains("Why the defaults are still --tol-vm 1e-6 pu and --tol-va 1e-5 deg (N248", py, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("crossval_classical.py")]
    [InlineData("crossval_smallsignal.py")]
    public void N248_an_unreadable_export_is_exit_3_not_python_s_usage_code_2(string tool)
    {
        string py = N248Tool(tool);
        Assert.DoesNotContain("return 2\n", py, StringComparison.Ordinal);
        Assert.Contains("return 3   # N248", py, StringComparison.Ordinal);
        Assert.Contains("3 = setup error", py, StringComparison.Ordinal);
    }

    /// <summary>N280 (October 2026): the frequency sidecar now matches the classical and small-signal ones. An
    /// unreadable export, or one with the wrong schema tag, exits 3 (setup error), never 2 (which Python also uses for
    /// an argparse usage error, so a bad file used to look like a bad command line). Added test; nothing existing
    /// moved.</summary>
    [Fact]
    public void N280_crossval_freq_an_unreadable_export_is_exit_3_not_python_s_usage_code_2()
    {
        string py = N248Tool("crossval_freq.py");
        Assert.DoesNotContain("return 2\n", py, StringComparison.Ordinal);
        Assert.Contains("return 3   # N280", py, StringComparison.Ordinal);
        Assert.Contains("3 = setup error", py, StringComparison.Ordinal);
        Assert.DoesNotContain("2 = setup error", py, StringComparison.Ordinal);
    }
}
