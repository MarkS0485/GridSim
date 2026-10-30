using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Stream B4 of the October 2026 inertia re-appraisal (review-progress/2026-10/inertia-reappraisal.md): GB credible loss
/// and thresholds. Items: N039 (the credible loss counts every single infeed, not synchronous machines only), N135 (1320 MW
/// is the SQSS NORMAL infeed loss risk), R287 (the H0 study's analytic RoCoF uses the first loss, not the sum of every
/// staged loss), R289 (the GFM fraction a study reports is the one it realised), R137 (one default table for protection
/// dwell / confirm times), R139 / R149 (one operational band; the SCADA alarm band named as such), R148 (the TRIP / DEVICE
/// sizing targets are the named GB limits), N041 (the RoCoF frequency-event lamp uses the post-ALoMCP 1 Hz/s setting in
/// both shells), N157 (NESO "Bioenergy" and NGED "(not CHP)" fuel strings) and N297 (screening, LFDD blocks, asset
/// protection stages and the what-if tabs at the model's nominal). Ruling R1 (Mark delegated EE calls 2026-10-04) covers
/// every behaviour change; every moved pin carries "Moved 2026-10 (B4 &lt;id&gt;, ...)". One partial file per item
/// (B4GbLossThresholdsTests.&lt;item&gt;.cs).
/// </summary>
public sealed partial class B4GbLossThresholdsTests
{
    /// <summary>A source file's text (LF line ends), via the shared repo-root helper.</summary>
    private static string Src(params string[] rel) => PresentationConstantsAntiDriftTests.Source(rel);

    /// <summary>Exact bit equality of two doubles (Assert.Equal on doubles treats +0 and -0 as equal; this does not).</summary>
    private static void AssertBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");
}
