using System;
using GridSim.Core.Protection.Coordination;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the inverse-time overcurrent coordination library (<see cref="OvercurrentCoordination"/>)
/// against the standard time-current-curve equations to machine epsilon:
///   - the IEC 60255-151 form t = TMS k / ((I/Is)^alpha - 1), matching the existing 51 relay's
///     standard-inverse curve;
///   - the IEEE C37.112 form t = TD (A/((I/Is)^p - 1) + B);
///   - the at-pickup / definite-time / monotonicity behaviours;
///   - the grading margin and coordination check between a primary and a backup element.
/// </summary>
public class OvercurrentCoordinationTests
{
    // ---- IEC standard-inverse operate time against its closed form (and the existing 51 relay) ----
    [Fact]
    public void Iec_standard_inverse_matches_the_closed_form()
    {
        const double pickup = 100.0, tms = 0.10, current = 500.0;   // M = 5
        var e = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, pickup, tms);
        double m = current / pickup;
        double expected = tms * 0.14 / (Math.Pow(m, 0.02) - 1.0);    // same form TimeOvercurrentRelay uses
        Assert.Equal(expected, OvercurrentCoordination.OperateTimeSeconds(e, current), 12);
    }

    // ---- The other IEC families against their constants ----
    [Fact]
    public void Iec_families_use_their_tabulated_constants()
    {
        const double pickup = 100.0, tms = 0.20, current = 400.0;   // M = 4
        double m = current / pickup;
        void Check(OvercurrentCurve curve, double k, double alpha)
        {
            var e = new OvercurrentElement(curve, pickup, tms);
            Assert.Equal(tms * k / (Math.Pow(m, alpha) - 1.0), OvercurrentCoordination.OperateTimeSeconds(e, current), 12);
        }
        Check(OvercurrentCurve.IecVeryInverse, 13.5, 1.0);
        Check(OvercurrentCurve.IecExtremelyInverse, 80.0, 2.0);
        Check(OvercurrentCurve.IecLongTimeInverse, 120.0, 1.0);
    }

    // ---- IEEE families against the C37.112 closed form ----
    [Fact]
    public void Ieee_families_match_the_c37_112_closed_form()
    {
        const double pickup = 100.0, td = 0.50, current = 600.0;    // M = 6
        double m = current / pickup;
        void Check(OvercurrentCurve curve, double a, double b, double p)
        {
            var e = new OvercurrentElement(curve, pickup, td);
            Assert.Equal(td * (a / (Math.Pow(m, p) - 1.0) + b), OvercurrentCoordination.OperateTimeSeconds(e, current), 12);
        }
        Check(OvercurrentCurve.IeeeModeratelyInverse, 0.0515, 0.1140, 0.02);
        Check(OvercurrentCurve.IeeeVeryInverse, 19.61, 0.491, 2.0);
        Check(OvercurrentCurve.IeeeExtremelyInverse, 28.2, 0.1217, 2.0);
    }

    // ---- At or below pickup an IDMT element never operates; time scales linearly with the dial ----
    [Fact]
    public void Below_pickup_is_infinite_and_the_dial_scales_time_linearly()
    {
        var e = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100.0, 0.10);
        Assert.True(double.IsPositiveInfinity(OvercurrentCoordination.OperateTimeSeconds(e, 100.0)));  // I = Is
        Assert.True(double.IsPositiveInfinity(OvercurrentCoordination.OperateTimeSeconds(e, 80.0)));   // I < Is

        double t1 = OvercurrentCoordination.OperateTimeSeconds(e, 500.0);
        var e2 = e with { TimeDial = 0.20 };
        double t2 = OvercurrentCoordination.OperateTimeSeconds(e2, 500.0);
        Assert.Equal(2.0 * t1, t2, 12);                                  // doubling the dial doubles the time

        // Higher current => faster operation (strictly monotone decreasing above pickup).
        Assert.True(OvercurrentCoordination.OperateTimeSeconds(e, 1000.0) < OvercurrentCoordination.OperateTimeSeconds(e, 500.0));
    }

    // ---- Definite-time: a fixed delay above pickup, independent of current ----
    [Fact]
    public void Definite_time_is_a_fixed_delay_above_pickup()
    {
        var e = new OvercurrentElement(OvercurrentCurve.DefiniteTime, 100.0, DefiniteTimeSeconds: 0.5);
        Assert.Equal(0.5, OvercurrentCoordination.OperateTimeSeconds(e, 200.0), 12);
        Assert.Equal(0.5, OvercurrentCoordination.OperateTimeSeconds(e, 5000.0), 12);   // same at any level
        Assert.True(double.IsPositiveInfinity(OvercurrentCoordination.OperateTimeSeconds(e, 90.0)));
    }

    // ---- Grading: identical relays have zero margin; a slower backup coordinates ----
    [Fact]
    public void Grading_margin_is_backup_minus_primary()
    {
        const double fault = 800.0;
        var primary = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100.0, 0.10);

        // Identical backup: margin 0 => not coordinated against a positive CTI.
        GradingResult same = OvercurrentCoordination.Grade(primary, primary, fault, requiredMarginSeconds: 0.30);
        Assert.Equal(0.0, same.MarginSeconds, 12);
        Assert.False(same.Coordinated);

        // A backup on a higher time dial lags the primary; margin = t_backup - t_primary.
        var backup = primary with { TimeDial = 0.50 };
        GradingResult graded = OvercurrentCoordination.Grade(primary, backup, fault, requiredMarginSeconds: 0.30);
        double tP = OvercurrentCoordination.OperateTimeSeconds(primary, fault);
        double tB = OvercurrentCoordination.OperateTimeSeconds(backup, fault);
        Assert.Equal(tB - tP, graded.MarginSeconds, 12);
        Assert.Equal(graded.MarginSeconds >= 0.30, graded.Coordinated);
    }

    // ---- A primary that does not operate at the fault current cannot coordinate ----
    [Fact]
    public void A_non_operating_primary_is_not_coordinated()
    {
        // Fault below the primary's pickup: primary time is infinite, so no coordination is possible.
        var primary = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 1000.0, 0.10);
        var backup = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100.0, 0.50);
        GradingResult g = OvercurrentCoordination.Grade(primary, backup, faultCurrent: 500.0);
        Assert.True(double.IsPositiveInfinity(g.PrimaryTimeSeconds));
        Assert.False(g.Coordinated);
    }

    // ---- A chain grades each consecutive pair; the binding margin is the smallest ----
    [Fact]
    public void Chain_grades_each_consecutive_pair()
    {
        const double fault = 900.0;
        var r1 = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100.0, 0.10);   // downstream
        var r2 = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100.0, 0.30);
        var r3 = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100.0, 0.60);   // upstream

        var chain = OvercurrentCoordination.GradeChain(new[] { r1, r2, r3 }, fault, requiredMarginSeconds: 0.20);
        Assert.Equal(2, chain.Count);
        // Each pair's margin matches the standalone grade of that pair.
        Assert.Equal(OvercurrentCoordination.Grade(r1, r2, fault, 0.20).MarginSeconds, chain[0].MarginSeconds, 12);
        Assert.Equal(OvercurrentCoordination.Grade(r2, r3, fault, 0.20).MarginSeconds, chain[1].MarginSeconds, 12);
    }
}
