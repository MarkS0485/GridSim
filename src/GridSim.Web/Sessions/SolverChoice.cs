using GridSim.Core.PowerFlow;

namespace GridSim.Web.Sessions;

/// <summary>
/// N063: the web solver switch. A live in-process session's power-flow engine is chosen when the session is created
/// (the same <see cref="SolverKind"/> the desktop SOLVER picker and the CLI <c>--solver</c> family choose from); the
/// dashboard header offers the choice and recreates the session on change, and a <c>/ws/sim</c> <c>start</c> frame
/// may name one. This class is the one place the wire / query-string names are mapped, so the two surfaces cannot
/// disagree.
/// </summary>
public static class SolverChoice
{
    /// <summary>The names accepted, in the order the picker lists them, with the engine each selects.</summary>
    public static readonly IReadOnlyList<(string Name, SolverKind Kind, string Label)> Options = new[]
    {
        ("auto", SolverKind.Auto, "Auto"),
        ("dense", SolverKind.DenseNewton, "Dense NR"),
        ("sparse", SolverKind.SparseNewton, "Sparse NR"),
        ("sweep", SolverKind.DistributionSweep, "Radial sweep"),
    };

    /// <summary>
    /// Map a name to its engine. Null, empty or whitespace means <see cref="SolverKind.Auto"/> (the behaviour before
    /// the switch existed). Case-insensitive. Fail loud: any other text throws, naming the accepted values, rather
    /// than silently running Auto under a name the user did not mean.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not one of <see cref="Options"/>.</exception>
    public static SolverKind Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return SolverKind.Auto;
        string n = name.Trim();
        foreach ((string Name, SolverKind Kind, string _) o in Options)
            if (string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase)) return o.Kind;
        throw new ArgumentException(
            $"Unknown solver '{n}'. Use one of: {string.Join(", ", Options.Select(o => o.Name))}.", nameof(name));
    }

    /// <summary>The wire / query-string name of an engine ("auto", "dense", "sparse", "sweep").</summary>
    public static string NameOf(SolverKind kind)
    {
        foreach ((string Name, SolverKind Kind, string _) o in Options)
            if (o.Kind == kind) return o.Name;
        throw new ArgumentOutOfRangeException(nameof(kind), kind, "No web name for this solver kind.");
    }
}
