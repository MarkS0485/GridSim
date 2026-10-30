# 1. Architecture

This chapter maps the codebase for an engineer who modifies or audits it: the projects in `GridSim.slnx` and the
dependency rules between them, the namespaces inside `GridSim.Core`, the seams (the interfaces and registries where an
alternative implementation plugs in), the end-to-end data flows, the cross-cutting rules that hold everywhere, and the
tests that pin them. It names types and files and leaves the mathematics to the subsystem chapters, which it links.

The shape is simple. `GridSim.Core` holds every capability: the model, the solvers, the estimator, the dynamics, the
protection layer, the importers and exporters, the evidence engine and the wire codecs. The CLI (`GridSim.Cli`) is the
only place that selects a compute backend and reads argv; the exporters in Core write the files and the CLI chooses the
paths. The two dashboards (`GridSim.Web`,
`GridSim.Wpf`) and the case editor (`GridSim.SchemaEditor`) are presentation only. `GridSim.Gpu` and `GridSim.Gda` are
leaves that hold the two things Core is forbidden to hold: device drivers and a Parquet reader.

The contract behind the shape is the one in the repository README: power-flow results are bit-identical across runs,
across parallel paths, across CPU instruction-set tiers and between the dense and sparse solvers, and a result that is not
converged or not valid is never reported as success. Each cross-cutting rule below exists to keep that contract.

## Projects

`GridSim.slnx` lists seven projects under `src/` and one under `tests/`. The dependency column is read from the
`ProjectReference` and `PackageReference` items in each `.csproj`.

| Project | Kind and target | Responsibility | Project references | Packages |
|---|---|---|---|---|
| `src/GridSim.Core` | class library, `net10.0` | The physics and every capability: model, power flow, sparse and complex numerics, estimation, dynamics, harmonics, protection, assets, storage, scenarios and evidence, IO, interposer and synchrophasor codecs, the bridge client, the cycle scheduler, diagnostics, licensing. | none | none |
| `src/GridSim.Cli` | console exe `gridsim`, `net10.0` | Argv parsing, the unknown-flag gate, every verb, the licence choke point for model loads, backend selection, run metadata, result files, the TCP transports of the interposer. Server GC, concurrent GC and tiered PGO are on for the compute loops. | Core, Gda, Gpu | none |
| `src/GridSim.Gda` | class library, `net10.0` | Maps the GB data lake (NESO and the DNO LTDS tables) onto a `GridModel`: `Ltds/`, `Neso/`, `Npg/`, `Ukpn/`, `Csv/`, `Geo/`, the case writer and the interposer's quarantine store. Embeds no data. Not packable: it ships inside the CLI. | Core | `Parquet.Net` |
| `src/GridSim.Gpu` | class library, `net10.0` | The GPU linear-solver backends behind the Core seams: ILGPU (CUDA and OpenCL) and native ROCm/HIP. | Core | `ILGPU`, `ILGPU.Algorithms` |
| `src/GridSim.Web` | web exe (`Microsoft.NET.Sdk.Web`), `net10.0` | Blazor Server dashboard, the REST API under `/api`, the `/ws/sim` WebSocket, sessions, the resident real-time estimator host, estate SSO consumer, deployment settings. | Core | none |
| `src/GridSim.Wpf` | WinExe, `net10.0-windows` | The desktop dashboard: view-models, views, and the schematic renderer. Runs on Windows only; `EnableWindowsTargeting` lets it compile on Linux. | Core | none |
| `src/GridSim.SchemaEditor` | console exe, `net10.0` | The full-screen text-mode case editor: key map, renderer, views. The format engine it drives is in Core under `IO/Json/Lossless` and `IO/Json/CaseEditing`. Not packable. | Core | none |
| `tests/GridSim.Tests` | xUnit, `net10.0` | The test suite, including source scans of Cli and Wpf and four linked Cli source files. | Core, Gda, Gpu, Web, SchemaEditor, and Cli with `ReferenceOutputAssembly=false` | `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing` |

Three things sit outside the solution:

- `tools/` holds the Python cross-validation sidecars (`tools/crossval/`, which the `crossval` verb runs against
  pandapower and scipy), the documentation tooling (`tools/docs/`: `lint.py`, `capture.ps1`), the
  benchmark and regime shell scripts (`tools/gpu-bench.sh`, `tools/run-gpu.sh`, `tools/run-regime.sh`,
  `tools/transient-suite.sh`, `tools/fetch-cases.sh`) and `tools/gb-reference/`. None of it is built by the solution.
- `tools/AssetSmoke` is an off-screen WPF smoke harness (Windows only to run) that instantiates every asset page and the
  map, generation, machine and SCADA views on a solved frame. It is in `GridSim.slnx`, so every build compiles it; it
  used to sit outside the solution and stopped building unnoticed (D-DOC-201). Nothing runs it automatically.
- The GDA repository, a separate repository, holds the Python builders and the bridge server that sit on the far side of
  the wire ([ch. 9](<0009 GDA integration.md>)). Nothing from the lake is committed here.

### Dependency rules

```
                         GridSim.Core
          (Abstractions, Model, PowerFlow, Numerics, Estimation, Dynamics, ...)
        /        |         |          |            \
   GridSim.Gpu  GridSim.Gda  GridSim.Web  GridSim.Wpf  GridSim.SchemaEditor
        \        /
         GridSim.Cli                (also references Core)

   tests/GridSim.Tests references Core, Gda, Gpu, Web and SchemaEditor, and builds Cli without referencing it.
```

1. **Core is the root and holds no packages.** `GridSim.Core.csproj` has no `PackageReference` and no `ProjectReference`.
   Its only non-source inputs are embedded resources: the MATPOWER library cases as `GridSim.Core.cases.<name>.m` and the
   v1 and v2 schema files as `GridSim.Core.schemas.v1.<file>` and `GridSim.Core.schemas.v2.<file>`. Core opens WebSockets
   with `System.Net.WebSockets` directly (`Net/GridBridgeClient.cs`). Anything that needs a NuGet package or a device
   driver goes in a leaf project behind a Core seam.
2. **Only the CLI and the tests reach `GridSim.Gpu`.** GPU backends meet the solvers only through `ILinearSolver`,
   `ISparseLinearSolver` and `IBatchedLinearSolver`. `GridSim.Web` and `GridSim.Wpf` do not reference Gpu, so the
   dashboards run the CPU engines only.
3. **Only the CLI and the tests reach `GridSim.Gda`.** The `gda`, `gda-replay` and `interpose` verbs use it; the
   dashboards read the lake only through the bridge.
4. **Presentation projects reference Core and nothing else.** Web, Wpf and SchemaEditor run no solver of their own: Web
   re-projects each `SimFrame` into DTOs on the server (`Web/Projection/FrameProjector.cs`) and Wpf binds views to the
   frame. Formatting rules, thresholds and fuel and carbon tables that both dashboards use live in `Core/Presentation/`.
   The rule is not yet complete. Some display arithmetic still lives in the shells, written twice (Wpf view-model and Web
   service) and off any validated path: THE MACHINE's narrative read-outs (the droop-implied surplus, commanded and
   delivered torque, the Hz.s and regeneration accumulators and the illustrative BESS state of charge, in
   `Wpf/Views/MachineVm.cs` and `Web/Sessions/MachineService.cs`), the per-unit reactive estimate on the generator pages
   (bus injection shared by output), the per-end circuit currents `S / (sqrt(3) x V)`, and the intrinsic-inertia sum on
   NESO RP1. The trip studies (`FrequencyDynamics.Simulate`, `TsgbSizer`, `InertiaEstimator`) are Core calls that the
   shells invoke. Until that arithmetic moves into Core presenters, a desktop or web number of this kind is a display
   estimate, not a validated Core result.
5. **Capability lives in Core and Cli.** A verb's arithmetic is a Core call; `Program.cs` parses, calls, formats and sets
   the exit code.
6. **Internals are opened by name.** Core grants `InternalsVisibleTo` to `GridSim.Tests` and `GridSim.SchemaEditor`; Web
   and SchemaEditor grant it to `GridSim.Tests`. The case-editing engine reaches the editor this way, and the tests reach
   internal numerics such as `IWlsStep` this way, without widening the public API.
7. **One version.** No project sets `<Version>`; `Directory.Build.props` owns it (see Cross-cutting rules).

The tests do not reference `GridSim.Wpf`; `tests/GridSim.Tests/WpfPresentationSourceContractTests.cs` and
`WpfDeadCodeAndCycleTests.cs` read its source instead. The test project references Cli only to build it
(`ReferenceOutputAssembly=false`), and the CLI contract tests read `Program.cs` as text. Four Cli files are the
exception: `tests/GridSim.Tests/GridSim.Tests.csproj` compiles `InterposeTransport.cs`, `VerbHelp.cs`,
`CliResultWriter.cs` and `VerbFlags.cs` into the test assembly (`Link="Linked\..."`), so `InterposeTransportTests`,
`VerbHelpTests`, `CliResultWriterTests` and `VerbFlagsTests` run the shipped code, not a source scan.

## Core namespaces

Every namespace below is under `src/GridSim.Core/<Folder>/` and `GridSim.Core.<Namespace>`. The key types are the ones
a reader meets first; each subsystem chapter lists the rest.

