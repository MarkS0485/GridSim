# RC3 d629adda vs RC3 + P1/P2 (candidate)

Baseline: 3 run(s); candidate: 3 run(s). Time = median over runs of the mean of 10 timed solves (range over runs in brackets). Speed-up = baseline / candidate. Gate: 10/10 converged, residual <= 1e-08 pu.

## sparse

| case | buses | before ms | after ms | speed-up | iterations before/after | residual before/after | note |
|---|---|---|---|---|---|---|---|
| case118 | 118 | 1.136 [1.112..1.232] | 0.9791 [0.8656..1.004] | 1.16x | 4 / 4 | 3.969e-13 / 4.08e-13 |  |
| case300 | 300 | 3.313 [3.272..3.313] | 2.537 [2.421..2.583] | 1.31x | 5 / 5 | 4.089e-09 / 4.089e-09 |  |
| case_ACTIVSg500 | 500 | 5.23 [4.981..5.26] | 3.519 [3.511..3.643] | 1.49x | 4 / 4 | 2.383e-12 / 2.38e-12 |  |
| case1197 | 1197 | 5.982 [5.858..5.987] | 4.941 [4.676..5.24] | 1.21x | 3 / 3 | 4.645e-12 / 4.645e-12 |  |
| case1354pegase | 1354 | 22.18 [21.69..22.6] | 15.29 [15.08..16.56] | 1.45x | 6 / 6 | 5.707e-12 / 3.102e-12 |  |
| case_ACTIVSg2000 | 2000 | 306.4 [301.2..315.3] | 46.1 [44.84..46.83] | 6.65x | 6 / 6 | 6.647e-09 / 6.647e-09 |  |
| case2383wp | 2383 | 115.2 [109..116.5] | 28.81 [28.59..28.96] | 4.00x | 6 / 6 | 5.407e-12 / 4.599e-12 |  |
| case2736sp | 2736 | 165.4 [160.6..165.6] | 38.76 [37.45..39.81] | 4.27x | 7 / 7 | 4.145e-10 / 4.145e-10 |  |
| case2737sop | 2737 | 235.7 [233..238.8] | 40.08 [39.7..40.17] | 5.88x | 7 / 7 | 5.663e-12 / 4.582e-12 |  |
| case2746wop | 2746 | 266 [249.4..267.7] | 44.44 [43.76..45.29] | 5.99x | 8 / 8 | 1.109e-11 / 6.001e-12 |  |
| case2746wp | 2746 | 267.7 [255.4..274.2] | 41.43 [40.94..42.9] | 6.46x | 8 / 8 | 1.414e-11 / 5.054e-12 |  |
| case2848rte | 2848 | - [-..-] | 65.46 [62.37..67.96] | - | - / 12 | - / 7.539e-12 | fails before, passes after |
| case2869pegase | 2869 | 72.83 [71.04..73.68] | 42.34 [42.12..43.9] | 1.72x | 7 / 7 | 3.979e-11 / 3.968e-11 |  |
| case3012wp | 3012 | 501.3 [491.4..510.6] | 70 [67.37..70.58] | 7.16x | 12 / 12 | 8.287e-12 / 8.772e-12 |  |
| case3120sp | 3120 | 240.1 [236.4..243.4] | 45.5 [44.48..45.66] | 5.28x | 7 / 7 | 5.539e-09 / 5.532e-09 |  |
| case3375wp | 3374 | 560.3 [550.9..574.5] | 82.82 [79.79..83.93] | 6.77x | 12 / 12 | 9.445e-12 / 1.15e-11 |  |

sparse: 15 cases timed on both sides; median speed-up 4.27x, best 7.16x (case3012wp), worst 1.16x (case118); rows with a note: 1.


Box hyperstack-3: AMD EPYC 7763, 28 vCPU KVM; one path at a time pinned to one core; Release, tiered JIT off; mean of 10 timed solves; alternating rounds in one session.
