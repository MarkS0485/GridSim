using System.Globalization;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Estimation;

/// <summary>The phasors <see cref="PmuMeasurementSetAdapter.FromSet"/> built, and what it did not use.</summary>
/// <param name="Voltages">One rectangular voltage phasor per bus that has both a VmPu and a VaDeg row.</param>
/// <param name="Currents">One rectangular current phasor per branch end that has both an IMagPu and an IAngDeg row.</param>
/// <param name="NotUsed">Every row the linear estimate cannot use, each named with the reason (a power or flow row,
/// or a magnitude / angle without its partner). Nothing is dropped without a line here.</param>
public sealed record PmuPhasorSet(
    IReadOnlyList<PmuVoltageMeasurement> Voltages,
    IReadOnlyList<PmuCurrentMeasurement> Currents,
    IReadOnlyList<string> NotUsed);

/// <summary>
/// The adapter from a polar <see cref="MeasurementSet"/> (gridsim-measurements/1, as <c>pmu-ingest</c> writes it through
/// <see cref="Synchrophasor.PmuMeasurementSetMapper"/>) to the rectangular phasors <see cref="LinearPmuStateEstimator"/>
/// takes (1.95.245; the outstanding item in that class's notes and in FEATURE-GAPS-RELEASE section 7).
///
/// <para><b>Pairing.</b> A bus's <see cref="MeasurementKind.VmPu"/> and <see cref="MeasurementKind.VaDeg"/> rows form its
/// voltage phasor; a branch end's <see cref="MeasurementKind.IMagPu"/> and <see cref="MeasurementKind.IAngDeg"/> rows (same
/// From, To, End and Circuit) form its current phasor. Angles are slack-referenced degrees, the set's convention. Rows of
/// any other kind, and a magnitude or angle without its partner, are listed in <see cref="PmuPhasorSet.NotUsed"/>.
/// Refused (<see cref="InvalidDataException"/>, every problem named): two rows of one kind at one place (which to
/// pair is ambiguous), and a current metered at the To end (the linear model holds the from-end current only).</para>
///
/// <para><b>Error model.</b> A polar reading z = m e^(j theta) with independent sigmas s_m (pu) and s_theta (degrees,
/// converted to radians) maps to the rectangular error covariance by the first-order (linearised) transform at the
/// measured value, R = J diag(s_m^2, s_theta^2) J^T with J = [[cos theta, -m sin theta], [sin theta, m cos theta]]:
/// var_re = c^2 s_m^2 + m^2 s^2 s_theta^2, var_im = s^2 s_m^2 + m^2 c^2 s_theta^2, cov = c s (s_m^2 - m^2 s_theta^2). Its
/// determinant is m^2 s_m^2 s_theta^2, positive for m above 0, so the pair is weighted exactly by its full 2x2 inverse
/// (<see cref="PmuVoltageMeasurement.SigmaIm"/>). A phasor of magnitude 0 has no direction to linearise about and is
/// refused. Sines and cosines are GridSim's own (<see cref="DetMath"/>, D1), so the result is the same on every CPU.</para>
/// </summary>
public static class PmuMeasurementSetAdapter
{
    /// <summary>Pair the set's polar phasor rows into rectangular phasors against <paramref name="model"/> (see the class
    /// remarks). The set should already have passed <see cref="MeasurementSet.Validate"/> against the same model.</summary>
    public static PmuPhasorSet FromSet(MeasurementSet set, GridModel model)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(model);
        var inv = CultureInfo.InvariantCulture;
        var problems = new List<string>();
        var notUsed = new List<string>();

