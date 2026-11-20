using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.PowerElectronics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt.Hvdc;

/// <summary>
/// The circulating-current suppression control of an <see cref="MmcConverter"/>. The second harmonic of the leg circulating currents
/// <c>i_c,j = (i_u + i_l)/2</c> is a NEGATIVE-sequence set at twice the grid frequency: leg j (modulation phase <c>psi_j = -120 deg j</c>)
/// carries <c>I cos(2 w t + 2 phi + 2 psi_j + gamma)</c>. It is measured in a frame rotating at <c>theta_j = 2 (w t + phi + psi_j)</c>:
/// <c>i_dq = (2/3) sum_j i_c,j e^(-j theta_j)</c>, in which it is a constant. The DC share of the circulating current and any
/// positive-sequence component cancel in the sum over the three legs. Per leg the circulating current obeys
/// <c>(R + sL) i_c = (V_dc - v_u - v_l)/2 = (V_dc - V_C)/2 - V_C z</c> (the common-mode insertion term <c>z</c> is the control input), so in the
/// rotating frame <c>L di/dt + j 2 w L i + R i = e</c> with <c>e = -V_C z</c>. The control is the PI law <c>e = u(-i_dq) + j 2 w L i_dq</c>
/// (cross-coupling decoupled), <c>Kp = alpha L</c>, <c>Ki = alpha R</c> (pole-zero cancellation: closed-loop bandwidth alpha), and
/// <c>z_j = -Re(e e^(j theta_j)) / V_C,nominal</c>. The integral action drives the second harmonic to zero in steady state. It acts on the
/// last accepted state (one step late) and is held between samples. Active from <c>enableAtSeconds</c>.
/// </summary>
public sealed class MmcCirculatingCurrentControl : NodalElement
{
    private readonly MmcConverter _conv;
    private readonly double _w, _phi, _l, _vcNominal, _enableAt;
    private readonly int _every;
    private readonly PiController _pd, _pq;
    private long _step;
    private double _dt;

    /// <summary>Create the control.</summary>
    /// <param name="name">Element name.</param>
    /// <param name="converter">The converter whose common-mode terms this control sets.</param>
    /// <param name="frequencyHz">Grid frequency.</param>
    /// <param name="phaseRad">Phase of the modulation reference of leg 0: <c>m_0 ~ sin(w t + phi)</c>.</param>
    /// <param name="bandwidthRadPerS">Closed-loop bandwidth alpha.</param>
    /// <param name="controlEvery">Solver steps per control sample.</param>
    /// <param name="enableAtSeconds">The control is off (z = 0) until this time.</param>
    public MmcCirculatingCurrentControl(string name, MmcConverter converter, double frequencyHz, double phaseRad, double bandwidthRadPerS, int controlEvery,
        double enableAtSeconds = 0.0) : base(name)
    {
        _conv = converter ?? throw new ArgumentNullException(nameof(converter));
        if (!(frequencyHz > 0) || !(bandwidthRadPerS > 0) || controlEvery < 1) throw new ArgumentException($"{name}: frequency and bandwidth must be positive, controlEvery at least 1.");
        _w = 2.0 * Math.PI * frequencyHz; _phi = phaseRad; _every = controlEvery; _enableAt = enableAtSeconds;
        _l = converter.Parameters.ArmInductanceH; _vcNominal = converter.Parameters.NominalDcVolts;
        double kp = bandwidthRadPerS * _l, ki = bandwidthRadPerS * converter.Parameters.ArmResistanceOhm;
        _pd = new PiController(kp, ki); _pq = new PiController(kp, ki);
    }

    internal override void Configure(double dt, IntegrationRule rule) => _dt = dt;
    internal override void Reset()
    {
        _step = 0; _pd.Initialize(0.0, 0.0); _pq.Initialize(0.0, 0.0);
        for (int k = 0; k < 3; k++) _conv.SetCommonMode(k, 0.0);
    }

