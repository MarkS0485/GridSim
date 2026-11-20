using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GridSim.Core.IO.Cim.Zip;

namespace GridSim.Core.IO.Cim.Profiles;

/// <summary>The outcome of one named check.</summary>
public enum CgmesCheckStatus { Pass, Fail, NotRun }

/// <summary>One named check, the specification it follows, its outcome and every finding.</summary>
public sealed record CgmesCheck(string Id, string Name, string Basis, CgmesCheckStatus Status, IReadOnlyList<string> Findings);

/// <summary>The result of <see cref="CgmesConformance.Run"/>: the edition, the check set (named, with its basis) and the verdict.</summary>
public sealed record CgmesConformanceResult(string Edition, IReadOnlyList<string> Documents, IReadOnlyList<CgmesCheck> Checks, IReadOnlyList<string> Notes)
{
    /// <summary>True when no check failed (NotRun checks are not passes: see <see cref="Checks"/>).</summary>
    public bool Passed => Checks.All(c => c.Status != CgmesCheckStatus.Fail);
    public int Failures => Checks.Count(c => c.Status == CgmesCheckStatus.Fail);
}

/// <summary>
/// RC4 R4-11 part B: <c>cgmes-conform</c>. Checks a CGMES bundle against GridSim's own, hand-coded subset of the published
/// profile rules and says so in the result: the check set, the specification each follows (CGMES 2.4.15 and CGMES 3.0, ENTSO-E,
/// as named in <see cref="CgmesVocabulary"/>) and what was NOT run. The free ENTSO-E SHACL constraint files (CGMES 3.0,
/// https://github.com/entsoe/application-profiles-library) and the RDFS schema are not applied here (no SHACL / RDFS engine is part
/// of GridSim), and the result lists them as NotRun: a pass here is NOT a conformity certificate, and vendor conformity is
/// COMPARATOR-UNVERIFIED.
/// </summary>
public static class CgmesConformance
{
    private static readonly Regex UuidForm = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private const string Cim16 = "http://iec.ch/TC57/2013/CIM-schema-cim16#";
    private const string Cim100 = "http://iec.ch/TC57/CIM100#";

