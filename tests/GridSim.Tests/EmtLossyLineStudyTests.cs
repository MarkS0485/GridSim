using System.Text.Json;
using GridSim.Core.Analysis.EmtLineStudy;
using GridSim.Core.Dynamics.Emt;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// RC3 P8 (EMT): the constant-parameter lossy Bergeron line (<see cref="LossyLineExpansion"/>) and the line impulse
/// study (<see cref="EmtLineStudyRunner"/>), checked against closed-form travelling-wave results derived here:
/// travel time length*sqrt(L'C'), reflection (Zl - Zc)/(Zl + Zc), front attenuation exp(-(R'/(2Zc) + G'Zc/2) length),
/// the exact undistorted solution of a distortionless line, the DC end state, and the first-order capacitor response.
/// </summary>
public sealed class EmtLossyLineStudyTests(ITestOutputHelper output)
{
    // L' = 1 uH/m, C' = 1/(9e10) F/m: Zc = 300 ohm, v = 3e8 m/s; 30 km gives tau = 100 us.
    private const double Lp = 1e-6, Cp = 1.0 / 9e10, Len = 30_000.0, Amp = 1000.0;
    private static double Zc => Math.Sqrt(Lp / Cp);

    private static LossyLineParameters Line(double rPerM = 0.0, double gPerM = 0.0) => new(Len, rPerM, Lp, Cp, gPerM);

    // Distortionless: G' = R' C'/L'. R' = 1 mohm/m gives R = 30 ohm, R/Zc = 0.1 neper.
    private static LossyLineParameters Distortionless() => Line(1e-3, 1e-3 * Cp / Lp);

    private static EmtLineStudyRequest Request(LossyLineParameters line, LineTermination term, LineSource? source = null, int segments = 1,
        double dt = 1e-6, double duration = 500e-6, int[]? divisors = null, int[]? segmentCounts = null) =>
        new(line, segments, source ?? new LineSource(LineSourceKind.Step, Amp), term, dt, duration, 1,
            divisors ?? [1], segmentCounts ?? [segments]);

    [Fact]
    public void Derived_quantities_are_the_textbook_ones()
    {
        LossyLineParameters p = Distortionless();
        Assert.Equal(300.0, p.SurgeImpedanceOhm, 9);
        Assert.Equal(3e8, p.VelocityMPerS, 0);
        Assert.Equal(100e-6, p.TravelTimeSeconds, 15);
        Assert.Equal(Len / p.VelocityMPerS, p.TravelTimeSeconds, 15);
        Assert.True(p.IsDistortionless());
        Assert.Equal(0.1, p.FrontAttenuationNepers, 12);   // R/(2Zc) + G Zc/2 = 0.05 + 0.05
        Assert.Equal(Math.Exp(-0.1), p.FrontAttenuation, 14);
        Assert.False(Line(1e-3).IsDistortionless());
    }

