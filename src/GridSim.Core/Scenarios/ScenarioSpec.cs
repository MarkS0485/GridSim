using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Replay;

namespace GridSim.Core.Scenarios;

/// <summary>Thrown when a counterfactual is requested but the no-device baseline does not reproduce the record.</summary>
public sealed class BaselineNotReproducedException : Exception
{
    public ReplayResult Baseline { get; }
    public BaselineNotReproducedException(ReplayResult baseline)
        : base($"Refusing to run counterfactuals: the no-device baseline is '{baseline.Verdict}', not 'Reproduced'. " +
               $"Reproduce reality first. {baseline.Summary}")
        => Baseline = baseline;
}

/// <summary>Which unfitted-constant corner to evaluate - the honest error bars around the documented-but-unfitted params.</summary>
public enum Corner { Best, Nominal, Worst }

/// <summary>
/// A device under test. <see cref="Apply"/> is the single intrinsic-vs-synthetic chokepoint: the device's real
/// (1/2)J*omega^2 rotational energy goes to <see cref="DynamicsConfig.DeviceRotationalInertiaMwS"/> and its fast response
/// to <see cref="DynamicsConfig.FastResponseMw"/> - two separate terms, never merged.
/// </summary>
public sealed record DeviceConfig(
    string Name,
    double RotationalInertiaMwS,
    double FastResponseMw,
    double FastResponseTimeConstantS = 0.5,
    double FastResponseDeadbandHz = 0.015)
{
    public static readonly DeviceConfig None = new("none", 0, 0);

    public DynamicsConfig Apply(DynamicsConfig baseCfg) => baseCfg with
    {
        DeviceRotationalInertiaMwS = RotationalInertiaMwS,
        FastResponseMw = FastResponseMw,
        FastResponseTimeConstantS = FastResponseTimeConstantS,
        FastResponseDeadbandHz = FastResponseDeadbandHz,
    };

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated ScenarioSpecJsonContext (below) so no runtime reflection is needed.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly ScenarioSpecJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    });

    public static DeviceConfig Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), Json.DeviceConfig)
        ?? throw new InvalidDataException($"Not a device config: {path}");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.DeviceConfig));
    }
}

/// <summary>The sweep axes: system inertia x devices x corners.</summary>
public sealed record SweepAxes(
    IReadOnlyList<double> InertiaGvaS,
    IReadOnlyList<DeviceConfig> Devices,
    IReadOnlyList<Corner> Corners)
{
    /// <summary>
    /// The GB inertia trajectory (GVA.s), verified against the TSGB corpus 2026-07-14:
    /// 260 ~ 2017 average outturn (259.85, NESO-attributed table, white paper sec. 2.6);
    /// 155 = 2025 average outturn (sec. 2.6); 120 = "typical current outturn" (sec. 1/sec. 2.6);
    /// 102 = the FRCR 2025 proposed minimum floor (Policy 0006); 50 ~ the recorded
    /// yearly minima band (44-52 GVA.s, 2021-2023, sec. 2.6).
    /// </summary>
    public static readonly IReadOnlyList<double> GbInertiaTrajectoryGvaS = new[] { 260.0, 155.0, 120.0, 102.0, 50.0 };
}

/// <summary>A full counterfactual scenario: the recorded event, the sweep, and the base/tolerance settings.</summary>
public sealed record ScenarioSpec(
    GridEvent Event,
    SweepAxes Axes,
    DynamicsConfig BaseConfig,
    ReplayTolerance Tolerance)
{
    /// <summary>Default sweep: the GB inertia trajectory, {no-device, device}, {best,worst} corners.</summary>
    public static ScenarioSpec Default(GridEvent ev, DeviceConfig device) => new(
        ev,
        new SweepAxes(SweepAxes.GbInertiaTrajectoryGvaS, new[] { DeviceConfig.None, device }, new[] { Corner.Best, Corner.Worst }),
        new DynamicsConfig(),
        ReplayTolerance.Default);
}

/// <summary>
/// Applies a <see cref="Corner"/> by scaling the DOCUMENTED-BUT-UNFITTED constants (load damping, responsive
/// fraction, governor lag). Best = optimistic arrest, Worst = pessimistic. These brackets carry the model's own
/// error bars into every result. Note: corners do NOT touch the initial RoCoF - that is set purely by E at t=0,
/// before governor/damping act - so the RoCoF story is corner-independent.
/// </summary>
public static class ScenarioCorners
{
    // ---- Corner multipliers (October 2026 review, WS13 R194). Values unchanged. Provenance: engineering
    // ---- brackets chosen when the corners were introduced, NOT fitted to recorded events; they are deliberately
    // ---- asymmetric (the pessimistic corner pulls harder than the optimistic one pushes) because a slow or weak
    // ---- response is the outcome a security study must not under-state. The H0 export references these names
    // ---- (R178, October 2026 H hygiene stream; it used to repeat them as literals).

    /// <summary>Best corner: load damping x1.5.</summary>
    public const double BestDampingScale = 1.5;
    /// <summary>Best corner: responsive fraction x1.5 (capped at 1).</summary>
    public const double BestResponsiveScale = 1.5;
    /// <summary>Best corner: governor time constant x0.7 (faster response).</summary>
    public const double BestGovernorTauScale = 0.7;
    /// <summary>Worst corner: load damping x0.6.</summary>
    public const double WorstDampingScale = 0.6;
    /// <summary>Worst corner: responsive fraction x0.6.</summary>
    public const double WorstResponsiveScale = 0.6;
    /// <summary>Worst corner: governor time constant x1.4 (slower response).</summary>
    public const double WorstGovernorTauScale = 1.4;
    /// <summary>The best corner caps the scaled responsive fraction at this (the whole demand cannot be more than all
    /// of itself). Only the best corner applies it; the worst corner scales down and the nominal corner does not scale.
    /// R178: named so the H0 export can publish it beside the factors (value unchanged).</summary>
    public const double MaxResponsiveFraction = 1.0;

    public static DynamicsConfig Apply(DynamicsConfig cfg, Corner c) => c switch
    {
        Corner.Best => cfg with
        {
            DampingPctPerHz = cfg.DampingPctPerHz * BestDampingScale,
            ResponsiveFraction = Math.Min(MaxResponsiveFraction, cfg.ResponsiveFraction * BestResponsiveScale),
            GovernorTimeConstantS = cfg.GovernorTimeConstantS * BestGovernorTauScale,
        },
        Corner.Worst => cfg with
        {
            DampingPctPerHz = cfg.DampingPctPerHz * WorstDampingScale,
            ResponsiveFraction = cfg.ResponsiveFraction * WorstResponsiveScale,
            GovernorTimeConstantS = cfg.GovernorTimeConstantS * WorstGovernorTauScale,
        },
        _ => cfg,
    };
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for a device configuration and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DeviceConfig))]
internal sealed partial class ScenarioSpecJsonContext : JsonSerializerContext;
