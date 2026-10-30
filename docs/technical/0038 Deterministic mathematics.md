# 38. Deterministic mathematics

GridSim's contract is bit-identity: the same case gives the same bits on every run, on every operating system
(Windows, Linux, macOS) and on every CPU vector tier (AVX-512, AVX2, SSE, no SIMD; x64 and ARM64). That contract
cannot rest on the platform's own maths, because the platform does not promise it. This chapter specifies the small
library in `src/GridSim.Core/Numerics/` that does: the deterministic elementary functions (`DetMath`, a port of
FDLIBM 5.3), the canonical NaN, the fixed-order vector kernels (`SimdKernels`), the fused multiply-add policy, the
deterministic parallel loop, the frozen sort, the complex helpers, the true-residual check and the tolerances
register. It then states how the rules are enforced (an IL-level analyser test) and how the CPU tiers are tested.

What the solvers do with these pieces is in [ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>);
the invariants that rest on them are listed in [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>).
A short overview for readers new to the repository is [MATH.md](../../MATH.md).

## Model

### Why the platform maths is not reproducible

Five things move the last bit of a floating-point result between machines, and each one is closed off separately.

| Source of drift | What happens | How GridSim closes it |
|---|---|---|
| The host maths library | .NET does not implement `Math.Sin`, `Cos`, `Tan`, `Asin`, `Acos`, `Atan`, `Atan2`, `Exp`, `Log`, `Log10`, `Pow`, `Sinh`, `Cosh` itself. At run time they call the C runtime of the operating system (the Microsoft C runtime on Windows, glibc's libm on Linux, Apple's libm on macOS), and those round some results differently in the last binary place. Measured on GridSim (N175): 33 exact-bit pins differed between Windows and `ubuntu-latest`. | `DetMath`, a C# port of FDLIBM 5.3 in plain IEEE 754 arithmetic. |
| `System.Numerics.Complex` | `Complex.Phase`, `FromPolarCoordinates`, `Sinh`, `Tanh` and the rest are built on the same host functions. | `DetMath.Phase`, `FromPolar`, `Sinh(Complex)`, `Tanh(Complex)`. |
| Fused multiply-add | `a * b + c` computed as one fused operation is rounded once instead of twice, so its last bit differs from the separate multiply and add. Whether a fused instruction exists depends on the CPU. | No fused multiply-add in any numerical path. The one fused arm in the code base (`SimdKernels.AddScaledRow` with `fastFma: true`) is selected by no numerical path. |
| Vector width | A reduction such as a dot product, split into as many partial sums as the vector has lanes (8, 4 or 2) and then reduced lane by lane, gives different bits at every width. | One defined summation order that every width reproduces exactly (`SimdKernels.Dot`). |
| Summation and scheduling order | Floating-point addition is not associative, so any sum whose order depends on threads, or on a runtime's sort internals, moves the last bit. | Parallel loops write each item to its own slot and never combine values across threads; ties in a sort that feeds numerics follow a frozen algorithm (`FrozenIntroSort`). |

Operations that IEEE 754 defines exactly stay on `System.Math`: `Math.Sqrt` (correctly rounded), `Abs`, `Floor`,
`Round`, `CopySign`, `ScaleB` and `IEEERemainder`. Every conforming implementation returns the same bits for them.
`Complex` arithmetic, `Complex.Magnitude` and `Complex.Sqrt` are managed .NET code built only from those operations, so
they stay too.

### DetMath

`DetMath` (`src/GridSim.Core/Numerics/DetMath.cs`) is the one front door. `DetMathGlobalUsing.cs` declares
`global using DetMath = GridSim.Core.Numerics.DetMath;`, so every file in GridSim.Core can write `DetMath.Sin(x)`
without importing the rest of the `Numerics` namespace. Each function forwards to the port in the internal class
`Fdlibm`.

