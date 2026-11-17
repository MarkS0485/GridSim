using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Numerics;

namespace GridSim.Core.Analysis.EmtLineStudy;

/// <summary>The impulse applied at the sending end of the line under study.</summary>
public enum LineSourceKind
{
    /// <summary>A step <c>e(t) = A</c> for <c>t &gt; 0</c>, zero at and before <c>t = 0</c>.</summary>
    Step,
    /// <summary>A double-exponential impulse <c>e(t) = A (exp(-alpha t) - exp(-beta t))</c>, <c>beta &gt; alpha &gt; 0</c>.</summary>
    DoubleExponential,
}

/// <summary>What terminates the receiving end of the line.</summary>
public enum LineTerminationKind
{
    /// <summary>Open circuit (reflection +1).</summary>
    Open,
    /// <summary>Short to ground, represented as <see cref="EmtLineStudyRunner.ShortCircuitOhm"/> (reflection close to -1;
    /// the oracle uses the represented resistance, so the comparison is exact).</summary>
    Short,
    /// <summary>A resistance to ground (ohm).</summary>
    Resistance,
    /// <summary>A capacitance to ground (farad).</summary>
    Capacitance,
}

/// <summary>The sending-end source: a Thevenin EMF behind a resistance.</summary>
/// <param name="Kind">Waveform.</param>
/// <param name="AmplitudeV">EMF amplitude A (V), positive.</param>
/// <param name="ResistanceOhm">Source resistance (ohm); null means matched (equal to the line's Zc).</param>
/// <param name="AlphaPerS">Double-exponential decay rate alpha (1/s); required for that kind only.</param>
/// <param name="BetaPerS">Double-exponential rise rate beta (1/s), greater than alpha; required for that kind only.</param>
public sealed record LineSource(LineSourceKind Kind, double AmplitudeV, double? ResistanceOhm = null, double? AlphaPerS = null, double? BetaPerS = null);

/// <summary>The receiving-end termination.</summary>
/// <param name="Kind">Kind.</param>
/// <param name="Value">Ohm for <see cref="LineTerminationKind.Resistance"/>, farad for
/// <see cref="LineTerminationKind.Capacitance"/>; null otherwise.</param>
public sealed record LineTermination(LineTerminationKind Kind, double? Value = null);

/// <summary>
/// RC3 P8 (EMT): a single-line impulse study. One lossy (or lossless) constant-parameter line between a Thevenin source
/// and a termination, run on the incumbent <see cref="EmtEngine"/> through <see cref="LossyLineExpansion"/>, with the
/// base run and a refinement grid over timestep divisors and section counts.
/// </summary>
/// <param name="Line">Per-metre line data.</param>
/// <param name="Segments">Bergeron sections for the base run.</param>
/// <param name="Source">Sending-end source.</param>
/// <param name="Termination">Receiving-end termination.</param>
/// <param name="DtSeconds">Base timestep (s).</param>
/// <param name="DurationSeconds">Simulated time (s); a whole number of base steps.</param>
/// <param name="SampleEvery">Base-run trace decimation (every k-th step is reported; 1 = every step).</param>
/// <param name="TimestepDivisors">Refinement: each divides the base timestep (strictly increasing, starting at 1).</param>
/// <param name="SegmentCounts">Refinement: section counts (strictly increasing).</param>
public sealed record EmtLineStudyRequest(
    LossyLineParameters Line, int Segments, LineSource Source, LineTermination Termination,
    double DtSeconds, double DurationSeconds, int SampleEvery,
    IReadOnlyList<int> TimestepDivisors, IReadOnlyList<int> SegmentCounts);

/// <summary>Quantities derived from the declared line and terminations.</summary>
public sealed record EmtLineDerived(
    double SurgeImpedanceOhm, double VelocityMPerS, double TravelTimeSeconds, double TotalResistanceOhm, double TotalConductanceS,
    double FrontAttenuationNepers, double FrontAttenuation, bool Distortionless, double SourceResistanceOhm,
    double SourceReflection, double? LoadReflection, double? LoadValue, string LoadRepresentation);

