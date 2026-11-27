using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GridSim.Core.IO.Scl;

// =====================================================================================================================
// RC5 WP30: reader for the substation section of an IEC 61850 SCL file (SSD, SCD, ICD, CID, ...).
//
// Source basis: the public SCL XML schema, version 2007 revision B release 4 (IEC 61850-6:2009/AMD1:2018 code
// components, SCL_Substation.xsd and SCL_Enums.xsd, copyright IEC, free to download under the IEC code component licence,
// www.iec.ch/CCv1). The element and attribute names below are the schema's. The schema gives the conducting-equipment
// type codes (tPredefinedCommonConductingEquipmentEnum) without any documentation of their meaning; the meanings used by
// the importer are the names OpenSCD shows for them (Apache-2.0, see SclImporter). No IEC 61850 standard text was used.
//
// The reader only collects: what is in the substation section, with every element counted, and what the other sections
// hold, counted. It does not judge the equipment; SclImporter does. It is safe on untrusted input: the file size is
// capped (InputFileLimits), a DTD is refused (so no entity can be declared or expanded, and nothing external is fetched),
// no resolver is set, and elements may nest at most MaxDepth deep.
// =====================================================================================================================

/// <summary>One <c>Terminal</c> of a piece of equipment or a transformer winding.</summary>
/// <param name="ConnectivityNode">The <c>connectivityNode</c> attribute: the pathName of the node it sits on.</param>
/// <param name="CNodeName">The <c>cNodeName</c> attribute. <c>grounded</c> marks a terminal joined to earth.</param>
public sealed record SclTerminal(string Name, string ConnectivityNode, string CNodeName)
{
    /// <summary>The name a terminal joined to earth carries in <c>cNodeName</c> (the convention OpenSCD documents for
    /// earth switches: a terminal with <c>cNodeName="grounded"</c>).</summary>
    public const string GroundedName = "grounded";

    /// <summary>True for a terminal joined to earth rather than to a connectivity node.</summary>
    public bool Grounded => CNodeName == GroundedName;
}

/// <summary>A <c>VoltageLevel</c>, with its <c>Voltage</c> as written (value, multiplier and unit), or nulls when it has none.</summary>
public sealed record SclVoltageLevel(string Substation, string Name, string Path, string? VoltageText, string? Multiplier, string? Unit);

/// <summary>A <c>Bay</c> of a voltage level.</summary>
public sealed record SclBay(string Substation, string VoltageLevelPath, string Name, string Path);

/// <summary>A <c>ConnectivityNode</c> of a bay, keyed by its <c>pathName</c>.</summary>
public sealed record SclConnectivityNode(string Substation, string VoltageLevelPath, string BayPath, string Name, string PathName);

/// <summary>A <c>ConductingEquipment</c> of a bay. <see cref="Path"/> is substation/voltage level/bay/name.</summary>
public sealed record SclEquipment(string Substation, string VoltageLevelPath, string BayPath, string Name, string Path, string Type,
    IReadOnlyList<SclTerminal> Terminals);

/// <summary>A <c>TransformerWinding</c> of a power transformer.</summary>
public sealed record SclWinding(string Name, string Path, string Type, IReadOnlyList<SclTerminal> Terminals);

/// <summary>A <c>PowerTransformer</c>, held by a substation, a voltage level or a bay (<see cref="ContainerPath"/>).</summary>
public sealed record SclTransformer(string Substation, string ContainerPath, string Name, string Path, string Type, IReadOnlyList<SclWinding> Windings);

/// <summary>A top-level <c>Line</c> or <c>Process</c> element (not imported). <see cref="HoldsEquipment"/> is true when
/// it holds conducting equipment or a power transformer, which the importer then refuses.</summary>
public sealed record SclRootContainer(string Kind, string Name, bool HoldsEquipment);

/// <summary>An element of the substation section the reader does not read (a <c>Function</c>, <c>SubFunction</c>,
/// <c>Private</c>, ...) that holds conducting equipment or a power transformer, which would otherwise be lost; the
/// importer refuses it. <see cref="Kind"/> is the element name, <see cref="Name"/> its name attribute (empty when it has
/// none) and <see cref="Where"/> what holds it.</summary>
public sealed record SclSkippedContainer(string Kind, string Name, string Where);

