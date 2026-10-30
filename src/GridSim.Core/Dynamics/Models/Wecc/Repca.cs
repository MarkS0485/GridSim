using System;
using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Models.Wecc;

/// <summary>
/// REPC_A (PSS/E REPCA1): the renewable energy plant controller, after EPRI 3002006525 Fig. 3-5. Two independent paths:
/// the reactive path regulates a voltage (RefFlag = 1; with line drop compensation or reactive droop) or a branch
/// reactive power (RefFlag = 0) and gives Qext to the REEC model; the active path (Fflag = 1) gives Pref with primary
/// frequency response (separate droops Ddn / Dup and a deadband). All P and Q quantities are per unit on the unit's
/// MBASE (DECISIONS D16); voltages in per unit.
/// </summary>
public sealed record RepcaParameters
{
    /// <summary>Regulated bus (BUSR): 0 = the unit's own terminal.</summary>
    public int RegulatedBus { get; init; }
    /// <summary>From bus of the branch whose flow is the plant output (BUS1): 0 = the unit's own P and Q.</summary>
    public int FlowFromBus { get; init; }
    /// <summary>To bus of that branch (BUS2).</summary>
    public int FlowToBus { get; init; }
    /// <summary>Circuit identifier of that branch (CKTID).</summary>
    public string FlowCircuit { get; init; } = "1";
    /// <summary>The branch's index in <c>model.Branches</c>, resolved when the record is bound (-1: no branch).</summary>
    public int FlowBranchIndex { get; init; } = -1;
    /// <summary>1 = line drop compensation |Vreg - (Rc + jXc) Ibranch|, 0 = reactive droop Vreg + Kc Qbranch.</summary>
    public int VcFlag { get; init; }
    /// <summary>1 = voltage control, 0 = branch reactive power control.</summary>
    public int RefFlag { get; init; } = 1;
    /// <summary>1 = active power / frequency control on (Pref driven by this model), 0 = Pref held.</summary>
    public int Fflag { get; init; }
    /// <summary>Voltage and reactive power measurement filter (s).</summary>
    public double Tfltr { get; init; } = 0.02;
    /// <summary>The default of <see cref="FrequencyFilterSeconds"/> (s), DECISIONS D15.</summary>
    public const double DefaultFrequencyFilterSeconds = 0.02;
    /// <summary>
    /// Time constant (s) of the first-order lag on GridSim's estimate of the frequency the controller regulates (the backward
    /// difference of the regulated voltage angle over each step), default <see cref="DefaultFrequencyFilterSeconds"/> = 0.02 s;
    /// 0 uses the unfiltered estimate. Not a WECC REPC_A parameter (the specification takes a measured frequency as its input,
    /// as PSS/E takes a bus frequency estimate), so no DYR record carries it: set it on the parameters (DECISIONS D15). It moves
    /// nothing at a steady state, where the estimate is constant; it shapes only how fast a frequency change, and the
    /// one-step angle spike of a network event, reach the frequency control.
    /// </summary>
    public double FrequencyFilterSeconds { get; init; } = DefaultFrequencyFilterSeconds;
    public double Kp { get; init; } = 18.0;
    public double Ki { get; init; } = 5.0;
    /// <summary>Lead time constant of the output lead-lag (s).</summary>
    public double Tft { get; init; }
    /// <summary>Lag time constant of the output lead-lag (s).</summary>
    public double Tfv { get; init; } = 0.075;
    /// <summary>Voltage below which the reactive PI integral is frozen (pu).</summary>
    public double Vfrz { get; init; }
    public double Rc { get; init; }
    public double Xc { get; init; }
    public double Kc { get; init; }
    /// <summary>Upper / lower limits on the deadbanded error.</summary>
    public double Emax { get; init; } = 0.1;
    public double Emin { get; init; } = -0.1;
    public double Dbd1 { get; init; }
    public double Dbd2 { get; init; }
    public double Qmax { get; init; } = 0.436;
    public double Qmin { get; init; } = -0.436;
    public double Kpg { get; init; } = 0.1;
    public double Kig { get; init; } = 0.05;
    /// <summary>Active power filter (s).</summary>
    public double Tp { get; init; } = 0.25;
    public double Fdbd1 { get; init; }
    public double Fdbd2 { get; init; }
    public double Femax { get; init; } = 999.0;
    public double Femin { get; init; } = -999.0;
    public double Pmax { get; init; } = 1.0;
    public double Pmin { get; init; }
    /// <summary>Lag on the active power reference (s), "Tlag" / "Tg" in the parameter lists.</summary>
    public double Tg { get; init; } = 0.1;
    /// <summary>Down-regulation droop gain (over-frequency, pu power per pu frequency).</summary>
    public double Ddn { get; init; } = 20.0;
    /// <summary>Up-regulation droop gain (under-frequency).</summary>
    public double Dup { get; init; }

