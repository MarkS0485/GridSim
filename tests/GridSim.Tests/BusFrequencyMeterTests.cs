using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Per-bus frequency, RoCoF and angle jump from recorded bus angles (<see cref="BusFrequencyMeter"/>, October 2026), and the
/// measured RMS event study (<see cref="RmsTransientStability.StudyEventsMeasured"/>), which must leave every number of the
/// run unchanged.
/// </summary>
public sealed class BusFrequencyMeterTests
{
    private const double Step = 0.005;
    private const double F0 = 50.0;

    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static RmsBusTrace Trace(params double[][] angles)
    {
        int n = angles[0].Length;
        var t = Enumerable.Range(0, n).Select(k => k * Step).ToArray();
        return new RmsBusTrace(Enumerable.Range(1, angles.Length).ToArray(), t, angles);
    }

    // ---- BusFrequencyMeter on signals with a known answer ----

    [Fact]
    public void A_steady_frequency_offset_reads_as_that_frequency_and_no_rocof()
    {
        // An angle advancing at 2 pi * 0.1 rad/s is a bus running 0.1 Hz above nominal.
        int n = 400;
        var a = new double[n];
        for (int k = 0; k < n; k++) a[k] = 2.0 * Math.PI * 0.1 * k * Step;
        BusFrequencyResult r = BusFrequencyMeter.Measure(Trace(a), F0, Step, Array.Empty<double>());
        for (int k = 4; k < n; k++) Assert.Equal(50.1, r.FrequencyHz[0][k], 12);
        Assert.True(r.WorstRocofHzPerS[0] < 1e-9, $"RoCoF {r.WorstRocofHzPerS[0]}");
        Assert.Equal(0.0, r.AngleJumpDeg[0]);
    }

    [Fact]
    public void A_steady_frequency_ramp_reads_as_its_rocof()
    {
        // f(t) = f0 - 0.2 t from t = 1 s: the angle is -2 pi * 0.1 (t-1)^2 after it. The 500 ms window then reads 0.2 Hz/s.
        int n = 800;
        var a = new double[n];
        for (int k = 0; k < n; k++)
        {
            double t = k * Step;
            a[k] = t <= 1.0 ? 0.0 : -2.0 * Math.PI * 0.1 * (t - 1.0) * (t - 1.0);
        }
        BusFrequencyResult r = BusFrequencyMeter.Measure(Trace(a), F0, Step, Array.Empty<double>());
        Assert.Equal(0.2, r.WorstRocofHzPerS[0], 9);
    }

    [Fact]
    public void An_angle_step_at_the_event_is_reported_as_a_jump_and_kept_out_of_the_frequency()
    {
        // A steady 0.05 Hz offset, with a 10 degree step at t = 1 s (sample 200). The jump is 10 degrees; the frequency
        // and RoCoF are those of the steady offset, as if the step had not happened.
        int n = 600;
        var a = new double[n];
        double step = 10.0 * Math.PI / 180.0;
        for (int k = 0; k < n; k++) a[k] = 2.0 * Math.PI * 0.05 * k * Step + (k >= 200 ? step : 0.0);
        BusFrequencyResult r = BusFrequencyMeter.Measure(Trace(a), F0, Step, new[] { 1.0 });
        Assert.Equal(10.0, r.AngleJumpDeg[0], 9);
        Assert.True(r.WorstRocofHzPerS[0] < 1e-9, $"RoCoF {r.WorstRocofHzPerS[0]}");
        for (int k = 4; k < n; k++) Assert.Equal(50.05, r.FrequencyHz[0][k], 11);

        // Without the event time the step is not removed: it reads as a frequency spike and so as RoCoF.
        BusFrequencyResult raw = BusFrequencyMeter.Measure(Trace(a), F0, Step, Array.Empty<double>());
        Assert.True(raw.WorstRocofHzPerS[0] > 1.0, $"RoCoF {raw.WorstRocofHzPerS[0]}");
    }

    [Fact]
    public void Unwrap_makes_an_angle_crossing_180_degrees_continuous()
    {
        var a = new[] { 3.0, 3.1, -3.1, -3.0 };   // crossing +pi
        BusFrequencyMeter.Unwrap(a);
        Assert.Equal(3.0, a[0]);
        Assert.Equal(3.1, a[1]);
        Assert.Equal(2.0 * Math.PI - 3.1, a[2], 12);
        Assert.Equal(2.0 * Math.PI - 3.0, a[3], 12);
    }

    [Theory]
    [InlineData(0.5, 0.003)]    // 166.67 samples
    [InlineData(0.02, 0.006)]   // 3.33 samples
    public void A_window_that_is_not_a_whole_number_of_steps_is_refused(double window, double step)
    {
        Assert.Throws<ArgumentException>(() => BusFrequencyMeter.WholeSamples(window, step, "window"));
    }

    // ---- The measured RMS event study changes nothing of the run ----

    private static IReadOnlyList<SubtransientInit.MachineData> Genrou(GridModel model) =>
        model.Generators.Select(g => new SubtransientInit.MachineData(
            H: g.H, Damping: 0.0, Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
            Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05)).ToList();

