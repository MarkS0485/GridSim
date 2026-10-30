namespace GridSim.Ui.Assets.Editing.Elements;

// UI programme Phase 4.3: the bus editor (v1 buses.json, v2 network buses). The ZIP load model block, when written,
// shows its six shares under the block's row.
//
// Derived read-outs (display only, never written back):
//   Voltage:
//     Base impedance (ohm)        Zb = baseKv^2 / baseMva
//     Base current (kA)           Ib = baseMva / (sqrt(3) * baseKv)
//     Voltage magnitude (kV)      = vm * baseKv               (vm as written, or its default 1)
//     Own lower limit (kV)        = vminPu * baseKv           (only when vminPu is written)
//     Own upper limit (kV)        = vmaxPu * baseKv           (only when vmaxPu is written)
//   Shunt:
//     Shunt conductance (pu)      = gs / baseMva              (gs is MW at 1 pu)
//     Shunt susceptance (pu)      = bs / baseMva              (bs is Mvar at 1 pu)

/// <summary>The per-element editor of a bus (v1 buses.json, v2 network buses).</summary>
public sealed class BusEditorModel : ElementEditorModel
{
    /// <summary>The section titles, in order.</summary>
    public const string Identity = "Identity and type", Voltage = "Voltage", Shunt = "Shunt (at 1 pu)", Load = "Load model", Position = "Layout";

    internal static readonly SectionSpec[] Layout =
    {
        new(Identity, "id", "name", "type"),
        new(Voltage, "baseKv", "vm", "vaDeg", "vminPu", "vmaxPu"),
        new(Shunt, "gs", "bs"),
        new(Load, "zip"),
        new(Position, "x", "y"),
    };

    internal BusEditorModel(EditorContext c) : base(c, "Bus", Layout, Derive) { }

    private static IReadOnlyList<DerivedReadout> Derive(EditorContext c, string section)
    {
        var list = new List<DerivedReadout>();
        (double? kv, string kvWhy) = c.Num("baseKv");
        (double? mva, string mvaWhy) = c.BaseMva();
        if (section == Voltage)
        {
            if (kv is not double k || mva is not double s)
            {
                list.Add(DerivedReadout.NotShown("Base impedance", "ohm", kv is null ? kvWhy : mvaWhy, "baseKv^2 / baseMva"));
                return list;
            }
            list.Add(DerivedReadout.Of("Base impedance", k * k / s, "ohm", "baseKv^2 / baseMva = " + EditorContext.Show(k) + "^2 / " + EditorContext.Show(s)));
            // I = S / (sqrt(3) * V): the base current in kA from MVA and kV (line-to-line).
            list.Add(k > 0
                ? DerivedReadout.Of("Base current", s / (Math.Sqrt(3.0) * k), "kA", "baseMva / (sqrt(3) * baseKv)")
                : DerivedReadout.NotShown("Base current", "kA", "baseKv is not above 0", "baseMva / (sqrt(3) * baseKv)"));
            (double? vm, string vmWhy) = c.Num("vm");
            list.Add(vm is double v ? DerivedReadout.Of("Voltage magnitude", v * k, "kV", "vm * baseKv") : DerivedReadout.NotShown("Voltage magnitude", "kV", vmWhy, "vm * baseKv"));
            if (c.Written("vminPu") && c.Num("vminPu").Value is double lo) list.Add(DerivedReadout.Of("Own lower limit", lo * k, "kV", "vminPu * baseKv"));
            if (c.Written("vmaxPu") && c.Num("vmaxPu").Value is double hi) list.Add(DerivedReadout.Of("Own upper limit", hi * k, "kV", "vmaxPu * baseKv"));
        }
        else if (section == Shunt && mva is double s2)
        {
            if (c.Num("gs").Value is double g) list.Add(DerivedReadout.Of("Shunt conductance", g / s2, "pu", "gs / baseMva"));
            if (c.Num("bs").Value is double b) list.Add(DerivedReadout.Of("Shunt susceptance", b / s2, "pu", "bs / baseMva"));
        }
        return list;
    }
}