    /// <summary>Every problem with these values (empty when usable).</summary>
    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        const string m = "REPC_A";
        if (RegulatedBus < 0) p.Add($"{m} BUSR = {RegulatedBus}: must be 0 (own terminal) or a bus number");
        if ((FlowFromBus == 0) != (FlowToBus == 0)) p.Add($"{m} BUS1 = {FlowFromBus}, BUS2 = {FlowToBus}: give both branch buses or neither");
        WeccCheck.Flag(p, m, "VCFlag", VcFlag, 0, 1);
        WeccCheck.Flag(p, m, "RefFlag", RefFlag, 0, 1);
        WeccCheck.Flag(p, m, "Fflag", Fflag, 0, 1);
        WeccCheck.AtLeast(p, m, "Tfltr", Tfltr, 0.0);
        WeccCheck.AtLeast(p, m, "FrequencyFilterSeconds", FrequencyFilterSeconds, 0.0);
        WeccCheck.Finite(p, m, "Kp", Kp);
        WeccCheck.Finite(p, m, "Ki", Ki);
        WeccCheck.AtLeast(p, m, "Tft", Tft, 0.0);
        WeccCheck.AtLeast(p, m, "Tfv", Tfv, 0.0);
        if (double.IsFinite(Tft) && double.IsFinite(Tfv) && Tft > 0.0 && Tfv <= WeccBlocks.InstantSeconds)
            p.Add($"{m} Tft = {WeccCheck.N(Tft)} with Tfv = 0: a lead with no lag is a pure derivative, which the lead-lag cannot realise");
        WeccCheck.Finite(p, m, "Vfrz", Vfrz);
        WeccCheck.Finite(p, m, "Rc", Rc);
        WeccCheck.Finite(p, m, "Xc", Xc);
        WeccCheck.Finite(p, m, "Kc", Kc);
        WeccCheck.Finite(p, m, "emax", Emax);
        WeccCheck.Finite(p, m, "emin", Emin);
        WeccCheck.Ordered(p, m, "emin", Emin, "emax", Emax);
        WeccCheck.Finite(p, m, "dbd1", Dbd1);
        WeccCheck.Finite(p, m, "dbd2", Dbd2);
        if (Dbd1 > 0.0) p.Add($"{m} dbd1 = {WeccCheck.N(Dbd1)}: must be 0 or negative");
        if (Dbd2 < 0.0) p.Add($"{m} dbd2 = {WeccCheck.N(Dbd2)}: must be 0 or positive");
        WeccCheck.Finite(p, m, "Qmax", Qmax);
        WeccCheck.Finite(p, m, "Qmin", Qmin);
        WeccCheck.Ordered(p, m, "Qmin", Qmin, "Qmax", Qmax);
        WeccCheck.Finite(p, m, "Kpg", Kpg);
        WeccCheck.Finite(p, m, "Kig", Kig);
        WeccCheck.AtLeast(p, m, "Tp", Tp, 0.0);
        WeccCheck.Finite(p, m, "fdbd1", Fdbd1);
        WeccCheck.Finite(p, m, "fdbd2", Fdbd2);
        if (Fdbd1 > 0.0) p.Add($"{m} fdbd1 = {WeccCheck.N(Fdbd1)}: must be 0 or negative");
        if (Fdbd2 < 0.0) p.Add($"{m} fdbd2 = {WeccCheck.N(Fdbd2)}: must be 0 or positive");
        WeccCheck.Finite(p, m, "femax", Femax);
        WeccCheck.Finite(p, m, "femin", Femin);
        WeccCheck.Ordered(p, m, "femin", Femin, "femax", Femax);
        WeccCheck.Finite(p, m, "Pmax", Pmax);
        WeccCheck.Finite(p, m, "Pmin", Pmin);
        WeccCheck.Ordered(p, m, "Pmin", Pmin, "Pmax", Pmax);
        WeccCheck.AtLeast(p, m, "Tg", Tg, 0.0);
        WeccCheck.AtLeast(p, m, "Ddn", Ddn, 0.0);
        WeccCheck.AtLeast(p, m, "Dup", Dup, 0.0);
        return p;
    }
}

