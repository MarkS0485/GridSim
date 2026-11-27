using System.Globalization;
using GridSim.Core.Api;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Gic;

/// <summary>What a DC element of the GIC network is.</summary>
public enum GicElementKind
{
    /// <summary>A substation's ground grid, neutral to remote earth.</summary>
    Ground,
    /// <summary>A line (three phases in parallel, with its field-induced source).</summary>
    Line,
    /// <summary>A grounded-wye HV winding, HV bus to the substation neutral.</summary>
    HvWinding,
    /// <summary>A grounded-wye LV winding, LV bus to the substation neutral.</summary>
    LvWinding,
    /// <summary>An autotransformer's series winding, H bus to X bus.</summary>
    Series,
    /// <summary>An autotransformer's common winding, X bus to the substation neutral.</summary>
    Common,
}

/// <summary>One DC element from node <see cref="A"/> to node <see cref="B"/> (-1 is remote earth), with conductance
/// <see cref="Siemens"/> (all three phases) and, for a line, its path vector (km). <see cref="Owner"/> is the index of the
/// line or transformer in the network's lists, or of the substation for a ground element.</summary>
public sealed record GicElement(GicElementKind Kind, int A, int B, double Siemens, double NorthKm, double EastKm, int Owner);

/// <summary>A line of the GIC network.</summary>
public sealed record GicLine(string Branch, int FromBus, int ToBus, double OhmsPerPhase, double NorthKm, double EastKm);

/// <summary>A transformer of the GIC network, with its HV and LV buses decided by base voltage.</summary>
public sealed record GicUnit(GicTransformer Data, int HvBus, int LvBus, double Ratio, double VoltagePu, int Substation);

