using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Lines;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt.FrequencyDependent;

/// <summary>Options of a line fit. The band is the frequency range over which the fit is made and judged; outside it the
/// model is an extrapolation and is not validated.</summary>
public sealed record FdFitOptions
{
    /// <summary>Lowest frequency of the band, Hz (positive).</summary>
    public double BandHzLow { get; init; } = 1.0;
    /// <summary>Highest frequency of the band, Hz.</summary>
    public double BandHzHigh { get; init; } = 10_000.0;
    /// <summary>Log-spaced samples across the band.</summary>
    public int Samples { get; init; } = 80;
    /// <summary>Number of real poles of each fitted function.</summary>
    public int Order { get; init; } = 8;
    /// <summary>Pole-relocation iterations.</summary>
    public int Iterations { get; init; } = 20;
    /// <summary>Enforce passivity by the minimal perturbation described in <see cref="FdLineFitter"/>; when false any violation is refused.</summary>
    public bool EnforcePassivity { get; init; } = true;
    /// <summary>Largest relative perturbation passivity enforcement may make before the fit is refused (default 0.05).</summary>
    public double MaxPassivityPerturbation { get; init; } = 0.05;
}

/// <summary>The fit report of one mode.</summary>
public sealed record FdModeFitReport(
    string Name,
    int Order,
    double DelaySeconds,
    double YcMaxRelError,
    double YcRmsRelError,
    double AMaxRelError,
    double ARmsRelError,
    double YcPassivityShiftSiemens,
    double APassivityScale,
    double MinRealYcOnGrid,
    double MaxAbsAOnGrid,
    double CompanionConductanceSiemens);

/// <summary>The fit report of a line: what was fitted, over what band, and how well.</summary>
public sealed record FdLineFitReport(double BandHzLow, double BandHzHigh, int Samples, int Order, double LengthMetres,
    double StepSeconds, IReadOnlyList<FdModeFitReport> Modes);

/// <summary>A fitted line ready for <see cref="FdLine"/>.</summary>
public sealed record FdLineModel(IReadOnlyList<FdMode> Modes, double[,]? Transform, FdLineFitReport Report);

/// <summary>
/// Fits the characteristic admittance <c>Yc(w) = sqrt(Y/Z)</c> and the propagation function <c>A(w) = exp(-gamma l)</c>
/// (<c>gamma = sqrt(Z Y)</c>) of a line from its per-length impedance and admittance over a stated band, with
/// <see cref="RationalFit"/>. The delay removed from <c>A</c> is the group delay at the top of the band, rounded DOWN to a
/// whole number of steps so the delay is read from history without interpolation (the remainder, under one step, stays in
/// the fitted <c>A_min</c>). The fit error is reported separately for Yc and A, per mode.
/// <para><b>Passivity.</b> A single-mode line is passive when <c>Re Yc(jw) &gt;= 0</c> and <c>|A(jw)| &lt;= 1</c> at every
/// frequency. These are checked on a dense log grid reaching a decade beyond the band each way plus <c>s = infinity</c>
/// (the constant term). If <c>Re Yc</c> dips below zero the constant term is raised by the least amount that cures it; if
/// <c>|A|</c> exceeds one <c>A_min</c> is scaled down by its maximum. A perturbation larger than
/// <see cref="FdFitOptions.MaxPassivityPerturbation"/> of the quantity, or any violation when enforcement is off, REFUSES the
/// fit (it is never silently used). The poles are real and negative by construction, so the recursive convolution is
/// unconditionally stable. This does not prove passivity of the whole network (a line coupled to a nonlinear or
/// active network is outside it).</para>
/// </summary>
public static class FdLineFitter
{
    private const double MuZero = 4.0e-7 * Math.PI;

