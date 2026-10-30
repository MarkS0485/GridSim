# 28. Benchmarking and GPU

GridSim measures its own speed with four verbs and runs its linear solves on a graphics card when asked. `bench` times
every solver on every case and writes a report; with `--perf` it times a fixed set of single-threaded paths, and with
`--estimate` it times the state estimator. `batchsolve` times the batched dense solver. `gpucheck` and `simdcheck` are
preflights that say which backend and which vector width a machine will use. `--cpu` and `--gpu` are switches, not verbs:
they choose the backend for any verb that solves a power flow. `cycle`, `nminus1-sweep` and `stress` double as soak and
scale drivers (see [Soak and scale drivers](#soak-and-scale-drivers)). You need this chapter to compare two machines, to
prove a build has not slowed down, to decide whether a GPU is worth renting, and to rent one without wasting the meter.
The numbers GridSim must reproduce bit for bit are validated by the verbs in
[ch. 27, Validation tools](<0027 Validation tools.md>); this chapter is the performance side.

## Before you start

- Every command here runs on the Release build. The header's `build` field records the configuration; timings from a
  Debug build are not comparable with Release timings.
- Reports go under `results/` in the current directory. `bench` and `bench --perf` create the directory.
- `bench` reads its cases from `data/matpower/` next to the `gridsim` binary, not from the current directory, and
  adds nine built-in classics. A self-contained publish carries the folder it was built with.
- A GPU is optional. Every verb works on a machine without one. `--gpu` (auto-detect) falls back to the CPU with a
  warning; a named vendor such as `--gpu=cuda` with no usable device exits 2 (except on `bench` and `gpucheck`, which
  report the absent vendor).
- Timings differ by machine and by run. The captures below are kept exactly as recorded on an 8-core Windows laptop
  and show the shape of each report, not figures to match.

## Worked example

Time every solver on every case, one run per case, two cases at a time:

```bash
gridsim bench --runs 1 --parallel 2 --out results/bench-quick.md
```

The command prints the report as it writes it, and a progress line per case on the error stream, so the two
interleave. The start of the report is:

```
$ gridsim bench --runs 1 --parallel 2 --out results/bench-quick.md
    GridSim  -  2026-10-04 10:10:54 UTC
    code:    git 3f9d9be3  |  GridSim.Core v1.95.147.0  |  build Release
    runtime: .NET 10.0.12  X64  GC=Server
    machine: LAPTOP  Microsoft Windows 10.0.26200
    cpu:     X64  -  8 logical cores  -  8 GB RAM
    simd:    Avx256  (4 doubles/step)  FMA=True
    backend: cpu
    run:     bench-matrix  cases=27  engines=3  runs=1  parallel=2

Full solver x backend matrix over the whole archive. Every **engine** below is swept across
every case (Q-limits off, tol 1e-8, max 100 iters). Cells: `conv / iters / mismatch / mean ms`.
BFS is radial-only (meshed cases show `n/a`). Dense solves are memory-gated (few concurrent)
and ordered small->large; GPU engines run one case at a time and skip cases >3000 buses
(consumer-GPU FP64 is slow). Absent accelerators are simply not listed as engines.

**Engines this run:** `cpu-dense` - Dense Newton-Raphson, CPU LU; `cpu-sparse` - Sparse Newton-Raphson, CPU (direct LU / ILU0-BiCGSTAB); `bfs` - Backward-forward sweep (radial feeders only)

_GPU engines not requested (pass `--gpu` to add ROCm/OpenCL/CUDA when a device is present)._
```

Each engine then gets a section with one row per case, in the order the cases finish. This is the start of the
`cpu-dense` section with five of its rows; each `...` line stands for rows left out of this listing (the capture holds
all 27):

```
## cpu-dense - Dense Newton-Raphson, CPU LU
| Case | Buses | Gens | Branches | conv / iters / mismatch / mean ms |
|------|------:|-----:|---------:|-----------------------------------|
...
| pglib_opf_case3_lmbd | 3 | 3 | 3 | **NO** / 100 / 6.76E+000 / 0ms |
...
| case14 | 14 | 5 | 20 | yes / 2 / 1.32E-010 / 1ms |
...
| case118 | 118 | 54 | 186 | yes / 3 / 1.51E-012 / 8ms |
...
| case300 | 300 | 69 | 411 | yes / 5 / 1.38E-012 / 47ms |
...
| case9241pegase | 9241 | 1445 | 16049 | yes / 6 / 2.13E-009 / 199934ms |
```

The run ends with the comparison table, one row per case and one column per engine, and the exit line:

```
## Comparison - mean solve time (ms), converged unless noted
| Case | Buses | cpu-dense | cpu-sparse | bfs |
|------|------:|------:|------:|------:|
| GBnetwork | 2224 | 2314ms | 170ms | n/a |
...
| case9 (WSCC) | 9 | 1ms | 0ms | n/a |
| case14 | 14 | 1ms | 0ms | n/a |
| cigre_mv | 15 | 1ms | 0ms | 30ms |
...
| pglib_opf_case162_ieee_dtc | 162 | NO | NO | n/a |
| pglib_opf_case240_pserc | 240 | NO | NO | n/a |
| case300 | 300 | 47ms | 8ms | n/a |
| case533mt_hi | 533 | 98ms | 29ms | 4ms |
| pglib_opf_case588_sdet | 588 | 162ms | 15ms | n/a |
| case1354pegase | 1354 | 610ms | 26ms | n/a |
| case2869pegase | 2869 | 5338ms | 198ms | n/a |
| case9241pegase | 9241 | 199934ms | 1178ms | n/a |
[bench] done (3 engines x 27 cases) -> results\bench-quick.md
[exit 0]
```

On the largest meshed case the sparse engine takes 1,178 ms where the dense engine takes 199,934 ms, and both reach
the same iteration count and the same mismatch (6 iterations, `2.13E-009`). That agreement is the signal that the two
engines solve the same problem; on the CPU they agree to the last bit (pinned by `DenseSparseParityTests`; see [Backends](#backends)). The full capture,
with every per-engine section, is `docs/_captures/bench/bench-matrix.txt`.

## Reading the output

**Header.** Eight lines identify the run: the time, the git commit the binary was built from, the core version and
build configuration, the .NET runtime and garbage-collector mode, the machine name and operating system, the CPU
and RAM, the active vector tier (`simd:`), the backend (`cpu`, or the GPU description when `--gpu` is given), and
the run settings. The stamps are not the same on every output:

- `bench` prints the header, writes `<report>.run-metadata.json` beside the report (with the run settings under
  `parameters`), and puts the commit in a `commit` column on every CSV row. The CSV rows carry no machine or runtime.
- `bench --perf` prints the same header and puts the commit in its CSV rows. It writes no `run-metadata.json`.
- `bench --estimate` prints no header. The `commit` column of its CSV is its only stamp.

The commit is the output of `git describe --always --dirty --abbrev=8`, taken when the binary is built (the
`StampGitCommit` target in `src/GridSim.Cli/GridSim.Cli.csproj`), so a server with no checkout still reports it, and
`-dirty` marks uncommitted changes. A build with no git reports the commit passed as `SourceRevisionId`, and
`unknown` when there is none. `--note "text"` adds a `note=` to the `run:` line of `bench` and `bench --perf`, and
`bench` also prints a `Run note:` paragraph; use it to record the hardware and the rental price.

**Engines.** Every engine is swept across every case with the options `MaxIterations = 100`,
`EnforceQLimits = false` and the default tolerance `1e-8` p.u. The engines are:

| Engine | What it is |
|---|---|
| `cpu-dense` | Dense Newton-Raphson. The Jacobian is held as a dense matrix; on the CPU the linear step is the shared sparse LU, so the answer is bit-identical to `cpu-sparse`
(`DenseSparseParityTests`). |
| `cpu-sparse` | Sparse Newton-Raphson with a direct sparse LU. ILU(0)-preconditioned BiCGSTAB runs only if the factorisation is singular. |
| `bfs` | The backward/forward sweep. It runs only on a radial network and prints `n/a (meshed)` on any other. |
| `cuda-dense`, `cuda-sparse` | The same two Newton engines with the linear step on a CUDA device. Added by `--gpu` when CUDA answers. |
| `rocm-dense`, `rocm-sparse` | The same on a ROCm device. Added by `--gpu` when ROCm answers. |
| `opencl-dense`, `opencl-sparse` | The same on an OpenCL device. Added by `--gpu` when an FP64 OpenCL device answers. |

`bench` builds each engine by name and does not use the automatic engine choice of
[ch. 5](<0005 Running power flows.md#how-the-solve-works>), because a benchmark has to run every engine on every case.

**Cases.** The nine built-in classics (`case9`, `case14`, `case30`, `case57`, `case118`, `case145`, `case300`,
`case24_ieee_rts`, `cigre_mv`) plus every `.m` file under `data/matpower/`, searched recursively. A file whose name
matches one of the nine built-ins is not run twice. Two files with the same name in different folders both run, and the
report does not merge them: `server-results/cpu/bench.csv` has `case9241pegase` three times. The header's `cases=` is
the count. Cases are ordered by a size hint, then by name. The hint of a file is the number made of the digits in its
name, and 0 when the name has none, so a file such as `GBnetwork` (2,224 buses) sorts before `case3`; a built-in uses a
fixed hint. Dropping more `.m` files into `data/matpower/` adds them to the next run;
`tools/fetch-cases.sh <dir>` downloads the PGLib-OPF and MATPOWER archives into `<dir>/data/matpower`.

**Cells.** Each cell reads `conv / iters / mismatch / mean ms`.

- `conv` is `yes` when the solve converged and `**NO**` when it did not. `iters` and `mismatch` come from the first
  run; the mean is over all runs. A `**NO**` row with `100` iterations hit the iteration cap.
- `mean ms` is the mean wall-clock time of a whole solve, rounded to whole milliseconds. Each cell first runs one
  untimed warm-up solve of the same case on the same engine, so the mean excludes the one-off cost of the first call
  (just-in-time compilation, and on a GPU engine kernel compilation). A cell timed once (above 4,000 buses) therefore
  takes twice its solve time.
- A cell can instead read `(xN)` after the time, meaning only `N` runs were made (see below); `OOM` when the
  allocation threw; `ERR:` and the message when the solve threw; `LOAD FAIL:` when the case file would not load;
  `n/a (meshed)` for `bfs` on a meshed case; and `n/a (>3000 buses, skipped)` for a GPU engine on a large case.

**Runs per case.** `--runs N` is the ceiling. A CPU engine makes 1 run on a case above 4,000 buses, at most 3 above
1,000 buses, and `N` otherwise. A GPU engine makes at most 3.

**Concurrency.** `--parallel N` sets how many cases run at once; the default 0 means every logical core. The memory
of a large dense solve is the constraint, so the dense engines run at most 3 cases above 3,000 and up to 8,000 buses at once
and 1 case above 8,000 buses, whatever `--parallel` says. GPU engines always run 1 case at a time, because one device
serves every thread. Parallel runs compete for cores and memory bandwidth, so use `--parallel 1` to measure the
latency of one solve and the default to measure throughput.

**Comparison table.** The last table has one row per case and one column per engine. A cell is the mean time, or `NO`
(did not converge), `n/a` (`bfs` on a meshed case), `skip` (a GPU engine on a case above 3,000 buses), `OOM`, `ERR:` or
`load-fail`. It is the table to read first. The failing rows in the capture (`pglib_opf_case3_lmbd`,
`pglib_opf_case162_ieee_dtc`, `pglib_opf_case240_pserc`) fail on every engine alike: the PGLib-OPF files hold
optimal-power-flow operating points, and a plain Newton power flow from a flat start does not reach some of them (three
of the six PGLib-OPF cases in the capture converge: `pglib_opf_case5_pjm`, `pglib_opf_case30_as` and
`pglib_opf_case73_ieee_rts`). A row that fails on every engine is a property of the case.

**Files.** `bench` writes three files next to each other.

| File | Contents |
|---|---|
| the `--out` file (default `results/benchmark-results.md`) | The report above. |
| the same name with `.csv` | One row per engine and case that was measured: `engine,case,buses,gens,branches,conv,iters,mismatch,mean_ms,runs,commit`. A case shown as `n/a`, `skip` or `LOAD FAIL` has no row; an `OOM` or `ERR:` case has a row with `conv=False`, `iters=0` and `runs=0`. |
| the same name with `.run-metadata.json` | The machine, build, runtime, vector tier and run settings as JSON, with `parameters` (`runs`, `parallel`, `cases`, `gpuRequested`, `engines`, `warmupSolvesPerCell`, the report and CSV paths). |

Each report keeps its own metadata, so several `bench` reports can share a folder (`--out results/a100-p1.md`,
`--out results/a100-p8.md`). A re-run with the same `--out` replaces that report and its metadata.

**Exit code.** Every cell is run and reported whatever happens to the others; the exit code is the batch rule: 1 when
any case could not be loaded (a `LOAD FAIL` row), otherwise 3 when any solve did not converge (`NO`), ran out of memory
or threw (`OOM`, `ERR:`), otherwise 0. `n/a` cells (the radial sweep on a meshed case) and GPU size skips are not
failures. The failed cells are listed on standard error at the end. A case that does not converge on any engine, such
as the three PGLib-OPF cases of the archive, therefore makes a full-archive run exit 3. The flags are in [ch. 19, CLI reference](<0019 CLI reference.md#bench>).

## Variations

### The performance yardstick: `bench --perf`

```bash
gridsim bench --perf --runs 5 --out results/perf-docs.md
```

```
$ gridsim bench --perf --runs 5 --out results/perf-docs.md
    GridSim  -  2026-10-20 05:05:20 UTC
    code:    git v1.95.294-173-g203c6b9c  |  GridSim.Core v1.95.467.0  |  build Release
    runtime: .NET 10.0.12  X64  GC=Server
    machine: LAPTOP  Microsoft Windows 10.0.26300
    cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
    simd:    Avx256  (4 doubles/step)  FMA=True
    backend: cpu
    run:     perf-baseline  runs=5

Focused before/after yardstick for the performance programme. Single-threaded, warmed,
median of N runs. Allocations are managed bytes on the measuring thread.

| Path | Case | Buses | metric | value |
|------|------|------:|--------|------:|
| sparse-nr | case118 | 118 | median_ms | 3.432 |
| sparse-nr | case118 | 118 | alloc_kb | 482.7 |
| sparse-nr | case300 | 300 | median_ms | 7.048 |
| sparse-nr | case300 | 300 | alloc_kb | 1899.7 |
| fault-study | case300 | 300 | median_ms | 3.966 |
| fault-study | case300 | 300 | alloc_kb | 1360.2 |
| oltc-loop | case118 | 118 | median_ms | 1.801 |
| oltc-loop | case118 | 118 | alloc_kb | 495.8 |
| harmonic-scan | case118 | 118 | median_ms | 2.669 |
| harmonic-scan | case118 | 118 | alloc_kb | 779.2 |

-> results\perf-docs.md
[exit 0]
```

`bench --perf` is the instrument for "did this change make it slower". It is single-threaded, makes one warm-up
call, and reports the middle sample of `--runs` runs (default 20; the upper of the two middle samples when `--runs` is even) and the managed memory allocated per run on the measuring
thread. It measures four paths: the sparse Newton solve end to end on case118 and case300, the three-phase fault study
on case300, the tap-changer outer loop on case118, and the harmonic penetration scan of 24 orders on case118.
The report goes to `results/perf-baseline.md` unless `--out` names another file, with a CSV of the same name beside it
(`path,case,buses,metric,value,runs,commit`); no `run-metadata.json` is written. Compare two runs by commit, on one machine, with the same `--runs`.
The flags are in [ch. 19, CLI reference](<0019 CLI reference.md#bench---perf>).

### State-estimation timing: `bench --estimate`

```bash
gridsim bench --estimate --runs 1 --cases case14,case30
```

```
$ gridsim bench --estimate --runs 1 --cases case14,case30
GridSim - WLS estimation benchmark
| case         | buses |   meas | backend | iters |   mean ms |
|--------------|-------|--------|---------|-------|-----------|
| case14       |    14 |    122 | dense   |     5 |       8.0 |
| case14       |    14 |    122 | sparse  |     5 |       6.7 |
| case30       |    30 |    254 | dense   |     4 |      14.7 |
| case30       |    30 |    254 | sparse  |     4 |       7.9 |

  -> results\bench-estimate
[exit 0]
```

This times the weighted-least-squares state estimator ([ch. 21](<0021 State estimation.md>)) once per case with
its dense backend and once with its sparse backend, on a seeded synthetic measurement set. Generating the measurements
and solving the truth power flow sit outside the timed region, so each figure is the estimator alone. The default case
list is `case14,case30,case57,case118,case300`; `--cases` replaces it and a positional folder holding a `system.json`
adds a JSON case. The dense backend is skipped above 1,500 buses. `--runs` defaults to 3 and is cut to a third above
1,000 buses. Each backend makes one warm-up estimate before the timed runs. The table goes to the console and the CSV
(`case,buses,measurements,states,backend,converged,iterations,mean_ms,runs,commit`) to
`results/bench-estimate/bench-estimate.csv`; `--out` does not apply. The sparse backend reads slower than the dense one
on the smallest cases, as in the capture. In the `--runs 5` figures under
[Published reference results](#published-reference-results) the two are level at case30 (12.25 and 12.03 ms), the sparse
backend is ahead from case57 (35.01 against 39.95 ms) and is 2.89 times as fast at case300 (330.11 against 954.82 ms).

### The matrix on a GPU: `bench --gpu`

```bash
gridsim bench --gpu --runs 5 --out results/gpu/bench.md
```

`--gpu` (or `--gpu=auto`) adds a dense and a sparse engine for each of CUDA, ROCm and OpenCL that answers.
`--gpu=rocm`, `--gpu=opencl` and `--gpu=cuda` probe and add that vendor only, and `--gpu=cpu` adds none, as it selects
the CPU for every other verb. A vendor that was asked for and does not answer prints one line under the header, naming
the request as given, and is not listed as an engine: `` _CUDA: `--gpu` requested but no device answered._ ``,
`` _ROCm/AMD: `--gpu=rocm` requested but no device answered._ `` or `` _OpenCL: `--gpu` requested but no device answered._ ``.
The `**Engines this run:**` paragraph under the header lists exactly what ran. GPU engines skip cases above 3,000 buses,
run one case at a time, and make at most 3 runs per case. In the A100 and H100 runs the GPU engines take from 19 times
(case118, `cuda-dense`, against a `cpu-sparse` figure of a rounded 1 ms) to 807 times (case2869pegase, A100
`cuda-dense`, 54,886 against 68 ms) as long as `cpu-sparse` (see
[Published reference results](#published-reference-results)); the engines exist to measure that, and to check that a GPU
gives the same answer.

### Many small systems at once: `batchsolve`

```bash
gridsim batchsolve
```

```
$ gridsim batchsolve
GridSim  -  batched dense solve: 64 systems x 64x64   backend batched-dense-lu (cpu)
==============================================================================
correctness: 64/64 solved   max|Delta| vs CPU reference = 0.000E+000
timing (median of 5): backend 1.1 ms   cpu-reference 0.9 ms   backend 57940 solves/s
[exit 0]
```

`batchsolve` builds `--count` strongly diagonally dominant `--n` by `--n` dense systems from a fixed seed (20260725),
solves them all in one batched dispatch on the selected backend, checks the answer against the CPU reference, and
times both. It is a micro-benchmark for the batched solver that a contingency sweep of many same-sized outages would
use, and it is the way to time that solver on a device. The defaults are 64 systems of 64 by 64, 5 runs. A larger batch for a device is `gridsim batchsolve --count 256 --n 64 --gpu`.

Read the two lines as follows. `correctness` gives how many systems solved and the largest absolute difference from
the CPU reference over every solution entry, printed `max|Delta|`.
A singular system returns NaN for its own segment and does not fail the batch; a NaN on one side only is reported as
`MASK MISMATCH`. `timing` gives the median time of the selected backend, the median time of the CPU reference, and
the backend's solves per second. The command exits 1 if the largest difference exceeds `1e-6`
(`CliDefaults.BatchedSolveAgreementTolerance`) or the NaN pattern differs, and 2 if `count x n x n` exceeds the
single-dispatch limit of 2,147,483,647 elements. With `--gpu` on a CUDA or OpenCL device the batch is factored on
the device under one lock and one host synchronisation, which spreads the launch cost over the batch. On a
machine without a device, and on ROCm (which has no batched entry point), the CPU reference runs:

```
$ gridsim batchsolve --count 10 --n 32 --runs 2 --gpu
GridSim  -  batched dense solve: 10 systems x 32x32   backend batched-dense-lu (cpu)
==============================================================================
correctness: 10/10 solved   max|Delta| vs CPU reference = 0.000E+000
timing (median of 2): backend 0.2 ms   cpu-reference 0.2 ms   backend 46189 solves/s
[gpu] no usable gpu device found - falling back to the CPU backend.
[exit 0]
```

The flags are in [ch. 19, CLI reference](<0019 CLI reference.md#batchsolve>).

### Many models of one network: multi-case lanes

A Monte Carlo over branch reactances, or any set of models on one topology, gives a family of matrices with one
sparsity structure. `DcPowerFlow.SolveBatch` (library API; there is no verb yet) solves such a family: each model's
reduced susceptance B' is built exactly as `DcPowerFlow.Solve` builds it, models with the same B' structure form a
family, and each family is factored and solved four models at a time, one model per lane of a 256-bit vector. Entry i
of the result holds exactly the bits of `DcPowerFlow.Solve` for model i, at any thread count, with hardware intrinsics
on or off (`DcPowerFlowBatchTests`, `MultiCaseSparseLuTests`, `SparseLuPlanTests`).

The lanes share one factorisation plan: the order of the depth-first reach and the pivot row of every column, recorded
from the family's first model. Each lane then repeats exactly the arithmetic of a full factorisation of its own matrix
(separate multiply and subtract, never a fused multiply-add), and a lane whose pivot search would choose a different
row leaves the shared plan and is factored alone, so a lane is never wrong, only sometimes not shared.

Sharing needs a stable pivot order, and plain partial pivoting does not give one on real networks: B' has many columns
whose two largest candidates are equal in exact arithmetic, and rounding decides between them. On gb-full only the
first model's own lane shares, even at a relative reactance spread of 1e-9. `SolveBatch` then notices from each
family's first batch that the lanes do not pay and factors the rest of the family one model at a time (the same bits);
its speed comes from running the batches in parallel. An opt-in pivot rule, `diagonalPreference` (threshold partial
pivoting with diagonal preference, as KLU and UMFPACK offer), takes a column's diagonal whenever it is at least that
fraction of the largest candidate; with it every gb-full lane shares. The rule changes the last bits of the answer, so
it is off by default, and a batch under it matches `DcPowerFlow.Solve(model, diagonalPreference)`, not the default
solve.

Measured on the development laptop (8 logical processors, AVX2, Release build, best of 5), 32 models with every
in-service reactance scaled by a factor in [0.9, 1.1] and every demand by one in [0.95, 1.05]:

| Case | `Solve` loop | `SolveBatch`, 1 thread | `SolveBatch`, 8 threads | `Solve(m, 0.1)` loop | `SolveBatch`, 1 thread, 0.1 | `SolveBatch`, 8 threads, 0.1 |
|---|---|---|---|---|---|---|
| case300 | 30.2 ms | 24.6 ms | 9.8 ms | 31.4 ms | 18.1 ms | 8.8 ms |
| gb-full | 220.5 ms | 184.7 ms | 108.6 ms | 189.3 ms | 162.5 ms | 80.6 ms |

Most of a gb-full DC solve is building B' from the model, not factoring it: the 32 factorisations alone take 60.0 ms one
at a time and 20.1 ms in the lanes under the 0.1 rule (3.0 times faster); under plain partial pivoting the lanes cannot
share, so there is no gain there.

The same opt-in rule also lets a Newton power flow reuse its factorisation plan between iterations.
`FixedPatternSparseLuSolver` (library API, passed to `SparseNewtonRaphsonPowerFlow`) keeps every stored Jacobian entry,
zeros included, so the pattern depends only on the structure, and replays the plan whenever the pivot order holds.
The default solver drops exact zeros by value and uses plain partial pivoting, and its pivot order changes at every
Newton step (8 to 12 columns on case300), so a plan is never reusable there. On gb-full the opt-in solver replays 8 of
the 11 factorisations, and the whole Newton solve takes 51.2 ms instead of 98.2 ms (11 iterations either way); the
voltages differ from the default by at most 2.4e-13 pu in magnitude and 9.6e-12 degrees in angle. On case300: 5.8 ms
instead of 9.1 ms, at most 1.5e-14 pu and 3.6e-13 degrees. Whether the default should adopt the rule is a separate
decision, because it would move the last bits of every sparse solve.

### Which backends does this machine have: `gpucheck`

```bash
gridsim gpucheck
```

```
$ gridsim gpucheck
GridSim GPU preflight
=====================
CPU cores: 8   OS: Microsoft Windows 10.0.26300
commit: v1.95.294-168-g786a2162

[CPU] cpu   (vendor=Cpu)
  CPU dense: build solver... solve case9... PASS  (4 it, 76.4 ms, |mis|=1.6E-014)
  CPU sparse: build solver... solve case9... PASS  (4 it, 3.5 ms, |mis|=1.6E-014)

[gpu] no usable rocm device found - falling back to the CPU backend.
[ROCm] cpu (rocm requested but unavailable)   (vendor=Cpu)
[ROCm] raw probe: ROCm unavailable: HIP runtime library (amdhip64) not found
[ROCm]   not present - skipped.

[gpu] no usable opencl device found - falling back to the CPU backend.
[OpenCL] cpu (opencl requested but unavailable)   (vendor=Cpu)
[OpenCL] raw probe: Intel(R) UHD Graphics [OpenCL] rejected: no usable FP64 (cl_khr_fp64) - GridSim needs double precision
[OpenCL]   not present - skipped.

[gpu] no usable cuda device found - falling back to the CPU backend.
[CUDA] cpu (cuda requested but unavailable)   (vendor=Cpu)
[CUDA] raw probe: no CUDA device available (ILGPU saw: [CPU, OpenCL]; driver probe: libcuda not found by .NET)
[CUDA]   not present - skipped.

Preflight done. A FAIL line names the failing call; if the process died mid-line, that call
segfaulted (a P/Invoke signature mismatch) - send that last line and I'll fix the binding.
[exit 0]
```

`gpucheck` probes the CPU, ROCm, OpenCL and CUDA in that order. For each it prints the backend description and, for
the three GPU backends, a `raw probe` line with the device name or the reason none was found. On every backend that
answers it then builds a dense and a sparse Newton solver, solves case9 and prints `PASS` with the iteration count, the
time and the final mismatch. Every line is flushed before the next call starts, so a crash names the call that caused
it. The solve time on a GPU `PASS` line includes the first-call kernel compilation (see
[First-call kernel compilation](#first-call-kernel-compilation)). The `[gpu]` lines are the selector's fallback
messages on the error stream; on a terminal they appear before the backend they describe. The verb takes no flags. It
ends with `gpucheck: OK - every probe that ran passed` and exit 0, or with the failed probes listed on standard error
and exit 3. A backend that is not present is skipped, not failed, so a machine with no GPU exits 0. A `FAIL` line names
the exception; `ran but NOT converged` on case9 means the
backend's arithmetic is wrong; a process that stops mid-line crashed in the native call it last printed. A
CUDA driver probe that hangs stops `gpucheck` after the ROCm lines and before any `[OpenCL]` line, because the first
access to the GPU layer, made by the OpenCL step, runs the `cuInit` probe. That is the symptom reported for an A100 on
an old driver series under [Driver requirements](#driver-requirements).

### Which vector width is running: `simdcheck`

```bash
gridsim simdcheck --expect Avx512
```

```
$ gridsim simdcheck --expect Avx512
GridSim SIMD preflight
======================
CPU cores: 8   OS: Microsoft Windows 10.0.26300
process arch: X64
commit: v1.95.294-173-g203c6b9c

SIMD: Avx256 (4 doubles/step)  FMA=yes  [Avx512F=False Avx2=True Fma=True Sse2=True AdvSimd=False]

Active vector tier: Avx256 (4 doubles per vector step)
Dense-LU / normal-equations / covariance kernels dispatch to this width at run time.
If this reports Avx256 on a box you expect to be AVX-512, the CPU or the runtime is capping the width.
simdcheck: FAILED - expected at least Avx512, but the active tier is Avx256.
[exit 3]
```

`simdcheck` reports the vector tier that the dense LU, the normal-equation and the covariance kernels use: `Avx512`
(8 doubles per step), `Avx256` (4), `Sse128` (2) or `Scalar` (1). The tier changes how fast those kernels run. The
dense LU row update is bit-identical at every tier (`SimdKernelsTests`), and so are the dot-product, batch sine and
cosine, and normal-equation kernels (`D1CrossTierTests`): the tier changes the speed, not the bits. On
a CPU that supports AVX-512F while the active tier is narrower, as on Skylake-SP and Cascade Lake Xeons under the .NET
default, the verb prints `WARNING: this CPU supports AVX-512F but the active tier is narrower` and names the fix:
set `DOTNET_PreferredVectorBitWidth=512` before starting GridSim.

`--expect <tier>` makes the verb a gate. It exits 0 and prints `simdcheck: OK` when the active tier is at least the
named one, exits 3 and prints `simdcheck: FAILED` to the error stream when it is narrower, and exits 2 for a name other
than `Avx512`, `Avx256`, `Sse128` or `Scalar` (case-insensitive). The numerals 0 to 3 are also accepted and name
`Scalar`, `Sse128`, `Avx256` and `Avx512` in that order, so `--expect 3` is `Avx512`. Put it in the preflight of any benchmark box that
should run 8-wide, so a run on a narrower runtime fails before it produces a misleading timing. The tier is also in
the `simd:` line of every `bench` header. The flags are in [ch. 19, CLI reference](<0019 CLI reference.md#simdcheck>); the
validation view of the verb is in [ch. 27](<0027 Validation tools.md>).

### Comparing two backends on one verb

`--cpu` is the default. `--gpu` (or `--gpu=rocm`, `--gpu=opencl`, `--gpu=cuda`) moves the linear solve of any
power-flow verb to the device. A mistyped vendor stops the run before any solve:

```
$ gridsim batchsolve --gpu=metal
error: --gpu expected one of auto, cpu, rocm, opencl, cuda but got 'metal'
[exit 2]
```

The header of every report and the `backend` field of every export record the backend that actually ran, for example
`cpu (gpu requested but unavailable)`, so a result always says what produced it.

### Soak and scale drivers

```bash
gridsim cycle case118 --workers 8 --window-ms 1500 --cycles 3
```

```
$ gridsim cycle case118 --workers 8 --window-ms 1500 --cycles 3
GridSim  -  prioritised parallel analysis cycle: IEEE 118-bus
============================================================================================
case 118 buses / 186 branches   engine Newton-Raphson (dense)
window 1500 ms   workers 8   budget 12.0 core-s/cycle   jobs 13 (3 High / 10 Low)
--------------------------------------------------------------------------------------------
cycle |  base ms | wall ms |  HP   | LP done/adm/def | util% | backlog | note
--------------------------------------------------------------------------------------------
    0 |    145.1 |     391 |  3/3  |  10/10 /0     |   19% |       0 | n-1 shards 8/8
    1 |     34.2 |     229 |  3/3  |  10/10 /0     |   10% |       0 | n-1 shards 8/8
    2 |     32.5 |     244 |  3/3  |  10/10 /0     |   12% |       0 | n-1 shards 8/8
--------------------------------------------------------------------------------------------
3 cycles   avg util 14%   HP met every cycle: YES   N-1 coverage 8/8 shards
[exit 0]
```

`cycle` is a performance soak driver. Each cycle makes one single-core base solve and fans every downstream analysis out
to a worker pool inside a time window. The table prints the base-solve time, whether every High-priority job finished
(`HP`), the Low-priority jobs done, admitted and deferred, the utilisation of the core-time budget, the backlog and the
N-1 shard coverage. The command exits 1 if the High tier is ever missed, so a long run is a gate on real-time headroom.
The first cycle includes first-call compilation (145.1 ms against 34.2 and 32.5 ms in the capture). The flags are in
[ch. 19, CLI reference](<0019 CLI reference.md#cycle>).

`nminus1-sweep` and `stress` are the scale drivers that `tools/run-regime.sh` runs over the whole case archive:
`gridsim nminus1-sweep --all --sample 3000 --parallel 28` and `gridsim stress --all --from 0.5 --to 3.0 --step 0.05`. Their
flags are in [ch. 19](<0019 CLI reference.md#nminus1-sweep>) and [ch. 19](<0019 CLI reference.md#stress>).

## Backends

A GridSim power flow is a Newton iteration. Each iteration forms the power mismatch, builds the Jacobian, solves the
linear system `J dx = -f` for the correction and updates the voltages. A backend is where that linear solve runs. The
equations are in [technical ch. 3](<../technical/0003 Power flow.md>) and the solver internals in
[technical ch. 4](<../technical/0004 Sparse and GPU numerics.md>).

**Choosing a backend.** The switches are global and apply to any verb:

- `--cpu` (the default) runs everything on the processor.
- `--gpu` or `--gpu=auto` tries ROCm, then OpenCL, then CUDA, and takes the first that answers. This order puts AMD
  Instinct first because ROCm is its native path and its double-precision rate is high; OpenCL is the portable
  open-driver route to AMD, Intel and NVIDIA; CUDA is NVIDIA through ILGPU.
- `--gpu=rocm`, `--gpu=opencl` and `--gpu=cuda` name one backend. `--gpu=cpu` is the CPU. Any other value exits 2.
- `gpucheck` probes in the order CPU, ROCm, OpenCL, CUDA and lists each result.

**Detection.** ROCm is available when the HIP runtime reports at least one device and the native hipSOLVER, rocSPARSE
and rocBLAS libraries load. CUDA is available when ILGPU opens a CUDA accelerator. OpenCL is available when ILGPU opens
an OpenCL accelerator and that device passes the double-precision test below. Probing never throws: a missing driver, a
missing library or a rejected device marks the backend unavailable and records the reason, which `gpucheck` prints on
the `raw probe` line.

**Double precision is required.** GridSim solves in 64-bit floating point. An OpenCL device is accepted only after a
one-thread kernel computes `(1 + 2^-40) - 1` in double precision from operands sent by the host and returns exactly `2^-40` (single precision returns 0); a device without `cl_khr_fp64`, such as the integrated Intel graphics in the
`gpucheck` capture, is rejected with `no usable FP64`. CUDA and ROCm devices are not tested the same way. For those two the description carries a note chosen from the
device name: `FP64 is fast (data-centre GPU)` for A100, H100, V100, Tesla and GH200 on CUDA, `FP64 near full rate (AMD
Instinct MI-series)` for AMD Instinct, MI-series and `gfx90a` or `gfx94` names on ROCm, and
`FP64 is likely rate-limited (consumer GPU)` (CUDA) or `FP64 rate is device-dependent` (ROCm) for anything else. An
OpenCL device that passes the test also gets a name-based note: `FP64 near full rate (AMD Instinct / data-centre GPU)`
for Instinct and MI-series names, and `FP64 may be rate-limited (consumer GPU)` otherwise. The note is a hint; `bench`
measures the real rate.

**Fallback.** When `--gpu` (auto-detect) finds no device, GridSim prints
`[gpu] no usable gpu device found - falling back to the CPU backend.` to the error stream, runs on the CPU and
records the backend as `cpu (gpu requested but unavailable)`. The report is the CPU report. A script that
discards the error stream cannot tell, so check the recorded backend, or run `gpucheck` first. A named vendor
(`--gpu=rocm`, `--gpu=opencl`, `--gpu=cuda`) with no usable device does not fall back: the run prints the `[gpu]` line
and `error: --gpu=<vendor> was asked for but no usable device of that kind was found; ...` and exits 2. `bench` and
`gpucheck` are the exceptions: they probe each vendor themselves and report an absent one in their output.

**What runs on the GPU.** Only the linear solve inside each Newton step, in one of three forms.

| Form | CUDA and OpenCL (ILGPU kernels) | ROCm (native libraries) |
|---|---|---|
| Dense LU | Gaussian elimination with partial pivoting on an augmented matrix, the pivot search done on the device, two host synchronisations per solve. A pivot at or below `1e-12` times the largest matrix entry raises an error, as on the CPU. | hipSOLVER `getrf` and `getrs`, with the same near-singular check applied to the factored matrix. |
| Sparse | Jacobi-preconditioned BiCGSTAB with a CSR matrix-vector product and on-device dot products, tolerance `1e-10` relative residual. | The same iteration, fully device-resident, on rocSPARSE `csrmv` and rocBLAS vector kernels. |
| Batched dense | The dense elimination with a batch index, one lock and one synchronisation for the whole batch (`batchsolve` only). | No entry point. The CPU reference runs. |

Everything else stays on the CPU: the mismatch, the Jacobian assembly, the voltage update, the generator reactive-limit
passes and the tap-changer loop. A GPU sparse solve that breaks down or stagnates returns nothing, and the Newton step
is retried on the exact CPU direct LU before the ILU(0) fallback is tried, so a device that gives up never stops a run.
One lock serialises every device call, so a verb that fans solves out over worker threads uses the device one solve at a
time.

With `--gpu` the engine is chosen by size: the dense engine up to 200 buses and the sparse engine above
(`SolverSelector.DenseSparseCrossoverBuses`), because the dense engine on a device is the device LU. On the CPU every
Newton power flow is sparse. The list of exceptions is in
[ch. 19, CLI reference](<0019 CLI reference.md#solver-selection>).

### First-call kernel compilation

The ILGPU kernels are compiled when a solver first loads them, not when GridSim starts, and the native ROCm libraries are
loaded on first use. The first GPU solve in a process therefore includes compilation, library loading and device
initialisation on top of the solve. No run in the repository separates that cost from the solve time, so this chapter
gives no figure for it, and a one-off timing of a GPU solve says nothing about its steady-state speed.

Three consequences follow from the code.

- The time on a GPU `PASS` line in `gpucheck` includes compilation.
- `bench` warms up every cell with one untimed solve, so its means exclude the compile; the time of a `gpucheck` probe
  does not.
- Time a GPU with several runs all the same: the warm-up removes the compile, not the run-to-run spread.

### What is outside the bit-identity contract

GridSim's promise is that results are bit-identical across runs, across parallel paths, across CPUs and between the dense
and sparse engines. These are outside it:

- **Every GPU solver.** A device LU or BiCGSTAB sums in a different order from the CPU. The tests pin CPU dense equal to
  CPU sparse, bit for bit (`DenseSparseParityTests`), and pin the device-gives-up fallback to the CPU path bit for bit
  (`GpuFallbackTests`). They pin the device solvers to the CPU solvers only to a decimal tolerance on a 4 by 4 system,
  10 places for the dense LU and 8 for the sparse solve (`GpuSolverSeamTests`), not to the bit. Those are ordinary tests:
  on a machine with no device they run the CPU fallback, so they exercise the device only where one is present.
  `GpuDeviceKernelTests` repeats the device checks (the batched dense LU included) for CUDA and for OpenCL and runs only
  where that accelerator is present; elsewhere each is reported skipped with the reason, so a passed row there means the
  kernels ran on the device.
  `docs/developer/benchmarks/server-results/a100-results.md` says that exported converged voltages from the A100 match the CPU's; that is
  prose, with no export file in the repository, and its heading "bit-for-bit" is not supported by any test. A GPU
  result is correct to the solver tolerance, not identical to the last bit.
- **The opt-in diagonal-preference pivot rule.** `DcPowerFlow.Solve(model, diagonalPreference)`, `SolveBatch` with a
  `diagonalPreference` and `FixedPatternSparseLuSolver` agree with the default solve to rounding, not to the bit (see
  [Many models of one network](#many-models-of-one-network-multi-case-lanes)). They are deterministic across runs and
  threads and use the same IEEE operations as the default path (no fused multiply-add). At a preference of 0 the two DC
  calls are the default solve exactly; `FixedPatternSparseLuSolver` keeps explicit zeros, so it differs from the default
  even at 0.
- **Timings.** They differ by machine and by run.

The CPU path is the validated reference: it does not change with `--gpu` absent, and every number in this chapter that
GridSim promises to reproduce comes from it.

## Published reference results

Every row below names its machine, case, backend, time, date and the file it comes from. Paths are relative to `docs/`.
Times are as recorded; they differ by machine and run. The July 2026 runs were made on older builds, before the CPU
dense engine took the sparse LU step ([Backends](#backends)), so their `cpu-dense` times do not describe the current
engine. The 1 July server sweep labels its sparse engine `ILU0/BiCGSTAB`; the 2 July GPU runs label `cpu-sparse`
`direct LU / ILU0-BiCGSTAB`, which is the current label, so the 1 July sparse column may describe a different algorithm
from the current engine.

**Desktop CPU matrix.** Machine `DESKTOP-S5V62MR` (Windows, X64, 8 logical cores, 8 GB), commit `aa40db5b-dirty`,
2026-07-08, `bench --runs 10 --parallel 8`, mean ms per solve. Source: `benchmark-results.csv` and `run-metadata.json`.
The `benchmark-results.md` beside them is a stub.

| Case | Buses | cpu-dense | cpu-sparse |
|---|---:|---:|---:|
| case118 | 118 | 13.07 | 3.81 |
| case300 | 300 | 158.49 | 5.46 |
| case1354pegase | 1,354 | 9,170.42 (3 runs) | 37.53 (3 runs) |
| case2869pegase | 2,869 | 89,403.18 (3 runs) | 184.52 (3 runs) |
| case9241pegase | 9,241 | no result (0 runs recorded) | 857.33 (1 run) |

**Server CPU sweep.** Machine `curious-bohr` (Ubuntu 22.04.5, AMD EPYC 7763, 28 logical cores, 57 GB), commit
`c41ef951-dirty`, 2026-07-01, `bench --runs 10 --parallel 28`, sparse engine, mean ms. Source:
`server-results/cpu/bench.csv` (first row per case), with the narrative in `server-results/README-section.md`. The run
was a partial snapshot; the dense half was never run. Twenty-eight cases solving at once compete for the machine, so
these are throughput figures, not single-solve latency.

| Case | Buses | Iterations | Mean ms |
|---|---:|---:|---:|
| case300 | 300 | 5 | 32.12 |
| case1354pegase | 1,354 | 4 | 213.89 |
| case2869pegase | 2,869 | 6 | 735.31 |
| case9241pegase | 9,241 | 6 | 3,831.78 |
| case_ACTIVSg10k | 10,000 | 4 | 16,662.34 |

**Single-thread yardstick.** `bench --perf`, median ms. Source: `perf-baseline.md` (reference machine) and
`perf-after.md` (desktop, a Debug build, so not comparable with Release).

| Machine | Date | Run | sparse case118 | sparse case300 | fault case300 | OLTC case118 | harmonic case118 |
|---|---|---|---:|---:|---:|---:|---:|
| `quick-curie` (EPYC 7763, 28 logical cores, 57 GB, Ubuntu 24.04.3, .NET 10.0.10) | 2026-08-11 | `--runs 40`, Release, under other load | 1.770 | 8.334 | 8.640 | 8.468 | 8.012 |
| `DESKTOP-S5V62MR` (8 logical cores, 8 GB) | 2026-07-19 | `--runs 20`, Debug | 1.388 | 8.150 | 26.570 | 13.950 | 6.293 |

**State estimation.** `bench --estimate --runs 5`, mean ms, dense and sparse backend. Source: `bench-estimate.csv`
(the file records commit `unknown` and no machine).

| Case | Buses | Measurements | States | Iterations | Dense | Sparse |
|---|---:|---:|---:|---:|---:|---:|
| case14 | 14 | 122 | 27 | 5 | 4.79 | 9.39 |
| case30 | 30 | 254 | 59 | 4 | 12.25 | 12.03 |
| case57 | 57 | 491 | 113 | 5 | 39.95 | 35.01 |
| case118 | 118 | 1,098 | 235 | 6 | 73.35 | 55.27 |
| case300 | 300 | 2,544 | 599 | 9 | 954.82 | 330.11 |

**GPU against CPU.** `bench --gpu --parallel 1 --runs 5` on rented boxes, mean ms per solve, commit `8ec809d2-dirty`,
2026-07-02, CUDA driver series 570 (CUDA 12.8). Source: `server-results/a100/cuda-p1/bench.md` and
`server-results/h100/h100-p1/bench.md`, with the write-up in `server-results/a100-results.md`. Each GPU is compared with
the CPU of its own box (AMD EPYC 9554, 28 logical cores).

| Machine | Case | cpu-sparse | cpu-dense | cuda-dense | cuda-sparse |
|---|---|---:|---:|---:|---:|
| `keen-darwin`, A100 80GB PCIe, 118 GB RAM | case118 | 1 | 5 | 19 | 38 |
| | case300 | 5 | 78 | 341 | 179 |
| | case1354pegase | 17 | 2,281 | 7,808 | 923 |
| | case2869pegase | 68 | 46,695 | 54,886 | 4,851 |
| `curious-fermi`, H100 PCIe, 177 GB RAM | case118 | 1 | 5 | 19 | 32 |
| | case300 | 5 | 80 | 354 | 165 |
| | case1354pegase | 16 | 2,243 | 6,226 | 886 |
| | case2869pegase | 65 | 33,865 | 44,496 | 4,320 |

On case2869pegase `cpu-sparse` is faster than the faster GPU engine, `cuda-sparse`, by 71.3 times on the A100 (4,851
against 68 ms) and 66.5 times on the H100 (4,320 against 65 ms). In all eight rows `cuda-dense` is slower than
`cpu-dense`, and `cuda-sparse` is faster than `cpu-dense` only on case1354pegase and case2869pegase. For `cuda-dense` on
case2869pegase the H100 takes 19 per cent less time than the A100 (44,496 ms against 54,886 ms), which is 1.23 times as
fast. `a100-results.md` infers from this that launch and transfer overhead limit the solve, not arithmetic. The
repository holds no profile that tests that inference, so this chapter states no cause for the GPU times.

`a100-results.md` also reports the following, with no data file behind any of it. On the A100 box, case118 N-1 screening
ran at 932 solves per second on 28 CPU threads (0.2 s) against 18 solves per second on the GPU run serially (10.5 s). At
`--parallel 28`, case2869pegase read `cpu-sparse` 137 ms, `cuda-sparse` 4,597 ms and `cuda-dense` 55,111 ms; the CPU-only
`--parallel 28` sweep in `server-results/a100/bench.md` (a different box, `brave-heisenberg`) records `cpu-sparse` 135 ms
for the same case.

**A July result.** In the July 2026 A100 run (`server-results/a100/cuda-p1/bench.md`) the `cpu-dense` engine recorded an
error on case9241pegase, a negative element count (`-1973164928`), while `cpu-sparse` solved the case in 405 ms. The
current build no longer shows this: its `cpu-dense` engine solves case9241pegase in 6 iterations (199,934 ms in the
capture above). GPU engines skip cases above 3,000 buses. There are no AMD Instinct results:
`server-results/mi300/README.md` is an empty results tree awaiting hardware.

**Figures with no data file.** These were recorded in the project's earlier benchmarking write-up and have no result
file in the repository. That write-up named the reference machine `quick-curie` (AMD EPYC 7763, 28 logical cores, 57 GB:
the machine of the `bench --perf` row above, whose CPU `perf-baseline.md` records) and an NVIDIA RTX A6000 48 GB with
CUDA 12.8; the repository records no graphics card for it. Reproduce each with the command given; your figures differ.

| Workload | Result | Reproduce |
|---|---|---|
| `uk` (the reduced GB model, 20 buses) | 0.84 ms per solve, idle machine; the current build prints 3 cold-start iterations | `gridsim chain uk --steps 500` |
| `gb-synthetic` (2,227 buses) | 21 ms per solve (300 warm solves in 6.9 s, loaded machine); the write-up gave 6 cold-start iterations, the current build prints 5 | `gridsim chain gb-synthetic --steps 300` |
| RTX A6000, dense CUDA engine, case300 | over 5.5 minutes, against 0.6 s on the CPU, with the correct answer; the CPU engine then was the old dense LU, and the current `cpu-dense` takes 47 ms in the capture above | `gridsim bench --gpu --parallel 1 --runs 1`, then read the `cuda-dense` column for case300 |

With `--gpu`, a single verb routes the dense engine up to 200 buses and the sparse engine above, so
`gridsim case300 --gpu=cuda --once` runs the sparse engine and is not a reproduction of the dense row; `bench` builds
each engine by name.

The 14 July 2026 baseline of the performance programme was recorded on the desktop (8 threads, Release build, builds
before the CPU dense engine took the sparse LU step):

| Workload | Result | Reproduce |
|---|---|---|
| `bench --runs 3`, dense / sparse mean ms | case118 8 / 60, case300 1,964 / 23, case1354pegase 7,130 / 59, case2869pegase 65,820 / 150 | `gridsim bench --runs 3` |
| `bench --estimate`, dense / sparse mean ms | case57 19 / 18, case118 110 / 51, case300 1,236 / 161 | `gridsim bench --estimate --runs 3 --cases case57,case118,case300` |
| `transient case118`, 5 minutes at 20 ms | 129.6 s for 15,000 slices | `gridsim transient case118 --minutes 5 --dt-ms 20` |
| `nminus1-sweep case118` | 708 solves per second on 8 threads | `gridsim nminus1-sweep case118` |

The cross-validation instruments and their captured results are in [ch. 27](<0027 Validation tools.md>); the GB
models, the replay and the estimator are described in [ch. 22](<0022 Estimating GB history.md>) and
[ch. 23](<0023 Replaying estimated states.md>).

The server sweep of 1 July (`server-results/cpu/bench.csv`) also holds `case_ACTIVSg2000` (2,000 buses, 2,067 ms),
`pglib_opf_case5658_epigrids` (5,658 buses, 11,963 ms) and `case8387pegase` (8,387 buses, 1,115 ms) beside the rows
above. The archive holds networks of up to about 78,000 buses (ACTIVSg25k and ACTIVSg70k among them) that the partial
sweep never reached.

**This chapter's captures.** Machine `LAPTOP` (8 logical cores, 8 GB), Release build, in `docs/_captures/bench/`.
Their header lines name two builds. `bench-matrix.txt` (the matrix above, 2026-10-04), `gpucheck.txt` and
`simdcheck.txt` name Windows 10.0.26200 and commit `3f9d9be3`. `bench-perf.txt` (2026-10-20, GridSim.Core 1.95.467),
`simdcheck-expect.txt` and `simdcheck-expect-ok.txt` name Windows 10.0.26300 and commit `v1.95.294-173-g203c6b9c`.
`bench-estimate.txt`, `batchsolve-default.txt`, `batchsolve-gpu.txt`, `batchsolve.txt` (a UTF-8 console capture that
shows the Greek Delta the other two print as `?`), `gpu-bad-value.txt` and `cycle.txt` print no machine or commit line.

### Do you need a GPU for power flow

In every row of the GPU table `cpu-sparse` is the fastest engine, and no GPU engine beat it. A GPU engine beat
`cpu-dense` only as `cuda-sparse`, on case1354pegase (923 against 2,281 ms on the A100) and case2869pegase (4,851 against
46,695 ms); `cuda-dense` was slower than `cpu-dense` in all eight rows. A power-flow Jacobian is sparse because each bus is
joined to few others: in the captured cases there are between 1.37 branches per bus (case300, 411 branches for 300 buses)
and 1.74 (case9241pegase, 16,049 branches for 9,241 buses). The design argument for the CPU is in
[technical ch. 4](<../technical/0004 Sparse and GPU numerics.md>); the measurements here show the outcome, not the
cause. On these results, spend on CPU cores for power flow. A GPU is worth renting to measure the question for a new
card, and `batchsolve` is the verb that shows whether a batched device kernel wins on a workload of many identical small
systems.

## Renting a GPU server

This is the procedure for measuring the CPU, a CUDA card and an AMD Instinct card against each other, at a known price.
It answers whether you need a GPU at all, not which GPU is fastest. It runs in three phases, each producing a comparable
`bench.md`, `bench.csv` and `run-metadata.json`.

1. **CPU baseline** on your own machine or a rented CPU box.
2. **CUDA** on a rented NVIDIA box. Rent the same generation as the AMD card: A100 against MI250, H100 against MI300X.
3. **AMD Instinct** on a rented MI250 or MI300X box.

Rent NVIDIA and AMD from the same provider so the prices compare, and record the exact price per hour in the run note.

### Driver requirements

The verified configuration is NVIDIA driver series 570 with CUDA 12.8. Both published GPU runs record
`driverVer=12080` (`server-results/a100/cuda-p1/run-metadata.json` and `server-results/h100/h100-p1/run-metadata.json`),
and their run notes name driver series 570. `server-results/a100-results.md` reports that a rented A100 with driver series 535 and
CUDA 12.2 could not run CUDA: `cuInit` hung and the kernel log showed Xid 119 (a GSP RPC timeout), reboots, disabling the
GSP firmware and persistence mode did not clear it, and an image with series 570 and CUDA 12.8 fixed it at once. That
report is prose: the repository holds no log of the 535 failure, and no driver series other than 535 and 570 has been
tried.

| NVIDIA driver | CUDA | Status on A100 | Source |
|---|---|---|---|
| 535 series | 12.2 | Reported failing: Xid 119, `cuInit` hangs | `server-results/a100-results.md` (prose) |
| 570 series | 12.8 | Verified: dense and sparse CUDA engines ran | `driverVer=12080` in both `run-metadata.json` files |

`gpucheck` shows the fault in seconds. A CUDA driver probe that hangs stops `gpucheck` after the ROCm lines and before
any `[OpenCL]` line (see [Which backends does this machine have](#which-backends-does-this-machine-have-gpucheck)). That
is the symptom reported for series 535: try an image with series 570 before debugging further, and do not debug it on
the meter. When the probe succeeds the CUDA description ends `driver probe: cuInit=0 driverVer=12080 ...`, where
`driverVer=12080` is CUDA 12.8. For AMD, a comment in `src/GridSim.Gpu/RocmInterop.cs` states that an MI300X needs
ROCm 6.0 or newer and an MI250 runs on ROCm 5.x; no ROCm run has been made to test it.

### Publish once

Publish a self-contained build on any machine with the .NET 10 SDK, so the rented box needs no .NET install and only its
own GPU driver stack:

```bash
dotnet publish src/GridSim.Cli -c Release -r linux-x64 --self-contained \
    -p:PublishSingleFile=false -o publish/linux-x64
cp tools/gpu-bench.sh tools/run-regime.sh tools/run-gpu.sh publish/linux-x64/
tar czf gridsim-linux-x64.tar.gz -C publish/linux-x64 .
```

The publish carries the CLI with Core, Gda and Gpu and the .NET runtime; the Windows-only WPF app is not part of it. The
native GPU libraries are not bundled. They live on the rented box and are resolved on first use, so the same tarball
runs on a CPU box, a CUDA box, an OpenCL box and an MI box.

- **ROCm:** `libamdhip64.so`, `libhipsolver.so`, `librocsparse.so` and `librocblas.so`.
- **OpenCL:** the platform's OpenCL loader, `libOpenCL.so.1`.
- **CUDA:** the CUDA driver, `libcuda.so`.

To bench the whole case archive rather than the bundled cases, run `tools/fetch-cases.sh publish` on a machine with
network access; it fills `publish/data/matpower` with the PGLib-OPF and MATPOWER archives.

### On each rented box

Copy the tarball to the box with the provider's file transfer, then unpack it:

```bash
mkdir gridsim && tar xzf gridsim-linux-x64.tar.gz -C gridsim && cd gridsim
chmod +x gridsim gpu-bench.sh run-regime.sh run-gpu.sh
```

**Step 1, preflight first.** Run `./gridsim gpucheck` and read it before the sweep.

- `CPU dense` and `CPU sparse` pass on every box.
- A `PASS` under ROCm, OpenCL or CUDA means that path works; go on.
- `[OpenCL] ... rejected: no usable FP64` means the OpenCL device has no double precision, as an Intel integrated GPU does.
  It is skipped correctly. Use ROCm or CUDA.
- `FAIL <Type>: <message>` is a binding problem; the message names the failing call.
- A process that dies mid-line crashed in the native call it last printed (a P/Invoke signature mismatch). The ROCm
  signatures in `src/GridSim.Gpu/RocmInterop.cs` are matched against the ROCm 6.x headers, each entry citing the header
  it follows. The CPU result stays valid.

**AMD Instinct first contact.** The native ROCm path has never run on real hardware (see [Limits](#limits)); work
through this on the MI box.

1. `./gridsim gpucheck`. The `[ROCm] raw probe` line prints the device name read with `hipDeviceGetName` and an FP64
   note, in the form `AMD ROCm/HIP: <name> (device 0/<count>) [HIP] - <FP64 note>`; for an Instinct card the note is
   `FP64 near full rate (AMD Instinct MI-series)`. A name means `hipGetDeviceCount` found a device. The description is
   built after `hipSetDevice` whatever its status, so check that the line has no `(hipSetDevice status=N)` suffix.
2. Confirm the four native libraries resolve: `ls /opt/rocm*/lib | grep -E 'amdhip64|hipsolver|rocsparse|rocblas'`. If
   one is missing, set `LD_LIBRARY_PATH=/opt/rocm/lib`, or add the versioned symlinks the resolver already probes
   (`libhipsolver.so.0`, `librocblas.so.4`).
3. If a native call faults, the header citations in `RocmInterop.cs` are the map. The dense path uses the native
   hipSOLVER `hipsolverDgetrf` and `hipsolverDgetrs` with an explicit workspace. The sparse path is device-resident
   BiCGSTAB on `rocsparse_dcsrmv` and rocBLAS level-1 calls (`ddot`, `daxpy`, `dscal`, `dcopy`), with the Jacobi
   apply done by `rocblas_ddgmm`.

**Step 2, the one-shot run.**

```bash
# GPU box: adds whichever of rocm, opencl and cuda engines answered
./gpu-bench.sh "H100 SXM 80GB - \$2.99/hr RunPod"

# CPU baseline box
./gpu-bench.sh "EPYC 9654 96c - baseline" --cpu-only
```

`gpu-bench.sh` runs three steps: `gpucheck` (saved as `preflight.txt`), `bench --gpu --note "<text>" --out <dir>/bench.md`
(the matrix, with the default `--parallel`, where the published GPU runs used `--parallel 1`), and a traced solve export
of case118 and case300 with `--enableExport`, on the GPU when `--cpu-only` is absent. The note goes into the report
header and `run-metadata.json`, and the commit is in the header and every CSV row. The script bundles
`results/<stamp>.tar.gz` holding `preflight.txt`, `bench.md`, `bench.csv`, `run-metadata.json`, `case118-solve.json` and
`case300-solve.json`. Copy the bundle back.

**Step 3, compare.** Line up the three comparison tables and read the mean solve time per engine for each case. The
price in each run note anchors the conclusion: at equal rental cost, the CPU wins for power flow. If a GPU engine does
win somewhere, the table says where and by how much. The measured result so far is under
[Published reference results](#published-reference-results).

### A full server regime

`tools/run-regime.sh`, run from the unpacked publish folder, is the one-shot driver for a whole-archive test on a
server. It runs these phases, each under its own `timeout`: a bare `case9` smoke solve; `bench --runs 10 --parallel
<cores>` (180 minutes); `nminus1-sweep --all --sample 3000 --parallel <cores>` (90 minutes); a full `nminus1-sweep` on
case1354pegase, case2869pegase and case9241pegase (30 minutes each); `stress --all --from 0.5 --to 3.0 --step 0.05
--parallel <cores>` (60 minutes); `chain` of 100 steps on case118 (`fixed`), case300 (`ramp`) and case1354pegase
(`daily`) (15 minutes each); then a tarball `gridsim-results-<host>-<stamp>.tar.gz`. It sets `DOTNET_GCHeapHardLimit` to
the fixed value `0xC80000000` (53,687,091,200 bytes, 50 GiB) whatever the RAM, so an oversized case raises a catchable
out-of-memory error, recorded as `OOM`, instead of being killed by the operating system. Results are flushed per row, so
a killed phase still leaves its finished rows. The sweep verbs are in [ch. 11](<0011 Contingency analysis.md>) and
[ch. 19](<0019 CLI reference.md#nminus1-sweep>).

`tools/run-gpu.sh` is the isolated CPU-versus-GPU check for a box with a device. It solves case9 with `--gpu` as a
probe, exports case118, case300 and case1354pegase on the CPU and on the GPU and compares the two exports, then runs
`bench --runs 3` on `--gpu` and on `--cpu`. The export takes the dense engine up to 200 buses and the sparse engine
above, so case118 is the only comparison on the GPU dense LU; case300 and case1354pegase compare the GPU sparse engine. The comparison passes when `max|dVm|` is at most `1e-6` pu and `max|dVa|` is
at most `1e-4` deg, and the script exits 1 if any comparison fails or any solve exits non-zero. It needs `python3` and
writes under `results-gpu/<stamp>/`.

## Pitfalls

- **A one-run timing is not a benchmark.** Two cases running at once disturb each other, and one run shows no spread.
  (The capture below predates the warm-up solve, so its one-run means include the first call's compilation.) In the capture the sparse engine reads slower than
  the dense one on `case57` (41 against 18 ms), `case69` (6 against 5 ms) and `GBreducednetwork` (63 against 49 ms),
  the reverse of the other cases of 118 buses and more; no run isolates the cause. Use `--runs` of 5 or more, or
  `bench --perf`, which warms up.
- **Parallel runs measure throughput.** With the default `--parallel` every core runs a case, so each solve competes with
  the others. Use `--parallel 1` for latency, as the A100 and H100 runs did.
- **A GPU request on a machine without a GPU still exits 0.** The only sign is the `[gpu]` line on the error stream and the
  recorded backend `cpu (gpu requested but unavailable)`. A benchmark of "the GPU" on such a machine times the CPU.
- **`bench --gpu` shows no GPU engines when nothing answered.** The report says so, with a line such as
  `` _CUDA: `--gpu` requested but no device answered._ ``; an engine list with only `cpu-*` and `bfs` means no GPU ran.
- **`gpucheck` exits 0 on a machine with no GPU.** An absent backend is skipped, not failed. A script that needs a
  particular backend gates on `PASS` appearing for it, not on the exit code alone.
- **`simdcheck --expect Avx512` fails on a CPU without AVX-512.** Exit 3 is correct there. On a CPU that has AVX-512F the
  fix is `DOTNET_PreferredVectorBitWidth=512`.
- **A `gpucheck` that stops after the ROCm lines on a rented NVIDIA box** matches the series 535 symptom described under
  [Driver requirements](#driver-requirements). Change the image; do not wait.
- **A re-run with the same `--out` replaces the report and its `.run-metadata.json`.** Give each kept run its own
  `--out` name; several can share a folder.
- **`batchsolve` exits 1.** The batched result differed from the CPU reference by more than `1e-6` or the singular-system
  pattern differed. Treat it as a device fault and run `gpucheck`.
- **A failing row is not always a bug.** `NO` on every engine, as for the three PGLib-OPF cases in the capture, is a
  property of the case. See [ch. 30, Troubleshooting](<0030 Troubleshooting.md>).

## Limits

- The ROCm backend is complete against the ROCm 6.x headers and covered by tests that need no hardware, but it has never
  run on an AMD GPU, so no ROCm timing exists (`docs/developer/benchmarks/server-results/mi300/README.md`).
- The captured runs in this chapter predate the warm-up solve, so their first case per engine includes first-call
  compilation.
- `batchsolve` has no ROCm batched kernel; on ROCm it times the CPU reference.
- GPU engines in `bench` skip cases above 3,000 buses, so no GPU timing exists for the largest networks.
- The published GPU results predate the CPU dense engine's use of the sparse LU step. Only their `cpu-sparse` and GPU
  columns compare with a current run, and the 1 July sparse column carries a different engine label (see
  [Published reference results](#published-reference-results)).
- The case9241pegase `cpu-dense` failure in the July A100 run is a July result that the current build does not show.
- The N-1, `--parallel 28` and "bit-for-bit" statements of `a100-results.md` have no data or export file behind them,
  and the driver-series 535 failure has no log.
- The figures under "Figures with no data file" have no result file in the repository.
- A bare case name other than `case9` (for example `gridsim case14 >/dev/null`) starts the interactive live loop and does
  not return; `case9` prints its solve and exits, which is why the `case9` smoke step of `tools/run-regime.sh` returns.
  Add `--once` to any other bare case in a script.
- The `0xC80000000` heap limit in `tools/run-regime.sh` and `tools/run-gpu.sh` is 50 GiB, although the comment in
  `tools/run-regime.sh` says 48 GiB.
- `tools/gpu-bench.sh` names `docs/developer/gpu-rental-runbook.md` in its error message for a box with no binary; that file is
  now a redirect to this chapter.
- `batchsolve` prints a Greek Delta that a console without UTF-8 shows as `?`.

## See also

- [Technical ch. 4, Sparse and GPU numerics](<../technical/0004 Sparse and GPU numerics.md>) - the solvers, tolerances,
  device kernels and the case for the CPU
- [Technical ch. 3, Power flow](<../technical/0003 Power flow.md>) - the Newton iteration the backends serve
- [Ch. 5, Running power flows](<0005 Running power flows.md>) - the `--gpu` switch in a solve, engine choice
- [Ch. 27, Validation tools](<0027 Validation tools.md>) - `crossval`, `simdcheck`, `batchsolve` and `gpucheck` as
  correctness checks
- [Ch. 19, CLI reference](<0019 CLI reference.md#bench>) - `bench`, [`bench --estimate`](<0019 CLI reference.md#bench---estimate>),
  [`bench --perf`](<0019 CLI reference.md#bench---perf>), [`gpucheck`](<0019 CLI reference.md#gpucheck>),
  [`simdcheck`](<0019 CLI reference.md#simdcheck>), [`batchsolve`](<0019 CLI reference.md#batchsolve>) and
  [global switches](<0019 CLI reference.md#global-switches>)
- [Ch. 11, Contingency analysis](<0011 Contingency analysis.md>) - the sweeps `bench` stands in front of
- `docs/developer/benchmarks/perf-baseline.md`, `docs/developer/benchmarks/perf-after.md`, `docs/developer/benchmarks/bench-estimate.csv`, `docs/developer/benchmarks/benchmark-results.csv` and
  `docs/developer/benchmarks/server-results/` - the data files this chapter quotes
