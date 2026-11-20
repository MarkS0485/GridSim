using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.StudyCases;

// RC4 R4-01: grouping objects (area, zone, owner, feeder and free groups) as a SIDECAR bound to a case. GridModel is
// not touched: GridModelCopyTests fails when a GridModel property is not carried through CopyWith, and a group is a
// property of the study, not of the physics. Identities are the RC3 PlanningTarget rule: buses by ID, generators by
// fleet index plus expected bus, branches by index plus expected From and To (parallel circuits stay distinct).

/// <summary>The kind of a group. Area, zone and owner are exclusive: one element belongs to at most one group of each.
/// A feeder is ordered and has a head element; a free group is any other named collection.</summary>
public enum GroupKind { Area, Zone, Owner, Feeder, Free }

/// <summary>A generator member: zero-based index into the whole fleet (out-of-service units included) and its expected bus.</summary>
public sealed record GroupGenerator(int Index, int Bus);

/// <summary>A branch member: zero-based index into the resolved branch list and its expected From and To bus IDs.</summary>
public sealed record GroupBranch(int Index, int From, int To);

/// <summary>The head of a feeder: <c>bus</c> (Bus), <c>branch</c> (Index) or <c>generator</c> (Index). It must be a member.</summary>
public sealed record GroupHead(string Element, int? Bus, int? Index);

/// <summary>One group as authored.</summary>
public sealed record ModelGroup(string Id, GroupKind Kind, string? Description, IReadOnlyList<int> Buses,
    IReadOnlyList<GroupGenerator> Generators, IReadOnlyList<GroupBranch> Branches, GroupHead? Head)
{
    public bool Exclusive => Kind is GroupKind.Area or GroupKind.Zone or GroupKind.Owner;
    public static string KindName(GroupKind kind) => kind.ToString().ToLowerInvariant();
}

/// <summary>The groups document (<c>gridsim-groups/1</c>): groups in file order, bound to a case by its bundle SHA-256.</summary>
public sealed record ModelGroupSet(string CaseBundleSha256, IReadOnlyList<ModelGroup> Groups)
{
    /// <summary>Canonical text of every membership, for fingerprints (culture-invariant, file order).</summary>
    public string Canonical() => Canonical(Groups);

