using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Impedance;

/// <summary>Which minor-loop criterion to apply.</summary>
public enum StabilityCriterion
{
    /// <summary>Scalar sequence-domain Middlebrook minor loop: the positive- and negative-sequence loops
    /// <c>Z_pp Y_pp</c> and <c>Z_nn Y_nn</c> in the converter's dq frame (Z_pp(f) = Z(f + f0), Z_nn(f) = Z(f - f0)),
    /// cross-sequence coupling neglected. Equal to <see cref="Mimo"/> whenever the converter has no cross-sequence
    /// coupling. Default.</summary>
    Siso,
    /// <summary>Full 2x2 generalised Nyquist (captures the PLL dq cross-coupling). Opt-in.</summary>
    Mimo,
}

/// <summary>Options for the impedance-based stability scan.</summary>
public sealed record ImpedanceStabilityOptions
{
    /// <summary>Lowest perturbation frequency (Hz). &gt; 0 (the driving-point solve is singular at DC).</summary>
    public double FMinHz { get; init; } = 0.1;
    /// <summary>Highest perturbation frequency (Hz) - a few times f0 to capture sub-/super-synchronous.</summary>
    public double FMaxHz { get; init; } = 250.0;
    /// <summary>Log-grid density.</summary>
    public int PointsPerDecade { get; init; } = 50;
    /// <summary>Nominal frequency (Hz). Defaults to <see cref="FrequencyLimits.NominalHz"/>; a caller scanning a
    /// model should pass that model's <c>FrequencyHz</c>; on the real-network path a different value is refused
    /// (<see cref="ImpedanceStabilityScan.Run"/>, R196 / D2).</summary>
    public double F0Hz { get; init; } = FrequencyLimits.NominalHz;
    /// <summary>The criterion (SISO default).</summary>
    public StabilityCriterion Criterion { get; init; } = StabilityCriterion.Siso;

    /// <summary>Use the canonical single-inverter-vs-Thevenin analytic grid impedance (exact). When false the
    /// scan uses the real network's harmonic driving-point solve at the converter bus.</summary>
    public bool UseCanonicalThevenin { get; init; } = true;
    /// <summary>The base-case SCR for the frequency sweep (canonical path).</summary>
    public double Scr { get; init; } = 3.0;
    /// <summary>Thevenin X/R ratio (canonical path).</summary>
    public double XOverR { get; init; } = 10.0;
    /// <summary>Converter rating (pu on baseMva) - sets the Thevenin |Z| from SCR.</summary>
    public double PRatedPu { get; init; } = 1.0;

    /// <summary>SCR sweep bounds and resolution (canonical path).</summary>
    public double ScrMin { get; init; } = 1.0;
    /// <summary>Upper SCR of the sweep.</summary>
    public double ScrMax { get; init; } = 10.0;
    /// <summary>Number of SCR steps.</summary>
    public int ScrSteps { get; init; } = 37;

    /// <summary>Harmonic model options for the driving-point solve (real-network path).</summary>
    public HarmonicModelOptions Harmonic { get; init; } = new();
}

/// <summary>One frequency sample of the scan (for export / cross-validation). Every quantity is on the
/// SYSTEM base (pu on the model's BaseMva): <see cref="YConv"/> is the converter's own admittance (pu on its
/// rating) multiplied by <see cref="ImpedanceStabilityOptions.PRatedPu"/>, so <c>L = ZGrid * YConv</c> holds
/// exactly as stored (R008).</summary>
public sealed record FreqSample(double FHz, Complex ZGrid, Complex YConv, Complex L, Complex Eig1, Complex Eig2);

/// <summary>One SCR sample of the sweep.</summary>
public sealed record ScrSample(double Scr, int Encirclements, bool Stable, double MinDistToMinusOne);

