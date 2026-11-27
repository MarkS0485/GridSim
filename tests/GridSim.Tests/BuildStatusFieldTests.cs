using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Cli;
using GridSim.Core.Diagnostics.SupplyChain;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The build status (RELEASE or UNSUPPORTED BUILD) a running GridSim reports about itself: the rule
/// (<see cref="BuildStatus.Of"/>, from the compliance attestation), and the additive field in the --json result document
/// (<c>buildStatus</c>, after <c>tool</c>), the run journal line (<c>buildStatus</c>, after <c>commit</c>) and the rpc hello
/// (<c>build_status</c>, after <c>tool</c>). A test build is never a release build, so it reports UNSUPPORTED BUILD, and the
/// field is the only difference from the document as it was written before the field existed.
/// </summary>
public sealed class BuildStatusFieldTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("build-status");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private const string FullCommit = "0123456789abcdef0123456789abcdef01234567";

    private static ComplianceAttestation Attestation(bool release, int inScope, int achieved, string commit = FullCommit) =>
        new("2.1.0", commit, "hash", release ? new string('a', 64) : BuildProvenance.SbomUnset, "manifest", inScope, achieved, release);

    [Fact]
    public void Release_is_a_release_build_that_names_its_full_commit_whatever_the_compliance_verdict()
    {
        // A CI release build of a clean checkout that names a full commit: RELEASE, compliant or not.
        Assert.Equal("RELEASE", BuildStatus.Of(Attestation(release: true, inScope: 3, achieved: 3)));
        Assert.Equal("RELEASE", BuildStatus.Of(Attestation(release: true, inScope: 3, achieved: 2)));
        Assert.False(Attestation(release: true, inScope: 3, achieved: 2).IsCompliant);
        Assert.Equal("RELEASE", BuildStatus.Of(Attestation(release: true, inScope: 3, achieved: 0, commit: new string('b', 64))));
        // Not a release build: UNSUPPORTED BUILD, compliant counts or not.
        Assert.Equal("UNSUPPORTED BUILD", BuildStatus.Of(Attestation(release: false, inScope: 3, achieved: 3)));
        Assert.Equal("UNSUPPORTED BUILD", BuildStatus.Of(Attestation(release: false, inScope: 0, achieved: 0)));
        // A release build whose commit does not name one checkout: UNSUPPORTED BUILD.
        Assert.Equal("UNSUPPORTED BUILD", BuildStatus.Of(Attestation(release: true, inScope: 3, achieved: 3, commit: "01234567")));
        Assert.Equal("UNSUPPORTED BUILD", BuildStatus.Of(Attestation(release: true, inScope: 3, achieved: 3, commit: "unknown")));
        Assert.Equal("UNSUPPORTED BUILD", BuildStatus.Of(Attestation(release: true, inScope: 3, achieved: 3, commit: new string('g', 40))));
        Assert.Equal(BuildStatus.Of(ComplianceAttestation.Current), BuildStatus.Current);
        // A test build injects no SBOM hash, so it is not a release build.
        Assert.False(ComplianceAttestation.Current.IsReleaseBuild);
        Assert.Equal(BuildStatus.Unsupported, BuildStatus.Current);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("0123456", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456", false)]
    [InlineData("0123456789abcdef0123456789abcdef012345678", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Full_commit_is_40_or_64_hex_digits(string? commit, bool expected) =>
        Assert.Equal(expected, BuildStatus.IsFullCommit(commit));

    [Fact]
    public void Result_document_has_build_status_after_tool_and_is_otherwise_unchanged()
    {
        var r = new CliResult("test");
        r.Set("x", 0.5).Table("rows", "i").Row(1);
        string json = CliResultWriter.JsonText(r, "gridsim test", ["test"], 0);
        using JsonDocument d = JsonDocument.Parse(json);
        Assert.Equal(new[] { "schema", "tool", "buildStatus", "verb", "args", "exitCode", "summary", "tables" },
            d.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("UNSUPPORTED BUILD", d.RootElement.GetProperty("buildStatus").GetString());
        // The document as written before the field: the same text with the one buildStatus line taken out.
        string nl = Environment.NewLine == "\r\n" && json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string before = string.Join(nl, new[]
        {
            "{", "  \"schema\": \"gridsim-cli-result/1\",", "  \"tool\": \"gridsim test\",", "  \"verb\": \"test\",",
            "  \"args\": [", "    \"test\"", "  ],", "  \"exitCode\": 0,", "  \"summary\": {", "    \"x\": 0.5", "  },",
            "  \"tables\": {", "    \"rows\": [", "      {", "        \"i\": 1", "      }", "    ]", "  }", "}",
        });
        Assert.Equal(before, json.Replace("  \"buildStatus\": \"UNSUPPORTED BUILD\"," + nl, "", StringComparison.Ordinal));
    }

    [Fact]
    public void Journal_line_has_build_status_after_commit_only_when_known()
    {
        var e = new JournalEntry { Seq = 1, RecordedUtc = "2026-01-01T00:00:00.0000000Z", Version = "2.1.0", InformationalVersion = "2.1.0+abc", Commit = "abc", Verb = "pf" };
        string without = JournalJson.Line(e);
        Assert.DoesNotContain("buildStatus", without, StringComparison.Ordinal);
        e.BuildStatus = BuildStatus.Unsupported;
        string with = JournalJson.Line(e);
        Assert.Contains("\"commit\":\"abc\",\"buildStatus\":\"UNSUPPORTED BUILD\",\"verb\":\"pf\"", with, StringComparison.Ordinal);
        Assert.Equal(without, with.Replace(",\"buildStatus\":\"UNSUPPORTED BUILD\"", "", StringComparison.Ordinal));
        // Both forms read back to the same line.
        Assert.Equal(with, JournalJson.Line(JournalJson.Parse(with, out string? err1)!));
        Assert.Null(err1);
        JournalEntry old = JournalJson.Parse(without, out string? err2)!;
        Assert.Null(err2);
        Assert.Null(old.BuildStatus);
        Assert.Equal(without, JournalJson.Line(old));
    }

    [Fact]
    public void A_local_build_reports_unsupported_build_in_json_journal_and_hello()
    {
        string journal = Path.Combine(_dir, "j", "journal.jsonl");
        CliProcess.Result run = JournalTestSupport.Run(_dir, "--journal", journal, "pf", "case9", "--json", "r.json");
        Assert.Equal(0, run.ExitCode);

        string json = File.ReadAllText(Path.Combine(_dir, "r.json"));
        using (JsonDocument d = JsonDocument.Parse(json))
        {
            string[] keys = d.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(new[] { "schema", "tool", "buildStatus", "verb", "args", "exitCode", "summary", "tables" }, keys);
            Assert.Equal("UNSUPPORTED BUILD", d.RootElement.GetProperty("buildStatus").GetString());
        }

        string line = Assert.Single(File.ReadAllLines(journal), l => l.Length > 0);
        Assert.Matches("\"commit\":\"[^\"]*\",\"buildStatus\":\"UNSUPPORTED BUILD\",\"verb\":\"pf\"", line);
        Assert.Equal(BuildStatus.Unsupported, JournalJson.Parse(line, out _)!.BuildStatus);

        string hello = Hello();
        Assert.Matches("^\\{\"protocol\":\"gridsim-rpc/1\",\"id\":1,\"op\":\"hello\",\"status\":\"ok\",\"exitCode\":0,\"case_sha256\":null,\"result\":"
            + "\\{\"protocol\":\"gridsim-rpc/1\",\"tool\":\"gridsim [^\"]+\",\"build_status\":\"UNSUPPORTED BUILD\",\"operations\":\\[", hello);
    }

    // One rpc session that asks hello and ends; the first line of its output.
    private string Hello()
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet")
        {
            WorkingDirectory = _dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(CliProcess.BuiltCliDll());
        psi.ArgumentList.Add("rpc");
        psi.Environment["GRIDSIM_CRASH_DIR"] = Path.Combine(_dir, "crash");
        psi.Environment.Remove("GRIDSIM_LICENCE");
        psi.Environment["GRIDSIM_LICENCE_FILE"] = Path.Combine(_dir, CliProcess.IsolatedLicenceFileName);
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        Task<string> outText = p.StandardOutput.ReadToEndAsync();
        p.StandardInput.Write("{\"id\":1,\"op\":\"hello\"}\n");
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(120_000), "rpc did not finish");
        p.WaitForExit();
        _ = err.Result;
        return Regex.Split(outText.Result, "\r?\n").First(l => l.Length > 0);
    }
}