| Namespace | Responsibility and key types |
|---|---|
| `Abstractions` | The seam interfaces and nothing else: `IPowerFlowSolver`, `ILinearSolver`, `ISparseLinearSolver`, `IBatchedLinearSolver`, `IComplexLinearSolver` with `IComplexLinearFactorization`, `IInertiaModel`. |
| `Model` | The immutable network records and `GridModel`: `Bus`, `BusType`, `Generator`, `Branch`, `Oltc`, `Boundary`, `ZipLoad`, `ConverterPlant`, `GridDiagnostic`, plus `FuelTypes`. `GridModel.CopyWith` is the one copy path that carries every property ([ch. 2](<0002 Data model and per-unit conventions.md>)). |
| `PowerFlow` | The steady-state engines and the loops around them: `NewtonRaphsonPowerFlow`, `SparseNewtonRaphsonPowerFlow`, `BackwardForwardSweep`, `NewtonShell` (the one shared Newton step and update), `SteadyStateSolver` (the OLTC loop), `SolverSelector`, `YBus`, `DenseLuSolver`, `CpuSparseLuSolver`, `CpuBatchedLuSolver`, `PowerFlowOptions`, `PowerFlowResult`, `ConvergenceContract`, `TopologyOps`, `IslandPartition`, `KronReduction` ([ch. 3](<0003 Power flow.md>)). |
| `Numerics` | Sparse and dense kernels with no network knowledge: `CsrMatrix`, `ComplexCsr`, `SparseLu`, `Ilu0`, `BiCgStab`, `HouseholderQr`, `SimdKernels`, `SimdCapabilities`, `NumericTolerances` (the one tolerance table), the complex solvers, eigen solvers, `BoundedSimplex` ([ch. 4](<0004 Sparse and GPU numerics.md>)). |
| `Estimation` | State estimation: `WlsStateEstimator`, the internal `IWlsStep` backends, `MeasurementSet`, `MeasurementModel`, `ObservabilityAnalysis`, `BadDataAnalysis`, `ChiSquare`, `HypothesisTestingBadData`, `NoiseFloorAnalysis`, `SyntheticMeasurements`, `EstimationResult`, `Dse/` (Kalman, unscented and ensemble trackers), `Security/FdiaScreen` ([ch. 6](<0006 State estimation.md>)). |
| `Dynamics` | Frequency and stability: `FrequencyDynamics`, `FrequencyIntegrator`, `AssetAwareFrequencyIntegrator`, `LumpedScalarInertiaModel`, `InertiaModelSelector`, `RocofScreening`, `TsgbSizer`, `FrequencyLimits`; `Distributed/` (multi-domain swing), `Forming/` (grid-forming formation), `Models/` (machines, exciters, governors, RMS transient stability), `SmallSignal/`, `Impedance/`, `Emt/` ([ch. 5](<0005 Frequency dynamics.md>), [ch. 24](<0024 Standard dynamic-model library.md>), [ch. 30](<0030 Small-signal and impedance stability.md>), [ch. 21](<0021 Electromagnetic transient (EMT) simulation.md>)). |
| `Assets` | The per-asset dynamics framework on the asset seam: `IAssetDynamics`, `IActiveAssetDynamics`, `IPassiveAssetDynamics`, `AssetClassLibrary`, `AssetBindingSet`, `AssetControllerFactory`, `AssetDynamicsFleet`, `PassivePlantFleet`, `GridFollowingInverter`, `GridFormingInverter`, and `Assets/Protection/` (`ProtectionController`, `ProtectionEvaluator`, `ProtectionModel`) ([ch. 13](<0013 Asset-dynamics data grounding.md>), [ch. 15](<0015 Protections.md>)). |
| `Protection` | The passive relay overlay and fault studies: `IProtectionRelay`, `ProtectionRegistry`, `ProtectionAssessment`, `ProtectionCascade`, `FaultAnalysis`, `SequenceOfEvents`, `Coordination/OvercurrentCoordination`, `ShortCircuit/Iec60909` ([ch. 17](<0017 Protection assessment (a passive relay overlay).md>), [ch. 25](<0025 IEC 60909 short-circuit duty study.md>), [ch. 26](<0026 Overcurrent coordination (IDMT grading).md>)). |
| `Analysis` | Network studies over a solved state: `ContingencyAnalysis`, `DcPowerFlow`, `DcOptimalPowerFlow` and its variants, `AcOptimalPowerFlow`, `ContinuationPowerFlow`, `DistributionFactors`, `LossAllocation`, `TapOptimizer`, `RestorationSequencer`, `WatchList`, `BoundaryAnalysis`, `VoltageLimits` ([ch. 23](<0023 Contingency and security analysis (N-k).md>), [ch. 29](<0029 OPF, CPF, taps, distribution factors and loss allocation.md>)). |
| `Harmonics` | `Y(h)`, frequency scan, penetration, coupled harmonic power flow, harmonic state estimation, compliance: `FrequencyDependentYBus`, `HarmonicPenetration`, `HarmonicStateEstimator`, `ConverterHarmonicSources` ([ch. 19](<0019 Harmonics.md>)). |
| `Distribution` | The unbalanced three-phase feeder model and its sweep, fault study and estimator: `MultiPhaseModel`, `ThreePhaseSweep`, `ThreePhaseWlsEstimator`, `BranchCurrentDsse`, `SequenceFaultStudy` ([ch. 18](<0018 Real-time, distribution and dynamic estimation.md>)). |
| `Physics` | The physical-law audit and its registry: `PhysicsLaws`, `PhysicsFeasibility`, `PhysicsCheckRegistry`, and the opt-in `Experimental/` models ([ch. 16](<0016 Physics conformance and the constrained estimator.md>)). |
| `Simulation` | The loops that drive solves over time: `LiveSim` and `SimFrame`, `LiveSolveSettings`, `GdaReplayLoop`, `PresolvedReplay`, `RealtimeEstimatorEngine`, `RealtimeDseEngine`, `GridScenario`, `LoadProfile`, `GcScope`; `HybridMultiRateScheduler` is internal. |
| `Scheduling` | The cycle engine: one base solve, then a prioritised parallel fan-out. `CycleRunner`, `CycleScheduler`, `AnalysisJob`, `AnalysisJobLibrary`, `JobCostModel`, `CycleBudget`, `CycleResult`, `ResidentCycleService`. |
| `Dispatch` | `Dispatcher`: default capacity factors by fuel and the slack balance. |
| `Storage` | The chemistry-first battery model and its dispatch policies: `CellModel`, `PackModel`, `AgeingModel`, `StorageFleet`, `IStorageDispatchPolicy` ([ch. 27](<0027 Battery energy storage.md>)). |
| `Events`, `Replay`, `Scenarios`, `Evidence`, `Fleet` | The evidence engine: `EventCatalog`, `GridEvent`, `ReplayValidator` (the credibility gate), `ScenarioEngine`, `H0StudyEngine`, `FleetDynamics`, `EvidenceReport`, `AvoidedCost` ([ch. 7](<0007 The evidence engine.md>)). |
| `Frequency` | NESO frequency traces and their replay: `NesoFrequencyCsv`, `RocofUpsampler`, `ReplayController`. |
| `Gda` | `GdaTimeSeries`: the dated demand series that `GdaReplayLoop` replays. (The lake reader is the separate `GridSim.Gda` project.) |
| `IO` | Everything that crosses a file or wire boundary: `IeeeCases`, `Matpower/`, `Json/` (v1 loader, `V2CaseLoader`, `Schema/`, `Lossless/`, `CaseEditing/`), `Cim/`, `PyPsa/`, `Geo/`, `Telemetry/` frame codecs, `BridgeProtocol`, `SolveExport`, `SolveImport`, `EstimateExport`, `FrequencyExport`, `HarmonicExport`, `SmallSignalExport`, `ImpedanceStabilityExport`, `H0StudyExport`, `NeverOverwrite`, `DirectoryScan` ([ch. 10](<0010 File and wire formats.md>), [ch. 28](<0028 The v2 JSON schema.md>)). |
| `Interpose` | The RTU/SCADA interposer pipeline: `InterposePipeline`, `FrameCorrection`, `IRtuProtocol`, `RtuProtocolRegistry`, `QualityFold`, `RtuPointMap`, `RtuMeasurementSetMapper`, `Protocols/` ([ch. 20](<0020 The RTU and SCADA interposer.md>)). |
| `Synchrophasor` | IEEE C37.118 decode and concentration: `C37118Decoder`, `PhasorDataConcentrator`, `PmuMeasurementSetMapper`. |
| `Net` | `GridBridgeClient`, the WebSocket client of the GDA bridge. |
| `Diagnostics` | Cross-cutting guarantees: `ForwardInferenceGuard`, `ProvenanceTag`, `SolveTrace`, `AuditLog` and its writer and recorder, `RestrictionReview`, `CrashLog`, `UtcTime` ([ch. 31](<0031 Audit log and diagnostics.md>), [ch. 8](<0008 Provenance and invariants.md>)). |
| `Licensing` | `LicenceKey`, `LicenceGate`, `Entitlement`, `BundledNetworks` ([ch. 22](<0022 Licensing - key format and trust model.md>)). |
| `Presentation`, `Layout`, `Viz` | Shared, UI-free presentation logic: thresholds, status-lamp rules, carbon factors, fuel taxonomy (`Presentation`); schematic layout (`Layout/GraphLayout`, `TopologyTiers`); the visualisation JSON (`Viz/VizExport`). |
| `Synthetic` | `SyntheticGb`: a procedurally generated GB-shaped network at GDA scale, under no licence. |
| root | `BuildInfo`: the release number and commit read from the assembly's informational version. |

## Seams

A seam is an interface or registry placed where an alternative implementation is expected. Each entry gives the
contract, the implementations, who owns the call site, and the file.

### Power-flow solver

`IPowerFlowSolver.Solve(GridModel, PowerFlowOptions?)` returns a `PowerFlowResult`
(`src/GridSim.Core/Abstractions/IPowerFlowSolver.cs`).

| Implementation | File | Chosen when |
|---|---|---|
| `SparseNewtonRaphsonPowerFlow` | `src/GridSim.Core/PowerFlow/SparseNewtonRaphsonPowerFlow.cs` | Every meshed network on the CPU: `SolverSelector.DefaultSparseThreshold` is `0`, so any model with at least one bus routes here. |
| `NewtonRaphsonPowerFlow` | `src/GridSim.Core/PowerFlow/NewtonRaphsonPowerFlow.cs` | By explicit choice (`SolverKind.DenseNewton`, `gda --solve --dense`, `bench`), on a GPU backend up to 200 buses, and with a caller threshold above the model's bus count. |
| `BackwardForwardSweep` | `src/GridSim.Core/PowerFlow/BackwardForwardSweep.cs` | A strictly radial network: exactly one slack, in-service branches equal to buses minus one, and every bus reachable (`SolverSelector.LooksRadial`). |

`SolverSelector` (`src/GridSim.Core/PowerFlow/SolverSelector.cs`) is the single source of truth for the choice. On the
CPU the two Newton engines are bit-identical, so the threshold is a speed and memory choice and never changes a number.
The dense engine solves every step with the shared CPU sparse LU unless it is handed a GPU `ILinearSolver`
(`NewtonRaphsonPowerFlow` builds a `CpuSparseLuSolver` for a `null` or `DenseLuSolver` argument). Three thresholds exist,
each owned by one caller:

- `SolverSelector.DefaultSparseThreshold` (`0`): the power-flow rule used by `SolverBackend` on the CPU, by the analyses
  (contingency, AC-OPF, CPF, tap optimiser, restoration), the protection cascade and `SteadyStateSolver` with no engine.
- `SolverSelector.DenseSparseCrossoverBuses` (`200`): the GPU routing in `SolverBackend.SparseThreshold`, the traced
  solve export (`SolverBackend.ExportSolver`, because only the dense engine records the Jacobian) and
  `EstimationOptions.SparseBusThreshold`.
- `LiveSolveSettings.SparseThresholdBuses` (`1500`): the per-tick loops `LiveSim` and `GdaReplayLoop`, which call
  `SolverSelector.Create(kind, model, sparseThreshold: 1500)`. When the resolved engine is sparse
  Newton (above `1500` buses on `Auto`, or by explicit `SolverKind.SparseNewton`) they run it with `MaxIterations = 40`
  and no Q-limit switching, and skip the OLTC loop (`LiveSim.cs`, `GdaReplayLoop.cs`).

`SteadyStateSolver` (`src/GridSim.Core/PowerFlow/SteadyStateSolver.cs`) wraps any `IPowerFlowSolver` in the on-load
tap-changer loop and exposes `RegulatedModel`, `TapsSettled`, `TapsAtLimit` and `OltcPasses`. It runs on internal clones,
so a consumer that needs post-solve taps reads `RegulatedModel`, never the input model. An instance keeps per-solve state
and rejects concurrent `Solve` calls with `InvalidOperationException`; parallel code builds one instance per thread.

`PowerFlowOptions.Cancellation` lets a deferrable solve abort at a cycle deadline. `NewtonRaphsonPowerFlow`,
`SparseNewtonRaphsonPowerFlow` and `BackwardForwardSweep` check it at the top of every iteration or sweep, and the default
token never throws, so a solve with no token is bit-for-bit the solve with `default`. `ContingencyAnalysis.ScreenBranchesParallel`
takes a `CancellationToken` parameter and aborts between outages.

### Linear solvers

`src/GridSim.Core/Abstractions/` defines four contracts and the backends sit on both sides of the Core boundary.

| Contract | Signature | Consumers |
|---|---|---|
| `ILinearSolver` | `void SolveInPlace(double[,] a, double[] b)`: dense `Ax = b`; the solution overwrites `b` and `a` may be overwritten, so the Newton loop reuses one buffer set. The extension `LinearSolverExtensions.Solve(a, b)` (`src/GridSim.Core/Abstractions/ILinearSolver.cs`) clones both inputs first and returns the solution without mutating the caller's arrays. | The dense estimator steps (`DenseWlsStep`, `ConstrainedDenseWlsStep`, `FeasibleWlsStep`) and `NewtonRaphsonPowerFlow` on a GPU backend. |
| `ISparseLinearSolver` | `double[]? Solve(CsrMatrix a, double[] b)`: the solution, or `null` when it cannot solve. | Both Newton engines, through `NewtonShell.SolveStep`. |
| `IBatchedLinearSolver` | `bool[] SolveBatch(int count, int n, double[] aFlat, double[] bFlat)`: `count` independent `n x n` systems in one dispatch; a singular system is filled with `NaN` and reported `false`, and never fails the batch. | The `batchsolve` verb. |
| `IComplexLinearSolver` | `Complex[]? Solve(ComplexCsr a, Complex[] b)` and `Factor` for repeated right-hand sides. | Harmonics, the impedance-stability scan, the coupled harmonic power flow. |

| Implementation | File | Notes |
|---|---|---|
| `DenseLuSolver` | `src/GridSim.Core/PowerFlow/DenseLuSolver.cs` | Partial-pivot Gaussian elimination over a flat row-major copy with SIMD row updates (`DenseLuKernels.SubtractScaledRow`): a separate multiply then subtract, never FMA, so it equals the scalar textbook order bit for bit. Throws `InvalidOperationException` on a pivot at or below `NumericTolerances.RelativePivotFloor` (`1e-12`) times max abs of `A`. `Name` is `dense-lu (cpu)`. |
| `CpuSparseLuSolver` | `src/GridSim.Core/PowerFlow/CpuSparseLuSolver.cs` | The canonical Newton step solver on the CPU. Gilbert-Peierls LU over a reverse Cuthill-McKee ordering, caching the ordering while the sparsity pattern is unchanged (a deterministic function of the pattern, so reuse is bit-identical). `Name` is `sparse-lu (cpu)`. |
| `CpuBatchedLuSolver` | `src/GridSim.Core/PowerFlow/CpuBatchedLuSolver.cs` | Loops `DenseLuSolver.FactorSolveInPlace` across the batch in parallel, each system on its own segment. The reference the GPU batched kernel is validated against. `Name` is `batched-dense-lu (cpu)`. |
| `DenseComplexLuSolver`, `RealExpandedComplexSolver`, `SparseComplexBiCgStabSolver` | `src/GridSim.Core/Numerics/ComplexLinearSolvers.cs` | The complex backends that cross-validate. |
| `GpuDenseLinearSolver` | `src/GridSim.Gpu/GpuDenseLinearSolver.cs` | ILGPU elimination over an augmented `[A|b]`, selectable by `AcceleratorType` (CUDA or OpenCL, same kernels); defers to `DenseLuSolver` when no such device exists. `EliminateKernel` runs one GPU thread per row; the pivot search (`PivotKernel`) is a one-block, 256-thread shared-memory argmax; a device flag records the first column whose pivot is at or below `NumericTolerances.PivotFloor(aNorm)` (`1e-12` times the matrix scale `aNorm`); the host synchronises exactly twice, once to read that flag and once to read the solution. |
| `GpuSparseLinearSolver` | `src/GridSim.Gpu/GpuSparseLinearSolver.cs` | ILGPU Jacobi-preconditioned BiCGSTAB with the `4n + 200` iteration cap; returns `null` on breakdown, stagnation, non-convergence or a non-finite result; defers to `CpuSparseLuSolver` when no device exists or a diagonal is missing. |
| `GpuBatchedDenseSolver` | `src/GridSim.Gpu/GpuBatchedDenseSolver.cs` | The dense elimination with a batch index threaded through every kernel: one flat augmented buffer factored under one `GpuContext.Lock` acquisition and one pair of host syncs. Defers to `CpuBatchedLuSolver` with no device. |
| `RocmDenseLinearSolver`, `RocmSparseLinearSolver` | `src/GridSim.Gpu/RocmDenseLinearSolver.cs`, `RocmSparseLinearSolver.cs` | Native hipSOLVER `Dgetrf`/`Dgetrs`, and a device-resident BiCGSTAB (`rocsparse_dcsrmv`, rocBLAS level 1, `rocblas_ddgmm` Jacobi apply), through P/Invoke in `RocmInterop.cs`. Both defer to the CPU solver with no HIP device. ROCm has no batched entry point, so its batched solver is the CPU reference. |

