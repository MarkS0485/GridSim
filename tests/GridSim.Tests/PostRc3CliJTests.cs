using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Post-RC3 audit batch J (CLI-21, CLI-22, CLI-24, CLI-07): argument handling. Every newly refused command line exits
/// with the usage code 2 and the CLI's "error: ..." message style, and prints nothing on stdout.
/// </summary>
public sealed class PostRc3CliJTests
{
    [Theory]
    [InlineData("estimate", "case14", "case30", "--synth")]
    [InlineData("crossval", "case9", "case14")]
    [InlineData("replay-event", "gb-2019-08-09", "extra")]
    [InlineData("serve", "case9", "case14", "--measurements-dir", "nowhere")]
    public void CLI21_a_second_positional_is_refused_with_exit_2(params string[] args)
    {
        CliProcess.Result r = CliProcess.RunBuilt(args);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains($"error: {args[0]} takes one ", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("argument but got:", r.StdErr, StringComparison.Ordinal);
        Assert.Equal("", r.StdOut);
    }

    [Fact]
    public void CLI21_interpose_refuses_a_second_positional_with_exit_2()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-postrc3-j-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string crosswalk = Path.Combine(dir, "modbus-crosswalk.json");
            new RtuPointMap
            {
                Case = "case14",
                Bindings = new List<RtuPointBinding> { new() { Address = "MB:U1:3:@100", Kind = MeasurementKind.PInjMw, Bus = 4, Scale = 0.1 } },
            }.Save(crosswalk);
            CliProcess.Result r = CliProcess.RunBuilt("interpose", "case14", "case30", "--protocol", "modbus", "--port", "0",
                "--crosswalk", crosswalk, "--live-seconds", "0.5");
            Assert.Equal(2, r.ExitCode);
            Assert.Contains("error: interpose takes one case argument but got: case14 case30", r.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("listening", r.StdOut, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void CLI21_one_positional_still_runs()
    {
        CliProcess.Result r = CliProcess.RunBuilt("estimate", "case14", "--synth");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("status: Converged", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void CLI22_a_repeated_phaseangle_is_refused_with_exit_2()
    {
        CliProcess.Result r = CliProcess.RunBuilt("estimate", "case14", "--synth",
            "--phaseangle", "4:-3.1:2026-01-01T00:00:00Z:50", "--phaseangle", "9:-7.0:2026-01-01T00:00:00Z:50");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("error: --phaseangle given more than once", r.StdErr, StringComparison.Ordinal);
        Assert.Equal("", r.StdOut);
    }

    [Fact]
    public void CLI24_scuc_periods_zero_is_refused_with_exit_2()
    {
        CliProcess.Result r = CliProcess.RunBuilt("scuc", "case14", "--periods", "0");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("error: --periods expected at least 1 but got '0'", r.StdErr, StringComparison.Ordinal);
        Assert.Equal("", r.StdOut);
    }

    [Theory]
    [InlineData("--allow-unmodelled")]
    [InlineData("--tap-control")]
    [InlineData("--shunt-control")]
    public void CLI07_dynamics_study_names_a_refused_global_switch(string flag)
    {
        CliProcess.Result r = CliProcess.RunBuilt("dynamics-study", "study.json", flag);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains($"error: dynamics-study takes its interpretation options from the study file; {flag} would change", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void CLI07_dynamics_study_names_a_refused_switch_that_takes_a_value()
    {
        CliProcess.Result r = CliProcess.RunBuilt("dynamics-study", "study.json", "--psse-revision", "33");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("--psse-revision would change", r.StdErr, StringComparison.Ordinal);
    }
}