        // Group the phasor rows by place, in first-seen order (the output order follows the file, deterministically).
        var busOrder = new List<int>();
        var bus = new Dictionary<int, (Measurement? Mag, Measurement? Ang)>();
        var branchOrder = new List<(int From, int To, BranchEnd End, string? Circuit)>();
        var branch = new Dictionary<(int, int, BranchEnd, string?), (Measurement? Mag, Measurement? Ang)>();
        for (int i = 0; i < set.Measurements.Count; i++)
        {
            Measurement m = set.Measurements[i];
            string where = $"measurement #{i.ToString(inv)} ({m.Kind} @ {m.Describe()})";
            switch (m.Kind)
            {
                case MeasurementKind.VmPu:
                case MeasurementKind.VaDeg:
                {
                    if (!bus.TryGetValue(m.Bus, out var pair)) { busOrder.Add(m.Bus); pair = (null, null); }
                    bool mag = m.Kind == MeasurementKind.VmPu;
                    if ((mag ? pair.Mag : pair.Ang) is not null) { problems.Add($"{where}: a second {m.Kind} row at bus {m.Bus.ToString(inv)}; which to pair is ambiguous"); break; }
                    bus[m.Bus] = mag ? (m, pair.Ang) : (pair.Mag, m);
                    break;
                }
                case MeasurementKind.IMagPu:
                case MeasurementKind.IAngDeg:
                {
                    if (m.End == BranchEnd.To)
                    {
                        problems.Add($"{where}: a current metered at the To end; the linear PMU estimate holds the from-end current only (meter the From end, or use estimate)");
                        break;
                    }
                    var key = (m.From, m.To, m.End, m.Circuit);
                    if (!branch.TryGetValue(key, out var pair)) { branchOrder.Add(key); pair = (null, null); }
                    bool mag = m.Kind == MeasurementKind.IMagPu;
                    if ((mag ? pair.Mag : pair.Ang) is not null) { problems.Add($"{where}: a second {m.Kind} row on this branch end; which to pair is ambiguous"); break; }
                    branch[key] = mag ? (m, pair.Ang) : (pair.Mag, m);
                    break;
                }
                default:
                    notUsed.Add($"{where}: not a phasor quantity (the linear PMU estimate uses voltage and current phasors only)");
                    break;
            }
        }

        var voltages = new List<PmuVoltageMeasurement>();
        foreach (int id in busOrder)
        {
            (Measurement? mag, Measurement? ang) = bus[id];
            if (mag is null || ang is null)
            {
                notUsed.Add($"bus {id.ToString(inv)}: {(mag is null ? "VaDeg" : "VmPu")} with no {(mag is null ? "VmPu" : "VaDeg")} row, so no phasor");
                continue;
            }
            if (Polar(mag, ang, $"bus {id.ToString(inv)}", problems) is { } p)
                voltages.Add(new PmuVoltageMeasurement(id, p.Value, p.SigmaRe, p.Cov, p.SigmaIm));
        }

        var currents = new List<PmuCurrentMeasurement>();
        foreach (var key in branchOrder)
        {
            (Measurement? mag, Measurement? ang) = branch[key];
            string at = $"branch {key.From.ToString(inv)}->{key.To.ToString(inv)}{(key.Circuit is null ? "" : $" '{key.Circuit}'")}";
            if (mag is null || ang is null)
            {
                notUsed.Add($"{at}: {(mag is null ? "IAngDeg" : "IMagPu")} with no {(mag is null ? "IMagPu" : "IAngDeg")} row, so no phasor");
                continue;
            }
            if (Polar(mag, ang, at, problems) is { } p)
                currents.Add(new PmuCurrentMeasurement(key.From, key.To, key.Circuit, p.Value, p.SigmaRe, p.Cov, p.SigmaIm));
        }

        if (problems.Count > 0)
            throw new InvalidDataException("The linear PMU estimate cannot use this measurement set: " + string.Join("; ", problems) + ".");
        return new PmuPhasorSet(voltages, currents, notUsed);
    }

    /// <summary>The rectangular phasor and its first-order error covariance (class remarks), or null (problem added).</summary>
    private static (Complex Value, double SigmaRe, double SigmaIm, double Cov)? Polar(Measurement mag, Measurement ang, string at, List<string> problems)
    {
        double m = mag.Value;
        if (!double.IsFinite(m) || m <= 0.0 || !double.IsFinite(ang.Value) || !(mag.Sigma > 0.0) || !(ang.Sigma > 0.0)
            || !double.IsFinite(mag.Sigma) || !double.IsFinite(ang.Sigma))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{at}: magnitude {m} (sigma {mag.Sigma}) and angle {ang.Value} deg (sigma {ang.Sigma}) do not make a phasor with a usable error model (the magnitude must be above 0 and both sigmas finite and above 0)"));
            return null;
        }
        const double DegToRad = Math.PI / 180.0;
        (double s, double c) = DetMath.SinCos(ang.Value * DegToRad);
        double sm2 = mag.Sigma * mag.Sigma;
        double st = ang.Sigma * DegToRad;
        double mst2 = m * m * (st * st);
        double varRe = c * c * sm2 + s * s * mst2;
        double varIm = s * s * sm2 + c * c * mst2;
        double cov = c * s * (sm2 - mst2);
        return (new Complex(m * c, m * s), Math.Sqrt(varRe), Math.Sqrt(varIm), cov);
    }
}
