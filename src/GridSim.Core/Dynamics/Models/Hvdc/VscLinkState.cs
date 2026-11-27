using System.Numerics;
using System.Runtime.CompilerServices;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP6 (RC5): the DC side of <see cref="VscDcLinks.SolveDc"/>, copied (that solver is not changed), for a given power order:
/// the power converter draws P = (Vdc + R I) I + loss_p(|I|) (MW, I in kA, Vdc the voltage converter's DCSET), the voltage
/// converter delivers Vdc I - loss_v(|I|), each loss max(MINLOSS, A + B |I|).
/// </summary>
internal static class VscQuasiSteady
{
    /// <summary>The DC current (kA) and the voltage converter's AC injection (MW) for the power converter drawing
    /// <paramref name="drawnMw"/>; also each converter's loss (MW).</summary>
    internal static (double CurrentKa, double VoltageSideMw, double PowerLossMw, double VoltageLossMw) Dc(VscDcLink link, double drawnMw)
    {
        bool firstIsVoltage = link.First.DcControl == VscDcControl.DcVoltage;
        VscConverter v = firstIsVoltage ? link.First : link.Second, p = firstIsVoltage ? link.Second : link.First;
        double vdc = v.DcSetpoint, r = link.ResistanceOhms;
        double a = p.LossAKw / 1000, b = p.LossBKwPerA, min = p.MinLossKw / 1000;
        double I = Current(drawnMw, vdc, r, a, b, link);
        if (a + b * Math.Abs(I) < min) I = Current(drawnMw, vdc, r, min, 0, link);
        double lossP = Math.Max(min, a + b * Math.Abs(I));
        double lossV = Math.Max(v.MinLossKw / 1000, v.LossAKw / 1000 + v.LossBKwPerA * Math.Abs(I));
        return (I, vdc * I - lossV, lossP, lossV);
    }

    private static double Current(double p, double vdc, double r, double a, double b, VscDcLink link)
    {
        double c = a - p;
        if (c <= 0)
        {
            double bb = vdc + b, disc = bb * bb - 4 * r * c;
            return -2 * c / (bb + Math.Sqrt(disc));
        }
        double bn = vdc - b, dn = bn * bn - 4 * r * c;
        if (dn < 0 || !(bn > 0))
            throw new InvalidOperationException($"VSC1 '{link.Name}': no DC current delivers a power order of {ControlBlocks.Num(p)} MW; the DC side has no steady state.");
        return -(2 * c / (bn + Math.Sqrt(dn)));
    }

    /// <summary>WP16: the inverse of <see cref="Dc"/>: the DC current (kA) and the power the power converter draws (MW) when
    /// the voltage converter delivers <paramref name="voltageSideMw"/> to its AC bus. The voltage converter's equation
    /// Vdc I - max(MINLOSS, A + B |I|) = P is linear in I on each branch of the max and |I|, so I is found in closed form;
    /// then drawn = (Vdc + R I) I + max(MINLOSS, A + B |I|) of the power converter.</summary>
    internal static (double CurrentKa, double DrawnMw) DcFromVoltageSide(VscDcLink link, double voltageSideMw)
    {
        bool firstIsVoltage = link.First.DcControl == VscDcControl.DcVoltage;
        VscConverter v = firstIsVoltage ? link.First : link.Second, p = firstIsVoltage ? link.Second : link.First;
        double vdc = v.DcSetpoint, r = link.ResistanceOhms;
        double a = v.LossAKw / 1000, b = v.LossBKwPerA, min = v.MinLossKw / 1000;
        // Try each branch: the linear loss (A + B |I| at or above MINLOSS) for each sign of I, then the floor MINLOSS.
        double I = double.NaN;
        foreach (double sign in new[] { 1.0, -1.0 })
        {
            double den = vdc - sign * b;
            if (!(den > 0)) continue;
            double x = (voltageSideMw + a) / den;
            if (x * sign >= 0 && a + b * Math.Abs(x) >= min) { I = x; break; }
        }
        if (double.IsNaN(I)) I = (voltageSideMw + min) / vdc;
        double lossP = Math.Max(p.MinLossKw / 1000, p.LossAKw / 1000 + p.LossBKwPerA * Math.Abs(I));
        return (I, (vdc + r * I) * I + lossP);
    }

