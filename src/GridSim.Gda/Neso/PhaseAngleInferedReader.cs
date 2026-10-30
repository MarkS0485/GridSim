using System.Globalization;
using GridSim.Gda.Csv;
using Parquet;
using Parquet.Schema;

namespace GridSim.Gda.Neso;

/// <summary>One 1 Hz sample of GDA's inferred SYSTEM phase angle: the instant, the phase (degrees, vs the
/// 50 Hz rotating frame), and the centre-of-inertia frequency + RoCoF at that instant.</summary>
public sealed record PhaseAngleSample(DateTime Utc, double PhaseDeg, double FreqHz, double RocofHzPerS);

/// <summary>
/// A window of GDA's inferred SYSTEM phase angle (<c>PHASEANGLE_INFERED</c>, the <c>off000</c> series), sorted
/// by time and re-zeroed so the first sample reads 0 degrees - the "0 degrees at the first timestamp, track from
/// there" convention. This is the common-mode system angle theta_sys(t) (frequency-integrated,
/// theta = theta_0 + 360 * integral(f - 50) dt), NOT a per-bus voltage angle. Used correctly it is the absolute
/// reference frame and the true instantaneous frequency/RoCoF; it must never be injected as a per-bus estimator
/// measurement (it cancels against the slack).
///
/// <para><b>Maximum age (R299).</b> <see cref="At(DateTime)"/> answers with the sample at or before the instant only
/// when that sample is at most <see cref="MaxAge"/> old (default <see cref="DefaultMaxAge"/>, 2 s: two reporting
/// intervals of the 1 Hz product, so one missing sample is tolerated and a gap of two or more is not). It throws
/// when the instant is before the first sample (there is no sample at or before it; answering with a LATER sample
/// would use data from after the instant) and when the newest sample at or before the instant is older than the
/// maximum age (beyond the end of the window, or inside a gap in it). It used to answer the first sample for any
/// earlier instant and the last sample for any later one, however far away, so a reference frame from another day
/// could be applied silently. The system angle advances 360 degrees per second per hertz of frequency deviation
/// (theta = theta_0 + 360 * integral(f - 50) dt), so at a typical 0.05 Hz deviation a sample 2 s stale is already
/// 36 degrees out; a stale angle is not a slightly worse answer, it is a wrong one.</para>
/// </summary>
public sealed class PhaseAngleInferedSeries
{
    /// <summary>The default <see cref="MaxAge"/>: 2 s, two reporting intervals of the 1 Hz PHASEANGLE_INFERED
    /// product (one dropped sample is tolerated, two are not).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(2);

    private readonly List<PhaseAngleSample> _samples;   // sorted ascending by Utc
    private readonly TimeSpan _maxAge = DefaultMaxAge;

    public PhaseAngleInferedSeries(IEnumerable<PhaseAngleSample> samples)
        => _samples = samples.OrderBy(s => s.Utc).ToList();

    // R129: a list already in the order the public constructor would give it (OrderBy is stable, so re-sorting a
    // sorted list returns the same order); skips the second O(n log n) sort in ReZeroed.
    private PhaseAngleInferedSeries(List<PhaseAngleSample> alreadySorted, bool _) => _samples = alreadySorted;

    public IReadOnlyList<PhaseAngleSample> Samples => _samples;
    public int Count => _samples.Count;

