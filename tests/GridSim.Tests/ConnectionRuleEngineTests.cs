using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Harmonics.Planning;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-20e: the rule-set engine. The German connection rules are paid text, so the shipped files are empty and flagged; the engine is tested on a
/// TOY rule file written here (every number is synthetic, a made-up requirement, not a value from any standard) and the shipped files are
/// tested to stay unfilled and to be refused by the engine as awaiting.
/// </summary>
public class ConnectionRuleEngineTests
{
    private const string Toy = """
    {
      "schema": "gridsim-connection-rules/1", "id": "toy", "title": "SYNTHETIC toy rules, not a standard",
      "provenance": {"source": "SYNTHETIC TEST FIXTURE", "edition": "none", "filledBy": "test", "date": "2026-11-17"},
      "scope": {"voltageKvMin": 1.0, "voltageKvMax": 60.0, "source": "synthetic"},
      "rules": [
        {"id": "dv", "topic": "voltage change", "clause": "toy 1", "quantity": "dv_pct", "comparison": "<=", "limit": 2.0, "unit": "%"},
        {"id": "pf", "topic": "power factor", "clause": "toy 2", "quantity": "pf", "comparison": "range", "limit": 0.9, "limitHigh": 1.0, "unit": ""},
        {"id": "q", "topic": "reactive", "clause": "toy 3", "quantity": "q_mvar", "comparison": ">=", "limit": 0.5, "unit": "Mvar",
         "when": [{"quantity": "p_mw", "op": ">", "value": 5.0}]},
        {"id": "later", "topic": "awaiting one", "clause": "", "quantity": "x", "comparison": "<=", "limit": null, "unit": ""}
      ]
    }
    """;

    private static IReadOnlyDictionary<string, double> Q(params (string, double)[] q) => q.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Comparisons_margins_and_conditions_follow_the_toy_rules()
    {
        ConnectionRuleSet set = ConnectionRuleSet.Parse(Toy);
        Assert.Equal("PARTLY FILLED", set.Status);
        RuleSetResult r = RuleSetEngine.Evaluate(set, 11.0, Q(("dv_pct", 1.5), ("pf", 0.95), ("p_mw", 8.0), ("q_mvar", 0.4), ("x", 3.0)));
        RuleResult dv = r.Rules.Single(x => x.RuleId == "dv"), pf = r.Rules.Single(x => x.RuleId == "pf"), q = r.Rules.Single(x => x.RuleId == "q");
        Assert.Equal(RuleOutcome.Pass, dv.Outcome);
        Assert.Equal(0.5, dv.Margin!.Value, 12);                               // 2.0 - 1.5
        Assert.Equal(RuleOutcome.Pass, pf.Outcome);
        Assert.Equal(0.05, pf.Margin!.Value, 12);                              // min(0.95 - 0.9, 1.0 - 0.95)
        Assert.Equal(RuleOutcome.Fail, q.Outcome);                             // condition p > 5 holds, 0.4 < 0.5
        Assert.Equal(-0.1, q.Margin!.Value, 12);
        Assert.Equal(RuleOutcome.OutOfScope, r.Rules.Single(x => x.RuleId == "later").Outcome);
        Assert.Equal(RuleSetEngine.NonCompliant, r.Verdict);                   // any failure decides
        Assert.Equal((2, 1, 0, 1, 0), (r.Passed, r.Failed, r.NotApplicable, r.OutOfScope, r.NotEvaluated));
        // The condition not met: the rule does not apply.
        RuleSetResult small = RuleSetEngine.Evaluate(set, 11.0, Q(("dv_pct", 1.5), ("pf", 0.95), ("p_mw", 3.0)));
        Assert.Equal(RuleOutcome.NotApplicable, small.Rules.Single(x => x.RuleId == "q").Outcome);
    }

    [Fact]
    public void A_missing_quantity_or_awaiting_limit_is_never_a_pass()
    {
        ConnectionRuleSet set = ConnectionRuleSet.Parse(Toy);
        RuleSetResult r = RuleSetEngine.Evaluate(set, 11.0, Q(("dv_pct", 1.0), ("pf", 0.95), ("p_mw", 1.0)));
        Assert.Equal(RuleSetEngine.Incomplete, r.Verdict);                     // 'later' is awaiting, nothing failed
        RuleSetResult missing = RuleSetEngine.Evaluate(set, 11.0, Q(("pf", 0.95), ("p_mw", 1.0)));
        Assert.Equal(RuleOutcome.NotEvaluated, missing.Rules.Single(x => x.RuleId == "dv").Outcome);
        Assert.Contains("dv_pct", missing.Rules.Single(x => x.RuleId == "dv").Reason);
        // Out of the voltage range of the set: nothing is evaluated.
        RuleSetResult wrong = RuleSetEngine.Evaluate(set, 110.0, Q(("dv_pct", 1.0)));
        Assert.Equal(RuleSetEngine.WrongScope, wrong.Verdict);
        Assert.All(wrong.Rules, x => Assert.Equal(RuleOutcome.OutOfScope, x.Outcome));
        // Fully filled and all inside: COMPLIANT.
        string full = Toy.Replace("\"limit\": null", "\"limit\": 9.0").Replace("\"clause\": \"\"", "\"clause\": \"toy 4\"");
        Assert.Equal(RuleSetEngine.Compliant, RuleSetEngine.Evaluate(ConnectionRuleSet.Parse(full), 11.0, Q(("dv_pct", 1.0), ("pf", 0.95), ("p_mw", 1.0), ("x", 2.0))).Verdict);
        Assert.Equal(RuleSetEngine.Incomplete, RuleSetEngine.Evaluate(ConnectionRuleSet.Parse(full), 11.0, Q(("dv_pct", 1.0), ("pf", 0.95), ("p_mw", 1.0))).Verdict);
    }

