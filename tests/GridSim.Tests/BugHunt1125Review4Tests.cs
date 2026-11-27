using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GridSim.Cli;
using GridSim.Core.IO.Comtrade;
using GridSim.Core.IO.Psse;
using GridSim.Core.Journal;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug hunt of 2026-11-25, batch 4b: the review of 2.1.157 to 2.1.160 (REVIEW-B4.md). The py launcher and relative
/// interpreter paths (R4-01, R4-11) and the token-file names (R4-06) are in Doc7ValidationSidecarTests and
/// BugHunt1125SecurityTests; the rest is here.
/// </summary>
public sealed class BugHunt1125Review4Tests
{
    // ---- R4-02 / R4-09: which unhandled exceptions are input failures --------------------------------------------------

    private sealed class Loop { public Loop? Next { get; set; } }

    private static JsonException ReaderException()
    {
        try { using JsonDocument d = JsonDocument.Parse("{ \"a\": "); }
        catch (JsonException ex) { return ex; }
        throw new InvalidOperationException("the parse did not fail");
    }

    private static JsonException WriterException()
    {
        var a = new Loop();
        a.Next = a;
        try { _ = JsonSerializer.Serialize(a); }
        catch (JsonException ex) { return ex; }
        throw new InvalidOperationException("the serialisation did not fail");
    }

    [Fact]
    public void R4_02_only_a_reader_json_exception_is_bad_input_and_a_serializer_cycle_is_a_defect()
    {
        JsonException reader = ReaderException(), writer = WriterException();
        Assert.NotNull(reader.LineNumber);
        Assert.Null(writer.LineNumber);
        Assert.Equal(CliFailures.Kind.Json, CliFailures.Classify(reader).Kind);
        Assert.Equal(CliFailures.Kind.None, CliFailures.Classify(writer).Kind);
        Assert.Equal(CliFailures.Kind.None, CliFailures.Classify(new AggregateException(reader, writer)).Kind);
    }

    [Fact]
    public void R4_09_an_aggregate_of_file_and_json_reading_failures_is_an_input_failure()
    {
        JsonException reader = ReaderException();
        Assert.Equal(CliFailures.Kind.Mixed, CliFailures.Classify(new AggregateException(new IOException("x"), reader)).Kind);
        Assert.Equal(CliFailures.Kind.File, CliFailures.Classify(new AggregateException(new IOException("x"), new UnauthorizedAccessException("y"))).Kind);
        Assert.Equal(CliFailures.Kind.Json, CliFailures.Classify(new AggregateException(reader)).Kind);
        Assert.Equal(CliFailures.Kind.None, CliFailures.Classify(new AggregateException(new IOException("x"), new InvalidOperationException("defect"))).Kind);
        Assert.Equal(CliFailures.Kind.File, CliFailures.Classify(new IOException("x")).Kind);
        Assert.Equal(CliFailures.Kind.None, CliFailures.Classify(new InvalidOperationException("defect")).Kind);
    }

    // ---- R4-03: a tap range the search cannot cover is refused ---------------------------------------------------------

    [Fact]
    public void R4_03_a_tap_range_with_more_positions_than_the_search_covers_is_refused()
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(PsseLccTests.Case(vschd: 520), "lcc").Model;
        Assert.DoesNotContain(m.Validate(), d => d.Severity == ValidationSeverity.Error);
        // 0.51 to 1.5 at 1e-5: about 99,000 positions; the search stopped quietly after 10,000.
        GridModel fine = m.CopyWith(lccLinks: m.LccLinks.Select(l => l with { Rectifier = l.Rectifier with { TapMin = 0.51, TapMax = 1.5, TapStep = 1e-5 } }).ToList());
        GridDiagnostic d = Assert.Single(fine.Validate(), x => x.Severity == ValidationSeverity.Error);
        Assert.Contains("rectifier's tap range 0.51 to 1.5 at a step of 1E-05 has", d.Message, StringComparison.Ordinal);
        Assert.Contains("positions, more than the 10000 the tap search moves through", d.Message, StringComparison.Ordinal);
        // A fine but searchable range (1,000 positions) is accepted.
        GridModel edge = m.CopyWith(lccLinks: m.LccLinks.Select(l => l with { Rectifier = l.Rectifier with { TapMin = 0.5, TapMax = 1.5, TapStep = 1e-3 } }).ToList());
        Assert.DoesNotContain(edge.Validate(), x => x.Severity == ValidationSeverity.Error);
    }

    // ---- R4-04: rate segments by binary search, whatever the order of the samples --------------------------------------

    private static string Cfg(string rates) =>
        $"STN,DEV,1999\n1,1A,0D\n1,IA,A,,A,1,0,0,-32768,32767,1,1,P\n50\n{rates}\n01/01/2020,00:00:00.000000\n01/01/2020,00:00:00.000000\nASCII\n1\n";

    [Fact]
    public void R4_04_decreasing_sample_numbers_over_many_rates_are_timed_in_log_time_with_the_same_times()
    {
        const int n = 100_000;
        var rates = new StringBuilder();
        for (int k = 1; k <= n; k++) rates.Append(k % 2 == 0 ? "2000," : "1000,").Append(k).Append('\n');
        var data = new StringBuilder();
        for (int k = n; k >= 1; k--) data.Append(k).Append(",,1\n");   // the last sample first: the old cursor walked every time
        var sw = Stopwatch.StartNew();
        ComtradeRecording r = ComtradeReader.Parse(Cfg(n + "\n" + rates.ToString().TrimEnd('\n')), Encoding.ASCII.GetBytes(data.ToString()));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
        Assert.Equal(n, r.TimesSeconds.Count);
        // The times are those of the in-order walk: sample 1 at 0, sample 2 at 0.5 ms (2000 Hz), sample 3 at 1.5 ms.
        Assert.Equal(0.0, r.TimesSeconds[n - 1]);
        Assert.Equal(0.0005, r.TimesSeconds[n - 2], 12);
        Assert.Equal(0.0015, r.TimesSeconds[n - 3], 12);
    }

    // ---- R4-05: only sharing and lock violations are retried ---------------------------------------------------------

    [Fact]
    public void R4_05_only_a_sharing_or_lock_violation_is_retried()
    {
        Assert.True(JournalSnapshot.IsSharingOrLock(new IOException("shared", unchecked((int)0x80070020))));   // ERROR_SHARING_VIOLATION
        Assert.True(JournalSnapshot.IsSharingOrLock(new IOException("locked", unchecked((int)0x80070021))));   // ERROR_LOCK_VIOLATION
        Assert.False(JournalSnapshot.IsSharingOrLock(new IOException("disk full", unchecked((int)0x80070070)))); // ERROR_DISK_FULL
        Assert.False(JournalSnapshot.IsSharingOrLock(new IOException("too long", unchecked((int)0x800700CE)))); // ERROR_FILENAME_EXCED_RANGE
        Assert.Equal(OperatingSystem.IsWindows(), JournalSnapshot.IsSharingOrLock(new UnauthorizedAccessException("denied")));
    }
}
