# PerformanceOpts.md - Catalogue of Existing Performance Optimizations

Purpose: an audit inventory of every **deliberate** performance optimization currently in `src/`.
Optimizations are where errors hide - a cached value that should have been invalidated, a "bit-identical"
claim that quietly stopped being true, a fast path that diverges from the reference. Each entry therefore
records three things:

1. **Mechanism** - what the optimization actually does.
2. **What it speeds up** - the workload that justifies it.
3. **Correctness claim & guard** - what the code *promises* about equivalence with the unoptimized path,
   and how (or whether) that promise is enforced. Entries whose claim is enforced only by a comment are
   the audit hot spots.

Line numbers are as of 2026-07-19. Entries marked **[in-flight]** were added in the AdmittanceStamp /
complex-solver work this catalogue was written alongside. The claims and guards in sections 1 to 4, 6, 11.4 and 12
were brought up to date with the code in October 2026 (pivot floors, sort keys, the determinism changes and the
engine routing); their line numbers were not, so find the code by name, not by line.

---

## 1. Sparse linear-algebra core (`src/GridSim.Core/Numerics/`)

### 1.1 CSR sparse storage with cached diagonal pointers
[Sparse.cs:10-112](src/GridSim.Core/Numerics/Sparse.cs) - `CsrMatrix`: real sparse matrix in compressed-sparse-row
form, columns sorted within each row, plus a `DiagPtr[]` cache locating each row's diagonal.
- **Speeds up:** Jacobian storage and mat-vec for the large-network power flow - per-row work is O(neighbours)
  instead of O(n); `DiagPtr` gives ILU(0) O(1) pivot access.
- **Claim/guard:** duplicates are summed at `Build()`; a zero diagonal can be injected (`ensureDiagonal`)
  so factorizations never chase a missing pivot. No independent check; relied on by every consumer.

### 1.2 Builder sort without per-row comparer allocation (real and complex builders)
[Sparse.cs:75-85](src/GridSim.Core/Numerics/Sparse.cs) - `CsrMatrix.Builder.Build` sorts each row via a parallel
key array (`Array.Sort(keys, idx, ...)`) instead of `Comparer<int>.Create` with a capturing lambda.
- **Speeds up:** Jacobian assembly, which runs once per Newton iteration - the old form allocated a comparer
  delegate + closure per row per iteration.
- **Claim/guard:** the key is the unique 64-bit `(column << 32) | insertionIndex`, so the sort is stable by
  construction and duplicate cells are summed in insertion order whatever sort algorithm the runtime uses
  (pinned by `Doc6CsrStableOrderTests`). The complex twin `ComplexCsr.Builder` uses the same unique-key sort.

### 1.3 Gilbert-Peierls left-looking sparse LU
[SparseLuFactorization.cs](src/GridSim.Core/Numerics/SparseLuFactorization.cs) - each column of the factor is
produced by one sparse triangular solve whose nonzero pattern is found first by an iterative DFS "reach"
over the graph of L (lines 161-229); numeric work is proportional to true post-fill nonzeros. Workspaces
(`x`, `xi`, `pstack`, `marked`) are allocated once per factorization (lines 60-64); L/U storage grows by
doubling (lines 231-243). The `marked[]` array is restored to all-false after every reach (line 192) so no
per-column clearing pass is needed.
- **Speeds up:** the exact direct solve for stiff Jacobians (GDA full-replica scale). The class comment
  records that this **replaced** a `Dictionary<int,double>`-row scheme that paid hash overhead and re-derived
  the sparsity pattern on every solve.
- **Claim/guard:** partial pivoting with a hard `null` return when the best candidate is at or below the relative
  floor `NumericTolerances.PivotFloor(max|A|)` (1e-12 times the largest entry), so a matrix singular to working
  precision is refused at any scale; a NaN entry throws. Correctness is cross-checked at the system level (the
  dense and sparse Newton engines share this step and are bit-identical; see section 3).

### 1.4 Reverse Cuthill-McKee fill-reducing ordering, and its reuse seams
[SparseLu.cs:89-165](src/GridSim.Core/Numerics/SparseLu.cs) - RCM over the structurally-symmetrised graph;
adjacency built in flat CSR-like arrays with "no per-node LINQ", reusable neighbour buffer.
The public seams are the important optimization:
- `Factor(a)` - ordering + factorization per call.
- `Ordering(a)` + `FactorWithOrdering(a, q)` ([SparseLu.cs:36-80](src/GridSim.Core/Numerics/SparseLu.cs)) -
  compute RCM **once** per sparsity pattern, refactor values each iteration.
- `FactorPrepared(n, ap, ai, ax, q)` ([SparseLu.cs:85-86](src/GridSim.Core/Numerics/SparseLu.cs)) - additionally
  skip the CSR->CSC conversion when the caller keeps its own CSC arrays.
- **Speeds up:** any loop that re-solves the same structure - the NR iteration, the estimator's per-pass
  refactorization, the residual-covariance multi-RHS path.
- **Claim/guard:** "bit-identical to `Factor` because RCM is a pure function of the structure" - a comment-level
  claim, true only while callers correctly detect structure changes (see section 3.4). CSR->CSC drops explicit zeros
  ([SparseLu.cs:50-57](src/GridSim.Core/Numerics/SparseLu.cs)), documented as matching the old solver's semantics.
- **Audit note:** the BFS inside RCM allocates one comparer and one neighbour buffer per call, not per visited
  node, and sorts with a frozen introsort so a runtime that retunes its own sort cannot change the ordering.

