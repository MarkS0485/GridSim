using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Final batch (2026-10-10): a NaN that arithmetic produces has no defined sign or payload (IEEE 754), and on x64 the
/// JIT tier decides it (measured: a diverged frequency run gave NaN 0x7FF8... before tier-up and 0xFFF8... after, with
/// HWIntrinsic off). Result types store every NaN as the one canonical NaN so the bytes they report are the same on
/// every run, tier, CPU and OS. These tests pin that rule; finite and infinite values must pass bit for bit.
/// </summary>
public sealed class CanonicalNaNTests
{
    private static long Bits(double d) => BitConverter.DoubleToInt64Bits(d);

    [Fact]
    public void Every_nan_becomes_the_one_canonical_nan_and_everything_else_is_untouched()
    {
        double negativeNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000000));
        double payloadNaN = BitConverter.Int64BitsToDouble(0x7FF0000000000001);
        foreach (double nan in new[] { double.NaN, negativeNaN, payloadNaN, -double.NaN })
            Assert.Equal(DetMath.CanonicalNaNBits, Bits(DetMath.CanonicalNaN(nan)));
        foreach (double x in new[] { 0.0, -0.0, 1.5, -2.25, double.Epsilon, double.MaxValue, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Equal(Bits(x), Bits(DetMath.CanonicalNaN(x)));
    }

    [Fact]
    public void Frequency_points_and_responses_store_nan_canonically_through_the_constructor_and_with()
    {
        double negativeNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000000));
        var p = new FrequencyPoint(DateTime.UnixEpoch, negativeNaN, negativeNaN);
        Assert.Equal(DetMath.CanonicalNaNBits, Bits(p.Hz));
        Assert.Equal(DetMath.CanonicalNaNBits, Bits(p.RocofHzPerS));
        FrequencyPoint q = p with { Hz = negativeNaN, RocofHzPerS = -0.0 };
        Assert.Equal(DetMath.CanonicalNaNBits, Bits(q.Hz));
        Assert.Equal(Bits(-0.0), Bits(q.RocofHzPerS));

        var r = new FrequencyResponse(negativeNaN, negativeNaN, negativeNaN, negativeNaN, new[] { p });
        foreach (double d in new[] { r.InitialRocofHzPerS, r.NadirHz, r.NadirSeconds, r.SettleHz })
            Assert.Equal(DetMath.CanonicalNaNBits, Bits(d));
        FrequencyResponse s = r with { SettleHz = negativeNaN, NadirHz = 49.5 };
        Assert.Equal(DetMath.CanonicalNaNBits, Bits(s.SettleHz));
        Assert.Equal(Bits(49.5), Bits(s.NadirHz));
    }

    [Fact]
    public void A_diverged_frequency_run_reports_the_canonical_nan_however_warm_the_jit_is()
    {
        // The 1 MW.s inertia floor (E = 0, no forming) diverges: the trace used to run to -infinity and then NaN, and
        // this test pinned that the NaN it reported was the canonical one at every JIT tier.
        // Test changed 2026-10-19: since 1.95.300 (commit 31ab597c, integration stability guards) the explicit swing
        // refuses an inertia below its damping stability bound (dt*f0*D/(2E) < 2) up front, and a non-finite step fails
        // loud, so a diverged run no longer reaches a result at all. The NaN-sign hazard this test guarded is therefore
        // closed more strongly: no NaN of either sign can be reported. The test now pins that the refusal is the same at
        // every tier (repeated so the integrator is tiered up); the canonical storage rule stays pinned by the two tests
        // above.
        FrequencyEvent[] trip = { new(1.0, -1320.0), new(6.0, -200.0), new(12.0, 300.0) };
        for (int rep = 0; rep < 40; rep++)
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => FrequencyDynamics.Simulate(0.0, 30_000, trip, new DynamicsConfig()));
            Assert.Contains("too small to integrate", ex.Message, StringComparison.Ordinal);
        }
    }
}
