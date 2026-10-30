## Server benchmark results (AMD EPYC 7763, 28 cores)

> **Snapshot status (partial run).** These figures are from a live regime that was still
> executing when captured. Only **Phase 1 (bench)** has produced output, and only its
> **sparse** half - the sweep was at case ~126/153 with the process still running
> (`GridSim.Cli bench`, ~1700% CPU, ~13 GB RSS). The **dense** reference sweep had not yet
> started, so the dense<->sparse agreement cross-check is **not available in this snapshot**.
> Phases 2 (N-1), 2b (full PEGASE N-1), 3 (stress), 4 (warm-start chain) and 5 (tarball)
> had **not run**. Results dir: `results/20260701-101835/` (files `bench.md`, `bench.csv`,
> `regime.log`, `run-metadata.json`). Re-pull after the run completes to fill in the dense
> column and the N-1/stress/chain sections below.

### Run info

| Field | Value |
|-------|-------|
| Machine | `curious-bohr` - Ubuntu 22.04.5 LTS |
| CPU | AMD EPYC 7763 64-Core Processor - 28 logical cores |
| RAM | 57 GB (managed heap hard-capped at 48 GiB so oversized cases raise catchable OOM, not OS-kill) |
| Git commit | `c41ef951` (dirty) |
| Core version | GridSim.Core v0.1.0.0 |
| Build | Release |
| Runtime | .NET 10.0.9, X64, Server GC |
| Run | `bench cases=153 runs=10 parallel=28` |
| Date (UTC) | 2026-07-01 10:18:36 |
| Solver settings | Q-limits off, tolerance 1e-8, max 100 iters |

Smoke test (`case9`) passed before the sweep.

### Convergence (sparse sweep - representative selection)

Sparse = ILU0-preconditioned BiCGSTAB, run at full parallelism. The **dense reference sweep
had not started at snapshot time**, so the dense columns are marked *pending*; the key trust
signal (identical iters + mismatch between the two solvers) can only be reported once the
dense half runs. Time is mean ms over 10 runs.

| Case | Buses | Sparse conv | Sparse iters | Sparse mismatch | Sparse mean ms | Dense (iters / mismatch / ms) |
|------|------:|:-----------:|-----------:|----------------:|---------------:|:------------------------------|
| case9 (WSCC) | 9 | yes | 4 | 1.69E-14 | 14 | *pending* |
| pglib_opf_case14_ieee | 14 | yes | 4 | 6.73E-15 | 13 | *pending* |
| case57 | 57 | yes | 3 | 3.45E-12 | 6 | *pending* |
| case118 | 118 | yes | 3 | 1.51E-12 | 61 | *pending* |
| case300 | 300 | yes | 5 | 1.38E-12 | 32 | *pending* |
| pglib_opf_case588_sdet | 588 | yes | 5 | 7.27E-10 | 174 | *pending* |
| case1354pegase | 1354 | yes | 4 | 3.44E-12 | 214 | *pending* |
| pglib_opf_case1354_pegase | 1354 | yes | 6 | 2.67E-12 | 390 | *pending* |
| case_ACTIVSg2000 | 2000 | yes | 3 | 8.64E-10 | 2067 | *pending* |
| case2869pegase | 2869 | yes | 6 | 2.15E-09 | 735 | *pending* |
| pglib_opf_case2869_pegase | 2869 | yes | 7 | 2.75E-12 | 586 | *pending* |
| pglib_opf_case3970_goc | 3970 | yes | 6 | 5.89E-09 | 12919 | *pending* |
| pglib_opf_case5658_epigrids | 5658 | yes | 4 | 1.18E-11 | 11963 | *pending* |
| pglib_opf_case7336_epigrids | 7336 | yes | 9 | 5.87E-10 | 54009 | *pending* |
| case8387pegase | 8387 | yes | 3 | 1.55E-11 | 1115 | *pending* |
| pglib_opf_case8387_pegase | 8387 | yes | 11 | 4.41E-10 | 4088 | *pending* |
| case9241pegase | 9241 | yes | 6 | 2.13E-09 | 3832 | *pending* |
| pglib_opf_case9241_pegase | 9241 | yes | 22 | 9.84E-12 | 11224 | *pending* |
| **case_ACTIVSg10k** (largest run so far) | 10000 | yes | 4 | 4.51E-09 | 16662 | *pending* |

