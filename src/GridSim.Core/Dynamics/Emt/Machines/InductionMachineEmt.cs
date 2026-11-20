using GridSim.Core.Diagnostics;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.Emt.Machines;

/// <summary>
/// The data of an EMT induction machine: the incumbent single-cage equivalent circuit of the case model
/// (<see cref="InductionMachine"/>: machine base MBASE, RATEKV, stator RA + j XA, magnetising j XM, rotor R1 + j X1, all pu), so a case
/// that the steady-state and motor-start studies solve runs in EMT with the same numbers; the EMT machine adds the inertia constant and
/// the shaft load.
/// </summary>
/// <param name="Circuit">The case-model machine record (its bus, schedule and name are not used by the EMT machine).</param>
/// <param name="FrequencyHz">Rated frequency.</param>
/// <param name="InertiaSeconds">Inertia constant H of the rotating set, seconds on the machine base.</param>
public sealed record InductionMachineEmtData(InductionMachine Circuit, double FrequencyHz, double InertiaSeconds);

/// <summary>
/// An induction machine (single cage) in the stationary alpha-beta frame on the nodal EMT solver: stator and rotor flux dynamics,
/// swing equation <c>2H dw/dt = Te - Tl(w)</c> with <c>Tl</c> the load torque in pu of the rated torque (a function of the speed in pu of
/// synchronous speed), no saturation, three-wire stator (no zero sequence). Motor convention at the terminals: probe currents are
/// absorbed by the machine. The stationary frame makes the stator impedance isotropic, so the only changes of the step matrix are the
/// speed-dependent rotor terms.
/// <para>Probe states (channel): 0 speed pu, 1 electromagnetic torque pu, 2 slip, 3 load torque pu, 4 stator flux magnitude pu, 5 rotor flux magnitude pu.</para>
/// </summary>
public sealed class InductionMachineEmt : FluxMachineElement
{
    private readonly InductionMachineEmtData _data;
    private readonly double _h;
    private double _tl;
    private double _initSpeed;

    /// <summary>Create the machine at three stator nodes (no neutral). It starts at rest unless <see cref="StartAtSpeed"/> is called.</summary>
    public InductionMachineEmt(string name, int a, int b, int c, InductionMachineEmtData data)
        : base(name, a, b, c, -1, false, Check(data).Circuit.MBase, data.Circuit.RatedKv, data.FrequencyHz)
    {
        _data = data;
        _h = data.InertiaSeconds;
        InductionMachine m = data.Circuit;
        double ls = m.XaPu + m.XmPu, lr = m.X1Pu + m.XmPu, lm = m.XmPu;
        N = 4; NS = 2; FieldIndex = -1;
        Lmat = new double[16]; Rd = new double[4]; Smat = new double[16];
        Lmat[0] = Lmat[5] = ls; Lmat[10] = Lmat[15] = lr;
        Lmat[2] = Lmat[8] = lm; Lmat[7] = Lmat[13] = lm;
        Rd[0] = Rd[1] = m.RaPu; Rd[2] = Rd[3] = m.R1Pu;
        Smat[2 * 4 + 3] = -1.0;      // rotor alpha: - w psi_r,beta
        Smat[3 * 4 + 2] = 1.0;       // rotor beta:  + w psi_r,alpha
        FinishModel();
        SetInitial(new double[4], 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
    }

    private static InductionMachineEmtData Check(InductionMachineEmtData d)
    {
        static Exception R(string m) => DomainRefusal.Mark(new ArgumentException(m));
        InductionMachine m = d.Circuit;
        if (!(m.MBase > 0) || !(m.RatedKv > 0)) throw R("The induction machine needs a positive MBASE and RATEKV.");
        if (!(m.RaPu >= 0) || !(m.XaPu >= 0) || !(m.XmPu > 0) || !(m.R1Pu > 0) || !(m.X1Pu >= 0)) throw R("The induction machine circuit needs RA, XA, X1 not negative, XM and R1 positive.");
        if (!(d.InertiaSeconds > 0) || !double.IsFinite(d.InertiaSeconds)) throw R("The inertia constant must be finite and positive.");
        return d;
    }

    /// <summary>Shaft load torque in pu of the rated torque versus the speed in pu of synchronous speed (null = no load).</summary>
    public Func<double, double>? LoadTorquePu { get; set; }

    /// <summary>Start from a rotor speed (pu of synchronous) with the fluxes at zero (the machine is switched on at the stated speed).</summary>
    public void StartAtSpeed(double speedPu)
    {
        if (!double.IsFinite(speedPu)) throw DomainRefusal.Mark(new ArgumentException("The initial speed must be finite."));
        _initSpeed = speedPu;
        SetInitial(new double[4], speedPu, 0.0, 0.0, 0.0, 0.0, 0.0);
    }

    private protected override double Predict(double t, double dt, double c) => Omega + dt * Alpha;

    private protected override double Torque(double[] psi, double[] x) => psi[0] * x[1] - psi[1] * x[0];

    private protected override double Advance(double te, double t, double dt, double c)
    {
        double wp = Omega + dt * Alpha;
        double tlp = LoadTorquePu?.Invoke(wp) ?? 0.0;
        double w = Omega + dt * ((1.0 - c) * Alpha + c * (te - tlp) / (2.0 * _h));
        _tl = LoadTorquePu?.Invoke(w) ?? 0.0;
        Alpha = (te - _tl) / (2.0 * _h);
        Omega = w;
        return w;
    }

    private protected override void ResetMechanics() { Alpha = 0.0; _tl = 0.0; }

    private protected override double ProbeSign => 1.0;

    internal override double ProbeStateAt(int channel) => channel switch
    {
        0 => Omega,
        1 => Te,
        2 => 1.0 - Omega,
        3 => _tl,
        4 => Math.Sqrt(Psi[0] * Psi[0] + Psi[1] * Psi[1]),
        5 => Math.Sqrt(Psi[2] * Psi[2] + Psi[3] * Psi[3]),
        _ => double.NaN,
    };

    /// <summary>The initial speed the run starts from.</summary>
    public double InitialSpeedPu => _initSpeed;
}