    /// <summary>Fit one mode from per-metre series impedance <paramref name="zPerM"/> (ohm/m) and shunt admittance
    /// <paramref name="yPerM"/> (S/m) sampled at <paramref name="freqHz"/>, for a line <paramref name="lengthM"/> long, to be
    /// run at step <paramref name="dt"/>.</summary>
    public static (FdMode Mode, FdModeFitReport Report) FitMode(string name, double[] freqHz, Complex[] zPerM, Complex[] yPerM,
        double lengthM, double dt, FdFitOptions options)
    {
        static Exception R(string m) => DomainRefusal.Mark(new ArgumentException(m));
        int m = freqHz.Length;
        if (zPerM.Length != m || yPerM.Length != m) throw R("Frequency, impedance and admittance arrays differ in length.");
        if (!(lengthM > 0 && double.IsFinite(lengthM))) throw R("The line length must be positive.");
        if (!(dt > 0 && double.IsFinite(dt))) throw R("The step must be positive.");
        var w = new double[m];
        var yc = new Complex[m];
        var a = new Complex[m];
        var gamma = new Complex[m];
        for (int i = 0; i < m; i++)
        {
            w[i] = 2.0 * Math.PI * freqHz[i];
            Complex z = zPerM[i], y = yPerM[i];
            if (!(z.Real > 0)) throw R($"Mode '{name}': the series resistance at {freqHz[i]} Hz is not positive.");
            Complex g = ComplexDet.Sqrt(z * y);
            if (g.Real < 0) g = -g;
            gamma[i] = g;
            Complex zc = ComplexDet.Sqrt(z / y);
            if (zc.Real < 0) zc = -zc;
            yc[i] = 1.0 / zc;
            a[i] = ComplexDet.Exp(-g * lengthM);
        }
        // Group delay at the top of the band from the exact phase constant, then round down to whole steps.
        double tauGroup = lengthM * (gamma[m - 1].Imaginary - gamma[m - 2].Imaginary) / (w[m - 1] - w[m - 2]);
        int steps = (int)Math.Floor(tauGroup / dt + 1e-9);
        if (steps < 1) throw R($"Mode '{name}': the travel time {tauGroup:R} s is shorter than the step {dt:R} s; the step must not exceed it.");
        double tau = steps * dt;
        var amin = new Complex[m];
        for (int i = 0; i < m; i++) amin[i] = a[i] * ComplexDet.Exp(new Complex(0.0, w[i] * tau));

        RationalFitResult fy = RationalFit.Fit(w, yc, options.Order, options.Iterations);
        RationalFitResult fa = RationalFit.Fit(w, amin, options.Order, options.Iterations);
        RationalFunction ycF = fy.Function, aF = fa.Function;

        // Passivity on a dense grid a decade beyond the band each way.
        double wLo = w[0] / 10.0, wHi = w[m - 1] * 10.0;
        const int grid = 400;
        double minRe = double.PositiveInfinity, maxA = 0.0, maxYc = 0.0;
        for (int i = 0; i < grid; i++)
        {
            double ww = wLo * DetMath.Pow(wHi / wLo, (double)i / (grid - 1));
            Complex yv = ycF.AtOmega(ww);
            minRe = Math.Min(minRe, yv.Real);
            maxA = Math.Max(maxA, Complex.Abs(aF.AtOmega(ww)));
            maxYc = Math.Max(maxYc, Complex.Abs(yv));
        }
        minRe = Math.Min(minRe, ycF.D);
        maxA = Math.Max(maxA, Math.Abs(aF.D));
        double ycShift = 0.0, aScale = 1.0;
        if (minRe < 0.0)
        {
            ycShift = -minRe * (1.0 + 1e-6);
            if (!options.EnforcePassivity || ycShift > options.MaxPassivityPerturbation * maxYc)
                throw R($"Mode '{name}': the fitted characteristic admittance is not passive (least real part {minRe:E3} S) and cannot be corrected by a perturbation of at most {options.MaxPassivityPerturbation:P1}; the fit is refused. Raise the order or narrow the band.");
            ycF = new RationalFunction(ycF.D + ycShift, ycF.Poles, ycF.Residues);
        }
        if (maxA > 1.0)
        {
            aScale = 1.0 / (maxA * (1.0 + 1e-9));
            if (!options.EnforcePassivity || (1.0 - aScale) > options.MaxPassivityPerturbation)
                throw R($"Mode '{name}': the fitted propagation function exceeds 1 in magnitude ({maxA:R}) and cannot be corrected by a scaling of at most {options.MaxPassivityPerturbation:P1}; the fit is refused.");
            aF = new RationalFunction(aF.D * aScale, aF.Poles, aF.Residues.Select(r => r * aScale).ToArray());
        }

        double maxEy = 0.0, sumEy = 0.0, maxEa = 0.0, sumEa = 0.0;
        for (int i = 0; i < m; i++)
        {
            double ey = Complex.Abs(ycF.AtOmega(w[i]) - yc[i]) / Complex.Abs(yc[i]);
            double ea = Complex.Abs(aF.AtOmega(w[i]) - amin[i]) / Complex.Abs(amin[i]);
            maxEy = Math.Max(maxEy, ey); sumEy += ey * ey;
            maxEa = Math.Max(maxEa, ea); sumEa += ea * ea;
        }
        FdLine.Coeffs(ycF.Poles, dt, out _, out double[] lam, out _);
        double gm = ycF.D;
        for (int k = 0; k < ycF.Poles.Length; k++) gm += ycF.Residues[k] * lam[k];
        if (!(gm > 0)) throw R($"Mode '{name}': the companion conductance of the fitted admittance at step {dt:R} s is not positive ({gm:E3} S); the fit is refused.");

        var rep = new FdModeFitReport(name, options.Order, tau, maxEy, Math.Sqrt(sumEy / m), maxEa, Math.Sqrt(sumEa / m),
            ycShift, aScale, Math.Min(minRe, ycF.D), Math.Min(maxA, 1.0), gm);
        return (new FdMode(name, ycF, aF, tau), rep);
    }

