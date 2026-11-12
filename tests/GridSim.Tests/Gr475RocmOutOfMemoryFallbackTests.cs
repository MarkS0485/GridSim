using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-475: inside the ROCm sparse solve, running out of device memory (hipErrorOutOfMemory, or the rocSPARSE / rocBLAS
/// memory-error status) is the one device fault that falls back to the CPU sparse LU, counted and written to stderr.
/// Every other status still throws a plain InvalidOperationException and ends the solve. The status classification and
/// the fallback record are tested here; the device path itself needs an AMD card (NEEDS-BIGBOX / device run).
/// </summary>
public class Gr475RocmOutOfMemoryFallbackTests
{
    [Fact]
    public void Gr475_the_three_allocation_statuses_raise_the_out_of_memory_fault()
    {
        Assert.Equal(2, RocmInterop.HipErrorOutOfMemory);
        Assert.Equal(5, RocmInterop.RocSparseStatusMemoryError);
        Assert.Equal(5, RocmInterop.RocBlasStatusMemoryError);

        Assert.Throws<RocmOutOfMemoryException>(() => RocmSparseLinearSolver.CheckHip(RocmInterop.HipErrorOutOfMemory, "hipMalloc(x)"));
        Assert.Throws<RocmOutOfMemoryException>(() => RocmSparseLinearSolver.Check(RocmInterop.RocSparseStatusMemoryError, "rocsparse_dcsrmv_analysis"));
        Assert.Throws<RocmOutOfMemoryException>(() => RocmSparseLinearSolver.CheckBlas(RocmInterop.RocBlasStatusMemoryError, "rocblas_create_handle"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(-1)]
    public void Gr475_every_other_failure_status_stays_a_loud_device_fault(int status)
    {
        // Assert.Throws checks the exact type: a plain InvalidOperationException, not the fallback fault.
        if (status != RocmInterop.HipErrorOutOfMemory)
            Assert.Throws<InvalidOperationException>(() => RocmSparseLinearSolver.CheckHip(status, "hipMemcpy"));
        if (status != RocmInterop.RocSparseStatusMemoryError)
            Assert.Throws<InvalidOperationException>(() => RocmSparseLinearSolver.Check(status, "rocsparse_dcsrmv"));
        if (status != RocmInterop.RocBlasStatusMemoryError)
            Assert.Throws<InvalidOperationException>(() => RocmSparseLinearSolver.CheckBlas(status, "rocblas_ddot"));
    }

    [Fact]
    public void Gr475_success_throws_nothing()
    {
        RocmSparseLinearSolver.CheckHip(RocmInterop.HipSuccess, "hipMalloc(x)");
        RocmSparseLinearSolver.Check(RocmInterop.RocSparseStatusSuccess, "rocsparse_dcsrmv");
        RocmSparseLinearSolver.CheckBlas(RocmInterop.RocBlasStatusSuccess, "rocblas_ddot");
    }

    [Fact]
    public void Gr475_an_out_of_memory_fallback_is_counted_kept_and_written_to_stderr()
    {
        var fault = new RocmOutOfMemoryException("ROCm sparse solve: hipMalloc(val) failed (hipError=2).");
        long before = RocmSparseLinearSolver.OutOfMemoryFallbacks;
        var err = new StringWriter();
        RocmSparseLinearSolver.RecordOutOfMemoryFallback(fault, err);   // the solve passes null: Console.Error

        // Other tests in the process cannot raise this counter without an AMD device, but allow for it: at least one more.
        Assert.True(RocmSparseLinearSolver.OutOfMemoryFallbacks >= before + 1);
        Assert.Equal(fault.Message, RocmSparseLinearSolver.LastOutOfMemoryFallback);
        Assert.Contains("hipMalloc(val) failed (hipError=2)", err.ToString(), StringComparison.Ordinal);
        Assert.Contains("Solved on the CPU", err.ToString(), StringComparison.Ordinal);
    }
}
