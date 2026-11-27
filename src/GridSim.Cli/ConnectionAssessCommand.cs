using System.Text;
using System.Text.Json;
using GridSim.Core.Harmonics.Planning;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-20e: <c>gridsim connection-assess &lt;study.json&gt; --rules &lt;id|rules.json&gt; [--out &lt;result.json&gt;]</c>, a rule-set driven connection
/// assessment (German connection rules VDE-AR-N 4105, 4110, 4120, 4130 and the BDEW guideline, or any rule file the owner supplies) on the R4-21
/// framework. The built-in rule sets ship with every limit empty: their rules are listed and reported OUT OF SCOPE (AWAITING STANDARD TEXT).
/// The study file is strict JSON: <c>schemaVersion</c> 1, <c>label</c>, <c>pccVoltageKv</c>, <c>quantities</c> (name to number), and optionally
/// <c>harmonicAssess</c>, the path of a <c>harmonic-assess</c> result file whose THD figures are added as quantities. <c>--list-rules</c> prints the
/// built-in rule set ids. Exit 0 COMPLIANT, 3 NON-COMPLIANT, INCOMPLETE or out of scope, 1 input it cannot accept, 2 usage.
/// </summary>
internal static class ConnectionAssessCommand
{
    private const string Verb = "connection-assess";
    private const string Usage = "usage: gridsim connection-assess <study.json> --rules <id|rules.json> [--out <result.json>]   |   gridsim connection-assess --list-rules";

    private sealed record Study
    {
        public required int SchemaVersion { get; init; }
        public required string Label { get; init; }
        public required double PccVoltageKv { get; init; }
        public required Dictionary<string, double> Quantities { get; init; }
        public string? HarmonicAssess { get; init; }
    }

    public static int Run(List<string> a)
    {
        if (a.Count == 2 && a[1] == "--list-rules")
        {
            foreach (string id in ConnectionRuleSets.BuiltInIds) Console.WriteLine(id);
            return CliExit.Ok;
        }
        string? input = null, rules = null, output = null;
        for (int i = 1; i < a.Count; i++)
        {
            if (a[i] == "--rules" && i + 1 < a.Count && rules is null) rules = a[++i];
            else if (a[i] == "--out" && i + 1 < a.Count && output is null) output = a[++i];
            else if (!a[i].StartsWith('-') && input is null) input = a[i];
            else { Console.Error.WriteLine(Usage); return CliExit.Usage; }
        }
        if (input is null || rules is null || input.StartsWith('-') || rules.StartsWith('-') || (output is not null && output.StartsWith('-')))
        { Console.Error.WriteLine(Usage); return CliExit.Usage; }
        string? temporary = null;
        try
        {
            string? outFull = output is null ? null : Path.GetFullPath(output);
            if (outFull is not null && (File.Exists(outFull) || Directory.Exists(outFull)))
                throw new IOException("Result path already exists; use a new path for this invocation.");
            string studyPath = Path.GetFullPath(input);
            byte[] bytes = File.ReadAllBytes(studyPath);
            using (JsonDocument d = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes))) TransmissionSecurityCommand.CheckDuplicates(d.RootElement);
            Study s = JsonSerializer.Deserialize<Study>(GridSim.Core.IO.Utf8Json.WithoutBom(bytes).Span, TransmissionSecurityCommand.Json) ?? throw new InvalidDataException("Study is null.");
            if (s.SchemaVersion != 1) throw new InvalidDataException("schemaVersion must be 1.");
            if (string.IsNullOrWhiteSpace(s.Label)) throw new InvalidDataException("label must not be empty.");
            if (!(s.PccVoltageKv > 0.0) || !double.IsFinite(s.PccVoltageKv)) throw new InvalidDataException("pccVoltageKv must be finite and above 0.");
            var q = new SortedDictionary<string, double>(StringComparer.Ordinal);
            foreach ((string k, double v) in s.Quantities)
            {
                if (string.IsNullOrWhiteSpace(k) || !double.IsFinite(v)) throw new InvalidDataException("every quantity needs a name and a finite value.");
                q[k] = v;
            }
            if (s.HarmonicAssess is string ha)
                foreach ((string k, double v) in ImportHarmonicAssess(Path.GetFullPath(ha, Path.GetDirectoryName(studyPath)!)))
                    if (!q.TryAdd(k, v)) throw new InvalidDataException($"quantity '{k}' is given both in the study and by the harmonic-assess result.");

            ConnectionRuleSet set;
            string rulesSource;
            if (rules.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || rules.Contains('/') || rules.Contains('\\'))
            {
                byte[] rb = File.ReadAllBytes(Path.GetFullPath(rules));
                try { set = ConnectionRuleSet.Parse(Encoding.UTF8.GetString(rb).TrimStart('\uFEFF')); } catch (FormatException ex) { throw new InvalidDataException(ex.Message); }
                rulesSource = "file sha256 " + SpecialistStudyCli.Hash(rb);
            }
            else { set = ConnectionRuleSets.Load(rules); rulesSource = "built in"; }

            RuleSetResult result = RuleSetEngine.Evaluate(set, s.PccVoltageKv, q);
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                study = Verb,
                label = s.Label,
                provenance = new
                {
                    studySha256 = SpecialistStudyCli.Hash(bytes), engine = GridSim.Core.BuildInfo.InformationalVersion, rules = rulesSource,
                    ruleProvenance = set.Provenance, scope = set.Scope,
                    note = "rule structure from public summaries; limit values AWAITING STANDARD TEXT unless the owner filled them; not verified against the standards",
                },
                result,
            }, TransmissionSecurityCommand.Json);
            if (outFull is not null)
            {
                temporary = outFull + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
                File.Move(temporary, outFull, overwrite: false);
                temporary = null;
            }
            CliResultFromJson.Fill(json);
            Console.WriteLine(json);
            return result.Verdict == RuleSetEngine.Compliant ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine(Verb + ": " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The THD figures of a <c>harmonic-assess</c> result file (<c>result.thdPlanningLevelPct</c>, <c>result.stage2C.thdPredictedPct</c> and
    /// <c>thdIncrementalPct</c>), under the quantity names of <see cref="Quantities.FromAssessment"/>.</summary>
    private static IEnumerable<KeyValuePair<string, double>> ImportHarmonicAssess(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(path)));
        JsonElement result = Child(doc.RootElement, "result") ?? throw new InvalidDataException($"{path}: no 'result' object (is it a harmonic-assess result file?)");
        var d = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (Child(result, "thdPlanningLevelPct") is JsonElement p && p.ValueKind == JsonValueKind.Number) d["harmonic.thd_planning_level_pct"] = p.GetDouble();
        else throw new InvalidDataException($"{path}: result.thdPlanningLevelPct is missing");
        if (Child(result, "stage2C") is JsonElement s && s.ValueKind == JsonValueKind.Object)
        {
            if (Child(s, "thdPredictedPct") is JsonElement a && a.ValueKind == JsonValueKind.Number) d["harmonic.thd_predicted_pct"] = a.GetDouble();
            if (Child(s, "thdIncrementalPct") is JsonElement b && b.ValueKind == JsonValueKind.Number) d["harmonic.thd_incremental_pct"] = b.GetDouble();
        }
        return d;
    }

    private static JsonElement? Child(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (JsonProperty p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
}