| Function | FDLIBM 5.3 source | Notes |
|---|---|---|
| `Sin(x)` | `s_sin.c` 1.3 with `k_sin.c` 1.3 | |
| `Cos(x)` | `s_cos.c` 1.3 with `k_cos.c` 1.3 | |
| `SinCos(x)` | `s_sin.c` and `s_cos.c` sharing one argument reduction | each half is bit-identical to `Sin` and `Cos` called separately (the same statements run) |
| `Tan(x)` | `s_tan.c` 1.3 with `k_tan.c` 1.5 | the 0.96 ulp version of `k_tan`, the one `StrictMath` uses |
| `Asin(x)` | `e_asin.c` | the square root inside is `Math.Sqrt` (correctly rounded) |
| `Acos(x)` | `e_acos.c` | as `Asin` |
| `Atan(x)` | `s_atan.c` | |
| `Atan2(y, x)` | `e_atan2.c` | the angle of (x, y) in (-pi, pi], argument order as `Math.Atan2` |
| `Exp(x)` | `e_exp.c` 1.6 | |
| `Expm1(x)` | `s_expm1.c` 1.5 | e^x - 1, accurate near zero; used by `Sinh` and `Cosh` |
| `Log(x)` | `e_log.c` 1.3 | |
| `Log10(x)` | `e_log10.c` 1.3 | n log10(2) + log10(e) log(x / 2^n), from `Log` |
| `Pow(x, y)` | `e_pow.c` 1.5 | callers write a small whole-number power as a product (x * x is exact and is also what fdlibm returns for x^2) |
| `Sinh(x)` | `e_sinh.c` 1.3 | `Expm1` for \|x\| below 22, `Exp` above |
| `Cosh(x)` | `e_cosh.c` 1.3 | |

The port is line by line. Each C function becomes one C# method with the same name in PascalCase, the same constants
written exactly as the C source writes them (with the source's hexadecimal bit patterns in the comments), the same
statement order and the same branch structure, so it can be read side by side with the C file it cites. fdlibm reads
and writes the two 32-bit halves of a double through pointer casts (`__HI(x)`, `__LO(x)`); the port does it with
`BitConverter.DoubleToInt64Bits` and back, an exact reinterpretation (`Fdlibm.Hi`, `Fdlibm.Lo`, `Fdlibm.Make`). The C
idioms that need a zero the compiler cannot fold (`-two54/zero` for minus infinity, `(x-x)/zero` for NaN) use a
non-constant static `ZeroVar = 0.0`, as fdlibm does. The C `goto recompute` in `k_rem_pio2.c` stays a C# `goto` to the
same point.

**Argument reduction for sin, cos and tan.** `Fdlibm.RemPio2` (`e_rem_pio2.c` 1.4) returns the quadrant count n and the
reduced argument y0 + y1 = x - n pi/2 with \|y0 + y1\| up to pi/4. Medium arguments (\|x\| up to 2^19 pi/2) use
Cody-Waite subtraction of pi/2 split into 33 + 33 + 33 + 53 bits (`Pio2_1`, `Pio2_2`, `Pio2_3` and their tails
`Pio2_1t`, `Pio2_2t`, `Pio2_3t`) with up to three refinement rounds. Larger arguments go to `KernelRemPio2`
(`k_rem_pio2.c` 1.3), Payne-Hanek reduction against the many-digit table of 2/pi (`TwoOverPi`), which leaves the
fraction of x 2/pi with 53 or more good bits.

**Accuracy.** fdlibm's: under 1 ulp for every function, not always correctly rounded. Correct rounding is not what
determinism needs; what it needs is that the rounding is the same everywhere, and plain IEEE 754 arithmetic with no
fused operation and no platform call gives exactly that.

**Proof.** Java's `StrictMath` is defined by the specification as the results of FDLIBM 5.3, so it is an independent
reference. `tools/strictmath/StrictMathVectors.java`, run on Eclipse Temurin 21.0.12.1, wrote
`tests/GridSim.Tests/fixtures/strictmath/strictmath-vectors.txt`: explicit vectors for the special and boundary
arguments of all 14 functions, and SHA-256 digests over 20,000 pseudo-random arguments per argument class (about 1.1
million results). `FdlibmStrictMathVectorTests` regenerates the same arguments with an exact mirror of the Java
generator (SplitMix64 and plain IEEE arithmetic), evaluates `DetMath`, and requires every explicit result and every
digest to match bit for bit.

### The canonical NaN

IEEE 754 does not fix the sign or payload of a NaN that arithmetic produces, and on x64 the bits really do vary: with
two NaN operands an SSE instruction returns its first operand, and the JIT may swap the operands of a commutative
operation at a higher optimisation tier. Measured: a diverged frequency run (inertia floored to 1 MW.s, hardware
intrinsics off) gave `0x7FF8000000000000` before tier-up and `0xFFF8000000000000` after, from the same source.
`DetMath.CanonicalNaN(x)` returns x unchanged except that any NaN becomes the one quiet NaN `CanonicalNaNBits` =
`0x7FF8000000000000`. The frequency result types (`FrequencyTypes`, `FrequencyDynamics`) canonicalise their samples, and
the web map payload's exact-change test canonicalises both sides. Other result types (power flow, estimation, bad
data, cascade, continuation power flow) are not canonicalised; a consumer that compares their raw NaN bits must
canonicalise first. Text output is unaffected: every NaN prints as `NaN`.

