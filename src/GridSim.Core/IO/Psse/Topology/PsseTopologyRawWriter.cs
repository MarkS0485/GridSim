using System.Globalization;
using System.Text;

namespace GridSim.Core.IO.Psse.Topology;

/// <summary>One source field the topology-aware writer could not carry, with its source line.</summary>
public sealed record PsseTopologyWriteOmission(int SourceLine, string Detail);

/// <summary>The written RAW text and what it could not carry.</summary>
/// <param name="Text">The revision 35 RAW text (LF line endings).</param>
/// <param name="Omissions">Source data fields that are not in the written text. Empty means the written substation data
/// carries every field the source substation data held.</param>
/// <param name="SubstationRecordCount">Substation data records written (headers, nodes, switches and terminals).</param>
public sealed record PsseTopologyWriteResult(string Text, IReadOnlyList<PsseTopologyWriteOmission> Omissions,
    int SubstationRecordCount);

/// <summary>
/// Topology-aware PSS/E revision 35 RAW writer, separate from the incumbent <see cref="PsseRawWriter"/> (which writes a
/// <see cref="GridSim.Core.Model.GridModel"/> and no substation data, and is left unchanged).
/// <para>The substation data is regenerated from the typed native graph (<see cref="PsseSubstationTopology"/>): every
/// substation block in source order, its nodes, switching devices (with an optional explicit state overlay applied to
/// STATUS; NSTAT is the source's normal state) and equipment terminals, each in source order. Every other line of the
/// hash-bound source text (case identification, titles, system-wide data and all bus-branch sections) is carried
/// verbatim, so equipment fields are preserved exactly. Comment and heading lines inside the substation data are not
/// carried. Source fields beyond the documented record layout are reported as omissions; with
/// <c>requireLossless</c> any omission refuses the write.</para>
/// </summary>
public static class PsseTopologyRawWriter
{
    public static PsseTopologyWriteResult Write(PsseSubstationCase source,
        IReadOnlyDictionary<PsseSwitchKey, bool>? switchStates = null, bool requireLossless = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(source.SourceText))
            throw new ArgumentException("The case carries no source text; read it with PsseSubstationCaseReader.", nameof(source));
        PsseSubstationTopology topology = source.Topology;
        if (switchStates is not null)
        {
            var known = topology.Switches.Select(PsseSubstationProjection.Key).ToHashSet();
            foreach (PsseSwitchKey key in switchStates.Keys)
                if (!known.Contains(key))
                    throw new InvalidDataException($"Switch state was supplied for unknown PSS/E switch in substation {key.Substation}, nodes {key.FromNode}-{key.ToNode}, circuit '{key.CircuitId}'.");
            foreach (PsseSwitchKey key in known)
                if (!switchStates.ContainsKey(key))
                    throw new InvalidDataException($"An explicit state is required for PSS/E switch in substation {key.Substation}, nodes {key.FromNode}-{key.ToNode}, circuit '{key.CircuitId}'.");
            // IO-23: the reader refuses a closed switch between nodes of different bus-branch buses, so do not write one.
            var busByNode = topology.Nodes.ToDictionary(n => (n.Substation, n.Number), n => n.Bus);
            foreach (PsseTopologySwitch sw in topology.Switches)
                if (switchStates[PsseSubstationProjection.Key(sw)] &&
                    busByNode.TryGetValue((sw.Substation, sw.FromNode), out int fromBus) && busByNode.TryGetValue((sw.Substation, sw.ToNode), out int toBus) && fromBus != toBus)
                    throw new InvalidDataException($"A switch closed in the file must connect nodes assigned to the same bus-branch bus (substation {sw.Substation}, nodes {sw.FromNode} and {sw.ToNode}, circuit '{sw.CircuitId}' are on buses {fromBus} and {toBus}).");
        }

        PsseRawLayout layout = PsseRawLayout.Read(source.SourceText, source.Parsed.Revision);
        var sourceRow = layout.DataLines.Where(l => l.Section == PsseRawLayout.Substation).ToDictionary(l => l.Line);
        var omissions = new List<PsseTopologyWriteOmission>();
        var block = new StringBuilder();
        int records = 0;

        // represented: how many leading fields of the documented record the typed graph carries by value. A source field
        // at or beyond that position is not written and is listed (a trailing default the typed value reproduces, such as
        // a zero rating left off, is not data loss).
        void Emit(int sourceLine, List<string?> fields, int represented)
        {
            if (sourceRow.TryGetValue(sourceLine, out PsseRawLine? row))
                for (int i = Math.Max(fields.Count, represented); i < row.Fields.Count; i++)
                    if (!string.IsNullOrWhiteSpace(row.Fields[i]))
                        omissions.Add(new(sourceLine, $"Field {i + 1} ('{row.Fields[i]}') is not represented in the typed substation graph and is not written."));
            block.Append(PsseRawLayout.FormatRecord(fields)).Append('\n');
            records++;
        }

