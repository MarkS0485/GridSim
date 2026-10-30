using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.SchemaEditor.Ui;
using Xunit;
using static GridSim.Tests.Editor.CaseSessionTests;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// Slice E6 of the case editor (SCHEMA-EDITOR-PLAN.md section 10): the inventory commands, one key-sequence test per
/// command, with refusals and undo. Every test runs on the bundled uk case in memory (nothing is saved) unless it says
/// otherwise.
/// </summary>
public class InventoryUiTests
{
    private static EditorDriver Uk() => new(CaseSession.OpenV1(CaseDir("uk"), Unlimited()), "data/uk");

    private static int Count(EditorDriver d, string file) => d.Editor.Session.Doc(file).Text.Root.Elements.Count;

    [Fact]
    public void A_in_a_station_adds_a_unit_with_its_name_type_and_bus_preset_and_U_removes_it()
    {
        EditorDriver d = Uk();
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("hydro").Keys(ConsoleKey.Enter).Select("North Scotland Hydro").Keys(ConsoleKey.Enter);
        d.Keys("a");
        Assert.False(d.Editor.MessageIsError, d.Editor.Message);
        Assert.Equal(30, Count(d, "generation.json"));
        JsonSourceNode added = d.Editor.Session.Doc("generation.json").Text.Root.Elements[1];
        StringComparer ci = StringComparer.OrdinalIgnoreCase;
        Assert.Equal("North Scotland Hydro", RecordRead.Str(added, "name", ci));
        Assert.Equal("hydro", RecordRead.Str(added, "type", ci));
        Assert.Equal(1, RecordRead.Int(added, "bus", ci));
        Assert.Equal(2, d.Editor.Top.Visible.Count);           // the station now has two units
        Assert.StartsWith("#1", d.CurrentRow, StringComparison.Ordinal);   // the selection is on the new unit
        d.Keys("u");
        Assert.Equal(29, Count(d, "generation.json"));
        Assert.False(d.Editor.Session.IsDirty);
    }

