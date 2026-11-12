using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace GridSim.Core.Qsts;

/// <summary>
/// Bug #193 (2026-11-01): the list a study's Validate method collects its refusal messages in, formatting every
/// interpolated number with the INVARIANT culture. The messages used to be built with plain string interpolation, which
/// formats doubles in the current culture, so the same refused study said "got 0,5" on a de-DE machine and "got 0.5"
/// elsewhere: the same run wrote different bytes per machine. An interpolated message (<c>p.Add($"... {x} ...")</c>)
/// binds to the handler overload below, which formats with <see cref="CultureInfo.InvariantCulture"/>; a plain string
/// literal binds to <see cref="Add(string)"/>. Call sites read exactly as they did with a <c>List&lt;string&gt;</c>.
/// </summary>
internal sealed class InvariantProblemList : IEnumerable<string>
{
    private readonly List<string> _items = new();

    /// <summary>How many problems were found.</summary>
    public int Count => _items.Count;

    /// <summary>Add a message with nothing to format.</summary>
    public void Add(string message) => _items.Add(message);

    /// <summary>Add an interpolated message, every hole formatted with the invariant culture.</summary>
    public void Add(ref InvariantMessageHandler message) => _items.Add(message.ToStringAndClear());

    /// <inheritdoc/>
    public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Bug #193: the interpolated-string handler behind <see cref="InvariantProblemList.Add(ref InvariantMessageHandler)"/>:
/// the standard handler, fixed to the invariant culture.</summary>
[InterpolatedStringHandler]
internal ref struct InvariantMessageHandler
{
    private DefaultInterpolatedStringHandler _inner;

    public InvariantMessageHandler(int literalLength, int formattedCount)
    {
        _inner = new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture);
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);

    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);

    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

    public string ToStringAndClear() => _inner.ToStringAndClear();
}
