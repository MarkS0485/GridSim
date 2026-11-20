using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace GridSim.Core.Harmonics.Planning;

/// <summary>Where a rule file's values come from. A rule with a limit must sit in a file whose provenance is complete.</summary>
public sealed record RuleProvenance(string Source, string Edition, string FilledBy, string Date);

/// <summary>A condition that decides whether a rule applies: a supplied quantity compared with a value (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>==</c>).</summary>
public sealed record RuleCondition(string Quantity, string Op, double Value);

/// <summary>
/// One requirement. <see cref="Limit"/> null means the value is AWAITING STANDARD TEXT (the rule is listed, never evaluated, never defaulted).
/// <see cref="Comparison"/> is <c>&lt;=</c> (the quantity must not exceed the limit), <c>&gt;=</c>, or <c>range</c> (between <see cref="Limit"/> and
/// <see cref="LimitHigh"/>). <see cref="Clause"/> is the clause the limit was read from (empty while awaiting).
/// </summary>
public sealed record ConnectionRule(
    string Id, string Topic, string Clause, string Quantity, string Comparison, double? Limit, double? LimitHigh, string Unit,
    IReadOnlyList<RuleCondition> When, string Note);

/// <summary>The voltage range a rule set covers, with where that range was read (null bounds = not known).</summary>
public sealed record RuleScope(double? VoltageKvMin, double? VoltageKvMax, string Source);

/// <summary>A connection-rule set file (<c>gridsim-connection-rules/1</c>): structure plus, where the owner fills them, limit values.</summary>
public sealed record ConnectionRuleSet(string Id, string Title, RuleProvenance Provenance, RuleScope Scope, IReadOnlyList<ConnectionRule> Rules)
{
    public const string Schema = "gridsim-connection-rules/1";
    public int FilledLimits => Rules.Count(r => r.Limit.HasValue);
    public string Status => FilledLimits == 0 ? "EMPTY (every limit AWAITING STANDARD TEXT)" : FilledLimits == Rules.Count ? "FILLED" : "PARTLY FILLED";

    private static readonly HashSet<string> Ops = new(StringComparer.Ordinal) { "<", "<=", ">", ">=", "==" };

    public static ConnectionRuleSet Parse(string json)
    {
        var problems = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("rule file is not valid JSON: " + ex.Message); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("rule file must be a JSON object");
            foreach (JsonProperty p in root.EnumerateObject())
                if (p.Name is not ("schema" or "id" or "title" or "provenance" or "scope" or "rules")) problems.Add($"unknown key '{p.Name}'");
            if (!root.TryGetProperty("schema", out JsonElement sch) || sch.ValueKind != JsonValueKind.String || sch.GetString() != Schema)
                problems.Add($"schema must be \"{Schema}\"");
            string id = Str(root, "id"), title = Str(root, "title");
            if (id.Length == 0) problems.Add("id is required");
            RuleProvenance prov = new("", "", "", "");
            if (root.TryGetProperty("provenance", out JsonElement pe) && pe.ValueKind == JsonValueKind.Object)
                prov = new RuleProvenance(Str(pe, "source"), Str(pe, "edition"), Str(pe, "filledBy"), Str(pe, "date"));
            RuleScope scope = new(null, null, "");
            if (root.TryGetProperty("scope", out JsonElement se) && se.ValueKind == JsonValueKind.Object)
            {
                double? lo = OptNum(se, "voltageKvMin", "scope", problems), hi = OptNum(se, "voltageKvMax", "scope", problems);
                if (lo is double a && hi is double b && !(a < b)) problems.Add("scope: voltageKvMin must be below voltageKvMax");
                scope = new RuleScope(lo, hi, Str(se, "source"));
                if ((lo.HasValue || hi.HasValue) && scope.Source.Length == 0) problems.Add("scope: a voltage range needs its source");
            }
            var rules = new List<ConnectionRule>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("rules", out JsonElement re) && re.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement e in re.EnumerateArray())
                {
                    string w = $"rules[{i++}]";
                    string rid = Str(e, "id");
                    if (rid.Length == 0 || !ids.Add(rid)) problems.Add($"{w}: id is required and must be unique");
                    string cmp = Str(e, "comparison");
                    if (cmp is not ("<=" or ">=" or "range")) problems.Add($"{w}: comparison must be <=, >= or range");
                    double? lim = OptNum(e, "limit", w, problems), high = OptNum(e, "limitHigh", w, problems);
                    if (cmp == "range" && lim.HasValue != high.HasValue) problems.Add($"{w}: a range needs both limit and limitHigh, or neither");
                    if (cmp == "range" && lim is double l1 && high is double h1 && !(l1 < h1)) problems.Add($"{w}: limit must be below limitHigh");
                    if (cmp != "range" && high.HasValue) problems.Add($"{w}: limitHigh is only for range");
                    string clause = Str(e, "clause");
                    if (lim.HasValue && clause.Length == 0) problems.Add($"{w}: a limit needs the clause it was read from");
                    if (Str(e, "quantity").Length == 0) problems.Add($"{w}: quantity is required");
                    var when = new List<RuleCondition>();
                    if (e.TryGetProperty("when", out JsonElement we) && we.ValueKind == JsonValueKind.Array)
                    {
                        int j = 0;
                        foreach (JsonElement c in we.EnumerateArray())
                        {
                            string cw = $"{w}.when[{j++}]";
                            string op = Str(c, "op");
                            if (!Ops.Contains(op)) problems.Add($"{cw}: op must be <, <=, >, >= or ==");
                            double? v = OptNum(c, "value", cw, problems);
                            if (!v.HasValue) problems.Add($"{cw}: value is required");
                            if (Str(c, "quantity").Length == 0) problems.Add($"{cw}: quantity is required");
                            when.Add(new RuleCondition(Str(c, "quantity"), op, v ?? double.NaN));
                        }
                    }
                    foreach (JsonProperty p in e.EnumerateObject())
                        if (p.Name is not ("id" or "topic" or "clause" or "quantity" or "comparison" or "limit" or "limitHigh" or "unit" or "when" or "note")) problems.Add($"{w}: unknown key '{p.Name}'");
                    rules.Add(new ConnectionRule(rid, Str(e, "topic"), clause, Str(e, "quantity"), cmp, lim, high, Str(e, "unit"), when, Str(e, "note")));
                }
            }
            var set = new ConnectionRuleSet(id, title, prov, scope, rules);
            if (set.FilledLimits > 0 && (prov.Source.Length == 0 || prov.Edition.Length == 0 || prov.FilledBy.Length == 0 || prov.Date.Length == 0))
                problems.Add("a rule file with limit values must carry provenance: source, edition, filledBy and date (these standards are paid text)");
            if (problems.Count > 0) throw new FormatException("rule file: " + string.Join("; ", problems));
            return set;
        }
    }

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static double? OptNum(JsonElement e, string key, string w, List<string> problems)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number || !double.IsFinite(v.GetDouble())) { problems.Add($"{w}: '{key}' must be a finite number or null"); return null; }
        return v.GetDouble();
    }
}