/// <summary>An SCL file the reader refuses: not well formed, not SCL, with a DTD, too deep, or too large.</summary>
public sealed class SclFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What <see cref="SclReader"/> found in one SCL file. Every list is in document order.</summary>
public sealed class SclDocument
{
    /// <summary>The <c>version</c>, <c>revision</c> and <c>release</c> attributes of the root (empty when absent).</summary>
    public required string Version { get; init; }
    public required string Revision { get; init; }
    public required string Release { get; init; }
    /// <summary>The <c>Header/@id</c> (empty when absent).</summary>
    public required string HeaderId { get; init; }
    /// <summary>The SHA-256 of the file's bytes, lower-case hex.</summary>
    public required string Sha256 { get; init; }
    public required IReadOnlyList<string> Substations { get; init; }
    public required IReadOnlyList<SclVoltageLevel> VoltageLevels { get; init; }
    public required IReadOnlyList<SclBay> Bays { get; init; }
    public required IReadOnlyList<SclConnectivityNode> Nodes { get; init; }
    public required IReadOnlyList<SclEquipment> Equipment { get; init; }
    public required IReadOnlyList<SclTransformer> Transformers { get; init; }
    public required IReadOnlyList<SclRootContainer> RootContainers { get; init; }
    /// <summary>Unread elements that hold conducting equipment or a power transformer (see <see cref="SclSkippedContainer"/>).</summary>
    public IReadOnlyList<SclSkippedContainer> SkippedWithEquipment { get; init; } = [];
    /// <summary>Elements of the substation section (and top-level elements) the importer does not read, by element
    /// name, with how many there are. Only the outermost unread element is counted (its children are not).</summary>
    public required IReadOnlyDictionary<string, int> NotImported { get; init; }
    /// <summary>The sections read past (Communication, IED, DataTypeTemplates), as named counts, in a fixed order.</summary>
    public required IReadOnlyList<(string What, int Count)> ReadPast { get; init; }
    /// <summary>The GOOSE control blocks (<c>GSEControl</c>), named IED/LDevice/name. Listed by name only.</summary>
    public required IReadOnlyList<string> GooseControlBlocks { get; init; }
    /// <summary>The number of elements of each name in the whole file (SCL namespace by local name; other namespaces as
    /// {namespace}name), in ordinal order.</summary>
    public required IReadOnlyDictionary<string, int> ElementCounts { get; init; }
}

/// <summary>Reads an SCL file into an <see cref="SclDocument"/>. See the file header for the source basis and the
/// safety rules.</summary>
public static class SclReader
{
    /// <summary>The SCL namespace (all SCL editions use it).</summary>
    public const string Namespace = "http://www.iec.ch/61850/2003/SCL";

    /// <summary>The deepest element nesting read (the root is depth 0). Real SCL files nest about 15 deep.</summary>
    public const int MaxDepth = 64;

    private static readonly XNamespace Ns = Namespace;

