# 4. Sparse and GPU numerics

This chapter specifies the linear algebra under every solver in GridSim: CSR storage and its builders, the
reverse Cuthill-McKee (RCM) ordering, the sparse direct LU, the dense real and complex LUs, ILU(0) with BiCGSTAB,
Householder QR, the eigen solvers behind modal analysis, the vector kernels (`SimdKernels`) that the dense paths
share, the GPU backends, and the one table of numerical tolerances. It states the order in which each routine
performs its arithmetic, because GridSim's contract is bit-identity: the same bits across runs, across parallel
paths, across CPU vector tiers where a path claims it, and between the dense and sparse Newton engines. It also
states which paths are outside that contract, exactly.

The CPU code lives in `src/GridSim.Core/Numerics/` (storage, factorisations, eigen solvers, kernels, tolerances),
with the dense real LU and the shared Newton step in `src/GridSim.Core/PowerFlow/`. The device code lives in
`src/GridSim.Gpu/` behind the `ILinearSolver`, `ISparseLinearSolver` and `IBatchedLinearSolver` seams in
`src/GridSim.Core/Abstractions/`. Who calls what is in [ch. 3, Power flow](<0003 Power flow.md>) (the Newton
step), [ch. 6, State estimation](<0006 State estimation.md>) (the gain factorisations) and
[ch. 30, Small-signal and impedance stability](<0030 Small-signal and impedance stability.md>) (the eigen
solvers). Measured timings of the backends are in
[user ch. 28, Benchmarking and GPU](<../manual/0028 Benchmarking and GPU.md>).

## Algorithm

### CSR storage and the builders

`CsrMatrix` (`src/GridSim.Core/Numerics/Sparse.cs`) is compressed sparse row: `RowPtr[n+1]`, `ColIdx[nnz]`,
`Val[nnz]`, columns ascending within each row, and `DiagPtr[n]`, the index of each row's diagonal entry (-1 when the
row has none). `Multiply(x, y)` is the plain SpMV, one left-to-right accumulation per row:

```
y[i] = 0;  for k = RowPtr[i] .. RowPtr[i+1]-1:  y[i] += Val[k] * x[ColIdx[k]]
```

`ComplexCsr` is the same structure with two value arrays `G[]` and `B[]` sharing one pattern: the network
admittance matrix `Y = G + jB`.

**`CsrMatrix.Builder`.** `Add(row, col, value)` drops `value == 0.0` (either sign of zero) and otherwise appends a
triplet. `Build(ensureDiagonal = true)` does, in order:

1. A counting sort of the triplets into rows. Within a row the triplets keep their insertion order.
2. Per row, `Array.Sort(keys, idx, 0, len)` with the 64-bit key `(column << 32) | insertionIndex`. The keys are
   unique, so the sort is stable by construction whatever algorithm the runtime uses, and the entries of one cell keep
   the order they were added in, at every row length.
3. A single pass that sums duplicate columns left to right in that order (`outVal[^1] += v`), which is insertion
   order. A sum that cancels exactly stays as a stored `0.0`.
4. When `ensureDiagonal` is true, a stored `0.0` is inserted at the sorted position of any missing diagonal, so
   `DiagPtr[i] >= 0` for every row.

The consequence: every duplicated cell is summed in insertion order, so the summation order is a property of the
code that stamps the matrix, not of the sort. The pattern builds of the Newton Jacobian (`SparseNewtonRaphsonPowerFlow.BuildJacobianStructure`) and the
estimator gain (`SparseWlsStep.Begin`) stamp the placeholder value `1.0`, which refill overwrites, so their
summation order never reaches a result. The builders whose duplicate stamps carry real values (the EMT nodal
matrices in `EmtEngine` and `ThreePhaseEmt`, where a node's diagonal collects one stamp per branch and shunt; the
susceptance matrix in `DistributionFactors`; the matrix in `PowerFlowTracing`) sum in the order they stamp.

**`ComplexCsr.Builder`.** `Add(row, col, g, b)` drops a stamp whose `g` and `b` are both zero. `Build()` sorts each
row by the 64-bit key `(column << 32) | insertionIndex`. The keys are unique, so the sort is stable by
construction whatever algorithm sorts them, and the stamps of a duplicated cell are summed in exactly the order
they were added. Each output cell starts as `0.0 + g` (and `0.0 + b`), so a first stamp of `-0.0` becomes `+0.0`,
as in a dense array that starts from zero. A zero diagonal is always inserted when missing. The stable order
follows from the keys and is shown in the worked example below; no test pins a row of more than 16 stamps for this
builder. It is what makes the sparse Y-bus bit-for-bit the dense Y-bus: the dense `YBus.Build` and `SparseNewtonRaphsonPowerFlow.BuildY` add
the branch stamps, then the bus shunts, in model order, and the sparse build replays that order per cell.

### Reverse Cuthill-McKee ordering

`SparseLu.ReverseCuthillMcKee` (`src/GridSim.Core/Numerics/SparseLu.cs`) orders the graph of the matrix made
symmetric by taking the union of `A` and `A^T` off-diagonals. It depends on the structure only:

```
1. adjacency(i) = sorted, de-duplicated { j != i : A(i,j) stored or A(j,i) stored }
2. degree(i)    = |adjacency(i)|
3. byDegree     = all nodes sorted by ascending degree (Array.Sort with a degree comparer)
4. for each node s in byDegree, if not visited:
       visit s; queue = [s]
       while queue not empty:
           u = pop front; append u to order
           nbr = unvisited neighbours of u in adjacency order, marked visited
           sort nbr by ascending degree (Array.Sort with the same comparer); push all to the queue
5. order = reverse(order)
```

Each connected component starts from its lowest-degree node. The two sorts are introsorts with a degree
comparer, so which of two equal-degree nodes comes first is decided by the sort algorithm. That algorithm is GridSim's
own copy of the .NET 10 introsort (`FrozenIntroSort`), not the runtime's `Array.Sort`, so a runtime release that retuned
its sort cannot change the permutation; on .NET 10 the two give exactly the same order (`FrozenIntroSortTests`). The
ordering is therefore a pure function of the stored pattern on every runtime. "Stored" matters: the production Jacobian is the structural superset of all four
blocks (cells that are numerically zero at the start point are still stored), so the ordering is computed from that
superset and does not change as values move.

### The sparse LU (Gilbert-Peierls)

`SparseLu` (`SparseLu.cs`) is the entry point and `SparseLuFactorization`
(`src/GridSim.Core/Numerics/SparseLuFactorization.cs`) holds the factors. The factorisation is left-looking
Gilbert-Peierls with partial pivoting over the RCM column order:

```
L U = P A Q
Q : pivot step k -> original column q[k]            (RCM)
P : original row i -> pivot step pinv[i]            (partial pivoting)
L : unit lower triangular, CSC, unit diagonal stored FIRST in each column
U : upper triangular, CSC, pivot stored LAST in each column
```

Per column `k = 0 .. n-1`, with `col = q[k]`:

```
1. reach  : depth-first search over the graph of the L columns built so far, started from every row index of
            A(:,col); output is the non-zero pattern in topological order (iterative DFS, reverse postorder)
2. x      : scatter A(:,col) into the work vector, then for each pattern row j in topological order whose
            pinv[j] >= 0:  for each below-diagonal entry (i, l) of L column pinv[j]:  x[i] -= l * x[j]
3. pivot  : among pattern rows i with pinv[i] < 0, take the largest |x[i]|; the FIRST maximum in pattern order
            wins a tie (strict >)
            pattern rows with pinv[i] >= 0 are written to U(pinv[i], k) = x[i]
4. test   : if no candidate row exists, or best <= PivotFloor(max|A|), the factorisation returns null
5. store  : U(k,k) = x[ipiv]; pinv[ipiv] = k; L(k,k) = 1;
            every other candidate row i:  L(i,k) = x[i] / x[ipiv]
6. clear  : x[i] = 0 over the pattern
```

After the last column the row indices of `L` are remapped from original rows to pivot steps (`li[p] = pinv[li[p]]`).
The pivot floor is `NumericTolerances.PivotFloor(aNorm)`, `1e-12 * max|A_ij|` over the live entries of the matrix being
factored. The comparison is `<=`, so an exact zero is always singular, including the all-zero matrix. A `NaN` entry
throws `ArgumentException` before any elimination. Pivoting is plain partial pivoting by magnitude: there is no
threshold preference for the diagonal, so the pivot order is whatever the largest magnitude dictates, and the same
rule is applied in the dense LU (below), which is why the two direct solvers reject the same near-singular matrices
(`NumericTolerancesTests.Sparse_and_dense_LU_reject_the_same_near_singular_matrix`).

Storage for `L` and `U` is seeded at `max(2*nnz + n, 16)` entries and doubles whenever a column could overflow it.

**Symbolic and numeric work.** Pattern discovery is not separated from the numerics: the DFS in step 1 runs on every
factorisation, because the pattern of column `k` depends on the pivot rows chosen so far. What is reused across
factorisations is the ordering `Q` and, for the estimator, the CSC input arrays. Nothing about the factors
themselves is reused: every factorisation recomputes `L`, `U` and `pinv` from the values.

**Solve.** `Solve(b)` and `SolveInto(b, x, work)` perform identical arithmetic, operation for operation; `SolveInto`
writes into caller-owned buffers of length at least `n` (only the first `n` entries are used; a longer buffer's tail is
left untouched) and allocates nothing.

```
work[pinv[k]] = b[k]                                  for k = 0 .. n-1       (P b)
for j = 0 .. n-1:       xj = work[j];  work[li[p]] -= lx[p] * xj   for the below-diagonal entries p of L column j
for j = n-1 .. 0:       xj = work[j] / (pivot of U column j);  work[j] = xj;
                        work[ui[p]] -= ux[p] * xj                  for the above-diagonal entries p of U column j
x[q[k]] = work[k]                                     for k = 0 .. n-1       (Q y)
```

Every update is a separate multiply and subtract. The claim that the solve rounds the same on every CPU rests on the
JIT not contracting them into a fused multiply-add; the explicit-tier seam tests cover `SimdKernels`, not this solve.

**The reuse tiers.** `SparseLu` exposes four entry points, each dropping repeated work for callers with more
structure.

