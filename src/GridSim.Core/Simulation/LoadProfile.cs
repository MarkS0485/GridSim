namespace GridSim.Core.Simulation;

/// <summary>
/// A simple daily demand shape (a multiplier on base demand): trough around 04:00,
/// peak around 16:00, swinging roughly 0.6 -> 1.0. Enough to make the live
/// simulation and the visualiser move through a believable day; a real profile
/// (or GDA half-hourly demand) can replace it later.
/// </summary>
public static class LoadProfile
{
    public static double DailyFactor(double hour)
    {
        double x = ((hour - 4 + 24) % 24) / 24.0;              // hours since the 04:00 trough
        return 0.60 + 0.40 * (0.5 - 0.5 * DetMath.Cos(2 * Math.PI * x));
    }
}
