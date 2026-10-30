using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Stream B5 of the October 2026 inertia re-appraisal (review-progress/2026-10/inertia-reappraisal.md): converter models.
/// Items: R138 (one PLL default for the three converter representations), R140 (grid-forming impedance in per-unit
/// quantities), R035 with the R185 remainder (the wave model's synchronising matrix), N291 (the fleet engines run
/// grid-forming instead of refusing it) and N309 (code defaults for the B3 kind keys). Ruling R1 (Mark delegated EE calls
/// 2026-10-04) covers every behaviour change. One partial file per item (B5ConverterModelsTests.&lt;item&gt;.cs); where an
/// item is a behaviour fix the region pins the new behaviour and proves that every input the fix does not reach is
/// unchanged, bit for bit.
/// </summary>
public sealed partial class B5ConverterModelsTests
{
    /// <summary>A Core source file's text (LF line ends), via the shared repo-root helper.</summary>
    private static string CoreSrc(params string[] rel) =>
        PresentationConstantsAntiDriftTests.Source(new[] { "src", "GridSim.Core" }.Concat(rel).ToArray());

    /// <summary>Exact bit equality of two doubles (Assert.Equal on doubles treats +0 and -0 as equal; this does not).</summary>
    private static void AssertBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>Two responses identical to the bit: every headline number and every trajectory point.</summary>
    private static void AssertSameBits(GridSim.Core.Dynamics.FrequencyResponse expected, GridSim.Core.Dynamics.FrequencyResponse actual, string what)
    {
        AssertBits(expected.InitialRocofHzPerS, actual.InitialRocofHzPerS, what + " peak RoCoF");
        AssertBits(expected.NadirHz, actual.NadirHz, what + " nadir");
        AssertBits(expected.NadirSeconds, actual.NadirSeconds, what + " nadir time");
        AssertBits(expected.SettleHz, actual.SettleHz, what + " settle");
        Assert.Equal(expected.Points.Count, actual.Points.Count);
        for (int i = 0; i < expected.Points.Count; i++)
        {
            FrequencyPoint a = expected.Points[i], b = actual.Points[i];
            Assert.Equal(a.Time, b.Time);
            AssertBits(a.Hz, b.Hz, $"{what} point {i} Hz");
            AssertBits(a.RocofHzPerS, b.RocofHzPerS, $"{what} point {i} RoCoF");
        }
    }
}