    internal override void Accept(double t, double[] v)
    {
        _step++;
        if (t < _enableAt || _step % _every != 0) return;
        Span<double> cs = stackalloc double[3];
        Span<double> sn = stackalloc double[3];
        double d = 0.0, q = 0.0;
        for (int j = 0; j < 3; j++)
        {
            (double s, double c) = DetMath.SinCos(2.0 * (_w * t + _phi - j * (2.0 * Math.PI / 3.0)));
            sn[j] = s; cs[j] = c;
            double ic = _conv.LegCirculatingCurrent(j);
            d += ic * c; q -= ic * s;
        }
        d *= 2.0 / 3.0; q *= 2.0 / 3.0;
        double ts = _every * _dt;
        double ud = _pd.Step(-d, ts), uq = _pq.Step(-q, ts);
        double wf = 2.0 * _w;
        double ed = ud - wf * _l * q, eq = uq + wf * _l * d;
        for (int j = 0; j < 3; j++) _conv.SetCommonMode(j, -(ed * cs[j] - eq * sn[j]) / _vcNominal);
    }

    /// <summary>The second-harmonic circulating current amplitude (d, q magnitude) at the last control sample is not stored; read the legs.</summary>
    internal override double ProbeCurrent(int channel) => 0.0;
}

/// <summary>What the outer loop of a VSC station regulates.</summary>
public enum VscStationMode
{
    /// <summary>Active power injected into the grid at the PCC (negative = drawn from the grid), watts.</summary>
    Power,
    /// <summary>The DC link voltage (pole to pole), volts.</summary>
    DcVoltage,
}

/// <summary>Which converter a station uses.</summary>
public enum VscStationConverter
{
    /// <summary>The R4-17 two-level average-value converter.</summary>
    TwoLevelAverage,
    /// <summary>MMC, arm-average.</summary>
    MmcArmAverage,
    /// <summary>MMC, sub-module level.</summary>
    MmcDetailed,
}

/// <summary>Data of a VSC HVDC station: grid, filter, DC side and controls. SI units, three-phase, peak phase voltages.</summary>
public sealed record VscStationSpec
{
    /// <summary>Peak phase voltage of the grid source, volts.</summary>
    public double GridPeakVolts { get; init; }
    /// <summary>Grid frequency, hertz.</summary>
    public double FrequencyHz { get; init; } = 50.0;
    /// <summary>Phase of the grid source of phase a: <c>e_a = Vm sin(w t + theta)</c>; zero starts the source at zero volts.</summary>
    public double GridAngleRad { get; init; }
    /// <summary>Grid series resistance and inductance (the source impedance), per phase.</summary>
    public double GridROhm { get; init; }
    /// <summary>Grid inductance, henries.</summary>
    public double GridLHenry { get; init; }
    /// <summary>Phase reactor (converter to PCC) resistance.</summary>
    public double FilterROhm { get; init; }
    /// <summary>Phase reactor inductance.</summary>
    public double FilterLHenry { get; init; }
    /// <summary>Series resistance of the average-value two-level converter legs (ignored by an MMC).</summary>
    public double ConverterRsOhm { get; init; } = 1e-3;
    /// <summary>Nominal pole-to-pole DC voltage, volts.</summary>
    public double DcNominalVolts { get; init; }
    /// <summary>DC link capacitance (series pair), farads.</summary>
    public double DcCapF { get; init; }
    /// <summary>The converter.</summary>
    public VscStationConverter Converter { get; init; } = VscStationConverter.TwoLevelAverage;
    /// <summary>MMC data (required for an MMC converter).</summary>
    public MmcParameters? Mmc { get; init; }
    /// <summary>What the outer loop regulates.</summary>
    public VscStationMode Mode { get; init; }
    /// <summary>The set point: watts injected into the grid (Power) or DC volts (DcVoltage).</summary>
    public double SetPoint { get; init; }
    /// <summary>Inner current-loop bandwidth, rad/s.</summary>
    public double CurrentBandwidthRadPerS { get; init; } = 1000.0;
    /// <summary>Outer loop natural frequency, rad/s (power: integral bandwidth; DC voltage: PI natural frequency).</summary>
    public double OuterBandwidthRadPerS { get; init; } = 60.0;
    /// <summary>Solver steps per control sample.</summary>
    public int ControlEvery { get; init; } = 2;
    /// <summary>Reactive current reference, amperes peak (positive lagging / injected on the q axis as the controller defines it).</summary>
    public double ReactiveCurrentAmps { get; init; }
    /// <summary>Largest d-axis current reference, amperes peak.</summary>
    public double CurrentLimitAmps { get; init; } = 1e6;
    /// <summary>Initial DC link voltage (the capacitors' charge); zero = nominal.</summary>
    public double InitialDcVolts { get; init; }
    /// <summary>Circulating-current suppression bandwidth, rad/s, for an MMC (0 = no suppression).</summary>
    public double SuppressionBandwidthRadPerS { get; init; }
}