/// <summary>Measured wave quantities of one run, next to their analytic expectation.</summary>
/// <param name="LaunchStep">First step at which the sending end is non-zero.</param>
/// <param name="ArrivalStep">First step at which the receiving end is non-zero (null if it never is).</param>
/// <param name="MeasuredDelaySeconds">(arrival - launch) * dt.</param>
/// <param name="ExpectedDelaySeconds">The declared travel time length/velocity.</param>
/// <param name="MeasuredFrontAttenuation">Receiving-end front over <c>(1 + Gamma_L)</c> times the ideal launched wave;
/// null for a capacitive termination (its front reflection is frequency dependent).</param>
/// <param name="ExpectedFrontAttenuation">exp(-(R'/(2Zc) + G'Zc/2) length).</param>
/// <param name="MeasuredReflection">The returned wave at the sending end at <c>launch + 2 tau</c>, over
/// <c>A^2 (1 + Gamma_S)</c> times the launched wave (for a step, the jump between consecutive samples; for an impulse,
/// the sample minus the ideal incident wave); null for a capacitive termination or when the run ends before the
/// return.</param>
/// <param name="ExpectedReflection">(Zl - Zc)/(Zl + Zc).</param>
public sealed record EmtLineMeasures(int LaunchStep, int? ArrivalStep, double? MeasuredDelaySeconds, double ExpectedDelaySeconds,
    double? MeasuredFrontAttenuation, double ExpectedFrontAttenuation, double? MeasuredReflection, double? ExpectedReflection);

/// <summary>One refinement run.</summary>
/// <param name="TimestepDivisor">Base dt divided by this.</param>
/// <param name="DtSeconds">The run's timestep.</param>
/// <param name="Segments">Sections.</param>
/// <param name="Steps">Steps run.</param>
/// <param name="MaxAbsErrorV">Largest |model - analytic| over both ends and every step (V); null without an exact oracle.</param>
/// <param name="MaxRelativeError">The same over the source amplitude.</param>
/// <param name="Measures">Wave measures.</param>
public sealed record EmtLineRefinementRow(int TimestepDivisor, double DtSeconds, int Segments, int Steps,
    double? MaxAbsErrorV, double? MaxRelativeError, EmtLineMeasures Measures);

/// <summary>An observed convergence order between two refinement rows: <c>log(e1/e2)/log(h1/h2)</c>.</summary>
/// <param name="Axis"><c>timestep</c> or <c>segments</c>.</param>
/// <param name="FixedValue">The other axis' value (segments for the timestep axis, divisor for the segments axis).</param>
/// <param name="From">Coarser value on the axis.</param>
/// <param name="To">Finer value on the axis.</param>
/// <param name="ErrorFrom">Error at the coarser value (V).</param>
/// <param name="ErrorTo">Error at the finer value (V).</param>
/// <param name="ObservedOrder">Null when either error is at rounding level (below
/// <see cref="EmtLineStudyRunner.RoundingFloor"/> times the amplitude) or unavailable.</param>
public sealed record EmtLineConvergence(string Axis, int FixedValue, int From, int To, double ErrorFrom, double ErrorTo, double? ObservedOrder);

/// <summary>The base-run trace (decimated).</summary>
public sealed record EmtLineTrace(int SampleEvery, IReadOnlyList<double> TimeSeconds, IReadOnlyList<double> SendingV, IReadOnlyList<double> ReceivingV,
    IReadOnlyList<double?> SendingAnalyticV, IReadOnlyList<double?> ReceivingAnalyticV, IReadOnlyList<double> SendingCurrentA);

/// <summary>The study result.</summary>
public sealed record EmtLineStudyResult(
    string Model, EmtLineDerived Derived, string Oracle, string OracleBasis, LossyLineLayout BaseLayout, EmtLineRefinementRow Base,
    EmtLineTrace Trace, IReadOnlyList<EmtLineRefinementRow> Refinement, IReadOnlyList<EmtLineConvergence> Convergence,
    IReadOnlyList<string> Deferred, string Status);

