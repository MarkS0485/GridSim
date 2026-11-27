using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace GridSim.Core.IO.Comtrade;

/// <summary>How the samples of a COMTRADE data (.dat) file are stored (IEEE C37.111 file type line).</summary>
public enum ComtradeFileType
{
    /// <summary>Comma-separated text, one line per sample.</summary>
    Ascii,
    /// <summary>16-bit signed integers, little-endian.</summary>
    Binary,
    /// <summary>32-bit signed integers, little-endian (C37.111-2013).</summary>
    Binary32,
    /// <summary>32-bit IEEE floating point, little-endian (C37.111-2013).</summary>
    Float32,
}

/// <summary>One analog channel line of a configuration (.cfg) file. A sample's value is <c>A x + B</c> for the stored number x.</summary>
public sealed record ComtradeAnalogChannel(int Number, string Id, string Phase, string Component, string Unit, double A, double B,
    double SkewMicroseconds, double Min, double Max, double Primary, double Secondary, char PrimarySecondary);

/// <summary>One status (digital) channel line of a configuration file.</summary>
public sealed record ComtradeDigitalChannel(int Number, string Id, string Phase, string Component, int NormalState);

/// <summary>One sampling-rate line: the rate in hertz (0 when the rate is not constant) and the last sample number it covers.</summary>
public sealed record ComtradeSampleRate(double RateHz, int LastSample);

/// <summary>A parsed COMTRADE configuration (.cfg) file.</summary>
public sealed record ComtradeConfig(string Station, string Device, int RevisionYear,
    IReadOnlyList<ComtradeAnalogChannel> Analog, IReadOnlyList<ComtradeDigitalChannel> Digital, double LineFrequencyHz,
    IReadOnlyList<ComtradeSampleRate> Rates, string Start, string Trigger, ComtradeFileType FileType, double TimeMultiplier);

/// <summary>A COMTRADE recording: the configuration and every sample.</summary>
/// <param name="Config">The configuration.</param>
/// <param name="SampleNumbers">The sample numbers as the data file gives them (1 upward).</param>
/// <param name="TimesSeconds">Each sample's time in seconds from the start of the record.</param>
/// <param name="Analog">Per analog channel, the value of every sample in the channel's own unit (<c>A x + B</c>); no
/// primary or secondary conversion is applied.</param>
/// <param name="Digital">Per status channel, the state of every sample.</param>
/// <param name="MissingValues">IO-11: how many analog values the data file marks as missing (the standard's missing-data
/// codes); each is NaN in <paramref name="Analog"/>.</param>
public sealed record ComtradeRecording(ComtradeConfig Config, IReadOnlyList<int> SampleNumbers, IReadOnlyList<double> TimesSeconds,
    IReadOnlyList<IReadOnlyList<double>> Analog, IReadOnlyList<IReadOnlyList<bool>> Digital, int MissingValues = 0);

/// <summary>
/// Reads IEEE C37.111 (COMTRADE) 1991, 1999 and 2013 recordings: the configuration file and a data file stored as ASCII,
/// BINARY, BINARY32 or FLOAT32.
/// </summary>
/// <remarks>
/// <para><b>Configuration file.</b> Line 1 <c>station,device,year</c> (no year: 1991); line 2 <c>TT,nA,mD</c>; one line per analog
/// channel <c>An,id,phase,component,unit,a,b,skew,min,max,primary,secondary,P|S</c> (the last three are optional, as in 1991);
/// one per status channel <c>Dn,id,phase,component,normal</c> (1991: <c>Dn,id,normal</c>); the line frequency; the number of
/// sampling rates and one <c>rate,last sample</c> line for each (one line is read when the count is 0); the start and trigger
/// times <c>dd/mm/yyyy,hh:mm:ss.ssssss</c>, kept as written; the file type; and the time multiplier (1999 and later; 1
/// when absent). The 2013 time-code and quality lines that may follow are read past.</para>
/// <para><b>Data file.</b> ASCII: <c>n,timestamp,analog...,status...</c>. Binary: little-endian, 4 bytes sample number, 4 bytes
/// time stamp, then 2 (BINARY), 4 (BINARY32) or 4 (FLOAT32) bytes per analog channel, then one 16-bit word per 16 status
/// channels (the first channel in the lowest bit). The time stamp counts microseconds multiplied by the time multiplier
/// (nanoseconds when the configuration writes its start time to nanosecond precision, more than six decimal places); when a
/// file leaves it empty or at the all-ones value, the time comes from the sampling rate of the segment the sample falls in
/// (each rate line covers the samples up to its last sample number). In a file of revision 1999 or later the missing-data
/// codes are read as missing (NaN, counted in <see cref="ComtradeRecording.MissingValues"/>): 0x8000 in a BINARY word,
/// 0x80000000 in a BINARY32 word and 99999 in an ASCII field; an empty analog field is missing in any revision. These three
/// rules follow C37.111 as GridSim reads it; they have not been checked against the standard's text. A combined .cff file
/// is not read.</para>
/// <para><b>Limits.</b> At most <see cref="MaxChannels"/> analog and as many status channels (the standard's six-digit
/// channel numbers), and no more channels than the configuration has lines left; nothing is sized from a count before the
/// lines it promises are read.</para>
/// </remarks>
public static class ComtradeReader
{
    /// <summary>IO-08: the most analog (or status) channels a configuration may declare: the standard numbers channels with
    /// at most six digits.</summary>
    public const int MaxChannels = 999_999;

