using System.Numerics;
using System.Runtime.CompilerServices;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP6 (RC5): the converter equations of <see cref="LccDcLinks"/>, copied (that solver is not changed): Eac = |V| EBAS TR /
/// TAP; Vd = N (k Eac cos(angle) - (3/pi Xc + 2 Rc) I), k = 3 sqrt(2) / pi; the fundamental power factor
/// cos(phi) = cos(angle) - 3/pi Xc I / (k Eac).
/// </summary>
internal static class LccQuasiSteady
{
    internal static readonly double K = 3.0 * Math.Sqrt(2.0) / Math.PI;
    internal const double KX = 3.0 / Math.PI;

    /// <summary>Floor on cos(phi), so a converter driven to the edge of its range cannot return an infinite reactive power.</summary>
    internal const double MinCosPhi = 1e-3;

    internal static double Eac(LccConverter c, double vPu, double tap) => vPu * c.BaseKv * c.TransformerRatio / tap;

    /// <summary>The DC voltage drop per kA of a converter: N (3/pi Xc + 2 Rc).</summary>
    internal static double Drop(LccConverter c) => c.Bridges * (KX * c.XcOhms + 2 * c.RcOhms);

    /// <summary>tan(phi) of a converter at cos(angle) <paramref name="cosAngle"/>, AC voltage <paramref name="vPu"/>, tap and current (kA).</summary>
    internal static double TanPhi(LccConverter c, double vPu, double tap, double cosAngle, double iKa)
    {
        double vd0 = K * Eac(c, vPu, tap);
        if (!(vd0 > 0)) return 0.0;
        double cosPhi = Math.Clamp(cosAngle - KX * c.XcOhms * iKa / vd0, MinCosPhi, 1.0);
        return Math.Sqrt(1 - cosPhi * cosPhi) / cosPhi;
    }

    internal static double Rad(double deg) => deg * Math.PI / 180.0;
}

/// <summary>WP6: the quasi-steady operating point of an LCC link at one DC current and pair of AC voltages.</summary>
internal readonly record struct LccOperatingPoint(double CurrentKa, double RectifierDcKv, double InverterDcKv, double CosAlpha, double CosGamma,
    double RectifierMw, double RectifierMvar, double InverterMw, double InverterMvar, bool AtAlphaMin);

/// <summary>
/// WP6 (RC5): the initialised LCC1 (<see cref="Lcc1Data"/>) of one two-terminal link: its power-flow state, frozen taps,
/// the run's extinction angle, and the order. Both terminal units share it; for each run the first terminal's model makes
/// the link state and the second finds it through the run's converter-node list.
/// </summary>
internal sealed class LccLinkPlan
{
    private readonly ConditionalWeakTable<IReadOnlyList<int>, LccLinkState> _runs = new();

