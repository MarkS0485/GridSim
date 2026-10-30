using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Tests.Editor;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 5g (2026-10-20): the HVDC link of the asset overlay, headless, on the 5-view fixture
/// (UiOverlayFixture: 'ov.hvdc' groups the authored generators Moyle, an interconnector, and Torness Nuclear). Pinned: the
/// page's facts and tables as written; in Run mode the SOLVED output of each end equals the frame model's Pg of that
/// generator bit for bit, frame after frame (read-only; labelled solved); the sum and loss check is display only; the
/// CHECK consistency notes (both ends the same MW sign, an end above the rating) are display only and never errors; and
/// the frames are bit-identical with and without the overlay.
/// </summary>
public sealed class UiHvdcLinkPagesTests
{
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static (LiveLoadResult Loaded, (SimFrame Frame, string Clock)[] Frames) Run(string manifest, int count = 3)
    {
        var host = new SimHost();
        var dash = new DashboardModel();
        LiveLoadResult loaded = host.LoadLive(ModelCatalog.V2CaseSource(manifest), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.True(loaded.Loaded, loaded.Refusal);
        var frames = new (SimFrame, string)[count];
        for (int i = 0; i < frames.Length; i++)
        {
            HostFrame hf = Assert.IsType<HostFrame>(host.ProduceNext());
            Assert.Equal(HostFrameKind.LiveSolved, hf.Kind);
            dash.Update(hf.Frame!, hf.Boundaries, hf.SolveMs);
            frames[i] = (hf.Frame!, dash.Clock);
        }
        return (loaded, frames);
    }

    // The navigator declared on the case and fed the first frame only (the test feeds the others itself).
    private static AssetNavigatorModel Navigator(string manifest, CaseAssetOverlay? overlay, (SimFrame Frame, string Clock) first)
    {
        var nav = new AssetNavigatorModel { Active = true };
        nav.SetSource(AssetCaseSource.From(ModelCatalog.V2CaseSource(manifest)), CaseSessionTests.Unlimited(), overlay, null);
        nav.Update(first.Frame, first.Clock);
        Assert.NotNull(nav.Index);
        return nav;
    }

    private static Generator Unit(SimFrame f, string name) => f.Model.Generators.Single(g => g.Name == name);

    private static string F1(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_HVDC_page_shows_its_ends_rating_and_DC_values_and_each_end_s_solved_output_equals_the_frame_by_bits()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames[0]);
        OverlayIndex ov = nav.Index!.Overlay!;
        OverlayEntry hvdc = ov.Find("ov.hvdc")!;
        nav.Select(nav.Tree.NodeFor(hvdc.Key)!);
        AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
        Assert.Equal("HVDC link  -  carried, not used by the solver", page.Sub);
        Assert.Equal(OverlayIndex.SolverNote, page.Note);
        Assert.Equal(new[]
        {
            new OverviewFact("Sending converter", hvdc.Refs[0].Resolved),
            new OverviewFact("Receiving converter", hvdc.Refs[1].Resolved),
            new OverviewFact("Technology", "vsc"),
            new OverviewFact("Rated transfer", "500 MW"),
            new OverviewFact("DC voltage", "250 kV (pole to earth)"),
            new OverviewFact("Cable length", "55 km"),
            new OverviewFact("Losses", "not carried: the hvdcLink kind has no loss parameter; the sum of the two ends' solved outputs below is a display-only check"),
            new OverviewFact("Converters", "the authored converter generators stay what the solver uses; this record groups them and changes nothing"),
        }, page.Facts.TakeLast(8));
        Assert.Equal(new[] { OverlayKindPages.HvdcTable, OverlayKindPages.HvdcEndsTable, OverlayKindPages.HvdcCheckTable }, page.Tables.Select(t => t.Title));
        Assert.Equal(new[] { "Technology", "Rated transfer (MW)", "DC voltage (kV)", "Cable length (km)" }, page.Tables[0].Columns);
        Assert.Equal(new[] { "vsc", "500", "250", "55" }, page.Tables[0].Rows.Single().Cells);
        Assert.EndsWith(OverlayKindPages.TableTag, page.Tables[0].Title, StringComparison.Ordinal);

        // Frame after frame, the solved output of each end is the frame model's Pg, bit for bit, and the page follows it.
        foreach (var (frame, clock) in frames)
        {
            nav.Update(frame, clock);
            page = Assert.IsType<AssetOverviewPage>(nav.Current);
            IReadOnlyList<OverlayKindPages.HvdcEnd> ends = OverlayKindPages.HvdcEnds(hvdc, frame);
            Assert.Equal(new[] { "sendingConverter", "receivingConverter" }, ends.Select(x => x.Role));
            Generator moyle = Unit(frame, "Moyle"), torness = Unit(frame, "Torness Nuclear");
            Assert.Equal(Bits(moyle.Pg), Bits(ends[0].SolvedMw!.Value));
            Assert.Equal(Bits(torness.Pg), Bits(ends[1].SolvedMw!.Value));
            Assert.Equal((int?)moyle.Bus, ends[0].Bus);
            Assert.All(ends, x => Assert.Equal("solved", x.State));
            OverviewTable t = page.Tables[1];
            Assert.Equal(new[] { "End", "Generator", "Bus", "Solved output (MW)", "State" }, t.Columns);
            Assert.Equal(new[]
            {
                new[] { "sending", "Moyle", moyle.Bus.ToString(System.Globalization.CultureInfo.InvariantCulture), F1(moyle.Pg), "solved" },
                new[] { "receiving", "Torness Nuclear", torness.Bus.ToString(System.Globalization.CultureInfo.InvariantCulture), F1(torness.Pg), "solved" },
            }, t.Rows.Select(r => r.Cells.ToArray()));
            Assert.Equal("Solved: each end's output is its generator's Pg in the frame's model (positive = injecting into the AC network), read-only.", t.Note);
            // The display-only sum and loss check.
            double sum = moyle.Pg + torness.Pg;
            bool opposite = (moyle.Pg < 0 && torness.Pg > 0) || (moyle.Pg > 0 && torness.Pg < 0);
            Assert.Equal(new[]
            {
                new[] { "Sending end, solved", F1(moyle.Pg) },
                new[] { "Receiving end, solved", F1(torness.Pg) },
                new[] { "Sum of the two ends", F1(sum) },
                new[] { "Implied DC loss (minus the sum)", opposite ? F1(-sum) : "not meaningful: the ends do not have opposite signs" },
            }, page.Tables[2].Rows.Select(r => r.Cells.ToArray()));
        }

        // Before any frame there is no solved value, and the page says so.
        Assert.All(OverlayKindPages.HvdcEnds(hvdc, null), x => { Assert.Null(x.SolvedMw); Assert.Equal("no solved frame yet", x.State); });
        OverviewContent unsolved = OverlayPages.Build(hvdc.Page, nav.Index, null);
        Assert.Equal("No solved frame yet: the solved outputs appear once the case runs.", unsolved.Tables[1].Note);
        Assert.Empty(unsolved.Tables[2].Rows);
        Assert.Equal("No check: the sending end is no solved frame yet; the receiving end is no solved frame yet.", unsolved.Tables[2].Note);
    }