/// <summary>
/// RC3 P8 (EMT): runs an <see cref="EmtLineStudyRequest"/>. Order of operations: validate the request; derive Zc,
/// velocity, travel time, attenuation and reflection coefficients; select the analytic oracle; expand and run the base
/// case; run every refinement pair (divisor-major, then sections) on a fresh engine; compute the convergence orders.
/// <para>The exact oracle exists for a distortionless line (R'C' = G'L', including lossless) driven by a matched
/// source: every wave then travels undistorted with attenuation <c>A = exp(-R' length/Zc)</c>, nothing returns from
/// the source, so <c>v_r(t) = (1 + Gamma) A v_i(t - tau)</c> and <c>v_s(t) = v_i(t) + Gamma A^2 v_i(t - 2 tau)</c> for a
/// resistive termination, and the first-order <c>Zc C dv/dt + v = 2 A v_i(t - tau)</c> for a capacitive one. Any other
/// line has no closed form (its step response is a Bessel-function series), so only the front attenuation and the
/// delay are compared.</para>
/// </summary>
public static class EmtLineStudyRunner
{
    /// <summary>The resistance a <see cref="LineTerminationKind.Short"/> is represented by (ohm).</summary>
    public const double ShortCircuitOhm = 1e-6;

    /// <summary>Errors below this fraction of the source amplitude are treated as rounding: no convergence order is
    /// computed from them (a lossless line with a resistive termination is exact to rounding at every step).</summary>
    public const double RoundingFloor = 1e-11;

    /// <summary>Largest number of steps in one run.</summary>
    public const int MaxStepsPerRun = 2_000_000;

    /// <summary>The documented step order.</summary>
    public static readonly IReadOnlyList<string> StepOrder =
    [
        "validate request",
        "derive line and termination quantities",
        "select analytic oracle",
        "expand and run base case",
        "run refinement grid (divisor-major, then sections)",
        "compute observed convergence orders",
    ];

    /// <summary>What is not implemented or not evidenced.</summary>
    public static readonly IReadOnlyList<string> DeferredItems =
    [
        "Frequency-dependent line models (J. Marti / universal line model) and their fitting-order comparison: not implemented; the model is constant-parameter.",
        "Phase-domain multi-conductor lines (coupled phases, modal transformation): not implemented; the line is a single conductor over ground.",
        "PSCAD or EMTP cross-validation with matched line data: deferred, no comparator is available on this machine; the evidence is analytic only.",
    ];