    /// <summary>Read the recording whose configuration is <paramref name="cfgPath"/>; the data file is the same name with
    /// the extension .dat (any case) unless <paramref name="datPath"/> names it.</summary>
    public static ComtradeRecording ReadFiles(string cfgPath, string? datPath = null)
    {
        if (!File.Exists(cfgPath)) throw new FileNotFoundException("COMTRADE configuration file not found.", cfgPath);
        if (datPath is null)
        {
            string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cfgPath)) ?? ".", Path.GetFileNameWithoutExtension(cfgPath));
            datPath = new[] { ".dat", ".DAT", ".Dat" }.Select(e => stem + e).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("COMTRADE data file not found next to the configuration file.", stem + ".dat");
        }
        return Parse(GridSim.Core.IO.InputFileLimits.ReadAllText(cfgPath), GridSim.Core.IO.InputFileLimits.ReadAllBytes(datPath));   // IO-14
    }

    /// <summary>Parse a configuration file's text.</summary>
    public static ComtradeConfig ParseConfig(string cfgText)
    {
        ArgumentNullException.ThrowIfNull(cfgText);
        string[] lines = cfgText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int at = 0;
        string[] Next(string what)
        {
            while (at < lines.Length && lines[at].Trim().Length == 0) at++;
            if (at >= lines.Length) throw new InvalidDataException($"COMTRADE configuration: the file ends before {what}.");
            return lines[at++].Split(',').Select(f => f.Trim()).ToArray();
        }
        string pos() => $"COMTRADE configuration line {at}";

        string[] l1 = Next("the station line");
        if (l1.Length < 2) throw new InvalidDataException($"{pos()}: the first line needs the station name and the device id.");
        int year = 1991;
        if (l1.Length > 2 && l1[2].Length > 0)
            year = ToInt(l1[2], "the revision year", pos());
        string[] l2 = Next("the channel count line");
        if (l2.Length < 3) throw new InvalidDataException($"{pos()}: the channel count line is 'TT,##A,##D'.");
        int total = ToInt(l2[0], "TT", pos());
        int na = CountOf(l2[1], 'A', pos()), nd = CountOf(l2[2], 'D', pos());
        // IO-08: the sum in long (in int it could wrap and pass), each count within the standard's limit, and no more
        // channels than lines left in the file, before any list is sized from them.
        if (na < 0 || nd < 0 || (long)na + nd != total)
            throw new InvalidDataException($"{pos()}: TT {total} is not the analog count {na} plus the status count {nd}.");
        if (na > MaxChannels || nd > MaxChannels)
            throw new InvalidDataException($"{pos()}: {na} analog and {nd} status channels; the standard allows at most {MaxChannels} of each.");
        int linesLeft = lines.Skip(at).Count(l => l.Trim().Length > 0);
        if (total > linesLeft)
            throw new InvalidDataException($"{pos()}: the configuration declares {total} channels but has only {linesLeft} lines left.");

        var analog = new List<ComtradeAnalogChannel>(na);
        for (int k = 0; k < na; k++)
        {
            string[] f = Next($"analog channel {k + 1}");
            if (f.Length < 10) throw new InvalidDataException($"{pos()}: an analog channel line needs An, id, phase, component, unit, a, b, skew, min and max (10 fields); this has {f.Length}.");
            double primary = f.Length > 10 && f[10].Length > 0 ? ToReal(f[10], "primary", pos()) : 1.0;
            double secondary = f.Length > 11 && f[11].Length > 0 ? ToReal(f[11], "secondary", pos()) : 1.0;
            char ps = f.Length > 12 && f[12].Length > 0 ? char.ToUpperInvariant(f[12][0]) : 'P';
            if (ps != 'P' && ps != 'S') throw new InvalidDataException($"{pos()}: the primary/secondary flag is '{f[12]}'; it must be P or S.");
            analog.Add(new ComtradeAnalogChannel(ToInt(f[0], "the channel number", pos()), f[1], f[2], f[3], f[4],
                ToReal(f[5], "a", pos()), ToReal(f[6], "b", pos()), f[7].Length == 0 ? 0 : ToReal(f[7], "skew", pos()),
                ToReal(f[8], "min", pos()), ToReal(f[9], "max", pos()), primary, secondary, ps));
        }
        var digital = new List<ComtradeDigitalChannel>(nd);
        for (int k = 0; k < nd; k++)
        {
            string[] f = Next($"status channel {k + 1}");
            if (f.Length < 3) throw new InvalidDataException($"{pos()}: a status channel line needs Dn, id and the normal state.");
            string y = f[^1];
            digital.Add(new ComtradeDigitalChannel(ToInt(f[0], "the channel number", pos()), f[1],
                f.Length > 4 ? f[2] : "", f.Length > 4 ? f[3] : "", y.Length == 0 ? 0 : ToInt(y, "the normal state", pos())));
        }
        string[] lf = Next("the line frequency");
        double freq = ToReal(lf[0], "the line frequency", pos());
        string[] nr = Next("the number of sampling rates");
        int nrates = ToInt(nr[0], "the number of sampling rates", pos());
        if (nrates < 0) throw new InvalidDataException($"{pos()}: the number of sampling rates is {nrates}.");
        var rates = new List<ComtradeSampleRate>();
        for (int k = 0; k < Math.Max(nrates, 1); k++)
        {
            string[] f = Next($"sampling rate {k + 1}");
            if (f.Length < 2) throw new InvalidDataException($"{pos()}: a sampling rate line is 'rate,last sample'.");
            rates.Add(new ComtradeSampleRate(ToReal(f[0], "the sampling rate", pos()), ToInt(f[1], "the last sample", pos())));
        }
        string[] st = Next("the start time");
        string[] tr = Next("the trigger time");
        if (st.Length < 2 || tr.Length < 2) throw new InvalidDataException($"{pos()}: a time line is 'dd/mm/yyyy,hh:mm:ss.ssssss'.");
        string[] ftl = Next("the file type");
        ComtradeFileType ft = ftl[0].ToUpperInvariant() switch
        {
            "ASCII" => ComtradeFileType.Ascii,
            "BINARY" => ComtradeFileType.Binary,
            "BINARY32" => ComtradeFileType.Binary32,
            "FLOAT32" => ComtradeFileType.Float32,
            _ => throw new InvalidDataException($"{pos()}: the file type is '{ftl[0]}'; it must be ASCII, BINARY, BINARY32 or FLOAT32."),
        };
        double timemult = 1.0;
        if (year >= 1999 && lines.Skip(at).Any(l => l.Trim().Length > 0))
        {
            string[] tm = Next("the time multiplier");
            timemult = ToReal(tm[0], "the time multiplier", pos());
            if (!(timemult > 0)) throw new InvalidDataException($"{pos()}: the time multiplier is {tm[0]}; it must be above 0.");
        }
        return new ComtradeConfig(l1[0], l1[1], year, analog, digital, freq, rates, st[0] + "," + st[1], tr[0] + "," + tr[1], ft, timemult);
    }

    /// <summary>Parse a recording from its configuration text and its data file bytes.</summary>
    public static ComtradeRecording Parse(string cfgText, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ComtradeConfig cfg = ParseConfig(cfgText);
        int na = cfg.Analog.Count, nd = cfg.Digital.Count;
        var numbers = new List<int>();
        var times = new List<double>();
        var analog = new List<double>[na];
        var digital = new List<bool>[nd];
        for (int k = 0; k < na; k++) analog[k] = new List<double>();
        for (int k = 0; k < nd; k++) digital[k] = new List<bool>();
        // IO-11 (ruling 19; follows C37.111, not checked against the standard's text): the time stamp's unit is microseconds,
        // or nanoseconds when the configuration gives its start time to nanosecond precision (more than six decimal places;
        // only the 2013 format writes it so).
        double stampUnit = FractionDigits(cfg.Start) > 6 ? 1e9 : 1e6;
        int missing = 0;

        var rateCursor = new RateCursor(cfg.Rates);
        void Add(long n, long? stamp, Func<int, double> a, Func<int, bool> d)
        {
            // R2-07: a sample number above int.MaxValue (a binary number is unsigned 32-bit) is refused by number; it used to
            // wrap to a negative SampleNumbers entry without a word.
            if (n > int.MaxValue || n < int.MinValue)
                throw new InvalidDataException(FormattableString.Invariant($"COMTRADE data: sample number {n} is beyond {int.MaxValue}, the most this reader holds."));
            numbers.Add((int)n);
            times.Add(stamp is long s ? s * cfg.TimeMultiplier / stampUnit : rateCursor.TimeOf(n));
            for (int k = 0; k < na; k++)
            {
                double x = a(k);
                if (double.IsNaN(x)) { missing++; analog[k].Add(double.NaN); }
                else analog[k].Add(cfg.Analog[k].A * x + cfg.Analog[k].B);
            }
            for (int k = 0; k < nd; k++) digital[k].Add(d(k));
        }

        if (cfg.FileType == ComtradeFileType.Ascii)
        {
            // IO-13: a C37.111-1991 ASCII data file may end in the DOS end-of-file mark (0x1A); it is not data.
            string asciiText = Encoding.ASCII.GetString(data).TrimEnd().TrimEnd('\u001A');
            string[] lines = asciiText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                string[] f = lines[i].Split(',').Select(x => x.Trim()).ToArray();
                if (f.Length != 2 + na + nd)
                    throw new InvalidDataException($"COMTRADE data line {i + 1}: {f.Length} fields; the configuration has {na} analog and {nd} status channels, so {2 + na + nd} are expected.");
                string pos() => $"COMTRADE data line {i + 1}";
                long n = ToLong(f[0], "the sample number", pos());
                long? stamp = f[1].Length == 0 ? null : ToLong(f[1], "the time stamp", pos());
                // IO-11 (ruling 19 and R2-14; follows C37.111, not checked against the standard's text): an empty analog
                // field, and 99999 in a file of revision 1999 or later, mark a missing value; a 1991 file's 99999 is data.
                Add(n, stamp, k => f[2 + k].Length == 0 || (cfg.RevisionYear >= 1999 && f[2 + k] == "99999")
                        ? double.NaN : ToReal(f[2 + k], $"analog channel {k + 1}", pos()),
                    k => ToInt(f[2 + na + k], $"status channel {k + 1}", pos()) != 0);
            }
        }
        else
        {
            int aBytes = cfg.FileType == ComtradeFileType.Binary ? 2 : 4;
            int words = (nd + 15) / 16;
            // IO-08: the record size in long arithmetic (in int it could wrap).
            long sizeLong = 8L + (long)aBytes * na + 2L * words;
            if (sizeLong > int.MaxValue)
                throw new InvalidDataException($"COMTRADE data file: {na} analog and {nd} status channels make a sample of {sizeLong} bytes, too large to read.");
            int size = (int)sizeLong;
            if (data.Length % size != 0)
                throw new InvalidDataException($"COMTRADE data file: {data.Length} bytes is not a whole number of {size}-byte samples ({na} analog and {nd} status channels).");
            for (int off = 0; off < data.Length; off += size)
            {
                ReadOnlySpan<byte> rec = data.AsSpan(off, size);
                uint n = BinaryPrimitives.ReadUInt32LittleEndian(rec);
                uint stamp = BinaryPrimitives.ReadUInt32LittleEndian(rec[4..]);
                Add(n, stamp == uint.MaxValue ? (long?)null : stamp, k =>
                {
                    ReadOnlySpan<byte> r = data.AsSpan(off, size);
                    int p = 8 + aBytes * k;
                    // IO-11 (ruling 19 and R2-14; follows C37.111, not checked against the standard's text): the most negative
                    // word (0x8000, 0x80000000) is the missing-data code in a file of revision 1999 or later; in a 1991
                    // BINARY file it is data.
                    return cfg.FileType switch
                    {
                        ComtradeFileType.Binary => BinaryPrimitives.ReadInt16LittleEndian(r[p..]) is var w16 && w16 == short.MinValue && cfg.RevisionYear >= 1999 ? double.NaN : w16,
                        ComtradeFileType.Binary32 => BinaryPrimitives.ReadInt32LittleEndian(r[p..]) is var w32 && w32 == int.MinValue && cfg.RevisionYear >= 1999 ? double.NaN : w32,
                        _ => (double)BinaryPrimitives.ReadSingleLittleEndian(r[p..]),
                    };
                }, k =>
                {
                    ReadOnlySpan<byte> r = data.AsSpan(off, size);
                    ushort w = BinaryPrimitives.ReadUInt16LittleEndian(r[(8 + aBytes * na + 2 * (k / 16))..]);
                    return ((w >> (k % 16)) & 1) != 0;
                });
            }
        }
        for (int i = 0; i < times.Count; i++)
            if (double.IsNaN(times[i])) throw new InvalidDataException("COMTRADE data: a sample has no time stamp and the configuration gives no constant sampling rate.");
        return new ComtradeRecording(cfg, numbers, times, analog.Select(l => (IReadOnlyList<double>)l).ToList(), digital.Select(l => (IReadOnlyList<bool>)l).ToList(), missing);
    }

    /// <summary>R2-08 / R4-04: <see cref="RateTime"/> in O(log rates) per sample whatever the order of the sample numbers: the
    /// start time and the previous segment's last sample of every segment are computed once, with the same arithmetic
    /// <see cref="RateTime"/> uses (so the times are the same bits), and a sample's segment is found by binary search on the
    /// running maximum of the segments' last samples (the first segment whose last sample is at or above n, as
    /// <see cref="RateTime"/> walks to it). A segment at or after the first rate of 0 has no time (NaN), as before. A walk
    /// from the first segment for every sample was samples x rates; a sample number that went back, or a rate of 0 before
    /// the sample's segment, fell back to that walk.</summary>
    private sealed class RateCursor
    {
        private readonly IReadOnlyList<ComtradeSampleRate> _rates;
        private readonly double[] _start;      // the time of the last sample of segment k - 1 (0 for the first)
        private readonly long[] _previousLast; // that sample's number (0 for the first)
        private readonly long[] _maxLast;      // the running maximum of LastSample up to k
        private readonly int _firstZero;       // the first segment whose rate is not above 0 (Count when none)

        public RateCursor(IReadOnlyList<ComtradeSampleRate> rates)
        {
            _rates = rates;
            int c = rates.Count;
            _start = new double[c];
            _previousLast = new long[c];
            _maxLast = new long[c];
            _firstZero = c;
            double t = 0;
            long previousLast = 0, max = long.MinValue;
            for (int k = 0; k < c; k++)
            {
                _start[k] = t;
                _previousLast[k] = previousLast;
                max = Math.Max(max, rates[k].LastSample);
                _maxLast[k] = max;
                double r = rates[k].RateHz;
                if (!(r > 0) && _firstZero == c) _firstZero = k;
                if (_firstZero == c)
                    t = k == 0 ? (rates[k].LastSample - 1) / r : t + (rates[k].LastSample - previousLast) / r;
                previousLast = rates[k].LastSample;
            }
        }

        public double TimeOf(long n)
        {
            int c = _rates.Count;
            if (c == 0) return double.NaN;
            int lo = 0, hi = c - 1;   // the first k with _maxLast[k] >= n, else the last segment
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (_maxLast[mid] >= n) hi = mid; else lo = mid + 1;
            }
            int k = lo;
            if (k >= _firstZero) return double.NaN;
            double rk = _rates[k].RateHz;
            return k == 0 ? (n - 1) / rk : _start[k] + (n - _previousLast[k]) / rk;
        }
    }

    /// <summary>IO-11: the time of sample <paramref name="n"/> (1 upward) from the sampling rates: each rate line covers the
    /// samples after the previous line's last sample up to its own, so a recording with several rates (fast around a fault,
    /// slow after) is timed segment by segment. NaN when the segment's rate is 0 (the time stamps must carry the timing).</summary>
    private static double RateTime(IReadOnlyList<ComtradeSampleRate> rates, long n)
    {
        double t = 0;
        long previousLast = 0;
        for (int k = 0; k < rates.Count; k++)
        {
            double r = rates[k].RateHz;
            bool last = k == rates.Count - 1;
            if (n <= rates[k].LastSample || last)
            {
                if (!(r > 0)) return double.NaN;
                return k == 0 ? (n - 1) / r : t + (n - previousLast) / r;
            }
            if (!(r > 0)) return double.NaN;
            t = k == 0 ? (rates[k].LastSample - 1) / r : t + (rates[k].LastSample - previousLast) / r;
            previousLast = rates[k].LastSample;
        }
        return double.NaN;
    }

    /// <summary>The number of decimal places of the seconds in a <c>dd/mm/yyyy,hh:mm:ss.sss...</c> time.</summary>
    private static int FractionDigits(string time)
    {
        int dot = time.LastIndexOf('.');
        if (dot < 0) return 0;
        int digits = 0;
        for (int i = dot + 1; i < time.Length && char.IsAsciiDigit(time[i]); i++) digits++;
        return digits;
    }

    private static int CountOf(string field, char suffix, string pos)
    {
        string t = field.Trim();
        if (t.Length < 2 || char.ToUpperInvariant(t[^1]) != suffix) throw new InvalidDataException($"{pos}: '{field}' must be a count followed by {suffix}.");
        return ToInt(t[..^1], "a channel count", pos);
    }

    private static int ToInt(string s, string what, string pos)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) throw new InvalidDataException($"{pos}: {what} is '{s}'; it must be a whole number.");
        return v;
    }

    private static long ToLong(string s, string what, string pos)
    {
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) throw new InvalidDataException($"{pos}: {what} is '{s}'; it must be a whole number.");
        return v;
    }

    private static double ToReal(string s, string what, string pos)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new InvalidDataException($"{pos}: {what} is '{s}'; it must be a finite number.");
        return v;
    }
}

