using GridSim.Core.IO.Json.CaseEditing;
using GridSim.SchemaEditor.Ui;
using Xunit;
using static GridSim.Tests.Editor.CaseSessionTests;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// The screens the manual chapter on the case editor shows (slice E8), rendered by the real renderer at 80 x 25 from
/// a walk through data/uk. The test checks each screen's breadcrumb; when the environment variable
/// GRIDSIM_EDITOR_CAPTURES names a folder, it also writes each screen there as text (the chapter's captures are
/// produced that way, into docs/_captures/case-editor). Nothing is written otherwise.
/// </summary>
public class EditorScreenCaptureTests
{
    [Fact]
    public void The_manual_walk_through_screens_render()
    {
        string? folder = Environment.GetEnvironmentVariable("GRIDSIM_EDITOR_CAPTURES");
        var d = new EditorDriver(CaseSession.OpenV1(CaseDir("uk"), Unlimited()), "data/uk");

        void Capture(string name, string crumb)
        {
            ScreenBuffer s = d.Screen();
            Assert.Equal(crumb, s.RowText(1).TrimEnd());
            if (folder is null) return;
            Directory.CreateDirectory(folder);
            // The title bar carries the build version; it is kept as rendered (the chapter says so).
            File.WriteAllLines(Path.Combine(folder, name + ".txt"), Enumerable.Range(0, s.Height).Select(y => s.RowText(y).TrimEnd()));
        }

        Capture("root", "Case");
        d.Select("Generation").Keys(ConsoleKey.Enter);
        Capture("fuel-types", "Case > Generation");
        d.Select("ccgt").Keys(ConsoleKey.Enter);
        Capture("stations", "Case > Generation > ccgt");
        d.Select("Pembroke CCGT").Keys(ConsoleKey.Enter, ConsoleKey.Enter);
        Capture("form", "Case > Generation > ccgt > Pembroke CCGT (1 units) > #26 'Pembroke CCGT'");
        d.Select("Capacity (MVA)").Keys(ConsoleKey.Enter, "2100");
        Capture("editing", "Case > Generation > ccgt > Pembroke CCGT (1 units) > #26 'Pembroke CCGT'");
        d.Keys(ConsoleKey.Enter);
        Capture("edited", "Case > Generation > ccgt > Pembroke CCGT (1 units) > #26 'Pembroke CCGT'");
        d.Keys(ConsoleKey.F12);
        Capture("quit-prompt", "Case > Generation > ccgt > Pembroke CCGT (1 units) > #26 'Pembroke CCGT'");
        d.Keys(ConsoleKey.Escape, ConsoleKey.Escape, ConsoleKey.Escape, ConsoleKey.Escape, ConsoleKey.Escape);
        d.Select("Buses").Keys(ConsoleKey.Enter).Select("400 kV").Keys(ConsoleKey.Enter, "x", "y");
        Capture("delete-refused", "Case > Buses > 400 kV");
    }
}
