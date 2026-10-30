using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// RMS transient-stability study on the salient/subtransient multi-machine model (backlog P1-2) - the
/// production step above the classical critical-clearing-time screen (<see cref="Distributed.ClassicalInit"/>).
/// It seeds the fleet from a solved power flow with <see cref="SubtransientInit"/>, applies a bolted
/// three-phase bus fault over a window through the integrator's network schedule (faulted reduced admittance
/// during the fault, base network on clearing), and integrates the full six-state machines with
/// <see cref="MultiMachineSubtransient"/>.
///
/// <para>This is the RMS engine the standard-model library was built for: real cases, the standard machine
/// models, and a scheduled disturbance, with per-machine angle/speed/flux output and a synchronism verdict.
/// By default the field is held (the classic first-swing assumption). Optional per-machine exciters (R061:
/// <see cref="RmsExciter"/>, SEXS, IEEET1, EXAC1, ESAC1A or ESST4B, each optionally with a PSS1A) can be passed to <see cref="Study"/>
/// and <see cref="CriticalClearingTimeSeconds"/>; their voltage references are set from the initial terminal
/// voltages so the undisturbed fleet stays at equilibrium. With no exciters passed, every number is bit-for-bit
/// the held-field study. Governors (R061, governor part, October 2026 stream B3) come with the machine data:
/// <see cref="SubtransientInit.MachineData.Governor"/> (TGOV1, IEEEG1 or the hydro governor, <see cref="GovernorData"/>)
/// drives that machine's mechanical power from its own rotor speed each step, initialised at the solved operating point;
/// with no governor in the data every number is bit-for-bit the constant-Pm study. It reduces to the trusted classical critical-clearing time when the reactances are collapsed to
/// <c>X'd</c>. Converter-interfaced plant enters as REGC_A/REEC-style current sources on retained buses
/// (<see cref="RmsConverterState"/>), not as machines; the result's <c>Converters</c> carries their traces.</para>
/// </summary>
public static partial class RmsTransientStability
{
    // ---- Named defaults (October 2026 review, WS13 R168). Values unchanged.

    /// <summary>Default integration step (s) of a single fault-and-clear <see cref="Study"/>.</summary>
    public const double StudyDefaultStepSeconds = 0.005;

    /// <summary>Default integration step (s) of the <see cref="CriticalClearingTimeSeconds"/> bisection.
    /// <b>Differs from <see cref="StudyDefaultStepSeconds"/></b> (R168, left for Mark): a CCT found at the
    /// 2 ms step and a single study re-run at that clearing time on the 5 ms default step can disagree right at
    /// the stability boundary. Unifying them would move pinned CCTs, so both values are kept and named.</summary>
    public const double CctDefaultStepSeconds = 0.002;

    /// <summary>Default simulated length (s) of a single <see cref="Study"/>.</summary>
    public const double StudyDefaultDurationSeconds = 10.0;

    /// <summary>Each CCT trial runs until this long (s) after the fault clears: long enough for a first-swing
    /// loss of synchronism to show as a slip past the limit.</summary>
    public const double PostClearWindowSeconds = 5.0;

    /// <summary>Default upper bound (s) of the CCT bisection.</summary>
    public const double CctDefaultMaxSeconds = 1.0;

    /// <summary>Default CAP on the number of CCT bisection halvings. Since R106 (Q15) the bisection runs over whole
    /// integration steps and stops as soon as the stable and unstable clearing steps are adjacent, so the cap is
    /// only a safety limit: the default window of 500 steps (1 s at 2 ms) reaches step resolution in 9 halvings,
    /// and any window up to 2^18 = 262,144 steps does so before the cap.</summary>
    public const int CctDefaultBisectionIterations = 18;

    /// <summary>Default fault onset (s) of the CCT trials, leaving a short pre-fault steady stretch.</summary>
    public const double CctDefaultOnsetSeconds = 0.1;

    /// <summary>Default rotor-angle slip (rad) beyond which a machine counts as out of step: one full pole slip.</summary>
    public const double DefaultSlipLimitRad = 2.0 * Math.PI;

    /// <summary>Run a fault-and-clear study: a bolted three-phase fault at <paramref name="faultBusId"/> from
    /// <paramref name="onsetSeconds"/>, cleared after <paramref name="clearingSeconds"/>. Returns the
    /// per-machine trajectories and whether the fleet stayed in synchronism.
    ///
    /// <para><paramref name="exciters"/> (optional, R061) aligns to <c>model.Generators</c> like
    /// <paramref name="data"/>: one <see cref="RmsExciter"/> per synchronous machine, or null to hold that
    /// machine's field. Entries for units that are not integrated as machines (converter plant, power-flow-only
    /// or zero-inertia held units) must be null. Each exciter instance (and each PSS inside one) must be used by
    /// one machine only. With exciters the fault onset must be after t = 0, so the voltage references are measured
    /// on the undisturbed network. Null (the default) runs the held-field study unchanged.</para>
    ///
    /// <para><paramref name="f0"/> (R238): the nominal frequency the swing is integrated at; null (the default) takes the
    /// model's own <see cref="GridModel.FrequencyHz"/> (see <see cref="ResolveNominalHz"/>).</para>
    ///
    /// <para>N271 (October 2026): the network is always the sparse full network, whatever the case size (see
    /// <see cref="SubtransientInit"/>); the old <c>sparseNetwork</c> argument, which let a caller pick the dense Kron path,
    /// is gone.</para></summary>
    public static MultiMachineSubtransient.Result Study(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        int faultBusId, double onsetSeconds, double clearingSeconds,
        double? f0 = null, double stepSeconds = StudyDefaultStepSeconds,
        double durationSeconds = StudyDefaultDurationSeconds,
        double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null)
        => StudyWithFleet(model, result, data, faultBusId, onsetSeconds, clearingSeconds, f0, stepSeconds,
            durationSeconds, slipLimitRad, exciters).Result;

