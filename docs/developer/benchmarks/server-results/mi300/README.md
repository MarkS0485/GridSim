# AMD Instinct MI300X / MI250 - results (unpopulated: awaiting hardware)

This tree is the AMD counterpart to `../a100/` and `../h100/` and is **deliberately empty of
measurements** - GridSim's native ROCm/HIP backend has never run on AMD hardware yet. The code is
complete and matched against the ROCm 6.x headers (see `src/GridSim.Gpu/RocmInterop.cs`,
`RocmContext.cs`, `RocmDenseLinearSolver.cs`, `RocmSparseLinearSolver.cs`), and is exercised
offline via the hardware-free seam + interop-shape tests (`tests/GridSim.Tests/RocmSolverSeamTests.cs`,
`RocmInteropShapeTests.cs`); it awaits its first *runtime* run on an MI-series card.

## How to populate it

Follow [`../../gpu-rental-runbook.md`](../../../gpu-rental-runbook.md) on a rented MI250 (ROCm 5.x) or
MI300X (ROCm 6.0+) box:

```bash
# inside the ROCm container on the MI box, from the published tarball:
./gridsim gpucheck                       # run the AMD first-contact checklist first
./gpu-bench.sh "MI300X 192GB - \$X.XX/hr <provider>"
```

Then drop the produced `preflight.txt`, `bench.md`, `bench.csv`, `run-metadata.json` and the
solve-export JSONs here (mirroring `../a100/`), and add an `mi300-results.md` summary alongside
`../a100-results.md` pairing the MI300X against the H100 and the MI250 against the A100 at equal
$/hr - the comparison the runbook is built to make.

## What to expect

Per the runbook's thesis and the measured NVIDIA verdict, `cpu-sparse` is expected to win here too:
even an MI300's ~82 TFLOP/s FP64 can't beat a sparse-direct LU that solves in tens of milliseconds
on one CPU core. The value of this tree is to **measure** that at equal cost with a real Instinct
FP64 rate, and to confirm the native hipSOLVER/rocSPARSE/rocBLAS path is bit-for-bit correct against
the CPU reference (`--enableExport` voltages match, as they do for CUDA).
