using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;
using Xunit;
using static GridSim.Tests.Editor.CaseSessionTests;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// N101 (October 2026): one rule for unlimited generator limits across the loader, the v1 schemas and the v2 Phase 1
/// schema. MATPOWER allows an unlimited reactive or real limit (Inf / -Inf); since F24 the convert verb writes it as the
/// JSON string "Infinity" / "-Infinity" and the loader reads it. The schemas used to admit numbers only, so the
/// SchemaEditor save gate refused a converted case the loader accepts. The rule (approved): the loader keeps the two
/// strings; both schema sets accept a number or exactly the string of the unlimited direction ("-Infinity" for
/// qMinMvar, "Infinity" for qMaxMvar and pMaxMw); every other string is still refused.
/// </summary>
public class N101UnlimitedLimitsTests
{
    /// <summary>
    /// The case the finding is about: a real MATPOWER case with Inf limits (case1354pegase carries them) converted by
    /// the JSON case writer, opened in the case editor and run through the save gate in check mode: it passes.
    /// </summary>
    [Fact]
    public void A_converted_matpower_case_with_unlimited_limits_passes_the_save_gate()
    {
        GridModel m = S10TestSupport.LoadMatpower("pegase/case1354pegase.m").Model;
        Assert.Contains(m.AllGenerators, g => double.IsInfinity(g.Qmax) || double.IsInfinity(g.Qmin) || double.IsInfinity(g.Pmax));
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n101-" + Guid.NewGuid().ToString("N"));
        try
        {
            JsonCaseWriter.WriteDirectory(m, dir);
            string gen = File.ReadAllText(Path.Combine(dir, "generation.json"));
            Assert.Contains("\"Infinity\"", gen, StringComparison.Ordinal);   // the converted file really carries the string

            // The v1 schema accepts the written file.
            IReadOnlyList<SchemaError> errors = BundledSchemas.V1Validate.Validate(
                JsonSourceText.Load(Path.Combine(dir, "generation.json"), StringComparer.OrdinalIgnoreCase).ToElement(), "generation.schema.json");
            Assert.True(errors.Count == 0, V2.V2TestSupport.Dump(errors.Take(20)));

            // The save gate (schema stage, then the real loader) passes it.
            CaseSession s = CaseSession.OpenV1(dir, Unlimited());
            SaveReport r = SaveGate.Run(s, new SaveOptions(CheckOnly: true));
            Assert.True(r.Outcome == SaveOutcome.CheckPassed, r.Headline + Environment.NewLine + string.Join(Environment.NewLine, r.Errors.Select(p => p.ToString())));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Per field: the unlimited direction's string and any number pass; the other direction, other strings and NaN fail.</summary>
    [Theory]
    [InlineData("qMinMvar", "\"-Infinity\"", true)]
    [InlineData("qMaxMvar", "\"Infinity\"", true)]
    [InlineData("pMaxMw", "\"Infinity\"", true)]
    [InlineData("qMinMvar", "-12.5", true)]
    [InlineData("pMaxMw", "300", true)]
    [InlineData("qMinMvar", "\"Infinity\"", false)]    // the loader refuses +Infinity as a lower limit too
    [InlineData("qMaxMvar", "\"-Infinity\"", false)]
    [InlineData("pMaxMw", "\"-Infinity\"", false)]
    [InlineData("qMaxMvar", "\"NaN\"", false)]
    [InlineData("qMaxMvar", "\"inf\"", false)]
    [InlineData("pMinMw", "\"-Infinity\"", false)]       // pMinMw has no unlimited form (the loader refuses a non-finite one)
    // The same save-gate case found one more mismatch: convert writes a MATPOWER unit's (absent) technology as "type": ""
    // and the loader reads it, but the schema pattern refused the empty string. It now admits it (and nothing else new).
    [InlineData("type", "\"\"", true)]
    [InlineData("type", "\"wind-onshore\"", true)]
    [InlineData("type", "\"Wind\"", false)]
    public void The_v1_generator_schema_admits_exactly_the_unlimited_strings(string field, string value, bool ok)
    {
        IReadOnlyList<SchemaError> errors = BundledSchemas.V1Validate.Validate(
            Element($"{{ \"bus\": 1, \"{field}\": {value} }}"), "generation.schema.json", "#/$defs/generator");
        Assert.True(ok == (errors.Count == 0), $"{field} = {value}: {V2.V2TestSupport.Dump(errors)}");
    }

    /// <summary>The v2 Phase 1 network schema carries the same rule (read as text: the three properties are anyOf with the one string).</summary>
    [Fact]
    public void The_v2_network_schema_carries_the_same_rule()
    {
        string text = File.ReadAllText(Path.Combine(SchemasDir, "v2", "network.schema.json"));
        Assert.Contains("\"qMinMvar\": { \"anyOf\": [ { \"type\": \"number\" }, { \"enum\": [\"-Infinity\"] } ]", text, StringComparison.Ordinal);
        Assert.Contains("\"qMaxMvar\": { \"anyOf\": [ { \"type\": \"number\" }, { \"enum\": [\"Infinity\"] } ]", text, StringComparison.Ordinal);
        Assert.Contains("\"pMaxMw\": { \"anyOf\": [ { \"type\": \"number\" }, { \"enum\": [\"Infinity\"] } ]", text, StringComparison.Ordinal);
    }
}