    /// <summary>
    /// N246 rest (CF2, October 2026): a <see cref="Study"/> run together with what its synchronism test was judged on,
    /// so a caller can score law 9 from the same trajectory on the same machine set
    /// (<see cref="Physics.SynchronismFromTrajectory.Subtransient"/>).
    /// </summary>
    /// <param name="Result">The run, exactly as <see cref="Study"/> returns it.</param>
    /// <param name="H">The system-base inertia per integrated machine, in trace order (the vector the integrator used).</param>
    /// <param name="SynchronismMachines">How many leading traces the integrator's synchronism test covers (island
    /// reference machines, after them, are excluded).</param>
    /// <param name="NominalHz">The nominal frequency the swing was integrated at.</param>
    public sealed record FleetStudy(
        MultiMachineSubtransient.Result Result, IReadOnlyList<double> H, int SynchronismMachines, double NominalHz);

    /// <summary>
    /// <see cref="Study"/> with the fleet facts law 9 needs (see <see cref="FleetStudy"/>). <see cref="Study"/> is this
    /// method's <c>Result</c>: the body moved here unchanged (orchestration only), so every number is the same bits.
    /// </summary>
    public static FleetStudy StudyWithFleet(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        int faultBusId, double onsetSeconds, double clearingSeconds,
        double? f0 = null, double stepSeconds = StudyDefaultStepSeconds,
        double durationSeconds = StudyDefaultDurationSeconds,
        double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null)
    {
        double nominalHz = ResolveNominalHz(model, f0);
        result.RequireConverged("RMS transient stability");   // the convergence contract (October 2026, WS4)
        // The event times are refused, not clamped (as StudyEvents does): a NaN onset never applied the fault, a NaN
        // clearing time never cleared it, and a negative clearing time was silently turned into "clear at onset".
        if (!(onsetSeconds >= 0.0) || !double.IsFinite(onsetSeconds))
            throw new ArgumentOutOfRangeException(nameof(onsetSeconds), onsetSeconds, "The fault onset must be a finite number of seconds, 0 or more.");
        if (!(clearingSeconds >= 0.0) || !double.IsFinite(clearingSeconds))
            throw new ArgumentOutOfRangeException(nameof(clearingSeconds), clearingSeconds, "The fault clearing time must be a finite number of seconds, 0 or more.");
        if (!(nominalHz > 0.0) || !double.IsFinite(nominalHz))
            throw new ArgumentOutOfRangeException(nameof(f0), nominalHz, "The nominal frequency must be a finite number of Hz above 0.");
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, result, data, faultBusId);
        IRmsFieldController?[]? controllers = MapExcitersToFleet(model, data, p, exciters);
        GovernorPlan? governors = MapGovernorsToFleet(model, data, p);   // R061: null when no machine has a governor
        MultiMachineSubtransient.Result run =
            Run(p, onsetSeconds, clearingSeconds, nominalHz, stepSeconds, durationSeconds, slipLimitRad, controllers, governors);
        // The same synchronism count Run hands the integrator (both branches): machines less island references.
        return new FleetStudy(run, p.H, p.Machines.Count - (p.IslandReferences?.Count ?? 0), nominalHz);
    }

    /// <summary>
    /// R061 (governor part; October 2026 review, stream B3; Ruling R1: Mark delegated EE calls 2026-10-04): the
    /// governors of one study, by machine slot of the prepared fleet. <see cref="BySlot"/> holds each machine's governor
    /// data (null: constant mechanical power); <see cref="MachineToSystem"/> is MBase / BaseMva per slot, the factor that
    /// turns a governor's machine-base power into the system base the swing integrates on.
    /// </summary>
    private sealed record GovernorPlan(GovernorData?[] BySlot, double[] MachineToSystem);

    /// <summary>
    /// R061 (governor part): map <see cref="SubtransientInit.MachineData.Governor"/> onto the integrated machine fleet of
    /// <paramref name="p"/>. Returns null when no entry of <paramref name="data"/> carries a governor, so the study takes
    /// exactly its historical path (no mechanical-power hook at all). The classification is the one
    /// <see cref="MapExcitersToFleet"/> uses (and <see cref="SubtransientInit.FromSolvedCase"/>), cross-checked against
    /// the prepared fleet; a governor on a unit that is not integrated as a synchronous machine (converter plant, a
    /// power-flow-only unit, a zero-inertia held unit) is refused. Every governor is built and initialised once here, at
    /// its machine's initial mechanical power, so bad data or an initial power outside the governor's limits (R252)
    /// fails before any integration.
    /// </summary>
    private static GovernorPlan? MapGovernorsToFleet(
        GridModel model, IReadOnlyList<SubtransientInit.MachineData> data, SubtransientInit.Prepared p)
    {
        bool any = false;
        for (int k = 0; k < data.Count && !any; k++) any = data[k].Governor is not null;
        if (!any) return null;   // the historical constant-Pm study, untouched

        var bySlot = new GovernorData?[p.Machines.Count];
        var toSystem = new double[p.Machines.Count];
        for (int s = 0; s < toSystem.Length; s++) toSystem[s] = 1.0;   // island references: never governed
        int slot = 0;
        for (int k = 0; k < model.Generators.Count; k++)
        {
            Generator gen = model.Generators[k];
            GovernorData? gd = data[k].Governor;
            bool conv = SubtransientInit.IsConverterUnit(gen, data[k]);   // WECC: a unit with a WECC chain is converter plant
            bool held = gen.PowerFlowOnly || (!conv && !(data[k].H > 0.0));
            bool isMachine = !held && !conv;
            if (!isMachine)
            {
                if (gd is not null)
                    throw new ArgumentException($"data[{k}].Governor is set, but generator {k} at bus {gen.Bus} is not integrated "
                        + "as a synchronous machine (converter plant or a held unit), so it has no rotor speed to govern; its "
                        + "governor must be null.", nameof(data));
                continue;
            }
            if (slot >= bySlot.Length || p.GeneratorBuses[slot] != gen.Bus)
                throw new InvalidOperationException($"Governor mapping out of step with the prepared fleet at generator {k} "
                    + $"(bus {gen.Bus}); the machine classification here no longer matches SubtransientInit.");
            double mBase = MachineBase.EffectiveMva(gen, model.BaseMva);   // D-DOC-055: the one machine-base rule
            toSystem[slot] = mBase / model.BaseMva;
            if (gd is not null)
            {
                // Fail loud now, not mid-run: the constructor checks the data, Initialize checks the start (R252).
                IRmsGovernor probe = gd.Create();
                try { probe.Initialize(p.Pm[slot] / toSystem[slot]); }
                catch (ArgumentOutOfRangeException ex)
                {
                    throw new InvalidOperationException($"Generator {k} at bus {gen.Bus} ({gen.Name ?? "unnamed"}): its {gd.Model} "
                        + $"governor cannot start at the solved operating point ({ControlBlocks.Num(p.Pm[slot] / toSystem[slot])} pu "
                        + $"on its {ControlBlocks.Num(mBase)} MVA base): {ex.Message}", ex);
                }
            }
            bySlot[slot++] = gd;
        }
        int islandReferences = p.IslandReferences?.Count ?? 0;
        if (slot + islandReferences != p.Machines.Count)
            throw new InvalidOperationException($"Governor mapping found {slot} machines plus {islandReferences} island "
                + $"references, but the prepared fleet has {p.Machines.Count}; the classification no longer matches SubtransientInit.");
        return new GovernorPlan(bySlot, toSystem);
    }

    /// <summary>
    /// R061 (governor part): the per-step mechanical power hook of one run, with FRESH governors (a CCT search runs many
    /// trials; none may inherit another's state). Each governed machine's governor is initialised at its machine-base
    /// initial power <c>Pm0 / (MBase / BaseMva)</c>, and its output is applied as a CHANGE on the system base:
    /// <c>Pm(t) = Pm0 + (Pgov(t) - Pgov0) * MBase / BaseMva</c>. Algebraically that is <c>Pgov(t) * MBase / BaseMva</c>;
    /// written as a change it returns exactly the solved Pm0 whenever the governor returns exactly its initial value, so
    /// the base-conversion round trip cannot inject a spurious last-bit imbalance at t = 0. Ungoverned machines (and
    /// island references) keep exactly Pm0.
    /// </summary>
    private static Func<int, double, double> GovernorHook(SubtransientInit.Prepared p, GovernorPlan plan, double stepSeconds)
    {
        int n = p.Machines.Count;
        var govs = new IRmsGovernor?[n];
        var pgov0 = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (plan.BySlot[i] is not { } gd) continue;
            pgov0[i] = p.Pm[i] / plan.MachineToSystem[i];
            IRmsGovernor g = gd.Create();
            g.Initialize(pgov0[i]);
            govs[i] = g;
        }
        double[] pm = p.Pm;
        double[] toSystem = plan.MachineToSystem;
        return (i, speedDeviationPu) => govs[i] is { } g
            ? pm[i] + (g.Step(speedDeviationPu, stepSeconds) - pgov0[i]) * toSystem[i]
            : pm[i];
    }

    /// <summary>
    /// R238 (D2 nominal frequency, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the nominal frequency an
    /// RMS study integrates at. The synchronous speed ws = 2 * pi * f0 scales every rotor-angle rate, so a study of a
    /// 60 Hz system (the WSCC 9-bus, N017) run at 50 Hz swings 20 % too slowly and reports the wrong clearing time. The
    /// default used to be the GB constant <see cref="FrequencyLimits.NominalHz"/> whatever the model said, and no CLI path
    /// passed one; it is now the model's own <see cref="GridModel.FrequencyHz"/> unless the caller gives a value. For every
    /// 50 Hz model the resolved value is the same 50.0 as before, so those studies are bit-identical. Fail loud: a given
    /// or resolved frequency that is not finite and above 0 Hz throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    internal static double ResolveNominalHz(GridModel model, double? f0)
    {
        ArgumentNullException.ThrowIfNull(model);
        double nominalHz = f0 ?? model.FrequencyHz;
        if (!(nominalHz > 0.0) || !double.IsFinite(nominalHz))
            throw new ArgumentOutOfRangeException(nameof(f0), $"The RMS study's nominal frequency must be positive and finite (got {nominalHz} Hz"
                + (f0 is null ? ", the model's FrequencyHz)." : ")."));
        return nominalHz;
    }

    /// <summary>
    /// R061: re-index the caller's per-generator exciter list onto the integrated machine fleet of
    /// <paramref name="p"/>. Returns null when <paramref name="exciters"/> is null, so the held-field path is
    /// reached with exactly the arguments it always had.
    ///
    /// <para>The fleet is the synchronous machines in <c>model.Generators</c> order, followed by any synthetic
    /// island references (which never get an exciter). The classification below mirrors
    /// <see cref="SubtransientInit.FromSolvedCase"/> with its default <c>convertersAsCurrentSources: true</c>, and
    /// is cross-checked against the prepared fleet (machine count and terminal bus per slot), so if the two ever
    /// drift apart this fails loud instead of attaching an exciter to the wrong machine.</para>
    /// </summary>
    private static IRmsFieldController?[]? MapExcitersToFleet(
        GridModel model, IReadOnlyList<SubtransientInit.MachineData> data, SubtransientInit.Prepared p,
        IReadOnlyList<RmsExciter?>? exciters)
    {
        if (exciters is null) return null;   // the historical held-field study, untouched
        int g = model.Generators.Count;
        if (exciters.Count != g)
            throw new ArgumentException($"exciters must align to model.Generators: expected {g} entries (null to hold "
                + $"a machine's field), got {exciters.Count}.", nameof(exciters));

        var fleet = new IRmsFieldController?[p.Machines.Count];
        var seenExciters = new HashSet<RmsExciter>(ReferenceEqualityComparer.Instance);
        var seenStabilisers = new HashSet<Pss1a>(ReferenceEqualityComparer.Instance);
        int slot = 0;   // next machine slot in the prepared fleet
        for (int k = 0; k < g; k++)
        {
            Generator gen = model.Generators[k];
            RmsExciter? ex = exciters[k];
            // Same three-way split as SubtransientInit: held (power-flow-only, or a non-converter with no
            // inertia), converter plant, or an integrated synchronous machine.
            bool conv = SubtransientInit.IsConverterUnit(gen, data[k]);   // WECC: a unit with a WECC chain is converter plant
            bool held = gen.PowerFlowOnly || (!conv && !(data[k].H > 0.0));
            bool isMachine = !held && !conv;
            if (!isMachine)
            {
                if (ex is not null)
                    throw new ArgumentException($"exciters[{k}] is set, but generator {k} at bus {gen.Bus} is not "
                        + "integrated as a synchronous machine (converter plant or a held unit); its entry must be null.",
                        nameof(exciters));
                continue;
            }
            if (slot >= fleet.Length || p.GeneratorBuses[slot] != gen.Bus)
                throw new InvalidOperationException($"Exciter mapping out of step with the prepared fleet at generator "
                    + $"{k} (bus {gen.Bus}); the machine classification here no longer matches SubtransientInit.");
            if (ex is not null)
            {
                // Stateful blocks must not be shared between machines.
                if (!seenExciters.Add(ex))
                    throw new ArgumentException($"exciters[{k}] is the same instance as an earlier entry; each machine "
                        + "needs its own exciter.", nameof(exciters));
                if (ex.Stabiliser is { } pss && !seenStabilisers.Add(pss))
                    throw new ArgumentException($"exciters[{k}] shares its PSS1A instance with an earlier exciter; each "
                        + "machine needs its own stabiliser.", nameof(exciters));
                if (ex.NeedsTerminalQuantities)
                {
                    // ESST4B reads the terminal voltage; with a unit transformer folded into the machine reactances the
                    // voltage the kernel recovers is the HV bus's, not the terminal's, so refuse rather than misread it.
                    if (gen.UnitTransformerXPu > 0)
                        throw new ArgumentException($"exciters[{k}] is {ex.Kind}, which reads the machine terminal voltage, but "
                            + $"generator {k} at bus {gen.Bus} has a unit transformer folded into its reactances, so the "
                            + "terminal voltage is not available.", nameof(exciters));
                    ex.BindMachineBase(MachineBase.EffectiveMva(gen, model.BaseMva) / model.BaseMva);
                }
            }
            fleet[slot++] = ex;
        }
        int islandReferences = p.IslandReferences?.Count ?? 0;
        if (slot + islandReferences != p.Machines.Count)
            throw new InvalidOperationException($"Exciter mapping found {slot} machines plus {islandReferences} island "
                + $"references, but the prepared fleet has {p.Machines.Count}; the classification no longer matches "
                + "SubtransientInit.");
        return fleet;   // island-reference slots stay null: held field
    }

    private static MultiMachineSubtransient.Result Run(
        SubtransientInit.Prepared p, double onsetSeconds, double clearingSeconds,
        double f0, double stepSeconds, double durationSeconds, double slipLimitRad,
        IRmsFieldController?[]? controllers, GovernorPlan? governors)
    {
        // R061 (governor part): fresh governors for this run, or no hook at all (the historical constant-Pm path).
        Func<int, double, double>? mechanicalPower = governors is null ? null : GovernorHook(p, governors, stepSeconds);
        // N271: production always prepares the sparse network, so this branch is the engine. The dense branch below runs
        // only for a case prepared under the test-only SubtransientInit.DenseKronReferenceScope (the oracle).
        if (p.IsSparse)
        {
            if (p.NetworkFaulted is null)
                throw new InvalidOperationException("The prepared case carries no faulted network; pass a faultBusId.");
            var netStages = new List<RmsNetworkStage>
            {
                new(onsetSeconds, p.NetworkFaulted),
                new(onsetSeconds + Math.Max(0.0, clearingSeconds), p.Network!),
            };
            return MultiMachineSubtransient.Simulate(
                p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.Network!,
                f0, stepSeconds, durationSeconds, netStages, slipLimitRad, converters: p.Converters,
                synchronismMachines: p.Machines.Count - (p.IslandReferences?.Count ?? 0),
                fieldControllers: controllers,   // R061: null = held field, the historical path
                mechanicalPower: mechanicalPower);   // R061 governors: null = constant Pm, the historical path
        }
        if (p.YReducedFaulted is null)
            throw new InvalidOperationException("The prepared case carries no faulted admittance; pass a faultBusId.");
        var stages = new List<MultiMachineSubtransient.NetworkStage>
        {
            new(onsetSeconds, p.YReducedFaulted),
            new(onsetSeconds + Math.Max(0.0, clearingSeconds), p.YReduced),
        };
        return MultiMachineSubtransient.Simulate(
            p.Machines, p.H, p.Damping, p.Pm, p.Efd,
            p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.YReduced,
            f0, stepSeconds, durationSeconds, fault: null, slipLimitRad: slipLimitRad,
            exciters: null, voltageReferencePu: null, admittanceStages: stages,
            mechanicalPower: mechanicalPower,   // R061 governors: null = constant Pm, the historical path
            converters: p.Converters, synchronismMachines: p.Machines.Count - (p.IslandReferences?.Count ?? 0),
            fieldControllers: controllers);   // R061: null = held field, the historical path
    }

    /// <summary>
    /// Critical clearing time (seconds) for a bolted three-phase fault at <paramref name="faultBusId"/> on the
    /// subtransient model, quantised to the integration step (R106, ruling Q15). This is
    /// <see cref="CriticalClearingBracket"/>'s <see cref="CctBracket.StableSeconds"/>: the LONGEST clearing time
    /// on the step grid (a whole number of <paramref name="stepSeconds"/> steps) after which the fleet still
    /// holds synchronism. When the search reached step resolution (always, with the default cap and window),
    /// clearing one step later loses synchronism. Returns 0 if even an instantaneous fault is unstable, or the
    /// last grid point inside <paramref name="maxSeconds"/> if no clearing time in the window destabilises it.
    ///
    /// <para>Why step-quantised: the fault is switched on and off only at whole integration steps (a network
    /// stage takes effect at the first step whose time has reached it), so every clearing time inside one step
    /// gives exactly the same simulation. The old 18-halving bisection over continuous time reported the
    /// midpoint of a 3.8 microsecond bracket inside one 2 ms step: digits below the step carried no
    /// information. Feeding the returned value back into <see cref="Study"/> with the same step, onset and
    /// slip limit reproduces the stable verdict exactly.</para>
    ///
    /// <para><paramref name="exciters"/> (optional, R061) follows the same rules as in <see cref="Study"/>. The
    /// same exciter instances are reused by every trial: each run re-initialises every state, so trials do not
    /// leak into one another.</para>
    /// </summary>
    public static double CriticalClearingTimeSeconds(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data, int faultBusId,
        double? f0 = null, double stepSeconds = CctDefaultStepSeconds,
        double maxSeconds = CctDefaultMaxSeconds, int iterations = CctDefaultBisectionIterations,
        double onsetSeconds = CctDefaultOnsetSeconds, double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null, int scanSteps = CctSearch.DefaultScanSteps)
        => CriticalClearingBracket(model, result, data, faultBusId, f0, stepSeconds, maxSeconds, iterations,
            onsetSeconds, slipLimitRad, exciters, scanSteps).StableSeconds;

    /// <summary>
    /// The step-quantised critical-clearing bracket (R106, ruling Q15). The search runs over whole integration
    /// steps m = 0 .. M, where M is the number of whole steps inside <paramref name="maxSeconds"/>; a trial
    /// with m steps clears the fault at exactly <c>m * stepSeconds</c> after onset, so the fault spans exactly m
    /// steps of the integrator. Integer bisection keeps a stable step count <c>lo</c> and an unstable one
    /// <c>hi</c> and stops as soon as they are adjacent (or when the <paramref name="iterations"/> cap is reached,
    /// which the default cap never is for windows up to 2^18 steps). No floating-point midpoint is ever formed,
    /// so the search visits exactly the same clearing times on every CPU and solver path.
    ///
    /// <para>Edge cases: if the instantaneous clear (m = 0) already loses synchronism the bracket is
    /// [0, 0] steps with <see cref="CctBracket.UnstableAtZero"/> set; if the whole window survives, the stable
    /// end is M and <see cref="CctBracket.UnstableSteps"/> is null (no unstable clearing time was found).</para>
    ///
    /// <para>N048: the reported CCT is the FIRST loss of synchronism from below, not wherever a bisection lands when
    /// stability is not monotone in the clearing time. After the bisection the search verifies the
    /// <paramref name="scanSteps"/> step counts below the stable end (re-searching lower if one of them loses
    /// synchronism) and probes the same number above the unstable end for stable pockets, which it reports in
    /// <see cref="CctBracket.NonMonotone"/> and <see cref="CctBracket.PocketStableSteps"/>. See
    /// <see cref="CctSearch"/> for the guarantee and the cost (at most 2 x <paramref name="scanSteps"/> extra trials
    /// on a monotone case; 0 gives the bisection alone).</para>
    /// </summary>
    public static CctBracket CriticalClearingBracket(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data, int faultBusId,
        double? f0 = null, double stepSeconds = CctDefaultStepSeconds,
        double maxSeconds = CctDefaultMaxSeconds, int iterations = CctDefaultBisectionIterations,
        double onsetSeconds = CctDefaultOnsetSeconds, double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null, int scanSteps = CctSearch.DefaultScanSteps)
    {
        // Pedantic gates: the step grid must exist and hold at least one whole step, or the search is meaningless.
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds))
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), $"The CCT step must be positive and finite (got {stepSeconds}).");
        if (!(maxSeconds > 0.0) || !double.IsFinite(maxSeconds))
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window must be positive and finite (got {maxSeconds}).");
        if (iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(iterations), $"The CCT bisection needs at least one halving (got {iterations}).");
        if (scanSteps < 0)
            throw new ArgumentOutOfRangeException(nameof(scanSteps), $"The CCT scan depth must be 0 or more steps (got {scanSteps}).");

        // M = the number of whole steps inside the window. The same millionth-of-a-step tolerance the integrator
        // applies to stage times (MultiMachineSubtransient.StageTimeToleranceSteps) absorbs the rounding of
        // maxSeconds / stepSeconds (1.0 / 0.002 is not exactly 500 in binary), so a window that is a whole
        // number of steps keeps its last step.
        double stepsInWindow = maxSeconds / stepSeconds + MultiMachineSubtransient.StageTimeToleranceSteps;
        if (stepsInWindow < 1.0)
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window {maxSeconds} s is shorter than "
                + $"one integration step ({stepSeconds} s); there is no clearing time to search.");
        if (stepsInWindow > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window {maxSeconds} s holds more than "
                + $"{int.MaxValue} steps of {stepSeconds} s.");
        int maxSteps = (int)Math.Floor(stepsInWindow);

        // R238: the model's nominal frequency unless the caller overrides it, resolved once for every trial.
        double nominalHz = ResolveNominalHz(model, f0);

        result.RequireConverged("RMS transient stability");   // the convergence contract (October 2026, WS4)
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, result, data, faultBusId);   // N271: sparse
        IRmsFieldController?[]? controllers = MapExcitersToFleet(model, data, p, exciters);
        GovernorPlan? governors = MapGovernorsToFleet(model, data, p);   // R061: null when no machine has a governor

        // One trial: the fault spans exactly `steps` integration steps. The clearing time is formed as
        // steps * stepSeconds, the same product the integrator uses for its own step times (t = k * stepSeconds),
        // and the stage tolerance in the integrator absorbs the one-ulp rounding of onset + that product.
        // R061: each trial builds fresh governors (Run), so no trial inherits another's governor state.
        // Bug hunt 2026-10-29, finding 24: count the trials whose converter iteration hit its cap, so the bracket says
        // when its verdicts rest on unsettled steps (the single Study path prints a WARNING for them; the search
        // used to carry no trace of them). The search memoises, so each step count is counted at most once.
        int unsettledTrials = 0;
        bool Survives(int steps)
        {
            double clear = steps * stepSeconds;
            MultiMachineSubtransient.Result trial = Run(p, onsetSeconds, clear, nominalHz, stepSeconds,
                onsetSeconds + clear + PostClearWindowSeconds, slipLimitRad, controllers, governors);
            if (trial.ConverterIterationFailures > 0) unsettledTrials++;
            return trial.StayedInSynchronism;
        }

        // The shared search (N048): the unchanged bisection, then the downward verification and the upward probe.
        return CctSearch.Run(Survives, maxSteps, stepSeconds, iterations, scanSteps) with { TrialsWithConverterIterationFailures = unsettledTrials };
    }
}

