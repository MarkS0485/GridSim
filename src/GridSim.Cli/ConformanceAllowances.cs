namespace GridSim.Cli;

/// <summary>
/// D-DOC-039 (October 2026): the explicit, documented list of shipped corpus cases that `gridsim conformance` is
/// allowed to find unconverged, each with its reason.
///
/// <para>Why a list. Since R220 the verb exits 3 when any case does not converge or cannot be run, so that a case
/// which stops converging cannot vanish from the gate. But three PGLib optimal-power-flow cases in the shipped corpus
/// never converge as power flows, so the verb exited 3 on EVERY run of the shipped corpus and the exit code told a CI
/// job nothing. The allowance list names exactly those cases, so the shipped corpus passes (exit 0) and any NEW
/// failure still fails: a case not on the list that does not converge, any case that cannot be loaded or run, an
/// allowed case that fails in a different way than the list says, or a gate failure on a converged case.</para>
///
/// <para>What an allowance covers, and what it does not. An allowance matches by the case's file stem AND by the
/// solver status it expects (<see cref="PowerFlowStatusName"/>, "MaxIterations"): the same case ending
/// "NonFinite" (a divergence to NaN) is a different failure and still fails the run. An allowance never covers a load
/// failure or a solve that throws. An allowed case that converges is audited like any other case and a note says the
/// allowance was not needed (a solve that improves is not a regression, so it does not fail the run). `--no-allowances`
/// switches the list off for a run that wants every case to converge.</para>
///
/// <para>The reasons. All three are PGLib-OPF (IEEE PES Power Grid Library, optimal power flow) benchmark files: each
/// stores an optimisation operating point, not a power-flow start, and plain Newton-Raphson from the stored voltages
/// with reactive limits enforced does not reach the 1e-8 pu mismatch in 100 iterations (status MaxIterations, measured
/// on the shipped files with 1.95.195; manual ch. 30 lists the family). This is a property of the input file, not a
/// solver regression: nothing in the list was ever converged by this verb.</para>
/// </summary>
internal static class ConformanceAllowances
{
    /// <summary>The solver status an allowed case is expected to end with (PowerFlowStatus.MaxIterations).</summary>
    internal const string PowerFlowStatusName = "MaxIterations";

    /// <summary>One allowed unconverged case: its file stem, the status it is allowed to end with, and why.</summary>
    internal sealed record Allowance(string CaseStem, string ExpectedStatus, string Reason);

    /// <summary>The list, in corpus order. Adding a case here needs a reason a reader can check.</summary>
    internal static readonly IReadOnlyList<Allowance> KnownUnconverged = new[]
    {
        new Allowance("pglib_opf_case3_lmbd", PowerFlowStatusName,
            "PGLib-OPF 3-bus optimisation operating point (data/matpower/nesta); Newton from its stored voltages does not converge in 100 iterations"),
        new Allowance("pglib_opf_case162_ieee_dtc", PowerFlowStatusName,
            "PGLib-OPF 162-bus optimisation operating point (data/matpower/nesta); Newton from its stored voltages does not converge in 100 iterations"),
        new Allowance("pglib_opf_case240_pserc", PowerFlowStatusName,
            "PGLib-OPF 240-bus optimisation operating point (data/matpower/nesta); Newton from its stored voltages does not converge in 100 iterations"),
    };

    /// <summary>
    /// The allowance for an unconverged case, or null when the case is not allowed to be unconverged with this status.
    /// Matching is exact and ordinal on the stem (a renamed or new case is never covered by accident) and on the status.
    /// </summary>
    internal static Allowance? For(string caseStem, string status)
    {
        ArgumentNullException.ThrowIfNull(caseStem);
        ArgumentNullException.ThrowIfNull(status);
        foreach (Allowance a in KnownUnconverged)
            if (string.Equals(a.CaseStem, caseStem, StringComparison.Ordinal)
                && string.Equals(a.ExpectedStatus, status, StringComparison.Ordinal))
                return a;
        return null;
    }

    /// <summary>True when the case stem is on the list at all (whatever status), for the "allowance not needed" note.</summary>
    internal static bool IsListed(string caseStem)
        => KnownUnconverged.Any(a => string.Equals(a.CaseStem, caseStem, StringComparison.Ordinal));

    /// <summary>
    /// The conformance exit code from the run's tallies (D-DOC-039; the rule in manual ch. 19). 3 wins over 1:
    /// <list type="bullet">
    /// <item>3 when any case could not be loaded or run (<paramref name="skipped"/>) or did not converge without a
    /// matching allowance (<paramref name="unallowedUnconverged"/>): a convergence or run failure;</item>
    /// <item>1 when a converged case failed the hard-law gate (<paramref name="gateFailures"/>);</item>
    /// <item>0 otherwise, including when the only unconverged cases are allowed ones.</item>
    /// </list>
    /// </summary>
    internal static int ExitCode(int gateFailures, int unallowedUnconverged, int skipped)
    {
        if (gateFailures < 0 || unallowedUnconverged < 0 || skipped < 0)
            throw new ArgumentOutOfRangeException(nameof(gateFailures), "conformance tallies cannot be negative");
        if (unallowedUnconverged > 0 || skipped > 0) return 3;
        if (gateFailures > 0) return 1;
        return 0;
    }
}
