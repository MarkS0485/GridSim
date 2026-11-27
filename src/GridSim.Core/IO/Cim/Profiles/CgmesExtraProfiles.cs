using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Cim.Profiles;

// =====================================================================================================================
// RC5 WP10: the CGMES diagram layout (DL), geographical location (GL), short-circuit (SC) and dynamics (DY) profiles for
// the bus-branch export of CgmesWriter. New entry points only: CgmesWriter, CimExporter, CimImporter, the bridge and the
// node-breaker exporter are unchanged, and their output is byte for byte what it was. Each profile is a separate
// document whose objects refer to the CgmesWriter document's objects (TopologicalNode _N<id>, ACLineSegment _L<k>,
// PowerTransformer _T<k> with ends _T<k>_e1 / _e2, SynchronousMachine _G<k>).
//
// Specifications read: the ENTSO-E CGMES 2.4.15 RDFS (ENTSOE_CGMES_v2.4.15_04Jul2016_RDFS.zip, the CGMES Library at
// https://www.entsoe.eu/data/cim/cim-for-grid-models-exchange/): DiagramLayoutProfileRDFSAugmented-v2_4_15-16Feb2016,
// GeographicalLocationProfileRDFSAugmented-v2_4_15-16Feb2016, EquipmentProfileCoreShortCircuitRDFSAugmented-v2_4_15-4Jul2016
// and DynamicsProfileRDFSAugmented-v2_4_15-16Feb2016 (class and attribute names, data types, multiplicities); and the
// CGMES 3.0 PROF and RDFS files of https://github.com/entsoe/CGMES-AP (Apache-2.0) for the 3.0 profile URIs.
// =====================================================================================================================

/// <summary>What <see cref="CgmesExtraProfiles.Write(GridModel, CgmesWriteResult, CgmesExtraOptions)"/> writes, and the data it writes from.</summary>
/// <param name="Profiles">The profiles to write: any of DL, GL, SC and DY (each at most once).</param>
/// <param name="Version">The edition. Only CGMES 2.4.15 is written: the bus-branch document of <see cref="CgmesWriter"/>
/// that these profiles extend is a CIM16 (CGMES 2.4.15) document.</param>
/// <param name="Geo">The geographical data for GL (a <c>gridsim-geo/1</c> sidecar); required for GL.</param>
/// <param name="Dynamics">The DYR records for DY (from <see cref="PsseDyrReader.ReadRecords(string)"/>); required for DY.</param>
/// <param name="MachineKeys">For DY: the PSS/E bus and machine ID of each in-service generator of the model, in
/// <see cref="GridModel.Generators"/> order (for a case read from a RAW file, <see cref="CgmesDynamicsProfile.KeysOf"/>);
/// required for DY.</param>
/// <param name="ScenarioTime">md:Model.scenarioTime and md:Model.created of every document.</param>
/// <param name="MachineArmatureResistances">For DY: the armature resistance of each in-service generator, pu on its MBase,
/// aligned to <paramref name="MachineKeys"/> (for a case read from a RAW file, the ZSORCE resistance ZR:
/// <see cref="CgmesDynamicsProfile.ArmatureResistancesOf"/>); required for DY. It is written as
/// RotatingMachineDynamics.statorResistance.</param>
public sealed record CgmesExtraOptions(
    IReadOnlyList<CgmesProfileKind> Profiles,
    CgmesVersion Version = CgmesVersion.V2_4_15,
    GeoSidecar? Geo = null,
    IReadOnlyList<PsseDyrRecord>? Dynamics = null,
    IReadOnlyList<PsseMachineKey>? MachineKeys = null,
    string ScenarioTime = "2026-01-01T00:00:00Z",
    IReadOnlyList<double>? MachineArmatureResistances = null);

/// <summary>What <see cref="CgmesExtraProfiles.Write(GridModel, CgmesWriteResult, CgmesExtraOptions)"/> wrote.</summary>
/// <param name="Files">One document per requested profile, in the order DL, GL, SC, DY.</param>
/// <param name="Omissions">What the documents do not carry, and why.</param>
/// <param name="Notes">Values with no exact CGMES form and other one-line facts.</param>
public sealed record CgmesExtraResult(IReadOnlyList<CgmesProfileFile> Files, IReadOnlyList<CgmesOmission> Omissions, IReadOnlyList<string> Notes);

