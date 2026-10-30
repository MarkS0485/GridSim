using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Frequency;
using GridSim.Core.Harmonics;
using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.IO;
using GridSim.Core.IO.Telemetry;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Realtime;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-3 (docs-session stream 3, October 2026): the no-forward-inference invariant's horizon guard, in-process. Every
/// test proves both directions: a fresh, future, undated or later-than-the-clock input is REFUSED, and an allowed
/// (historical, dated) one PASSES. Items: D-DOC-018 (a requested 'now' may not pass the real clock; the live interpose
/// flush judges against the real horizon; the web feed loads against the real horizon), D-DOC-061 (undated measurement
/// and harmonic sets; PresolvedReplay calls the guard), D-DOC-063 (strict ISO 8601 parsing), D-DOC-194 (NesoFrequencyCsv
/// refuses a fresh row).
/// </summary>
public sealed class Doc3HorizonGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-doc3-" + Guid.NewGuid().ToString("N"));

    public Doc3HorizonGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static readonly double H = ForwardInferenceGuard.HorizonHours;

    // ================================================================================================================
    // D-DOC-018: a requested 'now' may move the horizon back, never forward past the real clock
    // ================================================================================================================

    [Fact]
    public void D018_a_requested_now_later_than_the_real_clock_is_refused_and_an_earlier_one_is_used()
    {
        DateTime real = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        var ex = Assert.Throws<ForwardInferenceException>(() =>
            ForwardInferenceGuard.RequireNotAfterRealClock(new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc), "--utc-now", real));
        Assert.Equal(ForwardInferenceReason.NowLaterThanRealClock, ex.Reason);
        Assert.Contains("--utc-now 2100-01-01T00:00:00.0000000Z is later than the real clock", ex.Message, StringComparison.Ordinal);
        // One tick past the real clock is already too late.
        Assert.Throws<ForwardInferenceException>(() => ForwardInferenceGuard.RequireNotAfterRealClock(real.AddTicks(1), "x", real));

        // Allowed: the real clock itself, an earlier instant (an Unspecified one is UTC by contract), and no request.
        Assert.Equal(real, ForwardInferenceGuard.RequireNotAfterRealClock(real, "x", real));
        DateTime earlier = new(2024, 3, 1, 12, 0, 0, DateTimeKind.Unspecified);
        DateTime used = ForwardInferenceGuard.RequireNotAfterRealClock(earlier, "x", real);
        Assert.Equal(DateTimeKind.Utc, used.Kind);
        Assert.Equal(earlier.Ticks, used.Ticks);
        Assert.Equal(real, ForwardInferenceGuard.RequireNotAfterRealClock(null, "x", real));
    }

    [Fact]
    public void D018_the_realtime_engines_refuse_an_options_now_later_than_the_real_clock()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet w = WlsEstimatorTests.ExactFullSet(model, truth) with { TakenUtc = new DateTime(2099, 1, 1, 12, 0, 0, DateTimeKind.Utc) };
        var future = new RealtimeEstimatorOptions { UtcNow = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        // Before D-DOC-018 this estimated the 2099 window (the horizon moved to 2099-12-24) and reported it converged.
        var e1 = Assert.Throws<ForwardInferenceException>(() => new RealtimeEstimatorEngine(model).Run(new[] { w }, future));
        Assert.Equal(ForwardInferenceReason.NowLaterThanRealClock, e1.Reason);
        var e2 = Assert.Throws<ForwardInferenceException>(() => new RealtimeDseEngine(model).Run(new[] { w }, future));
        Assert.Equal(ForwardInferenceReason.NowLaterThanRealClock, e2.Reason);

        // Allowed: a historical window under the real clock is estimated.
        MeasurementSet old = w with { TakenUtc = DateTime.UtcNow.AddHours(-(H + 24)) };
        RealtimeRunSummary s = new RealtimeEstimatorEngine(model).Run(new[] { old }, new RealtimeEstimatorOptions());
        Assert.Equal(1, s.WindowsProcessed);
        Assert.Equal(1, s.WindowsConverged);
    }

    // ---- the live interpose flush ----------------------------------------------------------------------------------

    private static byte[] ShortFloatFrame(int ioa, float value)
    {
        var asdu = new List<byte> { 13, 0x01, 0x03, 0x00, 0x01, 0x00 };
        asdu.Add((byte)(ioa & 0xFF)); asdu.Add((byte)((ioa >> 8) & 0xFF)); asdu.Add((byte)((ioa >> 16) & 0xFF));
        var f = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(f, value);
        asdu.AddRange(f);
        asdu.Add(0x00);
        return Iec104Frame.ToBytes(new Iec104Apdu(Iec104Format.I, SendSeq: 1, RecvSeq: 0, Asdu: asdu.ToArray()));
    }

    private static InterposePipeline PipelineWithOnePoint()
    {
        var map = new RtuPointMap
        {
            Case = "case14",
            Bindings = new List<RtuPointBinding> { new() { Address = "IOA:1001", Kind = MeasurementKind.VmPu, Bus = 4, Sigma = 0.01 } },
        };
        var p = new InterposePipeline(new Iec104Protocol(), map, caseName: "case14");
        p.Ingest(ShortFloatFrame(1001, 1.02f));   // M_ME_NC_1 carries no time tag: the window instant is the fallback
        return p;
    }

    [Fact]
    public void D018_live_flush_holds_a_fresh_window_and_never_returns_it_as_an_estimator_input()
    {
        // Live telemetry: the window instant is now. Before, the CLI flushed with DateTime.MaxValue and wrote it as an
        // estimator-ready gridsim-measurements/1 window.
        InterposePipeline refuse = PipelineWithOnePoint();
        InterposeFlush? r = refuse.FlushWindow(DateTime.UtcNow, InterposeFreshPolicy.Refuse);
        Assert.NotNull(r);
        Assert.Null(r!.Estimable);
        Assert.Null(r.QuarantineCapture);
        Assert.Equal(1, refuse.HeldFresh);
        Assert.Equal(0, refuse.PendingPoints);   // the window is still flushed exactly once

        InterposePipeline keep = PipelineWithOnePoint();
        InterposeFlush? q = keep.FlushWindow(DateTime.UtcNow, InterposeFreshPolicy.QuarantineCapture);
        Assert.NotNull(q);
        Assert.Null(q!.Estimable);
        Assert.NotNull(q.QuarantineCapture);
        Assert.Equal(RtuMeasurementSetMapper.QuarantineCaptureNote, q.QuarantineCapture!.Note);
        Assert.Equal(1.02, Assert.Single(q.QuarantineCapture.Measurements).Value, 5);
        Assert.Equal(1, keep.HeldFresh);
    }

    [Fact]
    public void D018_live_flush_passes_a_historical_window_through_the_guarded_mapper()
    {
        InterposePipeline p = PipelineWithOnePoint();
        DateTime old = DateTime.UtcNow.AddHours(-(H + 10));
        InterposeFlush? r = p.FlushWindow(old, InterposeFreshPolicy.QuarantineCapture);
        Assert.NotNull(r!.Estimable);
        Assert.Null(r.QuarantineCapture);
        Assert.Equal("mapped from RTU interposer", r.Estimable!.Note);
        Assert.Equal(old, r.Estimable.TakenUtc);
        Assert.Equal(0, p.HeldFresh);
    }

    [Fact]
    public void D018_live_flush_refuses_a_test_now_later_than_the_real_clock()
    {
        InterposePipeline p = PipelineWithOnePoint();
        var ex = Assert.Throws<ForwardInferenceException>(() =>
            p.FlushWindow(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), InterposeFreshPolicy.Refuse, utcNow: new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(ForwardInferenceReason.NowLaterThanRealClock, ex.Reason);
        Assert.Equal(0, p.PendingPoints);
    }

    [Fact]
    public void D018_the_quarantine_capture_map_and_the_guarded_map_give_the_same_measurements()
    {
        // MapQuarantineCapture shares the guarded Map's body exactly (only the guard and the note differ), so a capture
        // is the same data the window would have been, bit for bit.
        var map = new RtuPointMap
        {
            Case = "c",
            Bindings = new List<RtuPointBinding> { new() { Address = "IOA:1", Kind = MeasurementKind.PInjMw, Bus = 2, Sigma = 0.5, Scale = 1.5 } },
        };
        var pts = new List<RtuPoint> { new("IOA:1", RtuPointType.Analog, 12.25, RtuQuality.Good, null) };
        DateTime old = DateTime.UtcNow.AddHours(-(H + 1));
        MeasurementSet guarded = RtuMeasurementSetMapper.Map(pts, map, old);
        MeasurementSet capture = RtuMeasurementSetMapper.MapQuarantineCapture(pts, map, old);
        Assert.Equal(guarded.Measurements, capture.Measurements);
        Assert.Equal(guarded.TakenUtc, capture.TakenUtc);
        // And the guarded map still refuses a fresh window on its own.
        Assert.Throws<ForwardInferenceException>(() => RtuMeasurementSetMapper.Map(pts, map, DateTime.UtcNow));
    }

    // ---- the web feed (same seam as serve) ---------------------------------------------------------------------------

    [Fact]
    public void D018_D061_the_web_feed_skips_fresh_and_undated_windows_and_yields_historical_ones()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet template = WlsEstimatorTests.ExactFullSet(model, truth);
        (template with { TakenUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc) }).Save(Path.Combine(_dir, "a-old.json"));
        (template with { TakenUtc = DateTime.UtcNow.AddHours(-1) }).Save(Path.Combine(_dir, "b-fresh.json"));
        (template with { TakenUtc = null }).Save(Path.Combine(_dir, "c-undated.json"));
        (template with { TakenUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc) }).Save(Path.Combine(_dir, "d-future.json"));

        var feed = new ReplayDirectoryWindowFeed(model, _dir);
        List<MeasurementSet> windows = feed.Windows().ToList();

        Assert.Single(windows);   // only the historical window
        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), windows[0].TakenUtc);
        Assert.Equal(3, feed.SkippedFiles.Count);
        Assert.StartsWith("b-fresh.json: measurement set timestamp", feed.SkippedFiles[0], StringComparison.Ordinal);
        Assert.Contains("newer than the historical horizon", feed.SkippedFiles[0], StringComparison.Ordinal);
        Assert.StartsWith("c-undated.json: measurement set", feed.SkippedFiles[1], StringComparison.Ordinal);
        Assert.Contains("carries no timestamp ('takenUtc' is missing)", feed.SkippedFiles[1], StringComparison.Ordinal);
        Assert.Contains("newer than the historical horizon", feed.SkippedFiles[2], StringComparison.Ordinal);
    }

    // ================================================================================================================
    // D-DOC-061: undated sets do not skip the guard; PresolvedReplay calls it
    // ================================================================================================================

    private static MeasurementSet OneVoltage(DateTime? taken) => new()
    {
        Case = "case9",
        TakenUtc = taken,
        Measurements = new[] { new Measurement(MeasurementKind.VmPu, 1.04, 0.004, Bus: 1) },
    };

    [Fact]
    public void D061_an_undated_measurement_set_is_refused_and_a_dated_historical_one_loads()
    {
        string undated = Path.Combine(_dir, "undated.json");
        OneVoltage(null).Save(undated);
        var ex = Assert.Throws<ForwardInferenceException>(() => MeasurementSet.Load(undated));
        Assert.Equal(ForwardInferenceReason.Undated, ex.Reason);
        Assert.Contains($"measurement set '{undated}' carries no timestamp ('takenUtc' is missing)", ex.Message, StringComparison.Ordinal);
        // Even a far-future 'now' (the old deferral trick) cannot wave an undated set through.
        Assert.Equal(ForwardInferenceReason.Undated,
            Assert.Throws<ForwardInferenceException>(() => MeasurementSet.Load(undated, DateTime.MaxValue)).Reason);

        string old = Path.Combine(_dir, "old.json");
        OneVoltage(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc)).Save(old);
        Assert.Equal(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc), MeasurementSet.Load(old).TakenUtc);

        string fresh = Path.Combine(_dir, "fresh.json");
        OneVoltage(DateTime.UtcNow.AddHours(-1)).Save(fresh);
        Assert.Equal(ForwardInferenceReason.NewerThanHorizon,
            Assert.Throws<ForwardInferenceException>(() => MeasurementSet.Load(fresh)).Reason);
    }

    [Fact]
    public void D061_a_malformed_undated_set_still_reports_what_is_malformed_first()
    {
        // The schema and required-field checks run before the date check, so the user is told the real problem.
        string path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, """{ "schema": "gridsim-measurements/1", "measurements": [ { "sigma": 0.1, "bus": 1 } ] }""");
        Assert.Throws<InvalidDataException>(() => MeasurementSet.Load(path));
        File.WriteAllText(path, """{ "schema": "gridsim-measurements/9", "measurements": [] }""");
        Assert.Throws<InvalidDataException>(() => MeasurementSet.Load(path));
    }

    [Fact]
    public void D061_a_harmonic_set_with_measured_spectra_must_be_dated_and_an_assumed_library_set_need_not_be()
    {
        HarmonicSource assumed = HarmonicSourceLibrary.Make("6-pulse", bus: 5, fundamentalCurrentPu: 0.8);
        HarmonicSource measured = assumed with { Provenance = ProvenanceTag.Recorded };
        HarmonicSource derived = assumed with { Provenance = ProvenanceTag.DerivedFromRecorded };

        string p = Path.Combine(_dir, "h.json");
        new HarmonicSourceSet { Case = "case9", Sources = new[] { assumed, measured } }.Save(p);
        var ex = Assert.Throws<ForwardInferenceException>(() => HarmonicSourceSet.Load(p));
        Assert.Equal(ForwardInferenceReason.Undated, ex.Reason);
        Assert.Contains("it holds measured spectra", ex.Message, StringComparison.Ordinal);

        new HarmonicSourceSet { Case = "case9", Sources = new[] { derived } }.Save(p);
        Assert.Equal(ForwardInferenceReason.Undated, Assert.Throws<ForwardInferenceException>(() => HarmonicSourceSet.Load(p)).Reason);

        // Allowed: measured spectra with a historical date; an all-assumed library set with no date (equipment data).
        new HarmonicSourceSet { Case = "case9", TakenUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), Sources = new[] { measured } }.Save(p);
        Assert.Single(HarmonicSourceSet.Load(p).Sources);
        new HarmonicSourceSet { Case = "case9", Sources = new[] { assumed } }.Save(p);
        Assert.Null(HarmonicSourceSet.Load(p).TakenUtc);

        // A dated set is still judged against the horizon whatever its sources.
        new HarmonicSourceSet { Case = "case9", TakenUtc = DateTime.UtcNow, Sources = new[] { assumed } }.Save(p);
        Assert.Equal(ForwardInferenceReason.NewerThanHorizon, Assert.Throws<ForwardInferenceException>(() => HarmonicSourceSet.Load(p)).Reason);
    }

    // Build a two-frame gda-replay style run directory whose state-estimate.csv carries the given time_utc cells
    // (the PresolvedReplayUtcTests layout).
    private string MakeRun(string time0, string time3)
    {
        string dir = Path.Combine(_dir, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        var trace = new SolveTrace();
        GridModel model = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        foreach (int idx in new[] { 0, 3 })
        {
            var meta = new SolveExportMeta("case9", "cpu", "c", "2026-07-02T00:00:00Z", 1.0, "test");
            SolveExport.WriteJson(Path.Combine(dir, "exports", $"state-{idx:D6}.json"), model, r, trace, meta);
        }
        File.WriteAllText(Path.Combine(dir, "state-estimate.csv"),
            "index,time_utc,gen_mw,demand_mw\n" + $"0,{time0},320.0,315.0\n" + $"3,{time3},321.0,316.0\n");
        return dir;
    }

    [Fact]
    public void D061_presolved_replay_refuses_a_replayed_state_newer_than_the_horizon()
    {
        string fresh = DateTime.UtcNow.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        string dir = MakeRun("2024-06-01T09:00:00Z", fresh);
        var ex = Assert.Throws<ForwardInferenceException>(() => PresolvedReplay.Load(dir));
        Assert.Contains("state-estimate.csv line 3: replayed state time_utc timestamp", ex.Message, StringComparison.Ordinal);

        // Allowed: an all-historical run loads; and a fixed earlier 'now' that puts the second row inside the horizon
        // refuses it (the 'now' can be moved back, never forward).
        string ok = MakeRun("2024-06-01T09:00:00Z", "2024-06-01T12:30:00Z");
        Assert.Equal(2, PresolvedReplay.Load(ok).Count);
        Assert.Throws<ForwardInferenceException>(() => PresolvedReplay.Load(ok, new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(ForwardInferenceReason.NowLaterThanRealClock,
            Assert.Throws<ForwardInferenceException>(() => PresolvedReplay.Load(ok, new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc))).Reason);
    }

    // ================================================================================================================
    // D-DOC-063: strict ISO 8601 parsing
    // ================================================================================================================

    [Theory]
    [InlineData("2024-03-01", "2024-03-01T00:00:00.0000000Z")]
    [InlineData("2024-03-01T12:34Z", "2024-03-01T12:34:00.0000000Z")]
    [InlineData("2024-03-01T12:34:56Z", "2024-03-01T12:34:56.0000000Z")]
    [InlineData("2024-03-01T12:34:56", "2024-03-01T12:34:56.0000000Z")]            // naive = UTC (the contract)
    [InlineData("2024-03-01T13:34:56+01:00", "2024-03-01T12:34:56.0000000Z")]      // offset converted
    [InlineData("2024-03-01T12:34:56.1234567Z", "2024-03-01T12:34:56.1234567Z")]   // round-trip ("o") form
    [InlineData("2024-03-01T12:34:56.5-02:00", "2024-03-01T14:34:56.5000000Z")]
    public void D063_iso_8601_and_round_trip_shapes_parse_to_utc(string text, string expectedRoundTrip)
    {
        Assert.True(UtcTime.TryParseIso8601(text, out DateTime utc), text);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(expectedRoundTrip, utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("01/02/2100")]              // the finding: read as 2 January by the lenient parser
    [InlineData("02/01/2100")]
    [InlineData("2 Jan 2100")]
    [InlineData("January 2, 2100")]
    [InlineData("2100-1-2")]
    [InlineData("2100-01-02 03:04:05Z")]    // a space, not T
    [InlineData(" 2100-01-02T03:04:05Z")]
    [InlineData("2100-01-02T03:04:05Z ")]
    [InlineData("2100-01-02T03:04:05.Z")]   // a fraction point with no digits
    [InlineData("2100-01-02T24:00:00Z")]
    [InlineData("2100-13-02T00:00:00Z")]
    [InlineData("2100-01-02T03:04:05.12345678Z")]   // eight fraction digits
    [InlineData("20240301T120000Z")]        // ISO basic format: not accepted (one reading only, extended form)
    [InlineData("")]
    [InlineData(null)]
    public void D063_ambiguous_or_malformed_instants_are_refused(string? text)
    {
        Assert.False(UtcTime.TryParseIso8601(text, out _), text ?? "(null)");
    }

    [Fact]
    public void D063_a_phaseangle_tuple_with_an_ambiguous_date_is_refused_and_an_iso_one_parses()
    {
        var ex = Assert.Throws<FormatException>(() => PhaseAngleFeed.Parse("1:0:01/02/2100:50"));
        Assert.Contains("UTC '01/02/2100' is not an ISO-8601 instant", ex.Message, StringComparison.Ordinal);
        PhaseAngleObs ok = Assert.Single(PhaseAngleFeed.Parse("1:0:2024-03-01T12:00:00Z:50"));
        Assert.Equal(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc), ok.Utc);
    }

    // ================================================================================================================
    // D-DOC-194: NesoFrequencyCsv refuses a fresh row
    // ================================================================================================================

    private string Csv(params string[] rows)
    {
        string path = Path.Combine(_dir, "neso-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllLines(path, new[] { "dtm,f" }.Concat(rows));
        return path;
    }

    private static string Stamp(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void D194_a_fresh_row_is_refused_and_a_historical_file_reads()
    {
        DateTime now = DateTime.UtcNow;
        // Historical rows, then one row an hour old: the read refuses the file when it reaches the fresh row.
        string mixed = Csv("2024-08-01T00:00:00Z,50.01", "2024-08-01T00:00:01Z,50.02", $"{Stamp(now.AddHours(-1))},49.98");
        var ex = Assert.Throws<ForwardInferenceException>(() => NesoFrequencyCsv.Read(mixed).ToList());
        Assert.StartsWith("NESO frequency sample timestamp", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ForwardInferenceReason.NewerThanHorizon, ex.Reason);
        // It is a stream: the rows before the fresh one were yielded first, but no caller that reads to the end gets a result.
        Assert.Equal(2, NesoFrequencyCsv.Read(mixed).Take(2).Count());

        // A future row, and a row 1 h inside the horizon, are refused; a row 1 h past the horizon is read.
        Assert.Throws<ForwardInferenceException>(() => NesoFrequencyCsv.Read(Csv("2100-01-01T00:00:00Z,50.0")).ToList());
        Assert.Throws<ForwardInferenceException>(() => NesoFrequencyCsv.Read(Csv($"{Stamp(now.AddHours(-(H - 1)))},50.0")).ToList());
        FrequencySample old = Assert.Single(NesoFrequencyCsv.Read(Csv($"{Stamp(now.AddHours(-(H + 1)))},50.05")).ToList());
        Assert.Equal(50.05, old.Hz);

        // An offset row that is fresh in UTC terms is refused too (the stamp is converted to UTC, not read as local).
        string offset = now.AddHours(-1).AddHours(5).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "+05:00";
        Assert.Throws<ForwardInferenceException>(() => NesoFrequencyCsv.Read(Csv($"{offset},50.0")).ToList());

        // Allowed: the bundled historical sample reads in full.
        string sample = Path.Combine(CliProcess.Root(), "data", "neso");
        string? any = Directory.Exists(sample) ? Directory.EnumerateFiles(sample, "*.csv").FirstOrDefault() : null;
        if (any is not null) Assert.NotEmpty(NesoFrequencyCsv.Read(any).Take(5).ToList());
    }
}