/// <summary>
/// The step-quantised critical-clearing bracket of <see cref="RmsTransientStability.CriticalClearingBracket"/>
/// (R106, ruling Q15) and of the classical <see cref="Distributed.ClassicalInit.CriticalClearingBracket"/> (N042,
/// the same contract). Clearing after <see cref="StableSteps"/> whole integration steps keeps synchronism; clearing
/// after <see cref="UnstableSteps"/> steps loses it (null when the whole search window survived). With the default
/// iteration cap the two are adjacent steps, so the true boundary of the discretised model lies between them.
/// <para>N048: <see cref="UnstableSteps"/> is the FIRST loss from below (see <see cref="CctSearch"/> for the search
/// and its guarantee), and <see cref="NonMonotone"/> says whether a stable pocket was seen above it.</para>
/// </summary>
/// <param name="StableSteps">Longest fault duration, in whole steps, found to keep synchronism (0 when even the
/// instantaneous clear fails; see <see cref="UnstableAtZero"/>).</param>
/// <param name="UnstableSteps">Shortest fault duration, in whole steps, found to lose synchronism; null when every
/// clearing time in the window survived.</param>
/// <param name="StepSeconds">The integration step of the search (s).</param>
/// <param name="UnstableAtZero">True when an instantaneous clear (zero steps) already loses synchronism; the CCT is
/// then reported as 0.</param>
public sealed record CctBracket(int StableSteps, int? UnstableSteps, double StepSeconds, bool UnstableAtZero)
{
    /// <summary>The reported critical clearing time (s): <c>StableSteps * StepSeconds</c>, the longest clearing
    /// time on the step grid that keeps synchronism (0 when <see cref="UnstableAtZero"/>).</summary>
    public double StableSeconds => UnstableAtZero ? 0.0 : StableSteps * StepSeconds;

