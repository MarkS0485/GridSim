using System.Globalization;
using System.Numerics;
using System.Text;
using GridSim.Core.Analysis;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dispatch;
using GridSim.Core.Events;
using GridSim.Core.Harmonics;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit.Iec61363;
using Xunit;

namespace GridSim.Tests;

/// <summary>Bug hunt batch 5, ANA and SEC lows: one regression test per fix that a unit test can reach.</summary>
public sealed class BugHunt1125LowsAnaSecTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bh1125-lows-anasec-" + Guid.NewGuid().ToString("N"));

    public BugHunt1125LowsAnaSecTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static GridModel TwoBus() => new()
    {
        Name = "two", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 30, Qd: 10, BaseKv: 132) },
        Generators = new[] { new Generator(1, Pg: 30, Vg: 1.0, Qmin: -100, Qmax: 100, Pmax: 100) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
    };

    [Fact]
    public void Ana12_an_unknown_candidate_source_bus_is_named()
    {
        var ex = Assert.Throws<ArgumentException>(() => HarmonicStateEstimator.Estimate(TwoBus(), 5,
            new[] { new HarmonicVoltageMeasurement(1, Complex.Zero), new HarmonicVoltageMeasurement(2, Complex.Zero) }, new[] { 99 }));
        Assert.Contains("candidate source 0 names bus 99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ana15_a_bus_number_that_is_not_whole_is_a_problem_line()
    {
        string doc(string bus) => "{ \"schema\": \"" + Iec61363Data.Schema + "\", \"generators\": [ { \"bus\": " + bus
            + ", \"xdTransPu\": 0.2, \"xdSyncPu\": 1.8, \"tdSubS\": 0.02, \"tdTransS\": 0.8, \"taS\": 0.1 } ], \"motors\": [] }";
        var ex = Assert.Throws<FormatException>(() => Iec61363Data.Parse(doc("2.7")));
        Assert.Contains("'bus' must be a whole bus number (got 2.7)", ex.Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => Iec61363Data.Parse(doc("1e12")));
        try { Iec61363Data.Parse(doc("2.0")); }
        catch (FormatException f) { Assert.DoesNotContain("'bus'", f.Message, StringComparison.Ordinal); }
    }

    [Fact]
    public void Ana18_an_exhaustive_screen_beyond_the_combination_cap_is_refused_before_any_solve()
    {
        var buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 132) };
        var branches = new List<Branch>();
        for (int i = 2; i <= 600; i++) { buses.Add(new Bus(i, BusType.Pq, Pd: 0.1, BaseKv: 132)); branches.Add(new Branch(1, i, 0.01, 0.1)); }
        var m = new GridModel { Name = "star", BaseMva = 100, Buses = buses, Generators = new[] { new Generator(1, Pg: 60, Vg: 1.0) }, Branches = branches };
        var ex = Assert.Throws<ArgumentException>(() => ContingencyAnalysis.ScreenNMinusKReport(m, 3, exhaustive: true));
        Assert.Contains("outage combinations, more than the 20000000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sec14_a_licence_file_of_comment_lines_only_holds_no_key()
        => Assert.Equal("", LicenceGate.ExtractToken("  # only a comment  \n# another\n"));

    [Fact]
    public void Sec13_an_over_long_licence_file_is_no_licence_and_does_not_stop_start_up()
    {
        string p = Path.Combine(_dir, "big.gridsim");
        File.WriteAllText(p, new string('A', LicenceGate.MaxLicenceFileBytes + 10));
        Assert.Null(LicenceGate.ReadFileToken(p));
        string ok = Path.Combine(_dir, "ok.gridsim");
        File.WriteAllText(ok, "# header\nGSK1.abc\n");
        Assert.Equal("GSK1.abc", LicenceGate.ReadFileToken(ok));
    }

    [Fact]
    public void Sec15_a_null_event_is_named_by_position()
    {
        var ex = Assert.Throws<InvalidDataException>(() => EventCatalog.Validate(new GridEvent[] { null! }));
        Assert.Contains("Event 0 of the catalog is null", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sec19_one_rule_for_a_line_breaking_outcome()
    {
        Assert.True(AuditEvent.BreaksLine((char)0x2028));
        Assert.True(AuditEvent.BreaksLine('\n'));
        Assert.False(AuditEvent.BreaksLine('x'));
    }

    [Fact]
    public void Sec23_a_model_with_no_slack_is_refused_by_name()
    {
        var m = TwoBus().CopyWith(buses: new[] { new Bus(1, BusType.Pv, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 30, BaseKv: 132) });
        var ex = Assert.Throws<InvalidDataException>(() => Dispatcher.BalanceDetailed(m));
        Assert.Contains("has no slack bus", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// R3-08 (review of batch 3): a malformed or null JSON document given to each verb that reads a study or data JSON ends
/// with a clean refusal (exit 1 or 2), never an internal error or a crash log.
/// </summary>
public sealed class BugHunt1125JsonVerbFuzzTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("bh1125-json-fuzz");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    public static IEnumerable<object[]> Verbs() => new[]
    {
        "transmission-security", "transfer-capability", "motor-start", "dynamics-study", "corrective-sequence", "planning-study",
        "cgmes-study", "restoration-study", "reliability-study", "emt-line", "arc-flash", "cable-rating", "cable-size",
        "cosim", "emt-partition", "long-term", "probabilistic", "reliability-network", "connection-assess", "protection-audit",
        "study-case", "qsts", "hosting", "emt3",
    }.Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(Verbs))]
    public void A_malformed_or_null_document_is_a_clean_refusal(string verb)
    {
        foreach ((string name, string text) in new[] { ("bad.json", "{ \"schema\": "), ("null.json", "null"), ("array.json", "[1, 2]") })
        {
            string p = Path.Combine(_dir, verb + "-" + name);
            File.WriteAllText(p, text, new UTF8Encoding(false));
            CliProcess.Result r = JournalTestSupport.Run(_dir, verb, p);
            Assert.True(r.ExitCode is 1 or 2, $"{verb} {name}: {r}");
            Assert.DoesNotContain("internal error", r.StdErr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Object reference not set", r.StdErr, StringComparison.Ordinal);
        }
    }
}