### SIMD tiers

`SimdCapabilities` (`SimdCapabilities.cs`) picks the widest vector width the JIT really accelerates on this process,
once, at start-up, from `Vector512.IsHardwareAccelerated`, then `Vector256`, then `Vector128`:

| `SimdTier` | Width | Doubles per step |
|---|---|---:|
| `Avx512` | 512 bits | 8 |
| `Avx256` | 256 bits (AVX, AVX2) | 4 |
| `Sse128` | 128 bits (SSE2 on x64, AdvSimd on ARM64) | 2 |
| `Scalar` | none | 1 |

The choice is made at run time from the hardware flags, never from the build machine, so one binary runs 8 wide on an
AVX-512 server and 4 wide on an AVX2 desktop. `System.Numerics.Vector<T>` is deliberately not used for the width: it
does not reliably widen to 512 bits on AVX-512 hardware. `Avx512Unused` is true when the CPU supports AVX-512F but the
active tier is narrower (Skylake-SP and Cascade Lake Xeons, where .NET does not accelerate `Vector512` by default;
`DOTNET_PreferredVectorBitWidth=512` enables it). `gridsim simdcheck` prints the tier and the flags.

### The vector kernels

`SimdKernels` (`SimdKernels.cs`) holds the shared double primitives of the dense LU trailing update, the
normal-equations build and the dense dot products. Each kernel is written widest first with explicit
`Vector512` / `Vector256` / `Vector128` arms selected by `SimdCapabilities.Tier`, and each has an explicit-tier
overload, the test seam that runs every tier's arm on any machine (`Vector512` then runs in software: slow but exact).

| Kernel | Operation | Bit-exact at every width because |
|---|---|---|
| `SubtractScaledRow` | `dst[t] -= factor * src[t]` | each lane does a separate multiply, then the subtract, exactly the scalar statement |
| `AddScaledRow` with `fastFma: false` | `dst[t] += factor * src[t]` | as above |
| `SubtractScaledComplexColumn`, `SubtractScaledComplexColumns4` | `c[dst] -= c[l] * u` over complex columns, skipping a zero multiplier | each lane computes the four products of the complex multiply, each rounded on its own, then the real part as `l.Re*u.Re + (-(l.Im*u.Im))` (IEEE defines x - y as x + (-y), and negation is exact) and the imaginary part as the same two-term sum; skipping a zero multiplier, as the scalar loop does, keeps a -0 from turning into +0 |
| `Dot` | sum of `x[t] * y[t]` | one defined order, below |
| `SinCos` (batch) | `(sinOut[t], cosOut[t]) = DetMath.SinCos(angles[t])` | the same fdlibm call per element at every width |
| `DotExact` | left-to-right scalar dot | the reference order, kept available |

