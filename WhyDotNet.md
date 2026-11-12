# Why .NET

GridSim is written in C# on .NET 10. This page records the reasons, and what they do and do not claim.

## Performance is adequate for the work

GridSim's workload is offline throughput, not a hard real-time loop: nothing breaks if a runtime pause happens during a
solve, and results do not depend on timing. The measured speed of the solvers is in the README
([Performance and GPU](README.md#performance-and-gpu)). The sparse engine solves the 9,241-bus PEGASE case in 1,178 ms.
GridSim does not claim that .NET is the fastest possible choice; it claims the speed is enough for the job.

## Hot paths do not allocate

The sparse core uses flat arrays and `Span<T>` slices, and allocates its workspaces once per factorisation. The
pattern is set out in [PerformanceOpts.md](PerformanceOpts.md). The garbage collector manages the object-graph work
around the maths (models, ingest, results), not the inner kernels.

## Determinism is controlled in code

Bit-identical results need control of the arithmetic, not the runtime alone. GridSim uses IEEE 754 double arithmetic,
vector kernels with a fixed reduction order, its own elementary-function library (`DetMath`) and no fused
multiply-add. See [MATH.md](MATH.md).

## One engine, several front ends

The engine sits behind a solver seam that dispatches to CPU or GPU backends, and the same C# engine serves the CLI, the
web app, the desktop apps and the data pipeline. One language for all of them keeps the engine's invariants in one
place.

## The platform

.NET is a mature, widely deployed runtime. The release builds target Windows and Linux x64 (see [RC1.md](RC1.md)); CI
also runs the test suite on macOS and on ARM64 runners.