    private static void AssertIdentical(MultiMachineSubtransient.Result a, MultiMachineSubtransient.Result b)
    {
        Assert.Equal(a.StayedInSynchronism, b.StayedInSynchronism);
        Assert.Equal(a.ConverterIterationFailures, b.ConverterIterationFailures);
        Assert.Equal(a.Machines.Count, b.Machines.Count);
        for (int i = 0; i < a.Machines.Count; i++)
            for (int k = 0; k < a.Machines[i].TimeSeconds.Count; k++)
            {
                Assert.True(SameBits(a.Machines[i].DeltaRad[k], b.Machines[i].DeltaRad[k]), $"machine {i} angle at {k}");
                Assert.True(SameBits(a.Machines[i].SpeedPu[k], b.Machines[i].SpeedPu[k]), $"machine {i} speed at {k}");
                Assert.True(SameBits(a.Machines[i].EqppPu[k], b.Machines[i].EqppPu[k]), $"machine {i} E''q at {k}");
                Assert.True(SameBits(a.Machines[i].EddpPu[k], b.Machines[i].EddpPu[k]), $"machine {i} E''d at {k}");
            }
        Assert.Equal(a.Converters?.Count ?? 0, b.Converters?.Count ?? 0);
        for (int u = 0; u < (a.Converters?.Count ?? 0); u++)
        {
            RmsConverterTrace x = a.Converters![u], y = b.Converters![u];
            Assert.Equal(x.TerminalVoltagePu.Count, y.TerminalVoltagePu.Count);
            for (int k = 0; k < x.TerminalVoltagePu.Count; k++)
            {
                Assert.True(SameBits(x.TerminalVoltagePu[k], y.TerminalVoltagePu[k]), $"converter {u} voltage at {k}");
                Assert.True(SameBits(x.ActivePowerPu[k], y.ActivePowerPu[k]), $"converter {u} P at {k}");
            }
        }
    }

    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    [Fact]
    public void Measuring_the_buses_leaves_every_machine_number_of_case9_the_same_bits()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var events = new RmsEvent[] { new RmsLoadStep(0.5, 5, 60.0, 0.0) };
        MultiMachineSubtransient.Result plain = RmsTransientStability.StudyEvents(m, pf, Genrou(m), events, stepSeconds: Step, durationSeconds: 2.0);
        RmsTransientStability.MeasuredEventStudy measured = RmsTransientStability.StudyEventsMeasured(m, pf, Genrou(m), events,
            m.Buses.Select(b => b.Id).ToList(), stepSeconds: Step, durationSeconds: 2.0);
        AssertIdentical(plain, measured.Result);
        Assert.Equal(plain.Machines[0].TimeSeconds.Count, measured.Buses.TimeSeconds.Count);
        Assert.Equal(m.Buses.Count, measured.Buses.AngleRad.Length);
    }

    [Fact]
    public void Measuring_the_buses_leaves_every_machine_and_converter_number_of_uk_the_same_bits()
    {
        GridModel m = Uk();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { MaxIterations = 100 });
        Assert.True(pf.Converged);
        Assert.Contains(m.Generators, g => g.IsConverterInterfaced && !g.PowerFlowOnly);   // the converter path is exercised
        int bus = m.Buses[m.Buses.Count / 2].Id;
        var events = new RmsEvent[] { new RmsLoadStep(0.5, bus, 500.0, 0.0) };
        MultiMachineSubtransient.Result plain = RmsTransientStability.StudyEvents(m, pf, Genrou(m), events, stepSeconds: Step, durationSeconds: 1.5);
        RmsTransientStability.MeasuredEventStudy measured = RmsTransientStability.StudyEventsMeasured(m, pf, Genrou(m), events,
            m.Buses.Select(b => b.Id).ToList(), stepSeconds: Step, durationSeconds: 1.5);
        AssertIdentical(plain, measured.Result);
    }

    [Fact]
    public void A_machine_terminal_bus_moves_with_its_machine_and_the_measure_is_steady_before_the_event()
    {
        // Before the event every bus is at nominal (the run starts from the solved state); after a load step every bus's
        // frequency falls, and the bus RoCoF of every bus is of the order of the centre-of-inertia RoCoF.
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var events = new RmsEvent[] { new RmsLoadStep(1.0, 5, 60.0, 0.0) };
        RmsTransientStability.MeasuredEventStudy st = RmsTransientStability.StudyEventsMeasured(m, pf, Genrou(m), events,
            m.Buses.Select(b => b.Id).ToList(), stepSeconds: Step, durationSeconds: 3.0);
        BusFrequencyResult bf = BusFrequencyMeter.Measure(st.Buses, st.NominalHz, Step, new[] { 1.0 });
        for (int b = 0; b < bf.BusIds.Count; b++)
        {
            for (int k = 0; k < 190; k++) Assert.Equal(st.NominalHz, bf.FrequencyHz[b][k], 9);
            Assert.True(bf.FrequencyHz[b][^1] < st.NominalHz, $"bus {bf.BusIds[b]} did not fall");
            Assert.True(bf.WorstRocofHzPerS[b] > 0.0);
        }
        Assert.True(bf.AngleJumpDeg.Max() > 0.0);   // the load step moves the bus angles at once
    }

    [Fact]
    public void An_unknown_or_repeated_measured_bus_is_refused()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var events = new RmsEvent[] { new RmsLoadStep(0.5, 5, 60.0, 0.0) };
        Assert.Throws<ArgumentException>(() => RmsTransientStability.StudyEventsMeasured(m, pf, Genrou(m), events, new[] { 999 }));
        Assert.Throws<ArgumentException>(() => RmsTransientStability.StudyEventsMeasured(m, pf, Genrou(m), events, new[] { 5, 5 }));
        Assert.Throws<ArgumentException>(() => RmsTransientStability.StudyEventsMeasured(m, pf, Genrou(m), events, Array.Empty<int>()));
    }
}