/// <summary>
/// RC5 WP10: writes the DL, GL, SC and DY profile documents of a bus-branch <see cref="GridModel"/> that
/// <see cref="CgmesWriter.Write"/> has written (its <see cref="CgmesWriteResult"/> names the objects these documents refer
/// to). Throws <see cref="InvalidDataException"/> listing every reason when a requested profile cannot be written.
/// </summary>
/// <remarks>
/// <para>Each document has an md:FullModel header naming its profile (<see cref="CgmesVocabulary.ExtraProfileUris"/>) and a
/// Model.DependentOn on the id <c>urn:uuid:</c> + <see cref="CgmesVocabulary.Uuid"/>("model/&lt;name&gt;/EQ"). The bus-branch
/// document itself carries no md:FullModel header (CgmesWriter's output is unchanged), so that id is the one the export set
/// gives its equipment document; it is stated in the omissions. New objects are identified rdf:ID="_" + the UUID of
/// "DL:", "GL:" or "DY:" plus the object's key; attributes of objects the bus-branch document defines are written as
/// rdf:about descriptions of those objects.</para>
/// </remarks>
public static class CgmesExtraProfiles
{
    /// <summary>Write the requested profiles of <paramref name="model"/>, whose bus-branch document is <paramref name="written"/>.</summary>
    public static CgmesExtraResult Write(GridModel model, CgmesWriteResult written, CgmesExtraOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(options);
        var refused = new List<string>();
        if (options.Version != CgmesVersion.V2_4_15)
            refused.Add("CGMES 3.0 is not written for the bus-branch export: the CgmesWriter document these profiles refer to is a CGMES 2.4.15 (CIM16) document, and a 3.0 profile cannot name its objects.");
        foreach (var g in options.Profiles.GroupBy(p => p).Where(g => g.Count() > 1))
            refused.Add($"the profile {g.Key} is requested {g.Count()} times.");
        foreach (CgmesProfileKind k in options.Profiles.Distinct())
            if (k is not (CgmesProfileKind.DL or CgmesProfileKind.GL or CgmesProfileKind.SC or CgmesProfileKind.DY))
                refused.Add($"the profile {k} is not one of DL, GL, SC and DY.");
        if (refused.Count > 0) throw Refused(refused);
        return WriteCore(model, written, options, CgmesVersion.V2_4_15);
    }

    /// <summary>
    /// WP24: write the requested profiles in CGMES 3.0 for a model whose EQ, TP, SSH and SV documents are
    /// <paramref name="written"/> (<see cref="CgmesBusBranch30Writer.Write"/>). The documents name the 3.0 objects by their
    /// urn:uuid: identifiers; new objects carry IdentifiedObject.mRID. <paramref name="options"/>.Version must be
    /// <see cref="CgmesVersion.V3_0"/>. SC machine r0, x0, r2 and x2 are written in ohms (the 3.0 unit).
    /// </summary>
    public static CgmesExtraResult Write(GridModel model, CgmesBusBranch30Result written, CgmesExtraOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(options);
        var refused = new List<string>();
        if (options.Version != CgmesVersion.V3_0)
            refused.Add("this entry point writes CGMES 3.0 profiles for the CGMES 3.0 bus-branch documents; for CGMES 2.4.15 pass the CgmesWriter result.");
        foreach (var g in options.Profiles.GroupBy(p => p).Where(g => g.Count() > 1))
            refused.Add($"the profile {g.Key} is requested {g.Count()} times.");
        foreach (CgmesProfileKind k in options.Profiles.Distinct())
            if (k is not (CgmesProfileKind.DL or CgmesProfileKind.GL or CgmesProfileKind.SC or CgmesProfileKind.DY))
                refused.Add($"the profile {k} is not one of DL, GL, SC and DY.");
        if (refused.Count > 0) throw Refused(refused);
        return WriteCore(model, written.Source, options, CgmesVersion.V3_0);
    }

