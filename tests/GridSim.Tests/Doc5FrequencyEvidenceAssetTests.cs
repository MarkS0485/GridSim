using System.Globalization;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Events;
using GridSim.Core.Evidence;
using GridSim.Core.Fleet;
using GridSim.Core.Frequency;
using GridSim.Core.Model;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// DOC-5 (October 2026): the documentation review's frequency, evidence and asset-dynamics findings (stream 5 of
/// review-progress/2026-10/DOCS-FINDINGS-TRIAGE.md) and N235, one region per id. Every test here is new.
/// </summary>
public sealed class Doc5FrequencyEvidenceAssetTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-doc5-" + Guid.NewGuid().ToString("N"));

    public Doc5FrequencyEvidenceAssetTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private static string Data(params string[] parts) => Path.Combine(new[] { CliProcess.Root(), "data" }.Concat(parts).ToArray());
    private static string Trace2019 => Data("neso", "gb-2019-08-09-frequency.csv");
    private static string Trace2023 => Data("neso", "gb-2023-12-22-frequency.csv");

    private string WriteFile(string name, params string[] lines)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static void AssertExit(int expected, CliProcess.Result r) => Assert.True(r.ExitCode == expected, $"expected exit {expected}\n{r}");

    // =================================================================================================================
    // D-DOC-027: freq on an unparsable file; NesoFrequencyCsv skipped bad lines silently
    // =================================================================================================================

    [Fact]
    public void D027_a_damaged_data_row_is_refused_naming_the_line()
    {
        string path = WriteFile("damaged.csv", "dtm,f", "2019-08-09 15:50:00,50.037", "", "2019-08-09 15:50:01,abc");
        var ex = Assert.Throws<InvalidDataException>(() => NesoFrequencyCsv.Read(path).ToList());
        Assert.Contains("line 4", ex.Message, StringComparison.Ordinal);   // blank line 3 counted, as an editor shows it
        Assert.Contains("the frequency does not parse", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'2019-08-09 15:50:01,abc'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2019-08-09 15:50:01,NaN", "not a finite number above 0")]
    [InlineData("2019-08-09 15:50:01,-50.0", "not a finite number above 0")]
    [InlineData("2019-08-09 15:50:01,0", "not a finite number above 0")]
    [InlineData("2019-08-09 25:50:01,50.0", "the timestamp does not parse")]
    [InlineData("50.0", "no 'timestamp,' field")]
    public void D027_every_kind_of_bad_row_is_refused(string row, string why)
    {
        string path = WriteFile("bad.csv", "dtm,f", "2019-08-09 15:50:00,50.0", row);
        var ex = Assert.Throws<InvalidDataException>(() => NesoFrequencyCsv.Read(path).ToList());
        Assert.Contains(why, ex.Message, StringComparison.Ordinal);
        Assert.Contains("line 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void D027_the_header_is_skipped_but_a_damaged_first_data_row_is_not_taken_for_one()
    {
        // A header (no digit in the first field) and blank lines are skipped, as before.
        GridSim.Core.Frequency.FrequencySample one = Assert.Single(NesoFrequencyCsv.Read(WriteFile("h.csv", "", "timestamp,frequency", "", "2019-08-09 15:50:00,50.0")).ToList());
        Assert.Equal(50.0, one.Hz);
        // A file with no header reads from its first line.
        Assert.Equal(2, NesoFrequencyCsv.Read(WriteFile("nh.csv", "2019-08-09 15:50:00,50.0", "2019-08-09 15:50:01,49.9")).Count());
        // A damaged FIRST row holds the digits of a date, so it is refused, not skipped as a header.
        var ex = Assert.Throws<InvalidDataException>(() => NesoFrequencyCsv.Read(WriteFile("d1.csv", "2019-08-09 15:50:00;50.0", "2019-08-09 15:50:01,49.9")).ToList());
        Assert.Contains("line 1", ex.Message, StringComparison.Ordinal);
        // A second header-like line is not a header.
        Assert.Throws<InvalidDataException>(() => NesoFrequencyCsv.Read(WriteFile("hh.csv", "dtm,f", "dtm,f", "2019-08-09 15:50:00,50.0")).ToList());
        // A header only: no rows, no exception (the callers decide that "nothing" is an error).
        Assert.Empty(NesoFrequencyCsv.Read(WriteFile("ho.csv", "dtm,f")).ToList());
    }

    [Theory]
    [InlineData("frequency-sample.csv", 300)]
    [InlineData("gb-2019-08-09-frequency.csv", 601)]
    [InlineData("gb-2023-12-22-frequency.csv", 601)]
    public void D027_every_bundled_trace_reads_in_full(string file, int rows)
        => Assert.Equal(rows, NesoFrequencyCsv.Read(Data("neso", file)).Count());

    [Fact]
    public void D027_an_attached_header_only_trace_says_so_and_scores_nothing()
    {
        GridEvent ev = EventCatalog.Get("gb-2019-08-09", utcNow: Now);
        ev = ev with { Outcome = ev.Outcome with { NesoTraceCsvPath = WriteFile("empty.csv", "dtm,f") } };
        ReplayResult r = ReplayValidator.Validate(ev);
        Assert.Null(r.PerSampleRmseHz);
        Assert.NotNull(r.TraceNote);
        Assert.Contains("holds no data rows", r.TraceNote!, StringComparison.Ordinal);
    }

    [Fact]
    public void D027_cli_freq_refuses_a_file_with_no_rows_and_a_damaged_row_with_exit_1()
    {
        CliProcess.Result empty = CliProcess.RunBuilt("freq", WriteFile("e.csv", "dtm,f"));
        AssertExit(1, empty);
        Assert.Contains("holds no data rows", empty.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Samples", empty.StdOut, StringComparison.Ordinal);   // no zero statistics

        CliProcess.Result bad = CliProcess.RunBuilt("freq", WriteFile("b.csv", "dtm,f", "2019-08-09 15:50:00,50.0", "junk,junk"));
        AssertExit(1, bad);
        Assert.Contains("line 3", bad.StdErr, StringComparison.Ordinal);

        CliProcess.Result replay = CliProcess.RunBuilt("replay", WriteFile("r.csv", "dtm,f", "2019-08-09 15:50:00,50.0", "x,y"), "--frames", "2");
        AssertExit(1, replay);
    }

    // =================================================================================================================
    // D-DOC-028: transient dropped --forming / --asset-dynamics silently
    // =================================================================================================================

    [Fact]
    public void D028_transient_carries_forming_into_the_run_and_refuses_asset_dynamics_without_a_bundle()
    {
        string outDir = Path.Combine(_dir, "tr");
        CliProcess.Result r = CliProcess.RunBuilt("transient", "case9", "--minutes", "0.01", "--forming", "droop",
            "--forming-damping", "500", "--out", outDir);
        AssertExit(0, r);
        string meta = File.ReadAllText(Path.Combine(outDir, "summary.md"));
        Assert.Contains("forming=Droop", meta, StringComparison.Ordinal);
        Assert.Contains("Dv=500MW/Hz", meta, StringComparison.Ordinal);

        CliProcess.Result ad = CliProcess.RunBuilt("transient", "case9", "--minutes", "0.01", "--asset-dynamics", "--out", Path.Combine(_dir, "tr2"));
        AssertExit(1, ad);
        Assert.Contains("--asset-dynamics needs a case with an asset bundle", ad.StdErr, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // D-DOC-029: trip --asset-dynamics dropped distributed; ignored without a bundle
    // =================================================================================================================

    [Fact]
    public void D029_trip_refuses_asset_dynamics_without_a_bundle_and_with_the_distributed_model()
    {
        CliProcess.Result noBundle = CliProcess.RunBuilt("trip", "uk", "--asset-dynamics");
        AssertExit(1, noBundle);
        Assert.Contains("has none, so the flag would do nothing", noBundle.StdErr, StringComparison.Ordinal);

        CliProcess.Result both = CliProcess.RunBuilt("trip", "--dir", Data("gb-synthetic"), "--asset-dynamics", "--inertia-model", "distributed");
        AssertExit(2, both);
        Assert.Contains("cannot be combined yet", both.StdErr, StringComparison.Ordinal);

        // The bundle case alone still runs, and (D-DOC-161) splits the trips by who tripped.
        CliProcess.Result ok = CliProcess.RunBuilt("trip", "--dir", Data("gb-synthetic"), "--asset-dynamics");
        AssertExit(0, ok);
        Assert.Contains("1 unit(s) tripped, 800 MW of infeed lost: 0 inverter(s) 0 MW (Loss-of-Mains or ride-through), 1 protection device(s) 800 MW",
            ok.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("lost to LoM", ok.StdOut, StringComparison.Ordinal);
        Assert.Contains("Nadir:          49.052 Hz", ok.StdOut, StringComparison.Ordinal);   // unchanged from 1.95.191
    }

    // =================================================================================================================
    // D-DOC-030: evidence used the instantaneous RoCoF, the validator a windowed one
    // =================================================================================================================

    [Fact]
    public void D030_the_simulated_column_is_the_gates_own_same_basis_rocof()
    {
        GridEvent ev = EventCatalog.Get("gb-2023-12-22", utcNow: Now);
        EvidenceReport report = EvidenceReport.Build(EvidenceSpec.Default(ev, new DeviceConfig("d", 30_000, 1_000)));

        // Rebuild the config Build hands the gate, and run the gate.
        DynamicsConfig cfg = ReplayValidator.ExtendToCover(ev.ApplyRecordedServices(new DynamicsConfig()), ev.ExogenousLossEvents());
        ReplayResult gate = ReplayValidator.Validate(ev, cfg);
        Assert.Equal(gate.SimulatedRocofHzPerS, report.Columns[1].RocofHzPerS);                       // bit for bit
        Assert.Equal(ReplayValidator.RocofOnRecordBasis(ev.Outcome, gate.Simulated), report.Columns[1].RocofHzPerS);
        Assert.NotEqual(gate.Simulated.InitialRocofHzPerS, report.Columns[1].RocofHzPerS);          // the old figure
        _out.WriteLine($"gb-2023-12-22 simulated column: {gate.Simulated.InitialRocofHzPerS:R} -> {report.Columns[1].RocofHzPerS:R} Hz/s");
        Assert.Equal(-0.123, Math.Round(report.Columns[1].RocofHzPerS, 3));
        Assert.Equal(-0.155, Math.Round(gate.Simulated.InitialRocofHzPerS, 3));
    }

    [Fact]
    public void D030_an_instantaneous_record_keeps_the_instantaneous_peak_bit_for_bit()
    {
        GridEvent ev = EventCatalog.Get("gb-2019-08-09", utcNow: Now);
        EvidenceReport report = EvidenceReport.Build(EvidenceSpec.Default(ev, new DeviceConfig("d", 30_000, 1_000)));
        DynamicsConfig cfg = ReplayValidator.ExtendToCover(ev.ApplyRecordedServices(new DynamicsConfig()), ev.ExogenousLossEvents());
        ReplayResult gate = ReplayValidator.Validate(ev, cfg);
        Assert.Equal(gate.Simulated.InitialRocofHzPerS, report.Columns[1].RocofHzPerS);
        // The counterfactual column is the device run's instantaneous peak, as before.
        FrequencyResponse cf = FrequencyDynamics.Simulate(ev.PreEventInertiaMwS, ev.PreEventDemandMw, ev.ExogenousLossEvents(),
            new DeviceConfig("d", 30_000, 1_000).Apply(cfg));
        Assert.Equal(cf.InitialRocofHzPerS, report.Columns[2].RocofHzPerS);
    }

    // =================================================================================================================
    // D-DOC-031: the dossier's fleet section used FleetSpec.Uniform defaults, not the device's values
    // =================================================================================================================

    [Fact]
    public void D031_the_fleet_section_uses_the_devices_own_lag_and_deadband()
    {
        GridEvent ev = EventCatalog.Get("gb-2019-08-09", utcNow: Now);
        DeviceConfig fast = DeviceConfig.Load(Data("devices", "tsgb-fleet.json"));
        Assert.Equal(0.05, fast.FastResponseTimeConstantS);
        EvidenceReport report = EvidenceReport.Build(EvidenceSpec.Default(ev, fast));
        Assert.NotNull(report.Fleet);

        DynamicsConfig cfg = ReplayValidator.ExtendToCover(ev.ApplyRecordedServices(new DynamicsConfig()), ev.ExogenousLossEvents());
        double eMwS = ev.PreEventInertiaMwS;
        FleetResult agg = FleetDynamics.Simulate(eMwS, ev.PreEventDemandMw, ev.ExogenousLossEvents(),
            FleetSpec.Uniform(1, fast.RotationalInertiaMwS, fast.FastResponseMw, ffrTauS: 0.05, deadbandHz: 0.015, maxStaggerS: 0), cfg);
        FleetResult flt = FleetDynamics.Simulate(eMwS, ev.PreEventDemandMw, ev.ExogenousLossEvents(),
            FleetSpec.Uniform(100, fast.RotationalInertiaMwS, fast.FastResponseMw, ffrTauS: 0.05, deadbandHz: 0.015, maxStaggerS: 10.0), cfg);
        Assert.Equal(agg.Response.NadirHz, report.Fleet!.AggregateNadirHz);
        Assert.Equal(flt.Response.NadirHz, report.Fleet.FleetNadirHz);
        Assert.Equal(agg.FfrAtNadirMw, report.Fleet.AggregateFfrAtNadirMw);

        // The old call (Uniform's 0.5 s default) gives a different section.
        FleetResult old = FleetDynamics.Simulate(eMwS, ev.PreEventDemandMw, ev.ExogenousLossEvents(),
            FleetSpec.Uniform(1, fast.RotationalInertiaMwS, fast.FastResponseMw, maxStaggerS: 0), cfg);
        _out.WriteLine($"tsgb-fleet aggregate nadir: {old.Response.NadirHz:R} -> {agg.Response.NadirHz:R} Hz");
        Assert.NotEqual(old.Response.NadirHz, agg.Response.NadirHz);

        // A device with DeviceConfig's defaults (0.5 s, 0.015 Hz) is exactly the old call.
        DeviceConfig plain = new("d", 30_000, 1_000);
        EvidenceReport r2 = EvidenceReport.Build(EvidenceSpec.Default(ev, plain));
        FleetResult oldPlain = FleetDynamics.Simulate(eMwS, ev.PreEventDemandMw, ev.ExogenousLossEvents(),
            FleetSpec.Uniform(1, plain.RotationalInertiaMwS, plain.FastResponseMw, maxStaggerS: 0), cfg);
        Assert.Equal(oldPlain.Response.NadirHz, r2.Fleet!.AggregateNadirHz);
    }

    // =================================================================================================================
    // D-DOC-032: scenario / fleet / evidence / h0-study had no --trace
    // =================================================================================================================

    [Theory]
    [InlineData("scenario")]
    [InlineData("fleet")]
    [InlineData("evidence")]
    [InlineData("h0-study")]
    public void D032_every_gated_verb_takes_trace_and_gets_the_replay_event_verdict(string verb)
    {
        // gb-2019-08-09 is Reproduced without a trace and Marginal with the bundled one (RMSE 0.153 Hz; LF6 N317, which
        // models the recorded response, brings it to about 0.099 Hz, still above the 0.05 Hz cap), exactly as
        // replay-event --trace says, so every gated verb now refuses its counterfactual with the trace attached.
        CliProcess.Result r = CliProcess.RunBuilt(verb, "--event", "gb-2019-08-09", "--trace", Trace2019, "--out", Path.Combine(_dir, verb));
        AssertExit(3, r);
        Assert.Contains("Marginal", r.StdOut + r.StdErr, StringComparison.Ordinal);

        CliProcess.Result missing = CliProcess.RunBuilt(verb, "--event", "gb-2019-08-09", "--trace", Path.Combine(_dir, "nope.csv"));
        AssertExit(2, missing);
        Assert.Contains("--trace file not found", missing.StdErr, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // D-DOC-033: --voll-source accepted any string and tagged the cost Recorded
    // =================================================================================================================

    [Fact]
    public void D033_voll_sources_are_the_references_on_disk_with_their_figures()
    {
        Assert.NotNull(VollSources.Find("S324", 6000.0));
        Assert.NotNull(VollSources.Find("s324 ", 3000.0));
        Assert.NotNull(VollSources.Find("S322", 16940.0));
        Assert.NotNull(VollSources.Find("S323", 16940.0));
        Assert.Null(VollSources.Find("S324", 6001.0));
        Assert.Null(VollSources.Find("S322", 17000.0));
        Assert.Null(VollSources.Find("Ofgem 2019", 6000.0));
        Assert.Equal(new[] { "S324", "S322", "S323" }, VollSources.Ids);
        Assert.Equal(new[] { 3000.0, 6000.0 }, VollSources.FiguresOf("S324"));
        // Each id is a catalogued reference (docs/reference/README.md, linked under voll/). The PDFs themselves are not
        // in the repository (their redistribution terms do not all allow it; Mark's ruling, 2026-10-15), so the file
        // check runs only where a working copy has fetched them; the catalogue check runs everywhere.
        string catalogue = File.ReadAllText(Path.Combine(CliProcess.Root(), "docs", "reference", "README.md"));
        string vollDir = Path.Combine(CliProcess.Root(), "docs", "reference", "voll");
        foreach (string id in VollSources.Ids)
        {
            Assert.Contains($"| {id} |", catalogue, StringComparison.Ordinal);
            Assert.Contains($"](voll/{id}-", catalogue, StringComparison.Ordinal);
            if (Directory.Exists(vollDir))
                Assert.Single(Directory.GetFiles(vollDir, id + "-*.pdf"));
        }
    }

    [Fact]
    public void D033_the_avoided_cost_takes_the_weaker_tag_of_its_two_inputs()
    {
        Assert.Equal(ProvenanceTag.Recorded, AvoidedCost.Weakest(ProvenanceTag.Recorded, ProvenanceTag.Recorded));
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, AvoidedCost.Weakest(ProvenanceTag.Recorded, ProvenanceTag.CuratedPlaceholder));
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, AvoidedCost.Weakest(ProvenanceTag.CuratedPlaceholder, ProvenanceTag.Recorded));
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, AvoidedCost.Weakest(ProvenanceTag.Recorded, ProvenanceTag.DerivedFromRecorded));
        Assert.Equal(ProvenanceTag.Synthetic, AvoidedCost.Weakest(ProvenanceTag.DerivedFromRecorded, ProvenanceTag.Synthetic));
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, AvoidedCost.Weakest(ProvenanceTag.Synthetic, ProvenanceTag.Counterfactual));

        // Moved 2026-10 (END-PASS CF6 row 5, end pass; N359, technical ch. 8 "trust only moves down": a VoLL is a policy
        // figure, AvoidedCost.VollBaseProvenance = CuratedPlaceholder, and a citation records where it is stated without
        // re-sourcing it, Ruling R1 and Mark's go on the end-pass moves 2026-10-11): a VoLL tagged Recorded is now refused,
        // with or without a citation. Before -> after:
        // - the cited call (VoLL Recorded, S324) returned cost 2,676,000, tag CuratedPlaceholder, VollProvenance Recorded ->
        //   throws InvalidDataException (observed, 1.95.273-275 runs: "the VoLL input cited to S324 (a citation records
        //   where the figure is stated; it does not re-source it) claims Recorded but its weakest input is
        //   CuratedPlaceholder: trust only moves down (technical ch. 8).");
        // - the cited arithmetic is kept on the tag a VoLL may carry: VollProvenance Recorded -> CuratedPlaceholder; cost,
        //   result tag CuratedPlaceholder and source S324 unchanged, cited.VollProvenance Recorded -> CuratedPlaceholder
        //   (derived from FromLfddAvoided, not observed);
        // - the `both` call (VoLL and duration Recorded, no citation) returned Recorded -> throws InvalidDataException
        //   (derived, not observed); the weaker-of-two rule on the result is now shown with the duration Recorded and the
        //   VoLL at its base tag: Weakest(CuratedPlaceholder, Recorded) = CuratedPlaceholder (derived, not observed).
        // The Weakest assertions above are unchanged.
        InvalidDataException raised = Assert.Throws<InvalidDataException>(() => AvoidedCost.FromLfddAvoided(892, new AvoidedCostInputs(6000, 0.5,
            VollProvenance: ProvenanceTag.Recorded, DurationProvenance: ProvenanceTag.CuratedPlaceholder, VollSourceId: "S324")));
        Assert.Contains("claims Recorded but its weakest input is CuratedPlaceholder: trust only moves down", raised.Message, StringComparison.Ordinal);
        AvoidedCostResult cited = AvoidedCost.FromLfddAvoided(892, new AvoidedCostInputs(6000, 0.5,
            VollProvenance: ProvenanceTag.CuratedPlaceholder, DurationProvenance: ProvenanceTag.CuratedPlaceholder, VollSourceId: "S324"));
        Assert.Equal(892 * 6000.0 * 0.5, cited.AvoidedCostGbp);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, cited.Provenance);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, cited.VollProvenance);
        Assert.Equal("S324", cited.VollSourceId);
        Assert.Throws<InvalidDataException>(() => AvoidedCost.FromLfddAvoided(892, new AvoidedCostInputs(6000, 0.5,
            VollProvenance: ProvenanceTag.Recorded, DurationProvenance: ProvenanceTag.Recorded)));
        AvoidedCostResult both = AvoidedCost.FromLfddAvoided(892, new AvoidedCostInputs(6000, 0.5,
            VollProvenance: ProvenanceTag.CuratedPlaceholder, DurationProvenance: ProvenanceTag.Recorded));
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, both.Provenance);
        Assert.Equal(ProvenanceTag.Recorded, both.DurationProvenance);
    }

    [Fact]
    public void D033_cli_voll_source_must_name_a_reference_and_one_of_its_figures()
    {
        CliProcess.Result any = CliProcess.RunBuilt("evidence", "--event", "gb-2019-08-09", "--voll-source", "Ofgem 2019", "--out", Path.Combine(_dir, "e1"));
        AssertExit(2, any);
        Assert.Contains("is not a reference that states a GB value of lost load", any.StdErr, StringComparison.Ordinal);

        CliProcess.Result wrong = CliProcess.RunBuilt("evidence", "--event", "gb-2019-08-09", "--voll", "17000", "--voll-source", "S322", "--out", Path.Combine(_dir, "e2"));
        AssertExit(2, wrong);
        Assert.Contains("it states 16940 GBP/MWh", wrong.StdErr, StringComparison.Ordinal);

        CliProcess.Result ok = CliProcess.RunBuilt("evidence", "--event", "gb-2019-08-09", "--voll-source", "S324", "--out", Path.Combine(_dir, "e3"));
        AssertExit(0, ok);
        // Moved 2026-10 (END-PASS CF6 row 6, end pass; N359, technical ch. 8 "trust only moves down", the CLI keeps a cited
        // VoLL at AvoidedCost.VollBaseProvenance, Ruling R1 and Mark's go on the end-pass moves 2026-10-11): the citation
        // is still checked and printed ("VoLL source: S324"), but no longer raises the tag. Before -> after: "VoLL
        // [Recorded]   duration [CuratedPlaceholder]" -> "VoLL [CuratedPlaceholder]   duration [CuratedPlaceholder]"
        // (observed: the old text not found, exit 0, 1.95.273-275 runs; the new text derived from Program.cs, which prints
        // `VoLL [{ac.VollProvenance}]   duration [{ac.DurationProvenance}]` with VollProvenance = VollBaseProvenance).
        // Exit codes and the other lines unchanged.
        Assert.Contains("VoLL [CuratedPlaceholder]   duration [CuratedPlaceholder]", ok.StdOut, StringComparison.Ordinal);
        Assert.Contains("VoLL source: S324", ok.StdOut, StringComparison.Ordinal);
        Assert.Contains("[CuratedPlaceholder]", ok.StdOut, StringComparison.Ordinal);   // the cost stays a placeholder
    }

    // =================================================================================================================
    // D-DOC-034: ExogenousLossEvents dropped every recorded action, not only LFDD
    // =================================================================================================================

    [Fact]
    public void D034_counterfactual_steps_keep_operator_instructions_and_drop_only_lfdd()
    {
        GridEvent e2019 = EventCatalog.Get("gb-2019-08-09", utcNow: Now);
        IReadOnlyList<FrequencyEvent> x2019 = e2019.ExogenousLossEvents();
        // Only LFDD recorded: the same steps, in the same order, as the losses alone (the old method), bit for bit.
        Assert.Equal(e2019.LossEvents(), x2019);
        Assert.DoesNotContain(x2019, e => e.DeltaPMw > 0);

        GridEvent e2023 = EventCatalog.Get("gb-2023-12-22", utcNow: Now);
        IReadOnlyList<FrequencyEvent> x2023 = e2023.ExogenousLossEvents();
        Assert.Equal(2 + 12, x2023.Count);
        Assert.Equal(e2023.ToFrequencyEvents(), x2023);   // no LFDD in 2023: every recorded step is exogenous
        for (int i = 1; i < x2023.Count; i++) Assert.True(x2023[i].AtSeconds >= x2023[i - 1].AtSeconds);
        Assert.Equal(2, e2023.LossEvents().Count);

        // The effect on a 2023 counterfactual-style run (recorded services, the event's inertia, no device): the
        // recovery is now in it. Printed for the notes; the settling frequency is higher with the recovery.
        DynamicsConfig cfgNew = ReplayValidator.ExtendToCover(e2023.ApplyRecordedServices(new DynamicsConfig()), x2023);
        DynamicsConfig cfgOld = ReplayValidator.ExtendToCover(e2023.ApplyRecordedServices(new DynamicsConfig()), e2023.LossEvents());
        FrequencyResponse withBm = FrequencyDynamics.Simulate(e2023.PreEventInertiaMwS, e2023.PreEventDemandMw, x2023, cfgNew);
        FrequencyResponse without = FrequencyDynamics.Simulate(e2023.PreEventInertiaMwS, e2023.PreEventDemandMw, e2023.LossEvents(), cfgOld);
        _out.WriteLine($"2023 no-device counterfactual inputs: window {cfgOld.DurationSeconds:R} -> {cfgNew.DurationSeconds:R} s; " +
                       $"nadir {without.NadirHz:R} -> {withBm.NadirHz:R} Hz at {without.NadirSeconds:R} -> {withBm.NadirSeconds:R} s; " +
                       $"settle {without.SettleHz:R} -> {withBm.SettleHz:R} Hz; peak RoCoF {without.InitialRocofHzPerS:R} -> {withBm.InitialRocofHzPerS:R}");
        Assert.True(withBm.SettleHz > without.SettleHz);
    }

    [Fact]
    public void D034_an_action_without_a_kind_is_refused_by_the_catalog_and_keeps_the_old_meaning_in_code()
    {
        GridEvent ev = EventCatalog.Get("gb-2019-08-09", utcNow: Now);
        GridEvent unkinded = ev with { RecordedActions = new List<RecordedAction> { new(76.0, +892, "LFDD", ProvenanceTag.Recorded) } };
        // Built in code, an action without a kind keeps the old meaning (left out of the counterfactual)...
        Assert.Equal(unkinded.LossEvents(), unkinded.ExogenousLossEvents());
        // ...but no catalogue (file or built-in) may leave it unstated.
        var refused = Assert.Throws<InvalidDataException>(() => EventCatalog.Validate(new[] { unkinded }, Now));
        Assert.Contains("has no kind", refused.Message, StringComparison.Ordinal);

        // A JSON catalogue whose action has no "kind" (the pre-DOC-5 shape) is refused on load, naming the event.
        // Moved 2026-10 (D-DOC-179, docs-session finding, Ruling R1: Mark delegated 2026-10-04): the bundled file lacked
        // gb-2023-12-22, which the built-ins carry (the defect D-DOC-179 reports), so its only action kind was
        // DemandDisconnection and stripping that one kind left no "kind" at all. The file now carries gb-2023-12-22, whose
        // twelve balancing-mechanism actions are "kind": "OperatorInstruction", so the pre-DOC-5 shape is made by stripping
        // EVERY action kind. The assertions are unchanged (no "kind" left; the load is refused naming gb-2019-08-09).
        // Before:
        //   .Replace(",\r\n          \"kind\": \"DemandDisconnection\"", "").Replace(",\n          \"kind\": \"DemandDisconnection\"", "")
        // After:
        //   every ',<newline><indent>"kind": "<Name>"' removed (the regular expression below).
        string json = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Data("events", "gb-events.json")), ",\\r?\\n[ ]*\"kind\": \"[A-Za-z]+\"", "");
        Assert.DoesNotContain("\"kind\"", json, StringComparison.Ordinal);
        string path = Path.Combine(_dir, "old.json");
        File.WriteAllText(path, json);
        var load = Assert.Throws<InvalidDataException>(() => EventCatalog.Load(path, Now));
        Assert.Contains("gb-2019-08-09", load.Message, StringComparison.Ordinal);

        // The bundled catalogues carry the kind and load.
        GridEvent bundled = EventCatalog.Load(Data("events", "gb-events.json"), Now).Single(e => e.Id == "gb-2019-08-09");
        Assert.Equal(RecordedActionKind.DemandDisconnection, Assert.Single(bundled.RecordedActions).Kind);
        GridEvent est = Assert.Single(EventCatalog.Load(Data("events", "gb-2019-08-09-estimated.json"), Now));
        Assert.Equal(RecordedActionKind.DemandDisconnection, Assert.Single(est.RecordedActions).Kind);
        // The built-ins round-trip with their kinds.
        string rt = Path.Combine(_dir, "rt.json");
        EventCatalog.Save(rt, EventCatalog.Builtin(Now));
        Assert.Equal(12, EventCatalog.Load(rt, Now).Single(e => e.Id == "gb-2023-12-22").RecordedActions.Count(a => a.Kind == RecordedActionKind.OperatorInstruction));
    }

    // =================================================================================================================
    // D-DOC-035: the inertia cross-check inverted a windowed RoCoF as if it were instantaneous
    // =================================================================================================================

    [Fact]
    public void D035_a_windowed_record_is_corrected_to_an_instantaneous_slope_before_the_inversion()
    {
        GridEvent ev = EventCatalog.Get("gb-2023-12-22", utcNow: Now);
        ReplayResult r = ReplayValidator.Validate(ev);
        Assert.NotNull(r.InertiaCrossCheckNote);
        double ratio = Math.Abs(r.Simulated.InitialRocofHzPerS) / Math.Abs(r.SimulatedRocofHzPerS);
        double expected = InertiaEstimator.EstimateInertiaMwS(1000.0, ev.Outcome.InitialRocofHzPerS * ratio, 50.0);
        Assert.Equal(expected, r.InferredInertiaMwS);
        double old = InertiaEstimator.EstimateInertiaMwS(1000.0, ev.Outcome.InitialRocofHzPerS, 50.0);
        _out.WriteLine($"gb-2023-12-22 inferred inertia: {old / 1000:R} -> {r.InferredInertiaMwS / 1000:R} GVA.s (ratio {ratio:R}); recorded 161.0");
        Assert.Equal(245.098, Math.Round(old / 1000, 3));
        Assert.True(Math.Abs(r.InferredInertiaMwS - ev.PreEventInertiaMwS) < Math.Abs(old - ev.PreEventInertiaMwS));
    }

    [Fact]
    public void D035_an_instantaneous_record_is_inverted_exactly_as_before()
    {
        foreach (string id in new[] { "gb-2019-08-09", "gb-largest-loss-1320" })
        {
            GridEvent ev = EventCatalog.Get(id, utcNow: Now);
            ReplayResult r = ReplayValidator.Validate(ev);
            Assert.Null(r.InertiaCrossCheckNote);
            double loss = ev.Losses.Where(l => l.AtSeconds <= 5.0).Sum(l => Math.Abs(l.DeltaPMw));
            Assert.Equal(InertiaEstimator.EstimateInertiaMwS(loss, ev.Outcome.InitialRocofHzPerS, 50.0), r.InferredInertiaMwS);
        }
    }

    [Fact]
    public void D035_a_self_consistent_windowed_event_recovers_its_own_inertia()
    {
        var cfg = new DynamicsConfig();
        var steps = new[] { new FrequencyEvent(1.0, -1320.0) };
        FrequencyResponse truth = FrequencyDynamics.Simulate(180_000, 30_000, steps,
            ReplayValidator.ExtendToCover(cfg, steps));
        double windowed = ReplayValidator.MaxWindowedRocof(truth, 1.0);
        var ev = new GridEvent("syn-w", "windowed", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 180_000, 30_000,
            new List<PowerStep> { new(1.0, -1320.0, "loss", ProvenanceTag.Synthetic) }, new List<RecordedAction>(),
            new RecordedOutcome(windowed, truth.NadirHz, truth.SettleHz, 0, null, ProvenanceTag.Synthetic, RocofMeasurementWindowS: 1.0),
            ProvenanceTag.Synthetic);
        ReplayResult r = ReplayValidator.Validate(ev);
        Assert.True(Math.Abs(r.InferredInertiaMwS - 180_000) <= 1e-9 * 180_000, $"inferred {r.InferredInertiaMwS:R}");
        // The old inversion of the windowed figure overstated it.
        Assert.True(InertiaEstimator.EstimateInertiaMwS(1320, windowed) > 180_000 * 1.0001);
    }

    // =================================================================================================================
    // D-DOC-036: the catalog loader did not check the schema id value
    // =================================================================================================================

    [Theory]
    [InlineData("gridsim-event-catalog/2")]
    [InlineData("gridsim-asset-classes/1")]
    [InlineData("GRIDSIM-EVENT-CATALOG/1")]
    public void D036_a_catalog_declaring_another_schema_is_refused(string schema)
    {
        string json = File.ReadAllText(Data("events", "gb-events.json")).Replace("\"gridsim-event-catalog/1\"", $"\"{schema}\"");
        string path = Path.Combine(_dir, "s.json");
        File.WriteAllText(path, json);
        var ex = Assert.Throws<InvalidDataException>(() => EventCatalog.Load(path, Now));
        Assert.Contains($"declares schema '{schema}'", ex.Message, StringComparison.Ordinal);
        Assert.Equal("gridsim-event-catalog/1", EventCatalog.SchemaId);
    }

    // =================================================================================================================
    // D-DOC-037: AssetValidation.Compare never called; blind freeze not enforced; nadir timing untested
    // =================================================================================================================

    private static (FrequencyResponse sim, List<(double, double)> pts) Base()
    {
        FrequencyResponse sim = FrequencyDynamics.Simulate(200_000, 30_000, new[] { new FrequencyEvent(1.0, -1320.0) }, new DynamicsConfig());
        return (sim, sim.Points.Select((p, i) => (i * 0.02, p.Hz)).ToList());
    }

    [Fact]
    public void D037_a_one_second_trace_is_compared_on_its_own_basis_and_a_fine_one_as_before()
    {
        (FrequencyResponse sim, List<(double, double)> fine) = Base();
        ValidationReport f = AssetValidation.Compare(sim, new RecordedFrequencyTrace(fine));
        Assert.Equal(sim.InitialRocofHzPerS, f.SimRocofHzPerS);   // at the simulation step: the instantaneous peak, as before

        var coarse = fine.Where((_, i) => i % 50 == 0).ToList();    // every second
        ValidationReport c = AssetValidation.Compare(sim, new RecordedFrequencyTrace(coarse));
        Assert.Equal(ReplayValidator.MaxWindowedRocof(sim, 1.0), c.SimRocofHzPerS);
        Assert.NotEqual(sim.InitialRocofHzPerS, c.SimRocofHzPerS);
        Assert.Equal(new RecordedFrequencyTrace(coarse).PeakRocofHzPerS(), c.RecordedRocofHzPerS);
    }

    [Fact]
    public void D037_nadir_timing_is_gated_on_its_own()
    {
        (FrequencyResponse sim, List<(double, double)> pts) = Base();
        // The same recording shifted later in time: nadir and RoCoF agree exactly, only the timing moves.
        ValidationReport late3 = AssetValidation.Compare(sim, new RecordedFrequencyTrace(pts.Select(p => (p.Item1 + 3.0, p.Item2)).ToList()));
        Assert.False(late3.Passed);
        Assert.Equal(0.0, late3.NadirErrHz);
        Assert.True(late3.RocofErrHzPerS <= 0.05);
        Assert.Equal(Math.Abs(sim.NadirSeconds - (sim.NadirSeconds + 3.0)), late3.NadirTimeErrS, 12);

        ValidationReport late1 = AssetValidation.Compare(sim, new RecordedFrequencyTrace(pts.Select(p => (p.Item1 + 1.0, p.Item2)).ToList()));
        Assert.True(late1.Passed, late1.Note);
        Assert.Equal(1.0, late1.NadirTimeErrS, 12);
    }

    [Fact]
    public void D037_the_replay_gate_runs_the_back_test_against_an_attached_trace()
    {
        GridEvent ev = EventCatalog.Get("gb-2023-12-22", utcNow: Now);
        ReplayResult none = ReplayValidator.Validate(ev);
        Assert.Null(none.BackTest);

        ReplayResult r = ReplayValidator.Validate(ev with { Outcome = ev.Outcome with { NesoTraceCsvPath = Trace2023 } });
        Assert.NotNull(r.BackTest);
        ValidationReport bt = r.BackTest!;
        _out.WriteLine($"gb-2023-12-22 back-test: passed {bt.Passed}, nadir err {bt.NadirErrHz:R} Hz, RoCoF sim {bt.SimRocofHzPerS:R} vs rec " +
                       $"{bt.RecordedRocofHzPerS:R} Hz/s, nadir time sim {bt.SimNadirTimeS:R} s vs rec {bt.RecordedNadirTimeS:R} s ({bt.NadirTimeErrS:R} s)");
        Assert.Equal(r.Simulated.NadirSeconds, bt.SimNadirTimeS);
        Assert.Equal(Math.Abs(bt.SimNadirTimeS - bt.RecordedNadirTimeS), bt.NadirTimeErrS);
        Assert.Equal(49.275, r.Simulated.NadirHz + bt.NadirErrHz, 9);   // the recorded minimum, inside the window
        Assert.True(bt.NadirTimeErrS > ValidationTolerance.Default.NadirTimeS, "the 2023 replay nadir lands well after the recorded one");
        Assert.False(bt.Passed);
        Assert.Equal(ReplayVerdict.Marginal, r.Verdict);   // unchanged: it was already Marginal
        Assert.Contains("back-test FAIL", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void D037_a_blind_prediction_must_use_the_parameters_frozen_at_the_anchor()
    {
        var shared = new DynamicsConfig { DurationSeconds = 150 };
        FrozenParameterSet frozen = FrozenParameterSet.Freeze("gb-2019-08-09", shared);
        GridEvent blind = EventCatalog.Get("gb-2023-12-22", utcNow: Now);
        RecordedFrequencyTrace rec = RecordedFrequencyTrace.FromSamples(NesoFrequencyCsv.Read(Trace2023), blind.WhenUtc, 0.5, 159.0);

        // The frozen set, unchanged: the blind prediction runs and is scored (and, for 2023, fails: an honest result).
        ValidationReport report = AssetValidation.BlindPredict(frozen, shared, blind, rec);
        _out.WriteLine($"blind 2023 from the 2019 freeze: {report.Note}");
        Assert.Equal(blind.Outcome.NadirHz, rec.NadirHz);

        // Re-tuned by any amount: refused, naming the constant.
        var retuned = shared with { DampingPctPerHz = 1.0000000000000002 };
        var ex = Assert.Throws<InvalidOperationException>(() => AssetValidation.BlindPredict(frozen, retuned, blind, rec));
        Assert.Contains("DampingPctPerHz", ex.Message, StringComparison.Ordinal);
        // The per-event inputs are not frozen (they come from the record).
        Assert.Empty(frozen.Differences(shared with { DurationSeconds = 400, InitialFrequencyHz = 49.9 }));
        // The anchor itself is not a blind prediction.
        Assert.Throws<InvalidOperationException>(() =>
            AssetValidation.BlindPredict(frozen, shared, EventCatalog.Get("gb-2019-08-09", utcNow: Now), rec));
    }

    // =================================================================================================================
    // D-DOC-038: the lumped path fixed voltage and loading at 1.0 pu and 0
    // =================================================================================================================

    private static AssetClass ProtClass(string name, params (string Key, double Value)[] p) =>
        new(name, AssetKind.Protection, "protection", p.ToDictionary(x => x.Key, x => x.Value),
            new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test [VERIFY]") });

    [Fact]
    public void D038_voltage_and_loading_stages_are_not_evaluated_without_a_network_and_run_with_one()
    {
        AssetClass uvHigh = ProtClass("uv-high", (AssetParams.UvTripPu, 1.05), (AssetParams.UvTripDwellS, 0.0));
        AssetClass ovLow = ProtClass("ov-low", (AssetParams.OvTripPu, 0.95), (AssetParams.OvTripDwellS, 0.0));
        var lib = new AssetClassLibrary(new[] { uvHigh, ovLow });
        GridModel b = AssetTestHelpers.Model(null);
        GridModel m = new()
        {
            BaseMva = b.BaseMva, Buses = b.Buses, Generators = b.Generators, Branches = b.Branches,
            Protections = new ProtectionModel(lib, new[] { new ProtectionInstance("uv", "uv-high", 1, 300), new ProtectionInstance("ov", "ov-low", 1, 200) }),
        };
        AssetFrequencyResult r = AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw,
            new[] { new FrequencyEvent(1.0, -500.0) }, new DynamicsConfig { EnableAssetDynamics = true });
        // Before: the made-up 1.0 pu tripped both at once (500 MW). Now neither stage is evaluated on the lumped path.
        Assert.Equal(0, r.TrippedCount);
        Assert.Equal(0.0, r.TotalTrippedMw);
        Assert.Equal(2, r.NetworkStageAssetCount);
        FrequencyResponse plain = FrequencyDynamics.Simulate(m.SystemInertiaGvaS * 1000.0, AssetTestHelpers.BaseDemandMw,
            new[] { new FrequencyEvent(1.0, -500.0) }, new DynamicsConfig());
        Assert.Equal(plain.NadirHz, r.Response.NadirHz);   // an idle fleet is the reduction gate, bit for bit

        // With a network feed (the live loop) the same stages see the voltage and operate.
        AssetDynamicsFleet fleet = AssetDynamicsFleet.Build(m, 50.0);
        fleet.Reset();
        AssetFleetStep s = fleet.StepCoi(50.0, 0.0, 0.0, 0.02, 0.0, voltageOf: _ => 1.0, loadingOf: _ => 0.0);
        Assert.Equal(500.0, s.TrippedDeficitMw);
        Assert.Equal(500.0, fleet.ProtectionTrippedMw);
        Assert.Equal(0.0, fleet.InverterTrippedMw);
    }

    [Fact]
    public void D038_an_lvrt_inverter_reports_its_network_stage_and_is_not_tripped_by_a_made_up_voltage()
    {
        var p = new Dictionary<string, double>(AssetTestHelpers.GflClass("x").Params) { [AssetParams.LvrtVoltagePu] = 1.2 };
        var cls = AssetTestHelpers.GflClass("x") with { Params = p };
        var inv = new GridFollowingInverter(cls, 1, 100, 100);
        Assert.True(inv.UsesNetworkState);
        AssetActuation a = inv.Update(new AssetStepContext(50, 0, 0, 0.02, 0.0, AssetStepContext.NoNetworkVoltagePu, AssetStepContext.NoNetworkLoadingFraction));
        Assert.False(a.TrippedNow);
        inv.Reset();
        Assert.True(inv.Update(new AssetStepContext(50, 0, 0, 0.02, 0.0, LocalVoltagePu: 1.0)).TrippedNow);   // a fed 1.0 < 1.2
        Assert.False(new GridFollowingInverter(AssetTestHelpers.GflClass("y"), 1, 100, 100).UsesNetworkState);
    }

    // =================================================================================================================
    // D-DOC-124: fleet printed fixed conclusion text
    // =================================================================================================================

    [Fact]
    public void D124_fleet_conclusions_follow_the_table()
    {
        // Moved 2026-10 (B1, final batch; LF6 N317, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): the
        // 2019 event replays the ESO recorded services, so the fleet numbers moved (LF6's list missed this test). Before
        // -> after: "RoCoF differs across unit counts (-0.104 vs -0.126 Hz/s)" -> "(-0.139 vs -0.145 Hz/s)"; "the same
        // 1500 MW online" -> "the same 1411 MW online"; nadirs 49.29 / 49.29 -> 49.53 / 49.53 (same nadir in both, so the
        // consequence line still holds).
        CliProcess.Result r = CliProcess.RunBuilt("fleet", "--event", "gb-2019-08-09", "--out", Path.Combine(_dir, "f1"));
        AssertExit(0, r);
        Assert.DoesNotContain("RoCoF identical", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("RoCoF differs across unit counts (-0.139 vs -0.145 Hz/s)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("has the same 1411 MW online", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("Consequence: the same nadir", r.StdOut, StringComparison.Ordinal);

        // gb-largest-loss-1320: RoCoF -0.138 both, FFR 692 vs 897 MW, fleet 0.16 Hz deeper.
        CliProcess.Result s = CliProcess.RunBuilt("fleet", "--event", "gb-largest-loss-1320", "--out", Path.Combine(_dir, "f2"));
        AssertExit(0, s);
        Assert.Contains("RoCoF the same across unit counts (-0.138 vs -0.138 Hz/s)", s.StdOut, StringComparison.Ordinal);
        Assert.Contains("has 692 MW online vs 897 MW", s.StdOut, StringComparison.Ordinal);
        Assert.Contains("(0.16 Hz deeper)", s.StdOut, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // D-DOC-185: GbResponseServices.Typical had no caller
    // =================================================================================================================

    [Fact]
    public void D185_every_typical_service_parameter_is_pinned()
    {
        IReadOnlyList<SystemResponseService> s = GbResponseServices.Typical();
        Assert.Equal(3, s.Count);
        Assert.Equal(new SystemResponseService(950.0, 0.015, 0.5, 0.5, 0.2, 0.05), s[0]);    // DC
        Assert.Equal(new SystemResponseService(520.0, 0.1, 0.2, 2.0, 0.1, 0.0), s[1]);       // DM
        Assert.Equal(new SystemResponseService(490.0, 0.015, 0.2, 10.0, 0.015, 0.0), s[2]);  // DR
        IReadOnlyList<SystemResponseService> half = GbResponseServices.Typical(0.5);
        Assert.Equal(new[] { 475.0, 260.0, 245.0 }, half.Select(x => x.Mw));
    }

    [Fact]
    public void D185_trip_gb_services_applies_the_typical_layer_and_raises_the_nadir()
    {
        string dir = Data("gb-synthetic");
        CliProcess.Result plain = CliProcess.RunBuilt("trip", "--dir", dir);
        CliProcess.Result gb = CliProcess.RunBuilt("trip", "--dir", dir, "--gb-services");
        CliProcess.Result half = CliProcess.RunBuilt("trip", "--dir", dir, "--gb-services=0.5");
        AssertExit(0, plain); AssertExit(0, gb); AssertExit(0, half);
        Assert.Contains("Response:       GB DC / DM / DR at 1 x the EAC volumes (950 / 520 / 490 MW)", gb.StdOut, StringComparison.Ordinal);
        Assert.Contains("(475 / 260 / 245 MW)", half.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("Response:", plain.StdOut, StringComparison.Ordinal);
        double Nadir(string o) => double.Parse(o.Split('\n').First(l => l.StartsWith("Nadir:", StringComparison.Ordinal))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], Inv);
        _out.WriteLine($"trip gb-synthetic nadir: plain {Nadir(plain.StdOut)}, half {Nadir(half.StdOut)}, full {Nadir(gb.StdOut)} Hz");
        Assert.Equal(48.334, Nadir(plain.StdOut));   // the default is unchanged (1.95.191 capture)
        Assert.True(Nadir(gb.StdOut) > Nadir(half.StdOut) && Nadir(half.StdOut) > Nadir(plain.StdOut));

        // A generator name after a bare --gb-services is not swallowed; a bad scale exits 2.
        CliProcess.Result named = CliProcess.RunBuilt("trip", "--dir", dir, "--gb-services", "Torness");
        AssertExit(0, named);
        Assert.Contains("Tripped:        Torness", named.StdOut, StringComparison.Ordinal);
        AssertExit(2, CliProcess.RunBuilt("trip", "--dir", dir, "--gb-services=0"));
        AssertExit(2, CliProcess.RunBuilt("trip", "--dir", dir, "--gb-services", "-1"));
    }

    // =================================================================================================================
    // D-DOC-186: the COI "machine epsilon" test asserted 1e-6 Hz; a tighter one, beside it
    // =================================================================================================================

    [Fact]
    public void D186_the_coi_trajectory_equals_the_lumped_model_to_a_few_ulps()
    {
        // The same two-domain grid as DistributedInvariantTests (a 2-pole thermal domain, E = 5000 MW.s, and an 8-pole
        // hydro domain, E = 1500 MW.s); that test is left as it is.
        var grid = new GridModel
        {
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq) },
            Generators = new Generator[]
            {
                new(Bus: 1, Pg: 800, MBase: 1000, H: 5.0, Type: "ccgt"),
                new(Bus: 2, Pg: 400, MBase: 500, H: 3.0, Type: "hydro"),
            },
            Branches = Array.Empty<Branch>(),
        };
        var loss = new[] { new FrequencyEvent(1.0, -300.0) };
        FrequencyResponse lumped = FrequencyDynamics.Simulate(grid.SystemInertiaGvaS * 1000.0, 1200.0, loss);
        var model = new DistributedInertiaModel();
        FrequencyResponse coi = model.Simulate(grid, 1200.0, loss, new DynamicsConfig { InertiaModel = InertiaModel.Distributed });
        Assert.Equal(2, model.LastDistributedResult!.Domains.Count);
        Assert.Equal(lumped.Points.Count, coi.Points.Count);

        double maxDiff = 0.0;
        for (int k = 0; k < lumped.Points.Count; k++) maxDiff = Math.Max(maxDiff, Math.Abs(lumped.Points[k].Hz - coi.Points[k].Hz));
        double ulp50 = Math.BitIncrement(50.0) - 50.0;   // 7.105427357601002E-15 Hz
        _out.WriteLine($"COI vs lumped: max |df| {maxDiff:R} Hz = {maxDiff / ulp50:R} ulp(50 Hz); nadir {Math.Abs(lumped.NadirHz - coi.NadirHz):R}; " +
                       $"settle {Math.Abs(lumped.SettleHz - coi.SettleHz):R}; RoCoF {Math.Abs(lumped.InitialRocofHzPerS - coi.InitialRocofHzPerS):R}");
        // 64 ulps of 50 Hz (4.5e-13 Hz): rounding of the per-domain sums, nothing physical.
        Assert.True(maxDiff <= 64 * ulp50, $"max |df| {maxDiff:R} Hz");
        Assert.True(Math.Abs(lumped.NadirHz - coi.NadirHz) <= 64 * ulp50);
        Assert.True(Math.Abs(lumped.SettleHz - coi.SettleHz) <= 64 * ulp50);
        Assert.True(Math.Abs(lumped.InitialRocofHzPerS - coi.InitialRocofHzPerS) <= 1e-12);
    }

    // =================================================================================================================
    // D-DOC-187: corners change the RoCoF on multi-step events; ScenarioEngineTests never exercised it
    // =================================================================================================================

    [Fact]
    public void D187_corners_move_the_rocof_of_a_multi_step_event_but_not_of_a_single_step()
    {
        GridEvent ev = EventCatalog.Get("gb-2019-08-09", utcNow: Now);
        ScenarioMatrix m = ScenarioEngine.Run(ScenarioSpec.Default(ev, new DeviceConfig("tsgb", 30_000, 1_000)));
        foreach (double gva in m.Cells.Select(c => c.InertiaGvaS).Distinct())
        {
            ScenarioCell best = m.Cells.Single(c => c.InertiaGvaS == gva && c.Device == "none" && c.Corner == Corner.Best);
            ScenarioCell worst = m.Cells.Single(c => c.InertiaGvaS == gva && c.Device == "none" && c.Corner == Corner.Worst);
            _out.WriteLine($"2019 @ {gva} GVA.s: RoCoF best {best.RocofHzPerS:R} worst {worst.RocofHzPerS:R}");
            // Losses at 0.1, 0.5, 1.0 and 3.0 s: the corner-scaled damping and governor act between the steps.
            Assert.NotEqual(best.RocofHzPerS, worst.RocofHzPerS);
            Assert.Equal(Math.Sign(best.RocofHzPerS), Math.Sign(worst.RocofHzPerS));
        }

        // A windowed (1 s) record of a two-step event moves with the corners too (gb-2023-12-22, on the gate's basis).
        GridEvent e23 = EventCatalog.Get("gb-2023-12-22", utcNow: Now);
        DynamicsConfig cfg = ReplayValidator.ExtendToCover(e23.ApplyRecordedServices(new DynamicsConfig()), e23.ToFrequencyEvents());
        double Basis(Corner c) => ReplayValidator.RocofOnRecordBasis(e23.Outcome,
            FrequencyDynamics.Simulate(e23.PreEventInertiaMwS, e23.PreEventDemandMw, e23.ToFrequencyEvents(), ScenarioCorners.Apply(cfg, c)));
        Assert.NotEqual(Basis(Corner.Best), Basis(Corner.Worst));

        // A single step at t = 1 s with no response deployed: the corners cannot reach the first-step RoCoF.
        var one = new[] { new FrequencyEvent(1.0, -1320.0) };
        var c0 = new DynamicsConfig();
        Assert.Equal(FrequencyDynamics.Simulate(155_000, 32_000, one, ScenarioCorners.Apply(c0, Corner.Best)).InitialRocofHzPerS,
                     FrequencyDynamics.Simulate(155_000, 32_000, one, ScenarioCorners.Apply(c0, Corner.Worst)).InitialRocofHzPerS);
    }

    // =================================================================================================================
    // D-DOC-214: no anti-drift test of the bundled rocofTripHzPerS against FrequencyLimits
    // =================================================================================================================

    [Theory]
    [InlineData("gb-full")]
    [InlineData("gb-synthetic")]
    public void D214_every_bundled_loss_of_mains_setting_is_one_of_the_FrequencyLimits_constants(string caseDir)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Data(caseDir, "asset-classes.json")));
        int lom = 0;
        foreach (JsonElement c in doc.RootElement.EnumerateArray())
        {
            if (!c.TryGetProperty("params", out JsonElement ps) || !ps.TryGetProperty("rocofTripHzPerS", out JsonElement v)) continue;
            double value = v.GetDouble();
            string note = c.TryGetProperty("provenance", out JsonElement prov) && prov.TryGetProperty("rocofTripHzPerS", out JsonElement pr)
                && pr.TryGetProperty("note", out JsonElement n) ? n.GetString() ?? "" : "";
            if (!note.Contains("LoM", StringComparison.Ordinal)) continue;   // a non-LoM setting (GFM 2.5 Hz/s, VSM 1e9) is not a GB LoM figure
            lom++;
            string name = c.GetProperty("name").GetString()!;
            if (note.Contains("Legacy", StringComparison.Ordinal))
                Assert.True(value == FrequencyLimits.RocofLimitLegacyHzPerS, $"{caseDir}/{name}: legacy LoM {value} != {FrequencyLimits.RocofLimitLegacyHzPerS}");
            else if (note.Contains("Modern", StringComparison.Ordinal))
                Assert.True(value == FrequencyLimits.RocofLimitModernHzPerS, $"{caseDir}/{name}: modern LoM {value} != {FrequencyLimits.RocofLimitModernHzPerS}");
            else
                Assert.Fail($"{caseDir}/{name}: a LoM RoCoF setting whose note says neither Legacy nor Modern: '{note}'");
        }
        Assert.True(lom >= 4, $"{caseDir}: only {lom} LoM settings found");
    }

    // =================================================================================================================
    // N235: the GFL / GFM RoCoF confirm timed by repeated float addition
    // =================================================================================================================

    [Theory]
    [InlineData(0.2, 0.02, 10, 11)]
    [InlineData(0.25, 0.02, 13, 13)]
    [InlineData(0.5, 0.02, 25, 25)]
    [InlineData(1.0, 0.02, 50, 50)]
    [InlineData(0.5, 0.01, 50, 50)]
    [InlineData(1.0, 0.001, 1000, 1000)]
    [InlineData(0.0, 0.02, 1, 1)]
    [InlineData(1.0, 0.1, 10, 11)]
    public void N235_the_confirm_is_reached_after_the_whole_number_of_substeps(double confirmS, double dt, int stepsNow, int stepsByAddition)
    {
        var timer = new ConfirmTimer();
        int n = 0;
        while (!ConfirmTimer.Reached(timer.Count(dt), confirmS)) n++;
        Assert.Equal(stepsNow, n + 1);

        double sum = 0.0; int m = 0;
        do { sum += dt; m++; } while (!(sum >= confirmS));
        Assert.Equal(stepsByAddition, m);   // what the old repeated addition gave
    }

    [Fact]
    public void N235_the_inverters_trip_on_the_tenth_20ms_step_of_a_0_2_s_confirm()
    {
        var gp = new Dictionary<string, double>(AssetTestHelpers.GflClass("g", rocofTripHzPerS: 0.1).Params) { [AssetParams.RocofTripConfirmS] = 0.2 };
        var gfl = new GridFollowingInverter(AssetTestHelpers.GflClass("g") with { Params = gp }, 1, 100, 100);
        var fp = new Dictionary<string, double>
        {
            [AssetParams.SynInertiaMwsPerHz] = 0.0, [AssetParams.ActuateTauS] = 0.05,
            [AssetParams.RocofTripHzPerS] = 0.1, [AssetParams.RocofTripConfirmS] = 0.2,
        };
        var gfm = new GridFormingInverter(AssetTestHelpers.GfmClass("f", 0.0) with { Params = fp }, 1, 100, 100);

        foreach (IActiveAssetDynamics inv in new IActiveAssetDynamics[] { gfl, gfm })
        {
            int tripStep = 0;
            for (int k = 1; k <= 20 && tripStep == 0; k++)
                if (inv.Update(new AssetStepContext(49.9, -1.0, 0.0, 0.02, k * 0.02)).TrippedNow) tripStep = k;
            Assert.True(tripStep == 10, $"{inv.Kind}: tripped on step {tripStep} (was 11 by repeated addition)");
        }
    }

    [Fact]
    public void N235_a_substep_change_banks_the_steps_taken_and_an_infinite_confirm_never_fires()
    {
        var t = new ConfirmTimer();
        Assert.Equal(0.02, t.Count(0.02));
        Assert.Equal(0.04, t.Count(0.02));
        Assert.Equal(0.04 + 0.05, t.Count(0.05));
        t.Clear();
        Assert.Equal(0.01, t.Count(0.01));

        // DOC-5 new finding: an infinite dwell used to read inf - e <= slack x inf, true, and fire at once.
        Assert.False(ProtectionEvaluator.DwellReached(1e9, double.PositiveInfinity));
        Assert.False(ConfirmTimer.Reached(0.02, double.PositiveInfinity));
        Assert.True(ProtectionEvaluator.DwellReached(0.19999999999999998, 0.2));   // the finite slack rule is unchanged
    }
}
