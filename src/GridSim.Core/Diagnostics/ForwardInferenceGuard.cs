namespace GridSim.Core.Diagnostics;

/// <summary>Why a <see cref="ForwardInferenceException"/> was thrown.</summary>
public enum ForwardInferenceReason
{
    /// <summary>The input's timestamp is newer than the historical horizon (recent or future data).</summary>
    NewerThanHorizon = 0,

    /// <summary>
    /// The input carries no timestamp at all, so the horizon cannot be checked (D-DOC-061, October 2026). An undated
    /// input is refused rather than waved through: a date that is missing cannot be shown to be old enough, and
    /// stripping the date from a live set must never be a way round the guard.
    /// </summary>
    Undated = 1,

    /// <summary>
    /// The caller asked the guard to judge against a "now" later than the real clock (D-DOC-018, October 2026), which
    /// would move the horizon forward and let recent data through. See
    /// <see cref="ForwardInferenceGuard.RequireNotAfterRealClock"/>.
    /// </summary>
    NowLaterThanRealClock = 2,
}

/// <summary>Thrown when data newer than the historical horizon (or with no date at all) is fed to the engine.</summary>
public sealed class ForwardInferenceException : Exception
{
    /// <summary>A refusal of data newer than the horizon (<see cref="ForwardInferenceReason.NewerThanHorizon"/>).</summary>
    public ForwardInferenceException(string message) : base(message) { }

    /// <summary>A refusal with an explicit reason (D-DOC-061 added <see cref="ForwardInferenceReason.Undated"/>), so a
    /// resident loop can skip a briefly-fresh window but still stop on an undated one.</summary>
    public ForwardInferenceException(string message, ForwardInferenceReason reason) : base(message) => Reason = reason;

    /// <summary>Why the input was refused.</summary>
    public ForwardInferenceReason Reason { get; } = ForwardInferenceReason.NewerThanHorizon;
}

/// <summary>
/// The hard "no forward inference" invariant. GridSim is a <b>historical state estimator</b>: it may only
/// ever process data that is at least <see cref="HorizonHours"/> old. Anything newer - recent OR future -
/// is rejected at the ingestion boundary, so the engine is <b>structurally incapable</b> of nowcasting or
/// prediction. It can only ever lag reality. Every dated input (event onsets, GDA time-series stamps, NESO
/// frequency samples) passes through here, so "it can only ever lag" is enforced by construction, not policy.
///
/// <para><b>Why SEVEN DAYS + ONE HOUR (widened from 25 h, 2026-07-14, owner decision):</b> the horizon
/// is a defence, and widening it strengthens the defence twice over. First, <b>forward-inference
/// defence in depth</b>: a state that is at least a week old cannot be mistaken for - or repurposed
/// as - a nowcast under ANY publication-lag or clock-skew argument; every data source the project
/// consumes has fully published well inside a week, so the guard margin is no longer comparable to
/// the lag of the slowest feed. Second, <b>reduced operational constraint space</b>: a week-lagged
/// state carries no operationally actionable information (unit commitment, outage windows, market
/// positions and constraint patterns have all rolled over), so the system's entire output space sits
/// outside the envelope an operational or market-sensitive reading could attach to. The extra hour
/// keeps a full settlement week strictly behind the boundary across DST transitions.</para>
/// </summary>
public static class ForwardInferenceGuard
{
    /// <summary>Minimum lag: any processed datum must be at least this many hours old (7 days + 1 h).
    /// The single knob - see the class remarks for the documented widening rationale.</summary>
    public const double HorizonHours = 169.0;

    /// <summary>The newest instant the engine may process, relative to <paramref name="utcNow"/>. The now is
    /// normalised to UTC first (a Local-kind now is converted, an Unspecified one is UTC by contract): DateTime
    /// comparison ignores Kind, so a Local now east of UTC used to put the horizon hours ahead.</summary>
    public static DateTime Horizon(DateTime utcNow) => UtcTime.Normalise(utcNow).AddHours(-HorizonHours);

