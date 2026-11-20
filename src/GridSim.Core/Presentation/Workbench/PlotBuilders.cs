using GridSim.Core.Analysis.VoltageStability;
using GridSim.Core.Numerics;
using GridSim.Core.Protection.Coordination;

namespace GridSim.Core.Presentation.Workbench;

/// <summary>One named data series: x and y of equal length, in source order. A NaN or non-finite y is a missing value
/// (a gap in the plot, never a zero); x must be finite.</summary>
public sealed record PlotSeriesData(string Name, IReadOnlyList<double> X, IReadOnlyList<double> Y);

/// <summary>One named point set of an R-X diagram: a locus (a closed polygon of its points) or a set of single points.</summary>
public sealed record RxSeries(string Name, IReadOnlyList<double> R, IReadOnlyList<double> X, bool Locus);

/// <summary>
/// The plot kit (R4-10): builders for the time-current (TCC) plot, the R-X diagram, the harmonic spectrum, the eigenvalue
/// plane, the Bode plot, the QV and PV curves and the plain time series. Each returns a <see cref="PlotSpec"/>: plain
/// geometry whose every coordinate is computed here, deterministically.
/// <para><b>Order of operations</b> (as the work order states it): the data points are taken in source order; the axis range
/// follows from the data by a fixed rule; the ticks follow from the range by a fixed rule; then the geometry is mapped.</para>
/// <para><b>Linear axis rule.</b> The range is the data minimum to maximum (widened to include zero where the plot says so;
/// a single value is widened by 10 per cent of itself, or 1 when it is zero). The tick step is the 1, 2 or 5 times a power
/// of ten at or above one fifth of the span; the axis then runs from the tick at or below the minimum to the tick at or
/// above the maximum, so ticks are the multiples of the step. <b>Log axis rule.</b> The data must be positive; the range is
/// the decade at or below the smallest value to the decade at or above the largest, with a tick at every decade.</para>
/// Transcendental maths is <see cref="DetMath"/> only (the log axes); the tick arithmetic is exact powers of ten.
/// </summary>
public static class PlotBuilders
{
    public const double DefaultWidth = 640;
    public const double DefaultHeight = 400;
    private const double MarginLeft = 64, MarginRight = 20, MarginTop = 36, MarginBottom = 52, PanelGap = 40;

    // ------------------------------------------------------------------ the public builders

    /// <summary>A plain line plot (a time series, or any y against x), linear on both axes.</summary>
    public static PlotSpec TimeSeries(string id, string title, string xLabel, string yLabel, IReadOnlyList<PlotSeriesData> series)
        => SinglePanel(id, title, xLabel, yLabel, false, false, series, false, null);

    /// <summary>A line plot with a choice of linear or log axes (a log axis needs positive values; a non-positive y is a gap).</summary>
    public static PlotSpec Lines(string id, string title, string xLabel, string yLabel, IReadOnlyList<PlotSeriesData> series, bool logX, bool logY)
        => SinglePanel(id, title, xLabel, yLabel, logX, logY, series, false, null);

