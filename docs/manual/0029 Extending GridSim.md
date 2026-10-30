# 29. Extending GridSim

GridSim grows through a small number of deliberate seams. A seam is an interface, an enum or a registry in
`GridSim.Core` with one place that selects an implementation, a shipped implementation to copy, and tests that pin the
contract. This chapter is for a developer who adds to the engine: a solver backend, an estimation method, a measurement
kind, an asset class, a protection, a file format, a DNO reader, an analysis job, a CLI verb, a desktop view or a web
tab. For each seam it names the type and its file, gives a minimal skeleton that compiles against the current tree,
lists the tests to copy, and states the mistakes that cost time. The rules every extension follows come first, because
they decide whether a change is accepted. Depth on each subsystem is in the technical manual, and every section links
the chapter that owns it.

## Before you start

Build the solution from the repository root. Warnings are errors (`Directory.Build.props` sets
`TreatWarningsAsErrors` and the .NET analysers at `Recommended`), so a skeleton that compiles with a warning does not
compile here:

```bash
dotnet build GridSim.slnx -c Release
```

Every skeleton in this chapter was built as ordinary source files in a scratch project that references `GridSim.Core`
(and `GridSim.Gda` for the DNO reader, `GridSim.Web` for the tab projection and `GridSim.Wpf` for the view model), the CLI
verb was built into a copy of `GridSim.Cli`, and the test snippets were run with xunit in a second scratch project. The
scratch projects live under `results/`, which git ignores, and are never committed. A scratch project under the
repository root takes `Directory.Build.props`, so the analysers apply to it as they do to `src/`.

A skeleton is shown as a whole file: its `using` lines, then a file-scoped `namespace`. A public type in the global
namespace raises CA1050, which is an error here, so every skeleton sits in a namespace (`GridSim.Samples` unless the
text names another), and a test snippet needs a `using` for the namespace of the skeleton it calls. The scratch project
enables `ImplicitUsings`, which supplies `System`, `System.IO`, `System.Linq` and `System.Collections.Generic`. The
estimator and measurement skeletons use `internal` types. `src/GridSim.Core/GridSim.Core.csproj` grants
`InternalsVisibleTo` to the assembly named `GridSim.Tests` (and to `GridSim.SchemaEditor`), so a scratch project with that
assembly name sees them exactly as the test project does:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>GridSim.Tests</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\GridSim.Core\GridSim.Core.csproj" />
  </ItemGroup>
</Project>
```

To run one family of the existing tests while you work, filter by class name:

```bash
dotnet test GridSim.slnx -c Release --no-build --filter "FullyQualifiedName~SolverSelectorTests"
```

## The seams at a glance

| Seam | Contract and file | Where an implementation is selected | Owning chapter |
|---|---|---|---|
| Power-flow engine | `IPowerFlowSolver` (`src/GridSim.Core/Abstractions/IPowerFlowSolver.cs`), `SolverKind` | `SolverSelector` (`src/GridSim.Core/PowerFlow/SolverSelector.cs`), `SolverBackend` (`src/GridSim.Cli/SolverBackend.cs`) | [Technical 3](<../technical/0003 Power flow.md>) |
| Dense and sparse linear solver, GPU | `ILinearSolver`, `ISparseLinearSolver` (`src/GridSim.Core/Abstractions/`) | `SolverBackend.DenseLinear`, `SparseLinear` | [Technical 4](<../technical/0004 Sparse and GPU numerics.md>) |
| Batched dense solver | `IBatchedLinearSolver` | `SolverBackend.DenseBatch` | [Technical 4](<../technical/0004 Sparse and GPU numerics.md>) |
| Complex linear solver | `IComplexLinearSolver` | the `solver` argument of the harmonic entry points | [Technical 19](<../technical/0019 Harmonics.md>) |
| Inertia model | `IInertiaModel` | `InertiaModelSelector` (`src/GridSim.Core/Dynamics/InertiaModelSelector.cs`) | [Technical 5](<../technical/0005 Frequency dynamics.md>) |
| Estimation method | `IWlsStep` (`internal`), `EstimationMethod` (`src/GridSim.Core/Estimation/WlsStep.cs`, `WlsStateEstimator.cs`) | `WlsStateEstimator.Estimate`, `gridsim estimate --method` | [Technical 6](<../technical/0006 State estimation.md>) |
| Measurement kind | `MeasurementKind` (`src/GridSim.Core/Estimation/Measurement.cs`) | `MeasurementModel` | [Technical 6](<../technical/0006 State estimation.md>) |
| Asset class and kind | `IActiveAssetDynamics`, `IPassiveAssetDynamics`, `AssetKind` (`src/GridSim.Core/Assets/`) | `AssetControllerFactory`, `asset-classes.json` | [Technical 13](<../technical/0013 Asset-dynamics data grounding.md>), [5](<../technical/0005 Frequency dynamics.md>) |
| Protection | `ProtectionStage`, `ProtectionEvaluator`, `ProtectionController` (`src/GridSim.Core/Assets/Protection/`) | `protections.json` | [Technical 15](<../technical/0015 Protections.md>) |
| Network importer and exporter | a static parse or write over `GridModel` (`src/GridSim.Core/IO/`) | `Gated` (`src/GridSim.Cli/Gated.cs`), the `convert` verb | [Technical 10](<../technical/0010 File and wire formats.md>), [28](<../technical/0028 The v2 JSON schema.md>) |
| DNO reader | `LtdsDataset` (`src/GridSim.Gda/GdaModelBuilder.cs`) | `gridsim gda --dno` | [Technical 9](<../technical/0009 GDA integration.md>) |
| Analysis job | `AnalysisJob` (`src/GridSim.Core/Scheduling/AnalysisJob.cs`) | `AnalysisJobLibrary.Build` | [Technical 1](<../technical/0001 Architecture.md>) |
| Evidence capability | `GridEvent`, `DeviceConfig`, `SweepAxes` | `EvidenceReport.Build` | [Technical 7](<../technical/0007 The evidence engine.md>) |
| CLI verb | the dispatch in `src/GridSim.Cli/Program.cs` | [`0019 CLI reference.md`](<0019 CLI reference.md>) | [Manual 19](<0019 CLI reference.md>) |
| Desktop view, web tab | `Notify` view model, `FrameEnvelope` DTO | `DashboardViewModel.Update`, `Dashboard.razor` | [Manual 8](<0008 The desktop app.md>), [10](<0010 Analysis views.md>) |

The full contract for each row, with every shipped implementation, is the Seams section of
[Technical 1, Architecture](<../technical/0001 Architecture.md>).

## Rules every extension follows

These come from the acceptance criteria in [`CONTRIBUTING.md`](../../CONTRIBUTING.md). A change that breaks one is wrong, however useful the feature.

1. **Results are bit-identical.** Across runs, across parallel paths, across CPUs (AVX-512, AVX2 and non-FMA) and between
   the dense and sparse CPU Newton engines. A change that only reorganises orchestration leaves every number unchanged,
   and you prove it with an exact comparison, not a tolerance:

   ```csharp
   Assert.Equal(BitConverter.DoubleToInt64Bits(before[i]), BitConverter.DoubleToInt64Bits(after[i]));
   ```

   New numerics on a validated path need re-validation. GPU arithmetic is outside the contract, and the CPU solvers are
   the reference a GPU backend is compared with. The mechanisms that deliver bit-identity (one shared Newton step,
   fixed summation orders, bit-exact SIMD kernels, results written into pre-allocated slots by index, one private
   engine per worker) are listed in [Technical 1](<../technical/0001 Architecture.md>) and, with the test that
   pins each, in [Technical 8](<../technical/0008 Provenance and invariants.md>).
2. **The tests are constitutional.** Never edit, loosen, re-baseline or delete a test to make a change pass. A test may
   change only with a written mathematical justification, agreed with the maintainer first and recorded in the commit
   and the CHANGELOG. Adding tests is always allowed.
3. **A new feature is off, or absent, by default, and its absence is proved.** `DynamicsConfig.EnableAssetDynamics` is
   `false`; `GridModel.AssetDynamics` and `GridModel.Protections` are `null` unless a case ships the files. The extension
   ships a reduction test showing that with nothing bound the old path is reproduced to the bit
   (`AssetDynamicsReductionTests`).
4. **Fail loud.** A non-converged, invalid or unsupported request ends in a message and a non-zero exit, never in a
   silent default and never in a number that looks like success. A flag, enum value, file key, class kind or parameter the
   code does not read is refused, not ignored. A non-finite value is never applied or reported as converged.
5. **Dated input passes the forward-inference guard.** GridSim reconstructs the past. Any reader of a timestamped input
   calls `ForwardInferenceGuard.RejectFuture` (`src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs`), which rejects a
   stamp newer than `utcNow` minus `ForwardInferenceGuard.HorizonHours` (169 h). A CLI verb reports it as
   `REJECTED (forward inference): ...` and exits 2 ([Technical 31](<../technical/0031 Audit log and diagnostics.md>)).
6. **Every unmeasured number carries provenance.** A parameter that is not grounded in a record is tagged
   `CuratedPlaceholder` and its note contains the literal `[VERIFY]`; the loader enforces it. The tag taxonomy is owned by
   [Technical 8](<../technical/0008 Provenance and invariants.md>) and is not restated in each section here.
   Fixtures are synthetic: never commit real DNO or lake data.
7. **Numbers are parsed and written with the invariant culture.** The CLI pins `CultureInfo.CurrentCulture` and
   `DefaultThreadCurrentCulture` at start-up; any other entry point you add does the same, and a CSV writer never relies
   on the host's decimal separator.
8. **Engine choice lives in one place.** A CLI file other than `SolverBackend.cs` never builds a Newton engine or compares
   a bus count to pick one, except the benchmark verbs, which carry a reasoned `// routing-exempt:` marker. `CliEngineChoiceScanTests`
   and `SolverRoutingTests` fail if any other file does.