    /// <summary>
    /// Fit a THREE-PHASE transposed line from the R4-04 line constants. <paramref name="template"/> carries the conductors,
    /// earth method, resistivity and transposition; its frequency is replaced by each band frequency in turn, so the
    /// frequency dependence is whatever the line-constants calculation gives (the earth-return integral; the conductor
    /// resistance is the stated input, with no skin-effect model). The modes are the zero sequence (Z0, C0) and the positive
    /// sequence twice (Z1, C1), joined by the orthonormal Clarke-type transformation, exact for a transposed line.
    /// </summary>
    public static FdLineModel FromLineConstants(LineGeometry template, double lengthKm, FdFitOptions options, double dt)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(options);
        static Exception R(string m) => DomainRefusal.Mark(new ArgumentException(m));
        if (!(lengthKm > 0 && double.IsFinite(lengthKm))) throw R("The line length must be positive.");
        if (!(options.BandHzLow > 0) || !(options.BandHzHigh > options.BandHzLow)) throw R("The band must have 0 < low < high (Hz).");
        if (options.Samples < 2 * options.Order + 4) throw R($"Order {options.Order} needs at least {2 * options.Order + 4} band samples.");
        int m = options.Samples;
        var f = new double[m];
        var z0 = new Complex[m]; var z1 = new Complex[m]; var y0 = new Complex[m]; var y1 = new Complex[m];
        for (int i = 0; i < m; i++)
        {
            f[i] = options.BandHzLow * DetMath.Pow(options.BandHzHigh / options.BandHzLow, (double)i / (m - 1));
            LineConstantsResult r = LineConstantsSolver.Solve(template with { FrequencyHz = f[i] });
            if (r.Sequence is not { } s) throw R(r.SequenceNote ?? "The line has no sequence constants (a three-phase transposed line is needed).");
            double w = 2.0 * Math.PI * f[i];
            z0[i] = s.Z0 / 1000.0; z1[i] = s.Z1 / 1000.0;
            y0[i] = new Complex(0.0, w * s.C0FaradPerKm / 1000.0); y1[i] = new Complex(0.0, w * s.C1FaradPerKm / 1000.0);
        }
        double lengthM = lengthKm * 1000.0;
        (FdMode m0, FdModeFitReport r0) = FitMode("zero", f, z0, y0, lengthM, dt, options);
        (FdMode m1, FdModeFitReport r1) = FitMode("positive", f, z1, y1, lengthM, dt, options);
        double s3 = Math.Sqrt(3.0), s6 = Math.Sqrt(6.0), s2 = Math.Sqrt(2.0);
        var t = new double[3, 3]
        {
            { 1 / s3, 1 / s3, 1 / s3 },
            { 2 / s6, -1 / s6, -1 / s6 },
            { 0, 1 / s2, -1 / s2 },
        };
        var modes = new[] { m0, m1, m1 with { Name = "positive-2" } };
        var reports = new[] { r0, r1, r1 with { Name = "positive-2" } };
        return new FdLineModel(modes, t, new FdLineFitReport(options.BandHzLow, options.BandHzHigh, m, options.Order, lengthM, dt, reports));
    }

    /// <summary>The exact open-ended sending-end impedance <c>Z0 coth(gamma l)</c> of a line with per-metre <paramref name="z"/>
    /// and <paramref name="y"/> (the analytic transmission-line formula; the oracle the simulation is compared with).</summary>
    public static Complex OpenEndImpedance(Complex z, Complex y, double lengthM)
    {
        Complex gamma = ComplexDet.Sqrt(z * y);
        if (gamma.Real < 0) gamma = -gamma;
        Complex zc = ComplexDet.Sqrt(z / y);
        if (zc.Real < 0) zc = -zc;
        Complex a2 = ComplexDet.Exp(-2.0 * gamma * lengthM);
        return zc * (1.0 + a2) / (1.0 - a2);
    }

    /// <summary>The open-ended sending-end impedance of a FITTED mode: <c>(1/Yc) (1 + A^2) / (1 - A^2)</c> with
    /// <c>A = exp(-s tau) A_min</c>.</summary>
    public static Complex OpenEndImpedance(FdMode mode, double omega)
    {
        Complex yc = mode.Yc.AtOmega(omega);
        Complex a = ComplexDet.Exp(new Complex(0.0, -omega * mode.DelaySeconds)) * mode.Amin.AtOmega(omega);
        Complex a2 = a * a;
        return (1.0 + a2) / (1.0 - a2) / yc;
    }
}

/// <summary>Deterministic complex square root and exponential (only correctly rounded <c>Math.Sqrt</c> and the repository's
/// DetMath transcendental functions).</summary>
internal static class ComplexDet
{
    public static Complex Sqrt(Complex z)
    {
        double re = z.Real, im = z.Imaginary;
        double r = Math.Sqrt(re * re + im * im);
        double a = Math.Sqrt(Math.Max((r + re) * 0.5, 0.0));
        double b = Math.Sqrt(Math.Max((r - re) * 0.5, 0.0));
        return new Complex(a, im < 0 ? -b : b);
    }

    public static Complex Exp(Complex z)
    {
        double e = DetMath.Exp(z.Real);
        (double s, double c) = DetMath.SinCos(z.Imaginary);
        return new Complex(e * c, e * s);
    }
}
