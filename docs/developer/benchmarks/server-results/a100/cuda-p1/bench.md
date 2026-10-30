    GridSim  -  2026-07-02 14:55:10 UTC
    code:    git 8ec809d2-dirty  |  GridSim.Core v0.1.0.0  |  build Release
    runtime: .NET 10.0.9  X64  GC=Server
    machine: keen-darwin  Ubuntu 24.04.3 LTS
    cpu:     AMD EPYC 9554 64-Core Processor  -  28 logical cores  -  118 GB RAM
    backend: gpu/cuda (NVIDIA A100 80GB PCIe [CUDA] - FP64 is fast (data-centre GPU) (driver probe: cuInit=0 driverVer=12080 cuDeviceGetCount rc=0 count=1))
    run:     bench-matrix  cases=25  engines=5  runs=5  parallel=1  note="A100 80GB PCIe R570 + EPYC 28c: parallel=1 (uncontended per-case latency)"

> **Run note:** A100 80GB PCIe R570 + EPYC 28c: parallel=1 (uncontended per-case latency)

Full solver x backend matrix over the whole archive. Every **engine** below is swept across
every case (Q-limits off, tol 1e-8, max 100 iters). Cells: `conv / iters / mismatch / mean ms`.
BFS is radial-only (meshed cases show `n/a`). Dense solves are memory-gated (few concurrent)
and ordered small->large; GPU engines run one case at a time and skip cases >3000 buses
(consumer-GPU FP64 is slow). Absent accelerators are simply not listed as engines.

**Engines this run:** `cpu-dense` - Dense Newton-Raphson, CPU LU; `cpu-sparse` - Sparse Newton-Raphson, CPU (direct LU / ILU0-BiCGSTAB); `bfs` - Backward-forward sweep (radial feeders only); `cuda-dense` - Dense NR, CUDA (gpu/cuda (NVIDIA A100 80GB PCIe [CUDA] - FP64 is fast (data-centre GPU) (driver probe: cuInit=0 driverVer=12080 cuDeviceGetCount rc=0 count=1))); `cuda-sparse` - Sparse NR, CUDA (gpu/cuda (NVIDIA A100 80GB PCIe [CUDA] - FP64 is fast (data-centre GPU) (driver probe: cuInit=0 driverVer=12080 cuDeviceGetCount rc=0 count=1)))
_ROCm/AMD: `--gpu` requested but no device answered._

## cpu-dense - Dense Newton-Raphson, CPU LU
| Case | Buses | Gens | Branches | conv / iters / mismatch / mean ms |
|------|------:|-----:|---------:|-----------------------------------|
| pglib_opf_case3_lmbd | 3 | 3 | 3 | **NO** / 100 / 8.27E+000 / 1ms |
| pglib_opf_case5_pjm | 5 | 5 | 6 | yes / 3 / 3.57E-011 / 0ms |
| case9 (WSCC) | 9 | 3 | 9 | yes / 4 / 1.84E-014 / 0ms |
| case14 | 14 | 5 | 20 | yes / 2 / 1.32E-010 / 0ms |
| cigre_mv | 15 | 1 | 14 | yes / 5 / 2.66E-009 / 1ms |
| case18 | 18 | 1 | 17 | yes / 4 / 5.97E-014 / 1ms |
| case22 | 22 | 1 | 21 | **NO** / 100 / 2.62E+007 / 15ms |
| case24_ieee_rts | 24 | 33 | 38 | yes / 4 / 2.31E-012 / 0ms |
| case30 | 30 | 6 | 41 | yes / 3 / 9.57E-010 / 1ms |
| pglib_opf_case30_as | 30 | 6 | 41 | yes / 4 / 1.97E-014 / 1ms |
| case33bw | 33 | 1 | 32 | **NO** / 100 / 1.35E+003 / 24ms |
| case57 | 57 | 7 | 80 | yes / 3 / 3.45E-012 / 3ms |
| case69 | 69 | 1 | 68 | **NO** / 100 / 3.16E+003 / 99ms |
| pglib_opf_case73_ieee_rts | 73 | 99 | 120 | yes / 5 / 2.40E-014 / 3ms |
| case85 | 85 | 1 | 84 | **NO** / 100 / 1.13E+006 / 142ms |
| case118 | 118 | 54 | 186 | yes / 3 / 1.51E-012 / 5ms |
| case145 | 145 | 50 | 453 | yes / 3 / 7.87E-012 / 10ms |
| pglib_opf_case162_ieee_dtc | 162 | 12 | 284 | **NO** / 100 / 2.21E+004 / 574ms |
| pglib_opf_case240_pserc | 240 | 143 | 448 | **NO** / 100 / 1.35E+004 / 1012ms |
| case300 | 300 | 69 | 411 | yes / 5 / 1.38E-012 / 78ms |
| case533mt_hi | 532 | 1 | 514 | ERR:Singular matrix in LU solve. |
| pglib_opf_case588_sdet | 588 | 95 | 686 | yes / 4 / 1.47E-011 / 245ms |
| case1354pegase | 1354 | 260 | 1991 | yes / 4 / 4.10E-012 / 2281ms (x3) |
| case2869pegase | 2869 | 510 | 4582 | yes / 6 / 2.15E-009 / 46695ms (x3) |
| case9241pegase | 9241 | 1445 | 16049 | ERR:count ('-1973164928') must be a non-negative value. (Parameter 'count') Actual value was -1973164928. |

