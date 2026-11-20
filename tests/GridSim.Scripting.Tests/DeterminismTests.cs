using System.Text;
using System.Text.Json;
using Xunit;

namespace GridSim.Scripting.Tests;

public class DeterminismTests
{
    private static string ScriptText() => string.Join("\n",
        "GRAMMAR 1",
        "# static part",
        TestPaths.Load("case9"),
        "SOLVE POWERFLOW",
        "REPORT BUSES",
        "REPORT BRANCHES",
        "EXPORT RESULT \"out/base.json\"",
        "TRIP LINE \"4-5\"",
        "SOLVE POWERFLOW",
        "EXPORT RESULT \"out/line-out.json\"",
        "CLOSE LINE \"4-5\"",
        "# live part",
        "SELECT ENGINE LIVE",
        "SET LOAD \"L5\" P 110 MW",
        "AT 100 MS TRIP GENERATOR \"G2\"",
        "AT 100 MS SET LOAD \"L7\" P 90 MW",
        "STEP SIM 400 MS",
        "REPORT SIM",
        "CLOSE GENERATOR \"G2\"",
        "STEP SIM 0.2 S",
        "REPORT SIM",
        "");

    private sealed record Outcome(string Record, string BaseJson, string LineOutJson, string Reports);

    private static Outcome RunIn(string dir, byte[] script)
    {
        var text = new StringWriter();
        RunResult r = Runner.Run(script, new RunOptions(dir, text));
        Assert.True(r.Ok, r.Error?.Display);
        return new Outcome(r.RecordJson,
            File.ReadAllText(Path.Combine(dir, "out", "base.json")),
            File.ReadAllText(Path.Combine(dir, "out", "line-out.json")),
            text.ToString());
    }

    [Fact]
    public void Same_script_twice_in_process_and_once_in_a_fresh_session_gives_identical_bytes()
    {
        byte[] script = Encoding.UTF8.GetBytes(ScriptText());

        Outcome first = RunIn(TestPaths.TempDir(), script);
        Outcome second = RunIn(TestPaths.TempDir(), script);

        // A fresh session: a new thread, a new Session object and a new LiveSim, nothing shared with the runs above.
        Outcome? third = null;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { third = RunIn(TestPaths.TempDir(), script); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { failure = ex; }
        });
        t.Start();
        t.Join();
        Assert.Null(failure);

        Assert.Equal(first.Record, second.Record);
        Assert.Equal(first.Record, third!.Record);
        Assert.Equal(first.BaseJson, second.BaseJson);
        Assert.Equal(first.BaseJson, third.BaseJson);
        Assert.Equal(first.LineOutJson, second.LineOutJson);
        Assert.Equal(first.LineOutJson, third.LineOutJson);
        Assert.Equal(first.Reports, second.Reports);
        Assert.Equal(first.Reports, third.Reports);

        // The two exports really differ (so the equality above is not vacuous).
        Assert.NotEqual(first.BaseJson, first.LineOutJson);
    }

    [Fact]
    public void The_run_record_carries_everything_the_design_asks_for()
    {
        byte[] script = Encoding.UTF8.GetBytes(ScriptText());
        string dir = TestPaths.TempDir();
        RunResult r = Runner.Run(script, new RunOptions(dir));
        Assert.True(r.Ok, r.Error?.Display);
        using JsonDocument doc = JsonDocument.Parse(r.RecordJson);
        JsonElement root = doc.RootElement;

        Assert.Equal("gridsim-script-run/1", root.GetProperty("schema").GetString());
        Assert.Equal(GridSim.Core.BuildInfo.Version, root.GetProperty("gridsimVersion").GetString());
        Assert.Equal("1", root.GetProperty("grammarVersion").GetString());
        Assert.Equal(Runner.Sha256(script), root.GetProperty("scriptSha256").GetString());
        string expectedModel = Runner.Sha256(File.ReadAllBytes(TestPaths.Case("case9")));
        Assert.Equal(expectedModel, root.GetProperty("modelSha256").GetString());
        Assert.Equal(expectedModel, root.GetProperty("inputs")[0].GetProperty("sha256").GetString());

        JsonElement solver = root.GetProperty("solver");
        Assert.Equal("LIVE", solver.GetProperty("engine").GetString());
        Assert.Equal(20_000, solver.GetProperty("integrationStepUs").GetInt64());
        Assert.True(solver.GetProperty("tolerancePu").GetDouble() > 0);
        Assert.False(string.IsNullOrEmpty(solver.GetProperty("powerFlow").GetString()));

        Assert.Equal(2, root.GetProperty("steps").GetArrayLength());
        Assert.Equal(20, root.GetProperty("steps")[0].GetProperty("ticks").GetInt32());
        Assert.Equal(64, root.GetProperty("steps")[0].GetProperty("frequencySeriesSha256").GetString()!.Length);
        Assert.Equal(2, root.GetProperty("events").GetArrayLength());
        Assert.Equal(100_000, root.GetProperty("events")[0].GetProperty("timeUs").GetInt64());

        // Output hashes: each export's recorded hash is the hash of the file on disk.
        foreach (JsonElement o in root.GetProperty("outputs").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "export"))
        {
            string file = Path.Combine(dir, o.GetProperty("name").GetString()!);
            Assert.Equal(Runner.Sha256(File.ReadAllBytes(file)), o.GetProperty("sha256").GetString());
        }
        Assert.Equal(2, root.GetProperty("outputs").EnumerateArray().Count(x => x.GetProperty("kind").GetString() == "export"));
        Assert.Equal("ok", root.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(600_000, root.GetProperty("result").GetProperty("finalClockUs").GetInt64());

        // No clock, host name or absolute path leaks into the record.
        Assert.DoesNotContain(dir, r.RecordJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, r.RecordJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_different_script_gives_a_different_record()
    {
        string a = ScriptText();
        string b = a.Replace("STEP SIM 400 MS", "STEP SIM 420 MS", StringComparison.Ordinal);
        Outcome ra = RunIn(TestPaths.TempDir(), Encoding.UTF8.GetBytes(a));
        Outcome rb = RunIn(TestPaths.TempDir(), Encoding.UTF8.GetBytes(b));
        Assert.NotEqual(ra.Record, rb.Record);
    }
}