    internal LccLinkPlan(LccDcLink link, LccLinkResult pf, Lcc1Data data, double baseMva, Complex vr0, Complex vi0, List<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(pf);
        ArgumentNullException.ThrowIfNull(data);
        string who = $"LCC1 '{link.Name}'";
        data.Validate(who);
        if (link.Mode == LccMode.Blocked || pf.Control == LccControl.Blocked)
            throw new ArgumentException($"{who}: the link is blocked in the power flow, so it has no operating point to start from.");
        if (!(pf.CurrentA > 0)) throw new ArgumentException($"{who}: the power flow carries no DC current.");
        Link = link; Pf = pf; Data = data; BaseMva = baseMva; Vr0 = vr0; Vi0 = vi0;
        TapR = pf.RectifierTap; TapI = pf.InverterTap;
        GammaDeg = pf.GammaDeg;
        CosGamma = DetMath.Cos(LccQuasiSteady.Rad(GammaDeg));
        CosAlphaMin = DetMath.Cos(LccQuasiSteady.Rad(data.AlphaMinDeg));
        if (GammaDeg != data.GammaMinDeg)
            notes?.Add($"{who}: the power flow's extinction angle of {ControlBlocks.Num(GammaDeg)} degrees differs from GAMDY = {ControlBlocks.Num(data.GammaMinDeg)}; the run keeps the power flow's angle so it starts in equilibrium.");
        Id0 = pf.CurrentA / 1000.0;
        PowerOrder = link.Mode == LccMode.Power && !pf.PowerOrderSwitchedToCurrent;
        MeasureAtRectifier = !PowerOrder || link.Setpoint >= 0;
        LccOperatingPoint op = Solve(Vr0.Magnitude, Vi0.Magnitude, Id0);
        if (op.AtAlphaMin)
            throw new ArgumentException($"{who}: at the power-flow point the rectifier's firing angle is below ALFDY = {ControlBlocks.Num(data.AlphaMinDeg)} degrees.");
        Vdc0 = MeasureAtRectifier ? op.RectifierDcKv : op.InverterDcKv;
        P0Mw = MeasureAtRectifier ? op.RectifierMw : op.InverterMw;
        if (!(Vdc0 > 0)) throw new ArgumentException($"{who}: the measured DC voltage at the power-flow point is not above 0.");
        if (Id0 < data.C0Amps / 1000.0) throw new ArgumentException($"{who}: the power-flow DC current ({ControlBlocks.Num(pf.CurrentA)} A) is below C0 = {ControlBlocks.Num(data.C0Amps)} A.");
        if (Vdcol(Vdc0) < Id0) throw new ArgumentException($"{who}: the power-flow DC current ({ControlBlocks.Num(pf.CurrentA)} A) is above the VDCOL limit at the measured DC voltage ({ControlBlocks.Num(Vdcol(Vdc0) * 1000)} A).");
        // The power flow settled its AC/DC alternation to 1e-9 (1 + |P|) MW (LccDcLinks.CouplingTolerance), from the AC
        // voltages of its previous AC solve, so the converter equations at the final voltages differ from the powers the AC
        // solve carried by that settling residual. It is held as a correction scaled by Id / Id0, so the run starts exactly on
        // the power flow's injections.
        ResidualR = new Complex(-pf.RectifierMw - (-op.RectifierMw), -pf.RectifierMvar - (-op.RectifierMvar)) / baseMva;
        ResidualI = new Complex(pf.InverterMw - op.InverterMw, -pf.InverterMvar - (-op.InverterMvar)) / baseMva;
        // The residual is only the settling gap. A larger one means LCC1's equations and the power flow disagree (an equation or
        // sign error), which the correction would hide, so the start is refused.
        // Each component has its own scale, as the power flow's settling test (HvdcCoupling.LccSettled) does: P by (1 + |P|), Q by
        // (1 + |Q|). The bound is 10 times the settling tolerance, so a case the power flow accepted is never refused at the edge.
        const double Margin = 10.0;
        double tol = Margin * LccDcLinks.CouplingTolerance;
        double sizeRp = Math.Abs(ResidualR.Real) * baseMva, sizeRq = Math.Abs(ResidualR.Imaginary) * baseMva;
        double sizeIp = Math.Abs(ResidualI.Real) * baseMva, sizeIq = Math.Abs(ResidualI.Imaginary) * baseMva;
        if (sizeRp > tol * (1 + Math.Abs(pf.RectifierMw)) || sizeRq > tol * (1 + Math.Abs(pf.RectifierMvar))
            || sizeIp > tol * (1 + Math.Abs(pf.InverterMw)) || sizeIq > tol * (1 + Math.Abs(pf.InverterMvar)))
            throw new ArgumentException($"{who}: at the power-flow point the converter equations differ from the power flow's powers by {ControlBlocks.Num(Math.Max(Math.Max(sizeRp, sizeRq), Math.Max(sizeIp, sizeIq)))} MW or Mvar, above 10 times the power flow's AC/DC settling tolerance (1e-9 of 1 + the component); the link would not start in equilibrium.");
    }

    internal LccDcLink Link { get; }
    internal LccLinkResult Pf { get; }
    internal Lcc1Data Data { get; }
    internal double BaseMva { get; }
    internal Complex Vr0 { get; }
    internal Complex Vi0 { get; }
    internal double TapR { get; }
    internal double TapI { get; }
    internal double GammaDeg { get; }
    internal double CosGamma { get; }
    internal double CosAlphaMin { get; }
    internal double Id0 { get; }
    internal bool PowerOrder { get; }
    internal bool MeasureAtRectifier { get; }
    internal double Vdc0 { get; }
    internal double P0Mw { get; }
    internal Complex ResidualR { get; }
    internal Complex ResidualI { get; }

