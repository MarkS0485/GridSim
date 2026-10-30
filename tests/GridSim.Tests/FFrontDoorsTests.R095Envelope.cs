using GridSim.Cli;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R095 remainder (stream F): <c>trip --asset-dynamics --envelope &lt;spec&gt;</c>, the front door of
/// <see cref="AssetEnvelope.Sweep"/>. Pinned: the spec parser's every rule against a real class library; that the
/// envelope block is inserted after the half-inertia line with every other line unchanged; that its numbers are the Core
/// sweep's for the trip's own inputs (gb-synthetic); and the refusals.
/// </summary>
public sealed partial class FFrontDoorsTests
{
    private static string GbSyntheticDir => Path.Combine(CliProcess.Root(), "data", "gb-synthetic");

    private static AssetClassLibrary GbSyntheticLibrary() => JsonGridLoader.LoadDirectory(GbSyntheticDir).AssetDynamics!.Library;

    [Fact]
    public void R095_envelope_spec_parses_items_with_the_library_spelling_of_the_class()
    {
        AssetClassLibrary lib = GbSyntheticLibrary();
        IReadOnlyList<AssetSweepParam>? s = AssetEnvelopeSpec.Parse(
            "DG-ALOMCP:rocofTripHzPerS=0.5..2, gfl-wind-2024:actuateTauS=0.1..0.6", lib, out string? error);
        Assert.Null(error);
        Assert.Equal(new[]
        {
            new AssetSweepParam("dg-alomcp", AssetParams.RocofTripHzPerS, 0.5, 2.0),
            new AssetSweepParam("gfl-wind-2024", AssetParams.ActuateTauS, 0.1, 0.6),
        }, s);
    }

