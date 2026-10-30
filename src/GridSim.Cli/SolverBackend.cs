using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Gpu;
using ILGPU.Runtime;

namespace GridSim.Cli;

/// <summary>
/// Selects the CPU or GPU linear-solver backends behind the power-flow solvers, from a runtime
/// switch: <c>--cpu</c> (default), <c>--gpu</c> (AMD-first auto-detect: ROCm, then OpenCL, then
/// CUDA), or an explicit <c>--gpu=rocm|opencl|cuda</c>. ROCm is the native AMD Instinct path
/// (hipSOLVER/rocSPARSE); OpenCL is the portable open-driver path (AMD/Intel/NVIDIA via ILGPU);
/// CUDA is NVIDIA via ILGPU. If the requested accelerator isn't present it warns and falls back to
/// CPU. The chosen backend is recorded in <see cref="RunInfo"/> so reports are comparable.
/// </summary>
public sealed class SolverBackend
{
    public enum Vendor { Cpu, Cuda, Rocm, OpenCl }

    public Vendor Device { get; }
    public string Description { get; }

    private SolverBackend(Vendor device, string description) { Device = device; Description = description; }

    /// <param name="spec">null / "cpu" -> CPU; "auto"/"" -> AMD-first auto-detect; "rocm"/"opencl"/"cuda" explicit.</param>
    /// <summary>True for the backend names <see cref="Select"/> accepts (case and surrounding space ignored).</summary>
    public static bool IsKnownSpec(string? spec) =>
        spec?.Trim().ToLowerInvariant() is null or "" or "auto" or "cpu" or "rocm" or "opencl" or "cuda";

    public static SolverBackend Select(string? spec)
    {
        spec = spec?.Trim().ToLowerInvariant();
        if (spec is null or "cpu") return new SolverBackend(Vendor.Cpu, "cpu");

        // Explicit vendor request.
        if (spec == "rocm")
            return RocmContext.IsAvailable ? Gpu(Vendor.Rocm, RocmContext.DeviceDescription) : FellBack("rocm");
        if (spec == "opencl")
            return GpuContext.IsOpenClAvailable ? Gpu(Vendor.OpenCl, GpuContext.OpenClDescription) : FellBack("opencl");
        if (spec == "cuda")
            return GpuContext.IsAvailable ? Gpu(Vendor.Cuda, GpuContext.DeviceDescription) : FellBack("cuda");

        // R222: anything other than auto / "" here is a typo, not a request for auto-detect. Program.cs refuses it
        // at parse time with exit 2; this throw is the backstop for any other caller.
        if (spec is not ("auto" or ""))
            throw new ArgumentException($"Unknown GPU backend '{spec}'. Expected one of auto, cpu, rocm, opencl, cuda.", nameof(spec));

        // Auto: AMD-first - prefer ROCm (native Instinct), then OpenCL (open drivers), then CUDA, then CPU.
        if (RocmContext.IsAvailable) return Gpu(Vendor.Rocm, RocmContext.DeviceDescription);
        if (GpuContext.IsOpenClAvailable) return Gpu(Vendor.OpenCl, GpuContext.OpenClDescription);
        if (GpuContext.IsAvailable) return Gpu(Vendor.Cuda, GpuContext.DeviceDescription);
        return FellBack("gpu");
    }

    private static SolverBackend Gpu(Vendor v, string desc) => new(v, $"gpu/{v.ToString().ToLowerInvariant()} ({desc})");

    private static SolverBackend FellBack(string requested)
    {
        Console.Error.WriteLine($"[gpu] no usable {requested} device found - falling back to the CPU backend.");
        return new SolverBackend(Vendor.Cpu, $"cpu ({requested} requested but unavailable)");
    }

    public ILinearSolver DenseLinear() => Device switch
    {
        Vendor.Rocm => new RocmDenseLinearSolver(),
        Vendor.Cuda => new GpuDenseLinearSolver(AcceleratorType.Cuda),
        Vendor.OpenCl => new GpuDenseLinearSolver(AcceleratorType.OpenCL),
        _ => new DenseLuSolver(),
    };

    public ISparseLinearSolver SparseLinear() => Device switch
    {
        Vendor.Rocm => new RocmSparseLinearSolver(),
        Vendor.Cuda => new GpuSparseLinearSolver(accel: AcceleratorType.Cuda),
        Vendor.OpenCl => new GpuSparseLinearSolver(accel: AcceleratorType.OpenCL),
        _ => new CpuSparseLuSolver(),
    };

    public IPowerFlowSolver DenseSolver() => new NewtonRaphsonPowerFlow(DenseLinear());
    public IPowerFlowSolver SparseSolver() => new SparseNewtonRaphsonPowerFlow(SparseLinear());