/// <summary>One analog series to write: a name, a phase label, a unit and a value per sample.</summary>
public sealed record ComtradeSeriesChannel(string Id, string Phase, string Unit, IReadOnlyList<double> Values);

/// <summary>One status series to write.</summary>
public sealed record ComtradeSeriesStatus(string Id, IReadOnlyList<bool> Values);

/// <summary>A simulation trajectory ready to be written: the sample times (seconds, strictly increasing) and the channels.</summary>
public sealed record ComtradeSeries(string Station, string Device, IReadOnlyList<double> TimesSeconds,
    IReadOnlyList<ComtradeSeriesChannel> Analog, IReadOnlyList<ComtradeSeriesStatus> Status);

/// <summary>How a COMTRADE recording is written.</summary>
public sealed record ComtradeWriteOptions
{
    /// <summary>The standard revision named in the configuration file: 1999 (the default) or 2013.</summary>
    public int RevisionYear { get; init; } = 1999;

    /// <summary>How the data file stores samples. ASCII (the default) keeps every value exactly; BINARY and BINARY32 scale each
    /// channel to its integer range; FLOAT32 stores single precision (2013 only).</summary>
    public ComtradeFileType FileType { get; init; } = ComtradeFileType.Ascii;

    /// <summary>The system frequency written to the configuration file (default 50).</summary>
    public double LineFrequencyHz { get; init; } = 50.0;

