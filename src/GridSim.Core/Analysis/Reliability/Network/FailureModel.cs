using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Reliability.Network;

/// <summary>Failure data for every in-service branch of one group: a rate per km of circuit (Branch.LengthKm), a rate
/// per item, or both (they add), the repair time and the switching time (hours). Any member may be absent; the study then
/// reports the missing field instead of assuming a value.</summary>
public sealed record FailureGroupData(string Group, double? FailuresPerKmYear, double? FailuresPerItemYear,
    double? RepairHours, double? SwitchingHours);

/// <summary>A per-branch replacement of the group figures (branch by index plus expected From and To, the RC3 identity rule).
/// <see cref="FailuresPerYear"/> replaces the group's per-km and per-item rate together.</summary>
public sealed record FailureOverride(int BranchIndex, int From, int To, double? FailuresPerYear, double? RepairHours,
    double? SwitchingHours);

/// <summary>One failable branch with its resolved data. <see cref="Id"/> is <c>b&lt;index&gt;</c>.</summary>
public sealed record FailureComponent(int Branch, string Id, string? Name, string Group, double? FailuresPerYear,
    double? RepairHours, double? SwitchingHours, string? RateIssue);

/// <summary>
/// RC4 R4-13 failure model: binds failure data to the in-service branches of the named groups (R4-01), in ascending
/// branch index ("model order"). Rate = per-km rate x circuit length + per-item rate; a per-km rate on a branch whose
/// length is not stated (0) is reported missing, never read as zero.
/// </summary>
public static class FailureModel
{
    public static IReadOnlyList<FailureComponent> Resolve(GridModel model, BoundGroups groups, IReadOnlyList<FailureGroupData> data,
        IReadOnlyList<FailureOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(groups);
        if (data is null || data.Count == 0) throw new ArgumentException("At least one failure-data group is required.");
        var byBranch = new SortedDictionary<int, (FailureGroupData Data, string Group)>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (FailureGroupData d in data)
        {
            if (d is null || string.IsNullOrWhiteSpace(d.Group)) throw new ArgumentException("Every failure-data row needs a group id.");
            if (!seenGroups.Add(d.Group)) throw new ArgumentException($"Failure data for group '{d.Group}' is given twice.");
            Check($"group '{d.Group}' failuresPerKmYear", d.FailuresPerKmYear);
            Check($"group '{d.Group}' failuresPerItemYear", d.FailuresPerItemYear);
            Check($"group '{d.Group}' repairHours", d.RepairHours);
            Check($"group '{d.Group}' switchingHours", d.SwitchingHours);
            if (!groups.TryGet(d.Group, out BoundGroup group))
                throw new ArgumentException($"Group '{d.Group}' is not defined in the groups document.");
            if (group.BranchIndices.Count == 0)
                throw new ArgumentException($"Group '{d.Group}' lists no branches; failure data attaches to branch members.");
            foreach (int b in group.BranchIndices)
            {
                if (!model.Branches[b].InService) continue;       // a normally-open tie is not a failable series item
                if (byBranch.TryGetValue(b, out var prior))
                    throw new ArgumentException($"Branch {b} is in both failure-data groups '{prior.Group}' and '{d.Group}'.");
                byBranch[b] = (d, d.Group);
            }
        }
        var ovr = new Dictionary<int, FailureOverride>();
        foreach (FailureOverride o in overrides ?? [])
        {
            if (o is null) throw new ArgumentException("A null override.");
            if (o.BranchIndex < 0 || o.BranchIndex >= model.Branches.Count)
                throw new ArgumentException($"Override branch index {o.BranchIndex} is outside the case.");
            Branch br = model.Branches[o.BranchIndex];
            if (br.From != o.From || br.To != o.To)
                throw new ArgumentException($"Override branch {o.BranchIndex} joins {br.From}-{br.To} in the case, not {o.From}-{o.To}.");
            if (!byBranch.ContainsKey(o.BranchIndex))
                throw new ArgumentException($"Override branch {o.BranchIndex} is not an in-service member of any failure-data group.");
            if (!ovr.TryAdd(o.BranchIndex, o)) throw new ArgumentException($"Override for branch {o.BranchIndex} is given twice.");
            Check($"override branch {o.BranchIndex} failuresPerYear", o.FailuresPerYear);
            Check($"override branch {o.BranchIndex} repairHours", o.RepairHours);
            Check($"override branch {o.BranchIndex} switchingHours", o.SwitchingHours);
        }
        var list = new List<FailureComponent>();
        foreach ((int b, (FailureGroupData d, string g)) in byBranch)
        {
            Branch br = model.Branches[b];
            double? rate = null;
            string? issue = null;
            if (d.FailuresPerKmYear is double perKm)
            {
                if (br.LengthKm > 0) rate = perKm * br.LengthKm;
                else issue = "lengthKm";
            }
            if (d.FailuresPerItemYear is double perItem && issue is null) rate = (rate ?? 0.0) + perItem;
            double? repair = d.RepairHours, switching = d.SwitchingHours;
            if (ovr.TryGetValue(b, out FailureOverride? o))
            {
                if (o.FailuresPerYear is double fy) { rate = fy; issue = null; }
                if (o.RepairHours is double rh) repair = rh;
                if (o.SwitchingHours is double sh) switching = sh;
            }
            list.Add(new FailureComponent(b, "b" + b.ToString(System.Globalization.CultureInfo.InvariantCulture), br.Name, g, rate, repair, switching, issue));
        }
        return list;
    }

    private static void Check(string what, double? v)
    {
        if (v is double x && (!double.IsFinite(x) || x < 0)) throw new ArgumentException($"{what} must be finite and non-negative.");
    }
}