        foreach (PsseSubstationBlock sub in source.Parsed.Substations)
        {
            int id = int.Parse(sub.Header[0]!, NumberStyles.Integer, CultureInfo.InvariantCulture);
            block.Append(PsseRawLayout.FormatRecord(sub.Header)).Append('\n');
            records++;
            foreach (PsseTopologyNode node in topology.Nodes.Where(n => n.Substation == id).OrderBy(n => n.SourceLine))
            {
                var f = new List<string?> { I(node.Number), node.Name, I(node.Bus), node.InService ? "1" : "0" };
                if (node.VmPu is not null || node.VaDeg is not null) f.Add(node.VmPu is double vm ? R(vm) : "");
                if (node.VaDeg is double va) f.Add(R(va));
                Emit(node.SourceLine, f, 6);
            }
            block.Append("0 / END OF SUBSTATION NODE DATA, BEGIN SUBSTATION SWITCHING DEVICE DATA\n");
            foreach (PsseTopologySwitch sw in topology.Switches.Where(s => s.Substation == id).OrderBy(s => s.SourceLine))
            {
                if (sw.ResistancePu != 0 || sw.ChargingSusceptancePu != 0 || sw.FromConductancePu != 0 || sw.FromSusceptancePu != 0 ||
                    sw.ToConductancePu != 0 || sw.ToSusceptancePu != 0)
                    omissions.Add(new(sw.SourceLine, "The switch carries R, charging or end shunts, which the RAW switching device record cannot hold."));
                bool closed = switchStates is null ? sw.Closed : switchStates[PsseSubstationProjection.Key(sw)];
                var f = new List<string?> { I(sw.FromNode), I(sw.ToNode), sw.CircuitId, sw.Name, I(sw.Type),
                    closed ? "1" : "0", sw.NormalClosed ? "1" : "0", R(sw.ReactancePu) };
                double[] rates = { sw.Rate1Mva, sw.Rate2Mva, sw.Rate3Mva };
                int lastRate = Array.FindLastIndex(rates, r => r != 0);
                for (int k = 0; k <= lastRate; k++) f.Add(R(rates[k]));
                Emit(sw.SourceLine, f, 11);
            }
            block.Append("0 / END OF SUBSTATION SWITCHING DEVICE DATA, BEGIN SUBSTATION TERMINAL DATA\n");
            foreach (PsseTopologyTerminal t in topology.Terminals.Where(t => t.Substation == id).OrderBy(t => t.SourceLine))
            {
                var f = new List<string?> { I(t.Bus), I(t.Node), t.EquipmentType };
                switch (t.EquipmentType)
                {
                    case "B" or "2": f.Add(I(t.ToBus!.Value)); f.Add(t.CircuitId); break;
                    case "3": f.Add(I(t.ToBus!.Value)); f.Add(I(t.ThirdBus!.Value)); f.Add(t.CircuitId); break;
                    case "S": break;
                    default: f.Add(t.EquipmentId); break;
                }
                Emit(t.SourceLine, f, t.EquipmentType switch { "B" or "2" => 5, "3" => 6, "S" => 3, _ => 4 });
            }
            block.Append("0 / END OF SUBSTATION TERMINAL DATA\n");
        }

        if (requireLossless && omissions.Count > 0)
            throw new InvalidDataException("Lossless topology output was requested but the source holds data the writer cannot carry:" +
                Environment.NewLine + string.Join(Environment.NewLine, omissions.Select(o => $"  line {o.SourceLine}: {o.Detail}")));