    /// <summary>Run the study. Throws <see cref="ArgumentException"/> listing every problem with the request.</summary>
    public static EmtLineStudyResult Run(EmtLineStudyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        LossyLineParameters line = request.Line;
        double zc = line.SurgeImpedanceOhm;
        double rs = request.Source.ResistanceOhm ?? zc;
        double gammaS = (rs - zc) / (rs + zc);
        (double? gammaL, double? loadValue, string loadText) = Load(request.Termination, zc);
        var derived = new EmtLineDerived(zc, line.VelocityMPerS, line.TravelTimeSeconds, line.TotalResistanceOhm, line.TotalConductanceS,
            line.FrontAttenuationNepers, line.FrontAttenuation, line.IsDistortionless(), rs, gammaS, gammaL, loadValue, loadText);

        bool matched = request.Source.ResistanceOhm is null || request.Source.ResistanceOhm == zc;
        bool exact = matched && derived.Distortionless && Degenerate(request) is null;
        string oracle = exact ? "EXACT" : "FRONT AND DELAY ONLY";
        string basis = exact
            ? "Distortionless (or lossless) line with a matched source: closed-form travelling-wave solution at every step."
            : !matched ? "The source is not matched to Zc: waves re-reflect at the source; only the delay, front attenuation and first reflection are compared."
            : !derived.Distortionless ? "R'C' != G'L': the line distorts (Bessel-function step response, no closed form); only the delay and the front attenuation exp(-(R'/(2Zc) + G'Zc/2) length) are compared."
            : Degenerate(request)!;

        int segmentsBase = request.Segments;
        (EmtLineRefinementRow baseRow, LossyLineLayout baseLayout, EmtLineTrace trace) = RunOne(request, derived, exact, 1, segmentsBase, trace: true);

        var rows = new List<EmtLineRefinementRow>();
        foreach (int div in request.TimestepDivisors)
            foreach (int seg in request.SegmentCounts)
                rows.Add(RunOne(request, derived, exact, div, seg, trace: false).Row);

        var conv = new List<EmtLineConvergence>();
        if (exact)
        {
            foreach (int seg in request.SegmentCounts)
                for (int i = 1; i < request.TimestepDivisors.Count; i++)
                    conv.Add(Order("timestep", seg, request.TimestepDivisors[i - 1], request.TimestepDivisors[i],
                        Find(rows, request.TimestepDivisors[i - 1], seg), Find(rows, request.TimestepDivisors[i], seg), request.Source.AmplitudeV));
            foreach (int div in request.TimestepDivisors)
                for (int i = 1; i < request.SegmentCounts.Count; i++)
                    conv.Add(Order("segments", div, request.SegmentCounts[i - 1], request.SegmentCounts[i],
                        Find(rows, div, request.SegmentCounts[i - 1]), Find(rows, div, request.SegmentCounts[i]), request.Source.AmplitudeV));
        }

        return new EmtLineStudyResult(
            "Constant-parameter Bergeron line, lumped-resistance split: per section R/4 - lossless tau/2 - R/2 - lossless tau/2 - R/4, conductance G/4 at each half-section terminal (incumbent EmtEngine, trapezoidal, no CDA events).",
            derived, oracle, basis, baseLayout, baseRow, trace, rows, conv, DeferredItems, "EVALUATED");
    }

    private static EmtLineRefinementRow Find(List<EmtLineRefinementRow> rows, int div, int seg) =>
        rows.First(r => r.TimestepDivisor == div && r.Segments == seg);

    private static EmtLineConvergence Order(string axis, int fixedValue, int from, int to, EmtLineRefinementRow a, EmtLineRefinementRow b, double amplitude)
    {
        double ea = a.MaxAbsErrorV ?? double.NaN, eb = b.MaxAbsErrorV ?? double.NaN;
        // Below RoundingFloor * amplitude an error is rounding, not discretisation: no order is claimed from it.
        double floor = RoundingFloor * amplitude;
        double? order = ea > floor && eb > floor ? DetMath.Log(ea / eb) / DetMath.Log((double)to / from) : null;
        return new EmtLineConvergence(axis, fixedValue, from, to, ea, eb, order);
    }

    private static (double? Gamma, double? Value, string Text) Load(LineTermination t, double zc) => t.Kind switch
    {
        LineTerminationKind.Open => (1.0, null, "open circuit (no element; reflection +1)"),
        LineTerminationKind.Short => ((ShortCircuitOhm - zc) / (ShortCircuitOhm + zc), ShortCircuitOhm, $"short represented as {ShortCircuitOhm:R} ohm to ground"),
        LineTerminationKind.Resistance => ((t.Value!.Value - zc) / (t.Value.Value + zc), t.Value, $"{t.Value.Value:R} ohm to ground"),
        _ => (null, t.Value, $"{t.Value!.Value:R} F to ground (reflection frequency dependent: -1 at the front, +1 at DC)"),
    };

    private static string? Degenerate(EmtLineStudyRequest r)
    {
        if (r.Termination.Kind != LineTerminationKind.Capacitance || r.Source.Kind != LineSourceKind.DoubleExponential) return null;
        double tc = r.Line.SurgeImpedanceOhm * r.Termination.Value!.Value;
        if (Math.Abs(r.Source.AlphaPerS!.Value * tc - 1.0) < 1e-9 || Math.Abs(r.Source.BetaPerS!.Value * tc - 1.0) < 1e-9)
            return "The capacitive time constant Zc*C equals 1/alpha or 1/beta (a repeated root); the closed form used does not cover it.";
        return null;
    }