    /// <summary>The shortest clearing time on the step grid found to lose synchronism (s); null when the whole
    /// window survived.</summary>
    public double? UnstableSeconds => UnstableAtZero ? 0.0 : UnstableSteps * StepSeconds;

    /// <summary>True when the stable and unstable ends are adjacent steps: the bracket is at step resolution.</summary>
    public bool AtStepResolution => UnstableAtZero || UnstableSteps == StableSteps + 1;

    /// <summary>N048: the highest clearing time (whole steps) the search saw HOLD synchronism above the first loss, a
    /// stable pocket; null when none was seen (within the scan depth either side of the bracket). Its presence means
    /// stability is not monotone in the clearing time here, so clearing later than the CCT is not reliably unstable.</summary>
    public int? PocketStableSteps { get; init; }

    /// <summary>N048: true when the search found stability NOT monotone in the clearing time (a stable pocket above the
    /// first loss, <see cref="PocketStableSteps"/>).</summary>
    public bool NonMonotone => PocketStableSteps is not null;

    /// <summary>Bug hunt 2026-10-29, finding 24: how many trials of the search had converter steps whose current
    /// iteration hit its cap (<see cref="MultiMachineSubtransient.Result.ConverterIterationFailures"/>). Above 0 the
    /// bracket rests on at least one verdict judged on unsettled steps, so treat it as unconfirmed. Always 0 for the
    /// classical search, which has no converter plant.</summary>
    public int TrialsWithConverterIterationFailures { get; init; }

