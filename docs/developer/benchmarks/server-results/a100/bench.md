    GridSim  -  2026-07-02 14:17:34 UTC
    code:    git 8ec809d2-dirty  |  GridSim.Core v0.1.0.0  |  build Release
    runtime: .NET 10.0.9  X64  GC=Server
    machine: brave-heisenberg  Ubuntu 22.04.5 LTS
    cpu:     AMD EPYC 9554 64-Core Processor  -  28 logical cores  -  118 GB RAM
    backend: cpu
    run:     bench-matrix  cases=25  engines=3  runs=5  parallel=28  note="A100 box: EPYC 28c/118GB, CPU parallel=28"

> **Run note:** A100 box: EPYC 28c/118GB, CPU parallel=28

Full solver x backend matrix over the whole archive. Every **engine** below is swept across
every case (Q-limits off, tol 1e-8, max 100 iters). Cells: `conv / iters / mismatch / mean ms`.
BFS is radial-only (meshed cases show `n/a`). Dense solves are memory-gated (few concurrent)
and ordered small->large; GPU engines run one case at a time and skip cases >3000 buses
(consumer-GPU FP64 is slow). Absent accelerators are simply not listed as engines.

**Engines this run:** `cpu-dense` - Dense Newton-Raphson, CPU LU; `cpu-sparse` - Sparse Newton-Raphson, CPU (direct LU / ILU0-BiCGSTAB); `bfs` - Backward-forward sweep (radial feeders only)

_GPU engines not requested (pass `--gpu` to add CUDA/ROCm when a device is present)._

## cpu-dense - Dense Newton-Raphson, CPU LU
| Case | Buses | Gens | Branches | conv / iters / mismatch / mean ms |
|------|------:|-----:|---------:|-----------------------------------|
| case9 (WSCC) | 9 | 3 | 9 | yes / 4 / 1.84E-014 / 7ms |
| pglib_opf_case5_pjm | 5 | 5 | 6 | yes / 3 / 3.57E-011 / 1ms |
| pglib_opf_case3_lmbd | 3 | 3 | 3 | **NO** / 100 / 8.27E+000 / 2ms |
| case14 | 14 | 5 | 20 | yes / 2 / 1.32E-010 / 2ms |
| case30 | 30 | 6 | 41 | yes / 3 / 9.57E-010 / 4ms |
| case18 | 18 | 1 | 17 | yes / 4 / 5.97E-014 / 4ms |
| pglib_opf_case30_as | 30 | 6 | 41 | yes / 4 / 1.97E-014 / 4ms |
| cigre_mv | 15 | 1 | 14 | yes / 5 / 2.66E-009 / 5ms |
| case24_ieee_rts | 24 | 33 | 38 | yes / 4 / 2.31E-012 / 7ms |
| case57 | 57 | 7 | 80 | yes / 3 / 3.45E-012 / 10ms |
| pglib_opf_case73_ieee_rts | 73 | 99 | 120 | yes / 5 / 2.40E-014 / 12ms |
| case118 | 118 | 54 | 186 | yes / 3 / 1.51E-012 / 19ms |
| case533mt_hi | 532 | 1 | 514 | ERR:Singular matrix in LU solve. |
| case145 | 145 | 50 | 453 | yes / 3 / 7.87E-012 / 28ms |
| case22 | 22 | 1 | 21 | **NO** / 100 / 2.62E+007 / 36ms |
| case33bw | 33 | 1 | 32 | **NO** / 100 / 1.35E+003 / 59ms |
| case300 | 300 | 69 | 411 | yes / 5 / 1.38E-012 / 143ms |
| case69 | 69 | 1 | 68 | **NO** / 100 / 3.16E+003 / 169ms |
| case85 | 85 | 1 | 84 | **NO** / 100 / 1.13E+006 / 195ms |
| pglib_opf_case588_sdet | 588 | 95 | 686 | yes / 4 / 1.47E-011 / 325ms |
| pglib_opf_case162_ieee_dtc | 162 | 12 | 284 | **NO** / 100 / 2.21E+004 / 652ms |
| pglib_opf_case240_pserc | 240 | 143 | 448 | **NO** / 100 / 1.35E+004 / 1124ms |
| case1354pegase | 1354 | 260 | 1991 | yes / 4 / 4.10E-012 / 2290ms (x3) |
| case9241pegase | 9241 | 1445 | 16049 | ERR:count ('-1973164928') must be a non-negative value. (Parameter 'count') Actual value was -1973164928. |
| case2869pegase | 2869 | 510 | 4582 | yes / 6 / 2.15E-009 / 34676ms (x3) |