    /// <summary>
    /// The oldest a sample may be, relative to the instant asked for, for <see cref="At(DateTime)"/> to use it
    /// (R299). Defaults to <see cref="DefaultMaxAge"/>; set it (with an object initialiser) for data at another
    /// rate. Must not be negative. Kept by <see cref="ReZeroed"/>.
    /// </summary>
    public TimeSpan MaxAge
    {
        get => _maxAge;
        init => _maxAge = value >= TimeSpan.Zero ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxAge), value, "the maximum sample age cannot be negative");
    }

    /// <summary>Re-zero the phase so the FIRST sample reads 0 degrees (a pure additive shift, wrapped to
    /// [-180, 180)). The maximum age is carried over.</summary>
    public PhaseAngleInferedSeries ReZeroed()
    {
        if (_samples.Count == 0) return this;
        double off = _samples[0].PhaseDeg;
        var shifted = new List<PhaseAngleSample>(_samples.Count);
        foreach (PhaseAngleSample s in _samples) shifted.Add(s with { PhaseDeg = Wrap180(s.PhaseDeg - off) });
        return new PhaseAngleInferedSeries(shifted, true) { MaxAge = MaxAge };   // a pure shift keeps the order
    }

    /// <summary>The newest sample at or before <paramref name="instant"/>, provided it is at most
    /// <see cref="MaxAge"/> old. Throws <see cref="InvalidOperationException"/> on an empty series, on an instant
    /// before the first sample, and on a stale answer (see the class remarks, R299).</summary>
    public PhaseAngleSample At(DateTime instant) => At(instant, MaxAge);

    /// <summary>
    /// The newest sample at or before <paramref name="instant"/>, provided it is at most <paramref name="maxAge"/>
    /// old (instant - sample time &lt;= maxAge). Throws <see cref="InvalidOperationException"/> on an empty series,
    /// when the instant is before the first sample, and when the newest sample at or before the instant is older
    /// than <paramref name="maxAge"/>. <see cref="TimeSpan.MaxValue"/> lifts the age limit (the at-or-before lookup
    /// alone); the before-the-first-sample refusal always applies.
    /// </summary>
    public PhaseAngleSample At(DateTime instant, TimeSpan maxAge)
    {
        if (maxAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxAge), maxAge, "the maximum sample age cannot be negative");
        if (_samples.Count == 0) throw new InvalidOperationException("empty phase-angle series");
        instant = GridSim.Core.Diagnostics.UtcTime.Normalise(instant);   // GR-812: compare UTC with UTC (a Local instant was compared by raw ticks)
        // R129: binary search for the LAST sample with Utc <= instant (the linear scan's answer, including the last
        // of several equal timestamps), in O(log n) instead of a scan of up to a week of 1 Hz samples.
        // DateTime comparison is by ticks only, as the scan's '<=' was.
        int lo = 0, hi = _samples.Count;   // invariant: every index < lo is <= instant, every index >= hi is later
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_samples[mid].Utc <= instant) lo = mid + 1;
            else hi = mid;
        }

        // R299: no sample at or before the instant. The old answer was the FIRST sample, i.e. data from after the
        // instant asked for, however far after; that is refused.
        if (lo == 0)
            throw new InvalidOperationException(
                $"PHASEANGLE_INFERED has no sample at or before {instant:O}: the series starts at " +
                $"{_samples[0].Utc:O}. A later sample is not used for an earlier instant.");

        // R299: the newest sample at or before the instant must be fresh enough. Past the end of the window, or in
        // a gap inside it, the old answer was the last sample before, however old; that is refused.
        PhaseAngleSample s = _samples[lo - 1];
        TimeSpan age = instant - s.Utc;   // >= 0 by the search above
        if (age > maxAge)
            throw new InvalidOperationException(
                $"PHASEANGLE_INFERED sample for {instant:O} is stale: the newest sample at or before it is at " +
                $"{s.Utc:O}, {age.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s old, beyond the " +
                $"maximum age of {maxAge.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s.");
        return s;
    }

    private static double Wrap180(double deg)
    {
        deg %= 360.0;
        if (deg >= 180.0) deg -= 360.0;
        if (deg < -180.0) deg += 360.0;
        return deg;
    }
}

/// <summary>
/// Reads GDA's <c>PHASEANGLE_INFERED</c> lake product
/// (<c>InferedOrDerivedData/Infered/PHASEANGLE_INFERED/year=YYYY/week=WW/*.parquet</c>), scoped to the ISO week
/// containing an instant so only ~one week of 1 Hz samples is loaded. Emits the SYSTEM COI angle (common-mode)
/// as a <see cref="PhaseAngleInferedSeries"/> - see that type for the "used correctly" caveat.
///
/// <para><b>Strict reading (R299, N035).</b> Every sample needs all four of <c>timestamp_utc</c>,
/// <c>phase_deg_off000</c>, <c>f_hz</c> and <c>rocof_hz_per_s</c>. A column missing from the partition, a row with
/// no value in one of them (including a part of the partition that lacks the column), a value that is not a finite
/// number, and a timestamp that does not parse are all errors (<see cref="InvalidDataException"/>), raised at the
/// first offending row. They used to be silent: an unparseable or empty timestamp skipped the row, and a missing
/// value read as phase 0, f 50.0 Hz, RoCoF 0 - so a partition with no f_hz column at all reported a perfectly
/// nominal 50.0 Hz grid. Both readers (<see cref="ReadPartition"/> and <see cref="ReadPartitionReference"/>) apply
/// exactly the same checks in the same row order with the same messages, so they still agree bit for bit on every
/// partition they accept and refuse every other partition identically.</para>
/// </summary>
public static class PhaseAngleInferedReader
{
    public const string Product = "PHASEANGLE_INFERED";