/// <summary>The measured inputs of REPC_A for one step (machine base for P and Q).</summary>
/// <param name="Vreg">Regulated bus voltage magnitude.</param>
/// <param name="Vcomp">The compensated voltage |Vreg - (Rc + jXc) Ibranch| (VCFlag = 1).</param>
/// <param name="Pbranch">Plant active power (branch flow or the unit's own).</param>
/// <param name="Qbranch">Plant reactive power.</param>
/// <param name="Freq">Measured frequency (pu).</param>
public readonly record struct RepcInputs(double Vreg, double Vcomp, double Pbranch, double Qbranch, double Freq);

/// <summary>
/// The REPC_A dynamics (see <see cref="RepcaParameters"/>, EPRI 3002006525 Fig. 3-5):
/// <code>
///   Vmeas = VCFlag ? Vcomp : Vreg + Kc Qbranch;     s0 = lag(Vmeas, Tfltr);   s1 = lag(Qbranch, Tfltr)
///   err   = RefFlag ? Vref - s0 : Qref - s1;        e = clamp(db(err, dbd1, dbd2), emin, emax)
///   s2    : PI Kp + Ki/s on e, integral and output in [Qmin, Qmax], integral frozen while Vreg &lt; Vfrz
///   Qext  = leadlag(PI out, Tft, Tfv)  (s3)
///   s4    = lag(Pbranch, Tp);   df = db(Freq_ref - Freq, fdbd1, fdbd2);  droop = Ddn min(df, 0) + Dup max(df, 0)
///   ep    = clamp(Plant_pref - s4 + droop, femin, femax);  s5: PI Kpg + Kig/s on ep in [Pmin, Pmax];  Pref = lag(s5 out, Tg) (s6)
/// </code>
/// Initialised so the plant output, the regulated voltage and the frequency are their initial values with zero error:
/// Vref = the initial Vmeas, Qref = the initial Qbranch, Plant_pref = the initial Pbranch, Freq_ref = 1, the PI
/// integrals and the lead-lag at the REEC model's initial Qext and Pref. With Fflag = 0 the Pref output is the REEC
/// model's initial Pref, held.
/// </summary>
public sealed class Repca
{
    private readonly RepcaParameters _p;
    private readonly double _vref, _qref, _plantPref, _pref0;
    private double _s0, _s1, _s2, _s3, _s4, _s5, _s6;

    public Repca(RepcaParameters p, in RepcInputs u0, double qext0, double pref0)
    {
        ArgumentNullException.ThrowIfNull(p);
        IReadOnlyList<string> bad = p.Check();
        if (bad.Count > 0) throw new ArgumentException("REPC_A parameters: " + string.Join("; ", bad), nameof(p));
        _p = p;
        double vmeas = p.VcFlag == 1 ? u0.Vcomp : u0.Vreg + p.Kc * u0.Qbranch;
        _s0 = vmeas; _vref = vmeas;
        _s1 = u0.Qbranch; _qref = u0.Qbranch;
        if (qext0 < p.Qmin || qext0 > p.Qmax)
            throw new ArgumentOutOfRangeException(nameof(qext0), qext0, $"REPC_A: the Qext the electrical control needs at the start ({WeccCheck.N(qext0)} pu) is outside [Qmin, Qmax] = [{WeccCheck.N(p.Qmin)}, {WeccCheck.N(p.Qmax)}].");
        _s2 = qext0;
        _s3 = LeadLagState(qext0);
        _s4 = u0.Pbranch; _plantPref = u0.Pbranch;
        _pref0 = pref0;
        if (p.Fflag == 1 && (pref0 < p.Pmin || pref0 > p.Pmax))
            throw new ArgumentOutOfRangeException(nameof(pref0), pref0, $"REPC_A: the Pref the electrical control needs at the start ({WeccCheck.N(pref0)} pu) is outside [Pmin, Pmax] = [{WeccCheck.N(p.Pmin)}, {WeccCheck.N(p.Pmax)}].");
        _s5 = pref0;
        _s6 = pref0;
        var r = new List<(string, double)>();
        Residuals(u0, r);
        WeccEquilibrium.Require("REPC_A", r);
    }

    public RepcaParameters Parameters => _p;

    /// <summary>The voltage reference (the initial measured voltage).</summary>
    public double Vref => _vref;

    /// <summary>The reactive PI's integral state s2 (pu).</summary>
    public double ReactiveIntegral => _s2;

