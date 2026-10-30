using System;

namespace GridSim.Core.Storage;

/// <summary>
/// A <b>synthetic</b> GB-shaped day-ahead energy price day (£/MWh) for exercising storage dispatch when no price
/// series is supplied: overnight trough, morning shoulder, a midday dip (solar), and the evening peak. It is
/// <see cref="Diagnostics.ProvenanceTag.Synthetic"/> - a shape for testing dispatch logic, never a market
/// record - and every output that uses it says so.
/// </summary>
public static class SyntheticPrices
{
    /// <summary>Price (£/MWh) at <paramref name="hour"/> of day.</summary>
    public static double GbDay(double hour)
    {
        double h = ((hour % 24) + 24) % 24;
        double Bump(double centre, double width, double height)
        {
            double z = (h - centre) / width;
            return height * DetMath.Exp(-0.5 * (z * z));   // D1: squared by multiplication (fdlibm's pow(x, 2) is x*x too)
        }
        return 45.0                     // overnight base
            + Bump(8.0, 1.5, 35.0)      // morning ramp
            - Bump(13.0, 2.0, 20.0)     // midday solar dip
            + Bump(18.0, 1.5, 85.0);    // evening peak
    }

    public const string ProvenanceNote = "Synthetic GB-shaped price day (not a market record)";
}
