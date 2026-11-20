using System.Numerics;
using GridSim.Core.Model;

namespace GridSim.Core.Harmonics.Planning;

/// <summary>The passive harmonic filter topologies of <see cref="FilterDesign"/>.</summary>
public enum FilterKind { SingleTuned, HighPass, CType }

/// <summary>
/// The design inputs. <see cref="VoltageKv"/> is the phase-phase voltage of the bus, <see cref="FundamentalMvar"/> the reactive power
/// the filter generates at the fundamental (three-phase, positive), <see cref="TunedOrder"/> the order h at which it is tuned (single-tuned
/// and C-type) or its corner order (high-pass). <see cref="QualityFactor"/> is q = R / (h X_L) with X_L the reactor's fundamental
/// reactance: for the single-tuned filter R is in series with the reactor (null is no resistor); for the high-pass and C-type
/// filters R is in parallel with the reactor (null is no resistor).
/// </summary>
public sealed record FilterDesignRequest(FilterKind Kind, double VoltageKv, double FundamentalMvar, double TunedOrder, double? QualityFactor = null, double FundamentalHz = 50.0);

/// <summary>
/// A designed filter: component reactances at the fundamental (ohm per phase, star equivalent), and its impedance at any order.
/// <see cref="CapacitorOhm"/> is the (main) capacitor, <see cref="Capacitor2Ohm"/> the C-type's second capacitor (0 otherwise),
/// <see cref="ReactorOhm"/> the reactor, <see cref="ResistorOhm"/> the damping resistor (null when there is none).
/// </summary>
public sealed record FilterDesignResult(
    FilterKind Kind,
    double VoltageKv,
    double TunedOrder,
    double? QualityFactor,
    double CapacitorOhm,
    double Capacitor2Ohm,
    double ReactorOhm,
    double? ResistorOhm,
    double FundamentalMvar,
    double FundamentalMw,
    double CapacitorMicrofarad,
    double Capacitor2Microfarad,
    double ReactorMillihenry,
    double? CapacitorVoltageRatio,
    double FundamentalHz)
{
    /// <summary>The filter's per-phase (star equivalent) impedance in ohm at harmonic order <paramref name="h"/> (h = 1 is the fundamental).</summary>
    public Complex ImpedanceOhm(double h)
    {
        if (!(h > 0.0) || !double.IsFinite(h)) throw new ArgumentOutOfRangeException(nameof(h), h, "The order must be finite and above 0.");
        Complex jXl = new(0.0, h * ReactorOhm);
        switch (Kind)
        {
            case FilterKind.SingleTuned:
                return new Complex(ResistorOhm ?? 0.0, 0.0) + jXl + new Complex(0.0, -CapacitorOhm / h);
            case FilterKind.HighPass:
            {
                Complex zc = new(0.0, -CapacitorOhm / h);
                if (ResistorOhm is not double r) return zc + jXl;
                return zc + jXl * r / (jXl + r);
            }
            default:   // CType
            {
                Complex zc1 = new(0.0, -CapacitorOhm / h);
                Complex branch = jXl + new Complex(0.0, -Capacitor2Ohm / h);
                if (ResistorOhm is not double r) return zc1 + branch;
                return zc1 + branch * r / (branch + r);
            }
        }
    }

    /// <summary>The filter's impedance in per unit on a base of <paramref name="baseMva"/> at its own voltage.</summary>
    public Complex ImpedancePu(double h, double baseMva) => ImpedanceOhm(h) * (baseMva / (VoltageKv * VoltageKv));
}

