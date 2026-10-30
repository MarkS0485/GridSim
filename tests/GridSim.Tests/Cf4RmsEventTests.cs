using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R061 rest (CF4, October 2026): RMS studies driven by scheduled events beyond the single bolted fault
/// (<see cref="RmsTransientStability.StudyEvents"/>): bolted fault and clearance, branch trip, load step and generator
/// trip, through the standard control library (exciters and governors), with fail-loud validation of every event input.
/// WSCC-9 with GENROU-like machine data, as the exciter and governor study tests use.
/// </summary>
public sealed class Cf4RmsEventTests
{
    private const double Step = 0.005;

    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model, GovernorData? governor = null)
    {
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
            data.Add(new SubtransientInit.MachineData(
                H: gen.H, Damping: 0.0,
                Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05, Governor: governor));
        return data;
    }

    private static (GridModel Model, PowerFlowResult Pf) Case()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf);
    }

    /// <summary>The index of the in-service branch joining buses a and b (either orientation).</summary>
    private static int BranchBetween(GridModel m, int a, int b)
    {
        for (int i = 0; i < m.Branches.Count; i++)
        {
            Branch br = m.Branches[i];
            if (br.InService && ((br.From == a && br.To == b) || (br.From == b && br.To == a))) return i;
        }
        throw new InvalidOperationException($"case9 has no branch {a}-{b}");
    }

    private static int GeneratorAt(GridModel m, int bus)
    {
        for (int i = 0; i < m.Generators.Count; i++) if (m.Generators[i].Bus == bus) return i;
        throw new InvalidOperationException($"case9 has no generator at bus {bus}");
    }

    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static void AssertIdentical(MultiMachineSubtransient.Result a, MultiMachineSubtransient.Result b)
    {
        Assert.Equal(a.StayedInSynchronism, b.StayedInSynchronism);
        Assert.Equal(a.Machines.Count, b.Machines.Count);
        for (int i = 0; i < a.Machines.Count; i++)
        {
            MultiMachineSubtransient.MachineTrace x = a.Machines[i], y = b.Machines[i];
            Assert.Equal(x.TimeSeconds.Count, y.TimeSeconds.Count);
            for (int k = 0; k < x.TimeSeconds.Count; k++)
            {
                Assert.True(SameBits(x.DeltaRad[k], y.DeltaRad[k]), $"machine {i} angle differs at sample {k}");
                Assert.True(SameBits(x.SpeedPu[k], y.SpeedPu[k]), $"machine {i} speed differs at sample {k}");
                Assert.True(SameBits(x.EqpPu[k], y.EqpPu[k]), $"machine {i} E'q differs at sample {k}");
                Assert.True(SameBits(x.EdpPu[k], y.EdpPu[k]), $"machine {i} E'd differs at sample {k}");
                Assert.True(SameBits(x.EqppPu[k], y.EqppPu[k]), $"machine {i} E''q differs at sample {k}");
                Assert.True(SameBits(x.EddpPu[k], y.EddpPu[k]), $"machine {i} E''d differs at sample {k}");
            }
        }
    }

    /// <summary>The inertia-weighted mean speed (pu) of the given machines at the last sample.</summary>
    private static double CoiSpeedAtEnd(MultiMachineSubtransient.Result r, GridModel m, IEnumerable<int> machines)
    {
        double num = 0.0, den = 0.0;
        foreach (int i in machines)
        {
            double h = m.Generators[i].H * m.Generators[i].MBase;
            IReadOnlyList<double> w = r.Machines[i].SpeedPu;
            num += h * w[w.Count - 1];
            den += h;
        }
        return num / den;
    }

    private static MultiMachineSubtransient.Result Run(GridModel m, PowerFlowResult pf, IReadOnlyList<RmsEvent> events,
        double duration = 2.0, GovernorData? governor = null, IReadOnlyList<RmsExciter?>? exciters = null)
        => RmsTransientStability.StudyEvents(m, pf, GenrouData(m, governor), events, stepSeconds: Step,
            durationSeconds: duration, exciters: exciters);

    // ---- A fault and its clearance are the single-fault study, bit for bit ----

    [Theory]
    [InlineData(7, 0.1, 0.08)]
    [InlineData(5, 0.1, 0.05)]
    [InlineData(9, 0.2, 0.1)]
    public void A_fault_and_its_clearance_reproduce_the_single_fault_study_bit_for_bit(int bus, double onset, double clearing)
    {
        // Same triplets in the same order (base stamps, then the fault shunt) and the same stage times, so the same bits.
        (GridModel m, PowerFlowResult pf) = Case();
        MultiMachineSubtransient.Result study = RmsTransientStability.Study(m, pf, GenrouData(m), bus, onset, clearing,
            stepSeconds: Step, durationSeconds: 2.0);
        MultiMachineSubtransient.Result events = Run(m, pf, new RmsEvent[]
        {
            new RmsBusFault(onset, bus),
            new RmsFaultClear(onset + Math.Max(0.0, clearing), bus),
        });
        AssertIdentical(study, events);
    }

    [Fact]
    public void The_same_events_give_the_same_bits_twice()
    {
        (GridModel m, PowerFlowResult pf) = Case();
        RmsEvent[] ev =
        {
            new RmsLoadStep(0.1, 5, 30.0, 10.0),
            new RmsBranchTrip(0.3, BranchBetween(m, 4, 5)),
            new RmsGeneratorTrip(0.5, GeneratorAt(m, 3)),
        };
        AssertIdentical(Run(m, pf, ev), Run(m, pf, ev));
    }

    [Fact]
    public void Before_the_first_event_every_run_is_the_same_bits()
    {
        (GridModel m, PowerFlowResult pf) = Case();
        MultiMachineSubtransient.Result a = Run(m, pf, new RmsEvent[] { new RmsLoadStep(0.5, 5, 40.0, 0.0) });
        MultiMachineSubtransient.Result b = Run(m, pf, new RmsEvent[] { new RmsBranchTrip(0.5, BranchBetween(m, 4, 5)) });
        int before = (int)(0.5 / Step) - 1;   // samples strictly before the event step
        for (int i = 0; i < a.Machines.Count; i++)
            for (int k = 0; k <= before; k++)
                Assert.True(SameBits(a.Machines[i].DeltaRad[k], b.Machines[i].DeltaRad[k]), $"machine {i} sample {k}");
    }

    // ---- Branch trip ----

    [Fact]
    public void A_branch_trip_moves_the_rotors_and_the_fleet_stays_in_step()
    {
        (GridModel m, PowerFlowResult pf) = Case();
        MultiMachineSubtransient.Result trip = Run(m, pf, new RmsEvent[] { new RmsBranchTrip(0.2, BranchBetween(m, 4, 5)) });
        Assert.True(trip.StayedInSynchronism);
        // Generator 1's angle relative to generator 3 swings once the ring is opened (the power reroutes): the largest
        // excursion from its pre-trip value after the trip is well above rounding.
        int g1 = GeneratorAt(m, 1), g3 = GeneratorAt(m, 3);
        double rel0 = trip.Machines[g1].DeltaRad[0] - trip.Machines[g3].DeltaRad[0];
        double swing = 0.0;
        for (int k = (int)(0.2 / Step) + 2; k < trip.Machines[g1].DeltaRad.Count; k++)
            swing = Math.Max(swing, Math.Abs(trip.Machines[g1].DeltaRad[k] - trip.Machines[g3].DeltaRad[k] - rel0));
        Assert.True(swing > 1e-3, $"relative angle barely moved: {swing:R} rad");
        foreach (MultiMachineSubtransient.MachineTrace t in trip.Machines)
            Assert.All(t.SpeedPu, w => Assert.True(double.IsFinite(w)));
    }

    // ---- Load step ----

    [Fact]
    public void A_load_step_decelerates_the_fleet_and_a_load_drop_accelerates_it()
    {
        (GridModel m, PowerFlowResult pf) = Case();
        int[] all = Enumerable.Range(0, m.Generators.Count).ToArray();
        MultiMachineSubtransient.Result up = Run(m, pf, new RmsEvent[] { new RmsLoadStep(0.2, 5, 50.0, 0.0) });
        MultiMachineSubtransient.Result down = Run(m, pf, new RmsEvent[] { new RmsLoadStep(0.2, 5, -50.0, 0.0) });
        // No governor, no damping, frequency-independent loads: the centre of inertia keeps falling (rising) while the
        // load exceeds (falls short of) the constant mechanical power. 50 MW on about 33 s of system-base inertia is
        // roughly 0.0076 pu/s; the bound is well inside that.
        Assert.True(CoiSpeedAtEnd(up, m, all) < -1e-3, $"COI speed {CoiSpeedAtEnd(up, m, all):R}");
        Assert.True(CoiSpeedAtEnd(down, m, all) > 1e-3, $"COI speed {CoiSpeedAtEnd(down, m, all):R}");
        Assert.True(up.StayedInSynchronism);
        Assert.True(down.StayedInSynchronism);
    }

    [Fact]
    public void Governors_arrest_the_frequency_fall_of_a_load_step()
    {
        (GridModel m, PowerFlowResult pf) = Case();
        int[] all = Enumerable.Range(0, m.Generators.Count).ToArray();
        RmsEvent[] ev = { new RmsLoadStep(0.2, 5, 50.0, 0.0) };
        MultiMachineSubtransient.Result free = Run(m, pf, ev, duration: 5.0);
        MultiMachineSubtransient.Result governed = Run(m, pf, ev, duration: 5.0,
            governor: new Tgov1Data(0.05, 0.5, 1.0, 5.0));
        Assert.True(CoiSpeedAtEnd(governed, m, all) > CoiSpeedAtEnd(free, m, all),
            $"governed {CoiSpeedAtEnd(governed, m, all):R} vs free {CoiSpeedAtEnd(free, m, all):R}");
    }

    [Fact]
    public void Two_steps_on_one_bus_add()
    {
        // Two steps at the same time on one bus are one accumulated admittance: the same bits as the single step.
        (GridModel m, PowerFlowResult pf) = Case();
        AssertIdentical(
            Run(m, pf, new RmsEvent[] { new RmsLoadStep(0.2, 6, 30.0, 10.0) }),
            Run(m, pf, new RmsEvent[] { new RmsLoadStep(0.2, 6, 20.0, 10.0), new RmsLoadStep(0.2, 6, 10.0, 0.0) }));
    }

    // ---- Generator trip ----

    [Fact]
    public void A_generator_trip_disconnects_the_machine_and_the_rest_decelerate()
    {
        (GridModel m, PowerFlowResult pf) = Case();
        int g3 = GeneratorAt(m, 3);
        MultiMachineSubtransient.Result r = Run(m, pf, new RmsEvent[] { new RmsGeneratorTrip(0.2, g3) });
        int[] rest = Enumerable.Range(0, m.Generators.Count).Where(i => i != g3).ToArray();
        Assert.True(CoiSpeedAtEnd(r, m, rest) < -1e-3, $"remaining COI speed {CoiSpeedAtEnd(r, m, rest):R}");
        Assert.True(r.StayedInSynchronism);   // judged over the machines still connected

        // The tripped rotor: no electrical power, no mechanical power, no damping, so its speed is frozen exactly from
        // the step after the trip on.
        IReadOnlyList<double> w = r.Machines[g3].SpeedPu;
        int from = (int)(0.2 / Step) + 2;
        for (int k = from; k < w.Count; k++)
            Assert.True(SameBits(w[from], w[k]), $"tripped machine speed moved at sample {k}: {w[k]:R} vs {w[from]:R}");
    }

    [Fact]
    public void A_generator_trip_works_with_exciters_on_every_machine()
    {
        (GridModel m, PowerFlowResult pf) = Case();
        var exciters = new List<RmsExciter?>();
        for (int i = 0; i < m.Generators.Count; i++)
            exciters.Add(RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0));
        MultiMachineSubtransient.Result r = Run(m, pf,
            new RmsEvent[] { new RmsGeneratorTrip(0.2, GeneratorAt(m, 3)), new RmsBranchTrip(0.4, BranchBetween(m, 4, 5)) },
            exciters: exciters);
        foreach (MultiMachineSubtransient.MachineTrace t in r.Machines)
            Assert.All(t.EqpPu, e => Assert.True(double.IsFinite(e)));
    }

    // ---- Fail loud ----

    public static TheoryData<string> BadEventNames => new()
    {
        "empty", "null-entry", "at-zero", "negative", "nan-time", "after-end", "unknown-bus-fault", "unknown-bus-load",
        "branch-out-of-range", "branch-twice", "clear-without-fault", "clear-before-fault", "fault-twice",
        "zero-load-step", "nan-load-step", "generator-out-of-range", "generator-twice", "trip-every-machine",
    };

    private static RmsEvent[] BadEvents(string name, GridModel m) => name switch
    {
        "empty" => Array.Empty<RmsEvent>(),
        "null-entry" => new RmsEvent[] { new RmsBusFault(0.1, 7), null! },
        "at-zero" => new RmsEvent[] { new RmsBusFault(0.0, 7) },
        "negative" => new RmsEvent[] { new RmsLoadStep(-0.1, 5, 10.0, 0.0) },
        "nan-time" => new RmsEvent[] { new RmsBranchTrip(double.NaN, 0) },
        "after-end" => new RmsEvent[] { new RmsBranchTrip(2.5, 0) },
        "unknown-bus-fault" => new RmsEvent[] { new RmsBusFault(0.1, 999) },
        "unknown-bus-load" => new RmsEvent[] { new RmsLoadStep(0.1, 999, 10.0, 0.0) },
        "branch-out-of-range" => new RmsEvent[] { new RmsBranchTrip(0.1, m.Branches.Count) },
        "branch-twice" => new RmsEvent[] { new RmsBranchTrip(0.1, 1), new RmsBranchTrip(0.3, 1) },
        "clear-without-fault" => new RmsEvent[] { new RmsFaultClear(0.1, 7) },
        "clear-before-fault" => new RmsEvent[] { new RmsFaultClear(0.2, 7), new RmsBusFault(0.2, 7) },
        "fault-twice" => new RmsEvent[] { new RmsBusFault(0.1, 7), new RmsBusFault(0.15, 7) },
        "zero-load-step" => new RmsEvent[] { new RmsLoadStep(0.1, 5, 0.0, 0.0) },
        "nan-load-step" => new RmsEvent[] { new RmsLoadStep(0.1, 5, double.NaN, 0.0) },
        "generator-out-of-range" => new RmsEvent[] { new RmsGeneratorTrip(0.1, m.Generators.Count) },
        "generator-twice" => new RmsEvent[] { new RmsGeneratorTrip(0.1, 0), new RmsGeneratorTrip(0.2, 0) },
        "trip-every-machine" => new RmsEvent[]
            { new RmsGeneratorTrip(0.1, 0), new RmsGeneratorTrip(0.2, 1), new RmsGeneratorTrip(0.3, 2) },
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(BadEventNames))]
    public void Bad_event_inputs_are_refused_by_name(string name)
    {
        (GridModel m, PowerFlowResult pf) = Case();
        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() => Run(m, pf, BadEvents(name, m)));
        Assert.Equal("events", ex.ParamName);
    }

    [Fact]
    public void A_null_event_list_and_bad_run_settings_are_refused()
    {
        (GridModel m, PowerFlowResult pf) = Case();
        IReadOnlyList<SubtransientInit.MachineData> d = GenrouData(m);
        RmsEvent[] ok = { new RmsLoadStep(0.1, 5, 10.0, 0.0) };
        Assert.Throws<ArgumentNullException>(() => RmsTransientStability.StudyEvents(m, pf, d, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => RmsTransientStability.StudyEvents(m, pf, d, ok, stepSeconds: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RmsTransientStability.StudyEvents(m, pf, d, ok, stepSeconds: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => RmsTransientStability.StudyEvents(m, pf, d, ok, durationSeconds: -1.0));
    }

    [Fact]
    public void The_kernel_refuses_malformed_machine_trips()
    {
        // The kernel-level guard behind the generator trip: out of range, negative time, twice, every machine.
        (GridModel m, PowerFlowResult pf) = Case();
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(m, pf, GenrouData(m));
        MultiMachineSubtransient.Result Sim(IReadOnlyList<RmsMachineTrip> trips) => MultiMachineSubtransient.Simulate(
            p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.Network!,
            60.0, Step, 0.1, machineTrips: trips);
        Assert.Throws<ArgumentOutOfRangeException>(() => Sim(new[] { new RmsMachineTrip(3, 0.05) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sim(new[] { new RmsMachineTrip(0, -0.05) }));
        Assert.Throws<ArgumentException>(() => Sim(new[] { new RmsMachineTrip(0, 0.05), new RmsMachineTrip(0, 0.06) }));
        Assert.Throws<ArgumentException>(() => Sim(new[]
            { new RmsMachineTrip(0, 0.05), new RmsMachineTrip(1, 0.05), new RmsMachineTrip(2, 0.05) }));
    }
}
