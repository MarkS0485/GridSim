namespace GridSim.Core.Dynamics.UserModels;

/// <summary>One refusal in a declarative model document: a stable code, the object it names, and the words.</summary>
/// <param name="Code">Short stable code, for example <c>unknown-block</c>, <c>algebraic-loop</c>, <c>unit-mismatch</c>.</param>
/// <param name="Object">The document object the refusal names (a block id, parameter name, port name or JSON path).</param>
/// <param name="Message">What is wrong and how to fix it.</param>
public sealed record ModelIssue(string Code, string Object, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"[{Code}] {Object}: {Message}";
}

/// <summary>
/// A declarative model document was refused (R4-08). Carries every issue found, in a fixed order (document order),
/// so a refusal names each offending object rather than the first one only. A model that needs something outside the
/// closed vocabulary is refused, never approximated.
/// </summary>
public sealed class UserModelException : Exception
{
    /// <summary>Every refusal, in document order.</summary>
    public IReadOnlyList<ModelIssue> Issues { get; }

    /// <summary>Build from one or more issues.</summary>
    public UserModelException(IReadOnlyList<ModelIssue> issues)
        : base(string.Join("; ", issues.Select(i => i.ToString())))
    {
        Issues = issues;
    }

    /// <summary>Build from a single issue.</summary>
    public UserModelException(string code, string obj, string message)
        : this(new[] { new ModelIssue(code, obj, message) })
    {
    }
}
