using System.Text.Json;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N208 (Ruling R1 follow-up, 2026-10-04: "a chi-square test that did not run reports null, never a pass"). After N078
/// a gb-spine estimate can keep no redundancy (dof 0): the chi-square test does not run (threshold NaN), but the
/// estimate export still said <c>chiSquarePassed: true</c> (the estimator's legacy flag is vacuously true then) and the
/// GDA builder stored that as a pass. Now the export writes <c>chiSquarePassed</c> as true / false only when the test
/// ran and as an explicit null when it did not (a documented wire change), and the audit text says chi2=not-run.
/// </summary>
public class N208ChiSquareNotRunTests
{
    private static (EstimationResult Est, JsonElement Estimate) Run(bool detectBadData)
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);
        EstimationResult est = new WlsStateEstimator().Estimate(model, set, new EstimationOptions { DetectBadData = detectBadData });
        var meta = new SolveExportMeta("case9", "test", "abc123", "2026-07-14T00:00:00Z", 0, "test-host");
        JsonElement doc = JsonDocument.Parse(EstimateExport.ToJson(model, set, est, meta)).RootElement.Clone();
        return (est, doc.GetProperty("estimate"));
    }

    [Fact]
    public void A_test_that_did_not_run_exports_chiSquarePassed_as_an_explicit_null()
    {
        (EstimationResult est, JsonElement estimate) = Run(detectBadData: false);
        Assert.Equal(ChiSquareOutcome.NotRun, est.ChiSquareTest);
        Assert.True(double.IsNaN(est.ChiSquareThreshold));
        // The in-memory legacy flag is still vacuously true (documented on EstimationResult) ...
        Assert.True(est.ChiSquarePassed);
        // ... but the wire never calls it a pass: the field is present, in its place, and null.
        Assert.True(estimate.TryGetProperty("chiSquarePassed", out JsonElement passed), "chiSquarePassed must stay in the document");
        Assert.Equal(JsonValueKind.Null, passed.ValueKind);
        // chiSquareTest carries the outcome as its enum number (0 = NotRun), as the wire-format chapter documents.
        Assert.Equal((int)ChiSquareOutcome.NotRun, estimate.GetProperty("chiSquareTest").GetInt32());
        string[] names = estimate.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(Array.IndexOf(names, "chiSquareThreshold") + 1, Array.IndexOf(names, "chiSquarePassed"));
        Assert.Equal(Array.IndexOf(names, "chiSquarePassed") + 1, Array.IndexOf(names, "chiSquareTest"));
    }

    [Fact]
    public void A_test_that_ran_exports_exactly_its_verdict_as_before()
    {
        (EstimationResult est, JsonElement estimate) = Run(detectBadData: true);
        Assert.NotEqual(ChiSquareOutcome.NotRun, est.ChiSquareTest);
        JsonElement passed = estimate.GetProperty("chiSquarePassed");
        // Unchanged bytes for a test that ran: the same true / false the bool wrote, equal to the legacy flag.
        Assert.Equal(est.ChiSquarePassed ? JsonValueKind.True : JsonValueKind.False, passed.ValueKind);
        Assert.Equal(est.ChiSquareTest == ChiSquareOutcome.Passed, passed.GetBoolean());
    }

    [Fact]
    public void The_wire_value_follows_the_outcome_and_never_says_true_for_not_run()
    {
        Assert.Equal(JsonValueKind.True, EstimateExport.ChiSquarePassedJson(ChiSquareOutcome.Passed).ValueKind);
        Assert.Equal(JsonValueKind.False, EstimateExport.ChiSquarePassedJson(ChiSquareOutcome.Failed).ValueKind);
        Assert.Equal(JsonValueKind.Null, EstimateExport.ChiSquarePassedJson(ChiSquareOutcome.NotRun).ValueKind);
        Assert.Equal("true", EstimateExport.ChiSquarePassedJson(ChiSquareOutcome.Passed).GetRawText());
        Assert.Equal("false", EstimateExport.ChiSquarePassedJson(ChiSquareOutcome.Failed).GetRawText());
        Assert.Equal("null", EstimateExport.ChiSquarePassedJson(ChiSquareOutcome.NotRun).GetRawText());
    }

    [Fact]
    public void The_audit_text_of_a_test_that_did_not_run_says_not_run()
    {
        (EstimationResult est, _) = Run(detectBadData: false);
        string outcome = EstimationAuditTrail.Outcome("Wls", est, 1);
        Assert.Contains(" chi2=not-run ", outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("chi2=PASS", outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("chi2=n/a", outcome, StringComparison.Ordinal);
        Assert.Equal("not-run", EstimationAuditTrail.NotRunText);
    }

    [Fact]
    public void The_cli_says_not_run_and_never_n_a()
    {
        // The CLI's chi-square text helper and the consistency line: "not run" in words (source pin; the CLI process
        // tests cover the --no-bad-data line, which already said "chi-square not run").
        string program = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("_ => \"not-run\",", program, StringComparison.Ordinal);
        Assert.Contains("\"not run (no redundancy: dof", program, StringComparison.Ordinal);
        // (A comment above the line still quotes the old "n/a (no redundancy)" text as history; the code may not.)
        Assert.DoesNotContain("? \"n/a (no redundancy)\"", program, StringComparison.Ordinal);
        Assert.DoesNotContain(": \"n/a (chi-square not run)\"", program, StringComparison.Ordinal);
    }
}