    // Mandatory attributes / associations by class and profile (the profile rules the set checks; names as in the cited profiles).
    private static readonly (string Class, CgmesProfileKind Profile, string[] Required)[] Mandatory =
    {
        ("BaseVoltage", CgmesProfileKind.EQ, new[] { "BaseVoltage.nominalVoltage" }),
        ("Substation", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name", "Substation.Region" }),
        ("VoltageLevel", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name", "VoltageLevel.Substation", "VoltageLevel.BaseVoltage" }),
        ("Bay", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name", "Bay.VoltageLevel" }),
        ("ConnectivityNode", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name", "ConnectivityNode.ConnectivityNodeContainer" }),
        ("Terminal", CgmesProfileKind.EQ, new[] { "Terminal.ConductingEquipment", "ACDCTerminal.sequenceNumber" }),
        ("Breaker", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name", "Switch.normalOpen" }),
        ("Disconnector", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name", "Switch.normalOpen" }),
        ("GroundDisconnector", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name", "Switch.normalOpen" }),
        ("LoadBreakSwitch", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name", "Switch.normalOpen" }),
        ("ACLineSegment", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name", "ACLineSegment.r", "ACLineSegment.x", "ACLineSegment.bch" }),
        ("PowerTransformer", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name" }),
        ("PowerTransformerEnd", CgmesProfileKind.EQ, new[] { "PowerTransformerEnd.PowerTransformer", "TransformerEnd.endNumber", "PowerTransformerEnd.ratedU", "PowerTransformerEnd.r", "PowerTransformerEnd.x" }),
        ("EnergyConsumer", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name" }),
        ("SynchronousMachine", CgmesProfileKind.EQ, new[] { "IdentifiedObject.name", "RotatingMachine.GeneratingUnit" }),
        ("LinearShuntCompensator", CgmesProfileKind.EQ, new[] { "LinearShuntCompensator.bPerSection", "ShuntCompensator.maximumSections" }),
        ("TopologicalNode", CgmesProfileKind.TP, new[] { "IdentifiedObject.name", "TopologicalNode.BaseVoltage", "TopologicalNode.ConnectivityNodeContainer" }),
        ("Breaker", CgmesProfileKind.SSH, new[] { "Switch.open" }),
        ("Disconnector", CgmesProfileKind.SSH, new[] { "Switch.open" }),
        ("GroundDisconnector", CgmesProfileKind.SSH, new[] { "Switch.open" }),
        ("LoadBreakSwitch", CgmesProfileKind.SSH, new[] { "Switch.open" }),
        ("Terminal", CgmesProfileKind.SSH, new[] { "ACDCTerminal.connected" }),
        ("EnergyConsumer", CgmesProfileKind.SSH, new[] { "EnergyConsumer.p", "EnergyConsumer.q" }),
        ("SynchronousMachine", CgmesProfileKind.SSH, new[] { "RotatingMachine.p", "RotatingMachine.q" }),
        ("LinearShuntCompensator", CgmesProfileKind.SSH, new[] { "ShuntCompensator.sections" }),
        ("SvVoltage", CgmesProfileKind.SV, new[] { "SvVoltage.v", "SvVoltage.angle", "SvVoltage.TopologicalNode" }),
    };

    // An attribute belongs to one profile; seeing it in another is a placement error.
    private static readonly Dictionary<string, CgmesProfileKind> Placement = new(StringComparer.Ordinal)
    {
        ["Switch.normalOpen"] = CgmesProfileKind.EQ, ["Switch.open"] = CgmesProfileKind.SSH, ["ACDCTerminal.connected"] = CgmesProfileKind.SSH,
        ["Terminal.TopologicalNode"] = CgmesProfileKind.TP, ["ConnectivityNode.TopologicalNode"] = CgmesProfileKind.TP,
        ["EnergyConsumer.p"] = CgmesProfileKind.SSH, ["RotatingMachine.p"] = CgmesProfileKind.SSH, ["ShuntCompensator.sections"] = CgmesProfileKind.SSH,
        ["SvVoltage.v"] = CgmesProfileKind.SV, ["ACLineSegment.r"] = CgmesProfileKind.EQ, ["BaseVoltage.nominalVoltage"] = CgmesProfileKind.EQ,
        ["Terminal.ConnectivityNode"] = CgmesProfileKind.EQ, ["Terminal.ConductingEquipment"] = CgmesProfileKind.EQ,
    };

    private static readonly HashSet<string> OwnedOutsideEq = new(StringComparer.Ordinal)
        { "TopologicalNode", "SvVoltage", "SvPowerFlow", "SvStatus", "SvInjection", "SvTapStep", "SvShuntCompensatorSections" };

    private sealed record Item(string Class, string Mrid, bool Defines, CgmesProfileKind Doc, Dictionary<string, string> Props, List<string> Refs);

    public static CgmesConformanceResult Run(CgmesArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var checks = new List<CgmesCheck>();
        var notes = new List<string>();
        var docs = archive.Entries.Where(e => e.Kind != CgmesProfileKind.Unknown).ToList();
        var unknown = archive.Entries.Where(e => e.Kind == CgmesProfileKind.Unknown).ToList();

        // ---- parse every document ----
        var items = new List<Item>();
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var models = new Dictionary<string, CgmesProfileKind>(StringComparer.Ordinal);
        var dependsOn = new List<(CgmesProfileKind Doc, string Target)>();
        var idSpellings = new List<(string Spelling, CgmesProfileKind Doc, string Class)>();
        var parseFail = new List<string>();
        foreach (CgmesArchiveEntry d in docs)
        {
            XDocument x;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
                using var sr = new StringReader(d.Text);
                using var r = XmlReader.Create(sr, settings);
                x = XDocument.Load(r);
            }
            catch (XmlException ex) { parseFail.Add($"{d.Path}: {ex.Message}"); continue; }
            foreach (XElement e in x.Root!.Elements())
            {
                namespaces.Add(e.Name.NamespaceName);
                if (e.Name.LocalName == "FullModel")
                {
                    string? about = Attr(e, "about");
                    if (about is not null) models[Norm(about)] = d.Kind;
                    foreach (XElement c in e.Elements().Where(c => c.Name.LocalName == "Model.DependentOn"))
                        if (Attr(c, "resource") is string t) dependsOn.Add((d.Kind, Norm(t)));
                    continue;
                }
                string? idAttr = Attr(e, "ID"), aboutAttr = Attr(e, "about");
                string spelling = idAttr ?? aboutAttr ?? "";
                bool defines = idAttr is not null || (aboutAttr is not null && aboutAttr.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)
                    && (d.Kind == CgmesProfileKind.EQ || OwnedOutsideEq.Contains(e.Name.LocalName)));
                var it = new Item(e.Name.LocalName, Norm(spelling), defines, d.Kind, new(StringComparer.Ordinal), new());
                if (defines) idSpellings.Add((spelling, d.Kind, e.Name.LocalName));
                foreach (XElement p in e.Elements())
                {
                    string? res = Attr(p, "resource");
                    string key = p.Name.LocalName;
                    if (res is not null) { it.Props.TryAdd(key, res); if (!res.Contains('#') || res.StartsWith('#')) it.Refs.Add(Norm(res)); }
                    else it.Props.TryAdd(key, p.Value.Trim());
                }
                items.Add(it);
            }
        }

        // ---- C01 well-formed, no DTD ----
        checks.Add(Make("C01", "documents are well-formed XML without a DTD", "RDF/XML syntax (ENTSO-E CGMES RDF syntax user guide); DTD refused by GridSim policy", parseFail));
        // ---- C02 profile declared / edition ----
        var c2 = new List<string>();
        foreach (CgmesArchiveEntry d in docs) if (d.ProfileUris.Count == 0) c2.Add($"{d.Path}: no md:Model.profile declared (kind taken from the file name).");
        foreach (CgmesArchiveEntry d in unknown) c2.Add($"{d.Path}: no recognised profile (not read).");
        checks.Add(Make("C02", "every document declares a recognised md:Model.profile", "IEC 61970-552 FullModel header; CGMES 2.4.15 / 3.0 profile URIs", c2));
        string edition = namespaces.Contains(Cim100) && !namespaces.Contains(Cim16) ? "CGMES 3.0" : namespaces.Contains(Cim16) && !namespaces.Contains(Cim100) ? "CGMES 2.4.15" : "undetermined";
        var c3 = new List<string>();
        if (namespaces.Contains(Cim100) && namespaces.Contains(Cim16)) c3.Add("the documents mix the CIM16 and CIM100 namespaces.");
        if (!namespaces.Contains(Cim100) && !namespaces.Contains(Cim16)) c3.Add("no CIM16 or CIM100 class namespace found.");
        checks.Add(Make("C03", "one CIM namespace (edition) across the bundle", "CGMES 2.4.15 uses CIM16, CGMES 3.0 uses CIM100", c3));
        // ---- C04 profile set ----
        var c4 = new List<string>();
        if (!docs.Any(d => d.Kind == CgmesProfileKind.EQ)) c4.Add("no EQ document.");
        if (docs.Any(d => d.Kind == CgmesProfileKind.SV) && !docs.Any(d => d.Kind == CgmesProfileKind.TP)) c4.Add("an SV document without a TP document.");
        if (docs.Any(d => d.Kind == CgmesProfileKind.SSH) && !docs.Any(d => d.Kind == CgmesProfileKind.EQ)) c4.Add("an SSH document without an EQ document.");
        checks.Add(Make("C04", "profile set is coherent (EQ present; SV needs TP)", "CGMES profile dependencies", c4));
        // ---- C05 dependencies ----
        var c5 = new List<string>();
        foreach (var (doc, target) in dependsOn) if (!models.ContainsKey(target)) c5.Add($"{doc}: Model.DependentOn '{target}' is not a model in this bundle.");
        checks.Add(Make("C05", "md:Model.DependentOn resolves to a document of the bundle", "IEC 61970-552 model dependencies", c5));
        // ---- C06 identifiers ----
        var c6 = new List<string>();
        var defined = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (Item it in items.Where(i => i.Defines))
            if (!defined.TryAdd(it.Mrid, it)) c6.Add($"identifier '{it.Mrid}' is defined twice ({defined[it.Mrid].Class} in {defined[it.Mrid].Doc}, {it.Class} in {it.Doc}).");
        foreach (var (sp, doc, cls) in idSpellings)
        {
            if (edition == "CGMES 3.0" && !(sp.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase) && UuidForm.IsMatch(sp["urn:uuid:".Length..])))
                c6.Add($"{cls} identifier '{sp}' in {doc} is not a urn:uuid: UUID (CGMES 3.0).");
            if (edition == "CGMES 2.4.15" && !sp.StartsWith('_'))
                c6.Add($"{cls} rdf:ID '{sp}' in {doc} does not start with '_' (CGMES 2.4.15 XML ID).");
        }
        checks.Add(Make("C06", "identifiers unique and in the edition's form", "CGMES 2.4.15 rdf:ID '_' + mRID; CGMES 3.0 urn:uuid:", c6));
        // ---- C07 references ----
        var c7 = new List<string>();
        foreach (Item it in items)
        {
            if (!it.Defines && !defined.ContainsKey(it.Mrid)) c7.Add($"{it.Class} '{it.Mrid}' in {it.Doc} describes an object no document defines.");
            foreach (string r in it.Refs.Distinct(StringComparer.Ordinal))
                if (!defined.ContainsKey(r)) c7.Add($"{it.Class} '{it.Mrid}' ({it.Doc}) references '{r}', which no document defines.");
        }
        checks.Add(Make("C07", "every reference and every rdf:about resolves to a defined object", "RDF/XML resource references; CGMES referential integrity", c7.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList()));
        // ---- C08 mandatory ----
        var c8 = new List<string>();
        var merged = new Dictionary<(string Mrid, CgmesProfileKind Doc), Item>();
        foreach (Item it in items) merged.TryAdd((it.Mrid, it.Doc), it);
        foreach (var (cls, prof, required) in Mandatory)
            foreach (Item it in items.Where(i => i.Class == cls && i.Doc == prof))
                foreach (string req in required)
                    if (!it.Props.ContainsKey(req)) c8.Add($"{cls} '{it.Mrid}' in {prof} lacks {req}.");
        // Every EQ object with a Terminal expected to have SSH/TP stubs is checked in C09.
        checks.Add(Make("C08", "mandatory attributes and associations present, per class and profile", "CGMES 2.4.15 / 3.0 profile cardinalities (the subset GridSim writes and reads; the full RDFS is NOT applied)", c8));
        // ---- C09 placement ----
        var c9 = new List<string>();
        foreach (Item it in items)
            foreach (string k in it.Props.Keys)
                if (Placement.TryGetValue(k, out CgmesProfileKind home) && home != it.Doc)
                    c9.Add($"{k} on {it.Class} '{it.Mrid}' is in the {it.Doc} document but belongs to {home}.");
        checks.Add(Make("C09", "attributes sit in the profile that owns them", "CGMES profile boundaries (EQ / TP / SSH / SV)", c9.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList()));
        // ---- C10 value ranges ----
        var c10 = new List<string>();
        foreach (Item it in items)
            foreach (var kv in it.Props)
            {
                if (kv.Key is "BaseVoltage.nominalVoltage" or "PowerTransformerEnd.ratedU" or "ACDCTerminal.sequenceNumber" or "TransformerEnd.endNumber" or "ShuntCompensator.maximumSections")
                {
                    if (!double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !(v > 0)) c10.Add($"{kv.Key} on {it.Class} '{it.Mrid}' is '{kv.Value}', not a number above 0.");
                }
                else if (kv.Key is "Switch.normalOpen" or "Switch.open" or "ACDCTerminal.connected" or "Switch.retained")
                {
                    if (kv.Value is not ("true" or "false")) c10.Add($"{kv.Key} on {it.Class} '{it.Mrid}' is '{kv.Value}', not true or false.");
                }
                else if (kv.Key.StartsWith("ACLineSegment.", StringComparison.Ordinal) || kv.Key.StartsWith("PowerTransformerEnd.", StringComparison.Ordinal)
                    || kv.Key is "EnergyConsumer.p" or "EnergyConsumer.q" or "RotatingMachine.p" or "RotatingMachine.q" or "SvVoltage.v" or "SvVoltage.angle")
                {
                    if (kv.Value.Length > 0 && !kv.Value.StartsWith('#') && !kv.Value.StartsWith("urn:", StringComparison.Ordinal)
                        && !(double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)))
                        c10.Add($"{kv.Key} on {it.Class} '{it.Mrid}' is '{kv.Value}', not a finite number.");
                }
            }
        checks.Add(Make("C10", "numeric and boolean values well formed and in range", "CGMES datatypes (Float, Boolean, positive voltages)", c10.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList()));
        // ---- C11 terminals ----
        var c11 = new List<string>();
        var terminalsOf = items.Where(i => i.Class == "Terminal" && i.Doc == CgmesProfileKind.EQ).GroupBy(i => i.Props.GetValueOrDefault("Terminal.ConductingEquipment") is string s ? Norm(s) : "").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Breaker"] = 2, ["Disconnector"] = 2, ["LoadBreakSwitch"] = 2, ["GroundDisconnector"] = 1, ["ACLineSegment"] = 2, ["PowerTransformer"] = 2,
            ["EnergyConsumer"] = 1, ["SynchronousMachine"] = 1, ["LinearShuntCompensator"] = 1,
        };
        foreach (Item it in items.Where(i => i.Doc == CgmesProfileKind.EQ && expected.ContainsKey(i.Class) && i.Defines))
        {
            int have = terminalsOf.GetValueOrDefault(it.Mrid);
            if (have != expected[it.Class]) c11.Add($"{it.Class} '{it.Mrid}' has {have} terminal(s); {expected[it.Class]} expected (two-winding transformers assumed).");
        }
        checks.Add(Make("C11", "conducting equipment has the number of terminals its class needs", "CGMES / IEC 61970-301 Terminal multiplicity", c11));
        // ---- not run ----
        checks.Add(new CgmesCheck("S01", "ENTSO-E SHACL constraint files (CGMES 3.0)", "https://github.com/entsoe/application-profiles-library (Apache-2.0)", CgmesCheckStatus.NotRun,
            new[] { "NotRun: GridSim has no SHACL engine and does not bundle the constraint files; run them with an external SHACL validator for a full check." }));
        checks.Add(new CgmesCheck("S02", "RDFS schema validation (CGMES 2.4.15 / 3.0 profile RDFS) and OCL constraints (2.4.15)", "ENTSO-E CGMES Library, https://www.entsoe.eu/data/cim/cim-for-grid-models-exchange/", CgmesCheckStatus.NotRun,
            new[] { "NotRun: the full RDFS/OCL sets are not applied; C08 covers only the classes GridSim writes and reads." }));
        notes.Add("Edition detected: " + edition + ". A pass is not a conformity certificate; vendor conformity is COMPARATOR-UNVERIFIED.");
        if (archive.Skipped.Count > 0) notes.Add("Entries skipped (not XML): " + string.Join(", ", archive.Skipped));
        return new CgmesConformanceResult(edition, archive.Entries.Select(e => $"{e.Path} [{e.Kind}]").ToList(), checks, notes);
    }

    private static CgmesCheck Make(string id, string name, string basis, IReadOnlyList<string> findings) =>
        new(id, name, basis, findings.Count == 0 ? CgmesCheckStatus.Pass : CgmesCheckStatus.Fail, findings);

    private static string? Attr(XElement e, string local) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == local && a.Name.Namespace.NamespaceName.StartsWith("http://www.w3.org/1999/02/22-rdf", StringComparison.Ordinal))?.Value;

    private static string Norm(string s)
    {
        string t = s.Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) return t["urn:uuid:".Length..].ToLowerInvariant();
        if (t.StartsWith('_')) t = t[1..];
        return t;
    }
}
