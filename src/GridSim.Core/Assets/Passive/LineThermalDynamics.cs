namespace GridSim.Core.Assets;

/// <summary>
/// A line/cable as PASSIVE thermal plant dynamics on the steady-state tick: an IEEE-738-style single
/// thermal time constant driven by loading vs ambient, producing a dynamic rating derate. A cable heats
/// (and derates) slowly - minutes - so a brief overload is tolerable where a sustained one is not. Emits
/// a <see cref="PlantStateUpdate"/> (rating derate + thermal state), never a swing-power term.
/// </summary>
public sealed class LineThermalDynamics : IPassiveAssetDynamics
{
    // N345 (CF3, October 2026): the fall-back value of each class parameter this code reads when the class does not
    // set it, named so the v2 kind specs (schemas/v2/kinds) cite it by symbol instead of quoting the source line.
    // Each constant is the literal it replaces, character for character, so no number moves.
    internal const double DefaultAmbientLoadingFraction = 0.0;   // AssetParams.AmbientLoadingFraction
    internal const double DefaultDerateAtFullThermal = 0.1;   // AssetParams.DerateAtFullThermal
    internal const double DefaultThermalTauMin = 15.0;   // AssetParams.ThermalTauMin

    private readonly double _thermalTauMin, _ambient, _derateFull;
    private readonly double _thermalTauS;   // #190: the thermal time constant in seconds (validated, never clamped to dt)
    private double _thermal;

    public LineThermalDynamics(AssetClass cls, string branchName)
    {
        ClassName = cls.Name;
        BranchName = branchName;
        _thermalTauMin = cls.Param(AssetParams.ThermalTauMin, DefaultThermalTauMin);
        _ambient = cls.Param(AssetParams.AmbientLoadingFraction, DefaultAmbientLoadingFraction);
        _derateFull = cls.Param(AssetParams.DerateAtFullThermal, DefaultDerateAtFullThermal);
        _thermalTauS = ThermalLag.TauSeconds(_thermalTauMin, cls.Name, branchName);   // #190: fail loud on a bad tau
        _thermal = _ambient;
    }

    public string ClassName { get; }
    public string BranchName { get; }
    public AssetKind Kind => AssetKind.Line;
    public int Bus => -1;
    public bool Tripped => false;
    public double ThermalState => _thermal;

    public void Reset() => _thermal = _ambient;

    public PlantStateUpdate Advance(in AssetStepContext ctx)
    {
        double dt = ctx.DtSeconds;
        // Thermal state chases loading^2 (heating ~ I^2 R); a first-order lag stepped exactly (ThermalLag, #190: the
        // old explicit Euler with tau = max(dt, 60 tauMin) lost the lag entirely once a tick reached tau).
        double drive = ctx.LoadingFraction * ctx.LoadingFraction;
        _thermal = ThermalLag.Step(_thermal, Math.Max(drive, _ambient), dt, _thermalTauS);
        double derate = _derateFull * Math.Clamp(_thermal, 0.0, 1.0);
        return new PlantStateUpdate(TapRatio: null, RatingDerateFraction: derate, ThermalStatePu: _thermal);
    }
}
