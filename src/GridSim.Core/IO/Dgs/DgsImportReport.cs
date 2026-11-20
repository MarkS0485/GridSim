using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Model.Substations;

namespace GridSim.Core.IO.Dgs;

/// <summary>
/// RC4 R4-11 part C: the import report (<c>gridsim-dgs-import/1</c>), a deterministic JSON text. It names every object
/// and what became of it, every unsupported class, every column that was read and not used, every UNCONFIRMED mapping that
/// was used, the General entries, the provenance tag, and the bus map of the projection. Nothing is left out silently.
/// </summary>
public static class DgsImportReport
{
    public const string Format = "gridsim-dgs-import/1";

    public static string ToJson(DgsImportResult r, SubstationProjectionResult? projection)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteString("provenance", r.Provenance.ToString());
            w.WriteString("provenanceNote", "Read with the DGS ASCII dialect in IO/Dgs/DgsTableReader.cs, built from public descriptions only; no real PowerFactory export has been tested (AWAITING FILES). Not a claim about PowerFactory behaviour.");
            w.WriteString("dgsVersion", r.DgsVersion);
            w.WriteBoolean("acceptedUnconfirmedMappings", r.AcceptedUnconfirmed);
            w.WriteBoolean("complete", r.Complete);
            w.WriteNumber("baseMva", r.Model.BaseMva);

            w.WriteStartArray("unconfirmedMappingsUsed");
            foreach (string u in r.UnconfirmedUsed) w.WriteStringValue(u);
            w.WriteEndArray();

            w.WriteStartArray("unsupportedClasses");
            foreach (DgsUnsupportedClass u in r.Unsupported)
            {
                w.WriteStartObject();
                w.WriteString("class", u.Class); w.WriteNumber("rows", u.Rows); w.WriteNumber("firstLine", u.FirstLine);
                w.WriteString("status", u.KnownAndRefused ? "known class, not implemented: refused" : "not interpreted by this importer");
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("unreadColumns");
            foreach (var kv in r.UnreadColumns)
            {
                w.WriteStartArray(kv.Key);
                foreach (string c in kv.Value) w.WriteStringValue(c);
                w.WriteEndArray();
            }
            w.WriteEndObject();

            w.WriteStartArray("general");
            foreach (var g in r.General)
            {
                w.WriteStartObject(); w.WriteString("descr", g.Descr); w.WriteString("val", g.Val); w.WriteNumber("line", g.Line); w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("counts");
            foreach (var grp in r.Objects.GroupBy(o => o.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(grp.Key);
                foreach (DgsObjectOutcome o in Enum.GetValues<DgsObjectOutcome>())
                    w.WriteNumber(o.ToString(), grp.Count(x => x.Outcome == o));
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WriteStartArray("objects");
            foreach (DgsObjectRecord o in r.Objects)
            {
                w.WriteStartObject();
                w.WriteString("class", o.Class); w.WriteString("id", o.Id);
                if (o.LocName is not null) w.WriteString("name", o.LocName);
                w.WriteNumber("line", o.Line); w.WriteString("outcome", o.Outcome.ToString());
                w.WriteStartArray("gridsimIds"); foreach (string id in o.GridSimIds) w.WriteStringValue(id); w.WriteEndArray();
                w.WriteString("note", o.Note);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("warnings");
            foreach (string x in r.Warnings) w.WriteStringValue(x);
            w.WriteEndArray();

            if (projection is not null)
            {
                w.WriteStartObject("projection");
                w.WriteString("grouping", projection.GroupingText());
                w.WriteNumber("buses", projection.Buses.Count);
                w.WriteNumber("demandMwTotal", projection.DemandMwTotal);
                w.WriteNumber("demandMwServed", projection.DemandMwServed);
                w.WriteStartArray("busMap");
                foreach (ProjectedBus b in projection.Buses)
                {
                    w.WriteStartObject();
                    w.WriteNumber("bus", b.BusId); w.WriteString("substation", b.Substation); w.WriteNumber("nominalKv", b.NominalKv);
                    w.WriteStartArray("dgsTerminals");
                    foreach (string n in b.Nodes) if (r.Identity.TryGetValue(n, out DgsObjectRecord? rec) && rec.Class == "ElmTerm") w.WriteStringValue(rec.Id);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("equipment");
                foreach (EquipmentOutcome e in projection.Equipment)
                {
                    w.WriteStartObject();
                    w.WriteString("id", e.EquipmentId); w.WriteString("disposition", e.Disposition.ToString()); w.WriteString("reason", e.Reason);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }
}