    [Theory]
    [InlineData(LineTerminationKind.Open, double.NaN)]
    [InlineData(LineTerminationKind.Short, double.NaN)]
    [InlineData(LineTerminationKind.Resistance, 900.0)]   // Gamma = +0.5
    [InlineData(LineTerminationKind.Resistance, 100.0)]   // Gamma = -0.5
    [InlineData(LineTerminationKind.Resistance, 300.0)]   // matched, Gamma = 0
    public void Lossless_line_reproduces_delay_and_reflection_exactly_at_every_step(LineTerminationKind kind, double value)
    {
        var term = new LineTermination(kind, double.IsNaN(value) ? null : value);
        EmtLineStudyResult r = EmtLineStudyRunner.Run(Request(Line(), term, segments: 2));
        double zl = kind switch { LineTerminationKind.Open => double.PositiveInfinity, LineTerminationKind.Short => EmtLineStudyRunner.ShortCircuitOhm, _ => value };
        double gamma = double.IsInfinity(zl) ? 1.0 : (zl - Zc) / (zl + Zc);

        Assert.Equal("EXACT", r.Oracle);
        Assert.Equal(gamma, r.Derived.LoadReflection!.Value, 14);
        EmtLineMeasures m = r.Base.Measures;
        Assert.Equal(1, m.LaunchStep);
        if (kind == LineTerminationKind.Short)
            Assert.True(r.Base.MaxAbsErrorV < 1e-9 * Amp);
        else
        {
            Assert.Equal(101, m.ArrivalStep);                       // launched at step 1, tau = 100 steps later
            Assert.Equal(100e-6, m.MeasuredDelaySeconds!.Value, 15);
            Assert.Equal(1.0, m.MeasuredFrontAttenuation!.Value, 12);
            if (gamma != 0) Assert.Equal(gamma, m.MeasuredReflection!.Value, 12);
            Assert.True(r.Base.MaxAbsErrorV < 1e-9 * Amp, $"max error {r.Base.MaxAbsErrorV}");
        }
        // Spot values from the closed form, independent of the oracle code: v_r = (1 + Gamma) A/2 after tau,
        // v_s = A/2 (1 + Gamma) after 2 tau.
        double vRecv = r.Trace.ReceivingV[150], vSend = r.Trace.SendingV[250];
        Assert.Equal((1 + gamma) * Amp / 2, vRecv, 6);
        Assert.Equal((1 + gamma) * Amp / 2, vSend, 6);
        Assert.Equal(Amp / 2, r.Trace.SendingV[150], 9);   // before the return
    }

    [Fact]
    public void Distortionless_line_converges_to_the_undistorted_attenuated_wave_as_sections_increase()
    {
        // Exact: v_r(t) = exp(-R/Zc) A/2 for t > tau; v_s(t) = A/2 always (matched load, matched source).
        EmtLineStudyResult r = EmtLineStudyRunner.Run(Request(Distortionless(), new LineTermination(LineTerminationKind.Resistance, Zc),
            segments: 1, dt: 0.125e-6, duration: 300e-6, segmentCounts: [1, 2, 4, 8, 16]));
        Assert.Equal("EXACT", r.Oracle);
        double[] err = r.Refinement.Select(x => x.MaxAbsErrorV!.Value).ToArray();
        output.WriteLine("distortionless max error by sections 1,2,4,8,16: " + string.Join(", ", err.Select(e => e.ToString("R"))));
        foreach (EmtLineConvergence c in r.Convergence) output.WriteLine($"{c.Axis} {c.From}->{c.To}: order {c.ObservedOrder:R}");
        for (int i = 1; i < err.Length; i++) Assert.True(err[i] < err[i - 1] / 1.5, $"error did not fall from {err[i - 1]} to {err[i]}");
        Assert.True(err[^1] < 2e-3 * Amp, $"16-section error {err[^1]}");
        // Measured front attenuation tends to exp(-0.1) and the delay is exact at every section count.
        foreach (EmtLineRefinementRow row in r.Refinement) Assert.Equal(100e-6, row.Measures.MeasuredDelaySeconds!.Value, 15);
        double front16 = r.Refinement[^1].Measures.MeasuredFrontAttenuation!.Value;
        Assert.True(Math.Abs(front16 - Math.Exp(-0.1)) < 1e-3, $"front {front16}");
        double[] frontErr = r.Refinement.Select(x => Math.Abs(x.Measures.MeasuredFrontAttenuation!.Value - Math.Exp(-0.1))).ToArray();
        for (int i = 1; i < frontErr.Length; i++) Assert.True(frontErr[i] < frontErr[i - 1]);
    }

