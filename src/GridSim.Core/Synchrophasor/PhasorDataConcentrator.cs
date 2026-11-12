namespace GridSim.Core.Synchrophasor;

/// <summary>One PMU's decoded measurement at a reporting instant, normalised from a
/// <see cref="C37118DataFrame"/> (PMU id attached from the stream's IDCODE / crosswalk).</summary>
/// <param name="PmuId">The reporting PMU's id.</param>
/// <param name="TimeUtc">The reporting instant (SOC + FRACSEC/TIME_BASE).</param>
/// <param name="Phasors">Phasors in engineering units.</param>
/// <param name="FreqHz">Frequency value.</param>
/// <param name="RocofHzPerS">Rate of change of frequency.</param>
public sealed record PmuSample(int PmuId, DateTime TimeUtc, Phasor[] Phasors, double FreqHz, double RocofHzPerS);

/// <summary>All the PMU samples that share one reporting instant, once the concentrator has aligned them.
/// A PMU that did not report by the wait-window is listed in <see cref="Dropped"/>.</summary>
/// <param name="TimeUtc">The aligned reporting instant.</param>
/// <param name="Samples">The PMUs that reported, keyed by id.</param>
/// <param name="Dropped">Expected PMUs that did not report at this instant (in ascending id order).</param>
public sealed record PdcSuperframe(
    DateTime TimeUtc,
    IReadOnlyDictionary<int, PmuSample> Samples,
    IReadOnlyList<int> Dropped);

/// <summary>
/// A Phasor Data Concentrator: it aligns the streams from several PMUs onto common reporting instants. PMU
/// frames arrive interleaved and slightly skewed; the concentrator buckets them by timestamp and, once a
/// timestamp has fallen behind the moving watermark by more than the wait-window, emits a
/// <see cref="PdcSuperframe"/> holding every PMU that reported and flagging those that did not (dropouts).
/// Deterministic: buckets emit in strict time order and dropped ids in ascending order. Pure transport - the
/// horizon guard is applied downstream when a superframe is mapped to a <see cref="Estimation.MeasurementSet"/>.
///
/// <para><b>Experimental</b> (R062, October 2026). The decoder fixes it waited on are done (FORMAT bit order R046,
/// PHUNIT / FNOM scaling R293, multi-PMU DATA frames N061), and the CLI verb <c>pmu-ingest</c> (N061) aligns a recorded
/// capture's samples with it. Still needs a live C37.118 stream transport (TCP / UDP) to feed it.</para>
/// </summary>
public sealed class PhasorDataConcentrator
{
    private readonly int[] _expected;
    private readonly TimeSpan _waitWindow;
    // Keyed by the bucket's EARLIEST tick (GR-806); each bucket spans at most TickTolerance ticks.
    private readonly SortedDictionary<long, Bucket> _buckets = new();

    // GR-806 (November 2026): one reporting instant may reach the concentrator as two adjacent ticks. The time of a DATA
    // frame is SOC + FRACSEC / TIME_BASE converted to 100 ns ticks; PMUs with different TIME_BASE values round the same
    // instant to ticks that can differ by one (for example 1/3 s is 3333333.3 ticks), and alignment used to be exact to
    // the tick, so such PMUs formed two superframes, each listing the other's PMUs as dropped. A sample now joins a bucket
    // when the bucket's whole span, with it, stays within one tick. Reporting instants are at least 1/240 s apart (C37.118
    // reporting rates), millions of ticks, so two real instants are never merged. Samples whose ticks agree exactly group
    // exactly as before.
    private const long TickTolerance = 1;

    /// <summary>The samples of one reporting instant and the range of ticks they carried.</summary>
    private sealed class Bucket
    {
        public long MinKey;
        public long MaxKey;
        public Dictionary<int, PmuSample> Samples { get; } = new();
    }

    // High-water mark: the latest tick already emitted in a superframe (R294; the bucket's latest tick since GR-806).
    // Ticks; MinValue = none yet.
    private long _emittedThrough = long.MinValue;

    /// <summary>
    /// Samples that arrived for an instant that had ALREADY been emitted (they missed the wait-window) and were
    /// therefore discarded, as a real PDC does. Exposed so a host can report late data rather than lose it
    /// quietly (R294).
    /// </summary>
    public long LateSamples { get; private set; }

