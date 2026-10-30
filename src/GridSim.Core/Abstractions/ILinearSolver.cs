namespace GridSim.Core.Abstractions;

/// <summary>
/// Solves the dense real linear system Ax = b. This is the seam the whole
/// simulator pivots on: every Newton-Raphson iteration is one solve here, and it's
/// where the heavy compute lives. A CPU implementation ships now; a CUDA/ILGPU
/// backend can drop in behind this interface later without the power-flow code
/// changing a line  -  the same "clean abstraction, swappable backend" pattern as
/// OpenMPPT's device drivers.
/// </summary>
public interface ILinearSolver
{
    string Name { get; }

    /// <summary>
    /// Solve Ax = b writing the solution into <paramref name="b"/>. May overwrite
    /// <paramref name="a"/> too. This is the allocation-free path the Newton-Raphson
    /// inner loop uses  -  it hands over reusable buffers it's about to rebuild anyway.
    /// </summary>
    void SolveInPlace(double[,] a, double[] b);
}

/// <summary>Convenience helpers over <see cref="ILinearSolver"/>.</summary>
public static class LinearSolverExtensions
{
    /// <summary>
    /// Non-mutating solve: returns x for Ax = b, leaving the caller's arrays
    /// untouched. Allocates copies, so prefer <see cref="ILinearSolver.SolveInPlace"/>
    /// in hot loops.
    /// </summary>
    public static double[] Solve(this ILinearSolver solver, double[,] a, double[] b)
    {
        var aa = (double[,])a.Clone();
        var bb = (double[])b.Clone();
        solver.SolveInPlace(aa, bb);
        return bb;
    }
}
