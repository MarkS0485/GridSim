using System.Globalization;
using System.Text.Json;
using GridSim.Core.Api;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Ras;

/// <summary>What a remedial action scheme condition tests (RC5 WP26).</summary>
public enum RasConditionKind
{
    /// <summary>Arming: the contingency is the named element, or contains it.</summary>
    Outage,
    /// <summary>Arming: the pre-contingency flow on a branch is above a value (MVA or MW).</summary>
    PreFlowAbove,
    /// <summary>Arming: the pre-contingency flow on a branch is below a value (MVA or MW).</summary>
    PreFlowBelow,
    /// <summary>Arming: the pre-contingency voltage of a bus is below a value (pu).</summary>
    PreVoltageBelow,
    /// <summary>Arming: the pre-contingency voltage of a bus is above a value (pu).</summary>
    PreVoltageAbove,
    /// <summary>Arming: always true.</summary>
    Always,
    /// <summary>Trigger: a branch is loaded above a percentage of its rating A, B or C.</summary>
    LoadingAbove,
    /// <summary>Trigger: the real power into a branch at one end is above a value (MW, signed).</summary>
    FlowAbove,
    /// <summary>Trigger: a bus voltage is below a value (pu).</summary>
    VoltageBelow,
    /// <summary>Trigger: a bus voltage is above a value (pu).</summary>
    VoltageAbove,
    /// <summary>Trigger: the power flow of the present state did not converge.</summary>
    NotConverged,
}

/// <summary>The kind of element a condition or action names.</summary>
public enum RasElement
{
    /// <summary>No element (always, notConverged).</summary>
    None,
    /// <summary>A branch, by its object-model key.</summary>
    Branch,
    /// <summary>A generating unit, by its object-model key.</summary>
    Unit,
    /// <summary>A bus, by its id.</summary>
    Bus,
}

/// <summary>What a scheme does when it operates.</summary>
public enum RasActionKind
{
    /// <summary>Take a generating unit out of service.</summary>
    TripGenerator,
    /// <summary>Open a branch.</summary>
    TripBranch,
    /// <summary>Close a branch.</summary>
    CloseBranch,
    /// <summary>Shed load at a bus, in MW or as a fraction of the bus load, at constant power factor.</summary>
    ShedLoad,
    /// <summary>Set a unit's real power, absolute or as a change, within its Pmin and Pmax.</summary>
    SetGeneratorP,
    /// <summary>Change a bus shunt by a number of Mvar (at 1 pu, positive is capacitive).</summary>
    SwitchShunt,
    /// <summary>Set a branch's off-nominal tap ratio.</summary>
    SetTap,
}

/// <summary>
/// One arming condition or trigger of a scheme.
/// </summary>
/// <param name="Kind">What it tests.</param>
/// <param name="Element">The kind of element it names.</param>
/// <param name="Key">The element's key as written in the file (empty for none).</param>
/// <param name="Value">The threshold: MVA or MW for flows, pu for voltages, percent for loading. 0 when not used.</param>
/// <param name="InMw">For the pre-contingency flow conditions: true for MW (real power at the from end, signed),
/// false for MVA (the larger end's apparent power).</param>
/// <param name="Rating">For loadingAbove: 'A', 'B' or 'C'.</param>
/// <param name="AtToEnd">For flowAbove: true to read the real power into the branch at its to end, false (the default)
/// at its from end.</param>
public sealed record RasCondition(RasConditionKind Kind, RasElement Element, string Key, double Value, bool InMw = false,
    char Rating = 'A', bool AtToEnd = false)
{
    /// <summary>The element's index in the model after <see cref="RasSet.Resolve"/>: into <see cref="GridModel.Buses"/>,
    /// <see cref="GridModel.AllGenerators"/> or <see cref="GridModel.Branches"/>; -1 before, and for no element.</summary>
    public int Index { get; init; } = -1;
}

