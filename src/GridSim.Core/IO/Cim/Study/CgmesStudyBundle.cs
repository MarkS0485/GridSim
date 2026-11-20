using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace GridSim.Core.IO.Cim.Study;

/// <summary>
/// RC3 P3: one profile document of a CGMES study bundle, as the manifest declares it and with the exact bytes the study
/// consumes. The caller reads each file once; every later stage works on these bytes, so the bundle identity binds what
/// was actually imported.
/// </summary>
/// <param name="Profile">The declared profile type: EQ, TP, SSH, SV, EQ_BD, TP_BD, DY, DL or GL.</param>
/// <param name="DeclaredVersion">The declared profile version (free text such as "2.4.15" or "3.0"); recorded, and
/// compared with nothing external (no conformity claim is made).</param>
/// <param name="Path">The manifest path label (as written in the study, forward slashes).</param>
/// <param name="Sha256">The declared SHA-256 of <paramref name="Bytes"/> (lowercase or uppercase hex).</param>
/// <param name="Bytes">The document bytes (UTF-8 RDF/XML).</param>
public sealed record CgmesProfileInput(string Profile, string DeclaredVersion, string Path, string Sha256, byte[] Bytes);

/// <summary>RC3 P3: what the bundle inspection recorded for one profile document.</summary>
/// <param name="Profile">The declared profile type.</param>
/// <param name="DeclaredVersion">The declared profile version.</param>
/// <param name="Path">The manifest path label.</param>
/// <param name="Sha256">The verified SHA-256 of the consumed bytes (lowercase hex).</param>
/// <param name="HeaderModelId">The md:FullModel identifier, or null when the document has no header.</param>
/// <param name="HeaderProfiles">The md:Model.profile URIs of the header, in document order.</param>
/// <param name="DependentOn">The md:Model.DependentOn references of the header, in document order.</param>
/// <param name="ScenarioTime">The md:Model.scenarioTime text, or null.</param>
/// <param name="ObjectCount">Described objects (elements with rdf:ID or rdf:about), excluding the header.</param>
/// <param name="Consumed">True when the document is passed to the CIM importer.</param>
/// <param name="Preservation">What of this document the study preserves for exchange.</param>
/// <param name="SimulationCoverage">What of this document the solved study actually uses.</param>
public sealed record CgmesProfileRecord(
    string Profile, string DeclaredVersion, string Path, string Sha256, string? HeaderModelId,
    IReadOnlyList<string> HeaderProfiles, IReadOnlyList<string> DependentOn, string? ScenarioTime, int ObjectCount,
    bool Consumed, string Preservation, string SimulationCoverage);

/// <summary>RC3 P3: one automatic control the bundle carries that the study does not represent.</summary>
/// <param name="Mrid">The device mRID.</param>
/// <param name="Class">Its CIM class.</param>
/// <param name="Treatment">What the study does with it instead, in plain words.</param>
public sealed record CgmesUnrepresentedControl(string Mrid, string Class, string Treatment);

/// <summary>RC3 P3: the inspected bundle.</summary>
/// <param name="BundleSha256">The bundle identity: SHA-256 of the UTF-8 manifest lines
/// <c>profile TAB declaredVersion TAB path TAB sha256 LF</c> in manifest order.</param>
/// <param name="Profiles">One record per manifest entry, in manifest order.</param>
/// <param name="OmittedProfiles">Optional profile types the bundle does not supply, each with what that means.</param>
/// <param name="Diagnostics">Harmless observations (no header, a header without a profile URI, ...).</param>
/// <param name="UnrepresentedControls">Automatic controls present in the bundle that the study does not apply.</param>
public sealed record CgmesBundleReport(
    string BundleSha256, IReadOnlyList<CgmesProfileRecord> Profiles, IReadOnlyList<string> OmittedProfiles,
    IReadOnlyList<string> Diagnostics, IReadOnlyList<CgmesUnrepresentedControl> UnrepresentedControls);

/// <summary>RC3 P3: a bundle the study refuses before any import, naming the profile and the record.</summary>
public sealed class CgmesStudyInputException : Exception
{
    /// <summary>Create the refusal.</summary>
    public CgmesStudyInputException(string profile, string? record, string message)
        : base($"CGMES bundle, profile {profile}{(record is null ? "" : $", record {record}")}: {message}")
    {
        Profile = profile;
        Record = record;
    }

