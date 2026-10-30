using GridSim.Gda.Neso;
using Parquet;
using Parquet.Schema;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// R129: the phase-angle reference is read from typed parquet columns instead of a boxed dictionary per row, its
/// lookup is a binary search, and a week is cached. The claim is exact replication of the original reader, so every
/// test compares <see cref="PhaseAngleInferedReader.ReadPartition"/> against
/// <see cref="PhaseAngleInferedReader.ReadPartitionReference"/> bit for bit (ticks, Kind and every double's bits),
/// over the storage layouts the lake can hold, and checks which path served the read.
///
/// <para>GR-880: the tests read the process-wide <see cref="PhaseAngleInferedReader.LastReadPath"/> and share its one
/// week cache, so the class runs in its own collection with parallelisation off: no other test class can read a
/// partition between a read here and the check of which path served it.</para>
/// </summary>
[Collection(PhaseAngleReaderTestGroup.Name)]
public sealed class PhaseAngleInferedTypedReaderTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gridsim-pa-{Guid.NewGuid():N}");

    public PhaseAngleInferedTypedReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string NewDir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void AssertBitIdentical(PhaseAngleInferedSeries expected, PhaseAngleInferedSeries actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            PhaseAngleSample e = expected.Samples[i], a = actual.Samples[i];
            Assert.Equal(e.Utc.Ticks, a.Utc.Ticks);
            Assert.Equal(e.Utc.Kind, a.Utc.Kind);
            Assert.Equal(BitConverter.DoubleToInt64Bits(e.PhaseDeg), BitConverter.DoubleToInt64Bits(a.PhaseDeg));
            Assert.Equal(BitConverter.DoubleToInt64Bits(e.FreqHz), BitConverter.DoubleToInt64Bits(a.FreqHz));
            Assert.Equal(BitConverter.DoubleToInt64Bits(e.RocofHzPerS), BitConverter.DoubleToInt64Bits(a.RocofHzPerS));
        }
    }

    /// <summary>Read through the new path (which must be <paramref name="expectedPath"/>) and the reference, and
    /// require identical series.</summary>
    private static PhaseAngleInferedSeries Parity(string dir, string expectedPath)
    {
        PhaseAngleInferedSeries reference = PhaseAngleInferedReader.ReadPartitionReference(dir);
        PhaseAngleInferedSeries fast = PhaseAngleInferedReader.ReadPartition(dir);
        Assert.Equal(expectedPath, PhaseAngleInferedReader.LastReadPath);
        AssertBitIdentical(reference, fast);
        return fast;
    }

    /// <summary>R299 / N035: both readers must REFUSE the partition, with the same exception type and the same message
    /// (same first offending row, same column); a refused partition is not cached. Returns the message.</summary>
    private static string RefusalParity(string dir)
    {
        var reference = Assert.Throws<InvalidDataException>(() => PhaseAngleInferedReader.ReadPartitionReference(dir));
        var fast = Assert.Throws<InvalidDataException>(() => PhaseAngleInferedReader.ReadPartition(dir));
        Assert.Equal(reference.Message, fast.Message);
        return fast.Message;
    }

    private static async Task Write(string path, params (DataField Field, Array Values)[] columns)
    {
        var schema = new ParquetSchema(columns.Select(c => (Field)c.Field).ToArray());
        using Stream s = File.Create(path);
        await using ParquetWriter w = await ParquetWriter.CreateAsync(schema, s);
        using ParquetRowGroupWriter rg = w.CreateRowGroup();
        foreach ((DataField f, Array v) in columns)
        {
            switch (v)
            {
                case string?[] x: await rg.WriteAsync(f, x); break;
                case DateTime[] x: await rg.WriteAsync(f, new ReadOnlyMemory<DateTime>(x)); break;
                case DateTime?[] x: await rg.WriteAsync(f, new ReadOnlyMemory<DateTime?>(x)); break;
                case double[] x: await rg.WriteAsync(f, new ReadOnlyMemory<double>(x)); break;
                case double?[] x: await rg.WriteAsync(f, new ReadOnlyMemory<double?>(x)); break;
                case float[] x: await rg.WriteAsync(f, new ReadOnlyMemory<float>(x)); break;
                case int[] x: await rg.WriteAsync(f, new ReadOnlyMemory<int>(x)); break;
                case long[] x: await rg.WriteAsync(f, new ReadOnlyMemory<long>(x)); break;
                case decimal[] x: await rg.WriteAsync(f, new ReadOnlyMemory<decimal>(x)); break;
                default: throw new InvalidOperationException(v.GetType().Name);
            }
        }
    }

    private static double[] Phases(int n, int seed)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => r.NextDouble() * 720.0 - 360.0).ToArray();
    }

    [Fact]
    public async Task String_timestamps_and_double_values_read_identically()
    {
        string d = NewDir("strings");
        const int n = 500;
        // Moved 2026-10 (Q5, authorised by Mark): row 7 held "not a time" and row 8 a null timestamp, and the test
        // expected both readers to SKIP them silently (n - 2 samples), i.e. it pinned the N035 / R299 defect. An
        // unparseable or missing timestamp is now an error in both readers. The parity of the typed path is checked
        // on the clean partition (all n rows), and each bad row separately must be refused identically by both.
        string?[] ts = Enumerable.Range(0, n).Select(i => T0.AddSeconds(i).ToString("o")).ToArray();
        async Task WriteWith(string dir, string?[] times) => await Write(Path.Combine(dir, "part-0.parquet"),
            (new DataField<string>("timestamp_utc"), times),
            (new DataField<double>("phase_deg_off000"), Phases(n, 1)),
            (new DataField<double>("f_hz"), Phases(n, 2)),
            (new DataField<double>("rocof_hz_per_s"), Phases(n, 3)));
        await WriteWith(d, ts);
        Assert.Equal(n, Parity(d, "typed").Count);

        string?[] unparseable = (string?[])ts.Clone();
        unparseable[7] = "not a time";
        string bad = NewDir("strings-unparseable");
        await WriteWith(bad, unparseable);
        Assert.Contains("row 8 has a timestamp_utc 'not a time'", RefusalParity(bad));   // rows are 1-based

        string?[] missing = (string?[])ts.Clone();
        missing[8] = null;
        string hole = NewDir("strings-null");
        await WriteWith(hole, missing);
        Assert.Contains("row 9 has no 'timestamp_utc' value", RefusalParity(hole));
    }

    [Fact]
    public async Task Datetime_timestamps_with_fractional_seconds_are_truncated_exactly_as_the_text_round_trip()
    {
        string d = NewDir("datetimes");
        const int n = 400;
        var r = new Random(4);
        DateTime[] ts = Enumerable.Range(0, n).Select(i => T0.AddSeconds(i).AddMilliseconds(r.Next(0, 1000))).ToArray();
        await Write(Path.Combine(d, "part-0.parquet"),
            (new DataField<DateTime>("timestamp_utc"), ts),
            (new DataField<float>("phase_deg_off000"), Phases(n, 5).Select(x => (float)x).ToArray()),
            (new DataField<int>("f_hz"), Enumerable.Range(0, n).Select(i => 49 + i % 3).ToArray()),
            (new DataField<long>("rocof_hz_per_s"), Enumerable.Range(0, n).Select(i => (long)(i % 5) - 2).ToArray()));
        PhaseAngleInferedSeries s = Parity(d, "typed");
        Assert.All(s.Samples, x => Assert.Equal(0, x.Utc.Ticks % TimeSpan.TicksPerSecond));
    }

    [Fact]
    public async Task Nulls_take_the_reference_defaults_and_null_times_skip_the_row()
    {
        // Moved 2026-10 (Q5, authorised by Mark): this test used to require both readers to SKIP rows with a null
        // timestamp and to fill null values with the defaults (phase 0, f 50.0 Hz, RoCoF 0), asserting that "the
        // f_hz default reached" a sample - i.e. it pinned the N035 / R299 defect (a missing frequency reported as a
        // nominal 50.0 Hz). The name is kept so the history is traceable; the expectation now is that nullable
        // columns WITHOUT holes read identically through both paths, and that each kind of hole (null time, null
        // phase, null f_hz, null rocof) is refused identically by both, at the same row, naming the same column.
        const int n = 300;
        DateTime?[] ts = Enumerable.Range(0, n).Select(i => (DateTime?)T0.AddSeconds(i)).ToArray();
        double?[] Full(int seed) => Phases(n, seed).Select(x => (double?)x).ToArray();
        async Task WriteWith(string dir, DateTime?[] t, double?[] ph, double?[] f, double?[] r) =>
            await Write(Path.Combine(dir, "part-0.parquet"),
                (new DataField<DateTime?>("timestamp_utc"), t),
                (new DataField<double?>("phase_deg_off000"), ph),
                (new DataField<double?>("f_hz"), f),
                (new DataField<double?>("rocof_hz_per_s"), r));

        string d = NewDir("nulls");
        await WriteWith(d, ts, Full(6), Full(7), Full(8));
        Assert.Equal(n, Parity(d, "typed").Count);

        DateTime?[] tHole = (DateTime?[])ts.Clone(); tHole[17] = null;
        string a = NewDir("nulls-time");
        await WriteWith(a, tHole, Full(6), Full(7), Full(8));
        Assert.Contains("row 18 has no 'timestamp_utc' value", RefusalParity(a));

        foreach ((string col, int idx) in new[] { ("phase_deg_off000", 0), ("f_hz", 1), ("rocof_hz_per_s", 2) })
        {
            double?[][] cols = { Full(6), Full(7), Full(8) };
            cols[idx][40] = null;
            string dir = NewDir("nulls-" + col);
            await WriteWith(dir, ts, cols[0], cols[1], cols[2]);
            Assert.Contains($"row 41 has no '{col}' value", RefusalParity(dir));
        }
    }

    [Fact]
    public async Task A_missing_f_hz_column_is_an_error_in_both_readers_not_a_nominal_50_hz()
    {
        // R299 / N035: the partition has no f_hz column at all. The readers used to report a perfect 50.0 Hz.
        string d = NewDir("no-fhz");
        await Write(Path.Combine(d, "part-0.parquet"),
            (new DataField<string>("timestamp_utc"), new string?[] { T0.ToString("o"), T0.AddSeconds(1).ToString("o") }),
            (new DataField<double>("phase_deg_off000"), new[] { 1.0, 2.0 }),
            (new DataField<double>("rocof_hz_per_s"), new[] { 0.0, 0.0 }));
        Assert.Contains("the 'f_hz' column is missing", RefusalParity(d));
    }

    [Fact]
    public async Task A_non_finite_value_is_an_error_in_both_readers()
    {
        string d = NewDir("nan");
        await Write(Path.Combine(d, "part-0.parquet"),
            (new DataField<string>("timestamp_utc"), new string?[] { T0.ToString("o"), T0.AddSeconds(1).ToString("o") }),
            (new DataField<double>("phase_deg_off000"), new[] { 1.0, 2.0 }),
            (new DataField<double>("f_hz"), new[] { 50.0, double.NaN }),
            (new DataField<double>("rocof_hz_per_s"), new[] { 0.0, 0.0 }));
        Assert.Contains("row 2 has a 'f_hz' value that is not a finite number", RefusalParity(d));
    }

    [Fact]
    public void A_csv_partition_with_a_blank_f_hz_or_a_bad_timestamp_is_refused_by_the_reference_reader()
    {
        string blank = NewDir("csv-blank");
        File.WriteAllText(Path.Combine(blank, "x.csv"),
            "timestamp_utc,phase_deg_off000,f_hz,rocof_hz_per_s\n" + $"{T0:o},5,50,0\n{T0.AddSeconds(1):o},6,,0.1\n");
        Assert.Contains("row 2 has no 'f_hz' value", RefusalParity(blank));

        string badTime = NewDir("csv-time");
        File.WriteAllText(Path.Combine(badTime, "x.csv"),
            "timestamp_utc,phase_deg_off000,f_hz,rocof_hz_per_s\n" + $"yesterday,5,50,0\n");
        Assert.Contains("timestamp_utc 'yesterday' that does not parse", RefusalParity(badTime));

        string noRocof = NewDir("csv-no-rocof");
        File.WriteAllText(Path.Combine(noRocof, "x.csv"), "timestamp_utc,phase_deg_off000,f_hz\n" + $"{T0:o},5,50\n");
        Assert.Contains("the 'rocof_hz_per_s' column is missing", RefusalParity(noRocof));
    }

    [Fact]
    public async Task Several_parts_unsorted_rows_a_column_missing_from_one_part_and_a_variant_header_read_identically()
    {
        // Moved 2026-10 (Q5, authorised by Mark): part 1 used to have no rocof column and the test required both
        // readers to give its row the default RoCoF 0 - i.e. it pinned the N035 / R299 defect (a missing value read
        // as a plausible number). Part 1 now carries its rocof column, so the rest of the test (variant header
        // spelling, several parts, unsorted parts, duplicate times kept in stable order) is checked exactly as
        // before; and the same layout WITH the rocof column missing from part 1 must now be refused identically by
        // both readers, at part 1's row (row 3 in the reference's part-by-part row order).
        async Task WriteParts(string dir, bool part1HasRocof)
        {
            // Part 0 uses a variant spelling of the time column (the reference resolves names trimmed and ignoring
            // case); part 2 holds earlier times than part 0.
            await Write(Path.Combine(dir, "part-0.parquet"),
                (new DataField<string>(" Timestamp_UTC "), new string?[] { T0.AddSeconds(10).ToString("o"), T0.AddSeconds(11).ToString("o") }),
                (new DataField<double>("phase_deg_off000"), new[] { 10.0, 11.0 }),
                (new DataField<double>("f_hz"), new[] { 50.01, 50.02 }),
                (new DataField<double>("rocof_hz_per_s"), new[] { 0.01, 0.02 }));
            if (part1HasRocof)
                await Write(Path.Combine(dir, "part-1.parquet"),
                    (new DataField<string>(" Timestamp_UTC "), new string?[] { T0.AddSeconds(20).ToString("o") }),
                    (new DataField<double>("phase_deg_off000"), new[] { 20.0 }),
                    (new DataField<double>("f_hz"), new[] { 49.9 }),
                    (new DataField<double>("rocof_hz_per_s"), new[] { -0.03 }));
            else
                await Write(Path.Combine(dir, "part-1.parquet"),
                    (new DataField<string>(" Timestamp_UTC "), new string?[] { T0.AddSeconds(20).ToString("o") }),
                    (new DataField<double>("phase_deg_off000"), new[] { 20.0 }),
                    (new DataField<double>("f_hz"), new[] { 49.9 }));
            // GR-794: part 2 used to hold two rows with ONE time (T0 + 1 s twice) and the test pinned that both were kept.
            // Duplicate times are now refused by both readers (see the GR-794 test below), so the second row is at
            // T0 + 2 s; the rest of the layout (several parts, unsorted, a variant header) is checked as before.
            await Write(Path.Combine(dir, "part-2.parquet"),
                (new DataField<string>(" Timestamp_UTC "), new string?[] { T0.AddSeconds(1).ToString("o"), T0.AddSeconds(2).ToString("o") }),
                (new DataField<double>("phase_deg_off000"), new[] { 1.0, 2.0 }),
                (new DataField<double>("f_hz"), new[] { 50.0, 50.0 }),
                (new DataField<double>("rocof_hz_per_s"), new[] { 0.0, 0.0 }));
        }

        string d = NewDir("parts");
        await WriteParts(d, part1HasRocof: true);
        PhaseAngleInferedSeries s = Parity(d, "typed");
        Assert.Equal(5, s.Count);

        string gap = NewDir("parts-missing-rocof");
        await WriteParts(gap, part1HasRocof: false);
        Assert.Contains("row 3 has no 'rocof_hz_per_s' value", RefusalParity(gap));
    }

    [Fact]
    public async Task GR794_two_rows_with_one_timestamp_are_refused_by_both_readers()
    {
        string d = NewDir("dup");
        await Write(Path.Combine(d, "part-0.parquet"),
            (new DataField<string>("timestamp_utc"), new string?[] { T0.ToString("o"), T0.AddSeconds(1).ToString("o"), T0.ToString("o") }),
            (new DataField<double>("phase_deg_off000"), new[] { 1.0, 2.0, 3.0 }),
            (new DataField<double>("f_hz"), new[] { 50.0, 50.0, 50.0 }),
            (new DataField<double>("rocof_hz_per_s"), new[] { 0.0, 0.0, 0.0 }));
        Assert.Contains("rows 1 and 3 have the same timestamp_utc 2026-09-14T00:00:00Z", RefusalParity(d));
    }

    [Fact]
    public async Task An_unsupported_storage_type_falls_back_to_the_reference_reader()
    {
        string d = NewDir("decimal");
        await Write(Path.Combine(d, "part-0.parquet"),
            (new DataField<string>("timestamp_utc"), new string?[] { T0.ToString("o"), T0.AddSeconds(1).ToString("o") }),
            (new DataField<decimal>("phase_deg_off000"), new[] { 1.5m, 2.5m }),
            (new DataField<double>("f_hz"), new[] { 50.0, 50.1 }),
            (new DataField<double>("rocof_hz_per_s"), new[] { 0.0, 0.1 }));
        Parity(d, "reference");
    }

    [Fact]
    public void A_csv_partition_is_read_by_the_reference_reader()
    {
        string d = NewDir("csv");
        File.WriteAllText(Path.Combine(d, "x.csv"),
            "timestamp_utc,phase_deg_off000,f_hz,rocof_hz_per_s\n" +
            $"{T0:o},5,50,0\n{T0.AddSeconds(1):o},6,50.1,0.1\n");
        Assert.Equal(2, Parity(d, "reference").Count);
    }

    [Fact]
    public async Task An_unchanged_week_is_served_from_the_cache_and_a_changed_one_is_read_again()
    {
        // Moved 2026-10 (Q5, authorised by Mark): both partitions here used to carry only timestamp_utc and
        // phase_deg_off000, relying on the silent f_hz = 50.0 / RoCoF = 0 defaults (N035 / R299) that are now an
        // error. They now carry f_hz and rocof_hz_per_s columns; what the test checks (the cache hit, and a re-read
        // after the files change) is unchanged.
        string d = NewDir("cache");
        string file = Path.Combine(d, "part-0.parquet");
        await Write(file,
            (new DataField<string>("timestamp_utc"), new string?[] { T0.ToString("o"), T0.AddSeconds(1).ToString("o") }),
            (new DataField<double>("phase_deg_off000"), new[] { 1.0, 2.0 }),
            (new DataField<double>("f_hz"), new[] { 50.0, 50.0 }),
            (new DataField<double>("rocof_hz_per_s"), new[] { 0.0, 0.0 }));
        PhaseAngleInferedSeries first = PhaseAngleInferedReader.ReadPartition(d);
        Assert.Equal("typed", PhaseAngleInferedReader.LastReadPath);
        Assert.Same(first, PhaseAngleInferedReader.ReadPartition(d));
        Assert.Equal("cache", PhaseAngleInferedReader.LastReadPath);

        await Write(file,
            (new DataField<string>("timestamp_utc"), new string?[] { T0.ToString("o"), T0.AddSeconds(1).ToString("o"), T0.AddSeconds(2).ToString("o") }),
            (new DataField<double>("phase_deg_off000"), new[] { 1.0, 2.0, 4.0 }),
            (new DataField<double>("f_hz"), new[] { 50.0, 50.0, 50.0 }),
            (new DataField<double>("rocof_hz_per_s"), new[] { 0.0, 0.0, 0.0 }));
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(5));   // a distinct stamp on coarse clocks
        PhaseAngleInferedSeries second = PhaseAngleInferedReader.ReadPartition(d);
        Assert.Equal("typed", PhaseAngleInferedReader.LastReadPath);
        Assert.Equal(3, second.Count);
    }

    [Fact]
    public async Task ReadWeek_finds_the_iso_week_partition_and_matches_the_reference()
    {
        DateTime at = T0.AddHours(30);
        string dir = Path.Combine(_root, "lake", "InferedOrDerivedData", "Infered", "PHASEANGLE_INFERED",
            $"year={System.Globalization.ISOWeek.GetYear(at)}", $"week={System.Globalization.ISOWeek.GetWeekOfYear(at):D2}");
        Directory.CreateDirectory(dir);
        const int n = 200;
        await Write(Path.Combine(dir, "part-0.parquet"),
            (new DataField<string>("timestamp_utc"), Enumerable.Range(0, n).Select(i => T0.AddHours(29).AddSeconds(i * 60).ToString("o")).ToArray<string?>()),
            (new DataField<double>("phase_deg_off000"), Phases(n, 9)),
            (new DataField<double>("f_hz"), Phases(n, 10)),
            (new DataField<double>("rocof_hz_per_s"), Phases(n, 11)));
        PhaseAngleInferedSeries week = PhaseAngleInferedReader.ReadWeek(Path.Combine(_root, "lake"), at);
        AssertBitIdentical(PhaseAngleInferedReader.ReadPartitionReference(dir), week);
    }

    [Fact]
    public void At_is_the_last_sample_at_or_before_the_instant_exactly_as_the_linear_scan()
    {
        var r = new Random(12);
        var samples = new List<PhaseAngleSample>();
        for (int i = 0; i < 400; i++)
        {
            DateTime t = T0.AddSeconds(r.Next(0, 300));   // many duplicate times
            samples.Add(new PhaseAngleSample(t, i, 50.0, 0.0));   // PhaseDeg = original index, to tell duplicates apart
        }
        var series = new PhaseAngleInferedSeries(samples);
        // Moved 2026-10 (Q5, authorised by Mark): this test used to require At to answer series.Samples[0] whenever
        // the linear scan found nothing at or before the instant (k = -5 .. first sample), and to answer the last
        // sample however long after the end (up to k = 309 against samples ending near 299 s) - the R299 defect.
        // The binary search is still checked against the verbatim linear scan at every instant, with the age limit
        // lifted (TimeSpan.MaxValue) so that only the search is under test; an instant before the first sample must
        // now throw, and the default 2 s age limit must refuse the instants well past the end.
        for (int k = -5; k < 310; k++)
        {
            DateTime q = T0.AddSeconds(k).AddTicks(k % 3 == 0 ? 0 : 1);
            // The original linear scan, verbatim.
            PhaseAngleSample? atOrBefore = null;
            foreach (PhaseAngleSample s in series.Samples)
            {
                if (s.Utc <= q) atOrBefore = s;
                else break;
            }
            if (atOrBefore is null)
                Assert.Throws<InvalidOperationException>(() => series.At(q, TimeSpan.MaxValue));
            else
                Assert.Same(atOrBefore, series.At(q, TimeSpan.MaxValue));
        }
        DateTime last = series.Samples[^1].Utc;
        Assert.Same(series.Samples[^1], series.At(last.AddSeconds(2)));                  // exactly the default age
        Assert.Throws<InvalidOperationException>(() => series.At(last.AddSeconds(2).AddTicks(1)));   // one tick beyond
        Assert.Throws<InvalidOperationException>(() => new PhaseAngleInferedSeries(Array.Empty<PhaseAngleSample>()).At(T0));
    }
}

/// <summary>GR-880: the non-parallel collection for tests that read the phase-angle reader's process-wide state.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PhaseAngleReaderTestGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "Phase-angle reader (process-wide state)";
}
