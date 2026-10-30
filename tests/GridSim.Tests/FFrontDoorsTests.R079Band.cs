using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R079 (stream F): <c>trip --band[=&lt;fraction&gt;]</c>, the front door of <see cref="FrequencySensitivitySweep"/>. Pinned:
/// without the flag nothing changes and with it every line the trip printed before is still there, in place, with the band
/// block inserted after the half-inertia line; the band's numbers are the Core sweep's for the trip's own inputs; and the
/// refusals (a fraction outside (0, 1), the asset fleet and the distributed model) exit 2.
/// </summary>
public sealed partial class FFrontDoorsTests
{
    /// <summary>Split a trip's stdout into the part up to and including the half-inertia line, and the rest.</summary>
    private static (string Head, string Tail) SplitAtHalfInertia(string stdout)
    {
        int i = stdout.IndexOf("...at HALF the inertia", StringComparison.Ordinal);
        Assert.True(i >= 0, "half-inertia line not found:\n" + stdout);
        int end = stdout.IndexOf('\n', i) + 1;
        return (stdout[..end], stdout[end..]);
    }

    [Fact]
    public void R079_trip_band_inserts_one_block_and_moves_no_existing_line()
    {
        CliProcess.Result plain = CliProcess.RunBuilt("trip", "case9");
        CliProcess.Result band = CliProcess.RunBuilt("trip", "case9", "--band");
        Assert.True(plain.ExitCode == 0, plain.ToString());
        Assert.True(band.ExitCode == 0, band.ToString());

        (string head, string tail) = SplitAtHalfInertia(plain.StdOut);
        Assert.StartsWith(head, band.StdOut, StringComparison.Ordinal);     // everything up to the half-inertia line, byte for byte
        Assert.EndsWith(tail, band.StdOut, StringComparison.Ordinal);       // and everything after it (the protection lines)
        string block = band.StdOut[head.Length..^tail.Length];
        Assert.Contains("Uncertainty band (--band 0.2): the unfitted swing constants each +/- 20 %", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Uncertainty band", plain.StdOut, StringComparison.Ordinal);
    }

    /// <summary>The printed band is the Core sweep's, for the trip's own inertia, demand, loss and config (case9 at 60 Hz).</summary>
    [Fact]
    public void R079_trip_band_prints_the_core_sweep_for_the_trip_inputs()
    {
        // The trip's inputs, built exactly as RunTrip builds them for "trip case9" (no generator named: the largest
        // non-slack unit with inertia; the lumped model; the case's own nominal frequency).
        GridModel model = IeeeCases.Case9();
        int slackBus = model.Buses.First(b => b.Type == BusType.Slack).Id;
        Generator gen = model.Generators.Where(g => g.Bus != slackBus && g.H > 0).OrderByDescending(g => g.Pg).First();
        double totalInertia = model.Generators.Sum(g => g.H * g.MBase);
        double remaining = Math.Max(1, totalInertia - gen.H * gen.MBase);
        double demand = model.Buses.Sum(b => b.Pd);
        var cfg = new DynamicsConfig { NominalHz = model.FrequencyHz };
        var ev = new[] { new FrequencyEvent(FrequencyDynamics.DesignEventSeconds, -gen.Pg) };
        FrequencySensitivityResult sr = FrequencySensitivitySweep.Sweep(remaining, demand, ev, cfg, 0.3);

        CliProcess.Result r = CliProcess.RunBuilt("trip", "case9", "--band=0.3");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("Uncertainty band (--band 0.3): the unfitted swing constants each +/- 30 %", r.StdOut, StringComparison.Ordinal);
        Assert.Contains($"nadir over every corner:  {sr.NadirMinHz:F3} .. {sr.NadirMaxHz:F3} Hz (width {sr.NadirBandHz:F3} Hz)", r.StdOut, StringComparison.Ordinal);
        // One row per constant, most leverage first, as the sweep sorted them.
        string[] rows = r.StdOut.Split('\n').Where(l => l.Contains("  (swing ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, rows.Length);
        for (int i = 0; i < 3; i++)
            Assert.Contains($"nadir {sr.PerParameter[i].NadirAtLow:F3} -> {sr.PerParameter[i].NadirAtHigh:F3} Hz  (swing {sr.PerParameter[i].NadirSwingHz:F3} Hz)",
                rows[i], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--band", "1" }, "--band expected a fraction strictly between 0 and 1")]
    [InlineData(new[] { "--band=0" }, "--band expected a fraction strictly between 0 and 1")]
    [InlineData(new[] { "--band", "--band" }, "--band given more than once")]
    [InlineData(new[] { "--band", "--inertia-model", "distributed" }, "cannot be combined with --inertia-model distributed")]
    public void R079_trip_band_refusals_exit_2(string[] flags, string expected)
    {
        CliProcess.Result r = CliProcess.RunBuilt(new[] { "trip", "case9" }.Concat(flags).ToArray());
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains(expected, r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void R079_trip_band_is_refused_with_the_asset_fleet()
    {
        CliProcess.Result r = CliProcess.RunBuilt("trip", "--dir", Path.Combine(CliProcess.Root(), "data", "gb-synthetic"), "--asset-dynamics", "--band");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("cannot be combined with --asset-dynamics", r.StdErr, StringComparison.Ordinal);
    }
}
