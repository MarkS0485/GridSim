using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Stream B3 of the October 2026 inertia re-appraisal (review-progress/2026-10/inertia-reappraisal.md): governors, RMS
/// and dynamic data (R252, R246, R083, R061 governors, N045, N289, N124, F23). Ruling R1 (Mark delegated EE calls
/// 2026-10-04) covers every behaviour change. Where an item is additive, the region proves that with the new input absent
/// every number is bit for bit what it was (the old code written out, or the old call compared exactly); where it is a
/// behaviour fix, the region pins the new behaviour against an independent oracle (closed form or the standard's block
/// diagram). One partial file per item (B3GovernorsDynamicDataTests.&lt;item&gt;.cs).
/// </summary>
public sealed partial class B3GovernorsDynamicDataTests
{
    /// <summary>Exact bit equality of two doubles (Assert.Equal on doubles treats +0 and -0 as equal; this does not).</summary>
    private static void AssertBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");
}
