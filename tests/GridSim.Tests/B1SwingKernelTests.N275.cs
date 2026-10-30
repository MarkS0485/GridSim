using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N275 (D2 open item O4): the model-based frequency entry points (the inertia-model seam, the distributed model, the
/// asset-aware lumped solve, the model-level RoCoF screen) run at the model's own nominal frequency. No config gives the
/// defaults at the model's nominal; a config at another nominal is refused, naming both. Before, a 60 Hz case (the WSCC
/// case9, N017) was silently run at the GB 50 Hz there.
/// </summary>
public sealed partial class B1SwingKernelTests
{
    private static readonly FrequencyEvent[] N275Loss = { new(1.0, -100.0) };

    [Fact]
    public void N275_with_no_config_every_model_based_entry_point_runs_at_the_models_nominal()
    {
        GridModel case9 = IeeeCases.Case9();
        Assert.Equal(60.0, case9.FrequencyHz);
        var at60 = new DynamicsConfig { NominalHz = 60.0 };
        FrequencyResponse direct = FrequencyDynamics.Simulate(case9.SystemInertiaGvaS * 1000.0, 315, N275Loss, at60);

        AssertSameBits(direct, new LumpedScalarInertiaModel().Simulate(case9, 315, N275Loss), "seam");
        AssertSameBits(direct, new DistributedInertiaModel().Simulate(case9, 315, N275Loss), "distributed (single domain)");
        AssertSameBits(direct, AssetFrequencyDynamics.Simulate(case9, 315, N275Loss).Response, "asset-aware (empty fleet)");
        Assert.Equal(60.0, direct.Points[0].Hz);
        // The first post-event RoCoF is f0 / (2E) * deltaP at f0 = 60 (1.2 times the 50 Hz figure for the same E and loss).
        double e = Math.Max(SwingKernel.MinInertiaMwS, case9.SystemInertiaGvaS * 1000.0);
        AssertBits(60.0 / (2.0 * e) * -100.0, direct.InitialRocofHzPerS, "peak RoCoF at 60 Hz");
    }

    [Fact]
    public void N275_a_config_at_another_nominal_is_refused_naming_both()
    {
        GridModel case9 = IeeeCases.Case9();
        var at50 = new DynamicsConfig();   // the GB default
        foreach (Action call in new Action[]
                 {
                     () => new LumpedScalarInertiaModel().Simulate(case9, 315, N275Loss, at50),
                     () => new DistributedInertiaModel().Simulate(case9, 315, N275Loss, at50),
                     () => AssetFrequencyDynamics.Simulate(case9, 315, N275Loss, at50),
                     () => RocofScreening.Screen(case9, at50),
                 })
        {
            var ex = Assert.Throws<ArgumentException>(call);
            Assert.Equal("config", ex.ParamName);
            Assert.Contains("50", ex.Message);
            Assert.Contains("60", ex.Message);
        }
    }

    [Fact]
    public void N275_a_50_Hz_model_with_no_config_is_bit_identical_to_the_default_config()
    {
        GridModel m = AssetTestHelpers.Model(assetDynamics: null);
        Assert.Equal(50.0, m.FrequencyHz);
        FrequencyResponse withDefault = FrequencyDynamics.Simulate(m.SystemInertiaGvaS * 1000.0, 30_000, R080Trip, new DynamicsConfig());
        AssertSameBits(withDefault, new LumpedScalarInertiaModel().Simulate(m, 30_000, R080Trip), "seam");
        AssertSameBits(withDefault, AssetFrequencyDynamics.Simulate(m, 30_000, R080Trip).Response, "asset");
        IReadOnlyList<RocofScreeningResult> a = RocofScreening.Screen(m), b = RocofScreening.Screen(m, new DynamicsConfig());
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++) Assert.Equal(b[i], a[i]);
    }

    [Fact]
    public void N275_the_screen_of_a_60_Hz_case_runs_at_60_Hz()
    {
        GridModel case9 = IeeeCases.Case9();
        IReadOnlyList<RocofScreeningResult> now = RocofScreening.Screen(case9);
        double total = case9.Generators.Sum(g => g.H * g.MBase);
        double demand = case9.Buses.Sum(b => b.Pd);
        var conts = case9.Generators.Where(g => g.Pg > 0).Select(g => new Contingency(g.Name ?? $"gen@{g.Bus}", g.Pg, g.H * g.MBase));
        IReadOnlyList<RocofScreeningResult> at60 = RocofScreening.Screen(total, demand, conts, new DynamicsConfig { NominalHz = 60.0 });
        Assert.Equal(at60.Count, now.Count);
        for (int i = 0; i < now.Count; i++) Assert.Equal(at60[i], now[i]);
        // Moved 2026-10 (B4 N297, Ruling R1: Mark delegated EE calls 2026-10-04; source: B2's band convention, the GB bands
        // as hertz offsets about the system's nominal, FrequencyBands): the screen now grades a 60 Hz case against 59.5 /
        // 58.8 Hz (both the model entry point and the explicit-config overload above, so the record equality still holds).
        // Before: Assert.All(now, r => Assert.False(r.StatutoryBreach)), "a 60 Hz nadir cannot reach the GB 49.5 Hz band",
        // which pinned the defect. After: each flag is the nadir against the bands about 60 Hz.
        FrequencyBands b60 = FrequencyBands.ForNominal(60.0);
        Assert.All(now, r => Assert.Equal(r.NadirHz < b60.StatutoryLowHz, r.StatutoryBreach));
        Assert.All(now, r => Assert.Equal(r.NadirHz <= b60.LfddFirstStageHz, r.LfddRisk));
    }
}
