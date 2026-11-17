using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.PlanningStudies;

/// <summary>
/// Deterministic expansion of a planning manifest into resolved cases. Order of operations (each step is complete
/// before the next starts, and a refusal stops the whole expansion before any study runs):
/// <list type="number">
/// <item>Validate the manifest against the immutable master: unique IDs, every target resolves to the stated
/// identity, field and value types agree, replacement values are physical, intervals are ordered, the project order
/// names each project at most once, every study binds to the master.</item>
/// <item>For each snapshot in declared order: select the operations active at its instant (from inclusive, to
/// exclusive); refuse two active projects changing the same field unless both are in the project order; order the
/// active operations (projects in project order, then unlisted projects in declaration order; operations in
/// authoring order); apply them one at a time to a copy of the master, checking each expected prior exactly against
/// the value it replaces; record an audit entry for every operation, active or not.</item>
/// <item>Validate topology and operating limits of each resolved case.</item>
/// <item>Confirm the master's content identity is unchanged, then form the jobs (snapshot-major, declared order).</item>
/// </list>
/// No merit-order balancing or other loader step runs between operations; only the declared fields change.
/// </summary>
public static class PlanningExpansion
{
    public static PlanningExpansionResult Expand(PlanningMaster master, PlanningManifest manifest,
        Func<GridModel, string> caseHasher)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(caseHasher);
        if (master.Interpretation.Dispatch || master.Interpretation.Loader != PlanningInterpretation.NativeJson)
            throw new PlanningManifestException("Only the native-json interpretation with dispatch false is supported.");
        string masterCase = caseHasher(master.Model);
        ValidateManifest(master.Model, manifest);

        var cases = new List<PlanningResolvedCase>(manifest.Snapshots.Count);
        foreach (PlanningSnapshot snapshot in manifest.Snapshots)
            cases.Add(Resolve(master, manifest, snapshot, caseHasher));

        // Immutability: the master must be bit-for-bit what it was before any snapshot was built.
        if (caseHasher(master.Model) != masterCase)
            throw new InvalidOperationException("The master model changed during expansion.");

