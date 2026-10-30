using System.Text.Json;
using GridSim.Core.IO.Json.Schema;
using GridSim.Tests.Editor;
using GridSim.Tests.V2;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme ASSETS slice 5-view: a JSON v2 case with an asset overlay for the ASSETS tests. The case is the bundled
/// GB reduced case (data/uk) projected to v2 exactly as the v2 bit-identity pins project it (V1Projector), written to a
/// fresh temporary folder, with (or without) two overlay files that use all thirteen overlay kinds and every reference
/// type against the case's own elements:
/// <list type="bullet">
/// <item>file A (assets/site.assets.json): the substation 'ov.sub' at North Scotland, its two busbar sections (bus 1, the
/// 400 kV bus, written BEFORE their parent; and bus 101, the GSP bus), a bus coupler, four bays (line, transformer,
/// generator, demand), a normally open disconnector and earth switch, an earth path to a soil zone in file B, a
/// three-winding transformer over T2/T3/T4 with its star bus 102, and an HVDC link between Moyle (an interconnector) and
/// Torness;</item>
/// <item>file B (lines.assets.json): a conductor and a tower type with one conductor position, the route of line L2-3
/// with a span and an earth wire, and the soil zone.</item>
/// </list>
/// Twenty records in all.
/// </summary>
internal sealed class UiOverlayFixture : IDisposable
{
    /// <summary>The overlay files as the manifest lists them.</summary>
    public const string FileA = "assets/site.assets.json", FileB = "lines.assets.json";

    /// <summary>How many records the overlay holds.</summary>
    public const int RecordCount = 20;

    public const string OverlayA = """
        { "schema": "gridsim-assets/2", "description": "ASSETS 5-view fixture, file A.", "records": [
          { "id": "ov.bb1", "kind": "busbarSection", "parent": "ov.sub", "refs": { "bus": { "bus": 1 } }, "params": { "label": "Main 400", "ratedCurrentA": { "v": 4000, "unit": "A", "prov": { "tag": "Synthetic", "note": "test fixture" } } } },
          { "id": "ov.sub", "kind": "substation", "refs": {}, "params": { "name": "North Scotland", "operator": "Test", "latitudeDeg": 57.5, "longitudeDeg": -4.2 }, "prov": { "tag": "Synthetic", "note": "test fixture" }, "note": "a substation note" },
          { "id": "ov.bb2", "kind": "busbarSection", "parent": "ov.sub", "refs": { "bus": { "bus": 101 } }, "params": {} },
          { "id": "ov.cpl", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" }, "to": { "record": "ov.bb2" } }, "params": { "device": "breaker", "normalOpen": false } },
          { "id": "ov.cbl", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" }, "line": { "line": "L1-2 N-S Scotland" } }, "params": { "device": "breaker", "normalOpen": false, "ratedCurrentA": 4000, "breakingCurrentA": 63000 } },
          { "id": "ov.cbt", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" }, "transformer": { "transformer": "T1 GSP N Scotland" } }, "params": { "device": "breaker", "normalOpen": false } },
          { "id": "ov.cbg", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" }, "generator": { "generator": "North Scotland Hydro" } }, "params": { "device": "breaker", "normalOpen": false } },
          { "id": "ov.cbd", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb2" }, "demand": { "demand": "North Scotland" } }, "params": { "device": "breaker", "normalOpen": false } },
          { "id": "ov.ds", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" } }, "params": { "device": "disconnector", "normalOpen": true } },
          { "id": "ov.es", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb2" } }, "params": { "device": "earthSwitch", "normalOpen": true } },
          { "id": "ov.earth", "kind": "earthPath", "parent": "ov.sub", "refs": { "bus": { "bus": 1 }, "soil": { "record": "ov.soil" } }, "params": { "resistanceOhm": { "v": 0.5, "unit": "ohm" } } },
          { "id": "ov.t3w", "kind": "transformer3W", "parent": "ov.sub", "refs": { "starBus": { "bus": 102 }, "hv": { "transformer": "T2 GSP S Scotland" }, "mv": { "transformer": "T3 GSP N England" }, "lv": { "transformer": "T4 GSP Yorkshire" } }, "params": { "baseMva": 100, "hvKv": 400, "mvKv": 132, "lvKv": 13, "r12Pu": 0.002, "x12Pu": 0.1, "r13Pu": 0.003, "x13Pu": 0.2, "r23Pu": 0.002, "x23Pu": 0.15 } },
          { "id": "ov.hvdc", "kind": "hvdcLink", "refs": { "sendingConverter": { "generator": "Moyle" }, "receivingConverter": { "generator": "Torness Nuclear" } }, "params": { "technology": "vsc", "ratedMw": 500, "dcVoltageKv": 250, "cableLengthKm": 55 }, "note": "over the authored generators" }
        ] }
        """;

