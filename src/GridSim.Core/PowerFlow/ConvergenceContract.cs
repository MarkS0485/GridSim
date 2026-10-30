namespace GridSim.Core.PowerFlow;

/// <summary>
/// Thrown when a study is asked to start from a power-flow state that did not converge. A non-converged
/// state is not a network state: its voltages are a transient iterate (or NaN), so anything built on it - a
/// fault study, a protection assessment, a dynamic initialisation - would report numbers that mean nothing.
/// </summary>
public sealed class NonConvergedStateException : InvalidOperationException
{
    public NonConvergedStateException(string study, PowerFlowResult state)
        : base($"{study}: the power-flow state it starts from did not converge (status {state.Status}, " +
               $"max mismatch {state.MaxMismatch:E3} p.u. after {state.Iterations} iterations). A non-converged " +
               "state is not a network state, so no result can be computed from it.")
    {
        Study = study;
        Status = state.Status;
    }

    /// <summary>The study that refused the state.</summary>
    public string Study { get; }

    /// <summary>The refused state's status.</summary>
    public PowerFlowStatus Status { get; }
}

/// <summary>
/// The one convergence contract (October 2026 review, workstream 4): a study that starts from a solved state
/// calls <see cref="RequireConverged"/> first, so a non-converged state can never flow into a result reported
/// as success. Before this, several studies (fault analysis, the protection cascade) read the voltages of
/// whatever state they were handed and reported their own result as converged.
/// </summary>
public static class ConvergenceContract
{
    /// <summary>Throw <see cref="NonConvergedStateException"/> naming <paramref name="study"/> unless
    /// <paramref name="state"/> converged; returns the state for fluent use.</summary>
    public static PowerFlowResult RequireConverged(this PowerFlowResult state, string study)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Converged) throw new NonConvergedStateException(study, state);
        return state;
    }
}