    /// <summary>The current limit with priority: reactive first, Iq = clip(Iq_cmd, +-Imax), Id = clip(Id_cmd,
    /// +-sqrt(Imax^2 - Iq^2)); active first the other way round.</summary>
    internal static (double Id, double Iq) Limit(double idCmd, double iqCmd, double imax, VscCurrentPriority priority)
    {
        if (priority == VscCurrentPriority.Reactive)
        {
            double iq = Math.Clamp(iqCmd, -imax, imax);
            double room = Math.Sqrt(Math.Max(0.0, imax * imax - iq * iq));
            return (Math.Clamp(idCmd, -room, room), iq);
        }
        double id = Math.Clamp(idCmd, -imax, imax);
        double roomQ = Math.Sqrt(Math.Max(0.0, imax * imax - id * id));
        return (id, Math.Clamp(iqCmd, -roomQ, roomQ));
    }
}

/// <summary>
/// WP6 (RC5): the initialised VSC1 (<see cref="Vsc1Data"/>) of one two-terminal link, on its power-flow state. Both terminal
/// units share it; each run's link state is made once and found by both ends through the run's converter-node list.
/// </summary>
internal sealed class VscLinkPlan
{
    private readonly ConditionalWeakTable<IReadOnlyList<int>, VscLinkState> _runs = new();

    internal VscLinkPlan(VscDcLink link, VscLinkResult pf, Vsc1Data data, double baseMva, Complex v10, Complex v20)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(pf);
        ArgumentNullException.ThrowIfNull(data);
        string who = $"VSC1 '{link.Name}'";
        data.Validate(who);
        Link = link; Pf = pf; Data = data; BaseMva = baseMva;
        PowerSide = link.First.DcControl == VscDcControl.Power ? 0 : 1;
        if ((PowerSide == 0 ? link.Second : link.First).DcControl != VscDcControl.DcVoltage)
            throw new ArgumentException($"{who}: one converter must hold the DC voltage and the other the power order.");
        V0 = new[] { v10, v20 };
        P0 = new[] { pf.First.Mw / baseMva, pf.Second.Mw / baseMva };
        Q0 = new[] { pf.First.Mvar / baseMva, pf.Second.Mvar / baseMva };
        Kv = new[] { data.FirstKv, data.SecondKv };
        ImaxSys = data.ImaxPu * data.MbaseMva / baseMva;
        OrderMw0 = -P0[PowerSide] * baseMva;
        for (int s = 0; s < 2; s++)
        {
            double vm = V0[s].Magnitude;
            if (!(vm > DeviceCurrents.MinVoltagePu)) throw new ArgumentException($"{who}: converter {s + 1} has no voltage in the power flow.");
            double i = Math.Sqrt(P0[s] * P0[s] + Q0[s] * Q0[s]) / vm;
            if (!(i <= ImaxSys * (1 + 1e-12)))
                throw new ArgumentException($"{who}: converter {s + 1} carries {ControlBlocks.Num(i / (data.MbaseMva / baseMva))} pu of its MBASE in the power flow, above Imax = {ControlBlocks.Num(data.ImaxPu)}.");
        }
    }

    internal VscDcLink Link { get; }
    internal VscLinkResult Pf { get; }
    internal Vsc1Data Data { get; }
    internal double BaseMva { get; }
    internal int PowerSide { get; }
    internal Complex[] V0 { get; }
    internal double[] P0 { get; }
    internal double[] Q0 { get; }
    internal double[] Kv { get; }
    internal double ImaxSys { get; }
    internal double OrderMw0 { get; }

    internal VscLinkState StateFor(IReadOnlyList<int> nodeBuses) => _runs.GetValue(nodeBuses, nb => new VscLinkState(this, nb));
}

/// <summary>
/// WP6 (RC5): the run state of a VSC1 link: the power converter's filtered power P_f (lag Tp toward its order), each
/// converter's reactive state (a filtered Q order, or a filtered reactive current under AC voltage control), and the DC
/// voltage converter's power from the DC side on the power the power converter exchanges (P_f, or |V| Id when its
/// current limit clips Id or its voltage is below the floor, so the DC side balances). The DC voltage converter's own
/// current limit is not fed back unless <see cref="Vsc1Data.DcFollowsLimitedPower"/> is set: without it, with a fault at
/// its bus the DC side does not balance (a real link would see its DC voltage rise until a chopper or a power reduction
/// acts), and that case is outside this model. Each lag advances by the exact zero-order hold of
/// <see cref="ControlBlocks"/> from the start-of-step voltages; within a step each converter is a phase-locked current
/// source <c>(Id - j Iq) V / |V|</c>.
/// </summary>
internal sealed class VscLinkState
{
    private static readonly string[] StateNames = { "p_order_filtered_pu", "dc_terminal_p_pu", "idc_ka", "q_state_1", "q_state_2" };

