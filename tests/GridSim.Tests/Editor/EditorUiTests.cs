using GridSim.Core;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.SchemaEditor;
using GridSim.SchemaEditor.Ui;
using Xunit;
using static GridSim.Tests.Editor.CaseSessionTests;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// Slice E5 of the case editor (SCHEMA-EDITOR-PLAN.md section 10): the GridSim.SchemaEditor app skeleton. Key-sequence
/// tests through the real state machine and renderer (no console): browse the v1 tree, edit a field in place, save,
/// quit prompts, the screen layout, the small-console notice, and the --check exit codes.
/// </summary>
public class EditorUiTests
{
    private static EditorDriver Open(string dir, string display = "data/uk") => new(CaseSession.OpenV1(dir, Unlimited()), display);

    [Fact]
    public void The_root_screen_shows_title_breadcrumb_sections_and_key_bar()
    {
        // Test input changed 2026-10-24 (agreed by Mark): the display name was "data/uk" (7 characters), which made the
        // 80-column title exactly 80 wide once the version became "2.0.NNN" (7 characters, one fewer than "1.95.NNN"),
        // so the tier fitted and the rule below was no longer exercised. With a 10-character display name the title
        // with the tier is wider than 80 and without it fits, for any version string of 5 to 10 characters.
        EditorDriver d = Open(CaseDir("uk"), "bundled/uk");
        ScreenBuffer s = d.Screen();
        string title = s.RowText(0);
        // At 80 columns the licence tier does not fit beside the [BUNDLED] flag, so the flag wins; at 100 both show.
        Assert.Equal($"GRIDSIM CASE EDITOR {BuildInfo.Version}  bundled/uk (v1)  20 buses  [BUNDLED]", title.TrimEnd());
        Assert.Equal($"GRIDSIM CASE EDITOR {BuildInfo.Version}  bundled/uk (v1)  20 buses  Network Operator  [BUNDLED]", d.Screen(100, 25).RowText(0).TrimEnd());
        Assert.Equal(CellStyle.Reverse, s.StyleAt(0, 0));
        Assert.StartsWith("Case", s.RowText(1), StringComparison.Ordinal);
        Assert.Contains("section", s.RowText(2), StringComparison.Ordinal);
        Assert.StartsWith("  System", s.RowText(3), StringComparison.Ordinal);
        Assert.Equal(CellStyle.Reverse, s.StyleAt(5, 3));   // the first row is highlighted
        Assert.StartsWith("  Buses", s.RowText(4), StringComparison.Ordinal);
        Assert.StartsWith("  Generation", s.RowText(5), StringComparison.Ordinal);
        Assert.Contains("29", s.RowText(5), StringComparison.Ordinal);
        Assert.Contains("Not part of the case", s.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("F1 Help  Enter Open  Esc Up  F2 Edit  U Undo  F9 Check  F10 Save  F12 Quit", s.RowText(24), StringComparison.Ordinal);
        Assert.Equal(CellStyle.Reverse, s.StyleAt(79, 24));
    }

    [Fact]
    public void The_title_shows_the_tier_exactly_when_there_is_room_for_it_whatever_the_version_string()
    {
        // The rule itself, at its boundary widths, so it holds however long the version string is (2026-10-24: the
        // 80-column screen tests above depended on that length).
        EditorDriver d = Open(CaseDir("uk"));
        string head = $"GRIDSIM CASE EDITOR {BuildInfo.Version}  data/uk (v1)  20 buses";
        const string tier = "  Network Operator", flags = "  [BUNDLED]";
        Assert.Equal(head + tier + flags, Renderer.Title(d.Editor, (head + tier + flags).Length));
        Assert.Equal(head + flags, Renderer.Title(d.Editor, (head + tier + flags).Length - 1));
        Assert.Equal(head + flags, Renderer.Title(d.Editor, (head + flags).Length));
    }

    [Fact]
    public void A_window_below_80_by_24_shows_only_a_notice()
    {
        EditorDriver d = Open(CaseDir("uk"));
        ScreenBuffer s = d.Screen(79, 30);
        Assert.StartsWith("Window too small: the case editor needs 80x24, this one is 79x30.", s.RowText(0), StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(s.RowText(1)));
        Assert.StartsWith("Window too small", d.Screen(100, 23).RowText(0), StringComparison.Ordinal);
    }

    [Fact]
    public void Browse_uk_to_a_hydro_unit_edit_its_capacity_save_and_find_exactly_that_token_changed()
    {
        // The plan's key-sequence: uk > Generation > hydro > station > unit > capacityMva, F10, file token checked.
        using var copy = new TempCopy(CaseDir("uk"));
        byte[] before = File.ReadAllBytes(Path.Combine(copy.Dir, "generation.json"));
        // Test input changed 2026-10-24 (agreed by Mark): the display name was "uk-copy" (7 characters), which let the
        // full modified-file flag fit in exactly 80 columns once the version became "2.0.NNN", so the count form below
        // was no longer exercised. A 10-character name keeps it exercised for any version string of 5 to 10 characters.
        EditorDriver d = Open(copy.Dir, "uk-working");

        d.Select("Generation").Keys(ConsoleKey.Enter);
        Assert.Equal("Case > Generation", d.Screen().RowText(1).TrimEnd());
        d.Select("hydro").Keys(ConsoleKey.Enter);
        d.Select("North Scotland Hydro").Keys(ConsoleKey.Enter);
        Assert.Equal("Case > Generation > hydro > North Scotland Hydro (1 units)", d.Screen().RowText(1).TrimEnd());
        Assert.StartsWith("#0", d.CurrentRow, StringComparison.Ordinal);
        d.Keys(ConsoleKey.Enter);
        // 87 characters do not fit in 80: the leading levels give way to "... > " so the current record stays visible.
        Assert.Equal("... > hydro > North Scotland Hydro (1 units) > #0 'North Scotland Hydro'", d.Screen().RowText(1).TrimEnd());
        Assert.Equal("Case > Generation > hydro > North Scotland Hydro (1 units) > #0 'North Scotland Hydro'", d.Screen(100, 25).RowText(1).TrimEnd());

        d.Select("Capacity (MVA)");
        Assert.Contains("1300", d.CurrentRow, StringComparison.Ordinal);
        d.Keys(ConsoleKey.Enter);
        Assert.Equal(EditorMode.EditField, d.Editor.Mode);
        d.Keys("1400", ConsoleKey.Enter);                     // typing replaces the value
        Assert.Equal(EditorMode.Browse, d.Editor.Mode);
        Assert.Contains("1300 -> 1400", d.Editor.Message, StringComparison.Ordinal);
        Assert.Contains("1400", d.CurrentRow, StringComparison.Ordinal);
        // At 80 columns the modified-file list is shortened to a count so the flag always shows; wider, it is listed.
        Assert.EndsWith("  [MODIFIED: 1 file(s)]", d.Screen().RowText(0).TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith("  [MODIFIED: generation.json]", d.Screen(120, 25).RowText(0).TrimEnd(), StringComparison.Ordinal);

        d.Keys(ConsoleKey.F10);
        Assert.StartsWith("Saved 1 file(s): generation.json.", d.Editor.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MODIFIED", d.Screen().RowText(0), StringComparison.Ordinal);
        byte[] after = File.ReadAllBytes(Path.Combine(copy.Dir, "generation.json"));
        string expected = System.Text.Encoding.UTF8.GetString(before);
        int at = expected.IndexOf("\"capacityMva\": 1300", StringComparison.Ordinal);
        expected = expected[..at] + "\"capacityMva\": 1400" + expected[(at + "\"capacityMva\": 1300".Length)..];
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(after));
    }

    [Fact]
    public void F2_keeps_the_text_a_refused_value_stays_in_the_edit_and_Esc_cancels()
    {
        EditorDriver d = Open(CaseDir("uk"));
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("hydro").Keys(ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter);
        d.Select("Capacity (MVA)").Keys(ConsoleKey.F2);
        Assert.Equal("1300", d.Editor.Line!.Text);
        d.Keys("x", ConsoleKey.Enter);                                   // "1300x" is not a number
        Assert.Equal(EditorMode.EditField, d.Editor.Mode);
        Assert.True(d.Editor.MessageIsError);
        Assert.Contains("not a JSON number", d.Editor.Message, StringComparison.Ordinal);
        d.Keys(ConsoleKey.Escape);
        Assert.Equal(EditorMode.Browse, d.Editor.Mode);
        Assert.False(d.Editor.Session.IsDirty);
    }

    [Fact]
    public void The_in_place_edit_is_drawn_in_the_value_column_with_the_caret()
    {
        EditorDriver d = Open(CaseDir("uk"));
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("hydro").Keys(ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter);
        d.Select("Capacity (MVA)").Keys(ConsoleKey.F2, "5");
        ScreenBuffer s = d.Screen();
        int y = s.Caret!.Value.Y;
        Assert.Equal("13005", s.RowText(y).Substring(Renderer.FormValueColumn, 5));
        Assert.Equal(Renderer.FormValueColumn + 5, s.Caret.Value.X);
    }

    [Fact]
    public void Space_toggles_a_boolean_and_steps_an_enum_and_U_undoes()
    {
        EditorDriver d = Open(CaseDir("uk"));
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("hydro").Keys(ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter);
        d.Select("Power-flow-only device").Keys(ConsoleKey.Spacebar);
        Assert.Contains("true", d.CurrentRow, StringComparison.Ordinal);
        d.Select("Role").Keys(ConsoleKey.Spacebar);
        Assert.Contains("\"pv\"", d.CurrentRow, StringComparison.Ordinal);
        d.Keys("u", "u");
        Assert.False(d.Editor.Session.IsDirty);
        d.Keys("U");                                                      // Shift+U redoes
        Assert.True(d.Editor.Session.IsDirty);
    }

    [Fact]
    public void Del_resets_a_field_to_absent_and_shows_its_default()
    {
        EditorDriver d = Open(CaseDir("uk"));
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("hydro").Keys(ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter);
        d.Select("Voltage setpoint (pu)").Keys(ConsoleKey.Delete);
        Assert.Contains("(absent: 1)", d.CurrentRow, StringComparison.Ordinal);
        d.Select("Bus").Keys(ConsoleKey.Delete);
        Assert.True(d.Editor.MessageIsError);   // required
    }

    [Fact]
    public void Quit_with_unsaved_changes_asks_and_N_quits_without_writing()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        var before = copy.Snapshot();
        EditorDriver d = Open(copy.Dir);
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("hydro").Keys(ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter);
        d.Select("Capacity (MVA)").Keys(ConsoleKey.Enter, "9", ConsoleKey.Enter);
        d.Keys(ConsoleKey.F12);
        Assert.Equal(EditorMode.Confirm, d.Editor.Mode);
        Assert.Equal("Unsaved changes (1 file(s)). Save before quitting? Y yes, N no, Esc cancel.", d.Screen().RowText(23).TrimEnd());
        d.Keys(ConsoleKey.Escape);                                        // cancel
        Assert.False(d.Editor.Finished);
        d.Keys(EditorDriver.Ctrl(ConsoleKey.C), "n");                      // Ctrl+C goes through the same question
        Assert.True(d.Editor.Finished);
        foreach ((string f, (byte[] bytes, _)) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(copy.Dir, f)));
    }

