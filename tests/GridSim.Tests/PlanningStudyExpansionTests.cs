using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P7.1/P7.2/P7.3: typed planning-snapshot expansion over the synthetic seasonal master
/// (fixtures/planning/master). The manifest built here mirrors fixtures/planning/seasonal.json.
/// </summary>
public sealed class PlanningStudyExpansionTests
{
    internal static string FixtureDir() => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "planning");
    private const string MasterBundle = "8a6e86d2fd32b6d0899e28176e9e0778c2f9a555e69e761d6fefee00725bfa93";

    private static readonly JsonSerializerOptions ModelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    internal static string Hash(GridModel model) =>
        PlanningFingerprints.Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(model, ModelJson)));

    internal static PlanningMaster Master()
    {
        string dir = Path.Combine(FixtureDir(), "master");
        var inputs = new[] { "system.json", "buses.json", "generation.json", "demand.json", "transmission.json" }
            .Select(n => new PlanningInputFile(n, PlanningFingerprints.Sha256(File.ReadAllBytes(Path.Combine(dir, n))))).ToArray();
        return new PlanningMaster(JsonGridLoader.LoadDirectory(dir, dispatch: false), PlanningFingerprints.Bundle(inputs),
            inputs, new PlanningInterpretation(PlanningInterpretation.NativeJson, false));
    }

    private static DateTimeOffset T(string text) => PlanningTime.Parse(text, "test");
    private static PlanningTarget BranchT(int i, int f, int t, string? name = null) => new(PlanningElementKind.Branch, i, f, t, null, name);

    private static readonly DateTimeOffset WinterFrom = T("2026-11-01T00:00:00Z"), WinterTo = T("2027-03-01T00:00:00Z");

    internal static PlanningProject Winter() => new("winter-2026-27",
    [
        new("east-demand", new(PlanningElementKind.Load, null, null, null, 2, "East"), PlanningField.LoadPdMw,
            PlanningValue.Of(150.0), PlanningValue.Of(190.0), WinterFrom, WinterTo),
        new("south-schedule", new(PlanningElementKind.Generator, 1, null, null, 3, "G2 South"), PlanningField.GeneratorPgMw,
            PlanningValue.Of(100.0), PlanningValue.Of(150.0), WinterFrom, WinterTo),
        new("l12-winter-b", BranchT(0, 1, 2), PlanningField.BranchRateBMva, PlanningValue.Of(180.0), PlanningValue.Of(210.0), WinterFrom, WinterTo),
        new("l23-winter-b", BranchT(1, 2, 3), PlanningField.BranchRateBMva, PlanningValue.Of(180.0), PlanningValue.Of(210.0), WinterFrom, WinterTo),
    ]);

    internal static PlanningProject Maintenance() => new("diagonal-maintenance",
    [
        new("open-l13", BranchT(4, 1, 3, "L1-3 diagonal"), PlanningField.BranchInService, PlanningValue.Of(true),
            PlanningValue.Of(false), T("2027-06-01T00:00:00Z"), T("2027-06-08T00:00:00Z"))
    ]);

    internal static TransmissionSecurityPlanningStudy N1() => new("n-1",
        [new(0, 1, 2), new(1, 2, 3), new(2, 3, 4), new(3, 4, 1), new(4, 1, 3)], 0.9, 1.1, PostFaultRating.RateB,
        new ScopfOptions().MaxRounds, null);

    internal static PlanningManifest Seasonal(PlanningMaster master, IReadOnlyList<PlanningProject>? projects = null,
        IReadOnlyList<string>? order = null, IReadOnlyList<PlanningSnapshot>? snapshots = null) => new(
        new PlanningMasterReference("master", master.BundleSha256, master.Interpretation),
        projects ?? [Winter(), Maintenance()],
        order ?? ["winter-2026-27", "diagonal-maintenance"],
        snapshots ??
        [
            new("winter-peak", T("2027-01-15T18:00:00Z")),
            new("outage-before", T("2027-05-31T23:59:59.9999999Z")),
            new("outage-at", T("2027-06-01T01:00:00+01:00")),
            new("outage-after", T("2027-06-01T00:00:00.0000001Z")),
            new("summer", T("2027-07-15T12:00:00Z")),
        ],
        [N1()]);

    [Fact]
    public void Fixture_master_bundle_digest_is_pinned()
    {
        Assert.Equal(MasterBundle, Master().BundleSha256);
    }

    [Fact]
    public void Timestamps_need_an_explicit_offset_and_normalise_to_utc()
    {
        Assert.Equal(T("2027-06-01T00:00:00Z"), T("2027-06-01T01:00:00+01:00"));
        Assert.Equal("2027-06-01T00:00:00.0000000Z", PlanningTime.Format(T("2027-06-01T01:00:00+01:00")));
        Assert.Equal(1, (T("2027-06-01T00:00:00.0000001Z") - T("2027-06-01T00:00:00Z")).Ticks);
        foreach (string bad in new[] { "2027-06-01T00:00:00", "2027-06-01 00:00:00Z", "2027-06-01", "2027-06-01T00:00Z",
                     "2027-06-01T00:00:00.12345678Z", "2027-02-30T00:00:00Z" })
            Assert.Throws<PlanningManifestException>(() => T(bad));
        var ex = Assert.Throws<PlanningManifestException>(() => PlanningTime.Parse("2027-03-28T01:30:00", "snapshots[0].instant"));
        Assert.Contains("explicit UTC offset", ex.Message);
        Assert.Contains("snapshots[0].instant", ex.Message);
    }

    [Fact]
    public void Effective_interval_is_from_inclusive_to_exclusive()
    {
        PlanningOperation op = Maintenance().Operations[0];
        Assert.False(op.ActiveAt(T("2027-05-31T23:59:59.9999999Z")));
        Assert.True(op.ActiveAt(T("2027-06-01T00:00:00Z")));
        Assert.True(op.ActiveAt(T("2027-06-01T00:00:00.0000001Z")));
        Assert.True(op.ActiveAt(T("2027-06-07T23:59:59.9999999Z")));
        Assert.False(op.ActiveAt(T("2027-06-08T00:00:00Z")));
    }

    [Fact]
    public void Seasonal_snapshots_resolve_with_exact_audit_and_case_identities()
    {
        PlanningMaster master = Master();
        string before = Hash(master.Model);
        PlanningExpansionResult x = PlanningExpansion.Expand(master, Seasonal(master), Hash);

        Assert.Equal(["winter-peak", "outage-before", "outage-at", "outage-after", "summer"], x.Cases.Select(c => c.Snapshot.Id));
        Assert.Equal(["winter-peak/n-1", "outage-before/n-1", "outage-at/n-1", "outage-after/n-1", "summer/n-1"], x.Jobs.Select(j => j.Id));
        Assert.Equal(["001-winter-peak--n-1", "002-outage-before--n-1"], x.Jobs.Take(2).Select(j => j.Folder));
        var c = x.Cases.ToDictionary(k => k.Snapshot.Id);

        // Immutability: the master is untouched, and the expansion confirms its own identity.
        Assert.Equal(before, Hash(master.Model));
        Assert.Equal(before, x.MasterCaseSha256);
        Assert.True(master.Model.Branches[4].InService);
        Assert.Equal(150.0, master.Model.Buses.Single(b => b.Id == 2).Pd);

        // Case identities: no active change = the master itself; an expired season restores the master exactly; the
        // instant at the outage start and one tick later resolve the same case; one tick before does not.
        Assert.Equal(before, c["outage-before"].CaseSha256);
        Assert.Equal(before, c["summer"].CaseSha256);
        Assert.Same(master.Model, c["summer"].Model);
        Assert.Equal(c["outage-before"].DerivationSha256, c["summer"].DerivationSha256);
        Assert.Equal(c["outage-at"].CaseSha256, c["outage-after"].CaseSha256);
        Assert.Equal(c["outage-at"].DerivationSha256, c["outage-after"].DerivationSha256);
        Assert.NotEqual(before, c["outage-at"].CaseSha256);
        Assert.NotEqual(before, c["winter-peak"].CaseSha256);
        Assert.Equal(3, new[] { "winter-peak", "outage-before", "outage-at" }.Select(k => c[k].CaseSha256).Distinct().Count());

        // Resolved values.
        GridModel winter = c["winter-peak"].Model;
        Assert.Equal(190.0, winter.Buses.Single(b => b.Id == 2).Pd);
        Assert.Equal(150.0, winter.AllGenerators[1].Pg);
        Assert.Equal(210.0, winter.Branches[0].RateBMva);
        Assert.Equal(210.0, winter.Branches[1].RateBMva);
        Assert.Equal(180.0, winter.Branches[2].RateBMva);
        Assert.Equal(170.0, winter.AllGenerators[0].Pg);   // no balancing ran: the slack schedule is untouched
        Assert.False(c["outage-at"].Model.Branches[4].InService);
        Assert.True(c["outage-before"].Model.Branches[4].InService);

        // Audit: one entry per operation at every snapshot, applied ones sequenced in application order.
        Assert.All(x.Cases, k => Assert.Equal(5, k.Audit.Count));
        PlanningAuditEntry demand = c["winter-peak"].Audit[0];
        Assert.Equal(new PlanningAuditEntry(1, "winter-2026-27", "east-demand", "load (bus 2)", "pdMw", PlanningDisposition.Applied,
            "150", "190", "150", "150", "190", "2026-11-01T00:00:00.0000000Z", "2027-03-01T00:00:00.0000000Z"), demand);
        Assert.Equal([1, 2, 3, 4, (int?)null], c["winter-peak"].Audit.Select(a => a.Sequence));
        Assert.Equal(PlanningDisposition.NotYetEffective, c["winter-peak"].Audit[4].Disposition);
        Assert.Equal(PlanningDisposition.NotYetEffective, c["outage-before"].Audit[4].Disposition);
        Assert.Equal(PlanningDisposition.Expired, c["outage-before"].Audit[0].Disposition);
        PlanningAuditEntry open = c["outage-at"].Audit[4];
        Assert.Equal((PlanningDisposition.Applied, "true", "true", "false", 1), (open.Disposition, open.Original, open.Prior, open.Effective, open.Sequence ?? 0));
        Assert.All(c["summer"].Audit, a => Assert.Equal(PlanningDisposition.Expired, a.Disposition));
        Assert.All(c["summer"].Audit, a => Assert.Equal(a.Original, a.Effective));
        Assert.All(x.Cases, k => Assert.True(k.Validation.Valid, string.Join(" ", k.Validation.Findings)));
        Assert.Equal(4, c["outage-at"].Validation.InServiceBranches);

        // Deterministic: a second expansion gives identical identities and fingerprints.
        PlanningExpansionResult again = PlanningExpansion.Expand(master, Seasonal(master), Hash);
        Assert.Equal(x.Jobs.Select(j => (j.RequestFingerprint, j.InputFingerprint)), again.Jobs.Select(j => (j.RequestFingerprint, j.InputFingerprint)));
        Assert.Equal(x.Cases.Select(k => k.CaseSha256), again.Cases.Select(k => k.CaseSha256));
    }

    [Fact]
    public void Shadowed_change_with_a_stale_expected_prior_is_refused_naming_both_operations()
    {
        PlanningMaster master = Master();
        DateTimeOffset from = T("2027-01-01T00:00:00Z");
        var reconductor = new PlanningProject("reconductor",
            [new("r1", BranchT(0, 1, 2), PlanningField.BranchRateAMva, PlanningValue.Of(150.0), PlanningValue.Of(200.0), from, null)]);
        var uprate = new PlanningProject("feeder-uprate",
            [new("f1", BranchT(0, 1, 2), PlanningField.BranchRateAMva, PlanningValue.Of(150.0), PlanningValue.Of(180.0), from, null)]);
        PlanningSnapshot[] feb = [new("feb", T("2027-02-01T00:00:00Z"))];

        var shadowed = Assert.Throws<PlanningConflictException>(() => PlanningExpansion.Expand(master,
            Seasonal(master, [reconductor, uprate], ["reconductor", "feeder-uprate"], feb), Hash));
        Assert.Equal("Snapshot 'feb' at 2027-02-01T00:00:00.0000000Z: operation 'feeder-uprate/f1' expects rateAMva of " +
            "branch 0 (1-2) to be 150 but operation 'reconductor/r1', applied earlier by projectOrder, set it to 200. " +
            "The change is shadowed; correct its expectedPrior or the project order.", shadowed.Message);
        Assert.Equal(("feb", "feeder-uprate/f1", "reconductor/r1"), (shadowed.Snapshot, shadowed.Operation, shadowed.OtherOperation));

        var unordered = Assert.Throws<PlanningConflictException>(() => PlanningExpansion.Expand(master,
            Seasonal(master, [reconductor, uprate], ["reconductor"], feb), Hash));
        Assert.Equal("Snapshot 'feb' at 2027-02-01T00:00:00.0000000Z: operations 'reconductor/r1' and 'feeder-uprate/f1' " +
            "both change rateAMva of branch 0 (1-2) while active; list both projects in projectOrder to define which " +
            "applies first.", unordered.Message);

        // Before either is active there is no conflict; a correct chain (expectedPrior = earlier value) applies in order.
        PlanningExpansion.Expand(master, Seasonal(master, [reconductor, uprate], [], [new("dec", T("2026-12-31T23:59:59Z"))]), Hash);
        var chained = new PlanningProject("feeder-uprate",
            [new("f1", BranchT(0, 1, 2), PlanningField.BranchRateAMva, PlanningValue.Of(200.0), PlanningValue.Of(180.0), from, null)]);
        PlanningResolvedCase ok = PlanningExpansion.Expand(master,
            Seasonal(master, [reconductor, chained], ["reconductor", "feeder-uprate"], feb), Hash).Cases[0];
        Assert.Equal(180.0, ok.Model.Branches[0].RateAMva);
        Assert.Equal(("150", "200", "180"), (ok.Audit[1].Original, ok.Audit[1].Prior, ok.Audit[1].Effective));

        // Reversing the declared order reverses which change is shadowed.
        var reversed = Assert.Throws<PlanningConflictException>(() => PlanningExpansion.Expand(master,
            Seasonal(master, [reconductor, chained], ["feeder-uprate", "reconductor"], feb), Hash));
        Assert.Contains("operation 'feeder-uprate/f1' expects rateAMva of branch 0 (1-2) to be 200 but the master holds 150.", reversed.Message);
    }

    [Fact]
    public void Invalid_operations_are_refused_before_any_snapshot_is_resolved()
    {
        PlanningMaster master = Master();
        DateTimeOffset from = T("2027-01-01T00:00:00Z");
        void Refuse(PlanningOperation op, string expected)
        {
            var ex = Assert.Throws<PlanningManifestException>(() =>
                PlanningExpansion.Expand(master, Seasonal(master, [new("p", [op])], []), Hash));
            Assert.Contains(expected, ex.Message);
        }
        Refuse(new("x", BranchT(0, 2, 1), PlanningField.BranchRateAMva, PlanningValue.Of(150.0), PlanningValue.Of(1.0), from, null),
            "branch 0 joins 1-2 in the master, not 2-1");
        Refuse(new("x", BranchT(9, 1, 2), PlanningField.BranchRateAMva, PlanningValue.Of(150.0), PlanningValue.Of(1.0), from, null),
            "branch index 9 is outside the master");
        Refuse(new("x", BranchT(0, 1, 2, "wrong"), PlanningField.BranchRateAMva, PlanningValue.Of(150.0), PlanningValue.Of(1.0), from, null),
            "named 'L1-2' in the master");
        Refuse(new("x", BranchT(0, 1, 2), PlanningField.BranchRateAMva, PlanningValue.Of(150.0), PlanningValue.Of(0.0), from, null),
            "normal rating (rateAMva) must be positive");
        Refuse(new("x", BranchT(0, 1, 2), PlanningField.BranchInService, PlanningValue.Of(150.0), PlanningValue.Of(false), from, null),
            "expectedPrior must be true or false");
        Refuse(new("x", BranchT(0, 1, 2), PlanningField.LoadPdMw, PlanningValue.Of(150.0), PlanningValue.Of(1.0), from, null),
            "field pdMw does not apply to a branch");
        Refuse(new("x", new(PlanningElementKind.Generator, 1, null, null, 3, null), PlanningField.GeneratorPgMw,
            PlanningValue.Of(100.0), PlanningValue.Of(301.0), from, null), "pgMw 301 is outside the unit's range [0, 300] MW");
        Refuse(new("x", new(PlanningElementKind.Generator, 1, null, null, 1, null), PlanningField.GeneratorPgMw,
            PlanningValue.Of(100.0), PlanningValue.Of(120.0), from, null), "generator 1 is at bus 3 in the master, not 1");
        Refuse(new("x", new(PlanningElementKind.Load, null, null, null, 7, null), PlanningField.LoadPdMw,
            PlanningValue.Of(0.0), PlanningValue.Of(1.0), from, null), "bus 7 is not in the master");
        Refuse(new("x", BranchT(0, 1, 2), PlanningField.BranchRateAMva, PlanningValue.Of(150.0), PlanningValue.Of(1.0), from, from),
            "effectiveTo must be later than effectiveFrom");

        var dup = Assert.Throws<PlanningManifestException>(() => PlanningExpansion.Expand(master,
            Seasonal(master, null, ["winter-2026-27", "winter-2026-27"]), Hash));
        Assert.Contains("Duplicate projectOrder entry ID 'winter-2026-27'", dup.Message);
        var unknown = Assert.Throws<PlanningManifestException>(() => PlanningExpansion.Expand(master,
            Seasonal(master, null, ["nope"]), Hash));
        Assert.Contains("projectOrder names 'nope'", unknown.Message);
    }

    [Fact]
    public void Islanding_planned_outages_make_an_invalid_case_that_never_reaches_the_study()
    {
        PlanningMaster master = Master();
        DateTimeOffset from = T("2027-01-01T00:00:00Z");
        // Bus 4 hangs on branches 2 (3-4) and 3 (4-1): opening both islands it.
        var isolate = new PlanningProject("isolate-west",
        [
            new("open-34", BranchT(2, 3, 4), PlanningField.BranchInService, PlanningValue.Of(true), PlanningValue.Of(false), from, null),
            new("open-41", BranchT(3, 4, 1), PlanningField.BranchInService, PlanningValue.Of(true), PlanningValue.Of(false), from, null),
        ]);
        PlanningExpansionResult x = PlanningExpansion.Expand(master,
            Seasonal(master, [isolate], [], [new("feb", T("2027-02-01T00:00:00Z"))]), Hash);
        Assert.False(x.Cases[0].Validation.Valid);
        Assert.Equal(2, x.Cases[0].Validation.Islands);
        PlanningJobResult result = PlanningStudyModes.RunJob(x.Jobs[0]);
        Assert.Equal(("INVALID CASE", "Invalid"), (result.Status, result.Outcome));
        Assert.Contains("form 2 islands", result.Reason);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public void Planned_outage_keeps_an_explicit_not_applicable_row_and_jobs_repeat_bit_for_bit()
    {
        PlanningMaster master = Master();
        PlanningExpansionResult x = PlanningExpansion.Expand(master, Seasonal(master), Hash);
        PlanningJobResult[] first = x.Jobs.Select(PlanningStudyModes.RunJob).ToArray();
        PlanningJobResult[] second = x.Jobs.Select(PlanningStudyModes.RunJob).ToArray();
        var opts = new JsonSerializerOptions(ModelJson) { WriteIndented = false };
        Assert.Equal(first.Select(r => JsonSerializer.Serialize<object>(r, opts)), second.Select(r => JsonSerializer.Serialize<object>(r, opts)));

        var at = (TransmissionSecurityPlanningEvidence)first[2].Evidence!;
        Assert.Equal(5, at.RequestedOutages.Count);
        Assert.Equal(("NOT APPLICABLE", "Branch is out of service in this snapshot."), (at.RequestedOutages[4].Applicability, at.RequestedOutages[4].Reason));
        Assert.Equal([0, 1, 2, 3], at.EffectiveRequest!.Outages.Select(o => o.Index));
        Assert.Equal(4, at.Result!.Outages.Count);
        var before = (TransmissionSecurityPlanningEvidence)first[1].Evidence!;
        Assert.All(before.RequestedOutages, r => Assert.Equal("REQUESTED", r.Applicability));
        Assert.Equal(5, before.Result!.Outages.Count);

        // Identical resolved cases give identical study results; the summer job reproduces the pre-outage job.
        Assert.Equal(JsonSerializer.Serialize<object>(first[2].Evidence!, opts), JsonSerializer.Serialize<object>(first[3].Evidence!, opts));
        Assert.Equal(JsonSerializer.Serialize<object>(first[1].Evidence!, opts), JsonSerializer.Serialize<object>(first[4].Evidence!, opts));

        // The study runs through the unaltered P1 runner: same request on the resolved model gives the same result.
        TransmissionSecurityResult direct = TransmissionSecurityRunner.Run(x.Cases[2].Model, at.EffectiveRequest);
        Assert.Equal(JsonSerializer.Serialize(direct, opts), JsonSerializer.Serialize(at.Result, opts));
        Assert.Equal(PlanningStudyExpectations.Statuses, first.Select(r => (r.Status, r.Outcome)));

        // The binding evidence: only the post-fault rating-B limits named above fail, by under 1.5 MVA.
        LimitEvidence[] breaches = at.Result.Outages.SelectMany(o => o.Limits.Where(l => l.Violated)
            .Select(l => l with { Source = o.OutageIndex + ":" + l.Source })).ToArray();
        Assert.Equal([("BranchFrom", 2, "1:B"), ("BranchTo", 1, "2:B")], breaches.Select(l => (l.Kind, l.Index, l.Source!)));
        Assert.All(breaches, l => Assert.InRange(l.Observed - 180.0, 0.5, 1.5));
        Assert.All(breaches, l => Assert.Equal(180.0, l.Upper));
        Assert.DoesNotContain(at.Result.BaseCase.Limits, l => l.Violated);
    }
}