/// <summary>
/// One action of a scheme.
/// </summary>
/// <param name="Kind">What it does.</param>
/// <param name="Element">The kind of element it names.</param>
/// <param name="Key">The element's key as written in the file.</param>
/// <param name="Value">MW (shedLoad, setGeneratorP), the fraction (shedLoad with fraction), Mvar (switchShunt) or the
/// ratio (setTap); 0 for the trips and closes.</param>
/// <param name="Fraction">shedLoad: true when <paramref name="Value"/> is a fraction of the bus load.</param>
/// <param name="Delta">setGeneratorP: true when <paramref name="Value"/> is a change to the unit's output.</param>
public sealed record RasAction(RasActionKind Kind, RasElement Element, string Key, double Value = 0, bool Fraction = false, bool Delta = false)
{
    /// <summary>The element's index in the model after <see cref="RasSet.Resolve"/> (as <see cref="RasCondition.Index"/>).</summary>
    public int Index { get; init; } = -1;
}

/// <summary>
/// One remedial action scheme: armed when all its arming conditions hold on the pre-contingency state and the
/// contingency, it operates when its triggers hold (all of them, or any one) on the post-contingency state, up to
/// <paramref name="MaxOperations"/> times per contingency.
/// </summary>
/// <param name="Name">Unique name.</param>
/// <param name="Priority">Lower runs first within a level.</param>
/// <param name="Ordinal">The scheme's place in the file (0-based).</param>
/// <param name="Arming">The arming conditions, all of which must hold.</param>
/// <param name="Triggers">The triggers.</param>
/// <param name="AnyTrigger">True for logic "any", false for "all".</param>
/// <param name="Actions">The actions, applied in the order listed.</param>
/// <param name="MaxOperations">How many times the scheme may operate in one contingency.</param>
public sealed record RasScheme(string Name, int Priority, int Ordinal, IReadOnlyList<RasCondition> Arming,
    IReadOnlyList<RasCondition> Triggers, bool AnyTrigger, IReadOnlyList<RasAction> Actions, int MaxOperations);

/// <summary>
/// A set of remedial action schemes, read from a <c>gridsim-ras/1</c> file (RC5 WP26).
/// </summary>
/// <param name="Schemes">The schemes, in file order.</param>
/// <param name="MaxLevels">How many rounds of operation a contingency may take (default 5).</param>
public sealed record RasSet(IReadOnlyList<RasScheme> Schemes, int MaxLevels)
{
    /// <summary>True once every key has been resolved against a model (<see cref="Resolve"/>).</summary>
    public bool Resolved { get; init; }

    /// <summary>
    /// Resolve every element key against <paramref name="model"/> with the object-model key rules
    /// (<see cref="ElementKeys"/>): a bus by its id; a unit by <c>bus:id</c> when the PSS/E machine identities are given,
    /// else its name or <c>G&lt;bus&gt;</c>; a branch by <c>from-to:circuit</c> when the PSS/E branch identities are
    /// given, else its name or <c>from-to</c> (<c>#n</c> on a repeat). An unknown key, or a loading trigger on a branch
    /// with no such rating, is refused by name (<see cref="InvalidDataException"/>).
    /// </summary>
    public RasSet Resolve(GridModel model, IReadOnlyList<ElementKeys.MachineId>? machines = null,
        IReadOnlyList<ElementKeys.BranchId>? branches = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        IReadOnlyList<string> unitKeys = ElementKeys.Generators(model, machines);
        IReadOnlyList<string> branchKeys = ElementKeys.Branches(model, branches);
        var units = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < unitKeys.Count; i++) units[unitKeys[i]] = i;
        var brs = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < branchKeys.Count; i++) brs[branchKeys[i]] = i;
        var buses = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < model.Buses.Count; i++) buses.TryAdd(model.Buses[i].Id.ToString(CultureInfo.InvariantCulture), i);

        int Find(RasElement e, string key, string where) => e switch
        {
            RasElement.Branch => brs.TryGetValue(key, out int b) ? b
                : throw new InvalidDataException($"{where}: no branch has the key '{key}' in this case (branch keys look like '{(branchKeys.Count > 0 ? branchKeys[0] : "1-2")}')."),
            RasElement.Unit => units.TryGetValue(key, out int g) ? g
                : throw new InvalidDataException($"{where}: no generating unit has the key '{key}' in this case (unit keys look like '{(unitKeys.Count > 0 ? unitKeys[0] : "G1")}')."),
            RasElement.Bus => buses.TryGetValue(key, out int k) ? k
                : throw new InvalidDataException($"{where}: no bus has the id '{key}' in this case."),
            _ => -1,
        };

        var schemes = new List<RasScheme>(Schemes.Count);
        foreach (RasScheme s in Schemes)
        {
            var arming = new List<RasCondition>();
            for (int i = 0; i < s.Arming.Count; i++)
                arming.Add(s.Arming[i] with { Index = Find(s.Arming[i].Element, s.Arming[i].Key, $"scheme '{s.Name}' arming {i + 1}") });
            var triggers = new List<RasCondition>();
            for (int i = 0; i < s.Triggers.Count; i++)
            {
                RasCondition c = s.Triggers[i];
                string where = $"scheme '{s.Name}' trigger {i + 1}";
                int idx = Find(c.Element, c.Key, where);
                if (c.Kind == RasConditionKind.LoadingAbove)
                {
                    Branch b = model.Branches[idx];
                    double rating = c.Rating switch { 'B' => b.RateBMva, 'C' => b.RateCMva, _ => b.RateAMva };
                    if (!(rating > 0))
                        throw new InvalidDataException($"{where}: branch '{c.Key}' has no rating {c.Rating}, so its loading cannot be judged.");
                }
                triggers.Add(c with { Index = idx });
            }
            var actions = new List<RasAction>();
            for (int i = 0; i < s.Actions.Count; i++)
                actions.Add(s.Actions[i] with { Index = Find(s.Actions[i].Element, s.Actions[i].Key, $"scheme '{s.Name}' action {i + 1}") });
            schemes.Add(s with { Arming = arming, Triggers = triggers, Actions = actions });
        }
        return new RasSet(schemes, MaxLevels) { Resolved = true };
    }
}

