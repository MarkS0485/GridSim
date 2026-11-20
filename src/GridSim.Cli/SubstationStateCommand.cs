using System.Globalization;
using System.Text.Json;
using GridSim.Core.IO;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Cim.Zip;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model.Substations;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-11 part A: <c>substation-state &lt;case&gt;</c>. Reads a native node-breaker file
/// (<c>gridsim-substations/1</c>), applies the stated switch states, projects it to an ordinary bus-branch model and
/// reports the bus groups, the identity maps, what became of every piece of equipment and the demand accounting. With
/// <c>--emit &lt;case.m&gt;</c> it also writes the projected case as a NEW MATPOWER file (never over an existing file, never
/// the input), so a solve through the existing verbs is `gridsim substation-state site.json --open CPL --emit
/// projected.m` then `gridsim pf projected.m`. All logic is in Core (<see cref="SubstationProjection"/>). RC4 wiring: the input may
/// also be a CGMES exchange (a .zip or a folder of EQ/TP/SSH/SV profile files), read as a node-breaker model by
/// <see cref="CgmesSubstationReader"/>; the SSH switch states are the starting states, which --open and --close override.
/// <c>--sequence steps.json</c> (a <c>gridsim-switching-sequence/1</c> file) then runs Core's <see cref="SwitchingScenario"/> from that
/// starting state and reports every step. Exit codes:
/// 0 done; 1 the file cannot be read or its topology is refused; 2 usage.
/// </summary>
internal static class SubstationStateCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Allowed = { "--open", "--close", "--unspecified", "--numbering", "--emit", "--base-mva", "--sequence" };

    private sealed class UsageException(string message) : Exception(message);

    public static int Run(List<string> a)
    {
        const string verb = "substation-state";
        try
        {
            string? casePath = null;
            var flags = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < a.Count; i++)
            {
                string t = a[i];
                if (!t.StartsWith("--", StringComparison.Ordinal))
                {
                    if (casePath is not null) throw new UsageException($"{verb} takes one positional argument but got: {casePath} {t}.");
                    casePath = t;
                    continue;
                }
                if (Array.IndexOf(Allowed, t) < 0) throw new UsageException($"{verb} does not use {t}. {verb} accepts: {string.Join(", ", Allowed)} (and --json/--csv/--out).");
                if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"{t} expected a value but none was given");
                if (!flags.TryAdd(t, a[++i])) throw new UsageException($"{t} was given twice");
            }
            if (casePath is null) throw new UsageException("usage: gridsim substation-state <case.json> [--open a,b] [--close c] [--unspecified refuse|normal] [--numbering node-rank|compact] [--emit projected.m]");

            UnspecifiedStatePolicy policy = flags.GetValueOrDefault("--unspecified", "refuse") switch
            {
                "refuse" => UnspecifiedStatePolicy.Refuse,
                "normal" => UnspecifiedStatePolicy.UseNormalState,
                var v => throw new UsageException($"--unspecified expected refuse or normal but got '{v}'"),
            };
            BusNumbering numbering = flags.GetValueOrDefault("--numbering", "node-rank") switch
            {
                "node-rank" => BusNumbering.NodeRank,
                "compact" => BusNumbering.Compact,
                var v => throw new UsageException($"--numbering expected node-rank or compact but got '{v}'"),
            };
            string[] open = Split(flags.GetValueOrDefault("--open"));
            string[] close = Split(flags.GetValueOrDefault("--close"));
            string? both = open.Intersect(close, StringComparer.Ordinal).FirstOrDefault();
            if (both is not null) throw new UsageException($"switch '{both}' is named in both --open and --close");
            string? emit = flags.GetValueOrDefault("--emit");
            string? sequence = flags.GetValueOrDefault("--sequence");
            if (sequence is not null && emit is not null) throw new UsageException("--sequence and --emit cannot be combined: --emit writes the one projected case of the starting state.");
            if (emit is not null)
            {
                if (!emit.EndsWith(".m", StringComparison.OrdinalIgnoreCase)) throw new UsageException($"--emit expected a file name ending in .m but got '{emit}'");
                if (string.Equals(Path.GetFullPath(emit), Path.GetFullPath(casePath), StringComparison.OrdinalIgnoreCase))
                    throw new UsageException("--emit would overwrite the input; name a new file");
            }

            bool cgmes = Directory.Exists(casePath) || casePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            double? baseMva = null;
            if (flags.TryGetValue("--base-mva", out string? bm))
            {
                if (!cgmes) throw new UsageException("--base-mva is for a CGMES input (CGMES carries no system base); a node-breaker .json file states its own.");
                if (!double.TryParse(bm, NumberStyles.Float, Inv, out double bmv) || !(bmv > 0) || !double.IsFinite(bmv)) throw new UsageException($"--base-mva expected a finite number above 0 but got '{bm}'");
                baseMva = bmv;
            }
            if (!cgmes && !File.Exists(casePath)) { Console.Error.WriteLine($"error: {verb}: case file '{casePath}' does not exist."); return CliExit.InputError; }
            SubstationModel model;
            CgmesReadResult? cg = null;
            try
            {
                if (cgmes)
                {
                    CgmesArchive archive = Directory.Exists(casePath) ? CgmesArchiveReader.ReadDirectory(casePath) : CgmesArchiveReader.ReadFile(casePath);
                    cg = CgmesSubstationReader.Read(archive, new CgmesReadOptions(baseMva));
                    model = cg.Model;
                }
                else model = SubstationJson.Read(File.ReadAllText(casePath));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }

            // The starting state: the declared normal states when the policy says so (the switches named in --open and
            // --close then override them); otherwise only what is named, and anything else is refused. A CGMES input starts from
            // the SSH states it carries (a switch the SSH does not state is left to the policy).
            var states = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (cg is not null) foreach (var kv in cg.SwitchStates) states[kv.Key] = kv.Value;
            foreach (string s in open) states[s] = true;
            foreach (string s in close) states[s] = false;
            SubstationProjectionResult r;
            try { r = SubstationProjection.Project(model, states, new SubstationProjectionOptions(policy, numbering)); }
            catch (SubstationTopologyException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }

            IReadOnlyList<SwitchingAction>? actions = null;
            if (sequence is not null)
            {
                if (!File.Exists(sequence)) { Console.Error.WriteLine($"error: {verb}: sequence file '{sequence}' does not exist."); return CliExit.InputError; }
                try { actions = ReadSequence(File.ReadAllText(sequence)); }
                catch (Exception ex) when (ex is InvalidDataException or JsonException) { Console.Error.WriteLine($"error: {verb}: {sequence}: {ex.Message}"); return CliExit.InputError; }
            }

            // F10: the one licence choke point (Gated). The projected case is a user model like any other: the tier's bus limit applies
            // to its bus count before anything is printed or emitted, exactly as for a case loaded by the other verbs.
            Gated.AdmitProjected(casePath, r.Model);

            Console.WriteLine($"substation-state - {model.Name}: {r.Buses.Count} bus(es) from {r.Groups.Count} node group(s); " +
                $"demand {F(r.DemandMwServed)} of {F(r.DemandMwTotal)} MW served ({F(r.DemandMwNotServed)} MW not served); " +
                $"generation {F(r.GenerationMwConnected)} of {F(r.GenerationMwTotal)} MW connected.");
            foreach (ProjectedBus b in r.Buses)
                Console.WriteLine($"  bus {b.BusId,-4} {b.Substation} {F(b.NominalKv)} kV  {string.Join("+", b.Nodes)}");
            foreach (NodeGroup g in r.Groups.Where(g => g.BusId is null))
                Console.WriteLine($"  no bus  {string.Join("+", g.Nodes)}: {g.Reason}");
            foreach (EquipmentOutcome e in r.Equipment.Where(e => e.Disposition != EquipmentDisposition.Connected))
                Console.WriteLine($"  {e.Kind.ToString().ToLowerInvariant()} '{e.EquipmentId}' {e.Disposition.ToString().ToLowerInvariant()}: {e.Reason}");
            foreach (string w in r.Warnings) Console.WriteLine($"  warning: {w}");
            if (cg is not null) foreach (string n in cg.Notes) Console.WriteLine($"  cgmes note: {n}");

            CliResult? rep = CliOutput.Report;
            if (rep is not null)
            {
                rep.Set("case", model.Name).Set("format", cg is null ? SubstationJson.Format : "cgmes-node-breaker")
                   .Set("unspecified_policy", policy == UnspecifiedStatePolicy.Refuse ? "refuse" : "normal")
                   .Set("numbering", numbering == BusNumbering.NodeRank ? "node-rank" : "compact")
                   .Set("buses", r.Buses.Count).Set("node_groups", r.Groups.Count).Set("branches", r.Model.Branches.Count)
                   .Set("demand_mw_total", r.DemandMwTotal).Set("demand_mw_served", r.DemandMwServed).Set("demand_mw_not_served", r.DemandMwNotServed)
                   .Set("generation_mw_total", r.GenerationMwTotal).Set("generation_mw_connected", r.GenerationMwConnected)
                   .Set("grouping", r.GroupingText());
                CliResultTable bt = rep.Table("buses", "bus_id", "representative_node", "substation", "nominal_kv", "nodes");
                foreach (ProjectedBus b in r.Buses) bt.Row(b.BusId, b.RepresentativeNode, b.Substation, b.NominalKv, string.Join("+", b.Nodes));
                CliResultTable gt = rep.Table("node_groups", "nodes", "bus_id", "reason");
                foreach (NodeGroup g in r.Groups) gt.Row(string.Join("+", g.Nodes), g.BusId, g.Reason ?? "");
                CliResultTable tt = rep.Table("terminals", "terminal", "bus_id");
                foreach (var kv in r.TerminalToBus) tt.Row(kv.Key, kv.Value);
                CliResultTable et = rep.Table("equipment", "equipment", "kind", "disposition", "mw", "mvar", "reason");
                foreach (EquipmentOutcome e in r.Equipment) et.Row(e.EquipmentId, e.Kind.ToString().ToLowerInvariant(), e.Disposition.ToString().ToLowerInvariant(), e.Mw, e.Mvar, e.Reason);
                CliResultTable st = rep.Table("switches", "switch", "state", "disposition");
                foreach (SwitchOutcome s in r.Switches) st.Row(s.SwitchId, s.Open ? "open" : "closed", s.Disposition.ToString());
                if (cg is not null)
                {
                    rep.Set("cgmes_ssh_switch_states", cg.SwitchStates.Count).Set("cgmes_topological_nodes", cg.TopologicalNodes.Count).Set("cgmes_sv_voltages", cg.Voltages.Count);
                    CliResultTable nt = rep.Table("cgmes_notes", "note");
                    foreach (string n in cg.Notes) nt.Row(n);
                }
            }

            if (actions is not null)
            {
                IReadOnlyList<SwitchingStep> run;
                try { run = SwitchingScenario.Run(model, states, actions, new SubstationProjectionOptions(policy, numbering)); }
                catch (SubstationTopologyException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }
                foreach (SwitchingStep st in run.Skip(1)) Gated.AdmitProjected(casePath, st.Projection.Model);
                Console.WriteLine($"  sequence: {actions.Count} step(s) from the starting state above.");
                foreach (SwitchingStep st in run.Skip(1))
                {
                    SubstationProjectionResult p = st.Projection;
                    Console.WriteLine($"  step {st.Index}: {st.Action!.Action.ToString().ToLowerInvariant()} {st.Action.SwitchId}{(st.Changed ? "" : " (already in that state)")}: " +
                        $"{p.Buses.Count} bus(es) from {p.Groups.Count} node group(s); demand {F(p.DemandMwServed)} of {F(p.DemandMwTotal)} MW served; generation {F(p.GenerationMwConnected)} of {F(p.GenerationMwTotal)} MW connected.");
                    if (st.GroupsRemoved.Count > 0) Console.WriteLine($"    groups removed: {string.Join(" | ", st.GroupsRemoved)}");
                    if (st.GroupsAdded.Count > 0) Console.WriteLine($"    groups added: {string.Join(" | ", st.GroupsAdded)}");
                }
                if (rep is not null)
                {
                    rep.Set("sequence_steps", actions.Count);
                    CliResultTable sq = rep.Table("sequence", "step", "action", "switch", "changed", "buses", "node_groups", "demand_mw_served", "demand_mw_not_served", "generation_mw_connected", "groups_removed", "groups_added");
                    CliResultTable sb = rep.Table("sequence_buses", "step", "bus_id", "representative_node", "substation", "nominal_kv", "nodes");
                    foreach (SwitchingStep st in run)
                    {
                        SubstationProjectionResult p = st.Projection;
                        sq.Row(st.Index, st.Action is null ? "start" : st.Action.Action.ToString().ToLowerInvariant(), st.Action?.SwitchId ?? "", st.Changed, p.Buses.Count, p.Groups.Count,
                            p.DemandMwServed, p.DemandMwNotServed, p.GenerationMwConnected, string.Join(" | ", st.GroupsRemoved), string.Join(" | ", st.GroupsAdded));
                        foreach (ProjectedBus b in p.Buses) sb.Row(st.Index, b.BusId, b.RepresentativeNode, b.Substation, b.NominalKv, string.Join("+", b.Nodes));
                    }
                }
            }

            if (emit is not null && r.Buses.Count == 0)
            {
                Console.Error.WriteLine($"error: {verb}: --emit: no electrical bus results from these switch states, so there is no case to write.");
                return CliExit.InputError;
            }
            if (emit is not null)
            {
                string text = MatpowerWriter.Write(r.Model).Text;
                string used;
                try { used = NeverOverwrite.WriteAllText(emit, text); }
                catch (IOException ex) { Console.Error.WriteLine($"error: {verb}: cannot write '{emit}': {ex.Message}"); return CliExit.InputError; }
                Console.WriteLine($"  projected case written to {used} (bus ids as above; solve it with the existing verbs).");
                rep?.Set("emitted_case", used);
            }
            rep?.Complete();
            return CliExit.Ok;
        }
        catch (UsageException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.Usage; }
    }

    /// <summary>Reads a <c>gridsim-switching-sequence/1</c> file: {"schema", "steps": [{"action": "open"|"close", "switch": id, "note"?}]}, strictly.</summary>
    internal static IReadOnlyList<SwitchingAction> ReadSequence(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false });
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("the sequence must be a JSON object.");
        foreach (JsonProperty p in root.EnumerateObject())
            if (p.Name is not ("schema" or "steps")) throw new InvalidDataException($"unknown key '{p.Name}' (allowed: schema, steps).");
        if (!root.TryGetProperty("schema", out JsonElement sc) || sc.ValueKind != JsonValueKind.String || sc.GetString() != SequenceSchema)
            throw new InvalidDataException($"\"schema\" must be \"{SequenceSchema}\".");
        if (!root.TryGetProperty("steps", out JsonElement steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0)
            throw new InvalidDataException("\"steps\" must be a non-empty array.");
        var list = new List<SwitchingAction>();
        int k = 0;
        foreach (JsonElement s in steps.EnumerateArray())
        {
            string w = $"steps[{k++}]";
            if (s.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{w} must be an object.");
            foreach (JsonProperty p in s.EnumerateObject())
                if (p.Name is not ("action" or "switch" or "note")) throw new InvalidDataException($"{w}: unknown key '{p.Name}' (allowed: action, switch, note).");
            string? a = s.TryGetProperty("action", out JsonElement av) && av.ValueKind == JsonValueKind.String ? av.GetString() : null;
            SwitchAction action = a switch
            {
                "open" => SwitchAction.Open,
                "close" => SwitchAction.Close,
                _ => throw new InvalidDataException($"{w}: \"action\" must be \"open\" or \"close\"."),
            };
            string? id = s.TryGetProperty("switch", out JsonElement iv) && iv.ValueKind == JsonValueKind.String ? iv.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException($"{w}: \"switch\" must be a non-empty switch id.");
            string? note = s.TryGetProperty("note", out JsonElement nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() : null;
            list.Add(new SwitchingAction(id, action, note));
        }
        return list;
    }

    internal const string SequenceSchema = "gridsim-switching-sequence/1";

    private static string[] Split(string? list) =>
        string.IsNullOrEmpty(list) ? Array.Empty<string>()
            : list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string F(double v) => v.ToString("0.####", Inv);
}
