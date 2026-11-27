using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.IO.Cim.Profiles;

// =====================================================================================================================
// RC4 R4-11 part B: shared vocabulary of the per-profile CGMES layer (new entry points; the incumbent CimImporter,
// CimExporter and CgmesWriter are untouched).
//
// Specifications used (ENTSO-E, free): CGMES 2.4.15 (2014, IEC 61970-600-1/-2 based; RDFS + OCL; ENTSO-E CGMES Library,
// https://www.entsoe.eu/data/cim/cim-for-grid-models-exchange/) and CGMES 3.0 (IEC 61970-600-2:2021 / CIM100; RDFS +
// SHACL, https://github.com/entsoe/application-profiles-library and https://github.com/entsoe/CGMES-AP, Apache-2.0).
// The files written here follow the class and attribute names of those profiles as GridSim's authors read them; no
// schema validator was run against them (see CgmesConformance for what IS checked).
// =====================================================================================================================

/// <summary>Which CGMES edition a document set follows.</summary>
public enum CgmesVersion
{
    /// <summary>CGMES 2.4.15: CIM16 namespace, rdf:ID="_mRID" identifiers.</summary>
    V2_4_15,
    /// <summary>CGMES 3.0: CIM100 namespace, urn:uuid: identifiers.</summary>
    V3_0,
}

/// <summary>The profile a CGMES document belongs to (from its md:Model.profile URIs).</summary>
public enum CgmesProfileKind
{
    Unknown,
    /// <summary>Equipment (core, operation).</summary>
    EQ,
    /// <summary>Topology.</summary>
    TP,
    /// <summary>Steady state hypothesis.</summary>
    SSH,
    /// <summary>State variables.</summary>
    SV,
    /// <summary>Diagram layout.</summary>
    DL,
    /// <summary>Geographical location.</summary>
    GL,
    /// <summary>Short circuit.</summary>
    SC,
    /// <summary>Dynamics.</summary>
    DY,
    /// <summary>Boundary equipment.</summary>
    EQBD,
    /// <summary>Boundary topology.</summary>
    TPBD,
}

/// <summary>One class or data item a profile export does not write, and why (the structured omissions record).</summary>
public sealed record CgmesOmission(CgmesProfileKind Profile, string Item, string Reason);

/// <summary>One document of a profile set.</summary>
public sealed record CgmesProfileFile(CgmesProfileKind Kind, string FileName, string Text);

/// <summary>Namespaces, profile URIs and identifier spellings of one CGMES edition.</summary>
public static class CgmesVocabulary
{
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public const string Md = "http://iec.ch/TC57/61970-552/ModelDescription/1#";

    public static string CimNamespace(CgmesVersion v) =>
        v == CgmesVersion.V3_0 ? "http://iec.ch/TC57/CIM100#" : "http://iec.ch/TC57/2013/CIM-schema-cim16#";

    /// <summary>The md:Model.profile URI(s) a written document declares.</summary>
    public static IReadOnlyList<string> ProfileUris(CgmesVersion v, CgmesProfileKind k) => (v, k) switch
    {
        (CgmesVersion.V2_4_15, CgmesProfileKind.EQ) => new[] { "http://entsoe.eu/CIM/EquipmentCore/3/1", "http://entsoe.eu/CIM/EquipmentOperation/3/1" },
        (CgmesVersion.V2_4_15, CgmesProfileKind.TP) => new[] { "http://entsoe.eu/CIM/Topology/4/1" },
        (CgmesVersion.V2_4_15, CgmesProfileKind.SSH) => new[] { "http://entsoe.eu/CIM/SteadyStateHypothesis/1/1" },
        (CgmesVersion.V2_4_15, CgmesProfileKind.SV) => new[] { "http://entsoe.eu/CIM/StateVariables/4/1" },
        (CgmesVersion.V3_0, CgmesProfileKind.EQ) => new[] { "http://iec.ch/TC57/ns/CIM/CoreEquipment-EU/3.0", "http://iec.ch/TC57/ns/CIM/Operation-EU/3.0" },
        (CgmesVersion.V3_0, CgmesProfileKind.TP) => new[] { "http://iec.ch/TC57/ns/CIM/Topology-EU/3.0" },
        (CgmesVersion.V3_0, CgmesProfileKind.SSH) => new[] { "http://iec.ch/TC57/ns/CIM/SteadyStateHypothesis-EU/3.0" },
        (CgmesVersion.V3_0, CgmesProfileKind.SV) => new[] { "http://iec.ch/TC57/ns/CIM/StateVariables-EU/3.0" },
        _ => throw new ArgumentOutOfRangeException(nameof(k), k, "GridSim writes EQ, TP, SSH and SV only."),
    };

