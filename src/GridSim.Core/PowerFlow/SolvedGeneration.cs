using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// Totals read off a solved power flow for the headline reports (the CLI <c>--once</c> report, the live dashboard and
/// the live tick line).
///
/// Why this exists: those reports used to print "Generation" as the sum of the POSITIVE bus net injections
/// (<see cref="PowerFlowResult.PInjMw"/> is generation minus demand at each bus). That is not what any generator
/// produces: a generator bus that also carries load contributes only its surplus, and a generator bus whose local load
/// exceeds its output contributes nothing at all. On IEEE case14 it read 0.25 GW against 0.26 GW of demand plus
/// 13.4 MW of losses, i.e. less generation than consumption, which cannot be right. This class reports the actual
/// generator output instead.
/// </summary>
public static class SolvedGeneration
{
    /// <summary>
    /// Total real-power output of the generators in MW at the solved state.
    ///
    /// How it is computed: at a solved point the network injection at a bus equals generation minus load, so the
    /// generation at a bus is <c>PInjMw[i] + Pload(V_i)</c>, where <c>Pload</c> is the bus demand <see cref="Bus.Pd"/>
    /// scaled by its ZIP multiplier at the solved voltage (exactly <see cref="Bus.Pd"/> for a constant-power bus,
    /// which is every bus without a <see cref="Bus.Zip"/>). That is summed over every bus that carries at least one
    /// generator, each bus counted once however many units it holds. This picks up the slack unit's solved output as
    /// well as every PV unit's scheduled output, without needing per-unit dispatch figures the result does not hold.
    /// Buses without a generator are left out: their generation is zero by definition, and adding them would only add
    /// the solver's residual mismatch.
    ///
    /// Fail loud: a result whose bus list does not match the model is a programming error and throws.
    /// </summary>
    /// <param name="model">The model that was solved.</param>
    /// <param name="result">The solved state of <paramref name="model"/>.</param>
    public static double GeneratorOutputMw(GridModel model, PowerFlowResult result)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        if (result.BusIds.Count != model.BusCount || result.PInjMw.Length != model.BusCount || result.Vm.Length != model.BusCount)
            throw new ArgumentException(
                $"the result has {result.BusIds.Count} bus ids, {result.PInjMw.Length} injections and {result.Vm.Length} voltages, " +
                $"but the model has {model.BusCount} buses: it is not a solve of this model.", nameof(result));

        // The set of buses that hold any generator (a bus with three units is still one bus).
        var generatorBuses = new HashSet<int>();
        foreach (Generator g in model.Generators) generatorBuses.Add(g.Bus);

        // Bus records by id, so each result row finds its demand and load model.
        var busById = new Dictionary<int, Bus>(model.BusCount);
        foreach (Bus b in model.Buses) busById[b.Id] = b;

