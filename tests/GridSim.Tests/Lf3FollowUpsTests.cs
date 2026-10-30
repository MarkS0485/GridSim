using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Stream LF3 of the October 2026 programme: follow-ups from the B4 and CIM3 streams and two LF1 / hygiene leftovers.
/// Items: N301 (the NESO RP1 page is hidden, with a one-line reason, on a case whose nominal is not 50 Hz), N302 (the WPF
/// TripCompare trips the shared largest secured unit; the WPF frequency dial colours by the lamp's band-edge rule), N304
/// (a protection frequency setting more than 5 Hz from its settings nominal is refused at load; LF6 N318 made the rule
/// direction-aware, within 10 Hz on the stage's own side), N278 (no middle dots
/// left in web / WPF display text), N283 (crossval_h0.py reads the responsiveCap export field), N306 (CIM exporter shunt
/// control, SwitchFlows on a controlled result, EquivalentInjection capability curve, CGMES 3.0 boundary base in the
/// node-breaker view) and N307 (shared remote voltage control by reactive range; transformer magnetising conductance).
/// Ruling R1 (Mark delegated EE calls 2026-10-04) covers every behaviour change. One partial file per item
/// (Lf3FollowUpsTests.&lt;item&gt;.cs).
/// </summary>
public sealed partial class Lf3FollowUpsTests
{
    /// <summary>A source file's text (LF line ends), via the shared repo-root helper.</summary>
    private static string Src(params string[] rel) => PresentationConstantsAntiDriftTests.Source(rel);

    /// <summary>Exact bit equality of two doubles (Assert.Equal on doubles treats +0 and -0 as equal; this does not).</summary>
    private static void AssertBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");
}
