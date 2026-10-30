using GridSim.Core.Assets;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Stream B2 of the October 2026 inertia re-appraisal (review-progress/2026-10/inertia-reappraisal.md): the live loop.
/// Items: R043 (asset trips book the dispatch actually running), R044 (asset trip deficits and protection sheds are
/// restored like manual trips), R231 (a battery pack keeps ageing after a reset), R253 grid-following part (an inverter
/// re-arms its compute latency after each excursion), N273 (the live loop, web and desktop take the nominal frequency and
/// the frequency bands from the model) and the live parts of N291 (the live step guard; bands not fixed GB values in the
/// shared flags). Ruling R1 (Mark delegated EE calls 2026-10-04) covers every behaviour change. One partial file per
/// item (B2LiveLoopTests.&lt;item&gt;.cs). Where an item can leave a path unchanged, the region proves it bit for bit.
/// </summary>
public sealed partial class B2LiveLoopTests
{
    /// <summary>A source file's text (LF line ends), via the shared repo-root helper.</summary>
    private static string Src(params string[] rel) => PresentationConstantsAntiDriftTests.Source(rel);

    /// <summary>A Core source file's text.</summary>
    private static string CoreSrc(params string[] rel) =>
        PresentationConstantsAntiDriftTests.Source(new[] { "src", "GridSim.Core" }.Concat(rel).ToArray());

    /// <summary>Exact bit equality of two doubles (Assert.Equal on doubles treats +0 and -0 as equal; this does not).</summary>
    private static void AssertBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>A one-bus GB-scale model with one grid-following class bound to a "storage" generator of
    /// <paramref name="pgMw"/> MW (rated <paramref name="ratedMw"/>), plus the synchronous slack of the shared helper.</summary>
    private static GridModel OneGflModel(AssetClass cls, double pgMw, double ratedMw)
    {
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { cls }, new AssetBinding("storage", cls.Name));
        return AssetTestHelpers.Model(bundle, new Generator(1, Pg: pgMw, MBase: ratedMw, H: 0, Type: "storage"));
    }
}
