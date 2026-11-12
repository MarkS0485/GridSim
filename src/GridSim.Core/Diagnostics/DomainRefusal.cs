namespace GridSim.Core.Diagnostics;

/// <summary>
/// GR-855 (November 2026): marks an exception as a DOMAIN refusal (the input or the numerical state cannot be processed:
/// a singular covariance, a matrix with no Cholesky factor, a window whose state layout changed), as distinct from a
/// programming fault. The mark is an entry in <see cref="Exception.Data"/>, so the exception keeps its type and message
/// and every caller that catches or asserts the type sees exactly what it did before. The real-time DSE engine counts a
/// marked <see cref="InvalidOperationException"/> as a failed window when it continues on error; an unmarked one (a
/// programming fault) fails the run loudly instead of being counted.
/// </summary>
public static class DomainRefusal
{
    /// <summary>The <see cref="Exception.Data"/> key of the mark.</summary>
    public const string DataKey = "GridSim.DomainRefusal";

    /// <summary>Mark <paramref name="ex"/> as a domain refusal and return it, for <c>throw DomainRefusal.Mark(new ...)</c>.</summary>
    public static TException Mark<TException>(TException ex) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(ex);
        ex.Data[DataKey] = true;
        return ex;
    }

    /// <summary>True when <paramref name="ex"/> carries the mark.</summary>
    public static bool IsMarked(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Data[DataKey] is true;
    }
}
