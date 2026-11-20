using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt.Machines;

/// <summary>
/// The data of an EMT synchronous machine. The dynamic data are the SAME parameter set the RMS studies use
/// (<see cref="SubtransientMachine.Parameters"/>: H, D, Xd, X'd, X''d, Xq, X'q, X''q, T'do, T''do, T'qo, T''qo and the leakage
/// reactance Xl); a case therefore runs in both. The RMS set has no armature resistance (the RMS machine has Rs = 0) and no rating,
/// so the EMT machine adds them. The external reactance and infinite-bus voltage of the RMS set are ignored (the network is the EMT network).
/// </summary>
/// <param name="Rms">The RMS parameter set.</param>
/// <param name="RatingMva">Machine base, MVA (the base the pu data are on).</param>
/// <param name="RatedKvLl">Rated line-to-line voltage, kV rms.</param>
/// <param name="FrequencyHz">Rated frequency.</param>
/// <param name="RaPu">Armature resistance, pu.</param>
/// <param name="X0Pu">Zero-sequence reactance, pu (null = the leakage reactance).</param>
public sealed record SynchronousMachineEmtData(SubtransientMachine.Parameters Rms, double RatingMva, double RatedKvLl, double FrequencyHz, double RaPu, double? X0Pu = null);

/// <summary>The equivalent circuit (pu, equal mutual) derived from the standard parameters; see <see cref="SynchronousMachineCircuit.From"/>.</summary>
public sealed record SynchronousMachineCircuit(
    double Xl, double Lad, double Lfd, double Rfd, double L1d, double R1d, bool HasDDamper,
    double Laq, double L1q, double R1q, double L2q, double R2q, int QWindings, double X0)
{
    /// <summary>
    /// Derive the circuit. With the leakage Xl, the d-axis magnetising reactance is <c>Lad = Xd - Xl</c>; the field leakage follows
    /// <c>X'd - Xl = Lad Lfd / (Lad + Lfd)</c>; the d damper from <c>1/(X''d - Xl) = 1/Lad + 1/Lfd + 1/L1d</c>; the resistances from the
    /// open-circuit time constants <c>T'do = (Lad + Lfd)/(w0 Rfd)</c> and <c>T''do = (L1d + Lad Lfd/(Lad + Lfd))/(w0 R1d)</c>
    /// (Kundur, Power System Stability and Control, ch. 4, the standard-parameter to circuit-parameter conversion; the q axis in the same
    /// way, with two windings when X'q &lt; Xq and one when X'q = Xq). These are the textbook approximations that assume well-separated
    /// transient and subtransient time constants; the circuit's own exact time constants differ from the standard ones by a few per cent.
    /// </summary>
    public static SynchronousMachineCircuit From(SynchronousMachineEmtData d)
    {
        static Exception R(string m) => DomainRefusal.Mark(new ArgumentException(m));
        SubtransientMachine.Parameters p = d.Rms;
        double w0 = 2.0 * Math.PI * d.FrequencyHz;
        if (!(d.RatingMva > 0) || !(d.RatedKvLl > 0) || !(d.FrequencyHz > 0)) throw R("The machine rating, rated voltage and frequency must be positive.");
        if (!(d.RaPu >= 0) || !double.IsFinite(d.RaPu)) throw R("The armature resistance must be finite and not negative.");
        double xl = p.LeakageReactance > 0.0 ? p.LeakageReactance : 0.5 * Math.Min(p.Xdpp, p.Xqpp);
        if (!(xl > 0 && xl < p.Xdpp && xl < p.Xqpp)) throw R($"The leakage reactance Xl = {xl:R} must be positive and below X''d and X''q.");
        const double eq = 1e-9;
        double lad = p.Xd - xl;
        double xdpl = p.Xdp - xl;
        if (!(lad > 0) || !(xdpl > 0)) throw R("Xd and X'd must exceed the leakage reactance.");
        double lfd, rfd, l1d = 0.0, r1d = 0.0;
        bool dd = p.Xdpp < p.Xdp * (1.0 - eq);
        if (p.Xdp >= p.Xd * (1.0 - eq)) throw R("X'd must be below Xd (the field winding needs a leakage).");
        lfd = lad * xdpl / (lad - xdpl);
        rfd = (lad + lfd) / (w0 * p.Tdop);
        if (dd)
        {
            double inv = 1.0 / (p.Xdpp - xl) - 1.0 / lad - 1.0 / lfd;
            if (!(inv > 0)) throw R("X''d is too close to X'd for a d-axis damper winding.");
            l1d = 1.0 / inv;
            r1d = (l1d + lad * lfd / (lad + lfd)) / (w0 * p.Tdopp);
        }
        double laq = p.Xq - xl;
        if (!(laq > 0)) throw R("Xq must exceed the leakage reactance.");
        double l1q = 0.0, r1q = 0.0, l2q = 0.0, r2q = 0.0;
        int qw = 0;
        if (p.Xqp < p.Xq * (1.0 - eq))
        {
            double xqpl = p.Xqp - xl;
            if (!(xqpl > 0)) throw R("X'q must exceed the leakage reactance.");
            l1q = laq * xqpl / (laq - xqpl);
            r1q = (laq + l1q) / (w0 * p.Tqop);
            qw = 1;
            if (p.Xqpp < p.Xqp * (1.0 - eq))
            {
                double inv = 1.0 / (p.Xqpp - xl) - 1.0 / laq - 1.0 / l1q;
                if (!(inv > 0)) throw R("X''q is too close to X'q for a second q-axis winding.");
                l2q = 1.0 / inv;
                r2q = (l2q + laq * l1q / (laq + l1q)) / (w0 * p.Tqopp);
                qw = 2;
            }
        }
        else if (p.Xqpp < p.Xq * (1.0 - eq))
        {
            double inv = 1.0 / (p.Xqpp - xl) - 1.0 / laq;
            l1q = 1.0 / inv;
            r1q = (l1q + laq) / (w0 * p.Tqopp);
            qw = 1;
        }
        double x0 = d.X0Pu ?? xl;
        if (!(x0 > 0)) throw R("The zero-sequence reactance must be positive.");
        return new SynchronousMachineCircuit(xl, lad, lfd, rfd, l1d, r1d, dd, laq, l1q, r1q, l2q, r2q, qw, x0);
    }
}

