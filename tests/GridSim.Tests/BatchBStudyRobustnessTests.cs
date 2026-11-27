using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Study;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>Post-RC3 batch B: the RC-3 study verbs report incomplete, not pass (CLI-01, CLI-02, CLI-05), refuse
/// bad input cleanly (CLI-03, CLI-04, CLI-26) and read booleans as the importer does (IO-27).</summary>
public sealed class BatchBStudyRobustnessTests
{
    private static TransferEvaluation Feasible() => new(TransferClass.Feasible, [], "", null, "F");

    [Theory]
    [InlineData(3.6, 1.2)]
    [InlineData(0.9, 0.3)]
    [InlineData(7.2, 0.6)]
    public void An_always_feasible_walk_ends_at_the_cap_and_is_verified(double max, double step)
    {
        var o = TransferSearch.Run(_ => Feasible(), new TransferSearchOptions(max, step, 0.1, 50, 1));
        Assert.Equal("AtLeastMax", o.Outcome);
        Assert.Equal("VERIFIED", o.Status);
        Assert.Equal(max, o.Lower!.TransferMw);
        Assert.Equal(max, o.Trials.Where(t => t.Phase == "coarse").Max(t => t.TransferMw));
        Assert.Equal(0, o.Accounting.CoarseUnevaluated);
    }

    private static CgmesStudyRequest Request(CimImportOptions options, CgmesSecurityRequest? security = null) =>
        new(CgmesStudyJobTests.Bundle(), options, 0.95, 1.05, ApplyControls: true, Security: security);

    private static readonly CimImportOptions Rated = new(StartingVoltages: true, ApparentPowerRatings: true, RequireSshSetPoints: true);

    [Fact]
    public void Unrated_live_branches_make_the_cgmes_study_incomplete_not_secure()
    {
        CgmesStudyResult r = CgmesStudyRunner.Run(Request(Rated with { ApparentPowerRatings = false })).Result;
        Assert.Equal("INCOMPLETE", r.Intact.Disposition);
        Assert.Contains("no rating", r.Intact.Reason);
        Assert.Equal("Incomplete", r.Outcome);
        Assert.Equal("NOT SECURE", r.Status);
    }

    [Fact]
    public void A_mistyped_security_outage_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            CgmesStudyRunner.Run(Request(Rated, new CgmesSecurityRequest(["_NO_SUCH_BRANCH"], PostFaultRating.RateA))));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("True", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("0", false)]
    [InlineData("False", false)]
    [InlineData("yes", null)]
    [InlineData(null, null)]
    public void The_study_reads_booleans_as_the_importer_does(string? text, bool? expected) =>
        Assert.Equal(expected, CgmesBundleInspector.ParseBoolean(text));

    [Fact]
    public void A_switch_state_written_as_zero_is_recorded_as_found()
    {
        string ssh = File.ReadAllText(Path.Combine(CgmesStudyJobTests.FixtureDir, "p3_SSH.xml"))
            .Replace("<cim:Switch.open>false</cim:Switch.open>", "<cim:Switch.open>0</cim:Switch.open>");
        byte[] bytes = Encoding.UTF8.GetBytes(ssh);
        var profiles = CgmesStudyJobTests.Bundle();
        profiles[2] = new CgmesProfileInput("SSH", "2.4.15", "p3_SSH.xml", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
        var request = new CgmesStudyRequest(profiles, Rated with { Topology = CimTopology.Switches }, 0.95, 1.05,
            ApplyControls: true, SwitchActions: [new CgmesSwitchAction("_BRK_A", true)]);
        CgmesStudyResult r = CgmesStudyRunner.Run(request).Result;
        Assert.False(r.Switching!.Actions.Single().AsFoundOpen);
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public void An_explicit_null_profile_path_exits_1_without_a_crash()
    {
        string dir = TestTemp.Under("gridsim-batchb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string study = Path.Combine(dir, "study.json");
            File.WriteAllText(study, """{"schemaVersion":1,"bundle":{"sha256":"00","profiles":[{"profile":"EQ","version":"3","path":null,"sha256":"00"}]},"voltage":{"minPu":0.9,"maxPu":1.1}}""");
            var run = CliProcess.RunBuilt("cgmes-study", study);
            Assert.Equal(1, run.ExitCode);
            Assert.DoesNotContain("NullReference", run.StdErr);
        }
        finally { Directory.Delete(dir, true); }
    }

    private const string Parallel = """
        function mpc = parallel
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        1 3 0 0 0 0 1 1 0 230 1 1.1 0.9;
        2 1 80 30 0 0 1 1 0 230 1 1.1 0.9;
        ];
        mpc.gen = [GENROW];
        mpc.branch = [
        1 2 0 .2 0 100 100 100 0 0 1 -360 360;
        1 2 0 .2 0 100 100 100 0 0 1 -360 360;
        ];
        """;

    private static (string Dir, string Study) TsStudy(string gen, object? costs)
    {
        string dir = TestTemp.Under("gridsim-batchb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string model = Path.Combine(dir, "parallel.m");
        File.WriteAllText(model, Parallel.Replace("GENROW", gen));
        string hash = Sha(File.ReadAllBytes(model));
        string study = Path.Combine(dir, "study.json");
        var doc = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["case"] = new { path = "parallel.m", sha256 = hash },
            ["outages"] = new[] { new { index = 0, from = 1, to = 2 } },
            ["voltage"] = new { minPu = 0.9, maxPu = 1.1 },
            ["postFaultRating"] = "A",
        };
        if (costs is not null) doc["costs"] = costs;
        File.WriteAllText(study, JsonSerializer.Serialize(doc));
        return (dir, study);
    }

    [Fact]
    public void A_slack_bus_without_a_unit_is_never_reported_secure()
    {
        (string dir, string study) = TsStudy("2 80 0 100 0 1 100 1 200 0;", null);
        try
        {
            var run = CliProcess.RunBuilt("transmission-security", study);
            Assert.NotEqual(0, run.ExitCode);
            Assert.DoesNotContain("\"SECURE\"", run.StdOut.Replace("NOT SECURE", ""));
            Assert.Contains("slack bus has no in-service unit", run.StdOut + run.StdErr);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_explicit_null_in_the_security_study_is_refused()
    {
        (string dir, string study) = TsStudy("1 80 0 100 0 1 100 1 200 0;", new { perMwh = (object?)null });
        try
        {
            var run = CliProcess.RunBuilt("transmission-security", study);
            Assert.Equal(1, run.ExitCode);
        }
        finally { Directory.Delete(dir, true); }
    }
}
