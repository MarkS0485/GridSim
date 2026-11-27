using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Api;

/// <summary>One accepted write: the revision it made, the element, the attribute, the value before (as read) and after.</summary>
public sealed record CaseEdit(int Revision, ElementKind Kind, string Key, string Attribute, object? Before, double After);

/// <summary>A read or write the object model refuses (unknown kind, key or attribute, a read-only attribute, a value out of
/// range, an edit that would make the model invalid). Nothing has changed when it is thrown.</summary>
public sealed class CaseEditException(string message) : Exception(message);

/// <summary>
/// RC5 WP11: the scripting object model over one case. It holds an immutable <see cref="GridModel"/>, an ordered edit log
/// and a revision counter. Every write builds new element records with <c>with</c> and replaces the model; nothing is
/// mutated in place, so no study ever sees a half-applied edit, and a refused write changes nothing. Keys and attributes
/// are those of <see cref="ElementKeys"/> and <see cref="ElementAttributes"/>. <see cref="Set"/> never adds or removes an
/// element (status 0 takes one out of service, which keeps every key stable); RC5 WP15's <see cref="Add"/> and
/// <see cref="Delete"/> do (CaseEditor.Structure.cs).
/// </summary>
public sealed partial class CaseEditor
{
    private readonly List<ElementKey> _buses, _generators, _loads, _branches, _shunts;
    private readonly Dictionary<(ElementKind, string), ElementKey> _byKey = new();
    private readonly HashSet<string> _originalErrors;
    // The islands without a slack that the model as it stands has, accepted because a structural edit left them (see
    // CaseEditor.Structure.cs). Replaced by the current model's islands after every accepted edit, never accumulated.
    private HashSet<string> _acceptedIslands = new(StringComparer.Ordinal);
    private readonly List<CaseEdit> _edits = [];
    private Dictionary<int, (double Pd, double Qd)> _tripped = [];

    /// <summary>Hold <paramref name="model"/>. <paramref name="machines"/> and <paramref name="branches"/> are the PSS/E
    /// identities of the case's generators and branches when it came from a RAW file (null otherwise).</summary>
    public CaseEditor(GridModel model, IReadOnlyList<ElementKeys.MachineId>? machines = null, IReadOnlyList<ElementKeys.BranchId>? branches = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        Original = model;
        Model = model;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _buses = model.Buses.Select((b, i) => new ElementKey(ElementKind.Bus, b.Id.ToString(inv), i)).ToList();
        IReadOnlyList<string> gk = ElementKeys.Generators(model, machines);
        _generators = gk.Select((k, i) => new ElementKey(ElementKind.Generator, k, i)).ToList();
        _loads = model.Buses.Select((b, i) => (b, i)).Where(t => t.b.Pd != 0 || t.b.Qd != 0)
            .Select(t => new ElementKey(ElementKind.Load, t.b.Id.ToString(inv), t.i)).ToList();
        IReadOnlyList<string> bk = ElementKeys.Branches(model, branches);
        _branches = bk.Select((k, i) => new ElementKey(ElementKind.Branch, k, i)).ToList();
        _shunts = model.Buses.Select((b, i) => (b, i)).Where(t => t.b.Gs != 0 || t.b.Bs != 0)
            .Select(t => new ElementKey(ElementKind.Shunt, t.b.Id.ToString(inv), t.i)).ToList();
        foreach (IReadOnlyList<ElementKey> list in new[] { _buses, _generators, _loads, _branches, _shunts })
            foreach (ElementKey k in list) _byKey[(k.Kind, k.Key)] = k;
        _originalErrors = Errors(model);
    }