/// <summary>The independent re-derivation parameters carried on the export so the crossval sidecar can rebuild
/// the grid and converter impedance from scratch (canonical path): the SRF-PLL PI gains, the current-loop
/// roll-off, and the canonical Thevenin/operating parameters. Zero/absent for a non-GFL or real-network run.</summary>
public sealed record ImpedanceScanParams(
    double PllKp, double PllKi, double CurrentLoopWci, bool IdealPll,
    double Scr, double XOverR, double PRatedPu, double F0Hz, bool Canonical);

/// <summary>The full impedance-stability result.</summary>
public sealed record ImpedanceStabilityResult(
    int InverterBus, AssetKind Mode, ImpedanceModelDetail Detail, ProvenanceTag Provenance,
    StabilityCriterion Criterion, ConverterOperatingPoint OperatingPoint,
    IReadOnlyList<double> FrequenciesHz, IReadOnlyList<FreqSample> Frequency,
    NyquistVerdict Nyquist, IReadOnlyList<ScrSample> ScrSweep,
    double CriticalScr, bool BoundaryFound,
    int? RhpEigenvalueCountFromModal, bool? AgreesWithModal,
    ImpedanceScanParams? Params);

/// <summary>
/// The impedance-based (generalised-Nyquist) inverter-grid stability screen. It forms the return ratio
/// <c>L = Z_grid * Y_conv</c> across frequency, counts encirclements of (-1,0) for the verdict, and sweeps the
/// short-circuit ratio to find the critical SCR below which the interaction goes unstable - the weak-grid PLL
/// instability boundary. The grid impedance reuses the harmonic Y(h) stack; the converter admittance is the
/// small-signal model that the behavioural time-domain controllers lack.
/// </summary>
public static class ImpedanceStabilityScan
{
    /// <summary>The Nyquist WINDING grid always reaches down to at least this frequency (Hz) on the canonical
    /// path, so the return difference reaches its real DC limit and the contour closes (R164).</summary>
    public const double WindingGridMinHz = 1e-3;

    /// <summary>The winding grid always reaches up to at least this frequency (Hz), where the physical
    /// current-loop roll-off has made the return ratio proper.</summary>
    public const double WindingGridMaxHz = 1e5;

    /// <summary>Minimum density (points per decade) of the winding grid, whatever the reporting density.</summary>
    public const int WindingGridMinPointsPerDecade = 60;

    /// <summary>On the real-network path the winding grid starts at this multiple of the DC-arm guard frequency
    /// (<see cref="GridImpedanceAssembler.DcArmGuardFraction"/> * f0), comfortably on the solvable side.</summary>
    public const double RealNetworkWindingGuardMultiple = 2.0;