    [Fact]
    public void Quit_with_unsaved_changes_and_Y_saves_then_quits()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        EditorDriver d = Open(copy.Dir);
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("hydro").Keys(ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter);
        d.Select("Capacity (MVA)").Keys(ConsoleKey.Enter, "1500", ConsoleKey.Enter, "q", "y");
        Assert.True(d.Editor.Finished);
        Assert.Contains("\"capacityMva\": 1500", File.ReadAllText(Path.Combine(copy.Dir, "generation.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Esc_at_the_root_of_a_clean_case_quits_at_once()
    {
        EditorDriver d = Open(CaseDir("uk"));
        d.Keys(ConsoleKey.Escape);
        Assert.True(d.Editor.Finished);
    }

    [Fact]
    public void A_refused_save_is_never_shown_as_success_and_opens_the_error_list()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        var before = copy.Snapshot();
        EditorDriver d = Open(copy.Dir);
        // Take away the only slack: valid as a record, refused by the loader at save.
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("ccgt").Keys(ConsoleKey.Enter).Select("marginal").Keys(ConsoleKey.Enter, ConsoleKey.Enter);
        d.Select("Role").Keys(ConsoleKey.Enter, "pv", ConsoleKey.Enter);
        d.Keys("s");
        Assert.True(d.Editor.MessageIsError);
        Assert.StartsWith("NOT SAVED: 1 error(s)", d.Editor.Message, StringComparison.Ordinal);
        Assert.Equal("Errors", d.Editor.Top.View.Crumb);
        Assert.Contains("slack", d.CurrentRow, StringComparison.OrdinalIgnoreCase);
        foreach ((string f, (byte[] bytes, _)) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(copy.Dir, f)));
    }

    [Fact]
    public void Help_lists_every_binding_from_the_key_table()
    {
        EditorDriver d = Open(CaseDir("uk"));
        d.Keys(ConsoleKey.F1);
        Assert.Equal("Help", d.Editor.Top.View.Crumb);
        Assert.Equal(KeyMap.Bindings.Count, d.Editor.Top.Visible.Count);
        d.Keys(ConsoleKey.Escape);
        Assert.Equal("Case", d.Editor.Top.View.Crumb);
    }

    [Fact]
    public void Every_command_has_a_plain_letter_or_an_unmodified_key()
    {
        // Risk R2: terminals capture F-keys and Ctrl chords, so no command may be reachable only through them.
        foreach (Command c in Enum.GetValues<Command>().Where(c => c != Command.None))
        {
            var bindings = KeyMap.Bindings.Where(b => b.Command == c).ToList();
            Assert.True(bindings.Count > 0, $"{c} has no key");
            Assert.True(bindings.Any(b => b.Char is not null || (b.Key is ConsoleKey k && b.Modifiers == 0 && !(k >= ConsoleKey.F1 && k <= ConsoleKey.F24))),
                $"{c} is reachable only by an F-key or a Ctrl chord");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // --check and exit codes
    // ---------------------------------------------------------------------------------------------------------------

    private static (int Code, string Out, string Err) RunCli(params string[] args)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        int code = EntryPoint.Run(args, _ => Unlimited(), o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Check_on_a_bundled_case_exits_0_and_prints_the_report()
    {
        (int code, string output, _) = RunCli(CaseDir("uk"), "--check");
        Assert.Equal(0, code);
        Assert.StartsWith("CHECK PASSED", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_of_a_case_with_schema_errors_exits_3()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        string gen = Path.Combine(copy.Dir, "generation.json");
        File.WriteAllText(gen, File.ReadAllText(gen).Replace("\"inertiaH\"", "\"InertiaH\"", StringComparison.Ordinal));
        (int code, string output, _) = RunCli(copy.Dir, "--check");
        Assert.Equal(3, code);
        Assert.StartsWith("CHECK FAILED", output, StringComparison.Ordinal);
        Assert.Contains("InertiaH", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_folder_exits_1_and_usage_errors_exit_2()
    {
        Assert.Equal(1, RunCli(Path.Combine(Path.GetTempPath(), "no-such-case-" + Guid.NewGuid().ToString("N")), "--check").Code);
        Assert.Equal(1, RunCli(Path.Combine(CaseDir("uk"), "buses.json"), "--check").Code);
        Assert.Equal(2, RunCli().Code);
        Assert.Equal(2, RunCli(CaseDir("uk"), "--frobnicate").Code);
        Assert.Equal(2, RunCli(CaseDir("uk"), CaseDir("gb-full")).Code);
        Assert.Equal(0, RunCli("--help").Code);
    }

    [Fact]
    public void A_case_the_licence_refuses_exits_2()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        using var o = new StringWriter();
        using var e = new StringWriter();
        Assert.Equal(2, EntryPoint.Run(new[] { copy.Dir, "--check" }, _ => Limited(10), o, e));
        Assert.Contains("20 buses", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_interactive_editor_refuses_a_redirected_console_with_exit_2()
    {
        // Test runners redirect standard output, so this is the path a script that forgets --check takes.
        if (!Console.IsOutputRedirected && !Console.IsInputRedirected) return;
        Assert.Equal(2, RunCli(CaseDir("uk")).Code);
    }
}