## cpu-sparse - Sparse Newton-Raphson, CPU (direct LU / ILU0-BiCGSTAB)
| Case | Buses | Gens | Branches | conv / iters / mismatch / mean ms |
|------|------:|-----:|---------:|-----------------------------------|
| pglib_opf_case5_pjm | 5 | 5 | 6 | yes / 3 / 3.57E-011 / 4ms |
| case9 (WSCC) | 9 | 3 | 9 | yes / 4 / 1.68E-014 / 5ms |
| case14 | 14 | 5 | 20 | yes / 2 / 1.32E-010 / 5ms |
| pglib_opf_case3_lmbd | 3 | 3 | 3 | **NO** / 100 / 6.67E+000 / 5ms |
| case18 | 18 | 1 | 17 | yes / 4 / 5.97E-014 / 5ms |
| cigre_mv | 15 | 1 | 14 | yes / 5 / 9.45E-011 / 6ms |
| case24_ieee_rts | 24 | 33 | 38 | yes / 4 / 6.52E-012 / 12ms |
| case30 | 30 | 6 | 41 | yes / 3 / 9.57E-010 / 12ms |
| pglib_opf_case30_as | 30 | 6 | 41 | yes / 4 / 8.93E-015 / 13ms |
| case57 | 57 | 7 | 80 | yes / 3 / 3.45E-012 / 13ms |
| case118 | 118 | 54 | 186 | yes / 3 / 1.51E-012 / 13ms |
| pglib_opf_case73_ieee_rts | 73 | 99 | 120 | yes / 7 / 2.81E-014 / 17ms |
| case145 | 145 | 50 | 453 | yes / 3 / 7.88E-012 / 15ms |
| case300 | 300 | 69 | 411 | yes / 5 / 1.38E-012 / 19ms |
| case22 | 22 | 1 | 21 | **NO** / 100 / 8.21E+001 / 41ms |
| case33bw | 33 | 1 | 32 | **NO** / 100 / 6.00E+001 / 46ms |
| pglib_opf_case588_sdet | 588 | 95 | 686 | yes / 5 / 7.27E-010 / 32ms |
| case1354pegase | 1354 | 260 | 1991 | yes / 4 / 1.45E-012 / 55ms (x3) |
| case69 | 69 | 1 | 68 | **NO** / 100 / 1.24E+002 / 65ms |
| case85 | 85 | 1 | 84 | **NO** / 100 / 1.14E+002 / 70ms |
| case2869pegase | 2869 | 510 | 4582 | yes / 6 / 2.15E-009 / 135ms (x3) |
| pglib_opf_case240_pserc | 240 | 143 | 448 | **NO** / 100 / 1.02E+002 / 156ms |
| pglib_opf_case162_ieee_dtc | 162 | 12 | 284 | **NO** / 100 / 3.37E+000 / 170ms |
| case9241pegase | 9241 | 1445 | 16049 | yes / 6 / 2.13E-009 / 659ms (x1) |
| case533mt_hi | 532 | 1 | 514 | **NO** / 0 / 2.76E-002 / 274ms |

