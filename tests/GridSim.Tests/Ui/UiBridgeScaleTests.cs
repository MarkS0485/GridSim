using System.Globalization;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// Two GDA-bridge readings seen end to end on the real lake (2026-10-24):
/// <list type="bullet">
/// <item>The HUD's fragility read 33.49 against a 0..1 colour ramp: the wire's <c>conditionIndex</c> is GDA's fragility
/// index on a 0..100 scale (GDA v1/Scripts/build_system_condition_index.py), and was used as if it were already GridSim's
/// 0..1 condition index. The protocol now carries both: the wire value, and the 0..1 condition index (value / 100) every
/// consumer uses; an impossible reading (outside 0..100) is no reading.</item>
/// <item>The main INERTIA tile read 0 while the HUD read 166.9 GVA.s: a bridge topology carries no machine inertia
/// constants, so the model's own total is 0. While the HUD shows, such a model's tiles take the stream's figure (or " - "
/// when the stream has none); a model that carries inertia, and every non-bridge run, is shown exactly as before.</item>
/// </list>
/// </summary>
public sealed class UiBridgeScaleTests
{
    private static string StateJson(string conditionIndexLiteral) => """
        { "type": "state", "t": "2026-07-01T12:00:00Z",
          "scalars": { "inertiaMwS": 166900.0, "conditionIndex": COND, "rocofHeadroomHzS": 0.09 } }
        """.Replace("COND", conditionIndexLiteral, StringComparison.Ordinal);

    [Fact]
    public void The_wire_conditionIndex_is_read_as_the_0_to_100_fragility_index_and_carried_as_the_0_to_1_condition_index()
    {
        BridgeProtocol.ScalarsBlock s = BridgeProtocol.ReadState(StateJson("33.49")).Scalars!;
        Assert.Equal(33.49, s.FragilityIndex);
        Assert.Equal(33.49 / 100.0, s.ConditionIndex);   // the exact quotient, no rounding

        // The ends of the scale, and impossible readings (no reading rather than a clamped colour).
        foreach ((string wire, double? expected) in new (string, double?)[]
                 { ("0", 0.0), ("100", 1.0), ("50", 0.5), ("100.0001", null), ("-0.5", null), ("1e9", null), ("null", null) })
        {
            BridgeProtocol.ScalarsBlock b = BridgeProtocol.ReadState(StateJson(wire)).Scalars!;
            Assert.Equal(expected, b.ConditionIndex);
        }
        Assert.Null(BridgeProtocol.ScalarsBlock.ConditionFromFragility(double.NaN));
        Assert.Null(BridgeProtocol.ScalarsBlock.ConditionFromFragility(double.PositiveInfinity));
    }

    [Fact]
    public void A_real_fragility_reading_no_longer_saturates_the_HUD_colour()
    {
        BridgeProtocol.ScalarsBlock s = BridgeProtocol.ReadState(StateJson("33.49")).Scalars!;
        var m = new DashboardModel();
        m.UpdateHud(s.InertiaMwS, s.ConditionIndex, s.RocofHeadroomHzS);
        Assert.Equal(ColourRamps.Condition(33.49 / 100.0), m.FragilityColour);
        Assert.NotEqual(ColourRamps.Condition(1.0), m.FragilityColour);
        Assert.Equal((33.49 / 100.0).ToString("F2", CultureInfo.CurrentCulture), m.FragilityIndex);
        Assert.Equal("166.9", m.InertiaGvaS);
    }

    // A solved uk frame, and the same frame with every machine's inertia constant zeroed (what a bridge topology carries).
    private static (SimFrame WithInertia, SimFrame WithoutInertia) Frames()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "data", "uk");
        var host = new SimHost();
        LiveLoadResult loaded = host.LoadLive(new ModelSource("GB reduced  10-zone", dir), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.True(loaded.Loaded, loaded.Refusal);
        HostFrame hf = Assert.IsType<HostFrame>(host.ProduceNext());
        SimFrame f = hf.Frame!;
        Assert.True(f.Model.SystemInertiaGvaS > 0, "the uk case carries machine inertia");
        GridModel zero = f.Model.CopyWith(generators: f.Model.Generators.Select(g => g with { H = 0 }).ToList());
        Assert.Equal(0.0, zero.SystemInertiaGvaS);
        return (f, f with { Model = zero });
    }

    [Fact]
    public void In_bridge_mode_a_model_without_machine_inertia_shows_the_streams_inertia_or_unknown_never_a_false_zero()
    {
        (SimFrame withH, SimFrame noH) = Frames();
        var m = new DashboardModel();
        var boundaries = Array.Empty<GridSim.Core.Analysis.BoundaryFlow>();

        // Not bridge mode: a zero-inertia model reads 0, exactly as before (a real all-converter case is 0).
        m.Update(noH, boundaries);
        Assert.Equal("0", m.Inertia);

        // Bridge mode with the stream's figure: the tile and the secure-minimum note use it.
        m.UpdateHud(166_900, 0.3349, 0.09);
        m.Update(noH, boundaries);
        Assert.Equal((166_900 / 1000.0).ToString("F0"), m.Inertia);
        Assert.StartsWith((166_900 / 1000.0).ToString("F0") + " GVA.s", m.InertiaNote, StringComparison.Ordinal);

        // Bridge mode without a figure: unknown, and no margin claimed.
        m.UpdateHud(null, 0.3349, 0.09);
        m.Update(noH, boundaries);
        Assert.Equal(" - ", m.Inertia);
        Assert.Equal(" - ", m.InertiaNote);
        Assert.Equal(DashboardColours.Gray, m.InertiaNoteColour);
        Assert.Equal(0.0, m.SecureMinInertiaGva);
        Assert.Equal(0.0, m.InertiaHeadroomGva);

        // A model that carries inertia keeps its own figure even while the HUD shows.
        m.UpdateHud(1.0, 0.3349, 0.09);
        m.Update(withH, boundaries);
        Assert.Equal(withH.Model.SystemInertiaGvaS.ToString("F0"), m.Inertia);

        // HUD hidden (back to a local run): the zero-inertia model reads 0 again.
        m.HideHud();
        m.Update(noH, boundaries);
        Assert.Equal("0", m.Inertia);
    }
}