    private readonly VscLinkPlan _p;
    private readonly int[] _node = { -1, -1 };
    private readonly FirstOrderLag _pf;
    private readonly FirstOrderLag[] _q = new FirstOrderLag[2];
    private double _pDc, _idcKa;
    private double? _pCap;   // WP16: the power converter's injection held to what the DC voltage converter can deliver (flag only)
    private readonly List<double[]> _samples = new();

    internal VscLinkState(VscLinkPlan plan, IReadOnlyList<int>? nodeBuses)
    {
        _p = plan;
        if (nodeBuses is not null)
            for (int s = 0; s < 2; s++)
            {
                int bus = s == 0 ? plan.Link.First.Bus : plan.Link.Second.Bus;
                for (int i = 0; i < nodeBuses.Count; i++) if (nodeBuses[i] == bus) { _node[s] = i; break; }
            }
        _pf = new FirstOrderLag(1.0, plan.Data.Tp);
        _pf.Initialize(plan.P0[plan.PowerSide]);
        for (int s = 0; s < 2; s++)
        {
            _q[s] = new FirstOrderLag(1.0, plan.Data.Tq);
            _q[s].Initialize(plan.Kv[s] > 0 ? plan.Q0[s] / plan.V0[s].Magnitude : plan.Q0[s]);
        }
        _pDc = plan.P0[1 - plan.PowerSide];
        _idcKa = plan.Pf.CurrentA / 1000.0;
    }

    internal VscLinkPlan Plan => _p;

    /// <summary>The power converter's filtered injected power (system pu).</summary>
    internal double PowerFiltered => _pf.Output;

    /// <summary>The DC voltage converter's injected power (system pu), from the DC side on the exchanged power.</summary>
    internal double DcTerminalPower => _pDc;

    /// <summary>The DC current (kA) of the exchanged power.</summary>
    internal double DcCurrentKa => _idcKa;

    /// <summary>The (Id, Iq) currents (system pu, Iq exporting positive) of converter <paramref name="side"/> at voltage
    /// magnitude <paramref name="vm"/>, the states held.</summary>
    internal (double Id, double Iq) Currents(int side, double vm)
    {
        double v = Math.Max(vm, DeviceCurrents.MinVoltagePu);
        double p = side == _p.PowerSide ? _pCap ?? _pf.Output : _pDc;
        double iq = _p.Kv[side] > 0 ? _q[side].Output : _q[side].Output / v;
        return VscQuasiSteady.Limit(p / v, iq, _p.ImaxSys, _p.Data.Priority);
    }

    internal Complex TrialCurrent(int side, Complex v)
    {
        double vm = v.Magnitude;
        if (!(vm > DeviceCurrents.MinVoltagePu)) return Complex.Zero;
        (double id, double iq) = Currents(side, vm);
        return new Complex(id, -iq) * (v / vm);
    }

    internal void Record()
    {
        _samples.Add(new[] { _pf.Output, _pDc, _idcKa, _q[0].Output, _q[1].Output });
    }

    internal RmsDeviceTrace Trace => new(_p.Link.Name, "VSC1", StateNames, _samples);