### 1.5 ILU(0) preconditioner + BiCGSTAB iterative fallback
[Sparse.cs:200-321](src/GridSim.Core/Numerics/Sparse.cs) - `Ilu0`: incomplete LU keeping exactly A's sparsity
pattern (no fill), built in-place over a clone of `Val` with an `iw[]` scatter index; a tiny pivot is raised to a
guard RELATIVE to its row's scale in the original matrix (`NumericTolerances.Ilu0PivotGuard`). `BiCgStab`:
preconditioned Krylov solver using only sparse mat-vecs; 8 work vectors allocated per call.
- **Speeds up:** the fallback linear solve when the direct LU reports singular; scales to networks where dense
  factorization can't fit.
- **Claim/guard:** iterative - answers are correct only to `tol`; breakdown (`rho`~0, `omega`=0) returns `false`
  so the caller can stop rather than apply garbage. The pivot guard only shapes the preconditioner: BiCGSTAB's own
  residual test against the true matrix decides success.

### 1.6 Complex solver suite **[in-flight]**
[ComplexLinearSolvers.cs](src/GridSim.Core/Numerics/ComplexLinearSolvers.cs) - three cross-validating
implementations of the new `IComplexLinearSolver` seam (harmonic engine `Y(h) V = I`):
- `DenseComplexLuSolver` - dense Gaussian elimination with partial pivoting, lifted verbatim from the fault
  study so the direct complex solve lives in one place. O(n^3), the small-case path and the **oracle**.
- `RealExpandedComplexSolver` - rewrites `(G+jB)(xr+jxi)=br+jbi` as the real 2nx2n system `[G -B; B G]` and
  solves it through the **existing tuned real sparse stack** (`SparseLu`), inheriting RCM + G-P LU for free.
  This is the scalable default (gb-full ~ 3,500 buses).
- `SparseComplexBiCgStabSolver` - Jacobi-preconditioned complex BiCGSTAB directly on the `ComplexCsr`
  (no real expansion, no dense fill); returns `null` on breakdown/stagnation/non-finite iterates.
- **Speeds up:** harmonic penetration solves at scale (real-expansion path) while keeping an exact small-case
  oracle and an independent third implementation.
- **Claim/guard:** the three must agree - enforced by
  [ComplexSolverCrossValidationTests.cs](tests/GridSim.Tests/ComplexSolverCrossValidationTests.cs)
  (direct-vs-direct to 1e-9, iterative to 1e-8, plus residual `Ax=b` checks). **Audit notes:**
  (a) through the interface (`Solve`) `DenseComplexLuSolver` returns `null` on a pivot at or below the shared
  relative floor, as the other two do, so the seam has one failure contract; only the fault study's own
  `SolveDense` keeps the historical clamp (now at the relative floor, not an absolute 1e-14) and never returns
  `null` (`ComplexSolverSingularContractTests`). (b) `RealExpandedComplexSolver.Factor` returns a factorization
  that solves many right-hand sides, and its RCM ordering is cached across same-pattern matrices (successive
  harmonic orders).

---

## 2. SIMD kernels and the bit-exactness contract (`src/GridSim.Core/Numerics/`)

### 2.1 Startup-time SIMD tier dispatch
[SimdCapabilities.cs:23-49](src/GridSim.Core/Numerics/SimdCapabilities.cs) - static constructor picks
AVX-512 -> AVX2 -> SSE2/AdvSimd -> scalar once at startup. Deliberately avoids `Vector<T>` because it can
silently cap at 256 bits on AVX-512 hardware. FMA availability is recorded **separately** because FMA
changes the last bit versus separate multiply-then-add.

### 2.2 Shared vector primitives with an explicit bit-exact/fast split
[SimdKernels.cs](src/GridSim.Core/Numerics/SimdKernels.cs):
- `SubtractScaledRow` / `AddScaledRow` (lines 39-158): the O(n^3) row-update hot spot of dense LU, 8-wide on
  AVX-512, operating on flat arrays or `double[,]` through `ref double` origins + `LoadUnsafe`/`StoreUnsafe`
  (no `unsafe` blocks, no bounds checks in the loop).
- With `fastFma=false` each lane does a **separate multiply then subtract** - bit-for-bit the scalar
  `x -= f*y` at every vector width. `fastFma=true` fuses where the CPU has FMA; no numerical path passes it, and
  `DeterministicMathGuardTests.No_numerical_path_selects_the_fused_multiply_add_arm` fails if one does.
- `Dot` vs `DotExact`: `Dot` sums in one defined order (four partial sums, combined as `(s0 + s1) + (s2 + s3)`,
  then the tail left to right, never fused) that every tier reproduces exactly, so it gives the same bits on every
  CPU; it is not the left-to-right order, which `DotExact` keeps.
- Batch `SinCos`: the deterministic fdlibm sine and cosine (`DetMath.SinCos`) per element, bit-identical at every
  tier and to the scalar call (`D1CrossTierTests`). The old Cephes minimax kernel with a `Math.SinCos` tail is gone.
- **Speeds up:** dense LU (power flow reference), H^T W H accumulation (estimator), swing integration.
- **Claim/guard:** every kernel a numerical path calls is bit-identical at every tier, pinned by
  `D1CrossTierTests` and `SimdTierSeamTests`. A change that adds a fused multiply-add, a horizontal vector
  reduction or a platform `Math` transcendental to a numerical path fails `DeterministicMathGuardTests`.

---

## 3. Power-flow engines and solver selection (`src/GridSim.Core/PowerFlow/`)

