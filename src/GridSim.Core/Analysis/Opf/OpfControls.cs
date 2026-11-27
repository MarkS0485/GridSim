using System.Globalization;
using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Opf;

/// <summary>A continuous transformer tap control for <see cref="AcOpfExtended"/>: branch <see cref="Branch"/> (an index
/// into <see cref="GridModel.Branches"/>), its ratio range (tau on the from side) and its step (the discrete grid is
/// tau = MinTap + n StepPu).</summary>
public sealed record OpfTapControl(int Branch, double MinTap, double MaxTap, double StepPu);

/// <summary>A continuous shunt control at bus <see cref="BusId"/>: susceptance in MVAr at 1 pu (capacitive positive),
/// added to the bus's own case shunt. The discrete grid is B = BMinMvar + n StepMvar; <see cref="InitialMvar"/> is the
/// case setting, which breaks a rounding tie.</summary>
public sealed record OpfShuntControl(int BusId, double BMinMvar, double BMaxMvar, double StepMvar, double InitialMvar);

/// <summary>A phase-shifter angle control on branch <see cref="Branch"/> (an index into <see cref="GridModel.Branches"/>):
/// the shift phi of the MATPOWER branch model (t = tau e^{j phi} at the from end; positive phi as MATPOWER's SHIFT), its
/// range in degrees, and its step in degrees (the discrete grid is phi = MinDeg + n StepDeg; a step of 0 means the angle
/// is continuous and is not rounded). The tap ratio of the branch stays at its case value.</summary>
public sealed record OpfPhaseShiftControl(int Branch, double MinDeg, double MaxDeg, double StepDeg);

/// <summary>The continuous controls of an AC-OPF: taps, then shunts, then phase-shifter angles (the variable order).</summary>
public sealed record OpfControlSet(IReadOnlyList<OpfTapControl> Taps, IReadOnlyList<OpfShuntControl> Shunts)
{
    /// <summary>No controls.</summary>
    public static OpfControlSet None { get; } = new(Array.Empty<OpfTapControl>(), Array.Empty<OpfShuntControl>());

    /// <summary>Phase-shifter angle controls (variables after the shunts). Empty unless asked for.</summary>
    public IReadOnlyList<OpfPhaseShiftControl> PhaseShifts { get; init; } = Array.Empty<OpfPhaseShiftControl>();

    /// <summary>The number of control variables.</summary>
    public int Count => Taps.Count + Shunts.Count + PhaseShifts.Count;

    /// <summary>The format name of the controls file.</summary>
    public const string Format = "gridsim-opf-controls/1";