    // The four columns every sample needs, in the order each row is checked.
    private const string TimestampColumn = "timestamp_utc";
    private const string PhaseColumn = "phase_deg_off000";
    private const string FreqColumn = "f_hz";
    private const string RocofColumn = "rocof_hz_per_s";
    private static readonly string[] RequiredColumns = { TimestampColumn, PhaseColumn, FreqColumn, RocofColumn };

    /// <summary>
    /// Read the ISO week containing <paramref name="instant"/>, re-zeroed to 0 degrees at the week's first sample.
    /// <paramref name="lakeRoot"/> is the GDA v1 root (the folder that holds <c>InferedOrDerivedData</c>).
    /// </summary>
    public static PhaseAngleInferedSeries ReadWeek(string lakeRoot, DateTime instant)
    {
        // GR-812: the shared rule (UtcTime.Normalise): an Unspecified instant is UTC, as everywhere else in GridSim.
        // ToUniversalTime read it as machine-local time, so the week chosen depended on the machine's time zone.
        DateTime utc = GridSim.Core.Diagnostics.UtcTime.Normalise(instant);
        int year = ISOWeek.GetYear(utc);
        int week = ISOWeek.GetWeekOfYear(utc);
        string productDir = Path.Combine(lakeRoot, "InferedOrDerivedData", "Infered", Product);
        string baseDir = Path.Combine(productDir, FormattableString.Invariant($"year={year}"));

        // Partition folders may be zero-padded (week=05) or not (week=5) - accept whichever exists.
        string[] weekNames = { FormattableString.Invariant($"week={week:D2}"), FormattableString.Invariant($"week={week}") };
        string? dir = weekNames.Select(w => Path.Combine(baseDir, w)).FirstOrDefault(Directory.Exists);

        // GR-318: the lake mixes ISO-year and calendar-year folders, which differ only for the days around New Year whose
        // ISO week belongs to the other year (2027-01-01 is ISO 2026-W53). The ISO-year folder is looked in first, as
        // before; the calendar-year folder of the same ISO week is now looked in too. When BOTH hold the week, which one is
        // the partition cannot be decided, so the read is refused naming both, never chosen silently.
        if (utc.Year != year)
        {
            string calendarDir = Path.Combine(productDir, FormattableString.Invariant($"year={utc.Year}"));
            string? other = weekNames.Select(w => Path.Combine(calendarDir, w)).FirstOrDefault(Directory.Exists);
            if (dir is not null && other is not null)
                throw new InvalidDataException(
                    $"PHASEANGLE_INFERED: ISO week {year}-W{week:D2} of {utc:u} is held both in '{dir}' (ISO year) and in '{other}' " +
                    "(calendar year); keep one, the reader will not choose between them.");
            dir ??= other;
        }
        if (dir is null)
            throw new DirectoryNotFoundException(
                $"PHASEANGLE_INFERED partition not found for {utc:u} (looked under {baseDir}\\week={week:D2}" +
                (utc.Year != year ? FormattableString.Invariant($" and the calendar-year folder year={utc.Year}") : "") + ").");

        return ReadPartition(dir);
    }

    // ---- R129: typed column reading, a one-week cache, the original reader kept as the reference ----

    private static readonly object CacheGate = new();
    private static (string Key, PhaseAngleInferedSeries Series)? _cache;   // the last week read (estimate reads one)