    /// <summary>The VDCOL current limit (kA) at measured DC voltage <paramref name="vKv"/>: C1 at and below V1, linear
    /// through (V2, C2) to (V3, C3), no limit at and above V3.</summary>
    internal double Vdcol(double vKv)
    {
        Lcc1Data d = Data;
        if (vKv >= d.V3Kv) return double.PositiveInfinity;
        if (vKv <= d.V1Kv) return d.C1Amps / 1000.0;
        if (vKv <= d.V2Kv)
            return (d.V2Kv > d.V1Kv ? d.C1Amps + (d.C2Amps - d.C1Amps) * (vKv - d.V1Kv) / (d.V2Kv - d.V1Kv) : d.C2Amps) / 1000.0;
        return (d.V3Kv > d.V2Kv ? d.C2Amps + (d.C3Amps - d.C2Amps) * (vKv - d.V2Kv) / (d.V3Kv - d.V2Kv) : d.C3Amps) / 1000.0;
    }

    /// <summary>
    /// The quasi-steady link at DC current <paramref name="idKa"/> and AC voltage magnitudes <paramref name="er"/>,
    /// <paramref name="ei"/> (pu): the inverter at the run's extinction angle, Vdi = A_i - b_i I; Vdr = Vdi + Rdc I; the
    /// rectifier's firing angle from Vdr. Below ALFDY the rectifier sits at ALFDY and the inverter takes the current at
    /// I (1 - margin), its extinction angle following from the voltage the rectifier gives; if even that needs the inverter
    /// below its angle, the current is where both converters sit at their minimum angles (0 when there is none).
    /// </summary>
    internal LccOperatingPoint Solve(double er, double ei, double idKa)
    {
        LccConverter rc = Link.Rectifier, ic = Link.Inverter;
        double rdc = Link.ResistanceOhms;
        double br = LccQuasiSteady.Drop(rc), bi = LccQuasiSteady.Drop(ic);
        double kr = rc.Bridges * LccQuasiSteady.K * LccQuasiSteady.Eac(rc, er, TapR);
        double ki = ic.Bridges * LccQuasiSteady.K * LccQuasiSteady.Eac(ic, ei, TapI);
        double I = idKa;
        double cosG = CosGamma;
        double vdi = ki * cosG - bi * I;
        double vdr = vdi + rdc * I;
        double cosA = kr > 0 ? (vdr + br * I) / kr : double.PositiveInfinity;
        bool atMin = false;
        if (cosA > CosAlphaMin && I > 0)
        {
            atMin = true;
            cosA = CosAlphaMin;
            double ar = kr * CosAlphaMin, ai = ki * CosGamma;
            I = idKa * (1 - Link.MarginPu);
            vdr = ar - br * I;
            vdi = vdr - rdc * I;
            cosG = ki > 0 ? (vdi + bi * I) / ki : double.PositiveInfinity;
            if (!(cosG <= CosGamma) || !(vdi > 0))
            {
                // The inverter cannot hold that current: both converters at their minimum angles.
                cosG = CosGamma;
                double den = rdc + br - bi;
                I = ar > ai && den > 0 ? Math.Min(I, (ar - ai) / den) : 0.0;
                if (!(I > 0)) I = 0.0;
                vdr = ar - br * I;
                vdi = vdr - rdc * I;
            }
        }
        double pr = vdr * I, pi = vdi * I;
        double qr = I > 0 ? pr * LccQuasiSteady.TanPhi(rc, er, TapR, cosA, I) : 0.0;
        double qi = I > 0 ? pi * LccQuasiSteady.TanPhi(ic, ei, TapI, cosG, I) : 0.0;
        return new LccOperatingPoint(I, vdr, vdi, cosA, cosG, pr, qr, pi, qi, atMin);
    }

    /// <summary>The link state of one run (made by the first terminal, found by the second).</summary>
    internal LccLinkState StateFor(IReadOnlyList<int> nodeBuses) => _runs.GetValue(nodeBuses, nb => new LccLinkState(this, nb));
}

