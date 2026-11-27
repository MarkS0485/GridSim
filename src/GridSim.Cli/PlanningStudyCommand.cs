using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC3 P7: <c>gridsim planning-study &lt;manifest.json&gt; --out &lt;dir&gt; [--parallel N] [--max-jobs N]</c>.
/// Parses the strict gridsim-planning/1 manifest, loads and fingerprints the immutable master through the native
/// gated loader (dispatch suppressed), expands snapshots deterministically in Core, runs each job into its own folder
/// and writes one ordered result manifest. Timings and reuse decisions live in a separate evidence file so the
/// result manifest, summary and job results are byte-identical between an uninterrupted run and a retried one.
/// </summary>
internal static partial class PlanningStudyCommand
{
    private const string Marker = ".gridsim-planning-run";
    private const string MarkerText = "gridsim-planning-run/1\n";
    private const string RunManifest = "run-manifest.json";
    private const string Summary = "summary.txt";
    private const string Evidence = "evidence.json";
    private const string ResultFile = "result.json";
    private const string CompleteFile = "complete.json";

    private static readonly JsonSerializerOptions OutputJson = CreateOutput();
    private static readonly JsonSerializerOptions ModelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static JsonSerializerOptions CreateOutput()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        options.Converters.Add(new FiniteDoubleWriter());
        return options;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private sealed record JobRecord(int Ordinal, string Id, string Snapshot, string Study, string Mode, string State,
        string? Status, string? Outcome, string? Reason, string? Result, string? ResultSha256, string CaseSha256,
        string RequestFingerprint, string InputFingerprint);
    private sealed record Completion(string SchemaVersion, string JobId, string Build, string RequestFingerprint,
        string InputFingerprint, string ResultSha256, long ResultBytes, string Status, string Outcome, string? Reason);
    private sealed record JobTiming(string Id, bool Attempted, bool Reused, double? ElapsedMs, string? ReuseRefusal);