    /// <summary>Run the scan for one converter model. <paramref name="realModel"/> is required only when
    /// <see cref="ImpedanceStabilityOptions.UseCanonicalThevenin"/> is false.</summary>
    public static ImpedanceStabilityResult Run(
        ImpedanceStabilityOptions opt, IConverterImpedanceModel conv,
        GridModel? realModel = null, IComplexLinearSolver? solver = null)
    {
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(conv);
        solver ??= new RealExpandedComplexSolver();

        // R196 remainder (D2 nominal frequency, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): a real-network
        // scan evaluates the model's branch reactances (given at the model's nominal frequency) at f / f0, so f0 must BE the
        // model's frequency. The options default to the GB 50 Hz, which silently mis-scaled every reactance of a 60 Hz model
        // by 5/6 when a caller forgot to pass it. Fail loud on a mismatch (the CLI passes model.FrequencyHz; every test and
        // bundled 50 Hz case already agrees, so nothing that ran before is refused).
        if (!opt.UseCanonicalThevenin && realModel is not null
            && BitConverter.DoubleToInt64Bits(opt.F0Hz) != BitConverter.DoubleToInt64Bits(realModel.FrequencyHz))
            throw new ArgumentException($"ImpedanceStabilityOptions.F0Hz {opt.F0Hz} Hz differs from the model's nominal frequency "
                + $"{realModel.FrequencyHz} Hz ({realModel.Name}); a real-network scan must run at the model's own f0. "
                + "Set F0Hz = model.FrequencyHz.", nameof(opt));

        // R008: one base for both halves of the return ratio. The grid impedance (canonical Thevenin or the
        // real network's driving point) is pu on the SYSTEM base; the converter admittance models are pu on
        // the CONVERTER rating (ConverterOperatingPoint is "pu on the converter rating"). An admittance moves
        // from the converter base to the system base by the rating ratio S_conv / S_base = PRatedPu, so
        // Y_sys = Y_conv * PRatedPu and the canonical loop gain becomes L = Y_conv / SCR, independent of the
        // rating. Multiplying the two raw values (the old code) made L too small by a factor PRatedPu: a
        // 1200 MVA unit on a 100 MVA base looked 12 times stronger-gridded than it is.
        if (!double.IsFinite(opt.PRatedPu) || opt.PRatedPu <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(opt), opt.PRatedPu,
                "ImpedanceStabilityOptions.PRatedPu (the converter rating on the system base) must be a finite number > 0.");
        double ratingRatio = opt.PRatedPu;
        // Scale by a REAL scalar component-wise (never through a complex product), so PRatedPu = 1 leaves
        // every admittance bit-for-bit unchanged.
        Complex YPos(double w)
        {
            Complex y = conv.PositiveSequenceAdmittance(w);
            return new Complex(y.Real * ratingRatio, y.Imaginary * ratingRatio);
        }
        Complex2x2 YDq(double w)
        {
            Complex2x2 y = conv.AdmittanceDq(w);
            return new Complex2x2(
                new Complex(y.M11.Real * ratingRatio, y.M11.Imaginary * ratingRatio),
                new Complex(y.M12.Real * ratingRatio, y.M12.Imaginary * ratingRatio),
                new Complex(y.M21.Real * ratingRatio, y.M21.Imaginary * ratingRatio),
                new Complex(y.M22.Real * ratingRatio, y.M22.Imaginary * ratingRatio));
        }

        // Grid impedance provider. R245: the real-network path used to catch EVERY exception from the
        // driving-point solve and silently substitute the canonical Thevenin, with nothing in the result
        // saying which frequencies were not the real network. It now never substitutes: a real-network
        // driving-point failure propagates as an explicit error naming the frequency. The winding grid
        // below is built so the real path never lands on the dq DC arm or below the DC-arm guard, so a
        // failure that remains is a genuine singularity the caller must see.
        if (!opt.UseCanonicalThevenin && realModel is null)
            throw new ArgumentNullException(nameof(realModel),
                "UseCanonicalThevenin is false, so the real network model is required for the driving-point solve.");
        // The SISO grid impedance in the SAME frame as the converter admittance (bug hunt 2026-10-29, finding 14).
        //
        // The converter's Y_pp(w) = 1/2[(Y_dd + Y_qq) + j(Y_qd - Y_dq)] is the modified-sequence reduction of its dq
        // admittance at dq perturbation frequency w. The grid impedance it is paired with must be the SAME reduction of
        // the grid's dq impedance at the same w. For an RL Thevenin, Z_dq(jw) = (R + jwL) I + w0 L J (CanonicalTheveninDq),
        // whose reduction is Z_pp(w) = R + jwL + j w0 L = R + j(w + w0)L: the stationary-frame impedance at f + f0, NOT at
        // f. The real network says the same: GridImpedanceAssembler.Dq builds Z_dq from Zp = Z(f + f0) and
        // Zn = Z(f - f0), and its reduction is exactly Zp. The SISO loop used to pair Y_pp(f) with Z(f), so for an
        // inductive grid the loop gain was missing the whole j w0 L term in the PLL band, where weak-grid instability
        // lives; only the MIMO path (which pairs the dq matrices) agreed with the Routh oracle.
        //
        // The negative-frequency arm of the contour is not conj(L_pp(w)): for real-coefficient dq dynamics
        // L_pp(-jw) = conj(L_nn(jw)), with Z_nn(w) = Z(f - f0) and Y_nn = 1/2[(Y_dd + Y_qq) - j(Y_qd - Y_dq)]. The SISO
        // verdict therefore winds (1 + L_pp)(1 + L_nn), which IS conjugate-symmetric (so the positive-frequency grid
        // plus the conjugate mirror covers the full contour) and counts each pp/nn closed-loop pole pair (one
        // oscillatory mode of the real system) as two right-half-plane eigenvalues, the MIMO / modal convention.
        Complex ZStationary(double fs, double scr, double fReport)
        {
            if (opt.UseCanonicalThevenin)
                return GridImpedanceAssembler.CanonicalTheveninImpedance(scr, opt.XOverR, opt.PRatedPu, fs, opt.F0Hz);
            return RealNetwork(fReport, () => GridImpedanceAssembler.PositiveSequence(realModel!, conv.Bus, fs, opt.F0Hz, opt.Harmonic, solver));
        }
        Complex ZPos(double f, double scr) => ZStationary(f + opt.F0Hz, scr, f);   // Z_pp(f) = Z(f + f0)
        Complex ZNeg(double f, double scr) => ZStationary(f - opt.F0Hz, scr, f);   // Z_nn(f) = Z(f - f0)
        Complex YNeg(double w)
        {
            Complex2x2 y = YDq(w);
            return 0.5 * ((y.M11 + y.M22) - (Complex.ImaginaryOne * (y.M21 - y.M12)));
        }
        Complex2x2 ZDq(double f, double scr)
        {
            if (opt.UseCanonicalThevenin)
                return GridImpedanceAssembler.CanonicalTheveninDq(scr, opt.XOverR, opt.PRatedPu, 2.0 * Math.PI * f, opt.F0Hz);
            return RealNetwork(f, () => GridImpedanceAssembler.Dq(realModel!, conv.Bus, f, opt.F0Hz, opt.Harmonic, solver));
        }

        // The reporting grid is the user band; the WINDING grid is widened to [<=1e-3, >=1e5] Hz so the return
        // difference reaches its (real) DC and infinity limits and the Nyquist contour closes cleanly to an
        // integer encirclement count (the physical current-loop roll-off makes the return ratio proper, so a
        // wide-but-finite band suffices).
        double[] freqs = LogGrid(opt.FMinHz, opt.FMaxHz, opt.PointsPerDecade);
        double windLowHz = Math.Min(opt.FMinHz, WindingGridMinHz);
        bool mimo = opt.Criterion == StabilityCriterion.Mimo;
        if (!opt.UseCanonicalThevenin)
        {
            // R245: the real-network driving-point solve is singular on the DC arm, |h| = |f / f0| below
            // GridImpedanceAssembler.DcArmGuardFraction. The canonical winding floor of 1e-3 Hz sits below
            // that guard (h = 2e-5 at 50 Hz), which is why every low winding point used to be silently
            // replaced by the canonical Thevenin. For the real path the winding grid therefore starts at
            // twice the guard frequency (comfortably on the solvable side), or at FMinHz if that is lower
            // but still solvable. A user band that itself reaches into the DC arm cannot be evaluated on
            // the real network at all, so it fails loud.
            double guardHz = GridImpedanceAssembler.DcArmGuardFraction * opt.F0Hz;
            if (opt.FMinHz <= guardHz)
                throw new ArgumentOutOfRangeException(nameof(opt), opt.FMinHz,
                    $"FMinHz {opt.FMinHz} Hz reaches the DC arm of the real-network driving-point solve (|f/f0| <= " +
                    $"{GridImpedanceAssembler.DcArmGuardFraction}, i.e. f <= {guardHz} Hz), where the network is singular. " +
                    "Raise FMinHz or use the canonical Thevenin.");
            windLowHz = Math.Min(opt.FMinHz, RealNetworkWindingGuardMultiple * guardHz);
        }
        double[] wind = LogGrid(windLowHz, Math.Max(opt.FMaxHz, WindingGridMaxHz), Math.Max(opt.PointsPerDecade, WindingGridMinPointsPerDecade));
        if (!opt.UseCanonicalThevenin)
        {
            // R245: in the dq transform the negative arm is Z(f_p - f0), which lands on the DC arm when f_p is
            // within the guard fraction of f0. A WINDING point there is an internal sample of the contour, so
            // it is dropped (the contour is still closed by its neighbours). A REPORTING point there is part of
            // the user's band and is checked in the reporting loop, where it fails loud. The sequence-domain SISO
            // criterion reads Z_nn(f) = Z(f - f0) too, so it drops the same points.
            wind = wind.Where(f => !OnDqDcArm(f, opt.F0Hz)).ToArray();
        }

        var (zPosW, yPosW, zNegW, yNegW, zDqW, yDqW) = Eval(wind, opt.Scr);
        NyquistVerdict verdict = mimo
            ? MinorLoopNyquist.Mimo(wind, zDqW, yDqW)
            : MinorLoopNyquist.SisoSequence(wind, zPosW, yPosW, zNegW, yNegW);

        // Reporting samples over the user band (system base throughout, R008).
        var samples = new List<FreqSample>(freqs.Length);
        foreach (double f in freqs)
        {
            if (!opt.UseCanonicalThevenin && OnDqDcArm(f, opt.F0Hz))
                throw new InvalidOperationException(
                    $"Reporting frequency {f} Hz is within {GridImpedanceAssembler.DcArmGuardFraction} of f0 = {opt.F0Hz} Hz, " +
                    "where the real-network dq impedance is singular (the negative-sequence arm is at DC). " +
                    "Shift FMinHz, FMaxHz or PointsPerDecade so no reporting point falls on f0.");
            double w = 2.0 * Math.PI * f;
            Complex zp = ZPos(f, opt.Scr);
            Complex yp = YPos(w);
            Complex2x2 zdq = ZDq(f, opt.Scr);
            Complex2x2 ydq = YDq(w);
            (Complex e1, Complex e2) = ydq.Multiply(zdq).Eigenvalues();
            samples.Add(new FreqSample(f, zp, yp, zp * yp, e1, e2));
        }

        (Complex[] ZPosArr, Complex[] YPosArr, Complex[] ZNegArr, Complex[] YNegArr, Complex2x2[] ZDqArr, Complex2x2[] YDqArr)
            Eval(double[] fs, double scr)
        {
            var zpA = new Complex[fs.Length];
            var ypA = new Complex[fs.Length];
            var znA = new Complex[fs.Length];
            var ynA = new Complex[fs.Length];
            var zdA = new Complex2x2[fs.Length];
            var ydA = new Complex2x2[fs.Length];
            for (int i = 0; i < fs.Length; i++)
            {
                double w = 2.0 * Math.PI * fs[i];
                // The dq pair is read only by the MIMO criterion and the sequence pair only by the SISO one (R245:
                // never evaluate what the criterion does not use, so a real-network run cannot fail on it).
                if (mimo)
                {
                    zdA[i] = ZDq(fs[i], scr);
                    ydA[i] = YDq(w);
                }
                else
                {
                    zpA[i] = ZPos(fs[i], scr);
                    ypA[i] = YPos(w);
                    znA[i] = ZNeg(fs[i], scr);
                    ynA[i] = YNeg(w);
                }
            }
            return (zpA, ypA, znA, ynA, zdA, ydA);
        }

        // SCR sweep (canonical analytic) - rebuild Z_grid per SCR through the same assembler and count.
        var scrSweep = new List<ScrSample>();
        double criticalScr = double.NaN;
        bool boundaryFound = false;
        if (opt.UseCanonicalThevenin && opt.ScrSteps >= 2)
        {
            bool prevStable = true;
            double prevScr = double.NaN;
            for (int j = 0; j < opt.ScrSteps; j++)
            {
                double scr = opt.ScrMin + ((opt.ScrMax - opt.ScrMin) * j / (opt.ScrSteps - 1));
                var zp = new Complex[wind.Length];
                var zn = new Complex[wind.Length];
                var zdq = new Complex2x2[wind.Length];
                for (int i = 0; i < wind.Length; i++)
                {
                    if (mimo) zdq[i] = ZDq(wind[i], scr);
                    else { zp[i] = ZPos(wind[i], scr); zn[i] = ZNeg(wind[i], scr); }
                }
                NyquistVerdict v = mimo
                    ? MinorLoopNyquist.Mimo(wind, zdq, yDqW)
                    : MinorLoopNyquist.SisoSequence(wind, zp, yPosW, zn, yNegW);
                scrSweep.Add(new ScrSample(scr, v.Encirclements, v.Stable, v.MinDistToMinusOne));

                // Boundary: the SCR at which the sweep transitions unstable(low SCR) -> stable(high SCR).
                if (!boundaryFound && j > 0 && !prevStable && v.Stable)
                {
                    criticalScr = 0.5 * (prevScr + scr);   // between the last-unstable and first-stable SCR
                    boundaryFound = true;
                }
                prevStable = v.Stable; prevScr = scr;
            }
        }

        ImpedanceScanParams? prms = conv is GridFollowingImpedance gfl
            ? new ImpedanceScanParams(gfl.PllGains.Kp, gfl.PllGains.Ki, gfl.CurrentLoopWci, gfl.IsIdealPll,
                opt.Scr, opt.XOverR, opt.PRatedPu, opt.F0Hz, opt.UseCanonicalThevenin)
            : null;

        return new ImpedanceStabilityResult(
            conv.Bus, conv.Kind, conv.Detail, conv.Provenance, opt.Criterion, conv.OperatingPoint,
            freqs, samples, verdict, scrSweep, criticalScr, boundaryFound,
            RhpEigenvalueCountFromModal: null, AgreesWithModal: null, Params: prms);
    }