    private static CgmesExtraResult WriteCore(GridModel model, CgmesWriteResult written, CgmesExtraOptions options, CgmesVersion version)
    {
        var refused = new List<string>();
        var files = new List<CgmesProfileFile>();
        var omissions = new List<CgmesOmission>();
        var notes = new List<string>();
        string name = model.Name ?? "case";
        foreach (CgmesProfileKind k in new[] { CgmesProfileKind.DL, CgmesProfileKind.GL, CgmesProfileKind.SC, CgmesProfileKind.DY })
        {
            if (!options.Profiles.Contains(k)) continue;
            var doc = new ExtraDocument(k, name, options.ScenarioTime, version);
            switch (k)
            {
                case CgmesProfileKind.DL: CgmesDiagramLayout.WriteInto(doc, model, refused, omissions, notes); break;
                case CgmesProfileKind.GL: CgmesGeographicalLocation.WriteInto(doc, model, written, options.Geo, refused, omissions, notes); break;
                case CgmesProfileKind.SC: CgmesShortCircuitProfile.WriteInto(doc, model, written, refused, omissions, notes); break;
                default: CgmesDynamicsProfile.WriteInto(doc, model, options.Dynamics, options.MachineKeys, options.MachineArmatureResistances, refused, omissions, notes); break;
            }
            files.Add(new CgmesProfileFile(k, name + "_" + k + ".xml", doc.Serialise()));
        }
        if (refused.Count > 0) throw Refused(refused);
        if (version == CgmesVersion.V3_0) return new CgmesExtraResult(files, omissions, notes);
        omissions.Add(new CgmesOmission(CgmesProfileKind.EQ, "md:FullModel header of the bus-branch document",
            "the CgmesWriter document is written exactly as before, without a header; the profile documents' Model.DependentOn names the id urn:uuid:"
            + CgmesVocabulary.Uuid($"model/{name}/EQ") + ", which only this export set uses."));
        return new CgmesExtraResult(files, omissions, notes);
    }

    internal static InvalidDataException Refused(IReadOnlyList<string> reasons) =>
        new("The CGMES profile documents cannot be written, so nothing was written:" + Environment.NewLine + "  - "
            + string.Join(Environment.NewLine + "  - ", reasons));

    internal static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    internal static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    /// <summary>The CgmesWriter search: the double whose read-back <paramref name="back"/> gives <paramref name="target"/>'s bits,
    /// searched outward from <paramref name="guess"/>; the nearest one (counted in <paramref name="inexact"/>) when none does.</summary>
    internal static double Exact(double target, double guess, Func<double, double> back, ref int inexact)
    {
        if (Same(back(guess), target)) return guess;
        double up = guess, down = guess, best = guess, bestErr = Math.Abs(back(guess) - target);
        for (int k = 0; k < 64; k++)
        {
            up = Math.BitIncrement(up);
            if (Same(back(up), target)) return up;
            down = Math.BitDecrement(down);
            if (Same(back(down), target)) return down;
            double eu = Math.Abs(back(up) - target), ed = Math.Abs(back(down) - target);
            if (eu < bestErr) { best = up; bestErr = eu; }
            if (ed < bestErr) { best = down; bestErr = ed; }
        }
        inexact++;
        return best;
    }
}

/// <summary>One profile document under construction. CGMES 2.4.15 (the default, byte for byte as WP10 wrote it): CIM16
/// namespace, new objects rdf:ID="_...", and objects of the CgmesWriter document named "#_&lt;mRID&gt;". CGMES 3.0 (WP24): CIM100
/// namespace and the 3.0 profile URI; new objects still rdf:ID="_&lt;uuid&gt;" (so the WP10 readers, which take a defining
/// rdf:ID, read them) and, being IdentifiedObjects, carry IdentifiedObject.mRID; objects of the
/// <see cref="CgmesBusBranch30Writer"/> documents are named by their urn:uuid: identifier.</summary>
internal sealed class ExtraDocument
{
    internal static readonly XNamespace Cim = CgmesVocabulary.CimNamespace(CgmesVersion.V2_4_15);
    private static readonly XNamespace RdfNs = CgmesVocabulary.Rdf, MdNs = CgmesVocabulary.Md;
    private readonly XElement _root;
    private readonly CgmesProfileKind _kind;
    private readonly XNamespace _cim;
    private readonly CgmesVersion _version;
    private readonly string _modelName;

    /// <summary>Classes written by these profiles that are not IdentifiedObjects (no mRID in CGMES 3.0).</summary>
    private static readonly HashSet<string> NotIdentified = new(StringComparer.Ordinal) { "DiagramObjectPoint", "PositionPoint" };

    public ExtraDocument(CgmesProfileKind kind, string modelName, string time) : this(kind, modelName, time, CgmesVersion.V2_4_15) { }

