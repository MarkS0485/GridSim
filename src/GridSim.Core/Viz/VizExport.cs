using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;

namespace GridSim.Core.Viz;

/// <summary>
/// Exports a grid plus a time-series of solved snapshots over a simulated day, as
/// a single JSON document a visualiser (the Unity front-end) can load. The network
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
                GenGw = (float)(r.PInjMw.Where(p => p > 0).Sum() / 1000.0),
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
        return scene;
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
