namespace GridSim.Core.IO.Geo;

/// <summary>
/// Ordnance Survey National Grid (OSGB36, EPSG:27700) easting / northing to latitude / longitude, and the GB map
/// layout GridSim draws buses on (S2 / P02, October 2026).
/// </summary>
/// <remarks>
/// <para>
/// Method: Ordnance Survey, "A guide to coordinate systems in Great Britain", Annex C (transverse Mercator formulae):
/// the inverse projection on the Airy 1830 ellipsoid gives OSGB36 latitude / longitude (iterated until the meridional
/// arc matches the northing to 0.01 mm), then the guide's published 7-parameter Helmert transformation OSGB36 to WGS84
/// (stated accuracy about 5 m) through Earth-centred cartesian coordinates. Every constant below is the OS guide's
/// published value, written out once here. The guide is not in docs/reference; the tests check this code against
/// the guide's worked example (Annex C) and against the independent WGS84 positions PyPSA-GB ships
/// (reduced_network/buses_wgs84_backup.csv), which it matches to 4.3e-8 degrees (about 5 mm).
/// </para>
/// <para>
/// Use: the PyPSA-GB reduced network gives its 29 substations in OSGB metres. GridSim's <see cref="Model.Bus.X"/> /
/// <see cref="Model.Bus.Y"/> are a 0..1 map layout over a fixed GB box (<see cref="ToGbLayout"/>), so the bus is drawn
/// where it is. A few metres of datum error is far below one pixel of that layout.
/// </para>
/// </remarks>
public static class OsgbGrid
{
    // ---- Airy 1830 ellipsoid (the OSGB36 datum), metres --------------------------------------------------------------
    private const double AiryA = 6377563.396;
    private const double AiryB = 6356256.909;

    // ---- GRS80 ellipsoid (WGS84 to within a millimetre at this scale), metres -----------------------------------------
    private const double Grs80A = 6378137.000;
    private const double Grs80B = 6356752.3141;

    // ---- National Grid projection: scale on the central meridian, true origin 49 N 2 W, false origin --------------------
    private const double F0 = 0.9996012717;
    private const double Lat0Deg = 49.0;
    private const double Lon0Deg = -2.0;
    private const double E0 = 400000.0;
    private const double N0 = -100000.0;

    // ---- Helmert OSGB36 -> WGS84: translations (m), scale (ppm), rotations (arc-seconds) --------------------------------
    private const double Tx = 446.448;
    private const double Ty = -125.157;
    private const double Tz = 542.060;
    private const double ScalePpm = -20.4894;
    private const double RxArcSec = 0.1502;
    private const double RyArcSec = 0.2470;
    private const double RzArcSec = 0.8421;

    /// <summary>The meridional-arc iteration stops when the arc matches the northing to this (metres): 0.01 mm, the OS
    /// guide's own stopping rule (Annex C).</summary>
    private const double ArcToleranceM = 1e-5;

    /// <summary>Iterations of the latitude refinement (OS guide, Annex C) and of the cartesian-to-geodetic step. Both converge
    /// in a handful; the cap only guards against a non-finite input looping for ever (it is refused first anyway).</summary>
    private const int MaxIterations = 100;

