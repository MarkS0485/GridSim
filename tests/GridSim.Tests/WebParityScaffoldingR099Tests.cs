using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Components.Assets;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R099 (October 2026 review): the web parity scaffolding that was left half-wired. These tests pin what was
/// finished: the SPEED DOMAINS status sentence, the EXPERIMENTAL wave-degraded and cyclic-trace flags, the MARKET
/// imbalance-price history, the SCADA stamp and click-through routing key, the HARMONICS source note that states
/// the single-synthetic-source limitation, and the removal of the never-set FrameEnvelope.Assets slot.
/// </summary>
public class WebParityScaffoldingR099Tests
{
    private static (GridModel Model, PowerFlowResult Result) Solve(GridModel m)
    {
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(m);
        return (solver.RegulatedModel, r);
    }

    private static SimFrame Frame(GridModel m, PowerFlowResult r) =>
        new(m, r, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: m.Generators.Sum(g => g.Pg), DemandMw: m.Buses.Sum(b => b.Pd));

    // ---- FrameEnvelope.Assets: removed, not left always-null ------------------------------------------------

    [Fact]
    public void FrameEnvelope_no_longer_carries_the_never_set_Assets_slot()
    {
        Assert.Null(typeof(FrameEnvelope).GetProperty("Assets"));
        Assert.Null(typeof(FrameEnvelope).Assembly.GetType("GridSim.Web.Projection.AssetsDto"));
    }

    // ---- SPEED DOMAINS status sentence ---------------------------------------------------------------------

    [Fact]
    public void Domains_status_sentence_matches_the_WPF_wording()
    {
        Assert.Equal("3 speed domains, ranked by apparent-power stiffness GVad = sum(S*C).", TabProjections.DomainsStatusText(3));
        Assert.Equal("2 speed domains, ranked by apparent-power stiffness GVad = sum(S*C).", TabProjections.DomainsStatusText(2));
        Assert.Equal("Single speed domain - equivalent to the lumped model.", TabProjections.DomainsStatusText(1));
        Assert.Equal("Single speed domain - equivalent to the lumped model.", TabProjections.DomainsStatusText(0));
    }

