### batchsolve

`gridsim batchsolve [--count N] [--n M] [--runs K] [--gpu]`

Solve N same-sized `MxM` dense systems in one batched dispatch on the selected backend (the GPU batched
Gaussian-elimination kernel on a CUDA or OpenCL device, else a parallel CPU loop over the reference LU; ROCm uses the
CPU loop), then verify the result against the CPU reference and report timing. A demonstrator and micro-benchmark for
the batched dense solver seam that turns a contingency sweep of many same-sized outages into a handful of batched
dispatches instead of hundreds of locked single solves. A singular system is isolated (its segment returns `NaN`) and
never fails the whole batch.

| Flag | Default | Meaning |
|---|---|---|
| `--count` | 64 | Number of independent systems in the batch (N); values below 1 are raised to 1. |
| `--n` | 64 | Dimension M of each dense `MxM` system; values below 1 are raised to 1. |
| `--runs` | 5 | Timing repeats (median reported); values below 1 are raised to 1. |
| `--gpu` | off | Select the GPU batched backend (the global `--gpu`); falls back to the CPU batched reference with no device. |

Exit 1 if the batched result differs from the CPU reference by more than 1e-6 or disagrees on which systems are
singular; exit 2 if `count x n x n` exceeds the single-dispatch limit.
