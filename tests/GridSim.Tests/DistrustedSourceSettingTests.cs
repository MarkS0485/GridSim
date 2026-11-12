using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Diagnostics.Incident;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Realtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CAF-9 / gap B2 (D1 distrusted-source containment, November 2026): the host side of the control.
/// <list type="bullet">
/// <item>The one reading of the operator's list (<see cref="DistrustedSourceSetting.Parse"/>), used by
/// <c>estimate --distrust</c>, <c>serve --distrust</c> and the web host's <c>Realtime:DistrustedSources</c>: valid lists
/// are read as written; every malformed value fails loud with a message that begins with the flag or setting.</item>
/// <item>The <see cref="SecurityEventKind.DistrustedSource"/> event: exactly one per distrusted source per run, through the
/// audit path (<see cref="AuditRunRecorder.RecordSecurityEvent"/>), baseline severity Significant, raised as an incident
/// by the incident wiring; once per run however many windows the resident estimator takes.</item>
/// <item>The CLI and the web host end to end: refusals exit 2 (CLI) or stop start-up (web) naming the flag or setting,
/// and an audited <c>estimate --distrust</c> writes one event per source.</item>
/// </list>
/// </summary>
public sealed class DistrustedSourceSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-b2-distrust-" + Guid.NewGuid().ToString("N"));

    public DistrustedSourceSettingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>The DistrustedSource entries of an audit log, in order.</summary>
    private static List<AuditLogEntry> DistrustEntries(string log) =>
        AuditLogFile.Load(log).Where(e => SecurityAudit.TryRead(e, out SecurityEventKind kind, out _) && kind == SecurityEventKind.DistrustedSource).ToList();

    // ---------------------------------------------------------------------------------------------------------------
    // Parsing and validation.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B2_a_valid_list_is_read_as_written_trimmed_and_in_order()
    {
        Assert.Equal(new[] { "scada" }, DistrustedSourceSetting.ParseNames("scada", "--distrust"));
        Assert.Equal(new[] { "scada", "pmu", "pseudo-demand+comp-range" },
            DistrustedSourceSetting.ParseNames(" scada , pmu,pseudo-demand+comp-range ", "--distrust"));
        DistrustedSources control = DistrustedSourceSetting.Parse("scada,pmu", DistrustedSourceSetting.WebSetting);
        Assert.False(control.IsEmpty);
        Assert.True(control.IsDistrusted("SCADA"));   // matched without regard to case, as the primitive matches
        Assert.Equal(DistrustedSources.DefaultSigmaInflation, control.SigmaInflation);
        Assert.Equal("--distrust", DistrustedSourceSetting.CliFlag);
        Assert.Equal("Realtime:DistrustedSources", DistrustedSourceSetting.WebSetting);
    }

    [Theory]
    [InlineData("", "names no source")]
    [InlineData("   ", "names no source")]
    [InlineData("scada,,pmu", "item 2 is empty")]
    [InlineData("scada,", "item 2 is empty")]
    [InlineData(",scada", "item 1 is empty")]
    [InlineData("sca da", "contains a space or a control character")]
    [InlineData("scada,\tpmu x", "contains a space or a control character")]
    [InlineData("prior-solved", "not a measurement feed")]
    [InlineData("scada,Removal-Backfill-Prior", "not a measurement feed")]
    [InlineData("scada,SCADA", "is given more than once")]
    public void B2_a_malformed_list_fails_loud_naming_the_flag(string value, string why)
    {
        foreach (string setting in new[] { "--distrust", "Realtime:DistrustedSources" })
        {
            var ex = Assert.Throws<ArgumentException>(() => DistrustedSourceSetting.Parse(value, setting));
            Assert.StartsWith(setting, ex.Message, StringComparison.Ordinal);
            Assert.Contains(why, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void B2_a_null_value_and_an_over_long_tag_are_refused()
    {
        var none = Assert.Throws<ArgumentException>(() => DistrustedSourceSetting.Parse(null, "--distrust"));
        Assert.StartsWith("--distrust names no source", none.Message, StringComparison.Ordinal);
        string longTag = new('a', DistrustedSourceSetting.MaxSourceNameLength + 1);
        var tooLong = Assert.Throws<ArgumentException>(() => DistrustedSourceSetting.Parse("scada," + longTag, "--distrust"));
        Assert.StartsWith("--distrust: source tag item 2 is 129 characters long; the limit is 128.", tooLong.Message, StringComparison.Ordinal);
        // Exactly at the limit is accepted.
        Assert.Single(DistrustedSourceSetting.ParseNames(new string('a', DistrustedSourceSetting.MaxSourceNameLength), "--distrust"));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The security event: exactly one per source per run.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B2_RecordApplied_writes_exactly_one_DistrustedSource_event_per_source_in_a_fixed_order()
    {
        DistrustedSources control = DistrustedSourceSetting.Parse("scada,PMU,feed-x", "--distrust");
        var written = new List<(SecurityEventKind Kind, string Reason)>();
        IReadOnlyList<string> recorded = DistrustedSourceSetting.RecordApplied(control, "estimate --distrust",
            s => s.Length, (k, r) => written.Add((k, r)));

        Assert.Equal(new[] { "feed-x", "PMU", "scada" }, recorded);   // ordinal ignoring case, whatever order was written
        Assert.Equal(3, written.Count);
        Assert.All(written, w => Assert.Equal(SecurityEventKind.DistrustedSource, w.Kind));
        Assert.Equal(
            "measurement source 'feed-x' distrusted by estimate --distrust: 6 row(s) demoted to pseudo-measurements (sigma x 10000) and left out of the chi-square redundancy",
            written[0].Reason);
        Assert.Single(written, w => w.Reason.Contains("'PMU'", StringComparison.Ordinal));
        Assert.Single(written, w => w.Reason.Contains("'scada'", StringComparison.Ordinal));

        // A stream (serve, the resident estimator) states no row count.
        var stream = new List<string>();
        DistrustedSourceSetting.RecordApplied(DistrustedSourceSetting.Parse("scada", "--distrust"), "serve --distrust", null, (_, r) => stream.Add(r));
        Assert.Equal("measurement source 'scada' distrusted by serve --distrust: its rows in every window demoted to pseudo-measurements (sigma x 10000) and left out of the chi-square redundancy",
            Assert.Single(stream));

        // An empty control applies nothing, so it has nothing to record: refused.
        Assert.Throws<ArgumentException>(() => DistrustedSourceSetting.RecordApplied(
            new DistrustedSources(Array.Empty<string>()), "estimate --distrust", null, (_, _) => { }));
    }

    [Fact]
    public void B2_the_events_go_through_the_audit_path_as_Significant_and_each_raises_one_incident()
    {
        Assert.Equal((IncidentCategory.DistrustedSource, SecuritySeverity.Significant), IncidentPolicy.Classify(SecurityEventKind.DistrustedSource));

        var incidents = new BufferedIncidentSink();
        var options = new AuditRecorderOptions { Sinks = IncidentWiring.AuditSinksFor(incidents) };
        AuditRunRecorder run = AuditRunRecorder.Start(P("audit.jsonl"), "analyst@workstation", "estimate",
            ProvenanceTag.DerivedFromRecorded, null, options);
        DistrustedSourceSetting.RecordApplied(DistrustedSourceSetting.Parse("scada,pmu", "--distrust"), "estimate --distrust",
            _ => 3, (k, r) => run.RecordSecurityEvent(k, r));
        Assert.Equal(2, run.SecurityEventCount);
        run.Finish(0);

        AuditLogVerification v = AuditLogFile.Verify(P("audit.jsonl"));
        Assert.True(v.IsValid, v.Reason);
        Assert.Equal(3, v.VerifiedCount);   // two security entries, then the run entry
        for (int i = 0; i < 2; i++)
        {
            Assert.True(SecurityAudit.TryRead(v.Entries[i], out SecurityEventKind kind, out SecuritySeverity sev));
            Assert.Equal(SecurityEventKind.DistrustedSource, kind);
            Assert.Equal(SecuritySeverity.Significant, sev);
        }
        Assert.Equal(2, incidents.Reports.Count);
        Assert.All(incidents.Reports, r => Assert.Equal(IncidentCategory.DistrustedSource, r.Category));
    }

    /// <summary>A feed of five dated windows of case9, three rows of each tagged <c>feed-x</c>. Every other row keeps
    /// <see cref="Measurement"/>'s default source, <c>scada</c>, so distrusting <c>feed-x</c> demotes exactly three rows.</summary>
    private sealed class TaggedFeed : IRealtimeWindowFeed
    {
        private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        private readonly MeasurementSet _template;
        public GridModel Model { get; } = IeeeCases.Case9();
        public TaggedFeed()
        {
            PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(Model);
            MeasurementSet exact = WlsEstimatorTests.ExactFullSet(Model, truth);
            var rows = exact.Measurements.ToList();
            int tagged = 0;
            for (int i = 0; i < rows.Count && tagged < 3; i++)
                if (rows[i].Kind == MeasurementKind.PFlowMw) { rows[i] = rows[i] with { Source = "feed-x" }; tagged++; }
            _template = exact with { Measurements = rows };
        }
        public IEnumerable<MeasurementSet> Windows()
        {
            DateTime baseT = Now.AddHours(-200);
            for (int k = 0; k < 5; k++) yield return _template with { TakenUtc = baseT.AddSeconds(k) };
        }
        public static RealtimeEstimatorOptions Options(DistrustedSources? distrust) => new()
        {
            UtcNow = Now,
            Estimation = new EstimationOptions { DistrustedSources = distrust },
        };
    }

    [Fact]
    public void B2_the_resident_estimator_records_one_event_per_source_per_run_not_per_window()
    {
        string log = P("resident.jsonl");
        AuditRunRecorder audit = AuditRunRecorder.Start(log, "web@host", "serve", ProvenanceTag.DerivedFromRecorded);
        // Not "scada": that is Measurement's default source, so every row of the window carries it and distrusting it
        // would demote all of them (no redundancy left). "feed-x" names exactly the three tagged rows; "pmu" names none.
        DistrustedSources distrust = DistrustedSourceSetting.Parse("feed-x,pmu", DistrustedSourceSetting.WebSetting);
        var svc = new RealtimeEstimatorHostedService(new TaggedFeed(), TaggedFeed.Options(distrust), audit);

        RealtimeRunSummary summary = svc.RunToCompletion();

        Assert.Equal(5, summary.WindowsProcessed);
        Assert.Equal(5, summary.WindowsConverged);
        List<AuditLogEntry> events = DistrustEntries(log);
        Assert.Equal(2, events.Count);   // two sources, five windows: one event per source for the run
        Assert.Contains("Realtime:DistrustedSources", events[0].Summary, StringComparison.Ordinal);
        Assert.Contains("'feed-x'", events[0].Summary, StringComparison.Ordinal);   // OrderedSources: ordinal-ignore-case
        Assert.Contains("'pmu'", events[1].Summary, StringComparison.Ordinal);
        // The window's three feed-x rows are not redundancy: case9 has 9 buses, 8 + 9 = 17 states.
        Assert.NotNull(svc.LatestFrame);
        int rows = new TaggedFeed().Windows().First().Measurements.Count;
        Assert.Equal(rows - 3 - 17, svc.LatestFrame!.Result.DegreesOfFreedom);
    }

    [Fact]
    public void B2_the_resident_estimator_without_the_setting_records_no_DistrustedSource_event()
    {
        string log = P("resident-off.jsonl");
        AuditRunRecorder audit = AuditRunRecorder.Start(log, "web@host", "serve", ProvenanceTag.DerivedFromRecorded);
        var svc = new RealtimeEstimatorHostedService(new TaggedFeed(), TaggedFeed.Options(null), audit);
        svc.RunToCompletion();
        Assert.Empty(DistrustEntries(log));
        int rows = new TaggedFeed().Windows().First().Measurements.Count;
        Assert.Equal(rows - 17, svc.LatestFrame!.Result.DegreesOfFreedom);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The web host: the setting is read once at start-up and every mistake stops it, naming the setting.
    // ---------------------------------------------------------------------------------------------------------------

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(_dir, "keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    private (string Key, string Value)[] ResidentHost(params (string Key, string Value)[] more)
    {
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        return new[]
        {
            ("GridSim:ReplayOnly", "false"), ("Cycle:Enabled", "false"),
            ("Realtime:Enabled", "true"), ("Realtime:Case", "case9"), ("Realtime:WindowsDir", windows),
        }.Concat(more).ToArray();
    }

    [Theory]
    [InlineData("scada,,pmu")]
    [InlineData("prior-solved")]
    [InlineData("scada,scada")]
    public void B2_a_malformed_web_setting_stops_start_up_naming_it(string value)
    {
        using var f = Factory(ResidentHost(("Realtime:DistrustedSources", value)));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Realtime:DistrustedSources", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void B2_the_web_setting_on_a_host_with_no_resident_estimator_stops_start_up()
    {
        using var f = Factory(("GridSim:ReplayOnly", "false"), ("Cycle:Enabled", "false"), ("Realtime:Enabled", "false"),
            ("Realtime:DistrustedSources", "scada"));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        string text = ex.ToString();
        Assert.Contains("Realtime:DistrustedSources is set, but this host runs no resident estimator", text, StringComparison.Ordinal);
    }

    [Fact]
    public void B2_the_web_setting_given_as_a_list_stops_start_up()
    {
        using var f = Factory(ResidentHost(("Realtime:DistrustedSources:0", "scada")));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Realtime:DistrustedSources is given as a list", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void B2_a_valid_web_setting_starts_the_host()
    {
        using var f = Factory(ResidentHost(("Realtime:DistrustedSources", "scada,pmu")));
        using var client = f.CreateClient();   // start-up succeeded
        Assert.NotNull(client);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The CLI, as a process.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>An exact case14 set saved to disk with three flow rows tagged feed-x and two tagged feed-y.</summary>
    private string TaggedMeasurementFile(out int rowCount)
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet exact = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0.0 });
        var rows = exact.Measurements.ToList();
        int x = 0, y = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind != MeasurementKind.PFlowMw) continue;
            if (x < 3) { rows[i] = rows[i] with { Source = "feed-x" }; x++; }
            else if (y < 2) { rows[i] = rows[i] with { Source = "feed-y" }; y++; }
        }
        rowCount = rows.Count;
        string path = P("tagged.json");
        (exact with { Measurements = rows }).Save(path);
        return path;
    }

    [Theory]
    [InlineData("feed-x,,feed-y", "item 2 is empty")]
    [InlineData("feed x", "contains a space")]
    [InlineData("prior-solved", "not a measurement feed")]
    [InlineData("feed-x,FEED-X", "given more than once")]
    public void B2_cli_estimate_refuses_a_malformed_distrust_list_with_exit_2(string value, string why)
    {
        string set = TaggedMeasurementFile(out _);
        CliProcess.Result r = CliProcess.RunBuilt("estimate", "case14", "--measurements", set, "--distrust", value, "--out", P("o"));
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("error: --distrust", r.StdErr, StringComparison.Ordinal);
        Assert.Contains(why, r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void B2_cli_estimate_refuses_a_tag_no_row_carries_and_the_hti_combination()
    {
        string set = TaggedMeasurementFile(out _);
        CliProcess.Result unknown = CliProcess.RunBuilt("estimate", "case14", "--measurements", set, "--distrust", "feed-z", "--out", P("o1"));
        Assert.True(unknown.ExitCode == 2, unknown.ToString());
        Assert.Contains("--distrust names 'feed-z', which no row of the measurement set carries", unknown.StdErr, StringComparison.Ordinal);
        Assert.Contains("feed-x", unknown.StdErr, StringComparison.Ordinal);   // the tags the set does carry are listed

        CliProcess.Result hti = CliProcess.RunBuilt("estimate", "case14", "--measurements", set, "--distrust", "feed-x", "--hti", "--out", P("o2"));
        Assert.True(hti.ExitCode == 2, hti.ToString());
        Assert.Contains("--distrust is not supported with --hti", hti.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void B2_cli_estimate_with_distrust_records_one_event_per_source_and_drops_the_rows_from_the_dof()
    {
        string set = TaggedMeasurementFile(out int rowCount);
        string log = P("cli-audit.jsonl");
        CliProcess.Result r = CliProcess.RunBuilt("--audit-log", log, "estimate", "case14", "--measurements", set,
            "--distrust", "feed-y,feed-x", "--out", P("o"));
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("distrust: source 'feed-x': 3 row(s) demoted to pseudo-measurements (sigma x 10000)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("distrust: source 'feed-y': 2 row(s) demoted to pseudo-measurements (sigma x 10000)", r.StdOut, StringComparison.Ordinal);
        // case14: 27 states; five distrusted rows are not redundancy.
        Assert.Contains($"redundancy (dof): {rowCount - 5 - 27}", r.StdOut, StringComparison.Ordinal);

        List<AuditLogEntry> events = DistrustEntries(log);
        Assert.Equal(2, events.Count);
        Assert.Contains("'feed-x' distrusted by estimate --distrust: 3 row(s)", events[0].Summary, StringComparison.Ordinal);
        Assert.Contains("'feed-y' distrusted by estimate --distrust: 2 row(s)", events[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void B2_cli_estimate_without_distrust_records_no_event_and_counts_every_row()
    {
        string set = TaggedMeasurementFile(out int rowCount);
        string log = P("cli-audit-off.jsonl");
        CliProcess.Result r = CliProcess.RunBuilt("--audit-log", log, "estimate", "case14", "--measurements", set, "--out", P("o"));
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain("distrust:", r.StdOut, StringComparison.Ordinal);
        Assert.Contains($"redundancy (dof): {rowCount - 27}", r.StdOut, StringComparison.Ordinal);
        Assert.Empty(DistrustEntries(log));
    }

    [Fact]
    public void B2_cli_serve_refuses_distrust_with_a_recursive_filter()
    {
        string windows = P("serve-windows");
        Directory.CreateDirectory(windows);
        CliProcess.Result r = CliProcess.RunBuilt("serve", "case9", "--measurements-dir", windows, "--method", "ekf", "--distrust", "scada");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--distrust applies to the static estimator (--method wls) only, not to --method ekf", r.StdErr, StringComparison.Ordinal);
    }
}