## cpu-sparse - Sparse Newton-Raphson, CPU (direct LU / ILU0-BiCGSTAB)
| Case | Buses | Gens | Branches | conv / iters / mismatch / mean ms |
|------|------:|-----:|---------:|-----------------------------------|
| pglib_opf_case3_lmbd | 3 | 3 | 3 | **NO** / 100 / 6.67E+000 / 2ms |
| pglib_opf_case5_pjm | 5 | 5 | 6 | yes / 3 / 3.57E-011 / 0ms |
| case9 (WSCC) | 9 | 3 | 9 | yes / 4 / 1.68E-014 / 0ms |
| case14 | 14 | 5 | 20 | yes / 2 / 1.32E-010 / 0ms |
| cigre_mv | 15 | 1 | 14 | yes / 5 / 9.45E-011 / 1ms |
| case18 | 18 | 1 | 17 | yes / 4 / 5.97E-014 / 1ms |
| case22 | 22 | 1 | 21 | **NO** / 100 / 8.21E+001 / 13ms |
| case24_ieee_rts | 24 | 33 | 38 | yes / 4 / 6.52E-012 / 1ms |
| case30 | 30 | 6 | 41 | yes / 3 / 9.57E-010 / 1ms |
| pglib_opf_case30_as | 30 | 6 | 41 | yes / 4 / 8.93E-015 / 1ms |
| case33bw | 33 | 1 | 32 | **NO** / 100 / 6.00E+001 / 12ms |
| case57 | 57 | 7 | 80 | yes / 3 / 3.45E-012 / 2ms |
| case69 | 69 | 1 | 68 | **NO** / 100 / 1.24E+002 / 34ms |
| pglib_opf_case73_ieee_rts | 73 | 99 | 120 | yes / 7 / 2.81E-014 / 2ms |
| case85 | 85 | 1 | 84 | **NO** / 100 / 1.14E+002 / 12ms |
| case118 | 118 | 54 | 186 | yes / 3 / 1.51E-012 / 1ms |
| case145 | 145 | 50 | 453 | yes / 3 / 7.88E-012 / 2ms |
| pglib_opf_case162_ieee_dtc | 162 | 12 | 284 | **NO** / 100 / 3.37E+000 / 77ms |
| pglib_opf_case240_pserc | 240 | 143 | 448 | **NO** / 100 / 1.02E+002 / 105ms |
| case300 | 300 | 69 | 411 | yes / 5 / 1.38E-012 / 5ms |
| case533mt_hi | 532 | 1 | 514 | **NO** / 0 / 2.76E-002 / 202ms |
| pglib_opf_case588_sdet | 588 | 95 | 686 | yes / 5 / 7.27E-010 / 9ms |
| case1354pegase | 1354 | 260 | 1991 | yes / 4 / 1.45E-012 / 17ms (x3) |
| case2869pegase | 2869 | 510 | 4582 | yes / 6 / 2.15E-009 / 68ms (x3) |
| case9241pegase | 9241 | 1445 | 16049 | yes / 6 / 2.13E-009 / 405ms (x1) |