    /// <summary>The profile (type and path) the refusal is about.</summary>
    public string Profile { get; }

    /// <summary>The record (object mRID and property), or null when the refusal concerns the whole profile.</summary>
    public string? Record { get; }
}

/// <summary>
/// RC3 P3: the input stage of a CGMES study. It verifies each declared digest, binds the profiles through their
/// md:FullModel identity and DependentOn references, and refuses a bundle with a missing required profile, a duplicated
/// profile type, a header that contradicts the declared type, an unresolved dependency, a reference to an object no
/// profile defines, or two profiles giving one property of one object different values. It reads the XML with DTD
/// processing prohibited and no resolver (as the importer does). Zipped bundles are not supported: the caller supplies
/// each document's bytes.
/// <para>No conformity to an external CGMES profile specification is checked or claimed: the profile URI check only
/// compares a header with the type the manifest declares.</para>
/// </summary>
public static class CgmesBundleInspector
{
    /// <summary>The profile types a v1 manifest may declare.</summary>
    public static readonly IReadOnlyList<string> ProfileTypes = ["EQ", "TP", "SSH", "SV", "EQ_BD", "TP_BD", "DY", "DL", "GL"];

    /// <summary>The types passed to the CIM importer. DL and GL carry no conducting equipment and are only digested.</summary>
    public static readonly IReadOnlySet<string> ImportedTypes = new HashSet<string>(StringComparer.Ordinal)
        { "EQ", "TP", "SSH", "SV", "EQ_BD", "TP_BD", "DY" };

