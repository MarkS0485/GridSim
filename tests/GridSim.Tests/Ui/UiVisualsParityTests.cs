using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using GridSim.Ui.Presentation;
using GridSim.Ui.Visuals;
using Xunit;
using New = GridSim.Tests.Ui.NewWpfVisuals;
using Old = GridSim.Tests.Ui.OldWpfVisuals;
using Shim = GridSim.Tests.Ui.WpfShim;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 1.3 (the small visuals): the arithmetic of the five WPF instrument controls (Gauge, Sparkline,
/// FrequencyTrace, Phasor, PqDiagram) moved into the platform-neutral GridSim.Ui/Visuals geometries, which the WPF
/// controls and the new Avalonia controls both draw from. That is a pure move, so every pixel the WPF controls draw must
/// be exactly what they drew before.
///
/// <para>How it is checked. Both versions of each WPF control are compiled here against a recording stand-in for WPF
/// (<c>UiVisualsWpfShim.cs</c>): the OLD files (<c>OldWpfVisuals/</c>, the oracle, exactly as at commit f3559445) and the
/// NEW files (<c>NewWpfVisuals/</c>, exactly the code in src/GridSim.Wpf/Visuals now). Both are rendered with the same
/// properties at the same sizes, over normal and edge inputs (NaN, infinities, a zero or inverted range, values outside
/// the range, empty and one-point series, controls below their minimum size, a culture with a decimal comma), and the
/// recordings must be equal line for line: every drawing call, pen, brush, point (each double by its round-trip text and
/// its 64 bits), text and flag, in the same order. The copies themselves are pinned: the old ones by a SHA-256 of their
/// text against the original files, the new ones by comparing their text with the real WPF files.</para>
///
/// <para>The colour and pen tokens the geometries hand out (which the Avalonia controls draw with) are checked against the
/// old WPF pens and brushes field by field, so the Avalonia instruments paint the WPF colours.</para>
/// </summary>
public sealed class UiVisualsParityTests
{
    private static readonly string[] Controls = { "Gauge", "Sparkline", "FrequencyTrace", "Phasor", "PqDiagram" };

    // ---------------------------------------------------------------------------------------------------------------
    // The copies are what they claim to be
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The SHA-256 of each original file (commit f3559445) without its using, namespace and "// TEST" lines,
    /// lines joined by a line feed. Worked out from git when the oracle was copied.</summary>
    private static readonly Dictionary<string, string> OldHashes = new(StringComparer.Ordinal)
    {
        ["Gauge"] = "2a8df690f9280d8cca712092e76a00cbb358b57b74d69a17c549c1a1fff7904e",
        ["Sparkline"] = "5c5a1849e03d7f315124b444003db6258b63f3c41ac31021887c0199c2154917",
        ["FrequencyTrace"] = "7b9920dc607775fb44cef0c2888010b6525c37bdea5d9400c46600c83f8f5a2c",
        ["Phasor"] = "bec247c029eb679c58f9cf1852d087884ebfb3440d2b0089fd6953f5997836f2",
        ["PqDiagram"] = "d7bed7b2226a24d6cc7da0a7823bcbe92f94fa069a76463d0d72f95fa44426ba",
    };

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
        foreach (string name in Controls)
        {
            string copy = Path.Combine(root, "tests", "GridSim.Tests", "Ui", "OldWpfVisuals", name + ".cs");
            Assert.True(OldHashes[name] == Sha256(Body(copy)), $"{name}: the oracle copy is not the original file's text");
        }
    }

    [Fact]
    public void The_new_copies_are_the_current_WPF_files_verbatim()
    {
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        foreach (string name in Controls)
        {
            string real = Body(Path.Combine(root, "src", "GridSim.Wpf", "Visuals", name + ".cs"));
            string copy = Body(Path.Combine(root, "tests", "GridSim.Tests", "Ui", "NewWpfVisuals", name + ".cs"));
            Assert.True(string.Equals(real, copy, StringComparison.Ordinal),
                $"{name}: tests/GridSim.Tests/Ui/NewWpfVisuals/{name}.cs is not the current src/GridSim.Wpf/Visuals/{name}.cs " +
                "(refresh the copy: same text, only the using and namespace lines differ)");
        }
    }

    [Fact]
    public void The_new_WPF_files_draw_from_the_shared_geometry()
    {
        // The move really happened: each WPF control calls its GridSim.Ui geometry, and the old inline maths is gone.
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        string Src(string n) => File.ReadAllText(Path.Combine(root, "src", "GridSim.Wpf", "Visuals", n + ".cs"));
        Assert.Contains("GaugeGeometry.Compute(", Src("Gauge"), StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Cos", Src("Gauge"), StringComparison.Ordinal);
        Assert.Contains("SparklineGeometry.Compute(", Src("Sparkline"), StringComparison.Ordinal);
        Assert.DoesNotContain("v.Min()", Src("Sparkline"), StringComparison.Ordinal);
        Assert.Contains("FrequencyTraceGeometry.Compute(", Src("FrequencyTrace"), StringComparison.Ordinal);
        Assert.DoesNotContain("GbEdgeAbout", Src("FrequencyTrace"), StringComparison.Ordinal);
        Assert.Contains("PhasorGeometry.Compute(", Src("Phasor"), StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Atan2", Src("Phasor"), StringComparison.Ordinal);
        Assert.Contains("PqDiagramGeometry.Compute(", Src("PqDiagram"), StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Clamp", Src("PqDiagram"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Old and new draw exactly the same
    // ---------------------------------------------------------------------------------------------------------------

    private static void Set(object control, IReadOnlyDictionary<string, object?> props)
    {
        foreach ((string name, object? value) in props)
        {
            PropertyInfo? p = control.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(p is not null, $"{control.GetType().Name} has no property {name}");
            p.SetValue(control, value);
        }
    }

    /// <summary>Render a fresh old and a fresh new control with the same properties at the same size; the recordings
    /// must be equal line for line. Returns the recording (so a caller can check it drew something).</summary>
    private static List<string> SameDrawing(Func<Shim.FrameworkElement> makeOld, Func<Shim.FrameworkElement> makeNew,
        IReadOnlyDictionary<string, object?> props, double w, double h, string what)
    {
        Shim.FrameworkElement o = makeOld(), n = makeNew();
        Set(o, props);
        Set(n, props);
        List<string> want = o.Record(w, h), got = n.Record(w, h);
        Assert.True(want.Count == got.Count, $"{what} at {w} x {h}: old drew {want.Count} calls, new drew {got.Count}\nold:\n{string.Join("\n", want)}\nnew:\n{string.Join("\n", got)}");
        for (int i = 0; i < want.Count; i++)
            Assert.True(string.Equals(want[i], got[i], StringComparison.Ordinal), $"{what} at {w} x {h}, call {i}:\nold: {want[i]}\nnew: {got[i]}");
        return want;
    }

    private static Dictionary<string, object?> P(params (string Name, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    private static readonly (double W, double H)[] GaugeSizes =
        { (29.9, 100), (100, 29.9), (30, 30), (100, 80), (240, 200), (1000, 30.5), (57.3, 91.7), (180, 400) };

    public static IEnumerable<Dictionary<string, object?>> GaugeCases()
    {
        yield return P();                                                   // every default
        yield return P(("Value", 42.0), ("AmberFrom", 0.7), ("RedFrom", 0.9), ("Unit", "RPM"), ("Caption", "GRID SPEED"));
        yield return P(("Value", 1499.7), ("Minimum", 1350.0), ("Maximum", 1650.0), ("AmberFrom", 0.7), ("RedFrom", 0.9),
            ("AmberTo", 0.3), ("RedTo", 0.1), ("Unit", "RPM"));               // symmetric instrument, both sides banded
        yield return P(("Value", 0.0), ("AmberTo", 0.3), ("RedTo", 0.1));
        yield return P(("Value", 100.0), ("AmberFrom", 0.95), ("RedFrom", 0.97));   // narrow high bands
        yield return P(("Value", 50.0), ("AmberFrom", 0.99995), ("RedFrom", 0.99999)); // bands below the drawable minimum
        yield return P(("Value", 250.0));                                   // above the range: pinned to full scale
        yield return P(("Value", -250.0));                                  // below the range: pinned to zero
        yield return P(("Value", double.NaN));                              // NaN value
        yield return P(("Value", double.PositiveInfinity));
        yield return P(("Value", double.NegativeInfinity));
        yield return P(("Value", 5.0), ("Minimum", 5.0), ("Maximum", 5.0));  // zero range
        yield return P(("Value", 5.0), ("Minimum", 10.0), ("Maximum", 0.0)); // inverted range
        yield return P(("Value", 1.0), ("Minimum", double.NaN), ("Maximum", double.PositiveInfinity));
        yield return P(("Value", 1.0), ("Maximum", double.NaN));
        yield return P(("AmberFrom", double.NaN), ("RedFrom", double.NaN), ("AmberTo", double.NaN), ("RedTo", double.NaN));
        yield return P(("AmberFrom", 0.9), ("RedFrom", 0.7));                // red before amber
        yield return P(("AmberFrom", -0.5), ("RedFrom", -0.2));              // negative fractions
        yield return P(("AmberTo", 1.7), ("RedTo", 2.5));                    // low bands past full scale
        yield return P(("AmberTo", 0.2), ("RedTo", 0.6));                    // low red wider than low amber
        yield return P(("AmberFrom", double.NegativeInfinity), ("RedFrom", double.PositiveInfinity), ("AmberTo", double.PositiveInfinity));
        yield return P(("Value", 12.345678), ("ValueText", "OFF"), ("Unit", " degC"), ("Caption", "WINDING"));
        yield return P(("Value", -0.004), ("Unit", "MNm"));                  // rounds to "-0" in "0.##"
        yield return P(("Value", 1e300), ("Maximum", 1e308));
    }

    [Fact]
    public void Gauge_draws_exactly_as_before()
    {
        int drawn = 0;
        foreach (Dictionary<string, object?> props in GaugeCases())
            foreach ((double w, double h) in GaugeSizes)
            {
                string what = "Gauge " + string.Join(", ", props.Select(kv => $"{kv.Key}={kv.Value}"));
                if (SameDrawing(() => new Old.Gauge(), () => new New.Gauge(), props, w, h, what).Count > 0) drawn++;
            }
        Assert.True(drawn > 100, $"only {drawn} gauge renders drew anything: the comparison would be vacuous");
    }

    private static readonly (double W, double H)[] SparklineSizes =
        { (3.9, 30), (120, 3.9), (4, 4), (120, 30), (200.5, 47.25), (37.7, 400) };

    public static IEnumerable<IReadOnlyList<double>?> SparklineSeries()
    {
        yield return null;
        yield return Array.Empty<double>();
        yield return new[] { 7.0 };
        yield return new[] { 1.0, 2.0 };
        yield return new[] { 5.0, 5.0, 5.0, 5.0 };                          // flat: centred line
        yield return new[] { 49.98, 50.01, 49.95, 50.03, 50.0, 49.91, 49.99 };
        yield return Enumerable.Range(0, 240).Select(i => Math.Sin(i * 0.1) * 300 + 1200).ToArray();
        yield return new[] { -3.5, 0.0, 12.25, -7.125, 4.0 };
        yield return new[] { 1.0, double.NaN, 3.0 };                       // NaN inside
        yield return new[] { double.NaN, double.NaN };
        yield return new[] { 1.0, double.PositiveInfinity, 2.0 };
        yield return new[] { double.NegativeInfinity, 0.0 };
        yield return new[] { 1e-300, 2e-300, 3e-300 };
        yield return new[] { -1e308, 1e308 };                              // the span overflows
        yield return new[] { 0.004, -0.004 };                              // labels round to "0" and "-0"
    }

    [Fact]
    public void Sparkline_draws_exactly_as_before()
    {
        object?[] captions = { null, "", "SIMULATED", "REPLAY" };
        int drawn = 0;
        foreach (IReadOnlyList<double>? v in SparklineSeries())
            foreach (bool minMax in new[] { false, true })
                foreach (object? caption in captions)
                    foreach ((double w, double h) in SparklineSizes)
                    {
                        var props = P(("Values", v), ("ShowMinMax", minMax));
                        if (!Equals(caption, "SIMULATED")) props["Caption"] = caption;     // "SIMULATED" = the default
                        string what = $"Sparkline [{(v is null ? "null" : string.Join(",", v.Take(8)))}] minmax={minMax} caption={caption ?? "null"}";
                        if (SameDrawing(() => new Old.Sparkline(), () => new New.Sparkline(), props, w, h, what).Count > 0) drawn++;
                    }
        // A caller-set stroke colour: the cached pen and the soft fill follow it.
        var amber = new Shim.SolidColorBrush(Shim.Palette.FromHex("#F3C13A")) { Opacity = 0.5 };
        SameDrawing(() => new Old.Sparkline(), () => new New.Sparkline(),
            P(("Values", new[] { 1.0, 3.0, 2.0 }), ("Stroke", amber), ("ShowMinMax", true)), 90, 20, "Sparkline amber stroke");
        Assert.True(drawn > 200, $"only {drawn} sparkline renders drew anything: the comparison would be vacuous");
    }

    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A response through the given (seconds, Hz) points.</summary>
    private static FrequencyResponse Resp(params (double S, double Hz)[] pts)
    {
        var points = pts.Select(p => new FrequencyPoint(T0.AddSeconds(p.S), p.Hz, 0.0)).ToList();
        double nadir = pts.Length == 0 ? double.NaN : pts.Min(p => p.Hz);
        return new FrequencyResponse(0.0, nadir, 0.0, pts.Length == 0 ? double.NaN : pts[^1].Hz, points);
    }

    /// <summary>A smooth dip from 50 Hz to <paramref name="nadir"/> and back to <paramref name="settle"/> over
    /// <paramref name="seconds"/>, sampled every 0.1 s.</summary>
    private static FrequencyResponse Dip(double nadir, double settle, double seconds)
    {
        int n = (int)(seconds / 0.1);
        var pts = new (double, double)[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double t = i * 0.1;
            double f = t < seconds / 3 ? 50 - (50 - nadir) * (t / (seconds / 3)) : nadir + (settle - nadir) * ((t - seconds / 3) / (seconds * 2 / 3));
            pts[i] = (t, f);
        }
        return Resp(pts);
    }

    public static IEnumerable<(FrequencyResponse? Base, FrequencyResponse? Tsgb, FrequencyBands? Bands, string What)> FrequencyCases()
    {
        FrequencyResponse normal = Dip(49.3, 49.7, 30), held = Dip(49.75, 49.9, 30);
        yield return (null, null, null, "no responses");
        yield return (normal, null, null, "baseline only");
        yield return (normal, held, null, "both");
        yield return (null, held, FrequencyBands.Gb, "tsgb only, explicit GB");
        yield return (Resp((0, 50)), Resp((0, 50)), null, "one point each (legend, no curves)");
        yield return (Resp(), null, null, "empty response");
        yield return (Dip(48.1, 48.9, 20), held, null, "baseline below the floor");
        yield return (Dip(48.1, 48.9, 20), Dip(48.3, 49.0, 20), null, "both below the floor");
        yield return (Resp((0, 50), (1, 50.9), (2, 50.2)), null, null, "above the ceiling");
        yield return (Resp((0, 50), (1, 51.2), (2, 48.0)), Resp((0, 50), (3, 50.6)), null, "both ends off-scale");
        yield return (Dip(59.4, 59.8, 30), Dip(59.7, 59.9, 30), FrequencyBands.ForNominal(60.0), "60 Hz system");
        yield return (Dip(58.2, 59.0, 30), null, FrequencyBands.ForNominal(60.0), "60 Hz below its floor");
        yield return (Dip(49.2, 49.8, 117.3), null, null, "long event (117.3 s)");
        yield return (Resp((0, 50), (0, 49.5)), null, null, "zero duration");
        yield return (Resp((0, 50), (0.004, 49.9)), null, null, "4 ms duration");
        yield return (Resp((0, 50), (1, double.NaN), (2, 49.8)), null, null, "NaN sample");
        yield return (Resp((0, double.PositiveInfinity), (1, double.NegativeInfinity)), null, null, "infinite samples");
        yield return (Resp((5, 49.9), (1, 49.8), (9, 49.7)), null, null, "times out of order");
    }

    [Fact]
    public void FrequencyTrace_draws_exactly_as_before()
    {
        (double W, double H)[] sizes = { (59.9, 100), (100, 49.9), (60, 50), (400, 220), (333.3, 181.7), (1200, 90) };
        int drawn = 0;
        foreach (var (b, t, bands, what) in FrequencyCases())
            foreach ((double w, double h) in sizes)
            {
                var o = new Old.FrequencyTrace();
                var n = new New.FrequencyTrace();
                o.SetResponses(b, t, bands);
                n.SetResponses(b, t, bands);
                List<string> want = o.Record(w, h), got = n.Record(w, h);
                Assert.Equal(want, got);
                if (want.Count > 1) drawn++;
            }
        Assert.True(drawn > 50, $"only {drawn} chart renders drew anything: the comparison would be vacuous");
    }

    private static readonly (double W, double H)[] PhasorSizes = { (39.9, 100), (100, 39.9), (40, 40), (160, 160), (300, 120.5), (77.7, 333.3) };

    public static IEnumerable<Dictionary<string, object?>> PhasorCases()
    {
        yield return P();
        foreach ((double p, double q) in new[]
                 {
                     (100.0, 0.0), (100.0, 30.0), (100.0, -30.0), (0.0, 0.0), (-50.0, 20.0), (-50.0, -20.0), (0.0, 80.0),
                     (0.0, -80.0), (0.0, 1e-7), (0.0, -1e-7), (1e-7, 1e-7), (100.0, 1.9), (100.0, -1.9), (100.0, 2.1),
                     (double.NaN, 5.0), (5.0, double.NaN), (double.PositiveInfinity, 1.0), (1.0, double.PositiveInfinity),
                     (double.NegativeInfinity, double.NegativeInfinity), (-0.0, -0.0), (1e300, 1e300),
                 })
            foreach (double sMax in new[] { 0.0, 50.0, 1e-7, 1000.0, double.NaN, -10.0 })
                yield return P(("P", p), ("Q", q), ("SMax", sMax));
        yield return P(("P", 300.0), ("Q", -120.0), ("ShowPowerFactor", false));
    }

    [Fact]
    public void Phasor_draws_exactly_as_before()
    {
        int drawn = 0;
        foreach (Dictionary<string, object?> props in PhasorCases())
            foreach ((double w, double h) in PhasorSizes)
            {
                string what = "Phasor " + string.Join(", ", props.Select(kv => $"{kv.Key}={kv.Value}"));
                // Test changed 2026-10-24 (agreed by Mark): the oracle is the original control with the phasor convention
                // corrected (Old.PhasorConvention: a lagging current below the axis), pinned below to differ from the
                // original in exactly those lines; every other draw call is still compared exactly.
                if (SameDrawing(() => new Old.PhasorConvention(), () => new New.Phasor(), props, w, h, what).Count > 0) drawn++;
            }
        Assert.True(drawn > 300, $"only {drawn} phasor renders drew anything: the comparison would be vacuous");
    }

    [Fact]
    public void Phasor_labels_follow_the_culture_exactly_as_before()
    {
        // Test changed 2026-10-24 (agreed by Mark): the angle and power-factor labels are now formatted with the invariant
        // culture in both apps (the old control used the machine's, so a German machine printed "PF 0,949" beside every
        // other read-out's decimal point). Under a decimal-comma culture the label keeps its point; under the invariant
        // culture the drawing is still compared call for call with the oracle by Phasor_draws_exactly_as_before.
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var n = new New.Phasor();
            Set(n, P(("P", 100.0), ("Q", 33.3)));
            List<string> rec = n.Record(160, 160);
            Assert.Contains(rec, l => l.Contains("'PF 0.949'", StringComparison.Ordinal));
            Assert.Contains(rec, l => l.Contains("'phi 18.4 deg'", StringComparison.Ordinal));
            Assert.DoesNotContain(rec, l => l.Contains("0,949", StringComparison.Ordinal) || l.Contains("18,4", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void The_convention_oracle_is_the_original_phasor_with_only_the_current_angle_corrected()
    {
        // OldWpfVisuals/PhasorConvention.cs must be OldWpfVisuals/Phasor.cs (itself pinned to commit f3559445 above) with
        // the class renamed and the current phasor's screen angle changed from -phi to +phi in its four uses, nothing else.
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        string[] Lines(string name) => File.ReadAllLines(Path.Combine(root, "tests", "GridSim.Tests", "Ui", "OldWpfVisuals", name + ".cs"))
            .Where(l => !l.StartsWith("// TEST ", StringComparison.Ordinal)).ToArray();
        string[] original = Lines("Phasor"), corrected = Lines("PhasorConvention");
        Assert.Equal(original.Length, corrected.Length);
        var changed = original.Zip(corrected).Where(p => p.First != p.Second).Select(p => (p.First.Trim(), p.Second.Trim())).ToList();
        Assert.Equal(new[]
        {
            ("public sealed class Phasor : FrameworkElement", "public sealed class PhasorConvention : FrameworkElement"),
            ("DrawVector(dc, c, iPen, -phi, iLen);", "DrawVector(dc, c, iPen, phi, iLen);"),
            ("double ix = c.X + Math.Cos(-phi) * iLen, iy = c.Y + Math.Sin(-phi) * iLen;", "double ix = c.X + Math.Cos(phi) * iLen, iy = c.Y + Math.Sin(phi) * iLen;"),
            ("new Point(ix + (Math.Cos(-phi) >= 0 ? 4 : -14), iy - 8), Pps(), TextAlignment.Left);", "new Point(ix + (Math.Cos(phi) >= 0 ? 4 : -14), iy - 8), Pps(), TextAlignment.Left);"),
            ("DrawArc(dc, c, r * 0.34, 0, -phi);", "DrawArc(dc, c, r * 0.34, 0, phi);"),
        }, changed);
    }

    [Fact]
    public void A_lagging_current_is_drawn_below_the_axis_and_a_leading_one_above()
    {
        // The standard convention, V as the reference along +x: a lagging current (Q > 0, exporting VArs) is phi behind V,
        // below the real axis; screen y grows downward, so "below" is a larger y than the centre.
        PhasorScene lag = PhasorGeometry.Compute(160, 160, 100, 50, 0)!, lead = PhasorGeometry.Compute(160, 160, 100, -50, 0)!;
        Assert.Equal(PhasorState.Lagging, lag.State);
        Assert.True(lag.Current.Tip.Y > lag.Centre.Y, $"lagging tip at y {lag.Current.Tip.Y}, centre {lag.Centre.Y}");
        Assert.Equal(PhasorState.Leading, lead.State);
        Assert.True(lead.Current.Tip.Y < lead.Centre.Y, $"leading tip at y {lead.Current.Tip.Y}, centre {lead.Centre.Y}");
        // Mirror images of each other about the axis, exactly.
        Assert.Equal(lag.Current.Tip.X, lead.Current.Tip.X);
        Assert.Equal(lag.Centre.Y - lag.Current.Tip.Y, lead.Current.Tip.Y - lead.Centre.Y, 12);
        // A pure lagging load at P = 0 points straight down; a pure leading one straight up.
        Assert.True(PhasorGeometry.Compute(160, 160, 0, 80, 0)!.Current.Tip.Y > 80);
        Assert.True(PhasorGeometry.Compute(160, 160, 0, -80, 0)!.Current.Tip.Y < 80);
    }

    [Fact]
    public void PqDiagram_draws_exactly_as_before()
    {
        (double W, double H)[] sizes = { (19.9, 100), (100, 19.9), (20, 20), (43.9, 100), (44, 36), (100, 35.9), (200, 140), (321.7, 207.3) };
        var cases = new List<Dictionary<string, object?>> { P() };
        foreach ((double pg, double qg) in new[]
                 {
                     (300.0, 50.0), (380.0, 50.0), (395.0, 50.0), (300.0, 190.0), (300.0, -140.0), (300.0, -149.0), (0.0, 0.0),
                     (-20.0, 0.0), (450.0, 250.0), (100.0, -400.0), (double.NaN, 0.0), (0.0, double.NaN),
                     (double.PositiveInfinity, double.NegativeInfinity), (-0.0, -0.0),
                 })
        {
            cases.Add(P(("Pg", pg), ("Qg", qg), ("Qmin", -150.0), ("Qmax", 200.0), ("Pmax", 400.0)));
        }
        cases.Add(P(("Pg", 0.5), ("Qg", 0.2), ("Qmin", 0.0), ("Qmax", 0.5), ("Pmax", 0.0)));     // limits inside the floors
        cases.Add(P(("Pg", 5.0), ("Qg", -5.0), ("Qmin", double.NaN), ("Qmax", double.NaN), ("Pmax", double.NaN)));
        cases.Add(P(("Pg", 5.0), ("Qg", 5.0), ("Qmin", double.NegativeInfinity), ("Qmax", double.PositiveInfinity), ("Pmax", double.PositiveInfinity)));
        cases.Add(P(("Pg", 5.0), ("Qg", 5.0), ("Qmin", 300.0), ("Qmax", -300.0), ("Pmax", 10.0)));   // inverted reactive limits

        int drawn = 0, threw = 0;
        foreach (Dictionary<string, object?> props in cases)
            foreach ((double w, double h) in sizes)
            {
                string what = "PqDiagram " + string.Join(", ", props.Select(kv => $"{kv.Key}={kv.Value}"));
                List<string> rec = SameDrawing(() => new Old.PqDiagram(), () => new New.PqDiagram(), props, w, h, what);
                if (rec.Count > 1) drawn++;
                if (rec.Count > 0 && rec[^1].StartsWith("THREW", StringComparison.Ordinal)) threw++;
            }
        Assert.True(drawn >= 57, $"only {drawn} diagram renders drew anything: the comparison would be vacuous");
        // Between 20 and 44 pixels wide (or 20 and 36 high) the old diagram's plot box has a negative size, which the WPF
        // Rect refuses with an exception. The move keeps that exactly (it is the WPF behaviour, pinned, not fixed here).
        Assert.True(threw > 0, "the negative-size plot box case was not exercised");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The tokens the Avalonia controls paint with are the WPF pens and brushes
    // ---------------------------------------------------------------------------------------------------------------

    private static T Field<T>(Type t, string name)
    {
        FieldInfo? f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(f is not null, $"{t.Name}.{name} not found");
        return Assert.IsAssignableFrom<T>(f.GetValue(null));
    }

    private static void SamePen(Shim.Pen old, VisualPen token, string what)
    {
        var b = Assert.IsType<Shim.SolidColorBrush>(old.Brush);
        Assert.True(new UiColour(b.Color.A, b.Color.R, b.Color.G, b.Color.B) == token.Colour, $"{what}: colour {b.Color} vs {token.Colour}");
        Assert.True(BitConverter.DoubleToInt64Bits(old.Thickness) == BitConverter.DoubleToInt64Bits(token.Thickness), $"{what}: thickness");
        Assert.True(BitConverter.DoubleToInt64Bits(b.Opacity) == BitConverter.DoubleToInt64Bits(token.Opacity), $"{what}: opacity");
        bool round = old.StartLineCap == Shim.PenLineCap.Round;
        Assert.True(round == token.RoundCaps && old.EndLineCap == old.StartLineCap, $"{what}: caps");
        Assert.True((old.LineJoin == Shim.PenLineJoin.Round) == token.RoundJoin, $"{what}: join");
        if (old.DashStyle is null) Assert.True(token.Dash == 0, $"{what}: old pen is solid, token is dashed");
        else
        {
            Assert.Equal(2, old.DashStyle.Dashes.Length);
            Assert.True(old.DashStyle.Dashes[0] == token.Dash && old.DashStyle.Dashes[1] == token.Dash && old.DashStyle.Offset == 0, $"{what}: dash");
        }
    }

    private static void SameFill(Shim.Brush old, VisualFill token, string what)
    {
        var b = Assert.IsType<Shim.SolidColorBrush>(old);
        Assert.True(new UiColour(b.Color.A, b.Color.R, b.Color.G, b.Color.B) == token.Colour, $"{what}: colour {b.Color} vs {token.Colour}");
        Assert.True(BitConverter.DoubleToInt64Bits(b.Opacity) == BitConverter.DoubleToInt64Bits(token.Opacity), $"{what}: opacity");
    }

    [Fact]
    public void The_colour_tokens_are_the_old_WPF_pens_and_brushes()
    {
        Type g = typeof(Old.Gauge);
        SamePen(Field<Shim.Pen>(g, "Track"), GaugeGeometry.Track, "Gauge Track");
        SamePen(Field<Shim.Pen>(g, "AmberArc"), GaugeGeometry.AmberArc, "Gauge AmberArc");
        SamePen(Field<Shim.Pen>(g, "RedArc"), GaugeGeometry.RedArc, "Gauge RedArc");
        SamePen(Field<Shim.Pen>(g, "TickPen"), GaugeGeometry.TickPen, "Gauge TickPen");
        SamePen(Field<Shim.Pen>(g, "NeedlePen"), GaugeGeometry.NeedlePen, "Gauge NeedlePen");
        SameFill(Field<Shim.Brush>(g, "HubBrush"), GaugeGeometry.Hub, "Gauge HubBrush");
        SameFill(Field<Shim.Brush>(g, "MutedBrush"), GaugeGeometry.Muted, "Gauge MutedBrush");

        Type s = typeof(Old.Sparkline);
        SamePen(Field<Shim.Pen>(s, "RefPen"), SparklineGeometry.RefPen, "Sparkline RefPen");
        SameFill(Field<Shim.Brush>(s, "RefLabel"), SparklineGeometry.RefLabel, "Sparkline RefLabel");
        SameFill(Field<Shim.Brush>(s, "BadgeBrush"), SparklineGeometry.Badge, "Sparkline BadgeBrush");
        var stroke = Assert.IsType<Shim.SolidColorBrush>(new Old.Sparkline().Stroke);
        Assert.Equal(new UiColour(stroke.Color.A, stroke.Color.R, stroke.Color.G, stroke.Color.B), SparklineGeometry.DefaultStroke);
        Assert.Equal("SIMULATED", new Old.Sparkline().Caption);
        Assert.Equal(SparklineGeometry.DefaultCaption, new Old.Sparkline().Caption);

        Type f = typeof(Old.FrequencyTrace);
        SameFill(Field<Shim.Brush>(f, "Bg"), FrequencyTraceGeometry.Background, "FrequencyTrace Bg");
        SameFill(Field<Shim.Brush>(f, "Axis"), FrequencyTraceGeometry.AxisText, "FrequencyTrace Axis");
        SameFill(Field<Shim.Brush>(f, "BaseBrush"), FrequencyTraceGeometry.BaselineText, "FrequencyTrace BaseBrush");
        SameFill(Field<Shim.Brush>(f, "TsgbBrush"), FrequencyTraceGeometry.TsgbText, "FrequencyTrace TsgbBrush");
        SamePen(Field<Shim.Pen>(f, "GridPen"), FrequencyTraceGeometry.GridPen, "FrequencyTrace GridPen");
        SamePen(Field<Shim.Pen>(f, "NominalPen"), FrequencyTraceGeometry.NominalPen, "FrequencyTrace NominalPen");
        SamePen(Field<Shim.Pen>(f, "OperPen"), FrequencyTraceGeometry.OperPen, "FrequencyTrace OperPen");
        SamePen(Field<Shim.Pen>(f, "StatPen"), FrequencyTraceGeometry.StatPen, "FrequencyTrace StatPen");
        SamePen(Field<Shim.Pen>(f, "LfddPen"), FrequencyTraceGeometry.LfddPen, "FrequencyTrace LfddPen");
        SamePen(Field<Shim.Pen>(f, "BasePen"), FrequencyTraceGeometry.BasePen, "FrequencyTrace BasePen");
        SamePen(Field<Shim.Pen>(f, "TsgbPen"), FrequencyTraceGeometry.TsgbPen, "FrequencyTrace TsgbPen");

        Type p = typeof(Old.Phasor);
        SamePen(Field<Shim.Pen>(p, "DiscPen"), PhasorGeometry.DiscPen, "Phasor DiscPen");
        SamePen(Field<Shim.Pen>(p, "AxisPen"), PhasorGeometry.AxisPen, "Phasor AxisPen");
        SamePen(Field<Shim.Pen>(p, "VPen"), PhasorGeometry.VPen, "Phasor VPen");
        SamePen(Field<Shim.Pen>(p, "LagPen"), PhasorGeometry.LagPen, "Phasor LagPen");
        SamePen(Field<Shim.Pen>(p, "LeadPen"), PhasorGeometry.LeadPen, "Phasor LeadPen");
        SamePen(Field<Shim.Pen>(p, "ArcPen"), PhasorGeometry.ArcPen, "Phasor ArcPen");
        SameFill(Field<Shim.Brush>(p, "TextBrush"), PhasorGeometry.Text, "Phasor TextBrush");
        SameFill(Field<Shim.Brush>(p, "MutedBrush"), PhasorGeometry.Muted, "Phasor MutedBrush");
        SameFill(Field<Shim.Brush>(p, "AmberBrush"), PhasorGeometry.Amber, "Phasor AmberBrush");
        SameFill(Field<Shim.Brush>(p, "CyanBrush"), PhasorGeometry.Cyan, "Phasor CyanBrush");

        Type q = typeof(Old.PqDiagram);
        SamePen(Field<Shim.Pen>(q, "Boundary"), PqDiagramGeometry.Boundary, "PqDiagram Boundary");
        SamePen(Field<Shim.Pen>(q, "Axis"), PqDiagramGeometry.Axis, "PqDiagram Axis");
        SameFill(Field<Shim.Brush>(q, "Fill"), PqDiagramGeometry.Fill, "PqDiagram Fill");
        SameFill(Field<Shim.Brush>(q, "DotBrush"), PqDiagramGeometry.DotNormal, "PqDiagram DotBrush");
        SameFill(Field<Shim.Brush>(q, "MutedBrush"), PqDiagramGeometry.Muted, "PqDiagram MutedBrush");
        // The near-limit and at-limit dots were inline hex strings in the old OnRender.
        Assert.Equal(ColourRamps.FromHex("#F3C13A"), PqDiagramGeometry.DotNear.Colour);
        Assert.Equal(ColourRamps.FromHex("#FF5B5B"), PqDiagramGeometry.DotAtLimit.Colour);
    }

    [Fact]
    public void The_old_property_defaults_are_the_shared_defaults()
    {
        // The Avalonia controls take their defaults from the same numbers; pin the old WPF defaults here.
        var g = new Old.Gauge();
        Assert.Equal((0.0, 0.0, 100.0, 1.0, 1.0, 0.0, 0.0), (g.Value, g.Minimum, g.Maximum, g.AmberFrom, g.RedFrom, g.AmberTo, g.RedTo));
        Assert.Equal(("", "", ""), (g.Unit, g.Caption, g.ValueText));
        var ph = new Old.Phasor();
        Assert.Equal((0.0, 0.0, 0.0, true), (ph.P, ph.Q, ph.SMax, ph.ShowPowerFactor));
        var pq = new Old.PqDiagram();
        Assert.Equal((0.0, 0.0, -1.0, 1.0, 1.0), (pq.Pg, pq.Qg, pq.Qmin, pq.Qmax, pq.Pmax));
        var sp = new Old.Sparkline();
        Assert.Null(sp.Values);
        Assert.False(sp.ShowMinMax);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // A few direct pins on the geometry (what the old code did, stated as numbers)
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_geometries_refuse_to_draw_below_the_old_minimum_sizes()
    {
        Assert.Null(GaugeGeometry.Compute(29.999, 100, 0, 0, 100, 1, 1, 0, 0, ""));
        Assert.NotNull(GaugeGeometry.Compute(30, 30, 0, 0, 100, 1, 1, 0, 0, ""));
        Assert.Null(SparklineGeometry.Compute(new[] { 1.0, 2.0 }, 3.999, 10));
        Assert.Null(SparklineGeometry.Compute(new[] { 1.0 }, 100, 10));
        Assert.Null(SparklineGeometry.Compute(null, 100, 10));
        Assert.NotNull(SparklineGeometry.Compute(new[] { 1.0, 2.0 }, 4, 4));
        Assert.Null(FrequencyTraceGeometry.Compute(59.999, 100, null, null, null));
        Assert.Null(FrequencyTraceGeometry.Compute(100, 49.999, null, null, null));
        Assert.NotNull(FrequencyTraceGeometry.Compute(60, 50, null, null, null));
        Assert.Null(PhasorGeometry.Compute(39.999, 100, 1, 1, 0));
        Assert.NotNull(PhasorGeometry.Compute(40, 40, 1, 1, 0));
        Assert.Null(PqDiagramGeometry.Compute(19.999, 100, 0, 0, -1, 1, 1));
        Assert.NotNull(PqDiagramGeometry.Compute(20, 20, 0, 0, -1, 1, 1));
        // Test changed 2026-10-24 (agreed by Mark): a NaN size passed the old size check (NaN < 30 is false), so the old
        // gauge went on with NaN points. The check is now written to refuse NaN (every finite size is unchanged), so a
        // NaN size draws nothing, for the gauge and for every instrument with a size check.
        Assert.Null(GaugeGeometry.Compute(double.NaN, 100, 0, 0, 100, 1, 1, 0, 0, ""));
        Assert.Null(GaugeGeometry.Compute(100, double.NaN, 0, 0, 100, 1, 1, 0, 0, ""));
        Assert.Null(PhasorGeometry.Compute(double.NaN, 100, 1, 1, 0));
        Assert.Null(PqDiagramGeometry.Compute(100, double.NaN, 1, 1, -1, 1, 1));
        Assert.Null(SparklineGeometry.Compute(new double[] { 1, 2, 3 }, double.NaN, 40));
        Assert.NotNull(GaugeGeometry.Compute(30, 30, 0, 0, 100, 1, 1, 0, 0, ""));   // the minimum itself still draws
        Assert.Throws<ArgumentNullException>(() => GaugeGeometry.Compute(100, 100, 0, 0, 100, 1, 1, 0, 0, null!));
    }

    [Fact]
    public void The_geometries_state_the_old_rules()
    {
        // Gauge: the value as a clamped fraction; a zero range is a 1e-9 range; NaN passes through.
        Assert.Equal(1.0, GaugeGeometry.Compute(100, 100, 250, 0, 100, 1, 1, 0, 0, "")!.NeedleFraction);
        Assert.Equal(0.0, GaugeGeometry.Compute(100, 100, -250, 0, 100, 1, 1, 0, 0, "")!.NeedleFraction);
        Assert.Equal(0.0, GaugeGeometry.Compute(100, 100, 5, 5, 5, 1, 1, 0, 0, "")!.NeedleFraction);
        Assert.True(double.IsNaN(GaugeGeometry.Compute(100, 100, double.NaN, 0, 100, 1, 1, 0, 0, "")!.NeedleFraction));
        Assert.Equal("12.35", GaugeGeometry.Compute(100, 100, 12.345678, 0, 100, 1, 1, 0, 0, "")!.ValueLabel);
        Assert.Equal("OFF", GaugeGeometry.Compute(100, 100, 12.3, 0, 100, 1, 1, 0, 0, "OFF")!.ValueLabel);
        Assert.Single(GaugeGeometry.Compute(100, 100, 0, 0, 100, 1, 1, 0, 0, "")!.Bands);                  // track only
        Assert.Equal(5, GaugeGeometry.Compute(100, 100, 0, 0, 100, 0.7, 0.9, 0.3, 0.1, "")!.Bands.Count);
        Assert.Equal(9, GaugeGeometry.Compute(100, 100, 0, 0, 100, 1, 1, 0, 0, "")!.Ticks.Count);
        Assert.Null(GaugeGeometry.Arc(new(50, 50), 40, 0.5, 0.50009));                                     // under 1e-4
        Assert.True(GaugeGeometry.Arc(new(50, 50), 40, 0, 1)!.Value.IsLargeArc);                           // 240 deg
        Assert.False(GaugeGeometry.Arc(new(50, 50), 40, 0, 0.75)!.Value.IsLargeArc);                       // exactly 180 deg

        // Sparkline: LINQ Min is NaN when any value is NaN; Max skips NaN.
        SparklineScene sp = SparklineGeometry.Compute(new[] { 1.0, double.NaN, 3.0 }, 100, 20)!;
        Assert.True(double.IsNaN(sp.DataMin));
        Assert.Equal(3.0, sp.DataMax);
        Assert.Equal(3 + 2, SparklineGeometry.Compute(new[] { 1.0, 2.0 }, 10, 10)!.FillPolygon.Count);
        Assert.Equal("-0", SparklineGeometry.Label(-0.004));

        // FrequencyTrace: 30 s when nothing sizes the chart; the GB scale is the constants; time labels every 5 s.
        FrequencyTraceScene ft = FrequencyTraceGeometry.Compute(400, 200, null, null, null)!;
        Assert.Equal(30.0, ft.Duration);
        Assert.Equal((48.6, 50.5), (ft.FMin, ft.FMax));
        Assert.Equal(new[] { "0s", "5s", "10s", "15s", "20s", "25s", "30s" }, ft.TimeTicks.Select(t => t.Label));
        Assert.Equal(new[] { "50.0", "49.8", "49.5", "48.8" }, ft.LimitLines.Select(l => l.Label));
        Assert.False(ft.ShowLegend);
        Assert.Equal(FrequencyTraceGeometry.LfddPen, FrequencyTraceGeometry.PenForKind("anything else"));
        Assert.Equal("baseline reaches 48.10 Hz, below the 48.6 Hz floor: drawn along the edge",
            FrequencyTraceGeometry.OffScaleText("baseline", 48.1, 48.6, 50.5));
        Assert.Equal("+ TSGB reaches 50.90 Hz, above the 50.5 Hz ceiling: drawn along the edge",
            FrequencyTraceGeometry.OffScaleText("+ TSGB", 50.9, 48.6, 50.5));

        // Phasor: the deadband and the states.
        Assert.Equal(PhasorState.Unity, PhasorGeometry.Compute(100, 100, 1, 1e-6, 0)!.State);
        Assert.Equal(PhasorState.Lagging, PhasorGeometry.Compute(100, 100, 1, 2e-6, 0)!.State);
        Assert.Equal(PhasorState.Leading, PhasorGeometry.Compute(100, 100, 1, -2e-6, 0)!.State);
        Assert.Null(PhasorGeometry.Compute(100, 100, 1, 0.01, 0)!.PhiArc);                                 // |phi| under 0.02
        Assert.Equal(1.0, PhasorGeometry.Compute(100, 100, 0, 0, 0)!.PowerFactor);

        // PqDiagram: the stress bands (strictly above 0.85 and 0.95; NaN is normal).
        Assert.Equal(PqStress.Normal, PqDiagramGeometry.StressOf(0.85));
        Assert.Equal(PqStress.Near, PqDiagramGeometry.StressOf(0.8500001));
        Assert.Equal(PqStress.Near, PqDiagramGeometry.StressOf(0.95));
        Assert.Equal(PqStress.AtLimit, PqDiagramGeometry.StressOf(0.9500001));
        Assert.Equal(PqStress.Normal, PqDiagramGeometry.StressOf(double.NaN));
        Assert.Equal(5, PqDiagramGeometry.Compute(200, 140, 0, 0, -1, 1, 1)!.Boundary.Count);
    }
}
