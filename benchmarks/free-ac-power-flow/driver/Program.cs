using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.PowerFlow;

if (args.Length != 4 || args[1] is not ("dense" or "sparse"))
{
    Console.Error.WriteLine("Usage: BenchmarkDriver case.m dense|sparse output-directory gridsim-commit");
    return 2;
}

var (casePath, path, output, commit) = (args[0], args[1], args[2], args[3]);
Directory.CreateDirectory(output);
var options = new PowerFlowOptions
{
    FlatStart = true,
    EnforceQLimits = false,
    Tolerance = 1e-8,
    MaxIterations = 50,
};
var json = new JsonSerializerOptions
{
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

IPowerFlowSolver Solver() => path == "dense"
    ? new NewtonRaphsonPowerFlow()
    : new SparseNewtonRaphsonPowerFlow();

Solver().Solve(MatpowerParser.ParseFile(casePath), options);
var saves = new List<Action>();
for (int attempt = 1; attempt <= 10; attempt++)
{
    var model = MatpowerParser.ParseFile(casePath);
    var solver = Solver();
    var started = DateTimeOffset.UtcNow;
    long start = Stopwatch.GetTimestamp();
    var result = solver.Solve(model, options);
    double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    var row = new { attempt, elapsedMs, started, result };
    int index = attempt;
    saves.Add(() =>
    {
        File.WriteAllText(Path.Combine(output, $"attempt-{index:D2}.json"), JsonSerializer.Serialize(row, json));
        SolveExport.WriteJson(Path.Combine(output, $"export-{index:D2}.json"), model, result,
            new SolveTrace(), new SolveExportMeta(Path.GetFileName(casePath), path, commit,
                started.ToString("O"), elapsedMs, Environment.ProcessorCount + " logical CPUs"));
    });
}
foreach (var save in saves)
    save();
return 0;