    public ExtraDocument(CgmesProfileKind kind, string modelName, string time, CgmesVersion version)
    {
        _kind = kind;
        _version = version;
        _modelName = modelName;
        _cim = CgmesVocabulary.CimNamespace(version);
        bool v3 = version == CgmesVersion.V3_0;
        _root = new XElement(RdfNs + "RDF",
            new XAttribute(XNamespace.Xmlns + "rdf", RdfNs.NamespaceName), new XAttribute(XNamespace.Xmlns + "cim", _cim.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "md", MdNs.NamespaceName));
        var fm = new XElement(MdNs + "FullModel", new XAttribute(RdfNs + "about", "urn:uuid:" + CgmesVocabulary.Uuid($"model/{modelName}/{kind}")),
            new XElement(MdNs + "Model.scenarioTime", time), new XElement(MdNs + "Model.created", time),
            new XElement(MdNs + "Model.description", v3 ? $"GridSim CGMES 3.0 bus-branch {kind} profile" : $"GridSim RC5 WP10 {kind} profile"), new XElement(MdNs + "Model.version", "1"),
            new XElement(MdNs + "Model.DependentOn", new XAttribute(RdfNs + "resource", "urn:uuid:" + CgmesVocabulary.Uuid($"model/{modelName}/EQ"))),
            new XElement(MdNs + "Model.profile", CgmesVocabulary.ExtraProfileUris(version, kind)),
            new XElement(MdNs + "Model.modelingAuthoritySet", v3 ? "http://gridsim.invalid/bus-branch-3.0" : "http://gridsim.invalid/rc5-wp10"));
        _root.Add(fm);
    }

    public CgmesVersion Version => _version;

    /// <summary>The rdf:ID of a new object of this profile with the key <paramref name="key"/>.</summary>
    public string NewId(string key) => "_" + CgmesVocabulary.Uuid(_kind + ":" + key);

    /// <summary>A new object (rdf:ID). Properties are (name, text), (name, Ref(..)) or (name, EnumOf(..)).</summary>
    public void New(string cls, string rdfId, params (string Prop, string Value)[] props)
    {
        var e = new XElement(_cim + cls, new XAttribute(RdfNs + "ID", rdfId));
        if (_version == CgmesVersion.V3_0 && !NotIdentified.Contains(cls))
            e.Add(new XElement(_cim + "IdentifiedObject.mRID", rdfId.StartsWith('_') ? rdfId[1..] : rdfId));
        Add(e, props);
    }

    /// <summary>A description of an object the bus-branch documents define: rdf:about="#_mrid" (2.4.15) or the object's
    /// urn:uuid: identifier (3.0).</summary>
    public void About(string cls, string mrid, params (string Prop, string Value)[] props) => Add(new XElement(_cim + cls, new XAttribute(RdfNs + "about", ObjectIri(mrid))), props);

    /// <summary>A reference value to <paramref name="rdfId"/> (an rdf:ID of this document, with its leading underscore).</summary>
    public static string Ref(string rdfId) => "\u0001#" + rdfId;

    /// <summary>A reference value to the object <paramref name="mrid"/> of the bus-branch documents (CgmesWriter's mRID).</summary>
    public string RefObject(string mrid) => "\u0001" + ObjectIri(mrid);

    private string ObjectIri(string mrid) =>
        _version == CgmesVersion.V3_0 ? CgmesBusBranch30Writer.ObjectIri(_modelName, mrid) : "#_" + mrid;

    /// <summary>A reference to an enumeration literal in this document's CIM namespace ("OrientationKind.negative").</summary>
    public string EnumOf(string literal) => "\u0001" + _cim.NamespaceName + literal;

    private void Add(XElement e, (string Prop, string Value)[] props)
    {
        foreach ((string p, string v) in props)
            e.Add(v.Length > 0 && v[0] == '\u0001'
                ? new XElement(_cim + p, new XAttribute(RdfNs + "resource", v[1..]))
                : new XElement(_cim + p, v));
        _root.Add(e);
    }

    public string Serialise()
    {
        var sb = new StringBuilder();
        var s = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace, OmitXmlDeclaration = true };
        using (var xw = XmlWriter.Create(sb, s)) _root.WriteTo(xw);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + sb + "\n";
    }
}