**The defined dot order.** `SimdKernels.Dot` once summed into as many partials as the vector had lanes, used fused
multiply-add where the CPU had it and reduced the lanes in a width-specific order, so AVX-512, AVX2 and scalar machines
gave different last bits (R279, R113). The order is now fixed, independent of the hardware:

```
s0 = s1 = s2 = s3 = +0
for t = 0 .. 4*floor(count/4) - 1, in increasing t:   s[t mod 4] += x[t] * y[t]     (product rounded on its own)
s = (s0 + s1) + (s2 + s3)
for t = 4*floor(count/4) .. count - 1:                 s += x[t] * y[t]              (the tail, left to right)
```

Every tier performs exactly these IEEE operations. The 256-bit arm holds s0..s3 in one vector. The 512-bit arm
multiplies eight elements at once and adds the low four products, then the high four, into the same four partials,
the same sequence of additions per partial. The 128-bit arm holds s0, s1 in one vector and s2, s3 in another. The
scalar arm holds four doubles. Lane-wise vector add and multiply round exactly as the scalar operations do, so all four
arms return the same bits on every CPU.

### Fused multiply-add policy

No numerical path fuses. `SimdCapabilities.FmaAvailable` reports whether a fused instruction exists (`Fma.IsSupported`
on x64, `AdvSimd.IsSupported` on ARM64), and `SimdKernels.AddScaledRow` keeps an opt-in `fastFma` arm that uses it, as a
tested kernel arm only. Every call of `AddScaledRow` outside the kernel names `fastFma: false` literally, and the
analyser test below fails if a fused call appears anywhere else. .NET's JIT never contracts a separate multiply and add
into a fused instruction on its own, so writing them separately is enough.

### Parallel loops

Every parallel loop in GridSim.Core runs independent items (contingencies, frequency points, harmonic orders, row
blocks of the bad-data quadratic form) and writes each item's result into its own slot of a pre-sized array. No
floating-point value is combined across threads, so the scheduling order cannot reach a result. Where a parallel loop
can fail, `DeterministicParallel.For` (`DeterministicParallel.cs`) keeps each item's exception by its index and, after
the loop, rethrows the one with the lowest failing index, unwrapped and with its original stack trace: the same
exception, of the same type, whether the items ran in parallel or not (`Parallel.For` would wrap one or several
exceptions in an `AggregateException` in the order the workers happened to fail). The harmonic sweep, the harmonic
impedance scan and the harmonic penetration study use it.

LINQ's `Enumerable.Sum` over doubles is used in places. It is deterministic only while the runtime keeps floating-point
`Sum` sequential (.NET vectorises `Sum` for integers only), and a test pins that assumption in every SIMD mode.

### The frozen sort

An introspective sort is not stable: when the comparer calls two elements equal (two nodes of the same degree in the
reverse Cuthill-McKee ordering) the order between them comes from the algorithm's internals, and those belong to the
runtime. A .NET release that tuned them would change the RCM permutation, hence the pivot order of every sparse LU,
hence the last bits of every sparse result, with no GridSim change at all. `FrozenIntroSort` (`FrozenIntroSort.cs`) is
GridSim's own copy of the .NET 10 `ArraySortHelper<T>` introsort, step for step: insertion sort at or below 16
elements (`IntrosortSizeThreshold`), median-of-three quicksort, heapsort past the depth limit
`2 * (Log2(length) + 1)`. On .NET 10 it gives exactly the order `Array.Sort` gives today, so no result moved when it
was introduced; it is used wherever a sort with ties feeds numerics. New code should prefer a total-order key with no
ties.

### Complex helpers