    /// <summary>N048: <see cref="PocketStableSteps"/> in seconds; null when no pocket was seen.</summary>
    public double? PocketStableSeconds => PocketStableSteps * StepSeconds;

    /// <summary>N048: the verification and probe depth the search used (whole steps; 0 = bisection alone).</summary>
    public int ScanSteps { get; init; }

    /// <summary>N048: how many distinct clearing times the search simulated (its cost in trial runs).</summary>
    public int Trials { get; init; }
}

/// <summary>
/// A standard exciter for the RMS transient-stability study (R061): a <see cref="Sexs"/> or an
/// <see cref="Ieeet1"/> AVR, optionally with a <see cref="Pss1a"/> stabiliser on the rotor speed deviation whose
/// output is added to the AVR summing junction (<c>Verr = Vref - Vt + Vs</c>). Build one per machine with
/// <see cref="FromSexs"/> or <see cref="FromIeeet1"/>; the parameters are the AVR's own constructor parameters.
///
/// <para>The wrapper owns the AVR (built here from the parameters, so it can also report the steady regulator
/// error the kernel needs for the voltage reference) and only routes the signals; all dynamics are the existing
/// blocks'. It is stateful: one instance per machine. <see cref="Initialize"/> resets every state, so one
/// instance can be reused across separate runs (the CCT bisection does this).</para>
/// </summary>
public sealed class RmsExciter : IRmsTerminalFieldController
{
    // Exactly one of the four AVRs is set.
    private readonly Sexs? _sexs;
    private readonly Ieeet1? _ieeet1;
    private readonly AcExciter? _ac;
    private readonly Esst4b? _st4b;

