    GridSim  -  2026-07-19 18:33:39 UTC
    code:    git e0709279-dirty  |  GridSim.Core v0.1.0.0  |  build Debug
    runtime: .NET 10.0.10  X64  GC=Server
    machine: DESKTOP-S5V62MR  Microsoft Windows 10.0.26200
    cpu:     X64  -  8 logical cores  -  8 GB RAM
    simd:    Avx256  (4 doubles/step)  FMA=True
    backend: cpu
    run:     perf-baseline  runs=20  note="after full PERF programme (waves 0-6)"

Focused before/after yardstick for the performance programme. Single-threaded, warmed,
median of N runs. Allocations are managed bytes on the measuring thread.

| Path | Case | Buses | metric | value |
|------|------|------:|--------|------:|
| sparse-nr | case118 | 118 | median_ms | 1.388 |
| sparse-nr | case118 | 118 | alloc_kb | 479.9 |
| sparse-nr | case300 | 300 | median_ms | 8.150 |
| sparse-nr | case300 | 300 | alloc_kb | 1892.9 |
| fault-study | case300 | 300 | median_ms | 26.570 |
| fault-study | case300 | 300 | alloc_kb | 3081.9 |
| oltc-loop | case118 | 118 | median_ms | 13.950 |
| oltc-loop | case118 | 118 | alloc_kb | 1503.0 |
| harmonic-scan | case118 | 118 | median_ms | 6.293 |
| harmonic-scan | case118 | 118 | alloc_kb | 870.7 |

-> docs/developer/benchmarks/perf-after.md