        // Sum in result order (a fixed order, so the total is deterministic run to run).
        double total = 0.0;
        for (int i = 0; i < result.BusIds.Count; i++)
        {
            int id = result.BusIds[i];
            if (!generatorBuses.Contains(id)) continue;
            if (!busById.TryGetValue(id, out Bus? bus))
                throw new ArgumentException($"result bus {id} is not in the model.", nameof(result));
            // GR-499: the one load rule the solver uses (LoadAtVoltage): a ZIP that does not vary with voltage draws Pd.
            double loadMw = LoadAtVoltage.Mw(bus, result.Vm[i]).PMw;
            total += result.PInjMw[i] + loadMw;
        }
        return total;
    }

    /// <summary>
    /// The SOLVED output of one generator, <paramref name="generatorIndex"/> in <c>model.Generators</c>, as
    /// (P in MW, Q in MVAr) (D-DOC-054, docs-session finding, Ruling R1: Mark delegated 2026-10-04).
    ///
    /// <para>Why: a generator's <see cref="Generator.Pg"/> / <see cref="Generator.Qg"/> are its SCHEDULE. The power flow
    /// sets the slack unit's P (it picks up the losses) and every PV unit's Q (whatever holds its voltage set point), so
    /// a study that starts from "the operating point" must read those from the solve, not from the schedule. The
    /// <c>impedance-stability</c> verb used the scheduled Pg / Qg for its converter operating point; a PV bus whose case
    /// schedules Qg = 0 was studied at Q = 0 whatever the solve gave.</para>
    ///
    /// <para>How it is computed: the generation at the unit's bus is the solved net injection plus the bus load at the
    /// solved voltage (<see cref="Bus.Pd"/> / <see cref="Bus.Qd"/>, scaled by the bus's ZIP multipliers when it has
    /// a <see cref="Bus.Zip"/>), exactly as <see cref="GeneratorOutputMw"/> reads it. When several units share the bus,
    /// that generation is split between them by the same rule the subtransient initialisation uses
    /// (<c>SubtransientInit.FromSolvedCase</c>): active power in proportion to the scheduled Pg (equally when the
    /// bus's scheduled total is within 1e-9 MW of zero), reactive power in proportion to each unit's reactive range
    /// Qmax - Qmin (its MBase when the range is not finite and positive). A lone unit takes the whole bus.</para>
    ///
    /// <para>Fail loud: a result that is not a solve of this model, an index outside <c>model.Generators</c>, or a
    /// generator bus missing from the result throws <see cref="ArgumentException"/> /
    /// <see cref="ArgumentOutOfRangeException"/>; a non-converged result throws
    /// <see cref="NonConvergedStateException"/> (a transient iterate is no operating point).</para>
    /// </summary>
    /// <param name="model">The model that was solved.</param>
    /// <param name="result">The converged solve of <paramref name="model"/>.</param>
    /// <param name="generatorIndex">The unit's index in <c>model.Generators</c>.</param>
    public static (double PMw, double QMvar) UnitOutput(GridModel model, PowerFlowResult result, int generatorIndex)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        result.RequireConverged("Solved generator output");
        if (generatorIndex < 0 || generatorIndex >= model.Generators.Count)
            throw new ArgumentOutOfRangeException(nameof(generatorIndex), generatorIndex,
                $"the model has {model.Generators.Count} generators.");
        if (result.BusIds.Count != model.BusCount || result.PInjMw.Length != model.BusCount
            || result.QInjMvar.Length != model.BusCount || result.Vm.Length != model.BusCount)
            throw new ArgumentException(
                $"the result has {result.BusIds.Count} bus ids, {result.PInjMw.Length} P and {result.QInjMvar.Length} Q "
                + $"injections and {result.Vm.Length} voltages, but the model has {model.BusCount} buses: it is not a "
                + "solve of this model.", nameof(result));

        Generator gen = model.Generators[generatorIndex];

        // The unit's bus row in the result (fail loud if the solve does not carry it).
        int row = -1;
        for (int i = 0; i < result.BusIds.Count; i++)
            if (result.BusIds[i] == gen.Bus) { row = i; break; }
        if (row < 0)
            throw new ArgumentException($"generator bus {gen.Bus} is not in the result.", nameof(result));
        Bus? bus = null;
        foreach (Bus b in model.Buses) if (b.Id == gen.Bus) { bus = b; break; }
        if (bus is null)
            throw new ArgumentException($"generator bus {gen.Bus} is not in the model.", nameof(model));

        // Generation at the bus = solved net injection + the load at the solved voltage (ZIP-aware).
        double vm = result.Vm[row];
        (double loadMw, double loadMvar) = LoadAtVoltage.Mw(bus, vm);   // GR-499: the solver's own load rule
        double busGenMw = result.PInjMw[row] + loadMw;
        double busGenMvar = result.QInjMvar[row] + loadMvar;

        // The units on this bus, in model order: scheduled P total, reactive weights, count.
        double schedP = 0.0, qWeightTotal = 0.0;
        int count = 0;
        foreach (Generator g in model.Generators)
        {
            if (g.Bus != gen.Bus) continue;
            schedP += g.Pg;
            qWeightTotal += ReactiveWeight(g);
            count++;
        }

        // A lone unit takes the whole bus (bit for bit the bus generation, no multiply by a share of 1).
        if (count == 1) return (busGenMw, busGenMvar);

        double pShare = Math.Abs(schedP) > ScheduledShareFloorMw ? gen.Pg / schedP : 1.0 / count;
        double qShare = ReactiveWeight(gen) / qWeightTotal;
        return (busGenMw * pShare, busGenMvar * qShare);
    }

    /// <summary>A bus whose scheduled generation is within this (MW) of zero shares its solved active power equally
    /// between its units (the subtransient initialisation's <c>MinScheduledPowerMw</c>).</summary>
    public const double ScheduledShareFloorMw = 1e-9;

    // A unit's reactive-sharing weight: its reactive range Qmax - Qmin when finite and positive, else its MBase
    // (floored at 1e-9 MVA so a zero-rated unit cannot make the total zero). The subtransient initialisation's QWeight.
    private static double ReactiveWeight(Generator g)
    {
        double range = g.Qmax - g.Qmin;
        return double.IsFinite(range) && range > 0 ? range : Math.Max(g.MBase, 1e-9);
    }
}
