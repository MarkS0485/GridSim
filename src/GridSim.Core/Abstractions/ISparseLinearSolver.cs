using GridSim.Core.Numerics;

namespace GridSim.Core.Abstractions;

/// <summary>
/// A pluggable sparse linear solve of <c>A x = b</c> for the sparse Newton-Raphson power flow  -
/// the sparse counterpart of <see cref="ILinearSolver"/>. A CPU direct-LU implementation ships
/// today; a GPU (ILGPU/cuSPARSE) backend drops in behind this interface without the power-flow
/// code changing. Returns the solution vector, or <c>null</c> if the matrix is
/// structurally/numerically singular (the caller then falls back to its ILU0/BiCGSTAB path).
/// </summary>
public interface ISparseLinearSolver
{
    string Name { get; }

    double[]? Solve(CsrMatrix a, double[] b);
}
