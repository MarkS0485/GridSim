using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace GridSim.Core.Numerics;

/// <summary>
/// Runtime detection of the vector instruction sets this process can actually use, and the
/// single place that decides which SIMD tier the numeric kernels dispatch to. The kernels are
/// written widest-first (<see cref="SimdTier.Avx512"/> down to <see cref="SimdTier.Scalar"/>);
/// this type reports what the CPU offers so the app can (a) pick the widest path at runtime and
/// (b) record which tier ran a benchmark. Detection is by <see cref="System.Runtime.Intrinsics"/>
/// hardware flags, NOT by the dev machine's capabilities - a build produced on an AVX2 box still
/// lights up the full 512-bit path on an AVX-512 EPYC because the choice is made at run time.
///
/// This deliberately does not rely on <c>System.Numerics.Vector&lt;T&gt;</c>'s width: that type
/// does not reliably widen to 512 bits on AVX-512 hardware, so it can silently cap at 256. The
/// kernels use explicit <see cref="Vector512{T}"/>/<see cref="Vector256{T}"/>/<see cref="Vector128{T}"/>
/// paths keyed off <see cref="Tier"/> instead.
/// </summary>
public static class SimdCapabilities
{
    static SimdCapabilities()
    {
        // Widest hardware-accelerated width wins. IsHardwareAccelerated is the honest gate: it is
        // true only when the JIT will emit real vector instructions for that width on this CPU.
        if (Vector512.IsHardwareAccelerated) Tier = SimdTier.Avx512;
        else if (Vector256.IsHardwareAccelerated) Tier = SimdTier.Avx256;
        else if (Vector128.IsHardwareAccelerated) Tier = SimdTier.Sse128;
        else Tier = SimdTier.Scalar;

        DoubleLanes = Tier switch
        {
            SimdTier.Avx512 => 8,
            SimdTier.Avx256 => 4,
            SimdTier.Sse128 => 2,
            _ => 1,
        };
    }

    /// <summary>The widest vector tier the kernels will dispatch to on this CPU.</summary>
    public static SimdTier Tier { get; }

    /// <summary>
    /// The tier the hardware flags say this process SHOULD run at, from the same IsHardwareAccelerated gates that
    /// choose <see cref="Tier"/>. R102: a test asserts <see cref="Tier"/> equals this, so a dispatch edit that
    /// silently drops a width fails; and <see cref="Avx512Unused"/> flags the Xeon case where the CPU has AVX-512
    /// but the runtime does not use it.
    /// </summary>
    public static SimdTier ExpectedTierFromHardware =>
        Vector512.IsHardwareAccelerated ? SimdTier.Avx512
        : Vector256.IsHardwareAccelerated ? SimdTier.Avx256
        : Vector128.IsHardwareAccelerated ? SimdTier.Sse128
        : SimdTier.Scalar;

    /// <summary>
    /// True when the CPU supports AVX-512F but the active tier is narrower. On Skylake-SP / Cascade Lake Xeons .NET
    /// reports Vector512.IsHardwareAccelerated = false by default (its downclock heuristic); setting the environment
    /// variable <c>DOTNET_PreferredVectorBitWidth=512</c> before start-up enables the 8-wide kernels. Reported by
    /// <c>gridsim simdcheck</c> (R102), which fails with <c>--expect Avx512</c>.
    /// </summary>
    public static bool Avx512Unused => Avx512F.IsSupported && Tier < SimdTier.Avx512;

    /// <summary>Doubles processed per vector step at the active <see cref="Tier"/> (8/4/2/1).</summary>
    public static int DoubleLanes { get; }

    /// <summary>Whether a fused multiply-add is available (needed by the opt-in fast-math kernels;
    /// FMA changes the last bit vs a separate multiply-then-add, so the bit-exact kernels avoid it).</summary>
    public static bool FmaAvailable { get; } = Fma.IsSupported || AdvSimd.IsSupported;

    /// <summary>A one-line human summary for <c>simdcheck</c> and the benchmark run header.</summary>
    public static string Describe()
        => $"SIMD: {Tier} ({DoubleLanes} doubles/step)  FMA={(FmaAvailable ? "yes" : "no")}  "
         + $"[Avx512F={Avx512F.IsSupported} Avx2={Avx2.IsSupported} Fma={Fma.IsSupported} "
         + $"Sse2={Sse2.IsSupported} AdvSimd={AdvSimd.IsSupported}]";
}

/// <summary>The vector width a numeric kernel will execute at, chosen once at startup by
/// <see cref="SimdCapabilities"/>.</summary>
public enum SimdTier
{
    /// <summary>No hardware vectorisation - the plain scalar loop.</summary>
    Scalar = 0,
    /// <summary>128-bit (SSE2/AdvSimd): 2 doubles per step.</summary>
    Sse128 = 1,
    /// <summary>256-bit (AVX/AVX2): 4 doubles per step.</summary>
    Avx256 = 2,
    /// <summary>512-bit (AVX-512): 8 doubles per step.</summary>
    Avx512 = 3,
}
