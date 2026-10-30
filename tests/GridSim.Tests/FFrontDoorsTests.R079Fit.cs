using GridSim.Cli;
using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R079 (stream F): <c>gridsim fit-frequency</c>, the front door of <see cref="FrequencyConstantsFitter"/>. Pinned: the
/// trace reader's every rule, the --fit list, the pre-event frequency rule, and that the verb prints exactly the value the
/// Core fitter returns for the same inputs (round-trip digits), with its refusals and exit codes.
/// </summary>
public sealed partial class FFrontDoorsTests
{
    // The fitter tests' own synthetic event: GB-scale inertia and demand, a 1.2 GW loss at 1 s.
    private const double FitInertiaMwS = 200_000, FitDemandMw = 30_000, FitLossMw = 1200, FitLossAt = 1.0;

    /// <summary>A measured-style trace sampled from the model with known constants (truth damping 1.6 %/Hz), written as
    /// seconds,Hz in round-trip digits so the CLI reads back exactly these doubles.</summary>
    private static (string Path, List<FrequencyTraceSample> Samples) WriteSyntheticTrace(string dir, double sampleDt = 0.5, double span = 25.0)
    {
        var truth = new DynamicsConfig { DampingPctPerHz = 1.6 };
        FrequencyResponse r = FrequencyDynamics.Simulate(FitInertiaMwS, FitDemandMw, new[] { new FrequencyEvent(FitLossAt, -FitLossMw) }, truth);
        var samples = new List<FrequencyTraceSample>();
        var lines = new List<string> { "seconds,Hz" };
        for (int i = 0; i * sampleDt <= span; i++)
        {
            double t = i * sampleDt;
            int k = Math.Min((int)Math.Round(t / truth.StepSeconds), r.Points.Count - 1);
            samples.Add(new FrequencyTraceSample(t, r.Points[k].Hz));
            lines.Add($"{R(t)},{R(r.Points[k].Hz)}");
        }
        string path = Path.Combine(dir, "trace.csv");
        File.WriteAllLines(path, lines);
        return (path, samples);
    }

    // ---- The trace reader -----------------------------------------------------------------------------------------

    [Fact]
    public void R079_trace_reader_takes_a_header_skips_blank_lines_and_keeps_exact_values()
    {
        var s = FrequencyTraceFile.Parse(new[] { "seconds,Hz", "", "0,50", "0.5, 49.9", "  ", "1.25,49.875" }, "t.csv");
        Assert.Equal(new[] { new FrequencyTraceSample(0.0, 50.0), new FrequencyTraceSample(0.5, 49.9), new FrequencyTraceSample(1.25, 49.875) }, s);
        // No header: the first line is data.
        Assert.Equal(3, FrequencyTraceFile.Parse(new[] { "0,50", "1,49.9", "2,49.8" }, "t.csv").Count);
    }

