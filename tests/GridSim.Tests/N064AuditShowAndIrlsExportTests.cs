using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N064 (October 2026), the CLI / Core half of the R075 remainder:
/// <list type="bullet">
/// <item>R175's <see cref="EstimationResult.IrlsCapReached"/> was printed by the CLI but missing from
/// <c>estimate-export.json</c>; it is now the additive field <c>estimate.irlsCapReached</c>, and the audit outcome
/// carries <c>irls_cap_reached=true</c> only when the flag is true (every other outcome line is byte-for-byte
/// unchanged).</item>
/// <item>A verified audit log can be shown, not only verified: <see cref="AuditLogFile.Describe"/> is the one display
/// form, used by <c>gridsim audit show</c> (and meant for the web and desktop shells).</item>
/// </list>
/// </summary>
public sealed class N064AuditShowAndIrlsExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-n064-{Guid.NewGuid():N}");

    public N064AuditShowAndIrlsExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort: a leftover temp folder is not a test failure */ }
    }

    private static readonly SolveExportMeta Meta = new("case", "test", "abc123", "2026-10-04T00:00:00Z", 0, "test-host");

    /// <summary>Plain WLS on case9 with the default synthetic set (never hits an IRLS cap).</summary>
    private static (GridModel Model, MeasurementSet Set, EstimationResult Est) Wls9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);
        return (model, set, new WlsStateEstimator().Estimate(model, set));
    }

    /// <summary>The Q13 set-up that is known to hit the cap: LAV on case14, exact set, one 30-sigma outlier.</summary>
    private static (GridModel Model, MeasurementSet Set, EstimationResult Est) LavAtCap14()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });
        var ms = set.Measurements.ToList();
        int victim = ms.FindIndex(m => m.Kind == MeasurementKind.PInjMw);
        ms[victim] = ms[victim] with { Value = ms[victim].Value + 30 * ms[victim].Sigma };
        MeasurementSet bad = set with { Measurements = ms };
        // Moved 2026-10 (END-PASS CF6 rows 2 and 3, end pass; N356, EstimationMethod.Lav is now the exact LP form and the
        // old IRLS form is EstimationMethod.LavIrls, the same code bit for bit, Ruling R1 and Mark's go on the end-pass moves
        // 2026-10-11): the two tests that use this helper pin the IRLS cap flag in the export and the audit line; that
        // behaviour still exists on LavIrls, so the helper is pointed there and both tests keep their old expectations
        // exactly (on Lav: observed irlsCapReached false and an outcome ending "measurements=122 fdia=clear", 1.95.273-275
        // runs). Before -> after: Method = EstimationMethod.Lav -> EstimationMethod.LavIrls. The flag being true on this
        // set and method is observed (Cf6LavExactTests.Lav_irls_keeps_the_old_behaviour_on_the_q13_set_cap_reached_status_converged,
        // green in those runs); the export and audit text on LavIrls is derived from the CF6 notes (LAV export shape with
        // irlsCapReached true; audit "... measurements=N irls_cap_reached=true fdia=clear"), not observed.
        return (model, bad, new WlsStateEstimator().Estimate(model, bad, new EstimationOptions { Method = EstimationMethod.LavIrls }));
    }

    // ------------------------------------------------------------------ the export field

    [Fact]
    public void Export_writes_irlsCapReached_false_for_plain_wls()
    {
        var (model, set, est) = Wls9();
        Assert.False(est.IrlsCapReached);
        JsonElement estimate = JsonDocument.Parse(EstimateExport.ToJson(model, set, est, Meta)).RootElement.GetProperty("estimate");
        Assert.Equal(JsonValueKind.False, estimate.GetProperty("irlsCapReached").ValueKind);
    }

    [Fact]
    public void Export_writes_irlsCapReached_true_when_lav_stops_on_its_cap()
    {
        var (model, set, est) = LavAtCap14();
        Assert.True(est.IrlsCapReached);
        JsonElement estimate = JsonDocument.Parse(EstimateExport.ToJson(model, set, est, Meta)).RootElement.GetProperty("estimate");
        Assert.Equal(JsonValueKind.True, estimate.GetProperty("irlsCapReached").ValueKind);
        // The flag does not change the status (R175): the export still says Converged.
        Assert.Equal("Converged", estimate.GetProperty("status").GetString());
    }

    [Fact]
    public void The_new_field_changes_no_other_value_in_the_export()
    {
        // Additive: removing the one new property gives back a document whose every other value is the same as the
        // values the estimator holds (spot-checked exactly on J, dof and every bus state, bit for bit).
        var (model, set, est) = Wls9();
        JsonElement root = JsonDocument.Parse(EstimateExport.ToJson(model, set, est, Meta)).RootElement;
        JsonElement estimate = root.GetProperty("estimate");
        Assert.Equal(est.ObjectiveJ, estimate.GetProperty("objectiveJ").GetDouble());
        Assert.Equal(est.DegreesOfFreedom, estimate.GetProperty("degreesOfFreedom").GetInt32());
        var buses = estimate.GetProperty("buses").EnumerateArray().ToList();
        Assert.Equal(est.BusIds.Count, buses.Count);
        for (int i = 0; i < buses.Count; i++)
        {
            Assert.Equal(est.Vm[i], buses[i].GetProperty("vm").GetDouble());
            Assert.Equal(est.VaDeg[i], buses[i].GetProperty("vaDeg").GetDouble());
        }
        // Field order: the new field sits after chiSquareTest and before buses, nothing else moved.
        string[] names = estimate.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[]
        {
            "status", "converged", "iterations", "maxStateUpdate", "objectiveJ", "degreesOfFreedom",
            "chiSquareThreshold", "chiSquarePassed", "chiSquareTest", "irlsCapReached", "buses",
        }, names);
    }

    // ------------------------------------------------------------------ the audit outcome

    [Fact]
    public void Outcome_of_a_run_that_did_not_hit_the_cap_is_unchanged()
    {
        var (_, set, est) = Wls9();
        string outcome = EstimationAuditTrail.Outcome("Wls", est, set.Measurements.Count);
        Assert.DoesNotContain("irls", outcome, StringComparison.Ordinal);
        // The exact pre-N064 layout, rebuilt field by field.
        // Moved 2026-10 (N208, Ruling R1 follow-up, Mark delegated 2026-10-04): the not-run text of the rebuilt layout.
        // Before: "n/a". After: "not-run". (This fixture's test runs and passes, so the expected line is unchanged.)
        string chi2 = est.ChiSquareTest == ChiSquareOutcome.Passed ? "PASS" : est.ChiSquareTest == ChiSquareOutcome.Failed ? "FAIL" : "not-run";
        Assert.Equal(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"method=Wls status={est.Status} observable={est.Observability.Observable} J={est.ObjectiveJ:R} chi2={chi2} dof={est.DegreesOfFreedom} removed={est.Removed.Count} measurements={set.Measurements.Count}"),
            outcome);
    }

    [Fact]
    public void Outcome_names_the_irls_cap_when_it_was_reached_before_the_extra_fields()
    {
        var (_, set, est) = LavAtCap14();
        // Moved 2026-10 (END-PASS CF6 row 3; see LavAtCap14): the method label follows the helper, "Lav" -> "LavIrls" (the
        // CLI passes method.ToString()); the asserted ending is unchanged.
        string outcome = EstimationAuditTrail.Outcome("LavIrls", est, set.Measurements.Count, " fdia=clear");
        Assert.EndsWith($"measurements={set.Measurements.Count} irls_cap_reached=true fdia=clear", outcome, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the display form

    [Fact]
    public void Describe_gives_one_ascii_line_with_every_field()
    {
        string path = Path.Combine(_dir, "a.jsonl");
        using (AuditLogWriter w = AuditLogWriter.Open(path))
        {
            w.Append(new DateTime(2026, 10, 4, 9, 30, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                ProvenanceTag.DerivedFromRecorded, "actor=a@b action=estimate outcome=exit=0 method=Wls");
            w.Append(new DateTime(2026, 10, 4, 9, 31, 0, DateTimeKind.Utc), null, ProvenanceTag.Synthetic, "caf\u00e9\nline");
        }
        var entries = AuditLogFile.Load(path);
        Assert.Equal(
            $"#0 run=2026-10-04T09:30:00.0000000Z state=2026-09-01T12:00:00.0000000Z provenance=DerivedFromRecorded hash={entries[0].Hash} actor=a@b action=estimate outcome=exit=0 method=Wls",
            AuditLogFile.Describe(entries[0]));
        // No state time -> "-"; a non-ASCII character -> '?'; a line break -> a space (one entry, one line).
        Assert.Equal(
            $"#1 run=2026-10-04T09:31:00.0000000Z state=- provenance=Synthetic hash={entries[1].Hash} caf? line",
            AuditLogFile.Describe(entries[1]));
    }

    [Fact]
    public void Describe_refuses_null()
        => Assert.Throws<ArgumentNullException>(() => AuditLogFile.Describe(null!));

    // ------------------------------------------------------------------ the CLI verb (built CLI, real process)

    private string TwoEntryLog()
    {
        string path = Path.Combine(_dir, "audit.jsonl");
        using AuditLogWriter w = AuditLogWriter.Open(path);
        w.Append(new DateTime(2026, 10, 4, 9, 30, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "actor=a action=estimate outcome=exit=0 SP1");
        w.Append(new DateTime(2026, 10, 4, 9, 31, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "actor=a action=estimate outcome=exit=3 SP2");
        return path;
    }

    [Fact]
    public void Cli_audit_show_prints_every_entry_then_the_ok_line()
    {
        string path = TwoEntryLog();
        var entries = AuditLogFile.Load(path);
        CliProcess.Result r = CliProcess.RunBuilt("audit", "show", path);
        Assert.True(r.ExitCode == 0, r.ToString());
        string[] lines = r.StdOut.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal(AuditLogFile.Describe(entries[0]), lines[0]);
        Assert.Equal(AuditLogFile.Describe(entries[1]), lines[1]);
        Assert.Equal($"OK {path}: 2 entries, head {entries[1].Hash}", lines[2]);
    }

    [Fact]
    public void Cli_audit_show_prints_nothing_of_a_tampered_log_and_exits_3()
    {
        string path = TwoEntryLog();
        string[] text = File.ReadAllLines(path);
        text[1] = text[1].Replace("SP2", "SP9", StringComparison.Ordinal);
        File.WriteAllText(path, string.Join("\n", text) + "\n");
        CliProcess.Result r = CliProcess.RunBuilt("audit", "show", path);
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.DoesNotContain("SP1", r.StdOut, StringComparison.Ordinal);   // not even the clean prefix
        Assert.Contains("TAMPERED", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_audit_verify_prints_no_entries()
    {
        string path = TwoEntryLog();
        CliProcess.Result r = CliProcess.RunBuilt("audit", "verify", path);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain("SP1", r.StdOut, StringComparison.Ordinal);
        Assert.StartsWith("OK ", r.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("list")]
    public void Cli_audit_usage_errors_exit_2(string sub)
    {
        // "show" with no file, and an unknown subcommand with a file, are usage errors.
        CliProcess.Result r = sub == "show" ? CliProcess.RunBuilt("audit", sub) : CliProcess.RunBuilt("audit", sub, "x.jsonl");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("audit verify|show", r.StdErr, StringComparison.Ordinal);
    }
}