    /// <summary>
    /// The time-current characteristic plot: one curve per element, the operating time (s, log) against the current (log), at the
    /// given currents in the given order. Each plotted y is exactly <see cref="OvercurrentCoordination.OperateTimeSeconds"/> at that
    /// current; a current at which the element never operates is a gap. Each element's pickup is a vertical rule.
    /// </summary>
    public static PlotSpec Tcc(string id, string title, IReadOnlyList<(string Name, OvercurrentElement Element)> elements, IReadOnlyList<double> currents)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(currents);
        var data = new List<PlotSeriesData>();
        foreach ((string name, OvercurrentElement el) in elements)
        {
            var y = new double[currents.Count];
            for (int i = 0; i < y.Length; i++)
            {
                double t = OvercurrentCoordination.OperateTimeSeconds(el, currents[i]);
                y[i] = double.IsInfinity(t) ? double.NaN : t;
            }
            data.Add(new PlotSeriesData(name, currents, y));
        }
        var pickups = elements.Select(e => e.Element.PickupCurrent).Where(p => p > 0 && double.IsFinite(p)).ToList();
        return SinglePanel(id, title, "Current", "Operating time (s)", true, true, data, false, (sx, sy) =>
        {
            var items = new List<PlotItem>();
            for (int i = 0; i < elements.Count; i++)
            {
                double ip = elements[i].Element.PickupCurrent;
                double px = sx.Map(ip);
                if (double.IsFinite(px) && ip >= sx.Axis.Min && ip <= sx.Axis.Max)
                    items.Add(new PlotRule("pickup " + elements[i].Name, px, sy.P0, px, sy.P1));
            }
            return items;
        }, pickups.Select(p => (p, double.NaN)).ToList());
    }

    /// <summary>The R-X diagram: single points as markers and loci as closed polygons with vertex markers; both axes include zero.</summary>
    public static PlotSpec RxDiagram(string id, string title, IReadOnlyList<RxSeries> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var pts = new List<(double, double)>();
        foreach (RxSeries s in series)
        {
            if (s.R.Count != s.X.Count) throw new ArgumentException($"Series '{s.Name}': R and X differ in length.");
            for (int i = 0; i < s.R.Count; i++) if (double.IsFinite(s.R[i]) && double.IsFinite(s.X[i])) pts.Add((s.R[i], s.X[i]));
        }
        (double w, double h) = Size();
        PlotPanel panel = Panel(MarginLeft, MarginTop, w, h, "R (pu)", "X (pu)", false, false, pts, true, true, (sx, sy) =>
        {
            var items = new List<PlotItem>();
            int style = 0;
            foreach (RxSeries s in series)
            {
                var polyPts = new List<PlotPoint>();
                for (int i = 0; i < s.R.Count; i++)
                {
                    bool ok = double.IsFinite(s.R[i]) && double.IsFinite(s.X[i]);
                    if (!ok) { items.Add(new PlotGap(s.Name, s.R[i], sx.Map(s.R[i]), "no value")); continue; }
                    polyPts.Add(new PlotPoint(s.R[i], s.X[i], sx.Map(s.R[i]), sy.Map(s.X[i])));
                }
                if (s.Locus && polyPts.Count > 1) items.Add(new PlotPolyline(s.Name, style, polyPts, true));
                foreach (PlotPoint p in polyPts)
                    items.Add(new PlotMarker(s.Name, style, s.Locus ? PlotMarkerShape.Square : PlotMarkerShape.Circle, p.DataX, p.DataY, p.Px, p.Py, null));
                style++;
            }
            items.Add(new PlotRule("R = 0", sx.Map(0), sy.P0, sx.Map(0), sy.P1));
            items.Add(new PlotRule("X = 0", sx.P0, sy.Map(0), sx.P1, sy.Map(0)));
            return items;
        });
        return Wrap(id, title, [panel], series.Select(s => s.Name).ToList());
    }

    /// <summary>
    /// The harmonic spectrum: one bar per order, centred on the order, from zero up to the amplitude. The y axis includes zero;
    /// the bar width is 0.6 of the smallest spacing between orders. A missing amplitude is a gap at that order.
    /// </summary>
    public static PlotSpec Spectrum(string id, string title, IReadOnlyList<double> orders, IReadOnlyList<double> amplitudes,
        string yLabel = "Amplitude", string seriesName = "spectrum")
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(amplitudes);
        if (orders.Count != amplitudes.Count) throw new ArgumentException("Orders and amplitudes differ in length.");
        var finite = new List<(double, double)>();
        for (int i = 0; i < orders.Count; i++)
        {
            RequireFiniteX(orders[i], "order");
            if (double.IsFinite(amplitudes[i])) finite.Add((orders[i], amplitudes[i]));
        }
        double gap = 1;
        var sorted = orders.OrderBy(o => o).ToList();
        double smallest = double.PositiveInfinity;
        for (int i = 1; i < sorted.Count; i++) { double d = sorted[i] - sorted[i - 1]; if (d > 0 && d < smallest) smallest = d; }
        if (double.IsFinite(smallest)) gap = smallest;
        // The x range is the orders widened by half a bar spacing, so the end bars sit inside the frame.
        var range = new List<(double, double)>(finite);
        if (orders.Count > 0) { range.Add((sorted[0] - gap / 2, 0)); range.Add((sorted[^1] + gap / 2, 0)); }
        (double w, double h) = Size();
        PlotPanel panel = Panel(MarginLeft, MarginTop, w, h, "Harmonic order", yLabel, false, false, range, false, true, (sx, sy) =>
        {
            var items = new List<PlotItem>();
            double barW = 0.6 * gap * Math.Abs(sx.Map(1) - sx.Map(0));
            double y0 = sy.Map(0);
            for (int i = 0; i < orders.Count; i++)
            {
                double px = sx.Map(orders[i]);
                if (!double.IsFinite(amplitudes[i])) { items.Add(new PlotGap(seriesName, orders[i], px, "no value")); continue; }
                double py = sy.Map(amplitudes[i]);
                items.Add(new PlotBar(seriesName, 0, orders[i], amplitudes[i], px, Math.Min(py, y0), barW, Math.Abs(y0 - py)));
            }
            return items;
        });
        return Wrap(id, title, [panel], [seriesName]);
    }

    /// <summary>The eigenvalue plane: a cross at each eigenvalue (real part on x, imaginary part on y), the imaginary axis drawn as the
    /// stability boundary and the real axis as a rule. Both axes include zero.</summary>
    public static PlotSpec EigenvaluePlane(string id, string title, IReadOnlyList<double> real, IReadOnlyList<double> imag,
        IReadOnlyList<string?>? labels = null)
    {
        ArgumentNullException.ThrowIfNull(real);
        ArgumentNullException.ThrowIfNull(imag);
        if (real.Count != imag.Count) throw new ArgumentException("Real and imaginary parts differ in length.");
        var pts = new List<(double, double)>();
        for (int i = 0; i < real.Count; i++) if (double.IsFinite(real[i]) && double.IsFinite(imag[i])) pts.Add((real[i], imag[i]));
        (double w, double h) = Size();
        PlotPanel panel = Panel(MarginLeft, MarginTop, w, h, "Real part (1/s)", "Imaginary part (rad/s)", false, false, pts, true, true, (sx, sy) =>
        {
            var items = new List<PlotItem>();
            for (int i = 0; i < real.Count; i++)
            {
                if (!(double.IsFinite(real[i]) && double.IsFinite(imag[i]))) { items.Add(new PlotGap("eigenvalues", real[i], sx.Map(real[i]), "no value")); continue; }
                items.Add(new PlotMarker("eigenvalues", 0, PlotMarkerShape.Cross, real[i], imag[i], sx.Map(real[i]), sy.Map(imag[i]),
                    labels is not null && i < labels.Count ? labels[i] : null));
            }
            items.Add(new PlotRule("stability boundary", sx.Map(0), sy.P0, sx.Map(0), sy.P1));
            items.Add(new PlotRule("real axis", sx.P0, sy.Map(0), sx.P1, sy.Map(0)));
            return items;
        });
        return Wrap(id, title, [panel], ["eigenvalues"]);
    }

    /// <summary>The Bode plot: magnitude (dB) over phase (degrees), both against a common log frequency axis (Hz).</summary>
    public static PlotSpec Bode(string id, string title, IReadOnlyList<double> frequencyHz, IReadOnlyList<double> magnitudeDb, IReadOnlyList<double> phaseDeg)
    {
        ArgumentNullException.ThrowIfNull(frequencyHz);
        if (magnitudeDb.Count != frequencyHz.Count || phaseDeg.Count != frequencyHz.Count) throw new ArgumentException("Frequency, magnitude and phase differ in length.");
        foreach (double f in frequencyHz) if (!(f > 0 && double.IsFinite(f))) throw new ArgumentException("A Bode frequency must be finite and positive.");
        double total = DefaultHeight - MarginTop - MarginBottom - PanelGap;
        double ph = total / 2;
        (double w, _) = Size();
        double xmin = frequencyHz.Count == 0 ? 1 : frequencyHz.Min(), xmax = frequencyHz.Count == 0 ? 10 : frequencyHz.Max();
        PlotPanel Mk(double top, string yLabel, IReadOnlyList<double> y, string name, string xLabel)
        {
            var pts = new List<(double, double)>();
            pts.Add((xmin, double.NaN)); pts.Add((xmax, double.NaN));
            for (int i = 0; i < y.Count; i++) if (double.IsFinite(y[i])) pts.Add((frequencyHz[i], y[i]));
            return Panel(MarginLeft, top, w, ph, xLabel, yLabel, true, false, pts, false, false, (sx, sy) => new List<PlotItem>(LineItems(sx, sy, [new PlotSeriesData(name, frequencyHz, y)])));
        }
        PlotPanel mag = Mk(MarginTop, "Magnitude (dB)", magnitudeDb, "magnitude", "");
        PlotPanel phase = Mk(MarginTop + ph + PanelGap, "Phase (deg)", phaseDeg, "phase", "Frequency (Hz)");
        return Wrap(id, title, [mag, phase], ["magnitude", "phase"]);
    }

    /// <summary>
    /// The QV curve (R4-03) of one bus: the held voltage (pu) on x against the reactive power the bus needed (MVAr) on y, anchored at the
    /// operating point (0 MVAr). A non-converged point is a gap. The operating point and, when bracketed, the nose are marked.
    /// </summary>
    public static PlotSpec QvCurvePlot(string id, string title, QvCurveResult curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var x = curve.Points.Select(p => p.VPu).ToList();
        var y = curve.Points.Select(p => p.Converged ? p.QMvar : double.NaN).ToList();
        var extra = new List<(double, double)>();
        bool nose = curve.Status == "OK" && curve.MinBracketed;
        if (nose) extra.Add((curve.NoseVPu, curve.NoseQMvar));
        PlotSpec spec = SinglePanel(id, title, "Bus voltage (pu)", "Reactive power needed (MVAr)", false, false,
            [new PlotSeriesData("QV bus " + WorkbenchText.Canon(curve.BusId), x, y)], true, (sx, sy) =>
            {
                var items = new List<PlotItem>();
                items.Add(new PlotRule("Q = 0", sx.P0, sy.Map(0), sx.P1, sy.Map(0)));
                if (curve.Points.Count > 0)
                    items.Add(new PlotMarker("operating point", 1, PlotMarkerShape.Circle, curve.BaseVPu, 0, sx.Map(curve.BaseVPu), sy.Map(0), "operating point"));
                if (nose)
                    items.Add(new PlotMarker("nose", 2, PlotMarkerShape.Cross, curve.NoseVPu, curve.NoseQMvar, sx.Map(curve.NoseVPu), sy.Map(curve.NoseQMvar), "nose"));
                return items;
            }, extra);
        return spec;
    }

    /// <summary>A PV curve: the bus voltage (pu) against the transferred or total load (MW). <paramref name="noseIndex"/>, when given,
    /// marks that point as the nose.</summary>
    public static PlotSpec PvCurvePlot(string id, string title, IReadOnlyList<double> loadMw, IReadOnlyList<double> voltagePu, int? noseIndex = null,
        string seriesName = "PV")
    {
        ArgumentNullException.ThrowIfNull(loadMw);
        ArgumentNullException.ThrowIfNull(voltagePu);
        if (loadMw.Count != voltagePu.Count) throw new ArgumentException("Load and voltage differ in length.");
        return SinglePanel(id, title, "Load (MW)", "Bus voltage (pu)", false, false, [new PlotSeriesData(seriesName, loadMw, voltagePu)], false,
            noseIndex is int k && k >= 0 && k < loadMw.Count && double.IsFinite(voltagePu[k])
                ? (sx, sy) => [new PlotMarker("nose", 1, PlotMarkerShape.Cross, loadMw[k], voltagePu[k], sx.Map(loadMw[k]), sy.Map(voltagePu[k]), "nose")]
                : null, null);
    }

    // ------------------------------------------------------------------ geometry machinery

    /// <summary>A built axis with its mapping. P0 is the pixel of <see cref="PlotAxis.Min"/>, P1 that of <see cref="PlotAxis.Max"/>.</summary>
    internal sealed class Scale
    {
        public required PlotAxis Axis { get; init; }
        public required double P0 { get; init; }
        public required double P1 { get; init; }
        public int KMin { get; init; }
        public int KMax { get; init; }

        /// <summary>The pixel of a data value; NaN for a value a log axis cannot show (zero or negative) or a non-finite one.</summary>
        public double Map(double v)
        {
            if (!double.IsFinite(v)) return double.NaN;
            if (Axis.Log)
            {
                if (!(v > 0)) return double.NaN;
                return P0 + (DetMath.Log10(v) - KMin) / (KMax - KMin) * (P1 - P0);
            }
            return P0 + (v - Axis.Min) / (Axis.Max - Axis.Min) * (P1 - P0);
        }
    }

    private static (double W, double H) Size() => (DefaultWidth - MarginLeft - MarginRight, DefaultHeight - MarginTop - MarginBottom);

    private static void RequireFiniteX(double x, string what)
    {
        if (!double.IsFinite(x)) throw new ArgumentException($"A plotted {what} must be finite; a missing value belongs in y.");
    }

    private static PlotSpec Wrap(string id, string title, IReadOnlyList<PlotPanel> panels, IReadOnlyList<string> legend)
        => new(id, title, DefaultWidth, DefaultHeight, panels, legend);

    private static PlotSpec SinglePanel(string id, string title, string xLabel, string yLabel, bool logX, bool logY,
        IReadOnlyList<PlotSeriesData> series, bool includeZeroY, Func<Scale, Scale, IEnumerable<PlotItem>>? extras,
        IReadOnlyList<(double X, double Y)>? extraRange = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        var pts = new List<(double, double)>();
        foreach (PlotSeriesData s in series)
        {
            if (s.X.Count != s.Y.Count) throw new ArgumentException($"Series '{s.Name}': x and y differ in length.");
            for (int i = 0; i < s.X.Count; i++)
            {
                RequireFiniteX(s.X[i], "x value");
                if (logX && !(s.X[i] > 0)) throw new ArgumentException($"Series '{s.Name}': a log x axis needs positive values.");
                pts.Add((s.X[i], double.IsFinite(s.Y[i]) && (!logY || s.Y[i] > 0) ? s.Y[i] : double.NaN));
            }
        }
        if (extraRange is not null) pts.AddRange(extraRange.Select(e => (e.X, e.Y)));
        (double w, double h) = Size();
        PlotPanel panel = Panel(MarginLeft, MarginTop, w, h, xLabel, yLabel, logX, logY, pts, false, includeZeroY,
            (sx, sy) =>
            {
                var items = new List<PlotItem>(LineItems(sx, sy, series));
                if (extras is not null) items.AddRange(extras(sx, sy));
                if (series.All(s => s.X.Count == 0))
                    items.Add(new PlotText(sx.P0 + (sx.P1 - sx.P0) / 2, sy.P0 + (sy.P1 - sy.P0) / 2, "no data", "middle"));
                return items;
            });
        return Wrap(id, title, [panel], series.Select(s => s.Name).ToList());
    }

    private static IEnumerable<PlotItem> LineItems(Scale sx, Scale sy, IReadOnlyList<PlotSeriesData> series)
    {
        int style = 0;
        foreach (PlotSeriesData s in series)
        {
            var pts = new List<PlotPoint>();
            var gaps = new List<PlotItem>();
            for (int i = 0; i < s.X.Count; i++)
            {
                double y = s.Y[i];
                double py = sy.Map(y);
                if (double.IsNaN(py))
                {
                    pts.Add(new PlotPoint(s.X[i], double.NaN, sx.Map(s.X[i]), double.NaN));
                    gaps.Add(new PlotGap(s.Name, s.X[i], sx.Map(s.X[i]), double.IsFinite(y) ? "not on the axis" : "no value"));
                }
                else pts.Add(new PlotPoint(s.X[i], y, sx.Map(s.X[i]), py));
            }
            yield return new PlotPolyline(s.Name, style, pts);
            foreach (PlotItem g in gaps) yield return g;
            style++;
        }
    }

    /// <summary>Build one panel. <paramref name="pts"/> gives the data extents (a NaN component is ignored for that axis).</summary>
    private static PlotPanel Panel(double left, double top, double w, double h, string xLabel, string yLabel, bool logX, bool logY,
        IReadOnlyList<(double X, double Y)> pts, bool includeZeroX, bool includeZeroY, Func<Scale, Scale, IEnumerable<PlotItem>> items)
    {
        Scale sx = BuildScale(xLabel, logX, pts.Select(p => p.X), left, left + w, includeZeroX, 6);
        Scale sy = BuildScale(yLabel, logY, pts.Select(p => p.Y), top + h, top, includeZeroY, 5);
        return new PlotPanel(left, top, w, h, sx.Axis, sy.Axis, items(sx, sy).ToList());
    }

    internal static Scale BuildScale(string label, bool log, IEnumerable<double> values, double p0, double p1, bool includeZero, int targetTicks)
    {
        double dmin = double.PositiveInfinity, dmax = double.NegativeInfinity;
        foreach (double v in values)
        {
            if (!double.IsFinite(v) || (log && !(v > 0))) continue;
            if (v < dmin) dmin = v;
            if (v > dmax) dmax = v;
        }
        bool any = dmin <= dmax;
        if (log)
        {
            if (!any) { dmin = 1; dmax = 10; }
            int kmin = 0;
            while (Pow10(kmin) > dmin) kmin--;
            while (Pow10(kmin + 1) <= dmin) kmin++;
            int kmax = kmin;
            while (Pow10(kmax) < dmax) kmax++;
            if (kmax == kmin) kmax++;
            var ticks = new List<PlotTick>();
            for (int k = kmin; k <= kmax; k++)
                ticks.Add(new PlotTick(Pow10(k), p0 + (double)(k - kmin) / (kmax - kmin) * (p1 - p0), TickLabel(Pow10(k), 0)));
            return new Scale { Axis = new PlotAxis(label, true, Pow10(kmin), Pow10(kmax), ticks), P0 = p0, P1 = p1, KMin = kmin, KMax = kmax };
        }

        if (!any) { dmin = 0; dmax = 1; }
        if (includeZero) { dmin = Math.Min(dmin, 0); dmax = Math.Max(dmax, 0); }
        if (dmin == dmax)
        {
            double pad = dmin == 0 ? 1 : Math.Abs(dmin) * 0.1;
            dmin -= pad; dmax += pad;
        }
        (double step, int decimals) = NiceStep(dmax - dmin, targetTicks);
        long ia = (long)Math.Floor(dmin / step + 1e-9);
        long ib = (long)Math.Ceiling(dmax / step - 1e-9);
        if (ib == ia) ib = ia + 1;
        double amin = Tick(ia, step, decimals), amax = Tick(ib, step, decimals);
        var lin = new List<PlotTick>();
        for (long i = ia; i <= ib; i++)
        {
            double v = Tick(i, step, decimals);
            lin.Add(new PlotTick(v, p0 + (v - amin) / (amax - amin) * (p1 - p0), TickLabel(v, decimals)));
        }
        return new Scale { Axis = new PlotAxis(label, false, amin, amax, lin), P0 = p0, P1 = p1 };
    }

    private static double Tick(long i, double step, int decimals) => Math.Round(i * step, Math.Min(15, decimals + 1));

    /// <summary>An exact power of ten (a product of tens, or the reciprocal of one), for integer exponents in the range plots use.</summary>
    internal static double Pow10(int k)
    {
        double r = 1;
        for (int i = 0; i < Math.Abs(k); i++) r *= 10;
        return k >= 0 ? r : 1 / r;
    }

    /// <summary>The tick step for a span: 1, 2 or 5 times a power of ten, at or above span / target.</summary>
    internal static (double Step, int Decimals) NiceStep(double span, int target)
    {
        double raw = span / target;
        int e = 0;
        while (Pow10(e + 1) <= raw) e++;
        while (Pow10(e) > raw) e--;
        double frac = raw / Pow10(e);
        double m = frac <= 1 ? 1 : frac <= 2 ? 2 : frac <= 5 ? 5 : 10;
        return (m * Pow10(e), Math.Max(0, -e));
    }

    private static string TickLabel(double v, int decimals)
    {
        if (v == 0) return "0";
        double a = Math.Abs(v);
        if (a >= 1e7 || a < 1e-4) return v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
        return Math.Round(v, Math.Min(15, decimals)).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}
