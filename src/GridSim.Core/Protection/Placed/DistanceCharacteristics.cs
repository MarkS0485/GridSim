using System;
using System.Numerics;
using GridSim.Core.Numerics;
using GridSim.Core.Protection.Devices;

namespace GridSim.Core.Protection.Placed;

/// <summary>
/// The operating characteristics of a placed distance zone (RC5 WP4), as pure functions of the measured impedance Z
/// (secondary ohms). Sources: G. Ziegler, Numerical Distance Protection (4th ed.), ch. 3; Network Protection &amp;
/// Automation Guide (NPAG), ch. 11.
/// </summary>
public static class DistanceCharacteristics
{
    /// <summary>The mho circle through the origin with diameter <paramref name="zr"/>: the phase comparator
    /// Re(Z conj(Zr - Z)) &gt;= 0 (the angle between Z and Zr - Z is at most 90 degrees). No trigonometry on Z.</summary>
    public static bool InMho(Complex z, Complex zr)
    {
        Complex d = zr - z;
        return z.Real * d.Real + z.Imaginary * d.Imaginary >= 0.0;
    }

    /// <summary>The reach of a zone as a complex impedance: |Zr| at the zone angle.</summary>
    public static Complex Reach(DistanceZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DetMath.FromPolar(zone.ReachOhms, zone.AngleDeg * Math.PI / 180.0);
    }

    /// <summary>
    /// The quadrilateral with zone angle theta: operate when (1) Im Z &lt;= Xr = |Zr| sin theta (the reactance line, not
    /// tilted); (2) Re Z - Im Z / tan theta &lt;= R (the right blinder, parallel to the zone angle); (3) Re Z - Im Z / tan
    /// theta &gt;= -RL (the left blinder); (4) <paramref name="directionalLowDeg"/> &lt;= arg Z &lt;=
    /// <paramref name="directionalHighDeg"/>. A zero Z has no argument and does not operate.
    /// </summary>
    public static bool InQuad(Complex z, DistanceZone zone, double directionalLowDeg, double directionalHighDeg)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (z == Complex.Zero) return false;
        double th = zone.AngleDeg * Math.PI / 180.0;
        (double sin, double cos) = DetMath.SinCos(th);
        double xr = zone.ReachOhms * sin;
        if (!(z.Imaginary <= xr)) return false;
        double offset = z.Real - z.Imaginary * cos / sin;
        if (!(offset <= zone.ResistiveReachOhms) || !(offset >= -zone.LeftReachOhms)) return false;
        double arg = DetMath.Phase(z) * 180.0 / Math.PI;
        return arg >= directionalLowDeg && arg <= directionalHighDeg;
    }

    /// <summary>Whether <paramref name="z"/> lies in <paramref name="zone"/> of <paramref name="setting"/>.</summary>
    public static bool InZone(Complex z, DistanceZone zone, DistanceSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return setting.Characteristic == DistanceCharacteristic.Mho ? InMho(z, Reach(zone)) : InQuad(z, zone, setting.DirectionalLowDeg, setting.DirectionalHighDeg);
    }
}
