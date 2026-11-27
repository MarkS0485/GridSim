using System.Globalization;
using System.Text;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (2.1.118): B27 in <c>breaking-change validate</c>. From 2.1.117, an EXDC2, IEEEX1, ESAC8B, ESST3A or IEEEG3 record
/// read with <c>--dyr-extended --allow-unmodelled</c> is modelled instead of left out. Like B3 it claims only a change of
/// model, so it is raised only for a job whose command line holds both options and whose DYR input holds one of those
/// records. Such a job is UNBOUNDED (B27); any other job never is.
/// </summary>
public sealed class Rc5BreakingChangeB27Tests
{
    private static readonly BreakingChangeMap Map = BreakingChangeMap.Default;

    // A build after B27 (2.1.117) and a job recorded on 2.1.100: B27 is in the job's window and B3 (2.1.083) is not.
    private const string Build = "2.1.118";
    private const string Recorded = "2.1.100";

    private static readonly string[] Argv = ["stability", "net.raw", "--rms", "--dyn-data", "net.dyr", "--dyr-extended", "--allow-unmodelled", "--json", "s.json"];

    private static readonly string[] Models = ["EXDC2", "IEEEX1", "ESAC8B", "ESST3A", "IEEEG3"];

    private static byte[] DyrWith(string model) => Encoding.ASCII.GetBytes($"1 'GENROU' 1 6.5 0.06 0.2 0.05 5.0 0.0 1.8 1.7 0.3 0.55 0.25 0.2 0.1 0.3 /\n1 '{model}' 1 0.0 300.0 /\n");

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static ReplayOutcome Job(IReadOnlyList<string> argv, byte[]? dyr, string recorded = Recorded) =>
        Job(argv, dyr is null ? Array.Empty<UnboundedChanges.DyrInput>() : new[] { new UnboundedChanges.DyrInput("net.dyr", dyr, null) }, recorded);

    // The verb is argv[0]. A recorded --dyn-data or .dyr token always gives validate a DyrInput: its bytes, or null bytes
    // with the reason they could not be read. A job with no such token has no DYR input at all.
    private static ReplayOutcome Job(IReadOnlyList<string> argv, UnboundedChanges.DyrInput[] inputs, string recorded = Recorded)
    {
        string verb = argv[0];
        FileComparison c = ResultCompare.Compare("s.json", Encoding.UTF8.GetBytes("""{"x_test":[1.0,2.0]}"""),
            Encoding.UTF8.GetBytes($$"""{"x_test":[{{N(1.5)}},2.0]}"""), verb, s => s, s => s, VolatileFields.Default);
        var o = new ReplayOutcome { Seq = 1, RecordedUtc = "2026-11-20T00:00:00.0000000Z", Verb = verb, RecordedVersion = recorded, Status = ReplayStatus.Changed };
        o.Fields.AddRange(c.Fields);
        UnboundedChanges.Judge(Map, o, Map.Explain(recorded, Build, verb, argv, ["/w/net.raw", "/w/net.dyr"]), verb, argv, inputs);
        return o;
    }

    private static readonly string[] PfArgv = ["pf", "case14", "--dyr-extended", "--allow-unmodelled", "--json", "s.json"];

    [Fact]
    public void B27_is_in_the_map_without_a_bound_and_scoped_to_both_options()
    {
        BreakingChange b = Map.Entries.Single(x => x.Id == "B27");
        Assert.True(UnboundedChanges.IsUnbounded(b));
        Assert.Equal("2.1.117", b.Version);
        Assert.Equal("2.1.117", b.Changelog);
        Assert.Contains("B27", Map.Explain(Recorded, Build, "stability", Argv, []));
        Assert.DoesNotContain("B3", Map.Explain(Recorded, Build, "stability", Argv, []));
    }

