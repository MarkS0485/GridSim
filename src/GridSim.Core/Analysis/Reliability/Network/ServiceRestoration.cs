using GridSim.Core.Analysis.Restoration;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.Reliability.Network;

internal enum RestorationClass { Repair, Switching, BackFeed }

/// <summary>How one interrupted load is restored in one event (null reason = restored before the repair).</summary>
internal sealed record LoadRestoration(RestorationClass Class, string? Tie, string? Reason);

/// <summary>The plan for one failed branch, independent of any rate or time: who clears it, what is opened to isolate it,
/// which loads are interrupted and how each is restored.</summary>
internal sealed record EventPlan(int Branch, int ClearingBranch, string ClearedBy, string? IsolatingSwitch,
    IReadOnlyList<int> Opened, IReadOnlyList<int> InterruptedBuses, LoadRestoration?[] PerLoad,
    IReadOnlyList<RestorationPathStep> Path, string Ac, string? AcNote);

/// <summary>
/// RC4 R4-13 service restoration on the real network. For one failed branch k, in this order:
/// 1. The clearing device c is the nearest placed device on the path from k to the source (k first), otherwise the feeder
///    breaker (the branch leaving the slack). Every bus at or below c is de-energised.
/// 2. Isolation. u is the nearest switching device on the path strictly below c (k first). If k itself carries one it is
///    a fully isolated section. Otherwise the nearest switching devices below k are opened too, so that the part of the
///    feeder beyond them is separate from the failed conductor.
/// 3. Source re-closure: if u exists and c is not k, c re-closes; every bus reachable from the source with k, u and the
///    opened devices out is restored (duration: the switching time). This is the incumbent radial rule.
/// 4. Back-feed: each remaining de-energised section not still attached to the failed conductor is offered, in model
///    order, the normally-open ties that join it to an energised bus; the first whose restored network passes the AC
///    verifier (the incumbent restoration enumerator) is closed. Loads so restored wait the switching time.
/// 5. Whatever remains waits for the repair, with the reason.
/// </summary>
internal sealed class ServiceRestoration
{
    private readonly GridModel _model;
    private readonly FeederTree _tree;
    private readonly HashSet<int> _clearing;
    private readonly HashSet<int> _switching;
    private readonly Dictionary<int, string> _deviceLabel;
    private readonly Dictionary<int, string> _switchLabel;
    private readonly IReadOnlyList<TiePoint> _ties;
    private readonly int[] _loadBusPos;
    private readonly AcVerificationOptions _ac;

    public ServiceRestoration(GridModel model, FeederTree tree, HashSet<int> clearing, HashSet<int> switching,
        Dictionary<int, string> deviceLabel, Dictionary<int, string> switchLabel, IReadOnlyList<TiePoint> ties,
        int[] loadBusPos, AcVerificationOptions ac)
    {
        _model = model; _tree = tree; _clearing = clearing; _switching = switching;
        _deviceLabel = deviceLabel; _switchLabel = switchLabel; _ties = ties; _loadBusPos = loadBusPos; _ac = ac;
    }

    private const string AttachedReason =
        "the section is still connected to the failed branch (no switching device isolates it), so supply waits for the repair";