    // The display-only notes expected for a link between 'send' and 'recv' rated 500 MW, from the model's own dispatch.
    private static List<OverlayCheckRow> ExpectedNotes(Generator send, Generator recv)
    {
        var notes = new List<OverlayCheckRow>();
        if ((send.Pg > 0 && recv.Pg > 0) || (send.Pg < 0 && recv.Pg < 0))
            notes.Add(new("/records/12", "Both ends have the same MW sign in the model (sending '" + send.Name + "' " + F1(send.Pg) + " MW, receiving '" + recv.Name + "' "
                + F1(recv.Pg) + " MW): a link moving power has one end importing and the other exporting. Display only: the generators are solved exactly as authored."));
        foreach (Generator g in new[] { send, recv })
            if (Math.Abs(g.Pg) > 500) notes.Add(new("/records/12", "The end '" + g.Name + "' is at " + F1(g.Pg) + " MW in the model, above the link's rated transfer 500 MW. Display only."));
        return notes;
    }

    [Fact]
    public void The_consistency_notes_are_display_only_on_CHECK_ends_of_opposite_sign_give_no_sign_note()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath, 1);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames[0]);
        GridModel m = nav.Index!.Model;
        Generator moyle = m.Generators.Single(g => g.Name == "Moyle"), torness = m.Generators.Single(g => g.Name == "Torness Nuclear");
        // The fixture's link moves power: Moyle exports, Torness injects (opposite signs), so there is no sign note.
        Assert.True(moyle.Pg < 0 && torness.Pg > 0, $"Moyle {moyle.Pg:R} MW, Torness {torness.Pg:R} MW");
        OverlayEntry hvdc = nav.Index.Overlay!.Find("ov.hvdc")!;
        List<OverlayCheckRow> notes = ExpectedNotes(moyle, torness);
        // Only the rating note is left: Torness is above the link's 500 MW (Moyle is not).
        Assert.True(Math.Abs(torness.Pg) > 500 && Math.Abs(moyle.Pg) <= 500, $"Moyle {moyle.Pg:R} MW, Torness {torness.Pg:R} MW");
        Assert.Equal(new[] { new OverlayCheckRow("/records/12", "The end 'Torness Nuclear' is at " + F1(torness.Pg) + " MW in the model, above the link's rated transfer 500 MW. Display only.") }, notes);
        Assert.Equal(notes, hvdc.Notes);
        Assert.Empty(hvdc.Problems);   // never an error: the checker found nothing
        nav.Select(nav.Tree.NodeFor(hvdc.Key)!);
        Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CHECK" }, nav.SubTabs);
        OverlayCheck check = Assert.IsType<OverlayCheck>(nav.OverlayCheck);
        Assert.Empty(check.Rows);
        Assert.Empty(check.Warnings);
        Assert.Equal(notes, check.Notes);
        Assert.Equal("1 consistency note on hvdcLink 'ov.hvdc' (display only, never an error)", check.Heading);
    }

    [Fact]
    public void The_same_MW_sign_at_both_ends_is_a_display_only_consistency_note_on_CHECK()
    {
        // The link between two units that both inject (Torness and Heysham, both nuclear).
        string a = UiOverlayFixture.OverlayA.Replace("{ \"sendingConverter\": { \"generator\": \"Moyle\" }", "{ \"sendingConverter\": { \"generator\": \"Heysham Nuclear\" }", StringComparison.Ordinal);
        Assert.NotEqual(UiOverlayFixture.OverlayA, a);
        using var fx = new UiOverlayFixture(overlayA: a);
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath, 1);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames[0]);
        GridModel m = nav.Index!.Model;
        Generator heysham = m.Generators.Single(g => g.Name == "Heysham Nuclear"), torness = m.Generators.Single(g => g.Name == "Torness Nuclear");
        Assert.True(heysham.Pg > 0 && torness.Pg > 0, $"Heysham {heysham.Pg:R} MW, Torness {torness.Pg:R} MW");
        OverlayEntry hvdc = nav.Index.Overlay!.Find("ov.hvdc")!;
        List<OverlayCheckRow> notes = ExpectedNotes(heysham, torness);
        Assert.StartsWith("Both ends have the same MW sign in the model (sending 'Heysham Nuclear' ", notes[0].Message, StringComparison.Ordinal);
        Assert.Equal(notes, hvdc.Notes);
        Assert.Empty(hvdc.Problems);

        nav.Select(nav.Tree.NodeFor(hvdc.Key)!);
        Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CHECK" }, nav.SubTabs);
        OverlayCheck check = Assert.IsType<OverlayCheck>(nav.OverlayCheck);
        Assert.Empty(check.Rows);
        Assert.Empty(check.Warnings);
        Assert.Equal(notes, check.Notes);
        Assert.Equal((notes.Count == 1 ? "1 consistency note" : notes.Count + " consistency notes") + " on hvdcLink 'ov.hvdc' (display only, never an error)", check.Heading);
        // The sum check on the page says the sum is not a loss when the ends do not have opposite signs.
        AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
        Assert.Equal(new[] { "Implied DC loss (minus the sum)", "not meaningful: the ends do not have opposite signs" }, page.Tables[2].Rows[3].Cells);
    }

    [Fact]
    public void A_link_with_one_end_in_the_case_has_no_sign_note_and_an_end_above_the_rating_is_noted()
    {
        // Only the sending end, rated at 1 MW: no same-sign note (one end), but the end is above the rating.
        string a = UiOverlayFixture.OverlayA.Replace(
            "\"refs\": { \"sendingConverter\": { \"generator\": \"Moyle\" }, \"receivingConverter\": { \"generator\": \"Torness Nuclear\" } }, \"params\": { \"technology\": \"vsc\", \"ratedMw\": 500,",
            "\"refs\": { \"sendingConverter\": { \"generator\": \"Moyle\" } }, \"params\": { \"technology\": \"vsc\", \"ratedMw\": 1,", StringComparison.Ordinal);
        Assert.NotEqual(UiOverlayFixture.OverlayA, a);
        using var fx = new UiOverlayFixture(overlayA: a);
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath, 1);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames[0]);
        Generator moyle = nav.Index!.Model.Generators.Single(g => g.Name == "Moyle");
        Assert.True(Math.Abs(moyle.Pg) > 1, $"Moyle {moyle.Pg:R} MW");
        OverlayEntry hvdc = nav.Index.Overlay!.Find("ov.hvdc")!;
        Assert.Equal(new[] { new OverlayCheckRow("/records/12", "The end 'Moyle' is at " + F1(moyle.Pg) + " MW in the model, above the link's rated transfer 1 MW. Display only.") }, hvdc.Notes);

        nav.Select(nav.Tree.NodeFor(hvdc.Key)!);
        AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
        Assert.Contains(new OverviewFact("Receiving converter", "absent (the far end is outside the case)"), page.Facts);
        OverviewTable ends = page.Tables[1];
        Assert.Equal(new[] { "receiving", OverlayKindPages.Absent, "-", "-", "absent (the far end is outside the case)" }, ends.Rows[1].Cells);
        Assert.Empty(page.Tables[2].Rows);
        Assert.Equal("No check: the receiving end is absent (the far end is outside the case).", page.Tables[2].Note);
    }

    [Fact]
    public void The_solved_frames_are_bit_identical_with_and_without_the_overlay()
    {
        using var with = new UiOverlayFixture();
        using var without = new UiOverlayFixture(withOverlay: false);
        (LiveLoadResult a, var fa) = Run(with.ManifestPath);
        (LiveLoadResult b, var fb) = Run(without.ManifestPath);
        Assert.NotNull(a.Overlay);
        Assert.Null(b.Overlay);
        for (int i = 0; i < fa.Length; i++)
        {
            PowerFlowResult x = fa[i].Frame.Result, y = fb[i].Frame.Result;
            Assert.Equal(x.BusIds, y.BusIds);
            Assert.Equal(x.Vm.Select(Bits), y.Vm.Select(Bits));
            Assert.Equal(x.VaDeg.Select(Bits), y.VaDeg.Select(Bits));
            Assert.Equal(x.PInjMw.Select(Bits), y.PInjMw.Select(Bits));
            Assert.Equal(fa[i].Frame.Model.Generators.Select(g => Bits(g.Pg)), fb[i].Frame.Model.Generators.Select(g => Bits(g.Pg)));
        }
    }
}