    private static void Validate(EmtLineStudyRequest r)
    {
        var bad = new List<string>(r.Line.Problems());
        void Positive(string name, double v) { if (!(v > 0) || !double.IsFinite(v)) bad.Add($"{name} must be a positive finite number; got {v:R}"); }
        if (r.Segments < 1 || r.Segments > LossyLineExpansion.MaxSegments) bad.Add($"segments must be 1 to {LossyLineExpansion.MaxSegments}");
        Positive("source.amplitudeV", r.Source.AmplitudeV);
        if (r.Source.ResistanceOhm is double rsv) Positive("source.resistanceOhm", rsv);
        if (r.Source.Kind == LineSourceKind.DoubleExponential)
        {
            if (r.Source.AlphaPerS is not double a || r.Source.BetaPerS is not double b) bad.Add("a doubleExponential source needs alphaPerS and betaPerS");
            else
            {
                Positive("source.alphaPerS", a);
                Positive("source.betaPerS", b);
                if (!(b > a)) bad.Add("source.betaPerS must be greater than source.alphaPerS");
            }
        }
        else if (r.Source.AlphaPerS is not null || r.Source.BetaPerS is not null) bad.Add("alphaPerS and betaPerS apply to a doubleExponential source only");
        switch (r.Termination.Kind)
        {
            case LineTerminationKind.Resistance:
            case LineTerminationKind.Capacitance:
                if (r.Termination.Value is double v) Positive("termination.value", v);
                else bad.Add($"a {r.Termination.Kind} termination needs a value");
                break;
            default:
                if (r.Termination.Value is not null) bad.Add($"a {r.Termination.Kind} termination takes no value");
                break;
        }
        Positive("dtSeconds", r.DtSeconds);
        Positive("durationSeconds", r.DurationSeconds);
        if (r.SampleEvery < 1) bad.Add("sampleEvery must be at least 1");
        if (bad.Count == 0)
        {
            double steps = r.DurationSeconds / r.DtSeconds;
            if (Math.Abs(steps - Math.Round(steps)) > 1e-6 * Math.Max(1.0, steps)) bad.Add("durationSeconds must be a whole number of timesteps");
        }
        if (r.TimestepDivisors is null || r.TimestepDivisors.Count == 0 || r.TimestepDivisors[0] != 1 ||
            r.TimestepDivisors.Zip(r.TimestepDivisors.Skip(1)).Any(p => p.Second <= p.First))
            bad.Add("timestepDivisors must start at 1 and strictly increase");
        if (r.SegmentCounts is null || r.SegmentCounts.Count == 0 || r.SegmentCounts.Any(s => s < 1 || s > LossyLineExpansion.MaxSegments) ||
            r.SegmentCounts.Zip(r.SegmentCounts.Skip(1)).Any(p => p.Second <= p.First))
            bad.Add($"segmentCounts must be strictly increasing values 1 to {LossyLineExpansion.MaxSegments}");
        if (bad.Count == 0)
        {
            long baseSteps = (long)Math.Round(r.DurationSeconds / r.DtSeconds);
            long worst = baseSteps * r.TimestepDivisors![^1];
            if (worst > MaxStepsPerRun) bad.Add($"the finest refinement run needs {worst} steps; at most {MaxStepsPerRun} are allowed");
            // Every run's half-section travel time must be a whole number of its timesteps.
            foreach (int div in r.TimestepDivisors!)
                foreach (int seg in r.SegmentCounts!.Append(r.Segments))
                {
                    double s = r.Line.TravelTimeSeconds / (2.0 * seg) / (r.DtSeconds / div);
                    double w = Math.Round(s);
                    if (w < 1 || Math.Abs(s - w) > LossyLineExpansion.StepQuantisationTolerance * Math.Max(1.0, w))
                        bad.Add($"with dt/{div} and {seg} section(s) the half-section travel time is {s:R} steps, not a whole number of at least 1");
                }
        }
        if (bad.Count > 0) throw new ArgumentException("EMT line study refused: " + string.Join("; ", bad.Distinct()));
    }

