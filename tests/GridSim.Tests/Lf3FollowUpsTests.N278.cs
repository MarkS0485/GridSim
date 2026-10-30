using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N278 (LF3; LF1 leftover): no non-ASCII middle dot (U+00B7) left in the web or WPF sources (display text, tooltips,
/// comments). Separators became " | ", unit products ASCII dots (GVA.s, MN.m, MW.s, Hz.s, ft.lb, K.m/W) and the formula
/// products " x " (H x MBase, Y(h) x V, S x C). Other non-ASCII symbols in presentation text (arrows, degree signs,
/// subscripts) are outside N278 and recorded as a new finding.
/// </summary>
public sealed partial class Lf3FollowUpsTests
{
    [Fact]
    public void N278_no_middle_dot_is_left_in_web_or_wpf_sources()
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
                                     || f.EndsWith(".css") || f.EndsWith(".html"))
                         .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains('·'))
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
            }
        }
        Assert.True(offenders.Count == 0, "middle dots left: " + string.Join(", ", offenders));
    }
}