`DetMath.Phase(z)` is `Atan2(z.Imaginary, z.Real)`, which is what `Complex.Phase` computes, through the deterministic
`Atan2`. `DetMath.FromPolar(m, p)` is `(m cos p, m sin p)` through `SinCos`. `DetMath.Sinh(Complex)` evaluates
sinh(a + ib) = sinh a cos b + i cosh a sin b, and `DetMath.Tanh(Complex)` evaluates
tanh(a + ib) = (sinh 2a + i sin 2b) / (cosh 2a + cos 2b); when cosh 2a overflows (\|a\| above about 355) the identity
gives inf / inf, and the exact limit sign(a) + i 0 is returned instead, the imaginary zero carrying the sign of sin 2b.
`Complex2x2` (`Complex2x2.cs`) is the 2 x 2 complex matrix of the impedance-stability screen: product, adjugate
inverse and closed-form eigenvalues, built from `Complex` arithmetic and `Complex.Sqrt`, which are exact-rounding .NET
code.

### The true-residual check

`TrueResidual.Verified(A, b, x, tol)` (`TrueResidual.cs`) is the success check every Krylov solver applies before it
returns a solution. BiCGSTAB's residual is updated by recurrence and drifts away from b - A x in finite precision,
worst on the ill-conditioned systems that reach an iterative fallback. The check forms A x with one more product and
returns x only when sqrt(sum (b - A x)^2) / sqrt(sum b^2) is below `tol`; otherwise null, the caller's failure path. A
zero right-hand side passes only when A x is zero to the bit. The sums run left to right in index order.

### The tolerances register

`NumericTolerances` (`NumericTolerances.cs`) is the one table of numerical thresholds shared by the linear solvers, the
eigen solvers and the estimator. Each constant names one concept, says what it is relative to, and is the only place
that value lives. A pivot floor decides only what is declared singular; it never enters the arithmetic of a
non-singular solve, so moving a site onto the register left every converged answer on a non-singular system
bit-for-bit unchanged.

## Algorithm

### How the rules are enforced

`DeterministicMathGuardTests` is an analyser written as a test. It reads the compiled `GridSim.Core.dll`, and the
GridSim.Gda, GridSim.Gpu and CLI assemblies, with `System.Reflection.Metadata`, decodes the IL of every method body, and
fails if any call or method-pointer load targets:

- a transcendental member of `System.Math`, `System.MathF`, `System.Double` or `System.Single` (`Sin`, `Cos`, `Tan`,
  `SinCos`, `Asin`, `Acos`, `Atan`, `Atan2`, `Exp`, `Log`, `Log10`, `Log2`, `Pow`, `Sinh`, `Cosh`, `Tanh`, the inverse
  hyperbolics, `Cbrt`, the `Pi` variants, `Exp2`, `Exp10`, `ExpM1` and the `P1` logarithms, `Hypot`, `RootN`, the
  reciprocal estimates, `FusedMultiplyAdd`, `MultiplyAddEstimate`);
- a `System.Numerics.Complex` member built on them (`Phase`, `FromPolarCoordinates`, `Exp`, `Log`, `Log10`, `Pow`,
  `Sin`, `Cos`, `Tan`, `Asin`, `Acos`, `Atan`, `Sinh`, `Cosh`, `Tanh`);
- on `Vector`, `Vector64`, `Vector128`, `Vector256` or `Vector512`: `FusedMultiplyAdd`, `MultiplyAddEstimate`, the
  horizontal reductions `Sum` and `Dot`, the vector transcendentals, the reciprocal estimates and `Lerp` (which may be
  lowered to a fused multiply-add);
- anything in `System.Numerics.Tensors.TensorPrimitives`.

The one allowed exception is `FusedMultiplyAdd` inside `SimdKernels.AddScaledRow`. Working on IL rather than source text
means the property form (`z.Phase`), a fully qualified name or a `using static` cannot slip past. A companion test
proves the scanner sees the calls it must see, a source check requires every `AddScaledRow` call outside the kernel to
pass a literal `fastFma: false`, and another fails if a numerical path selects the fused arm.

### How the tiers are tested

