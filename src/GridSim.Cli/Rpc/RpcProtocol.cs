namespace GridSim.Cli;

/// <summary>
/// RC5 WP11: the protocol levels of <c>gridsim rpc</c>. Level 1 (<c>gridsim-rpc/1</c>, <see cref="RpcCommand.Protocol"/>) is
/// the default and is byte for byte what it was. Level 2 (<c>gridsim rpc --protocol 2</c>, <c>gridsim-rpc/2</c>) answers every
/// level 1 operation as level 1 does apart from the protocol label, the handle (<c>case-&lt;host&gt;-N</c>, where
/// <c>&lt;host&gt;</c> is a random token of the host instance) and the <c>host</c> field of hello, and adds the object model
/// operations (<see cref="OperationsV2"/>, served by RpcObjectOps.cs).
/// </summary>
internal static class RpcProtocol
{
    /// <summary>The protocol label of level 2.</summary>
    public const string Label2 = "gridsim-rpc/2";

    /// <summary>The operations level 2 adds, in the order hello lists them after the level 1 operations. A separate array, so
    /// the pinned level 1 <c>Operations</c> text is untouched. RC5 WP15: <c>add</c> {handle, kind, key, values} and
    /// <c>delete</c> {handle, kind, key, cascade?} add and delete elements; hello lists them last.</summary>
    internal static readonly string[] OperationsV2 = ["elements", "get", "set", "edits", "study", "save", "add", "delete"];

    /// <summary>The verbs the level 2 <c>study</c> operation runs on a held (and possibly edited) case. <c>dynamics-study</c>
    /// names its RAW and DYR files by SHA-256 in its own study file, so it is reached through <c>save</c> and <c>run-verb</c>.</summary>
    internal static readonly string[] StudyVerbs = ["pf", "nminus1", "nminus2", "iec60909", "fault", "acopf-full", "dcopf", "scopf", "stability"];

    /// <summary>The formats the level 2 <c>save</c> operation writes.</summary>
    internal static readonly string[] SaveFormats = ["raw33", "raw34", "raw35", "v2", "raw36", "rawx"];   // raw36 and rawx: RC5 WP34, appended

    /// <summary>The branch attributes level 2 <c>get</c> reads from the pf solve of the held case as it stands (read only, on
    /// request only: <c>elements</c> does not list them).</summary>
    internal static readonly string[] FlowAttributes = ["p_from_mw", "q_from_mvar", "p_to_mw", "q_to_mvar", "loading_pct"];

    /// <summary>RC5 REVIEW-FIX-6: the generator attributes level 2 <c>get</c> reads from the pf solve of the held case as it
    /// stands (read only, on request only): the unit's solved P and Q (<c>SolvedGeneration.UnitOutput</c>), which psspy's
    /// <c>macdat</c> 'P'/'Q' and <c>amachreal</c> 'PGEN'/'QGEN' return.</summary>
    internal static readonly string[] GeneratorSolvedAttributes = ["p_solved_mw", "q_solved_mvar"];

    /// <summary>The label a session of <paramref name="level"/> carries.</summary>
    public static string Label(int level) => level == 2 ? Label2 : RpcCommand.Protocol;
}