/// <summary>
/// RC4 R4-21: passive harmonic filter design, single-tuned, high-pass (second-order damped) and C-type, by closed forms.
///
/// <list type="bullet">
/// <item><b>Single-tuned</b>, tuned at order h, generating Q at the fundamental on a bus of voltage V: the series branch has
/// Z(h') = R + j(h' X_L - X_C / h'), so at the fundamental X_C - X_L = V^2 / Q and X_L = X_C / h^2, giving
/// <c>X_C = V^2 h^2 / (Q (h^2 - 1))</c>; the impedance is R (zero without a resistor) at h exactly. Parallel resonance with a source
/// reactance X_s is at <c>h_p = sqrt(X_C / (X_L + X_s))</c>.</item>
/// <item><b>High-pass</b>: the capacitor in series with the reactor in parallel with R, X_L = X_C / h^2, R = q h X_L; X_C is solved so
/// that the filter generates exactly Q at the fundamental (the resistor takes a little current there).</item>
/// <item><b>C-type</b>: main capacitor C1 in series with a branch of C2 and the reactor, in parallel with R. C2 and the reactor are
/// tuned at the fundamental, so the resistor carries no fundamental current and the fundamental reactance is just X_C1: X_C1 = V^2 / Q.
/// Tuning the filter's zero at order h (lossless limit) needs X_C2 = X_C1 / (h^2 - 1) and X_L = X_C2.</item>
/// </list>
///
/// Quantities are per phase (star equivalent) in ohm: Q (MVAr) and V (kV, phase-phase) give ohm as V^2 / MVAr. The filter is evaluated
/// as an impedance at any order (so any topology can be put in parallel with a network's driving-point impedance, see
/// <see cref="Combined"/>); the single-tuned filter can also be added to a <see cref="GridModel"/> (<see cref="AddSingleTunedTo"/>)
/// so the existing frequency sweep finds the resonances.
/// </summary>
public static class FilterDesign
{
    public static FilterDesignResult Design(FilterDesignRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (!(req.VoltageKv > 0.0) || !double.IsFinite(req.VoltageKv)) throw new ArgumentOutOfRangeException(nameof(req), req.VoltageKv, "The voltage must be finite and above 0 kV.");
        if (!(req.FundamentalMvar > 0.0) || !double.IsFinite(req.FundamentalMvar)) throw new ArgumentOutOfRangeException(nameof(req), req.FundamentalMvar, "The fundamental reactive power must be finite and above 0 MVAr.");
        if (!(req.TunedOrder > 1.0) || !double.IsFinite(req.TunedOrder)) throw new ArgumentOutOfRangeException(nameof(req), req.TunedOrder, "The tuned order must be finite and above 1.");
        if (req.QualityFactor is double qf && (!(qf > 0.0) || !double.IsFinite(qf))) throw new ArgumentOutOfRangeException(nameof(req), qf, "The quality factor must be finite and above 0.");
        if (!(req.FundamentalHz > 0.0) || !double.IsFinite(req.FundamentalHz)) throw new ArgumentOutOfRangeException(nameof(req), req.FundamentalHz, "The fundamental frequency must be finite and above 0 Hz.");

        double v2 = req.VoltageKv * req.VoltageKv;
        double h = req.TunedOrder;
        double h2 = h * h;
        double xc, xc2 = 0.0, xl;
        double? r = null;
        double? vcRatio = null;
        switch (req.Kind)
        {
            case FilterKind.SingleTuned:
                xc = v2 * h2 / (req.FundamentalMvar * (h2 - 1.0));
                xl = xc / h2;
                if (req.QualityFactor is double q1) r = h * xl / q1;   // q = X_n / R at the tuned order, X_n = h X_L
                vcRatio = h2 / (h2 - 1.0);
                break;
            case FilterKind.HighPass:
            {
                // Unit capacitor X_C = 1: X_L = 1/h^2; R = q h X_L = q/h. z1 is the filter's fundamental impedance for that X_C.
                double xlUnit = 1.0 / h2;
                Complex z1 = new Complex(0.0, -1.0);
                if (req.QualityFactor is double q2)
                {
                    double ru = q2 * h * xlUnit;
                    Complex jx = new(0.0, xlUnit);
                    z1 += jx * ru / (jx + ru);
                }
                else z1 += new Complex(0.0, xlUnit);
                // Q generated = -Im(V^2 / conj(Z1)) with Z1 = X_C z1.
                xc = -v2 * (1.0 / Complex.Conjugate(z1)).Imaginary / req.FundamentalMvar;
                xl = xc / h2;
                if (req.QualityFactor is double q3) r = q3 * h * xl;
                break;
            }
            default:   // CType
                xc = v2 / req.FundamentalMvar;
                xc2 = xc / (h2 - 1.0);
                xl = xc2;
                if (req.QualityFactor is double q4) r = q4 * h * xl;
                break;
        }
        double w = 2.0 * Math.PI * req.FundamentalHz;
        var draft = new FilterDesignResult(req.Kind, req.VoltageKv, h, req.QualityFactor, xc, xc2, xl, r, req.FundamentalMvar, 0.0,
            1e6 / (w * xc), xc2 > 0.0 ? 1e6 / (w * xc2) : 0.0, 1e3 * xl / w, vcRatio, req.FundamentalHz);
        // Fundamental real power (the damping resistor's loss at 50 Hz) from the exact complex impedance, MW.
        Complex z = draft.ImpedanceOhm(1.0);
        Complex s = v2 / Complex.Conjugate(z);
        return draft with { FundamentalMw = s.Real };
    }

