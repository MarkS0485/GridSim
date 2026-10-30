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
    private double _thermal;

    public LineThermalDynamics(AssetClass cls, string branchName)
    {
        ClassName = cls.Name;
        BranchName = branchName;
        _thermalTauMin = cls.Param(AssetParams.ThermalTauMin, DefaultThermalTauMin);
        _ambient = cls.Param(AssetParams.AmbientLoadingFraction, DefaultAmbientLoadingFraction);
        _derateFull = cls.Param(AssetParams.DerateAtFullThermal, DefaultDerateAtFullThermal);
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
        double tauS = Math.Max(dt, _thermalTauMin * 60.0);
        // Thermal state chases loading^2 (heating ~ I^2 R), clamped; slow lag.
        double drive = ctx.LoadingFraction * ctx.LoadingFraction;
        _thermal += (Math.Max(drive, _ambient) - _thermal) / tauS * dt;
        double derate = _derateFull * Math.Clamp(_thermal, 0.0, 1.0);
        return new PlantStateUpdate(TapRatio: null, RatingDerateFraction: derate, ThermalStatePu: _thermal);
    }
}