    public EventPlan Plan(int k)
    {
        List<int> path = _tree.PathToRoot(k);
        int idxC = path.FindIndex(b => _clearing.Contains(b));
        string clearedBy;
        if (idxC < 0) { idxC = path.Count - 1; clearedBy = "FeederBreaker"; }
        else clearedBy = _deviceLabel.TryGetValue(path[idxC], out string? lab) ? lab : "b" + path[idxC];
        int c = path[idxC];
        SortedSet<int> dead = _tree.SubtreeBuses(c);

        bool kIsolated = _switching.Contains(k);
        int u = -1;
        for (int i = 0; i < idxC; i++) if (_switching.Contains(path[i])) { u = path[i]; break; }
        bool reclose = c != k && u >= 0;
        var opened = new List<int>();
        if (u >= 0) opened.Add(u);
        if (!kIsolated) CollectNearestSwitches(_tree.DownBus[k], opened);

        var blockedAll = new HashSet<int>(opened) { k };
        var perLoad = new LoadRestoration?[_loadBusPos.Length];
        for (int j = 0; j < perLoad.Length; j++)
            if (dead.Contains(_loadBusPos[j])) perLoad[j] = new LoadRestoration(RestorationClass.Repair, null, null);

        // The outage state the enumerator starts from: failed and opened branches out; the clearing branch c open.
        GridModel baseModel = _model.CopyWith(branches: _model.Branches.Select((b, i) =>
            (i == k || i == c || opened.Contains(i)) && b.InService ? b with { InService = false } : b).ToList());
        var accepted = new List<(string Id, int Branch)>();
        List<RestorationPathStep>? lastSteps = null;
        bool acNotRun = !_ac.Enabled;

        // When the outage leaves the source alone (the feeder breaker is the only branch it has), the incumbent enumerator has no
        // verifiable initial state; the restored networks are then verified directly, prefix by prefix, with the same verifier.
        bool trivialStart = !baseModel.Branches.Any(br => br.InService && (br.From == _model.Buses[_tree.SlackPos].Id || br.To == _model.Buses[_tree.SlackPos].Id));

        bool Try(List<(string Id, int Branch)> actions, out List<RestorationPathStep>? steps, out string note)
        {
            steps = null; note = "";
            if (acNotRun) return true;
            if (trivialStart) return TryDirect(baseModel, actions, out steps, out note);
            RestorationStudyResult result;
            Dictionary<int, int> back;
            try
            {
                GridModel reduced = Reduce(baseModel, actions.Select(a => a.Branch).ToHashSet(), out Dictionary<int, int> map);
                var acts = actions.Select(a =>
                {
                    Branch br = _model.Branches[a.Branch];
                    return new RestorationAction(a.Id, new RestorationBranch(map[a.Branch], br.From, br.To), 1.0);
                }).ToArray();
                result = RestorationStudy.Run(reduced, new RestorationRequest(acts, _ac.MinPu, _ac.MaxPu));
                back = map.ToDictionary(kv => kv.Value, kv => kv.Key);
            }
            catch (ArgumentException ex) { note = "restoration enumerator refused: " + ex.Message; return false; }
            bool ok = result.Status is "OPTIMAL" or "FEASIBLE" && result.Sequence.Count == actions.Count;
            if (!ok) { note = Describe(result); return false; }
            steps = result.Sequence.Select(st => new RestorationPathStep(st.Step, st.Action, back[st.Branch.Index], st.State.Disposition,
                st.State.Limits.Count(l => l.Violated))).ToList();
            return true;
        }

        // Step 3: source re-closure.
        var restoredBuses = new HashSet<int>();
        if (reclose)
        {
            var action = new List<(string Id, int Branch)> { ("reclose:" + (clearedBy == "FeederBreaker" ? "feeder-breaker-b" + c : clearedBy), c) };
            HashSet<int> fromSource = _tree.Reach([_tree.SlackPos], blockedAll);
            if (Try(action, out List<RestorationPathStep>? r, out string note))
            {
                accepted.AddRange(action);
                lastSteps = r;
                foreach (int b in fromSource) if (dead.Contains(b)) restoredBuses.Add(b);
                for (int j = 0; j < perLoad.Length; j++)
                    if (perLoad[j] is not null && restoredBuses.Contains(_loadBusPos[j]))
                        perLoad[j] = new LoadRestoration(RestorationClass.Switching, null, null);
            }
            else
            {
                for (int j = 0; j < perLoad.Length; j++)
                    if (perLoad[j] is not null && fromSource.Contains(_loadBusPos[j]))
                        perLoad[j] = new LoadRestoration(RestorationClass.Repair, null, "the source re-closure failed the AC verifier: " + note);
            }
        }

        // Step 4: back-feed of the de-energised sections that are not attached to the failed conductor.
        var blockedForSections = new HashSet<int>(blockedAll) { c };
        var remaining = new SortedSet<int>(dead);
        remaining.ExceptWith(restoredBuses);
        var sectionsDone = new HashSet<int>();
        int upK = _tree.UpBus[k], downK = _tree.DownBus[k];
        var acceptedTies = new HashSet<int>();
        foreach (int start in remaining.ToArray())
        {
            if (sectionsDone.Contains(start)) continue;
            HashSet<int> section = _tree.Reach([start], blockedForSections, remaining);
            sectionsDone.UnionWith(section);
            bool attached = !kIsolated && (section.Contains(upK) || section.Contains(downK));
            string? reason;
            string? tieId = null;
            if (attached) reason = AttachedReason;
            else
            {
                reason = "no normally-open tie reaches this section from an energised bus";
                var failures = new List<string>();
                foreach (TiePoint tie in _ties)
                {
                    if (acceptedTies.Contains(tie.BranchIndex)) continue;
                    Branch tb = _model.Branches[tie.BranchIndex];
                    int a = _tree.BusPos[tb.From], b = _tree.BusPos[tb.To];
                    bool aIn = section.Contains(a), bIn = section.Contains(b);
                    if (aIn == bIn) continue;
                    int other = aIn ? b : a;
                    bool energised = _tree.Reached[other] && (!dead.Contains(other) || restoredBuses.Contains(other));
                    if (!energised) continue;
                    var actions = new List<(string Id, int Branch)>(accepted) { ("tie:" + tie.Id, tie.BranchIndex) };
                    if (Try(actions, out List<RestorationPathStep>? r2, out string note2))
                    {
                        accepted = actions;
                        lastSteps = r2;
                        acceptedTies.Add(tie.BranchIndex);
                        tieId = tie.Id;
                        reason = null;
                        restoredBuses.UnionWith(section);
                        break;
                    }
                    failures.Add($"tie {tie.Id}: {note2}");
                }
                if (reason is not null && failures.Count > 0) reason = "every candidate tie failed the AC verifier: " + string.Join("; ", failures);
            }
            for (int j = 0; j < perLoad.Length; j++)
                if (perLoad[j] is not null && section.Contains(_loadBusPos[j]))
                    perLoad[j] = tieId is not null
                        ? new LoadRestoration(RestorationClass.BackFeed, tieId, null)
                        : new LoadRestoration(RestorationClass.Repair, null, reason);
        }
        // A repair load that still has no reason (not a member of any remaining section): the only path was the failed one.
        for (int j = 0; j < perLoad.Length; j++)
            if (perLoad[j] is { Class: RestorationClass.Repair, Reason: null } lr)
                perLoad[j] = lr with { Reason = AttachedReason };

        var steps = new List<RestorationPathStep>();
        string acText;
        if (accepted.Count == 0) acText = "NOT APPLICABLE";
        else if (acNotRun) acText = "NOT RUN";
        else acText = lastSteps![^1].Disposition;
        if (accepted.Count > 0)
        {
            if (acNotRun)
                for (int i = 0; i < accepted.Count; i++)
                    steps.Add(new RestorationPathStep(i + 1, accepted[i].Id, accepted[i].Branch, "NOT RUN", 0));
            else
                steps.AddRange(lastSteps!);
        }
        return new EventPlan(k, c, clearedBy, u >= 0 ? SwitchLabel(u) : null, opened, dead.Select(p => _model.Buses[p].Id).ToArray(),
            perLoad, steps, acText, null);
    }