        var jobs = new List<PlanningJob>();
        foreach (PlanningResolvedCase resolved in cases)
            foreach (PlanningStudyDefinition study in manifest.Studies)
            {
                string id = resolved.Snapshot.Id + "/" + study.Id;
                jobs.Add(new PlanningJob(jobs.Count + 1, id, resolved, study,
                    PlanningFingerprints.Request(id, resolved.Snapshot, study, resolved.DerivationSha256),
                    PlanningFingerprints.Input(master.MasterSha256, resolved.CaseSha256)));
            }
        return new PlanningExpansionResult(master.MasterSha256, masterCase, cases, jobs);
    }

    // ---- 1. Manifest validation against the master -------------------------------------------------------------

    private static void ValidateManifest(GridModel model, PlanningManifest manifest)
    {
        if (manifest.Snapshots.Count == 0) throw new PlanningManifestException("At least one snapshot is required.");
        if (manifest.Studies.Count == 0) throw new PlanningManifestException("At least one study is required.");
        Unique(manifest.Projects.Select(p => p.Id), "project");
        Unique(manifest.Snapshots.Select(s => s.Id), "snapshot");
        Unique(manifest.Studies.Select(s => s.Id), "study");
        Unique(manifest.ProjectOrder, "projectOrder entry");
        foreach (string id in manifest.ProjectOrder)
            if (manifest.Projects.All(p => p.Id != id))
                throw new PlanningManifestException($"projectOrder names '{id}', which is not a declared project.");
        foreach (PlanningProject project in manifest.Projects)
        {
            Unique(project.Operations.Select(o => o.Id), $"operation in project '{project.Id}'");
            foreach (PlanningOperation op in project.Operations)
                ValidateOperation(model, project.Id + "/" + op.Id, op);
        }
        foreach (PlanningStudyDefinition study in manifest.Studies)
            ValidateStudy(model, study);
    }

    private static void Unique(IEnumerable<string> ids, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
            if (!seen.Add(id)) throw new PlanningManifestException($"Duplicate {what} ID '{id}'.");
    }

    private static void ValidateOperation(GridModel model, string name, PlanningOperation op)
    {
        if (op.EffectiveTo is DateTimeOffset to && to <= op.EffectiveFrom)
            throw new PlanningManifestException($"Operation '{name}': effectiveTo must be later than effectiveFrom.");
        PlanningElementKind kind = KindOf(op.Field);
        if (op.Target.Kind != kind)
            throw new PlanningManifestException($"Operation '{name}': field {FieldName(op.Field)} does not apply to a {op.Target.Kind.ToString().ToLowerInvariant()}.");
        ResolveTarget(model, op.Target, name);
        bool flag = op.Field == PlanningField.BranchInService;
        foreach ((PlanningValue v, string label) in new[] { (op.ExpectedPrior, "expectedPrior"), (op.Value, "value") })
        {
            if (v.IsFlag != flag)
                throw new PlanningManifestException($"Operation '{name}': {label} must be {(flag ? "true or false" : "a number")} for {FieldName(op.Field)}.");
            if (!flag && !double.IsFinite(v.Number!.Value))
                throw new PlanningManifestException($"Operation '{name}': {label} must be finite.");
        }
        if (flag) return;
        double value = op.Value.Number!.Value;
        switch (op.Field)
        {
            case PlanningField.BranchRateAMva when value <= 0:
                throw new PlanningManifestException($"Operation '{name}': a normal rating (rateAMva) must be positive.");
            case PlanningField.BranchRateBMva or PlanningField.BranchRateCMva when value < 0:
                throw new PlanningManifestException($"Operation '{name}': an emergency rating cannot be negative (0 means not given).");
            case PlanningField.GeneratorPgMw:
                Generator unit = model.AllGenerators[op.Target.Index!.Value];
                if (value < unit.Pmin || value > unit.Pmax)
                    throw new PlanningManifestException(
                        $"Operation '{name}': pgMw {PlanningValue.Of(value)} is outside the unit's range [{PlanningValue.Of(unit.Pmin)}, {PlanningValue.Of(unit.Pmax)}] MW.");
                break;
            case PlanningField.GeneratorQgMvar:
                Generator q = model.AllGenerators[op.Target.Index!.Value];
                if (value < q.Qmin || value > q.Qmax)
                    throw new PlanningManifestException(
                        $"Operation '{name}': qgMvar {PlanningValue.Of(value)} is outside the unit's range [{PlanningValue.Of(q.Qmin)}, {PlanningValue.Of(q.Qmax)}] MVAr.");
                break;
        }
    }

    private static void ResolveTarget(GridModel model, PlanningTarget target, string name)
    {
        switch (target.Kind)
        {
            case PlanningElementKind.Branch:
                if (target.Index is not int b || target.From is not int from || target.To is not int to || target.Bus is not null)
                    throw new PlanningManifestException($"Operation '{name}': a branch target needs index, from and to (and no bus).");
                if (b < 0 || b >= model.Branches.Count)
                    throw new PlanningManifestException($"Operation '{name}': branch index {b} is outside the master (0..{model.Branches.Count - 1}).");
                Branch branch = model.Branches[b];
                if (branch.From != from || branch.To != to)
                    throw new PlanningManifestException($"Operation '{name}': branch {b} joins {branch.From}-{branch.To} in the master, not {from}-{to}.");
                if (target.Name is not null && branch.Name != target.Name)
                    throw new PlanningManifestException($"Operation '{name}': branch {b} is named '{branch.Name}' in the master, not '{target.Name}'.");
                break;
            case PlanningElementKind.Generator:
                if (target.Index is not int g || target.Bus is not int bus || target.From is not null || target.To is not null)
                    throw new PlanningManifestException($"Operation '{name}': a generator target needs index and bus (and no from/to).");
                if (g < 0 || g >= model.AllGenerators.Count)
                    throw new PlanningManifestException($"Operation '{name}': generator index {g} is outside the master fleet (0..{model.AllGenerators.Count - 1}).");
                Generator unit = model.AllGenerators[g];
                if (unit.Bus != bus)
                    throw new PlanningManifestException($"Operation '{name}': generator {g} is at bus {unit.Bus} in the master, not {bus}.");
                if (target.Name is not null && unit.Name != target.Name)
                    throw new PlanningManifestException($"Operation '{name}': generator {g} is named '{unit.Name}' in the master, not '{target.Name}'.");
                break;
            default:
                if (target.Bus is not int loadBus || target.Index is not null || target.From is not null || target.To is not null)
                    throw new PlanningManifestException($"Operation '{name}': a load target needs bus only.");
                Bus? found = model.Buses.FirstOrDefault(x => x.Id == loadBus);
                if (found is null)
                    throw new PlanningManifestException($"Operation '{name}': bus {loadBus} is not in the master.");
                if (target.Name is not null && found.Name != target.Name)
                    throw new PlanningManifestException($"Operation '{name}': bus {loadBus} is named '{found.Name}' in the master, not '{target.Name}'.");
                break;
        }
    }

    private static void ValidateStudy(GridModel model, PlanningStudyDefinition study)
    {
        switch (study)
        {
            case TransmissionSecurityPlanningStudy ts:
                if (ts.Outages.Count == 0)
                    throw new PlanningManifestException($"Study '{ts.Id}': at least one explicit branch outage is required.");
                var seen = new HashSet<int>();
                foreach (BranchOutage o in ts.Outages)
                {
                    if (o.Index < 0 || o.Index >= model.Branches.Count || !seen.Add(o.Index))
                        throw new PlanningManifestException($"Study '{ts.Id}': outage indices must be unique and within the master.");
                    Branch b = model.Branches[o.Index];
                    if (b.From != o.From || b.To != o.To)
                        throw new PlanningManifestException($"Study '{ts.Id}': outage {o.Index} joins {b.From}-{b.To} in the master, not {o.From}-{o.To}.");
                }
                if (!double.IsFinite(ts.MinPu) || !double.IsFinite(ts.MaxPu) || ts.MinPu <= 0 || ts.MaxPu < ts.MinPu)
                    throw new PlanningManifestException($"Study '{ts.Id}': voltage bounds must be finite, positive and ordered.");
                if (ts.MaxRounds < 1 || !Enum.IsDefined(ts.PostFaultRating))
                    throw new PlanningManifestException($"Study '{ts.Id}': invalid DC round cap or post-fault rating.");
                if (ts.Costs is not null && (ts.Costs.Count != model.Generators.Count || ts.Costs.Any(c => !double.IsFinite(c))))
                    throw new PlanningManifestException($"Study '{ts.Id}': costs must hold one finite value per in-service unit.");
                break;
            case TransferPlanningStudy tr:
                // Identity binding only; the transfer runner's own validation (weights, search bounds) runs per job.
                BindOutages(model, tr.Id, tr.Request.Outages, unique: true);
                if (tr.Request.Sources.Count == 0 || tr.Request.Sinks.Count == 0)
                    throw new PlanningManifestException($"Study '{tr.Id}': transfer needs at least one source and one sink unit.");
                foreach (TransferParticipant p in tr.Request.Sources.Concat(tr.Request.Sinks))
                    BindUnit(model, tr.Id, p.Index, p.Bus);
                foreach (TransferInterface i in tr.Request.Interfaces ?? [])
                    BindOutages(model, tr.Id, i.Branches.Select(b => new BranchOutage(b.Index, b.From, b.To)).ToArray(), unique: false);
                break;
            case CorrectiveN11PlanningStudy cn:
                if (cn.Request.Sequences.Count == 0)
                    throw new PlanningManifestException($"Study '{cn.Id}': at least one ordered outage sequence is required.");
                BindOutages(model, cn.Id, cn.Request.Sequences.SelectMany(q => new[] { q.First, q.Second }).ToArray(), unique: false);
                foreach (GeneratorActionEnvelope e in cn.Request.Policy.Eligible) BindUnit(model, cn.Id, e.Generator, e.Bus);
                foreach (GeneratorAction a in cn.Request.Sequences.SelectMany(q => q.Actions ?? [])) BindUnit(model, cn.Id, a.Generator, a.Bus);
                break;
            default:
                throw new PlanningManifestException($"Study '{study.Id}': mode '{study.Mode}' is not available in this build.");
        }
    }

    private static void BindOutages(GridModel model, string study, IReadOnlyList<BranchOutage> outages, bool unique)
    {
        if (outages.Count == 0)
            throw new PlanningManifestException($"Study '{study}': at least one explicit branch outage is required.");
        var seen = new HashSet<int>();
        foreach (BranchOutage o in outages)
        {
            if (o.Index < 0 || o.Index >= model.Branches.Count || (unique && !seen.Add(o.Index)))
                throw new PlanningManifestException($"Study '{study}': branch indices must be {(unique ? "unique and " : "")}within the master.");
            Branch b = model.Branches[o.Index];
            if (b.From != o.From || b.To != o.To)
                throw new PlanningManifestException($"Study '{study}': branch {o.Index} joins {b.From}-{b.To} in the master, not {o.From}-{o.To}.");
        }
    }

    /// <summary>Study unit identities use the in-service unit list (the convention of the P1, P4 and P5 runners).</summary>
    private static void BindUnit(GridModel model, string study, int index, int bus)
    {
        if (index < 0 || index >= model.Generators.Count)
            throw new PlanningManifestException($"Study '{study}': unit index {index} is outside the in-service unit list.");
        if (model.Generators[index].Bus != bus)
            throw new PlanningManifestException($"Study '{study}': unit {index} is at bus {model.Generators[index].Bus} in the master, not {bus}.");
    }

    // ---- 2. Resolve one snapshot -----------------------------------------------------------------------------------

    private sealed record Candidate(PlanningProject Project, int ProjectRank, int OperationRank, PlanningOperation Op)
    {
        public string Name => Project.Id + "/" + Op.Id;
    }

    private static PlanningResolvedCase Resolve(PlanningMaster master, PlanningManifest manifest,
        PlanningSnapshot snapshot, Func<GridModel, string> caseHasher)
    {
        GridModel source = master.Model;
        string at = $"Snapshot '{snapshot.Id}' at {PlanningTime.Format(snapshot.Instant)}";
        var order = manifest.ProjectOrder.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
        int declared = 0;
        var all = new List<Candidate>();
        foreach (PlanningProject project in manifest.Projects)
        {
            int rank = order.TryGetValue(project.Id, out int r) ? r : order.Count + declared;
            declared++;
            for (int i = 0; i < project.Operations.Count; i++)
                all.Add(new Candidate(project, rank, i, project.Operations[i]));
        }
        List<Candidate> active = all.Where(c => c.Op.ActiveAt(snapshot.Instant))
            .OrderBy(c => c.ProjectRank).ThenBy(c => c.OperationRank).ToList();

        // Same-field conflicts between projects need an explicit order for every project involved.
        foreach (var group in active.GroupBy(c => (c.Op.Target.Id, c.Op.Field)))
        {
            Candidate[] items = group.ToArray();
            for (int i = 0; i < items.Length; i++)
                for (int j = i + 1; j < items.Length; j++)
                {
                    if (items[i].Project.Id == items[j].Project.Id) continue;
                    if (order.ContainsKey(items[i].Project.Id) && order.ContainsKey(items[j].Project.Id)) continue;
                    throw new PlanningConflictException(
                        $"{at}: operations '{items[i].Name}' and '{items[j].Name}' both change {FieldName(group.Key.Field)} " +
                        $"of {group.Key.Id} while active; list both projects in projectOrder to define which applies first.",
                        snapshot.Id, items[i].Name, items[j].Name);
                }
        }

        Bus[] buses = source.Buses.ToArray();
        Generator[] fleet = source.AllGenerators.ToArray();
        Branch[] branches = source.Branches.ToArray();
        var lastWriter = new Dictionary<(string, PlanningField), Candidate>();
        var appliedAudit = new Dictionary<Candidate, PlanningAuditEntry>();
        int sequence = 0;
        foreach (Candidate c in active)
        {
            PlanningOperation op = c.Op;
            PlanningValue original = Read(source.Buses, source.AllGenerators, source.Branches, op.Target, op.Field);
            PlanningValue prior = Read(buses, fleet, branches, op.Target, op.Field);
            if (!prior.SameAs(op.ExpectedPrior))
            {
                if (lastWriter.TryGetValue((op.Target.Id, op.Field), out Candidate? earlier))
                    throw new PlanningConflictException(
                        $"{at}: operation '{c.Name}' expects {FieldName(op.Field)} of {op.Target.Id} to be {op.ExpectedPrior} " +
                        $"but operation '{earlier.Name}', applied earlier by {(earlier.Project.Id == c.Project.Id ? "authoring order" : "projectOrder")}, " +
                        $"set it to {prior}. The change is shadowed; correct its expectedPrior or the project order.",
                        snapshot.Id, c.Name, earlier.Name);
                throw new PlanningConflictException(
                    $"{at}: operation '{c.Name}' expects {FieldName(op.Field)} of {op.Target.Id} to be {op.ExpectedPrior} " +
                    $"but the master holds {prior}.", snapshot.Id, c.Name, "master");
            }
            Write(buses, fleet, branches, op.Target, op.Field, op.Value);
            lastWriter[(op.Target.Id, op.Field)] = c;
            appliedAudit[c] = new PlanningAuditEntry(++sequence, c.Project.Id, op.Id, op.Target.Id, FieldName(op.Field),
                PlanningDisposition.Applied, op.ExpectedPrior.ToString(), op.Value.ToString(), original.ToString(),
                prior.ToString(), op.Value.ToString(), PlanningTime.Format(op.EffectiveFrom),
                op.EffectiveTo is null ? null : PlanningTime.Format(op.EffectiveTo.Value));
        }

        // Audit: every operation, in manifest declaration order, so the list has the same shape at every snapshot.
        var audit = new List<PlanningAuditEntry>(all.Count);
        foreach (Candidate c in all)
        {
            if (appliedAudit.TryGetValue(c, out PlanningAuditEntry? entry)) { audit.Add(entry); continue; }
            string master0 = Read(source.Buses, source.AllGenerators, source.Branches, c.Op.Target, c.Op.Field).ToString();
            audit.Add(new PlanningAuditEntry(null, c.Project.Id, c.Op.Id, c.Op.Target.Id, FieldName(c.Op.Field),
                snapshot.Instant < c.Op.EffectiveFrom ? PlanningDisposition.NotYetEffective : PlanningDisposition.Expired,
                c.Op.ExpectedPrior.ToString(), c.Op.Value.ToString(), master0, master0, master0,
                PlanningTime.Format(c.Op.EffectiveFrom), c.Op.EffectiveTo is null ? null : PlanningTime.Format(c.Op.EffectiveTo.Value)));
        }

        GridModel resolved = sequence == 0 ? source : source.CopyWith(buses: buses, generators: fleet,
            branches: branches, generatorsAreWholeFleet: true);
        PlanningAuditEntry[] applied = audit.Where(a => a.Disposition == PlanningDisposition.Applied)
            .OrderBy(a => a.Sequence).ToArray();
        return new PlanningResolvedCase(snapshot, resolved, caseHasher(resolved),
            PlanningFingerprints.Derivation(master.MasterSha256, applied), audit, Validate(resolved));
    }

    private static PlanningValue Read(IReadOnlyList<Bus> buses, IReadOnlyList<Generator> fleet,
        IReadOnlyList<Branch> branches, PlanningTarget target, PlanningField field) => field switch
    {
        PlanningField.BranchInService => PlanningValue.Of(branches[target.Index!.Value].InService),
        PlanningField.BranchRateAMva => PlanningValue.Of(branches[target.Index!.Value].RateAMva),
        PlanningField.BranchRateBMva => PlanningValue.Of(branches[target.Index!.Value].RateBMva),
        PlanningField.BranchRateCMva => PlanningValue.Of(branches[target.Index!.Value].RateCMva),
        PlanningField.GeneratorPgMw => PlanningValue.Of(fleet[target.Index!.Value].Pg),
        PlanningField.GeneratorQgMvar => PlanningValue.Of(fleet[target.Index!.Value].Qg),
        PlanningField.LoadPdMw => PlanningValue.Of(buses.First(b => b.Id == target.Bus).Pd),
        PlanningField.LoadQdMvar => PlanningValue.Of(buses.First(b => b.Id == target.Bus).Qd),
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static void Write(Bus[] buses, Generator[] fleet, Branch[] branches, PlanningTarget target,
        PlanningField field, PlanningValue value)
    {
        switch (field)
        {
            case PlanningField.BranchInService: Edit(branches, target, b => b with { InService = value.Flag!.Value }); break;
            case PlanningField.BranchRateAMva: Edit(branches, target, b => b with { RateAMva = value.Number!.Value }); break;
            case PlanningField.BranchRateBMva: Edit(branches, target, b => b with { RateBMva = value.Number!.Value }); break;
            case PlanningField.BranchRateCMva: Edit(branches, target, b => b with { RateCMva = value.Number!.Value }); break;
            case PlanningField.GeneratorPgMw: fleet[target.Index!.Value] = fleet[target.Index!.Value] with { Pg = value.Number!.Value }; break;
            case PlanningField.GeneratorQgMvar: fleet[target.Index!.Value] = fleet[target.Index!.Value] with { Qg = value.Number!.Value }; break;
            case PlanningField.LoadPdMw: EditBus(buses, target, b => b with { Pd = value.Number!.Value }); break;
            case PlanningField.LoadQdMvar: EditBus(buses, target, b => b with { Qd = value.Number!.Value }); break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private static void Edit(Branch[] branches, PlanningTarget target, Func<Branch, Branch> change) =>
        branches[target.Index!.Value] = change(branches[target.Index!.Value]);
    private static void EditBus(Bus[] buses, PlanningTarget target, Func<Bus, Bus> change)
    {
        int i = Array.FindIndex(buses, b => b.Id == target.Bus);
        buses[i] = change(buses[i]);
    }

    // ---- 3. Topology and operating-limit validation ----------------------------------------------------------------

    /// <summary>Checks a resolved case before a study sees it: one connected island over the live branches, finite
    /// non-negative ratings with a positive normal rating on every live branch, and finite demand. Replacement unit
    /// setpoints are checked against the unit's own range when the manifest is validated, before any expansion.</summary>
    public static PlanningCaseValidation Validate(GridModel model)
    {
        var findings = new List<string>();
        int? islands = null;
        try { islands = DistributionFactors.FindIslands(model).Count; }
        catch (InvalidOperationException ex) { findings.Add("Topology: " + ex.Message); }
        if (islands is int n && n != 1)
            findings.Add($"Topology: the live branches form {n} islands; a planning case must be one connected network.");
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch b = model.Branches[i];
            if (!double.IsFinite(b.RateAMva) || !double.IsFinite(b.RateBMva) || !double.IsFinite(b.RateCMva) ||
                b.RateAMva < 0 || b.RateBMva < 0 || b.RateCMva < 0)
                findings.Add($"Branch {i} ({b.From}-{b.To}): ratings must be finite and non-negative.");
            else if (b.InService && b.RateAMva <= 0)
                findings.Add($"Branch {i} ({b.From}-{b.To}): a live branch needs a positive normal rating.");
        }
        foreach (Bus bus in model.Buses)
            if (!double.IsFinite(bus.Pd) || !double.IsFinite(bus.Qd))
                findings.Add($"Bus {bus.Id}: demand must be finite.");
        return new PlanningCaseValidation(findings.Count == 0, islands, model.Branches.Count(b => b.InService), findings);
    }

    // ---- Names -------------------------------------------------------------------------------------------------------

    public static PlanningElementKind KindOf(PlanningField field) => field switch
    {
        PlanningField.BranchInService or PlanningField.BranchRateAMva or PlanningField.BranchRateBMva
            or PlanningField.BranchRateCMva => PlanningElementKind.Branch,
        PlanningField.GeneratorPgMw or PlanningField.GeneratorQgMvar => PlanningElementKind.Generator,
        _ => PlanningElementKind.Load
    };

    /// <summary>The manifest spelling of each field.</summary>
    public static string FieldName(PlanningField field) => field switch
    {
        PlanningField.BranchInService => "inService",
        PlanningField.BranchRateAMva => "rateAMva",
        PlanningField.BranchRateBMva => "rateBMva",
        PlanningField.BranchRateCMva => "rateCMva",
        PlanningField.GeneratorPgMw => "pgMw",
        PlanningField.GeneratorQgMvar => "qgMvar",
        PlanningField.LoadPdMw => "pdMw",
        _ => "qdMvar"
    };

    public static bool TryParseField(string text, out PlanningField field)
    {
        foreach (PlanningField f in Enum.GetValues<PlanningField>())
            if (FieldName(f) == text) { field = f; return true; }
        field = default;
        return false;
    }
}