/// <summary>
/// The strict reader of <c>gridsim-ras/1</c> files (RC5 WP26). Every key is checked: an unknown key, kind or value, a
/// missing required key, a duplicate scheme name or a non-finite number is refused with
/// <see cref="InvalidDataException"/> naming where.
/// <code>
/// { "schema": "gridsim-ras/1", "maxLevels": 5,
///   "schemes": [ { "name": "RAS-1", "priority": 1,
///                  "arming":   [ { "kind": "outage", "branch": "1-2:A" } ],
///                  "triggers": [ { "kind": "loadingAbove", "branch": "1-2:B", "percent": 100, "rating": "A" } ],
///                  "logic": "all",
///                  "actions":  [ { "kind": "tripGenerator", "unit": "1:2" } ],
///                  "maxOperations": 1 } ] }
/// </code>
/// </summary>
public static class RasFile
{
    /// <summary>The schema string of the file.</summary>
    public const string Schema = "gridsim-ras/1";

    /// <summary>The default number of levels (rounds of operation) per contingency.</summary>
    public const int DefaultMaxLevels = 5;

    /// <summary>The largest number of levels a file may ask for.</summary>
    public const int MaxMaxLevels = 100;

    /// <summary>The range a setTap ratio must lie in (pu of nominal). A guard against a units slip (a percentage or a kV
    /// value typed as a ratio), not an engineering limit: real off-nominal taps lie well inside it.</summary>
    public const double MinTapRatio = 0.5, MaxTapRatio = 2.0;

    /// <summary>The largest |mvar| a switchShunt step may ask for (Mvar at 1 pu). A guard against a units slip (kvar typed
    /// as Mvar), not an engineering limit.</summary>
    public const double MaxShuntMvar = 100_000;

