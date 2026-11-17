using System.Text.Json;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.MotorStart;

/// <summary>
/// RC3 P8: the native case adapter of the motor acceleration study. It loads a native case through the incumbent gated
/// loaders' own entry points (<see cref="V2CaseLoader.LoadWithNotes"/>, <see cref="JsonGridLoader.LoadDirectory"/>)
/// with dispatch suppressed at the loader seam, and reads the AUTHORED generator schedule from the case files
/// themselves, so the study holds three separate states: authored inputs, the resolved model, and (in the runner) the
/// realised AC solution. Every in-service generator row must give outputMw, pMinMw and pMaxMw explicitly: a fixed
/// operating point never rests on a capacity-factor default.
/// </summary>
public static class MotorStartCase
{
    /// <summary>Load a gridsim-case/2 manifest. <paramref name="dispatch"/> is false for the study (loader balancing
    /// suppressed whatever system.dispatch says); true exists only to show the loader default against it.</summary>
    public static MotorStartCaseInput LoadV2(string manifestPath, string schemasDir, string? dataRoot, bool dispatch = false)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);
        V2CaseLoadResult loaded = V2CaseLoader.LoadWithNotes(manifestPath, schemasDir, dataRoot, dispatch);
        string full = Path.GetFullPath(manifestPath);
        string dir = Path.GetDirectoryName(full)!;
        bool? authoredDispatch;
        var authored = new List<MotorStartAuthoredGenerator>();
        using (JsonDocument manifest = JsonSchemaSubset.ParseFile(full))
        {
            JsonElement system = manifest.RootElement.GetProperty("system");
            authoredDispatch = system.TryGetProperty("dispatch", out JsonElement d) ? d.GetBoolean() : null;
            foreach (JsonElement n in manifest.RootElement.GetProperty("network").EnumerateArray())
            {
                string rel = n.GetString()!;
                using JsonDocument net = JsonSchemaSubset.ParseFile(Path.GetFullPath(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar))));
                if (net.RootElement.TryGetProperty("generators", out JsonElement gens))
                    Read(gens, rel, StringComparison.Ordinal, authored);
            }
        }
        bool effective = dispatch && (authoredDispatch ?? true);
        return new MotorStartCaseInput(loaded.Model, loaded.Motors, authored,
            new MotorStartLoadPolicy(V2CaseLoader.CaseSchemaTag, authoredDispatch, dispatch, effective, loaded.Notes));
    }

    /// <summary>Load a native v1 case folder as the pre-start network. A v1 case has no motor rows, so the motor data
    /// is given by the caller (<paramref name="motors"/>, with the meaning of a v2 motor row).</summary>
    public static MotorStartCaseInput LoadV1Directory(string directory, IReadOnlyList<PlacedMotor> motors, bool dispatch = false)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(motors);
        GridModel model = JsonGridLoader.LoadDirectory(directory, dispatch);
        bool? authoredDispatch = null;
        string system = Path.Combine(directory, "system.json");
        if (File.Exists(system))
        {
            using JsonDocument s = JsonSchemaSubset.ParseFile(system);
            foreach (JsonProperty p in s.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "dispatch", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    authoredDispatch = p.Value.GetBoolean();
        }
        var authored = new List<MotorStartAuthoredGenerator>();
        string gen = Path.Combine(directory, "generation.json");
        if (File.Exists(gen))
        {
            using JsonDocument g = JsonSchemaSubset.ParseFile(gen);
            Read(g.RootElement, "generation.json", StringComparison.OrdinalIgnoreCase, authored);
        }
        foreach (PlacedMotor m in motors)
            if (!model.Buses.Any(b => b.Id == m.Bus))
                throw new InvalidDataException($"motor '{m.Name}': bus {m.Bus} is not a bus of the v1 case.");
        return new MotorStartCaseInput(model, motors, authored,
            new MotorStartLoadPolicy("v1 case folder", authoredDispatch, dispatch, dispatch && (authoredDispatch ?? true), Array.Empty<string>()));
    }

    private static void Read(JsonElement array, string file, StringComparison names, List<MotorStartAuthoredGenerator> into)
    {
        int i = 0;
        foreach (JsonElement g in array.EnumerateArray())
        {
            string at = $"{file}: generator {i++}";
            string? name = Get(g, "name", names) is { ValueKind: JsonValueKind.String } n ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException($"{at}: the motor study resolves units by name, and this one has none.");
            JsonElement? bus = Get(g, "bus", names);
            bool inService = Get(g, "inService", names) is not { ValueKind: JsonValueKind.False };
            JsonElement? output = Get(g, "outputMw", names), pmin = Get(g, "pMinMw", names), pmax = Get(g, "pMaxMw", names);
            if (inService && (output is not { ValueKind: JsonValueKind.Number } || pmin is not { ValueKind: JsonValueKind.Number } || pmax is null))
                throw new InvalidDataException($"{at} ('{name}'): the motor study needs a fixed authored operating point, so every in-service generator must give outputMw, pMinMw and pMaxMw explicitly (an absent outputMw would fall back to capacity times capacity factor).");
            double max = pmax is { ValueKind: JsonValueKind.String } s && s.GetString() == "Infinity" ? double.PositiveInfinity
                : pmax is { ValueKind: JsonValueKind.Number } pm ? pm.GetDouble() : double.NaN;
            into.Add(new MotorStartAuthoredGenerator(name!, bus is { ValueKind: JsonValueKind.Number } b ? b.GetInt32() : 0,
                output is { ValueKind: JsonValueKind.Number } o ? o.GetDouble() : double.NaN,
                pmin is { ValueKind: JsonValueKind.Number } pn ? pn.GetDouble() : double.NaN, max, inService));
        }
    }

    private static JsonElement? Get(JsonElement o, string name, StringComparison cmp)
    {
        foreach (JsonProperty p in o.EnumerateObject())
            if (string.Equals(p.Name, name, cmp)) return p.Value;
        return null;
    }
}
