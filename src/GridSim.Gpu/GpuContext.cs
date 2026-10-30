using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;

namespace GridSim.Gpu;

/// <summary>
/// Thread-safe, lazily-initialised owner of the single ILGPU <see cref="Context"/> and the
/// per-vendor <see cref="Accelerator"/>s that ILGPU can drive: CUDA (NVIDIA) and OpenCL
/// (AMD / Intel / NVIDIA - the portable "open-driver" path). There is exactly one GPU in the
/// box and the CLI fans many solves out over <c>Parallel.ForEach</c>, so every device touch
/// (kernel launch, allocation, copy, synchronize) - on either accelerator - must be serialised
/// through the single global <see cref="Lock"/>.
///
/// Probing never throws: if a given accelerator type is absent (e.g. this laptop has no NVIDIA
/// GPU, or no FP64-capable OpenCL device), initialisation quietly marks that backend unavailable
/// and disposes anything it allocated. The dense/sparse GPU solvers then transparently fall back
/// to their CPU counterparts.
///
/// The same ILGPU kernels (<see cref="GpuDenseLinearSolver"/> / <see cref="GpuSparseLinearSolver"/>)
/// run unchanged on either accelerator - which type they use is chosen by the solver's ctor and
/// resolved here via <see cref="AcceleratorFor"/>. The historical CUDA-typed properties
/// (<see cref="IsAvailable"/>, <see cref="Accelerator"/>, <see cref="DeviceDescription"/>) are
/// preserved as thin wrappers over the CUDA entry.
/// </summary>
public static class GpuContext
{
    /// <summary>Kept for compatibility (R202). GR-042: the FP64 smoke test no longer uses it. It is now an exact comparison,
    /// (1 + 2^-40) - 1 == 2^-40, which single precision fails; the old 1.5 + 1.5 test passed in single precision too.</summary>
    public const double Fp64SmokeTolerance = 1e-12;

    // The DllImport("cuda") driver-probe below is mapped to the real driver library per OS (nvcuda.dll /
    // libcuda.so.1) by the single per-assembly resolver in RocmInterop - the default "cuda.dll"/"libcuda.so"
    // resolution never found the Windows driver and missed the versioned-only Linux one.

    /// <summary>Serialises all device access. One GPU, many CPU worker threads, both accelerators.</summary>
    public static readonly object Lock = new();

    private static readonly object InitLock = new();
    // GR-456: volatile, because it is read outside the lock in the double-checked initialisation; a plain field could
    // be read as true before the context it guards was published to this thread.
    private static volatile bool _initialised;
    private static Context? _context;

    /// <summary>Per-accelerator-type state. One entry each for the types we drive with ILGPU.</summary>
    private sealed class BackendState
    {
        public Accelerator? Accelerator;
        public string Description = "not initialised";
    }

    private static readonly Dictionary<AcceleratorType, BackendState> _backends = new()
    {
        [AcceleratorType.Cuda] = new BackendState(),
        [AcceleratorType.OpenCL] = new BackendState(),
    };

    // ---- Typed, per-accelerator accessors -------------------------------------------------

    /// <summary>True once an accelerator of <paramref name="type"/> has been successfully created.</summary>
    public static bool IsAvailableFor(AcceleratorType type)
    {
        EnsureInitialised();
        return _backends.TryGetValue(type, out BackendState? s) && s.Accelerator is not null;
    }

    /// <summary>The live accelerator of <paramref name="type"/>, or <c>null</c> when unavailable.</summary>
    public static Accelerator? AcceleratorFor(AcceleratorType type)
    {
        EnsureInitialised();
        return _backends.TryGetValue(type, out BackendState? s) ? s.Accelerator : null;
    }

    /// <summary>Device name plus an FP64 note for <paramref name="type"/> (diagnostic string).</summary>
    public static string DescriptionFor(AcceleratorType type)
    {
        EnsureInitialised();
        return _backends.TryGetValue(type, out BackendState? s) ? s.Description : $"{type}: not supported";
    }

    // ---- Back-compat CUDA-typed surface (delegates to the CUDA entry) ----------------------

    /// <summary>True once a CUDA accelerator has been successfully created.</summary>
    public static bool IsAvailable => IsAvailableFor(AcceleratorType.Cuda);

    /// <summary>The live CUDA accelerator, or <c>null</c> when unavailable.</summary>
    public static Accelerator? Accelerator => AcceleratorFor(AcceleratorType.Cuda);

    /// <summary>CUDA device name plus a note on double-precision (FP64) throughput.</summary>
    public static string DeviceDescription => DescriptionFor(AcceleratorType.Cuda);