/// <summary>
/// RC5 WP28: the DC network of a GIC study (NERC application guide, section on the network model and Appendix II), built
/// from a case and a <c>gridsim-gic/1</c> file. Nodes, in this fixed order: the substation neutrals (file order), then the
/// buses that any element touches (case order). Lines carry R/3 (three phases in parallel); a grounded-wye winding R/3
/// from its bus to the substation neutral; an autotransformer a series winding R_s/3 from H to X and a common winding R_c/3
/// from X to the neutral; delta windings are open; each substation neutral reaches remote earth through its ground grid.
/// A blocked transformer has its neutral open (the winding-to-neutral path is removed). Every in-service branch must be a
/// line between buses with a substation, or a transformer described in the file; anything else is refused by name.
/// </summary>
public sealed class GicNetwork
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The substations, in file order (node i is substation i's neutral).</summary>
    public IReadOnlyList<GicSubstation> Substations { get; }
    /// <summary>The bus of each bus node, in case order (node Substations.Count + i).</summary>
    public IReadOnlyList<int> BusNodes { get; }
    /// <summary>The lines, in case branch order.</summary>
    public IReadOnlyList<GicLine> Lines { get; }
    /// <summary>The transformers, in case branch order.</summary>
    public IReadOnlyList<GicUnit> Units { get; }
    /// <summary>The DC elements, in build order (grounds, then branches in case order).</summary>
    public IReadOnlyList<GicElement> Elements { get; }
    /// <summary>The SHA-256 of the gic file.</summary>
    public string FileSha256 { get; }

    /// <summary>The number of nodes.</summary>
    public int NodeCount => Substations.Count + BusNodes.Count;

    private GicNetwork(IReadOnlyList<GicSubstation> subs, IReadOnlyList<int> busNodes, IReadOnlyList<GicLine> lines,
        IReadOnlyList<GicUnit> units, IReadOnlyList<GicElement> elements, string sha)
    {
        Substations = subs; BusNodes = busNodes; Lines = lines; Units = units; Elements = elements; FileSha256 = sha;
    }

    /// <summary>Build the network. <paramref name="lineKm"/> replaces the computed path vector of a line, by its branch key
    /// (for checks against published distances); null computes every line from the coordinates.</summary>
    public static GicNetwork Build(GridModel model, GicFile file, IReadOnlyDictionary<string, (double NorthKm, double EastKm)>? lineKm = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(file);
        var busById = new Dictionary<int, Bus>();
        foreach (Bus b in model.Buses) busById.TryAdd(b.Id, b);
        var subOf = new Dictionary<int, int>();
        for (int s = 0; s < file.Substations.Count; s++)
            foreach (int id in file.Substations[s].Buses)
            {
                if (!busById.ContainsKey(id)) throw new InvalidDataException($"substation '{file.Substations[s].Name}': bus {id.ToString(Inv)} is not in the case.");
                subOf[id] = s;
            }

        IReadOnlyList<string> keys = ElementKeys.Branches(model, null);
        var keyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Count; i++) keyIndex[keys[i]] = i;
        var unitByBranch = new Dictionary<int, GicTransformer>();
        foreach (GicTransformer t in file.Transformers)
        {
            if (!keyIndex.TryGetValue(t.Branch, out int bi))
                throw new InvalidDataException($"transformer '{t.Branch}': no branch has this key in the case (branch keys look like '{(keys.Count > 0 ? keys[0] : "1-2")}').");
            unitByBranch[bi] = t;
        }
        var ohms = new Dictionary<int, double>();
        foreach (KeyValuePair<string, double> o in file.LineOhmsOverride)
        {
            if (!keyIndex.TryGetValue(o.Key, out int bi)) throw new InvalidDataException($"lineOhmsOverride '{o.Key}': no branch has this key in the case.");
            if (unitByBranch.ContainsKey(bi)) throw new InvalidDataException($"lineOhmsOverride '{o.Key}': the branch is a transformer in the file.");
            ohms[bi] = o.Value;
        }
        if (lineKm is not null)
            foreach (string k in lineKm.Keys.Order(StringComparer.Ordinal))
                if (!keyIndex.ContainsKey(k)) throw new InvalidDataException($"line distance '{k}': no branch has this key in the case.");

        int Sub(int bus, string what) => subOf.TryGetValue(bus, out int s) ? s
            : throw new InvalidDataException($"{what}: bus {bus.ToString(Inv)} is in no substation of the gic file.");

        // First pass: which buses are used, and the lines and transformers.
        var used = new HashSet<int>();
        var lines = new List<(int Branch, GicLine Line)>();
        var units = new List<(int Branch, GicUnit Unit)>();
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch br = model.Branches[i];
            if (!br.InService) continue;
            string key = keys[i];
            if (unitByBranch.TryGetValue(i, out GicTransformer? t))
            {
                Bus f = busById[br.From], to = busById[br.To];
                if (!(f.BaseKv > 0) || !(to.BaseKv > 0) || f.BaseKv == to.BaseKv)
                    throw new InvalidDataException($"transformer '{key}': the two buses need different base voltages above 0 to tell HV from LV (got {f.BaseKv.ToString("R", Inv)} and {to.BaseKv.ToString("R", Inv)} kV).");
                (Bus hv, Bus lv) = f.BaseKv > to.BaseKv ? (f, to) : (to, f);
                int sh = Sub(hv.Id, $"transformer '{key}'"), sl = Sub(lv.Id, $"transformer '{key}'");
                if (sh != sl) throw new InvalidDataException($"transformer '{key}': its buses are in different substations ('{file.Substations[sh].Name}' and '{file.Substations[sl].Name}').");
                units.Add((i, new GicUnit(t, hv.Id, lv.Id, hv.BaseKv / lv.BaseKv, hv.Vm, sh)));
                if (t.Type is GicTransformerType.Gsu or GicTransformerType.GyGy or GicTransformerType.Auto) used.Add(hv.Id);
                if (t.Type is GicTransformerType.GyGy or GicTransformerType.Auto or GicTransformerType.DGy) used.Add(lv.Id);
                continue;
            }
            if (br.IsTransformer || br.TapRatio != 1.0 || br.PhaseShiftDeg != 0.0)
                throw new InvalidDataException($"branch '{key}' is a transformer but the gic file does not describe it (add it to \"transformers\"; type \"open\" for no DC path).");
            int sf = Sub(br.From, $"line '{key}'"), st = Sub(br.To, $"line '{key}'");
            double r;
            if (!ohms.TryGetValue(i, out r))
            {
                double kv = busById[br.From].BaseKv;
                if (!(kv > 0)) throw new InvalidDataException($"line '{key}': bus {br.From.ToString(Inv)} has no base voltage, so its ohms are unknown (give lineOhmsOverride).");
                r = br.R * kv * kv / model.BaseMva;
                if (!(r > 0)) throw new InvalidDataException($"line '{key}': its resistance is {r.ToString("R", Inv)} ohms; a DC path needs more than 0 (give lineOhmsOverride).");
            }
            (double n, double e) = lineKm is not null && lineKm.TryGetValue(key, out var d) ? d
                : GicDistances.Between(file.Substations[sf].Latitude, file.Substations[sf].Longitude,
                    file.Substations[st].Latitude, file.Substations[st].Longitude, file.DistanceModel);
            lines.Add((i, new GicLine(key, br.From, br.To, r, n, e)));
            used.Add(br.From);
            used.Add(br.To);
        }

        int nSub = file.Substations.Count;
        var busNodes = new List<int>();
        var nodeOf = new Dictionary<int, int>();
        foreach (Bus b in model.Buses)
            if (used.Contains(b.Id) && nodeOf.TryAdd(b.Id, nSub + busNodes.Count)) busNodes.Add(b.Id);

        var el = new List<GicElement>();
        for (int s = 0; s < nSub; s++) el.Add(new GicElement(GicElementKind.Ground, s, -1, 1.0 / file.Substations[s].GroundOhms, 0, 0, s));
        // Branch elements in case branch order (lines and transformers interleaved as the case lists them).
        int li = 0, ui = 0;
        for (int i = 0; i < model.Branches.Count; i++)
        {
            if (li < lines.Count && lines[li].Branch == i)
            {
                GicLine l = lines[li].Line;
                el.Add(new GicElement(GicElementKind.Line, nodeOf[l.FromBus], nodeOf[l.ToBus], 3.0 / l.OhmsPerPhase, l.NorthKm, l.EastKm, li));
                li++;
            }
            else if (ui < units.Count && units[ui].Branch == i)
            {
                GicUnit u = units[ui].Unit;
                GicTransformer t = u.Data;
                int n = u.Substation;
                switch (t.Type)
                {
                    case GicTransformerType.Gsu:
                        if (!t.Blocked) el.Add(new GicElement(GicElementKind.HvWinding, nodeOf[u.HvBus], n, 3.0 / t.HvWindingOhms, 0, 0, ui));
                        break;
                    case GicTransformerType.GyGy:
                        if (!t.Blocked)
                        {
                            el.Add(new GicElement(GicElementKind.HvWinding, nodeOf[u.HvBus], n, 3.0 / t.HvWindingOhms, 0, 0, ui));
                            el.Add(new GicElement(GicElementKind.LvWinding, nodeOf[u.LvBus], n, 3.0 / t.LvWindingOhms, 0, 0, ui));
                        }
                        break;
                    case GicTransformerType.Auto:
                        el.Add(new GicElement(GicElementKind.Series, nodeOf[u.HvBus], nodeOf[u.LvBus], 3.0 / t.HvWindingOhms, 0, 0, ui));
                        if (!t.Blocked) el.Add(new GicElement(GicElementKind.Common, nodeOf[u.LvBus], n, 3.0 / t.LvWindingOhms, 0, 0, ui));
                        break;
                    case GicTransformerType.DGy:
                        if (!t.Blocked) el.Add(new GicElement(GicElementKind.LvWinding, nodeOf[u.LvBus], n, 3.0 / t.LvWindingOhms, 0, 0, ui));
                        break;
                    case GicTransformerType.Open:
                        break;
                }
                ui++;
            }
        }
        return new GicNetwork(file.Substations, busNodes, lines.Select(x => x.Line).ToList(), units.Select(x => x.Unit).ToList(), el, file.Sha256);
    }
}