    /// <summary>
    /// Convert National Grid easting / northing (metres, OSGB36) to OSGB36 latitude / longitude in degrees: the inverse
    /// projection only, no datum change. This is the quantity the OS guide's worked example (Annex C) prints, so it is
    /// what the tests check against the published numbers.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Easting or northing is not a finite number.</exception>
    public static (double LatDeg, double LonDeg) ToOsgb36LatLon(double easting, double northing)
    {
        RequireFinite(easting, nameof(easting));
        RequireFinite(northing, nameof(northing));
        double lat0 = Deg2Rad(Lat0Deg), lon0 = Deg2Rad(Lon0Deg);
        double e2 = 1.0 - (AiryB * AiryB) / (AiryA * AiryA);

        // Iterate the latitude until the developed meridional arc M reaches the northing (OS guide, Annex C).
        double lat = lat0, m = 0.0;
        for (int i = 0; ; i++)
        {
            lat = (northing - N0 - m) / (AiryA * F0) + lat;
            m = MeridionalArc(lat, lat0);
            if (Math.Abs(northing - N0 - m) < ArcToleranceM) break;
            if (i >= MaxIterations)
                throw new InvalidOperationException($"OSGB inverse projection did not settle for northing {northing} m.");
        }

        double s = DetMath.Sin(lat), c = DetMath.Cos(lat), t = DetMath.Tan(lat);
        double nu = AiryA * F0 / Math.Sqrt(1 - e2 * s * s);
        double rho = AiryA * F0 * (1 - e2) * DetMath.Pow(1 - e2 * s * s, -1.5);
        double eta2 = nu / rho - 1;
        double t2 = t * t, t4 = t2 * t2, t6 = t4 * t2;

        // The OS guide's series coefficients VII..XIIA (Annex C). D1 (October 2026): the whole-number powers of nu and
        // of the easting offset are plain repeated multiplication (deterministic and cheaper than a general pow).
        double nu3 = nu * nu * nu, nu5 = nu3 * nu * nu, nu7 = nu5 * nu * nu;
        double vii = t / (2 * rho * nu);
        double viii = t / (24 * rho * nu3) * (5 + 3 * t2 + eta2 - 9 * t2 * eta2);
        double ix = t / (720 * rho * nu5) * (61 + 90 * t2 + 45 * t4);
        double x = 1 / (c * nu);
        double xi = 1 / (c * 6 * nu3) * (nu / rho + 2 * t2);
        double xii = 1 / (c * 120 * nu5) * (5 + 28 * t2 + 24 * t4);
        double xiia = 1 / (c * 5040 * nu7) * (61 + 662 * t2 + 1320 * t4 + 720 * t6);

        double de = easting - E0;
        double de3 = de * de * de, de4 = de3 * de, de5 = de4 * de, de6 = de5 * de, de7 = de6 * de;
        double latOut = lat - vii * de * de + viii * de4 - ix * de6;
        double lonOut = lon0 + x * de - xi * de3 + xii * de5 - xiia * de7;
        return (Rad2Deg(latOut), Rad2Deg(lonOut));
    }

    /// <summary>
    /// Convert National Grid easting / northing (metres, OSGB36) to WGS84 latitude / longitude in degrees: the inverse
    /// projection (<see cref="ToOsgb36LatLon"/>) followed by the OS 7-parameter Helmert transformation (about 5 m).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Easting or northing is not a finite number.</exception>
    public static (double LatDeg, double LonDeg) ToWgs84(double easting, double northing)
    {
        (double latDeg, double lonDeg) = ToOsgb36LatLon(easting, northing);
        double lat = Deg2Rad(latDeg), lon = Deg2Rad(lonDeg);

        // OSGB36 geodetic -> cartesian on Airy 1830 (height taken as 0: the bus positions carry none).
        double e2 = 1.0 - (AiryB * AiryB) / (AiryA * AiryA);
        double s = DetMath.Sin(lat), c = DetMath.Cos(lat);
        double nu = AiryA / Math.Sqrt(1 - e2 * s * s);
        double x1 = nu * c * DetMath.Cos(lon), y1 = nu * c * DetMath.Sin(lon), z1 = (1 - e2) * nu * s;

        // Helmert (small-angle form, as the OS guide gives it).
        double sc = ScalePpm * 1e-6;
        double rx = Deg2Rad(RxArcSec / 3600.0), ry = Deg2Rad(RyArcSec / 3600.0), rz = Deg2Rad(RzArcSec / 3600.0);
        double x2 = Tx + (1 + sc) * x1 - rz * y1 + ry * z1;
        double y2 = Ty + rz * x1 + (1 + sc) * y1 - rx * z1;
        double z2 = Tz - ry * x1 + rx * y1 + (1 + sc) * z1;

        // Cartesian -> geodetic on GRS80, iterating the latitude (it settles to double precision in a few steps).
        double e2w = 1.0 - (Grs80B * Grs80B) / (Grs80A * Grs80A);
        double p = Math.Sqrt(x2 * x2 + y2 * y2);
        double phi = DetMath.Atan2(z2, p * (1 - e2w));
        for (int i = 0; i < MaxIterations; i++)
        {
            double nuW = Grs80A / Math.Sqrt(1 - e2w * DetMath.Sin(phi) * DetMath.Sin(phi));
            double next = DetMath.Atan2(z2 + e2w * nuW * DetMath.Sin(phi), p);
            if (next == phi) break;
            phi = next;
        }
        return (Rad2Deg(phi), Rad2Deg(DetMath.Atan2(y2, x2)));
    }