    // ---- OpenCL convenience surface (so the CLI need not reference ILGPU types) -------------

    /// <summary>True once an FP64-capable OpenCL accelerator has been successfully created.</summary>
    public static bool IsOpenClAvailable => IsAvailableFor(AcceleratorType.OpenCL);

    /// <summary>OpenCL device name plus an FP64 note.</summary>
    public static string OpenClDescription => DescriptionFor(AcceleratorType.OpenCL);

    private static void EnsureInitialised()
    {
        if (_initialised) return;
        lock (InitLock)
        {
            if (_initialised) return;
            try
            {
                // Direct CUDA-driver probe (bypasses ILGPU) - isolates "can .NET see the GPU at all?"
                // from "did ILGPU enumerate it?". Recorded into the CUDA description for diagnostics.
                string drv = DriverProbe();

                _context = Context.CreateDefault();

                // Enumerate everything ILGPU found, so a missing device is visible, not silent.
                var devs = _context.Devices;
                string seen = !devs.Any() ? "none" : string.Join(", ", devs.Select(d => d.AcceleratorType.ToString()));

                InitCuda(seen, drv);
                InitOpenCl(seen);

                // If neither accelerator came up, drop the context - nothing will touch the device.
                if (_backends.Values.All(s => s.Accelerator is null))
                {
                    _context.Dispose();
                    _context = null;
                }
            }
            catch (Exception ex)
            {
                // Never let a static probe throw. Report unavailable and clean up.
                foreach (BackendState s in _backends.Values)
                    s.Description = $"GPU unavailable: {ex.GetType().Name}: {ex.Message}";
                SafeDispose();
            }
            finally
            {
                _initialised = true;
            }
        }
    }

