# Understanding GridSim determinism

**What GridSim guarantees about reproducible results, what is pinned by a test, what the author has only observed, and what is outside the guarantee.**

GridSim's acceptance criterion for a result is bit-identity: the same case, the same options and the same build give the
same bits. This page answers the five questions a reviewer asks about that claim, one short answer each, with the tests
and chapters that carry the evidence. "Verified" here means checked against the mathematics or an independent reference
to machine epsilon. GridSim is verified in that sense. It has not been validated against the real grid at scale, and
this page makes no claim that it has.

The full specification is [technical ch. 46](<technical/0046 Determinism - specification and evidence.md>). The user-level
version is [user manual ch. 36](<manual/0036 Determinism and reproducibility.md>).

## 1. Deterministic across what?

Across runs, worker counts, CPU instruction-set tiers and operating systems, for one build of GridSim on .NET 10.

- **Runs.** A Newton-Raphson solve repeats to the bit (`SolverPropertyTests.Solving_twice_is_bit_for_bit_identical`).
- **Worker counts.** A parallel contingency screen at 1, 2 and 4 workers equals the sequential screen bit for bit
  (`ContingencyParallelBitIdentityTests`).
- **CPU tiers.** The AVX-512, AVX2, SSE and scalar paths give the same bits (`D1CrossTierTests`, and the CI legs below).
- **Operating systems.** Windows, Linux and macOS run the same exact-value pins in CI. The transcendental functions come
  from GridSim's own library, not the operating system's.
- **Vendors and architectures.** x64 and 64-bit Arm run the same pins, except first-generation AMD Zen (Zen 1, below). Kernel dispatch is by instruction-set tier, never by CPU vendor.
- **Compiler and runtime.** Not across versions. A different .NET major version is a different compiler and runtime, and
  the pinned values are re-proved on it before they are trusted (question 5).