    /// <summary>The network's driving-point impedance (per unit) with the filter in parallel with it at each order: <c>Z_net Z_f / (Z_net + Z_f)</c>.</summary>
    public static Complex[] Combined(FilterDesignResult filter, IReadOnlyList<double> orders, IReadOnlyList<Complex> networkZPu, double baseMva)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (orders.Count != networkZPu.Count) throw new ArgumentException("One network impedance per order is needed.", nameof(networkZPu));
        var z = new Complex[orders.Count];
        for (int i = 0; i < z.Length; i++)
        {
            Complex zf = filter.ImpedancePu(orders[i], baseMva);
            z[i] = networkZPu[i] * zf / (networkZPu[i] + zf);
        }
        return z;
    }

    /// <summary>
    /// The orders at which |Z| has a local maximum (parallel resonance) or a local minimum (series resonance) on a sampled impedance
    /// curve, each refined by a parabola through the three points around it (deterministic; no search).
    /// </summary>
    public static (IReadOnlyList<double> Peaks, IReadOnlyList<double> Valleys) ExtremaOrders(IReadOnlyList<double> orders, IReadOnlyList<Complex> z)
    {
        if (orders.Count != z.Count) throw new ArgumentException("One impedance per order is needed.", nameof(z));
        var peaks = new List<double>();
        var valleys = new List<double>();
        for (int i = 1; i + 1 < orders.Count; i++)
        {
            double a = z[i - 1].Magnitude, b = z[i].Magnitude, c = z[i + 1].Magnitude;
            bool peak = b > a && b >= c, valley = b < a && b <= c;
            if (!peak && !valley) continue;
            double den = a - 2.0 * b + c;
            double offset = den == 0.0 ? 0.0 : 0.5 * (a - c) / den;   // vertex of the parabola, in grid steps
            double step = orders[i + 1] - orders[i];
            (peak ? peaks : valleys).Add(orders[i] + offset * step);
        }
        return (peaks, valleys);
    }

    /// <summary>
    /// A copy of <paramref name="model"/> with a single-tuned filter added at <paramref name="pccBus"/>: a new bus (id one above the
    /// largest), a branch from the PCC with the reactor's reactance (and the series resistor) and a capacitive shunt on the new bus.
    /// The existing frequency sweep then sees the filter exactly (X_L scales with h, the shunt capacitor with h). Only the single-tuned
    /// topology is representable in the positive-sequence bus-shunt model; the others are evaluated as impedances by <see cref="Combined"/>.
    /// </summary>
    public static GridModel AddSingleTunedTo(GridModel model, int pccBus, FilterDesignResult filter)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Kind != FilterKind.SingleTuned) throw new ArgumentException("Only a single-tuned filter can be added as a branch and a shunt.", nameof(filter));
        if (!model.Buses.Any(b => b.Id == pccBus)) throw new ArgumentException($"Bus {pccBus} is not in the model.", nameof(pccBus));
        int id = model.Buses.Max(b => b.Id) + 1;
        double kv = filter.VoltageKv;
        double zBase = kv * kv / model.BaseMva;
        var buses = model.Buses.Append(new Bus(id, BusType.Pq, Bs: kv * kv / filter.CapacitorOhm, BaseKv: kv, Name: "single-tuned filter")).ToArray();
        var branches = model.Branches.Append(new Branch(pccBus, id, (filter.ResistorOhm ?? 0.0) / zBase, filter.ReactorOhm / zBase, Name: "filter reactor")).ToArray();
        return model.CopyWith(buses: buses, branches: branches);
    }
}
