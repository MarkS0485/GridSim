# hyperstack-3 (Canada) - 2026-11-18 - regression fixes A/B

Box: AMD EPYC 7763 (Zen 3), 28 vCPU, 56 GB, KVM VM, Ubuntu 24.04. Download: 7774 of 7774 files match the server SHA-256
manifest (VERIFIED.txt). 4000 solution export JSON files (6.4 GB) were left on the server; their SHA-256 list is in
day2-box\bulk-exports-not-downloaded.sha256.

Baseline = GridSim d629adda (RC3, same src as df3e4b1e). Candidate = d629adda + the P1/P2 src.patch
(sha256 5862b16609048f02e6cb74c5c61c1e908774e2f4dafdcb8b14bfa29d9d1adb7c; P1 minimum-degree LU ordering + P2 VmFloorPu
0.01), local commit 7dbbcfb on the box, never pushed. Harness ae2f53a, Release, TieredCompilation=false, --jobs 1 --pin,
strictly sequential. Gate: 10/10 converged, residual <= 1e-8 pu, 10% tie band.

## 1. Focused A/B, sparse path (median of 3 alternating rounds, mean of 10 solves)

"vs fastest other" = fastest other solver passing the gate, measured on this box.

| case | before ms | after ms | speedup | iters | residual after | vs fastest other |
|---|---|---|---|---|---|---|
| case_ACTIVSg2000 | 306.4 | 46.1 | 6.65x | 6/6 | 6.6e-9 | 69.6 ms, 1.5x faster |
| case2383wp | 115.2 | 28.8 | 4.00x | 6/6 | 4.6e-12 | 101.8 ms, 3.5x faster |
| case2737sop | 235.7 | 40.1 | 5.88x | 7/7 | 4.6e-12 | 234 ms, 5.8x faster |
| case2746wp | 267.7 | 41.4 | 6.46x | 8/8 | 5.1e-12 | 233 ms, 5.6x faster |
| case2746wop | 266.0 | 44.4 | 5.99x | 8/8 | 6.0e-12 | none passes |
| case2736sp | 165.4 | 38.8 | 4.27x | 7/7 | 4.1e-10 | none passes |
| case3012wp | 501.3 | 70.0 | 7.16x | 12/12 | 8.8e-12 | 140 ms, 2.0x faster |
| case3120sp | 240.1 | 45.5 | 5.28x | 7/7 | 5.5e-9 | 130 ms, 2.9x faster |
| case3375wp | 560.3 | 82.8 | 6.77x | 12/12 | 1.2e-11 | 192 ms, 2.3x faster |
| case2848rte | FAILS | 65.5 | n/a | -/12 | 7.5e-12 | 329 ms, 5x faster |
| case2869pegase | 72.8 | 42.3 | 1.72x | 7/7 | 4.0e-11 | 81.6 ms, 1.9x faster |
| case1354pegase | 22.2 | 15.3 | 1.45x | 6/6 | 3.1e-12 | 49 ms, 3.2x faster |
| case1197 | 6.0 | 4.9 | 1.21x | 3/3 | 4.6e-12 | 28 ms, 5.7x faster |
| case_ACTIVSg500 | 5.23 | 3.52 | 1.49x | 4/4 | 2.4e-12 | 14.8 ms, 4.1x faster |
| case300 | 3.31 | 2.54 | 1.31x | 5/5 | 4.1e-9 | 15.5 ms, 6x faster |
| case118 | 1.14 | 0.98 | 1.16x | 4/4 | 4.1e-13 | 6.0 ms, 6x faster |

Round-to-round spread <= ~5% above 5 ms. Dense measured in round 1 only (0-20% faster). Detail: analysis-focused\.

## 2. case2848rte

Passes with the candidate on both paths (sparse 65.5 ms, 12 iterations, residual 7.5e-12 pu; dense 6.8 s). Fails on
both paths on RC3.

## 3. Full 67-case suite (candidate GridSim on this box vs other solvers' times from box 2, same CPU class)

| | Wins | Ties | Losses | Nobody solved |
|---|---|---|---|---|
| RC3 baseline (this box) | 53 | 2 | 7 | 5 |
| Candidate (fixes) | **62** | **0** | **0** | 5 |

Nobody solved: case16am, case_RTS_GMLC, case1888rte, case1951rte, case2868rte. No convergence or iteration change except
case2848rte. Only flagged slowdowns are sub-millisecond (case18 0.15 -> 0.17 ms, case118zh 0.74 -> 0.87 ms).
Caveat: other solvers were re-measured on this box only for the 16 focused cases. Detail: scorecard.md,
scorecard-candidate.csv, scorecard-rc3.csv, analysis-full/ and analysis-vs-box2/.

## 4. Linux determinism tests

Tree: candidate + tests-rebaseline.patch (sha256 6c8bba6b..., the pre-approval version), SDK 10.0.401, 0 warnings.
Focused slice of 763 tests in normal, AVX2=0 and HWIntrinsic=0: 756 passed, 7 failed in each mode. The 7 are exactly
the pins approved this morning and updated afterwards on perf-rc3 (648ff4e4): EMT gb-full lumped/fault, EMT case9 fault,
Fb1 case9 60 Hz fault, MachineAdmittance case300, N186 case300-undamped, iberia UiAssetFixes. The digests Linux computed
are identical in all three modes and equal the values now committed on perf-rc3 (checked: e.g. gb-full fault
1C6C93CBEF54215423360AF2975389502035FAF36357315756DD85F7A04C4809). So Linux matches Windows bit for bit with the fixes.
Logs: kept offline.

Files: OPERATOR-LOG.md, VIRTUAL_MACHINE.md and the analysis-* folders. The kit scripts, box logs and per-run digests are kept offline.
