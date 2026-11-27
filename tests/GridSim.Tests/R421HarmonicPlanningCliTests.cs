using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Presentation.Workbench;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC4 R4-21: the <c>harmonic-assess</c>, <c>flicker</c> and <c>filter-design</c> verbs run as the built CLI (strict studies, exit
/// codes, result files that are never overwritten, the report through R4-10's generator, a Stage 3 on a bound case).</summary>
public sealed class R421HarmonicPlanningCliTests
{
    private static string Dir()
    {
        string d = TestTemp.Under("gridsim-r421-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Write(string dir, string name, string text)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private const string Accepted = """
        { "schemaVersion": 1, "study": "harmonic-assess",
          "pcc": { "lineKv": 11, "sscMva": 60, "xOverR": 10 },
          "equipment": [ { "id": "drive", "technology": "active-front-end", "ratingKva": 600 } ] }
        """;

    [Fact]
    public void Harmonic_assess_accepts_at_stage_2A_exit_0_and_writes_a_new_file_only()
    {
        string d = Dir();
        string study = Write(d, "s.json", Accepted);
        string outFile = Path.Combine(d, "r.json");
        CliProcess.Result r = CliProcess.RunBuilt("harmonic-assess", study, "--out", outFile);
        Assert.True(r.ExitCode == 0, r.ToString());
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outFile));
        JsonElement root = doc.RootElement;
        Assert.Equal("gridsim-harmonic-assess/1", root.GetProperty("format").GetString());
        JsonElement res = root.GetProperty("result");
        Assert.Equal("ACCEPTED", res.GetProperty("verdict").GetString());
        Assert.Equal("2A-1", res.GetProperty("route").GetString());
        Assert.Equal("Table 3", res.GetProperty("planningTable").GetString());
        Assert.Equal(4.5, res.GetProperty("thdPlanningLevelPct").GetDouble());
        // Hand: Table 21 673 kVA at 60 MVA; 600 <= 673.
        JsonElement sub = res.GetProperty("substages")[0];
        Assert.Equal(673.0, sub.GetProperty("permitted").GetDouble(), 9);
        Assert.Equal(600.0, sub.GetProperty("required").GetDouble(), 9);
        Assert.Equal(64, root.GetProperty("provenance").GetProperty("studySha256").GetString()!.Length);
        Assert.Equal(1, CliProcess.RunBuilt("harmonic-assess", study, "--out", outFile).ExitCode);   // an existing result path is refused
    }

    [Fact]
    public void A_failing_assessment_prints_the_verdict_and_exits_3_and_the_run_is_repeatable()
    {
        string d = Dir();
        string study = Write(d, "s.json", """
            { "schemaVersion": 1, "study": "harmonic-assess",
              "pcc": { "lineKv": 11, "sscMva": 5, "xOverR": 10 },
              "equipment": [ { "id": "big", "technology": "six-pulse", "ratingKva": 2000 } ],
              "background": { "mode": "assumed-75" } }
            """);
        CliProcess.Result a = CliProcess.RunBuilt("harmonic-assess", study);
        CliProcess.Result b = CliProcess.RunBuilt("harmonic-assess", study);
        Assert.Equal(3, a.ExitCode);
        Assert.Contains("STAGE 3 REQUIRED", a.StdOut);
        Assert.Equal(a.StdOut, b.StdOut);
    }

    [Fact]
    public void Bad_studies_are_refused_with_exit_1_and_usage_with_exit_2()
    {
        string d = Dir();
        Assert.Equal(1, CliProcess.RunBuilt("harmonic-assess", Write(d, "a.json", Accepted.Replace("\"lineKv\": 11", "\"lineKv\": 11, \"surprise\": 1"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("harmonic-assess", Write(d, "b.json", Accepted.Replace("active-front-end", "magic"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("harmonic-assess", Write(d, "c.json", Accepted.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("harmonic-assess", Write(d, "e.json", Accepted.Replace("\"id\": \"drive\",", "\"id\": \"drive\", \"id\": \"again\","))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("harmonic-assess", Write(d, "f.json", Accepted.Replace("\"ratingKva\": 600", "\"ratingKva\": -5"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("harmonic-assess", Path.Combine(d, "missing.json")).ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("harmonic-assess").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("harmonic-assess", Path.Combine(d, "a.json"), "--bogus").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("flicker").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("filter-design").ExitCode);
    }

    [Fact]
    public void The_result_is_reported_through_the_R4_10_report_generator()
    {
        string d = Dir();
        string study = Write(d, "s.json", Accepted);
        string result = Path.Combine(d, "r.json");
        Assert.Equal(0, CliProcess.RunBuilt("harmonic-assess", study, "--out", result).ExitCode);
        string template = Path.Combine(d, "t.json");
        CliProcess.Result t = CliProcess.RunBuilt("harmonic-assess", "--report-template", "--out", template);
        Assert.True(t.ExitCode == 0, t.ToString());
        Assert.Equal(ConnectionAssessmentReport.TemplateJson(), File.ReadAllText(template).TrimEnd());

        string md = Path.Combine(d, "report.md");
        CliProcess.Result rep = CliProcess.RunBuilt("report", template, result, "--out", md);
        Assert.True(rep.ExitCode == 0, rep.ToString());
        string text = File.ReadAllText(md);
        Assert.Contains("ACCEPTED", text);
        Assert.Contains("2A-1", text);
        Assert.Contains("673", text);
        // The same template through the library (no process) binds without a gap in the verdict section.
        ReportDocument doc = ReportRenderer.Build(File.ReadAllBytes(template), File.ReadAllBytes(result));
        Assert.Equal("UK harmonic connection assessment (ENA EREC G5 Issue 5)", doc.Title);
    }

    // ------------------------------------------------------------------ Stage 3 on a bound case

    private const string CaseM = """
        {
          "schema": "gridsim-network/2",
          "buses": [
            { "id": 1, "name": "S", "baseKv": 33, "type": "slack", "vm": 1.0, "vaDeg": 0 },
            { "id": 2, "name": "B2", "baseKv": 33, "type": "pq" },
            { "id": 3, "name": "B3", "baseKv": 33, "type": "pq" }
          ],
          "generators": [ { "name": "G", "bus": 1, "type": "gas", "capacityMva": 500, "outputMw": 0, "voltageSetpoint": 1.0, "qMinMvar": -300, "qMaxMvar": 300, "role": "slack" } ],
          "demands": [ { "name": "D2", "bus": 2, "pMw": 5 }, { "name": "D3", "bus": 3, "pMw": 5 } ],
          "lines": [
            { "name": "L0", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.03, "ratingMva": 50, "inService": true },
            { "name": "L1", "from": 2, "to": 3, "rPu": 0.01, "xPu": 0.03, "ratingMva": 50, "inService": true }
          ]
        }
        """;

    private static string Stage3Study(string sha) => $$"""
        { "schemaVersion": 1, "study": "harmonic-assess",
          "pcc": { "lineKv": 33, "sscMva": 500, "xOverR": 10 },
          "equipment": [ { "id": "inverter", "technology": "active-front-end", "ratingKva": 2000 } ],
          "stage3": {
            "case": { "path": "case.json", "sha256": "{{sha}}" },
            "pccBus": 3,
            "sources": [ { "bus": 3, "type": "afe-g5", "currentA": 40 } ],
            "options": { "orders": [5, 7, 11, 13] },
            "pccBackgroundPct": { "5": 0.5, "7": 0.5, "11": 0.5, "13": 0.5 },
            "assumedRemoteBackgroundFraction": 0.5
          } }
        """;

    [Fact]
    public void Stage_3_binds_the_case_by_hash_and_prints_the_limit_table()
    {
        string d = Dir();
        Write(d, "nat.json", CaseM);
        Write(d, "case.json", "{ \"schema\": \"gridsim-case/2\", \"name\": \"r421\", \"system\": { \"baseMva\": 100, \"frequencyHz\": 50 }, \"network\": [\"nat.json\"] }");
        CliProcess.Result bad = CliProcess.RunBuilt("harmonic-assess", Write(d, "probe.json", Stage3Study("00")));
        Assert.Equal(1, bad.ExitCode);
        Match id = Regex.Match(bad.StdErr, "Resolved identity: ([0-9a-f]{64})");
        Assert.True(id.Success, bad.ToString());

        string study = Write(d, "s.json", Stage3Study(id.Groups[1].Value));
        CliProcess.Result r = CliProcess.RunBuilt("harmonic-assess", study);
        Assert.True(r.ExitCode is 0 or 3, r.ToString());
        using JsonDocument doc = JsonDocument.Parse(r.StdOut);
        JsonElement res = doc.RootElement.GetProperty("result");
        Assert.Equal("Stage 3 (clause 10)", res.GetProperty("route").GetString());
        JsonElement s3 = res.GetProperty("stage3");
        Assert.Equal(4, s3.GetProperty("rows").GetArrayLength());
        Assert.Equal(3.7, s3.GetProperty("thdPlanningLevelPct").GetDouble());            // Table 1: 25 kV < 33 kV <= 66 kV
        Assert.Equal(2.8, s3.GetProperty("rows")[0].GetProperty("planningLevelPct").GetDouble());   // Table 4, 5th harmonic
        Assert.Equal(0.5, s3.GetProperty("rows")[0].GetProperty("multiplier").GetDouble());
        Assert.Equal(res.GetProperty("verdict").GetString() == "ACCEPTED" ? 0 : 3, r.ExitCode);
        CliProcess.Result again = CliProcess.RunBuilt("harmonic-assess", study);
        Assert.Equal(r.StdOut, again.StdOut);                                           // bit-identical repeat
    }

    // ------------------------------------------------------------------ flicker

    [Fact]
    public void Flicker_sums_sources_with_exponent_3_checks_steps_and_says_the_meter_is_awaiting_the_standard()
    {
        string d = Dir();
        string study = Write(d, "f.json", """
            { "schemaVersion": 1, "study": "flicker", "nominalKv": 11,
              "sources": [ { "id": "A", "pst": 0.5, "plt": 0.4 }, { "id": "B", "pst": 0.5, "plt": 0.4 }, { "id": "C", "pst": 0.5, "plt": 0.4 }, { "id": "D", "pst": 0.5, "plt": 0.4 } ],
              "steps": [ { "id": "motor", "deltaSMva": 3, "sscMva": 100, "intervalSeconds": 600 } ],
              "inrush": [ { "id": "tx", "m": 8, "k": 0.5, "ratedMva": 1, "sscMva": 100 } ],
              "rvc": [ { "id": "start", "category": 2, "samples": [ { "timeS": 0.05, "deviationPct": -9 }, { "timeS": 0.5, "deviationPct": -5 }, { "timeS": 3, "deviationPct": -2 } ], "steadyStateChangePct": -2 } ],
              "flickermeter": { } }
            """);
        string outFile = Path.Combine(d, "r.json");
        CliProcess.Result r = CliProcess.RunBuilt("flicker", study, "--out", outFile);
        Assert.True(r.ExitCode == 0, r.ToString());
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outFile));
        JsonElement res = doc.RootElement.GetProperty("result");
        Assert.Equal("ACCEPTED", res.GetProperty("verdict").GetString());
        Assert.Equal(0.9, res.GetProperty("planningLevels").GetProperty("pst").GetDouble());
        Assert.Equal(Math.Pow(0.5, 1.0 / 3.0), res.GetProperty("planning").GetProperty("pst").GetProperty("value").GetDouble(), 12);   // (4 x 0.125)^(1/3)
        Assert.Equal(3.0, res.GetProperty("steps")[0].GetProperty("stepPct").GetDouble(), 12);                                       // 3 / 100 x 100
        Assert.Equal("PASS", res.GetProperty("steps")[0].GetProperty("verdict").GetProperty("outcome").GetString());                 // 3 % every 600 s >= 475 s
        Assert.Equal(4.0, res.GetProperty("inrush")[0].GetProperty("voltageChangePct").GetDouble(), 12);
        Assert.Equal("PASS", res.GetProperty("rvc")[0].GetProperty("verdict").GetProperty("outcome").GetString());
        Assert.Contains("AWAITING STANDARD TEXT", res.GetProperty("flickermeter").GetProperty("status").GetString());
        Assert.Equal(1, CliProcess.RunBuilt("flicker", study, "--out", outFile).ExitCode);
    }

    [Fact]
    public void Flicker_fails_a_planning_level_exit_3_and_refuses_an_unlisted_transfer_pair()
    {
        string d = Dir();
        string eight = string.Join(",", Enumerable.Range(0, 8).Select(i => $"{{ \"id\": \"s{i}\", \"pst\": 0.5 }}"));
        CliProcess.Result r = CliProcess.RunBuilt("flicker", Write(d, "a.json", $"{{ \"schemaVersion\": 1, \"study\": \"flicker\", \"nominalKv\": 11, \"sources\": [ {eight} ] }}"));
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("NOT ACCEPTED", r.StdOut);
        CliProcess.Result t = CliProcess.RunBuilt("flicker", Write(d, "b.json", "{ \"schemaVersion\": 1, \"study\": \"flicker\", \"nominalKv\": 0.4, \"sources\": [ { \"id\": \"a\", \"pst\": 0.1 } ], \"transfer\": { \"fromKv\": 33 } }"));
        Assert.Equal(1, t.ExitCode);
        Assert.Contains("Table 3", t.StdErr);
        // Table 3 pair 11 kV -> LV is 1.0.
        Assert.Equal(0, CliProcess.RunBuilt("flicker", Write(d, "c.json", "{ \"schemaVersion\": 1, \"study\": \"flicker\", \"nominalKv\": 0.4, \"sources\": [ { \"id\": \"a\", \"pst\": 0.1 } ], \"transfer\": { \"fromKv\": 11 } }")).ExitCode);
    }

    [Fact]
    public void Flicker_runs_the_meter_structure_only_with_supplied_parameters_and_labels_it_awaiting()
    {
        string d = Dir();
        const string p = """
            "parameters": { "sampleRateHz": 4000, "nominalRmsVolts": 230, "highPassCornerHz": 0.05, "lowPassOrder": 6, "lowPassCornerHz": 35,
              "k": 1.5, "lambda": 25.13, "omega1": 56.55, "omega2": 15.7, "omega3": 7.54, "omega4": 131.9, "slidingMeanTauSeconds": 0.3,
              "p1sPercentiles": [0.7, 1.0, 1.5], "p3sPercentiles": [2.2, 3.0, 4.0], "p10sPercentiles": [6, 8, 10], "p50sPercentiles": [30, 50, 80] },
            "signal": { "durationSeconds": 20, "carrierHz": 50, "modulation": "sine", "modulationHz": 8.8, "depthPct": 0.2, "settleSeconds": 5 }
            """;
        CliProcess.Result r = CliProcess.RunBuilt("flicker", Write(d, "m.json", "{ \"schemaVersion\": 1, \"study\": \"flicker\", \"nominalKv\": 11, \"flickermeter\": { " + p + " } }"));
        Assert.True(r.ExitCode == 0, r.ToString());
        using JsonDocument doc = JsonDocument.Parse(r.StdOut);
        JsonElement m = doc.RootElement.GetProperty("result").GetProperty("flickermeter");
        Assert.True(m.GetProperty("pst").GetDouble() > 0.0);
        Assert.Contains("AWAITING STANDARD TEXT", m.GetProperty("verificationStatus").GetString());
    }

    // ------------------------------------------------------------------ filter-design

    [Fact]
    public void Filter_design_prints_the_closed_form_components_and_the_network_resonances()
    {
        string d = Dir();
        string study = Write(d, "f.json", """
            { "schemaVersion": 1, "study": "filter-design",
              "filters": [
                { "id": "F5", "kind": "single-tuned", "voltageKv": 11, "fundamentalMvar": 5, "tunedOrder": 4.9 },
                { "id": "HP", "kind": "high-pass", "voltageKv": 11, "fundamentalMvar": 5, "tunedOrder": 12, "qualityFactor": 2 },
                { "id": "CT", "kind": "c-type", "voltageKv": 11, "fundamentalMvar": 5, "tunedOrder": 3 } ],
              "network": { "source": { "sscMva": 250, "xOverR": 1000 } } }
            """);
        CliProcess.Result r = CliProcess.RunBuilt("filter-design", study);
        Assert.True(r.ExitCode == 0, r.ToString());
        using JsonDocument doc = JsonDocument.Parse(r.StdOut);
        JsonElement res = doc.RootElement.GetProperty("result");
        JsonElement f5 = res.GetProperty("filters")[0];
        double xc = 121.0 * 4.9 * 4.9 / (5.0 * (4.9 * 4.9 - 1.0));
        Assert.Equal(xc, f5.GetProperty("capacitorOhm").GetDouble(), 9);
        Assert.Equal(xc / (4.9 * 4.9), f5.GetProperty("reactorOhm").GetDouble(), 9);
        Assert.True(f5.GetProperty("impedanceAtTunedOrderOhm").GetProperty("abs").GetDouble() < 1e-9);
        // A source of Ssc 250 MVA and a very large X/R is a pure reactance Xs = 121/250 ohm: parallel resonance at sqrt(Xc / (XL + Xs)) = 4.0548 for the single-tuned filter.
        double hp = Math.Sqrt(xc / (xc / (4.9 * 4.9) + 121.0 / 250.0));
        JsonElement rows = res.GetProperty("network").GetProperty("rows")[0];
        double found = rows.GetProperty("parallelResonanceOrders").EnumerateArray().Select(e => e.GetDouble()).OrderBy(x => Math.Abs(x - hp)).First();
        Assert.True(Math.Abs(found - hp) < 0.01, $"parallel resonance {found}, closed form {hp}");
        Assert.True(Math.Abs(rows.GetProperty("seriesResonanceOrders").EnumerateArray().Select(e => e.GetDouble()).OrderBy(x => Math.Abs(x - 4.9)).First() - 4.9) < 0.01);
    }

    [Fact]
    public void Filter_design_refuses_bad_input()
    {
        string d = Dir();
        Assert.Equal(1, CliProcess.RunBuilt("filter-design", Write(d, "a.json", """{ "schemaVersion": 1, "study": "filter-design", "filters": [ { "id": "x", "kind": "notch", "voltageKv": 11, "fundamentalMvar": 5, "tunedOrder": 5 } ] }""")).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("filter-design", Write(d, "b.json", """{ "schemaVersion": 1, "study": "filter-design", "filters": [ { "id": "x", "kind": "single-tuned", "voltageKv": 11, "fundamentalMvar": 5, "tunedOrder": 0.5 } ] }""")).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("filter-design", Write(d, "c.json", """{ "schemaVersion": 1, "study": "filter-design", "filters": [] }""")).ExitCode);
    }
}
