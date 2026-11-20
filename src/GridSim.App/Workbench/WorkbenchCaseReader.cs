using System.Text.Json;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Cim.Zip;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Ucte;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;

namespace GridSim.App.Workbench;

/// <summary>What the workbench read: the model, its name, the notes the reader gave, and whether the diagram can edit the source.</summary>
internal sealed record WorkbenchCaseRead(GridModel Model, string Name, IReadOnlyList<string> Notes, string? EditableSource);

/// <summary>
/// A4: routes a path to the reader that already owns the format. It parses nothing itself: v2 JSON cases and MATPOWER files go
/// through <see cref="SimHost.ReadCase"/> (what the main window loads), v1 folders through <see cref="JsonGridLoader"/>, PSS/E RAW,
/// UCTE-DEF and CGMES through the Core readers the CLI uses (<see cref="PsseRawParser"/>, <see cref="UcteParser"/>,
/// <see cref="CgmesArchiveReader"/> with <see cref="CimImporter"/> and <see cref="CimGridModelBridge"/>), node-breaker files through
/// <see cref="SubstationJson"/>. The caller applies the licence gate.
/// </summary>
internal static class WorkbenchCaseReader
{
    public const string Supported = "a GridSim case folder or v2 *.case.json, a MATPOWER .m, PSS/E .raw, UCTE .uct, a CGMES .zip or folder, or a node-breaker .json file";

    /// <summary>Read <paramref name="full"/> (a full path). Returns null when the path is not a format the workbench reads.</summary>
    public static WorkbenchCaseRead? Read(string full)
    {
        if (Directory.Exists(full))
        {
            string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (File.Exists(Path.Combine(full, "buses.json"))) return new(JsonGridLoader.LoadDirectory(full), name, [], full);
            if (Directory.EnumerateFiles(full, "*.xml", SearchOption.AllDirectories).Any()) return Cgmes(CgmesArchiveReader.ReadDirectory(full), name);
            return null;
        }
        if (!File.Exists(full)) return null;
        string stem = Path.GetFileNameWithoutExtension(full);
        string ext = Path.GetExtension(full).ToLowerInvariant();
        switch (ext)
        {
            case ".m":
                return new(SimHost.ReadCase(new ModelSource("workbench", Path.GetDirectoryName(full)!, MFile: full)).Model, stem, [], null);
            case ".raw":
                {
                    PsseParseResult p = PsseRawParser.ParseFile(full);
                    return new(p.Model, stem, p.Notes, null);
                }
            case ".uct" or ".uce":
                {
                    UcteParseResult p = UcteParser.ParseFile(full);
                    return new(p.Model, stem, p.Notes, null);
                }
            case ".zip":
                return Cgmes(CgmesArchiveReader.ReadFile(full), stem);
            case ".json":
                switch (SchemaTag(full))
                {
                    case "gridsim-case/2":
                        {
                            (GridModel model, _, IReadOnlyList<string> notes) = SimHost.ReadCase(ModelCatalog.V2CaseSource(full));
                            return new(model, stem.EndsWith(".case", StringComparison.OrdinalIgnoreCase) ? stem[..^5] : stem, notes, null);
                        }
                    default:
                        // Any other .json is read as a node-breaker file (gridsim-substations/1), whose reader names what is wrong with it.
                        if (full.EndsWith(".case.json", StringComparison.OrdinalIgnoreCase)) return V2(full, stem);
                        return new(SubstationProjection.ProjectNormal(SubstationJson.Read(File.ReadAllText(full))).Model, stem, [], full);
                }
            default:
                return null;
        }
    }

    private static WorkbenchCaseRead V2(string full, string stem)
    {
        (GridModel model, _, IReadOnlyList<string> notes) = SimHost.ReadCase(ModelCatalog.V2CaseSource(full));
        return new(model, stem[..^5], notes, null);
    }

    private static WorkbenchCaseRead Cgmes(CgmesArchive archive, string name)
    {
        string[] docs = archive.Entries.Where(e => e.Kind is not (CgmesProfileKind.Unknown or CgmesProfileKind.DL or CgmesProfileKind.GL or CgmesProfileKind.SC))
            .Select(e => e.Text).ToArray();
        if (docs.Length == 0) throw new InvalidDataException("No CGMES profile documents (EQ, TP, SSH, SV) were found.");
        var notes = new List<string>();
        CimNetwork net = CimImporter.Parse(docs, CimImportOptions.Default);
        GridModel model = CimGridModelBridge.ToGridModel(net, name: name, notes: notes);
        return new(model, name, notes, null);
    }

    /// <summary>The "schema" property of a JSON file's root object, or "" when it has none or the file is not an object.</summary>
    private static string SchemaTag(string path)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("schema", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }
}
