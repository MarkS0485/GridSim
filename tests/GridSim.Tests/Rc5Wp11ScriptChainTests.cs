using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP11: the second link of the STUDIES chain. GridSim.Scripting.Tests' Rc5Wp11StudyExtensionTests shows a script's
/// STUDY equals these Core calls, bit for bit, on the model a script's LOAD CASE reads (MatpowerParser.Parse at 60 Hz); this
/// file shows the same Core calls equal the CLI verbs' result files (nminus1, iec60909 --bus 5, acopf-full) on the same
/// MATPOWER file, bit for bit. Together: a STUDY gives the CLI's numbers.
/// </summary>
public sealed class Rc5Wp11ScriptChainTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-wp11-chain-" + Guid.NewGuid().ToString("N"));

    public Rc5Wp11ScriptChainTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Case9Path => Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m");

    /// <summary>The model a script's <c>LOAD CASE "case9.m" FREQUENCY 60 HZ</c> holds.</summary>
    private static GridModel ScriptModel() => MatpowerParser.Parse(File.ReadAllText(Case9Path), "case9", 60.0);

    private JsonElement Summary(params string[] args)
    {
        string json = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        CliProcess.Result r = CliProcess.RunBuilt([.. args, "--json", json]);
        Assert.True(File.Exists(json), r.ToString());
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(json));
        return d.RootElement.GetProperty("summary").Clone();
    }

    private static void Bits(double expected, JsonElement actual) =>
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual.GetDouble()));

    [Fact]
    public void Short_circuit_study_core_call_equals_iec60909_bus_5()
    {
        GridModel m = ScriptModel();
        Iec60909.Duty d = Iec60909.ThreePhase(m, 5, ShortCircuitObjective.Maximum, f0Hz: m.FrequencyHz, breakTimeSeconds: 0.10, faultDurationSeconds: 1.0,
            peakFactorMethod: PeakFactorMethod.MethodB);
        Bits(d.IkppKa, Summary("iec60909", Case9Path, "--bus", "5").GetProperty("ikpp_ka"));
    }

    [Fact]
    public void Contingency_study_core_calls_equal_nminus1()
    {
        GridModel m = ScriptModel();
        PowerFlowOptions o = ContingencyAnalysis.StudyOptions with { PerBusVoltageLimits = true, PostFaultRating = PostFaultRating.RateA };
        ContingencyResult b = ContingencyAnalysis.ScreenBaseCase(m, ContingencyAnalysis.CreateStudySolver(m), o);
        var results = ContingencyAnalysis.NetAgainstBase(ContingencyAnalysis.ScreenBranches(m, ContingencyAnalysis.CreateStudySolver(m), o), b);
        JsonElement s = Summary("nminus1", Case9Path);
        Assert.Equal(results.Count, s.GetProperty("screened").GetInt32());
        Assert.Equal(results.Count(c => !c.Converged || !c.Secure),
            s.GetProperty("with_violations").GetInt32() + s.GetProperty("islanded").GetInt32() + s.GetProperty("not_solved").GetInt32());
    }

    [Fact]
    public void Opf_study_core_call_equals_acopf_full()
    {
        AcOpfIpmResult r = AcOpfInteriorPoint.Solve(ScriptModel(), new AcOpfIpmOptions { MaxIterations = 150 });
        Bits(r.ObjectivePerHour, Summary("acopf-full", Case9Path).GetProperty("objective_per_h"));
    }
}