    // ---- GB map layout ---------------------------------------------------------------------------------------------

    /// <summary>True when the point lies inside the GB layout box (edges included), so <see cref="ToGbLayout"/> places it
    /// exactly; false when it clamps it onto the box edge (Shetland, at about 60.4 N, is above the 59.0 N top).</summary>
    public static bool IsInsideGbLayout(double latDeg, double lonDeg)
    {
        RequireFinite(latDeg, nameof(latDeg));
        RequireFinite(lonDeg, nameof(lonDeg));
        double x = (lonDeg - LayoutWestLonDeg) / LayoutWidthLonDeg;
        double y = (latDeg - LayoutSouthLatDeg) / LayoutHeightLatDeg;
        return x >= 0.0 && x <= 1.0 && y >= 0.0 && y <= 1.0;
    }

    /// <summary>West edge of the GB layout box (degrees longitude).</summary>
    public const double LayoutWestLonDeg = -8.2;
    /// <summary>Width of the GB layout box (degrees longitude), about 660 km at 54 N.</summary>
    public const double LayoutWidthLonDeg = 10.1;
    /// <summary>South edge of the GB layout box (degrees latitude).</summary>
    public const double LayoutSouthLatDeg = 49.8;
    /// <summary>Height of the GB layout box (degrees latitude), about 1,020 km.</summary>
    public const double LayoutHeightLatDeg = 9.2;

    /// <summary>
    /// Map a WGS84 latitude / longitude to GridSim's 0..1 GB layout: x = (lon + 8.2) / 10.1, y = (lat - 49.8) / 9.2,
    /// each clamped to [0, 1]. This is the box the bundled GB JSON cases were laid out on (GDA NetworkModel
    /// <c>schemas.geo_xy</c>, the source of data/gb-full's x / y) and the box <c>PropagationDelay</c> assumes for its
    /// 660 km x 1,020 km span, so a reference model drawn with it lands on the same map.
    /// </summary>
    public static (double X, double Y) ToGbLayout(double latDeg, double lonDeg)
    {
        // Bug hunt 2026-10-29, finding 56: the clamp moves a point outside the box onto its edge. Callers that load
        // geographic data say so through IsInsideGbLayout (a zone's LayoutClamped flag, an importer note), so a point
        // drawn on the edge is not taken for a point that is there.
        RequireFinite(latDeg, nameof(latDeg));
        RequireFinite(lonDeg, nameof(lonDeg));
        double x = (lonDeg - LayoutWestLonDeg) / LayoutWidthLonDeg;
        double y = (latDeg - LayoutSouthLatDeg) / LayoutHeightLatDeg;
        return (Math.Clamp(x, 0.0, 1.0), Math.Clamp(y, 0.0, 1.0));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>The developed meridional arc from the true origin to <paramref name="lat"/> (radians), Airy 1830,
    /// scaled by F0 (OS guide, Annex C).</summary>
    private static double MeridionalArc(double lat, double lat0)
    {
        double n = (AiryA - AiryB) / (AiryA + AiryB);
        double n2 = n * n, n3 = n2 * n;
        double d = lat - lat0, sum = lat + lat0;
        return AiryB * F0 * (
            (1 + n + 1.25 * n2 + 1.25 * n3) * d
            - (3 * n + 3 * n2 + 2.625 * n3) * DetMath.Sin(d) * DetMath.Cos(sum)
            + (1.875 * n2 + 1.875 * n3) * DetMath.Sin(2 * d) * DetMath.Cos(2 * sum)
            - (35.0 / 24.0) * n3 * DetMath.Sin(3 * d) * DetMath.Cos(3 * sum));
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
    private static double Rad2Deg(double r) => r * 180.0 / Math.PI;

    private static void RequireFinite(double v, string name)
    {
        if (!double.IsFinite(v))
            throw new ArgumentOutOfRangeException(name, v, $"{name} must be a finite number.");
    }
}
