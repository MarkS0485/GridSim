using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Psse.Topology;

/// <summary>A connectivity node's native key: its substation and node number.</summary>
public readonly record struct PsseNodeKey(int Substation, int Node);

/// <summary>The declared policy of the supported-subset topology model builder.</summary>
public sealed record PsseTopologyModelOptions
{
    /// <summary>
    /// A closed substation switching device is contracted (its two nodes become one electrical bus) only when R, the
    /// charging and both end shunts are exactly zero and |X| is at or below this threshold, in pu on the system base.
    /// PSS/E RAW requires a finite non-zero switch X (default 0.0001 pu, which is also PSS/E's default zero-impedance
    /// threshold THRSHZ); the caller states the threshold explicitly so the contraction is a declared policy, not an
    /// inference. The native graph keeps the switch's finite X and ratings unchanged; only the bus grouping treats it as
    /// ideal. A closed switch above the threshold is refused: finite switch equipment is not yet modelled.
    /// </summary>
    public required double ZeroImpedanceThresholdPu { get; init; }
}

public enum PsseTopologyRefusalKind
{
    /// <summary>An equipment class this builder has no exact map for (HVDC, FACTS, GNE, induction machines).</summary>
    UnsupportedEquipmentClass,
    /// <summary>An automatic control this builder cannot rebind (OLTC or other transformer control, SVC or switched
    /// shunt control, remote or node voltage regulation, or a model control the parser produced).</summary>
    UnsupportedControl,
    /// <summary>A substation terminal of a type with no source-record binding (D, V, N, A, I).</summary>
    UnsupportedTerminal,
    /// <summary>A terminal whose source record is missing or ambiguous.</summary>
    UnresolvedTerminal,
    /// <summary>An equipment end on a split bus that no terminal row places on a bus section.</summary>
    UnplacedEquipmentEnd,
    /// <summary>A closed switch that is not within the declared zero-impedance policy.</summary>
    UnsupportedSwitch,
    /// <summary>A swing bus split so that the reference cannot be assigned to exactly one energised section.</summary>
    SwingBusSplit,
    /// <summary>A record whose meaning would change when buses are added (three-winding star numbering, multi-section
    /// line groupings) or which names a split bus as a reference (area swing bus).</summary>
    SplitBusReference,
    /// <summary>A closed switch joins nodes of two different native buses; merging bus-branch buses is not mapped.</summary>
    BusMerge,
    /// <summary>The incumbent parser refused the projected case.</summary>
    ProjectedCaseInvalid,
}

/// <summary>One explicit, fail-closed refusal with the source line (0 when the refusal has no single line).</summary>
public sealed record PsseTopologyRefusal(PsseTopologyRefusalKind Kind, int SourceLine, string Section, string Detail);

public enum PsseSectionState
{
    /// <summary>The native bus is not split; the bus record is used unchanged.</summary>
    Unsplit,
    /// <summary>A section of a split native bus with an in-service node and an in-service branch end.</summary>
    Energised,
    /// <summary>A section with no in-service node, or (for a split bus) no in-service branch end. It is written as an
    /// isolated (IDE 4) bus so the incumbent parser drops it and everything attached to it, with its notes.</summary>
    DeEnergised,
}

/// <summary>One electrical bus section of a native bus, and the model bus that represents it.</summary>
/// <param name="ModelBusId">The bus id in the projected model. The first section of a native bus (lowest substation
/// and node number) keeps the native id; further sections are numbered above every native bus id.</param>
/// <param name="NativeBus">The native RAW bus number the section belongs to.</param>
/// <param name="Nodes">The section's connectivity nodes in (substation, node) order.</param>
/// <param name="State">Whether the section is represented in the solve.</param>
/// <param name="BusType">The IDE written for the section (4 for a de-energised section).</param>
public sealed record PsseTopologyModelBus(int ModelBusId, int NativeBus, IReadOnlyList<PsseNodeKey> Nodes,
    PsseSectionState State, int BusType)
{
    public bool InModel => State != PsseSectionState.DeEnergised;
}

public enum PsseTerminalModelState
{
    /// <summary>The terminal's equipment is attached to an energised model bus.</summary>
    Connected,
    /// <summary>The terminal sits on a de-energised section; its equipment is absent from the solve.</summary>
    SectionDeEnergised,
    /// <summary>The terminal sits on an energised section but its equipment is out of service or omitted by the
    /// incumbent model (for example a branch whose other end is de-energised).</summary>
    EquipmentNotInModel,
}

/// <summary>Where one native terminal row lands in the projected model and why.</summary>
public sealed record PsseTerminalPlacement(PsseTopologyTerminal Terminal, int? ModelBusId,
    PsseTerminalModelState State, string Detail);