    [Fact]
    public void A_job_with_both_options_and_one_of_the_five_records_is_unbounded_b27()
    {
        foreach (string model in Models)
        {
            ReplayOutcome o = Job(Argv, DyrWith(model));
            Assert.Equal(ClaimVerdict.Unbounded, o.Verdict);
            Assert.Equal(new[] { "B27" }, o.ExplainedBy);
            FieldDifference f = Assert.Single(o.Fields);
            Assert.Equal(ClaimVerdict.Unbounded, f.Verdict);
            Assert.Equal("B27", f.ExplainedBy);
            Assert.Equal(4, ReplayReport.ExitCode([o]));
            Assert.Contains("UNBOUNDED (B27), review needed", ReplayReport.ConsoleText([o], Build), StringComparison.Ordinal);
        }
        // 2.1.127 (the B27 scope made strict): a recorded DYR whose bytes could not be read raises nothing and assumes
        // nothing (it used to raise "B27 assumed"); validate makes such a job CANNOT-REPLAY ("input unreadable") first.
        ReplayOutcome unread = Job(Argv, [new UnboundedChanges.DyrInput("net.dyr", null, "access denied")]);
        // 2.1.130: the exact verdict and reason: the moved field is UNEXPLAINED (exit 3), explained by nothing, with no note.
        Assert.Equal(ClaimVerdict.Unexplained, unread.Verdict);
        Assert.Empty(unread.ExplainedBy);
        Assert.Null(unread.Reason);
        Assert.Equal(3, ReplayReport.ExitCode([unread]));
        // Accepted, it is REVIEWED.
        ReplayOutcome accepted = Job(Argv, DyrWith("ESST3A"));
        UnboundedChanges.Review([accepted], new AcceptFile(), ["B27"], Build);
        Assert.Equal(ClaimVerdict.Reviewed, accepted.Verdict);
    }

    [Fact]
    public void A_job_outside_the_b27_condition_is_never_unbounded()
    {
        var cases = new List<(string Why, ReplayOutcome O)>
        {
            ("only --dyr-extended", Job(Argv.Where(a => a != "--allow-unmodelled").ToArray(), DyrWith("EXDC2"))),
            ("only --allow-unmodelled", Job(Argv.Where(a => a != "--dyr-extended").ToArray(), DyrWith("EXDC2"))),
            ("no option", Job(Argv.Where(a => a is not "--dyr-extended" and not "--allow-unmodelled").ToArray(), DyrWith("EXDC2"))),
            ("a DYR with none of the five records", Job(Argv, DyrWith("SEXS"))),
            ("a CSVGN1 record only (B3's model)", Job(Argv, DyrWith("CSVGN1"))),
            ("a name in a comment after '/'", Job(Argv, Encoding.ASCII.GetBytes("1 'GENCLS' 1 6.0 0.0 /  EXDC2 IEEEG3 named only in a comment\n"))),
            ("a longer model name", Job(Argv, Encoding.ASCII.GetBytes("1 'EXDC2A' 1 0.0 /\n"))),
            ("recorded on 2.1.117, after the change", Job(Argv, DyrWith("EXDC2"), "2.1.117")),
            ("no DYR input (pf, which reads none)", Job(PfArgv, Array.Empty<UnboundedChanges.DyrInput>())),
            ("no DYR input (stability with a JSON --dyn-data)", Job(Argv.Select(a => a == "net.dyr" ? "net.json" : a).ToArray(), Array.Empty<UnboundedChanges.DyrInput>())),
        };
        foreach ((string why, ReplayOutcome o) in cases)
        {
            Assert.True(o.Verdict != ClaimVerdict.Unbounded, $"{why}: {o.Verdict}");
            Assert.DoesNotContain("B27", o.ExplainedBy);
            Assert.All(o.Fields, f => Assert.NotEqual(ClaimVerdict.Unbounded, f.Verdict));
            Assert.True(o.Reason is null || !o.Reason.Contains("assumed", StringComparison.Ordinal), $"{why}: {o.Reason}");
        }
        Assert.Empty(UnboundedChanges.Review(cases.Select(c => c.O).ToList(), new AcceptFile(), ["B27"], Build));
    }
}
