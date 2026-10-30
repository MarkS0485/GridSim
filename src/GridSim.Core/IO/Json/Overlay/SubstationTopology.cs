using System.Globalization;
using System.Text.Json;

namespace GridSim.Core.IO.Json.Overlay;

// =====================================================================================================================
// Phase 5c (2026-10-20): the substation topology of the asset overlay, and the implied-split / implied-coupling WARNING.
//
// A substation record (kind substation) holds busbar sections (busbarSection, each collapsing to one authored bus) and
// switching devices (switchingDevice: a breaker, disconnector or earth switch with its normal state). This file projects
// one substation's children into plain values: the sections, the devices and what each connects, the bays, and which
// sections are electrically ONE NODE when every device is in its normal state (normalOpen). It then compares that with
// the authored buses:
//   - sections that collapse to ONE bus but fall into two or more separate nodes: the switch states imply a bus split
//     the authored buses do not show;
//   - sections that collapse to DIFFERENT buses but are joined by closed devices: the switch states imply a coupling the
//     authored case does not model.
// Either is a WARNING, never an error: the overlay is carried, not used by the solver, so the case still loads and
// solves exactly as written. Nothing here changes V2AssetChecker.Check (its errors are as they were); the warnings are a
// separate list (V2AssetChecker.Warnings, or WarningsOf below).
//
// Pure and deterministic: the same records always give the same topology and the same warning texts (overlay order
// throughout, invariant digits). It reads records leniently (a missing or mistyped parameter is treated as described
// on each member below), because the editor shows the topology of an overlay that may be mid-edit; on a checked overlay
// every record has the shape the kind specs require.
// =====================================================================================================================

/// <summary>What a switching device is (the kind spec's "device" parameter).</summary>
public enum TopologyDeviceType
{
    /// <summary>A circuit breaker ("breaker"; also what a missing or unknown "device" value is drawn and counted as).</summary>
    Breaker,
    /// <summary>A disconnector (isolator).</summary>
    Disconnector,
    /// <summary>An earth switch: it closes onto earth, so it never joins two sections.</summary>
    EarthSwitch,
}

/// <summary>Where a switching device sits in the substation.</summary>
public enum TopologyDeviceRole
{
    /// <summary>Between two busbar sections of this substation (a bus coupler or bus section breaker): "from" and "to".</summary>
    Coupler,
    /// <summary>Between a section and the circuit it switches (a line, transformer, generator or demand).</summary>
    Feeder,
    /// <summary>An earth switch on a section (it joins nothing).</summary>
    Earth,
    /// <summary>On a section and switching nothing the overlay names (a spare bay device).</summary>
    Spare,
    /// <summary>Its "from" is not a busbar section of this substation, so it is listed but joins nothing here.</summary>
    Unplaced,
}

/// <summary>Which of the two topology warnings.</summary>
public enum TopologyWarningKind
{
    /// <summary>Sections of one authored bus fall into two or more nodes under the normal switch states.</summary>
    ImpliedSplit,
    /// <summary>Sections of different authored buses are joined into one node by closed devices.</summary>
    ImpliedCoupling,
}

/// <summary>The circuit a feeder device switches: a line, transformer, generator or demand by its name.</summary>
/// <param name="Target">The element type.</param>
/// <param name="Key">The name the reference gives.</param>
public sealed record TopologyFeeder(AssetRefTarget Target, string Key)
{
    /// <summary>The reference as a reader would write it ("line 'L1-2'"), as <see cref="AssetOverlayRef.Display"/>.</summary>
    public string Display => OverlayKindSet.TargetWord(Target) + " '" + Key + "'";
}

/// <summary>One busbar section of the substation.</summary>
/// <param name="Id">The section's record id.</param>
/// <param name="Bus">The authored bus it collapses to (its "bus" reference), or null when it names none.</param>
/// <param name="Group">The index into <see cref="SubstationTopology.Groups"/> of the node it is part of.</param>
public sealed record TopologySection(string Id, long? Bus, int Group)
{
    /// <summary>"bus 12", or "no bus".</summary>
    public string BusText => Bus is long b ? "bus " + b.ToString(CultureInfo.InvariantCulture) : "no bus";
}

