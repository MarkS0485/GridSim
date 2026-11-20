# Task 6: extended / large-case suite on GridSim 7b648759 (FINAL)

Snapshot: 2026-11-19 16:19Z. FINAL. Models and standard tiers finished; the large tier ran out of hardware at 41/47 (details below).

Setup: Release, TieredCompilation=false, BenchmarkDriver 10 timed attempts after warm-up, flat start, tol 1e-8, max 50 iterations, Q limits off. Three streams ran concurrently, each pinned to its own CPU (standard CPU 1 / 5 GB cgroup, large CPU 2 / 13 GB, models CPU 3 / 2 GB): timings carry shared-memory-bandwidth contention and are not clean single-stream numbers. Dense paths capped at 5000 buses (CPU) and 1500 buses (GPU) in the MATPOWER tiers; 6000 in the models tier. Per-path timeout 1800 s standard, 3600 s large, 900 s models.

Catalogue: 277 unique case files (MATPOWER 79, PGLib-OPF 198) + 36 GridSim-Models; 68 power-flow-equivalent duplicates skipped (66 PGLib `__sad`, case30Q, case30pwl), so 209 MATPOWER-format cases scheduled (162 standard <= 5000 buses, 47 large). Licences: PGLib CC BY 4.0 and case_RTS_GMLC (NREL notice) and GridSim-Models (ODbL) vendored; 52 MATPOWER cases with no redistribution grant (43 none stated, 9 "with permission") fetched at run time from the pinned commit. Not obtainable: NESTA (discontinued), TAMU Texas7k/Western/Eastern (HTTP 403 to automated download).

## standard tier: 162/162 cases done (finished)

GridSim CPU sparse, by outcome:

| Outcome | Cases |
|---|---:|
| pass | 104 |
| no solver converged (flat start) | 36 |
| parser refused (gen PMIN>PMAX or QMIN>QMAX) | 21 |
| not materialised (Case has unsupported electrical field dcline) | 1 |

All paths (`complete` = 10/10 converged with reference-residual evidence):

| Path | case not materialised | complete | incomplete | no evidence |
|---|---:|---:|---:|---:|
| dense | 1 | 104 | 57 | 0 |
| sparse | 1 | 104 | 57 | 0 |
| gpu-dense | 1 | 76 | 19 | 66 |
| gpu-sparse | 1 | 104 | 57 | 0 |
| matpower | 1 | 110 | 51 | 0 |
| pandapower | 1 | 108 | 53 | 0 |
| pypsa | 1 | 60 | 101 | 0 |
| veragrid | 1 | 116 | 45 | 0 |
| powsybl | 1 | 100 | 61 | 0 |
| andes | 1 | 112 | 49 | 0 |
| powermodels | 1 | 117 | 44 | 0 |

## large tier: 40/47 cases done (STOPPED: out of hardware, see below)

**Out of hardware: the large tier is incomplete.** At 15:59 UTC on 2026-11-19 PyPSA reached 13.4 GB RSS on case_ACTIVSg25k and hit the stream's 13 GB memory cap (as much as one stream gets on this 20 GB box). The out-of-memory kill stopped the whole capped run, and the box was handed back, so the remaining cases were not run.