    /// <summary>
    /// Read a <c>gridsim-opf-controls/1</c> file against <paramref name="model"/>:
    /// <c>{"format": "gridsim-opf-controls/1", "taps": ["T1", ...] | "oltcs", "shunts": [{"bus": 2, "bMinMvar": 0,
    /// "bMaxMvar": 100, "stepMvar": 10, "initialMvar": 0}]}</c>. Every member but <c>format</c> is optional (format, if
    /// given, must be this one); <c>initialMvar</c> defaults to 0. A tap names a branch by its unique name; its range and
    /// step come from the model's <see cref="Oltc"/> on that branch, else the <see cref="Oltc"/> defaults (0.9 .. 1.1,
    /// 0.0125). <c>"oltcs"</c> takes every OLTC of the model, in model order. Unknown members, unknown or ambiguous names
    /// and a branch that is not a transformer are refused with <see cref="InvalidDataException"/>.
    /// <para><c>"pst": [{"branch": "PS1", "minDeg": -30, "maxDeg": 30, "stepDeg": 1}]</c> adds phase-shifter angle
    /// controls (an optional <c>"kind": "pst"</c> member is accepted). <c>minDeg</c> and <c>maxDeg</c> default to the
    /// angle range of the branch's active power flow control (PSS/E COD 3) when the case has one, and are needed
    /// otherwise; <c>stepDeg</c> defaults to that control's step, else 0 (continuous).</para>
    /// </summary>
    public static OpfControlSet Parse(string json, GridModel model)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(model);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException($"OPF controls: not valid JSON ({ex.Message})."); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OPF controls: the file must be a JSON object.");
            var taps = new List<OpfTapControl>();
            var shunts = new List<OpfShuntControl>();
            var psts = new List<OpfPhaseShiftControl>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (!seen.Add(prop.Name)) throw new InvalidDataException($"OPF controls: member '{prop.Name}' is given twice.");
                switch (prop.Name)
                {
                    case "format":
                        if (prop.Value.ValueKind != JsonValueKind.String || prop.Value.GetString() != Format)
                            throw new InvalidDataException($"OPF controls: format must be \"{Format}\".");
                        break;
                    case "taps":
                        ReadTaps(prop.Value, model, taps);
                        break;
                    case "shunts":
                        ReadShunts(prop.Value, shunts);
                        break;
                    case "pst":
                        ReadPhaseShifts(prop.Value, model, psts);
                        break;
                    default:
                        throw new InvalidDataException($"OPF controls: unknown member '{prop.Name}' (known: format, taps, shunts, pst).");
                }
            }
            return psts.Count == 0 ? new OpfControlSet(taps, shunts) : new OpfControlSet(taps, shunts) { PhaseShifts = psts };
        }
    }

    private static void ReadTaps(JsonElement e, GridModel model, List<OpfTapControl> taps)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            if (e.GetString() != "oltcs") throw new InvalidDataException("OPF controls: taps must be a list of branch names or the word \"oltcs\".");
            if (model.Oltcs.Count == 0) throw new InvalidDataException("OPF controls: taps is \"oltcs\" but the case has no OLTC.");
            foreach (Oltc o in model.Oltcs) taps.Add(TapFor(model, o.Transformer));
            return;
        }
        if (e.ValueKind != JsonValueKind.Array) throw new InvalidDataException("OPF controls: taps must be a list of branch names or the word \"oltcs\".");
        foreach (JsonElement t in e.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.String) throw new InvalidDataException("OPF controls: each tap must be a branch name (a string).");
            taps.Add(TapFor(model, t.GetString()!));
        }
    }

    private static OpfTapControl TapFor(GridModel model, string name)
    {
        int found = -1;
        for (int k = 0; k < model.Branches.Count; k++)
            if (string.Equals(model.Branches[k].Name, name, StringComparison.Ordinal))
            {
                if (found >= 0) throw new InvalidDataException($"OPF controls: tap '{name}' names more than one branch.");
                found = k;
            }
        if (found < 0) throw new InvalidDataException($"OPF controls: tap '{name}' names no branch of the case.");
        Oltc? oltc = null;
        foreach (Oltc o in model.Oltcs)
            if (string.Equals(o.Transformer, name, StringComparison.Ordinal)) { oltc = o; break; }
        Branch br = model.Branches[found];
        if (oltc is null && !br.IsTransformer)
            throw new InvalidDataException($"OPF controls: tap '{name}' is branch {found} ({br.From}-{br.To}), which is not a transformer.");
        oltc ??= new Oltc(name, br.To);
        return new OpfTapControl(found, oltc.MinTap, oltc.MaxTap, oltc.StepPu);
    }

    private static void ReadPhaseShifts(JsonElement e, GridModel model, List<OpfPhaseShiftControl> psts)
    {
        if (e.ValueKind != JsonValueKind.Array) throw new InvalidDataException("OPF controls: pst must be a list of objects.");
        foreach (JsonElement s in e.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OPF controls: each pst must be an object.");
            string? name = null;
            double? min = null, max = null, step = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in s.EnumerateObject())
            {
                if (!seen.Add(p.Name)) throw new InvalidDataException($"OPF controls: pst member '{p.Name}' is given twice.");
                switch (p.Name)
                {
                    case "kind":
                        if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() != "pst")
                            throw new InvalidDataException("OPF controls: a pst entry's kind must be \"pst\".");
                        break;
                    case "branch":
                        if (p.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("OPF controls: a pst's branch must be a branch name (a string).");
                        name = p.Value.GetString()!;
                        break;
                    case "minDeg": min = Num(p, "pst"); break;
                    case "maxDeg": max = Num(p, "pst"); break;
                    case "stepDeg": step = Num(p, "pst"); break;
                    default:
                        throw new InvalidDataException($"OPF controls: unknown pst member '{p.Name}' (known: kind, branch, minDeg, maxDeg, stepDeg).");
                }
            }
            if (name is null) throw new InvalidDataException("OPF controls: a pst needs a branch.");
            int found = -1;
            for (int k = 0; k < model.Branches.Count; k++)
                if (string.Equals(model.Branches[k].Name, name, StringComparison.Ordinal))
                {
                    if (found >= 0) throw new InvalidDataException($"OPF controls: pst '{name}' names more than one branch.");
                    found = k;
                }
            if (found < 0) throw new InvalidDataException($"OPF controls: pst '{name}' names no branch of the case.");
            TransformerFlowControl? fc = null;
            foreach (TransformerFlowControl c in model.TransformerFlowControls)
                if (c.Kind == TransformerFlowKind.ActivePower && string.Equals(c.Transformer, name, StringComparison.Ordinal)) { fc = c; break; }
            if ((min is null || max is null) && fc is null)
                throw new InvalidDataException($"OPF controls: pst '{name}' needs minDeg and maxDeg, because the case has no active power flow control (COD 3) on it to take the angle range from.");
            psts.Add(new OpfPhaseShiftControl(found, min ?? fc!.Min, max ?? fc!.Max, step ?? fc?.Step ?? 0.0));
        }
    }

    private static void ReadShunts(JsonElement e, List<OpfShuntControl> shunts)
    {
        if (e.ValueKind != JsonValueKind.Array) throw new InvalidDataException("OPF controls: shunts must be a list of objects.");
        foreach (JsonElement s in e.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OPF controls: each shunt must be an object.");
            int? bus = null;
            double? bmin = null, bmax = null, step = null, init = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in s.EnumerateObject())
            {
                if (!seen.Add(p.Name)) throw new InvalidDataException($"OPF controls: shunt member '{p.Name}' is given twice.");
                switch (p.Name)
                {
                    case "bus":
                        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out int b))
                            throw new InvalidDataException("OPF controls: a shunt's bus must be a whole number (the bus id).");
                        bus = b; break;
                    case "bMinMvar": bmin = Num(p); break;
                    case "bMaxMvar": bmax = Num(p); break;
                    case "stepMvar": step = Num(p); break;
                    case "initialMvar": init = Num(p); break;
                    default:
                        throw new InvalidDataException($"OPF controls: unknown shunt member '{p.Name}' (known: bus, bMinMvar, bMaxMvar, stepMvar, initialMvar).");
                }
            }
            if (bus is null || bmin is null || bmax is null || step is null)
                throw new InvalidDataException("OPF controls: a shunt needs bus, bMinMvar, bMaxMvar and stepMvar.");
            shunts.Add(new OpfShuntControl(bus.Value, bmin.Value, bmax.Value, step.Value, init ?? 0.0));
        }
    }

    private static double Num(JsonProperty p, string what = "shunt")
    {
        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out double v) || !double.IsFinite(v))
            throw new InvalidDataException($"OPF controls: {what} member '{p.Name}' must be a finite number, not {p.Value.GetRawText()}.");
        return v;
    }

    internal static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