/// <summary>One switching device of the substation, as written.</summary>
/// <param name="Id">The device's record id.</param>
/// <param name="Device">What it is.</param>
/// <param name="NormalOpen">Its normal state: true open, false closed, null when the record gives no boolean (an
/// unknown state, counted as NOT closed so it never joins anything).</param>
/// <param name="From">Its "from" section id, or null when it names none.</param>
/// <param name="To">Its "to" section id when it is a <see cref="TopologyDeviceRole.Coupler"/>, else null.</param>
/// <param name="Feeder">What it switches when it is a <see cref="TopologyDeviceRole.Feeder"/>, else null.</param>
/// <param name="Role">Where it sits.</param>
public sealed record TopologyDevice(string Id, TopologyDeviceType Device, bool? NormalOpen, string? From, string? To,
    TopologyFeeder? Feeder, TopologyDeviceRole Role)
{
    /// <summary>True only when the device is written normally CLOSED (normalOpen false).</summary>
    public bool IsClosed => NormalOpen == false;
}

/// <summary>
/// One bay: the devices between one section and one circuit, in overlay order (taken to be in series, so the bay
/// conducts only when every one of them is closed). A spare device or an earth switch is a bay of its own.
/// </summary>
/// <param name="Id">The bay's id: its first device's id.</param>
/// <param name="Section">The section it hangs from.</param>
/// <param name="Feeder">The circuit at its far end, or null (a spare bay or an earth switch).</param>
/// <param name="Devices">Its device ids, in overlay order.</param>
/// <param name="Closed">True when every device of the bay is closed.</param>
public sealed record TopologyBay(string Id, string Section, TopologyFeeder? Feeder, IReadOnlyList<string> Devices, bool Closed);

/// <summary>A set of sections that are electrically one node under the normal switch states.</summary>
/// <param name="Sections">Their ids, in overlay order.</param>
/// <param name="Buses">The distinct authored buses they collapse to, in the order first seen.</param>
public sealed record TopologyGroup(IReadOnlyList<string> Sections, IReadOnlyList<long> Buses);

/// <summary>
/// One topology warning (never an error: the case still loads and solves exactly as written).
/// </summary>
/// <param name="Kind">Split or coupling.</param>
/// <param name="Substation">The substation's record id.</param>
/// <param name="Document">The overlay file the substation is written in.</param>
/// <param name="Pointer">The substation record's JSON pointer in that file.</param>
/// <param name="Sections">The sections the warning is about, in overlay order.</param>
/// <param name="Devices">The devices it names (the open devices between the groups, or the closed devices that join),
/// in overlay order.</param>
/// <param name="Message">The full warning text.</param>
public sealed record SubstationTopologyWarning(TopologyWarningKind Kind, string Substation, string Document, string Pointer,
    IReadOnlyList<string> Sections, IReadOnlyList<string> Devices, string Message);

