namespace GridSim.Core.Assets.Protection;

/// <summary>
/// One placed protective device: which authored class it uses, the bus it monitors, the MW it acts on
/// (the infeed it trips or the demand it sheds), and - for a topology recloser - the branch it opens.
/// Placement is separate from asset-class binding because a protection needs an explicit location and
/// capacity that fuel-type binding cannot express.
/// </summary>
public sealed record ProtectionInstance(string Name, string ClassName, int MonitorBus, double CapacityMw, string? TargetBranch = null);

/// <summary>
/// The authored-protection bundle attached to a <see cref="Model.GridModel"/>: the class library plus the
/// placed instances. Present only when a case ships <c>protections.json</c>; null otherwise (so every
/// existing case is unchanged and the reduction gate holds by construction).
/// </summary>
public sealed record ProtectionModel(AssetClassLibrary Library, IReadOnlyList<ProtectionInstance> Instances)
{
    /// <summary>
    /// Check every placed device against the network it sits in, and throw (plain English, every problem listed)
    /// if any cannot do what its class says (R232, October 2026). Before this nothing was checked:
    /// <list type="bullet">
    /// <item>a recloser (a class with <c>autoRecloseDelayS</c>) with no TargetBranch latched Tripped and opened
    /// nothing; one whose TargetBranch names no branch matched nothing when the topology was applied - either way
    /// the dashboard showed a trip on an untouched network;</item>
    /// <item>a TargetBranch on a device that is not a recloser was silently ignored;</item>
    /// <item>a MonitorBus that is not a bus read voltage 1.0 and loading 0.0, so its under/over-voltage and
    /// overload stages could never operate;</item>
    /// <item>an instance naming a class missing from the library, or a class that is not a protection.</item>
    /// </list>
    /// </summary>
    public void ValidateAgainst(Model.GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var busIds = new HashSet<int>();
        foreach (Model.Bus b in model.Buses) busIds.Add(b.Id);
        // GR-141: how many branches carry each name. A recloser opens every branch whose name is its target (TopologyOps),
        // so a name shared by several branches (gb-full reuses transformer names across substations) would open all of
        // them; such a target is refused, as a missing one is.
        var branchNameCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Model.Branch br in model.Branches)
            if (br.Name is { } n) branchNameCount[n] = branchNameCount.GetValueOrDefault(n) + 1;

        var problems = new List<string>();
        foreach (ProtectionInstance pi in Instances)
        {
            if (!Library.Contains(pi.ClassName))
            {
                problems.Add($"device '{pi.Name}' uses class '{pi.ClassName}', which is not in the class library");
                continue;
            }
            AssetClass cls = Library.Get(pi.ClassName);
            if (cls.Kind != AssetKind.Protection)
                problems.Add($"device '{pi.Name}' uses class '{pi.ClassName}', which is kind {cls.Kind}, not protection");
            if (!busIds.Contains(pi.MonitorBus))
                problems.Add($"device '{pi.Name}' monitors bus {pi.MonitorBus}, which is not a bus of model '{model.Name}'");

            // D-DOC-094 (October 2026): the capacity is the MW the device trips or sheds; the schema says minimum 0, but
            // nothing checked it, so a negative value loaded and added a negative infeed loss (a gain) when it tripped.
            if (!(pi.CapacityMw >= 0.0) || !double.IsFinite(pi.CapacityMw))
                problems.Add($"device '{pi.Name}' has capacityMw {pi.CapacityMw}, which must be a finite number of 0 MW or more");

            // D-DOC-093 (October 2026): an overload stage (overloadTripFraction) reads the loading of the in-service
            // branches at its monitor bus, each as its MVA over its RateA; a branch with no rating (RateA 0) reads as
            // unloaded. A device whose monitor bus has NO rated in-service branch therefore read loading 0 for ever and its
            // overload stage could never operate, silently. Refused, naming the bus, so the author rates a branch there or
            // drops the stage.
            if (cls.Params.ContainsKey(AssetParams.OverloadTripFraction) && busIds.Contains(pi.MonitorBus))
            {
                bool rated = false;
                foreach (Model.Branch br in model.Branches)
                    if (br.InService && br.RateAMva > 0.0 && (br.From == pi.MonitorBus || br.To == pi.MonitorBus)) { rated = true; break; }
                if (!rated)
                    problems.Add($"device '{pi.Name}' (class '{pi.ClassName}') has an overload stage ({AssetParams.OverloadTripFraction}), but no in-service "
                        + $"branch at its monitor bus {pi.MonitorBus} has a rating (RateA > 0), so its loading always reads 0 and the stage could never operate");
            }

            bool recloser = cls.Params.ContainsKey(AssetParams.AutoRecloseDelayS);
            bool hasTarget = !string.IsNullOrWhiteSpace(pi.TargetBranch);
            if (recloser && !hasTarget)
                problems.Add($"device '{pi.Name}' is a recloser (class '{pi.ClassName}' sets {AssetParams.AutoRecloseDelayS}) but names no TargetBranch to open");
            else if (recloser && !branchNameCount.ContainsKey(pi.TargetBranch!))
                problems.Add($"device '{pi.Name}' targets branch '{pi.TargetBranch}', which is not a branch of model '{model.Name}'");
            else if (recloser && branchNameCount[pi.TargetBranch!] > 1)
                problems.Add($"device '{pi.Name}' targets branch '{pi.TargetBranch}', a name {branchNameCount[pi.TargetBranch!]} branches of model " +
                             $"'{model.Name}' share, so operating it would open all of them; give the target branch a unique name");
            else if (!recloser && hasTarget)
                problems.Add($"device '{pi.Name}' names TargetBranch '{pi.TargetBranch}', but class '{pi.ClassName}' is not a recloser (no {AssetParams.AutoRecloseDelayS}), so it would never open it");
        }
        if (problems.Count > 0)
            throw new InvalidDataException("Protection placement is invalid:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", problems));
    }
}