| Entry point | Work done | Callers |
|---|---|---|
| `Solve(a, b)` | `Factor` and one solve | none in production; `SparseLuTests` |
| `Factor(a)` | CSR to CSC (explicit zeros dropped), RCM, numeric factorisation; the result serves many right-hand sides | `DistributionFactors`; the EMT initial-condition solve (`EmtEngine`); `PowerFlowTracing` |
| `Ordering(a)` then `FactorWithOrdering(a, q)` | RCM once; each call converts CSR to CSC (explicit zeros dropped) and factors with the given `q`. Bit-identical to `Factor`, because RCM is a pure function of the pattern | `CpuSparseLuSolver` (every Newton step); `RealExpandedComplexSolver`; the EMT nodal matrix in `EmtEngine` and `ThreePhaseEmt` |
| `Ordering(a)` then `FactorPrepared(n, ap, ai, ax, q)` | numeric factorisation of an already prepared CSC matrix; no conversion, explicit zeros kept as structural entries | `SparseWlsStep` |

`CpuSparseLuSolver` (`src/GridSim.Core/PowerFlow/CpuSparseLuSolver.cs`) caches `q` together with the `RowPtr` and
`ColIdx` of the matrix it ordered, and compares each new matrix's arrays by content. The sparse Newton engine hands
it the same `CsrMatrix` instance every iteration of a Q-limit pass. The ordering is computed once per distinct
pattern for the life of the solver instance, across passes and across `Solve` calls; a change in `RowPtr` or
`ColIdx`, such as a bus-type switch rebuilding the pattern, triggers a fresh ordering. `RealExpandedComplexSolver` keeps the same cache across the
harmonic orders of a scan; the cache is per instance and is not thread-safe, so parallel scans use one solver per
worker.

`SparseWlsStep.Begin` computes once per bad-data pass: the structural columns of each measurement row, the gain
pattern `G = H^T W H` (through `CsrMatrix.Builder` with placeholder values), the RCM ordering of that pattern, and
for each measurement row the slot in the gain's value array of every column pair. Each Gauss-Newton iteration
then refills `G` and the right-hand side through those slots, and calls `FactorPrepared`.

**Inverse quadratic forms.** The estimator's residual-covariance stage needs `h^T G^-1 h` for every measurement
row. `SparseLuFactorization.InverseQuadraticForm` evaluates it against the factors of one `G` without a full solve
per row: the forward sweep starts at the first non-zero pivot-step slot, the back sweep stops at the smallest slot the
form reads, columns whose multiplier is exactly zero are skipped, and only the requested entries are read back.
Each trimming is chosen so the result equals, bit for bit, the reference
`x = Solve(h_dense); q = sum hVal[p] * x[hCol[p]]`. `EvaluateBlock` solves up to 8 rows at once in a lane-interleaved
workspace, with a separate vector multiply and subtract per lane at every SIMD tier (never a fused multiply-add), so each
lane rounds as the scalar statement does. If any factor entry is not finite the evaluator falls back to the full
reference arithmetic.

### The dense LU

`DenseLuSolver` (`src/GridSim.Core/PowerFlow/DenseLuSolver.cs`) is Gaussian elimination with partial pivoting on a
flat row-major copy of `A`. `SolveInPlace` copies the matrix, runs `FactorSolveInPlace`, and writes the factored
matrix back into the caller's array. `FactorSolveInPlace(m, b, n)` is the shared body:

```
aNorm      = max |m[i]|
pivotFloor = 1e-12 * aNorm
for k = 0 .. n-1:
    p = k;  max = |m[k,k]|
    for i = k+1 .. n-1:  if |m[i,k]| > max: max = |m[i,k]|; p = i        (first maximum wins a tie)
    if max <= pivotFloor: throw InvalidOperationException("Near-singular matrix in LU solve ...")
    swap rows k and p over columns k..n-1, and b[k] with b[p]
    for i = k+1 .. n-1:
        factor = m[i,k] / m[k,k]
        if factor == 0: continue
        m[i, k..n-1] -= factor * m[k, k..n-1]          SimdKernels.SubtractScaledRow (bit-exact)
        b[i] -= factor * b[k]
back substitution, scalar:  for i = n-1 .. 0:  sum = b[i];  for j = i+1 .. n-1:  sum -= m[i,j]*b[j];  b[i] = sum / m[i,i]
```

The pivot search, the row swaps and the back substitution are scalar and keep the textbook summation order. The row
update is the only vectorised step, and it is the bit-exact kernel: one multiply, then one subtract, per lane, so
the result is the scalar elimination's at every vector width. `DenseLuSolver` calls `SimdKernels.SubtractScaledRow`
directly; `DenseLuKernels.SubtractScaledRow` is a forwarder with no caller in `src/`. A singular matrix throws; the batched wrapper catches per system.