/// <summary>The built-in rule sets, embedded from <c>Harmonics/Planning/Rules/&lt;id&gt;.json</c>.</summary>
public static class ConnectionRuleSets
{
    private const string Prefix = "GridSim.Core.connection-rules.";

    public static IReadOnlyList<string> BuiltInIds { get; } =
        typeof(ConnectionRuleSets).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..^5]).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    public static ConnectionRuleSet Load(string id)
    {
        Assembly asm = typeof(ConnectionRuleSets).Assembly;
        using Stream? s = asm.GetManifestResourceStream(Prefix + id + ".json");
        if (s is null) throw new ArgumentException($"no built-in rule set '{id}' (built in: {string.Join(", ", BuiltInIds)})");
        using var r = new StreamReader(s);
        return ConnectionRuleSet.Parse(r.ReadToEnd());
    }
}

public enum RuleOutcome { Pass, Fail, NotApplicable, OutOfScope, NotEvaluated }

/// <summary>One rule's outcome: the supplied value, the limit(s), the signed margin (positive = inside the limit) and the reason when not evaluated.</summary>
public sealed record RuleResult(
    string RuleId, string Topic, string Clause, RuleOutcome Outcome, string Quantity, double? Value, double? Limit, double? LimitHigh, string Unit,
    double? Margin, string Reason);

public sealed record RuleSetResult(
    string Format, string RuleSetId, string RuleSetStatus, string Verdict, double PccVoltageKv, string ScopeStatus, IReadOnlyList<RuleResult> Rules,
    int Passed, int Failed, int NotApplicable, int OutOfScope, int NotEvaluated, IReadOnlyList<string> Notes);

/// <summary>
/// Evaluates a connection study (a PCC voltage and named quantities) against a rule set file (RC4 R4-20e), on the R4-21 assessment framework
/// (the G5/5 staged assessment supplies quantities through <see cref="Quantities.FromAssessment"/>). The engine is general; the German rule files
/// (VDE-AR-N 4105, 4110, 4120, 4130 and the BDEW guideline) ship EMPTY because their limits are in paid texts: every rule is listed with its topic and
/// is reported OUT OF SCOPE (AWAITING STANDARD TEXT) until the owner fills a limit with its clause and the file's provenance. Order: scope (voltage range),
/// then each rule in file order: conditions, limit present, quantity supplied, comparison. A rule that cannot be evaluated is never a pass:
/// the verdict is NON-COMPLIANT on any failure, else INCOMPLETE while anything applicable is unevaluated, else COMPLIANT.
/// </summary>
public static class RuleSetEngine
{
    public const string Format = "gridsim-connection-assess/1";
    public const string Compliant = "COMPLIANT", NonCompliant = "NON-COMPLIANT", Incomplete = "INCOMPLETE", WrongScope = "OUT OF SCOPE (rule set does not cover this voltage)";

