namespace GridSim.Ui.Geography;

/// <summary>
/// A point in the map's world coordinates, with no user-interface framework behind it: <see cref="X"/> runs 0..1 west to
/// east across the GB bounding box and <see cref="Y"/> runs north-down (0 at the top), the convention every bus world
/// coordinate and the coastline use. It carries the same two doubles the WPF <c>System.Windows.Point</c> carried before
/// UI programme Phase 0.2, so a front end that converts it to its own point type gets the same numbers bit for bit.
/// </summary>
/// <param name="X">World x, 0 (west edge) to 1 (east edge); a polygon may run a touch past either edge.</param>
/// <param name="Y">World y, 0 (north edge) to 1 (south edge); a polygon may run a touch past either edge.</param>
public readonly record struct WorldPoint(double X, double Y);
