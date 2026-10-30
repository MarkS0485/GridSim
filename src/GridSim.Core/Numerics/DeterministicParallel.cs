using System.Runtime.ExceptionServices;

namespace GridSim.Core.Numerics;

/// <summary>
/// A parallel loop over independent items whose failure is reported exactly as the serial loop would report it.
/// <c>Parallel.For</c> wraps whatever the workers throw in an <see cref="AggregateException"/>, holding one or several
/// inner exceptions in the order the workers happened to fail, so a caller that catches the specific exception type (a
/// refused input, a singular network) missed it, and which of several errors surfaced depended on scheduling. Here each
/// item's exception is kept by its index, and after the loop the one for the LOWEST failing index is rethrown as itself
/// (with its original stack trace): the same exception, of the same type, whether the items ran in parallel or not.
/// </summary>
public static class DeterministicParallel
{
    /// <summary>Run <paramref name="body"/> for every index in [0, <paramref name="count"/>) in parallel; rethrow the
    /// exception of the lowest failing index unwrapped, if any failed.</summary>
    public static void For(int count, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var errors = new Exception?[count];
        Parallel.For(0, count, i =>
        {
            try { body(i); }
            catch (Exception ex) { errors[i] = ex; }
        });
        foreach (Exception? ex in errors)
            if (ex is not null) ExceptionDispatchInfo.Capture(ex).Throw();
    }
}