    /// <summary>Throw <see cref="ForwardInferenceException"/> if <paramref name="stampUtc"/> is newer than the horizon.</summary>
    /// <remarks>Bug hunt 2026-10-29, finding 82 (decided, documented): a supplied <paramref name="utcNow"/> is NOT held to
    /// the real clock here. Some callers rely on that on purpose: the live RTU path passes <see cref="DateTime.MaxValue"/>
    /// to defer this check to the consuming engine, which re-checks against the real clock (RtuMeasurementSetMapper,
    /// pinned by RtuMeasurementSetMapperTests). A caller that takes its "now" from outside (a client, a command line)
    /// must pass it through <see cref="RequireNotAfterRealClock"/> first, as <c>serve --utc-now</c> does; otherwise a later
    /// "now" would move the horizon forward and admit recent data.</remarks>
    public static void RejectFuture(DateTime stampUtc, DateTime? utcNow = null, string? what = null)
    {
        // The now is normalised exactly as the stamp is. A Local-kind now (DateTime.Now, say) passed the real-clock
        // check by the instant it names but its ticks were the local wall clock, so on a host east of UTC the horizon
        // moved forward by the UTC offset (BST: 168 h-old data accepted against the 169 h rule).
        DateTime now = UtcTime.Normalise(utcNow ?? DateTime.UtcNow);
        DateTime horizon = now.AddHours(-HorizonHours);
        DateTime stamp = UtcTime.Normalise(stampUtc);   // Unspecified is UTC by contract (same rule as before)
        if (stamp > horizon)
            throw new ForwardInferenceException(
                $"{what ?? "input"} timestamp {stamp:o} is newer than the historical horizon {horizon:o} " +
                $"(data must lag real time by >={HorizonHours} h). GridSim replays history only - it never " +
                $"processes recent or future data, so it cannot forward-infer.");
    }

    /// <summary>
    /// D-DOC-018 (October 2026): the "now" a caller asks the guard to judge against may only move the horizon BACK
    /// (stricter, for a reproducible test or replay), never forward past the real clock. A requested now later than
    /// the real clock would move the horizon into the present or the future and let recent data through: before this
    /// check, <c>gridsim serve --utc-now 2100-01-01T00:00:00Z</c> estimated a window stamped 2099 and exited 0.
    /// Returns the effective now: <paramref name="requestedUtcNow"/> (normalised to UTC) when it is given and not
    /// later than the real clock, else the real clock when nothing was requested.
    /// </summary>
    /// <param name="requestedUtcNow">The caller's override, or null for the real clock.</param>
    /// <param name="what">Names the override in the refusal message (for example <c>--utc-now</c>).</param>
    /// <param name="realUtcNow">The real clock; null reads <see cref="DateTime.UtcNow"/>. Tests pass a fixed value.</param>
    /// <exception cref="ForwardInferenceException">The requested now is later than the real clock
    /// (<see cref="ForwardInferenceReason.NowLaterThanRealClock"/>).</exception>
    public static DateTime RequireNotAfterRealClock(DateTime? requestedUtcNow, string what = "the requested 'now'",
        DateTime? realUtcNow = null)
    {
        DateTime real = UtcTime.Normalise(realUtcNow ?? DateTime.UtcNow);
        if (requestedUtcNow is not DateTime requested) return real;
        DateTime req = UtcTime.Normalise(requested);
        if (req > real)
            throw new ForwardInferenceException(
                $"{what} {req:o} is later than the real clock {real:o}. It may only move the horizon back (a stricter, " +
                $"reproducible check), never forward: a later 'now' would let data newer than {HorizonHours} h through.",
                ForwardInferenceReason.NowLaterThanRealClock);
        return req;
    }

    /// <summary>
    /// D-DOC-061 (October 2026): refuse an input that carries no timestamp. The dated ingesters used to check the stamp
    /// only "when present", so an undated set skipped the guard entirely. Returns the stamp when there is one; otherwise
    /// throws a <see cref="ForwardInferenceException"/> with <see cref="ForwardInferenceReason.Undated"/>.
    /// </summary>
    /// <param name="stampUtc">The input's timestamp, or null when it has none.</param>
    /// <param name="what">Names the input in the message (for example <c>measurement set 'x.json'</c>).</param>
    /// <param name="fieldHint">The field that should carry the date (for example <c>takenUtc</c>).</param>
    public static DateTime RequireDated(DateTime? stampUtc, string what, string fieldHint)
    {
        if (stampUtc is DateTime stamp) return stamp;
        throw new ForwardInferenceException(
            $"{what} carries no timestamp ('{fieldHint}' is missing), so the historical horizon cannot be checked. " +
            $"An undated input is refused: add '{fieldHint}', an ISO 8601 UTC instant at least {HorizonHours} h old " +
            $"(for example \"2024-03-01T12:00:00Z\"), stating when the data was taken.",
            ForwardInferenceReason.Undated);
    }

    /// <summary>Assert an entire dated series is historical; throws on the first violation.</summary>
    public static void AssertHistorical(IEnumerable<DateTime> stampsUtc, DateTime? utcNow = null, string? what = null)
    {
        DateTime now = UtcTime.Normalise(utcNow ?? DateTime.UtcNow);
        foreach (DateTime s in stampsUtc) RejectFuture(s, now, what);
    }
}
