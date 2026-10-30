using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N310 (LF4; follows N278, which removed the middle dots): every web and WPF source file (display text, tooltips, CSS
/// content, comments) is plain ASCII. The symbols that were left became ASCII words or marks: degree sign -> " deg" /
/// "degC", pound sign -> "GBP", arrows -> "->" / "&lt;-" / "&lt;->", ellipsis -> "...", multiplication sign -> "x",
/// plus-minus -> "+/-", super / subscripts -> "^2" / "2", Greek letters -> their names, trend triangles -> "^" / "v" / "=",
/// status dots -> "*", tick / cross -> "OK" / "X" (the conformance verdict "PASS" / "FAIL"), warning sign -> "!", em and
/// en dashes -> "-", and the pictographs (calendar, clock, pin, sun, lightning) dropped.
/// </summary>
public sealed partial class LF4LeftoversTests
{
    [Fact]
    public void N310_every_web_and_wpf_source_file_is_plain_ascii()
    {
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        var offenders = new List<string>();
        // Test retargeted 2026-10-20 (UI programme 0.2): code moved from src/GridSim.Wpf to src/GridSim.Ui (the host, model
        // catalogue, settings, geography and colour ramps); strengthened: GridSim.Ui is scanned too, so moved code stays covered.
        foreach (string project in new[] { "GridSim.Web", "GridSim.Wpf", "GridSim.Ui" })
        {
            string dir = Path.Combine(root, "src", project);
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".razor") || f.EndsWith(".cs") || f.EndsWith(".js") || f.EndsWith(".xaml")
                                     || f.EndsWith(".css") || f.EndsWith(".html") || f.EndsWith(".json") || f.EndsWith(".resx"))
                         .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                // ReadAllLines drops a UTF-8 byte-order mark, which is file encoding, not text.
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    int col = lines[i].ToList().FindIndex(c => c > '\u007f');   // first non-ASCII character, -1 for none
                    if (col >= 0)
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}:{col + 1} U+{(int)lines[i][col]:X4}");
                }
            }
        }
        Assert.True(offenders.Count == 0, "non-ASCII characters left: " + string.Join(", ", offenders));
    }

    [Fact]
    public void N310_the_wpf_angle_text_still_parses_after_the_degree_sign_became_deg()
    {
        // HumanScaleVm orders machines and corridors by parsing its own DegText ("+12.3 deg" since N310). The parser must
        // strip the new suffix; a source check keeps the two in step (the view model needs a WPF dispatcher to build).
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/HumanScaleVm.cs moved to
        // src/GridSim.Ui/Tabs/HumanScale/HumanScaleModel.cs; assertions unchanged, and strengthened: the WPF view-model
        // must derive from that model and may not format or parse an angle itself; and, since the model now builds without
        // WPF, the parse is checked by behaviour too (UiTabsThirdBatchParityTests orders the machine rows of real frames
        // exactly as the old view-model did).
        string src = File.ReadAllText(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "src", "GridSim.Ui", "Tabs", "HumanScale", "HumanScaleModel.cs"));
        Assert.Contains("DegText = $\"{mean:+0.0;-0.0;0.0} deg\"", src, StringComparison.Ordinal);
        Assert.Contains("s.EndsWith(\" deg\", StringComparison.Ordinal) ? s[..^4] : s", src, StringComparison.Ordinal);
        string adapter = File.ReadAllText(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "src", "GridSim.Wpf", "Views", "HumanScaleVm.cs"));
        Assert.Contains("public sealed class HumanScaleVm : HumanScaleModel", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("DegText", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("ParseDeg", adapter, StringComparison.Ordinal);
    }
}
