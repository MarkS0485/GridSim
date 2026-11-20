    GridSim  -  2026-08-11 21:23:21 UTC
    code:    git unknown  |  GridSim.Core v1.3.0.0  |  build Release
    runtime: .NET 10.0.10  X64  GC=Server
    machine: quick-curie  Ubuntu 24.04.3 LTS
    cpu:     AMD EPYC 7763 64-Core Processor  -  28 logical cores  -  57 GB RAM
    simd:    Avx256  (4 doubles/step)  FMA=True
    backend: cpu
    run:     perf-baseline  runs=40  note="quick-curie under Phase B load"

Focused before/after yardstick for the performance programme. Single-threaded, warmed,
median of N runs. Allocations are managed bytes on the measuring thread.

| Path | Case | Buses | metric | value |
|------|------|------:|--------|------:|
| sparse-nr | case118 | 118 | median_ms | 1.770 |
| sparse-nr | case118 | 118 | alloc_kb | 481.8 |
| sparse-nr | case300 | 300 | median_ms | 8.334 |
| sparse-nr | case300 | 300 | alloc_kb | 1897.6 |
| fault-study | case300 | 300 | median_ms | 8.640 |
| fault-study | case300 | 300 | alloc_kb | 3081.9 |
| oltc-loop | case118 | 118 | median_ms | 8.468 |
| oltc-loop | case118 | 118 | alloc_kb | 1504.3 |
| harmonic-scan | case118 | 118 | median_ms | 8.012 |
| harmonic-scan | case118 | 118 | alloc_kb | 758.9 |

-> docs/developer/benchmarks/perf-baseline.md