    // ESST4B: the machine base over the system base (MBase / BaseMva), bound by the study; NaN until bound.
    private double _machineOverSystem = double.NaN;

    // The AVR gain: K for SEXS, Ka for IEEET1. Both constructors already reject a gain <= 0.
    private readonly double _gain;

    // IEEET1 exciter constant Ke and saturation Se = Aex*exp(Bex*Efd); all zero (unused) for SEXS.
    private readonly double _ke, _aex, _bex;

    // The limits an equilibrium must respect: the field limits [Emin, Emax] for SEXS, the regulator limits
    // [Vrmin, Vrmax] for IEEET1 (whose regulator output at equilibrium is Vr = (Ke + Se)*Efd).
    private readonly double _min, _max;

    private RmsExciter(Sexs? sexs, Ieeet1? ieeet1, double gain, double ke, double aex, double bex,
        double min, double max, Pss1a? stabiliser, string kind, AcExciter? ac = null, Esst4b? st4b = null)
    {
        _sexs = sexs; _ieeet1 = ieeet1; _ac = ac; _st4b = st4b; _gain = gain; _ke = ke; _aex = aex; _bex = bex;
        _min = min; _max = max; Stabiliser = stabiliser; Kind = kind;
    }

    /// <summary>True for an exciter that reads the field current (EXAC1, ESAC1A, ESST4B).</summary>
    public bool NeedsFieldCurrent => _ac is not null || _st4b is not null;