/// <summary>
/// WP6 (RC5): the run state of an LCC1 link: the DC current Id (lag TIDC toward the order), the measured DC voltage (lag
/// TVDC) and the power order (lag Tp), blocking and recovery. Each block advances by the exact zero-order hold of
/// <see cref="ControlBlocks"/> from the start-of-step voltages; within a step the link is quasi-steady at the held Id.
/// </summary>
internal sealed class LccLinkState
{
    private static readonly string[] StateNames = { "id_ka", "vdc_measured_kv", "power_order_mw", "blocked", "rectifier_dc_kv", "inverter_dc_kv" };

    private readonly LccLinkPlan _p;
    private readonly int _invNode = -1;
    private readonly FirstOrderLag _id, _vdc, _pord;
    private double _er, _ei;   // the held AC voltage magnitudes (start of the step being advanced)
    private bool _blocked, _recovering;
    private double _tBlock, _tUnblock;
    private readonly List<double[]> _samples = new();

    internal LccLinkState(LccLinkPlan plan, IReadOnlyList<int>? nodeBuses)
    {
        _p = plan;
        if (nodeBuses is not null) _invNode = IndexOf(nodeBuses, plan.Link.Inverter.Bus);
        Lcc1Data d = plan.Data;
        _id = new FirstOrderLag(1.0, d.Tidc); _id.Initialize(plan.Id0);
        _vdc = new FirstOrderLag(1.0, d.Tvdc); _vdc.Initialize(plan.Vdc0);
        _pord = new FirstOrderLag(1.0, d.Tp); _pord.Initialize(plan.PowerOrder ? plan.P0Mw : plan.Id0 * 1000.0);
        _er = plan.Vr0.Magnitude;
        _ei = plan.Vi0.Magnitude;
    }

