using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N230 (October 2026, Mark's ruling: loopback test). PROCESS-LEVEL tests of the real <c>interpose</c> verb. Until now
/// the verb had no process test: its flush logic was unit-tested in-process and its CLI wiring by source checks, because
/// a run needs a live IEC 60870-5-104 or Modbus TCP device on the other end of the socket.
///
/// Each test starts the built CLI (<see cref="CliProcess.RunBuilt"/>, as every other CLI process test does) listening on
/// 127.0.0.1 at an OS-chosen free port, and plays the field device with the test-only <see cref="LoopbackRtuClient"/>
/// (the pattern of the CF5 / N340 live PMU tests, with the roles swapped: here GridSim listens and the test connects).
/// For IEC 104 the client plays a REAL outstation (main's ruling on question 3): it sends nothing until the interposer,
/// the controlling station, sends STARTDT act, then confirms it, and it records every APDU the interposer sends.
/// The assertions are on what the verb shows the operator: its stop summary counts, the window files it writes, the
/// quarantine store, the audit log, its exit code, and the link-layer APDUs it sent.
///
/// Hard timeouts, three deep, so a hang fails rather than blocks: every run passes <c>--live-seconds</c> (the interposer
/// stops itself), the client gives up at <see cref="LoopbackRtuClient.Deadline"/>, and <see cref="CliProcess"/> kills
/// the process at its own 120 s limit.
///
/// The device time tags are in November 2023, well before the 169 h horizon, so IEC 104 windows are recorded-capture
/// windows the verb writes to <c>--measurements-dir</c>. Modbus carries no time tag, so its windows are live (newer than
/// the horizon) and are held, as D-DOC-018 requires.
///
/// Timing assumption: every IEC 104 run here ends well inside t2 = 10 s after its first unacknowledged I-frame and
/// t3 = 20 s of idle, so the only acknowledgements seen are the w = 8 ones and no TESTFR act is sent. The t2 and t3
/// timers are tested with a fake clock in Iec104ControllingStationTests.
/// </summary>
public sealed class InterposeLoopbackProcessTests : IDisposable
{
    // The device clock of the IEC 104 test frames: 2023-11-14 22:13:20.000 UTC (historical).
    private static readonly DateTime T0 = new(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc);

    // A large window, so a run writes exactly one window: the tail flush when the stream stops. (A 1 s window would
    // split the frames across windows depending on how fast the machine is, and the counts would not be fixed.)
    private const string OneWindowMs = "600000";

    // The interposer's own stop: long enough for a slow CI start, short enough that a lost frame fails quickly.
    private const string BackstopSeconds = "30";

    // The APDUs the interposer sends as controlling station (octet for octet).
    private static readonly byte[] StartDtAct = { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 };
    private static readonly byte[] TestFrCon = { 0x68, 0x04, 0x83, 0x00, 0x00, 0x00 };
    private static byte[] SAck(int nr) => new byte[] { 0x68, 0x04, 0x01, 0x00, (byte)((nr << 1) & 0xFE), (byte)((nr >> 7) & 0xFF) };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-n230-interpose-" + Guid.NewGuid().ToString("N"));

    public InterposeLoopbackProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    // ================================================================================================================
    // Helpers
    // ================================================================================================================

    /// <summary>The IEC 104 crosswalk: one binding per analogue information object, each to a different measurement
    /// kind, so every kind the mapper handles for a bus or a branch is seen once. Status, counter, step position and
    /// bitstring addresses are left unbound (decoded and counted, never measurements).</summary>
    private string Iec104Crosswalk()
    {
        string path = Path.Combine(_dir, "iec104-crosswalk.json");
        new RtuPointMap
        {
            Case = "case14",
            Bindings = new List<RtuPointBinding>
            {
                new() { Address = "IOA:1001", Kind = MeasurementKind.VmPu, Bus = 4 },
                new() { Address = "IOA:1002", Kind = MeasurementKind.PInjMw, Bus = 5, Scale = 100.0 },
                new() { Address = "IOA:1003", Kind = MeasurementKind.PFlowMw, From = 4, To = 5, Circuit = "1" },
                new() { Address = "IOA:1004", Kind = MeasurementKind.QInjMvar, Bus = 4 },
                new() { Address = "IOA:1005", Kind = MeasurementKind.VaDeg, Bus = 5 },
                new() { Address = "IOA:1006", Kind = MeasurementKind.QFlowMvar, From = 4, To = 5, Circuit = "1", End = BranchEnd.To },
            },
        }.Save(path);
        return path;
    }

    /// <summary>The Modbus crosswalk: registers 100 and 101 of unit 1, function 3, by absolute address (the interposer
    /// sees the poll request, so it can pair the response with it).</summary>
    private string ModbusCrosswalk()
    {
        string path = Path.Combine(_dir, "modbus-crosswalk.json");
        new RtuPointMap
        {
            Case = "case14",
            Bindings = new List<RtuPointBinding>
            {
                new() { Address = "MB:U1:3:@100", Kind = MeasurementKind.PInjMw, Bus = 4, Scale = 0.1 },
                new() { Address = "MB:U1:3:@101", Kind = MeasurementKind.PFlowMw, From = 4, To = 5, Circuit = "1", DataType = RtuRegisterType.S16 },
            },
        }.Save(path);
        return path;
    }

    // The common interpose command line: protocol, loopback endpoint, crosswalk, one window, then the test's own flags.
    // --live-seconds BackstopSeconds is added unless the test sets its own.
    private static string[] InterposeArgs(string protocol, int port, string crosswalk, params string[] extra)
    {
        var args = new List<string>
        {
            "interpose", "--protocol", protocol, "--listen", string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{port}"),
            "--crosswalk", crosswalk, "--window-ms", OneWindowMs,
        };
        args.AddRange(extra);
        if (!extra.Contains("--live-seconds")) args.AddRange(new[] { "--live-seconds", BackstopSeconds });
        return args.ToArray();
    }

    /// <summary>
    /// Start the loopback client, run the interposer to its end, then collect what the client saw. The client's
    /// connect wait is cancelled once the process has ended, so a run that never listened does not hold the test for
    /// the client's whole deadline. A client failure is reported together with the process output.
    /// </summary>
    private static async Task<(CliProcess.Result Run, IReadOnlyList<LoopbackSessionResult> Sessions)> RunWithClientAsync(
        int port, IReadOnlyList<LoopbackSession> sessions, string[] args)
    {
        using var stopConnecting = new CancellationTokenSource();
        Task<IReadOnlyList<LoopbackSessionResult>> client = LoopbackRtuClient.RunAsync(port, sessions, stopConnecting.Token);
        CliProcess.Result run = await Task.Run(() => CliProcess.RunBuilt(args));
        await stopConnecting.CancelAsync();
        try
        {
            IReadOnlyList<LoopbackSessionResult> seen = await client;
            return (run, seen);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or SocketException or InvalidDataException)
        {
            throw new InvalidOperationException("the loopback client failed: " + ex.Message + "\n" + run, ex);
        }
    }

    /// <summary>The interposer's stop line, field by field, exactly as RunInterpose prints it (discarded-bytes last,
    /// N230).</summary>
    private static string StopLine(
        long accepted, long rejected, long decodeErrors, long points, long stepPositions, long bitstrings,
        long windowsWritten, long clientResets, string unsupported, long heldFresh, long quarantined,
        long discardedBytes = 0, long mapErrors = 0)
        => string.Join(", ",
            string.Create(CultureInfo.InvariantCulture, $"stopped: accepted {accepted}, rejected {rejected}, decode-errors {decodeErrors}, map-errors {mapErrors}"),
            string.Create(CultureInfo.InvariantCulture, $"points {points}, step-positions {stepPositions}, bitstrings {bitstrings}"),
            string.Create(CultureInfo.InvariantCulture, $"windows-written {windowsWritten}, duplicate-names 0, client-resets {clientResets}"),
            string.Create(CultureInfo.InvariantCulture, $"unsupported-type-ids {unsupported}, held-fresh {heldFresh}, quarantined {quarantined}"),
            string.Create(CultureInfo.InvariantCulture, $"discarded-bytes {discardedBytes}"));

    private static Measurement Only(MeasurementSet set, MeasurementKind kind)
        => Assert.Single(set.Measurements, m => m.Kind == kind);

    // The APDUs the interposer sent on one connection, in order.
    private static List<byte[]> Sent(LoopbackSessionResult session) => Iec104TestFrames.SplitApdus(session.Received);

    // A time-tagged short-float I-frame (M_ME_TF_1) for IOA 1001 at T0 + k ms, with send sequence ns.
    private static byte[] TimedFloat(int ns, int ioa, float value, int ms)
        => Iec104TestFrames.IFrame(ns, Iec104TestFrames.Asdu(36, ioa, Iec104TestFrames.ShortFloat(value), T0.AddMilliseconds(ms)));

    // ================================================================================================================
    // IEC 60870-5-104
    // ================================================================================================================

    [Fact]
    public async Task Iec104_session_with_every_decoded_type_writes_one_recorded_window_and_counts_everything()
    {
        string crosswalk = Iec104Crosswalk();
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        string log = Path.Combine(_dir, "audit.jsonl");
        int port = LoopbackRtuClient.FreeLoopbackPort();

        // A real outstation's session: after STARTDT act / con (done by the client), a TESTFR act, then spontaneous
        // monitor ASDUs of every type the decoder supports (one information object each) and one ASDU of a type it
        // does not decode (45, C_SC_NA_1 single command).
        int ns = 0;
        byte[] I(byte[] asdu) => Iec104TestFrames.IFrame(ns++, asdu);
        var frames = new List<byte[]>
        {
            Iec104TestFrames.TestFrAct(),
            // The six analogue types, all bound in the crosswalk: three time-tagged, three not.
            I(Iec104TestFrames.Asdu(36, 1001, Iec104TestFrames.ShortFloat(1.03f), T0)),                     // M_ME_TF_1
            I(Iec104TestFrames.Asdu(34, 1002, Iec104TestFrames.Int16(16384), T0.AddMilliseconds(100))),     // M_ME_TD_1 0.5
            I(Iec104TestFrames.Asdu(35, 1003, Iec104TestFrames.Int16(-120), T0.AddMilliseconds(200))),      // M_ME_TE_1
            I(Iec104TestFrames.Asdu(13, 1004, Iec104TestFrames.ShortFloat(0.25f))),                         // M_ME_NC_1
            I(Iec104TestFrames.Asdu(9, 1005, Iec104TestFrames.Int16(-16384))),                              // M_ME_NA_1 -0.5
            I(Iec104TestFrames.Asdu(11, 1006, Iec104TestFrames.Int16(42))),                                 // M_ME_NB_1
            // Status and counters: decoded points, not bound.
            I(Iec104TestFrames.Asdu(1, 2001, new byte[] { 0x01 })),                                         // M_SP_NA_1 on
            I(Iec104TestFrames.Asdu(3, 2002, new byte[] { 0x02 })),                                         // M_DP_NA_1 on
            I(Iec104TestFrames.Asdu(30, 2003, new byte[] { 0x00 }, T0.AddMilliseconds(300))),               // M_SP_TB_1 off
            I(Iec104TestFrames.Asdu(31, 2004, new byte[] { 0x01 }, T0.AddMilliseconds(400))),               // M_DP_TB_1 off
            I(Iec104TestFrames.Asdu(15, 2005, Iec104TestFrames.Counter(7))),                                // M_IT_NA_1
            I(Iec104TestFrames.Asdu(37, 2006, Iec104TestFrames.Counter(8), T0.AddMilliseconds(500))),       // M_IT_TB_1
            // Step positions and bitstrings (N337): decoded and counted on the stop line, never measurements.
            I(Iec104TestFrames.Asdu(5, 2007, Iec104TestFrames.StepPosition(3, transient: false))),          // M_ST_NA_1
            I(Iec104TestFrames.Asdu(32, 2008, Iec104TestFrames.StepPosition(-2, transient: true), T0.AddMilliseconds(600))), // M_ST_TB_1
            I(Iec104TestFrames.Asdu(7, 2009, Iec104TestFrames.Bitstring(0xA5A50F0Fu))),                     // M_BO_NA_1
            I(Iec104TestFrames.Asdu(33, 2010, Iec104TestFrames.Bitstring(1u), T0.AddMilliseconds(700))),    // M_BO_TB_1
            // Not decoded: counted by type id (R089).
            I(Iec104TestFrames.Asdu(45, 3001, new byte[] { 0x01 }, cause: 6)),                              // C_SC_NA_1 act
        };
        Assert.Equal(18, frames.Count);   // + the client's STARTDT con = 19, the --ticks below: the run ends on the last frame

        // Fragmented: the whole session is cut inside headers and bodies, so the transport must reassemble.
        (CliProcess.Result r, IReadOnlyList<LoopbackSessionResult> seen) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(frames, Fragment: true, Iec104Outstation: true) },
            new[] { "--audit-log", log }.Concat(InterposeArgs("iec104", port, crosswalk, "--measurements-dir", windows, "--ticks", "19")).ToArray());

        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"interpose IEC 60870-5-104: listening 127.0.0.1:{port}, case 'case14', 6 binding(s)"), r.StdOut, StringComparison.Ordinal);
        Assert.Contains("headless: stops after 19 frame(s).", r.StdOut, StringComparison.Ordinal);
        // 16 objects decoded (the six analogues, four status, two counters, two step positions, two bitstrings); the U
        // frames and the command ASDU decode to none. The latest device time tag (T0 + 700 ms) dates the window.
        Assert.Contains(StopLine(accepted: 19, rejected: 0, decodeErrors: 0, points: 16, stepPositions: 2, bitstrings: 2,
            windowsWritten: 1, clientResets: 0, unsupported: "45x1", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        Assert.Contains("  window 2023-11-14T22:13:20.7000000Z: 6 measurement(s) -> 20231114T221320700Z-000000.json", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("error:", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("warning:", r.StdErr, StringComparison.OrdinalIgnoreCase);

        // The controlling station's side of the link: STARTDT act (confirmed by the client before it sent anything),
        // TESTFR con for the outstation's TESTFR act, and an S-frame after the 8th and the 16th I-frame (w = 8). The
        // 17th I-frame is not yet acknowledged when the run stops (t2 has not run).
        LoopbackSessionResult session = Assert.Single(seen);
        Assert.True(session.Connected);
        List<byte[]> sent = Sent(session);
        Assert.Equal(4, sent.Count);
        Assert.Equal(StartDtAct, sent[0]);
        Assert.Equal(TestFrCon, sent[1]);
        Assert.Equal(SAck(8), sent[2]);
        Assert.Equal(SAck(16), sent[3]);

        // The window file: one recorded window, every bound analogue mapped exactly (all values are exact in binary).
        string file = Assert.Single(Directory.GetFiles(windows));
        Assert.Equal("20231114T221320700Z-000000.json", Path.GetFileName(file));
        MeasurementSet set = MeasurementSet.Load(file);
        Assert.Equal("case14", set.Case);
        Assert.Equal(T0.AddMilliseconds(700), set.TakenUtc!.Value);
        Assert.Equal("mapped from RTU interposer", set.Note);
        Assert.Equal(6, set.Measurements.Count);
        Measurement vm = Only(set, MeasurementKind.VmPu);
        Assert.Equal((double)1.03f, vm.Value);     // the single-precision value widened exactly; base 1 kV, scale 1
        Assert.Equal(4, vm.Bus);
        Measurement pinj = Only(set, MeasurementKind.PInjMw);
        Assert.Equal(50.0, pinj.Value);            // 16384 / 32768 = 0.5, scale 100
        Assert.Equal(5, pinj.Bus);
        Measurement pflow = Only(set, MeasurementKind.PFlowMw);
        Assert.Equal(-120.0, pflow.Value);
        Assert.Equal((4, 5, BranchEnd.From, "1"), (pflow.From, pflow.To, pflow.End, pflow.Circuit));
        Measurement qinj = Only(set, MeasurementKind.QInjMvar);
        Assert.Equal(0.25, qinj.Value);
        Assert.Equal(4, qinj.Bus);
        Measurement va = Only(set, MeasurementKind.VaDeg);
        Assert.Equal(-0.5, va.Value);              // -16384 / 32768, datum 0
        Assert.Equal(5, va.Bus);
        Measurement qflow = Only(set, MeasurementKind.QFlowMvar);
        Assert.Equal(42.0, qflow.Value);
        Assert.Equal((4, 5, BranchEnd.To, "1"), (qflow.From, qflow.To, qflow.End, qflow.Circuit));
        Assert.All(set.Measurements, m => Assert.Equal(0.01, m.Sigma));   // good quality: the binding's sigma unchanged
        Assert.All(set.Measurements, m => Assert.Equal("rtu", m.Source));

        // The audit log: the start entry when it began listening, and the stop entry with the same counts.
        IReadOnlyList<AuditLogEntry> entries = AuditLogFile.Load(log, AuditAnchorFile.ReadIfExists(log + ".anchor"));
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(ProvenanceTag.Recorded, e.Provenance));
        Assert.Contains(" action=interpose ", entries[0].Summary, StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"outcome=started protocol=iec104 listen=127.0.0.1:{port} monitor=False"), entries[0].Summary, StringComparison.Ordinal);
        Assert.Contains(" outcome=exit=0 stopped accepted=19 rejected=0 decode-errors=0 map-errors=0 points=16 step-positions=2 " +
            "bitstrings=2 windows-written=1 duplicate-names=0 client-resets=0 unsupported-type-ids=45x1 held-fresh=0 quarantined=0 " +
            "discarded-bytes=0",
            entries[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Iec104_handshake_the_interposer_sends_startdt_act_first_and_nothing_else_for_one_i_frame()
    {
        string crosswalk = Iec104Crosswalk();
        int port = LoopbackRtuClient.FreeLoopbackPort();

        // The outstation sends nothing until STARTDT act arrives (the client fails the run otherwise), confirms with
        // STARTDT con, then sends one I-frame. One unacknowledged I-frame is below w, and t2 does not run out.
        (CliProcess.Result r, IReadOnlyList<LoopbackSessionResult> seen) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(new[] { TimedFloat(0, 1001, 1.03f, 0) }, Iec104Outstation: true) },
            InterposeArgs("iec104", port, crosswalk, "--ticks", "2"));

        Assert.True(r.ExitCode == 0, r.ToString());
        // windows-written 1 with no sink: the recorded window is still formed and counted (decode only, nothing saved).
        Assert.Contains(StopLine(accepted: 2, rejected: 0, decodeErrors: 0, points: 1, stepPositions: 0, bitstrings: 0,
            windowsWritten: 1, clientResets: 0, unsupported: "none", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        byte[] only = Assert.Single(Sent(Assert.Single(seen)));
        Assert.Equal(StartDtAct, only);
    }

    [Fact]
    public async Task Iec104_testfr_act_from_the_outstation_is_answered_with_testfr_con_every_time()
    {
        string crosswalk = Iec104Crosswalk();
        int port = LoopbackRtuClient.FreeLoopbackPort();

        (CliProcess.Result r, IReadOnlyList<LoopbackSessionResult> seen) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(new[] { Iec104TestFrames.TestFrAct(), Iec104TestFrames.TestFrAct() }, Iec104Outstation: true) },
            InterposeArgs("iec104", port, crosswalk, "--ticks", "3"));

        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains(StopLine(accepted: 3, rejected: 0, decodeErrors: 0, points: 0, stepPositions: 0, bitstrings: 0,
            windowsWritten: 0, clientResets: 0, unsupported: "none", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        List<byte[]> sent = Sent(Assert.Single(seen));
        Assert.Equal(3, sent.Count);
        Assert.Equal(StartDtAct, sent[0]);
        Assert.Equal(TestFrCon, sent[1]);
        Assert.Equal(TestFrCon, sent[2]);
    }

    [Fact]
    public async Task Iec104_eight_i_frames_are_acknowledged_with_one_s_frame_carrying_nr_8()
    {
        string crosswalk = Iec104Crosswalk();
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        int port = LoopbackRtuClient.FreeLoopbackPort();

        // Nine I-frames: the 8th triggers the acknowledgement (w = 8, N(R) = 8); the 9th is outstanding at the stop.
        var frames = Enumerable.Range(0, 9).Select(k => TimedFloat(k, 1001, 1.0f + k, k * 10)).ToList();
        (CliProcess.Result r, IReadOnlyList<LoopbackSessionResult> seen) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(frames, Iec104Outstation: true) },
            InterposeArgs("iec104", port, crosswalk, "--measurements-dir", windows, "--ticks", "10"));

        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains(StopLine(accepted: 10, rejected: 0, decodeErrors: 0, points: 9, stepPositions: 0, bitstrings: 0,
            windowsWritten: 1, clientResets: 0, unsupported: "none", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        List<byte[]> sent = Sent(Assert.Single(seen));
        Assert.Equal(2, sent.Count);
        Assert.Equal(StartDtAct, sent[0]);
        Assert.Equal(new byte[] { 0x68, 0x04, 0x01, 0x00, 0x10, 0x00 }, sent[1]);   // S-frame, N(R) = 8 (8 << 1 = 0x10)

        // The data path is unchanged: the window keeps the latest sample of IOA 1001 (the 9th, 9.0 at T0 + 80 ms).
        MeasurementSet set = MeasurementSet.Load(Assert.Single(Directory.GetFiles(windows)));
        Assert.Equal(9.0, Assert.Single(set.Measurements).Value);
        Assert.Equal(T0.AddMilliseconds(80), set.TakenUtc!.Value);
    }

    [Fact]
    public async Task Iec104_malformed_frames_warn_count_and_exit_1()
    {
        string crosswalk = Iec104Crosswalk();
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        int port = LoopbackRtuClient.FreeLoopbackPort();

        (CliProcess.Result r, IReadOnlyList<LoopbackSessionResult> seen) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(MalformedThenGood(), Iec104Outstation: true) },
            InterposeArgs("iec104", port, crosswalk, "--measurements-dir", windows, "--ticks", "4"));

        // Main's ruling (question 1): the first malformed frame is named on stderr when it arrives, every one is counted
        // on the stop line, and the run exits 1 at its end (the summary and the good window are still produced).
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains(StopLine(accepted: 3, rejected: 1, decodeErrors: 1, points: 1, stepPositions: 0, bitstrings: 0,
            windowsWritten: 1, clientResets: 0, unsupported: "none", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        Assert.Contains("WARNING: interpose: malformed iec104 frame (rejected by frame correction, 4 octet(s)) at frame 2; later malformed frames are counted, not named.", r.StdErr, StringComparison.Ordinal);
        Assert.Single(r.StdErr.Split('\n'), l => l.StartsWith("WARNING: interpose: malformed", StringComparison.Ordinal));   // named once
        Assert.Contains("error: interpose: 2 malformed frame(s) (1 rejected, 1 undecodable); pass --tolerate-malformed to accept a noisy line.", r.StdErr, StringComparison.Ordinal);
        Assert.True(Assert.Single(seen).Connected);

        // Only the good frame's value reached the window: nothing of the cut ASDU (also IOA 1001) leaked into it.
        MeasurementSet set = MeasurementSet.Load(Assert.Single(Directory.GetFiles(windows)));
        Measurement vm = Assert.Single(set.Measurements);
        Assert.Equal(MeasurementKind.VmPu, vm.Kind);
        Assert.Equal((double)1.03f, vm.Value);
        Assert.Equal(T0, set.TakenUtc!.Value);
    }

    [Fact]
    public async Task Iec104_malformed_frames_with_tolerate_malformed_still_warn_but_exit_0()
    {
        string crosswalk = Iec104Crosswalk();
        int port = LoopbackRtuClient.FreeLoopbackPort();

        (CliProcess.Result r, _) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(MalformedThenGood(), Iec104Outstation: true) },
            InterposeArgs("iec104", port, crosswalk, "--ticks", "4", "--tolerate-malformed"));

        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains(StopLine(accepted: 3, rejected: 1, decodeErrors: 1, points: 1, stepPositions: 0, bitstrings: 0,
            windowsWritten: 1, clientResets: 0, unsupported: "none", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        Assert.Contains("WARNING: interpose: malformed iec104 frame (rejected by frame correction, 4 octet(s)) at frame 2;", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("WARNING: interpose: 2 malformed frame(s) (1 rejected, 1 undecodable) tolerated (--tolerate-malformed).", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("error:", r.StdErr, StringComparison.Ordinal);
    }

    // After the handshake: a mis-framed APDU (start 0x68, length 2: shorter than the four control octets, so frame
    // correction rejects the 4 octets the transport hands up), a framed I-frame whose ASDU is cut inside its one object
    // (IOA 1001 and two of the five octets of a short float: a decode error), then a good frame. With the client's
    // STARTDT con that is 4 frames.
    private static List<byte[]> MalformedThenGood() => new()
    {
        new byte[] { 0x68, 0x02, 0x07, 0x00 },
        Iec104TestFrames.IFrame(0, new byte[] { 13, 0x01, 0x03, 0x00, 0x01, 0x00, 0xE9, 0x03, 0x00, 0x00, 0x00 }),
        TimedFloat(1, 1001, 1.03f, 0),
    };

    [Fact]
    public async Task Iec104_junk_and_a_half_frame_left_at_disconnect_are_counted_as_discarded_bytes()
    {
        string crosswalk = Iec104Crosswalk();
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        int port = LoopbackRtuClient.FreeLoopbackPort();

        // First client: (handshake) three junk octets with no 0x68 among them, one whole frame, then the first 8 octets
        // of a 27-octet frame, then it goes away.
        byte[] cut = TimedFloat(1, 1002, 9.0f, 0);
        Assert.Equal(27, cut.Length);   // 6 APCI + 6 DUI + 3 IOA + 5 element + 7 CP56Time2a, so 8 octets is mid-frame
        var first = new LoopbackSession(new List<byte[]>
        {
            new byte[] { 0x01, 0x02, 0x03 },
            TimedFloat(0, 1001, 1.03f, 0),
            cut.AsSpan(0, 8).ToArray(),
        }, CloseAfterSending: true, Iec104Outstation: true);
        // Second client: (handshake) one whole frame (QInjMvar at bus 4), one second later on the device clock.
        var second = new LoopbackSession(new List<byte[]>
        {
            Iec104TestFrames.IFrame(0, Iec104TestFrames.Asdu(36, 1004, Iec104TestFrames.ShortFloat(0.5f), T0.AddSeconds(1))),
        }, Iec104Outstation: true);

        (CliProcess.Result r, IReadOnlyList<LoopbackSessionResult> seen) = await RunWithClientAsync(port,
            new[] { first, second },
            InterposeArgs("iec104", port, crosswalk, "--measurements-dir", windows, "--ticks", "4"));

        Assert.True(r.ExitCode == 0, r.ToString());
        // Frames: STARTDT con and the whole frame from each client = 4. client-resets 1: the first client went away.
        // discarded-bytes 11: the 3 junk octets skipped while resynchronising, and the 8 octets of the half frame left
        // when the first client went away (never glued to the second client's stream). Neither is a malformed frame.
        Assert.Contains(StopLine(accepted: 4, rejected: 0, decodeErrors: 0, points: 2, stepPositions: 0, bitstrings: 0,
            windowsWritten: 1, clientResets: 1, unsupported: "none", heldFresh: 0, quarantined: 0, discardedBytes: 11), r.StdOut, StringComparison.Ordinal);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, s => Assert.True(s.Connected));
        // Each connection was started afresh with its own STARTDT act.
        Assert.All(seen, s => Assert.Equal(StartDtAct, Sent(s)[0]));

        MeasurementSet set = MeasurementSet.Load(Assert.Single(Directory.GetFiles(windows)));
        Assert.Equal(T0.AddSeconds(1), set.TakenUtc!.Value);
        Assert.Equal(2, set.Measurements.Count);
        Assert.Equal((double)1.03f, Only(set, MeasurementKind.VmPu).Value);
        Assert.Equal(0.5, Only(set, MeasurementKind.QInjMvar).Value);
        Assert.DoesNotContain(set.Measurements, m => m.Kind == MeasurementKind.PInjMw);   // the cut frame's IOA 1002
    }

    [Fact]
    public async Task Iec104_run_that_times_out_short_of_its_ticks_prints_the_summary_and_exits_1()
    {
        string crosswalk = Iec104Crosswalk();
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        int port = LoopbackRtuClient.FreeLoopbackPort();

        // Two frames arrive (STARTDT con and one I-frame), three were asked for: the outstation stays connected but
        // silent until the time limit.
        (CliProcess.Result r, _) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(new[] { TimedFloat(0, 1001, 1.03f, 0) }, Iec104Outstation: true) },
            InterposeArgs("iec104", port, crosswalk, "--measurements-dir", windows, "--ticks", "3", "--live-seconds", "3"));

        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("time limit: stops after 3 s whatever has arrived (--live-seconds).", r.StdOut, StringComparison.Ordinal);
        // The summary is still printed, and what did arrive is still written.
        Assert.Contains(StopLine(accepted: 2, rejected: 0, decodeErrors: 0, points: 1, stepPositions: 0, bitstrings: 0,
            windowsWritten: 1, clientResets: 0, unsupported: "none", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        Assert.Contains("error: interpose: --live-seconds 3 elapsed after 2 of the 3 frame(s) asked for by --ticks.", r.StdErr, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(windows));
    }

    // ================================================================================================================
    // The zero-frames stop
    // ================================================================================================================

    [Fact]
    public void Zero_frames_with_no_client_stops_on_time_with_an_all_zero_summary_and_a_warning()
    {
        string crosswalk = Iec104Crosswalk();
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        int port = LoopbackRtuClient.FreeLoopbackPort();

        // No --ticks: the time limit is the requested end, so exit 0, but "nothing arrived" is said on stderr.
        CliProcess.Result r = CliProcess.RunBuilt(InterposeArgs("iec104", port, crosswalk, "--measurements-dir", windows, "--live-seconds", "2"));

        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("press any key to stop.", r.StdOut, StringComparison.Ordinal);
        Assert.Contains(StopLine(accepted: 0, rejected: 0, decodeErrors: 0, points: 0, stepPositions: 0, bitstrings: 0,
            windowsWritten: 0, clientResets: 0, unsupported: "none", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        Assert.Contains("warning: interpose: no frames received in 2 s (--live-seconds); check the device and the port.", r.StdErr, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(windows));
    }

    [Fact]
    public async Task Zero_frames_from_a_client_that_connects_and_leaves_counts_the_reset_and_warns()
    {
        string crosswalk = Iec104Crosswalk();
        int port = LoopbackRtuClient.FreeLoopbackPort();

        // A raw client (not an outstation): it connects and closes without reading the STARTDT act or sending anything.
        (CliProcess.Result r, IReadOnlyList<LoopbackSessionResult> seen) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(Array.Empty<byte[]>(), CloseAfterSending: true) },
            InterposeArgs("iec104", port, crosswalk, "--live-seconds", "3"));

        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("no --measurements-dir/--out-store: decoding only (add a sink to persist).", r.StdOut, StringComparison.Ordinal);
        Assert.Contains(StopLine(accepted: 0, rejected: 0, decodeErrors: 0, points: 0, stepPositions: 0, bitstrings: 0,
            windowsWritten: 0, clientResets: 1, unsupported: "none", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        Assert.Contains("warning: interpose: no frames received in 3 s (--live-seconds)", r.StdErr, StringComparison.Ordinal);
        Assert.True(Assert.Single(seen).Connected);
    }

    [Fact]
    public void Zero_frames_with_ticks_asked_for_exits_1_naming_the_shortfall()
    {
        string crosswalk = Iec104Crosswalk();
        int port = LoopbackRtuClient.FreeLoopbackPort();

        CliProcess.Result r = CliProcess.RunBuilt(InterposeArgs("iec104", port, crosswalk, "--ticks", "2", "--live-seconds", "2"));

        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains(StopLine(accepted: 0, rejected: 0, decodeErrors: 0, points: 0, stepPositions: 0, bitstrings: 0,
            windowsWritten: 0, clientResets: 0, unsupported: "none", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        Assert.Contains("error: interpose: --live-seconds 2 elapsed after 0 of the 2 frame(s) asked for by --ticks.", r.StdErr, StringComparison.Ordinal);
        // The shortfall is the error; the "no frames" warning is for runs without --ticks.
        Assert.DoesNotContain("warning: interpose: no frames received", r.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void A_bad_live_seconds_is_a_usage_error_before_anything_listens(string value)
    {
        string crosswalk = Iec104Crosswalk();
        int port = LoopbackRtuClient.FreeLoopbackPort();

        CliProcess.Result r = CliProcess.RunBuilt(InterposeArgs("iec104", port, crosswalk, "--ticks", "1", "--live-seconds", value));

        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains($"error: interpose --live-seconds must be a number above 0 and at most 86400 (got '{value}').", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("listening", r.StdOut, StringComparison.Ordinal);
    }

    // ================================================================================================================
    // Modbus TCP (received passively: the interposer neither polls nor answers)
    // ================================================================================================================

    // One poll as a tap sees it: the read request (registers 100 and 101 of unit 1, function 3) and its response
    // (240, and 0xFF88 = -120 as a signed 16-bit register).
    private static List<byte[]> ModbusPoll(int transactionId) => new()
    {
        ModbusTestFrames.ReadRequest(transactionId, unit: 1, functionCode: 3, start: 100, quantity: 2),
        ModbusTestFrames.ReadResponse(transactionId, 1, 3, 240, 0xFF88),
    };

    [Fact]
    public async Task Modbus_poll_is_live_so_its_window_is_held_and_kept_only_as_a_quarantine_capture()
    {
        string crosswalk = ModbusCrosswalk();
        string store = Path.Combine(_dir, "quarantine");
        int port = LoopbackRtuClient.FreeLoopbackPort();

        (CliProcess.Result r, IReadOnlyList<LoopbackSessionResult> seen) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(ModbusPoll(1), Fragment: true) },
            InterposeArgs("modbus", port, crosswalk, "--out-store", store, "--ticks", "2"));

        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"interpose Modbus TCP: listening 127.0.0.1:{port}, case 'case14', 2 binding(s)"), r.StdOut, StringComparison.Ordinal);
        // Modbus has no time tag, so the window is dated by the receive clock: newer than the horizon, held, and kept
        // only in the firewalled quarantine store (D-DOC-018). Nothing is a written estimator window.
        Assert.Contains(StopLine(accepted: 2, rejected: 0, decodeErrors: 0, points: 2, stepPositions: 0, bitstrings: 0,
            windowsWritten: 0, clientResets: 0, unsupported: "none", heldFresh: 1, quarantined: 1), r.StdOut, StringComparison.Ordinal);
        Assert.Contains(": 2 measurement(s) held (newer than the horizon) [quarantine capture only]", r.StdOut, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(store, "part-*.parquet", SearchOption.AllDirectories));
        LoopbackSessionResult session = Assert.Single(seen);
        Assert.Empty(session.Received);   // the Modbus transport stays passive: it never writes to the connection
    }

    [Fact]
    public async Task Modbus_live_window_with_measurements_dir_is_refused_as_forward_inference_exit_2()
    {
        string crosswalk = ModbusCrosswalk();
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        int port = LoopbackRtuClient.FreeLoopbackPort();

        (CliProcess.Result r, _) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(ModbusPoll(7)) },
            InterposeArgs("modbus", port, crosswalk, "--measurements-dir", windows, "--ticks", "2"));

        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains(StopLine(accepted: 2, rejected: 0, decodeErrors: 0, points: 2, stepPositions: 0, bitstrings: 0,
            windowsWritten: 0, clientResets: 0, unsupported: "none", heldFresh: 1, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        Assert.Contains("REJECTED (forward inference): 1 window(s) newer than the ", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("were not written to --measurements-dir.", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("it is not written to --measurements-dir (live telemetry is never an estimator input)", r.StdErr, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(windows));
    }

    [Fact]
    public async Task Modbus_response_that_does_not_match_its_request_is_a_malformed_frame_and_exits_1()
    {
        string crosswalk = ModbusCrosswalk();
        int port = LoopbackRtuClient.FreeLoopbackPort();

        // The request asks for 2 registers; the response with the same transaction id carries 3. The decoder refuses
        // it (it cannot know which register is which), so no point is decoded and no window is formed.
        var frames = new List<byte[]>
        {
            ModbusTestFrames.ReadRequest(3, unit: 1, functionCode: 3, start: 100, quantity: 2),
            ModbusTestFrames.ReadResponse(3, 1, 3, 240, 0xFF88, 0x0001),
        };
        (CliProcess.Result r, _) = await RunWithClientAsync(port,
            new[] { new LoopbackSession(frames) },
            InterposeArgs("modbus", port, crosswalk, "--ticks", "2"));

        // As for IEC 104 (main's ruling, question 1): warned once, counted, exit 1.
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains(StopLine(accepted: 2, rejected: 0, decodeErrors: 1, points: 0, stepPositions: 0, bitstrings: 0,
            windowsWritten: 0, clientResets: 0, unsupported: "none", heldFresh: 0, quarantined: 0), r.StdOut, StringComparison.Ordinal);
        Assert.Contains("WARNING: interpose: malformed modbus frame (framed but undecodable, 15 octet(s)) at frame 2;", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("error: interpose: 1 malformed frame(s) (0 rejected, 1 undecodable); pass --tolerate-malformed to accept a noisy line.", r.StdErr, StringComparison.Ordinal);
    }
}