A `null` from a sparse backend is not a failed solve. `NewtonShell.SolveStep`
(`src/GridSim.Core/PowerFlow/NewtonShell.cs`) retries a `null` from a GPU backend on the CPU direct LU
(`directFallback`), and only a `null` from the CPU LU goes to ILU(0)-preconditioned BiCGSTAB with the cap
`NumericTolerances.DefaultKrylovMaxIterations` (`4m + 200`). A step that is not finite is never applied: the step is
rejected and the engine reports non-convergence. The mechanics are in [ch. 4](<0004 Sparse and GPU numerics.md>).

`SolverBackend` (`src/GridSim.Cli/SolverBackend.cs`) is the one place a backend is chosen. `--cpu` is the default;
`--gpu` probes AMD-first (ROCm, then OpenCL, then CUDA, then CPU); `--gpu=rocm|opencl|cuda` is explicit; any other value
is refused with exit 2 (`SolverBackend.IsKnownSpec`). An unavailable accelerator prints a warning on stderr and falls
back to the CPU, and the availability probes (`GpuContext.IsAvailable`, `GpuContext.IsOpenClAvailable`,
`RocmContext.IsAvailable`) never throw. The choice is recorded in `RunInfo.Backend` so reports are comparable. Engine
choice per model goes through `Solver(m)` (radial check, then the shared rule), `NewtonSolver(m)` (the rule only) and
`Pick(m)` (the rule, wrapped in a `SteadyStateSolver`); `DenseBatch()` vends the batched solver. GPU arithmetic is outside
the bit-identity contract, and the CPU backends are the production path.

`GpuContext` (`src/GridSim.Gpu/GpuContext.cs`) owns one ILGPU `Context` and one `Accelerator` per accelerator type.
Because the CLI fans solves out with `Parallel.ForEach` over a single device, every device touch is serialised through
`GpuContext.Lock`. An OpenCL device is accepted only if an on-device kernel computes `(1 + 2^-40) - 1` in double precision from operands sent by the host and returns exactly `2^-40` (single precision returns 0), so a device without usable FP64 is rejected rather than selected and faulted. The device description flags data-centre parts
(A100, H100, V100, Tesla, GH200 on CUDA; the Instinct MI series on OpenCL) as fast FP64 and everything else as rate-limited.
Power flow is `double` end to end, so FP64 throughput decides GPU value, which is why auto-detection is AMD-first. The
measurement behind that order: an NVIDIA RTX A6000 (FP64 at 1:64 of its FP32 rate) took over 5.5 minutes on the dense
case300 solve against 0.6 s on the CPU ([ch. 28](<../manual/0028 Benchmarking and GPU.md>)). CUDA stays
supported through ILGPU and is probed last. Batching a full AC N-1 across divergent Newton iteration counts, and
memory-chunking very large batches, is not implemented (see Limits).

### Estimator step backend

`IWlsStep` (`src/GridSim.Core/Estimation/WlsStep.cs`, `internal`) separates the estimator loop from the representation
of the normal equations. `WlsStateEstimator` owns the loop: start point, trust-capped damping, convergence, chi-square,
bad-data removal and the IRLS reweighting outer loop. A step owns `Begin` (bind a model, measurement set and weights, do
structural work once), `RebindWeights` (IRLS passes), `GateObservability`, `TrySolve` (one undamped Gauss-Newton
correction; `false` means a singular gain) and `NormalizedResiduals`.

| Implementation | Chosen by | Notes |
|---|---|---|
| `CanonicalWlsStep` | `EstimationMethod.Wls`, `Huber`, `LavIrls`, `SchweppeGm` on the CPU (the default `DenseLuSolver`), at every size | Solves with the `SparseWlsStep` arithmetic at every size, so the estimate does not depend on the bus count; at or below `EstimationOptions.SparseBusThreshold` (`200`) its observability report is the dense gain-rank proof of `DenseWlsStep`, above it the sparse diagnosis. |
| `DenseWlsStep` | the same methods with a non-CPU `ILinearSolver` (a GPU dense backend) | Dense normal equations `G = H^T W H` on `ILinearSolver`; proves observability by gain-matrix rank. |
| `SparseWlsStep` | inside `CanonicalWlsStep` | CSR gain and Gilbert-Peierls LU; builds the gain pattern, slot maps and RCM ordering once per `Begin`, then refills values. |
| `ConstrainedDenseWlsStep` | `EstimationMethod.Constrained` | Rows tagged `virtual-zero-injection` become hard equalities in a KKT system. Dense only. |
| `FeasibleWlsStep` | `EstimationMethod.Feasible` | The constrained KKT plus an active set (generator Q capability on by default, angle difference, branch thermal rating opt-in) and an interior guard that keeps `Vm` above `FeasibilityConstraints.VmFloor` (`1e-6` pu). The voltage band is audited after convergence by `PhysicsFeasibility.Check`, not enforced during iteration. Dense only. |
| `QrWlsStep` | `EstimationMethod.Qr` | Householder QR on the weighted Jacobian, without forming `G`. Dense only. |

The dense-only methods throw `NotSupportedException` above `DenseEstimationLimits.DenseMethodMaxBuses` (`3000`) rather
than run unconstrained or exhaust memory; the CLI turns that into exit 2. The CLI `estimate --method` takes `wls`,
`huber`, `lav`, `lav-irls`, `schweppe-gm` (alias `gm`), `qr`, `constrained` and `feasible` (alias `constrained-physics`).
Huber, LAV-IRLS and Schweppe-GM are not separate steps: the estimator reweights and re-runs the same step backend. The
exact LAV (`lav`) solves a linear program at each iterate (`LavLinearProgram`).

`IJacobianSink` (`src/GridSim.Core/Estimation/JacobianSinks.cs`, `internal`) gives the dense and sparse backends one
Jacobian evaluation path: `MeasurementModel.EvaluateJacobian` computes each partial once and pushes it into a
`DenseJacobianSink` or `SparseJacobianRows`, so the two cannot drift. Injection rows use the private
`MeasurementModel.ComputeInjectionsSparse`, which sums only the electrical neighbourhood and is pinned bit-equal to
`NewtonRaphsonPowerFlow.ComputeInjections`; flow rows use the same pi-model stamps as `NewtonShell.ComputeLineFlows`. Details:
[ch. 6](<0006 State estimation.md>).

### Inertia model

`IInertiaModel` (`src/GridSim.Core/Abstractions/IInertiaModel.cs`) turns a model, a demand baseline and power-step events
into a `FrequencyResponse`, with a `CoiInertiaGvaS` aggregate for scalar readouts and a `LastDistributedResult` for
spatial detail. `LumpedScalarInertiaModel` (`Dynamics/LumpedScalarInertiaModel.cs`) is the default;
`DistributedInertiaModel` (`Dynamics/Distributed/DistributedInertiaModel.cs`) is the multi-domain model and returns the
COI-collapsed response so scalar consumers keep working. `InertiaModelSelector` (`Dynamics/InertiaModelSelector.cs`)
builds either; there is no shape-based auto-selection, because the choice is explicit (`--inertia-model=lumped|distributed`).
`IFrequencyIntegrator` (`Dynamics/IFrequencyIntegrator.cs`) is the stateful swing-integrator contract that `LiveSim`
drives, with `FrequencyIntegrator` as the baseline and `AssetAwareFrequencyIntegrator` when an authored fleet is bound.

### Asset dynamics

The asset seam attaches authored, stateful devices to a `GridModel` without touching the steady-state solve.

- **Data.** A case folder may carry `asset-classes.json` (the class library), `asset-bindings.json` (bulk binding rules)
  and `protections.json` (placed protective devices); `JsonGridLoader` turns them into `GridModel.AssetDynamics`
  (`AssetDynamicsModel(Library, Bindings)`) and `GridModel.Protections` (`ProtectionModel`). Absent files leave both
  `null`, and a case with nothing bound takes exactly the path of the feature off.
- **Contracts** (`src/GridSim.Core/Assets/IAssetDynamics.cs`). `IAssetDynamics` is the base. `IActiveAssetDynamics.Update(in
  AssetStepContext)` returns an `AssetActuation` and contributes a swing-power term on the fast sub-step clock.
  `IPassiveAssetDynamics.Advance(in AssetStepContext)` returns a `PlantStateUpdate` (tap ratio, rating derate, thermal
  state) on the steady-state tick and is never a swing term.
- **Frequency seen by an asset.** The active fleet has one step, `AssetDynamicsFleet.StepCoi(hz, rocof, phase, dt,
  clock, voltageOf, loadingOf)`: every asset sees the same lumped (centre-of-inertia) frequency, RoCoF and phase sample,
  with the bus voltage and branch loading from the network when the caller supplies them. Without them each asset's
  context carries `NaN` voltage and loading (`AssetStepContext.NoNetworkVoltagePu`, `NoNetworkLoadingFraction`), on
  which no voltage or loading stage operates; frequency, RoCoF and phase stages still act. There is no per-domain
  frequency sampling for assets.
- **Construction.** `AssetControllerFactory` (`Assets/AssetBinding.cs`) builds `GridFollowingInverter` and
  `GridFormingInverter` as active assets, and `TransformerDynamics` and `LineThermalDynamics` as passive assets.
  `AssetDynamicsFleet` (`Assets/AssetDynamicsFleet.cs`) steps the active fleet and `PassivePlantFleet`
  (`Assets/PassivePlantFleet.cs`) the passive one; both are empty, and contribute exactly zero, when nothing is bound.
- **Consumers.** `LiveSim` builds the fleet when `DynamicsConfig.EnableAssetDynamics` is set and the model carries a
  bundle; `GdaReplayLoop` advances the passive plant when one is supplied. The CLI switches are `--asset-dynamics` and
  `--passive-plant`.

### Protections

There are two protection layers, deliberately separate.

- **The passive relay overlay.** `IProtectionRelay.Evaluate(ProtectionContext)` (`src/GridSim.Core/Protection/ProtectionRelays.cs`)
  scans a solved state and yields every operating location plus the closest non-operating one. `ProtectionRegistry.All`
  lists the relays in registry order: undervoltage (27), overvoltage (59), directional power (32), undercurrent (37),
  thermal overload (49), instantaneous (50) and time (51) overcurrent, distance (21), under- and over-frequency (81U,
  81O), RoCoF (81R), vector shift (78), differential (87) and ground fault (50N/51N/21N/46). `ProtectionAssessment.Assess(model, result)` runs them and never touches the caller's solve;
  `ProtectionCascade.Simulate` chains the branch-tripping elements through topology on copies of the model. The default
  solve report calls `Assess` on a converged state, and the `protection-check` verb prints the full list. The study
  entry points refuse a non-converged state through `ConvergenceContract.RequireConverged`
  ([ch. 17](<0017 Protection assessment (a passive relay overlay).md>)).
- **The active layer.** `ProtectionController : IActiveAssetDynamics` (`src/GridSim.Core/Assets/Protection/ProtectionController.cs`)
  is an asset on the asset seam: `AssetDynamicsFleet.Build` creates one per `ProtectionInstance`, it watches
  frequency, voltage or loading at its monitored bus, and its trips and recloser state feed back into `LiveSim` as a
  deficit and an open-branch set ([ch. 15](<0015 Protections.md>)).

### Importers and exporters

Every format has one reader and one writer in `src/GridSim.Core/IO/`, and one owner chapter ([ch. 10](<0010 File and wire formats.md>)).

| Direction | Format | Type and file |
|---|---|---|
| In | Built-in cases (embedded MATPOWER) | `IeeeCases` (`IO/IeeeCases.cs`); `Case9()` lays the published WSCC machine data over `case9.m` |
| In | MATPOWER `.m` | `MatpowerParser` (`IO/Matpower/MatpowerParser.cs`), with `MatpowerUnitNormalizer` for raw ohm and kW feeders |
| In | JSON v1 case folder | `JsonGridLoader.LoadDirectory` (`IO/Json/JsonGridLoader.cs`): `system.json`, `buses.json` and `generation.json` required; `demand`, `transmission`, `transformer`, `onloadtapchanger`, `boundaries`, `asset-classes`, `asset-bindings` and `protections` optional |
| In | JSON v2 manifest (`gridsim-case/2`) | `V2CaseLoader` (`IO/Json/V2CaseLoader.cs`) with `IO/Json/Schema/` validators |
| In | CIM / CGMES | `CimImporter`, `CimGridModelBridge` (`IO/Cim/`) |
| In | PyPSA-GB | `PypsaGbReduced`, `PypsaGbZonal`, `PypsaCsv` (`IO/PyPsa/`) |
| In | GDA lake | `GdaModelBuilder` in `GridSim.Gda` |
| In | Measurements (`gridsim-measurements/1`) | `MeasurementSet.Load` (`Estimation/MeasurementSet.cs`) |
| In | Solve export (`gridsim-solve-export/1`) | `SolveImport` (`IO/SolveImport.cs`) |
| Out | MATPOWER, JSON v1, CIM | `MatpowerWriter`, `JsonCaseWriter`, `CimExporter` (the `convert` verb) |
| Out | Solve and estimate exports | `SolveExport`, `EstimateExport` |
| Out | Study exports | `FrequencyExport`, `HarmonicExport`, `SmallSignalExport`, `ImpedanceStabilityExport`, `H0StudyExport`, `EvidenceReport.ToJson`, `VizExport` |
| Out | CLI result files | `CliResultWriter` (`gridsim-cli-result/1`) |
| Out | GDA case folder | `GdaModelWriter` in `GridSim.Gda` |