    private static int IndexOf(IReadOnlyList<int> list, int bus)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == bus) return i;
        return -1;
    }

    internal LccLinkPlan Plan => _p;

    /// <summary>The DC current (kA).</summary>
    internal double CurrentKa => _blocked ? 0.0 : _id.Output;

    internal bool Blocked => _blocked;

    internal double MeasuredDcKv => _vdc.Output;

    internal double PowerOrderFiltered => _pord.Output;

    /// <summary>The injected complex powers (system pu) at the rectifier and inverter at AC voltage magnitudes
    /// <paramref name="er"/>, <paramref name="ei"/>, the states held.</summary>
    internal (Complex Sr, Complex Si, LccOperatingPoint Op) Injections(double er, double ei)
    {
        double id = CurrentKa;
        LccOperatingPoint op = _p.Solve(er, ei, id);
        double scale = id / _p.Id0;
        double sb = _p.BaseMva;
        var sr = new Complex(-op.RectifierMw / sb, -op.RectifierMvar / sb) + _p.ResidualR * scale;
        var si = new Complex(op.InverterMw / sb, -op.InverterMvar / sb) + _p.ResidualI * scale;
        return (sr, si, op);
    }

    /// <summary>The injected current of terminal <paramref name="side"/> (0 rectifier, 1 inverter) at its trial voltage, the
    /// other end at its held voltage.</summary>
    internal Complex TrialCurrent(int side, Complex v)
    {
        (Complex sr, Complex si, _) = side == 0 ? Injections(v.Magnitude, _ei) : Injections(_er, v.Magnitude);
        return DeviceCurrents.FromPower(side == 0 ? sr : si, v);
    }

    /// <summary>Record the states at a commit (once per sample, by the rectifier terminal).</summary>
    internal void Record(double er)
    {
        LccOperatingPoint op = _p.Solve(er, _ei, CurrentKa);
        _samples.Add(new[] { CurrentKa, _vdc.Output, _pord.Output, _blocked ? 1.0 : 0.0, op.RectifierDcKv, op.InverterDcKv });
    }

    internal RmsDeviceTrace Trace => new(_p.Link.Name, "LCC1", StateNames, _samples);

    /// <summary>Advance the link one step of <paramref name="dt"/> from the start-of-step AC voltages.</summary>
    internal void Advance(double t, Complex vr, Complex vi, double dt)
    {
        Lcc1Data d = _p.Data;
        _er = vr.Magnitude;
        _ei = vi.Magnitude;
        double eps = dt * MultiMachineSubtransient.StageTimeToleranceSteps;
        // Blocking and unblocking, from the rectifier's AC voltage.
        if (!_blocked && _er < d.VblockPu)
        {
            _blocked = true;
            _recovering = false;
            _tBlock = t;
            _id.Initialize(0.0);
        }
        else if (_blocked && t - _tBlock + eps >= d.TblockSeconds && _er > d.VunblockPu)
        {
            _blocked = false;
            _recovering = true;
            _tUnblock = t;
        }
        LccOperatingPoint now = _p.Solve(_er, _ei, CurrentKa);
        double vdcNow = _p.MeasureAtRectifier ? now.RectifierDcKv : now.InverterDcKv;

        double order = OrderStepChecks.At(d.OrderSteps, t, dt, _p.PowerOrder ? _p.P0Mw : _p.Id0 * 1000.0);
        double ordF = _pord.Step(order, dt);
        double vdcM = _vdc.Output;
        double vForVdcol = vdcM;
        double io = _p.PowerOrder ? ordF / Math.Max(vdcM, d.PowerOrderVoltageFloorPu * _p.Vdc0) : ordF / 1000.0;
        if (_recovering && d.VrampPuPerSecond > 0) vForVdcol = Math.Min(vForVdcol, d.VrampPuPerSecond * _p.Vdc0 * (t + dt - _tUnblock));
        io = Math.Max(d.C0Amps / 1000.0, Math.Min(io, _p.Vdcol(vForVdcol)));
        if (_recovering)
        {
            // The current order recovers along the ramp CRAMP Id0 (t - t_unblock), sampled at the end of the step.
            double cap = d.CrampPuPerSecond > 0 ? d.CrampPuPerSecond * _p.Id0 * (t + dt - _tUnblock) : double.PositiveInfinity;
            bool vDone = !(d.VrampPuPerSecond > 0) || d.VrampPuPerSecond * _p.Vdc0 * (t + dt - _tUnblock) >= vdcM;
            if (cap >= io && vDone) _recovering = false;
            else io = Math.Min(io, cap);
        }
        if (_blocked) _id.Initialize(0.0);
        else _id.Step(io, dt);
        _vdc.Step(vdcNow, dt);
    }

    /// <summary>The run's start-of-step voltages for the rectifier's <see cref="Advance"/>: its own and the inverter's node.</summary>
    internal void AdvanceFromNodes(double t, Complex vr, Complex[] nodeVoltages, double dt)
    {
        Complex vi = _invNode >= 0 && _invNode < nodeVoltages.Length ? nodeVoltages[_invNode] : _p.Vi0;
        Advance(t, vr, vi, dt);
    }
}

/// <summary>WP6: one terminal of an LCC1 link as a kernel converter model. The rectifier (side 0) advances and records the
/// shared link state; the inverter only injects.</summary>
internal sealed class LccTerminalModel : IRmsConverterModel, IRmsDeviceStateRecorder
{
    private readonly LccLinkState _s;
    private readonly int _side;
    private double _ip, _iq;

    internal LccTerminalModel(LccLinkState state, int side) { _s = state; _side = side; }

    public Complex TrialCurrent(Complex v) => _s.TrialCurrent(_side, v);

    public Complex Commit(Complex v)
    {
        Complex i = TrialCurrent(v);
        (_ip, _iq) = DeviceCurrents.Split(i, v);
        if (_side == 0) _s.Record(v.Magnitude);
        return i;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => injection;

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt)
    {
        if (_side == 0) _s.AdvanceFromNodes(t, v, nodeVoltages, dt);
    }

    public double ActiveCurrent => _ip;

    public double ReactiveCurrent => _iq;

    public bool InDip => _s.Blocked;

    public RmsDeviceTrace? Trace => _side == 0 ? _s.Trace : null;
}

/// <summary>WP6: the plan of one LCC1 terminal (the kernel unit's <see cref="RmsConverterUnit.Device"/>).</summary>
internal sealed class LccTerminalPlan(LccLinkPlan link, int side) : RmsDevicePlan
{
    internal LccLinkPlan Link => link;

    internal override IRmsConverterModel CreateModel(IReadOnlyList<int> nodeBuses) => new LccTerminalModel(link.StateFor(nodeBuses), side);
}