    /// <summary>Read a file.</summary>
    public static RasSet ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Read the text of a file.</summary>
    public static RasSet Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException($"RAS file: not valid JSON ({ex.Message})."); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("RAS file: the top level must be an object.");
            Keys(root, "RAS file", "schema", "maxLevels", "schemes");
            string schema = Str(root, "schema", "RAS file");
            if (schema != Schema) throw new InvalidDataException($"RAS file: schema '{schema}' is not '{Schema}'.");
            int maxLevels = root.TryGetProperty("maxLevels", out _) ? Int(root, "maxLevels", "RAS file") : DefaultMaxLevels;
            if (maxLevels < 1 || maxLevels > MaxMaxLevels)
                throw new InvalidDataException($"RAS file: maxLevels must be a whole number from 1 to {MaxMaxLevels} (got {maxLevels}).");
            JsonElement list = Arr(root, "schemes", "RAS file");
            var schemes = new List<RasScheme>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            int ordinal = 0;
            foreach (JsonElement s in list.EnumerateArray())
            {
                RasScheme scheme = ReadScheme(s, ordinal);
                if (!names.Add(scheme.Name)) throw new InvalidDataException($"RAS file: two schemes are named '{scheme.Name}'.");
                schemes.Add(scheme);
                ordinal++;
            }
            if (schemes.Count == 0) throw new InvalidDataException("RAS file: 'schemes' is empty.");
            return new RasSet(schemes, maxLevels);
        }
    }

    private static RasScheme ReadScheme(JsonElement s, int ordinal)
    {
        string at = $"RAS file scheme {ordinal + 1}";
        if (s.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{at}: must be an object.");
        Keys(s, at, "name", "priority", "arming", "triggers", "logic", "actions", "maxOperations");
        string name = Str(s, "name", at);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException($"{at}: the name is empty.");
        at = $"RAS file scheme '{name}'";
        int priority = s.TryGetProperty("priority", out _) ? Int(s, "priority", at) : 0;
        int maxOps = s.TryGetProperty("maxOperations", out _) ? Int(s, "maxOperations", at) : 1;
        if (maxOps < 1) throw new InvalidDataException($"{at}: maxOperations must be 1 or more (got {maxOps}).");
        string logic = s.TryGetProperty("logic", out _) ? Str(s, "logic", at) : "all";
        if (logic is not ("all" or "any")) throw new InvalidDataException($"{at}: logic must be 'all' or 'any' (got '{logic}').");

        var arming = new List<RasCondition>();
        int i = 0;
        foreach (JsonElement c in Arr(s, "arming", at).EnumerateArray()) arming.Add(ReadArming(c, $"{at} arming {++i}"));
        if (arming.Count == 0) throw new InvalidDataException($"{at}: 'arming' is empty (use {{\"kind\": \"always\"}} for a scheme that is always armed).");
        var triggers = new List<RasCondition>();
        i = 0;
        foreach (JsonElement c in Arr(s, "triggers", at).EnumerateArray()) triggers.Add(ReadTrigger(c, $"{at} trigger {++i}"));
        if (triggers.Count == 0) throw new InvalidDataException($"{at}: 'triggers' is empty.");
        var actions = new List<RasAction>();
        i = 0;
        foreach (JsonElement a in Arr(s, "actions", at).EnumerateArray()) actions.Add(ReadAction(a, $"{at} action {++i}"));
        if (actions.Count == 0) throw new InvalidDataException($"{at}: 'actions' is empty.");
        return new RasScheme(name, priority, ordinal, arming, triggers, logic == "any", actions, maxOps);
    }

    private static RasCondition ReadArming(JsonElement c, string at)
    {
        if (c.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{at}: must be an object.");
        string kind = Str(c, "kind", at);
        switch (kind)
        {
            case "always":
                Keys(c, at, "kind");
                return new RasCondition(RasConditionKind.Always, RasElement.None, "", 0);
            case "outage":
            {
                Keys(c, at, "kind", "branch", "unit", "bus");
                int named = (c.TryGetProperty("branch", out _) ? 1 : 0) + (c.TryGetProperty("unit", out _) ? 1 : 0) + (c.TryGetProperty("bus", out _) ? 1 : 0);
                if (named != 1) throw new InvalidDataException($"{at}: an outage condition names exactly one of 'branch', 'unit' or 'bus'.");
                if (c.TryGetProperty("branch", out _)) return new RasCondition(RasConditionKind.Outage, RasElement.Branch, Str(c, "branch", at), 0);
                if (c.TryGetProperty("unit", out _)) return new RasCondition(RasConditionKind.Outage, RasElement.Unit, Str(c, "unit", at), 0);
                return new RasCondition(RasConditionKind.Outage, RasElement.Bus, BusKey(c, at), 0);
            }
            case "preFlowAbove":
            case "preFlowBelow":
            {
                Keys(c, at, "kind", "branch", "mva", "mw");
                bool mva = c.TryGetProperty("mva", out _), mw = c.TryGetProperty("mw", out _);
                if (mva == mw) throw new InvalidDataException($"{at}: give exactly one of 'mva' or 'mw'.");
                double v = Num(c, mw ? "mw" : "mva", at);
                return new RasCondition(kind == "preFlowAbove" ? RasConditionKind.PreFlowAbove : RasConditionKind.PreFlowBelow,
                    RasElement.Branch, Str(c, "branch", at), v, InMw: mw);
            }
            case "preVoltageBelow":
            case "preVoltageAbove":
            {
                Keys(c, at, "kind", "bus", "pu");
                return new RasCondition(kind == "preVoltageBelow" ? RasConditionKind.PreVoltageBelow : RasConditionKind.PreVoltageAbove,
                    RasElement.Bus, BusKey(c, at), Positive(c, "pu", at));
            }
            default:
                throw new InvalidDataException($"{at}: unknown arming kind '{kind}' (the kinds are outage, preFlowAbove, preFlowBelow, preVoltageBelow, preVoltageAbove, always).");
        }
    }

    private static RasCondition ReadTrigger(JsonElement c, string at)
    {
        if (c.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{at}: must be an object.");
        string kind = Str(c, "kind", at);
        switch (kind)
        {
            case "loadingAbove":
            {
                Keys(c, at, "kind", "branch", "percent", "rating");
                string rating = c.TryGetProperty("rating", out _) ? Str(c, "rating", at) : "A";
                if (rating is not ("A" or "B" or "C")) throw new InvalidDataException($"{at}: rating must be 'A', 'B' or 'C' (got '{rating}').");
                return new RasCondition(RasConditionKind.LoadingAbove, RasElement.Branch, Str(c, "branch", at), Positive(c, "percent", at), Rating: rating[0]);
            }
            case "flowAbove":
            {
                Keys(c, at, "kind", "branch", "mw", "atEnd");
                string end = c.TryGetProperty("atEnd", out _) ? Str(c, "atEnd", at) : "from";
                if (end is not ("from" or "to")) throw new InvalidDataException($"{at}: atEnd must be 'from' or 'to' (got '{end}').");
                return new RasCondition(RasConditionKind.FlowAbove, RasElement.Branch, Str(c, "branch", at), Num(c, "mw", at), InMw: true, AtToEnd: end == "to");
            }
            case "voltageBelow":
            case "voltageAbove":
            {
                Keys(c, at, "kind", "bus", "pu");
                return new RasCondition(kind == "voltageBelow" ? RasConditionKind.VoltageBelow : RasConditionKind.VoltageAbove,
                    RasElement.Bus, BusKey(c, at), Positive(c, "pu", at));
            }
            case "notConverged":
                Keys(c, at, "kind");
                return new RasCondition(RasConditionKind.NotConverged, RasElement.None, "", 0);
            default:
                throw new InvalidDataException($"{at}: unknown trigger kind '{kind}' (the kinds are loadingAbove, flowAbove, voltageBelow, voltageAbove, notConverged).");
        }
    }

    private static RasAction ReadAction(JsonElement a, string at)
    {
        if (a.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{at}: must be an object.");
        string kind = Str(a, "kind", at);
        switch (kind)
        {
            case "tripGenerator":
                Keys(a, at, "kind", "unit");
                return new RasAction(RasActionKind.TripGenerator, RasElement.Unit, Str(a, "unit", at));
            case "tripBranch":
                Keys(a, at, "kind", "branch");
                return new RasAction(RasActionKind.TripBranch, RasElement.Branch, Str(a, "branch", at));
            case "closeBranch":
                Keys(a, at, "kind", "branch");
                return new RasAction(RasActionKind.CloseBranch, RasElement.Branch, Str(a, "branch", at));
            case "shedLoad":
            {
                Keys(a, at, "kind", "bus", "mw", "fraction");
                bool mw = a.TryGetProperty("mw", out _), fr = a.TryGetProperty("fraction", out _);
                if (mw == fr) throw new InvalidDataException($"{at}: give exactly one of 'mw' or 'fraction'.");
                double v = Positive(a, fr ? "fraction" : "mw", at);
                if (fr && v > 1.0) throw new InvalidDataException($"{at}: fraction must be above 0 and at most 1 (got {v.ToString("R", CultureInfo.InvariantCulture)}).");
                return new RasAction(RasActionKind.ShedLoad, RasElement.Bus, BusKey(a, at), v, Fraction: fr);
            }
            case "setGeneratorP":
            {
                Keys(a, at, "kind", "unit", "mw", "mode");
                string mode = a.TryGetProperty("mode", out _) ? Str(a, "mode", at) : "absolute";
                if (mode is not ("absolute" or "delta")) throw new InvalidDataException($"{at}: mode must be 'absolute' or 'delta' (got '{mode}').");
                return new RasAction(RasActionKind.SetGeneratorP, RasElement.Unit, Str(a, "unit", at), Num(a, "mw", at), Delta: mode == "delta");
            }
            case "switchShunt":
            {
                Keys(a, at, "kind", "bus", "mvar");
                double v = Num(a, "mvar", at);
                if (v == 0) throw new InvalidDataException($"{at}: mvar must not be 0.");
                if (Math.Abs(v) > MaxShuntMvar)
                    throw new InvalidDataException($"{at}: |mvar| must be at most {MaxShuntMvar.ToString("R", CultureInfo.InvariantCulture)} (got {v.ToString("R", CultureInfo.InvariantCulture)}).");
                return new RasAction(RasActionKind.SwitchShunt, RasElement.Bus, BusKey(a, at), v);
            }
            case "setTap":
            {
                Keys(a, at, "kind", "branch", "ratio");
                double ratio = Positive(a, "ratio", at);
                if (ratio < MinTapRatio || ratio > MaxTapRatio)
                    throw new InvalidDataException(
                        $"{at}: ratio must be from {MinTapRatio.ToString("R", CultureInfo.InvariantCulture)} to {MaxTapRatio.ToString("R", CultureInfo.InvariantCulture)} (got {ratio.ToString("R", CultureInfo.InvariantCulture)}).");
                return new RasAction(RasActionKind.SetTap, RasElement.Branch, Str(a, "branch", at), ratio);
            }
            default:
                throw new InvalidDataException($"{at}: unknown action kind '{kind}' (the kinds are tripGenerator, tripBranch, closeBranch, shedLoad, setGeneratorP, switchShunt, setTap).");
        }
    }

    // ---- strict readers ------------------------------------------------------------------------------------------

    // Every object of the file passes through here: an unknown key, or a key given twice (JsonDocument keeps both and
    // TryGetProperty would silently read one of them), is refused by name.
    private static void Keys(JsonElement o, string at, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in o.EnumerateObject())
        {
            if (Array.IndexOf(allowed, p.Name) < 0)
                throw new InvalidDataException($"{at}: unknown key '{p.Name}' (allowed here: {string.Join(", ", allowed)}).");
            if (!seen.Add(p.Name))
                throw new InvalidDataException($"{at}: the key '{p.Name}' is given twice.");
        }
    }

    private static string Str(JsonElement o, string name, string at)
    {
        if (!o.TryGetProperty(name, out JsonElement v)) throw new InvalidDataException($"{at}: '{name}' is missing.");
        if (v.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{at}: '{name}' must be text.");
        return v.GetString()!;
    }

    private static JsonElement Arr(JsonElement o, string name, string at)
    {
        if (!o.TryGetProperty(name, out JsonElement v)) throw new InvalidDataException($"{at}: '{name}' is missing.");
        if (v.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{at}: '{name}' must be a list.");
        return v;
    }

    private static double Num(JsonElement o, string name, string at)
    {
        if (!o.TryGetProperty(name, out JsonElement v)) throw new InvalidDataException($"{at}: '{name}' is missing.");
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d))
            throw new InvalidDataException($"{at}: '{name}' must be a finite number.");
        return d;
    }

    private static double Positive(JsonElement o, string name, string at)
    {
        double d = Num(o, name, at);
        if (!(d > 0)) throw new InvalidDataException($"{at}: '{name}' must be above 0 (got {d.ToString("R", CultureInfo.InvariantCulture)}).");
        return d;
    }

    private static int Int(JsonElement o, string name, string at)
    {
        if (!o.TryGetProperty(name, out JsonElement v)) throw new InvalidDataException($"{at}: '{name}' is missing.");
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i)) throw new InvalidDataException($"{at}: '{name}' must be a whole number.");
        return i;
    }

    // A bus is named by its id: a whole number, or the same number as text.
    private static string BusKey(JsonElement o, string at)
    {
        if (!o.TryGetProperty("bus", out JsonElement v)) throw new InvalidDataException($"{at}: 'bus' is missing.");
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int id)) return id.ToString(CultureInfo.InvariantCulture);
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int sid))
            return sid.ToString(CultureInfo.InvariantCulture);
        throw new InvalidDataException($"{at}: 'bus' must be a bus id (a whole number).");
    }
}
