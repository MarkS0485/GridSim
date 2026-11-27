using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Probabilistic;
using GridSim.Core.Model;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-09: <c>gridsim probabilistic &lt;study.json&gt; [--out &lt;result.json&gt;] [--threads N]</c>. A strict
/// <c>gridsim-probabilistic/1</c> study: a probabilistic load flow over a case bound by SHA-256, or a generation adequacy
/// study (no case), run by one or several methods (Monte Carlo, quasi-Monte Carlo, shifted quasi-Monte Carlo,
/// convolution) so a single study can compare them. The seed is mandatory. A method outside its domain is reported OUT OF
/// SCOPE, never approximated. The result is written only to a new file, through a temporary file and a final move.
/// </summary>
internal static class ProbabilisticCommand
{
    private const string Usage = "usage: gridsim probabilistic <study.json> [--out <result.json>] [--threads N]";

    private sealed record DistributionInput
    {
        public required string Kind { get; init; }
        public double? Mean { get; init; }
        public double? Sd { get; init; }
        public double? Mu { get; init; }
        public double? Sigma { get; init; }
        public double? Scale { get; init; }
        public double? Shape { get; init; }
        public double? Alpha { get; init; }
        public double? Beta { get; init; }
        public double? Lo { get; init; }
        public double? Hi { get; init; }
        public double? Capacity { get; init; }
        public double? Availability { get; init; }
        public AtomInput[]? Atoms { get; init; }
        public double[]? Values { get; init; }
    }
    private sealed record AtomInput { public required double Value { get; init; } public required double Probability { get; init; } }
    private sealed record InputRow
    {
        public required string Id { get; init; }
        public required string Target { get; init; }
        public required int Bus { get; init; }
        public int? Unit { get; init; }
        public bool? Scale { get; init; }
        public required DistributionInput Distribution { get; init; }
    }
    private sealed record CorrelationRow { public required int A { get; init; } public required int B { get; init; } public required double Rho { get; init; } }
    private sealed record VoltageRow { public required double MinPu { get; init; } public required double MaxPu { get; init; } }
    private sealed record StateRow { public required double CapacityMw { get; init; } public required double Probability { get; init; } }
    private sealed record UnitRow
    {
        public required string Id { get; init; }
        public double? CapacityMw { get; init; }
        public double? Availability { get; init; }
        public StateRow[]? States { get; init; }
    }
    private sealed record LoadRow { public double[]? HourlyMw { get; init; } public double? ConstantMw { get; init; } public int? Hours { get; init; } }
    private sealed record MethodRow
    {
        public required string Method { get; init; }
        public required int Samples { get; init; }
        public int? Replicates { get; init; }
        public int? CellsPerSd { get; init; }
    }
    private sealed record CaseRow { public string? Path { get; init; } public string? BuiltIn { get; init; } public required string Sha256 { get; init; } }
    private sealed record Request
    {
        public required int SchemaVersion { get; init; }
        public required string Study { get; init; }
        public required ulong Seed { get; init; }
        public required MethodRow[] Methods { get; init; }
        public string? Domain { get; init; }
        // load-flow
        public CaseRow? Case { get; init; }
        public string? Evaluator { get; init; }
        public InputRow[]? Inputs { get; init; }
        public CorrelationRow[]? Correlation { get; init; }
        public double[]? Quantiles { get; init; }
        public double? LoadingLimitPercent { get; init; }
        public VoltageRow? Voltage { get; init; }
        public int[]? MonitorBranches { get; init; }
        public int[]? MonitorBuses { get; init; }
        public bool? QFollowsP { get; init; }
        public double? StepMw { get; init; }
        // adequacy
        public UnitRow[]? Units { get; init; }
        public LoadRow? Load { get; init; }
    }