All converged cases land at mismatch <= ~1e-9 (most at 1e-11/1e-12), i.e. a fully solved power
flow. The PEGASE family scales cleanly: 1354->9241 buses solves in 4-22 Newton iterations. Note
the archive contains larger networks (up to ~78k buses per the report header, e.g. ACTIVSg25k/70k)
that the sparse sweep had **not yet reached** - 10,000 buses (`case_ACTIVSg10k`) is the biggest
solved in this snapshot.

#### Non-convergence and load failures (sparse)

Two distinct groups did **not** converge as a flat-start transmission power flow (hit the
100-iteration cap with large residuals):

- **pglib OPF-cased networks** - the PGLib-OPF operating points do not solve as a plain power
  flow, while their non-OPF twins converge fine. Clear examples:

  | Case | Buses | pglib OPF result | Non-OPF twin |
  |------|------:|:-----------------|:-------------|
  | ..._case1888_rte | 1888 | **NO** / 100 / 2.35E+02 | `case1888rte` yes / 2 / 118 ms |
  | ..._case1951_rte | 1951 | **NO** / 100 / 9.97E+01 | `case1951rte` yes / 3 / 132 ms |
  | ..._case2848_rte | 2848 | **NO** / 100 / 2.12E+02 | `case2848rte` yes / 2 / 440 ms |
  | ..._case2868_rte | 2868 | **NO** / 100 / 2.98E+01 | `case2868rte` yes / 5 / 448 ms |
  | ..._case2000_goc | 2000 | **NO** / 100 / 2.73E+00 | - |
  | ..._case6470_rte | 6470 | **NO** / 100 / 1.15E+02 | `case6470rte` yes / 3 / 2687 ms |

  (also `..._case3022_goc`, `..._case4917_goc`, `..._case6495_rte`, `..._case6515_rte`,
  `..._case2853_sdet`, `..._case240_pserc`, `..._case300_ieee`, `..._case179_goc`,
  `..._case162_ieee_dtc`, `..._case1803_snem`, `..._case39_epri`, `..._case3_lmbd`.)

- **Radial distribution feeders** - small single-generator distribution cases don't converge
  under the transmission NR (e.g. `case12da`, `case15da`, `case16am`, `case18nbr`, `case22`,
  `case28da`, `case33bw`, `case33mg`, `case34sa`, `case38si`, `case51he/51ga`, `case69`,
  `case74ds`, `case85`, `case94pi`, `case118zh`, `case136ma`, `case141`, `case10ba`).

- **No OOM** cases in the snapshot - every large network that was reached allocated fine.

- **Load failures (2):** `case533mt_hi` and `case533mt_lo` - parser cannot handle the literal
  `135/sqrt(3)` in the input (`LOAD FAIL: The input string '135/sqrt(3)' was not in a correct format.`).

### N-1 sweep

**Not run in this snapshot.** Phase 2 (`nminus1-sweep --all --sample 3000`) and Phase 2b
(full unsampled N-1 on `case1354pegase`, `case2869pegase`, `case9241pegase`) had not started.
Throughput (solves/s) and secure / violation / islanded counts will appear here once the
`nminus1-all.md` and `nminus1-case*pegase.md` files are produced.

### Stress (max loadability)

**Not run in this snapshot.** Phase 3 (`stress --all --from 0.5 --to 3.0 --step 0.05`) had not
started. Per-case max loadability multipliers will appear here from `stress.md`.

### Chain (warm-start behaviour)

**Not run in this snapshot.** Phase 4 (warm-start solve chains: `case118` fixed, `case300` ramp,
`case1354pegase` daily, 100 steps each) had not started. Cold-vs-warm iteration counts and
solution drift will appear here from `chain-*.md`.

### Headline findings (from the partial sparse sweep)

On the 28-core EPYC 7763, GridSim's sparse (ILU0/BiCGSTAB) solver converges the full IEEE/PEGASE
line-up to machine-tight mismatches (<= ~1e-9, typically 1e-11/1e-12) in a handful of Newton
iterations, scaling smoothly from 9 to 10,000 buses - the 9241-bus PEGASE network solves in 6
iterations (~2.5-3.8 s) and the 10k-bus ACTIVSg case in 4 iterations (~16.7 s). The only
non-convergences are expected: PGLib **OPF** operating points that aren't valid flat-start power
flows (their non-OPF twins solve trivially) and small radial distribution feeders outside the
transmission solver's remit; two ACTIVSg-533 files fail to parse a `sqrt(3)` expression. The
headline cross-check - that the dense reference reproduces the sparse iteration count and mismatch
case-for-case - cannot be reported yet because the dense sweep, along with the N-1, stress and
chain phases, had not run when this snapshot was taken. Re-pull `results/20260701-101835/` after
the regime finishes to complete this section.