/// <summary>
/// The outcome of one projection: either a <see cref="GridModel"/> with complete forward and reverse maps, or an
/// explicit list of refusals and no model.
/// </summary>
public sealed record PsseTopologyModelResult(
    string SourceSha256,
    IReadOnlyDictionary<PsseSwitchKey, bool> SwitchStates,
    IReadOnlyList<PsseTopologyRefusal> Refusals,
    GridModel? Model,
    PsseParseResult? ProjectedParse,
    string? ProjectedRawText,
    string? ProjectedRawSha256,
    IReadOnlyList<PsseTopologyModelBus> Buses,
    IReadOnlyDictionary<PsseNodeKey, int?> NodeToModelBus,
    IReadOnlyList<PsseTerminalPlacement> Terminals,
    IReadOnlyList<PsseBranchKey> BranchSources,
    IReadOnlyList<PsseMachineKey> MachineSources)
{
    public bool Built => Model is not null;

    /// <summary>The native bus a model bus id represents (model ids that are native ids map to themselves).</summary>
    public int NativeBusOf(int modelBusId) =>
        Buses.FirstOrDefault(b => b.ModelBusId == modelBusId)?.NativeBus ?? modelBusId;
}

/// <summary>
/// Supported-subset builder from the projected node-breaker topology to a solvable <see cref="GridModel"/>.
/// <para><b>Sequence.</b> (1) Project connectivity through an explicit state for every switch
/// (<see cref="PsseSubstationProjection"/>). (2) Refuse, fail closed, every equipment class and control the builder has
/// no exact map for, and every closed switch outside the declared zero-impedance policy. (3) Form bus sections: the
/// occupied connectivity groups of each native bus, in (substation, node) order. A native bus with one occupied group is
/// unsplit and its records are used unchanged. (4) Place every equipment end on a split bus through its terminal row;
/// an end that no terminal places is refused. (5) Write a projected bus-branch RAW text: the source text with the
/// substation data removed, each section after the first added as a bus record directly after its native bus record,
/// and only the bus fields of placed records rewritten. (6) Parse that text with the incumbent
/// <see cref="PsseRawParser"/> and default options, so every per-record conversion and every bus-level sum is the
/// incumbent's own, in file order. (7) Map model branches and machines back to their native keys.</para>
/// <para>When no bus is split (for example all switches closed) the projected text differs from the source only by the
/// substation data, which the incumbent parser reads past; the model is then the incumbent model.</para>
/// </summary>
public static class PsseTopologyModelBuilder
{
    private const int BranchStatusIndex = 23, GeneratorIregIndex = 7, GeneratorNregIndex = 8, GeneratorStatusIndex = 15,
        TransformerCodIndex = 15, SwitchedShuntModswIndex = 2, SwitchedShuntStatusIndex = 4, BusIdeIndex = 3, AreaSwingIndex = 1;

    /// <summary>The switch states the source file records (STATUS of each switching device).</summary>
    public static IReadOnlyDictionary<PsseSwitchKey, bool> SourceStates(PsseSubstationTopology topology) =>
        topology.Switches.ToDictionary(PsseSubstationProjection.Key, sw => sw.Closed);

    /// <summary>Every switch closed: the state equivalent to the bus-branch case.</summary>
    public static IReadOnlyDictionary<PsseSwitchKey, bool> AllClosed(PsseSubstationTopology topology) =>
        topology.Switches.ToDictionary(PsseSubstationProjection.Key, _ => true);

    /// <summary>A copy of <paramref name="states"/> with the named switches set.</summary>
    public static IReadOnlyDictionary<PsseSwitchKey, bool> With(IReadOnlyDictionary<PsseSwitchKey, bool> states,
        params (PsseSwitchKey Key, bool Closed)[] changes)
    {
        var copy = states.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach ((PsseSwitchKey key, bool closed) in changes)
        {
            if (!copy.ContainsKey(key))
                throw new ArgumentException($"Unknown switch: substation {key.Substation}, nodes {key.FromNode}-{key.ToNode}, circuit '{key.CircuitId}'.", nameof(changes));
            copy[key] = closed;
        }
        return copy;
    }