/// <summary>The initial state found from a terminal phasor operating point.</summary>
/// <param name="ThetaRad">The d-axis angle from the phase-a axis at t = 0.</param>
/// <param name="LoadAngleRad">The angle of the q-axis (the internal voltage behind Xq) from the terminal voltage phasor.</param>
/// <param name="FieldVoltagePu">The field voltage that holds the state, pu (pu of the field base of the equal-mutual per-unit system).</param>
/// <param name="FieldCurrentPu">The field current, pu.</param>
/// <param name="MechanicalTorquePu">The mechanical torque that balances the electrical torque, pu.</param>
/// <param name="IdPu">Stator d current (out of the machine), pu.</param>
/// <param name="IqPu">Stator q current (out of the machine), pu.</param>
public sealed record SynchronousInitialState(double ThetaRad, double LoadAngleRad, double FieldVoltagePu, double FieldCurrentPu, double MechanicalTorquePu, double IdPu, double IqPu);

/// <summary>
/// A synchronous machine in dq coordinates on the nodal EMT solver: stator flux dynamics (EMT, not the RMS algebraic stator), field
/// and damper windings, swing equation <c>2H dw/dt = Tm - Te - D (w - 1)</c>, no saturation. Generator convention at the terminals
/// (probe currents are delivered to the network). It is in the Park frame, so the dq currents are constant in a balanced steady
/// state. Per-unit data come from the RMS parameter set (<see cref="SynchronousMachineEmtData"/>).
/// <para>Probe states (channel): 0 speed pu, 1 load angle (rotor angle minus w0 t, rad), 2 electromagnetic torque pu (generator, positive
/// when the machine delivers power), 3 field current pu, 4 flux d, 5 flux q, 6 electrical power out pu, 7 terminal voltage pu.</para>
/// </summary>
public sealed class SynchronousMachineEmt : FluxMachineElement
{
    private readonly SynchronousMachineEmtData _data;
    private readonly SynchronousMachineCircuit _circ;
    private readonly double _h, _damp;
    private double _tmInit, _efdInit;

