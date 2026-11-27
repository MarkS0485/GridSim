using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP6 (RC5): the initialised CSVGN1 (<see cref="Csvgn1Data"/>) of one compensator: the reactor fraction x0 that holds the
/// power-flow susceptance, the voltage reference, and the susceptance the network already holds (BINIT for a compensator
/// of the model, 0 for one held as a generator). Refuses a power-flow point outside the model's range.
/// </summary>
internal sealed class Csvgn1Plan : RmsDevicePlan
{
    internal Csvgn1Plan(Csvgn1Data data, string name, double baseMva, double y0Pu, double heldPu, Complex v0)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Validate($"CSVGN1 '{name}'");
        Data = data;
        Name = name;
        double mb = data.MbaseMva / baseMva;
        CbasePu = data.CbaseMvar / baseMva;
        MbasePu = mb;
        HeldPu = heldPu;
        X0 = (CbasePu - y0Pu) / mb;
        XMin = data.RminMvar / data.MbaseMva;
        if (!double.IsFinite(X0) || !(X0 >= XMin && X0 <= 1.0) || !(X0 >= data.Vmin && X0 <= data.Vmax))
            throw new ArgumentException($"CSVGN1 '{name}': the power flow puts the SVC outside its dynamic range: its susceptance of "
                + $"{ControlBlocks.Num(y0Pu * baseMva)} Mvar needs the reactor fraction x0 = {ControlBlocks.Num(X0)}, outside [RMIN / MBASE, 1] = "
                + $"[{ControlBlocks.Num(XMin)}, 1] or [VMIN, VMAX] = [{ControlBlocks.Num(data.Vmin)}, {ControlBlocks.Num(data.Vmax)}].");
        V0 = Math.Max(v0.Magnitude, DeviceCurrents.MinVoltagePu);
        Vref = V0 - X0 / data.K;
    }

    internal Csvgn1Data Data { get; }
    internal string Name { get; }
    internal double CbasePu { get; }
    internal double MbasePu { get; }
    internal double HeldPu { get; }
    internal double X0 { get; }
    internal double XMin { get; }
    internal double V0 { get; }
    internal double Vref { get; }

    internal override IRmsConverterModel CreateModel(IReadOnlyList<int> nodeBuses) => new Csvgn1State(this);
}

/// <summary>
/// WP6 (RC5): the run state of CSVGN1. The three blocks advance by the exact zero-order hold of <see cref="ControlBlocks"/>
/// (in chain order, each from its upstream block's new output) from the start-of-step voltage; within a step the
/// compensator is the susceptance Y of its held reactor fraction, injecting <c>-j (Y - Yheld) V</c>.
/// </summary>
internal sealed class Csvgn1State : IRmsConverterModel, IRmsDeviceStateRecorder
{
    private static readonly string[] States = { "x_pu", "b_pu", "q_pu" };

    private readonly Csvgn1Plan _p;
    private readonly LeadLag? _ll1;
    private readonly LeadLag? _ll2;
    private readonly FirstOrderLag? _lag2;
    private readonly FirstOrderLag _lag5;
    private double _ip, _iq;
    private readonly List<double[]> _samples = new();

    internal Csvgn1State(Csvgn1Plan plan)
    {
        _p = plan;
        Csvgn1Data d = plan.Data;
        double x0 = plan.X0;
        double e0 = x0 / d.K;
        if (d.T3 > ControlBlocks.InstantThresholdSeconds) { _ll1 = new LeadLag(d.T1, d.T3); _ll1.Initialize(e0); }
        if (d.T2 > 0) { _ll2 = new LeadLag(d.T2, d.T4); _ll2.Initialize(x0); }
        else if (d.T4 > ControlBlocks.InstantThresholdSeconds) { _lag2 = new FirstOrderLag(1.0, d.T4, d.Vmin, d.Vmax); _lag2.Initialize(x0); }
        _lag5 = new FirstOrderLag(1.0, d.T5, plan.XMin, 1.0);
        _lag5.Initialize(x0);
    }

    /// <summary>The reactor fraction (pu of MBASE).</summary>
    internal double X => _lag5.Output;

    /// <summary>The compensator's susceptance (system pu, capacitive positive).</summary>
    internal double Susceptance => _p.CbasePu - _lag5.Output * _p.MbasePu;

    public Complex TrialCurrent(Complex v) => new Complex(0.0, -(Susceptance - _p.HeldPu)) * v;

    public Complex Commit(Complex v)
    {
        Complex i = TrialCurrent(v);
        (_ip, _iq) = DeviceCurrents.Split(i, v);
        double b = Susceptance;
        _samples.Add(new[] { X, b, b * (v.Real * v.Real + v.Imaginary * v.Imaginary) });
        return i;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => injection;

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt)
    {
        Csvgn1Data d = _p.Data;
        double e = v.Magnitude - _p.Vref;
        double u1 = d.K * (_ll1 is null ? e : _ll1.Step(e, dt));
        double u2 = _ll2 is not null ? Math.Clamp(_ll2.Step(u1, dt), d.Vmin, d.Vmax)
            : _lag2 is not null ? _lag2.Step(u1, dt)
            : Math.Clamp(u1, d.Vmin, d.Vmax);
        _lag5.Step(u2, dt);
    }

    public double ActiveCurrent => _ip;

    public double ReactiveCurrent => _iq;

    public bool InDip => false;

    public RmsDeviceTrace? Trace => new(_p.Name, _p.Data.Model, States, _samples);
}