    /// <summary>True for an exciter that reads the terminal voltage and current (ESST4B).</summary>
    public bool NeedsTerminalQuantities => _st4b is not null;

    /// <summary>A static ESST4B exciter (see <see cref="Esst4b"/>) with an optional PSS1A. It reads the terminal
    /// quantities on its machine base, so the study binds that base (<see cref="BindMachineBase"/>) before it runs.</summary>
    public static RmsExciter FromEsst4b(Esst4b exciter, Pss1a? stabiliser = null)
    {
        ArgumentNullException.ThrowIfNull(exciter);
        return new RmsExciter(null, null, 1.0, 0.0, 0.0, 0.0, double.NegativeInfinity, double.PositiveInfinity, stabiliser, "ESST4B", null, exciter);
    }

    /// <summary>Bind the machine base: <paramref name="machineOverSystemBase"/> = MBase / system base (positive, finite),
    /// so the machine current on the system base converts to the machine base as I_sys / ratio.</summary>
    public void BindMachineBase(double machineOverSystemBase)
    {
        if (!(machineOverSystemBase > 0) || !double.IsFinite(machineOverSystemBase))
            throw new ArgumentOutOfRangeException(nameof(machineOverSystemBase), machineOverSystemBase, "The machine base ratio must be a positive finite number.");
        _machineOverSystem = machineOverSystemBase;
    }

    /// <summary>An alternator-rectifier exciter (EXAC1 or ESAC1A, see <see cref="AcExciter"/>) with an optional PSS1A.
    /// It reads the machine's field current, so it runs only where the kernel supplies it
    /// (<see cref="IRmsFieldCurrentController"/>).</summary>
    public static RmsExciter FromAc(AcExciter exciter, Pss1a? stabiliser = null)
    {
        ArgumentNullException.ThrowIfNull(exciter);
        return new RmsExciter(null, null, 1.0, 0.0, 0.0, 0.0, double.NegativeInfinity, double.PositiveInfinity, stabiliser, exciter.Kind, exciter);
    }

    /// <summary>The optional power-system stabiliser (null when there is none).</summary>
    public Pss1a? Stabiliser { get; }

    /// <summary>"SEXS", "IEEET1", "EXAC1", "ESAC1A" or "ESST4B", for reports and error messages.</summary>
    public string Kind { get; }

    /// <summary>A SEXS AVR (parameters exactly as <see cref="Sexs"/>'s constructor), with an optional PSS1A.</summary>
    public static RmsExciter FromSexs(double leadSeconds, double lagSeconds, double gain, double exciterTimeSeconds,
        double fieldMinPu = double.NegativeInfinity, double fieldMaxPu = double.PositiveInfinity, Pss1a? stabiliser = null)
    {
        if (!(fieldMaxPu >= fieldMinPu))
            throw new ArgumentException($"SEXS needs Emax >= Emin (got Emin = {fieldMinPu}, Emax = {fieldMaxPu}).", nameof(fieldMaxPu));
        var avr = new Sexs(leadSeconds, lagSeconds, gain, exciterTimeSeconds, fieldMinPu, fieldMaxPu);
        return new RmsExciter(avr, null, gain, 0.0, 0.0, 0.0, fieldMinPu, fieldMaxPu, stabiliser, "SEXS");
    }

    /// <summary>An IEEE Type-1 AVR (parameters exactly as <see cref="Ieeet1"/>'s constructor), with an optional
    /// PSS1A.</summary>
    public static RmsExciter FromIeeet1(double regulatorGain, double regulatorTimeSeconds, double exciterConstant,
        double exciterTimeSeconds, double feedbackGain, double feedbackTimeSeconds,
        double regulatorMinPu = double.NegativeInfinity, double regulatorMaxPu = double.PositiveInfinity,
        double saturationA = 0.0, double saturationB = 0.0, Pss1a? stabiliser = null)
    {
        if (!(regulatorMaxPu >= regulatorMinPu))
            throw new ArgumentException($"IEEET1 needs Vrmax >= Vrmin (got Vrmin = {regulatorMinPu}, Vrmax = {regulatorMaxPu}).", nameof(regulatorMaxPu));
        var avr = new Ieeet1(regulatorGain, regulatorTimeSeconds, exciterConstant, exciterTimeSeconds,
            feedbackGain, feedbackTimeSeconds, regulatorMinPu, regulatorMaxPu, saturationA, saturationB);
        return new RmsExciter(null, avr, regulatorGain, exciterConstant, saturationA, saturationB,
            regulatorMinPu, regulatorMaxPu, stabiliser, "IEEET1");
    }

