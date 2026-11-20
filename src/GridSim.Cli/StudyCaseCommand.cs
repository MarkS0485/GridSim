using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-01: <c>gridsim study-case &lt;study-case.json&gt; --out &lt;dir&gt; [--parallel N] [--max-jobs N]</c>.
/// Parses the strict <c>gridsim-studycase/1</c> document and the groups sidecar it names, resolves the immutable master
/// through the native gated loader (dispatch suppressed), expands variations and scenarios to explicit dated operations
/// through the unchanged RC3 planning expansion, runs one job per (scenario, instant, study) into its own folder and
/// writes one ordered result manifest, a derivation record and a summary. Timings and reuse decisions go to a separate
/// evidence file, so the other outputs are byte-identical between an uninterrupted run and a retried one.
/// </summary>
internal static class StudyCaseCommand
{
    private const string Marker = ".gridsim-studycase-run";
    private const string MarkerText = "gridsim-studycase-run/1\n";
    private const string RunManifest = "run-manifest.json";
    private const string Summary = "summary.txt";
    private const string Evidence = "evidence.json";
    private const string Derivation = "derivation.json";

    private const string Usage = "usage: gridsim study-case <study-case.json> --out <dir> [--parallel N] [--max-jobs N]";

    public static int Run(List<string> arguments, string root)
    {
        if (!TryParseArguments(arguments, out string studyArg, out string outArg, out int parallel, out int? maxJobs))
        {
            Console.Error.WriteLine(Usage);
            return CliExit.Usage;
        }
        var clock = Stopwatch.StartNew();
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            string studyPath = Path.GetFullPath(studyArg);
            string outDir = Path.GetFullPath(outArg);
            string baseDir = Path.GetDirectoryName(studyPath)!;
            byte[] studyBytes = File.ReadAllBytes(studyPath);
            string studySha = PlanningFingerprints.Sha256(studyBytes);

            // 1. Parse and validate the study case and the groups document, before any case is read.
            StudyCaseModel studyCase = Parse(studyBytes);
            ModelGroupSet? groupSet = null;
            if (studyCase.Groups is not null)
            {
                string groupsPath = Path.GetFullPath(studyCase.Groups.Path, baseDir);
                byte[] groupBytes = File.ReadAllBytes(groupsPath);
                if (!string.Equals(PlanningFingerprints.Sha256(groupBytes), studyCase.Groups.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Groups document SHA256 mismatch. Resolved identity: " + PlanningFingerprints.Sha256(groupBytes));
                groupSet = ModelGroupsDocument.Parse(groupBytes);
                if (!string.Equals(groupSet.CaseBundleSha256, studyCase.Master.BundleSha256, StringComparison.OrdinalIgnoreCase))
                    throw new StudyCaseException("The groups document is bound to case " + groupSet.CaseBundleSha256 +
                        ", not to the study case's master " + studyCase.Master.BundleSha256.ToLowerInvariant() + ".");
            }

            // 2. Resolve the master with the approved loader and check the complete input manifest.
            string casePath = Path.GetFullPath(studyCase.Master.Path, baseDir);
            if (!Directory.Exists(casePath) && !(File.Exists(casePath) && casePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("master.case.path must name a native JSON case directory or v2 case manifest: " + casePath);
            List<PlanningInputFile> inputs = PlanningStudyCommand.SeamInputs(casePath, root);
            string bundle = PlanningFingerprints.Bundle(inputs);
            if (!string.Equals(bundle, studyCase.Master.BundleSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Master case SHA256 mismatch. Resolved identity: " + bundle);
            GridModel model = Gated.TransmissionNative(casePath, root);
            if (!inputs.SequenceEqual(PlanningStudyCommand.SeamInputs(casePath, root)))
                throw new IOException("Master inputs changed during loading.");
            var master = new PlanningMaster(model, bundle, inputs, studyCase.Master.Interpretation);

            // 3. Bind groups to identities (unresolved and duplicate members are refused).
            BoundGroups? groups = groupSet is null ? null : BoundGroups.Bind(groupSet, model);

            // 4-6. Expand scenarios, hand the explicit operations to the RC3 expansion, validate each case.
            StudyCaseExpansionResult expansion = StudyCaseExpansion.Expand(master, studyCase, groups, PlanningStudyCommand.SeamCaseSha256);

            PrepareOutput(outDir);
            string build = PlanningStudyCommand.SeamBuild();
            PlanningStudyCommand.SeamOutcome[] outcomes = PlanningStudyCommand.SeamRunJobs(expansion.Jobs, outDir, master, build,
                parallel, maxJobs, job => StudyCaseModes.RunJob(job, groups), cancel.Token);

            if (!inputs.SequenceEqual(PlanningStudyCommand.SeamInputs(casePath, root)) ||
                PlanningFingerprints.Sha256(File.ReadAllBytes(studyPath)) != studySha)
                throw new IOException("Master inputs or the study case changed during the run; no result manifest was written.");

            string state = outcomes.Any(o => o.State == "WRITE FAILED") ? "INCOMPLETE"
                : outcomes.Any(o => o.State == "NOT ATTEMPTED") ? "CANCELLED" : "COMPLETE";
            bool Passed(PlanningStudyCommand.SeamOutcome o) => o.State == "COMPLETED" && StudyCaseModes.Passed(o.Job.Study.Mode, o.Status);
            bool Verdict(PlanningStudyCommand.SeamOutcome o) => PlanningStudyModes.IsVerdict(o.Status);

            string runManifest = PlanningStudyCommand.SeamJson(new
            {
                schemaVersion = StudyCaseVersions.StudyCaseResult,
                state,
                manifestVersion = StudyCaseVersions.StudyCase,
                manifestSha256 = studySha,
                build,
                master = new
                {
                    path = studyCase.Master.Path,
                    bundleSha256 = bundle,
                    interpretation = studyCase.Master.Interpretation,
                    masterSha256 = expansion.MasterSha256,
                    caseSha256 = expansion.MasterCaseSha256,
                    inputs
                },
                groups = groups is null ? null : new
                {
                    path = studyCase.Groups!.Path,
                    sha256 = studyCase.Groups.Sha256.ToLowerInvariant(),
                    caseBundleSha256 = groups.Set.CaseBundleSha256,
                    groups = groups.Groups.Select(g => new
                    {
                        id = g.Id,
                        kind = ModelGroup.KindName(g.Group.Kind),
                        buses = g.BusIds,
                        generators = g.GeneratorIndices,
                        branches = g.BranchIndices
                    })
                },
                variationOrder = studyCase.Variations.Order,
                cases = expansion.Cases.Select(c => new
                {
                    scenario = c.Scenario,
                    instant = c.InstantId,
                    instantUtc = PlanningTime.Format(c.Case.Snapshot.Instant),
                    caseSha256 = c.Case.CaseSha256,
                    derivationSha256 = c.Case.DerivationSha256,
                    appliedOperations = c.Case.AppliedOperations,
                    valid = c.Case.Validation.Valid
                }),
                jobs = outcomes.Select(o => new
                {
                    ordinal = o.Job.Ordinal,
                    id = o.Job.Id,
                    snapshot = o.Job.Case.Snapshot.Id,
                    study = o.Job.Study.Id,
                    mode = o.Job.Study.Mode,
                    state = o.State,
                    status = o.Status,
                    outcome = o.Outcome,
                    reason = o.Reason,
                    result = o.Result,
                    resultSha256 = o.ResultSha256,
                    caseSha256 = o.Job.Case.CaseSha256,
                    requestFingerprint = o.Job.RequestFingerprint,
                    inputFingerprint = o.Job.InputFingerprint
                }),
                counts = new
                {
                    jobs = outcomes.Length,
                    completed = outcomes.Count(o => o.State == "COMPLETED"),
                    notAttempted = outcomes.Count(o => o.State == "NOT ATTEMPTED"),
                    writeFailed = outcomes.Count(o => o.State == "WRITE FAILED"),
                    passed = outcomes.Count(Passed),
                    notPassed = outcomes.Count(o => Verdict(o) && !Passed(o)),
                    invalidCase = outcomes.Count(o => o.Status == PlanningStudyModes.InvalidCase),
                    failed = outcomes.Count(o => o.Status == PlanningStudyModes.Failed)
                }
            });
            string derivation = PlanningStudyCommand.SeamJson(new
            {
                schemaVersion = "gridsim-studycase-derivation/1",
                note = "Every operation of every resolved case: variations (with their expansion stage) and the explicit element " +
                       "operations of each scenario, with the RC3 dispositions Applied, NotYetEffective and Expired. A refusal stops the run before any output.",
                cases = expansion.Cases.Select(c => new
                {
                    scenario = c.Scenario,
                    instant = c.InstantId,
                    instantUtc = PlanningTime.Format(c.Case.Snapshot.Instant),
                    derivationSha256 = c.Case.DerivationSha256,
                    operations = c.Derivation.Select(d => new
                    {
                        origin = d.Origin,
                        expansionStage = d.ExpansionStage,
                        d.Entry.Project,
                        d.Entry.Operation,
                        d.Entry.Target,
                        d.Entry.Field,
                        disposition = d.Entry.Disposition,
                        sequence = d.Entry.Sequence,
                        expectedPrior = d.Entry.ExpectedPrior,
                        replacement = d.Entry.Replacement,
                        original = d.Entry.Original,
                        prior = d.Entry.Prior,
                        effective = d.Entry.Effective,
                        effectiveFromUtc = d.Entry.EffectiveFromUtc,
                        effectiveToUtc = d.Entry.EffectiveToUtc
                    }),
                    scenarioChanges = c.ScenarioChanges
                })
            });
            string summary = SummaryText(studySha, expansion, studyCase, groups, outcomes, state, Passed, Verdict);

            // The evidence file first and the result manifest last: a run manifest present means the run finished.
            PlanningStudyCommand.SeamWriteNew(Path.Combine(outDir, Evidence), PlanningStudyCommand.SeamJson(new
            {
                note = "Execution evidence only: elapsed times and reuse decisions vary between runs and are not study results.",
                parallel,
                maxJobs,
                totalElapsedMs = clock.Elapsed.TotalMilliseconds,
                jobs = outcomes.Select(o => new { id = o.Job.Id, attempted = o.State != "NOT ATTEMPTED", reused = o.Reused, elapsedMs = o.ElapsedMs, reuseRefusal = o.ReuseRefusal })
            }));
            PlanningStudyCommand.SeamWriteNew(Path.Combine(outDir, Derivation), derivation);
            PlanningStudyCommand.SeamWriteNew(Path.Combine(outDir, Summary), summary);
            PlanningStudyCommand.SeamWriteNew(Path.Combine(outDir, RunManifest), runManifest);
            CliResultFromJson.Fill(runManifest);
            Console.Write(summary);
            if (state == "INCOMPLETE") return CliExit.InputError;
            return state == "COMPLETE" && outcomes.All(Passed) ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is PlanningManifestException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine("study-case: " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static bool TryParseArguments(List<string> a, out string studyCase, out string outDir, out int parallel, out int? maxJobs)
    {
        studyCase = outDir = "";
        parallel = 1;
        maxJobs = null;
        if (a.Count < 2 || a[1].StartsWith('-')) return false;
        studyCase = a[1];
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

    // ---- Output folder ---------------------------------------------------------------------------------------------

    private static void PrepareOutput(string outDir)
    {
        if (File.Exists(outDir)) throw new IOException("--out names a file; give a folder for the study-case run.");
        if (Directory.Exists(outDir))
        {
            if (Directory.EnumerateFileSystemEntries(outDir).Any() && !File.Exists(Path.Combine(outDir, Marker)))
                throw new IOException("--out is a non-empty folder that is not a study-case run folder; use a new folder.");
        }
        else Directory.CreateDirectory(outDir);
        string marker = Path.Combine(outDir, Marker);
        if (!File.Exists(marker)) File.WriteAllText(marker, MarkerText, new UTF8Encoding(false));
        else if (File.ReadAllText(marker) != MarkerText)
            throw new IOException("--out holds a study-case run marker of another version.");
        foreach (string name in new[] { RunManifest, Summary, Evidence, Derivation })
        {
            string path = Path.Combine(outDir, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string SummaryText(string studySha, StudyCaseExpansionResult expansion, StudyCaseModel studyCase,
        BoundGroups? groups, PlanningStudyCommand.SeamOutcome[] outcomes, string state,
        Func<PlanningStudyCommand.SeamOutcome, bool> passed, Func<PlanningStudyCommand.SeamOutcome, bool> verdict)
    {
        var sb = new StringBuilder();
        sb.Append("study-case ").Append(StudyCaseVersions.StudyCase).Append('\n');
        sb.Append("manifest  ").Append(studySha).Append('\n');
        sb.Append("master    ").Append(expansion.MasterSha256).Append("  (bundle ").Append(studyCase.Master.BundleSha256.ToLowerInvariant())
          .Append(", ").Append(studyCase.Master.Interpretation.Loader).Append(", dispatch ")
          .Append(studyCase.Master.Interpretation.Dispatch ? "true" : "false").Append(")\n");
        sb.Append("groups    ").Append(groups is null ? "none" : studyCase.Groups!.Sha256.ToLowerInvariant() + "  (" + groups.Groups.Count + " groups)").Append('\n');
        sb.Append("cases\n");
        foreach (StudyCaseCase c in expansion.Cases)
            sb.Append("  ").Append(c.Case.Snapshot.Id).Append("  ").Append(PlanningTime.Format(c.Case.Snapshot.Instant))
              .Append("  case ").Append(c.Case.CaseSha256[..16]).Append("  applied ").Append(c.Case.AppliedOperations)
              .Append(c.Case.Validation.Valid ? "" : "  INVALID").Append('\n');
        sb.Append("jobs\n");
        foreach (PlanningStudyCommand.SeamOutcome o in outcomes)
        {
            sb.Append("  ").Append(o.Job.Ordinal.ToString("D3", CultureInfo.InvariantCulture)).Append("  ").Append(o.Job.Id).Append("  ");
            sb.Append(o.State == "COMPLETED" ? $"{o.Status} ({o.Outcome})" : o.State);
            if (o.Reason is not null) sb.Append("  ").Append(o.Reason);
            sb.Append('\n');
        }
        sb.Append("run ").Append(state).Append(": ")
          .Append(outcomes.Count(o => o.State == "COMPLETED")).Append(" of ").Append(outcomes.Length).Append(" jobs completed, ")
          .Append(outcomes.Count(passed)).Append(" passed, ")
          .Append(outcomes.Count(o => verdict(o) && !passed(o))).Append(" not passed, ")
          .Append(outcomes.Count(o => o.Status is PlanningStudyModes.InvalidCase or PlanningStudyModes.Failed)).Append(" failed\n");
        return sb.ToString();
    }

    // ---- Strict parsing --------------------------------------------------------------------------------------------

    internal static StudyCaseModel Parse(byte[] bytes)
    {
        using JsonDocument document = StudyCaseJson.Parse(bytes, "The study case");
        JsonElement root = document.RootElement;
        StudyCaseJson.Obj(root, "study case",
            ["manifestVersion", "master", "groups", "instants", "variations", "variationOrder", "scenarios", "studies"],
            ["manifestVersion", "master", "instants", "studies"]);
        string version = StudyCaseJson.Str(root.GetProperty("manifestVersion"), "manifestVersion");
        if (version != StudyCaseVersions.StudyCase)
            throw new StudyCaseException($"manifestVersion must be \"{StudyCaseVersions.StudyCase}\", not \"{version}\".");

        JsonElement masterEl = root.GetProperty("master");
        StudyCaseJson.Obj(masterEl, "master", ["case", "interpretation"], ["case", "interpretation"]);
        JsonElement caseEl = masterEl.GetProperty("case");
        StudyCaseJson.Obj(caseEl, "master.case", ["path", "sha256"], ["path", "sha256"]);
        JsonElement interp = masterEl.GetProperty("interpretation");
        StudyCaseJson.Obj(interp, "master.interpretation", ["loader", "dispatch"], ["loader", "dispatch"]);
        string loader = StudyCaseJson.Str(interp.GetProperty("loader"), "master.interpretation.loader");
        if (loader != PlanningInterpretation.NativeJson)
            throw new StudyCaseException($"master.interpretation.loader must be \"{PlanningInterpretation.NativeJson}\" in this version.");
        if (StudyCaseJson.Bool(interp.GetProperty("dispatch"), "master.interpretation.dispatch"))
            throw new StudyCaseException("master.interpretation.dispatch must be false: loader merit-order balancing would change generation outside the declared operations.");
        var masterRef = new PlanningMasterReference(StudyCaseJson.Str(caseEl.GetProperty("path"), "master.case.path"),
            StudyCaseJson.Str(caseEl.GetProperty("sha256"), "master.case.sha256"), new PlanningInterpretation(loader, false));

        StudyCaseGroupsReference? groups = null;
        if (root.TryGetProperty("groups", out JsonElement groupsEl))
        {
            StudyCaseJson.Obj(groupsEl, "groups", ["path", "sha256"], ["path", "sha256"]);
            groups = new StudyCaseGroupsReference(StudyCaseJson.Str(groupsEl.GetProperty("path"), "groups.path"),
                StudyCaseJson.Str(groupsEl.GetProperty("sha256"), "groups.sha256"));
        }

        var instants = new List<StudyCaseInstant>();
        foreach ((JsonElement e, int i) in StudyCaseJson.Arr(root.GetProperty("instants"), "instants"))
        {
            string at = $"instants[{i}]";
            StudyCaseJson.Obj(e, at, ["id", "description", "instant"], ["id", "instant"]);
            if (e.TryGetProperty("description", out JsonElement d)) StudyCaseJson.Str(d, at + ".description");
            instants.Add(new StudyCaseInstant(StudyCaseJson.Id(e.GetProperty("id"), at + ".id"),
                PlanningTime.Parse(StudyCaseJson.Str(e.GetProperty("instant"), at + ".instant"), at + ".instant")));
        }

        var variations = new List<Variation>();
        if (root.TryGetProperty("variations", out JsonElement variationsEl))
            foreach ((JsonElement v, int vi) in StudyCaseJson.Arr(variationsEl, "variations"))
            {
                string at = $"variations[{vi}]";
                StudyCaseJson.Obj(v, at, ["id", "description", "expansionStage", "operations"], ["id", "operations"]);
                if (v.TryGetProperty("description", out JsonElement d)) StudyCaseJson.Str(d, at + ".description");
                string? stage = v.TryGetProperty("expansionStage", out JsonElement st) ? StudyCaseJson.Str(st, at + ".expansionStage") : null;
                var ops = new List<PlanningOperation>();
                foreach ((JsonElement o, int oi) in StudyCaseJson.Arr(v.GetProperty("operations"), at + ".operations"))
                    ops.Add(PlanningStudyCommand.SeamOperation(o, $"{at}.operations[{oi}]"));
                variations.Add(new Variation(StudyCaseJson.Id(v.GetProperty("id"), at + ".id"), null, stage, ops));
            }
        var order = new List<string>();
        if (root.TryGetProperty("variationOrder", out JsonElement orderEl))
            foreach ((JsonElement e, int i) in StudyCaseJson.Arr(orderEl, "variationOrder")) order.Add(StudyCaseJson.Id(e, $"variationOrder[{i}]"));

        var scenarios = new List<OperationScenario>();
        if (root.TryGetProperty("scenarios", out JsonElement scenariosEl))
            foreach ((JsonElement s, int si) in StudyCaseJson.Arr(scenariosEl, "scenarios"))
            {
                string at = $"scenarios[{si}]";
                StudyCaseJson.Obj(s, at, ["id", "description", "instants", "operations"], ["id", "operations"]);
                string? description = s.TryGetProperty("description", out JsonElement d) ? StudyCaseJson.Str(d, at + ".description") : null;
                List<string>? only = null;
                if (s.TryGetProperty("instants", out JsonElement onlyEl))
                {
                    only = [];
                    foreach ((JsonElement e, int i) in StudyCaseJson.Arr(onlyEl, at + ".instants")) only.Add(StudyCaseJson.Id(e, $"{at}.instants[{i}]"));
                }
                var ops = new List<ScenarioOperation>();
                foreach ((JsonElement o, int oi) in StudyCaseJson.Arr(s.GetProperty("operations"), at + ".operations"))
                {
                    string oa = $"{at}.operations[{oi}]";
                    StudyCaseJson.Obj(o, oa, ["id", "description", "group", "field", "action", "value"], ["id", "field", "action", "value"]);
                    if (o.TryGetProperty("description", out JsonElement od)) StudyCaseJson.Str(od, oa + ".description");
                    string fieldText = StudyCaseJson.Str(o.GetProperty("field"), oa + ".field");
                    if (!ScenarioExpander.TryParseQuantity(fieldText, out ScenarioQuantity quantity))
                        throw new StudyCaseException($"{oa}.field \"{fieldText}\" is not supported (load, generation, pdMw, qdMvar, pgMw, qgMvar, inService).");
                    string actionText = StudyCaseJson.Str(o.GetProperty("action"), oa + ".action");
                    ScenarioAction action = actionText switch
                    {
                        "scale" => ScenarioAction.Scale,
                        "set" => ScenarioAction.Set,
                        _ => throw new StudyCaseException($"{oa}.action must be scale or set, not \"{actionText}\".")
                    };
                    JsonElement ve = o.GetProperty("value");
                    PlanningValue value = ve.ValueKind switch
                    {
                        JsonValueKind.True => PlanningValue.Of(true),
                        JsonValueKind.False => PlanningValue.Of(false),
                        JsonValueKind.Number => PlanningValue.Of(StudyCaseJson.Num(ve, oa + ".value")),
                        _ => throw new StudyCaseException(oa + ".value must be a number or true/false.")
                    };
                    ops.Add(new ScenarioOperation(StudyCaseJson.Id(o.GetProperty("id"), oa + ".id"),
                        o.TryGetProperty("group", out JsonElement g) ? StudyCaseJson.Id(g, oa + ".group") : null, quantity, action, value));
                }
                scenarios.Add(new OperationScenario(StudyCaseJson.Id(s.GetProperty("id"), at + ".id"), description, ops, only));
            }

        var studies = new List<StudyCaseStudy>();
        foreach ((JsonElement s, int si) in StudyCaseJson.Arr(root.GetProperty("studies"), "studies"))
        {
            string at = $"studies[{si}]";
            StudyCaseJson.Obj(s, at, ["id", "description", "scenario", "mode", "parameters"], ["id", "mode", "parameters"]);
            string? scenario = s.TryGetProperty("scenario", out JsonElement sc) ? StudyCaseJson.Id(sc, at + ".scenario") : null;
            string mode = StudyCaseJson.Str(s.GetProperty("mode"), at + ".mode");
            if (mode == GroupSummaryStudy.ModeName)
            {
                if (s.TryGetProperty("description", out JsonElement sd)) StudyCaseJson.Str(sd, at + ".description");
                JsonElement p = s.GetProperty("parameters");
                StudyCaseJson.Obj(p, at + ".parameters", ["groups", "solve"], []);
                List<string>? ids = null;
                if (p.TryGetProperty("groups", out JsonElement gl))
                {
                    ids = [];
                    foreach ((JsonElement e, int i) in StudyCaseJson.Arr(gl, at + ".parameters.groups")) ids.Add(StudyCaseJson.Id(e, $"{at}.parameters.groups[{i}]"));
                }
                bool solve = p.TryGetProperty("solve", out JsonElement sv) && StudyCaseJson.Bool(sv, at + ".parameters.solve");
                studies.Add(new StudyCaseStudy(scenario, new GroupSummaryStudy(StudyCaseJson.Id(s.GetProperty("id"), at + ".id"), ids, solve)));
                continue;
            }
            // An RC3 mode: the planning-study parser, handed the study without the study-case-only member.
            JsonNode node = JsonNode.Parse(s.GetRawText())!;
            node.AsObject().Remove("scenario");
            using JsonDocument rc3 = JsonDocument.Parse(node.ToJsonString());
            studies.Add(new StudyCaseStudy(scenario, PlanningStudyCommand.SeamStudy(rc3.RootElement, at)));
        }
        return new StudyCaseModel(masterRef, groups, instants, new VariationSet(variations, order), scenarios, studies);
    }
}