    // The CIM switch classes (Switch and its subclasses), as CimImporter reads them. Kept here rather than exposing the
    // importer's private set; the switch-action tests pin that the two agree on the classes used.
    internal static readonly IReadOnlySet<string> SwitchClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Switch", "Breaker", "Disconnector", "LoadBreakSwitch", "Fuse", "Jumper", "GroundDisconnector",
        "ProtectedSwitch", "Recloser", "Sectionaliser", "DisconnectingCircuitBreaker",
    };

    private const string RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    /// <summary>The bundle identity of a manifest (see <see cref="CgmesBundleReport.BundleSha256"/>).</summary>
    public static string BundleIdentity(IReadOnlyList<CgmesProfileInput> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var sb = new StringBuilder();
        foreach (CgmesProfileInput p in profiles)
            sb.Append(p.Profile).Append('\t').Append(p.DeclaredVersion).Append('\t').Append(p.Path).Append('\t')
              .Append(Hash(p.Bytes)).Append('\n');
        return Hash(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>Lowercase hex SHA-256.</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>One parsed document of the bundle, kept for the later stages.</summary>
    internal sealed record Parsed(CgmesProfileInput Input, string Text, XDocument Xml);

    /// <summary>Inspect the bundle. Throws <see cref="CgmesStudyInputException"/> on the first refusal.</summary>
    /// <param name="profiles">The manifest entries in manifest order.</param>
    /// <param name="topologyFromSwitches">True when the study builds buses from the switch states (TP then optional).</param>
    /// <param name="needsStateVariables">True when an option reads the SV profile (starting voltages, SV tap steps or
    /// SV shunt sections); SV is then required.</param>
    public static CgmesBundleReport Inspect(
        IReadOnlyList<CgmesProfileInput> profiles, bool topologyFromSwitches, bool needsStateVariables) =>
        InspectParsed(profiles, topologyFromSwitches, needsStateVariables, out _);

    internal static CgmesBundleReport InspectParsed(
        IReadOnlyList<CgmesProfileInput> profiles, bool topologyFromSwitches, bool needsStateVariables,
        out IReadOnlyList<Parsed> parsed)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0) throw new CgmesStudyInputException("(none)", null, "the manifest lists no profile.");
        var diagnostics = new List<string>();
        var docs = new List<Parsed>(profiles.Count);
        var byType = new Dictionary<string, CgmesProfileInput>(StringComparer.Ordinal);

        // ---- 1. declared identity: type, version, digest, one document per type ------------------------------------
        foreach (CgmesProfileInput p in profiles)
        {
            if (p is null) throw new CgmesStudyInputException("(null)", null, "a manifest entry is null.");
            string who = Who(p);
            if (!ProfileTypes.Contains(p.Profile))
                throw new CgmesStudyInputException(who, null,
                    $"'{p.Profile}' is not a profile type this study reads ({string.Join(", ", ProfileTypes)}).");
            if (string.IsNullOrWhiteSpace(p.DeclaredVersion))
                throw new CgmesStudyInputException(who, null, "the declared profile version is empty.");
            if (string.IsNullOrWhiteSpace(p.Path))
                throw new CgmesStudyInputException(who, null, "the path is empty.");
            if (p.Bytes is null) throw new CgmesStudyInputException(who, null, "no bytes were read.");
            string actual = Hash(p.Bytes);
            if (!string.Equals(actual, p.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CgmesStudyInputException(who, null,
                    $"SHA-256 mismatch: declared {p.Sha256}, the file's bytes give {actual}.");
            if (byType.TryGetValue(p.Profile, out CgmesProfileInput? first))
                throw new CgmesStudyInputException(who, null,
                    $"duplicated profile: {Who(first)} already supplies {p.Profile}. A v1 bundle holds one document per " +
                    "profile type, so which one applies would be a guess.");
            byType[p.Profile] = p;
        }

        // ---- 2. required and optional profile types --------------------------------------------------------------
        void Require(string type, string why)
        {
            if (!byType.ContainsKey(type))
                throw new CgmesStudyInputException(type, null, $"the required profile is missing: {why}.");
        }
        Require("EQ", "the equipment profile defines every object the study solves");
        Require("SSH", "the steady-state hypothesis holds the set-points and switch states of the case");
        if (!topologyFromSwitches) Require("TP", "the bus-branch view is read from the TopologicalNodes (choose topology 'switches' to build buses from the switch states instead)");
        if (needsStateVariables) Require("SV", "an import option reads the state variables (starting voltages, SV tap steps or SV shunt sections)");
        var omitted = new List<string>();
        if (!byType.ContainsKey("TP")) omitted.Add("TP: not supplied; the buses are built from the switch states (topology 'switches').");
        if (!byType.ContainsKey("SV")) omitted.Add("SV: not supplied; every bus starts at 1.0 pu, 0 degrees and tap steps and shunt sections come from SSH.");
        if (!byType.ContainsKey("EQ_BD") && !byType.ContainsKey("TP_BD")) omitted.Add("EQ_BD / TP_BD: not supplied; every reference must resolve inside the bundle.");
        if (!byType.ContainsKey("DY")) omitted.Add("DY: not supplied; no machine inertia is read (not used by this study's power flow).");
        if (!byType.ContainsKey("DL")) omitted.Add("DL: not supplied (layout only).");
        if (!byType.ContainsKey("GL")) omitted.Add("GL: not supplied (geography only).");

        // ---- 3. parse, read the header ----------------------------------------------------------------------------
        var headerIds = new Dictionary<string, CgmesProfileInput>(StringComparer.Ordinal);
        var headers = new Dictionary<CgmesProfileInput, (string? Id, List<string> Profiles, List<string> Deps, string? Time)>();
        foreach (CgmesProfileInput p in profiles)
        {
            string who = Who(p);
            string text;
            XDocument xml;
            try
            {
                text = new UTF8Encoding(false, true).GetString(p.Bytes);
                if (text.Length > 0 && text[0] == '﻿') text = text[1..];   // a byte-order mark, as File.ReadAllText drops it
                xml = ParseXml(text);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or DecoderFallbackException)
            {
                throw new CgmesStudyInputException(who, null, "not well-formed UTF-8 RDF/XML: " + ex.Message);
            }
            if (xml.Root is null || xml.Root.Name.LocalName != "RDF" || xml.Root.Name.NamespaceName != RdfNs)
                throw new CgmesStudyInputException(who, null, "the document element is not rdf:RDF.");
            XElement? header = xml.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "FullModel");
            string? id = null, time = null;
            var hp = new List<string>();
            var deps = new List<string>();
            if (header is null)
            {
                diagnostics.Add($"{who}: no md:FullModel header; the declared type is taken from the manifest alone and no dependency can be checked.");
            }
            else
            {
                id = About(header) is string raw ? Mrid(raw) : null;
                if (id is null) throw new CgmesStudyInputException(who, "md:FullModel", "the header has no rdf:about identifier.");
                if (headerIds.TryGetValue(id, out CgmesProfileInput? other))
                    throw new CgmesStudyInputException(who, "md:FullModel " + id,
                        $"the model identifier is also the identifier of {Who(other)}; two documents cannot be one model.");
                headerIds[id] = p;
                foreach (XElement e in header.Elements())
                {
                    switch (e.Name.LocalName)
                    {
                        case "Model.profile": hp.Add(e.Value.Trim()); break;
                        case "Model.DependentOn":
                            if (Resource(e) is string dep) deps.Add(Mrid(dep));
                            break;
                        case "Model.scenarioTime": time = e.Value.Trim(); break;
                    }
                }
                if (hp.Count == 0)
                    diagnostics.Add($"{who}: its header names no md:Model.profile; the declared type is taken from the manifest.");
                else if (!hp.Any(u => UriMatches(p.Profile, u)))
                    throw new CgmesStudyInputException(who, "md:Model.profile",
                        $"declared {p.Profile} but the header names {string.Join(", ", hp)}.");
            }
            headers[p] = (id, hp, deps, time);
            docs.Add(new Parsed(p, text, xml));
        }

        // ---- 4. relationships: every DependentOn names a model of this bundle --------------------------------------
        foreach (CgmesProfileInput p in profiles)
            foreach (string dep in headers[p].Deps)
                if (!headerIds.ContainsKey(dep))
                    throw new CgmesStudyInputException(Who(p), "md:Model.DependentOn " + dep,
                        "the profile depends on a model that no document of this bundle is (mis-referenced or missing " +
                        "profile; a boundary set must be supplied as EQ_BD / TP_BD).");

        // ---- 5. records: conflicting values across profiles, references that resolve to nothing --------------------
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var value = new Dictionary<(string Mrid, string Prop), (string Value, CgmesProfileInput Where)>();
        var classOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var props = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var count = new Dictionary<CgmesProfileInput, int>();
        foreach (Parsed d in docs)
        {
            int n = 0;
            foreach (XElement obj in d.Xml.Root!.Elements())
            {
                if (obj.Name.LocalName == "FullModel") continue;
                string? raw = IdOf(obj);
                if (raw is null) continue;
                string mrid = Mrid(raw);
                n++;
                defined.Add(mrid);
                if (obj.Name.LocalName != "Description") classOf.TryAdd(mrid, obj.Name.LocalName);
                if (!props.TryGetValue(mrid, out Dictionary<string, string>? bag)) props[mrid] = bag = new(StringComparer.Ordinal);
                foreach (XElement prop in obj.Elements())
                {
                    string name = prop.Name.LocalName;
                    string v = Resource(prop) is string r ? (IsInternal(r) ? "#" + Mrid(r) : r) : prop.Value.Trim();
                    bag.TryAdd(name, v);
                    if (value.TryGetValue((mrid, name), out (string Value, CgmesProfileInput Where) had))
                    {
                        if (!ReferenceEquals(had.Where, d.Input) && !string.Equals(had.Value, v, StringComparison.Ordinal))
                            throw new CgmesStudyInputException(Who(d.Input), $"{obj.Name.LocalName} {mrid} {name}",
                                $"conflicting duplicate: {Who(had.Where)} gives '{had.Value}', this profile gives '{v}'. " +
                                "The importer would silently keep the first; the bundle is refused instead.");
                    }
                    else value[(mrid, name)] = (v, d.Input);
                }
            }
            count[d.Input] = n;
        }
        foreach (Parsed d in docs)
            foreach (XElement obj in d.Xml.Root!.Elements())
            {
                if (obj.Name.LocalName == "FullModel" || IdOf(obj) is not string raw) continue;
                foreach (XElement prop in obj.Elements())
                    if (Resource(prop) is string r && IsInternal(r) && !defined.Contains(Mrid(r)))
                        throw new CgmesStudyInputException(Who(d.Input), $"{obj.Name.LocalName} {Mrid(raw)} {prop.Name.LocalName}",
                            $"the reference '{r}' names an object that no profile of this bundle defines (invalid or " +
                            "mis-referenced; a boundary object needs the EQ_BD / TP_BD of its boundary set).");
            }

        // ---- 6. automatic controls the study does not represent ------------------------------------------------------
        var unrepresented = new List<CgmesUnrepresentedControl>();
        foreach ((string mrid, string cls) in classOf.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!cls.EndsWith("TapChanger", StringComparison.Ordinal) && !cls.Contains("TapChanger", StringComparison.Ordinal)) continue;
            if (cls.EndsWith("Table", StringComparison.Ordinal) || cls.EndsWith("TablePoint", StringComparison.Ordinal) || cls == "TapChangerControl") continue;
            Dictionary<string, string> bag = props[mrid];
            bool enabled = bag.TryGetValue("TapChanger.controlEnabled", out string? ce) && ParseBoolean(ce) == true;
            bool hasControl = bag.ContainsKey("TapChanger.TapChangerControl");
            if (enabled || hasControl)
                unrepresented.Add(new CgmesUnrepresentedControl(mrid, cls,
                    "automatic tap control is not applied: the tap is held at the step the import options select (SSH or SV)."));
        }

        var records = new List<CgmesProfileRecord>(profiles.Count);
        foreach (CgmesProfileInput p in profiles)
        {
            var h = headers[p];
            bool consumed = ImportedTypes.Contains(p.Profile);
            records.Add(new CgmesProfileRecord(p.Profile, p.DeclaredVersion, p.Path, Hash(p.Bytes), h.Id, h.Profiles, h.Deps,
                h.Time, count[p], consumed,
                "Digest recorded; the source document is not re-emitted. The exchange output is regenerated from the solved model.",
                Coverage(p.Profile, topologyFromSwitches)));
        }
        parsed = docs;
        return new CgmesBundleReport(BundleIdentity(profiles), records, omitted, diagnostics, unrepresented);
    }

    private static string Coverage(string type, bool fromSwitches) => type switch
    {
        "EQ" => "Equipment, impedances, machine and unit limits, permanent (PATL) apparent-power ratings, regulating controls.",
        "TP" => fromSwitches
            ? "TopologicalNodes only compared with the buses the switch states give (topology 'switches')."
            : "Bus-branch topology: the TopologicalNodes are the buses.",
        "SSH" => "Set-points, switch states, tap steps, shunt sections, in-service and connected flags.",
        "SV" => "Starting voltages, SV tap steps and SV shunt sections, as the import options select.",
        "EQ_BD" or "TP_BD" => "Boundary nodes and their topology.",
        "DY" => "Machine inertia only; this study runs no dynamic simulation.",
        _ => "None: layout or geography only; not passed to the importer.",
    };

    /// <summary>Whether a header profile URI names the declared type (keyword match on the CGMES profile names; a
    /// consistency check only, not a conformity check).</summary>
    internal static bool UriMatches(string type, string uri)
    {
        bool Has(string s) => uri.Contains(s, StringComparison.OrdinalIgnoreCase);
        return type switch
        {
            "EQ" => Has("Equipment") && !Has("Boundary"),
            "EQ_BD" => Has("EquipmentBoundary") || (Has("Equipment") && Has("Boundary")),
            "TP" => Has("Topology") && !Has("Boundary"),
            "TP_BD" => Has("TopologyBoundary") || (Has("Topology") && Has("Boundary")),
            "SSH" => Has("SteadyStateHypothesis"),
            "SV" => Has("StateVariables"),
            "DY" => Has("Dynamics"),
            "DL" => Has("DiagramLayout"),
            "GL" => Has("GeographicalLocation"),
            _ => false,
        };
    }

    internal static string Who(CgmesProfileInput p) => $"{p.Profile} ({p.Path})";

    /// <summary>An XML Schema boolean as the importer reads it ("true"/"false" in any letter case, "1", "0");
    /// null for anything else or for nothing.</summary>
    internal static bool? ParseBoolean(string? value)
    {
        string? s = value?.Trim();
        if (s is null) return null;
        if (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>The mRID an identifier reduces to, by the importer's rule: one leading '#', then a 'urn:uuid:' prefix or
    /// one leading '_'.</summary>
    internal static string Mrid(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) return s["urn:uuid:".Length..];
        if (s.StartsWith('_')) s = s[1..];
        return s;
    }

    /// <summary>A reference inside the model (as opposed to an enumeration or namespace URI).</summary>
    private static bool IsInternal(string reference) =>
        reference.StartsWith('#') || reference.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase);

    internal static string? IdOf(XElement e) =>
        e.Attribute(XName.Get("ID", RdfNs))?.Value ?? About(e);

    private static string? About(XElement e) => e.Attribute(XName.Get("about", RdfNs))?.Value;

    private static string? Resource(XElement e) => e.Attribute(XName.Get("resource", RdfNs))?.Value;

    internal static XDocument ParseXml(string xml)
    {
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
        };
        using var sr = new StringReader(xml);
        using var reader = System.Xml.XmlReader.Create(sr, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }
}