    private static Func<double, double> Emf(LineSource s)
    {
        double amp = s.AmplitudeV;
        if (s.Kind == LineSourceKind.Step) return t => t > 0 ? amp : 0.0;
        double a = s.AlphaPerS!.Value, b = s.BetaPerS!.Value;
        return t => t > 0 ? amp * (DetMath.Exp(-a * t) - DetMath.Exp(-b * t)) : 0.0;
    }

    private static (EmtLineRefinementRow Row, LossyLineLayout Layout, EmtLineTrace Trace) RunOne(
        EmtLineStudyRequest r, EmtLineDerived d, bool exact, int divisor, int segments, bool trace)
    {
        double dt = r.DtSeconds / divisor;
        int steps = (int)Math.Round(r.DurationSeconds / r.DtSeconds) * divisor;
        Func<double, double> emf = Emf(r.Source);
        var elements = new List<EmtElement> { new VoltageSource(0, 0, d.SourceResistanceOhm, emf) };
        switch (r.Termination.Kind)
        {
            case LineTerminationKind.Short: elements.Add(new ShuntElement(1, 1, G: 1.0 / ShortCircuitOhm)); break;
            case LineTerminationKind.Resistance: elements.Add(new ShuntElement(1, 1, G: 1.0 / r.Termination.Value!.Value)); break;
            case LineTerminationKind.Capacitance: elements.Add(new ShuntElement(1, 1, C: r.Termination.Value!.Value)); break;
        }
        LossyLineNetwork net = LossyLineExpansion.Expand(2, elements, [new LossyBergeronLine(0, 0, 1, r.Line, segments)], dt);
        LossyLineLayout layout = net.Lines[0];
        var probes = new[]
        {
            new EmtProbe("v:send", EmtProbeKind.NodeVoltage, 0),
            new EmtProbe("v:recv", EmtProbeKind.NodeVoltage, 1),
            new EmtProbe("i:send", EmtProbeKind.BranchCurrent, layout.SendingEndElementId),
        };
        var engine = new EmtEngine(net.Network, dt, new EmtOptions());
        EmtResult res = engine.Simulate(new EmtCase(net.Network, dt, steps * dt, probes));

        int nTau = 2 * segments * layout.HalfSectionSteps;   // the realised one-way delay in steps
        double vScale = d.SurgeImpedanceOhm / (d.SurgeImpedanceOhm + d.SourceResistanceOhm);
        double Vinc(int m) => m <= 0 ? 0.0 : vScale * emf(m * dt);   // the ideal launched wave at step m
        var oracle = exact ? new Oracle(r, d, dt, nTau, Vinc) : null;

        double thr = 1e-12 * r.Source.AmplitudeV;
        int launch = -1, arrival = -1;
        double maxErr = 0.0;
        for (int k = 0; k < res.Points.Count; k++)
        {
            double vs = res.Points[k].Probe[0], vr = res.Points[k].Probe[1];
            if (launch < 0 && Math.Abs(vs) > thr) launch = k;
            if (arrival < 0 && Math.Abs(vr) > thr) arrival = k;
            if (oracle is not null)
            {
                maxErr = Math.Max(maxErr, Math.Abs(vs - oracle.Sending(k)));
                maxErr = Math.Max(maxErr, Math.Abs(vr - oracle.Receiving(k)));
            }
        }

        double? frontRatio = null, reflection = null;
        if (launch > 0 && arrival > 0 && d.LoadReflection is double gl && Vinc(launch) != 0)
        {
            frontRatio = res.Points[arrival].Probe[1] / ((1.0 + gl) * Vinc(launch));
            int ret = launch + 2 * nTau;
            if (ret < res.Points.Count)
            {
                double a2 = d.FrontAttenuation * d.FrontAttenuation;
                // A step's returning front is the jump between consecutive samples (which also removes the lumped
                // terminal offset); a smooth impulse has no jump, so the ideal incident wave is subtracted instead.
                double jump = r.Source.Kind == LineSourceKind.Step
                    ? res.Points[ret].Probe[0] - res.Points[ret - 1].Probe[0]
                    : res.Points[ret].Probe[0] - Vinc(ret);
                reflection = jump / (a2 * (1.0 + d.SourceReflection) * Vinc(launch));
            }
        }
        var measures = new EmtLineMeasures(launch, arrival >= 0 ? arrival : null,
            launch >= 0 && arrival >= 0 ? (arrival - launch) * dt : null, d.TravelTimeSeconds,
            frontRatio, d.FrontAttenuation, reflection, d.LoadReflection);
        var row = new EmtLineRefinementRow(divisor, dt, segments, steps,
            oracle is null ? null : maxErr, oracle is null ? null : maxErr / r.Source.AmplitudeV, measures);

        EmtLineTrace? tr = null;
        if (trace)
        {
            var t = new List<double>(); var s = new List<double>(); var rv = new List<double>();
            var sa = new List<double?>(); var ra = new List<double?>(); var ia = new List<double>();
            for (int k = 0; k < res.Points.Count; k += r.SampleEvery)
            {
                t.Add(res.Points[k].TSeconds); s.Add(res.Points[k].Probe[0]); rv.Add(res.Points[k].Probe[1]); ia.Add(res.Points[k].Probe[2]);
                sa.Add(oracle?.Sending(k)); ra.Add(oracle?.Receiving(k));
            }
            tr = new EmtLineTrace(r.SampleEvery, t, s, rv, sa, ra, ia);
        }
        return (row, layout, tr!);
    }

