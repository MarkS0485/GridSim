# Deterministic mathematics in GridSim

GridSim gives the same answer to the last bit on every run, on Windows, Linux and macOS, and on every CPU it supports:
AVX-512, AVX2, SSE or no vector unit at all, x64 or ARM64. Not "close to machine epsilon": the same bits. That is what
lets a result be pinned exactly in a test, compared against another machine byte for byte, and trusted when an
iterative solver runs thousands of steps.

The platform does not give you this for free. This page says why, what GridSim does instead, and how to keep it that
way in new code. The full specification is
[technical chapter 38, Deterministic mathematics](<docs/technical/0038 Deterministic mathematics.md>).

## Why the platform is not enough

- **`Math.Sin`, `Math.Exp` and friends are not .NET's own.** They call the operating system's C maths library, and
  Windows, glibc and Apple's libm round some results differently in the last place. Example: sin(pi/4) is
  `0x3FE6A09E667F3BCC` from fdlibm and `0x3FE6A09E667F3BCD` from the Windows C runtime.
- **Fused multiply-add** rounds `a * b + c` once instead of twice, and only some CPUs have it.
- **Vector width** changes the order of a sum: 8 lanes, 4 lanes and 2 lanes add the same numbers in different orders,
  and floating-point addition is not associative.
- **Threads and sort internals** change orders too, if any value is combined across threads or a sort breaks ties by
  runtime internals.

## What GridSim does

1. **Its own elementary functions.** `DetMath` (`src/GridSim.Core/Numerics/DetMath.cs`) is a line-by-line C# port of
   Sun's FDLIBM 5.3, the library Java's `StrictMath` is defined by: `Sin`, `Cos`, `SinCos`, `Tan`, `Asin`, `Acos`,
   `Atan`, `Atan2`, `Exp`, `Expm1`, `Log`, `Log10`, `Pow`, `Sinh`, `Cosh`, plus the complex `Phase`, `FromPolar`, `Sinh`
   and `Tanh`. It is plain IEEE 754 arithmetic and integer bit work, so it gives the same bits everywhere. It is checked
   bit for bit against Java 21 `StrictMath` on special values and about 1.1 million pseudo-random arguments.
2. **No fused multiply-add** in any numerical path.
3. **One defined order for every reduction.** The vector kernels (`SimdKernels`) pick AVX-512, AVX2, SSE or scalar at
   run time, and each arm performs exactly the same IEEE operations in the same order. The dot product, for example,
   always sums into four partials, combines them as `(s0 + s1) + (s2 + s3)`, then adds the tail left to right.
4. **No cross-thread arithmetic, no runtime-dependent ties.** Parallel loops write each item to its own slot;
   `DeterministicParallel.For` rethrows the lowest failing item's exception; `FrozenIntroSort` freezes the sort order
   that feeds the sparse LU's ordering. Any NaN that is output passes through `DetMath.CanonicalNaN`.

Exactly rounded operations stay on `System.Math`: `Sqrt`, `Abs`, `Floor`, `Round`, `CopySign`, `ScaleB`,
`IEEERemainder`.

## Writing new code

- Call `DetMath.Sin(x)`, never `Math.Sin(x)`; likewise for every function listed above. `DetMath` is a global alias
  in GridSim.Core, so no `using` is needed.
- Use `DetMath.Phase(z)` and `DetMath.FromPolar(m, p)`, never `z.Phase` or `Complex.FromPolarCoordinates`.
- Write `x * x`, not `Pow(x, 2)`.
- Never use `Math.FusedMultiplyAdd`, `Vector*.FusedMultiplyAdd`, `Vector*.Sum` or `Vector*.Dot`. Call
  `SimdKernels.AddScaledRow` with a literal `fastFma: false`.
- Do not combine floating-point values across threads; give each item its own output slot.
- If a sort with ties feeds numerics, use a total-order key, or `FrozenIntroSort`.

## How it is enforced and tested

- `DeterministicMathGuardTests` reads the compiled assemblies (Core, Gda, Gpu and the CLI), decodes every method's IL,
  and fails the test run on any call to a platform transcendental, a fused multiply-add or a horizontal vector
  reduction. Because it reads IL, a property, a qualified name or a `using static` cannot hide a call.
- `FdlibmStrictMathVectorTests` proves `DetMath` equals Java `StrictMath` bit for bit.
- `D1CrossTierTests` runs every vector arm on the same input in one process and requires exactly equal results.
- CI runs the whole suite, with exact pins, on Linux and Windows in normal, AVX2-off (also FMA off), hardware-intrinsics
  off, forced AVX-512 and forced 256-bit modes, and on macOS, Linux and Windows ARM64. `gridsim simdcheck` shows
  which tier your machine runs.

GPU solvers are outside this contract (device reductions run in a different order); see
[technical chapter 4](<docs/technical/0004 Sparse and GPU numerics.md>).
