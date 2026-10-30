using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Evidence;
using GridSim.Core.Frequency;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// N317 (LF6; Ruling R1, Mark delegated EE calls 2026-10-04; ruled by main: a timing miss does not downgrade
/// "Reproduced", the verdict is on the value and the timing gap is printed beside it): the gb-2019-08-09 event model
/// carries the response the system had, from the record, through the existing response-service machinery, instead of the
/// generic governor (20 % of demand responsive, 5 % droop, 8 s lag) and 1 %/Hz load damping:
/// <list type="bullet">
/// <item>six services at the MW each DELIVERED (National Grid ESO interim report of 16 August 2019, docs/reference S192,
/// Table 3, PDF p.15: 266 + 231 + 165 + 198 + 200 + 30 = 1,090 MW), on the published delivery shapes and with lags of
/// the same mean delay as the documented release times (primary 10 s, secondary 30 s, static and EFR 1 s);</item>
/// <item>the recorded demand effect, 700 MW/Hz (S192 Tables 3 and 4 footnote: about 350 MW at 49.5 Hz for the event's
/// 29 GW demand), carried as <see cref="RecordedDemandEffect"/>;</item>
/// <item>the generic governor zeroed (<see cref="GridEvent.ResponseServicesComplete"/>), as the tables are the whole
/// low-frequency response held;</item>
/// <item>the distributed-generation RoCoF-protection trip at 1.0 s, the second the ESO timeline gives it (S192 Table 1,
/// PDF p.11: 16:52:34), not 3.0 s.</item>
/// </list>
/// Expected numbers below come from a double-precision replica of the swing kernel (same operations, same order; see the
/// LF6 notes), which reproduces the pre-N317 figures exactly (nadir 48.820307507181965 Hz at 14.06 s). They are checked
/// to 1e-9 and printed in full, to be confirmed at the final batch.
/// </summary>
public sealed partial class Lf6CalibrationBoundsTests
{
    private static readonly DateTime N317Now = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
    private readonly ITestOutputHelper? _out;

