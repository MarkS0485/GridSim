using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N151 (October 2026): the presentation shells show (1) the audit log, verified first, through Core's
/// <see cref="AuditLogDisplay"/> (tampered shows nothing but the TAMPERED verdict, as <c>gridsim audit show</c>);
/// (2) the R175 IrlsCapReached warning on the estimate quality pill (<see cref="BridgeProtocol.EstimateBlock"/>); and
/// (3) in the web HARMONICS tab, N075's ComponentsOutsideOrders and N036's per-converter sources as an opt-in source
/// choice. The default harmonic study is unchanged bit for bit (N131 stays Mark's).
/// </summary>
public sealed class N151ShellAuditIrlsHarmonicsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-n151-{Guid.NewGuid():N}");

    public N151ShellAuditIrlsHarmonicsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root(), "src" }.Concat(parts).ToArray()));

    private string TwoEntryLog(string name = "audit.jsonl")
    {
        string path = Path.Combine(_dir, name);
        using AuditLogWriter w = AuditLogWriter.Open(path);
        w.Append(new DateTime(2026, 10, 4, 9, 30, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "actor=a action=estimate outcome=exit=0 SP1");
        w.Append(new DateTime(2026, 10, 4, 9, 31, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "actor=a action=estimate outcome=exit=3 SP2");
        return path;
    }

    private static void TamperSecondEntry(string path)
    {
        string[] text = File.ReadAllLines(path);
        text[1] = text[1].Replace("SP2", "SP9", StringComparison.Ordinal);
        File.WriteAllText(path, string.Join("\n", text) + "\n");
    }

    // ------------------------------------------------------------------ audit log display (Core, shared by both shells)

    [Fact]
    public void A_verified_log_shows_every_entry_in_the_cli_form_then_the_ok_line_and_its_anchor()
    {
        string path = TwoEntryLog();
        var entries = AuditLogFile.Load(path);
        AuditLogDisplayResult d = AuditLogDisplay.FromFile(path);
        Assert.True(d.Verified);
        Assert.Equal(2, d.EntryLines.Count);
        Assert.Equal(AuditLogFile.Describe(entries[0]), d.EntryLines[0]);
        Assert.Equal(AuditLogFile.Describe(entries[1]), d.EntryLines[1]);
        Assert.Equal($"OK {path}: 2 entries, head {entries[1].Hash}", d.Verdict);
        Assert.Equal(new AuditAnchor(2, entries[1].Hash), d.Anchor);
    }

    [Fact]
    public void A_tampered_log_shows_no_entry_at_all_only_the_tampered_verdict()
    {
        string path = TwoEntryLog();
        TamperSecondEntry(path);
        AuditLogDisplayResult d = AuditLogDisplay.FromFile(path);
        Assert.False(d.Verified);
        Assert.Empty(d.EntryLines);   // not even entry 0, which still verifies on its own
        Assert.Null(d.Anchor);
        Assert.StartsWith($"TAMPERED {path}: entry 1: ", d.Verdict, StringComparison.Ordinal);
        Assert.EndsWith("(1 entries verified before it)", d.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void An_anchor_that_the_file_no_longer_matches_is_tampered()
    {
        string path = TwoEntryLog();
        // The log claims 3 entries were once there: tail truncation is visible only against the anchor.
        AuditLogDisplayResult d = AuditLogDisplay.FromFile(path, new AuditAnchor(3, "00"));
        Assert.False(d.Verified);
        Assert.Empty(d.EntryLines);
        Assert.StartsWith("TAMPERED ", d.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void Uploaded_bytes_give_the_same_display_as_the_file()
    {
        string path = TwoEntryLog();
        AuditLogDisplayResult fromFile = AuditLogDisplay.FromFile(path);
        AuditLogDisplayResult fromBytes = AuditLogDisplay.FromBytes(File.ReadAllBytes(path), path);
        Assert.Equal(fromFile.Verified, fromBytes.Verified);
        Assert.Equal(fromFile.EntryLines, fromBytes.EntryLines);
        Assert.Equal(fromFile.Verdict, fromBytes.Verdict);
        Assert.Equal(fromFile.Anchor, fromBytes.Anchor);

        TamperSecondEntry(path);
        AuditLogDisplayResult bad = AuditLogDisplay.FromBytes(File.ReadAllBytes(path), "up.jsonl");
        Assert.False(bad.Verified);
        Assert.Empty(bad.EntryLines);
        Assert.StartsWith("TAMPERED up.jsonl: entry 1: ", bad.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_log_fails_loud_never_an_empty_clean_log()
        => Assert.Throws<FileNotFoundException>(() => AuditLogDisplay.FromFile(Path.Combine(_dir, "absent.jsonl")));

    [Fact]
    public void An_upload_over_the_cap_is_refused()
    {
        var big = new byte[AuditLogDisplay.MaxLogBytes + 1];
        Assert.Throws<ArgumentException>(() => AuditLogDisplay.FromBytes(big, "big.jsonl"));
    }

    [Theory]
    [InlineData(null, null, true, null)]
    [InlineData("", "  ", true, null)]
    [InlineData("2", "abc", true, 2)]
    [InlineData(" 0 ", "abc", true, 0)]
    [InlineData("2", null, false, null)]
    [InlineData(null, "abc", false, null)]
    [InlineData("-1", "abc", false, null)]
    [InlineData("two", "abc", false, null)]
    public void The_anchor_boxes_follow_the_cli_rules(string? count, string? head, bool ok, int? expectedCount)
    {
        bool parsed = AuditLogDisplay.TryParseAnchor(count, head, out AuditAnchor? anchor, out string? error);
        Assert.Equal(ok, parsed);
        if (ok)
        {
            Assert.Null(error);
            Assert.Equal(expectedCount, anchor?.Count);
            if (anchor is not null) Assert.Equal("abc", anchor.HeadHash);
        }
        else
        {
            Assert.Null(anchor);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void The_shell_display_matches_the_built_cli_audit_show_line_for_line()
    {
        string path = TwoEntryLog();
        AuditLogDisplayResult d = AuditLogDisplay.FromFile(path);
        CliProcess.Result r = CliProcess.RunBuilt("audit", "show", path);
        Assert.True(r.ExitCode == 0, r.ToString());
        string[] lines = r.StdOut.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Assert.Equal(d.EntryLines.Append(d.Verdict).ToArray(), lines);

        TamperSecondEntry(path);
        AuditLogDisplayResult bad = AuditLogDisplay.FromFile(path);
        CliProcess.Result rb = CliProcess.RunBuilt("audit", "show", path);
        Assert.True(rb.ExitCode == 3, rb.ToString());
        Assert.Equal(bad.Verdict, rb.StdErr.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
    }

    [Fact]
    public void Both_shells_render_the_audit_log_only_through_the_core_display()
    {
        string tab = Src("GridSim.Web", "Components", "Tabs", "AuditLogTab.razor");
        Assert.Contains("AuditLogDisplay.FromBytes(", tab, StringComparison.Ordinal);
        Assert.Contains("_result is { Verified: false } bad", tab, StringComparison.Ordinal);
        // No second display path: the tab never verifies or describes a log itself.
        Assert.DoesNotContain("AuditLogFile.Verify(", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("AuditLogFile.Load(", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("AuditLogFile.Describe(", tab, StringComparison.Ordinal);
        string dash = Src("GridSim.Web", "Components", "Pages", "Dashboard.razor");
        Assert.Contains("\"AUDIT LOG\"", dash, StringComparison.Ordinal);
        Assert.Contains("<AuditLogTab />", dash, StringComparison.Ordinal);

        // Test retargeted 2026-10-20 (UI programme 1.6): the WPF audit-log window's display logic (OpenAndShow) moved
        // from src/GridSim.Wpf/AuditLogWindow.cs to src/GridSim.Ui/Tabs/AuditLog/AuditLogModel.cs; the two assertions
        // read it there, unchanged apart from the entries' target (the WPF "_entries.Text" box is now the model's
        // Entries, which the window shows), and strengthened: the entries line is pinned whole, both desktop windows
        // (the WPF one and the Avalonia src/GridSim.App/Tabs/AuditLogWindow) must show the log only through that model,
        // and neither may verify, load, describe or display a log itself (no second display path). The MainWindow.xaml
        // assertion is unchanged.
        string model = Src("GridSim.Ui", "Tabs", "AuditLog", "AuditLogModel.cs");
        Assert.Contains("AuditLogDisplay.FromFile(", model, StringComparison.Ordinal);
        Assert.Contains("if (result.Verified) Entries = string.Join(Environment.NewLine, result.EntryLines);", model, StringComparison.Ordinal);
        string wpf = Src("GridSim.Wpf", "AuditLogWindow.cs");
        Assert.Contains("private readonly AuditLogModel _model = new();", wpf, StringComparison.Ordinal);
        Assert.Contains("_model.Open(", wpf, StringComparison.Ordinal);
        Assert.Contains("_entries.Text = _model.Entries;", wpf, StringComparison.Ordinal);
        string avalonia = Src("GridSim.App", "Tabs", "AuditLogWindow.axaml.cs");
        Assert.Contains("await Model.OpenAsync(ChooseFile)", avalonia, StringComparison.Ordinal);
        foreach (string window in new[] { wpf, avalonia })
            foreach (string own in new[] { "AuditLogDisplay.", "AuditLogFile.Verify(", "AuditLogFile.Load(", "AuditLogFile.Describe(" })
                Assert.DoesNotContain(own, window, StringComparison.Ordinal);
        Assert.Contains("Click=\"AuditLog_Click\"", Src("GridSim.Wpf", "MainWindow.xaml"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ IrlsCapReached on the quality pill

    [Fact]
    public void Without_the_irls_flag_the_quality_text_and_colour_are_exactly_as_before()
    {
        foreach (bool? flag in new bool?[] { null, false })
        {
            var pass = new BridgeProtocol.EstimateBlock { ChiPassed = true, ObjectiveJ = 41.26, MeasurementsN = 120, IrlsCapReached = flag };
            Assert.Equal("chi-square PASS  *  J=41.3  n=120", pass.QualitySummary());
            Assert.True(pass.QualityOk);
            var fail = new BridgeProtocol.EstimateBlock { ChiPassed = false, ObjectiveJ = 900.04, MeasurementsN = 98, RemovedN = 2, IrlsCapReached = flag };
            Assert.Equal("chi-square FAIL  *  J=900.0  n=98  removed=2", fail.QualitySummary());
            Assert.False(fail.QualityOk);
        }
    }

    [Fact]
    public void A_capped_robust_fit_adds_the_warning_and_is_never_shown_green()
    {
        var est = new BridgeProtocol.EstimateBlock { ChiPassed = true, ObjectiveJ = 41.26, MeasurementsN = 120, IrlsCapReached = true };
        Assert.Equal("chi-square PASS  *  J=41.3  n=120  *  " + BridgeProtocol.EstimateBlock.IrlsCapWarning, est.QualitySummary());
        Assert.False(est.QualityOk);
    }

    [Fact]
    public void The_wire_field_is_optional_camel_case_and_the_derived_flag_is_not_written()
    {
        var absent = JsonSerializer.Deserialize<BridgeProtocol.EstimateBlock>("{\"chiPassed\":true}", BridgeProtocol.JsonOptions)!;
        Assert.Null(absent.IrlsCapReached);
        var present = JsonSerializer.Deserialize<BridgeProtocol.EstimateBlock>("{\"chiPassed\":true,\"irlsCapReached\":true}", BridgeProtocol.JsonOptions)!;
        Assert.True(present.IrlsCapReached);
        string written = JsonSerializer.Serialize(present, BridgeProtocol.JsonOptions);
        Assert.Contains("\"irlsCapReached\":true", written, StringComparison.Ordinal);
        Assert.DoesNotContain("qualityOk", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Both_shells_colour_the_pill_with_quality_ok()
    {
        Assert.Contains("est.QualityOk ?", Src("GridSim.Web", "Components", "Pages", "Dashboard.razor"), StringComparison.Ordinal);
        Assert.Contains("est.QualityOk ? BridgeOkBrush : BridgeBadBrush", Src("GridSim.Wpf", "MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ web harmonics: default unchanged, opt-in sources

    private static void AssertSameStudy(HarmonicsDto a, HarmonicsDto b)
    {
        Assert.Equal(a.Orders, b.Orders);
        Assert.Equal(a.Indices.WorstBusId, b.Indices.WorstBusId);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Indices.WorstThdPct), BitConverter.DoubleToInt64Bits(b.Indices.WorstThdPct));
        Assert.Equal(a.BusSpectra.Count, b.BusSpectra.Count);
        for (int i = 0; i < a.BusSpectra.Count; i++)
        {
            Assert.Equal(a.BusSpectra[i].BusId, b.BusSpectra[i].BusId);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.BusSpectra[i].ThdPct), BitConverter.DoubleToInt64Bits(b.BusSpectra[i].ThdPct));
            for (int k = 0; k < a.BusSpectra[i].Orders.Count; k++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.BusSpectra[i].Orders[k].MagnitudePct),
                             BitConverter.DoubleToInt64Bits(b.BusSpectra[i].Orders[k].MagnitudePct));
        }
        Assert.Equal(a.Scan!.ResonanceOrder, b.Scan!.ResonanceOrder);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Scan.PeakMagnitude), BitConverter.DoubleToInt64Bits(b.Scan.PeakMagnitude));
    }

    [Fact]
    public void The_default_web_study_is_unchanged_and_reports_no_component_outside_its_orders()
    {
        GridModel case9 = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(case9);
        HarmonicsDto old = SimSession.StudyHarmonics(case9, r);
        HarmonicsDto syn = SimSession.StudyHarmonics(case9, r, HarmonicSourceChoice.Synthetic);
        AssertSameStudy(old, syn);
        Assert.Equal(old.SourceNote, syn.SourceNote);
        Assert.NotNull(syn.ComponentsOutsideOrders);
        Assert.Empty(syn.ComponentsOutsideOrders!);
        Assert.Null(syn.ConverterSources);

        // The same numbers as the engine called directly with the default source and options (bit for bit).
        // Moved 2026-10 (N131, Ruling R1, Mark delegated 2026-10-04): the default synthetic source is the G5 Issue 5
        // Table B.3 six-pulse profile, so the reference call names HarmonicSourceLibrary.DefaultSyntheticSourceType
        // ("6-pulse-g5") instead of "6-pulse" (1/h). Worst THD on case9 before -> after: 6.25287132456945 % (bits
        // 4618726125669219563) -> 6.311536972429553 % (bits 4618792177316680115). The 1/h study is still reachable bit
        // for bit through HarmonicSourceChoice.SyntheticIdeal (R1DefaultsTests).
        int srcBus = case9.Buses.OrderByDescending(b => b.Pd).First().Id;
        var sources = new[] { HarmonicSourceLibrary.Make(HarmonicSourceLibrary.DefaultSyntheticSourceType, srcBus, fundamentalCurrentPu: 0.5) };
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(case9, sources, HarmonicModelOptions.Study);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen, HarmonicIndices.SolvedFundamentalVm(pen, r));
        Assert.Equal(BitConverter.DoubleToInt64Bits(idx.WorstThdPct), BitConverter.DoubleToInt64Bits(syn.Indices.WorstThdPct));
    }

    [Fact]
    public void A_per_converter_study_on_a_case_with_no_converter_is_a_named_failure_not_a_result()
    {
        GridModel case9 = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(case9);
        HarmonicsDto dto = SimSession.StudyHarmonics(case9, r, HarmonicSourceChoice.ConverterRated);
        Assert.True(dto.Failed);
        Assert.Contains("no converter-interfaced unit that injects", dto.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HarmonicSourceChoice.ConverterRated, ConverterHarmonicLevel.Rated, "rated")]
    [InlineData(HarmonicSourceChoice.ConverterDispatched, ConverterHarmonicLevel.Dispatched, "dispatched")]
    public void The_per_converter_web_study_is_the_core_rule_bit_for_bit_and_lists_what_it_left_out(
        HarmonicSourceChoice choice, ConverterHarmonicLevel level, string word)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(uk);
        Assert.True(r.Converged);
        HarmonicsDto dto = SimSession.StudyHarmonics(uk, r, choice);
        Assert.False(dto.Failed, dto.Error);

        // Moved 2026-10 (N150, Ruling R1, Mark delegated 2026-10-04; the D-DOC-054 rule): the dispatched level now reads
        // each unit's SOLVED output from the operating point, so the reference call is given the same solve the web
        // study uses. The test's intent (the web study is the Core rule bit for bit) is unchanged. Before: FromModel(uk,
        // level, balanced: true) on the schedule, uk dispatched worst THD 2.429044746062342 %. After: with
        // operatingPoint: r, 2.45829149297198 % (the rated row is unchanged: the rated level ignores the solve).
        ConverterHarmonicSourceSet set = ConverterHarmonicSources.FromModel(uk, level, balanced: true, operatingPoint: r);
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(uk, set.Sources, ConverterHarmonicSources.StudyOptions);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen, HarmonicIndices.SolvedFundamentalVm(pen, r));
        Assert.Equal(ConverterHarmonicSources.StudyOrders, dto.Orders);
        Assert.Equal(idx.WorstBusId, dto.Indices.WorstBusId);
        Assert.Equal(BitConverter.DoubleToInt64Bits(idx.WorstThdPct), BitConverter.DoubleToInt64Bits(dto.Indices.WorstThdPct));

        HarmonicConverterSourcesDto conv = Assert.IsType<HarmonicConverterSourcesDto>(dto.ConverterSources);
        Assert.Equal(word, conv.Level);
        Assert.Equal(set.Sources.Count, conv.SelfCommutated + conv.LineCommutated);
        Assert.Equal(set.Sources.Count(s => s.SourceType == ConverterHarmonicSources.SelfCommutatedProfile), conv.SelfCommutated);
        Assert.Equal(set.OmittedComponents, conv.OmittedComponents);
        Assert.Equal(set.UnitsNotModelled, conv.UnitsNotModelled);
        Assert.NotEmpty(conv.OmittedComponents);   // the AFE triplens the balanced study cannot carry
        Assert.Equal(HarmonicsDto.ConverterSourceNote(conv), dto.SourceNote);
        Assert.Empty(dto.ComponentsOutsideOrders!);   // orders 2 to 50 cover Table B.3
    }

    [Fact]
    public void The_tab_offers_the_source_choice_and_shows_the_omissions()
    {
        string tab = Src("GridSim.Web", "Components", "Tabs", "HarmonicsTab.razor");
        Assert.Contains("RunStudyWith", tab, StringComparison.Ordinal);
        Assert.Contains("res.ComponentsOutsideOrders is { Count: > 0 } outside", tab, StringComparison.Ordinal);
        Assert.Contains("res.ConverterSources is { } conv", tab, StringComparison.Ordinal);
        Assert.Contains("RunStudyWith=\"_session.RunHarmonicsStudyWith\"", Src("GridSim.Web", "Components", "Pages", "Dashboard.razor"), StringComparison.Ordinal);
        // The default choice is the synthetic study.
        Assert.Contains("private HarmonicSourceChoice _choice = HarmonicSourceChoice.Synthetic;", tab, StringComparison.Ordinal);
    }
}
