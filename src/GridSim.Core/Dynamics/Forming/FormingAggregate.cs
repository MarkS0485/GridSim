using GridSim.Core.Assets;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.Forming;

/// <summary>
/// The virtual former the grid frequency is built on: virtual inertia Ev (MW.s - a CONTROL parameter
/// Hv*Sv, NEVER real (1/2)Jw^2), damping Dv (MW/Hz), and a deviation setpoint (MW). The MVP reads these
/// straight from <see cref="DynamicsConfig"/>; the fleet-derived aggregate (A2) is the
/// <see cref="FromFleet"/> seam.
/// </summary>
public readonly record struct FormingAggregate(double VirtualInertiaMwS, double DampingMwPerHz, double SetpointMw)
{
    // N345 (CF3, October 2026): the fall-back value of each class parameter this code reads when the class does not
    // set it, named so the v2 kind specs (schemas/v2/kinds) cite it by symbol instead of quoting the source line.
    // Each constant is the literal it replaces, character for character, so no number moves.
    internal const double DefaultDampingMwPerHz = 0.0;   // AssetParams.DampingMwPerHz
    internal const double DefaultSynInertiaMwsPerHz = 0.0;   // AssetParams.SynInertiaMwsPerHz

    /// <summary>The lumped, config-driven former (MVP path).</summary>
    public static FormingAggregate FromConfig(DynamicsConfig cfg)
        => new(cfg.VirtualInertiaMwS, cfg.FormingDampingMwPerHz, cfg.FormingSetpointMw);

    /// <summary>
    /// A2 seam: aggregate the virtual inertia of the grid-forming assets a model binds -
    /// <c>Ev = sum( SynInertiaMwsPerHz_i * f0/2 )</c>, <c>Dv = sum( DampingMwPerHz_i )</c> - the
    /// re-interpretation of the <see cref="GridFormingInverter"/> params from a df/dt POWER injection
    /// (which lowers RoCoF from OUTSIDE the swing) into the forming DENOMINATOR (which forms the
    /// frequency). Only <see cref="AssetKind.GridFormingInverter"/> classes contribute; grid-following
    /// units keep tripping through the fleet. The re-interpretation is exact: folding the injection
    /// <c>-synInertia*(df/dt)</c> onto the LHS of <c>(2E/f0) df/dt = pForcing - D*df</c> gives an
    /// effective <c>2(E+Ev)/f0</c> with <c>Ev = synInertia*f0/2</c>.
    ///
    /// <para>IDEALISATION (documented MVP simplification): representing the GFM fleet as an ideal Ev/Dv
    /// former idealises away its SoC/headroom cap (which the fleet-injection path would enforce). Ev is
    /// virtual CONTROL inertia; it is NEVER real stored (1/2)Jw^2 and is NEVER summed into the real
    /// inertia budget E - it lives only in the forming denominator.</para>
    /// </summary>
    public static FormingAggregate FromFleet(GridModel model, double f0)
    {
        if (model.AssetDynamics is not { } spec) return default;

        double ev = 0.0, dv = 0.0;
        foreach (Generator g in model.Generators)
        {
            string? className = spec.Bindings.Resolve(g.DynamicsClass, g.Type);
            if (className is null) continue;
            AssetClass cls = spec.Library.Get(className);        // throws on unknown - fail loud
            if (cls.Kind != AssetKind.GridFormingInverter) continue;
            ev += cls.Param(AssetParams.SynInertiaMwsPerHz, DefaultSynInertiaMwsPerHz) * f0 / 2.0;
            dv += cls.Param(AssetParams.DampingMwPerHz, DefaultDampingMwPerHz);
        }
        return new FormingAggregate(ev, dv, 0.0);
    }
}
