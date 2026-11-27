namespace GridSim.Core.Analysis.Ras;

/// <summary>Where a contingency ends after its remedial action schemes (RC5 WP26).</summary>
public enum RasStatus
{
    /// <summary>The final state solves and adds no violation of its own (base-case violations are netted out).</summary>
    SecureAfterRas,
    /// <summary>The final state solves and still has violations the base case did not have.</summary>
    ViolationsAfterRas,
    /// <summary>The final state has no converged power flow.</summary>
    NotConvergedAfterRas,
    /// <summary>The level limit was reached while schemes would still operate.</summary>
    RasLoop,
}

/// <summary>The words for a <see cref="RasStatus"/>.</summary>
public static class RasStatusText
{
    /// <summary>SECURE AFTER RAS, VIOLATIONS AFTER RAS, NOT CONVERGED AFTER RAS or RAS LOOP.</summary>
    public static string Of(RasStatus status) => status switch
    {
        RasStatus.SecureAfterRas => "SECURE AFTER RAS",
        RasStatus.ViolationsAfterRas => "VIOLATIONS AFTER RAS",
        RasStatus.NotConvergedAfterRas => "NOT CONVERGED AFTER RAS",
        RasStatus.RasLoop => "RAS LOOP",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "unknown RAS status"),
    };

    /// <summary>The file spelling of an action kind (tripGenerator, ...).</summary>
    public static string Of(RasActionKind kind) => kind switch
    {
        RasActionKind.TripGenerator => "tripGenerator",
        RasActionKind.TripBranch => "tripBranch",
        RasActionKind.CloseBranch => "closeBranch",
        RasActionKind.ShedLoad => "shedLoad",
        RasActionKind.SetGeneratorP => "setGeneratorP",
        RasActionKind.SwitchShunt => "switchShunt",
        RasActionKind.SetTap => "setTap",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown RAS action kind"),
    };
}

/// <summary>
/// One action taken (or skipped) by a scheme.
/// </summary>
/// <param name="Level">The level it was taken at (1-based).</param>
/// <param name="Scheme">The scheme's name.</param>
/// <param name="Action">The action's place in the scheme (1-based).</param>
/// <param name="Kind">What it does.</param>
/// <param name="Element">The element's key as written in the file.</param>
/// <param name="Applied">False when it was skipped (ACTION SKIPPED); <paramref name="Detail"/> says why.</param>
/// <param name="Detail">What was done, or why it was skipped, in words.</param>
public sealed record RasActionRecord(int Level, string Scheme, int Action, RasActionKind Kind, string Element, bool Applied, string Detail);

/// <summary>The schemes that operated at one level, in the order they were applied.</summary>
public sealed record RasLevelRecord(int Level, IReadOnlyList<string> Schemes);

/// <summary>
/// One contingency with its remedial action schemes.
/// </summary>
/// <param name="PreRas">The post-contingency state before any scheme, netted against the base case: exactly the row the
/// contingency screen gives without schemes.</param>
/// <param name="PostRas">The final state after the schemes, netted against the base case. The same object as
/// <paramref name="PreRas"/> when no action was applied.</param>
/// <param name="Armed">The schemes armed for this contingency, in file order.</param>
/// <param name="Levels">The levels at which schemes operated.</param>
/// <param name="Actions">Every action taken or skipped, in order.</param>
/// <param name="Status">Where the contingency ends.</param>
/// <param name="Note">Why no scheme was evaluated, when none could be; null otherwise.</param>
public sealed record RasContingencyResult(ContingencyResult PreRas, ContingencyResult PostRas, IReadOnlyList<string> Armed,
    IReadOnlyList<RasLevelRecord> Levels, IReadOnlyList<RasActionRecord> Actions, RasStatus Status, string? Note = null)
{
    /// <summary>The last level at which a scheme operated; 0 when none did.</summary>
    public int LastLevel => Levels.Count == 0 ? 0 : Levels[^1].Level;

    /// <summary>Every scheme operation, level by level, in the order applied.</summary>
    public IReadOnlyList<string> Fired => Levels.SelectMany(l => l.Schemes).ToList();
}

/// <summary>
/// A contingency screen with remedial action schemes: the base case and one result per contingency.
/// </summary>
/// <param name="BaseCase">The base case (no outage), solved with the screen's solver and options.</param>
/// <param name="Results">One per contingency, in the screen's order.</param>
public sealed record RasScreenReport(ContingencyResult BaseCase, IReadOnlyList<RasContingencyResult> Results)
{
    /// <summary>For an N-k screen: the single outages left out of the pairing (as <see cref="NkScreenReport"/>).</summary>
    public IReadOnlyList<ContingencyResult> ExcludedSingleOutages { get; init; } = Array.Empty<ContingencyResult>();

    /// <summary>For an N-k screen: why no combination was screened (as <see cref="NkScreenReport.Note"/>).</summary>
    public string? Note { get; init; }
}