### 3.1 Measured dense/sparse/radial engine routing
[SolverSelector.cs](src/GridSim.Core/PowerFlow/SolverSelector.cs) - single source of truth for engine choice:
- `DefaultSparseThreshold = 0`: every CPU Newton power flow routes to the sparse engine. The two Newton engines
  share the sparse-LU step and are bit-identical on the CPU, so the choice is speed and memory only, and a re-bench
  found sparse faster at every size. The old value was 200 (bench 2026-07-14: case300 dense 1964 ms vs sparse 23 ms;
  case9241 dense OOM), and before that 3,000, which left every 300 to 3,000-bus case on the slow path. The 200-bus
  figure survives as `DenseSparseCrossoverBuses`, used by the estimator's observability report, the GPU dense /
  sparse routing and the solve-export trace.
- `LooksRadial` (lines 40-41): strictly radial feeders (branches == buses-1, one slack) route to the
  backward/forward sweep instead of transmission NR.
- **Claim/guard:** all three engines implement `IPowerFlowSolver` and are expected to agree on overlapping
  cases; the sparse engine's doc claims it "reproduces the dense solver's voltages to solver tolerance".

### 3.2 Dense LU reference solver: flat buffer + bit-exact SIMD
[DenseLuSolver.cs:34-88](src/GridSim.Core/PowerFlow/DenseLuSolver.cs) - copies the `double[,]` into a flat
row-major `double[]` via `Buffer.BlockCopy` (drops 2-D indexer bounds checks, contiguous rows for cache and
vector loads), then runs elimination with
[`SimdKernels.SubtractScaledRow`](src/GridSim.Core/Numerics/SimdKernels.cs). Pivot search, swaps, and back-substitution keep scalar order.
- **Speeds up:** dense solves outside the CPU Newton step (the CPU dense Newton engine takes the shared sparse
  step instead, and every CPU power flow routes sparse by default).
- **Claim/guard:** "bit-for-bit identical to the textbook version" - this solver is the **validated
  reference** that residual/pandapower/GPU checks diff against, so it deliberately refuses FMA
  ([SimdKernels.cs](src/GridSim.Core/Numerics/SimdKernels.cs)). Relative near-singular floor
  `aNorm*1e-12`.

### 3.3 Sparse Newton-Raphson: sparse assembly, direct-first solve, damping
[SparseNewtonRaphsonPowerFlow.cs](src/GridSim.Core/PowerFlow/SparseNewtonRaphsonPowerFlow.cs):
- Sparse `ComplexCsr` Y-bus and a Jacobian assembled by walking each bus's neighbours (lines 214-256) -
  O(nnz) per iteration instead of O(n^2).
- Direct sparse LU first; ILU(0)+BiCGSTAB **only** as a singular-fallback (lines 152-164) - "a NaN/garbage
  solution must never be applied".
- Adaptive step damping capped at ~0.4 (lines 180-187): protects stiff flat starts, decays to 1 near the
  solution so quadratic convergence is preserved.
- Trace capture skips serialising the sparse Jacobian (lines 174-176) - diagnostics stay affordable at scale.
- `ComputeInjections` uses `DetMath.SinCos` per edge, the deterministic fdlibm port, not the platform
  `Math.SinCos`, whose last bits depend on the operating system's libm.
- **Audit notes:** the Jacobian's CSR is rebuilt from triplets (builder, count-sort, per-row sort, dedup)
  **every iteration** even though its structure is fixed within a Q-limit pass - the structure-reuse treatment
  given to the estimator (section 4.1) was not applied here; only the RCM ordering is cached (section 3.4). `BuildY`
  (lines 258-285) still inlines the branch stamp rather than calling the new `AdmittanceStamp` (see section 3.6).

### 3.4 Cross-call RCM ordering cache keyed by pattern content
[CpuSparseLuSolver.cs](src/GridSim.Core/PowerFlow/CpuSparseLuSolver.cs) - caches the RCM ordering across
`Solve` calls; the pattern is compared **by content** each call (`Span.SequenceEqual` over `RowPtr`/`ColIdx`,
O(n+nnz), cheap next to the ordering it saves).
- **Speeds up:** every NR iteration after the first (same-structured Jacobian, values change only).
- **Claim/guard:** "bit-identical ... speed-only" because RCM is deterministic in the pattern; a Q-limit
  bus-type switch changes the pattern and *transparently* triggers a fresh ordering. The by-content compare -
  rather than reference identity or a dirty flag - is what makes the cache safe against callers that rebuild
  an identical-shape matrix each iteration. This is the model example of a correctly-guarded cache.

### 3.5 Island pre-pass with a no-op fast path
[IslandPartition.cs](src/GridSim.Core/PowerFlow/IslandPartition.cs) - union-find with path halving over
in-service branches; a fully connected model with the slack returns the input unchanged - "a pure no-op, so a
fully-connected model is unchanged (bit-identical solve)".

### 3.6 Shared branch admittance stamp **[in-flight]**
[AdmittanceStamp.cs](src/GridSim.Core/PowerFlow/AdmittanceStamp.cs) - the pi-model 2x2 stamp
(series admittance + line charging + tap/phase-shift) centralised as `AdmittanceStamp.Branch(...)`, now
frequency-parameterisable (`X->hX`, `B->hB`, `R->R*rFactor`) so the harmonic Y(h) is built from the same
arithmetic.
- **Claim/guard:** "at h=1, rFactor=1 this reproduces the fundamental stamp bit-for-bit (multiplying by 1.0
  is exact for doubles)". Consumed by `YBus.Build`, `YBus.BuildSparse`, and
  [FrequencyDependentYBus.cs:42](src/GridSim.Core/Harmonics/FrequencyDependentYBus.cs).