    /// <summary>
    /// The batched dense solver for many same-sized systems in one dispatch (e.g. a contingency sweep): the
    /// ILGPU device path on CUDA/OpenCL, else the CPU reference loop. ROCm has no batched native entry point
    /// yet, so it uses the CPU batched reference (still correct; just not device-accelerated).
    /// </summary>
    public IBatchedLinearSolver DenseBatch() => Device switch
    {
        Vendor.Cuda => new GpuBatchedDenseSolver(AcceleratorType.Cuda),
        Vendor.OpenCl => new GpuBatchedDenseSolver(AcceleratorType.OpenCL),
        _ => new CpuBatchedLuSolver(),
    };

    /// <summary>
    /// The dense / sparse threshold this backend routes by (N057, Ruling R1, Mark delegated 2026-10-04). On the CPU it is
    /// the shared power-flow rule, <see cref="SolverSelector.DefaultSparseThreshold"/> (0 since N057: every Newton solve is
    /// sparse; the CPU engines are bit-identical, so only speed and memory change). On a GPU device the dense engine is
    /// the device's dense LU, which is outside the CPU bit-identity contract and was not re-benched, so the GPU routing
    /// keeps the old measured crossover, <see cref="SolverSelector.DenseSparseCrossoverBuses"/> (200 buses), exactly as
    /// before the split.
    /// </summary>
    public int SparseThreshold => Device == Vendor.Cpu ? SolverSelector.DefaultSparseThreshold : SolverSelector.DenseSparseCrossoverBuses;

    /// <summary>The Newton engine kind this backend routes <paramref name="m"/> to (<see cref="SolverSelector.ResolveNewton"/>
    /// at <see cref="SparseThreshold"/>).</summary>
    public SolverKind ResolveNewton(GridModel m) => SolverSelector.ResolveNewton(m, SparseThreshold);

    /// <summary>A steady-state solver sized to the model by the shared rule (<see cref="SolverSelector.ResolveNewton"/>
    /// at <see cref="SparseThreshold"/>: always sparse on the CPU since N057, sparse past 200 buses on a GPU) - each on
    /// the selected CPU/GPU linear backend. Was a local 3,000 (R003).</summary>
    public SteadyStateSolver Pick(GridModel m) => Pick(ResolveNewton(m));

    /// <summary>A steady-state solver on an engine the CALLER has already resolved: <see cref="SolverKind.SparseNewton"/>
    /// gives the sparse engine, anything else the dense one, on the selected CPU/GPU linear backend. For a verb whose
    /// user forces the engine with an explicit flag (<c>gda --solve --dense|--sparse</c>); every other caller uses
    /// <see cref="Pick(GridModel)"/>, which resolves by the shared rule. Never the radial sweep.</summary>
    public SteadyStateSolver Pick(SolverKind kind) =>
        kind == SolverKind.SparseNewton
            ? new SteadyStateSolver(SparseSolver())
            : new SteadyStateSolver(DenseLinear());

    /// <summary>The engine <see cref="SolverSelector.Resolve"/> picks for <paramref name="m"/> (radial -> sweep, then the
    /// shared dense/sparse rule), on the selected backend. Every CLI site that used to inline
    /// <c>LooksRadial ? sweep : BusCount &gt; 3000 ? sparse : dense</c> routes here.</summary>
    public IPowerFlowSolver Solver(GridModel m) => SolverSelector.Resolve(m, SparseThreshold) switch
    {
        SolverKind.DistributionSweep => new BackwardForwardSweep(),
        SolverKind.SparseNewton => SparseSolver(),
        _ => DenseSolver(),
    };

    /// <summary>
    /// The engine for a traced solve export (<c>--enableExport</c>): radial -> sweep, then dense up to
    /// <see cref="SolverSelector.DenseSparseCrossoverBuses"/> (200) buses and sparse above, on the selected backend. This is
    /// the routing the export always had; N057 moved the general power-flow rule to always-sparse, but only the dense
    /// engine records the Jacobian in the trace (the export's <c>--export-jacobian</c> purpose), so the export keeps it.
    /// On the CPU the numbers are the same bits either way.
    /// </summary>
    public IPowerFlowSolver ExportSolver(GridModel m) => SolverSelector.Resolve(m, SolverSelector.DenseSparseCrossoverBuses) switch
    {
        SolverKind.DistributionSweep => new BackwardForwardSweep(),
        SolverKind.SparseNewton => SparseSolver(),
        _ => DenseSolver(),
    };

    /// <summary>The Newton engine <see cref="SolverSelector.ResolveNewton"/> picks for <paramref name="m"/> (never the sweep),
    /// on the selected backend.</summary>
    public IPowerFlowSolver NewtonSolver(GridModel m) =>
        ResolveNewton(m) == SolverKind.SparseNewton ? SparseSolver() : DenseSolver();

    /// <summary>A strictly radial (tree) network routed to the backward/forward-sweep solver.
    /// Delegates to <see cref="SolverSelector.LooksRadial"/> so the CLI and the dashboard agree.</summary>
    public static bool LooksRadial(GridModel m) => SolverSelector.LooksRadial(m);
}