    /// <summary>The clock time of the first sample, written as the start and trigger times. The writer never reads the clock.</summary>
    public DateTime StartUtc { get; init; } = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}

/// <summary>The text of a configuration file and the bytes of its data file.</summary>
public sealed record ComtradeFiles(string Config, byte[] Data);

/// <summary>
/// Writes a simulation trajectory as an IEEE C37.111 recording (1999 or 2013 revision), ASCII or binary.
/// </summary>
/// <remarks>
/// Times are written relative to the first sample, in microseconds multiplied by the time multiplier (1 unless the
/// spacing is finer than a microsecond, then a power of ten below 1). A constant spacing is written as one sampling rate;
/// otherwise the rate is 0 and the time stamps carry the timing. ASCII writes each value exactly with a = 1, b = 0; BINARY
/// and BINARY32 give each channel a = peak / full scale and b = 0, so a value is stored to within a / 2; FLOAT32 stores the
/// nearest single-precision value. The output depends only on the series and the options.
/// </remarks>
public static class ComtradeWriter
{
    private static readonly double[] Multipliers = { 1.0, 0.1, 0.01, 0.001, 0.0001, 0.00001, 0.000001 };

    /// <summary>Write the series to a configuration text and a data file's bytes.</summary>
    public static ComtradeFiles Write(ComtradeSeries series, ComtradeWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        options ??= new ComtradeWriteOptions();
        if (options.RevisionYear is not (1999 or 2013))
            throw new ArgumentOutOfRangeException(nameof(options), options.RevisionYear, "The revision year must be 1999 or 2013.");
        if ((options.FileType is ComtradeFileType.Binary32 or ComtradeFileType.Float32) && options.RevisionYear < 2013)
            throw new ArgumentOutOfRangeException(nameof(options), options.FileType, "BINARY32 and FLOAT32 are C37.111-2013 file types.");
        if (!(options.LineFrequencyHz > 0) || !double.IsFinite(options.LineFrequencyHz))
            throw new ArgumentOutOfRangeException(nameof(options), options.LineFrequencyHz, "The line frequency must be a positive, finite number.");
        int n = series.TimesSeconds.Count;
        if (n == 0) throw new ArgumentException("A recording needs at least one sample.", nameof(series));
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(series.TimesSeconds[i])) throw new ArgumentException($"Sample time {i + 1} is not finite.", nameof(series));
            if (i > 0 && !(series.TimesSeconds[i] > series.TimesSeconds[i - 1])) throw new ArgumentException($"Sample times must increase strictly; sample {i + 1} does not.", nameof(series));
        }
        foreach (ComtradeSeriesChannel c in series.Analog)
        {
            CheckName(c.Id, "channel id"); CheckName(c.Phase, "phase"); CheckName(c.Unit, "unit");
            if (c.Values.Count != n) throw new ArgumentException($"Channel '{c.Id}' has {c.Values.Count} values for {n} samples.", nameof(series));
            if (c.Values.Any(v => !double.IsFinite(v))) throw new ArgumentException($"Channel '{c.Id}' has a value that is not finite.", nameof(series));
        }
        foreach (ComtradeSeriesStatus c in series.Status)
        {
            CheckName(c.Id, "channel id");
            if (c.Values.Count != n) throw new ArgumentException($"Status channel '{c.Id}' has {c.Values.Count} values for {n} samples.", nameof(series));
        }
        CheckName(series.Station, "station"); CheckName(series.Device, "device");

        double t0 = series.TimesSeconds[0];
        double minDt = double.MaxValue;
        for (int i = 1; i < n; i++) minDt = Math.Min(minDt, series.TimesSeconds[i] - series.TimesSeconds[i - 1]);
        double timemult = 1.0;
        if (n > 1) foreach (double m in Multipliers) { timemult = m; if (minDt * 1e6 / m >= 1.0 - 1e-9) break; }
        double rateHz = 0;
        if (n > 1)
        {
            double dt = series.TimesSeconds[1] - series.TimesSeconds[0];
            bool uniform = true;
            for (int i = 2; i < n && uniform; i++)
                uniform = Math.Abs((series.TimesSeconds[i] - series.TimesSeconds[i - 1]) - dt) <= 1e-9 * dt;
            if (uniform) rateHz = 1.0 / dt;
        }

        int na = series.Analog.Count, nd = series.Status.Count;
        // Per-channel scale: ASCII exact (a = 1), BINARY and BINARY32 by the channel's peak, FLOAT32 a = 1 (single precision stored).
        var scale = new double[na];
        var lo = new double[na];
        var hi = new double[na];
        for (int k = 0; k < na; k++)
        {
            double peak = 0;
            foreach (double v in series.Analog[k].Values) peak = Math.Max(peak, Math.Abs(v));
            double full = options.FileType == ComtradeFileType.Binary ? 32767.0 : options.FileType == ComtradeFileType.Binary32 ? 2147483647.0 : 0.0;
            scale[k] = full > 0 && peak > 0 ? peak / full : 1.0;
            lo[k] = double.MaxValue; hi[k] = double.MinValue;
            foreach (double v in series.Analog[k].Values)
            {
                double stored = Stored(v, scale[k], options.FileType);
                lo[k] = Math.Min(lo[k], stored); hi[k] = Math.Max(hi[k], stored);
            }
        }

        var cfg = new StringBuilder();
        string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        cfg.Append(series.Station).Append(',').Append(series.Device).Append(',').Append(options.RevisionYear.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        cfg.Append(FormattableString.Invariant($"{na + nd},{na}A,{nd}D")).Append("\r\n");
        for (int k = 0; k < na; k++)
        {
            ComtradeSeriesChannel c = series.Analog[k];
            cfg.Append(FormattableString.Invariant($"{k + 1},{c.Id},{c.Phase},,{c.Unit},{Inv(scale[k])},0,0,{Inv(lo[k])},{Inv(hi[k])},1,1,P")).Append("\r\n");
        }
        for (int k = 0; k < nd; k++)
            cfg.Append(FormattableString.Invariant($"{k + 1},{series.Status[k].Id},,,0")).Append("\r\n");
        cfg.Append(Inv(options.LineFrequencyHz)).Append("\r\n");
        if (rateHz > 0) cfg.Append("1\r\n").Append(Inv(rateHz)).Append(',').Append(n.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        else cfg.Append("0\r\n").Append("0,").Append(n.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        string start = options.StartUtc.ToString("dd/MM/yyyy,HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        cfg.Append(start).Append("\r\n").Append(start).Append("\r\n");
        cfg.Append(options.FileType switch
        {
            ComtradeFileType.Ascii => "ASCII",
            ComtradeFileType.Binary => "BINARY",
            ComtradeFileType.Binary32 => "BINARY32",
            _ => "FLOAT32",
        }).Append("\r\n");
        cfg.Append(Inv(timemult)).Append("\r\n");
        if (options.RevisionYear >= 2013) cfg.Append("0,0\r\n0,0\r\n");   // time code and local code; time quality and leap second

        byte[] data;
        if (options.FileType == ComtradeFileType.Ascii)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(',').Append(Stamp(series.TimesSeconds[i] - t0, timemult).ToString(CultureInfo.InvariantCulture));
                for (int k = 0; k < na; k++) sb.Append(',').Append(Inv(series.Analog[k].Values[i]));
                for (int k = 0; k < nd; k++) sb.Append(',').Append(series.Status[k].Values[i] ? '1' : '0');
                sb.Append("\r\n");
            }
            data = Encoding.ASCII.GetBytes(sb.ToString());
        }
        else
        {
            int aBytes = options.FileType == ComtradeFileType.Binary ? 2 : 4;
            int words = (nd + 15) / 16;
            int size = 8 + aBytes * na + 2 * words;
            data = new byte[size * n];
            for (int i = 0; i < n; i++)
            {
                Span<byte> rec = data.AsSpan(i * size, size);
                BinaryPrimitives.WriteUInt32LittleEndian(rec, (uint)(i + 1));
                BinaryPrimitives.WriteUInt32LittleEndian(rec[4..], checked((uint)Stamp(series.TimesSeconds[i] - t0, timemult)));
                for (int k = 0; k < na; k++)
                {
                    double v = series.Analog[k].Values[i];
                    Span<byte> at = rec[(8 + aBytes * k)..];
                    switch (options.FileType)
                    {
                        case ComtradeFileType.Binary: BinaryPrimitives.WriteInt16LittleEndian(at, (short)Stored(v, scale[k], options.FileType)); break;
                        case ComtradeFileType.Binary32: BinaryPrimitives.WriteInt32LittleEndian(at, (int)Stored(v, scale[k], options.FileType)); break;
                        default: BinaryPrimitives.WriteSingleLittleEndian(at, (float)v); break;
                    }
                }
                for (int w = 0; w < words; w++)
                {
                    ushort word = 0;
                    for (int b = 0; b < 16 && w * 16 + b < nd; b++)
                        if (series.Status[w * 16 + b].Values[i]) word |= (ushort)(1 << b);
                    BinaryPrimitives.WriteUInt16LittleEndian(rec[(8 + aBytes * na + 2 * w)..], word);
                }
            }
        }
        return new ComtradeFiles(cfg.ToString(), data);
    }

    /// <summary>Write the series to <paramref name="stemPath"/> with the extensions .cfg and .dat. A file that exists is never overwritten.</summary>
    public static void WriteFiles(string stemPath, ComtradeSeries series, ComtradeWriteOptions? options = null)
    {
        ComtradeFiles f = Write(series, options);
        string cfgPath = stemPath + ".cfg", datPath = stemPath + ".dat";
        using (var cfg = new FileStream(cfgPath, FileMode.CreateNew, FileAccess.Write)) cfg.Write(Encoding.ASCII.GetBytes(f.Config));
        try { using var dat = new FileStream(datPath, FileMode.CreateNew, FileAccess.Write); dat.Write(f.Data); }
        catch { File.Delete(cfgPath); throw; }
    }

    private static long Stamp(double seconds, double timemult) => (long)Math.Round(seconds * 1e6 / timemult, MidpointRounding.AwayFromZero);

    /// <summary>The number the data file stores for <paramref name="v"/>: the scaled integer for BINARY and BINARY32, the value for the rest.</summary>
    private static double Stored(double v, double scale, ComtradeFileType type) =>
        type is ComtradeFileType.Binary or ComtradeFileType.Binary32 ? Math.Round(v / scale, MidpointRounding.AwayFromZero)
        : type == ComtradeFileType.Float32 ? (double)(float)v : v;

    private static void CheckName(string s, string what)
    {
        if (s is null || s.Contains(',', StringComparison.Ordinal) || s.Contains('\n', StringComparison.Ordinal) || s.Contains('\r', StringComparison.Ordinal))
            throw new ArgumentException($"The {what} '{s}' must be text with no comma or line break.");
    }
}
