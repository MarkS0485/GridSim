using System;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Distribution;
using GridSim.Core.Harmonics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF6 (N360): the gridsim-dist3ph/2 form holds the feeder's active-front-end converter units (the v1 "converters"
/// section of N354): v1 -> v2 -> model is bit-identical, the v2 text is byte-stable, a feeder without converters writes
/// no section (its document is unchanged), and malformed units are refused at their JSON path.
/// </summary>
public sealed class Cf6Dist3phV2ConvertersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-cf6-v2conv-" + Guid.NewGuid().ToString("N"));

    public Cf6Dist3phV2ConvertersTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string FeederPath(string name) => Path.Combine(CliProcess.Root(), "data", "dist3ph", name);

    // The CF5 construction (Cf5CliFollowUpTests.WithConverters): a bundled v1 feeder with a converters section appended.
    private static string WithConverters(string file, string converters)
    {
        string text = File.ReadAllText(FeederPath(file)).Replace("\r\n", "\n");
        int at = text.LastIndexOf('}');
        return text[..at].TrimEnd() + ",\n  \"converters\": " + converters + "\n}\n";
    }

    private const string Feeder = "ieee4-gyGy-stepdown-balanced.json";
    private const string TwoUnits =
        "[ { \"id\": \"afe4\", \"bus\": 4, \"kva\": 2000, \"xPercent\": 15, \"rPercent\": 1 }, { \"id\": \"afe3\", \"bus\": 3, \"kva\": 750, \"xPercent\": 12.5 } ]";

    private static MultiPhaseFeeder V1WithUnits() => MultiPhaseJsonLoader.Parse(WithConverters(Feeder, TwoUnits), "afe.json");

    private static string ToV2(MultiPhaseFeeder f) => MultiPhaseV2Writer.ToJson(f, MultiPhaseV2Writer.ConvertedWithoutProvenance("afe.json"));

    [Fact]
    public void A_v1_feeder_with_converters_round_trips_through_v2_bit_for_bit()
    {
        MultiPhaseFeeder v1 = V1WithUnits();
        Assert.Equal(2, v1.Converters.Count);
        string text = ToV2(v1);
        MultiPhaseFeeder back = MultiPhaseJsonLoader.ParseV2(text, "afe.v2.json");

        Assert.Null(MultiPhaseV2Writer.FirstDifference(v1, back));
        Assert.Equal(v1.ConverterIds, back.ConverterIds);
        for (int k = 0; k < v1.Converters.Count; k++)
        {
            ThreePhaseActiveFrontEnd a = v1.Converters[k], b = back.Converters[k];
            Assert.Equal(a.Bus, b.Bus);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.RatingMva), BitConverter.DoubleToInt64Bits(b.RatingMva));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.ReactancePu), BitConverter.DoubleToInt64Bits(b.ReactancePu));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.ResistancePu), BitConverter.DoubleToInt64Bits(b.ResistancePu));
        }
        // The v1 values are what the v2 form holds: kva / 1000 and percent / 100, computed once by the v1 reader.
        Assert.Equal(new ThreePhaseActiveFrontEnd(4, 2000 / 1000.0, 15 / 100.0, 1 / 100.0), back.Converters[0]);
        Assert.Equal(new ThreePhaseActiveFrontEnd(3, 750 / 1000.0, 12.5 / 100.0, 0.0), back.Converters[1]);
    }

    [Fact]
    public void The_v2_text_is_byte_stable_and_omits_a_zero_resistance()
    {
        string first = ToV2(V1WithUnits());
        MultiPhaseFeeder back = MultiPhaseJsonLoader.ParseV2(first, "afe.v2.json");
        string second = MultiPhaseV2Writer.ToJson(back);   // the read-back feeder carries its own provenance
        Assert.Equal(first, second);
        Assert.Contains("\"converters\": [", first, StringComparison.Ordinal);
        Assert.Contains("\"xOnRating\": 0.15", first, StringComparison.Ordinal);
        Assert.Contains("\"rOnRating\": 0.01", first, StringComparison.Ordinal);
        // afe3 has no resistance: the member is absent (the reader's absence rule gives exactly 0.0).
        int afe3 = first.IndexOf("\"afe3\"", StringComparison.Ordinal);
        Assert.True(afe3 > 0);
        Assert.DoesNotContain("rOnRating", first[afe3..], StringComparison.Ordinal);
    }

    [Fact]
    public void A_feeder_without_converters_writes_no_section()
    {
        MultiPhaseFeeder plain = MultiPhaseJsonLoader.Load(FeederPath(Feeder));
        Assert.Empty(plain.Converters);
        string text = ToV2(plain);
        Assert.DoesNotContain("converters", text, StringComparison.Ordinal);
        Assert.Empty(MultiPhaseJsonLoader.ParseV2(text, "plain.v2.json").Converters);
    }

    [Fact]
    public void Per_converter_provenance_is_read_and_written_back()
    {
        string text = ToV2(V1WithUnits());
        string withProv = text.Replace("\"id\": \"afe4\",", "\"id\": \"afe4\", \"prov\": { \"tag\": \"Recorded\", \"note\": \"nameplate\" },", StringComparison.Ordinal);
        Assert.NotEqual(text, withProv);
        MultiPhaseFeeder f = MultiPhaseJsonLoader.ParseV2(withProv, "prov.v2.json");
        Assert.Equal(ProvenanceTag.Recorded, f.Provenance!.For("converter:afe4").Tag);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, f.Provenance.For("converter:afe3").Tag);
        string again = MultiPhaseV2Writer.ToJson(f);
        Assert.Contains("\"note\": \"nameplate\"", again, StringComparison.Ordinal);
        Assert.Null(MultiPhaseV2Writer.FirstDifference(f, MultiPhaseJsonLoader.ParseV2(again, "again.v2.json")));
    }

    [Theory]
    [InlineData("\"bus\": 4,", "\"bus\": 9,", "$.converters[0].bus")]
    [InlineData("\"xOnRating\": 0.15", "\"xOnRating\": 0", "xOnRating")]
    [InlineData("\"ratingMva\": 2,", "\"ratingMva\": 0,", "ratingMva")]
    [InlineData("\"rOnRating\": 0.01", "\"rOnRating\": -0.01", "rOnRating")]
    [InlineData("\"id\": \"afe3\"", "\"id\": \"afe4\"", "$.converters[1].id")]
    [InlineData("\"xOnRating\": 0.15", "\"xOnRating\": 0.15, \"kva\": 2000", "kva")]
    public void Malformed_v2_converter_units_are_refused_by_name(string find, string replace, string named)
    {
        string text = ToV2(V1WithUnits());
        Assert.Contains(find, text, StringComparison.Ordinal);
        int at = text.IndexOf("\"converters\"", StringComparison.Ordinal);
        string bad = text[..at] + ReplaceFirst(text[at..], find, replace);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.ParseV2(bad, "bad.v2.json"));
        Assert.Contains(named, ex.Message, StringComparison.Ordinal);
    }

    private static string ReplaceFirst(string s, string find, string replace)
    {
        int i = s.IndexOf(find, StringComparison.Ordinal);
        return s[..i] + replace + s[(i + find.Length)..];
    }

    [Fact]
    public void First_difference_sees_a_converter_change()
    {
        MultiPhaseFeeder a = V1WithUnits();
        MultiPhaseFeeder b = a with { Converters = new[] { a.Converters[0], a.Converters[1] with { ReactancePu = 0.126 } } };
        Assert.Contains("converter afe3: reactance", MultiPhaseV2Writer.FirstDifference(a, b), StringComparison.Ordinal);
        MultiPhaseFeeder c = a with { Converters = new[] { a.Converters[0] }, ConverterIds = new[] { a.ConverterIds[0] } };
        Assert.Contains("converter count: 2 vs 1", MultiPhaseV2Writer.FirstDifference(a, c), StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_defines_the_converter_element()
    {
        string schema = File.ReadAllText(Path.Combine(CliProcess.Root(), "schemas", "v2", "dist3ph.schema.json"));
        Assert.Contains("\"converters\": { \"type\": \"array\", \"items\": { \"$ref\": \"#/$defs/converter\" } }", schema, StringComparison.Ordinal);
        Assert.Contains("\"required\": [\"id\", \"bus\", \"ratingMva\", \"xOnRating\"]", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_to_v2_writes_a_feeder_with_converters_and_harmonics_reads_its_units_back()
    {
        string v1 = Path.Combine(_dir, "afe.json");
        File.WriteAllText(v1, WithConverters(Feeder, "[ { \"id\": \"afe4\", \"bus\": 4, \"kva\": 2000, \"xPercent\": 15 } ]"));
        string v2 = Path.Combine(_dir, "afe.v2.json");
        CliProcess.Result toV2 = CliProcess.RunBuilt("dist3ph", v1, "to-v2", "--to", v2);
        Assert.True(toV2.ExitCode == 0, toV2.ToString());
        Assert.True(File.Exists(v2));

        CliProcess.Result fromV1 = CliProcess.RunBuilt("dist3ph", v1, "harmonics", "--bus", "4", "--spectrum", "6-pulse-g5", "--current", "0.1", "--afe", "feeder");
        CliProcess.Result fromV2 = CliProcess.RunBuilt("dist3ph", v2, "harmonics", "--bus", "4", "--spectrum", "6-pulse-g5", "--current", "0.1", "--afe", "feeder");
        Assert.True(fromV1.ExitCode == 0, fromV1.ToString());
        Assert.True(fromV2.ExitCode == 0, fromV2.ToString());
        const string line = "active-front-end converters in Y(h) (N354): 1 unit(s) from the feeder (afe4)";
        Assert.Contains(line, fromV1.StdOut, StringComparison.Ordinal);
        Assert.Contains(line, fromV2.StdOut, StringComparison.Ordinal);
        // The same model gives the same study: every line after the header that names the file is identical.
        string[] a = fromV1.StdOut.Split('\n').Where(l => !l.Contains("afe.", StringComparison.Ordinal)).ToArray();
        string[] b = fromV2.StdOut.Split('\n').Where(l => !l.Contains("afe.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(a, b);
    }
}
