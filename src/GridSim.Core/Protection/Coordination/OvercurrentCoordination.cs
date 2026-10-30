using System;
using System.Collections.Generic;

namespace GridSim.Core.Protection.Coordination;

/// <summary>The standard time-current characteristic (TCC) families an inverse-time overcurrent relay can
/// be set to. The IEC forms are <c>t = TMS * k / ((I/Is)^alpha - 1)</c> (IEC 60255-151); the IEEE forms are
/// <c>t = TD * (A / ((I/Is)^p - 1) + B)</c> (IEEE C37.112). Definite-time operates after a fixed delay for
/// any current above pickup.</summary>
public enum OvercurrentCurve
{
    /// <summary>IEC standard inverse: k = 0.14, alpha = 0.02 (the workhorse feeder curve).</summary>
    IecStandardInverse,
    /// <summary>IEC very inverse: k = 13.5, alpha = 1.0.</summary>
    IecVeryInverse,
    /// <summary>IEC extremely inverse: k = 80.0, alpha = 2.0.</summary>
    IecExtremelyInverse,
    /// <summary>IEC long-time inverse: k = 120.0, alpha = 1.0.</summary>
    IecLongTimeInverse,
    /// <summary>IEEE moderately inverse: A = 0.0515, B = 0.1140, p = 0.02.</summary>
    IeeeModeratelyInverse,
    /// <summary>IEEE very inverse: A = 19.61, B = 0.491, p = 2.0.</summary>
    IeeeVeryInverse,
    /// <summary>IEEE extremely inverse: A = 28.2, B = 0.1217, p = 2.0.</summary>
    IeeeExtremelyInverse,
    /// <summary>Definite time: a fixed delay above pickup, independent of current magnitude.</summary>
    DefiniteTime,
}

/// <summary>
/// One inverse-time / definite-time overcurrent element - a curve family, a pickup current <c>Is</c> (in the
/// same unit as the fault currents it is evaluated against, amps or per-unit) and a time dial. For an IDMT
/// curve <see cref="TimeDial"/> is the IEC time-multiplier setting (TMS) or the IEEE time dial (TD); for a
/// <see cref="OvercurrentCurve.DefiniteTime"/> element <see cref="DefiniteTimeSeconds"/> is the fixed delay.
/// </summary>
public sealed record OvercurrentElement(
    OvercurrentCurve Curve,
    double PickupCurrent,
    double TimeDial = 1.0,
    double DefiniteTimeSeconds = 0.0);

/// <summary>A coordination check between a primary (downstream) and backup (upstream) element at a common
/// fault current. <see cref="MarginSeconds"/> is <c>backup - primary</c> operating time - the grading
/// margin - and <see cref="Coordinated"/> is whether it meets the required coordination time interval with
/// the primary operating first.</summary>
public sealed record GradingResult(
    double PrimaryTimeSeconds,
    double BackupTimeSeconds,
    double MarginSeconds,
    double RequiredMarginSeconds,
    bool Coordinated)
{
    /// <summary>The primary element operates at this current (a finite operating time).</summary>
    public bool PrimaryOperates => !double.IsInfinity(PrimaryTimeSeconds);

    /// <summary>D-DOC-092 (October 2026): the backup element operates at this current (a finite operating time). A
    /// backup that never operates backs nothing up, so the pair is not coordinated whatever its (infinite) margin.</summary>
    public bool BackupOperates => !double.IsInfinity(BackupTimeSeconds);
}

