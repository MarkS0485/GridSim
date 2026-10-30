using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using GridSim.Core.Diagnostics;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF6 (N358): the opt-in quarantine path for a live C37.118 stream of present-time data. Without
/// <c>--live-quarantine</c> a present-time live run is REJECTED whole, exit 2 (the N340 default, unchanged). With it,
/// the present-time DATA frames are written byte for byte to a quarantine capture with a sidecar (tag
/// CuratedPlaceholder, never Recorded, non-evidential) and are not ingested; a quarantine capture is refused by
/// <c>pmu-ingest &lt;file&gt;</c> whatever its age. A simulated PMU runs on the loopback interface only.
/// </summary>
public sealed class Cf6LivePmuQuarantineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-cf6-quarantine-" + Guid.NewGuid().ToString("N"));

    public Cf6LivePmuQuarantineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly Doc15Frames.Pmu Pmu7 = new(7, "STN_A", new[] { Doc15Frames.Voltage, Doc15Frames.Current });

    // Present time: the second of the run (the horizon is 169 h back, so these frames are fresh by a wide margin).
    private static readonly uint NowSoc = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private const uint OldSoc = 1_700_000_000;   // 2023-11-14, well before the horizon

    private static byte[] Config(uint soc) => Doc15Frames.Config(1_000_000, soc, Pmu7);
    private static byte[] Data(uint soc, int k) => Doc15Frames.Data(soc, (uint)(20_000 * k), new[] { (1.0f, 0.01f * k), (0.5f, -0.2f) });

    // A simulated PMU as in Cf5LiveC37118Tests: accept one client, read the two COMMAND frames, write the bytes, then
    // read the stop command best effort.
    private static int StartTcpPmu(byte[] all)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() =>
        {
            try
            {
                using TcpClient c = listener.AcceptTcpClient();
                using NetworkStream s = c.GetStream();
                s.ReadTimeout = 20_000;
                for (int k = 0; k < 2; k++) ReadExactly(s, C37118Command.FrameSize);
                s.Write(all);
                s.Flush();
                try { ReadExactly(s, C37118Command.FrameSize); } catch (IOException) { }
            }
            finally { listener.Stop(); }
        });
        return port;
    }

    private static byte[] ReadExactly(NetworkStream s, int n)
    {
        var b = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = s.Read(b, got, n - got);
            if (r == 0) throw new IOException("closed");
            got += r;
        }
        return b;
    }

    // ------------------------------------------------------------------ Core

    [Fact]
    public void The_horizon_predicate_agrees_with_the_guard_on_both_sides_of_the_horizon()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        DateTime horizon = ForwardInferenceGuard.Horizon(now);
        foreach (DateTime stamp in new[] { horizon.AddTicks(-1), horizon, horizon.AddTicks(1), now, now.AddDays(1), horizon.AddDays(-30) })
        {
            bool guardThrows;
            try { ForwardInferenceGuard.RejectFuture(stamp, now); guardThrows = false; }
            catch (ForwardInferenceException) { guardThrows = true; }
            Assert.Equal(guardThrows, PmuLiveQuarantine.IsNewerThanHorizon(stamp, now));
        }
    }

    [Fact]
    public void The_tag_is_the_lowest_input_rank_and_never_recorded()
    {
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, PmuLiveQuarantine.Tag);
        Assert.NotEqual(ProvenanceTag.Recorded, PmuLiveQuarantine.Tag);
        Assert.Equal(1, ProvenanceTrust.Rank(PmuLiveQuarantine.Tag));
        // Nothing computed from a quarantined frame can claim more.
        Assert.Throws<InvalidDataException>(() => ProvenanceTrust.RequireNotRaised(new[] { PmuLiveQuarantine.Tag }, ProvenanceTag.Recorded, "x"));
    }

    [Fact]
    public void Write_keeps_the_frames_byte_for_byte_and_the_sidecar_says_what_they_are()
    {
        byte[][] frames = { Config(NowSoc), Data(NowSoc, 0), Data(NowSoc, 1) };
        var received = new DateTime(2026, 10, 11, 9, 30, 15, 250, DateTimeKind.Utc);
        DateTime horizon = ForwardInferenceGuard.Horizon(received);
        (string cap, string side) = PmuLiveQuarantine.Write(_dir, frames, "tcp://127.0.0.1:4712", received, horizon, 2, received.AddSeconds(-2), received.AddSeconds(-1));

        Assert.Equal(Path.Combine(_dir, "pmu-live-20261011T093015250Z.quarantine.c37"), cap);
        Assert.Equal(cap + ".quarantine.json", side);
        byte[] expected = frames.SelectMany(f => f).ToArray();
        Assert.Equal(expected, File.ReadAllBytes(cap));

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(side));
        JsonElement r = doc.RootElement;
        Assert.Equal("gridsim-pmu-quarantine/1", r.GetProperty("schema").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), r.GetProperty("sha256").GetString());
        Assert.Equal(expected.Length, r.GetProperty("bytes").GetInt64());
        Assert.Equal(3, r.GetProperty("frames").GetInt32());
        Assert.Equal(2, r.GetProperty("freshDataFrames").GetInt32());
        Assert.False(r.GetProperty("evidential").GetBoolean());
        Assert.False(r.GetProperty("recorded").GetBoolean());
        Assert.Equal("telemetry", r.GetProperty("sourceClass").GetString());
        Assert.Equal("CuratedPlaceholder", r.GetProperty("provenance").GetProperty("tag").GetString());
        // The trust machinery reads the sidecar's tag like any other provenance object.
        Assert.Equal(new[] { "CuratedPlaceholder" }, ProvenanceTrust.TagsIn(r).Select(t => t.Tag).ToArray());
        Assert.Empty(ProvenanceTrust.OutputTagsIn(r));
    }

    [Fact]
    public void Write_never_overwrites_a_clash_gets_a_dup_name()
    {
        byte[][] frames = { Config(NowSoc) };
        var received = new DateTime(2026, 10, 11, 9, 30, 15, DateTimeKind.Utc);
        (string first, _) = PmuLiveQuarantine.Write(_dir, frames, "udp://:4713", received, received, 0, null, null);
        (string second, _) = PmuLiveQuarantine.Write(_dir, frames, "udp://:4713", received, received, 0, null, null);
        Assert.NotEqual(first, second);
        Assert.EndsWith("-dup1.quarantine.c37", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_refuses_bad_frames_an_empty_list_and_the_evidence_lake()
    {
        var t = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        Assert.Throws<ArgumentException>(() => PmuLiveQuarantine.Write(_dir, Array.Empty<byte[]>(), "x", t, t, 0, null, null));
        byte[] notAFrame = new byte[20];
        Assert.Throws<ArgumentException>(() => PmuLiveQuarantine.Write(_dir, new[] { notAFrame }, "x", t, t, 0, null, null));
        byte[] truncated = Config(NowSoc).Take(30).ToArray();
        Assert.Throws<ArgumentException>(() => PmuLiveQuarantine.Write(_dir, new[] { truncated }, "x", t, t, 0, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => PmuLiveQuarantine.Write(_dir, new[] { Config(NowSoc) }, "x", t, t, 2, null, null));

        string lake = Path.Combine(_dir, "lake");
        Directory.CreateDirectory(Path.Combine(lake, "deep"));
        File.WriteAllText(Path.Combine(lake, PmuLiveQuarantine.EvidenceLakeMarker), "{}");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            PmuLiveQuarantine.Write(Path.Combine(lake, "deep", "q"), new[] { Config(NowSoc) }, "x", t, t, 0, null, null));
        Assert.Contains("evidence lake", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(lake, "deep", "q")));
    }

    [Fact]
    public void A_quarantine_capture_is_refused_as_a_recorded_capture_by_name_or_by_sidecar()
    {
        string named = Path.Combine(_dir, "x.quarantine.c37");
        Assert.Throws<InvalidDataException>(() => PmuLiveQuarantine.RefuseQuarantinedCapture(named));
        string renamed = Path.Combine(_dir, "renamed.bin");
        File.WriteAllBytes(renamed, Config(OldSoc));
        PmuLiveQuarantine.RefuseQuarantinedCapture(renamed);   // a plain capture passes
        File.WriteAllText(renamed + PmuLiveQuarantine.SidecarSuffix, "{}");
        Assert.Throws<InvalidDataException>(() => PmuLiveQuarantine.RefuseQuarantinedCapture(renamed));
    }

    // ------------------------------------------------------------------ the CLI

    [Fact]
    public void Default_unchanged_a_present_time_live_stream_is_rejected_whole()
    {
        byte[] all = Config(NowSoc).Concat(Data(NowSoc, 0)).Concat(Data(NowSoc, 1)).ToArray();
        int port = StartTcpPmu(all);
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", "--live", $"tcp://127.0.0.1:{port}", "--live-frames", "2", "--live-seconds", "20", "--live-idcode", "7");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("REJECTED (forward inference)", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_quarantine_present_time_frames_are_written_there_and_not_ingested()
    {
        byte[] config = Config(NowSoc), d0 = Data(NowSoc, 0), d1 = Data(NowSoc, 1);
        byte[] all = config.Concat(d0).Concat(d1).ToArray();
        int port = StartTcpPmu(all);
        string q = Path.Combine(_dir, "q");
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", "--live", $"tcp://127.0.0.1:{port}", "--live-frames", "2", "--live-seconds", "20",
            "--live-idcode", "7", "--live-quarantine", q);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("quarantine: 2 DATA frame(s) newer than the horizon held back, not ingested: 3 frame(s) written to", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("provenance CuratedPlaceholder, non-evidential, never Recorded", r.StdOut, StringComparison.Ordinal);
        // Nothing was aligned: the held-back frames reached no later step.
        Assert.Contains("aligned instants: 0 (0 with a PMU missing)", r.StdOut, StringComparison.Ordinal);

        string[] caps = Directory.GetFiles(q, "*" + PmuLiveQuarantine.CaptureSuffix);
        Assert.Single(caps);
        Assert.Equal(all, File.ReadAllBytes(caps[0]));
        Assert.True(File.Exists(caps[0] + PmuLiveQuarantine.SidecarSuffix));

        // The quarantine capture is refused as a recorded capture (exit 1), whatever its age.
        CliProcess.Result again = CliProcess.RunBuilt("pmu-ingest", caps[0]);
        Assert.True(again.ExitCode == 1, again.ToString());
        Assert.Contains("quarantined live capture", again.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_quarantine_old_frames_still_run_the_recorded_pipeline_and_nothing_is_written()
    {
        byte[] all = Config(OldSoc).Concat(Data(OldSoc + 1, 0)).Concat(Data(OldSoc + 1, 1)).ToArray();
        int port = StartTcpPmu(all);
        string q = Path.Combine(_dir, "q-old");
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", "--live", $"tcp://127.0.0.1:{port}", "--live-frames", "2", "--live-seconds", "20",
            "--live-idcode", "7", "--live-quarantine", q);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("quarantine: no DATA frame newer than the horizon; nothing written to the quarantine", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("aligned instants: 2", r.StdOut, StringComparison.Ordinal);
        Assert.False(Directory.Exists(q) && Directory.GetFiles(q).Length > 0);
    }

    [Fact]
    public void The_quarantine_flag_qualifies_live_only_and_refuses_the_lake()
    {
        CliProcess.Result alone = CliProcess.RunBuilt("pmu-ingest", "capture.bin", "--live-quarantine", _dir);
        Assert.True(alone.ExitCode == 2, alone.ToString());

        string lake = Path.Combine(_dir, "lake");
        Directory.CreateDirectory(lake);
        File.WriteAllText(Path.Combine(lake, PmuLiveQuarantine.EvidenceLakeMarker), "{}");
        // Refused before any connection is attempted (the port is not listened on).
        CliProcess.Result inLake = CliProcess.RunBuilt("pmu-ingest", "--live", "tcp://127.0.0.1:1", "--live-frames", "1", "--live-quarantine", Path.Combine(lake, "q"));
        Assert.True(inLake.ExitCode == 1, inLake.ToString());
        Assert.Contains("evidence lake", inLake.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void The_flag_has_its_chapter_19_row()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string ch19 = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "manual", "0019 CLI reference.md"));
        Assert.Contains("| `--live-quarantine` | off |", ch19, StringComparison.Ordinal);
    }
}
