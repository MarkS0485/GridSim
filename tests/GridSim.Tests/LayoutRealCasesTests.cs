using GridSim.Core.IO;
using GridSim.Core.Layout;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Exercises the dashboard's real load path on every standard case: parse -> auto-layout -> steady
/// live-sim. The WPF app has no coordinates for these, so <see cref="GraphLayout"/> must always
/// produce a finite, in-box, non-degenerate layout, and the case must solve. Guards against a case
/// whose topology trips the layout or the auto-selected solver.
/// </summary>
public class LayoutRealCasesTests
{
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "case9",   IeeeCases.Case9()   },
        new object[] { "case14",  IeeeCases.Case14()  },
        new object[] { "case30",  IeeeCases.Case30()  },
        new object[] { "case57",  IeeeCases.Case57()  },
        new object[] { "case118", IeeeCases.Case118() },
        new object[] { "case300", IeeeCases.Case300() },
        new object[] { "case145", IeeeCases.Case145() },
        new object[] { "rts24",   IeeeCases.Case24Rts() },
        new object[] { "cigre_mv", IeeeCases.CigreMv() },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Auto_layout_is_finite_in_box_and_non_degenerate(string name, GridModel model)
    {
        GridModel laid = GraphLayout.EnsureLayout(model);
        Assert.Equal(model.BusCount, laid.BusCount);

        double minX = 1, maxX = 0, minY = 1, maxY = 0;
        foreach (Bus b in laid.Buses)
        {
            Assert.True(double.IsFinite(b.X) && double.IsFinite(b.Y), $"{name}: non-finite coord");
            Assert.InRange(b.X, 0.0, 1.0);
            Assert.InRange(b.Y, 0.0, 1.0);
            minX = Math.Min(minX, b.X); maxX = Math.Max(maxX, b.X);
            minY = Math.Min(minY, b.Y); maxY = Math.Max(maxY, b.Y);
        }
        // The layout must actually spread out, not collapse to a point/line.
        Assert.True(maxX - minX > 0.1, $"{name}: X span collapsed ({maxX - minX:F3})");
        Assert.True(maxY - minY > 0.1, $"{name}: Y span collapsed ({maxY - minY:F3})");

        // Distinct positions (the renderer needs separable roundels).
        int distinct = laid.Buses.Select(b => (Math.Round(b.X, 6), Math.Round(b.Y, 6))).Distinct().Count();
        Assert.Equal(laid.BusCount, distinct);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Steady_live_sim_solves_the_laid_out_case(string name, GridModel model)
    {
        // Mirror MainWindow.SwitchTo for a MATPOWER case: lay out, then hold-steady live-sim.
        GridModel laid = GraphLayout.EnsureLayout(model);
        var sim = new LiveSim(laid, holdSteady: true);
        SimFrame f = sim.Step(1);
        Assert.True(f.Result.Converged, $"{name}: steady live-sim did not converge ({sim.ActiveSolver})");
        Assert.True(f.DemandMw >= 0);
    }
}
