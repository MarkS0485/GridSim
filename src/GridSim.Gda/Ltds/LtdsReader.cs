using GridSim.Gda.Csv;

namespace GridSim.Gda.Ltds;

/// <summary>
/// Schema-faithful readers for the NGED LTDS Tabular Model CSVs. Each method
/// streams one table into typed records. Column access is by name (tolerant of the
/// LTDS quirks  -  BOMs, tab-padded headers), so a small upstream change in column
/// order won't break ingestion. No values are cached or embedded; the file is read
/// from the authorised <c>/gda</c> path each call.
/// </summary>
public static class LtdsReader
{
    public static IReadOnlyList<LtdsNode> Nodes(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        // GR-289 (November 2026): a missing or renamed column is refused here, before the rows, not read as blank or 0.
        t.RequireColumns("LTDS nodes table", "Licence", "Code", "Name", "BSP Group", "GSP Group", "Status");
        var list = new List<LtdsNode>(23000);
        foreach (CsvRow r in t.Rows())
        {
            string code = r["Code"];
            if (code.Length == 0) continue;
            list.Add(new LtdsNode(r["Licence"], code, r["Name"], r["BSP Group"], r["GSP Group"], r["Status"]));
        }
        return list;
    }

    public static IReadOnlyList<LtdsCircuit> Circuits(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        // GR-289 (November 2026): a missing or renamed column is refused here, before the rows, not read as blank or 0.
        t.RequireColumns("LTDS circuits table", "Licence", "GSP Group", "BSP Group", "Node 1", "Node 2", "Circuit Status", "Operating Voltage (kV)", "Circuit Code", "R1", "X1", "B1", "R0", "X0", "Winter", "Summer", "Circuit Length (km)", "Equipment");
        var list = new List<LtdsCircuit>(23000);
        foreach (CsvRow r in t.Rows())
        {
            string n1 = r["Node 1"], n2 = r["Node 2"];
            if (n1.Length == 0 || n2.Length == 0) continue;
            list.Add(new LtdsCircuit(
                r["Licence"], r["GSP Group"], r["BSP Group"], n1, n2, r["Circuit Status"],
                r.Double("Operating Voltage (kV)"), r["Circuit Code"],
                r.Double("R1"), r.Double("X1"), r.Double("B1"), r.Double("R0"), r.Double("X0"),
                r.Double("Winter"), r.Double("Summer"), r.Double("Circuit Length (km)"), r["Equipment"]));
        }
        return list;
    }

    public static IReadOnlyList<LtdsTransformer2> TwoWindingTransformers(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        // GR-289 (November 2026): a missing or renamed column is refused here, before the rows, not read as blank or 0.
        t.RequireColumns("LTDS two-winding transformers table", "Licence", "GSP Group", "Node 1", "Operating Voltage 1", "Node 2", "Operating Voltage 2", "Status", "Vector Group", "R1", "X1", "X0", "Min Tap", "Max Tap", "Nominal Rating", "Emergency Rating");
        var list = new List<LtdsTransformer2>(3000);
        foreach (CsvRow r in t.Rows())
        {
            string n1 = r["Node 1"], n2 = r["Node 2"];
            if (n1.Length == 0 || n2.Length == 0) continue;
            list.Add(new LtdsTransformer2(
                r["Licence"], r["GSP Group"], n1, r.Double("Operating Voltage 1"),
                n2, r.Double("Operating Voltage 2"), r["Status"], r["Vector Group"],
                r.Double("R1"), r.Double("X1"), r.Double("X0"),
                r.Double("Min Tap"), r.Double("Max Tap"),
                r.Double("Nominal Rating"), r.Double("Emergency Rating")));
        }
        return list;
    }

    public static IReadOnlyList<LtdsTransformer3> ThreeWindingTransformers(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        // GR-289 (November 2026): a missing or renamed column is refused here, before the rows, not read as blank or 0.
        t.RequireColumns("LTDS three-winding transformers table", "Licence", "GSP Group", "Node 1", "Operating Voltage 1", "Node 2", "Operating Voltage 2", "Node 3", "Operating Voltage 3", "Status", "Vector Group", "R1", "X1 HV-LV", "X1 LV-Tertiary", "X1 HV-Tertiary", "Min Tap", "Max Tap", "Nominal Rating", "Emergency Rating");
        var list = new List<LtdsTransformer3>(100);
        foreach (CsvRow r in t.Rows())
        {
            string n1 = r["Node 1"], n2 = r["Node 2"], n3 = r["Node 3"];
            if (n1.Length == 0 || n2.Length == 0) continue;
            list.Add(new LtdsTransformer3(
                r["Licence"], r["GSP Group"],
                n1, r.Double("Operating Voltage 1"), n2, r.Double("Operating Voltage 2"),
                n3, r.Double("Operating Voltage 3"), r["Status"], r["Vector Group"],
                r.Double("R1"), r.Double("X1 HV-LV"), r.Double("X1 LV-Tertiary"), r.Double("X1 HV-Tertiary"),
                r.Double("Min Tap"), r.Double("Max Tap"),
                r.Double("Nominal Rating"), r.Double("Emergency Rating")));
        }
        return list;
    }

    public static IReadOnlyList<LtdsDemand> Demand(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        // GR-289 (November 2026): a missing or renamed column is refused here, before the rows, not read as blank or 0.
        t.RequireColumns("LTDS demand table", "GSP Group", "BSP Group", "Primary Substation", "Node", "Operating Voltage", "Current Year Max Demand", "Firm Capacity of Substation", "Status");
        var list = new List<LtdsDemand>(2400);
        foreach (CsvRow r in t.Rows())
        {
            string node = r["Node"];
            if (node.Length == 0 && r["Primary Substation"].Length == 0) continue;
            list.Add(new LtdsDemand(
                r["GSP Group"], r["BSP Group"], r["Primary Substation"], node,
                r.Double("Operating Voltage"), r.Double("Current Year Max Demand"),
                r.Double("Current Year Power Factor", 1.0), r.Double("Firm Capacity of Substation"),
                r["Status"]));
        }
        return list;
    }

    public static IReadOnlyList<LtdsGeneration> Generation(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        // GR-289 (November 2026): a missing or renamed column is refused here, before the rows, not read as blank or 0.
        t.RequireColumns("LTDS generation table", "GSP Group", "Bulk Supply Point", "Primary Substation", "Connection Voltage", "Agreed Export Capacity", "Fuel Type", "Connection Status");
        var list = new List<LtdsGeneration>(1000);
        foreach (CsvRow r in t.Rows())
        {
            if (r["Primary Substation"].Length == 0 && r["Bulk Supply Point"].Length == 0) continue;
            list.Add(new LtdsGeneration(
                r["GSP Group"], r["Bulk Supply Point"], r["Primary Substation"],
                r.Double("Connection Voltage"), r.Double("Agreed Export Capacity"),
                r["Fuel Type"], r["Connection Status"]));
        }
        return list;
    }
}