## bfs - Backward-forward sweep (radial feeders only)
| Case | Buses | Gens | Branches | conv / iters / mismatch / mean ms |
|------|------:|-----:|---------:|-----------------------------------|
| pglib_opf_case3_lmbd | 3 | 3 | 3 | n/a (meshed) |
| pglib_opf_case5_pjm | 5 | 5 | 6 | n/a (meshed) |
| case9 (WSCC) | 9 | 3 | 9 | n/a (meshed) |
| case14 | 14 | 5 | 20 | n/a (meshed) |
| cigre_mv | 15 | 1 | 14 | yes / 11 / 6.15E-010 / 1ms |
| case18 | 18 | 1 | 17 | yes / 12 / 1.11E-009 / 0ms |
| case22 | 22 | 1 | 21 | **NO** / 100 / 9.96E+002 / 2ms |
| case24_ieee_rts | 24 | 33 | 38 | n/a (meshed) |
| case30 | 30 | 6 | 41 | n/a (meshed) |
| pglib_opf_case30_as | 30 | 6 | 41 | n/a (meshed) |
| case33bw | 33 | 1 | 32 | **NO** / 100 / 1.01E+002 / 2ms |
| case57 | 57 | 7 | 80 | n/a (meshed) |
| case69 | 69 | 1 | 68 | **NO** / 100 / 8.51E+002 / 1ms |
| pglib_opf_case73_ieee_rts | 73 | 99 | 120 | n/a (meshed) |
| case85 | 85 | 1 | 84 | **NO** / 100 / 2.37E+003 / 1ms |
| case118 | 118 | 54 | 186 | n/a (meshed) |
| case145 | 145 | 50 | 453 | n/a (meshed) |
| pglib_opf_case162_ieee_dtc | 162 | 12 | 284 | n/a (meshed) |
| pglib_opf_case240_pserc | 240 | 143 | 448 | n/a (meshed) |
| case300 | 300 | 69 | 411 | n/a (meshed) |
| case533mt_hi | 532 | 1 | 514 | n/a (meshed) |
| pglib_opf_case588_sdet | 588 | 95 | 686 | n/a (meshed) |
| case1354pegase | 1354 | 260 | 1991 | n/a (meshed) |
| case2869pegase | 2869 | 510 | 4582 | n/a (meshed) |
| case9241pegase | 9241 | 1445 | 16049 | n/a (meshed) |