Evidence: [ch. 8, "Determinism and bit-identity as implemented"](<technical/0008 Provenance and invariants.md#determinism-and-bit-identity-as-implemented>);
[ch. 38](<technical/0038 Deterministic mathematics.md>).

## 2. Does it depend on thread scheduling or reduction order?

No. Each worker owns its solver, each result is written into a fixed slot of the result array, and ranked lists use a
stable sort on a pure score, so the thread schedule cannot change which result lands where
(`ContingencyParallelBitIdentityTests`, `ContingencyTests.N2_enumeration_is_deterministic_despite_the_parallel_solve`).
Reductions have one defined order, the same at every vector width: `SimdKernels.Dot` sums four partial sums and then the
tail, never fused (`D1CrossTierTests.Dot_is_bit_identical_at_every_tier_and_equals_the_defined_order`). Sparse matrix
rows are summed in insertion order by construction (`DenseSparseParityTests`, `Doc6CsrStableOrderTests`).

Evidence: [ch. 8, "Parallel paths and sequential paths"](<technical/0008 Provenance and invariants.md#parallel-paths-and-sequential-paths>);
[ch. 38, "Parallel loops" and "The vector kernels"](<technical/0038 Deterministic mathematics.md>).

## 3. Does it depend on hardware FMA or the operating system's maths library?

Neither. Every numerical caller passes `fastFma: false`, which does a separate multiply and add; the answer is the same
on a CPU with FMA and one without. `DeterministicMathGuardTests` fails the test run if a numerical method calls a fused
multiply-add, a horizontal vector reduction or a platform `Math` transcendental. Sine, cosine, exponential, logarithm and
the other elementary functions come from `DetMath`, a port of FDLIBM 5.3, so the operating system's C library does not
enter a result. The platform's own functions do differ: in a measured comparison one Windows runtime disagreed with
fdlibm in 237 of 3,778 results, which is why GridSim does not use it.

Evidence: [ch. 38, "Why the platform maths is not reproducible", "Fused multiply-add policy" and "How the rules are
enforced"](<technical/0038 Deterministic mathematics.md>).

## 4. Can I prove it mechanically?

Yes, on your own machine and your own cases. Run a verb with `--json <file>` on two machines (or two worker counts, or
two CPU tiers) and compare the SHA-256 of the result files. The files hold every number at full round-trip precision, so
equal hashes mean equal bits. The result file embeds the tool version and its own arguments, so compare runs of the same
build with the same arguments. For a record kept for you, `--journal <file>` writes the SHA-256 of every input and output
of a run.

Across CPU tiers, `gridsim simdcheck` reports the active tier and `--expect <tier>` gates on it. The release builds are
also checked: the Linux CLI is rebuilt on a fresh runner and compared with the shipped archive byte for byte, and every
release file carries a published SHA-256 and an attestation. Today the mechanical proof is the test suite plus hashing
your own result files.

Evidence: [ch. 8, "Determinism: two runs, one hash"](<technical/0008 Provenance and invariants.md>);
[ch. 35](<technical/0035 Deployment and security.md>) (release workflow); [ch. 42](<technical/0042 Run journal and breaking-change validation.md>).

## 5. What happens across versions, and can I rerun an old version years later?

Bits can move between versions, and each move is labelled. Every change that moves a result is recorded as
`BREAKING CHANGE (Bnn)` in `CHANGELOG.md`, with the magnitude, and in the machine-readable
`src/GridSim.Core/Journal/breaking-changes.json`, with a map to the tests that pin it. `gridsim breaking-change validate`
re-runs journal entries on a later build and says whether each difference is explained by a recorded change and within
its claimed bound.

To rerun an old version, you need the same inputs and the same toolchain, and the repository records both. The SDK is
pinned to one exact version in `global.json` with no roll-forward, NuGet restores run from lock files (locked mode on CI),
and every binary records the commit it was built from and the SHA-256 of its software bill of materials
(`gridsim --version` prints `gridsim <version>+<commit>`). Check out the tagged commit, restore with the pinned SDK and
the locked packages, and the build is the same. A rerun on the same .NET major version reproduces the old bits on the
same inputs; a different .NET major version is re-proved before it is trusted.

Evidence: [ch. 42](<technical/0042 Run journal and breaking-change validation.md>);
`BugHunt1101WebTests.B161_global_json_pins_one_exact_sdk_with_no_roll_forward`; [ch. 35](<technical/0035 Deployment and security.md>).

## Guaranteed and pinned by tests

- Newton-Raphson solves repeat bit for bit (`SolverPropertyTests.Solving_twice_is_bit_for_bit_identical`).
- Parallel and sequential contingency screens agree to the bit at 1, 2 and 4 workers (`ContingencyParallelBitIdentityTests`).
- Dense and sparse Newton-Raphson agree to the bit on the IEEE and bundled MATPOWER cases (`DenseSparseParityTests`).
- The vector kernels equal the scalar statement of the arithmetic at every CPU tier (`SimdTierSeamTests`, `D1CrossTierTests`).
- No numerical method calls a fused multiply-add, a horizontal reduction or a platform transcendental (`DeterministicMathGuardTests`).
- The suite's exact-value pins pass in the normal, AVX2-off, hardware-intrinsics-off, forced AVX-512 and FMA-off CI modes
  (forced AVX-512 when the runner has AVX-512; on a runner without it that leg reports NOT CHECKED),
  on Linux, Windows and macOS (Apple silicon), with Linux and Windows on 64-bit Arm in the normal and
  hardware-intrinsics-off modes.
- Number formatting and parsing do not depend on the machine's culture in the CLI and the web host: the CLI pins the invariant culture, and tests run
  the formatting sites under a comma-decimal culture (`BugHunt1101WebTests.B193_*`).
- A GPU solve that gives up falls back to the exact CPU LU and equals the CPU path to the bit
  (`GpuFallbackTests.A_backend_that_always_gives_up_falls_back_to_the_exact_cpu_lu_bit_for_bit`).
- The SDK is pinned exactly (`BugHunt1101WebTests.B161_global_json_pins_one_exact_sdk_with_no_roll_forward`).

## Observed on the author's own hardware

These are observations, not test-pinned guarantees. GridSim has been built and run by its author on Windows 10,
Windows 11, macOS, Android and Linux (Debian-family distributions), on 64-bit operating systems only.

- **64-bit Arm (ARMv8-A and later):** deterministic.
- **Apple Silicon:** M1 only so far.
- **Intel:** every 64-bit CPU from the 9th-generation Core onwards that the author tested.
- **AMD:** every Ryzen tested. First-generation Zen (Zen 1) is deterministic, meaning the same bits run to run on that
  CPU, but its results are not bit-identical to the machine-epsilon reference results. The cause is not yet identified.
- **Older AMD:** Bulldozer-family CPUs and Opterons run GridSim; older AMD CPUs do not. The cause is not yet identified:
  GridSim sets no CPU instruction-set requirement of its own, and the .NET 10 runtime's x64 baseline is x86-64-v1.

GridSim is not yet validated in every environment. Across a very wide range of hardware so far, behaviour is consistent
and results agree to machine epsilon.

## Not guaranteed, and known exceptions

- **The `cycle` verb.** Its analysis jobs run against a wall-clock budget, so which jobs finish in a cycle depends on the
  machine's speed, load and core count. Each job that runs gives its own deterministic result. Pin a study by running it
  directly (`nminus1`, `screen`, `physics-check`).
- **The GPU path.** A GPU solve is deterministic (the same build on the same device repeats) but is not bit-identical to
  the CPU: it runs different algorithms, a different reduction order and fused multiply-add. The measured envelope
  against the CPU is 1.45e-12 pu in voltage and 5.2e-11 degrees at most (RTX A4000, 27 MATPOWER cases).
- **Dense and sparse WLS.** The two estimator steps factorise differently and do not agree to the bit. A CPU estimate
  always takes the sparse step, so the result does not depend on the bus count; the dense step runs only with a GPU linear
  solver, and the constrained, feasible and QR methods are dense only.
- **Loss allocation.** `LossAllocationTests.Parallel_agrees_with_sequential` compares within 1e-6 MW, not bit for bit.
- **Zen 1.** Deterministic on that CPU, not bit-identical to the reference results; cause not yet identified.
- **Files that record wall time.** Execution evidence that records how long a run took differs between two identical
  runs: the `summary.md` of `emt` and `transient`, and `planning-study`'s `evidence.json` (job timings). Hash the result
  JSON or compare the result tables, not those files.
- **Other .NET major versions, and builds other than the shipped ones.** Not covered until re-proved.
- **`DetMath` accuracy.** It is accurate to under 1 ulp, not correctly rounded: results agree with Java `StrictMath`, not
  necessarily with the closest double or another library.

## See also

- [Technical ch. 46, Determinism: specification and evidence](<technical/0046 Determinism - specification and evidence.md>)
- [User manual ch. 36, Determinism and reproducibility](<manual/0036 Determinism and reproducibility.md>)
- [Technical ch. 8, Provenance and invariants](<technical/0008 Provenance and invariants.md#determinism-and-bit-identity-as-implemented>)
- [Technical ch. 38, Deterministic mathematics](<technical/0038 Deterministic mathematics.md>)
- [Technical ch. 4, Sparse and GPU numerics](<technical/0004 Sparse and GPU numerics.md>)