    public static string Canonical(IEnumerable<ModelGroup> groups)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        foreach (ModelGroup g in groups)
        {
            sb.Append("group\t").Append(g.Id).Append('\t').Append(ModelGroup.KindName(g.Kind)).Append('\n');
            sb.Append("buses\t").Append(string.Join(",", g.Buses.Select(b => b.ToString(inv)))).Append('\n');
            sb.Append("generators\t").Append(string.Join(",", g.Generators.Select(x => $"{x.Index.ToString(inv)}@{x.Bus.ToString(inv)}"))).Append('\n');
            sb.Append("branches\t").Append(string.Join(",", g.Branches.Select(x => $"{x.Index.ToString(inv)}:{x.From.ToString(inv)}-{x.To.ToString(inv)}"))).Append('\n');
            sb.Append("head\t").Append(g.Head is null ? "none" : $"{g.Head.Element}:{g.Head.Bus?.ToString(inv)}:{g.Head.Index?.ToString(inv)}").Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>Strict reader of <c>gridsim-groups/1</c>.</summary>
public static class ModelGroupsDocument
{
    public static ModelGroupSet Parse(byte[] bytes)
    {
        using JsonDocument document = StudyCaseJson.Parse(bytes, "The groups document");
        JsonElement root = document.RootElement;
        StudyCaseJson.Obj(root, "groups document", ["manifestVersion", "case", "groups"], ["manifestVersion", "case", "groups"]);
        string version = StudyCaseJson.Str(root.GetProperty("manifestVersion"), "manifestVersion");
        if (version != StudyCaseVersions.Groups)
            throw new StudyCaseException($"manifestVersion must be \"{StudyCaseVersions.Groups}\", not \"{version}\".");
        JsonElement caseEl = root.GetProperty("case");
        StudyCaseJson.Obj(caseEl, "case", ["sha256"], ["sha256"]);
        string sha = StudyCaseJson.Str(caseEl.GetProperty("sha256"), "case.sha256").ToLowerInvariant();

        var groups = new List<ModelGroup>();
        foreach ((JsonElement g, int gi) in StudyCaseJson.Arr(root.GetProperty("groups"), "groups"))
        {
            string at = $"groups[{gi}]";
            StudyCaseJson.Obj(g, at, ["id", "kind", "description", "buses", "generators", "branches", "head"], ["id", "kind"]);
            string kindText = StudyCaseJson.Str(g.GetProperty("kind"), at + ".kind");
            GroupKind kind = kindText switch
            {
                "area" => GroupKind.Area, "zone" => GroupKind.Zone, "owner" => GroupKind.Owner,
                "feeder" => GroupKind.Feeder, "free" => GroupKind.Free,
                _ => throw new StudyCaseException($"{at}.kind must be area, zone, owner, feeder or free, not \"{kindText}\".")
            };
            var buses = new List<int>();
            if (g.TryGetProperty("buses", out JsonElement busesEl))
                foreach ((JsonElement b, int bi) in StudyCaseJson.Arr(busesEl, at + ".buses")) buses.Add(StudyCaseJson.Int(b, $"{at}.buses[{bi}]"));
            var gens = new List<GroupGenerator>();
            if (g.TryGetProperty("generators", out JsonElement gensEl))
                foreach ((JsonElement x, int xi) in StudyCaseJson.Arr(gensEl, at + ".generators"))
                {
                    string xa = $"{at}.generators[{xi}]";
                    StudyCaseJson.Obj(x, xa, ["index", "bus"], ["index", "bus"]);
                    gens.Add(new GroupGenerator(StudyCaseJson.Int(x.GetProperty("index"), xa + ".index"), StudyCaseJson.Int(x.GetProperty("bus"), xa + ".bus")));
                }
            var branches = new List<GroupBranch>();
            if (g.TryGetProperty("branches", out JsonElement brEl))
                foreach ((JsonElement x, int xi) in StudyCaseJson.Arr(brEl, at + ".branches"))
                {
                    string xa = $"{at}.branches[{xi}]";
                    StudyCaseJson.Obj(x, xa, ["index", "from", "to"], ["index", "from", "to"]);
                    branches.Add(new GroupBranch(StudyCaseJson.Int(x.GetProperty("index"), xa + ".index"),
                        StudyCaseJson.Int(x.GetProperty("from"), xa + ".from"), StudyCaseJson.Int(x.GetProperty("to"), xa + ".to")));
                }
            GroupHead? head = null;
            if (g.TryGetProperty("head", out JsonElement headEl))
            {
                string ha = at + ".head";
                StudyCaseJson.Obj(headEl, ha, ["element", "bus", "index"], ["element"]);
                string element = StudyCaseJson.Str(headEl.GetProperty("element"), ha + ".element");
                if (element is not ("bus" or "branch" or "generator"))
                    throw new StudyCaseException($"{ha}.element must be bus, branch or generator, not \"{element}\".");
                head = new GroupHead(element,
                    headEl.TryGetProperty("bus", out JsonElement hb) ? StudyCaseJson.Int(hb, ha + ".bus") : null,
                    headEl.TryGetProperty("index", out JsonElement hi) ? StudyCaseJson.Int(hi, ha + ".index") : null);
            }
            groups.Add(new ModelGroup(StudyCaseJson.Id(g.GetProperty("id"), at + ".id"), kind,
                g.TryGetProperty("description", out JsonElement d) ? StudyCaseJson.Str(d, at + ".description") : null,
                buses, gens, branches, head));
        }
        return new ModelGroupSet(sha, groups);
    }
}

/// <summary>A group resolved against a model: the member positions in model order (a feeder keeps its listed order).</summary>
public sealed record BoundGroup(ModelGroup Group, IReadOnlyList<int> BusIds, IReadOnlyList<int> GeneratorIndices,
    IReadOnlyList<int> BranchIndices)
{
    public string Id => Group.Id;
}

/// <summary>A groups document bound to one model: every member resolved, duplicates and unresolved members refused.</summary>
public sealed class BoundGroups
{
    private readonly Dictionary<string, BoundGroup> _byId;

    private BoundGroups(ModelGroupSet set, IReadOnlyList<BoundGroup> groups)
    {
        Set = set;
        Groups = groups;
        _byId = groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
    }

    public ModelGroupSet Set { get; }
    public IReadOnlyList<BoundGroup> Groups { get; }
    public bool TryGet(string id, out BoundGroup group) => _byId.TryGetValue(id, out group!);
    public BoundGroup Get(string id) => _byId.TryGetValue(id, out BoundGroup? g) ? g
        : throw new StudyCaseException($"Group '{id}' is not defined in the groups document.");

    /// <summary>
    /// Resolve and check. Order: unique group IDs; each group names at least one member; every member resolves to the
    /// stated identity; a member appears once per group; the head of a feeder is one of its members (and only a feeder
    /// has a head); an exclusive kind (area, zone, owner) holds an element in at most one group. The first failure
    /// refuses the whole document, naming the group and the member.
    /// </summary>
    public static BoundGroups Bind(ModelGroupSet set, GridModel model)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(model);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var busPosition = new Dictionary<int, int>();
        for (int i = 0; i < model.Buses.Count; i++) busPosition.TryAdd(model.Buses[i].Id, i);
        var bound = new List<BoundGroup>();
        foreach (ModelGroup g in set.Groups)
        {
            if (!ids.Add(g.Id)) throw new StudyCaseException($"Duplicate group ID '{g.Id}'.");
            if (g.Buses.Count == 0 && g.Generators.Count == 0 && g.Branches.Count == 0)
                throw new StudyCaseException($"Group '{g.Id}' has no members.");
            if (g.Head is not null && g.Kind != GroupKind.Feeder)
                throw new StudyCaseException($"Group '{g.Id}': only a feeder has a head element.");
            if (g.Kind == GroupKind.Feeder && g.Head is null)
                throw new StudyCaseException($"Group '{g.Id}': a feeder needs a head element.");

            var seenBus = new HashSet<int>();
            foreach (int b in g.Buses)
            {
                if (!busPosition.ContainsKey(b))
                    throw new StudyCaseException($"Group '{g.Id}': bus {b} is not in the case.");
                if (!seenBus.Add(b)) throw new StudyCaseException($"Group '{g.Id}': bus {b} is listed twice.");
            }
            var seenGen = new HashSet<int>();
            foreach (GroupGenerator x in g.Generators)
            {
                if (x.Index < 0 || x.Index >= model.AllGenerators.Count)
                    throw new StudyCaseException($"Group '{g.Id}': generator index {x.Index} is outside the fleet (0..{model.AllGenerators.Count - 1}).");
                if (model.AllGenerators[x.Index].Bus != x.Bus)
                    throw new StudyCaseException($"Group '{g.Id}': generator {x.Index} is at bus {model.AllGenerators[x.Index].Bus} in the case, not {x.Bus}.");
                if (!seenGen.Add(x.Index)) throw new StudyCaseException($"Group '{g.Id}': generator {x.Index} is listed twice.");
            }
            var seenBranch = new HashSet<int>();
            foreach (GroupBranch x in g.Branches)
            {
                if (x.Index < 0 || x.Index >= model.Branches.Count)
                    throw new StudyCaseException($"Group '{g.Id}': branch index {x.Index} is outside the case (0..{model.Branches.Count - 1}).");
                Branch br = model.Branches[x.Index];
                if (br.From != x.From || br.To != x.To)
                    throw new StudyCaseException($"Group '{g.Id}': branch {x.Index} joins {br.From}-{br.To} in the case, not {x.From}-{x.To}.");
                if (!seenBranch.Add(x.Index)) throw new StudyCaseException($"Group '{g.Id}': branch {x.Index} is listed twice.");
            }
            if (g.Head is GroupHead h)
            {
                bool member = h.Element switch
                {
                    "bus" => h.Bus is int hb && h.Index is null && seenBus.Contains(hb),
                    "branch" => h.Index is int hi && h.Bus is null && seenBranch.Contains(hi),
                    _ => h.Index is int gi && h.Bus is null && seenGen.Contains(gi)
                };
                if (!member) throw new StudyCaseException($"Group '{g.Id}': the head ({h.Element}) is not a member of the group.");
            }

            bool ordered = g.Kind == GroupKind.Feeder;
            IReadOnlyList<int> busIds = ordered ? g.Buses.ToArray() : g.Buses.OrderBy(b => busPosition[b]).ToArray();
            IReadOnlyList<int> gens = ordered ? g.Generators.Select(x => x.Index).ToArray() : g.Generators.Select(x => x.Index).Order().ToArray();
            IReadOnlyList<int> brs = ordered ? g.Branches.Select(x => x.Index).ToArray() : g.Branches.Select(x => x.Index).Order().ToArray();
            bound.Add(new BoundGroup(g, busIds, gens, brs));
        }

        // Exclusive kinds: an element in two groups of one exclusive kind is refused, naming both groups.
        foreach (GroupKind kind in new[] { GroupKind.Area, GroupKind.Zone, GroupKind.Owner })
        {
            var busOwner = new Dictionary<int, string>();
            var genOwner = new Dictionary<int, string>();
            var branchOwner = new Dictionary<int, string>();
            foreach (BoundGroup g in bound.Where(x => x.Group.Kind == kind))
            {
                foreach (int b in g.BusIds)
                    if (!busOwner.TryAdd(b, g.Id))
                        throw new StudyCaseException($"Bus {b} is in both {ModelGroup.KindName(kind)} groups '{busOwner[b]}' and '{g.Id}'; a {ModelGroup.KindName(kind)} is exclusive.");
                foreach (int x in g.GeneratorIndices)
                    if (!genOwner.TryAdd(x, g.Id))
                        throw new StudyCaseException($"Generator {x} is in both {ModelGroup.KindName(kind)} groups '{genOwner[x]}' and '{g.Id}'; a {ModelGroup.KindName(kind)} is exclusive.");
                foreach (int x in g.BranchIndices)
                    if (!branchOwner.TryAdd(x, g.Id))
                        throw new StudyCaseException($"Branch {x} is in both {ModelGroup.KindName(kind)} groups '{branchOwner[x]}' and '{g.Id}'; a {ModelGroup.KindName(kind)} is exclusive.");
            }
        }
        return new BoundGroups(set, bound);
    }

    /// <summary>The in-fleet generator indices of a group: its own generator members, or, when it lists none, every
    /// unit (in fleet order) whose bus is a member bus.</summary>
    public static IReadOnlyList<int> GeneratorsOf(BoundGroup g, GridModel model)
    {
        if (g.GeneratorIndices.Count > 0) return g.GeneratorIndices;
        var buses = new HashSet<int>(g.BusIds);
        var result = new List<int>();
        for (int i = 0; i < model.AllGenerators.Count; i++)
            if (buses.Contains(model.AllGenerators[i].Bus)) result.Add(i);
        return result;
    }
}