Two layers. In one process, the explicit-tier seams run every kernel arm (`Scalar`, `Sse128`, `Avx256`, `Avx512`) on
the same inputs and require exact equality across the four and with a plain scalar statement of the defined
arithmetic (`D1CrossTierTests`). Across processes, CI runs the whole suite as a matrix, and the tests pin exact values,
so a result that differs on any leg fails that leg:

| Leg | Setting | What it exercises |
|---|---|---|
| normal | none | whatever the runner offers (often AVX-512 or AVX2) |
| avx2-off | `DOTNET_EnableAVX2=0` | the SSE / 128-bit arms; on .NET 10 this also turns FMA off (the whole x86-64-v3 group), and the leg's preflight requires `Fma=False`, so it holds the no-FMA axis |
| hwintrinsic-off | `DOTNET_EnableHWIntrinsic=0` | the scalar fallback arms |
| avx512 | `DOTNET_PreferredVectorBitWidth=512` and `simdcheck --expect Avx512` | the 8-wide arms; the leg fails on a runner without AVX-512 rather than testing a narrower tier |
| avx256 | `DOTNET_PreferredVectorBitWidth=256`, preflight must report `Avx256` | the 4-wide arms, even on an AVX-512 runner |

Linux (`ubuntu-latest`) and Windows (`windows-latest`) run normal, avx2-off and hwintrinsic-off, and the avx512 and
avx256 legs. macOS on Apple silicon (`macos-latest`), Linux ARM64 (`ubuntu-24.04-arm`) and Windows ARM64
(`windows-11-arm`) run normal and hwintrinsic-off against the same recorded pins. No leg is advisory.

## Parameters and defaults

| Name | Value | Meaning | Source symbol |
|---|---|---|---|
| Canonical NaN | `0x7FF8000000000000` | the one quiet NaN GridSim reports where it canonicalises | `DetMath.CanonicalNaNBits` |
| Medium-argument reduction limit | \|x\| up to 2^19 pi/2 | Cody-Waite below, Payne-Hanek above | `Fdlibm.RemPio2` |
| pi/2 split | 33 + 33 + 33 + 53 bits | `Pio2_1` = 1.57079632673412561417, `Pio2_1t` = 6.07710050650619224932e-11, and so on | `Fdlibm.Pio2_*` |
| 2/pi to 53 bits | 6.36619772367581382433e-01 | quadrant estimate | `Fdlibm.Invpio2` |
| `exp` overflow / underflow thresholds | 709.782712893383973096 / -745.133219101941108420 | above / below: +inf / 0 | `Fdlibm.O_threshold`, `U_threshold` |
| `sinh` switch | \|x\| below 22 | `Expm1` below, `Exp` above | `Fdlibm.Sinh` |
| `Tanh(Complex)` limit | cosh 2a infinite, 2b finite | return sign(a) + i 0 | `DetMath.Tanh` |
| Vector widths | 8 / 4 / 2 / 1 doubles | `Avx512` / `Avx256` / `Sse128` / `Scalar` | `SimdCapabilities.DoubleLanes` |
| Dot partial sums | 4, combined `(s0 + s1) + (s2 + s3)` | the defined order at every width | `SimdKernels.Dot` |
| Insertion-sort threshold | 16 | as the .NET 10 introsort | `FrozenIntroSort.IntrosortSizeThreshold` |
| Introsort depth limit | `2 * (Log2(length) + 1)` | heapsort past it | `FrozenIntroSort.Sort` |
| Machine epsilon | 2.220446049250313e-16 | 2^-52 | `NumericTolerances.MachineEpsilon` |
| Relative singular-pivot floor | 1e-12 | times the largest \|A_ij\| of the original matrix | `NumericTolerances.RelativePivotFloor` |
| Underflow guard | 1e-300 | | `NumericTolerances.UnderflowGuard` |
| Default Krylov tolerance | 1e-10 (real), 1e-12 (complex) | relative true residual | `NumericTolerances.DefaultKrylovTolerance`, `DefaultComplexKrylovTolerance` |
| Jacobi sweeps / off-diagonal tolerance | 100 / machine epsilon | symmetric eigen solver | `NumericTolerances.JacobiMaxSweeps`, `JacobiRelativeOffTolerance` |
| Eigen cluster, parallel and residual tolerances | 1e-6 each | relative | `NumericTolerances.EigenClusterRelativeTolerance`, `EigenParallelTolerance`, `EigenResidualRelativeTolerance` |

