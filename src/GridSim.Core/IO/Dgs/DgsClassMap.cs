namespace GridSim.Core.IO.Dgs;

// =====================================================================================================================
// RC4 R4-11 part C: the declarative class map. One entry per DGS element class the importer knows, one row per
// attribute, each with what becomes of it (mapped, approximated, carried, refused) and how well the NAME and MEANING of
// the attribute are documented in a public source (Confirmed) or only stated by this importer (Unconfirmed).
//
// Sources for the Confirmed rows (all public, second-hand for PowerFactory; no PowerFactory behaviour is claimed):
//   [P] PowSyBl PowerFactory import documentation, https://powsybl.readthedocs.io/projects/powsybl-core/en/latest/
//       grid_exchange_formats/powerfactory/import.html : ElmCoup on_off (initial state) and aUsage (cbk breaker, dct
//       disconnector); ElmSym ip_ctrl (reference machine); ElmTerm, ElmLne, TypLne need only default attributes.
//   [O] OPAL-RT ePHASORSIM documentation, https://opal-rt.atlassian.net/wiki/spaces/PEUD/pages/637862257/
//       General+Elm+Components : ElmTerm uknom (kV, line to line); ElmLne dline (km); TypLne uline, sline, rline
//       (ohm/km), xline (ohm/km), bline (uS/km); ElmLod plini (MW), qlini (MVAr), scale0; outserv; ID unique key.
// Every other attribute name below is remembered from general PowerFactory usage and has no public source cited here:
// UNCONFIRMED. An UNCONFIRMED attribute that is present with a value makes its object REFUSED unless the importer is
// told to accept unconfirmed mappings, and every use of one is then listed in the import report.
// =====================================================================================================================

/// <summary>How well an attribute's name and meaning are documented in a public source.</summary>
public enum DgsConfidence { Confirmed, Unconfirmed }

/// <summary>What becomes of an attribute or a class.</summary>
public enum DgsDisposition
{
    /// <summary>Read and used exactly (up to a stated unit conversion).</summary>
    Mapped,
    /// <summary>Read and used with a known simplification, stated in the note.</summary>
    Approximated,
    /// <summary>Read and recorded in the import report but without electrical effect.</summary>
    Carried,
    /// <summary>The class (or this use of the attribute) is not supported: the object is refused by name.</summary>
    Refused,
}

/// <param name="Name">The DGS column name (case sensitive).</param>
/// <param name="Unit">Unit in the DGS file as far as it is known ("" when none or unknown).</param>
/// <param name="Target">The GridSim field it feeds.</param>
public sealed record DgsAttributeMap(string Name, DgsDisposition Disposition, DgsConfidence Confidence, string Unit, string Target, string Note);

/// <param name="GridSimKind">The record kind it becomes ("" for classes that do not become equipment).</param>
public sealed record DgsClassMapEntry(string Class, DgsDisposition Disposition, string GridSimKind, string Note, IReadOnlyList<DgsAttributeMap> Attributes)
{
    public DgsAttributeMap? Attribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);
}

/// <summary>The class map. Read-only data; the importer and the report both read it, so the two cannot disagree.</summary>
public static class DgsClassMap
{
    private static DgsAttributeMap A(string n, DgsDisposition d, DgsConfidence c, string unit, string target, string note = "") => new(n, d, c, unit, target, note);
    private const DgsDisposition M = DgsDisposition.Mapped, X = DgsDisposition.Approximated, C = DgsDisposition.Carried, R = DgsDisposition.Refused;
    private const DgsConfidence Ok = DgsConfidence.Confirmed, Un = DgsConfidence.Unconfirmed;

