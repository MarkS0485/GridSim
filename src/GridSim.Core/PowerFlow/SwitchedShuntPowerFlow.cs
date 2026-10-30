using System.Text.Json;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// F22 (October 2026): a discrete switched shunt (capacitor or reactor bank) at a bus, switched in whole steps to keep
/// the bus voltage inside a band. <see cref="StepMvar"/> is the reactive injection of one step at 1 pu voltage (positive =
/// capacitive, negative = reactive), added to the bus's fixed shunt Bs exactly as a MATPOWER Bs (MVAr at 1 pu) is; the
/// bank sits at a whole number of steps between <see cref="MinSteps"/> and <see cref="MaxSteps"/>, starting at
/// <see cref="InitialSteps"/>.
/// </summary>
public sealed record SwitchedShunt(int Bus, double StepMvar, int MinSteps, int MaxSteps, int InitialSteps, double VminPu, double VmaxPu, string? Name = null)
{
    /// <summary>The bus whose voltage the bank holds in its band, or null for its own bus (the usual case). A remote
    /// bus (PSS/E SWREM / SWREG) is regulated the same way: switching in raises it.</summary>
    public int? ControlsBus { get; init; }

    /// <summary>Unequal blocks (PSS/E switches blocks in the order they are listed): the susceptance (Mvar at 1 pu)
    /// the bank adds at each step from <see cref="MinSteps"/> to <see cref="MaxSteps"/>, increasing, 0 at
    /// <see cref="InitialSteps"/>; null for equal steps of <see cref="StepMvar"/> (the usual case). When set,
    /// <see cref="StepMvar"/> must be positive (a step up is more capacitive) and gives only the direction.</summary>
    public IReadOnlyList<double>? StepLevelsMvar { get; init; }

    /// <summary>The susceptance (Mvar at 1 pu) the bank adds at step <paramref name="step"/>.</summary>
    public double AddedMvar(int step) => StepLevelsMvar is { } l ? l[step - MinSteps] : step * StepMvar;
}

/// <summary>
/// F22 (October 2026): the power flow with discrete switched shunts, as an outer loop around an unchanged engine (the
/// way PSS/E-style discrete shunt control and GridSim's own tap-changer loop work). After each converged solve every
/// bank whose bus voltage is below its band switches IN one step (more capacitive), and every bank above its band
/// switches OUT one step, within its step range; the network is re-solved with the new shunts until no bank moves.
/// A bank that reverses direction is frozen at its current step (it is hunting across a band narrower than one step),
/// which guarantees the loop ends. Each pass is an ordinary engine solve, so the dense and sparse engines give the same
/// bits. <see cref="ShuntsSettled"/> is false when the pass cap stopped the loop with a bank still wanting to move, or
/// when a bank had to be frozen while outside its band; a caller must not report that state as regulated.
/// Opt-in: only used when asked (the CLI <c>pf --switched-shunts</c>).
/// </summary>
public sealed class SwitchedShuntPowerFlow : IPowerFlowSolver
{
    /// <summary>Default cap on switching passes.</summary>
    public const int DefaultMaxPasses = 50;

    private readonly IPowerFlowSolver _engine;
    private readonly IReadOnlyList<SwitchedShunt> _shunts;
    private readonly int _maxPasses;

