using System.Numerics;
using GridSim.Core.Dynamics.Models.Wecc;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP20 (RC5): the two under-voltage trip stages of a CMLD three-phase motor (WECC Composite Load Model Specification,
/// "Three-phase Motor Model": Vtr1, Ttr1, Ftr1 and Vtr2, Ttr2, Ftr2). Each stage trips its fraction of the motor once the
/// far-end voltage stays below its Vtr for its Ttr; the fractions are cumulative and a tripped fraction stays out (the
/// reconnection pairs Vrc, Trc are not modelled). A stage with Ftr 0 does nothing.
/// </summary>
public sealed record MotorUnderVoltageRelay(double Vtr1, double Ttr1, double Ftr1, double Vtr2 = 0.0, double Ttr2 = 9999.0, double Ftr2 = 0.0)
{
    internal void Check(string who)
    {
        foreach ((string n, double x) in new[] { ("Vtr1", Vtr1), ("Ttr1", Ttr1), ("Vtr2", Vtr2), ("Ttr2", Ttr2) }) LibraryChecks.NonNegative(who, n, x);
        foreach ((string n, double x) in new[] { ("Ftr1", Ftr1), ("Ftr2", Ftr2) })
            if (!(x >= 0.0 && x <= 1.0)) throw LoadChecks.OutOfRange(n, x, $"{who}: {n} must lie in [0, 1].");
        if (!(Ftr1 + Ftr2 <= 1.0)) throw LoadChecks.OutOfRange(nameof(Ftr2), Ftr2, $"{who}: Ftr1 + Ftr2 must not exceed 1.");
    }
}

/// <summary>WP20 (RC5): a CMLD three-phase motor B or C: the same third-order motor as motor A
/// (<see cref="InductionMotorParameters"/>) taking <paramref name="Fraction"/> of the far-end power on a rating of that power
/// over <paramref name="LoadingFactor"/>, with optional under-voltage relays.</summary>
public sealed record CompositeLoadMotor(double Fraction, double LoadingFactor, InductionMotorParameters Motor, MotorUnderVoltageRelay? Relay = null);

/// <summary>
/// WP20 part B (RC5): the distributed generation of the composite load, a WECC DER_A model (<see cref="DeraParameters"/>, the
/// existing <see cref="DerAState"/> unchanged) at the far-end node. At the start it generates <paramref name="Fraction"/> of
/// the far-end load power (the power the load parts A to D, electronic and static draw, whose fractions still add up to 1),
/// so the far end's net draw is (1 - Fraction) of it and the bus draws its power-flow P and Q as before. Its reactive power
/// follows <paramref name="PowerFactor"/> (positive: the generation exports Q, negative: it absorbs Q; 1 gives Q = 0). Its
/// MVA rating is its starting power over <paramref name="LoadingFactor"/>, so DER_A starts at P0 = LoadingFactor on its own
/// base (inside [Pmin, Pmax]). Fraction must lie in [0, 1); DER_A's own start checks apply (vl1 &lt; V0 &lt; vh1 and the
/// rest, see <see cref="DerAState"/>). GridSim's definition of the share: the WECC specification sizes the CMLD generation in
/// MW, which here is Fraction times the far-end load power.
/// </summary>
public sealed record CompositeLoadGeneration(double Fraction, double LoadingFactor, DeraParameters Der, double PowerFactor = 1.0)
{
    internal double TanPhi => Math.Sqrt(1.0 - PowerFactor * PowerFactor) / Math.Abs(PowerFactor) * Math.Sign(PowerFactor);

    internal void Check(string who)
    {
        ArgumentNullException.ThrowIfNull(Der);
        if (!(Fraction >= 0.0 && Fraction < 1.0)) throw LoadChecks.OutOfRange(nameof(Fraction), Fraction, $"{who}: the distributed generation fraction must lie in [0, 1).");
        LibraryChecks.Positive(who, "distributed generation loading factor", LoadingFactor);
        if (!(PowerFactor >= -1.0 && PowerFactor <= 1.0 && PowerFactor != 0.0)) throw LoadChecks.OutOfRange(nameof(PowerFactor), PowerFactor, $"{who}: the distributed generation power factor must lie in [-1, 0) or (0, 1].");
        IReadOnlyList<string> bad = Der.Check();
        // Der is a member of this record, not a parameter of Check, so the message names it (CA2208).
        if (bad.Count > 0) throw new ArgumentException($"{who}: distributed generation (Der): " + string.Join("; ", bad));
    }
}

/// <summary>
/// WP20 (RC5): the parts of the WECC composite load the WP3b subset leaves out, attached through
/// <see cref="CompositeLoadData.Extensions"/>. With it null the composite load is the WP3b subset, bit for bit. With it set
/// the far-end power splits as FmA + FmB + FmC + FmD + Fel + Fstatic = 1: FmA is <see cref="CompositeLoadData.MotorFraction"/>
/// (0 allowed here), the static load takes the rest, and fractions that add up to more than 1 + 1e-12 are refused (the WECC
/// specification would scale them down; GridSim asks for data that adds up).
/// </summary>
public sealed record CompositeLoadExtensions
{
    /// <summary>Motor B, or null.</summary>
    public CompositeLoadMotor? MotorB { get; init; }

    /// <summary>Motor C, or null.</summary>
    public CompositeLoadMotor? MotorC { get; init; }

    /// <summary>Motor A's under-voltage relays, or null.</summary>
    public MotorUnderVoltageRelay? MotorARelay { get; init; }

    /// <summary>FmD, motor D's fraction (0 leaves it out).</summary>
    public double MotorDFraction { get; init; }

    /// <summary>Motor D, the single-phase air-conditioner performance model; needed when <see cref="MotorDFraction"/> is
    /// above 0.</summary>
    public AcMotorParameters? MotorD { get; init; }

    /// <summary>Fel, the electronic load's fraction (0 leaves it out).</summary>
    public double ElectronicFraction { get; init; }

    /// <summary>The electronic load; needed when <see cref="ElectronicFraction"/> is above 0.</summary>
    public ElectronicLoadParameters? Electronic { get; init; }

    /// <summary>The substation tap changer, or null for the fixed tap.</summary>
    public CompositeLoadLtcParameters? Ltc { get; init; }

    /// <summary>WP20 part B: the distributed generation at the far end (DER_A), or null for none. A fraction of 0 leaves it
    /// out.</summary>
    public CompositeLoadGeneration? Generation { get; init; }
}

/// <summary>
/// WP20 (RC5): the composite load with its extensions: the WP3b subset's internal network (substation transformer, feeder and
/// far-end node) carrying three-phase motors A to C, motor D, the electronic load and the static load, with relays and the
/// tap changer. The arithmetic follows the subset's step for step, adding each present part in the order A, B, C, D,
/// electronic, static and skipping absent ones, so a single three-phase motor gives the subset's bits.
/// <para>Motor D enters the far-end node as an admittance set from its power at the far-end voltage at the end of each step
/// and held over the next (at the start, at the initial voltage), in every run state: its run curve below Vbrk draws more as
/// the voltage falls, which a network solution cannot hold as a power. The GridPACK reference implementation of the WECC
/// model (Huang et al., arXiv 1708.00939, section III.D) holds the admittance from the last step only below Vbrk; GridSim
/// holds it above Vbrk as well, which adds a lag of one step there (first order in the step, like the rest of the
/// integration). The electronic and static loads are solved at the trial voltage.</para>
/// <para>The distributed generation (<see cref="CompositeLoadExtensions.Generation"/>) is a DER_A current source at the
/// far-end node: its current at the trial far-end voltage enters the internal network solve, and its states advance from
/// the far-end voltage at the start of each step, as the RMS kernel drives a DER_A unit at a bus.</para>
/// <para>The reactive balance against the power flow (the shunt at the system bus) is fixed at the start; the WECC rule
/// that trips it with the load components is not modelled.</para>
/// </summary>
internal static class CompositeLoadExtended
{
    private const int InitIterations = 200, SolveIterations = 100;
    private const double InitTolerance = 1e-14, SolveTolerance = 1e-13, FractionTolerance = 1e-12;

    /// <summary>One three-phase motor of the composite load (A, B or C).</summary>
    internal sealed class Motor3(string name, double fraction, double loadingFactor, InductionMotorParameters m, MotorUnderVoltageRelay? relay)
    {
        internal readonly string Name = name;
        internal readonly double Fraction = fraction, LoadingFactor = loadingFactor;
        internal readonly InductionMotorParameters Parameters = m;
        internal readonly MotorUnderVoltageRelay? Relay = relay;
        internal double K;   // motor MVA / system MVA
        internal Complex E0, I0;
        internal double W0, T0;
    }

    internal sealed class Setup
    {
        internal double Tap0, Xt, Bl, Bf, Vlow, StaticFraction, Pend, V0f, Ps0, Qs0, Pd0, Pe0, Qe0, V0l;
        internal Complex Zf;
        internal StaticLoadCharacteristic Sp = StaticLoadCharacteristic.ConstantPower, Sq = StaticLoadCharacteristic.ConstantPower;
        internal Motor3[] Motors = Array.Empty<Motor3>();
        internal AcMotorParameters? MotorD;
        internal double MotorDBase;   // motor D MVA / system MVA
        internal ElectronicLoadParameters? Electronic;
        internal CompositeLoadLtcParameters? Ltc;
        internal string Who = "";
        internal double Hz;
        internal int Bus;
        internal CompositeLoadGeneration? Generation;   // WP20 part B: null, or a fraction above 0
        internal double Pg0, Qg0;                       // its starting generation (system pu)
        internal Complex Vf0;                           // the starting far-end voltage phasor
    }

    internal static DynamicLoadPlan Plan(CompositeLoadData d, CompositeLoadExtensions x, Complex v0, double baseMva, double nominalHz)
    {
        string who = d.Label;
        ArgumentNullException.ThrowIfNull(d.MotorA);
        ArgumentNullException.ThrowIfNull(d.StaticP);
        ArgumentNullException.ThrowIfNull(d.StaticQ);
        d.StaticP.Check(who);
        d.StaticQ.Check(who);
        LibraryChecks.Positive(who, "PMw", d.PMw);
        LoadChecks.Finite(who, "QMvar", d.QMvar);
        LibraryChecks.Positive(who, "LoadMvaBase", d.LoadMvaBase);
        LibraryChecks.Positive(who, "Xxf (transformer reactance)", d.TransformerX);
        LibraryChecks.Positive(who, "transformer tap", d.TransformerTap);
        LoadChecks.Finite(who, "Bss", d.SubstationShuntB);
        LibraryChecks.NonNegative(who, "Rfdr", d.FeederR);
        LoadChecks.Finite(who, "Xfdr", d.FeederX);
        if (!(new Complex(d.FeederR, d.FeederX).Magnitude > 0.0)) throw LoadChecks.OutOfRange(nameof(d.FeederR), d.FeederR, $"{who}: the feeder impedance must not be 0 (give a small value).");
        LoadChecks.Finite(who, "feeder compensation B", d.FeederShuntB);
        if (!(d.FeederShuntFraction >= 0.0 && d.FeederShuntFraction <= 1.0)) throw LoadChecks.OutOfRange(nameof(d.FeederShuntFraction), d.FeederShuntFraction, $"{who}: Fb must lie in [0, 1].");
        if (!(d.StaticPowerFactor >= -1.0 && d.StaticPowerFactor <= 1.0 && d.StaticPowerFactor != 0.0)) throw LoadChecks.OutOfRange(nameof(d.StaticPowerFactor), d.StaticPowerFactor, $"{who}: the static power factor must lie in [-1, 0) or (0, 1].");
        LibraryChecks.NonNegative(who, "VLOW", d.LowVoltagePu);

        var motors = new List<Motor3>();
        void AddMotor(string name, double f, double lf, InductionMotorParameters? m, MotorUnderVoltageRelay? relay)
        {
            if (!(f >= 0.0 && f <= 1.0)) throw LoadChecks.OutOfRange(name + " fraction", f, $"{who}: the {name} fraction must lie in [0, 1].");
            relay?.Check($"{who} {name}");
            if (f == 0.0) return;
            ArgumentNullException.ThrowIfNull(m);
            m.Check();
            LibraryChecks.Positive(who, name + " loading factor", lf);
            motors.Add(new Motor3(name, f, lf, m, relay));
        }
        AddMotor("motor A", d.MotorFraction, d.MotorLoadingFactor, d.MotorA, x.MotorARelay);
        if (x.MotorB is { } mb) AddMotor("motor B", mb.Fraction, mb.LoadingFactor, mb.Motor, mb.Relay);
        if (x.MotorC is { } mc) AddMotor("motor C", mc.Fraction, mc.LoadingFactor, mc.Motor, mc.Relay);
        foreach ((string n, double f) in new[] { ("FmD", x.MotorDFraction), ("Fel", x.ElectronicFraction) })
            if (!(f >= 0.0 && f <= 1.0)) throw LoadChecks.OutOfRange(n, f, $"{who}: {n} must lie in [0, 1].");
        if (x.MotorDFraction > 0.0)
        {
            if (x.MotorD is null) throw new ArgumentException($"{who}: FmD is above 0 but motor D's data is missing.", nameof(x));
            x.MotorD.Check(who);
        }
        if (x.ElectronicFraction > 0.0)
        {
            if (x.Electronic is null) throw new ArgumentException($"{who}: Fel is above 0 but the electronic load's data is missing.", nameof(x));
            x.Electronic.Check(who);
        }
        x.Ltc?.Check(who, d.TransformerTap);
        x.Generation?.Check(who);
        CompositeLoadGeneration? dg = x.Generation is { Fraction: > 0.0 } g0 ? g0 : null;
        double sum = d.MotorFraction + (x.MotorB?.Fraction ?? 0.0) + (x.MotorC?.Fraction ?? 0.0) + x.MotorDFraction + x.ElectronicFraction;
        if (sum > 1.0 + FractionTolerance)
            throw LoadChecks.OutOfRange("fractions", sum, $"{who}: the far-end fractions FmA + FmB + FmC + FmD + Fel add up to {ControlBlocks.Num(sum)}, above 1.");
        double fStatic = 1.0 - d.MotorFraction;
        if (x.MotorB is { Fraction: > 0.0 }) fStatic -= x.MotorB.Fraction;
        if (x.MotorC is { Fraction: > 0.0 }) fStatic -= x.MotorC.Fraction;
        if (x.MotorDFraction > 0.0) fStatic -= x.MotorDFraction;
        if (x.ElectronicFraction > 0.0) fStatic -= x.ElectronicFraction;
        if (fStatic < 0.0) fStatic = 0.0;

        double zb = baseMva / d.LoadMvaBase;
        var s = new Setup
        {
            Tap0 = d.TransformerTap, Xt = d.TransformerX * zb, Bl = (d.SubstationShuntB + d.FeederShuntFraction * d.FeederShuntB) / zb,
            Bf = (1.0 - d.FeederShuntFraction) * d.FeederShuntB / zb, Zf = new Complex(d.FeederR, d.FeederX) * zb, Vlow = d.LowVoltagePu,
            StaticFraction = fStatic, Sp = d.StaticP, Sq = d.StaticQ, Motors = motors.ToArray(),
            MotorD = x.MotorDFraction > 0.0 ? x.MotorD : null, Electronic = x.ElectronicFraction > 0.0 ? x.Electronic : null,
            Ltc = x.Ltc, Who = who, Hz = nominalHz, Bus = d.BusId, Generation = dg,
        };
        double tanPhi = Math.Sqrt(1.0 - d.StaticPowerFactor * d.StaticPowerFactor) / Math.Abs(d.StaticPowerFactor) * Math.Sign(d.StaticPowerFactor);
        double ph = d.PMw / baseMva;
        Complex vf = v0 / d.TransformerTap;
        double pend = ph;
        Complex sh = Complex.Zero, vl = Complex.Zero;
        double pd = 0.0, qd = 0.0, pe = 0.0, pg = 0.0, qg = 0.0;
        bool settled = false;
        for (int it = 0; it < InitIterations; it++)
        {
            if (!(pend > 0.0)) throw new InvalidOperationException($"{who}: the far-end power fell to {ControlBlocks.Num(pend)} pu during the initialisation; the composite load cannot carry the bus power.");
            Complex iF = new Complex(0.0, s.Bf) * vf;
            foreach (Motor3 m in s.Motors)
            {
                m.K = m.Fraction * pend / m.LoadingFactor;
                (m.E0, m.W0, m.T0, m.I0) = InductionMotorState.Equilibrium(m.Parameters, m.LoadingFactor, vf, nominalHz, $"{who} {m.Name}");
                iF += m.I0 * m.K;
            }
            if (s.MotorD is { } md)
            {
                // Motor D on its MVA base (its power over LFm) draws LFm at the far-end voltage by the choice of Po.
                pd = x.MotorDFraction * pend;
                (_, double qo) = md.Initial(vf.Magnitude);
                double baseD = pd / md.LoadingFactor;
                qd = baseD * (qo + md.RunTerms(vf.Magnitude).Q);
                iF += Complex.Conjugate(new Complex(pd, qd) / vf);
            }
            if (s.Electronic is { } el)
            {
                pe = x.ElectronicFraction * pend;
                iF += Complex.Conjugate(new Complex(pe, pe * el.TanPhi) / vf);
            }
            double ps = s.StaticFraction * pend;
            Complex iStatic = Complex.Conjugate(new Complex(ps, ps * tanPhi) / vf);
            iF += iStatic;
            if (dg is not null)
            {
                // The generation: its share of the far-end load power, injected (a negative load).
                pg = dg.Fraction * pend;
                qg = pg * dg.TanPhi;
                iF -= Complex.Conjugate(new Complex(pg, qg) / vf);
            }
            vl = vf + s.Zf * iF;
            Complex iT = new Complex(0.0, s.Bl) * vl + iF;
            Complex vh = d.TransformerTap * (vl + new Complex(0.0, s.Xt) * iT);
            Complex ih = iT / d.TransformerTap;
            sh = vh * Complex.Conjugate(ih);
            double dv = (vh - v0).Magnitude, dp = sh.Real - ph;
            if (dv < InitTolerance && Math.Abs(dp) < InitTolerance * Math.Max(1.0, Math.Abs(ph))) { settled = true; s.Ps0 = ps; s.Qs0 = ps * tanPhi; break; }
            vf *= v0 / vh;
            // With generation the bus power moves by about (1 - Fdg) per unit of far-end load power.
            if (dg is null) pend -= dp;
            else pend -= dp / (1.0 - dg.Fraction);
        }
        if (!settled)
            throw new InvalidOperationException($"{who}: the composite load did not settle at the solved bus voltage in {InitIterations} iterations; check the transformer, feeder and motor data.");
        s.Pend = pend;
        s.V0f = vf.Magnitude;
        s.V0l = vl.Magnitude;
        if (s.StaticFraction > 0.0 && !(s.V0f >= d.LowVoltagePu))
            throw new InvalidOperationException($"{who}: the far-end voltage {ControlBlocks.Num(s.V0f)} pu is below VLOW, so the static load would start on its low-voltage branch.");
        if (s.Electronic is { } e0 && !(s.V0f >= e0.Vd1))
            throw new InvalidOperationException($"{who}: the far-end voltage {ControlBlocks.Num(s.V0f)} pu is below the electronic load's Vd1, so it would start partly tripped.");
        if (s.MotorD is { } d0)
        {
            s.MotorDBase = pd / d0.LoadingFactor;
            s.Pd0 = pd;
        }
        s.Pe0 = pe;
        s.Qe0 = s.Electronic is { } e1 ? pe * e1.TanPhi : 0.0;
        s.Pg0 = pg;
        s.Qg0 = qg;
        s.Vf0 = vf;
        // A quiet start: no relay, contactor or tap changer may act with no disturbance (motor D's own start checks are in
        // its state; the generation's in DER_A's).
        foreach (Motor3 m in s.Motors)
            if (m.Relay is { } r && ((r.Ftr1 > 0.0 && s.V0f < r.Vtr1) || (r.Ftr2 > 0.0 && s.V0f < r.Vtr2)))
                throw new InvalidOperationException($"{who}: the far-end voltage {ControlBlocks.Num(s.V0f)} pu is below the {m.Name} under-voltage relay setting "
                    + $"(Vtr1 {ControlBlocks.Num(r.Vtr1)}, Vtr2 {ControlBlocks.Num(r.Vtr2)}), so it would trip with no disturbance.");
        if (s.Ltc is { } lt && (s.V0l < lt.Vmin || s.V0l > lt.Vmax))
            throw new InvalidOperationException($"{who}: the low-side voltage {ControlBlocks.Num(s.V0l)} pu lies outside the LTC band [{ControlBlocks.Num(lt.Vmin)}, "
                + $"{ControlBlocks.Num(lt.Vmax)}], so the tap would move with no disturbance; give a band that holds the starting voltage, or a starting tap that puts it there.");

        // Fresh run states from the plan: motor D's state checks its start (refusing a start at or below the stall voltage).
        var probe = new State(s, Complex.Zero, Complex.Zero, vf);
        Complex yF = new Complex(0.0, s.Bf);
        foreach (Motor3 m in s.Motors) yF += Complex.One / new Complex(m.Parameters.Ra, m.Parameters.Xp) * m.K;
        yF += probe.MotorDAdmittance;
        if (s.Electronic is not null) yF += Complex.Conjugate(new Complex(s.Pe0, s.Qe0)) / (s.V0f * s.V0f);
        if (s.StaticFraction > 0.0) yF += Complex.Conjugate(new Complex(s.Ps0, s.Qs0)) / (s.V0f * s.V0f);
        double vh2 = v0.Magnitude * v0.Magnitude;
        Complex yComp = new Complex(0.0, -(d.QMvar / baseMva - sh.Imaginary)) / vh2;
        Complex yL = new Complex(0.0, s.Bl) + Complex.One / (s.Zf + Complex.One / yF);
        Complex yH = Complex.One / (new Complex(0.0, s.Xt) + Complex.One / yL) / (d.TransformerTap * d.TransformerTap);
        return new Plan_(s, yH + yComp, yComp, vf);
    }

    private sealed class Plan_(Setup s, Complex yN, Complex yComp, Complex vf0) : DynamicLoadPlan
    {
        internal override Complex NortonAdmittance => yN;

        internal override IRmsConverterModel CreateModel() => new State(s, yN, yComp, vf0);
    }

    /// <summary>A three-phase motor's run state: the motor and its relay.</summary>
    internal sealed class MotorRun
    {
        internal readonly Motor3 Def;
        internal readonly InductionMotorState Motor;
        private long _n1, _n2;   // whole steps below each stage's voltage
        private bool _trip1, _trip2;

        internal MotorRun(Motor3 def, double hz)
        {
            Def = def;
            Motor = new InductionMotorState(def.Parameters, hz, def.E0, def.W0, def.T0);
        }

        /// <summary>The fraction still connected (1 less the tripped stages).</summary>
        internal double Connected { get; private set; } = 1.0;

        /// <summary>The motor's MVA on the system base times its connected fraction.</summary>
        internal double K => Connected == 1.0 ? Def.K : Def.K * Connected;

        internal void Relay(double v, double dt)
        {
            if (Def.Relay is not { } r) return;
            if (!_trip1 && r.Ftr1 > 0.0)
            {
                if (v < r.Vtr1) { if (StepTimer.Reached(++_n1, r.Ttr1, dt)) _trip1 = true; } else _n1 = 0;
            }
            if (!_trip2 && r.Ftr2 > 0.0)
            {
                if (v < r.Vtr2) { if (StepTimer.Reached(++_n2, r.Ttr2, dt)) _trip2 = true; } else _n2 = 0;
            }
            Connected = 1.0 - (_trip1 ? r.Ftr1 : 0.0) - (_trip2 ? r.Ftr2 : 0.0);
        }
    }

    /// <summary>The running composite load with its extensions.</summary>
    internal sealed class State : DynamicLoadState
    {
        private readonly Setup _s;
        private readonly Complex _yComp;
        private readonly MotorRun[] _motors;
        private readonly AcMotorState? _motorD;
        private readonly CompositeLoadLtc? _ltc;
        private readonly DerAState? _dg;
        private Complex _vf, _yD;
        private double _vminEl, _t;

        internal State(Setup s, Complex yN, Complex yComp, Complex vf0) : base(yN)
        {
            _s = s;
            _yComp = yComp;
            _vf = vf0;
            _motors = s.Motors.Select(m => new MotorRun(m, s.Hz)).ToArray();
            if (s.MotorD is { } md)
            {
                _motorD = new AcMotorState(md, s.V0f, s.Who);
                _yD = MotorDAdmittanceAt(s.V0f);
            }
            if (s.Ltc is { } ltc) _ltc = new CompositeLoadLtc(ltc, s.Tap0);
            if (s.Generation is { } g)
            {
                // The DER_A unit at the far-end node: rated at its starting power over its loading factor (system pu).
                var unit = new RmsConverterUnit(s.Bus, s.Who + " distributed generation", s.Pg0 / g.LoadingFactor, s.Pg0, s.Qg0, s.Vf0,
                    new RmsConverterParameters(NominalHz: s.Hz)) { Wecc = WeccPlant.DerA(g.Der, "CMLD distributed generation") };
                _dg = new DerAState(unit);
            }
            _vminEl = s.V0f;
            LowSideVoltage = s.V0l;
        }

        /// <summary>The distributed generation's DER_A state, or null.</summary>
        internal DerAState? Generation => _dg;

        /// <summary>The three-phase motors present, in the order A, B, C.</summary>
        internal IReadOnlyList<MotorRun> Motors => _motors;

        /// <summary>Motor D's state, or null.</summary>
        internal AcMotorState? MotorD => _motorD;

        /// <summary>The tap changer, or null.</summary>
        internal CompositeLoadLtc? Ltc => _ltc;

        /// <summary>The present ratio.</summary>
        internal double Tap => _ltc?.Tap ?? _s.Tap0;

        internal Complex FarEndVoltage => _vf;

        /// <summary>The low-side voltage at the end of the last step (the initial one before any step).</summary>
        internal double LowSideVoltage { get; private set; }

        /// <summary>The electronic load's held Vmin.</summary>
        internal double ElectronicVmin => _vminEl;

        /// <summary>The electronic load's connected fraction at far-end |V| = <paramref name="vm"/>.</summary>
        internal double ElectronicFraction(double vm) => _s.Electronic?.Fraction(vm, _vminEl) ?? 0.0;

        /// <summary>Motor D's admittance held over the next step (system pu).</summary>
        internal Complex MotorDAdmittance => _yD;

        /// <summary>Motor D's power on the system base at far-end |V| = <paramref name="vm"/>, its states held.</summary>
        internal Complex MotorDPower(double vm)
        {
            if (_motorD is null) return Complex.Zero;
            (double p, double q) = _motorD.Power(vm);
            return new Complex(p, q) * _s.MotorDBase;
        }

        private Complex MotorDAdmittanceAt(double vm) => vm > 0.0 ? Complex.Conjugate(MotorDPower(vm)) / (vm * vm) : _yD;

        private Complex StaticPower(double vm)
        {
            (double ve, double scale) = LoadChecks.LowVoltage(vm, _s.Vlow);
            double u = ve / _s.V0f;
            return new Complex(_s.Ps0 * _s.Sp.Factor(u) * scale, _s.Qs0 * _s.Sq.Factor(u) * scale);
        }

        /// <summary>The motors' EMF sources (E' Y k), in the order A, B, C.</summary>
        private Complex Source()
        {
            Complex src = Complex.Zero;
            for (int i = 0; i < _motors.Length; i++)
            {
                MotorRun m = _motors[i];
                Complex x = m.Motor.Emf * m.Motor.Admittance * m.K;
                src = i == 0 ? x : src + x;
            }
            return src;
        }

        /// <summary>The internal network at bus voltage <paramref name="vh"/> (the subset's fixed iteration, with the motors,
        /// motor D's held admittance, the electronic load and the static load at the far end).</summary>
        internal (Complex Vl, Complex Vf, Complex Ih) Solve(Complex vh, Complex source, Complex vfGuess)
        {
            double n = Tap;
            Complex vlp = vh / n;
            Complex yt = Complex.One / new Complex(0.0, _s.Xt), yf = Complex.One / _s.Zf;
            Complex vf = vfGuess, vl = Complex.Zero;
            for (int it = 0; it < SolveIterations; it++)
            {
                double vm = vf.Magnitude;
                Complex a22 = yf + new Complex(0.0, _s.Bf);
                foreach (MotorRun m in _motors) a22 += Complex.One / new Complex(m.Def.Parameters.Ra, m.Def.Parameters.Xp) * m.K;
                if (_motorD is not null) a22 += _yD;
                if (_s.Electronic is { } el && vm > 0.0)
                {
                    double f = el.Fraction(vm, _vminEl);
                    if (f > 0.0) a22 += Complex.Conjugate(new Complex(_s.Pe0, _s.Qe0) * f) / (vm * vm);
                }
                Complex ys = Complex.Zero;
                if (_s.StaticFraction > 0.0 && vm > 0.0) ys = Complex.Conjugate(StaticPower(vm)) / (vm * vm);
                a22 += ys;
                Complex a11 = yt + new Complex(0.0, _s.Bl) + yf, a12 = -yf;
                Complex det = a11 * a22 - a12 * a12;
                Complex b1 = yt * vlp, b2 = _dg is null ? source : source + _dg.TrialCurrent(vf);
                Complex nl = (b1 * a22 - a12 * b2) / det, nf = (a11 * b2 - a12 * b1) / det;
                double change = (nf - vf).Magnitude;
                vl = nl; vf = nf;
                if (change < SolveTolerance) break;
            }
            if (!double.IsFinite(vf.Real) || !double.IsFinite(vf.Imaginary))
                throw new InvalidOperationException($"{_s.Who}: the internal network solution failed (non-finite far-end voltage).");
            Complex ih = (vlp - vl) * yt / n;
            return (vl, vf, ih);
        }

        protected override Complex Drawn(Complex v)
        {
            (_, _, Complex ih) = Solve(v, Source(), _vf);
            return ih + _yComp * v;
        }

        protected override void Advance(Complex v, double dt)
        {
            (Complex vl, Complex vf, _) = Solve(v, Source(), _vf);
            _vf = vf;
            foreach (MotorRun m in _motors) m.Motor.Advance(vf, dt);
            double vm = vf.Magnitude;
            LowSideVoltage = vl.Magnitude;
            foreach (MotorRun m in _motors) m.Relay(vm, dt);
            if (_motorD is not null)
            {
                _motorD.Advance(vm, dt);
                _yD = MotorDAdmittanceAt(vm);
            }
            if (_s.Electronic is { } el) _vminEl = el.NextVmin(_vminEl, vm);
            _ltc?.Advance(LowSideVoltage, dt);
            if (_dg is not null)
            {
                // As the kernel drives a DER_A unit: commit at the step's far-end voltage, then advance from it.
                _dg.Commit(vf);
                _dg.Step(_t, vf, [vf], dt);
            }
        }

        protected override void Advance(double t, Complex v, double dt)
        {
            _t = t;
            Advance(v, dt);
        }
    }
}
