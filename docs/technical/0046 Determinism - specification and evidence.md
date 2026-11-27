# 46. Determinism: specification and evidence

This chapter is the specification of GridSim's determinism contract: the axes it covers, the mechanism that holds each
axis, the tests that pin it, the exceptions, and what the author has observed on real hardware. It does not repeat the
implementation chapters; it indexes them. The mechanisms are in
[ch. 38, Deterministic mathematics](<0038 Deterministic mathematics.md>); the solver and GPU numerics are in
[ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>); what the code delivers per axis, with the worked hash
example, is in [ch. 8, "Determinism and bit-identity as implemented"](<0008 Provenance and invariants.md#determinism-and-bit-identity-as-implemented>).
The reviewer-facing summary is [Understanding GridSim determinism](<../Understanding GridSim determinism.md>), and the
user-level chapter is [user manual ch. 36](<../manual/0036 Determinism and reproducibility.md>).

## Model

**The contract.** For one build of GridSim on one .NET major version, the same input and options produce bit-identical
output. "Bit-identical" means equal IEEE 754 binary64 values, compared with exact equality, not within a tolerance.

**Verified, not validated.** The core is verified against the mathematics and independent references to machine epsilon
([ch. 11](<0011 Validation.md>)). Determinism makes that verification repeatable; it does not validate the model against
measurements of a real grid at scale.

**The accepted exception to the contract** is a mathematical proof that a reference implementation elsewhere is wrong.
A change that only reorganises orchestration leaves every number unchanged to the last bit.

### The axes

| Axis | Held by | Pinned by |
|---|---|---|
| Run to run | no state, clock or random input in a solve | `SolverPropertyTests.Solving_twice_is_bit_for_bit_identical` |
| Parallel vs sequential | one solver per worker, fixed result slots, stable sort on a pure score | `ContingencyParallelBitIdentityTests` |
| Worker count | the same | `ContingencyParallelBitIdentityTests` at 1, 2 and 4 workers |
| CPU tier | bit-exact kernels, one defined reduction order, no FMA | `SimdTierSeamTests`, `D1CrossTierTests`, the CI legs |
| FMA | `fastFma: false` at every numerical caller | `D1CrossTierTests.Dense_WLS_gain_is_bit_identical_at_every_tier_and_has_no_fused_multiply_add`, `DeterministicMathGuardTests` |
| Operating system and C library | `DetMath`, a port of FDLIBM 5.3 | the CI matrix on Linux, Windows and macOS |
| Dense vs sparse Newton-Raphson | the shared `NewtonShell` step and insertion-order sums | `DenseSparseParityTests` |
| Culture | the CLI pins the invariant culture; formatting sites pass it explicitly | `BugHunt1101WebTests.B193_*` |
| Toolchain | exact SDK pin, NuGet lock files, recorded commit and SBOM hash | `BugHunt1101WebTests.B161_global_json_pins_one_exact_sdk_with_no_roll_forward` |

## Mechanism, axis by axis

### Runs and threads

The parallel and sequential screens call the same per-outage function (`ContingencyAnalysis.ScreenOutageCore`,
`LossContingency.ScreenOne`). A parallel sweep gives each worker its own solver and writes each result into a fixed slot
of the result array, so the thread schedule cannot change which result lands where. Ranked lists sort by a pure score
with a stable sort. The deterministic parallel loop and the frozen sort are specified in
[ch. 38](<0038 Deterministic mathematics.md>), sections "Parallel loops" and "The frozen sort".

### Reduction order

Every reduction has one defined order that all tiers reproduce. `SimdKernels.Dot` keeps four partial sums, then the tail,
never fused. The bit-exact row kernels (`SubtractScaledRow`, `AddScaledRow` with `fastFma: false`) do a separate
multiply and add per lane. Sparse rows sum in insertion order by the unique sort key `(column << 32) | insertionIndex`
([ch. 4](<0004 Sparse and GPU numerics.md>), "CSR storage and the builders"). Newton-Raphson uses only the CPU sparse
LU, which calls no `SimdKernels` and no explicit FMA, so its results do not depend on the tier.

### Fused multiply-add and the maths library

The FMA policy is in [ch. 38, "Fused multiply-add policy"](<0038 Deterministic mathematics.md>): the fused arm exists
only behind an opt-in flag that no numerical caller sets. Elementary functions come from `DetMath` (including the batch
`SimdKernels.SinCos`, which is `DetMath` per element), not from the operating system's library, and NaNs are
canonicalised in the frequency result types. `DeterministicMathGuardTests` is an IL-level analyser: it fails the test run
if a numerical method calls a fused multiply-add, a horizontal vector reduction or a platform `Math` transcendental.

### CPU tiers

`SimdCapabilities.Tier` selects `Avx512`, `Avx256`, `Sse128` or `Scalar` kernels once per process. Dispatch is by
instruction-set capability, never by vendor (the vendor exception is Zen 1: see the platform observations). In one process, the explicit-tier seams run every kernel arm on the same
inputs and require exact equality across the four and with a plain scalar statement of the arithmetic
(`SimdTierSeamTests`, `D1CrossTierTests`). Across processes, CI runs the whole suite as a matrix of exact-value pins:

| Leg | Setting |
|---|---|
| normal | none |
| avx2-off | `DOTNET_EnableAVX2=0` (on .NET 10 this also turns FMA off) |
| hwintrinsic-off | `DOTNET_EnableHWIntrinsic=0` |
| avx512 | `DOTNET_PreferredVectorBitWidth=512`, `simdcheck --expect Avx512` |
| avx256 | `DOTNET_PreferredVectorBitWidth=256`, preflight must report `Avx256` |

Linux and Windows run all five legs. The avx512 leg needs a runner with AVX-512; on a runner without it the leg reports
NOT CHECKED and its tests are skipped. macOS on Apple silicon, Linux ARM64 and Windows ARM64 run normal and
hwintrinsic-off against the same recorded pins. No leg is advisory
([ch. 38, "How the tiers are tested"](<0038 Deterministic mathematics.md>)).

### Culture

`Program.cs` sets `DefaultThreadCurrentCulture` and `CurrentCulture` to `InvariantCulture` before any verb runs, so a
comma-decimal machine cannot change how a flag is parsed or a number written. Result CSV is invariant-culture
([ch. 19](<../manual/0019 CLI reference.md>), "Result files"). The `BugHunt1101WebTests.B193_*` tests run the web
host's formatting sites under `de-DE`.

### Toolchain and provenance

`global.json` pins one exact SDK with `rollForward: disable`. NuGet restores from lock files, and on CI in locked mode.
The binary records its commit in `InformationalVersion` (`gridsim --version` prints `gridsim <version>+<commit>`) and its
SBOM's SHA-256 through `BuildProvenance`, whose build hash is the same for the same commit, SDK and SBOM on any machine.
The release workflow rebuilds the Linux CLI on a fresh runner and compares it byte for byte with the shipped archive
([ch. 35](<0035 Deployment and security.md>)).

### Across versions

Every change that moves a result is labelled `BREAKING CHANGE (Bnn)` in `CHANGELOG.md`, with a magnitude. The
machine-readable record is `src/GridSim.Core/Journal/breaking-changes.json`: each entry names its scope (verbs, options,
input formats), its claimed effect on the fields it moves (a bound, an exact transform or a qualitative transition) and a
map to the tests that pin it. `breaking-change validate` explains a re-run difference only when a recorded change lies
between the recorded version and the current build and its claim covers the field
([ch. 42](<0042 Run journal and breaking-change validation.md>)). IDs are never reused.

## Evidence

| Claim | Test | Where |
|---|---|---|
| Solves repeat to the bit | `SolverPropertyTests.Solving_twice_is_bit_for_bit_identical` | ch. 8 |
| Parallel = sequential, 1, 2, 4 workers | `ContingencyParallelBitIdentityTests` | ch. 8 |
| Parallel enumeration is deterministic | `ContingencyTests.N2_enumeration_is_deterministic_despite_the_parallel_solve`, `N1_1_is_deterministic_despite_the_parallel_solve` | ch. 8 |
| Default (sparse) engine = dense engine in screens | `DefaultEngineRoutingTests.Contingency_screens_default_engine_matches_the_dense_engine_bit_for_bit` | ch. 8 |
| Dense NR = sparse NR | `DenseSparseParityTests` | ch. 4, ch. 8 |
| CPU WLS does not depend on bus count | `CanonicalWlsParityTests` | ch. 8 |
| Kernels bit-exact at every tier | `SimdTierSeamTests.SubtractScaledRow_is_bit_exact_vs_scalar_at_every_tier`, `AddScaledRow_without_fma_is_bit_exact_vs_scalar_at_every_tier` | ch. 8, ch. 38 |
| Dense WLS gain, Dot, SinCos, swing trajectory identical at every tier | `D1CrossTierTests` (`Dense_WLS_gain_is_bit_identical_at_every_tier_and_has_no_fused_multiply_add`, `Dot_is_bit_identical_at_every_tier_and_equals_the_defined_order`, `Batch_SinCos_is_bit_identical_at_every_tier_and_to_DetMath_for_every_element`, `Classical_swing_trajectory_is_bit_identical_at_every_tier`) | ch. 38 |
| No FMA, horizontal reduction or platform transcendental in numerical code | `DeterministicMathGuardTests` | ch. 38 |
| Insertion-order sums in long rows | `Doc6CsrStableOrderTests` | ch. 4, ch. 8 |
| GPU gives-up falls back to the exact CPU LU | `GpuFallbackTests.A_backend_that_always_gives_up_falls_back_to_the_exact_cpu_lu_bit_for_bit` | ch. 4, ch. 8 |
| SDK pinned exactly, restore locked | `BugHunt1101WebTests.B161_global_json_pins_one_exact_sdk_with_no_roll_forward` | ch. 35 |
| Formatting independent of machine culture | `BugHunt1101WebTests.B193_*` | this chapter |
| Breaking-change map is consistent and its claims hold | `BreakingChangeMapTests`, `BreakingChangeClaimTests`, `BreakingChangeCensusTests` | ch. 42 |

## Exceptions

- **The `cycle` verb and the live web cycle** admit Low-priority jobs (rotating N-1 shards, the RoCoF screen, the physics
  audit) against a wall-clock budget of `workers x window-ms`. Which jobs run depends on speed, load and core count. Each
  job that runs is deterministic; the job set is not. Run the study directly to pin it.
- **Loss allocation in parallel** agrees with the sequential run within 1e-6 MW
  (`LossAllocationTests.Parallel_agrees_with_sequential`), not to the bit.
- **GPU.** Deterministic (the same build and device repeat) but outside bit-identity: natural-order dense elimination,
  Jacobi-preconditioned BiCGSTAB at `1e-10`, a different reduction order, and a device that fuses multiply-add. Measured
  envelope, RTX A4000, 27 MATPOWER cases: 1.45e-12 pu and 5.2e-11 degrees at most
  ([ch. 4](<0004 Sparse and GPU numerics.md>), "GPU accuracy"). The 4096-epsilon cross-check gate does not apply to GPU paths.
- **Dense vs sparse WLS.** The two steps factorise differently. A CPU plain WLS or robust estimate always takes the sparse
  step (`CanonicalWlsStep`); the dense step solves an estimate only with a GPU linear solver, and the constrained,
  feasible and QR methods are dense only.
- **`DetMath` is accurate to under 1 ulp**, not correctly rounded; it agrees with Java `StrictMath`.
- **Other .NET major versions** are a different compiler and runtime; the pins are re-proved before they are trusted.
- **Unstable sorts.** `FrozenIntroSort` freezes the .NET 10 order; ties land where that algorithm puts them.
- **Files that record wall time.** The `summary.md` of `emt` and `transient` and `planning-study`'s `evidence.json`
  (job timings) record elapsed time, so they differ between two identical runs (`ReplayPolicy` lists them as
  run-dependent). Hash the result JSON or compare the result tables instead.

## Platform observations

These are the author's observations, not test-pinned. The CI matrix above is the pinned part.

GridSim has been built and run on Windows 10, Windows 11, macOS, Android and Linux (Debian-family distributions), on
64-bit operating systems only.

| Hardware | Observation |
|---|---|
| 64-bit Arm (ARMv8-A and later) | deterministic |
| Apple Silicon | deterministic; M1 only so far |
| Intel | deterministic on every 64-bit CPU from 9th-generation Core onwards that was tested |
| AMD Ryzen | deterministic on every Ryzen tested |
| AMD Zen 1 | deterministic run to run on that CPU, but not bit-identical to the machine-epsilon reference results; the cause is not yet identified |
| AMD Bulldozer family, Opteron | runs GridSim |
| AMD before Bulldozer | does not run GridSim |

Why older AMD CPUs do not run GridSim is not yet identified. GridSim sets no instruction-set requirement of its own (no
ReadyToRun or native-AOT instruction-set setting in any project), and the .NET 10 runtime's x64 baseline is x86-64-v1.
The x86-64-v2 baseline (SSE4.2 and POPCNT), which pre-Bulldozer AMD CPUs lack, arrives with .NET 11; a move to .NET 11
would make this boundary a stated runtime requirement.

The summary: not yet validated in every environment, but behaviour is broadly consistent, with machine-epsilon agreement,
across a very wide range of hardware so far. The Zen 1 result is the one open discrepancy.

## See also

- [Understanding GridSim determinism](<../Understanding GridSim determinism.md>) - the reviewer-facing summary.
- [User manual ch. 36, Determinism and reproducibility](<../manual/0036 Determinism and reproducibility.md>)
- [ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>)
- [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md#determinism-and-bit-identity-as-implemented>)
- [ch. 35, Deployment and security](<0035 Deployment and security.md>)
- [ch. 38, Deterministic mathematics](<0038 Deterministic mathematics.md>)
- [ch. 42, Run journal and breaking-change validation](<0042 Run journal and breaking-change validation.md>)