    [Fact]
    public void A_in_a_voltage_group_adds_a_bus_with_the_next_id_and_that_baseKv()
    {
        EditorDriver d = Uk();
        d.Select("Buses").Keys(ConsoleKey.Enter).Select("400 kV").Keys(ConsoleKey.Enter);
        d.Keys("a");
        Assert.Equal(EditorMode.Prompt, d.Editor.Mode);
        Assert.StartsWith("New bus: Name (Enter skips) =", d.Screen().RowText(23), StringComparison.Ordinal);
        d.Keys("Test bus", ConsoleKey.Enter);
        Assert.Equal(EditorMode.Browse, d.Editor.Mode);
        Assert.Equal(21, Count(d, "buses.json"));
        JsonSourceNode bus = d.Editor.Session.Doc("buses.json").Text.Root.Elements[20];
        Assert.Equal(111, RecordRead.Int(bus, "id", StringComparer.OrdinalIgnoreCase));
        Assert.Equal(400.0, RecordRead.Num(bus, "baseKv", StringComparer.OrdinalIgnoreCase));
        Assert.Equal("Test bus", RecordRead.Str(bus, "name", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_refuses_a_missing_required_value_and_a_bus_that_does_not_exist()
    {
        EditorDriver d = Uk();
        d.Select("Demand").Keys(ConsoleKey.Enter, "a", "Test load", ConsoleKey.Enter);
        Assert.StartsWith("New demand: Bus =", d.Screen().RowText(23), StringComparison.Ordinal);
        d.Keys(ConsoleKey.Enter);                                        // empty: required, the prompt stays
        Assert.Equal(EditorMode.Prompt, d.Editor.Mode);
        Assert.True(d.Editor.MessageIsError);
        d.Keys("999", ConsoleKey.Enter);
        Assert.Equal(EditorMode.Browse, d.Editor.Mode);
        Assert.True(d.Editor.MessageIsError);
        Assert.Contains("bus 999 does not exist", d.Editor.Message, StringComparison.Ordinal);
        Assert.False(d.Editor.Session.IsDirty);
    }

    [Fact]
    public void D_duplicates_a_line_and_says_the_copy_shares_its_name()
    {
        EditorDriver d = Uk();
        int lines = Count(d, "transmission.json");
        d.Select("Lines").Keys(ConsoleKey.Enter, "d");
        Assert.Equal(lines + 1, Count(d, "transmission.json"));
        Assert.Contains("shares the name", d.Editor.Message, StringComparison.Ordinal);
        Assert.StartsWith("#1", d.CurrentRow, StringComparison.Ordinal);
        d.Keys(ConsoleKey.F5);
        Assert.Equal(lines + 2, Count(d, "transmission.json"));
        d.Keys("u", "u");
        Assert.False(d.Editor.Session.IsDirty);
    }

    [Fact]
    public void X_asks_first_then_deletes_and_refuses_a_referenced_bus()
    {
        EditorDriver d = Uk();
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("solar").Keys(ConsoleKey.Enter, ConsoleKey.Enter, "x");
        Assert.Equal(EditorMode.Confirm, d.Editor.Mode);
        Assert.StartsWith("Delete generation.json #", d.Screen().RowText(23), StringComparison.Ordinal);
        d.Keys("n");
        Assert.Equal(29, Count(d, "generation.json"));
        d.Keys("x", "y");
        Assert.Equal(28, Count(d, "generation.json"));
        d.Keys("u");
        Assert.Equal(29, Count(d, "generation.json"));

        d.Keys(ConsoleKey.Escape, ConsoleKey.Escape, ConsoleKey.Escape);   // unit list -> stations -> fuel types -> the root
        d.Select("Buses").Keys(ConsoleKey.Enter).Select("400 kV").Keys(ConsoleKey.Enter, "x", "y");
        Assert.True(d.Editor.MessageIsError);
        Assert.Contains("refer to", d.Editor.Message, StringComparison.Ordinal);
        Assert.Equal(20, Count(d, "buses.json"));
    }

    [Fact]
    public void R_renames_a_bus_id_after_showing_the_cascade_and_refuses_a_collision()
    {
        EditorDriver d = Uk();
        int refs = ReferenceIndex.Build(d.Editor.Session).BusReferrers(1).Count;
        d.Select("Buses").Keys(ConsoleKey.Enter).Select("400 kV").Keys(ConsoleKey.Enter);
        Assert.StartsWith("#0", d.CurrentRow, StringComparison.Ordinal);

        d.Keys("r", ConsoleKey.Backspace, "2", ConsoleKey.Enter);       // bus 2 exists
        Assert.Equal(EditorMode.Prompt, d.Editor.Mode);
        Assert.Contains("already used", d.Editor.Message, StringComparison.Ordinal);
        d.Keys(ConsoleKey.Escape);

        d.Keys("r", "001", ConsoleKey.Enter);                            // "1" + "001" = 1001
        Assert.Equal(EditorMode.Confirm, d.Editor.Mode);
        Assert.Contains($"{refs} reference(s) follow", d.Editor.ConfirmText, StringComparison.Ordinal);
        d.Keys("y");
        ReferenceIndex ix = ReferenceIndex.Build(d.Editor.Session);
        Assert.Empty(ix.BusReferrers(1));
        Assert.Equal(refs, ix.BusReferrers(1001).Count);
        d.Keys("u");
        Assert.False(d.Editor.Session.IsDirty);
    }

    [Fact]
    public void R_on_a_station_renames_every_unit()
    {
        EditorDriver d = Uk();
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("ccgt").Keys(ConsoleKey.Enter).Select("London CCGT");
        d.Keys("r", " B", ConsoleKey.Enter);
        Assert.False(d.Editor.MessageIsError, d.Editor.Message);
        Assert.Contains(d.Editor.Top.Visible, i => d.Editor.Top.View.Row(i).StartsWith("London CCGT B", StringComparison.Ordinal));
        Assert.DoesNotContain(d.Editor.Top.Visible, i => d.Editor.Top.View.Row(i).StartsWith("London CCGT ", StringComparison.Ordinal) && !d.Editor.Top.View.Row(i).StartsWith("London CCGT B", StringComparison.Ordinal));
    }

    [Fact]
    public void Slash_filters_the_list_N_moves_on_and_an_empty_filter_shows_all()
    {
        EditorDriver d = Uk();
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("ccgt").Keys(ConsoleKey.Enter);
        int all = d.Editor.Top.Visible.Count;
        d.Keys("/", "ccgt", ConsoleKey.Enter);
        Assert.Equal(all, d.Editor.Top.Visible.Count);                  // every ccgt station name holds CCGT
        d.Keys("/", ConsoleKey.Backspace, ConsoleKey.Backspace, ConsoleKey.Backspace, ConsoleKey.Backspace, "wales", ConsoleKey.Enter);
        Assert.Single(d.Editor.Top.Visible);
        Assert.StartsWith("North Wales CCGT", d.CurrentRow, StringComparison.Ordinal);
        d.Keys("n");
        Assert.StartsWith("North Wales CCGT", d.CurrentRow, StringComparison.Ordinal);
        d.Keys("/", ConsoleKey.Backspace, ConsoleKey.Backspace, ConsoleKey.Backspace, ConsoleKey.Backspace, ConsoleKey.Backspace, ConsoleKey.Enter);
        Assert.Equal(all, d.Editor.Top.Visible.Count);
    }

    [Fact]
    public void F_finds_names_across_sections_and_Enter_opens_the_record()
    {
        EditorDriver d = Uk();
        d.Keys("f", "drax", ConsoleKey.Enter);
        Assert.Equal("Find 'drax'", d.Editor.Top.View.Crumb);
        Assert.Single(d.Editor.Top.Visible);
        Assert.Contains("Drax", d.CurrentRow, StringComparison.Ordinal);
        d.Keys(ConsoleKey.Enter);
        Assert.Contains("generation.json", d.Editor.Top.View.Crumb, StringComparison.Ordinal);
        Assert.Contains(d.Editor.Top.Visible, i => d.Editor.Top.View.Row(i).Contains("Drax Biomass", StringComparison.Ordinal));
    }

    [Fact]
    public void G_goes_to_a_bus_and_refuses_one_that_does_not_exist()
    {
        EditorDriver d = Uk();
        d.Keys("g", "105", ConsoleKey.Enter);
        Assert.Equal("bus 105", d.Editor.Top.View.Crumb);
        d.Keys("g", "999", ConsoleKey.Enter);
        Assert.Equal(EditorMode.Prompt, d.Editor.Mode);
        Assert.Contains("no bus 999", d.Editor.Message, StringComparison.Ordinal);
        d.Keys(ConsoleKey.Escape);
        Assert.Equal("bus 105", d.Editor.Top.View.Crumb);
    }

    [Fact]
    public void J_jumps_from_a_bus_field_to_the_bus()
    {
        EditorDriver d = Uk();
        d.Select("Generation").Keys(ConsoleKey.Enter).Select("hydro").Keys(ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter);
        d.Select("Bus").Keys("j");
        Assert.Equal("bus 1", d.Editor.Top.View.Crumb);
        Assert.Contains(d.Editor.Top.Visible, i => d.Editor.Top.View.Row(i).Contains("North Scotland 400kV", StringComparison.Ordinal));
    }

    [Fact]
    public void W_lists_where_a_bus_is_used_and_Enter_opens_a_referrer()
    {
        EditorDriver d = Uk();
        int refs = ReferenceIndex.Build(d.Editor.Session).BusReferrers(1).Count;
        d.Select("Buses").Keys(ConsoleKey.Enter).Select("400 kV").Keys(ConsoleKey.Enter, "w");
        Assert.StartsWith("Where used:", d.Editor.Top.View.Crumb, StringComparison.Ordinal);
        Assert.Equal(refs, d.Editor.Top.Visible.Count);
        d.Keys(ConsoleKey.Enter);
        Assert.IsType<GridSim.SchemaEditor.Ui.Views.RecordFormView>(d.Editor.Top.View);
    }

    [Fact]
    public void Enter_on_an_error_opens_the_record_it_names()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        string gen = Path.Combine(copy.Dir, "generation.json");
        File.WriteAllText(gen, File.ReadAllText(gen).Replace("\"inertiaH\"", "\"InertiaH\"", StringComparison.Ordinal));
        var d = new EditorDriver(CaseSession.OpenV1(copy.Dir, Unlimited()));
        d.Keys("k");
        Assert.Equal("Errors", d.Editor.Top.View.Crumb);
        Assert.Contains("InertiaH", d.CurrentRow, StringComparison.Ordinal);
        d.Keys(ConsoleKey.Enter);
        var form = Assert.IsType<GridSim.SchemaEditor.Ui.Views.RecordFormView>(d.Editor.Top.View);
        Assert.Equal("/0", form.ObjectPointer);
        // L normalises the spelling; the check then passes.
        d.Keys("l", "k");
        Assert.StartsWith("CHECK PASSED", d.Editor.Message, StringComparison.Ordinal);
    }
}