    /// <summary>The quantity the AVR limits clamp, at an equilibrium with field <paramref name="fieldVoltagePu"/>:
    /// Efd itself for SEXS; the regulator output <c>Vr = (Ke + Se(Efd))*Efd</c> for IEEET1 (Se computed by the
    /// same expression <see cref="Ieeet1"/> uses).</summary>
    private double LimitedQuantity(double fieldVoltagePu)
        => _sexs is not null ? fieldVoltagePu : (_ke + _aex * DetMath.Exp(_bex * fieldVoltagePu)) * fieldVoltagePu;

    /// <summary>Pre-load the AVR (and PSS) at equilibrium with field <paramref name="fieldVoltagePu"/> and zero
    /// speed deviation. Fails loud when the field is not finite or cannot be held inside the AVR's limits: the
    /// blocks would otherwise clamp silently and the "equilibrium" would start moving at t = 0.</summary>
    public void Initialize(double fieldVoltagePu)
    {
        if (!double.IsFinite(fieldVoltagePu))
            throw new ArgumentOutOfRangeException(nameof(fieldVoltagePu), $"Initial field {fieldVoltagePu} is not finite.");
        if (_ac is not null || _st4b is not null)
        {
            // Each checks its own limits at the equilibrium (field current = field voltage); ESST4B on its first step,
            // from that step's terminal measurements.
            if (_ac is not null) _ac.Initialize(fieldVoltagePu);
            else _st4b!.Initialize(fieldVoltagePu);
            Stabiliser?.Initialize(0.0);
            return;
        }
        double q = LimitedQuantity(fieldVoltagePu);
        if (!(q >= _min && q <= _max))
            throw new InvalidOperationException($"{Kind}: the initial field Efd = {fieldVoltagePu} pu needs "
                + (_sexs is not null ? "Efd" : "Vr") + $" = {q} pu, outside the limits [{_min}, {_max}]; the machine "
                + "cannot start at equilibrium under this exciter.");
        if (_sexs is not null) _sexs.Initialize(fieldVoltagePu);
        else _ieeet1!.Initialize(fieldVoltagePu);
        Stabiliser?.Initialize(0.0);   // zero speed deviation at equilibrium, so Vs = 0
    }

    /// <summary>The steady regulator error that holds field <paramref name="fieldVoltagePu"/>. SEXS:
    /// <c>Efd = K*Verr</c>, so <c>Verr = Efd/K</c>. IEEET1: <c>Ka*Verr = Vr = (Ke + Se)*Efd</c> with the rate
    /// feedback washed out, so <c>Verr = (Ke + Se)*Efd/Ka</c>. The PSS adds nothing in steady state.</summary>
    public double EquilibriumErrorPu(double fieldVoltagePu) =>
        _ac is not null ? _ac.EquilibriumErrorPu(fieldVoltagePu)
        : _st4b is not null ? _st4b.EquilibriumErrorPu(fieldVoltagePu)
        : LimitedQuantity(fieldVoltagePu) / _gain;

    /// <summary>One step: the stabiliser reads the start-of-step speed deviation, its output Vs joins the AVR
    /// summing junction, and the AVR returns the field voltage to hold across the step.</summary>
    public double Step(double voltageErrorPu, double speedDeviationPu, double dtSeconds)
    {
        if (_ac is not null || _st4b is not null)
            throw new InvalidOperationException($"{Kind} reads the machine's field current; the caller must use the step that supplies it.");
        double vs = Stabiliser is null ? 0.0 : Stabiliser.Step(speedDeviationPu, dtSeconds);
        double verr = voltageErrorPu + vs;
        return _sexs is not null ? _sexs.Step(verr, dtSeconds) : _ieeet1!.Step(verr, dtSeconds);
    }

    /// <summary>One step of an alternator-rectifier exciter: as the three-argument step, with the start-of-step field
    /// current <paramref name="fieldCurrentPu"/> (Xad base). For SEXS and IEEET1 the field current is not used.</summary>
    public double Step(double voltageErrorPu, double speedDeviationPu, double fieldCurrentPu, double dtSeconds)
    {
        if (_st4b is not null)
            throw new InvalidOperationException("ESST4B reads the terminal voltage and current; the caller must use the step that supplies them.");
        if (_ac is null) return Step(voltageErrorPu, speedDeviationPu, dtSeconds);
        double vs = Stabiliser is null ? 0.0 : Stabiliser.Step(speedDeviationPu, dtSeconds);
        return _ac.Step(voltageErrorPu + vs, fieldCurrentPu, dtSeconds);
    }

    /// <summary>One step of ESST4B with the terminal voltage and the machine current (system base, converted here to the
    /// bound machine base). For the other exciters the terminal quantities are not used.</summary>
    public double Step(double voltageErrorPu, double speedDeviationPu, double fieldCurrentPu, Complex terminalVoltagePu,
        Complex terminalCurrentSystemPu, double dtSeconds)
    {
        if (_st4b is null) return Step(voltageErrorPu, speedDeviationPu, fieldCurrentPu, dtSeconds);
        if (double.IsNaN(_machineOverSystem))
            throw new InvalidOperationException("ESST4B needs its machine base bound (BindMachineBase) to read the terminal current on it; the RMS study binds it.");
        double vs = Stabiliser is null ? 0.0 : Stabiliser.Step(speedDeviationPu, dtSeconds);
        Complex itMachine = terminalCurrentSystemPu / _machineOverSystem;
        return _st4b.Step(voltageErrorPu + vs, terminalVoltagePu.Real, terminalVoltagePu.Imaginary, itMachine.Real, itMachine.Imaginary,
            fieldCurrentPu, dtSeconds);
    }
}
