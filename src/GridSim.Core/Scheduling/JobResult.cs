namespace GridSim.Core.Scheduling;

/// <summary>How a job ended this cycle.</summary>
public enum JobOutcome
{
    /// <summary>Ran to completion; <see cref="JobResult.Payload"/> carries its output.</summary>
    Completed,
    /// <summary>Started but hit the deadline and cooperatively aborted (Low tier only).</summary>
    Cancelled,
    /// <summary>Never started this cycle - budget/deadline was spent first. Rotated to a later cycle.</summary>
    Deferred,
    /// <summary>Threw. <see cref="JobResult.Error"/> carries the message; the cycle continues.</summary>
    Faulted,
}

/// <summary>The outcome of one job in one cycle, plus its measured cost (for telemetry and the cost model).</summary>
public sealed record JobResult(
    string Id,
    string Name,
    JobPriority Priority,
    JobOutcome Outcome,
    double CoreMs,
    object? Payload,
    bool FromStaleFrame,
    string? Error = null)
{
    /// <summary>Convenience for consumers: the payload as <typeparamref name="T"/>, or null if absent/mismatched.</summary>
    public T? PayloadAs<T>() where T : class => Payload as T;
}