    /// <summary>RC5 REVIEW-FIX-6 (the level 2 rpc study check): null when every object model attribute of every element
    /// of <paramref name="back"/> equals that of <paramref name="held"/> (element counts and order the same; doubles
    /// compared bit for bit), and the system base is the same; otherwise the first difference, named.</summary>
    public static string? AttributeDifference(GridModel held, GridModel back)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(back);
        if (BitConverter.DoubleToInt64Bits(held.BaseMva) != BitConverter.DoubleToInt64Bits(back.BaseMva))
            return "the system base MVA differs";
        var a = new CaseEditor(held);
        var b = new CaseEditor(back);
        foreach (ElementKind kind in ElementKinds.All)
        {
            IReadOnlyList<ElementKey> ka = a.Elements(kind), kb = b.Elements(kind);
            if (ka.Count != kb.Count) return $"{ka.Count} {ElementKinds.Name(kind)} elements held, {kb.Count} read back";
            for (int i = 0; i < ka.Count; i++)
            {
                if (ka[i].Index != kb[i].Index) return $"{ElementKinds.Name(kind)} '{ka[i].Key}' is at another place in the model";
                IReadOnlyList<KeyValuePair<string, object?>> va = a.GetAll(kind, ka[i].Key), vb = b.GetAll(kind, kb[i].Key);
                for (int j = 0; j < va.Count; j++)
                {
                    bool same = (va[j].Value, vb[j].Value) switch
                    {
                        (double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
                        (null, null) => true,
                        (object x, object y) => x.Equals(y),
                        _ => false,
                    };
                    if (!same) return $"{ElementKinds.Name(kind)} '{ka[i].Key}' {va[j].Key} differs";
                }
            }
        }
        return null;
    }

    /// <summary>The model as loaded.</summary>
    public GridModel Original { get; }

    /// <summary>The model with every accepted edit applied.</summary>
    public GridModel Model { get; private set; }

    /// <summary>The number of accepted writes (0 for the case as loaded).</summary>
    public int Revision { get; private set; }

    /// <summary>Every accepted attribute change, in order.</summary>
    public IReadOnlyList<CaseEdit> Edits => _edits;

    /// <summary>The elements of a kind, in model order.</summary>
    public IReadOnlyList<ElementKey> Elements(ElementKind kind) => kind switch
    {
        ElementKind.Bus => _buses,
        ElementKind.Generator => _generators,
        ElementKind.Load => _loads,
        ElementKind.Branch => _branches,
        _ => _shunts,
    };

    /// <summary>The element of that key; refused naming the kind when there is none.</summary>
    public ElementKey Find(ElementKind kind, string key)
    {
        if (key is not null && _byKey.TryGetValue((kind, key), out ElementKey? k)) return k;
        string hint = kind switch
        {
            ElementKind.Load => " (a load is the bus id of a bus with demand when the case was loaded)",
            ElementKind.Shunt => " (a shunt is the bus id of a bus with Gs or Bs when the case was loaded)",
            _ => "",
        };
        throw new CaseEditException($"no {ElementKinds.Name(kind)} '{key}' in case '{Original.Name}'{hint}; 'elements' lists the keys.");
    }

    /// <summary>One attribute of one element, as it stands now.</summary>
    public object? Get(ElementKind kind, string key, string attribute)
    {
        ElementKey k = Find(kind, key);
        if (ElementAttributes.Find(kind, attribute) is null)
            throw new CaseEditException($"a {ElementKinds.Name(kind)} has no attribute '{attribute}'; its attributes are: {ElementAttributes.List(kind)}.");
        return Read(Model, _tripped, k, attribute);
    }

    /// <summary>Every attribute of one element, in the table's order.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> GetAll(ElementKind kind, string key)
    {
        ElementKey k = Find(kind, key);
        return ElementAttributes.Of(kind).Select(a => new KeyValuePair<string, object?>(a.Name, Read(Model, _tripped, k, a.Name))).ToList();
    }

