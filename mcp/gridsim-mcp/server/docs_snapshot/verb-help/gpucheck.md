### gpucheck

`gridsim gpucheck`

Fail-fast GPU preflight: reports CPU, ROCm, OpenCL and CUDA availability and runs one tiny case9 solve (dense and
sparse) on each present backend, flushing every step so a crash names the failing call. No flags. Exit 3 when any
probe that ran failed (a `FAIL` line, or a solve that ran but did not converge), listed on stderr; a backend that is not
present is skipped, not failed, so a machine with no GPU exits 0 (`gpucheck: OK - every probe that ran passed`). Each
GPU probe line ends with where its linear solves ran, for example
`[4 linear solves: 4 on the device, 0 on the CPU (0 gave up to the CPU LU, 0 on the host)]`; the CPU probe lines are
unchanged. A solve on a GPU backend (`--gpu`) likewise prints a `GPU:` line after the convergence line, and the run
metadata of a GPU run carries `gpuSolves` (`linearSolves`, `onDevice`, `gaveUpToCpuLu`, `onHost`, `onCpu`,
`ranEntirelyOnCpu`); a run whose every step fell back is the CPU result. See
[ch. 27, Validation tools](<0027 Validation tools.md>) and
[technical ch. 4, GPU accuracy](<../technical/0004 Sparse and GPU numerics.md#the-gpu-backends>).
