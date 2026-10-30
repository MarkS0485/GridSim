using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Scheduling;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Tabs.Cycle;
using GridSim.Ui.Tabs.Domains;
using GridSim.Ui.Tabs.Market;
using GridSim.Ui.Tabs.Operator;
using GridSim.Ui.Tabs.Scada;
using Xunit;
using Old = GridSim.Tests.Ui.OldTabs;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 1.6 (first tab batch): the logic of the OPERATOR, MARKET, SCADA, SPEED DOMAINS and CYCLE tabs moved
/// from the WPF view-models into the platform-neutral GridSim.Ui.Tabs models. That is an orchestration-only move, so
/// every value each tab shows must be exactly what the WPF view-model showed before. These tests feed the same frames
/// (from <see cref="SimHost"/>, case9 and the GB reduced case, with the dashboard's own clock) and the same calls to each
/// new model and to a verbatim copy of the old WPF code (the OldTabs folder), then compare EVERY public property of the
/// new model, and every row of every list, with exact equality: strings ordinal, doubles by their bits, colour tokens
/// against the colour of the brush the old code chose, bools against the old Visibility. A property added to either side
/// later fails the coverage check until it is compared here.
/// </summary>
public sealed class UiTabsParityTests
{
    private const int Ticks = 12;

    public static TheoryData<string> Cases => new() { "case9", "uk" };

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

