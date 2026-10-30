using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The H (hygiene) stream of the October 2026 inertia re-appraisal (review-progress/2026-10/inertia-reappraisal.md):
/// named constants, guards, globals and dead code in the frequency / inertia family. The rule for the whole stream is
/// that NO number moves: each region pins a new name to exactly the literal it replaced (exact <c>Assert.Equal</c> on
/// doubles), checks with the source text that the literal did not survive beside its new name, and, where the old
/// code path can still be written out, compares the new path with it bit for bit. Where an item is a behaviour fix
/// (a refusal, a guard against an input that used to produce NaN), the region says so and pins the new behaviour.
/// One partial file per item (HHygieneTests.&lt;item&gt;.cs).
/// </summary>
public sealed partial class HHygieneTests
{
    /// <summary>A Core source file's text (LF line ends), via the shared repo-root helper.</summary>
    private static string CoreSrc(params string[] rel) =>
        PresentationConstantsAntiDriftTests.Source(new[] { "src", "GridSim.Core" }.Concat(rel).ToArray());

    /// <summary>Exact bit equality of two doubles (Assert.Equal on doubles treats +0 and -0 as equal; this does not).</summary>
    private static void AssertBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>A spread of awkward inputs: signed zeros, values either side of every threshold the swing uses, large
    /// and tiny magnitudes. Deterministic.</summary>
    private static IEnumerable<double> Sweep()
    {
        double[] fixedValues = { 0.0, -0.0, 0.015, -0.015, 0.0150000001, -0.0150000001, 0.2, -0.2, 0.5, -0.5,
            0.5000000001, -0.4999999999, 1.0, -1.0, 2.5, -2.5, 1e-300, -1e-300, 1e6, -1e6 };
        foreach (double v in fixedValues) yield return v;
        var rng = new Random(20261005);
        for (int i = 0; i < 400; i++) yield return (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-4, 4));
    }
}
