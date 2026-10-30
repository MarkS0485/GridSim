namespace GridSim.Core.Presentation;

/// <summary>
/// The MARKET tab's rolling imbalance-price trace, shared by both shells (N144, October 2026). The web got the rule in
/// N037 (in its <c>TabProjections</c>); the WPF <c>MarketVm</c> still added a sample on every tick, so on the 1 s raw
/// replay one half-hourly price filled the 96-sample sparkline with up to 1,800 copies (96 s of one flat line instead
/// of two days of prices). The rule now lives here, in Core, and both shells call it; the web's
/// <c>TabProjections.AppendImbalancePrice</c> overloads are thin wrappers over these, so its behaviour is unchanged.
/// </summary>
public static class ImbalancePriceTrace
{
    /// <summary>How many imbalance prices the sparkline keeps: 96, about two days of half-hourly settlement periods
    /// when one price is taken per period.</summary>
    public const int HistoryLength = 96;

    /// <summary>The length of one GB settlement period: 30 minutes (BSC section X-1, "Settlement Period").</summary>
    public static readonly TimeSpan SettlementPeriodLength = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The settlement period an instant falls in, as a whole number of half-hours since 0001-01-01 00:00 UTC. GB
    /// settlement periods start on the clock's :00 and :30 in UK local time; the UK offset from UTC is always a whole
    /// hour (GMT or BST), so the local half-hour boundaries are exactly the UTC half-hour boundaries and flooring the
    /// UTC instant to a half-hour names the period without any time-zone table, on clock-change days too.
    /// </summary>
    public static long SettlementPeriodKey(DateTime tickUtc)
    {
        if (tickUtc.Kind == DateTimeKind.Local)
            throw new ArgumentException("SettlementPeriodKey needs a UTC (or unspecified-as-UTC) instant, not local time.", nameof(tickUtc));
        return tickUtc.Ticks / SettlementPeriodLength.Ticks;
    }

    /// <summary>
    /// Append one streamed price, oldest dropped past <see cref="HistoryLength"/>, with no settlement-period rule: the
    /// fallback for a tick whose instant is unknown. A missing price adds nothing, and so does a non-finite one (the
    /// sparkline scales to the data's min/max, so a NaN or infinity would wreck the whole trace). Returns true when a
    /// sample was added.
    /// </summary>
    public static bool Append(Queue<double> history, double? priceGbpMwh)
        => Append(history, priceGbpMwh, HistoryLength);

    /// <summary>
    /// <see cref="Append(Queue{double}, double?)"/> with the trace length given: oldest dropped past
    /// <paramref name="capacity"/> (D-DOC-200: the WPF NESO RP1 tab keeps one day, 48 periods, where MARKET keeps
    /// <see cref="HistoryLength"/>). A capacity below 1 fails loud.
    /// </summary>
    public static bool Append(Queue<double> history, double? priceGbpMwh, int capacity)
    {
        ArgumentNullException.ThrowIfNull(history);
        RequireCapacity(capacity);
        if (priceGbpMwh is not double p || !double.IsFinite(p)) return false;
        history.Enqueue(p);
        while (history.Count > capacity) history.Dequeue();
        return true;
    }

    /// <summary>
    /// ONE sample per settlement period (N037 on the web, N144 on WPF). Given the tick's UTC instant and the period key
    /// of the last sample taken (<paramref name="lastPeriodKey"/>, null before the first):
    /// <list type="bullet">
    /// <item><description>A new, later period appends a sample (oldest dropped past <see cref="HistoryLength"/>).</description></item>
    /// <item><description>The same period REPLACES that period's sample with the latest price, so a value revised
    /// within the period (indicative then settled) shows its latest figure and still counts once.</description></item>
    /// <item><description>An EARLIER period (a seek backwards, or a stored lake looping to its start) clears the
    /// history first: the sparkline is a continuous run of consecutive periods, never two runs spliced
    /// together.</description></item>
    /// <item><description>A missing or non-finite price adds nothing and leaves the period marker unchanged.</description></item>
    /// </list>
    /// Returns true when the history changed (a sample was appended or replaced).
    /// </summary>
    public static bool Append(Queue<double> history, double? priceGbpMwh, DateTime tickUtc, ref long? lastPeriodKey)
        => Append(history, priceGbpMwh, tickUtc, ref lastPeriodKey, HistoryLength);

    /// <summary>
    /// The one-sample-per-settlement-period rule of
    /// <see cref="Append(Queue{double}, double?, DateTime, ref long?)"/> with the trace length given (oldest dropped
    /// past <paramref name="capacity"/>). D-DOC-200: the WPF NESO RP1 price trace used to add a sample on every tick
    /// into a 48-sample list, so it held 48 TICKS while MARKET held 96 settlement PERIODS; it now uses this rule with
    /// its own one-day length (48 periods). A capacity below 1 fails loud.
    /// </summary>
    public static bool Append(Queue<double> history, double? priceGbpMwh, DateTime tickUtc, ref long? lastPeriodKey, int capacity)
    {
        ArgumentNullException.ThrowIfNull(history);
        RequireCapacity(capacity);
        if (priceGbpMwh is not double p || !double.IsFinite(p)) return false;
        long key = SettlementPeriodKey(tickUtc);
        if (lastPeriodKey is long last && history.Count > 0)
        {
            if (key == last)
            {
                // Same period: swap the newest sample for the latest price. Queue has no "replace tail", so rebuild
                // it (at most 96 items, once per tick).
                double[] items = history.ToArray();
                items[^1] = p;
                history.Clear();
                foreach (double v in items) history.Enqueue(v);
                return true;
            }
            if (key < last) history.Clear();   // time went backwards: start a new continuous run
        }
        history.Enqueue(p);
        while (history.Count > capacity) history.Dequeue();
        lastPeriodKey = key;
        return true;
    }

    private static void RequireCapacity(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A price trace must keep at least one sample.");
    }
}