9. **Each slice carries its documentation, version bump and CHANGELOG entry** (see [Delivering a slice](#delivering-a-slice)).

## A power-flow engine

**Seam.** `IPowerFlowSolver.Solve(GridModel model, PowerFlowOptions? options = null)` returns a `PowerFlowResult`
(`src/GridSim.Core/Abstractions/IPowerFlowSolver.cs`). Three engines ship: `SparseNewtonRaphsonPowerFlow`,
`NewtonRaphsonPowerFlow` and `BackwardForwardSweep` (`src/GridSim.Core/PowerFlow/`). `SolverSelector` is the one rule that
picks between them. `SolverSelector.Resolve` sends a strictly radial network (`LooksRadial`: one slack, in-service
branches equal to buses minus one, every bus reachable) to the sweep, and `ResolveNewton` sends every other network to
the sparse engine, because `SolverSelector.DefaultSparseThreshold` is `0`. `SteadyStateSolver(IPowerFlowSolver)` wraps any
engine in the on-load tap-changer loop.

**What an engine must return.** The contract is in the result type
(`src/GridSim.Core/PowerFlow/PowerFlowResult.cs`) and the three shipped engines honour it:

- `Converged` is `true` only when `Status` is `PowerFlowStatus.Converged`: the mismatch met `PowerFlowOptions.Tolerance`
  (`1e-8` pu) and every solved value is finite. A NaN or infinity anywhere is `PowerFlowStatus.NonFinite`; hitting
  `MaxIterations` (30) is `PowerFlowStatus.MaxIterations`. On every path the arrays keep their length; a `NonFinite` result
  may hold the non-finite values that caused it.
- `LineFlows` has exactly one entry per `model.Branches` entry, in the same order. Consumers pair `LineFlows[i]` with
  `Branches[i]`, so a failure path returns zero-flow placeholders rather than an empty list (`BackwardForwardSweep.NonRadial`
  is the model).
- `options.Cancellation.ThrowIfCancellationRequested()` runs at the top of every iteration or sweep. The default token
  never throws, so a solve with no token is bit-for-bit the solve with `default`; a deferrable job relies on it to abort at
  its deadline.
- Bus order in every array follows `model.Buses`. Every shipped engine honours `Bus.Zip` (voltage-dependent loads) and
  skips a branch with `InService` false. A model with several slack buses, one per island, is legal.

**Skeleton.** The smallest honest engine is a decorator: it adds a check without touching a number, and it shows the
contract in code. A decorator that returns the inner result unchanged is bit-identical by construction.

```csharp
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Samples;

public sealed class CheckedPowerFlow : IPowerFlowSolver
{
    private readonly IPowerFlowSolver _inner;
    public CheckedPowerFlow(IPowerFlowSolver inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
    {
        PowerFlowOptions opt = options ?? new PowerFlowOptions();
        opt.Cancellation.ThrowIfCancellationRequested();      // the cycle deadline reaches every engine through this token
        PowerFlowResult r = _inner.Solve(model, opt);

        if (r.LineFlows.Count != model.Branches.Count)          // consumers pair LineFlows[i] with Branches[i]
            throw new InvalidOperationException($"engine returned {r.LineFlows.Count} line flows for {model.Branches.Count} branches.");
        bool finite = r.Vm.All(double.IsFinite) && r.VaDeg.All(double.IsFinite) && double.IsFinite(r.TotalLossMw);
        if (r.Converged && (!finite || r.Status != PowerFlowStatus.Converged))
            throw new InvalidOperationException("engine reported Converged on a non-finite or non-Converged state.");
        return r;
    }
}
```

For a new numerical method, copy the structure of `BackwardForwardSweep` for result assembly, and
`NewtonShell` (`src/GridSim.Core/PowerFlow/NewtonShell.cs`) for the shared step (`SolveStep`), the damped update
(`ApplyStep`), the voltage floor (`VmFloorPu`, `0.2` pu) and the non-finite guard.

**Wire it.** An engine you only construct needs no wiring. To make it selectable by name:

1. Add a member to `SolverKind` and a case to `SolverSelector.Create` and `SolverSelector.Label`; extend `Resolve` only if it
   is chosen from the network shape.
2. Add it to the web table `SolverChoice` (`src/GridSim.Web/Sessions/SolverChoice.cs`, the `?solver=` values), to the desktop
   picker (`SolverChoices` in `src/GridSim.Wpf/MainWindow.xaml.cs`) and to the `--solver` mapping of `transient`
   (`RunTransientBatch` in `Program.cs`), which fails loud on an unknown value.
3. If a CLI verb should run it on the selected backend, add it to `SolverBackend` (`Solver`, `NewtonSolver`, `Pick`), never
   to the verb.

**Tests to copy.** `SolverSelectorTests` (routing and labels), `SolverRoutingTests` (the thresholds and the rule that the CLI
holds none of its own), `CliEngineChoiceScanTests`, `DenseSparseParityTests` and `DenseSparseLossParityTests` (the CPU
engines agree to the bit), `PowerFlowTests`, `SparsePowerFlowTests`, `QLimitTests`, `DistributionSolverTests` and
`tests/GridSim.Tests/Scheduling/SolverCancellationTests.cs`. The reduction test for a wrapper is the one that matters:

```csharp
[Fact]
public void A_wrapping_engine_leaves_every_bit_unchanged()
{
    GridModel m = IeeeCases.Case118();
    PowerFlowResult plain = new SparseNewtonRaphsonPowerFlow().Solve(m);
    PowerFlowResult wrapped = new CheckedPowerFlow(new SparseNewtonRaphsonPowerFlow()).Solve(m);
    Assert.True(wrapped.Converged);
    Assert.Equal(plain.Iterations, wrapped.Iterations);
    for (int i = 0; i < plain.Vm.Length; i++)
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(plain.Vm[i]), BitConverter.DoubleToInt64Bits(wrapped.Vm[i]));
        Assert.Equal(BitConverter.DoubleToInt64Bits(plain.VaDeg[i]), BitConverter.DoubleToInt64Bits(wrapped.VaDeg[i]));
    }
}
```

**Pitfalls.**

- Never share an engine across threads. A `SteadyStateSolver` rejects concurrent `Solve` calls with
  `InvalidOperationException`; parallel code builds one engine per worker from a factory (`CycleContext.SolverFactory`).
- A new engine inherits the solve options. `ContingencyAnalysis.StudyOptions` (100 iterations, reactive limits on, the
  default tolerance `1e-8` pu) is what the contingency verbs pass, and they solve through
  `ContingencyAnalysis.CreateStudySolver`, which wraps the engine in the tap-changer loop. An engine that ignores
  `EnforceQLimits` or `MaxIterations` changes their verdicts.
- Per-tick loops keep their own switch: `LiveSolveSettings.SparseThresholdBuses` (1,500) in `LiveSim` and `GdaReplayLoop`,
  the GPU routing keeps `SolverSelector.DenseSparseCrossoverBuses` (200), and the state estimator keeps
  `EstimationOptions.SparseBusThreshold` (200). Changing `DefaultSparseThreshold` moves none of them.

## A linear solver, a GPU backend or a batched solver

**Seams** (`src/GridSim.Core/Abstractions/`):

| Contract | Signature | Failure rule |
|---|---|---|
| `ILinearSolver` | `void SolveInPlace(double[,] a, double[] b)`: dense `Ax = b`; the solution overwrites `b` and `a` may be overwritten. | Throw `InvalidOperationException` on a singular matrix; throw `ArgumentException` when `a` is not square or does not match `b`. `DenseLuSolver` declares a pivot singular when it is at or below `NumericTolerances.RelativePivotFloor` (`1e-12`) times the largest magnitude of the original matrix, and every shipped dense and batched backend takes that decision from `NumericTolerances.PivotFloor`. |
| `ISparseLinearSolver` | `double[]? Solve(CsrMatrix a, double[] b)` | Return `null` on a singular matrix, a breakdown, stagnation or a non-finite result: never throw for those, never return garbage. A shape mismatch is an `ArgumentException`. |
| `IBatchedLinearSolver` | `bool[] SolveBatch(int count, int n, double[] aFlat, double[] bFlat)`: `count` row-major `n x n` systems and right-hand sides in flat buffers; the solutions overwrite `bFlat`. | A singular system fills its `bFlat` segment with `NaN` and sets its mask entry to `false`; it never fails the batch. Only malformed arguments throw. |
| `IComplexLinearSolver` | `Complex[]? Solve(ComplexCsr a, Complex[] b)` and `Factor` for repeated right-hand sides | Return `null` on a singular or broken-down matrix. All three shipped solvers do, pinned by `ComplexSolverSingularContractTests`. |

**How the engines use a backend.** `SparseNewtonRaphsonPowerFlow(ISparseLinearSolver?)` hands every Newton step to
`NewtonShell.SolveStep`. A `null` from a backend that is not the CPU direct LU is retried on a `CpuSparseLuSolver`, and only a
`null` from the CPU LU goes to ILU(0)-preconditioned BiCGSTAB with the iteration cap `4m + 200`
(`NumericTolerances.DefaultKrylovMaxIterations`) and tolerance `min(1e-10, PowerFlowOptions.Tolerance * 1e-3)`. A step that
is not finite is never applied: the engine stops and reports non-convergence. `NewtonRaphsonPowerFlow(ILinearSolver?)` is
different: given `null` or exactly a `DenseLuSolver` instance it solves every step with the same direct sparse LU as the
sparse engine, which is why the two are bit-identical; given any other `ILinearSolver` it uses it as given.

**The canonical path is a type test.** A wrapper around `DenseLuSolver` is not a `DenseLuSolver`, so it takes the as-given
path. The skeletons below were run on case9 and case118:

```csharp
using GridSim.Core.Abstractions;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Samples;

/// <summary>A sparse backend that refuses to return a step it cannot vouch for: null, never garbage.</summary>
public sealed class ResidualCheckedSparseSolver : ISparseLinearSolver
{
    private readonly ISparseLinearSolver _inner;
    public ResidualCheckedSparseSolver(ISparseLinearSolver inner) => _inner = inner;
    public string Name => $"residual-checked ({_inner.Name})";

    public double[]? Solve(CsrMatrix a, double[] b)
    {
        double[]? x = _inner.Solve(a, b);
        if (x is null || x.Length != a.N) return null;          // the engine retries on the exact CPU LU
        if (!x.All(double.IsFinite)) return null;               // non-finite: give up, never return it
        var ax = new double[a.N];
        a.Multiply(x, ax);                                      // y = A x
        double worst = 0, scale = 1e-300;
        for (int i = 0; i < a.N; i++) { worst = Math.Max(worst, Math.Abs(ax[i] - b[i])); scale = Math.Max(scale, Math.Abs(b[i])); }
        return worst <= 1e-6 * scale ? x : null;
    }
}

/// <summary>A dense backend: the solution goes into b, and a matrix it cannot solve throws.</summary>
public sealed class FiniteCheckedDenseSolver : ILinearSolver
{
    private readonly ILinearSolver _inner;
    public FiniteCheckedDenseSolver(ILinearSolver inner) => _inner = inner;
    public string Name => $"finite-checked ({_inner.Name})";

    public void SolveInPlace(double[,] a, double[] b)
    {
        if (a.GetLength(0) != b.Length || a.GetLength(1) != b.Length)
            throw new ArgumentException("A must be square and match b's length.");
        _inner.SolveInPlace(a, b);
        if (!b.All(double.IsFinite)) throw new InvalidOperationException("dense solve produced a non-finite solution.");
    }
}
```

Measured on the current build: the sparse wrapper around `CpuSparseLuSolver` gives case118 bits identical to the plain
sparse engine, because it returns the CPU solution unchanged. `NewtonRaphsonPowerFlow(new FiniteCheckedDenseSolver(new
DenseLuSolver()))` converges in the same iterations (4 on case9, 6 on case118) but is not the canonical path: its case9
voltages differ from the canonical ones by at most `2.220E-016` pu in magnitude and `9.770E-015` degrees in angle (loss
difference `1.421E-013` MW), and on case118 by at most `5.551E-016` pu. That difference is why a dense backend other than
`DenseLuSolver` is outside the bit-identity contract, as every GPU backend is.

A batched backend follows the same shape. This one solves the systems one after another on `DenseLuSolver`; copy
`CpuBatchedLuSolver` (`src/GridSim.Core/PowerFlow/CpuBatchedLuSolver.cs`) for the parallel reference:

```csharp
using GridSim.Core.Abstractions;
using GridSim.Core.PowerFlow;

namespace GridSim.Samples;

public sealed class SequentialBatchedSolver : IBatchedLinearSolver
{
    public string Name => "batched-dense (sequential)";

    public bool[] SolveBatch(int count, int n, double[] aFlat, double[] bFlat)
    {
        if (count < 0 || n < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (aFlat.Length < (long)count * n * n) throw new ArgumentException("aFlat is too short for count*n*n.", nameof(aFlat));
        if (bFlat.Length < (long)count * n) throw new ArgumentException("bFlat is too short for count*n.", nameof(bFlat));

        var ok = new bool[count];
        var dense = new DenseLuSolver();
        for (int s = 0; s < count; s++)
        {
            var a = new double[n, n];
            var b = new double[n];
            Buffer.BlockCopy(aFlat, s * n * n * sizeof(double), a, 0, n * n * sizeof(double));
            Array.Copy(bFlat, s * n, b, 0, n);
            try
            {
                dense.SolveInPlace(a, b);
                Array.Copy(b, 0, bFlat, s * n, n);
                ok[s] = true;
            }
            catch (InvalidOperationException)
            {
                for (int i = 0; i < n; i++) bFlat[s * n + i] = double.NaN;   // a singular system never fails the batch
            }
        }
        return ok;
    }
}
```

**A GPU backend.** For an accelerator ILGPU supports, write no new solver: `GpuDenseLinearSolver(AcceleratorType)`,
`GpuSparseLinearSolver(accel:)` and `GpuBatchedDenseSolver(AcceleratorType)` take the accelerator type, which is how OpenCL
reuses the CUDA kernels. For a native vendor stack, copy `RocmDenseLinearSolver`, `RocmSparseLinearSolver` and `RocmContext`
(`src/GridSim.Gpu/`):

- The availability probe never throws. `RocmContext.IsAvailable` makes one cheap runtime call inside a broad `try`, and a
  missing library means "unavailable", not an exception.
- Every solver defers to its CPU counterpart when the device is absent, so it is safe to construct unconditionally
  (`RocmDenseLinearSolver` falls back to `DenseLuSolver`, `RocmSparseLinearSolver` to `CpuSparseLuSolver`).
- One device serves many `Parallel.ForEach` workers, so every device touch (allocation, copy, library call, synchronise)
  runs under one lock (`RocmContext.Lock`, `GpuContext.Lock`).
- An OpenCL device is accepted only if an on-device kernel computes `(1 + 2^-40) - 1` in double precision from operands sent by the host and returns exactly `2^-40` (single precision returns 0); a device without
  FP64 is rejected rather than selected and faulted.
- A sparse backend that gives up returns `null`, so the exact CPU LU runs.

**Wire it** in four places. In `SolverBackend` (`src/GridSim.Cli/SolverBackend.cs`): add the vendor to the `Vendor` enum, to
`IsKnownSpec` and to `Select` (the auto order is AMD-first: ROCm, then OpenCL, then CUDA, then CPU, with a warning on stderr
and a CPU fallback when the device is absent), and vend your solvers from `DenseLinear()`, `SparseLinear()` and, if batched,
`DenseBatch()`. In `Program.cs`: the `FailFlag` text that lists the accepted `--gpu` vendors (the value is validated before
`Select` runs, so a typo exits 2), one `Probe` line in `RunGpuCheck` (the `gpucheck` preflight) and one engine entry in the
GPU rows of `RunBench`.

**Prove it** with the preflight and the batched check. `gridsim gpucheck` builds and solves case9 on every backend that
answers, printing one line per call, so a crash names the failing call. This capture is from a machine with no GPU: the
CPU rows run and the three vendors report why they were skipped. Timings differ by machine and run:

```
$ gridsim gpucheck
GridSim GPU preflight
=====================
CPU cores: 8   OS: Microsoft Windows 10.0.26300
commit: v1.95.294-168-g786a2162

[CPU] cpu   (vendor=Cpu)
  CPU dense: build solver... solve case9... PASS  (4 it, 76.4 ms, |mis|=1.6E-014)
  CPU sparse: build solver... solve case9... PASS  (4 it, 3.5 ms, |mis|=1.6E-014)

[gpu] no usable rocm device found - falling back to the CPU backend.
...
[exit 0]
```

The CPU rows are the reference: case9 converges in 4 iterations with 4.641 MW of losses, and the line a new backend prints is
compared with them. `gridsim batchsolve` solves a batch on the selected backend, checks it against `CpuBatchedLuSolver` and
exits 1 if the two differ by more than the agreement tolerance or disagree on which systems are singular
([`batchsolve`](<0019 CLI reference.md#batchsolve>)); its timings differ by machine and run too:

```
$ gridsim batchsolve
GridSim  -  batched dense solve: 64 systems x 64x64   backend batched-dense-lu (cpu)
==============================================================================
correctness: 64/64 solved   max|Delta| vs CPU reference = 0.000E+000
timing (median of 5): backend 1.1 ms   cpu-reference 0.9 ms   backend 57940 solves/s
[exit 0]
```

The verb prints the largest absolute difference from the CPU reference as `max|Delta|`.

`gridsim bench --gpu` ([`bench`](<0019 CLI reference.md#bench>)) adds a column per GPU vendor that answers. A cell reads `NO` where
the engine did not converge and `skip` above 3,000 buses, so a backend that fails where the CPU engines converge is visible
at once. GPU timings include kernel compilation on the first call, and a first-call figure is not a solve time.

**Tests to copy.** `GpuSolverSeamTests` (device against CPU to a decimal tolerance, near-singular pivot throws, shape
mismatch rejected), `RocmSolverSeamTests`, `GpuFallbackTests` (a backend that always gives up falls back to the exact CPU LU
and the answer is the CPU path's, to the bit), `tests/GridSim.Tests/Scheduling/BatchedSolverTests.cs` (CPU batched equals
per-system LU, singular isolation, GPU against CPU), `BatchedInPlaceLuBitIdentityTests`, `SparseLuTests` and, for the complex
seam, `ComplexSolverCrossValidationTests`, `ComplexSolverReusePinTests` and `ComplexSolveIntoBitIdentityTests`. This is the
shape of the singular-isolation test:

```csharp
[Fact]
public void A_singular_system_is_isolated_in_the_batch()
{
    // Three 2 x 2 systems: 2I x = (2,4); a singular [1 1; 1 1]; 4I x = (8,4).
    double[] a = { 2, 0, 0, 2,   1, 1, 1, 1,   4, 0, 0, 4 };
    double[] b = { 2, 4,   1, 2,   8, 4 };
    bool[] ok = new SequentialBatchedSolver().SolveBatch(3, 2, a, b);
    Assert.Equal(new[] { true, false, true }, ok);
    Assert.Equal(new[] { 1.0, 2.0 }, b[0..2]);
    Assert.True(double.IsNaN(b[2]) && double.IsNaN(b[3]));
    Assert.Equal(new[] { 2.0, 1.0 }, b[4..6]);
}
```

**Pitfalls.**

- A sparse solver that throws on a singular matrix breaks the fallback chain: the engine expects `null`.
- A GPU solver that returns a plausible vector for a non-converged iteration is worse than one that returns `null`. Check
  for non-finite values before returning, as `ResidualCheckedSparseSolver` does.
- `IComplexLinearSolver.Factor` has a default that re-solves per right-hand side (correct, no reuse). A direct solver
  overrides it to factor once; `IComplexLinearFactorization.SolveInto` must return the same bits as `Solve`.
  `HarmonicImpedanceScan` and `CoupledHarmonicPowerFlow` take an optional solver and default to `RealExpandedComplexSolver`;
  `ImpedanceStabilityScan` takes one too.
- The GPU engines skip cases above 3,000 buses in `bench`, and `batchsolve` exits 2 when `count x n x n` exceeds the
  single-dispatch limit (`int.MaxValue`).

## An inertia model

**Seam.** `IInertiaModel` (`src/GridSim.Core/Abstractions/IInertiaModel.cs`) turns a `GridModel`, a demand baseline and a list
of `FrequencyEvent` power steps into a `FrequencyResponse`. It also exposes `CoiInertiaGvaS(GridModel)`, the aggregate every
scalar readout reads, and `LastDistributedResult`, the spatial detail of the last `Simulate` (null for the lumped model).
`LumpedScalarInertiaModel` is the default and `DistributedInertiaModel` is the multi-domain model; a distributed model
returns the centre-of-inertia collapsed response from `Simulate`, so every scalar consumer keeps working.
`InertiaModelSelector.Create(InertiaModel)` builds either, and there is no shape-based auto-selection: the choice is
explicit (`--inertia-model lumped|distributed`).

**Skeleton.** A decorator again, because it is the shape that cannot change a number:

```csharp
using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;

namespace GridSim.Samples;

public sealed class FiniteCheckedInertiaModel : IInertiaModel
{
    private readonly IInertiaModel _inner;
    public FiniteCheckedInertiaModel(IInertiaModel inner) => _inner = inner;

    public FrequencyResponse Simulate(GridModel model, double baseDemandMw, IEnumerable<FrequencyEvent> events, DynamicsConfig? config = null)
    {
        FrequencyResponse r = _inner.Simulate(model, baseDemandMw, events, config);
        if (!double.IsFinite(r.NadirHz) || !double.IsFinite(r.InitialRocofHzPerS))
            throw new InvalidOperationException("frequency model returned a non-finite nadir or RoCoF.");
        return r;
    }

    public double CoiInertiaGvaS(GridModel model) => _inner.CoiInertiaGvaS(model);
    public DistributedResult? LastDistributedResult => _inner.LastDistributedResult;
}
```

**Wire it.** Add an `InertiaModel` member and a case to `InertiaModelSelector.Create` and `Label`, then the value to the
`--inertia-model` parse in `Program.cs`, which fails loud on an unknown word. The stateful live loop uses
`IFrequencyIntegrator` (`src/GridSim.Core/Dynamics/IFrequencyIntegrator.cs`), implemented by `FrequencyIntegrator` and
`AssetAwareFrequencyIntegrator`; a model that must run live implements that too. **Tests to copy.** `InertiaModelSeamTests`.
**Pitfall.** Real stored energy `E = (1/2) J omega^2` and emulated inertia are separate terms. A synthetic-inertia term is
a power injection and is never added into `E` ([Technical 5](<../technical/0005 Frequency dynamics.md>)).

## An estimation method

**Seam.** `IWlsStep` (`src/GridSim.Core/Estimation/WlsStep.cs`) is one backend of the Gauss-Newton step behind the state
estimator. `WlsStateEstimator` (`WlsStateEstimator.cs`) owns everything around it: the start point, the trust-capped
update, convergence, chi-square, bad-data removal and the IRLS reweighting outer loop. A step owns five methods:

| Method | Responsibility |
|---|---|
| `Begin(GridModel, MeasurementModel, double[] w)` | Bind a model, a measurement set and weights; do structural work (sparsity patterns, orderings, buffers) once per bad-data pass. |
| `RebindWeights(double[] w)` | Re-bind new weights for the same model and set (the robust outer loop). A backend whose prepared state does not read the weights only swaps the reference; one whose preparation reads them re-runs its `Begin`. Throws when called before `Begin` or with a wrong-length array. |
| `GateObservability(vm, va)` | The observability verdict at the start point. The dense gate proves gain-matrix rank. The sparse gate runs the numerical rank proof, which names the free state directions, at or below `DenseEstimationLimits.RankProofMaxState` (2,500 states) and only a topology sweep above it, reporting rank `-1`. |
| `TrySolve(vm, va, dxOut)` | One undamped Gauss-Newton correction. `false` means the gain is singular, which the estimator reports as `Unobservable`. |
| `NormalizedResiduals(vm, va, residualPu)` | Normalised residuals and critical-measurement flags for bad-data analysis. |

`IWlsStep` and `MeasurementModel` are `internal`. `GridSim.Core.csproj` grants `InternalsVisibleTo` only to `GridSim.Tests` and
`GridSim.SchemaEditor`, so a new step or method is a change inside `GridSim.Core`.

**The method enum.** `EstimationMethod` has `Wls`, `Huber`, `Lav`, `Constrained`, `Feasible`, `SchweppeGm` and `Qr`.
`WlsStateEstimator.Estimate` selects the step: `Constrained` and `Feasible` build `ConstrainedDenseWlsStep` or
`FeasibleWlsStep`; `Qr` builds `QrWlsStep`; every other method builds `SparseWlsStep` above `EstimationOptions.SparseBusThreshold`
(200 buses) and `DenseWlsStep` at or below it. `Huber`, `Lav` and `SchweppeGm` are not separate steps: they are reweighting loops
around the same step.

**Skeleton: a new step.** This one damps the gain diagonal, `G' = G + lambda * diag(G)`. It reuses `DenseWlsStep` for the
observability gate, the weight re-binding and the residual statistics, so only `TrySolve` is new:

```csharp
using GridSim.Core.Abstractions;
using GridSim.Core.Estimation;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Samples;

internal sealed class DampedDenseWlsStep : IWlsStep
{
    private readonly DenseWlsStep _plain;       // supplies GateObservability and NormalizedResiduals unchanged
    private readonly ILinearSolver _linear;
    private readonly double _lambda;

    private MeasurementModel _mm = null!;
    private double[] _w = null!;
    private double[] _h = null!;
    private double[,] _jac = null!;
    private double[,] _gain = null!;

    public DampedDenseWlsStep(ILinearSolver linear, double lambda)
    {
        if (!(lambda >= 0) || !double.IsFinite(lambda)) throw new ArgumentOutOfRangeException(nameof(lambda));
        _linear = linear;
        _lambda = lambda;
        _plain = new DenseWlsStep(linear);
    }

    public void Begin(GridModel model, MeasurementModel mm, double[] w)
    {
        _plain.Begin(model, mm, w);
        _mm = mm;
        _w = w;
        _h = new double[mm.MeasurementCount];
        _jac = new double[mm.MeasurementCount, mm.StateSize];
        _gain = new double[mm.StateSize, mm.StateSize];
    }

    // Only the weights change on a re-bind; every buffer is cleared and refilled before use.
    public void RebindWeights(double[] w)
    {
        if (_mm is null) throw new InvalidOperationException("RebindWeights called before Begin.");
        if (w.Length != _mm.MeasurementCount) throw new ArgumentException("wrong number of weights.", nameof(w));
        _plain.RebindWeights(w);
        _w = w;
    }

    public ObservabilityReport GateObservability(double[] vm, double[] va) => _plain.GateObservability(vm, va);

    public bool TrySolve(double[] vm, double[] va, double[] dxOut)
    {
        _mm.EvaluateH(vm, va, _h);
        _mm.EvaluateJacobian(vm, va, _jac);
        DenseWlsStep.FormNormalEquations(_mm.ZPu, _h, _w, _jac, _gain, dxOut);   // dxOut = H^T W (z - h)
        if (_lambda != 0)
            for (int c = 0; c < dxOut.Length; c++) _gain[c, c] += _lambda * _gain[c, c];
        try { _linear.SolveInPlace(_gain, dxOut); return true; }
        catch (InvalidOperationException) { return false; }   // singular: the estimator reports Unobservable
    }

    public (double[] Normalized, bool[] Critical) NormalizedResiduals(double[] vm, double[] va, double[] residualPu)
        => _plain.NormalizedResiduals(vm, va, residualPu);
}
```

**Wire it.** Add the member to `EstimationMethod`, then three edits:

1. In `WlsStateEstimator.Estimate`, add a branch to the step selection (the `if` chain that builds `FeasibleWlsStep`,
   `QrWlsStep` and the dense and sparse steps). A dense-only method refuses a case above
   `DenseEstimationLimits.DenseMethodMaxBuses` (3,000) with `NotSupportedException` rather than run unconstrained or
   exhaust memory; the CLI turns that into exit 2.
2. In `Program.cs`, add the name to the `--method` switch of `estimate` and to the method-label switch further down. An unknown
   value ends in `FailFlag`, which exits 2. `EstimateMethodSelectionTests` fails if any enum member lacks a `=> EstimationMethod.X,`
   line.
3. In the `estimate` section of [`0019 CLI reference.md`](<0019 CLI reference.md#estimate>), list the method in the `--method` row,
   because that section is the text `gridsim estimate --help` prints.

**A robust (reweighting) method.** Decide first whether your method is a new step or a new weighting policy. A robust
method is a weighting policy: at each converged inner solve the loop re-derives every row's weight from its standardised
residual `r_i = |z_i - h_i(x)| / sigma_i`, multiplies the declared weight by a factor, rebinds the weights and repeats until
no factor moves by more than `IrlsWeightTolerance` (`1e-3`) relative to the row's declared weight, with at most
`IrlsMaxOuterIterationsMEstimator` (50) outer passes for Huber and Schweppe-GM and `IrlsMaxOuterIterations` (10) for
LAV (N331, 1.95.249: the M-estimators' weights converge linearly and needed about 12 to 14 passes on clean case14 sets):

```
Huber        factor = 1                  if r_i <= k
                      k / r_i            otherwise              k = EstimationOptions.HuberK = 1.345
LAV          factor = 1 / max(r_i, 1e-3)                        floor = LavResidualFloor
SchweppeGm   the Huber factor on the leverage-adjusted residual r_i / sqrt(1 - K_ii); a critical row keeps factor 1
```

The method name appears in six conditions that must all learn the new member, because a missed one silently turns the
method into plain WLS or stops its loop after one pass: the `wEff` clone and `maxOuter` choice at the top of the pass, the
`is not (Huber or Lav or SchweppeGm)` exit after the inner loop, the `factor` switch, the chi-square branch that reports
against the original sigmas and skips bad-data removal, and the same test in `HtiBadDataEstimation`
(`src/GridSim.Core/Estimation/HtiBadDataEstimation.cs`). Search for `EstimationMethod.Lav` in `src/` to find every site. A
reweighted pass binds the weights it is given: if your factor needs the leverage `K_ii` or the residual covariance, those are
defined with the declared weights, so rebind to the declared weights for that evaluation and to the reweighted ones for the
next solve (`SchweppeGm` shows the pattern).

**Prove it.** A new method owes the project three demonstrations:

- It reduces to the plain method where it should, to the bit. The `lambda = 0` step above must equal `DenseWlsStep`:

  ```csharp
  [Fact]
  public void A_new_step_with_no_new_term_equals_the_dense_step_to_the_bit()
  {
      GridModel model = IeeeCases.Case9();
      PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
      MeasurementSet set = SyntheticMeasurements.Sample(model, truth);
      var mm = new MeasurementModel(model, set);
      double[] w = Enumerable.Range(0, mm.MeasurementCount).Select(r => 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r])).ToArray();
      double[] vm = Enumerable.Repeat(1.0, model.BusCount).ToArray();
      double[] va = new double[model.BusCount];

      var reference = new DenseWlsStep(new DenseLuSolver());
      var mine = new DampedDenseWlsStep(new DenseLuSolver(), lambda: 0.0);
      reference.Begin(model, mm, w);
      mine.Begin(model, mm, w);
      var dxRef = new double[mm.StateSize];
      var dxMine = new double[mm.StateSize];
      Assert.True(reference.TrySolve(vm, va, dxRef));
      Assert.True(mine.TrySolve(vm, va, dxMine));
      for (int i = 0; i < dxRef.Length; i++)
          Assert.Equal(BitConverter.DoubleToInt64Bits(dxRef[i]), BitConverter.DoubleToInt64Bits(dxMine[i]));
  }
  ```

- It recovers the truth on an exact set and agrees with WLS on clean data (`QrEstimatorTests`, `SchweppeGmTests`).
- Dense and sparse variants agree on a noisy set with the same verdicts (`SparseEstimatorTests`: `Vm` within `1e-8` pu and
  `Va` within `1e-7` degrees on case118, the same chi-square verdict and the same rank), and `gridsim bench --estimate`
  times both. The validation ladder in
  [ch. 27, Validation tools](<0027 Validation tools.md>) (`crossval_estimation.py`, `se_residual_check.py`) shows that the
  estimate is a real optimum, whatever the method.

Running an existing method, for orientation, ends in the standard report ([`estimate`](<0019 CLI reference.md#estimate>)):

```
$ gridsim estimate case14 --synth --method qr

=== WLS (QR) state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg

...
[exit 0]
```

**Pitfalls.**

- Keep `TrySolve` free of allocation where you can: the estimator calls it every iteration of every outer pass.
- A method that fuses a dynamics process model into the estimator (a recursive or forecasting filter) is not a static
  filter. It must call `RestrictionReview.AssertPermitted(method, usesDynamicsModel: true)` and appear in
  `RestrictionReview.Reviewed` with a certification that its output is present or past only, and its emitted stamp is
  checked with `RestrictionReview.AssertOutputHistorical`. `Qr` and `SchweppeGm` are static filters and need no entry
  (`QrEstimatorTests.Qr_is_a_static_filter_not_dynamics_fused` pins it).
- The estimator reports an unobservable set as `Unobservable`, never as a state with NaN. A new gate keeps that.
- `--audit-log` records `estimate` runs; a new method needs no extra audit code, because the outcome line comes from
  `EstimationAuditTrail.Outcome(method.ToString(), ...)`.

## A measurement kind

**Seam.** `MeasurementKind` (`src/GridSim.Core/Estimation/Measurement.cs`) names what a telemetered value observes. The
physics is in `MeasurementModel` (`src/GridSim.Core/Estimation/MeasurementModel.cs`). The eight kinds are `VmPu`, `PInjMw`,
`QInjMvar`, `PFlowMw`, `QFlowMvar`, `VaDeg`, `IMagPu` and `IAngDeg`. Bus kinds address a bus by id; branch kinds address a
branch by its `From` and `To` bus ids, plus `Circuit` (the branch name) for parallel circuits, and `End`.

A kind is a closed change. These sites each switch over the kind. A missing arm in `StructuralColumns`, `EvaluateH` or
`EvaluateJacobian` throws `InvalidOperationException("Unhandled measurement kind ...")` when the set is used. The two unit
switches (the constructor and `Assemble`) have a default arm that treats the row as power and divides by `BaseMva`, so a
kind that is per-unit or an angle and is missing from them is silently mis-scaled:

| Site | What it needs |
|---|---|
| `Measurement.IsBranch` | The kind, if it addresses a branch. |
| `MeasurementModel` constructor | The unit conversion to internal units: voltage and current magnitude stay per-unit, power divides by `BaseMva`, angles convert degrees to radians. |
| `MeasurementModel.StructuralColumns` | Which state columns the row touches. The sparse backend builds its gain pattern from this once per pass. |
| `MeasurementModel.EvaluateH` | `h(x)`. For an angle kind (`VaDeg`, `IAngDeg`) `h` is then folded toward `z`, so that `z - h` is wrapped to (-pi, pi]. |
| `MeasurementModel.EvaluateJacobian` | The analytic row, pushed into the shared sink so dense and sparse cannot drift. |
| `WlsStateEstimator.Assemble` | The factor that turns an internal residual back into the kind's engineering unit. |
| `HtiBadDataEstimation` | The unit and label of the kind in the hypothesis-testing report. |
| The mappers | `RtuMeasurementSetMapper`, `PmuMeasurementSetMapper` and the live store `QuarantineLiveStore`, if a feed carries the new quantity. |

**Then mirror it where the contract reaches.** The export writer carries `kind` by name, but the Python sidecars evaluate
`h` independently. Add the kind to `tools/crossval/se_residual_check.py` (the `SUPPORTED_KINDS` tuple, the `h` evaluation and
the Jacobian row) and, if pandapower can measure it, to `tools/crossval/crossval_estimation.py`. `crossval_estimation.py`
refuses `IMagPu` and `IAngDeg` with exit 3 because pandapower's current measurements are in kA on its own base, and it
refuses any kind it does not know, naming it; an export that contains such a kind cannot be cross-validated with
pandapower (see [ch. 27, Validation tools](<0027 Validation tools.md>)). Add an item of the new kind to
the export test: `EstimateExportContractTests` pins the field names of the `gridsim-estimate-export/1` document on a
case9 export, but its measurement-item check reads only `VmPu` and `PFlowMw` items and it does not enumerate
`MeasurementKind`, so a missing kind does not fail it. `MeasurementSet.Validate` already checks structure for free: bus and branch resolution, sigma
sanity and the base-MVA match.

**Test to copy.** A new kind's Jacobian must equal the central finite difference of its `h`, evaluated off the solution so no
partial degenerates. `CurrentMagnitudeMeasurementTests` is the model; this is the reusable core, run on case9 with a set of
`IMagPu` rows (maximum error `1.146E-008`, against the `1e-6` bound):

```csharp
public static double MaxJacobianError(GridModel model, PowerFlowResult truth, MeasurementSet set)
{
    var mm = new MeasurementModel(model, set);
    int n = model.BusCount;
    var vm = new double[n];
    var va = new double[n];
    for (int i = 0; i < n; i++)
    {
        vm[i] = truth.Vm[i] * (1.0 + 0.01 * Math.Sin(i + 1));                           // off-solution, so no partial degenerates
        va[i] = truth.VaDeg[i] * Math.PI / 180.0 + 0.005 * Math.Cos(2 * i + 1);
    }

    int rows = mm.MeasurementCount, s = mm.StateSize;
    var analytic = new double[rows, s];
    mm.EvaluateJacobian(vm, va, analytic);

    const double eps = 1e-7;
    var hPlus = new double[rows];
    var hMinus = new double[rows];
    double worst = 0;
    for (int c = 0; c < s; c++)
    {
        int bus = mm.StateBusIndex(c, out bool isAngle);
        double[] target = isAngle ? va : vm;
        double keep = target[bus];
        target[bus] = keep + eps; mm.EvaluateH(vm, va, hPlus);
        target[bus] = keep - eps; mm.EvaluateH(vm, va, hMinus);
        target[bus] = keep;
        for (int r = 0; r < rows; r++)
            worst = Math.Max(worst, Math.Abs(analytic[r, c] - (hPlus[r] - hMinus[r]) / (2 * eps)));
    }
    return worst;
}
```

Pair it with an exact-set recovery test (an exact set including the new kind recovers the truth to `1e-9` pu in `Vm` and
`1e-8` degrees in `Va`) as `CurrentMagnitudeMeasurementTests` does.

**Pitfalls.** A kind whose `h` is not smooth at the operating point (an absolute value, an angle across a branch cut)
needs the fold that `VaDeg` and `IAngDeg` have, or a wrapped PMU angle reads as a `2 pi` residual. A kind that is linear in
the state (as `VaDeg` is) has a one-entry Jacobian row.

## An asset-dynamics class

**Seams** (`src/GridSim.Core/Assets/IAssetDynamics.cs`). An asset is modelled by its dynamic behaviour, not by its fuel
string. Every controller derives from `IAssetDynamics` (`ClassName`, `Kind`, `Bus`, `Tripped`, `Reset()`):

- `IActiveAssetDynamics.Update(in AssetStepContext)` returns an `AssetActuation`: a swing-power term in MW (positive
  reduces the deficit) and the trip signals (`TrippedNow`, `TrippedInfeedMw`, `TrippedInertiaMwS`). It runs on the fast
  sub-step clock. It also reports `CurrentMw` and `SocFraction` (NaN when no storage is modelled) for the dashboards.
- `IPassiveAssetDynamics.Advance(in AssetStepContext)` returns a `PlantStateUpdate` (an optional tap ratio, a rating derate
  from 0 to 1, a thermal state) on the steady-state tick. It is never a swing-power term.

The `AssetStepContext` carries the frequency, RoCoF and phase the device sees at its own bus, the step `DtSeconds`, the
`ClockSeconds`, and, for network-aware devices, `LocalVoltagePu` (default `1.0`) and `LoadingFraction` (default `0.0`).
Every path that runs an asset fleet is lumped: `AssetDynamicsFleet.StepCoi` hands every asset the centre-of-inertia
sample, so a controller written against the context runs unchanged wherever the fleet is stepped.

**Author a class, not code.** The everyday extension is data. Add an entry to `data/<case>/asset-classes.json` (schema
`gridsim-asset-classes/1`, `schemas/v1/asset-classes.schema.json`):

```json
{
  "name": "gfl-wind-2024",
  "kind": "gridFollowingInverter",
  "mode": "grid-following",
  "params": { "deadbandHz": 0.015, "droopGainMwPerHzPerMw": 2.0, "rocofTripHzPerS": 1.0, "rocofTripConfirmS": 0.5 },
  "provenance": {
    "_default":        { "tag": "CuratedPlaceholder", "note": "Grid-following wind FFR loop; approximated from literature [VERIFY]" },
    "rocofTripHzPerS": { "tag": "Recorded",           "note": "Modern GB LoM setting" }
  }
}
```

The kinds are `gridFollowingInverter` (aliases `grid-following`, `gfl`), `gridFormingInverter` (`grid-forming`, `gfm`),
`transformer`, `line` (`cable`) and `protection` (`relay`, `protective`), compared ignoring case. `synchronousMachine` (alias `machine`) parses
but is refused at load (see below). `params` is a flat name-to-number bag whose accepted keys are the `AssetParams`
constants for that kind (`src/GridSim.Core/Assets/AssetClassLibrary.cs`); `provenance` gives each parameter a tag, with
`_default` covering the rest. A parameter with neither an entry nor a `_default` reads as `CuratedPlaceholder` with a
`[VERIFY]` note.

**Bind it.** Put `dynamicsClass` inline on a generator in `generation.json` (or a branch in `transmission.json` or
`transformer.json`), or write a rule in `asset-bindings.json` (`{ "matchType": "onshore", "class": "gfl-wind-2024" }`).
`AssetBindingSet.Resolve` takes the inline class first, then the first exact rule, then the first prefix rule (a
`matchType` ending in `*`, so `"wind*"` matches `wind-onshore`); matching ignores case; an unmatched asset stays passive.
The shipped GB libraries tag each unmeasurable control parameter `CuratedPlaceholder` and each grounded one `Recorded` or
`DerivedFromRecorded`; the grounding status of each is in
[Technical 13](<../technical/0013 Asset-dynamics data grounding.md>). The `gb-full` library is generated by the GDA
repository's `NetworkModel/build_asset_classes.py`, which is outside this repository.

**What the loader refuses.** Every one of these is a fail-loud `InvalidDataException`; each was reproduced by loading a
copy of `data/gb-synthetic` with one edit (the messages are quoted in part):

| Edit | Refusal |
|---|---|
| A class of kind `synchronousMachine` | `asset class 'sm' has kind SynchronousMachine, which GridSim cannot simulate as an authored class: there is no controller for it, so every asset bound to it would silently stay passive.` A synchronous machine's inertia comes from its generator's `H` and `MBase`. |
| `dispatchRampMwPerMin` or `minOutputFraction` | `... sets 'dispatchRampMwPerMin': no controller reads it ...` (the inverter controllers model the frequency response about a fixed dispatch). |
| A key nothing reads (a misspelling) | `... (kind GridFollowingInverter) sets 'droopGain', which nothing that simulates a GridFollowingInverter reads, so it would be silently ignored. Check the spelling; accepted keys: ...` |
| A `CuratedPlaceholder` note without `[VERIFY]` | `asset class '...' parameter '_default' is CuratedPlaceholder but its note lacks the literal [VERIFY] token.` |
| A duplicate class name | `Duplicate asset class name '...' in asset-classes.json.` |
| An unknown provenance tag or kind | `... has unknown provenance tag 'Guess'.` and `... has unknown kind 'windmill'.` |
| An unknown JSON key (`"colour"`) | `The JSON property 'colour' could not be mapped to any .NET member contained in type ...AssetClassDto.` Every v1 file refuses unknown members. |
| A binding rule naming a missing class | `asset-bindings.json rule for 'onshore' references unknown class 'nope'.` |
| `asset-bindings.json` with no `asset-classes.json` | `asset-bindings.json is present but asset-classes.json defines no classes.` |

All problems of one library are collected into one exception. The check is `AssetClassSupport.Problems`, and
`AssetClassSupportTests` pins the table of accepted keys equal to the keys the JSON v2 kind spec for that kind declares
(`schemas/v2/kinds/*.kind.json`), less the refused keys, so the table and the specs cannot drift.

**Skeleton: a new behaviour.** Write code only for a genuinely new control law. Copy `GridFormingInverter` (90 lines) for the
smallest controller, and `GridFollowingInverter` (the sample, compute, actuate, protect state machine with a measurement
delay). This one is a dead-banded droop with a headroom cap, a first-order lag and a RoCoF trip:

```csharp
using GridSim.Core.Assets;

namespace GridSim.Samples;

public sealed class DroopController : IActiveAssetDynamics
{
    private readonly double _f0, _pgMw, _capUpMw, _capDownMw;
    private readonly double _deadbandHz, _droopMwPerHz, _tauS, _rocofTripHzPerS, _rocofConfirmS;
    private double _p, _dwellS;
    private bool _tripped;

    public DroopController(AssetClass cls, int bus, double pgMw, double ratedMw, double f0 = 50.0)
    {
        ClassName = cls.Name;
        Bus = bus;
        _f0 = f0;
        _pgMw = pgMw;
        _deadbandHz = cls.Param(AssetParams.DeadbandHz, 0.015);
        _droopMwPerHz = cls.Param(AssetParams.DroopGainMwPerHzPerMw, 2.0) * ratedMw;   // per-MW gain times rating
        _tauS = cls.Param(AssetParams.ActuateTauS, 0.3);
        _capUpMw = cls.Param(AssetParams.HeadroomUpFraction, 0.1) * ratedMw;
        _capDownMw = cls.Param(AssetParams.HeadroomDownFraction, 0.1) * ratedMw;
        _rocofTripHzPerS = cls.Param(AssetParams.RocofTripHzPerS, double.PositiveInfinity);
        _rocofConfirmS = cls.Param(AssetParams.RocofTripConfirmS, 0.5);
    }

    public string ClassName { get; }
    public AssetKind Kind => AssetKind.GridFollowingInverter;
    public int Bus { get; }
    public bool Tripped => _tripped;
    public double CurrentMw => _tripped ? 0.0 : _p;
    public double SocFraction => double.NaN;                       // no storage modelled

    public void Reset() { _p = 0; _dwellS = 0; _tripped = false; }  // a fresh run must start from a clean state

    public AssetActuation Update(in AssetStepContext ctx)
    {
        if (_tripped) return default;                              // a tripped asset contributes nothing, ever

        _dwellS = Math.Abs(ctx.LocalRocofHzPerS) > _rocofTripHzPerS ? _dwellS + ctx.DtSeconds : 0.0;
        if (_dwellS >= _rocofConfirmS)
        {
            _tripped = true;                                       // the fleet folds TrippedInfeedMw into the deficit
            return new AssetActuation(0.0, TrippedNow: true, TrippedInfeedMw: _pgMw, TrippedInertiaMwS: 0.0);
        }

        double df = ctx.LocalHz - _f0;
        double active = Math.Abs(df) > _deadbandHz ? df : 0.0;
        double target = Math.Clamp(-_droopMwPerHz * active, -_capDownMw, _capUpMw);   // high frequency: negative, less power
        _p += (target - _p) / Math.Max(ctx.DtSeconds, _tauS) * ctx.DtSeconds;
        return new AssetActuation(_p, TrippedNow: false, TrippedInfeedMw: 0.0, TrippedInertiaMwS: 0.0);
    }
}
```

The sign convention is the one the shipped controllers use: a frequency above nominal asks for less power. `AssetActuation`
adds into `pImbalance` exactly as `pBase + pAssetFleet - tripDeficit`.

**Wire it.** The controller of a generator or branch asset is created only through `AssetControllerFactory`
(`src/GridSim.Core/Assets/AssetBinding.cs`); a placed protection is built by `AssetDynamicsFleet.Build`. `AssetDynamicsFleet`
has a private constructor, so a hand-built controller cannot be added to a fleet. A new kind therefore touches these places,
and the first three must agree:

1. `AssetKind` (`AssetKind.cs`): the new member.
2. `AssetControllerFactory.CreateActive` or `CreatePassive`: the `switch` arm that constructs it. A passive kind goes to
   `PassivePlantFleet`.
3. `AssetClassSupport` (`AssetClassLibrary.cs`): the key array for the kind, the `AcceptedKeys` arm, and `IsSupportedKind`;
   plus any new `AssetParams` constant.
4. `JsonGridLoader.ParseAssetKind`: the spellings the file may use.
5. The schemas: the `kind` enum of `schemas/v1/asset-classes.schema.json`, a `schemas/v2/kinds/<kind>.kind.json` spec and the
   `V2CaseLoader.V1KindOf` map ([Technical 28](<../technical/0028 The v2 JSON schema.md>)).

The swing term is already plumbed. `AssetDynamicsFleet.Step` sums every active asset's `ActivePowerMw` into
`pAssetFleet`, and the trip signals into the deficit and the lost inertia, in `AssetFrequencyDynamics.Simulate` (the batch
path) and `AssetAwareFrequencyIntegrator.Step` (the live path). The CLI switch is `--asset-dynamics` (honoured by `trip`;
[Global switches](<0019 CLI reference.md#global-switches>)).

**Rules that are invariants.**

- The path is off by default (`DynamicsConfig.EnableAssetDynamics`), and an empty or never-operating fleet contributes exactly
  `0.0`, so `x + 0.0 - 0.0 == x` and the trajectory is bit-identical to the lumped baseline. Pin it with a reduction test
  (`AssetDynamicsReductionTests`).
- Every unmeasurable control parameter is `CuratedPlaceholder` with `[VERIFY]`. The aggregate fleet result is tagged
  `UnvalidatedAtScale`, never `Recorded` (`AssetEnvelopeResult.Provenance`).
- For a real-fleet claim, the deliverable is a parameter-sweep envelope, not a point curve: `AssetEnvelope.Sweep` runs the
  `2^k` corners of up to 12 swept parameters with a tornado attribution, and `CascadeIsPossible` is `true` when the band
  brackets both "embedded generation trips" and "it does not", which is itself the finding. Validate against a recorded
  event with `AssetValidation.Compare` before calling any output evidence.
- A grid-forming synthetic-inertia term lowers RoCoF as a power injection. It is never folded into `E`.

**Tests to copy.** `AssetDynamicsReductionTests`, `InverterTripCascadeTests`, `AssetEnvelopeTests`, `AssetClassSupportTests`,
`AssetBindingTests`, `AssetProvenanceTests`, `AssetLiveIntegratorTests` and `AssetValidationTests`. The controller above is
covered by tests of this shape, which were run:

```csharp
[Fact]
public void Inside_the_deadband_the_controller_adds_exactly_zero()
{
    var c = new DroopController(Class(), bus: 1, pgMw: 100, ratedMw: 100);
    AssetActuation a = c.Update(new AssetStepContext(LocalHz: 50.01, LocalRocofHzPerS: 0, LocalPhaseDeg: 0, DtSeconds: 0.02, ClockSeconds: 0));
    Assert.Equal(0.0, a.ActivePowerMw);
    Assert.False(a.TrippedNow);
}

[Fact]
public void A_class_with_a_key_nothing_reads_is_refused_at_load()
{
    var bad = Class(("droopGain", 5.0));
    var ex = Assert.Throws<InvalidDataException>(() => new AssetClassLibrary(new[] { bad }));
    Assert.Contains("droopGain", ex.Message);
}
```

Here `Class(...)` builds an `AssetClass` of kind `GridFollowingInverter` with a `_default` provenance of
`CuratedPlaceholder` and a note containing `[VERIFY]`. With a 10 % headroom on 100 MW, eight seconds at 50.3 Hz settle at
`-10.000` MW, and a RoCoF of 2 Hz/s held for 0.5 s reports one trip of 100 MW.

**Pitfalls.**

- Voltage and loading stages (and the passive plant) read `LocalVoltagePu` and `LoadingFraction`. In the pure lumped model
  they stay `1.0` and `0.0`; `AssetAwareFrequencyIntegrator.SetNetworkState` supplies solved values from `LiveSim`, and a
  controller that needs them is inert without that feed.
- Read a parameter with `cls.Param(key, fallback)` and never with a literal in the controller: the loader accepts only keys
  that appear in the kind's table.
- A passive kind bound to a generator and an active kind bound to a branch are skipped when the fleet is built. A
  `transformer` class bound to a branch that is not a transformer throws `InvalidOperationException` at build, and an unnamed
  branch has no key and is skipped.

## A protection

**Seam** (`src/GridSim.Core/Assets/Protection/`). A protection generalises the inverter trip block into one or more
definite-time `ProtectionStage`s: a `MonitoredQuantity` (`Frequency`, `Rocof`, `Voltage`, `LoadingFraction`, `PhaseShift`)
crossing a threshold in a `ThresholdSense` (`Above` or `Below`), held for `DwellSeconds`, driving a `ProtectionAction`.
`ProtectionEvaluator` is the shared dwell and edge state machine for one stage: it returns `ProtectionEdge.Fired` exactly
once when the dwell is satisfied and `Restored` after a finite `AutoRestoreDelayS`. `ProtectionController` is an
`IActiveAssetDynamics` (`AssetKind.Protection`) that builds the stages from a class and maps their actions onto the
channels `AssetActuation` already has:

| Action | Effect |
|---|---|
| `TripInfeed` | Removes the protected infeed (`TrippedInfeedMw`, once, however many stages latch). |
| `ShedDemand` | A positive swing-power term (low-frequency demand disconnection). |
| `InjectStep` | A signed swing-power step. |
| `OpenBranch` | Opens `TargetBranch`; a finite `AutoRestoreDelayS` recloses it. Holds are counted per branch, so the branch recloses only when the last stage holding it open releases it. |
| `RestoreBranch` | Reserved. `ProtectionEvaluator` refuses it with `NotSupportedException`: nothing acts on it. A recloser restores through the delay of an `OpenBranch` stage. |

**Author a protection.** Add a `kind: "protection"` class to `asset-classes.json` and place it in `protections.json`
(`{ "name", "class", "monitorBus", "capacityMw", "targetBranch" }`, the last for a recloser). `ProtectionController` builds one
stage for each canonical key that is present:

| Keys | Stage |
|---|---|
| `rocofTripHzPerS`, `rocofTripConfirmS` | RoCoF above the threshold (scaled by `localRocofUpliftFactor`), `TripInfeed` |
| `dfDtTripHzPerS`, `dfDtTripDwellS` | A second RoCoF stage |
| `ufTripHz`, `ufTripDwellS` and `ofTripHz`, `ofTripDwellS` | Frequency below or above |
| `uvTripPu`, `uvTripDwellS` and `ovTripPu`, `ovTripDwellS` | Voltage below or above |
| `lvrtVoltagePu` | Voltage below the threshold with no dwell, when above 0 |
| `overloadTripFraction`, `overloadTripDwellS` | Loading fraction above the threshold |
| `vectorShiftTripDeg` | Phase step above the threshold, when finite |
| `lfddStageHz`, `lfddDwellS`, `lfddBlockMw` | Frequency below, always `ShedDemand` of the block |
| `autoRecloseDelayS` | Turns every trip stage into `OpenBranch` with this restore delay; needs `targetBranch` |

A class that sets `autoRecloseDelayS` is a recloser. The loader refuses a recloser with no target, a target that is not a
branch, a target on a device that is not a recloser, a `monitorBus` that is not a bus and an instance whose class is not a
protection, listing every problem in one `InvalidDataException` (`ProtectionModel.ValidateAgainst`). Loaded into a copy of
`data/gb-synthetic`, this class and placement produced one stage and a fleet of 43 active assets (40 inverters and 3
protections):

```json
{ "name": "prot-uv-demo", "kind": "protection", "mode": "protection",
  "params": { "uvTripPu": 0.85, "uvTripDwellS": 0.5, "autoRecloseDelayS": 3.0 },
  "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "No operational data for this device [VERIFY]" } } }
```

```json
{ "name": "uv-demo-1", "class": "prot-uv-demo", "monitorBus": 2, "capacityMw": 50, "targetBranch": "NSC 400 tie" }
```

**Skeleton: a stage in code.** A new stage kind is a new `MonitoredQuantity` or `ProtectionAction` plus a key in
`ProtectionController.BuildStages`. The evaluator is quantity-agnostic; the caller selects the value:

```csharp
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;

var stage = new ProtectionStage(MonitoredQuantity.Voltage, ThresholdSense.Below, Threshold: 0.8,
    DwellSeconds: 0.2, ProtectionAction.TripInfeed);
var ev = new ProtectionEvaluator(stage);
for (int i = 0; i < 40; i++)
{
    var ctx = new AssetStepContext(50.0, 0.0, 0.0, DtSeconds: 0.02, ClockSeconds: i * 0.02, LocalVoltagePu: 0.7);
    if (ev.Step(in ctx, ctx.LocalVoltagePu) == ProtectionEdge.Fired) { /* fires at i = 10, the eleventh step */ }
}
```

The dwell accumulates `0.02` s per step in floating point: the tenth accumulation is `0.19999999999999998`, below `0.2`,
so the stage fires on the eleventh step, `i = 10`.

**Wire it.** A new key is an `AssetParams` constant, an entry in `AssetClassSupport`'s protection key array, a stage in
`BuildStages`, and the key in `schemas/v2/kinds/protection.kind.json`. The GB protection classes are emitted by the GDA
repository's `NetworkModel/build_protections.py`, which is outside this repository ([Technical 15](<../technical/0015 Protections.md>)).

**Rules that are invariants.**

- The layer is null-gated: `GridModel.Protections` is `null` unless a case ships `protections.json`, so the frequency and
  power-flow reduction gates hold byte for byte.
- Grounded thresholds are tagged `Recorded` or `DerivedFromRecorded`; every unmeasurable setting (recloser, voltage,
  thermal) is `CuratedPlaceholder` with `[VERIFY]`.
- A topology recloser goes through `Branch.InService`, then `YBus` and the breadth-first search skip the branch, then
  `IslandPartition` (a slack is elected per island, and an island with none blacks out), then a re-solve. A fully connected
  model must solve bit-identically; pin it before exposing any topology feature (`IslandPartitionTests`).

**Tests to copy.** `ProtectionTests` (the reduction gate, each protection operating at its threshold, dynamic LFDD arresting a
fall), `ProtectionEvaluatorTests` (dwell, sub-dwell spike, auto-reclose, arm delay), `ProtectionRecloserTests`,
`IslandPartitionTests` and `ProtectionVoltageTests`:

```csharp
[Fact]
public void A_stage_fires_once_after_its_dwell_and_RestoreBranch_is_refused()
{
    var stage = new ProtectionStage(MonitoredQuantity.Voltage, ThresholdSense.Below, Threshold: 0.8, DwellSeconds: 0.2, ProtectionAction.TripInfeed);
    var ev = new ProtectionEvaluator(stage);
    int firedAt = -1, fired = 0;
    for (int i = 0; i < 40; i++)
    {
        var ctx = new AssetStepContext(50.0, 0, 0, DtSeconds: 0.02, ClockSeconds: i * 0.02, LocalVoltagePu: 0.7);
        if (ev.Step(in ctx, ctx.LocalVoltagePu) == ProtectionEdge.Fired) { fired++; if (firedAt < 0) firedAt = i; }
    }
    Assert.Equal(10, firedAt);
    Assert.Equal(1, fired);
    Assert.Throws<NotSupportedException>(() => new ProtectionEvaluator(stage with { Action = ProtectionAction.RestoreBranch }));
}
```

**Pitfalls.** The passive relay overlay (`IProtectionRelay`, `ProtectionAssessment`, `ProtectionCascade`) is a different layer
from this one: it scans a solved state and never feeds the swing equation ([Technical 17](<../technical/0017 Protection assessment (a passive relay overlay).md>)).
`S3ProtectionGroundingTests` belongs to that layer: it pins the `ProtectionSettings` defaults to G99 Issue 2 Table 10.1
(under-voltage `0.80` pu, over-voltage `1.10` pu, under-frequency `47.5` Hz, over-frequency `52.0` Hz, RoCoF `1` Hz/s),
the `400` ms default coordination time interval and the IEC curve constants. It never touches `ProtectionStage` or
`ProtectionController`.
A stage that watches voltage or loading is inert in the lumped model for the reason given under the asset class. An
`ArmDelayS` is set only by code that builds stages directly; an `armDelayS` key in a class is refused as unknown.

## A network importer or exporter

**Seam.** There is no importer interface. A reader is a static type in `src/GridSim.Core/IO/` that turns text or files into a
`GridModel`, and a writer is the inverse. The shipped ones are the template for each format:

| Format | Reader | Writer |
|---|---|---|
| MATPOWER `.m` | `MatpowerParser.Parse`, `ParseFile`, `ParseWithDiagnostics` (`IO/Matpower/MatpowerParser.cs`), with `MatpowerUnitNormalizer` | `MatpowerWriter.Write` |
| JSON v1 case folder | `JsonGridLoader.LoadDirectory` (`IO/Json/JsonGridLoader.cs`) | `JsonCaseWriter.WriteDirectory` |
| JSON v2 manifest | `V2CaseLoader.Load`, `LoadWithNotes` (`IO/Json/V2CaseLoader.cs`) | none |
| CIM / CGMES | `CimImporter.Parse` then `CimGridModelBridge.ToGridModel` (`IO/Cim/`) | `CimExporter.Export`, which takes a `CimNetwork`, not a `GridModel` |

**The rules a reader follows.**

- It reports, as notes, one plain-English line for each decision it made on the user's behalf. `MatpowerParser.ParseWithDiagnostics`
  and `V2CaseLoader.LoadWithNotes` return them with the model, and `CimImporter` lists what it left out in
  `CimNetwork.Diagnostics`. The CLI prints each note as `note: <file>: <text>` on stderr, and stdout stays the verb's own
  report.
- It refuses what it cannot represent, naming the file, the element and the value, with `InvalidDataException`: an unknown
  bus type or generator role, a bus without `baseKv`, a reference to a bus that does not exist, a missing slack, a branch
  name that matches nothing. `JsonGridLoader` refuses every unknown key (`JsonUnmappedMemberHandling.Disallow`), so a
  misspelt `rating_MVA` is an error and not an unrated branch. `CimGridModelBridge` throws when a load, generator, line or
  transformer names a node that is not a bus.
- It validates the model before returning it (`GridModel.Validate()`; the loader throws on every `Error` diagnostic).
- It parses numbers with the invariant culture and keeps file order: the solvers sum per-bus admittances and injections in
  model order, and a different order can change the last bit.

**The rules a writer follows** (`MatpowerWriter`, `JsonCaseWriter`). Every number is written in round-trip form, buses,
generators (the whole fleet, out-of-service units included) and branches keep model order, so the case read back solves
bit-identically. What the format cannot carry is returned as notes and, for MATPOWER, written as comments, never dropped
silently. A NaN, or an infinity where the format needs a finite value, is refused naming it. A writer never overwrites:
`JsonCaseWriter` refuses a folder that already holds any of its files with `IOException`, and `MatpowerWriter.Write`
returns text for the caller to place, so a new writer refuses an existing target the same way. The other pattern is
`NeverOverwrite.CreateNew(path, out string actualPath)` (`IO/NeverOverwrite.cs`), used by the evidence and window sinks
(`QuarantineLiveStore`) and by `MeasurementSet.SaveWithoutOverwrite`: it opens a new file with `FileMode.CreateNew` and,
where the name is taken, keeps the new content under the first free `-dupN` name (at most `NeverOverwrite.MaxDuplicates`,
1,000, then it throws `IOException`) and returns the path it used. No network writer calls it, and `convert` opens its
target with `FileMode.CreateNew` directly.

**Skeleton.** A toy line format (`base`, `bus`, `gen`, `branch` records) shows the shape: text in, a `GridModel` out, every
refusal naming the line, the model validated before it returns. It was built and solved on the sparse engine:

```csharp
using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Samples;

public sealed record ToyParseResult(GridModel Model, IReadOnlyList<string> Notes);

public static class ToyNetworkParser
{
    public static ToyParseResult Parse(string text, string name = "toy")
    {
        var notes = new List<string>();
        double baseMva = 0;
        var buses = new List<Bus>();
        var gens = new List<Generator>();
        var branches = new List<Branch>();
        var inv = CultureInfo.InvariantCulture;                      // a comma-decimal machine must read the same numbers

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int ln = 0; ln < lines.Length; ln++)
        {
            string line = lines[ln].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] f = line.Split(',', StringSplitOptions.TrimEntries);

            double D(int i) => i < f.Length && double.TryParse(f[i], NumberStyles.Float, inv, out double v) && double.IsFinite(v)
                ? v : throw new InvalidDataException($"line {ln + 1}: field {i + 1} of '{f[0]}' is not a finite number: '{(i < f.Length ? f[i] : "")}'.");
            int I(int i) => (int)D(i);

            switch (f[0])
            {
                case "base": baseMva = D(1); break;
                case "bus":
                    BusType t = f[2] switch
                    {
                        "slack" => BusType.Slack, "pv" => BusType.Pv, "pq" => BusType.Pq,
                        _ => throw new InvalidDataException($"line {ln + 1}: bus type '{f[2]}' is not slack, pv or pq."),
                    };
                    buses.Add(new Bus(I(1), t, Pd: D(3), Qd: D(4), BaseKv: D(5)));
                    break;
                case "gen": gens.Add(new Generator(I(1), Pg: D(2), Vg: D(3), MBase: baseMva)); break;
                case "branch": branches.Add(new Branch(I(1), I(2), D(3), D(4), D(5))); break;
                default: throw new InvalidDataException($"line {ln + 1}: unknown record '{f[0]}'.");
            }
        }
        if (baseMva <= 0) throw new InvalidDataException("no 'base' record, or a base that is not positive.");
        notes.Add("No nominal frequency in the format; 50 Hz assumed.");

        var model = new GridModel { Name = name, BaseMva = baseMva, Buses = buses, Generators = gens, Branches = branches };
        string[] errors = model.Validate().Where(d => d.Severity == ValidationSeverity.Error).Select(d => d.Message).ToArray();
        if (errors.Length > 0) throw new InvalidDataException("Invalid grid model:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", errors));
        return new ToyParseResult(model, notes);
    }
}
```

The test that goes with it parses, solves and refuses, in the pattern of `MatpowerParserTests`:

```csharp
[Fact]
public void The_toy_format_parses_solves_and_refuses_what_it_cannot_read()
{
    const string text = "base,100\nbus,1,slack,0,0,132\nbus,2,pq,40,10,132\ngen,1,45,1.02\nbranch,1,2,0.01,0.05,0.02\n";
    ToyParseResult r = ToyNetworkParser.Parse(text);
    PowerFlowResult pf = new SparseNewtonRaphsonPowerFlow().Solve(r.Model);
    Assert.True(pf.Converged);
    Assert.Equal(2, r.Model.BusCount);

    var typo = Assert.Throws<InvalidDataException>(() => ToyNetworkParser.Parse("base,100\nbus,1,slak,0,0,132"));
    Assert.Contains("line 2", typo.Message);
    Assert.Throws<InvalidDataException>(() => ToyNetworkParser.Parse("base,100\nbus,1,pq,0,0,132\nbus,2,pq,0,0,132"));   // no slack
}
```

**Wire it.** Core has no registry to edit. In the CLI, every user-facing model load goes through `Gated`
(`src/GridSim.Cli/Gated.cs`): `Gated.Dir` for a JSON folder, `Gated.Matpower` for a `.m` file, `Gated.V2Case` for a v2
manifest. `Gated` is the one place the licence tier's bus limit is enforced, it turns a content failure
(`InvalidDataException`, `JsonException`) into a `CaseLoadException` that the process-wide handler reports as `error: ...`
with exit 1, and it prints the parse notes. A new format adds a `Gated` method that calls `Unloadable(path, what, load)` and
`Enforce(path, model)`, and a branch in `LoadNamed` (`Program.cs`, which maps a built-in name or a file extension, `.m` or
`.json`, to a `Gated` method; `LoadCaseArgument` calls it) so every verb that takes a `<case>` accepts it.
To make it a `convert` target, extend that verb and keep its proof: the written case is read back, both are solved with the
contingency option set and the tap-changer loop, and every bus voltage, angle, injection, loss total and line flow must have
the same bits ([`convert`](<0019 CLI reference.md#convert>)). A format with a schema also gets a `validate` rule
([`validate`](<0019 CLI reference.md#validate>)).

**JSON v2 and CIM.** The v2 manifest (`gridsim-case/2`) is built by `V2CaseLoader` through the same
`JsonGridLoader.Build` as v1, so a v2 case whose elements equal a v1 case gives the same model bit for bit. Its schema, kind
specs and templates are owned by [Technical 28, The v2 JSON schema](<../technical/0028 The v2 JSON schema.md>); the other formats
by [Technical 10](<../technical/0010 File and wire formats.md>). Extending v2 content means a kind spec under
`schemas/v2/kinds/`, covered by the `V2KindSpec*` tests and by `V1ToV2ProjectionTests`. The CIM importer, bridge and exporter
are Core library code with no CLI verb (see [Limits](#limits)); [Technical 18](<../technical/0018 Real-time, distribution and dynamic estimation.md>)
covers their mapping.

**Tests to copy.** `MatpowerParserTests`, `N165MatpowerUnitDefaultTests`, `ConvertAndValidateTests` (the round trip and the
never-overwrite rule), `JsonLoaderStrictKeysTests` and `JsonLoaderS4RefusalTests` (each refusal on a copy of a bundled case),
`CimImporterTests`, `CimGridModelBridgeTests`, `CimTransformerImportTests`, `CimMultiProfileTests`, `Wave6IoTests` (the CIM exporter),
`SolveExportRoundTripTests`, and under `tests/GridSim.Tests/V2/` `V2CaseLoaderTests` and `V1ToV2ProjectionTests`.

**Pitfalls.** A unit repair belongs in the reader, once: a raw ohm and kW distribution feeder is normalised by
`MatpowerUnitNormalizer` in the parser's default mode, because a Core caller who forgot a wrapper would otherwise read kW
demand as MW and solve a network loaded 1,000 times too heavily. A reader that drops an element (an isolated bus, an out-of-service
branch) says so in a note and removes everything incident to it, so no dangling reference reaches a solver.

## A DNO reader

**Seam.** A reader is a static `Load` that returns an `LtdsDataset` (`src/GridSim.Gda/GdaModelBuilder.cs`), built from the
shared record types in `src/GridSim.Gda/Ltds/LtdsModel.cs`: `LtdsNode`, `LtdsCircuit`, `LtdsTransformer2`,
`LtdsTransformer3`, `LtdsDemand` and `LtdsGeneration`. `GdaModelBuilder.Build(LtdsDataset, GdaBuildOptions?)` consumes any
dataset identically and returns a `GdaBuildResult` (the `GridModel` and `GdaDiagnostics`), so there is no registry to edit.

**The reader's one promise is its unit convention.** Tag the dataset `ImpedanceConvention.OhmicReferredToGsp` (ohms and
microsiemens referred to the GSP voltage) or `PercentOn100Mva`, and set `TransformerPercentOnRating` truthfully (`true` is the
default; UKPN and NPg pass `false`, because their transformer impedances are percent on 100 MVA). The builder converts; the
reader never does. Ratings are MVA (a rating published in amps converts with `sqrt(3) * kV * A / 1000`, as the
private `AmpsToMva` of `UkpnSource` does). A series element whose `R1` and `X1` magnitudes sum to at most
`GdaBuildDefaults.TieImpedanceThresholdOhm` (`1e-9` ohm) is a busbar tie and is merged, not a branch. Read files through
`CsvTable` and `DataSourceTable.Open` (`src/GridSim.Gda/Csv/`), and route dated files through `GdaPaths.PickNewest`, so the
as-of horizon holds.

**Skeleton.** A reader for an imaginary operator, with a two-node synthetic network. It built, solved in 3 iterations and
reported one island with nothing unmatched:

```csharp
using GridSim.Gda;
using GridSim.Gda.Ltds;

namespace GridSim.Samples;

public static class ToyDnoSource
{
    public static LtdsDataset Load()
    {
        var nodes = new[]
        {
            new LtdsNode("TOY", "GSP1", "Toyton GSP 33kV", "Toyton BSP", "Toyton 33kV", "Energised"),
            new LtdsNode("TOY", "PRI1", "Toyton Primary 33kV", "Toyton BSP", "Toyton 33kV", "Energised"),
        };
        var circuits = new[]
        {
            new LtdsCircuit("TOY", "Toyton 33kV", "Toyton BSP", "GSP1", "PRI1", "Energised", KvBase: 33.0, "C1",
                R1Ohm: 0.5, X1Ohm: 1.5, B1MicroS: 20.0, R0Ohm: 1.5, X0Ohm: 4.5,
                WinterRatingMva: 40, SummerRatingMva: 35, LengthKm: 6.0, Equipment: "cable"),
        };
        var demand = new[] { new LtdsDemand("Toyton 33kV", "Toyton BSP", "Toyton Primary", "PRI1", 33.0, MaxDemandMw: 12.0, PowerFactor: 0.97, FirmCapacityMva: 20) };
        var generation = new[] { new LtdsGeneration("Toyton 33kV", "Toyton BSP", "Toyton Primary", 33.0, ExportCapacityMw: 3.0, FuelType: "Solar", Status: "Energised") };
        return new LtdsDataset(
            "TOY/synthetic", ImpedanceConvention.OhmicReferredToGsp,
            nodes, circuits, Array.Empty<LtdsTransformer2>(), Array.Empty<LtdsTransformer3>(), demand, generation);
    }
}
```

**Wire it** in `Program.cs`: add the reader to the `--dno` switch of `gda` (`nged` by default, `ukpn`, `npg`; the `FailFlag`
arm names the accepted values, and an unknown value exits 2), and name it in the `gda` section of
[`0019 CLI reference.md`](<0019 CLI reference.md#gda>). **Copy** `Ukpn/UkpnSource.cs` (percent on 100 MVA, ratings in amps, node codes
kept apart per licence area) or `Npg/NpgSource.cs` (Parquet, percent on 100 MVA). For a new file format rather than an
operator, the template is `MatpowerParser`.

**Test** in the manner of `tests/GridSim.Tests/Gda/GdaBuildTests.cs`: a small synthetic fixture with a known answer, checked
through `GdaDiagnostics` (islands, then unmatched demand and generation first) and a converged solve:

```csharp
[Fact]
public void A_dno_reader_builds_one_island_with_nothing_unmatched()
{
    GdaBuildResult r = GdaModelBuilder.Build(ToyDnoSource.Load());
    Assert.Equal(1, r.Diagnostics.Islands);
    Assert.Equal(0, r.Diagnostics.UnmatchedDemand);
    Assert.Equal(0, r.Diagnostics.UnmatchedGen);
    Assert.True(new SparseNewtonRaphsonPowerFlow().Solve(r.Model).Converged);
}
```

Also read `PercentConventionTests` and `GdaBuildDataContractTests`. Never commit real DNO data: fixtures are synthetic by
policy. The bridge and the build are described in [Technical 9](<../technical/0009 GDA integration.md>).

## An analysis job

**Seam.** Post-solve work that should run in the parallel per-cycle fan-out, beside the N-1 shards, harmonics, RoCoF, physics
conformance and the watch list, is an `AnalysisJob` (`src/GridSim.Core/Scheduling/AnalysisJob.cs`): a stable `Id`, a `Name`,
a `JobPriority`, a `SeedCoreMs` cost seed and `Run(CycleContext, CancellationToken)`. Read the solved grid from the
immutable `CycleContext.Frame` (a `SimFrame`: the regulated `GridModel` and the `PowerFlowResult`) and treat it as strictly
read-only. The frame is one shared reference handed to every job, so a job that writes `Vm`, `VaDeg`, `PInjMw` or `QInjMvar`
corrupts the whole cycle. A job that must re-solve takes its own engine from `CycleContext.SolverFactory`, because engines
are not thread-safe.

**Choose the priority deliberately.**

- `High` must complete every cycle and is never cancelled (`n-1:hot`, `harmonics`, `watch`). The base power solve is not a
  job: it runs before the fan-out and its cost is passed to the scheduler for telemetry only.
- `Low` is deferrable. It is admitted while the running estimate, the High tier's estimated cost plus the Low jobs
  already admitted, still fits the core-millisecond budget (`workers x window`). At least one Low job is admitted every
  cycle, even when its estimate alone exceeds the budget. A Low job runs under a deadline token and may be cancelled. A
  deferred or cancelled job keeps its place at the front of the rotating pool; a completed or faulted job goes to the
  back, so a faulting job is not retried every cycle.
- Express divisible work as many small jobs with stable ids (`n-1:lp:0`, `n-1:lp:1`, ...), not one large job. Stable ids are
  what let the scheduler rotate Low shards to full coverage over several cycles without starvation, and what key the
  `JobCostModel` moving average that governs admission. Give `SeedCoreMs` a realistic first estimate: it orders the first
  cycle only.
- Honour the token. A Low job calls `ct.ThrowIfCancellationRequested()` or folds the token into
  `PowerFlowOptions.Cancellation`, as `AnalysisJobLibrary` does for the N-1 jobs (`ScreenBranchesParallel` also takes the
  token), so a long solve aborts mid-iteration.

**Skeleton.** A Low job that reports the most loaded branch of the frame, run through the real scheduler:

```csharp
using GridSim.Core.PowerFlow;
using GridSim.Core.Scheduling;

namespace GridSim.Samples;

public static class MyJobs
{
    public static AnalysisJob MostLoadedBranch() => new()
    {
        Id = "mine:most-loaded",                       // stable across cycles: it keys the cost average and the Low rotation
        Name = "Most loaded branch",
        Priority = JobPriority.Low,
        SeedCoreMs = 1.0,                              // first-cycle estimate for admission
        Run = (ctx, ct) =>
        {
            ct.ThrowIfCancellationRequested();         // a Low job aborts promptly at the cycle deadline
            IReadOnlyList<LineFlow> flows = ctx.Frame.Result.LineFlows;
            int best = -1;
            for (int i = 0; i < flows.Count; i++)
                if (best < 0 || flows[i].LoadingPercent > flows[best].LoadingPercent) best = i;
            return best < 0 ? null : flows[best];
        },
    };
}
```

**Wire it.** Add a factory to `AnalysisJobLibrary` that wraps an existing `GridSim.Core` analysis as a job (nothing is
re-implemented there), and a key to the `enabled` filter of `AnalysisJobLibrary.Build` (`watch`, `n-1`, `harmonics`, `rocof`,
`physics`). `cycle --jobs` passes its comma list straight to that filter. `CycleScheduler` drains High before Low across the worker pool,
`CycleRunner` runs the base solve and then the cycle, and `ResidentCycleService` runs it off the UI thread for both
dashboards. A job result is `JobResult` with `JobOutcome` `Completed`, `Cancelled`, `Deferred` or `Faulted`; a fault is
isolated, and the cycle continues.

**Prove it.** [`gridsim cycle`](<0019 CLI reference.md#cycle>) runs the real-time cycle, prints per-cycle base-solve time, High
completion, the Low done, admitted and deferred counts, utilisation and N-1 coverage, and exits 1 if the High tier is ever
missed. Timings differ by machine and run:

```
$ gridsim cycle case118 --workers 8 --window-ms 1500 --cycles 3
GridSim  -  prioritised parallel analysis cycle: IEEE 118-bus
============================================================================================
case 118 buses / 186 branches   engine Newton-Raphson (dense)
window 1500 ms   workers 8   budget 12.0 core-s/cycle   jobs 13 (3 High / 10 Low)
--------------------------------------------------------------------------------------------
cycle |  base ms | wall ms |  HP   | LP done/adm/def | util% | backlog | note
--------------------------------------------------------------------------------------------
    0 |    145.1 |     391 |  3/3  |  10/10 /0     |   19% |       0 | n-1 shards 8/8
    1 |     34.2 |     229 |  3/3  |  10/10 /0     |   10% |       0 | n-1 shards 8/8
    2 |     32.5 |     244 |  3/3  |  10/10 /0     |   12% |       0 | n-1 shards 8/8
--------------------------------------------------------------------------------------------
3 cycles   avg util 14%   HP met every cycle: YES   N-1 coverage 8/8 shards
[exit 0]
```

**Tests to copy.** `tests/GridSim.Tests/Scheduling/CycleSchedulerTests.cs` (budget equals workers times window, High always
admitted, Low rotation to full coverage, results independent of the worker count, no frame mutation, a faulting job
isolated), `CycleDeadlineDeterministicTests`, `ResumableShardTests` and `SolverCancellationTests`. This test adds the job to
the standard set and checks that the High tier still holds:

```csharp
[Fact]
public void A_job_reads_the_frame_and_the_cycle_still_meets_its_high_tier()
{
    GridModel m = IeeeCases.Case9();
    PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(m);
    var frame = new SimFrame(m, r, 0, 0, 50.0, r.PInjMw.Where(p => p > 0).Sum(), m.Buses.Sum(b => b.Pd));
    var opt = new CycleOptions { Workers = 2, WindowMs = 1000 };
    var jobs = new List<AnalysisJob>(AnalysisJobLibrary.Build(m, opt)) { MyJobs.MostLoadedBranch() };
    CycleResult c = new CycleScheduler(opt, jobs).RunCycle(frame, () => new SparseNewtonRaphsonPowerFlow(), baseSolveMs: 0);
    Assert.True(c.HighTierMet);
    Assert.Equal(JobOutcome.Completed, c.Results.Single(j => j.Id == "mine:most-loaded").Outcome);
}
```

**Pitfalls.** `CycleOptions.PipelineBacklog` throws `NotSupportedException` in `CycleRunner`: an option the engine does not
implement is refused, not ignored. A word in `cycle --jobs` that is not a job key enables nothing and is not refused (see
[Limits](#limits)), so check that your key appears in the `jobs N (H High / L Low)` line of the report. The harmonics job is skipped above `CycleOptions.HarmonicsBusLimit` (1,500 buses) so a
huge case never blocks the window.

## An evidence capability

**Seams** (`src/GridSim.Core/`). A new **event** is a `GridEvent` (`Events/GridEvent.cs`) in a `gridsim-event-catalog/1` file
under `data/events/`: losses are negative-MW `PowerStep`s, recorded actions are positive, and every non-primary figure is
tagged `CuratedPlaceholder` with `[VERIFY]` notes. A new **device** is a `DeviceConfig` (`Scenarios/ScenarioSpec.cs`); its
`Apply` keeps rotational inertia (`DeviceRotationalInertiaMwS`) and fast response (`FastResponseMw`) as two separate terms,
never merged. New sweep values extend the `SweepAxes` lists, and `ScenarioEngine.Run` iterates whatever it is given. A new
**cost basis** follows `Evidence/AvoidedCost.FromLfddAvoided`. The built-in events are `gb-2019-08-09`, `gb-2023-12-22` (the
example of an event with recorded response services and an offset pre-event frequency) and `gb-largest-loss-1320`, in
`Events/EventCatalog.cs`.

**Wiring.** The assembly point is `EvidenceReport.Build` (`Evidence/EvidenceReport.cs`), which fixes the order: the
credibility gate first, then the counterfactual, risk curve, fleet and cost only when unlocked. The CLI flags live in the
`Run*` handlers in `Program.cs` (`RunReplayEvent`, `RunScenario`, `RunH0Study`, `RunFleet`, `RunEvidence`).

**Rules that are invariants.**

- The reproduced-baseline gate. `ScenarioEngine.Run` and `H0StudyEngine` throw `BaselineNotReproducedException`;
  `EvidenceReport` withholds the counterfactual sections; the CLI exits 3.
- The 169-hour forward-inference horizon on every dated input (`ForwardInferenceGuard.RejectFuture`); a violation prints
  `REJECTED (forward inference)` and exits 2.
- Corner scaling never touches the initial RoCoF (`Corners_bracket_the_nadir_but_not_the_initial_rocof`).
- A one-unit, zero-stagger fleet reproduces the single-device simulation exactly (`One_unit_zero_stagger_equals_Simulate`).

**Tests to copy.** `ScenarioEngineTests` (the gate throws when the baseline is not reproduced, the risk curve worsens as
inertia falls, the matrix has a cell per inertia, device and corner), `EvidenceReportTests` (the counterfactual is withheld
when the baseline fails, provenance-labelled columns never merge inertia with FFR), `FleetDynamicsTests` and
`ForwardInferenceGuardTests`. The evidence chapter is [Technical 7](<../technical/0007 The evidence engine.md>), and the
rule for the user is [ch. 20, Counterfactuals](<0020 Counterfactuals.md>).

## A CLI verb

**How `Program.cs` dispatches.** `src/GridSim.Cli/Program.cs` is one file of top-level statements. It runs in a fixed order,
and a new verb is reached after all of these have acted:

1. `--version` is answered first, and `help <verb>`, `<verb> --help` and `<verb> -h` print the verb's own section of
   [`0019 CLI reference.md`](<0019 CLI reference.md>) (compiled into the binary as an embedded resource) and exit 0.
2. A process-wide handler turns a missing path or a `CaseLoadException` into `error: ...` with exit 1; any other unhandled
   exception is a defect and writes a crash log.
3. `--importLicence <file>` installs a licence and exits. Otherwise the licence is resolved offline, then the
   **`knownFlags`** gate refuses a flag no verb knows (exit 2).
4. `--audit-log`, the global switches (`--once`, `--cpu`, `--gpu`, `--inertia-model`, `--asset-dynamics`, `--forming`,
   `--forming-inertia`, `--forming-damping`, `--bfs`, `--export-jacobian`, `--enableExport` and `--ticks`, as in
   [Global switches](<0019 CLI reference.md#global-switches>)) and the result-file flags (`CliOutput.TakeFlags`) are taken
   off the argument list.
5. **`VerbFlags.Check`** refuses a flag that another verb owns. A verb's accepted flags are every `--flag` written in its
   own section of `0019 CLI reference.md`, its group's shared notes and the global switches. Without a section the verb has no
   flag check, and `CliHelpContractTests` and `VerbHelpTests` fail.
6. The dispatch blocks run in turn: `if (rest.Count > 0 && rest[0].Equals("<verb>", StringComparison.OrdinalIgnoreCase))`,
   a call to `Run<Verb>(rest, baseDir)`, then `CliOutput.Finish(args)` and `return`.

**The contract a verb keeps.**

- **Flag parsing fails loud.** Use `FlagInt`, `FlagDouble` and `FlagString`: they parse with the invariant culture, refuse
  `NaN` and `Infinity`, and refuse a flag that is present with no value (only an absent flag takes its default). Refuse an
  out-of-range value with `FailFlag<T>(name, value, expected)`, which prints `error: <flag> expected <what> but got '<value>'`
  and exits 2. A boolean flag is read with `a.Remove("--flag")` before positionals are read, because `Positionals` treats
  every remaining `--flag` as taking the next token as its value. Name a literal that more than one place needs in
  `CliDefaults.cs`.
- **Cases load through the shared selector.** `SingleCasePositional(verb, a)` refuses a second case (exit 2);
  `LoadSelected(verb, (label, () => LoadCaseArgument(arg, root)))` loads it, and an unknown name is exit 2 naming the verb and
  the built-in names; `SelectCase` also honours `--dir` and refuses a positional case that disagrees with it. A batch verb loads lazily and reports
  a failed case as a row.
- **Exit codes follow the table** ([Parsing rules and exit codes](<0019 CLI reference.md#parsing-rules-and-exit-codes>)):
  `0` success; `1` input error or a failed check; `2` usage error; `3` validation or gate failure, and any non-converged
  power flow; `77` only for `crossval`. A verb that prints `DID NOT CONVERGE` sets exit 3 (`CliExitCodeContractTests` scans
  for it). A rejected forward-inference stamp is `REJECTED (forward inference): ...` with exit 2.
- **The engine comes from `SolverBackend`.** Call `backend.NewtonSolver(m)`, `backend.Solver(m)` or `backend.Pick(m)`; never
  build an engine or compare a bus count to choose one (`CliEngineChoiceScanTests`).
- **Result files.** A verb whose result is a summary and tables can be a result-file verb: add its name to
  `CliOutput.WriterVerbs` (`src/GridSim.Cli/CliResultWriter.cs`), fill `CliOutput.Report` (`Set` for the summary, `Table` and
  `Row` for the tables, `Complete` at the end) and call `CliOutput.Finish(args)` after it returns. `--json`, `--csv` and
  `--out` are then accepted, written once the result is complete (including a failing verdict, with the exit code inside).
  On every other verb the writer gate refuses `--json` and `--csv` with exit 2 (`modal`'s own `--json` and `screen`'s own
  `--csv` excepted). It leaves `--out` alone, because many verbs use it for their own folder or report path, so
  `VerbFlags.Check` refuses `--out` on a verb only when that verb's section of the reference does not name it. A run that
  stops before it has a result writes nothing and says so on stderr.
- **Audit-log support.** `--audit-log` is accepted only on the verbs that produce evidence (`estimate`, `crossval`,
  `interpose`) and refused with exit 2 on any other. A new evidence verb is added to that list in `Program.cs`, given a
  provenance in the `auditProvenance` switch beside it, and records its inputs and outcome through `auditRun?.AddInput(...)`
  and `auditRun?.SetOutcome(...)`; the log is opened and verified before the verb does any work. See
  [Technical 31](<../technical/0031 Audit log and diagnostics.md>) and
  [ch. 26](<0026 Audit log, deployment and assurance.md>).

These two real runs show the conventions on the shipped binary:

```
$ gridsim case14 --once --bogus
error: unknown flag '--bogus' (misspelled?). Run 'gridsim --help' for the verb list, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

```
$ gridsim case14 --once --audit-log results/ch26x.log
error: --audit-log applies to estimate, crossval, interpose and serve only (got 'case14').
[exit 2]
```

**Skeleton.** A verb that ranks the branches by real-power loss. This is a template: it is not part of the shipped CLI. It was
built into a copy of `GridSim.Cli` with the edits below, and run for a good value, a bad value, a flag of another verb, an
unknown case, `--json` and `--audit-log`; the results are exit 0, 2, 2, 2, 0 and 2.

```csharp
// 1. Program.cs, knownFlags: a flag the verb adds
    "--min-loss-mw",   // lossrank: hide branches that lose less than this

// 2. Program.cs, the --help block: the verb in its group line
    Console.WriteLine("            cpf, taps, restore, losses, lossrank, grade, dist3ph, pf,");

// 3. Program.cs, the dispatch (the pattern CliHelpContractTests scans for)
if (rest.Count > 0 && rest[0].Equals("lossrank", StringComparison.OrdinalIgnoreCase))
{
    RunLossRank(rest, baseDir);
    CliOutput.Finish(args);   // write the result files, if asked for
    return;
}

// 4. Program.cs, the handler
void RunLossRank(List<string> a, string root)
{
    // Parse and refuse every bad flag value BEFORE any work: exit 2 for a usage error, never a default.
    int top = FlagInt(a, "--top", 5);
    if (top < 1) FailFlag<int>("--top", top.ToString(System.Globalization.CultureInfo.InvariantCulture), "an integer of at least 1");
    double minLossMw = FlagDouble(a, "--min-loss-mw", 0.0);
    if (minLossMw < 0) FailFlag<double>("--min-loss-mw", minLossMw.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "a loss of 0 MW or more");

    // The one shared case selection: an unknown name is exit 2, an unloadable file exit 1, a second case exit 2.
    string pos = SingleCasePositional("lossrank", a) ?? "case9";
    GridModel m = LoadSelected("lossrank", (pos, () => LoadCaseArgument(pos, root)));

    // The engine by the shared rule, on the selected backend. A result that did not converge is a failure: exit 3.
    var result = backend.NewtonSolver(m).Solve(m);
    if (!result.Converged) { Console.Error.WriteLine("error: base case did not converge."); Environment.ExitCode = 3; return; }

    var ranked = result.LineFlows.Where(l => l.PLossMw >= minLossMw).OrderByDescending(l => l.PLossMw).ToList();
    Console.WriteLine($"Loss ranking - {m.Name} ({m.BusCount} buses, {m.Branches.Count} branches), total {result.TotalLossMw:F3} MW");
    Console.WriteLine("  | From | To | P Loss MW |");
    foreach (var l in ranked.Take(top)) Console.WriteLine($"  | {l.From} | {l.To} | {l.PLossMw:F3} |");

    // --json / --csv: every row, unrounded, from the same values (only a writer verb gets a Report).
    CliResult? rep = CliOutput.Report;
    if (rep is not null)
    {
        rep.Set("case", m.Name).Set("buses", m.BusCount).Set("total_p_loss_mw", result.TotalLossMw);
        CliResultTable rows = rep.Table("branches", "from", "to", "p_loss_mw");
        foreach (var l in ranked) rows.Row(l.From, l.To, l.PLossMw);
        rep.Complete();
    }
}

// 5. CliResultWriter.cs: add "lossrank" to CliOutput.WriterVerbs
```

**The reference section is part of the verb.** `0019 CLI reference.md` is embedded in the binary, so the verb's `--help` prints
its section, `VerbFlags` reads the accepted flags from it, and the verb-contract tests read it. Add the verb to the Verb index table and
give it a section whose heading is the verb name, with the usage line first, a flag table (`Flag`, `Default`, `Meaning`) and
the exit codes. Do not add a flag table anywhere else: task chapters link to the verb's section, they do not repeat it.

```markdown
### lossrank

`gridsim lossrank [<case>] [--top N] [--min-loss-mw X]`

The branches that lose the most real power on the solved base case, highest first. `<case>` defaults to `case9` and is
resolved like every other case argument ([Case arguments](#case-arguments)).

| Flag | Default | Meaning |
|---|---|---|
| `--top` | 5 | How many branches to print. At least 1. |
| `--min-loss-mw` | 0 | Leave out branches that lose less than this many MW. At least 0. |

Exit 3 if the base case does not converge; exit 2 for a bad flag value.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file ([Result files](#result-files)).
```

With the section in place, `--help` on the verb in the build printed the section and exited 0, and `lossrank case9 --branch 2`
was refused with this message and exit 2:

```
error: lossrank does not take --branch (it is not one of lossrank's flags, so it would be silently ignored). Run 'gridsim lossrank --help' for the flags lossrank takes, or see docs/manual/0019 CLI reference.md.
```

**Tests that guard a verb** (copy their patterns): `CliHelpContractTests` (every dispatched verb is in `--help` and in the
reference; `--help` names no verb that is not dispatched), `VerbHelpTests` (every verb resolves to a section, renders as plain
ASCII and carries the exit-code rule), `VerbFlagsTests` (a flag of another verb is refused and every flag the code reads is
in the verb's set), `CliExitCodeContractTests`, `CliFailLoudFlagContractTests`, `CliEngineChoiceScanTests`,
`CliResultWriterTests`, `CliVerbHelpProcessTests` and `CliProcessSmokeTests` (the real binary).

**Pitfalls.**

- The dispatch must match `rest[0].Equals("verb"` or `rest[0] is "verb"`; the contract tests find verbs by those two patterns.
- A flag you add must be in `knownFlags` and in the verb's section of the reference, or the run is refused (exit 2) before it
  starts.
- `Positionals` skips a flag and its value, so a boolean flag must be removed with `a.Remove` before the case is read.
- A verb that writes files defaults to a path under `results/` in the current directory (`DefaultResultsPath`) and uses an
  explicit `--out` exactly as given.
- A verb whose logic is long goes in its own class under `src/GridSim.Cli/` (`StorageCli.cs`, `LicenceCli.cs`) or, better, in
  `GridSim.Core` with the verb as a thin shell, so it is unit-testable: the test project does not use the CLI's types (it builds the CLI so that the process tests can run the binary).

## A desktop view and a web tab

The desktop app (`GridSim.Wpf`) and the web app (`GridSim.Web`) are presentation only. Both show the same solved
`SimFrame`, so a number worth showing lives in `GridSim.Core` once and both faces read it.

**Put the logic in Core.** Non-trivial physics or presentation rules go in a pure, unit-tested `GridSim.Core` class, not in
the view model. `GridSim.Core.Presentation` (`src/GridSim.Core/Presentation/`) holds the shared presenters
(`FrequencyRiskPresenter`, `StatusLampRules`, `CarbonFactors`, `InterconnectorCorridors`, `CredibleLossBasis`,
`ScadaThresholds`, `MechanicalThresholds`) and `Dynamics/StabilityMetrics.cs` the stability metrics. The test project does not
reference `GridSim.Wpf`, so a WPF view model is tested only by source-level checks
(`WpfPresentationSourceContractTests`, `PresentationConstantsAntiDriftTests`); logic in a view model is logic nobody can
test. `WebProjectionAntiDriftTests` recomputes a projected number straight from the Core presenter and fails if the web value
diverges from it. Show a real solved number, a Core-derived one, or an explicit `-`; never a plausible
invention, and never a number from a frame that did not converge (`DashboardViewModel.Update` sets `NOT CONVERGED` before any
other status verdict). Read voltages and taps from `f.Model` and `f.Result`: `SimFrame.Model` is the regulated model the solve
produced, not the model that was put in.

**Desktop: the view model.** A tab is a sub-view-model `class XVm : Notify` (`Notify`, with `Set(ref field, value)` and
`Raise(name)`, is in `src/GridSim.Wpf/DashboardViewModel.cs`) with a per-frame `Update(SimFrame f, ..., string clock)`. Rows
are small `Notify` classes created once and mutated frame to frame: `Update` runs on the UI thread, so it must be cheap and
allocation-light. This one was built against the WPF project:

```csharp
using System.Collections.ObjectModel;
using GridSim.Core.Simulation;

namespace GridSim.Wpf.Views;

public sealed class BranchLossRow : Notify
{
    private string _name = "";
    private string _lossMw = "-";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string LossMw { get => _lossMw; set => Set(ref _lossMw, value); }
}

public sealed class LossesVm : Notify
{
    private const int RowCount = 5;
    public ObservableCollection<BranchLossRow> Rows { get; } = new();

    private string _total = "-";
    public string TotalLossMw { get => _total; set => Set(ref _total, value); }

    private string _stamp = "";
    public string Stamp { get => _stamp; set => Set(ref _stamp, value); }

    public LossesVm() { for (int i = 0; i < RowCount; i++) Rows.Add(new BranchLossRow()); }

    public void Update(SimFrame f, string clock)
    {
        if (!f.Result.Converged) { TotalLossMw = "NOT CONVERGED"; return; }     // never present an unconverged iterate as a number
        var top = f.Result.LineFlows.OrderByDescending(l => l.PLossMw).Take(RowCount).ToList();
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].Name = i < top.Count ? (string.IsNullOrEmpty(top[i].Name) ? $"{top[i].From}-{top[i].To}" : top[i].Name) : "-";
            Rows[i].LossMw = i < top.Count ? top[i].PLossMw.ToString("F3") : "-";
        }
        TotalLossMw = f.Result.TotalLossMw.ToString("F2");
        Stamp = clock;
    }
}
```

On case9 it showed `4.64` and its first row as `8-9 2.300`. The view is a `UserControl` whose root `Grid` takes
`DataContext="{Binding Losses}"` from the dashboard view model, as `NesoRp1View.xaml` and `DomainsView.xaml` do:

```xml
<UserControl x:Class="GridSim.Wpf.Views.LossesView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="10" DataContext="{Binding Losses}">
        <StackPanel>
            <TextBlock Text="LOSSES" Style="{StaticResource PanelHeader}" />
            <TextBlock Text="{Binding TotalLossMw}" FontSize="28" />
            <ItemsControl ItemsSource="{Binding Rows}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <DockPanel>
                            <TextBlock DockPanel.Dock="Right" Text="{Binding LossMw}" />
                            <TextBlock Text="{Binding Name}" />
                        </DockPanel>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </StackPanel>
    </Grid>
</UserControl>
```

The code-behind is one line, and the namespaces are `GridSim.Wpf.Views` for the view model and the view:

```csharp
using System.Windows.Controls;

namespace GridSim.Wpf.Views;

public partial class LossesView : UserControl
{
    public LossesView() => InitializeComponent();
}
```

**Desktop: wire it** in three places. A property on the root `DashboardViewModel`
(`public Views.LossesVm Losses { get; } = new();`), a call in the `Update(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries)`
fan-out (`Losses.Update(f, Clock);`), and a `<TabItem Header="LOSSES" Style="{StaticResource RoomTabItem}"><views:LossesView /></TabItem>`
in `MainWindow.xaml`, placed before ASSETS. The tab order is NETWORK, GENERATION & INERTIA, THE MACHINE, SCADA, OPERATOR, MARKET,
NESO RP1, MECHANICAL, HUMAN SCALE, SPEED DOMAINS, INVERTER FLEET, EXPERIMENTAL, HARMONICS, CYCLE, ASSETS, ABOUT.
`Tabs_SelectionChanged`, `OpenInExplorer_Click` and `OnScadaNavigate` in `MainWindow.xaml.cs` identify the explorer by its
position at the end of the strip (`Tabs.Items.Count - 1`), so insert before ASSETS and never after ABOUT (see
[Limits](#limits)). A tab whose per-frame work is heavy gates it on an `Active` flag that `Tabs_SelectionChanged` sets from
the tab's header text, not its index (`ExperimentalVm.Active`), so inserting a tab cannot point the gate at the wrong one.
A clickable row is a `Button` styled `{StaticResource RowLinkBtn}` (a list row that is itself a link: hand cursor, row look,
lifts on hover) with its `Command` bound to a `RelayCommand` (`src/GridSim.Wpf/Views/Assets/AssetPages.cs`), as in
`InspectorView.xaml`; no view in the project uses a `MouseBinding`.
**Copy** the NESO RP1 tab (`Views/NesoRp1Vm.cs`, `Views/NesoRp1View.xaml`) for a large tab and `Views/DomainsVm.cs` with
`DomainsView.xaml` for a small one.

**Web: the pieces.** A web tab is a DTO, a pure projection, an envelope slot, a session enrichment, a component and a tab
name:

1. A record in `src/GridSim.Web/Projection/TabDtos.cs`: plain data the browser reads, with no Core types or behaviour.
2. A pure static projection of the frame in `Projection/TabProjections.cs`, unit-testable without a session.
3. A slot in `FrameEnvelope` (`Projection/Dtos.cs`): `LossesDto? Losses = null`. A null slot means the tab is not computed.
4. The enrichment in `SimSession.BuildEnvelope` (`Sessions/SimSession.cs`), the `with { ... }` initialiser that adds every
   tab's DTO to the frame on the live stream (`Losses = TabProjections.Losses(f),`). The one-shot `/solve` response enriches
   only the tabs listed in `RestApi.cs`; add yours there if it should appear in a one-shot solve.
5. A component `Components/Tabs/LossesTab.razor` with `[Parameter] public FrameEnvelope? Frame { get; set; }`, showing
   `Waiting for first frame` (the source adds an ellipsis character) while the slot is null, as `DomainsTab.razor` does.
6. The tab name in `AllTabs` in `Components/Pages/Dashboard.razor` (and in `RawTabs` if it works on the raw replay, which has
   no solve) and a `case "LOSSES":` arm in the `switch (_tab)`. A name in the strip with no arm shows the stub
   `wired in a later build step`. The web strip is matched by name, not position, so the order of `AllTabs` is free.

The DTO and projection, built against `GridSim.Web`:

```csharp
using GridSim.Core.Simulation;

namespace GridSim.Web.Projection;

public sealed record LossRowDto(string Name, double LossMw);
public sealed record LossesDto(double TotalLossMw, IReadOnlyList<LossRowDto> Rows);

public static class MyProjections
{
    public static LossesDto Losses(SimFrame f)
    {
        var rows = f.Result.LineFlows
            .OrderByDescending(l => l.PLossMw).Take(5)
            .Select(l => new LossRowDto(string.IsNullOrEmpty(l.Name) ? $"{l.From}-{l.To}" : l.Name, l.PLossMw))
            .ToList();
        return new LossesDto(f.Result.TotalLossMw, rows);
    }
}
```

**Tests to copy.** `GridSimWebProjectionTests` pins a DTO to the numbers the solver produced on case9;
`WebProjectionAntiDriftTests` pins a projection to the shared Core presenter; `WebParityScaffoldingR099Tests` pins wording
the two faces share (the speed-domain status sentence is identical word for word in `DomainsVm.Status` and
`TabProjections.DomainsStatusText`); `StabilityMetricsTests` is the pattern for a Core class behind a tab. This test passed:

```csharp
[Fact]
public void The_tab_projection_carries_the_numbers_the_solver_produced()
{
    GridModel m = IeeeCases.Case9();
    PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(m);
    var f = new SimFrame(m, r, 0, 0, 50.0, r.PInjMw.Where(p => p > 0).Sum(), m.Buses.Sum(b => b.Pd));
    LossesDto dto = MyProjections.Losses(f);
    Assert.Equal(r.TotalLossMw, dto.TotalLossMw);
    Assert.Equal(r.LineFlows.Max(l => l.PLossMw), dto.Rows[0].LossMw);
}
```

**Pitfalls.** Every tick's `FrameEnvelope` carries every tab's DTO, so keep a DTO small and put a heavy study behind an
on-demand call (the HARMONICS tab runs its study from a button, through `RunStudy`). A projection that reads the model must
read the post-solve model. A threshold shared by both faces is owned by one Core presenter; where the WPF view model keeps a
literal mirror of it, `PresentationConstantsAntiDriftTests` pins the WPF constant equal to the web `PresentationLimits`
value, or to the literal it replaced. It checks only `StatusLampRules.IdleInterconnectorMw` against Core, and the WPF copy
of that one is removed.

## Delivering a slice

A slice is one unit of work that leaves the tree green: code, tests, documentation and a CHANGELOG entry, then a commit.

1. **Code and tests.** The tests that pin the contract are in the same change. A test of a new numeric path compares with
   exact bits where the path is meant to reduce to an old one.
2. **Build clean.** `dotnet build GridSim.slnx -c Release` gives 0 warnings, and the test suite stays green:

   ```bash
   dotnet build GridSim.slnx -c Release
   dotnet test GridSim.slnx -c Release --no-build
   ```

3. **Version and CHANGELOG.** Every commit bumps the version by one. The one `<Version>` lives in `Directory.Build.props`
   (`1.95.NNN`); no `.csproj` sets its own. Add a matching heading `## [1.95.NNN]` to `CHANGELOG.md`, ascending, newest at
   the bottom, with the entry beneath it. `VersionContractTests` fails if a project overrides the version, if the CHANGELOG
   has no heading for the current version, if `BuildInfo` disagrees with the file, or if a loadable assembly carries a
   different informational version. `gridsim --version`, the web footer and the desktop About tab all show it.
4. **Documentation, per slice.** Update the technical chapter that owns the subsystem (equations as implemented, parameters
   with source symbols, the invariants with their tests, a worked example, a source map), the user manual chapter that
   uses it, and `0019 CLI reference.md` for a CLI change. Follow [STYLE.md](../developer/STYLE.md): plain ASCII, EN-GB spelling,
   present tense, no repair history in the body, real captured output, relative links, and a limitation stated once under
   `## Limits`. A chapter file is named `NNNN Chapter title.md` (the chapter number, then its title); a rename
   updates every link to it in the same change. Check the files before you commit:

   ```bash
   python tools/docs/lint.py --strict "docs/manual/0029 Extending GridSim.md"
   ```

5. **Commit and push.** A plain-English commit subject, then push straight away. A test changes only under the written
   justification rule above.

## Limits

- The set of estimation methods, measurement kinds, asset kinds and protection actions is closed: each is an enum switched
  on inside `GridSim.Core`, and `IWlsStep` and `MeasurementModel` are `internal`, so a new member is a change to the
  repository and cannot be added from another assembly.
- `AssetKind.SynchronousMachine` has no controller and an authored class of that kind is refused at load; a machine's inertia
  comes from its generator's `H` and `MBase`.
- `ProtectionAction.RestoreBranch` is reserved and refused; reclosing is the finite restore delay of an `OpenBranch` stage.
- The CIM importer, bridge and exporter are library code: no `gridsim` verb reads or writes CIM, and `convert` handles
  MATPOWER and JSON only.
- ROCm has no native batched entry point, so `SolverBackend.DenseBatch` returns the CPU batched reference for it.
- The desktop shell identifies the ASSETS tab as the last tab (`Tabs.Items.Count - 1` in `Tabs_SelectionChanged`,
  `OpenInExplorer_Click` and `OnScadaNavigate`), but ABOUT is last, so those handlers act on ABOUT.
- A word in `cycle --jobs` that matches no job key is accepted: no job runs, the report shows `jobs 0 (0 High / 0 Low)` and
  the verb exits 0.
- An unknown case name exits 2 on verbs that load through `LoadSelected` and exits 1 on verbs that catch the load themselves
  (`dcopf` among them), so the exit code for the same mistake depends on the verb.

## See also

- [Technical 1, Architecture](<../technical/0001 Architecture.md>): every seam with its implementations, and the data
  flows.
- [Technical 3, Power flow](<../technical/0003 Power flow.md>) and
  [Technical 4, Sparse and GPU numerics](<../technical/0004 Sparse and GPU numerics.md>): engines, backends and the
  tolerance table.
- [Technical 6, State estimation](<../technical/0006 State estimation.md>): the `IWlsStep` formulation and the robust
  estimators.
- [Technical 8, Provenance and invariants](<../technical/0008 Provenance and invariants.md>): the tags, the 169-hour
  guard and the determinism rules.
- [Technical 13](<../technical/0013 Asset-dynamics data grounding.md>) and
  [Technical 15](<../technical/0015 Protections.md>): the asset and protection layers.
- [Technical 10, File and wire formats](<../technical/0010 File and wire formats.md>) and
  [Technical 28, The v2 JSON schema](<../technical/0028 The v2 JSON schema.md>): importers, exporters and schemas.
- [ch. 19, CLI reference](<0019 CLI reference.md>): the flags and exit codes a new backend, method or verb plugs into.
- [ch. 20, Counterfactuals](<0020 Counterfactuals.md>): the gate your evidence capability must respect.
- [ch. 27, Validation tools](<0027 Validation tools.md>): how to prove an extension against the validation ladder.
- [ch. 28, Benchmarking and GPU](<0028 Benchmarking and GPU.md>): `bench`, `batchsolve` and the GPU preflight in use.
- [STYLE.md](../developer/STYLE.md): the documentation rules every slice follows.
