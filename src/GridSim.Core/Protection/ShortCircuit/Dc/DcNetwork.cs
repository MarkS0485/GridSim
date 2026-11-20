using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GridSim.Core.Protection.ShortCircuit.Dc;

public enum DcSourceKind { Battery, Rectifier, Capacitor, Motor }

/// <summary>A series R-L branch between two named nodes (lumped over both conductors of the DC circuit). <c>L = 0</c> is allowed (pure resistance).</summary>
public sealed record DcBranch(string Name, string From, string To, double ROhm, double LHenry);

/// <summary>
/// A DC source attached between <see cref="Node"/> and the common return (reference). All four kinds are a voltage behind R and L:
/// battery and rectifier hold a constant EMF (the rectifier's own behaviour, AC-side decay and current limiting, is AWAITING STANDARD TEXT);
/// a capacitor's EMF is its voltage U with <c>C dU/dt = -i</c>; a motor's EMF is <c>k w</c> with <c>J dw/dt = -k i</c> (the machine
/// generates into the fault and slows; load torque and field decay are neglected).
/// </summary>
public sealed record DcSource(
    string Name, DcSourceKind Kind, string Node, double EVolt, double ROhm, double LHenry,
    double CFarad = double.NaN, double JKgM2 = double.NaN, double KVsPerRad = double.NaN);

/// <summary>The short circuit: a node joined to the return through <see cref="ROhm"/> (0 = bolted).</summary>
public sealed record DcFault(string Node, double ROhm);

/// <summary>
/// A small, self-contained DC network for the IEC 61660-style study (<c>gridsim-dc-network/1</c>). The case format has no DC data, so this
/// type is its own study file. Node <c>"0"</c> is the common return (reference); every other name is a node. Nothing is defaulted.
/// </summary>
public sealed record DcNetwork(IReadOnlyList<DcBranch> Branches, IReadOnlyList<DcSource> Sources, DcFault Fault)
{
    public const string Schema = "gridsim-dc-network/1";
    public const string Reference = "0";

    public static DcNetwork Parse(string json)
    {
        var problems = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("dc network is not valid JSON: " + ex.Message); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("dc network must be a JSON object");
            foreach (JsonProperty p in root.EnumerateObject())
                if (p.Name is not ("schema" or "branches" or "sources" or "fault")) problems.Add($"unknown key '{p.Name}'");
            if (!root.TryGetProperty("schema", out JsonElement sch) || sch.ValueKind != JsonValueKind.String || sch.GetString() != Schema)
                problems.Add($"schema must be \"{Schema}\"");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var branches = new List<DcBranch>();
            if (root.TryGetProperty("branches", out JsonElement be) && be.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement e in be.EnumerateArray())
                {
                    string w = $"branches[{i++}]";
                    string name = Str(e, "name"), from = Str(e, "from"), to = Str(e, "to");
                    if (name.Length == 0 || !names.Add(name)) problems.Add($"{w}: name is required and must be unique");
                    if (from.Length == 0 || to.Length == 0) problems.Add($"{w}: 'from' and 'to' are required");
                    else if (from == to) problems.Add($"{w}: 'from' and 'to' must differ");
                    branches.Add(new DcBranch(name, from, to, Num(e, "rOhm", w, problems, true), Num(e, "lH", w, problems, true)));
                }
            }
            var sources = new List<DcSource>();
            if (root.TryGetProperty("sources", out JsonElement se) && se.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement e in se.EnumerateArray())
                {
                    string w = $"sources[{i++}]";
                    string name = Str(e, "name"), node = Str(e, "node"), kind = Str(e, "kind");
                    if (name.Length == 0 || !names.Add(name)) problems.Add($"{w}: name is required and must be unique");
                    if (node.Length == 0 || node == Reference) problems.Add($"{w}: 'node' is required and must not be the reference \"0\"");
                    DcSourceKind k = DcSourceKind.Battery;
                    switch (kind)
                    {
                        case "battery": k = DcSourceKind.Battery; break;
                        case "rectifier": k = DcSourceKind.Rectifier; break;
                        case "capacitor": k = DcSourceKind.Capacitor; break;
                        case "motor": k = DcSourceKind.Motor; break;
                        default: problems.Add($"{w}: kind must be battery, rectifier, capacitor or motor"); break;
                    }
                    double r = Num(e, "rOhm", w, problems, true), l = Num(e, "lH", w, problems, true);
                    double c = double.NaN, j = double.NaN, kk = double.NaN;
                    double ev;
                    if (k == DcSourceKind.Capacitor) { ev = Num(e, "uV", w, problems, false); c = Num(e, "cF", w, problems, false); }
                    else ev = Num(e, "eV", w, problems, false);
                    if (k == DcSourceKind.Motor) { j = Num(e, "jKgM2", w, problems, false); kk = Num(e, "kVsPerRad", w, problems, false); }
                    if ((k == DcSourceKind.Capacitor || k == DcSourceKind.Motor) && !(l > 0.0))
                        problems.Add($"{w}: a {kind} needs lH above 0 (its response is a second-order circuit)");
                    sources.Add(new DcSource(name, k, node, ev, r, l, c, j, kk));
                }
            }
            DcFault fault = new("", double.NaN);
            if (root.TryGetProperty("fault", out JsonElement fe) && fe.ValueKind == JsonValueKind.Object)
            {
                string node = Str(fe, "node");
                if (node.Length == 0 || node == Reference) problems.Add("fault: 'node' is required and must not be the reference \"0\"");
                fault = new DcFault(node, Num(fe, "rOhm", "fault", problems, true));
            }
            else problems.Add("fault: an object with 'node' and 'rOhm' is required");
            if (sources.Count == 0) problems.Add("at least one source is required");
            if (problems.Count == 0)
            {
                var nodes = new HashSet<string>(StringComparer.Ordinal);
                foreach (DcBranch b in branches) { nodes.Add(b.From); nodes.Add(b.To); }
                foreach (DcSource s in sources) nodes.Add(s.Node);
                if (!nodes.Contains(fault.Node)) problems.Add($"fault node '{fault.Node}' is not used by any branch or source");
            }
            if (problems.Count > 0) throw new FormatException("dc network: " + string.Join("; ", problems));
            return new DcNetwork(branches, sources, fault);
        }
    }

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    /// <summary>A required number; <paramref name="allowZero"/> admits 0 (a resistance or inductance), otherwise it must be above 0.</summary>
    private static double Num(JsonElement e, string key, string where, List<string> problems, bool allowZero)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Number)
        { problems.Add($"{where}: '{key}' is required and must be a number"); return double.NaN; }
        double d = v.GetDouble();
        if (!double.IsFinite(d) || d < 0.0 || (!allowZero && !(d > 0.0))) { problems.Add($"{where}: '{key}' must be finite and {(allowZero ? "0 or more" : "above 0")}"); return double.NaN; }
        return d;
    }
}
