using GridSim.Core.Presentation;
using GridSim.Ui.Geography;
using GridSim.Ui.Presentation;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 0.2: the colour arithmetic of the WPF <c>Visuals/Palette.cs</c> moved to the platform-neutral
/// <see cref="ColourRamps"/>, and the WPF palette now converts its results. The test project cannot reference WPF, so
/// the old code is copied below verbatim, with the WPF <c>Color</c> (four byte channels) replaced by a byte tuple; every
/// ramp is compared with it, byte for byte, over a dense sweep that includes every band edge, values outside the bands,
/// infinities and NaN.
/// </summary>
public sealed class UiColourRampsTests
{
    // ---------------- the old WPF Palette arithmetic, verbatim (1.95.478), on byte tuples ----------------

    private readonly record struct Old(byte A, byte R, byte G, byte B);

    private static Old OldFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 255, i = 0;
        if (hex.Length == 8) { a = Convert.ToByte(hex.Substring(0, 2), 16); i = 2; }
        byte r = Convert.ToByte(hex.Substring(i, 2), 16);
        byte g = Convert.ToByte(hex.Substring(i + 2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(i + 4, 2), 16);
        return new Old(a, r, g, b);
    }

    private static Old OldLerp(Old a, Old b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Old(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static readonly Old Blue = OldFromHex("#3FA9F5");
    private static readonly Old Green = OldFromHex("#2FD27F");
    private static readonly Old Amber = OldFromHex("#F3C13A");
    private static readonly Old Red = OldFromHex("#FF5B5B");

    private static Old OldVoltage(double vm)
    {
        if (vm <= 0.96) return Blue;
        if (vm < 1.00) return OldLerp(Blue, Green, (vm - 0.96) / 0.04);
        if (vm <= 1.03) return OldLerp(Green, Amber, (vm - 1.00) / 0.03);
        if (vm <= 1.06) return OldLerp(Amber, Red, (vm - 1.03) / 0.03);
        return Red;
    }

    private static Old OldLoading(double frac)
    {
        if (frac <= 0.6) return OldLerp(Green, Amber, frac / 0.6);
        if (frac <= 1.0) return OldLerp(Amber, Red, (frac - 0.6) / 0.4);
        return Red;
    }

    private static Old OldCondition(double idx01)
    {
        double t = Math.Clamp(idx01, 0, 1);
        return t <= 0.5 ? OldLerp(Green, Amber, t / 0.5) : OldLerp(Amber, Red, (t - 0.5) / 0.5);
    }

    private static Old OldStress(double frac)
    {
        double t = Math.Clamp(frac, 0, 1);
        return t <= 0.5 ? OldLerp(Blue, Amber, t / 0.5) : OldLerp(Amber, Red, (t - 0.5) / 0.5);
    }

    private static readonly string[] OldTubeLines =
    {
        "#E32017", "#0098D4", "#00782A", "#9B0056", "#F3A9BB", "#003688",
        "#EE7C0E", "#6950A1", "#B36305", "#00A4A7", "#FFD300", "#A0A5A9",
    };

    // ---------------- the comparisons ----------------

    private static void Same(Old want, UiColour got, string what) =>
        Assert.True(want.A == got.A && want.R == got.R && want.G == got.G && want.B == got.B,
            $"{what}: want ({want.A},{want.R},{want.G},{want.B}), got ({got.A},{got.R},{got.G},{got.B})");

    /// <summary>A dense sweep plus every band edge, the values a hair either side of it, and the non-finite values.</summary>
    private static IEnumerable<double> Inputs()
    {
        for (int i = -2000; i <= 4000; i++) yield return i / 1000.0;   // -2 .. 4 in steps of 0.001
        double[] edges = { 0, 0.5, 0.6, 0.94, 0.96, 1.0, 1.03, 1.06, 1.1, 1.2 };
        foreach (double e in edges)
        {
            yield return e;
            yield return Math.BitDecrement(e);
            yield return Math.BitIncrement(e);
        }
        yield return -0.0;
        yield return double.NaN;
        yield return double.PositiveInfinity;
        yield return double.NegativeInfinity;
        yield return double.Epsilon;
        yield return double.MaxValue;
        yield return double.MinValue;
    }

    [Fact]
    public void Every_ramp_is_the_old_wpf_arithmetic_byte_for_byte()
    {
        int n = 0;
        foreach (double x in Inputs())
        {
            Same(OldVoltage(x), ColourRamps.Voltage(x), $"Voltage({x:R})");
            Same(OldLoading(x), ColourRamps.Loading(x), $"Loading({x:R})");
            Same(OldCondition(x), ColourRamps.Condition(x), $"Condition({x:R})");
            Same(OldStress(x), ColourRamps.Stress(x), $"Stress({x:R})");
            n++;
        }
        Assert.True(n > 6000, $"the sweep ran {n} inputs");
    }

    [Fact]
    public void Lerp_hex_and_the_tube_palette_are_the_old_values()
    {
        string[] hexes = { "#3FA9F5", "2FD27F", "#80F3C13A", "#00000000", "#FFFFFFFF", "#8492A0", "#E0B341", "#4FC3F7" };
        foreach (string h in hexes) Same(OldFromHex(h), ColourRamps.FromHex(h), $"FromHex({h})");
        Assert.Throws<FormatException>(() => ColourRamps.FromHex("#GG0000"));
        Assert.Throws<FormatException>(() => OldFromHex("#GG0000"));   // the same refusal as before

        Old a = OldFromHex("#10203040"), b = OldFromHex("#F0E0D0C0");
        UiColour ua = ColourRamps.FromHex("#10203040"), ub = ColourRamps.FromHex("#F0E0D0C0");
        foreach (double t in new[] { -1.0, 0, 0.1, 0.25, 1.0 / 3, 0.5, 0.75, 0.999, 1, 2, double.NaN })
        {
            Same(OldLerp(a, b, t), ColourRamps.Lerp(ua, ub, t), $"Lerp(t={t:R})");
            Same(OldLerp(b, a, t), ColourRamps.Lerp(ub, ua, t), $"Lerp(reverse, t={t:R})");
        }

        Assert.Equal(OldTubeLines.Length, ColourRamps.TubeLines.Count);
        for (int i = 0; i < OldTubeLines.Length; i++) Same(OldFromHex(OldTubeLines[i]), ColourRamps.TubeLines[i], $"TubeLines[{i}]");
        Assert.Equal("#FF3FA9F5", ColourRamps.ToHex(ColourRamps.FromHex("#3FA9F5")));
        Assert.Equal("#80F3C13A", ColourRamps.ToHex(ColourRamps.FromHex("#80F3C13A")));
    }

    [Fact]
    public void The_moved_geography_loads_the_same_points_and_the_fuel_colour_the_old_brush_carried()
    {
        // GeographyData used to carry Palette.Frozen(Fuels.Classify(tech).Color) per asset, and Fuels.Classify(tech).Color
        // is Palette.FromHex(FuelTaxonomy.Classify(tech).Color): the neutral marker must carry exactly those bytes, and
        // every point must be the old equirectangular projection, bit for bit.
        string baseDir = Path.Combine(Path.GetTempPath(), $"gridsim-ui-geo-{Guid.NewGuid():N}");
        string geo = Path.Combine(baseDir, "gda-out", "geo");
        Directory.CreateDirectory(geo);
        try
        {
            File.WriteAllText(Path.Combine(geo, "dno_areas.geojson"),
                "{\"features\":[{\"properties\":{\"area\":\"North\",\"dno\":\"SSEN\"},\"geometry\":{\"type\":\"Polygon\"," +
                "\"coordinates\":[[[-4.25,57.5],[-3.0,58.1],[-2.1,57.0],[-4.25,57.5]]]}}]}");
            File.WriteAllText(Path.Combine(geo, "assets.geojson"),
                "{\"features\":[" +
                "{\"properties\":{\"technology_or_fuel\":\"Wind Onshore\",\"capacity_mw\":120.5,\"name\":\"A\",\"source\":\"s\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[-3.2,55.9]}}," +
                "{\"properties\":{\"technology_or_fuel\":\"CCGT\",\"capacity_mw\":900,\"name\":\"B\",\"source\":\"s\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[0.3,51.4]}}," +
                "{\"properties\":{\"technology_or_fuel\":\"hydrogen\",\"name\":\"C\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[-1.5,53.8]}}]}");
            var problems = new List<string>();
            GeographyData data = Assert.IsType<GeographyData>(GeographyData.Load(baseDir, problems));
            Assert.Empty(problems);
            Assert.Empty(data.Gsp);

            GeographyData.Region region = Assert.Single(data.Dno);
            Assert.Equal("North", region.Label);
            Assert.Equal("SSEN", region.Tag);
            WorldPoint[] ring = Assert.Single(region.Rings);
            double[][] coords = { new[] { -4.25, 57.5 }, new[] { -3.0, 58.1 }, new[] { -2.1, 57.0 }, new[] { -4.25, 57.5 } };
            Assert.Equal(coords.Length, ring.Length);
            for (int i = 0; i < coords.Length; i++) SamePoint(OldProject(coords[i][0], coords[i][1]), ring[i], $"ring point {i}");

            Assert.Equal(3, data.Assets.Count);
            (string Tech, double Lon, double Lat, double Mw)[] want = { ("Wind Onshore", -3.2, 55.9, 120.5), ("CCGT", 0.3, 51.4, 900), ("hydrogen", -1.5, 53.8, 0) };
            for (int i = 0; i < want.Length; i++)
            {
                GeographyData.AssetMarker a = data.Assets[i];
                Assert.Equal(want[i].Tech, a.Tech);
                SamePoint(OldProject(want[i].Lon, want[i].Lat), a.World, $"asset {i}");
                Same(OldFromHex(FuelTaxonomy.Classify(want[i].Tech).Color), a.Fill, $"asset {i} fill");
                Assert.Equal(BitConverter.DoubleToInt64Bits(want[i].Mw), BitConverter.DoubleToInt64Bits(a.Mw));
            }
            Assert.Equal(900.0, data.MaxAssetMw);
        }
        finally { try { Directory.Delete(baseDir, recursive: true); } catch (IOException) { /* best effort */ } }
    }

    // The old GeographyData.Project, verbatim (GB box -8.2..1.9 E, 49.8..59.0 N; y north-down).
    private static (double X, double Y) OldProject(double lon, double lat)
    {
        const double LonMin = -8.2, LonMax = 1.9, LatMin = 49.8, LatMax = 59.0;
        double x = (lon - LonMin) / (LonMax - LonMin);
        double yUp = (lat - LatMin) / (LatMax - LatMin);
        return (x, 1.0 - yUp);
    }

    private static void SamePoint((double X, double Y) want, WorldPoint got, string what)
    {
        Assert.True(BitConverter.DoubleToInt64Bits(want.X) == BitConverter.DoubleToInt64Bits(got.X), $"{what} x: want {want.X:R}, got {got.X:R}");
        Assert.True(BitConverter.DoubleToInt64Bits(want.Y) == BitConverter.DoubleToInt64Bits(got.Y), $"{what} y: want {want.Y:R}, got {got.Y:R}");
    }

    [Fact]
    public void A_world_point_carries_its_two_doubles_unchanged()
    {
        var p = new WorldPoint(-0.0, double.Epsilon);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(p.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(double.Epsilon), BitConverter.DoubleToInt64Bits(p.Y));
    }
}
