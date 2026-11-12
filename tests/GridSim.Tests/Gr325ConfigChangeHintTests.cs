using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using GridSim.Core.Synchrophasor;
using Xunit;
using static GridSim.Tests.Doc15Frames;

namespace GridSim.Tests;

/// <summary>
/// GR-325 (November 2026), decided: a DATA frame whose STAT bit 10 (configuration change) is set stays usable. By IEEE
/// C37.118.2 the bit WARNS of a coming change (it is set for about a minute before it); the data is valid under the
/// configuration in force, so the verdict (D108) is unchanged. pmu-ingest now counts such frames and prints ONE stderr
/// line asking the operator to re-send the CONFIG frame once the change is made.
/// </summary>
public sealed class Gr325ConfigChangeHintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-gr325-" + Guid.NewGuid().ToString("N"));

    public Gr325ConfigChangeHintTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private const uint OldSoc = 1_700_000_000;   // historical
    private static readonly Pmu VoltageOnly = new(101, "STN_A", new[] { Voltage });
    private static readonly (float, float)[] OnePhasor = { (9_500.0f, 0.0f) };

    /// <summary>A one-PMU DATA frame with the given STAT word (the first block's STAT sits right after the 14-octet
    /// header) and its CRC rewritten.</summary>
    private static byte[] DataWithStat(uint fracsec, ushort stat)
    {
        byte[] f = Data(OldSoc, fracsec, OnePhasor);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(14), stat);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(f.Length - 2), C37118Crc.Compute(f.AsSpan(0, f.Length - 2)));
        return f;
    }

    private string Capture(params byte[][] frames)
    {
        string path = Path.Combine(_dir, $"cap-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, frames.SelectMany(x => x).ToArray());
        return path;
    }

    [Fact]
    public void Bit_10_is_the_configuration_change_flag_and_does_not_change_the_verdict()
    {
        Assert.True(C37118SampleQuality.ConfigChangePending(0x0400));
        Assert.True(C37118SampleQuality.ConfigChangePending(0xFFFF));
        Assert.False(C37118SampleQuality.ConfigChangePending(0x0000));
        Assert.False(C37118SampleQuality.ConfigChangePending(0xFBFF));
        Assert.Equal(C37118SampleVerdict.Usable, C37118SampleQuality.OfStat(0x0400));
        Assert.Equal(C37118SampleQuality.OfStat(0x2000), C37118SampleQuality.OfStat(0x2400));
        Assert.Equal(
            "pmu-ingest: 2 DATA frame(s) carry STAT bit 10 (configuration change pending). Their data is valid under the configuration in force and was used; once the PMU has changed its configuration, re-send the CONFIG frame (CFG-2) so the frames that follow are decoded with the new one.",
            C37118SampleQuality.ConfigChangeHint(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => C37118SampleQuality.ConfigChangeHint(0));
    }

    [Fact]
    public void Pmu_ingest_counts_the_frames_and_prints_one_hint_line_without_changing_the_result()
    {
        string plain = Capture(Config(1_000_000, OldSoc, VoltageOnly), DataWithStat(0, 0), DataWithStat(20_000, 0), DataWithStat(40_000, 0));
        string flagged = Capture(Config(1_000_000, OldSoc, VoltageOnly), DataWithStat(0, 0), DataWithStat(20_000, 0x0400), DataWithStat(40_000, 0x0400));

        CliProcess.Result a = CliProcess.RunBuilt("pmu-ingest", plain);
        CliProcess.Result b = CliProcess.RunBuilt("pmu-ingest", flagged);

        Assert.True(a.ExitCode == b.ExitCode, $"exit codes differ: {a} / {b}");
        Assert.Contains("PMU INGEST", a.StdOut, StringComparison.Ordinal);
        Assert.Contains("PMU INGEST", b.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("STAT bit 10", a.StdErr, StringComparison.Ordinal);
        Assert.Contains(C37118SampleQuality.ConfigChangeHint(2), b.StdErr, StringComparison.Ordinal);
        Assert.Single(b.StdErr.Split('\n'), l => l.Contains("STAT bit 10", StringComparison.Ordinal));
    }
}