    /// <summary>The part of <paramref name="baseModel"/> that is supplied from the slack when <paramref name="closed"/> are in
    /// service: the incumbent enumerator requires a connected target network, so buses that stay dark are dropped. Returns the
    /// sub-model and the map from original to sub-model branch index.</summary>
    private GridModel Reduce(GridModel baseModel, HashSet<int> closed, out Dictionary<int, int> map)
    {
        Branch[] br = baseModel.Branches.Select((b, i) => closed.Contains(i) ? b with { InService = true } : b).ToArray();
        var live = new HashSet<int> { _model.Buses[_tree.SlackPos].Id };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Branch b in br)
                if (b.InService && live.Contains(b.From) != live.Contains(b.To)) { live.Add(b.From); live.Add(b.To); grew = true; }
        }
        map = [];
        var kept = new List<Branch>();
        for (int i = 0; i < br.Length; i++)
        {
            if ((br[i].InService || closed.Contains(i)) && live.Contains(br[i].From) && live.Contains(br[i].To)) { map[i] = kept.Count; kept.Add(br[i]); }
        }
        var names = new HashSet<string>(kept.Where(b => b.Name is not null).Select(b => b.Name!), StringComparer.Ordinal);
        return baseModel.CopyWith(
            buses: baseModel.Buses.Where(b => live.Contains(b.Id)).ToList(),
            generators: baseModel.Generators.Where(g => live.Contains(g.Bus)).ToList(),
            branches: kept,
            oltcs: baseModel.Oltcs.Where(o => names.Contains(o.Transformer) && live.Contains(o.ControlsBus)).ToList());
    }

    private bool TryDirect(GridModel baseModel, List<(string Id, int Branch)> actions, out List<RestorationPathStep>? steps, out string note)
    {
        steps = null; note = "";
        var closed = new HashSet<int>();
        var path = new List<RestorationPathStep>();
        foreach (var (id, branch) in actions)
        {
            closed.Add(branch);
            GridModel sub = Reduce(baseModel, closed, out _);
            var state = TransmissionSecurityRunner.VerifyState(sub, _ac.MinPu, _ac.MaxPu, PostFaultRating.RateA, null);
            int violations = state.Limits.Count(l => l.Violated);
            path.Add(new RestorationPathStep(path.Count + 1, id, branch, state.Disposition, violations));
            if (state.Disposition != "SECURE")
            {
                note = $"AC verification after {id}: {state.Disposition}" + (state.Reason is null ? "" : " (" + state.Reason + ")") + (violations > 0 ? $", {violations} limit violation(s)" : "");
                return false;
            }
        }
        steps = path;
        return true;
    }

    private string SwitchLabel(int branch) =>
        _switchLabel.TryGetValue(branch, out string? s) ? s : _deviceLabel.TryGetValue(branch, out string? d) ? d : "b" + branch;

    private void CollectNearestSwitches(int busPos, List<int> into)
    {
        foreach (int d in _tree.ChildBranches(busPos))
        {
            if (_switching.Contains(d)) into.Add(d);
            else CollectNearestSwitches(_tree.DownBus[d], into);
        }
    }

    private static string Describe(RestorationStudyResult r)
    {
        var parts = new List<string> { r.Status };
        if (r.Reason is not null) parts.Add(r.Reason);
        foreach (var s in r.States.Where(s => !s.Admissible).Take(3))
            parts.Add($"state [{string.Join(",", s.Closed)}] {s.Disposition}" + (s.ViolatedLimits.Count > 0 ? $" ({s.ViolatedLimits.Count} limit(s))" : ""));
        if (r.InitialState is { } st && st.Disposition != "SECURE") parts.Add("initial state " + st.Disposition);
        return string.Join("; ", parts);
    }
}
