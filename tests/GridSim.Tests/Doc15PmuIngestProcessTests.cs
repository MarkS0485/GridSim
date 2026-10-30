using System;
using System.IO;
using System.Linq;
using GridSim.Core.Estimation;
using Xunit;
using static GridSim.Tests.Doc15Frames;

namespace GridSim.Tests;

/// <summary>
/// DOC-15 (docs-session stream 15, October 2026), the built CLI end to end. D-DOC-008: <c>pmu-ingest --channels</c>
/// checks the channel map against every configuration in force for a decoded DATA frame, not only the capture's last
/// CONFIG frame (a mid-stream change used to let a map through that crashed the mapper with a raw
/// IndexOutOfRangeException). D-DOC-223: the capture is framed whole before any frame is decoded. D-DOC-111: a CONFIG
/// frame with TIME_BASE 0 is refused by name; a DATA frame whose FRACSEC is not below TIME_BASE is malformed. D-DOC-110:
/// the instants written to the measurement-set file names are exact. Each refusal is paired with the allowed case.
/// </summary>
public sealed class Doc15PmuIngestProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-doc15-cli-" + Guid.NewGuid().ToString("N"));

    public Doc15PmuIngestProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static void AssertExit(int expected, CliProcess.Result r) => Assert.True(r.ExitCode == expected, r.ToString());

    private const uint OldSoc = 1_700_000_000;   // 2023-11-14T22:13:20Z: historical

    // PMU 101 with one phasor (a voltage), and the same PMU after a configuration change adding a current phasor.
    private static readonly Pmu VoltageOnly = new(101, "STN_A", new[] { Voltage });
    private static readonly Pmu VoltageAndCurrent = new(101, "STN_A", new[] { Voltage, Current });

    private static readonly (float, float)[] OnePhasor = { (9_500.0f, 0.0f) };
    private static readonly (float, float)[] TwoPhasors = { (9_500.0f, 0.0f), (1_000.0f, -100.0f) };

    private string Capture(params byte[][] frames)
    {
        string path = Path.Combine(_dir, $"cap-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, frames.SelectMany(x => x).ToArray());
        return path;
    }

    // Voltage phasor 0 at bus 1 (the reference) and current phasor 1 on branch 1 -> 4.
    private string ChannelMap()
    {
        string path = Path.Combine(_dir, $"channels-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "schema": "gridsim-pmu-channels/1", "case": "case9", "baseMva": 100,
              "reference": { "pmu": 101, "phasor": 0, "anchorDeg": 0 },
              "pmus": [ { "pmu": 101,
                          "voltages": [ { "phasor": 0, "bus": 1, "baseVolts": 9500, "vmSigmaPu": 0.002, "vaSigmaDeg": 0.1 } ],
                          "currents": [ { "phasor": 1, "from": 1, "to": 4, "baseAmps": 1000, "iMagSigmaPu": 0.005, "iAngSigmaDeg": 0.2 } ] } ] }
            """);
        return path;
    }

    [Fact]
    public void D008_a_map_that_fits_only_the_later_configuration_is_refused_naming_the_earlier_one()
    {
        // Frames 1-2: PMU 101 sends one phasor; frames 3-4: the configuration changes to two. The map uses phasor 1, which
        // the LAST configuration has. Before: checked against the last configuration only, it passed, and the first
        // instant's sample (one phasor) reached the mapper: an unhandled IndexOutOfRangeException.
        string cap = Capture(
            Config(1_000_000, OldSoc, VoltageOnly), Data(OldSoc, 0, OnePhasor),
            Config(1_000_000, OldSoc, VoltageAndCurrent), Data(OldSoc, 20_000, TwoPhasors));
        string outDir = Path.Combine(_dir, "sets");
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap, "--channels", ChannelMap(), "--measurements-dir", outDir);
        AssertExit(1, r);
        Assert.Contains("against the configuration from CONFIG frame 1 (in force for 1 decoded DATA frame(s))", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("PMU 101 ('STN_A') has 1 phasor(s); current phasor 1 does not exist", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIG frame 3", r.StdErr, StringComparison.Ordinal);   // the later configuration fits
        Assert.DoesNotContain("IndexOutOfRange", r.StdErr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outDir));   // refused before any output

        // The reverse change (two phasors, then one) is refused naming the later configuration.
        string reverse = Capture(
            Config(1_000_000, OldSoc, VoltageAndCurrent), Data(OldSoc, 0, TwoPhasors),
            Config(1_000_000, OldSoc, VoltageOnly), Data(OldSoc, 20_000, OnePhasor));
        CliProcess.Result rr = CliProcess.RunBuilt("pmu-ingest", reverse, "--channels", ChannelMap(), "--measurements-dir", outDir);
        AssertExit(1, rr);
        Assert.Contains("against the configuration from CONFIG frame 3 (in force for 1 decoded DATA frame(s))", rr.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIG frame 1 ", rr.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void D008_a_configuration_change_that_the_map_fits_throughout_writes_every_set()
    {
        // Allowed: the CONFIG frame is sent again mid-stream (the station renamed, the same two phasors), so two
        // configurations are in force, one per DATA frame, and the map fits both: every instant gives a set.
        var renamed = new Pmu(101, "STN_A2", new[] { Voltage, Current });
        string cap = Capture(
            Config(1_000_000, OldSoc, VoltageAndCurrent), Data(OldSoc, 0, TwoPhasors),
            Config(1_000_000, OldSoc, renamed), Data(OldSoc, 820_000, TwoPhasors));
        string outDir = Path.Combine(_dir, "sets-ok");
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap, "--channels", ChannelMap(), "--measurements-dir", outDir);
        AssertExit(0, r);
        Assert.Contains($"measurement sets: 2 written to {outDir}", r.StdOut, StringComparison.Ordinal);

        // D-DOC-110: FRACSEC 820,000 at TIME_BASE 1,000,000 is .82 s exactly. Before, the instant was one tick short and
        // the file was named ...2213208199999Z.json.
        string[] files = Directory.GetFiles(outDir, "*.json").Select(f => Path.GetFileName(f)!).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "pmu-20231114T2213200000000Z.json", "pmu-20231114T2213208200000Z.json" }, files);
        MeasurementSet set = MeasurementSet.Load(Path.Combine(outDir, files[1]));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(OldSoc).AddTicks(8_200_000), set.TakenUtc);
    }

    [Fact]
    public void D223_a_capture_that_does_not_frame_is_refused_before_any_frame_is_decoded()
    {
        // Frame 3 is a CONFIG frame with TIME_BASE 0 (refused when parsed), and the file ends in a stray byte that cannot
        // start a frame. With framing and decoding in one loop (as before D-DOC-223), frames 1 and 2 would be parsed and
        // decoded and frame 3's parse refusal reported, the framing fault never seen. Now the capture is framed first, so
        // the framing fault is what is reported and nothing is decoded.
        string cap = Capture(
            Config(1_000_000, OldSoc, VoltageOnly), Data(OldSoc, 0, OnePhasor),
            Config(0, OldSoc, VoltageOnly), new byte[] { 0x00 });
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap);
        AssertExit(1, r);
        Assert.Contains("does not start a C37.118 frame", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("3 frame(s) read before it", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be parsed", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("PMU INGEST", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void D111_a_config_frame_with_time_base_0_is_refused_by_name()
    {
        // Before: accepted; the DATA frame with FRACSEC 20,000 then failed with a raw "Value to add was out of range"
        // (exit 1), and one with FRACSEC 0 decoded silently at its SOC.
        string cap = Capture(Config(0, OldSoc, VoltageOnly), Data(OldSoc, 0, OnePhasor), Data(OldSoc, 20_000, OnePhasor));
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap);
        AssertExit(1, r);
        Assert.Contains("CONFIG frame 1 passed its CRC but cannot be parsed: C37.118 CONFIG frame declares TIME_BASE 0", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Value to add was out of range", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void D111_a_data_frame_whose_fracsec_is_not_below_time_base_is_counted_malformed()
    {
        // FRACSEC 1,000,000 at TIME_BASE 1,000,000 is a whole second, not a fraction: malformed, counted, exit 3. Before it
        // decoded as SOC + 1 s, the same instant as the next second's first frame.
        string cap = Capture(Config(1_000_000, OldSoc, VoltageOnly), Data(OldSoc, 0, OnePhasor), Data(OldSoc, 1_000_000, OnePhasor));
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap);
        AssertExit(3, r);
        Assert.Contains("2 DATA (1 decoded, 0 bad CRC, 1 malformed, 0 before any CONFIG)", r.StdOut, StringComparison.Ordinal);

        // Allowed: the last FRACSEC below TIME_BASE.
        CliProcess.Result ok = CliProcess.RunBuilt("pmu-ingest",
            Capture(Config(1_000_000, OldSoc, VoltageOnly), Data(OldSoc, 0, OnePhasor), Data(OldSoc, 999_999, OnePhasor)));
        AssertExit(0, ok);
        Assert.Contains("2 DATA (2 decoded, 0 bad CRC, 0 malformed, 0 before any CONFIG)", ok.StdOut, StringComparison.Ordinal);
    }
}