    [Theory]
    [InlineData("dg-alomcp", "is not <class>:<param>=<low>..<high>")]
    [InlineData("dg-alomcp:rocofTripHzPerS=0.5", "is not <class>:<param>=<low>..<high>")]
    [InlineData("dg-alomcp:rocofTripHzPerS=0.5..", "is not <class>:<param>=<low>..<high>")]
    [InlineData("no-such-class:rocofTripHzPerS=0.5..2", "asset class 'no-such-class' is not in this case's asset-classes.json")]
    [InlineData("dg-alomcp:ufTripHz=47..48", "'ufTripHz' is not a parameter a GridFollowingInverter class is simulated with")]
    [InlineData("dg-alomcp:rocofTripHzPerS=a..2", "the range must be two finite numbers")]
    [InlineData("dg-alomcp:rocofTripHzPerS=2..0.5", "the low end must be below the high end")]
    [InlineData("dg-alomcp:rocofTripHzPerS=1..1", "the low end must be below the high end")]
    [InlineData("dg-alomcp:rocofTripHzPerS=0.5..2,dg-alomcp:rocofTripHzPerS=0.1..3", "names dg-alomcp:rocofTripHzPerS twice")]
    public void R095_envelope_spec_refuses_each_bad_item_naming_it(string spec, string expected)
    {
        Assert.Null(AssetEnvelopeSpec.Parse(spec, GbSyntheticLibrary(), out string? error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void R095_envelope_spec_refuses_more_than_twelve_parameters()
    {
        AssetClassLibrary lib = GbSyntheticLibrary();
        string[] keys = AssetClassSupport.AcceptedKeys(lib.Get("dg-alomcp").Kind).OrderBy(k => k, StringComparer.Ordinal).Take(13).ToArray();
        Assert.Equal(13, keys.Length);
        string spec = string.Join(",", keys.Select(k => $"dg-alomcp:{k}=1..2"));
        Assert.Null(AssetEnvelopeSpec.Parse(spec, lib, out string? error));
        Assert.Contains("at most 12", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The envelope block sits after the half-inertia line, every line of the plain asset-dynamics trip is unchanged, and
    /// the numbers are the Core sweep's for the trip's own post-trip model, demand, loss and config.
    /// </summary>
    [Fact]
    public void R095_trip_envelope_inserts_the_core_sweep_and_moves_no_existing_line()
    {
        const string Spec = "dg-alomcp:rocofTripHzPerS=0.05..10";
        CliProcess.Result plain = CliProcess.RunBuilt("trip", "--dir", GbSyntheticDir, "--asset-dynamics");
        CliProcess.Result env = CliProcess.RunBuilt("trip", "--dir", GbSyntheticDir, "--asset-dynamics", "--envelope", Spec);
        Assert.True(plain.ExitCode == 0, plain.ToString());
        Assert.True(env.ExitCode == 0, env.ToString());
        (string head, string tail) = SplitAtHalfInertia(plain.StdOut);
        Assert.StartsWith(head, env.StdOut, StringComparison.Ordinal);
        Assert.EndsWith(tail, env.StdOut, StringComparison.Ordinal);
        string block = env.StdOut[head.Length..^tail.Length];

        // The trip's inputs, built exactly as RunTrip builds them (largest non-slack unit with inertia, case nominal).
        GridModel model = JsonGridLoader.LoadDirectory(GbSyntheticDir);
        int slackBus = model.Buses.First(b => b.Type == BusType.Slack).Id;
        Generator gen = model.Generators.Where(g => g.Bus != slackBus && g.H > 0).OrderByDescending(g => g.Pg).First();
        var cfg = new DynamicsConfig { NominalHz = model.FrequencyHz, EnableAssetDynamics = true };
        var ev = new[] { new FrequencyEvent(FrequencyDynamics.DesignEventSeconds, -gen.Pg) };
        IReadOnlyList<AssetSweepParam> sweep = AssetEnvelopeSpec.Parse(Spec, model.AssetDynamics!.Library, out _)!;
        AssetEnvelopeResult core = AssetEnvelope.Sweep(PostTripModel.Without(model, gen), model.Buses.Sum(b => b.Pd), ev, cfg, sweep);

        Assert.Contains("Asset envelope (--envelope, 1 parameter(s): 2 corner run(s) + 2 tornado run(s); provenance UnvalidatedAtScale)", block, StringComparison.Ordinal);
        Assert.Contains($"nadir:       {core.NadirMinHz:F3} .. {core.NadirMedianHz:F3} (median) .. {core.NadirMaxHz:F3} Hz", block, StringComparison.Ordinal);
        Assert.Contains($"peak RoCoF:  {core.RocofMinHzPerS:F3} .. {core.RocofMaxHzPerS:F3} Hz/s", block, StringComparison.Ordinal);
        Assert.Contains(core.CascadeIsPossible ? "cascade:     POSSIBLE" : "cascade:     ", block, StringComparison.Ordinal);
        Assert.Contains($"nadir {core.Tornado[0].NadirAtLow:F3} -> {core.Tornado[0].NadirAtHigh:F3} Hz", block, StringComparison.Ordinal);
        Assert.Contains("dg-alomcp:rocofTripHzPerS", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Asset envelope", plain.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void R095_trip_envelope_refusals()
    {
        // Without the asset fleet: exit 2 before any load.
        CliProcess.Result noAssets = CliProcess.RunBuilt("trip", "--dir", GbSyntheticDir, "--envelope", "dg-alomcp:rocofTripHzPerS=0.5..2");
        Assert.True(noAssets.ExitCode == 2, noAssets.ToString());
        Assert.Contains("add --asset-dynamics", noAssets.StdErr, StringComparison.Ordinal);

        // A bad item: exit 2, named, before the trip prints anything.
        CliProcess.Result bad = CliProcess.RunBuilt("trip", "--dir", GbSyntheticDir, "--asset-dynamics", "--envelope", "nope:rocofTripHzPerS=0.5..2");
        Assert.True(bad.ExitCode == 2, bad.ToString());
        Assert.Contains("asset class 'nope' is not in this case's asset-classes.json", bad.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Tripped:", bad.StdOut, StringComparison.Ordinal);

        // Present with no value: the shared value-flag refusal.
        CliProcess.Result empty = CliProcess.RunBuilt("trip", "--dir", GbSyntheticDir, "--asset-dynamics", "--envelope");
        Assert.True(empty.ExitCode == 2, empty.ToString());
        Assert.Contains("--envelope expected a value", empty.StdErr, StringComparison.Ordinal);
    }
}
