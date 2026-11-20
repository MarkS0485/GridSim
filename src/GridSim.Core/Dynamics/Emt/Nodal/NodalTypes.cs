namespace GridSim.Core.Dynamics.Emt.Nodal;

// RC4 R4-16 (EMT depth A). A NEW nodal electromagnetic-transient solver in SI units. The incumbent EmtEngine / EmtElement
// set (single-phase, per-unit, sparse LU, sealed element list) has no hook for a nonlinear element or a convolution
// line, so this is a separate solver in new files; the incumbent engine, its elements and its digests are untouched. It
// reuses only the pure companion-model functions (EmtCompanionStamp) and the IntegrationRule enum.

/// <summary>How the solver ends a run.</summary>
public enum NodalStatus
{
    /// <summary>Every requested step was taken and every Newton solve converged.</summary>
    Completed,
    /// <summary>A Newton solve of a nonlinear element did not converge within the fixed iteration limit; the run stops at that step.</summary>
    NewtonNotConverged,
    /// <summary>The nodal matrix was singular (a floating node or an unsolvable network); the run stops at that step.</summary>
    SingularMatrix,
}

/// <summary>What the solver does at a switching event. An event is applied at the first step boundary at or after the event
/// time; there is no interpolation back to the exact instant, so an event is quantised UP to the step grid (an error of
/// at most one step).</summary>
public enum NodalEventRule
{
    /// <summary>Trapezoidal throughout: the event step is a normal trapezoidal step (second-order; rings at Nyquist on a hard discontinuity).</summary>
    Trapezoidal,
    /// <summary>The event step is one full backward-Euler step (L-stable; damps the Nyquist ring), then trapezoidal again.</summary>
    BackwardEulerStep,
}

/// <summary>The fixed Newton iteration policy for nonlinear elements. Iteration count, tolerances and the number of
/// step-halvings are all fixed numbers, so the result is bit-repeatable.</summary>
public sealed record NewtonPolicy
{
    /// <summary>Maximum Newton iterations per time step (default 100).</summary>
    public int MaxIterations { get; init; } = 100;
    /// <summary>Absolute voltage-change tolerance, volts (default 1e-9).</summary>
    public double VoltageAbsTol { get; init; } = 1e-9;
    /// <summary>Relative voltage-change tolerance (default 1e-10).</summary>
    public double VoltageRelTol { get; init; } = 1e-10;
    /// <summary>Absolute residual (KCL) tolerance, amperes (default 1e-9).</summary>
    public double ResidualAbsTol { get; init; } = 1e-9;
    /// <summary>Relative residual tolerance, against the largest source or branch current at the step (default 1e-10).</summary>
    public double ResidualRelTol { get; init; } = 1e-10;
    /// <summary>Maximum halvings of a Newton step that increases the residual (default 30).</summary>
    public int MaxBacktracks { get; init; } = 30;
}

/// <summary>Solver options.</summary>
public sealed record NodalOptions
{
    /// <summary>The base integration rule.</summary>
    public IntegrationRule Rule { get; init; } = IntegrationRule.Trapezoidal;
    /// <summary>The event rule.</summary>
    public NodalEventRule EventRule { get; init; } = NodalEventRule.Trapezoidal;
    /// <summary>The Newton policy.</summary>
    public NewtonPolicy Newton { get; init; } = new();
    /// <summary>Record every this-many steps (1 = every step). The initial state at t = 0 is always recorded.</summary>
    public int SampleEvery { get; init; } = 1;
}

/// <summary>What a probe reads.</summary>
public enum NodalProbeKind
{
    /// <summary>A node voltage (volts); <c>Target</c> is the node name.</summary>
    NodeVoltage,
    /// <summary>An element current (amps); <c>Target</c> is the element name and <c>Channel</c> the element's own current index.</summary>
    ElementCurrent,
    /// <summary>An element state (for a saturable inductor, its flux linkage in Wb); <c>Target</c> the element name.</summary>
    ElementState,
}

/// <summary>One recorded channel.</summary>
public sealed record NodalProbe(string Name, NodalProbeKind Kind, string Target, int Channel = 0);

/// <summary>A scheduled switch change.</summary>
public sealed record NodalEvent(double AtSeconds, string SwitchName, bool Close);

/// <summary>Run statistics.</summary>
/// <param name="Steps">Steps completed.</param>
/// <param name="Refactorisations">LU factorisations of the LINEAR nodal matrix: once at the start, then whenever a switch
/// event, a backward-Euler event step or the return to trapezoidal changes it.</param>
/// <param name="NewtonIterations">Total Newton iterations over the run (each factorises the iteration matrix).</param>
/// <param name="MaxNewtonIterations">Largest iteration count of any one step.</param>
public readonly record struct NodalRunStats(long Steps, int Refactorisations, long NewtonIterations, int MaxNewtonIterations);

/// <summary>The result of a run. When <see cref="Status"/> is not Completed the series end at the last good step and
/// <see cref="StatusMessage"/> names the step, time and cause; the last value is never repeated to fill the run.</summary>
public sealed record NodalResult(
    double StepSeconds,
    string Method,
    NodalStatus Status,
    string StatusMessage,
    double FailedAtSeconds,
    IReadOnlyList<string> ProbeNames,
    double[] Times,
    double[][] Series,
    NodalRunStats Stats,
    NewtonPolicy Newton);
