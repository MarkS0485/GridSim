namespace GridSim.Core.Dynamics.SmallSignal;

/// <summary>
/// The kind of a small-signal state variable. The load-bearing distinction is REAL rotational energy
/// (<see cref="SwingAngle"/>/<see cref="SwingSpeed"/> - a machine's (1/2)J*omega^2 mass) versus SYNTHETIC
/// control emulation (the inverter states) - the codebase's non-negotiable separation (see
/// <c>FrequencyDynamics.DeviceRotationalInertiaMwS</c>). A synthetic df/dt "inertia" coefficient must
/// NEVER be summed into a machine's H or internal EMF E; the kind tag makes that machine-checkable and it
/// travels on the wire (the export carries each state's kind).
/// </summary>
public enum StateKind
{
    /// <summary>Synchronous-machine rotor angle deviation d(delta) (rad) - REAL.</summary>
    SwingAngle,
    /// <summary>Synchronous-machine per-unit speed deviation d(omega-bar) - REAL ((1/2)J*omega^2 mass).</summary>
    SwingSpeed,
    /// <summary>Grid-forming virtual angle d(delta_v) - SYNTHETIC control (reserved; the faithful GFM
    /// reduction uses a first-order actuator, not a self-forming virtual rotor).</summary>
    VsmAngle,
    /// <summary>Grid-forming virtual frequency d(omega_v) - SYNTHETIC control (reserved).</summary>
    VsmSpeed,
    /// <summary>Grid-following PLL angle error d(theta_pll) - control (archetype SRF-PLL).</summary>
    PllAngle,
    /// <summary>Grid-following PLL frequency estimate d(omega_pll) - control (archetype SRF-PLL).</summary>
    PllSpeed,
    /// <summary>Inverter first-order actuator lag d(p_lag) - control. Carries the GFM's df and df/dt
    /// feedthrough (its synthetic-inertia response) as an injection, never as stored energy.</summary>
    DroopActuatorLag,
    /// <summary>Slow energy integrator d(SoC) - control (reserved: not produced; an SoC state would be a pure origin
    /// pole, excluded from the stability matrix by design).</summary>
    StateOfCharge,
    /// <summary>Grid-following PLL frequency-estimate rate d(f_est)/dt - control (the second state of the GFL
    /// reduction). R085: this state used to be labelled <see cref="PllAngle"/> although it is not an angle.
    /// Appended last so the ordinal of every earlier kind is unchanged.</summary>
    PllFrequencyRate,
}

/// <summary>One labelled entry of the small-signal state vector: its position, physical kind, the owning
/// device id (machine/inverter bus) and a short human name.</summary>
public readonly record struct StateLabel(int Index, StateKind Kind, int OwnerId, string Name)
{
    /// <summary>True when this state is a SYNTHETIC control emulation, not real rotational energy - the
    /// states whose coefficients must never be folded into a machine H/E.</summary>
    public bool IsSynthetic => Kind is not (StateKind.SwingAngle or StateKind.SwingSpeed);

    /// <summary>True for the grid-forming virtual-synchronous-machine states specifically. R085: always false today,
    /// because no model produces <see cref="StateKind.VsmAngle"/> or <see cref="StateKind.VsmSpeed"/> (the grid-forming
    /// reduction is the first-order actuator, whose synthetic-inertia response is the df/dt feedthrough carried on
    /// <see cref="InverterLinearBlock.SyntheticInertiaMwsPerHz"/>, not a state). A virtual-rotor model would produce
    /// them; that is frequency / inertia work and is deferred with that pass.</summary>
    public bool IsSyntheticInertia => Kind is StateKind.VsmAngle or StateKind.VsmSpeed;
}
