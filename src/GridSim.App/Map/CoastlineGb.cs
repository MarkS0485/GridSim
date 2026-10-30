using GridSim.Ui.Geography;

namespace GridSim.App.Map;

/// <summary>
/// A compact, simplified outline of the Great Britain mainland (~100 points, clockwise from Land's End), for the map's
/// faint coastline backdrop (UI programme Phase 1.5). It is a copy, point for point, of the WPF
/// <c>Visuals/CoastlineGb.cs</c> (which this app cannot reference): the same lon/lat table, projected with the same
/// equirectangular GB bounding box that <c>GridSim.Gda.Geo.GeoLocator</c> uses, so it lines up with buses that were
/// geolocated from real substation lat/lon. The only change is the point type: the neutral <see cref="WorldPoint"/>
/// (the same two doubles) in place of the WPF <c>Point</c>. <c>CoastlineGbTests</c> reads the WPF file at test time and
/// fails if the two tables ever differ.
///
/// The coastline is generic public geography, not grid data, so committing it respects the
/// "converter in repo, data stays out" policy.
/// </summary>
public static class CoastlineGb
{
    // GB bounding box  -  must match GridSim.Gda.Geo.GeoLocator.{Lon,Lat}{Min,Max}.
    private const double LonMin = -8.2, LonMax = 1.9, LatMin = 49.8, LatMax = 59.0;

    // lon, lat pairs, clockwise around the mainland.
    private static readonly double[,] LonLat =
    {
        {-5.71, 50.07}, {-5.20, 49.96}, {-4.66, 50.20}, {-4.14, 50.37}, {-3.55, 50.22},
        {-2.94, 50.72}, {-2.45, 50.57}, {-1.90, 50.72}, {-1.40, 50.78}, {-0.78, 50.73},
        {-0.10, 50.78}, { 0.27, 50.74}, { 0.97, 50.92}, { 1.34, 51.13}, { 1.45, 51.39},
        { 0.95, 51.49}, { 0.71, 51.54}, { 1.00, 51.78}, { 1.16, 51.79}, { 1.28, 52.06},
        { 1.58, 52.10}, { 1.75, 52.48}, { 1.65, 52.77}, { 1.30, 52.93}, { 0.55, 52.97},
        { 0.35, 52.80}, { 0.20, 52.92}, { 0.34, 53.14}, { 0.10, 53.40}, { 0.13, 53.58},
        {-0.20, 53.72}, {-0.08, 54.12}, {-0.55, 54.48}, {-1.18, 54.69}, {-1.42, 55.02},
        {-1.60, 55.32}, {-2.00, 55.77}, {-2.52, 56.00}, {-2.85, 56.06}, {-3.18, 56.07},
        {-2.80, 56.20}, {-2.60, 56.28}, {-2.47, 56.71}, {-2.10, 57.14}, {-1.95, 57.50},
        {-2.07, 57.70}, {-2.80, 57.70}, {-3.60, 57.65}, {-3.95, 57.60}, {-4.10, 57.85},
        {-3.80, 58.20}, {-3.37, 58.67}, {-3.02, 58.64}, {-4.20, 58.55}, {-5.00, 58.62},
        {-5.30, 58.25}, {-5.16, 57.90}, {-5.65, 57.55}, {-5.70, 57.30}, {-5.90, 56.95},
        {-6.20, 56.70}, {-5.70, 56.55}, {-5.47, 56.41}, {-5.60, 56.00}, {-5.80, 55.30},
        {-5.20, 55.25}, {-4.85, 55.44}, {-4.60, 55.46}, {-4.90, 55.00}, {-4.86, 54.63},
        {-4.40, 54.85}, {-3.90, 54.88}, {-3.50, 54.95}, {-3.56, 54.64}, {-3.40, 54.40},
        {-3.20, 54.10}, {-2.95, 53.93}, {-3.05, 53.82}, {-3.00, 53.55}, {-3.00, 53.40},
        {-3.30, 53.35}, {-3.80, 53.32}, {-4.10, 53.32}, {-4.63, 53.31}, {-4.55, 53.13},
        {-4.76, 52.79}, {-4.40, 52.90}, {-4.05, 52.55}, {-4.08, 52.41}, {-4.70, 52.12},
        {-5.30, 51.88}, {-5.05, 51.70}, {-4.30, 51.62}, {-4.00, 51.57}, {-3.50, 51.40},
        {-3.16, 51.46}, {-2.95, 51.38}, {-3.40, 51.22}, {-3.47, 51.20}, {-4.00, 51.18},
        {-4.20, 51.05}, {-4.52, 51.02}, {-4.55, 50.83}, {-4.80, 50.60}, {-5.08, 50.42},
        {-5.40, 50.25}, {-5.71, 50.07},
    };

    /// <summary>The outline in map world coords (x in 0..1, y north-down: y = 1 - lat-frac), as the WPF copy builds it.</summary>
    public static IReadOnlyList<WorldPoint> World { get; } = Build();

    private static WorldPoint[] Build()
    {
        int n = LonLat.GetLength(0);
        var pts = new WorldPoint[n];
        for (int i = 0; i < n; i++)
        {
            double x = (LonLat[i, 0] - LonMin) / (LonMax - LonMin);
            double yUp = (LonLat[i, 1] - LatMin) / (LatMax - LatMin);
            pts[i] = new WorldPoint(x, 1.0 - yUp);   // world y is north-down to match Bus world coords
        }
        return pts;
    }
}