## cuda-dense - Dense NR, CUDA (gpu/cuda (NVIDIA A100 80GB PCIe [CUDA] - FP64 is fast (data-centre GPU) (driver probe: cuInit=0 driverVer=12080 cuDeviceGetCount rc=0 count=1)))
| Case | Buses | Gens | Branches | conv / iters / mismatch / mean ms |
|------|------:|-----:|---------:|-----------------------------------|
| pglib_opf_case3_lmbd | 3 | 3 | 3 | **NO** / 100 / 6.71E+000 / 201ms (x3) |
| pglib_opf_case5_pjm | 5 | 5 | 6 | yes / 3 / 3.57E-011 / 1ms (x3) |
| case9 (WSCC) | 9 | 3 | 9 | yes / 4 / 1.84E-014 / 2ms (x3) |
| case14 | 14 | 5 | 20 | yes / 2 / 1.32E-010 / 1ms (x3) |
| cigre_mv | 15 | 1 | 14 | yes / 5 / 2.66E-009 / 4ms (x3) |
| case18 | 18 | 1 | 17 | yes / 4 / 5.27E-014 / 11ms (x3) |
| case22 | 22 | 1 | 21 | **NO** / 100 / 1.94E+006 / 112ms (x3) |
| case24_ieee_rts | 24 | 33 | 38 | yes / 4 / 2.31E-012 / 4ms (x3) |
| case30 | 30 | 6 | 41 | yes / 3 / 9.57E-010 / 4ms (x3) |
| pglib_opf_case30_as | 30 | 6 | 41 | yes / 4 / 7.32E-015 / 5ms (x3) |
| case33bw | 33 | 1 | 32 | **NO** / 100 / 9.12E+002 / 189ms (x3) |
| case57 | 57 | 7 | 80 | yes / 3 / 3.45E-012 / 10ms (x3) |
| case69 | 69 | 1 | 68 | **NO** / 100 / 9.38E+002 / 493ms (x3) |
| pglib_opf_case73_ieee_rts | 73 | 99 | 120 | yes / 5 / 2.09E-014 / 17ms (x3) |
| case85 | 85 | 1 | 84 | **NO** / 100 / 2.66E+005 / 668ms (x3) |
| case118 | 118 | 54 | 186 | yes / 3 / 1.51E-012 / 19ms (x3) |
| case145 | 145 | 50 | 453 | yes / 3 / 7.87E-012 / 37ms (x3) |
| pglib_opf_case162_ieee_dtc | 162 | 12 | 284 | **NO** / 100 / 7.66E+003 / 1993ms (x3) |
| pglib_opf_case240_pserc | 240 | 143 | 448 | **NO** / 100 / 4.26E+005 / 4144ms (x3) |
| case300 | 300 | 69 | 411 | yes / 5 / 1.38E-012 / 341ms (x3) |
| case533mt_hi | 532 | 1 | 514 | ERR:Singular matrix in GPU LU solve. |
| pglib_opf_case588_sdet | 588 | 95 | 686 | yes / 4 / 1.47E-011 / 911ms (x3) |
| case1354pegase | 1354 | 260 | 1991 | yes / 4 / 1.87E-012 / 7808ms (x3) |
| case2869pegase | 2869 | 510 | 4582 | yes / 6 / 2.15E-009 / 54886ms (x3) |
| case9241pegase | 9241 | 1445 | 16049 | n/a (>3000 buses, skipped) |

## cuda-sparse - Sparse NR, CUDA (gpu/cuda (NVIDIA A100 80GB PCIe [CUDA] - FP64 is fast (data-centre GPU) (driver probe: cuInit=0 driverVer=12080 cuDeviceGetCount rc=0 count=1)))
| Case | Buses | Gens | Branches | conv / iters / mismatch / mean ms |
|------|------:|-----:|---------:|-----------------------------------|
| pglib_opf_case3_lmbd | 3 | 3 | 3 | **NO** / 100 / 6.67E+000 / 91ms (x3) |
| pglib_opf_case5_pjm | 5 | 5 | 6 | yes / 3 / 3.57E-011 / 3ms (x3) |
| case9 (WSCC) | 9 | 3 | 9 | yes / 4 / 1.84E-014 / 9ms (x3) |
| case14 | 14 | 5 | 20 | yes / 2 / 1.32E-010 / 6ms (x3) |
| cigre_mv | 15 | 1 | 14 | yes / 5 / 9.45E-011 / 32ms (x3) |
| case18 | 18 | 1 | 17 | yes / 4 / 1.19E-013 / 31ms (x3) |
| case22 | 22 | 1 | 21 | **NO** / 100 / 8.21E+001 / 3736ms (x3) |
| case24_ieee_rts | 24 | 33 | 38 | yes / 4 / 6.52E-012 / 16ms (x3) |
| case30 | 30 | 6 | 41 | yes / 3 / 9.57E-010 / 20ms (x3) |
| pglib_opf_case30_as | 30 | 6 | 41 | yes / 4 / 1.61E-014 / 26ms (x3) |
| case33bw | 33 | 1 | 32 | **NO** / 100 / 6.00E+001 / 3589ms (x3) |
| case57 | 57 | 7 | 80 | yes / 3 / 3.45E-012 / 65ms (x3) |
| case69 | 69 | 1 | 68 | **NO** / 100 / 1.24E+002 / 8701ms (x3) |
| pglib_opf_case73_ieee_rts | 73 | 99 | 120 | yes / 7 / 2.84E-014 / 58ms (x3) |
| case85 | 85 | 1 | 84 | **NO** / 100 / 1.14E+002 / 7707ms (x3) |
| case118 | 118 | 54 | 186 | yes / 3 / 1.50E-012 / 38ms (x3) |
| case145 | 145 | 50 | 453 | yes / 3 / 7.87E-012 / 45ms (x3) |
| pglib_opf_case162_ieee_dtc | 162 | 12 | 284 | **NO** / 100 / 3.37E+000 / 1761ms (x3) |
| pglib_opf_case240_pserc | 240 | 143 | 448 | **NO** / 100 / 9.07E+001 / 13202ms (x3) |
| case300 | 300 | 69 | 411 | yes / 5 / 1.38E-012 / 179ms (x3) |
| case533mt_hi | 532 | 1 | 514 | **NO** / 0 / 2.76E-002 / 178ms (x3) |
| pglib_opf_case588_sdet | 588 | 95 | 686 | yes / 5 / 7.27E-010 / 1567ms (x3) |
| case1354pegase | 1354 | 260 | 1991 | yes / 4 / 3.73E-012 / 923ms (x3) |
| case2869pegase | 2869 | 510 | 4582 | yes / 6 / 2.15E-009 / 4851ms (x3) |
| case9241pegase | 9241 | 1445 | 16049 | n/a (>3000 buses, skipped) |