/// <summary>
/// Inverse-time overcurrent (ANSI 51) <b>coordination</b> - the shared time-current-curve (TCC) library and
/// the grading / selectivity-margin checks that GridSim lacked (the existing <c>TimeOvercurrentRelay</c>
/// hard-codes a single IEC standard-inverse curve to flag a relay on a solved state; it does not grade one
/// relay against another). Everything here is a pure closed form of the standard curve equations, validated
/// to machine epsilon.
///
/// <para>The operate times are functions of a <b>supplied</b> fault current (the current the element sees).
/// Deriving each relay's current contribution from the network - the per-branch split of the fault current
/// computed by <see cref="ShortCircuit.Iec60909"/> / <see cref="FaultAnalysis"/> - is available from
/// <c>Iec60909.BranchFaultCurrents</c>; callers pass that current in (R095: this doc used to call the coupling "a
/// later slice"). Keeping the current an input keeps this library exact and independent of the operating
/// point.</para>
/// </summary>
public static class OvercurrentCoordination
{
    /// <summary>
    /// The default required coordination time interval (grading margin) between a relay and its back-up, seconds:
    /// 0.40 s. S3 G01 grounding (October 2026); the default was an unsourced 0.30 s ("a typical CTI").
    /// <list type="bullet">
    /// <item>National Grid PS(T)010 (RES) Issue 1, July 2017, "Back-up protection grading across National Grid /
    /// Distribution Network Operator interfaces" (docs/reference S158), Appendix A.5, p.11: "the minimum acceptable grading
    /// margin is 400 ms if the calculation yields a lower" value (the required margin there is the larger of 400 ms and
    /// a breaker / overshoot / safety-margin sum).</item>
    /// <item>Electricity North West EPD350 Issue 6, March 2023, "Protection for 132 kV, 33 kV and 11/6.6 kV systems"
    /// (S165), section 4.3 "Grading Margin and Reset Times", p.10: the typical margin with electromechanical relays and
    /// oil circuit breakers is 0.4 s; with modern electronic relays and vacuum breakers in adjacent use it is
    /// permissible to reduce it to 0.2 s.</item>
    /// </list>
    /// 0.40 s is the value both documents give for the general case (0.2 s is a stated relaxation that needs modern
    /// relays and breakers on both sides, which a generic study cannot assume). A caller passes its own CTI to relax it.
    /// </summary>
    public const double DefaultCoordinationTimeIntervalS = 0.40;

    /// <summary>The IEC 60255-151 curve constants <c>(k, alpha)</c> for an IEC family. The same four (alpha, k) pairs
    /// are printed in ABB Distribution Automation Handbook section 8.2 "Relay Coordination" (1MRS757285, docs/reference
    /// S317), Table 8.2.1 "Curve set related parameters", p.4: normal inverse (0.02, 0.14), very inverse (1.0, 13.5),
    /// extremely inverse (2.0, 80.0), long-time inverse (1.0, 120.0). The IEEE C37.112 constants below are not printed
    /// in any document in docs/reference (IEEE C37.112 itself would settle them).</summary>
    public static (double K, double Alpha) IecConstants(OvercurrentCurve curve) => curve switch
    {
        OvercurrentCurve.IecStandardInverse => (0.14, 0.02),
        OvercurrentCurve.IecVeryInverse => (13.5, 1.0),
        OvercurrentCurve.IecExtremelyInverse => (80.0, 2.0),
        OvercurrentCurve.IecLongTimeInverse => (120.0, 1.0),
        _ => throw new ArgumentOutOfRangeException(nameof(curve), curve, "Not an IEC curve."),
    };

    /// <summary>The IEEE C37.112 curve constants <c>(A, B, p)</c> for an IEEE family.</summary>
    public static (double A, double B, double P) IeeeConstants(OvercurrentCurve curve) => curve switch
    {
        OvercurrentCurve.IeeeModeratelyInverse => (0.0515, 0.1140, 0.02),
        OvercurrentCurve.IeeeVeryInverse => (19.61, 0.491, 2.0),
        OvercurrentCurve.IeeeExtremelyInverse => (28.2, 0.1217, 2.0),
        _ => throw new ArgumentOutOfRangeException(nameof(curve), curve, "Not an IEEE curve."),
    };