    [Theory]
    [InlineData(new[] { "0,50", "1,49.9,7", "2,49.8" }, "line 2 has 3 field(s)")]
    [InlineData(new[] { "0,50", "-1,49.9", "2,49.8" }, "line 2: the time is not a finite number of seconds, 0 or more")]
    [InlineData(new[] { "0,50", "1,NaN", "2,49.8" }, "line 2: the frequency is not a finite number")]
    [InlineData(new[] { "0,50", "1,49.9", "1,49.8" }, "line 3: the time 1 s does not increase on the previous row (1 s)")]
    [InlineData(new[] { "0,60", "1,59.9", "2,59.8" }, "line 1: 60 Hz is 5 Hz or more from the 50 Hz nominal")]
    [InlineData(new[] { "seconds,Hz", "0,50", "1,49.9" }, "2 data row(s); the fit needs at least 3")]
    [InlineData(new[] { "seconds,Hz", "x,50", "1,49.9", "2,49.8" }, "line 2: the time is not a finite number of seconds")]
    public void R079_trace_reader_refuses_each_bad_row_naming_it(string[] lines, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => FrequencyTraceFile.Parse(lines, "t.csv"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.StartsWith("t.csv", ex.Message, StringComparison.Ordinal);
    }

    // ---- --fit, the pre-event frequency, the seed ---------------------------------------------------------------------

    [Fact]
    public void R079_fit_list_parses_the_three_words_in_the_order_given_and_refuses_anything_else()
    {
        Assert.Equal(new[] { FrequencyFitParam.Damping }, FrequencyFitCli.ParseFit("damping"));
        Assert.Equal(new[] { FrequencyFitParam.GovernorTimeConstant, FrequencyFitParam.Damping, FrequencyFitParam.ResponsiveFraction },
            FrequencyFitCli.ParseFit("governor, DAMPING,responsive"));
        Assert.Null(FrequencyFitCli.ParseFit("inertia"));
        Assert.Null(FrequencyFitCli.ParseFit("damping,damping"));
        Assert.Null(FrequencyFitCli.ParseFit(""));
        Assert.Null(FrequencyFitCli.ParseFit("damping,"));
    }

    [Fact]
    public void R079_pre_event_frequency_is_the_last_sample_strictly_before_the_loss()
    {
        var trace = new[] { new FrequencyTraceSample(0, 49.97), new FrequencyTraceSample(0.5, 49.96), new FrequencyTraceSample(1.0, 49.9) };
        Assert.Equal(49.96, FrequencyFitCli.PreEventHz(trace, 1.0));     // the sample AT the loss is not before it
        Assert.Equal(49.9, FrequencyFitCli.PreEventHz(trace, 1.01));
        Assert.Null(FrequencyFitCli.PreEventHz(trace, 0.0));              // the trace starts at the loss: nominal start
        // The seed is the shipped default config, plus the start frequency when there is one.
        Assert.Equal(new DynamicsConfig(), FrequencyFitCli.Seed(null));
        Assert.Equal(new DynamicsConfig { InitialFrequencyHz = 49.96 }, FrequencyFitCli.Seed(49.96));
    }

    // ---- The verb, as a process -------------------------------------------------------------------------------------

    /// <summary>
    /// The verb prints exactly what the Core fitter returns for the same trace and inputs (the CLI composition is the
    /// trace reader, the pre-event seed and Fit with the default optimiser), and it recovers the truth the trace was made
    /// with to the fitter tests' own tolerance.
    /// </summary>
    [Fact]
    public void R079_fit_frequency_prints_the_core_fit_exactly()
    {
        string dir = TempDir();
        try
        {
            (string path, List<FrequencyTraceSample> samples) = WriteSyntheticTrace(dir);
            CliProcess.Result r = CliProcess.RunBuilt("fit-frequency", path, "--inertia", "200", "--demand", "30000", "--loss", "1200");
            Assert.True(r.ExitCode == 0, r.ToString());

            DynamicsConfig seed = FrequencyFitCli.Seed(FrequencyFitCli.PreEventHz(samples, FitLossAt));
            Assert.Equal(50.0, seed.InitialFrequencyHz);   // the sample at 0 s, before the loss at 1 s
            FrequencyFitResult fit = FrequencyConstantsFitter.Fit(FitInertiaMwS, FitDemandMw, new FrequencyEvent(FitLossAt, -FitLossMw),
                samples, seed, new[] { FrequencyFitParam.Damping });
            Assert.True(fit.Converged);
            Assert.True(Math.Abs(fit.DampingPctPerHz - 1.6) < 0.02, $"recovered damping {fit.DampingPctPerHz} vs truth 1.6");

            Assert.Contains("FREQUENCY CONSTANTS FIT", r.StdOut, StringComparison.Ordinal);
            string dampingRow = r.StdOut.Split('\n').Single(l => l.StartsWith("damping (% of demand per Hz)", StringComparison.Ordinal));
            Assert.Contains(R(fit.DampingPctPerHz), dampingRow, StringComparison.Ordinal);
            Assert.EndsWith("fitted", dampingRow.TrimEnd(), StringComparison.Ordinal);
            Assert.EndsWith("held", r.StdOut.Split('\n').Single(l => l.StartsWith("responsive fraction", StringComparison.Ordinal)).TrimEnd(), StringComparison.Ordinal);
            Assert.Contains($"RMS error {R(fit.RmsErrorHz)} Hz over {samples.Count} samples", r.StdOut, StringComparison.Ordinal);
            Assert.Contains($"nadir error {R(fit.NadirErrorHz)} Hz", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("Start:   pre-event 50 Hz", r.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("weakly identified", r.StdOut, StringComparison.Ordinal);   // one constant fitted
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void R079_fitting_more_than_one_constant_says_it_is_weakly_identified()
    {
        string dir = TempDir();
        try
        {
            (string path, _) = WriteSyntheticTrace(dir);
            CliProcess.Result r = CliProcess.RunBuilt("fit-frequency", path, "--inertia", "200", "--demand", "30000", "--loss", "1200",
                "--fit", "damping,governor", "--max-iter", "4000");
            Assert.True(r.ExitCode is 0 or 3, r.ToString());
            Assert.Contains("weakly identified", r.StdOut, StringComparison.Ordinal);
            Assert.EndsWith("fitted", r.StdOut.Split('\n').Single(l => l.StartsWith("governor time constant", StringComparison.Ordinal)).TrimEnd(), StringComparison.Ordinal);
        }
        finally { Clean(dir); }
    }

    /// <summary>An optimiser that runs out of iterations is a convergence failure: exit 3, never a fit.</summary>
    [Fact]
    public void R079_a_fit_that_does_not_converge_exits_3()
    {
        string dir = TempDir();
        try
        {
            (string path, _) = WriteSyntheticTrace(dir);
            CliProcess.Result r = CliProcess.RunBuilt("fit-frequency", path, "--inertia", "200", "--demand", "30000", "--loss", "1200", "--max-iter", "1");
            Assert.True(r.ExitCode == 3, r.ToString());
            Assert.Contains("NOT CONVERGED", r.StdErr, StringComparison.Ordinal);
        }
        finally { Clean(dir); }
    }

    [Theory]
    [InlineData(new[] { "--demand", "30000", "--loss", "1200" }, "needs --inertia")]
    [InlineData(new[] { "--inertia", "200", "--loss", "1200" }, "needs --demand")]
    [InlineData(new[] { "--inertia", "200", "--demand", "30000" }, "needs --loss")]
    [InlineData(new[] { "--inertia", "0", "--demand", "30000", "--loss", "1200" }, "--inertia expected")]
    [InlineData(new[] { "--inertia", "200", "--demand", "30000", "--loss", "1200", "--loss-at", "-1" }, "--loss-at expected")]
    [InlineData(new[] { "--inertia", "200", "--demand", "30000", "--loss", "1200", "--loss-at", "25" }, "is not before the last sample")]
    [InlineData(new[] { "--inertia", "200", "--demand", "30000", "--loss", "1200", "--fit", "inertia" }, "--fit expected")]
    [InlineData(new[] { "--inertia", "200", "--demand", "30000", "--loss", "1200", "--max-iter", "0" }, "--max-iter expected")]
    [InlineData(new[] { "--inertia", "200", "--demand", "30000", "--loss", "1200", "--asset-dynamics" }, "plain lumped swing")]
    [InlineData(new[] { "--inertia", "200", "--demand", "30000", "--loss", "1200", "--top", "3" }, "fit-frequency does not take --top")]
    public void R079_usage_errors_exit_2(string[] flags, string expected)
    {
        string dir = TempDir();
        try
        {
            (string path, _) = WriteSyntheticTrace(dir);
            CliProcess.Result r = CliProcess.RunBuilt(new[] { "fit-frequency", path }.Concat(flags).ToArray());
            Assert.True(r.ExitCode == 2, r.ToString());
            Assert.Contains(expected, r.StdErr, StringComparison.Ordinal);
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void R079_no_trace_argument_exits_2_and_a_bad_or_missing_trace_exits_1()
    {
        CliProcess.Result none = CliProcess.RunBuilt("fit-frequency", "--inertia", "200", "--demand", "30000", "--loss", "1200");
        Assert.True(none.ExitCode == 2, none.ToString());
        Assert.Contains("needs a trace file", none.StdErr, StringComparison.Ordinal);

        string dir = TempDir();
        try
        {
            string bad = Path.Combine(dir, "bad.csv");
            File.WriteAllLines(bad, new[] { "0,50", "1,49.9", "0.5,49.8" });
            CliProcess.Result r = CliProcess.RunBuilt("fit-frequency", bad, "--inertia", "200", "--demand", "30000", "--loss", "1200");
            Assert.True(r.ExitCode == 1, r.ToString());
            Assert.Contains("line 3", r.StdErr, StringComparison.Ordinal);

            CliProcess.Result missing = CliProcess.RunBuilt("fit-frequency", Path.Combine(dir, "nope.csv"), "--inertia", "200", "--demand", "30000", "--loss", "1200");
            Assert.True(missing.ExitCode == 1, missing.ToString());
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void R079_fit_frequency_has_help()
    {
        CliProcess.Result r = CliProcess.RunBuilt("fit-frequency", "--help");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("gridsim fit-frequency <trace.csv> --inertia <GVA.s> --demand <MW> --loss <MW>", r.StdOut, StringComparison.Ordinal);
    }
}