    /// <summary>Bring up the CUDA accelerator (unchanged behaviour: name + FP64 heuristic + driver probe).</summary>
    private static void InitCuda(string seen, string drv)
    {
        BackendState s = _backends[AcceleratorType.Cuda];
        try
        {
            Device? device = _context!.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda);
            if (device is null)
            {
                s.Description = $"no CUDA device available (ILGPU saw: [{seen}]; driver probe: {drv})";
                return;
            }
            s.Accelerator = device.CreateAccelerator(_context);
            s.Description = $"{device.Name} [CUDA] - {Fp64Note(s.Accelerator.Name)} (driver probe: {drv})";
        }
        catch (Exception ex)
        {
            s.Description = $"CUDA unavailable: {ex.GetType().Name}: {ex.Message}";
            TryDispose(s);
        }
    }

    /// <summary>
    /// Bring up the OpenCL accelerator - the portable path that lets the existing ILGPU kernels
    /// run on AMD (incl. Instinct), Intel and NVIDIA GPUs. OpenCL FP64 (cl_khr_fp64) is optional;
    /// GridSim solves in <c>double</c>, so a device without usable FP64 must not be selected. We
    /// verify FP64 definitively with a tiny on-device smoke kernel (backend-agnostic, no reliance
    /// on capability strings) rather than trusting an advertised extension list.
    /// </summary>
    private static void InitOpenCl(string seen)
    {
        BackendState s = _backends[AcceleratorType.OpenCL];
        try
        {
            Device? device = _context!.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL);
            if (device is null)
            {
                s.Description = $"no OpenCL device available (ILGPU saw: [{seen}])";
                return;
            }

            Accelerator acc = device.CreateAccelerator(_context);
            if (!Fp64Works(acc))
            {
                s.Description = $"{device.Name} [OpenCL] rejected: no usable FP64 (cl_khr_fp64) - GridSim needs double precision";
                try { acc.Dispose(); } catch { /* ignore */ }
                return;
            }
            s.Accelerator = acc;
            s.Description = $"{device.Name} [OpenCL] - {OpenClFp64Note(device.Name)}";
        }
        catch (Exception ex)
        {
            s.Description = $"OpenCL unavailable: {ex.GetType().Name}: {ex.Message}";
            TryDispose(s);
        }
    }

    /// <summary>
    /// Definitive on-device FP64 check: run a one-thread kernel that computes (1 + 2^-40) - 1 in <c>double</c> from
    /// operands sent from the host, and read it back. Only 53-bit arithmetic returns exactly 2^-40: single precision (or
    /// a device that demotes double to float) returns 0. GR-042: the old test, 1.5 + 1.5, is exact in binary32 too, so it
    /// proved only that a double kernel compiled and ran. Any failure (no cl_khr_fp64, compile error, wrong result) marks
    /// the backend unavailable so the solvers keep their CPU fallback.
    /// </summary>
    private static bool Fp64Works(Accelerator acc)
    {
        try
        {
            lock (Lock)
            {
                double tiny = Math.ScaleB(1.0, -40);
                using var buf = acc.Allocate1D<double>(2);
                buf.CopyFromCPU(new[] { 1.0, tiny });   // operands from the host, so nothing can be folded at compile time
                var kernel = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>>(Fp64SmokeKernel);
                kernel(1, buf.View);
                acc.Synchronize();
                var host = new double[2];
                buf.CopyToCPU(host);
                return host[0] == tiny;   // exact: 2^-40 survives (1 + 2^-40) - 1 only in 53-bit arithmetic
            }
        }
        catch
        {
            return false;
        }
    }

    private static void Fp64SmokeKernel(Index1D index, ArrayView<double> v)
    {
        double one = v[0], tiny = v[1];
        v[0] = (one + tiny) - one;   // exactly tiny iff FP64 arithmetic actually executed on-device
    }

    /// <summary>
    /// Best-effort note on CUDA FP64 speed. Consumer GeForce parts throttle double precision
    /// heavily (commonly 1/32-1/64 of FP32); data-centre parts (Tesla/A100/H100) run it fast.
    /// We can only report a heuristic without launching a benchmark.
    /// </summary>
    private static string Fp64Note(string? name)
    {
        name ??= string.Empty;
        bool dataCentre =
            name.Contains("A100", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("H100", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("V100", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Tesla", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("GH200", StringComparison.OrdinalIgnoreCase);
        return dataCentre
            ? "FP64 is fast (data-centre GPU)"
            : "FP64 is likely rate-limited (consumer GPU)";
    }

    /// <summary>
    /// Best-effort note on OpenCL FP64 speed, by device name. AMD Instinct (MI-series) runs FP64
    /// at near-full rate; consumer AMD/Intel parts are typically rate-limited.
    /// </summary>
    private static string OpenClFp64Note(string? name)
    {
        name ??= string.Empty;
        bool fast =
            name.Contains("Instinct", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI50", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI100", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI200", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI210", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI250", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI300", StringComparison.OrdinalIgnoreCase);
        return fast
            ? "FP64 near full rate (AMD Instinct / data-centre GPU)"
            : "FP64 may be rate-limited (consumer GPU)";
    }

    // Direct CUDA driver-API P/Invokes (libcuda.so on Linux / nvcuda.dll on Windows). Used only for
    // the diagnostic probe - proves whether the driver is reachable from .NET independent of ILGPU.
    [DllImport("cuda", EntryPoint = "cuInit")] private static extern int cuInit(uint flags);
    [DllImport("cuda", EntryPoint = "cuDeviceGetCount")] private static extern int cuDeviceGetCount(out int count);
    [DllImport("cuda", EntryPoint = "cuDriverGetVersion")] private static extern int cuDriverGetVersion(out int version);

    private static string DriverProbe()
    {
        // Ensure the per-assembly DllImport resolver (which maps "cuda" -> nvcuda.dll/libcuda.so.1) is
        // registered before the first cuda P/Invoke, even on an NVIDIA box where RocmInterop is otherwise
        // never touched.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RocmInterop).TypeHandle);
        try
        {
            int init = cuInit(0);
            if (init != 0) return $"cuInit rc={init}";
            int vrc = cuDriverGetVersion(out int ver);
            int cnt = -1, ccrc = cuDeviceGetCount(out cnt);
            return $"cuInit=0 driverVer={ver} (rc={vrc}) cuDeviceGetCount rc={ccrc} count={cnt}";
        }
        catch (DllNotFoundException) { return "libcuda not found by .NET"; }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    private static void TryDispose(BackendState s)
    {
        try { s.Accelerator?.Dispose(); } catch { /* ignore */ }
        s.Accelerator = null;
    }

    private static void SafeDispose()
    {
        foreach (BackendState s in _backends.Values) TryDispose(s);
        try { _context?.Dispose(); } catch { /* ignore */ }
        _context = null;
    }

    /// <summary>Tear down every accelerator and the context. Safe to call more than once.</summary>
    public static void Shutdown()
    {
        lock (InitLock)
        // Also take the device lock every solve holds while it uses an accelerator, so disposal waits for an in-flight
        // solve instead of pulling the accelerator out from under it. Lock order InitLock then Lock; no solve path takes
        // InitLock while holding Lock, so the two cannot deadlock.
        lock (Lock)
        {
            SafeDispose();
            foreach (BackendState s in _backends.Values) s.Description = "shut down";
            _initialised = true; // stay down; do not re-probe after an explicit shutdown
        }
    }

    /// <summary>Alias for <see cref="Shutdown"/>.</summary>
    public static void Dispose() => Shutdown();
}