    public static readonly IReadOnlyList<DgsClassMapEntry> Entries = new DgsClassMapEntry[]
    {
        new("ElmTerm", M, "connectivity node", "A terminal is one connectivity node; its class and ID are kept as the node id.", new[]
        {
            A("loc_name", C, Ok, "", "identity", "name kept in the identity map"),
            A("uknom", M, Ok, "kV line to line", "voltage level nominal kV"),
            A("outserv", R, Ok, "", "", "an out-of-service terminal is refused by name (no guess at what it isolates)"),
            A("fold_id", M, Un, "", "substation", "folder pointer: an ElmSubstat row makes the terminal part of that substation; other values are not read"),
            A("iUsage", C, Un, "", "identity", "busbar / junction / internal usage, carried"),
        }),
        new("ElmSubstat", M, "substation", "A substation groups terminals (fold_id). Class name and the folder scheme are UNCONFIRMED.", new[]
        {
            A("loc_name", C, Un, "", "identity", "substation name"),
        }),
        new("StaCubic", M, "terminal attachment", "A cubicle attaches one end of an element to a terminal and may hold switches (connection scheme UNCONFIRMED).", new[]
        {
            A("fold_id", M, Un, "", "terminal id", "pointer to the terminal the cubicle belongs to"),
            A("obj_id", M, Un, "", "element id", "pointer to the element it connects"),
            A("obj_bus", M, Un, "", "element end", "0 for the first end (line from, transformer high voltage side), 1 for the second; default 0"),
        }),
        new("StaSwitch", M, "switch", "A switch inside a cubicle, between the terminal and the element end.", new[]
        {
            A("fold_id", M, Un, "", "cubicle id", "pointer to the cubicle"),
            A("on_off", M, Ok, "", "normal state", "1 closed, 0 open; absent is refused (no inferred closed switch)"),
            A("aUsage", M, Ok, "", "switch kind", "cbk breaker, dct disconnector; empty is a breaker (approximated); others refused"),
            A("loc_name", C, Un, "", "identity", ""),
        }),
        new("ElmCoup", M, "switch", "A coupler joins the terminals at its two cubicles (obj_bus 0 and 1) and must lie inside one substation.", new[]
        {
            A("on_off", M, Ok, "", "normal state", "1 closed, 0 open; absent is refused"),
            A("aUsage", M, Ok, "", "switch kind", "cbk breaker, dct disconnector; empty is a breaker (approximated); others refused"),
            A("outserv", R, Ok, "", "", "an out-of-service coupler is refused by name"),
            A("loc_name", C, Un, "", "identity", ""),
        }),
        new("ElmLne", M, "line", "A line between two cubicles. Per unit on the model base from the terminal kV.", new[]
        {
            A("loc_name", C, Un, "", "identity", ""),
            A("dline", M, Ok, "km", "length"),
            A("typ_id", M, Un, "", "line type", "pointer to a TypLne row (name of the pointer for lines UNCONFIRMED)"),
            A("outserv", M, Ok, "", "omitted", "1: the line is left out of the model and listed as out of service"),
            A("nlnum", R, Un, "", "", "parallel-line count other than 1 is refused"),
        }),
        new("TypLne", M, "line type", "Series impedance and charging per km; rated current gives the rating.", new[]
        {
            A("rline", M, Ok, "ohm/km", "R"),
            A("xline", M, Ok, "ohm/km", "X"),
            A("bline", M, Ok, "uS/km", "B", "total line charging, half at each end as in the bus-branch model"),
            A("sline", X, Ok, "kA", "rating", "rating = sqrt(3) x terminal kV x sline; the PowerFactory meaning of the rated current is not claimed"),
            A("uline", C, Ok, "kV", "check", "a difference from the terminal kV is reported, the terminal kV is used"),
        }),
        new("ElmTr2", M, "two-winding transformer", "Between two cubicles, obj_bus 0 high voltage and 1 low voltage (UNCONFIRMED).", new[]
        {
            A("typ_id", M, Un, "", "transformer type", "pointer to a TypTr2 row"),
            A("nntap", R, Un, "", "", "a tap position other than 0 is refused (tap table not read)"),
            A("outserv", M, Ok, "", "omitted", "1: left out and listed as out of service"),
            A("loc_name", C, Un, "", "identity", ""),
        }),
        new("TypTr2", M, "transformer type", "Short-circuit voltage and copper losses on the rating.", new[]
        {
            A("strn", M, Un, "MVA", "rating"),
            A("utrn_h", M, Un, "kV", "ratio"),
            A("utrn_l", M, Un, "kV", "ratio"),
            A("uktr", M, Un, "%", "Z"),
            A("pcutr", M, Un, "kW", "R"),
            A("i0tr", X, Un, "%", "none", "magnetising current is not modelled (B = 0); listed"),
        }),
        new("ElmLod", M, "load", "Constant-power demand (plini x scale0, qlini x scale0).", new[]
        {
            A("plini", M, Ok, "MW", "Pd"),
            A("qlini", M, Ok, "MVAr", "Qd"),
            A("scale0", M, Ok, "1", "scale", "multiplies plini and qlini; absent is 1"),
            A("outserv", M, Ok, "", "omitted", "1: left out and listed as out of service"),
            A("typ_id", X, Un, "", "none", "the load type (voltage dependence) is NOT applied: the load is constant power at every voltage"),
            A("u0", C, Ok, "pu", "none", "operating-point voltage, carried"),
            A("loc_name", C, Un, "", "identity", ""),
        }),
        new("ElmSym", M, "generator", "Active power and voltage set point; reactive limits are not read.", new[]
        {
            A("pgini", M, Un, "MW", "Pg"),
            A("qgini", C, Un, "MVAr", "none", "initial reactive output, carried"),
            A("usetp", M, Un, "pu", "Vg"),
            A("ip_ctrl", M, Ok, "", "slack", "1: the reference machine of its bus (slack)"),
            A("outserv", M, Ok, "", "omitted", "1: left out and listed as out of service"),
            A("loc_name", C, Un, "", "identity", ""),
        }),
        new("ElmXnet", M, "generator", "External grid as the slack: bustp SL gives a slack generator at usetp. Short-circuit data is not used.", new[]
        {
            A("bustp", M, Un, "", "bus type", "SL slack; PV generator at pgini; PQ is refused"),
            A("usetp", M, Un, "pu", "Vg"),
            A("pgini", M, Un, "MW", "Pg", "used for bustp PV"),
            A("ip_ctrl", M, Ok, "", "slack", "1 also marks the slack"),
            A("outserv", M, Ok, "", "omitted", "1: left out and listed as out of service"),
            A("snss", X, Un, "MVA", "none", "short-circuit power: not used"),
            A("loc_name", C, Un, "", "identity", ""),
        }),
        new("ElmShnt", M, "shunt", "A capacitor of qcapn MVAr (shtype 2 only).", new[]
        {
            A("qcapn", M, Un, "MVAr", "Bs", "Bs = +qcapn (a capacitor injects qcapn MVAr at 1 pu)"),
            A("shtype", M, Un, "", "kind", "present and not 2: refused"),
            A("outserv", M, Ok, "", "omitted", "1: left out and listed as out of service"),
            A("loc_name", C, Un, "", "identity", ""),
        }),
    };

    /// <summary>Element classes the importer recognises by name and REFUSES (not implemented), so the report can say
    /// "refused" rather than "unknown" for them. The list is not a claim about their DGS layout.</summary>
    public static readonly IReadOnlyList<string> RefusedClasses = new[]
    {
        "ElmTr3", "ElmTr4", "ElmVsc", "ElmSvs", "ElmGenstat", "ElmPvsys", "ElmZpu", "ElmSind", "ElmScap", "ElmRelay", "ElmDsl", "ElmComp", "ElmAsm",
    };

    public static DgsClassMapEntry? Find(string cls) => Entries.FirstOrDefault(e => e.Class == cls);

    /// <summary>The attribute names that sit under an UNCONFIRMED name or meaning, as "Class.attribute".</summary>
    public static IEnumerable<string> UnconfirmedAttributes() =>
        Entries.SelectMany(e => e.Attributes.Where(a => a.Confidence == DgsConfidence.Unconfirmed).Select(a => e.Class + "." + a.Name));
}
