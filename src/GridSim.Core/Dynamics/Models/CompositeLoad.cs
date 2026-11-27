using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP3b (RC5): a documented subset of the WECC composite load model (CMLD; PSS/E CMLDBLU1 data sheet 9.6, public copy at
/// pdfcoffee.com/models-pdf-free.html). Modelled: the substation transformer (an ideal fixed ratio
/// <paramref name="TransformerTap"/> high to low, then <paramref name="TransformerX"/>), the substation shunt
/// <paramref name="SubstationShuntB"/>, the feeder <paramref name="FeederR"/> + j<paramref name="FeederX"/> with its
/// compensation <paramref name="FeederShuntB"/> split <paramref name="FeederShuntFraction"/> at the substation end and the
/// rest at the far end, and at the far end motor A (a third-order motor, <see cref="InductionMotorParameters"/>, taking
/// <paramref name="MotorFraction"/> of the far-end power on a rating of that power over <paramref name="MotorLoadingFactor"/>)
/// with the static load (the rest, <paramref name="StaticP"/> and <paramref name="StaticQ"/>, the reactive power from the
/// power factor <paramref name="StaticPowerFactor"/>, positive lagging). Impedances and susceptances are per unit on
/// <paramref name="LoadMvaBase"/>.
/// <para><b>Not modelled</b> in the subset on its own (<see cref="Extensions"/> adds motors B to D, the electronic load,
/// the relays and contactors, the LTC and DER_A distributed generation): motors B, C and D, the single-phase air-conditioner motor (the CMLD motor D and its stall,
/// restart and thermal logic), the electronic load, the distributed generation, the load tap changer (LTC: the tap is
/// fixed), the motor under-voltage relays and contactors, the frequency dependence of the static load, and motor A's
/// subtransient level (X'' = X'). Where the WECC model computes the feeder compensation and the LTC tap at initialisation,
/// this subset takes them as data; the reactive power left over against the power flow goes into a shunt at the system
/// bus (as PSS/E's CIM5 does), so the CMLD record itself is not read from a DYR file.</para>
/// <para><b>Initialisation</b>: the far-end voltage and power are found by a fixed iteration (scaling the far-end voltage by
/// V_bus / V_computed and adding the active-power error to the far-end power) until the bus voltage and the bus active
/// power match the solved values to 1e-14; a composite load that does not settle in 200 iterations is refused.</para>
/// </summary>
public sealed record CompositeLoadData(int BusId, double PMw, double QMvar, double LoadMvaBase,
    double TransformerX, double TransformerTap, double SubstationShuntB,
    double FeederR, double FeederX, double FeederShuntB, double FeederShuntFraction,
    double MotorFraction, double MotorLoadingFactor, InductionMotorParameters MotorA,
    double StaticPowerFactor, StaticLoadCharacteristic StaticP, StaticLoadCharacteristic StaticQ,
    double LowVoltagePu = 0.7) : DynamicLoadData(BusId, PMw, QMvar)
{
    /// <inheritdoc/>
    public override string Model => "CMLD subset";

    private const int InitIterations = 200, SolveIterations = 100;
    private const double InitTolerance = 1e-14, SolveTolerance = 1e-13;

    /// <summary>The internal network at a bus voltage, in system pu (shared by the initialisation and the run).</summary>
    internal sealed class Network
    {
        internal double N, Xt, Bl, Bf, Vlow, StaticFraction, Pend, V0f;
        internal Complex Zf, Ymotor;
        internal StaticLoadCharacteristic Sp = StaticLoadCharacteristic.ConstantPower, Sq = StaticLoadCharacteristic.ConstantPower;
        internal double Ps0, Qs0;   // the static load at the initial far-end voltage (system pu)

        /// <summary>The static load's power at far-end |V| = <paramref name="vm"/>.</summary>
        internal Complex StaticPower(double vm)
        {
            (double ve, double scale) = LoadChecks.LowVoltage(vm, Vlow);
            double u = ve / V0f;
            return new Complex(Ps0 * Sp.Factor(u) * scale, Qs0 * Sq.Factor(u) * scale);
        }

        /// <summary>Solve the substation and far-end voltages for bus voltage <paramref name="vh"/> and motor EMF source
        /// <paramref name="source"/> (= E' Ymotor, system pu); returns (V_L, V_F, the current drawn from the bus).</summary>
        internal (Complex Vl, Complex Vf, Complex Ih) Solve(Complex vh, Complex source, Complex vfGuess)
        {
            Complex vlp = vh / N;
            Complex yt = Complex.One / new Complex(0.0, Xt), yf = Complex.One / Zf;
            Complex vf = vfGuess, vl = Complex.Zero;
            for (int it = 0; it < SolveIterations; it++)
            {
                double vm = vf.Magnitude;
                Complex ys = Complex.Zero;
                if (StaticFraction > 0.0 && vm > 0.0) ys = Complex.Conjugate(StaticPower(vm)) / (vm * vm);
                // Nodes L and F: [yt + jBl + yf, -yf; -yf, yf + jBf + Ymotor + ys] [VL; VF] = [yt VLp; source].
                Complex a11 = yt + new Complex(0.0, Bl) + yf, a12 = -yf, a22 = yf + new Complex(0.0, Bf) + Ymotor + ys;
                Complex det = a11 * a22 - a12 * a12;
                Complex b1 = yt * vlp, b2 = source;
                Complex nl = (b1 * a22 - a12 * b2) / det, nf = (a11 * b2 - a12 * b1) / det;
                double change = (nf - vf).Magnitude;
                vl = nl; vf = nf;
                if (change < SolveTolerance) break;
            }
            Complex ih = (vlp - vl) * yt / N;
            return (vl, vf, ih);
        }
    }

    /// <summary>
    /// WP20 (RC5): the parts of the full WECC composite load this subset leaves out (motors B, C and D, the electronic load,
    /// motor under-voltage relays and the substation tap changer; see <see cref="CompositeLoadExtensions"/>). Null (the
    /// default) is the subset above, bit for bit.
    /// </summary>
    public CompositeLoadExtensions? Extensions { get; init; }

    internal override DynamicLoadPlan Plan(Complex v0, double baseMva, double nominalHz)
    {
        if (Extensions is not null) return CompositeLoadExtended.Plan(this, Extensions, v0, baseMva, nominalHz);
        string who = Label;
        ArgumentNullException.ThrowIfNull(MotorA);
        ArgumentNullException.ThrowIfNull(StaticP);
        ArgumentNullException.ThrowIfNull(StaticQ);
        MotorA.Check();
        StaticP.Check(who);
        StaticQ.Check(who);
        LibraryChecks.Positive(who, "PMw", PMw);
        LoadChecks.Finite(who, "QMvar", QMvar);
        LibraryChecks.Positive(who, "LoadMvaBase", LoadMvaBase);
        LibraryChecks.Positive(who, "Xxf (transformer reactance)", TransformerX);
        LibraryChecks.Positive(who, "transformer tap", TransformerTap);
        LoadChecks.Finite(who, "Bss", SubstationShuntB);
        LibraryChecks.NonNegative(who, "Rfdr", FeederR);
        LoadChecks.Finite(who, "Xfdr", FeederX);
        if (!(new Complex(FeederR, FeederX).Magnitude > 0.0)) throw LoadChecks.OutOfRange("FeederR", FeederR, $"{who}: the feeder impedance must not be 0 (give a small value).");
        LoadChecks.Finite(who, "feeder compensation B", FeederShuntB);
        if (!(FeederShuntFraction >= 0.0 && FeederShuntFraction <= 1.0)) throw LoadChecks.OutOfRange("FeederShuntFraction", FeederShuntFraction, $"{who}: Fb must lie in [0, 1].");
        if (!(MotorFraction > 0.0 && MotorFraction <= 1.0)) throw LoadChecks.OutOfRange("MotorFraction", MotorFraction, $"{who}: the motor A fraction must lie in (0, 1].");
        LibraryChecks.Positive(who, "motor loading factor", MotorLoadingFactor);
        if (!(StaticPowerFactor >= -1.0 && StaticPowerFactor <= 1.0 && StaticPowerFactor != 0.0)) throw LoadChecks.OutOfRange("StaticPowerFactor", StaticPowerFactor, $"{who}: the static power factor must lie in [-1, 0) or (0, 1].");
        LibraryChecks.NonNegative(who, "VLOW", LowVoltagePu);

        double zb = baseMva / LoadMvaBase;   // load-base impedance -> system base: Z_sys = Z_load * Sb / Sload
        var net = new Network
        {
            N = TransformerTap, Xt = TransformerX * zb, Bl = (SubstationShuntB + FeederShuntFraction * FeederShuntB) / zb,
            Bf = (1.0 - FeederShuntFraction) * FeederShuntB / zb, Zf = new Complex(FeederR, FeederX) * zb, Vlow = LowVoltagePu,
            StaticFraction = 1.0 - MotorFraction, Sp = StaticP, Sq = StaticQ,
        };
        double tanPhi = Math.Sqrt(1.0 - StaticPowerFactor * StaticPowerFactor) / Math.Abs(StaticPowerFactor) * Math.Sign(StaticPowerFactor);
        double ph = PMw / baseMva;
        Complex vf = v0 / TransformerTap;
        double pend = ph;
        Complex e0 = Complex.Zero, im0 = Complex.Zero;
        double w0 = 1.0, t0 = 0.0, k = 0.0;
        Complex sh = Complex.Zero, vl = Complex.Zero;
        bool settled = false;
        for (int it = 0; it < InitIterations; it++)
        {
            // Far end at (vf, pend): motor A on a rating of its power over the loading factor, so on its own base it always
            // draws the loading factor; the static load at the far-end voltage.
            k = MotorFraction * pend / MotorLoadingFactor;   // motor MVA / system MVA
            if (!(k > 0.0)) throw new InvalidOperationException($"{who}: the far-end power fell to {ControlBlocks.Num(pend)} pu during the initialisation; the composite load cannot carry the bus power.");
            (e0, w0, t0, im0) = InductionMotorState.Equilibrium(MotorA, MotorLoadingFactor, vf, nominalHz, who);
            double ps = (1.0 - MotorFraction) * pend;
            Complex iStatic = Complex.Conjugate(new Complex(ps, ps * tanPhi) / vf);
            Complex iF = new Complex(0.0, net.Bf) * vf + im0 * k + iStatic;
            vl = vf + net.Zf * iF;
            Complex iT = new Complex(0.0, net.Bl) * vl + iF;
            Complex vh = TransformerTap * (vl + new Complex(0.0, net.Xt) * iT);
            Complex ih = iT / TransformerTap;
            sh = vh * Complex.Conjugate(ih);
            double dv = (vh - v0).Magnitude, dp = sh.Real - ph;
            if (dv < InitTolerance && Math.Abs(dp) < InitTolerance * Math.Max(1.0, Math.Abs(ph))) { settled = true; net.Ps0 = ps; net.Qs0 = ps * tanPhi; break; }
            vf *= v0 / vh;
            pend -= dp;
        }
        if (!settled)
            throw new InvalidOperationException($"{who}: the composite load did not settle at the solved bus voltage in {InitIterations} iterations; check the transformer, feeder and motor data.");
        net.Pend = pend;
        net.V0f = vf.Magnitude;
        if (!(net.V0f >= LowVoltagePu))
            throw new InvalidOperationException($"{who}: the far-end voltage {ControlBlocks.Num(net.V0f)} pu is below VLOW, so the static load would start on its low-voltage branch.");
        Complex ymotor = Complex.One / new Complex(MotorA.Ra, MotorA.Xp) * k;
        net.Ymotor = ymotor;
        double vh2 = v0.Magnitude * v0.Magnitude;
        Complex yComp = new Complex(0.0, -(QMvar / baseMva - sh.Imaginary)) / vh2;
        // The linear driving-point admittance at the bus (the static load at its initial admittance), the Norton part.
        Complex ys0 = net.StaticFraction > 0.0 ? Complex.Conjugate(new Complex(net.Ps0, net.Qs0)) / (net.V0f * net.V0f) : Complex.Zero;
        Complex yF = new Complex(0.0, net.Bf) + ymotor + ys0;
        Complex yL = new Complex(0.0, net.Bl) + Complex.One / (net.Zf + Complex.One / yF);
        Complex yH = Complex.One / (new Complex(0.0, net.Xt) + Complex.One / yL) / (TransformerTap * TransformerTap);
        return new Plan_(net, MotorA, nominalHz, e0, w0, t0, k, yH + yComp, yComp, vf);
    }

    private sealed class Plan_(Network net, InductionMotorParameters m, double hz, Complex e0, double w0, double t0, double k,
        Complex yN, Complex yComp, Complex vf0) : DynamicLoadPlan
    {
        internal override Complex NortonAdmittance => yN;

        internal override IRmsConverterModel CreateModel() => new State(net, new InductionMotorState(m, hz, e0, w0, t0), k, yN, yComp, vf0);
    }

    /// <summary>The running composite load: the internal network is solved at each trial bus voltage (motor EMF held).</summary>
    internal sealed class State(Network net, InductionMotorState motor, double k, Complex yN, Complex yComp, Complex vf0) : DynamicLoadState(yN)
    {
        private Complex _vf = vf0;

        internal InductionMotorState Motor => motor;

        internal Complex FarEndVoltage => _vf;

        protected override Complex Drawn(Complex v)
        {
            (_, _, Complex ih) = net.Solve(v, motor.Emf * motor.Admittance * k, _vf);
            return ih + yComp * v;
        }

        protected override void Advance(Complex v, double dt)
        {
            (_, Complex vf, _) = net.Solve(v, motor.Emf * motor.Admittance * k, _vf);
            _vf = vf;
            motor.Advance(vf, dt);
        }
    }
}