On the CPU the dense Newton engine does not use this LU for its step: `NewtonRaphsonPowerFlow` built with a null or
`DenseLuSolver` linear solver copies its dense Jacobian into the shared CSR pattern and takes the canonical sparse
step (below), so the dense and sparse engines solve every Newton step with the same factorisation. `DenseLuSolver` is
the default linear solver of the dense estimator methods (`ConstrainedDenseWlsStep`, `FeasibleWlsStep`) and of the
dense gain-rank observability proof that `CanonicalWlsStep` runs at or below `SparseBusThreshold` buses (a plain WLS or
robust estimate on the CPU is solved by the sparse step at every size; `DenseWlsStep` solves an estimate only when the
estimator is given a non-CPU `ILinearSolver`), the CPU fallback of the
GPU dense solvers, and, through `FactorSolveInPlace`, the body of `CpuBatchedLuSolver`. A third dense LU lives
inside `BadDataAnalysis.LuFactorization` (the estimator's residual-covariance factor); it uses the same pivot rule
and `SubtractScaledRow` for the update, with a floor of `PivotFloor` of the gain's largest entry. Its back
substitution is not scalar: it calls `SimdKernels.Dot` (see the kernel table).

### The dense complex LU

`DenseComplexLuSolver` (`src/GridSim.Core/Numerics/ComplexLinearSolvers.cs`) is the same elimination over
`System.Numerics.Complex`, the pivot chosen by `Complex.Magnitude` (first maximum wins). It has two modes, selected
by `clampSingular`:

- **Honest.** `Solve` (the `IComplexLinearSolver` seam) and `SolveDenseOrNull` return `null` when a pivot or a back
  substitution diagonal is at or below `PivotFloor(max|A_ij|)`.
- **Clamping.** `SolveDense` and `FactorDense` never return `null`: a pivot at or below the floor is replaced by the
  floor itself (`ClampedPivot`, or 1.0 on the all-zero matrix), and a back substitution diagonal at or below the floor
  gives a zero component. `DenseComplexLu.ClampedPivots` counts the clamps; callers that need a true solution check it.
  The clamp is kept because inverse iteration in `GeneralEigen` factors `A - shift*I` with the shift beside an
  eigenvalue, where a vanishing pivot is expected and the clamped factor still gives the eigenvector direction.

A `NaN` entry throws `ArgumentException` in both modes (`MaxMagnitude`).

`FactorDense` factors once and returns a `DenseComplexLu` that solves any number of right-hand sides at O(n^2) each by
replaying the recorded row swaps and multipliers; each solve is bit-identical to the one-shot `SolveDense`. The
factorisation runs column by column ("left-looking") on a column-major copy, in blocks of `FactorBlockColumns` (16)
columns, so each multiplier column is loaded once per block. The inner update is
`SimdKernels.SubtractScaledComplexColumn` (and `SubtractScaledComplexColumns4` for four destination columns sharing one
multiplier column): `c[dst+t] -= c[l+t] * u`, skipping any `t` whose multiplier equals zero exactly as the scalar loop
does. Each lane computes the four real products of `Complex` multiplication, each rounded on its own, the real part as
`l.Re*u.Re + (-(l.Im*u.Im))` and the imaginary part as the same two-term sum, then `dst - product`: the scalar
statement's operations, with no fused multiply-add, at every tier. The block width changes only the speed.

`RealExpandedComplexSolver` solves a complex system through the real `2n x 2n` expansion:

```
(G + jB)(xr + j xi) = br + j bi      <=>      [ G  -B ] [xr]   [br]
                                              [ B   G ] [xi] = [bi]
```

It assembles that matrix with `CsrMatrix.Builder`, orders and factors it with `SparseLu` (ordering cached across
matrices with the same `RowPtr` and `ColIdx`), and its factorisation handle back-substitutes each complex
right-hand side. `SolveInto` is operation for operation `Solve` with reusable scratch vectors taken by an atomic
exchange, so two threads sharing one factorisation never share scratch. It is the scalable default for harmonics,
the fault network and impedance scans. `SparseComplexBiCgStabSolver` is a third, independent path: Jacobi-preconditioned
BiCGSTAB in complex arithmetic with conjugated inner products `(a, b) = sum conj(a_i) b_i`; it exists to cross-validate
the two direct solvers.

### ILU(0) and BiCGSTAB

`Ilu0.Factor(CsrMatrix)` (`Sparse.cs`) is an incomplete LU on the pattern of `A` with no fill: the IKJ sweep over each
row's strictly-lower entries.

```
lu = copy of A.Val
for i = 0 .. n-1:
    for each stored k in row i with column j < i:
        l = (lu[diag j] != 0) ? lu[k] / lu[diag j] : 0;   lu[k] = l
        if l != 0: for each stored entry kk in row j right of the diagonal:  if (i, col kk) is stored: lu[pos] -= l * lu[kk]
    rowScale = max |A(i, :)|;  guard = 1e-12 * (rowScale > 0 ? rowScale : 1)
    if |lu[diag i]| < guard: lu[diag i] = +/- guard (the sign of the pivot; positive for zero)
```

The guard is relative to the row's scale in the original matrix. Regularising a preconditioner pivot is legitimate:
the preconditioner only steers the iteration, and the residual test against the true matrix decides success.
`Apply(r, z)` solves `(LU) z = r` with a unit-lower forward sweep and an upper back sweep.

`BiCgStab.Solve(a, b, x, precond, tol, maxIter)` is right-preconditioned BiCGSTAB with `x0 = 0`:

```
r = rhat = b;  p = v = 0;  rho = alpha = omega = 1
loop it = 0 .. maxIter-1:
    rhoNew = (rhat, r);   fail if |rhoNew| < 1e-300 or NaN
    beta   = (rhoNew/rho) * (alpha/omega);   p = r + beta*(p - omega*v)
    ph = M^-1 p;  v = A ph;   rv = (rhat, v);   fail if rv == 0 or NaN;   alpha = rhoNew / rv
    s = r - alpha*v;   if ||s||/||b|| < tol:  x += alpha*ph;  return true
    sh = M^-1 s;  t = A sh;   omega = (t,s)/(t,t)  (0 if (t,t) = 0)
    x += alpha*ph + omega*sh;   r = s - omega*t;   rho = rhoNew
    if ||r||/||b|| < tol: return true;   if omega == 0: fail
return false (iteration cap)
```

The defaults are `NumericTolerances.DefaultKrylovTolerance` (1e-10) and `DefaultKrylovMaxIterations(n)`
(`4n + 200`); a non-positive or infinite `tol`, or a negative `maxIter`, throws `ArgumentOutOfRangeException`. A zero
right-hand side returns `true` with `x = 0`.

**When it runs.** In both Newton engines ILU(0) with BiCGSTAB is the last fallback of
`NewtonShell.SolveStep` (`src/GridSim.Core/PowerFlow/NewtonShell.cs`):

```
sol = primary.Solve(J, f)                            primary: CPU sparse LU, or a GPU/ROCm solver
if sol == null and the primary is not the CPU LU:    sol = CpuSparseLuSolver.Solve(J, f)      (exact retry)
if sol == null:  ILU(0)-preconditioned BiCGSTAB on J,
                 tol = min(1e-10, PowerFlowOptions.Tolerance * 1e-3),  maxIter = 4m + 200,  x0 = 0
                 not converged -> the step fails
the step must be finite; a NaN or infinite component fails the step (nothing is applied)
```

A failed step stops the Newton loop and the engine reports non-convergence. Because the sparse LU rejects at the
relative floor, the fallback is reached for any Jacobian that is singular to working precision, not only an exactly
singular one. The dense Newton engine with a GPU dense solver (`--gpu` on a model at or below 200 buses) bypasses
`SolveStep`: it calls the device solver as given, checks the step for finiteness and has no CPU retry.

### Householder QR

`HouseholderQr.SolveLeastSquares(a, b)` (`src/GridSim.Core/Numerics/HouseholderQr.cs`) minimises `||A x - b||` for
`m >= n` without forming `A^T A`, which would square the condition number. It is the solver of the `Qr` estimation
method (`QrWlsStep`); the rows of `A` and `b` are pre-scaled by `sqrt(w)`.

```
rt[j,i] = A[i,j]                                  (column-major working copy: every inner loop is contiguous)
floor   = 1e-12 * max|A_ij|                       (NaN entry throws ArgumentException)
for k = 0 .. n-1:
    norm  = sqrt(sum_{i>=k} rt[k,i]^2);  if norm == 0: continue
    alpha = (rt[k,k] > 0) ? -norm : norm
    v     = rt[k, k..m-1];  v[k] -= alpha;  vnorm2 = sum v[i]^2;  if vnorm2 <= 0: continue
    for j = k .. n-1:   s = 2 * (sum v[i]*rt[j,i]) / vnorm2;   rt[j,i] -= s * v[i]
    s = 2 * (sum v[i]*c[i]) / vnorm2;   c[i] -= s * v[i]
back substitution for i = n-1 .. 0:  sum = c[i] - sum_{j>i} rt[j,i]*x[j]
    if |rt[i,i]| <= floor: throw InvalidOperationException("rank-deficient least-squares system ...")
    x[i] = sum / rt[i,i]
```

`m < n` throws `ArgumentException`. The column-major layout changes only memory access; the arithmetic order is the
original row-major routine's (`HouseholderQrLayoutBitIdentityTests`).

### Eigen solvers

**`GeneralEigen`** (`src/GridSim.Core/Numerics/GeneralEigen.cs`) serves modal analysis ([ch. 30](<0030 Small-signal and impedance stability.md>));
`ModalAnalysis.Analyze` calls `Decompose` on both its undamped swing path and its damped or inverter-augmented path,
and on both falls back to `Eigenvalues` (participation zero, the failure recorded on the result) when the matrix is
defective. `SmallSignalConsistency` (`src/GridSim.Core/Physics/Experimental/`) calls `Eigenvalues` alone. It is for the real, non-symmetric state matrix of tens of states (the largest participation run
in the repository is order 1,902, which is why the parallel inverse iteration exists).

```
eigenvalues:  balance (Parlett-Reinsch, radix 2)  ->  Householder reduction to upper Hessenberg
              ->  Francis double-shift implicit QR (EISPACK hqr): 1x1 blocks real, 2x2 blocks conjugate pairs
              exceptional shifts at iterations 10, 20, 30 and 40 (EISPACK); past 60 iterations on one eigenvalue,
              one every 10 (LAPACK dlahqr KEXSH); at most 30 * max(10, n) iterations per eigenvalue (dlahqr ITMAX),
              else InvalidOperationException
sort          by (Re, Im)
right vectors: inverse iteration on the ORIGINAL A, one per eigenvalue mu
               shift = mu + eps * (1 + j*0.37),   eps = 1e-10 * max(scale, |mu| + 1)
               start v_i = (1 + 0.017 i) + j*0.003 (i + 1), normalised
               factor (A - shift*I) ONCE with DenseComplexLuSolver.FactorDense, then
               w = Solve(v); normalise; stop at 40 sweeps or when |1 - |v^H w|| < 1e-14
repeated eigenvalues: copies of one eigenvalue (|lambda_j - lambda_k| <= 1e-6 * max(scale, |lambda_k|)) whose
               plain vectors coincide (outside-span^2 <= 2e-6) are recomputed with deflation against the vectors
               already found; a deflated vector with residual |A v - lambda v| > 1e-6 * scale throws
               DefectiveEigenproblemException
left vectors:  W = V^-1 (condition-guarded complex Gauss-Jordan, pivot floor 1e-12 * max|V|);
               psi_k = conj(W[k,:]);  |psi^H phi - 1| above max(1e-9, 1e-6) throws DefectiveEigenproblemException
```

A singular `V` also throws `DefectiveEigenproblemException`: a defective matrix has no biorthonormal basis, and
participation factors are never fabricated. `ModalAnalysis` catches the exception, deflates the rigid-body
(centre-of-inertia) mode of the undamped swing system before decomposing, and records the failure on the result
when the reduced matrix is still defective.

For order 32 and above the plain inverse iterations run on worker threads. Iteration `k` reads only `A`, `values[k]`
and constants fixed before the loop and writes only its own column, so the bits do not depend on the worker count;
the number of workers is the smallest of the processor count, `n`, the caller's `maxDegreeOfParallelism` when it is
positive, and a memory budget (`InverseIterationWorkers`). Each worker costs `32 n^2` bytes. The budget is a quarter
of the memory the runtime may use, or half of the memory free at the last garbage collection when that is smaller. A failure in any iteration rethrows the failure of the lowest `k`, as the
sequential loop would. The repeated-eigenvalue pass reads earlier columns and stays sequential.

**`SymmetricEigen`** (`SymmetricEigen.cs`) is cyclic Jacobi for small symmetric matrices. It stops when
`sum_{p<q} a_pq^2 <= (eps * ||A||_F)^2` with `eps = 2.220446049250313e-16`, throws if 100 sweeps are exhausted or an
input entry is not finite, skips an off-diagonal below `1e-300`, and returns eigenvalues ascending. Only the
experimental continuum-wave model (`ContinuumWaveModel`) calls it; modal analysis does not.

### Batched dense LU

`IBatchedLinearSolver.SolveBatch(count, n, aFlat, bFlat)` solves `count` independent `n x n` systems; `aFlat` and
`bFlat` are the row-major matrices and right-hand sides end to end, and each solution overwrites its own `bFlat`
segment. The result is a per-system success mask: a singular system's segment is filled with `NaN` and its flag is
`false`; the batch never fails because one system does. `CpuBatchedLuSolver` runs `DenseLuSolver.FactorSolveInPlace`
on each system's own slice of the arrays with `Parallel.For`; the systems are independent, so the result does not
depend on scheduling. The GPU implementation is described with the GPU backends.

### SimdKernels

`SimdCapabilities` (`src/GridSim.Core/Numerics/SimdCapabilities.cs`) chooses one `SimdTier` at start-up from
`IsHardwareAccelerated` of `Vector512`, `Vector256` and `Vector128`: `Avx512` (8 doubles per step), `Avx256` (4),
`Sse128` (2) or `Scalar` (1). `FmaAvailable` is `Fma.IsSupported || AdvSimd.IsSupported`. On Skylake-SP class Xeons
.NET reports `Vector512.IsHardwareAccelerated = false` by default, and `DOTNET_PreferredVectorBitWidth=512` enables the
8-wide kernels; `gridsim simdcheck` reports the active tier and fails with `--expect Avx512` when it is narrower
([`simdcheck`](<../manual/0019 CLI reference.md#simdcheck>)). Each tier-dispatched kernel has an overload taking an explicit `SimdTier`,
which the tests use to run every tier's code on any machine (the AVX-512 arm runs in software on a CPU without it).

| Kernel | Operation | Bit-exact at every tier | Used by |
|---|---|---|---|
| `SubtractScaledRow` | `dst[t] -= f * src[t]`, a multiply then a subtract per lane | yes | `DenseLuSolver`; `BadDataAnalysis.LuFactorization`; the `ObservabilityAnalysis` rank elimination; the `KalmanStateEstimator` inverse |
| `AddScaledRow(..., fastFma: false)` | `dst[t] += f * src[t]`, a multiply then an add per lane | yes | `DenseWlsStep.FormNormalEquations` (`H^T W H` rank-1 updates), which `DenseWlsStep`, `ConstrainedDenseWlsStep` and `QrWlsStep` all call (`src/GridSim.Core/Estimation/WlsStep.cs`); `WlsStateEstimator` gain formation; `ConstrainedDenseWlsStep` KKT; `FeasibleWlsStep` KKT |
| `AddScaledRow(..., fastFma: true)` | `dst[t] = fma(f, src[t], dst[t])` when `FmaAvailable`, else as above | no: one rounding instead of two, and the result depends on `FmaAvailable` | no numerical path; it remains only as a tested kernel arm, and `DeterministicMathGuardTests.No_numerical_path_selects_the_fused_multiply_add_arm` fails if a caller passes it |
| `SubtractScaledComplexColumn`, `...Columns4` | complex `c[dst] -= c[l] * u`, zero multipliers skipped | yes | `DenseComplexLuSolver.FactorDense` |
| `Dot` | dot product in one defined order: four partial sums `s0..s3` start at `+0`; for `t` below `4 * floor(count/4)`, in increasing `t`, the product `x[t]*y[t]` (rounded on its own, never fused) is added to `s[t mod 4]`; the partials combine as `(s0 + s1) + (s2 + s3)`; the remaining `count mod 4` products are added left to right | yes: every tier performs exactly these IEEE operations (the 512-bit arm adds the low then the high four products into the same four partials, the 128-bit arm holds `s0, s1` and `s2, s3` in two vectors, the scalar arm holds four doubles) | `BadDataAnalysis` (`h^T G^-1 h` in the dense normalised residuals, and the upper back substitution of its LU); `ClassicalMultiMachine` |
| `DotExact` | left-to-right scalar sum, the reference order | yes (it is the scalar loop) | tests |
| `SinCos` | batch sine and cosine: `(sinOut[t], cosOut[t]) = Fdlibm.SinCos(angles[t])` for every element, the same deterministic fdlibm port (`DetMath`) as every other trigonometric call in GridSim | yes: one formula at every width, whatever the batch length or the element's position | `ClassicalMultiMachine` only |

The bit-exact kernels are exact because every lane performs the scalar statement's IEEE operations, each rounded on
its own. A path is covered by the bit-identity contract across tiers only if every kernel it calls is in the "yes"
rows. Every numerical path is: the sparse LU, the sparse solve, the Newton step, ILU(0), BiCGSTAB, Householder QR, the
dense complex LU, the dense estimator steps and `ClassicalMultiMachine` call only "yes" kernels.
`D1CrossTierTests` runs the formerly tier-dependent paths at every tier and pins their bits
(`Dot_is_bit_identical_at_every_tier_and_equals_the_defined_order`,
`Batch_SinCos_is_bit_identical_at_every_tier_and_to_DetMath_for_every_element`,
`Dense_WLS_gain_is_bit_identical_at_every_tier_and_has_no_fused_multiply_add`,
`Classical_swing_trajectory_is_bit_identical_at_every_tier`,
`Formerly_tier_dependent_paths_give_the_pinned_bits_in_every_mode`).

### Deterministic elementary functions and the canonical NaN

The platform's `Math.Sin`, `Math.Cos`, `Math.SinCos`, `Math.Exp`, `Math.Log` and their relatives call the operating
system's C library, whose last bits differ between Windows and Linux libm. GridSim's numerical code does not call
them. It calls `DetMath` (`src/GridSim.Core/Numerics/`), a C# port of fdlibm (`Fdlibm`) whose results are pinned bit
for bit against Java's `StrictMath` (`FdlibmStrictMathVectorTests.Explicit_vectors_match_Java_StrictMath_bit_for_bit`,
`Pseudo_random_digests_match_Java_StrictMath_bit_for_bit`, `SinCos_halves_are_bit_identical_to_Sin_and_Cos`,
`Complex_helpers_are_the_documented_formulas_over_the_deterministic_functions`). The complex helpers
(`DetMath.FromPolar` and the rest) are the documented formulas over those functions. A NaN
where the bits are part of a result (the frequency points and responses) is written as
`DetMath.CanonicalNaN`, the one quiet NaN `0x7FF8000000000000`, so its bytes do not depend on the JIT tier
(`CanonicalNaNTests`). `DeterministicMathGuardTests` scans the compiled `GridSim.Core`, `GridSim.Gda`, `GridSim.Gpu`
and `GridSim.Cli` assemblies and fails the test run if a method outside the deterministic module calls
platform-dependent mathematics, or if a numerical path selects the fused `AddScaledRow` arm.

### The GPU backends

`GridSim.Gpu` supplies device implementations of the three seams on three backends: ROCm/HIP (native AMD), OpenCL
and CUDA through ILGPU. OpenCL and CUDA share one code path, the same ILGPU kernels compiled for either accelerator.
Three rules hold for every GPU solver:

- **FP64 is required.** All arithmetic is `double`. An OpenCL device is accepted only after `GpuContext` runs a
  one-thread smoke kernel that computes `(1 + 2^-40) - 1` in `double` from operands sent by the host and reads back
  exactly `2^-40` (single precision gives 0); a device that fails to compile or execute FP64, or returns a wrong value, is
  reported unavailable ("no usable FP64"). `Fp64Note` and `OpenClFp64Note` name whether a device runs FP64 at
  data-centre rate (A100, H100, V100, Tesla, GH200; AMD Instinct MI50 to MI300) or throttled.
- **Probing never throws.** `GpuContext` (ILGPU: CUDA and OpenCL) and `RocmContext` (HIP) probe once, lazily. On any
  failure (no device, no driver library, an ILGPU exception) the backend is marked unavailable, and every solver
  defers to its CPU counterpart, so the types are safe to construct and call on any machine. `RocmContext` calls
  `hipGetDeviceCount` inside a broad catch, so on a machine without ROCm the first call fails with
  `DllNotFoundException` and no native library is ever loaded; on a device it binds device 0 with `hipSetDevice`,
  reads the name through `hipDeviceGetName`, and loads `hipsolver`, `rocsparse` and `rocblas` by the solvers' own route
  (`RocmInterop.TryLoadLikePInvoke`: the assembly's versioned-name resolver, then the default search, GR-043): if any is
  missing the backend is unavailable rather than throwing on the first solve.
- **All device access is serialised** through one static `Lock` per context (`GpuContext.Lock`, `RocmContext.Lock`):
  every allocation, copy, kernel launch and synchronise takes it, because the CLI fans solves out over
  `Parallel.ForEach`.

**ILGPU dense LU (`GpuDenseLinearSolver`).** Gaussian elimination with partial pivoting over an augmented row-major
`[A | b]` of `n x (n+1)` doubles. Per column `k`, three kernels are queued on the one stream, which executes in
submission order:

```
PivotKernel   1 block, 256 threads: each thread scans rows k+tid, k+tid+256, ... for the largest |aug[i,k]|
              (strict >); a shared-memory tree reduction (strict >, the lower thread wins a tie) picks the
              block's argmax; thread 0 writes the
              pivot row to device memory and sets info = k+1 if the maximum <= pivotFloor
SwapRowsKernel   ncol threads: swap rows k and piv[0] over all columns (no-op when equal)
EliminateKernel  n threads, one per row i > k: factor = aug[i,k]/aug[k,k]; if factor != 0:
                 aug[i, k..ncol-1] -= factor * aug[k, k..ncol-1]
```

The pivot tie-break equals the CPU's first-maximum rule only while at most 256 candidate rows remain (`n - k <= 256`).
Each thread then holds at most one row, and the lowest row index wins an exact tie. With more candidates a thread
holds several rows, and an exact tie can go to a later row: row `k+256`, held by thread 0, beats row `k+1`, held by
thread 1. The batched kernel has the same shape with 128 threads, so its bound is 128 candidate rows.

Back substitution is a column sweep: `BackDivideKernel` (1 thread) computes `x[i] = aug[i,rhs] / aug[i,i]`, then
`BackUpdateKernel` (i threads) subtracts `aug[r,i] * x[i]` from the right-hand side of every earlier row `r`. The pivot
index never touches the host, and the host synchronises exactly twice: after the elimination, to read the
device-side singularity flag, and after the back substitution, to read `x`. A raised flag throws
`InvalidOperationException` ("Near-singular matrix in GPU LU solve"), which is the CPU contract. The pivot floor is
`PivotFloor(max|A_ij|)` computed on the host while flattening and passed to the kernel. A wrong-sized `A` throws
`ArgumentException`; with no device of the requested type the solve runs `DenseLuSolver` instead.

**ILGPU sparse solver (`GpuSparseLinearSolver`).** Jacobi (diagonal) preconditioned BiCGSTAB from ILGPU kernels, one
thread per row for the CSR SpMV and for each vector update, and a one-block, 256-thread shared-memory reduction for
each dot product, which returns one scalar to the host. Each dot synchronises, so the host stalls at exactly the
points BiCGSTAB needs a scalar (`rho`, `(rhat, v)`, `(t, t)`, `(t, s)` and the residual norms); the vector updates
between them are queued without a synchronise. It returns `null` on breakdown (`|rho| < 1e-300`, a zero `(rhat, v)`),
a `NaN` residual, stagnation (`omega = 0`), the iteration cap `4n + 200`, or a non-finite solution, and the Newton
step then retries on the CPU LU. A missing or zero diagonal (no Jacobi preconditioner exists) sends the solve to
`CpuSparseLuSolver` before any device work. The default tolerance is `DefaultKrylovTolerance` (1e-10).

**ILGPU batched dense LU (`GpuBatchedDenseSolver`).** The same five kernels as the single dense solver with a system
index threaded through each: one flat augmented buffer carries the batch (system `s` at stride `n*(n+1)`), the pivot
kernel launches one 128-thread block per system, and the swap, eliminate, divide and update kernels launch over
`count * ncol` or `count * n` threads. The whole batch is factored under one lock acquisition. The elimination and the back
substitution are queued with no host stall between them; the host then calls `Synchronize` once and copies back the
flags and the solutions. Each system carries its own pivot floor, computed from its own `max|A_ij|`, and its own
on-device flag. A singular system is therefore detected after the back substitution has run on it, and the host masks
it afterwards: its segment is returned as `NaN` and its flag as `false`. A batch whose `count * n * (n+1)` exceeds `int.MaxValue` throws
`ArgumentException` (the caller chunks). With no device, or `count == 0` or `n == 0`, the call runs
`CpuBatchedLuSolver`. ROCm has no batched entry point; `SolverBackend.DenseBatch()` returns the CPU solver for ROCm.
Only the `batchsolve` verb constructs a batched solver.

**ROCm dense LU (`RocmDenseLinearSolver`).** LU through the native hipSOLVER API (`hipsolverDgetrf`, `hipsolverDgetrs`,
with the explicit `work` and `lwork` arguments). `A` is transpose-packed into column-major (LAPACK layout); the
workspace is the larger of the two `bufferSize` queries. `devInfo` is read after each call (an unreadable `devInfo`
throws), and because `devInfo` flags only an exactly zero `U(i,i)`, the factored matrix is copied back after `getrf` and
`ThrowIfNearSingular` rejects any `|U(k,k)|` at or below `PivotFloor(max|A_ij|)` through
`NumericTolerances.FirstSingularPivot`. With partial pivoting the CPU's k-th pivot is `|U(k,k)|`, so the same floor
applied to the `U` that `getrf` produced rejects the same matrices as the CPU LU exactly when `getrf` chooses the same
pivot sequence. `getrf` is a blocked vendor routine whose pivot order and rounding are not specified to match, and no
ROCm hardware run exists, so the agreement is not verified on a device. A `NaN` entry throws `ArgumentException`.

**ROCm sparse solver (`RocmSparseLinearSolver`).** Jacobi-preconditioned BiCGSTAB, fully device-resident: the CSR
matrix and every Krylov vector stay on the GPU, the SpMV is `rocsparse_dcsrmv` (after `rocsparse_dcsrmv_analysis`),
the vector algebra is rocBLAS level 1 (`ddot`, `daxpy`, `dscal`, `dcopy`) and the Jacobi apply is the Hadamard product
`rocblas_ddgmm`. Only the convergence scalars and the final solution cross the bus. Its `null` conditions, tolerance
and iteration cap are the ILGPU solver's, and so is the retry on the CPU LU in the Newton step. The P/Invoke
signatures in `RocmInterop` are matched against the ROCm 6.x headers.

**Selection.** `SolverBackend.Select` (`src/GridSim.Cli/SolverBackend.cs`) maps `--cpu` (the default), `--gpu` (auto)
and `--gpu=rocm|opencl|cuda`. Auto tries ROCm, then OpenCL, then CUDA, then the CPU. The order is the code's design choice (native AMD
Instinct first, then OpenCL on open drivers, then CUDA), not a measured ranking. An unavailable requested backend prints a warning to standard error
and falls back to the CPU; a name outside `auto, cpu, rocm, opencl, cuda` throws `ArgumentException`. Nothing outside
the CLI constructs a GPU solver.

Which Newton engine a model gets is the shared rule in `SolverSelector` ([ch. 3, Engine selection](<0003 Power flow.md#engine-selection>)).
`SolverBackend.SparseThreshold` is `SolverSelector.DefaultSparseThreshold` (0, every CPU Newton solve is sparse) on
the CPU and `SolverSelector.DenseSparseCrossoverBuses` (200) on a GPU backend. On a GPU, a model with more than 200
buses gets `SparseLinear()` (device BiCGSTAB, with the CPU LU retry) and a smaller one gets `DenseLinear()` (the device
dense LU, used as given). The GPU dense and sparse results are outside the bit-identity contract; the contract holds on
the CPU path only ([`gpucheck`](<../manual/0019 CLI reference.md#gpucheck>) preflights a backend,
[`bench`](<../manual/0019 CLI reference.md#bench>) compares engines).

**Placement of the workload.** Steady-state power flow runs on the CPU by the shape of the problem. The matrix is
almost entirely zeros: `nnz(Y)` is at most `n + 2 * branches` (parallel branches share a cell and out-of-service branches
are skipped), which grows linearly, while the dense footprint grows with `n^2`. The exact solve is the sparse direct
LU, whose column `k` depends on the fill of columns `0 .. k-1` (sequential by data dependency) and whose pivoting and
fill are data-dependent over irregular index structures; and Newton pays a fresh Jacobian transfer and a
kernel launch on every iteration. The measurements in `docs/developer/benchmarks/server-results/a100-results.md` (each GPU compared with its
own co-located EPYC, one solve at a time, a validated driver stack) confirm it:

- `case2869pegase`: 68 ms `cpu-sparse` (A100 host), 65 ms (H100 host); `cuda-sparse` 4,851 ms and 4,320 ms;
  `cuda-dense` 54,886 ms and 44,496 ms; the CPU dense engine of that run took 46,695 ms.
- The H100 has 3.5 times the A100's FP64 throughput and ran `cuda-dense` 19 percent faster: the work is bound by launch
  and transfer overhead and the sequential LU, not by FP64.
- Contingency screening of `case118` (186 outages): 932 solves per second on 28 CPU threads
  (`ContingencyAnalysis.ScreenBranchesParallel`, safe because `GridModel` is immutable) against 18 on the A100 solving
  serially.
- CPU and CUDA solve exports of `case9`, `case118` and `case300` agree on the converged voltages.

A GPU backend is an independent cross-check, a second implementation reaching the same answer, not a performance path.
On servers with compute cards the first GPU call is dominated by kernel compilation, not by the solve.

## Parameters and defaults

One table holds every numerical tolerance, cap and threshold in `Numerics`, `PowerFlow` and `Estimation`, with its
value and the symbol that defines it. The pivot floors decide only what is declared singular; a floor never enters the
arithmetic of a non-singular solve. Values in `NumericTolerances` are shared: a site that needs the concept reads the
constant. `SharedToleranceRoutingTests.Pivot_floor_comes_from_the_shared_table` pins by source text (a string search
of two files) that `DenseLuSolver.cs` and `KronReduction.cs` call `NumericTolerances.PivotFloor(aNorm)`; the other
sites are not covered by it.

| Symbol | Value | Source | Decides |
|---|---|---|---|
| `RelativePivotFloor` | `1e-12` x max\|A_ij\| | `Numerics/NumericTolerances.cs` | A pivot at or below it is singular: sparse LU, dense LU, dense complex LU (honest and clamping), Householder QR, ILGPU dense and batched LU, ROCm LU (on `U(k,k)` after getrf), the estimator LU and Kalman inverse, the observability rank test (`ObservabilityAnalysis.RankFloor`), `KronReduction`, the eigenvector-matrix inverse in `GeneralEigen` |
| `PivotFloor(scale)` | `scale * 1e-12`; throws for a negative or `NaN` scale | `NumericTolerances.cs` | The helper every site calls |
| `Ilu0PivotGuard(row)` | `1e-12` x max\|A(i,:)\| (1.0 as scale for a zero row) | `NumericTolerances.cs` | ILU(0) pivot regularisation |
| `UnderflowGuard` | `1e-300` | `NumericTolerances.cs` | Krylov breakdown scalar; Jacobi "nothing to rotate" skip. Not a scale tolerance |
| `DefaultKrylovTolerance` | `1e-10` relative residual | `NumericTolerances.cs` | Real BiCGSTAB (CPU, ILGPU, ROCm) |
| `DefaultComplexKrylovTolerance` | `1e-12` relative residual | `NumericTolerances.cs` | `SparseComplexBiCgStabSolver` |
| `DefaultKrylovMaxIterations(n)` | `4n + 200` | `NumericTolerances.cs` | Every BiCGSTAB iteration cap |
| Newton fallback BiCGSTAB | `min(1e-10, Tolerance * 1e-3)`, cap `4m + 200` | `NewtonShell.SolveStep` | The ILU(0)/BiCGSTAB step |
| `MachineEpsilon` | `2.220446049250313e-16` | `NumericTolerances.cs` | The gap between 1.0 and the next double (not `double.Epsilon`) |
| `JacobiRelativeOffTolerance` | `MachineEpsilon` | `NumericTolerances.cs` | Jacobi stops at off-diagonal mass `<= (eps * \|\|A\|\|_F)^2` |
| `JacobiMaxSweeps` | 100 | `NumericTolerances.cs` | More sweeps than this throws |
| `EigenClusterRelativeTolerance` | `1e-6` | `NumericTolerances.cs` | Two eigenvalues are copies of one |
| `EigenParallelTolerance` | `1e-6` | `NumericTolerances.cs` | Two eigenvectors are the same direction |
| `EigenResidualRelativeTolerance` | `1e-6` x matrix scale | `NumericTolerances.cs` | A deflated vector meeting it is genuine; missing it means defective |
| `GeneralEigen.DefaultInverseIterations` | 40 | `Numerics/GeneralEigen.cs` | Inverse-iteration sweeps per eigenvector |
| `GeneralEigen.InverseIterationStopTolerance` | `1e-14` | `GeneralEigen.cs` | Early stop of inverse iteration |
| `GeneralEigen.ShiftOffsetRelative`, `ShiftImaginarySkew` | `1e-10`, `0.37` | `GeneralEigen.cs` | Shift off the eigenvalue, skewed into the complex plane |
| `GeneralEigen.DefaultBiorthonormalityTolerance`, `BiorthonormalitySlack` | `1e-9`, `1e-6` | `GeneralEigen.cs` | `\|psi^H phi - 1\|` bound is the larger of the two |
| `GeneralEigen.QrMaxIterationsPerEigenvalue` | 60 | `GeneralEigen.cs` | Francis QR iterations on one eigenvalue under the original EISPACK shift schedule; past it, an exceptional shift every 10 iterations |
| `GeneralEigen.QrHardCapIterations(n)` | `30 * max(10, n)` | `GeneralEigen.cs` | Francis QR hard cap per eigenvalue (LAPACK dlahqr ITMAX), else InvalidOperationException |
| `GeneralEigen.ParallelInverseIterationMinOrder` | 32 | `GeneralEigen.cs` | Order at which inverse iterations run in parallel |
| `DenseComplexLuSolver.FactorBlockColumns` | 16 | `Numerics/ComplexLinearSolvers.cs` | Columns updated together (speed only) |
| `BoundedSimplex.DefaultTolerance` | `1e-9` | `Numerics/BoundedSimplex.cs` | Reduced-cost and feasibility tolerance |
| `BoundedSimplex.DefaultMaxIterations` | 20000 | `BoundedSimplex.cs` | Simplex iteration cap |
| `BoundedSimplex.PhaseOneFeasibilityTolerance` | `1e-7` absolute | `BoundedSimplex.cs` | Phase I ends feasible |
| `BoundedSimplex.RefactorInterval` | 64 | `BoundedSimplex.cs` | Eta updates between refactorisations |
| `BoundedSimplex.RatioTieMargin`, `EtaPivotFloor` | `1e-12`, `1e-12` absolute | `BoundedSimplex.cs` | Ratio-test tie; smallest accepted pivot |
| `BoundedSimplex.RefactorPivotClamp` | `1e-14` absolute | `BoundedSimplex.cs` | Clamp of a tiny pivot during refactorisation |
| `HardConstraintWeightScale` | `1e4` x max(w_free) | `NumericTolerances.cs` | Constraint pseudo-weight of an equality row (gate and covariance) |
| `CriticalResidualVarianceFraction` | `1e-8` | `NumericTolerances.cs` | A measurement with `Omega_ii <= 1e-8 * sigma_i^2` is critical (`BadDataAnalysis.CriticalFloor`) |
| `PowerFlowOptions.Tolerance` | `1e-8` pu | `PowerFlow/PowerFlowOptions.cs` | Largest mismatch at which Newton stops |
| `PowerFlowOptions.MaxIterations` | 30 | `PowerFlowOptions.cs` | Newton iteration cap |
| `PowerFlowOptions.MaxQLimitPasses` | 12 | `PowerFlowOptions.cs` | Q-limit outer-loop passes |
| `NewtonShell.MaxUndampedStep` | `0.4` (rad or pu) | `PowerFlow/NewtonShell.cs` | Largest component applied undamped; larger steps are scaled so the largest equals it |
| `NewtonShell.VmFloorPu` | `0.2` pu | `NewtonShell.cs` | Floor on a PQ voltage magnitude after an update |
| `NewtonShell.QLimitTolMva`, `RevertVoltTolPu` | `1e-3` MVA, `1e-4` pu | `NewtonShell.cs` | Q-limit comparison slack; PQ to PV revert deadband |
| `DistributedSlackPowerFlow.ToleranceMw`, `DefaultMaxPasses` | `1e-4` MW, 50 | `PowerFlow/DistributedSlackPowerFlow.cs` | Slack allocation stop and cap |
| `SteadyStateSolver.TapChangeEpsilon`, `DefaultMaxOltcPasses` | `1e-12` pu ratio, 25 | `PowerFlow/SteadyStateSolver.cs` | OLTC settled; OLTC pass cap |
| `SwitchedShuntPowerFlow.DefaultMaxPasses` | 50 | `PowerFlow/SwitchedShuntPowerFlow.cs` | Switched-shunt pass cap |
| `BackwardForwardSweep.MaxCompensatedLoops` | 6 | `PowerFlow/BackwardForwardSweep.cs` | Loops the sweep compensates before it declines |
| `BackwardForwardSweep.SafetyFloor`, `MinControlTolPu` | `1e-6`, `1e-6` pu | `BackwardForwardSweep.cs` | Floor on `\|V\|` in a division and on the path reactance in the PV dV/dQ sensitivity; floor on the PV and loop control tolerance |
| `BackwardForwardSweep.LoopPivotFloor` | `1e-14` pu, absolute by design | `BackwardForwardSweep.cs` | Pivot of the breakpoint-impedance solve |
| `SolverSelector.DefaultSparseThreshold` | 0 buses | `PowerFlow/SolverSelector.cs` | Newton routing: every CPU solve is sparse |
| `SolverSelector.DenseSparseCrossoverBuses` | 200 buses | `SolverSelector.cs` | GPU routing, the estimator's dense or sparse step, the solve export |
| `EstimationOptions.Tolerance` | `1e-8` (rad or pu) | `Estimation/WlsStateEstimator.cs` | Gauss-Newton stops on the infinity norm of `dx` |
| `EstimationOptions.MaxIterations` | 30 | `WlsStateEstimator.cs` | Gauss-Newton cap |
| `EstimationOptions.MaxAngleStepRad`, `MaxVmStepPu` | `0.2` rad, `0.1` pu | `WlsStateEstimator.cs` | Trust caps on one step |
| `EstimationOptions.NormalizedResidualThreshold` | 3.0 | `WlsStateEstimator.cs` | A measurement is removable above this normalised residual |
| `EstimationOptions.SparseBusThreshold` | 200 buses | `WlsStateEstimator.cs` | Above it the estimator uses the sparse step |
| `WlsStateEstimator.IrlsMaxOuterIterations` | 10 | `WlsStateEstimator.cs` | Outer IRLS passes of the robust methods |
| `WlsStateEstimator.LavResidualFloor`, `IrlsWeightTolerance` | `1e-3`, `1e-3` | `WlsStateEstimator.cs` | Floor on `\|r\|` in the LAV weight; IRLS stop |
| `WlsStateEstimator.BackfillSigmaMultiplier`, `BackfillRelativeSigmaFloor` | 3.0, 0.05 | `WlsStateEstimator.cs` | Sigma of a prior-backfilled measurement |
| `FeasibleWlsStep.ThermalHysteresisFraction`, `AngleHysteresisFraction`, `GeneratorQHysteresisFraction` | `1e-3` each | `Estimation/FeasibleWlsStep.cs` | Active-set hysteresis |
| `FeasibleWlsStep.GeneratorQBandFloorPu` | `1e-6` pu | `FeasibleWlsStep.cs` | Narrowest generator-Q band |
| `FeasibilityConstraints.VmFloor`, `AuditTolMva` | `1e-6` pu, `1e-4` MVA | `Estimation/FeasibilityConstraints.cs` | Voltage floor; acceptance-gate audit tolerance |
| `MeasurementModel.ZeroFlowMagnitudePu`, `ZeroFlowMagnitudeSquaredPu` | `1e-12`, `1e-24` | `Estimation/MeasurementModel.cs` | A branch with `\|S\|` at or below it carries no flow |
| `ProjectionStatistics.DegenerateScale`, `MadNormalConsistency` | `1e-15`, `1.4826` | `Estimation/ProjectionStatistics.cs` | Degenerate direction or MAD; MAD to sigma |
| `MeasurementSet.BaseMvaMatchToleranceMva` | `1e-9` MVA | `Estimation/MeasurementSet.cs` | Measurement base must match the model's |
| `NoiseFloorAnalysis.DefaultMiscalibrationTolerance` | 0.25 | `Estimation/NoiseFloorAnalysis.cs` | Miscalibration flag |
| `DenseEstimationLimits.DenseMethodMaxBuses` | 3000 buses | `Estimation/DenseEstimationLimits.cs` | Cap of the constrained, feasible and QR methods |
| `DenseEstimationLimits.RankProofMaxState` | 2500 states | `DenseEstimationLimits.cs` | Largest state for the dense rank proof; also the Kalman filter and innovation filter caps |
| `DenseEstimationLimits.StateCovarianceMaxState` | 2000 states | `DenseEstimationLimits.cs` | Largest state for `diag(G^-1)` |
| `GpuContext.Fp64SmokeTolerance` | `1e-12` | `src/GridSim.Gpu/GpuContext.cs` | kept for compatibility; the OpenCL FP64 smoke test is now an exact comparison |
| `ReduceGroupSize` | 256 (dense, sparse), 128 (batched) | `GpuDenseLinearSolver.cs`, `GpuSparseLinearSolver.cs`, `GpuBatchedDenseSolver.cs` | Threads per reduction block |

## Invariants

**Mechanisms.** Bit-identity is delivered by six mechanisms, each with the tests that pin it.

1. **One canonical Newton step.** Both Newton engines solve every step with `CpuSparseLuSolver` through
   `NewtonShell.SolveStep` and apply it with `NewtonShell.ApplyStep`; only the assembly differs, and assembly is exact
   (same products and sums in the same order). `ComplexCsr.Builder`'s stable keyed sort, its `0.0 + g` start and the
   single branch stamp `AdmittanceStamp.Branch` make the sparse Y-bus the dense Y-bus bit for bit.
2. **Reuse is a pure function of structure.** A cached ordering, a prepared CSC matrix, a refilled Jacobian, a
   reused `SolveInto` buffer and a factor-once back substitution all give the bits of the recompute from scratch.
3. **Bit-exact kernels.** The dense LUs, `FactorDense` and the block evaluator use only kernels whose every lane is the
   scalar statement's operations (a separate multiply and subtract, no fused multiply-add). The back substitution of
   the `BadDataAnalysis` LU calls `Dot`, whose defined order is the same at every tier. Trigonometry and the other
   elementary functions go through `DetMath` (fdlibm), never the platform libm.
4. **Order is part of the algorithm.** Pivot ties go to the first maximum; the DFS emits a fixed topological order;
   bus and equation numbering are fixed (`NewtonShell.EquationPositions`); parallel loops write disjoint slices
   (`CpuBatchedLuSolver`, `InverseIterationWorkers`) or reduce in a fixed order.
5. **Fail loud.** A `NaN` input, a pivot at or below the floor, an unreadable device status or a defective eigenproblem
   throws or returns `null`; the caller never receives a number computed from a singular matrix.
6. **Explicit-tier test seams.** Every tier-dispatched kernel has an overload taking a `SimdTier`, so one machine runs
   all tiers.

| Invariant | Test |
|---|---|
| The sparse LU solves random sparse systems | `SparseLuTests.Solves_random_sparse_systems` |
| `SolveInto` equals `Solve`, and a cached ordering equals a fresh one, bit for bit | `SparseSolveIntoBitIdentityTests.SolveInto_is_bit_identical_to_Solve_and_cached_ordering_to_a_fresh_one` |
| The sparse and dense LU reject the same near-singular matrix, at any scale; the sparse LU fails loud on `NaN` | `NumericTolerancesTests.Sparse_and_dense_LU_reject_the_same_near_singular_matrix`, `Sparse_LU_returns_null_for_a_matrix_singular_to_working_precision_at_any_scale`, `Sparse_LU_fails_loud_on_a_NaN_entry` |
| The pivot floor is `1e-12` relative; Krylov defaults are the named values | `NumericTolerancesTests.The_shared_pivot_floor_is_relative_1e_minus_12`, `Krylov_defaults_are_the_named_values` |
| The dense LU and the Kron reduction call `PivotFloor` (source text); the Newton fallback Krylov cap comes from the shared table | `SharedToleranceRoutingTests.Pivot_floor_comes_from_the_shared_table`, `Newton_fallback_krylov_cap_comes_from_the_shared_table` |
| `ComplexCsr.Builder` equals a reference build for shuffled and duplicated stamps in short rows | `ComplexCsrBuilderPinTests.Build_is_byte_identical_to_a_reference_for_shuffled_and_duplicate_entries` |
| The refilled Jacobian equals the triplet build | `SparseJacobianRefillPinTests.Refill_matches_the_old_triplet_build_bit_for_bit` |
| The dense and sparse Newton engines are bit-identical on the bundled cases in the test lists | `DenseSparseParityTests.Built_in_case_is_bit_identical_dense_vs_sparse`, `Matpower_file_is_bit_identical_dense_vs_sparse`, `Json_case_is_bit_identical_dense_vs_sparse`, `Repeated_solves_on_one_sparse_instance_stay_bit_identical` |
| A stub backend that always returns `null` falls back to the exact CPU LU bit for bit; the sparse engine equals the dense engine on case118 with Q limits | `GpuFallbackTests.A_backend_that_always_gives_up_falls_back_to_the_exact_cpu_lu_bit_for_bit`, `The_cpu_path_is_unchanged_and_never_double_solves` |
| The ROCm post-factorisation check rejects a near-singular 3 x 3 matrix the CPU LU rejects, given a host-side imitation of `getrf`; the ILGPU dense LU throws on a near-singular pivot (with a device) | `NumericTolerancesTests.ROCm_pivot_check_rejects_exactly_what_the_CPU_LU_rejects`, `Gpu_dense_lu_throws_on_a_near_singular_pivot_not_just_an_exact_zero` (`GpuSolverSeamTests`) |
| `SubtractScaledRow` and `AddScaledRow` without FMA are bit-exact at every tier | `SimdTierSeamTests.SubtractScaledRow_is_bit_exact_vs_scalar_at_every_tier`, `AddScaledRow_without_fma_is_bit_exact_vs_scalar_at_every_tier` |
| `Dot` is bit-identical at every tier and equals the defined order; batch `SinCos` is bit-identical at every tier and to `DetMath.SinCos` for every element; the dense WLS gain and the classical swing trajectory are bit-identical at every tier | `D1CrossTierTests.Dot_is_bit_identical_at_every_tier_and_equals_the_defined_order`, `Batch_SinCos_is_bit_identical_at_every_tier_and_to_DetMath_for_every_element`, `Dense_WLS_gain_is_bit_identical_at_every_tier_and_has_no_fused_multiply_add`, `Classical_swing_trajectory_is_bit_identical_at_every_tier`; `SimdTierSeamTests.Dot_agrees_with_the_scalar_sum_at_every_tier` and `SimdKernelsTests.Vectorised_SinCos_matches_Math_SinCos_to_1e12` check closeness to the left-to-right sum and to the platform `Math.SinCos` |
| `DetMath` matches Java `StrictMath` bit for bit; no numerical method calls platform-dependent mathematics or the fused arm; a frequency result stores NaN as the canonical NaN | `FdlibmStrictMathVectorTests.Explicit_vectors_match_Java_StrictMath_bit_for_bit`, `Pseudo_random_digests_match_Java_StrictMath_bit_for_bit`; `DeterministicMathGuardTests.GridSim_Core_Gda_Gpu_and_Cli_call_no_platform_dependent_maths_outside_the_deterministic_module`, `No_numerical_path_selects_the_fused_multiply_add_arm`; `CanonicalNaNTests.Every_nan_becomes_the_one_canonical_nan_and_everything_else_is_untouched` |
| The production overloads are exactly the active tier, and the active tier is what the hardware flags say | `SimdTierSeamTests.The_production_overloads_are_exactly_the_active_tier`, `The_active_tier_is_what_the_hardware_flags_say` |
| The dense complex factor equals the previous loop at every tier and block width, including the clamp | `N186DenseComplexFactorBitIdentityTests.Every_order_from_0_to_40_matches_the_previous_factor_at_every_block_width`, `Singular_and_degenerate_matrices_match_including_the_clamp` |
| A factor-once back substitution equals one-shot solves | `FactorOnceInverseIterationTests.Factor_once_back_substitution_is_bit_identical_to_one_shot_solves` |
| The real-expanded complex solver's `SolveInto` equals `Solve`; the ordering cache equals a fresh solver | `ComplexSolveIntoBitIdentityTests.Real_expanded_SolveInto_is_bit_identical_to_Solve_over_many_right_hand_sides`, `ComplexSolverReusePinTests.Cached_ordering_across_same_pattern_orders_matches_a_fresh_solver_bit_for_bit` |
| All three complex solvers return `null` on a singular system; `SolveDense` keeps its clamp | `ComplexSolverSingularContractTests.All_three_solvers_return_null_on_a_singular_system`, `SolveDense_keeps_its_historical_clamping_and_never_returns_null` |
| The complex BiCGSTAB solves the system whose bilinear `rho` is zero | `NumericTolerancesTests.Complex_BiCgStab_solves_the_system_whose_bilinear_rho_is_zero` |
| Householder QR in column-major layout equals the original routine; a rank-deficient column is rejected | `HouseholderQrLayoutBitIdentityTests.Column_major_qr_is_bit_identical_to_the_original_routine`, `NumericTolerancesTests.Householder_QR_rejects_a_column_dependent_to_rounding` |
| The blocked inverse quadratic form equals the full solve per lane at every tier | `ResidualCovarianceBitIdentityTests.Blocked_inverse_quadratic_form_matches_the_full_solve_per_lane_at_every_simd_tier`, `Inverse_quadratic_form_with_non_finite_factors_reproduces_the_reference_exactly` |
| `CsrMatrix.Builder` sums duplicate cells in insertion order in rows of 17 to 80 entries and in a three-entry row | `Doc6CsrStableOrderTests.D026_duplicate_cells_are_summed_in_insertion_order_in_long_rows`, `D026_a_three_entry_row_keeps_insertion_order` |
| The batched in-place LU equals the dense solver for every system | `BatchedInPlaceLuBitIdentityTests.Every_system_equals_the_dense_solver_bit_for_bit` |
| Eigenpairs and participation do not depend on the worker count; repeated eigenvalues decompose; a defective cluster throws | `N186ParticipationBitIdentityTests.One_worker_and_many_workers_give_the_same_bits`, `NumericTolerancesTests.A_repeated_non_defective_eigenvalue_decomposes_instead_of_throwing`, `A_defective_block_inside_a_larger_cluster_still_fails_loud` |
| Jacobi converges at any scale and fails loud on a non-finite matrix | `NumericTolerancesTests.Jacobi_rotates_a_tiny_scale_matrix_instead_of_returning_its_diagonal`, `Jacobi_fails_loud_on_a_non_finite_matrix` |
| The shared constants equal the literals they replaced | `Ws13CoreConstantsAntiDriftTests.Numerics_constants_equal_the_literals_they_replaced`, `Estimation_constants_equal_the_literals_they_replaced`, `Physics_and_power_flow_constants_equal_the_literals_they_replaced` |

Three test caveats apply. `ComplexCsrBuilderPinTests` compares against a stable reference on short rows and cannot
tell a stable order from an unstable one, because its own header notes that tie order does not change a pair's sum;
the end-to-end pin is `DenseSparseParityTests`. The lists in `DenseSparseParityTests` omit `case9241pegase` (excluded
on memory grounds in the test header) and the two GB MATPOWER files `GBnetwork.m` and `GBreducednetwork.m`; the
`gb-synthetic` and `gb-full` JSON networks are included. The second `GpuFallbackTests` test counts no calls: it compares
the two engines' results on case118, and its name's "never double solves" is not checked by a call count.

The GPU tests in `GpuSolverSeamTests` and `RocmSolverSeamTests` compare each device solver with the CPU solver on a
machine that has the device and are the evidence that a GPU backend is correct there; they are not bit-identity
tests, because a device reduction and a device elimination order differ from the CPU's. `GpuSolverSeamTests` has no
availability gate: with no device `GpuDenseLinearSolver` runs `DenseLuSolver`, so on such a machine its tests check
the CPU LU. `GpuDeviceKernelTests` has the gate: its CUDA and OpenCL tests (batched dense LU, singular-system isolation,
dense LU, near-singular refusal, sparse solve) run only where `GpuContext.IsAvailableFor` finds that accelerator and are
reported skipped with the reason elsewhere.

## Worked example

The capture below is produced by a scratch program (`docs/_captures/numerics/numdemo-source.txt`) that runs the dense
Newton engine on `case9` with a `SolveTrace`, takes the Jacobian of pass 1 iteration 1, holds it in the structural
pattern the engines use (`SparseNewtonRaphsonPowerFlow.BuildJacobianStructure`), and factors it with the same calls
`CpuSparseLuSolver` makes. Timings and stamps in other captures differ by machine and run; every number here is
deterministic except the SIMD line. The capture was taken on an earlier build: the `CsrMatrix.Builder` column and the
`Dot` line near the end show the builder sort and the dot kernel as they were before the changes noted under those
blocks.

```
$ scratch numdemo (calls the library directly; source in numdemo-source.txt)
case9 pass 1 iteration 1: Jacobian order 14, structural nnz 82 of 196 (41.8 %), numerically non-zero 78
equation rows: P of buses [1,2,3,4,5,6,7,8], Q of buses [3,4,5,6,7,8] (0-based bus index)
max |J_ij| = 40.17033317056342  pivot floor = 4.0170333170563415E-11
RCM ordering q (pivot step k -> original column): 9 3 1 8 2 10 4 13 11 7 5 12 6 0
pivot row p (pivot step k -> original row):   9 3 4 8 2 10 1 13 11 7 5 12 6 0
nnz(L) incl. unit diagonal = 58, nnz(U) incl. pivots = 63, nnz(A) = 82, fill = 25
```

The Jacobian is 14 x 14: P equations for the eight non-slack buses and Q equations for the six PQ buses. The stored
pattern has 82 cells; four of them are numerically zero at the start point, and the RCM ordering is computed on the
82-cell pattern, so it does not move when those cells fill in. The factorisation reads the 78 non-zero cells (the CSR
to CSC conversion drops explicit zeros). The pivot floor is `1e-12` times the largest entry, 40.17. The factors hold
58 entries in `L` (14 of them the unit diagonal) and 63 in `U` (14 of them the pivots), so 107 distinct entries after
the unit diagonal is discounted: 29 more than the 78 non-zeros of `J`. The `fill = 25` line of the capture subtracts the
82 stored cells instead.

```
k  col q[k]  pivot row  U(k,k)
 0      9          9  1.558293E+001
 1      3          3  1.676603E+001
 2      1          4  -1.749147E+001
 3      8          8  3.136122E+001
 4      2          2  3.345696E+001
 5     10         10  2.899530E+001
 6      4          1  1.315340E+001
 7     13         13  1.275242E+001
 8     11         11  1.982959E+001
 9      7          7  1.368418E+001
10      5          5  1.627056E+001
11     12         12  2.228041E+001
12      6          6  2.104733E+001
13      0          0  3.621179E+000
```

Read a row as: at pivot step `k` the factorisation processes original column `q[k]`, and partial pivoting picks
original row `pivot row`. In 12 of the 14 steps the pivot row is the column's own row (the diagonal is the largest
candidate). At step 2 the column of unknown 1 takes row 4 (`|-17.49|` beats the remaining candidates), and at step 6
the column of unknown 4 takes row 1: a swap of rows 1 and 4 chosen by magnitude, not by the diagonal. Every `|U(k,k)|`
is far above the floor 4.0e-11, so the factorisation succeeds.

```
sparse solve vs the iteration's recorded correction: max |diff| = 0.000E+000
sparse solve vs dense LU solve: max |dx| = 1.726317E-001, max |diff| = 2.776E-017, bit-identical components = 0 of 14
max |J dx - f| (sparse solution) = 4.267E-016
converged: True, iterations: 4
```

The sparse solve reproduces the Newton correction recorded by the engine exactly (difference 0): the engine ran this
very factorisation. The dense LU solves the same system to within `2.8e-17` of the largest correction `0.17`, but
none of the 14 components has the same bits, because the two eliminations round in a different order; that is why the
dense Newton engine does not use its own LU on the CPU and takes the sparse step instead. The residual of the sparse
solution is `4.3e-16`.

The RCM ordering of the `case14` Jacobian shows what the ordering buys:

```
case14 pass 1 iteration 1: Jacobian order 22, structural nnz 146, numerically non-zero 146
RCM ordering q: 19 10 20 11 18 9 4 21 17 12 8 14 3 0 1 16 13 7 2 15 5 6
half-bandwidth natural = 16, RCM = 10
nnz(L)+nnz(U)-n natural = 348, RCM = 194
```

The half-bandwidth is the largest `|pos(i) - pos(j)|` over the stored cells `(i, j)`, where `pos` is the position in
the ordering applied to rows and columns alike. It measures how compact the ordering is; it is not the profile of the
LU factors, which permute columns by `q` and rows by pivoting. The natural ordering has half-bandwidth 16 and produces 348 stored factor entries (counting `U`'s pivots, not `L`'s
unit diagonal); the RCM ordering has half-bandwidth 10 and produces 194.

On the build the capture was taken from, the builders differed on a row with 17 or more entries. The program searches
random rows of 20 stamps over 5 columns for the first where the real `CsrMatrix.Builder` does not reproduce the
insertion-order sum; seed 1 was the first. The columns print the IEEE-754 bits of each cell:

```
seed 1: row 0 with 20 stamps over 5 columns; real CsrMatrix.Builder differs from insertion-order sum
col  insertion-order sum (bits)    ComplexCsr.Builder (bits)     CsrMatrix.Builder (bits)
  0  40E7F1555DFE539C  40E7F1555DFE539C  40E7F1555DFE539C
  1  C17DCB3FFC3AAF80  C17DCB3FFC3AAF80  C17DCB3FFC3AAF81
  2  C175AA3536EE463C  C175AA3536EE463C  C175AA3536EE463C
  3  4173369A0B676FF3  4173369A0B676FF3  4173369A0B676FF3
  4  BFDBFFF20DF7FFE4  BFDBFFF20DF7FFE4  BFDBFFF20DF7FFE4
```

`ComplexCsr.Builder` equals the insertion-order sum in every cell; that build's `CsrMatrix.Builder` differed in the
last bit of column 1, because it sorted each row by column alone with an unstable sort. `CsrMatrix.Builder` now sorts
by the unique key `(column << 32) | insertionIndex` (see the builders above), so on the current build it equals the
insertion-order sum in every cell, as `ComplexCsr.Builder` does; the capture has not been retaken. Finally, the vector
dot against the scalar reference, on the capture machine and that earlier build:

```
SIMD: Avx256 (4 doubles/step)  FMA=yes  [Avx512F=False Avx2=True Fma=True Sse2=True AdvSimd=False]
Dot      (1000 terms, seed 12345) = -0.7310824929902175  bits BFE765071CC246BA
DotExact (1000 terms, seed 12345) = -0.7310824929902172  bits BFE765071CC246B7
```

`Dot` and `DotExact` differ in the last three bits of the same 1,000-term sum. On that build `Dot` used four lanes
with fused multiply-adds, so a machine at another tier or without FMA gave it a different value. The current `Dot`
sums in the defined four-partial order with no fused multiply-add, which still differs from the left-to-right
`DotExact` order but gives the same bits at every tier and on every CPU; its value on the same input has not been
re-captured.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Numerics/Sparse.cs` | `CsrMatrix`, `CsrMatrix.Builder`, `ComplexCsr`, `ComplexCsr.Builder`, `Ilu0`, `BiCgStab` | CSR storage and builders, ILU(0), preconditioned BiCGSTAB |
| `src/GridSim.Core/Numerics/SparseLu.cs` | `SparseLu` | Entry points (`Factor`, `Ordering`, `FactorWithOrdering`, `FactorPrepared`) and `ReverseCuthillMcKee` |
| `src/GridSim.Core/Numerics/SparseLuFactorization.cs` | `SparseLuFactorization`, `InverseQuadraticForm` | Gilbert-Peierls factors, `Solve`, `SolveInto`, inverse quadratic forms |
| `src/GridSim.Core/Numerics/NumericTolerances.cs` | `NumericTolerances` | The shared tolerance table |
| `src/GridSim.Core/Numerics/ComplexLinearSolvers.cs` | `DenseComplexLuSolver`, `DenseComplexLu`, `RealExpandedComplexSolver`, `SparseComplexBiCgStabSolver` | Complex direct and iterative solvers |
| `src/GridSim.Core/Numerics/HouseholderQr.cs` | `HouseholderQr` | Least squares by orthogonal triangularisation |
| `src/GridSim.Core/Numerics/GeneralEigen.cs` | `GeneralEigen` | Non-symmetric eigenpairs with biorthonormal vectors |
| `src/GridSim.Core/Numerics/SymmetricEigen.cs` | `SymmetricEigen` | Cyclic Jacobi |
| `src/GridSim.Core/Numerics/SimdKernels.cs`, `SimdCapabilities.cs` | `SimdKernels`, `SimdCapabilities`, `SimdTier` | Vector kernels and tier selection |
| `src/GridSim.Core/Numerics/BoundedSimplex.cs` | `BoundedSimplex` | Bounded-variable simplex used by the DC OPF family |
| `src/GridSim.Core/PowerFlow/DenseLuSolver.cs`, `DenseLuKernels.cs` | `DenseLuSolver`, `DenseLuKernels` | Dense real LU |
| `src/GridSim.Core/PowerFlow/CpuSparseLuSolver.cs` | `CpuSparseLuSolver` | Sparse LU with an ordering cache |
| `src/GridSim.Core/PowerFlow/CpuBatchedLuSolver.cs` | `CpuBatchedLuSolver` | Reference batched LU |
| `src/GridSim.Core/PowerFlow/NewtonShell.cs` | `NewtonShell` | Canonical Newton step, fallback chain, damped update |
| `src/GridSim.Core/Estimation/WlsStep.cs` | `DenseWlsStep`, `SparseWlsStep`, `QrWlsStep` | Estimator gain factorisation backends |
| `src/GridSim.Core/Abstractions/` | `ILinearSolver`, `ISparseLinearSolver`, `IBatchedLinearSolver`, `IComplexLinearSolver` | The solver seams |
| `src/GridSim.Gpu/GpuContext.cs` | `GpuContext` | ILGPU context, CUDA and OpenCL accelerators, FP64 smoke test, lock |
| `src/GridSim.Gpu/GpuDenseLinearSolver.cs` | `GpuDenseLinearSolver` | ILGPU dense LU |
| `src/GridSim.Gpu/GpuSparseLinearSolver.cs` | `GpuSparseLinearSolver` | ILGPU Jacobi BiCGSTAB |
| `src/GridSim.Gpu/GpuBatchedDenseSolver.cs` | `GpuBatchedDenseSolver` | ILGPU batched dense LU |
| `src/GridSim.Gpu/RocmContext.cs`, `RocmInterop.cs` | `RocmContext`, `RocmInterop` | HIP probe and the native P/Invoke surface |
| `src/GridSim.Gpu/RocmDenseLinearSolver.cs` | `RocmDenseLinearSolver` | hipSOLVER LU with the near-singular check |
| `src/GridSim.Gpu/RocmSparseLinearSolver.cs` | `RocmSparseLinearSolver` | rocSPARSE and rocBLAS BiCGSTAB |
| `src/GridSim.Cli/SolverBackend.cs` | `SolverBackend` | Backend selection and routing |
| `docs/_captures/numerics/` | captures | `numerics-demo.txt` and the scratch program that produced it |

## Limits

- The dense estimator steps (`DenseWlsStep` with a non-CPU linear solver, and the constrained, feasible and QR methods)
  and the sparse step do not round in the same order, so a dense-only method's estimate is not the bits a sparse
  solve would give. A plain WLS or robust estimate on the CPU always takes the sparse step, so it does not depend on
  the bus count (`CanonicalWlsParityTests.The_estimate_is_bit_identical_across_the_dense_sparse_threshold`).
- GPU results are outside the bit-identity contract: device reductions, the device elimination order differ from the
  CPU's, and the device pivot tie-break differs from the CPU's first-maximum rule once more than 256 (dense) or 128
  (batched) candidate rows remain.
- The ROCm solvers are covered by hardware-free seam and signature tests (the near-singular check by one 3 x 3
  host-side imitation of `getrf`); no recorded run on an AMD Instinct device
  exists in `docs/developer/benchmarks/server-results/mi300/`.
- ROCm has no batched solver; the ILGPU batched solver has no memory chunking and no AC N-1 batching across Newton
  iterations that diverge in count.
- The sparse LU has no threshold pivoting and no reuse of a symbolic factorisation across numeric factorisations; the
  reach runs on every factor.
- The dense estimator methods (constrained, feasible, QR) refuse models above 3000 buses, and the dense rank proof,
  state covariance and Kalman filters cap at 2500, 2000 and 2500 states.
- `GeneralEigen` and `SymmetricEigen` are dense and unblocked, intended for tens of states; the largest decomposed
  matrix in the repository is order 1,902.

## See also

- [ch. 3, Power flow](<0003 Power flow.md>) - the Newton loops and engine selection these solvers serve.
- [ch. 6, State estimation](<0006 State estimation.md>) - the gain factorisations, bad-data statistics and dense limits.
- [ch. 30, Small-signal and impedance stability](<0030 Small-signal and impedance stability.md>) - modal analysis on `GeneralEigen`.
- [ch. 1, Architecture](<0001 Architecture.md>) - the seams the backends plug into.
- [ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>) - what the matrices represent.
- [user ch. 28, Benchmarking and GPU](<../manual/0028 Benchmarking and GPU.md>) - measured backend timings and driver requirements.
- [user ch. 27, Validation tools](<../manual/0027 Validation tools.md>) - `bench` and `crossval`.
- [CLI reference](<../manual/0019 CLI reference.md#solver-selection>) - the `--cpu` and `--gpu` switches.