/// <summary>Measures the PCC quantities of a VSC station in the grid frame at the last accepted step: the d and q voltages, the d and q
/// converter currents, the active power injected into the grid <c>P = 3/2 (v_d i_d + v_q i_q)</c> and the DC link voltage.</summary>
public sealed class VscStationMeasurement : NodalElement
{
    private readonly NodalElement[] _filter;
    private readonly int[] _pcc;
    private readonly int _dcP, _dcN;
    private readonly double _w, _phi;

    internal VscStationMeasurement(string name, NodalElement[] filter, int[] pcc, int dcP, int dcN, double frequencyHz, double phaseRad) : base(name)
    {
        _filter = filter; _pcc = pcc; _dcP = dcP; _dcN = dcN; _w = 2.0 * Math.PI * frequencyHz; _phi = phaseRad;
    }

    /// <summary>The d-axis PCC voltage, volts peak.</summary>
    public double Vd { get; private set; }
    /// <summary>The q-axis PCC voltage.</summary>
    public double Vq { get; private set; }
    /// <summary>The d-axis converter current, amperes peak.</summary>
    public double Id { get; private set; }
    /// <summary>The q-axis converter current.</summary>
    public double Iq { get; private set; }
    /// <summary>Active power into the grid at the PCC, watts.</summary>
    public double P => 1.5 * (Vd * Id + Vq * Iq);
    /// <summary>DC link voltage, volts.</summary>
    public double Vdc { get; private set; }

    internal override void Configure(double dt, IntegrationRule rule) { }
    internal override void Reset() { Vd = Vq = Id = Iq = 0.0; Vdc = 0.0; }

    internal override void Accept(double t, double[] v)
    {
        (double s, double c) = DetMath.SinCos(_w * t + _phi);
        const double Th = 0.8660254037844386;
        double c1 = -0.5 * c + Th * s, s1 = -0.5 * s - Th * c;
        double c2 = -0.5 * c - Th * s, s2 = -0.5 * s + Th * c;
        double k = 2.0 / 3.0;
        double i0 = _filter[0].ProbeCurrent(0), i1 = _filter[1].ProbeCurrent(0), i2 = _filter[2].ProbeCurrent(0);
        double v0 = V(v, _pcc[0]), v1 = V(v, _pcc[1]), v2 = V(v, _pcc[2]);
        Id = k * (i0 * c + i1 * c1 + i2 * c2); Iq = -k * (i0 * s + i1 * s1 + i2 * s2);
        Vd = k * (v0 * c + v1 * c1 + v2 * c2); Vq = -k * (v0 * s + v1 * s1 + v2 * s2);
        Vdc = V(v, _dcP) - V(v, _dcN);
    }
}

/// <summary>The outer loop of a VSC station: turns a power or DC-voltage set point into the d-axis current reference of the inner
/// <see cref="VscCurrentController"/>. Power: <c>i_d* = (2/3) P* / v_d + integral(w_p (2/(3 v_d,nom)) (P* - P))</c> (feed-forward plus an
/// integral trim, so the steady state is exactly P*). DC voltage: a PI on <c>V_dc - V_dc*</c> (positive error injects more into the grid),
/// <c>Kp = 2 zeta w_n / k</c>, <c>Ki = w_n^2 / k</c>, <c>k = 3 v_d / (2 C V_dc*)</c>, from the integrator plant <c>C V dV/dt = -3/2 v_d i_d</c>
/// (zeta = 0.9).</summary>
public sealed class VscOuterLoop
{
    private readonly VscStationMode _mode;
    private readonly VscStationMeasurement _m;
    private readonly double _setPoint, _iq, _vdNominal;
    private readonly PiController _pi;
    private readonly double _ts;