    /// <summary>Advance the link one step from the start-of-step converter voltages.</summary>
    internal void Advance(double t, Complex v1, Complex v2, double dt)
    {
        Vsc1Data d = _p.Data;
        // The last step's cap is not this step's input: the power converter's exchange below comes from its own order and
        // limit, and FollowVoltageConverterLimit decides the cap afresh. (Left in place, the cap made the power converter's
        // exchange equal the voltage side's limited power, so the limit test read "not clipped" on rounding and released
        // the cap on alternate steps while the limit still bound.) Always null without DcFollowsLimitedPower.
        _pCap = null;
        double order = OrderStepChecks.At(d.OrderSteps, t, dt, _p.OrderMw0);
        double pTarget = -order / _p.BaseMva;
        for (int s = 0; s < 2; s++)
        {
            double vm = (s == 0 ? v1 : v2).Magnitude;
            if (_p.Kv[s] > 0) _q[s].Step(_p.Q0[s] / _p.V0[s].Magnitude + _p.Kv[s] * (_p.V0[s].Magnitude - vm), dt);
            else _q[s].Step(_p.Q0[s], dt);
        }
        double pf = _pf.Step(pTarget, dt);
        // The DC side carries the power the power converter actually exchanges: |V| Id at the start-of-step voltage, with
        // Id after the current limit, and nothing below the converter's voltage floor. While Id is not clipped that is the
        // filtered order itself (kept as pf, not |V| (pf / |V|), which can differ by one rounding).
        double vmP = (_p.PowerSide == 0 ? v1 : v2).Magnitude;
        double pDel;
        if (!(vmP > DeviceCurrents.MinVoltagePu)) pDel = 0.0;
        else
        {
            (double id, _) = Currents(_p.PowerSide, vmP);
            pDel = id == pf / Math.Max(vmP, DeviceCurrents.MinVoltagePu) ? pf : vmP * id;
        }
        (double iKa, double vMw, _, _) = VscQuasiSteady.Dc(_p.Link, -pDel * _p.BaseMva);
        _idcKa = iKa;
        _pDc = vMw / _p.BaseMva;
        if (d.DcFollowsLimitedPower) FollowVoltageConverterLimit(_p.PowerSide == 0 ? v2 : v1);
    }

    /// <summary>WP16 (opt-in, <see cref="Vsc1Data.DcFollowsLimitedPower"/>): when the DC voltage converter's own current limit
    /// clips the power it must deliver at its AC bus (V Id after the limit), it delivers only that, and the power converter's
    /// power is cut to what the DC side then carries: the power that delivers it plus the line and converter losses, by the
    /// DC equations of <see cref="VscQuasiSteady.DcFromVoltageSide"/>. Decided afresh each step from the start-of-step
    /// voltages; the power converter's own limit (always followed) is applied first.</summary>
    private void FollowVoltageConverterLimit(Complex vVoltageSide)
    {
        _pCap = null;
        int vs = 1 - _p.PowerSide;
        double vm = vVoltageSide.Magnitude;
        double pV;
        if (!(vm > DeviceCurrents.MinVoltagePu)) pV = 0.0;
        else
        {
            double v = Math.Max(vm, DeviceCurrents.MinVoltagePu);
            double idCmd = _pDc / v;
            (double id, _) = Currents(vs, vm);
            if (id == idCmd) return;
            pV = vm * id;
        }
        (double iKa, double drawnMw) = VscQuasiSteady.DcFromVoltageSide(_p.Link, pV * _p.BaseMva);
        _idcKa = iKa;
        _pDc = pV;
        _pCap = -drawnMw / _p.BaseMva;
    }

    internal void AdvanceFromNodes(double t, Complex vOwn, Complex[] nodeVoltages, double dt)
    {
        Complex v2 = _node[1] >= 0 && _node[1] < nodeVoltages.Length ? nodeVoltages[_node[1]] : _p.V0[1];
        Advance(t, vOwn, v2, dt);
    }
}

/// <summary>WP6: one converter of a VSC1 link as a kernel converter model. The first converter (side 0) advances and records
/// the shared link state.</summary>
internal sealed class VscTerminalModel : IRmsConverterModel, IRmsDeviceStateRecorder
{
    private readonly VscLinkState _s;
    private readonly int _side;
    private double _ip, _iq;

    internal VscTerminalModel(VscLinkState state, int side) { _s = state; _side = side; }

    public Complex TrialCurrent(Complex v) => _s.TrialCurrent(_side, v);

    public Complex Commit(Complex v)
    {
        Complex i = TrialCurrent(v);
        (_ip, _iq) = DeviceCurrents.Split(i, v);
        if (_side == 0) _s.Record();
        return i;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => injection;

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt)
    {
        if (_side == 0) _s.AdvanceFromNodes(t, v, nodeVoltages, dt);
    }

    public double ActiveCurrent => _ip;

    public double ReactiveCurrent => _iq;

    public bool InDip => false;

    public RmsDeviceTrace? Trace => _side == 0 ? _s.Trace : null;
}

/// <summary>WP6: the plan of one VSC1 converter (the kernel unit's <see cref="RmsConverterUnit.Device"/>).</summary>
internal sealed class VscTerminalPlan(VscLinkPlan link, int side) : RmsDevicePlan
{
    internal override IRmsConverterModel CreateModel(IReadOnlyList<int> nodeBuses) => new VscTerminalModel(link.StateFor(nodeBuses), side);
}