    /// <summary>Attach a Workstream-C modal cross-check to a completed result (the CLI/test supplies the
    /// independent RHP count; agreement is the Routh-corroborated equality).</summary>
    public static ImpedanceStabilityResult WithModalCrossCheck(this ImpedanceStabilityResult r, int rhpCount)
        => r with { RhpEigenvalueCountFromModal = rhpCount, AgreesWithModal = r.Nyquist.UnstableModeCount == rhpCount };

    /// <summary>True when the dq negative-sequence arm of perturbation frequency <paramref name="fHz"/>
    /// (h = (f - f0) / f0) falls inside the DC-arm guard, where the real-network solve is singular.</summary>
    private static bool OnDqDcArm(double fHz, double f0Hz)
        => Math.Abs((fHz - f0Hz) / f0Hz) < GridImpedanceAssembler.DcArmGuardFraction;

    /// <summary>Evaluate one real-network driving-point quantity, re-raising any failure with the frequency
    /// that caused it (R245: never silently substitute the canonical Thevenin).</summary>
    private static T RealNetwork<T>(double fHz, Func<T> evaluate)
    {
        try
        {
            return evaluate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"The real-network grid impedance could not be evaluated at {fHz} Hz: {ex.Message} " +
                "The scan does not substitute the canonical Thevenin for the real network.", ex);
        }
    }

    /// <summary>A log-spaced frequency grid [fMin, fMax] at the given points-per-decade (inclusive of fMax).</summary>
    public static double[] LogGrid(double fMin, double fMax, int pointsPerDecade)
    {
        if (fMin <= 0.0) throw new ArgumentOutOfRangeException(nameof(fMin), fMin, "fMin must be > 0 (DC is singular).");
        if (fMax <= fMin) throw new ArgumentOutOfRangeException(nameof(fMax), fMax, "fMax must exceed fMin.");
        double decades = DetMath.Log10(fMax / fMin);
        int n = Math.Max(2, (int)Math.Round(decades * pointsPerDecade) + 1);
        var g = new double[n];
        for (int i = 0; i < n; i++) g[i] = fMin * DetMath.Pow(10.0, decades * i / (n - 1));
        g[n - 1] = fMax;
        return g;
    }
}
