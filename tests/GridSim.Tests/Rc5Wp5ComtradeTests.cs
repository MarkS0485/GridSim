using System.Buffers.Binary;
using System.Text;
using GridSim.Core.IO.Comtrade;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 parity programme, WP5: COMTRADE (IEEE C37.111-1999 and -2013) read and write.
/// <para>The fixtures are written here from the layout the standard gives (configuration file lines and their field order, ASCII
/// data lines, the binary sample record of 4-byte sample number, 4-byte time stamp, two or four bytes per analog channel and a
/// 16-bit word per sixteen status channels); no vendor recording is used. Every expected value is the number the fixture
/// was written with, or the arithmetic of the standard on it (value = a x + b; time = stamp x time multiplier microseconds).</para>
/// </summary>
public class Rc5Wp5ComtradeTests
{
    private const string Cfg1999 =
        "SUBSTATION A,DEV 1,1999\r\n4,2A,2D\r\n" +
        "1,IA,A,,A,0.5,0.25,0,-32768,32767,100,5,P\r\n" +
        "2,VA,A,,V,2.0,-1.0,10,-32768,32767,11000,110,S\r\n" +
        "1,TRIP,,,0\r\n2,BRK,,,1\r\n" +
        "50\r\n1\r\n1000,3\r\n01/02/2003,04:05:06.123456\r\n01/02/2003,04:05:06.223456\r\n";

