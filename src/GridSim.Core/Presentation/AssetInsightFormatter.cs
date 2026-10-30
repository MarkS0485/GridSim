using GridSim.Core.IO;

namespace GridSim.Core.Presentation;

/// <summary>
/// The display rows of the GDA per-asset card on a substation page (the bridge <c>op="asset"</c> drill-down): the
/// generator envelope (MSG / Pmax), battery storage ratings, tap-changer wear and connected consumers, each block
/// capped at <see cref="BlockCap"/> rows with an "...and N more" line.
///
/// <para>N063: moved here, text for text, from the WPF <c>SubstationDetailVm</c> (where it was private), so the web
/// substation page shows the same rows from the one copy instead of a "TODO(parity)" note. Nothing is computed: each
/// row only arranges the dataset's own formatted field values.</para>
/// </summary>
public static class AssetInsightFormatter
{
    /// <summary>Rows shown per block before the "...and N more" line.</summary>
    public const int BlockCap = 10;

    /// <summary>One display row: <see cref="Group"/> is the block title on the block's first row, else "".</summary>
    public sealed record Row(string Group, string Head, string Sub)
    {
        /// <summary>True on the first row of a block (the one carrying its title).</summary>
        public bool HasGroup => Group.Length > 0;
    }

    /// <summary>All rows for one bus's asset payload, in block order; empty when <paramref name="asset"/> is null or
    /// carries no rows.</summary>
    public static IReadOnlyList<Row> Rows(AssetData? asset)
    {
        var rows = new List<Row>();
        if (asset is null) return rows;
        AddBlock(rows, "GENERATOR ENVELOPE  (MSG / Pmax)", asset.Envelope, EnvelopeRow);
        AddBlock(rows, "BATTERY STORAGE  (SoC / ratings)", asset.BessSoc, BessRow);
        AddBlock(rows, "TAP-CHANGER WEAR", asset.TapWear, TapRow);
        AddBlock(rows, "CONSUMERS / DEMAND PLACEMENT", asset.Consumers, ConsumerRow);
        return rows;
    }

    private static void AddBlock(List<Row> rows, string group, IReadOnlyList<AssetEntry> entries,
                                 Func<AssetEntry, (string head, string sub)> fmt, int cap = BlockCap)
    {
        if (entries.Count == 0) return;
        bool first = true;
        foreach (AssetEntry e in entries.Take(cap))
        {
            (string head, string sub) = fmt(e);
            rows.Add(new Row(first ? group : "", head, sub));
            first = false;
        }
        if (entries.Count > cap)
            rows.Add(new Row("", $"...and {entries.Count - cap} more", ""));
    }

    /// <summary>A field's value by dataset column name (case-insensitive), or "" when the row lacks it.</summary>
    public static string Field(AssetEntry e, string key)
    {
        ArgumentNullException.ThrowIfNull(e);
        foreach (AssetField f in e.Fields)
            if (string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) return f.Value;
        return "";
    }

    private static string Mw(AssetEntry e, string key) { string v = Field(e, key); return v.Length > 0 ? $"{v} MW" : ""; }

    /// <summary>Generator envelope row: "BMU   Pmax N MW" / "MSG N MW (P% of Pmax)   *   psrType".</summary>
    public static (string Head, string Sub) EnvelopeRow(AssetEntry e)
    {
        string head = $"{Field(e, "bmUnit")}   Pmax {Mw(e, "p_max")}".Trim();
        string msg = Field(e, "p_min_stable_msg");
        string pct = Field(e, "msg_pct_of_pmax");
        string sub = $"MSG {(msg.Length > 0 ? msg + " MW" : "-")}{(pct.Length > 0 ? $" ({pct}% of Pmax)" : "")}   *   {Field(e, "psrType")}";
        return (head, sub);
    }

    /// <summary>Battery row: "unit   N MW" / "tech   *   N MWh   *   N h   *   source" (absent parts left out).</summary>
    public static (string Head, string Sub) BessRow(AssetEntry e)
    {
        string head = $"{Field(e, "unit_id")}   {Mw(e, "rated_power_mw")}".Trim();
        string en = Field(e, "rated_energy_mwh");
        string dur = Field(e, "duration_hours");
        var bits = new List<string> { Field(e, "tech") };
        if (en.Length > 0) bits.Add($"{en} MWh");
        if (dur.Length > 0) bits.Add($"{dur} h");
        bits.Add(Field(e, "source"));
        return (head, string.Join("   *   ", bits.Where(b => b.Length > 0)));
    }

    /// <summary>Tap-changer row: "GSP SGT   N kV" / "OLTC   *   step N%   *   a/b steps used   *   LTDS +/-N%".</summary>
    public static (string Head, string Sub) TapRow(AssetEntry e)
    {
        string head = $"{Field(e, "gsp")} {Field(e, "sgt")}".Trim();
        string kv = Field(e, "vnom_kv");
        if (kv.Length > 0) head += $"   {kv} kV";
        var bits = new List<string>();
        if (Field(e, "is_oltc") == "yes") bits.Add("OLTC");
        string step = Field(e, "step_pct");
        if (step.Length > 0) bits.Add($"step {step}%");
        string vis = Field(e, "n_visible_steps"), span = Field(e, "span_steps");
        if (vis.Length > 0 || span.Length > 0) bits.Add($"{vis}/{span} steps used");
        string ltds = Field(e, "ltds_tap_span_pct");
        if (ltds.Length > 0) bits.Add($"LTDS +/-{ltds}%");
        return (head, string.Join("   *   ", bits));
    }

    /// <summary>Consumer row: "name   (type)" / "import N MW   *   export N MW   *   N MWh storage   *   tech".</summary>
    public static (string Head, string Sub) ConsumerRow(AssetEntry e)
    {
        string head = $"{Field(e, "name")}   ({Field(e, "type")})".Trim();
        var bits = new List<string>();
        string imp = Field(e, "import_mw"), exp = Field(e, "export_mw"), st = Field(e, "storage_mwh");
        if (imp.Length > 0) bits.Add($"import {imp} MW");
        if (exp.Length > 0) bits.Add($"export {exp} MW");
        if (st.Length > 0) bits.Add($"{st} MWh storage");
        string tech = Field(e, "energy_tech");
        if (tech.Length > 0) bits.Add(tech);
        return (head, string.Join("   *   ", bits));
    }
}
