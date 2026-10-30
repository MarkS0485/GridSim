using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;
using Xunit;
using Old = GridSim.Tests.Ui.OldDashboard;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 0.2 (second slice): the dashboard's logic moved from the WPF <c>DashboardViewModel</c> into the
/// platform-neutral <see cref="DashboardModel"/>. That is an orchestration-only move, so every value the dashboard
/// shows must be exactly what the WPF view-model showed before. These tests feed the same frames (from
/// <see cref="SimHost"/>, case9 and the GB reduced case) and the same calls to the new model and to a verbatim copy of
/// the old WPF code (<c>UiDashboardOracle.cs</c>), then compare EVERY public property of the new model, and every row of
/// every list, with exact equality: strings ordinal, doubles by their bits, colour tokens against the colour of the
/// brush the old code chose, bools against the old Visibility. A property added to either side later fails the
/// coverage check until it is compared here.
/// </summary>
public sealed class UiDashboardParityTests
{
    private const int Ticks = 12;

    public static TheoryData<string> Cases => new() { "case9", "uk" };

    private static ModelSource SourceOf(string name)
    {
        if (name == "case9")
        {
            string file = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "lib", "case9.m");
            Assert.True(File.Exists(file), $"bundled case not found: {file}");
            return new ModelSource("MATPOWER  case9", Path.GetDirectoryName(file)!, file);
        }
        string dir = Path.Combine(AppContext.BaseDirectory, "data", "uk");
        Assert.True(File.Exists(Path.Combine(dir, "system.json")), $"bundled case not found: {dir}");
        return new ModelSource("GB reduced  10-zone", dir);
    }

    /// <summary>The live host loaded with a case, and its next solved frame on demand.</summary>
    private static SimHost Host(string name)
    {
        var host = new SimHost();
        LiveLoadResult loaded = host.LoadLive(SourceOf(name), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.True(loaded.Loaded, loaded.Refusal);
        return host;
    }

    private static HostFrame Next(SimHost host)
    {
        HostFrame hf = Assert.IsType<HostFrame>(host.ProduceNext());
        Assert.Equal(HostFrameKind.LiveSolved, hf.Kind);
        return hf;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Live frames, and every other entry point the window calls, interleaved
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_displayed_value_equals_the_old_wpf_view_model_frame_for_frame(string name)
    {
        SimHost host = Host(name);
        var neu = new DashboardModel();
        var old = new Old.DashboardViewModel();
        int tripsNew = 0, tripsOld = 0;
        neu.TripCompared += () => tripsNew++;
        old.TripCompared += () => tripsOld++;
        AssertSame(old, neu, $"{name} before the first frame");

        // Before any frame: the trip compare has no model and does nothing on either side.
        neu.TripCompare(); old.TripCompare();
        AssertSame(old, neu, $"{name} trip compare before a frame");

        for (int tick = 1; tick <= Ticks; tick++)
        {
            HostFrame hf = Next(host);
            SimFrame f = hf.Frame!;
            neu.Update(f, hf.Boundaries, hf.SolveMs);
            old.Update(f, hf.Boundaries, hf.SolveMs);
            AssertSame(old, neu, $"{name} tick {tick}");

            switch (tick)
            {
                case 2:
                    // The bridge HUD: first reading, a rise, an ease, a steady, a gap, and no readings at all.
                    foreach ((double? i, double? c, double? r) in new (double?, double?, double?)[]
                             { (250_000, 0.31, 0.42), (251_000, 0.40, 0.40), (249_500, 0.20, null), (null, 0.2049, 0.1),
                               (1.0, 0.95, -0.25), (null, null, null), (300_000, 0.5, 0.0) })
                    {
                        neu.UpdateHud(i, c, r); old.UpdateHud(i, c, r);
                        AssertSame(old, neu, $"{name} tick {tick} HUD ({i}, {c}, {r})");
                    }
                    neu.HideHud(); old.HideHud();
                    AssertSame(old, neu, $"{name} tick {tick} HUD hidden");
                    break;
                case 3:
                    // Every colour legend, and back to loading.
                    foreach (MapColourMode mode in new[] { MapColourMode.Voltage, MapColourMode.Condition, MapColourMode.Stress, MapColourMode.Loading })
                    {
                        neu.SetColorLegend(mode); old.SetColorLegend((Old.FastSchematic.ColorModeKind)(int)mode);
                        AssertSame(old, neu, $"{name} tick {tick} legend {mode}");
                    }
                    break;
                case 4:
                    // The bridge query panel: verdicts of every class, a violation of every detail shape, no violations.
                    var violations = new List<Violation>
                    {
                        new("thermal", "Line A", 1234.5678, 1000.0, "over rating"),
                        new("voltage", null, 1.0712, null, null),
                        new(null, "  ", null, 0.94, "under"),
                        new(null, null, null, null, null),
                    };
                    var at = new DateTime(2026, 7, 1, 12, 30, 0, DateTimeKind.Utc);
                    foreach ((string? verdict, bool overlay, bool notConverged) in new (string?, bool, bool)[]
                             { ("FEASIBLE", true, false), ("infeasible", false, false), ("MARGINAL", false, true), (null, false, false), (" ", true, false), ("secure", false, false) })
                    {
                        var qr = new QueryResult("scenario", at, null, verdict, verdict is null ? null : "B6 boundary", violations);
                        neu.ShowQueryResult(qr, "WHAT-IF  scenario", overlay, notConverged);
                        old.ShowQueryResult(qr, "WHAT-IF  scenario", overlay, notConverged);
                        AssertSame(old, neu, $"{name} tick {tick} query {verdict}");
                    }
                    var empty = new QueryResult("inertia", DateTime.MinValue, null, "OK", null, Array.Empty<Violation>());
                    neu.ShowQueryResult(empty, "INERTIA", false); old.ShowQueryResult(empty, "INERTIA", false);
                    AssertSame(old, neu, $"{name} tick {tick} query without violations");
                    neu.ClearQueryResult(); old.ClearQueryResult();
                    AssertSame(old, neu, $"{name} tick {tick} query cleared");
                    break;
                case 5:
                    // The TSGB compare at the default device, then at a bigger and at a zero device, and a map trip.
                    neu.TripCompare(); old.TripCompare();
                    AssertSame(old, neu, $"{name} tick {tick} trip compare");
                    neu.TsgbInertiaGva = old.TsgbInertiaGva = 75;
                    neu.TsgbFfrGw = old.TsgbFfrGw = 3.5;
                    neu.TripCompare(); old.TripCompare();
                    AssertSame(old, neu, $"{name} tick {tick} trip compare, bigger device");
                    neu.TsgbInertiaGva = old.TsgbInertiaGva = 0;
                    neu.TsgbFfrGw = old.TsgbFfrGw = 0;
                    neu.TripCompare(); old.TripCompare();
                    AssertSame(old, neu, $"{name} tick {tick} trip compare, no device");
                    neu.AnalyseTrip(150_000, 25_000, -1_320, "map trip @ bus 7"); old.AnalyseTrip(150_000, 25_000, -1_320, "map trip @ bus 7");
                    AssertSame(old, neu, $"{name} tick {tick} map trip");
                    neu.TsgbInertiaGva = old.TsgbInertiaGva = 30;
                    neu.TsgbFfrGw = old.TsgbFfrGw = 2;
                    break;
                case 6:
                    // The detail fly-out, the bridge pill, the map hint and the event log.
                    KeyVal[] rows = { new("kV", "400"), new("V", "1.012 pu"), new("", "") };
                    neu.Select("SUBSTATION", "Bus 4", "400 kV", rows, "3 connections", "Line 4-5, Line 4-9"); old.Select("SUBSTATION", "Bus 4", "400 kV", rows.Select(r => new Old.KeyVal(r.Key, r.Value)), "3 connections", "Line 4-5, Line 4-9");
                    AssertSame(old, neu, $"{name} tick {tick} selection");
                    neu.ClearSelection(); old.ClearSelection();
                    UiColour warn = ColourRamps.Loading(0.7);
                    neu.SetBridgeStatus("CONNECTING  *  uk", warn); old.SetBridgeStatus("CONNECTING  *  uk", Old.Palette.Frozen(warn));
                    AssertSame(old, neu, $"{name} tick {tick} bridge pill");
                    neu.ClearBridgeStatus(); old.ClearBridgeStatus();
                    neu.MapHint = old.MapHint = DashboardModel.MapHintReplay;
                    for (int i = 0; i < 20; i++) { neu.Log(neu.Clock, $"line {i}"); old.Log(old.Clock, $"line {i}"); }
                    AssertSame(old, neu, $"{name} tick {tick} log overflow");
                    break;
                case 7:
                    // A frame that did not converge, a voltage excursion and an overloaded corridor, then recovery.
                    SimFrame bad = f with { Result = Copy(f.Result, converged: false, vmScale: 1.0) };
                    neu.Update(bad, hf.Boundaries); old.Update(bad, hf.Boundaries);
                    AssertSame(old, neu, $"{name} tick {tick} not converged");
                    SimFrame high = f with { Result = Copy(f.Result, converged: true, vmScale: 1.12) };
                    var overloaded = new List<BoundaryFlow>(hf.Boundaries) { new("TEST CUT", -5_432.1, 4_000, 135.8025, true), new("IDLE CUT", 0, 0, 0, false) };
                    neu.Update(high, overloaded); old.Update(high, overloaded);
                    AssertSame(old, neu, $"{name} tick {tick} voltage and corridor alerts");
                    neu.Update(high, overloaded); old.Update(high, overloaded);   // no second alarm for the same onset
                    AssertSame(old, neu, $"{name} tick {tick} alerts held");
                    SimFrame low = f with { Result = Copy(f.Result, converged: true, vmScale: 0.9), FrequencyHz = f.FrequencyHz - 0.35, RocofHzPerS = 1.5 };
                    neu.Update(low, Array.Empty<BoundaryFlow>()); old.Update(low, Array.Empty<BoundaryFlow>());
                    AssertSame(old, neu, $"{name} tick {tick} frequency event, corridors pruned");
                    neu.Update(f, hf.Boundaries); old.Update(f, hf.Boundaries);
                    AssertSame(old, neu, $"{name} tick {tick} recovered");
                    break;
                case 8:
                    // The frequency dial driven directly: the band edges, a non-finite value and a wide swing.
                    double nominal = f.Model.FrequencyHz;
                    foreach (double hz in new[] { nominal + 0.2, nominal - 0.2, nominal + 0.1, nominal - 0.0999, nominal + 0.75, double.NaN, double.PositiveInfinity, nominal })
                    {
                        neu.SetFrequency(hz); old.SetFrequency(hz);
                        AssertSame(old, neu, $"{name} tick {tick} SetFrequency({hz:R})");
                    }
                    break;
                case 9:
                    // A lost stream, then the alarm list cleared.
                    neu.MarkStreamLost("BRIDGE FAULT"); old.MarkStreamLost("BRIDGE FAULT");
                    AssertSame(old, neu, $"{name} tick {tick} stream lost");
                    for (int i = 0; i < 15; i++) { neu.AddAlarm($"alarm {i}", ColourRamps.Loading(1.2)); old.AddAlarm($"alarm {i}", Old.Palette.Frozen(ColourRamps.Loading(1.2))); }
                    AssertSame(old, neu, $"{name} tick {tick} alarm overflow");
                    neu.ClearAlarms(); old.ClearAlarms();
                    AssertSame(old, neu, $"{name} tick {tick} alarms cleared");
                    break;
                case 10:
                    // A timeline break and a model change: the next frames decide everything again.
                    neu.ResetTimeline(); old.ResetTimeline();
                    AssertSame(old, neu, $"{name} tick {tick} timeline reset");
                    neu.ModelChanged(); old.ModelChanged();
                    AssertSame(old, neu, $"{name} tick {tick} model changed");
                    break;
            }
        }
        Assert.Equal(tripsOld, tripsNew);
        Assert.True(tripsNew >= 4, "the trip compare must have run");
    }

    [Fact]
    public void A_switch_between_two_cases_rebuilds_every_list_exactly_as_before()
    {
        // case9 then uk then case9 into ONE dashboard: the boundary rows, voltage dials, fuel rows and import links of
        // the old model are pruned and the new model's rows built, in the old order.
        var neu = new DashboardModel();
        var old = new Old.DashboardViewModel();
        foreach (string name in new[] { "case9", "uk", "case9" })
        {
            SimHost host = Host(name);
            neu.ModelChanged(); old.ModelChanged();
            for (int tick = 1; tick <= 4; tick++)
            {
                HostFrame hf = Next(host);
                neu.Update(hf.Frame!, hf.Boundaries); old.Update(hf.Frame!, hf.Boundaries);
                AssertSame(old, neu, $"{name} tick {tick} after a switch");
            }
        }
    }

    [Fact]
    public void The_kv_label_and_torque_helpers_are_the_old_ones()
    {
        foreach (double kv in new[] { 400, 275, 132, 33, 11.5, 12, 0.4, 0.415, 0.0004, 6.6, -1, 1e6, double.NaN })
            Assert.Equal(Old.DashboardViewModel.KvLabel(kv), DashboardModel.KvLabel(kv));
        foreach ((double mw, double hz) in new[] { (30_000.0, 50.0), (30_000.0, 49.8), (0.0, 50.0), (1.0, 0.0), (1.0, -50.0), (1.0, double.NaN), (double.NaN, 50.0), (double.PositiveInfinity, 50.0) })
            Assert.Equal(Old.DashboardViewModel.FormatTorqueMnm(mw, hz), DashboardModel.FormatTorqueMnm(mw, hz));
    }

    [Fact]
    public void The_constants_are_the_old_values()
    {
        Assert.Equal(Old.DashboardViewModel.BarTrack, DashboardModel.BarTrack);
        Assert.Equal(Old.DashboardViewModel.FuelBarTrack, DashboardModel.FuelBarTrack);
        Assert.Equal(Old.DashboardViewModel.FrequencyEventBandHz, DashboardModel.FrequencyEventBandHz);
        Assert.Equal(Old.DashboardViewModel.FreqDialFullScaleHz, DashboardModel.FreqDialFullScaleHz);
        Assert.Equal(Old.DashboardViewModel.FreqDialSweepDeg, DashboardModel.FreqDialSweepDeg);
        Assert.Equal(Old.DashboardViewModel.FreqNominalColourBandHz, DashboardModel.FreqNominalColourBandHz);
        Assert.Equal(Old.DashboardViewModel.LowInertiaSyncFraction, DashboardModel.LowInertiaSyncFraction);
        Assert.Equal(Old.DashboardViewModel.TwoPoleSyncRpm, DashboardModel.TwoPoleSyncRpm);
        Assert.Equal(Old.DashboardViewModel.EventLogCapacity, DashboardModel.EventLogCapacity);
        Assert.Equal(Old.DashboardViewModel.AlarmListCapacity, DashboardModel.AlarmListCapacity);
        Assert.Equal(Old.DashboardViewModel.MapHintLocal, DashboardModel.MapHintLocal);
        Assert.Equal(Old.DashboardViewModel.MapHintReplay, DashboardModel.MapHintReplay);
        Assert.Equal(Old.DashboardViewModel.MapHintBridge, DashboardModel.MapHintBridge);
    }

    [Fact]
    public void The_neutral_fuel_classification_equals_the_old_wpf_one()
    {
        // The dashboard now classifies through UiFuels; the old WPF Fuels (verbatim in the oracle) must agree on every
        // field for every generator type in the bundled cases and a list of awkward spellings.
        var types = new HashSet<string?>(StringComparer.Ordinal) { null, "", " ", "ccgt", "CCGT", "ocgt", "gas", "nuclear", "wind", "Wind Offshore",
            "solar", "hydro", "hydrogen", "fuel cell", "electrolyser", "recip", "diesel", "engine", "battery", "pumped storage",
            "interconnector", "Interconnector", "coal", "biomass", "unknown thing", "grid infeed" };
        foreach (string name in new[] { "case9", "uk" })
            foreach (Generator g in Host(name).ProduceNext()!.Frame!.Model.Generators) types.Add(g.Type);
        foreach (string? t in types)
        {
            Old.FuelInfo o = Old.Fuels.Classify(t);
            UiFuelInfo n = UiFuels.Classify(t);
            Assert.Equal(o.Name, n.Name);
            Assert.Equal(o.Color, n.Colour);
            Assert.Equal(o.Synchronous, n.Synchronous);
            Assert.Equal(o.IsFossil, n.IsFossil);
            Assert.Equal(o.IsRenewable, n.IsRenewable);
            Assert.Equal(o.Class == Old.FuelClass.Interconnector, n.Category == GridSim.Core.Presentation.FuelCategory.Interconnector);
            Assert.Equal(o.Class.ToString(), n.Category.ToString());
        }
    }

    [Fact]
    public void The_moved_interconnector_routes_equal_the_old_table()
    {
        foreach (string? n in new[] { null, "", "  ", "Moyle", "Auchencrosh (interconnector CCT) interconnector", "NS Link interconnector",
                     "North Sea Link", "IFA", "IFA2", "IFA Interconnector interconnector", "ElecLink", "Nemo Link", "BritNed", "EWIC",
                     "East West", "Viking Link", "Greenlink", "Isle of Man", "Not a link", "ifa 2", "NSL" })
            Assert.Equal(Old.Interconnectors.RouteOf(n), InterconnectorRoutes.RouteOf(n));
    }

    [Fact]
    public void The_wpf_adapter_maps_every_token_and_bool_to_its_old_property_name()
    {
        // Source contract (the WPF project is not loaded by the test host): every colour token, every visibility bool and
        // the legend mode of the neutral model is announced to the XAML under its old WPF name, and every old WPF name
        // is a brush or Visibility adapter property built from that token.
        string wpf = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "DashboardViewModel.cs");
        foreach ((string neutral, string adapter) in AdapterNames)
        {
            Assert.Contains($"[nameof({neutral})] = nameof({adapter}),", wpf, StringComparison.Ordinal);
            if (adapter.EndsWith("Vis", StringComparison.Ordinal))
                Assert.Contains($"public Visibility {adapter} => {neutral} ? Visibility.Visible : Visibility.Collapsed;", wpf, StringComparison.Ordinal);
            else if (adapter != "LegendBrush" && adapter != "StatusBrush")
                Assert.Contains($"public Brush {adapter} => Palette.Frozen({neutral});", wpf, StringComparison.Ordinal);
        }
        Assert.Contains("public Brush StatusBrush { get => Palette.Frozen(StatusColour); set => StatusColour = TokenOf(value); }", wpf, StringComparison.Ordinal);
        Assert.Contains("public Brush LegendBrush => Legend switch", wpf, StringComparison.Ordinal);
        // Every neutral token / bool / legend property has an entry (none forgotten), and nothing else does.
        var tokenProps = typeof(DashboardModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && (p.PropertyType == typeof(UiColour) || p.PropertyType == typeof(MapColourMode)
                        || (p.PropertyType == typeof(bool) && p.Name != nameof(DashboardModel.FreqLocked))))
            .Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(tokenProps, AdapterNames.Select(a => a.Neutral).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(AdapterNames.Length, Regex.Count(wpf, @"\[nameof\(\w+\)\] = nameof\(\w+\),"));
    }

    /// <summary>Each neutral token / bool / legend property and the old WPF property it stands in for.</summary>
    private static readonly (string Neutral, string Adapter)[] AdapterNames =
    {
        ("FreqColour", "FreqBrush"), ("InertiaNoteColour", "InertiaNoteBrush"), ("StatusColour", "StatusBrush"),
        ("QueryVerdictColour", "QueryVerdictBrush"), ("FragilityColour", "FragilityBrush"),
        ("FragilityTrendColour", "FragilityTrendBrush"), ("BridgeStatusColour", "BridgeStatusBrush"),
        ("StabilityColour", "StabilityBrush"), ("TripVerdictColour", "TripVerdictBrush"), ("Legend", "LegendBrush"),
        ("InvalidState", "InvalidStateVis"), ("SelectionVisible", "SelectionVis"), ("QueryVisible", "QueryVis"),
        ("QueryViolationsEmpty", "QueryViolationsEmptyVis"), ("HudVisible", "HudVis"),
        ("BridgeStatusVisible", "BridgeStatusVis"), ("AlarmsEmpty", "AlarmsEmptyVis"),
    };

    // ---------------------------------------------------------------------------------------------------------------
    // The comparison
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A copy of <paramref name="r"/> with its converged flag set and every voltage scaled.</summary>
    private static PowerFlowResult Copy(PowerFlowResult r, bool converged, double vmScale) => new()
    {
        Converged = converged,
        Iterations = r.Iterations,
        MaxMismatch = r.MaxMismatch,
        BusIds = r.BusIds,
        Vm = r.Vm.Select(v => v * vmScale).ToArray(),
        VaDeg = r.VaDeg,
        PInjMw = r.PInjMw,
        QInjMvar = r.QInjMvar,
        TotalLossMw = r.TotalLossMw,
        TotalLossMvar = r.TotalLossMvar,
        LineFlows = r.LineFlows,
    };

    // The neutral dashboard properties that stand in for a renamed old one, and the old dashboard properties that have no
    // neutral counterpart by design (none: every old property is covered).
    private static readonly Dictionary<string, string> Renamed = AdapterNames.ToDictionary(a => a.Neutral, a => a.Adapter, StringComparer.Ordinal);

    private static void AssertSame(Old.DashboardViewModel old, DashboardModel neu, string at)
    {
        PropertyInfo[] neuProps = typeof(DashboardModel).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo[] oldProps = typeof(Old.DashboardViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var oldByName = oldProps.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var coveredOld = new HashSet<string>(StringComparer.Ordinal);

        foreach (PropertyInfo np in neuProps)
        {
            string oldName = Renamed.GetValueOrDefault(np.Name, np.Name);
            Assert.True(oldByName.TryGetValue(oldName, out PropertyInfo? op), $"{at}: the old view-model has no {oldName} for {np.Name}");
            coveredOld.Add(oldName);
            object? nv = np.GetValue(neu), ov = op!.GetValue(old);
            SameValue(ov, nv, $"{at}: {np.Name}");
        }
        // Every old property is compared (a property added to the old copy, or dropped from the new model, fails here).
        Assert.Equal(oldProps.Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal), coveredOld.OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>One value of the old view-model against the neutral value that replaced it.</summary>
    private static void SameValue(object? ov, object? nv, string what)
    {
        switch (nv)
        {
            case null:
                Assert.True(ov is null, $"{what}: want {ov}, got null");
                return;
            case string s:
                Assert.True(ov is string os && string.Equals(os, s, StringComparison.Ordinal), $"{what}: want \"{ov}\", got \"{s}\"");
                return;
            case double d:
                Assert.True(ov is double od && BitConverter.DoubleToInt64Bits(od) == BitConverter.DoubleToInt64Bits(d), $"{what}: want {ov:R}, got {d:R}");
                return;
            case int i:
                Assert.True(ov is int oi && oi == i, $"{what}: want {ov}, got {i}");
                return;
            case UiColour c:
                // The token must be the colour of the brush the old code chose (its frozen solid brush).
                var brush = Assert.IsType<Old.SolidColorBrush>(ov);
                Assert.True(brush.Color == c, $"{what}: want {ColourRamps.ToHex(brush.Color)}, got {ColourRamps.ToHex(c)}");
                return;
            case bool b when ov is Old.Visibility vis:
                Assert.True((b ? Old.Visibility.Visible : Old.Visibility.Collapsed) == vis, $"{what}: want {vis}, got {b}");
                return;
            case bool b:
                Assert.True(ov is bool ob && ob == b, $"{what}: want {ov}, got {b}");
                return;
            case MapColourMode mode:
                // The legend swatch: the old gradient's stops against the neutral stops of the named ramp.
                var ramp = Assert.IsType<Old.LinearGradientBrush>(ov);
                Assert.Equal(new Old.Point(0, 0), ramp.Start);
                Assert.Equal(new Old.Point(1, 0), ramp.End);
                IReadOnlyList<(UiColour Colour, double Offset)> stops = DashboardModel.LegendRampStops(mode);
                Assert.Equal(ramp.Stops.Count, stops.Count);
                for (int k = 0; k < stops.Count; k++)
                {
                    Assert.True(ramp.Stops[k].Color == stops[k].Colour, $"{what}: stop {k} colour");
                    Assert.True(BitConverter.DoubleToInt64Bits(ramp.Stops[k].Offset) == BitConverter.DoubleToInt64Bits(stops[k].Offset), $"{what}: stop {k} offset");
                }
                return;
            case FrequencyResponse r:
                var orr = Assert.IsType<FrequencyResponse>(ov);
                SameValue(orr.InitialRocofHzPerS, r.InitialRocofHzPerS, what + ".InitialRocofHzPerS");
                SameValue(orr.NadirHz, r.NadirHz, what + ".NadirHz");
                SameValue(orr.NadirSeconds, r.NadirSeconds, what + ".NadirSeconds");
                SameValue(orr.SettleHz, r.SettleHz, what + ".SettleHz");
                Assert.Equal(orr.Points.Count, r.Points.Count);
                for (int k = 0; k < r.Points.Count; k++)
                {
                    Assert.Equal(orr.Points[k].Time, r.Points[k].Time);
                    SameValue(orr.Points[k].Hz, r.Points[k].Hz, $"{what}.Points[{k}].Hz");
                    SameValue(orr.Points[k].RocofHzPerS, r.Points[k].RocofHzPerS, $"{what}.Points[{k}].RocofHzPerS");
                }
                return;
            case FrequencyBands fb:
                var ob2 = Assert.IsType<FrequencyBands>(ov);
                foreach (PropertyInfo p in typeof(FrequencyBands).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.PropertyType == typeof(double)) SameValue(p.GetValue(ob2), p.GetValue(fb), $"{what}.{p.Name}");
                return;
            case IList list:
                var olist = Assert.IsAssignableFrom<IList>(ov);
                Assert.True(olist.Count == list.Count, $"{what}: want {olist.Count} rows, got {list.Count}");
                for (int k = 0; k < list.Count; k++) SameRow(olist[k], list[k], $"{what}[{k}]");
                return;
            default:
                Assert.Fail($"{what}: no comparison for a {nv.GetType().Name}");
                return;
        }
    }

    /// <summary>The old WPF row property each neutral row's colour token stands in for.</summary>
    private static string OldRowName(object neuRow, string name) => (neuRow, name) switch
    {
        (BoundaryRowModel or ImportLinkRowModel, "BarColour") => "Bar",
        (ImportLinkRowModel, "BadgeColour") => "BadgeBrush",
        (FuelRowModel, "Colour") => "Color",
        (VoltageBandDialModel or AlarmEntry or ViolationEntry, "Colour") => "Brush",
        _ => name,
    };

    /// <summary>One row of a list (a string, a key/value, or a row object), every public property compared.</summary>
    private static void SameRow(object? orow, object? nrow, string what)
    {
        if (nrow is string or null) { SameValue(orow, nrow, what); return; }
        Assert.NotNull(orow);
        PropertyInfo[] np = nrow.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo[] op = orow!.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var oldByName = op.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo p in np)
        {
            if (p.GetIndexParameters().Length > 0) continue;
            if (p.Name == "EqualityContract") continue;   // a record's compiler-made member
            string oldName = OldRowName(nrow, p.Name);
            Assert.True(oldByName.TryGetValue(oldName, out PropertyInfo? o), $"{what}: the old row has no {oldName}");
            covered.Add(oldName);
            SameValue(o!.GetValue(orow), p.GetValue(nrow), $"{what}.{p.Name}");
        }
        Assert.Equal(op.Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract").Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal),
            covered.OrderBy(s => s, StringComparer.Ordinal));
    }
}