## Invariants

| Invariant | Pinned by |
|---|---|
| Every `DetMath` function equals Java `StrictMath` bit for bit on the explicit special and boundary vectors | `FdlibmStrictMathVectorTests.Explicit_vectors_match_Java_StrictMath_bit_for_bit` |
| ... and on the SHA-256 digests of about 1.1 million pseudo-random results | `FdlibmStrictMathVectorTests.Pseudo_random_digests_match_Java_StrictMath_bit_for_bit` |
| `SinCos` halves are bit-identical to `Sin` and `Cos` | `FdlibmStrictMathVectorTests.SinCos_halves_are_bit_identical_to_Sin_and_Cos` |
| The complex helpers are the documented formulas over the deterministic functions | `FdlibmStrictMathVectorTests.Complex_helpers_are_the_documented_formulas_over_the_deterministic_functions` |
| No platform-dependent maths, fused multiply-add or horizontal vector reduction in Core, Gda, Gpu or the CLI outside the allowed arm | `DeterministicMathGuardTests.GridSim_Core_Gda_Gpu_and_Cli_call_no_platform_dependent_maths_outside_the_deterministic_module`, `The_scanner_sees_calls_it_must_see` |
| No numerical path selects the fused arm; every `AddScaledRow` call names `fastFma: false` | `DeterministicMathGuardTests.No_numerical_path_selects_the_fused_multiply_add_arm`, `Every_AddScaledRow_call_outside_the_kernel_names_fastFma_false` |
| `Enumerable.Sum` over doubles adds left to right in this runtime and SIMD mode | `DeterministicMathGuardTests.Enumerable_Sum_over_doubles_adds_left_to_right_in_this_runtime_and_mode` |
| `Dot` is bit-identical at every tier and equals the defined order | `D1CrossTierTests.Dot_is_bit_identical_at_every_tier_and_equals_the_defined_order` |
| Batch `SinCos` is bit-identical at every tier and to `DetMath.SinCos` per element | `D1CrossTierTests.Batch_SinCos_is_bit_identical_at_every_tier_and_to_DetMath_for_every_element` |
| The dense WLS gain and the classical swing trajectory are bit-identical at every tier | `D1CrossTierTests.Dense_WLS_gain_is_bit_identical_at_every_tier_and_has_no_fused_multiply_add`, `Classical_swing_trajectory_is_bit_identical_at_every_tier` |
| The formerly tier-dependent paths give the pinned SHA-256 digests in every mode | `D1CrossTierTests.Formerly_tier_dependent_paths_give_the_pinned_bits_in_every_mode` |
| Every NaN canonicalises to one bit pattern, other values untouched; a diverged frequency run reports it however warm the JIT is | `CanonicalNaNTests.Every_nan_becomes_the_one_canonical_nan_and_everything_else_is_untouched`, `Frequency_points_and_responses_store_nan_canonically_through_the_constructor_and_with`, `A_diverged_frequency_run_reports_the_canonical_nan_however_warm_the_jit_is` |
| The frozen sort reproduces `Array.Sort` on .NET 10 and pins tie orders | `FrozenIntroSortTests.On_dotnet_10_it_reproduces_array_sort_exactly_on_tie_heavy_inputs`, `Tied_elements_land_in_the_pinned_order`, `Range_sort_touches_only_the_range` |

## Worked example

### The host library rounds differently

Take x = pi/4 as a double, bit pattern `0x3FE921FB54442D18`. The reference vectors hold Java `StrictMath.sin` of it,
which `DetMath.Sin` reproduces:

```
x                    0x3FE921FB54442D18   0.7853981633974483
DetMath.Sin(x)       0x3FE6A09E667F3BCC   0.7071067811865475   (= StrictMath, fdlibm)
Windows C runtime    0x3FE6A09E667F3BCD   0.7071067811865476   (one ulp higher)
```

The C-runtime figure was measured on Windows 11 through Python's `math` module, which calls the Windows C runtime.
Over the explicit vectors in the fixture, that runtime disagreed with fdlibm in 237 of 3,778 results of the twelve
one-argument functions: `sin` 10 of 408, `cos` 6 of 408, `tan` 10 of 408, `asin` 4 of 184, `acos` 2 of 184, `atan` 0 of
414, `exp` 27 of 362, `expm1` 54 of 362, `log` 7 of 208, `log10` 17 of 208, `sinh` 58 of 316 and `cosh` 42 of 316. Neither side is wrong (both are within fdlibm's 1 ulp); they are different, and an iterative solver
carries a one-ulp difference forward into every later step.

### Summation order decides the answer

Take x = (1e16, 1, -1e16, 1, 3, 1) and y all ones. The exact dot product is 6. At 1e16 the spacing of doubles is 2, so
the order of the additions decides what survives:

```
left to right (DotExact)      ((((1e16 + 1) - 1e16) + 1) + 3) + 1        = 5   0x4014000000000000
defined order (Dot)           s = (1e16, 1, -1e16, 1)
                              (1e16 + 1) + (-1e16 + 1) = 0, then + 3 + 1 = 4   0x4010000000000000
```

Neither is 6. The point is that `Dot` returns 4 at every width on every CPU, because the order is part of its
definition, and a lane-count-dependent order would not.

### The tier on this machine

`gridsim simdcheck` on an 8-core Windows laptop, then with the two environment switches the CI legs use:

```
SIMD: Avx256 (4 doubles/step)  FMA=yes  [Avx512F=False Avx2=True Fma=True Sse2=True AdvSimd=False]
SIMD: Sse128 (2 doubles/step)  FMA=no   [Avx512F=False Avx2=False Fma=False Sse2=True AdvSimd=False]    DOTNET_EnableAVX2=0
SIMD: Scalar (1 doubles/step)  FMA=no   [Avx512F=False Avx2=False Fma=False Sse2=False AdvSimd=False]  DOTNET_EnableHWIntrinsic=0
```

The same suite, with the same pinned bits, passes in all three.

## Limits

- `DetMath` is accurate to under 1 ulp, not correctly rounded. Results agree with Java `StrictMath`, not necessarily
  with the closest double or with another library.
- The NaN is canonicalised only in the frequency result types and the web map payload's change test; other result
  types can carry NaNs of either sign.
- `Enumerable.Sum` over doubles is deterministic by the runtime's present behaviour, which a test pins; a runtime that
  vectorised it would fail that test rather than silently move numbers.
- `FrozenIntroSort` freezes the .NET 10 order; it does not make an unstable sort stable. Ties still land where that
  algorithm puts them.
- GPU results are outside the bit-identity contract: device reductions and elimination order differ from the CPU's
  ([ch. 4](<0004 Sparse and GPU numerics.md>), Limits).
- The cross-tier proof in one process covers the kernels and paths named above; every other path relies on the CI
  matrix legs, which need GitHub Actions to run.
- Bit-identity across CPU and OS holds for one build of GridSim on .NET 10. A different .NET major version is a
  different compiler and runtime, and the pinned values are re-proved on it before they are trusted.

## See also

- [MATH.md](../../MATH.md) - the short overview.
- [ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) - the solvers that use these kernels, and the GPU paths outside the contract.
- [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) - the invariants that rest on bit-identity.
- [ch. 11, Validation](<0011 Validation.md>) - agreement with independent tools at machine epsilon.
- [ch. 36, Known limitations and design decisions](<0036 Known limitations and design decisions.md>).
- `THIRD-PARTY-NOTICES.md` - the FDLIBM notice.
