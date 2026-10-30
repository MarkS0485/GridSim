using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R038 (October 2026 review): a GPU/ROCm sparse solver returns null when its iterative solve breaks down or
/// stalls, and documents that the caller then falls back to a direct CPU factorisation - but the sparse Newton
/// engine went straight to ILU0/BiCGSTAB and never tried the exact CPU LU. A step the device cannot solve is now
/// retried on the CPU direct LU first.
/// </summary>
public class GpuFallbackTests
{
    /// <summary>A device backend that gives up on every solve (counts the calls).</summary>
    private sealed class AlwaysGivesUp : ISparseLinearSolver
    {
        public int Calls;
        public string Name => "always-gives-up (test)";
        public double[]? Solve(CsrMatrix a, double[] b) { Calls++; return null; }
    }

    [Fact]
    public void A_backend_that_always_gives_up_falls_back_to_the_exact_cpu_lu_bit_for_bit()
    {
        GridModel m = IeeeCases.Case300();
        var opt = new PowerFlowOptions { EnforceQLimits = true };
        var device = new AlwaysGivesUp();

        PowerFlowResult viaFallback = new SparseNewtonRaphsonPowerFlow(device).Solve(m, opt);
        PowerFlowResult cpu = new SparseNewtonRaphsonPowerFlow().Solve(m, opt);

        Assert.True(device.Calls > 0, "the device backend was never asked");
        Assert.True(viaFallback.Converged);
        // The fallback IS the CPU direct LU, so every step - and the answer - is the CPU path's, to the bit.
        Assert.Equal(cpu.Iterations, viaFallback.Iterations);
        for (int i = 0; i < cpu.Vm.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(cpu.Vm[i]), BitConverter.DoubleToInt64Bits(viaFallback.Vm[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(cpu.VaDeg[i]), BitConverter.DoubleToInt64Bits(viaFallback.VaDeg[i]));
        }
    }

    [Fact]
    public void The_cpu_path_is_unchanged_and_never_double_solves()
    {
        // On the CPU path the primary already is the direct LU: a null means singular, and the step goes straight
        // to the iterative fallback (no second direct attempt). Pinned through the public result: identical to the
        // dense engine, as the parity tests require.
        GridModel m = IeeeCases.Case118();
        var opt = new PowerFlowOptions { EnforceQLimits = true };
        PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m, opt);
        PowerFlowResult d = new NewtonRaphsonPowerFlow().Solve(m, opt);
        Assert.Equal(d.Iterations, s.Iterations);
        for (int i = 0; i < d.Vm.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(d.Vm[i]), BitConverter.DoubleToInt64Bits(s.Vm[i]));
    }
}