    /// <summary>Create the machine at the three stator nodes with the neutral at <paramref name="neutral"/> (ground is <see cref="Nodal.NodalNetwork.Ground"/>).</summary>
    public SynchronousMachineEmt(string name, int a, int b, int c, int neutral, SynchronousMachineEmtData data)
        : base(name, a, b, c, neutral, true, data.RatingMva, data.RatedKvLl, data.FrequencyHz)
    {
        _data = data;
        _circ = SynchronousMachineCircuit.From(data);
        _h = data.Rms.InertiaSeconds; _damp = data.Rms.DampingPu;
        Build();
    }

    /// <summary>The derived circuit (reported with a result).</summary>
    public SynchronousMachineCircuit Circuit => _circ;

    /// <summary>Mechanical torque input in pu versus time; null holds the value that balances the initial state.</summary>
    public Func<double, double>? MechanicalTorquePu { get; set; }

    /// <summary>Field voltage input in pu versus time; null holds the value that balances the initial state.</summary>
    public Func<double, double>? FieldVoltagePu { get; set; }

    private void Build()
    {
        SynchronousMachineCircuit c = _circ;
        double ra = _data.RaPu;
        // indices: 0 d, 1 q, 2 zero, 3 fd, then 1d (if any), then 1q, 2q
        int idx = 3;
        int fd = idx++;
        int d1 = c.HasDDamper ? idx++ : -1;
        int q1 = c.QWindings >= 1 ? idx++ : -1;
        int q2 = c.QWindings >= 2 ? idx++ : -1;
        N = idx; NS = 3; FieldIndex = fd;
        Lmat = new double[N * N]; Rd = new double[N]; Smat = new double[N * N];
        void Sym(int i, int j, double v) { Lmat[i * N + j] = v; Lmat[j * N + i] = v; }
        Sym(0, 0, c.Lad + c.Xl); Sym(1, 1, c.Laq + c.Xl); Sym(2, 2, c.X0);
        Sym(0, fd, c.Lad); Sym(fd, fd, c.Lad + c.Lfd);
        if (d1 >= 0) { Sym(0, d1, c.Lad); Sym(fd, d1, c.Lad); Sym(d1, d1, c.Lad + c.L1d); }
        if (q1 >= 0) { Sym(1, q1, c.Laq); Sym(q1, q1, c.Laq + c.L1q); }
        if (q2 >= 0) { Sym(1, q2, c.Laq); Sym(q1, q2, c.Laq); Sym(q2, q2, c.Laq + c.L2q); }
        Rd[0] = ra; Rd[1] = ra; Rd[2] = ra; Rd[fd] = c.Rfd;
        if (d1 >= 0) Rd[d1] = c.R1d;
        if (q1 >= 0) Rd[q1] = c.R1q;
        if (q2 >= 0) Rd[q2] = c.R2q;
        Smat[0 * N + 1] = 1.0;       // d: + w psi_q
        Smat[1 * N + 0] = -1.0;      // q: - w psi_d
        FinishModel();
    }