Each case source has rules worth knowing before extending it:

- **MATPOWER** (`MatpowerParser.Parse`, `ParseFile`, `ParseWithDiagnostics`): `%` comments, `...` continuations and
  `Inf` and `-Inf` are preprocessed; bus type 3 becomes `Slack`, 2 becomes `Pv` and anything else `Pq`; type-4 (isolated)
  buses are dropped with their incident elements and the drop is a parse note; a file with no type-3 bus is refused; a
  generator's `MBase` falls back to the case `baseMVA` when it is not positive and its `H` is `0`, because MATPOWER carries
  no inertia. The CLI prints every parse note on stderr as `note: <file>: <text>`.
- **JSON v1 folder** (`JsonGridLoader`): bus type is resolved from the generator roles (a `slack` unit makes its bus
  `Slack`, a `pq` unit leaves the bus type alone, any other in-service unit makes its bus `Pv`), and a case with no slack is
  refused; a generator role that is not `slack`, `pv` or `pq` is refused; demand `Q` comes from the power factor when `qMvar`
  is absent; parallel `Circuits` divide the series impedance and multiply the charging and rating; a unit's output is
  `OutputMw`, else capacity times `CapacityFactor`, else capacity times `Dispatcher.DefaultCapacityFactor`; with
  `dispatch: true` (the default) the model is balanced by `Dispatcher.Balance` before it is returned; `baseKv` is required on
  every bus; every boundary branch name must resolve to exactly one branch. The DTOs in `IO/Json/JsonDtos.cs` read case
  insensitively, with comments and trailing commas allowed.
- **JSON v2** (`V2CaseLoader.Load`) builds through the same `JsonGridLoader.Build` as v1, so a v2 case whose elements equal
  a v1 case gives the same model bit for bit.
- **Synthetic GB** (`SyntheticGb.Build(Options?)`) generates a GB-shaped network from `Options.Scale` (a finite number above
  0; `1.0` gives a network of more than two thousand buses) with no data under licence; the `synth` verb builds it, solves it
  on the sparse engine and materialises it as a JSON case folder.
- **Built-in names** are resolved by `LoadNamed` in `Program.cs` for every verb that takes a case argument: `case9`
  (alias `ieee9`), `case14`, `case30`, `case57`, `case118`, `case300`, `case145`, `case24` (alias `rts`), `cigre`, `uk`
  (aliases `gb`, `gb-reduced`), `gb-full`, `gb-synthetic` (alias `synthetic`), `gb-reduced-29`, `gb-network-2224`,
  `pypsa-gb-reduced`, a `.m` file, a `.json` v2 manifest, or a folder; `pypsa-gb-zonal` is refused as not solvable by
  AC power flow. An unknown name throws `unknown case '<name>'` with the list of built-in names, and each verb reports
  it in its own way: `estimate` prints `error loading case '<name>': ...` and exits 1; `nminus1` prints `error:
  nminus1: unknown case ...` and exits 2; `stability` prints `stability: unknown case ...` and exits 2. The bare-case
  path (`gridsim <case> --once`) has its own shorter list, described under Data flows.

### Wire formats and the boundaries they cross

"Boundary" names where the bytes change hands. The field reference for each is [ch. 10](<0010 File and wire formats.md>).