    [Fact]
    public void Repeat_runs_are_identical()
    {
        ConnectionRuleSet set = ConnectionRuleSet.Parse(Toy);
        var q = Q(("dv_pct", 1.5), ("pf", 0.95), ("p_mw", 8.0), ("q_mvar", 0.6));
        RuleSetResult a = RuleSetEngine.Evaluate(set, 11.0, q), b = RuleSetEngine.Evaluate(set, 11.0, q);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"gridsim-connection-rules/1\",\"id\":\"x\",\"bogus\":1}")]
    // a limit without provenance
    [InlineData("{\"schema\":\"gridsim-connection-rules/1\",\"id\":\"x\",\"rules\":[{\"id\":\"a\",\"clause\":\"c\",\"quantity\":\"q\",\"comparison\":\"<=\",\"limit\":1}]}")]
    // a limit without its clause (provenance present)
    [InlineData("{\"schema\":\"gridsim-connection-rules/1\",\"id\":\"x\",\"provenance\":{\"source\":\"s\",\"edition\":\"e\",\"filledBy\":\"f\",\"date\":\"d\"},\"rules\":[{\"id\":\"a\",\"quantity\":\"q\",\"comparison\":\"<=\",\"limit\":1}]}")]
    // a range with one end
    [InlineData("{\"schema\":\"gridsim-connection-rules/1\",\"id\":\"x\",\"rules\":[{\"id\":\"a\",\"quantity\":\"q\",\"comparison\":\"range\",\"limit\":1}]}")]
    // a voltage range without its source
    [InlineData("{\"schema\":\"gridsim-connection-rules/1\",\"id\":\"x\",\"scope\":{\"voltageKvMin\":1,\"voltageKvMax\":2}}")]
    public void Strict_parser_refuses_unprovenanced_or_malformed_files(string json) => Assert.Throws<FormatException>(() => ConnectionRuleSet.Parse(json));

    [Fact]
    public void Shipped_german_rule_sets_are_empty_flagged_and_report_awaiting()
    {
        Assert.Equal(new[] { "bdew-mv-guideline", "vde-ar-n-4105", "vde-ar-n-4110", "vde-ar-n-4120", "vde-ar-n-4130" }, ConnectionRuleSets.BuiltInIds.ToArray());
        foreach (string id in ConnectionRuleSets.BuiltInIds)
        {
            ConnectionRuleSet s = ConnectionRuleSets.Load(id);
            Assert.Equal(0, s.FilledLimits);                       // a shipped file is never silently filled: filling needs a reviewed change here
            Assert.StartsWith("EMPTY", s.Status);
            Assert.NotEmpty(s.Rules);
            double kv = s.Scope.VoltageKvMin is double lo ? lo + 0.5 : 20.0;
            RuleSetResult r = RuleSetEngine.Evaluate(s, kv, Q(("voltage_change_pct", 1.0)));
            Assert.Equal(RuleSetEngine.Incomplete, r.Verdict);
            Assert.All(r.Rules, x => { Assert.Equal(RuleOutcome.OutOfScope, x.Outcome); Assert.Contains("AWAITING STANDARD TEXT", x.Reason); });
        }
        // The four VDE sets carry the public voltage ranges, with a source; 4130 is open-ended upward.
        Assert.Equal(1.0, ConnectionRuleSets.Load("vde-ar-n-4110").Scope.VoltageKvMin);
        Assert.Equal(60.0, ConnectionRuleSets.Load("vde-ar-n-4110").Scope.VoltageKvMax);
        Assert.Null(ConnectionRuleSets.Load("vde-ar-n-4130").Scope.VoltageKvMax);
        Assert.Equal(RuleSetEngine.WrongScope, RuleSetEngine.Evaluate(ConnectionRuleSets.Load("vde-ar-n-4105"), 20.0, Q()).Verdict);
    }
}
