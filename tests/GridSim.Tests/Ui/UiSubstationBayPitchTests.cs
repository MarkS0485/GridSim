using GridSim.Ui.Diagrams;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// The substation single-line diagram's bay spacing (ASSETS A3 follow-up, 2026-10-24): a bay-end label is drawn to the
/// right of its end symbol, so the bays are spaced for the longest such label and no label runs into the next bay.
/// </summary>
public sealed class UiSubstationBayPitchTests
{
    private static SubstationMapInput Input(string farEndA, string farEndB) => new()
    {
        VoltagePu = 1.04, AngleDeg = 3.0, BaseKv = 400, BusType = "PV",
        Connections =
        [
            new("L1", farEndA, "LINE", 1521, true, 0.4),
            new("T1", farEndB, "TRANSFORMER", 589, true, 0.61),
        ],
    };

    private static double TapX(DiagramModel m, int tap) => m.Symbol(SubstationDiagramBuilder.BusbarId).Taps![tap];

    [Fact]
    public void Short_labels_keep_the_default_pitch_exactly()
    {
        SubstationMapInput d = Input("A", "B");
        Assert.Equal(DiagramLayout.DefaultBayPitch, SubstationDiagramBuilder.PitchFor(d));
        DiagramModel m = SubstationDiagramBuilder.Build(d);
        Assert.Equal(DiagramLayout.DefaultBayPitch, TapX(m, 1) - TapX(m, 0));
    }

    [Fact]
    public void A_long_far_end_name_widens_the_bays_so_its_label_clears_the_next_bays_symbol()
    {
        // The case seen on the uk case: "South Scotland 400kV" beside a transformer bay.
        SubstationMapInput d = Input("South Scotland 400kV", "North Scotland 132kV");
        double pitch = SubstationDiagramBuilder.PitchFor(d);
        Assert.Equal(20 * SubstationDiagramBuilder.LabelCharWidth + SubstationDiagramBuilder.LabelClearance, pitch);
        DiagramModel m = SubstationDiagramBuilder.Build(d);
        Assert.Equal(pitch, TapX(m, 1) - TapX(m, 0));

        // The line end's label (from just right of its symbol, about LabelCharWidth a character) ends left of the
        // transformer symbol of the next bay, which sits at the same height.
        ScreenPoint end = m.Symbol("C0/END").Position, tx = m.Symbol("C1/TX").Position;
        double labelRight = end.X + 14 + "South Scotland 400kV".Length * SubstationDiagramBuilder.LabelCharWidth;
        Assert.True(labelRight < tx.X - 12, $"the label ends at {labelRight}, the transformer symbol starts at {tx.X - 12}");
    }

    [Fact]
    public void The_pitch_counts_the_generator_and_demand_labels_too_and_is_deterministic()
    {
        var d = new SubstationMapInput
        {
            VoltagePu = 1.0, BaseKv = 132, BusType = "PQ", DemandMw = 1234.4, ReactiveMvar = 56.7,
            Generators = [new("A very long generator name", "CCGT", default, 100.0)],
        };
        Assert.Equal(("A very long generator name 100 MW".Length * SubstationDiagramBuilder.LabelCharWidth) + SubstationDiagramBuilder.LabelClearance,
            SubstationDiagramBuilder.PitchFor(d));
        Assert.Equal(SubstationDiagramBuilder.PitchFor(d), SubstationDiagramBuilder.PitchFor(d));
    }
}