    /// <summary>A copy of <paramref name="r"/> with its converged flag set, every voltage scaled and every loading scaled.</summary>
    private static PowerFlowResult Copy(PowerFlowResult r, bool converged, double vmScale, double loadingScale = 1.0) => new()
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
        LineFlows = r.LineFlows.Select(l => l with { LoadingPercent = l.LoadingPercent * loadingScale }).ToList(),
    };

    // ---------------------------------------------------------------------------------------------------------------
    // OPERATOR
    // ---------------------------------------------------------------------------------------------------------------

    private static IEnumerable<BridgeProtocol.OperatorBlock?> OperatorBlocks()
    {
        yield return null;
        yield return new BridgeProtocol.OperatorBlock();
        yield return new BridgeProtocol.OperatorBlock
        {
            ResponseMw = 1234.567, ReserveMw = 3456.78, DeratedMarginMw = 4321.0, NetInterconnectorMw = -2345.6,
            LargestLossMw = 1800, Warning = "  NESO \\n system warning\nline two\r\n  with   gaps  ",
            BalancingActions = new()
            {
                new() { BmUnit = "T_DRAXX-1", Kind = "OFFER", LevelMw = 412.6, PriceGbpMwh = 87.125 },
                new() { BmUnit = null, Kind = "bid", LevelMw = null, PriceGbpMwh = null },
                new() { BmUnit = "E_WIND-2", Kind = null, LevelMw = -88.5, PriceGbpMwh = -12.5 },
            },
            Constraints = new()
            {
                new() { Name = "B6 SCOTEX", FlowMw = 5432.1, LimitMw = 6000, CostGbp = 123_456, Binding = true },
                new() { Name = null, FlowMw = null, LimitMw = 950, CostGbp = null, Binding = null },
            },
            Warnings = new() { "NRAPM issued", "Low frequency demand disconnection armed" },
        };
        yield return new BridgeProtocol.OperatorBlock { Warning = new string('W', 400), NetInterconnectorMw = 0 };
        yield return new BridgeProtocol.OperatorBlock
        {
            Warning = "",
            BalancingActions = Enumerable.Range(0, 55).Select(i => new BridgeProtocol.BalancingActionDto { BmUnit = $"U{i}", Kind = i % 2 == 0 ? "BID" : "OFFER", LevelMw = i * 10.25, PriceGbpMwh = i }).ToList(),
            Constraints = Enumerable.Range(0, 40).Select(i => new BridgeProtocol.ConstraintDto { Name = $"C{i}", FlowMw = i * 100.5, LimitMw = 2000, CostGbp = i * 999.0, Binding = i % 3 == 0 }).ToList(),
            Warnings = Enumerable.Range(0, 25).Select(i => $"warning {i}").ToList(),
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Operator_every_displayed_value_equals_the_old_wpf_view_model_frame_for_frame(string name)
    {
        SimHost host = Host(name);
        var dash = new DashboardModel();
        var neu = new OperatorModel();
        var old = new Old.OperatorVm();
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
                    foreach (BridgeProtocol.OperatorBlock? op in OperatorBlocks())
                    {
                        neu.SetOperatorData(op, clock); old.SetOperatorData(op, clock);
                        AssertSame(old, neu, $"{name} tick {tick} operator block {k++}");
                    }
                    break;
                case 4:
                    // The frequency colour at the band edges about the model's own nominal, and non-finite readings.
                    double nominal = f.Model.FrequencyHz;
                    foreach (double hz in new[] { nominal + 0.2, nominal - 0.2, nominal + 0.2000001, nominal + 0.1, nominal - 0.1000001, nominal + 0.15, double.NaN, double.NegativeInfinity, nominal })
                    {
                        SimFrame g = f with { FrequencyHz = hz, RocofHzPerS = -0.1234 };
                        neu.Update(g, hf.Boundaries, clock); old.Update(g, hf.Boundaries, clock);
                        AssertSame(old, neu, $"{name} tick {tick} at {hz:R} Hz");
                    }
                    break;
                case 6:
                    // Boundaries grow, shrink and empty; a frame with no voltages.
                    var more = new List<BoundaryFlow>(hf.Boundaries) { new("TEST CUT", -5_432.1, 4_000, 135.8025, true), new("IDLE CUT", 0, 0, 0, false), new("SMALL CUT", 999.9, 999.99, 99.99, false) };
                    neu.Update(f, more, clock); old.Update(f, more, clock);
                    AssertSame(old, neu, $"{name} tick {tick} more boundaries");
                    neu.Update(f, Array.Empty<BoundaryFlow>(), clock); old.Update(f, Array.Empty<BoundaryFlow>(), clock);
                    AssertSame(old, neu, $"{name} tick {tick} no boundaries");
                    SimFrame bad = f with { Result = Copy(f.Result, converged: false, vmScale: 1.07) };
                    neu.Update(bad, hf.Boundaries, clock); old.Update(bad, hf.Boundaries, clock);
                    AssertSame(old, neu, $"{name} tick {tick} not converged");
                    break;
                case 8:
                    neu.Reset(); old.Reset();
                    AssertSame(old, neu, $"{name} tick {tick} reset");
                    break;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // MARKET
    // ---------------------------------------------------------------------------------------------------------------

    private static IEnumerable<BridgeProtocol.MarketBlock?> MarketBlocks()
    {
        yield return null;
        yield return new BridgeProtocol.MarketBlock();
        yield return new BridgeProtocol.MarketBlock
        {
            ImbalancePriceGbpMwh = 72.345, SystemSellPriceGbpMwh = 70.1, MarketIndexGbpMwh = 68.999, DayAheadGbpMwh = 65.5,
            BsuosGbpMwh = 12.345, ConstraintCostGbp = 1_234_567.8, NetImbalanceVolumeMwh = -123.5, CarbonIntensityGco2PerKwh = 99.9,
            DemandForecastMw = 31_234.5,
            Interconnectors = new()
            {
                new() { Name = "IFA", FlowMw = 1999.5, PriceGbpMwh = 80.25 },
                new() { Name = null, FlowMw = null, PriceGbpMwh = null },
                new() { Name = "NSL", FlowMw = -1400.25 },
            },
        };
        yield return new BridgeProtocol.MarketBlock { ImbalancePriceGbpMwh = -5.5, ConstraintCostGbp = 4_567.0, NetImbalanceVolumeMwh = 0, CarbonIntensityGco2PerKwh = 100 };
        yield return new BridgeProtocol.MarketBlock { ImbalancePriceGbpMwh = 81, ConstraintCostGbp = 999.4, NetImbalanceVolumeMwh = 45.5, CarbonIntensityGco2PerKwh = 250, Interconnectors = new() };
        yield return new BridgeProtocol.MarketBlock { ImbalancePriceGbpMwh = 150.0001, CarbonIntensityGco2PerKwh = 249.99 };
        yield return new BridgeProtocol.MarketBlock { ImbalancePriceGbpMwh = 150 };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Market_every_displayed_value_equals_the_old_wpf_view_model_frame_for_frame(string name)
    {
        SimHost host = Host(name);
        var dash = new DashboardModel();
        var neu = new MarketModel();
        var old = new Old.MarketVm();
        AssertSame(old, neu, $"{name} before the first frame");
        var t0 = new DateTime(2026, 7, 1, 11, 50, 0, DateTimeKind.Utc);
        for (int tick = 1; tick <= Ticks; tick++)
        {
            (HostFrame hf, string clock) = Next(host, dash);
            SimFrame f = hf.Frame!;
            neu.Update(f, clock); old.Update(f, clock);
            AssertSame(old, neu, $"{name} tick {tick}");
            switch (tick)
            {
                case 2:
                    int k = 0;
                    foreach (BridgeProtocol.MarketBlock? mk in MarketBlocks())
                    {
                        neu.SetMarketData(mk); old.SetMarketData(mk);
                        AssertSame(old, neu, $"{name} tick {tick} market block {k++} (no tick time)");
                    }
                    break;
                case 3:
                    // Dated ticks: one price trace sample per settlement period, across a day and past the history length.
                    for (int s = 0; s < 120; s++)
                    {
                        var mk = new BridgeProtocol.MarketBlock { ImbalancePriceGbpMwh = 40 + (s * 7 % 97) + 0.125 };
                        DateTime at = t0.AddMinutes(s * 17);
                        neu.SetMarketData(mk, at); old.SetMarketData(mk, at);
                        AssertSame(old, neu, $"{name} tick {tick} dated sample {s}");
                    }
                    break;
                case 5:
                    neu.Reset(); old.Reset();
                    AssertSame(old, neu, $"{name} tick {tick} reset");
                    break;
                case 7:
                    foreach (BridgeProtocol.MarketBlock? mk in MarketBlocks())
                    {
                        neu.SetMarketData(mk, t0); old.SetMarketData(mk, t0);
                        AssertSame(old, neu, $"{name} tick {tick} market block at one instant");
                    }
                    break;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // SCADA
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void Scada_every_displayed_value_equals_the_old_wpf_view_model_frame_for_frame(string name)
    {
        SimHost host = Host(name);
        var dash = new DashboardModel();
        var neu = new ScadaModel();
        var old = new Old.ScadaVm();
        AssertSame(old, neu, $"{name} before the first frame");
        for (int tick = 1; tick <= Ticks; tick++)
        {
            (HostFrame hf, string clock) = Next(host, dash);
            SimFrame f = hf.Frame!;
            neu.Update(f, clock); old.Update(f, clock);
            AssertSame(old, neu, $"{name} tick {tick}");
            switch (tick)
            {
                case 2:
                    // The filter: matches by tag, by description, none, whitespace, then cleared.
                    foreach (string filter in new[] { "V_PU", "  power flow ", "zzz-no-match", "   ", "SYS", "" })
                    {
                        neu.Filter = filter; old.Filter = filter;
                        AssertSame(old, neu, $"{name} tick {tick} filter '{filter}'");
                        neu.Update(f, clock); old.Update(f, clock);
                        AssertSame(old, neu, $"{name} tick {tick} filter '{filter}' then a frame");
                    }
                    break;
                case 3:
                    // Excursions: not converged, voltage out of band, frequency outside the alarm band, hot corridors, then
                    // all clear (each logs once on its onset and once on recovery).
                    SimFrame bad = f with { Result = Copy(f.Result, converged: false, vmScale: 1.0) };
                    neu.Update(bad, clock); old.Update(bad, clock);
                    AssertSame(old, neu, $"{name} tick {tick} not converged");
                    neu.Update(bad, clock); old.Update(bad, clock);
                    AssertSame(old, neu, $"{name} tick {tick} still not converged");
                    SimFrame high = f with { Result = Copy(f.Result, converged: true, vmScale: 1.09, loadingScale: 3.0), FrequencyHz = f.Model.FrequencyHz - 0.25 };
                    neu.Update(high, clock); old.Update(high, clock);
                    AssertSame(old, neu, $"{name} tick {tick} voltage, frequency and corridor excursions");
                    SimFrame warm = f with { Result = Copy(f.Result, converged: true, vmScale: 0.9, loadingScale: 1.6), FrequencyHz = f.Model.FrequencyHz + 0.15 };
                    neu.Update(warm, clock); old.Update(warm, clock);
                    AssertSame(old, neu, $"{name} tick {tick} low voltage, high frequency, watch corridors");
                    neu.Update(f, clock); old.Update(f, clock);
                    AssertSame(old, neu, $"{name} tick {tick} recovered");
                    break;
                case 4:
                    // Acknowledge the newest, the oldest, then everything.
                    neu.Ack(neu.Events[0]); Ack(old, 0);
                    AssertSame(old, neu, $"{name} tick {tick} ack the newest");
                    neu.Ack(neu.Events[^1]); Ack(old, old.Events.Count - 1);
                    AssertSame(old, neu, $"{name} tick {tick} ack the oldest");
                    neu.AckAll(); old.AckAllCmd.Execute(null);
                    AssertSame(old, neu, $"{name} tick {tick} ack all");
                    break;
                case 6:
                    // A tap ratio that moves (an OLTC event), when the case has an OLTC point.
                    GridModel moved = f.Model.CopyWith(branches: f.Model.Branches.Select(b => b.IsTransformer ? b with { TapRatio = b.TapRatio + 0.0125 } : b).ToList());
                    SimFrame tapped = f with { Model = moved };
                    neu.Update(tapped, clock); old.Update(tapped, clock);
                    AssertSame(old, neu, $"{name} tick {tick} taps moved");
                    break;
                case 8:
                    neu.ForgetModel(); old.ForgetModel();
                    AssertSame(old, neu, $"{name} tick {tick} model forgotten");
                    break;
                case 10:
                    // Over the 200-event cap: alternate in and out of the voltage band.
                    for (int i = 0; i < 120; i++)
                    {
                        SimFrame g = i % 2 == 0 ? f with { Result = Copy(f.Result, converged: true, vmScale: 1.2) } : f;
                        neu.Update(g, clock); old.Update(g, clock);
                    }
                    AssertSame(old, neu, $"{name} tick {tick} event log capped");
                    break;
            }
        }
    }

    // The old per-row ACK, through its command, on the old row at index i.
    private static void Ack(Old.ScadaVm old, int i) => old.AckCmd.Execute(old.Events[i]);

    [Fact]
    public void Scada_click_through_hands_the_point_to_the_host_as_the_old_command_did()
    {
        SimHost host = Host("case9");
        var dash = new DashboardModel();
        var neu = new ScadaModel();
        var old = new Old.ScadaVm();
        (HostFrame hf, string clock) = Next(host, dash);
        neu.Update(hf.Frame!, clock); old.Update(hf.Frame!, clock);
        var gotNew = new List<ScadaPointModel>();
        var gotOld = new List<Old.ScadaPoint>();
        // No host wired: nothing happens (and nothing throws) on either side.
        neu.OpenPoint(neu.Points[0]); old.OpenPointCmd.Execute(old.Points[0]);
        neu.Navigate = gotNew.Add; old.Navigate = gotOld.Add;
        for (int i = 0; i < neu.Points.Count; i++) { neu.OpenPoint(neu.Points[i]); old.OpenPointCmd.Execute(old.Points[i]); }
        old.OpenPointCmd.Execute("not a point");   // the old command ignores anything else
        Assert.Equal(gotOld.Count, gotNew.Count);
        for (int i = 0; i < gotNew.Count; i++) SameRow(gotOld[i], gotNew[i], $"opened point {i}");
        Assert.Throws<ArgumentNullException>(() => neu.OpenPoint(null!));
        Assert.Throws<ArgumentNullException>(() => neu.Ack(null!));
    }

    [Fact]
    public void Scada_rebuilds_the_register_on_a_switch_between_two_cases_exactly_as_before()
    {
        var neu = new ScadaModel();
        var old = new Old.ScadaVm();
        foreach (string name in new[] { "case9", "uk", "case9" })
        {
            SimHost host = Host(name);
            var dash = new DashboardModel();
            neu.ForgetModel(); old.ForgetModel();
            for (int tick = 1; tick <= 4; tick++)
            {
                (HostFrame hf, string clock) = Next(host, dash);
                neu.Update(hf.Frame!, clock); old.Update(hf.Frame!, clock);
                AssertSame(old, neu, $"{name} tick {tick} after a switch");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // SPEED DOMAINS
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void Domains_every_displayed_value_equals_the_old_wpf_view_model_frame_for_frame(string name)
    {
        SimHost host = Host(name);
        var dash = new DashboardModel();
        var neu = new DomainsModel();
        var old = new Old.DomainsVm();
        AssertSame(old, neu, $"{name} before the first frame");
        for (int tick = 1; tick <= Ticks; tick++)
        {
            (HostFrame hf, string clock) = Next(host, dash);
            neu.Update(hf.Frame!); old.Update(hf.Frame!);
            AssertSame(old, neu, $"{name} tick {tick}");
            // The tab gate: shown on tick 3 (catches up on the remembered frame at once), hidden on 7, shown again on 9.
            if (tick is 3 or 9) { neu.Active = true; old.Active = true; AssertSame(old, neu, $"{name} tick {tick} shown"); }
            if (tick == 7) { neu.Active = false; old.Active = false; AssertSame(old, neu, $"{name} tick {tick} hidden"); }
            if (tick == 5)
            {
                // Showing twice is a no-op; a frame with no model is ignored.
                neu.Active = true; old.Active = true;
                neu.Update(hf.Frame! with { Model = null! }); old.Update(hf.Frame! with { Model = null! });
                AssertSame(old, neu, $"{name} tick {tick} no model");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // CYCLE
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The old view-model's private service field and its UI-thread projection (the oracle's own code).</summary>
    private static void OldProject(Old.CycleVm old, ResidentCycleService? svc)
    {
        typeof(Old.CycleVm).GetField("_svc", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(old, svc);
        typeof(Old.CycleVm).GetMethod("ApplyLatest", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(old, null);
        typeof(Old.CycleVm).GetField("_svc", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(old, null);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Cycle_projects_a_completed_cycle_exactly_as_the_old_wpf_view_model(string name)
    {
        SimHost host = Host(name);
        var dash = new DashboardModel();
        var neu = new CycleModel(_ => throw new InvalidOperationException("not used: this test projects by hand"));
        var old = new Old.CycleVm();
        AssertSame(old, neu, $"{name} before the first cycle");

        // No service, and a service with no completed cycle: both say there is no data.
        neu.Project(null); OldProject(old, null);
        AssertSame(old, neu, $"{name} no service");

        for (int round = 1; round <= 3; round++)
        {
            (HostFrame hf, _) = Next(host, dash);
            using var svc = new ResidentCycleService(hf.Frame!.Model, new CycleOptions { Workers = 2 });
            neu.Project(svc); OldProject(old, svc);
            AssertSame(old, neu, $"{name} round {round} before a cycle completed");
            using var done = new ManualResetEventSlim();
            svc.Updated += done.Set;
            svc.Offer(hf.Frame!, round == 2 ? 0 : hf.SolveMs + 0.05 * round);
            Assert.True(done.Wait(TimeSpan.FromMinutes(5)), "a cycle must complete");
            Assert.Null(svc.LastError);
            Assert.NotNull(svc.Latest);
            // One frame was offered, so the finished cycle is the last: Latest and the coverage no longer change.
            neu.Project(svc); OldProject(old, svc);
            AssertSame(old, neu, $"{name} round {round} cycle {svc.Latest!.CycleIndex}");
        }
        neu.ForgetModel(); old.ForgetModel();
        AssertSame(old, neu, $"{name} model forgotten");
    }

    [Fact]
    public void Cycle_worst_contingency_and_job_rows_equal_the_old_code_for_every_outcome()
    {
        ContingencyResult Ok(string outage, int viol) => new(outage, true, 50, 40, 0.98, 1.02, Enumerable.Range(0, viol).Select(i => $"v{i}").ToList());
        ContingencyResult Fail(string outage, bool islanded, int viol) => new(outage, false, 0, 0, 0, 0, Enumerable.Range(0, viol).Select(i => $"v{i}").ToList()) { Islanded = islanded };
        JobResult Job(string id, JobPriority p, JobOutcome o, double ms, object? payload) => new(id, id.ToUpperInvariant(), p, o, ms, payload, false);
        var payloads = new List<IReadOnlyList<ContingencyResult>>
        {
            Array.Empty<ContingencyResult>(),
            new[] { Ok("L1", 0), Ok("L2", 1) },
            new[] { Ok("L1", 3), Ok("L2", 2), Ok("L3", 3) },
            new[] { Ok("L1", 4), Fail("L2", islanded: true, viol: 2), Fail("L3", islanded: false, viol: 9) },
            new[] { Fail("L4", islanded: false, viol: 0), Ok("L5", 7) },
        };
        foreach (IReadOnlyList<ContingencyResult> list in payloads)
        {
            var jobs = new List<JobResult>
            {
                Job("n-1:hot", JobPriority.High, JobOutcome.Completed, 12.5, list),
                Job("rocof", JobPriority.Low, JobOutcome.Cancelled, 30.25, null),
                Job("harm", JobPriority.High, JobOutcome.Faulted, 1.5, "not a list"),
                Job("conf", JobPriority.Low, JobOutcome.Deferred, 0, null),
                Job("n-1:lp:0", JobPriority.Low, JobOutcome.Completed, 99.5, list),
            };
            var r = new CycleResult(7, 3.25, 41.5, 2, 1, 3, 1, 1, 1, 400, 123.4, 2, false, jobs);
            Assert.Equal(Old.CycleVm.WorstContingency(r), CycleModel.WorstContingency(r));
        }
    }

    [Fact]
    public void Cycle_marshals_each_completed_cycle_through_the_front_ends_poster()
    {
        // The neutral model never projects on the worker thread: every completed cycle is handed to the poster, and the
        // projection runs only when the front end runs what it was handed (here, on the test thread).
        var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var neu = new CycleModel(posted.Enqueue);
        Assert.Throws<ArgumentNullException>(() => new CycleModel(null!));
        SimHost host = Host("case9");
        var dash = new DashboardModel();
        (HostFrame hf, _) = Next(host, dash);
        neu.Update(hf.Frame!, hf.SolveMs);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (posted.IsEmpty && clock.Elapsed < TimeSpan.FromMinutes(5)) Thread.Sleep(5);
        Assert.False(posted.IsEmpty, "a completed cycle must be posted");
        Assert.False(neu.HasData);   // nothing projected until the poster runs it
        Assert.Equal("WAITING FOR FIRST CYCLE", neu.Hero);
        Assert.True(posted.TryDequeue(out Action? apply));
        apply!();
        Assert.True(neu.HasData);
        Assert.StartsWith("HIGH TIER ", neu.Hero, StringComparison.Ordinal);
        neu.ForgetModel();
        Assert.False(neu.HasData);
        neu.Dispose();   // a second dispose is safe
    }

    [Fact]
    public void Cycle_starts_and_forgets_with_the_old_values()
    {
        var neu = new CycleModel(_ => { });
        var old = new Old.CycleVm();
        AssertSame(old, neu, "new");
        neu.ForgetModel(); old.ForgetModel();
        AssertSame(old, neu, "forgotten");
        neu.Dispose(); old.Dispose();
        AssertSame(old, neu, "disposed");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The WPF adapters (source contract: the WPF project is not loaded by the test host)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Each WPF adapter file, the neutral model its view-model derives from, and the old names it maps.</summary>
    public static TheoryData<string, string, string> Adapters => new()
    {
        { "Views/OperatorVm.cs", "public sealed class OperatorVm : OperatorModel", "FrequencyColour=FrequencyBrush;WarningColour=WarningBrush" },
        { "Views/MarketVm.cs", "public sealed class MarketVm : MarketModel", "ImbalanceColour=ImbalanceBrush;NivColour=NivBrush;CarbonColour=CarbonBrush" },
        { "Views/CycleVm.cs", "public sealed class CycleVm : CycleModel", "HeroColour=HeroBrush;HighColour=HighBrush;CoverageColour=CoverageBrush;WorstColour=WorstBrush" },
        { "Views/DomainsVm.cs", "public sealed class DomainsVm : DomainsModel", "" },
        { "Views/ScadaVm.cs", "public sealed class ScadaVm : ScadaModel", "" },
    };

    [Theory]
    [MemberData(nameof(Adapters))]
    public void Each_wpf_tab_derives_from_its_model_and_maps_every_token_to_its_old_brush(string file, string derives, string mapped)
    {
        string wpf = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", file);
        Assert.Contains(derives, wpf, StringComparison.Ordinal);
        foreach (string pair in mapped.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = pair.Split('=');
            Assert.Contains($"public Brush {p[1]} => Palette.Frozen({p[0]});", wpf, StringComparison.Ordinal);
            Assert.Contains($"[nameof({p[0]})] = nameof({p[1]}),", wpf, StringComparison.Ordinal);
        }
        // The adapter is thin: no frame logic of its own.
        Assert.DoesNotContain("SimFrame", wpf, StringComparison.Ordinal);
        Assert.DoesNotContain("ToString(\"F", wpf, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\$""\{"), wpf);
    }

    [Fact]
    public void The_wpf_visibility_adapters_turn_each_bool_back_into_the_old_visibility()
    {
        // Where a WPF Visibility became a bool, both are pinned: the bool in the model, and the adapter that the XAML binds.
        string op = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", "OperatorVm.cs");
        Assert.Contains("public Visibility WarningVisible => IsWarningVisible ? Visibility.Visible : Visibility.Collapsed;", op, StringComparison.Ordinal);
        Assert.Contains("[nameof(IsWarningVisible)] = nameof(WarningVisible),", op, StringComparison.Ordinal);
        string scada = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", "ScadaVm.cs");
        Assert.Contains("public Visibility Vis => Visible ? Visibility.Visible : Visibility.Collapsed;", scada, StringComparison.Ordinal);
        Assert.Contains("else if (name == nameof(Visible)) base.OnPropertyChanged(nameof(Vis));", scada, StringComparison.Ordinal);
        // The rows map their tokens too.
        foreach ((string f, string line) in new[]
                 {
                     ("OperatorVm.cs", "public Brush LoadingBrush => Palette.Frozen(LoadingColour);"),
                     ("OperatorVm.cs", "public Brush Swatch => Palette.Frozen(SwatchColour);"),
                     ("OperatorVm.cs", "public Brush KindBrush => Palette.Frozen(KindColour);"),
                     ("OperatorVm.cs", "public Brush BindingBrush => Palette.Frozen(BindingColour);"),
                     ("OperatorVm.cs", "public Brush Brush => Palette.Frozen(Colour);"),
                     ("MarketVm.cs", "public Brush FlowBrush => Palette.Frozen(FlowColour);"),
                     ("ScadaVm.cs", "public Brush QualityBrush => Palette.Frozen(QualityColour);"),
                     ("ScadaVm.cs", "public Brush LoadBrush => Palette.Frozen(LoadColour);"),
                     ("ScadaVm.cs", "public Brush PriorityBrush => Palette.Frozen(PriorityColour);"),
                     ("DomainsVm.cs", "public Brush DominantBrush => Palette.Frozen(DominantColour);"),
                     ("DomainsVm.cs", "public Brush Accent => Palette.Frozen(AccentColour);"),
                     ("CycleVm.cs", "public Brush OutcomeBrush => Palette.Frozen(OutcomeColour);"),
                     ("CycleVm.cs", "public Brush PriorityBrush => Palette.Frozen(PriorityColour);"),
                 })
            Assert.Contains(line, PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", f), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The comparison
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The old WPF property each neutral property stands in for, where it was renamed (token or bool).</summary>
    private static string OldName(object neu, string name) => (neu, name) switch
    {
        (OperatorModel, nameof(OperatorModel.FrequencyColour)) => "FrequencyBrush",
        (OperatorModel, nameof(OperatorModel.WarningColour)) => "WarningBrush",
        (OperatorModel, nameof(OperatorModel.IsWarningVisible)) => "WarningVisible",
        (OpBoundaryRowModel, nameof(OpBoundaryRowModel.LoadingColour)) => "LoadingBrush",
        (OpFuelRowModel, nameof(OpFuelRowModel.SwatchColour)) => "Swatch",
        (OpActionRowModel, nameof(OpActionRowModel.KindColour)) => "KindBrush",
        (OpConstraintRowModel, nameof(OpConstraintRowModel.BindingColour)) => "BindingBrush",
        (OpWarningRowModel, nameof(OpWarningRowModel.Colour)) => "Brush",
        (MarketModel, nameof(MarketModel.ImbalanceColour)) => "ImbalanceBrush",
        (MarketModel, nameof(MarketModel.NivColour)) => "NivBrush",
        (MarketModel, nameof(MarketModel.CarbonColour)) => "CarbonBrush",
        (MktInterconnectorRowModel, nameof(MktInterconnectorRowModel.FlowColour)) => "FlowBrush",
        (ScadaPointModel, nameof(ScadaPointModel.QualityColour)) => "QualityBrush",
        (ScadaPointModel, nameof(ScadaPointModel.LoadColour)) => "LoadBrush",
        (ScadaPointModel, nameof(ScadaPointModel.Visible)) => "Vis",
        (ScadaEventModel, nameof(ScadaEventModel.PriorityColour)) => "PriorityBrush",
        (DomainsModel, nameof(DomainsModel.DominantColour)) => "DominantBrush",
        (SpeedDomainRowModel, nameof(SpeedDomainRowModel.AccentColour)) => "Accent",
        (CycleModel, nameof(CycleModel.HeroColour)) => "HeroBrush",
        (CycleModel, nameof(CycleModel.HighColour)) => "HighBrush",
        (CycleModel, nameof(CycleModel.CoverageColour)) => "CoverageBrush",
        (CycleModel, nameof(CycleModel.WorstColour)) => "WorstBrush",
        (CycleJobRowModel, nameof(CycleJobRowModel.OutcomeColour)) => "OutcomeBrush",
        (CycleJobRowModel, nameof(CycleJobRowModel.PriorityColour)) => "PriorityBrush",
        _ => name,
    };

    /// <summary>
    /// Old properties with no neutral twin by design: the SCADA view-model's three WPF commands, which stay on the WPF
    /// adapter over the neutral AckAll / Ack / OpenPoint (Scada_click_through... and the ACK steps of the SCADA test drive
    /// them on the old side and compare the results).
    /// </summary>
    private static readonly HashSet<string> OldOnly = new(StringComparer.Ordinal) { "AckAllCmd", "AckCmd", "OpenPointCmd" };

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
                // The token must be the colour of the brush the old code chose.
                var brush = Assert.IsType<Old.SolidColorBrush>(ov);
                Assert.True(brush.Color.Token == c, $"{what}: want {ColourRamps.ToHex(brush.Color.Token)}, got {ColourRamps.ToHex(c)}");
                return;
            case bool b when ov is Old.Visibility vis:
                Assert.True((b ? Old.Visibility.Visible : Old.Visibility.Collapsed) == vis, $"{what}: want {vis}, got {b}");
                return;
            case bool b:
                Assert.True(ov is bool ob && ob == b, $"{what}: want {ov}, got {b}");
                return;
            case PointKind k:
                Assert.True(ov is Old.PointKind ok && ok.ToString() == k.ToString() && (int)ok == (int)k, $"{what}: want {ov}, got {k}");
                return;
            case Delegate:
                // The SCADA click-through hook: set by the host on both sides (compared by what it receives, elsewhere).
                Assert.IsAssignableFrom<Delegate>(ov);
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
