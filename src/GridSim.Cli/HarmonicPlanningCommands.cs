using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GridSim.Core.Harmonics;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Harmonics.Planning.Flicker;
using GridSim.Core.Model;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-21: the UK power-quality and connection-assessment verbs. <c>harmonic-assess</c> (ENA EREC G5 Issue 5 staged assessment,
/// with an optional Stage 3 on a bound case), <c>flicker</c> (ENA EREC P28 Issue 2 flicker and rapid voltage change, and the IEC 61000-4-15
/// flickermeter structure) and <c>filter-design</c> (single-tuned, high-pass and C-type filters). Each reads a strict v1 study file,
/// prints the result JSON and, with <c>--out</c>, writes it to a new file only (temporary file, then a move). The existing
/// <c>harmonics</c> verb is unchanged. The result of <c>harmonic-assess</c> is reported through the R4-10 generator
/// (<c>gridsim report</c>, template from <see cref="ConnectionAssessmentReport.TemplateJson"/> via <c>--report-template</c>).
/// </summary>
internal static class HarmonicPlanningCommands
{
    private static readonly JsonSerializerOptions Json = TransmissionSecurityCommand.Json;

    // ---------------------------------------------------------------- shared plumbing

    private static bool TryArguments(List<string> a, string usage, out string input, out string? output, out bool template)
    {
        input = ""; output = null; template = false;
        if (a.Count < 2) return false;
        int i = 1;
        if (!a[1].StartsWith('-')) { input = a[1]; i = 2; }
        for (; i < a.Count; i++)
        {
            if (a[i] == "--out" && i + 1 < a.Count && !a[i + 1].StartsWith('-') && output is null) output = a[++i];
            else if (a[i] == "--report-template" && !template) template = true;
            else return false;
        }
        _ = usage;
        return template ? input.Length == 0 && output is not null : input.Length > 0;
    }

    private static int Finish(string verb, string json, string? outPath, ref string? temporary, int exit)
    {
        if (outPath is not null)
        {
            temporary = outPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
            File.Move(temporary, outPath, overwrite: false);
            temporary = null;
        }
        CliResultFromJson.Fill(json);
        Console.WriteLine(json);
        return exit;
    }

    private static T Read<T>(string studyPath, string verb, out byte[] bytes)
    {
        bytes = File.ReadAllBytes(studyPath);
        using (JsonDocument document = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes))) TransmissionSecurityCommand.CheckDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(GridSim.Core.IO.Utf8Json.WithoutBom(bytes).Span, Json) ?? throw new InvalidDataException("Study is null.");
    }

    private static string? OutPath(string? output)
    {
        string? full = output is null ? null : Path.GetFullPath(output);
        if (full is not null && (File.Exists(full) || Directory.Exists(full)))
            throw new IOException("Result path already exists; use a new path for this invocation.");
        return full;
    }

    private static IReadOnlyDictionary<int, double>? Orders(Dictionary<string, double>? map, string what)
    {
        if (map is null) return null;
        var d = new SortedDictionary<int, double>();
        foreach ((string k, double v) in map)
        {
            if (!int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out int h) || h < 2)
                throw new InvalidDataException($"{what}: '{k}' is not a harmonic order of 2 or more.");
            if (!double.IsFinite(v) || v < 0.0) throw new InvalidDataException($"{what} at order {h} must be finite and not negative.");
            d[h] = v;
        }
        return d;
    }

    private static object Z(Complex z) => new { re = z.Real, im = z.Imaginary, abs = z.Magnitude };

    private sealed record CaseRow { public string? Path { get; init; } public string? BuiltIn { get; init; } public required string Sha256 { get; init; } }

    private static (GridModel, object) LoadCase(CaseRow c, string studyPath, string root, Func<string, GridModel> load)
    {
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

    private static readonly Type[] UserErrors = [typeof(InvalidDataException), typeof(IOException), typeof(UnauthorizedAccessException), typeof(JsonException),
        typeof(ArgumentException), typeof(InvalidOperationException), typeof(KeyNotFoundException)];

    private static bool IsUserError(Exception ex) => UserErrors.Any(t => t.IsInstanceOfType(ex));

    // ---------------------------------------------------------------- harmonic-assess

    private sealed record PccRow { public required double LineKv { get; init; } public required double SscMva { get; init; } public double? SscSinglePhaseMva { get; init; } public required double XOverR { get; init; } }
    private sealed record EquipmentRow { public required string Id { get; init; } public required string Technology { get; init; } public required double RatingKva { get; init; } }
    private sealed record BackgroundRow { public required string Mode { get; init; } public Dictionary<string, double>? Pct { get; init; } public bool? MissingOrdersAreZero { get; init; } }
    private sealed record ResonantRow { public required double SscXMva { get; init; } public required double QrpMvar { get; init; } public required double PdMw { get; init; } }
    private sealed record SpectrumRow { public required int Order { get; init; } public required double Pct { get; init; } public double? PhaseDeg { get; init; } }
    private sealed record SourceRow
    {
        public required int Bus { get; init; }
        public string? Type { get; init; }
        public SpectrumRow[]? Spectrum { get; init; }
        public double? CurrentA { get; init; }
        public double? FundamentalCurrentPu { get; init; }
    }
    private sealed record OptionsRow { public int[]? Orders { get; init; } public bool? Balanced { get; init; } public bool? LoadShunts { get; init; } public bool? GeneratorSources { get; init; } }
    private sealed record RemoteRow { public required int Bus { get; init; } public required Dictionary<string, double> Pct { get; init; } }
    private sealed record Stage3Row
    {
        public required CaseRow Case { get; init; }
        public required int PccBus { get; init; }
        public required SourceRow[] Sources { get; init; }
        public OptionsRow? Options { get; init; }
        public required Dictionary<string, double> PccBackgroundPct { get; init; }
        public RemoteRow[]? RemoteBackground { get; init; }
        public double? AssumedRemoteBackgroundFraction { get; init; }
        public double? MvaCapacity { get; init; }
        public double? TransferThreshold { get; init; }
        public bool? MissingBackgroundIsZero { get; init; }
    }
    private sealed record AssessRequest
    {
        public required int SchemaVersion { get; init; }
        public required string Study { get; init; }
        public required PccRow Pcc { get; init; }
        public required EquipmentRow[] Equipment { get; init; }
        public bool? DeclaredCompliantIec61000_3_2 { get; init; }
        public BackgroundRow? Background { get; init; }
        public Dictionary<string, double>? EmissionPct { get; init; }
        public bool? DiversityDeclared { get; init; }
        public int? MaxOrder { get; init; }
        public ResonantRow? ResonantPlant { get; init; }
        public Stage3Row? Stage3 { get; init; }
    }

    private static G55Technology Technology(string s) => s switch
    {
        "six-pulse" => G55Technology.SixPulse,
        "active-front-end" => G55Technology.ActiveFrontEnd,
        "twelve-pulse" => G55Technology.TwelvePulse,
        "single-phase-rectifier" => G55Technology.SinglePhaseRectifier,
        _ => throw new InvalidDataException("technology must be six-pulse, active-front-end, twelve-pulse or single-phase-rectifier."),
    };

    public static int RunAssess(List<string> arguments, string root, Func<string, GridModel> load)
    {
        const string Verb = "harmonic-assess";
        const string Usage = "usage: gridsim harmonic-assess <study.json> [--out <result.json>]\n       gridsim harmonic-assess --report-template --out <template.json>";
        if (!TryArguments(arguments, Usage, out string input, out string? output, out bool templateOnly))
        {
            Console.Error.WriteLine(Usage);
            return CliExit.Usage;
        }
        string? temporary = null;
        try
        {
            string? outPath = OutPath(output);
            if (templateOnly)
            {
                File.WriteAllText(outPath!, ConnectionAssessmentReport.TemplateJson() + "\n", new UTF8Encoding(false));
                Console.WriteLine("harmonic-assess: wrote the report template " + outPath);
                return CliExit.Ok;
            }
            string studyPath = Path.GetFullPath(input);
            AssessRequest req = Read<AssessRequest>(studyPath, Verb, out byte[] bytes);
            if (req.SchemaVersion != 1) throw new InvalidDataException("schemaVersion must be 1.");
            if (req.Study != Verb) throw new InvalidDataException($"study must be \"{Verb}\".");
            if (req.Equipment.Length == 0) throw new InvalidDataException("equipment must list at least one item.");
            IReadOnlyDictionary<int, double>? measured = null;
            bool assumed = false, zeroMissing = false;
            if (req.Background is not null)
            {
                switch (req.Background.Mode)
                {
                    case "measured":
                        measured = Orders(req.Background.Pct ?? throw new InvalidDataException("background.pct is required for mode measured."), "background.pct");
                        zeroMissing = req.Background.MissingOrdersAreZero ?? false;
                        break;
                    case "assumed-75":
                        if (req.Background.Pct is not null) throw new InvalidDataException("background.pct belongs to mode measured.");
                        assumed = true;
                        break;
                    default: throw new InvalidDataException("background.mode must be measured or assumed-75.");
                }
            }
            var request = new ConnectionAssessmentRequest(
                req.Pcc.LineKv, req.Pcc.SscMva, req.Pcc.SscSinglePhaseMva, req.Pcc.XOverR,
                req.Equipment.Select(e => new G55Equipment(e.Id, Technology(e.Technology), e.RatingKva)).ToArray(),
                req.DeclaredCompliantIec61000_3_2 ?? false, measured, assumed, Orders(req.EmissionPct, "emissionPct"),
                req.DiversityDeclared ?? false, req.MaxOrder ?? 100, zeroMissing,
                req.ResonantPlant is null ? null : new ResonantPlantInput(req.ResonantPlant.SscXMva, req.ResonantPlant.QrpMvar, req.ResonantPlant.PdMw));
            ConnectionAssessmentResult result = ConnectionAssessmentRunner.Run(request);

            object? caseProvenance = null;
            if (req.Stage3 is not null)
            {
                (GridModel model, object prov) = LoadCase(req.Stage3.Case, studyPath, root, load);
                caseProvenance = prov;
                G55Stage3Result s3 = G55Stage3.Assess(BuildStage3(req.Stage3, model, req.DiversityDeclared ?? false));
                string verdict = result.Verdict;
                string route = result.Route;
                if (verdict == ConnectionAssessmentRunner.Stage3Required)
                {
                    route = "Stage 3 (clause 10)";
                    verdict = s3.Outcome == G55Outcome.Pass ? ConnectionAssessmentRunner.Accepted
                        : s3.Outcome == G55Outcome.Incomplete ? ConnectionAssessmentRunner.Incomplete : ConnectionAssessmentRunner.NoConnection;
                }
                result = result with { Stage3 = s3, Verdict = verdict, Route = route };
            }

            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                format = ConnectionAssessmentRunner.Format,
                study = Verb,
                provenance = new
                {
                    studySha256 = TransmissionSecurityCommand.Hash(bytes),
                    @case = caseProvenance,
                    standard = "ENA Engineering Recommendation G5 Issue 5 (2020), docs/reference S139",
                    engine = GridSim.Core.BuildInfo.InformationalVersion,
                },
                result,
            }, TransmissionSecurityCommand.OutputJson);
            return Finish(Verb, json, outPath, ref temporary, result.Verdict == ConnectionAssessmentRunner.Accepted ? CliExit.Ok : CliExit.Verdict);
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (IsUserError(ex))
        {
            Console.Error.WriteLine(Verb + ": " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }

    private static G55Stage3Request BuildStage3(Stage3Row s, GridModel model, bool diversity)
    {
        var sources = new List<HarmonicSource>();
        foreach (SourceRow src in s.Sources)
        {
            Bus bus = model.Buses.FirstOrDefault(b => b.Id == src.Bus) ?? throw new InvalidDataException($"Source bus {src.Bus} is not in the case.");
            if ((src.Type is null) == (src.Spectrum is null)) throw new InvalidDataException($"Source at bus {src.Bus}: give exactly one of type and spectrum.");
            if ((src.CurrentA is null) == (src.FundamentalCurrentPu is null)) throw new InvalidDataException($"Source at bus {src.Bus}: give exactly one of currentA and fundamentalCurrentPu.");
            double pu;
            if (src.CurrentA is double amps)
            {
                if (!(bus.BaseKv > 0.0)) throw new InvalidDataException($"Source bus {src.Bus} has no nominal voltage, so a current in amps cannot be converted to per unit.");
                pu = amps / (model.BaseMva * 1000.0 / (Math.Sqrt(3.0) * bus.BaseKv));
            }
            else pu = src.FundamentalCurrentPu!.Value;
            IReadOnlyList<HarmonicComponent> spectrum = src.Spectrum is null
                ? (HarmonicSourceLibrary.ByType.TryGetValue(src.Type!, out var lib) ? lib : throw new InvalidDataException($"Unknown source type '{src.Type}'."))
                : src.Spectrum.Select(c => new HarmonicComponent(c.Order, c.Pct, c.PhaseDeg ?? 0.0)).ToArray();
            sources.Add(new HarmonicSource(src.Bus, src.Type ?? "explicit", pu, spectrum));
        }
        HarmonicModelOptions opt = new()
        {
            IncludeGeneratorSources = s.Options?.GeneratorSources ?? true,
            IncludeLoadShunts = s.Options?.LoadShunts ?? true,
            BalancedHarmonicSequences = s.Options?.Balanced ?? true,
        };
        if (s.Options?.Orders is int[] o) opt = opt with { Orders = o };
        Dictionary<int, IReadOnlyDictionary<int, double>>? remote = null;
        if (s.RemoteBackground is not null)
        {
            remote = [];
            foreach (RemoteRow r in s.RemoteBackground) remote[r.Bus] = Orders(r.Pct, $"remoteBackground[{r.Bus}]")!;
        }
        return new G55Stage3Request(model, s.PccBus, sources, opt, Orders(s.PccBackgroundPct, "pccBackgroundPct")!, remote,
            s.AssumedRemoteBackgroundFraction, diversity, s.MvaCapacity, s.TransferThreshold ?? 0.0, null, s.MissingBackgroundIsZero ?? false);
    }

    // ---------------------------------------------------------------- flicker

    private sealed record SourceFlicker { public required string Id { get; init; } public required double Pst { get; init; } public double? Plt { get; init; } }
    private sealed record TransferRow { public required double FromKv { get; init; } public double? Coefficient { get; init; } }
    private sealed record StepRow { public required string Id { get; init; } public double? DeltaSMva { get; init; } public double? SscMva { get; init; } public double? StepPct { get; init; } public required double IntervalSeconds { get; init; } }
    private sealed record InrushRow { public required string Id { get; init; } public required double M { get; init; } public required double K { get; init; } public required double RatedMva { get; init; } public required double SscMva { get; init; } }
    private sealed record SampleRow { public required double TimeS { get; init; } public required double DeviationPct { get; init; } }
    private sealed record RvcRow { public required string Id { get; init; } public required int Category { get; init; } public required SampleRow[] Samples { get; init; } public required double SteadyStateChangePct { get; init; } }
    private sealed record Stage1Row
    {
        public required double RatedCurrentA { get; init; } public required bool ThreePhase { get; init; } public required bool ConformsEn61000_3_3 { get; init; }
        public required bool ConformsEn61000_3_11 { get; init; } public required double SupplyImpedanceOhm { get; init; } public double? EquipmentZmaxOhm { get; init; }
        public required bool ServiceCapacityConfirmedAdequate { get; init; } public required bool ServiceCapacityAtLeast100A { get; init; }
    }
    private sealed record CurvePoint { public required double IntervalSeconds { get; init; } public required double StepPct { get; init; } }
    private sealed record SignalRow { public required double DurationSeconds { get; init; } public required double CarrierHz { get; init; } public required string Modulation { get; init; } public required double ModulationHz { get; init; } public required double DepthPct { get; init; } public double? SettleSeconds { get; init; } }
    private sealed record MeterRow
    {
        public FlickermeterParameters? Parameters { get; init; }
        public SignalRow? Signal { get; init; }
    }
    private sealed record FlickerRequest
    {
        public required int SchemaVersion { get; init; }
        public required string Study { get; init; }
        public required double NominalKv { get; init; }
        public SourceFlicker[]? Sources { get; init; }
        public SourceFlicker[]? Background { get; init; }
        public TransferRow? Transfer { get; init; }
        public double? Exponent { get; init; }
        public StepRow[]? Steps { get; init; }
        public CurvePoint[]? Pst05Curve { get; init; }
        public InrushRow[]? Inrush { get; init; }
        public RvcRow[]? Rvc { get; init; }
        public Stage1Row? Stage1 { get; init; }
        public MeterRow? Flickermeter { get; init; }
    }

    public static int RunFlicker(List<string> arguments)
    {
        const string Verb = "flicker";
        const string Usage = "usage: gridsim flicker <study.json> [--out <result.json>]";
        if (!TryArguments(arguments, Usage, out string input, out string? output, out bool templ) || templ)
        {
            Console.Error.WriteLine(Usage);
            return CliExit.Usage;
        }
        string? temporary = null;
        try
        {
            string? outPath = OutPath(output);
            FlickerRequest req = Read<FlickerRequest>(Path.GetFullPath(input), Verb, out byte[] bytes);
            if (req.SchemaVersion != 1) throw new InvalidDataException("schemaVersion must be 1.");
            if (req.Study != Verb) throw new InvalidDataException($"study must be \"{Verb}\".");
            P28PlanningLevels levels = P28Assessment.PlanningLevels(req.NominalKv);
            double alpha = req.Exponent ?? P28Assessment.DefaultExponent;
            bool fail = false, incomplete = false;

            object? planning = null;
            if (req.Sources is { Length: > 0 } || req.Background is { Length: > 0 })
            {
                double transfer = 1.0;
                if (req.Transfer is not null)
                    transfer = req.Transfer.Coefficient ?? P28Assessment.TransferCoefficient(req.Transfer.FromKv, req.NominalKv)
                        ?? throw new InvalidDataException("Table 3 has no coefficient for that pair of voltages: give transfer.coefficient.");
                var all = (req.Sources ?? []).Select(s => (s.Pst * transfer, (s.Plt ?? double.NaN) * transfer)).ToList();
                all.AddRange((req.Background ?? []).Select(s => (s.Pst, s.Plt ?? double.NaN)));
                bool havePlt = all.All(x => !double.IsNaN(x.Item2));
                var check = P28Assessment.PlanningCheck(req.NominalKv, all.Select(x => x.Item1).ToArray(), havePlt ? all.Select(x => x.Item2).ToArray() : [0.0], alpha);
                fail |= check.Pst.Outcome != G55Outcome.Pass || (havePlt && check.Plt.Outcome != G55Outcome.Pass);
                planning = new { transferCoefficientApplied = transfer, exponent = alpha, pst = check.Pst, plt = havePlt ? check.Plt : null, pltNote = havePlt ? null : "Plt not given for every source; Plt not checked." };
            }

            var steps = new List<object>();
            foreach (StepRow s in req.Steps ?? [])
            {
                double pct = s.StepPct ?? (s.DeltaSMva is double d && s.SscMva is double ssc ? P28Assessment.VoltageChangePct(d, ssc)
                    : throw new InvalidDataException($"Step '{s.Id}': give stepPct, or deltaSMva with sscMva."));
                P28Verdict v = P28Assessment.SimplifiedStep(pct, s.IntervalSeconds, req.Pst05Curve?.Select(p => (p.IntervalSeconds, p.StepPct)).ToArray());
                fail |= v.Outcome == "STAGE 3";
                incomplete |= v.Outcome == G55Outcome.Incomplete;
                steps.Add(new { s.Id, stepPct = pct, s.IntervalSeconds, verdict = v });
            }

            var inrush = new List<object>();
            foreach (InrushRow r in req.Inrush ?? [])
                inrush.Add(new { r.Id, voltageChangePct = P28Assessment.InrushVoltageChangePct(r.M, r.K, r.RatedMva, r.SscMva), note = "Annex C equation C.1: first step only; compare with the RVC envelopes." });

            var rvc = new List<object>();
            foreach (RvcRow r in req.Rvc ?? [])
            {
                if (r.Category is < 1 or > 3) throw new InvalidDataException($"RVC '{r.Id}': category must be 1, 2 or 3.");
                P28Verdict v = P28Assessment.RapidVoltageChange((P28RvcCategory)r.Category, r.Samples.Select(x => new P28Sample(x.TimeS, x.DeviationPct)).ToArray(), r.SteadyStateChangePct);
                fail |= v.Outcome == G55Outcome.Fail;
                rvc.Add(new { r.Id, r.Category, verdict = v });
            }

            P28Verdict? stage1 = req.Stage1 is null ? null : P28Assessment.Stage1Lv(req.Stage1.RatedCurrentA, req.Stage1.ThreePhase, req.Stage1.ConformsEn61000_3_3,
                req.Stage1.ConformsEn61000_3_11, req.Stage1.SupplyImpedanceOhm, req.Stage1.EquipmentZmaxOhm, req.Stage1.ServiceCapacityConfirmedAdequate, req.Stage1.ServiceCapacityAtLeast100A);

            object? meter = null;
            if (req.Flickermeter is not null)
            {
                if (req.Flickermeter.Parameters is null)
                    meter = new { status = Flickermeter.AwaitingStandardText, note = "No filter parameters were supplied and none are built in: the IEC 61000-4-15 coefficients are in the paid standard." };
                else
                {
                    SignalRow sg = req.Flickermeter.Signal ?? throw new InvalidDataException("flickermeter.signal is required with parameters.");
                    FlickermeterParameters p = req.Flickermeter.Parameters;
                    double[] u = Flickermeter.ModulatedVoltage(p.SampleRateHz, sg.DurationSeconds, p.NominalRmsVolts, sg.CarrierHz, sg.Modulation, sg.ModulationHz, sg.DepthPct / 100.0);
                    meter = Flickermeter.Run(p, u, sg.SettleSeconds ?? 0.0);
                }
            }

            string verdict = fail ? "NOT ACCEPTED" : incomplete ? "INCOMPLETE" : "ACCEPTED";
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                format = "gridsim-flicker/1",
                study = Verb,
                provenance = new
                {
                    studySha256 = TransmissionSecurityCommand.Hash(bytes),
                    standard = "ENA Engineering Recommendation P28 Issue 2 (2019), docs/reference S141; flickermeter structure of IEC 61000-4-15 (verification AWAITING STANDARD TEXT)",
                    engine = GridSim.Core.BuildInfo.InformationalVersion,
                },
                result = new { verdict, nominalKv = req.NominalKv, planningLevels = levels, planning, steps, inrush, rvc, stage1, flickermeter = meter },
            }, TransmissionSecurityCommand.OutputJson);
            return Finish(Verb, json, outPath, ref temporary, verdict == "ACCEPTED" ? CliExit.Ok : CliExit.Verdict);
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            Console.Error.WriteLine(Verb + ": " + CliExit.UserMessage(ex));
            return CliExit.InputError;
        }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }

    // ---------------------------------------------------------------- filter-design

    private sealed record FilterRow
    {
        public required string Id { get; init; } public required string Kind { get; init; } public required double VoltageKv { get; init; }
        public required double FundamentalMvar { get; init; } public required double TunedOrder { get; init; } public double? QualityFactor { get; init; }
        public double? FundamentalHz { get; init; }
    }
    private sealed record SourceRowNet { public required double SscMva { get; init; } public required double XOverR { get; init; } }
    private sealed record NetRow { public SourceRowNet? Source { get; init; } public CaseRow? Case { get; init; } public int? Bus { get; init; } }
    private sealed record SweepRow { public double? MinOrder { get; init; } public double? MaxOrder { get; init; } public double? Step { get; init; } }
    private sealed record FilterRequest
    {
        public required int SchemaVersion { get; init; }
        public required string Study { get; init; }
        public required FilterRow[] Filters { get; init; }
        public NetRow? Network { get; init; }
        public SweepRow? Sweep { get; init; }
    }

    public static int RunFilterDesign(List<string> arguments, string root, Func<string, GridModel> load)
    {
        const string Verb = "filter-design";
        const string Usage = "usage: gridsim filter-design <study.json> [--out <result.json>]";
        if (!TryArguments(arguments, Usage, out string input, out string? output, out bool templ) || templ)
        {
            Console.Error.WriteLine(Usage);
            return CliExit.Usage;
        }
        string? temporary = null;
        try
        {
            string? outPath = OutPath(output);
            string studyPath = Path.GetFullPath(input);
            FilterRequest req = Read<FilterRequest>(studyPath, Verb, out byte[] bytes);
            if (req.SchemaVersion != 1) throw new InvalidDataException("schemaVersion must be 1.");
            if (req.Study != Verb) throw new InvalidDataException($"study must be \"{Verb}\".");
            if (req.Filters.Length == 0) throw new InvalidDataException("filters must list at least one filter.");

            var designs = new List<(string Id, FilterDesignResult D)>();
            foreach (FilterRow f in req.Filters)
            {
                FilterKind kind = f.Kind switch
                {
                    "single-tuned" => FilterKind.SingleTuned,
                    "high-pass" => FilterKind.HighPass,
                    "c-type" => FilterKind.CType,
                    _ => throw new InvalidDataException("kind must be single-tuned, high-pass or c-type."),
                };
                designs.Add((f.Id, FilterDesign.Design(new FilterDesignRequest(kind, f.VoltageKv, f.FundamentalMvar, f.TunedOrder, f.QualityFactor, f.FundamentalHz ?? 50.0))));
            }

            SweepRow sw = req.Sweep ?? new SweepRow();
            double min = sw.MinOrder ?? 1.5, max = sw.MaxOrder ?? 50.0, step = sw.Step ?? 0.05;
            if (!(min > 0) || !(max > min) || !(step > 0) || (max - min) / step > 100000) throw new InvalidDataException("sweep needs 0 < minOrder < maxOrder and a step giving at most 100000 points.");
            int n = (int)Math.Floor((max - min) / step + 1e-9) + 1;
            var grid = new double[n];
            for (int i = 0; i < n; i++) grid[i] = min + i * step;

            object? network = null;
            object? caseProvenance = null;
            if (req.Network is not null)
            {
                Complex[] zNet;
                double baseMva;
                double kv = designs[0].D.VoltageKv;
                if (req.Network.Source is SourceRowNet src)
                {
                    if (req.Network.Case is not null) throw new InvalidDataException("network: give source or case, not both.");
                    baseMva = 100.0;
                    double z1 = kv * kv / src.SscMva / baseMva * baseMva;   // ohm
                    double r1 = z1 / Math.Sqrt(1.0 + src.XOverR * src.XOverR), x1 = r1 * src.XOverR;
                    zNet = grid.Select(h => new Complex(r1, h * x1) / (kv * kv / baseMva)).ToArray();
                }
                else
                {
                    CaseRow c = req.Network.Case ?? throw new InvalidDataException("network needs source or case.");
                    int bus = req.Network.Bus ?? throw new InvalidDataException("network.bus is required with a case.");
                    (GridModel model, object prov) = LoadCase(c, studyPath, root, load);
                    caseProvenance = prov;
                    baseMva = model.BaseMva;
                    FrequencySweepResult s = HarmonicFrequencySweep.DrivingPoint(model, bus, HarmonicModelOptions.Study,
                        new FrequencySweepOptions { MinOrder = min, MaxOrder = max, Step = step });
                    grid = s.Orders.ToArray();
                    zNet = s.PositiveZ;
                }
                var rows = new List<object>();
                foreach ((string id, FilterDesignResult d) in designs)
                {
                    Complex[] comb = FilterDesign.Combined(d, grid, zNet, baseMva);
                    (IReadOnlyList<double> peaks, IReadOnlyList<double> valleys) = FilterDesign.ExtremaOrders(grid, comb);
                    (IReadOnlyList<double> p0, IReadOnlyList<double> v0) = FilterDesign.ExtremaOrders(grid, zNet);
                    rows.Add(new
                    {
                        id,
                        parallelResonanceOrders = peaks,
                        seriesResonanceOrders = valleys,
                        networkAloneParallelResonanceOrders = p0,
                        combinedAtOrders = new[] { 3, 5, 7, 11, 13 }.Where(h => h >= grid[0] && h <= grid[^1]).Select(h =>
                        {
                            int k = (int)Math.Round((h - grid[0]) / (grid[1] - grid[0]));
                            return new { order = h, gridOrder = grid[k], zPu = Z(comb[k]), networkZPu = Z(zNet[k]) };
                        }).ToArray(),
                    });
                }
                network = new { baseMva, rows };
            }

            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                format = "gridsim-filter-design/1",
                study = Verb,
                provenance = new { studySha256 = TransmissionSecurityCommand.Hash(bytes), @case = caseProvenance, engine = GridSim.Core.BuildInfo.InformationalVersion },
                result = new
                {
                    verdict = "COMPLETE",
                    filters = designs.Select(x => new
                    {
                        x.Id, kind = x.D.Kind.ToString(), x.D.VoltageKv, x.D.TunedOrder, x.D.QualityFactor,
                        capacitorOhm = x.D.CapacitorOhm, capacitor2Ohm = x.D.Capacitor2Ohm, reactorOhm = x.D.ReactorOhm, resistorOhm = x.D.ResistorOhm,
                        capacitorMicrofarad = x.D.CapacitorMicrofarad, capacitor2Microfarad = x.D.Capacitor2Microfarad, reactorMillihenry = x.D.ReactorMillihenry,
                        x.D.FundamentalMvar, fundamentalMw = x.D.FundamentalMw, capacitorVoltageRatio = x.D.CapacitorVoltageRatio,
                        impedanceAtTunedOrderOhm = Z(x.D.ImpedanceOhm(x.D.TunedOrder)),
                    }).ToArray(),
                    network,
                },
            }, TransmissionSecurityCommand.OutputJson);
            return Finish(Verb, json, outPath, ref temporary, CliExit.Ok);
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (IsUserError(ex))
        {
            Console.Error.WriteLine(Verb + ": " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }
}
