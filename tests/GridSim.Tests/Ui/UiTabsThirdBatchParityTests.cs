using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using GridSim.Core.Analysis;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Tabs.AuditLog;
using GridSim.Ui.Tabs.Experimental;
using GridSim.Ui.Tabs.HumanScale;
using GridSim.Ui.Tabs.NesoRp1;
using Xunit;
using Old = GridSim.Tests.Ui.OldTabsC;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 1.6 (third tab batch): the logic of THE HUMAN SCALE, NESO RP1 and EXPERIMENTAL tabs and of the
/// audit-log window moved from the WPF view-models (and the WPF window) into the platform-neutral GridSim.Ui.Tabs
/// models. That is an orchestration-only move, so every value each one shows must be exactly what the WPF code showed
/// before. These tests feed the same frames (from <see cref="SimHost"/>, case9 and the GB reduced case, with the
/// dashboard's own clock) and the same calls to each new model and to a verbatim copy of the old WPF code (the OldTabsC
/// folder), then compare EVERY public property of the new model, every row of every list, and every drill-down page,
/// with exact equality: strings ordinal, doubles by their bits, colour tokens against the colour of the brush the old
/// code chose, bools against the old Visibility. A property added to either side later fails the coverage check until
/// it is compared here. The EXPERIMENTAL tab's background work is driven deterministically: inline (no UI loop) on both
/// sides, and through a queue the test drains (a synchronisation context for the old code, the poster for the new).
/// </summary>
public sealed class UiTabsThirdBatchParityTests : IDisposable
{
    private const int Ticks = 12;

    public static TheoryData<string> Cases => new() { "case9", "uk" };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-ui-audit-{Guid.NewGuid():N}");

    public UiTabsThirdBatchParityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    /// <summary>
    /// xUnit runs a test body inside its own synchronisation context, and the old EXPERIMENTAL code chose its path by
    /// SynchronizationContext.Current; so the EXPERIMENTAL tests run with none (the WPF "no dispatcher" path), set by
    /// this scope and restored when it ends.
    /// </summary>
    private sealed class NoUiContext : IDisposable
    {
        private readonly SynchronizationContext? _was = SynchronizationContext.Current;
        public NoUiContext() => SynchronizationContext.SetSynchronizationContext(null);
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_was);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The feed: a loaded host, its frames, and the dashboard clock the tabs are stamped with
    // ---------------------------------------------------------------------------------------------------------------

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

