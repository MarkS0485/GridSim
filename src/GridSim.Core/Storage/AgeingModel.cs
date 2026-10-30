using System;
using System.Collections.Generic;

namespace GridSim.Core.Storage;

/// <summary>
/// Semi-empirical capacity fade (<see cref="AgeingParameters"/>), advanced step by step alongside the cell:
/// <list type="bullet">
///   <item><b>calendar</b> fade <c>k_cal A(T) f(SoC) sqrt(days)</c>, integrated through time-varying stress by the
///   equivalent-time method (the fade so far maps back to an equivalent age at the present stress);</item>
///   <item><b>cycle</b> fade on charge throughput: 20 % over <c>N80</c> equivalent full cycles, scaled by
///   temperature (Arrhenius) and C-rate.</item>
/// </list>
/// The total updates <see cref="CellModel.CapacityFade"/>, which shrinks capacity and grows resistance.
/// Depth-of-discharge-aware cycle fade over a whole run is available from a rainflow count
/// (<see cref="Rainflow"/>, <see cref="CycleFadeFromRainflow"/>).
/// </summary>
public sealed class AgeingModel
{
    // ---- Named constants (October 2026 review, WS13 R197). Values unchanged.

    /// <summary>Capacity fade that defines end of life: 20 % (the "80" in N80, cycles to 80 % capacity). One
    /// equivalent full cycle costs <c>EndOfLifeFade / N80</c> of fade at reference conditions.</summary>
    public const double EndOfLifeFade = 0.2;

    /// <summary>Cap on the total capacity fade written to the cell (80 %): beyond it the cell model would have
    /// almost no capacity left and its resistance growth is no longer meaningful.</summary>
    public const double MaxCapacityFade = 0.8;

    /// <summary>Floor on the calendar SoC stress factor <c>1 + k_soc (SoC - 0.5)</c>, so a large negative SoC
    /// sensitivity cannot drive the stress to zero or below.</summary>
    public const double MinSocStressFactor = 0.05;

    /// <summary>State of charge at which the calendar SoC stress factor is 1 (the reference mid-point).</summary>
    public const double SocStressReference = 0.5;

    /// <summary>Seconds in a day (the calendar-fade law is in square-root days).</summary>
    public const double SecondsPerDay = 86400.0;

    /// <summary>One equivalent full cycle is a charge PLUS a discharge of the full capacity: throughput
    /// divided by this many capacities.</summary>
    public const double ThroughputPerFullCycle = 2.0;

    private readonly AgeingParameters _p;

    public AgeingModel(AgeingParameters parameters) => _p = parameters ?? throw new ArgumentNullException(nameof(parameters));

    public double CalendarFade { get; private set; }
    public double CycleFade { get; private set; }
    public double TotalFade => CalendarFade + CycleFade;

    /// <summary>The ageing parameters this model integrates.</summary>
    public AgeingParameters Parameters => _p;

    /// <summary>
    /// R231 (October 2026 review, stream B2): a copy carrying the same parameters and the same accumulated fade, so a pack
    /// snapshot taken with ageing keeps it (<see cref="PackModel.Clone(bool)"/>).
    /// </summary>
    public AgeingModel Copy() => new(_p) { CalendarFade = CalendarFade, CycleFade = CycleFade };

