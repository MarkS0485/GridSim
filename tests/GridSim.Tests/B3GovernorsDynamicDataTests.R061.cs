using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R061 (governor part): turbine-governors wired into the RMS transient-stability study through
/// <see cref="SubtransientInit.MachineData.Governor"/>. Pinned here:
///   - the WIRING against an independent hand-built hook on the kernel (same governors, same base conversion), bit for bit;
///   - an undisturbed governed fleet is stationary;
///   - ACTION: after a cleared fault an ungoverned fleet keeps the speed offset it gained (constant Pm, no frequency
///     dependent load, D = 0), while governors pull the centre-of-inertia speed back toward rated (primary response);
///   - CCT trials do not leak governor state (two searches give the same bracket);
///   - FAIL LOUD: a governor on a converter, or an operating point outside the governor's valve limits.
/// </summary>
public sealed partial class B3GovernorsDynamicDataTests
{
    private static (GridModel Model, PowerFlowResult Pf) R061Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf);
    }

    private static List<SubtransientInit.MachineData> R061Data(GridModel model, Func<int, GovernorData?> governor)
    {
        var data = new List<SubtransientInit.MachineData>();
        for (int k = 0; k < model.Generators.Count; k++)
            data.Add(new SubtransientInit.MachineData(
                H: model.Generators[k].H, Damping: 0.0,
                Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05, Governor: governor(k)));
        return data;
    }

    private static Tgov1Data R061Tgov1() => new(DroopPu: 0.05, ValveTimeSeconds: 0.5, ReheatLeadSeconds: 1.0,
        ReheatLagSeconds: 5.0, ValveMinPu: 0.0, ValveMaxPu: 3.0);

    private static double CoiSpeed(MultiMachineSubtransient.Result r, IReadOnlyList<double> h, int k)
    {
        double hs = 0.0, hw = 0.0;
        for (int i = 0; i < r.Machines.Count; i++) { hs += h[i]; hw += h[i] * r.Machines[i].SpeedPu[k]; }
        return hw / hs;
    }

    [Fact]
    public void R061_study_governors_are_exactly_the_hand_built_kernel_hook()
    {
        (GridModel model, PowerFlowResult pf) = R061Case9();
        List<SubtransientInit.MachineData> data = R061Data(model, _ => R061Tgov1());
        const double onset = 0.1, clear = 0.08, dt = 0.005, duration = 3.0;
        MultiMachineSubtransient.Result study = RmsTransientStability.Study(model, pf, data, 7, onset, clear,
            stepSeconds: dt, durationSeconds: duration);

        // The oracle: the same prepared case on the kernel, with a hook written out here from the published rule
        // Pm(t) = Pm0 + (Pgov(t) - Pgov0) * MBase / BaseMva and fresh TGOV1 instances.
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, pf, data, 7);
        int n = p.Machines.Count;
        var govs = new Tgov1[n];
        var pgov0 = new double[n];
        var toSys = new double[n];
        for (int i = 0; i < n; i++)
        {
            toSys[i] = model.Generators[i].MBase / model.BaseMva;
            pgov0[i] = p.Pm[i] / toSys[i];
            govs[i] = new Tgov1(0.05, 0.5, 1.0, 5.0, 0.0, 0.0, 3.0);
            govs[i].Initialize(pgov0[i]);
        }
        var stages = new List<RmsNetworkStage> { new(onset, p.NetworkFaulted!), new(onset + clear, p.Network!) };
        MultiMachineSubtransient.Result oracle = MultiMachineSubtransient.Simulate(
            p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.Network!,
            model.FrequencyHz, dt, duration, stages, RmsTransientStability.DefaultSlipLimitRad,
            mechanicalPower: (i, w) => p.Pm[i] + (govs[i].Step(w, dt) - pgov0[i]) * toSys[i],
            converters: p.Converters, synchronismMachines: n);

        Assert.Equal(oracle.StayedInSynchronism, study.StayedInSynchronism);
        for (int i = 0; i < n; i++)
            for (int k = 0; k < oracle.Machines[i].SpeedPu.Count; k++)
            {
                AssertBits(oracle.Machines[i].SpeedPu[k], study.Machines[i].SpeedPu[k], $"machine {i} speed {k}");
                AssertBits(oracle.Machines[i].DeltaRad[k], study.Machines[i].DeltaRad[k], $"machine {i} angle {k}");
            }
    }

    [Fact]
    public void R061_an_undisturbed_governed_fleet_is_stationary()
    {
        (GridModel model, PowerFlowResult pf) = R061Case9();
        List<SubtransientInit.MachineData> data = R061Data(model, k => k switch
        {
            0 => new HydroGovernorData(0.04, 0.4, 5.0, 0.2, 2.0, GateMinPu: 0.0, GateMaxPu: 3.0),
            1 => new Ieeeg1Data(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, K1: 0.3, K3: 0.4, K5: 0.3, ValveMaxPu: 3.0,
                ValveOpeningRatePuPerS: 0.1, ValveClosingRatePuPerS: -1.0),
            _ => R061Tgov1(),
        });
        // Fault scheduled after the run ends: the network never changes.
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 100.0,
            clearingSeconds: 0.1, stepSeconds: 0.005, durationSeconds: 5.0);
        Assert.True(r.StayedInSynchronism);
        foreach (MultiMachineSubtransient.MachineTrace m in r.Machines)
        {
            int last = m.SpeedPu.Count - 1;
            Assert.True(Math.Abs(m.SpeedPu[last]) < 1e-9, $"speed drifted to {m.SpeedPu[last]:R}");
            Assert.True(Math.Abs(m.DeltaRad[last] - m.DeltaRad[0]) < 1e-6, $"angle drifted {m.DeltaRad[last] - m.DeltaRad[0]:R}");
        }
    }

    [Fact]
    public void R061_governors_pull_the_post_fault_speed_offset_back_toward_rated()
    {
        (GridModel model, PowerFlowResult pf) = R061Case9();
        List<SubtransientInit.MachineData> plain = R061Data(model, _ => null);
        List<SubtransientInit.MachineData> governed = R061Data(model, _ => R061Tgov1());
        MultiMachineSubtransient.Result a = RmsTransientStability.Study(model, pf, plain, 7, 0.1, 0.08,
            stepSeconds: 0.005, durationSeconds: 20.0);
        MultiMachineSubtransient.Result b = RmsTransientStability.Study(model, pf, governed, 7, 0.1, 0.08,
            stepSeconds: 0.005, durationSeconds: 20.0);
        Assert.True(a.StayedInSynchronism && b.StayedInSynchronism);
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, pf, plain, 7);
        int last = a.Machines[0].SpeedPu.Count - 1;
        double wPlain = CoiSpeed(a, p.H, last), wGov = CoiSpeed(b, p.H, last);
        Assert.True(Math.Abs(wPlain) > 1e-5, $"the ungoverned fleet should keep a speed offset, got {wPlain:R}");
        Assert.True(Math.Abs(wGov) < 0.5 * Math.Abs(wPlain), $"governed COI speed {wGov:R} vs ungoverned {wPlain:R}");
        // Before the fault the two runs are the same numbers (the governors sit at equilibrium).
        int pre = 10;
        for (int i = 0; i < a.Machines.Count; i++)
            Assert.True(Math.Abs(a.Machines[i].SpeedPu[pre] - b.Machines[i].SpeedPu[pre]) < 1e-12);
    }

    [Fact]
    public void R061_a_governed_cct_search_is_repeatable_trial_after_trial()
    {
        (GridModel model, PowerFlowResult pf) = R061Case9();
        List<SubtransientInit.MachineData> data = R061Data(model, _ => R061Tgov1());
        CctBracket first = RmsTransientStability.CriticalClearingBracket(model, pf, data, 7, maxSeconds: 0.4);
        CctBracket second = RmsTransientStability.CriticalClearingBracket(model, pf, data, 7, maxSeconds: 0.4);
        Assert.Equal(first, second);
    }

    [Fact]
    public void R061_governor_data_that_cannot_be_used_is_refused()
    {
        (GridModel model, PowerFlowResult pf) = R061Case9();
        // Generator 1 (bus 2) carries 163 MW on its 192 MVA base, about 0.85 pu: a valve ceiling of 0.5 pu cannot hold it.
        List<SubtransientInit.MachineData> tooSmall = R061Data(model, k => k == 1
            ? new Tgov1Data(0.05, 0.5, 1.0, 5.0, ValveMaxPu: 0.5) : null);
        var ex = Assert.Throws<InvalidOperationException>(() => RmsTransientStability.Study(model, pf, tooSmall, 7, 0.1, 0.08,
            durationSeconds: 1.0));
        Assert.Contains("TGOV1", ex.Message);

        // A governor on converter plant has no rotor to govern.
        GridModel withPv = model.CopyWith(generators: model.Generators.Select((g, i) => i == 2 ? g with { Type = "solar" } : g).ToList());
        PowerFlowResult pf2 = new NewtonRaphsonPowerFlow().Solve(withPv);
        Assert.True(pf2.Converged);
        List<SubtransientInit.MachineData> onConverter = R061Data(withPv, k => k == 2 ? R061Tgov1() : null);
        Assert.Throws<ArgumentException>(() => RmsTransientStability.Study(withPv, pf2, onConverter, 7, 0.1, 0.08,
            durationSeconds: 1.0));

        // Bad IEEEG1 fractions fail at the study's mapping (before any integration).
        List<SubtransientInit.MachineData> badFractions = R061Data(model, k => k == 0
            ? new Ieeeg1Data(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, K1: 0.5) : null);
        Assert.Throws<ArgumentException>(() => RmsTransientStability.Study(model, pf, badFractions, 7, 0.1, 0.08,
            durationSeconds: 1.0));
    }
}