    public static int Run(List<string> arguments, string root)
    {
        if (!TryParseArguments(arguments, out string manifestArg, out string outArg, out int parallel, out int? maxJobs))
        {
            Console.Error.WriteLine("usage: gridsim planning-study <manifest.json> --out <dir> [--parallel N] [--max-jobs N]");
            return CliExit.Usage;
        }
        var clock = Stopwatch.StartNew();
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            string manifestPath = Path.GetFullPath(manifestArg);
            string outDir = Path.GetFullPath(outArg);
            byte[] manifestBytes = File.ReadAllBytes(manifestPath);
            string manifestSha = PlanningFingerprints.Sha256(manifestBytes);
            PlanningManifest manifest = Parse(manifestBytes);

            // 1. Resolve the master with the approved loader and check the complete input manifest.
            string casePath = Path.GetFullPath(manifest.Master.Path, Path.GetDirectoryName(manifestPath)!);
            if (!Directory.Exists(casePath) && !(File.Exists(casePath) && casePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("master.case.path must name a native JSON case directory or v2 case manifest: " + casePath);
            List<PlanningInputFile> inputs = Inputs(casePath, root);
            string bundle = PlanningFingerprints.Bundle(inputs);
            if (!string.Equals(bundle, manifest.Master.BundleSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Master case SHA256 mismatch. Resolved identity: " + bundle);
            GridModel model = Gated.TransmissionNative(casePath, root);
            if (!inputs.SequenceEqual(Inputs(casePath, root)))
                throw new IOException("Master inputs changed during loading.");
            var master = new PlanningMaster(model, bundle, inputs, manifest.Master.Interpretation);

            // 2-3. Deterministic expansion; any conflict or invalid operation refuses the whole run before a job runs.
            PlanningExpansionResult expansion = PlanningExpansion.Expand(master, manifest, CaseSha256);

            PrepareOutput(outDir);
            string build = BuildFingerprint();
            int limit = maxJobs ?? int.MaxValue;
            var records = new JobRecord[expansion.Jobs.Count];
            var timings = new JobTiming[expansion.Jobs.Count];
            var options = new ParallelOptions { MaxDegreeOfParallelism = parallel };
            // 4-5. Independent jobs, bounded concurrency, one folder each, results stored by ordinal.
            Parallel.For(0, expansion.Jobs.Count, options, i =>
            {
                PlanningJob job = expansion.Jobs[i];
                if (i >= limit || cancel.IsCancellationRequested)
                {
                    records[i] = Record(job, "NOT ATTEMPTED", null, null, null, null);
                    timings[i] = new JobTiming(job.Id, false, false, null, null);
                    return;
                }
                (records[i], timings[i]) = Execute(job, outDir, master, build);
            });

            if (!inputs.SequenceEqual(Inputs(casePath, root)) || PlanningFingerprints.Sha256(File.ReadAllBytes(manifestPath)) != manifestSha)
                throw new IOException("Master inputs or the manifest changed during the run; no result manifest was written.");

            string state = records.Any(r => r.State == "WRITE FAILED") ? "INCOMPLETE"
                : records.Any(r => r.State == "NOT ATTEMPTED") ? "CANCELLED" : "COMPLETE";
            string runManifest = JsonSerializer.Serialize(new
            {
                schemaVersion = PlanningManifestVersion.Result,
                state,
                manifestVersion = PlanningManifestVersion.Current,
                manifestSha256 = manifestSha,
                build,
                master = new
                {
                    path = manifest.Master.Path,
                    bundleSha256 = bundle,
                    interpretation = manifest.Master.Interpretation,
                    masterSha256 = expansion.MasterSha256,
                    caseSha256 = expansion.MasterCaseSha256,
                    inputs
                },
                projectOrder = manifest.ProjectOrder,
                snapshots = expansion.Cases.Select(c => new
                {
                    id = c.Snapshot.Id,
                    instantUtc = PlanningTime.Format(c.Snapshot.Instant),
                    caseSha256 = c.CaseSha256,
                    derivationSha256 = c.DerivationSha256,
                    appliedOperations = c.AppliedOperations,
                    valid = c.Validation.Valid
                }),
                jobs = records,
                counts = new
                {
                    jobs = records.Length,
                    completed = records.Count(r => r.State == "COMPLETED"),
                    notAttempted = records.Count(r => r.State == "NOT ATTEMPTED"),
                    writeFailed = records.Count(r => r.State == "WRITE FAILED"),
                    passed = records.Count(Passed),
                    notPassed = records.Count(r => PlanningStudyModes.IsVerdict(r.Status) && !Passed(r)),
                    invalidCase = records.Count(r => r.Status == PlanningStudyModes.InvalidCase),
                    failed = records.Count(r => r.Status == PlanningStudyModes.Failed)
                }
            }, OutputJson) + "\n";
            string summary = SummaryText(manifestSha, expansion, manifest, records, state);
            // The evidence file first and the result manifest last: a run manifest present means the run finished.
            WriteNew(Path.Combine(outDir, Evidence), JsonSerializer.Serialize(new
            {
                note = "Execution evidence only: elapsed times and reuse decisions vary between runs and are not study results.",
                parallel,
                maxJobs,
                totalElapsedMs = clock.Elapsed.TotalMilliseconds,
                jobs = timings
            }, OutputJson) + "\n");
            WriteNew(Path.Combine(outDir, Summary), summary);
            WriteNew(Path.Combine(outDir, RunManifest), runManifest);
            Console.Write(summary);
            if (state == "INCOMPLETE") return CliExit.InputError;
            return state == "COMPLETE" && records.All(Passed) ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is PlanningManifestException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine("planning-study: " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>A job passes when its status is its own engine's pass (SECURE, or VERIFIED for transfer).</summary>
    private static bool Passed(JobRecord r) => r.State == "COMPLETED" && PlanningStudyModes.Passed(r.Mode, r.Status);

    private static bool TryParseArguments(List<string> a, out string manifest, out string outDir, out int parallel, out int? maxJobs)
    {
        manifest = outDir = "";
        parallel = 1;
        maxJobs = null;
        if (a.Count < 2 || a[1].StartsWith('-')) return false;
        manifest = a[1];
        string? o = null;
        for (int i = 2; i < a.Count; i += 2)
        {
            if (i + 1 >= a.Count || a[i + 1].StartsWith('-')) return false;
            string value = a[i + 1];
            switch (a[i])
            {
                case "--out" when o is null: o = value; break;
                case "--parallel" when parallel == 1 && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p is >= 1 and <= 64:
                    parallel = p; break;
                case "--max-jobs" when maxJobs is null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int m) && m >= 0:
                    maxJobs = m; break;
                default: return false;
            }
        }
        if (o is null) return false;
        outDir = o;
        return true;
    }

    // ---- Job execution and reuse -----------------------------------------------------------------------------------

    private static (JobRecord, JobTiming) Execute(PlanningJob job, string outDir, PlanningMaster master, string build,
        Func<PlanningJob, PlanningJobResult>? runner = null)
    {
        string folder = Path.Combine(outDir, "jobs", job.Folder);
        string resultPath = Path.Combine(folder, ResultFile);
        string completePath = Path.Combine(folder, CompleteFile);
        string relative = "jobs/" + job.Folder + "/" + ResultFile;
        var watch = Stopwatch.StartNew();
        string? refusal = ReuseRefusal(job, build, resultPath, completePath, out Completion? reusable);
        if (reusable is not null)
            return (Record(job, "COMPLETED", reusable.Status, reusable.Outcome, reusable.Reason, relative, reusable.ResultSha256),
                new JobTiming(job.Id, true, true, watch.Elapsed.TotalMilliseconds, null));

        // Not reusable: remove this job's earlier files first, so a failure below can never leave an older result
        // standing at this path as if it were current.
        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(completePath)) File.Delete(completePath);
            if (File.Exists(resultPath)) File.Delete(resultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Record(job, "WRITE FAILED", null, null, ex.Message, null, null),
                new JobTiming(job.Id, false, false, watch.Elapsed.TotalMilliseconds, refusal));
        }

        PlanningJobResult result = (runner ?? PlanningStudyModes.RunJob)(job);
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = PlanningManifestVersion.Result,
            job = new
            {
                ordinal = job.Ordinal,
                id = job.Id,
                snapshot = new { id = job.Case.Snapshot.Id, instantUtc = PlanningTime.Format(job.Case.Snapshot.Instant) },
                study = new { id = job.Study.Id, mode = job.Study.Mode }
            },
            provenance = new
            {
                bundleSha256 = master.BundleSha256,
                interpretation = master.Interpretation,
                masterSha256 = master.MasterSha256,
                caseSha256 = job.Case.CaseSha256,
                derivationSha256 = job.Case.DerivationSha256,
                requestFingerprint = job.RequestFingerprint,
                inputFingerprint = job.InputFingerprint,
                engine = GridSim.Core.BuildInfo.InformationalVersion
            },
            audit = job.Case.Audit,
            validation = job.Case.Validation,
            study = (object)job.Study,
            status = result.Status,
            outcome = result.Outcome,
            reason = result.Reason,
            evidence = result.Evidence
        }, OutputJson) + "\n";
        byte[] bytes = new UTF8Encoding(false).GetBytes(json);
        string sha = PlanningFingerprints.Sha256(bytes);
        try
        {
            WriteNew(resultPath, bytes);
            byte[] back = File.ReadAllBytes(resultPath);
            if (back.Length != bytes.Length || PlanningFingerprints.Sha256(back) != sha)
                throw new IOException("Result read-back does not match what was written.");
            // Only verdicts are reusable: an INVALID CASE or FAILED job is always re-run on retry.
            if (PlanningStudyModes.IsVerdict(result.Status))
                WriteNew(completePath, JsonSerializer.Serialize(new Completion("gridsim-planning-complete/1", job.Id, build,
                    job.RequestFingerprint, job.InputFingerprint, sha, bytes.Length, result.Status, result.Outcome, result.Reason), OutputJson) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch (IOException) { }
            return (Record(job, "WRITE FAILED", result.Status, result.Outcome, ex.Message, null, null),
                new JobTiming(job.Id, true, false, watch.Elapsed.TotalMilliseconds, refusal));
        }
        return (Record(job, "COMPLETED", result.Status, result.Outcome, result.Reason, relative, sha),
            new JobTiming(job.Id, true, false, watch.Elapsed.TotalMilliseconds, refusal));
    }

    /// <summary>Reuse an earlier output only when the completion record names this job, this build, the same request
    /// and input fingerprints, and the result file on disk still has the recorded length and SHA-256.</summary>
    private static string? ReuseRefusal(PlanningJob job, string build, string resultPath, string completePath, out Completion? reusable)
    {
        reusable = null;
        if (!File.Exists(completePath)) return File.Exists(resultPath) ? "result without a completion record" : null;
        Completion? record;
        try { record = JsonSerializer.Deserialize<Completion>(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(completePath)).Span, OutputJson); }
        catch (JsonException) { return "unreadable completion record"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "completion record unreadable: " + ex.Message; }
        if (record is null || record.SchemaVersion != "gridsim-planning-complete/1" || record.JobId != job.Id) return "completion record is for another job";
        if (record.Build != build) return "build fingerprint differs";
        if (record.RequestFingerprint != job.RequestFingerprint) return "request fingerprint differs";
        if (record.InputFingerprint != job.InputFingerprint) return "input fingerprint differs";
        if (!File.Exists(resultPath)) return "result file missing";
        byte[] bytes;
        try { bytes = File.ReadAllBytes(resultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "result file unreadable: " + ex.Message; }
        if (bytes.Length != record.ResultBytes || PlanningFingerprints.Sha256(bytes) != record.ResultSha256) return "result file fails its write check";
        if (!PlanningStudyModes.IsVerdict(record.Status)) return "only study verdicts are reused";
        reusable = record;
        return null;
    }

    private static JobRecord Record(PlanningJob job, string state, string? status, string? outcome, string? reason,
        string? result, string? sha = null) => new(job.Ordinal, job.Id, job.Case.Snapshot.Id, job.Study.Id, job.Study.Mode,
        state, status, outcome, reason, result, sha, job.Case.CaseSha256, job.RequestFingerprint, job.InputFingerprint);

    private static string BuildFingerprint() =>
        GridSim.Core.BuildInfo.InformationalVersion +
        " core:" + typeof(PlanningExpansion).Module.ModuleVersionId.ToString("N") +
        " cli:" + typeof(PlanningStudyCommand).Module.ModuleVersionId.ToString("N");

    // ---- Output folder ---------------------------------------------------------------------------------------------

    /// <summary>The output folder is new, empty, or an earlier planning run (it holds the marker). The previous run's
    /// manifest, summary and evidence are removed before any job starts, so an interrupted run never leaves an older
    /// run manifest looking current.</summary>
    private static void PrepareOutput(string outDir)
    {
        if (File.Exists(outDir)) throw new IOException("--out names a file; give a folder for the planning run.");
        if (Directory.Exists(outDir))
        {
            if (Directory.EnumerateFileSystemEntries(outDir).Any() && !File.Exists(Path.Combine(outDir, Marker)))
                throw new IOException("--out is a non-empty folder that is not a planning run folder; use a new folder.");
        }
        else Directory.CreateDirectory(outDir);
        string marker = Path.Combine(outDir, Marker);
        if (!File.Exists(marker)) File.WriteAllText(marker, MarkerText, new UTF8Encoding(false));
        else if (File.ReadAllText(marker) != MarkerText)
            throw new IOException("--out holds a planning run marker of another version.");
        foreach (string name in new[] { RunManifest, Summary, Evidence })
        {
            string path = Path.Combine(outDir, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WriteNew(string path, string text) => WriteNew(path, new UTF8Encoding(false).GetBytes(text));
    private static void WriteNew(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string SummaryText(string manifestSha, PlanningExpansionResult expansion, PlanningManifest manifest,
        JobRecord[] records, string state)
    {
        var sb = new StringBuilder();
        sb.Append("planning-study ").Append(PlanningManifestVersion.Current).Append('\n');
        sb.Append("manifest  ").Append(manifestSha).Append('\n');
        sb.Append("master    ").Append(expansion.MasterSha256).Append("  (bundle ").Append(manifest.Master.BundleSha256.ToLowerInvariant())
          .Append(", ").Append(manifest.Master.Interpretation.Loader).Append(", dispatch ")
          .Append(manifest.Master.Interpretation.Dispatch ? "true" : "false").Append(")\n");
        sb.Append("snapshots\n");
        foreach (PlanningResolvedCase c in expansion.Cases)
            sb.Append("  ").Append(c.Snapshot.Id).Append("  ").Append(PlanningTime.Format(c.Snapshot.Instant))
              .Append("  case ").Append(c.CaseSha256[..16]).Append("  applied ").Append(c.AppliedOperations)
              .Append(c.Validation.Valid ? "" : "  INVALID").Append('\n');
        sb.Append("jobs\n");
        foreach (JobRecord r in records)
        {
            sb.Append("  ").Append(r.Ordinal.ToString("D3", CultureInfo.InvariantCulture)).Append("  ").Append(r.Id).Append("  ");
            sb.Append(r.State == "COMPLETED" ? $"{r.Status} ({r.Outcome})" : r.State);
            if (r.Reason is not null) sb.Append("  ").Append(r.Reason);
            sb.Append('\n');
        }
        sb.Append("run ").Append(state).Append(": ")
          .Append(records.Count(r => r.State == "COMPLETED")).Append(" of ").Append(records.Length).Append(" jobs completed, ")
          .Append(records.Count(Passed)).Append(" passed, ")
          .Append(records.Count(r => PlanningStudyModes.IsVerdict(r.Status) && !Passed(r))).Append(" not passed, ")
          .Append(records.Count(r => r.Status is PlanningStudyModes.InvalidCase or PlanningStudyModes.Failed)).Append(" failed\n");
        return sb.ToString();
    }

    // ---- Identity ------------------------------------------------------------------------------------------------

    /// <summary>The content identity of a model: SHA-256 of its complete JSON serialisation (every public property,
    /// shortest round-trip doubles, infinities as named literals).</summary>
    private static string CaseSha256(GridModel model) =>
        PlanningFingerprints.Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(model, ModelJson)));

    /// <summary>The consumed-file manifest of a native case, in load order: the same list and encoding as
    /// transmission-security, so its case.sha256 and this master's bundle digest agree.</summary>
    private static List<PlanningInputFile> Inputs(string path, string root) =>
        CaseIdentity.Inputs(path, root, (label, sha) => new PlanningInputFile(label, sha), requireJsonExtension: false);

    // ---- Strict manifest parsing ---------------------------------------------------------------------------------

    internal static PlanningManifest Parse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes));
        JsonElement root = document.RootElement;
        Obj(root, "manifest", ["manifestVersion", "master", "projects", "projectOrder", "snapshots", "studies"],
            ["manifestVersion", "master", "snapshots", "studies"]);
        string version = Str(root.GetProperty("manifestVersion"), "manifestVersion");
        if (version != PlanningManifestVersion.Current)
            throw new PlanningManifestException($"manifestVersion must be \"{PlanningManifestVersion.Current}\", not \"{version}\".");

        JsonElement masterEl = root.GetProperty("master");
        Obj(masterEl, "master", ["case", "interpretation"], ["case", "interpretation"]);
        JsonElement caseEl = masterEl.GetProperty("case");
        Obj(caseEl, "master.case", ["path", "sha256"], ["path", "sha256"]);
        JsonElement interp = masterEl.GetProperty("interpretation");
        Obj(interp, "master.interpretation", ["loader", "dispatch"], ["loader", "dispatch"]);
        string loader = Str(interp.GetProperty("loader"), "master.interpretation.loader");
        if (loader != PlanningInterpretation.NativeJson)
            throw new PlanningManifestException($"master.interpretation.loader must be \"{PlanningInterpretation.NativeJson}\" in this version.");
        bool dispatch = Bool(interp.GetProperty("dispatch"), "master.interpretation.dispatch");
        if (dispatch)
            throw new PlanningManifestException("master.interpretation.dispatch must be false: loader merit-order balancing would change generation outside the declared operations.");
        var masterRef = new PlanningMasterReference(Str(caseEl.GetProperty("path"), "master.case.path"),
            Str(caseEl.GetProperty("sha256"), "master.case.sha256"), new PlanningInterpretation(loader, dispatch));

        var projects = new List<PlanningProject>();
        if (root.TryGetProperty("projects", out JsonElement projectsEl))
            foreach ((JsonElement p, int pi) in Arr(projectsEl, "projects"))
            {
                string at = $"projects[{pi}]";
                Obj(p, at, ["id", "description", "operations"], ["id", "operations"]);
                if (p.TryGetProperty("description", out JsonElement d)) Str(d, at + ".description");
                var ops = new List<PlanningOperation>();
                foreach ((JsonElement o, int oi) in Arr(p.GetProperty("operations"), at + ".operations"))
                    ops.Add(Operation(o, $"{at}.operations[{oi}]"));
                projects.Add(new PlanningProject(Id(p.GetProperty("id"), at + ".id"), ops));
            }
        var order = new List<string>();
        if (root.TryGetProperty("projectOrder", out JsonElement orderEl))
            foreach ((JsonElement e, int i) in Arr(orderEl, "projectOrder")) order.Add(Id(e, $"projectOrder[{i}]"));

        var snapshots = new List<PlanningSnapshot>();
        foreach ((JsonElement s, int si) in Arr(root.GetProperty("snapshots"), "snapshots"))
        {
            string at = $"snapshots[{si}]";
            Obj(s, at, ["id", "description", "instant"], ["id", "instant"]);
            if (s.TryGetProperty("description", out JsonElement d)) Str(d, at + ".description");
            snapshots.Add(new PlanningSnapshot(Id(s.GetProperty("id"), at + ".id"),
                PlanningTime.Parse(Str(s.GetProperty("instant"), at + ".instant"), at + ".instant")));
        }

        var studies = new List<PlanningStudyDefinition>();
        foreach ((JsonElement s, int si) in Arr(root.GetProperty("studies"), "studies"))
            studies.Add(Study(s, $"studies[{si}]"));
        return new PlanningManifest(masterRef, projects, order, snapshots, studies);
    }

    private static PlanningOperation Operation(JsonElement o, string at)
    {
        Obj(o, at, ["id", "description", "target", "field", "expectedPrior", "value", "effectiveFrom", "effectiveTo"],
            ["id", "target", "field", "expectedPrior", "value", "effectiveFrom"]);
        if (o.TryGetProperty("description", out JsonElement d)) Str(d, at + ".description");
        JsonElement t = o.GetProperty("target");
        Obj(t, at + ".target", ["element", "index", "from", "to", "bus", "name"], ["element"]);
        PlanningElementKind kind = Str(t.GetProperty("element"), at + ".target.element") switch
        {
            "branch" => PlanningElementKind.Branch,
            "generator" => PlanningElementKind.Generator,
            "load" => PlanningElementKind.Load,
            string other => throw new PlanningManifestException($"{at}.target.element must be branch, generator or load, not \"{other}\".")
        };
        int? OptInt(string name) => t.TryGetProperty(name, out JsonElement v) ? Int(v, $"{at}.target.{name}") : null;
        var target = new PlanningTarget(kind, OptInt("index"), OptInt("from"), OptInt("to"), OptInt("bus"),
            t.TryGetProperty("name", out JsonElement n) ? Str(n, at + ".target.name") : null);
        string fieldText = Str(o.GetProperty("field"), at + ".field");
        if (!PlanningExpansion.TryParseField(fieldText, out PlanningField field))
            throw new PlanningManifestException($"{at}.field \"{fieldText}\" is not supported (inService, rateAMva, rateBMva, rateCMva, pgMw, qgMvar, pdMw, qdMvar).");
        DateTimeOffset? to = null;
        if (o.TryGetProperty("effectiveTo", out JsonElement toEl) && toEl.ValueKind != JsonValueKind.Null)
            to = PlanningTime.Parse(Str(toEl, at + ".effectiveTo"), at + ".effectiveTo");
        return new PlanningOperation(Id(o.GetProperty("id"), at + ".id"), target, field,
            Value(o.GetProperty("expectedPrior"), at + ".expectedPrior"), Value(o.GetProperty("value"), at + ".value"),
            PlanningTime.Parse(Str(o.GetProperty("effectiveFrom"), at + ".effectiveFrom"), at + ".effectiveFrom"), to);
    }

    private static PlanningStudyDefinition Study(JsonElement s, string at)
    {
        Obj(s, at, ["id", "description", "mode", "parameters"], ["id", "mode", "parameters"]);
        if (s.TryGetProperty("description", out JsonElement d)) Str(d, at + ".description");
        string id = Id(s.GetProperty("id"), at + ".id");
        string mode = Str(s.GetProperty("mode"), at + ".mode");
        JsonElement p = s.GetProperty("parameters");
        if (mode == TransmissionSecurityPlanningStudy.ModeName)
        {
            string pa = at + ".parameters";
            Obj(p, pa, ["outages", "voltage", "postFaultRating", "dc", "costs"], ["outages", "voltage", "postFaultRating"]);
            var outages = new List<BranchOutage>();
            foreach ((JsonElement o, int oi) in Arr(p.GetProperty("outages"), pa + ".outages"))
            {
                string oa = $"{pa}.outages[{oi}]";
                Obj(o, oa, ["index", "from", "to"], ["index", "from", "to"]);
                outages.Add(new BranchOutage(Int(o.GetProperty("index"), oa + ".index"), Int(o.GetProperty("from"), oa + ".from"),
                    Int(o.GetProperty("to"), oa + ".to")));
            }
            JsonElement v = p.GetProperty("voltage");
            Obj(v, pa + ".voltage", ["minPu", "maxPu"], ["minPu", "maxPu"]);
            PostFaultRating rating = Str(p.GetProperty("postFaultRating"), pa + ".postFaultRating") switch
            {
                "A" => PostFaultRating.RateA, "B" => PostFaultRating.RateB, "C" => PostFaultRating.RateC,
                _ => throw new PlanningManifestException(pa + ".postFaultRating must be A, B or C.")
            };
            int rounds = new ScopfOptions().MaxRounds;
            if (p.TryGetProperty("dc", out JsonElement dc))
            {
                Obj(dc, pa + ".dc", ["maxRounds"], ["maxRounds"]);
                rounds = Int(dc.GetProperty("maxRounds"), pa + ".dc.maxRounds");
            }
            List<double>? costs = null;
            if (p.TryGetProperty("costs", out JsonElement c))
            {
                Obj(c, pa + ".costs", ["perMwh"], ["perMwh"]);
                costs = Arr(c.GetProperty("perMwh"), pa + ".costs.perMwh").Select(x => Num(x.Item1, pa + ".costs.perMwh")).ToList();
            }
            return new TransmissionSecurityPlanningStudy(id, outages, Num(v.GetProperty("minPu"), pa + ".voltage.minPu"),
                Num(v.GetProperty("maxPu"), pa + ".voltage.maxPu"), rating, rounds, costs);
        }
        if (mode == TransferPlanningStudy.ModeName) return new TransferPlanningStudy(id, TransferParameters(p, at + ".parameters"));
        if (mode == CorrectiveN11PlanningStudy.ModeName) return new CorrectiveN11PlanningStudy(id, CorrectiveParameters(p, at + ".parameters"));
        if (PlanningStudyModes.Reserved.Contains(mode))
            throw new PlanningManifestException($"{at}.mode \"{mode}\" is reserved for a later increment and is not available in this build.");
        throw new PlanningManifestException($"{at}.mode \"{mode}\" is not a planning study mode (available: {string.Join(", ", PlanningStudyModes.Available.Keys)}).");
    }

    private static BranchOutage Outage(JsonElement o, string at)
    {
        Obj(o, at, ["index", "from", "to"], ["index", "from", "to"]);
        return new BranchOutage(Int(o.GetProperty("index"), at + ".index"), Int(o.GetProperty("from"), at + ".from"),
            Int(o.GetProperty("to"), at + ".to"));
    }

    private static (double, double) Voltage(JsonElement p, string pa)
    {
        JsonElement v = p.GetProperty("voltage");
        Obj(v, pa + ".voltage", ["minPu", "maxPu"], ["minPu", "maxPu"]);
        return (Num(v.GetProperty("minPu"), pa + ".voltage.minPu"), Num(v.GetProperty("maxPu"), pa + ".voltage.maxPu"));
    }

    private static PostFaultRating Rating(JsonElement e, string at) => Str(e, at) switch
    {
        "A" => PostFaultRating.RateA, "B" => PostFaultRating.RateB, "C" => PostFaultRating.RateC,
        _ => throw new PlanningManifestException(at + " must be A, B or C.")
    };

    /// <summary>The <c>transfer-capability</c> study fields, without schemaVersion and case.</summary>
    private static TransferStudyRequest TransferParameters(JsonElement p, string pa)
    {
        Obj(p, pa, ["sources", "sinks", "outages", "voltage", "postFaultRating", "search", "interfaces"],
            ["sources", "sinks", "outages", "voltage", "postFaultRating", "search"]);
        TransferParticipant[] Group(string name) => Arr(p.GetProperty(name), pa + "." + name).Select(x =>
        {
            string at = $"{pa}.{name}[{x.Item2}]";
            Obj(x.Item1, at, ["index", "bus", "weight"], ["index", "bus", "weight"]);
            return new TransferParticipant(Int(x.Item1.GetProperty("index"), at + ".index"), Int(x.Item1.GetProperty("bus"), at + ".bus"),
                Num(x.Item1.GetProperty("weight"), at + ".weight"));
        }).ToArray();
        JsonElement s = p.GetProperty("search");
        Obj(s, pa + ".search", ["maxMw", "stepMw", "toleranceMw", "maxTrials", "probes"], ["maxMw", "stepMw", "toleranceMw", "maxTrials"]);
        var search = new TransferSearchOptions(Num(s.GetProperty("maxMw"), pa + ".search.maxMw"), Num(s.GetProperty("stepMw"), pa + ".search.stepMw"),
            Num(s.GetProperty("toleranceMw"), pa + ".search.toleranceMw"), Int(s.GetProperty("maxTrials"), pa + ".search.maxTrials"),
            s.TryGetProperty("probes", out JsonElement pr) ? Int(pr, pa + ".search.probes") : 1);
        TransferInterface[]? interfaces = null;
        if (p.TryGetProperty("interfaces", out JsonElement ifs))
            interfaces = Arr(ifs, pa + ".interfaces").Select(x =>
            {
                string at = $"{pa}.interfaces[{x.Item2}]";
                Obj(x.Item1, at, ["name", "branches", "limitMw"], ["name", "branches"]);
                InterfaceBranch[] branches = Arr(x.Item1.GetProperty("branches"), at + ".branches").Select(b =>
                {
                    string ba = $"{at}.branches[{b.Item2}]";
                    Obj(b.Item1, ba, ["index", "from", "to", "sign"], ["index", "from", "to", "sign"]);
                    return new InterfaceBranch(Int(b.Item1.GetProperty("index"), ba + ".index"), Int(b.Item1.GetProperty("from"), ba + ".from"),
                        Int(b.Item1.GetProperty("to"), ba + ".to"), Int(b.Item1.GetProperty("sign"), ba + ".sign"));
                }).ToArray();
                return new TransferInterface(Str(x.Item1.GetProperty("name"), at + ".name"), branches,
                    x.Item1.TryGetProperty("limitMw", out JsonElement l) ? Num(l, at + ".limitMw") : null);
            }).ToArray();
        (double min, double max) = Voltage(p, pa);
        return new TransferStudyRequest(Group("sources"), Group("sinks"),
            Arr(p.GetProperty("outages"), pa + ".outages").Select(x => Outage(x.Item1, $"{pa}.outages[{x.Item2}]")).ToArray(),
            min, max, Rating(p.GetProperty("postFaultRating"), pa + ".postFaultRating"), search, interfaces);
    }

    /// <summary>The <c>corrective-sequence</c> study fields, without schemaVersion and case.</summary>
    private static CorrectiveSequenceRequest CorrectiveParameters(JsonElement p, string pa)
    {
        Obj(p, pa, ["voltage", "ratings", "policy", "sequences"], ["voltage", "ratings", "policy", "sequences"]);
        JsonElement r = p.GetProperty("ratings");
        Obj(r, pa + ".ratings", ["firstStage", "intermediate", "final"], ["firstStage", "intermediate", "final"]);
        JsonElement pol = p.GetProperty("policy");
        Obj(pol, pa + ".policy", ["eligible", "availableMinutes", "balanceToleranceMw"], ["eligible", "availableMinutes"]);
        GeneratorActionEnvelope[] eligible = Arr(pol.GetProperty("eligible"), pa + ".policy.eligible").Select(x =>
        {
            string at = $"{pa}.policy.eligible[{x.Item2}]";
            JsonElement e = x.Item1;
            Obj(e, at, ["generator", "bus", "direction", "minFinalMw", "maxFinalMw", "maxDeltaMw", "rampMwPerMin", "windowMinutes"],
                ["generator", "bus", "direction", "minFinalMw", "maxFinalMw", "maxDeltaMw", "rampMwPerMin", "windowMinutes"]);
            ActionDirection direction = Str(e.GetProperty("direction"), at + ".direction") switch
            {
                "Up" => ActionDirection.Up, "Down" => ActionDirection.Down, "Both" => ActionDirection.Both,
                _ => throw new PlanningManifestException(at + ".direction must be Up, Down or Both.")
            };
            return new GeneratorActionEnvelope(Int(e.GetProperty("generator"), at + ".generator"), Int(e.GetProperty("bus"), at + ".bus"),
                direction, Num(e.GetProperty("minFinalMw"), at + ".minFinalMw"), Num(e.GetProperty("maxFinalMw"), at + ".maxFinalMw"),
                Num(e.GetProperty("maxDeltaMw"), at + ".maxDeltaMw"), Num(e.GetProperty("rampMwPerMin"), at + ".rampMwPerMin"),
                Num(e.GetProperty("windowMinutes"), at + ".windowMinutes"));
        }).ToArray();
        double minutes = Num(pol.GetProperty("availableMinutes"), pa + ".policy.availableMinutes");
        CorrectiveActionPolicy policy = pol.TryGetProperty("balanceToleranceMw", out JsonElement tol)
            ? new CorrectiveActionPolicy(eligible, minutes, Num(tol, pa + ".policy.balanceToleranceMw"))
            : new CorrectiveActionPolicy(eligible, minutes);
        CorrectiveSequence[] sequences = Arr(p.GetProperty("sequences"), pa + ".sequences").Select(x =>
        {
            string at = $"{pa}.sequences[{x.Item2}]";
            Obj(x.Item1, at, ["first", "second", "proposal", "actions"], ["first", "second", "proposal"]);
            CorrectiveProposal proposal = Str(x.Item1.GetProperty("proposal"), at + ".proposal") switch
            {
                "None" => CorrectiveProposal.None, "Prescribed" => CorrectiveProposal.Prescribed,
                "PlannedRedispatch" => CorrectiveProposal.PlannedRedispatch,
                _ => throw new PlanningManifestException(at + ".proposal must be None, Prescribed or PlannedRedispatch.")
            };
            GeneratorAction[]? actions = null;
            if (x.Item1.TryGetProperty("actions", out JsonElement acts))
                actions = Arr(acts, at + ".actions").Select(a =>
                {
                    string aa = $"{at}.actions[{a.Item2}]";
                    Obj(a.Item1, aa, ["generator", "bus", "deltaMw"], ["generator", "bus", "deltaMw"]);
                    return new GeneratorAction(Int(a.Item1.GetProperty("generator"), aa + ".generator"), Int(a.Item1.GetProperty("bus"), aa + ".bus"),
                        Num(a.Item1.GetProperty("deltaMw"), aa + ".deltaMw"));
                }).ToArray();
            return new CorrectiveSequence(Outage(x.Item1.GetProperty("first"), at + ".first"), Outage(x.Item1.GetProperty("second"), at + ".second"),
                proposal, actions);
        }).ToArray();
        (double min, double max) = Voltage(p, pa);
        return new CorrectiveSequenceRequest(sequences, min, max, Rating(r.GetProperty("firstStage"), pa + ".ratings.firstStage"),
            Rating(r.GetProperty("intermediate"), pa + ".ratings.intermediate"), Rating(r.GetProperty("final"), pa + ".ratings.final"), policy);
    }

    private static void Obj(JsonElement e, string at, string[] allowed, string[] required)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new PlanningManifestException(at + " must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new PlanningManifestException($"{at}: duplicate property \"{p.Name}\".");
            if (!allowed.Contains(p.Name)) throw new PlanningManifestException($"{at}: unknown property \"{p.Name}\".");
        }
        foreach (string r in required)
            if (!seen.Contains(r)) throw new PlanningManifestException($"{at}: required property \"{r}\" is missing.");
    }
    private static IEnumerable<(JsonElement, int)> Arr(JsonElement e, string at) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Select((x, i) => (x, i)).ToArray()
        : throw new PlanningManifestException(at + " must be an array.");
    private static string Str(JsonElement e, string at) => e.ValueKind == JsonValueKind.String
        ? e.GetString()! : throw new PlanningManifestException(at + " must be a string.");
    private static string Id(JsonElement e, string at)
    {
        string id = Str(e, at);
        return IdPattern().IsMatch(id) ? id
            : throw new PlanningManifestException($"{at} \"{id}\" must be 1-64 characters of letters, digits, '.', '_' or '-', starting with a letter or digit.");
    }
    private static bool Bool(JsonElement e, string at) => e.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => throw new PlanningManifestException(at + " must be true or false.")
    };
    private static int Int(JsonElement e, string at) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v)
        ? v : throw new PlanningManifestException(at + " must be an integer.");
    private static double Num(JsonElement e, string at) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double v) && double.IsFinite(v)
        ? v : throw new PlanningManifestException(at + " must be a finite number.");
    private static PlanningValue Value(JsonElement e, string at) => e.ValueKind switch
    {
        JsonValueKind.True => PlanningValue.Of(true),
        JsonValueKind.False => PlanningValue.Of(false),
        JsonValueKind.Number => PlanningValue.Of(Num(e, at)),
        _ => throw new PlanningManifestException(at + " must be a number or true/false.")
    };

    private sealed class FiniteDoubleWriter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsFinite(value)) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
        }
    }
}