    [Fact]
    public void Domains_projection_carries_the_status_sentence_for_its_own_domain_count()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        DomainsDto d = TabProjections.Domains(f);
        Assert.Equal(TabProjections.DomainsStatusText(d.DomainCount), d.StatusText);
    }

    // ---- EXPERIMENTAL: wave degraded flag and cyclic-trace status ------------------------------------------

    [Fact]
    public void Wave_degraded_flag_is_false_when_the_wave_model_forms()
    {
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case9());
        Assert.True(FrameProjector.WaveModelCanForm(m, r));
        ExperimentalDto x = FrameProjector.Project(Frame(m, r)).Experimental!;
        Assert.False(x.WaveDegraded);
        Assert.NotEqual("needs per-machine inertia", x.WaveSplit);
    }

    [Fact]
    public void Wave_degraded_flag_is_true_when_the_case_has_no_per_machine_inertia()
    {
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case14());   // MATPOWER: loader leaves H = 0
        Assert.False(FrameProjector.WaveModelCanForm(m, r));
        ExperimentalDto x = FrameProjector.Project(Frame(m, r)).Experimental!;
        Assert.True(x.WaveDegraded);
        Assert.Equal("needs per-machine inertia", x.WaveSplit);   // unchanged split text (WaveModelGuardTests)
        Assert.Contains("H > 0", ExperimentalDto.WaveDegradedNote);
    }

    [Fact]
    public void Cyclic_trace_flag_mirrors_the_Core_trace_result()
    {
        foreach (GridModel c in new[] { IeeeCases.Case9(), IeeeCases.Case14() })
        {
            (GridModel m, PowerFlowResult r) = Solve(c);
            Assert.True(m.BusCount <= PresentationLimits.HeavyModelBusLimit);   // tracing runs
            bool acyclic = PowerFlowTracing.Trace(m, r).FlowGraphAcyclic;
            ExperimentalDto x = FrameProjector.Project(Frame(m, r)).Experimental!;
            Assert.Equal(!acyclic, x.FlowGraphCyclic);
            if (x.FlowGraphCyclic) Assert.Empty(x.TopFlows);
        }
    }

    [Fact]
    public void Trace_empty_text_tells_a_cycle_apart_from_an_empty_result()
    {
        Assert.Equal("Flow graph has a cycle - proportional trace not defined.", ExperimentalDto.TraceEmptyText(true));
        Assert.Equal("No dominant traced flows on this frame.", ExperimentalDto.TraceEmptyText(false));
    }

    // ---- MARKET imbalance-price history --------------------------------------------------------------------

    [Fact]
    public void Imbalance_price_history_appends_finite_prices_and_caps_at_96()
    {
        Assert.Equal(96, TabProjections.ImbalancePriceHistoryLength);
        var q = new Queue<double>();
        Assert.False(TabProjections.AppendImbalancePrice(q, null));
        Assert.False(TabProjections.AppendImbalancePrice(q, double.NaN));
        Assert.False(TabProjections.AppendImbalancePrice(q, double.PositiveInfinity));
        Assert.Empty(q);

        for (int i = 0; i < 100; i++) Assert.True(TabProjections.AppendImbalancePrice(q, i));
        Assert.Equal(96, q.Count);
        Assert.Equal(4.0, q.First());    // oldest four dropped
        Assert.Equal(99.0, q.Last());
        Assert.True(TabProjections.AppendImbalancePrice(q, -12.5));   // negative prices are real (paid to generate)
        Assert.Equal(-12.5, q.Last());
    }

    [Fact]
    public void Imbalance_price_history_is_attached_to_the_lake_block_as_a_copy()
    {
        var lake = new MarketLakeDto(50, 49, 48, 0, 47, 3, 0, 120, 30000);
        var withLake = new MarketDto(40, 0.2, 30000, 10000, 12000, Array.Empty<MktInterconnectorRowDto>(), lake);
        var noLake = withLake with { Lake = null };
        var q = new Queue<double>();

        Assert.Same(withLake, TabProjections.WithImbalancePriceHistory(withLake, q));   // no history yet
        q.Enqueue(50); q.Enqueue(60);
        Assert.Same(noLake, TabProjections.WithImbalancePriceHistory(noLake, q));       // no market feed

        MarketDto m = TabProjections.WithImbalancePriceHistory(withLake, q);
        Assert.Equal(new[] { 50.0, 60.0 }, m.Lake!.ImbalancePriceHistory);
        q.Enqueue(70);   // a later append must not reach a DTO already handed out
        Assert.Equal(2, m.Lake.ImbalancePriceHistory!.Count);
        Assert.Null(lake.ImbalancePriceHistory);   // the default is "no history"
    }

    // ---- SCADA stamp + click-through routing key ----------------------------------------------------------

    [Fact]
    public void Scada_points_carry_the_frame_stamp_and_their_asset_page()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        ScadaDto d = new ScadaService().Project(f);
        string stamp = FrameProjector.FormatClock(f.Hour);
        var busIds = f.Model.Buses.Select(b => b.Id).ToHashSet();

        Assert.NotEmpty(d.Points);
        foreach (ScadaPointDto p in d.Points)
        {
            Assert.Equal(stamp, p.Stamp);
            if (p.Tag == "SYS.FREQ")
            {
                Assert.Equal(ScadaTarget.None, p.Target);
                Assert.Null(AssetRoute.FromScada(p.Target, p.TargetKey));
            }
            else if (p.Tag.EndsWith(".V", StringComparison.Ordinal))
            {
                Assert.Equal(ScadaTarget.Substation, p.Target);
                Assert.Contains(p.TargetKey, busIds);
                Assert.Equal(new AssetRoute(AssetPage.SubDetail, p.TargetKey), AssetRoute.FromScada(p.Target, p.TargetKey));
            }
            else if (p.Tag.EndsWith(".P", StringComparison.Ordinal) || p.Tag.EndsWith(".Q", StringComparison.Ordinal))
            {
                Assert.Equal(ScadaTarget.Circuit, p.Target);
                Assert.InRange(p.TargetKey, 0, f.Model.Branches.Count - 1);
                Assert.False(f.Model.Branches[p.TargetKey].IsTransformer);
                Assert.Equal(new AssetRoute(AssetPage.CktDetail, p.TargetKey), AssetRoute.FromScada(p.Target, p.TargetKey));
            }
        }
        Assert.Contains(d.Points, p => p.Target == ScadaTarget.Substation);
        Assert.Contains(d.Points, p => p.Target == ScadaTarget.Circuit);
    }

    [Fact]
    public void Scada_route_mapping_covers_transformers_and_rejects_bad_keys()
    {
        Assert.Equal(new AssetRoute(AssetPage.TxDetail, 3), AssetRoute.FromScada(ScadaTarget.Transformer, 3));
        Assert.Null(AssetRoute.FromScada(ScadaTarget.Transformer, -1));
        Assert.Null(AssetRoute.FromScada(ScadaTarget.Circuit, -1));
        Assert.Null(AssetRoute.FromScada(ScadaTarget.None, 5));
    }

    // ---- HARMONICS: the single-synthetic-source limitation is stated, not hidden ---------------------------

    [Fact]
    public void Harmonics_study_names_its_single_synthetic_source_and_the_per_converter_limitation()
    {
        GridModel case9 = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(case9);
        HarmonicsDto dto = SimSession.StudyHarmonics(case9, r);

        int largestDemandBus = case9.Buses.OrderByDescending(b => b.Pd).First().Id;
        Assert.False(dto.HeavyModelsSkipped);
        Assert.NotNull(dto.SourceNote);
        // Moved 2026-10 (N131, Ruling R1, Mark delegated 2026-10-04): the default synthetic source is the G5 Issue 5
        // Table B.3 six-pulse profile; the note names its library type. Before: SyntheticSourceNote("6-pulse", ...);
        // after: SyntheticSourceNote("6-pulse-g5", ...).
        Assert.Equal(HarmonicsDto.SyntheticSourceNote("6-pulse-g5", 0.5, largestDemandBus), dto.SourceNote);
        Assert.Contains($"at bus {largestDemandBus} ", dto.SourceNote);
        Assert.EndsWith(HarmonicsDto.PerConverterLimitation, dto.SourceNote);
        Assert.Contains("NOT modelled", HarmonicsDto.PerConverterLimitation);
    }

    [Fact]
    public void Harmonics_per_frame_stub_and_failures_carry_no_source_note()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        HarmonicsDto stub = HarmonicsProjection.Project(f.Model, f.Result);
        Assert.True(stub.HeavyModelsSkipped);
        Assert.Null(stub.SourceNote);
        Assert.Null(HarmonicsDto.Failure("engine said no").SourceNote);
    }
}
