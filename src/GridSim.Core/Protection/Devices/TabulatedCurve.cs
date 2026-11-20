using System;
using System.Collections.Generic;
using GridSim.Core.Numerics;

namespace GridSim.Core.Protection.Devices;

/// <summary>What a tabulated time-current curve describes: a fuse's minimum melting time, a fuse's total clearing time,
/// or a relay / recloser operating characteristic.</summary>
public enum CurveKind { MinimumMelt, TotalClear, Operate }

/// <summary>Where a curve came from. Required on every data file (RC4 decision 4): a curve without provenance is refused.
/// <see cref="Kind"/> is <c>standard</c> (a published equation), <c>manufacturer</c> (a publicly documented manufacturer
/// table) or <c>synthetic</c> (a test fixture that makes no vendor claim).</summary>
public sealed record CurveSource(string Kind, string Title, string Publisher, string Url, string Locator, string Retrieved, string? DocumentSha256);

/// <summary>
/// A time-current curve given as points (amps, seconds) and interpolated linearly in log-log coordinates:
/// <c>ln t = ln t1 + (ln I - ln I1) / (ln I2 - ln I1) * (ln t2 - ln t1)</c> between neighbouring points. Below the first
/// point's current the device does not operate (+infinity); above the last point the last time is held and the result is
/// flagged <c>aboveRange</c> (never extrapolated silently).
/// </summary>
public sealed class TabulatedCurve
{
    public string Id { get; }
    public string Description { get; }
    public CurveKind Kind { get; }
    public CurveSource Source { get; }
    public IReadOnlyList<(double CurrentA, double TimeS)> Points { get; }

    public TabulatedCurve(string id, string description, CurveKind kind, CurveSource source, IReadOnlyList<(double CurrentA, double TimeS)> points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) throw new ArgumentException($"Curve '{id}' needs at least two points.", nameof(points));
        for (int i = 0; i < points.Count; i++)
        {
            (double a, double t) = points[i];
            if (!(a > 0.0) || !double.IsFinite(a) || !(t > 0.0) || !double.IsFinite(t))
                throw new ArgumentException($"Curve '{id}' point {i}: current and time must be finite and > 0.", nameof(points));
            if (i > 0 && !(a > points[i - 1].CurrentA))
                throw new ArgumentException($"Curve '{id}' point {i}: currents must be strictly increasing.", nameof(points));
            if (i > 0 && t > points[i - 1].TimeS)
                throw new ArgumentException($"Curve '{id}' point {i}: times must not increase with current.", nameof(points));
        }
        Id = id; Description = description; Kind = kind; Source = source; Points = points;
    }

    /// <summary>The time (s) at <paramref name="currentA"/>; +infinity below the first point.</summary>
    public double TimeAt(double currentA, out bool aboveRange)
    {
        aboveRange = false;
        if (double.IsNaN(currentA) || currentA < 0.0) throw new ArgumentOutOfRangeException(nameof(currentA), currentA, "The current must be >= 0.");
        if (currentA < Points[0].CurrentA) return double.PositiveInfinity;
        int last = Points.Count - 1;
        if (currentA > Points[last].CurrentA) { aboveRange = true; return Points[last].TimeS; }
        if (currentA == Points[last].CurrentA) return Points[last].TimeS;
        int k = 0;
        while (k + 1 < last && currentA >= Points[k + 1].CurrentA) k++;
        (double i1, double t1) = Points[k];
        (double i2, double t2) = Points[k + 1];
        if (currentA == i1) return t1;
        double u = (DetMath.Log(currentA) - DetMath.Log(i1)) / (DetMath.Log(i2) - DetMath.Log(i1));
        return DetMath.Exp(DetMath.Log(t1) + u * (DetMath.Log(t2) - DetMath.Log(t1)));
    }
}