/// <summary>The pinned verdicts of the seasonal fixture, shared by the Core and process tests.</summary>
internal static class PlanningStudyExpectations
{
    internal static readonly (string, string)[] Statuses =
    [
        ("SECURE", "Verified"),   // winter-peak
        ("SECURE", "Verified"),   // outage-before
        // With the diagonal out the network is a 4-bus ring. Losing 2-3 (or 3-4) leaves the remaining paths radial; the
        // DC candidate loads 3-4 (or 2-3) to exactly its 180 MVA rating B on MW alone, and the AC MVA, which adds the
        // reactive flow, exceeds it: the P1 "DC-feasible, AC rejects" class, now caused by the planned outage.
        ("NOT SECURE", "Violations"),   // outage-at
        ("NOT SECURE", "Violations"),   // outage-after
        ("SECURE", "Verified"),   // summer
    ];

    /// <summary>Verdicts of the three-mode manifest (outage-before then outage-at; n-1, south-north transfer, n-1-1).
    /// Before the outage the meshed network carries the whole 150 MW transfer cap and both sequences are secure. With
    /// the diagonal open the transfer is bracketed at 78.125-81.25 MW, limited by branch 1 (2-3) at rating B after
    /// outage 2 (3-4); and N-1-1 sequence 2 (lose 1-2, then 3-4) splits the remaining ring into {1,4} and {2,3}, so its
    /// final state is unevaluated (Incomplete), while sequence 1 needs the opened diagonal (NOT APPLICABLE).</summary>
    internal static readonly (string, string)[] Modes =
    [
        ("SECURE", "Verified"), ("VERIFIED", "AtLeastMax"), ("SECURE", "Verified"),
        ("NOT SECURE", "Violations"), ("QUALIFIED", "Limited"), ("NOT SECURE", "Incomplete"),
    ];
}
