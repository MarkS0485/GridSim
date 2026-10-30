using System;
using System.IO;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N251 (CF1, October 2026): <c>--enableExport</c> solve exports carried the everyday 1e-8 pu convergence residual, so
/// one could never meet the machine-epsilon comparison on its own. <c>--export-tight</c> (opt-in) exports the tightest
/// converged state (1e-9 ... 1e-15 pu, the crossval verb's N243 rule) and names it in <c>solve.tightenedTolerancePu</c>.
/// Without the switch the export is unchanged: the new SolveExport argument defaults to null and writes nothing.
/// </summary>
public sealed partial class Cf1OpenCodeFindingsTests
{
    private static (PowerFlowResult R, SolveTrace T) N251Solve(double tol)
    {
        var trace = new SolveTrace();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case14(), new PowerFlowOptions { Tolerance = tol, Trace = trace });
        Assert.True(r.Converged);
        return (r, trace);
    }

    private static readonly SolveExportMeta N251Meta = new("case14", "cpu", "deadbeef", "2026-10-10T00:00:00Z", 1.0, "test");

    [Fact]
    public void N251_without_a_tightened_tolerance_the_export_is_byte_identical_and_has_no_field()
    {
        (PowerFlowResult r, SolveTrace t) = N251Solve(1e-8);
        string before = SolveExport.ToJson(IeeeCases.Case14(), r, t, N251Meta);
        string explicitNull = SolveExport.ToJson(IeeeCases.Case14(), r, t, N251Meta, tightenedTolerancePu: null);
        Assert.Equal(before, explicitNull);
        Assert.DoesNotContain("tightenedTolerancePu", before, StringComparison.Ordinal);
    }

    [Fact]
    public void N251_a_tightened_export_names_its_tolerance_and_only_adds_that_field()
    {
        (PowerFlowResult r, SolveTrace t) = N251Solve(1e-11);
        string plain = SolveExport.ToJson(IeeeCases.Case14(), r, t, N251Meta);
        string tight = SolveExport.ToJson(IeeeCases.Case14(), r, t, N251Meta, tightenedTolerancePu: 1e-11);
        using JsonDocument doc = JsonDocument.Parse(tight);
        JsonElement solve = doc.RootElement.GetProperty("solve");
        Assert.Equal(1e-11, solve.GetProperty("tightenedTolerancePu").GetDouble());
        Assert.True(solve.GetProperty("maxMismatch").GetDouble() <= 1e-11);
        // The same state written twice: removing the one property gives the plain document's solve block.
        using JsonDocument p = JsonDocument.Parse(plain);
        Assert.False(p.RootElement.GetProperty("solve").TryGetProperty("tightenedTolerancePu", out _));
        Assert.Equal(p.RootElement.GetProperty("solve").GetProperty("maxMismatch").GetDouble(), solve.GetProperty("maxMismatch").GetDouble());
        Assert.Equal(p.RootElement.GetProperty("result").GetRawText(), doc.RootElement.GetProperty("result").GetRawText());
    }

    [Fact]
    public void N251_a_tightened_tolerance_is_refused_on_an_estimate_or_when_not_positive_and_finite()
    {
        (PowerFlowResult r, SolveTrace t) = N251Solve(1e-8);
        var est = new SolveExportEstimate("Converged", 1e-9, "Wls");
        Assert.Throws<ArgumentException>(() => SolveExport.ToJson(IeeeCases.Case14(), r, t, N251Meta, estimate: est, tightenedTolerancePu: 1e-12));
        Assert.Throws<ArgumentException>(() => SolveExport.ToJson(IeeeCases.Case14(), r, t, N251Meta, tightenedTolerancePu: 0));
        Assert.Throws<ArgumentException>(() => SolveExport.ToJson(IeeeCases.Case14(), r, t, N251Meta, tightenedTolerancePu: double.NaN));
    }

    [Fact]
    public void N251_the_cli_switch_tightens_the_export_and_is_refused_where_it_would_be_ignored()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n251-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string tightPath = Path.Combine(dir, "tight.json");
            CliProcess.Result tight = CliProcess.RunBuilt("case14", "--enableExport=" + tightPath, "--export-tight");
            Assert.True(tight.ExitCode == 0, tight.ToString());
            Assert.Contains("[export] tightened: solve tolerance ", tight.StdOut, StringComparison.Ordinal);
            using (JsonDocument d = JsonDocument.Parse(File.ReadAllText(tightPath)))
            {
                JsonElement solve = d.RootElement.GetProperty("solve");
                double tol = solve.GetProperty("tightenedTolerancePu").GetDouble();
                Assert.True(tol <= 1e-9 && tol >= 1e-15, tol.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                Assert.True(solve.GetProperty("maxMismatch").GetDouble() <= tol);
                Assert.True(solve.GetProperty("converged").GetBoolean());
            }

            string plainPath = Path.Combine(dir, "plain.json");
            CliProcess.Result plain = CliProcess.RunBuilt("case14", "--enableExport=" + plainPath);
            Assert.True(plain.ExitCode == 0, plain.ToString());
            Assert.DoesNotContain("[export] tightened", plain.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("tightenedTolerancePu", File.ReadAllText(plainPath), StringComparison.Ordinal);

            CliProcess.Result alone = CliProcess.RunBuilt("case14", "--export-tight");
            Assert.True(alone.ExitCode == 2, alone.ToString());
            Assert.Contains("--export-tight tightens the state a solve export writes, so it needs --enableExport", alone.StdErr, StringComparison.Ordinal);

            CliProcess.Result harm = CliProcess.RunBuilt("harmonics", "case9", "--enableExport=" + Path.Combine(dir, "h.json"), "--export-tight");
            Assert.True(harm.ExitCode == 2, harm.ToString());
            Assert.Contains("--export-tight applies to the solve export of a case run", harm.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(dir, "h.json")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