    /// <summary>Diagnostic: how the most recent <see cref="ReadPartition"/> was served - <c>typed</c> (typed
    /// columns), <c>reference</c> (the original reader) or <c>cache</c>. Not for program logic.</summary>
    public static string LastReadPath { get; private set; } = "none";

    /// <summary>
    /// Read one partition directory (as <see cref="ReadWeek"/> does after locating it). The parquet columns
    /// <c>timestamp_utc</c>, <c>phase_deg_off000</c>, <c>f_hz</c> and <c>rocof_hz_per_s</c> are read directly as typed
    /// columns, with exactly the conversions and checks of <see cref="ReadPartitionReference"/> (bit-identical
    /// series, identical refusals), instead of building a boxed dictionary per row for every column. Any layout the
    /// typed path does not cover (a CSV partition, a nested column, a storage type other than string / DateTime for
    /// the timestamp or double / float / int / long for the values) is read by <see cref="ReadPartitionReference"/>
    /// unchanged. The result is cached against the partition's file names, sizes and modification times, so a second
    /// read of an unchanged week is free; any change to the files reads again. A refused partition is not cached.
    /// </summary>
    /// <exception cref="InvalidDataException">A required column or value is missing, a value is not finite, or a
    /// timestamp does not parse (R299 / N035).</exception>
    public static PhaseAngleInferedSeries ReadPartition(string dir)
    {
        List<string> parts = Directory.EnumerateFiles(dir, "*.parquet", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        string key = Fingerprint(dir, parts);
        lock (CacheGate)
            if (_cache is { } c && c.Key == key) { LastReadPath = "cache"; return c.Series; }

        PhaseAngleInferedSeries? typed = parts.Count > 0 ? TryReadTyped(dir, parts) : null;
        PhaseAngleInferedSeries series = typed ?? ReadPartitionReference(dir);
        lock (CacheGate)
        {
            _cache = (key, series);
            LastReadPath = typed is null ? "reference" : "typed";
        }
        return series;
    }

    /// <summary>
    /// The original reader (through <see cref="DataSourceTable"/>, every cell boxed and the timestamp re-parsed as
    /// text). Kept as the fallback for layouts the typed path does not cover and as the reference the parity tests
    /// compare <see cref="ReadPartition"/> against bit for bit. Strict (R299 / N035): a missing column, a missing or
    /// non-finite value, or an unparseable timestamp throws <see cref="InvalidDataException"/> at the first such row,
    /// where it used to skip the row or substitute phase 0, f 50 Hz, RoCoF 0.
    /// </summary>
    public static PhaseAngleInferedSeries ReadPartitionReference(string dir)
    {
        var samples = new List<PhaseAngleSample>();
        using DataSourceTable table = DataSourceTable.Open(dir);

        // Every required column must exist somewhere in the partition (headers are compared as the table resolves
        // them: trimmed, ignoring case).
        foreach (string col in RequiredColumns)
            if (!table.Headers.Any(h => string.Equals(CsvTable.Normalize(h), col, StringComparison.OrdinalIgnoreCase)))
                throw MissingColumn(dir, col);

        long rowNo = 0;   // 1-based, in table order (parts in ordinal file order, rows in file order)
        foreach (IRow row in table.Rows())
        {
            rowNo++;
            // The timestamp as text, parsed exactly as before (AssumeUniversal | AdjustToUniversal, Kind Utc).
            string tsText = row[TimestampColumn];
            if (string.IsNullOrWhiteSpace(tsText)) throw NoValue(dir, rowNo, TimestampColumn);
            DateTime ts = ParseLikeReference(tsText) ?? throw BadTimestamp(dir, rowNo, tsText);

            samples.Add(new PhaseAngleSample(
                ts,
                ReferenceValue(dir, rowNo, row, PhaseColumn),
                ReferenceValue(dir, rowNo, row, FreqColumn),
                ReferenceValue(dir, rowNo, row, RocofColumn)));
        }
        return new PhaseAngleInferedSeries(RefuseDuplicateStamps(dir, samples)).ReZeroed();
    }

    // One value cell through IRow, strictly: absent or blank is an error, and so is anything that is not a finite
    // number (an unparseable text cell reads as the NaN fallback, which is caught by the same test).
    private static double ReferenceValue(string dir, long rowNo, IRow row, string col)
    {
        if (!row.Has(col)) throw NoValue(dir, rowNo, col);
        double v = row.Double(col, double.NaN);
        if (!double.IsFinite(v)) throw NotFinite(dir, rowNo, col);
        return v;
    }

    // GR-794: two rows with one timestamp (after the whole-second reading both readers apply) are refused, naming the
    // stamp and both rows. They used to be kept, and the series answered with whichever the sort left last, so the
    // phase used for an instant depended on row order (two vintages of the week in one folder, or sub-second samples
    // that collapse onto one second). Shared by both readers, so they still refuse identically. Row n is samples[n - 1]:
    // every row either adds one sample or throws.
    private static List<PhaseAngleSample> RefuseDuplicateStamps(string dir, List<PhaseAngleSample> samples)
    {
        var firstRow = new Dictionary<DateTime, int>(samples.Count);
        for (int i = 0; i < samples.Count; i++)
        {
            if (firstRow.TryAdd(samples[i].Utc, i + 1)) continue;
            throw new InvalidDataException(
                $"PHASEANGLE_INFERED partition '{dir}': rows {firstRow[samples[i].Utc].ToString(CultureInfo.InvariantCulture)} and " +
                $"{(i + 1).ToString(CultureInfo.InvariantCulture)} have the same timestamp_utc " +
                $"{samples[i].Utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}; one instant has one sample. " +
                "Keep one publication of the week in the folder.");
        }
        return samples;
    }

    // ---- the errors, shared word for word by both readers ----

    private static InvalidDataException MissingColumn(string dir, string col) => new(
        $"PHASEANGLE_INFERED partition '{dir}': the '{col}' column is missing. Every sample needs timestamp_utc, " +
        "phase_deg_off000, f_hz and rocof_hz_per_s; a missing column is an error, not a default (R299 / N035).");

    private static InvalidDataException NoValue(string dir, long rowNo, string col) => new(
        $"PHASEANGLE_INFERED partition '{dir}': row {rowNo.ToString(CultureInfo.InvariantCulture)} has no '{col}' " +
        "value; a missing value is an error, not a default (R299 / N035).");

    private static InvalidDataException NotFinite(string dir, long rowNo, string col) => new(
        $"PHASEANGLE_INFERED partition '{dir}': row {rowNo.ToString(CultureInfo.InvariantCulture)} has a '{col}' " +
        "value that is not a finite number.");

    private static InvalidDataException BadTimestamp(string dir, long rowNo, string text) => new(
        $"PHASEANGLE_INFERED partition '{dir}': row {rowNo.ToString(CultureInfo.InvariantCulture)} has a " +
        $"timestamp_utc '{text}' that does not parse as a date and time; the row is not skipped (R299 / N035).");

    private static string Fingerprint(string dir, List<string> parts)
    {
        var sb = new System.Text.StringBuilder(Path.GetFullPath(dir));
        // CSV partitions are fingerprinted too (the reference path reads them).
        IEnumerable<string> files = parts.Count > 0 ? parts
            : Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal);
        foreach (string f in files)
        {
            var info = new FileInfo(f);
            sb.Append('|').Append(f).Append(':').Append(info.Length.ToString(CultureInfo.InvariantCulture))
              .Append(':').Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // The column a name resolves to, as DataSourceTable does it: the FIRST field (in the union of every part's
    // fields, first-seen order) whose trimmed name equals the wanted name ignoring case. A part that lacks that
    // exact field reads as null for its rows (as the union-schema reader gives).
    private static string? Resolve(List<DataField> union, string wanted)
    {
        foreach (DataField f in union)
            if (string.Equals(CsvTable.Normalize(f.Name), wanted, StringComparison.OrdinalIgnoreCase)) return f.Name;
        return null;
    }

    private static PhaseAngleInferedSeries? TryReadTyped(string dir, List<string> parts)
    {
        return TryReadTypedAsync(dir, parts).GetAwaiter().GetResult();
    }

    private static async Task<PhaseAngleInferedSeries?> TryReadTypedAsync(string dir, List<string> parts)
    {
        // Pass 1: the union of every part's fields, first-seen order (DataSourceTable's union schema).
        var union = new List<DataField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in parts)
        {
            using Stream st = File.OpenRead(file);
            ParquetSchema schema = await ParquetReader.ReadSchemaAsync(st).ConfigureAwait(false);
            foreach (DataField df in schema.DataFields)
                if (seen.Add(df.Name)) union.Add(df);
        }
        // R299 / N035: a required column absent from every part is an error, in the reference's words and order.
        string tsName = Resolve(union, TimestampColumn) ?? throw MissingColumn(dir, TimestampColumn);
        string phName = Resolve(union, PhaseColumn) ?? throw MissingColumn(dir, PhaseColumn);
        string fName = Resolve(union, FreqColumn) ?? throw MissingColumn(dir, FreqColumn);
        string rName = Resolve(union, RocofColumn) ?? throw MissingColumn(dir, RocofColumn);

        var samples = new List<PhaseAngleSample>();
        long rowNo = 0;   // 1-based across every part and row group, the reference's row order
        foreach (string file in parts)
        {
            using Stream st = File.OpenRead(file);
            await using ParquetReader reader = await ParquetReader.CreateAsync(st).ConfigureAwait(false);
            // Only top-level primitive columns are read here; the untyped reader shows a nested leaf as null, so
            // a wanted name that is nested in this part sends the whole read to the reference path.
            DataField? ts = TopLevel(reader.Schema, tsName, out bool tsNested);
            DataField? ph = TopLevel(reader.Schema, phName, out bool phNested);
            DataField? fz = TopLevel(reader.Schema, fName, out bool fNested);
            DataField? rc = TopLevel(reader.Schema, rName, out bool rNested);
            if (tsNested || phNested || fNested || rNested) return null;
            if (ts is not null && !IsText(ts.ClrType) && ts.ClrType != typeof(DateTime)) return null;
            foreach (DataField? v in new[] { ph, fz, rc })
                if (v is not null && !IsNumeric(v.ClrType)) return null;
            if (ts is not null && ts.IsArray || ph is not null && ph.IsArray || fz is not null && fz.IsArray || rc is not null && rc.IsArray)
                return null;

            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                using ParquetRowGroupReader rg = reader.OpenRowGroupReader(g);
                int n = checked((int)rg.RowCount);
                (DateTime?[] times, string?[] badText) = await ReadTimes(rg, ts, n).ConfigureAwait(false);
                double?[] phase = await ReadDoubles(rg, ph, n).ConfigureAwait(false);
                double?[] freq = await ReadDoubles(rg, fz, n).ConfigureAwait(false);
                double?[] rocof = await ReadDoubles(rg, rc, n).ConfigureAwait(false);
                for (int i = 0; i < n; i++)
                {
                    rowNo++;
                    // The reference's checks, in the reference's order: timestamp, phase, f_hz, rocof.
                    if (times[i] is not DateTime t)
                        throw badText[i] is string text ? BadTimestamp(dir, rowNo, text) : NoValue(dir, rowNo, TimestampColumn);
                    samples.Add(new PhaseAngleSample(t,
                        TypedValue(dir, rowNo, phase[i], PhaseColumn),
                        TypedValue(dir, rowNo, freq[i], FreqColumn),
                        TypedValue(dir, rowNo, rocof[i], RocofColumn)));
                }
            }
        }
        return new PhaseAngleInferedSeries(RefuseDuplicateStamps(dir, samples)).ReZeroed();
    }