    internal VscOuterLoop(VscStationSpec spec, VscStationMeasurement m, double samplePeriodSeconds)
    {
        _mode = spec.Mode; _m = m; _setPoint = spec.SetPoint; _iq = spec.ReactiveCurrentAmps; _ts = samplePeriodSeconds;
        _vdNominal = spec.GridPeakVolts;
        double lim = spec.CurrentLimitAmps;
        if (_mode == VscStationMode.Power)
            _pi = new PiController(0.0, spec.OuterBandwidthRadPerS * 2.0 / (3.0 * _vdNominal), -lim, lim);
        else
        {
            double k = 3.0 * _vdNominal / (2.0 * spec.DcCapF * spec.SetPoint);
            double wn = spec.OuterBandwidthRadPerS;
            _pi = new PiController(2.0 * 0.9 * wn / k, wn * wn / k, -lim, lim);
        }
    }

    /// <summary>The reference function for the inner controller (called once per control sample).</summary>
    public (double Id, double Iq) Reference(double t)
    {
        double vd = Math.Max(_m.Vd, 0.1 * _vdNominal);
        if (_mode == VscStationMode.Power)
        {
            double trim = _pi.Step(_setPoint - _m.P, _ts);
            return (2.0 * _setPoint / (3.0 * vd) + trim, _iq);
        }
        return (_pi.Step(_m.Vdc - _setPoint, _ts), _iq);
    }
}

/// <summary>A VSC HVDC station built into a network: a grid source behind its impedance, a phase reactor, a converter (two-level
/// average, MMC arm-average or MMC sub-module level), a DC link, a measurement element, the inner dq current controller and the outer
/// loop. Nodes are named <c>prefix + name</c> so the station can sit in a partition (use <c>"S1/"</c> with
/// <see cref="Partition.PartitionedModel"/>): <c>gen0..2</c> (ideal source), <c>pcc0..2</c>, <c>cv0..2</c> (converter AC), <c>dc.p</c>, <c>dc.n</c>.</summary>
public sealed class VscHvdcStation
{
    private VscHvdcStation(string name) { Name = name; }

    /// <summary>The station name (element-name prefix).</summary>
    public string Name { get; }
    /// <summary>DC positive node id.</summary>
    public int DcPositive { get; private set; }
    /// <summary>DC negative node id.</summary>
    public int DcNegative { get; private set; }
    /// <summary>The measurement element.</summary>
    public VscStationMeasurement Measurement { get; private set; } = null!;
    /// <summary>The inner controller.</summary>
    public VscCurrentController Controller { get; private set; } = null!;
    /// <summary>The converter model, for the result's model-class statement.</summary>
    public IConverterModel Converter { get; private set; } = null!;
    /// <summary>The MMC (null for the two-level converter).</summary>
    public MmcConverter? Mmc { get; private set; }
    /// <summary>Element name of the filter branch of phase <paramref name="k"/> (current converter to PCC).</summary>
    public string FilterBranch(int k) => $"{Name}.f{k}";
    /// <summary>The DC capacitor element names (positive, negative).</summary>
    public (string Positive, string Negative) DcCapacitors { get; private set; }