    /// <summary>
    /// R231: set the accumulated fade to <paramref name="other"/>'s (a reset to a snapshot). Fails loud when the two models
    /// integrate different parameters: a reset never changes a pack's chemistry.
    /// </summary>
    public void RestoreFrom(AgeingModel other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!ReferenceEquals(other._p, _p) && !Equals(other._p, _p))
            throw new ArgumentException("AgeingModel.RestoreFrom: the snapshot ages with different parameters (another chemistry).", nameof(other));
        CalendarFade = other.CalendarFade;
        CycleFade = other.CycleFade;
    }

    /// <summary>Accumulate one step of ageing for a cell carrying <paramref name="current"/> for <paramref name="dt"/> s.</summary>
    public void Advance(CellModel cell, double current, double dt)
    {
        double tK = cell.TemperatureK;
        double arrCal = DetMath.Exp(-_p.CalendarActivationEnergy / ChemistryParameters.GasConstant * (1.0 / tK - 1.0 / ChemistryParameters.TRefK));
        double stress = _p.CalendarFadePerSqrtDay * arrCal * Math.Max(MinSocStressFactor, 1.0 + _p.CalendarSocSensitivity * (cell.Soc - SocStressReference));
        double days = dt / SecondsPerDay;
        double ratio = CalendarFade / stress;
        double tEq = CalendarFade > 0 ? ratio * ratio : 0.0;   // D1: squared by multiplication (fdlibm's pow(x, 2) is x*x too)
        CalendarFade = stress * Math.Sqrt(tEq + days);

        if (current != 0)
        {
            double arrCyc = DetMath.Exp(-_p.CycleActivationEnergy / ChemistryParameters.GasConstant * (1.0 / tK - 1.0 / ChemistryParameters.TRefK));
            double cRate = Math.Abs(current) / cell.Chemistry.CapacityAh;
            double cFactor = 1.0 + _p.CRateSensitivity * Math.Max(0.0, cRate - 1.0);
            double efc = Math.Abs(current) * dt / 3600.0 / (ThroughputPerFullCycle * cell.Chemistry.CapacityAh);   // equivalent full cycles
            CycleFade += EndOfLifeFade / _p.CyclesTo80Percent * efc * arrCyc * cFactor;
        }
        cell.CapacityFade = Math.Min(MaxCapacityFade, TotalFade);
    }

    /// <summary>Cycle fade from rainflow cycles (depth, count): each cycle of depth <c>d</c> costs
    /// <c>(0.2 / N80) d^DodExponent</c> (a full cycle, d = 1, costs the reference amount).</summary>
    public double CycleFadeFromRainflow(IEnumerable<(double Depth, double Count)> cycles)
    {
        double f = 0;
        foreach ((double d, double n) in cycles) f += n * EndOfLifeFade / _p.CyclesTo80Percent * DetMath.Pow(Math.Abs(d), _p.DodExponent);
        return f;
    }
}

/// <summary>Rainflow cycle counting (ASTM E1049-85, three-point method) of a SoC (or any) history.</summary>
public static class Rainflow
{
    /// <summary>Cycles as (range, count): full cycles count 1, the residue's half cycles 0.5.</summary>
    public static List<(double Depth, double Count)> Count(IEnumerable<double> series)
    {
        // Reduce to turning points.
        var tp = new List<double>();
        foreach (double x in series)
        {
            if (tp.Count == 0 || x != tp[^1])
            {
                if (tp.Count >= 2 && (tp[^1] - tp[^2]) * (x - tp[^1]) > 0) tp[^1] = x;   // same direction: extend
                else tp.Add(x);
            }
        }

        var result = new List<(double, double)>();
        var stack = new List<double>();
        foreach (double x in tp)
        {
            stack.Add(x);
            while (stack.Count >= 3)
            {
                double xr = Math.Abs(stack[^1] - stack[^2]);
                double yr = Math.Abs(stack[^2] - stack[^3]);
                if (xr < yr) break;
                if (stack.Count == 3)
                {
                    result.Add((yr, 0.5));                  // Y contains the start point: half cycle
                    stack.RemoveAt(0);
                }
                else
                {
                    result.Add((yr, 1.0));                  // full cycle: drop the two points of Y
                    stack.RemoveAt(stack.Count - 2);
                    stack.RemoveAt(stack.Count - 2);
                }
            }
        }
        for (int i = 0; i + 1 < stack.Count; i++) result.Add((Math.Abs(stack[i + 1] - stack[i]), 0.5));
        return result;
    }
}