    private static bool Holds(RuleCondition c, double v) => c.Op switch
    {
        "<" => v < c.Value, "<=" => v <= c.Value, ">" => v > c.Value, ">=" => v >= c.Value, _ => v == c.Value,
    };

    public static RuleSetResult Evaluate(ConnectionRuleSet set, double pccVoltageKv, IReadOnlyDictionary<string, double> quantities)
    {
        if (!(pccVoltageKv > 0.0) || !double.IsFinite(pccVoltageKv)) throw new ArgumentOutOfRangeException(nameof(pccVoltageKv), "PCC voltage must be above 0 kV");
        var notes = new List<string>();
        string scopeStatus;
        if (set.Scope.VoltageKvMin is null && set.Scope.VoltageKvMax is null)
        { scopeStatus = "NOT CHECKED (the rule file gives no voltage range)"; notes.Add("the voltage range of this rule set is not given: AWAITING"); }
        else if ((set.Scope.VoltageKvMin is double lo && pccVoltageKv < lo) || (set.Scope.VoltageKvMax is double hi && pccVoltageKv > hi))
            scopeStatus = "OUTSIDE";
        else scopeStatus = "INSIDE";
        if (set.FilledLimits == 0) notes.Add($"rule set '{set.Id}' is shipped empty: every limit is AWAITING STANDARD TEXT, so no rule is evaluated");

        var results = new List<RuleResult>();
        foreach (ConnectionRule r in set.Rules)
        {
            RuleResult Make(RuleOutcome o, double? v, double? margin, string reason) =>
                new(r.Id, r.Topic, r.Clause.Length == 0 ? "AWAITING STANDARD TEXT" : r.Clause, o, r.Quantity, v, r.Limit, r.LimitHigh, r.Unit, margin, reason);
            if (scopeStatus == "OUTSIDE") { results.Add(Make(RuleOutcome.OutOfScope, null, null, $"PCC {pccVoltageKv:R} kV is outside the voltage range of rule set '{set.Id}'")); continue; }
            string? skipped = null; bool unmet = false;
            foreach (RuleCondition c in r.When)
            {
                if (!quantities.TryGetValue(c.Quantity, out double cv)) { skipped = $"condition quantity '{c.Quantity}' not supplied"; break; }
                if (!Holds(c, cv)) { unmet = true; break; }
            }
            if (skipped is not null) { results.Add(Make(RuleOutcome.NotEvaluated, null, null, skipped)); continue; }
            if (unmet) { results.Add(Make(RuleOutcome.NotApplicable, null, null, "a condition of the rule is not met")); continue; }
            if (r.Limit is null) { results.Add(Make(RuleOutcome.OutOfScope, null, null, "limit value AWAITING STANDARD TEXT")); continue; }
            if (!quantities.TryGetValue(r.Quantity, out double v)) { results.Add(Make(RuleOutcome.NotEvaluated, null, null, $"quantity '{r.Quantity}' not supplied")); continue; }
            (bool ok, double margin) = r.Comparison switch
            {
                "<=" => (v <= r.Limit.Value, r.Limit.Value - v),
                ">=" => (v >= r.Limit.Value, v - r.Limit.Value),
                _ => (v >= r.Limit.Value && v <= r.LimitHigh!.Value, Math.Min(v - r.Limit.Value, r.LimitHigh!.Value - v)),
            };
            results.Add(Make(ok ? RuleOutcome.Pass : RuleOutcome.Fail, v, margin, ""));
        }
        int pass = results.Count(x => x.Outcome == RuleOutcome.Pass), fail = results.Count(x => x.Outcome == RuleOutcome.Fail),
            na = results.Count(x => x.Outcome == RuleOutcome.NotApplicable), oos = results.Count(x => x.Outcome == RuleOutcome.OutOfScope),
            ne = results.Count(x => x.Outcome == RuleOutcome.NotEvaluated);
        string verdict = scopeStatus == "OUTSIDE" ? WrongScope : fail > 0 ? NonCompliant : (oos + ne > 0 || results.Count == 0) ? Incomplete : Compliant;
        return new RuleSetResult(Format, set.Id, set.Status, verdict, pccVoltageKv, scopeStatus, results, pass, fail, na, oos, ne, notes);
    }
}

/// <summary>Quantities handed over from the R4-21 assessment framework to the rule engine.</summary>
public static class Quantities
{
    /// <summary>The numbers a G5/5 staged assessment result offers: the THD planning level and, when Stage 2C ran, the predicted and incremental THD (percent).</summary>
    public static IReadOnlyDictionary<string, double> FromAssessment(ConnectionAssessmentResult r)
    {
        var d = new SortedDictionary<string, double>(StringComparer.Ordinal) { ["harmonic.thd_planning_level_pct"] = r.ThdPlanningLevelPct };
        if (r.Stage2C is G55Stage2CResult s)
        {
            d["harmonic.thd_predicted_pct"] = s.ThdPredictedPct;
            d["harmonic.thd_incremental_pct"] = s.ThdIncrementalPct;
        }
        return d;
    }
}