## Comparison - mean solve time (ms), converged unless noted
| Case | Buses | cpu-dense | cpu-sparse | bfs | cuda-dense | cuda-sparse |
|------|------:|------:|------:|------:|------:|------:|
| pglib_opf_case3_lmbd | 3 | NO | NO | n/a | NO | NO |
| pglib_opf_case5_pjm | 5 | 0ms | 0ms | n/a | 1ms | 3ms |
| case9 (WSCC) | 9 | 0ms | 0ms | n/a | 2ms | 9ms |
| case14 | 14 | 0ms | 0ms | n/a | 1ms | 6ms |
| cigre_mv | 15 | 1ms | 1ms | 1ms | 4ms | 32ms |
| case18 | 18 | 1ms | 1ms | 0ms | 11ms | 31ms |
| case22 | 22 | NO | NO | NO | NO | NO |
| case24_ieee_rts | 24 | 0ms | 1ms | n/a | 4ms | 16ms |
| case30 | 30 | 1ms | 1ms | n/a | 4ms | 20ms |
| pglib_opf_case30_as | 30 | 1ms | 1ms | n/a | 5ms | 26ms |
| case33bw | 33 | NO | NO | NO | NO | NO |
| case57 | 57 | 3ms | 2ms | n/a | 10ms | 65ms |
| case69 | 69 | NO | NO | NO | NO | NO |
| pglib_opf_case73_ieee_rts | 73 | 3ms | 2ms | n/a | 17ms | 58ms |
| case85 | 85 | NO | NO | NO | NO | NO |
| case118 | 118 | 5ms | 1ms | n/a | 19ms | 38ms |
| case145 | 145 | 10ms | 2ms | n/a | 37ms | 45ms |
| pglib_opf_case162_ieee_dtc | 162 | NO | NO | n/a | NO | NO |
| pglib_opf_case240_pserc | 240 | NO | NO | n/a | NO | NO |
| case300 | 300 | 78ms | 5ms | n/a | 341ms | 179ms |
| case533mt_hi | 532 | ERR:Singular matrix in LU solve. | NO | n/a | ERR:Singular matrix in GPU LU solve. | NO |
| pglib_opf_case588_sdet | 588 | 245ms | 9ms | n/a | 911ms | 1567ms |
| case1354pegase | 1354 | 2281ms | 17ms | n/a | 7808ms | 923ms |
| case2869pegase | 2869 | 46695ms | 68ms | n/a | 54886ms | 4851ms |
| case9241pegase | 9241 | ERR:count ('-1973164928') must be a non-negative value. (Parameter 'count') Actual value was -1973164928. | 405ms | n/a | skip | skip |
