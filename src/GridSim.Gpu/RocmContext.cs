namespace GridSim.Gpu;

/// <summary>
/// Thread-safe, lazily-initialised availability probe for the AMD ROCm/HIP backend - the
/// ROCm counterpart of <see cref="GpuContext"/>. There is exactly one GPU in the box and
/// the CLI fans many solves out over <c>Parallel.ForEach</c>, so every ROCm device touch
/// (allocation, memcpy, hipSOLVER/rocSPARSE call, synchronize) must be serialised through
/// <see cref="Lock"/>.
///
/// Probing NEVER throws. It makes one cheap HIP runtime call (<c>hipGetDeviceCount</c>)
/// inside a broad try/catch. On a machine with no ROCm libraries installed (this Windows
/// laptop) the very first P/Invoke throws <see cref="DllNotFoundException"/> /
/// <see cref="EntryPointNotFoundException"/>; we swallow it and report the backend
/// unavailable, so the dense/sparse ROCm solvers transparently fall back to their CPU
/// counterparts. Because the probe short-circuits, none of the native libs are ever loaded
/// on this box - which is exactly why the assembly builds and runs here without ROCm.
/// </summary>
public static class RocmContext
{
    /// <summary>Serialises all ROCm device access. One GPU, many CPU worker threads.</summary>
    public static readonly object Lock = new();

    /// <summary>Device index this backend binds to (default 0). One physical accelerator per box.</summary>
    private const int DeviceIndex = 0;

    private static readonly object InitLock = new();
    // GR-456: volatile, because it is read outside the lock in the double-checked initialisation; a plain field could
    // be read as true before the context it guards was published to this thread.
    private static volatile bool _initialised;
    private static bool _available;
    private static int _deviceCount;
    private static string _description = "not initialised";

    /// <summary>True once at least one HIP device has been detected.</summary>
    public static bool IsAvailable
    {
        get { EnsureInitialised(); return _available; }
    }

    /// <summary>Number of HIP devices reported, or 0 when unavailable.</summary>
    public static int DeviceCount
    {
        get { EnsureInitialised(); return _deviceCount; }
    }

    /// <summary>Human-readable description of the ROCm backend state.</summary>
    public static string DeviceDescription
    {
        get { EnsureInitialised(); return _description; }
    }

    private static void EnsureInitialised()
    {
        if (_initialised) return;
        lock (InitLock)
        {
            if (_initialised) return;
            try
            {
                // First-ever P/Invoke into amdhip64. If the library (or the entry point) is
                // absent, the CLR throws here and we drop to the catch - never propagating.
                int status = RocmInterop.hipGetDeviceCount(out int count);

                if (status == RocmInterop.HipSuccess && count > 0)
                {
                    _deviceCount = count;
                    _available = true;

                    // Bind subsequent runtime calls to a specific device so a multi-GPU MI box is
                    // addressable and every allocation/kernel targets the same device (the CUDA
                    // path does this implicitly). Default index 0.
                    int setStatus = RocmInterop.hipSetDevice(DeviceIndex);

                    // Real device name via the stable, struct-free hipDeviceGetName - deliberately
                    // avoids the large, version-sensitive hipDeviceProp_t struct. FP64 throughput is
                    // inferred from the name: AMD Instinct (MI-series) runs FP64 near full rate.
                    string name = DeviceName(DeviceIndex);
                    string setNote = setStatus == RocmInterop.HipSuccess ? "" : $" (hipSetDevice status={setStatus})";
                    _description =
                        $"AMD ROCm/HIP: {name} (device {DeviceIndex}/{count}) [HIP] - {Fp64Note(name)}{setNote}";

                    // The ROCm solvers additionally P/Invoke hipsolver/rocsparse/rocblas; a runtime-only ROCm
                    // install (HIP present, math libs absent) would otherwise pass this probe and then throw
                    // DllNotFoundException on the first solver call. Probe the WHOLE stack so IsAvailable is
                    // honest and callers fall back to CPU cleanly instead of crashing the solve.
                    // GR-043: by the P/Invokes' own route (the versioned-name resolver, then the default search).
                    foreach (string lib in new[] { RocmInterop.HipSolverLib, RocmInterop.RocSparseLib, RocmInterop.RocBlasLib })
                        if (!RocmInterop.TryLoadLikePInvoke(lib))
                        {
                            _available = false;
                            _description = $"ROCm HIP present but '{lib}' library missing; using CPU ({name})";
                            break;
                        }
                }
                else
                {
                    // Library present but no usable device (or a non-success status).
                    _available = false;
                    _description = $"no HIP device available (hipGetDeviceCount status={status}, count={count})";
                }
            }
            catch (DllNotFoundException)
            {
                // Expected on this Windows laptop: no libamdhip64 / amdhip64.dll present.
                _available = false;
                _description = "ROCm unavailable: HIP runtime library (amdhip64) not found";
            }
            catch (EntryPointNotFoundException)
            {
                _available = false;
                _description = "ROCm unavailable: hipGetDeviceCount entry point missing (unexpected ROCm version)";
            }
            catch (Exception ex)
            {
                // Never let a static probe throw. Report unavailable with the reason.
                _available = false;
                _description = $"ROCm unavailable: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                _initialised = true;
            }
        }
    }

    /// <summary>Read the device's marketing name via the struct-free hipDeviceGetName (best-effort).</summary>
    private static string DeviceName(int device)
    {
        try
        {
            var buf = new byte[256];
            int st = RocmInterop.hipDeviceGetName(buf, buf.Length, device);
            if (st != RocmInterop.HipSuccess) return "unknown device";
            int len = Array.IndexOf(buf, (byte)0);
            if (len < 0) len = buf.Length;
            return len > 0 ? System.Text.Encoding.ASCII.GetString(buf, 0, len) : "unnamed device";
        }
        catch
        {
            return "unknown device";
        }
    }

    /// <summary>
    /// Heuristic FP64 note by device name. AMD Instinct (MI-series, gfx9 CDNA) runs FP64 at
    /// near-full rate - the whole point of preferring AMD for double-precision power-flow; other
    /// parts are device-dependent. A note only; it launches no benchmark.
    /// </summary>
    private static string Fp64Note(string name)
    {
        bool instinct =
            name.Contains("Instinct", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI50", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI100", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI200", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI210", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI250", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MI300", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("gfx90a", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("gfx94", StringComparison.OrdinalIgnoreCase);
        return instinct
            ? "FP64 near full rate (AMD Instinct MI-series)"
            : "FP64 rate is device-dependent";
    }
}
