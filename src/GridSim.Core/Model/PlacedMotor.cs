namespace GridSim.Core.Model;

/// <summary>
/// RC3 P8 (motor acceleration): the shaft load torque of a placed motor as a function of rotor speed,
/// Tl(w) = <see cref="ConstantPu"/> + <see cref="LinearPu"/> w + <see cref="QuadraticPu"/> w^2, with w the rotor speed in
/// per unit of synchronous speed and the torque in per unit of the motor's torque base (rated MVA over synchronous
/// mechanical speed). A fan or centrifugal pump is mostly quadratic, a conveyor mostly constant. A positive torque opposes
/// motoring rotation.
/// </summary>
public sealed record MotorLoadTorque(double ConstantPu = 0, double LinearPu = 0, double QuadraticPu = 0)
{
    /// <summary>The load torque (pu) at rotor speed <paramref name="speedPu"/> (pu of synchronous speed).</summary>
    public double At(double speedPu) => ConstantPu + LinearPu * speedPu + QuadraticPu * speedPu * speedPu;
}

/// <summary>
/// RC3 P8 (motor acceleration): one placed induction motor of a native v2 network file (its <c>motors</c> array). It is
/// carried BESIDE the power-flow model (<see cref="IO.Json.V2CaseLoadResult.Motors"/>), never inside it: the steady-state
/// power flow and every existing study see exactly the network they saw before motor rows existed. Only the dedicated
/// motor acceleration study connects it.
///
/// <para><b>Bases and units.</b> The equivalent circuit is the single-cage circuit of <see cref="InductionMachine"/>
/// (stator <see cref="RaPu"/> + j <see cref="XaPu"/>, magnetising j <see cref="XmPu"/>, rotor <see cref="R1Pu"/> / s + j
/// <see cref="X1Pu"/>) in per unit on the motor base: <see cref="RatedMva"/> and <see cref="RatedKv"/> (line-to-line).
/// <see cref="InertiaH"/> is the stored kinetic energy at synchronous speed over <see cref="RatedMva"/> (seconds) of the
/// motor rotor and everything coupled to its shaft. Torque is per unit of the torque base Tb = RatedMva / w_sync,mech, so
/// the swing equation is 2 H dw/dt = Te - Tl with w in pu of synchronous speed; with the pole count p and the supply
/// frequency f the SI form is J dw_m/dt = Te - Tl with J = 2 H S / w_m,sync^2 and w_m,sync = 4 pi f / p.</para>
/// </summary>
public sealed record PlacedMotor(
    string Name,
    int Bus,
    double RatedMva,
    double RatedKv,
    double RaPu,
    double XaPu,
    double XmPu,
    double R1Pu,
    double X1Pu,
    double InertiaH,
    MotorLoadTorque LoadTorque,
    string? Description = null)
{
    /// <summary>The single-cage equivalent circuit as the incumbent <see cref="InductionMachine"/> record, so the
    /// incumbent arithmetic (<see cref="PowerFlow.InductionMachines.AtSlip"/>) evaluates it. The schedule fields are
    /// unused by that entry point (mechanical code, 0 MW).</summary>
    public InductionMachine Circuit() => new(Bus, Name, InductionPowerCode.Mechanical, 0, RatedMva, RatedKv, RaPu, XaPu, XmPu, R1Pu, X1Pu);
}