Partial: case_ACTIVSg25k.m (25000 buses). GridSim CPU sparse 10/10 converged, 9 iterations, max mismatch 7.6e-12 pu (GridSim's own figure, no independent audit), mean 1033 ms per solve. GPU sparse, MATPOWER and pandapower also finished 10 attempts (raw output kept outside the repository); ANDES, PowerModels, PowSyBl, PyPSA and VeraGrid did not run.

**Not run (6):** pglib_opf_case30000_goc.m, pglib_opf_case30000_goc__api.m, case_ACTIVSg70k.m, pglib_opf_case78484_epigrids.m, pglib_opf_case78484_epigrids__api.m, case_SyntheticUSA.m

GridSim CPU sparse, by outcome:

| Outcome | Cases |
|---|---:|
| no solver converged (flat start) | 24 |
| pass | 8 |
| parser refused (gen PMIN>PMAX or QMIN>QMAX) | 7 |
| GRIDSIM-ONLY NON-CONVERGENCE | 1 |

**GRIDSIM-ONLY NON-CONVERGENCE:**
- case13659pegase.m (13659 buses): converged=False, 50 iterations, mismatch 6.32e+01 pu; converged elsewhere: veragrid, powsybl

All paths (`complete` = 10/10 converged with reference-residual evidence):

| Path | complete | incomplete | no evidence |
|---|---:|---:|---:|
| dense | 0 | 0 | 40 |
| sparse | 8 | 32 | 0 |
| gpu-dense | 0 | 0 | 40 |
| gpu-sparse | 8 | 32 | 0 |
| matpower | 9 | 31 | 0 |
| pandapower | 9 | 31 | 0 |
| pypsa | 8 | 32 | 0 |
| veragrid | 10 | 30 | 0 |
| powsybl | 9 | 31 | 0 |
| andes | 10 | 30 | 0 |
| powermodels | 7 | 33 | 0 |

Correction (2026-11-19 13:50Z): "converged elsewhere" now requires the other tool's independently checked residual <= 1e-8 pu, not just its own converged flag. The earlier interim listed case16am, case2868rte and pglib_opf_case20758_epigrids as GridSim-only failures; on that check no tool reached 1e-8 on them (case16am: GridSim 2.9e-8 was the closest, pandapower 4.0e-8, PowerModels 2.0e-7).

Notes: `no evidence` on dense/gpu-dense = above the dense bus cap (skipped by design). The parser refusals are GridSim's MatpowerParser rejecting PGLib `__api` generator rows with PMIN > PMAX (the API cases raise loads and PMIN past PMAX); MATPOWER and the other tools ignore those OPF limits in a power flow. ANDES adds 1e-8 pu to line R and X, so its reference-residual check fails by design.

## models tier: 36/36 GridSim-Models done (finished)

Gate (GridSim only, no independent audit): 10/10 converged, mismatch <= 1e-8 pu, 10 results bit-identical.

| Path | pass | FAIL | error | skipped |
|---|---:|---:|---:|---:|
| dense | 33 | 0 | 3 | 0 |
| sparse | 36 | 0 | 0 | 0 |
| gpu-dense | 30 | 0 | 6 | 0 |
| gpu-sparse | 35 | 0 | 1 | 0 |

Errors (all resource limits, none numerical):

- japan-west-spine-500-66-r6 (3611 buses) gpu-dense: timeout 900 s, peak RSS 1414.8 MB
- iberia-spine-400-220-132-r9 (3688 buses) gpu-dense: timeout 900 s, peak RSS 1684.0 MB
- japan-east-spine-500-66-r6 (4551 buses) gpu-dense: .NET "Out of memory." in the 2 GB cgroup, peak RSS 1149.7 MB
- italy-spine-380-220-150-r5 (5006 buses) dense: .NET "Out of memory." in the 2 GB cgroup, peak RSS 1566.3 MB
- italy-spine-380-220-150-r5 (5006 buses) gpu-sparse: timeout 900 s, peak RSS 286.2 MB
- italy-spine-380-220-150-r5 (5006 buses) gpu-dense: .NET "Out of memory." in the 2 GB cgroup, peak RSS 1306.0 MB
- india-spine-220-r13 (5318 buses) dense: .NET "Out of memory." in the 2 GB cgroup, peak RSS 567.6 MB
- india-spine-220-r13 (5318 buses) gpu-dense: .NET "Out of memory." in the 2 GB cgroup, peak RSS 670.0 MB
- ercot-spine-345-138-r3 (5771 buses) dense: .NET "Out of memory." in the 2 GB cgroup, peak RSS 646.5 MB
- ercot-spine-345-138-r3 (5771 buses) gpu-dense: .NET "Out of memory." in the 2 GB cgroup, peak RSS 749.6 MB

CPU sparse peak RSS across models: max 394 MB.