    /// <summary>Wrap <paramref name="engine"/> with the banks <paramref name="shunts"/> (each checked: a finite non-zero
    /// step, MinSteps &lt;= InitialSteps &lt;= MaxSteps, 0 &lt; VminPu &lt; VmaxPu).</summary>
    public SwitchedShuntPowerFlow(IPowerFlowSolver engine, IReadOnlyList<SwitchedShunt> shunts, int maxPasses = DefaultMaxPasses)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(shunts);
        if (maxPasses < 1) throw new ArgumentOutOfRangeException(nameof(maxPasses), maxPasses, "at least one pass is needed");
        for (int i = 0; i < shunts.Count; i++)
        {
            SwitchedShunt s = shunts[i];
            string who = $"switched shunt {i + 1} ({s.Name ?? $"bus {s.Bus}"})";
            if (!double.IsFinite(s.StepMvar) || s.StepMvar == 0.0) throw new ArgumentException($"{who}: stepMvar must be finite and non-zero.");
            if (s.MinSteps > s.MaxSteps || s.InitialSteps < s.MinSteps || s.InitialSteps > s.MaxSteps)
                throw new ArgumentException($"{who}: the steps must satisfy minSteps <= initialSteps <= maxSteps ({s.MinSteps}, {s.InitialSteps}, {s.MaxSteps}).");
            if (!(s.VminPu > 0.0) || !(s.VmaxPu > s.VminPu) || !double.IsFinite(s.VmaxPu))
                throw new ArgumentException($"{who}: the band must satisfy 0 < vminPu < vmaxPu ({s.VminPu}, {s.VmaxPu}).");
            if (s.StepLevelsMvar is { } lv)
            {
                if (!(s.StepMvar > 0)) throw new ArgumentException($"{who}: a bank with step levels needs a positive stepMvar (its direction).");
                if (lv.Count != s.MaxSteps - s.MinSteps + 1) throw new ArgumentException($"{who}: {lv.Count} step levels for steps {s.MinSteps}..{s.MaxSteps}.");
                for (int k = 1; k < lv.Count; k++)
                    if (!(lv[k] > lv[k - 1])) throw new ArgumentException($"{who}: the step levels must increase.");
                if (lv[s.InitialSteps - s.MinSteps] != 0) throw new ArgumentException($"{who}: the step level at the initial step must be 0.");
            }
        }
        _engine = engine; _shunts = shunts.ToArray(); _maxPasses = maxPasses;
    }

    /// <summary>Switching passes (engine solves) taken by the last <see cref="Solve"/>.</summary>
    public int LastPasses { get; private set; }

    /// <summary>Each bank's final step count in the last <see cref="Solve"/>, aligned to the banks given.</summary>
    public IReadOnlyList<int> LastSteps { get; private set; } = Array.Empty<int>();

    /// <summary>Whether every bank ended inside its band or at the end of its range with nothing more to do.</summary>
    public bool ShuntsSettled { get; private set; }

    /// <summary>Why the last <see cref="Solve"/> did not settle (null when it did).</summary>
    public string? LastNote { get; private set; }

    /// <summary>Solve <paramref name="model"/> with the switched shunts regulating.</summary>
    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var busIds = new HashSet<int>(model.Buses.Select(b => b.Id));
        foreach (SwitchedShunt s in _shunts)
        {
            if (!busIds.Contains(s.Bus)) throw new ArgumentException($"switched shunt {s.Name ?? ""} sits on bus {s.Bus}, which is not in the model.");
            if (s.ControlsBus is int cb && !busIds.Contains(cb)) throw new ArgumentException($"switched shunt {s.Name ?? ""} regulates bus {cb}, which is not in the model.");
        }

        int n = _shunts.Count;
        var steps = new int[n];
        var lastDir = new int[n];
        var frozen = new bool[n];
        for (int i = 0; i < n; i++) steps[i] = _shunts[i].InitialSteps;
        var exhausted = new List<string>();   // banks at the end of their range with the bus still outside its band (this pass)
        LastNote = null;
        ShuntsSettled = false;
        PowerFlowResult? r = null;
        for (int pass = 1; pass <= _maxPasses; pass++)
        {
            LastPasses = pass;
            r = _engine.Solve(WithShunts(model, steps), options);
            LastSteps = (int[])steps.Clone();
            if (!r.Converged) { LastNote = $"the engine did not converge on pass {pass}"; return r; }

            var vmById = new Dictionary<int, double>(r.BusIds.Count);
            for (int i = 0; i < r.BusIds.Count; i++) vmById.TryAdd(r.BusIds[i], r.Vm[i]);
            bool moved = false, outsideFrozen = false;
            for (int i = 0; i < n; i++)
            {
                SwitchedShunt s = _shunts[i];
                double vm = vmById[s.ControlsBus ?? s.Bus];
                // "In" = more capacitive: one step of positive StepMvar raises the voltage, of negative lowers it.
                int want = vm < s.VminPu ? +1 : vm > s.VmaxPu ? -1 : 0;
                if (want == 0) continue;
                if (frozen[i]) { outsideFrozen = true; continue; }
                int dir = s.StepMvar > 0 ? want : -want;           // the step direction that moves the voltage the right way
                int next = steps[i] + dir;
                // At the end of its range with its bus still out of band: nothing more it can do, but the bus is NOT
                // regulated, so the result must not read settled (the OLTC loop reports the same case as unsettled).
                if (next < s.MinSteps || next > s.MaxSteps) { exhausted.Add(s.Name ?? $"bank@{s.Bus}"); continue; }
                if (lastDir[i] != 0 && dir != lastDir[i]) { frozen[i] = true; outsideFrozen = true; continue; }   // hunting
                steps[i] = next;
                lastDir[i] = dir;
                moved = true;
            }
            if (!moved)
            {
                ShuntsSettled = !outsideFrozen && exhausted.Count == 0;
                if (outsideFrozen) LastNote = "a bank was frozen while its bus was outside its band (it hunts across a band narrower than one step)";
                if (exhausted.Count > 0)
                    LastNote = (LastNote is null ? "" : LastNote + "; ")
                        + $"{exhausted.Count} bank(s) at the end of their step range with the bus still outside its band: {string.Join(", ", exhausted)}";
                return r;
            }
            exhausted.Clear();
        }
        LastNote = $"the switched shunts did not settle within {_maxPasses} passes";
        return r!;
    }

    /// <summary>The model with each bank's steps added to its bus's Bs (MVAr at 1 pu, as MATPOWER's Bs).</summary>
    private GridModel WithShunts(GridModel model, int[] steps)
    {
        var add = new Dictionary<int, double>();
        for (int i = 0; i < _shunts.Count; i++)
        {
            // Skip a bank only when it adds nothing at its present step. "Step 0" is not that test: with unequal blocks
            // (StepLevelsMvar) the level that is 0 is the one at InitialSteps, so a bank built with InitialSteps != 0
            // that stepped through 0 had its (non-zero) step-0 susceptance dropped from the bus, and the voltage the
            // loop read disagreed with the reported step. With equal steps the two tests agree (step 0 adds 0).
            double added = _shunts[i].AddedMvar(steps[i]);
            if (added == 0) continue;
            add[_shunts[i].Bus] = add.GetValueOrDefault(_shunts[i].Bus) + added;
        }
        if (add.Count == 0) return model;
        var buses = new List<Bus>(model.Buses.Count);
        foreach (Bus b in model.Buses) buses.Add(add.TryGetValue(b.Id, out double q) ? b with { Bs = b.Bs + q } : b);
        return model.CopyWith(buses: buses);
    }

    /// <summary>The format tag of the switched-shunt file.</summary>
    public const string FileFormat = "gridsim-switched-shunts/1";

    /// <summary>
    /// Read a switched-shunt file:
    /// <c>{ "format": "gridsim-switched-shunts/1", "shunts": [ { "bus": 5, "stepMvar": 10, "minSteps": 0, "maxSteps": 4,
    /// "initialSteps": 0, "vminPu": 0.97, "vmaxPu": 1.03, "name": "C5" } ] }</c>. Every key is checked (an unknown key is
    /// refused); bus, stepMvar, maxSteps, vminPu and vmaxPu are required; minSteps and initialSteps default to 0. Throws
    /// <see cref="InvalidDataException"/> naming the entry.
    /// </summary>
    public static IReadOnlyList<SwitchedShunt> ReadFile(string json, string label)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException($"switched-shunt file {label}: not valid JSON ({ex.Message})."); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"switched-shunt file {label}: the top level must be an object.");
            string? format = null;
            JsonElement? list = null;
            foreach (JsonProperty p in root.EnumerateObject())
            {
                if (p.Name == "format") format = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                else if (p.Name == "shunts") list = p.Value;
                else throw new InvalidDataException($"switched-shunt file {label}: unknown key '{p.Name}' (allowed: format, shunts).");
            }
            if (format != FileFormat) throw new InvalidDataException($"switched-shunt file {label}: \"format\" must be \"{FileFormat}\".");
            if (list is not { ValueKind: JsonValueKind.Array } arr) throw new InvalidDataException($"switched-shunt file {label}: \"shunts\" must be an array.");
            var shunts = new List<SwitchedShunt>();
            int entry = 0;
            foreach (JsonElement e in arr.EnumerateArray())
            {
                entry++;
                string where = $"switched-shunt file {label}, shunts entry {entry}";
                if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where}: must be an object.");
                int? bus = null, maxSteps = null; int minSteps = 0, initialSteps = 0;
                double? step = null, vmin = null, vmax = null; string? name = null;
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "bus": bus = Int(p.Value, where, "bus"); break;
                        case "stepMvar": step = Num(p.Value, where, "stepMvar"); break;
                        case "minSteps": minSteps = Int(p.Value, where, "minSteps"); break;
                        case "maxSteps": maxSteps = Int(p.Value, where, "maxSteps"); break;
                        case "initialSteps": initialSteps = Int(p.Value, where, "initialSteps"); break;
                        case "vminPu": vmin = Num(p.Value, where, "vminPu"); break;
                        case "vmaxPu": vmax = Num(p.Value, where, "vmaxPu"); break;
                        case "name":
                            if (p.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{where}: \"name\" must be a string.");
                            name = p.Value.GetString(); break;
                        default:
                            throw new InvalidDataException($"{where}: unknown key '{p.Name}' (allowed: bus, stepMvar, minSteps, maxSteps, initialSteps, vminPu, vmaxPu, name).");
                    }
                }
                if (bus is null || step is null || maxSteps is null || vmin is null || vmax is null)
                    throw new InvalidDataException($"{where}: bus, stepMvar, maxSteps, vminPu and vmaxPu are required.");
                shunts.Add(new SwitchedShunt(bus.Value, step.Value, minSteps, maxSteps.Value, initialSteps, vmin.Value, vmax.Value, name));
            }
            return shunts;
        }
    }

    private static int Int(JsonElement v, string where, string key)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i)) throw new InvalidDataException($"{where}: \"{key}\" must be a whole number.");
        return i;
    }

    private static double Num(JsonElement v, string where, string key)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d))
            throw new InvalidDataException($"{where}: \"{key}\" must be a finite number.");
        return d;
    }
}