    [Fact]
    public void Lumped_front_equals_the_independent_junction_chain_and_tends_to_exp_minus_R_over_2Zc()
    {
        // G = 0 lossy line, matched source and load. The front through each lumped junction is the static transmission
        // of a resistor network between two Zc lines (derived below); the model's first-arrival sample must equal it.
        double rTot = 60.0;   // R/(2Zc) = 0.1
        foreach (int n in new[] { 1, 2, 4, 8 })
        {
            // dt = 0.625 us: tau / (2n) = 6.25 us at n = 8 is then a whole 10 steps (20, 40, 80 at n = 4, 2, 1), which the
            // study requires; the front oracle below does not depend on dt.
            EmtLineStudyResult r = EmtLineStudyRunner.Run(Request(Line(rTot / Len), new LineTermination(LineTerminationKind.Resistance, Zc),
                segments: n, dt: 0.625e-6));
            Assert.Equal("FRONT AND DELAY ONLY", r.Oracle);
            double rs = rTot / n;
            // Source: E behind Zc, series rs/4, into the line: launched front E Zc/(Zc + Zc + rs/4).
            double front = Amp * Zc / (2 * Zc + rs / 4);
            // Each interior junction (rs/2 in series between two Zc lines): T = 2Zc/(2Zc + rs/2). There are 2n - 1.
            for (int j = 0; j < 2 * n - 1; j++) front *= 2 * Zc / (2 * Zc + rs / 2);
            // Load: series rs/4 into Zc: v_load = 2 v Zc/(Zc + rs/4 + Zc).
            front *= 2 * Zc / (2 * Zc + rs / 4);
            double model = r.Base.Measures.MeasuredFrontAttenuation!.Value * (Amp / 2);   // un-normalise ((1 + 0) * A/2)
            Assert.Equal(front, model, 9);
            Assert.Equal(Math.Exp(-0.1), r.Derived.FrontAttenuation, 14);
            output.WriteLine($"n={n}: front ratio {model / (Amp / 2):R}, exp(-R/2Zc) {Math.Exp(-0.1):R}");
        }
    }

    [Fact]
    public void Lossy_line_settles_to_the_dc_divider()
    {
        // G = 0: at DC the line is its series resistance, so the far end settles at E Zl/(Rs + R + Zl).
        double rTot = 60.0, zl = 600.0;
        EmtLineStudyResult r = EmtLineStudyRunner.Run(Request(Line(rTot / Len), new LineTermination(LineTerminationKind.Resistance, zl),
            segments: 4, duration: 20e-3, dt: 2.5e-6));
        double expected = Amp * zl / (Zc + rTot + zl);
        Assert.Equal(expected, r.Trace.ReceivingV[^1], 6);
        Assert.Equal(Amp * (rTot + zl) / (Zc + rTot + zl), r.Trace.SendingV[^1], 6);
    }

    [Fact]
    public void Capacitive_termination_shows_second_order_timestep_convergence()
    {
        // Lossless line into C with a double-exponential impulse: the only discretisation error is the trapezoidal
        // capacitor companion, so the error falls as dt^2.
        var src = new LineSource(LineSourceKind.DoubleExponential, Amp, null, 1.0 / 50e-6, 1.0 / 1.2e-6);
        EmtLineStudyResult r = EmtLineStudyRunner.Run(Request(Line(), new LineTermination(LineTerminationKind.Capacitance, 50e-9), src,
            segments: 1, dt: 0.25e-6, duration: 200e-6, divisors: [1, 2, 4, 8], segmentCounts: [1]));
        Assert.Equal("EXACT", r.Oracle);
        double[] err = r.Refinement.Select(x => x.MaxAbsErrorV!.Value).ToArray();
        output.WriteLine("capacitor max error by dt/1,2,4,8: " + string.Join(", ", err.Select(e => e.ToString("R"))));
        foreach (EmtLineConvergence c in r.Convergence.Where(c => c.Axis == "timestep"))
        {
            output.WriteLine($"dt/{c.From} -> dt/{c.To}: order {c.ObservedOrder:R}");
            Assert.InRange(c.ObservedOrder!.Value, 1.8, 2.2);
        }
        Assert.True(err[^1] < 1e-3 * Amp);
    }

