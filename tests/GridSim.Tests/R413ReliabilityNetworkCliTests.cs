using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC4 R4-13: the <c>reliability-network</c> verb run as the built CLI on a MATPOWER case with a normally-open tie
/// (strict study, case identity, groups and devices sidecars, exit codes, result file, thread invariance).</summary>
public sealed class R413ReliabilityNetworkCliTests
{
    private static string Dir()
    {
        string d = TestTemp.Under("gridsim-r413-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Write(string dir, string name, string text)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    // Buses 1 (slack), 2, 3, 4 with 1 MW at each load bus; lines b0 1-2, b1 2-3, b2 1-4 and the open tie b3 3-4
    // (no loader carries an open branch: the study declares the tie and opens it).
    private const string CaseM = """
        {
          "schema": "gridsim-network/2",
          "buses": [
            { "id": 1, "name": "S", "baseKv": 11, "type": "slack", "vm": 1.0, "vaDeg": 0 },
            { "id": 2, "name": "B2", "baseKv": 11, "type": "pq" },
            { "id": 3, "name": "B3", "baseKv": 11, "type": "pq" },
            { "id": 4, "name": "B4", "baseKv": 11, "type": "pq" }
          ],
          "generators": [ { "name": "G", "bus": 1, "type": "gas", "capacityMva": 500, "outputMw": 0, "voltageSetpoint": 1.0, "qMinMvar": -300, "qMaxMvar": 300, "role": "slack" } ],
          "demands": [ { "name": "D2", "bus": 2, "pMw": 1 }, { "name": "D3", "bus": 3, "pMw": 1 }, { "name": "D4", "bus": 4, "pMw": 1 } ],
          "lines": [
            { "name": "L0", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.03, "ratingMva": 50, "inService": true },
            { "name": "L1", "from": 2, "to": 3, "rPu": 0.01, "xPu": 0.03, "ratingMva": 50, "inService": true },
            { "name": "L2", "from": 1, "to": 4, "rPu": 0.01, "xPu": 0.03, "ratingMva": 50, "inService": true },
            { "name": "TIE", "from": 3, "to": 4, "rPu": 0.01, "xPu": 0.03, "ratingMva": 50, "inService": true }
          ]
        }
        """;

    private const string GroupsJson = """
        { "manifestVersion": "gridsim-groups/1", "case": { "sha256": "00" },
          "groups": [ { "id": "F", "kind": "feeder",
            "branches": [ {"index":0,"from":1,"to":2}, {"index":1,"from":2,"to":3}, {"index":2,"from":1,"to":4} ],
            "head": { "element": "branch", "index": 0 } } ] }
        """;

    private const string DevicesJson = """
        { "manifestVersion": "gridsim-devices/1",
          "devices": [ { "id": "BRK", "type": "breaker", "branch": { "index": 2, "from": 1, "to": 4, "end": "from" } } ] }
        """;

    private static string Study(string sha, string method, string extra = "", string devices = "\"devices\": { \"path\": \"devices.json\" },") => $$"""
        { "schemaVersion": 1, "study": "reliability-network", "method": "{{method}}", {{extra}}
          "case": { "path": "case.json", "sha256": "{{sha}}" },
          "groups": { "path": "groups.json" }, {{devices}}
          "failureData": [ { "group": "F", "failuresPerItemYear": 0.1, "repairHours": 3 } ],
          "overrides": [ { "branch": {"index":0,"from":1,"to":2}, "failuresPerYear": 0.1, "repairHours": 6 },
                         { "branch": {"index":1,"from":2,"to":3}, "failuresPerYear": 0.2, "repairHours": 4 },
                         { "branch": {"index":2,"from":1,"to":4}, "failuresPerYear": 0.3, "repairHours": 2 } ],
          "switches": [ { "id": "SW", "branch": {"index":1,"from":2,"to":3} } ],
          "ties": [ { "id": "T", "branch": {"index":3,"from":3,"to":4} } ],
          "loadPoints": [ {"id":"LP2","bus":2,"customers":100}, {"id":"LP3","bus":3,"customers":100}, {"id":"LP4","bus":4,"customers":100} ],
          "switchingHours": 0.5, "hoursPerYear": 8760,
          "acVerification": { "enabled": true, "minPu": 0.9, "maxPu": 1.1 } }
        """;

    private static (string Dir, string Sha) Prepare()
    {
        string d = Dir();
        Write(d, "nat.json", CaseM);
        Write(d, "case.json", "{ \"schema\": \"gridsim-case/2\", \"name\": \"r413\", \"system\": { \"baseMva\": 100, \"frequencyHz\": 50 }, \"network\": [\"nat.json\"] }");
        Write(d, "groups.json", GroupsJson);
        Write(d, "devices.json", DevicesJson);
        CliProcess.Result bad = CliProcess.RunBuilt("reliability-network", Write(d, "probe.json", Study("00", "enumeration")));
        Assert.Equal(1, bad.ExitCode);
        Match id = Regex.Match(bad.StdErr, "Resolved identity: ([0-9a-f]{64})");
        Assert.True(id.Success, bad.ToString());
        return (d, id.Groups[1].Value);
    }

    [Fact]
    public void Enumeration_runs_the_joined_study_and_writes_a_new_file_only()
    {
        (string d, string sha) = Prepare();
        string study = Write(d, "s.json", Study(sha, "enumeration"));
        string outFile = Path.Combine(d, "r.json");
        CliProcess.Result r = CliProcess.RunBuilt("reliability-network", study, "--out", outFile);
        Assert.True(r.ExitCode == 0, r.ToString());
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outFile));
        JsonElement root = doc.RootElement;
        Assert.Equal("gridsim-reliability-network/1", root.GetProperty("format").GetString());
        JsonElement res = root.GetProperty("result");
        Assert.Equal("COMPLETE", res.GetProperty("status").GetString());
        // Hand (breaker on b2 isolates it, so the tie back-feeds bus 4): LP2 0.1x6 + 0.2x0.5 = 0.7; LP3 0.1x0.5 + 0.2x0.5 = 0.15;
        // LP4 0.3x0.5 = 0.15; SAIDI = (0.7 + 0.15 + 0.15) / 3.
        double[] u = res.GetProperty("loadPoints").EnumerateArray().Select(p => p.GetProperty("outageHoursPerYear").GetDouble()).ToArray();
        Assert.Equal([0.7, 0.15, 0.15], u, new Tolerant());
        double saidi = res.GetProperty("systemIndices").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "SAIDI").GetProperty("value").GetDouble();
        Assert.Equal(1.0 / 3.0, saidi, 12);
        Assert.Equal("BRK", res.GetProperty("events")[2].GetProperty("clearedBy").GetString());
        Assert.Equal(64, root.GetProperty("provenance").GetProperty("groupsSha256").GetString()!.Length);
        // An existing result path is refused.
        Assert.Equal(1, CliProcess.RunBuilt("reliability-network", study, "--out", outFile).ExitCode);
    }

    private sealed class Tolerant : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-12;
        public int GetHashCode(double v) => 0;
    }

    [Fact]
    public void Monte_carlo_is_seeded_and_threads_do_not_change_a_byte()
    {
        (string d, string sha) = Prepare();
        string mc(int seed) => "\"monteCarlo\": { \"seed\": " + seed + ", \"years\": 20000 },";
        string study = Write(d, "m.json", Study(sha, "monte-carlo", mc(5)));
        CliProcess.Result one = CliProcess.RunBuilt("reliability-network", study, "--threads", "1");
        CliProcess.Result many = CliProcess.RunBuilt("reliability-network", study, "--threads", "12");
        Assert.True(one.ExitCode == 0, one.ToString());
        Assert.Equal(one.StdOut, many.StdOut);
        using JsonDocument doc = JsonDocument.Parse(one.StdOut);
        JsonElement summary = doc.RootElement.GetProperty("result").GetProperty("monteCarlo");
        Assert.Equal("gridsim-rng/1", summary.GetProperty("generatorVersion").GetString());
        Assert.Equal(5UL, summary.GetProperty("seed").GetUInt64());
        string other = Write(d, "n.json", Study(sha, "monte-carlo", mc(6)));
        using JsonDocument doc2 = JsonDocument.Parse(CliProcess.RunBuilt("reliability-network", other).StdOut);
        Assert.NotEqual(summary.GetProperty("resultDigest").GetString(), doc2.RootElement.GetProperty("result").GetProperty("monteCarlo").GetProperty("resultDigest").GetString());
    }

    [Fact]
    public void Bad_input_is_refused_with_exit_1_missing_seed_included_and_usage_with_exit_2()
    {
        (string d, string sha) = Prepare();
        // monte-carlo without its settings, enumeration with them, an unknown field, a bad method, a device that does not bind.
        Assert.Equal(1, CliProcess.RunBuilt("reliability-network", Write(d, "a.json", Study(sha, "monte-carlo"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("reliability-network", Write(d, "b.json", Study(sha, "enumeration", "\"monteCarlo\": { \"seed\": 1, \"years\": 100 },"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("reliability-network", Write(d, "c.json", Study(sha, "enumeration", "\"surprise\": 1,"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("reliability-network", Write(d, "e.json", Study(sha, "sampling"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("reliability-network", Write(d, "f.json", Study(sha, "monte-carlo", "\"monteCarlo\": { \"years\": 100 },"))).ExitCode);
        Write(d, "devices.json", DevicesJson.Replace("\"to\": 4", "\"to\": 3"));
        Assert.Equal(1, CliProcess.RunBuilt("reliability-network", Write(d, "g.json", Study(sha, "enumeration"))).ExitCode);
        // A study that is out of scope (order 2) prints the result and exits 3.
        Write(d, "devices.json", DevicesJson);
        CliProcess.Result second = CliProcess.RunBuilt("reliability-network", Write(d, "h.json", Study(sha, "enumeration", "\"order\": 2,")));
        Assert.Equal(3, second.ExitCode);
        Assert.Contains("OUT OF SCOPE", second.StdOut);
        Assert.Equal(2, CliProcess.RunBuilt("reliability-network").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("reliability-network", Path.Combine(d, "s.json"), "--bogus").ExitCode);
    }
}