/// <summary>
/// The topology of one substation record of the asset overlay (Phase 5c): its busbar sections, its switching devices,
/// its bays, the nodes the sections form under the normal switch states, and the implied split / coupling warnings.
/// <para>Rules, in plain words:</para>
/// <list type="bullet">
/// <item>The substation's sections and devices are its DIRECT children (kinds busbarSection and switchingDevice), in
/// overlay order.</item>
/// <item>A device is an earth switch (joins nothing); else a coupler when its "to" is another section of this
/// substation; else a feeder device when it names a line, transformer, generator or demand (the first such role
/// written); else a spare. A device whose "from" is not a section of this substation is unplaced.</item>
/// <item>Feeder devices on the same section naming the same circuit are one bay, in series. A circuit reached from two
/// sections through closed bays joins those sections (a double busbar selector arrangement).</item>
/// <item>Two sections are one node when a chain of closed couplers and closed bays joins them (union-find).</item>
/// </list>
/// </summary>
public sealed class SubstationTopology
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The roles that name the circuit a feeder device switches, in the order they are looked for.</summary>
    public static readonly IReadOnlyList<string> FeederRoles = new[] { "line", "transformer", "generator", "demand" };

    /// <summary>The closing sentence every warning carries.</summary>
    public const string StillSolves = "This is a warning, not an error: the case still loads and solves exactly as written.";

    private readonly Dictionary<string, int> _sectionIndex;

    private SubstationTopology(string substation, string document, string pointer, IReadOnlyList<TopologySection> sections,
        IReadOnlyList<TopologyDevice> devices, IReadOnlyList<TopologyBay> bays, IReadOnlyList<TopologyGroup> groups,
        IReadOnlyList<SubstationTopologyWarning> warnings)
    {
        Substation = substation;
        Document = document;
        Pointer = pointer;
        Sections = sections;
        Devices = devices;
        Bays = bays;
        Groups = groups;
        Warnings = warnings;
        _sectionIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < sections.Count; i++) _sectionIndex.TryAdd(sections[i].Id, i);
    }

    /// <summary>The substation's record id.</summary>
    public string Substation { get; }

    /// <summary>The overlay file the substation is written in.</summary>
    public string Document { get; }

    /// <summary>The substation record's JSON pointer.</summary>
    public string Pointer { get; }

    /// <summary>The busbar sections, in overlay order.</summary>
    public IReadOnlyList<TopologySection> Sections { get; }

    /// <summary>The switching devices, in overlay order.</summary>
    public IReadOnlyList<TopologyDevice> Devices { get; }

    /// <summary>The bays (feeder, spare and earth), in the order of their first device.</summary>
    public IReadOnlyList<TopologyBay> Bays { get; }

    /// <summary>The nodes the sections form under the normal switch states, in the order of their first section.</summary>
    public IReadOnlyList<TopologyGroup> Groups { get; }

    /// <summary>The implied split and coupling warnings (empty for a consistent topology).</summary>
    public IReadOnlyList<SubstationTopologyWarning> Warnings { get; }

    /// <summary>The section with this id, or null.</summary>
    public TopologySection? Section(string id) => _sectionIndex.TryGetValue(id, out int i) ? Sections[i] : null;

    /// <summary>The device with this id, or null.</summary>
    public TopologyDevice? Device(string id) => Devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));

    /// <summary>The topology of every substation record in <paramref name="records"/>, in overlay order.</summary>
    public static IReadOnlyList<SubstationTopology> All(IReadOnlyList<AssetOverlayRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.Where(r => r.Kind == "substation").Select(s => Build(s, records)).ToList();
    }

    /// <summary>Every topology warning of a checked overlay, substation by substation in overlay order.</summary>
    public static IReadOnlyList<SubstationTopologyWarning> WarningsOf(CaseAssetOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        return All(overlay.Records).SelectMany(t => t.Warnings).ToList();
    }

    /// <summary>
    /// The topology of substation record <paramref name="substation"/>, whose children are found among
    /// <paramref name="records"/> (every record of the overlay, or at least the substation's children).
    /// </summary>
    public static SubstationTopology Build(AssetOverlayRecord substation, IReadOnlyList<AssetOverlayRecord> records)
    {
        ArgumentNullException.ThrowIfNull(substation);
        ArgumentNullException.ThrowIfNull(records);
        if (substation.Kind != "substation")
            throw new ArgumentException($"record '{substation.Id}' is a '{substation.Kind}', not a substation.", nameof(substation));

        List<AssetOverlayRecord> children = records.Where(r => string.Equals(r.Parent, substation.Id, StringComparison.Ordinal)).ToList();

        // ---- the sections (a repeated id keeps its first record: the checker refuses a repeat anyway) ----
        var sectionRecords = new List<AssetOverlayRecord>();
        var sectionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (AssetOverlayRecord r in children)
            if (r.Kind == "busbarSection" && sectionIds.Add(r.Id)) sectionRecords.Add(r);

        // ---- the devices, each with its role ----
        var devices = new List<TopologyDevice>();
        foreach (AssetOverlayRecord r in children.Where(c => c.Kind == "switchingDevice"))
        {
            TopologyDeviceType type = ParamString(r, "device") switch
            {
                "disconnector" => TopologyDeviceType.Disconnector,
                "earthSwitch" => TopologyDeviceType.EarthSwitch,
                _ => TopologyDeviceType.Breaker,
            };
            bool? normalOpen = ParamBool(r, "normalOpen");
            string? from = RecordRef(r, "from"), to = RecordRef(r, "to");
            TopologyFeeder? feeder = FeederOf(r);
            TopologyDeviceRole role;
            if (from is null || !sectionIds.Contains(from)) role = TopologyDeviceRole.Unplaced;
            else if (type == TopologyDeviceType.EarthSwitch) role = TopologyDeviceRole.Earth;
            else if (to is not null && to != from && sectionIds.Contains(to)) role = TopologyDeviceRole.Coupler;
            else if (feeder is not null) role = TopologyDeviceRole.Feeder;
            else role = TopologyDeviceRole.Spare;
            devices.Add(new TopologyDevice(r.Id, type, normalOpen, from,
                role == TopologyDeviceRole.Coupler ? to : null, role == TopologyDeviceRole.Feeder ? feeder : null, role));
        }

        // ---- the bays: feeder devices grouped by (section, circuit); a spare device or an earth switch on its own ----
        var bays = new List<(string Id, string Section, TopologyFeeder? Feeder, List<TopologyDevice> Devices)>();
        foreach (TopologyDevice d in devices)
        {
            if (d.Role is TopologyDeviceRole.Coupler or TopologyDeviceRole.Unplaced) continue;
            int at = d.Role == TopologyDeviceRole.Feeder
                ? bays.FindIndex(b => b.Feeder is not null && b.Section == d.From && b.Feeder == d.Feeder)
                : -1;
            if (at >= 0) bays[at].Devices.Add(d);
            else bays.Add((d.Id, d.From!, d.Feeder, new List<TopologyDevice> { d }));
        }
        List<TopologyBay> bayList = bays.Select(b => new TopologyBay(b.Id, b.Section, b.Feeder,
            b.Devices.Select(x => x.Id).ToList(), b.Devices.All(x => x.IsClosed))).ToList();

        // ---- the nodes: union-find over the sections (0..n-1) and the circuits (n..), through closed couplers and bays ----
        int n = sectionRecords.Count;
        var sectionIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++) sectionIndex[sectionRecords[i].Id] = i;
        var feederIndex = new Dictionary<TopologyFeeder, int>();
        foreach (TopologyBay b in bayList)
            if (b.Feeder is not null && !feederIndex.ContainsKey(b.Feeder)) feederIndex[b.Feeder] = n + feederIndex.Count;
        var uf = new UnionFind(n + feederIndex.Count);
        foreach (TopologyDevice d in devices)
            if (d.Role == TopologyDeviceRole.Coupler && d.IsClosed) uf.Union(sectionIndex[d.From!], sectionIndex[d.To!]);
        foreach (TopologyBay b in bayList)
            if (b.Feeder is not null && b.Closed) uf.Union(sectionIndex[b.Section], feederIndex[b.Feeder]);

        // Groups in the order of their first section; sections in overlay order within a group.
        var rootToGroup = new Dictionary<int, int>();
        var groupSections = new List<List<int>>();
        var sectionGroup = new int[n];
        for (int i = 0; i < n; i++)
        {
            int root = uf.Find(i);
            if (!rootToGroup.TryGetValue(root, out int g))
            {
                g = groupSections.Count;
                rootToGroup[root] = g;
                groupSections.Add(new List<int>());
            }
            groupSections[g].Add(i);
            sectionGroup[i] = g;
        }
        long?[] busOf = sectionRecords.Select(BusOf).ToArray();
        var sections = new List<TopologySection>(n);
        for (int i = 0; i < n; i++) sections.Add(new TopologySection(sectionRecords[i].Id, busOf[i], sectionGroup[i]));
        var groups = groupSections.Select(g => new TopologyGroup(
            g.Select(i => sectionRecords[i].Id).ToList(),
            g.Where(i => busOf[i] is not null).Select(i => busOf[i]!.Value).Distinct().ToList())).ToList();

        // ---- the warnings ----
        var warnings = new List<SubstationTopologyWarning>();
        string sub = substation.Id;

        // (1) Implied split: one authored bus, its sections in two or more nodes. Buses in the order first seen.
        foreach (long bus in busOf.Where(b => b is not null).Select(b => b!.Value).Distinct())
        {
            List<int> mine = Enumerable.Range(0, n).Where(i => busOf[i] == bus).ToList();
            List<int> theirGroups = mine.Select(i => sectionGroup[i]).Distinct().ToList();
            if (theirGroups.Count < 2) continue;
            var roots = new HashSet<int>(mine.Select(i => uf.Find(i)));

            // The open devices that lie between two of those nodes: an open coupler whose two sections are in different
            // ones of them, and each not-closed device of a bay whose section and circuit are in different ones of them.
            var between = new HashSet<string>(StringComparer.Ordinal);
            foreach (TopologyDevice d in devices)
                if (d.Role == TopologyDeviceRole.Coupler && !d.IsClosed)
                {
                    int a = uf.Find(sectionIndex[d.From!]), b = uf.Find(sectionIndex[d.To!]);
                    if (a != b && roots.Contains(a) && roots.Contains(b)) between.Add(d.Id);
                }
            foreach (TopologyBay b in bayList)
            {
                if (b.Feeder is null || b.Closed) continue;
                int a = uf.Find(sectionIndex[b.Section]), c = uf.Find(feederIndex[b.Feeder]);
                if (a != c && roots.Contains(a) && roots.Contains(c))
                    foreach (string id in b.Devices)
                        if (!devices.First(x => x.Id == id).IsClosed) between.Add(id);
            }
            List<string> named = devices.Where(d => between.Contains(d.Id)).Select(d => d.Id).ToList();
            string devText = named.Count switch
            {
                0 => "no switching device joins them",
                1 => "the open device " + Q(named[0]) + " lies between them",
                _ => "the open devices " + List(named.Select(Q)) + " lie between them",
            };
            string groupText = List(theirGroups.Select(g => "[" + string.Join(", ", groupSections[g].Where(i => busOf[i] == bus).Select(i => Q(sectionRecords[i].Id))) + "]"));
            List<string> sectionIdsOfBus = mine.Select(i => sectionRecords[i].Id).ToList();
            string message = "substation " + Q(sub) + ": busbar sections " + List(sectionIdsOfBus.Select(Q)) + " all collapse to bus "
                + bus.ToString(Inv) + ", but under the normal switch states they fall into " + theirGroups.Count.ToString(Inv)
                + " separate groups (" + groupText + "); " + devText + ". The switch states imply a bus split the authored buses do not show. "
                + StillSolves;
            warnings.Add(new SubstationTopologyWarning(TopologyWarningKind.ImpliedSplit, sub, substation.Document, substation.Pointer,
                sectionIdsOfBus, named, message));
        }

        // (2) Implied coupling: one node holding sections of two or more different authored buses.
        for (int g = 0; g < groups.Count; g++)
        {
            if (groups[g].Buses.Count < 2) continue;
            int root = uf.Find(groupSections[g][0]);

            // The closed devices doing the joining: every closed coupler inside the node, and the devices of every closed
            // bay into a circuit that two or more sections of the node reach through closed bays.
            var joining = new HashSet<string>(StringComparer.Ordinal);
            foreach (TopologyDevice d in devices)
                if (d.Role == TopologyDeviceRole.Coupler && d.IsClosed && uf.Find(sectionIndex[d.From!]) == root) joining.Add(d.Id);
            foreach (IGrouping<TopologyFeeder, TopologyBay> byFeeder in bayList.Where(b => b.Feeder is not null && b.Closed
                         && uf.Find(sectionIndex[b.Section]) == root).GroupBy(b => b.Feeder!))
                if (byFeeder.Select(b => b.Section).Distinct(StringComparer.Ordinal).Count() >= 2)
                    foreach (TopologyBay b in byFeeder)
                        foreach (string id in b.Devices) joining.Add(id);
            List<string> named = devices.Where(d => joining.Contains(d.Id)).Select(d => d.Id).ToList();
            string devText = named.Count switch
            {
                0 => "they are joined",
                1 => "the closed device " + Q(named[0]) + " joins them",
                _ => "the closed devices " + List(named.Select(Q)) + " join them",
            };
            List<int> members = groupSections[g];
            string message = "substation " + Q(sub) + ": busbar sections "
                + List(members.Select(i => Q(sectionRecords[i].Id) + " (" + sections[i].BusText + ")"))
                + " collapse to different buses, but under the normal switch states " + devText
                + " into one node. The switch states imply a coupling the authored case does not model. " + StillSolves;
            warnings.Add(new SubstationTopologyWarning(TopologyWarningKind.ImpliedCoupling, sub, substation.Document, substation.Pointer,
                members.Select(i => sectionRecords[i].Id).ToList(), named, message));
        }

        return new SubstationTopology(sub, substation.Document, substation.Pointer, sections, devices, bayList, groups, warnings);
    }

    // ---- small helpers ----

    private static string Q(string id) => "'" + id + "'";

    // "a", "a and b", "a, b and c" (EN-GB: no comma before the "and").
    private static string List(IEnumerable<string> items)
    {
        List<string> l = items.ToList();
        return l.Count switch
        {
            0 => "",
            1 => l[0],
            _ => string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1],
        };
    }

    // The bus a section collapses to: its "bus" reference to a bus, or null.
    private static long? BusOf(AssetOverlayRecord r) =>
        r.Refs.FirstOrDefault(x => x.Role == "bus" && x.Target == AssetRefTarget.Bus)?.Bus;

    // The record id a role names, or null when the role is absent or not a record reference.
    private static string? RecordRef(AssetOverlayRecord r, string role) =>
        r.Refs.FirstOrDefault(x => x.Role == role && x.Target == AssetRefTarget.Record)?.Key;

    // The first circuit role written (line, transformer, generator or demand) with the target type it should have.
    private static TopologyFeeder? FeederOf(AssetOverlayRecord r)
    {
        foreach (AssetOverlayRef x in r.Refs)
            if (FeederRoles.Contains(x.Role, StringComparer.Ordinal) && x.Key is string key && x.Target == OverlayKindSet.ParseTarget(x.Role))
                return new TopologyFeeder(x.Target, key);
        return null;
    }

    // A parameter's value, bare or in the {v, unit, prov} form.
    private static JsonElement? Param(AssetOverlayRecord r, string name)
    {
        if (r.Params.ValueKind != JsonValueKind.Object || !r.Params.TryGetProperty(name, out JsonElement v)) return null;
        return v.ValueKind == JsonValueKind.Object && v.TryGetProperty("v", out JsonElement inner) ? inner : v;
    }

    private static string? ParamString(AssetOverlayRecord r, string name) =>
        Param(r, name) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    private static bool? ParamBool(AssetOverlayRecord r, string name) => Param(r, name)?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    // Union-find with path halving and union by size (deterministic: the same unions in the same order give the same
    // roots; only "same set or not" is ever used, never which root).
    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _size;

        public UnionFind(int count)
        {
            _parent = new int[count];
            _size = new int[count];
            for (int i = 0; i < count; i++) { _parent[i] = i; _size[i] = 1; }
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _size[ra] += _size[rb];
        }
    }
}