        var text = new StringBuilder();
        string[] lines = layout.Lines;
        int? first = layout.SubstationFirstLine, end = layout.SubstationTerminatorLine;
        if (first is null && source.Parsed.Substations.Count > 0)
            throw new InvalidDataException("The source's substation data could not be located.");
        for (int line = 1; line <= lines.Length; line++)
        {
            if (first is int f0 && line == f0) text.Append(block);
            if (first is int f1 && line >= f1 && line < end) continue;
            text.Append(lines[line - 1]);
            if (line < lines.Length) text.Append('\n');
        }
        return new PsseTopologyWriteResult(text.ToString(), Array.AsReadOnly(omissions.ToArray()), records);
    }

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// Topology equivalence of two hash-bound cases, compared before any solved value: substation headers, the normalised
/// native graph (nodes, switches with every typed field, terminals; source line numbers ignored), terminal bindings,
/// equipment source rows and keys, and the incumbent bus-branch model records.
/// </summary>
public static class PsseTopologyEquivalence
{
    public static IReadOnlyList<string> Differences(PsseSubstationCase expected, PsseSubstationCase actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var d = new List<string>();
        PsseParseResult e = expected.Parsed, a = actual.Parsed;
        if (e.Revision != a.Revision) d.Add($"revision {e.Revision} vs {a.Revision}");
        if (!expected.Topology.Substations.SequenceEqual(actual.Topology.Substations)) d.Add("substation numbers differ");
        if (e.Substations.Count != a.Substations.Count ||
            e.Substations.Zip(a.Substations).Any(p => !p.First.Header.SequenceEqual(p.Second.Header)))
            d.Add("substation header records differ");

        Compare(d, "node", expected.Topology.Nodes.OrderBy(n => n.Substation).ThenBy(n => n.Number).Select(n => n with { SourceLine = 0 }),
            actual.Topology.Nodes.OrderBy(n => n.Substation).ThenBy(n => n.Number).Select(n => n with { SourceLine = 0 }));
        Compare(d, "switch", Sorted(expected.Topology.Switches).Select(s => s with { SourceLine = 0 }),
            Sorted(actual.Topology.Switches).Select(s => s with { SourceLine = 0 }));
        Compare(d, "terminal", Sorted(expected.Topology.Terminals).Select(t => t with { SourceLine = 0 }),
            Sorted(actual.Topology.Terminals).Select(t => t with { SourceLine = 0 }));
        Compare(d, "terminal binding",
            SortedBindings(expected.TerminalBindings).Select(b => b with { Terminal = b.Terminal with { SourceLine = 0 } }),
            SortedBindings(actual.TerminalBindings).Select(b => b with { Terminal = b.Terminal with { SourceLine = 0 } }));
        Compare(d, "equipment source", e.EquipmentSources.Select(Normalised), a.EquipmentSources.Select(Normalised));
        Compare(d, "branch key", e.BranchKeys, a.BranchKeys);
        Compare(d, "skipped branch key", e.SkippedBranchKeys, a.SkippedBranchKeys);
        Compare(d, "machine key", e.MachineKeys, a.MachineKeys);
        Compare(d, "native bus id", e.RawBusIds, a.RawBusIds);
        Compare(d, "model bus", e.Model.Buses, a.Model.Buses);
        Compare(d, "model branch", e.Model.Branches, a.Model.Branches);
        Compare(d, "model generator", e.Model.AllGenerators, a.Model.AllGenerators);
        if (e.Model.BaseMva != a.Model.BaseMva || e.Model.FrequencyHz != a.Model.FrequencyHz) d.Add("system base or frequency differs");
        return Array.AsReadOnly(d.ToArray());
    }

    private static string Normalised(PsseEquipmentSource s) =>
        $"{s.Kind}|{s.EquipmentType}|{s.Bus}|{s.ToBus}|{s.ThirdBus}|{s.Id}|{s.InService}|{s.LegacyModelRetained}|" +
        string.Join("\u001f", s.Fields.Select(f => f ?? "\u0000")) + "|" +
        string.Join("\u001e", s.SourceRows.Select(r => string.Join("\u001f", r.Fields.Select(f => f ?? "\u0000"))));

    private static IEnumerable<PsseTopologySwitch> Sorted(IEnumerable<PsseTopologySwitch> s) =>
        s.OrderBy(x => x.Substation).ThenBy(x => x.FromNode).ThenBy(x => x.ToNode).ThenBy(x => x.CircuitId, StringComparer.Ordinal);

    private static IEnumerable<PsseTopologyTerminal> Sorted(IEnumerable<PsseTopologyTerminal> t) => t.OrderBy(TerminalOrder, StringComparer.Ordinal);

    private static IEnumerable<PsseTerminalBinding> SortedBindings(IEnumerable<PsseTerminalBinding> b) =>
        b.OrderBy(x => TerminalOrder(x.Terminal), StringComparer.Ordinal);

    private static string TerminalOrder(PsseTopologyTerminal t) => string.Create(CultureInfo.InvariantCulture,
        $"{t.Substation:D6}|{t.Bus:D7}|{t.Node:D4}|{t.EquipmentType}|{t.EquipmentId}|{t.ToBus}|{t.ThirdBus}|{t.CircuitId}");

    private static void Compare<T>(List<string> d, string what, IEnumerable<T> expected, IEnumerable<T> actual)
    {
        T[] e = expected.ToArray(), a = actual.ToArray();
        if (e.Length != a.Length) { d.Add($"{what} count {e.Length} vs {a.Length}"); return; }
        for (int i = 0; i < e.Length; i++)
            if (!EqualityComparer<T>.Default.Equals(e[i], a[i])) d.Add($"{what} {i}: {e[i]} vs {a[i]}");
    }
}