/// <summary>One object of a set of CGMES RDF/XML documents: its class, bare mRID and properties in document order.</summary>
internal sealed class CgmesObject(string mrid)
{
    public string Mrid { get; } = mrid;
    /// <summary>The class of the element that defined it (rdf:ID, or the first rdf:about description when none did).</summary>
    public string? Class { get; set; }
    public bool Defined { get; set; }
    /// <summary>(property local name, literal text or null, referenced bare mRID or enumeration IRI or null).</summary>
    public List<(string Prop, string? Text, string? Ref)> Props { get; } = new();

    public string? Text(string prop) { foreach (var p in Props) if (p.Prop == prop && p.Text is not null) return p.Text; return null; }
    public string? Ref(string prop) { foreach (var p in Props) if (p.Prop == prop && p.Ref is not null) return p.Ref; return null; }
    public bool Has(string prop) { foreach (var p in Props) if (p.Prop == prop) return true; return false; }
    public IEnumerable<string> Names => Props.Select(p => p.Prop).Distinct();
}

/// <summary>
/// The objects of a set of CGMES RDF/XML documents, merged by mRID across documents (DTD processing prohibited). An
/// identifier is reduced as CimImporter reduces it: one leading '#', then one leading '_', or a <c>urn:uuid:</c> prefix
/// (a UUID folded to lower case). An enumeration value keeps its full IRI.
/// </summary>
internal sealed class CgmesObjectSet
{
    private readonly Dictionary<string, CgmesObject> _byMrid = new(StringComparer.Ordinal);
    private readonly List<CgmesObject> _order = new();

    public IReadOnlyList<CgmesObject> Objects => _order;

    public CgmesObject? Get(string mrid) => _byMrid.GetValueOrDefault(mrid);

    public IEnumerable<CgmesObject> OfClass(string cls) => _order.Where(o => o.Class == cls);

    public static string Bare(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase))
        {
            string id = s["urn:uuid:".Length..];
            return Guid.TryParseExact(id, "D", out _) ? id.ToLowerInvariant() : id;
        }
        if (s.StartsWith('#')) s = s[1..];
        return s.StartsWith('_') ? s[1..] : s;
    }

    public static CgmesObjectSet Parse(IEnumerable<string> documents)
    {
        var set = new CgmesObjectSet();
        const string rdf = CgmesVocabulary.Rdf;
        int n = 0;
        foreach (string text in documents)
        {
            n++;
            XDocument doc;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var sr = new StringReader(text);
                using var xr = XmlReader.Create(sr, settings);
                doc = XDocument.Load(xr);
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"CGMES document {n} is not a well-formed XML document without a DTD: {ex.Message}");
            }
            if (doc.Root is null) continue;
            foreach (XElement e in doc.Root.Elements())
            {
                if (e.Name.NamespaceName == CgmesVocabulary.Md) continue;
                string? id = (string?)e.Attribute(XName.Get("ID", rdf));
                string? about = (string?)e.Attribute(XName.Get("about", rdf));
                if (id is null && about is null) continue;
                string mrid = Bare(id ?? about!);
                if (!set._byMrid.TryGetValue(mrid, out CgmesObject? o))
                {
                    o = new CgmesObject(mrid);
                    set._byMrid[mrid] = o;
                    set._order.Add(o);
                }
                if (id is not null && !o.Defined) { o.Class = e.Name.LocalName; o.Defined = true; }
                else o.Class ??= e.Name.LocalName;
                foreach (XElement p in e.Elements())
                {
                    string? res = (string?)p.Attribute(XName.Get("resource", rdf));
                    if (res is not null)
                    {
                        string r = res.Trim();
                        bool local = r.StartsWith('#') || r.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase) || !r.Contains(':', StringComparison.Ordinal);
                        o.Props.Add((p.Name.LocalName, null, local ? Bare(r) : r));
                    }
                    else o.Props.Add((p.Name.LocalName, p.Value.Trim(), null));
                }
            }
        }
        return set;
    }

    /// <summary>The leaf of an enumeration IRI ("...#WindingConnection.Yn" gives "Yn").</summary>
    public static string EnumLeaf(string iri)
    {
        int cut = Math.Max(iri.LastIndexOf('.'), iri.LastIndexOf('#'));
        return iri[(cut + 1)..];
    }

    /// <summary>A double attribute; null when absent. Throws naming the object when the text is not a finite number.</summary>
    public static double? Double(CgmesObject o, string prop)
    {
        string? t = o.Text(prop);
        if (t is null) return null;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new InvalidDataException($"{o.Class} '{o.Mrid}': {prop} is '{t}', not a finite number.");
        return v;
    }
}