    /// <summary>
    /// Write one or more attributes of one element at once and return the new revision. Every value is checked before any is
    /// applied, and the edited model is checked as the loaders check one; any refusal leaves the model, the log and the
    /// revision exactly as they were.
    /// </summary>
    public int Set(ElementKind kind, string key, IReadOnlyList<KeyValuePair<string, double>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ElementKey k = Find(kind, key);
        if (values.Count == 0) throw new CaseEditException("no attribute was given to set.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, double v) in values)
        {
            ElementField? a = ElementAttributes.Find(kind, name)
                ?? throw new CaseEditException($"a {ElementKinds.Name(kind)} has no attribute '{name}'; its attributes are: {ElementAttributes.List(kind)}.");
            if (a.ReadOnly) throw new CaseEditException($"{ElementKinds.Name(kind)} attribute '{name}' is read only.");
            if (!names.Add(name)) throw new CaseEditException($"'{name}' is given twice.");
            if (!double.IsFinite(v)) throw new CaseEditException($"'{name}' must be a finite number.");
            try
            {
                if (name == "status") ElementAttributes.StatusOf(v);
                if (name == "type") ElementAttributes.BusTypeOf(v);
            }
            catch (ArgumentException ex) { throw new CaseEditException($"{ElementKinds.Name(kind)} '{key}': {ex.Message}; got {v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}."); }
        }

        var buses = Model.Buses.ToArray();
        var fleet = Model.AllGenerators.ToArray();
        var branches = Model.Branches.ToArray();
        var tripped = new Dictionary<int, (double Pd, double Qd)>(_tripped);
        bool busesChanged = false, fleetChanged = false, branchesChanged = false;
        var log = new List<CaseEdit>();
        GridModel view = Model;
        foreach ((string name, double v) in values)
        {
            object? before = Read(view, tripped, k, name);
            switch (kind)
            {
                case ElementKind.Bus:
                {
                    Bus b = buses[k.Index];
                    buses[k.Index] = name switch
                    {
                        "type" => b with { Type = ElementAttributes.BusTypeOf(v) },
                        "vm" => b with { Vm = v },
                        "va_deg" => b with { VaDeg = v },
                        "vmin" => b with { VminPu = v },
                        _ => b with { VmaxPu = v },
                    };
                    busesChanged = true;
                    break;
                }
                case ElementKind.Generator:
                {
                    Generator g = fleet[k.Index];
                    fleet[k.Index] = name switch
                    {
                        "pg" => g with { Pg = v },
                        "qg" => g with { Qg = v },
                        "vg" => g with { Vg = v },
                        "qmin" => g with { Qmin = v },
                        "qmax" => g with { Qmax = v },
                        "pmin" => g with { Pmin = v },
                        "pmax" => g with { Pmax = v },
                        "mbase" => g with { MBase = v },
                        _ => g with { InService = ElementAttributes.StatusOf(v) },
                    };
                    fleetChanged = true;
                    break;
                }
                case ElementKind.Load:
                {
                    Bus b = buses[k.Index];
                    bool off = tripped.TryGetValue(k.Index, out (double Pd, double Qd) kept);
                    switch (name)
                    {
                        case "status":
                            bool on = ElementAttributes.StatusOf(v);
                            if (on && off) { buses[k.Index] = b with { Pd = kept.Pd, Qd = kept.Qd }; tripped.Remove(k.Index); }
                            else if (!on && !off) { tripped[k.Index] = (b.Pd, b.Qd); buses[k.Index] = b with { Pd = 0, Qd = 0 }; }
                            break;
                        case "pd":
                            if (off) tripped[k.Index] = (v, kept.Qd); else buses[k.Index] = b with { Pd = v };
                            break;
                        default:
                            if (off) tripped[k.Index] = (kept.Pd, v); else buses[k.Index] = b with { Qd = v };
                            break;
                    }
                    busesChanged = true;
                    break;
                }
                case ElementKind.Branch:
                {
                    Branch br = branches[k.Index];
                    branches[k.Index] = name switch
                    {
                        "r" => br with { R = v },
                        "x" => br with { X = v },
                        "b" => br with { B = v },
                        "tap" => br with { TapRatio = v },
                        "shift_deg" => br with { PhaseShiftDeg = v },
                        "rate_a" => br with { RateAMva = v },
                        "rate_b" => br with { RateBMva = v },
                        "rate_c" => br with { RateCMva = v },
                        _ => br with { InService = ElementAttributes.StatusOf(v) },
                    };
                    branchesChanged = true;
                    break;
                }
                default:
                {
                    Bus b = buses[k.Index];
                    buses[k.Index] = name == "gs" ? b with { Gs = v } : b with { Bs = v };
                    busesChanged = true;
                    break;
                }
            }
            view = Model.CopyWith(buses: busesChanged ? buses.ToArray() : null, generators: fleetChanged ? fleet.ToArray() : null,
                branches: branchesChanged ? branches.ToArray() : null, generatorsAreWholeFleet: fleetChanged);
            log.Add(new CaseEdit(Revision + 1, kind, k.Key, name, before, v));
        }

        HashSet<string> all = Errors(view);
        var errors = new HashSet<string>(all, StringComparer.Ordinal);
        errors.ExceptWith(_originalErrors);
        errors.ExceptWith(_acceptedIslands);
        if (errors.Count > 0)
            throw new CaseEditException($"the edit is refused: the model would not be valid ({errors.Order(StringComparer.Ordinal).First()}); nothing was changed.");
        _acceptedIslands = Islands(all);
        Model = view;
        _tripped = tripped;
        Revision++;
        _edits.AddRange(log);
        return Revision;
    }

    /// <summary>The island-without-slack errors among <paramref name="errors"/>.</summary>
    private static HashSet<string> Islands(HashSet<string> errors) =>
        new(errors.Where(e => e.StartsWith(IslandErrorPrefix, StringComparison.Ordinal)), StringComparer.Ordinal);

    private static HashSet<string> Errors(GridModel m) =>
        new(m.Validate().Where(d => d.Severity == ValidationSeverity.Error).Select(d => d.Message), StringComparer.Ordinal);

    private static object? Read(GridModel m, IReadOnlyDictionary<int, (double Pd, double Qd)> tripped, ElementKey k, string attribute)
    {
        switch (k.Kind)
        {
            case ElementKind.Bus:
            {
                Bus b = m.Buses[k.Index];
                return attribute switch
                {
                    "type" => (double)ElementAttributes.BusTypeCode(b.Type),
                    "vm" => b.Vm,
                    "va_deg" => b.VaDeg,
                    "base_kv" => b.BaseKv,
                    "name" => b.Name,
                    "vmin" => b.VminPu,
                    _ => b.VmaxPu,
                };
            }
            case ElementKind.Generator:
            {
                Generator g = m.AllGenerators[k.Index];
                return attribute switch
                {
                    "pg" => g.Pg,
                    "qg" => g.Qg,
                    "vg" => g.Vg,
                    "qmin" => g.Qmin,
                    "qmax" => g.Qmax,
                    "pmin" => g.Pmin,
                    "pmax" => g.Pmax,
                    "mbase" => g.MBase,
                    _ => g.InService ? 1.0 : 0.0,
                };
            }
            case ElementKind.Load:
            {
                Bus b = m.Buses[k.Index];
                bool off = tripped.TryGetValue(k.Index, out (double Pd, double Qd) kept);
                return attribute switch
                {
                    "pd" => off ? kept.Pd : b.Pd,
                    "qd" => off ? kept.Qd : b.Qd,
                    _ => off ? 0.0 : 1.0,
                };
            }
            case ElementKind.Branch:
            {
                Branch br = m.Branches[k.Index];
                return attribute switch
                {
                    "r" => br.R,
                    "x" => br.X,
                    "b" => br.B,
                    "tap" => br.TapRatio,
                    "shift_deg" => br.PhaseShiftDeg,
                    "rate_a" => br.RateAMva,
                    "rate_b" => br.RateBMva,
                    "rate_c" => br.RateCMva,
                    _ => br.InService ? 1.0 : 0.0,
                };
            }
            default:
            {
                Bus b = m.Buses[k.Index];
                return attribute == "gs" ? b.Gs : b.Bs;
            }
        }
    }
}

/// <summary>
/// A read-only view of a power-flow result in the object model's terms: per bus the solved magnitude and angle by bus id,
/// per branch the flows of <see cref="PowerFlowResult.LineFlows"/> (index-aligned with the model's branches). Nothing is
/// re-derived: every number is the result's own.
/// </summary>
public sealed class LoadFlowView
{
    private readonly Dictionary<int, int> _busRow = [];

    public LoadFlowView(GridModel model, PowerFlowResult result)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        Model = model;
        Result = result;
        for (int i = 0; i < result.BusIds.Count; i++) _busRow[result.BusIds[i]] = i;
    }

    public GridModel Model { get; }
    public PowerFlowResult Result { get; }
    public bool Converged => Result.Converged;

    /// <summary>The solved (vm pu, va degrees) of a bus, or null when the bus is not in the solved state (a dead island).</summary>
    public (double Vm, double VaDeg)? Bus(int id) => _busRow.TryGetValue(id, out int i) ? (Result.Vm[i], Result.VaDeg[i]) : null;

    /// <summary>The flows of the branch at <paramref name="index"/> in <see cref="GridModel.Branches"/>.</summary>
    public LineFlow Branch(int index) => Result.LineFlows[index];
}
