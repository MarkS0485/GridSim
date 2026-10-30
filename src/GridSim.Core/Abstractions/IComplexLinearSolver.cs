using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Abstractions;

/// <summary>
/// Solves a complex linear system <c>A x = b</c> where A is a sparse complex matrix (the natural shape of a
/// harmonic admittance solve <c>Y(h) V(h) = I(h)</c>). GridSim's fundamental power flow is real (polar-form
/// Jacobian), so no complex linear solver existed; the harmonic engine needs one, and it is built as a seam
/// with several implementations that cross-validate: a dense direct solve (small cases / oracle), a real
/// 2n-expansion over the tuned real sparse stack (scales to gb-full), and a native complex iterative solve.
/// </summary>
public interface IComplexLinearSolver
{
    /// <summary>A short label for the backend (recorded in reports).</summary>
    string Name { get; }

    /// <summary>Solve <c>A x = b</c>; returns the solution, or <c>null</c> on a singular/breakdown matrix.</summary>
    Complex[]? Solve(ComplexCsr a, Complex[] b);

    /// <summary>
    /// Factor <paramref name="a"/> once for <b>repeated right-hand sides</b> against the same matrix - the
    /// shape of harmonic state estimation (one Y(h), one impedance column per candidate source) and the
    /// coupled harmonic power flow (one Y(h) per order, iterated to a fixed point). Returns <c>null</c> when
    /// the matrix is singular. The default implementation simply re-solves per RHS (correct, no reuse); the
    /// direct solvers override it to reuse the ordering and numeric factorization, turning N factorizations
    /// into one factorization + N cheap back-substitutions.
    /// </summary>
    IComplexLinearFactorization? Factor(ComplexCsr a) => new ReSolvingComplexFactorization(this, a);
}

/// <summary>A one-off factorization of a complex matrix that can be solved against many right-hand sides
/// (see <see cref="IComplexLinearSolver.Factor"/>).</summary>
public interface IComplexLinearFactorization
{
    /// <summary>Solve <c>A x = b</c> for the factored A; <c>null</c> if the factorization was singular.</summary>
    Complex[]? Solve(Complex[] b);

    /// <summary>
    /// The same solve as <see cref="Solve"/>, written into the caller's <paramref name="x"/> instead of a new array
    /// (N060 / R115, October 2026: the sparse RMS network solves once per RK stage, once to commit and in every
    /// converter iteration). Returns false, leaving <paramref name="x"/> unspecified, exactly when <see cref="Solve"/>
    /// returns null. The values are bit-identical to <see cref="Solve"/>'s. This default copies the result of
    /// <see cref="Solve"/>, so a factorization that only implements <see cref="Solve"/> works unchanged; the real
    /// 2n-expansion factorization overrides it without allocating. <paramref name="x"/> must not be
    /// <paramref name="b"/>, and must have the solution's length (<see cref="ArgumentException"/> otherwise).
    /// </summary>
    bool SolveInto(Complex[] b, Complex[] x)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (ReferenceEquals(b, x)) throw new ArgumentException("x must not be the right-hand side array.", nameof(x));
        Complex[]? r = Solve(b);
        if (r is null) return false;
        if (x.Length != r.Length)
            throw new ArgumentException($"x holds {x.Length} entries; the system has {r.Length} unknowns.", nameof(x));
        Array.Copy(r, x, r.Length);
        return true;
    }
}

/// <summary>Fallback factorization for solvers that don't specialise: holds the matrix and re-invokes the
/// solver's one-shot <see cref="IComplexLinearSolver.Solve"/> per RHS. Correct, but with no reuse - so the
/// default never regresses a solver that has nothing to cache (e.g. the iterative BiCGSTAB).</summary>
internal sealed class ReSolvingComplexFactorization : IComplexLinearFactorization
{
    private readonly IComplexLinearSolver _solver;
    private readonly ComplexCsr _a;
    public ReSolvingComplexFactorization(IComplexLinearSolver solver, ComplexCsr a) { _solver = solver; _a = a; }
    public Complex[]? Solve(Complex[] b) => _solver.Solve(_a, b);
}
