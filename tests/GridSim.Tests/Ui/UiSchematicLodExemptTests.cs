using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Tests.Editor;
using GridSim.Ui.Schematic;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// SchematicView's level-of-detail exemptions (UI programme 4.2 follow-up, 2026-10-24): Author mode keeps its selected
/// bus and Connect's first bus on the map at every zoom, so a low-kV bus placed on a large case does not vanish at the
/// fitted view. With no exemption every rule is the old one (UiSchematicParityTests pins that against the old map).
/// </summary>
public sealed class UiSchematicLodExemptTests
{
    // gb-synthetic: 2,227 buses, above ShowAllCeiling, so the EHV-first level of detail applies.
    private static SchematicView Fitted(out GridModel model)
    {
        model = JsonGridLoader.LoadDirectory(EditorTestSupport.CaseDir("gb-synthetic"));
        var v = new SchematicView();
        v.Load(model);
        v.ViewportWidth = 1280;
        v.ViewportHeight = 800;
        Assert.True(v.FitToView());
        Assert.False(v.ShowAllNodes);
        return v;
    }

    [Fact]
    public void An_exempt_bus_below_the_cut_is_shown_and_hit_and_without_the_exemption_it_is_hidden_again()
    {
        SchematicView v = Fitted(out _);
        double minKv = v.LodMinBaseKv();
        Assert.Equal(200, minKv);   // the fitted view of a large case: the EHV backbone only
        Assert.Empty(v.LodExemptBuses);

        // A bus below the cut whose point nothing visible covers: hidden, so a click there finds nothing.
        int index = Enumerable.Range(0, v.Nodes.Count).First(i => v.Nodes[i].DisplayKv < minKv
            && v.HitTestNode(v.NodeScreen(i)) is null);
        int id = v.Nodes[index].Id;
        ScreenPoint at = v.NodeScreen(index);
        Assert.True(v.IsHiddenAtLod(index, minKv));

        // Exempt: shown and hit, at the same zoom (the cut itself is unchanged).
        Assert.True(v.SetLodExempt(new[] { id }));
        Assert.Equal(new[] { id }, v.LodExemptBuses);
        Assert.False(v.IsHiddenAtLod(index, minKv));
        Assert.Equal(id, v.HitTestNode(at));
        Assert.Equal(minKv, v.LodMinBaseKv());
        Assert.False(v.SetLodExempt(new[] { id }));   // the same set: no change to redraw

        // Every other bus below the cut is still hidden; a bus above it never was.
        Assert.All(Enumerable.Range(0, v.Nodes.Count).Where(i => i != index),
            i => Assert.Equal(v.Nodes[i].DisplayKv < minKv, v.IsHiddenAtLod(i, minKv)));

        // Exemption removed: hidden again, exactly as before.
        Assert.True(v.SetLodExempt(Array.Empty<int>()));
        Assert.Empty(v.LodExemptBuses);
        Assert.True(v.IsHiddenAtLod(index, minKv));
        Assert.Null(v.HitTestNode(at));
    }

    [Fact]
    public void A_branch_between_two_shown_buses_is_hit_only_while_its_low_kV_end_is_exempt()
    {
        SchematicView v = Fitted(out _);
        double minKv = v.LodMinBaseKv();

        // A valid branch with exactly one end below the cut, whose midpoint no visible element covers while hidden.
        foreach (SchematicEdge e in v.Edges.Where(e => e.Valid))
        {
            bool aLow = v.Nodes[e.A].DisplayKv < minKv, bLow = v.Nodes[e.B].DisplayKv < minKv;
            if (aLow == bLow) continue;
            ScreenPoint pa = v.NodeScreen(e.A), pb = v.NodeScreen(e.B);
            double len = Math.Sqrt((pb.X - pa.X) * (pb.X - pa.X) + (pb.Y - pa.Y) * (pb.Y - pa.Y));
            if (len < 1) continue;   // a drawn segment (co-located busbars project onto one point)
            var mid = new ScreenPoint((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
            if (v.HitTestBranch(mid) is not null || v.HitTestNode(mid) is not null) continue;

            int low = aLow ? e.A : e.B;
            Assert.True(v.SetLodExempt(new[] { v.Nodes[low].Id }));
            int? hit = v.HitTestBranch(mid);
            Assert.NotNull(hit);
            // The hit is a branch drawn at that point now; with the low end shown, this one is among them.
            Assert.Contains(v.Edges, x => x.BranchIndex == hit && x.Valid && (x.A == low || x.B == low));
            Assert.True(v.SetLodExempt(Array.Empty<int>()));
            Assert.Null(v.HitTestBranch(mid));
            return;
        }
        Assert.Fail("no branch of gb-synthetic with one end below the fitted cut and a clear midpoint");
    }
}