    public static int Run(List<string> arguments, string root, Func<string, GridModel> load)
    {
        if (!TryArguments(arguments, out string input, out string? output, out int threads))
        {
            Console.Error.WriteLine(Usage);
            return CliExit.Usage;
        }
        string? temporary = null;
        try
        {
            string studyPath = Path.GetFullPath(input);
            string? outPath = output is null ? null : Path.GetFullPath(output);
            if (outPath is not null && (File.Exists(outPath) || Directory.Exists(outPath)))
                throw new IOException("Result path already exists; use a new path for this invocation.");
            byte[] bytes = File.ReadAllBytes(studyPath);
            using JsonDocument document = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes));
            TransmissionSecurityCommand.CheckDuplicates(document.RootElement);
            Request req = JsonSerializer.Deserialize<Request>(GridSim.Core.IO.Utf8Json.WithoutBom(bytes).Span, TransmissionSecurityCommand.Json) ?? throw new InvalidDataException("Study is null.");
            if (req.SchemaVersion != 1) throw new InvalidDataException("schemaVersion must be 1.");
            if (req.Methods is null || req.Methods.Length == 0) throw new InvalidDataException("At least one method is required.");
            string domain = req.Domain ?? req.Study;
            var methodResults = new List<object>();
            bool allComplete = true;
            object? provenance;
            if (req.Study == "load-flow")
            {
                RequireNone(req.Units is null && req.Load is null && req.StepMw is null, "units, load and stepMw belong to an adequacy study");
                (GridModel model, object caseProvenance) = LoadCase(req, studyPath, root, load);
                provenance = caseProvenance;
                var inputs = BuildInputs(req);
                var corr = req.Correlation?.Select(c => new InputCorrelation(c.A, c.B, c.Rho)).ToArray();
                PlfEvaluator ev = req.Evaluator switch
                {
                    "dc" => PlfEvaluator.Dc, "ac" => PlfEvaluator.Ac,
                    _ => throw new InvalidDataException("evaluator must be dc or ac."),
                };
                foreach (MethodRow m in req.Methods)
                {
                    ProbabilisticMethod method = ParseMethod(m.Method);
                    var plf = new PlfRequest(inputs, corr, ev, method, m.Samples, m.Replicates, req.Seed, domain, req.Quantiles,
                        req.LoadingLimitPercent ?? 100.0, req.Voltage?.MinPu ?? 0.95, req.Voltage?.MaxPu ?? 1.05, req.MonitorBranches,
                        req.MonitorBuses, m.CellsPerSd ?? 100, req.StepMw, req.QFollowsP ?? true, threads);
                    PlfResult r = ProbabilisticLoadFlow.Run(model, plf);
                    if (r.Status != ProbabilisticStatus.Complete) allComplete = false;
                    methodResults.Add(new { request = m, result = r });
                }
            }
            else if (req.Study == "adequacy")
            {
                RequireNone(req.Case is null && req.Evaluator is null && req.Inputs is null && req.Correlation is null && req.Quantiles is null
                    && req.LoadingLimitPercent is null && req.Voltage is null && req.MonitorBranches is null && req.MonitorBuses is null && req.QFollowsP is null,
                    "case, evaluator, inputs, correlation, quantiles, limits and monitors belong to a load-flow study");
                provenance = new { caseSha256 = (string?)null, note = "adequacy reads no case" };
                var units = BuildUnits(req);
                double[] loads = BuildLoad(req);
                foreach (MethodRow m in req.Methods)
                {
                    ProbabilisticMethod method = ParseMethod(m.Method);
                    var a = new AdequacyRequest(units, loads, method, m.Samples, m.Replicates, req.Seed, domain, req.StepMw, threads);
                    AdequacyResult r = GenerationAdequacy.Run(a);
                    if (r.Status != ProbabilisticStatus.Complete) allComplete = false;
                    methodResults.Add(new { request = m, result = r });
                }
            }
            else throw new InvalidDataException("study must be load-flow or adequacy.");

            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                study = req.Study,
                provenance = new { studySha256 = TransmissionSecurityCommand.Hash(bytes), seed = req.Seed, domain, input = provenance,
                    engine = GridSim.Core.BuildInfo.InformationalVersion },
                status = allComplete ? "COMPLETE" : "INCOMPLETE",
                methods = methodResults,
            }, TransmissionSecurityCommand.OutputJson);
            if (outPath is not null)
            {
                temporary = outPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
                File.Move(temporary, outPath, overwrite: false);
                temporary = null;
            }
            CliResultFromJson.Fill(json);
            Console.WriteLine(json);
            return allComplete ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine("probabilistic: " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool TryArguments(List<string> a, out string input, out string? output, out int threads)
    {
        input = ""; output = null; threads = 0;
        if (a.Count < 2 || a[1].StartsWith('-')) return false;
        input = a[1];
        for (int i = 2; i < a.Count; i++)
        {
            if (a[i] == "--out" && i + 1 < a.Count && !a[i + 1].StartsWith('-') && output is null) output = a[++i];
            else if (a[i] == "--threads" && i + 1 < a.Count && int.TryParse(a[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int t) && t >= 1 && threads == 0) { threads = t; i++; }
            else return false;
        }
        return true;
    }

    private static void RequireNone(bool ok, string what)
    {
        if (!ok) throw new InvalidDataException("Field not valid for this study kind: " + what + ".");
    }

    private static ProbabilisticMethod ParseMethod(string name) =>
        ProbabilisticMethods.Parse(name) ?? throw new InvalidDataException($"Unknown method '{name}': use monte-carlo, qmc, qmc-shifted or convolution.");

    private static (GridModel, object) LoadCase(Request req, string studyPath, string root, Func<string, GridModel> load)
    {
        CaseRow c = req.Case ?? throw new InvalidDataException("A load-flow study needs a case.");
        if ((c.Path is null) == (c.BuiltIn is null)) throw new InvalidDataException("Case must specify exactly one path or builtIn.");
        if (string.IsNullOrWhiteSpace(c.Sha256)) throw new InvalidDataException("case.sha256 is required.");
        if (c.Path is string relative)
        {
            string path = Path.GetFullPath(relative, Path.GetDirectoryName(studyPath)!);
            var files = TransmissionSecurityCommand.Inputs(path, root);
            bool native = Directory.Exists(path) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            string identity = native
                ? TransmissionSecurityCommand.Hash(Encoding.UTF8.GetBytes(string.Concat(files.Select(f => f.Path + "\t" + f.Sha256 + "\n"))))
                : files.Single().Sha256;
            TransmissionSecurityCommand.RequireIdentity(c.Sha256, identity);
            GridModel model = native ? Gated.TransmissionNative(path, root) : load(path);
            if (!files.SequenceEqual(TransmissionSecurityCommand.Inputs(path, root)))
                throw new IOException("Case inputs changed during loading.");
            return (model, new { caseSha256 = identity, inputs = files, loadPolicy = native ? "Native dispatch suppressed" : "Incumbent file loader" });
        }
        string name = c.BuiltIn!;
        if (!BuiltInCases.Names.Contains(name.ToLowerInvariant())) throw new UnknownCaseException(name);
        GridModel built = load(name);
        string id = TransmissionSecurityCommand.Hash(Encoding.UTF8.GetBytes(GridSim.Core.BuildInfo.InformationalVersion + "\n" +
            JsonSerializer.Serialize(built, TransmissionSecurityCommand.ModelJson)));
        TransmissionSecurityCommand.RequireIdentity(c.Sha256, id);
        return (built, new { caseSha256 = id, loadPolicy = "Incumbent built-in loader" });
    }

    private static UncertainInput[] BuildInputs(Request req)
    {
        if (req.Inputs is null || req.Inputs.Length == 0) throw new InvalidDataException("A load-flow study needs inputs.");
        return req.Inputs.Select(i =>
        {
            UncertainTarget t = i.Target switch
            {
                "bus-load" => UncertainTarget.BusLoad,
                "generator-pg" => UncertainTarget.GeneratorPg,
                _ => throw new InvalidDataException("input target must be bus-load or generator-pg."),
            };
            if (t == UncertainTarget.BusLoad && i.Unit.HasValue) throw new InvalidDataException($"Input '{i.Id}': unit applies to generator-pg only.");
            return new UncertainInput(i.Id, t, i.Bus, i.Unit ?? 0, BuildDistribution(i.Distribution), i.Scale ?? false);
        }).ToArray();
    }

    private static UncertainDistribution BuildDistribution(DistributionInput d)
    {
        void Only(params string[] names)
        {
            var given = new Dictionary<string, bool>
            {
                ["mean"] = d.Mean.HasValue, ["sd"] = d.Sd.HasValue, ["mu"] = d.Mu.HasValue, ["sigma"] = d.Sigma.HasValue,
                ["scale"] = d.Scale.HasValue, ["shape"] = d.Shape.HasValue, ["alpha"] = d.Alpha.HasValue, ["beta"] = d.Beta.HasValue,
                ["lo"] = d.Lo.HasValue, ["hi"] = d.Hi.HasValue, ["capacity"] = d.Capacity.HasValue, ["availability"] = d.Availability.HasValue,
                ["atoms"] = d.Atoms is not null, ["values"] = d.Values is not null,
            };
            foreach ((string k, bool present) in given)
                if (present && !names.Contains(k)) throw new InvalidDataException($"Distribution '{d.Kind}' does not take '{k}'.");
        }
        double Need(double? v, string n) => v ?? throw new InvalidDataException($"Distribution '{d.Kind}' needs '{n}'.");
        switch (d.Kind)
        {
            case "normal": Only("mean", "sd"); return new NormalDistribution(Need(d.Mean, "mean"), Need(d.Sd, "sd"));
            case "lognormal": Only("mu", "sigma"); return new LogNormalDistribution(Need(d.Mu, "mu"), Need(d.Sigma, "sigma"));
            case "weibull": Only("scale", "shape"); return new WeibullDistribution(Need(d.Scale, "scale"), Need(d.Shape, "shape"));
            case "beta": Only("alpha", "beta", "lo", "hi"); return new BetaDistribution(Need(d.Alpha, "alpha"), Need(d.Beta, "beta"), d.Lo ?? 0.0, d.Hi ?? 1.0);
            case "two-state": Only("capacity", "availability"); return new TwoStateDistribution(Need(d.Capacity, "capacity"), Need(d.Availability, "availability"));
            case "discrete":
                Only("atoms");
                return new DiscreteDistribution((d.Atoms ?? throw new InvalidDataException("Distribution 'discrete' needs 'atoms'.")).Select(a => (a.Value, a.Probability)));
            case "empirical":
                Only("values");
                return new EmpiricalDistribution(d.Values ?? throw new InvalidDataException("Distribution 'empirical' needs 'values'."));
            default: throw new InvalidDataException($"Unknown distribution kind '{d.Kind}'.");
        }
    }

    private static AdequacyUnit[] BuildUnits(Request req)
    {
        if (req.Units is null || req.Units.Length == 0) throw new InvalidDataException("An adequacy study needs units.");
        return req.Units.Select(u =>
        {
            bool two = u.CapacityMw.HasValue || u.Availability.HasValue;
            if (two == (u.States is not null)) throw new InvalidDataException($"Unit '{u.Id}': give capacityMw and availability, or states, not both or neither.");
            if (two)
                return new AdequacyUnit(u.Id, new TwoStateDistribution(
                    u.CapacityMw ?? throw new InvalidDataException($"Unit '{u.Id}' needs capacityMw."),
                    u.Availability ?? throw new InvalidDataException($"Unit '{u.Id}' needs availability.")));
            return new AdequacyUnit(u.Id, new DiscreteDistribution(u.States!.Select(s => (s.CapacityMw, s.Probability))));
        }).ToArray();
    }

    private static double[] BuildLoad(Request req)
    {
        LoadRow l = req.Load ?? throw new InvalidDataException("An adequacy study needs a load.");
        if (l.HourlyMw is not null)
        {
            if (l.ConstantMw.HasValue || l.Hours.HasValue) throw new InvalidDataException("load: give hourlyMw, or constantMw with hours, not both.");
            return l.HourlyMw;
        }
        if (!l.ConstantMw.HasValue || !l.Hours.HasValue || l.Hours.Value < 1) throw new InvalidDataException("load: give hourlyMw, or constantMw with hours (at least 1).");
        return Enumerable.Repeat(l.ConstantMw.Value, l.Hours.Value).ToArray();
    }
}