    /// <summary>Read the SCL file at <paramref name="path"/> (refused when longer than the input-file limit).</summary>
    public static SclDocument Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(InputFileLimits.ReadAllBytes(path), System.IO.Path.GetFileName(path));
    }

    /// <summary>Read SCL text (tests).</summary>
    public static SclDocument ParseText(string xml, string name = "scl") =>
        Parse(new UTF8Encoding(false).GetBytes(xml ?? throw new ArgumentNullException(nameof(xml))), name);

    /// <summary>Read SCL bytes.</summary>
    public static SclDocument Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.LongLength > InputFileLimits.MaxBytes)
            throw new InputFileTooLargeException(name, bytes.LongLength, InputFileLimits.MaxBytes);
        XDocument doc = Load(bytes, name);
        XElement root = doc.Root ?? throw new SclFormatException($"{name}: the file has no root element.");
        if (root.Name != Ns + "SCL")
            throw new SclFormatException($"{name}: the root element is '{root.Name.LocalName}' in namespace '{root.Name.NamespaceName}', not SCL in {Namespace}; not an SCL file.");
        return new Walker(name).Walk(root, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static XmlReaderSettings Settings(long maxChars) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        // A DTD is refused, so no entity can be declared; the cap is still finite (the document's own size).
        MaxCharactersFromEntities = maxChars,
        MaxCharactersInDocument = maxChars,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    private static XDocument Load(byte[] bytes, string name)
    {
        // Each byte decodes to at most one character, so the byte count caps the characters too.
        XmlReaderSettings settings = Settings(Math.Max(1, bytes.LongLength));
        try
        {
            // First pass: depth only, without building a tree.
            using (var ms = new MemoryStream(bytes, writable: false))
            using (var r = XmlReader.Create(ms, settings))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.Depth > MaxDepth)
                        throw new SclFormatException($"{name}: elements nest deeper than {MaxDepth} levels (element '{r.LocalName}'); refused.");
            using var ms2 = new MemoryStream(bytes, writable: false);
            using var r2 = XmlReader.Create(ms2, settings);
            return XDocument.Load(r2);
        }
        catch (XmlException ex)
        {
            if (HasDoctype(bytes))
                throw new SclFormatException($"{name}: the file has a DTD (<!DOCTYPE>). GridSim never processes a DTD in an SCL file (no entity is declared or expanded and nothing external is fetched); refused.", ex);
            throw new SclFormatException($"{name}: not well-formed XML: {ex.Message}", ex);
        }
    }

    /// <summary>True when the text of the file holds a document type declaration.</summary>
    private static bool HasDoctype(byte[] bytes)
    {
        string text;
        try
        {
            using var sr = new StreamReader(new MemoryStream(bytes, writable: false), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            text = sr.ReadToEnd();
        }
        catch (DecoderFallbackException) { return false; }
        return text.Contains("<!DOCTYPE", StringComparison.Ordinal);
    }

    private sealed class Walker(string fileName)
    {
        private readonly List<string> _subs = [];
        private readonly List<SclVoltageLevel> _levels = [];
        private readonly List<SclBay> _bays = [];
        private readonly List<SclConnectivityNode> _nodes = [];
        private readonly List<SclEquipment> _equipment = [];
        private readonly List<SclTransformer> _transformers = [];
        private readonly List<SclRootContainer> _roots = [];
        private readonly List<SclSkippedContainer> _skipped = [];
        private readonly SortedDictionary<string, int> _notImported = new(StringComparer.Ordinal);
        private readonly List<string> _goose = [];

        private static string Key(XName n) => n.Namespace == Ns ? n.LocalName : "{" + n.NamespaceName + "}" + n.LocalName;

        private void Skip(XElement e) => _notImported[Key(e.Name)] = _notImported.GetValueOrDefault(Key(e.Name)) + 1;

        private static bool HoldsEquipment(XElement e) =>
            e.Descendants().Any(d => d.Name == Ns + "ConductingEquipment" || d.Name == Ns + "PowerTransformer");

        /// <summary>Count an unread element, and record it when it holds equipment (so the import refuses it rather than
        /// drop that equipment silently).</summary>
        private void Skip(XElement e, string where)
        {
            Skip(e);
            if (HoldsEquipment(e)) _skipped.Add(new SclSkippedContainer(Key(e.Name), Attr(e, "name"), where));
        }

        private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? "";

        private string Name(XElement e, string where)
        {
            string n = Attr(e, "name");
            if (n.Length == 0) throw new SclFormatException($"{fileName}: a {e.Name.LocalName} in {where} has no name attribute.");
            return n;
        }

        public SclDocument Walk(XElement root, string sha)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (XElement e in root.DescendantsAndSelf())
                counts[Key(e.Name)] = counts.GetValueOrDefault(Key(e.Name)) + 1;

            string headerId = "";
            int subNetworks = 0, connectedAps = 0, ieds = 0, logicalNodes = 0, svControls = 0, templates = 0, communication = 0, dataTypeTemplates = 0;
            foreach (XElement c in root.Elements())
            {
                if (c.Name == Ns + "Header") { headerId = Attr(c, "id"); continue; }
                if (c.Name == Ns + "Substation") { ReadSubstation(c); continue; }
                if (c.Name == Ns + "Communication")
                {
                    communication++;
                    subNetworks += c.Elements(Ns + "SubNetwork").Count();
                    connectedAps += c.Descendants(Ns + "ConnectedAP").Count();
                    continue;
                }
                if (c.Name == Ns + "IED")
                {
                    ieds++;
                    logicalNodes += c.Descendants().Count(d => d.Name == Ns + "LN0" || d.Name == Ns + "LN");
                    svControls += c.Descendants(Ns + "SampledValueControl").Count();
                    string ied = Attr(c, "name");
                    foreach (XElement ld in c.Descendants(Ns + "LDevice"))
                        foreach (XElement g in ld.Descendants(Ns + "GSEControl"))
                            _goose.Add($"{ied}/{Attr(ld, "inst")}/{Attr(g, "name")}");
                    continue;
                }
                if (c.Name == Ns + "DataTypeTemplates") { dataTypeTemplates++; templates += c.Elements().Count(); continue; }
                if (c.Name == Ns + "Line" || c.Name == Ns + "Process")
                {
                    _roots.Add(new SclRootContainer(c.Name.LocalName, Attr(c, "name"), HoldsEquipment(c)));
                    Skip(c);
                    continue;
                }
                Skip(c, "the file");
            }

            return new SclDocument
            {
                Version = Attr(root, "version"),
                Revision = Attr(root, "revision"),
                Release = Attr(root, "release"),
                HeaderId = headerId,
                Sha256 = sha,
                Substations = _subs.AsReadOnly(),
                VoltageLevels = _levels.AsReadOnly(),
                Bays = _bays.AsReadOnly(),
                Nodes = _nodes.AsReadOnly(),
                Equipment = _equipment.AsReadOnly(),
                Transformers = _transformers.AsReadOnly(),
                RootContainers = _roots.AsReadOnly(),
                SkippedWithEquipment = _skipped.AsReadOnly(),
                NotImported = _notImported,
                ReadPast =
                [
                    ("Communication sections", communication), ("SubNetwork", subNetworks), ("ConnectedAP", connectedAps),
                    ("IED", ieds), ("logical nodes (LN0 and LN)", logicalNodes), ("SampledValueControl", svControls),
                    ("DataTypeTemplates sections", dataTypeTemplates), ("DataTypeTemplates entries", templates),
                ],
                GooseControlBlocks = _goose.AsReadOnly(),
                ElementCounts = counts,
            };
        }

        private void ReadSubstation(XElement s)
        {
            string sub = Name(s, "the file");
            _subs.Add(sub);
            foreach (XElement c in s.Elements())
            {
                if (c.Name == Ns + "VoltageLevel") ReadLevel(sub, c);
                else if (c.Name == Ns + "PowerTransformer") ReadTransformer(sub, sub, c);
                else Skip(c, $"substation '{sub}'");
            }
        }

        private void ReadLevel(string sub, XElement v)
        {
            string path = sub + "/" + Name(v, $"substation '{sub}'");
            XElement? voltage = v.Element(Ns + "Voltage");
            _levels.Add(new SclVoltageLevel(sub, Attr(v, "name"), path, voltage?.Value.Trim(),
                voltage is null ? null : Attr(voltage, "multiplier"), voltage is null ? null : Attr(voltage, "unit")));
            foreach (XElement c in v.Elements())
            {
                if (c.Name == Ns + "Voltage") continue;
                if (c.Name == Ns + "Bay") ReadBay(sub, path, c);
                else if (c.Name == Ns + "PowerTransformer") ReadTransformer(sub, path, c);
                else Skip(c, $"voltage level '{path}'");
            }
        }

        private void ReadBay(string sub, string levelPath, XElement b)
        {
            string path = levelPath + "/" + Name(b, $"voltage level '{levelPath}'");
            _bays.Add(new SclBay(sub, levelPath, Attr(b, "name"), path));
            foreach (XElement c in b.Elements())
            {
                if (c.Name == Ns + "ConnectivityNode")
                    _nodes.Add(new SclConnectivityNode(sub, levelPath, path, Attr(c, "name"), Attr(c, "pathName")));
                else if (c.Name == Ns + "ConductingEquipment")
                    _equipment.Add(new SclEquipment(sub, levelPath, path, Name(c, $"bay '{path}'"), path + "/" + Attr(c, "name"),
                        Attr(c, "type"), Terminals(c, $"equipment '{path}/{Attr(c, "name")}'")));
                else if (c.Name == Ns + "PowerTransformer") ReadTransformer(sub, path, c);
                else Skip(c, $"bay '{path}'");
            }
        }

        private void ReadTransformer(string sub, string container, XElement t)
        {
            string path = container + "/" + Name(t, $"'{container}'");
            var windings = new List<SclWinding>();
            foreach (XElement c in t.Elements())
            {
                if (c.Name == Ns + "TransformerWinding")
                    windings.Add(new SclWinding(Name(c, $"power transformer '{path}'"), path + "/" + Attr(c, "name"), Attr(c, "type"),
                        Terminals(c, $"winding '{path}/{Attr(c, "name")}'")));
                else Skip(c, $"power transformer '{path}'");
            }
            _transformers.Add(new SclTransformer(sub, container, Attr(t, "name"), path, Attr(t, "type"), windings.AsReadOnly()));
        }

        /// <summary>The terminals of an equipment or winding; every other child is counted as not imported.</summary>
        private List<SclTerminal> Terminals(XElement e, string where)
        {
            var list = new List<SclTerminal>();
            foreach (XElement c in e.Elements())
            {
                if (c.Name == Ns + "Terminal") list.Add(new SclTerminal(Attr(c, "name"), Attr(c, "connectivityNode"), Attr(c, "cNodeName")));
                else Skip(c, where);
            }
            return list;
        }
    }
}
