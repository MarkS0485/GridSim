namespace GridSim.Core.Protection;

/// <summary>One row of a real, timestamped sequence-of-events (SOE) / alarm log: a protection operation on a
/// branch. Map a real alarm's absolute UTC to <see cref="AtSeconds"/> (seconds from the fault t=0) before
/// cross-referencing. Non-branch alarms (bus voltage/frequency) can be logged with <see cref="From"/> =
/// <see cref="To"/> = 0 and are not matched against the branch-trip cascade.</summary>
/// <param name="AtSeconds">Time of the alarm, seconds from the event start (t=0).</param>
/// <param name="Device">ANSI device id as logged, e.g. "51", "50", "21-Z1" (matched loosely).</param>
/// <param name="From">Branch endpoint bus id (0 = not a branch alarm).</param>
/// <param name="To">Branch endpoint bus id.</param>
/// <param name="Description">Free-text alarm description (carried through to the report).</param>
public sealed record AlarmEvent(double AtSeconds, string Device, int From, int To, string Description = "");

/// <summary>How one event lines up between the predicted cascade and the observed alarm log.</summary>
public enum SoeMatchKind
{
    /// <summary>A predicted trip and an observed alarm on the same branch, in the predicted order.</summary>
    Agreement,
    /// <summary>Predicted a trip on this branch, but no alarm for it in the log - the model over-predicted.</summary>
    PredictedNotObserved,
    /// <summary>A real branch-trip alarm the model did not predict - THIS is where the model is missing something.</summary>
    ObservedNotPredicted,
    /// <summary>Predicted and observed on the same branch, but the observed order contradicts the prediction.</summary>
    OrderMismatch,
}

/// <summary>One reconciled entry between prediction and observation.</summary>
public sealed record SoeEntry(
    SoeMatchKind Kind,
    int? PredictedOrder,
    double? ObservedAtSeconds,
    string Device,
    int From,
    int To,
    string Detail);

/// <summary>The reconciliation of a predicted cascade against a real alarm log.</summary>
public sealed record SoeCrossReference(
    IReadOnlyList<SoeEntry> Entries,
    int Agreements,
    int PredictedNotObserved,
    int ObservedNotPredicted,
    int OrderMismatches)
{
    /// <summary>True when every predicted trip was observed and vice-versa, all in the predicted order -
    /// the model reproduced the event exactly.</summary>
    public bool FullyConsistent => PredictedNotObserved == 0 && ObservedNotPredicted == 0 && OrderMismatches == 0;

    /// <summary>The divergences - the model's blind spots - worth investigating first.</summary>
    public IEnumerable<SoeEntry> Divergences => Entries.Where(e => e.Kind != SoeMatchKind.Agreement);
}

/// <summary>
/// Cross-references a PREDICTED protection cascade (<see cref="CascadeResult"/>) against a REAL timestamped
/// alarm log, so an analyst can see where the physics reproduced the event and - more usefully - where it
/// diverged. A predicted trip with no matching alarm means the model over-predicted; a real branch-trip
/// alarm the cascade never predicted is where the model is missing something; a matched pair that arrived
/// out of the predicted order flags a sequencing error. The cascade orders trips by relay operating-time
/// rank rather than wall-clock, so matching is by branch identity and relative order, not exact timing.
/// </summary>
public static class SequenceOfEvents
{
    /// <summary>Reconcile <paramref name="predicted"/> against <paramref name="observed"/>.</summary>
    public static SoeCrossReference CrossReference(CascadeResult predicted, IReadOnlyList<AlarmEvent> observed)
    {
        // Only branch alarms participate in the branch-trip cross-reference.
        var alarms = observed.Where(a => a.From != 0 && a.To != 0)
            .OrderBy(a => a.AtSeconds)
            .Select((a, i) => (Alarm: a, Index: i, Key: Key(a.From, a.To), Matched: false))
            .ToArray();

        var entries = new List<SoeEntry>();
        // Greedily match each predicted step (in predicted order) to the earliest unmatched alarm on its branch.
        var matched = new List<(int PredictedOrder, double ObservedAt, string Device, int From, int To)>();
        foreach (CascadeStep step in predicted.Steps)
        {
            (int f, int t) key = Key(step.From, step.To);
            int hit = -1;
            for (int i = 0; i < alarms.Length; i++)
                if (!alarms[i].Matched && alarms[i].Key == key) { hit = i; break; }

            if (hit >= 0)
            {
                alarms[hit].Matched = true;
                matched.Add((step.Order, alarms[hit].Alarm.AtSeconds, step.Device, step.From, step.To));
            }
            else
            {
                entries.Add(new SoeEntry(SoeMatchKind.PredictedNotObserved, step.Order, null,
                    step.Device, step.From, step.To,
                    $"predicted {step.Device} trip on {step.BranchName} not seen in the alarm log"));
            }
        }

        // Matched pairs: an out-of-order arrival (observed time falls before the previous matched step's
        // observed time, despite a later predicted order) is a sequencing divergence; otherwise agreement.
        int orderMismatches = 0, agreements = 0;
        double prevObserved = double.NegativeInfinity;
        foreach (var m in matched)
        {
            bool outOfOrder = m.ObservedAt < prevObserved - 1e-9;
            if (outOfOrder)
            {
                orderMismatches++;
                entries.Add(new SoeEntry(SoeMatchKind.OrderMismatch, m.PredictedOrder, m.ObservedAt,
                    m.Device, m.From, m.To, $"branch {m.From}-{m.To} tripped out of the predicted order"));
            }
            else
            {
                agreements++;
                entries.Add(new SoeEntry(SoeMatchKind.Agreement, m.PredictedOrder, m.ObservedAt,
                    m.Device, m.From, m.To, $"predicted and observed on branch {m.From}-{m.To}"));
            }
            prevObserved = m.ObservedAt;
        }

        // Observed branch-trip alarms the cascade never predicted - the model's blind spots.
        int observedNotPredicted = 0;
        foreach (var a in alarms)
            if (!a.Matched)
            {
                observedNotPredicted++;
                entries.Add(new SoeEntry(SoeMatchKind.ObservedNotPredicted, null, a.Alarm.AtSeconds,
                    a.Alarm.Device, a.Alarm.From, a.Alarm.To,
                    $"observed {a.Alarm.Device} trip on branch {a.Alarm.From}-{a.Alarm.To} was not predicted"));
            }

        return new SoeCrossReference(entries, agreements, entries.Count(e => e.Kind == SoeMatchKind.PredictedNotObserved),
            observedNotPredicted, orderMismatches);
    }

    private static (int, int) Key(int a, int b) => a <= b ? (a, b) : (b, a); // unordered branch identity
}
