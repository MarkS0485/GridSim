namespace GridSim.Core.Distribution;

/// <summary>
/// Pseudo-measurement generation for under-metered distribution feeders. A real feeder has telemetry only at
/// the primary substation plus sparse AMI, so the estimator needs pseudo-measurements to be observable. These
/// are <b>historical / allocation priors, not forecasts</b>: nominal or load-research values with a
/// deliberately loose sigma, generated deterministically (no wall-clock read), exactly like
/// <c>SyntheticMeasurements</c>. They add no observation of the real feeder's unpublished present, so they sit
/// squarely inside the no-forward-inference rule.
/// </summary>
public static class DistributionPseudoMeasurements
{
    /// <summary>Nominal per-phase voltage-magnitude pseudo-measurements (at <paramref name="nominalPu"/>, loose
    /// <paramref name="sigma"/>) for every non-source bus not already in <paramref name="meteredBusIds"/> - the
    /// classic voltage pseudo-measurements that make a magnitude-under-metered feeder observable. GR-584: only on the
    /// phases each bus has (the phases of the branch that feeds it from the source, as the three-phase sweep assigns
    /// them); a single- or two-phase lateral used to get a nominal 1 pu prior on phases it does not have.</summary>
    public static IReadOnlyList<ThreePhaseVMag> NominalVoltageMagnitudes(
        MultiPhaseModel model, ISet<int> meteredBusIds, double nominalPu = 1.0, double sigma = 0.05)
    {
        Dictionary<int, Phase> phases = BusPhasesFromSource(model);
        var list = new List<ThreePhaseVMag>();
        foreach (PhaseBus bus in model.Buses)
        {
            if (bus.IsSource || meteredBusIds.Contains(bus.Id)) continue;
            // A bus the walk did not reach (no path from a source) keeps all three phases, the old behaviour; the
            // estimator refuses such a model on its own terms.
            Phase has = phases.TryGetValue(bus.Id, out Phase ph) ? ph : Phase.Abc;
            for (int p = 0; p < 3; p++)
                if (PhaseIndex.Has(has, p)) list.Add(new ThreePhaseVMag(bus.Id, p, nominalPu, sigma));
        }
        return list;
    }

    // The phases of each bus, walked breadth-first out from the source buses in model order (deterministic): a source
    // has all three, every other bus the phases of the first branch that reaches it, the rule ThreePhaseSweep uses.
    private static Dictionary<int, Phase> BusPhasesFromSource(MultiPhaseModel model)
    {
        var phases = new Dictionary<int, Phase>();
        var queue = new Queue<int>();
        foreach (PhaseBus bus in model.Buses)
        {
            if (!bus.IsSource) continue;
            phases[bus.Id] = Phase.Abc;
            queue.Enqueue(bus.Id);
        }
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            foreach (PhaseBranch br in model.Branches)
            {
                int other;
                if (br.From == at) other = br.To;
                else if (br.To == at) other = br.From;
                else continue;
                if (phases.TryAdd(other, br.Phases)) queue.Enqueue(other);
            }
        }
        return phases;
    }
}