| Format | Written by | Read by | Boundary |
|---|---|---|---|
| `gridsim-solve-export/1` | `SolveExport` (`--enableExport`, `transient`, `gda-replay`, `estimate --export-solve`); the GDA Python exporter | `SolveImport`, `PresolvedReplay`, web `CaseCatalog` | disk: CLI run directories to desktop and web replay; Python to .NET |
| Bridge messages (`topology`, `state`, `result`, `error`, and the server's `info` and `done`) | the GDA bridge server | `BridgeProtocol` through `GridBridgeClient`, which acts on `topology`, `state`, `result` and `error` and ignores every other type | one WebSocket, Python server to .NET client (desktop and web); the `topology` model block and the `result` payload reuse the solve-export shapes |
| `gridsim-measurements/1` | the GDA measurement pipeline; `SyntheticMeasurements`; the interposer (`MeasurementSet.SaveWithoutOverwrite`) | `MeasurementSet.Load` (`estimate --measurements`, `serve`) | disk: lake or telemetry to estimator |
| `gridsim-estimate-export/1` | `EstimateExport` | the GDA `build_estimated_state` pipeline; validation tooling | disk: estimator to lake |
| `gridsim-frequency-export/1` | `FrequencyExport` | analysis tooling | disk |
| `gridsim-evidence/1` | `EvidenceReport.ToJson` | consumers of the dossier | disk |
| `gridsim-event-catalog/1` | curated files under `data/events/` | `EventCatalog.Load` | disk: recorded-event evidence to the replay gate |
| `gridsim-rtu-pointmap/1` | the engineer | `RtuPointMap.Load` | disk: crosswalk to interposer |
| `gridsim-audit/1` | `AuditLogWriter` | `AuditLogFile` and the `audit` verb | disk: a run to its tamper-evident log |
| `gridsim-cli-result/1` | `CliResultWriter` | scripts | disk |
| MATPOWER `.m` | external tools, `MatpowerWriter` | `MatpowerParser` | disk: the MATPOWER ecosystem to GridSim |
| JSON case folder | `synth`, GDA materialisation, `convert`, the case editor, hand editing | `JsonGridLoader.LoadDirectory` | disk: case library to any entry point |
| REST `/api/*` and WebSocket `/ws/sim` | `GridSim.Web/Api/RestApi.cs`, `SimWebSocket.cs` | external consumers (the Blazor UI drives its `SimSession` in process) | HTTP and WebSocket out of `GridSim.Web` |

All inbound JSON is case-insensitive and accepts the named floats `Infinity`, `-Infinity` and `NaN` (open generator
limits): `SolveImport`, `BridgeProtocol.JsonOptions` and `MeasurementSet` pin the same options. Two writers refuse to overwrite:
`NeverOverwrite` is used by `MeasurementSet.SaveWithoutOverwrite` (the interposer's measurement files) and by
`QuarantineLiveStore` (`GridSim.Gda`), which write a `-dupN` name instead of replacing a file. Every other result file
overwrites: `Program.cs` writes directly with `File.WriteAllText`, `StreamWriter` and `File.Create` at 37 sites (for
example `residuals.csv` and `measurements.json` of `estimate`), the Core exporters (`SolveExport`, `EstimateExport`,
`FrequencyExport`, `HarmonicExport`, `MeasurementSet.Save`, `AuditLogWriter`, `CrashLog`) write their own files, the case
editor saves through `CaseCommitter`, and the desktop app writes its settings file (`GridSim.Wpf/AppSettings.cs`). The case editor reads and writes JSON through the lossless text layer
(`IO/Json/Lossless/`), edits it through `CaseSession` and `CaseOps`, and saves only through `SaveGate`
(schemas, loader, licence) and `CaseCommitter`.

### Telemetry protocols

`IRtuProtocol` (`src/GridSim.Core/Interpose/IRtuProtocol.cs`) has `Name`, `DisplayName`, `Transport`, `DefaultPort` and
`Decode(ReadOnlySpan<byte> frame, QualityFold quality)`, which returns `IReadOnlyList<RtuPoint>`. `RtuProtocolRegistry` registers
`Iec104Protocol` (IEC 60870-5-104) and `ModbusProtocol` (Modbus TCP) and nothing else. DNP3 and IEC 61850 GOOSE exist only
as link-framing scaffolding in `IO/Telemetry/` (`Dnp3Frame`, `Iec61850GooseFrame`) and are not selectable.
`FrameCorrection.ForProtocol` supplies the per-protocol framing gate. The transports are in the CLI
(`InterposeTransport.cs`: `IRtuTransport`, `TcpDelimitedFrameTransport`, `TcpMbapFrameTransport`), so Core opens no
RTU socket (Core's only socket is the bridge WebSocket). IEEE C37.118 is a separate path: `C37118Decoder` and
`PhasorDataConcentrator` in `Synchrophasor/` are driven by the `pmu-ingest` verb, which decodes and aligns frames and
prints them; it builds no `MeasurementSet`. `PmuMeasurementSetMapper` (which would build a horizon-guarded
`MeasurementSet` from a superframe) is library code that no verb calls. The interposer does build `MeasurementSet`s, with
the horizon guard deferred (see the interposer flow) ([ch. 20](<0020 The RTU and SCADA interposer.md>),
[ch. 18](<0018 Real-time, distribution and dynamic estimation.md>)).

### Bridge

The bridge is a wire-level seam, not a C# interface. The server is in the GDA repository; the client is
`GridBridgeClient` (`src/GridSim.Core/Net/GridBridgeClient.cs`) and the schema is `BridgeProtocol`
(`src/GridSim.Core/IO/BridgeProtocol.cs`). One WebSocket carries `topology`, `state`, `result` and `error`
messages, which the client acts on; any other well-formed type, such as the server's `info`, is a forward-compatible
no-op in `GridBridgeClient.Dispatch`. Every stream carries a `source`: `live`, `presolved`, `estimated`, `solves` or `raw`; `GridBridgeClient.StartAsync`
defaults to `presolved`. The WPF source picker offers `presolved`, `estimated` and `live`
(`GridSim.Wpf/MainWindow.xaml.cs`, `--bridge-source`); the web case id `bridge:<case>@<source>` accepts all five
(`GridSim.Web/Sessions/SessionManager.cs`), and the landing id of a `GridSim:ReplayOnly` instance is `bridge:gb-full@raw`.
The server echoes the choice in `StateMessage.Source`, and an `estimated` frame carries a per-frame `EstimateBlock`
(objective, chi-square verdict, redundancy, removals). The protocol is specified in [ch. 9](<0009 GDA integration.md>).

### Cycle jobs

`AnalysisJob` (`src/GridSim.Core/Scheduling/AnalysisJob.cs`) is a unit of post-solve work with a stable `Id`, a
`JobPriority`, a `SeedCoreMs` cost seed and a `Run(CycleContext, CancellationToken)` delegate. `AnalysisJobLibrary.Build`
wraps the existing analyses and re-implements none of them: `watch` (`WatchList.Assess`, High), `n-1:hot` (High) over
`ContingencyAnalysis.ScreenBranchesParallel`, `n-1:lp:<shard>` (Low; each shard is a resumable loop over
`ContingencyAnalysis.ScreenOutage` that keeps its progress across deadline cancellations and keys each branch by identity
(`BranchKey`: from bus, to bus, occurrence), so a de-energised island is skipped), `harmonics` (`HarmonicPenetration.Solve`, High,
skipped above `CycleOptions.HarmonicsBusLimit`), `rocof` (`RocofScreening.Screen`, Low) and `physics` (`PhysicsLaws.Audit`,
Low). A new job is a new `AnalysisJob`; the scheduler does not change.

### Dispatch and scenario helpers

These are the pure functions every loop builds on.

- `Dispatcher` (`src/GridSim.Core/Dispatch/Dispatcher.cs`). `DefaultCapacityFactor(type)` maps a generator type to a
  capacity factor: nuclear `0.90`, biomass `0.70`, hydro `0.40`, pumped storage, storage and battery `0.20`, offshore wind
  `0.45`, onshore wind `0.30`, solar `0.15`, interconnector `0.60`, CCGT and gas `0.50`, OCGT and peaker `0.05`, coal `0.0`,
  anything else `0.40`. `Balance(model, lossAllowance = 0.02)` sets the first generator on the first slack bus to
  `max(0, totalDemand * (1 + lossAllowance) - otherGeneration)` and returns a copy; `BalanceDetailed` also returns a note
  when that target lies outside the marginal plant's `Pmin` and `Pmax`, and never clamps it, because the power flow decides
  what is feasible. `Dispatcher.Clone` goes through `GridModel.CopyWith`, which carries every model property, including the
  out-of-service units.
- `GridScenario` (`Simulation/GridScenario.cs`). `AtLoad(base, factor)` scales every bus `Pd` and `Qd` and every generator
  not on the slack bus by `factor`, then re-balances with `Dispatcher.Balance`. `WithSolvedVoltages(base, result)` returns a
  copy whose bus `Vm` and `VaDeg` are the solved values keyed by bus id: a warm start.
- `LoadProfile.DailyFactor(hour)` (`Simulation/LoadProfile.cs`):

```
x      = ((hour - 4 + 24) mod 24) / 24
factor = 0.60 + 0.40 * (0.5 - 0.5 * cos(2 * pi * x))          trough 0.60 at 04:00, peak 1.00 at 16:00
```

- `GcScope` (`Simulation/GcScope.cs`). `LowLatency()` selects `SustainedLowLatency` for the live and replay loops, and
  `NoGc(budgetBytes = 256 MiB)` opens a no-GC region; each returns an `IDisposable`. `LowLatency` restores the previous latency mode; `NoGc` ends its region
  (`GC.EndNoGCRegion`) and restores nothing. Both degrade to a no-op if the runtime cannot honour them.
- Contingency screening (`Analysis/ContingencyAnalysis.cs`). `ScreenBranchesParallel` builds one solver per worker from a
  `solverFactory` through `ThreadLocal`, fills a pre-allocated result array in branch order, and defaults the degree of
  parallelism to `Environment.ProcessorCount`; it is thread-safe because `GridModel` is immutable and an engine holds no
  state shared across instances. `ContingencyResult.Secure` is `Converged && Violations.Count == 0`. `ScreenBranches` is the sequential sweep beside it.
- `PowerFlowOptions.FlatStart` (default `false`) starts every bus at flat voltage instead of the model values;
  `PowerFlowOptions.EnforceQLimits` (default `true`) runs the reactive-limit loop, whose violation tolerance is
  `NewtonShell.QLimitTolMva` (`1e-3` MVA). `Oltc.DefaultStepPu` is `0.0125` and `Oltc.DeadbandPu` defaults to `0.01`; the
  OLTC loop is described in [ch. 3](<0003 Power flow.md>).
- `FrequencyLimits` (`Dynamics/FrequencyLimits.cs`) holds the GB limits: operational band `49.8` to `50.2` Hz, statutory
  `49.5` to `50.5` Hz, first low-frequency demand disconnection stage `48.8` Hz and the legacy RoCoF limit `0.125` Hz/s.
  `InertiaEstimator.EstimateInertiaMwS` inverts the swing equation, `E = f * |deltaP| / (2 * |RoCoF|)`
  ([ch. 5](<0005 Frequency dynamics.md>)).

### Other registries and policies

- `IStorageDispatchPolicy` (`Storage/StorageDispatch.cs`): `IdlePolicy`, `PeakShavePolicy`, `PriceThresholdPolicy`,
  `ResponseHoldingPolicy`, `SchedulePolicy`.
- `IMultiPhaseSolver` (`Distribution/ThreePhaseSweep.cs`), `IHarmonicCouplingModel` (`Harmonics/CoupledHarmonicPowerFlow.cs`),
  `IConverterImpedanceModel` (`Dynamics/Impedance/IConverterImpedanceModel.cs`), `IProcessModel` (`Estimation/Dse/IProcessModel.cs`)
  and `IRmsNetwork` (`Dynamics/Models/RmsNetwork.cs`) are the extension points of their subsystems.
- `IRealtimeWindowFeed` (`GridSim.Web/Realtime/RealtimeEstimatorHostedService.cs`) feeds the resident estimator host its
  dated measurement windows.
- `PhysicsCheckRegistry` (`Physics/PhysicsCheckRegistry.cs`) lists the physical-law checks.

### Adding a solver backend

A new backend is a drop-in with no change to the physics. For an ILGPU-supported accelerator no new solver is needed,
because the ILGPU solvers take an `AcceleratorType`. For a native vendor stack:

1. **Implement the interface.** A dense backend implements `ILinearSolver.SolveInPlace`; a sparse backend implements
   `ISparseLinearSolver.Solve` and returns `null` when it cannot solve. Mirror the ILGPU backends: probe device
   availability once and never throw from the probe, defer to the CPU implementation when the device is absent,
   serialise access to a shared device as `GpuContext.Lock` does, and take the singular-pivot decision from
   `NumericTolerances.PivotFloor` so every backend declares singularity by the same rule.
2. **Wire it into the factory.** Extend `SolverBackend`: the `Vendor` enum, `Select`, `IsKnownSpec`, `DenseLinear`,
   `SparseLinear` and, if batched, `DenseBatch`. Record a description in `RunInfo.Backend`. The `--gpu=<vendor>` value is
   validated before `Select` runs, so a typo fails with exit 2.
3. **Feed it through.** `NewtonRaphsonPowerFlow(ILinearSolver)` and `SparseNewtonRaphsonPowerFlow(ISparseLinearSolver)` take
   the backend by constructor, and `SteadyStateSolver` accepts either an `ILinearSolver` or a whole `IPowerFlowSolver`.
   `YBus`, the mismatch and Jacobian assembly, the Q-limit loop and the OLTC loop do not change. A `null` from the new
   sparse solver is retried on the CPU direct LU automatically.
4. **Keep engine choice in one place.** `CliEngineChoiceScanTests` fails if any CLI file other than `SolverBackend.cs`
   builds an engine directly or compares a bus count in a line that names an engine, unless the line carries a reasoned
   `// routing-exempt:` marker.
5. **Validate.** The CPU solvers are the reference: compare the new backend against `CpuSparseLuSolver` and
   `DenseLuSolver` in the pattern of `GpuSolverSeamTests`, then run `gridsim gpucheck` and `gridsim bench`. A GPU backend
   agrees to solver tolerance and sits outside bit-identity.

## Data flows

Each flow lists the types in the order they run.

### CLI solve: `gridsim <case> --once`

1. `src/GridSim.Cli/Program.cs` runs top-level statements in a fixed order. It pins `InvariantCulture`; answers
   `--version` and the per-verb help (`VerbHelp.Render`, which prints that verb's section of the embedded CLI reference)
   before anything can refuse them; registers the process-wide unhandled-exception net (a missing path or a
   `CaseLoadException` becomes `error: ...` and exit 1, anything else is a defect, written by `CrashReport.Write` and left
   to crash); resolves `LicenceGate.Resolve()` and calls `Gated.Init`; checks every `--flag` against the `knownFlags`
   set and the verb's own flags (exit 2); opens the `--audit-log` when the verb is `estimate`, `crossval` or `interpose`;
   builds the backend with `SolverBackend.Select(gpuSpec)`; and parses the global switches.
2. The model is loaded through the licence choke point. `Gated.Dir` (JSON case folder) and `Gated.Matpower` (a `.m` file,
   after `MatpowerUnitNormalizer.Normalize`) enforce the tier's bus limit unless the path is a bundled network; the
   bare-case switch accepts ten names: `case9` (`ieee9`), `case14`, `case30`, `case57`, `case118`, `case300`, `case145`,
   `case24` (`rts`) and `cigre` call `IeeeCases`, and `uk` goes through `Gated.Dir` on `data/uk`. The longer `LoadNamed`
   list applies only to verbs that take a case argument, so `gridsim gb-full --once` is an unknown token. An unknown
   first token is `UnknownVerb` (`error: unknown command or case '<name>'`), exit 2, and never starts the live loop.
3. The engine is chosen. `PickSolver` returns a `SteadyStateSolver` over `BackwardForwardSweep` for a radial network (or
   with `--bfs`), else `SolverBackend.Pick(model)`, which resolves the Newton engine by `SolverSelector.ResolveNewton` at
   `SolverBackend.SparseThreshold` (sparse for every model on the CPU).
4. `SteadyStateSolver.Solve(model)` runs the engine inside the OLTC loop. The engine builds the Y-bus (`ComplexCsr` for
   the sparse engine), builds the bus specification (`NewtonShell.BuildBusSpec`), runs Newton passes (`NewtonShell.SolveStep`
   then `NewtonShell.ApplyStep`, which damps the largest correction to `0.4` and floors `Vm` at `0.2` pu), runs the
   reactive-limit loop (`NewtonShell.ApplyQLimits`), and assembles a `PowerFlowResult` with `NewtonShell.AssembleResult`
   and `ComputeLineFlows`. The OLTC loop steps taps and re-solves on internal clones until the taps sit inside their
   deadbands or `DefaultMaxOltcPasses` (`25`) passes are used.
5. `PrintDetailed` reports the result: `Totals`, the bus table, `BoundaryAnalysis.Evaluate`, the most-loaded branches, the
   voltage range against `VoltageLimits.OperationalLowPu` and `OperationalHighPu`, and, only on a converged state,
   `ProtectionAssessment.Assess`. A non-converged result prints `DID NOT CONVERGE` and sets `Environment.ExitCode = 3`.
   A converged result whose OLTC loop did not settle prints one extra line and keeps exit 0.

`case9` and `ieee9` are the exception: they call `PrintAndExitCase9`, which prints the report above and exits whether or
not `--once` is given. Without `--once`, every other bare case runs `RunLive`: each tick scales the base model with `GridScenario.AtLoad` at
`LoadProfile.DailyFactor(hour)` under `GcScope.LowLatency()`, solves it with the same `SteadyStateSolver` and renders it
to the console. `RunLive` does not use `LiveSim`.

### Live view frame: web and desktop

1. A host creates the feed. The web `SessionManager.Create(caseId, solver)` builds a `SimSession` through
   `SimSession.CreateLive`, `CreateReplay` or `CreateBridge`; the WPF `MainWindow` builds a `LiveSim` or loads a
   `PresolvedReplay` directly. Neither host references Gpu or `SolverBackend`.
2. `LiveSim` resolves its engine with `SolverSelector.Create(solver, model, sparseThreshold: 1500)` and wraps it in a
   `SteadyStateSolver`. When the resolved engine is sparse Newton (above 1500 buses on `Auto`, or by explicit choice) it runs
   with `MaxIterations = 40` and no Q-limit switching and skips the OLTC loop; a radial network uses the sweep with no OLTC loop; anything else runs the full regulated solve.
3. One tick is `LiveSim.Step(tick, stepMinutes)`: scale demand with `GridScenario.AtLoad` (or hold the base loads and warm
   start from `GridScenario.WithSolvedVoltages` in hold-steady mode), advance any storage fleet, apply passive-plant taps,
   apply the open-branch set through `TopologyOps.Apply` (islanding and blackout), solve, take `RegulatedModel` so the
   frame carries the solved tap ratios, advance the swing integrator, and return `SimFrame(Model, Result, Hour, Tick,
   FrequencyHz, GenerationMw, DemandMw, RocofHzPerS, ...)`. Only a converged result seeds the next warm start. The ambient
   load jitter uses a fixed seed (`LiveSolveSettings.LiveSimSeed`), so a live session is reproducible.
4. Two other producers build the same `SimFrame`: `PresolvedReplay` (loads a run directory of `exports/*.json` into a
   frame cursor, with telemetry from the sidecar CSV) and `GridBridgeClient` (rebuilds frames from streamed `state`
   messages against the cached topology). All three feed the same views, so replay and bridge are branches in the shell
   and not separate UIs.
5. The view layer consumes the frame. WPF: `MainWindow` runs `sim.Step` on a worker task, skips a tick while a solve is in
   flight, and on the UI thread calls `DashboardViewModel.Update(frame, boundaries, solveMs)`. Web: `SimSession.BuildEnvelope`
   hands the frame of a live session to `SessionCycleService.Offer`, calls `FrameProjector.Project` to produce a `FrameEnvelope` of per-tab
   DTOs, and raises `FrameProduced`; the Blazor `Dashboard` page and the `/ws/sim` endpoint (`SimWebSocket` through a
   bounded `SimOutbound` queue that drops the oldest frame) both subscribe to it. The web serialises DTOs and never view
   models, with non-finite doubles written as `null` by `NonFiniteDoubleConverter`.

### Cycle fan-out

The base Newton solve is single-core, and everything a control room needs after it converges reads the same solved frame
and parallelises.

1. `CycleRunner.RunOnce` calls `LiveSim.Step` for the base solve, then `CycleScheduler.RunCycle(frame, solverFactory,
   baseSolveMs)`. The frame is a shared reference, not a deep copy, because `GridModel` and `PowerFlowResult` are immutable
   by convention and jobs treat the bare arrays as read-only. The solver factory gives each worker its own engine.
2. The scheduler runs every High job unconditionally and never cancels it. It reserves their estimated cost, then admits
   Low jobs from a rotating pool while the running estimate fits the budget, and always admits at least one Low job
   per cycle even when its estimate does not fit. A Low job that completes or faults rotates to the back of the pool; one
   that is cancelled or deferred keeps the front, so later cycles cover it (round-robin, no starvation).
3. The currency is core-milliseconds: `CycleBudget(workers, windowMs)` owns `workers x windowMs`. Low jobs run against a
   deadline token linked to `windowMs`, which `AnalysisJobLibrary` folds into `PowerFlowOptions.Cancellation`, so a long
   deferrable solve aborts mid-iteration. `JobCostModel` keeps a per-job exponentially weighted average of measured
   core-ms (`CycleOptions.CostEwmaAlpha`) that feeds admission.
4. Workers drain the queue with a `Parallel.For` over an atomic cursor. A cycle is sequential; only the fan-out inside it is
   parallel.
5. `CycleResult` (`Scheduling/CycleResult.cs`) carries `CycleIndex`, `BaseSolveMs`, `WallMs`, `HighTotal`,
   `HighCompleted`, `LowAdmitted`, `LowCompleted`, `LowDeferred`, `LowCancelled`, `BudgetTotalCoreMs`,
   `BudgetSpentCoreMs`, `BacklogDepth`, `FromStaleFrame` and `Results`, with the derived `Utilization` and `HighTierMet`.
   Shard coverage is `ResidentCycleService.ShardsCovered` over `ShardsTotal`; the worst contingency is read from the
   `Results` payloads by the web DTO.
6. `ResidentCycleService` is the host-agnostic background driver both dashboards wrap: `Offer(frame, baseMs)` runs a cycle
   off the caller's thread, coalesces to the newest frame, publishes `Latest` and the rolling shard coverage, and raises
   `Updated`. The web `CycleTab.razor` and the WPF `CycleView` render it. The web host gates it with `Cycle:Enabled`
   (default `true` for live sessions) and sizes it with `Cycle:WindowMs`, `Cycle:Workers` and `Cycle:Shards`; its default
   worker count is the library default capped at `PresentationLimits.DefaultCycleWorkerCap` (`4`). The CLI
   `cycle` verb is the soak driver and exits 1 if High is ever missed.

### Estimation run: `gridsim estimate`

1. `RunEstimate` loads the case through `Gated.Dir` or `LoadNamed`.
2. The measurements arrive from `MeasurementSet.Load`, which rejects an unknown schema and runs
   `ForwardInferenceGuard.RejectFuture` on `TakenUtc` (a rejection prints `REJECTED (forward inference)` and exits 2), or
   from `SyntheticMeasurements.Sample` over a truth solved by `backend.Solver(model)` (`--synth`, seeded). `--phaseangle`
   adds angle rows through `PhaseAngleFeed`, guarded the same way; `--phaseangle-from-gda` only supplies the common-mode
   reference frame of those rows and has no effect without `--phaseangle`.
3. `EstimationOptions` carries the method, tolerance (`1e-8`), iteration cap (`30`) and bad-data switch. The CLI runs
   `new WlsStateEstimator().Estimate(model, set, options)`, or `HtiBadDataEstimation.Estimate` with `--hti`. The CLI
   estimator uses the default `DenseLuSolver`, not the selected GPU backend.
4. Inside `Estimate`: `MeasurementSet.Validate(model)`, the start state, the `IWlsStep` choice (dense methods fail loud
   above 3000 buses), a `MeasurementModel` over the active measurements, the observability gate
   (`ObservabilityAnalysis`), the Gauss-Newton loop with the trust cap, the optional IRLS reweighting, then the chi-square
   test and largest-normalised-residual removal in `BadDataAnalysis` (critical measurements are flagged and never
   removed), repeated until the set is clean or the removal cap is reached.
5. The result is an `EstimationResult` with the state, residuals, chi-square verdict, observability report and removals.
   `EstimateExport.WriteJson` writes `estimate-export.json` (`gridsim-estimate-export/1`); `--export-solve` also writes
   `estimated-solve-export.json` from `EstimationResult.ToPowerFlowResult` through `SolveExport.WriteJson`, an ordinary
   `gridsim-solve-export/1` document, so replay and bridge plumbing consume estimated states unchanged. `residuals.csv` is
   written beside them.
6. `--export-solve` writes `estimated-solve-export.json` only when the estimate converged. The exit code is `0` when the
   estimate converged and the network is observable, else `3`; with `--fdia-screen` it is also `3` for a suspected
   false-data injection and when the screen has no verdict (not converged, or the chi-square test did not run). With `--audit-log`, `AuditRunRecorder` writes one hash-chained entry whose
   provenance is `DerivedFromRecorded` (or `Synthetic` with `--synth`).
7. The GDA estimated-state pipeline batches such exports into the lake's `Derived/estimated_state` product, and the bridge
   streams them back as `source=estimated` frames. The resident path is `serve`: `RealtimeEstimatorEngine.Run` (or
   `RealtimeDseEngine` for `ekf`, `ukf` and `enkf`) estimates a stream of dated windows, guards each window's `TakenUtc` and
   each output stamp, and the web host `RealtimeEstimatorHostedService` runs the same engine once over an
   `IRealtimeWindowFeed` and publishes through `GET /api/realtime`.

### GDA build and replay

Build, `gridsim gda`:

1. `RunGda` resolves the lake root (`--root`, else `GDA_DATA_ROOT`, else `GDA_ROOT`), constructs `GdaPaths(root,
   registerAsOf)` (a missing root throws `DirectoryNotFoundException`, exit 1) and reads the register vintage with
   `--as-of`, which fails loud on an unparseable value.
2. The `--dno` choice selects the reader: `GdaModelBuilder.LoadNged(paths)`, `UkpnSource.Load(root, note)` or
   `NpgSource.Load(root)`. Each returns an `LtdsDataset`; `Ltds/LtdsReader` and `Csv/` read the tables and `Parquet.Net`
   reads the parquet-only sources.
3. `GdaModelBuilder.Build(ds, GdaBuildOptions)` returns `GdaBuildResult(Model, Diagnostics)`. Every figure it cannot take
   from a published table is a named default in `GdaBuildDefaults`, and every dropped row is counted in the
   diagnostics. `GeoLocator` places buses on the geography.
4. `GdaModelWriter.WriteDirectory(model, dir)` writes the JSON case folder, and `--solve` solves the model with
   `backend.Pick(kind)` (`--dense` or `--sparse` force the engine); a non-converged solve exits 3.

Replay, `gridsim gda-replay`:

1. `RunGdaReplay` loads the case (a case folder or a built-in name) and a `GdaTimeSeries` (`GdaTimeSeries.Load`, or
   `Synthetic`). `ForwardInferenceGuard.AssertHistorical` checks every step stamp at load (`GdaTimeSeries.Load`) and again once at the top
   of `GdaReplayLoop.Replay`, before the step loop.
2. `GdaReplayLoop.Replay` solves each step: `step.ApplyTo(base)` (`GridScenario.AtLoad`, then `Dispatcher.Balance`), the
   storage fleet if supplied, the passive plant if bound, warm start from the last converged result, then
   `SteadyStateSolver.Solve` on the engine from `SolverSelector.Create(kind, base, 1500)`.
3. The `onSolved` callback writes `exports/state-NNNNNN.json` through `SolveExport.WriteJson` for step 0 and every
   `--export-every` step, and the verb writes `state-estimate.csv` and `run-metadata.json` (`RunInfo.WriteJson`).
4. The exit code is `3` if any step did not converge, `2` for a forward-inference rejection.
5. The output directory is a run directory: `PresolvedReplay.Load` (the desktop replay picker and the web
   `CaseCatalog.LoadReplay`) turns `exports/*.json` back into `SimFrame`s. The CLI `replay` verb is unrelated: it plays a
   NESO frequency CSV (`--sps`, `--seconds`, `--frames`).

### Interposer window

1. `RunInterpose` loads the crosswalk (`RtuPointMap.Load`), looks up the protocol in `RtuProtocolRegistry`, and opens a
   transport with `TransportFactory.Open`, which binds a TCP listener and accepts one client.
2. The loop calls `IRtuTransport.TryReadFrame` (reassemble one frame, resynchronise on a bad lead-in) with a 200 ms
   timeout and passes each frame to `InterposePipeline.Ingest`.
3. `Ingest` runs `FrameCorrection.Validate` (the framing gate, with accepted and rejected tallies), then
   `IRtuProtocol.Decode`, which returns an `IReadOnlyList<RtuPoint>` with quality folded by `QualityFold`, and appends the points to the
   window stamped with the frame sequence number.
4. Every `--window-ms` (default `1000`) and once at shutdown, the verb calls `FlushWindow(DateTime.UtcNow, utcNow:
   DateTime.MaxValue)`, which hands the window to `RtuMeasurementSetMapper.Map`. The `DateTime.MaxValue` clock defers the
   horizon guard (the verb prints that the guard is deferred), so a window is written unguarded and the check runs when
   `serve` or `estimate` loads it. The mapper then does: horizon guard (inert here), latest value per address, drop by quality, resolve against the crosswalk,
   per-unit conversion, angle datum. The window is cleared even on failure, and a window that does not fit its crosswalk
   is discarded and counted in `MapErrors`.
5. The result is a `MeasurementSet` with source `rtu`. Two optional sinks take it: `--measurements-dir` writes it through
   `MeasurementSet.SaveWithoutOverwrite` (a `gridsim-measurements/1` file that `serve` or `estimate` reads), and
   `--out-store` writes it through `QuarantineLiveStore.WriteWindow` (GDA-shaped Parquet, firewalled from the lake). Both
   sinks open through `NeverOverwrite`, so an existing file is kept and a `-dupN` name is written; with `--monitor` and
   neither flag, nothing is written.
6. The verb exits 3 when any window was discarded. The estimator runs later and elsewhere: nothing here estimates.

### Bridge stream

1. A host constructs `GridBridgeClient`, calls `ConnectAsync(endpoint)` and then `StartAsync(case, start, fps, source)`.
   The server (in the GDA repository, specified in [ch. 9](<0009 GDA integration.md>)) sends the topology, then one
   `state` message per tick; the client acts on `topology`, `state`, `result` and `error`, and its dispatch ignores every
   other type.
2. The receive loop assembles each message from 64 KiB chunks and `Dispatch` routes by `BridgeProtocol.PeekType`.
   `HandleTopology` builds and caches the `GridModel` and raises `TopologyReceived`.
3. `HandleState` first refuses an undated frame, then calls `ForwardInferenceGuard.RejectFuture(timestamp, DateTime.UtcNow,
   "bridge state")` before any frame is built. A violation throws `ForwardInferenceException`, which ends the receive
   loop, fails every pending query and raises `Faulted`.
4. A passing `state` message becomes a `SimFrame` through `StateMessage.ToSimFrame(topology, tick)`, and the client raises
   `FrameReceived`, `StateReceived`, `MarketDataReceived`, `EstimateReceived` and the raw-source events. Queries
   (`Query(op, at, params)`) are correlated by `id` and time out after `QueryTimeout` (60 s).
5. The frame enters the same view path as a live frame (WPF `MainWindow`, web `SimSession.OnBridgeState`).

### Recorded-event gate

`gridsim replay-event`, `scenario`, `h0-study` and `evidence` share one pipeline. `EventCatalog.Load` (or
`Builtin`) reads dated events behind the horizon guard; `ReplayValidator.Validate` reproduces the recorded RoCoF and nadir
with no device and returns a `ReplayResult` whose verdict is `Reproduced`, `Marginal` or `Failed`; `ScenarioEngine.Run` and `H0StudyEngine` run counterfactual sweeps only when
the baseline is `Reproduced` and otherwise throw `BaselineNotReproducedException`; `EvidenceReport.Build` assembles the
dossier with each column hard-tagged by provenance. Exit 3 means the gate was not passed ([ch. 7](<0007 The evidence engine.md>)).

## Cross-cutting rules

### Determinism and bit-identity

Bit-identical results come from a short list of mechanisms, each in code:

- **One Newton step.** Dense and sparse Newton share `NewtonShell.SolveStep` (one CPU direct sparse LU) and
  `NewtonShell.ApplyStep` (one damped update), and assemble every quantity with the same products and sums in the same
  order. Two correct LU factorisations round differently, so the sparse elimination, the only one that runs at GB scale,
  is canonical.
- **Fixed orders.** Parallel sweeps write each result into a pre-allocated slot by index and rank with a stable sort;
  workers take a private engine from a `ThreadLocal`; Newton engines use ascending bus order.
- **Bit-exact kernels.** `SimdKernels.SubtractScaledRow` and `AddScaledRow` with `fastFma=false` do a separate multiply
  and add per lane, matching the scalar loop at every vector width. `DenseLuKernels` uses only the bit-exact kernel.
  `SimdCapabilities.Tier` is chosen at run time from `Vector512`, `Vector256` and `Vector128` `IsHardwareAccelerated`.
- **Cached structure, not cached numbers.** `CpuSparseLuSolver` reuses the RCM ordering because it is a deterministic
  function of the sparsity pattern. `SparseWlsStep` reuses the gain pattern and slot maps and refills every value.
- **One tolerance table.** `NumericTolerances` holds the singular-pivot floor (`1e-12` relative), the Krylov cap and
  tolerance. A pivot floor decides only what is declared singular, never the arithmetic of a non-singular solve.
- **Fixed seeds.** `LiveSolveSettings.LiveSimSeed` seeds the live load jitter; `SyntheticMeasurements` is a seeded
  sampler, exact at zero noise.
- **No hidden state in the model.** `GridModel` and the records under it are immutable; `SteadyStateSolver` rejects
  re-entry, and the Newton engines keep per-solve state and are not thread-safe, so parallel code builds one engine per
  thread.
- **Culture.** See Invariant culture below.
- **Orchestration changes are proved.** A change that only reorganises orchestration must leave every number unchanged,
  proved with exact `Assert.Equal`. The CI matrix runs the whole suite in three SIMD modes (`normal`, `avx2-off`,
  `hwintrinsic-off`) on Linux and Windows.

GPU arithmetic is outside the contract. The CPU dense and sparse engines are pinned; a GPU solve is an independent
cross-check. [Ch. 8](<0008 Provenance and invariants.md>) records the evidence per invariant.

### Fail loud

A non-converged, invalid or unsupported request ends in a message and a non-zero exit, never in a silent default:

- **Flags and verbs.** An unknown flag, a flag another verb owns, a value flag with no value, an unparseable number, an
  unknown enum value, an unknown verb or case and a conflicting pair all exit 2 (`knownFlags`, `VerbFlags`, `FailFlag`).
- **Exit codes.** `0` success; `1` input error or a failed check; `2` usage, licence refusal or forward-inference
  rejection; `3` validation or gate failure, including any verb whose power flow did not converge; `77` `crossval` only,
  when no Python or pandapower exists. The table is in the [CLI reference](<../manual/0019 CLI reference.md>).
- **Convergence contract.** `ConvergenceContract.RequireConverged` throws `NonConvergedStateException`; it is called by
  `ProtectionAssessment`, `FaultAnalysis`, `ProtectionCascade`, `LossContingency`, `SmallSignalAssembler`,
  `RmsTransientStability` and `SubtransientInit`. Other studies over a solved state (`WatchList.Assess`,
  `BoundaryAnalysis.Evaluate`, `PhysicsLaws.Audit`, the distribution-factor and loss-allocation studies) do not call it. A non-finite Newton step is never applied. The OLTC loop never
  steps a tap on a non-converged solve. `PowerFlowStatus` distinguishes `Converged`, `MaxIterations` and `NonFinite`.
- **Singular matrices.** Every elimination refuses a pivot under the shared floor; a singular batch member is isolated and
  marked, and a singular system is never solved to `NaN` silently.
- **Estimator limits.** `DenseEstimationLimits` fixes four sizes. Two fail loud: `DenseMethodMaxBuses` (`3000`) throws
  `NotSupportedException` for the dense-only methods, and `RecursiveFilterMaxState` (`2500`) fails the Kalman filter at
  construction. Two degrade: above `RankProofMaxState` (`2500`) the observability gate skips the numerical rank proof and
  returns the topology-only report with `Rank = -1`, and above `StateCovarianceMaxState` (`2000`) the per-bus confidence
  bounds stay `null`.
- **Process safety net.** A missing input path or `CaseLoadException` is `error: ...`, exit 1; any other unhandled
  exception is a defect and writes a crash log through `CrashReport` and `CrashLog` before the runtime's crash exit.
- **Unimplemented options.** An option the engine does not implement throws (`CycleOptions.PipelineBacklog` throws
  `NotSupportedException` in `CycleRunner`) instead of being accepted and ignored.
- **Audit.** `--audit-log` is accepted only on `estimate`, `crossval` and `interpose`, the log is opened and verified
  before the verb runs, and a broken hash chain stops the run ([ch. 31](<0031 Audit log and diagnostics.md>)).

### Forward-inference guard

GridSim is a historical state estimator. `ForwardInferenceGuard` (`src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs`)
fixes `HorizonHours = 169.0` and throws `ForwardInferenceException` for any stamp newer than `utcNow - 169 h`. The check
runs at every dated ingestion seam: `MeasurementSet.Load`, `HarmonicSourceSet.Load`, `EventCatalog.Validate`,
`NesoFrequencyCsv.Read`, `GdaTimeSeries.Load`, `GdaReplayLoop.Replay`, `RtuMeasurementSetMapper.Map`,
`PmuMeasurementSetMapper.Map`, the `GridBridgeClient` state handler, `RealtimeEstimatorEngine.Run`, `RealtimeDseEngine` and
the phase-angle feed; two engines also guard the stamp of each output through `RestrictionReview`. The bridge server holds
the same constant on its side, so each end is a complete defence if the other fails. The CLI verbs that load dated input
print `REJECTED (forward inference): <message>` and exit 2. The full seam table is in
[ch. 31](<0031 Audit log and diagnostics.md>) and the rationale for 169 hours in
[ch. 8](<0008 Provenance and invariants.md>).

### Provenance

`ProvenanceTag` (`src/GridSim.Core/Diagnostics/Provenance.cs`) has seven members: `Recorded`, `DerivedFromRecorded`,
`CuratedPlaceholder`, `Synthetic`, `Counterfactual`, `Interpolated` and `UnvalidatedAtScale`. Evidence records, asset
classes (`AssetClassProvenance`) and audit-log entries carry a tag that the constructor demands, so it cannot be
forgotten, and it survives serialisation. The tags are permissions: data moves down the trust ladder and never up. Two rules sit
on top. A `CuratedPlaceholder` asset-class parameter must carry a `[VERIFY]` marker in its note, enforced by
`AssetClassLibrary.ValidateProvenance` and pinned by `AssetProvenanceTests`; no general rule extends this to other
records. A `Counterfactual` result is labelled in its output as never an input to another replay; that is policy, and no
code rejects such an input. Ch. 8 owns the taxonomy.

### Invariant culture

The CLI (`Program.cs`), the web host (`GridSim.Web/Program.cs`) and the case editor (`EntryPoint.Main`) set
`CultureInfo.DefaultThreadCurrentCulture` and `CultureInfo.CurrentCulture` to `InvariantCulture` before anything else, so
a comma-decimal host cannot parse `0.5` as `5` or write `1,2340` into a CSV that the invariant reader would then
mis-column. Flags parse with `NumberStyles.Float` and `InvariantCulture`; `NaN` and `Infinity` are refused as flag values.
The desktop app (`GridSim.Wpf/App.xaml.cs`, `OnStartup`) does the same before its first window exists, and also sets
the WPF element language (`FrameworkElement.LanguageProperty`) to the invariant culture, because XAML `StringFormat`
bindings format with the element language, not the thread culture.

### Licence gate

Every user-facing model load in the CLI goes through `Gated.Dir`, `Gated.Matpower` or `Gated.V2Case`, so the tier's bus
limit (`LicenceGate`, `Entitlement`) is enforced in one place; networks under the repository's `data/` tree are bundled and
exempt (`BundledNetworks`), and so are the built-in `IeeeCases`. The licence resolves offline, never over the network, in
the order `GRIDSIM_LICENCE`, the installed licence file, then the free Personal tier. The web host resolves it once and
hands it to `CaseCatalog` ([ch. 22](<0022 Licensing - key format and trust model.md>)).

### Versioning

`Directory.Build.props` holds the single `<Version>` for every project. No `.csproj` sets `Version`, `VersionPrefix`,
`AssemblyVersion`, `FileVersion` or `InformationalVersion`. The SDK derives `AssemblyVersion` and `FileVersion` as
`<Version>.0` and `InformationalVersion` as `<Version>+<full commit sha>`; `IncludeSourceRevisionInInformationalVersion`
keeps the suffix, and a container build passes the commit with `-p:SourceRevisionId`. `GridSim.Core.BuildInfo` reads the
informational version and splits it into `Version`, `Commit` and `DisplayLine`; `gridsim --version`, the web footer, the
web OpenAPI document and the desktop About box show it. A bump is one edit to that file plus a matching `## [<Version>]`
heading in `CHANGELOG.md` in the same commit. The CLI additionally stamps the git commit and build configuration into the
assembly (`StampGitCommit` target) for `RunInfo`.

### Build rules and embedded resources

`Directory.Build.props` turns on `Nullable`, `TreatWarningsAsErrors`, the .NET code analysers at `latest` level and
`AnalysisMode` `Recommended`, with rule severities tuned in `.editorconfig`; a warning is a build failure. The CLI embeds
`docs/manual/0019 CLI reference.md` as `GridSim.Cli.19-cli-reference.md`, so `gridsim help <verb>` prints the manual's own
section and the build fails if the chapter is missing. Core embeds the bundled MATPOWER cases and the schemas, and the
CLI copies the JSON, CSV and MATPOWER files under `data/` and the JSON files under `schemas/` next to the binary.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Power-flow sparse threshold | `0` | buses | Newton solves above it are sparse; `0` makes every CPU Newton solve sparse | `SolverSelector.DefaultSparseThreshold` |
| Dense and sparse crossover | `200` | buses | GPU routing, solve-export engine and the WLS observability report (the CPU estimate itself is the same sparse arithmetic either side) | `SolverSelector.DenseSparseCrossoverBuses`, `EstimationOptions.SparseBusThreshold` |
| Live per-tick threshold | `1500` | buses | Above it, or when sparse Newton is chosen explicitly, the live and GDA-replay loops run sparse with the options below | `LiveSolveSettings.SparseThresholdBuses` |
| Live sparse iteration cap | `40` | iterations | Newton cap of a sparse per-tick solve; no Q-limit switching | `LiveSolveSettings.SparseMaxIterations` |
| OLTC pass cap | `25` | passes | Outer tap-loop cap | `SteadyStateSolver.DefaultMaxOltcPasses` |
| Newton tolerance | `1e-8` | pu | Largest power mismatch | `PowerFlowOptions.Tolerance` |
| Newton iteration cap | `30` | iterations | Inner cap | `PowerFlowOptions.MaxIterations` |
| Reactive-limit pass cap | `12` | passes | Outer Q-limit cap | `PowerFlowOptions.MaxQLimitPasses` |
| Largest undamped Newton step | `0.4` | rad or pu | Larger steps are scaled down | `NewtonShell.MaxUndampedStep` |
| Voltage floor after a step | `0.2` | pu | Floor on a PQ bus `Vm` | `NewtonShell.VmFloorPu` |
| Relative pivot floor | `1e-12` | relative | A pivot at or below it is singular | `NumericTolerances.RelativePivotFloor` |
| Krylov iteration cap | `4n + 200` | iterations | BiCGSTAB cap for order `n` | `NumericTolerances.DefaultKrylovMaxIterations` |
| Dense estimation methods cap | `3000` | buses | Constrained, Feasible and QR fail loud above it | `DenseEstimationLimits.DenseMethodMaxBuses` |
| Rank-proof size | `2500` | states | Sparse backend runs the numerical rank proof at or below it; above it the gate returns the topology-only report | `DenseEstimationLimits.RankProofMaxState` |
| State-covariance size | `2000` | states | Per-bus confidence bounds are computed at or below it and are `null` above it; `HtiBadDataEstimation.MaxState` reuses it | `DenseEstimationLimits.StateCovarianceMaxState` |
| Recursive-filter size | `2500` | states | The Kalman filter fails at construction above it; the streaming engine applies it to `ekf`, `ukf` and `enkf` | `DenseEstimationLimits.RecursiveFilterMaxState` |
| Estimator tolerance | `1e-8` | radians or pu | Largest state change | `EstimationOptions.Tolerance` |
| Estimator iteration cap | `30` | iterations | Gauss-Newton cap | `EstimationOptions.MaxIterations` |
| Feasible voltage floor | `1e-6` | pu | Interior guard | `FeasibilityConstraints.VmFloor` |
| Forward-inference horizon | `169.0` | hours | Newest processable stamp is now minus this | `ForwardInferenceGuard.HorizonHours` |
| Cycle window | `1000` | ms | Wall-clock deadline of a cycle | `CycleOptions.WindowMs` |
| Cycle workers | processor count minus one, at least 1 | threads | Fan-out width; the web host caps its default at `4` | `CycleOptions.Workers`, `PresentationLimits.DefaultCycleWorkerCap` |
| Cycle N-1 shards | `8` | shards | Low-tier N-1 rotation | `CycleOptions.NMinus1Shards` |
| Hot branch loading | `80.0` | percent | Loading at which a branch joins the High N-1 set | `CycleOptions.HotBranchLoadingPercent` |
| Cycle harmonics gate | `1500` | buses | Harmonics job returns nothing above it | `CycleOptions.HarmonicsBusLimit` |
| Cost-model smoothing | `0.3` | fraction | Weight of the newest core-ms measurement | `CycleOptions.CostEwmaAlpha` |
| Live load seed | `20260605` | seed | Ambient load jitter generator | `LiveSolveSettings.LiveSimSeed` |
| Live step | `20` | minutes per tick | Default simulated step | `LiveSolveSettings.DefaultStepMinutes` |
| Bridge query timeout | `60` | seconds | Wait for a matching `result` | `GridBridgeClient.QueryTimeout` |
| Bridge default source | `presolved` | name | Source of `StartAsync` | `GridBridgeClient.StartAsync` |
| GPU FP64 smoke tolerance | `1e-12` | absolute | kept for compatibility; the smoke test is now exact (`(1 + 2^-40) - 1 == 2^-40`) | `GpuContext.Fp64SmokeTolerance` |
| Interposer window | `1000` | ms | Window length | `--window-ms` of `RunInterpose` |

## Invariants

| Invariant | Pinned by |
|---|---|
| One `<Version>` in `Directory.Build.props`, no project overrides it, `CHANGELOG.md` has its heading, `BuildInfo` reports it, and Core, Gda, Gpu, Web and the test assembly carry the identical informational version | `VersionContractTests` (`No_project_file_sets_its_own_version`, `Changelog_has_a_heading_for_the_current_version`, `Core_build_info_reports_the_props_version`, `Every_loadable_gridsim_assembly_carries_the_same_stamp`) |
| `--version` is answered before the licence and the flag gate | `VersionContractTests.Cli_answers_version_before_any_other_processing`, `CliProcessSmokeTests` |
| Every dispatched verb is listed by `--help`, `--help` names no undispatched verb, and every verb has a section in the CLI reference and its own help | `CliHelpContractTests`, `VerbHelpTests` |
| The CLI reference is embedded in the build, and the help text is plain ASCII with the exit-code rule | `VerbHelpTests.The_chapter_is_embedded_in_the_cli_build`, `Every_help_text_is_plain_ascii_with_a_title_and_the_exit_code_rule` |
| Flag helpers never substitute a default for a bad or missing value, and flags the CLI accepts are not ignored | `CliFailLoudFlagContractTests`, `CliLiteralsAndFlagsContractTests` |
| Every reported non-convergence exits 3, and fault, protection and the default report refuse or skip a non-converged state | `CliExitCodeContractTests`, `CliProcessSmokeTests` |
| The engine rule lives in `SolverSelector`: `DefaultSparseThreshold` is `0`, the crossover is `200`, the sweep is never chosen by the Newton rule, and `CreateNewton` builds the resolved engine | `SolverRoutingTests` |
| No CLI file chooses an engine by itself | `SolverRoutingTests.No_local_dense_sparse_threshold_remains_in_the_cli`, `CliEngineChoiceScanTests` |
| Contingency screens and the loss sweep match an explicit dense engine bit for bit on the default engine | `DefaultEngineRoutingTests` |
| Dense and sparse Newton are bit-identical on the CPU across the built-ins, the bundled MATPOWER files and the JSON cases | `DenseSparseParityTests` |
| A GPU solve that gives up is retried on the exact CPU LU | `GpuFallbackTests` |
| GPU backends match the CPU solvers within solver tolerance and reject singular and mismatched input | `GpuSolverSeamTests`, `RocmSolverSeamTests` |
| The batched in-place LU is bit-identical to the single-system LU | `BatchedInPlaceLuBitIdentityTests` |
| The bit-exact SIMD kernels equal the scalar loop at every tier, and the active tier is what the hardware flags say | `SimdTierSeamTests` |
| Estimator kernels equal their scalar references to the bit | `ResidualCovarianceBitIdentityTests`, `ObservabilityRankBitIdentityTests`, `HouseholderQrLayoutBitIdentityTests`, `KalmanDenseKernelBitIdentityTests` |
| Estimator limits keep their values; the dense-method bus cap fails loud above it | `DenseEstimationLimitsTests` |
| A study refuses a non-converged state | `ConvergenceContractTests`, `ConvergenceContractStudyTests` |
| The horizon is a single knob, every dated seam rejects a fresh stamp, and the output guard matches the input horizon | `ForwardInferenceGuardTests`, `RestrictionInvariantTests` |
| The inertia model seam routes bit for bit to `FrequencyDynamics` | `InertiaModelSeamTests` |
| The cycle budget is workers times window, High is always admitted and Low is gated by budget (the first Low job of a cycle is always admitted, which no test covers), Low rotates to full coverage, results do not depend on worker count, the fan-out never mutates the shared frame, and a faulting job is isolated | `Scheduling/CycleSchedulerTests` |
| A cancellation during the High drain stops no High job and starts no Low job; the default token is bit-for-bit identical to no token; a cancelled token aborts a solve | `Scheduling/CycleDeadlineDeterministicTests`, `Scheduling/SolverCancellationTests` |
| A `CuratedPlaceholder` without `[VERIFY]` is a violation; recorded and derived tags need none | `AssetProvenanceTests` |
| Result files are written without locale influence | `CliResultWriterTests` (runs under `de-DE`) |
| CI covers both operating systems and all three SIMD modes, the release refuses a tag that disagrees with the version, and the vulnerable-package gate exists | `WorkflowContractTests` |
| Every shipped package appears with its version in the third-party notices | `ThirdPartyNoticesTests` |
| The real web host starts in memory (middleware order, replay-only switch, version in the footer, OpenAPI document), and its DTOs carry exactly the solver's numbers | `WebHostTests`, `GridSimWebProjectionTests` |

## Worked example

`gridsim case9 --once` exercises the whole chain of the first flow on the smallest case. The block below is
`docs/_captures/start/case9-once.txt` with the bus and branch tables elided; its stamps and timings, where a capture
has them, differ by machine and run. `case9` loads at its WSCC nominal frequency, so the second line ends `60 Hz`; the
power flow does not use the frequency.

```
$ gridsim case9 --once
GridSim  -  IEEE 9-bus (WSCC)
9 buses  9 branches  3 generators  base 100 MVA  60 Hz
========================================================================
Converged (4 NR iters, 0 OLTC passes, max mismatch 1.65E-014 p.u.)
Generator output 0.32 GW  Demand 0.32 GW  Losses 4.6 MW  Inertia 7.5 GVAs
...
Voltage range: 0.996 - 1.040 p.u. (in band)
Total losses: 4.64 MW, -92.16 MVAr
Protection simulation: no relay would operate on this state (generic settings).
[exit 0]
```

Reading it against the architecture:

- `case9` is a built-in name, so `Program.cs` calls `IeeeCases.Case9()` and, because it is not an export, `PrintAndExitCase9`
  calls `PrintDetailed(model, backend.Pick(model))`. The model never touches `Gated`, so the licence gate does not count it.
- `SolverBackend.Pick` resolves the engine with `SolverSelector.ResolveNewton(model, SparseThreshold)`. The CPU backend's
  `SparseThreshold` is `DefaultSparseThreshold` (`0`), and 9 buses is above 0, so the engine is
  `SparseNewtonRaphsonPowerFlow` on `CpuSparseLuSolver`, wrapped in a `SteadyStateSolver`. case9 has 9 buses and 9 in-service
  branches, not buses minus one, so `LooksRadial` is false and the sweep is not considered.
- `Converged (4 NR iters, 0 OLTC passes, ...)` is `PowerFlowResult.Iterations` and `SteadyStateSolver.OltcPasses`: four
  Newton passes to a largest mismatch of `1.65E-014` pu against `PowerFlowOptions.Tolerance` of `1e-8`, and no OLTC in the
  case, so the loop is a single pass. The dense engine returns the same bits on this case (`DenseSparseParityTests`).
- The balance is checkable by hand from the elided bus table. The loads are 90, 100 and 125 MW, 315 MW in all. The three
  generators supply `71.6 + 163.0 + 85.0 = 319.6` MW, and the difference of `4.6` MW is the `Losses 4.6 MW` figure, whose
  exact value is the `4.64 MW` line (`PowerFlowResult.TotalLossMw`, the sum of the net injections). `Generator output 0.32
  GW` is `SolvedGeneration.GeneratorOutputMw`, the generators' own output, not the sum of positive bus injections.
- The last line is `ProtectionAssessment.Assess` on the converged state, which is skipped for a non-converged one. The
  `[exit 0]` marker is the capture's exit status: `PrintDetailed` sets exit code 3 only when `Converged` is false.

The version stamp the same chain reports is `docs/_captures/start/version.txt`:

```
$ gridsim --version
gridsim 1.95.467+203c6b9cc0edff55f2bd73642c94dc0963a82b92
[exit 0]
```

The release number is the single `<Version>` and the suffix is the commit the binary was built from; both come from
`BuildInfo.InformationalVersion`.

## Source map

| File | Type or function | Responsibility |
|---|---|---|
| `GridSim.slnx` | solution | The eight projects. |
| `Directory.Build.props` | build properties | Code-analysis and warning policy, the single `<Version>`, CI path mapping. |
| `src/GridSim.Core/BuildInfo.cs` | `BuildInfo` | Release number and commit from the informational version. |
| `src/GridSim.Core/Abstractions/` | `IPowerFlowSolver`, `ILinearSolver`, `ISparseLinearSolver`, `IBatchedLinearSolver`, `IComplexLinearSolver`, `IInertiaModel` | The seam contracts. |
| `src/GridSim.Core/PowerFlow/SolverSelector.cs` | `SolverSelector`, `SolverKind` | Engine choice and the three threshold constants. |
| `src/GridSim.Core/PowerFlow/SteadyStateSolver.cs` | `SteadyStateSolver` | OLTC loop, `RegulatedModel`, `TapsSettled`. |
| `src/GridSim.Core/PowerFlow/NewtonShell.cs` | `NewtonShell` | The shared Newton step, update, bus specification, reactive limits, result assembly. |
| `src/GridSim.Core/PowerFlow/ConvergenceContract.cs` | `ConvergenceContract`, `NonConvergedStateException` | Refusal of a non-converged state. |
| `src/GridSim.Core/PowerFlow/CpuSparseLuSolver.cs`, `DenseLuSolver.cs`, `CpuBatchedLuSolver.cs` | CPU linear backends | The reference solvers. |
| `src/GridSim.Core/Numerics/NumericTolerances.cs` | `NumericTolerances` | The one tolerance table. |
| `src/GridSim.Core/Numerics/SimdKernels.cs`, `SimdCapabilities.cs` | `SimdKernels`, `SimdCapabilities` | Vector kernels and run-time tier dispatch. |
| `src/GridSim.Core/Estimation/WlsStateEstimator.cs`, `WlsStep.cs`, `FeasibleWlsStep.cs`, `JacobianSinks.cs`, `DenseEstimationLimits.cs` | estimator and step backends | The estimator loop, the `IWlsStep` seam, limits. |
| `src/GridSim.Core/Assets/IAssetDynamics.cs`, `AssetBinding.cs`, `AssetDynamicsFleet.cs`, `PassivePlantFleet.cs` | asset seam | Active and passive asset contracts and fleets. |
| `src/GridSim.Core/Protection/ProtectionRelays.cs`, `ProtectionAssessment.cs`, `ProtectionCascade.cs` | relay overlay | The passive protection layer. |
| `src/GridSim.Core/Simulation/LiveSim.cs`, `LiveSolveSettings.cs`, `GdaReplayLoop.cs`, `PresolvedReplay.cs` | frame producers | `SimFrame` and its producers. |
| `src/GridSim.Core/Scheduling/` | cycle engine | `CycleRunner`, `CycleScheduler`, `AnalysisJobLibrary`, `ResidentCycleService`. |
| `src/GridSim.Core/Interpose/` | interposer | Pipeline, protocols, crosswalk, mapper. |
| `src/GridSim.Core/Net/GridBridgeClient.cs`, `src/GridSim.Core/IO/BridgeProtocol.cs` | bridge | Client and wire schema. |
| `src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs`, `Provenance.cs`, `AuditLog.cs`, `CrashLog.cs` | guarantees | Horizon, provenance, audit, crash log. |
| `src/GridSim.Core/Licensing/` | licensing | Key, gate, entitlement, bundled networks. |
| `src/GridSim.Cli/Program.cs` | top-level statements | Dispatch, flag gate, every verb. |
| `src/GridSim.Cli/SolverBackend.cs` | `SolverBackend` | CPU or GPU backend and engine selection. |
| `src/GridSim.Cli/Gated.cs` | `Gated` | The licence choke point for model loads. |
| `src/GridSim.Cli/VerbHelp.cs`, `VerbFlags.cs` | `VerbHelp`, `VerbFlags` | Per-verb help and flag gate read from the embedded reference. |
| `src/GridSim.Cli/CliResultWriter.cs`, `RunInfo.cs`, `CrashReport.cs`, `InterposeTransport.cs` | CLI support | Result files, run metadata, crash report, TCP transports. |
| `src/GridSim.Gpu/GpuContext.cs`, `GpuDenseLinearSolver.cs`, `GpuSparseLinearSolver.cs`, `GpuBatchedDenseSolver.cs` | ILGPU backends | CUDA and OpenCL solvers. |
| `src/GridSim.Gpu/RocmContext.cs`, `RocmInterop.cs`, `RocmDenseLinearSolver.cs`, `RocmSparseLinearSolver.cs` | ROCm backends | Native HIP solvers. |
| `src/GridSim.Gda/GdaModelBuilder.cs`, `GdaModelWriter.cs`, `GdaPaths.cs` | lake builder | LTDS dataset to `GridModel` and the case writer. |
| `src/GridSim.Gda/Interpose/QuarantineLiveStore.cs` | `QuarantineLiveStore` | Firewalled Parquet sink of the interposer. |
| `src/GridSim.Web/Program.cs` | host setup | Culture, licence, services, REST and WebSocket mapping. |
| `src/GridSim.Web/Sessions/SessionManager.cs`, `SimSession.cs` | sessions | Feed creation, frame production. |
| `src/GridSim.Web/Projection/FrameProjector.cs` | `FrameProjector` | `SimFrame` to per-tab DTOs. |
| `src/GridSim.Web/Api/RestApi.cs`, `SimWebSocket.cs`, `SimOutbound.cs` | API | REST under `/api`, `/ws/sim`. |
| `src/GridSim.Wpf/MainWindow.xaml.cs`, `DashboardViewModel.cs` | desktop shell | Tick loop, view-model update. |
| `src/GridSim.SchemaEditor/EntryPoint.cs`, `Ui/` | case editor | Key map, renderer, views. |
| `tests/GridSim.Tests/` | tests | The suite; contract tests read `Program.cs` and Wpf source. |

## Limits

- No test pins the dependency directions between projects or Core's zero-package rule; the `.csproj` files are the pin,
  and `ThirdPartyNoticesTests` checks only that every shipped package is named in the notices.
- `IBatchedLinearSolver` has one consumer, the `batchsolve` verb; no analysis, including the contingency sweeps, calls it.
  Batching a full AC N-1 across divergent Newton iteration counts, and memory-chunking very large batches, is not
  implemented.
- GPU backends are outside bit-identity, and the ROCm solvers are built against the ROCm 6.x headers and have had no
  runtime run on Instinct hardware.
- The dense estimator step (`DenseWlsStep`, used to solve an estimate only with a GPU linear solver) and the sparse step
  are not bit-identical to each other; on the CPU every WLS and robust estimate takes the sparse step at every size
  (`CanonicalWlsParityTests`).
- `tools/AssetSmoke` is compiled by every build but run by nothing: no CI job or test starts it, so a page that crashes
  only at template load is still caught only by running it by hand on Windows.
- The WPF source picker offers `presolved`, `estimated` and `live` only; `solves` and `raw` are reachable from the web host.

## See also

- [ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>) - the records every flow carries.
- [ch. 3, Power flow](<0003 Power flow.md>) - inside `IPowerFlowSolver`.
- [ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) - inside the linear-solver seams.
- [ch. 6, State estimation](<0006 State estimation.md>) - inside `IWlsStep`.
- [ch. 7, The evidence engine](<0007 The evidence engine.md>) - the recorded-event gate.
- [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) - the guard, the tags and the numerical invariants.
- [ch. 9, GDA integration](<0009 GDA integration.md>) - the lake builder, the replay loop and the bridge protocol.
- [ch. 10, File and wire formats](<0010 File and wire formats.md>) - every schema.
- [ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>) - the swing stack behind `IInertiaModel`.
- [ch. 11, Validation](<0011 Validation.md>) - the cross-checks that validate the solver and the estimator.
- [ch. 20, The RTU/SCADA interposer](<0020 The RTU and SCADA interposer.md>) - the interposer pipeline.
- [ch. 31, Audit log and diagnostics](<0031 Audit log and diagnostics.md>) - the audit log and the guard's seams.
- [ch. 35, Deployment and security](<0035 Deployment and security.md>) - containers, the web host, CI and release workflows.
- [../manual/0001 Getting started.md](<../manual/0001 Getting started.md>) - building and running the projects.
- [../manual/0029 Extending GridSim.md](<../manual/0029 Extending GridSim.md>) - adding an implementation behind a seam.
- [../manual/0019 CLI reference.md](<../manual/0019 CLI reference.md>) - the verbs that drive these flows.
- [../manual/0008 The desktop app.md](<../manual/0008 The desktop app.md>) - the WPF dashboard.
- [../manual/0033 The case editor.md](<../manual/0033 The case editor.md>) - the case editor.