    /// <summary>
    /// The operating time (seconds) of an inverse-time / definite-time element at a fault current
    /// <paramref name="current"/>. Below or at pickup an IDMT element never operates and the time is
    /// <see cref="double.PositiveInfinity"/> (the <c>(I/Is)^alpha - 1</c> denominator vanishes as
    /// <c>I -&gt; Is</c>). A definite-time element operates after its fixed delay for any current above pickup.
    /// </summary>
    public static double OperateTimeSeconds(OvercurrentElement element, double current)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!(element.PickupCurrent > 0.0) || !double.IsFinite(element.PickupCurrent))
            throw new ArgumentOutOfRangeException(nameof(element), "Pickup current must be finite and > 0.");
        // Fail loud on settings and currents that have no physical meaning (R095: the CLI now feeds user input
        // here). A NaN current used to give NaN or "never operates"; a zero or negative time dial a zero or
        // negative operating time; a NaN definite-time delay a NaN time that compared as "not coordinated".
        if (double.IsNaN(current) || current < 0.0 || double.IsPositiveInfinity(current))
            throw new ArgumentOutOfRangeException(nameof(current), current, "The fault current must be finite and >= 0.");
        if (element.Curve == OvercurrentCurve.DefiniteTime)
        {
            if (!(element.DefiniteTimeSeconds >= 0.0) || !double.IsFinite(element.DefiniteTimeSeconds))
                throw new ArgumentOutOfRangeException(nameof(element), "A definite-time delay must be finite and >= 0 s.");
        }
        else if (!(element.TimeDial > 0.0) || !double.IsFinite(element.TimeDial))
            throw new ArgumentOutOfRangeException(nameof(element), "The time dial (TMS / TD) must be finite and > 0.");

        double m = current / element.PickupCurrent;      // I / Is
        if (element.Curve == OvercurrentCurve.DefiniteTime)
            return m > 1.0 ? element.DefiniteTimeSeconds : double.PositiveInfinity;

        if (m <= 1.0) return double.PositiveInfinity;     // at or below pickup: no operation

        switch (element.Curve)
        {
            case OvercurrentCurve.IecStandardInverse:
            case OvercurrentCurve.IecVeryInverse:
            case OvercurrentCurve.IecExtremelyInverse:
            case OvercurrentCurve.IecLongTimeInverse:
            {
                (double k, double alpha) = IecConstants(element.Curve);
                return element.TimeDial * k / (DetMath.Pow(m, alpha) - 1.0);
            }
            default:
            {
                (double a, double b, double p) = IeeeConstants(element.Curve);
                return element.TimeDial * (a / (DetMath.Pow(m, p) - 1.0) + b);
            }
        }
    }

    /// <summary>
    /// Grade a <paramref name="primary"/> (downstream) element against a <paramref name="backup"/>
    /// (upstream) element at a common <paramref name="faultCurrent"/>. The grading margin is
    /// <c>backup - primary</c> operating time; the pair is coordinated when the primary operates first and
    /// the margin is at least the required coordination time interval <paramref name="requiredMarginSeconds"/>.
    /// If the primary does not operate at this current the pair cannot coordinate; nor can it if the BACKUP does not
    /// operate (D-DOC-092: it used to pass with an infinite margin).
    /// <para>Default CTI <see cref="DefaultCoordinationTimeIntervalS"/> = 0.40 s (S3 G01, October 2026; was an unsourced
    /// 0.30 s): see that constant for the cited documents.</para>
    /// </summary>
    public static GradingResult Grade(
        OvercurrentElement primary, OvercurrentElement backup, double faultCurrent,
        double requiredMarginSeconds = DefaultCoordinationTimeIntervalS)
    {
        if (!(requiredMarginSeconds >= 0.0) || !double.IsFinite(requiredMarginSeconds))
            throw new ArgumentOutOfRangeException(nameof(requiredMarginSeconds), requiredMarginSeconds,
                "The required coordination time interval must be finite and >= 0 s.");
        double tPrimary = OperateTimeSeconds(primary, faultCurrent);
        double tBackup = OperateTimeSeconds(backup, faultCurrent);
        double margin = tBackup - tPrimary;
        // Coordinated only if the primary actually operates (finite time), the backup ALSO operates (finite time) and
        // the backup lags the primary by the CTI. D-DOC-092 (October 2026): a backup that never operates at this
        // current (below its pickup) used to give margin +infinity, which passed "margin >= CTI", so the pair was graded
        // coordinated and `grade` exited 0 although nothing would back the primary up. The margin itself is still
        // reported as +infinity (the arithmetic is honest); the verdict is what changes.
        bool coordinated = !double.IsInfinity(tPrimary) && !double.IsInfinity(tBackup) && margin >= requiredMarginSeconds;
        return new GradingResult(tPrimary, tBackup, margin, requiredMarginSeconds, coordinated);
    }

    /// <summary>
    /// Check a radial coordination chain (ordered downstream-to-upstream) at a common fault current: grade
    /// each consecutive primary/backup pair and report the pairwise results in order. The chain is coordinated
    /// when every consecutive pair is. The smallest pairwise margin is the chain's binding grading margin.
    /// </summary>
    public static IReadOnlyList<GradingResult> GradeChain(
        IReadOnlyList<OvercurrentElement> downstreamToUpstream, double faultCurrent,
        double requiredMarginSeconds = DefaultCoordinationTimeIntervalS)
    {
        ArgumentNullException.ThrowIfNull(downstreamToUpstream);
        var results = new List<GradingResult>(Math.Max(0, downstreamToUpstream.Count - 1));
        for (int i = 0; i + 1 < downstreamToUpstream.Count; i++)
            results.Add(Grade(downstreamToUpstream[i], downstreamToUpstream[i + 1], faultCurrent, requiredMarginSeconds));
        return results;
    }

    /// <summary>
    /// Parse one element from the compact CLI form (R095): <c>CURVE:PICKUP:TIMEDIAL</c> for an inverse-time curve,
    /// <c>dt:PICKUP:DELAY_S</c> for definite time. Curve names: <c>iec-si</c>, <c>iec-vi</c>, <c>iec-ei</c>,
    /// <c>iec-lti</c>, <c>ieee-mi</c>, <c>ieee-vi</c>, <c>ieee-ei</c>, <c>dt</c> (case-insensitive). Numbers are
    /// invariant culture. Anything else (an unknown curve, a missing or extra field, a value that is not a finite
    /// positive number, a negative delay) throws <see cref="FormatException"/> naming the spec.
    /// </summary>
    public static OvercurrentElement ParseElement(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        string[] f = spec.Trim().Split(':');
        if (f.Length != 3)
            throw new FormatException($"Relay '{spec}': expected CURVE:PICKUP:TIMEDIAL (or dt:PICKUP:DELAY_S), three fields.");
        OvercurrentCurve curve = f[0].Trim().ToLowerInvariant() switch
        {
            "iec-si" => OvercurrentCurve.IecStandardInverse,
            "iec-vi" => OvercurrentCurve.IecVeryInverse,
            "iec-ei" => OvercurrentCurve.IecExtremelyInverse,
            "iec-lti" => OvercurrentCurve.IecLongTimeInverse,
            "ieee-mi" => OvercurrentCurve.IeeeModeratelyInverse,
            "ieee-vi" => OvercurrentCurve.IeeeVeryInverse,
            "ieee-ei" => OvercurrentCurve.IeeeExtremelyInverse,
            "dt" => OvercurrentCurve.DefiniteTime,
            var other => throw new FormatException(
                $"Relay '{spec}': unknown curve '{other}' (iec-si, iec-vi, iec-ei, iec-lti, ieee-mi, ieee-vi, ieee-ei or dt)."),
        };
        double pickup = Number(f[1], spec, "pickup");
        double third = Number(f[2], spec, curve == OvercurrentCurve.DefiniteTime ? "delay" : "time dial");
        if (!(pickup > 0.0))
            throw new FormatException($"Relay '{spec}': the pickup must be > 0.");
        if (curve == OvercurrentCurve.DefiniteTime)
        {
            if (third < 0.0)
                throw new FormatException($"Relay '{spec}': the definite-time delay must be >= 0 s.");
            return new OvercurrentElement(curve, pickup, DefiniteTimeSeconds: third);
        }
        if (!(third > 0.0))
            throw new FormatException($"Relay '{spec}': the time dial must be > 0.");
        return new OvercurrentElement(curve, pickup, TimeDial: third);
    }

    private static double Number(string text, string spec, string what)
    {
        if (!double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new FormatException($"Relay '{spec}': the {what} '{text.Trim()}' is not a finite number.");
        return v;
    }
}
