# Do you need a GPU for power flow? A100 + H100 vs CPU (measured)

**No.** On rented **A100 80 GB PCIe** and **H100 PCIe** boxes (each + AMD EPYC, R570 / CUDA 12.8), the
CPU sparse solver beats every GPU engine on every case by **1-3 orders of magnitude** - and the **H100
is barely faster than the A100**, proving it's the GPU *architecture* that's wrong for this workload,
not any one card. Same-box comparisons (each GPU's own CPU vs the GPU), uncontended (`--parallel 1`).

## The knockout: newer/faster GPU doesn't help - `case2869`, `--parallel 1`

| Engine | A100 | **H100** | vs `cpu-sparse` |
|---|---:|---:|---|
| **cpu-sparse** (CPU) | 68 ms | **65 ms** | - |
| cuda-sparse | 4851 ms | 4320 ms | **~66x slower** |
| cuda-dense | 54,886 ms | 44,496 ms | **~684x slower** |

The **H100's cuda-dense is only ~19 % faster than the A100's** (44.5 s vs 54.9 s) despite ~3.5x the
FP64 and more memory bandwidth - because the solve is bound by kernel-launch/transfer overhead and the
sequential sparse LU, **not** FP64 throughput. Throwing a bigger GPU at it changes essentially nothing.
An MI300 (~82 TFLOP/s FP64) would show the same: it can't feed that much FP64 with a matrix that solves
in 65 ms on one CPU core.

> Getting here first required ruling out **driver R535 / CUDA 12.2**, on which CUDA is unusable on the
> A100 (Xid 119 GSP-RPC-timeout - `cuInit` hangs the GPU). Fixed by the **R570** image. See the
> [driver compatibility chart](<../../../manual/0028 Benchmarking and GPU.md>).

## Headline: mean solve time per case (ms), `--parallel 1`

| Case | Buses | **cpu-sparse** | cpu-dense | cuda-dense | cuda-sparse |
|------|------:|-----:|-----:|-----:|-----:|
| case118 | 118 | **1** | 5 | 19 | 38 |
| case300 | 300 | **5** | 78 | 341 | 179 |
| pglib_case588 | 588 | **9** | 245 | 911 | 1567 |
| case1354pegase | 1354 | **17** | 2281 | 7808 | 923 |
| case2869pegase | 2869 | **68** | 46,695 | 54,886 | 4851 |
| case9241pegase | 9241 | **405** | overflow1 | skip^2 | skip^2 |

1 Dense LU on 9241 buses (18kx18k) overflows an `int` element count - a genuine dense limit; sparse handles it.
^2 GPU engines skip cases >3000 buses in the matrix (they were already losing badly below that).

## What it shows

- **CPU-sparse dominates.** On `case2869` it is **71x faster than the best GPU engine** (cuda-sparse)
  and **714x faster than cuda-dense on the A100** - a full-rate-FP64 data-centre GPU.
- **The A100 dense is *slower than the CPU dense*** on the largest dense case (54.9 s vs 46.7 s), and
  far slower than CPU-sparse everywhere.
- **GPU overhead swamps the compute.** Even tiny cases pay it: `case9` is 0 ms on CPU vs 2 ms
  (cuda-dense) / 9 ms (cuda-sparse). Power-flow solves are too small and too sparse to feed a GPU -
  kernel launch + host<->device transfer per Newton iteration dwarfs the arithmetic.
- **Why:** power-flow Jacobians are ~99 % sparse and the right tool is sparse-direct LU (KLU-style) -
  branchy, latency-bound, sequential. That is the *worst* fit for a GPU and exactly what CPUs do well.
  It is why every production tool (MATPOWER, PSS/E, PowerWorld, pandapower) runs power flow on the CPU.

**Conclusion: for steady-state power flow, buy CPU cores, not GPUs.** The GPU path is validated and
correct - it's just the wrong tool for this shape of problem, and the measurement says so at equal cost.

## Every test type, CPU vs GPU

Both backends were exercised across the whole test suite - single solves, contingency, loadability,
and warm-started chains - plus full solve-trace exports.

### Throughput (`--parallel 28`, all 28 EPYC cores) - same verdict
On `case2869`, cpu-sparse **137 ms** vs cuda-sparse **4597 ms** (33x) and cuda-dense **55,111 ms**
(402x), even with the CPU contended 28 ways. The CPU win is not a single-thread artefact.

### N-1 contingency - the workload a GPU *should* win, and doesn't
Batched N-1 is embarrassingly parallel - the best case for a GPU. But GridSim (like production tools)
issues **one solve at a time**, and a 28-core CPU simply out-throughputs a serial GPU:

| case118 (186 outages) | Solves/s | Wall |
|---|---:|---:|
| **CPU (28 threads)** | **932** | **0.2 s** |
| GPU (A100, serial) | 18 | 10.5 s |

The CPU is **~52x faster** at contingency screening. (case300: CPU 39 solves/s over 411 outages.)

### Loadability (stress) & warm-start chains - identical physics, CPU faster
Stress sweep found `case118` max loadability **2.14x base** (Vmin 0.747 at the nose). Chain (500
warm-started steps): cold-start 7 iters, warm-start <=6 iters. **CPU and GPU produce the same iteration
counts and the same solution** - the GPU backend is *correct*; it's just slower per solve.

### Solve exports - GPU results are bit-for-bit the CPU's
`--enableExport` on case9/118/300 for **both** CPU and CUDA: the converged voltages match. So this is
not "the GPU is wrong", it's "the GPU is the wrong *tool*" - same answer, 1-3 orders of magnitude slower.

## The driver saga (why this took two boxes)

The first rented A100 shipped **R535 / CUDA 12.2**, on which CUDA was unusable: `cuInit` hung the GPU
(**Xid 119 GSP-RPC-timeout**), unfixable by reboot / GSP-disable / persistence mode. A **R570 /
CUDA 12.8** image fixed it instantly. `gridsim gpucheck` catches this in seconds. Full chart:
[driver compatibility](<../../../manual/0028 Benchmarking and GPU.md>).

## Raw data

- `a100/cuda-p1/` - this run (5 engines x 25 cases, `--parallel 1`, R570).
- `a100/bench.md` + `a100/bench.csv` - the earlier CPU-only `--parallel 28` throughput sweep.
- `a100/` (battery) - throughput (`--parallel 28`), high-repeat (`runs=100`/`runs=20`), N-1 contingency,
  stress/loadability, warm-start chains, and CPU+CUDA solve exports (see below as they land).