    // One typed value, as strictly as ReferenceValue: null (no cell, or a part without the column) and any
    // non-finite number are errors.
    private static double TypedValue(string dir, long rowNo, double? cell, string col)
    {
        if (cell is not double v) throw NoValue(dir, rowNo, col);
        if (!double.IsFinite(v)) throw NotFinite(dir, rowNo, col);
        return v;
    }

    private static DataField? TopLevel(ParquetSchema schema, string? name, out bool nested)
    {
        nested = false;
        if (name is null) return null;
        foreach (Field f in schema.Fields)
        {
            if (f.Name != name) continue;
            if (f is DataField df) return df;
            nested = true;   // a struct / list / map of that name: not a primitive column
            return null;
        }
        foreach (DataField df in schema.DataFields)
            if (df.Name == name) { nested = true; return null; }   // present, but only as a nested leaf
        return null;
    }

    // Parquet.Net 6 reports a UTF-8 string column as ReadOnlyMemory<char> (read through the string overload).
    private static bool IsText(Type t) => t == typeof(string) || t == typeof(ReadOnlyMemory<char>);

    private static bool IsNumeric(Type t) => t == typeof(double) || t == typeof(float) || t == typeof(int) || t == typeof(long);

    // The timestamp exactly as the reference gets it: the cell converted to text (Convert.ToString, invariant
    // culture) and parsed back with AssumeUniversal | AdjustToUniversal, Kind set to Utc. For a DateTime cell that
    // text is the invariant "G" pattern (whole seconds), so the result is the same wall-clock time truncated to the
    // second, as UTC, whatever the cell's Kind; computed directly here (pinned against the text round trip by
    // PhaseAngleInferedTypedReaderTests). Per row the result is the time, or null with 'badText' null (no value:
    // a null or blank cell, or a part without the column), or null with 'badText' the text that did not parse.
    private static async Task<(DateTime?[] Times, string?[] BadText)> ReadTimes(ParquetRowGroupReader rg, DataField? field, int n)
    {
        var result = new DateTime?[n];
        var badText = new string?[n];
        if (field is null) return (result, badText);   // the column is absent from this part: no value on any row
        if (IsText(field.ClrType))
        {
            var text = new string?[n];
            await rg.ReadAsync(field, text.AsMemory()).ConfigureAwait(false);
            for (int i = 0; i < n; i++)
            {
                string cell = text[i] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(cell)) continue;   // no value (the reference's blank test)
                result[i] = ParseLikeReference(cell);
                if (result[i] is null) badText[i] = cell;
            }
            return (result, badText);
        }
        if (field.IsNullable)
        {
            var raw = new DateTime?[n];
            await rg.ReadAsync<DateTime>(field, raw.AsMemory()).ConfigureAwait(false);
            for (int i = 0; i < n; i++) result[i] = raw[i] is DateTime d ? TruncateLikeReference(d) : null;
        }
        else
        {
            var raw = new DateTime[n];
            await rg.ReadAsync<DateTime>(field, raw.AsMemory()).ConfigureAwait(false);
            for (int i = 0; i < n; i++) result[i] = TruncateLikeReference(raw[i]);
        }
        return (result, badText);
    }

    private static DateTime? ParseLikeReference(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime ts)
            ? DateTime.SpecifyKind(ts, DateTimeKind.Utc)
            : null;

    private static DateTime TruncateLikeReference(DateTime d) =>
        new(d.Ticks - d.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    // A value column exactly as IRow.Double gives it: double as is, float / int / long widened; null where there
    // is no value (a null cell, or a part without the column), which the caller treats as an error.
    private static async Task<double?[]> ReadDoubles(ParquetRowGroupReader rg, DataField? field, int n)
    {
        var result = new double?[n];
        if (field is null) return result;   // the column is absent from this part: no value on any row
        Type t = field.ClrType;
        if (t == typeof(double)) await Fill<double>(rg, field, n, result, v => v).ConfigureAwait(false);
        else if (t == typeof(float)) await Fill<float>(rg, field, n, result, v => v).ConfigureAwait(false);
        else if (t == typeof(int)) await Fill<int>(rg, field, n, result, v => v).ConfigureAwait(false);
        else await Fill<long>(rg, field, n, result, v => v).ConfigureAwait(false);
        return result;
    }

    private static async Task Fill<T>(ParquetRowGroupReader rg, DataField field, int n, double?[] into,
        Func<T, double> widen) where T : struct
    {
        if (field.IsNullable)
        {
            var raw = new T?[n];
            await rg.ReadAsync<T>(field, raw.AsMemory()).ConfigureAwait(false);
            for (int i = 0; i < n; i++) into[i] = raw[i] is T v ? widen(v) : null;
        }
        else
        {
            var raw = new T[n];
            await rg.ReadAsync<T>(field, raw.AsMemory()).ConfigureAwait(false);
            for (int i = 0; i < n; i++) into[i] = widen(raw[i]);
        }
    }
}
