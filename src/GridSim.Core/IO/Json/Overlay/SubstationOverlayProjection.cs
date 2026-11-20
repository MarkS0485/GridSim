using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;

namespace GridSim.Core.IO.Json.Overlay;

/// <summary>A bus created by splitting an authored bus along the substation's switch states.</summary>
/// <param name="AuthoredBus">The authored bus the sections collapse to.</param>
/// <param name="NewBusId">The id of the bus made for the second (and later) electrical group.</param>
public sealed record OverlayBusSplit(string Substation, long AuthoredBus, int NewBusId, IReadOnlyList<string> Sections);

/// <summary>What the overlay projection did, beyond the model itself.</summary>
public sealed class SubstationOverlayProjectionResult
{
    public required GridModel Model { get; init; }
    public required IReadOnlyList<OverlayBusSplit> Splits { get; init; }
    /// <summary>The implied-split warnings the projection resolved into an exact bus split (the warning is superseded by the mapping).</summary>
    public required IReadOnlyList<SubstationTopologyWarning> SupersededWarnings { get; init; }
    /// <summary>The disposition of every circuit end the substation records name and that the projection moved or cut.</summary>
    public required IReadOnlyList<string> Dispositions { get; init; }
    /// <summary>Things carried but not applied (for example an implied coupling, which is not merged).</summary>
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// RC4 R4-11 part A, the overlay path: a v2 case whose asset overlay holds substation records (busbar sections and
/// switching devices) today only WARNS when the switch states disagree with the authored buses
/// (<see cref="SubstationTopology"/>). This projection turns the same records, with the switch states in force, into an
/// exact bus-branch model: a bus whose sections fall into separate electrical groups is split, one new bus per further
/// group, and every line, transformer and generator named by a bay moves to the bus of its section's group (or is taken
/// out of service when every bay of that end is open). New bus ids are the highest authored id plus one, in substation,
/// bus and group order. The first group of a split bus keeps the authored id.
/// <para>Limits, stated not hidden. A demand record is a load folded onto its bus in a <see cref="GridModel"/> and has no
/// identity of its own, so a demand behind an open bay, or in a group other than the bus's first, is refused with a
/// clear message rather than moved. An implied coupling (sections of different buses joined by closed devices) is
/// reported in <see cref="SubstationOverlayProjectionResult.Notes"/>, not merged.</para>
/// </summary>
public static class SubstationOverlayProjection
{
    /// <summary>Project <paramref name="records"/> onto <paramref name="baseModel"/>. <paramref name="openOverrides"/>
    /// (device id to open) replaces the device's written normal state; null uses the written states.</summary>
    public static SubstationOverlayProjectionResult Project(GridModel baseModel, IReadOnlyList<AssetOverlayRecord> records,
        IReadOnlyDictionary<string, bool>? openOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentNullException.ThrowIfNull(records);
        if (openOverrides is not null)
        {
            var ids = new HashSet<string>(records.Where(r => r.Kind == "switchingDevice").Select(r => r.Id), StringComparer.Ordinal);
            foreach (string k in openOverrides.Keys.OrderBy(k => k, StringComparer.Ordinal))
                if (!ids.Contains(k)) throw new SubstationTopologyException(new[] { $"a state is given for unknown switching device '{k}'." });
            records = records.Select(r => openOverrides.TryGetValue(r.Id, out bool open) && r.Kind == "switchingDevice" ? WithNormalOpen(r, open) : r).ToList();
        }

        var buses = baseModel.Buses.ToList();
        var busIndex = new Dictionary<int, int>();
        for (int i = 0; i < buses.Count; i++) busIndex[buses[i].Id] = i;
        var branches = baseModel.Branches.ToList();
        var gens = baseModel.AllGenerators.ToList();
        int nextId = buses.Count == 0 ? 1 : buses.Max(b => b.Id) + 1;
        var splits = new List<OverlayBusSplit>();
        var superseded = new List<SubstationTopologyWarning>();
        var dispositions = new List<string>();
        var notes = new List<string>();
        var genChanged = new HashSet<int>();

        foreach (SubstationTopology t in SubstationTopology.All(records))
        {
            // Bus per (authored bus, group): the first group keeps the authored id, the others get new ids.
            var busOfGroup = new Dictionary<(long Bus, int Group), int>();
            var newBusesOf = new Dictionary<long, List<int>>();
            foreach (long authored in t.Sections.Where(s => s.Bus is not null).Select(s => s.Bus!.Value).Distinct())
            {
                if (!busIndex.ContainsKey(checked((int)authored)))
                    throw new SubstationTopologyException(new[] { $"substation '{t.Substation}' names bus {authored}, which the case does not have." });
                List<int> groups = t.Sections.Where(s => s.Bus == authored).Select(s => s.Group).Distinct().ToList();
                busOfGroup[(authored, groups[0])] = (int)authored;
                newBusesOf[authored] = new List<int> { (int)authored };
                foreach (int g in groups.Skip(1))
                {
                    int id = nextId++;
                    busOfGroup[(authored, g)] = id;
                    newBusesOf[authored].Add(id);
                    Bus src = buses[busIndex[(int)authored]];
                    busIndex[id] = buses.Count;
                    buses.Add(new Bus(id, BusType.Pq, BaseKv: src.BaseKv, Vm: src.Vm, VaDeg: src.VaDeg, Name: (src.Name ?? authored.ToString()) + "/" + id,
                        X: src.X, Y: src.Y, VminPu: src.VminPu, VmaxPu: src.VmaxPu));
                    splits.Add(new OverlayBusSplit(t.Substation, authored, id,
                        t.Sections.Where(s => s.Bus == authored && s.Group == g).Select(s => s.Id).ToList()));
                }
            }
            superseded.AddRange(t.Warnings.Where(w => w.Kind == TopologyWarningKind.ImpliedSplit && splits.Any(sp => sp.Substation == t.Substation)));
            foreach (SubstationTopologyWarning w in t.Warnings.Where(w => w.Kind == TopologyWarningKind.ImpliedCoupling))
                notes.Add($"substation '{t.Substation}': implied coupling carried, not merged. {string.Join(", ", w.Sections)} remain on their authored buses.");

            // Each circuit end: the bays that name it, grouped by (target, name, authored bus of the section).
            foreach (var endBays in t.Bays.Where(b => b.Feeder is not null)
                         .GroupBy(b => (b.Feeder!, Bus: t.Section(b.Section)!.Bus)))
            {
                if (endBays.Key.Bus is null) continue;
                long authored = endBays.Key.Bus.Value;
                TopologyFeeder f = endBays.Key.Item1;
                TopologyBay? closed = endBays.FirstOrDefault(b => b.Closed);
                int? target = closed is null ? null : busOfGroup[(authored, t.Section(closed.Section)!.Group)];
                string where = $"substation '{t.Substation}', {f.Display} at bus {authored}";

                switch (f.Target)
                {
                    case AssetRefTarget.Line:
                    case AssetRefTarget.Transformer:
                    {
                        int bi = branches.FindIndex(b => b.Name == f.Key && (b.From == authored || b.To == authored));
                        if (bi < 0) throw new SubstationTopologyException(new[] { $"{where}: the case has no such branch end." });
                        Branch br = branches[bi];
                        bool fromEnd = br.From == authored;
                        if (target is null)
                        {
                            branches[bi] = br with { InService = false };
                            dispositions.Add($"{where}: every bay is open; the branch is taken out of service.");
                        }
                        else if (target.Value != authored)
                        {
                            branches[bi] = fromEnd ? br with { From = target.Value } : br with { To = target.Value };
                            dispositions.Add($"{where}: moved to bus {target.Value}.");
                        }
                        break;
                    }
                    case AssetRefTarget.Generator:
                    {
                        int gi = gens.FindIndex(g => g.Name == f.Key && g.Bus == authored);
                        if (gi < 0) throw new SubstationTopologyException(new[] { $"{where}: the case has no such generator." });
                        if (target is null) { gens[gi] = gens[gi] with { InService = false }; genChanged.Add(gi); dispositions.Add($"{where}: every bay is open; the generator is taken out of service."); }
                        else if (target.Value != authored) { gens[gi] = gens[gi] with { Bus = target.Value }; genChanged.Add(gi); dispositions.Add($"{where}: moved to bus {target.Value}."); }
                        break;
                    }
                    case AssetRefTarget.Demand:
                        if (target is null || target.Value != authored)
                            throw new SubstationTopologyException(new[]
                            {
                                $"{where}: the demand sits behind an open bay or in a group other than the bus's first, but a bus-branch model folds a load onto its bus and cannot place it; give the load its own bus in the case, or switch it as an equipment terminal in a native node-breaker file.",
                            });
                        break;
                }
            }
        }

        // Bus types of the buses a split touched: the slack stays on the first bus that still has an in-service generator.
        foreach (var (authored, ids) in splits.GroupBy(s => s.AuthoredBus).Select(g => (g.Key, Ids: new[] { (int)g.Key }.Concat(g.Select(s => s.NewBusId)).ToList())))
        {
            BusType original = baseModel.Buses.First(b => b.Id == authored).Type;
            bool slackPlaced = false;
            foreach (int id in ids)
            {
                bool has = gens.Any(g => g.InService && g.Bus == id);
                BusType type = original == BusType.Pq || !has ? BusType.Pq
                    : original == BusType.Slack && !slackPlaced ? BusType.Slack : BusType.Pv;
                if (type == BusType.Slack) slackPlaced = true;
                buses[busIndex[id]] = buses[busIndex[id]] with { Type = type };
            }
        }

        GridModel model = baseModel.CopyWith(buses: buses, generators: gens, branches: branches, generatorsAreWholeFleet: true);
        return new SubstationOverlayProjectionResult
        {
            Model = model,
            Splits = splits.AsReadOnly(),
            SupersededWarnings = superseded.AsReadOnly(),
            Dispositions = dispositions.AsReadOnly(),
            Notes = notes.AsReadOnly(),
        };
    }

    private static JsonElement ParseElement(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static AssetOverlayRecord WithNormalOpen(AssetOverlayRecord r, bool open)
    {
        JsonNode node = JsonNode.Parse(r.Params.GetRawText()) ?? new JsonObject();
        JsonObject obj = node as JsonObject ?? new JsonObject();
        if (obj["normalOpen"] is JsonObject wrapped && wrapped.ContainsKey("v")) wrapped["v"] = open;
        else obj["normalOpen"] = open;
        return new AssetOverlayRecord
        {
            Id = r.Id, Kind = r.Kind, Parent = r.Parent, Refs = r.Refs,
            Params = ParseElement(obj.ToJsonString()), Prov = r.Prov, Note = r.Note,
            Document = r.Document, Pointer = r.Pointer,
        };
    }
}
