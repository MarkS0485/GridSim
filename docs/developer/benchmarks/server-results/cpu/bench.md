    GridSim  -  2026-07-01 10:18:36 UTC
    code:    git c41ef951-dirty  |  GridSim.Core v0.1.0.0  |  build Release
    runtime: .NET 10.0.9  X64  GC=Server
    machine: curious-bohr  Ubuntu 22.04.5 LTS
    cpu:     AMD EPYC 7763 64-Core Processor  -  28 logical cores  -  57 GB RAM
    run:     bench  cases=153  runs=10  parallel=28

Two sweeps over the whole archive. **Sparse** (ILU0/BiCGSTAB) runs every case at full
parallelism. **Dense** (the reference) is then attempted on every case too - no case is
pre-judged: a case only shows `OOM` if the allocation actually fails. Large dense solves are
memory-gated (few concurrent) and ordered small->large; deterministic iterations mean dense
uses fewer timing repeats on big cases. Q-limits off, tol 1e-8, max 100 iters.

## Sparse sweep (all cases)
| Case | Buses | Gens | Branches | conv / iters / mismatch / mean ms |
|------|------:|-----:|---------:|-----------------------------------|
| case4gs | 4 | 2 | 4 | yes / 3 / 1.07E-009 / 6ms |
| case5 | 5 | 5 | 6 | yes / 3 / 6.42E-011 / 1ms |
| pglib_opf_case5_pjm | 5 | 5 | 6 | yes / 3 / 3.57E-011 / 2ms |
| pglib_opf_case5_pjm | 5 | 5 | 6 | yes / 3 / 3.57E-011 / 1ms |
| case9 (WSCC) | 9 | 3 | 9 | yes / 4 / 1.69E-014 / 14ms |
| case6ww | 6 | 3 | 11 | yes / 3 / 2.09E-010 / 3ms |
| case9Q | 9 | 3 | 9 | yes / 4 / 5.55E-014 / 8ms |
| cigre_mv | 15 | 1 | 14 | yes / 5 / 9.45E-011 / 21ms |
| case9target | 9 | 3 | 9 | yes / 6 / 4.22E-014 / 12ms |
| case14 | 14 | 5 | 20 | yes / 2 / 1.32E-010 / 7ms |
| pglib_opf_case30_as | 30 | 6 | 41 | yes / 4 / 8.93E-015 / 42ms |
| pglib_opf_case14_ieee | 14 | 5 | 20 | yes / 4 / 6.73E-015 / 13ms |
| case118 | 118 | 54 | 186 | yes / 3 / 1.51E-012 / 61ms |
| case_RTS_GMLC | 73 | 96 | 120 | yes / 4 / 4.62E-014 / 68ms |
| pglib_opf_case30_as | 30 | 6 | 41 | yes / 4 / 8.93E-015 / 25ms |
| pglib_opf_case30_ieee | 30 | 6 | 41 | yes / 4 / 1.06E-014 / 11ms |
| case_ACTIVSg200 | 200 | 38 | 245 | yes / 2 / 3.19E-013 / 80ms |
| case145 | 145 | 50 | 453 | yes / 3 / 7.85E-012 / 84ms |
| case12da | 12 | 1 | 11 | **NO** / 100 / 6.00E+001 / 99ms |
| case17me | 17 | 1 | 16 | yes / 4 / 4.44E-015 / 3ms |
| case18 | 18 | 1 | 17 | yes / 4 / 5.97E-014 / 3ms |
| case16am | 15 | 1 | 14 | **NO** / 100 / 5.00E+002 / 90ms |
| case18nbr | 18 | 1 | 17 | **NO** / 100 / 1.43E+000 / 117ms |
| case15da | 15 | 1 | 14 | **NO** / 100 / 1.43E+002 / 64ms |
| pglib_opf_case200_activ | 200 | 38 | 245 | yes / 4 / 1.11E-013 / 29ms |
| case_ieee30 | 30 | 6 | 41 | yes / 2 / 3.47E-009 / 5ms |
| case30 | 30 | 6 | 41 | yes / 3 / 9.57E-010 / 2ms |
| case30pwl | 30 | 6 | 41 | yes / 3 / 9.57E-010 / 4ms |
| case30Q | 30 | 6 | 41 | yes / 3 / 9.57E-010 / 2ms |
| case28da | 28 | 1 | 27 | **NO** / 100 / 5.71E+001 / 139ms |
| case57 | 57 | 7 | 80 | yes / 3 / 3.45E-012 / 6ms |
| case34sa | 34 | 1 | 33 | **NO** / 100 / 2.30E+002 / 147ms |
| case16ci | 16 | 3 | 13 | **NO** / 100 / 4.99E+002 / 39ms |
| pglib_opf_case57_ieee | 57 | 7 | 80 | yes / 4 / 1.80E-012 / 6ms |
| case60nordic | 60 | 23 | 88 | yes / 1 / 3.10E-009 / 8ms |
| case15nbr | 15 | 1 | 14 | **NO** / 100 / 1.44E+000 / 46ms |
| case33bw | 33 | 1 | 32 | **NO** / 100 / 6.00E+001 / 84ms |
| pglib_opf_case60_c | 60 | 23 | 88 | yes / 5 / 3.97E-011 / 8ms |
| case22 | 22 | 1 | 21 | **NO** / 100 / 8.21E+001 / 60ms |
| case24_ieee_rts | 24 | 33 | 38 | yes / 4 / 6.52E-012 / 3ms |
| case51he | 51 | 1 | 50 | **NO** / 100 / 1.49E+002 / 169ms |
| pglib_opf_case24_ieee_rts | 24 | 33 | 38 | yes / 4 / 5.27E-010 / 1ms |
| pglib_opf_case588_sdet | 588 | 95 | 686 | yes / 5 / 7.27E-010 / 174ms |
| case69 | 69 | 1 | 68 | **NO** / 100 / 1.24E+002 / 186ms |
| case74ds | 74 | 1 | 73 | **NO** / 100 / 4.18E+002 / 196ms |
| case533mt_hi | - | - | - | LOAD FAIL: The input string '135/sqrt(3)' was not in a correct format. |
| case533mt_lo | - | - | - | LOAD FAIL: The input string '135/sqrt(3)' was not in a correct format. |
| case38si | 38 | 1 | 37 | **NO** / 100 / 6.00E+002 / 75ms |
| case39 | 39 | 10 | 46 | yes / 1 / 8.32E-013 / 0ms |
| case33mg | 33 | 1 | 32 | **NO** / 100 / 6.00E+002 / 62ms |
| case_ACTIVSg500 | 500 | 56 | 597 | yes / 3 / 2.65E-011 / 55ms |
| case118zh | 118 | 1 | 117 | **NO** / 100 / 1.21E+002 / 203ms |
| pglib_opf_case39_epri | 39 | 10 | 46 | **NO** / 100 / 1.79E+000 / 65ms |
| pglib_opf_case118_ieee | 118 | 54 | 186 | yes / 5 / 1.97E-009 / 5ms |
| case85 | 85 | 1 | 84 | **NO** / 100 / 1.14E+002 / 126ms |
| pglib_opf_case588_sdet | 588 | 95 | 686 | yes / 5 / 7.27E-010 / 97ms |
| case89pegase | 89 | 12 | 210 | yes / 5 / 3.90E-011 / 19ms |
| pglib_opf_case89_pegase | 89 | 12 | 210 | yes / 5 / 2.34E-012 / 18ms |
| case51ga | 51 | 1 | 50 | **NO** / 100 / 1.20E+002 / 53ms |
| case136ma | 136 | 1 | 135 | **NO** / 100 / 1.51E+002 / 166ms |
| pglib_opf_case1354_pegase | 1354 | 260 | 1991 | yes / 6 / 2.67E-012 / 390ms |
| pglib_opf_case500_goc | 500 | 171 | 728 | yes / 7 / 1.39E-012 / 165ms |
| case1354pegase | 1354 | 260 | 1991 | yes / 4 / 3.44E-012 / 214ms |
| case94pi | 94 | 1 | 93 | **NO** / 100 / 5.21E+002 / 112ms |
| case2848rte | 2848 | 512 | 3776 | yes / 2 / 9.56E-009 / 440ms |
| pglib_opf_case793_goc | 793 | 97 | 913 | yes / 6 / 2.12E-012 / 178ms |
| case141 | 141 | 1 | 140 | **NO** / 100 / 7.50E+001 / 165ms |
| pglib_opf_case179_goc | 179 | 29 | 263 | **NO** / 100 / 7.28E+001 / 354ms |
| pglib_opf_case197_snem | 197 | 35 | 286 | yes / 4 / 2.64E-012 / 6ms |
| case1354pegase | 1354 | 260 | 1991 | yes / 4 / 3.44E-012 / 168ms |
| case2869pegase | 2869 | 510 | 4582 | yes / 6 / 2.15E-009 / 735ms |
| pglib_opf_case300_ieee | 300 | 69 | 411 | **NO** / 100 / 2.36E+001 / 768ms |
| pglib_opf_case240_pserc | 240 | 143 | 448 | **NO** / 100 / 9.26E+001 / 622ms |
| pglib_opf_case240_pserc | 240 | 143 | 448 | **NO** / 100 / 9.26E+001 / 699ms |
| case300 | 300 | 69 | 411 | yes / 5 / 1.38E-012 / 32ms |
| pglib_opf_case162_ieee_dtc | 162 | 12 | 284 | **NO** / 100 / 3.37E+000 / 915ms |
| case2869pegase | 2869 | 510 | 4582 | yes / 6 / 2.15E-009 / 555ms |
| case2736sp | 2736 | 270 | 3269 | yes / 4 / 5.84E-012 / 1052ms |
| pglib_opf_case2383wp_k | 2383 | 327 | 2896 | yes / 6 / 4.21E-012 / 1362ms |
| case3012wp | 3012 | 385 | 3572 | yes / 3 / 7.47E-012 / 989ms |
| pglib_opf_case2746wop_k | 2746 | 431 | 3307 | yes / 4 / 6.80E-011 / 1160ms |
| pglib_opf_case2869_pegase | 2869 | 510 | 4582 | yes / 7 / 2.75E-012 / 586ms |
| pglib_opf_case162_ieee_dtc | 162 | 12 | 284 | **NO** / 100 / 3.37E+000 / 690ms |
| case2746wop | 2746 | 431 | 3307 | yes / 4 / 2.36E-009 / 1291ms |
| case_ACTIVSg2000 | 2000 | 432 | 3206 | yes / 3 / 8.64E-010 / 2067ms |
| pglib_opf_case2736sp_k | 2736 | 270 | 3269 | yes / 5 / 3.36E-012 / 1291ms |
| case6470rte | 6470 | 762 | 9005 | yes / 3 / 8.61E-012 / 2687ms |
| pglib_opf_case1803_snem | 1803 | 230 | 2795 | **NO** / 100 / 3.10E+000 / 2509ms |
| case1888rte | 1888 | 291 | 2531 | yes / 2 / 7.45E-012 / 118ms |
| case3120sp | 3120 | 298 | 3693 | yes / 7 / 5.06E-012 / 2487ms |
| case2746wp | 2746 | 456 | 3279 | yes / 4 / 5.40E-010 / 1430ms |
| pglib_opf_case2746wp_k | 2746 | 456 | 3279 | yes / 5 / 7.41E-012 / 1762ms |
| pglib_opf_case3_lmbd | 3 | 3 | 3 | **NO** / 100 / 6.67E+000 / 0ms |
| case4_dist | 4 | 2 | 3 | yes / 3 / 2.69E-009 / 0ms |
| pglib_opf_case3012wp_k | 3012 | 385 | 3572 | yes / 8 / 5.81E-012 / 2338ms |
| case2737sop | 2737 | 219 | 3269 | yes / 5 / 5.68E-012 / 1311ms |
| pglib_opf_case3120sp_k | 3120 | 298 | 3693 | yes / 5 / 1.25E-009 / 1509ms |
| case9241pegase | 9241 | 1445 | 16049 | yes / 6 / 2.13E-009 / 3832ms |
| pglib_opf_case2737sop_k | 2737 | 219 | 3269 | yes / 4 / 4.22E-012 / 1046ms |
| case3375wp | 3374 | 479 | 4161 | yes / 2 / 6.73E-012 / 810ms |
| pglib_opf_case1951_rte | 1951 | 366 | 2596 | **NO** / 100 / 9.97E+001 / 5452ms |
| pglib_opf_case3375wp_k | 3374 | 479 | 4161 | yes / 6 / 1.10E-011 / 2385ms |
| pglib_opf_case1888_rte | 1888 | 290 | 2531 | **NO** / 100 / 2.35E+002 / 4921ms |
| case1951rte | 1951 | 367 | 2596 | yes / 3 / 1.18E-011 / 132ms |
| pglib_opf_case2848_rte | 2848 | 511 | 3776 | **NO** / 100 / 2.12E+002 / 9118ms |
| pglib_opf_case3970_goc | 3970 | 383 | 6641 | yes / 6 / 5.89E-009 / 12919ms |
| pglib_opf_case2853_sdet | 2853 | 819 | 3921 | **NO** / 100 / 1.03E+001 / 6306ms |
| pglib_opf_case9241_pegase | 9241 | 1445 | 16049 | yes / 22 / 9.84E-012 / 11224ms |
| case2868rte | 2868 | 562 | 3808 | yes / 5 / 7.67E-012 / 448ms |
| case_ACTIVSg10k | 10000 | 1937 | 12706 | yes / 4 / 4.51E-009 / 16662ms |
| case10ba | 10 | 1 | 9 | **NO** / 100 / 1.84E+002 / 4ms |
| pglib_opf_case2000_goc | 2000 | 238 | 3633 | **NO** / 100 / 2.73E+000 / 16314ms |
| pglib_opf_case2312_goc | 2312 | 226 | 3013 | yes / 6 / 2.31E-012 / 1095ms |
| pglib_opf_case3022_goc | 3022 | 327 | 4135 | **NO** / 100 / 3.46E+001 / 20368ms |
| case2383wp | 2383 | 327 | 2896 | yes / 9 / 9.43E-012 / 1106ms |
| pglib_opf_case2868_rte | 2868 | 561 | 3808 | **NO** / 100 / 2.98E+001 / 7113ms |
| pglib_opf_case4917_goc | 4917 | 567 | 6726 | **NO** / 100 / 4.14E+001 / 46157ms |
| pglib_opf_case6515_rte | 6515 | 684 | 9037 | **NO** / 100 / 8.33E+001 / 54277ms |
| pglib_opf_case7336_epigrids | 7336 | 684 | 11519 | yes / 9 / 5.87E-010 / 54009ms |
| pglib_opf_case6495_rte | 6495 | 680 | 9019 | **NO** / 100 / 9.29E+001 / 54217ms |
| case8387pegase | 8387 | 1865 | 14561 | yes / 3 / 1.55E-011 / 1115ms |
| pglib_opf_case6470_rte | 6470 | 761 | 9005 | **NO** / 100 / 1.15E+002 / 55726ms |
| case6515rte | 6515 | 685 | 9037 | yes / 3 / 8.38E-012 / 1563ms |
| case6495rte | 6495 | 681 | 9019 | yes / 2 / 7.39E-011 / 977ms |
| pglib_opf_case5658_epigrids | 5658 | 474 | 9072 | yes / 4 / 1.18E-011 / 11963ms |
| pglib_opf_case8387_pegase | 8387 | 1865 | 14561 | yes / 11 / 4.41E-010 / 4088ms |
| case6468rte | 6468 | 400 | 9000 | yes / 3 / 7.80E-012 / 1570ms |
| case9241pegase | 9241 | 1445 | 16049 | yes / 6 / 2.13E-009 / 2457ms |
