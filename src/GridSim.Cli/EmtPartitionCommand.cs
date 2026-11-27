using System.Globalization;
using System.Text.Json;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.Partition;
using GridSim.Core.Numerics;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP27: <c>gridsim emt-partition &lt;study.json&gt; [--partition auto|&lt;cut.json&gt;] [--threads 1] [--monolithic]
/// [--out &lt;file.json&gt;]</c>. The partitioned parallel EMT run of the Core (<see cref="PartitionedEngine"/>,
/// <see cref="LinePartitioner"/>, unchanged) on a single-conductor network described in a strict
/// <c>gridsim-emt-partition/1</c> document: sinusoidal sources, R, L, C and series R-L branches, switches with timed events,
/// and Bergeron lines (each conductor with ground return). The Core cuts a network only at such lines, so the three-phase
/// <c>emt3</c> study, whose lines are modal, cannot be partitioned; this verb gives the engine its own input instead.
/// The network is split into partitions: the connected parts left when the cut lines are removed. <c>--partition auto</c>
/// cuts every line whose travel time is at least the step; a <c>gridsim-emt-cut/1</c> file names the lines to cut; with
/// neither, nothing is cut. Partitions are numbered p1, p2, ... in the order their first node appears in the document,
/// and a node is named <c>pK/node</c> inside the engine. <c>--monolithic</c> builds the same partitions as one network and
/// runs the incumbent nodal solver (the engine's oracle: the two give the same bits). Exit 0 completed; 3 the run ended
/// with a named status other than completed; 1 a missing or invalid file, or a network or cut the engine refuses; 2 usage.
/// </summary>
internal static class EmtPartitionCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Usage = "usage: gridsim emt-partition <study.json> [--partition auto|<cut.json>] [--threads 1] [--monolithic] [--out <file.json>]";

    /// <summary>The input is not a valid document (exit 1).</summary>
    internal sealed class InputException(string message) : Exception(message);

    internal sealed record Source(string Name, string Node, double ROhm, double AmplitudeV, double FrequencyHz, double PhaseDeg);
    internal sealed record Branch(string Name, string Kind, string From, string To, double ROhm, double LH, double CF);
    internal sealed record Switch(string Name, string From, string To, double RonOhm, double RoffOhm, bool Closed, IReadOnlyList<(double AtS, bool Close)> Events);
    internal sealed record Line(string Name, string From, string To, double SurgeOhm, double TravelS, double Attenuation);
    internal sealed record Output(string Name, string Kind, string Target);
    internal sealed record Study(double StepS, double DurationS, int SampleEvery, IReadOnlyList<Source> Sources, IReadOnlyList<Branch> Branches,
        IReadOnlyList<Switch> Switches, IReadOnlyList<Line> Lines, IReadOnlyList<Output> Outputs);

    private static bool IsGround(string n) => n is "gnd" or "ground";

    private static void Keys(JsonElement e, string where, params string[] allowed)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InputException($"{where} must be an object.");
        foreach (JsonProperty p in e.EnumerateObject())
            if (Array.IndexOf(allowed, p.Name) < 0) throw new InputException($"{where}: unknown key '{p.Name}' (allowed: {string.Join(", ", allowed)}).");
    }

    private static double Num(JsonElement e, string key, string where, double? def = null)
    {
        if (!e.TryGetProperty(key, out JsonElement v)) return def ?? throw new InputException($"{where}: \"{key}\" is required.");
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d)) throw new InputException($"{where}: \"{key}\" must be a finite number.");
        return d;
    }

    private static string Str(JsonElement e, string key, string where)
    {
        if (!e.TryGetProperty(key, out JsonElement v)) throw new InputException($"{where}: \"{key}\" is required.");
        if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())) throw new InputException($"{where}: \"{key}\" must be a non-empty string.");
        return v.GetString()!;
    }

    private static string NodeName(JsonElement e, string key, string where)
    {
        string n = Str(e, key, where);
        if (n.Contains('/', StringComparison.Ordinal)) throw new InputException($"{where}: node '{n}' may not contain '/'.");
        return n;
    }

    private static IEnumerable<(JsonElement El, string Where)> Items(JsonElement root, string key, bool required = false)
    {
        if (!root.TryGetProperty(key, out JsonElement v))
        {
            if (required) throw new InputException($"document: \"{key}\" is required.");
            return [];
        }
        if (v.ValueKind != JsonValueKind.Array) throw new InputException($"document: \"{key}\" must be an array.");
        int i = 0;
        return v.EnumerateArray().Select(x => (x, $"{key}[{i++}]")).ToList();
    }

    /// <summary>Parse a <c>gridsim-emt-partition/1</c> document strictly.</summary>
    internal static Study Parse(JsonElement root)
    {
        Keys(root, "document", "schema", "time_step_s", "duration_s", "sample_every", "sources", "branches", "switches", "lines", "outputs");
        string schema = Str(root, "schema", "document");
        if (schema != "gridsim-emt-partition/1") throw new InputException($"schema must be \"gridsim-emt-partition/1\" but is \"{schema}\".");
        double dt = Num(root, "time_step_s", "document"), dur = Num(root, "duration_s", "document");
        double every = Num(root, "sample_every", "document", 1);
        if (every != Math.Floor(every) || every < 1 || every > int.MaxValue) throw new InputException("document: \"sample_every\" must be a whole number of 1 or more.");
        var sources = new List<Source>();
        foreach ((JsonElement el, string w) in Items(root, "sources"))
        {
            Keys(el, w, "name", "node", "r_ohm", "amplitude_v", "frequency_hz", "phase_deg");
            sources.Add(new Source(Str(el, "name", w), NodeName(el, "node", w), Num(el, "r_ohm", w, 0), Num(el, "amplitude_v", w), Num(el, "frequency_hz", w), Num(el, "phase_deg", w, 0)));
        }
        var branches = new List<Branch>();
        foreach ((JsonElement el, string w) in Items(root, "branches"))
        {
            Keys(el, w, "name", "kind", "from", "to", "r_ohm", "l_h", "c_f");
            string kind = Str(el, "kind", w);
            (string[] need, string[] refuse) = kind switch
            {
                "resistor" => (new[] { "r_ohm" }, new[] { "l_h", "c_f" }),
                "inductor" => (new[] { "l_h" }, new[] { "r_ohm", "c_f" }),
                "capacitor" => (new[] { "c_f" }, new[] { "r_ohm", "l_h" }),
                "series-rl" => (new[] { "r_ohm", "l_h" }, new[] { "c_f" }),
                _ => throw new InputException($"{w}: kind must be \"resistor\", \"inductor\", \"capacitor\" or \"series-rl\" but is \"{kind}\"."),
            };
            foreach (string k in refuse) if (el.TryGetProperty(k, out _)) throw new InputException($"{w}: \"{k}\" is not used by a {kind}.");
            foreach (string k in need) _ = Num(el, k, w);
            branches.Add(new Branch(Str(el, "name", w), kind, NodeName(el, "from", w), NodeName(el, "to", w), Num(el, "r_ohm", w, 0), Num(el, "l_h", w, 0), Num(el, "c_f", w, 0)));
        }
        var switches = new List<Switch>();
        foreach ((JsonElement el, string w) in Items(root, "switches"))
        {
            Keys(el, w, "name", "from", "to", "ron_ohm", "roff_ohm", "closed", "events");
            bool closed = false;
            if (el.TryGetProperty("closed", out JsonElement c))
            {
                if (c.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InputException($"{w}: \"closed\" must be true or false.");
                closed = c.GetBoolean();
            }
            var events = new List<(double, bool)>();
            foreach ((JsonElement ev, string ew) in Items(el, "events"))
            {
                Keys(ev, $"{w}.{ew}", "at_s", "close");
                if (!ev.TryGetProperty("close", out JsonElement cl) || cl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InputException($"{w}.{ew}: \"close\" must be true or false.");
                events.Add((Num(ev, "at_s", $"{w}.{ew}"), cl.GetBoolean()));
            }
            switches.Add(new Switch(Str(el, "name", w), NodeName(el, "from", w), NodeName(el, "to", w), Num(el, "ron_ohm", w, 1e-6), Num(el, "roff_ohm", w, 1e9), closed, events));
        }
        var lines = new List<Line>();
        foreach ((JsonElement el, string w) in Items(root, "lines"))
        {
            Keys(el, w, "name", "from", "to", "surge_ohm", "travel_s", "attenuation");
            lines.Add(new Line(Str(el, "name", w), NodeName(el, "from", w), NodeName(el, "to", w), Num(el, "surge_ohm", w), Num(el, "travel_s", w), Num(el, "attenuation", w, 1.0)));
        }
        var outputs = new List<Output>();
        foreach ((JsonElement el, string w) in Items(root, "outputs", required: true))
        {
            Keys(el, w, "name", "kind", "target");
            string kind = Str(el, "kind", w);
            if (kind is not ("voltage" or "current")) throw new InputException($"{w}: kind must be \"voltage\" or \"current\" but is \"{kind}\".");
            outputs.Add(new Output(Str(el, "name", w), kind, Str(el, "target", w)));
        }
        if (outputs.Count == 0) throw new InputException("document: \"outputs\" needs at least one output.");
        return new Study(dt, dur, (int)every, sources, branches, switches, lines, outputs);
    }

    /// <summary>Read a <c>gridsim-emt-cut/1</c> file: the names of the lines to cut.</summary>
    internal static IReadOnlyList<string> ParseCut(JsonElement root)
    {
        Keys(root, "cut file", "schema", "cut");
        string schema = Str(root, "schema", "cut file");
        if (schema != "gridsim-emt-cut/1") throw new InputException($"cut file: schema must be \"gridsim-emt-cut/1\" but is \"{schema}\".");
        if (!root.TryGetProperty("cut", out JsonElement c) || c.ValueKind != JsonValueKind.Array) throw new InputException("cut file: \"cut\" must be an array of line names.");
        var names = new List<string>();
        foreach (JsonElement e in c.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString())) throw new InputException("cut file: every entry of \"cut\" must be a line name.");
            if (names.Contains(e.GetString()!, StringComparer.Ordinal)) throw new InputException($"cut file: line '{e.GetString()}' is named twice.");
            names.Add(e.GetString()!);
        }
        return names;
    }

    /// <summary>The lines <c>--partition auto</c> cuts: every line whose travel time is at least the step (the engine's
    /// own test, <see cref="LinePartitioner.Plan"/>).</summary>
    internal static IReadOnlyList<string> AutoCut(Study s) =>
        s.Lines.Where(l => l.TravelS >= s.StepS * (1.0 - 1e-12)).Select(l => l.Name).ToList();

    /// <summary>
    /// The partition of every node: the connected parts of the network with the cut lines removed, numbered p1, p2, ... in
    /// the order their first node appears (sources, then branches, switches and lines, each in document order).
    /// </summary>
    internal static (IReadOnlyList<string> Names, Dictionary<string, string> NodePartition) Split(Study s, IReadOnlyCollection<string> cut)
    {
        var order = new List<string>();
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        void See(string n) { if (!IsGround(n) && !parent.ContainsKey(n)) { parent[n] = n; order.Add(n); } }
        string Find(string n) { while (parent[n] != n) { parent[n] = parent[parent[n]]; n = parent[n]; } return n; }
        void Join(string a, string b)
        {
            if (IsGround(a) || IsGround(b)) return;
            string ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            // the root is the node seen first, so the numbering does not depend on the join order
            if (order.IndexOf(ra) < order.IndexOf(rb)) parent[rb] = ra; else parent[ra] = rb;
        }
        foreach (Source x in s.Sources) See(x.Node);
        foreach (Branch x in s.Branches) { See(x.From); See(x.To); }
        foreach (Switch x in s.Switches) { See(x.From); See(x.To); }
        foreach (Line x in s.Lines) { See(x.From); See(x.To); }
        foreach (Branch x in s.Branches) Join(x.From, x.To);
        foreach (Switch x in s.Switches) Join(x.From, x.To);
        foreach (Line x in s.Lines) if (!cut.Contains(x.Name)) Join(x.From, x.To);
        var rootName = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string n in order)
        {
            string r = Find(n);
            if (!rootName.TryGetValue(r, out string? p)) { p = "p" + (names.Count + 1).ToString(Inv); rootName[r] = p; names.Add(p); }
            map[n] = p;
        }
        return (names, map);
    }

    /// <summary>Build the partitioned model (or, with <paramref name="single"/>, the same description as one network), the
    /// probes and the events. Elements are added sources, branches, switches, lines, each in document order.</summary>
    internal static PartitionedModel Build(Study s, IReadOnlyList<string> partitions, Dictionary<string, string> nodePartition, bool single,
        out List<NodalProbe> probes, out List<NodalEvent> events)
    {
        static Exception Refuse(string m) => new InputException(m);
        if (partitions.Count == 0) throw Refuse("the network has no node.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        void NewName(string n, string kind) { if (!names.Add(n)) throw Refuse($"the name '{n}' is used twice."); kinds[n] = kind; }
        string Part(string who, string a, string b)
        {
            if (IsGround(a) && IsGround(b)) throw Refuse($"{who}: both ends are ground.");
            return nodePartition[IsGround(a) ? b : a];
        }
        PartitionedModel m = PartitionedModel.Create(partitions, single);
        events = [];
        foreach (Source x in s.Sources)
        {
            NewName(x.Name, "source");
            if (IsGround(x.Node)) throw Refuse($"{x.Name}: a source needs a node, not ground.");
            if (!(x.FrequencyHz > 0) || !(x.ROhm >= 0)) throw Refuse($"{x.Name}: the frequency must be above 0 and the resistance not negative.");
            string p = nodePartition[x.Node];
            double amp = x.AmplitudeV, w = 2.0 * Math.PI * x.FrequencyHz, ph = x.PhaseDeg * Math.PI / 180.0;
            m.Add(p, new NodalVoltageSource(x.Name, m.Node(p, x.Node), x.ROhm, t => amp * DetMath.Sin(w * t + ph)));
        }
        foreach (Branch x in s.Branches)
        {
            NewName(x.Name, "branch");
            string p = Part(x.Name, x.From, x.To);
            int a = m.Node(p, x.From), b = m.Node(p, x.To);
            m.Add(p, x.Kind switch
            {
                "resistor" => NodalBranch.Resistor(x.Name, a, b, x.ROhm),
                "inductor" => NodalBranch.Inductor(x.Name, a, b, x.LH),
                "capacitor" => NodalBranch.Capacitor(x.Name, a, b, x.CF),
                _ => NodalBranch.SeriesRl(x.Name, a, b, x.ROhm, x.LH),
            });
        }
        foreach (Switch x in s.Switches)
        {
            NewName(x.Name, "switch");
            string p = Part(x.Name, x.From, x.To);
            m.Add(p, new NodalSwitch(x.Name, m.Node(p, x.From), m.Node(p, x.To), x.RonOhm, x.RoffOhm, x.Closed));
            foreach ((double at, bool close) in x.Events) events.Add(new NodalEvent(at, x.Name, close));
        }
        foreach (Line x in s.Lines)
        {
            NewName(x.Name, "line");
            if (IsGround(x.From) || IsGround(x.To)) throw Refuse($"{x.Name}: both ends of a line must be nodes (the return is ground).");
            m.AddLine(x.Name, nodePartition[x.From], x.From, nodePartition[x.To], x.To, x.SurgeOhm, x.TravelS, x.Attenuation);
        }
        probes = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Output o in s.Outputs)
        {
            if (!seen.Add(o.Name)) throw Refuse($"output name '{o.Name}' is used twice.");
            if (o.Kind == "voltage")
            {
                if (!nodePartition.TryGetValue(o.Target, out string? p)) throw Refuse($"output '{o.Name}': no node '{o.Target}'.");
                probes.Add(new NodalProbe(o.Name, NodalProbeKind.NodeVoltage, p + "/" + o.Target));
            }
            else
            {
                if (!kinds.TryGetValue(o.Target, out string? k) || k is not ("branch" or "switch"))
                    throw Refuse($"output '{o.Name}': '{o.Target}' is not a branch or a switch (a current is measured on those).");
                probes.Add(new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, o.Target));
            }
        }
        return m;
    }

    public static int Run(List<string> a)
    {
        Wp27CliSupport.Args? args = Wp27CliSupport.Parse(a, "emt-partition", Usage, ["--partition", "--threads", "--out"], ["--monolithic"]);
        if (args is null) return CliExit.Usage;
        if (args.Positional.Count != 1)
        {
            Console.Error.WriteLine(args.Positional.Count > 1 ? $"error: emt-partition takes one study file. {Usage}" : Usage);
            return CliExit.Usage;
        }
        int threads = 1;
        if (args.Get("--threads") is { } tt && (!int.TryParse(tt, NumberStyles.Integer, Inv, out threads) || threads < 1))
        {
            Console.Error.WriteLine($"error: emt-partition: --threads expected a whole number of 1 or more but got '{tt}'. {Usage}");
            return CliExit.Usage;
        }
        bool monolithic = args.Switches.Contains("--monolithic");
        if (monolithic && args.Get("--threads") is not null)
        {
            Console.Error.WriteLine($"error: emt-partition: --threads sets the engine's worker threads; --monolithic runs the single-network solver. {Usage}");
            return CliExit.Usage;
        }
        if (!Wp27CliSupport.PrepareOutput(args.Get("--out"), "emt-partition", "partitioned EMT result", out string? output)) return CliExit.Usage;

        string file = args.Positional[0];
        string? partition = args.Get("--partition");
        Study study;
        IReadOnlyList<string> cut;
        try
        {
            study = Parse(ReadJson(file));
            if (partition is null) cut = [];
            else if (partition == "auto") cut = AutoCut(study);
            else
            {
                cut = ParseCut(ReadJson(partition));
                foreach (string n in cut)
                    if (!study.Lines.Any(l => l.Name == n)) throw new InputException($"cut file: '{n}' is not a line of the study.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InputException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: emt-partition: {ex.Message}");
            return CliExit.InputError;
        }

        NodalResult run;
        PartitionPlan plan;
        int used;
        try
        {
            (IReadOnlyList<string> names, Dictionary<string, string> nodePartition) = Split(study, new HashSet<string>(cut, StringComparer.Ordinal));
            PartitionedModel model = Build(study, names, nodePartition, monolithic, out List<NodalProbe> probes, out List<NodalEvent> events);
            var nodal = new NodalOptions { SampleEvery = study.SampleEvery };
            if (monolithic)
            {
                plan = LinePartitioner.Plan(model, study.StepS);
                run = NodalSolver.Run(model.Networks[0], study.StepS, study.DurationS, probes, events, nodal);
                used = 1;
            }
            else
            {
                PartitionedResult pr = PartitionedEngine.Run(model, study.StepS, study.DurationS, probes, events, new PartitionedOptions { Threads = threads, Nodal = nodal });
                run = pr.Run; plan = pr.Plan; used = pr.Threads;
            }
        }
        catch (InputException ex)
        {
            Console.Error.WriteLine($"error: emt-partition: {ex.Message}");
            return CliExit.InputError;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: emt-partition: {CliExit.UserMessage(ex)}");
            return CliExit.InputError;
        }

        Console.WriteLine($"EMT-PARTITION - {run.Method}{(monolithic ? ", one network (monolithic)" : "")}, step {R(run.StepSeconds)} s, {run.Stats.Steps.ToString(Inv)} step(s), {used.ToString(Inv)} thread(s)");
        Console.WriteLine($"  plan: {plan.Description}");
        Console.WriteLine($"  linear matrix factorisations {run.Stats.Refactorisations.ToString(Inv)}");
        for (int k = 0; k < run.ProbeNames.Count; k++)
        {
            double max = 0.0;
            foreach (double x in run.Series[k]) max = Math.Max(max, Math.Abs(x));
            Console.WriteLine($"  {run.ProbeNames[k],-16} max |x| = {max.ToString("E6", Inv)}");
        }
        int exit = CliExit.Ok;
        if (run.Status != NodalStatus.Completed)
        {
            Console.Error.WriteLine($"emt-partition: the run did not complete: {run.StatusMessage}");
            exit = CliExit.Verdict;
        }
        else Console.WriteLine("  status: completed");

        if (output is not null)
        {
            try
            {
                Wp27CliSupport.WriteJson(output, w =>
                {
                    w.WriteString("schema", "gridsim-emt-partition/1");
                    w.WriteString("status", run.Status.ToString());
                    w.WriteString("statusMessage", run.StatusMessage);
                    w.WriteString("form", monolithic ? "monolithic" : "partitioned");
                    w.WriteNumber("threads", used);
                    w.WriteNumber("partitions", plan.Partitions);
                    w.WriteStartArray("cutLines");
                    foreach (string c in plan.CutLines) w.WriteStringValue(c);
                    w.WriteEndArray();
                    w.WriteNumber("windowSteps", plan.WindowSteps);
                    w.WriteString("plan", plan.Description);
                    Wp27CliSupport.Number(w, "timeStepS", run.StepSeconds);
                    w.WriteNumber("steps", run.Stats.Steps);
                    w.WriteNumber("refactorisations", run.Stats.Refactorisations);
                    Wp27CliSupport.Series(w, "timeS", run.Times);
                    w.WriteStartObject("traces");
                    for (int k = 0; k < run.ProbeNames.Count; k++)
                    {
                        Wp27CliSupport.Series(w, run.ProbeNames[k], run.Series[k]);
                    }
                    w.WriteEndObject();
                });
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("error: emt-partition: cannot write the result: " + ex.Message);
                return CliExit.InputError;
            }
            Console.WriteLine($"  wrote {output}");
        }
        return exit;
    }

    private static JsonElement ReadJson(string path)
    {
        if (!File.Exists(path)) throw new InputException($"file not found: {path}");
        using JsonDocument doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(path)), new JsonDocumentOptions { AllowTrailingCommas = false });
        return doc.RootElement.Clone();
    }

    private static string R(double v) => v.ToString("R", Inv);
}