    public const string OverlayB = """
        { "schema": "gridsim-assets/2", "records": [
          { "id": "ov.zebra", "kind": "conductorType", "refs": {}, "params": { "code": "Zebra", "material": "acsr", "crossSectionMm2": { "v": 428.9, "unit": "mm2" }, "diameterMm": { "v": 28.62, "unit": "mm" }, "dcResistanceOhmPerKm": 0.0674, "ratedCurrentA": 900 } },
          { "id": "ov.l6", "kind": "towerType", "refs": {}, "params": { "code": "L6", "heightM": 50, "circuits": 2 } },
          { "id": "ov.l6.a", "kind": "conductorPosition", "parent": "ov.l6", "refs": {}, "params": { "phase": "a", "circuit": 1, "xM": -6.5, "yM": { "v": 30, "unit": "m" } } },
          { "id": "ov.route", "kind": "lineRoute", "refs": { "line": { "line": "L2-3 B6 boundary" } }, "params": { "name": "B6 route", "routeLengthKm": 120 } },
          { "id": "ov.span1", "kind": "span", "parent": "ov.route", "refs": { "conductor": { "record": "ov.zebra" }, "tower": { "record": "ov.l6" } }, "params": { "lengthM": 350, "chainageM": 0, "sagM": 9.5, "latitudeDeg": 55.9, "longitudeDeg": -3.0 } },
          { "id": "ov.ew", "kind": "earthWire", "parent": "ov.route", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "count": 1, "bondedAtBothEnds": true } },
          { "id": "ov.soil", "kind": "soilZone", "refs": {}, "params": { "resistivityOhmM": { "v": 100, "unit": "ohm.m" }, "depthM": 2 } }
        ] }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-assets-5view-" + Guid.NewGuid().ToString("N"));

    /// <summary>A fresh v2 projection of data/uk, with the overlay when <paramref name="withOverlay"/>, and
    /// <paramref name="overlayA"/> in place of file A when given (a bad overlay for the refusal tests).</summary>
    public UiOverlayFixture(bool withOverlay = true, string? overlayA = null)
    {
        Directory.CreateDirectory(_root);
        V2Projection p = V1Projector.Project(V1Projector.ReadV1(Path.Combine(EditorTestSupport.DataDir, "uk")));
        ManifestPath = Path.Combine(_root, "uk.case.json");
        File.WriteAllBytes(ManifestPath, p.CaseJson);
        File.WriteAllBytes(Path.Combine(_root, V1Projector.NetworkFileName), p.NetworkJson);
        if (withOverlay) AddOverlay(ManifestPath, overlayA ?? OverlayA, OverlayB);
    }

    /// <summary>The case folder.</summary>
    public string CaseDir => _root;

    /// <summary>The manifest (gridsim-case/2).</summary>
    public string ManifestPath { get; }

    /// <summary>The network file of the projection.</summary>
    public string NetworkPath => Path.Combine(_root, V1Projector.NetworkFileName);

    /// <summary>Write the two overlay files beside <paramref name="manifestPath"/> and list them in its "assets" member.</summary>
    public static void AddOverlay(string manifestPath, string a, string b)
    {
        string dir = Path.GetDirectoryName(manifestPath)!;
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(Path.Combine(dir, FileA.Replace('/', Path.DirectorySeparatorChar)), a);
        File.WriteAllText(Path.Combine(dir, FileB), b);
        using JsonDocument m = JsonSchemaSubset.ParseFile(manifestPath);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (JsonProperty prop in m.RootElement.EnumerateObject())
                if (prop.Name != "assets") prop.WriteTo(w);
            w.WriteStartArray("assets");
            w.WriteStringValue(FileA);
            w.WriteStringValue(FileB);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.WriteAllBytes(manifestPath, ms.ToArray());
    }

    /// <summary>The two overlay files as (name, root) pairs, read back from disk (for the checker).</summary>
    public IReadOnlyList<(string Name, JsonElement Root)> OverlayDocuments() => new[]
    {
        (FileA, V2TestSupport.ReadRoot(Path.Combine(_root, FileA.Replace('/', Path.DirectorySeparatorChar)))),
        (FileB, V2TestSupport.ReadRoot(Path.Combine(_root, FileB))),
    };

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a leftover temp folder is harmless */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
    }
}
