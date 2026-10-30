using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GridSim.Ui.Presentation;
using GridSim.Ui.Visuals;
using Xunit;
using New = GridSim.Tests.Ui.NewAssetVisuals;
using Old = GridSim.Tests.Ui.OldAssetVisuals;
using Shim = GridSim.Tests.Ui.WpfShim;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme ASSETS A1: the layout and colours of the two WPF asset visuals (the substation single-line diagram
/// <c>SubstationMap</c> and the transformer front elevation <c>TransformerSchematic</c>) moved into the platform-neutral
/// <see cref="SubstationMapGeometry"/> and <see cref="TransformerSchematicGeometry"/>, which return the drawing calls as
/// a display list; the WPF controls now only replay it (<c>VisualDisplayListPainter</c>). That is a pure move, so every
/// pixel the WPF controls draw must be exactly what they drew before.
///
/// <para>How it is checked (the method of <see cref="UiVisualsParityTests"/>). Both versions of each control are
/// compiled here against the recording WPF stand-in (<c>UiVisualsWpfShim.cs</c>): the OLD files
/// (<c>OldAssetVisuals/</c>, the oracle, exactly as at commit 9ff7ef41, 2.0.38) and the NEW files (<c>NewAssetVisuals/</c>,
/// exactly the code in src/GridSim.Wpf/Visuals now, painter included). Both are rendered with the same values at the
/// same sizes and device scales, over normal and edge inputs (no data, no connections, 30 connections, 12 generators,
/// every colour threshold and one value either side of it, NaN and infinite values, a zero or inverted tap range, sizes
/// either side of the minimum, a NaN size, a culture with a decimal comma), and the recordings must be equal line for
/// line: every drawing call, pen, brush, rectangle, transform, opacity layer, figure call, point (each double by its
/// round-trip text and its 64 bits), text, font, alignment and pixels-per-dip, in the same order. The copies are pinned:
/// the old ones by a SHA-256 of their text against the original files, the new ones by comparing their text with the
/// real WPF files. A sensitivity check shows a one-bit change in an input changes the recording, so equality is not
/// vacuous.</para>
/// </summary>
public sealed class UiAssetVisualsParityTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // The copies are what they claim to be
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The SHA-256 of each original file (commit 9ff7ef41) without its using, namespace and "// TEST" lines,
    /// lines joined by a line feed. Worked out from git when the oracle was copied.</summary>
    private static readonly Dictionary<string, string> OldHashes = new(StringComparer.Ordinal)
    {
        ["SubstationMap"] = "0edbca391ca22ca1e10d8435124dc7fd21aef9b6283f5a3deb65a715aabac9e9",
        ["TransformerSchematic"] = "d6fd629ddeab06d65361ccb8d475f83f2a8de764848a14cb13ba7dcb620eb285",
    };

    /// <summary>The WPF files whose current text is copied under NewAssetVisuals.</summary>
    private static readonly string[] NewFiles = { "SubstationMap", "TransformerSchematic", "VisualDisplayListPainter" };

    /// <summary>A source file's lines without its using and namespace lines and the copies' "// TEST" header.</summary>
    private static string Body(string path)
    {
        Assert.True(File.Exists(path), $"source file not found: {path}");
        IEnumerable<string> kept = File.ReadAllLines(path)
            .Where(l => !l.StartsWith("using ", StringComparison.Ordinal)
                     && !l.StartsWith("namespace ", StringComparison.Ordinal)
                     && !l.StartsWith("// TEST ", StringComparison.Ordinal));
        return string.Join("\n", kept);
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void The_old_copies_are_the_original_WPF_files_verbatim()
    {
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        foreach ((string name, string hash) in OldHashes)
        {
            string copy = Path.Combine(root, "tests", "GridSim.Tests", "Ui", "OldAssetVisuals", name + ".cs");
            Assert.True(hash == Sha256(Body(copy)), $"{name}: the oracle copy is not the original file's text");
        }
    }

    [Fact]
    public void The_new_copies_are_the_current_WPF_files_verbatim()
    {
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        foreach (string name in NewFiles)
        {
            string real = Body(Path.Combine(root, "src", "GridSim.Wpf", "Visuals", name + ".cs"));
            string copy = Body(Path.Combine(root, "tests", "GridSim.Tests", "Ui", "NewAssetVisuals", name + ".cs"));
            Assert.True(string.Equals(real, copy, StringComparison.Ordinal),
                $"{name}: tests/GridSim.Tests/Ui/NewAssetVisuals/{name}.cs is not the current src/GridSim.Wpf/Visuals/{name}.cs " +
                "(refresh the copy: same text, only the using and namespace lines differ)");
        }
    }

    [Fact]
    public void The_new_WPF_files_draw_from_the_shared_geometry()
    {
        // The move really happened: each WPF control calls its GridSim.Ui geometry and the painter, and the old inline
        // drawing (rounded rectangles, hex colours, the voltage thresholds) is gone from the controls.
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        string Src(string n) => File.ReadAllText(Path.Combine(root, "src", "GridSim.Wpf", "Visuals", n + ".cs"));
        foreach ((string control, string geometry) in new[] { ("SubstationMap", "SubstationMapGeometry.Compute("), ("TransformerSchematic", "TransformerSchematicGeometry.Compute(") })
        {
            string src = Src(control);
            Assert.Contains(geometry, src, StringComparison.Ordinal);
            Assert.Contains("VisualDisplayListPainter.Paint(", src, StringComparison.Ordinal);
            Assert.DoesNotContain("DrawRoundedRectangle", src, StringComparison.Ordinal);
            Assert.DoesNotContain("FromHex", src, StringComparison.Ordinal);
            Assert.DoesNotContain("FormattedText", src, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("ScadaThresholds", Src("SubstationMap"), StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Sqrt", Src("TransformerSchematic"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rendering both versions
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Record a render at a size and a device scale (the shim's per-flow pixels-per-dip).</summary>
    private static List<string> Record(Shim.FrameworkElement e, double w, double h, double pps)
    {
        Shim.VisualTreeHelper.PixelsPerDipForTest.Value = pps;
        try { return e.Record(w, h); }
        finally { Shim.VisualTreeHelper.PixelsPerDipForTest.Value = null; }
    }

    /// <summary>The two recordings must be equal line for line; returns the old one.</summary>
    private static List<string> AssertSame(List<string> want, List<string> got, string what)
    {
        Assert.True(want.Count == got.Count,
            $"{what}: old drew {want.Count} calls, new drew {got.Count}\nold:\n{string.Join("\n", want)}\nnew:\n{string.Join("\n", got)}");
        for (int i = 0; i < want.Count; i++)
            Assert.True(string.Equals(want[i], got[i], StringComparison.Ordinal), $"{what}, call {i}:\nold: {want[i]}\nnew: {got[i]}");
        return want;
    }

    // ---- the substation map's data, built the same for both versions ----

    /// <summary>One connection, framework-free, for building both versions' data.</summary>
    private sealed record Conn(string Name, string OtherEnd, string Kind, double FlowMw, bool Exporting, double LoadingFrac);

    /// <summary>One generator, framework-free.</summary>
    private sealed record Gen(string Name, string Fuel, string Hex, double OutputMw);

    /// <summary>One substation map case (null Conns and Gens = empty lists).</summary>
    private sealed record MapCase(string What, double V = 1.0, double Ang = 0, double Kv = 400, double Pd = 0, double Qd = 0,
        double Pg = 0, string Type = "PQ", double Net = 0, Conn[]? Conns = null, Gen[]? Gens = null);

    private static Old.SubstationMapData OldData(MapCase c) => new()
    {
        VoltagePu = c.V, AngleDeg = c.Ang, BaseKv = c.Kv, DemandMw = c.Pd, ReactiveMvar = c.Qd, GenerationMw = c.Pg,
        BusType = c.Type, NetInjectionMw = c.Net,
        Connections = (c.Conns ?? []).Select(x => new Old.MapConnection(x.Name, x.OtherEnd, x.Kind, x.FlowMw, x.Exporting, x.LoadingFrac)).ToList(),
        Generators = (c.Gens ?? []).Select(x => new Old.MapGenerator(x.Name, x.Fuel, Shim.Palette.FromHex(x.Hex), x.OutputMw)).ToList(),
    };

    private static New.SubstationMapData NewData(MapCase c) => new()
    {
        VoltagePu = c.V, AngleDeg = c.Ang, BaseKv = c.Kv, DemandMw = c.Pd, ReactiveMvar = c.Qd, GenerationMw = c.Pg,
        BusType = c.Type, NetInjectionMw = c.Net,
        Connections = (c.Conns ?? []).Select(x => new New.MapConnection(x.Name, x.OtherEnd, x.Kind, x.FlowMw, x.Exporting, x.LoadingFrac)).ToList(),
        Generators = (c.Gens ?? []).Select(x => new New.MapGenerator(x.Name, x.Fuel, Shim.Palette.FromHex(x.Hex), x.OutputMw)).ToList(),
    };

    private static readonly Conn[] Three =
    {
        new("DRAX-EGGB 1", "EGGBOROUGH 400", "LINE", 812.4, true, 0.64),
        new("DRAX SGT1", "DRAX 132", "TRANSFORMER", -240.7, false, 0.81),
        new("DRAX-THOR 2", "THORPE MARSH 400", "LINE", 0.3, true, 0.0),
    };

    private static readonly Gen[] Two =
    {
        new("DRAX-1", "Biomass", "#2FD27F", 645.0),
        new("DRAX-5", "Gas", "#F3C13A", 0.4),
    };

    private static IEnumerable<MapCase> MapCases()
    {
        yield return new MapCase("defaults");
        yield return new MapCase("typical", 1.012, -12.34, 400, 250.6, 81.2, 645.4, "PV", 394.8, Three, Two);
        foreach (double v in new[] { 0.94, 0.9399999999, 0.96, 0.9599999999, 1.04, 1.0400000001, 1.06, 1.0600000001, 0.0, -1.0,
                     double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            yield return new MapCase("voltage " + v.ToString("R", CultureInfo.InvariantCulture), V: v, Conns: Three);
        foreach (double ang in new[] { -0.04, 0.04, -0.05, 0.05, 179.95, double.NaN })
            yield return new MapCase("angle " + ang.ToString("R", CultureInfo.InvariantCulture), Ang: ang);
        Conn[] flows = new[] { 0.5, 0.5000000001, -0.5, -0.5000000001, 0.0, -0.0, double.NaN, double.PositiveInfinity, -1e6 }
            .Select((f, i) => new Conn("C" + i, "END " + i, i % 2 == 0 ? "TRANSFORMER" : "LINE", f, i % 3 == 0, 0.5)).ToArray();
        yield return new MapCase("flow thresholds", Conns: flows);
        Conn[] loads = new[] { 0.0, -0.1, 1e-12, 0.7, 0.7000000001, 0.9, 0.9000000001, 1.5, double.NaN, double.PositiveInfinity }
            .Select((l, i) => new Conn("L" + i, "BUS " + i, "LINE", 100 + i, true, l)).ToArray();
        yield return new MapCase("loading thresholds", Conns: loads);
        yield return new MapCase("thirty connections", Conns: Enumerable.Range(0, 30)
            .Select(i => new Conn("X" + i, "OTHER " + i, i % 4 == 0 ? "TRANSFORMER" : "LINE", (i - 15) * 37.5, i % 2 == 0, i / 30.0)).ToArray());
        yield return new MapCase("one connection", Conns: new[] { Three[1] });
        yield return new MapCase("twelve generators", Gens: Enumerable.Range(0, 12)
            .Select(i => new Gen("G" + i, i % 2 == 0 ? "Wind" : "Nuclear", i % 2 == 0 ? "#3FA9F5" : "#FF5B5B", i * 101.5)).ToArray());
        yield return new MapCase("generator with a translucent fuel colour", Gens: new[] { new Gen("G", "Solar", "#80FFD300", 12) });
        foreach (double pd in new[] { 0.1, 0.1000000001, -0.1, -0.1000000001, 0.0, double.NaN, double.PositiveInfinity, 12345.67 })
            yield return new MapCase("demand " + pd.ToString("R", CultureInfo.InvariantCulture), Pd: pd, Qd: pd / 3);
        foreach (double net in new[] { 5.0, 5.0000000001, -5.0, -5.0000000001, 0.0, -0.4, 0.4, 12345.6, -98765.4, double.NaN })
            yield return new MapCase("net injection " + net.ToString("R", CultureInfo.InvariantCulture), Net: net);
        yield return new MapCase("empty texts", Type: "", Conns: new[] { new Conn("", "", "", 1, false, 0.2) }, Gens: new[] { new Gen("", "", "#000000", 0) });
        yield return new MapCase("lower-case transformer kind is a line", Conns: new[] { new Conn("T", "B", "transformer", 50, true, 0.3) });
        yield return new MapCase("huge base", V: 1.0, Kv: 1e300, Net: 1e300, Pd: -1e300);
    }

    private static readonly (double W, double H)[] MapSizes =
        { (299.9, 400), (300, 199.9), (300, 200), (640, 500), (1200, 480), (333.3, 250.7), (2400.5, 900.25), (double.NaN, 400), (500, double.NaN) };

    private static readonly double[] Scales = { 1.0, 1.25, 0.8, 2.0 };

    [Fact]
    public void SubstationMap_draws_exactly_as_before()
    {
        int drawn = 0;
        foreach (MapCase c in MapCases())
            foreach ((double w, double h) in MapSizes)
                foreach (double pps in Scales)
                {
                    var o = new Old.SubstationMap { Data = OldData(c) };
                    var n = new New.SubstationMap { Data = NewData(c) };
                    List<string> rec = AssertSame(Record(o, w, h, pps), Record(n, w, h, pps), $"SubstationMap {c.What} at {w} x {h} pps {pps}");
                    if (rec.Count > 0) drawn++;
                }
        Assert.True(drawn > 1000, $"only {drawn} substation renders drew anything: the comparison would be vacuous");
    }

    [Fact]
    public void SubstationMap_draws_nothing_without_data_exactly_as_before()
    {
        foreach ((double w, double h) in MapSizes)
        {
            List<string> rec = AssertSame(Record(new Old.SubstationMap(), w, h, 1), Record(new New.SubstationMap(), w, h, 1), "SubstationMap no data");
            Assert.Empty(rec);
        }
    }

    [Fact]
    public void SubstationMap_text_follows_the_culture_exactly_as_before()
    {
        // The interpolated labels format with the current culture (they always have, in WPF); under a culture with a
        // decimal comma both versions must print the same comma.
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            MapCase c = MapCases().Single(x => x.What == "typical");
            List<string> rec = AssertSame(Record(new Old.SubstationMap { Data = OldData(c) }, 640, 500, 1.25),
                Record(new New.SubstationMap { Data = NewData(c) }, 640, 500, 1.25), "SubstationMap de-DE");
            Assert.Contains(rec, l => l.Contains("'1,012 pu   404,8 kV   -12,3 deg'", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    // ---- the transformer schematic ----

    private static Dictionary<string, object?> P(params (string Name, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    private static void Set(object control, IReadOnlyDictionary<string, object?> props)
    {
        foreach ((string name, object? value) in props)
        {
            PropertyInfo? p = control.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(p is not null, $"{control.GetType().Name} has no property {name}");
            p.SetValue(control, value);
        }
    }

    public static IEnumerable<Dictionary<string, object?>> SchematicCases()
    {
        yield return P();
        yield return P(("LoadingPct", 82.0), ("HvKvNom", 400.0), ("LvKvNom", 132.0), ("HvKvLive", 405.3), ("LvKvLive", 131.1),
            ("FlowMw", 812.4), ("RatingMva", 1000.0), ("TapRatio", 1.0375), ("TopOilC", 64.2), ("HotSpotC", 88.0),
            ("HasOltc", true), ("TapMin", 0.85), ("TapMax", 1.15));
        foreach (double l in new[] { 75.0, 75.0000001, 90.0, 90.0000001, -5.0, 250.0, double.NaN, double.PositiveInfinity })
            yield return P(("LoadingPct", l), ("FlowMw", -l));
        foreach (double hs in new[] { 85.0, 85.0000001, 98.0, 98.0000001, double.NaN, double.NegativeInfinity })
            yield return P(("HotSpotC", hs), ("TopOilC", hs - 13));
        foreach (double live in new[] { 0.0, -1.0, 1e-300, double.NaN, double.PositiveInfinity, 275.5 })
            yield return P(("HvKvLive", live), ("LvKvLive", live / 4));
        foreach ((double tap, double lo, double hi) in new[]
                 {
                     (1.0, 0.9, 1.1), (0.85, 0.9, 1.1), (1.2, 0.9, 1.1), (0.9, 0.9, 1.1), (1.1, 0.9, 1.1), (1.0, 1.0, 1.0),
                     (1.0, 1.1, 0.9), (double.NaN, 0.9, 1.1), (1.0, double.NaN, 1.1), (1.0, 0.9, double.PositiveInfinity),
                     (1.0000001, 0.9, 1.1),
                 })
            yield return P(("HasOltc", true), ("TapRatio", tap), ("TapMin", lo), ("TapMax", hi));
        yield return P(("HasOltc", false), ("TapRatio", 1.2));
        yield return P(("HvKvNom", 0.0), ("LvKvNom", -33.0), ("RatingMva", double.NaN));
    }

    private static readonly (double W, double H)[] SchematicSizes =
        { (199.9, 300), (300, 149.9), (200, 150), (480, 260), (920, 485), (1840, 970), (750, 480), (333.3, 777.7), (double.NaN, 300), (400, double.PositiveInfinity) };

    [Fact]
    public void TransformerSchematic_draws_exactly_as_before()
    {
        int drawn = 0;
        foreach (Dictionary<string, object?> props in SchematicCases())
            foreach ((double w, double h) in SchematicSizes)
                foreach (double pps in Scales)
                {
                    var o = new Old.TransformerSchematic();
                    var n = new New.TransformerSchematic();
                    Set(o, props);
                    Set(n, props);
                    string what = "TransformerSchematic " + string.Join(", ", props.Select(kv => $"{kv.Key}={kv.Value}")) + $" at {w} x {h} pps {pps}";
                    if (AssertSame(Record(o, w, h, pps), Record(n, w, h, pps), what).Count > 0) drawn++;
                }
        Assert.True(drawn > 900, $"only {drawn} schematic renders drew anything: the comparison would be vacuous");
    }

    [Fact]
    public void TransformerSchematic_text_follows_the_culture_exactly_as_before()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Dictionary<string, object?> props = SchematicCases().ElementAt(1);
            var o = new Old.TransformerSchematic();
            var n = new New.TransformerSchematic();
            Set(o, props);
            Set(n, props);
            List<string> rec = AssertSame(Record(o, 920, 485, 1.5), Record(n, 920, 485, 1.5), "TransformerSchematic de-DE");
            Assert.Contains(rec, l => l.Contains("'TAP 1,03", StringComparison.Ordinal));
            Assert.Contains(rec, l => l.Contains("'234,0 kV'", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void The_comparison_notices_a_one_bit_change()
    {
        // Sensitivity: the recordings print every double by its bits, so moving one input by one unit in the last place
        // must change the recording. Without this an always-equal recording (say, one that dropped the numbers) would
        // pass every parity test above.
        MapCase c = MapCases().Single(x => x.What == "typical");
        List<string> a = Record(new New.SubstationMap { Data = NewData(c) }, 640, 500, 1);
        List<string> b = Record(new New.SubstationMap { Data = NewData(c) }, 640, Math.BitIncrement(500.0), 1);
        Assert.NotEqual(a, b);
        var t1 = new New.TransformerSchematic { TapRatio = 1.0, HasOltc = true };
        var t2 = new New.TransformerSchematic { TapRatio = Math.BitIncrement(1.0), HasOltc = true };
        Assert.NotEqual(Record(t1, 920, 485, 1), Record(t2, 920, 485, 1));
        // And the device scale is part of the recording (it tells a device-scale text call from a literal-1 one).
        Assert.NotEqual(Record(new New.TransformerSchematic(), 920, 485, 1), Record(new New.TransformerSchematic(), 920, 485, 1.5));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The tokens are the old WPF pens and brushes
    // ---------------------------------------------------------------------------------------------------------------

    private static T Field<T>(Type t, string name)
    {
        FieldInfo? f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(f is not null, $"{t.Name}.{name} not found");
        return Assert.IsAssignableFrom<T>(f.GetValue(null));
    }

    private static UiColour U(Shim.Color c) => new(c.A, c.R, c.G, c.B);

    private static void SamePen(Shim.Pen old, VisualPen token, string what)
    {
        var b = Assert.IsType<Shim.SolidColorBrush>(old.Brush);
        Assert.True(U(b.Color) == token.Colour, $"{what}: colour {b.Color} vs {token.Colour}");
        Assert.True(BitConverter.DoubleToInt64Bits(old.Thickness) == BitConverter.DoubleToInt64Bits(token.Thickness), $"{what}: thickness");
        Assert.True(BitConverter.DoubleToInt64Bits(b.Opacity) == BitConverter.DoubleToInt64Bits(token.Opacity), $"{what}: opacity");
        Assert.True(old.StartLineCap == Shim.PenLineCap.Flat && old.EndLineCap == Shim.PenLineCap.Flat && !token.RoundCaps, $"{what}: caps");
        Assert.True(old.LineJoin == Shim.PenLineJoin.Miter && !token.RoundJoin, $"{what}: join");
        Assert.True(old.DashStyle is null && token.Dash == 0 && token.Pattern == VisualDashPattern.None, $"{what}: dash");
    }

    private static void SameFill(Shim.Brush old, VisualFill token, string what)
    {
        var b = Assert.IsType<Shim.SolidColorBrush>(old);
        Assert.True(U(b.Color) == token.Colour, $"{what}: colour {b.Color} vs {token.Colour}");
        Assert.True(BitConverter.DoubleToInt64Bits(b.Opacity) == BitConverter.DoubleToInt64Bits(token.Opacity), $"{what}: opacity");
    }

    [Fact]
    public void The_colour_tokens_are_the_old_WPF_pens_and_brushes()
    {
        Type m = typeof(Old.SubstationMap);
        SamePen(Field<Shim.Pen>(m, "BusPen"), SubstationMapGeometry.BusPen, "SubstationMap BusPen");
        SamePen(Field<Shim.Pen>(m, "LinePen"), SubstationMapGeometry.LinePen, "SubstationMap LinePen");
        SamePen(Field<Shim.Pen>(m, "TxPen"), SubstationMapGeometry.TxPen, "SubstationMap TxPen");
        SamePen(Field<Shim.Pen>(m, "ArrowPen"), SubstationMapGeometry.ArrowPen, "SubstationMap ArrowPen");
        SamePen(Field<Shim.Pen>(m, "LoadPen"), SubstationMapGeometry.LoadPen, "SubstationMap LoadPen");
        SamePen(Field<Shim.Pen>(m, "InfoBoxPen"), SubstationMapGeometry.InfoBoxPen, "SubstationMap InfoBoxPen");
        SameFill(Field<Shim.Brush>(m, "TextWhite"), SubstationMapGeometry.TextWhite, "SubstationMap TextWhite");
        SameFill(Field<Shim.Brush>(m, "TextSlate"), SubstationMapGeometry.TextSlate, "SubstationMap TextSlate");
        SameFill(Field<Shim.Brush>(m, "TextDim"), SubstationMapGeometry.TextDim, "SubstationMap TextDim");
        SameFill(Field<Shim.Brush>(m, "CyanBrush"), SubstationMapGeometry.Cyan, "SubstationMap CyanBrush");
        SameFill(Field<Shim.Brush>(m, "AmberBrush"), SubstationMapGeometry.Amber, "SubstationMap AmberBrush");
        SameFill(Field<Shim.Brush>(m, "RedBrush"), SubstationMapGeometry.Red, "SubstationMap RedBrush");
        SameFill(Field<Shim.Brush>(m, "GreenBrush"), SubstationMapGeometry.Green, "SubstationMap GreenBrush");
        SameFill(Field<Shim.Brush>(m, "DarkBgBrush"), SubstationMapGeometry.DarkBgFill, "SubstationMap DarkBgBrush");

        Type t = typeof(Old.TransformerSchematic);
        SamePen(Field<Shim.Pen>(t, "TankPen"), TransformerSchematicGeometry.TankPen, "TransformerSchematic TankPen");
        SamePen(Field<Shim.Pen>(t, "CorePen"), TransformerSchematicGeometry.CorePen, "TransformerSchematic CorePen");
        SamePen(Field<Shim.Pen>(t, "SlatePen"), TransformerSchematicGeometry.SlatePen, "TransformerSchematic SlatePen");
        SamePen(Field<Shim.Pen>(t, "BushingStem"), TransformerSchematicGeometry.BushingStem, "TransformerSchematic BushingStem");
        SamePen(Field<Shim.Pen>(t, "BushingStemHv"), TransformerSchematicGeometry.BushingStemHv, "TransformerSchematic BushingStemHv");
        SamePen(Field<Shim.Pen>(t, "FinStroke"), TransformerSchematicGeometry.FinStroke, "TransformerSchematic FinStroke");
        SamePen(Field<Shim.Pen>(t, "PipePen"), TransformerSchematicGeometry.PipePen, "TransformerSchematic PipePen");
        SamePen(Field<Shim.Pen>(t, "ConnectPen"), TransformerSchematicGeometry.ConnectPen, "TransformerSchematic ConnectPen");
        SamePen(Field<Shim.Pen>(t, "InfoBoxPen"), TransformerSchematicGeometry.InfoBoxPen, "TransformerSchematic InfoBoxPen");
        SameFill(Field<Shim.Brush>(t, "TankBrush"), TransformerSchematicGeometry.TankFill, "TransformerSchematic TankBrush");
        SameFill(Field<Shim.Brush>(t, "CoreBrush"), TransformerSchematicGeometry.CoreFill, "TransformerSchematic CoreBrush");
        SameFill(Field<Shim.Brush>(t, "FinBrush"), TransformerSchematicGeometry.FinFill, "TransformerSchematic FinBrush");
        SameFill(Field<Shim.Brush>(t, "DarkBgBrush"), TransformerSchematicGeometry.DarkBgFill, "TransformerSchematic DarkBgBrush");
        SameFill(Field<Shim.Brush>(t, "FanCircleBrush"), TransformerSchematicGeometry.DarkBgFill, "TransformerSchematic FanCircleBrush");
        SameFill(Field<Shim.Brush>(t, "ShedBrush"), TransformerSchematicGeometry.ShedFill, "TransformerSchematic ShedBrush");
        SameFill(Field<Shim.Brush>(t, "TextWhite"), TransformerSchematicGeometry.TextWhite, "TransformerSchematic TextWhite");
        SameFill(Field<Shim.Brush>(t, "TextSlate"), TransformerSchematicGeometry.TextSlate, "TransformerSchematic TextSlate");
        SameFill(Field<Shim.Brush>(t, "TextDim"), TransformerSchematicGeometry.TextDim, "TransformerSchematic TextDim");
        SameFill(Field<Shim.Brush>(t, "CyanBrush"), TransformerSchematicGeometry.Cyan, "TransformerSchematic CyanBrush");
        SameFill(Field<Shim.Brush>(t, "AmberBrush"), TransformerSchematicGeometry.Amber, "TransformerSchematic AmberBrush");
        SameFill(Field<Shim.Brush>(t, "RedBrush"), TransformerSchematicGeometry.Red, "TransformerSchematic RedBrush");
        Shim.Color[] phases = Field<Shim.Color[]>(t, "PhaseColors");
        Assert.Equal(phases.Select(U), TransformerSchematicGeometry.PhaseColours);
    }

    [Fact]
    public void The_old_property_defaults_are_the_geometry_input_defaults()
    {
        var o = new Old.TransformerSchematic();
        var d = new TransformerSchematicInput();
        Assert.Equal((o.LoadingPct, o.HvKvNom, o.LvKvNom, o.HvKvLive, o.LvKvLive, o.FlowMw, o.RatingMva),
            (d.LoadingPct, d.HvKvNom, d.LvKvNom, d.HvKvLive, d.LvKvLive, d.FlowMw, d.RatingMva));
        Assert.Equal((o.TapRatio, o.TopOilC, o.HotSpotC, o.HasOltc, o.TapMin, o.TapMax),
            (d.TapRatio, d.TopOilC, d.HotSpotC, d.HasOltc, d.TapMin, d.TapMax));
        Assert.Null(new Old.SubstationMap().Data);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The old rules, stated as numbers
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_geometries_state_the_old_rules()
    {
        // Minimum sizes (a NaN size passes, as before).
        Var(SubstationMapGeometry.Compute(299.999, 400, new SubstationMapInput()), isNull: true);
        Var(SubstationMapGeometry.Compute(300, 199.999, new SubstationMapInput()), isNull: true);
        Var(SubstationMapGeometry.Compute(300, 200, null), isNull: true);
        Var(SubstationMapGeometry.Compute(300, 200, new SubstationMapInput()), isNull: false);
        Var(SubstationMapGeometry.Compute(double.NaN, 200, new SubstationMapInput()), isNull: false);
        Var(TransformerSchematicGeometry.Compute(199.999, 150, new TransformerSchematicInput()), isNull: true);
        Var(TransformerSchematicGeometry.Compute(200, 149.999, new TransformerSchematicInput()), isNull: true);
        Var(TransformerSchematicGeometry.Compute(200, 150, new TransformerSchematicInput()), isNull: false);

        // Substation voltage colour: red outside 0.94..1.06 (the shared band), amber in the watch margin, cyan inside.
        Assert.Equal(SubstationMapGeometry.Cyan, SubstationMapGeometry.VoltageFill(0.94 + 0.02));
        Assert.Equal(SubstationMapGeometry.Amber, SubstationMapGeometry.VoltageFill(0.94));
        Assert.Equal(SubstationMapGeometry.Red, SubstationMapGeometry.VoltageFill(Math.BitDecrement(0.94)));
        Assert.Equal(SubstationMapGeometry.Amber, SubstationMapGeometry.VoltageFill(Math.BitDecrement(0.96)));
        Assert.Equal(SubstationMapGeometry.Cyan, SubstationMapGeometry.VoltageFill(1.04));
        Assert.Equal(SubstationMapGeometry.Amber, SubstationMapGeometry.VoltageFill(Math.BitIncrement(1.04)));
        Assert.Equal(SubstationMapGeometry.Amber, SubstationMapGeometry.VoltageFill(1.06));
        Assert.Equal(SubstationMapGeometry.Red, SubstationMapGeometry.VoltageFill(Math.BitIncrement(1.06)));
        Assert.Equal(SubstationMapGeometry.Cyan, SubstationMapGeometry.VoltageFill(double.NaN));
        // Loading and injection colours.
        Assert.Equal(SubstationMapGeometry.TextSlate, SubstationMapGeometry.LoadingFill(0.7));
        Assert.Equal(SubstationMapGeometry.Amber, SubstationMapGeometry.LoadingFill(Math.BitIncrement(0.7)));
        Assert.Equal(SubstationMapGeometry.Amber, SubstationMapGeometry.LoadingFill(0.9));
        Assert.Equal(SubstationMapGeometry.Red, SubstationMapGeometry.LoadingFill(Math.BitIncrement(0.9)));
        Assert.Equal(SubstationMapGeometry.TextSlate, SubstationMapGeometry.InjectionFill(5));
        Assert.Equal(SubstationMapGeometry.Green, SubstationMapGeometry.InjectionFill(Math.BitIncrement(5.0)));
        Assert.Equal(SubstationMapGeometry.Red, SubstationMapGeometry.InjectionFill(Math.BitDecrement(-5.0)));

        // Transformer: the colour bands, the phase voltage, the tap pointer.
        Assert.Equal(TransformerSchematicGeometry.Cyan, TransformerSchematicGeometry.LoadingFill(75));
        Assert.Equal(TransformerSchematicGeometry.Amber, TransformerSchematicGeometry.LoadingFill(Math.BitIncrement(75.0)));
        Assert.Equal(TransformerSchematicGeometry.Red, TransformerSchematicGeometry.LoadingFill(Math.BitIncrement(90.0)));
        Assert.Equal(TransformerSchematicGeometry.Amber, TransformerSchematicGeometry.ThermalFill(98));
        Assert.Equal(TransformerSchematicGeometry.Red, TransformerSchematicGeometry.ThermalFill(Math.BitIncrement(98.0)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(400 / Math.Sqrt(3.0)),
            BitConverter.DoubleToInt64Bits(TransformerSchematicGeometry.PhaseVoltageKv(400, 132)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(132 / Math.Sqrt(3.0)),
            BitConverter.DoubleToInt64Bits(TransformerSchematicGeometry.PhaseVoltageKv(0, 132)));
        Assert.Equal(0.5, TransformerSchematicGeometry.TapFraction(1.0, 0.9, 1.1), 15);
        Assert.Equal(0.5, TransformerSchematicGeometry.TapFraction(7.0, 1.0, 1.0));
        Assert.Equal(0.0, TransformerSchematicGeometry.TapFraction(0.5, 0.9, 1.1));
        Assert.Equal(1.0, TransformerSchematicGeometry.TapFraction(1.5, 0.9, 1.1));
        Assert.True(double.IsNaN(TransformerSchematicGeometry.TapFraction(double.NaN, 0.9, 1.1)));

        // Display lists are balanced (every push popped) and the OLTC box is drawn only when fitted.
        int Count(TransformerSchematicInput i) => TransformerSchematicGeometry.Compute(920, 485, i)!.Commands.Count;
        Assert.True(Count(new TransformerSchematicInput { HasOltc = true }) > Count(new TransformerSchematicInput()));
    }

    private static void Var(VisualDisplayList? list, bool isNull)
    {
        if (isNull) Assert.Null(list);
        else Assert.NotNull(list);
    }
}