    [Fact]
    public void Unmatched_source_is_compared_on_delay_and_reflections_only()
    {
        var src = new LineSource(LineSourceKind.Step, Amp, ResistanceOhm: 100.0);
        EmtLineStudyResult r = EmtLineStudyRunner.Run(Request(Line(), new LineTermination(LineTerminationKind.Open), src));
        Assert.Equal("FRONT AND DELAY ONLY", r.Oracle);
        Assert.Null(r.Base.MaxAbsErrorV);
        Assert.Empty(r.Convergence);
        Assert.Equal(-0.5, r.Derived.SourceReflection, 14);
        Assert.Equal(1.0, r.Base.Measures.MeasuredReflection!.Value, 12);   // open end, after the (1 + Gamma_S) factor
        Assert.Equal(100e-6, r.Base.Measures.MeasuredDelaySeconds!.Value, 15);
    }

    [Fact]
    public void Requests_the_engine_would_round_or_cannot_model_are_refused()
    {
        // tau/(2n) = 50 us is not a whole number of 3 us steps.
        var ex = Assert.Throws<ArgumentException>(() => EmtLineStudyRunner.Run(Request(Line(), new LineTermination(LineTerminationKind.Open), dt: 3e-6, duration: 300e-6)));
        Assert.Contains("not a whole number", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => EmtLineStudyRunner.Run(Request(Line(-1.0), new LineTermination(LineTerminationKind.Open))));
        Assert.Throws<ArgumentException>(() => EmtLineStudyRunner.Run(Request(Line(), new LineTermination(LineTerminationKind.Resistance))));
        Assert.Throws<ArgumentException>(() => EmtLineStudyRunner.Run(Request(Line(), new LineTermination(LineTerminationKind.Open), divisors: [2])));
        Assert.Throws<ArgumentException>(() => LossyLineExpansion.Expand(2, [], [new LossyBergeronLine(0, 0, 0, Line())], 1e-6));
        // The lossy line is not an engine element: the incumbent engine never sees it, only its expansion.
        Assert.False(typeof(EmtElement).IsAssignableFrom(typeof(LossyBergeronLine)));
    }

    [Fact]
    public void Expansion_uses_only_incumbent_elements_and_keeps_the_existing_ones_first()
    {
        var src = new VoltageSource(7, 0, 300.0, _ => 1.0);
        LossyLineNetwork net = LossyLineExpansion.Expand(2, [src], [new LossyBergeronLine(3, 0, 1, Distortionless(), 2)], 1e-6);
        Assert.Same(src, net.Network.Elements[0]);
        Assert.All(net.Network.Elements.Skip(1), e => Assert.True(e is SeriesBranch or BergeronLine or ShuntElement));
        Assert.Equal(4, net.Network.Elements.OfType<BergeronLine>().Count());
        Assert.Equal(8 + 0, net.Network.Elements.OfType<ShuntElement>().Count());
        Assert.Equal(6, net.Network.Elements.OfType<SeriesBranch>().Count());
        Assert.Equal(25, net.Lines[0].HalfSectionSteps);   // tau/(2n) = 100 us / 4 = 25 steps of 1 us
        Assert.Equal(2 * 4 + 1, net.Lines[0].InternalNodes.Count);   // per section p, q, r, s, plus the joint between sections
        Assert.Equal(2 + 9, net.Network.NodeCount);
    }

    [Fact]
    public void Repeat_runs_are_bit_identical()
    {
        EmtLineStudyRequest q = Request(Distortionless(), new LineTermination(LineTerminationKind.Capacitance, 20e-9),
            new LineSource(LineSourceKind.DoubleExponential, Amp, null, 2e4, 8e5), segments: 2, divisors: [1, 2], segmentCounts: [1, 2]);
        string a = JsonSerializer.Serialize(EmtLineStudyRunner.Run(q));
        string b = JsonSerializer.Serialize(EmtLineStudyRunner.Run(q));
        Assert.Equal(a, b);
    }
}