    /// <summary>The active PI's integral state s5 (pu).</summary>
    public double ActiveIntegral => _s5;

    // The lead-lag (1 + s Tft) / (1 + s Tfv) is held as y = (Tft / Tfv) u + x with x the lag of (1 - Tft/Tfv) u (as
    // ControlBlocks.LeadLag); Tfv = 0 (and so Tft = 0, checked) is a pass-through.
    private double LeadRatio => _p.Tfv > WeccBlocks.InstantSeconds ? _p.Tft / _p.Tfv : 1.0;
    private double LeadLagState(double u) => _p.Tfv > WeccBlocks.InstantSeconds ? (1.0 - LeadRatio) * u : 0.0;

    /// <summary>The reactive PI's error and output for the step (pure).</summary>
    private (double E, double PiOut) QPath(in RepcInputs u)
    {
        double err = _p.RefFlag == 1 ? _vref - _s0 : _qref - _s1;
        double e = Math.Clamp(WeccBlocks.Deadband(err, _p.Dbd1, _p.Dbd2), _p.Emin, _p.Emax);
        return (e, Math.Clamp(_p.Kp * e + _s2, _p.Qmin, _p.Qmax));
    }

    private (double E, double PiOut) PPath(in RepcInputs u)
    {
        double df = WeccBlocks.Deadband(1.0 - u.Freq, _p.Fdbd1, _p.Fdbd2);
        double droop = _p.Ddn * Math.Min(df, 0.0) + _p.Dup * Math.Max(df, 0.0);
        double e = Math.Clamp(_plantPref - _s4 + droop, _p.Femin, _p.Femax);
        return (e, Math.Clamp(_p.Kpg * e + _s5, _p.Pmin, _p.Pmax));
    }

    /// <summary>The outputs for the step from the current states: Qext (to the REEC model) and Pref.</summary>
    public (double Qext, double Pref) Outputs(in RepcInputs u)
    {
        (_, double q) = QPath(u);
        double qext = LeadRatio * q + _s3;
        return (qext, _p.Fflag == 1 ? _s6 : _pref0);
    }

    /// <summary>Advance every state one step from the start-of-step inputs.</summary>
    public void Advance(in RepcInputs u, double dt)
    {
        (double eq, double q) = QPath(u);
        (double ep, double pOut) = PPath(u);
        if (!(u.Vreg < _p.Vfrz)) _s2 = WeccBlocks.Integrate(_s2, _p.Ki, eq, dt, _p.Qmin, _p.Qmax);
        if (_p.Tfv > WeccBlocks.InstantSeconds) _s3 = WeccBlocks.Lag(_s3, (1.0 - LeadRatio) * q, _p.Tfv, dt);
        if (_p.Fflag == 1)
        {
            _s5 = WeccBlocks.Integrate(_s5, _p.Kig, ep, dt, _p.Pmin, _p.Pmax);
            _s6 = WeccBlocks.Lag(_s6, pOut, _p.Tg, dt);
        }
        double vmeas = _p.VcFlag == 1 ? u.Vcomp : u.Vreg + _p.Kc * u.Qbranch;
        _s0 = WeccBlocks.Lag(_s0, vmeas, _p.Tfltr, dt);
        _s1 = WeccBlocks.Lag(_s1, u.Qbranch, _p.Tfltr, dt);
        _s4 = WeccBlocks.Lag(_s4, u.Pbranch, _p.Tp, dt);
    }

    /// <summary>The equilibrium residual of each state for the given inputs.</summary>
    public void Residuals(in RepcInputs u, List<(string Name, double Value)> into)
    {
        (double eq, double q) = QPath(u);
        (double ep, double pOut) = PPath(u);
        double vmeas = _p.VcFlag == 1 ? u.Vcomp : u.Vreg + _p.Kc * u.Qbranch;
        into.Add(("REPC_A s0 (Vmeas - s0)", vmeas - _s0));
        into.Add(("REPC_A s1 (Qbranch - s1)", u.Qbranch - _s1));
        into.Add(("REPC_A s2 PI input", eq));
        if (_p.Tfv > WeccBlocks.InstantSeconds) into.Add(("REPC_A s3 lead-lag", (1.0 - LeadRatio) * q - _s3));
        into.Add(("REPC_A s4 (Pbranch - s4)", u.Pbranch - _s4));
        if (_p.Fflag == 1)
        {
            into.Add(("REPC_A s5 PI input", ep));
            into.Add(("REPC_A s6 (PI out - Pref)", pOut - _s6));
        }
    }
}