    /// <summary>
    /// Initialise at a steady operating point. <paramref name="vTermPu"/> and <paramref name="iOutPu"/> are phasors in pu of the peak bases
    /// (<c>v_a(t) = Vb Re(V e^(j w0 t))</c>, phase b lagging by 120 degrees), the current being delivered to the network. The q-axis is
    /// the internal voltage behind Xq: <c>E = V + (Ra + j Xq) I</c>; the d-axis angle is its argument minus 90 degrees.
    /// </summary>
    public SynchronousInitialState InitialiseFromPhasors(Complex vTermPu, Complex iOutPu)
    {
        SynchronousMachineCircuit c = _circ;
        double xq = c.Laq + c.Xl, xd = c.Lad + c.Xl, ra = _data.RaPu;
        Complex e = vTermPu + new Complex(ra, xq) * iOutPu;
        double theta0 = DetMath.Phase(e) - Math.PI / 2.0;
        Complex rot = DetMath.FromPolar(1.0, -theta0);
        Complex vdq = vTermPu * rot, idq = iOutPu * rot;
        double vd = vdq.Real, vq = vdq.Imaginary, id = idq.Real, iq = idq.Imaginary;
        double psiD = vq + ra * iq, psiQ = -(vd + ra * id);        // steady state, w = 1, generator convention
        double ifd = (psiD + xd * id) / c.Lad;
        // motor-convention stator currents are the negatives of the generator ones
        var xv = new double[N];
        xv[0] = -id; xv[1] = -iq; xv[FieldIndex] = ifd;
        double[] psi = new double[N];
        for (int i = 0; i < N; i++) { double s = 0.0; for (int j = 0; j < N; j++) s += Lmat[i * N + j] * xv[j]; psi[i] = s; }
        double efd = Rd[FieldIndex] * ifd;
        double te = psiQ * (-id) - psiD * (-iq);                    // generator torque psi_q xbar_d - psi_d xbar_q
        _tmInit = te; _efdInit = efd;
        SetInitial(psi, 1.0, theta0, vd, vq, 0.0, efd);
        return new SynchronousInitialState(theta0, DetMath.Phase(e) - DetMath.Phase(vTermPu), efd, ifd, te, id, iq);
    }

    /// <summary>Initialise open-circuit at terminal voltage <paramref name="vPu"/> (pu, zero phase) and no current.</summary>
    public SynchronousInitialState InitialiseNoLoad(double vPu) => InitialiseFromPhasors(new Complex(vPu, 0.0), Complex.Zero);

    private protected override double Predict(double t, double dt, double c)
    {
        double wp = Omega + dt * Alpha;
        Theta += W0 * dt * ((1.0 - c) * Omega + c * wp);
        return wp;
    }

    private protected override double FieldVoltageAt(double t) => FieldVoltagePu?.Invoke(t) ?? _efdInit;

    private protected override double Torque(double[] psi, double[] x) => psi[1] * x[0] - psi[0] * x[1];

    private protected override double Advance(double te, double t, double dt, double c)
    {
        double tm = MechanicalTorquePu?.Invoke(t) ?? _tmInit;
        double h2 = 2.0 * _h;
        double w = (Omega + dt * (1.0 - c) * Alpha + c * dt * (tm - te + _damp) / h2) / (1.0 + c * dt * _damp / h2);
        Alpha = (tm - te - _damp * (w - 1.0)) / h2;
        Omega = w;
        return w;
    }

    private protected override double ProbeSign => -1.0;

    internal override double ProbeStateAt(int channel)
    {
        double[] x = CurrentsPu, vs = StatorVoltagesPu;
        return channel switch
        {
            0 => Omega,
            1 => Theta - W0 * TimeNow,
            2 => Te,
            3 => x[FieldIndex],
            4 => Psi[0],
            5 => Psi[1],
            6 => -(vs[0] * x[0] + vs[1] * x[1] + 2.0 * vs[2] * x[2]),
            7 => Math.Sqrt(vs[0] * vs[0] + vs[1] * vs[1]),
            _ => double.NaN,
        };
    }
}
