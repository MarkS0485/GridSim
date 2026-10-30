namespace GridSim.Core.Assets;

/// <summary>
/// The behavioural family of an authored asset class. Determines which controller the factory
/// builds and whether it contributes a swing-power term (active) or a slow plant-state update
/// (passive). The string <c>Type</c> on <see cref="Model.Generator"/> (fuel/technology) is
/// orthogonal - this is the <i>dynamic behaviour</i> the asset exhibits, not what it burns.
/// </summary>
public enum AssetKind
{
    /// <summary>PLL-behind current source: samples LOCAL frequency, droops/df-dt responds, trips on RoCoF/vector-shift. Active.</summary>
    GridFollowingInverter,
    /// <summary>Voltage-source converter: near-instant synthetic inertia (power proportional to df/dt), ride-through biased. Active.</summary>
    GridFormingInverter,
    /// <summary>Rotating machine authored as a class (reserved; active machine trips deferred - see plan risk 1).</summary>
    SynchronousMachine,
    /// <summary>On-load tap changer + thermal derating on the steady-state tick. Passive.</summary>
    Transformer,
    /// <summary>Line/cable dynamic thermal rating on the steady-state tick. Passive.</summary>
    Line,
    /// <summary>A protective device (RoCoF/LoM, U/O-frequency, U/O-voltage, LFDD, thermal, recloser). Active.</summary>
    Protection,
}