    private static SimHost Host(string name)
    {
        var host = new SimHost();
        LiveLoadResult loaded = host.LoadLive(SourceOf(name), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.True(loaded.Loaded, loaded.Refusal);
        return host;
    }

    /// <summary>The next live frame, and the clock the dashboard shows for it (the tabs are fed the dashboard's Clock).</summary>
    private static (HostFrame Frame, string Clock) Next(SimHost host, DashboardModel dash)
    {
        HostFrame hf = Assert.IsType<HostFrame>(host.ProduceNext());
        Assert.Equal(HostFrameKind.LiveSolved, hf.Kind);
        dash.Update(hf.Frame!, hf.Boundaries, hf.SolveMs);
        return (hf, dash.Clock);
    }

    /// <summary>A copy of <paramref name="r"/> with its converged flag set, and (optionally) no bus angles or other ones, or no voltages.</summary>
    private static PowerFlowResult Copy(PowerFlowResult r, bool converged, bool dropAngles = false, bool dropVoltages = false, double[]? angles = null) => new()
    {
        Converged = converged,
        Iterations = r.Iterations,
        MaxMismatch = r.MaxMismatch,
        BusIds = r.BusIds,
        Vm = dropVoltages ? Array.Empty<double>() : r.Vm,
        VaDeg = dropAngles ? Array.Empty<double>() : angles ?? r.VaDeg,
        PInjMw = r.PInjMw,
        QInjMvar = r.QInjMvar,
        TotalLossMw = r.TotalLossMw,
        TotalLossMvar = r.TotalLossMvar,
        LineFlows = r.LineFlows,
    };

    /// <summary>The same network at another nominal frequency (the NESO RP1 page hides itself off 50 Hz, N301).</summary>
    private static GridModel AtNominal(GridModel m, double hz) => new()
    {
        BaseMva = m.BaseMva,
        Buses = m.Buses,
        Generators = m.Generators,
        Branches = m.Branches,
        Oltcs = m.Oltcs,
        Boundaries = m.Boundaries,
        FrequencyHz = hz,
        Name = m.Name,
    };

    // ---------------------------------------------------------------------------------------------------------------
    // THE HUMAN SCALE
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void HumanScale_every_displayed_value_equals_the_old_wpf_view_model_frame_for_frame(string name)
    {
        SimHost host = Host(name);
        var dash = new DashboardModel();
        var neu = new HumanScaleModel();
        var old = new Old.HumanScaleVm();
        AssertSame(old, neu, $"{name} before the first frame");
        int mostMachines = 0, reordered = 0;
        for (int tick = 1; tick <= Ticks; tick++)
        {
            (HostFrame hf, string clock) = Next(host, dash);
            SimFrame f = hf.Frame!;
            neu.Update(f, clock); old.Update(f, clock);
            AssertSame(old, neu, $"{name} tick {tick}");
            mostMachines = Math.Max(mostMachines, neu.Machines.Count);
            switch (tick)
            {
                case 3:
                case 5:
                    // The bus angles reversed and then restored: the machine ranking turns over and back, so the rows are
                    // re-ordered by the parsed "+12.3 deg" text (N310) on both sides.
                    string before = string.Join("|", neu.Machines.Select(m => m.Key));
                    SimFrame turned = f with { Result = Copy(f.Result, converged: true, angles: Enumerable.Reverse(f.Result.VaDeg).ToArray()) };
                    neu.Update(turned, clock); old.Update(turned, clock);
                    AssertSame(old, neu, $"{name} tick {tick} angles reversed");
                    if (string.Join("|", neu.Machines.Select(m => m.Key)) != before) reordered++;
                    neu.Update(f, clock); old.Update(f, clock);
                    AssertSame(old, neu, $"{name} tick {tick} angles restored");
                    break;
                case 2:
                    // The flywheel delivering, absorbing, coasting at the deadband edges, and a frequency read as unset.
                    foreach (double rocof in new[] { -0.25, 0.25, -1e-4, 1e-4, -1.0000001e-4, 1.0000001e-4, 0.0, -0.0123456 })
                        foreach (double hz in new[] { f.FrequencyHz, 0.5, 49.75, 50.3 })
                        {
                            SimFrame g = f with { RocofHzPerS = rocof, FrequencyHz = hz };
                            neu.Update(g, clock); old.Update(g, clock);
                            AssertSame(old, neu, $"{name} tick {tick} rocof {rocof:R} at {hz:R} Hz");
                        }
                    break;
                case 10:
                    // No bus angles: the twist panels clear. Test changed 2026-10-24 (agreed by Mark): the machine key
                    // index is now cleared with the rows in both apps (and in the oracle, edit listed in its header), so
                    // the ranked machines come back on the next frame with angles; the remaining ticks compare that.
                    SimFrame flat = f with { Result = Copy(f.Result, converged: true, dropAngles: true) };
                    neu.Update(flat, clock); old.Update(flat, clock);
                    AssertSame(old, neu, $"{name} tick {tick} no angles");
                    Assert.Empty(neu.Machines);
                    neu.Update(f, clock); old.Update(f, clock);
                    AssertSame(old, neu, $"{name} tick {tick} angles back");
                    if (name == "uk") Assert.NotEmpty(neu.Machines);   // the ranked machines are back, not lost for the case
                    break;
                case 6:
                    // A not-converged frame, and a frame at another nominal (the rpm reference follows the case).
                    SimFrame bad = f with { Result = Copy(f.Result, converged: false) };
                    neu.Update(bad, clock); old.Update(bad, clock);
                    AssertSame(old, neu, $"{name} tick {tick} not converged");
                    SimFrame sixty = f with { Model = AtNominal(f.Model, 60.0), FrequencyHz = 59.9 };
                    neu.Update(sixty, clock); old.Update(sixty, clock);
                    AssertSame(old, neu, $"{name} tick {tick} at 60 Hz");
                    break;
            }
        }
        Assert.NotEmpty(neu.Regions);
        if (name == "uk")
        {
            // The GB case has typed synchronous plant: the ranked machines were compared, in order, and re-ordered.
            Assert.True(mostMachines > 5, $"only {mostMachines} ranked machines were compared");
            Assert.True(reordered > 0, "the machine ranking never changed, so the re-ordering was not compared");
        }
        else Assert.Equal(0, mostMachines);   // case9's generators are untyped ("Other"): no synchronous machine to rank
    }

    // ---------------------------------------------------------------------------------------------------------------
    // NESO RP1
    // ---------------------------------------------------------------------------------------------------------------

    private static IEnumerable<(BridgeProtocol.OperatorBlock? Op, BridgeProtocol.MarketBlock? Mk, DateTime? At)> Rp1Blocks()
    {
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        yield return (null, null, null);
        yield return (new BridgeProtocol.OperatorBlock(), new BridgeProtocol.MarketBlock(), t0);
        yield return (
            new BridgeProtocol.OperatorBlock
            {
                SynchronousInertiaGvaS = 123.456, OutturnInertiaGvaS = 150.5, MarketInertiaGvaS = 22.25,
                BalancingActions = new()
                {
                    new() { BmUnit = "T_DRAXX-1", Kind = "OFFER", LevelMw = 412.6, FuelType = "BIOMASS", Synchronous = true },
                    new() { BmUnit = "T_PEMB-21", Kind = "BID", LevelMw = -150.4, FuelType = "CCGT", Synchronous = true },
                    new() { BmUnit = "E_WIND-2", Kind = "offer", LevelMw = 88.5, FuelType = "WIND", Synchronous = false },
                    new() { BmUnit = null, Kind = null, LevelMw = null, FuelType = null, Synchronous = null },
                },
                Constraints = new()
                {
                    new() { Name = "B6 SCOTEX", CostGbp = 123_456, Binding = true },
                    new() { Name = null, CostGbp = null, Binding = null },
                    new() { Name = "EC5", CostGbp = 0.5, Binding = false },
                },
            },
            new BridgeProtocol.MarketBlock
            {
                ImbalancePriceGbpMwh = 72.345,
                Interconnectors = new()
                {
                    new() { Name = "IFA", FlowMw = 1999.5 },
                    new() { Name = "ElecLink interconnector", FlowMw = 1000.25 },
                    new() { Name = "NS Link interconnector", FlowMw = -1400.25 },
                    new() { Name = "Viking", FlowMw = 1400.0 },
                    new() { Name = "IFA2", FlowMw = 950.0 },
                    new() { Name = "Moyle", FlowMw = 0.2 },   // idle (below the 0.5 MW cut-off): left out
                    new() { Name = null, FlowMw = 300.0 },
                    new() { Name = "BritNed", FlowMw = null },
                },
            },
            t0.AddMinutes(1));
        yield return (null, new BridgeProtocol.MarketBlock { ImbalancePriceGbpMwh = -5.5 }, t0.AddMinutes(2));   // same period
        yield return (null, new BridgeProtocol.MarketBlock { ImbalancePriceGbpMwh = 99.125 }, t0.AddMinutes(31));   // next period
        yield return (null, new BridgeProtocol.MarketBlock { ImbalancePriceGbpMwh = 12.0 }, null);   // undated: every price a sample
        yield return (
            new BridgeProtocol.OperatorBlock
            {
                SynchronousInertiaGvaS = 0.5, MarketInertiaGvaS = null,
                BalancingActions = Enumerable.Range(0, 45).Select(i => new BridgeProtocol.BalancingActionDto { BmUnit = $"U{i}", Kind = i % 2 == 0 ? "BID" : "OFFER", LevelMw = i * 10.25, Synchronous = i % 3 == 0 ? null : i % 3 == 1 }).ToList(),
                Constraints = Enumerable.Range(0, 20).Select(i => new BridgeProtocol.ConstraintDto { Name = $"C{i}", CostGbp = i * 999.0, Binding = i % 2 == 0 }).ToList(),
            },
            new BridgeProtocol.MarketBlock
            {
                Interconnectors = new() { new() { Name = "IFA", FlowMw = 4500.0 }, new() { Name = "ElecLink", FlowMw = 1000.0 } },
            },
            t0.AddMinutes(61));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NesoRp1_every_displayed_value_and_drill_down_page_equals_the_old_wpf_view_model_frame_for_frame(string name)
    {
        SimHost host = Host(name);
        var dash = new DashboardModel();
        var neu = new NesoRp1Model();
        var old = new Old.NesoRp1Vm();
        var pages = new List<Rp1InspectPage>();
        neu.InspectRequested += pages.Add;
        AssertSame(old, neu, $"{name} before the first frame");
        for (int tick = 1; tick <= Ticks; tick++)
        {
            (HostFrame hf, string clock) = Next(host, dash);
            SimFrame f = hf.Frame!;
            neu.Update(f, hf.Boundaries, clock); old.Update(f, hf.Boundaries, clock);
            AssertSame(old, neu, $"{name} tick {tick}");
            switch (tick)
            {
                case 2:
                    int k = 0;
                    foreach ((BridgeProtocol.OperatorBlock? op, BridgeProtocol.MarketBlock? mk, DateTime? at) in Rp1Blocks())
                    {
                        neu.SetRp1Data(op, mk, at); old.SetRp1Data(op, mk, at);
                        AssertSame(old, neu, $"{name} tick {tick} block {k}");
                        AssertDrillDowns(old, neu, pages, $"{name} tick {tick} block {k}");
                        k++;
                    }
                    break;
                case 4:
                    // Boundaries grow, shrink, empty and come back (the trend arrows and the narrative's top boundary).
                    var more = new List<BoundaryFlow>(hf.Boundaries) { new("TEST CUT", -5_432.1, 4_000, 135.8025, true), new("SMALL CUT", 999.9, 999.99, 99.99, false) };
                    neu.Update(f, more, clock); old.Update(f, more, clock);
                    AssertSame(old, neu, $"{name} tick {tick} more boundaries");
                    var moved = more.Select(b => b with { LoadingPercent = b.LoadingPercent + (b.Name.Length % 2 == 0 ? 0.75 : -0.75) }).ToList();
                    neu.Update(f, moved, clock); old.Update(f, moved, clock);
                    AssertSame(old, neu, $"{name} tick {tick} moved boundaries");
                    neu.Update(f, Array.Empty<BoundaryFlow>(), clock); old.Update(f, Array.Empty<BoundaryFlow>(), clock);
                    AssertSame(old, neu, $"{name} tick {tick} no boundaries");
                    break;
                case 6:
                    // N301: a case off 50 Hz hides the page (every figure still computed); back at 50 Hz it shows again.
                    SimFrame sixty = f with { Model = AtNominal(f.Model, 60.0) };
                    neu.Update(sixty, hf.Boundaries, clock); old.Update(sixty, hf.Boundaries, clock);
                    AssertSame(old, neu, $"{name} tick {tick} at 60 Hz");
                    Assert.False(neu.IsPageVisible);
                    neu.Update(f, hf.Boundaries, clock); old.Update(f, hf.Boundaries, clock);
                    AssertSame(old, neu, $"{name} tick {tick} back at 50 Hz");
                    break;
                case 8:
                    neu.Reset(); old.Reset();
                    AssertSame(old, neu, $"{name} tick {tick} reset");
                    break;
            }
            AssertDrillDowns(old, neu, pages, $"{name} tick {tick}");
        }
        Assert.NotEmpty(pages);   // drill-down pages were built and compared
    }

    /// <summary>
    /// Every drill-down the tab offers right now, on both sides: each fuel of the dispatch stack and of inertia-by-source
    /// (and a fuel with no unit), each BM row, each interconnector row; and from each page, every row that drills
    /// further (a unit from its fuel, the fuel back from a unit, a co-member from an interconnector).
    /// </summary>
    private static void AssertDrillDowns(Old.NesoRp1Vm old, NesoRp1Model neu, List<Rp1InspectPage> pages, string at)
    {
        void Same(string what)
        {
            Assert.True(old.Inspector.IsOpen, $"{at} {what}: the old inspector is not open");
            SamePage(old.Inspector, pages[^1], $"{at} {what}");
        }

        // A fuel with no unit online this frame shows nothing on either side: no new page, and the old inspector
        // untouched (the same open state, title and first row object as before the click).
        void Fuel(string fuel)
        {
            int n = pages.Count;
            (bool open, string title, object? first) = (old.Inspector.IsOpen, old.Inspector.Title, old.Inspector.Rows.FirstOrDefault());
            neu.InspectFuel(fuel); old.InspectFuelCommand.Execute(fuel);
            if (pages.Count == n)
            {
                Assert.True(open == old.Inspector.IsOpen && title == old.Inspector.Title && ReferenceEquals(first, old.Inspector.Rows.FirstOrDefault()),
                    $"{at} fuel {fuel}: the new tab showed no page but the old one did");
                return;
            }
            Assert.Equal(n + 1, pages.Count);
            Same($"fuel {fuel}");
            DrillEveryRow(old, pages, $"{at} fuel {fuel}", depth: 2);
        }

        Fuel("NO SUCH FUEL");
        foreach (string fuel in neu.GenStack.Select(g => g.Fuel).Concat(neu.InertiaBySource.Select(i => i.Fuel)).Distinct().ToList())
            Fuel(fuel);
        for (int i = 0; i < neu.BmDelta.Count; i++)
        {
            neu.InspectBm(neu.BmDelta[i]); old.InspectBmCommand.Execute(old.BmDelta[i]);
            Same($"BM row {i}");
        }
        for (int i = 0; i < neu.Interconnectors.Count; i++)
        {
            neu.InspectIc(neu.Interconnectors[i]); old.InspectIcCommand.Execute(old.Interconnectors[i]);
            Same($"interconnector row {i}");
            DrillEveryRow(old, pages, $"{at} interconnector row {i}", depth: 1);
        }
        old.Inspector.Close();
    }

    /// <summary>Click every drilling row of the page now shown (old and new together), compare, and go on to depth.</summary>
    private static void DrillEveryRow(Old.NesoRp1Vm old, List<Rp1InspectPage> pages, string at, int depth)
    {
        if (depth == 0) return;
        Rp1InspectPage page = pages[^1];
        Old.InspectRow[] oldRows = old.Inspector.Rows.ToArray();
        Assert.Equal(oldRows.Length, page.Rows.Count);
        for (int r = 0; r < page.Rows.Count; r++)
        {
            Rp1InspectRow row = page.Rows[r];
            Assert.Equal(oldRows[r].Drill is not null, row.Drill is not null);
            if (row.Drill is null) continue;
            row.Drill(); oldRows[r].Drill!.Execute(null);
            SamePage(old.Inspector, pages[^1], $"{at} > row {r}");
            DrillEveryRow(old, pages, $"{at} > row {r}", depth - 1);
        }
    }

    /// <summary>One drill-down page: the old inspector's title, subtitle, stat tiles and rows against the neutral page.</summary>
    private static void SamePage(Old.InspectorVm old, Rp1InspectPage neu, string at)
    {
        SameText(old.Title, neu.Title, $"{at}: Title");
        SameText(old.Subtitle, neu.Subtitle, $"{at}: Subtitle");
        Assert.True(old.Stats.Count == neu.Stats.Count, $"{at}: want {old.Stats.Count} stats, got {neu.Stats.Count}");
        for (int i = 0; i < neu.Stats.Count; i++)
        {
            SameText(old.Stats[i].Label, neu.Stats[i].Label, $"{at}: Stats[{i}].Label");
            SameText(old.Stats[i].Value, neu.Stats[i].Value, $"{at}: Stats[{i}].Value");
            SameColour(old.Stats[i].ValueBrush, neu.Stats[i].ValueColour, $"{at}: Stats[{i}].ValueColour");
        }
        Assert.True(old.Rows.Count == neu.Rows.Count, $"{at}: want {old.Rows.Count} rows, got {neu.Rows.Count}");
        for (int i = 0; i < neu.Rows.Count; i++)
        {
            Old.InspectRow o = old.Rows[i];
            Rp1InspectRow n = neu.Rows[i];
            SameText(o.Primary, n.Primary, $"{at}: Rows[{i}].Primary");
            SameText(o.Secondary, n.Secondary, $"{at}: Rows[{i}].Secondary");
            SameText(o.Value, n.Value, $"{at}: Rows[{i}].Value");
            SameText(o.Sub, n.Sub, $"{at}: Rows[{i}].Sub");
            SameColour(o.Swatch, n.Swatch, $"{at}: Rows[{i}].Swatch");
            SameColour(o.ValueBrush, n.ValueColour, $"{at}: Rows[{i}].ValueColour");
            Assert.True((n.HasDrill ? Old.Visibility.Visible : Old.Visibility.Collapsed) == o.Chevron, $"{at}: Rows[{i}].HasDrill");
            Assert.True((o.Drill is null) == (n.Drill is null), $"{at}: Rows[{i}].Drill");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // EXPERIMENTAL
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void Experimental_every_displayed_value_equals_the_old_wpf_view_model_when_computed_inline(string name)
    {
        using var noUi = new NoUiContext();
        // No synchronisation context (the WPF test-host path) on the old side; a poster that says "no UI loop" on the new.
        Assert.Null(SynchronizationContext.Current);
        SimHost host = Host(name);
        var dash = new DashboardModel();
        var neu = new ExperimentalModel(() => null);
        var old = new Old.ExperimentalVm();
        AssertSame(old, neu, $"{name} before the first frame");
        for (int tick = 1; tick <= Ticks; tick++)
        {
            (HostFrame hf, _) = Next(host, dash);
            SimFrame f = hf.Frame!;
            neu.Update(f); old.Update(f);
            AssertSame(old, neu, $"{name} tick {tick}");
            // The tab gate: shown on tick 3 (catches up on the remembered frame at once), hidden on 7, shown again on 9.
            if (tick is 3 or 9) { neu.Active = true; old.Active = true; AssertSame(old, neu, $"{name} tick {tick} shown"); }
            if (tick == 7) { neu.Active = false; old.Active = false; AssertSame(old, neu, $"{name} tick {tick} hidden"); }
            if (tick == 5)
            {
                // Showing twice is a no-op; a frame with no model or no result is ignored; a not-converged frame says so.
                neu.Active = true; old.Active = true;
                neu.Update(f with { Model = null! }); old.Update(f with { Model = null! });
                neu.Update(f with { Result = null! }); old.Update(f with { Result = null! });
                AssertSame(old, neu, $"{name} tick {tick} no model");
                SimFrame bad = f with { Result = Copy(f.Result, converged: false) };
                neu.Update(bad); old.Update(bad);
                AssertSame(old, neu, $"{name} tick {tick} not converged");
                Assert.Equal("power flow did not converge on this frame - nothing to trace", neu.TraceStatus);
            }
        }
        Assert.NotEmpty(neu.Laws);   // the law table was compared row by row, not only an empty list
    }

    /// <summary>A UI loop for the old code: every Post is queued, and the test runs them, with itself as the context.</summary>
    private sealed class QueueContext : SynchronizationContext
    {
        public readonly ConcurrentQueue<(SendOrPostCallback Cb, object? State)> Posted = new();
        public override void Post(SendOrPostCallback d, object? state) => Posted.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("the tab never sends");

        /// <summary>Run the next posted item (waiting for the worker to post it), on this context.</summary>
        public void RunNext()
        {
            (SendOrPostCallback Cb, object? State) item = Take(Posted, "the old tab never posted its result");
            SynchronizationContext? was = Current;
            SetSynchronizationContext(this);
            try { item.Cb(item.State); }
            finally { SetSynchronizationContext(was); }
        }
    }

    /// <summary>The new tab's poster as a queue the test drains (the front end's UI loop, driven by hand).</summary>
    private sealed class PosterQueue
    {
        public readonly ConcurrentQueue<Action> Posted = new();
        public int Applied { get; private set; }

        /// <summary>The poster the model is built with: the worker's "show this result" step is queued, never run.</summary>
        public void Post(Action apply) => Posted.Enqueue(apply);

        public void RunNext()
        {
            Action apply = Take(Posted, "the new tab never posted its result");
            apply();
            Applied++;
        }
    }

    /// <summary>The next queued item, waiting (up to five minutes) for the worker to queue it; fails loud otherwise.</summary>
    private static T Take<T>(ConcurrentQueue<T> queue, string failure)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        T? item;
        while (!queue.TryDequeue(out item))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(5), failure);
            Thread.Sleep(2);
        }
        return item!;
    }

    /// <summary>True when nothing is queued within <paramref name="ms"/> milliseconds (nothing more is coming).</summary>
    private static bool StaysEmpty<T>(ConcurrentQueue<T> queue, int ms)
    {
        Thread.Sleep(ms);
        return queue.IsEmpty;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Experimental_computes_off_the_calling_thread_and_applies_only_through_the_poster_exactly_as_the_old_code(string name)
    {
        using var noUi = new NoUiContext();
        SimHost host = Host(name);
        var dash = new DashboardModel();
        var posts = new PosterQueue();
        var neu = new ExperimentalModel(() => posts.Post);
        var ctx = new QueueContext();
        var old = new Old.ExperimentalVm();
        SimFrame[] frames = Enumerable.Range(0, 4).Select(_ => Next(host, dash).Frame.Frame!).ToArray();

        // Show the tab with three frames arriving while the first is still being computed: the first is computed, the
        // second is parked and then replaced by the third (only the NEWEST parked frame is computed next).
        neu.Active = true;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            old.Active = true;
            foreach (SimFrame f in frames.Take(3)) { neu.Update(f); old.Update(f); }
        }
        finally { SynchronizationContext.SetSynchronizationContext(null); }

        // Nothing is shown until the front end runs what the worker handed it: the calling thread was never blocked.
        Assert.Empty(neu.Laws);
        Assert.Equal(" - ", neu.Gate);
        AssertSame(old, neu, $"{name} before any result is applied");

        // First result (frame 0), then the chained computation of the newest parked frame (frame 2), and no more.
        posts.RunNext(); ctx.RunNext();
        AssertSame(old, neu, $"{name} first result");
        posts.RunNext(); ctx.RunNext();
        AssertSame(old, neu, $"{name} newest parked frame");
        Assert.Equal(2, posts.Applied);
        Assert.True(StaysEmpty(posts.Posted, 200), "frame 1 was parked and replaced, never computed");

        // That last state is exactly the inline computation of frame 2.
        var inline = new ExperimentalModel(() => null) { Active = true };
        inline.Update(frames[2]);
        AssertSameModel(inline, neu, $"{name} async equals inline");

        // A frame while hidden is remembered, not computed; showing the tab again computes it through the poster.
        neu.Active = false; old.Active = false;
        neu.Update(frames[3]); old.Update(frames[3]);
        Assert.Empty(posts.Posted);
        Assert.Empty(ctx.Posted);
        neu.Active = true;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try { old.Active = true; }
        finally { SynchronizationContext.SetSynchronizationContext(null); }
        posts.RunNext(); ctx.RunNext();
        AssertSame(old, neu, $"{name} caught up on show");

        Assert.Throws<ArgumentNullException>(() => new ExperimentalModel(null!));
    }

    [Fact]
    public void Experimental_a_model_that_throws_is_shown_as_model_failed_exactly_as_the_old_code()
    {
        using var noUi = new NoUiContext();
        SimHost host = Host("case9");
        var dash = new DashboardModel();
        SimFrame good = Next(host, dash).Frame.Frame!;
        // No voltages: the conformance audit cannot index the solution, so a model throws on the worker.
        SimFrame broken = good with { Result = Copy(good.Result, converged: true, dropVoltages: true) };
        var posts = new PosterQueue();
        var neu = new ExperimentalModel(() => posts.Post) { Active = true };
        var ctx = new QueueContext();
        var old = new Old.ExperimentalVm();
        SynchronizationContext.SetSynchronizationContext(ctx);
        try { old.Active = true; neu.Update(good); old.Update(good); }
        finally { SynchronizationContext.SetSynchronizationContext(null); }
        posts.RunNext(); ctx.RunNext();
        AssertSame(old, neu, "good frame");
        Assert.StartsWith("ADMISSIBLE", neu.Gate, StringComparison.Ordinal);

        SynchronizationContext.SetSynchronizationContext(ctx);
        try { neu.Update(broken); old.Update(broken); }
        finally { SynchronizationContext.SetSynchronizationContext(null); }
        posts.RunNext(); ctx.RunNext();
        AssertSame(old, neu, "broken frame");
        Assert.StartsWith("MODEL FAILED: ", neu.Gate, StringComparison.Ordinal);
        Assert.Empty(neu.Laws);
        Assert.Equal(new UiColour(0xFF, 0xE0, 0x5A, 0x5A), neu.GateColour);
    }

    [Fact]
    public void Experimental_wave_guard_is_the_old_predicate()
    {
        SimHost host = Host("case9");
        SimFrame f = Next(host, new DashboardModel()).Frame.Frame!;
        foreach (PowerFlowResult r in new[] { f.Result, Copy(f.Result, converged: false) })
            Assert.Equal(Old.ExperimentalVm.WaveModelCanForm(f.Model, r), ExperimentalModel.WaveModelCanForm(f.Model, r));
        Assert.Equal(Old.ExperimentalVm.HeavyModelBusLimit, ExperimentalModel.HeavyModelBusLimit);
        Assert.Equal(BitConverter.DoubleToInt64Bits(Old.ExperimentalVm.ModeShapeSplitAmplitude), BitConverter.DoubleToInt64Bits(ExperimentalModel.ModeShapeSplitAmplitude));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // AUDIT LOG
    // ---------------------------------------------------------------------------------------------------------------

    private string Log(string name, bool tamper)
    {
        string path = Path.Combine(_dir, name);
        using (AuditLogWriter w = AuditLogWriter.Open(path))
        {
            w.Append(new DateTime(2026, 10, 4, 9, 30, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "actor=a action=estimate outcome=exit=0 SP1");
            w.Append(new DateTime(2026, 10, 4, 9, 31, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "actor=a action=estimate outcome=exit=3 SP2");
            w.Append(new DateTime(2026, 10, 4, 9, 32, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "actor=b action=replay outcome=exit=0 SP3");
        }
        if (tamper)
        {
            // The fixture's tampered line: the second entry's text is changed after it was chained.
            string[] text = File.ReadAllLines(path);
            text[1] = text[1].Replace("SP2", "SP9", StringComparison.Ordinal);
            File.WriteAllText(path, string.Join("\n", text) + "\n");
        }
        return path;
    }

    /// <summary>The theme brush colour the old window showed for each tone (WPF Theme.xaml Text, Green, Red).</summary>
    private static UiColour ToneColour(AuditVerdictTone tone) => tone switch
    {
        AuditVerdictTone.Neutral => ColourRamps.FromHex("#E6EDF3"),
        AuditVerdictTone.Verified => ColourRamps.FromHex("#2FD27F"),
        AuditVerdictTone.Failed => ColourRamps.FromHex("#FF5B5B"),
        _ => throw new ArgumentOutOfRangeException(nameof(tone)),
    };

    private static void SameAudit(Old.AuditLogWindow old, AuditLogModel neu, string at)
    {
        SameText(old.Verdict, neu.Verdict, $"{at}: Verdict");
        SameText(old.Entries, neu.Entries, $"{at}: Entries");
        SameColour(old.VerdictBrush, ToneColour(neu.Tone), $"{at}: Tone");
        SameText(old.CountText, neu.AnchorCount, $"{at}: AnchorCount");
        SameText(old.HeadText, neu.AnchorHead, $"{at}: AnchorHead");
    }

    [Fact]
    public void AuditLog_shows_exactly_what_the_old_window_showed_and_flags_the_tampered_line()
    {
        string good = Log("good.jsonl", tamper: false);
        string bad = Log("tampered.jsonl", tamper: true);
        string head = AuditLogDisplay.FromFile(good).Anchor!.HeadHash;
        var old = new Old.AuditLogWindow();
        var neu = new AuditLogModel();
        SameAudit(old, neu, "new window");
        Assert.Equal(AuditVerdictTone.Neutral, neu.Tone);

        // (anchor count, anchor head, file chosen or null = cancelled)
        var steps = new (string Count, string Head, string? File, string What)[]
        {
            ("", "", good, "a log that verifies"),
            ("", "", bad, "the tampered log"),
            ("", "", null, "cancelled after a verdict"),
            ("3", head, good, "the verified log against its own anchor"),
            ("2", head, good, "the verified log against a shorter anchor"),
            ("3", "00", good, "a wrong head hash"),
            ("abc", "", good, "an anchor that does not parse"),
            ("3", "", good, "a count with no head"),
            ("", "", Path.Combine(_dir, "absent.jsonl"), "a missing file"),
            ("", "", _dir, "a folder"),
            ("3", head, bad, "the tampered log against the good anchor"),
            ("", "", good, "verified again after failures"),
        };
        foreach ((string count, string h, string? file, string what) in steps)
        {
            old.CountText = count; old.HeadText = h; old.NextFile = file;
            neu.AnchorCount = count; neu.AnchorHead = h;
            int asked = 0, oldAsked = old.Dialogs;
            neu.Open(() => { asked++; return file; });
            old.Open();
            SameAudit(old, neu, what);
            // The dialog is asked for exactly when the old window opened its dialog (only once the anchor parsed), and
            // with the old title and filter.
            Assert.True(asked == old.Dialogs - oldAsked, $"{what}: the new model asked for the file {asked} times, the old window {old.Dialogs - oldAsked}");
            if (asked > 0)
            {
                Assert.Equal(AuditLogModel.DialogTitle, old.DialogTitle);
                Assert.Equal(AuditLogModel.DialogFilter, old.DialogFilter);
            }
        }

        // The tampered line is flagged as WPF flags it: only the TAMPERED verdict, in red, and no entries at all.
        neu.AnchorCount = ""; neu.AnchorHead = "";
        neu.Open(() => bad);
        Assert.Equal("", neu.Entries);
        Assert.Equal(AuditVerdictTone.Failed, neu.Tone);
        Assert.Contains("TAMPERED", neu.Verdict, StringComparison.Ordinal);
        Assert.Equal(AuditLogDisplay.FromFile(bad).Verdict, neu.Verdict);
        neu.Open(() => good);
        Assert.Equal(AuditVerdictTone.Verified, neu.Tone);
        Assert.Equal(string.Join(Environment.NewLine, AuditLogDisplay.FromFile(good).EntryLines), neu.Entries);
    }

    [Fact]
    public async Task AuditLog_asks_for_the_file_only_after_the_anchor_parsed_and_the_async_open_is_the_same()
    {
        string good = Log("good.jsonl", tamper: false);
        var neu = new AuditLogModel { AnchorCount = "x" };
        int asked = 0;
        neu.Open(() => { asked++; return good; });
        Assert.Equal(0, asked);
        Assert.StartsWith("NOT CHECKED: ", neu.Verdict, StringComparison.Ordinal);

        var a = new AuditLogModel();
        var b = new AuditLogModel();
        a.Open(() => good);
        await b.OpenAsync(() => Task.FromResult<string?>(good));
        Assert.Equal(a.Verdict, b.Verdict);
        Assert.Equal(a.Entries, b.Entries);
        Assert.Equal(a.Tone, b.Tone);
        Assert.Throws<ArgumentNullException>(() => a.Open(null!));
        Assert.Throws<ArgumentNullException>(() => a.ShowFile(null!, null));

        // The dialog's name-and-pattern form is the WPF filter taken apart (one source, so they cannot drift).
        Assert.Equal(new[] { "Audit logs", "All files" }, AuditLogModel.DialogFileTypes.Select(t => t.Name));
        Assert.Equal(new[] { "*.jsonl", "*.log", "*.json" }, AuditLogModel.DialogFileTypes[0].Patterns);
        Assert.Equal(new[] { "*.*" }, AuditLogModel.DialogFileTypes[1].Patterns);
        Assert.Equal("GridSim - audit log (verified)", AuditLogModel.WindowTitle);
        Assert.Equal(" OPEN LOG... ", AuditLogModel.OpenButtonText);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The comparison
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The old WPF property each neutral property stands in for, where it was renamed (token or bool).</summary>
    private static string OldName(object neu, string name) => (neu, name) switch
    {
        (HumanScaleModel, nameof(HumanScaleModel.RegenColour)) => "RegenBrush",
        (HumanScaleModel, nameof(HumanScaleModel.TwistColour)) => "TwistBrush",
        (TwistRegionRowModel, nameof(TwistRegionRowModel.BarColour)) => "Bar",
        (TwistMachineRowModel, nameof(TwistMachineRowModel.BarColour)) => "Bar",
        (NesoRp1Model, nameof(NesoRp1Model.PurchasedColour)) => "PurchasedBrush",
        (NesoRp1Model, nameof(NesoRp1Model.UnitColour)) => "UnitBrush",
        (NesoRp1Model, nameof(NesoRp1Model.GroupColour)) => "GroupBrush",
        (NesoRp1Model, nameof(NesoRp1Model.LfddColour)) => "LfddBrush",
        (NesoRp1Model, nameof(NesoRp1Model.IsPageVisible)) => "PageVisibility",
        (NesoRp1Model, nameof(NesoRp1Model.IsNotApplicableVisible)) => "NotApplicableVisibility",
        (Rp1InertiaRowModel, nameof(Rp1InertiaRowModel.SwatchColour)) => "Swatch",
        (Rp1GenRowModel, nameof(Rp1GenRowModel.SwatchColour)) => "Swatch",
        (Rp1GenRowModel, nameof(Rp1GenRowModel.KindColour)) => "KindBrush",
        (Rp1BoundaryRowModel, nameof(Rp1BoundaryRowModel.LoadingColour)) => "LoadingBrush",
        (Rp1BoundaryRowModel, nameof(Rp1BoundaryRowModel.TrendColour)) => "TrendBrush",
        (Rp1ConstraintRowModel, nameof(Rp1ConstraintRowModel.BindingColour)) => "BindingBrush",
        (Rp1BmRowModel, nameof(Rp1BmRowModel.DirColour)) => "DirBrush",
        (Rp1IcRowModel, nameof(Rp1IcRowModel.FlowColour)) => "FlowBrush",
        (Rp1IcRowModel, nameof(Rp1IcRowModel.FlagColour)) => "FlagBrush",
        (ExperimentalModel, nameof(ExperimentalModel.GateColour)) => "GateBrush",
        (LawRowModel, nameof(LawRowModel.StatusColour)) => "StatusBrush",
        _ => name,
    };

    /// <summary>
    /// Old properties with no neutral property twin by design: the NESO RP1 view-model's three WPF commands and its WPF
    /// InspectorVm, which stay on the WPF adapter over the neutral InspectFuel / InspectBm / InspectIc and the pages they
    /// raise (AssertDrillDowns drives the commands on the old side and compares every page they show).
    /// </summary>
    private static readonly HashSet<string> OldOnly = new(StringComparer.Ordinal) { "Inspector", "InspectFuelCommand", "InspectBmCommand", "InspectIcCommand" };

    private static void AssertSame(object old, object neu, string at)
    {
        PropertyInfo[] neuProps = neu.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo[] oldProps = old.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var oldByName = oldProps.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo np in neuProps)
        {
            string oldName = OldName(neu, np.Name);
            Assert.True(oldByName.TryGetValue(oldName, out PropertyInfo? op), $"{at}: the old view-model has no {oldName} for {np.Name}");
            covered.Add(oldName);
            SameValue(op!.GetValue(old), np.GetValue(neu), $"{at}: {np.Name}");
        }
        Assert.Equal(oldProps.Select(p => p.Name).Where(n => !OldOnly.Contains(n)).OrderBy(s => s, StringComparer.Ordinal),
            covered.OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>Two neutral models of the same type, every public property (for the async-equals-inline check).</summary>
    private static void AssertSameModel(object want, object got, string at)
    {
        foreach (PropertyInfo p in want.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? w = p.GetValue(want), g = p.GetValue(got);
            if (w is IList wl)
            {
                var gl = Assert.IsAssignableFrom<IList>(g);
                Assert.True(wl.Count == gl.Count, $"{at}: {p.Name} want {wl.Count} rows, got {gl.Count}");
                for (int i = 0; i < wl.Count; i++)
                    foreach (PropertyInfo rp in wl[i]!.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        Assert.True(Equals(rp.GetValue(wl[i]), rp.GetValue(gl[i])), $"{at}: {p.Name}[{i}].{rp.Name}");
            }
            else Assert.True(Equals(w, g), $"{at}: {p.Name} want {w}, got {g}");
        }
    }

    private static void SameText(string? want, string? got, string what) =>
        Assert.True(string.Equals(want, got, StringComparison.Ordinal), $"{what}: want \"{want}\", got \"{got}\"");

    /// <summary>A colour token against the colour of the brush the old code chose.</summary>
    private static void SameColour(object? oldBrush, UiColour c, string what)
    {
        var brush = Assert.IsType<Old.SolidColorBrush>(oldBrush);
        Assert.True(brush.Color.Token == c, $"{what}: want {ColourRamps.ToHex(brush.Color.Token)}, got {ColourRamps.ToHex(c)}");
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
                SameColour(ov, c, what);
                return;
            case bool b when ov is Old.Visibility vis:
                Assert.True((b ? Old.Visibility.Visible : Old.Visibility.Collapsed) == vis, $"{what}: want {vis}, got {b}");
                return;
            case bool b:
                Assert.True(ov is bool ob && ob == b, $"{what}: want {ov}, got {b}");
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

    /// <summary>One row of a list (a number, a string, or a row object, every public property compared).</summary>
    private static void SameRow(object? orow, object? nrow, string what)
    {
        if (nrow is string or double or null) { SameValue(orow, nrow, what); return; }
        Assert.NotNull(orow);
        PropertyInfo[] np = nrow.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo[] op = orow!.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var oldByName = op.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo p in np)
        {
            if (p.GetIndexParameters().Length > 0) continue;
            string oldName = OldName(nrow, p.Name);
            Assert.True(oldByName.TryGetValue(oldName, out PropertyInfo? o), $"{what}: the old row has no {oldName}");
            covered.Add(oldName);
            SameValue(o!.GetValue(orow), p.GetValue(nrow), $"{what}.{p.Name}");
        }
        Assert.Equal(op.Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal),
            covered.OrderBy(s => s, StringComparer.Ordinal));
    }
}
