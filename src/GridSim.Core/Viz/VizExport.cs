using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;

namespace GridSim.Core.Viz;

/// <summary>
/// Exports a grid plus a time-series of solved snapshots over a simulated day, as
/// a single JSON document an external visualiser can load. The network
/// (buses with layout coordinates, branches) is static; each frame carries the
/// solved per-bus voltages/angles/injections and the system totals, so the viewer
/// can animate the grid through the day and let the user scrub/play/inspect.
/// </summary>
public static class VizExport
{
    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated VizExportJsonContext (below) so no runtime reflection is needed.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly VizExportJsonContext Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    });

    public static VizScene Build(GridModel baseModel, int frames = 72)
    {
        frames = Math.Max(1, frames);
        var solver = new SteadyStateSolver();

        var scene = new VizScene
        {
            Name = baseModel.Name,
            BaseMva = (float)baseModel.BaseMva,
            Buses = baseModel.Buses.Select(b => new VizBus
            {
                Id = b.Id,
                Name = b.Name ?? $"Bus {b.Id}",
                Kv = (float)b.BaseKv,
                Type = b.Type.ToString(),
                X = (float)b.X,
                Y = (float)b.Y,
            }).ToArray(),
            Branches = baseModel.Branches.Select(br => new VizBranch
            {
                From = br.From,
                To = br.To,
                Name = br.Name ?? "",
                Transformer = br.IsTransformer,
            }).ToArray(),
        };

        var frameList = new List<VizFrame>(frames);
        for (int i = 0; i < frames; i++)
        {
            double hour = i * 24.0 / frames;
            GridModel m = GridScenario.AtLoad(baseModel, LoadProfile.DailyFactor(hour));
            PowerFlowResult r = solver.Solve(m);

            frameList.Add(new VizFrame
            {
                Hour = (float)hour,
                LoadGw = (float)(m.Buses.Sum(b => b.Pd) / 1000.0),
                GenGw = (float)(SolvedGeneration.GeneratorOutputMw(m, r) / 1000.0),   // SEC-07: the units' output (was the positive net injections)
                LossMw = (float)r.TotalLossMw,
                FreqHz = (float)baseModel.FrequencyHz, // steady state: nominal (was hard-coded 50 Hz; R296)
                Converged = r.Converged,
                Vm = r.Vm.Select(v => (float)v).ToArray(),
                VaDeg = r.VaDeg.Select(v => (float)v).ToArray(),
                PInjMw = r.PInjMw.Select(v => (float)v).ToArray(),
                QInjMvar = r.QInjMvar.Select(v => (float)v).ToArray(),
            });
        }

        scene.Frames = frameList.ToArray();
        CheckFinite(scene);
        return scene;
    }

    /// <summary>SEC-07: every number of the scene is a 32-bit float, and JSON has no infinity or NaN. A value that is not
    /// finite in single precision (a base kV of 1e39, a NaN from a solve) is refused, naming where it is, instead of
    /// failing in the serializer as an internal error.</summary>
    private static void CheckFinite(VizScene scene)
    {
        static void Check(float v, string where)
        {
            if (!float.IsFinite(v))
                throw new InvalidDataException($"gridviz scene: {where} is {v.ToString(System.Globalization.CultureInfo.InvariantCulture)}, which is not a finite single-precision number, so the scene cannot be written.");
        }
        Check(scene.BaseMva, "the base MVA");
        foreach (VizBus b in scene.Buses)
        {
            Check(b.Kv, $"bus {b.Id}'s base kV");
            Check(b.X, $"bus {b.Id}'s x");
            Check(b.Y, $"bus {b.Id}'s y");
        }
        foreach (VizFrame f in scene.Frames)
        {
            string at = $"the frame at hour {f.Hour.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";
            Check(f.Hour, "a frame's hour");
            Check(f.LoadGw, at + ": the load");
            Check(f.GenGw, at + ": the generation");
            Check(f.LossMw, at + ": the losses");
            Check(f.FreqHz, at + ": the frequency");
            for (int k = 0; k < f.Vm.Length; k++) Check(f.Vm[k], $"{at}: bus {scene.Buses[k].Id}'s voltage magnitude");
            for (int k = 0; k < f.VaDeg.Length; k++) Check(f.VaDeg[k], $"{at}: bus {scene.Buses[k].Id}'s voltage angle");
            for (int k = 0; k < f.PInjMw.Length; k++) Check(f.PInjMw[k], $"{at}: bus {scene.Buses[k].Id}'s P injection");
            for (int k = 0; k < f.QInjMvar.Length; k++) Check(f.QInjMvar[k], $"{at}: bus {scene.Buses[k].Id}'s Q injection");
        }
    }

    public static string ToJson(VizScene scene) => JsonSerializer.Serialize(scene, Json.VizScene);

    public static void Write(VizScene scene, string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson(scene));
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for a visualiser scene and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(VizScene))]
internal sealed partial class VizExportJsonContext : JsonSerializerContext;
