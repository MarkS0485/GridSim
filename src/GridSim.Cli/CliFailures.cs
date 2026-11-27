namespace GridSim.Cli;

/// <summary>
/// The shared handler's reading of an unhandled exception (CLI-02, R3-06, R3-08, R4-02, R4-09): which failures are the
/// environment or a bad input (exit 1 with the message) rather than a defect (a crash log). Pure, so tests drive it
/// directly.
/// </summary>
internal static class CliFailures
{
    /// <summary>The kind of an input failure; <see cref="None"/> is a defect (or not this handler's).</summary>
    internal enum Kind
    {
        /// <summary>Not a file or JSON-reading failure.</summary>
        None,
        /// <summary>A file that could not be read or written (IOException, UnauthorizedAccessException).</summary>
        File,
        /// <summary>A JSON input that could not be read.</summary>
        Json,
        /// <summary>An aggregate of both kinds (R4-09).</summary>
        Mixed,
    }

    /// <summary>R4-02: a JsonException thrown while READING carries the reader's position (<see cref="System.Text.Json.JsonException.LineNumber"/>;
    /// the serializer adds it to a converter's exception too). One thrown while writing (an object cycle, the maximum
    /// depth) carries none, and is a defect in a result writer, not a bad input.</summary>
    internal static bool IsReaderJson(Exception x) => x is System.Text.Json.JsonException j && j.LineNumber is not null;

    private static bool IsFile(Exception x) => x is IOException or UnauthorizedAccessException;

    /// <summary>The kind of <paramref name="o"/> and the exception whose message is reported. An aggregate counts only when
    /// every exception in it (flattened) is a file failure or a reader JsonException; one defect beside them leaves the
    /// whole aggregate a defect.</summary>
    internal static (Kind Kind, Exception? Report) Classify(object? o)
    {
        if (o is AggregateException agg)
        {
            IReadOnlyList<Exception> inner = agg.Flatten().InnerExceptions;
            if (inner.Count == 0) return (Kind.None, null);
            bool anyFile = false, anyJson = false;
            foreach (Exception x in inner)
            {
                if (IsFile(x)) anyFile = true;
                else if (IsReaderJson(x)) anyJson = true;
                else return (Kind.None, null);
            }
            return (anyFile && anyJson ? Kind.Mixed : anyFile ? Kind.File : Kind.Json, inner[0]);
        }
        if (o is Exception e && IsFile(e)) return (Kind.File, e);
        if (o is Exception j && IsReaderJson(j)) return (Kind.Json, j);
        return (Kind.None, null);
    }
}