    private static void AssertSameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual), $"{what}: expected {expected:R}, got {actual:R}");

    private static void AssertExpectedSample(ComtradeRecording r)
    {
        Assert.Equal(new[] { 1, 2, 3 }, r.SampleNumbers);
        Assert.Equal(new[] { 0.0, 0.001, 0.002 }, r.TimesSeconds);
        Assert.Equal(new[] { 50.25, 50.75, 51.25 }, r.Analog[0]);
        Assert.Equal(new[] { -401.0, -399.0, -397.0 }, r.Analog[1]);
        Assert.Equal(new[] { false, true, true }, r.Digital[0]);
        Assert.Equal(new[] { true, true, false }, r.Digital[1]);
    }

    [Fact]
    public void A_1999_configuration_file_is_read_field_by_field()
    {
        ComtradeConfig c = ComtradeReader.ParseConfig(Cfg1999 + "ASCII\r\n1\r\n");
        Assert.Equal(("SUBSTATION A", "DEV 1", 1999), (c.Station, c.Device, c.RevisionYear));
        Assert.Equal(2, c.Analog.Count);
        ComtradeAnalogChannel a = c.Analog[0], v = c.Analog[1];
        Assert.Equal((1, "IA", "A", "", "A", 0.5, 0.25, 0.0, -32768.0, 32767.0, 100.0, 5.0, 'P'),
            (a.Number, a.Id, a.Phase, a.Component, a.Unit, a.A, a.B, a.SkewMicroseconds, a.Min, a.Max, a.Primary, a.Secondary, a.PrimarySecondary));
        Assert.Equal((2, "VA", 2.0, -1.0, 10.0, 11000.0, 110.0, 'S'), (v.Number, v.Id, v.A, v.B, v.SkewMicroseconds, v.Primary, v.Secondary, v.PrimarySecondary));
        Assert.Equal(new[] { ("TRIP", 0), ("BRK", 1) }, c.Digital.Select(d => (d.Id, d.NormalState)).ToArray());
        Assert.Equal(50.0, c.LineFrequencyHz);
        Assert.Equal((1000.0, 3), (c.Rates[0].RateHz, c.Rates[0].LastSample));
        Assert.Equal("01/02/2003,04:05:06.123456", c.Start);
        Assert.Equal("01/02/2003,04:05:06.223456", c.Trigger);
        Assert.Equal((ComtradeFileType.Ascii, 1.0), (c.FileType, c.TimeMultiplier));
    }

    [Fact]
    public void An_ascii_data_file_is_read_with_the_standards_scaling()
    {
        string dat = "1,0,100,-200,0,1\r\n2,1000,101,-199,1,1\r\n3,2000,102,-198,1,0\r\n";
        ComtradeRecording r = ComtradeReader.Parse(Cfg1999 + "ASCII\r\n1\r\n", Encoding.ASCII.GetBytes(dat));
        AssertExpectedSample(r);
    }

    private static byte[] BinarySamples(int analogBytes, params (uint N, uint T, long A1, long A2, ushort Word)[] samples)
    {
        using var ms = new MemoryStream();
        foreach (var s in samples)
        {
            var b = new byte[8 + 2 * analogBytes + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(b, s.N);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), s.T);
            if (analogBytes == 2)
            {
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(8), (short)s.A1);
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(10), (short)s.A2);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), (int)s.A1);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), (int)s.A2);
            }
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8 + 2 * analogBytes), s.Word);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    [Fact]
    public void A_binary_data_file_is_read_with_the_same_values_as_its_ascii_twin()
    {
        // Status word: channel 1 is bit 0 and channel 2 bit 1: samples (0,1), (1,1), (1,0).
        byte[] dat = BinarySamples(2, (1, 0, 100, -200, 0b10), (2, 1000, 101, -199, 0b11), (3, 2000, 102, -198, 0b01));
        AssertExpectedSample(ComtradeReader.Parse(Cfg1999 + "BINARY\r\n1\r\n", dat));
    }

    [Fact]
    public void A_binary32_data_file_is_read_whole_32_bit_integers()
    {
        string cfg = Cfg1999.Replace("1999\r\n4,2A,2D", "2013\r\n4,2A,2D", StringComparison.Ordinal) + "BINARY32\r\n1\r\n0,0\r\n0,0\r\n";
        byte[] dat = BinarySamples(4, (1, 0, 100000, -200000, 0b10), (2, 1000, 100001, -199999, 0b11), (3, 2000, 100002, -199998, 0b01));
        ComtradeRecording r = ComtradeReader.Parse(cfg, dat);
        Assert.Equal(new[] { 0.5 * 100000 + 0.25, 0.5 * 100001 + 0.25, 0.5 * 100002 + 0.25 }, r.Analog[0]);
        Assert.Equal(2013, r.Config.RevisionYear);
        Assert.Equal(ComtradeFileType.Binary32, r.Config.FileType);
    }

    [Fact]
    public void A_float32_data_file_gives_the_single_precision_values_exactly()
    {
        string cfg = Cfg1999.Replace("0.5,0.25", "1,0", StringComparison.Ordinal).Replace("2.0,-1.0", "1,0", StringComparison.Ordinal)
            .Replace("1999\r\n4,2A,2D", "2013\r\n4,2A,2D", StringComparison.Ordinal) + "FLOAT32\r\n1\r\n";
        using var ms = new MemoryStream();
        foreach (float v in new[] { 1.5f, -2.25f, 0.1f })
        {
            var b = new byte[8 + 8 + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 5);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), v);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), -v);
            ms.Write(b);
        }
        ComtradeRecording r = ComtradeReader.Parse(cfg, ms.ToArray());
        Assert.Equal(new[] { 1.5, -2.25, (double)0.1f }, r.Analog[0]);
        Assert.Equal(new[] { -1.5, 2.25, -(double)0.1f }, r.Analog[1]);
    }

    [Fact]
    public void A_1991_configuration_with_no_year_digital_lines_of_three_fields_and_no_time_multiplier_is_read()
    {
        const string cfg = "OLD STATION,OLD DEV\r\n3,1A,2D\r\n1,IA,A,,A,1,0,0,0,4095\r\n1,TRIP,0\r\n2,BRK,1\r\n60\r\n1\r\n600,2\r\n01/01/1995,00:00:00.000000\r\n01/01/1995,00:00:00.000000\r\nASCII\r\n";
        ComtradeRecording r = ComtradeReader.Parse(cfg, Encoding.ASCII.GetBytes("1,,10,0,1\r\n2,,11,1,1\r\n"));
        Assert.Equal((1991, 1.0), (r.Config.RevisionYear, r.Config.TimeMultiplier));
        Assert.Equal(1.0, r.Config.Analog[0].Primary);
        Assert.Equal(new[] { ("TRIP", 0), ("BRK", 1) }, r.Config.Digital.Select(d => (d.Id, d.NormalState)).ToArray());
        // No time stamp in the data: the time comes from the constant sampling rate, (n - 1) / rate.
        Assert.Equal(new[] { 0.0, 1.0 / 600.0 }, r.TimesSeconds);
        Assert.Equal(new[] { 10.0, 11.0 }, r.Analog[0]);
    }

    [Fact]
    public void A_time_multiplier_scales_the_time_stamps()
    {
        string cfg = Cfg1999 + "ASCII\r\n0.1\r\n";
        ComtradeRecording r = ComtradeReader.Parse(cfg, Encoding.ASCII.GetBytes("1,0,0,0,0,0\r\n2,10,0,0,0,0\r\n"));
        Assert.True(Math.Abs(1e-6 - r.TimesSeconds[1]) < 1e-18, $"time {r.TimesSeconds[1]:R}");
    }

    [Theory]
    [InlineData("4,2A,3D", "TT 4")]
    [InlineData("4,2,2D", "must be a count")]
    public void A_channel_count_line_that_disagrees_with_itself_is_refused(string line, string mentions)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ComtradeReader.ParseConfig(Cfg1999.Replace("4,2A,2D", line, StringComparison.Ordinal) + "ASCII\r\n1\r\n"));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_data_file_of_the_wrong_shape_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ComtradeReader.Parse(Cfg1999 + "ASCII\r\n1\r\n", Encoding.ASCII.GetBytes("1,0,100,-200,0\r\n")));
        Assert.Contains("data line 1", ex.Message, StringComparison.Ordinal);
        var ex2 = Assert.Throws<InvalidDataException>(() => ComtradeReader.Parse(Cfg1999 + "BINARY\r\n1\r\n", new byte[13]));
        Assert.Contains("whole number", ex2.Message, StringComparison.Ordinal);
        var ex3 = Assert.Throws<InvalidDataException>(() => ComtradeReader.ParseConfig(Cfg1999 + "TEXT\r\n1\r\n"));
        Assert.Contains("file type", ex3.Message, StringComparison.Ordinal);
    }

    // ---- Writing ---------------------------------------------------------------------------------------------------

    private static ComtradeSeries Series(int n, int status = 2)
    {
        double[] t = Enumerable.Range(0, n).Select(i => i / 100.0).ToArray();
        double[] ia = t.Select(x => 1234.5678 * (x * 7.0 - 0.3) + 1.0 / 3.0).ToArray();
        double[] vm = t.Select(x => 0.98765432101234 + 0.0123456789 * x).ToArray();
        var st = Enumerable.Range(0, status).Select(k => new ComtradeSeriesStatus("S" + (k + 1), Enumerable.Range(0, n).Select(i => ((i + k) % 3) == 0).ToArray())).ToArray();
        return new ComtradeSeries("STN", "SIM", t,
            new[] { new ComtradeSeriesChannel("IA", "A", "A", ia), new ComtradeSeriesChannel("VM", "", "pu", vm) }, st);
    }

    [Fact]
    public void The_configuration_text_has_the_standards_lines_in_the_standards_order()
    {
        var s = new ComtradeSeries("STN", "SIM", new[] { 0.0, 0.5, 1.0 },
            new[] { new ComtradeSeriesChannel("IA", "A", "kA", new[] { -3.0, 0.0, 3.0 }) },
            new[] { new ComtradeSeriesStatus("TRIP", new[] { false, true, true }) });
        ComtradeFiles f = ComtradeWriter.Write(s);
        Assert.Equal("STN,SIM,1999\r\n2,1A,1D\r\n1,IA,A,,kA,1,0,0,-3,3,1,1,P\r\n1,TRIP,,,0\r\n50\r\n1\r\n2,3\r\n" +
            "01/01/1970,00:00:00.000000\r\n01/01/1970,00:00:00.000000\r\nASCII\r\n1\r\n", f.Config);
        Assert.Equal("1,0,-3,0\r\n2,500000,0,1\r\n3,1000000,3,1\r\n", Encoding.ASCII.GetString(f.Data));
    }

    [Fact]
    public void An_ascii_recording_reads_back_bit_for_bit()
    {
        ComtradeSeries s = Series(25);
        ComtradeFiles f = ComtradeWriter.Write(s);
        ComtradeRecording r = ComtradeReader.Parse(f.Config, f.Data);
        Assert.Equal(25, r.SampleNumbers.Count);
        for (int i = 0; i < 25; i++)
        {
            AssertSameBits(s.TimesSeconds[i], r.TimesSeconds[i], $"time {i}");
            for (int k = 0; k < 2; k++) AssertSameBits(s.Analog[k].Values[i], r.Analog[k][i], $"channel {k} sample {i}");
            for (int k = 0; k < 2; k++) Assert.Equal(s.Status[k].Values[i], r.Digital[k][i]);
        }
        Assert.Equal(("IA", "A", "A"), (r.Config.Analog[0].Id, r.Config.Analog[0].Phase, r.Config.Analog[0].Unit));
        Assert.Equal(100.0, r.Config.Rates[0].RateHz, 9);
    }

    [Theory]
    [InlineData(ComtradeFileType.Binary, 1999)]
    [InlineData(ComtradeFileType.Binary32, 2013)]
    public void A_binary_recording_reads_back_to_within_half_a_count(ComtradeFileType type, int year)
    {
        ComtradeSeries s = Series(40);
        ComtradeFiles f = ComtradeWriter.Write(s, new ComtradeWriteOptions { FileType = type, RevisionYear = year });
        ComtradeRecording r = ComtradeReader.Parse(f.Config, f.Data);
        Assert.Equal(type, r.Config.FileType);
        for (int k = 0; k < 2; k++)
        {
            double count = r.Config.Analog[k].A;
            for (int i = 0; i < 40; i++)
                Assert.True(Math.Abs(s.Analog[k].Values[i] - r.Analog[k][i]) <= 0.5 * count * (1 + 1e-12), $"channel {k} sample {i}: {s.Analog[k].Values[i]:R} vs {r.Analog[k][i]:R}, count {count:R}");
        }
        for (int i = 0; i < 40; i++)
        {
            AssertSameBits(s.TimesSeconds[i], r.TimesSeconds[i], $"time {i}");
            Assert.Equal(s.Status[0].Values[i], r.Digital[0][i]);
        }
        Assert.Equal(f.Data.Length, 40 * (8 + 2 * (type == ComtradeFileType.Binary ? 2 : 4) + 2));
    }

    [Fact]
    public void A_float32_recording_stores_the_nearest_single_precision_value()
    {
        ComtradeSeries s = Series(10);
        ComtradeFiles f = ComtradeWriter.Write(s, new ComtradeWriteOptions { FileType = ComtradeFileType.Float32, RevisionYear = 2013 });
        ComtradeRecording r = ComtradeReader.Parse(f.Config, f.Data);
        for (int k = 0; k < 2; k++)
            for (int i = 0; i < 10; i++)
                AssertSameBits((double)(float)s.Analog[k].Values[i], r.Analog[k][i], $"channel {k} sample {i}");
        Assert.EndsWith("FLOAT32\r\n1\r\n0,0\r\n0,0\r\n", f.Config, StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_sixteen_status_channels_use_a_second_word()
    {
        ComtradeSeries s = Series(6, status: 19);
        ComtradeFiles f = ComtradeWriter.Write(s, new ComtradeWriteOptions { FileType = ComtradeFileType.Binary });
        Assert.Equal(6 * (8 + 4 + 4), f.Data.Length);
        ComtradeRecording r = ComtradeReader.Parse(f.Config, f.Data);
        for (int k = 0; k < 19; k++)
            for (int i = 0; i < 6; i++)
                Assert.Equal(s.Status[k].Values[i], r.Digital[k][i]);
    }

    [Fact]
    public void Uneven_sampling_is_written_with_rate_zero_and_the_time_stamps_carry_the_timing()
    {
        var s = new ComtradeSeries("STN", "SIM", new[] { 0.0, 0.001, 0.0035, 0.01 },
            new[] { new ComtradeSeriesChannel("IA", "A", "A", new[] { 1.0, 2.0, 3.0, 4.0 }) }, Array.Empty<ComtradeSeriesStatus>());
        ComtradeFiles f = ComtradeWriter.Write(s);
        Assert.Contains("\r\n0\r\n0,4\r\n", f.Config, StringComparison.Ordinal);
        ComtradeRecording r = ComtradeReader.Parse(f.Config, f.Data);
        Assert.Equal(new[] { 0.0, 0.001, 0.0035, 0.01 }, r.TimesSeconds);
    }

    [Fact]
    public void Spacing_finer_than_a_microsecond_uses_a_time_multiplier_below_one()
    {
        double[] t = Enumerable.Range(0, 5).Select(i => i * 1e-7).ToArray();
        var s = new ComtradeSeries("STN", "SIM", t, new[] { new ComtradeSeriesChannel("IA", "A", "A", new[] { 1.0, 2.0, 3.0, 4.0, 5.0 }) }, Array.Empty<ComtradeSeriesStatus>());
        ComtradeFiles f = ComtradeWriter.Write(s);
        ComtradeRecording r = ComtradeReader.Parse(f.Config, f.Data);
        Assert.Equal(0.1, r.Config.TimeMultiplier);
        for (int i = 0; i < 5; i++) Assert.Equal(t[i], r.TimesSeconds[i], 15);
    }

    [Fact]
    public void A_2013_configuration_carries_the_time_code_and_quality_lines_and_reads_back()
    {
        ComtradeFiles f = ComtradeWriter.Write(Series(3), new ComtradeWriteOptions { RevisionYear = 2013, LineFrequencyHz = 60, StartUtc = new DateTime(2026, 11, 22, 13, 14, 15, 678, DateTimeKind.Utc) });
        Assert.Contains("STN,SIM,2013\r\n", f.Config, StringComparison.Ordinal);
        Assert.Contains("22/11/2026,13:14:15.678000\r\n22/11/2026,13:14:15.678000\r\nASCII\r\n1\r\n0,0\r\n0,0\r\n", f.Config, StringComparison.Ordinal);
        ComtradeRecording r = ComtradeReader.Parse(f.Config, f.Data);
        Assert.Equal((2013, 60.0, "22/11/2026,13:14:15.678000"), (r.Config.RevisionYear, r.Config.LineFrequencyHz, r.Config.Start));
    }

    [Fact]
    public void The_same_series_writes_the_same_bytes_every_time()
    {
        ComtradeFiles a = ComtradeWriter.Write(Series(30), new ComtradeWriteOptions { FileType = ComtradeFileType.Binary });
        ComtradeFiles b = ComtradeWriter.Write(Series(30), new ComtradeWriteOptions { FileType = ComtradeFileType.Binary });
        Assert.Equal(a.Config, b.Config);
        Assert.Equal(a.Data, b.Data);
    }

    [Fact]
    public void A_series_the_standard_cannot_hold_is_refused()
    {
        ComtradeSeries ok = Series(4);
        Assert.Throws<ArgumentException>(() => ComtradeWriter.Write(ok with { TimesSeconds = new[] { 0.0, 0.1, 0.1, 0.3 } }));
        Assert.Throws<ArgumentException>(() => ComtradeWriter.Write(ok with { TimesSeconds = new[] { 0.0, 0.1 } }));
        Assert.Throws<ArgumentException>(() => ComtradeWriter.Write(ok with { Analog = new[] { new ComtradeSeriesChannel("A,B", "A", "A", new double[4]) } }));
        Assert.Throws<ArgumentException>(() => ComtradeWriter.Write(ok with { Analog = new[] { new ComtradeSeriesChannel("IA", "A", "A", new[] { 1.0, double.NaN, 1.0, 1.0 }) } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ComtradeWriter.Write(ok, new ComtradeWriteOptions { FileType = ComtradeFileType.Float32 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ComtradeWriter.Write(ok, new ComtradeWriteOptions { RevisionYear = 2000 }));
    }

    [Fact]
    public void Files_are_written_and_read_by_path_and_never_overwritten()
    {
        string dir = TestTemp.Under("gridsim-wp5-comtrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string stem = Path.Combine(dir, "run1");
            ComtradeSeries s = Series(12);
            ComtradeWriter.WriteFiles(stem, s, new ComtradeWriteOptions { FileType = ComtradeFileType.Binary });
            Assert.True(File.Exists(stem + ".cfg") && File.Exists(stem + ".dat"));
            ComtradeRecording r = ComtradeReader.ReadFiles(stem + ".cfg");
            Assert.Equal(12, r.SampleNumbers.Count);
            Assert.Throws<IOException>(() => ComtradeWriter.WriteFiles(stem, s));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