    /// <summary>xUnit injects the output helper; the parameterless N318 tests share the class.</summary>
    public Lf6CalibrationBoundsTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The 2019 event as it was before N317, for the tests that check a mechanism on the old trajectory (N237's timing
    /// note, R286's nearest-sample RMSE pin): no services, the generic governor kept, no demand effect, and the
    /// RoCoF-protection trip back at 3.0 s with its old text (same list position, so the event order is the old one).
    /// </summary>
    internal static GridEvent Pre2019N317(GridEvent ev)
    {
        Assert.Equal("gb-2019-08-09", ev.Id);
        var losses = ev.Losses.Select(l =>
            l.DeltaPMw == -390.0 && l.What.Contains("RoCoF-protection", StringComparison.Ordinal)
                ? l with { AtSeconds = 3.0, What = "distributed generation RoCoF-protection trip, 350-430 MW est. (Ofgem 2.4.6)" }
                : l).ToList();
        Assert.Single(losses, l => l.AtSeconds == 3.0);
        return ev with { Losses = losses, ResponseServices = null, ResponseServicesComplete = false, DemandEffect = null };
    }

    private static GridEvent Aug2019() => EventCatalog.Get("gb-2019-08-09", utcNow: N317Now);

    private static void Near(double expected, double actual, string what, double tol = 1e-9)
        => Assert.True(Math.Abs(expected - actual) <= tol, $"{what}: expected {expected:R}, got {actual:R}");

    [Fact]
    public void N317_the_2019_event_carries_the_delivered_services_and_the_demand_effect()
    {
        GridEvent ev = Aug2019();
        IReadOnlyList<ResponseService> s = ev.ResponseServices!;
        Assert.Equal(6, s.Count);
        // (MW delivered, deadband Hz, full-delivery deviation Hz, lag s), in catalogue order.
        (double Mw, double Db, double Full, double Tau)[] expected =
        {
            (266, 0.015, 0.5, 5.0),   // dynamic BM (mandatory)
            (231, 0.015, 0.5, 5.0),   // dynamic NBM (FFR)
            (165, 0.015, 0.5, 0.5),   // EFR
            (198, 0.3, 0.3, 15.0),    // static 49.7 Hz secondary
            (200, 0.4, 0.4, 0.5),     // static 49.6 Hz BM
            (30, 0.4, 0.4, 0.5),      // static 49.6 Hz NBM
        };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Mw, s[i].Mw);
            Assert.Equal(expected[i].Db, s[i].DeadbandHz);
            Assert.Equal(expected[i].Full, s[i].FullDeliveryDeviationHz);
            Assert.Equal(expected[i].Tau, s[i].TimeConstantS);
            Assert.Equal(0.0, s[i].KneeDeviationHz);
            Assert.Equal(0.0, s[i].KneeFraction);
            Assert.Equal(ProvenanceTag.Recorded, s[i].Provenance);
            Assert.Contains("S192 Table 3", s[i].What, StringComparison.Ordinal);
        }
        Assert.Equal(1090.0, s.Sum(x => x.Mw));   // the "Low Frequency Delivered" total of S192 Table 3
        Assert.True(ev.ResponseServicesComplete);
        Assert.NotNull(ev.DemandEffect);
        Assert.Equal(700.0, ev.DemandEffect!.MwPerHz);
        Assert.Equal(ProvenanceTag.Recorded, ev.DemandEffect.Provenance);
        // The RoCoF-protection trip is at 1.0 s now; the 5 s RoCoF-setting loss and the total are unchanged.
        PowerStep rocofTrip = Assert.Single(ev.Losses, l => l.What.Contains("RoCoF-protection", StringComparison.Ordinal));
        Assert.Equal(1.0, rocofTrip.AtSeconds);
        Assert.Equal(-390.0, rocofTrip.DeltaPMw);
        Assert.Equal(1521.0, ev.RocofSettingLossMw());
        Assert.Equal(2768.0, ev.TotalLossMw);
        // The other events gain nothing.
        Assert.Null(EventCatalog.Get("gb-2023-12-22", utcNow: N317Now).DemandEffect);
        Assert.Null(EventCatalog.Get("gb-largest-loss-1320", utcNow: N317Now).DemandEffect);
    }

    [Fact]
    public void N317_both_bundled_catalogues_carry_the_same_2019_record_as_the_built_in()
    {
        string events = Path.Combine(CliProcess.Root(), "data", "events");
        GridEvent builtIn = Aug2019();
        foreach (string file in new[] { "gb-events.json", "gb-2019-08-09-estimated.json" })
        {
            GridEvent fromFile = EventCatalog.Load(Path.Combine(events, file), N317Now).Single(e => e.Id == "gb-2019-08-09");
            Assert.Equal(builtIn.ResponseServices!.ToList(), fromFile.ResponseServices!.ToList());
            Assert.True(fromFile.ResponseServicesComplete, file);
            Assert.Equal(builtIn.DemandEffect, fromFile.DemandEffect);
            Assert.Equal(builtIn.Losses.Select(l => (l.AtSeconds, l.DeltaPMw)), fromFile.Losses.Select(l => (l.AtSeconds, l.DeltaPMw)));
        }
    }

    [Fact]
    public void N317_the_demand_effect_becomes_exactly_700_mw_per_hz_of_damping_and_the_governor_is_zeroed()
    {
        GridEvent ev = Aug2019();
        DynamicsConfig cfg = ev.ApplyRecordedServices(new DynamicsConfig());
        // 700 / 21,100 x 100, the IEEE double the kernel multiplies back by the same demand.
        Assert.Equal(700.0 / 21_100.0 * 100.0, cfg.DampingPctPerHz);
        Assert.Equal(3.3175355450236967, cfg.DampingPctPerHz);
        Assert.Equal(0.0, cfg.ResponsiveFraction);
        Assert.Equal(6, cfg.SystemResponseServices.Count);
        Assert.Null(cfg.InitialFrequencyHz);
        Assert.Equal(700.0, new SwingResponders(cfg, ev.PreEventDemandMw).DampingMwPerHz);   // exactly, bit for bit
        // The measured-operating-point catalogue (21,147.2 MW) lands on exactly 700 MW/Hz too.
        GridEvent est = Assert.Single(EventCatalog.Load(Path.Combine(CliProcess.Root(), "data", "events", "gb-2019-08-09-estimated.json"), N317Now));
        Assert.Equal(21_147.2, est.PreEventDemandMw);
        Assert.Equal(700.0, new SwingResponders(est.ApplyRecordedServices(new DynamicsConfig()), est.PreEventDemandMw).DampingMwPerHz);
    }

    [Fact]
    public void N317_an_event_without_a_demand_effect_keeps_the_configs_damping_bit_for_bit()
    {
        foreach (string id in new[] { "gb-2023-12-22", "gb-largest-loss-1320" })
        {
            GridEvent ev = EventCatalog.Get(id, utcNow: N317Now);
            foreach (double pct in new[] { 1.0, 0.7, 2.5 })
            {
                DynamicsConfig cfg = ev.ApplyRecordedServices(new DynamicsConfig { DampingPctPerHz = pct });
                Assert.Equal(BitConverter.DoubleToInt64Bits(pct), BitConverter.DoubleToInt64Bits(cfg.DampingPctPerHz));
            }
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-700.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void N317_a_demand_effect_that_is_not_a_positive_finite_figure_is_refused_by_name(double mwPerHz)
    {
        GridEvent bad = Aug2019() with { DemandEffect = new RecordedDemandEffect(mwPerHz, "test", ProvenanceTag.Synthetic) };
        string? problem = bad.DemandEffectProblem();
        Assert.NotNull(problem);
        Assert.Contains("Event 'gb-2019-08-09': demandEffect.mwPerHz", problem, StringComparison.Ordinal);
        var load = Assert.Throws<InvalidDataException>(() => EventCatalog.Validate(new[] { bad }, N317Now));
        Assert.Equal(problem, load.Message);
        var apply = Assert.Throws<InvalidDataException>(() => bad.ApplyRecordedServices(new DynamicsConfig()));
        Assert.Equal(problem, apply.Message);
    }

    [Fact]
    public void N317_a_demand_effect_on_an_event_with_no_demand_is_refused()
    {
        GridEvent bad = Aug2019() with { PreEventDemandMw = 0.0 };
        string? problem = bad.DemandEffectProblem();
        Assert.NotNull(problem);
        Assert.Contains("preEventDemandMw", problem, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => EventCatalog.Validate(new[] { bad }, N317Now));
        // Without a demand effect the same event is not judged here (the swing kernel refuses a negative demand itself).
        Assert.Null((bad with { DemandEffect = null }).DemandEffectProblem());
    }

    [Fact]
    public void N317_the_2019_replay_reproduces_the_nadir_value_and_its_time()
    {
        ReplayResult r = ReplayValidator.Validate(Aug2019());
        _out?.WriteLine($"verdict {r.Verdict}; RoCoF {r.SimulatedRocofHzPerS:R} Hz/s; nadir {r.SimulatedNadirHz:R} Hz at {r.SimulatedNadirSeconds:R} s; " +
                        $"corners best {r.NadirBestCornerHz:R} worst {r.NadirWorstCornerHz:R}; settle {r.Simulated.SettleHz:R}");
        _out?.WriteLine(r.Summary);
        Assert.Equal(ReplayVerdict.Reproduced, r.Verdict);
        // Before N317: RoCoF -0.1607763134153839 Hz/s (5 % error), nadir 48.820307507181965 Hz at 14.06 s (the model's
        // first arrest, 61.8 s before the recorded nadir). After (replica): the nadir is the LFDD instant.
        Near(-0.17097330951595094, r.SimulatedRocofHzPerS, "peak RoCoF (Hz/s)");
        Near(48.84096136852519, r.SimulatedNadirHz, "nadir (Hz)");
        Assert.Equal(76.0, r.SimulatedNadirSeconds);
        Assert.Equal(75.9, r.RecordedNadirSeconds);
        Assert.True(Math.Abs(r.SimulatedNadirSeconds - 75.9) <= ReplayTolerance.Default.NadirTimeToleranceS);
        Assert.Null(r.NadirTimingNote);
        Assert.DoesNotContain("nadir time", r.Summary, StringComparison.Ordinal);
        Assert.True(r.RocofRelError <= ReplayTolerance.Default.RocofRelTol, $"RoCoF error {r.RocofRelError:R}");
        Assert.True(r.NadirErrorHz <= ReplayTolerance.Default.NadirAbsToleranceHz, $"nadir error {r.NadirErrorHz:R}");
        // The declared-uncertainty corners scale the damping (x1.5 / x0.6); the recorded 48.8 Hz lies inside them.
        Near(49.172636097836424, r.NadirBestCornerHz, "best-corner nadir (Hz)");
        Near(48.19524559923123, r.NadirWorstCornerHz, "worst-corner nadir (Hz)");
        Assert.True(r.RecordedNadirInEnvelope);
        Near(48.89159962877177, r.Simulated.SettleHz, "frequency at the end of the 120 s window (Hz)");
    }

    [Fact]
    public void N317_the_2019_trajectory_follows_the_recorded_shape()
    {
        FrequencyResponse sim = ReplayValidator.Validate(Aug2019()).Simulated;
        Assert.Equal(6001, sim.Points.Count);   // 120 s at 20 ms (the 90 s last step + the 30 s tail)
        // The first arrest: replica 49.23389303381097 Hz at 16.72 s. The record: 49.1 Hz, "approximately 15 seconds" (S192
        // PDF p.8) to 25 s (Ofgem 2.4.8). Before N317 the model's first arrest was 48.82 Hz at 14.06 s, 0.28 Hz too deep.
        FrequencyPoint first = sim.Points.Where(p => (p.Time - sim.Points[0].Time).TotalSeconds <= 50.0).MinBy(p => p.Hz)!;
        Near(49.23389303381097, first.Hz, "first-arrest frequency (Hz)");
        Near(16.72, (first.Time - sim.Points[0].Time).TotalSeconds, "first-arrest time (s)", 1e-6);
        // The plateau: the record gives 49.2 Hz at 45 s (Ofgem 2.4.8; S192 Table 1 "recovers to 49.2Hz" at 16:53:18).
        Near(49.29000153608175, sim.Points[2225].Hz, "frequency at 44.5 s (Hz)");
        // The LFDD instant: the nadir point (76.0 s, index 3800).
        Near(48.84096136852519, sim.Points[3800].Hz, "frequency at 76.0 s (Hz)");
    }

    [Fact]
    public void N317_with_the_bundled_trace_the_2019_replay_is_still_capped_at_marginal_but_closer()
    {
        GridEvent ev = Aug2019();
        string csv = Path.Combine(CliProcess.Root(), "data", "neso", "gb-2019-08-09-frequency.csv");
        ReplayResult r = ReplayValidator.Validate(ev with { Outcome = ev.Outcome with { NesoTraceCsvPath = csv } });
        Assert.NotNull(r.PerSampleRmseHz);
        _out?.WriteLine($"RMSE {r.PerSampleRmseHz!.Value:R} Hz over {r.TraceSamplesScored} samples (before N317: 0x3FC39F50E4EFD314, about 0.1533 Hz)");
        Assert.Equal(5996, r.TraceSamplesScored);
        // Replica (linear interpolation of the 1 s trace): about 0.0988 Hz. Still above the 0.05 Hz cap, so Marginal.
        Assert.InRange(r.PerSampleRmseHz.Value, 0.08, 0.12);
        Assert.Equal(ReplayVerdict.Marginal, r.Verdict);
    }

    [Fact]
    public void N317_the_2019_dossier_carries_no_timing_statement_and_its_counterfactual_still_unlocks()
    {
        EvidenceReport rep = EvidenceReport.Build(EvidenceSpec.Default(Aug2019(), new DeviceConfig("lf6-n317-test", 10_000, 500)));
        Assert.Equal(ReplayVerdict.Reproduced, rep.BaselineVerdict);
        Assert.StartsWith("Baseline Reproduced; counterfactual device ", rep.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("nadir time", rep.Summary, StringComparison.Ordinal);
        // The scenario sweep gates on the same baseline.
        ScenarioMatrix m = ScenarioEngine.Run(ScenarioSpec.Default(Aug2019(), new DeviceConfig("lf6-n317-test", 40_000, 1500)));
        Assert.NotEmpty(m.RiskCurve.Points);
    }

    [Fact]
    public void N317_the_pre_n317_event_rebuilt_for_older_pins_is_the_old_model()
    {
        GridEvent old = Pre2019N317(Aug2019());
        ReplayResult r = ReplayValidator.Validate(old);
        // The figures the old model gave (LF4 notes; the replica agrees to the last bit).
        Near(48.820307507181965, r.SimulatedNadirHz, "pre-N317 nadir (Hz)");
        Near(14.06, r.SimulatedNadirSeconds, "pre-N317 nadir time (s)", 1e-6);
        Near(-0.1607763134153839, r.SimulatedRocofHzPerS, "pre-N317 peak RoCoF (Hz/s)");
        Assert.NotNull(r.NadirTimingNote);
    }
}