- **Audit note:** the doc-comment says centralisation "removes the drift risk", but the identical inline
  stamp still exists in at least seven places:
  [SparseNewtonRaphsonPowerFlow.cs:265](src/GridSim.Core/PowerFlow/SparseNewtonRaphsonPowerFlow.cs),
  [NewtonShell.cs:292](src/GridSim.Core/PowerFlow/NewtonShell.cs),
  [FaultAnalysis.cs:165](src/GridSim.Core/Protection/FaultAnalysis.cs),
  [MeasurementModel.cs:151,384](src/GridSim.Core/Estimation/MeasurementModel.cs),
  [PhysicsLaws.cs:98](src/GridSim.Core/Physics/PhysicsLaws.cs),
  [ReciprocityCheck.cs:39](src/GridSim.Core/Physics/Experimental/ReciprocityCheck.cs).
  Until those migrate, the drift risk the comment claims to have removed still exists.

### 3.7 Sparse Y-bus cells without the dense array
[YBus.cs:64-104](src/GridSim.Core/PowerFlow/YBus.cs) - `YBus.BuildSparse` accumulates nonzero cells into a
`Dictionary<(row,col), Complex>` instead of materialising the dense `[n,n]` array ("~200 MB at gb-full's
3,539 buses") for consumers that only read the nonzero pattern (the estimator's measurement model).
- **Claim/guard:** visits branches then shunts "in EXACTLY the same order as `Build`, so each cell's
  floating-point sum is bit-for-bit the dense value" - an ordering-dependent claim enforced only by keeping
  the two loops textually parallel. Any reordering of either loop silently breaks it.

### 3.8 Q-limit handling defaults preserve pinned regressions
[PowerFlowOptions.cs:21-36](src/GridSim.Core/PowerFlow/PowerFlowOptions.cs) - `RevertQLimits` defaults OFF to
keep historical one-way PV->PQ switching "bit-for-bit unchanged" against pinned published-solution regression
tests; `Trace = null` default means diagnostic capture costs nothing unless opted in.

### 3.9 Fault study: dense with an explicit ceiling
[FaultAnalysis.cs:47-59](src/GridSim.Core/Protection/FaultAnalysis.cs) - `DenseBusLimit = 1500`: the fault
study is a dense complex solve and **fails loud** past 1,500 buses rather than silently degrading ("a fault
study is run on a specific case, not the whole estate"). The Zbus column is obtained by solving `Y*x = e_k`
(one dense solve) rather than inverting Y.
- **Audit note:** within the cap the path still does redundant work - `YBus.Build(model).Y.Clone()` clones a
  freshly built matrix, and `YBus.Build` is called a second time for post-fault injections (see review
  findings).

---

## 4. State estimation (`src/GridSim.Core/Estimation/`)

### 4.1 Sparse WLS: structural work once per pass, refill per iteration
[WlsStep.cs](src/GridSim.Core/Estimation/WlsStep.cs) - `SparseWlsStep.Begin` builds, exactly once per pass:
the CSC structure of the gain matrix `G = H^T W H`, per-measurement-row column lists, a per-(c1,c2) slot map
(binary search done once), and the RCM ordering (`SparseLu.Ordering`, line 196). Each iteration then only
refills numeric values and calls `SparseLu.FactorPrepared` (lines 263, 273).
- **Speeds up:** gb-full-scale estimation - comment: "removes the per-iteration CSR rebuild + re-sort +
  re-ordering the first implementation paid."
- **Claim/guard:** valid only while the measurement structure is genuinely fixed across the pass; the
  structure/value split is by construction, not checked at runtime (contrast section 3.4's by-content guard).
- The dense H^T W H accumulation path uses `SimdKernels.AddScaledRow(..., fastFma: false)`, so the dense gain is
  bit-identical at every tier. A plain WLS or robust estimate on the CPU is solved by the sparse step at every
  size (`CanonicalWlsStep`); the dense step runs for the constrained, feasible and QR methods and with a GPU solver.

### 4.2 QR instead of normal equations where conditioning demands it
[WlsStateEstimator.cs:213](src/GridSim.Core/Estimation/WlsStateEstimator.cs) - Householder QR step avoids
forming `H^T W H` (squares the condition number); an algorithm choice trading raw speed for stability.

### 4.3 Bad-data analysis hot spots on the shared kernels
[BadDataAnalysis.cs:166-195](src/GridSim.Core/Estimation/BadDataAnalysis.cs) - the O(s^3) LU trailing update
and back-substitution route through `SimdKernels.SubtractScaledRow` / `Dot`; the residual-covariance path
factors the gain matrix once and back-substitutes one column per measurement (`SparseLu.Factor` reuse seam).

### 4.4 Kalman DSE reuses the snapshot estimator's Jacobian
[KalmanStateEstimator.cs:18-23](src/GridSim.Core/Estimation/Dse/KalmanStateEstimator.cs) - shares the
measurement Jacobian machinery and avoids forming a dense mxm innovation inverse.

### 4.5 Chi-square tables in ROData
[ChiSquare.cs:28-43](src/GridSim.Core/Estimation/ChiSquare.cs) - coefficient tables as
`ReadOnlySpan<double>` over compile-time data (no heap allocation, no runtime table construction).

---

## 5. Distribution factors & contingency analysis (`src/GridSim.Core/Analysis/`)

### 5.1 One B' factorization, unlimited back-substitutions
[DistributionFactors.cs](src/GridSim.Core/Analysis/DistributionFactors.cs) - factors the reduced DC
susceptance matrix **once** (`SparseLu.Factor`, lines 87-88, stored on the instance); every PTDF column,
LODF column, and DC flow is then a single triangular solve. `PtdfColumn`/`LodfColumn` exist specifically so
large networks never materialise the full dense PTDF matrix (lines 184-199).
- **Claim/guard:** LODF islanding guard `|1 - Phi_kk| < 1e-9` refuses to compute factors for a branch whose
  outage islands the network.

### 5.2 Parallel N-1 screening with thread-local solvers
[ContingencyAnalysis.cs:45-62](src/GridSim.Core/Analysis/ContingencyAnalysis.cs) - `Parallel.For` over
branches with a `ThreadLocal<IPowerFlowSolver>` per worker (`SteadyStateSolver` is documented not
thread-safe - one instance per thread is the sanctioned pattern, enforced by an `Interlocked` re-entrancy
guard in [SteadyStateSolver.cs:55](src/GridSim.Core/PowerFlow/SteadyStateSolver.cs)). Results are written by
index, so output order is deterministic regardless of completion order. A `branchIndices` subset parameter
supports sampling the largest systems.

---

## 6. Dynamics / transient integration (`src/GridSim.Core/Dynamics/`)

### 6.1 Allocation-free RK4 swing integrator
[ClassicalMultiMachine.cs](src/GridSim.Core/Dynamics/Distributed/ClassicalMultiMachine.cs):
- Kron-reduced network (system reduced to generator internal nodes) - an algorithmic size reduction before
  any code-level tuning ([KronReduction.cs](src/GridSim.Core/PowerFlow/KronReduction.cs): sequential
  star-mesh elimination "without forming the inverse", pivot floor `aNorm*1e-12`).
- Real/imag parts of the (pre/post-fault) reduced Y split into flat `G`/`B` arrays **once** (lines 66-77) -
  "the kernel reads G,B millions of times".
- All RK4 slope buffers + derivative scratch allocated **once** and reused across steps (lines 94-102) -
  "the previous code newed ~14 `double[n]` per step, pure GC churn in the hot integrator."
- Inner derivative uses the batch `SinCos` and `SimdKernels.Dot`, both bit-identical at every tier (section 2.2),
  so the trajectory is the same bits on every CPU tier.
- [SwingKernel.cs:5-12](src/GridSim.Core/Dynamics/SwingKernel.cs) - governor/FFR primitives are
  `[MethodImpl(AggressiveInlining)]`: called per-substep x per-unit, thousands of times per contingency.

### 6.2 Deterministic multi-rate scheduling
[HybridMultiRateScheduler.cs:16-37](src/GridSim.Core/Simulation/HybridMultiRateScheduler.cs) - the
interleaved SCADA/PMU schedule is precomputed from the periods (no wall-clock reads); coincident instants
deterministically resolved (SCADA supersedes PMU).

---

## 7. GC control and allocation discipline

### 7.1 Scoped GC modes for real-time sections
[GcScope.cs](src/GridSim.Core/Simulation/GcScope.cs):
- `LowLatency()` - `SustainedLowLatency` for a scope: full blocking gen2 collections deferred, safe to hold
  long-term. Used around the CLI replay/transient loops
  ([Program.cs:1849](src/GridSim.Cli/Program.cs), [Program.cs:2026](src/GridSim.Cli/Program.cs) - "no gen2
  stutter while the loop runs", "smooth 20 ms playback").
- `NoGc(budget = 256 MB)` - a true no-GC region for **bounded** bursts; documented "never an open-ended
  loop"; a runtime refusal degrades to a no-op instead of throwing; `Dispose` checks the region is still
  active before ending it (the region can end itself on budget exhaustion).
- **Audit note:** `NoGc` is the classic optimization-that-hides-errors shape - if a "bounded" burst grows
  past the budget, behaviour silently reverts to normal GC and the perf guarantee evaporates with no signal.

### 7.2 Point allocations avoided in hot/startup paths
- `stackalloc` char buffer for string normalisation: [GdaModelBuilder.cs:457](src/GridSim.Gda/GdaModelBuilder.cs).
- Lock-free pending-query correlation: `ConcurrentDictionary` in
  [GridBridgeClient.cs:33](src/GridSim.Core/Net/GridBridgeClient.cs).
- Capacity pre-sizing throughout (e.g. [ContingencyAnalysis.cs:106](src/GridSim.Core/Analysis/ContingencyAnalysis.cs),
  [PresolvedReplay.cs:68-69](src/GridSim.Core/Simulation/PresolvedReplay.cs), `GraphLayout` index dictionaries).

---

## 8. GPU backends (`src/GridSim.Gpu/`)

### 8.1 ILGPU context: lazy, guarded, honest about FP64
[GpuContext.cs](src/GridSim.Gpu/GpuContext.cs) - lazy thread-safe init that never throws (probe failures mark
the backend unavailable -> CPU fallback); a single global lock serialises all device access ("one GPU, many
CPU worker threads"); `Fp64Works` (lines 196-221) runs an actual 1-thread `1.5+1.5` kernel on the device to
verify double precision rather than trusting capability strings - GridSim solves in `double`, so a
non-FP64 device must never be selected. Same kernels target CUDA and OpenCL.

### 8.2 GPU dense LU: stream-queued, two syncs total
[GpuDenseLinearSolver.cs](src/GridSim.Gpu/GpuDenseLinearSolver.cs) - the whole forward elimination is
submitted to one stream with zero host stalls; the host synchronises exactly twice (read singularity flag;
read solution). On-device partial pivoting via a single-block shared-memory argmax reduction (lines 131-168)
so the pivot index never crosses PCIe. Explicitly contrasts the old design that round-tripped to the host
three times per column. Transparent fallback to the CPU `DenseLuSolver` when no device exists; the
device-side singularity flag mirrors the CPU solver's relative floor so the throw-on-singular contract is
preserved.

### 8.3 GPU sparse BiCGSTAB: device-resident vectors, scalar-only round-trips
[GpuSparseLinearSolver.cs](src/GridSim.Gpu/GpuSparseLinearSolver.cs) - all Krylov vectors live on the device;
dot products use a single-block reduction kernel returning one scalar (lines 218-237) instead of copying the
whole vector back and summing on the CPU. Falls back to `CpuSparseLuSolver` when no device or a zero/NaN
diagonal; returns `null` on breakdown so the caller falls back. A deliberately deferred optimization is
recorded at lines 286-288: no cuBLAS trailing-update, to keep the build free of the cuBLAS native dependency.

### 8.4 ROCm/HIP backend: load-only-if-used natives, analysis-once SpMV
[RocmInterop.cs](src/GridSim.Gpu/RocmInterop.cs) - `DllImport` resolution happens on first call only, with a
custom resolver trying versioned `.so` names and `$ROCM_PATH/lib`, so the assembly loads cleanly on machines
with no ROCm as long as availability short-circuits first. `rocsparse_dcsrmv_analysis` runs **once** per
uploaded CSR because the solver issues many SpMVs against it (lines 281-329).
[RocmSparseLinearSolver.cs:12-18](src/GridSim.Gpu/RocmSparseLinearSolver.cs) - fully device-resident
BiCGSTAB; "only convergence scalars and the final solution cross PCIe."

### 8.5 CLI benchmark harness parallelism with memory gates
[Program.cs:3244-3276](src/GridSim.Cli/Program.cs) - `Parallel.ForEach` over benchmark cases; GPU engines
forced to degree 1 (one device); `SemaphoreSlim` gates limit concurrent 3k-8k-bus dense cases to 3 and
>8k-bus to 1 (dense memory pressure); run counts adapt to case size. Parallel LODF/PTDF at line 3423.

---

## 9. WPF rendering (`src/GridSim.Wpf/`)

### 9.1 FastSchematic: retained batched geometry instead of per-element Shapes
[FastSchematic.cs](src/GridSim.Wpf/Visuals/FastSchematic.cs) - the ~6,000-bus/~8,000-branch renderer:
- One `OnRender` pass over a handful of batched frozen `StreamGeometry` objects; replaces one `Shape` per
  element ("hopeless at thousands").
- Dirty-flag retained geometry (`_staticDirty`): corridors/roundels rebuilt only on zoom/pan/state change,
  never per frame; property setters invalidate only on actual change.
- Corridor colour **bucketing** (`LoadingBuckets = 12`): branches batch into ~12 geometries instead of one
  pen per branch; only the marching-dash flow overlay is rebuilt per animation frame, and only for the
  visible, in-LOD subset.
- Level-of-detail (`ShowAllCeiling = 800`, voltage-tier LOD above it), viewport culling, baked
  world-coordinate boundary/ring geometries.

### 9.2 Frozen freezables everywhere
Static pens/brushes/geometries built once and `.Freeze()`d (render-thread-shareable, no change tracking):
[Palette.cs:86](src/GridSim.Wpf/Visuals/Palette.cs) (`Palette.Frozen` helper),
[DashboardViewModel.cs:431-441](src/GridSim.Wpf/DashboardViewModel.cs) (colour-ramp brushes),
Sparkline, Gauge, Phasor, PqDiagram, FrequencyTrace.

### 9.3 Off-screen and per-frame work skipping
[AssetPages.cs:53-66](src/GridSim.Wpf/Views/Assets/AssetPages.cs) - `Active` flag: off-screen tabs skip their
per-frame `Refresh` entirely and catch up on show. [ScadaVm.cs:256](src/GridSim.Wpf/Views/ScadaVm.cs) - refresh
touches only live columns. Several VMs (Substation/Operator/Market/NesoRp1/Circuit pages) reuse values the
dashboard already computed instead of recomputing per frame. Playback advances on a fixed `DispatcherTimer`
cadence ([MainWindow.xaml.cs:126](src/GridSim.Wpf/MainWindow.xaml.cs)).

---

## 10. I/O, startup, and caching

### 10.1 Pre-solved replay instead of re-solving
[PresolvedReplay.cs](src/GridSim.Core/Simulation/PresolvedReplay.cs) - a cursor over exported solved
snapshots; nothing is re-solved at playback. The map topology is constant across frames, so **one** laid-out
model (`GraphLayout.EnsureLayout`, memoized with `??=` on first frame) drives the renderer while only the
result changes. Sidecar telemetry CSV parsed once into a tick-keyed dictionary.
- **Audit note:** replay correctness is entirely inherited from the exporter - a stale or mismatched
  `exports/` directory replays wrong physics with full confidence.

### 10.2 Deterministic layout with size-adaptive algorithms
[GraphLayout.cs](src/GridSim.Core/Layout/GraphLayout.cs) - Fruchterman-Reingold for small/medium graphs;
above `LargeGraphThreshold = 2000` falls back to a linear-time concentric-ring layout; radial-tree layout for
tree components. The O(m^2) FR inner loop's iteration budget scales down with size
(`Clamp(120_000/m, 90, 450)`) "so a big case doesn't stall the UI thread". Models that already carry real
coordinates skip layout entirely. Fully deterministic: no unseeded `Random`, no `DateTime`, no hashing of
unordered collections - layout is display-only and never alters physics.

### 10.3 Web projection: topology once, state per tick, size-gated extras
[Dtos.cs:22-23](src/GridSim.Web/Projection/Dtos.cs) / [SimWebSocket.cs:117](src/GridSim.Web/Api/SimWebSocket.cs) -
the topology payload is sent once and cached client-side; per-tick messages carry state only.
[FrameProjector.cs:113-147](src/GridSim.Web/Projection/FrameProjector.cs) - cheap experimental models run
every frame, but the O(generators x solve) tracing/carbon models are skipped above 1,500 buses so per-frame
cost stays bounded. Per-fuel breakdown computed once and shared across panels; SCADA register cached against
the model shape; the case catalog's filesystem discovery runs once per process.

---

## 11. Prioritised parallel analysis cycle (`src/GridSim.Core/Scheduling/`)

The single largest structural optimization in the live path, and the one this catalogue exists to guard:
the Newton-Raphson base solve is inherently single-core, but **everything a control room needs *after* the
base case converges** - N-1 contingency sweeps, harmonics, RoCoF screening, physics conformance, the watch
list - reads the *same* solved state and is embarrassingly parallel. The **cycle engine** does one single-core
base solve per cycle, shares the immutable solved frame to every downstream analysis, and fans them out across
a worker pool. It is a scheduling optimization, so its correctness rests on two claims - *no job mutates the
shared frame*, and *deferral never becomes starvation* - both of which are the kind of promise this document
flags as an audit surface.

### 11.1 Zero-copy shared frame + priority-tiered worker-pool fan-out
[`src/GridSim.Core/Scheduling/`](src/GridSim.Core/Scheduling/) - `CycleScheduler`, `CycleRunner`,
`AnalysisJob`, `JobPriority`, `CycleBudget`, `CycleResult`, `AnalysisJobLibrary`.
- **Mechanism:** the shared input is the existing immutable `SimFrame` (`GridModel` + `PowerFlowResult`).
  Both are immutable-by-convention, so "copy the result to each job" is a **shared reference - zero deep-copy**.
  Each unit of post-solve work is an `AnalysisJob` (stable `Id`, a `JobPriority`, a `SeedCoreMs` cost seed, and
  a `Run(CycleContext, CancellationToken)` delegate); divisible work such as a full N-1 sweep is expressed as
  many small jobs with stable ids (`n-1:lp:0`, `n-1:lp:1`, ...). `AnalysisJobLibrary` **wraps existing analyses
  as jobs - nothing is re-implemented**: `ContingencyAnalysis.ScreenBranchesParallel` (hot set as `High`,
  branch-index shards as rotating `Low`), `HarmonicPenetration.Solve`, `RocofScreening.Screen`,
  `PhysicsLaws.Audit`, `WatchList.Assess`. `CycleScheduler` drains High-before-Low on a `Parallel.For` over an
  atomic cursor.
- **Speeds up:** the whole post-solve analysis suite in the live/control-room loop - it turns a serial pass
  over every screen into a single parallel fan-out over the solved frame, using the cores the base solve leaves
  idle.
- **Claim/guard:** the budget currency is **core-milliseconds** - with `W` workers and a `windowMs` window one
  cycle owns `W x windowMs` of compute (28 cores x 1000 ms = 28,000 core-ms), so the budget **scales with core
  count** while the window stays the wall-clock deadline (`CycleBudget`, `CycleOptions.WindowMs`/`Workers`).
  **`High` always completes and is never cancelled**; `Low` is admitted only while budget remains and any `Low`
  not served this cycle **keeps its place at the FRONT of the rotating pool** so a later cycle covers it -
  round-robin coverage, **no starvation**. Admission is driven by `JobCostModel`, a per-job EWMA of measured
  core-ms. The scheduler is **not re-entrant** (cycles are sequential; only the fan-out *inside* a cycle is
  parallel). The two load-bearing safety properties - no job mutates the shared frame, and rotation covers
  every shard without starving - are pinned by `CycleSchedulerTests` (budget = Wxwindow, High-always, rotation
  coverage, worker-count-independent results, no-frame-mutation, faulted-isolation). **The one sharp edge the
  audit must remember:** `PowerFlowResult.Vm`/`VaDeg`/`PInjMw`/`QInjMvar` are bare `double[]`, so the
  read-only-by-convention treatment is enforced only by the test, not by the type.
- `ResidentCycleService` is the host-agnostic driver: a UI hands each solved frame to `Offer(frame, baseMs)`,
  it runs the scheduler **off** the caller's thread, **coalesces to the newest frame**, publishes the latest
  `CycleResult` + rolling shard coverage, and raises `Updated` on completion. Both the Blazor web and WPF
  control rooms wrap it, so engine and telemetry are shared with **no WPF<->web copy-drift**.

### 11.2 Pipelined backlog - filling the cores idle during the serial base solve **[opt-in / scaffolded]**
`CycleOptions.PipelineBacklog`.
- **Mechanism:** cycles are sequential and the base NR solve is single-core, so while it runs the other workers
  would sit idle. The opt-in flag reserves the obvious win: while cycle *N*'s base solve occupies one core, the
  remaining workers drain cycle *N-1*'s `Low` backlog against the previous (stale-by-one) frame.
- **Claim/guard:** correct for deferrable work **by definition** - such jobs are already best-effort and out of
  date by up to a cycle - and the stale input is labelled `CycleContext.FromStaleFrame` so a consumer can tell.
  Scaffolded; the `ResidentCycleService` frame-coalescing already ensures heavy fan-out never stalls the step
  loop, which is the same idle-core problem approached from the other side.

### 11.3 Preemptive solver cancellation (solver-core change) **[in-flight]**
[PowerFlowOptions.cs](src/GridSim.Core/PowerFlow/PowerFlowOptions.cs) - `PowerFlowOptions.Cancellation`.
- **Mechanism:** a new `CancellationToken` (default `None`). `NewtonRaphsonPowerFlow` and
  `SparseNewtonRaphsonPowerFlow` check it at the **top of every inner iteration** (~ms granularity), so a long
  deferrable solve aborts at the cycle deadline instead of overrunning the window.
  `ContingencyAnalysis.ScreenBranchesParallel` gained an optional `CancellationToken ct = default` wired onto
  `ParallelOptions.CancellationToken`, so a deferred N-1 shard aborts between outages.
- **Speeds up:** nothing on its own - it is the mechanism that lets `Low` respect the deadline (section 11.1) without
  the scheduler having to kill threads.
- **Claim/guard:** **`default`/`None` is a guaranteed no-op** - `ThrowIfCancellationRequested` never throws for
  a `None` token, so **every existing solve is bit-for-bit unchanged**. This is exactly the "a fast/optional
  path must not diverge from the reference" contract sections 2-3 are built around, and it is pinned rather than
  asserted: 73 solver-regression tests stay green and `SolverCancellationTests` asserts **exact `Vm`/`VaDeg`
  equality** between the token and no-token paths (plus cancelled-token abort, and Low-cancelled/High-not).

### 11.4 Batched GPU dense solve **[in-flight]**
`IBatchedLinearSolver` (`GridSim.Core/Abstractions`), `CpuBatchedLuSolver` (`GridSim.Core/PowerFlow`),
`GpuBatchedDenseSolver` (`GridSim.Gpu`).
- **Mechanism:** `IBatchedLinearSolver.SolveBatch(int count, int n, double[] aFlat, double[] bFlat)` solves
  `count` independent same-sized `nxn` systems in one call over row-major flat buffers; the solution overwrites
  each system's `bFlat` segment and a per-system success mask is returned (a singular system's segment is
  filled `NaN`/`false` - **it never fails the whole batch**). `GpuBatchedDenseSolver` is ILGPU
  Gaussian-elimination-with-partial-pivoting - the same algorithm as `GpuDenseLinearSolver` (section 8.2) with a batch
  index threaded through every kernel - factoring one flat augmented buffer for the whole batch **under one
  `GpuContext.Lock` acquisition and one device synchronisation**, amortising the per-solve launch + global-lock
  overhead (section 8.1) across the batch. `SolverBackend.DenseBatch()` returns it for CUDA/OpenCL, else the CPU
  reference.
- **Speeds up:** a contingency sweep of many same-sized outages becomes a handful of batched dispatches instead
  of hundreds of individually-locked single solves. (Batching a full AC N-1 across divergent NR iterations, and
  memory-chunking large batches, is future work.)
- **Claim/guard:** `CpuBatchedLuSolver` - which just loops `DenseLuSolver` across the batch in parallel - is the
  **ground truth** the GPU path is validated against; `GpuBatchedDenseSolver` **falls back to it when no device
  is present** (always safe to construct), and ROCm, which has no native batched entry point yet, uses the CPU
  batched reference. `BatchedSolverTests` pins CPU-batched == per-system LU, singular isolation, and
  GPU-vs-CPU agreement.

---

## 12. Index of explicit "bit-for-bit / deterministic" claims

These are the places where an optimization's safety rests on an equivalence claim. They deserve regression
pinning, because a violation is silent by construction:

| Claim | Where | Enforced by |
|---|---|---|
| SIMD dense LU == scalar textbook LU at every width | `SimdKernels.cs:21-31`, `DenseLuSolver.cs:14-20` | kernel design (no FMA); reference diffs (residual/pandapower/GPU) |
| RCM ordering reuse == recomputing | `CpuSparseLuSolver.cs:12-17`, `SparseLu.cs:36-43` | by-content pattern compare (CpuSparseLuSolver); comment only (WlsStep) |
| `YBus.BuildSparse` cells == dense build cells | `YBus.cs:64-110` | identical loop order, comment only |
| `AdmittanceStamp` at h=1 == historical inline stamp | `AdmittanceStamp.cs:19-23` | x1.0 exactness argument; power-flow regression suite |
| Island pre-pass no-op on connected models | `IslandPartition.cs:22-25` | fast-path returns input unchanged |
| `RevertQLimits=false` preserves pinned published solutions | `PowerFlowOptions.cs:21-28` | pinned regression tests |
| Batch `SinCos` and `Dot` bit-identical at every tier | `SimdKernels.cs` | `D1CrossTierTests`; `DeterministicMathGuardTests` |
| Complex solver trio agreement | `ComplexLinearSolvers.cs` | `ComplexSolverCrossValidationTests.cs` |
| Layout determinism (display-only, seeded) | `GraphLayout.cs:16-19` | code inspection; no runtime check |
| GPU dense LU preserves CPU singular contract | `GpuDenseLinearSolver.cs` | device-side flag mirrors CPU floor |
| `PowerFlowOptions.Cancellation` default `None` == no-token solve | `PowerFlowOptions.cs`, NR engines | `SolverCancellationTests` exact `Vm`/`VaDeg` equality; 73 solver-regression tests |
| Cycle jobs never mutate the shared solved frame; `Low` rotation never starves | `src/GridSim.Core/Scheduling/` | `CycleSchedulerTests` (no-frame-mutation, rotation coverage, worker-count-independent) |
| CPU/GPU batched dense solve == per-system LU | `CpuBatchedLuSolver.cs`, `GpuBatchedDenseSolver.cs` | `BatchedSolverTests` (batched == per-system, singular isolation, GPU-vs-CPU) |
