using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-3 (docs-session stream 3, October 2026), the built CLI end to end. D-DOC-018: <c>serve --utc-now</c> cannot move
/// the horizon past real time, and serve loads windows against the real horizon. D-DOC-061: an undated measurement set
/// is refused by estimate and serve. D-DOC-063: <c>--utc-now</c> / <c>--phaseangle-at</c> parse strictly. D-DOC-107:
/// <c>pmu-ingest</c> applies the horizon guard and builds measurement sets with <c>--channels</c>. D-DOC-108: STAT and
/// the FRACSEC time quality decide which PMU samples are used. Each refusal is paired with the allowed case.
/// </summary>
public sealed class Doc3CliHorizonProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-doc3-cli-" + Guid.NewGuid().ToString("N"));

    public Doc3CliHorizonProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static void AssertExit(int expected, CliProcess.Result r) => Assert.True(r.ExitCode == expected, r.ToString());

    private string Windows(params DateTime?[] stamps)
    {
        string dir = Path.Combine(_dir, "windows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet template = WlsEstimatorTests.ExactFullSet(model, truth);
        for (int i = 0; i < stamps.Length; i++)
            (template with { TakenUtc = stamps[i] }).Save(Path.Combine(dir, $"w{i:D3}.json"));
        return dir;
    }

    private static readonly DateTime Historical = new(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    // ================================================================================================================
    // D-DOC-018 / D-DOC-063: serve --utc-now
    // ================================================================================================================

    [Fact]
    public void D018_serve_utc_now_later_than_the_real_clock_is_rejected_before_any_window_is_estimated()
    {
        // The finding's reproduction: a window stamped 2099 with --utc-now 2100-01-01 was estimated and exited 0.
        string dir = Windows(new DateTime(2099, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        CliProcess.Result r = CliProcess.RunBuilt("serve", "case9", "--measurements-dir", dir, "--utc-now", "2100-01-01T00:00:00Z");
        AssertExit(2, r);
        Assert.Contains("REJECTED (forward inference): --utc-now 2100-01-01T00:00:00.0000000Z is later than the real clock", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("#0", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("done:", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void D018_serve_utc_now_earlier_than_the_real_clock_still_gives_a_reproducible_stricter_run()
    {
        // Allowed: an earlier 'now'. With now = 2024-03-05, a window of 2024-03-01 is inside the 169 h horizon (skipped),
        // and one of 2024-02-01 is estimated.
        string dir = Windows(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), Historical);
        CliProcess.Result r = CliProcess.RunBuilt("serve", "case9", "--measurements-dir", dir, "--utc-now", "2024-03-05T00:00:00Z");
        AssertExit(0, r);
        Assert.Contains("horizon guard: now=2024-03-05T00:00:00.0000000Z", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("done: processed 1, converged 1, rejected-fresh 1, failed 0", r.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("01/02/2100")]
    [InlineData("2 Jan 2024")]
    [InlineData("2024-03-05 00:00:00")]
    public void D063_serve_utc_now_in_an_ambiguous_shape_is_a_usage_error(string value)
    {
        string dir = Windows(Historical);
        CliProcess.Result r = CliProcess.RunBuilt("serve", "case9", "--measurements-dir", dir, "--utc-now", value);
        AssertExit(2, r);
        Assert.Contains($"error: --utc-now expected an ISO 8601 UTC instant (for example 2024-03-01T12:00:00Z) but got '{value}'", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void D018_serve_counts_a_fresh_window_refused_at_load_as_rejected_fresh()
    {
        // The window is refused by MeasurementSet.Load against the real horizon (the seam serve used to switch off with
        // DateTime.MaxValue); the summary reads exactly as when the engine skipped it.
        string dir = Windows(DateTime.UtcNow.AddHours(-1));
        CliProcess.Result r = CliProcess.RunBuilt("serve", "case9", "--measurements-dir", dir);
        AssertExit(3, r);
        Assert.Contains("done: processed 0, converged 0, rejected-fresh 1, failed 0", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("error: no window produced a usable estimate: all 1 window(s) are newer than the 169 h horizon.", r.StdErr, StringComparison.Ordinal);

        string mixed = Windows(Historical, DateTime.UtcNow.AddHours(-1));
        CliProcess.Result m = CliProcess.RunBuilt("serve", "case9", "--measurements-dir", mixed);
        AssertExit(0, m);
        Assert.Contains("done: processed 1, converged 1, rejected-fresh 1, failed 0", m.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void D061_serve_and_estimate_refuse_an_undated_measurement_set()
    {
        string dir = Windows((DateTime?)null);
        CliProcess.Result s = CliProcess.RunBuilt("serve", "case9", "--measurements-dir", dir);
        AssertExit(2, s);
        Assert.Contains("REJECTED (forward inference): measurement set '", s.StdErr, StringComparison.Ordinal);
        Assert.Contains("carries no timestamp ('takenUtc' is missing)", s.StdErr, StringComparison.Ordinal);

        string file = Path.Combine(dir, "w000.json");
        CliProcess.Result e = CliProcess.RunBuilt("estimate", "case9", "--measurements", file, "--out", Path.Combine(_dir, "est"));
        AssertExit(2, e);
        Assert.Contains("carries no timestamp ('takenUtc' is missing)", e.StdErr, StringComparison.Ordinal);

        // Allowed: the same set, dated in the past, estimates.
        string dated = Windows(Historical);
        CliProcess.Result ok = CliProcess.RunBuilt("estimate", "case9", "--measurements", Path.Combine(dated, "w000.json"), "--out", Path.Combine(_dir, "est2"));
        AssertExit(0, ok);
    }

    [Fact]
    public void D063_estimate_phaseangle_at_in_an_ambiguous_shape_is_a_usage_error()
    {
        string dated = Windows(Historical);
        string set = Path.Combine(dated, "w000.json");
        CliProcess.Result r = CliProcess.RunBuilt("estimate", "case9", "--measurements", set, "--phaseangle", "1:0:2024-03-01T12:00:00Z:50",
            "--phaseangle-at", "01/02/2100", "--out", Path.Combine(_dir, "pa"));
        AssertExit(2, r);
        Assert.Contains("error: --phaseangle-at expected an ISO 8601 UTC instant (for example 2024-03-01T12:00:00Z) but got '01/02/2100'", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void D018_the_live_paths_no_longer_defer_the_guard_with_datetime_maxvalue()
    {
        // Source-level anti-drift (the interpose listener needs a live socket to run end to end; its flush is proved in
        // Doc3HorizonGuardTests): no DateTime.MaxValue in serve or interpose, and the live flush is the policy overload.
        string program = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
        // Code only: the comments explain the old DateTime.MaxValue deferral, so they are stripped before the check.
        string serve = CodeOnly(Body(program, "void RunServe(", "\n// interpose [case]"));
        string interpose = CodeOnly(Body(program, "void RunInterpose(", "\n// emt [case]"));
        Assert.DoesNotContain("DateTime.MaxValue", serve, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime.MaxValue", interpose, StringComparison.Ordinal);
        Assert.Contains("MeasurementSet.Load(f, now)", serve, StringComparison.Ordinal);
        Assert.Contains("pipeline.FlushWindow(DateTime.UtcNow, freshPolicy)", interpose, StringComparison.Ordinal);
        string feed = CodeOnly(File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Web", "Realtime", "ReplayDirectoryWindowFeed.cs")).Replace("\r\n", "\n"));
        Assert.DoesNotContain("DateTime.MaxValue", feed, StringComparison.Ordinal);
    }

    // Each line up to any '//' (no string in these bodies holds '//').
    private static string CodeOnly(string src) => string.Join("\n", src.Split('\n').Select(l =>
    {
        int c = l.IndexOf("//", StringComparison.Ordinal);
        return c >= 0 ? l[..c] : l;
    }));

    private static string Body(string src, string start, string end)
    {
        int a = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, start);
        int b = src.IndexOf(end, a, StringComparison.Ordinal);
        Assert.True(b > a, end);
        return src[a..b];
    }

    // ================================================================================================================
    // D-DOC-107 / D-DOC-108: pmu-ingest
    // ================================================================================================================

    // One float-format PMU with two phasors: phasor 0 a voltage, phasor 1 a current (PHUNIT type byte 1).
    private const int PmuId = 101;
    private static readonly C37118DataFormat FloatAll = C37118DataFormat.PhasorFloat | C37118DataFormat.FreqFloat;

    private static byte[] Finish(byte[] f)
    {
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(f.Length - 2), C37118Crc.Compute(f.AsSpan(0, f.Length - 2)));
        return f;
    }

    private static void Header(byte[] f, C37118FrameType type, uint soc, uint fracsec)
    {
        f[0] = 0xAA;
        f[1] = (byte)(((int)type << 4) | 1);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)f.Length);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 7);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(6), soc);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(10), fracsec);
    }

    private static byte[] Config(uint soc)
    {
        const int phasors = 2;
        int size = 14 + 4 + 2 + (16 + 2 + 2 + 2 + 2 + 2 + 16 * phasors + 4 * phasors + 2 + 2) + 2 + 2;
        var f = new byte[size];
        Header(f, C37118FrameType.Config2, soc, 0);
        int off = 14;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), 1_000_000); off += 4;   // TIME_BASE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;           // NUM_PMU
        Encoding.ASCII.GetBytes("STN_A".PadRight(16)).CopyTo(f.AsSpan(off)); off += 16;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), PmuId); off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)FloatAll); off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), phasors); off += 2;
        off += 4;                                                                    // ANNMR, DGNMR = 0
        off += 16 * phasors;                                                         // channel names (blank)
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), 0); off += 4;           // phasor 0: voltage
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), 0x0100_0000); off += 4; // phasor 1: current
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;           // FNOM 50 Hz
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;           // CFGCNT
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), 50);                     // DATA_RATE
        return Finish(f);
    }

    // A DATA frame: STAT, phasor 0 (rectangular volts), phasor 1 (rectangular amps), FREQ, DFREQ.
    private static byte[] Data(uint soc, uint fracsec, ushort stat, double v0re = 9_500.0, double v0im = 0.0)
    {
        var f = new byte[14 + 2 + 2 * 8 + 8 + 2];
        Header(f, C37118FrameType.Data, soc, fracsec);
        int off = 14;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), stat); off += 2;
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), (float)v0re); off += 4;
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), (float)v0im); off += 4;
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), 1_000.0f); off += 4;
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), -100.0f); off += 4;
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), 50.0f); off += 4;
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), 0.0f);
        return Finish(f);
    }

    private string Capture(params byte[][] frames)
    {
        string path = Path.Combine(_dir, $"cap-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, frames.SelectMany(x => x).ToArray());
        return path;
    }

    private const uint OldSoc = 1_700_000_000;   // 2023-11-14T22:13:20Z: historical

    private static uint Soc(DateTime t) => (uint)(t - DateTime.UnixEpoch).TotalSeconds;

    [Fact]
    public void D107_pmu_ingest_refuses_a_capture_with_a_frame_newer_than_the_horizon()
    {
        uint fresh = Soc(DateTime.UtcNow.AddHours(-1));
        string cap = Capture(Config(OldSoc), Data(OldSoc, 0, 0), Data(fresh, 0, 0));
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap);
        AssertExit(2, r);
        Assert.Contains("REJECTED (forward inference): pmu-ingest: ", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("C37.118 DATA frame timestamp", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("PMU INGEST", r.StdOut, StringComparison.Ordinal);   // nothing summarised from a fresh capture

        // A future frame too.
        string future = Capture(Config(OldSoc), Data(Soc(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)), 0, 0));
        AssertExit(2, CliProcess.RunBuilt("pmu-ingest", future));

        // Allowed: an all-historical capture.
        CliProcess.Result ok = CliProcess.RunBuilt("pmu-ingest", Capture(Config(OldSoc), Data(OldSoc, 0, 0), Data(OldSoc, 20_000, 0)));
        AssertExit(0, ok);
        Assert.Contains("| STN_A | 101 | 2 | 2 |", ok.StdOut, StringComparison.Ordinal);
        Assert.Contains(" | 0 | 0 |", ok.StdOut, StringComparison.Ordinal);   // STAT non-zero 0, dropped 0
        Assert.Contains("aligned instants: 2 (0 with a PMU missing)", ok.StdOut, StringComparison.Ordinal);
        Assert.Contains("quality: 0 sample(s) dropped and not used", ok.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((ushort)0x8000, 0u, "1 invalid data")]          // STAT 15-14 = 10: test mode / do not use
    [InlineData((ushort)0x4000, 0u, "1 invalid data")]          // 01: PMU error, no information
    [InlineData((ushort)0xC000, 0u, "1 invalid data")]          // 11: PMU error
    [InlineData((ushort)0x2000, 0u, "1 unsynchronised")]        // bit 13: not in sync with UTC
    [InlineData((ushort)0x0100, 0u, "1 unsynchronised")]        // time quality 100: 100 us
    [InlineData((ushort)0x0010, 0u, "1 unsynchronised")]        // unlocked 10 s to 100 s
    [InlineData((ushort)0x0000, 0x0F00_0000u, "1 with an unreliable frame time")]   // FRACSEC quality 0xF: clock failure
    [InlineData((ushort)0x0000, 0x0600_0000u, "1 with an unreliable frame time")]   // 0x6: within 100 us only
    public void D108_an_invalid_or_unsynchronised_sample_is_dropped_counted_and_exits_3(ushort stat, uint quality, string reason)
    {
        string cap = Capture(Config(OldSoc), Data(OldSoc, 0, 0), Data(OldSoc, quality | 20_000, stat));
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap);
        AssertExit(3, r);
        Assert.Contains("| STN_A | 101 | 2 | 2 |", r.StdOut, StringComparison.Ordinal);   // both decoded...
        Assert.Contains(" | 1 |\n", r.StdOut.Replace("\r\n", "\n"), StringComparison.Ordinal);   // ...one dropped
        Assert.Contains("aligned instants: 1 (0 with a PMU missing)", r.StdOut, StringComparison.Ordinal);   // only the good one aligned
        Assert.Contains("quality: 1 sample(s) dropped and not used", r.StdOut, StringComparison.Ordinal);
        Assert.Contains(reason, r.StdOut, StringComparison.Ordinal);
        Assert.Contains("pmu-ingest: 1 PMU sample(s) dropped by the STAT / time-quality rule", r.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((ushort)0x00C0, 0u)]          // STAT time quality 011: within 10 us (usable)
    [InlineData((ushort)0x0800, 0u)]          // trigger detected: describes the stream, not the value's trust
    [InlineData((ushort)0x0400, 0u)]          // configuration change flag
    [InlineData((ushort)0x0000, 0x0500_0000u)]   // FRACSEC quality 0x5: within 10 us (usable)
    [InlineData((ushort)0x0000, 0x1000_0000u)]   // leap second pending: does not decide usability
    public void D108_a_good_sample_with_informational_flags_is_used(ushort stat, uint quality)
    {
        string cap = Capture(Config(OldSoc), Data(OldSoc, quality | 20_000, stat));
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap);
        AssertExit(0, r);
        Assert.Contains("quality: 0 sample(s) dropped and not used", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("aligned instants: 1 (0 with a PMU missing)", r.StdOut, StringComparison.Ordinal);
    }

    private string ChannelMap(int voltagePhasor = 0, int currentPhasor = 1, string? extra = null)
    {
        string path = Path.Combine(_dir, $"channels-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            { "schema": "gridsim-pmu-channels/1", "case": "case9", "baseMva": 100,
              "reference": { "pmu": 101, "phasor": {{voltagePhasor}}, "anchorDeg": 0 },
              "pmus": [ { "pmu": 101,
                          "voltages": [ { "phasor": {{voltagePhasor}}, "bus": 1, "baseVolts": 9500, "vmSigmaPu": 0.002, "vaSigmaDeg": 0.1 } ],
                          "currents": [ { "phasor": {{currentPhasor}}, "from": 1, "to": 4, "baseAmps": 1000, "iMagSigmaPu": 0.005, "iAngSigmaDeg": 0.2 } ] } ]{{extra}} }
            """);
        return path;
    }

    [Fact]
    public void D107_pmu_ingest_with_a_channel_map_writes_one_guarded_measurement_set_per_usable_instant()
    {
        string cap = Capture(Config(OldSoc), Data(OldSoc, 0, 0), Data(OldSoc, 20_000, 0x8000), Data(OldSoc, 40_000, 0));
        string outDir = Path.Combine(_dir, "sets");
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap, "--channels", ChannelMap(), "--measurements-dir", outDir);
        AssertExit(3, r);   // one sample dropped (STAT 0x8000)
        Assert.Contains($"measurement sets: 2 written to {outDir}", r.StdOut, StringComparison.Ordinal);

        string[] files = Directory.GetFiles(outDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, files.Length);
        Assert.Equal("pmu-20231114T2213200000000Z.json", Path.GetFileName(files[0]));
        Assert.Equal("pmu-20231114T2213200400000Z.json", Path.GetFileName(files[1]));
        MeasurementSet set = MeasurementSet.Load(files[0]);   // the guard again, on load
        Assert.Equal("case9", set.Case);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(OldSoc), set.TakenUtc);
        Assert.Equal(4, set.Measurements.Count);
        Measurement vm = set.Measurements.Single(m => m.Kind == MeasurementKind.VmPu);
        Assert.Equal(1, vm.Bus);
        Assert.Equal(9_500.0 / 9_500.0, vm.Value);   // |V| / baseVolts: the float phasor 9500 + 0j is exact
        Measurement va = set.Measurements.Single(m => m.Kind == MeasurementKind.VaDeg);
        Assert.Equal(0.0, va.Value);                  // the reference channel itself: angle - angle + anchor 0
        Measurement im = set.Measurements.Single(m => m.Kind == MeasurementKind.IMagPu);
        Assert.Equal(Math.Sqrt(1_000.0 * 1_000.0 + 100.0 * 100.0) / 1_000.0, im.Value);

        // Allowed without a map: the decode-only run writes nothing (and needs no folder).
        CliProcess.Result plain = CliProcess.RunBuilt("pmu-ingest", Capture(Config(OldSoc), Data(OldSoc, 0, 0)));
        AssertExit(0, plain);
        Assert.DoesNotContain("measurement sets:", plain.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void D107_pmu_ingest_refuses_a_channel_map_that_does_not_fit_or_is_given_alone()
    {
        string cap = Capture(Config(OldSoc), Data(OldSoc, 0, 0));
        string outDir = Path.Combine(_dir, "sets2");

        CliProcess.Result alone = CliProcess.RunBuilt("pmu-ingest", cap, "--channels", ChannelMap());
        AssertExit(2, alone);
        Assert.Contains("--channels and --measurements-dir go together", alone.StdErr, StringComparison.Ordinal);
        AssertExit(2, CliProcess.RunBuilt("pmu-ingest", cap, "--measurements-dir", outDir));

        // Phasor 5 does not exist (the PMU has 2): refused naming it, before any set is written (it used to be a raw
        // IndexOutOfRangeException inside the mapper, D-DOC-008's class).
        CliProcess.Result badIndex = CliProcess.RunBuilt("pmu-ingest", cap, "--channels", ChannelMap(voltagePhasor: 0, currentPhasor: 5), "--measurements-dir", outDir);
        AssertExit(1, badIndex);
        Assert.Contains("has 2 phasor(s); current phasor 5 does not exist", badIndex.StdErr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outDir));

        // A voltage channel on the current phasor (PHUNIT says current).
        CliProcess.Result swapped = CliProcess.RunBuilt("pmu-ingest", cap, "--channels", ChannelMap(voltagePhasor: 1, currentPhasor: 0), "--measurements-dir", outDir);
        AssertExit(1, swapped);
        Assert.Contains("phasor 1 is a current channel in the CONFIG frame (PHUNIT), but the map uses it as a voltage", swapped.StdErr, StringComparison.Ordinal);

        // A malformed map (wrong schema, missing reference) is refused at load, naming each problem.
        string bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, """{ "schema": "gridsim-pmu-channels/9", "baseMva": 100, "pmus": [ { "pmu": 101, "voltages": [ { "phasor": 0, "bus": 1, "baseVolts": -1, "vmSigmaPu": 0.002, "vaSigmaDeg": 0.1 } ] } ] }""");
        CliProcess.Result m = CliProcess.RunBuilt("pmu-ingest", cap, "--channels", bad, "--measurements-dir", outDir);
        AssertExit(1, m);
        Assert.Contains("schema is 'gridsim-pmu-channels/9'", m.StdErr, StringComparison.Ordinal);
        Assert.Contains("baseVolts is -1; it must be a finite number above 0", m.StdErr, StringComparison.Ordinal);
        Assert.Contains("reference is missing", m.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void D107_an_instant_whose_reference_channel_was_dropped_writes_no_set_and_exits_3()
    {
        // Single PMU: when its sample is dropped there is no usable reference at that instant, so the PDC has no sample
        // for it at all; when it reports with an unusable FRACSEC time every block of that frame is dropped.
        string cap = Capture(Config(OldSoc), Data(OldSoc, 0, 0), Data(OldSoc, 0x0F00_0000u | 20_000, 0));
        string outDir = Path.Combine(_dir, "sets3");
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap, "--channels", ChannelMap(), "--measurements-dir", outDir);
        AssertExit(3, r);
        Assert.Single(Directory.GetFiles(outDir, "*.json"));
    }
}