    /// <summary>Build the station. <paramref name="stepSeconds"/> is the solver step (it sets the control sample period).</summary>
    public static VscHvdcStation Add(NodalNetwork net, string name, string nodePrefix, VscStationSpec s, double stepSeconds)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(s);
        static Exception Refuse(string m) => DomainRefusal.Mark(new ArgumentException(m));
        if (!(s.GridPeakVolts > 0) || !(s.DcNominalVolts > 0) || !(s.DcCapF > 0) || !(s.FrequencyHz > 0) || !(s.FilterLHenry > 0) || !(s.GridLHenry >= 0)
            || !(s.GridROhm >= 0) || !(s.FilterROhm >= 0) || !(stepSeconds > 0) || s.ControlEvery < 1)
            throw Refuse($"{name}: voltages, capacitance, frequency, reactor inductance and step must be positive, resistances not negative.");
        if (s.Mode == VscStationMode.DcVoltage && !(s.SetPoint > 0)) throw Refuse($"{name}: the DC voltage set point must be positive.");
        if (s.Converter != VscStationConverter.TwoLevelAverage && s.Mmc is null) throw Refuse($"{name}: an MMC station needs MMC data.");
        var st = new VscHvdcStation(name);
        double w = 2.0 * Math.PI * s.FrequencyHz;
        int[] gen = new int[3], pcc = new int[3], cv = new int[3];
        for (int k = 0; k < 3; k++)
        {
            gen[k] = net.Node($"{nodePrefix}gen{k}"); pcc[k] = net.Node($"{nodePrefix}pcc{k}"); cv[k] = net.Node($"{nodePrefix}cv{k}");
        }
        double v0 = s.InitialDcVolts > 0 ? s.InitialDcVolts : s.DcNominalVolts;
        DcLink dc = DcLink.Add(net, nodePrefix + "dc", s.DcCapF, v0, 0.0, 0.0);
        // Element names of the link are fixed by DcLink ("<nodePrefix>dc.cp"); keep the station's own names in step.
        st.DcPositive = dc.Positive; st.DcNegative = dc.Negative;
        st.DcCapacitors = (dc.PositiveCapacitor, dc.NegativeCapacitor);
        for (int k = 0; k < 3; k++)
        {
            int kk = k;
            double ang = s.GridAngleRad - kk * (2.0 * Math.PI / 3.0);
            net.Add(new NodalVoltageSource($"{name}.e{k}", gen[k], 0.0, t => s.GridPeakVolts * DetMath.Sin(w * t + ang)));
            net.Add(NodalBranch.SeriesRl($"{name}.g{k}", gen[k], pcc[k], s.GridROhm, Math.Max(s.GridLHenry, 1e-12)));
        }
        var filter = new NodalElement[3];
        for (int k = 0; k < 3; k++) filter[k] = net.Add(NodalBranch.SeriesRl($"{name}.f{k}", cv[k], pcc[k], s.FilterROhm, s.FilterLHenry));
        double phi = s.GridAngleRad - Math.PI / 2.0;
        var meas = net.Add(new VscStationMeasurement($"{name}.meas", filter, pcc, dc.Positive, dc.Negative, s.FrequencyHz, phi));
        double ts = s.ControlEvery * stepSeconds;
        var outer = new VscOuterLoop(s, meas, ts);
        double rTune = s.FilterROhm, lTune = s.FilterLHenry;
        if (s.Converter == VscStationConverter.TwoLevelAverage) rTune += s.ConverterRsOhm;
        else { rTune += 0.5 * s.Mmc!.ArmResistanceOhm; lTune += 0.5 * s.Mmc.ArmInductanceH; }
        var ctl = new VscCurrentController($"{name}.ctl", filter, pcc, dc.Positive, dc.Negative, s.FrequencyHz, phi, rTune, lTune, s.CurrentBandwidthRadPerS,
            s.DcNominalVolts, s.ControlEvery, outer.Reference);
        // The element order matters: measurement before the controller (it reads the measurement in its Accept).
        if (s.Converter == VscStationConverter.TwoLevelAverage)
        {
            var conv = net.Add(new AverageValueConverter($"{name}.conv", dc.Positive, dc.Negative, cv, s.ConverterRsOhm, s.DcNominalVolts, ctl.Reference));
            st.Converter = conv;
        }
        else
        {
            MmcConverter mmc = MmcConverter.Add(net, $"{name}.mmc", dc.Positive, dc.Negative, cv, s.Mmc!,
                s.Converter == VscStationConverter.MmcDetailed ? MmcStackKind.Detailed : MmcStackKind.ArmAverage, ctl.Reference);
            st.Converter = mmc; st.Mmc = mmc;
            if (s.SuppressionBandwidthRadPerS > 0)
                net.Add(new MmcCirculatingCurrentControl($"{name}.ccsc", mmc, s.FrequencyHz, s.GridAngleRad, s.SuppressionBandwidthRadPerS, s.ControlEvery));
        }
        net.Add(ctl);
        st.Measurement = meas; st.Controller = ctl;
        return st;
    }
}
