using System.Text;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;

namespace GridSim.Tests;

/// <summary>RC4 R4-01 test cases: small native JSON cases written into a temporary folder (so no digest-bound fixture
/// bytes are checked in), with their independent closed-form oracles documented where they are used.</summary>
internal static class R401Cases
{
    internal static readonly string[] Files = ["system.json", "buses.json", "generation.json", "demand.json", "transmission.json"];

    internal static string NewRoot()
    {
        string root = TestTemp.Under("gridsim-r401-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Put(string dir, string name, string text)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), new UTF8Encoding(false).GetBytes(text));
    }

    /// <summary>The RC3 W1 two-bus network: 100 MVA base, slack 1.0 pu, demand 80 MW and 30 MVAr, two ideal parallel
    /// 0.2 pu reactances (named one and two).</summary>
    internal static string TwoBus(string root, string name = "two-bus")
    {
        string dir = Path.Combine(root, name);
        Put(dir, "system.json", "{ \"name\": \"R4-01 two-bus\", \"baseMva\": 100, \"frequencyHz\": 50, \"dispatch\": false }\n");
        Put(dir, "buses.json", "[\n  { \"id\": 1, \"name\": \"Source\", \"baseKv\": 230 },\n  { \"id\": 2, \"name\": \"Load\", \"baseKv\": 230 }\n]\n");
        Put(dir, "generation.json", "[\n  { \"name\": \"G1\", \"bus\": 1, \"type\": \"ccgt\", \"role\": \"slack\", \"capacityMva\": 200, \"outputMw\": 80, \"pMinMw\": 0, \"pMaxMw\": 200, \"qMinMvar\": 0, \"qMaxMvar\": 100, \"voltageSetpoint\": 1.0 }\n]\n");
        Put(dir, "demand.json", "[\n  { \"name\": \"Load\", \"bus\": 2, \"pMw\": 80, \"qMvar\": 30 }\n]\n");
        Put(dir, "transmission.json", "[\n  { \"name\": \"one\", \"from\": 1, \"to\": 2, \"rPu\": 0, \"xPu\": 0.2, \"bPu\": 0, \"ratingMva\": 100 },\n  { \"name\": \"two\", \"from\": 1, \"to\": 2, \"rPu\": 0, \"xPu\": 0.2, \"bPu\": 0, \"ratingMva\": 100 }\n]\n");
        return dir;
    }

    /// <summary>A lossless three-bus case for the grouping oracle. Bus 1 slack (data set point 120 MW), bus 2 load
    /// 100 MW and 20 MVAr, bus 3 a 40 MW unit and a 60 MW and 10 MVAr load; three 0.1 pu reactances; no resistance or
    /// charging. Area A is {1, 2} and area B is {3}.</summary>
    internal static string ThreeBus(string root, string name = "three-bus")
    {
        string dir = Path.Combine(root, name);
        Put(dir, "system.json", "{ \"name\": \"R4-01 three-bus\", \"baseMva\": 100, \"frequencyHz\": 50, \"dispatch\": false }\n");
        Put(dir, "buses.json", "[\n  { \"id\": 1, \"name\": \"A1\", \"baseKv\": 230 },\n  { \"id\": 2, \"name\": \"A2\", \"baseKv\": 230 },\n  { \"id\": 3, \"name\": \"B1\", \"baseKv\": 230 }\n]\n");
        Put(dir, "generation.json", "[\n  { \"name\": \"G1\", \"bus\": 1, \"type\": \"ccgt\", \"role\": \"slack\", \"capacityMva\": 400, \"outputMw\": 120, \"pMinMw\": 0, \"pMaxMw\": 400, \"qMinMvar\": -200, \"qMaxMvar\": 200, \"voltageSetpoint\": 1.0 },\n  { \"name\": \"G2\", \"bus\": 3, \"type\": \"ccgt\", \"role\": \"pv\", \"capacityMva\": 100, \"outputMw\": 40, \"pMinMw\": 0, \"pMaxMw\": 100, \"qMinMvar\": -50, \"qMaxMvar\": 50, \"voltageSetpoint\": 1.0 }\n]\n");
        Put(dir, "demand.json", "[\n  { \"name\": \"D2\", \"bus\": 2, \"pMw\": 100, \"qMvar\": 20 },\n  { \"name\": \"D3\", \"bus\": 3, \"pMw\": 60, \"qMvar\": 10 }\n]\n");
        Put(dir, "transmission.json", "[\n  { \"name\": \"L1-2\", \"from\": 1, \"to\": 2, \"rPu\": 0, \"xPu\": 0.1, \"bPu\": 0, \"ratingMva\": 300 },\n  { \"name\": \"L2-3\", \"from\": 2, \"to\": 3, \"rPu\": 0, \"xPu\": 0.1, \"bPu\": 0, \"ratingMva\": 300 },\n  { \"name\": \"L1-3\", \"from\": 1, \"to\": 3, \"rPu\": 0, \"xPu\": 0.1, \"bPu\": 0, \"ratingMva\": 300 }\n]\n");
        return dir;
    }

    /// <summary>The bundle digest of a case folder, as every study verb takes it: path TAB sha256 LF per file, in load order.</summary>
    internal static string Bundle(string dir) => PlanningFingerprints.Bundle(Files
        .Where(f => File.Exists(Path.Combine(dir, f)))
        .Select(f => new PlanningInputFile(f, PlanningFingerprints.Sha256(File.ReadAllBytes(Path.Combine(dir, f))))));

    internal static PlanningMaster Master(string dir)
    {
        PlanningInputFile[] inputs = Files.Where(f => File.Exists(Path.Combine(dir, f)))
            .Select(f => new PlanningInputFile(f, PlanningFingerprints.Sha256(File.ReadAllBytes(Path.Combine(dir, f))))).ToArray();
        return new PlanningMaster(JsonGridLoader.LoadDirectory(dir, dispatch: false), PlanningFingerprints.Bundle(inputs), inputs,
            new PlanningInterpretation(PlanningInterpretation.NativeJson, false));
    }

    internal static string Sha(string path) => PlanningFingerprints.Sha256(File.ReadAllBytes(path));

    /// <summary>The RC3 W1 closed form (per unit, lossless, sending voltage 1.0, equivalent reactance x, demand p + jq):
    /// y = V^2 is the larger root of y^2 + (2xq - 1)y + x^2(p^2 + q^2) = 0.</summary>
    internal static double ReceivingVoltage(double x, double pMw, double qMvar, double baseMva = 100)
    {
        double p = pMw / baseMva, q = qMvar / baseMva;
        double b = 2 * x * q - 1;
        double y = (-b + Math.Sqrt(b * b - 4 * x * x * (p * p + q * q))) / 2;
        return Math.Sqrt(y);
    }

    internal static string TwoBusStudyCase(string bundle, string extraVariations = "", string scenarios = "", string studyScenario = "peak",
        string groups = "", string? instants = null)
    {
        instants ??= "{ \"id\": \"before\", \"instant\": \"2026-06-01T00:00:00Z\" }, { \"id\": \"after\", \"instant\": \"2027-02-01T00:00:00Z\" }";
        scenarios = scenarios.Length > 0 ? scenarios :
            "{ \"id\": \"peak\", \"operations\": [ { \"id\": \"all-load\", \"field\": \"load\", \"action\": \"scale\", \"value\": 1.0 } ] }";
        return "{\n" +
            "  \"manifestVersion\": \"gridsim-studycase/1\",\n" +
            "  \"master\": { \"case\": { \"path\": \"two-bus\", \"sha256\": \"" + bundle + "\" }, \"interpretation\": { \"loader\": \"native-json\", \"dispatch\": false } },\n" +
            groups +
            "  \"instants\": [ " + instants + " ],\n" +
            "  \"variations\": [ { \"id\": \"outage-one\", \"expansionStage\": \"2027 outage\", \"operations\": [ { \"id\": \"open-one\", " +
            "\"target\": { \"element\": \"branch\", \"index\": 0, \"from\": 1, \"to\": 2 }, \"field\": \"inService\", \"expectedPrior\": true, \"value\": false, " +
            "\"effectiveFrom\": \"2027-01-01T00:00:00Z\" } ] }" + extraVariations + " ],\n" +
            "  \"scenarios\": [ " + scenarios + " ],\n" +
            "  \"studies\": [ { \"id\": \"n1\", \"scenario\": \"" + studyScenario + "\", \"mode\": \"transmission-security\", \"parameters\": { " +
            "\"outages\": [ { \"index\": 0, \"from\": 1, \"to\": 2 }, { \"index\": 1, \"from\": 1, \"to\": 2 } ], " +
            "\"voltage\": { \"minPu\": 0.9, \"maxPu\": 1.1 }, \"postFaultRating\": \"A\" } } ]\n}\n";
    }
}
