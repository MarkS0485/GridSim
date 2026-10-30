# Changelog

All notable changes to GridSim. The format is based on [Keep a Changelog](https://keepachangelog.com).
Versions are listed oldest first and are undated.

---

## [0.1.114]
### Added
- MATPOWER `.m` parser (`GridSim.Core.IO.Matpower.MatpowerParser`), the single import path for every standard test
  system. Parses `mpc.baseMVA`, `mpc.bus`, `mpc.gen` and `mpc.branch` into a `GridModel` with exact per-unit values,
  with comment stripping and `...` line-continuation handling.

## [0.1.121]
### Added
- Parser robustness: `Inf`/`-Inf` generator limits, dropping of isolated (type-4) buses with their incident elements,
  skipping of out-of-service (status-0) elements, transformer detection by tap ratio, and non-contiguous bus ids
  preserved in file order (PEGASE numbering).
- `ParseWithDiagnostics` returns notes on dropped or skipped elements and multiple slacks.

## [0.1.128]
### Added
- `IeeeCases` factory methods for the standard systems (`Case14/30/57/118/300/145` and `Case24Rts`), loaded from
  embedded `.m` resources; `Case9` stays as the hand-coded ground truth.

## [0.1.134]
### Added
- Case libraries under `data/matpower/`: MATPOWER 8.0 `lib/`, PEGASE `case1354/2869/9241`, PGLib RTS-96 (73-bus) and a
  NESTA-lineage subset, with provenance and licences recorded.

## [0.1.140]
### Added
- CIGRE MV distribution benchmark built to the CIGRE TF C6.04.02 specification (`data/matpower/benchmarks/cigre_mv.m`,
  `IeeeCases.CigreMv`).

## [0.1.146]
### Fixed
- Fixed build breaks from an empty character literal in `GridSim.Gda/Csv/Csv.cs` (UTF-8 BOM trim) and in the CLI
  replay gauge glyphs.

## [0.1.152]
### Fixed
- Tests: `IeeeCasesTests` now re-anchors both solved and published angles to the slack bus, so cases whose slack sits
  at a non-zero reference angle (for example IEEE-118) compare correctly.

## [0.1.158]
### Changed
- Tests: added `MatpowerParserTests` (tokenizer, `case9` parse against the hand-coded case, and PEGASE, RTS-96, NESTA
  and CIGRE convergence checks).

## [0.1.165]
### Added
- The CLI runs any MATPOWER case: positional `<file>.m` or `--mpc <file>`, plus built-in case names (`case14` to
  `case300`, `case24`/`rts`, `cigre`).

## [0.1.171]
### Added
- The WPF source picker discovers `data/matpower/**/*.m`, so the case libraries appear in the desktop app.

## [0.1.178]
### Added
- First `bench` verb: a dense-vs-sparse convergence table (iterations, mismatch, timing) over the built-in and library
  cases.

## [0.1.185]
### Added
- Size-aware backend selection in the CLI solve paths: the sparse solver is used past the dense memory ceiling.
### Changed
- `data/matpower/README.md` records provenance and BSD/CC-BY licence notes for the bundled cases.

## [0.1.190]
### Changed
- Documentation: README sections on the MATPOWER importer, the two solvers and benchmarking.

## [0.1.198]
### Changed
- Documentation: benchmark results in `docs/developer/benchmarks/benchmark-results.md`. Dense and sparse agree on iteration count and final
  mismatch on every case both can solve, up to the 9241-bus PEGASE system.

## [0.2.0]
### Changed
- `bench` runs cases in parallel (`--parallel N`, default the core count) with deterministic output ordering, and
  writes CSV alongside the markdown table.

## [0.2.9]
### Added
- Run metadata (`RunInfo`): every report and a `run-metadata.json` sidecar record the machine, runtime, active backend
  and the git commit captured at build time.

## [0.2.18]
### Changed
- `bench` runs a complete sparse sweep followed by a best-effort dense sweep.
### Fixed
- Removed the dense "infeasible" cap, which was calibrated to laptop RAM. Dense is attempted on every case and only a
  real allocation failure is recorded as `OOM`; large dense solves are memory-gated and run small to large.

## [0.2.31]
### Added
- `nminus1-sweep`: parallel full or sampled N-1 branch contingency screening
  (`ContingencyAnalysis.ScreenBranchesParallel`, per-thread solvers), reporting security counts and solves per second.
  `--sample N` bounds the largest systems and is always logged.

## [0.2.44]
### Added
- `stress`: load-to-collapse sweep via `GridScenario.AtLoad`, reporting each case's maximum loadability (the P-V nose
  point) with parallel load steps.

## [0.2.57]
### Added
- `chain`: consecutive warm-started solves (`GridScenario.WithSolvedVoltages`) in `fixed`, `ramp` and `daily` modes,
  tracking warm-start iteration counts and solution drift.

## [0.2.70]
### Added
- `tools/fetch-cases.sh` fetches the full PGLib-OPF archive and all MATPOWER cases (about 161 systems, 3 to about
  78,000 buses), which the bench and sweep verbs discover automatically.

## [0.2.83]
### Added
- `tools/run-regime.sh` runs the full server test regime (bench, sampled N-1, full PEGASE N-1, stress, warm-start
  chains, tarball) with a managed-heap hard limit (a catchable OOM instead of an OS kill) and per-phase `timeout`
  guards. Uses a self-contained `linux-x64` publish, so no SDK is needed on the target.

## [0.2.96]
### Added
- The regime streams versioned results per row. The sparse solver scales to 25,000-bus systems on a 28-core AMD EPYC
  7763.
### Changed
- Known non-convergence classes: PGLib OPF operating points (not valid flat-start power flows) and radial distribution
  feeders (unsuited to transmission Newton-Raphson).

## [0.2.140]
### Added
- Sparse linear-solver interface `Abstractions.ISparseLinearSolver` with `CpuSparseLuSolver`;
  `SparseNewtonRaphsonPowerFlow` takes the solver by injection (default CPU direct LU), matching the dense
  `ILinearSolver`.

## [0.2.170]
### Added
- `GridSim.Gpu` project (ILGPU): `GpuContext` (thread-safe CUDA device probe and lock), `GpuDenseLinearSolver` (GPU LU
  with partial pivoting) and `GpuSparseLinearSolver` (GPU BiCGSTAB with Jacobi preconditioner and CPU fallback on
  stagnation).

## [0.2.190]
### Added
- `--cpu` / `--gpu` switch (`SolverBackend` factory) on every verb. Warns and falls back to CPU when no usable device
  is present, and records the active backend in reports.

## [0.2.205]
### Changed
- Documentation: README sections on the GPU and on compute-backend priority (AMD/ROCm, then CPU, then open drivers
  (OpenCL/Vulkan), then CUDA), with the double-precision rationale for AMD Instinct.

## [0.2.215]
### Changed
- Documentation: GPU results on an NVIDIA RTX A6000. Results are correct (case9 exact), but its 1:64 FP64 rate makes
  the dense GPU solve about 500x slower than CPU, so double-precision power flow needs full-rate FP64 hardware.

## [0.2.222]
### Changed
- Documentation: developer documentation under `docs/` covering architecture and solver internals, the CLI, case
  libraries and the MATPOWER parser, and benchmarking and GPU.

## [0.2.229]
### Changed
- Known limitation: the `.m` parser does not evaluate arithmetic expressions (for example `135/sqrt(3)`), so some
  ACTIVSg cases fail to load.
- Known limitation: radial distribution feeders do not converge under the transmission Newton-Raphson solver.
- Known limitation: the ILGPU dense LU is correctness-first and slow.

## [0.2.240]
### Fixed
- The MATPOWER parser evaluates arithmetic expressions in data matrices (`sqrt(...)`, `* / + -`, parentheses, `pi`),
  so the ACTIVSg cases `case533mt_hi` and `case533mt_lo` load.

## [0.2.258]
### Added
- Backward/forward-sweep distribution solver (`BackwardForwardSweep : IPowerFlowSolver`), selectable with `--bfs`. It
  converges high-R/X radial feeders that Newton-Raphson cannot, and models line charging, bus shunts and unity taps
  so it solves the same pi-model. Matches the dense solver on CIGRE MV and reproduces the Baran & Wu 33-bus published
  solution (min V about 0.9131, loss about 0.2 MW) once its data is converted to per-unit.
- Radial feeders under `data/matpower/dist/` (case33bw/69/85/18/22). `case33bw` is stored in raw units (ohms and kW),
  not MATPOWER per-unit/MW.

## [0.3.0]
### Added
- ROCm/HIP GPU backend in `GridSim.Gpu`: `RocmContext`, `RocmDenseLinearSolver` (hipSOLVER dense LU) and
  `RocmSparseLinearSolver` (rocSPARSE, Jacobi-BiCGSTAB), behind the same `ILinearSolver` / `ISparseLinearSolver`
  interfaces as the CPU and CUDA backends. Not yet validated at run time on AMD Instinct hardware.
- `--gpu` auto-detects ROCm first, then CUDA, with explicit `--gpu=rocm` / `--gpu=cuda`; unavailable devices warn and
  fall back to CPU. The active backend is recorded in every report.

## [0.3.10]
### Changed
- Sparse LU rewritten as a Gilbert-Peierls left-looking factorization (flat CSC arrays, RCM ordering): 6-7x faster on
  the linear solve (case1354 44 to 6.3 ms, case2869 96 to 16 ms per solve), with bit-identical results.
- Dense LU vectorized with `Vector<double>` SIMD (no FMA, so bit-identical): 1.5-1.9x faster end to end.

## [0.3.20]
### Changed
- CUDA dense solver does on-device pivot search with a deferred singularity flag, cutting host-device syncs from about
  5n to 2; sparse BiCGSTAB dot products run as on-device reductions. ROCm dense and sparse hardened to match (hipSOLVER
  getrf/getrs buffer sizing, rocSPARSE SpMV analysis). Both keep the CPU fallback; hardware validation is pending.

## [0.3.30]
### Added
- The backward/forward-sweep solver supports weakly meshed networks (breakpoint loop compensation, up to 6 loops) and
  PV buses held to setpoint within Q limits; densely meshed grids are still declined. Radial results are unchanged.
- Raw-unit detection (`MatpowerUnitNormalizer`) recognises distribution files stored in ohms and kW (for example
  `case33bw`) and converts them to per-unit/MW, leaving correctly per-unit cases such as CIGRE MV untouched.
- The CLI normalises raw `.m` files on load and sends strictly radial networks to the backward/forward-sweep solver,
  so `gridsim case33bw.m` converges out of the box (Vmin 0.913). Meshed cases stay on Newton-Raphson.
- Tests: solver invariants (power balance, dense equals sparse, determinism, Q limits, warm start), N-1 sequential vs
  parallel agreement, load scaling, RoCoF round-trip and MATPOWER stored-solution cross-checks.

## [0.4.0]
### Added
- `GridModel.Validate()` checks unique ids, a single slack, resolvable endpoints and LV-side OLTCs. It throws at the
  JSON and MATPOWER entry points and only warns for GDA input.
- Configurable RoCoF threshold (legacy 0.125 or modern 1.0 Hz/s; default 1.0).
- Inertia headroom against the secure minimum on the headline tiles; SIMULATED markers on synthetic panels and
  sparklines; theme resources for track colour, font scale and gauge bands.
- The backward/forward-sweep solver is exact at non-unity transformer taps.
- Shared dense/sparse Newton code (`NewtonShell`), with bit-identical results.
- `LiveSim`/`SimFrame` moved to Core, with unit tests.
- Frequency calibration harness against the NESO trace, with documented constant provenance.
### Changed
- `SteadyStateSolver` is guarded as non-thread-safe (re-entrancy check for parallel N-1).
- Removed the AI-attribution footer from the product; improved dark-theme contrast; clamped the over-frequency trace;
  froze the sparkline pen; made the asset home lookup O(1).
### Fixed
- Case9 inertia corrected to the published WSCC / Anderson-Fouad data (H = 23.64/6.40/3.01 s on 247.5/192/128 MVA;
  total 7.47 GVA.s, was 1.62), with branch ratings, so its frequency and RoCoF screens are meaningful.
- The SCADA register rebuilt every 900 ms, wiping tap events and alarm de-duplication; it now keys on a clone-stable
  identity.
- A faulted asynchronous solve now drives the status lamp, an alarm and the log instead of silently freezing the
  dashboard.
- Fixed the dispatcher double-counting a multi-generator slack.
- `GdaPaths.Newest` orders chronologically rather than lexicographically.
- Losses are unified (MACHINE uses the solved `TotalLossMw`); CO2 is per fuel (was a flat 0.38 on all generation);
  unbound MACHINE outputs and missing glyphs restored.

## [0.5.0]
### Added
- MATPOWER test cases run live in the dashboard: picker cases start and keep re-solving a steady state, warm-started
  each tick (`LiveSim(holdSteady)`), and a launch argument (for example `-- case9`) opens a named case.
- All three solvers are selectable in the front end through `SolverSelector` (Auto, dense NR, sparse NR,
  backward/forward sweep) with a SOLVER dropdown and active-engine readout. Auto sends radial feeders to the sweep and
  large cases to sparse; the CLI uses the same rule.
- Opt-in PV-to-PQ revert and slack-Q diagnostic (`RevertQLimits`, default off), with a one-revert anti-oscillation cap.
- Automatic layout for cases without coordinates (`GraphLayout`): deterministic Fruchterman-Reingold for meshed
  networks, a radial tidy-tree for feeders, a concentric-ring fallback for very large graphs, and an iteration budget
  that scales with size.
- Display voltage tiers for flat single-voltage imports (`TopologyTiers`), mapped to GB-like nominal kV so the map
  draws a voltage hierarchy. True kV stays in tooltips.
- Tests: `GraphLayout`, `TopologyTiers`, `SolverSelector`, `LiveSim` steady mode and the Q-limit revert path.
### Changed
- Roundel radius is graded across voltage levels (400 to 11 kV and below) instead of two sizes.
### Fixed
- Imported cases below 200 kV (case33bw, case14, case118, cigre_mv) drew a blank map because `FastSchematic` culled
  everything under the GB EHV level-of-detail floor. Small networks now draw in full at any zoom.

## [0.6.0]
### Added
- `gridsim bench` benchmark matrix: every case on every available engine (`cpu-dense`, `cpu-sparse`, `bfs` for radial
  cases, and `cuda-*` / `rocm-*` with `bench --gpu` when a device answers; absent devices are labelled, not errors).
  One table per engine plus a case-by-engine timing matrix; the CSV gains an `engine` column. GPU engines run serially
  and skip cases over 3000 buses.
- `--enableExport[=path]` writes everything a solve computes to JSON: run metadata, network inputs, the admittance
  matrix, the per-unit bus spec, every iteration's mismatch and correction vectors and the Jacobian (size-gated;
  `--export-jacobian` forces it), Q-limit switching and the converged state with line flows. Works for dense, sparse
  and BFS (`SolveTrace` on `PowerFlowOptions.Trace`, `SolveExport`, schema `gridsim-solve-export/1`, documented in
  `docs/developer/solve-export.md`). A solve with a trace attached is bit-identical to one without, and the JSON is
  deterministic.
- Tests: `SolveExportTests`.

## [0.7.0]
### Added
- Variable time step and playback speed, set separately: a STEP control (1 ms to 20 min of simulated time per solve)
  and a playback speed. Keyboard: `,`/`.` change the step.
- Live frequency dynamics (`FrequencyIntegrator`): a sub-stepped centre-of-inertia swing integrator advanced at the
  step's dt, so frequency, RoCoF, nadir and recovery are integrated physics. Stable from 1 ms to 20 min.
- Live generator trip (`LiveSim.TripInfeed`): right-click a generator or press `T` to trip the largest infeed; the
  loss plays out through the live integrator and recovers over about 5 min. `SimFrame` carries `RocofHzPerS`.
- Tests: `FrequencyIntegratorTests` (nominal hold, initial RoCoF, agreement with `Simulate`, dt invariance) and a
  `LiveSim` trip-and-recover test.

## [0.8.0]
### Added
- `gridsim gpucheck`: a fast preflight that reports each backend's device and runs one small solve on it (CPU, CUDA,
  ROCm), flushing every step, so a crash leaves a precise trail.
- `bench --note "<hw, $/hr>"` records hardware and cost in the report header and run metadata.
- The ROCm `DllImportResolver` falls back to versioned `.so` names (`librocsparse.so.1`, ...) and `$ROCM_PATH/lib`, so
  the backend loads on runtime-only ROCm containers.
- `tools/gpu-bench.sh` one-shot runner (preflight, matrix, solve exports, tarball) and `docs/developer/gpu-rental-runbook.md`,
  a CPU, CUDA and MI300 comparison runbook.
### Changed
- The ROCm interop (`RocmInterop`) has not been run on AMD hardware; `gpucheck` is the tool to validate it.

## [0.9.0]
### Added
- No-forward-inference evidence engine: `ForwardInferenceGuard` (25 h horizon, on every dated ingester), the
  `replay-event` credibility gate, `scenario` counterfactual sweeps, `fleet` single-vs-fleet non-additivity,
  `evidence` dossiers and the `gda-replay` state estimator. Intrinsic inertia and FFR are reported in separate
  columns, and counterfactuals require a reproduced baseline.
- `crossval` compares the solver with pandapower (case9/14/30/57/118 agree to machine epsilon; swing vs scipy RK45
  within 2e-4 Hz).
- WPF pre-solved replay: load a run directory and step or scrub through exported frames, with a transport bar
  (Pause/Step/Speed/Colour/Fleet) on every tab (`SolveImport`, `PresolvedReplay`).
### Fixed
- `fleet` ran counterfactuals without a baseline gate. It now validates the device-free baseline and, unless it is
  `Reproduced`, prints `GATE FAILED ...` and exits 3.
- Numeric flags parsed with the ambient culture and silently fell back to defaults on bad input (on a comma-decimal
  locale `--dt-ms 0.5` became 5.0). Flags now parse with `InvariantCulture`; an unparseable value prints
  `error: <flag> expected ...` and exits 2.
- CSV writers used the current culture, so comma-decimal locales produced shifted columns. The process now runs with
  `InvariantCulture` on all threads.
- MACHINE tab integrators accumulated phantom time when replay moved backwards and were never reset on a model switch.
  `MachineVm.Reset()` now runs on switch, replay load, restart and play-from-end, and a backward tick integrates
  nothing. The trip lamp and alarm de-duplication are also cleared.
- `fleet` stagger was anchored to t=0, so the non-additivity result was always zero. Each unit now arms when frequency
  leaves its own deadband, plus `StaggerSeconds`; the default spread is 10 s (`--stagger`, `EvidenceSpec.Default`).
  On `gb-largest-loss-1320`: 692 MW online at nadir vs 897 MW, nadir 0.16 Hz deeper.
- `SolveExport` crashed on infinite values (PEGASE `Inf` limits, a non-converged BFS). It now writes named
  floating-point literals, matching `SolveImport`.
- The map's flow overlay drew every loading bucket with the same segments; each bucket now gets its own frozen
  geometry.
- Boundary rows were never removed, so replay and model switches showed stale corridor flows. Rows are now pruned.
- Positional case and event arguments were order-sensitive and could run the wrong case (for example
  `transient --minutes 5 case14` ran case9). Positionals now skip flags and their values in `replay-event`,
  `crossval`, `transient`, `chain`, `stress` and `nminus1-sweep`.
- An unknown verb started the live UK simulator. It now prints usage and exits 2; no argument or `uk` still starts the
  live simulator.
- The pandapower subprocess could deadlock on full pipes; stdout and stderr are now read concurrently.
- Exit codes: a missing required argument exits 2 (`replay-event`, `scenario`, `evidence`, `gda-replay`, `crossval`,
  `fleet`); `transient` exits 1 if no case ran; `crossval` without Python or pandapower exits 77 (skipped), distinct
  from 0 (agrees) and 3 (mismatch).
- The schematic re-rendered at 60 fps while paused; paused frames now do no render work.
- Solve export now includes bus and generator `name`, so replayed runs keep asset names. OLTCs and boundaries are
  still not exported.
- The "FREQUENCY EVENT" lamp was overwritten on the next tick. It now shows whenever |f - 50| exceeds 0.2 Hz and
  clears on recovery.
- Switching to a different model with the same bus count kept the old circuit register, causing wrong rows or an
  `ArgumentOutOfRangeException`. The register now keys on name, bus count and branch count.
- Documentation: README test count corrected.

## [0.10.0]
### Added
- Measurement-based WLS state estimator (`Core/Estimation/`, manual chapter 78). `Measurement`/`MeasurementSet` (wire
  format `gridsim-measurements/1`: Vm/PInj/QInj/PFlow/QFlow kinds with sigmas, bus-id/branch-name addressing including
  `"#k"` for unnamed parallel circuits, horizon-guarded `takenUtc`). `WlsStateEstimator` solves the Gauss-Newton normal
  equations on `ILinearSolver` with flat or warm start and non-finite guards; `ToPowerFlowResult` feeds estimates into
  replay, UI and export. The measurement model uses the solver's own injection and line-flow code.
- Observability analysis: unmeasured-island sweep and gain-matrix rank check naming the free state directions;
  unobservable sets return `Unobservable` with a report, never NaN.
- Chi-square bad-data detection (Wilson-Hilferty 95% quantile) with largest-normalized-residual removal; critical
  measurements are flagged and protected.
- Deterministic synthetic measurement sampler (`SyntheticMeasurements`, seed 20260713, `NoiseScale=0` exact mode).
- `estimate` CLI verb: writes `estimate-export.json` (`gridsim-estimate-export/1`), `residuals.csv` and run metadata;
  `--synth` saves the sampled set and scores the estimate against truth.
- Cross-validation against `pandapower.estimation` (`tools/crossval/crossval_estimation.py`): case14 2.3e-13 pu /
  1.0e-11 deg, case30 5.6e-16 pu, case118 2.3e-13 pu; `--bad-data` shows both tools remove the same +20 sigma error.
  `tools/crossval/se_residual_check.py` independently checks residuals, objective, optimality and (`--leverage`)
  criticality.
- Tests: exact recovery, Jacobian checks, observability, bad data, sampler determinism and the export contract.
### Fixed
- The residual-covariance LU applied row interchanges in the wrong order, which mis-flagged a healthy case14
  measurement as critical. Interchanges are now applied before forward substitution (LAPACK `dgetrs` order).

## [0.11.0]
### Added
- Sparse WLS backend above 3,000 buses, so the estimator runs on the GB grid: one Jacobian path for both backends
  (`IJacobianSink`), CSR gain with Gilbert-Peierls LU (`SparseLu.Factor` factor-once/solve-many) and sparse
  observability. Dense and sparse agree to 1e-8 with identical chi-square and bad-data verdicts.
- Trust-region damping (`MaxAngleStepRad` 0.2, `MaxVmStepPu` 0.1) so Gauss-Newton cannot walk voltages through zero.
- `estimate` loads GDA/JSON case directories and gains `--warm-start` and `--export-solve` (writes a
  `gridsim-solve-export/1` document).
- Documentation: manual section 78.9 on real data. gb-spine (316 buses) converges in 24 iterations, chi-square pass;
  gb-full (3,539 buses, 10,617 measurements) converges in 8 iterations (about 107 s), chi-square pass.

## [0.12.0]
### Changed
- Forward-inference horizon widened from 25 hours to 169 hours (7 days + 1 hour), set by
  `ForwardInferenceGuard.HorizonHours`. The extra hour keeps a full settlement week behind the boundary across DST
  changes. GDA's `Scripts/horizon.py` matches; raw data acquisition keeps its 25.2 h window via `ingest_cutoff()`.

## [0.13.0]
### Added
- The `gb-2019-08-09` built-in event now uses sourced data from the Ofgem investigation report (loss sequence, LFDD,
  outcome) and an operating point of 215 GVA.s inertia and 19.6 GW demand. The baseline is reproduced: RoCoF -0.162
  vs -0.170 Hz/s recorded, nadir 48.76 vs 48.80 Hz.
- `data/events/gb-2019-08-09-estimated.json`, an event catalog built from measured operating points. Use with
  `replay-event gb-2019-08-09 --catalog data/events/gb-2019-08-09-estimated.json`.
### Fixed
- `ReplayValidator` extends the simulation to the last recorded event plus 30 s, so the event's LFDD nadir at about
  76 s is no longer clipped.
- The inferred-inertia cross-check uses losses inside the RoCoF window (5 s or less): 223.7 GVA.s against the recorded
  215.

## [0.13.1]
### Added
- The bridge protocol adds `StateMessage.Source` (`live|presolved|estimated`) and an `EstimateBlock` (J, chi-square
  verdict and threshold, dof, measurement count, removals, iterations); `GridBridgeClient.EstimateReceived` exposes
  both. Older servers give nulls.
- WPF: a bridge source picker (PRESOLVED / ESTIMATED / LIVE), a chi-square status pill, a `--bridge-source` launch
  argument and `gb-spine` in the zone list.
- Web: `bridge:<case>@<source>` case ids and an "Estimated bridge" card on the home screen.

## [0.13.2]
### Added
- `--backfill-removals` (`EstimationOptions.BackfillRemovedFromPrior`): a removed real-power measurement is replaced
  by a pseudo-measurement from the solved prior (sigma inflated 3x, floored at 5% of value, tagged
  `source="removal-backfill-prior"`, never itself removed). This stops an under-observed area drifting to a wrong
  state with a passing chi-square; affected periods now report chi-square fail instead. Reactive measurements are not
  backfilled.

## [0.13.3]
### Added
- PMU phase-angle measurements (`MeasurementKind.VaDeg`), in degrees against the slack reference. A voltage-only set
  (Vm and VaDeg everywhere) is fully observable. `docs/pmu-gridradar-design-note.md` documents the re-referencing rule.

## [0.13.4]
### Changed
- The measurement model stores admittances per bus (neighbour arrays and diagonals) instead of a dense G/B pair
  (about 200 MB on gb-full). Summation order is unchanged, so results are bit-identical. The dense `YBus.Build` peak
  remains.

## [0.14.0]
### Added
- Per-asset dynamics framework (`GridSim.Core.Assets`): a versioned asset-class format (`gridsim-asset-classes/1`)
  describing how a device samples, computes, actuates and protects, bound to a bus and added to the swing model as a
  `pAssetFleet` term.
- Grid-following inverter (`GridFollowingInverter`): delayed local sampling, droop and df/dt response with ramp limits,
  and RoCoF, vector-shift and LVRT protection trips whose lost output becomes a deficit.
- Grid-forming inverter (`GridFormingInverter`): synthetic inertia as a power injection proportional to df/dt,
  separate from intrinsic inertia.
- Passive plant: `TransformerDynamics` (delayed OLTC, thermal derating) and `LineThermalDynamics` (IEEE-738), applied
  by `PassivePlantFleet.ApplyTo` on the steady-state tick.
- Optional `asset-classes.json` and `asset-bindings.json` per case; an inline `dynamicsClass` on a generator or branch
  overrides technology binding, and an unknown class fails the load. Parameters carry provenance, and curated
  placeholder (`CuratedPlaceholder`) values must carry the `[VERIFY]` token, enforced at load.
- `AssetEnvelope.Sweep` sweeps unmeasurable parameters to give a nadir/RoCoF band, tornado sensitivities and a
  `CascadeIsPossible` flag; `AssetValidation.Compare` back-tests against a recorded trace.
- `DomainFrequencySampler` and `AssetDynamicsFleet.AssignDomains` attribute power per frequency domain; the swing
  stays on the centre-of-inertia model.
- Asset dynamics are off by default (`DynamicsConfig.EnableAssetDynamics`; CLI `trip --asset-dynamics`). An empty or
  passive fleet gives results bit-identical to the lumped model.
- INVERTER FLEET tab (WPF and Web): per-class online MW, SoC, trips, net response and loss-of-mains deficit.
- GDA: `NetworkModel/build_asset_classes.py` generates the gb-full class library, bindings and passive-plant classes
  from the assembler's fuel types; on gb-full it binds 1,995 inverter units.
- Data-grounded class parameters, including RoCoF trip settings with the 500 ms delay (`rocofTripConfirmS`), OLTC tap
  step (about 1.25%) and span (±15%), summer cable derating (about 0.89 of winter) and per-fuel dispatch ramps.
  Documented in `docs/technical/13-asset-dynamics-data-grounding.md`.
- `GbResponseServices`: DC/DM/DR response at NESO-cleared volumes (about 950, 520 and 490 MW each direction) on their
  published delivery curves.
- `localRocofUpliftFactor` (default 1.0; GDA emits 1.16): scales the RoCoF seen by loss-of-mains relays above the 1 s
  centre-of-inertia value.
- `Asset2019EventTests`: with only the Hornsea and Little Barford losses at 215 GVA·s, a legacy DG fleet trips on
  RoCoF and an upgraded fleet rides through. The recorded 48.787 Hz nadir lies within the envelope band; depth is not
  predicted.
- Documentation: `docs/technical/14-cascade-validation-and-blind-prediction.md` and `BlindPredictionTests`. The cascade
  model is an envelope tool tagged `UnvalidatedAtScale`, not a validated predictor.
### Changed
- Asset bindings accept a trailing `*` prefix wildcard (`"wind*"` matches `wind-onshore` and `wind-offshore`); exact
  matches win.
- The Gridradar phasor network is described as 4 nodes (london, buckingham, manchester, strathclyde).
### Fixed
- `AssetAwareFrequencyIntegrator` passed a zero controller clock every substep, disabling detection latency, arming
  delay and vector-shift in the live simulation; the clock now advances.
- `Reset()` did not restore inverter state of charge; it now resets to `SocInit`.
- Passive plant skips unnamed branches, which could not be matched back in `ApplyTo`.

## [0.15.0]
### Added
- Protections layer (`GridSim.Core.Assets.Protection`): definite-time stages (monitored quantity, threshold, dwell,
  action) via `ProtectionElement`, `ProtectionEvaluator` and `ProtectionController`.
- Frequency protections: RoCoF / loss-of-mains (0.125 or 1.0 Hz/s with 500 ms delay), under/over-frequency, df/dt,
  and dynamic LFDD that sheds demand when simulated frequency crosses 48.8 Hz.
- Under/over-voltage and thermal-overload trips fed per-bus voltage and per-branch loading through
  `AssetAwareFrequencyIntegrator.SetNetworkState` (`LiveSim`).
- Topology-aware recloser: `Branch.InService` (results bit-identical when all branches are in service),
  `IslandPartition` (slack election for a generating island, blackout with lost load and generation for a dead one)
  and `TopologyOps`. `LiveSim` applies open branches before solving and recloses after the delay.
- JSON: protection classes in `asset-classes.json` (`kind: "protection"`) and a `protections.json` placement file
  (`{name, class, monitorBus, capacityMw, targetBranch?}`). Without protections, frequency and power flow results are
  bit-identical.
- GDA: `NetworkModel/build_protections.py` emits protection classes and placements.
- `trip --asset-dynamics` reports protections and the legacy DG loss-of-mains cascade.
- This is a generic protections model: thresholds are grounded where event data exists (RoCoF, LFDD, frequency) and
  placeholders elsewhere (recloser, voltage, thermal).

## [0.15.1]
### Added
- Runtime SIMD detection (`Core/Numerics/SimdCapabilities`): AVX-512, AVX2, SSE2 or scalar, chosen at run time, so
  one binary runs 8-wide on AVX-512 hardware. `gridsim simdcheck` reports the tier; benchmark headers and
  `run-metadata.json` gain a `simd:` line.
- Tiered SIMD kernels (`Core/Numerics/SimdKernels`) for scaled-row updates and dot products, in bit-exact and FMA
  variants.
- Vectorised batch `SinCos`, accurate to 1e-12 against `Math.SinCos`; used only on the opt-in transient path.
### Changed
- Dense LU, the estimator gain matrix (`FormNormalEquations`) and the residual-covariance LU use the shared kernels.
  Dense LU stays bit-exact; the gain matrix uses FMA and differs by about 1e-13.
- Paired `Cos`/`Sin` calls replaced by `Math.SinCos` in the solver, estimator and transient code, with bit-identical
  results.
- `CpuSparseLuSolver` caches the RCM ordering across Newton iterations (bit-identical): sparse power flow on case1354
  19% faster, case2869 6%.
- `CsrMatrix.Builder` sorts by key array, removing a per-row delegate allocation.
- `ClassicalMultiMachine.Deriv` is vectorised with preallocated buffers: about 1.85x on the classical transient
  kernel. Results differ by about 1e-13.

## [0.16.0]
### Added
- `PhysicsLaws.Audit` (`Core/Physics/`) re-derives the network laws from raw branch data independently of the solver
  and returns a `PhysicsLawReport` with a per-law result and a `Gate(hardLaws)` verdict. Energy conservation, Ohm,
  Joule, Kirchhoff current and complex power (`S=VI*`) form the hard gate (`PhysicsCheckRegistry.HardGateLaws`);
  Faraday and Ampere are constitutive, Kirchhoff voltage structural, AC synchronism a dynamics margin, and Gauss is not
  modelled.
- `gridsim physics-check <case> [--experimental]` prints the per-law table and gate verdict and exits 1 on a gate
  failure.
- Physics-constrained estimator `EstimationMethod.Feasible` (CLI `--method feasible`, alias `constrained-physics`;
  dense only, up to 3,000 buses). It keeps voltage magnitudes above zero, enforces violated generator Q-capability
  limits as constraints, and marks a state that breaks a hard law as `EstimationStatus.Infeasible`. Operational limits
  appear on `EstimationResult.LimitViolations` and demote the result only with
  `FeasibilityConstraints.RejectOnLimitViolation`. On a feasible state it matches plain WLS; other methods are
  unchanged.
- `Estimation/FeasibilityConstraints`, `EstimationOptions.Feasibility` and `MeasurementModel.InjectionQRow`.
- A `physics` block in `gridsim-solve-export/1` and `gridsim-estimate-export/1` with the per-law report and gate
  verdict (additive; schema unchanged).
- `tools/crossval/physics_audit.py` recomputes the hard laws in NumPy and checks the exported verdict; agreement is at
  machine epsilon.
- `gridsim conformance` audits the whole case corpus into `results/conformance/conformance.{md,csv}` with run
  metadata. Every converged benchmark case is admissible (17/17).
- Documentation: `docs/technical/16-physics-conformance.md` and a note in `docs/manual/14-validation-tools.md`.
- Web CONFORMANCE tab with an ADMISSIBLE / IMPOSSIBLE gate and per-law verdicts for the current state.
- `Physics/Experimental/ReciprocityCheck` (law 11, opt-in via `physics-check --experimental`): checks `Y = Y^T`
  excluding phase shifters; an asymmetry indicates a model defect. Never part of the hard gate.
### Fixed
- Cases with a negative-resistance branch (`case145`, `case588`, `case9241pegase`) failed the Joule check. Joule now
  checks loss consistency only; branch passivity is reported under Ampere and is not gated.

## [0.17.0]
### Added
- `DistributionFactors`: DC PTDF and LODF from a once-factored reduced susceptance matrix (`PtdfColumn`, `LodfColumn`,
  `DcFlows`, `OutageIslands`).
- `DcOptimalPowerFlow` solves security-constrained economic dispatch as an exact LP (generator bounds and two-sided
  line limits) to a global optimum with the new `BoundedSimplex`, replacing one-line-at-a-time redispatch. Merit order
  when limits are off; fuel-defaulted linear costs. On the GB spine: about 53 GW met at about £257k/h.
- `GridSim.Core.Numerics.BoundedSimplex`: dense two-phase primal simplex for `min c.x s.t. Ax=b, lo<=x<=hi` with native
  bounds; reports `Optimal`, `Infeasible`, `Unbounded` or `IterationLimit`.
- `PowerFlowTracing` (Bialek proportional sharing): allocates each load's supply and each branch flow to generators;
  defined only on an acyclic flow graph, and reports a cycle when present.
- `CarbonTracing`: flow-based system and per-load CO2 intensity (gCO2/kWh); about 181 gCO2/kWh on the GB spine.
- `ContinuumWaveModel` and `WaveField`: inter-area electromechanical modes (about 0.1-2 Hz) from the linearised swing
  equation, reducing to the SMIB analytic frequency; fails loudly without per-machine inertia. `WaveField` drives a map
  animation.
- `SymmetricEigen`: cyclic-Jacobi symmetric eigensolver (`Eigenvalues`, `EigenDecomposition`).
- `gridsim experimental <reciprocity|wave|trace|carbon|boundaries>`, `gridsim ptdf`, `gridsim lodf` and
  `gridsim dcopf` verbs.
- Web: EXPERIMENTAL tab (reciprocity, wave modes, traced flows, carbon), CONFORMANCE tab and a WAVE map overlay.
- WPF EXPERIMENTAL tab with conformance table, reciprocity, dominant wave mode, top generator-to-load allocations and
  carbon intensity.
- Opt-in branch thermal constraint in the feasible estimator (`FeasibilityConstraints.EnforceBranchRatings`, default
  off) using `MeasurementModel.BranchFlowRow`; overloads are always reported on `LimitViolations`.
- Documentation: experimental models and GB results in `docs/technical/16-physics-conformance.md` and
  `docs/manual/14-validation-tools.md`.
### Fixed
- `gb-full` (3,539 buses) loads and audits end to end (energy, KCL and `S=VI*` residuals about 4e-9). `GdaModelWriter`
  and the Python `writer.py` now escape quotes in generator names.

## [0.18.0]
### Added
- Passive protection-assessment overlay (`Core/Protection/`): `ProtectionAssessment.Assess(model, result, settings?,
  trajectory?)` reads a solved state and reports which protective devices would operate and why. It never calls the
  solver and never changes the model or result.
- `ProtectionContext` computes every operating quantity once from the solved state: bus voltage, branch apparent
  power, current, loading and apparent impedance `V^2/conj(S)`, the busbar KCL differential residual and frequency.
- Relay library (ANSI device numbers): 27/59 under/overvoltage, 32 reverse power, 37 undercurrent (an absolute
  de-energised floor), 49 thermal overload, 50/51 instantaneous and IEC 60255 standard-inverse overcurrent, 21
  distance (three forward zones), 81U/81O frequency, 81R RoCoF, 78 vector shift and 87 busbar differential.
- Each element yields a `RelayVerdict` per location with quantity, pickup, signed margin, inverse-time trip time,
  a plain-language basis and a confidence grade (Grounded, Generic or NotCheckable).
- Pickups are Grounded only for the statutory GB frequency band and the ALoMCP RoCoF limit. Earth-fault and
  negative-sequence elements (50N/51N/21N/46) report NotCheckable, as do RoCoF and vector shift without a
  dynamic `ProtectionTrajectory`.
- `gridsim protection-check <case> [--uv/--ov pu] [--uf/--of Hz] [--oc xIn] [--thermal frac] [--rocof Hz/s]`
  solves and prints the operating and not-evaluated elements. The flags override the generic pickups.
- A plain solve's `PrintDetailed` output ends with one passive protection line; the solve itself is unchanged.
- Balanced three-phase fault injection (`Core/Protection/FaultAnalysis.cs`) by Thevenin / bus-impedance
  superposition with subtransient generator reactance (synthesised generic X"d). Dense; fails loud above 1,500
  buses. Earth faults are reported as not representable.
- `ProtectionAssessment.AssessThreePhaseFault` applies a fault and assesses it in one call; `ProtectionContext`
  carries an optional per-bus fault current so the 87 element sees in-zone faults.
- `gridsim fault <case> --bus <id> [--zf pu]` prints fault current (pu/kA), Thevenin impedance, collapsed
  voltages and the operating relays.
- Cascade propagation (`Core/Protection/ProtectionCascade.cs`): trips the fastest branch element on a copy of the
  model, re-solves and repeats until nothing trips, the re-solve fails (split or blackout) or a step cap is hit.
  `gridsim protection-check <case> --cascade` prints the trip sequence.
- Documentation: technical chapter `docs/technical/17-protection-assessment.md`.

### Fixed
- The busbar differential nets off bus shunt (`Gs`/`Bs`) current, so a capacitor bank no longer reads as an
  internal fault.

## [0.18.1]
### Changed
- Web: RP1 purchased time uses the live frequency; credible loss and largest unit count synchronous units only;
  carbon factors match `MarketVm`; replay ramps use the 30-minute settlement step; SCADA corridor thresholds match
  WPF (P2 >90%, P3 watch >70%, 49.9-50.1 Hz band, 275 kV EHV cutoff).
- `GridSim.Gpu` now has hardware-free seam tests (dense/sparse CPU agreement, near-singular rejection,
  dimension checks).

### Fixed
- Sparse Newton `BuildY` skips out-of-service branches. Above the sparse threshold an opened branch previously
  stayed electrically connected, so recloser trips and cascade re-solves above 3,000 buses used the wrong network.
- `BackwardForwardSweep` emits zero-flow placeholders for out-of-service branches so `LineFlows` stays
  index-aligned with `Branches`.
- `PhysicsLaws.Audit` skips out-of-service branches instead of failing the hard-gate laws after a trip.
- Newton Q-limit switching happens only after a converged sub-solve, as in MATPOWER.
- `ProtectionCascade.FindBranch` resolves the tripped branch by `LocationId`, so parallel circuits on MATPOWER
  models are opened correctly.
- `ProtectionController` no longer counts one infeed's `CapacityMw` once per trip parameter.
- Estimation: `VaDeg` residuals and predictions are converted from radians to degrees instead of being scaled by
  `BaseMva`.
- `FeasibleWlsStep` honours `EnforceGeneratorQ = false`.
- `JsonGridLoader` throws on a demand row that references a nonexistent bus instead of dropping it.
- `GraphLayout.EnsureLayout` and `AssetEnvelope.WithOverrides` keep `AssetDynamics` and `Protections` when
  rebuilding the model.
- `GdaModelBuilder` converts percent-convention shunt susceptance with 100/BaseMva (was BaseMva/100).
- `GridBridgeClient` rejects a bridge state frame with no `t` timestamp.
- `scenario` counterfactual export and the `fleet` verb apply the event's recorded response services, consistent
  with the matrix, evidence and replay paths.
- `replay-event` exports the effective config (`ReplayResult.EffectiveConfig`); a missing `--trace` path fails.
- CLI: unknown flag names exit 2; `--as-of` (ISO-8601 only), `--dno`, `chain --mode` and `transient --solver`
  fail on bad values; `gda` failures exit 1; `transient` exits 3 on a non-converged slice; `conformance` exits 1
  when any case fails the law gate.
- Web: bridge start is derived from `ForwardInferenceGuard.Horizon`, so the server no longer rejects it.
- Web: `/api/solve` serialises non-finite values via `NonFiniteDoubleConverter`; `/ws/sim` reports bridge faults
  as error frames and caps control frames at 16 KB; `NetworkMap` sends only nodes and edges through JS interop.
- WPF: `OperatorVm` resets on source switch; `ApplyBridgeStream` drops cached frames; RP1 clears a stale boundary
  narrative; fleet registers prune rows outside the top 200; MECHANICAL ramps use the frame-clock delta;
  circuit detail Iin/Iout swap correctly on reversed flow.
- `GpuDenseLinearSolver` applies the CPU solver's relative near-singular pivot floor on the device.

## [0.19.0]
### Added
- `Diagnostics/RestrictionReview` gates any estimation method that fuses a dynamics process model; it fails
  unless registered and certified present/past-only. Documented in `docs/restriction-review-log.md`.
- `Simulation/RealtimeEstimatorEngine`, a streaming estimator over dated, permitted-age measurement windows with
  warm start, and `Simulation/RealtimeDseEngine`, which drives a recursive tracking filter.
- Per-bus 1-sigma bounds from `diag(G^-1)`: `EstimationResult.StateVmSigma` and `StateVaSigmaDeg`.
- CLI `serve` verb; Web `RealtimeEstimatorHostedService` with `ReplayDirectoryWindowFeed`, enabled by
  `Realtime:Enabled`.
- Dynamic state estimation (`Estimation/Dse/`): `IProcessModel`, `RandomWalkProcessModel`, EKF
  (`KalmanStateEstimator`), UKF (`UnscentedKalmanStateEstimator`), EnKF (`EnsembleKalmanStateEstimator`) and
  `FrequencyRocofFilter`, which tracks RoCoF as a state.
- Measurement kinds `IMagPu` (branch current magnitude) and `IAngDeg` (current-phasor angle).
- Correlated 2x2 block-R weighting in the linear PMU estimator.
- `Numerics/HouseholderQr` and `EstimationMethod.Qr` (`QrWlsStep`), which avoids forming the normal equations.
- `EstimationMethod.SchweppeGm` (Schweppe-Huber GM), `ProjectionStatistics` and `HypothesisTestingBadData` for
  interacting bad data.
- Distribution (`Distribution/`): `MultiPhaseModel`, 3x3 `PhaseImpedance`, `ThreePhaseSweep` power flow,
  `SymmetricalComponents`, `ThreePhaseWlsEstimator` (with WLAV mode) and `BranchCurrentDsse`.
- `SequenceFaultStudy` (three-phase, SLG, LL, LLG faults), `TransformerConnectionModel` (Yg-Yg, delta-wye
  30-degree shift), nominal-voltage pseudo-measurements and the voltage-unbalance factor.
- Synchrophasor (`Synchrophasor/`): IEEE C37.118.2 decoder, phasor data concentrator, `PmuMeasurementSetMapper`,
  `Estimation/LinearPmuStateEstimator` and a hybrid SCADA+PMU scheduler.
- `Estimation/Security/FdiaScreen` false-data-injection detector; `Diagnostics/AuditLog` (append-only SHA-256
  hash chain) and `EstimationAuditTrail`. Assurance notes under `docs/assurance/`.
- `IO/Cim/`: CIM / IEC 61970 (CGMES) RDF importer and exporter and `CimGridModelBridge` to a solvable `GridModel`.
- `IO/Telemetry/`: frame codecs for IEC 60870-5-104 (APCI), DNP3 (data link + CRC-16/DNP) and IEC 61850 GOOSE.
- `Analysis/ContinuationPowerFlow` (voltage-collapse loadability margin) and `Analysis/AcOptimalPowerFlow`
  (loss-aware AC-OPF by successive DC-OPF).
- Documentation: design specs under `docs/technical/design/`.

## [0.20.0]
### Added
- OpenCL GPU backend via ILGPU, running the same kernels as CUDA. Devices must pass an on-device FP64 check;
  devices without `cl_khr_fp64` are skipped.
- `GpuDenseLinearSolver` and `GpuSparseLinearSolver` take an `AcceleratorType` (default CUDA).
- `--gpu=opencl` and `Vendor.OpenCl`; the automatic backend order is rocm, opencl, cuda, cpu. `gpucheck` and
  `bench` include OpenCL.
- Documentation: `docs/developer/benchmarks/server-results/mi300/` placeholder, AMD checklist in the runbook, and OpenCL in
  `benchmarking.md` and the README backend table.

### Changed
- `GpuContext` supports CUDA and OpenCL over one shared ILGPU context.
- `RocmInterop` uses the native hipSOLVER API (`hipsolverDgetrf`/`hipsolverDgetrs`) matched to ROCm 6.x headers.
- `RocmContext` reports the real device name and selects the device with `hipSetDevice`.
- `RocmSparseLinearSolver` runs BiCGSTAB fully on the device using rocBLAS and `rocsparse_dcsrmv`. The native
  ROCm path has not yet been run on AMD Instinct hardware.

### Removed
- The unused generic `rocsparse_spmv` declaration.

## [0.20.1]
### Added
- `Diagnostics/RestrictionReview.AssertOutputHistorical` throws `RestrictionViolationException` when
  `RealtimeDseEngine` or `RealtimeEstimatorEngine` would emit an estimate newer than the 169 h horizon.
- Tests: RoCoF-as-state against finite-difference RoCoF in `FrequencyRocofFilterTests`.

### Changed
- `DistributedConfig.DominanceThreshold` (0.10) is documented as an engineering-judgement value; over 103,433 GB
  samples it flags about the bottom decile of dominance-gap conditions. It can be set through `init`.

### Fixed
- `FdiaScreen.Screen` takes a `physicsTolMva` (default `0.1 * BaseMva`) instead of the 1e-4 MVA power-flow
  tolerance, which flagged about 96% of noisy estimates as attacks.

## [0.21.0]
### Added
- International starter maps under `data/intl/`, in the standard case format with `MODEL_CARD.md` and
  `ATTRIBUTION.txt`: `ireland-all-island` (60 buses, converges), `iberia` (1,497), `france` (1,976) and
  `continental-europe` (5,824 buses).
- The maps use real open topology (PyPSA-Eur / GridKit, OpenStreetMap) with synthetic per-km impedances and
  demand; they are indicative, not planning-grade. The larger cases may not fully converge. Licence: ODbL and
  CC-BY-4.0. See `data/intl/README.md`.

## [0.22.0]
### Added
- Harmonics engine (`src/GridSim.Core/Harmonics/`), validated to machine epsilon against an independent NumPy
  reference (`tools/crossval/crossval_harmonics.py`).
- Complex solvers behind `IComplexLinearSolver`: `DenseComplexLuSolver`, `RealExpandedComplexSolver` (default,
  scales to gb-full) and `SparseComplexBiCgStabSolver`.
- `FrequencyDependentYBus` builds `Y(h)` with skin-effect R, frequency-dependent shunts, optional load damping
  and the long-line correction; it equals `YBus` at h=1.
- `HarmonicSourceLibrary` (6/12-pulse, PV, EV, arc furnace, saturated transformer, all placeholder spectra) and
  `IO/HarmonicSourceSet` (`gridsim-harmonics/1`). Measured spectra load as `Recorded`.
- `HarmonicPenetration`, `HarmonicImpedanceScan`, `HarmonicSequence`, `HarmonicIndices` (THD, K-factor) and
  `HarmonicCompliance` (IEEE 519, G5/5).
- `ThreePhaseHarmonicPenetration` for unbalanced networks; `CoupledHarmonicPowerFlow` with
  `NortonCouplingModel`; `HarmonicStateEstimator`.
- Opt-in per-order harmonic KCL law `Physics/Experimental/HarmonicKclCheck` (law 12, not a hard-gate law).
- `gridsim harmonics <case> [--spectra <file> | --synth-sources] [--enableExport[=path]]` and
  `IO/HarmonicExport` (`gridsim-harmonic-export/1`).
- HARMONICS tab in the Web and WPF dashboards.
- `Branch` and `JsonGridLoader` read `lengthKm`, `equipment` (OHL/cable), `rPu0` and `xPu0`. GDA publishes the
  `gridsim-harmonics/1` schema as `NetworkModel/HarmonicDataSchema.json`.
- Documentation: `docs/technical/19-harmonics.md` and `docs/manual/20-running-a-harmonic-study.md`.

### Changed
- A single shared `PowerFlow/AdmittanceStamp` replaces duplicated Y stamps. Results are bit-identical.

## [0.23.0]
### Added
- `gridsim bench --perf` harness, with results in `docs/developer/benchmarks/perf-baseline.md`.

### Changed
- Fault study builds the admittance once and walks sparse Y cells: ~46% fewer allocations at 300 buses.
- Sparse Newton-Raphson reuses the Jacobian pattern each iteration: ~31-37% fewer allocations per solve.
- `ComplexCsr.Builder`, RCM ordering and the OLTC outer loop allocate less. Results are bit-identical.
- `DenseComplexLuSolver` returns `null` on a singular matrix instead of a clamped answer (the fault study keeps
  its clamping) and uses flat row-major storage.
- All remaining inline pi-model stamps use `AdmittanceStamp`.
- Harmonic scans factor once per order, cache RCM ordering and solve orders in parallel: ~4x faster with ~83%
  fewer allocations. Results are bit-identical.

### Fixed
- A signed-zero difference between `YBus.BuildSparse` and `YBus.Build`.

## [0.24.0]
### Added
- `FrequencyConstantsFitter` (with `Numerics.NelderMead`) fits `DampingPctPerHz`, and optionally responsive
  fraction and governor lag, to a measured loss-of-infeed trace. The default is unchanged.
- `NoiseFloorAnalysis` compares each measurement class's actual noise with its declared sigma and suggests a
  rescale.
- `IeeeCases.TwoAreaFourMachine` test system.
- The CIM importer reads `PowerTransformer`, `PowerTransformerEnd`, `RatioTapChanger` and `PhaseTapChanger`.
- `ZipLoad` on `Bus` adds a voltage-dependent constant-Z/I/P load to both Newton solvers; the default stays
  constant power.
- The sparse estimator names unobservable state directions and separates under-metered buses from unmeasured
  islands.
- `RestorationSequencer` energises branches step by step and flags over-voltage or reactive-limited steps.
- `TapOptimizer` computes a coordinated OLTC tap schedule that flattens the voltage profile.

## [0.25.0]
### Added
- `FrequencySensitivitySweep` reports the frequency nadir as an uncertainty band over the swing constants.
- `SequenceOfEvents` and `AlarmEvent` reconcile a predicted protection cascade with a timestamped alarm log.
- `ProtectionSettings.PerAsset` and `ProtectionContext.SettingsFor` apply site-specific settings to branch relays
  (49/50/51/21); a null map leaves assessments unchanged.
- `WatchList` ranks overloads, voltages, convergence health, time-to-trip and boundary transfers by consequence
  and proximity.

## [1.0.0] - BREAKING RELEASE
### Changed
- Breaking: results generated by earlier versions must be re-run, because of the fixes below.
- Breaking: `GdaModelBuilder` no longer treats the NGED `'-'` marker as a join key; it had placed 375 rows
  (13.7 GW) of generation on one bus.
- Breaking: `FuelMap` classifies `'Pump Storage'` as synchronous pumped hydro (H=3.0), adding 2.7 GW to the
  inertia total.
- Breaking: `--gsp` and `--all-gsps` carves keep their embedded generation (`GspKey`); about 92% was dropped.
- Breaking: `GdaModelBuilder` no longer turns synthesised 3-winding star nodes into slack buses.
- Breaking: `GdaPaths` finds the demand table by `*table-3*.csv` and keeps undated files under an as-of.
- Breaking: `NationalFleet` filters interconnectors by status and uses current capacity, removing ~29 GW.
- Breaking: `UkpnSource` accepts the `ukpn-` filename prefix; `LtdsReader` honours demand `Removed` status; TEC
  prefers connected capacity; an empty `substations.csv` gives a note instead of a failure.
- Breaking: `EvidenceReport`, `ScenarioEngine` and `fleet` replay the whole recorded event window, not 30 s.
  Avoided-cost and device-benefit results for late-staged events change.
- Breaking: `SparseNewtonRaphsonPowerFlow` switches Q limits only after a converged sub-solve, matching the
  dense solver bit-for-bit.
- Breaking: `ThreePhaseSweep` returns `NonRadial` for meshed input; `BackwardForwardSweep` carries tap and phase
  on mesh links and reports `NonRadial` as non-converged.
- Breaking: `Complex2x2.Inverse` uses a relative singularity test; `GeneralEigen.Balance` no longer hangs on Inf;
  `BoundedSimplex` prices free variables both ways; `CoupledHarmonicPowerFlow` uses its full iteration budget.
- Breaking: relay 51 uses M = I/pickup, not I/rated; trip times near pickup change by up to ~23x.
- Breaking: the `ProtectionController` recloser waits for the last hold; `FaultAnalysis.FaultedFlows` stays
  index-aligned; `SequenceFaultStudy.Thevenin` rejects meshed models; relay 21 margins are negative when not
  operating.
- Breaking: `FrequencyDynamics` and `AssetFrequencyDynamics` start forming replays at the recorded frequency.
- Breaking: `NoiseFloorAnalysis` reads half-normal residuals correctly.
- Breaking: out-of-service branch measurements predict zero; `VaDeg`/`IAngDeg` residuals wrap to (-pi, pi];
  `EnsembleKalmanStateEstimator` propagates members; `LinearPmuStateEstimator` and `BranchCurrentDsse` honour
  observability and `InService`.
- Breaking: `CimExporter` round-trips loads, generators, base voltages and transformers; `MatpowerParser` reads
  newline-separated rows; `Iec104Frame`, `BridgeProtocol.PeekType` and `C37118Decoder` are corrected.
- CLI: `--name=value` flags and `--help` work; unknown cases and failed loads exit 2/1; parse errors exit cleanly.
- Web: `/ws/sim`, realtime file loading, catalog ids, bridge start errors and a `map-interop.js` leak are fixed.
- WPF: bad bridge or replay frames no longer crash the app; stale rows are pruned on model switch.
- GPU: ROCm availability checks hipsolver, rocsparse and rocblas; the CUDA driver probe finds the right driver.

### Security
- `publish.yml` closes a script-injection hole through tag or input values.

## [1.1.0]
### Added
- `gridsim interpose` receives SCADA/RTU frames, corrects and decodes them, maps them through a
  `gridsim-rtu-pointmap/1` crosswalk and emits `gridsim-measurements/1` windows and quarantined Parquet.
  `--protocol` selects the stack; `--list-protocols` lists them. Active: `iec104`, `modbus`.
- `IO/Telemetry/Iec104Asdu` decodes IEC 60870-5-101/104 ASDUs, including measured, counter and point types,
  quality octets and CP56Time2a; `IO/Telemetry/ModbusFrame` decodes Modbus TCP FC 3/4 responses.
- `Interpose/` subsystem: `IRtuProtocol`, `RtuProtocolRegistry`, `QualityFold` (quality bits to sigma),
  `TimeDecode`, `FrameCorrection`, `RtuPointMap`, `RtuMeasurementSetMapper` and `InterposePipeline`.
- `GridSim.Gda/Interpose/QuarantineLiveStore` writes GDA-shaped Parquet tagged `source_class="telemetry"` with a
  `_QUARANTINE.json` sidecar, kept out of the evidence lake.
- TCP transports `TcpDelimitedFrameTransport` (IEC 104) and `TcpMbapFrameTransport` (Modbus).
- Live windows become estimable only after passing the 169 h horizon; fresher windows are skipped.
- Documentation: `docs/rtu-interposer.md`, technical manual ch. 20, user manual ch. 21.

## [1.2.0]
### Added
- EMT time-domain solver (`Dynamics/Emt/`) using Dommel companion models with a factor-once nodal solve.
- `EmtNetwork` (with `FromGridModel`), `EmtEngine`, series, shunt, source and switch elements, and trapezoidal
  and backward-Euler rules. `EmtEngine.InitializeRest` gives correct step responses from rest.
- Switching and faults with Critical Damping Adjustment (`--no-cda` or `--rule be` to change).
- `gridsim emt [case] --dt-us N --duration S [--source] [--amp] [--freq] [--probe] [--event] [--rule]
  [--no-cda] [--out]` writes `timeseries.csv`, `summary.md` and `run-metadata.json`.
- `BergeronLine` travelling-wave lines; constant-impedance loads in `FromGridModel` (`IncludeLoads`, default on).
- `ThreePhaseEmtEngine` with 3x3 mutually coupled branches and `ThreePhaseFault`.
- Documentation: `docs/technical/design/emt-time-domain.md` and technical manual ch. 21.

## [1.3.0]
### Added
- `GridSim.Web` reaches feature parity with `GridSim.Wpf`, using the same control-room theme.
- Web UI primitives (`Components/Ui/`) and SVG visuals (`Gauge`, `Phasor`, `PqDiagram`, `FrequencyTrace`).
- Web GB network map (`NetworkMap.razor`, `map-interop.js`) with culling, colour modes, right-click trip,
  minimap, search and geographic overlays; optional `/geo` mapping for `gda-out/geo`.
- Web assets explorer with fleet registers and per-asset detail pages.
- Web tabs: HUMAN SCALE, HARMONICS (`SimSession.RunHarmonicsStudy`), and fuller THE MACHINE, NESO RP1,
  MECHANICAL and GENERATION & INERTIA tabs.
- Header status lamp, step-size picker (1 ms to 20 min via `SimSession.SetStepMinutes`) and substation search.
- Documentation: user manual ch. 4 covers the web dashboard.

### Changed
- Shared presentation constants moved to `GridSim.Core.Presentation` (`CarbonFactors`,
  `EnergyEconomicsConstants`, `ScadaThresholds`, `CredibleLossBasis`, `MechanicalThresholds`) and used by both
  dashboards.

## [1.4.0]
### Added
- Parallel analysis cycle (`GridSim.Core.Scheduling`): one base solve per cycle, then downstream analyses fan
  out across workers on the shared `SimFrame`. High-priority jobs always complete; low-priority jobs run within
  a `W x windowMs` core-millisecond budget and rotate.
- `PowerFlowOptions.Cancellation` aborts Newton-Raphson solves; the default token leaves results bit-identical.
  `ContingencyAnalysis.ScreenBranchesParallel` accepts a `CancellationToken`.
- `IBatchedLinearSolver` with `CpuBatchedLuSolver` and `GpuBatchedDenseSolver`, via `SolverBackend.DenseBatch()`.
- `gridsim cycle <case|--dir d> [--window-ms] [--workers] [--cycles] [--shards] [--jobs] [--hold-steady]
  [--pipeline]` (exits non-zero if a high-priority job misses) and `gridsim batchsolve [--count] [--n] [--runs]
  [--gpu]`.
- CYCLE tab in the Web and WPF dashboards via `ResidentCycleService`, configured by `Cycle:Enabled`, `WindowMs`,
  `Workers` and `Shards`.

## [1.5.0]
### Added
- `GridSim.Core.Estimation.PhaseAngleFeed` turns per-bus phase-angle observations into slack-referenced `VaDeg`
  rows; a common-mode system angle cancels exactly.
- `estimate --phaseangle BUS:ANGLE:UTC:FREQ` with `--phaseangle-ref auto|referenced|raw`, `--phaseangle-sigma`,
  `--phaseangle-at` and `--phaseangle-from-gda`. Injected rows obey the 169 h horizon.
- `PhaseAngleInferedReader` reads the GDA `PHASEANGLE_INFERED` system angle as the reference frame and
  common mode, not as a per-bus measurement.

## [1.6.0]
### Added
- Offline licence engine (`src/GridSim.Core/Licensing/`): ECDSA P-256 signed `.gridsim` keys carry email, org,
  tier, bus limit and dates, verified offline. Bundled networks are exempt.
- Licence resolution order: `GRIDSIM_LICENCE` / `Licence:Key`, then `%LOCALAPPDATA%\GridSim\licence.gridsim`,
  then the free Personal tier.
- `gridsim licence status|import|path` and `--importLicence <file.gridsim>`; over-limit or expired models are
  blocked on load.
- Web returns HTTP 403 on a licence denial and shows the tier; the desktop app shows it in the title bar.
- Documentation: manual ch. 23, technical ch. 22.

## [1.7.0]
### Added
- `Core/Analysis/LossAllocation` splits `TotalLossMw` per branch and per zone with a reconciliation residual.
  `gridsim losses <case>` and a LOSSES tab in both dashboards.
- `ContingencyResult.DeltaLossMw` gives the loss change for each N-1 outage; `gridsim nminus1-sweep --losses`
  and the CYCLE tab show it.

## [1.8.0]
### Added
- `ScreenNMinusK`, `ScreenNMinus2` and `ScreenNMinus1Minus1` contingency screening, with SC-DC-OPF redispatch as
  the default corrective action. CLI verbs `nminus2` (`n-2`) and `n-1-1` (`nminus1minus1`).
- k=1 is bit-identical to the existing N-1 sweep. Documentation: user manual ch. 6, CLI reference ch. 19,
  technical manual ch. 23.

## [1.9.0]
### Added
- Dynamic-model library (`Core/Dynamics/Models/`): `FirstOrderLag`, `LeadLag`, `Washout`, `Integrator` and
  `PiController`, using exact zero-order-hold discretisation.
- IEEE `Tgov1` turbine-governor and `Sexs` excitation system.
- `GovernedMultiMachine` adds governors to the classical multi-machine swing; with no governor it is
  bit-identical to the classical swing.
- `OneAxisMachine` (SMIB with varying `E'q`, saliency and AVR) and `MultiMachineFluxDecay` for networks.
- Documentation: technical manual ch. 24.

## [1.10.0]
### Added
- IEC 60909-0 short-circuit duty (`Core/Protection/ShortCircuit/Iec60909`): `ThreePhase`, `FromThevenin` and
  `VoltageFactor` give `Ik''`, peak `ip`, DC component and thermal `Ith` for balanced far-from-generator faults.
- Documentation: technical manual ch. 25.

## [1.11.0]
### Added
- `Iec60909.FromSequenceImpedances` gives SLG, LL and LLG duties from supplied `Z1/Z2/Z0`;
  `Iec60909.LineToLine` and `PositiveSequenceThevenin` compute LL faults from the model.

## [1.12.0]
### Added
- `Iec60909.BreakingFactorMu`, `MotorBreakingFactorQ` and `SynchronousBreakingCurrentKa` give near-to-generator
  breaking factors at the IEC break times. The steady-state factor `λ` is not offered.

## [1.13.0]
### Added
- `Iec60909.GeneratorCorrectionKg` and `TransformerCorrectionKt` impedance-correction factors. They are not
  yet applied inside `ThreePhase`.

## [1.14.0]
### Added
- Overcurrent coordination (`Core/Protection/Coordination/OvercurrentCoordination`): IEC 60255-151 and IEEE
  C37.112 inverse curves and definite time, `OperateTimeSeconds`, `Grade` and `GradeChain` against a CTI.
- Documentation: technical manual ch. 26.

## [1.15.0]
### Added
- `Generator.SubtransientXdPu` gives each machine its own `X″d` for `Iec60909` and `FaultAnalysis`; the default
  stays 0.20 pu, and cases without it are bit-identical.

## [1.16.0]
### Added
- `Generator.NegativeSeqXPu` and `Iec60909.NegativeSequenceThevenin`: `LineToLine` uses a real `Z2`. It
  defaults to `X″d`, so existing cases are bit-identical.

## [1.17.0]
### Added
- `WindingConnection` and grounding fields on `Branch` build the zero-sequence network;
  `Iec60909.ZeroSequenceThevenin`, `SingleLineToGround` and `DoubleLineToGround` work from the model.
- An ungrounded bus gives `Z0 = ∞` and zero earth-fault duty. Existing cases are unchanged.

## [1.18.0]
### Added
- `Iec60909.BreakerRating`, `CheckBreaker` and `BreakerAdequacy` check voltage, making, breaking and thermal
  ratings against a `Duty`, with signed kA margins.
- `Duty` carries `BreakTimeSeconds` and `FaultDurationSeconds`.

## [1.19.0]
### Added
- `Iec60909.PartialThreePhase`, `FaultContribution` and `MachinePartialCurrent` give each generator's partial
  fault current and the multi-machine breaking current.

## [1.20.0]
### Added
- `Iec60909.BranchFaultCurrents` and `BranchFaultCurrent` give each branch's three-phase fault current, which
  can feed `OvercurrentCoordination.GradeChain`.
- Documentation: technical manual ch. 25 and ch. 26.

## [1.21.0]
### Added
- Two-axis synchronous machine (`Core/Dynamics/Models/TwoAxisMachine`): adds the q-axis transient EMF `E'd` as a
  fourth state (`T'qo·dE'd/dt = −E'd + (Xq−X'q)·Iq`), transient reactances on both stator axes, and air-gap power
  `Pe = E'd·Id + E'q·Iq + (X'q−X'd)·Id·Iq`. Flux states use the exact zero-order-hold `FirstOrderLag`; the rotor
  swing is RK4. Reduces exactly to `OneAxisMachine` when `X'q = Xq`.
- Tests: added `TwoAxisMachineTests` (one-axis reduction, q-axis decay, equilibrium, determinism).
- Documentation: technical manual ch. 24.

## [1.22.0]
### Added
- Subtransient synchronous machine (`Core/Dynamics/Models/SubtransientMachine`), the GENROU/GENSAL-class model:
  adds the subtransient EMFs `E''q`, `E''d` behind `X''d`, `X''q`, cascaded off the transient EMFs, with
  `Pe = E''d·Id + E''q·Iq + (X''q−X''d)·Id·Iq`. Reduces exactly to `TwoAxisMachine` when `X'' = X'`.
- Tests: added `SubtransientMachineTests` (two-axis reduction, subtransient decay, equilibrium, determinism).
- Documentation: technical manual ch. 24.

## [1.23.0]
### Added
- Optional magnetic saturation on the subtransient machine (`SubtransientMachine.SaturationCurve`; null =
  unsaturated, unchanged). The scaled-quadratic curve `Se(x) = B·(x−A)²/x` is fitted to `Se(1.0)` and `Se(1.2)`
  and adds `Se(E'q)` to the field balance, so a saturated machine needs exactly `Se(E'q)` more `Efd` at equilibrium.
- Tests: added saturation cases to `SubtransientMachineTests`.
- Documentation: technical manual ch. 24.

## [1.24.0]
### Added
- Salient/subtransient multi-machine network interface (`Core/Dynamics/Models/SubtransientNetworkInterface`): dq
  current injection for two-axis and subtransient machines in a coupled network, generalising
  `MultiMachineFluxDecay.NetworkInjection`. With `X''` folded into the reduced admittance it needs no iterative
  solve; with every `E''d = 0` it matches `NetworkInjection` exactly.
- Tests: added `SubtransientNetworkInterfaceTests`.
- Documentation: technical manual ch. 24.

## [1.25.0]
### Added
- Salient/subtransient multi-machine integrator (`Core/Dynamics/Models/MultiMachineSubtransient`): every machine
  carries six states (rotor angle and speed, `E'q`, `E'd`, `E''q`, `E''d`) with its own reactances, time constants
  and optional saturation. Reduces to `ClassicalMultiMachine` to machine epsilon over a transient. The field is
  held per machine in this version.
- Tests: added `MultiMachineSubtransientTests`.
- Documentation: technical manual ch. 24.

## [1.26.0]
### Added
- Multi-machine AVR for the subtransient fleet: `SubtransientNetworkInterface.TerminalVoltageMagnitudes` recovers
  terminal voltage as `V_t = E'' − jX''·I`, and `MultiMachineSubtransient.Simulate` takes optional per-machine
  `Sexs` exciters. Omitting them keeps the held-field behaviour unchanged.
- Documentation: technical manual ch. 24.

## [1.27.0]
### Added
- Power-flow-based initialisation for the subtransient fleet (`Core/Dynamics/Models/SubtransientInit`), the
  subtransient analogue of `ClassicalInit`: seeds `MultiMachineSubtransient` at its six-state fixed point from a
  solved power flow, so runs from a real dispatch are stationary. With flat reactances it reduces to
  `ClassicalInit`.
- Tests: added `SubtransientInitTests` (WSCC-9 fixed point, classical reduction).
- Documentation: technical manual ch. 24.

## [1.28.0]
### Added
- Standard control models (`Core/Dynamics/Models/Ieeet1`, `Ieeeg1`, `HydroGovernor`, `Pss1a`):
  - `Ieeet1`: IEEE Type-1 DC exciter with self-excitation, exponential saturation and rate feedback.
  - `Ieeeg1`: tandem-compound steam governor with a four-stage steam train; steady droop `−1/R`.
  - `HydroGovernor`: transient-droop hydro governor with the ideal water column `(1−sTw)/(1+0.5sTw)`.
  - `Pss1a`: single-input power-system stabiliser with washout, two lead-lags and output limits.
- Tests: added `ControlModelFamiliesTests`.
- Documentation: technical manual ch. 24.

## [1.29.0]
### Added
- `MultiMachineSubtransient.Simulate` takes a per-step `mechanicalPower(machineIndex, speedDeviation)` hook for
  turbine-governors and a piecewise `NetworkStage` schedule for fault-then-clear or branch-trip sequences. A
  two-stage schedule reproduces the single fault window exactly.
- Documentation: technical manual ch. 24.

## [1.30.0]
### Added
- RMS transient-stability study (`Core/Dynamics/Models/RmsTransientStability`) on the subtransient fleet: seeds
  from a solved power flow, applies a scheduled bolted three-phase bus fault and integrates the six-state
  machines. Gives a full run (trajectories and synchronism verdict) and a critical clearing time by bisection.
  With reactances collapsed to `X'd`, the CCT on WSCC-9 matches `ClassicalInit.CriticalClearingTimeSeconds` to a
  few milliseconds.
- `SubtransientInit` takes an optional `faultBusId` for the faulted reduced admittance.
- Tests: added `RmsTransientStabilityTests`.
- Documentation: technical manual ch. 24.

## [1.31.0]
### Added
- `gridsim stability --rms` runs the subtransient RMS engine: `--screen <case>` gives the critical clearing time
  at every generator bus, `--fault-bus N <case>` at one bus. GENROU parameters are synthesised from X'd, so the
  output is flagged INDICATIVE.
- Documentation: user manual ch. 19.

## [1.32.0]
### Added
- `DenseComplexLuSolver.FactorDense` (factor once; identical results to `SolveDense`).
### Fixed
- Converter-interfaced plant (batteries, PV, wind, VSC links) no longer overstates fault levels; it was modelled
  as an EMF behind `X''d` (about 5 pu instead of about 1.2 pu). `Generator.IsConverterInterfaced` and
  `ConverterFaultCurrentPu` (default 1.2) make `Iec60909` treat it as an IEC 60909-0:2016 current source
  (`Duty.ConverterIkppKa`), and `FaultAnalysis` / the `fault` verb as a current-limited injection with K = 2 and
  reactive priority (`FaultResult.Converters`).
- Fault levels on cases with converter plant (GB, GDA) fall; earlier figures there were too high.
  Synchronous-only models are unchanged.
- Tests: added `ConverterFaultContributionTests`.
- Documentation: technical manual ch. 25.

## [1.33.0]
### Added
- Fault studies run on gb-full: `FaultNetworkSystem` replaces the 1,500-bus dense cap in `Iec60909` and
  `FaultAnalysis`, using dense LU up to 1,500 buses (identical results) and sparse LU above.
  `gridsim fault data/gb-full --bus N` takes about 6 s.
- `generation.json` fields `converterInterfaced` and `converterFaultCurrentPu` (nullable; `0` = no fault current).
- Tests: added `FaultNetworkSystemTests` (dense vs sparse on IEEE 118/300).
### Changed
- LCC HVDC links (Moyle/Auchencrosh, IFA, BritNed) are tagged `converterFaultCurrentPu: 0` in `gb-full`,
  `gb-synthetic` and `uk`; the Isle of Man HVAC link is non-converter in `gb-full`.
- Known limitation: gb-full 400 kV fault levels remain about 80-95 kA, driven by power-flow control units, the
  266 GVA slack and machines without unit transformers.

## [1.34.0]
### Added
- Converter plant in the RMS engine: converter-interfaced generators become REGC_A/REEC-style grid-following
  current sources (`RmsConverterState`) on retained buses (`ConverterNetwork`), with K = 2 reactive boost, a
  `k Ir` current limit with reactive priority, LVACM, active-current freeze and ramp-limited recovery.
- `SubtransientInit.FromSolvedCase(..., convertersAsCurrentSources: true)` is the default; `false` gives the old
  behaviour. `MultiMachineSubtransient.Result.Converters` gives per-unit traces.
- Tests: added `RmsConverterTests`.
- Documentation: technical manual ch. 24.
### Changed
- Known limitation: RMS on gb-full is not yet possible; power-flow-only `grid` units carry `H = 0`, and the dense
  engine does not scale to 3,324 machines plus 1,994 converter units.
### Fixed
- `stability --rms` refused any case with converter plant (`H = 0`); it now runs them (e.g. `uk`).
- The CLI named case `gb-full` built the synthetic GB model; it now loads the real `data/gb-full` case.
  Use `gb-synthetic` for the synthetic one.

## [1.35.0]
### Added
- RMS stability runs on gb-full: the kernel integrates against an `IRmsNetwork`, and large cases use
  `SparseRmsNetwork` (full network, machines as Norton sources behind X''). It agrees with the Kron-reduced model
  on WSCC-9 to 1e-6 rad and 5 ms CCT. A 150 ms gb-full fault study takes about 11 s.
- `stability --rms <case> --fault-bus N --clear MS` runs a single fault-and-clear study.
- `Generator.PowerFlowOnly` and `Generator.UnitTransformerXPu` (with JSON fields). gb-full marks its
  voltage-control units and national slack as power-flow-only, its zero-inertia `other` units as
  converter-interfaced, and its synchronous units with a 0.15 pu unit transformer. The Bramford fault level falls
  from 81 to 61.9 kA.
- LCC HVDC in RMS as a blocking converter (`BlockInDip`), never aggregated with VSC units on a bus.
- An island with no machine gets a stiff reference at its slack or PV bus.
### Fixed
- Converter k averaging let an LCC link (k = 0) choke co-located VSC units, and a lone LCC unit lost its power
  even undisturbed.
- Reactive power of co-located units was split by active power, overloading small machines beside voltage-control
  units (gb-full small-CHP instability).
- The `FaultAnalysis` converter fixed point uses adaptive damping; with about 3,300 converters it failed to
  converge.
- Synchronous-typed units with no inertia are held as constant injections (and listed) instead of failing the
  study.
- `stability --rms` uses the sparse power flow above 3,000 buses.

## [1.36.0]
### Added
- Grid-following converters in the three-phase EMT engine (`ThreePhaseConverter`): an average-value,
  PLL-synchronised current source with the RMS converter's control law, an AC filter capacitor and a soft start.
- Tests: added `EmtConverterTests`.
- Documentation: technical manual 21.8a.

## [1.37.0]
### Added
- Battery energy storage, chemistry-first (`GridSim.Core/Storage/`):
  - `CellModel`: equivalent circuit with OCV(SoC,T), R0, two RC pairs, Arrhenius scaling, coulombic efficiency,
    self-discharge and Peukert.
  - `PackModel`: series/parallel, BMS limits, lumped thermal model with HVAC, auxiliary load and state of power.
  - `AgeingModel`: calendar and cycle fade, resistance growth, ASTM E1049 rainflow counting.
  - `BatteryChemistryLibrary`: LFP, NMC, LTO, Na-ion, VRFB, lead-acid; all parameters are `CuratedPlaceholder`.
- `IDcSource` behind the grid-following and grid-forming inverter assets: `SocBucket` (unchanged behaviour) or
  `BatteryDcSource`, selected by the new `batteryChemistry` asset parameter.
- Tests: added `StorageChemistryTests` and `BatteryAssetTests`.
- Documentation: technical manual ch. 27.

## [1.38.0]
### Added
- `RmsConverterUnit.Store`: a battery pack (or any `IDcSource`) behind an RMS converter caps and drains the
  active current; reactive support is unaffected. Documentation: technical manual 27.5a.

## [1.39.0]
### Added
- Storage through the time series: `StorageFleet` carries battery sites (SoC, temperature, ageing) step to step
  and injects the delivered power plus auxiliary load into each power flow. Used by `chain`
  (`--bess chem:bus:MW:MWh[:soc]`, repeatable), `LiveSim` (`storage:`; `SimFrame.Storage`) and `gda-replay`
  (`--bess`, output tagged Counterfactual).
- Dispatch policies (idle, peak-shave, price threshold, response holding) and `StorageLpScheduler`, a linear
  programme against a price series using the pack-derived `StorageEnvelope`. `SyntheticPrices.GbDay` is a
  labelled synthetic price day.
- Tests: added `StorageTimeSeriesTests` and `StorageLoopTests`.
- Documentation: technical manual 27.5b.
### Fixed
- `PackModel.Step` could carry SoC past its window within a step; the current is now capped at the edge (BMS
  taper).

## [1.40.0]
### Added
- Grid-forming converter in the RMS engine (`Generator.GridForming`, JSON `gridForming`): a virtual synchronous
  machine behind a virtual reactance with virtual inertia and damping, an integral voltage loop and a current
  limit, on dense and sparse networks.
- Tests: added `RmsGridFormingTests`.
- Documentation: technical manual ch. 24.
### Changed
- Network-constrained multi-site storage scheduling is deferred to a future release (it needs a sparse LP solver
  at gb-full scale).

## [1.41.0]
### Added
- Grid-forming converter in the three-phase EMT engine: an EMF behind the virtual reactance with the shared swing
  and voltage loop, power measured from terminal current, and a current limit armed after start-up with
  hysteresis.
- Tests: added `EmtGridFormingTests`.
- Documentation: technical manual 21.8a.

## [1.42.0]
### Fixed
- The classical `stability` screen refused cases with converter plant, power-flow-only or zero-inertia units.
  `ClassicalInit.FromSolvedCase(..., holdNonMachines: true)` holds them as constant admittances, with
  `Prepared.MachineIndices` mapping back to `model.Generators`; the CLI opts in and uses the sparse power flow
  above 3,000 buses. `stability uk` now runs (787 ms CCT). The classical screen is still slow on gb-full; use
  `stability --rms` there.

## [1.42.1]
### Changed
- Internal: updated workspace paths in the manuals, technical documentation, tooling defaults and a test's
  default report directory.

## [1.95.000]
### Changed
- Version scheme: hard break to 1.95.000. Every change now bumps the last three digits. The assembly version
  carries the same number without leading zeros (1.95.001 = 1.95.1).
- Internal: added planning records for a review and fix programme. No code or numbers changed.

## [1.95.001]
### Fixed
- Dense and sparse Newton-Raphson are bit-identical on the CPU across every voltage, angle, flow, loss, mismatch,
  iteration count and status, on all bundled cases including gb-full and PEGASE 1354/2869. Previously they
  differed in the last bits, in iteration counts and in end states of non-converging cases.
- The Newton step is shared (`NewtonShell.EquationPositions`, `SolveStep`, `ApplyStep`) and uses the CPU direct
  sparse LU. Sparse results are unchanged; dense results move by at most about 1e-14. GPU backends are outside
  the bit-identity contract.
- Step damping and the voltage floor are named constants (`NewtonShell.MaxUndampedStep` = 0.4, `VmFloorPu` =
  0.2) and apply to both engines.
- The dense engine no longer iterates on a NaN mismatch; a non-finite step stops the solve with status
  `NonFinite`.
- The dense Jacobian uses `Math.SinCos` like the sparse refill.
- `ComplexCsr.Builder` sorts each row stably and starts every cell from +0.0, like `YBus.Build`; rows with more
  than 16 entries were summed in a different order.
- Tests: added `DenseSparseParityTests` (exact bit comparison, dense vs sparse, across the bundled cases).
- Documentation: technical manual ch. 3, `architecture.md`, `case-libraries.md`.

## [1.95.002]
### Fixed
- Harmonic studies model machine source impedance. `HarmonicModelOptions.IncludeGeneratorSources` stamps each
  synchronous machine as `1/(j*h*X)` to ground, with `X` = X2 (default X''d) plus unit transformer; converter and
  power-flow-only units are not stamped. An invalid reactance throws, naming the unit.
- `HarmonicModelOptions.Study` (machines plus load damping shunts) is now used by the CLI `harmonics` verb, the Web
  HARMONICS tab and the scheduled `harmonics` job. Results change substantially, e.g. case118 worst THD 19.903% ->
  2.527%, IEEE-519 compliant buses 42/118 -> 115/118; uk 32.596% -> 7.175%. The bare builder default is unchanged.
- A singular harmonic network is an error, no longer reported as 0% THD and Compliant:
  `SingularHarmonicNetworkException` names the lowest singular order, and the CLI exits 1.
- The harmonic export gains an optional `harmonicModel` block; `tools/crossval/crossval_harmonics.py` checks the
  new model independently.
- Tests: added `HarmonicSourceImpedanceTests`.
- Documentation: technical manual ch. 19, user manual ch. 20.

## [1.95.003]
### Fixed
- Balanced harmonic studies use the correct sequence network (`HarmonicModelOptions.BalancedHarmonicSequences`, on
  in `HarmonicModelOptions.Study`). Negative-sequence orders see transformer phase shifts reversed; triplen
  (zero-sequence) orders are refused or carry zero voltage, and the impedance scan skips them. Non-integer orders
  are refused in this mode.
- Load shunts: a net-exporting bus (Pd < 0) adds no conductance, and a leading load (Qd < 0) is a capacitor whose
  susceptance grows with h.
- On the bundled cases every THD and compliance verdict is unchanged; only the scan moves where its peak was a
  triplen (case118 resonance h21 -> h23).
- The export records `balancedHarmonicSequences`; `crossval_harmonics.py` applies the same rules.
- Tests: added `HarmonicBalancedSequenceTests`.
- Documentation: technical manual ch. 19.

## [1.95.004]
### Added
- Fine harmonic frequency sweep (`HarmonicFrequencySweep.DrivingPoint`): driving-point |Z| over a grid of
  frequency multiples (default h = 1.5 to 50, step 0.1), with the largest peak refined by golden-section search.
  The CLI `harmonics` verb prints the refined peak; the export gains `resonanceSweep`. Example: gb-full h5 |Z|
  112.064 pu -> h 4.9818 (249.09 Hz) 157.445 pu.
- `FrequencyDependentYBus.BuildSequence` (positive- or negative-sequence network at any frequency multiple).
- A sweep refuses invalid bounds, steps, grids over 100,000 points and unknown buses; a singular point throws
  `SingularHarmonicNetworkException`.
- Tests: added `HarmonicFrequencySweepTests`.
- Documentation: technical manual ch. 19.

## [1.95.005]
### Fixed
- A bus with no valid `BaseKv` is no longer certified at the most lenient harmonic limits: it is judged at the
  strictest tier, is never `Compliant`, and has `BusComplianceResult.VoltageLevelKnown = false`. New
  `HarmonicCompliance.G55Verdict` applies the same rule to G5/5, including in the Web HARMONICS tab. The CLI warns
  with a count; the export carries `voltageLevelKnown`. case14 and case57 (no base kV) now have no certified buses.
- Tests: added `HarmonicComplianceUnknownKvTests`.
- Documentation: technical manual ch. 19.

## [1.95.006]
### Added
- CLI `experimental harmonic-kcl <case>` and `experimental passivity <case> [--bus ID]`; exit code 1 on a
  violation or a singular network.
- Tests: added `ExperimentalLawNumberTests`.
- Documentation: technical manual ch. 16.
### Fixed
- Experimental physics laws have unique numbers from `PhysicsCheckRegistry` (11 reciprocity, 12 harmonic KCL, 13
  Laplacian row sum, 14 grid passivity, 15 converter non-passive band, 16 COI null mode). The COI null mode moves
  from 12 to 16.
- `ImpedancePassivityCheck.AuditGridPassivity` throws when every swept frequency is singular instead of reporting
  the network passive, and states how many frequencies it skipped.

## [1.95.007]
### Changed
- One dense/sparse rule for every power-flow entry point: new `SolverSelector.ResolveNewton` / `CreateNewton`; the
  CLI routes through `SolverBackend`, replacing its own 1,000-3,000-bus thresholds with the 200-bus
  `SolverSelector` rule. The RMS and classical stability verbs now use the selected backend and honour `--gpu`.
- Results on the CPU are unchanged. Large cases are faster, e.g. case2869pegase about 12.7 s -> 1.0 s. With
  `--gpu`, cases of 201-3,000 buses now use the GPU sparse backend.
- Radial-to-sweep routing is unchanged.
- Tests: added `SolverRoutingTests`.
- Documentation: `architecture.md`, technical manual ch. 3, user manual ch. 3.

## [1.95.008]
### Changed
- With no solver supplied, `ContingencyAnalysis`, `LossContingency`, `AcOptimalPowerFlow`,
  `ContinuationPowerFlow`, `TapOptimizer` and `RestorationSequencer` use `SolverSelector.CreateNewton(model)`
  (sparse above 200 buses) instead of always dense. The parameterless `SteadyStateSolver()` (REST `/solve`, viz
  export, bench) picks the engine per model. CLI `impedance-stability`, `stability --screen`, `modal` and
  `experimental modal-consistency` use the selected backend and honour `--gpu`.
- Results on the CPU are unchanged. A full sequential gb-full N-1 drops from about 16 hours to about 12 minutes.
- Tests: added `DefaultEngineRoutingTests`.
- Documentation: technical manual ch. 23.

## [1.95.009]
### Fixed
- When a GPU sparse solve (`GpuSparseLinearSolver`, `RocmSparseLinearSolver`) gives up, the sparse Newton engine
  now retries on an exact CPU direct LU before the ILU0/BiCGSTAB fallback, matching the CPU result to the bit.
  The CPU path is unchanged.
- Tests: added `GpuFallbackTests`.

## [1.95.010]
### Fixed
- A non-converged state can no longer flow into a result reported as success. New
  `ConvergenceContract.RequireConverged` throws `NonConvergedStateException` at the start of fault analysis,
  protection assessment and cascade, loss-contingency sweeps, small-signal assembly, subtransient initialisation
  and RMS transient studies.
- OLTC loop (`SteadyStateSolver`): never steps a tap on a non-converged or NaN solve; new `TapsSettled`.
- Q-limit outer loop: reaching `MaxQLimitPasses` right after a bus type switch reports `MaxIterations`, not
  Converged; `MaxQLimitPasses` below 1 is refused.
- DC-OPF is feasible only on an Optimal security LP; new `DcOpfResult.SecurityLpStatus`.
- LiveSim hold-steady and `GdaReplayLoop` warm-start only from a converged state.
- Viz export frames carry `Converged`, and `FreqHz` is the model's nominal frequency (was fixed at 50 Hz).
- Contingency screens record the actual solver error (`solver error: <type>: <message>`) instead of "islanded / no
  solution", and let `OperationCanceledException` through.
- Tests: added `ConvergenceContractTests`.
- Documentation: technical manual ch. 3.

## [1.95.011]
### Fixed
- CLI: every reported non-convergence now exits 3 (breaking for scripts that relied on 0, 1 or 2). This covers the
  default solve report, the synthetic-GB solve, `gda --solve`, `--all-gsps`, `serve`, `chain`, `conformance`,
  `impedance-stability`, the stability and modal verbs, the experimental models and the loss sweep.
- `fault` and `protection-check` refuse a non-converged pre-fault state with exit 3.
- `chain` keeps the last converged state as its seed.
- `conformance` counts cases that stop converging or fail to load as failures.
- The default solve report crashed on a non-converging case in 1.95.010; it now skips the protection overlay with
  a message and exits 3.
- Tests: added `CliExitCodeContractTests`.
- Documentation: user manual ch. 19.

## [1.95.012]
### Fixed
- GDA OLTCs regulate the right end: transformer branches are built HV -> LV, so the OLTC controls the untapped
  end; flows are reported with "from" on the HV side. Hand-built Core models with a From-side controlled bus are
  not yet handled.
- OLTC limits use the published LTDS tap range; off-load units and invalid ranges get no OLTC (invalid ranges
  are warned and counted). Zero-impedance non-busbar circuits are counted (`ZeroImpedanceNonTieCircuits`).
- Only connected embedded generation enters the model: new `GdaBuildOptions.ConnectedGenerationOnly` (default
  true; CLI `--all-gen` turns it off). Excluded MW is reported.
- The national fleet counts only connected Embedded Register projects, at connected MW.
- `--minkv` folds demand and embedded generation below the cut onto the nearest retained bus instead of dropping
  it.
- Embedded generation joins on (GSP group, primary), falling back to name only when unique; ambiguous names are
  left unplaced with a warning.
- Results on real GDA data change; rebuild with `gridsim gda`.
- Tests: added `GdaBuildDataContractTests` and `NationalFleetEmbeddedTests`.
- Documentation: technical manual ch. 9, user manual ch. 19.

## [1.95.013]
### Changed
- Documentation: brought user manual ch. 9-13 (counterfactuals, state estimation, estimated replay, bridge) in
  line with the code, including the 200-bus sparse switchover, `--method feasible`, `evidence --voll-source` and
  full exit codes.

## [1.95.014]
### Changed
- Documentation: brought user manual ch. 5-8 (analysis views, contingency, frequency, event replay) in line with
  the code, including the tab list, contingency error rows, `nminus1-sweep --losses`, `chain --bess` and exit
  codes.

## [1.95.015]
### Changed
- Documentation: brought user manual ch. 20-23 (harmonics, interposer, EMT, licensing) in line with the code; the
  harmonics chapter is rewritten for the new study model, and `gb-full` needs a licence for at least 3,539 buses.

## [1.95.016]
### Changed
- Documentation: brought technical manual ch. 5-6 (frequency dynamics, state estimation) in line with the code.

## [1.95.017]
### Changed
- Documentation: brought technical manual ch. 7, 8, 13 and 14 (evidence engine, provenance and invariants, asset
  dynamics grounding, blind prediction) in line with the code, with a new "Numerical invariants" section.

## [1.95.018]
### Changed
- Documentation: brought technical manual ch. 9-10 (GDA integration, file and wire formats) in line with the code,
  with an index of all 12 schema strings and new sections on the web API and visualisation scene.

## [1.95.019]
### Changed
- Documentation: brought technical manual ch. 11-12 (validation, glossary) in line with the code, with measured
  agreement figures and a table of the independent cross-check sidecars.

## [1.95.020]
### Changed
- Documentation: brought technical manual ch. 20-22 (RTU/SCADA interposer, EMT, licensing) in line with the code.

## [1.95.021]
### Changed
- Documentation: brought technical manual ch. 23-27 (contingency, dynamic-model library, IEC 60909, overcurrent,
  battery storage) in line with the code.

## [1.95.022]
### Changed
- Documentation: the CLI reference (user manual ch. 19) documents every dispatched verb, with a verb index, a
  "Solver selection" section, corrected stale entries, missing flags and exit codes for every verb.

## [1.95.023]
### Changed
- Documentation: brought technical manual ch. 0-4 (architecture, data model, power flow, sparse and GPU numerics)
  in line with the code.

## [1.95.024]
### Changed
- Documentation: brought user manual ch. 0-4 (getting started, loading networks, power flows, desktop and web
  apps) in line with the code, with a table of shipped JSON cases and their bus counts.

## [1.95.025]
### Changed
- Documentation: brought user manual ch. 14-18 (validation tools, exports and formats, configuration,
  troubleshooting, extending) in line with the code, with a new `gridviz.json` section.

## [1.95.026]
### Changed
- Documentation: brought technical manual ch. 15-19 (protections, physics conformance, protection assessment,
  real-time and distribution, harmonics) in line with the code.

## [1.95.027]
### Changed
- Documentation: the CLI reference's exit-code table states that any verb whose power flow did not converge exits 3,
  and the `stability`, `modal`, `impedance-stability`, `fault`, `protection-check`, `conformance` and experimental-model
  passages now say the same; the `--minkv` and `--all-gen` rows are kept. Technical manual ch. 9 gains sections on the
  in-repo GDA verbs and on the LTDS builder and its data contract. No code changed.

## [1.95.028]
### Changed
- Known limitation: grid-forming units are not yet included in the subtransient RMS synchronism verdict.
- Known limitation: the storage LP policy can still schedule simultaneous charge and discharge.

### Fixed
- Fixed the subtransient RMS kernel reporting a NaN or diverged run as `StayedInSynchronism`; non-finite angles or
  speeds now fail the check, as in the classical kernel.
- The subtransient RMS kernel counts steps whose converter algebraic iteration hit its 50-pass cap
  (`ConverterIterationFailures`, `ConverterIterationsConverged`) instead of passing them silently.
- AC-OPF reports converged only when the loss iteration settled and the final DC-OPF dispatch is feasible; it used to
  return `Converged = true` for an unsettled loop and ignored `dc.Feasible`.
- Continuation loadability reports a singular Jacobian at the nose as non-convergence instead of throwing (fixed in
  1.95.001; now covered by a test).
- The sequence fault study throws, naming the bus, for a bolted fault at the infinite-bus source (zero loop impedance)
  instead of returning NaN currents.
- The ensemble Kalman estimator computes its validity flag (state and variances finite) instead of hard-coding true,
  matching the EKF; `DseEstimate.Finite` names what the flag means.
- WLS estimator: new tri-state `EstimationResult.ChiSquareTest` (NotRun / Passed / Failed). A test that never ran
  (unobservable, non-converged, non-finite, detection off, no redundancy) used to be recorded and printed as PASS; the
  audit trail, the CLI (`estimate`, `serve`) and the export now show n/a, and the FDIA screen refuses a non-converged
  estimate or one whose test did not run. `ChiSquarePassed` keeps its documented meaning.
- Feasible WLS also requires the unscaled Gauss-Newton correction to be below tolerance whenever the interior guard
  scaled the step down; a step frozen at zero by the guard was read as converged. Unchanged when the guard does not bind.
- Storage LP policy (`--policy lp`) uses the schedule only when the LP is Optimal; otherwise the run stops with the LP
  status.
- Tests: added `ConvergenceContractStudyTests`.

## [1.95.029]
### Changed
- The Web and WPF apps stay presentation only: the new rules live in Core (`InfeedSelection`,
  `InterconnectorCorridors`, `UnitRampHistory`, `AssetLookup`, `DirectoryScan`) and both shells call them.
- Known limitation: the Web and WPF apps use different credible-loss rules.
- Known limitation: WPF circuit and interconnector detail pages are still opened by name.

### Fixed
- Map trip removes the largest generating unit, not the biggest nameplate: right-click / T trips the unit with the
  largest live output (Pg > 0; slack and voltage-control devices excluded; model order breaks ties) via
  `InfeedSelection.LargestOnline`, in both shells. On gb-full the T key injected a 266,424.9878610732 MW loss (the slack
  balancer's nameplate); it now trips Drax Power Station biomass at 2,353.5692490619185 MW. A bus with nothing
  generating trips nothing and says so. Fleet markers and the fly-out leave power-flow-only devices out.
- WPF single-letter map shortcuts are ignored while focus is in a text box, password box or combo box, and for
  Ctrl/Alt chords; typing 't' in the substation search no longer trips the largest infeed.
- WPF map animation advances by the time since the previous animation step, so dashes and the wave run at the
  documented speed (4x faster than before; the ~15 fps throttle is unchanged).
- `/ws/sim` delivers each start's topology, not only the first; frames from a replaced session are dropped (new
  `SimOutbound` queue with a per-start generation).
- HARMONICS RUN STUDY reports a failed study instead of hanging on RUNNING: an ungrounded network
  (`SingularHarmonicNetworkException`) shows STUDY FAILED with the engine's message (`HarmonicsDto.Error` / `Failed`).
- NESO RP1 correlated corridors recognise the model's interconnector names. On gb-full the correlated group was "" at
  0 MW with no links flagged; it is now Sellindge (IFA+ElecLink) at 1,494 MW with 4 links flagged; North Sea sums
  1,398.902467345442 MW. Credible loss, purchased time and grade are unchanged.
- MECHANICAL ramps are keyed by unit index: steady namesakes on gb-full showed +/-0.4895 MW/min phantom ramps, now
  exactly 0. Ramps follow the STEP picker: after 20 min -> 1 s, +1 MW read 0.05 MW/min, now 60 MW/min.
- The WPF asset explorer keys units and branches by index. On gb-full: import links 11 -> 12 rows (both Auchencrosh),
  generator register 174 -> 200 distinct rows, 75 of 2,620 transformers now show their own flow (mean loading
  23.95193271490783 % -> 23.899817181227533 %, displayed 24 % either way).
- OPEN IN ASSETS opens the drilled substation.
- Auto-layout ignores out-of-service branches; no bundled case changes.
- The EXPERIMENTAL carbon card shows "- (skipped on this large network)" instead of "0 gCO2/kWh" when tracing is
  skipped.
- Trip controls appear only on live in-process sessions; a `/ws/sim` trip on a replay or bridge session returns an
  error frame.
- Model catalogues survive unreadable `gda-out/` and `results/` folders and report them.
- Tests: added tests for infeed selection, the outbound queue, harmonics failure reporting, interconnector corridors,
  ramp identity, asset lookup, layout, the carbon label, trip availability and directory scans.
- Documentation: user manual ch. 4.

## [1.95.030]
### Changed
- Interposer bindings gain `dataType` (AsDecoded/U16/S16/U32/S32/F32) and `wordOrder`: a -120 MW S16 register
  (0xFF88) read +65416, now -120.
- API: `PhasorDataConcentrator.Ingest` returns bool.
- The JSON case loader refuses unknown or misspelt keys, naming the file; every shipped case loads.
- `GridModel.CopyWith` carries every property, and every internal copy site uses it (unit normaliser, contingency and
  loss outages, restoration, measured-injection prior, GDA per-bus replay, GDA geolocator). No numeric change.
- Known limitation: the restoration model carries no OLTCs.

### Removed
- API: `ClockSkewToleranceMinutes` (the bridge client's 15-minute clock-skew allowance); the exact 169 h horizon now
  applies.

### Fixed
- Presolved replay reads a naive `time_utc` as UTC: on a BST machine 12:30 used to read as hour 11.5, now 12.5; an
  unreadable stamp throws instead of reading as midnight.
- Bridge client: one unreadable message no longer ends the receive loop (it is counted in `RejectedMessages`, raised
  on `MessageRejected`, shown in Web and WPF, and skipped; a message with no type is an error). Frames 168 h 45 min
  old are no longer accepted. An Unspecified DateTime is UTC (`UtcTime.Normalise`): a BST machine sent 12:00 as 11:00Z
  and now sends 12:00Z, and audit hashes no longer depend on the time zone.
- The solve export round-trips the network state: branch `inService`, `r0`/`x0`, winding connections and neutral
  reactances, bus `zip`, generator `pmin`, `oltcs` and `boundaries` (schema /1, additive). A post-outage export
  re-imports with its opened branch out of service, and a re-solve of the re-import matches exactly. The crossval
  sidecars honour `inService` and `pmin`.
- The bridge backfills parallel circuits in order: three unnamed twins read A1/500 MVA each; now A1/500, A2/400,
  A3/300.
- Interposer: one measurement per address per window (a point reported N times was N measurements at the same sigma);
  Modbus responses paired with their request get absolute addresses (`MB:U<unit>:<fc>:@<register>`), and a second
  differently sized positional block on the same unit/fc is refused.
- C37.118 FORMAT flags follow IEEE C37.118.2 (PhasorPolar=1, PhasorFloat=2, AnalogFloat=4, FreqFloat=8; they were
  reversed). The PDC drops samples for an already-emitted instant (`LateSamples`) instead of emitting a duplicate
  superframe.
- A three-phase load on an unknown bus is refused (it was dropped and the sweep reported Converged). An EMT switch
  event on a non-switch element throws, and a no-op switch event no longer triggers the damping burst and re-factor
  (that run is bit-identical to having no event).
- N-1 shards resume across cycle deadlines: previously 0 of 8 shards ever finished on gb-full; a shard cut off after 4
  of 9 outages now solves only the remaining 5 next cycle. Cancelled runs no longer feed the cost estimate;
  uninterrupted results are bit-identical. Shards track branches by identity.
- GDA readers are deterministic: one csv as before, several by the strictly newest date in the file name, otherwise
  an error naming the candidates (never file times). Parquet parts merge columns and stay row-aligned. UKPN and NPg
  find the newest demand-year column by pattern and throw when there is no demand (a 2025/26 table gave zero demand).
- CIM import merges multi-profile CGMES by mRID with one node view, keeps three-winding transformers (star point in the
  bridge) and lists what it leaves out (`CimNetwork.Diagnostics`); the bridge fails loud on an unknown node.
- Tests: added tests for replay UTC handling, bridge robustness, export round-trips, interposer windows and Modbus
  types, C37.118 bit order, input validation, strict JSON keys, model copies, resumable shards, GDA inputs and CIM
  multi-profile import.
- Documentation: manual ch. 6 and 13, technical ch. 8, 9, 10, 12 and 20 describe the fixed behaviour.

## [1.95.031]
### Changed
- New `GridSim.Core.Licensing.BundledNetworks` holds the one list of bundled networks (matpower, uk, gb-synthetic,
  gb-reduced, gb-full) used by the CLI, WPF and Web. Matching is on whole path segments, so `data/ukx/...` or
  `data/gb-fullish` no longer count as bundled.
- Value flags with no value (`--minutes`, `--out`, `--catalog` and every other value flag as the last token, or
  followed by another `--flag`) exit 2 instead of running on the default. Number flags refuse `NaN` and `Infinity`.
- Enum flags reject typos: `emt --rule`, `impedance-stability --converter` and `--criterion` accept only trap/be,
  gfl/gfm and siso/mimo; `harmonics --bus` with a non-integer exits 2 instead of scanning the worst-THD bus. An unknown
  `--gpu=` vendor exits 2 instead of auto-detecting.
- Batch verbs (`transient --cases`, `nminus1-sweep`, `stress`) exit 1 when some cases fail to load; the other cases
  still run and an existing exit 3 (non-convergence) is kept.
- `crossval`: `crossval.py` reports an absent pandapower with exit 77 (skip); a Python exit 2 (usage error, script not
  found) gives exit 3 instead of reading as "never ran".
- Known limitation: an expired paid licence denies models that the free tier allows, and the shipped `data/intl`
  starter maps are not bundled networks.

### Fixed
- The free tier no longer refuses the shipped gb-full case in the CLI: every CLI verb on the 3,539-bus case exited 2
  with the 1,500-bus limit error while the Web and WPF apps loaded it.
- Every `.m` load is unit-normalised in one place: `Gated.Matpower` parses, repairs raw ohm / kW units, then checks the
  licence. Before, crossval, stress, chain, nminus1-sweep, cycle, losses, the named-case fallback and
  bench/conformance solved a raw feeder un-normalised (case33bw demand read as 3,715 MW instead of 3.715 MW). `losses
  dist/case33bw.m` now reports 0.20 MW total loss (textbook: 202.7 kW). Every per-unit case is bit-identical.
- `transient` refuses `--dt-ms <= 0` and `--minutes <= 0`: `--dt-ms 0` ran forever, and a negative step or
  `--minutes 0` wrote "frequency nadir 999.0000 Hz" with exit 0. A run shorter than one slice is refused, and the
  nadir starts as NaN.
- `tools/run-gpu.sh` no longer hangs and compares CPU with GPU: each solve uses `--enableExport=<file>`, the script
  diffs Vm/Va (tolerances 1e-6 pu, 1e-4 deg, worst differences printed to 17 significant digits) and exits 1 on any
  mismatch or failed solve.
- The frequency export writes `systemResponseServices` and `config.initialFrequencyHz` when present, so it carries an
  event's recorded response services and pre-event frequency; every service-free nominal-start export is
  byte-identical. `crossval_freq.py` reports NOT CHECKED with exit 77, naming the reasons, for such exports instead of a
  MISMATCH or a coincidental pass.
- `crossval_impedance.py` exits 77 (NOT CHECKED) when it made no comparisons (no `converterParams`: grid-forming,
  asset-class or real-network runs) instead of printing PASS. The `crossval.py` docstring no longer promises
  branch-flow diffs, and `crossval_estimation.py --bad-data` no longer claims to check that both tools removed the same
  measurement.
- Estimation sidecars handle angle and current measurements: `se_residual_check.py` evaluates `VaDeg`, `IMagPu` and
  `IAngDeg`; `crossval_estimation.py` feeds pandapower `VaDeg` as `va` and refuses the current kinds with exit 3. Both
  refuse any unknown kind with exit 3, naming it, instead of crashing with a KeyError.
- Tests: added `BundledNetworksTests`, `CliFailLoudFlagContractTests`, `FrequencyExportServicesTests` and
  `CrossvalSidecarExitContractTests`.
- Documentation: CLI reference parsing rules, validation tools chapter (crossval exit codes, measurement kinds),
  exports chapter (frequency export services block) and technical validation chapter (exit 77).

## [1.95.032]
### Changed
- New `GridSim.Core.Numerics.NumericTolerances` holds the shared numeric floors, each relative to the matrix scale, and
  every listed solver uses it. A pivot floor only decides what is declared singular, so no converged answer on a
  non-singular system changes.
- All per-fuel carbon figures live in one `CarbonIntensityTable`, read by both the presentation views and carbon
  tracing.
- Known limitation: the per-fuel carbon figures used by the presentation views and by carbon tracing still differ.
- Known limitation: the ROCm pivot check has not been run on AMD hardware.

### Fixed
- Sparse LU declares singularity at `|pivot| <= 1e-12 x max|A|` instead of an absolute 1e-300, and a NaN entry throws.
  A well-conditioned matrix with entries of 1e-301 now solves; a matrix singular to working precision is rejected at
  every scale. Householder QR rejects `|r_ii| <= 1e-12 x max|A|` instead of only an exact zero.
- ROCm dense LU rejects what the CPU and ILGPU reject: after getrf it checks `|U_kk| <= 1e-12 x max|A|`, and an
  unreadable devInfo throws.
- Estimator backends share one floor: covariance LU and the observability rank test still use 1e-12; the DSE inverse
  moves from 1e-14 to 1e-12 relative; the DSE Cholesky throws on a non-positive pivot instead of replacing it with
  1e-12; the critical-residual floor is still 1e-8.
- Constrained WLS uses one hard-constraint weight, `1e4 x max(free-row weight)`, for both the observability gate and
  the covariance (was an absolute 1e6 and 1e8), and throws when there is no free telemetry or a weight is not positive.
- The complex LU clamp is `1e-12 x max|A|` (was an absolute 1e-14): on diag(1e-20, 2e-20) the solution was (0, 0) and
  is now exactly (1, 1). The real BiCGSTAB default cap is `4n + 200` (was 2,000). `GeneralEigen`'s inverse floor moves
  from 1e-13 to 1e-12.
- The ILU(0) guard is relative to the row (`1e-12 x max|row|`), so a small-scale matrix keeps its exact factors.
- The Jacobi eigen stop is scale-relative (`(eps x ||A||_F)^2`), and running out of 100 sweeps or a non-finite input
  throws. For 1e-16 x [[2,1],[1,2]] the eigenvalues were (2e-16, 2e-16) and are now (1e-16, 3e-16).
- `GeneralEigen.Decompose` handles repeated non-defective eigenvalues (for example identical units) instead of throwing
  `DefectiveEigenproblemException`; a copy that misses the eigen-equation by more than 1e-6 of the matrix scale is
  still reported as defective. Distinct eigenvalues are unchanged.
- Complex BiCGSTAB uses conjugated inner products: with A = I, b = [1, i] it returned null and now solves.
- Tests: added `NumericTolerancesTests` and `CarbonIntensityTableTests`.
- Documentation: technical ch. 3, 4 (new "One tolerance table" section), 6, 12 and 25.

## [1.95.033]
### Changed
- Several power-flow, analysis and protection results change on bundled cases; dense and sparse Newton stay
  bit-identical to each other. Before -> after figures are given below.
- Known limitation: battery ageing is switched off by `Reset`.

### Fixed
- A PV bus with no in-service generator is solved as PQ, as MATPOWER does (also in the MATPOWER parser). Two bundled
  cases have such buses:
  - pglib_opf_case588_sdet (43 such buses), default options: loss 366.4792767199177 -> 365.19961761740956 MW, loss Q
    2.308929886764647 -> -22.583092958449477 MVAr, bus 9 Vm 0.9597388876517722 -> 0.9860022057942351, iterations
    12 -> 11. The phantom PV buses had changed which real units hit their Q limits. Q limits off: loss
    363.97206488521664 -> 366.6886238646117 MW.
  - pglib_opf_case30_as (buses 22, 23, 27), default: same solution in fewer passes, iterations 9 -> 6, loss
    8.49408881044796 -> 8.494088810448497 MW. Q limits off: loss 8.675385748156023 -> 8.584529013931478 MW, bus 22
    Vm 1.025 -> 0.9906577087564662.
- IEC 60909 networks drop line charging and bus shunts, as IEC 60909-0 directs. case9 Ik'' (pu): bus 1
  17.088638834774674 -> 17.534688310401165, bus 5 8.25267328250892 -> 8.774528526143634, bus 9 8.71053214396903 ->
  9.229188456464296. Bus 5 ip 3.386240815028396 -> 3.6032937549618294 kA. A singular dense fault network fails loud,
  and branch fault currents use the shared admittance stamp.
- Thermal (49) and watch-list loading use the heavier end, and distance (21) is evaluated at both ends, so verdicts no
  longer depend on branch orientation. case9 base loading: branch 6-7 0.16255854966721534 -> 0.2281204495509109,
  branch 4-5 0.12288376621852731 -> 0.1389219842894727, branch 5-6 0.40644245871064566 -> 0.4229713316356193, branch
  3-6 0.28563639048246287 -> 0.28768543420191456.
- The backward / forward sweep honours FlatStart (warm start from stored voltages when off, the default),
  EnforceQLimits, MaxQLimitPasses, Cancellation, ZIP loads, the shared tap rule and the slack angle; `LooksRadial`
  counts in-service branches only. case18 loss 0.26018795213929996 -> 0.2601879519754232 MW (iterations 12 -> 11),
  cigre_mv 0.30409911748639623 -> 0.3040991175878677 MW; case22, case33bw, case69, case85 and case533mt_hi are
  bit-identical.
- DC-OPF uses Pmin as given, so storage and pumped-storage charging can be dispatched; Pmin > Pmax or a non-finite
  Pmin throws. Units dispatched below zero (limits off / on): case1354pegase 0/0 -> 35/47, case240_pserc 0/0 -> 2/3,
  case588_sdet 0/0 -> 1/3. Total cost unchanged to the last digits.
- Unchanged on every bundled case: OLTC step direction for a From-side controlled bus (raise the tap to raise the bus);
  one voltage-setpoint rule, the last in-service generator at the bus, as MATPOWER; storage keeps the bus setpoint
  instead of forcing Vg = 1.0; one voltage-dependent load helper `LoadAtVoltage`; the physics audit keeps its branch
  alignment after a skipped branch; one voltage-limit table `Analysis/VoltageLimits` for contingency, watch list and
  restoration; a boundary limit of 0 means no limit and a negative or non-finite one throws; watch-list voltage
  deviation is measured from the regulated setpoint; restoration energises sequenced branches stored open, throws on
  unknown or repeated names and carries live OLTCs; a recloser with no target throws and
  `ProtectionModel.ValidateAgainst` checks a placement; MATPOWER frequency is explicit with a note when 50 Hz is
  assumed; hydrogen, fuel cells and electrolysers are no longer hydro; one Core `FuelTaxonomy`, with reciprocating
  engines as synchronous fossil.
- Tests: added `OltcFromSideTests`, `PowerFlowPhysicsConsistencyTests`, `AnalysisWs6Tests`, `ProtectionWs6Tests` and
  `ImportAndFuelWs6Tests`.
- Documentation: nine technical pages.

## [1.95.034]
### Changed
- `EstimationResult.ToPowerFlowResult` (used for the warm start, the Feasible gate, the estimate export and the FDIA
  screen) is sparse. It built dense n x n arrays: 400.8 MB and 12,524,521 pair evaluations at gb-full (3,539 buses). It
  now assembles the sparse Y as the sparse Newton solver does and touches at most 15,375 cells. Results are
  bit-identical to the dense form.

## [1.95.035]
### Added
- Every SIMD tier is tested on every machine: the three dispatching kernels (`SubtractScaledRow`, `AddScaledRow`,
  `Dot`) gain an internal explicit-tier overload, and the Scalar, Sse128, Avx256 and Avx512 arms are tested on any CPU
  (Vector512 runs in software where the CPU lacks it). Production overloads are unchanged.
- `SimdCapabilities.ExpectedTierFromHardware` and `Avx512Unused` (AVX-512F present but the runtime narrower, as on
  Skylake-SP / Cascade Lake).
- `gridsim simdcheck --expect <tier>` exits 3 when the active tier is narrower than expected (`--expect Avx512` on an
  AVX2 machine -> exit 3, `--expect avx256` -> OK, `--expect wide` -> exit 2). `simdcheck` warns when the CPU has
  AVX-512F that the runtime is not using, naming `DOTNET_PreferredVectorBitWidth=512`. No computed number changes.
- Documentation: CLI reference.

## [1.95.036]
### Changed
- `--synth-sources` selects the synthetic harmonic source explicitly and exits 2 when given with `--spectra`
  (`--spectra` used to win silently). `crossval --tool` accepts only `pandapower`.
- `impedance-stability` refuses a flag the selected model ignores: `--pll-damping` with `--converter gfm` or a bound
  asset class, `--current-bw` with `--converter gfm`, and `--pll-bw` with a grid-forming asset class each exit 2 naming
  the reason.
- The CLI's literals are named once in `GridSim.Cli.CliDefaults`; the WSCC-9 Anderson-Fouad X'd {0.0608, 0.1198,
  0.1813} is `IeeeCases.Wscc9TransientReactancePu`; the operational voltage band comes from `Analysis.VoltageLimits`
  (0.94 / 1.06); the "169 h" help strings read `ForwardInferenceGuard.HorizonHours`. Every value is unchanged.
- The classical study and its export take f0 from `model.FrequencyHz` (was a literal 50.0); this is 50 Hz on every
  bundled case (`stability case9`: CCT 611 ms, as before).
- `--help` lists every verb; a contract test keeps `--help`, the dispatch and the CLI reference in step. The old text
  listed fewer than half the verbs and named a non-existent "protection" verb.
- Documentation: CLI reference (`--synth-sources`, `--tool`, `--pll-bw`, `--pll-damping`, `--current-bw`) and the
  harmonic study chapter.

### Removed
- `--serial` (no serial transport exists); it is no longer a known flag and exits 2.

### Fixed
- Modbus/TCP framing bounds the MBAP length at 254 (unit id plus the 253-byte maximum PDU); frames up to 266 bytes
  were accepted instead of triggering a resync.
- Tests: added `CliHelpContractTests` and `CliLiteralsAndFlagsContractTests`.

## [1.95.037]
### Changed
- Presentation and GDA literals are named (web `Projection/PresentationLimits.cs`, GDA `GdaBuildDefaults.cs`, WPF
  constants), with values unchanged except the WPF GENERATION torque, which uses the live frequency: at 50 Hz and
  30,000 MW 95.490 -> 95.493 MNm (displayed "95.5" both).
- WPF fuels use Core's taxonomy: "hydrogen" / "fuel cell" / "electrolyser" read as Hydrogen (storage, no inertia)
  instead of Hydro, and "recip" / "diesel" / "engine" as a synchronous fossil machine instead of Other. This moves WPF
  inertia and fossil figures on models with those types, to agree with Core and the web. Marker colours in
  `map-geo.js`: hydrogen #29B6F6 -> #80CBC4, recip/diesel/engine #9E9E9E -> #A1887F, efw/incinerator (without "waste")
  #8BC34A -> #9E9E9E.
- Known limitation: Core's `WaveField.Compute` still makes a second dense build (about 1.46 GB per gb-full frame), the
  per-frame web state is unchanged at 1,212,846 characters, and the WPF EXPERIMENTAL audit still runs on the UI thread.
- Known limitation: per-island infeed sizing and other Web vs WPF differences remain.

### Fixed
- A non-converged solve no longer shows SYSTEM NORMAL. The web lamp logic is in `StatusLamp.Evaluate`: NOT CONVERGED
  (red) outranks every verdict, and the map shows a red "NOT CONVERGED - STATE NOT VALID" banner. The WPF header lamp
  reads `Result.Converged` first (also during a trip), raises an alarm on onset and logs onset and recovery.
- The web EXPERIMENTAL projection checks convergence, generator count and H > 0 before building the dense wave model:
  gb-full per-frame allocation 2,922,247,456 B -> 1,465,611,840 B. WPF EXPERIMENTAL computes only while its tab is
  selected.
- The web map caches its static layers and caps animation at about 15 fps, as WPF does. The wave field (229,908 JSON
  chars per gb-full frame) is pushed only in WAVE mode. WPF schematic labels are laid out once per view. WPF fleet
  pages build id lookups once per refresh: gb-full 2,620 transformers 108-177 ms -> 0.8-0.9 ms, 1,383 tap changers
  22.7-55.8 ms -> 0.24-0.48 ms, identical sums.
- Corrected a WPF doc comment that gave the default as 2 days where the code uses 9.
- Tests: added `StatusLampConvergenceTests`, `WaveModelGuardTests`, `PresentationConstantsAntiDriftTests`,
  `WpfPresentationSourceContractTests` and `FuelTaxonomyJsAntiDriftTests`.
- Documentation: manual ch. 4.

## [1.95.038]
### Changed
- `impedance-stability --pll-damping` is passed to a bound grid-following asset class (it exited 2 in 1.95.036);
  grid-forming still exits 2.
- Known limitation: on case57 and case118 the Feasible estimator now reports Infeasible because of a KKT scaling
  defect.
- Known limitation: battery ageing is still switched off by `Reset`.

### Fixed
- The Feasible estimator's KCL gate audits laws 6 and 8 against the hard zero-injection constraint rows through new
  `PhysicsLaws.AuditAgainst` (`Audit` unchanged); with no such rows they read NotApplicable, never a vacuous pass. No
  estimated state changes on case9/14/30/57/118; verdicts: noisy case9, case14, case30 Infeasible -> Converged; case57,
  case118 Unobservable -> Infeasible.
- Robust SchweppeGm normalises residuals with the declared weights; the first IRLS pass is unchanged. case14: J
  80.1827474462989 -> 80.87739163443453, max |dVm| 0.0013602703237021174 -> 0.0016010767593979125 pu, max |dVa|
  0.22042253902512776 -> 0.21477206941510119 deg. case118: J 882.400399084558 -> 912.77768081754, iterations 29 -> 32,
  max |dVm| 0.0016121949843507233 -> 0.0016114572910870262 pu, max |dVa| 0.11518370613341666 -> 0.11834431246438726 deg.
- Subtransient kernel: stages take effect when `t + 1e-6 dt >= AtSeconds` (the EMT convention), so a case9 bus 7
  clearing at 140 ms now clears at 0.24 s, not 0.245 s (machine 1 end delta 3.81770474267125 -> 3.6782339733860923
  rad, machine 2 4.130510791639398 -> 3.990869660008799 rad). Loss of synchronism uses the classical centre-of-inertia
  test. CCTs are bit-identical (case9 bus 7 0.6039981842041016 s, case14 bus 4 0.12999916076660156 s). Initialisation
  throws when X''d != X''q; the converter store is charged only for power injected.
- The impedance scan reports Y_conv on the system base, so the verdict and critical SCR no longer depend on the
  rating: at PRatedPu = 12 the critical SCR 0.11297468354430379 -> 0.9946202531645569 (bit-identical at 1).
  `crossval_impedance.py` scales its Y the same way.
- Real-network grid impedance is no longer silently replaced by the Thevenin; it fails loud with the frequency.
- Authored PLL damping is kept when only the bandwidth is overridden (new `pllDampingOverride`); any override drops the
  Recorded tag.
- Small-signal analysis holds H = 0 and inverter-bound units instead of counting them as machines; case9 is
  bit-identical, and with generator 3 at H = 0 it gives order 4, 2 machines instead of throwing.
  `impedance-stability` reports why the modal cross-check is unavailable.
- EMT initial condition: pure-resistance arms conduct, floating nodes are pinned at 0 V and counted
  (`InitialConditionFloatingNodes`, shown by `emt`), and a still-singular system throws. Bit-identical on every bundled
  case.
- Estimation and distribution fail loud: a non-slack phase-angle datum needs an explicit `DatumAnchorDeg`;
  BranchCurrentDsse reports not converged on meshed or parallel networks; reversed current rows are re-keyed and
  negated and an unmatched one throws; the three-phase J skips source-bus magnitude residuals as the fit does; dt = 0
  adds no process noise and a negative or non-finite dt throws; parallel circuits give one constraint row per pair or
  group, and a KKT failure on an observable set is Infeasible; the island sweep skips out-of-service branches.
- Tests: added tests for the KCL gate, impedance base and fallback, small-signal held units, EMT initial conditions,
  phase-angle anchors, estimation robustness, the distribution estimator and the subtransient kernel.
- Documentation: technical ch. 6, 11, 16, 18, 21 and 24.

## [1.95.039]
### Changed
- Schematic betweenness allocates its buffers once instead of per source (about 12.5M list allocations on the gb-full
  WPF schematic load). Results are bit-identical.
- `ProtectionContext.BusName` uses a dictionary instead of scanning every bus per call (about 19M comparisons per
  gb-full assessment); first match wins, as before.
- Tests: added `LookupAndBufferReuseTests`.

## [1.95.040]
### Changed
- `cycle --pipeline` exits 2 and `CycleOptions.PipelineBacklog` throws `NotSupportedException`: the pipelined backlog
  drain is not implemented, and both were silently ignored.
- Known limitation: the batched LU seam is not wired into the contingency sweep.

### Fixed
- New `Dispatcher.BalanceDetailed` returns the balanced model (bit-identical to `Balance`), the marginal target, a
  plain-English note when it falls outside the marginal plant's [Pmin, Pmax] (never silently clamped), and the
  slack-bus count. Only the first slack bus carries the marginal plant.
- New `FeasibilityConstraints.Validate` (positive floor, VmFloor < VmMin < VmMax, every per-bus min < max, angle limit
  in (0, pi]) fails loud, and the Feasible estimator calls it before solving. The docs now state that the voltage band
  is audited after convergence, not enforced during iteration, and that `LimitViolations` forces Infeasible only under
  `RejectOnLimitViolation`.
- The physics audit labels every headline law from `PhysicsCheckRegistry.Names`, whose copy had drifted.
- The batched CPU LU copy no longer overflows its int byte offset above 268,435,455 elements; same bits.
- Tests: added `DispatcherBalanceDiagnosticsTests`, `FeasibilityEnvelopeValidationTests`, `PhysicsRegistryNamesTests`
  and `BatchedAndCycleOptionTests`.
- Documentation: CLI reference (`cycle --pipeline`); scheduler docs corrected.

## [1.95.041]
### Changed
- Internal: reworded a source comment in `CpuBatchedLuSolver`; no code change.

## [1.95.042]
### Fixed
- `JsonGridLoader` checks protection placement with `ProtectionModel.ValidateAgainst`, so a recloser with a missing
  or unknown target, an unknown monitored bus or a wrongly kinded class fails the load, listing every problem. The
  shipped gb-synthetic devices pass.
- `ScadaThresholds.VoltageBand*` read `Analysis.VoltageLimits.Operational*` (0.94 / 1.06).
- `estimate` anchors a non-slack phase-angle datum: it resolves `--prior` first and passes the datum's angle from the
  solved prior (or the `--warm-start` base solve) as `DatumAnchorDeg`; without either it exits 2 naming the fix.
- Real-time DSE: an equal timestamp steps with dt = 0 (it was coerced to 1 s of process noise), and an earlier window
  is refused (skipped by default, or fails the run without ContinueOnError).
- The storage time series looks up the slack through a map instead of a linear search per result bus; same results.
- Tests: added `RealtimeDseTimeOrderTests` and `FollowUpWiringTests`.

## [1.95.043]
### Fixed
- The dense LU and Kron-reduction pivot floor and the Newton fallback BiCGSTAB cap use
  `NumericTolerances.PivotFloor` / `DefaultKrylovMaxIterations`; values and results are unchanged.
- Harmonic state estimation fails loud instead of returning zeros: a singular Y(h) throws
  `SingularHarmonicNetworkException`, and an unobservable source set is refused with a plain-English message through
  the new `DenseComplexLuSolver.SolveDenseOrNull`. An observable estimate is bit-identical.
- Tests: added `SharedToleranceRoutingTests` and `HarmonicStateEstimatorFailLoudTests`.

## [1.95.044]
### Changed
- Known limitation: the CLI has no verbs for some IEC 60909 and coordination studies yet.

### Removed
- The unused private `MedianAbsoluteDeviation` in `NoiseFloorAnalysis`.

### Fixed
- Documentation: `ProjectionStatistics` is described as a tested building block not yet wired (SchweppeGm uses the
  hat-matrix normalised residual). Corrected stale docs on `Iec60909.SynchronousBreakingCurrentKa` (multi-machine
  decomposition is `PartialThreePhase`; `ThreePhase` reports Ib with mu = 1), `Iec60909.LineToLine`,
  `OvercurrentCoordination` and `EventCatalog` (it ships three built-ins). No code path changes.
- Tests: added `StaleDocCorrectionsTests`.

## [1.95.045]
### Changed
- Known limitation: DNP3 and GOOSE application layers remain framing-only.

### Fixed
- IEC 104 decodes type 31 (M_DP_TB_1, timed double point), which was silently discarded, like type 3 with its time
  tag. Unsupported types (5 / 32, 7 / 33, commands) are counted per type id on `Iec104Protocol.UnsupportedTypeIds`
  (new `Iec104Asdu.IsSupportedType`).
- Documentation: `EstimationStatus.Infeasible` lists all three routes that set it (a hard-law violation, a limit
  breach under `RejectOnLimitViolation`, and a KKT failure on an observable set).
- Tests: added `Iec104DoublePointTimedTests`.

## [1.95.046]
### Added
- `impedance-stability --grid canonical|real`: `--grid real` scans against the network's own harmonic driving-point
  impedance at the converter bus (exit 3 with the reason if that solve is singular or a reporting point sits on the dq
  DC arm); the SCR sweep flags apply to `canonical` only and are refused with `real`; any other value exits 2. The
  default is unchanged (`canonical`).
- Documentation: CLI reference.
- Tests: added `ImpedanceGridFlagContractTests`.

## [1.95.047]
### Changed
- Remaining literals in Dynamics, Analysis, Estimation, IO, Numerics, Physics, PowerFlow, Protection, Scenarios,
  Simulation, Storage and Gpu are named constants (for example `Dynamics/ConverterDefaults.cs`,
  `GridModel.DefaultBaseMva` = 100, `SteadyStateSolver.DefaultMaxOltcPasses` = 25, `ScenarioCorners`,
  `LiveSolveSettings`, `GpuContext.Fp64SmokeTolerance`, `ReplayValidator.PostEventTailSeconds`). Every constant equals
  the literal it replaced; no computed number changes on any bundled case.
- PresolvedReplay, BridgeProtocol and PassivePlantFleet use the model frequency instead of a literal 50 Hz
  (number-neutral: every bundled case is 50 Hz).
- The CLI and Web reference the new Core constants (same values); the CLI's EMT switch-off resistance constant is
  renamed `EmtSwitchOffResistance` (it was mislabelled ohm).
- Tools: the lake path defaults to `$GDA_ROOT/DataSources/Derived` and the CLI path is chosen per OS.
  `lake_conformance.py` exits 2 when no lake is set or it is missing (it printed "data-gated" and exited 0);
  `mc_estimate_sweep.py` exits 2 if the Release CLI is not built.
- Tests: added constant anti-drift tests.
- Documentation: technical ch. 11 and 17.

## [1.95.048]
### Added
- CLI verbs `cpf` (continuation power flow: uniform load scaling with the slack absorbing the balance, the largest
  converged lambda and the margin above base), `taps` (`TapOptimizer.Flatten`: one coordinated tap schedule over every
  OLTC, objective before and after) and `acopf` (`AcOptimalPowerFlow`: loss-aware dispatch by successive
  linearisation). Each validates its flags and exits 3 when its base or outer iteration does not converge. `cpf` and
  `stress` use distinct, labelled loadability definitions (`stress` re-dispatches generation at each factor).

### Changed
- Known limitation: there is no `restore` verb yet (a sequence-file format is needed first).

## [1.95.049]
### Added
- `gridsim iec60909`: IEC 60909 short-circuit duties for a three-phase, line-to-line or single-line-to-earth fault at
  `--bus` (maximum or minimum objective, breaker time and fault duration selectable): Ik'', ip with kappa and R/X, Ib,
  Ik, Ith with m, the DC component, the Thevenin impedance and the converter share of Ik'', at round-trip precision,
  with the model's own frequency.

### Changed
- Known limitation: OvercurrentCoordination, SequenceOfEvents, AssetEnvelope, AssetValidation and the hybrid scheduler
  have no CLI verb yet.

## [1.95.050]
### Changed
- State-estimation performance; every output is bit-identical. Timings are Release wall clock on a low-power laptop CPU.
- gb-full estimate 128,451 ms -> 9,428 ms, allocation 4,355 MB -> 515 MB. The residual-covariance stage went from
  122,457 ms to about 5,500 ms; with 6 bad-data removals 929,394 ms -> 56,871 ms and 29.4 GB -> 2.5 GB. pegase1354
  covariance stage 11,129-11,302 ms -> 275-359 ms; case300 166-213 ms -> 12-13 ms. Results do not depend on the thread
  count.
- Robust IRLS keeps the sparse structure: allocation pegase1354 Huber 581 -> 285 MB, case300 Huber 110 -> 41 MB.
- The sparse-backend observability rank proof uses the SIMD row kernel: case300 60 -> 24 ms, case533 273 -> 136 ms,
  case588 342 -> 191 ms.
- New `DenseEstimationLimits` holds the dense size caps (3,000 buses for Constrained, Feasible and QR; 2,500 states for
  the rank proof and EKF; 2,000 states for the opt-in covariance). QR now fails loud above 3,000 buses; case300 QR
  98,462 ms -> about 17,500 ms, allocation 352 -> 145 MB.
- EKF fails loud above 2,500 states; per window case300 about 2,600 -> 750 ms, case588 about 22,000 -> 7,000 ms.
  `serve --method ekf` above the cap exits 2 with the reason instead of an unhandled exception.
- Known limitation: the EKF is still dense (O(s^3) per window) and the observability gate still densifies.
- Tests: added bit-identity tests for the residual covariance, observability rank, the Kalman kernels and the QR
  layout, and `DenseEstimationLimitsTests`.
- Documentation: technical ch. 6 and 18.

## [1.95.051]
### Changed
- `GeneralEigen`'s inverse iteration factors the shifted matrix once with `DenseComplexLuSolver.FactorDense` and
  back-substitutes (O(n^2) per iteration instead of O(n^3)). Every eigenvector is bit-identical.
- Tests: added `FactorOnceInverseIterationTests`.

## [1.95.052]
### Fixed
- `GeneralEigen.Decompose` throws `DefectiveEigenproblemException` whenever |psi^H phi - 1| exceeds the larger of
  `biorthoTol` and `BiorthonormalitySlack` (defaults 1e-6), naming the deviation; a badly converged pair with
  psi^H phi = 0.5 used to pass. No result changes. The unused left-vector inverse iteration is removed.
- Tests: added `BiorthonormalityGuardTests`.

## [1.95.053]
### Changed
- Both EMT engines (`EmtEngine`, `ThreePhaseEmt`) solve into reused buffers through new
  `SparseLuFactorization.SolveInto(b, x, work)` and cache the reverse Cuthill-McKee ordering while the sparsity pattern
  is unchanged. Results are bit-identical.
- Tests: added `SparseSolveIntoBitIdentityTests`.

## [1.95.054]
### Changed
- The subtransient RMS step allocates its EMF, governor and RK4 work arrays once per run (`SwingWorkspace`) instead of
  every step. Results are bit-identical.

## [1.95.055]
### Changed
- New `DashboardViewModel.Update(frame, boundaries, baseSolveMs)` overload; the two-argument `Update` is kept.
- Known limitation: the illustrative WPF unit pages and the simulated SCADA 'STALE' quality remain until real
  telemetry is available.

### Removed
- WPF dead code: `SchematicRenderer` and `PlaceholderPageVm`.

### Fixed
- The WPF window's close handler disposes the cycle view's `ResidentCycleService`.
- The WPF CYCLE tab shows the base-solve time instead of " - ": `StepOnce` times the solve and passes it on (replay and
  bridge frames still pass 0).
- Tests: added `WpfDeadCodeAndCycleTests`.

## [1.95.056]
### Changed
- Internal: verification run; no code change.
- Known limitation: CCT bisection continues below the step resolution, so CCT runs take about twice as long as
  needed.

## [1.95.057]
### Changed
- Known limitation: the VSM state kinds are reserved (`IsSyntheticInertia` is always false), and the inverter
  reductions ignore their `ratedMw` / `f0` inputs.

### Fixed
- The grid-following reduction's second state, d(f_est)/dt, is the new `StateKind.PllFrequencyRate` instead of
  `StateKind.PllAngle` (also on the wire); it is appended last, so every existing kind keeps its ordinal.
- `includeSoc` / `modal --include-soc` were accepted and ignored; no SoC state exists, so Core throws
  `NotSupportedException` and the CLI exits 2 with the reason.
- Documentation: `Kext`, `BuildSwingFromCoupling` and the synchronising Laplacian are per-unit power per radian on the
  system base, not MW/rad. No computed number changes.
- Tests: added `SmallSignalLabelAndSocTests`.

## [1.95.058]
### Added
- `GET /api/realtime` returns the resident real-time estimator's run summary, latest estimate (sequence, state time,
  status, converged, J, chi-square verdict) and skipped files (404 when `Realtime:Enabled` is off); listed in the
  OpenAPI document.

### Changed
- Documentation: the real-time estimator runs once over its feed at start-up and does not watch the directory.

### Removed
- Unused `GaugeDto` / `GaugeBandDto`.

### Fixed
- `HumanScaleProjection` and `SimSession` summaries describe what the code does (raw replay starts on 1 January of the
  current year, clamped to the lake).
- Tests: added `RealtimeEstimatorOutputTests`.

## [1.95.059]
### Changed
- Internal: verification run; no code change.
- Known limitation: the EMT build does not model series-compensated branches (X <= 0: case145, case300, case240_pserc,
  case588_sdet, case9241pegase) or off-nominal taps (case14 to case300).

## [1.95.060]
### Added
- EMT models off-nominal taps (an ideal a : 1 transformer on the From side, matching the power flow's Yff / Yft / Ytf
  / Ytt at the system frequency) and series capacitors (new `SeriesCapacitorBranch` for X < 0, which used to become a
  pure resistor).
- `EmtCase.Reset()` returns the engine to its built state; a run after it is bit-identical to a fresh engine.

### Changed
- EMT refuses phase-shifting transformers and R = X = 0 branches with an `ArgumentException` naming the branch, and
  refuses a negative inductance, R = L = 0, a bad tap or capacitance, or any element with infinite conductance.
- EMT against the power flow, worst |dV|: IEEE 14 0.016146232237465356 -> 5.442925472231795E-06 pu; IEEE 57
  0.09263089867708872 -> 5.353021737251943E-06; gb-synthetic 0.08226721015590352 -> 5.990969817265349E-06; IEEE 24
  0.018025258585895373 -> 1.1352704024266598E-06.
- Bundled cases whose EMT behaviour changes: IEEE 14 / 57 / 118 / 24, pglib 73, pglib 162 and gb-synthetic give
  power-flow-consistent trajectories; IEEE 300 / 145, pglib 240 / 588 and PEGASE 9241 now build instead of failing on a
  singular matrix; cigre_mv and PEGASE 1354 / 2869 / 9241 are refused because they contain phase shifters. gb-full, uk,
  IEEE 9, IEEE 30, case33bw and other cases without taps, X < 0 or shifters are bit-identical.
- `EmtCase.Simulate` throws unless the case's timestep and network match its engine's, and on a second `Simulate` or
  one after a manual `Step`. `ThreePhaseEmtEngine` also refuses a second `Simulate`.
- Known limitation: the IEEE 300 / 145, pglib 240 / 588 and PEGASE 9241 EMT runs grow without bound because of negative
  demand and negative charging loads.

### Fixed
- `emt` exits 2 with the reason on an `ArgumentException` from the run (for example `--event switch:<id>` naming a
  non-switch), which escaped unhandled before.
- Tests: added `EmtTapAndSeriesCapacitorTests`, `EmtUntappedBitIdentityTests` and `EmtCaseConsistencyTests`.
- Documentation: technical ch. 21, design `emt-time-domain.md`, manual ch. 22.

## [1.95.061]
### Changed
- Internal: planning documents only; no code change.

## [1.95.062]
### Added
- `estimate --method` gains `schweppe-gm` (alias `gm`) and `qr`; the error for an unknown value lists all seven.
- Documentation: CLI reference and technical ch. 6 method table.
- Tests: added `EstimateMethodSelectionTests`.

## [1.95.063]
### Changed
- Documentation: `ImpedanceModelDetail.PllOuter` describes the first-order current-loop roll-off at
  `GridFollowingImpedance.IdealCurrentLoopHz` (1,000 Hz); `OuterLoopOnly` is code-only;
  `ConverterOperatingPoint.Guarded`'s `vaRad` is unused by design. No code path changes.
- Known limitation: the grid-forming admittance does not depend on the operating point.

## [1.95.064]
### Changed
- Security-constrained DC-OPF uses the new `DcOpfSecuritySimplex`, which runs `BoundedSimplex.Solve`'s algorithm step
  for step with the same tolerances but never builds the constraint matrix (about 73 MB on gb-full against about
  1.9 GB of dense arrays). Results are bit-identical to the dense path (at most the sign of a zero can differ), which
  stays callable as `DcOptimalPowerFlow.SolveDenseReference`. `DistributionFactors` finds bus ids through a dictionary.
- Tests: added `DcOpfSecuritySimplexParityTests`.

## [1.95.065]
### Added
- `buses.json` takes an optional `"zip": {"ap","bp","cp","aq","bq","cq"}` block (the keys the solve export writes):
  all six required, finite, each triple summing to 1 within 1e-9, otherwise `InvalidDataException` naming the bus and
  key. Absent or null means constant power.

### Fixed
- `PhysicsFeasibility.Check` derives a generator bus's Qg from `LoadAtVoltage` at the solved voltage, and
  `PowerFlowTracing` takes demand from `LoadAtVoltage`, so a solved ZIP bus no longer shows as a phantom generator.
  Every bundled result is unchanged (no bundled case has a ZIP load).
- Documentation: manual ch. 2.
- Tests: added `ZipLoadWiringTests`.

## [1.95.066]
### Added
- `EmtBuildOptions.NegativeDemand`: `ConstantCurrent` (default; |Pd| / BaseMva pu shaped like the first source's
  waveform) or `Refuse` (throws, listing every such bus).
- `ModalResult.ParticipationUnavailable` and `ParticipationUnavailableReason`; `gridsim modal` prints the reason.

### Changed
- Known limitation: a bus shunt with Gs < 0 is still a negative conductance in EMT.

### Fixed
- `EmtNetwork.FromGridModel` throws an `ArgumentException` naming the branch and the missing end when an in-service
  branch ends on a bus not in the model (it was skipped).
- EMT no longer turns non-passive loads into negative elements: negative line charging (B < 0) becomes a shunt reactor
  with exactly the branch's susceptance at the system frequency, and negative demand follows
  `EmtBuildOptions.NegativeDemand`. Cases with neither are bit-identical.
- A defective eigenproblem in modal analysis falls back to eigenvalues only with the reason, instead of throwing on the
  undamped path or silently dropping participation on the damped path.
- Tests: added `EmtPassivityAndDanglingBranchTests` and `ModalAnalysisDefectiveTests`.

## [1.95.067]
### Added
- Exciters in the RMS transient-stability study: new `IRmsFieldController` and `RmsExciter` (`FromSexs`,
  `FromIeeet1`, each optionally with a `Pss1a`); `RmsTransientStability.Study` and `CriticalClearingTimeSeconds` take an
  optional per-generator `exciters` list (null entries hold that machine's field). It fails loud on a mis-sized list, a
  shared instance, both exciter lists at once, a fault at t = 0 with an automatic reference, an initial field the
  limits cannot hold, and inverted limits. With no exciters results are bit-identical.
- CLI: `stability --rms --avr none|sexs|ieeet1 [--pss]` (indicative settings; `none` is the default; `--pss` needs an
  AVR; both need `--rms`). The banner states the field model in use.
- Documentation: technical ch. 24, CLI reference.
- Tests: added `RmsExciterStudyTests` and `StabilityAvrFlagContractTests`.

### Changed
- Known limitation: governors are not yet modelled in the RMS study.

## [1.95.068]
### Added
- Tamper-evident audit log on disk: `AuditLogWriter` appends to a `gridsim-audit/1` file (UTF-8 without BOM, one
  compact JSON line per entry, LF, fixed field order, UTC times), checks the file before appending, is single-writer
  (exclusive `<log>.lock`) and refuses appends after a part-written line. `AuditLogFile.Verify` / `Load` stop at the
  first broken entry with its index and reason. Tail truncation and whole-log rewrites are caught only against an
  `AuditAnchor` (count + head hash) kept outside the log. `AuditEvent.Record` records who / what / inputs (SHA-256) /
  outcome.
- `gridsim audit verify <file> [--anchor-count N --anchor-head HEX]`: OK exit 0, TAMPERED exit 3 naming the entry,
  unreadable exit 1.
- Documentation: technical ch. 10 (format), CAF mapping C1, CLI reference.
- Tests: added `AuditLogFileTests`.

## [1.95.069]
### Changed
- `AuditLogTamperException` derives from `IOException` (callers catching `IOException` see it).

### Fixed
- Tests: fixed a compile error in `DcOpfSecuritySimplexParityTests` and the t = 0 sample check in
  `EmtPassivityAndDanglingBranchTests`.

## [1.95.070]
### Changed
- Security DC-OPF performance per solve, before -> after, with every status and cost unchanged: case1354pegase
  73,445 ms, 516 MB -> about 1,100 ms, 40 MB; gb-synthetic (Infeasible) 396,550 ms, 1,619 MB -> about 720 ms, 31 MB;
  case588_sdet about 5,400 ms, 68 MB -> about 130 ms, 8.5 MB; case240_pserc about 2,100 ms, 30 MB -> about 130 ms,
  5 MB; case533mt_hi about 1,270 ms, 28 MB -> about 20 ms, 1.75 MB; case162_ieee_dtc about 158 ms, 5.7 MB -> about
  17 ms, 0.91 MB.
- Known limitation: gb-full does not reach the DC-OPF LP: its islands make the DC susceptance singular and
  `DistributionFactors.Build` throws.

## [1.95.071]
### Added
- `--audit-log <file>` on `estimate`, `crossval` and `interpose`: the log is opened and verified before any work (a
  tampered or locked log, or a missing directory, exits 1); one entry is written when the run ends, however it ends,
  with the actor, verb, each input's SHA-256, `exit=<code>` and the verb's result line (never frame contents). The
  anchor is printed to stderr for `gridsim audit verify --anchor-count/--anchor-head`. With any other verb, or given
  twice, the flag exits 2.
- Core: `AuditRunRecorder`.
- Documentation: manual ch. 19 (global switches), technical ch. 10.
- Tests: added `AuditRunRecorderTests`.

## [1.95.072]
### Added
- `gridsim grade`: inverse-time overcurrent grading of a radial relay chain at one or more fault currents. Relays are
  `CURVE:PICKUP:TIMEDIAL` (IEC SI / VI / EI / LTI, IEEE MI / VI / EI) or `dt:PICKUP:DELAY_S`, downstream first; exit 0
  graded, 3 when any pair is not coordinated, 2 on a usage error. Core: `OvercurrentCoordination.ParseElement`.
- `protection-check --cascade --alarms <soe.csv>` cross-references the predicted cascade against a sequence-of-events
  log; exit 3 when the prediction does not reproduce the log. Core: `AlarmLogCsv` refuses a malformed line with its
  line number.
- Documentation: manual ch. 19 (`grade`, `--alarms`).
- Tests: added `GradeAndSoeCliTests`.

### Changed
- Known limitation: AssetEnvelope.Sweep, AssetValidation.Compare, HybridMultiRateScheduler and
  ProtectionAssessment.AssessThreePhaseFault have no CLI verb, and `ThreePhase`'s `BreakingIbKa` does not yet use
  PartialThreePhase's mu.

### Fixed
- `OvercurrentCoordination.OperateTimeSeconds` / `Grade` throw `ArgumentOutOfRangeException` for a NaN, negative or
  infinite fault current, an infinite pickup, a zero, negative or non-finite time dial, and a negative or non-finite
  definite-time delay or CTI. Valid inputs give the same bits as before.

## [1.95.073]
### Changed
- `estimate --phaseangle-from-gda` reads the phase-angle reference faster: `PhaseAngleInferedReader.ReadWeek` reads the
  four needed parquet columns as typed columns, `At` finds the last sample at or before the instant by binary search,
  `ReZeroed` no longer re-sorts, and the week is cached against the partition's file names, sizes and modification
  times. Layouts the typed path does not cover (CSV, nested or other storage types) use the original reader, kept as
  `ReadPartitionReference`. Results are bit-identical to the previous reader.
- One week (604,800 rows at 1 Hz): DateTime timestamps 3.3-6.7 s -> 0.34-0.64 s and 700 MB -> 223 MB allocated; text
  timestamps 2.5-3.7 s -> 0.62-1.06 s and 775 MB -> 358 MB; a cached re-read takes 0.4-0.5 ms.
- Known limitation: a row whose timestamp does not parse is skipped, and a missing value becomes phase 0, f 50 Hz or
  RoCoF 0, as before.

## [1.95.074]
### Changed
- The subtransient RMS step solves the network into reused buffers. New `IRmsNetwork.SolveInto(emf, ic,
  machineCurrents, converterVoltages)` writes into caller arrays; its default implementation copies `Solve`'s result,
  so a third-party network keeps working. `DenseMachineNetwork`, `ConverterNetwork` and `SparseRmsNetwork` override it
  without allocating outputs. Results are bit-identical (every trace and the synchronism verdict on case9 dense and
  sparse, with a battery converter and with SEXS field controllers).
- Allocation per 5 s run at a 5 ms step: case9 with a battery dense 2.15 -> 0.18 MB, sparse 10.75 -> 7.55 MB; case118
  dense 11.06 -> 2.57 MB, sparse 56.77 -> 39.05 MB. Wall time: case9 dense 8.1-10.4 -> 5.8-6.6 ms, sparse 29.5-31.7 ->
  24.2-25.0 ms; case118 within noise.
- Known limitation: on the sparse path, `IComplexLinearFactorization.Solve` still allocates its solution vector.

## [1.95.075]
### Changed
- `CpuBatchedLuSolver` factors each system in place in `aFlat` / `bFlat` and allocates no per-system arrays. The shared
  in-place core is `DenseLuSolver.FactorSolveInPlace(Span m, Span b, n)`; `DenseLuSolver.SolveInPlace` runs it on its
  private copy as before. Results are bit-identical to `DenseLuSolver` for every system (a singular system is still
  NaN and flagged), with and without AVX2 and hardware intrinsics.
- Known limitation: the N-1 sweep does not use the batched solver.

## [1.95.076]
### Changed
- Internal: planning for the first v2 JSON schema.

## [1.95.077]
### Added
- First part of the v2 JSON schema. Additive only: no v1 file, loader, model, DTO or numeric path changed.
- Zero-dependency schema-subset validator in Core (`src/GridSim.Core/IO/Json/Schema/`): `JsonSchemaSubset` (loads
  `schemas/v2`, resolves local and cross-file `$ref`, refuses non-consuming `$ref` cycles and any keyword outside the
  supported subset), `SchemaValidator` (every error with a JSON pointer; duplicate member names refused; `$` in a
  pattern means end of string, as JSON Schema intends), `KindSpecRegistry` (checks template `params` against their
  kind: unknown key, unit mismatch, range, required, null rules, enum and string types) and `V2LibraryChecker`
  (cross-template rules, such as a placeholder never being a controller target).
- Schemas `schemas/v2/common.schema.json` (quantity, the closed unit vocabulary, provenance with the `[VERIFY]` rule,
  `templateRef`, status), `library.schema.json`, `kind.schema.json` and `kinds/site.kind.json`.
- `data/library/placeholders/unmodelled-sites.json`: the fifteen placeholder sites, every value null, provenance
  `CuratedPlaceholder` `[VERIFY]`. Nothing references them by name, so deleting the file removes them.
### Changed
- Tests: added v2 schema tests under `tests/GridSim.Tests/V2/`.

## [1.95.078]
### Changed
- Documentation: new technical ch. 28, "The v2 JSON schema (Phase 1)" (first draft), with pointers from the technical
  contents, technical ch. 10 and manual ch. 2.

## [1.95.079]
### Added
- Fifteen per-kind parameter contracts in `schemas/v2/kinds/` (synchronous machine, exciter, PSS, governor,
  grid-following and grid-forming inverters, line and transformer types, OLTC, protection, storage, harmonic spectrum,
  load, grid-code and solver profiles): 346 parameters and 576 `codeDefaults` entries, one per C# consumer, each
  checked to equal the code value exactly.
- Inconsistency register in technical ch. 28.8 (34 rows).
### Changed
- `KindSpecRegistry` accepts `"+Infinity"` / `"-Infinity"` as the `codeDefaults` value of a number parameter. Template
  values must still be finite.

## [1.95.080]
### Added
- `schemas/v2/case.schema.json` (`gridsim-case/2`: name, system, network file list, imports, local templates,
  bindings, profiles, and overrides that each need a reason and `"shadow": "explicit"`) and
  `schemas/v2/network.schema.json` (`gridsim-network/2`: the v1 element shapes plus new fields; unknown members
  refused). Phase 1 of the v2 schema is complete.
### Changed
- Tests: a test-only v1-to-v2 projector round-trips every bundled JSON case field for field; v1 `transformer` classes
  map to the `oltc` kind.
- Known limitation: one blank generator name in `continental-europe` fails v2 validation.

## [1.95.081]
### Added
- The three-phase distribution stack is reachable. `MultiPhaseJsonLoader` reads a strict `gridsim-dist3ph/1` feeder
  format and `gridsim-dist3ph-measurements/1`; every error names the file and JSON path. Grounded-wye to grounded-wye
  transformers at rated ratio are accepted; anything the sweep does not model (delta or open connections, off-nominal
  ratios, delta or voltage-dependent loads) is refused with the reason.
- `data/dist3ph/`: the IEEE 4-node test feeder (balanced and unbalanced) and a synthetic measurement set. Every node
  and phase matches the published voltages to within half the last published digit (worst 0.49 V and 0.045 deg); full
  table in technical ch. 18.5.2.
- `gridsim dist3ph <feeder.json> [sweep | fault --bus ID [--type all|3ph|slg|ll|llg] [--zf] | estimate --measurements
  m.json [--method wls|wlav]]`: exit 0, 1 input, 2 usage, 3 non-convergence (status on stderr, no voltage table).
  Loading is subject to the licence bus limit.
- Documentation: technical ch. 18.5, the ch. 10 schema table, ch. 19 and manual ch. 19.
### Changed
- Known limitation: the three-phase namespace is experimental. Phase-enum laterals, sweep transformer elements on
  `TransformerConnectionModel`, regulators, capacitors, delta / ZIP loads, switches and an adapter from `GridModel` are
  not yet supported.

## [1.95.082]
### Changed
- An expired paid licence falls back to the free Personal tier (1,500 buses, never expires) instead of denying every
  non-bundled model. Only a model over that cap is denied, with a message naming the expiry.
- `BundledNetworks.DataFolders` gains `intl` and `dist3ph`, so France (1,976 buses) and Continental Europe (5,824
  buses) load on the free tier. `LICENSE.md` section 1 lists both folders.
- Tests: `LicensingTests.Expired_licence_is_denied_valid_one_is_allowed` now expects 100 and 1,500 buses allowed and
  1,501 denied after expiry.
### Removed
- `src/GridSim.Core/GSCode.zip`, an unreferenced 4.6 MB snapshot of Core source.

## [1.95.083]
### Added
- `estimate --hti`: bad data identified by hypothesis testing (`HtiBadDataEstimation`). It estimates with LNR removal
  off and, while chi-square fails, identifies, removes and re-estimates; it stops on a pass, on nothing identified or at
  the removal cap, and says which. Removals go to `hti-removals.csv`. Refused (exit 2) with `--no-bad-data`, the robust
  methods, `--backfill-removals`, and above 2,000 states. On a clean set it equals plain WLS exactly.
- `estimate-noise-floor <estimate-export.json|dir>` (`NoiseFloorAnalysis`): per source and measurement kind, whether the
  declared sigma matches the observed noise; writes `noise-floor.csv`; flags `--miscal-tol` and `--min-samples`.
- `estimate --fdia-screen` (`FdiaScreen`) writes a verdict to `fdia-screen.json`; "suspected" or "no verdict" exits 3.
- `serve --method ukf|enkf` (`UnscentedKalmanStateEstimator`, `EnsembleKalmanStateEstimator`) in the same streaming
  engine as `ekf`; refused (exit 2) above 2,500 states at start or 2,500 measurements in a window.
- Web: SPEED DOMAINS status sentence; EXPERIMENTAL wave-degraded note and cyclic-trace status; MARKET imbalance-price
  sparkline (up to 96 points per session); SCADA STAMP column with row click-through to ASSETS; the harmonics study
  states its limitation (`HarmonicsDto.SourceNote`).
### Changed
- Documentation: `C37118Decoder` names its CONFIG parser; `robust-gm-leverage.md` says what shipped and that LTS / LMS
  do not exist. `LinearPmuStateEstimator`, `FrequencyRocofFilter`, `HarmonicStateEstimator`,
  `CoupledHarmonicPowerFlow`, `ThreePhaseHarmonicPenetration`, the PDC and the PMU mapper are marked experimental.
- Known limitation: the web harmonics study uses one synthetic 6-pulse source at the largest-demand bus; per-converter
  sources are not modelled.
- Known limitation: the MARKET sparkline takes one sample per streamed price, not per settlement period.
- Known limitation: there is no `pmu-ingest` verb and no web solver switch.
### Removed
- `FrameEnvelope.Assets` / `AssetsDto` (never set or read).

## [1.95.084]
### Fixed
- `data/intl/continental-europe/generation.json`: generator entry 676 (212.2 MVA hydro at bus 1721), named " " (one
  space), is now "hydro @ bus 1721". The name is a label only, so results are unchanged. The v2 schema still refuses
  blank names.

## [1.95.085]
### Changed
- Known limitation: whether float FREQ in C37.118 data is the actual frequency (as coded) or the deviation is still to
  be checked against the standard.
### Fixed
- CGMES import: SSH `RotatingMachine.p` and `.q` use the load sign convention (CGMES 2.4.15 / IEC 61970-600-2). The
  importer now negates them (new `CimGenerator.Q`) and the exporter negates back, so round trips stay exact. Imported
  generator P and Q change sign compared with earlier versions.
- C37.118: new `DecodeData(frame, timeBase, PmuConfig)` scales integer FREQ to FNOM + count / 1000 Hz, DFREQ to
  count / 100 Hz/s and phasors to count x PHUNIT x 1e-5 (C37.118.2-2011 Tables 5 and 9). The format-word overload
  decodes float data and refuses integer formats.
- PMU angles are re-referenced: new `PmuAngleReference(PmuId, PhasorIndex, AnchorDeg)`; each angle is its wrapped
  difference from the named reference channel plus the anchor. No reference, a dropped reference PMU or a non-voltage
  reference throws. The reference channel's own row is emitted.
- Phase-angle reference: `At` has a maximum age (2 s default, configurable) and throws before the first sample, past
  the end or inside a gap. Both readers refuse a missing column, a missing or non-finite value and an unparseable
  timestamp.
- Tests: added `C37118IntegerScalingTests` and `PmuAngleReferenceTests`; updated the CIM, C37.118, PMU mapper and
  phase-angle tests to the corrected behaviour.

## [1.95.086]
### Changed
- The RP1 credible loss follows the NESO Frequency Risk and Control Report 2025 (consultation version, March 2025): the
  loss of any single generating or importing unit is secured and simultaneous losses are not (s.6 Table 1, s.6.1.1,
  s.6.2, s.7.1). The web rule (largest single unit) stands; WPF's largest correlated interconnector group is shown as
  unsecured context, flagged when larger than the largest unit, never as the credible loss. One Core presenter,
  `FrequencyRiskPresenter`, serves the web RP1 / OPERATOR tabs and WPF `NesoRp1Vm` / `OperatorVm`.
- RP1 grade ladder (no document defines one): NORMAL >= 5 s, WATCH from 3 s, ADVISORY from 2 s, WARNING below 2 s, with
  falling-trend escalation. Web colour cuts unchanged (5 s, 2 s); the web grade text names the step.
- Web and WPF share status lamps (Core `StatusLampRules`). FREQUENCY EVENT compares the frequency itself with 49.8 /
  50.2 Hz, so 49.8 Hz now reads SYSTEM NORMAL and 49.79 Hz is an event. The voltage alert uses the lowest and highest
  bus voltage (the web headline shows both). One shared idle interconnector cut-off of 0.5 MW; WPF's own 5 MW
  `IdleLinkMw` is removed.
- GDA grid infeed is sized per GSP island: 5 x max(1,000 MW, the island's own demand), not the whole network's.
  Two-island example: both infeeds 16,000 MVA before; now 15,000 MVA (3,000 MW island) and 5,000 MVA (200 MW island).
  Single-island models are bit-identical. Not yet checked on real DNO data.
- Known limitation: the credible loss counts synchronous machines only; per the FRCR, single importing units
  (interconnector imports, converter-connected wind farms) should count too.
- Known limitation: WPF labels a 3,600 MW Dogger Bank "design basis" and strikes 1,800 MW through as "superseded"; the
  FRCR treats 1,800 MW as the future largest loss.
- Known limitation: the web lamp also raises a frequency event at RoCoF >= 0.125 Hz/s (the legacy loss-of-mains
  setting; the current one is 1 Hz/s); WPF has no RoCoF check.

## [1.95.087]
### Changed
- JSON v2 units: the vocabulary gains `MW.s/Hz`, `MW/Hz`, `MW/s/MW`, `V` and `min` (20 -> 25 units). Seven keys are
  declared on their kinds (`rocofGainMwsPerHz`, two `fastSlew` keys, `dcVoltageV`, `synInertiaMwsPerHz`,
  `dampingMwPerHz`, `thermalTauMin`) with 14 new code defaults, so every bundled v1 key is known to v2. Technical
  ch. 28.5 updated.
- Breaking: a bus without `baseKv` is refused, naming each. The 400 kV default is removed; every bundled bus has it.
- A line flow matching no branch throws instead of borrowing another branch's impedance or a 0.0001 pu placeholder.
- The bridge / solve-export model block carries an optional `frequencyHz` (absent means 50 Hz), so a 60 Hz model
  survives a round trip.
- New `EstimationResult.IrlsCapReached` flag; the status is unchanged.
- Tests: `DispatcherFallbackReachTests` fail if a bundled generator ever reaches the dispatcher's capacity-factor
  fallback.

## [1.95.088]
### Changed
- `stability --rms` finds the critical clearing time at step resolution: the bisection searches whole 2 ms steps and
  stops when the stable and unstable step counts are adjacent (9 runs instead of 18 for a 1 s window).
  `CriticalClearingTimeSeconds` returns the longest clearing time on the step grid that keeps synchronism; new
  `CriticalClearingBracket` gives both ends; the CLI explains the number and prints the bracket for a single bus. case9:
  bus 7 0.6039981842041016 -> 0.604 s, bus 1 0.5300006866455078 -> 0.53, bus 2 0.3579998016357422 -> 0.358, bus 3
  0.32000160217285156 -> 0.32; case14 bus 4 0.12999916076660156 -> 0.13. No verdict changed.
- Saturation follows the PSS/E GENROU convention: new `SaturationCurve.FieldIncrement(x) = x * Se(x)`, used by the
  single-machine model, `SubtransientInit` and the RMS integrator. Field rise at E'q0 = 1.1: 0.19078939073186518 ->
  0.20986832980505143; saturated case9 seeded Efd, machine 0: 1.1996919425369508 -> 1.2061333835558483. Unsaturated runs
  are unchanged.
- Feasible estimator: KKT constraint rows are scaled by an exact power of two near sqrt(max diag G), and angle rows
  switch on at (1 - 1e-3) x limit. Synthetic telemetry: case57 noise 0 and 1 and case118 noise 0 go from Infeasible at
  iteration 0 to Converged in 8; case118 noise 1 from Infeasible to MaxIterations (30); case9, case14 and case30 are
  bit-identical.
- Known limitation: the classical CCT (`stability` without `--rms`), the Feasible reactive-limit rows and the
  Constrained method do not yet get these fixes; GENROU saturation on |psi''| on both axes is not modelled.

## [1.95.089]
### Changed
- Internal: full test run; no change.

## [1.95.090]
### Added
- Passive plant per tick, opt-in: `DynamicsConfig.EnablePassivePlant` (live sim), a `passivePlant` argument on the
  replay loop and `gda-replay --passive-plant`. Each tick applies the previous tick's delayed OLTC taps and thermal
  ratings before the solve; controllers advance only after a converged solve. Its timings are placeholder values.
  Default runs are unchanged.
- In the passive plant, each OLTC starts at its branch's `TapRatio`, steps in the direction set by which side the
  controlled bus is on, derates from the original rating (no compounding), and is keyed by name, both end buses and
  order; a transformer regulating a generator-held (PV) bus keeps its tap and only derates. gb-full replay, 12 x 30 min,
  off -> on: step 1 losses 242.3439336602299 -> 242.3485309327937 MW, step 11 199.94464012003388 -> 199.9532719970346
  MW; 2,050 of 3,539 bus voltages differ at step 11 (largest 0.0275730708611445 pu); 5,664 of 5,918 ratings derated.
### Changed
- Breaking: building an asset-class library refuses inputs that loaded but were ignored, listing every problem in one
  error: kind `synchronousMachine`, keys `dispatchRampMwPerMin` and `minOutputFraction`, and any key nothing simulating
  that kind reads. A `RestoreBranch` protection stage is refused. The accepted keys per kind match the v2 kind specs.
- `dispatchRampMwPerMin` is removed from the bundled asset classes (values kept in technical ch. 13); no result
  changes. A gb-full regenerated by the current GDA builder still loads.
- Known limitation: the solver's own instant OLTC loop can run a generator-bus tap to its limit.

## [1.95.091]
### Changed
- Documentation: house style `docs/developer/STYLE.md`; docs tools under `tools/docs/` (`lint.py`, `rename.py`, and
  `capture.ps1` with `captures.txt`, writing to `docs/_captures/`); rewrote manual "Running power flows" and technical
  ch. 3 "Power flow".

## [1.95.092]
### Changed
- Documentation: the manual is renumbered into nine parts (getting going, steady state, the apps, security and
  protection, dynamics, events, estimation, operations, reference). Old to new: 16 -> 3 Configuration, 23 -> 4
  Licensing, 03 -> 5 Running power flows, 04 -> 8 The desktop app, 05 -> 10, 06 -> 11, 07 -> 14, 22 -> 16, 20 -> 17,
  08 -> 18, 09 -> 20, 10 -> 21, 11 -> 22, 12 -> 23, 13 -> 24, 21 -> 25, 14 -> 27, 18 -> 29, 17 -> 30, 15 -> 31; 01, 02
  and 19 keep their numbers. Chapters 6, 7, 9, 12, 13, 15, 26, 28 and 32 are listed as in preparation.
- Documentation: every chapter file is renamed `1-95-NNN-CC-slug.md` (NNN is the version it was last verified against);
  `19-cli-reference.md` keeps its name. Redirect stubs remain at `docs/technical/13-asset-dynamics-data-grounding.md`
  and `docs/technical/14-cascade-validation-and-blind-prediction.md`.
- Documentation: both contents pages rewritten; each design record in `docs/technical/design/` opens with a status
  block; the broken runbook link in `docs/developer/benchmarks/server-results/a100-results.md` is fixed.

## [1.95.093]
### Changed
- Documentation corrected against the code: estimator `--method` accepts `schweppe-gm` (alias `gm`) and `qr`;
  `crossval_estimation.py` handles `VaDeg` and refuses `IMagPu` / `IAngDeg` with exit 3; a flag given as the last token
  with no value exits 2; `nminus1-sweep` is dense up to 1,000 buses and `--parallel 0` means all cores; `modal`,
  `stability` and `fault` exit 3 on a non-converged base solve; `grade` is in the ch. 19 verb index; the bridge pickers
  are ZONE / FROM / SRC.
- Documentation: `gb-full`, `data/intl` and `data/dist3ph` are bundled networks at every tier, and an expired licence
  falls back to the free tier. Further stale facts fixed (CIM import takes one merged RDF/XML document; the
  web-versus-desktop differences; a real `run-metadata.json`). New `tools/docs/relabel.py`.

## [1.95.094]
### Added
- `protection-check --fault-bus ID [--zf pu]` runs `ProtectionAssessment.AssessThreePhaseFault`: exit 0 result, 1 fault
  study failed, 2 usage, 3 non-convergence. case9 bus 7 bolted: 17 relays operate.
### Changed
- The classical CCT (`stability` without `--rms`) searches whole 2 ms steps and returns the same bracket as the RMS
  CCT; the CLI prints the bracket. WSCC-9 (s): bus 1 0.5500011444091797 -> 0.55, bus 2 0.3430004119873047 -> 0.342,
  bus 3 0.33099937438964844 -> 0.33, bus 4 0.5189990997314453 -> 0.518, bus 5 1 -> 1, bus 6 0.3340015411376953 ->
  0.332, bus 7 0.6109981536865234 -> 0.61, bus 8 0.35700035095214844 -> 0.356, bus 9 0.7609996795654297 -> 0.76.
- Feasible estimator: generator Q-limit rows are banded with the standard active-set rule (kept while inside the band
  and binding, otherwise released). case57 noise 0 Converged 8 -> 9 iterations, J 1.6764772915324424E-23 ->
  1.4821666807502987E-23; noise 1 Converged 8 -> 10, J 407.1442711244868 -> 407.14427112448703; case118 noise 0
  Converged 8, J 1.631166577699373E-22 -> 1.4604475072059656E-22; case118 noise 1 MaxIterations (30) -> Converged (8,
  J 877.8811650453016); case14 angle cap at 0.7x MaxIterations -> Converged (68, J 72.38263564898884).
- Constrained estimator: exact power-of-two row scaling. No change on the synthetic sets; with hard zero-injection
  rows, case57 and case118 noise 0 and 1 go from Unobservable at iteration 0 to Converged in 5-6 iterations; case14 is
  bit-identical.
- IEC 60909 breaking current in `ThreePhase` uses mu for meshed networks (IEC 60909-0:2016, 10.1.6; the clause is still
  to be checked against the standard). case9 Ib (kA): bus 1 2.934393338019216 -> 2.3509284626333975, bus 7
  1.647443765211713 -> 1.630308759669193; buses 5 and 9 unchanged.
- `HybridMultiRateScheduler` (SCADA / PMU estimation scheduling) is marked EXPERIMENTAL.
- Known limitation: the classical integrator has no tolerance at the fault edges.
- Known limitation: stability is not monotone in clearing time on WSCC-9 bus 2 (stable to 342.5 ms, unstable
  343-348 ms, stable again 348.5-350 ms), so a CCT bisection can land in such a pocket.
- Known limitation: `PartialThreePhase`'s total Ib does not yet use the mu formula, so it disagrees with `ThreePhase`.

## [1.95.095]
### Fixed
- Version numbering: a release labelled 1.95.092 reused a number already taken and set the project version back to
  1.95.92. Its entry is renumbered 1.95.094 and the version moves forward to 1.95.95.

## [1.95.096]
### Changed
- Documentation: rewrote manual ch. 14 "Frequency studies" (verb chooser, worked `freq` and `transient uk` examples,
  line-by-line output, why `h0-study` exits 3 on gb-2023-12-22), technical ch. 5 "Frequency dynamics" (the swing
  equation as coded with every default, a worked `trip` example, determinism), and manual ch. 16 and technical ch. 21
  on EMT (companion models, refusals and exit codes, step-size accuracy, a worked case14 bus-5 fault).
- Known limitation: `domains <case>` loads `data/uk`; `transient` ignores `--forming` and `--asset-dynamics`; an EMT
  `switch:` with any action word except `close` opens; the `CsrMatrix.Builder` sort is unstable for rows of 17 or more
  entries.

## [1.95.097]
### Changed
- Internal: planning.

## [1.95.098]
### Changed
- DC-OPF and PTDF work per island: each DC island gets its own reference bus, chosen as the AC power flow does (the
  island's slack, else its largest generator); the DC-OPF balances each island and lists every island, including any
  with no generation. Connected cases are bit-identical. gb-full has 2 islands (3,536 buses at slack 194; 3 buses at
  slack 6774). `dcopf gb-full` now runs: the small island is Optimal and the main island's security LP is Infeasible
  (121 lines overloaded under merit order), so it reports INFEASIBLE and exits 1 after about 136 s; with `--no-limits`
  it is feasible in about 1 s at 53560.37556125464 GBP/h.
- OLTC taps hold at generator-held buses, and OLTC records are matched by name and end bus (the passive plant uses the
  same matcher). gb-full: 71 taps that ran 1.0 -> 1.0999999999999996 now hold; OLTC passes 8 -> 4; losses
  530.7899042534549 -> 523.1288528336736 MW; largest voltage change 0.00019445173554877293 pu (bus 5764). gb-synthetic
  and uk are bit-identical.
- The classical integrator has a tolerance at the fault edges: classical CCT bus 6 0.332 s (166 steps) -> 0.334 s (167
  steps); other WSCC-9 buses unchanged.
- CCT searches (RMS and classical) detect non-monotone stability: after the bisection they scan 16 steps below and probe
  16 above; new result fields flag non-monotonicity and count the runs; the CLI prints NON-MONOTONE. No WSCC-9 CCT
  moves; classical buses 2, 8 and 9 are flagged (stable pockets at 175, 190 and 395 steps). Cost: 38-39 runs per bus
  instead of 10-11.
- IEC 60909: `PartialThreePhase`'s total Ib now equals `ThreePhase`'s bit for bit. WSCC-9 bus 5 1.4690796589338537 ->
  1.4683989584292976 kA (the old total exceeded Ik''), bus 4 1.9953536674616223 -> 2.0600168548926723 kA, similar on all
  9 buses.
- Known limitation: the RMS CLI runs the CCT search twice.

## [1.95.099]
### Changed
- Internal: full test run; no change.

## [1.95.100]
### Changed
- Documentation: rewrote manual ch. 18 "Replaying recorded events" and ch. 20 "Counterfactuals", technical ch. 7 "The
  evidence engine" (with a gb-2019-08-09 worked example: RoCoF -0.160776 Hz/s, nadir 48.82031 Hz, RMSE 0.1533 Hz,
  avoided cost GBP 2,676,000), manual ch. 17 "Harmonic studies" and technical ch. 19 "Harmonics" (a worked bus-9 THD of
  6.225551642 %).
- Known limitation: in `harmonics`, a triplen source or an unknown `--bus` crashes with exit -532462766, and spectrum
  orders outside the analysed set are ignored silently; `fleet` prints fixed conclusion text; `nminus1`, `nminus2`,
  `n-1-1`, `screen`, `trip`, `domains` and `export` ignore a positional case.

## [1.95.101]
### Changed
- Documentation: rewrote manual ch. 11 "Contingency analysis" and technical ch. 23 "Contingency and security analysis
  (N-k)", including which network each verb loads.
- Known limitation: `nminus1`, `nminus2`, `n-1-1` and `screen` read only `--dir` and ignore a positional case, while
  `nminus1-sweep`, `stress` and `chain` take a positional case and ignore `--dir`; N-1-1 redispatch ignores boundary
  limits (B4 at 115.6 % before the second outage); `trip` drops a generator name that follows a flag; a bare `.m` name
  under `data/matpower` never loads.

## [1.95.102]
### Changed
- Documentation: rewrote manual ch. 21 and technical ch. 6 "State estimation". Dense and sparse estimation are stated
  as not bit-identical, agreeing to 1e-8 pu and 1e-7 deg.
- Known limitation: a measurement file's `kind` and `value` are not enforced (missing ones load as VmPu with value 0);
  `--method constrained`, `feasible` or `qr` above 3,000 buses throws an unhandled exception; `--hti` crashes on a
  singular suspect block.

## [1.95.103]
### Changed
- Documentation: rewrote manual ch. 22 "Estimating GB history", ch. 23 "Replaying estimated states", ch. 24 "The GDA
  bridge" and technical ch. 9 "GDA integration"; the chapters name the environment variable the code reads, `$GDA_ROOT`.
- Known limitation: `gda-replay` cannot replay a `gda --out` case; a mistyped `--gsp` builds an empty model; embedded
  generation joins 3 of 897 NGED rows; a stored `first` start without `topology.json` fails with `Invalid start date`.

## [1.95.104]
### Changed
- Documentation: rewrote manual ch. 25 "Interposing live telemetry" and technical ch. 20 "The RTU/SCADA interposer" on
  a real loopback session over IEC 104 and Modbus. `docs/rtu-interposer.md` is now a redirect stub.
- Known limitation: after a client reset the interposer transport never accepts another client; a mapping error during
  a flush crashes the verb and loses the window; `serve` exits 0 when every window is unobservable.

## [1.95.105]
### Fixed
- A missing or unparseable case, `.m` file or `--dir` folder exits 1 with `error: <message>` instead of a stack trace and
  exit -532462766; `stress`, `nminus1-sweep` and `bench` still report LOAD FAIL per case and carry on.
- A non-converged case9 exits 3 (it exited 0).
- `losses`, `nminus1-sweep` (and its base solve), `synth`, `gda --solve` and `--all-gsps` choose the engine through the
  shared selector (`gda --dense` / `--sparse` still force it). `losses case300` output is byte-identical;
  `nminus1-sweep case300 --losses` 6.0 s -> 2.5 s with the same output.
- Reports show `Generator output` (the generators' Pg; `n/a (not converged)` when not converged): case14 0.25 -> 0.27 GW,
  case118 3.78 -> 4.37 GW (display only).
- `MaxIterations` is the real cap: both Newton engines evaluate the mismatch after the last allowed step. No converged
  result or status changed on any bundled case; only the reported mismatch of solves that hit the cap now describes the
  state returned (case9241pegase flat start, cap 10: 18.89833591341877 -> 0.07772159550836377).
- `--help` points at `docs/manual/19-cli-reference.md`; `--ticks 0` or negative exits 2; `LooksRadial` checks
  connectivity; a tap stuck at its limit with its bus out of band counts as not settled and is named in the new
  `TapsAtLimit`.
- Tests: new pins for `MaxUndampedStep`, `VmFloorPu`, `RevertVoltTolPu` and the sweep `SafetyFloor`, and a dense /
  sparse parity test of per-branch `PLossMw` / `QLossMvar`.

## [1.95.106]
### Added
- `docs/reference/`: official and public grid documents (SQSS, FRCR, Grid Code, Distribution Code, ENA ERECs,
  loss-of-mains / RoCoF, TO and DNO protection, event reports, frequency services, fault levels, models, VoLL,
  international), 239 of 324 catalogue rows, plus the MIT PyPSA-GB mirror `etys-pypsa-gb/` (ETYS Appendix B 2022-2024,
  App F and G, FES regional breakdowns, GSP regions, ETYS boundaries, the Strathclyde 29-bus GBreducednetwork.m). 298
  files, 426.9 MB, SHA-256 in `MANIFEST.sha256`; `README.md` indexes every row with its file or why it is missing.
  ukgds is excluded (no licence). A value taken from a document cites its id, version and section. Redistribution terms
  are to be checked before any public release.
### Changed
- Internal: planning for GB reference-model support.

## [1.95.107]
### Fixed
- A named case is no longer ignored. `nminus1` / `n-1`, `nminus2` / `n-2`, `n-1-1`, `screen`, `trip`, `domains`,
  `export`, `emt` and `harmonics` choose their network through one shared function: `gridsim nminus1 case14` screens
  IEEE 14-bus (it ran GB reduced). A positional case and a `--dir` naming different networks exit 2; a missing `--dir`
  folder exits 1 before any report file opens. `nminus1-sweep`, `stress` and `chain` honour `--dir`. `export case14`
  exports case14 (it wrote the GB model). `cycle`: a missing case exits 1, an unknown name 2.
- `trip --dir data/uk Torness` trips Torness (a name after a flag was dropped); an unknown name exits 2 listing the
  units. Bare `.m` names load from the `data/matpower` subfolders (a name matching two files exits 2). `stress --from 2
  --to 1` exits 2; the chain fixed-mode footer prints.
- Event verbs accept only the flags they read (`fleet --device-ffr`, `evidence --corners`, `replay-event --device-ffr`
  exit 2 naming the flag); `trip` says `Peak RoCoF`; a trace shorter than the comparison window is no longer compared
  against its last sample (the missing samples are excluded and reported).
- `estimate`: `--synth` with `--measurements` is refused; full usage banner; the header names the method;
  `IrlsCapReached` is printed; the `--hti` singular-covariance crash exits 3; Constrained / Feasible / QR above 3,000
  buses exit 2 with the reason; `estimate --synth case14` works.
- Harmonics: a spectrum line at an order the study does not analyse is refused (exit 1, naming it); a triplen source
  exits 1 and an unknown `--bus` exits 2. CLI and web compute THD on the solved fundamental through one Core function,
  which changes results: case9 worst THD (bus 9) 6.225551642146658 % -> 6.25287132456945 %, case30 worst (bus 8)
  12.431468792652895 % -> 12.941038914682215 %; IEEE-519 compliance counts unchanged on every case run.
- Measurement files: a row without `kind` or `value` is refused (exit 1, naming each row).
### Changed
- Known limitation: `impedance-stability` ignores a positional case; `freq` and `replay` read their file only from the
  token after the verb; `HarmonicPenetration.Solve` still drops unanalysed orders silently for non-CLI callers; the
  `screen --csv` column `initial_rocof_hz_per_s` holds the peak.

## [1.95.108]
### Changed
- `estimate` warns when the prior's voltages leave 0.9-1.1 pu (no numbers change). A GDA state at 1.267891 pu passed
  chi-square because `--prior solve` voltages entered as prior rows that always agree with the inputs that made them.
- Known limitation: prior-solved rows still count as chi-square redundancy; verbs other than those fixed write default
  output beside the executable; a crosswalk with two bindings for one address keeps the last silently; `interpose`
  ignores other verbs' flags.
### Fixed
- Interposer: a window that does not fit its binding is discarded, logged and counted, and the listener carries on (exit
  3 at stop). A 32-bit binding on register 65535, or one whose second register is bound separately, is refused at
  crosswalk load. Both transports drop a client on any read failure other than a real timeout, so the next client is
  accepted. The stop summary prints unsupported IEC 104 type ids, map errors, duplicate names and client resets.
  `--monitor` with `--out-store` / `--measurements-dir` exits 2; window files and Parquet parts are never overwritten (a
  clash saves as `-dupN`).
- `serve` exits 3 when no window gives a usable estimate (including all `rejected-fresh`).
- Estimation: HTI never removes a `virtual-zero-injection` row under `constrained` / `feasible` (plain WLS
  bit-identical). `--fdia-screen` counts a limit breach only beyond three times the meter's sigma: case118 and case30 go
  from suspected (exit 3) to clear (exit 0); case14 and case57 unchanged. `EstimationAuditTrail` is the one source of
  the `--audit-log` outcome text (byte-identical).
- GDA: a mistyped `--gsp` exits 2 suggesting near names; an empty carve exits 3 and writes nothing. `gda --all-gsps`
  builds two spellings of one group once ("Iron Acton" was built twice, double-counting NGED: solved demand 20.24 ->
  19.26 GW, losses 265 -> 247 MW), lists nine empty spelling variants separately and still exits 3 (Feckenham 66 kV does
  not converge). `gda-replay --case` accepts a `gda --out` folder and `--out` defaults to `results/`. The web live-bridge
  card reads "GDA lake: presolved states (gb-full)"; the dashboard shows the estimate quality pill.

## [1.95.109]
### Changed
- Internal: full test run; no change.

## [1.95.110]
### Changed
- Internal: project planning.

## [1.95.111]
### Changed
- Internal: project planning.

## [1.95.112]
### Fixed
- `nminus1`, `nminus2`, `n-1-1`, `nminus1-sweep`, `stress` and `chain` share one study option set (100 iterations,
  Q limits on, OLTC loop; NETS SQSS v2.10 s.6.2); library defaults unchanged. Counts are secure / violating /
  islanded: case118 sweep 167 / 10 / 9 -> 164 / 13 / 9 (now equal to `nminus1`); GB reduced secure-row Vmin 1.027 ->
  0.998 pu (matches `uk --once`); case118 loss-sweep base losses 132.86 -> 132.48 MW. The sweep is bit-identical dense
  and sparse on case57, case118 and case300.
- Violations are netted against the base case: case14 0 / 19 / 1 -> 16 / 3 / 1; Ireland 2 / 76 / 4 -> 70 / 8 / 4;
  case57 0 / 78 / 2 -> 31 / 47 / 2; case300 0 / 294 / 117 -> 204 / 90 / 117. Rows carrying only base-case violations
  read `base only`, not `secure`.
- `nminus2` and `n-1-1` pair only solvable singles; failing singles are listed apart and counted in the verdict. GB
  reduced `--candidates 5`: 10 pairs all islanded -> 10 pairs all secure, 12 singles listed apart.
- Corrective redispatch acts only when a line or boundary is over its limit and moves the minimum MW respecting line
  and boundary limits, island by island. GB reduced, each of the 12 meshed first outages: 31,810-32,380 MW moved and B4
  48.435617734804836 % -> 115.62311773480394 % before; now 0 MW and B4 stays at 48.435617734804836 %. Exhaustive N-1-1
  GB reduced 0 / 128 / 424 -> 122 / 6 / 424.
- Changed exit codes: `nminus1`, `nminus2`, `n-1-1` and `nminus1-sweep` exit 3 when anything is not secure or the base
  case does not converge; `screen` exits 3 on any breach; `losses <unknown>` exits 2 naming the case (it was an
  unhandled exception), and a missing `.m` exits 1.
- Documentation: manual ch. 11, technical ch. 23 and ch. 19 updated.

## [1.95.113]
### Added
- One `<Version>` in `Directory.Build.props` for every project; InformationalVersion is `1.95.NNN+<commit>`, shown by
  `gridsim --version`, the Web footer, the OpenAPI version and the WPF ABOUT tab. A release is the pushed tag
  `v1.95.NNN`.
- Publish profiles (CLI single-file self-contained win-x64 / linux-x64 / osx-arm64, Web, WPF; trimming and ReadyToRun
  off) and a tag-driven release workflow. A published win-x64 case118 export is byte-identical to the normal build
  except commit, timestamp and elapsed fields.
- Dockerfile; compose, appsettings and nginx made neutral (site values from environment, `.env` or
  `compose.override.yml`; nginx example on port 5540). Web Production is replay-only. Live deployments must set their
  site values outside the repository.
- CI matrix (Windows and Linux, three SIMD modes), dependabot, CodeQL, a vulnerable-package gate, an SBOM attached to
  releases, and THIRD-PARTY-NOTICES.
- Crash logs and messages in the CLI (without `--audit-log`) and WPF.
- Tests: CLI process smoke tests, published-output test, licensing tests and WebApplicationFactory host tests.
### Changed
- The CLI binary is named `gridsim`; self-contained binaries need no SDK and keep `data/` beside them.
- README and the WPF HARMONICS tab describe what is modelled; caf-mapping C1 is marked achieved.
- Known limitation: the release workflows have not yet run on GitHub, the Docker image and the linux / osx publishes
  are not yet built, and the WPF crash handler and ABOUT tab are untested by hand.

## [1.95.114]
### Added
- `dcpf` verb on the existing `DistributionFactors` DC model (same results); `cpf` / `stress --pv-out` write a per-bus
  PV CSV from the shared loadability loop (case9 lambda 2.373046875 unchanged).
- MATPOWER and JSON case writers. `convert` (.m <-> JSON) re-reads its output and exits 3 naming the first difference
  unless the solve matches bit for bit (data/uk -> .m exits 3: MATPOWER cannot hold its 10 OLTCs), and never
  overwrites. gencost and dcline are parsed and kept (unused by solvers). `validate` checks v2 documents. Every bundled
  case round-trips bit-identically (pegase `Qd = -0` returns as +0, solve unchanged). New optional v1 keys support the
  lossless round trip; absent keys load as before.
- `--bus-vlimits` on `nminus1` / `nminus2` / `n-1-1` uses per-bus Vmin / Vmax from MATPOWER columns 12-13 and JSON
  `vminPu` / `vmaxPu` (refused, exit 2, on other verbs). Without the flag the old path runs. With it, verdicts move on 9
  bundled cases (case9 5 -> 4 insecure outages; case33bw base undervoltage 0.913 pu no longer flagged).
### Changed
- Out-of-service generators are kept and marked (`Generator.InService`, JSON and MATPOWER status), not dropped.
  `GridModel.Generators` is the in-service fleet the engines read; `AllGenerators` keeps every unit for the writers.
  Only pglib_opf_case588_sdet has status-0 units (72 of 167). Every bundled case solves bit-identically, dense and
  sparse.
- `acopf` is labelled "DC dispatch + AC loss loop ... [not a full AC-OPF: ...]" in output, help and docs; numbers
  unchanged (case9: 5 iterations, 12.242 MW, 16,362 GBP/h).
- The v1-to-v2 projection maps a v2 generator with `inService: false` back to v1, which now has the field.

## [1.95.115]
### Added
- New `src/GridSim.SchemaEditor` console app (Core only, no packages): one key table; add, duplicate, delete and rename
  with reference cascades; filter, find, go-to and where-used; v2 manifests and network files with cross-file reference
  checks; exit codes 0 / 1 / 2 / 3. Documented in manual ch. 33.
- Core: a lossless JSON text layer that patches the original bytes (a one-field edit changes one token; BOM, newline
  style, comments and layout kept; undo restores the bytes exactly). Bundled cases round-trip byte for byte and give a
  bit-identical dispatched model.
- `schemas/v1`, embedded in Core and pinned to the loader DTOs. The v1 schema refuses JSON null (the editor removes a
  field instead); v1 numeric ranges follow the v2 network schema; v2 generators are a flat merged list.
- A save gate: schemas, a staged load through the real loader, licence, confirmation of new warnings, write beside,
  verify, rename, and roll back on partial failure.

## [1.95.116]
### Changed
- Known limitation: the "Infinity" strings the loader accepts on generator limits are not admitted by the v1 schema.
### Fixed
- `schemas/v1` lists the newer v1 members (system `dispatch`; bus `vminPu`, `vmaxPu`; generator `inService`,
  `pMinMw`, `pMaxMw`, `qOutputMvar`; line `tapRatio`, `phaseShiftDeg`, `isTransformer`) with title, description, type
  and ranges as in the v2 network schema.
- Tests: the CLI smoke test expects the relabelled `acopf` header "ACOPF (DC dispatch + AC loss loop) - IEEE 9-bus
  (WSCC) ...".

## [1.95.117]
### Added
- `gridsim <verb> --help`, `<verb> -h` and `gridsim help <verb>` print that verb's section of manual ch. 19 (usage,
  every flag with its default, exit codes, the general exit-code rule), including aliases and modes. Chapter 19 is
  embedded in the CLI, so help and manual cannot drift.
- `--json`, `--csv` and `--out <file.json|file.csv>` on 23 verbs through one writer (`convert` takes `--json` / `--csv`
  only; `serve` is excluded). Stdout is unchanged with or without the flags (writer messages go to stderr). Files carry
  every row (the console shows the top N); doubles are in round-trip form (JSON NaN / infinity as named strings); CSV
  uses the invariant culture. A finished result is written even when its verdict fails, with the exit code inside; a
  run that stops early writes nothing and says so; an unwritable file exits 1.
### Changed
- Behaviour scripts will see: `<verb> --help` prints that verb's help, not the verb list (exit 0); the unknown-flag
  message says "Run 'gridsim <verb> --help'" and points at `docs/manual/19-cli-reference.md`; `--json` / `--csv` on
  non-writer verbs exit 2 instead of being ignored (`modal`'s own `--json` and `screen`'s own `--csv` unchanged);
  `nminus1 --out x.json` and similar now write files; `dcpf --out` reproduces its two old CSVs byte for byte, adds
  islands and summary files, and words its stderr line differently.
### Removed
- The `docs/cli-reference.md` stub.

## [1.95.118]
### Changed
- `losses` reports bus-shunt conductance draw as `ShuntPMw`; case300's 1.2108721600802141 MW residual is that draw, and
  the residual is now -4.16e-12 MW. `TotalLossMw` keeps its definition.
- `DenseComplexLu.ClampedPivots` reports pivot clamping.
- `synth` refuses a bad scale (exit 2) and explains a failed solve.
- Known limitation: `synth --scale` keeps GB demand (every scale up to 0.36 builds the same 1,082-bus network).
- Known limitation: `acopf` fails at the first AC solve on case118 and case300; three intl models do not converge in a
  base solve; three gb-full circuits cannot be relieved by any dispatch, so the security DC-OPF is Infeasible.
- Known limitation: the bundled feeder files use raw units, and three pglib files carry placeholder dispatches; both
  converge once normalised or balanced.
### Fixed
- Corrective redispatch measures the slack unit's room from its DC-balanced output, not its stated output.
- `chain --bess` uses the study option set and the OLTC loop. case118 bit-identical; GB reduced step 0 Vmin
  1.0313969238603367 -> 0.9977947297199731 pu.
- The OLTC hold follows the bus type the solve ended with (`PowerFlowResult.SolvedBusTypes`, both engines): a PV bus
  pinned at its Q limit had its tap held at 0.9457 pu, out of band, while `TapsSettled` said true; it is now regulated to
  1.0 pu.
- The AC-OPF loss loop scales each island by its own losses. Before, one island was over-dispatched by 2.296 MW and the
  other island's slack ran 2.30 MW above Pmax. Connected cases bit-identical.
- The RMS converter network refuses a singular block.

## [1.95.119]
### Changed
- Internal: full test run in the normal, AVX2-off and hardware-intrinsics-off modes; no user-visible change.

## [1.95.120]
### Added
- All features below are opt-in; without the new flags every existing run is bit-identical to the previous version.
- `dcopf --costs case|<file.json>` uses each generator's cost curve (MATPOWER `gencost` or a JSON cost file), and
  `--prices` adds LMPs and line shadow prices; `acopf --costs` likewise. Quadratic costs are solved exactly by a new
  solver. HiGHS agrees within 1e-6 on dispatch, cost and every price for case5_pjm, case3_lmbd and case9; on case30 /
  case30_as GridSim matches the exact equal-cost dispatch to about 1e-14 (HiGHS was off by up to 4e-4 MW).
- PJM 5-bus LMPs are 16.9774, 26.3845, 30.0000, 39.9427 and 10.0000; the comparison with the printed values in Li and
  Bo (2010) is not yet confirmed against the paper.
- `nminus1 --outage-types branch,gen,bus`. RateB and RateC are read and written (MATPOWER, JSON);
  `--post-fault-rating A|B|C` applies to outage states, while the base case always uses rating A (NETS SQSS v2.10).
- `fault --type slg|ll|dlg` on the three sequence networks and `protection-check --fault-type`; 50N, 51N, 21N and 46
  elements are assessed on an unbalanced fault (still "not checkable" on a power flow).
- `iec60909 --type dlg`, `--breaker`, `--branches`, `--all-buses` and `--corrections --rated-pf` (K_G, K_S, K_SO, K_T
  applied on a model copy). The K_S / K_SO factors and the relay pickups are not yet confirmed against their sources.
- New `pf <case>` verb with distributed slack (`pmax|pg|equal`) and switched shunts, run as loops around the unchanged
  Newton engines (dense and sparse bit-identical).
### Changed
- Known limitation: no bundled case carries winding connections, so the new earth-fault paths refuse with exit 3 and
  say why.

## [1.95.121]
### Added
- `gb-reduced-29` (Strathclyde 29-bus model) bundled in `data/matpower/gb` with sources, licences and checksums:
  converges in 16 iterations, losses 1905.8260814601151 MW, dense and sparse bit-identical. As shipped it carries
  75,012.14 MW of generation against 56,325.86 MW of demand, so the slack absorbs about 15.5 GW (not physically
  meaningful).
- PyPSA-GB importers. `pypsa-gb-reduced` takes demand and generation from the 29-bus file (the PyPSA-GB CSVs carry
  none) and reports each topology difference from it (three lines, no phase shifters, Errochty 275 kV vs 132 kV):
  21 iterations, losses 2066.1979255895526 MW.
- `pypsa-gb-zonal` (20 zones, 37 links) is shown by the new `zonal` verb and refused by every solving verb (exit 1).
  B4, B6, B7 and B8 boundary cuts are derived from NESO's ETYS boundary map (`tools/gb-reference/derive_boundaries.py`);
  no transfer limits are invented.
- `gb-network-2224` (the Edinburgh original `.m`; pandapower's JSON is a different data version, 60,651.17 vs
  60,077.56 MW load): 13 iterations, losses 1224.9099110491472 MW, dense and sparse bit-identical. PYPOWER with Q limits
  off agrees to 4.4e-16 pu (29 bus) and 3.5e-13 pu (2,224 bus).
- Boundary cross-check on a GridSim-balanced dispatch (`pf --distributed-slack pmax`): B6 3003.93 MW against gb-full
  3779.63 MW, B8 4745.37 MW against 8535.77 MW; no conclusion is drawn from the gaps.
### Changed
- Known limitation: the NESO GB reduced-model files are not bundled; they need a manual download.

## [1.95.122]
### Changed
- Internal: full test run in the normal, AVX2-off and hardware-intrinsics-off modes; no user-visible change.

## [1.95.123]
### Changed
- Overvoltage (59) pickup 1.20 -> 1.10 pu (G99 Issue 2, Table 10.1); the 0.80 pu, 47.5 / 52 Hz and 1 Hz/s settings
  and the 12-degree vector shift (G59/3-8) are now cited. case145 operating elements 40 -> 86 (59 rows 1 -> 47);
  Ireland all-island 0 -> 45; case145 fault mode 3ph 70 -> 74, slg 73 -> 119, ll and dlg 80 -> 85. Every other bundled
  case is byte-identical; exit codes are unchanged; cascades are unaffected (59 never trips a branch).
- Default grading margin (CTI) 0.30 -> 0.40 s (National Grid PS(T)010 A.5; ENWL EPD350 s.4.3). No bundled case is
  affected; output prints "CTI 0.4 s".
- G5/5 THD planning levels follow G5 Issue 5 Table 1: <=0.4 kV 5 %, <=25 kV 4.5 %, <=66 kV 3.7 %, <=230 kV 3 %,
  above 3 %; 400 and 275 kV 2 % -> 3 % (web Harmonics tab only).
- Cited without a numeric change: G5 Table B.3 six-pulse, twelve-pulse and active-front-end spectra as opt-in types
  (the 1/h default stays); the 1320 MW screening step as the SQSS v2.10 Normal Infeed Loss Risk; asset-class LoM and
  LFDD notes; IEC curve constants (ABB handbook).
- Not yet confirmed against source documents: 50N / 51N / 46 pickups, IEC 60909-0 K_S / K_SO, the Li and Bo 2010 LMPs,
  IEEE 14 / 57 base kV, C37.118.2 FREQ, IEEE 4-node voltages and per-fuel carbon factors.
### Fixed
- The WPF app no longer strikes through 1,800 MW (FRCR 2025: the future largest loss).

## [1.95.124]
### Changed
- Every verb accepts only its own flags (from its CLI reference section, the global switches and the writer flags);
  any other flag exits 2. `gda-replay --passive-plant` is documented.
- A non-converged outage reads ISLANDED only when the network really splits, otherwise NO SOLUTION; the count line adds
  "N not solved" and result files gain `not_solved`. Verdicts and exit codes are unchanged.
- `iec60909 --type slg|dlg` with no winding data exits 3 (was 0 with an all-zero duty).
- Screen CSV column renamed to `peak_rocof_hz_per_s`; `losses` gains a case header; `pf` writes result files.
- `modal` keeps console `--json`; `--json x.json` exits 2 and points at `--export`.
- `impedance-stability` splits its exit 1 and exit 2 cases.
- Scripts must expect: exit 3 from slg / dlg without winding data; exit 2 for another verb's flag; refusals of bad
  type / role / boundary names and repeated crosswalk addresses; NO SOLUTION rows and `not_solved`; the renamed screen
  column; the losses header; the impedance-stability exit split; pf result files; modal `--json x.json` exit 2.
- "Taps NOT SETTLED" is shown in the solve report, live dashboard, WPF and web (exit unchanged); "not in the data"
  labels; case and file arguments are found anywhere on the command line.
- `crossval_harmonics` defaults to 4096 machine epsilons.
### Fixed
- The loader refuses a null or unknown bus `type` or generator `role`, naming file, element and value (a null type
  crashed with a NullReferenceException, exit 139); boundary branch names that match no branch or several are refused.
  Every bundled case still loads.
- The loader keeps "Infinity" / "-Infinity"; v1 and v2 schemas accept a number or only the unlimited direction's
  string ("-Infinity" on `qMinMvar`, "Infinity" on `qMaxMvar` and `pMaxMw`). The converter's `"type": ""` for MATPOWER
  generators is now admitted, so a converted case1354pegase passes the SchemaEditor save gate.
- A crosswalk address bound twice is refused.
- Tests fail when bundled data is missing; the deadline race has a deterministic pair of tests.

## [1.95.125]
### Changed
- Web analytics, share image, organisation block, footer links and `security.txt` come from `Site:*` settings. Unset,
  the page loads no third-party script, names no host, and `security.txt` returns 404; a malformed value stops
  start-up and names the setting. The static `security.txt` is removed. Unset `Auth:ApplicationName` means `GridSim`.
- Deployments must set `Auth__ApplicationName` (to the old fallback value, or SSO cookies stop decrypting) and the
  `Site__*` values.
- Live sessions get a solver picker (Auto / Dense / Sparse / Sweep, `?solver=`, `/ws/sim` `"solver"`); unknown names
  and solver requests on replay / bridge feeds are refused. Bridge sessions fill the map overlays and the FRAGILITY /
  RoCoF headroom readout; the substation page's GDA per-asset card queries the bridge.
- The per-frame map message uses a compact column layout aligned to the topology the browser holds and is not resent
  when unchanged: gb-full 1,157,821 -> 580,568 chars per frame (-49.86 %), values decoded bit-identical. `/ws/sim`
  `assetGraph` (5,253,301 chars) is now opt-in (`"assetGraph": true`), a wire change.
- New `Core.Presentation.LiveZoneTable` pairs real lower-voltage in-service neighbours (replacing a GB-only pairing in
  the CLI dashboard) and reproduces the GB 10-zone table byte for byte.
- 16 shared constants live once in `Core.Presentation.PresentationConstants`; values unchanged.
### Fixed
- `/ws/sim` answers a client close with its own close frame (client's status code, 5 s timeout).
- MARKET sparkline takes one sample per 30-minute settlement period (the 1 s replay added up to 1,800 copies of one
  price); the 96-sample window is now 96 periods (2 days).

## [1.95.126]
### Added
- Core `AuditLogFile.Describe` and `gridsim audit show <file>`, which prints every entry only when the whole log
  verifies (a tampered log prints none, exit 3).
- `estimate-export.json` gains `estimate.irlsCapReached` (always written); the audit outcome line gains
  ` irls_cap_reached=true` only when the cap was hit (every other outcome line byte-identical).
- Core `ConverterHarmonicSources` and opt-in `harmonics --converter-sources rated|dispatched`: one source per
  in-service converter unit at phase 0; self-commutated units (battery, PV, wind, VSC HVDC) take the G5 Issue 5 Table
  B.3 active-front-end profile; line-commutated HVDC takes twelve-pulse (on uk: Moyle, IFA, BritNed); linear addition
  per G5 s.6.3.3; orders 2-50 with triplens left out and listed. Default spectrum unchanged. uk: 16 sources, 91 triplen
  components left out, worst THD 5.15 % (rated) / 2.52 % (dispatched).
- `RefuseComponentsOutsideOrders` fails loud for callers that want it; the penetration result lists components at
  orders it did not analyse. Voltages bit-identical; CLI refusal unchanged.
### Changed
- The |Z| of 157 pu at gb-full bus 4992 near h 4.98 is a genuine resonance of the data (Silsden T2, the open end of an
  unloaded 132 kV spur behind a synthesised 0.2222 pu transformer; hand estimate h 4.932-5.112, engine 4.9818).

## [1.95.127]
### Changed
- Performance work; every result is bit-identical to the previous version on dense and sparse cases.
- MeasurementDelay ring buffer and binary search: gb-full asset dynamics 60 s run 17,786-17,991 ms, 119.7 MB ->
  2,172-2,585 ms, 31.0 MB.
- `WaveField.Compute` per frame on gb-full 1,457,070,328 B -> 559,512 B (checks `ContinuumWaveModel.CanForm` first and
  reuses the modes). Known limitation: direct callers still allocate about 1.46 GB before failing on a no-inertia case.
- WPF EXPERIMENTAL tab runs off the UI thread (was 21-44 ms per frame on gb-full).
- EKF reuses its buffers: 3 windows on case588_sdet 474.8 MB -> 147.1 MB; observability gate 49.1 MB -> 38.1 MB.
- `SolveInto` on the complex sparse LU: sparse RMS case300 102.4 MB -> 5.9 MB.
- Dense RMS product on the vector units, row order kept: case300 130-151 ms -> 61-65 ms.
- Kron reduction skips zero pivot-row entries: pegase1354 172-178 -> 132-155 ms, case300 9.9-11.9 -> 4.9-5.7 ms.
- Replay RMSE 937-1,686 ms -> 8-20 ms. The map wave field in WAVE mode (229,908 bytes on gb-full) is not resent when
  identical; `/ws/sim` still sends it every frame.
- The WPF operator frequency colour uses the model frequency.
### Fixed
- Dense RMS networks refuse an input of the wrong length (was a silently shorter result).

## [1.95.128]
### Changed
- One fuel taxonomy in Core, matched by whole-word starts. uk and gb-synthetic wind ("onshore", "offshore") was not
  recognised by the old cost and carbon tables (50 GBP/MWh default, unknown carbon); it is now 1 GBP/MWh and zero
  carbon. Results change: `dcopf uk` total cost 1488000.0000000002 -> 705499.9999999999 GBP/h (dispatch moves),
  `dcopf uk --no-limits` 1464500 -> 582500, `dcopf gb-synthetic --no-limits` 1421778.1575537946 -> 525358.7475012719,
  `experimental carbon` uk and gb-synthetic 233 -> 165 gCO2/kWh. No synchronous or inertia classification changed.
- `nminus1-sweep` gains a `Not solved` column and `not_solved` CSV field (case14 islanded 1 -> 0, not solved 1).
- Every `.m`-loading verb prints the parse notes as `note:` lines on stderr (stdout unchanged).
- Distributed slack takes one reference bus per island; two in one network are refused, naming them.
- RateB / RateC in `transformer.json`. `acopf --prices`.
- The `dcopf`/`acopf` gate message reads "dcopf and acopf only".
### Fixed
- EMT rest state: open switches no longer anchor islands, and capacitors take the t = 0+ inflow. EMT trajectories are
  unchanged; the t = 0 row still holds the pre-switch-on zeros.
- A null generator `type` and "+Infinity" are refused (exit 1). Boundary names are checked on models built in code
  (including GDA bridge streams).
- Derived models keep out-of-service generators. Passive-plant tap hold follows the solved bus type.
- WPF MARKET sparkline takes one sample per settlement period.
- The MATPOWER `gencost` note no longer says the costs are unused.

## [1.95.129]
### Added
- New `restore <case> --order ...` verb (exit 0 when all steps are feasible, 3 when unsafe or unsolved).
- Three-phase sweep gains single- and two-phase laterals, line charging, fixed-tap regulators, switches, capacitors,
  constant-current / impedance and delta loads (models without them solve exactly as before; solvers that cannot
  handle them refuse them). A radial positive-sequence -> three-phase adapter agrees with Newton on case33bw within
  1e-9 pu.
- `dist3ph` accepts a `.m` or JSON case folder and gains `harmonics` (no default spectrum) and
  `estimate --method branch-current`.
- IEEE 123-node feeder bundled with its published results: branch currents within 0.087 A of 628 A and 0.08 degrees;
  voltages worst 5.2e-4 pu (node 95) and 0.053 degrees (node 110).
- Multi-PMU C37.118 DATA frames decode (a one-PMU stream is bit-identical to before); new `pmu-ingest` verb; HTI
  correct-in-place is opt-in (`--hti --hti-correct`), removal stays the default.
- `impedance-stability --outer-loop-only`.
### Changed
- `stress` runs through the shared loadability loop (`ContinuationPowerFlow.ScanGrid`); numbers bit-identical.
- `experimental wave` refuses a no-inertia case before the 1.46 GB build (exit 1 as before, clearer message).
- Ignored small-signal parameters are labelled as such. DNP3 / GOOSE are labelled framing scaffolding in code, help,
  docs and `--list-protocols`. PSS1A switches a stage off when both time constants are zero.

## [1.95.130]
### Added
- A `.json` case argument loads as a `gridsim-case/2` manifest, validated by `V2DocumentValidator` (imports,
  overrides, kind specs); v2 content the model cannot hold yet is refused by name; v1 stays the default. Every bundled
  case's v2 projection gives the same model and a bit-identical solve. Bus `baseKv` is required in v2.
### Changed
- `acopf` names which of three causes stopped it; `dcopf` no longer blames generation capability when line limits are
  the cause. Exit codes unchanged.
- Both simplex solvers refuse a pivot below 1e-12 before the basis changes; at the default tolerance (1e-9) it cannot
  fire, so every LP result is unchanged.
- Loading iberia, france or continental-europe prints a stderr note: only the island slacks hold voltage (225 of 227,
  293 of 295, 1,604 of 1,608 units are fixed injections) against 22.8, 34.5 and 140.7 GVAr of line charging.
- Phase relays read true phase currents and voltages in unbalanced studies, and distance relays the phase-to-phase
  loops (power flows and balanced faults unchanged).
- Converter units' sequence fault behaviour is not defined in the model, so they are listed in a note and the result
  file rather than given invented values.
### Fixed
- `data/matpower/gb/README.md` corrected (gb-full mismatch 28.28 MW / 210.67 MVAr, mostly from 4-decimal voltage
  printing). Documentation: gb-full's lower losses come from 1,229 of its 2,620 transformers at r = 0 and lighter
  transfers.
- `docs/reference/README.md` catalogue rows corrected against the PDFs.

## [1.95.131]
### Changed
- Known limitation: on Linux, results are not bit-identical to Windows; 33 exact-match tests differ in all three SIMD
  modes, likely from the platform C runtime under `System.Math` transcendentals.
- Internal: full test run in three modes on Windows; CI job outage noted.

## [1.95.132]
### Added
- Core `AuditLogDisplay`, one display path for both apps, worded exactly as `gridsim audit show`; a tampered log shows
  only the TAMPERED verdict. Web AUDIT LOG tab (browser upload, capped at 64 MiB); WPF AUDIT LOG... button.
- An `irlsCapReached` flag in the estimate block turns both quality pills red with the warning (absent or false:
  unchanged).
- Web harmonics tab sources picker: the default synthetic study is unchanged; per-converter rated / dispatched as
  `--converter-sources`; lists components and units left out and any `ComponentsOutsideOrders`.
### Changed
- Both crossval scripts default to 4096 machine epsilons (9.09e-13). Impedance: worst 25.55 eps over 18,165
  comparisons (gb-reduced-29); small-signal: worst 0.2165 eps over 18 modal exports.
### Fixed
- `C37118ConfigParser.Parse` checks the frame size and CRC and bounds-checks every PMU block and DATA_RATE; a bad frame
  throws `C37118FrameException`. Good frames parse as before.
- `HtiIdentified.EstimatedError` documentation gives the real units (pu on the system base for P / Q, pu for Vm /
  Imag, radians for angles). The CLI reference row for `--pll-damping` is corrected.

## [1.95.133]
### Changed
- HTI `estimated_error_pu` becomes four CSV columns (value, unit `pu` / `rad` / `pu_base_mva`, value in measured units,
  that unit); console e.g. `0.23808 pu_base_mva (23.808 MW)`. A CSV contract change.
- `crossval_smallsignal` pairs eigenvalues by optimal assignment (tolerances unchanged): gb-synthetic
  `--enable-inverters` 5.000000000000015 -> 6.187947918113335e-08; other exports unchanged.
- The modal export gains `participationAvailable`, `participationUnavailableReason` and `exitCode` (old fields
  identical); the console no longer prints "(rigid COI mode)" against every mode. The exit 2 on MATPOWER cases names
  missing inertia (H = 0).
- `gda --fleet` says when a NESO register is in the lake only as Parquet. The UKPN refusal names the header-only
  RAWData tables.
- Known limitation: gb-full modal analysis takes 47.07 s for eigenvalues and about 1.9 h for participation.

## [1.95.134]
### Changed
- `MatpowerParser` normalises the four raw ohm / kW feeders (case22, case33bw, case69, case85) by default with a note,
  using the same normaliser as the CLI, web and desktop; `MatpowerUnitHandling.AsWritten` opts out. Results are
  bit-identical; a file still raw after one conversion is refused. CLI loads of those files print one extra stderr note.
- `acopf` stops at pass 0 only where no later pass can be feasible; exit stays 3. gb-full 213.2 -> 137.4 s (verdict is
  now the DC infeasibility proved at pass 0); gb-synthetic 3.87 -> 2.39 s.
- gb-synthetic two-node zones' single 400 kV circuits renamed `<tag> 400 tie` (no number changes).
- Fault study, WLS, branch-current estimator and harmonics model the new distribution elements they can define; the
  rest are refused by name. Fed IEEE 123's published currents, the branch-current estimator gives the published
  voltages within 4.376e-4 pu and 0.0557 degrees.
- Documentation: `data/intl/README.md` notes Ireland converges, the physics-check gate audits conservation only, and
  46 of 60 buses sit above the 1.06 pu band (highest 1.1258 pu at bus 9). The 58 duplicated gb-full names are supergrid
  transformers from GDA's spine assembler.

## [1.95.135]
### Changed
- Internal: full test run in the normal, AVX2-off and hardware-intrinsics-off modes; no user-visible change.

## [1.95.136]
### Changed
- Each UKPN table is read from its RAWData CSV when that has rows, otherwise from the lake's Parquet copy with each
  cell given the CSV reader's meaning; a text column turned numeric is refused (exit 1); neither source with rows
  gives a refusal naming both. A `note:` line per table read from Parquet; 8 generation capacities with 16-17
  significant digits may be one binary unit off the CSV and are reported. NGED and NPg outputs are byte-identical.
- Known limitation: UKPN builds (5,720 buses, 7,034 branches, 22.69 GW, 103 islands) but does not converge.
- The dense complex LU works column by column in cache-sized blocks with a vectorised inner update (same operations in
  the same order per entry), and per-eigenvalue inverse iterations run in parallel (bounded by memory). Every
  eigenvalue, eigenvector and participation factor is bit-identical in all three CPU modes. LU at order 1,902
  7.2 -> 2.7 s; full decomposition at order 600 117.7 -> 16.8 s; gb-full estimated about 28 minutes (was about 1.9 h).

## [1.95.137]
### Fixed
- UKPN tap range is read as percent of nominal ratio (e.g. 90 / 110) and converted to an offset; a blank cell is
  refused with a warning naming the unit. Tap changers 0 -> 2,169.
- UKPN data reading: transformer impedances are percent on 100 MVA, not on rating; table 2b (143 three-winding
  transformers) is read; 100 node codes naming different substations in different licence areas are kept apart (12
  genuine boundary sites stay one node); substation demand is shared over feeding transformer LV nodes by winter
  rating; 160 grid-substation demand rows (9,746.7 MW) are no longer counted twice; one GSP infeed per GSP group per
  island at every section of the GSP site.
- UKPN on the full lake: 6,023 buses, 7,463 branches, 63 islands, demand 13.82 GW (was 22.69 GW), 221 GSP infeeds;
  converges in 4 NR iterations and 8 tap passes, mismatch 1.83e-9; voltages 0.865-1.010 pu; losses 169.7 MW; worst
  infeed 7.82 % of capacity (Walpole GIS 132kV). NGED and NPg outputs are byte-identical.

## [1.95.138]
### Changed
- Internal: project decisions recorded; no code change.

## [1.95.139]
### Changed
- Internal: CI (`ci.yml`) runs on release tags (`v*`) and on demand only; `codeql.yml` and the Dependabot auto-merge
  workflow run on demand only; Dependabot opens updates monthly.
- The NESO GB Reduced Model is published only as PowerFactory binaries; importing it needs an ASCII DGS export.
  `data/reference-inbox/` third-party binaries are git-ignored.

## [1.95.140]
### Changed
- Known limitation: on Linux GridSim is not bit-identical to Windows (33 exact-match tests differ in the last binary
  digits). .NET calls the operating system's C runtime maths library for transcendental functions (Microsoft C runtime
  on Windows, glibc libm on Linux), which rounds some results differently, and the iterative solvers carry the
  difference forward. Windows is the reference platform until GridSim has its own deterministic maths routines. README
  gains a "Known limitation" section.
- Internal: CI Linux jobs report but no longer fail the workflow.

## [1.95.141]
### Changed
- GridSim.Gda and the GDA data are not release artefacts; the `gda` verbs stay in the CLI. The neutral web defaults
  are the release defaults.
- Internal: GDA data-root configuration and GDA repository follow-ups.

## [1.95.142]
### Changed
- Known limitation: bit-identity across CPU tiers was overstated in the README. Three paths are not yet
  tier-independent: the dense WLS gain formed with FMA where available, `SimdKernels.Dot` summation order set by the
  vector width, and `SimdKernels` SinCos handing a width-dependent tail to the OS maths library. README lists both gaps
  (CPU tiers and Linux) and the planned fix: non-FMA fixed-order reductions and GridSim's own transcendental routines.

## [1.95.143]
### Changed
- Documentation: rewrote manual ch. 27 "Validation tools" (`crossval` and Python sidecars, `physics-check`,
  `conformance`, estimation checks, `simdcheck`, `batchsolve`, `gpucheck`, with a per-instrument exit-code table
  including exit 77), technical ch. 11 "Validation" and technical ch. 16 "Physics conformance and the constrained
  estimator".
- Known limitation: `SimdKernels.Dot` and the FMA `AddScaledRow` in `DenseWlsStep` are not bit-identical across vector
  tiers or FMA and non-FMA CPUs; `physics-check` never tests convergence; `gpucheck` exits 0 on FAIL; `conformance`
  exits 3 on every run of the shipped corpus.
- `tools/docs`: the CLI executable is `gridsim.exe`.

## [1.95.144]
### Changed
- Documentation: new manual ch. 6 (`dcpf`, `dcopf`, `acopf`, `cpf`, `taps`, `ptdf`, `lodf`, `losses`, with generator
  costs via `--costs` and `--prices`) and new technical ch. 29 (DC-OPF formulation, security simplex, the AC loss loop
  as a DC-OPF with losses rather than a full AC-OPF, CPF, PTDF/LODF, loss allocation and the tap optimiser).

## [1.95.145]
### Changed
- `nminus1`, `nminus2` and `n-1-1` use per-bus voltage limits by default; `--global-vlimits` reproduces the old
  reports byte for byte; `--bus-vlimits` is accepted as a no-op, and giving both flags exits 2. Verdicts change on 13
  bundled names (including case30, case24, cigre, gb-reduced-29, pypsa-gb-reduced); exit 3 on every one either way.
- Every CPU power flow routes to the sparse engine; the 200-bus threshold remains for the estimator, GPU routing and
  solve exports. No numbers change; dense stays available by explicit choice.
- The default harmonic source is the G5 Issue 5 Table B.3 six-pulse profile; `harmonics --synth-spectrum ideal` (and a
  web choice) restores the 1/h spectrum. THD and compliance results change.
- `cpf` solves on StudyOptions inside the OLTC loop, as `stress` does; the printed lambda is identical on all 30
  bundled cases run.
- A no-inertia wave case fails before the dense build (same exception). The RMS CCT search runs once.
- `/ws/sim` sends the wave field only when the start frame asks `"waveField": true` (a wire change).
- `dcopf` exits 3 on an infeasible dispatch (was 1).
- Every verb's default output goes under `results/` in the current directory (explicit `--out` unchanged; `synth`
  still builds `data/gb-synthetic`). Scripts reading the build folder, `./emt-out`, `./harmonic-export.json`,
  `./exports/` or `docs/developer/benchmarks/benchmark-results.md` need updating.
- Prior-solved measurement rows stay in the fit but no longer count in chi-square J or degrees of freedom; runs
  without them are bit-identical.

## [1.95.146]
### Changed
- Without `--root`, `gda` reads `GDA_DATA_ROOT`, then `GDA_ROOT`; a set variable naming no folder stops with exit 1,
  naming the variable (never its value), without falling back. `lake_conformance.py` reads the same order.
- 86 name lines in `data/gb-full/transformer.json` made unique (every number identical). The README records possible
  double-counted SGT capacity (3,840 MVA at 7 GSP groups) as unverified.
- New gb-full and gb-synthetic READMEs.
- Below scale 1.0 each zone's demand, embedded plant and transmission fleet shrink with its 11 kV layer; 0.95, 1.0 and
  2.0 are byte-identical; every scale 0.02-2.0 now solves (0.02 carries 14.5 GW, was 39.5 GW).
- NGED generation joins on the canonical primary key (generators 2 -> 284, 0.00 -> 1.09 GW; losses 212.1 -> 205.8 MW);
  NPg demand joins by site name and nearest voltage within 5 % (34 -> 86 points, 0.26 -> 2.27 GW); UKPN byte-identical.
  Known limitation: the NPg full-lake solve no longer converges (exit 3), from one 33 kV island fed by a single infeed.
- Rated lines no dispatch can relieve are left out of the security LP and named (console and result file); every
  previously feasible LP is bit-identical. gb-full: `dcopf` INFEASIBLE (exit 1) -> Optimal 53,285 MW at 312,021 GBP/h,
  exit 0; `acopf` converges in 6 passes, losses 991.204 MW. gb-synthetic: 53 lines left out, `dcopf` / `acopf`
  feasible (602,945 GBP/h).

## [1.95.147]
### Changed
- Documentation: new manual ch. 15 "RMS, modal and impedance stability" and technical ch. 30 "Small-signal and
  impedance stability" (swing kernel, CCT search, modal analysis, converter admittances, Nyquist, SCR sweep, with case9
  values recomputed by hand).
- Known limitation: the classical swing kernel's sums depend on SIMD width for 4 or more machines; an unknown
  `--fault-bus` crashes; the SCR-boundary line says "stable" when every swept SCR is unstable; `crossval_impedance.py`
  exits 1, not 77, on a `--grid real` export.

## [1.95.148]
### Changed
- Built-in `case9` reads the embedded `case9.m` and adds only the published WSCC MBase and H, so name and file give
  identical results. Built-in name results change: nminus1 5 -> 4 insecure outages; dcopf bus 2 45 -> 35 MW plus bus 1
  at 10 MW (Pmin), cost unchanged; acopf losses 12.242 -> 11.615 MW, cost 16,362 -> 16,331 GBP/h; `--costs case` works.
- SP02 (2026-09-01) estimation with the new chi-square rule: J 301.585 -> 245.859, dof 317 -> 2, threshold
  359.52 -> 5.94, verdict PASS -> FAIL, bad-data removals 1 -> 20 (the cap).
- NPg takes the per-GSP-group-per-island infeed rule, and a site-name join connects 308 transformer windings. Full lake
  3,197 -> 2,892 buses, 649 -> 364 islands; converges in 4 iterations (mismatch 1.16e-10, V 0.948-1.017 pu); worst
  infeed 5.98 % (West Boldon 66kV). NGED and UKPN byte-identical.
- The gb-synthetic builder marks Moyle, IFA and BritNed line-commutated and sizes every radial branch to the parallel
  standard units its peak demand needs (EREC P2 Issue 8 Table 1; 84 branches became two units). Unrelievable lines
  53 -> 2; acopf final pass left out 86 -> 5, losses 1,425.8 -> 1,385.1 MW; dcopf cost unchanged at 602,945 GBP/h.
  `data/gb-synthetic` regenerated.
- `lake_conformance.py` reads `GDA_ROOT` as the DataSources folder, as the CLI does.

## [1.95.149]
### Changed
- Documentation: new manual ch. 28 "Benchmarking and GPU" (`bench`, `batchsolve`, `gpucheck`, `simdcheck`, `cycle`,
  GPU backends, reference results, renting a GPU server). `docs/benchmarking.md`, `GRIDSIM_BENCHMARKS.md` and
  `gpu-rental-runbook.md` are folded in and replaced by stubs; older stubs and links are repointed.

## [1.95.150]
### Added
- 1.0 ships the CLI as self-contained binaries (win-x64, linux-x64, osx-arm64), GridSim.Core as a NuGet package and
  the web app as a replay-only container image (`ghcr.io/<owner>/gridsim-web:<version>`). No web or desktop zip; the
  Release page holds three CLI archives and two SBOMs. GridSim.Gda is not packable. README gains "What ships in 1.0".
- The Windows CLI binary and the NuGet package are signed in the release workflow from repository secrets
  `SIGNING_CERT_PFX_BASE64` and `SIGNING_CERT_PASSWORD` (optional variable `SIGNING_TIMESTAMP_URL`); without the
  certificate the release builds unsigned with a warning; a certificate without its password fails the run.
- `THIRD-PARTY-NOTICES` gains a "Bundled network data" section with the Edinburgh citation.
### Changed
- A licence key carries its major version inside the signed part: a 1.x key works on every 1.x build and is refused on
  2.x (free tier; `licence status` names both versions; `licence import` refuses and writes nothing). Existing keys
  count as 1.x; a 2.x key is refused on 1.x.
- The estimate export writes `chiSquarePassed` as null when the test did not run (a wire change); audit and console
  say "not run".
- `GdaModelWriter` (behind `synth` and `gda --out`) keeps every generator field a JSON case can carry (including
  out-of-service units and unlimited limits); fields JSON cannot hold (X''d, X2, cost curves) get a `note:` line.

## [1.95.151]
### Changed
- Internal: full test run in the normal, AVX2-off and hardware-intrinsics-off modes; no user-visible change.

## [1.95.152]
### Changed
- Documentation: new manual ch. 26 "Audit log, deployment and assurance", technical ch. 31 "Audit log and
  diagnostics" (the `gridsim-audit/1` hash payload, writer, verifier, guard seams) and technical ch. 35 "Deployment and
  security".
- Known limitation: replay-only mode redirects only `/`; `serve --utc-now` can move the horizon; two guard seams are
  off on the live paths; SSO tickets are not single-use without a distributed cache; undated measurement sets skip the
  guard.

## [1.95.153]
### Changed
- Documentation: new manual ch. 13 "Battery storage studies" (`--bess` sites on `chain` and `gda-replay`, all six
  chemistries) and rewrote technical ch. 27 "Battery energy storage". Price and LP policies on `chain` are not
  documented as available.
- Known limitation: the CLI reference lists `chain --prices` as a live flag, but the CLI refuses it.

## [1.95.154]
### Changed
- Documentation: rewrote manual ch. 1 "Getting started" and ch. 3 "Configuration" (every environment variable,
  settings, licence and crash-log locations, MATPOWER name resolution, default output paths). The Linux CI caveat is
  removed from manual ch. 1, 3 and 26 and technical ch. 35.

## [1.95.155]
### Changed
- Documentation: rewrote manual ch. 30 "Troubleshooting" (symptom index, full exit-code table: 0, 1, 2, 3, 77 and
  -532462766 on Windows) and ch. 31 "Exports and formats" (file index, `run-metadata.json`, non-finite handling).
  Ch. 22 quotes the current GDA `GRIDSIM_CLI` messages.

## [1.95.156]
### Changed
- Documentation: rewrote manual ch. 8 "The desktop app".
- Known limitation: the ASSETS jumps land on ABOUT, and the explorer refreshes only while ABOUT is selected; the first
  non-dash launch argument is taken as the model even when it is a flag value.

## [1.95.157]
### Changed
- Documentation: rewrote manual ch. 10 "Analysis views" (thirteen analysis tabs, with formulas, thresholds and colour
  rules).
- Known limitation: MARKET keeps bridge prices after leaving the bridge; desktop asset pages show hard-coded values
  untagged; web SCADA raises a frequency excursion at P1 where the desktop uses P2.

## [1.95.158]
### Changed
- Documentation: rewrote manual ch. 29 "Extending GridSim", a developer guide to every extension seam; every code
  skeleton compiles against the current code.
- Known limitation: `cycle --jobs bogus` runs nothing and reports success; the v1 asset schema lists kinds the loader
  refuses.

## [1.95.159]
### Changed
- Documentation: rewrote technical ch. 1 "Architecture"; `docs/architecture.md` is folded in and is now a stub.

## [1.95.160]
### Changed
- Documentation: rewrote manual ch. 2 "Loading networks" (built-in names, MATPOWER resolution, JSON case folders, GB,
  international and distribution cases, the 15 placeholder sites); `docs/developer/case-libraries.md` is folded in.

## [1.95.161]
### Changed
- Known limitation: `BundledNetworks.IsBundledPath` treats any path with a `data/<bundled folder>` segment as bundled,
  so a user network placed under such a path escapes the licence bus limit. `DataFolders` also lists `gb-reduced`,
  which has no folder.

## [1.95.162]
### Changed
- Documentation: rewrote manual ch. 4 "Licensing and activation" and technical ch. 22 "Licensing: key format and trust
  model" (byte layout, ECDSA P-256 signature, verification steps, a worked decode of a throw-away test key).
- Known limitation: `--importLicence=` crashes; `--importLicence` takes the next flag as its file; a future-dated key
  applies at once; EXPIRED shows "unlimited buses"; `gridsim help licence` says gb-full counts.

## [1.95.163]
### Changed
- Documentation: rewrote technical ch. 2 "Data model and per-unit conventions".
- Known limitation: copies from in-service generators drop out-of-service units; `Validate` misses `BaseMva`,
  `FrequencyHz` and zero-impedance branches.

## [1.95.164]
### Changed
- Documentation: rewrote technical ch. 4 "Sparse and GPU numerics" (CSR builders, orderings, LUs, iterative solvers,
  `SimdKernels`, GPU backends, a 62-row tolerance table).
- Known limitation: FMA and vector `Dot` paths in the estimator and classical swing; the `CsrMatrix.Builder` sort is
  unstable; GPU tie-break beyond 256 or 128 candidate rows.

## [1.95.165]
### Changed
- Documentation: rewrote technical ch. 8 "Provenance and invariants" (provenance tags, the `[VERIFY]` check and the
  placeholder sites, the 169-hour guard rule, determinism and fail-loud rules, where the code misses the CPU-tier
  contract). Technical ch. 4 lists FMA gain assembly as reaching `ConstrainedDenseWlsStep` and `QrWlsStep`.

## [1.95.166]
### Changed
- Documentation: re-verified manual ch. 19 "CLI reference" for all 66 verbs; `pf`, `dcpf`, `convert`, `validate`,
  `zonal`, `pmu-ingest` and `audit show` gain full sections. Corrected defaults: `protection-check --ov` is 1.10 and
  `grade --cti` is 0.40. Documented: `chain --prices` and `--policy lp` exit 2; `--ticks 0` exits 2; `conformance`
  exits 3 if a case is skipped. New tables give each verb's case-loading form and unknown-case exit code.

## [1.95.167]
### Changed
- Documentation: rewrote technical ch. 15 "Protections".

## [1.95.168]
### Changed
- Documentation: new technical ch. 32 "CIM/CGMES import and export"; `design/cim-cgmes-mapping.md` status now includes
  three-winding transformers.

## [1.95.169]
### Changed
- Documentation: new technical ch. 33 "C37.118 synchrophasor ingest"; `design/c37118-synchrophasor.md` status records
  the `pmu-ingest` verb.
- Known limitation: a zero TIME_BASE is not checked, and frame instants can land one tick short.

## [1.95.170]
### Changed
- Documentation: rewrote technical ch. 14 "Cascade validation and blind prediction", including the 9 August 2019
  cascade.

## [1.95.171]
### Changed
- Documentation: rewrote technical ch. 17 "Protection assessment", with `fault` and `protection-check` exit codes.

## [1.95.172]
### Changed
- Documentation: rewrote technical ch. 24 "Standard dynamic model library". Manual ch. 15 and ch. 19: `--rms` runs one
  clearing-time search per bus, not two. Technical ch. 30: the classical `X'd` conversion floors `MBase` at `1e-6`.

## [1.95.173]
### Changed
- Documentation: rewrote technical ch. 25 "Short circuit (IEC 60909)".

## [1.95.174]
### Changed
- Documentation: rewrote technical ch. 26 "Overcurrent coordination" (IEC and IEEE curves, grading margin, the `grade`
  verb including decimal-comma input).

## [1.95.175]
### Changed
- Documentation: rewrote technical ch. 13 "Asset-dynamics data grounding".

## [1.95.176]
### Changed
- Documentation: new manual chapter 7, "Three-phase distribution feeders": feeder and measurement file formats, the
  radial-tree rule and its refusals, the unbalanced sweep on the IEEE 4-node and 123-node feeders, sequence-impedance
  fault studies, state estimation (`wls`, `wlav`, `branch-current`), harmonics, lifting a positive-sequence case,
  result files, exit codes and limits.
- Known limitation: `branch-current` estimation takes a missing phase as zero current.

## [1.95.177]
### Changed
- Documentation: new manual chapter 12, "Faults, short circuit and protection grading", covering `fault`,
  `protection-check` (steady state, fault bus, cascade against an alarm log), `iec60909` (single bus, every bus, branch
  currents, minimum duty, breaker checks, correction factors), `grade` and `trip --asset-dynamics`, with limits
  including where `fault` and `protection-check` treat `--zf` differently.

## [1.95.178]
### Changed
- Documentation: re-checked manual chapter 5, "Running power flows" (which paths exit 3 on non-convergence and which,
  `--enableExport` and `--ticks`, exit 0; the live-loop rescale with its 2 % slack balance; the `--trip-window`
  default; the `slackQNote` export field) and technical chapter 3, "Power flow" (the opt-in wrappers
  `DistributedSlackPowerFlow`, `OltcLoopPowerFlow` and `SwitchedShuntPowerFlow`, and the Newton step constants).

## [1.95.179]
### Changed
- Documentation: rewrote technical chapter 10, "File and wire formats": every document GridSim reads or writes, with
  schema id, fields, types, units and version rules, including the solve, estimate and estimated-solve exports (whose
  `maxMismatch` is the last state update), input files, the CLI result document, bridge and web messages, the audit
  line, the run-metadata sidecar, CSV reports and GDA lake tables.

## [1.95.180]
### Changed
- Documentation: twelve manual and technical chapters rewritten in the present tense; the open question on the EMT
  `t = 0` sample is listed under technical chapter 21 limits.
- Documentation: technical chapters 6, 9 and 29 state that the CPU power flow is always sparse (`DefaultSparseThreshold`
  0) and that 200 buses is the GPU and estimator crossover (`DenseSparseCrossoverBuses`).
- Documentation: every chapter under `docs/manual` and `docs/technical` passes `tools/docs/lint.py --strict`.

## [1.95.181]
### Changed
- Documentation: rewrote technical chapter 18, "Real-time, distribution and dynamic estimation": the EKF, UKF and EnKF,
  the RoCoF filter, the `serve` engines and their exit codes, the `gridsim-dist3ph/1` formats, the three-phase sweep,
  sequence faults, three-phase WLS and WLAV, and the branch-current estimator, with the IEEE 123-node agreement.

## [1.95.182]
### Changed
- Documentation: rewrote technical chapter 28, "JSON v2 schema": the schema subset, library files, templates, kind
  specs, the code-default register, the case manifest, network document, imports, overrides and bindings, and the
  `validate` verb and v2 case loader with every message and exit code.
- Known limitation: `validate` accepts a `2.0` schema version written as an integer that the v2 loader refuses.

## [1.95.183]
### Fixed
- Fixed data loss: `gridsim export <manifest.json>` overwrote a v2 case manifest, because `export` took any free `.json`
  argument as its output path. A `.json` file that is a GridSim case manifest is now read as the case and never
  written, and `export` refuses (exit 2, naming the file) to overwrite any existing file that is not a scene it wrote.

## [1.95.184]
### Changed
- Internal: planning update; no code change.

## [1.95.185]
### Added
- `DetMath`, GridSim's own maths library: a C# port of FDLIBM 5.3 (the library Java's StrictMath is defined by), proved
  bit for bit against Java 21 StrictMath. The licence notice is in `THIRD-PARTY-NOTICES.md` and the Core package.
### Changed
- Results are now bit-identical across CPU tiers (AVX-512, AVX2, non-FMA) and operating systems: the dense WLS gain no
  longer uses fused multiply-add, `Dot` sums in one fixed order at every vector width, batch SinCos uses one routine
  for every element, and every transcendental call in Core, Gda and the CLI goes through `DetMath`.
- Changed results: some outputs move in the last bits, including the two `iec60909` CLI outputs and the case57 noisy
  estimate objective J (407.14427112449795 -> 407.14427112449533). Cross-validation against pandapower is unchanged
  to the bit on case9-case118 and moves by 1e-14 or less elsewhere.
- Performance: `DetMath.SinCos` costs about the same as `Math.SinCos`; `Exp` and `Cos` cost about 2-3x.
- Known limitation: identity on Linux is proven only once CI runs on Linux; GPU backends are outside the bit-identity
  guarantee. The README "Known limitations" section describes this.
### Fixed
- Fixed the JSON loader's default source relying on atan2 returning its angle exactly.
- Fixed Francis QR failing on the defective 116-state gb-synthetic inverter case: the iteration cap is now LAPACK's
  30*max(10, n). Every run that converged before gives bit-identical results.

## [1.95.186]
### Added
- `bench --cases a,b,c` benchmarks only the named cases.
### Changed
- Changed exit codes (scripts must check these): an unknown case name exits 2 on every verb (was 1 on `pf`, `fault`,
  `dcopf` and the other single-case verbs); `fault --bus` not in the case 1 -> 2; a missing or unloadable case file or
  folder exits 1 (`modal` missing `--dir`, and `stability` / `modal` with an unloadable named case, 2 -> 1); a batch
  keeps its LOAD FAIL row and exits 1; `cpf data/intl/france` 0 -> 3; `bench` and `gpucheck` can exit 1 or 3.
- `stability` and `modal` take their case like the other verbs: a positional case and a `--dir` that disagree exit 2.
- An unknown bus exits 2 before any solve, without "(Parameter ...)" text.
### Fixed
- Fixed unhandled exceptions, now refused: `freq` / `replay` / `replay-event --trace` on a fresh trace print
  `REJECTED (forward inference)` and exit 2; `stability --fault-bus` not in the case exits 2; `dcopf` / `acopf` with
  Pmin above Pmax exit 1; `gda-replay --bess` with an unknown bus exits 2; `--importLicence=` exits 2; `serve` skips a
  non-JSON window file, names it and exits 1. Any missed forward-inference rejection maps to REJECTED / exit 2.
- Fixed false exit 0: `cpf` exits 3 when the base load fails but a lighter load converges; `gpucheck` with a FAIL line
  and `bench` / `bench --estimate` exit 1 if anything failed to load, else 3 if anything failed; `fault` exits 3 when
  the converter iteration does not converge; `--bfs` on a meshed case with `--enableExport` or `--ticks` exits 3.

## [1.95.187]
### Added
- `Auth__KeyProtection` chooses how the web key ring is encrypted at rest: `auto` (Windows DPAPI for the current
  account, else a certificate from `Auth__KeyCertificatePath` / `Auth__KeyCertificatePassword`, else plain with a
  start-up warning), `dpapi`, `dpapi-machine`, `certificate` or `none`. Existing plain keys stay readable.
- `GridSim__MaxSessions` limits web sessions (default 64).
- `Sso__PublicOrigin` sets the public origin behind proxies that do not pass X-Forwarded-Proto. Deployments behind such
  a proxy must set it.
- An `/Error` page and a Development `launchSettings.json` for the web host.
### Changed
- `POST /api/solve` answers 200 only when the solve converged, else 422 with `converged: false`, the status and the
  state.
- `Sso:IsAuthority=true` stops start-up (GridSim has no login page); unauthorised requests get 401 / 403 instead of a
  redirect to a missing login page.
- OpenAPI responses are filled in; a `source` field that does not match the case's feed is refused, as is an unknown
  `@source` suffix.
- `/ws/sim` answers every frame it cannot act on with an error frame.
- A blank or missing case answers 400; a licence failure at start-up is reported clearly.
- Replay topology is cached per run (a case118 6-frame run: 1879.1 ms -> 2.768 ms).
- Documentation: technical chapters 35 and 10, manual chapter 26 and the deploy README.
### Security
- On a replay-only host, every `/dashboard/<id>` except `bridge:<case>@raw` answers 404 before any page code runs, and
  sessions cannot be created for those ids, including by in-browser navigation.
- An SSO ticket can be used only once (an in-memory store, plus a shared cache when one is registered); wrong-audience,
  expired, tampered and nonce-less tickets are refused.
- The web key ring is encrypted at rest by default.
- The nginx example marks the API `no-store` (a `^~ /api/` block); it previously cached `/api/openapi.json` for a day.

## [1.95.188]
### Added
- `serve --audit-log`; the web host's resident estimator writes one audit entry per run when `Audit:LogPath` is set
  (start-up stops if it is set without a resident estimator).
- Audit anchors: each audit log gets an anchor file (`<log>.anchor` or `--audit-anchor <file>`); a truncated, restored
  or rewritten log is refused (exit 1) before anything is appended. An old log without an anchor is accepted with a
  warning and its entry marked `audit-anchor=missing`.
- `pmu-ingest --channels <map.json> --measurements-dir <dir>` writes one measurement set per aligned instant through
  the guarded mapper.
### Changed
- `serve --utc-now` may only be earlier than the real clock (a later one exits 2); `serve` and the web feed check each
  window against the real horizon; the real-time engines refuse a "now" later than the clock.
- `interpose` judges every window against the real horizon and never writes live windows to `--measurements-dir`
  (they are kept only as quarantine captures in `--out-store`); a run with `--measurements-dir` that held any window
  exits 2.
- An undated measurement set is refused (`estimate`, `serve` exit 2); an undated harmonic set is refused only when it
  holds measured spectra; presolved replay checks every dated row against the horizon.
- `--utc-now`, `--phaseangle-at` and the dates in `--phaseangle` accept ISO 8601 only (`01/02/2100` exits 2).
- `pmu-ingest` refuses a capture with any frame newer than the horizon (exit 2). Samples flagged invalid,
  unsynchronised or with unreliable frame time are dropped, counted per PMU and reason, and the run exits 3 (cut-off
  10 microseconds of time error, 0.18 degrees at 50 Hz).
- A network counts as bundled only under the install's own `data/` folder (or the source checkout's when run from a
  build inside it); a large case copied under another `.../data/matpower/` is refused on the free tier. The stale
  `gb-reduced` folder entry is removed; the name still loads `data/uk`.
- Tests: the required-fields measurement fixture is dated; the audited-verb list includes `serve`; paths outside
  the install's `data/` folder are not bundled; the clean PMU capture uses status 0.

## [1.95.189]
### Added
- `docs/Public/GB`: a git submodule linking the public grid-documents repository from which GridSim's cited values
  were taken. It is not part of GridSim and a normal clone leaves the folder empty; `git submodule update --init
  docs/Public/GB` fetches it (about 60 MB), as does `--recurse-submodules` on clone (see `docs/Public/README.md`). CI
  does not fetch submodules, and the docs lint skips `docs/Public`.

## [1.95.190]
### Added
- `SUBMODULES.md`: the rules for git submodules (never required to build, test or run; they cannot change the core
  maths; their data goes through the normal loaders, provenance checks and horizon guard; pinned by commit), the current
  list, planned data-library submodules, and how to fetch, update and add one. The README links to it.

## [1.95.191]
### Changed
- Tests: shorter suite runtime (slow tests run in parallel; at most two dense solves over 2,000 buses at once), and the
  zero-allocation test now needs three consecutive clean 20-solve windows after warm-up. Estimation and power-flow
  timings were measured unchanged after 1.95.185.

## [1.95.192]
### Changed
- Changed results: IEC 60909 kappa takes the 1.15 factor for meshed networks, so peak current ip rises by 1.00-1.15x
  (case9 bus 5 kappa 1.7351642985814115 -> 1.995438943368623, ip 3.6032937549618294 -> 4.143787818206103 kA). Ik'', Ib,
  Ik, Ith, m, idc and R/X are bit-identical.
- `iec60909 gb-full --bus 35` exits 0 (was 1); `--all-buses` studies 3,536 buses and refuses 3 by name (a
  converter-only island, buses 6773-6775). The Ik line says which steady-state rule applies.
- Changed results: fault-state branch loading takes both ends (case9 bus 5, branch 5-6 8.8e-15 -> 114.546 %); no relay
  verdict moves.
- Relays are listed fastest first; "primary clearance" names the faulted bus's 87, else the fastest timed branch
  element (`fault uk --bus 1` now names 87). `trip` prints 81U, 81O, 81R and 78 on its trajectory.
- Changed exit codes: `grade` with a backup that never operates exits 3 (was 0); `grade --current 2000,5` exits 2 (the
  list must be strictly ascending); `fault --zf -0.1` exits 2 (was 0).
- Headers say `Zf=j...` (`fault`, `protection-check`) or `(resistive)` (dist3ph); -inf prints as -inf;
  `iec60909 --branches` prints every row and `--top N` limits it.
- Asset protection: an overload stage on RateA <= 0 is refused; `capacityMw` is range-checked; dwell is counted in
  whole steps (0.2 s at 20 ms fires after 10 steps, was 11).
- Known limitation: the dense and sparse fault engines differ by up to 4.8e-15 relative.
- Tests: five CLI stdout fixtures re-captured for the IEC 60909 and relay-order changes.
### Fixed
- Fixed 21N operating without earth current at the relay (it no longer operates on LL faults).
- Fixed relay 37 considering out-of-service branches.

## [1.95.193]
### Changed
- Changed results: the inertia cross-check scales the 1 s windowed RoCoF by the replay's own instantaneous-to-windowed
  ratio before inversion (gb-2023-12-22: 245.098 -> 193.587 GVA.s; recorded 161.0). Evidence on gb-2023-12-22 scores
  simulated RoCoF -0.123 Hz/s (was -0.155).
- Changed results: `--device data/devices/tsgb-fleet.json` uses the device's own values (gb-largest-loss-1320 fleet
  nadirs 49.64 / 49.48 -> 49.65 / 49.50 Hz; FFR at the nadir 811 / 598 -> 795 / 633 MW).
- Each recorded action in an event catalogue states its kind (`DemandDisconnection`, never replayed;
  `OperatorInstruction`, kept); a catalogue without kinds is refused (exit 1) and the loader checks the schema id.
- `freq` / `replay` on a file with no data rows or a damaged line exit 1 naming the line (was exit 0).
- `transient` honours `--forming` and `--asset-dynamics`; `trip` refuses `--asset-dynamics` without an asset bundle
  (exit 1) and with `--inertia-model distributed` (exit 2).
- `--trace` is accepted on `scenario`, `fleet`, `evidence` and `h0-study`.
- `--voll-source` must name S322, S323 or S324 with a figure that source states.
- The back-test gate runs whenever a trace is attached; a failure caps Reproduced at Marginal.
- `trip --gb-services[=scale]` adds DC / DM / DR on top of the generic governor (gb-synthetic nadir 48.334 -> 49.127
  Hz). Tier B voltage and loading checks run on the trip path.
- Inverter RoCoF confirm time is counted in whole steps (0.2 s at 20 ms trips on step 10, was 11).
### Fixed
- Fixed fleet conclusion lines contradicting the table (gb-2019-08-09 printed "RoCoF identical" beside different
  values).
- Fixed "MW lost to LoM" counting trips other than LoM.
- Fixed an infinite dwell firing on the first step.

## [1.95.194]
### Added
- `schemas/schemaNotes.md`: a guide for producers of GridSim data: the v1 folder and v2 manifest, network, library and
  kind formats, the 16 kinds, the rules GridSim enforces (unknown keys refused, closed unit vocabulary, provenance with
  `[VERIFY]` for placeholders, immutable id@rev, explicit overrides, unique names, data only, the no-forward-inference
  guard, plain ASCII, validate before hand-over), and what is not supported yet (time-varying profiles).

## [1.95.195]
### Added
- `emt --allow-non-passive`: branches with negative R and shunts with negative G are refused by default at the CLI;
  `emt case145` and `emt pglib_opf_case588_sdet` exit 2 unless the flag is given.
- `run-metadata.json` gains a `parameters` object (probes, events, model switches); `summary.md` reports whether CDA
  actually ran.
### Changed
- Changed results: `CsrMatrix.Builder` sums duplicate cells in a defined order, so EMT and distribution-factor results
  on larger cases move in the last bits (largest EMT change 9.7e-11 pu on gb-full); power-flow tracing is
  bit-identical. DC-OPF security results on five PGLib cases move by at most one ulp in cost with the same binding
  lines; on pglib case588, 44.06 MW moves between two generators at identical cost.
- EMT refuses (exit 2, nothing written) negative, NaN or beyond-the-end event times, fault resistance <= 0, switch
  words other than close / open, probes that name nothing, a duration that gives no step and negative `--freq`. Core
  refuses duplicate ids, two ideal sources on a bus, Rs <= 0 or NaN and bad Bergeron parameters by name.
- Tests: seven EMT digests and five DC-OPF security parity rows updated for the defined summation order.
### Fixed
- Fixed duplicate sparse-matrix cells being summed in an unstable order for rows over 16 entries.
- Fixed a second EMT fault at the same bus using the first fault's resistance.
- Fixed negative `--freq` silently running at DC.

## [1.95.196]
### Added
- `conformance --no-allowances` turns off the allowance list.
- Cross-validation sidecars try `GRIDSIM_PYTHON`, then `VIRTUAL_ENV`, then `PATH`, and print the interpreter used; a
  `GRIDSIM_PYTHON` naming nothing exits 1. Missing Python packages exit 77 with a clear message.
### Changed
- One pass rule everywhere (exports, `physics-check`, `conformance`, both Python sidecars): BaseMva x 1e-8 MVA. This
  is exactly 1e-6 on 100 MVA cases (verdicts unchanged) and 1e-4 on the 10,000 MVA uk case, which now passes. A
  negative `--tol` exits 2.
- `conformance` on the shipped corpus exits 0 (was 3): the three never-converging PGLib cases are on an explicit
  allowance list; any other unconverged case, or one of those failing differently, still exits 3.
- `physics-check` fails a non-converged solve (exit 3); the worst violated law prints as a sentence; law 9 is reported
  "NOT SCORED".
- PV buses switched to PQ are checked for reactive balance (conformance figures move for five cases, all still pass).
- Known limitation: power-flow cross-validation against pandapower uses 1e-3 pu, and uk differs by 6.64e-4 pu at bus 8.
- Tests: the `physics-check` case9 and case14 fixtures show the new worst-law sentence (residuals byte-identical).
### Fixed
- Fixed estimate exports being scored against the wrong state.
- Fixed sidecars ignoring ZIP loads (a case14 ZIP variant now passes at 8.48e-10 pu), printing wrong case names,
  `physics_audit.py` not reading estimate exports, and `crossval_impedance.py` reporting false mismatches on a
  `--grid real` export.

## [1.95.197]
### Changed
- `crossval` default tolerance is 4096 machine epsilons (9.09e-13) on |V| (pu) and angle (rad), was 1e-3 pu. The verb
  re-solves GridSim's case from 1e-9 down to 1e-15 pu and exports the tightest that converges; the solver and every
  other verb are unchanged.
- Cross-validation now compares like for like: generators combined per bus, transformer charging as bus shunts,
  low-voltage-first transformers rewritten high-voltage first, pandapower with MATPOWER's transformer model and Q limits
  on, and a guard on the admittance matrix. Worst |V| differences: uk 6.6e-4 -> 4.4e-16 pu; case24 8.0e-2 -> 6.7e-16;
  gb-full 0.43 -> 2.6e-13; case9-case118 4.4e-16 to 1.8e-15. gb-network-2224 passes under a measured allowance (2e-12
  pu, 4e-12 rad). No GridSim defect was found.
- The harmonic export records the THD denominator (an optional field; exports without it are byte-identical), and
  `crossval_harmonics.py` uses it.
- `crossval_classical`, `crossval_smallsignal` and `crossval_freq` exit 77 on a missing package.
- Documentation: manual chapters 19 and 27 and technical chapter 11.

## [1.95.198]
### Changed
- Internal: recorded a compatibility check of the Profiles data repository.

## [1.95.199]
### Changed
- The CIM importer now throws `InvalidDataException` on malformed input: an unparsable attribute (naming element, mRID,
  attribute and text), a BaseVoltage without a positive nominal voltage, line ends with different bases, an impedance
  with no base anywhere unless `unbasedImpedancesArePerUnit: true`, ids that collide or are spelled differently from
  their object, mixed explicit and implicit end numbers, and lines with three or more terminals.
- With no `referencePriority`, the slack is the largest unit (then the lowest bus number), not the first.
- Ids lose one `#` and one `_` only (`__a` keeps one underscore).
- Tests: the CIM bridge solvability test passes `unbasedImpedancesArePerUnit: true`.
### Fixed
- ConformLoad, NonConformLoad and StationSupply are read as loads; connected equipment of unread classes is listed.
- Symmetrical, asymmetrical and tabular phase tap changers and tabular ratio tap changers are read; a missing SSH step
  falls back to normalStep (it read 0, which with neutral step 10 silently gave tap 0.875); changers on both ends of a
  two-winding transformer combine.
- `Terminal.connected = false` and `inService` / `normallyInService = false` are honoured.
- Generators keep Q, the voltage set-point, Q and P limits, ratedS as MBase and H; a bus whose machines all have
  voltage control off becomes PQ.
- Unnumbered transformer ends take document order; the bus-branch diagnostic counts only mapped ConnectivityNodes.

## [1.95.200]
### Added
- `bench`, `nminus1-sweep`, `stress` and `chain` write `<report>.run-metadata.json` beside the report.
- `bench --gpu=<vendor>` probes only that vendor (`--gpu=cpu` adds no GPU engine); one untimed warm-up solve runs per
  cell.
- The estimated solve export writes `maxStateUpdate`, the estimator status and `solver: "state-estimate (Wls)"`, and no
  `maxMismatch`. Exports gain `solve.status` and `converterAdmittance`.
### Changed
- Every CLI write goes through one rule: a missing folder is created, an existing output replaced, and a write refused
  (exit 2) when it would overwrite a run input (a case file, a file in a case folder, a bundled data file) or the path
  is a folder. The interposer, `convert` and `synth` keep their own documented rules.
- `screen` on a case with no inertia data exits 1 with no CSV (was about -5810 Hz/s and exit 3).
- The solve export carries every bus, generator and branch field (out-of-service generators `inService: false`);
  re-import gives the same network and re-solves to the same bits.
- Readers refuse missing residual locations, a file without a `schema` member and a series without `steps`.
- `gb-events.json` is regenerated from the built-ins and includes gb-2023-12-22.
- Documentation: chapter 19, technical chapter 10, manual chapters 11, 28 and 31.
### Deprecated
- `converterImpedance` in exports; use `converterAdmittance` (same values).
### Fixed
- Fixed the sweep recording busSpec.Qd in the wrong unit (now Mvar, as Newton does).
- Fixed ElapsedMs reported as 0 in five places, and the CPU name on Windows and macOS.
- Fixed `crossval_estimation.py` after the 1.95.197 cross-validation changes.

## [1.95.201]
### Changed
- Tests: the harmonic-export source check now expects the path wrapped in `CliFiles.Output`.

## [1.95.202]
### Added
- Core `DesktopLaunchArgs`: desktop launch options are parsed strictly; unknown options, missing values and values
  matching nothing are refused in a message box with exit 2.
- Amber STATIC DEMO tags on every fixed-value desktop panel and dial.
### Changed
- `cycle --jobs bogus` or an empty list exits 2 listing the valid jobs (was exit 0).
- Desktop, web and the cycle runner share `LiveSim.AuthoredBundleConfig`, so a protections-only case runs its
  protections.
- SCADA priorities come from one Core constant set (a frequency excursion is P2 on both consoles; the web logged P1).
- The RP1 trace samples once per settlement period; labels read "N-1 shards covered since load" and "last 60 frames".
- Bridge query replies are paired by id only.
- Documentation: manual chapters 8, 10, 19 and 24; technical chapters 1, 9 and 15.
### Fixed
- Fixed jumps to ASSETS selecting the wrong tab.
- Fixed a flag's value being taken as the model (`--back 30` booted case30).
- Fixed culture-dependent formatting in desktop code and XAML.
- Fixed bridge PAUSE not freezing the map, overlays, HUD, OPERATOR and MARKET.
- Fixed `MarketVm` not resetting on a model or feed change.
- Fixed circuit pages opening the wrong branch (now by branch index).
- Fixed `tools/AssetSmoke` and added it to the solution.

## [1.95.203]
### Changed
- Changed results: synchrophasor frame time is computed exactly in whole 100 ns ticks, rounded down; some frame times
  move by exactly one tick (FRACSEC 820,000 at TIME_BASE 1e6 now reads .82 s). Phasors, frequencies, STAT and quality
  verdicts are unchanged.
- TIME_BASE 0 is refused by name; a DATA frame whose FRACSEC is not below TIME_BASE is malformed (exit 3).
- The PMU mapper checks every channel against what its PMU reported; `pmu-ingest` checks the map against every
  configuration in the capture and names a CONFIG frame that does not fit (exit 1).
- `LinearPmuStateEstimator` refuses an unknown bus, a non-finite or non-positive sigma, a bad or non-positive-definite
  covariance and non-finite values, by name.
### Fixed
- Fixed a mid-stream configuration change in `pmu-ingest` crashing with IndexOutOfRangeException.
- Fixed the linear PMU estimator silently treating a bad covariance as independent.

## [1.95.204]
### Changed
- Changed results: the DC-OPF simplex enters the lowest-index column when two reduced costs agree within 1e-10
  relative, so near-tied LPs give stable dispatches. Cases with no near-tie (12 of 16) are bit-identical. pglib case588
  and gb-synthetic reach another optimal dispatch at the same cost; `acopf gb-synthetic` 684,072 -> 675,277 GBP/h,
  losses 1,385.085 -> 1,235.507 MW.
- `nminus1-sweep` uses per-bus voltage limits by default, so its counts equal `nminus1`'s (case9.m secure / violating
  4/2 -> 5/1, case30.m 26/13 -> 23/16, case588 296/188 -> 304/179); `--global-vlimits` gives the old rows.
- Changed exit codes: `taps` refuses an OLTC naming no branch (exit 1) and exits 3 when its search stops at the cap;
  `ptdf` with an unknown bus exits 2; `cpf` exits 3 and says "NOT bracketed" when `--max-steps` runs out.
- `--cpu` / `--gpu` may come before the verb.
- `chain --prices` runs the lp and price policies; `--policy price` without prices exits 2; storage flags without
  `--bess` exit 2; a bad price file exits 1. `gda-replay` accepts `--shave-high` / `--shave-low` and refuses
  `--policy price` / `lp`. `--bess` is checked field by field.
- `cpf` is no longer labelled a continuation power flow; non-converged storage steps print "-"; `--costs` result keys
  no longer say GBP; unnamed branches print as "from-to".
- Tests: four DC-OPF parity rows, two `acopf` tests, five message texts and one cigre fixture line ("branch 0-1 104%")
  updated.

## [1.95.205]
### Added
- `dist3ph` estimation prints per-measurement residuals; new `--robust-passes` / `--robust-floor` (wlav only, defaults
  8 and 0.001).
- `serve` ends each window line with its largest sigma and prints the last state bus by bus with sigmas.
### Changed
- The EnKF draws process noise from the full Q. With a diagonal Q (the default) results are bit-identical; with a
  correlated Q they change.
- Filter steps are all-or-nothing, so a throw leaves the state unchanged; the EnKF refuses an ensemble of fewer than 2;
  UKF and EnKF constructors check the state size.
- The three-phase WLS refuses feeders over 416 non-source buses (exit 1); robust three-phase WLS warns at the IRLS cap
  (exit 0, as `estimate`).
- Branch-current estimation: a partly measured branch exits 3 naming the missing phases (was exit 0 with zero
  current); a reversed-orientation current is accepted; a reversed current across a regulator or charged line, or a
  current on no branch, is refused by name.
- `serve --max-windows 0` or negative exits 2.
### Fixed
- Fixed fault types other than SLG returning Infinity / NaN on a zero loop impedance (now refused).
- Fixed a missing-file absolute path being printed twice, switch-only feeders not printing the element line, and stale
  text in two `data/dist3ph` descriptions and the harmonics refusal.

## [1.95.206]
### Changed
- Tests: the `dist3ph-estimate` stdout fixtures now expect the per-measurement residual table after the voltages.

## [1.95.207]
### Changed
- Changed results: the impedance operating point uses the unit's solved output (`SolvedGeneration.UnitOutput`), not
  scheduled Pg / Qg. Every Nyquist verdict is unchanged; margins move (gb-synthetic `--grid real` GM / PM inf / 37.0 deg
  -> 7.3 dB / 4.0 deg, still STABLE).
- One machine-base rule (`MachineBase.EffectiveMva`): positive MBase as is, zero or negative uses the system base,
  NaN or infinite refused. `SubtransientMachine` refuses Xd / Xq not finite, not above 0, or below the transient
  reactance.
- Changed exit codes: a negative, NaN or infinite `--clear` exits 2 (`--clear -50` printed STABLE, exit 0); `--export`
  with `--rms` or `--screen` exits 2; `--current-bw` <= 0 exits 2.
- The screen counts machines and held units apart (data/uk 13 machines + 16 held; gb-synthetic 13 + 61).
- The case9 cross-check uses the Anderson-Fouad X'd and states its basis.
### Fixed
- Fixed the SCR line claiming stability when every swept SCR is unstable or under `--grid real`.
- Fixed a crash with `--current-bw` on a bound asset class.

## [1.95.208]
### Changed
- Refused at load by name: a system base or frequency that is not finite and positive, a self-loop, a branch with
  R = X = 0, and an island with no slack. The bridge reader refuses an unparseable bus type and unusable per-bus limits.
- Loss allocation, power-flow tracing, carbon tracing and boundary analysis refuse a non-converged state; the CLI
  dashboard, case report, web and desktop views check convergence first.
- A future-dated licence key gets the free tier until its issue day; the status line shows the limit that applies.
- An analysis verb on a folder that is not a case exits 1 naming what it lacks (was 2, "unknown case").
- `--enableExport` on a `.m` path writes `<stem>-solve.json`.
- The v1 asset schema no longer admits `synchronousMachine` / `machine`.
- An EMT `i:` probe on parallel branches is refused as ambiguous; `#k` picks one.
- A GDA series step without `timeUtc` or load factor is refused; HTI passes count from 0.
- `synth` writes to `results/synth/gb-synthetic` unless `--out` says otherwise (no longer into bundled data).
- The estimate export lists every generator with its service state.
- Internal: the release workflow runs the licence-path tests on macOS.
### Fixed
- Fixed an expired licence key printing "unlimited buses".
- Fixed the asset refusal citing a file that does not exist.
- Fixed the protection cascade and inertia transforms dropping out-of-service units.
- Fixed desktop interconnector pages, fleet rows and substation rows (now by index), and the `pmu-ingest` table
  ignoring configuration changes.

## [1.95.209]
### Changed
- Changed results: every fault network is solved by the sparse engine at every size; small cases move by ulps (case9
  IEC 60909 bus 5 Ik'' 1.4683989584292976 -> 1.4683989584292985). Above 1,500 buses nothing changes. Tied 21-Z2 rows in
  `fault` and `protection-check` on case14 are reordered.
- Changed results: corrective redispatch and the storage LP scheduler use the near-tie entering rule from 1.95.204;
  where a near-tie is met some corrective plans reach another optimal dispatch at equal cost (case145 one unit 200 MW).
  Every storage schedule tested is bit-identical.
- RoCoF screening sorts with a stable tie-break (numbers unchanged; only the order of exactly equal values).
- A converged sweep says "converged on the voltage step".
- Known limitation: GPU late singular masking is unverified on hardware.
- Tests: bit-identity of Newton solves and dense estimation across four vector tiers; dense / sparse parity on more
  cases, including case9241pegase (about 2.7 GB).
### Removed
- Unused `DenseLuKernels`.

## [1.95.210]
### Added
- Global `--frequency-hz` declares the nominal frequency of a MATPOWER file.
### Changed
- Changed results: case9 (WSCC) and the Kundur two-area case are 60 Hz. CCT searches and the RMS study integrate at the
  model frequency; inverter, impedance scan, trip and dashboard use it where it applies; EMT sources follow the engine
  frequency. GB 47.5 / 52.0 Hz relays report "not checkable" on a non-50 Hz system. case9 IEC 60909 m / Ith / idc and
  some stability and EMT outputs change.
- Power flow, OPF and estimation do not read the frequency (bit-identical at 50 and 60 Hz, dense and sparse).

## [1.95.211]
### Changed
- Built-in IEEE cases other than case9 and Kundur stay at 50 Hz; use `--frequency-hz 60` to declare 60 Hz.

## [1.95.212]
### Added
- Optional `chiTest` field in the bridge estimate block ("not-run" shows "chi-square not run").
### Changed
- Protection rows with equal operating times are listed in row order (case14 `fault` and `protection-check` 21-Z2 rows
  2-4, 3-4, 4-5, 4-7, 4-9; 21-Z3 rows 1-5, 2-5, 5-6).
- The bridge refuses a present `baseMva` that is not finite and positive (absent still means 100).
- Three-phase estimators refuse unusable measurement sigmas; the UKF refuses bad alpha / beta / kappa.
- `/ws/sim` names an unknown property in its error; SSO audience and allowed-client checks compare origins
  (`https://host:443` = `https://host`).
- " | " replaces the middle dot in the licence line and web footer.
- `crossval_classical` / `crossval_smallsignal` exit 3 on an unreadable export.
- Tests: one protection tie-order test updated.
### Fixed
- Fixed an active v2 `synchronousMachine` template failing the whole case; binding an element to one is refused by name.

## [1.95.213]
### Changed
- `data/gb-full` ships with 1.0.
- The web key-ring protection setting is meant to be left blank; the default `auto` applies.
- Known limitation: there is no Linux CI run.

## [1.95.214]
### Added
- Release signing with a self-signed code-signing certificate (CN=GridSim, RSA 3072, SHA-256, valid to 2029-10-05).
  Its public part and how to check a binary against it are in `signing/`; the release workflow reads the private key
  from the `SIGNING_CERT_PFX_BASE64` / `SIGNING_CERT_PASSWORD` repository secrets. SmartScreen still warns.

## [1.95.215]
### Changed
- Breaking API changes: `TsgbSizing` gained a parameter, `ActiveRocofLimitHzPerS` lost its setter (the RoCoF limit can
  no longer be changed process-wide), and the RMS methods lost `sparseNetwork`.
- The H0 export adds `responsiveCap` per corner.
- The RMS subtransient initialisation runs on the sparse network at every size.
- Governor lags refuse a negative or NaN time constant; one shorter than the step is deadbeat. The sensitivity sweep
  refuses a range outside (0, 1); `FleetDynamics` refuses a grid-forming config; `TsgbSizing.Feasible` labels a size that
  cannot hold the nadir target NOT ACHIEVED.
- Internal: named and shared constants in the swing, sizer, fitter and wave models (bit-identical).
- Tests: removed the two tests that exercised the removed per-domain sampling.
### Removed
- `AssetFleetStep.PowerByDomain` and the unused per-domain sampling.
### Fixed
- Fixed a governor time constant of 0 giving Infinity / NaN.

## [1.95.216]
### Added
- CGMES topology processor: closed switches of every class join their ConnectivityNodes into one bus (open,
  out-of-service or disconnected switches separate; SSH `open` beats EQ `normalOpen`; earthing switches never join).
  Bus naming and order are deterministic. Retained closed switches keep two buses and solve as one. Where
  TopologicalNodes are present they stay the bus set and disagreements are listed; an option rebuilds from switches.
- SvTapStep is read (SSH wins by default, differences listed, option to use SV); generator type comes from the
  generating-unit class (wind and solar count as converter plant in fault studies). A multi-file `Parse` overload.
- Opt-in test against the ENTSO-E conformity models via `GRIDSIM_CGMES_CONFORMITY_DIR` (MiniGrid 13 buses, SmallGrid 118,
  as published).
### Fixed
- Fixed a closed switch across two TopologicalNodes being dropped, and base voltage not found through a Bay.
- Contradictory PhaseTapChangerLinear xMin / xMax are refused.

## [1.95.217]
### Changed
- One governor, device and response-service implementation serves all seven frequency engines. Live integrators now
  honour recorded response services and the starting frequency; negative or non-finite demand is refused everywhere;
  the live fleet engine refuses grid-forming.
- The distributed model places a loss at its bus's machine group, else by electrical closeness, else on the group with
  the largest unit; `trip --inertia-model distributed` prints `Loss placed:`. A `Coupling:` line shows when the 0.5 Hz
  fallback is used.
- Over-frequency breaches are flagged; fleet units re-arm their onset delay after each excursion.
- Changed results: the seam, distributed model, asset solve and `RocofScreening.Screen(model)` run at the model frequency
  and refuse a config at another, so `screen case9` uses 60 Hz like `trip case9`. The wave model skips one zero mode
  per island.
- Tests: seam and distributed-reduction inputs moved to 60 Hz; the `modal` message check updated.
### Fixed
- Fixed an inertia value making a battery or wind farm rotating mass, and buses without coordinates sitting at (0, 0).

## [1.95.218]
### Added
- CIM: shunt compensators with their in-service sections, ExternalNetworkInjection and EquivalentInjection as
  generators, AsynchronousMachine as a fixed P / Q load, and PowerElectronicsConnection as a converter-interfaced
  generator (solar / wind / battery). Unsupported classes and contradictory limits are refused by name.
- An island with neither the slack nor a voltage-controlling machine is left out with a note naming its buses.
  `CimGridModelBridge.Bridge` returns `SwitchFlows`.
### Fixed
- Fixed transformer rated voltages that differ from bus base voltages being ignored (MiniGrid solved 4.8 % off the
  published voltages), and `PowerTransformerEnd.b` not being read.
- MiniGrid now matches the published SvVoltage to 7e-6 pu / 9.4e-5 deg and SmallGrid to 1.45e-4 pu / 7.2e-3 deg.

## [1.95.219]
### Changed
- Frequency bands are Hz offsets around the model's own nominal; a new `FrequencyBands` feeds assessment flags, lamps,
  SCADA alarms, sparklines, dials and gauges in web and desktop. 50 Hz output is byte-identical; case9 at 60 Hz is graded
  against 59.8 / 60.2, 59.5 / 60.5, 58.8 and 59.9 / 60.1 Hz.
- LiveSim refuses a config nominal that differs from the model's; `transient` passes the model nominal.
- Changed results: an inverter re-arms its compute latency after each excursion, so live gb-full / gb-synthetic views
  move.
- Tests: LiveSim and transient case9 frequency bounds now expect 60 Hz values.
### Fixed
- Fixed live integrators and LiveSim accepting a NaN, infinite or non-positive step or a non-finite event power.
- Fixed live trips removing the full base output instead of the load-scaled dispatch (1.67x too much at load factor
  0.6).
- Fixed fleet trips and LFDD sheds not restoring over 300 s like manual trips.
- Fixed an inverter battery's ageing being lost on a reset.

## [1.95.220]
### Changed
- Performance: a contingency screen no longer solves an outage that leaves a part with no reference bus that provably
  cannot converge; it returns the same non-converged result at once. Results are bit-identical.
- JSON v2 generator `subtransientXdPu` / `negativeSeqXPu` and transformer `windings`, `r0Pu` / `x0Pu`, `neutralXPu`
  reach the model; earth faults on a v2 case with windings exit 0 (still 3 without). Invalid combinations are refused
  by name.
- The protection cascade takes equal-time trips in row order.
- `crossval_freq.py` exits 3 on an unreadable export.
- Tests: the v2 sequence-data refusal and transformer zero-sequence tests now expect the data to load.

## [1.95.221]
### Changed
- Changed results: the credible loss is the largest single infeed of any technology (SQSS 5.1.2); grid infeeds and idle
  or absorbing units never count.
- Changed results: the H0 study's analytic RoCoF uses only losses in the first 5 s (gb-2019-08-09: 1,521 MW instead of
  2,768 MW). `h0-study.json` keeps `largestLossMw` and adds correctly named fields. The achieved grid-forming fraction
  is reported.
- Both inverter kinds fall back to a 0.5 s RoCoF confirm time (grid-forming was 1.0 s); a class with a threshold but no
  delay is refused.
- FREQUENCY EVENT lamps fire only above 1 Hz/s in web and desktop.
- `screen` grades against bands about the case nominal; `screen case9` verdicts and exit code may change. LFDD blocks
  shed their share of demand actually running; protection frequency settings move with the nominal (new class key
  `settingsNominalHz`, default 50).
- Tests: credible-loss, lamp, fuel-mapping, code-default count and confirm-time expectations updated.
### Fixed
- Fixed NESO "Bioenergy" not reading as biomass and NGED "(not CHP)" reading as CHP.
- Fixed the web TRIP / DEVICE tab using an unsourced 0.5 Hz/s threshold.

## [1.95.222]
### Added
- CIM: SeriesCompensator and StaticVarCompensator are read; machine capability curves replace minQ / maxQ; remote
  voltage control is kept; a CGMES 3.0 boundary node without a TopologicalNode becomes its own bus.
- Opt-in `CimControlledPowerFlow` outer loop regulates remote machines and SVCs and reports `Converged` false if it
  cannot settle.
### Changed
- A line whose ends have different base voltages is per-unitised on each end's base with the ratio as an off-nominal
  tap; bases more than 1.1x apart are still refused.
- Regulating linear shunts are solved in whole sections or continuously (from the data's `discrete` flag); an energised
  second island gets its own slack.
- Tests: an island with its own voltage control now validates and solves.
### Fixed
- Fixed `ACLineSegment.gch` being ignored.

## [1.95.223]
### Added
- Governors in the RMS study (bit-identical without one); full IEEEG1 with valve rate limits and LP-shaft fractions
  (bit-identical at defaults).
- `nminus1 --outage-dispatch reference|droop|inertia` (default unchanged).
- Machine dynamic data file `gridsim-dynamics/2` (`schemas/v2/dynamics.schema.json`, `stability --rms --dyn-data`).
### Changed
- Governors and exciters refuse a start outside their limits; set-point steps go through the lags.
- Changed results: GENROU saturation acts on |psi''| on both axes with Xl.
- Converter H is excluded from SystemInertiaGvaS.
- Tests: SubtransientMachine saturation now expects Efd_sat 1.99248490643736 (was 1.97712064413393).

## [1.95.224]
### Changed
- Internal: tracker update; no code change.

## [1.95.225]
### Changed
- Web and desktop RP1 pages show a one-line reason on a non-50 Hz case.
- A UF / OF / LFDD setting more than 5 Hz from its `settingsNominalHz` is refused.
- Web and desktop text is plain ASCII.
- The CIM exporter writes regulating shunt sections and control; shared remote voltage control splits Q by reactive
  range; transformer magnetising g is represented half at each end.
- Tests: the opt-in MicroGrid BE benchmark worst error is about 1.80e-4 pu (limit 2.5e-4).
### Fixed
- Fixed desktop TripCompare tripping a grid infeed, and the desktop dial showing red at exactly 49.8 / 50.2 Hz.
- Fixed `crossval_h0.py` capping where `responsiveCap` does not say so.
- Fixed SwitchFlows ignoring regulated values from a `CimControlledPowerFlow` result.

## [1.95.226]
### Changed
- Performance: new `SparseKronReduction`; distributed coupling and impact shares no longer build a 200 MB dense matrix
  on gb-full; the machine-augmented network is built sparsely (up to about 1.46 GB per call on gb-full saved);
  `ClassicalInit.FaultScreen` builds the fault-independent part once; the web what-if skips recomputing on identical
  inputs; the protection cascade and `--losses` sweep skip unsolvable islands. Results are bit-identical.

## [1.95.227]
### Added
- Fleet engines run grid-forming: `vsm` (a second-order virtual synchronous machine) and `droop` (a P-f droop former).
  `fleet` gains `--forming`, `--forming-inertia` and `--forming-damping`.
### Changed
- Changed results: the grid-forming power loop is per unit on the unit rating; models without damping or synthetic
  inertia are unchanged.
- Changed results: one PLL default, 20 Hz at 0.707, for EMT, impedance and small-signal; every default
  `impedance-stability` run moves.
- Changed results: the wave model analyses the symmetric part of the coupling and reports the asymmetric part; mean
  stiffness is in MW/rad (case9 dominant mode 0.8048 -> 0.9013 Hz). Symmetric inputs are bit-identical.
- Code-default register gains entries for the dynamics kind keys and PLL damping.
- Tests: code-default total 595; fleet grid-forming tests expect the forming result; the case9 wave fixture updated.

## [1.95.228]
### Added
- `fit-frequency <trace.csv> --inertia --demand --loss` fits load damping (optionally responsive fraction and governor
  lag) to a measured `seconds,Hz` trace; exit 3 no convergence, 1 bad trace, 2 usage.
- `trip --band[=<fraction>]` gives a nadir band over the unfitted swing constants. `trip --asset-dynamics --envelope
  <class>:<param>=<low>..<high>[,...]` gives nadir / RoCoF bands, cascade possibility and the most sensitive parameter.
- `--dyn-data` on `trip`, `transient` (H) and `modal` (H, X'd, damping).
### Changed
- `HybridMultiRateScheduler` is internal.

## [1.95.229]
### Changed
- A droop former with non-zero virtual inertia is refused; `--forming droop --forming-inertia` exits 2.
- Changed results: each EnKF window draws from its own seeded stream; window 0 is unchanged, later windows move. The
  diagonal sampler refuses a negative, NaN or infinite variance.
- `trip gb-synthetic --asset-dynamics` loads `data/gb-synthetic` with its asset bundle (exit 0, was 1).
- Replay and evidence summaries state when the simulated nadir time is more than 2 s off the record (2019-08-09 nadir
  75.9 s); verdicts unchanged.
- Web and desktop text is plain ASCII; dynamics-file keys are marked not gated.
- Tests: droop RoCoF and droop virtual-inertia expectations updated.

## [1.95.230]
### Changed
- The `gb-2019-08-09` event now replays the recorded response: six services at their delivered MW (1,090 MW in all,
  ESO 16 August 2019 report, Table 3), a demand effect of 700 MW/Hz (new `RecordedDemandEffect`), the generic governor
  off (`ResponseServicesComplete`), and the RoCoF-protection loss at 1.0 s, the same second as Little Barford.
- Results change for the 2019 event: peak RoCoF -0.1608 -> -0.1710 Hz/s (record -0.17); nadir 48.820 Hz at 14.06 s ->
  48.841 Hz at 76.0 s (record 48.8 Hz at 75.9 s); the verdict is Reproduced without the timing note; RMSE against the
  bundled trace about 0.153 -> 0.099 Hz (still Marginal). Every 2019 counterfactual, risk-curve and avoided-cost figure
  moves (for example no device and no LFDD: 48.48 -> 47.65 Hz).
- Frequency settings are bounded: UF and LFDD settings must be strictly below the settings nominal and OF strictly
  above, each within 10 Hz (`AssetClassSupport.MaxFrequencySettingDistanceHz`). Quebec's +6.0 Hz OF stage now loads;
  60 Hz settings on a 50 Hz system are still refused.
- Tests: the asset-class edge rows now expect `ufTripHz` 44.99 and `ofTripHz` 55.01 to be accepted; added tests for the
  2019 replay and the setting bounds.

## [1.95.231]
### Added
- `fit-frequency --nominal-hz`; without it the nominal is taken from the trace's first sample. 50 Hz fits are
  bit-identical and 60 Hz traces now fit.
- Classical `stability` and `stability --screen` take `--dyn-data` (H, X'd, damping); without damping the CCT search is
  bit-identical.
- New `GridModel.SystemInertiaMwS` / `RotatingInertiaMwS`.
### Changed
- `convert <in> <out.json>` now writes a v2 case (it wrote a v1 folder), through the new `V2CaseWriter`, byte-stable on
  every OS and carrying all sequence data.
- A network counts as bundled only when it is in place and its content matches `data/bundled.sha256` (SHA-256 of 120
  shipped files, line endings normalised; generated by `tools/gen_bundled_manifest.py`). The web catalogue no longer
  treats every MATPOWER entry as bundled.
- `MultiDeviceFrequencyIntegrator` is now internal.
### Fixed
- `trip` uses the rotating-mass inertia total; no bundled result changes.
- `stability --screen` refuses `--fault-bus` and `--clear` (exit 2); `--clear` is capped at 1000 ms, which covers GB's
  slowest back-up clearance of 800 ms (Grid Code CC.6.2.2.2.2); `--pll-bw` and `--pll-damping` <= 0 are refused;
  `TwoAxisMachine` validates Xd and Xq.

## [1.95.232]
### Changed
- No GridSim change. Companion GDA repository: the bridge sends `chiTest` (`passed`, `failed`, `not-run`), so the
  "chi-square not run" display now works; the event builder writes the recorded 2019 response and nadir time. The
  builder's default output path is `GRIDSIM_REPO`, else the sibling GridSim checkout, else `--out`.

## [1.95.233]
### Changed
- No GridSim change. Companion GDA repository: GDA-SOLVES diagnostics carry `chi_test` from GridSim's `chiSquareTest`
  (0 NotRun, 1 Passed, 2 Failed), forwarded by the bridge as `chiTest`; older diagnostics are unchanged. The solve
  campaign and heat-window scripts find GridSim through `GRIDSIM_REPO`, else the sibling checkout.

## [1.95.234]
### Changed
- No GridSim change. Companion GDA repository: the bridge and the solve campaign find GDA-SOLVES through
  `GDA_SOLVES_ROOT`, else beside the repository (the old default looked inside the checkout). Deployments of
  `gridsim-bridge.service` must set `GDA_SOLVES_ROOT` (the repository copy sets `/gda/GDA-SOLVES`).

## [1.95.235]
### Changed
- `gb-synthetic` and `synthetic` now load `data/gb-synthetic` for every verb, the same as `--dir data/gb-synthetic`; the
  in-memory builder is used only by `synth`. Results by name change: the dispatch is balanced on load (pf, dcpf and
  contingency numbers move), the branch order differs, and the asset and protection bundle is present (`trip` and
  transient `--asset-dynamics` run; impedance-stability includes the bound inverters). The name now goes through the
  licence content check (2,227 buses).
- `screen` uses the rotating-mass inertia total (`SystemInertiaMwS`); bit-identical on bundled cases.
- The v2 writer writes asset classes as case-local templates, binding rules, element classes and placed protective
  devices (all or nothing, with a note when a bundle cannot be written exactly).
- Tests: `acopf gb-synthetic` now expects the output of the `data/gb-synthetic` folder; the v2 round-trip test compares
  the whole model including bundles.
### Fixed
- `OneAxisMachine` refuses an Xd that is not finite, not above 0 or below X'd.

## [1.95.236]
### Changed
- CLI output is plain ASCII ("GBP", "T deg C", "Delta"); a test checks that every string literal in Cli, Core, Gda and
  Gpu is ASCII.
- v2 messages and schema descriptions no longer refer to internal plan phases.
- Tests: three v2 message checks expect the new wording; one kind test is renamed and also covers `site`.
- Internal: comment and documentation corrections. No result changes.
### Fixed
- Web and WPF inertia read-outs count rotating mass only (`SystemInertiaMwS` / `RotatingInertiaMwS`); bit-identical on
  every bundled case.

## [1.95.237]
### Changed
- Internal: planning notes only. No code change.

## [1.95.238]
### Changed
- The case editor's v2 save gate runs the v2 loader over a staged copy of a case manifest that imports nothing, once
  the schema, semantic and licence checks pass (the kind specs are now embedded as `GridSim.Core.schemas.v2kinds.*`).
  It reports "the v2 loader reads the case" or "WARNING: the v2 loader refuses the case, so GridSim will not run it as
  saved: <loader message>". The save outcome is unchanged. A case that imports library templates, and a lone network
  file, keep the "not run on save" note.
- Internal: the FDIA screen writer reuses the CLI's cached JSON options; output is byte-identical.

## [1.95.239]
### Fixed
- Documentation: `docs/technical/design/c37118-synchrophasor.md` section 6.2 said a float FREQ is a deviation from
  nominal. IEEE C37.118.2-2011 Table 5 makes the float field the actual frequency in Hz; only the 16-bit integer field
  is a millihertz deviation from FNOM. The decoder already read it as absolute Hz.

## [1.95.240]
### Changed
- One field edit in the case editor on gb-full takes about 77 ms, down from 195-274 ms. `SetField` builds the reference
  index only when the field is a key or a reference, and the transaction reuses the text `SetField` already parsed (new
  `CaseTransaction.Patch(doc, before, applied)`, refused when `before` is no longer current). Study results are
  unchanged.

## [1.95.241]
### Changed
- Internal: record keeping only. No code change.

## [1.95.242]
### Changed
- Internal: record keeping only. No code change.

## [1.95.243]
### Changed
- Results change for `harmonics --converter-sources dispatched`, for example the uk worst THD moves from 2.4290 % to
  2.4583 %. Rated studies and the default synthetic study are unchanged.
- Tests: the web and Core dispatched-harmonics comparison passes the same solved operating point to both.
- Known limitation: the twelve-pulse mapping for line-commutated links is not yet confirmed against the source.
### Fixed
- `harmonics --converter-sources dispatched` sized each converter's harmonic source from the scheduled Pg and Qg, not
  the solved operating point. `ConverterHarmonicSources.FromModel` takes an optional solved `operatingPoint` and reads
  each unit with `SolvedGeneration.UnitOutput` (null keeps the schedule bit for bit; a non-converged result throws). The
  CLI solves the fundamental first (non-convergence exits 3) and the web HARMONICS tab passes its tick's solve.

## [1.95.244]
### Changed
- Internal: planning notes only. No code change.

## [1.95.245]
### Added
- `gridsim pmu-estimate <case> --measurements <set.json>`: the linear PMU state estimate. It reads a
  gridsim-measurements/1 set (as `pmu-ingest --measurements-dir` writes), pairs each bus's VmPu and VaDeg rows and each
  from-end branch current's IMagPu and IAngDeg rows into phasors, and solves in one non-iterative WLS step. Unusable rows
  are listed with the reason; duplicate rows, a to-end current and a zero-magnitude phasor are refused (exit 1); a set
  that does not observe every bus exits 3 (NOT OBSERVABLE); a set newer than the horizon is REJECTED (exit 2). Output
  has `buses` and `not_used`. Manual ch. 19.
- `PmuMeasurementSetAdapter`: polar sigmas map to the rectangular error covariance by the first-order transform at the
  measured value.
- `PmuVoltageMeasurement` and `PmuCurrentMeasurement` take an optional `SigmaIm`; null is bit-identical to before. The
  estimator's branch lookup accepts the ordinal circuit form `#k`.
- Tests: `pmu-estimate` tests, including a `pmu-ingest` -> `pmu-estimate` chain and exact recovery of the case9 state.

## [1.95.246]
### Fixed
- `LinearPmuStateEstimator`'s documentation marks it Experimental again (one solve, no bad-data test). Comment only.

## [1.95.247]
### Changed
- Internal: projection statistics were tried as Schweppe-GM leverage and not adopted. `ProjectionStatistics.Compute`
  is the dense form, and on the sparse Jacobian it down-weights nearly every row (clean case14: J 86.33 PASS ->
  365.50 FAIL). Recorded in the class documentation. No code change.

## [1.95.248]
### Changed
- Internal: the projection-statistics trial was kept aside for later work. No code change.

## [1.95.249]
### Changed
- Huber and Schweppe-GM have their own IRLS cap, `IrlsMaxOuterIterationsMEstimator = 50`; LAV keeps
  `IrlsMaxOuterIterations = 10`. Every robust estimate that settled within 10 passes is bit-identical; an estimate that
  hit the old cap moves to its settled value (case14 seed 3 Schweppe-GM: J 86.33 -> 86.34, settles at pass 13). Manual
  ch. 29.
- Known limitation: LAV's IRLS does not settle; a LAV result is whatever the tenth pass gives, and it is flagged.
### Fixed
- Huber and Schweppe-GM stopped on the IRLS cap before their weights settled, on clean data, and printed the cap
  WARNING.

## [1.95.250]
### Changed
- Internal: a trial of LAV estimation by linear programming was kept aside for later work. No code change.

## [1.95.251]
### Added
- `dist3ph` feeders take `"connection": "d-gy"`: a delta / grounded-wye step-down transformer at rated ratio (delta on
  the higher-voltage From side; the wye side lags by 30 degrees; no zero-sequence current on the delta side), from
  Kersting's generalised matrices in per unit (lossless).
- Validated against the IEEE 4 Node Test Feeder's published D-GrY step-down results, balanced and unbalanced: 35 of 36
  values within half the last printed digit. The exception, unbalanced node 3 phase b (2261.597 V against a printed
  2261 V, 0.026 %), has a cause not yet confirmed against the source.
- New bundled feeders `data/dist3ph/ieee4-dGy-stepdown-{balanced,unbalanced}.json`; `data/bundled.sha256` regenerated.
- A branch without the flag is unchanged (`PhaseBranch.DeltaWyeStepDown` defaults to false). The WLS estimator,
  harmonic penetration, the branch-current estimator and the sequence fault study refuse the new element; the loader
  refuses a step-up `d-gy`. `dist3ph` prints the transformer count when there is one. Manual ch. 7, technical ch. 18.
- Tests: the loader's unmodelled-connection case now uses `"d-d"`.

## [1.95.252]
### Added
- Delta / grounded-wye step-up transformers in the three-phase sweep: a `d-gy` whose delta (From) side is the lower
  voltage is a step-up (the wye side leads by 30 degrees), `PhaseBranch.DeltaWyeStepUp`. A `d-gy` with equal rated
  voltages is refused. Every other three-phase solver refuses either kind by name (`PhaseBranch.IsDeltaWye`).
- Validated against the IEEE 4 Node Test Feeder's published D-GrY step-up results (12.47 / 24.9 kV): all 36 values
  within half the last printed digit. New bundled feeders `data/dist3ph/ieee4-dGy-stepup-{balanced,unbalanced}.json`;
  `data/bundled.sha256` regenerated. Manual ch. 2 and 7, technical ch. 18.
- Tests: the refusal case now expects a `d-gy` with equal rated voltages to be refused, with the wording "(delta /
  grounded wye".

## [1.95.253]
### Added
- Ungrounded-wye / delta (`y-d`) and delta / delta (`d-d`) transformers in the three-phase sweep, with equal winding
  impedances at rated ratio, step-down or step-up. A Y-D is the D-GrY element of the same direction; a D-D passes the
  positive and negative sequences 1:1 and blocks the zero sequence. The D-GrY element is unchanged.
- Three-wire rule: a delta secondary has no neutral, so the sweep refuses a wye load, a capacitor, line charging or a
  regulator fed from it (until a `d-gy` grounds it again), and the loader refuses a `gy-gy` transformer there. Other
  three-phase solvers refuse the new elements by name (`MultiPhaseElement.DeltaSecondaryTransformer`).
- Validated against the IEEE 4 Node Test Feeder's published Y-D and D-D results (eight cases): 62 of 72 values within
  half the last printed digit; ten residuals are tested at their measured deviations. Four are one pattern not yet
  confirmed against the source: the Y-D node 2 phase a line-to-neutral magnitude on the ungrounded-wye side is 1.26 to
  1.86 V low (0.018 to 0.026 %) in every Y-D case.
- Eight new bundled feeders `data/dist3ph/ieee4-{yD,dD}-{stepdown,stepup}-{balanced,unbalanced}.json`;
  `data/bundled.sha256` regenerated. Manual ch. 2 and 7, technical ch. 18.
- Tests: the unmodelled-connection cases now use `"open-delta"`.

## [1.95.254]
### Added
- Off-nominal transformer ratios in three-phase feeders: a transformer whose rated kV differ from its buses' kV bases
  (within 20 %) uses the per-unit ideal ratio t = (base_from / rated_from)(rated_to / base_to), with its impedance moved
  to the To base. A rated transformer is unchanged. A rated kV more than 20 % from its bus base is refused as a data
  error. Proved by per-unit invariance to 1e-9 relative for `gy-gy`, `d-gy`, `y-d` and `d-d`. Manual ch. 7, technical
  ch. 18.
- Tests: the loader's off-nominal refusal case now checks that 41.6 kV on a 4.16 kV bus is refused.
### Changed
- Known limitation: open-wye / open-delta transformers, unequal winding impedances, IEC 104 ASDU types 5 / 32 and
  7 / 33, the PSS1A transducer and torsional filter, a v2 form of the dist3ph format and a live C37.118 PMU transport
  are not yet supported.

## [1.95.255]
### Fixed
- Internal: fixed an analyser error (CA1869) in the `estimate --fdia` path; `fdia-screen.json` is byte-identical.

## [1.95.256]
### Fixed
- `trip` builds the shared post-trip copy once, used by both the asset-fleet run and the `--envelope` sweep;
  bit-identical.
- The classical stability study and its export path use `Wscc9XdpByGenerator` instead of repeating the WSCC-9 X'd
  loop.
- Internal: comment corrections.

## [1.95.257]
### Changed
- Internal: planning notes only. No code change.

## [1.95.258]
### Changed
- Internal: two development lines combined; the entries now numbered 1.95.255-1.95.257 were renumbered. No behaviour
  change.

## [1.95.259]
### Changed
- Internal: validation run on Windows. No code change.

## [1.95.260]
### Changed
- Tests: the `iec60909` case9 fixtures now expect 60 Hz figures (max: Ith 1.4881526648788939 kA, m
  0.027086065637215752, idc(tb) 0.03963009392407432 kA; ll min: Ith 1.17161637499512 kA, idc(tb) 0.031200607356919714
  kA); the modal COI residual 2.593E-008 rad/s; passivity Re(Z) >= 2.19E-005 pu. The same bits on Linux and Windows.
- Tests: IEC 60909 single-fed sums at buses 5, 6 and 9 differ in the last digit; `stability case9 --screen` expects
  "312 NON-MONOTONE: holds again at 320 ms"; the AssetParams constant count is 59; source checks include the frequency
  argument.
- Tests: the 2019 fleet RoCoF is (-0.139 vs -0.145) Hz/s with 1411 MW online; `case118.m` is at `data/matpower/lib/`.
- Tests: the restoration run lasts 15 time constants; the frequency-rise case uses +500 MW; the CIM round trip has 7
  generators and repeated nonlinear-shunt sections get their own rdf:ID; switch Q from side A is -54.7998329398381
  MVAr; the DetMath-based Q check is exactly 210.67221853205206 on every OS.
- Tests: kind files merge repeated references into one entry naming every consumer (code-default total 591); no value
  changed.

## [1.95.261]
### Added
- The classical CCT search takes an optional `postClearWindowSeconds` (default 5 s, bit-identical when omitted; zero,
  negative or non-finite refused). A 60 Hz search equals a 50 Hz search with every time scaled by sqrt(1.2), exactly.
### Changed
- Known limitation: the classical CCT depends on the post-clear window (case9 bus 7 at 60 Hz: 275 steps at 5 s, 218
  at 20 s).
- Tests: the 60 Hz CCT test expects exact equality with the time-scaled 50 Hz search (275 / 276, pocket 279); new exact
  tests for case9 at 60 Hz (classical and RMS CCT, modal and EMT digests) and for `acopf gb-synthetic`, which matches
  `acopf data/gb-synthetic` byte for byte (6 passes, losses 1235.507 MW, 675,277 GBP/h). Manual ch. 19.
### Fixed
- CIM: two rdf:ID definitions of one mRID in one document are refused, naming the id, document and both elements;
  across documents they merge as before, with a note. The EQ / TP / SSH rdf:about merge is unchanged.

## [1.95.262]
### Fixed
- Determinism: with `DOTNET_EnableHWIntrinsic=0`, a diverged swing run could produce a NaN whose sign bit depended on
  the JIT tier. New `DetMath.CanonicalNaN` maps any NaN to the one quiet NaN 0x7FF8000000000000 and is applied by
  `FrequencyPoint` (Hz, RocofHzPerS) and `FrequencyResponse` (peak RoCoF, nadir, nadir time, settle). Every non-NaN
  number is unchanged.

## [1.95.263]
### Added
- `Iec104StatusTypes` decodes and encodes ASDU types 5 / 32 (step position) and 7 / 33 (32-bit bitstring), refusing bad
  input; still behind the existing refusal. Bit numbering and the CP56 century are not yet confirmed against the source.
- `gridsim-dist3ph/2`, the per-unit v2 form of the three-phase feeder (schema, reader, writer, `dist3ph ... to-v2`,
  `validate`); every bundled feeder converts and reads back bit-identical; v1 is unchanged.
- Classical `stability --clear` scores law 9 (synchronism) from the run's rotor angles: any machine more than 180
  degrees from the centre of inertia (the PSS/E / PowerFactory out-of-step criterion). The verdict is unchanged.
- `--export-tight` (with `--enableExport`) re-solves down to 1e-15 pu and exports the tightest converged state, named
  in `solve.tightenedTolerancePu`; default exports are unchanged.
### Changed
- The CCT is the first loss of synchronism; pockets above it are reported as notes. Every CCT explanation and manual
  ch. 19 state the 5 s first-swing window and that a longer window can give a shorter CCT.

## [1.95.264]
### Added
- IEC 104 step position (5 / 32) and bitstring (7 / 33) are decoded in `Iec104Asdu` and the interposer, each as its own
  point type (a moving tap flags `RtuQuality.Transient`); they are never estimator inputs, and a crosswalk binding one
  is refused.
- `stability --rms --clear` prints the law 9 (synchronism) line; the steady-state "NOT SCORED" text names the verb that
  scores it.
### Changed
- Documentation: the `gb-reduced-29` and `gb-synthetic` READMEs describe their data as shipped (no data change);
  `data/bundled.sha256` regenerated.
- Internal: kind-spec references use symbols instead of quoted source lines; no value change.
### Fixed
- NGED: a site with demand on several voltages joins generation to the bus of its own voltage (otherwise it is left
  out with a warning); "S.Stn" no longer leaves a stray "s"; KNAREBOROUGH and KNARESBOROUGH read as one group.
### Security
- `GridSim:TrustedProxies` decides whose forwarded headers are trusted (default loopback; Compose sets
  `loopback,private`). Deployments behind a proxy that is not on loopback must set it; a bad value stops start-up.

## [1.95.265]
### Added
- `harmonics --afe-reactance`: an active-front-end converter can be modelled as resonant plant in Y(h). Opt-in with no
  default (G5 Issue 5 s.3.29 names the effect but gives no value); every existing result is bit-identical.
- `iec60909 --peak-method c`: the IEC 60909-0 Method C peak factor (fc / f = 0.4, not yet confirmed against the
  source). Method B stays the default, bit for bit.
- `GridSim:ReplayCases` (default gb-full): a replay-only instance refuses any other `bridge:<case>@raw` by name.
- `Auth:KeyPreviousCertificatePath`: a decrypt-only previous certificate for key-ring rotation. Deployments that rotate
  the certificate must set it (`docs/developer/deploy/README.md`).
- The `interpose` stop line and audit record count step positions and bitstrings.
### Changed
- NGED transformer impedances are read as percent on 100 MVA (they were read as ohms referred to the GSP voltage),
  proved on NGED's own fault-level table at Banbury Road 33/11 kV. NGED gda results change; UKPN and NPg are unchanged.
- Internal: kind specs and the CLI reference named constants (`CliDefaults`) instead of literals; no value change.
  Process tests for the `fault` and `chain --policy lp` exit 3 paths build their own inputs.

## [1.95.266]
### Changed
- Internal: planning notes only. No code change.

## [1.95.267]
### Changed
- Internal: planning notes only. No code change.

## [1.95.268]
### Added
- The contingency sweep can solve its Newton steps through a sparse batched interface (`ISparseBatchedLinearSolver`).
  The CPU implementation runs the same sparse LU per system, so results are bit-identical to the sequential and
  parallel sweeps. The dense GPU batched solver plugs in through an adapter, outside the bit-identity guarantee.
- RMS studies take an event list (bus fault and clearance, branch trip, load step, generator trip) with the standard
  control library; invalid inputs are refused by name. A fault plus clearance as events reproduces the single-fault
  study bit for bit; with no events the old path runs unchanged.
- The harmonic export records the AFE impedance and `crossval_harmonics.py` applies it (exports without the option are
  byte-identical); the three-phase harmonic study takes AFE units as balanced three-wire shunts; IEC 60909 Method C
  covers idc and the three unbalanced duties (the DC frequency ratios are not yet confirmed against the source).
### Changed
- One per-fuel direct-combustion carbon table from NESO's Carbon Intensity Regional Forecast Methodology (May 2024)
  Table 1: coal 0.937, CCGT 0.394, OCGT 0.651, oil 0.935, biomass 0.120 (supply-chain basis), other 0.300 t/MWh;
  interconnectors and grid infeeds 0. MACHINE, MARKET and carbon tracing all use it, so their carbon figures change.
- NGED circuits are read as percent on 100 MVA (`GdaModelBuilder.NgedCircuitConvention`), proved from NGED's Table 4
  fault-level impedances at five radially fed sites. NGED circuit R and X rise 1.7424x in 132 kV groups and charging
  57.39x. UKPN and NPg are unchanged.

## [1.95.269]
### Added
- `estimate --method lav-lp`: exact LAV estimation by linear programming; case14, case30, case57 and case118 seeds 1-5
  converge in 4-7 iterations. IRLS `lav` is unchanged bit for bit and marked approximate in a NOTE line.
- Open wye / open delta and open delta / open delta banks, and unequal winding impedances, in the three-phase sweep
  (Kersting's construction); three equal windings reduce to the existing element bit for bit. Not yet confirmed
  against Kersting's examples.
- `pmu-ingest --live tcp://...|udp://...`: live C37.118 transport (framing faults stop the run, CRC failures are
  counted; off by default).
- `nminus1 --batched-lanes N`.
- `stability --rms --events`, `--clear-by-trip`, an event-driven CCT search and a reclose event.
- `iec60909 --peak-method c` with `--type ll`, `slg` and `dlg`.
- Feeder `converters` section and `dist3ph harmonics --afe`; the v2 writer refuses a feeder with converters rather than
  dropping them.
- Sparse projection statistics (median and MAD over nonzero projections). Not used by Schweppe-GM: with them, clean
  case14 gives J 509.80 (FAIL) against 86.34 (PASS) with the default leverage.
### Changed
- The case editor refuses a v2 save that the v2 loader refuses. `ProvenanceTrust` (ranked, downgrade-only) is enforced
  for replay inputs and for case files at save.
- The eigenvector-matrix inverse runs in parallel from order 256, bit-identical for any worker count.

## [1.95.270]
### Added
- `pmu-ingest --live ... --live-quarantine <dir>`: present-time frames are written byte for byte to `*.quarantine.c37`
  with a CuratedPlaceholder sidecar and reach nothing downstream; `pmu-ingest <file>` refuses a quarantine capture.
  Default unchanged.
- The `dist3ph` v2 form holds `converters` (v1 -> v2 round trip bit-identical; feeders without converters write the
  same bytes).
- `stability --rms --events ... --events-out <dir> [--events-every N]`: trajectory CSVs with run header and provenance
  line, a `gridsim-rms-events-export/1` summary and `run-metadata.json` (default every step).
### Changed
- `estimate --method lav` is now the exact LP form (`lav-lp` is an alias); the IRLS form is `--method lav-irls`, marked
  approximate. LAV results change. LAV prints sum |r| / sigma and a NOTE with no chi-square verdict anywhere: export
  chi-square fields are null with `chiSquareVerdict: false` and `lavObjective`, the audit line reads
  `chi2=no-verdict lav_objective=...`, and `--fdia-screen` gives no verdict. Non-LAV exports are byte-identical. The
  dense 2000-state cap refusal names the cap and suggests `lav-irls`.
- An event's effective provenance tag is the weakest of its parts. `replay-event` refuses, by name (exit 1), any event
  not Recorded or DerivedFromRecorded throughout. `scenario`, fleet, evidence and `h0-study` accept placeholder or
  synthetic events (for example `gb-largest-loss-1320`) and their outputs inherit the weaker tag (`input provenance:`
  line, `provenance=` in the run header). `--voll-source` no longer raises the VoLL tag; the harmonic headline and
  built-in recorded events with derived loss figures report DerivedFromRecorded.
### Fixed
- `replay-event` tagged its simulated `baseline-frequency.json` as Recorded; it is now DerivedFromRecorded at most.

## [1.95.271]
### Changed
- Internal: validation run recorded. No code change.

## [1.95.272]
### Changed
- Tests: the projection-statistics leverage test now expects the documented Wilson-Hilferty quantile
  (`Assert.InRange(d, 2.7085, 2.7086)`) instead of the exact chi-square quantile.
### Fixed
- The exact LAV linear program (`LavLinearProgram`) cycled at degenerate vertices, which every exact measurement set
  has at the truth. Each nonbasic row now keeps its sign side, residuals within 1e-12 of their operands' scale snap to
  exactly 0, Bland's rule applies after a non-improving step, and the result comes from a fresh refactorisation of the
  final basis, so cold and warm starts give the same bits. Random stress against brute-force basis enumeration: no
  pivot-cap hits (at most 21 pivots).

## [1.95.273]
### Changed
- Tests: the Method C `ll` DC-line bound is `<= 8 * 2^-52 * sqrt(1 + (R/X)^2)`; the clearing-by-trip test uses case9
  bus pair 7-8 (MATPOWER numbering); the open-bank test halves the IEEE 4-node load (the full load has no solution
  through open banks); the README check reads from the repository root; the v2 round trip names the provenance.
### Fixed
- An invalid AFE impedance is checked once, up front, in `HarmonicPenetration.Solve`, `HarmonicImpedanceScan` and
  `HarmonicFrequencySweep`, so the `ArgumentException` is no longer wrapped in an `AggregateException`. No result
  changes.
### Security
- `TrustedProxies.Parse` refuses a CIDR whose written address is not the network base address (.NET 10 accepts
  `10.1.2.3/8` and widens it to 10.0.0.0/8, so a typo would trust a whole /8).

## [1.95.274]
### Changed
- Internal: validation re-run and research into values not yet confirmed against their sources. No code change.

## [1.95.275]
### Changed
- Citations corrected in comments and manual ch. 19, with no number changes: IEC 60909-0:2016 clause numbers (Method B
  and fc = 0.4 f 8.1.2, m clause 14 / Annex A, unbalanced Ib 9.2, meshed breaking 9.1.7, single-fed 9.1.1, meshed
  steady state 11.2.7, lambda Figures 15 / 16, idc clause 10); the PSS1A filter has A1 and A2 only, with T6 the
  transducer lag; ESQCR reg. 27(3)(a); the 21N 0.10 preset cites the Siemens 7SA522 V4.3 manual address 1207A.
- The loader refuses a step-up `oy-od` bank by name.
- Known limitation: the step-up open-wye / open-delta bank of the IEEE 4-node feeder is a leading connection the
  element does not model.
- Tests: new tests check the IEEE 4-node "Open Gr.Y-D" step-down cases, balanced and unbalanced: 17 of 18 voltages and
  10 of 10 currents within half the last printed digit; the balanced node 2 phase a angle (+0.0577 deg) is tested as a
  measured residual.

## [1.95.276]
### Changed
- Internal: no code change.

## [1.95.277]
### Changed
- Documentation: the user manual is brought in line with the code (chapters 1-8, 10-18, 20-22, 25-31, 33, the CLI
  reference and the contents page), with a new chapter 9 on the web app and the REST / WebSocket API. Ch. 10 quotes the
  single NESO carbon table.

## [1.95.278]
### Changed
- Documentation: technical chapters, design and assurance records and README files brought in line with the code;
  ch. 32 (CIM) rewritten; new ch. 34; ch. 25 uses the IEC 60909-0:2016 clause numbers.

## [1.95.279]
### Fixed
- Internal: restored record files damaged by a script. No code change.

## [1.95.280]
### Changed
- Documentation: NESO's Carbon Intensity Regional Forecast Methodology v2 (May 2024) is filed as
  `docs/reference/carbon/S325`; its Table 1 matches `CarbonFactors` exactly. The `CarbonFactors` comment cites NESO's
  approximate import figures and why direct accounting keeps imports and GSP infeeds at 0; no value changes.
- Known limitation: under direct accounting, the default, a DNO model fed from the grid reads near zero carbon at those
  loads (manual ch. 10).

## [1.95.281]
### Changed
- Documentation: the manual ch. 10 carbon section says grid infeeds are 0 too, that a distribution model fed mostly
  through its infeeds therefore reads close to zero, and quotes NESO's approximate import figures as not applied.

## [1.95.282]
### Changed
- Internal: record keeping only. No code change.

## [1.95.283]
### Changed
- Internal: planning notes only. No code change.

## [1.95.284]
### Added
- Internal: reproductions of three pandapower `from_ppc` conversion issues (transformer ratio on the wrong winding with
  `tap_side` ignored; transformer B turned into i0_percent = |B|; only the first generator per bus keeps voltage control
  and a Q range), checked on pandapower 3.5.6.

## [1.95.285]
### Changed
- `crossval` keeps its three workarounds for the pandapower `from_ppc` issues and the Ybus guard that stops any
  comparison that is not like for like.

## [1.95.286]
### Added
- `docs/PAYWALLS.md`: why some figures stay marked `[VERIFY]` because the source documents are not free, and
  for each what GridSim uses, what public evidence confirms, the document that would settle it, and the flag or JSON
  field that overrides it (or that it is fixed in code). Linked from the README.
- `.github/workflows/crossval.yml`: manual trigger and weekly schedule on windows-latest with Python 3.12 and
  pandapower 3.5.6, running `gridsim crossval` on 15 cases. Any case not exiting 0 with `within_tolerance` true fails
  (gb-network-2224 under its documented allowance); exit 77 is a failure.
- README roadmap item 15: per-asset profiles (the Parquet profile library).

## [1.95.287]
### Changed
- Tests: the LAV cap tests run `lav-irls` with their old expectations; the NGED caller-built test sets the ohmic
  convention; the IEC 104 unsupported-type counter uses type 21 and checks type 5 as decoded; the LAV NOTE check names
  `lav-irls`; the VoLL test also checks that a Recorded VoLL is refused.
- Tests: expectations follow the new behaviour: NESO carbon figures, decoded IEC 104 types, NGED percent on 100 MVA,
  the new refusals and provenance tags, two physics-check fixture lines, Method C idc at buses 1, 5 and 7, and the IEEE
  4-node values (-93.5 deg, 2247.6 V, Y-D cases against the ungrounded column with 10 residual rows).
- Tests: the kind-spec one-ulp check quotes `DefaultTapStepPu = 0.0125`, so the comparison runs again.

## [1.95.288]
### Added
- `interpose --live-seconds S` bounds a run (same name and limits as `pmu-ingest`); without it nothing changes.
- Tests: loopback process tests for `interpose` with a test IEC 104 outstation and a Modbus tap client.
### Changed
- IEC 104: `interpose` acts as the controlling station (new `Iec104ControllingStation`): STARTDT act on connect, TESTFR
  con in reply to TESTFR act, an S-frame acknowledgement after w = 8 I-frames or t2 = 10 s, and TESTFR act after t3 =
  20 s idle (IEC 60870-5-104 clause 5 defaults, not yet confirmed against the source). The received-data path is
  unchanged.
- Exit code: the first rejected or undecodable frame prints a WARNING on stderr and the run exits 1 at the end unless
  `--tolerate-malformed` is given (then exit 0 with the warning); exits 3 and 2 take precedence. Discarded bytes are
  counted in the stop line (`, discarded-bytes N`) and the audit record (` discarded-bytes=N`). Manual ch. 19.
- Modbus: `interpose` still taps the stream passively and never writes.
### Fixed
- `interpose` never sent anything on IEC 104, so a real outstation waiting for STARTDT would never have sent data.

## [1.95.289]
### Added
- Large-case tests are gated by installed RAM (see `tests/CONDITIONAL-TESTING.md`): 8 GB runs everything up to gb-full
  (3,539 buses), 16 GB and up runs the whole suite, and between them the cap rises. A test above the cap is reported
  SKIPPED with the reason, never passed. `GRIDSIM_TEST_MAX_BUSES` (`all` or a bus count) overrides it, and any other
  value fails; the release workflow sets `all`. New `[LargeCaseFact]`, `[LargeCaseTheory]` and `[LargeCaseInlineData]`.
### Changed
- Tests: the case9241pegase and continental-europe large-case tests run only when the RAM gate allows; their
  assertions are unchanged, and the test inventory is the same on every machine. GridSim results are unaffected.

## [1.95.290]
### Changed
- Internal: removed every branch except `main`, including stale trial and documentation branches.

## [1.95.291]
### Removed
- Internal review and planning records are no longer part of the repository.

## [1.95.292]
### Changed
- Documentation: the changelog is rewritten as a user-facing changelog.

## [1.95.293]
### Changed
- Documentation: the acceptance criteria and contributor conventions are in `CONTRIBUTING.md`.
- Documentation: the README lifecycle note and the restriction and assurance documents are tidied.
- The `docs/Public/GB` submodule moves to the GridSim-GB-Documents repository on `main`, and now also holds the
  official policy documents and legislation (about 265 MB).
### Removed
- Internal: two unused GitHub workflows and the documentation fact-check working notes.

## [1.95.294]
### Changed
- Documentation: the assurance documents and the audit, deployment and security chapters describe the controls as they
  are at this release, including the release signing, the two software bills of materials and the web container image.
- Documentation: the user-manual and technical-manual chapter files are named `NNNN Chapter title.md` (for example
  `docs/manual/0019 CLI reference.md`), and every link, help message and error message that names one follows.
- Documentation: the README is rewritten as a full overview of every feature, the solvers and their mathematics, with
  quick starts.
- Documentation: `PAYWALLS.md` moves to the repository root.
- Documentation: audit-log examples use a neutral actor (`analyst@workstation`), with every hash, anchor and chain
  link recomputed so each example still verifies.
- The licence states no prices; commercial and network-operator terms are on application. The licensing contact is
  `gdatooling@proton.me`, in the licence, the documentation and every licence message.
- Dependabot version-update pull requests are paused for the release (`open-pull-requests-limit: 0`); security
  alerts are unaffected.
### Removed
- The two redirect stubs for technical chapters 13 and 14, the review notes under `docs/reviews/`, one GB screenshot,
  and the documentation tools tied to the old chapter naming (`tools/docs/rename.py`, `tools/docs/relabel.py`).
### Fixed
- Documentation: plain ASCII and EN-GB spelling throughout `docs/`, and stale source links in `PerformanceOpts.md`.
- Tests: new tests pin the FDIA screen against a residual-cloaked attack and a chi-square failure, bit-identical
  estimates and replays run to run, the estimator's independence from the clock, and detection of deleted audit
  entries.
- Tests: an IEC 104 interposer test expected a 26-octet time-tagged float frame; the frame is 27 octets (6 APCI, 6
  data-unit identifier, 3 address, 5 element, 7 CP56Time2a), as the builder produces and the test's own comment sums.
- Tests: the tests that read the CLI reference or check the README follow the new chapter file names and the README's
  current wording; their assertions are unchanged in substance.

## [1.95.295]
### Added
- Compliance attestation: a build carries a machine-readable inventory of the controls it provides, each bound to the
  test or artefact that evidences it, and a deterministic build hash. `gridsim compliance verify` (and a web
  `/compliance` endpoint) reports the attestation and whether every in-scope control is achieved, so a running binary
  can prove it is the attested build. A release build is attested compliant only when every in-scope control is
  achieved and its test is green.
- Incident reporting: detected security events can be raised as structured incidents to a deploying operator's
  reporting channel, with a conservative policy that escalates a burst of events to a single alert; incidents are
  deterministic and never carry a future-dated grid instant.
- Access control: a role-based access-control gate (viewer, analyst, operator, administrator) for the hosting service
  seams, independent of the data-boundary guard; every denial is recorded in the audit log.
- Availability: a service-level objective and health probe over the resident estimator's own counters, with a web
  `/health` endpoint that reports degraded when an objective is breached.
- Resilience and limits: a bounded reconnect-backoff schedule, a cap on the largest bridge message and the number of
  in-flight queries, refusal of an unencrypted bridge endpoint unless explicitly allowed, and a cap on the number of
  measurements loaded from one set.
- False-data detection: the FDIA screen can take protected (synchrophasor-anchored) measurements and flag a feasible,
  in-limit cloaked attack that disturbs an anchored quantity.
- Response: a measurement source can be marked distrusted, forcing its rows to pseudo-measurement weight without
  changing any value.
### Security
- Security monitoring: the audit trail can record one entry per security event (a refused too-fresh input, a refused
  dynamics fusion, a suspected false-data-injection, an infeasible estimate, an access denial, a quarantined live
  feed), each with its reason, in addition to the per-run entry; entries can be forwarded to a monitoring sink (for a
  SIEM, syslog or a write-once store), with the on-disk hash chain staying the authoritative record and a sink outage
  never stopping estimation; and the audit anchor can carry a detached signature, so a rewrite of the log and its
  anchor together is detectable when the signing key is held off the host.
- Secrets: a secret-store seam lets a deployment resolve the data-protection certificate password (and other feed
  credentials) from an operating-system keychain or vault rather than from plain configuration; a test enforces that
  no secret appears in an audit summary.
- Supply chain: the NuGet restore graph is pinned with a per-project lock file, and a release build can attest the
  software bill of materials it was produced against.
### Changed
- Documentation: the assurance documents map GridSim to the Cyber Assessment Framework version 4.0 and add governance,
  risk-register and incident-response statements, plus companion cross-framework mappings (NIST SSDF, SLSA build
  provenance, model-and-simulation credibility, Cyber Essentials, and a roadmap of further standards).

## [1.95.296]
### Fixed
- MATPOWER import: a scalar such as `mpc.baseMVA` is evaluated as a whole expression, like a matrix entry, and anything
  the evaluator cannot read completely is refused instead of truncated. The bundled `case533mt_hi` (`baseMVA = 50/3`)
  now loads on its own 16.667 MVA base instead of 50 MVA, so every result of that case changes.
- Backward/forward sweep: the reported total losses include the bus shunts, as the Newton engines' do, so a radial
  feeder with a conductive shunt or a capacitor bank no longer fails the physical-law audit on a correct solve.
- FDIA screen: Kirchhoff's current law is judged only where the estimate is physically bound (its zero-injection rows),
  not against the case file's dispatch, so a correct estimate of real telemetry no longer reads as a suspected
  false-data injection.
- Hydro governor: an instant gate (`Tg = 0`) solves the gate and transient-droop loop exactly instead of diverging; a
  finite gate time too short for the step, which would ring or diverge, is refused naming the smallest usable value; a
  non-finite gate time or a negative transient droop is refused.
- IEC 60909 breaker check: the short-time withstand is scaled down for a fault longer than its rated duration but never
  raised for a shorter one, so a short fault above the rating is no longer reported adequate.
- Generator costs: a redispatched model keeps every generator, out-of-service units included, in its file position, so
  a cost file's `index` lands on the intended unit; `index` is refused on a case whose loader skipped generator rows
  (address those units by name).
- `stress` exits 3 when a case has no converged load point; load points that fail numerically are counted in the
  report, and any other exception stops the run instead of reading as voltage collapse.
- Web and desktop bridge: a `ws://` bridge endpoint can be allowed with `GridSim:BridgeAllowUnencrypted=true`
  (`BridgeAllowUnencrypted` on the desktop); without it the web host stops at start-up naming the setting, and an
  endpoint that is not a `ws://` or `wss://` URI is refused. Every bridge client gets the host's resource limits.
- UKPN builds: demand rows whose substation names no network node, or that have no substation name, are counted and
  reported with their megawatts instead of disappearing silently.
- Tests: new tests pin each of these; the generator-copy property list gains the new skipped-row count.

## [1.95.297]
### Added
- Audit: one audit entry per security event at the detection sites of `estimate`, `serve`, `interpose` and the web
  host's resident estimator (an input refused by the horizon guard, an infeasible estimate, a suspected false-data
  injection, an output-guard refusal, a quarantined live window, a breached service-level objective).
- Audit: `--audit-forward <file>` (web `Audit:ForwardPath`) copies every entry, as one JSON line, to a second file for a
  SIEM collector or a write-once mount; `--incident-log <file>` (web `Audit:IncidentLogPath`) raises significant
  security events, and bursts of routine ones, as incidents in a JSON-lines file, each naming the audit entry that
  evidences it.
- Audit: the anchor can be signed after every entry (`--audit-sign-key`, web `Audit:AnchorSigningKeyPath`, an ECDSA
  P-256 private key) and checked before a run (`--audit-verify-key`, web `Audit:AnchorVerificationKeyPath`), so a log
  and anchor rewritten together without the key are refused; `gridsim audit verify --pubkey` checks it offline and
  `gridsim audit keygen` makes a key pair.
- Secrets: `Secrets:Directory` on the web host reads secrets from one file per secret (Docker, Kubernetes or systemd
  credentials) before the environment, for the key-ring certificate password.
- Release: every file on a GitHub Release gets a signed build-provenance attestation (`gh attestation verify`).
### Changed
- Compliance attestation: only a release build can attest compliance, and only when every in-scope control is recorded
  Achieved; a local build reports "not attested" and `gridsim compliance verify` exits 1 for it. The report (and the web
  `/compliance` endpoint) lists every control not yet Achieved. Role-based access control, distrusted-source
  containment and the reconnect schedule are recorded as partially achieved: their primitives exist but no shipped path
  uses them yet.
- Build identity: the build hash takes the .NET SDK that built the binary instead of the runtime it runs on, so it is
  the same on every machine; it is documented as an identity label, not an integrity proof.
- Release: the SBOMs are generated first and their SHA-256 is built into the CLI, the web image and the NuGet package;
  each CLI binary must report a release build with that hash before it is released.
- Release: every publishing job waits for the full CI matrix, now also Apple-silicon macOS, AVX-512 forced and FMA off,
  with no advisory legs.
- Signing: the release and package signatures are verified against exactly the pinned release certificate, so signing
  works with the current self-signed certificate.
- Repository: the third-party reference PDFs under `docs/reference` are no longer tracked (their redistribution terms
  do not all allow it); the catalogue and checksums stay.
### Fixed
- Bundled data now ships with its licences and attribution files (MATPOWER, PyPSA-GB, the starter maps' ODbL and
  CC BY attribution), and the third-party notices list every bundled data source.
- Tests: new tests cover the audit wiring, the directory secret store, the release gates and the shipped licence files;
  the compliance tests that asserted the hard-coded statuses are replaced by tests of the verdict's real inputs, and
  the VoLL reference test checks the catalogue where the PDFs are not present.

## [1.95.298]
### Added
- `gridsim audit sign <log> --key <private.pem>` verifies a log against its anchor and signs that anchor offline, the
  step that lets a host keep only the public key; a log that does not verify is not signed.
- Release: the web image must report a release build with its SBOM hash from `/compliance` before it is released, and
  the pushed image gets a signed build-provenance attestation in the registry.
### Changed
- Documentation: the assurance documents, the README and the audit chapter state what the code and the release
  workflow now do: which controls are achieved and which are partially achieved, the build identity as a label rather
  than an integrity proof, how a download's integrity is checked, the per-event audit, forwarding, incidents and the
  signed anchor, the secrets directory, the bridge transport setting and the release gates.
- Determinism: the orderings that feed numerical results (the reverse Cuthill-McKee ordering behind every sparse LU,
  degenerate eigenvalues, conjugate-pair modes, near-equal LAV breakpoints) use GridSim's own frozen copy of the .NET 10
  sort, so a runtime release cannot change them; on .NET 10 the order, and every result, is unchanged.
- GDA inputs: file modification times are never used to choose a register or table; several candidates without a
  publication date in their name are refused, naming them. `gda --fleet` prints the register as-of and the files read.
- `gda-replay --start` takes an ISO 8601 instant read as UTC whatever the machine's time zone (any other text exits 2).
- IEC 104: a CP56Time2a time tag with the summer-time bit set is an invalid time (the station must stamp UTC).
### Fixed
- Replays: two snapshots with the same index, a repeated telemetry key, a culture-shaped `time_utc` and a non-numeric
  telemetry cell are refused instead of being read in an order or meaning that differs between machines.
- Result text in replay summaries and MATPOWER file notes uses the invariant culture.
- The web host reports a failed audit forward or incident write, and an anchor it could not update, when the resident
  run ends.
- The web map payload's change test treats two NaNs that differ only in sign as the same value.
- Tests: the licence-install refusal test uses a temp licence file, never the installed one; the EMT out-of-phase test
  writes only to its own temp folder (unless `EMT_REPORT_DIR` is set) and checks the CSV's content; new tests pin the
  frozen sort, the summer-time refusal and the replay refusals.

## [1.95.299]
### Changed
- State estimation: every WLS and robust (Huber, Schweppe-GM, LAV by IRLS) estimate on the CPU is solved through one
  canonical sparse step at every size, as every Newton step already is, so an estimate is bit-identical whichever side
  of the 200-bus threshold the case falls; the threshold now only chooses how the observability report is computed.
  Estimates of cases at or below 200 buses change in their last bits.
### Fixed
- Iterative solvers (BiCGSTAB on the CPU, the complex Jacobi-BiCGSTAB, and the ILGPU and ROCm sparse solvers) decide
  success on the true residual b - A x, not only on the recursively updated one, so a solve that drifted from its
  stated tolerance reports failure (or falls back) instead of returning an inaccurate step.
- Tests: an exact test pins WLS and Huber estimates bit-identical across the dense/sparse threshold.

## [1.95.300]
### Fixed
- Frequency studies: the explicit swing refuses an inertia below its damping stability bound (it used to run into
  Infinity and NaN and return the result), any non-finite step fails loud, and the grid-forming sub-step count is never
  silently capped below what its gain bound needs.
- RoCoF screening: a loss that leaves too little inertia to integrate at the screen's step is reported with its
  closed-form initial RoCoF, an unknown nadir and every limit breached, and named on stderr, instead of being integrated
  on the 1 MW.s floor.
- Harmonics: an order list that is empty, holds a non-positive order or repeats an order is refused (a repeated order
  was counted twice in THD), `Y(h)` refuses a non-positive or non-finite order, an impedance scan left with no order
  (all triplens in balanced mode) is refused instead of reading "no resonance", and damping sharpness refuses an empty
  scan.
- MATPOWER import: a raw-unit (ohm and kW) feeder with more than one generator is refused instead of keeping its
  generators in kW while its loads became MW.
- CLI: `--forming-inertia` and `--forming-damping` refuse NaN, Infinity and negative values and take a negative number
  written after a space as their value, and both are refused without a former; `modal --damping` refuses NaN, Infinity
  and negative damping.
- Event replay: a simulated RoCoF of the opposite sign to the recorded one fails the RoCoF test instead of scoring a
  zero error.
- Tests: new tests cover the swing, forming and screening guards and the harmonic order checks.

## [1.95.301]
### Security
- Web: a browser WebSocket upgrade to `/ws/sim` must come from this host or an origin listed in
  `GridSim:AllowedOrigins`; any other origin is refused (403), so a page elsewhere cannot drive or read sessions
  through a user's browser.
- Web: a replay run is subject to the licence bus limit like a live case (a run of a bundled network stays exempt).
- Licensing: the shipped-content manifest that decides which networks are bundled is compiled into GridSim.Core; a line
  appended to the copy beside the data no longer makes a network bundled.
- MATPOWER v2 case loading checks that every listed network file lies inside the case folder before any of them is
  read, so a path outside it is never opened or quoted in an error.
- `crossval` looks for its sidecar script only beside the executable, never in the current folder.
- The horizon guard normalises the "now" it judges against to UTC, so a local-time now no longer moves the horizon
  forward by the UTC offset; the real-time engines use the checked, normalised now.
- The audit monitor reads a security event only from an entry that is one, parsed by position, with defined kind and
  severity names only.
- `.gitignore` excludes private-key and certificate-with-key files.
### Fixed
- Harmonics: an error raised on a parallel order or frequency point surfaces as itself (the lowest index first), not
  wrapped in an aggregate exception that the CLI did not catch.
- The analysis cycle publishes a failure instead of dying silently (the web CYCLE tab says the cycle stopped), no longer
  touches its token source after disposal, and publishes its results with memory barriers.
- GPU shutdown waits for an in-flight solve before disposing the accelerators.
- Web: the resident estimator's skipped-file list is read as a snapshot, so the API cannot fail while a file is being
  added.

## [1.95.302]
### Fixed
- LP solver (DC-OPF, storage dispatch, security-constrained DC-OPF): malformed input (a non-finite coefficient,
  right-hand side or cost, a NaN bound, an empty interval) is refused; a NaN Phase I sum reads infeasible; a basis that
  is singular at refactorisation fails loud instead of being clamped; and an Optimal point is checked against its own
  rows and bounds before it is returned.
- Eigen-solvers: a non-finite eigenvalue or eigenvector, and NaN in the biorthonormality or cluster checks, fail loud;
  the symmetric eigen-solver refuses a non-symmetric matrix instead of returning the eigenvalues of neither it nor its
  symmetric part.
- Linear solvers: the dense LU refuses a matrix with a NaN entry (it returned a NaN solution as success); the CPU and
  GPU batched solvers report a system with a non-finite solution as failed; the GPU sparse solver no longer overwrites
  the caller's right-hand side when it is zero, and decides a zero right side exactly.
- Power flow: a NaN in the Jacobian (for example a stored Vm of 0 at a PQ bus without a flat start) stops the solve as
  not converged instead of escaping as an error; a singular step on a GPU dense backend stops the pass as on the CPU;
  `PowerFlowOptions` refuses a tolerance that is not a finite number in (0, 0.01] pu and a negative iteration cap; a
  result's status follows its convergence flag when a producer does not set it.
- Model validation refuses NaN generator limits, Qmin above Qmax, a voltage set-point not above 0 pu and a non-finite
  stored bus voltage or angle (a non-positive stored Vm is a warning); the backward/forward sweep refuses a non-positive
  generator set-point instead of replacing it with 1.0 pu.
- Switched shunts: a bank at the end of its range with its bus still outside the band is reported, by name, and the
  shunts are not reported settled.
- Physics audits: a NaN residual, voltage, loss or impedance fails the check instead of being skipped by a maximum or
  minimum and reported as holding.

## [1.95.303]
### Fixed
- State estimation: every estimation option is validated before the estimate runs (an invalid chi-square confidence
  used to run the bad-data removal while reporting the test not run); a negative state variance beyond round-off, or a
  NaN one, reports a NaN sigma instead of sigma 0.
- Frequency studies: every swing engine checks each step for a non-finite value and fails loud; grading refuses a
  non-finite response instead of calling it within limits; every numeric frequency-model setting (droop, damping,
  step, duration, governor and fast-response constants, forming damping) is validated.
- Dynamics: the machine integrators refuse a NaN step or duration (a NaN step gave a one-sample "stayed in synchronism"
  result), and the RMS transient-stability study refuses a non-finite or negative fault onset or clearing time instead
  of clamping it.
- Harmonics: a harmonic source on a bus that is not in the model, or on the three-phase source bus, is refused instead
  of being dropped; the coupled harmonic flow reports the source components outside the analysed orders; the harmonic
  state estimator refuses an unknown bus and an unusable sigma.
- Three-phase WLS: a voltage-magnitude measurement on an unknown bus or phase is refused instead of being dropped.

## [1.95.304]
### Fixed
- Fault analysis: a faulted state whose converter current fixed point did not converge is reported not converged, so
  the studies that require a converged state refuse it.
- Protection cascade: only a numerical failure of a re-solve is read as a non-convergent split; any other error is no
  longer turned into a blackout verdict.
- IEC 60909: the duty formulae refuse a non-finite or non-positive nominal frequency or fault duration, a negative or
  non-finite breaker time, and a Thevenin reactance that is not above 0.
- Storage: a `batteryChemistry` code that is not 0 (none) or one of 1 to 6 is refused instead of silently giving the
  lossless model.
- Watch list: a non-converged power flow gives only the convergence item; thermal and voltage items are no longer
  built from its iterate.

## [1.95.305]
### Fixed
- MATPOWER import: a negative generator mBase is refused, and a 0 mBase (the system base) is noted.
- Generator cost files refuse duplicate keys; bridge cost curves are checked for shape (piecewise-linear pairs, at least
  two points; at least one polynomial coefficient) and finite numbers.
- IEC 104: an APDU length above 253, and an S- or U-format frame carrying a payload, are refused.

## [1.95.306]
### Fixed
- Desktop app: a settings file that cannot be read is reported and never overwritten; a power flow that did not
  converge is shown as not valid (a banner over every tab and a veiled map) instead of as a state; a what-if solve that
  did not converge is never drawn; a lost bridge stream says so, clears the bridge data and offers a reconnect; views
  rebuild on every model change and key their rows by identity, not by position; OLTC views use the one shared mapping
  from tap-changer records to branches; SCADA points read invalid on a non-converged solve.
- Desktop app: the frequency dials match their needle scale, any loading of 100 % or more is red, transformer bushing
  voltages are phase voltages (no invented imbalance), the pole-slip read-out uses the widest angle across one branch,
  generator CO2 reads the one carbon table, and synthetic AVR and governor rows are labelled as such.
- Case editor: keys are refused while the window is too small (except quit and copy), Caps Lock no longer turns undo
  into redo, unexpected errors are shown without losing the session, and the editor redraws on resize.
- Unity viewer: the loader checks its input, and a non-converged frame is drawn grey.
- Documentation: the desktop-app, analysis-views, case-editor and architecture chapters follow these changes.
- Tests: new tests pin the case-editor guards and the desktop app's fail-loud presentation rules.

## [1.95.307]
### Changed
- Documentation: the manual and technical chapters, the README and the assurance documents describe the current code
  where they had fallen behind it (solver options, exit codes, defaults, validation rules, estimator and dynamics
  behaviour, the GDA readers, the canonical WLS step), and captured outputs from earlier builds are marked as such.

## [1.95.308]
### Fixed
- Sequence fault study: a single-line-to-ground fault whose current is not finite is refused rather than reported.
- Multi-phase JSON loader: a line with a negative self resistance, a non-symmetric shunt matrix or a negative self
  susceptance is refused.
- CLI: `emt` takes its case name through the same single-positional check as the other commands.

## [1.95.309]
### Fixed
- MATPOWER import: a row shorter than the standard columns, a fractional or non-finite bus id or type, a bus type other
  than 1 to 4, a negative or non-finite status, a NaN, a negative tap or mBase, a non-positive baseMVA, an unclosed
  matrix and a field assigned twice are refused. A missing baseMVA, gen or branch matrix, indexed assignments after the
  matrices (which are not run) and branch angle-difference limits (which are not read) are reported in the notes.
- MATPOWER export: a negative mBase is refused, and the class notes say plainly that a bus with no per-bus limits is
  written with the operational band.
- Modbus decoding: a frame whose size does not match its MBAP length, or a read response with an inconsistent byte
  count, is refused (counted as a decode error) instead of being read as "no registers".
- JSON v1 loader: duplicate keys, a line with fewer than one circuit, a tap ratio that is not above 0, a demand power
  factor outside 0.01 to 1 (it was clamped), an OLTC with no transformer or an unusable tap range, step, deadband or
  target, and a boundary direction other than 1 or -1 are refused.
- JSON v2 loader: a whole number written as `1.0` reads as an integer; a fraction or an out-of-range value is refused
  naming the member.
- JSON v2 writer: a generator type, limit, inertia, rating or tap the v2 loader would refuse is refused when writing.
- Bridge protocol: timestamps must be ISO 8601 (for example `2024-03-01T12:00:00Z`); other forms are refused. Propagation
  data giving one corridor two different delays is refused.

## [1.95.310]
### Fixed
- Loss contingency sweep: a cancelled sweep stays cancelled, and an exception that is not a numerical solve failure is
  raised rather than recorded as a not-converged outage.
- Scenario matrix: the inertia axis must be finite and above 0, and a run with an undefined nadir is refused rather than
  graded "no LFDD risk".
- Inertia transforms: a NaN inertia scale or grid-forming fraction is refused.
- Avoided cost: VoLL and LFDD duration must be finite and above 0, and the shed finite.
- Health probe: a run in which no window was processed because every window was rejected or failed is not healthy, and
  failed windows count against the converged objective.
- Dispatcher: a generation surplus held at a zero slack target is reported in the balance note.
- Analysis cycle: when the base solve did not converge no job runs; every job is reported deferred with the reason.
- Live simulation: protections observe only converged network state; a failed tick keeps the last converged state.
- GDA replay: timestamps must be strictly increasing.
- Event catalog: duplicate ids, missing losses or outcome, non-positive inertia or demand, and non-finite or negative
  loss and action times are refused.

## [1.95.311]
### Fixed
- IEC 60870-5-104: an ASDU whose object count does not account for its whole length, or whose sequence of objects runs
  past the 24-bit address range, is refused (counted as a decode error). A NaN or infinite float is not turned into a
  point and is counted, as is an indeterminate double point; a point of a timed type whose time tag is invalid is
  marked invalid and counted, instead of being kept as an untimed point.
- RTU crosswalk: a binding's sigma, baseKv and baseAmps must be finite and above 0 and its scale finite and non-zero;
  a non-finite value is left out of the window and counted in its note.
- C37.118: a phasor channel carrying the missing-data marker (0x8000, or NaN) gives no measurement rows and is counted
  in the note; a missing angle-reference phasor refuses the superframe.

## [1.95.312]
### Fixed
- GDA build: transformers whose published reactance is degenerate are still built at the minimum reactance, but are now
  counted, named in a warning note and reported in the diagnostics. Demand rows left out for a zero or negative maximum
  demand are counted in a note with the negative total. A NaN demand, export capacity or transformer impedance is
  refused.
- GDA CSV reader: a quote inside an unquoted cell is read as a literal character (it used to swallow the rest of the
  file into one cell), a file that ends inside a quoted cell is refused, and a cell reading NaN or Infinity is refused.

## [1.95.313]
### Fixed
- Multi-phase model: a transformer ratio that is not finite and above 0, a non-finite impedance or shunt entry, a load
  that does not give three finite per-phase powers and a source voltage that is not three finite phasors are refused
  when the model is built.
- Harmonic Y(h): a branch with zero series impedance is refused, naming it, on both the lumped and the long-line path
  (the long-line path used to stamp it as an open circuit).
- Bridge protocol: a boundary with no name, or a boundary branch whose direction is not 1 or -1, is refused.
- Harmonic source sets and GDA demand series refuse unknown keys.

## [1.95.314]
### Fixed
- CLI: an empty flag value (`--dir ""`, `--out=`) is a missing value (exit 2) instead of an internal error.
- CLI: `--inertia`, `--sync-inertia-scale`, `--gfm-fraction` and `--units` lists name their own flag in errors and
  refuse non-finite or out-of-range values (inertia above 0, scale and fraction 0 to 1, units 1 or more).
- CLI: `emt --event` with no value is refused instead of running with no event.
- CLI: `gda --as-of` later than the historical horizon is refused.
- CLI: `trip <gen>` refuses a name that matches several generators (an exact name still picks its unit).
- CLI: `chain --bess` storage flags given without a value, a non-finite number, a non-positive `--step-hours`, a
  negative `--degradation-gbp-per-mwh` and `--steps` below 1 are refused (exit 2).

## [1.95.315]
### Fixed
- CLI: the interactive live run, `replay` and `interpose` refuse to start (exit 2) when standard input is redirected and
  no headless limit is given, instead of running until killed.
- CLI: `protection-check` pickups must be above 0 with `--uv` below `--ov` and `--uf` below `--of`;
  `impedance-stability` needs `--fmin` above 0 and below `--fmax`, positive SCR values with `--scr-max` no lower than
  `--scr-min`, and at least one point per decade and SCR step; `gda --base` must be above 0 and `--minkv` 0 or above.

## [1.95.316]
### Fixed
- Web host: with the resident estimator enabled, a blank or missing `Realtime:WindowsDir` stops start-up with a message
  naming the setting, instead of a run of 0 windows.
- `/ws/sim`: `seek` on a live or bridge session and `step` on a bridge session are answered with an error frame instead
  of doing nothing.
- `/ws/sim`: error frames are held outside the drop-oldest frame queue and sent first, so a slow client can no longer
  lose them.
- Live web session: a step that throws pauses the session and records the error, instead of leaving it "playing" on a
  frozen frame.

## [1.95.317]
### Fixed
- CLI: a bare `--asset-dynamics` no longer takes the case or generator name after it as its value, and
  `protection-check --cascade` no longer swallows the token after it.
- GDA: a NESO register status such as "Not Connected", "Awaiting Connection" or "Disconnected" no longer counts a
  project as connected, and "Not in service", "De-energised" and "Not energised" are read as not connected / dead.
- Container image: the JSON schemas Core embeds are copied into the build.
- Server test regime script: every failed or timed-out phase is logged, and the script exits non-zero when any failed.

## [1.95.318]
### Fixed
- CLI audit record: an unhandled exception records the exit code the platform really uses (SIGABRT, 134, on Linux and
  macOS; 0xE0434352 on Windows).
- CLI `replay` (interactive): output to a file or pipe writes plain lines instead of crashing on the console width.
- Web: a live session whose first frame fails releases its analysis cycle and token source instead of leaving the cycle
  running in the background.
- GDA demand series: a per-bus factor naming a bus that is not in the model, or one that is not a finite number 0 or
  above, is refused.

## [1.95.319]
### Fixed
- Island pre-pass: OLTC records whose transformer or controlled bus was removed with a dead island are dropped, and a
  boundary branch that was removed leaves its boundary (a boundary left with none is dropped), so boundary studies of
  the adjusted model no longer fail on a branch name that no longer exists.
- OLTC loop: a tap that starts outside its range is never stepped against the wanted direction by the range clamp; it
  holds and is reported at its limit.

## [1.95.320]
### Fixed
- JSON case writer: every file is built before any is written, so a refused value leaves no half-written case folder
  (which used to block the retry); a write that fails part-way removes the files it had created.
- GDA case writer: every file is serialised before any is written, and each is written to a temporary name and moved
  into place, so a refused value no longer leaves a mix of this run's and the previous run's files.

## [1.95.321]
### Fixed
- Bridge client: an exception thrown by GridSim's own handling of a good message (a view building its frame) is reported
  as a GridSim fault through a new `SubscriberFailed` event, instead of as a rejected server message or a dead stream;
  the web session and the desktop app report it.
- Web dashboard: a skipped bridge message is shown as a notice above the dashboard instead of replacing it with
  "Couldn't load" for the rest of the session.
- Web bridge session: Play after Pause resumes from the last tick received (raw and live streams) instead of starting
  again from the beginning.

## [1.95.322]
### Fixed
- JSON v1 and v2 case writers: a boundary or OLTC record that names an out-of-service branch (which the writers leave
  out) is refused before anything is written, naming the record, instead of producing a case the loader refuses.

## [1.95.323]
### Changed
- Cross-validation workflow: pandapower, NumPy and SciPy are pinned to the versions the published cross-validation
  evidence was measured on (pandapower 3.5.5, NumPy 2.4.6, SciPy 1.18.1), and pip is no longer upgraded to latest.
### Fixed
- `tools/fetch-cases.sh`: a failed download (an HTTP error) is reported and never stored as a case file; each file is
  written to a temporary name and moved into place only on success.

## [1.95.324]
### Fixed
- Unscented and ensemble Kalman filters: `Step` refuses a window with more measurements than the per-window cap, as the
  streaming engine already did, so a direct caller can no longer form a multi-gigabyte innovation matrix.
### Changed
- Code documentation follows the code: the dense Newton engine's trigonometry is DetMath (fdlibm), the exact LAV step
  is accepted by a proximal ratio test (not halved), both innovation-form filters check their own size limits, the
  subtransient initialisation uses the GENROU saturation increment, and the dense / sparse crossover note no longer
  says the WLS estimator switches on size.

## [1.95.325]
### Fixed
- Protection placement: a recloser whose target branch name is shared by several branches is refused (operating it
  would have opened all of them).
- CLI `crossval`: a JSON v2 case manifest given as the case is solved as a case, instead of being passed to the
  sidecar as a solve export.

## [1.95.326]
### Fixed
- CIM export: each transformer end terminal names its PowerTransformer as its conducting equipment, as CGMES requires,
  so other tools see the transformer connected.

## [1.95.327]
### Fixed
- Branch admittance stamp: a negative or NaN tap ratio is refused (0 still means a line).
- ACOPF: fewer than one loss iteration, or a loss tolerance that is not a finite number above 0, is refused.
- PTDF: a bus that is not in the model is refused instead of returning an all-zero column.
- Frequency / RoCoF filter: non-finite or non-positive starting values, sigmas and measurements are refused.
- Measurement sets: a declared base MVA that is not a finite number above 0 is refused.
- First-order lag block: a negative or NaN time constant, or a non-finite gain, is refused.
- IEEE 519 limits: a negative or NaN nominal voltage is refused.
- Sequence fault study: a fault impedance that is not finite or has a negative resistance is refused.
- Transformer connection model: a turns ratio that is not a finite number above 0 is refused instead of replaced by 1.

## [1.95.328]
### Fixed
- MATPOWER import: a generator row with QMIN above QMAX or PMIN above PMAX is refused, and a matrix entry that is not a
  number is reported with its matrix, row and column.
- Harmonic source sets: a base MVA that is not a finite number above 0, a repeated order in a spectrum and a negative
  magnitude are refused.
- DNP3: a data-link header whose LENGTH octet is below 5 is refused.

## [1.95.329]
### Fixed
- Analysis cycle: the cycle options (window, workers, shards, hot-branch loading) and the cost-model smoothing factor
  and measured costs are checked, NaN included; `cycle --hot-loading` below 0 is refused.
- Fleet specification: a negative unit count or a non-finite or negative budget, time constant, deadband or stagger is
  refused.
- C37.118: a PHUNIT channel type other than 0 (voltage) or 1 (current) is refused instead of read as a voltage.
- MATPOWER DC lines: a NaN, a fractional bus id or a negative status is refused.
- Presolved replay: a telemetry row whose key is missing or not a whole number is refused instead of skipped.

## [1.95.330]
### Fixed
- CLI `stability --export` and `modal --export`: a following flag is no longer taken as the file name.
- IEC 60870-5-104 counters: the BCR carry bit (the counter wrapped) marks the point overflowed, and the adjusted bit
  marks it substituted, instead of both being dropped.
- Replay validation: a recorded trace whose times go backwards or repeat is refused, naming the first such sample.

## [1.95.331]
### Fixed
- CLI: a negative `--top`, `--sample`, `--frames` or `--export-every`, a `--candidates` below 1, a negative
  `transient --trip` and a `--trip-window` that is not above 0 are refused (exit 2) instead of accepted.

## [1.95.332]
### Fixed
- Sparse matrix builders: an entry outside the matrix is refused when it is added.
- Sparse LU solve: a right-hand side of the wrong length is refused.
- Harmonic sequence: an order below 1 is refused instead of being classed as zero sequence.
- Replay validation: an integrator step longer than 2 s (which rounded the trace upsampling factor to 0) is refused.
- Live simulation trip: the value keeps its sign (losing a unit that absorbs power is a surplus, not a deficit), and a
  non-finite value is refused.
- GDA case writer: out-of-service branches are left out and noted, instead of being written as in service.

## [1.95.333]
### Fixed
- MATPOWER import: an apostrophe used as MATLAB's transpose no longer opens a string, so a comment after it is stripped.
- MATPOWER export: a model name that is a MATLAB keyword gets the `case_` prefix, and the function name stays within
  MATLAB's 63-character limit.
- CLI `trip`: a single `.json` argument is read as a JSON v2 case manifest, not as a generator name.
- Scenario risk curve: an axis with more than one device is refused (the curve compares one device with none) instead
  of silently using the first.

## [1.95.334]
### Fixed
- Messages that carry numbers (the slack reactive-limit note, the dispatcher balance notes, the MATPOWER assumed
  frequency note and the evidence avoided-cost summary) are formatted in the invariant culture, so their text does not
  depend on the machine's culture when Core is used as a library.
- CIM import: XML documents are read with DTD processing prohibited (CGMES documents carry none), so a document that
  declares one is refused rather than expanded.
### Security
- FDIA screen: a protected anchor with a non-finite value or an unusable sigma is refused instead of silently giving no
  verdict.

## [1.95.335]
### Fixed
- Audit records: an outcome containing a Unicode line or paragraph separator is refused, like one with a newline.
- Culture-independent text: the RTU protocol list's port column, the GDA generation-join note, the UKPN Parquet row
  count note and the quarantine store's partition and file names are formatted in the invariant culture.

## [1.95.336]
### Fixed
- Analysis cycle budget: a non-finite or negative recorded spend is refused (a NaN used to make the lock-free add spin
  for ever).
- Eigenvalue solver: a matrix with a NaN or infinite entry is refused up front, naming the entry, instead of running the
  QR iteration to its cap first.
- GPU contexts: the double-checked initialisation flag is volatile.
- CLI `crossval`: the Python sidecar is stopped after 2 hours instead of being waited on for ever.

## [1.95.337]
### Fixed
- IEC 60909 zero-sequence network: a transformer whose zero-sequence path has zero impedance is refused, naming it,
  instead of stamping an infinite admittance.
- 2 x 2 complex inverse: a matrix with a NaN entry is reported singular instead of returning a NaN inverse.
- Frequency response services: a delivery curve that is out of order (knee outside the deadband-to-full range, full
  delivery not beyond the deadband, a knee fraction outside 0 to 1) is refused when the run is set up.

## [1.95.338]
### Fixed
- Harmonic indices: a fundamental-voltage array of the wrong length, or with an entry that is not a finite number above
  0, is refused instead of being replaced by 1.0 pu for that bus.
- Feasible estimator constraints: a fraction-to-boundary outside (0, 1] or an audit tolerance that is not a finite
  number above 0 is refused.
- Phase-angle feed: a set with no observations and no target instant is refused instead of being dated 1970-01-01.
- Classical initialisation: a transient reactance that is not a finite number above 0 is refused.

## [1.95.339]
### Fixed
- Coupled harmonic power flow: a non-finite voltage is refused instead of reading as "converged" on the first pass.
- Three-phase sweep: a tolerance that is not a finite number above 0, or a cap below one iteration, is refused.
- DC-OPF (connected network): a cost vector whose length differs from the generator count is refused, as on the island
  path.

## [1.95.340]
### Fixed
- IEC 60909 decay factors: a NaN break time is refused instead of silently using the 0.25 s value.
- Estimate export reader: a measurement kind written as a number is refused (the export writes names).
- Frequency export: a response with no samples is refused instead of being written as an empty-looking document.
- IEC 104 encoder: send and receive sequence numbers outside 0 to 32767 are refused instead of being masked.

## [1.95.341]
### Fixed
- JSON schema validation: a pattern that runs past its match time limit is reported as a validation error at its
  pointer instead of escaping as an exception.
- WLS estimator: a warm-start state with a voltage that is not a finite number above 0, or a non-finite angle, is
  refused, naming the bus.
- RMS transient stability: network stages that are not finite times in time order are refused.

## [1.95.342]
### Fixed
- Three-phase EMT engine: a time step that is not a finite number above 0, a non-finite or negative duration and a
  probe bus outside the network are refused.

## [1.95.343]
### Fixed
- CLI: a missing assembly (a broken install) is no longer reported as the user's missing-file input error with exit 1;
  it is treated as the defect it is.

## [1.95.344]
### Fixed
- Linear PMU state estimator: an unobservable result (singular gain) carries NaN voltages and angles instead of zeros,
  which read as a blackout state to a caller that ignored the not-converged flag.

## [1.95.345]
### Fixed
- Asset-aware frequency integrator: the network state needs both the voltage and the loading supplier, or neither; one
  alone (which fabricated the other as 1.0 pu or 0.0 loading) is refused.

## [1.95.346]
### Changed
- Internal tidying of two recent changes (local names in the `trip` generator match; a null-safe event raise in the
  bridge client). No behaviour change.

## [1.95.347]
### Changed
- CI: every job has a time limit (test legs 180 minutes, the others 10 to 60), instead of GitHub's 6-hour default.
- Container build context: the local reference-document inbox is excluded.
### Fixed
- `tools/gen_bundled_manifest.py`: a missing data folder stops the script instead of being left out of the manifest.

## [1.95.348]
### Fixed
- RTU crosswalk: a binding of a binary status (a breaker state) or an integrated counter (an energy total) as an
  electrical measurement is refused, as step positions and bitstrings already were.

## [1.95.349]
### Fixed
- IEC 61850 GOOSE framing: the simulation bit (Reserved1 bit 15) is read and written, so a test frame is
  distinguishable from a live one.

## [1.95.350]
### Fixed
- Web clock text: a non-finite simulation hour reads "--:--" on every CPU instead of a CPU-dependent value.
- Tests: the clock test covers NaN and infinite hours.

## [1.95.351]
### Fixed
- Frequency assessment summaries, distributed-inertia event placement rules and speed-domain coupling reasons format
  their numbers the same way on every machine locale (a comma-decimal machine no longer prints "0,125 Hz/s").
- Tests: a frequency assessment summary is the same under a comma-decimal culture.

## [1.95.352]
### Fixed
- `bench`: each engine's table rows, CSV lines and failure list are written in case order once that engine's sweep
  finishes, so two runs of the same matrix give the same report and CSV (progress lines still appear as cases finish).
- `conformance`, `bench` and `gda --all-gsps`: cases of the same size, and GSP groups, are ordered by ordinal name, so
  the order does not depend on the machine's ICU version.

## [1.95.353]
### Fixed
- `stress`: the maximum loadability is printed and written to the CSV with up to four decimals, so a fine `--step`
  (such as 0.005) shows the factor the walk found; with a step of 0.01 or coarser the text is unchanged.

## [1.95.354]
### Fixed
- The sparse LU and the least-squares QR refuse a matrix with an infinite entry as corrupt input, as they already
  refused NaN, instead of reporting it as singular or rank-deficient. A power-flow step whose Jacobian holds an
  infinite entry still ends the run as not converged.
- Tests: infinite entries in the sparse LU and the QR.

## [1.95.355]
### Fixed
- C37.118 decoding: a frame whose CRC is valid but whose SYNC byte is wrong is reported as malformed instead of
  throwing, and the single-PMU decode refuses a frame longer than its configuration describes (it used to decode the
  first block and drop the rest).
- DNP3 absolute time: a millisecond count past the year 9999 decodes as no time instead of throwing.
- Tests: SYNC byte, overlong frame and DNP3 time range.

## [1.95.356]
### Fixed
- ILU(0) preconditioner: a matrix row with no stored diagonal is refused with a message naming the row, instead of an
  index-out-of-range crash.
- Sparse LU on a prepared matrix (the state estimator's gain path) checks its column pointers, row indices and column
  ordering, so a malformed input is reported as such rather than as a singular matrix.
- Tests: missing ILU diagonal and malformed prepared sparse LU inputs.

## [1.95.357]
### Fixed
- In-memory audit log: an entry with no summary or an undefined provenance is refused when appended (it used to fail
  only when the log was saved), and entry times are stored in UTC, as the on-disk log stores them.
- Incidents: the detail must be valid JSON, and an incident policy refuses a negative burst threshold.
- Tests: audit log append checks, incident detail and policy thresholds.

## [1.95.358]
### Fixed
- CLI count flags below their documented minimum are refused (exit 2) instead of being raised to it without a word:
  `bench --runs`, `bench --perf --runs`, `chain --steps`, `cycle --window-ms/--workers/--cycles/--shards`,
  `transient --log-every` and `interpose --window-ms`. A negative `--parallel` on `bench`, `stress` and
  `nminus1-sweep` is refused (0 still means all cores).
- `licence`: `status` and `path` refuse further arguments, and `import` refuses a second file.
- Tests: count flags below their minimum and extra licence arguments exit 2.

## [1.95.359]
### Fixed
- `interpose`: a port outside 0 to 65535 on `--port` or `--listen` is a usage error (exit 2) instead of a transport
  failure, and an IPv6 address can be given in brackets (`--listen [::1]:2404`).
- Tests: interpose endpoints that cannot be bound as given exit 2.

## [1.95.360]
### Fixed
- `transient`: a second positional case, a positional case together with `--cases`, and a `--cases` list naming no
  case are refused (exit 2); they used to be ignored.
- Bare `case9` honours `--bfs`, as every other bare case does.
- Tests: transient case arguments.

## [1.95.361]
### Fixed
- Licence files: a key wrapped across several lines is read whole (its lines are joined up to the next blank or
  comment line), as it already was from `GRIDSIM_LICENCE`; only the first line used to be kept.
- Harmonic frequency sweep: the grid-size check counts points exactly as the grid is built, so a sweep cannot pass the
  limit and then be built with one point more.
- Tests: wrapped licence keys.

## [1.95.362]
### Changed
- Watch list, loss allocation by zone, asset tornado, frequency sensitivity, carbon tracing, replay RoCoF median and
  the CLI critical-clearing-time tables sort with GridSim's frozen copy of the .NET 10 sort, so items with equal keys
  keep one order on every runtime. Output is unchanged.

## [1.95.363]
### Fixed
- Folder and case-file listings (model catalogues, the bench and conformance case archive, GDA input choice) break a
  tie between names that differ only in letter case by exact name, so their order is fixed on a case-sensitive file
  system too. Nothing changes on Windows.

## [1.95.364]
### Fixed
- Tests: the harmonic source-set and restriction tests write to a folder of their own per run, so concurrent test
  processes no longer read each other's files. No assertion changed.

## [1.95.365]
### Fixed
- Run headers: installed RAM is reported in decimal GB on Linux too (it was GiB under a "GB" label), and the macOS CPU
  name probe really is bounded to 2 s (a hung `sysctl` is killed instead of holding the report).
- `bench --cases` with a list that names no case is refused before any report file is created.
- `trip` prints the nadir time from the named design-event instant, and the classical export records the kick it
  applied from the same named constant (both values are unchanged).

## [1.95.366]
### Changed
- Code comments corrected where they disagreed with the code (complex tanh limit, ILU(0) pivot guard edge, a cited
  test name, the bridge model block's frequency, the VoLL provenance rule, the synthetic GB zone demand). No behaviour
  change.

## [1.95.367]
### Changed
- Code comments and the technical manual corrected where they disagreed with the code: the modal-analysis reference
  machine rule, the GDA writer's embedded-generator output, what the experimental flow trace's acyclic flag means,
  the sparse LU buffer lengths (technical ch. 4) and the CIM importer source map (technical ch. 32). No behaviour
  change.

## [1.95.368]
### Changed
- Technical manual corrected to the code: the Francis QR iteration limits and shift schedule (ch. 4), the chi-square
  degrees of freedom with prior-solved rows, the audit outcome's chi2 values and extra fields, and the HTI pass
  numbering (ch. 6), and the full `estimate --method` list (ch. 18). No behaviour change.

## [1.95.369]
### Changed
- Manual ch. 26: the audit outcome's chi2 values are `PASS`, `FAIL`, `not-run` and `no-verdict`, as the code writes them.

## [1.95.370]
### Fixed
- CIM import: the bridged model goes through the same structural check as JSON and MATPOWER cases, so a line whose
  two terminals are on one node, or a zero-impedance line, is refused at import instead of reaching a solver.
- Web host: when the resident estimator fails, a failed audit delivery or audit anchor write is reported on stderr, as
  it already was when the estimator completed.
- Tests: CIM self-loop and zero-impedance lines are refused.

## [1.95.371]
### Fixed
- Web footer: an expired, not-yet-valid or other-major licence key shows the free bus cap that is actually enforced,
  with EXPIRED or NOT YET VALID, instead of the key's own limit (the tooltip already did).
- Tests: the shared free-tier rule.

## [1.95.372]
### Fixed
- Bridge topology messages and solve-export imports (the presolved replay) go through the same structural check as
  every other loader, so duplicate bus ids, a branch or generator on a missing bus, a self-loop or a zero-impedance
  branch are refused by name instead of failing later inside a view.
- Tests: invalid bridge topologies are refused.

## [1.95.373]
### Fixed
- `crossval` (pandapower sidecar): a GridSim bus with no pandapower result, or a difference that is not finite, now
  gives no verdict (exit 4) instead of being skipped, so a partial or NaN comparison can no longer read as agreeing.

## [1.95.374]
### Fixed
- Multi-phase models: a branch whose end is not a bus of the model is refused when the model is built, by name, as a
  load or capacitor on an unknown bus already was. The three-phase harmonic study used to treat the unknown end as
  ground.
- Tests: multi-phase branch endpoints.

## [1.95.375]
### Added
- `lake_conformance.py --gate`: exit 1 when any audited stored period is not physically admissible, so the tool can be
  scripted as a gate. Without the flag it still reports only (exit 0).

## [1.95.376]
### Changed
- Manuals corrected to the code: the sparse threshold (technical ch. 29), domain speed at the model's own frequency
  (ch. 5), the PLL bandwidth default (ch. 13), the EMT three-phase source frequency default (ch. 21), the bundled
  step-up 4-node cases (ch. 18), `afe-g5` among the spectra with triplens (manual ch. 17), and `GRIDSIM_PYTHON` and
  `VIRTUAL_ENV` in the environment variable table (manual ch. 3).

## [1.95.377]
### Changed
- Manuals corrected to the code: which integrators refuse grid-forming, the device full-delivery constant, the removed
  demand floor and the sub-step constant, and the deterministic calls (technical ch. 5); the EMT conductance refusal
  and the deterministic AC source cosine (technical ch. 21, manual ch. 16); the classical machine base rule and the
  governor initialisation (technical ch. 24); and when the impedance-scan flags change the verdict grid (technical
  ch. 30).

## [1.95.378]
### Changed
- Manuals corrected to the code: the model copy method and frequency default (technical ch. 2), a cross-reference and a
  stray marker (ch. 19), the outage engine's element types and the candidate bound (ch. 23), which case format carries
  winding data (ch. 25), and what the `fault` closing line names (manual ch. 12).

## [1.95.379]
### Changed
- Technical ch. 17: `fault` exits 3 when the converter iteration does not converge and refuses a negative `--zf`, as
  `protection-check` does (the chapter said otherwise in two places).
- Technical ch. 34: the route table lists the always-mapped anonymous `/compliance` and `/health` endpoints.

## [1.95.380]
### Fixed
- State estimation: a bad-data pass that fails the observability gate no longer carries the previous pass's normalised
  residuals, criticality and chi-square verdict into its result.
- Validation sidecars use one exit-code convention: `lake_conformance.py` exits 77 when a package is missing (was 2)
  and `crossval_h0.py` exits 3 for an unreadable export (was 2, which Python also uses for a usage error).

## [1.95.381]
### Fixed
- v2 template libraries: two library files with the same name in different folders are told apart in error messages
  by their full path; a uniquely named file is still named by its file name.

## [1.95.382]
### Fixed
- Three-phase estimators (WLS and branch-current): a measurement on a phase other than 0, 1 or 2 is refused by name
  instead of failing with an index-out-of-range error.
- Tests: three-phase measurement phase indices.

## [1.95.383]
### Changed
- README: estimation arithmetic never reads the clock, but the forward-inference horizon does (unless `--utc-now`
  pins it), so which windows a run accepts depends on when it runs.
- Technical ch. 3 states the sweep's low-voltage division rule as the code applies it, technical ch. 18 says the
  fault study refuses a non-gy-gy transformer only on the fault path, and the pivot-floor comment names the
  thresholds that are not the shared floor.

## [1.95.384]
### Changed
- Technical ch. 15 corrected to the code: a demand-shed stage sheds its share of the running demand, `capacityMw` is
  range-checked, and the removed per-domain fleet step is no longer described.

## [1.95.385]
### Fixed
- `export` writes its scene through the same output rule as every other verb, so an input of the run, a file of a
  protected case folder or an existing folder is refused as an output.
### Changed
- CLI reference: a refusal that ends a run at once prints only its own error line, not the "no result file written"
  notice.

## [1.95.386]
### Fixed
- `transient`: a `.m` case is resolved as every MATPOWER argument is (the path as given first, then under
  `data/matpower`), so a user's own file given by a relative path is found.

## [1.95.387]
### Fixed
- `crossval` and `bench --estimate` write `run-metadata.json` into their run folders, and the storage `chain` report
  writes its `<report>.run-metadata.json`, as every other run folder and report file does.

## [1.95.388]
### Changed
- CLI reference: `--audit-log` records every run that ends after the log is opened; a run refused by the global checks
  that come first (an unknown flag, `--importLicence`, `--frequency-hz`) writes no entry.

## [1.95.389]
### Changed
- CLI: more refusal and failure messages start with `error:` (frequency file not found, the GDA root error, the
  `stability` and `modal` refusals and non-converged base cases, an unknown `compliance` subcommand), so a log search
  for `error:` finds them.

## [1.95.390]
### Changed
- Technical ch. 3: the OLTC loop takes one step per OLTC record per pass, so two records resolving to one transformer
  move it two steps.

## [1.95.391]
### Fixed
- Interposer, IEC 104: the frame gate accepts exactly one APDU (length octet 4 to 253, no trailing bytes); a buffer
  holding two APDUs, or an out-of-range length octet, is rejected and counted instead of passing with the rest lost.
- Tests: the frame-correction stage.

## [1.95.392]
### Fixed
- Web dashboard: a bridge topology with no buses shows an error instead of waiting for a topology for ever.

## [1.95.393]
### Fixed
- Web host: a raw replay range that cannot be fetched from the bridge is reported on stderr (the date picker then has
  no bounds) instead of being swallowed, and the metadata response is capped at 1 MiB.

## [1.95.394]
### Fixed
- Interposer, IEC 104: objects in an ASDU that is a negative confirmation, or that carries an "unknown" cause of
  transmission (44 to 47), are marked invalid and counted, instead of being taken as live values.
- Tests: IEC 104 cause of transmission.

## [1.95.395]
### Added
- Tests: the parallel N-1 branch screen is bit-identical to the sequential screen at several degrees of parallelism,
  and across runs.

## [1.95.396]
### Added
- Tests: the order of a grid event's steps at equal times (losses first, then recorded actions, each in catalogue
  order) is pinned.

## [1.95.397]
### Added
- Tests: IEC 104 non-finite short floats are counted and dropped, and a time-tagged value with an invalid time tag is
  kept, marked invalid and counted.

## [1.95.398]
### Added
- Tests: a GDA CSV numeric cell holding NaN or an infinity is refused by column name, and a blank cell takes the
  fallback.

## [1.95.399]
### Fixed
- Hypothesis-testing bad-data identification refuses an estimate that did not converge or is not of the given model.
- The FDIA screen (Core API) refuses an estimate whose chi-square is not a verdict (a LAV estimate), as the CLI does.
- Tests: HTI refuses another model's estimate.

## [1.95.400]
### Fixed
- Frequency-constant fitting: a trace whose times are not finite and strictly increasing is refused, and a fit that
  ends outside the physical parameter box is refused instead of being returned as the fitted value.

## [1.95.401]
### Fixed
- `gda-replay`: `--policy`, `--shave-high` and `--shave-low` without a `--bess` site are refused (exit 2), as `chain`
  refuses them, instead of being ignored.
- Tests: storage flags without a site.

## [1.95.402]
### Fixed
- `replay`: `--sps` outside 0.25 to 2000 slices per second and `--seconds` below 1 are refused (exit 2); the rate used
  to be clamped without a word, and a negative duration read nothing and reported "No data."
- Tests: replay flag ranges.

## [1.95.403]
### Fixed
- GDA builds: an optional dataset is treated as absent only when it is not there. A permission error, an ambiguous
  file choice or a lake that holds the dataset only as Parquet now stops the build with its own message instead of
  being skipped without a word.

## [1.95.404]
### Added
- `GET /api/realtime` carries a top-level `ok`: true only when there is a latest estimate and it converged, so a
  consumer need not read `latest.converged`. The answer is still 200 when the estimator is enabled.

## [1.95.405]
### Fixed
- Web projection: a solved state whose voltage, angle or injection arrays do not cover every bus is refused instead of
  being padded with 1.0 pu and 0 and graded by the status lamp.

## [1.95.406]
### Fixed
- Solved generator output uses the same load rule as the solver, so a constant-power ZIP load is never scaled by its
  multiplier in the report. Every case the JSON loader accepts gives the same numbers as before.

## [1.95.407]
### Changed
- Documentation and code comments corrected to match current behaviour: the single GPU batch synchronisation, the DC
  island reference rule, the FDIA protected-anchor test, how each dynamics kernel reads a fault window, the
  offline-derived IEEE 4-node line code, the Y-D validation counts, the undercurrent relay's in-service gate,
  case-insensitive CIM booleans, the web footer version form, the windowed-RoCoF inertia correction, the assumed H=0
  un-formable cell fields, the workflow trigger comments, the glossary's case9 figures and the GB documents submodule
  size. Four top-level documents are now plain ASCII.

## [1.95.408]
### Changed
- The solve-export documentation says the JSON is byte-for-byte reproducible apart from its `meta` block, which records
  the run's time, duration and machine.
- Code comments corrected: the dense complex solve has no production caller and the transformer connection model is
  not used by the sweep; the frequency export names the swing's inertia floor constant (same value).

## [1.95.409]
### Fixed
- Bridge client: the cap on queries in flight is reserved atomically, so concurrent callers can no longer exceed it.

## [1.95.410]
### Fixed
- Low-latency GC scopes are counted, so overlapping scopes ended in any order leave the process in its original GC
  mode, and a scope disposed twice no longer unbalances it.
- Tests: overlapping GC scopes.

## [1.95.411]
### Changed
- The distributed inertia model documents that one instance serves one caller at a time: its last spatial result
  describes the most recent call on that instance.

## [1.95.412]
### Changed
- The CPU sparse LU solver documents that an instance is not thread-safe; parallel work gives each worker its own.

## [1.95.413]
### Fixed
- Web dashboard: the presence and live-tab timers are disposed when their loops end, and a session teardown that fails
  on a case or solver change is reported on stderr.

## [1.95.414]
### Added
- Tests: MATPOWER rows short of the standard columns, and a NaN in a row, are refused by matrix, row and column.

## [1.95.415]
### Fixed
- BiCGSTAB solvers (real, complex and ROCm) return x = 0 only for an exactly zero right-hand side; a non-zero one
  whose norm underflows to 0 is reported as a failure instead of being "solved" by x = 0.
- The GPU and ROCm sparse solvers refuse a tolerance that is not positive and finite, as the CPU solvers do.

## [1.95.416]
### Changed
- Feasible state estimation: the generator reactive-limit constraints are listed in the order their buses first appear
  in the generator list, kept explicitly rather than taken from a dictionary's enumeration. The order, and every
  number, is unchanged.

## [1.95.417]
### Fixed
- Three-phase feeders: an `oy-od` transformer with the same rated voltage on both sides is refused by name, as `y-d`
  and `d-gy` are (it was given the step-down shift).
- Three-phase harmonics: a balanced source whose spectrum repeats an order is refused, as the spectra file loader
  refuses it (the last entry used to win silently).

## [1.95.418]
### Fixed
- Web map: a call from the map to the server that fails after the page lost its connection no longer shows as an
  unhandled promise rejection in the browser console.
### Changed
- A SIMD kernel comment states that its NaN-bit argument holds on x86 and is not tested on arm64.

## [1.95.419]
### Fixed
- NESO phase-angle reader: an instant with no time zone is read as UTC, as everywhere else, so the week chosen no
  longer depends on the machine's time zone, and a local instant is converted before the sample lookup.
- Live C37.118 receiver: its deadlines are measured on the monotonic clock (a wall-clock correction no longer shortens
  or stretches them), and its timeout message prints the same on every locale.

## [1.95.420]
### Changed
- README and technical ch. 8 state the live analysis cycle as the one scheduling exception to run-to-run determinism:
  which Low-priority jobs run in a cycle depends on the machine, while each job's result is deterministic. Chapter 8
  also lists the new parallel-screen bit-identity test.

## [1.95.421]
### Fixed
- GDA builds: a node code published at more than one voltage is named in a `WARNING node-kv` note (the higher voltage
  is still used, as before).

## [1.95.422]
### Fixed
- MATPOWER parser: a matrix whose closing `]` is missing before the next matrix starts is refused, instead of being
  read together with the next matrix.
- Tests: the unclosed-matrix refusal.

## [1.95.423]
### Fixed
- Case editor: a save is refused when any file of the case changed on disk since it was opened, not only an edited
  one, so the saved case is always the combination that was checked.

## [1.95.424]
### Added
- Tests: two separate CLI runs write the same solve export, byte for byte apart from the meta block, on case9 and
  case118.

## [1.95.425]
### Added
- Technical ch. 36, Known limitations and design decisions: behaviour kept on purpose after the October 2026 review,
  with what happens, when it shows, why it stays and where it lives. This release adds its numerics section.
### Fixed
- GPU: the OpenCL FP64 check now computes (1 + 2^-40) - 1 from host operands and requires exactly 2^-40, so a device
  that runs double in single precision is rejected (the old 1.5 + 1.5 check was exact in single precision too).
- ROCm on Windows: the HIP SDK 6.x runtime name `amdhip64_6.dll` is tried.
- The real-expanded complex solver returns the empty solution for an empty system, as the other solvers do, instead
  of reporting it singular.

## [1.95.426]
### Added
- Technical ch. 36: the power-flow section (island slack, sweep convergence and Q-limits, generator-less slack,
  distributed slack at limits, OLTC name matching, solver constants, engine efficiency).
### Fixed
- Experimental carbon tracing: a ZIP load's intensity uses the load it draws at its solved voltage, the same demand
  the flow trace allocated to it. Constant-power cases are unchanged.
### Changed
- The physics audit's summary states which laws are re-derived independently and which hold by construction.

## [1.95.427]
### Added
- Technical ch. 36: the state-estimation section (constraint weighting, Kalman filter numerics, distrusted sources,
  FDIA screen bounds, slack references, the chi-square approximation, robust-method verdicts, priors and resolvers).

## [1.95.428]
### Added
- Technical ch. 36: the dynamics section (distributed coupling and propagation, Nyquist sampling, small-signal GFM
  RoCoF, converter iteration and initial state, machine initialisation conventions, inertia routes, event timing,
  library edges, measured traces).
### Changed
- A distributed-swing comment no longer claims the delayed ties cancel.

## [1.95.429]
### Added
- Technical ch. 36: the distribution-feeder and harmonics section.
- Tests: a balanced three-phase harmonic source refuses a repeated order.

## [1.95.430]
### Added
- Technical ch. 36: the short-circuit and protection section (IEC 60909 edges, zero-sequence scope, breaker checks,
  operating-point fault studies, relay models, contingency netting, DC model couplers, OLTC supersession, asset and
  storage model edges, alarm matching, external benchmarks).

## [1.95.431]
### Added
- Technical ch. 36: the file-formats, CIM and JSON section.
- Tests: the bridge message-type peek.
### Fixed
- Bridge messages with two `type` keys are refused as bad messages (the type peek and the deserialiser read them
  differently).
- v2 cases: the inside-the-case-folder check compares paths by the operating system's rule (exact on Linux), as the
  case editor does.

## [1.95.432]
### Added
- Technical ch. 36: the command-line section (per-verb solve options, GPU routing, loader exits, lenient flag gates,
  exit codes, WSCC-9 data by name, usage on stdout).
### Changed
- CLI reference: `crossval` reports a crashed sidecar as a comparison failure (exit 3), because the sidecar's own exit
  1 means out of tolerance. Troubleshooting: `convert` exits 0 for a bit-identical round trip of a case that does not
  converge.

## [1.95.433]
### Added
- Technical ch. 36: the web host, sessions and sign-on section.
### Fixed
- `/sso/logout` answers 404 when sign-on is off, as `/sso/bridge` and `/sso/land` do.
- The analytics page view calls its function directly instead of through `eval`, so a Content-Security-Policy need
  not allow `unsafe-eval`.
### Changed
- Deployment guide: the host serves security.txt from its settings only, and checks its expiry at start-up.

## [1.95.434]
### Added
- Technical ch. 36: the desktop app, GDA data builds, and interposer, telemetry and synchrophasor sections.

## [1.95.435]
### Added
- Technical ch. 36: the scheduling and live simulation, and audit trail, incidents and crash logs sections.
### Changed
- Assurance documents: cross-references to the CAF mapping corrected.
- LiveSim: the class summary states what drives the live frequency; an unused field removed. No output changes.

## [1.95.436]
### Added
- Technical ch. 36: the determinism checks and web session section.
### Fixed
- Web: a bridge session's per-tick values are written and read under the session lock, so a tick time is never read
  half-written.
### Changed
- Tests: a stricter check that every call of the row kernel in the source names the non-fused arm.

## [1.95.437]
### Added
- Technical ch. 36: the test suite section.
### Changed
- Test guide: the RAM gate's memory figure is the runtime's available memory, which a container limit lowers.
- Tests: new tests of the audit forwarding path and of the eigenvalue refusal on an infinite entry (with a time limit);
  the phase-angle reader tests run without parallel neighbours; exclusive large solves queue on their own lock; four
  test comments corrected. No assertion changed.

## [1.95.438]
### Added
- Technical ch. 36: the build, release and deployment section.
### Fixed
- The crossval workflow runs and judges every case and reports all failures, instead of stopping at the first.
### Changed
- MATPOWER case-file licensing stated as the MATPOWER licence file gives it: the case files are not covered by its
  BSD-3-Clause code licence (data README, third-party notices; bundled-data manifest regenerated).
- README: the assurance section lists every assurance document.
- The Dependabot auto-merge workflow's header describes its disabled state, and it passes the pull request URL through
  the environment.

## [1.95.439]
### Fixed
- The Release build compiles cleanly again: an analyser finding in the MATPOWER statement check, a test using the old
  frequency-point namespace, the storage CLI's run-metadata helper not linked into the tests, and a test collection
  name the analyser refuses.

## [1.95.440]
### Fixed
- Two-by-two complex eigenvalues keep the small root when the two differ by many orders of magnitude.
- Householder QR solves a system whose column sums of squares underflow, instead of skipping the reflection.
- The real-expanded complex solver reports a non-finite solution as a failed solve; the dense complex LU refuses an
  infinite matrix entry instead of returning zeros.
### Changed
- The chi-square threshold is exact at one and two degrees of freedom (Wilson-Hilferty from three up), so a bad-data
  verdict at one or two degrees of freedom can change.
- Tests: the projection-statistics leverage check expects the exact quantile (2.7162) instead of the approximation's.

## [1.95.441]
### Fixed
- The three-phase sweep reports a constant-power or constant-current load on a phase at 0 V as not converged, instead
  of drawing nothing; a dead bus's voltage unbalance factor is NaN, not 0 %.
- Distribution pseudo-measurements go only on the phases a bus has.
- Grid-forming impedance refuses a non-positive virtual reactance and a NaN power-loop bandwidth, and a singular
  impedance is an error instead of a zero admittance.
- EMT simulation stops with an error at the first non-finite probe value.

## [1.95.442]
### Fixed
- Inverters and protection devices refuse a detection latency longer than their measurement history holds, instead of
  shortening it.
- A battery converter efficiency outside 0.5 to 1 is refused instead of clamped.
- IEC 60909 corrections leave an element on a bus with no base kV uncorrected and list it, instead of using the
  low-voltage factor.
- CIM export writes LF line ends on every operating system.

## [1.95.443]
### Fixed
- `--gpu=rocm`, `--gpu=opencl` and `--gpu=cuda` exit 2 when no device of that vendor is usable; plain `--gpu` still
  falls back to the CPU.
- Licence base64url decoding accepts only the canonical spelling.
- Environment secrets are found under the ASP.NET form (`Bridge__API_KEY`) as well as the older one.
- Web: the exception handler runs before every other middleware; `/health` answers `starting` while the resident
  estimator has no summary yet.

## [1.95.444]
### Fixed
- GDA CSV and Parquet reading refuses a column whose name appears twice and a row whose extra cells hold values,
  instead of reading the first or a shifted value.
- NPg notes a Parquet read that fell back to CSV.
- Busbar tie words match at the start of a word only; `sept` reads as September in register names.
- The telemetry protocol registry returns a fresh protocol instance per use.
### Removed
- The unused `CsvRow.Int`.

## [1.95.445]
### Fixed
- The determinism guard's two remaining platform-maths calls (the hydro governor's refusal message, the reconnect
  back-off delay) go through the deterministic maths routines.
- `chain --prices` with no value names the two accepted forms again.
- Tests: the GPU vendor refusal test's messages compile; a CIM network with no generator and an unreachable island is
  refused naming the island (the bridge now validates); the instant-gate hydro test runs long enough (1,500 s, 35 time
  constants) to settle to 9 decimal places, assertions unchanged.

## [1.95.446]
### Changed
- Tests that cannot run on a machine (missing optional data, the wrong operating system, CGMES conformity models not
  configured) are reported as skipped instead of passed; committed data a test needs is asserted present.
- Tests: misleading names that promised machine precision now state the bound they check; the AC-OPF test solves its
  dispatch; a new independent Y-bus balance test; dense/sparse parity also compares branch losses, rating, name and
  solved bus types; the layout timing test runs without parallel neighbours.
- CI: each reduced SIMD leg fails unless its switch took effect; the package workflow waits for the CI matrix when
  started by hand or by a published release.

## [1.95.447]
### Fixed
- The built-in 2019 event's static response service (a step at the trigger, no knee) is accepted again; evidence,
  replay-event, scenario, fleet and h0-study runs on it no longer crash.
- Real-network impedance scans below the fundamental work again (negative frequency ratios are allowed on that path
  only; harmonic studies still refuse a non-positive order).
- The sparse LU's non-finite-factor fallback no longer throws.
- `export` may rewrite its own earlier scene again.
- `fault`: when the converter current iteration does not converge, the relays are reported not assessed and the run
  exits 3, instead of crashing.
- Tests: updated where a deliberate earlier change altered a message, an exception type or a refusal (each with its
  reason in the test); the WLS bit pins re-captured after the canonical sparse step (case14 J 80.01052231478428 ->
  80.01052231478407, case57 407.14427112449533 -> 407.1442711244965, case118 877.8012390625249 -> 877.8012390625028;
  Ring B objective 72.382635648990458 -> 72.382635648986437).

## [1.95.448]
### Fixed
- `protection-check --fault-bus`: when the converter current iteration does not converge, the study is printed, the
  relays are reported not assessed and the run exits 3, as `fault` does (it reported the study as failed, exit 1).
- The skin-effect factor uses the magnitude of the harmonic order, so the dq impedance mirror Z(-f) = conj(Z(f)) holds
  with skin effect on. Harmonic studies (positive orders) are bit-identical.
### Changed
- Tests: the response-service delivery-curve rule is pinned by its own tests; new tests for the two fixes above; a
  determinism test renamed to say what it checks.

## [1.95.449]
### Fixed
- Distributed slack stops at once when every participating unit is at a limit and the shortfall points the same way,
  instead of running every pass and reporting an inflated participation.
- A slack bus with no generator holds its stored voltage on a flat start; a stored voltage at or below 0 is refused.
- Physics feasibility refuses a NaN reactive limit, a +Inf Qmin and a -Inf Qmax instead of reading them as no limit.
- A transformer steps at most once per OLTC pass (the first record in model order wins); validation warns when two
  records drive one branch and picks the branch by the OLTC loop's own rule.
- The QR solve refuses a right-hand side that is not finite or has the wrong length.
- Estimation: every generator at the slack bus is kept in the measured-injection prior; EKF and UKF covariances are
  stored exactly symmetric; a UKF posterior variance that is not positive and finite is refused; a Vm floor clamp
  counts in the step that decides convergence; prior backfill from a flat start is refused (exit 2).
- FDIA screening uses the smallest sigma for bus rows.
### Added
- The island report carries each elected island's load, schedule, imbalance and unit limits.

## [1.95.450]
### Fixed
- Frequency events, RMS stages and domain arrivals fire on the step they fall on: event times are compared with the
  clock to a millionth of a step, so an event on the step grid no longer fires one step late.
- Converter inverter ratings that are zero, negative or not finite are refused; frequency traces whose time stamps go
  backwards are refused.
- Three-phase sweep branch currents are reported towards the To bus whatever the direction a line is written in; line
  codes whose series matrices are not symmetric are refused.
- The RMS event, trip and clearing verdicts print a warning when converter current iterations failed.
### Added
- `GovernedMultiMachine` takes an optional machine-to-system base ratio, so each governor runs on its own base.

## [1.95.451]
### Fixed
- IEC 60909 breaker checks compare the rated voltage with the highest system voltage Um; an optional asymmetrical
  breaking rating (a sixth `--breaker` value) is checked against the DC-inclusive duty; double-line-to-earth duties are
  judged at the largest line current; a fault source with an MBase that is not a positive finite number is refused.
- Protection refuses a bus missing from the result instead of reading 1.0 pu.
- Battery packs more than 50 % from the requested energy are refused; a state-of-charge bucket delivers only the
  energy left on the step that reaches its window limit; an end-energy request outside the window is refused.
- Distribution factors refuse an in-service zero-reactance branch inside a mesh.
- Asset envelopes take the median of an even count as the mean of the middle pair.
- CIM: `urn:uuid` ids are matched case-insensitively; a machine with no rating takes the system base; a moved SVC
  starting susceptance is noted.
### Added
- Contingency screens list base-case violations that an outage makes materially worse (more than 1 percentage point of
  loading or 0.005 pu of voltage); an opt-in counts them against the outage.

## [1.95.452]
### Changed
- The command line refuses input it used to ignore: a value flag given twice, a single-dash word (`-bus`), extra words
  after the case run's case, a second case for single-case verbs, `--trip-at` or `--trip-window` without a trip, and
  `--enableExport` with a spaced path; usage banners for a missing argument go to stderr.
- The WSCC-9 machine data is chosen by the case argument (`case9`, `ieee9`) only.
- Web: `/ws/sim` error frames carry no server paths; a speed or fps outside 0.1 to 60 is refused; `/geo` serves GeoJSON
  and JSON only; the sign-in cookie is always Secure when sign-on is on.

## [1.95.453]
### Fixed
- C37.118: DATA frames from another stream are refused (counted as malformed and named); CONFIG frames with protocol
  version 0 are refused; a CONFIG-1 never replaces a CONFIG-2.
- Modbus: an exception response closes its transaction; a reused transaction id with a different request is refused;
  session state resets when a client drops. IEC 104: an unconfirmed STARTDT or TESTFR after t1, or an out-of-sequence
  I-frame, closes the link.
- Quarantine store: links are resolved for the lake firewall; the marker is written when the store opens; a window
  with no time is refused.
- Replay validation refuses non-uniform trajectories for windowed RoCoF and an event with zero recorded RoCoF.
- Phase-angle readers refuse duplicate time stamps and a week present in both year folders.
### Added
- `--live-udp-source <ip>` accepts synchrophasor datagrams from one sender only. UKPN aggregate notes state their MW.

## [1.95.454]
### Fixed
- LiveSim frames say whether taps were regulated, so the sparse and sweep engines no longer report every frame as
  taps not settled.
- Fleet units arm on the step their stagger lands on (compared to a millionth of a step).
- GDA numbers follow one rule: thousands separators only in place, a percent sign dropped; other locale or accounting
  forms are refused naming the column.
- The audit directory hash refuses ambiguous file names; crash logs on Linux and macOS are owner-only.
- Crossval passes only when voltages agree and both tools end with the same reactive-limit outcome.
### Changed
- CI: new 256-bit vector-tier legs; release: the signing secrets are passed by name, `signtool` signs from the
  certificate store, and a repository variable can make signing required.

## [1.95.455]
### Fixed
- Release and publish workflows: an unsigned release stays the default (with a warning) while the certificate is
  self-signed; `GRIDSIM_ALLOW_UNSIGNED_RELEASE = false` makes signing required.
- The frequency export's recurrence states the event rule the engines use.
- Tests: the pre-kernel reference loops follow the event and arming rules above; the nminus1 case14 output re-captured
  with its two worsened base-case lines; build fixes in two new test files.

## [1.95.456]
### Fixed
- Solve imports refuse a document with no `solve` block or no `converged` flag instead of reading it as a converged
  solve; a document with no status that did not converge reads as not converged.
- Impedance stability prints a warning when the Nyquist contour turns more than a quarter turn between two grid points,
  because the encirclement count may then be wrong; rerun with a larger `--ppd`.

## [1.95.457]
### Fixed
- `nminus1`, `nminus2` and `n-1-1` solve the base case and the outages on the backend chosen with `--gpu` (CPU runs are
  unchanged); with `--batched-lanes` and a GPU selected, a note says the batched branch sweep runs on the CPU.

## [1.95.458]
### Fixed
- Classical machine initialisation shares a bus's reactive power between co-located machines by reactive capability, so
  a 0 MW voltage-control unit carries its part of the MVAr; buses with one machine are unchanged.

## [1.95.459]
### Fixed
- Passive-plant tap control supersedes only the OLTC record that drives the transformer it regulates; another
  transformer with the same name keeps its own regulation.

## [1.95.460]
### Fixed
- The ROCm availability probe finds the math libraries the same way the solvers do, so an install that has only the
  versioned library names is no longer reported unavailable.

## [1.95.461]
### Fixed
- The GDA case writer keeps the voltage-dependent load, per-bus voltage limits and the remaining line and transformer
  fields a JSON v1 case carries; a case that sets none of them is written exactly as before.

## [1.95.462]
### Fixed
- An isolated bus elsewhere in the model no longer makes every operating-point fault study fail; a fault on an isolated
  bus is refused by name.

## [1.95.463]
### Changed
- Documentation: command output re-captured for power flows, contingency, EMT, frequency, event replay,
  counterfactuals, state estimation, harmonics and the frequency export, and the chapter examples that quote them
  updated.

## [1.95.464]
### Fixed
- The web host warns on stderr, naming the path and the error, when the usage log cannot be written, instead of
  switching it off silently.

## [1.95.465]
### Fixed
- GDA models whose folder names differ only in case, spacing or punctuation get distinct ids in the web case catalogue,
  so each one can be opened.

## [1.95.466]
### Fixed
- CIM export refuses a network that would define one rdf:ID twice, naming the identifier, instead of writing a document
  that cannot be read back.

## [1.95.467]
### Fixed
- A bridge pause, speed change or seek that fails is shown as a warning on the web dashboard instead of being discarded
  silently.

## [1.95.468]
### Changed
- Documentation: command output re-captured for OPF and losses, stability, audit, storage, exports, networks,
  troubleshooting, validation, licensing and start-up, and the chapter examples that quote them updated; examples
  that print the local account or home folder keep their neutral output.

## [1.95.469]
### Changed
- Documentation: worked examples and the figures quoted around them follow the current output across the user manual
  and technical reference (event replays, counterfactuals, contingency, stability, harmonics, OPF, exports).

## [1.95.470]
### Fixed
- Documentation: worked examples that depend on files written by an earlier step (audit tamper set, noise-floor,
  interposer sessions, licence limits, exports) show their successful runs again instead of a missing-file error.

## [1.95.471]
### Changed
- Documentation: export excerpts show the current file layout; the stability chapters state the frequency each critical
  clearing time is computed at (case9 at its 60 Hz); the study verbs' `--trace` and the benchmark capture builds are
  described as they are.

## [1.95.472]
### Fixed
- Documentation: the dynamic-model library worked example runs against the current library (the prepared network and
  the case frequency) and its output is re-captured (case9 bus 7 critical clearing time 554 ms at 60 Hz).

## [1.95.473]
### Changed
- Documentation: the data-model chapter's solve-export excerpt shows the current bus and branch fields, and the wire
  format chapter quotes the bridge sample frames as they stand.

## [1.95.474]
### Changed
- README: the power flow, estimation, event replay, DC-OPF, short-circuit, harmonics and EMT examples show the
  current output.

## [1.95.475]
### Changed
- Documentation: 189 more single-command examples are registered in the capture list and re-captured (short circuit,
  protection, overcurrent grading, three-phase feeders, real-time estimation, synchrophasor ingest, v2 schema), and
  the chapter examples that quote them updated; examples whose inputs come from an earlier step keep their output.

## [1.95.476]
### Fixed
- `docs/reference/MANIFEST.sha256` verifies with `sha256sum -c` on Windows, Linux and macOS: the reference files keep
  their stored bytes on every checkout, and the README entry is current.

## [1.95.477]
### Changed
- Documentation: short-circuit, protection, feeder, estimation and synchrophasor examples follow the current output;
  the IEC 60909 worked example is recomputed for case9 at 60 Hz with the meshed-network peak factor; breaker examples
  show the check against the highest system voltage; the 9 August 2019 figures in the README, the evidence note and
  the cascade-validation chapter follow the current replay (nadir 48.841 Hz at 76.0 s, RoCoF -0.171 Hz/s).

## [1.95.478]
### Changed
- Documentation: the breaker-check examples show a breaker rated for the highest system voltage passing (362 kV on the
  345 kV case9 buses, exit 0) beside the voltage and current failures; the EMT troubleshooting entry and the date-flag
  paragraph describe the current refusals.

## [2.0.0]
### Added
- Version 2.0 starts the cross-platform desktop programme: the desktop app is being ported from WPF (Windows only) to
  Avalonia (Windows, macOS and Linux), with WPF kept as the reference until the port reaches parity.
- New platform-neutral project GridSim.Ui (net10.0, Core only) for the logic the desktop apps share; the map's colour
  ramps live there, and the WPF palette uses them.
- Tests: UI-framework neutrality of GridSim.Ui and colour-ramp parity with the previous palette maths over every band
  edge, NaN and infinity.

## [2.0.1]
### Changed
- The desktop app's settings, model catalogue and geography overlay live in GridSim.Ui; settings stay in the same file.
- Tests: the settings, bundled-network and directory-scan contracts read the moved files, with equal or stronger
  assertions (the settings test also checks the old copy is gone; the scans cover GridSim.Ui).

## [2.0.2]
### Changed
- The desktop app runs its play loop through the shared simulation host in GridSim.Ui. Every frame, tick, step and
  message is unchanged; the loop now waits one interval after each frame is shown instead of firing on a fixed timer,
  so solves never overlap and a slow solve slows the pacing rather than queueing frames.
- Tests: new host-frame equality test (case9 and the GB reduced model, every field compared by bits against the
  previous loop, including a step change, a trip and replay cursor rules); the convergence, authored-bundle and bridge
  pause contracts read the host, with stronger assertions (the window no longer builds a LiveSim or calls the boundary
  analysis unguarded).

## [2.0.3]
### Added
- Cross-platform desktop app skeleton on Avalonia 11.3.22: GridSim.App and the GridSim.App.Desktop launcher open a
  "GridSim" window showing the build line (the same text as the WPF About box). The WPF app is unchanged.
- Admitted packages, pinned exactly with committed lock files: Avalonia 11.3.22 (with Fluent theme, Inter font, Skia and
  desktop backends), SkiaSharp 2.88.9, HarfBuzzSharp 8.3.1.1, CommunityToolkit.Mvvm 8.4.2, and for tests
  Avalonia.Headless.XUnit 11.3.22.
- Tests: new GridSim.App.Tests: a headless start-up test (window title and build line) and a boundary test that the app
  never reaches GridSim.Wpf, Web, Gda or Gpu.

## [2.0.4]
### Changed
- THIRD-PARTY-NOTICES.md lists every new package and the Inter font, with the Skia and ANGLE BSD-3-Clause, HarfBuzz
  "Old MIT" and SIL OFL 1.1 licence texts.
- Tests: the notices test also covers every package in the lock files of GridSim.Ui, GridSim.App and
  GridSim.App.Desktop, and requires the desktop launcher to ship the licence and notices.

## [2.0.5]
### Changed
- Dependabot watches the new projects; Avalonia, SkiaSharp and HarfBuzzSharp updates are merged together.
- Tests: the workflow contract requires every solution project that uses packages to be watched by Dependabot.

## [2.0.6]
### Security
- Build telemetry is off for every build: Avalonia.BuildServices' statistics target is replaced by an empty one in
  Directory.Build.targets, and the CI workflows set `AVALONIA_TELEMETRY_OPTOUT=1`.
### Changed
- CONTRIBUTING.md describes the 2.0.N version scheme and the build telemetry rule.
- Tests: new test pinning the empty statistics target, the workflow-level opt-out in every building workflow and the
  contributor note.

## [2.0.7]
### Added
- Command registry shared by the desktop apps: every menu item, shortcut and context-menu entry comes from one list of
  commands, which refuses duplicate ids, clashing shortcuts and unreachable commands when they are registered.

## [2.0.8]
### Added
- The cross-platform desktop app uses the control-room look of the Windows app: the same dark colours, fonts and button,
  card and tab styles.
- Tests: theme parity with the Windows app's Theme.xaml, checked colour by colour.

## [2.0.9]
### Added
- The cross-platform desktop app has a menu bar: File (Open case..., Open run folder..., Exit) and Help (Licence,
  Third-party notices, About GridSim), with Ctrl+O, Ctrl+Shift+O and Ctrl+Q (Command on macOS, where the menu sits in
  the system menu bar).
- Open case lists the same models as the Windows app's network picker and opens on the last one used; the chosen case
  is shown in the status bar (loading it comes in a later update). Open run folder applies the same run-folder check.
- About shows the version, build, runtime and licence; Help opens the licence and third-party notices shipped with the
  app and says plainly if a file is missing.
- The app shares its settings file with the Windows app, rewrites it only when something changed, and reports a
  settings file it cannot read without overwriting it.
- Tests: headless tests for the menu bar and its order, the shortcuts, command failures, About, the licence and notices
  viewers, Open case, the settings round trip and format, and the registry rules.

## [2.0.10]
### Changed
- The desktop dashboard's values, texts, colours and warning states are computed in the shared, platform-neutral UI
  layer (GridSim.Ui), ready for the cross-platform app; the Windows app binds to them through a thin adapter and shows
  exactly what it showed before.
- Tests: dashboard parity: every dashboard value equals the previous desktop code, frame for frame, on case9 and the GB
  reduced model (strings exactly, numbers by bits, colours by bytes); the source checks for the moved dashboard code
  read the shared layer and check the desktop files hold no second copy.

## [2.0.11]
### Changed
- The network map's projection, zoom and pan, level of detail, colour bins and click targets are computed in the shared
  UI layer; the Windows map draws the same pixels.
- Tests: map parity: projection, level of detail, colour bins and hit tests equal the previous desktop code at every
  zoom on case9, the GB reduced model, gb-synthetic and a stacked-busbar case.

## [2.0.12]
### Added
- Run commands in the shared command registry: play or pause, step once, solver, step size and speed, with the Windows
  app's choices in the same order.

## [2.0.13]
### Added
- The cross-platform app's run view model drives the shared simulation host, and its live panel formats every figure
  with the Windows app's format strings; numbers use the invariant culture on every machine, as in the Windows app.

## [2.0.14]
### Added
- Cross-platform app: File > Open case... loads and runs the case through the shared simulation host, with the same
  licence check, cap and refusal text as the Windows app; File > Open run folder... loads a pre-solved run after the
  same run-folder check.
- Run menu and transport bar: play or pause (F5), step once (F6), solver, step size ('.' finer, ',' coarser) and speed.
  Step once also steps a live case (pause, then exactly one tick).
- A live panel shows the tick, simulated time, frequency and deviation, RoCoF, convergence, iterations, max mismatch,
  generation, demand, losses and the licence lines.
- Fail loud: a refused load, a state that did not converge and a solve fault are shown in the alarm colour with the
  Windows app's texts.

## [2.0.15]
### Added
- Tests: headless run-pipeline tests: the frames shown equal a directly driven host under the Windows app's formats;
  pause, step once and the solver, step and speed choices reach the host; the play loop ends cleanly on a new case and
  on close; licence-cap, not-a-run-folder and bridge refusals; the Run menu and its shortcuts.

## [2.0.16]
### Changed
- The maths behind the gauge, sparkline, frequency trace, phasor and P-Q capability diagram lives in the shared UI
  layer; the Windows app draws from it with unchanged pixels.
- Tests: parity tests compare the previous and current Windows drawing call by call, every number by bits, over normal
  and edge inputs.

## [2.0.17]
### Added
- The cross-platform app has the gauge, sparkline, frequency trace, phasor and P-Q capability diagram, with the
  Windows app's properties, defaults and colours; shapes with non-finite points are skipped rather than drawn.

## [2.0.18]
### Added
- Tests: headless tests for the five instruments: rendering over edge inputs, property binding, redraw on change,
  theme colours and one pixel check per instrument; the headless test host renders with Skia and the bundled Inter
  font.

## [2.0.19]
### Added
- Map commands in the shared command registry: fit, minimap, coastline, boundaries, GSP areas, the geography layers,
  trip the largest infeed, clear the selection, the fleet layer and the four colour modes.

## [2.0.20]
### Added
- The cross-platform app's network map control: it draws what the Windows map draws (coastline, DNO and GSP outlines,
  corridors coloured by loading, voltage, condition or stress, moving flow dashes, roundels, labels, boundary cuts,
  fleet, minimap) from the shared map logic, with cached geometry per colour bin; on the full GB model a still frame
  draws in about 13 ms.

## [2.0.21]
### Added
- The cross-platform app shows the network map beside the live panel and updates it on every frame, with the Windows
  map's mouse controls (wheel zoom about the cursor, drag to pan, click for the detail panel, double-click to drill in,
  hover for a bus's voltage and demand) and the STATE NOT VALID veil, legend and hints.
- A View menu carries the Windows map keys (F, M, C, B, G, D, R, A, T, Escape), the fleet layer and the colour modes;
  single-key shortcuts are ignored while you type in a text box or choose in a drop-down. Layer and colour choices are
  saved in the settings file.
- Right-click a bus to trip its largest generating unit from a context menu; a unit can be tripped once per run, as in
  the Windows app.

## [2.0.22]
### Added
- Tests: headless map tests: cases loaded through the run pipeline match the shared map state, pixel checks of node and
  branch colours, exact click selection, exact zoom and pan, every map key, the typing guard, the bus context menu and
  the coastline data. The run-menu test now also checks the View menu and that each map key is bound to its command.

## [2.0.23]
### Added
- The cross-platform app's run pipeline tells the dashboard about each load, refusal, model switch, frame and replay
  restart at the same points as the Windows app, so both dashboards update in the same order.

## [2.0.24]
### Added
- Dashboard tiles for the cross-platform app: status lamp and clock, the frequency dial and headline tiles, voltage-level
  gauges, the map overlays, the what-if panel, transmission boundaries, alarms, the event log and the STATE NOT VALID
  banner, with the Windows app's texts, colours and visibility rules.

## [2.0.25]
### Added
- Tests: headless checks that the dashboard matches a reference model fed the Windows app's calls on every frame of
  case9 and the GB reduced model (every tile's text, colour and visibility), the not-converged and solve-fault paths,
  the reset on a model switch and detach; a parity test reads both XAML files and fails if a Windows dashboard binding
  or label is missing from the new view.

## [2.0.26]
### Changed
- The logic of the SCADA, OPERATOR, MARKET, SPEED DOMAINS and CYCLE tabs is in the shared UI layer; the Windows tabs
  bind to it through thin adapters and show exactly what they showed before.
- Tests: field-for-field parity of each moved tab against the previous Windows code on case9 and the GB reduced model;
  nine source-text tests read the moved files, with their assertions kept and strengthened (each also checks the
  Windows adapter derives from its model and holds none of the moved logic; the SCADA alarm test also pins the amber
  colour token to the previous brush's colour).

## [2.0.27]
### Added
- The cross-platform app has the SCADA, OPERATOR, MARKET, SPEED DOMAINS and CYCLE tabs beside the map, in the Windows
  app's tab order, with the STATE NOT VALID banner over every tab; SPEED DOMAINS computes only while it is showing and
  the CYCLE worker stops when the window closes, as in the Windows app.
### Fixed
- The cross-platform run pipeline raises the dashboard's model switch before the map load and the dashboard update before
  the map's frame, in the Windows window's order.

## [2.0.28]
### Added
- Tests: headless tests for the five tabs: tab order and switching, each tab's texts and colours equal its model after a
  loaded run, the SCADA filter and acknowledge buttons, the banner and the colour converter.

## [2.0.29]
### Changed
- The logic of THE MACHINE, MECHANICAL and INVERTER FLEET tabs, the drill-down inspector and the swing study a map trip
  runs (RoCoF and nadir on the log line, the TRIP alarm) is in the shared UI layer; the Windows app uses it through thin
  adapters and shows exactly what it showed before.
- Tests: exact parity of the moved tabs, the inspector and the trip study against the previous Windows code on case9 and
  the GB reduced model (every trippable unit for the trip study); four source-contract tests read the moved code, each
  kept equal or made stronger.

## [2.0.30]
### Added
- Views for the cross-platform app's GENERATION, THE MACHINE, MECHANICAL and INVERTER FLEET tabs and the drill-down
  inspector, built from the same code as the Windows tabs (not yet placed in the tab strip).
- Tests: headless view tests for each new view fed from a live run, and a parity test that every Windows binding and
  fixed text of those tabs appears in the port.

## [2.0.31]
### Fixed
- Tests: the second tab batch's reference copy of the previous Windows code has its own namespace, so it no longer
  clashes with the first batch's stand-in types; no assertion changed.

## [2.0.32]
### Changed
- Cross-platform app: Open case labels a pre-solved run with its frame count ("... (pre-solved run, 2 frames)"), and
  when a replay has played its last recorded frame the status bar says so and that PLAY restarts it. A replay holds
  only the frames it recorded and stops at the last one, as in the Windows app; a short one no longer looks like a
  stalled run.
- Tests: the frame-count label (stray non-snapshot files not counted, an unreadable run shows its label alone) and the
  end-of-replay message on a two-frame run.

## [2.0.33]
### Changed
- The logic of the HUMAN SCALE, NESO RP1 and EXPERIMENTAL tabs and the audit-log viewer is in the shared UI layer; the
  Windows app shows exactly what it showed before, and EXPERIMENTAL still runs its models off the UI thread.

## [2.0.34]
### Added
- Views for the cross-platform app's HUMAN SCALE, NESO RP1 and EXPERIMENTAL tabs and an audit-log viewer window, built
  from the same code as the Windows app (not yet placed in the tab strip or the File menu).
### Changed
- Tests: eleven source-text tests read the moved tab code, each kept equal or made stronger; the two constant checks
  whose every row moved into the shared layer are now single checks that no file in the Windows app declares any of
  those constants again.

## [2.0.35]
### Added
- Tests: exact parity of the HUMAN SCALE, NESO RP1 and EXPERIMENTAL models and the audit-log reader against the previous
  Windows code on case9 and the GB reduced model (EXPERIMENTAL's off-thread work drained deterministically; a tampered
  audit log); headless tests for the new views.

## [2.0.36]
### Changed
- Cross-platform app: Help is always the last menu; the map layer, fleet and colour-mode items show a tick when on.
- Tests: registry tests for menu order (commands on one menu that disagree on its place are refused, naming both) and
  the tick.

## [2.0.37]
### Changed
- The cross-platform app's main window is laid out like the Windows app: a header with the clock and status lamp, the
  tabs in the same order (NETWORK, GENERATION & INERTIA, THE MACHINE, SCADA, OPERATOR, MARKET, NESO RP1, MECHANICAL,
  HUMAN SCALE, SPEED DOMAINS, INVERTER FLEET, EXPERIMENTAL, CYCLE), the STATE NOT VALID banner over every tab, and the
  transport bar under the tabs. The NETWORK tab is the full dashboard with the map in its centre card; the temporary
  live panel is gone.
- Every tab is fed by the dashboard alone, once per frame, in the Windows app's order; a frame the dashboard cannot
  show feeds no tab. EXPERIMENTAL runs only while its tab is selected.
- Tripping a unit from the map writes its RoCoF and nadir to the event log and raises a TRIP alarm, as in the Windows
  app; NESO RP1 drill-downs open in an inspector overlay; File has Audit log....
- Tests: today's app tests that read the removed live panel and overlays read the same values in their new places
  (equal or stronger; the live panel's convergence label, which the Windows app does not show, is replaced by a check
  that the status lamp shows the dashboard's verdict).

## [2.0.38]
### Added
- Tests: end-to-end headless test of the integrated window on case9 and the GB reduced model: every tab gets exactly
  one update per frame through the dashboard, a frame the dashboard rejects feeds no tab, the map sits in the dashboard,
  menu order and ticks, the trip's swing-study line and alarm, and the inspector overlays.

## [2.0.39]
### Added
- Cross-platform app: the HARMONICS tab, with the Windows app's texts and layout, between EXPERIMENTAL and CYCLE.
- Tests: HARMONICS texts and layout against the Windows XAML; the tab-strip test includes HARMONICS at its place.

## [2.0.40]
### Added
- Cross-platform app header as in the Windows app: the NETWORK source picker (choosing a source loads it), the SOLVER
  picker, the substation search (Enter centres the map on the match and selects it; a miss is logged) with the typing
  guard, and the LOAD RUN... and AUDIT LOG... buttons.
- Tests: header order, picker loading, search selection and centring, the typing guard with the search box focused.

## [2.0.41]
### Added
- Cross-platform transport bar as in the Windows app: COLOR, FLEET, DNO and GSP buttons showing their state, and for
  replays RESTART, STEP BACK and the scrubber, which reset the timeline where the Windows app does; the Run menu ticks
  the solver, step and speed in force.
- Tests: transport buttons with and without geography, replay moves against a directly driven host and the reset
  order, Run menu ticks; parity of labels, opacities and the reset rule with the Windows sources.

## [2.0.42]
### Fixed
- Cross-platform app: a frame the dashboard refuses no longer reaches the map, becomes the current frame or moves the
  replay read-out, as in the Windows app.
- Tests: a NaN-frequency run is refused on every frame without reaching the map, and a good case afterwards does.

## [2.0.43]
### Changed
- Cross-platform app: the window title is the Windows app's, with version and licence tier.
- Tests: the title tests pin the full Windows title format (stronger than the bare name); the transport test file names
  its two replay glyphs by code point so it stays plain ASCII.

## [2.0.44]
### Changed
- The substation single-line diagram and the transformer schematic take their layout and colours from the shared UI
  layer, ready for the cross-platform app; the Windows pictures are unchanged pixel for pixel.
- Tests: the previous and current Windows drawings compared call by call (every number by bits) over normal and edge
  inputs, device scales and a decimal-comma culture; the transformer bushing test reads the shared geometry and also
  checks that a 400 kV unit shows 230.9 kV on each bushing.

## [2.0.45]
### Added
- Electrical diagram toolkit in the shared UI layer: IEC 60617 style single-line symbols (busbar, bus section, circuit
  breaker, disconnector, earth switch, two- and three-winding transformer, generator, load, current and voltage
  transformer, protection relay, cable and line ends), ports, orthogonal connections, bay layout helpers, live-state
  colours (energised, open or closed, loading), hit testing and zoom, with substation and transformer diagram builders
  for the coming ASSETS views. Switches follow the GB control-room colours: red closed, green open.
- Tests: every symbol's shapes and ports, bay placement, hit testing on a probe grid, styling and both builders.

## [2.0.46]
### Added
- Cross-platform diagram control that draws the toolkit's diagrams, zooms with the mouse wheel and reports the symbol
  under the pointer on hover and click.
- Tests: rendering every symbol, a pixel probe of breaker open and closed colours, click and hover reporting, and the
  per-state drawing cache.

## [2.0.47]
### Added
- The GDA bridge's pickers, constants and session logic as shared, platform-neutral code in the UI layer; the Windows
  app is unchanged.
- Tests: a check that fails if the shared bridge tables, texts or pickers ever differ from the Windows app's.

## [2.0.48]
### Added
- The cross-platform app streams from the GDA bridge as the Windows app does, on desktop only: live frames, the HUD,
  OPERATOR, MARKET, NESO RP1 and MECHANICAL data, what-if queries with RETURN TO LIVE, PAUSE hold, the ZONE, FROM and
  SRC pickers, stream-lost reporting and right-click reconnect. An unencrypted ws:// address is refused unless the
  settings allow it. Android and iOS have no bridge.
- The cross-platform app opens the case named on the command line, else the last-used source, else the first case, as
  the Windows app does; refused launch arguments are reported and the app exits with code 2.
- Tests: bridge tests against a fake loopback server with exact parity against a reference dashboard; start-up load
  tests; app tests written for an empty window turn the start-up load off (assertions unchanged), and the
  bridge-refusal test runs with the bridge switched off.

## [2.0.49]
### Changed
- The ASSETS pages (landing page, fleet pages, generator, transformer, circuit, substation and interconnector pages) run
  on shared code the cross-platform app will also use; the Windows app shows exactly the same values, rows and colours.
- Tests: the source-contract tests for the asset pages check the shared page code as well as the Windows adapters, with
  added checks that the adapters hold no logic and that the shared code keeps the index-keyed lookups and shared rules.

## [2.0.50]
### Added
- Tests: every ASSETS page checked value for value against the previous Windows code (case9, the GB reduced model and
  gb-synthetic; every card, list end, chip and cross-link; voltage and loading excursions; model swaps; a decimal-comma
  culture), with a self-test that one bit, one colour byte or one character of difference is caught.

## [2.0.51]
### Added
- The ASSETS explorer's tree (substations by kV, generators by fuel, station and unit, interconnectors, lines by band,
  transformers by band, tap changers), search (name, kind or bus id; Enter on a bus id jumps to it), back and forward
  and the crumb, in the shared UI layer; each node opens its page by the same route as the Windows explorer.
- Tests: navigator tests for tree shape on case9, the GB reduced model and gb-synthetic, search, history and equality
  of every opened page with the existing route.

## [2.0.52]
### Added
- The ASSETS pages in the cross-platform app, matching the Windows pages; substation and transformer pages show the
  Windows diagram and, with IEC VIEW, an IEC single-line view whose bays and busbars open the asset clicked.
- Tests: a parity test that every Windows ASSETS template binding and fixed text appears in the new pages.

## [2.0.53]
### Added
- The ASSETS tab in the cross-platform app, fed once per frame through the dashboard and refreshed only while showing,
  reachable from OPEN IN EXPLORER, the SCADA register and a new "Open in Assets" item on the map's bus and branch
  right-click menu; SHOW ON MAP returns to the network with the asset selected.
- Tests: ASSETS view and bridge tests; the tab-strip, map bus-menu and bridge reference tests include the new tab and
  menu item (equal or stronger).

## [2.0.54]
### Changed
- Core's case and data readers (v1 case folders, measurement sets, event catalogues, GDA series, RTU maps, harmonic
  sets, device configs, PMU channel maps, PyPSA boundaries, solve-export import) read through source-generated JSON,
  with no runtime reflection; loaded models are unchanged, and a file that leaves out an optional field still gets the
  documented default.

## [2.0.55]
### Changed
- The GDA bridge, the PMU quarantine sidecar, the visualiser scene and the evidence dossier use source-generated JSON;
  the bytes on the wire and on disk are unchanged. GridBridgeClient.Query(object) is marked as needing reflection.

## [2.0.56]
### Changed
- The solve, estimate, frequency, H=0, harmonic, impedance, RMS-event and small-signal exports are written through
  source-generated JSON, byte for byte as before.

## [2.0.57]
### Changed
- GridSim.Core is AOT- and trim-compatible (IsAotCompatible, the analysers clean as errors), the prerequisite for the
  iOS app; the SDK's build-time ILLink analyser package appears in Core's lock file.

## [2.0.58]
### Added
- Tests: reflection and source-generated JSON give deep-equal loads (doubles by bits) and identical bytes on every
  repository fixture and exporter input, including every optional block, both shapes of each polymorphic member and
  every reachable enum value; the frozen previous exporters are kept as test-only copies; a scan guards the rule that
  optional fields keep their defaults.

## [2.0.59]
### Fixed
- ASSETS substation fleet: the kV filter chips are rebuilt when the model changes, so they always show the running
  model's voltage levels; a picked level stays picked if the new model has it, otherwise the filter returns to ALL. The
  Windows app and the cross-platform app both get the fix.
- Tests: the ASSETS parity test names one exception, the substation kV chips after a model swap, and checks the rebuilt
  chips exactly; new tests cover chip rebuilds across seven case-swap sequences.

## [2.0.60]
### Fixed
- ASSETS transformer fleet: OLTC FITTED counts transformers that have an on-load tap changer mapped to them by the
  solver's own rule, not OLTC records, so it can no longer read more fitted than there are transformers. No bundled
  case's value changes (GB reduced 10 of 10, gb-synthetic 357 of 2168, gb-full 1383 of 2620).
- Tests: the ASSETS parity test names one exception, OLTC FITTED, checking the previous and fixed values exactly; new
  tests pin the value on every bundled case and show the difference on a case with a shared and an unmapped record
  (12 of 10 before, 10 of 10 now).

## [2.0.61]
### Added
- ASSETS: one identity per asset, and a map from each running-model element to the case record the loader built it
  from, following the loader's own rules (including a line record marked as a transformer); a case changed on disk
  after loading is refused with the problem named.
- Tests: the map pinned against the loader on all seven bundled cases, copies with out-of-service units, the v2
  projections and a case split across two network files; asset identity equality and display.

## [2.0.62]
### Changed
- ASSETS: every explorer tree node carries its asset identity; the tree is otherwise unchanged.
- Tests: every node of the GB reduced and gb-synthetic trees carries its expected identity and is found by it.

## [2.0.63]
### Added
- ASSETS (Run mode): PROPERTIES shows the case record as written, read-only, with the solver's value beside it and
  labelled; CONNECTIONS lists what the asset connects to and where the case refers to it, each a link to that asset;
  new DEMANDS, BOUNDARIES, PROTECTIONS, ASSET CLASSES and OUT OF SERVICE nodes; MATPOWER, replay and bridge sources say
  why there is no PROPERTIES.
- Tests: PROPERTIES rows equal the record literals and field specs exactly; CONNECTIONS equal the model adjacency and
  the where-used list; every link selects a node; the new pages computed independently.

## [2.0.64]
### Added
- Cross-platform ASSETS tab: the PROPERTIES and CONNECTIONS sub-tabs and the new class pages; OPEN INVERTER FLEET jumps
  to that tab.
- Tests: sub-tabs appear only with content and their links select the right node; the ASSETS page-kind test lists the
  exact sub-tabs per node and includes the new page type (stronger).

## [2.0.65]
### Added
- A v2 case manifest can list asset overlay files in a new optional `assets` member, in the new `gridsim-assets/2`
  format: records of substations, busbar sections, switching devices, conductor and tower types, conductor positions,
  line routes, spans, earth wires, soil zones, earth paths, three-winding transformers and HVDC links, defined as data
  in `schemas/v2/kinds/overlay`. The unit list gains m, mm, mm2, A, ohm and ohm.m.
- Tests: the unit-list pin gains the six units and the schema file-layout pin admits the overlay kind folder (equal
  strength).

## [2.0.66]
### Added
- `validate` and the v2 loader check asset overlays and list every problem together, each naming the record and field:
  unknown kind, bad parameter, duplicate id, missing or wrong-kind parent, parent loop, undeclared or missing role,
  unresolved or ambiguous reference. A loaded case carries its overlay beside the model with the note "overlay: N
  records carried, not used by the solver"; cases without `assets` load exactly as before.

## [2.0.67]
### Added
- Tests: with and without a full overlay (all thirteen kinds, every reference type, every new unit), eight bundled cases
  and two fixtures give a deep-equal model and bit-identical power flow and frequency runs; a source and reflection
  guard keeps overlay types out of the solver; checker negatives per kind and per rule; schema and validator tests.

## [2.0.68]
### Changed
- Documentation: an "Asset overlay (gridsim-assets/2)" section in the v2 schema chapter; the unit list in that chapter
  includes the new units.

## [2.0.69]
### Fixed
- ASSETS home: the GENERATOR PHASORS card's ANGLE SPREAD is taken over the generator buses, as on the phasor page, so
  both show the same figure in both apps (gb-full 60.4 -> 51.3 deg, case9 13.3 -> 9.3 deg, GB reduced 5.8 -> 3.4 deg;
  no card changes colour).
- Tests: the ASSETS parity test names one exception for this card's figure, colour and bar (previous and fixed values
  checked exactly); a new test pins the value on every bundled case and checks it equals the phasor page's.

## [2.0.70]
### Fixed
- ASSETS CABLES & LINES: a circuit more than 0.5 kV from every GB ladder level is banded at its own kV instead of the
  nearest GB level, so international cases show their 220, 300, 380, 500 and 750 kV bands and case9 shows 345 kV; GB
  cases are unchanged (pages, chips, circuit pages and the ASSETS tree).
- Tests: the ASSETS parity test names one exception for band chips, row bands and the circuit page subtitle, which must
  apply on case9 and never on a GB case; new tests pin every bundled case's band counts before and after and the rule's
  edges.

## [2.0.71]
### Added
- ASSETS: the case's asset overlay in the explorer tree, by kind under OVERLAY and nested under the buses, lines,
  transformers, generators and demands it attaches to; read-only and marked "carried, not used by the solver". A new
  OVERLAY sub-tab on model assets; PROPERTIES (as written) and CHECK on overlay records.
- The cross-platform run pipeline loads JSON v2 cases through the v2 loader; a refused case fails loudly, as v1 does.
- Tests: overlay index, tree nesting, sub-tabs, pages and links on a GB reduced v2 case with a twenty-record overlay;
  every model page identical with and without the overlay; the v2 load path and its refusals.

## [2.0.72]
### Added
- Cross-platform ASSETS: the overlay pages and sub-tabs, and a simple single-line view of a substation record's
  sections and switchgear as written (normally open devices drawn open; clicking a symbol opens that record or the
  asset it switches).
- Tests: headless tests for the overlay views, the solver note on every overlay page, and a refused bad overlay.

## [2.0.73]
### Fixed
- Cross-platform ASSETS: in bridge mode the GDA per-asset card on a substation page is filled on every route to the
  page (tree, search, go to bus id, back and forward, SCADA row, Open in Assets, OPEN IN EXPLORER, unit and CONNECTIONS
  links), not only from the map's OPEN IN EXPLORER; outside bridge mode nothing is asked. The Windows app is unchanged.
- Tests: nine routes against a fake loopback bridge and the outside-bridge case; the earlier bridge test now asserts
  the fixed behaviour (one more identical query and the same card; stronger).

## [2.0.74]
### Fixed
- ASSETS substation fleet: a repeated bus id reads its first solved voltage, like every other asset page, through one
  shared lookup; no bundled case has a repeated id, so no displayed value changes.
- Tests: the repeated-id check on every bundled case (none); a built repeated-id case with exact before and after
  values; a named parity exception that must never apply on a bundled case; the transformer index source check reads
  the shared lookup (stronger).

## [2.0.75]
### Fixed
- ASSETS transformer PROTECTION panel: for a transformer the case's protections.json covers (by its monitored bus or a
  recloser's target branch), the panel lists the case's own devices and settings with units and provenance, without
  the STATIC DEMO tag; everywhere else the demo panel and its tag are unchanged. Among the bundled cases this affects
  one transformer, gb-synthetic's NSC GSP 2 SGT. Both apps.
- Tests: the panel on every transformer of every bundled case, NSC GSP 2 SGT pinned word for word, built links and the
  swap back to the demo panel, named parity exceptions for the panel and its tag, the tag binding in both apps.

## [2.0.76]
### Security
- Both desktop apps write the GDA bridge endpoint to the event log and status bar without its user-info, query string
  or fragment (each shown as <redacted>), so a token or password in the URL never reaches the log; an ordinary
  endpoint is shown exactly as before.
- Tests: the exact text shown for plain, port, query, user-info, fragment, IPv6 and invalid endpoints; both apps'
  connect lines use the redaction; the bridge parity table pins the two lines equal with the redaction in both.

## [2.0.77]
### Added
- Case editing for the ASSETS tab (shared layer): edit, undo, redo, check, save and Save As a case file, each refusal
  saying why. Bundled cases are Save As only, into the user folder; the shipped files are never written. Fields the
  loader cannot load yet are shown locked with the loader's reason (reset to absent is allowed). A case with an asset
  overlay cannot be saved yet, and says so.
- Tests: editing round trips on a v1 and a v2 case byte for byte; bundled Save As with the shipped hashes unchanged;
  Save As refusals; a refused delete lists its referrers; the locked fields pinned to the loader; the overlay refusal;
  the Author tree, sub-tabs and mode; the registry commands.

## [2.0.78]
### Added
- Cross-platform ASSETS: a RUN / AUTHOR toggle. AUTHOR pauses the run, shows no live values and edits the case in the
  same split layout: an editable PROPERTIES grid, where-used, CHECK, and a case summary on Home. New Edit and Case
  menus (Undo Ctrl+Z, Redo Ctrl+Y, Check, Save Ctrl+S, Save As... Ctrl+Shift+S, Switch to Run) and right-click record
  actions (duplicate, delete, rename, where used, add record), in Author mode only. Returning to RUN asks Save, Discard
  or Cancel for unsaved changes and reloads the saved case. AUTHOR is not offered for MATPOWER, replay or bridge
  sources, with the reason shown.
- Tests: headless Author, grid edit, undo and redo by command and key, Check, Save and RUN reload on a v2 case; the
  Save, Discard, Cancel prompt; bundled Save As in the window; unavailable sources; gestures and context menus.

## [2.0.79]
### Added
- The cross-platform app's case list includes JSON v2 cases (`*.case.json`, gridsim-case/2) found in data/, gda-out/ and
  results/, labelled "v2  <file name>"; a file that cannot be read or is not a v2 case is skipped and listed with the
  reason. The Windows app's case list is exactly as before.
- Tests: the Windows case list equal entry for entry to the previous code on the real app folder and on one holding v2
  files; v2 discovery and labels; invalid files skipped and recorded; the Windows catalogue call unchanged.

## [2.0.80]
### Added
- File > Open case folder... and File > Open v2 case... open a case with the system's folder or file picker; it joins
  this session's case list and is remembered by path. File > Open case... also lists the other v2 cases in folders
  opened this session. Open run folder... uses the system picker too.
- Author mode's Save As... chooses its folder with the system's folder picker; the same safety rules apply after the
  pick (not a non-empty folder, not a bundled folder, not the case's own folder).
- Tests: the new menu items, Open v2 case (load, list, invalid file, cancel), Open case folder (refusal and load), Save
  As through a test picker with every refusal; the File menu tests include the two new items (stronger).

## [2.0.81]
### Added
- Case editor: a v2 case's asset overlay files open, are checked (schema, the overlay checker with every problem
  listed, the v2 loader on the staged case) and are saved atomically with the case; an edit changes only the token it
  touches. Deleting a model asset an overlay record names is refused, listing the overlay referrers; renaming a bus,
  line or transformer updates the overlay's references in one undo step; overlay records are keyed by id with rename
  cascade, guarded delete and a fresh-id duplicate.
- Tests: lossless overlay edits; the model bit-identical after overlay-only edits; delete, rename and duplicate rules
  with exact messages; kind-spec refusals; the save gate refusing a checker-failing overlay with exactly its problems.

## [2.0.82]
### Changed
- ASSETS AUTHOR: cases with an asset overlay are edited, checked and saved instead of refused; overlay records edit in
  PROPERTIES from their kind spec (parameters with units and ranges, references and parent as typed pickers), sit in
  an "Asset overlay" tree by parent, and can be added under a record, attached to a model asset, or at the top level.
- Tests: overlay authoring (grid, actions and refusals, CHECK, Save and Save As, tree, commands); the earlier overlay
  refusal test now asserts the lifted behaviour: the same edit saves with exactly that token changed, Save As copies
  every file byte for byte, and a checker-failing overlay is still refused with nothing written (stronger).

## [2.0.83]
### Added
- Cross-platform ASSETS AUTHOR: "Add overlay record..." on overlay records, the overlay node and model assets (desktop
  only); PROPERTIES edits overlay records; RUN reloads the saved case with the edited overlay.
- Tests: headless overlay authoring (grid edit, context menus, add, CHECK, Save, RUN reload).

## [2.0.84]
### Added
- `tools/UiSmoke`: a headless page-capture tool that saves a PNG of every tab and every ASSETS page of the
  cross-platform window on Windows, Linux and macOS, with a contact sheet and a manifest of file hashes; options choose
  the cases, output folder, ticks, window size and pages. It uses its own settings file, the free licence tier, no
  bridge and no dialogs, so it never touches the user's setup. Dependabot watches it.

## [2.0.85]
### Added
- A parity checklist pairing each Windows app page with its picture from the cross-platform window
  (`tools/UiSmoke/PARITY-CHECKLIST.md`).

## [2.0.86]
### Added
- Tests: the page capture checked on case9 for size, non-blank pictures, true manifest hashes, relative contact-sheet
  links and an untouched user settings file; its command line checked for defaults and refusals.

## [2.0.87]
### Added
- The overlay checker warns when a substation's switch states imply a bus split the authored buses do not show, or a
  coupling the case does not model, naming the substation, sections and devices. It is a warning only: the case still
  loads and solves exactly as written, and the checker's errors are unchanged.
- Tests: the topology projection and exact warning texts on simple, split, coupled, earth-switch and multi-bay
  overlays, none for a consistent one; errors unchanged; the GB reduced case with a warning topology is bit-identical.

## [2.0.88]
### Added
- A substation's OVERVIEW draws its overlay topology as an IEC single-line diagram: sections, couplers, bays with
  breakers, disconnectors and earth switches, and the circuits they switch; switch states as written, and in Run mode
  each section coloured by its bus's solved voltage. The page and CHECK show the topology warnings.
- Tests: exact diagram output, hit testing, page facts, CHECK and click routes; the overlay diagram tests check the
  energised state per section and the new captions (stronger).

## [2.0.89]
### Added
- Author mode: a substation and its records show the topology diagram and warnings from the unsaved session; toggling a
  device's normalOpen redraws them at once, and clicking a symbol selects its record.
- Tests: the Author toggle and click selection, in the shared layer and in the window.

## [2.0.90]
### Added
- Author mode can place a bus at a map point (x and y written in the case's own layout convention, to 6 decimal
  places), connect two buses with a line or a transformer, and move a bus, each as one undo step. A case with no layout
  coordinates gets the map's automatic layout written into its buses on the first bus placed or moved, so the saved
  case looks as the map did. New Map menu in Author mode: Select tool (Escape), Place bus (P), Connect (L), Delete
  (Delete key), and a map context menu with Delete and Show in Assets.
- Tests: the coordinate convention against the map's projection; a placed bus byte for byte equal to the editor's add
  record, with undo restoring the bytes; connect on v1 and v2; a drag changing exactly the x and y tokens; the layout
  write-in; the exact refused-delete text; the commands and gestures.

## [2.0.91]
### Added
- Cross-platform NETWORK map editing in Author mode (desktop): the map shows the case being edited with no live
  values, shares its selection with the ASSETS tree, and supports placing, connecting, dragging and deleting; a refused
  delete lists its referrers in the status bar. Leaving Author gives the run back its own map, or reloads the saved
  case. The Run-mode map is unchanged.
- Tests: headless map editing on v1 and v2 cases with real mouse and keys, selection sync both ways, the context menu,
  Save and RUN reload, and the return to the run's own map; the Author menu bar test includes the Map menu.

## [2.0.92]
### Added
- ASSETS Author mode: an editor for each element kind (bus, line, two-winding transformer, generator with its
  converter block, demand, OLTC, boundary, protection) over the same field grid: fields in sections with units and
  help, pickers for references, enums and booleans, and derived read-outs (X/R, ohms from per unit, impedance on the
  transformer's own rating, MVA at the P/Q corner and others) shown for display only, each with its formula, never
  written. v2 fields with no schema title take their label from the v1 field they came from, and the source is shown.
- Tests: sections and field order, labels, units and help, every read-out checked to the bit against an independent
  formula, editor edits byte-identical to grid edits, locked fields, refusal reasons kept on the field, nothing written
  when an editor is built.

## [2.0.93]
### Added
- Cross-platform ASSETS Author mode: PROPERTIES opens a record of those kinds in its editor; a pick commits, a refused
  value's reason appears under the field, locked fields show the loader's reason, and a button switches to the generic
  field grid and back.
- Tests: headless editor tests on v2 and v1 cases (render, edit and undo, the reason on the field, a pick, locked
  boxes, the switch); the Author grid test switches to the grid before typing (assertions unchanged).

## [2.0.94]
### Added
- Asset overlay: soil zone, earth path and earth wire records show their values in tables with units, linked to the
  buses, lines and records they refer to; a substation shows an earthing summary of its earth paths and soil zones.
  Read-only in Run, editable in Author; carried, not used by the solver.
- Tests: exact page facts, tables and links for each earthing kind; absent values read "absent"; edits in Author one
  undo step each; the tables render in the window.

## [2.0.95]
### Added
- Asset overlay: a three-winding transformer record shows its star bus, three legs, nameplate and pairwise impedances
  as authored, and each leg's authored r and x beside the values the nameplate gives. In Author mode, Derive star legs
  proposes the six leg values (old -> new, with the bases and formulas: Z1 = (Z12 + Z13 - Z23)/2 and cyclic, on the
  common base, then on the case base) and writes them only on Apply, as one undo step; a stale proposal is refused. The
  solver keeps using the authored leg values. CHECK shows display-only consistency notes.
- The case editor can apply several field edits as one undo step, each with the single-field checks.
- Tests: hand-worked star-leg cases with exact doubles (equal ratings, unequal bases, voltage referral, a negative
  reactance); refusals with reasons; Apply changes exactly six values losslessly and undo restores every byte; solved
  frames bit-identical with and without the overlay.

## [2.0.96]
### Added
- Asset overlay: an HVDC link record shows its converter generators, rating, DC voltage and cable length, and in Run
  mode each end's solved output with a display-only sum and loss check; CHECK notes (display only) when both ends have
  the same MW sign or an end is above the rating.
- Tests: solved MW per end equal to the frame's values by bits; notes pinned exactly; solved frames bit-identical with
  and without the overlay; the tables and notes render in the window.

## [2.0.97]
### Added
- Author mode: a v2 case's inverter templates appear under a TEMPLATES node. Local templates are editable, imported
  library templates are read only, and overrides show as child nodes. The template editor lists each parameter with
  its unit, range, default, whether it is required, its choices and help.
- Templates can be added, duplicated and deleted; deleting a template that is in use is refused with every use listed.
  Binding rules and generator bindings can be edited; an override shows the template value beside it and can be reset
  to the template. The generator editor links to its template and shows the effective parameters exactly as the case
  loader resolves them. Every change is one undo step.
- Tests: template pages, edits, refusals and effective parameters pinned exactly; each change one undo step; the
  template views render in the window.
## [2.0.98]
### Added
- Asset overlay: a line route record shows the line it carries, its spans in route order with their towers, lengths,
  positions (geographic or chainage), tower types and conductors, its earth wires, and the total route length beside
  the line's length. Spans are ordered by chainage when every span writes one, otherwise in file order, and the page
  says which. Span, tower type, conductor position and conductor type records show their values with units.
- CHECK notes (display only): a route length more than 1% away from the line's, spans that do not join up by
  chainage, a span with no conductor type.
- NETWORK map: an optional Line routes layer (View menu, off by default, remembered in settings) draws each route
  through its geographic towers. A click selects the route in ASSETS; in Run mode a route takes its line's loading
  colour. Show on map from a route page frames the route. Read only; carried, not used by the solver.
- Tests: route, span, tower and conductor pages pinned exactly; span edits in Author one undo step; the layer's
  points equal the map projection by bits; solved frames bit-identical with and without the overlay. The View menu
  test now includes Line routes, with an item-count check and the layer off by default.

## [2.0.99]
### Changed
- Capability backlog: new items for a Python API (wrapping the CLI's JSON output), study-case and variant
  management, a study report generator, a single-line diagram editor, GIS import and export, a grid-code
  compliance suite, dynamic model parameter estimation, probabilistic load flow, and composite and motor load
  models; an out-of-scope list; the HVDC entry notes what exists today; a revised order of work that brings the
  PSS/E, PowerFactory DGS and UCTE readers forward.

## [2.0.100]
### Added
- Author mode: a transformer3W overlay record's context menu in the ASSETS tree offers Derive star legs..., which makes
  the same proposal as the record's DERIVE STAR LEGS button (shown on its page, written only by Apply). No other
  record's menu changes.
- Tests: the command is Author mode and desktop only, on transformer3W overlay records alone, declared once, enabled
  only with such a record selected; run from the window's menu it proposes the six leg edits and changes nothing.

## [2.0.101]
### Added
- Author mode: the CHECK tab lists the substation topology warnings (implied splits and couplings) beside the last
  Check's problems, from the case as it is now: Home lists every substation's, an overlay record its own
  substation's, each with the substation record's file and pointer. Display only; they never stop a Check or a save.
- Tests: the warnings on Home, a substation and its children, none outside a substation or on a model record, gone
  when the coupler is opened and back on undo, with the last Check's problems unchanged; shown on screen under their
  own header.

## [2.0.102]
### Changed
- Case editing: moving a bus on the map (its x and y written together, as one undo step) is now an operation of the
  case-editing engine rather than of the desktop app. Behaviour, messages and written bytes are unchanged.
- Tests: the engine operation on its own (two buses in one undo step writing only their x and y, nothing to do,
  refusals that change nothing, a repeated bus refused as a caller error) and the map's move writing exactly the bytes
  the engine writes for the same numbers.

## [2.0.103]
### Fixed
- Author mode NETWORK map: a double-click on a bus drills in onto it (centred at substation zoom), as in Run mode.
  A click followed by a quick drag still moves the bus, and while Connect waits for its second bus a double press
  still picks the bus up.
- Tests: the double-click gives exactly the centred substation-zoom view, selects the bus, and changes nothing in the
  case.

## [2.0.104]
### Fixed
- Author mode: the telemetry tiles beside the NETWORK map, and the what-if and boundary panels, are veiled and labelled
  as the paused run's last values (the case being edited is not solved until you return to RUN). Alarms and the event
  log stay readable. Nothing is cleared: leaving AUTHOR shows the run exactly as it was.
- Tests: the veil appears on entering AUTHOR with its label, never takes a click, and goes on leaving, with the tile
  values unchanged.

## [2.0.105]
### Fixed
- Author mode NETWORK map: the selected bus and the first bus of a Connect are drawn and clickable at every zoom, so a
  low-voltage bus placed on a large case no longer disappears below the voltage level of detail at the fitted view.
  Branches between shown buses are drawn with them. Run mode is unchanged.
- Tests: on gb-synthetic, an exempt bus below the cut is shown and hit and is hidden again without the exemption, as is a
  branch to it; the exemptions follow the Author selection and Connect, and leaving AUTHOR clears them.

## [2.0.106]
### Fixed
- Desktop app header: the clock and the status lamp are always shown whole. At the default window size the whole
  header now fits; in a narrower window the right end of the left-hand controls is clipped instead of the clock and
  lamp being drawn over. The engine-in-use text is capped at 130 px with an ellipsis, its full text in a tool tip.
- Tests: at the default size every header control has its natural width and none overlaps the clock or the lamp; at
  1440x860 the clock and the lamp are still whole and nothing overlaps them.

## [2.0.107]
### Fixed
- THE MACHINE: THE SPINNING GRID panel no longer draws its live headline, recent traces and shaft over each other when
  the window is short. The headline (frequency, speed, stored spin, voltage) is laid out first and is whole at the
  default window size; the two traces share what is left, up to 70 px each, and every part clips to its own space.
- Tests: at the default size and at 1440x860 every squeezed part of the panel clips, nothing shares the headline's
  space, and at the default size the headline is whole.

## [2.0.108]
### Fixed
- ASSETS transformer page: the whole page scrolls, as the generator page does, so the schematic is never squeezed
  under the LOADING, TOP-OIL and HOT-SPOT dials and the dials, flow, rating, tap and history can always be reached.
  Both cards also clip to their own space.
- Tests: at the default size and at 1440x860 the page scrolls, each card has its full height, the cards never share
  space, and the dials can be brought wholly into view.

## [2.0.109]
### Fixed
- GENERATION & INERTIA: the fuel-mix and inertia-by-class lists keep their values clear of the scroll bar, and the
  side column scrolls, so the TSGB DEVICE card, its SIMULATE LARGEST-UNIT TRIP button and the trip results can
  always be reached on a short window.
- Tests: at the default size and at 1440x860 no list text runs under a scroll bar and the trip button can be brought
  wholly into view.

## [2.0.110]
### Fixed
- ASSETS substation page: the whole page scrolls, so the single-line diagram has its full height (its labels were drawn
  past the card's edge) and everything below it can be reached. The substation and transformer pages keep their
  content clear of the scroll bar.
- Tests: on both pages, at the default size and at 1440x860, the page scrolls, the diagram card has its full height and
  clips, and no text in view runs under the scroll bar.

## [2.0.111]
### Fixed
- NETWORK map: the LINE LOADING legend and the key hint start to the right of the minimap, so neither is drawn over it.
### Changed
- UiSmoke draws its two NETWORK pictures with the coastline and the fleet glyphs, as AssetSmoke does, so the map
  pictures pair up for the parity check.
- Tests: at the default size and at 1440x860 neither the legend nor the hint overlaps the minimap.

## [2.0.112]
### Fixed
- Row lists whose rows are links (MECHANICAL per-machine state, NESO RP1, the machine inspector) stretch each row across
  the list as WPF does, so the row columns line up with the list header instead of bunching at the left.
- Tests: on MECHANICAL, at the default size and at 1440x860, every row's MW value ends in the header's MW column.

## [2.0.113]
### Fixed
- NESO RP1: the page body keeps the 720 px it needs and scrolls on a shorter window (a taller one is filled as
  before), so LIVE CREDIBLE LOSS shows every row; the PURCHASED TIME dial sits beside its read-out with the card's
  full height instead of being squeezed under its caption; the design-basis line wraps inside the card.
- Tests: at the default size, at 1440x860 and at 1920x1200 the credible-loss rows are whole, the dial has room, the
  design-basis text stays in its card, and the body scrolls only on the shorter windows.

## [2.0.114]
### Fixed
- MECHANICAL: every list keeps its text clear of the scroll bar (the GOV column and the right-aligned values were
  covered), and the P-Q diagram shrinks to fit the PROTECTION ENVELOPE card instead of running its axis labels past it.
- OPERATOR: the largest-loss design-basis note wraps in its column instead of being cut off mid-word.
- Tests: at the default size and at 1440x860 no MECHANICAL list text runs under a scroll bar, the P-Q diagram stays in
  its card, and the OPERATOR note wraps when it is wider than its column.

## [2.0.115]
### Added
- Electrical references: any number written as electrical words read aloud and back, one word per byte from a fixed
  list of 256 (units, pioneers, materials, physics, signals and more; no switching, safety, phase, colour, asset,
  fuel or alarm words), most significant first and joined by dots, for example copper.tesla.kron. Three words give
  16,777,216 references and four give 4,294,967,296. Reading one back is strict (lower case, exact word count) and an
  unknown word is refused with the nearest valid words. An optional check word (CRC-8/SAE-J1850) catches every
  single wrong word and every swap of two neighbouring words.
- A keyed scatter that turns sequential asset numbers into unrelated-looking references with no two ever the same,
  identical for the same key on every machine, with its exact inverse.
- New references either drawn at random (checked against those already taken) or following from an existing id
  (SHA-256); ids that share a reference are reported, never silently resolved.
- Tests: the list (exactly 256, unique, 3 to 10 lower-case letters, no word the start of another, no excluded word,
  its order pinned by SHA-256), round trips, malformed input, the check word against every substitution and
  neighbour swap over a sample, the scatter as a bijection over the whole 24-bit space, and pinned references.

## [2.0.116]
### Fixed
- Author mode: when one step of a combined edit (a bus placed on a case with no layout, a map edit) is refused, the
  case and its whole undo and redo history are put back as they were before the edit. The undone steps are no longer
  left on the redo list, where Redo would have replayed half of a refused edit, and a redo history you had before
  is kept. A successful combined edit at the undo limit now drops only the oldest undo level.
- Tests: the refused-edit test now expects the history as before the edit (nothing left to redo) and the new message;
  new tests pin a kept redo history that still replays, the kept undo history and save point, and the undo limit.

## [2.0.117]
### Fixed
- Tests: two case-editor screen tests had come to depend on the length of the version string (a shorter version let
  the licence tier and the full modified-file list fit in 80 columns, so the rules they check were no longer
  exercised and they failed). They now use 10-character display names that exercise the same rules for any version
  string of 5 to 10 characters, and a new test pins the title rule at its exact boundary widths. The editor itself
  is unchanged.

## [2.0.118]
### Fixed
- Phasor (both apps): a lagging current (exporting VArs) is drawn below the axis and a leading one above, by the
  standard convention with the voltage as the reference; it used to be the other way round, against the labels.
- Tests: the phasor drawing comparisons use the original control with only the current angle corrected, pinned to
  differ from the original in exactly those lines; a new test pins lagging below, leading above, mirrored exactly.

## [2.0.119]
### Fixed
- Desktop app header: the search box and LOAD RUN... keep their original spacing again (16 px), as the earlier header
  change had narrowed it; the engine-in-use text is capped at 118 px instead, so the whole header still fits at the
  default window size.

## [2.0.120]
### Fixed
- GDA bridge: the HUD's FRAGILITY reading is read on the stream's own 0 to 100 scale (GDA's system fragility
  index) and coloured accordingly; a real reading such as 33.49 no longer saturates the colour ramp. A value outside
  0 to 100 is shown as no reading. The web dashboard's map HUD gets the same 0 to 1 condition index.
- GDA bridge: the dashboard's INERTIA tile and its secure-minimum note show the stream's inertia figure, or " - " when
  the stream sends none, instead of 0 (the streamed network carries no machine inertia constants). A model that
  carries inertia, and every local or replay run, is shown exactly as before.
- Tests: new tests for the fragility scale, impossible readings, the HUD colour and the bridge inertia tile. Two
  existing bridge test fixtures now send the fragility on the real 0 to 100 wire scale (72 and 70+2k instead of 0.72
  and 0.70+0.02k); their assertions are unchanged.

## [2.0.121]
### Changed
- Desktop app start-up: a case opens solving in continuous operation. When the last case you used was a pre-solved
  run (which opens paused on its first frame), the app now opens the first live case instead; the run is still in
  the NETWORK picker, one choice away. A case named on the command line, a last-used live case and a last-used
  bridge open as before.
- Tests: every kind of local case (case folder, MATPOWER file, JSON v2 case) loads at start and keeps solving with no
  click; a last-used run gives way to the first live case; the start-up choice rule on its own.

## [2.0.122]
### Added
- CLI: gridsim ref encode, decode, scatter and from-id write and read electrical references from the command line,
  with --words N and an optional --check word; scatter takes a required --seed. A refused reference exits 1 with
  the reason and the nearest valid words; a usage error exits 2. See the CLI reference.
- Tests: each subcommand against the library, refusals, usage errors and the verb help.

## [2.0.123]
### Added
- CI: the release-gate matrix also runs the full suite on Linux ARM64 and Windows ARM64 runners, in the normal and
  hardware-intrinsics-off modes, against the same pinned results as every other leg.
- Tests: the four ARM64 legs are pinned in the gated matrix job, and no leg is advisory.

## [2.0.124]
### Fixed
- ASSETS: a voltage-level group under SUBSTATIONS opens the substations page filtered to that level (the SUBSTATIONS
  node itself opens it unfiltered).
- ASSETS: the explorer tree only makes the rows in view, so a large voltage group (over 1,500 buses on the synthetic
  GB case) opens at once; a node selected from elsewhere is still brought into view and selected.
- ASSETS substation single-line diagram: the bays are spaced for the longest far-end, generator or demand label, so a
  label no longer runs into the next bay.
- Tests: the group filter, scrolling to a far node, only the rows in view made, and the bay spacing rule; the explorer
  route comparison now expects a voltage group's page filtered to its level.

## [2.0.125]
### Fixed
- ASSETS: a voltage band group under CIRCUITS opens the circuits page filtered to that band, as the substation
  groups do.
- HUMAN SCALE: a frame with no angles clears the machine list, so the machines come back correctly when angles return.
- Phasor diagram: the power-factor and angle labels use a full stop for the decimal point on every locale.
- Instruments: a gauge, phasor, P-Q diagram, sparkline or frequency trace given a size that is not a number draws
  nothing instead of drawing with invalid points; every real size draws as before.
- UiSmoke: the scratch copy of the case is kept inside the output folder.
- Tests: the circuit band filter, the machines returning after a frame with no angles, the labels under a German
  locale, and the scratch folder; the size check comparison now expects nothing drawn for a size that is not a number,
  and the HUMAN SCALE comparison copy carries the same machine list fix.

## [2.0.126]
### Fixed
- Bundled networks: the checkout's own tools (UiSmoke, AssetSmoke) recognise the bundled cases as the app does, so
  the free tier loads gb-full in them.
- UiSmoke: on a large case it waits for the frame being made at the pause before stepping, so gb-full no longer
  times out; it chooses the generator and interconnector units by the explorer tree's rule, and skips the AUTHOR
  picture (with the reason) when the temporary copy is above the free tier's bus limit.
- UiSmoke parity checklist filled in for the 2026-10-24 run on Windows 11.
- Tests: a build output under the checkout's tools folder gets the checkout data root, and a lookalike folder does not.

## [2.0.127]
### Added
- PSS/E RAW import: a PSS/E power-flow file of revision 33, 34 or 35 (`.raw`) loads wherever a MATPOWER `.m` file
  does: the case run, every verb that takes a case, and `convert` (so `gridsim convert grid.raw grid.m` writes it out
  and proves the round trip). Buses, loads (constant power, current and admittance), fixed and switched shunts,
  generators, branches, switching devices, two- and three-winding transformers (a star-point bus per three-winding
  unit) and area, zone and owner names are read; every refusal names the line, the section and the field.
- `--allow-unmodelled` reads a `.raw` file holding in-service DC links, FACTS devices, induction machines or impedance
  correction tables, which are otherwise refused because GridSim does not model them; each kind is counted in a note.
- A RAW writer in the core library (revisions 33, 34, 35), used to make the reader's round-trip fixtures; the IEEE
  14, 39 and 118-bus cases written as RAW read back to the same admittance matrix and power flow bit for bit.
- Docs: manual chapter 2 (PSS/E RAW files) and chapter 19 (`convert`, `--allow-unmodelled`, `--frequency-hz` for a
  `.raw` file).
- Tests: the IEEE 14, 39 and 118-bus RAW fixtures against the MATPOWER cases, every revision layout, MATPOWER's own
  PSS/E test files, a hand-written revision 35 file holding every section, malformed input, and the CLI routes.

## [2.0.128]
### Fixed
- ASSETS: the INTERCONNECTORS page, its landing card and the explorer list only real interconnector links. Grid
  infeeds (the slack balancer and the voltage-control units at a model boundary) were listed as links, so on gb-full
  the page showed 1,084 links and 849.3 GW of capacity instead of its 12 links and 11.0 GW. Each grid infeed is now
  listed in the explorer under the substation bus it feeds; the generator pages and every generator figure are
  unchanged.
- GENERATION & INERTIA: the import-links panel and its net import count only interconnector links, never a grid
  infeed's output. The same rule now applies in the web views.
- The fuel mix, carbon intensity and THE MACHINE's IMPORTS group still count a grid infeed as imported energy.
- Tests: the link and plant rules, the gb-full and continental cases counted from their raw files, the landing cards,
  the INTERCONNECTORS page, the explorer's grid infeed nodes, and the import-links panel.

## [2.0.129]
### Fixed
- THE MACHINE: the page scrolls as a whole, so the spinning grid always shows its live headline, both recent traces at
  full height and the whole shaft. On the default window the traces and the shaft had no room. The fuel-in and
  power-out cards are never taller than the spinning grid; their lists scroll inside them.
- Tests: the spinning grid check now requires every part at its full height, both traces at 70 px and the side cards
  no taller than the spinning grid, at the default and the 1440x860 window sizes. It used to require the panel to be
  squeezed and checked only that the squeezed parts were clipped.

## [2.0.130]
### Added
- PSS/E DYR dynamic data: `--dyn-data grid.dyr` on a `.raw` case (`stability --rms`, `trip`, `transient`, `modal`) maps
  GENROU and GENSAL machines, SEXS and IEEET1 exciters, PSS1A stabilisers and TGOV1, IEEEG1 and HYGOV governors onto
  the case's machines by bus and machine ID, then checks and binds them as any machine dynamic data file. Values are
  tagged with the file and line they came from; the few GridSim needs in another form (SEXS lead time, IEEEG1 droop,
  the GENSAL q axis) are derived exactly and tagged as derived.
- A DYR file holding models GridSim does not map, a REGC_A converter, a controller with no machine model, or values a
  GridSim model has no block for (for example a transducer lag, a PSS filter or a HYGOV turbine gain) is refused with
  every item listed; `--allow-unmodelled` leaves them out, each in a note, and uses the rest.
- Docs: manual chapter 2 (Dynamic data (DYR)) and chapter 19 (`--dyn-data` takes a `.dyr` file).
- Tests: a hand-written IEEE 14-bus DYR file loaded through the machine dynamic data loader with every value checked,
  unknown models, unmodelled values, malformed records, and the CLI route.

## [2.0.131]
### Fixed
- MECHANICAL, NESO RP1 and the inspector: list rows that open a detail page have the same padding as in the WPF
  window, which had been much tighter.
- AssetSmoke draws the OPERATOR, MARKET, NESO RP1, MECHANICAL, HUMAN SCALE, SPEED DOMAINS, INVERTER FLEET,
  EXPERIMENTAL and CYCLE tabs of the WPF window, so every room tab with a WPF view can be compared picture by
  picture. SPEED DOMAINS, INVERTER FLEET and EXPERIMENTAL are drawn while active, as selecting the tab makes them.
- UiSmoke parity checklist: THE MACHINE and the room tabs T6 to T15 compared (HARMONICS has no WPF view to draw);
  gb-full's interconnector rows checked again.
- Tests: every list row in the room tabs that opens a detail page has the padding the WPF rows are drawn with, and
  the MECHANICAL rows are drawn with it.

## [2.0.132]
### Added
- Docs: `docs/ui/WPF-RETIREMENT-INVENTORY.md` lists everything that still depends on the WPF desktop app (the
  project, the AssetSmoke tool, the tests that read its files, CI, release and docs) and the order of the steps that
  retire it. Nothing is removed yet.

## [2.0.133]
### Fixed
- PSS/E RAW reader: the registered sign in its revision pattern is written as an escape, so the source holds only
  plain ASCII text; a revision named as PSS(R)E-NN with the registered sign is still read.
- Tests: the revision read from a comment that names PSS/E with the registered sign.

## [2.0.134]
### Added
- PSS/E RAW revision 30, the layout of the UKGDS distribution cases and of PSS/E 30's own sample case: bus records
  that carry their own shunt (GL, BL), switched shunts with no status field, and the revision 30 section order.
  Revisions 31 and 32 are refused with the reason: no sample or format reference is on hand to check their layouts
  against, and a guessed layout could read one field as another with no error. The writer still writes 33, 34, 35.
- `--psse-revision <n>`: the revision of a `.raw` file whose first line does not say (the UKGDS files do not).
- PSS/E sequence data (SEQ, revision 30 layout) with `--seq-data <file.seq>`: machine subtransient and
  negative-sequence reactances, line zero-sequence impedances, and two-winding transformer zero-sequence impedances
  with their winding connections (connection codes 1 to 4; a neutral grounding impedance is folded in as 3 (RG + jXG)),
  so `fault` and `iec60909` get the file's earth paths. Data GridSim's fault networks have no element for (a machine's
  own zero-sequence impedance, sequence shunt loads, mutual coupling, zero-sequence switched shunts) is refused unless
  `--allow-unmodelled`; three-winding records, connection codes 5 to 9 and a change case are refused. Every refusal
  names the SEQ line, section and field.
- All 12 UKGDS cases load with their sequence data. The 7 HV cases and EHV5 solve as written; EHV2, 3, 4 and 6 have no
  power-flow solution at the file's taps and need the automatic tap control the files ask for (COD 1), which the
  reader does not apply (noted on load).
- Docs: manual chapter 2 (revision 30, Sequence data (SEQ)) and chapter 19 (`--psse-revision`, `--seq-data`).
- Tests: a hand-written network in the UKGDS layout (tabs, headings, CR LF, a cp1252 quote) that reads to the same
  model as its revision 33 twin bit for bit; PSS/E's revision 30 sample checked value by value; the SEQ data applied
  field by field with a hand-worked zero-sequence driving-point impedance; malformed and mismatched SEQ records; the
  CLI route, including iec60909 finding the earth paths only with the SEQ file; and, when `GRIDSIM_UKGDS_DIR` names the
  UKGDS raw folder (not committed: it has no licence), every UKGDS case. Two existing tests changed with the revision
  30 support: the test that pinned the refusal of revision 30 now pins that the same file is read and refused only for
  the devices it holds that GridSim does not model, and the help-text test names revision 30 among those read.

## [2.0.135]
### Added
- UCTE-DEF files (`.uct`, version 02) load as a case wherever a `.m` or `.raw` file does (the bare run, every verb's
  case, `convert`'s input). Nodes, lines, busbar couplers, two-winding transformers with phase and angle regulation
  (asymmetric and symmetric) and their magnetising admittance are read by their fixed columns; base voltages come from
  the node codes' voltage level character; generation is read with its load sign turned round; X-nodes are buses with
  their written injections. Transformer impedances are referred to the non-regulated winding, as PowSyBl's importer
  reads them. Node type 1, lines between voltage levels, both regulations off neutral on one transformer, and blocks
  out of order are refused, naming the line, block and field; special transformer tables (##TT) are refused unless
  `--allow-unmodelled`. No system frequency in the format: `--frequency-hz`, else 50 Hz, noted.
- Docs: manual chapter 2 (UCTE-DEF files) and chapter 19 (`convert` input, `--allow-unmodelled`).
- Tests: a hand-written two-country network with an X-node, a coupler, a regulated transformer and an exchange block,
  every per-unit value worked out by hand; angle regulation; malformed and inconsistent records; ##TT; the CLI route
  and a bit-identical `convert` round trip. One existing test changed: the PSS/E CLI test that reads `convert`'s usage
  line now expects `.uct` beside `.raw` in it.

## [2.0.136]
### Added
- A full AC optimal power flow in the core library (`AcOpfInteriorPoint`): a primal-dual interior point method (MATPOWER's
  MIPS, with its defaults, starting point and stopping conditions) over every bus voltage angle and magnitude and every
  unit's active and reactive output, with exact analytic first and second derivatives and the sparse LU on the reduced
  KKT system. It minimises the units' polynomial cost curves subject to the AC power balance at every bus, each rated
  branch's MVA limit at both ends, each bus's voltage limits and each unit's P and Q limits, with every slack angle
  fixed. It returns the dispatch, voltages, branch flows, the nodal prices of active and reactive power, the shadow
  prices of every limit, the binding constraints and the iteration log. A run that does not converge says so (iteration
  cap or numerical breakdown) with its log; it is never reported as an optimum. The existing `acopf` (a DC dispatch with
  an AC loss loop) is unchanged.
- It refuses, naming the cause: a unit with no cost curve or a piecewise-linear one, a dispatchable load (Pmin below 0
  with Pmax 0), a voltage-dependent load, crossed or non-numeric limits, a zero-impedance branch, a network with no slack
  bus, and a network in more than one island. Branch angle difference limits are not enforced (they are not part of the
  model; the MATPOWER reader notes them).
- Tests: the objective of MATPOWER `runopf` to its printed two decimals on case9, case14, case30, case57, case118,
  case300, case1354pegase and case2869pegase, and the case9 dispatch; the analytic gradient and Hessian of the Lagrangian
  against central finite differences; the KKT conditions at the optimum (a unit inside its limits priced at its own
  marginal cost, no reactive price at a unit inside its Q limits); case30's binding branch and its price separation;
  two solves bit-identical; the case9 optimum pinned bit for bit; failing loud at the iteration cap and on an infeasible
  case; every refusal.

## [2.0.137]
### Added
- `--tap-control`: a PSS/E `.raw` file's automatic voltage control by tap (COD 1) is applied. Each in-service controlled
  winding becomes an on-load tap changer on its branch (controlled bus CONT, band VMI..VMA, ratio limits RMI..RMA in
  the units CW gives, step (RMA - RMI) / (NTP - 1), divided by winding 2's ratio for a two-winding unit), stepped by
  the solve's tap loop from the file's ratios. A controlled transformer with no name, or a name another branch shares,
  is given one of its own (`NAME (I-J CKT)`), noted. COD 2 to 5, CONT 0 and a dropped controlled bus are noted and not
  applied; a negative CONT naming a remote bus, a CONT naming no bus of the file, fewer than 2 tap positions, RMA not
  above RMI and an empty band are refused, naming the line. Without the flag nothing changes.
- Ramp start for tap-changing cases (with `--tap-control`): a case that does not converge from the file's taps is
  solved at 10 %, 20 %, ... 90 % of its demand and generation first, the taps settling at each stage, then at full load
  from those taps; the report says so on a `Ramp start:` line. A case that converges from its taps never ramps, and a
  stage that does not converge leaves the solve from the file's taps as the result.
- With `--tap-control` all 12 UKGDS cases solve: EHV2, 3, 4 and 6, which have no solution at the file's taps, solve from
  the ramp start, whose first stage starts the bus angles from the transformer phase shifts (30 degree vector-group
  shifts there). In EHV2 two 132/66 kV units end at the bottom of their range with their bus below its band, named on
  the `Taps NOT SETTLED` line.
- Docs: manual chapter 2 (Tap control) and chapter 19 (`--tap-control`).
- Tests: the tap changers read from the file field by field (two- and three-winding, winding 2 ratio, parallel units
  renamed); refusals with their line; a two-bus case whose tap settles where the closed-form two-bus solution says; a
  two-bus case beyond its nose point at the file's tap that the ramp start solves to the closed-form tap (and does not
  converge without it); a ramp stage that fails returning the case's own solve bit for bit; the ramp never touching a
  case that converges from its taps (bit for bit); the CLI route; and, when `GRIDSIM_UKGDS_DIR` names the UKGDS raw
  folder, every UKGDS case with its tap control applied.

## [2.0.138]
### Added
- Python package `python/gridsim` (standard library only): runs a result-file verb of the CLI with `--json` and returns
  the result document as Python values, so scripts get the CLI's numbers bit for bit. `GridSim().run(verb, *args)` and
  one method per verb (`pf`, `dcopf`, `n_1_1`, `physics_check`, ...); a `Result` carries the summary, the tables, the
  exit code (a failing verdict is still a result) and the console output; `"NaN"` and the infinities come back as
  floats; a run that writes no result raises `GridSimError` with its exit code and last stderr line. The CLI is found
  from `cli=`, `GRIDSIM_CLI`, the PATH or the checkout's own build. It wraps exactly the 28 result-file verbs; the
  rest write their own files or run without end and are listed in `python/README.md` as not wrapped.
- Docs: manual chapter 19 (Python) and `python/README.md`.
- Tests: the package's own unittest suite (`python -m unittest discover -s python/tests`: a stand-in CLI for argument
  passing, round-trip bits, named floats, failures and refusals; the real CLI on built-in cases when one is found), and
  a GridSim test that keeps the package's verb list and aliases equal to the CLI's.

## [2.0.139]
### Added
- `gridsim acopf-full <case> [--max-iter 150] [--top 12] [--cost-file <file.json>]`: the full AC optimal power flow from the
  command line. It prints the objective, generation, load and losses, the units' P and Q, the nodal prices of active and
  reactive power with each bus's voltage, and the binding constraints with their shadow prices. The costs are the case's
  own curves (a `.m` file's `mpc.gencost`) or a cost file. Exit 1 for a case it refuses (with the reason), 3 when it does
  not converge (with the iteration log on stderr, no dispatch and no result file). Result files (`--json`, `--csv`,
  `--out`) carry every bus, unit, branch, binding constraint and iteration.
- Docs: manual chapter 6 (worked example on case9, reading the output, limits) and chapter 19 (`acopf-full`); `--help`
  lists the verb.
- Tests: the built CLI on case9 and case30, its result file carrying the library's numbers bit for bit, a cost file giving
  the same optimum, exit 3 with the log and no result file at the iteration cap, exit 1 for a case without costs, exit 2
  for foreign flags. One existing test changed: the list of verbs that write result files now includes `acopf-full`.

## [2.0.140]
### Added
- A sparse linear programming solver in the core library (`SparseInteriorPointLp`): Mehrotra's predictor-corrector
  primal-dual interior point method for minimise c'x subject to A x = b and lo <= x <= hi (any bound may be infinite; a
  free variable is allowed), with A given as sparse entries and each Newton step solved by the sparse LU. It returns the
  point, the equality and bound multipliers, the objective and the final residuals. An infeasible or unbounded LP is
  reported as such, never as an optimum; a malformed problem is refused. It is the basis for the network-constrained
  unit commitment and security-constrained OPF to come; the dense bounded simplex is unchanged.
- Tests: textbook LPs with known optima and shadow prices, strong duality, native upper bounds and their duals, a free
  variable, a repeated constraint, generated sparse LPs against the dense bounded simplex, infeasible and unbounded LPs,
  two solves bit-identical, malformed problems refused.

## [2.0.141]
### Added
- `--shunt-control`: a PSS/E `.raw` file's switched shunts with discrete local voltage control (MODSW 1) switch in whole
  blocks to hold their bus in VSWLO..VSWHI, when their blocks are all one size and BINIT is a whole number of them
  (reactor and capacitor blocks both; a revision 35 block with status 0 left out). The case solve switches them inside
  the tap loop and reports a `Switched shunts:` line; `pf` switches them as it does a `--switched-shunts` file's banks
  (giving both is refused). BINIT stays in the bus shunt and the blocks count from it. Other control modes, remote
  regulation, mixed block sizes and a BINIT off the block grid or outside it stay fixed, noted; a band that cannot make
  voltage control and a negative block count are refused, naming the line. Without the flag nothing changes.
- The Python package wraps `acopf-full` (method `acopf_full`), which writes a result document since 2.0.139.
- Docs: manual chapter 2 (Shunt control) and chapter 19 (`--shunt-control`).
- Tests: the banks read from the file (one size, reactor and capacitor blocks, BINIT inside them), every reason a shunt
  stays fixed, the refusal with its line, PSS/E's revision 35 sample, the bank switching until its bus is in band with
  a solve bit-identical to the fixed-shunt solve at that block count (and out of band without it), and the CLI route in
  the case solve and `pf`.

## [2.0.142]
### Added
- Bus frequency, RoCoF and voltage angle jump in the RMS engine (`BusFrequencyMeter`, `RmsTransientStability.StudyEventsMeasured`).
  An RMS event study can now record the voltage angle of any set of buses at every step, from a second solve of the same
  network, machine EMFs and converter currents the step was committed with. From the angles: each bus's frequency over
  about one nominal cycle, its RoCoF over a 500 ms sliding window (the GB loss-of-mains window), and the voltage angle
  jump at each event, which is taken out of the angle before the frequency is formed and reported on its own. Recording
  feeds nothing back: every machine and converter number of the run is the same bits as without it.
- Tests: known signals (a steady offset, a frequency ramp, an angle step at an event, an angle crossing 180 degrees, a
  window that is not a whole number of steps), every machine and converter number of case9 and uk bit-identical with and
  without measurement, the measure steady before the event and every bus falling after a load step, and unknown or
  repeated buses refused.

## [2.0.143]
### Added
- `inertia-placement`: the inertia placement study. With a network's synchronous inertia in better places, can it run on
  less of it? Today's fleet keeps a share of its inertia (default a quarter; machines, dispatch and locations unchanged)
  and the rest of each budget is synchronous condensers (H 8 s, no power at the solved state, so the power flow is the
  case's) placed five ways: where today's inertia is, by demand, at eleven inverted-T / ladder sites from Inverness to
  Cornwall and Kent, all at one London site, and at the T sites with weights from a coordinate search. Each 1.8 GW loss
  (Sizewell, Hinkley Point, the Kent interconnectors, Scotland over B6) is run on the RMS engine with IEEET1 exciters
  and every 132 kV and above bus measured. It reports per run the centre-of-inertia and worst bus RoCoF and their ratio
  K (the spatial penalty), the loss the machines felt, the full-loss worst bus RoCoF, the angle jump, the north-south
  inter-area oscillation and the time to 49.8 Hz; and per placement the smallest budget keeping the worst bus RoCoF
  under each limit (0.125, 0.25, 0.5 and 1 Hz/s) and giving 3 s and 5 s to 49.8 Hz, with the lumped time to 49.8 Hz with
  frequency response beside it. Default case `gb-full`; result files through `--json` / `--csv`.
- Docs: manual chapter 19 (`inertia-placement`, its flags, output, exit codes and limits).
- Tests: great-circle miles against known distances, condensers adding exactly the budget less the residual with every
  existing unit in place and the power flow unchanged, sites on their nearest transmission bus, the lumped time falling
  with less inertia, the verb on uk with its result file, and malformed or foreign flags exiting 2. One existing test
  changed: the list of verbs that write result files now includes `inertia-placement`.

## [2.0.144]
### Added
- A sparse bounded-variable revised simplex in the numerics (`SparseBoundedSimplex`): minimise c'x subject to A x = b
  and lower and upper bounds (infinite bounds and free columns allowed), two phases on the sparse LU with product-form
  updates and periodic refactorisation, Dantzig pricing with a switch to Bland's rule after a run of degenerate steps.
  It proves infeasibility, reports unboundedness, lands on a vertex and returns the row duals. The dense simplex is
  unchanged.
- A mixed-integer linear programming solver (`MilpBranchAndBound`): LP-based branch-and-bound over the sparse simplex,
  best-bound or depth-first node order, relative and absolute gap, integrality tolerance and node cap. The answer's
  integer columns are exactly whole; a node cap or a node relaxation that fails stops the search with the incumbent and
  best bound so far, never a guessed result. Deterministic: the whole search is the same bits from run to run.
- Tests: the sparse simplex on textbook LPs with their duals, Chvatal's cycling example, agreement with the dense simplex
  on 60 generated LPs (some long enough to rebuild the factors, some infeasible), free and fixed columns, a feasible set
  with no interior point, bound overrides and bit-for-bit repeatability; the branch-and-bound on a knapsack, a facility
  location and a three-unit commitment with known optima, agreement with brute-force enumeration on 40 generated
  problems, infeasibility at and below the root, the node cap, both node orders and repeatability; the interior point
  solver on the two infeasible problems that made it throw.
### Fixed
- The sparse interior point LP solver threw an exception on some infeasible problems (a bound slack reached zero before
  the iterates diverged, giving the sparse LU an infinite entry); it now stops with a status saying the LP is probably
  infeasible.

## [2.0.145]
### Added
- Security-constrained unit commitment in the analysis layer (`UnitCommitment`): which units run in each period, and at
  what output, at least total cost, solved exactly as a mixed-integer program. Per unit: on/off and start decisions,
  output between Pmin and Pmax while on, minimum up and down times (held from the unit's initial state), ramp limits,
  start-up and no-load costs; per period: the demand balance and a spinning reserve requirement; per rated branch: its DC
  flow within RATE_A, and for each listed outage every rated branch's post-outage flow within RATE_A. Costs come from a
  flat marginal cost, else the case's cost curve (linear and piecewise-linear exactly, a quadratic cut into chord
  segments), else the fuel-type cost. It reports each unit's schedule and cost, each period's totals and system price,
  and the binding branch limits.
- The unit commitment data file `gridsim-uc/1` (`UnitCommitmentFile`): periods, period length, per-period demand scale
  and reserve, per-unit data by index or name, and the outages to secure by branch index or name; every key checked.
- Tests: the three-unit instance against full enumeration, minimum times and reserve against an enumerating reference
  in six combinations, a congested line and a secured parallel-line outage against hand calculation, a ramp limit, the
  system price, case9's own cost curves, repeatability, the refusals, and the data file (a valid file and nine malformed
  ones).

## [2.0.146]
### Fixed
- `inertia-placement`: no placement puts a condenser on a slack bus any more. The demand placement put one on gb-full's
  slack (Melksham); the RMS initialisation shares a bus's solved generation between its units equally when their
  scheduled total is zero, so the condenser was handed part of the slack's output, started with a field (25.9 pu) its
  exciter could not hold, and the whole study stopped at 120 GVA.s. Sites, the demand gathering and the fleet placement
  leave a slack bus out, and a placement naming one is refused before any work. The other placements are unchanged.
- Tests: no placement on uk's slack bus and a placement naming it refused (fails on 2.0.145).

## [2.0.147]
### Added
- `scuc`: security-constrained unit commitment on the command line. Which units run in each period and at what output,
  at least cost, with minimum up/down times, ramps, start-up and no-load costs, spinning reserve, the rated branches' DC
  limits and the outages a `gridsim-uc/1` data file lists (`--uc-data`). Prints each period's totals and system price,
  the largest units' schedules and the binding limits; `--periods`, `--no-line-limits`, `--node-cap`, `--gap`, `--top`.
  Exit 3 when no optimum is proven (an incumbent found before the node cap is printed, labelled as not proven). Result
  files through `--json` / `--csv`.
- Docs: manual chapter 19 (`scuc`, the data file, its flags, output, exit codes and result tables).
- The Python package wraps `scuc` and `inertia-placement`.
- Tests: the verb on case9 (one period, and four periods from a data file with its result file equal to the library's
  numbers bit for bit), an infeasible demand exiting 3 with no result file, a malformed data file exiting 1, foreign and
  bad flags exiting 2. One existing test changed: the list of verbs that write result files now includes `scuc`.
### Fixed
- The Python package's verb list now includes `inertia-placement`, so it matches the command line's list again.

## [2.0.148]
### Added
- PSS/E transformer impedance correction tables (TAB) are modelled. A winding with a table has its impedance (Z1-2 for a
  two-winding unit, its star impedance for a three-winding one) multiplied by the table's factor at the winding's ratio,
  or at its phase shift angle for a phase shifter: linear interpolation, the end factor outside the table (noted), real
  factors in revision 33 and earlier and complex ones in revisions 34 and 35. With `--tap-control` the factor follows
  every tap the tap loop steps. Such files are no longer refused and need no `--allow-unmodelled`; malformed tables
  (fewer than two points, T not increasing, a zero factor, a table defined twice) are refused naming the line.
- Docs: manual chapters 2 and 19.
- Tests: hand-calculated factors on the two-bus case (between points, on a point, beyond the end), the impedance at the
  tap the loop settles on, complex factors, PSS/E 30's own sample, and malformed tables. Two existing tests changed: the
  one that pinned the refusal of a transformer using a table now pins its corrected impedance, and PSS/E 30's sample is
  no longer refused for its tables (it still is for its DC links and FACTS devices).

## [2.0.149]
### Fixed
- `inertia-placement`: a condenser put on a bus that already has units now keeps the bus's voltage setpoint. The power
  flow takes a bus's setpoint from its last unit, and an appended condenser carried the solved voltage, so at a bus held
  off its setpoint by a reactive limit the study's power flow moved: on gb-full's fleet placement, Willesden 66 kV by
  0.0010613504115799532 pu. The largest move left is about 2e-11 pu, from the microvar each way a condenser adds to its
  bus's reactive limits. The other placements were not affected on gb-full.
- Tests: gb-full's fleet placement leaves every bus voltage within 1e-9 pu and 1e-9 degrees (fails on 2.0.148).

## [2.0.150]
### Added
- Two-terminal line-commutated HVDC links (PSS/E two-terminal DC records) are solved with the AC power flow. Each
  converter follows PSS/E's two-terminal model: DC voltage from its AC voltage, transformer ratio, tap, bridges, firing
  (rectifier) or extinction (inverter) angle and commutating reactance; the inverter at its minimum extinction angle
  with its tap bringing the compounded DC voltage nearest the schedule; a power order at either end or a current order;
  the rectifier's angle and tap within their limits, handing the current to the inverter (less the margin) when it
  cannot reach the voltage; a power order becoming a current order below the mode-switch voltage. The converters draw
  and deliver their DC power and absorb their reactive power at their AC buses, alternating with the AC solve until both
  settle. An order no angle and tap can meet makes the solve not converged, naming the link. The case report prints
  each link's state. Such files are no longer refused (capacitor-commutated links still are).
- Docs: manual chapters 2 and 19.
- Tests: every solved DC state recomputed from the converter equations at the solved AC voltages and taps (power order
  at the rectifier and the inverter, current order, the rectifier at its minimum angle, the switch to current control
  below VCMOD), the AC buses carrying the converters' powers, the inverter tap nearest the schedule, dense and sparse
  Newton bit-identical, MATPOWER's lossless DC links delivering their orders, a link no state can satisfy, and malformed
  records. Existing tests changed: the ones that pinned the refusal of plain two-terminal links now pin their model (or,
  for the refusal checks, use a capacitor-commutated link), MATPOWER's revision 33 file is compared with its MATPOWER
  case without its links, and the list of model properties copied includes the links.
### Changed
- A model with HVDC links is solved by a Newton engine; the backward/forward sweep refuses one, and the MATPOWER, JSON
  and PSS/E writers refuse to write one, rather than drop the links.

## [2.0.151]
### Changed
- `inertia-placement`: the north-south oscillation is now measured over every half-cycle of the run, after a 0.4 s moving
  average that removes local machine swings, instead of from its first two peaks. The two-peak measure latched onto a
  2.5 to 3.1 Hz local swing in some gb-full runs and read it as negative damping (fleet -0.085 and demand -0.098 at 120
  GVA.s for the Scotland loss); measured over the whole swing those runs read 0.1805 at 0.953 Hz and 0.1769 at 0.952 Hz,
  and every gb-full run reads positive damping. A new column gives the half-cycles fitted; fewer than four reads `NaN`.
- Tests: a damped 0.6 Hz swing read through a 2.5 Hz ripple, a growing swing reading negative, too few half-cycles NaN.

## [2.0.152]
### Added
- VSC HVDC links (PSS/E VSC DC records) are solved with the AC power flow. One converter holds the DC voltage and the
  other an active power order in either direction; converter losses (a constant plus a term in the DC current, at
  least a minimum) are taken from the AC side and the DC line loss is R I^2. Each converter holds its AC bus voltage
  within its reactive limits (releasing the bus at a limit) or runs at a fixed power factor, and its MVA and AC current
  ratings are enforced at the solved voltage, reducing its order and reactive power by its power weighting fraction.
  The case report prints each link's current and both converters' powers, DC voltages and losses. Such files are no
  longer refused (a converter regulating a remote bus still is).
- Docs: manual chapters 2 and 19.
- Tests: the DC current, losses and power balance against the closed form (both directions, the minimum loss), the AC
  buses carrying the converters, a voltage-controlling converter at its reactive limit, the MVA rating met with each
  weighting, dense and sparse Newton bit-identical, the writers and the sweep refusing a model with links, malformed
  records, a remote-regulating converter refused and an out-of-service link skipped; and the AC side against pandapower
  3.5.6 (scipy 1.18.1, numpy 2.4.6, pandas 2.3.3) with the link as a DC line of the same loss: bus angles and converter
  reactive power agree within 1.1e-12 (angles in degrees, reactive power within 4.8e-12 Mvar). Existing tests changed:
  PSS/E 30's sample is no longer refused for its VSC links, and the list of model properties copied includes them.
### Changed
- The AC/DC alternation is shared by LCC and VSC links.
### Fixed
- The two-terminal LCC model used the platform's cosine and arc cosine, which can differ in the last bit between CPUs
  and operating systems; it now uses GridSim's deterministic maths, so its results are the same bits everywhere.

## [2.0.153]
### Added
- Multi-terminal line-commutated DC links (PSS/E multi-terminal DC records) are solved with the AC power flow: the
  converters (the two-terminal converter equations) on a resistive DC network with its line and grounding resistances.
  The voltage converter runs at its minimum angle with its tap bringing its DC voltage nearest the schedule; the others
  hold their power or current orders (rectifiers positive, inverters negative); the voltage converter takes the current
  that balances the network. Each converter's angle follows from its DC bus voltage and current within its limits. The
  mode switch voltage, the alternate voltage converter, the current margin and the participation factor are not applied:
  a converter that would need them makes the solve not converged, naming it. The case report prints each converter.
  Such files are no longer refused (a converter connected to two DC buses still is).
- Docs: manual chapters 2 and 19.
- Tests: Kirchhoff's current law and Ohm's law on the DC network, the orders, the voltage converter's tap nearest its
  schedule, each converter's angle from the converter equation, the AC buses carrying the converters and the DC power
  balance; a current-order link; a state no angle and tap can reach; dense and sparse Newton bit-identical; malformed
  records; PSS/E 30's own four-converter link read. Existing tests changed: PSS/E 30's sample is no longer refused for
  its multi-terminal link, and the list of model properties copied includes the links.


## [2.0.154]
### Added
- Docs: the inertia placement study results on `gb-full` (`docs/validation/studies/inertia-placement.md`): method, the regional
  RoCoF penalty and full-loss worst bus RoCoF per placement and budget, the smallest budgets meeting each criterion,
  the north-south swing damping, sensitivity runs, limits and how to reproduce them; linked from manual chapter 19.
- Docs: the backlog gains a status and plan section (done since 2.0.99, the queued work in order, the long-term
  direction) and its HVDC, unit commitment, Python API and native-format entries show what is delivered.

## [2.0.155]
### Added
- FACTS devices (PSS/E FACTS device records, modes 1 to 3) are solved with the AC power flow: the shunt element holds
  its bus at VSET within its current rating (over the rating it gives the most the rating allows and the bus is
  released); a STATCOM is the shunt element alone; a UPFC delivers PDES + j QDES to its terminal end bus and takes PDES
  from its sending end bus, its series voltage, series current and bridge power checked against VSMX, IMX and TRMX and
  its terminal end bus against VTMN..VTMX (outside any of them the solve is not converged, naming the device); a
  bypassed series element (mode 2) and a constant series impedance (mode 3) are branches between the two buses. The case
  report prints each device. Such files are no longer refused; modes 4 to 8 and a FACTS device regulating a remote bus
  still are.
- With `--shunt-control`, switched shunts with continuous voltage control (MODSW 2) are static var compensators: the
  susceptance varies continuously across their blocks, staying at BINIT while the bus is in its band and otherwise
  holding the band edge (or the single set-point when VSWHI equals VSWLO), fixed at the end of its range when that is not
  enough. The case report prints each one.
- Docs: manual chapter 2.
- Tests: each solved UPFC quantity recomputed by hand from the solved voltages, the network side against pandapower
  3.5.6 (scipy 1.18.1, numpy 2.4.6, pandas 2.3.3) with the device as its injections (bus angles and voltages within
  1e-8), a current-limited shunt element on its rating, a STATCOM held and limited, the series branches of modes 2 and 3,
  each UPFC limit, dense and sparse Newton bit-identical, the refusals, PSS/E 30's own STATCOM and UPFC read; static var
  compensators holding the band edge, fixed at their maximum (bit-identical to the fixed-shunt solve), staying at BINIT
  in band and holding a single set-point. Existing tests changed: PSS/E 30's sample is no longer refused or noted for its
  FACTS devices, the switched shunt row for an unswitched mode uses MODSW 3 (MODSW 2 is now modelled), and the list of
  model properties copied includes the new devices.
### Fixed
- An HVDC converter at a bus whose load varies with voltage (a constant-current PSS/E load) had its power scaled with
  the load; it now stays the converter's constant power (a 200 MW converter beside a 10 MW + 20 MW constant-current
  load: bus injection -229.833 MW instead of -228.718 MW at the solved voltage).

## [2.0.156]
### Added
- Induction machines (PSS/E induction machine records of custom design with a single cage) are solved with the AC power
  flow: each runs at the slip on its stable branch that meets its schedule (shaft power out, or electrical power in),
  drawing the power its equivalent circuit gives at the solved voltage; a schedule beyond its peak power makes the solve
  not converged, naming the machine. The case report prints each machine's slip and powers. Such files are no longer
  refused; machines with a standard design code, a double cage or saturation still are.
- Docs: manual chapters 2 and 19.
- Tests: the solved slip and powers against the equivalent circuit written out with complex numbers (shaft power and
  drawn power within 1e-12), the bus carrying the machine, a generator at negative slip, an electrical schedule, a rated
  voltage other than the bus base, a schedule past the peak, dense and sparse Newton bit-identical, the writers and the
  sweep refusing a model with machines, and the refusals. Existing test changed: the list of model properties copied
  includes the machines.

## [2.0.157]
### Added
- With `--tap-control`, PSS/E transformers regulating the reactive power through them (COD 2, by tap) or the active
  power (COD 3, phase shifters, by angle) are stepped in the solve's tap loop until the flow into the transformer is in
  its band; one that runs out of range is named as not settled.
- With `--shunt-control`, switched shunts with blocks of different sizes switch one block at a time in the order listed,
  and switched shunts regulating a remote bus hold that bus in their band (a remote bus a generator holds stays noted,
  not switched).
- Docs: manual chapters 2 and 19.
- Tests: a phase shifter and a reactive power tap each stopping at the first position whose plain solve puts the flow in
  its band (bit-identical to that solve for the phase shifter), a band out of reach named at its limit, the refusals;
  unequal blocks bit-identical to the fixed-shunt solve at the chosen level, block levels counted from BINIT, a remote
  regulated bus brought into band bit-identically, a generator-held or undefined remote bus. Existing tests changed: the
  "not applied" transformer control check uses COD 4 (COD 2 is applied now) and its note names COD 4 and 5; the
  switched shunt row for unequal blocks now checks a BINIT that is none of their levels; the list of model properties
  copied includes the flow controls.

## [2.0.158]
### Added
- PSS/E sequence data: a machine's zero-sequence impedance is applied as a grounded neutral (a shunt to earth at its
  bus, corrected by K_G and scaled by Method C like X''d), and zero-sequence mutual coupling between whole lines is
  applied to the zero-sequence network (each group of coupled lines from the inverse of its impedance matrix); the
  fault results' line zero-sequence currents include the coupling. Such files are no longer refused. Coupling over part
  of a line, sequence shunt loads and zero-sequence switched shunts still are; three-winding records and connection
  codes 5 to 9 stay refused (no definition of their networks to check against).
- The case writers refuse a model carrying machine zero-sequence impedances or mutual couplings rather than drop them.
- Docs: manual chapters 2 and 19.
- Tests: the zero-sequence driving point with a mutual coupling against a nodal network built by hand (1e-12), the
  record directions setting the sign, the induced voltage on an unloaded coupled line equal to Zm times the other line's
  current, a machine's zero-sequence impedance as the only earth at its bus. Existing tests changed: the UKGDS-style
  sequence file reads without the unmodelled allowance (its grid infeed's zero-sequence impedance is applied), so the
  hand-worked bus 1 driving point now includes that impedance in parallel, the refusal checks use a zero-sequence shunt
  load and a partial coupling, and the list of model properties copied includes the couplings.

## [2.0.159]
### Added
- Batched DC power flow (`DcPowerFlow.SolveBatch`, library API) for Monte Carlo and scenario sets: models whose DC
  susceptance matrix has one sparsity structure are factored and solved four at a time in SIMD lanes, with batches in
  parallel. Each result is bit-identical to `DcPowerFlow.Solve` for that model, at any thread count and with hardware
  intrinsics on or off. 32 reactance-varied gb-full models: 220.5 ms one at a time, 108.6 ms batched on 8 threads.
- Opt-in diagonal-preference pivoting for the sparse LU (threshold partial pivoting, off by default; default results
  unchanged): `DcPowerFlow.Solve(model, diagonalPreference)`, `SolveBatch(..., diagonalPreference)` and
  `FixedPatternSparseLuSolver` for the sparse Newton power flow. It keeps the pivot order stable across a family of
  matrices, so the lanes share one factorisation plan (32 gb-full DC factorisations: 60.0 ms alone, 20.1 ms in lanes)
  and Newton reuses its plan between iterations (gb-full power flow 98.2 ms to 51.2 ms; voltages within 2.4e-13 pu of
  the default). Results agree with the default solve to rounding, not to the bit.
- Docs: manual chapter 28 (multi-case lanes, measured timings, why plain partial pivoting cannot share a plan).
- Tests: a replayed factorisation holds the bits of a fresh one (random matrices, both pivot rules) and is refused when
  a pivot moves; every lane of a multi-case batch holds the bits of its single-case factorisation and solve, including
  lanes that fall back (a moved pivot, a dropped zero, a singular matrix) and a singular template; a matrix outside the
  family is refused by name; batched DC results bit-identical to single solves on case118, case300 and gb-full, across
  structure families, thread counts and the opt-in rule; a model the DC model cannot represent fails the batch naming
  its position; the opt-in Newton solver equals a fresh structural factorisation at every step and converges to the
  default answer.

## [2.0.160]
### Added
- `gridsim convert` writes PSS/E RAW (an output ending in `.raw`, revision 33) and proves it as it proves MATPOWER and
  JSON: the file is read back with tap and shunt control applied and must solve to the same bits. RAW holds lines and
  transformers in separate sections, so the read-back is compared with the input in the file's order, and a second line
  says whether the input in its own order also gives the same bits.
- The PSS/E RAW writer writes the devices the RAW reader models: two-terminal LCC, VSC and multi-terminal DC links,
  FACTS devices (STATCOM, UPFC, constant series impedance), static var compensators, induction machines, on-load tap
  changers (COD 1) and transformer flow controls (COD 2 and 3). A tap changer or flow control whose numbers the reader
  cannot give back to the bit (for example a band of 1.0 +/- 0.01 pu, which VMA and VMI cannot express exactly) is left
  at its present tap and named in the notes, so the proof shows the difference.
- Docs: manual chapter 19 (convert to RAW).
- Tests: every modelled device type round-trips through the writer in revisions 33, 34 and 35 to the same records and
  power flow bits; an inexact tap changer band is listed and not written, an exact one round-trips; a name with a
  single quote is written in double quotes; `convert` to `.raw` proves case14. Existing tests changed: the FACTS, flow
  control, induction machine, VSC and LCC tests that pinned the RAW writer's refusal now check that the written file
  reads back to the same device records (the writer carries them now).

### Changed
- The PSS/E RAW writer writes a name holding a single quote in double quotes instead of refusing it.

## [2.0.161]

### Fixed
- Live C37.118 TCP receive (`pmu-ingest --live`) connects on a thread of its own, so a busy machine can no longer
  report a device that answered as "no connection within N s".
- Tests: the UDP sender-filter test sends its datagrams before the receive starts instead of from a background task
  that could start after the window closed; the RP1 page test pins both the scroll host and the page body of the
  desktop view to the page-visibility rule (the body moved into a scroll host in 2.0.113).

## [2.0.162]

### Added
- Tests: GPU kernel tests that run only where the accelerator is present (CUDA and OpenCL each: batched dense LU against
  the CPU batched solver, singular-system isolation in the batch, dense LU, near-singular pivot refusal, sparse solve).
  Without the device each is reported skipped with the reason, so a pass means the kernels ran on the device; the
  existing hardware-free GPU tests are unchanged. The CUDA batched kernel had no test before.

### Fixed
- `tools/run-gpu.sh` and the GPU chapter no longer call case300 a dense-path comparison: the export takes the dense
  engine only up to 200 buses, so case118 is the only GPU dense LU comparison and case300 compares the sparse engine.

## [2.0.163]

### Added
- Preventive security-constrained DC optimal power flow (`DcScopf`): the cheapest dispatch that keeps every rated
  branch within RateA before any outage and within its post-outage rating (RateA, or RateB / RateC where given) after
  each single-branch outage, solved as a sparse LP on bus angles with the outage limits added only when the dispatch
  breaks them. Outages that would island the network are listed and not secured; a named one is refused. Reports the
  dispatch, cost, base flows, nodal prices and the binding pre- and post-outage limits; an infeasible security set or
  the round cap is reported, never dispatched. No CLI verb yet.
- Docs: technical chapter 29 (preventive SCOPF formulation, constraint generation, validation, limits).
- Tests: the preventive SCOPF against hand calculation (three-bus dispatch, cost, prices and emergency rating), an
  independent per-outage-state formulation on the Wood and Wollenberg six-bus network and case30, the DC-OPF when
  nothing is secured, post-outage flows re-solved with the branch removed, the full LP, repeatability and refusals.

## [2.0.164]

### Added
- `scopf` verb: the preventive security-constrained DC OPF from the command line. Secures every non-islanding branch
  outage (or a listed set) at post-outage rating A, B or C, on fuel-type costs or the units' linear cost curves
  (`--linear-costs case|<file.json>`; a quadratic or piecewise-linear curve is refused), with constraint generation or
  the full LP (`--full-lp`). Prints the secured and islanding outages, the LP size, the total cost, the nodal price range,
  the dispatch and the binding limits; `--json`, `--csv` and `--out` write the result. Exit 3 when no secure dispatch
  exists. The Python package lists `scopf` among the verbs it wraps.
- Docs: manual chapter 19 (`scopf`), technical chapter 29.
- Tests: the built `scopf` verb against the library bit for bit (constraint generation and full LP), an infeasible
  security set, the refusals and bad flags; the writer-verb list test names `scopf`.

## [2.0.165]

### Added
- `convert` writes CGMES: an output ending in `.xml` is written as one CGMES RDF/XML file (buses with base voltage and
  starting voltage, demand, fixed shunts, in-service generators with voltage control and limits, lines and two-winding
  transformers with ratio and phase shift, rate A as a permanent apparent-power limit) and proved by reading it back
  through the CIM reader and solving both. A value that has no CGMES form converting back to the same bits (ohms,
  siemens and kV do not reach every per-unit double) is written as the nearest one, counted in a note by kind with the
  first named, and the proof then exits 3; a model already read from CIM converts exactly. Devices the CIM reader does
  not rebuild into the solved model (on-load tap changers, flow controls, HVDC links, FACTS devices, static var
  compensators, induction machines), ZIP loads and buses with no base voltage are refused with every reason listed and
  nothing written.
- CIM import options `StartingVoltages` (read `SvVoltage` as each bus's starting voltage) and `ApparentPowerRatings`
  (read permanent `ApparentPowerLimit` values as rate A). Both are off by default, so existing imports are unchanged.
- Docs: manual chapter 19 (`convert` to CGMES).
- Tests: CGMES round trips to the same records and bits for a model CGMES holds (case30, case118), the first pass of a
  MATPOWER case within one ulp per value with the count reported, refusals listed together, the import options read
  only when asked, a ratio written as a table when the linear changer cannot give it back, and the built `convert` verb.

### Changed
- The CIM exporter writes every transformer ratio other than exactly 1 (a ratio within 1e-12 of 1 used to be dropped),
  and writes a ratio the linear tap changer cannot give back to the bit as a one-point ratio table.

## [2.0.166]

### Added
- `convert ... --public [--provenance <manifest.json>]`: a public export writes only what a `gridsim-provenance/1`
  manifest traces to published sources (buses by id, branches and generators by name, optionally demand, shunts and
  on-load tap changers). Unsourced elements are stripped, unsourced demand, shunts and layout are zeroed, a PV bus left
  with no generator becomes PQ, and devices the manifest has no section for are stripped; every strip is printed and
  written to the result file. The manifest defaults to `provenance.json` in the case folder or
  `<file>.provenance.json` beside a case file. No manifest, a manifest for another case, or a public model with no
  usable slack bus exits 1 with nothing written; `gb-full` has no manifest, so its public export is refused.
- Docs: manual chapter 19 (`convert --public`).
- Tests: the public export gate on a synthetic manifest over the IEEE 9-bus case (all published, elements stripped and
  zeroed, refusals, manifest validation) and the built `convert --public` verb, including the refusal for `gb-full`.

## [2.0.167]

### Security
- The licence signing key pair is replaced. Builds from this version verify only keys issued with the new key;
  every earlier key, including all 1.x keys, is refused with `licence signature is invalid` and the build falls back to
  the free tier. Earlier GridSim builds are unaffected.
- Docs: technical chapter 22 carries the new embedded public key.

## [2.0.168]

### Fixed
- The direct linear solvers refuse a NaN or infinite right-hand side, naming the row, instead of returning a NaN or
  infinite solution as if it were an answer: the dense LU (CPU and GPU), the CPU sparse LU and the GPU sparse solver
  (which used to return no solution and leave the caller to fall back). The GPU dense LU also refuses a NaN matrix
  entry, as the CPU dense LU does. The checks only read the input, so every solve of a finite system is unchanged.
- Tests: the refusals for each solver (NaN, +Inf and -Inf, on the device or its CPU fallback), a bad matrix keeping
  its own refusal, a singular sparse matrix still reported as singular, finite solves bit for bit, and the batched
  dense solver flagging only the system with the bad right-hand side.

## [2.0.169]

### Fixed
- Docs: the validation-tools chapter's `crossval_freq.py` example ran `replay-event` on a curated screening event,
  which is refused; it now cross-checks the `scenario` counterfactual of that event, with the output of that run. The
  exports chapter no longer says the `gb-2019-08-09` replay export is checked: it carries recorded response services, so
  the script reports it not checked.
- `tools/crossval/mc_estimate_sweep.py` takes `--cli <path>` (or `GRIDSIM_CLI`) for the CLI to run, a `.dll` through
  `dotnet`, and `--out <file>` for the report, so it runs against a published CLI or a read-only tree. The defaults are
  unchanged.
- `tools/docs/capture.ps1` passes a double-quoted argument holding spaces as one argument, expands `$NAME` at the start
  of a longer argument (a path under `$GDA_ROOT`), and takes `-OutDir` to write the captures outside `docs/_captures`.
- Docs: configuration chapter (capture script), validation-tools chapter (`mc_estimate_sweep.py` options).

## [2.0.170]

### Added
- Docs: "gb-full through every writer", what the JSON v2, MATPOWER, PSS/E RAW and CGMES writers carry, note and refuse
  for the full GB case, with the counts and the size of the difference when a format drops the tap changers.

### Changed
- When `convert`'s proof fails it names the largest voltage difference (|dVm| and |dVa| with their buses) and how many
  buses differ, as well as the first difference in bus order, which can be a last-bit one while the case moved a long
  way. The result file carries it as `largest_voltage_difference`.

### Fixed
- The CGMES writer's refusal counts the buses whose machines hold different set-points, and the slack buses with no
  generator, on one line each with the first five named, instead of one line per bus.
- Tests: the counted refusal line, and the largest voltage difference in a failed proof.

## [2.0.171]

### Added
- Docs: gb-full licence and provenance audit (assurance): what the case files record about their sources, the element
  classes that are not published data (synthesised supergrid transformers, the power-flow-only slack and voltage-control
  units, layout coordinates, generic tap-changer settings) with their counts, and why a public export of gb-full is
  refused. No data is changed.

## [2.0.172]

### Added
- WECC second-generation renewable models in the RMS study: REGC_A (generator/converter, with LVPL, LVACM, HVRCM and
  the current rate limits) and the electrical controls REEC_A (voltage-dip logic with the Iqfrz/Thld state machine,
  Thld2, VDL1/VDL2 current limit tables, P or Q priority), REEC_B and REEC_C (battery state of charge). Each start is
  checked: a unit that cannot hold its power-flow point is refused with the reason, and every state must be at rest
  to 1e-9.
- WECC REPC_A plant controller in the RMS study: voltage or reactive power control at a regulated bus (line drop
  compensation or reactive droop, freeze below Vfrz), and primary frequency response with separate up and down droops
  and a deadband. The plant output can be the flow of a named branch; the regulated bus voltage is in the converter
  trace. The time constant of its frequency measurement filter is a parameter (default 0.02 s); it changes no steady
  state.
- WECC wind turbine models in the RMS study: the WTGT_A two-mass drive train (type 4 torsional ripple), and for type 3
  turbines the WTGAR_A aerodynamics, WTGPT_A pitch controller and WTGTRQ_A torque controller with its power-speed
  table. The drive train's torsional mode is integrated exactly, so it neither grows nor decays numerically.
- DER_A aggregated distributed energy resource model in the RMS study: voltage and frequency control, current limit,
  storage mode, partial voltage tripping with partial recovery (Vrfrac) and frequency tripping.
- WECC REGC_B voltage-source generator/converter model in the RMS study (source behind re + jXe with its Te lags, the
  RateFlag current or power recovery ramp, and the converter current limit at the network interface). Its network
  interface is solved exactly with the network, so it converges on weak connections and with several units, and its
  source follows the terminal voltage angle of the same solution, so an off-nominal frequency does not bias its power.
- WECC REEC_D electrical control in the RMS study: ten-point voltage dependent current limits (negative reactive limits
  and the Ke charging limit), local current or reactive droop compensation, the post-dip reactive and active holds, and
  converter blocking with a release delay.
- REGFM_A1 droop grid-forming inverter model in the RMS study: P-f and Q-V droop, active and reactive power limit
  controls, terminal or internal voltage control, and the fault current limit.
- PSS/E DYR: REGCA1, REECA1, REECB1, REECCU1, REPCA1, WTDTA1, WTARA1, WTPTA1 and WTTQA1 records (also under their V33
  user-model and WECC names, and as USRMDL records) give a unit its WECC model chain for `stability --rms --dyn-data`;
  REPCA1's regulated bus and flow branch are checked against the RAW case; every value is range-checked and the file is
  refused with the full list of problems. A DYR file may give only renewable units.
- PSS/E DYR: 'DER_A', 'REGC_B', 'REEC_D' and 'REGFM_A1' records in the orders of their public specifications; PSS/E
  DERA1, DERAU1, REGCB1, REECDU1 and REGFMA1 records are refused with the reason (their record orders are not public).

### Changed
- PSS/E DYR: a REGC_A record is now read as a model instead of being refused; a unit with both a machine record and a
  renewable record is refused.

### Fixed
- RMS study: a step where the converter network iteration of WECC REGC_A units does not settle is re-solved exactly with
  the network instead of being reported as unconverged; steps that settle are unchanged.
- Tests: one DYR refusal test now pins the REGC_A messages of the modelled converter.

## [2.0.173]

### Added
- Quasi-static time series engine (`GridSim.Core.Qsts`): a time-stepped sequence of warm-started power flows over load,
  generation, PV and storage profiles with controllers that act in time: on-load tap changers (deadband, definite or
  inverse delay, continuous or sequential timing, coordination, line drop compensation), switched capacitors and
  reactors (voltage, var or time control, delays, dead time), inverter volt-var, volt-watt and fixed power factor (IEEE
  1547-2018 curves, response time, var or watt priority) and storage dispatch (schedule, peak shaving,
  self-consumption, SOC limits, efficiencies). Every operation is logged and counted.
- `qsts` verb: quasi-static time series of a case over a `gridsim-qsts/1` study file (profiles, PV, storage, added
  loads, OLTC timing, capacitor banks, limits), with per-step results, the operation log, energy totals and
  duration-weighted voltage and loading violations through `--json` / `--csv` / `--out`. A study can make generation
  follow load (`"generationFollowsLoad": true`, the re-dispatch `chain` uses).
- `hosting` verb and `GridSim.Core.Qsts.HostingCapacity`: hosting capacity for PV or added load at each candidate bus by
  bisection, snapshot or time-series method, judged on over- and under-voltage, thermal loading, reverse power through
  the source branch, tap-operation increase and convergence, with the limiting constraint per location; base-case
  violations are not blamed on the resource.
- Technical chapter 37: quasi-static time series and hosting capacity. User manual chapter 34: time series and hosting
  capacity, with worked `qsts` and `hosting` examples.
- Study: GB summer minimum demand high-voltage study on `gb-full` (hotspots, the reactive absorption needed and where,
  and reactors, inverter volt-var and synchronous condensers compared), with its script
  `tools/studies/summer_minimum_voltage.py`.

### Changed
- Technical chapter 11 lists the QSTS cross-check against OpenDSS.
- Tests: QSTS engine, control, CLI process and hosting-capacity tests; QSTS cross-checked against OpenDSS (tap and
  capacitor operations step for step, voltages within 1e-10 pu) with the reference script
  `tools/crossval/crossval_qsts_opendss.py` and its data in `data/qsts`; a QSTS digest pinned across SIMD tiers; the
  writer-verb list test includes `qsts` and `hosting`.

## [2.0.174]

### Fixed
- `qsts` (timed mode): a step with many coupled volt-var or volt-watt inverters (or peak-shave and self-consumption
  batteries) no longer stalls unsettled. When the per-unit damped iteration reaches `maxContinuousIterations` without
  settling, an accelerated iteration over all continuous set points together (Anderson acceleration, deterministic)
  takes over for the rest of the run with a fresh cap of the same size, under the same settle test. Runs that the
  per-unit iteration settles are unchanged bit for bit. The GB summer minimum-demand volt-var run on `gb-full` (about
  1,900 units) now settles at the default tolerance; the study document and script use the settled run.
- Tests: coupled volt-var units on a synthetic feeder settle on their curves after the per-unit cap (caps 200 and
  1000), with the same bits across runs, threads, dense and sparse Newton, and a digest pinned across SIMD tiers; a
  self-consumption battery among them settles on its request.

## [2.0.175]

### Fixed
- `crossval` run from a release folder (or any copy of the binary outside the source tree) no longer stops with
  "could not find tools/crossval/crossval.py": the build and the publish now ship the pandapower sidecar
  (`tools/crossval/crossval.py` and `sidecar_common.py`) beside the executable, where the verb looks first.
- Tests: the built CLI carries the sidecar byte for byte; a copy of the CLI outside the source tree finds it; the
  published-output check requires it in the release folder.

## [2.0.176]

### Fixed
- User manual ch. 22 and ch. 30: the `gda --fleet` missing-register example is now captured on a lake root that has no
  TEC register (`GDA_ROOT_NO_TEC`), and the text says what a lake holding the register only as Parquet gets (exit 1,
  with a message naming the Parquet copy). The `gda-fleet` and `gda-fleet-missing` captures were the same command and
  both showed a message the current build no longer prints; each is re-captured.
- `tools/docs/capture.ps1` takes `-Capture <name>` to re-capture one line.

## [2.0.177]

### Changed
- CI: the `fma-off` matrix legs are removed. .NET 10 ignores `DOTNET_EnableFMA=0`, so those legs re-ran the normal
  vector tier and verified nothing. The non-FMA axis of the bit-identity rule is held by the `avx2-off` legs
  (`DOTNET_EnableAVX2=0` turns FMA off with the rest of the x86-64-v3 group), whose preflight now also requires
  `simdcheck` to report `Fma=False`. Technical chapter 35 says so.
- Tests: the release-gate test pins the `avx2-off` legs, their `Fma=False` preflight and the absence of the ignored
  switch, instead of the removed `fma-off` legs.

## [2.0.178]

### Fixed
- `gda --fleet`: the NESO registers (TEC, interconnector, embedded, 24-month constraint limits) are also found under
  `NESO/Processors/RAWData`, where the lake's NESO acquire step writes them; `NESO/RAWData` still wins when it exists,
  and with neither present the error is unchanged.
- Tests: the register lookup in the acquire tree, the precedence of `NESO/RAWData`, and the unchanged Parquet-only error.

## [2.0.179]

### Changed
- Docs: the `gb-full` README and manual chapter 2 describe the case as assembled from published data plus synthesised
  elements, drop the internal export paths, and note that `gb-full` is not a public-export model.

## [2.0.180]

### Added
- PSS/E RAW: several FACTS shunt elements and static var compensators may hold one bus. The bus is held at their common
  set-point and the reactive power is shared by each device's RMPCT (the FACTS and switched shunt RMPCT fields are now
  read and written); a device whose share passes its current rating or susceptance range is fixed at that limit and the
  others carry the rest. Conflicting set-points on one bus are refused (or fail the solve, naming both devices), and a
  shared bus needs a positive RMPCT on every device. A bus also held by a generator or a VSC converter is still refused.
- Tests: RMPCT sharing between two STATCOMs, a STATCOM and a compensator, devices at their limits, the refusals and the
  RAW round trip, checked against the hand value of the reactive power a held bus sends into its line.

### Changed
- Tests: the PSS/E 30 sample test no longer pins the refusal of two shunt elements on bus 153; it asserts that both
  devices read with their RMPCT and that the solve still fails loud for the sample's multi-terminal DC control.

## [2.0.181]

### Added
- RMS dynamics: the GAST gas-turbine governor (droop, fuel valve lag with limits, turbine lag, exhaust temperature
  load limit through a low value gate, turbine damping). A `gridsim-dynamics/2` governor template takes `model`
  `GAST` with `droopPu`, `gastValveTimeS`, `gastTurbineTimeS`, `gastExhaustTimeS`, `gastAmbientLimitPu`,
  `gastTemperatureGain` and optional `valveMinPu`, `valveMaxPu`, `turbineDampingPu`; the PSS/E DYR reader maps GAST
  records (R T1 T2 T3 AT KT VMAX VMIN DTURB) onto it. A unit whose initial power is above AT or outside its valve
  limits is refused, since it cannot start at equilibrium.
- RMS dynamics: the EXAC1 and ESAC1A alternator-rectifier exciters (IEEE AC1 / AC1A): regulator with lead-lag and
  limits, rate feedback, exciter with quadratic saturation, demagnetising term KD and the rectifier regulation function
  FEX, which read the machine's field current from the RMS engine. A `gridsim-dynamics/2` exciter template takes
  `model` `EXAC1` or `ESAC1A` with the `ac...` parameters (all required); the PSS/E DYR reader maps EXAC1 records (TR 0).
  ESAC1A records are not read from DYR files, since their PSS/E parameter order is not publicly documented.
- Tests: GAST against the closed form of its cascaded lags, its temperature and valve limit steady states, its
  refusals and the DYR mapping; EXAC1 / ESAC1A against hand values of FEX and the saturation fit, the closed form of the
  linear case, a bisection-solved operating point, the ESAC1A limits, the DYR mapping, and a reduced EXAC1 matching
  IEEET1 to the bit through a whole RMS study.

## [2.0.182]

### Added
- RMS dynamics: the ESST4B static exciter (IEEE ST4B): power source voltage from the terminal voltage and current
  (KP, THETAP, KI, XL) with the rectifier regulation FEX and its ceiling VBMAX, an outer voltage PI regulator with lag
  TA and an inner field-voltage PI regulator with feedback KG. The RMS engine passes it the terminal phasors and the
  field current, and the study binds the machine base it reads the current on. A `gridsim-dynamics/2` exciter template
  takes `model` `ESST4B` with the `st4b...` parameters (all required); the PSS/E DYR reader maps ESST4B records
  (TR 0). A unit with a unit transformer folded into its reactances, or with no integral gain in either regulator, is
  refused.
- Tests: ESST4B against complex arithmetic done by hand for its source voltage, a stationary start, the steady states
  its integrators force, the VBMAX ceiling, a case 9 study, its refusals and the DYR mapping.

### Changed
- Tests: the DYR test of unmapped models uses ESST1A as its example in place of ESST4B, which is now mapped.

## [2.0.183]

### Added
- A static code review report of the core library and the command line at 2.0.182 (findings ranked by severity, with
  file and line). No code change.

## [2.0.184]

### Fixed
- RMS dynamics: machine damping D, given on the machine's own base as in PSS/E GENROU and GENSAL data, is converted to
  the system base like the inertia. A unit whose MBase differs from the system base had its damping torque wrong by
  MBase / BaseMva (a 600 MVA unit on a 100 MVA base had six times too little).
- Power flow: when a branch opening splits the grid and an island needs a new reference or is blacked out, HVDC links,
  FACTS devices, static var compensators, induction machines, transformer flow controls and zero-sequence mutuals stay
  in the solve. They used to be dropped, so the network was solved without them and reported converged. A device on a
  de-energised bus leaves the solve with its island and is named in the island report.
- Frequency dynamics: a multi-domain swing that diverges fails with an error naming the time and the domain, instead of
  returning a trace of non-finite values as a result.
- Dispatch: the hyphenated technology names `wind-onshore`, `wind-offshore` and `pumped-storage` (the only spellings the
  case schemas allow) take their own default capacity factors instead of the 0.40 used for other plant.
- Scheduling: a cycle window of 0 (no deadline) no longer gives a zero compute budget, so every Low job is admitted
  each cycle. Two analysis jobs with the same id are refused.

### Changed
- Tests: bug-hunt regression tests for the fixes above.

## [2.0.185]

### Changed
- Impedance stability: the SISO criterion (the default) pairs the converter's positive-sequence admittance with the
  grid impedance in the same (dq) frame, Z(f + f0), and winds the positive- and negative-sequence loops together, so it
  gives the MIMO verdict whenever the converter has no cross-sequence coupling. It used to pair the converter's
  dq-frame admittance with the grid impedance at f, which leaves out the grid's reactance at the fundamental in the PLL
  band. SISO verdicts, margins and critical SCRs change; the exported `gridImpedance` is Z(f + f0). The NumPy
  impedance cross-check follows the same criterion.
- Tests: the real-network SISO test reads the network's driving point at f + f0; new tests compare SISO with MIMO on a
  converter with no cross-sequence coupling.

## [2.0.186]

### Changed
- Documentation: the impedance stability chapters, the CLI reference and the exports chapter quote the SISO
  sequence-domain loop (grid impedance at f + f0 and f - f0) and its new captures: the `case9` and `case14` margins, the
  300 Hz PLL boundary at SCR 1.12, the UNSTABLE weak-grid example with its MIMO comparison, the real-network contour
  warning, the worked 5 Hz loop gain and the NumPy cross-check output.

## [2.0.187]

### Fixed
- Linear programming: the sparse bounded simplex checks every Optimal point against its rows and bounds before
  returning it; a point that breaks them is reported as a numerical failure, never as optimal. The documented
  phase-one infeasibility threshold now states the factor the solver uses.
- Mixed-integer programming: a node whose relaxation is whole but whose rounded, integers-fixed re-solve is infeasible
  is branched on instead of ending the whole search with no answer.
- Power flow: a switched shunt bank with unequal steps keeps its susceptance at step 0 when that step is not its
  zero level. A VSC converter in AC voltage control on the slack bus is refused. A non-finite start state makes the
  dense Newton engine report non-convergence, as the sparse engine does, instead of an internal-error exception.
- JSON cases: a case file whose whole content is `null` is refused instead of loading as an empty list; loader and
  harmonic-source refusal messages print numbers with a decimal point on every locale.
- Physics audit: Ohm's-law check (law 2) reports NotApplicable when no reported line flow lines up with a branch,
  and says how many were compared when only some do; audit detail texts use the invariant culture.
- Contingency analysis: violation texts use the invariant culture; a branch with a NaN rating is refused by name
  instead of scoring the outage as secure; an N-k or N-1-1 screen that can form no outage combination says why, and
  the CLI prints it.
- QSTS: a negative CSV column index is refused by name, and profile CSV files may quote fields (a quoted header such
  as "load, kW" no longer shifts the columns).
- Bus frequency measure: an event at the trace's second sample is refused by name instead of being dropped.
- CLI: the transient batch run-folder stamp is UTC like every other stamp.

### Changed
- Tests: bug-hunt regression tests for the fixes above.

## [2.0.188]

### Fixed
- Frequency screening: the RoCoF screen judges its explicit-Euler stability check and its closed-form RoCoF on the
  inertia the swing integrates on, including a fitted device's rotational inertia.
- Live frequency integrators: a step past the explicit-Euler damping bound is refused, as the batch swing refuses it,
  and a diverged swing throws instead of publishing a non-finite frequency.
- Inertia estimate: an inertia constant on a base that is not a finite MVA above 0 is refused instead of returning NaN.
- Three-phase EMT: a non-finite node voltage ends the run with an error instead of a NaN trajectory, and the network
  record is validated (bus and phase numbers, fault resistance above 0, fault timing) before anything is stamped.
- Distributed inertia: on the grid-forming and single-domain paths each event's placement is recorded, saying that a
  given location is not used.
- RMS critical clearing time: the bracket counts the trials whose converter iteration hit its cap, and the CLI warns
  when any did.
- RMS control blocks: SEXS, TGOV1, IEEEG1 and the hydro governor refuse a negative time constant under their own
  parameter names; 0 is the instantaneous block. A tripped machine's SEXS field is held on the legacy exciter path.
- PSS/E RAW writer: names that begin or end with a blank are listed in the notes (the reader trims them).
- MATPOWER writer: a PMIN of -Inf is written as -Inf instead of being refused.
- IEC 61850 GOOSE encoder: an APPID or PDU length that does not fit its 16-bit field is refused instead of wrapping.
- GB map layout: a point outside the layout box is flagged (the PyPSA-GB Shetland zone, the zonal verb's list and the
  reduced importer's notes) instead of being moved onto the edge silently.
- Bridge client: cancelling the connect token fails the pending queries at once, and a query timeout while the
  request is still being sent is reported as a timeout.

### Changed
- Documentation (code): the settle frequency of the swing family is stated as the value one step after the last trace
  point; the exciter lists name EXAC1, ESAC1A and ESST4B.
- Tests: bug-hunt regression tests for the fixes above.

## [2.0.189]

### Fixed
- Generator cost files: a model whose loader dropped generator rows is written with units addressed by name (the
  only form it can read back), a unit that cannot be named that way is refused, and a non-finite cost is refused by
  name. An index written as `1.0` is read as 1, as every other whole number in the JSON family.
- Case editor: an id or bus reference written as a whole number with a fraction part (`12.0`) is read as that
  number, so it is indexed, checked for dangling references and renamed like `12`.
- Bridge and solve-export model: a boundary with an empty branch list is kept, so the solve-export round trip keeps
  every boundary.
- v2 case writer: an element name that is empty or only blanks is refused by name instead of being written into a
  case the loader then refuses.
- Machine dynamics files: `unit` counts a bus's units in case order, out-of-service units included; a unit that is
  out of service is refused by name instead of the binding moving to the next machine.
- Storage fleet: the state-of-charge history kept for cycle counting holds only its turning points, so a long
  study's memory no longer grows by one value per sub-step; the cycle counts are unchanged.

### Changed
- Documentation (code): the CLI's positional-argument and missing-value conventions are stated where they are
  implemented.
- Tests: bug-hunt regression tests for the fixes above, and one pinning the existing refusal of a boundary branch
  name that two branches share.

## [2.0.190]

### Fixed
- Islands: an island whose units are all scheduled at 0 MW but can produce real power elects one of them as its
  reference instead of being blacked out; an island with only synchronous condensers is still blacked out.
- Backward-forward sweep: a generator or branch on a bus the model does not have is refused by name, as the Newton
  engines refuse it.
- Case editor: two network files of one case with the same file name in different folders are refused.
- v2 cases on Linux and macOS: file paths that differ only in letter case are two files, in the duplicate, library and
  pulled-network checks (Windows is unchanged).
- Solve-export import: the schema tag must be exactly `gridsim-solve-export/1`.
- PSS/E RAW: a COD 1 control whose positive CONT names the winding's own bus (regulated by PSS/E from the far side)
  is refused; the writer writes an OLTC on its own winding-1 bus with a negative CONT, so it reads back.
- MATPOWER: MATLAB `%{ ... %}` block comments are skipped; an unclosed block is refused.
- CIM: the import log lists a transformer whose rated-voltage correction cannot be applied (a bus with no base
  voltage) and the machines read as regulating because they give no control; the remote-control limit check uses
  the demand of the network it solved.
- QSTS: a load binding with an empty bus list is refused.
- UCTE: the refusal of inverted permissible-generation limits names the remedy.

### Changed
- Documentation (code): the stated conventions of the forming twin engines, the multi-domain inertia floor, GENROU
  saturation with a unit transformer, the REEC_A rate-limit order, the PSS/E impedance-table abscissa, CIM islands
  held only by remote controllers, bridge error messages, the storage state-of-power terminals, the double-line-to-
  ground ground current, the lost-power sharing note, the chi-square approximation error, the MBase-not-given
  readings and the forward-inference guard's deferred-now callers.
- Tests: bug-hunt regression tests for the fixes above.

## [2.0.191]

### Fixed
- gb-full counts as a bundled network again: the shipped-content manifest `data/bundled.sha256` carries the current
  hash of `gb-full/README.md` (the README was reworded without refreshing the manifest, so gb-full read as edited and
  its 3,539 buses were held to the licence tier's bus limit).

## [2.0.192]

### Changed
- Tests: the two desktop dashboard tests that drive case 9 at 100 times its load now expect the live engine to refuse
  each frame as a solver fault ("Solve failed at tick N"), since the live frequency integrator no longer publishes a
  non-finite frequency. The dashboard outcome they check is unchanged: the SOLVE FAULT lamp and colour, one alarm per
  frame, nothing on the map and no current frame.

## [2.0.193]

### Changed
- README: the OPF section lists `acopf-full` (the full AC optimal power flow with voltage, reactive and branch MVA
  limits), `scopf` (preventive security-constrained DC OPF) and `scuc` (security-constrained unit commitment), and the
  known-limits note on `acopf` points to `acopf-full`.
- The bug hunt report of 2026-10-29 is filed as `review-progress/bug-hunt-2026-10-29.md`, in plain ASCII.
- Tests: a GDA regression test's header comment no longer names the tool or run that found the defects.

## [2.0.194]

### Changed
- Documentation layout for the release: `docs/README.md` indexes the folders. The user manual (`docs/manual`), the
  technical reference (`docs/technical`) and the worked examples (`docs/_captures`) stay where they were. Validation
  evidence (cross-validation results, the solver validation report, the GB event reconstructions, the distributed
  inertia and NESO RP1 notes, and the studies) moved to `docs/validation`. Developer material (the documentation style
  guide, deployment examples, benchmarks and performance baselines, case libraries, the solve export format and the
  GPU rental runbook) moved to `docs/developer`. Every link and path that pointed at a moved file follows it.

### Removed
- Redirect stubs for pages that moved into the manuals long ago, and internal planning notes, are left out of the
  release tree.
- Tests: the project-version check skips any project under a hidden folder (local tool or editor state), in place of
  one named folder.

## [2.0.195]

Release candidate 1.0.

### Added
- `RC1.md`: the release notes of release candidate 1.0: the builds (Windows x64 and Linux x64), their SHA-256 hashes,
  system requirements for running and for building from source, how to build for macOS, and where to report bugs.
- README: bugs are reported to gdatooling@proton.me.