    /// <param name="expectedPmuIds">The PMUs whose absence counts as a dropout.</param>
    /// <param name="waitWindow">How long to wait past a timestamp for stragglers before emitting it.</param>
    public PhasorDataConcentrator(IEnumerable<int> expectedPmuIds, TimeSpan waitWindow)
    {
        var ids = new SortedSet<int>(expectedPmuIds);
        _expected = ids.ToArray();
        _waitWindow = waitWindow;
    }

    /// <summary>Buffer one PMU sample against its reporting instant. A later sample for the same
    /// (PMU, instant) replaces the earlier one. A sample for an instant at or before the last emitted one is too
    /// late: its superframe has gone. It is discarded and counted in <see cref="LateSamples"/>; it used to
    /// recreate the bucket and be emitted again as a second superframe for the same instant, holding only the
    /// straggler and listing every other PMU as dropped (R294). Returns false when the sample was discarded.</summary>
    public bool Ingest(PmuSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        long key = sample.TimeUtc.Ticks;
        // GR-806: a sample within one tick of the last emitted instant belongs to that emitted superframe (it would have
        // joined it), so it is late too.
        if (_emittedThrough != long.MinValue && key <= _emittedThrough + TickTolerance)
        {
            LateSamples++;
            return false;
        }
        Bucket? bucket = FindBucket(key);
        if (bucket is null)
        {
            bucket = new Bucket { MinKey = key, MaxKey = key };
            _buckets[key] = bucket;
        }
        else if (key < bucket.MinKey)
        {
            // Re-key the bucket under its new earliest tick, so the superframe's instant does not depend on arrival order.
            _buckets.Remove(bucket.MinKey);
            bucket.MinKey = key;
            _buckets[key] = bucket;
        }
        else if (key > bucket.MaxKey)
            bucket.MaxKey = key;
        bucket.Samples[sample.PmuId] = sample;
        return true;
    }

    // GR-806: the bucket this tick joins, or null. A bucket spans at most TickTolerance ticks, so its earliest tick is
    // within [key - TickTolerance, key + TickTolerance]; the exact tick is tried first, then the earlier and the later.
    private Bucket? FindBucket(long key)
    {
        if (_buckets.TryGetValue(key, out Bucket? exact)) return exact;
        foreach (long min in new[] { key - TickTolerance, key + TickTolerance })
            if (_buckets.TryGetValue(min, out Bucket? b)
                && Math.Max(b.MaxKey, key) - Math.Min(b.MinKey, key) <= TickTolerance)
                return b;
        return null;
    }

    /// <summary>Emit, in time order, every buffered instant that has fallen more than the wait-window behind
    /// <paramref name="watermark"/> (the newest instant seen, or wall time). Emitted instants are removed.</summary>
    public IReadOnlyList<PdcSuperframe> Flush(DateTime watermark)
    {
        long cutoff = watermark.Ticks - _waitWindow.Ticks;
        var ready = new List<PdcSuperframe>();
        var emittedKeys = new List<long>();

        long emittedMax = long.MinValue;
        foreach (KeyValuePair<long, Bucket> kv in _buckets)
        {
            // GR-806: a bucket is ready once its LATEST tick is past the cutoff (with exact ticks, the same test as before).
            if (kv.Value.MaxKey > cutoff) break; // ascending by earliest tick; the spans do not overlap, so nothing later is ready
            var dropped = new List<int>();
            foreach (int id in _expected)
                if (!kv.Value.Samples.ContainsKey(id)) dropped.Add(id);
            // The superframe's instant is the bucket's earliest tick.
            ready.Add(new PdcSuperframe(new DateTime(kv.Key, DateTimeKind.Utc), kv.Value.Samples, dropped));
            emittedKeys.Add(kv.Key);
            emittedMax = Math.Max(emittedMax, kv.Value.MaxKey);
        }

        foreach (long key in emittedKeys) _buckets.Remove(key);
        // Buckets are emitted in ascending order; the latest tick of the last one is the new high-water mark.
        if (emittedKeys.Count > 0) _emittedThrough = emittedMax;
        return ready;
    }

    /// <summary>Emit every remaining buffered instant regardless of the wait-window (end of stream).</summary>
    public IReadOnlyList<PdcSuperframe> FlushAll() => Flush(DateTime.MaxValue);
}