## bfs - Backward-forward sweep (radial feeders only)
| Case | Buses | Gens | Branches | conv / iters / mismatch / mean ms |
|------|------:|-----:|---------:|-----------------------------------|
| case9 (WSCC) | 9 | 3 | 9 | n/a (meshed) |
| pglib_opf_case3_lmbd | 3 | 3 | 3 | n/a (meshed) |
| case14 | 14 | 5 | 20 | n/a (meshed) |
| pglib_opf_case5_pjm | 5 | 5 | 6 | n/a (meshed) |
| case30 | 30 | 6 | 41 | n/a (meshed) |
| pglib_opf_case30_as | 30 | 6 | 41 | n/a (meshed) |
| case24_ieee_rts | 24 | 33 | 38 | n/a (meshed) |
| case57 | 57 | 7 | 80 | n/a (meshed) |
| cigre_mv | 15 | 1 | 14 | yes / 11 / 6.15E-010 / 7ms |
| case18 | 18 | 1 | 17 | yes / 12 / 1.11E-009 / 7ms |
| case33bw | 33 | 1 | 32 | **NO** / 100 / 1.01E+002 / 5ms |
| case85 | 85 | 1 | 84 | **NO** / 100 / 2.37E+003 / 2ms |
| pglib_opf_case73_ieee_rts | 73 | 99 | 120 | n/a (meshed) |
| case118 | 118 | 54 | 186 | n/a (meshed) |
| case22 | 22 | 1 | 21 | **NO** / 100 / 9.96E+002 / 11ms |
| pglib_opf_case162_ieee_dtc | 162 | 12 | 284 | n/a (meshed) |
| case69 | 69 | 1 | 68 | **NO** / 100 / 8.51E+002 / 9ms |
| case145 | 145 | 50 | 453 | n/a (meshed) |
| case300 | 300 | 69 | 411 | n/a (meshed) |
| pglib_opf_case240_pserc | 240 | 143 | 448 | n/a (meshed) |
| case533mt_hi | 532 | 1 | 514 | n/a (meshed) |
| pglib_opf_case588_sdet | 588 | 95 | 686 | n/a (meshed) |
| case1354pegase | 1354 | 260 | 1991 | n/a (meshed) |
| case2869pegase | 2869 | 510 | 4582 | n/a (meshed) |
| case9241pegase | 9241 | 1445 | 16049 | n/a (meshed) |

## Comparison - mean solve time (ms), converged unless noted
| Case | Buses | cpu-dense | cpu-sparse | bfs |
|------|------:|------:|------:|------:|
| pglib_opf_case3_lmbd | 3 | NO | NO | n/a |
| pglib_opf_case5_pjm | 5 | 1ms | 4ms | n/a |
| case9 (WSCC) | 9 | 7ms | 5ms | n/a |
| case14 | 14 | 2ms | 5ms | n/a |
| cigre_mv | 15 | 5ms | 6ms | 7ms |
| case18 | 18 | 4ms | 5ms | 7ms |
| case22 | 22 | NO | NO | NO |
| case24_ieee_rts | 24 | 7ms | 12ms | n/a |
| case30 | 30 | 4ms | 12ms | n/a |
| pglib_opf_case30_as | 30 | 4ms | 13ms | n/a |
| case33bw | 33 | NO | NO | NO |
| case57 | 57 | 10ms | 13ms | n/a |
| case69 | 69 | NO | NO | NO |
| pglib_opf_case73_ieee_rts | 73 | 12ms | 17ms | n/a |
| case85 | 85 | NO | NO | NO |
| case118 | 118 | 19ms | 13ms | n/a |
| case145 | 145 | 28ms | 15ms | n/a |
| pglib_opf_case162_ieee_dtc | 162 | NO | NO | n/a |
| pglib_opf_case240_pserc | 240 | NO | NO | n/a |
| case300 | 300 | 143ms | 19ms | n/a |
| case533mt_hi | 532 | ERR:Singular matrix in LU solve. | NO | n/a |
| pglib_opf_case588_sdet | 588 | 325ms | 32ms | n/a |
| case1354pegase | 1354 | 2290ms | 55ms | n/a |
| case2869pegase | 2869 | 34676ms | 135ms | n/a |
| case9241pegase | 9241 | ERR:count ('-1973164928') must be a non-negative value. (Parameter 'count') Actual value was -1973164928. | 659ms | n/a |
