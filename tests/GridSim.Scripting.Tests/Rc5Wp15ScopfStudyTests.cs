using System.Globalization;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Scripting.Tests;

/// <summary>
/// RC5 WP15: STUDY SCOPF in a <c>GRAMMAR 1 WITH STUDIES</c> script. The oracle is the Core call the <c>scopf</c> verb makes
/// with its defaults (<see cref="DcScopf.Solve"/>: every branch outage, rating A, fuel-type costs, constraint generation,
/// 50 rounds) on the same model, bit for bit in the run record's round-trip text; GridSim.Tests'
/// Rc5Wp15ObjectModelAddDeleteTests ties the same call to the CLI verb. SCOPF and SECURED are read in place and are not
/// reserved, so scripts that used them as names are unchanged.
/// </summary>
public sealed class Rc5Wp15ScopfStudyTests
{
    private static RunResult RunScript(params string[] lines) =>
        Runner.Run(new System.Text.UTF8Encoding(false).GetBytes(string.Join("\n", lines) + "\n"), new RunOptions(TestPaths.TempDir()));

    private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    [Fact]
    public void Scopf_study_equals_the_core_call_of_the_scopf_verb()
    {
        RunResult r = RunScript("GRAMMAR 1 WITH STUDIES", TestPaths.Load("case9"), "STUDY SCOPF AS c", "LET o = OBJECTIVE OF STUDY c",
            "LET n = SECURED OF STUDY c", "PRINT n", "REPORT STUDY c");
        Assert.True(r.Ok, r.Error?.Message);
        GridModel m = MatpowerParser.Parse(File.ReadAllText(TestPaths.Case("case9")), "case9", 60.0);
        ScopfResult want = DcScopf.Solve(m, new ScopfOptions
        {
            Contingencies = null, PostContingencyRating = PostFaultRating.RateA, CostPerGen = null, ConstraintGeneration = true, MaxRounds = 50,
        });
        Assert.True(want.Optimal);
        using JsonDocument d = JsonDocument.Parse(r.RecordJson);
        JsonElement st = d.RootElement.GetProperty("studies")[0];
        Assert.Equal("SCOPF", st.GetProperty("study").GetString());
        Assert.Equal(R(want.TotalCost), st.GetProperty("objective").GetString());
        Assert.Equal(want.Contingencies.Count, st.GetProperty("secured").GetInt32());
        Assert.Equal(want.Status.ToString(), st.GetProperty("status").GetString());
        Assert.Equal((decimal)want.Contingencies.Count, r.Session.Globals["n"].Number);
    }

    [Fact]
    public void Scopf_and_secured_stay_free_names()
    {
        Session s = TestPaths.Run(TestPaths.TempDir(), "GRAMMAR 1 WITH STUDIES", "LET scopf = 1", "LET secured = scopf + 1", "PRINT secured");
        Assert.Equal(2m, s.Globals["secured"].Number);
        Session plain = TestPaths.Run(TestPaths.TempDir(), "LET scopf = 3");
        Assert.Equal(3m, plain.Globals["scopf"].Number);
        // A SCOPF reading of another kind of study is refused by name.
        RunResult r = RunScript("GRAMMAR 1 WITH STUDIES", TestPaths.Load("case9"), "STUDY CONTINGENCY AS a", "LET n = SECURED OF STUDY a");
        Assert.False(r.Ok);
        Assert.Contains("CONTINGENCY study", r.Error!.Message, StringComparison.Ordinal);
    }
}