    /// <summary>The closed-form distortionless travelling-wave solution, evaluated on step indices so a discontinuity
    /// falls on the same sample in the model and the oracle.</summary>
    private sealed class Oracle(EmtLineStudyRequest r, EmtLineDerived d, double dt, int nTau, Func<int, double> vinc)
    {
        private readonly double _a = d.FrontAttenuation;

        // The receiving-end voltage at step k.
        public double Receiving(int k)
        {
            int m = k - nTau;
            if (d.LoadReflection is double g) return (1.0 + g) * _a * vinc(m);
            return CapacitorResponse(m);
        }

        // The sending-end voltage at step k: incident plus the wave reflected at the load, one travel time later.
        public double Sending(int k)
        {
            if (d.LoadReflection is double g) return vinc(k) + g * _a * _a * vinc(k - 2 * nTau);
            int m = k - nTau;                                   // far-end time index of the wave now returning
            double reflectedAtLoad = CapacitorResponse(m - nTau) - _a * vinc(m - nTau);
            return vinc(k) + _a * reflectedAtLoad;
        }

        // y(m*dt) for Zc C y' + y = 2 A v_inc(t), y(0) = 0 (the far-end voltage, time measured from arrival).
        private double CapacitorResponse(int m)
        {
            if (m <= 0) return 0.0;
            double t = m * dt;
            double tc = d.SurgeImpedanceOhm * r.Termination.Value!.Value;
            double scale = 2.0 * _a * d.SurgeImpedanceOhm / (d.SurgeImpedanceOhm + d.SourceResistanceOhm) * r.Source.AmplitudeV;
            double decay = DetMath.Exp(-t / tc);
            if (r.Source.Kind == LineSourceKind.Step) return scale * (1.0 - decay);
            double a = r.Source.AlphaPerS!.Value, b = r.Source.BetaPerS!.Value;
            return scale * ((DetMath.Exp(-a * t) - decay) / (1.0 - a * tc) - (DetMath.Exp(-b * t) - decay) / (1.0 - b * tc));
        }
    }
}
