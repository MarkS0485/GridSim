using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Stream B1 of the October 2026 inertia re-appraisal (review-progress/2026-10/inertia-reappraisal.md): one swing kernel
/// (R080) and the items that ride on it (R060, R081, R253, R025, R239, R240, N275, N282). Ruling R1 (Mark delegated EE
/// calls 2026-10-04) covers every behaviour change. Where an item is a refactor, the region carries the old code verbatim
/// and compares it with the new path bit for bit; where it is a behaviour fix, the region pins the new behaviour and,
/// where it can, proves that every input the fix does not reach is unchanged. One partial file per item
/// (B1SwingKernelTests.&lt;item&gt;.cs).
/// </summary>
public sealed partial class B1SwingKernelTests
{
    /// <summary>A Core source file's text (LF line ends), via the shared repo-root helper.</summary>
    private static string CoreSrc(params string[] rel) =>
        PresentationConstantsAntiDriftTests.Source(new[] { "src", "GridSim.Core" }.Concat(rel).ToArray());

    /// <summary>Exact bit equality of two doubles (Assert.Equal on doubles treats +0 and -0 as equal; this does not).</summary>
    private static void AssertBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>Two responses identical to the bit: every headline number and every trajectory point.</summary>
    private static void AssertSameBits(FrequencyResponse expected, FrequencyResponse actual, string what)
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

    /// <summary>The GB-calibrated recorded services the replay events carry (DC / DM / DR shapes), for configs that must
    /// exercise the service path.</summary>
    private static IReadOnlyList<SystemResponseService> Services() => new[]
    {
        new SystemResponseService(950, 0.015, 0.5, 0.5, KneeDeviationHz: 0.2, KneeFraction: 0.05),
        new SystemResponseService(520, 0.1, 0.2, 1.0, KneeDeviationHz: 0.1, KneeFraction: 0.0),
        new SystemResponseService(490, 0.015, 0.2, 2.0, KneeDeviationHz: 0.015, KneeFraction: 0.0),
    };
}