    public static PsseTopologyModelResult Build(PsseSubstationCase source, IReadOnlyDictionary<PsseSwitchKey, bool> switchStates,
        PsseTopologyModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(switchStates);
        ArgumentNullException.ThrowIfNull(options);
        if (!(double.IsFinite(options.ZeroImpedanceThresholdPu) && options.ZeroImpedanceThresholdPu >= 0))
            throw new ArgumentOutOfRangeException(nameof(options), options.ZeroImpedanceThresholdPu, "The zero-impedance threshold must be finite and non-negative.");
        if (string.IsNullOrEmpty(source.SourceText))
            throw new ArgumentException("The case carries no source text; read it with PsseSubstationCaseReader.", nameof(source));

        PsseParseResult parsed = source.Parsed;
        PsseSubstationTopology topology = source.Topology;
        PsseSubstationConnectivityResult connectivity = PsseSubstationProjection.Project(topology, switchStates);
        PsseRawLayout layout = PsseRawLayout.Read(source.SourceText, parsed.Revision);
        var refusals = new List<PsseTopologyRefusal>();
        var bindingByLine = source.TerminalBindings.ToDictionary(b => b.Terminal.SourceLine);

        // --- Step 2: classes and controls with no exact map --------------------------------------------------------
        foreach (string section in new[] { PsseRawLayout.TwoTerminalDc, PsseRawLayout.VscDc, PsseRawLayout.MultiTerminalDc,
                     PsseRawLayout.Facts, PsseRawLayout.Gne, PsseRawLayout.Induction })
        {
            PsseRawLine[] rows = layout.DataLines.Where(l => l.Section == section).ToArray();
            if (rows.Length > 0)
                refusals.Add(new(PsseTopologyRefusalKind.UnsupportedEquipmentClass, rows[0].Line, section,
                    $"{rows.Length} line(s) of {section} data; the topology model builder has no exact map for this class, in or out of service."));
        }
        foreach (PsseTerminalBinding binding in source.TerminalBindings)
        {
            if (binding.Status == PsseTerminalBindingStatus.UnsupportedEquipmentType)
                refusals.Add(new(PsseTopologyRefusalKind.UnsupportedTerminal, binding.Terminal.SourceLine, PsseRawLayout.Substation,
                    $"Terminal type '{binding.Terminal.EquipmentType}' at bus {binding.Terminal.Bus}, node {binding.Terminal.Node}: {binding.Detail}"));
            else if (binding.Status is PsseTerminalBindingStatus.MissingSourceRecord or PsseTerminalBindingStatus.AmbiguousSourceRecord)
                refusals.Add(new(PsseTopologyRefusalKind.UnresolvedTerminal, binding.Terminal.SourceLine, PsseRawLayout.Substation,
                    $"Terminal type '{binding.Terminal.EquipmentType}' at bus {binding.Terminal.Bus}, node {binding.Terminal.Node}: {binding.Detail}"));
        }
        foreach (PsseTopologyTerminal terminal in connectivity.UnmappedTerminals)
            refusals.Add(new(PsseTopologyRefusalKind.UnresolvedTerminal, terminal.SourceLine, PsseRawLayout.Substation,
                $"Terminal at bus {terminal.Bus}, node {terminal.Node} maps to no single connectivity node."));
        foreach (PsseEquipmentSource equipment in parsed.EquipmentSources.Where(e => e.InService))
        {
            if (equipment.Kind == "transformer")
                foreach (PsseEquipmentSourceRow row in equipment.SourceRows.Skip(2))
                    if (IntField(row.Fields, TransformerCodIndex, 0) != 0)
                        refusals.Add(new(PsseTopologyRefusalKind.UnsupportedControl, row.SourceLine, "TRANSFORMER",
                            $"Transformer {equipment.Bus}-{equipment.ToBus} circuit '{equipment.Id}' has automatic control COD {IntField(row.Fields, TransformerCodIndex, 0)}; tap and phase controls (OLTC) are not rebound by the topology model builder."));
            if (equipment.Kind == "switched shunt" && IntField(equipment.Fields, SwitchedShuntModswIndex, 1) != 0)
                refusals.Add(new(PsseTopologyRefusalKind.UnsupportedControl, equipment.SourceLine, "SWITCHED SHUNT",
                    $"Switched shunt at bus {equipment.Bus} has control mode MODSW {IntField(equipment.Fields, SwitchedShuntModswIndex, 1)}; only a locked (MODSW 0) switched shunt is mapped (fixed at BINIT)."));
            if (equipment.Kind == "machine")
            {
                int ireg = IntField(equipment.Fields, GeneratorIregIndex, 0), nreg = IntField(equipment.Fields, GeneratorNregIndex, 0);
                if ((ireg != 0 && ireg != equipment.Bus) || nreg != 0)
                    refusals.Add(new(PsseTopologyRefusalKind.UnsupportedControl, equipment.SourceLine, "GENERATOR",
                        $"Machine '{equipment.Id}' at bus {equipment.Bus} regulates bus {ireg}, node {nreg}; remote and node voltage regulation are not rebound by the topology model builder."));
            }
        }
        GridModel legacy = parsed.Model;
        if (legacy.Oltcs.Count + legacy.LccLinks.Count + legacy.VscLinks.Count + legacy.MtdcLinks.Count + legacy.FactsDevices.Count +
            legacy.StaticVarCompensators.Count + legacy.InductionMachines.Count + legacy.TransformerFlowControls.Count + parsed.SwitchedShunts.Count > 0)
            refusals.Add(new(PsseTopologyRefusalKind.UnsupportedControl, 0, "MODEL",
                "The incumbent model of the source carries a controlled device or DC link; the topology model builder has no exact map for it."));
        foreach (PsseTopologySwitch sw in topology.Switches)
        {
            PsseSwitchKey key = PsseSubstationProjection.Key(sw);
            if (!connectivity.EffectiveSwitchStates[key]) continue;
            bool contractible = sw.ResistancePu == 0 && sw.ChargingSusceptancePu == 0 && sw.FromConductancePu == 0 &&
                sw.FromSusceptancePu == 0 && sw.ToConductancePu == 0 && sw.ToSusceptancePu == 0 &&
                Math.Abs(sw.ReactancePu) <= options.ZeroImpedanceThresholdPu;
            if (!contractible)
                refusals.Add(new(PsseTopologyRefusalKind.UnsupportedSwitch, sw.SourceLine, PsseRawLayout.Substation,
                    $"Closed switch {sw.FromNode}-{sw.ToNode} '{sw.CircuitId}' in substation {sw.Substation} (X {sw.ReactancePu.ToString("R", CultureInfo.InvariantCulture)} pu) is outside the declared zero-impedance policy ({options.ZeroImpedanceThresholdPu.ToString("R", CultureInfo.InvariantCulture)} pu, R and shunts zero); finite switch equipment is not modelled."));
        }

        foreach (PsseConnectivityGroup merged in connectivity.Groups.Where(g => g.NativeBusIds.Count > 1))
            refusals.Add(new(PsseTopologyRefusalKind.BusMerge, merged.Nodes[0].SourceLine, PsseRawLayout.Substation,
                $"Closed switches join nodes of buses {string.Join(", ", merged.NativeBusIds)} into one electrical bus; merging bus-branch buses is not mapped by the topology model builder."));

        // --- Step 3: bus sections -------------------------------------------------------------------------------
        var groupById = connectivity.Groups.ToDictionary(g => g.Id);
        var terminalsByGroup = topology.Terminals.Where(t => connectivity.TerminalLineToConnectivityGroup.ContainsKey(t.SourceLine))
            .GroupBy(t => connectivity.TerminalLineToConnectivityGroup[t.SourceLine]).ToDictionary(g => g.Key, g => g.ToArray());
        var busRecordLine = layout.DataLines.Where(l => l.Section == PsseRawLayout.Bus)
            .ToDictionary(l => IntField(l.Fields, 0, 0), l => l);
        int nextBusId = checked(parsed.RawBusIds.DefaultIfEmpty(0).Max() + 1);
        var buses = new List<PsseTopologyModelBus>();
        var groupToModelBus = new Dictionary<int, PsseTopologyModelBus>();
        var splitBuses = new HashSet<int>();
        var cutButUnsplit = new HashSet<int>();   // IO-21: cut by an open switch, yet fewer than two groups hold terminal rows
        var nativeIdeChange = new Dictionary<int, int>();
        var addedBuses = new Dictionary<int, List<PsseTopologyModelBus>>();
        var nodeToBus = new Dictionary<PsseNodeKey, int?>();
        foreach (int native in parsed.RawBusIds)
        {
            if (!connectivity.NativeBusToConnectivityGroups.TryGetValue(native, out IReadOnlyList<int>? groupIds)) continue;
            PsseConnectivityGroup[] groups = groupIds.Select(id => groupById[id]).ToArray();
            PsseConnectivityGroup[] occupied = groups.Where(g => terminalsByGroup.ContainsKey(g.Id)).ToArray();
            int nativeIde = IntField(busRecordLine[native].Fields, BusIdeIndex, 1);
            if (occupied.Length <= 1)
            {
                if (groups.Count(g => terminalsByGroup.ContainsKey(g.Id) || g.Nodes.Any(n => n.InService)) > 1) cutButUnsplit.Add(native);
                PsseConnectivityGroup? only = occupied.Length == 1 ? occupied[0] : groups.FirstOrDefault(g => g.Nodes.Any(n => n.InService));
                bool alive = only is not null && only.Nodes.Any(n => n.InService);
                PsseSectionState state = alive ? PsseSectionState.Unsplit : PsseSectionState.DeEnergised;
                int ide = alive ? nativeIde : 4;
                if (!alive && nativeIde != 4) nativeIdeChange[native] = 4;
                var bus = new PsseTopologyModelBus(native, native,
                    Array.AsReadOnly((only?.Nodes ?? groups[0].Nodes).Select(n => new PsseNodeKey(n.Substation, n.Number)).ToArray()), state, ide);
                buses.Add(bus);
                foreach (PsseConnectivityGroup g in groups)
                {
                    bool member = only is not null && g.Id == only.Id;
                    if (member) groupToModelBus[g.Id] = bus;
                    foreach (PsseTopologyNode n in g.Nodes)
                        nodeToBus[new PsseNodeKey(n.Substation, n.Number)] = member && alive ? native : null;
                }
                continue;
            }

            splitBuses.Add(native);
            var sections = new List<(PsseConnectivityGroup Group, int Id, bool Alive, bool HasMachines)>();
            for (int s = 0; s < occupied.Length; s++)
            {
                PsseConnectivityGroup g = occupied[s];
                PsseTopologyTerminal[] terms = terminalsByGroup[g.Id];
                bool inServiceNode = g.Nodes.Any(n => n.InService);
                bool branchEnd = terms.Any(t => t.EquipmentType is "B" or "2" or "3" &&
                    bindingByLine.TryGetValue(t.SourceLine, out PsseTerminalBinding? b) && b.Status == PsseTerminalBindingStatus.BoundToBranch);
                bool machines = terms.Any(t => t.EquipmentType == "M" && bindingByLine.TryGetValue(t.SourceLine, out PsseTerminalBinding? b) &&
                    b.Status == PsseTerminalBindingStatus.BoundToMachine && b.GeneratorIndex is int gi && legacy.AllGenerators[gi].InService);
                int id = s == 0 ? native : nextBusId++;
                sections.Add((g, id, inServiceNode && branchEnd, machines));
            }
            int liveWithMachines = sections.Count(s => s.Alive && s.HasMachines);
            if (nativeIde == 3 && liveWithMachines != 1)
                refusals.Add(new(PsseTopologyRefusalKind.SwingBusSplit, busRecordLine[native].Line, PsseRawLayout.Bus,
                    $"Swing bus {native} splits into {sections.Count} sections with {liveWithMachines} energised section(s) holding an in-service machine; the reference needs exactly one."));
            foreach (var section in sections)
            {
                int ide = !section.Alive ? 4
                    : nativeIde == 4 ? 4
                    : nativeIde == 3 ? (section.HasMachines ? 3 : 1)
                    : nativeIde == 2 ? (section.HasMachines ? 2 : 1)
                    : 1;
                var bus = new PsseTopologyModelBus(section.Id, native,
                    Array.AsReadOnly(section.Group.Nodes.Select(n => new PsseNodeKey(n.Substation, n.Number)).ToArray()),
                    section.Alive && nativeIde != 4 ? PsseSectionState.Energised : PsseSectionState.DeEnergised, ide);
                buses.Add(bus);
                groupToModelBus[section.Group.Id] = bus;
                if (section.Id == native) { if (ide != nativeIde) nativeIdeChange[native] = ide; }
                else
                {
                    if (!addedBuses.TryGetValue(native, out List<PsseTopologyModelBus>? added)) addedBuses[native] = added = new();
                    added.Add(bus);
                }
            }
            foreach (PsseConnectivityGroup g in groups)
                foreach (PsseTopologyNode n in g.Nodes)
                    nodeToBus[new PsseNodeKey(n.Substation, n.Number)] =
                        groupToModelBus.TryGetValue(g.Id, out PsseTopologyModelBus? b) && b.InModel ? b.ModelBusId : null;
        }

        if (splitBuses.Count > 0)
        {
            PsseEquipmentSource? three = parsed.EquipmentSources.FirstOrDefault(e => e.EquipmentType == "3");
            if (three is not null)
                refusals.Add(new(PsseTopologyRefusalKind.SplitBusReference, three.SourceLine, "TRANSFORMER",
                    "A three-winding transformer is present while a bus is split; its star-point numbering depends on the highest bus number, so added sections could renumber it."));
            PsseRawLine? multi = layout.DataLines.FirstOrDefault(l => l.Section == PsseRawLayout.MultiSection);
            if (multi is not null)
                refusals.Add(new(PsseTopologyRefusalKind.SplitBusReference, multi.Line, PsseRawLayout.MultiSection,
                    "A multi-section line grouping is present while a bus is split; its bus references are not rebound."));
            foreach (PsseRawLine area in layout.DataLines.Where(l => l.Section == PsseRawLayout.Area))
                if (splitBuses.Contains(IntField(area.Fields, AreaSwingIndex, 0)))
                    refusals.Add(new(PsseTopologyRefusalKind.SplitBusReference, area.Line, PsseRawLayout.Area,
                        $"Area {IntField(area.Fields, 0, 0)} names split bus {IntField(area.Fields, AreaSwingIndex, 0)} as its swing bus."));
        }

        // --- Step 4: place every equipment end on a split bus through its terminal row -----------------------------
        var equipmentByEnds = parsed.EquipmentSources.GroupBy(e => EndsKey(e.EquipmentType, EquipmentBuses(e)))
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var rewrites = new Dictionary<int, Dictionary<int, int>>();   // source line -> (field index -> model bus id)
        var placed = new Dictionary<(int Line, int Field), int>();
        var terminalPlacements = new List<PsseTerminalPlacement>();
        foreach (PsseTopologyTerminal terminal in topology.Terminals)
        {
            if (!connectivity.TerminalLineToConnectivityGroup.TryGetValue(terminal.SourceLine, out int groupId) ||
                !groupToModelBus.TryGetValue(groupId, out PsseTopologyModelBus? section))
            {
                PsseConnectivityGroup? g = connectivity.TerminalLineToConnectivityGroup.TryGetValue(terminal.SourceLine, out int gid) ? groupById[gid] : null;
                terminalPlacements.Add(new(terminal, null, PsseTerminalModelState.SectionDeEnergised,
                    g is null ? "The terminal maps to no connectivity node." : "The terminal's node is in no modelled section."));
                continue;
            }
            bindingByLine.TryGetValue(terminal.SourceLine, out PsseTerminalBinding? binding);
            if (!section.InModel)
                terminalPlacements.Add(new(terminal, null, PsseTerminalModelState.SectionDeEnergised,
                    $"Node {terminal.Node} is in a de-energised section of bus {section.NativeBus}; the equipment is absent from the solve and is not served."));
            else if (binding is null || binding.Status is not (PsseTerminalBindingStatus.BoundToBranch or PsseTerminalBindingStatus.BoundToMachine or
                         PsseTerminalBindingStatus.BoundToLoadRecord or PsseTerminalBindingStatus.BoundToShuntRecord))
                terminalPlacements.Add(new(terminal, section.ModelBusId, PsseTerminalModelState.EquipmentNotInModel,
                    binding?.Detail ?? "No binding."));
            else
                terminalPlacements.Add(new(terminal, section.ModelBusId, PsseTerminalModelState.Connected, binding.Detail));

            bool cutOnly = !splitBuses.Contains(terminal.Bus) && cutButUnsplit.Contains(terminal.Bus);
            if (!splitBuses.Contains(terminal.Bus) && !cutOnly) continue;
            PsseEquipmentSource[] candidates = equipmentByEnds.TryGetValue(EndsKey(terminal.EquipmentType, TerminalBuses(terminal)), out var near)
                ? near.Where(e => SameEquipment(e, terminal)).ToArray() : Array.Empty<PsseEquipmentSource>();
            if (cutOnly)
            {
                // Placement is only recorded (no rewrite): the bus keeps its number, but every end must be named by a row.
                if (candidates.Length == 1)
                    foreach (int field in EndFields(candidates[0], terminal.Bus)) placed[(candidates[0].SourceLine, field)] = section.ModelBusId;
                continue;
            }
            if (candidates.Length != 1)
            {
                refusals.Add(new(PsseTopologyRefusalKind.UnresolvedTerminal, terminal.SourceLine, PsseRawLayout.Substation,
                    $"Terminal type '{terminal.EquipmentType}' at bus {terminal.Bus}, node {terminal.Node} matches {candidates.Length} source records; exactly one is required to place it."));
                continue;
            }
            PsseEquipmentSource equipment = candidates[0];
            foreach (int field in EndFields(equipment, terminal.Bus))
            {
                if (placed.TryGetValue((equipment.SourceLine, field), out int prior) && prior != section.ModelBusId)
                {
                    refusals.Add(new(PsseTopologyRefusalKind.UnresolvedTerminal, terminal.SourceLine, PsseRawLayout.Substation,
                        $"Two terminal rows place the same end of the record at line {equipment.SourceLine} on different sections."));
                    continue;
                }
                placed[(equipment.SourceLine, field)] = section.ModelBusId;
                if (!rewrites.TryGetValue(equipment.SourceLine, out Dictionary<int, int>? fields)) rewrites[equipment.SourceLine] = fields = new();
                fields[field] = section.ModelBusId;
            }
            if (equipment.Kind == "machine" && IntField(equipment.Fields, GeneratorIregIndex, 0) == equipment.Bus)
                rewrites[equipment.SourceLine][GeneratorIregIndex] = section.ModelBusId;
        }
        foreach (PsseEquipmentSource equipment in parsed.EquipmentSources)
            foreach (int bus in EquipmentBuses(equipment).Where(b => splitBuses.Contains(b) || cutButUnsplit.Contains(b)).Distinct())
                foreach (int field in EndFields(equipment, bus))
                    if (!placed.ContainsKey((equipment.SourceLine, field)))
                        refusals.Add(new(PsseTopologyRefusalKind.UnplacedEquipmentEnd, equipment.SourceLine, equipment.Kind.ToUpperInvariant(),
                            $"The {equipment.Kind} '{equipment.Id}' end at split bus {bus} has no terminal row; it cannot be placed on a section."));

        var states = new System.Collections.ObjectModel.ReadOnlyDictionary<PsseSwitchKey, bool>(
            switchStates.ToDictionary(kv => kv.Key, kv => kv.Value));
        var nodeMap = new System.Collections.ObjectModel.ReadOnlyDictionary<PsseNodeKey, int?>(nodeToBus);
        PsseTopologyModelBus[] busArray = buses.ToArray();
        PsseTerminalPlacement[] placementArray = terminalPlacements.ToArray();
        PsseTopologyModelResult Refused() => new(source.SourceSha256, states,
            Array.AsReadOnly(refusals.OrderBy(r => r.SourceLine).ThenBy(r => r.Kind).ToArray()), null, null, null, null,
            Array.AsReadOnly(busArray), nodeMap, Array.AsReadOnly(placementArray),
            Array.Empty<PsseBranchKey>(), Array.Empty<PsseMachineKey>());
        if (refusals.Count > 0) return Refused();

        // --- Step 5: projected bus-branch text --------------------------------------------------------------------
        var sb = new StringBuilder();
        string[] lines = layout.Lines;
        var dataByLine = layout.DataLines.ToDictionary(l => l.Line);
        for (int line = 1; line <= lines.Length; line++)
        {
            if (layout.SubstationFirstLine is int first && line >= first && line < layout.SubstationTerminatorLine) continue;
            string text = lines[line - 1];
            dataByLine.TryGetValue(line, out PsseRawLine? data);
            PsseRawLine? busLine = data is not null && data.Section == PsseRawLayout.Bus ? data : null;
            if (rewrites.TryGetValue(line, out Dictionary<int, int>? fields))
                text = PsseRawLayout.FormatRecord(Rewrite(data!.Fields, fields));
            else if (busLine is not null && nativeIdeChange.TryGetValue(IntField(busLine.Fields, 0, 0), out int ide))
                text = PsseRawLayout.FormatRecord(Rewrite(busLine.Fields, new Dictionary<int, int> { [BusIdeIndex] = ide }));
            sb.Append(text).Append('\n');
            if (busLine is not null && addedBuses.TryGetValue(IntField(busLine.Fields, 0, 0), out List<PsseTopologyModelBus>? added))
                foreach (PsseTopologyModelBus bus in added)
                    sb.Append(PsseRawLayout.FormatRecord(Rewrite(busLine.Fields, new Dictionary<int, int> { [0] = bus.ModelBusId, [BusIdeIndex] = bus.BusType })))
                      .Append('\n');
        }
        string projectedText = sb.ToString();
        if (projectedText.EndsWith('\n') && !source.SourceText.EndsWith('\n')) projectedText = projectedText[..^1];

        // --- Step 6: the incumbent parser builds the model ------------------------------------------------------
        PsseParseResult projected;
        try { projected = PsseRawParser.ParseWithDiagnostics(projectedText); }
        catch (InvalidDataException error)
        {
            refusals.Add(new(PsseTopologyRefusalKind.ProjectedCaseInvalid, 0, "PROJECTED", error.Message));
            return Refused();
        }

        // --- Step 7: reverse maps -----------------------------------------------------------------------------------
        var nativeOfModelBus = busArray.ToDictionary(b => b.ModelBusId, b => b.NativeBus);
        int Native(int id) => nativeOfModelBus.TryGetValue(id, out int n) ? n : id;
        PsseBranchKey[] branchSources = projected.BranchKeys.Select(k => k with
        {
            FromBus = Native(k.FromBus), ToBus = Native(k.ToBus), ThirdBus = k.ThirdBus == 0 ? 0 : Native(k.ThirdBus)
        }).ToArray();
        PsseMachineKey[] machineSources = projected.MachineKeys.Select(k => k with { Bus = Native(k.Bus) }).ToArray();

        // A placement on an energised section whose equipment the projected model dropped (for example a branch whose
        // far end is de-energised) is reported as not in the model rather than connected.
        var modelBranches = branchSources.GroupBy(k => BranchEndsKey(k.FromBus, k.ToBus, k.ThirdBus, k.Circuit))
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        for (int i = 0; i < placementArray.Length; i++)
        {
            PsseTerminalPlacement p = placementArray[i];
            if (p.State != PsseTerminalModelState.Connected || p.Terminal.EquipmentType is not ("B" or "2" or "3")) continue;
            bool present = modelBranches.TryGetValue(BranchEndsKey(p.Terminal.Bus, p.Terminal.ToBus ?? 0, p.Terminal.ThirdBus ?? 0, p.Terminal.CircuitId ?? ""),
                out PsseBranchKey[]? nearBranches) && nearBranches.Any(k => SameBranch(k, p.Terminal));
            if (!present)
                placementArray[i] = p with { State = PsseTerminalModelState.EquipmentNotInModel,
                    Detail = "The branch is attached here but the projected model omits it (its other end is de-energised or out of service)." };
        }

        return new PsseTopologyModelResult(source.SourceSha256, states, Array.Empty<PsseTopologyRefusal>(), projected.Model, projected,
            projectedText, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectedText))).ToLowerInvariant(),
            Array.AsReadOnly(busArray), nodeMap, Array.AsReadOnly(placementArray),
            Array.AsReadOnly(branchSources), Array.AsReadOnly(machineSources));
    }

    private static IEnumerable<string?> Rewrite(IReadOnlyList<string?> fields, IReadOnlyDictionary<int, int> changes)
    {
        int count = Math.Max(fields.Count, changes.Keys.DefaultIfEmpty(-1).Max() + 1);
        for (int i = 0; i < count; i++)
        {
            string? original = i < fields.Count ? fields[i] : null;
            if (!changes.TryGetValue(i, out int value)) { yield return original; continue; }
            string text = value.ToString(CultureInfo.InvariantCulture);
            // A negative J (or K) marks the metered end; the sign stays with the end.
            yield return original is not null && original.TrimStart().StartsWith('-') && i is 1 or 2 ? "-" + text : text;
        }
    }

    private static int[] TerminalBuses(PsseTopologyTerminal t) => t.ThirdBus is int k ? new[] { t.Bus, t.ToBus!.Value, k }
        : t.ToBus is int j ? new[] { t.Bus, j } : new[] { t.Bus };

    private static string EndsKey(string type, int[] buses) =>
        type + "|" + string.Join(",", buses.Order().Select(b => b.ToString(CultureInfo.InvariantCulture)));

    /// <summary>A branch's unordered end set with its circuit, trimmed and upper-cased: a hash bucket whose members are
    /// then confirmed with <see cref="SameBranch"/>.</summary>
    private static string BranchEndsKey(int a, int b, int c, string circuit) =>
        string.Join(",", new[] { a, b, c }.Where(x => x != 0).Order().Select(x => x.ToString(CultureInfo.InvariantCulture))) +
        "|" + circuit.Trim().ToUpperInvariant();

    private static int[] EquipmentBuses(PsseEquipmentSource e) => e.ThirdBus is int k
        ? new[] { e.Bus, e.ToBus!.Value, k }
        : e.ToBus is int j ? new[] { e.Bus, j } : new[] { e.Bus };

    /// <summary>The row-0 field indices that hold <paramref name="bus"/> as one end of the record.</summary>
    private static IEnumerable<int> EndFields(PsseEquipmentSource e, int bus)
    {
        if (e.Bus == bus) yield return 0;
        if (e.ToBus == bus) yield return 1;
        if (e.ThirdBus == bus) yield return 2;
    }

    private static bool SameEquipment(PsseEquipmentSource e, PsseTopologyTerminal t)
    {
        bool typeMatches = e.EquipmentType == t.EquipmentType;
        if (!typeMatches) return false;
        int[] sourceBuses = EquipmentBuses(e).Order().ToArray();
        int[] terminalBuses = TerminalBuses(t).Order().ToArray();
        if (!sourceBuses.SequenceEqual(terminalBuses)) return false;
        if (t.EquipmentType == "S") return true;
        string id = t.EquipmentType is "B" or "2" or "3" ? t.CircuitId ?? "1" : t.EquipmentId ?? "1";
        return string.Equals(e.Id.Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameBranch(PsseBranchKey k, PsseTopologyTerminal t) =>
        t.ToBus is int to && string.Equals(k.Circuit.Trim(), t.CircuitId?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        (t.ThirdBus is int third
            ? new[] { k.FromBus, k.ToBus, k.ThirdBus }.Order().SequenceEqual(new[] { t.Bus, to, third }.Order())
            : (k.FromBus == t.Bus && k.ToBus == to) || (k.FromBus == to && k.ToBus == t.Bus));

    private static int IntField(IReadOnlyList<string?> fields, int index, int fallback)
    {
        string? s = index < fields.Count ? fields[index] : null;
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        return double.TryParse(s.Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
            v == Math.Floor(v) && v is >= int.MinValue and <= int.MaxValue ? (int)v : fallback;
    }
}