    /// <summary>
    /// RC5 WP10: the md:Model.profile URI of the diagram layout, geographical location, short-circuit and dynamics
    /// profiles (<see cref="ProfileUris"/> keeps refusing these four kinds). CGMES 2.4.15: the profile URIs the ENTSO-E
    /// RDFS files of 4 July 2016 declare (DiagramLayout/3/1, GeographicalLocation/2/1, EquipmentShortCircuit/3/1, which
    /// ENTSO-E places inside the EQ profile, and Dynamics/3/1). CGMES 3.0: the owl:versionIRI of the PROF files in
    /// entsoe/CGMES-AP (CGMES/PROF/CGMES-DL-PROF.rdf and so on; note that the dynamics profile is version 1.0).
    /// </summary>
    public static string ExtraProfileUris(CgmesVersion v, CgmesProfileKind k) => (v, k) switch
    {
        (CgmesVersion.V2_4_15, CgmesProfileKind.DL) => "http://entsoe.eu/CIM/DiagramLayout/3/1",
        (CgmesVersion.V2_4_15, CgmesProfileKind.GL) => "http://entsoe.eu/CIM/GeographicalLocation/2/1",
        (CgmesVersion.V2_4_15, CgmesProfileKind.SC) => "http://entsoe.eu/CIM/EquipmentShortCircuit/3/1",
        (CgmesVersion.V2_4_15, CgmesProfileKind.DY) => "http://entsoe.eu/CIM/Dynamics/3/1",
        (CgmesVersion.V3_0, CgmesProfileKind.DL) => "http://iec.ch/TC57/ns/CIM/DiagramLayout-EU/3.0",
        (CgmesVersion.V3_0, CgmesProfileKind.GL) => "http://iec.ch/TC57/ns/CIM/GeographicalLocation-EU/3.0",
        (CgmesVersion.V3_0, CgmesProfileKind.SC) => "http://iec.ch/TC57/ns/CIM/ShortCircuit-EU/3.0",
        (CgmesVersion.V3_0, CgmesProfileKind.DY) => "http://iec.ch/TC57/ns/CIM/Dynamics-EU/1.0",
        _ => throw new ArgumentOutOfRangeException(nameof(k), k, "ExtraProfileUris covers DL, GL, SC and DY only."),
    };

    /// <summary>Classify a document from its declared profile URIs (null when none is recognised).</summary>
    public static CgmesProfileKind? KindOf(IEnumerable<string> profileUris)
    {
        foreach (string u in profileUris)
        {
            string s = u.ToLowerInvariant();
            // Order matters: the boundary profiles contain the words of the main ones.
            if (s.Contains("equipmentboundary") || s.Contains("equipmentbd") || s.Contains("equipmentboundary-eu")) return CgmesProfileKind.EQBD;
            if (s.Contains("topologyboundary") || s.Contains("topologybd")) return CgmesProfileKind.TPBD;
            if (s.Contains("equipmentcore") || s.Contains("coreequipment") || s.Contains("equipmentoperation")
                || s.Contains("operation-eu") || s.Contains("equipment/")) return CgmesProfileKind.EQ;
            if (s.Contains("equipmentshortcircuit") || s.Contains("shortcircuit")) return CgmesProfileKind.SC;
            if (s.Contains("steadystatehypothesis")) return CgmesProfileKind.SSH;
            if (s.Contains("statevariables")) return CgmesProfileKind.SV;
            if (s.Contains("topology")) return CgmesProfileKind.TP;
            if (s.Contains("diagramlayout")) return CgmesProfileKind.DL;
            if (s.Contains("geographicallocation")) return CgmesProfileKind.GL;
            if (s.Contains("dynamics")) return CgmesProfileKind.DY;
        }
        return null;
    }

    /// <summary>Classify by the file name's suffix (_EQ.xml, _TP.xml, ...), the fallback when no profile URI is declared.</summary>
    public static CgmesProfileKind KindOfFileName(string name)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        foreach (var (suffix, kind) in new[]
        {
            ("_EQ_BD", CgmesProfileKind.EQBD), ("_TP_BD", CgmesProfileKind.TPBD), ("_EQ", CgmesProfileKind.EQ), ("_TP", CgmesProfileKind.TP),
            ("_SSH", CgmesProfileKind.SSH), ("_SV", CgmesProfileKind.SV), ("_DL", CgmesProfileKind.DL), ("_GL", CgmesProfileKind.GL),
            ("_SC", CgmesProfileKind.SC), ("_DY", CgmesProfileKind.DY),
        })
            if (stem.EndsWith(suffix, StringComparison.Ordinal)) return kind;
        return CgmesProfileKind.Unknown;
    }

    /// <summary>A deterministic RFC 4122 style UUID (lower case) from a string: SHA-256, first 16 bytes, version 5 / variant bits set.</summary>
    public static string Uuid(string key)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var b = new byte[16];
        Array.Copy(h, b, 16);
        b[6] = (byte)((b[6] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        string x = Convert.ToHexString(b).ToLowerInvariant();
        return $"{x[..8]}-{x[8..12]}-{x[12..16]}-{x[16..20]}-{x[20..]}";
    }
}
