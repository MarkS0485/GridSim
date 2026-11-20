using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Emt.FrequencyDependent;
using GridSim.Core.Dynamics.Emt.Hvdc;
using GridSim.Core.Dynamics.Emt.Machines;
using GridSim.Core.Dynamics.Emt.PowerElectronics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.Partition;
using GridSim.Core.Dynamics.Emt.Nonlinear;
using GridSim.Core.Lines;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt;

// RC4 R4-16: the typed three-phase study behind `gridsim emt3` (strict gridsim-emt3/1, parsed in GridSim.Cli). Everything is SI
// units; a bus has the three nodes "<bus>.a", "<bus>.b", "<bus>.c". The existing `emt` verb and ThreePhaseEmtEngine are untouched.

/// <summary>A balanced three-phase wye source behind an optional series R-L: phase a leads by <c>AngleDeg</c>, b lags it by 120
/// degrees. With <c>RsOhm = 0</c> and <c>LsH = 0</c> it is an ideal source at the bus; otherwise a Thevenin source.</summary>
public sealed record Emt3Source(string Name, string Bus, double VllRms, double FrequencyHz, double AngleDeg, double RsOhm, double LsH);

/// <summary>A three-phase series branch of TOTAL (not per-km) sequence impedance: positive (R1, L1) and zero (R0, L0).
/// It is stamped as the coupled phase matrices Rs = (R0 + 2 R1)/3, Rm = (R0 - R1)/3 (the same for L).</summary>
public sealed record Emt3Branch(string Name, string From, string To, double R1Ohm, double L1H, double R0Ohm, double L0H);

/// <summary>A wye shunt to ground at a bus: each non-zero part is its own element per phase.</summary>
public sealed record Emt3Shunt(string Name, string Bus, double ROhm, double CF, double LH);

/// <summary>A transmission line from the R4-04 line constants. <c>Model</c> is <c>fd</c> (frequency dependent: fitted
/// Yc and A over <c>Fit</c>'s band, from the geometry at each band frequency) or <c>bergeron</c> (constant parameter, from the
/// geometry's own frequency; lossless but for a distortionless-line attenuation exp(-R l / Zc)).</summary>
public sealed record Emt3Line(string Name, string From, string To, double LengthKm, string Model, LineGeometry Geometry, FdFitOptions Fit);

/// <summary>A shunt fault on selected phases (letters of "abc"), to ground or, when <c>ToGround</c> is false, to a common
/// isolated fault star point. Applied at <c>AtS</c>, cleared at <c>ClearS</c> (null = never).</summary>
public sealed record Emt3Fault(string Name, string Bus, string Phases, double ROhm, bool ToGround, double AtS, double? ClearS);

/// <summary>A change of state of a switch.</summary>
public sealed record Emt3SwitchEvent(double AtS, bool Close);

/// <summary>A three-phase breaker between two buses (or from a bus to ground when <c>To</c> is null), phases as letters.</summary>
public sealed record Emt3Switch(string Name, string From, string? To, string Phases, double RonOhm, double RoffOhm, bool Closed, IReadOnlyList<Emt3SwitchEvent> Events);

/// <summary>A surge arrester per listed phase from the bus to ground: power law (<c>Points</c> null) or piecewise V-I (volts, amps).</summary>
public sealed record Emt3Arrester(string Name, string Bus, string Phases, double VrefV, double IrefA, double Alpha, IReadOnlyList<(double X, double Y)>? Points);

/// <summary>A saturable shunt inductor per listed phase from the bus to ground (current-flux curve: Wb, A).</summary>
public sealed record Emt3SaturableInductor(string Name, string Bus, string Phases, IReadOnlyList<(double X, double Y)> Curve, double InitialFluxWb);

/// <summary>A three single-phase-unit saturable transformer bank (T model, secondary referred to the primary), phase by phase.</summary>
public sealed record Emt3SaturableTransformer(string Name, string PrimaryBus, string SecondaryBus, double R1Ohm, double Ll1H, double R2Ohm, double Ll2H,
    double CoreLossOhm, IReadOnlyList<(double X, double Y)> Curve, double[] ResidualFluxWb);

/// <summary>Synchronous machine dynamic data on the machine base (the RMS parameter set of <see cref="SubtransientMachine.Parameters"/>, plus
/// the armature resistance and zero-sequence reactance the RMS set lacks). <c>XlPu</c> 0 means the leakage default of <see cref="SynchronousMachineCircuit"/>.</summary>
public sealed record Emt3SynchronousData(double InertiaS, double DampingPu, double XdPu, double XdpPu, double XdppPu, double XqPu, double XqpPu, double XqppPu,
    double TdopS, double TdoppS, double TqopS, double TqoppS, double XlPu, double RaPu, double? X0Pu);

/// <summary>Single-cage induction machine data on the machine base (the case model's circuit) and the inertia constant.</summary>
public sealed record Emt3InductionData(double RaPu, double XaPu, double XmPu, double R1Pu, double X1Pu, double InertiaS);

/// <summary>The initial operating point of a synchronous machine: <c>no-load</c> (terminal voltage <c>VPu</c> at <c>AngleDeg</c>, no current) or
/// <c>phasor</c> (the same voltage and the power <c>PMw</c>, <c>QMvar</c> delivered to the network). Angles follow the source convention: phase a is
/// <c>sin(w t + angle)</c>.</summary>
public sealed record Emt3MachineInitial(string Mode, double VPu, double AngleDeg, double PMw, double QMvar);

/// <summary>From <c>AtS</c> the input is its initial balanced value plus <c>DeltaPu</c> (per unit of the machine base).</summary>
public sealed record Emt3ValueStep(double AtS, double DeltaPu);

/// <summary>Induction machine shaft load <c>Tl = K w^Exponent</c> in pu of rated torque, w in pu of synchronous speed.</summary>
public sealed record Emt3LoadTorque(double KPu, double SpeedExponent);

/// <summary>A rotating machine on a bus: <c>Kind</c> is <c>synchronous</c> (dq, generator convention, grounded or isolated neutral) or
/// <c>induction</c> (alpha-beta, motor convention, three-wire). Exactly one of <c>Synchronous</c> and <c>Induction</c> is set.</summary>
public sealed record Emt3Machine(string Name, string Kind, string Bus, bool NeutralGrounded, double RatingMva, double RatedKvLl, double FrequencyHz,
    Emt3SynchronousData? Synchronous, Emt3InductionData? Induction, Emt3MachineInitial? Initial,
    IReadOnlyList<Emt3ValueStep> TorqueSteps, IReadOnlyList<Emt3ValueStep> FieldVoltageSteps, double StartSpeedPu, Emt3LoadTorque? Load);

/// <summary>What the runner found for a machine: the derived initial state (null for the quantities a kind does not have).</summary>
public sealed record Emt3MachineReport(string Name, string Kind, double? LoadAngleRad, double? FieldVoltagePu, double? FieldCurrentPu,
    double? MechanicalTorquePu, double? IdPu, double? IqPu, double? StartSpeedPu);

/// <summary>A DC bus: a split DC link (the Core <see cref="DcLink"/>): rails <c>Name.p</c> and <c>Name.n</c> either side of the grounded mid-point, two
/// capacitors (series value <c>CapacitanceF</c>) charged to <c>InitialV</c> pole to pole and an optional Thevenin supply of <c>SupplyV</c> pole to pole
/// behind <c>SupplyOhm</c> (0 is an ideal supply; <c>SupplyV</c> 0 is no supply).</summary>
public sealed record Emt3DcBus(string Name, double CapacitanceF, double InitialV, double SupplyV, double SupplyOhm);

/// <summary>A series R-L filter between a converter's terminals and its AC bus.</summary>
public sealed record Emt3Filter(double ROhm, double LH);

/// <summary>An open-loop sinusoidal modulation <c>m sin(2 pi f t + phase - 120 deg k)</c> per phase k.</summary>
public sealed record Emt3Modulation(double Index, double FrequencyHz, double PhaseDeg);

/// <summary>The triangle carrier of a switched converter: frequency and phase as a fraction of a period.</summary>
public sealed record Emt3Pwm(double CarrierHz, double CarrierPhase);

/// <summary>From <c>AtS</c> the d and q current references are <c>IdA</c> and <c>IqA</c> (amperes, peak).</summary>
public sealed record Emt3RefStep(double AtS, double IdA, double IqA);

/// <summary>The sampled dq current controller of a grid-connected converter (the Core <see cref="VscCurrentController"/>): tuned on the filter
/// (Kp = alpha L, Ki = alpha R), a control sample every <c>ControlEvery</c> solver steps, the d axis on the phase-a sine axis shifted by
/// <c>PhaseDeg</c> (the angle of a source with that <c>angle_deg</c>).</summary>
public sealed record Emt3Control(double BandwidthRadPerS, int ControlEvery, double FrequencyHz, double PhaseDeg, double IdA, double IqA, IReadOnlyList<Emt3RefStep> Steps);

/// <summary>A three-phase two-level voltage-source converter between an AC bus and a DC bus: <c>Kind</c> <c>average</c> (the Core
/// <see cref="AverageValueConverter"/>, series resistance <c>RsOhm</c>) or <c>switched</c> (the Core <see cref="TwoLevelBridge"/> of gated diodes, on
/// resistance <c>RsOhm</c>, off <c>RoffOhm</c>, sine PWM <see cref="Emt3Pwm"/>). The reference is an open-loop <see cref="Emt3Modulation"/> or the
/// <see cref="Emt3Control"/> loop (which needs a filter). With a filter the converter terminals are internal nodes behind it.</summary>
public sealed record Emt3Converter(string Name, string Kind, string AcBus, string DcBus, double RsOhm, double RoffOhm, double? NominalVdcV,
    Emt3Filter? Filter, Emt3Modulation? Modulation, Emt3Pwm? Pwm, Emt3Control? Control);

/// <summary>Modular multilevel converter arm data (the Core <see cref="MmcParameters"/> without the nominal DC voltage, which is the station's).</summary>
public sealed record Emt3MmcData(int SubmodulesPerArm, double SubmoduleCapF, double ArmResistanceOhm, double ArmInductanceH);

/// <summary>A VSC HVDC station (the Core <see cref="VscHvdcStation"/>): an ideal grid source behind a grid impedance, the converter reactor, the
/// converter (<c>Converter</c> is <c>average</c>, <c>mmc-average</c> or <c>mmc-detailed</c>), a DC link, the measurement, the dq current controller
/// and the outer loop (<c>Mode</c> <c>power</c>: <c>SetPoint</c> is the active power injected into the grid at the PCC in watts; <c>dc-voltage</c>:
/// the pole-to-pole voltage in volts). <c>DcSupplyV</c> adds a stiff supply of that voltage pole to pole, each pole behind <c>DcSupplyOhm</c> (0 is ideal, and the
/// current of an ideal source is not measured); null leaves the DC side to a DC line.</summary>
public sealed record Emt3HvdcStation(double GridVllRmsV, double FrequencyHz, double GridAngleDeg, double GridROhm, double GridLH, double FilterROhm, double FilterLH,
    double ConverterRsOhm, double DcNominalV, double DcCapF, string Converter, Emt3MmcData? Mmc, string Mode, double SetPoint, double CurrentBandwidthRadPerS,
    double OuterBandwidthRadPerS, int ControlEvery, double ReactiveCurrentA, double CurrentLimitA, double InitialDcV, double SuppressionBandwidthRadPerS, double? DcSupplyV,
    double DcSupplyOhm = 0.0);

/// <summary>From <c>AtS</c> the firing delay is <c>AlphaDeg</c>.</summary>
public sealed record Emt3AlphaStep(double AtS, double AlphaDeg);

/// <summary>A line-commutated bridge group (the Core <see cref="LccBridgeEmt"/>, 6 or 12 pulses, thyristor valves, own transformer secondary sources) into a series
/// R-L DC load. Nodes <c>Name.dcp</c> and <c>Name.dcn</c>.</summary>
public sealed record Emt3HvdcLcc(double VllRmsV, double FrequencyHz, double CommutationLH, double SourceROhm, int Pulses, double ValveRonOhm, double ValveRoffOhm,
    double NeutralReferenceOhm, double AlphaDeg, IReadOnlyList<Emt3AlphaStep> AlphaSteps, double LoadROhm, double LoadLH);

/// <summary>A two-pole DC line between two VSC stations: half of <c>ROhm</c> (the loop resistance) in each pole at the <c>From</c> end, then a Bergeron line
/// (<c>SurgeOhm</c>, <c>DelayS</c>) per pole. It starts in the steady state that carries <c>InitialCurrentA</c> from <c>From</c> to <c>To</c> at the stations' initial DC voltages.</summary>
public sealed record Emt3DcLine(string From, string To, double ROhm, double SurgeOhm, double DelayS, double InitialCurrentA);

/// <summary>An HVDC entry: <c>Kind</c> <c>vsc</c> (<see cref="Emt3HvdcStation"/>), <c>lcc</c> (<see cref="Emt3HvdcLcc"/>) or <c>dc-line</c> (<see cref="Emt3DcLine"/>).</summary>
public sealed record Emt3Hvdc(string Name, string Kind, Emt3HvdcStation? Station, Emt3HvdcLcc? Lcc, Emt3DcLine? Line);

/// <summary>One output trace. <c>Kind</c> is <c>voltage</c> (Target = bus), <c>current</c> (Target = a branch, line,
/// arrester, saturable inductor, switch, fault or source; for a line <c>End</c> is <c>from</c> or <c>to</c>) or <c>flux</c> (Target = a saturable
/// inductor or transformer; Wb). <c>Phase</c> is a, b or c. The machine kinds take a machine as Target and no phase: <c>speed</c> (pu of
/// synchronous speed), <c>torque</c> (electromagnetic, pu), <c>rotor-angle</c> (load angle of a synchronous machine, rad) and
/// <c>field-current</c> (pu); a machine's <c>current</c> takes a phase. A DC bus gives <c>dc-voltage-positive</c> and <c>dc-voltage-negative</c> (each rail to the
/// grounded mid-point, volts); a converter gives <c>current</c> (with a phase: its filter current, or for an unfiltered average converter its leg current) and,
/// for an average converter, <c>dc-current</c>. An HVDC VSC station gives <c>dc-voltage-positive</c>, <c>dc-voltage-negative</c>, a filter <c>current</c> with a
/// phase and, with a <c>DcSupplyV</c>, <c>dc-current</c> (its supply); an LCC bridge gives <c>dc-voltage-positive</c>, <c>dc-voltage-negative</c> and <c>dc-current</c> (the
/// load current); a DC line gives <c>dc-current</c> at its <c>End</c>.</summary>
public sealed record Emt3Output(string Name, string Kind, string Target, string Phase, string End);

/// <summary>The whole study.</summary>
public sealed record Emt3Study(
    double StepS, double DurationS, int SampleEvery, double FrequencyHz, IntegrationRule Rule, NodalEventRule EventRule, NewtonPolicy Newton,
    IReadOnlyList<string> Buses,
    IReadOnlyList<Emt3Source> Sources, IReadOnlyList<Emt3Branch> Branches, IReadOnlyList<Emt3Shunt> Shunts, IReadOnlyList<Emt3Line> Lines,
    IReadOnlyList<Emt3SaturableTransformer> Transformers, IReadOnlyList<Emt3SaturableInductor> SaturableInductors,
    IReadOnlyList<Emt3Arrester> Arresters, IReadOnlyList<Emt3Switch> Switches, IReadOnlyList<Emt3Fault> Faults,
    IReadOnlyList<Emt3Output> Outputs,
    IReadOnlyList<Emt3Machine>? Machines = null,
    IReadOnlyList<Emt3DcBus>? DcBuses = null, IReadOnlyList<Emt3Converter>? Converters = null, IReadOnlyList<Emt3Hvdc>? Hvdc = null);

/// <summary>The result of an <see cref="Emt3Study"/>.</summary>
/// <param name="Run">The solver result: status, traces, statistics.</param>
/// <param name="LineFits">Per line, the fit report (null for a constant-parameter line).</param>
/// <param name="Machines">Per machine, the initial state found (null when the study has no machines).</param>
/// <param name="Converters">Name, model class and description of each converter (null when the study has none).</param>
/// <param name="SwitchingTiming">The switching-instant contract the Core states for converter runs (null when the study has no converter).</param>
public sealed record Emt3Result(NodalResult Run, IReadOnlyList<(string Line, FdLineFitReport? Fit)> LineFits, IReadOnlyList<Emt3MachineReport>? Machines = null,
    IReadOnlyList<(string Name, string ModelClass, string Description)>? Converters = null, string? SwitchingTiming = null);

/// <summary>Builds and runs an <see cref="Emt3Study"/> on the nodal solver.</summary>
public static class Emt3Runner
{
    /// <summary>Phase letters in order.</summary>
    public const string PhaseLetters = "abc";

    /// <summary>Build the network and run it.</summary>
    /// <exception cref="ArgumentException">The study refers to a missing bus, element or phase, or an element is unusable.</exception>
    public static Emt3Result Run(Emt3Study study)
    {
        ArgumentNullException.ThrowIfNull(study);
        static Exception R(string m) => DomainRefusal.Mark(new ArgumentException(m));
        var net = new NodalNetwork();
        var buses = new HashSet<string>(study.Buses, StringComparer.Ordinal);
        if (buses.Count != study.Buses.Count) throw R("Bus names must be unique.");
        foreach (string b in study.Buses) foreach (char p in PhaseLetters) net.Node(Nd(b, p));
        void NeedBus(string who, string bus) { if (!buses.Contains(bus)) throw R($"{who}: no bus '{bus}'."); }
        var events = new List<NodalEvent>();
        var fits = new List<(string, FdLineFitReport?)>();
        // name -> how to find the per-phase element for a current probe
        var perPhase = new Dictionary<string, string>(StringComparer.Ordinal);      // name -> kind
        var names = new HashSet<string>(StringComparer.Ordinal);
        void NewName(string n) { if (!names.Add(n)) throw R($"The name '{n}' is used twice."); }

        foreach (Emt3Source s in study.Sources)
        {
            NewName(s.Name); NeedBus(s.Name, s.Bus);
            if (!(s.VllRms > 0) || !(s.FrequencyHz > 0) || !(s.RsOhm >= 0) || !(s.LsH >= 0)) throw R($"{s.Name}: voltage and frequency must be positive; Rs and Ls not negative.");
            double vpk = s.VllRms * Math.Sqrt(2.0 / 3.0), w = 2.0 * Math.PI * s.FrequencyHz, ang = s.AngleDeg * Math.PI / 180.0;
            for (int k = 0; k < 3; k++)
            {
                double phi = ang - k * 2.0 * Math.PI / 3.0;
                Func<double, double> emf = t => vpk * DetMath.Sin(w * t + phi);
                int bus = net.Node(Nd(s.Bus, PhaseLetters[k]));
                if (s.LsH > 0)
                {
                    int inner = net.Node($"{s.Name}.{PhaseLetters[k]}.int");
                    net.Add(new NodalVoltageSource($"{s.Name}.{PhaseLetters[k]}", inner, 0.0, emf));
                    net.Add(NodalBranch.SeriesRl($"{s.Name}.{PhaseLetters[k]}.z", inner, bus, s.RsOhm, s.LsH));
                }
                else net.Add(new NodalVoltageSource($"{s.Name}.{PhaseLetters[k]}", bus, s.RsOhm, emf));
            }
            perPhase[s.Name] = s.LsH > 0 ? "sourcez" : "source";
        }
        foreach (Emt3Branch b in study.Branches)
        {
            NewName(b.Name); NeedBus(b.Name, b.From); NeedBus(b.Name, b.To);
            if (!(b.L1H > 0) || !(b.L0H > 0) || !(b.R1Ohm >= 0) || !(b.R0Ohm >= 0)) throw R($"{b.Name}: inductances must be positive and resistances not negative.");
            var r = new double[3, 3]; var l = new double[3, 3];
            double rs = (b.R0Ohm + 2 * b.R1Ohm) / 3, rm = (b.R0Ohm - b.R1Ohm) / 3, ls = (b.L0H + 2 * b.L1H) / 3, lm = (b.L0H - b.L1H) / 3;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) { r[i, j] = i == j ? rs : rm; l[i, j] = i == j ? ls : lm; }
            net.Add(new NodalCoupledRl(b.Name, Nodes(net, b.From), Nodes(net, b.To), r, l));
            perPhase[b.Name] = "coupled";
        }
        foreach (Emt3Shunt s in study.Shunts)
        {
            NewName(s.Name); NeedBus(s.Name, s.Bus);
            if (!(s.ROhm >= 0) || !(s.CF >= 0) || !(s.LH >= 0)) throw R($"{s.Name}: shunt values must not be negative.");
            foreach (char p in PhaseLetters)
            {
                int n = net.Node(Nd(s.Bus, p));
                if (s.ROhm > 0) net.Add(NodalBranch.Resistor($"{s.Name}.{p}.r", n, NodalNetwork.Ground, s.ROhm));
                if (s.CF > 0) net.Add(NodalBranch.Capacitor($"{s.Name}.{p}.c", n, NodalNetwork.Ground, s.CF));
                if (s.LH > 0) net.Add(NodalBranch.Inductor($"{s.Name}.{p}.l", n, NodalNetwork.Ground, s.LH));
            }
        }
        foreach (Emt3Line ln in study.Lines)
        {
            NewName(ln.Name); NeedBus(ln.Name, ln.From); NeedBus(ln.Name, ln.To);
            FdLineModel model;
            if (ln.Model == "fd")
            {
                model = FdLineFitter.FromLineConstants(ln.Geometry, ln.LengthKm, ln.Fit, study.StepS);
                fits.Add((ln.Name, model.Report));
            }
            else
            {
                LineConstantsResult lc = LineConstantsSolver.Solve(ln.Geometry);
                if (lc.Sequence is not { } sq) throw R($"{ln.Name}: {lc.SequenceNote ?? "no sequence constants"}");
                double w = 2.0 * Math.PI * lc.FrequencyHz;
                FdMode Mode(string nm, System.Numerics.Complex z, double cKm)
                {
                    double lKm = z.Imaginary / w;
                    double zc = Math.Sqrt(lKm / cKm);
                    double tau = ln.LengthKm * Math.Sqrt(lKm * cKm);
                    double h = DetMath.Exp(-z.Real * ln.LengthKm / zc);
                    return FdMode.Bergeron(nm, zc, tau, h);
                }
                FdMode m0 = Mode("zero", sq.Z0, sq.C0FaradPerKm), m1 = Mode("positive", sq.Z1, sq.C1FaradPerKm);
                double s3 = Math.Sqrt(3.0), s6 = Math.Sqrt(6.0), s2 = Math.Sqrt(2.0);
                var t = new double[3, 3] { { 1 / s3, 1 / s3, 1 / s3 }, { 2 / s6, -1 / s6, -1 / s6 }, { 0, 1 / s2, -1 / s2 } };
                model = new FdLineModel([m0, m1, m1 with { Name = "positive-2" }], t,
                    new FdLineFitReport(lc.FrequencyHz, lc.FrequencyHz, 1, 0, ln.LengthKm * 1000.0, study.StepS, []));
                fits.Add((ln.Name, null));
            }
            net.Add(new FdLine(ln.Name, Nodes(net, ln.From), Nodes(net, ln.To), model.Modes, model.Transform));
            perPhase[ln.Name] = "line";
        }
        foreach (Emt3SaturableTransformer tr in study.Transformers)
        {
            NewName(tr.Name); NeedBus(tr.Name, tr.PrimaryBus); NeedBus(tr.Name, tr.SecondaryBus);
            var curve = new PiecewiseCurve(tr.Curve);
            for (int k = 0; k < 3; k++)
            {
                double res = tr.ResidualFluxWb.Length == 3 ? tr.ResidualFluxWb[k] : tr.ResidualFluxWb.Length == 1 ? tr.ResidualFluxWb[0] : 0.0;
                SaturableTransformer.AddTo(net, $"{tr.Name}.{PhaseLetters[k]}", Nd(tr.PrimaryBus, PhaseLetters[k]), Nd(tr.SecondaryBus, PhaseLetters[k]),
                    new SaturableTransformerData(tr.R1Ohm, tr.Ll1H, tr.R2Ohm, tr.Ll2H, tr.CoreLossOhm, curve, res));
            }
            perPhase[tr.Name] = "transformer";
        }
        foreach (Emt3SaturableInductor si in study.SaturableInductors)
        {
            NewName(si.Name); NeedBus(si.Name, si.Bus);
            var curve = new PiecewiseCurve(si.Curve);
            foreach (char p in Letters(si.Name, si.Phases))
                net.Add(new SaturableInductor($"{si.Name}.{p}", net.Node(Nd(si.Bus, p)), NodalNetwork.Ground, curve, si.InitialFluxWb));
            perPhase[si.Name] = "satind";
        }
        foreach (Emt3Arrester ar in study.Arresters)
        {
            NewName(ar.Name); NeedBus(ar.Name, ar.Bus);
            foreach (char p in Letters(ar.Name, ar.Phases))
            {
                int n = net.Node(Nd(ar.Bus, p));
                net.Add(ar.Points is null
                    ? SurgeArrester.PowerLaw($"{ar.Name}.{p}", n, NodalNetwork.Ground, ar.VrefV, ar.IrefA, ar.Alpha)
                    : SurgeArrester.Piecewise($"{ar.Name}.{p}", n, NodalNetwork.Ground, ar.Points));
            }
            perPhase[ar.Name] = "arrester";
        }
        foreach (Emt3Switch sw in study.Switches)
        {
            NewName(sw.Name); NeedBus(sw.Name, sw.From);
            if (sw.To is not null) NeedBus(sw.Name, sw.To);
            foreach (char p in Letters(sw.Name, sw.Phases))
            {
                int a = net.Node(Nd(sw.From, p)), b = sw.To is null ? NodalNetwork.Ground : net.Node(Nd(sw.To, p));
                net.Add(new NodalSwitch($"{sw.Name}.{p}", a, b, sw.RonOhm, sw.RoffOhm, sw.Closed));
                foreach (Emt3SwitchEvent ev in sw.Events) events.Add(new NodalEvent(ev.AtS, $"{sw.Name}.{p}", ev.Close));
            }
            perPhase[sw.Name] = "switch";
        }
        foreach (Emt3Fault f in study.Faults)
        {
            NewName(f.Name); NeedBus(f.Name, f.Bus);
            if (!(f.ROhm > 0)) throw R($"{f.Name}: the fault resistance must be positive.");
            int star = f.ToGround ? NodalNetwork.Ground : net.Node(f.Name + ".star");
            foreach (char p in Letters(f.Name, f.Phases))
            {
                net.Add(new NodalSwitch($"{f.Name}.{p}", net.Node(Nd(f.Bus, p)), star, f.ROhm, 1e9, false));
                events.Add(new NodalEvent(f.AtS, $"{f.Name}.{p}", true));
                if (f.ClearS is { } c) events.Add(new NodalEvent(c, $"{f.Name}.{p}", false));
            }
            perPhase[f.Name] = "switch";
        }

        List<Emt3MachineReport>? machineReports = null;
        if (study.Machines is { Count: > 0 } machineList)
        {
            machineReports = [];
            foreach (Emt3Machine m in machineList)
            {
                NewName(m.Name); NeedBus(m.Name, m.Bus);
                machineReports.Add(AddMachine(net, m, perPhase));
            }
        }

        var dcBuses = new Dictionary<string, (DcLink Link, double InitialV)>(StringComparer.Ordinal);
        foreach (Emt3DcBus d in study.DcBuses ?? [])
        {
            NewName(d.Name);
            dcBuses[d.Name] = (DcLink.Add(net, d.Name, d.CapacitanceF, d.InitialV, d.SupplyV, d.SupplyOhm), d.InitialV);
            perPhase[d.Name] = "dcbus";
        }
        var converters = new List<IConverterModel>();
        foreach (Emt3Converter c in study.Converters ?? [])
        {
            NewName(c.Name); NeedBus(c.Name, c.AcBus);
            converters.Add(AddConverter(net, c, dcBuses, perPhase));
        }
        if (study.Hvdc is { Count: > 0 } hvdcList) AddHvdc(net, study.StepS, hvdcList, perPhase, NewName, converters);

        var probes = new List<NodalProbe>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Emt3Output o in study.Outputs)
        {
            if (!seen.Add(o.Name)) throw R($"Output name '{o.Name}' is used twice.");
            if (TakesNoPhase(o.Kind))
            {
                if (o.Phase.Length != 0) throw R($"Output '{o.Name}': kind {o.Kind} takes no phase.");
                probes.Add(o.Kind.StartsWith("dc-", StringComparison.Ordinal) ? DcProbe(o, perPhase) : MachineProbe(o, perPhase));
                continue;
            }
            int ph = o.Phase.Length == 1 ? PhaseLetters.IndexOf(o.Phase[0], StringComparison.Ordinal) : -1;
            if (ph < 0) throw R($"Output '{o.Name}': the phase must be a, b or c.");
            char pc = PhaseLetters[ph];
            switch (o.Kind)
            {
                case "voltage":
                    NeedBus("Output " + o.Name, o.Target);
                    probes.Add(new NodalProbe(o.Name, NodalProbeKind.NodeVoltage, Nd(o.Target, pc)));
                    break;
                case "current":
                    if (!perPhase.TryGetValue(o.Target, out string? kind)) throw R($"Output '{o.Name}': no element '{o.Target}'.");
                    probes.Add(kind switch
                    {
                        "line" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, o.Target, (o.End == "to" ? 3 : 0) + ph),
                        "coupled" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, o.Target, ph),
                        "source" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, $"{o.Target}.{pc}"),
                        "sourcez" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, $"{o.Target}.{pc}.z"),
                        "transformer" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, $"{o.Target}.{pc}.primary"),
                        "machine-synchronous" or "machine-induction" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, o.Target, ph),
                        "converter-filtered-average" or "converter-filtered-switched" or "hvdc-vsc" or "hvdc-vsc-supply" or "hvdc-vsc-ideal" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, $"{o.Target}.f{ph}"),
                        "hvdc-lcc" or "hvdc-dcline" => throw R($"Output '{o.Name}': use dc-current for the current of an LCC bridge or a DC line."),
                        "converter-average" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, o.Target, ph),
                        "converter-switched" => throw R($"Output '{o.Name}': a switched converter without a filter has no single current; give it a filter or measure a branch."),
                        _ => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, $"{o.Target}.{pc}"),
                    });
                    break;
                case "flux":
                    if (!perPhase.TryGetValue(o.Target, out string? k2) || (k2 != "satind" && k2 != "transformer")) throw R($"Output '{o.Name}': '{o.Target}' is not a saturable inductor or transformer.");
                    probes.Add(new NodalProbe(o.Name, NodalProbeKind.ElementState, k2 == "satind" ? $"{o.Target}.{pc}" : $"{o.Target}.{pc}.core"));
                    break;
                default:
                    throw R($"Output '{o.Name}': kind must be voltage, current, flux, speed, torque, rotor-angle, field-current, dc-voltage-positive, dc-voltage-negative or dc-current.");
            }
        }
        if (probes.Count == 0) throw R("The study has no output.");
        var opts = new NodalOptions { Rule = study.Rule, EventRule = study.EventRule, Newton = study.Newton, SampleEvery = study.SampleEvery };
        if (converters.Count > 0)
        {
            PowerElectronicsResult pe = PowerElectronicsRunner.Run(net, study.StepS, study.DurationS, probes, converters, events, opts);
            return new Emt3Result(pe.Run, fits, machineReports, pe.Converters.Select(x => (x.Name, x.Class.ToString(), x.Description)).ToArray(), pe.SwitchingTiming);
        }
        NodalResult res2 = NodalSolver.Run(net, study.StepS, study.DurationS, probes, events, opts);
        return new Emt3Result(res2, fits, machineReports);
    }

    /// <summary>The output kinds that name a machine, DC bus or converter quantity and take no phase.</summary>
    public static bool TakesNoPhase(string kind) =>
        kind is "speed" or "torque" or "rotor-angle" or "field-current" or "dc-voltage-positive" or "dc-voltage-negative" or "dc-current";

    private static NodalProbe DcProbe(Emt3Output o, Dictionary<string, string> perPhase)
    {
        static Exception R(string m) => DomainRefusal.Mark(new ArgumentException(m));
        if (!perPhase.TryGetValue(o.Target, out string? k)) throw R($"Output '{o.Name}': no element '{o.Target}'.");
        if (o.Kind == "dc-current")
            return k switch
            {
                "converter-average" or "converter-filtered-average" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, o.Target, 3),
                "hvdc-lcc" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, o.Target + ".load"),
                "hvdc-dcline" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, o.Target + ".p." + (o.End == "to" ? "b" : "a")),
                "hvdc-vsc-supply" => new NodalProbe(o.Name, NodalProbeKind.ElementCurrent, o.Target + ".dcp"),
                "hvdc-vsc-ideal" => throw R($"Output '{o.Name}': the current of the ideal DC supply of '{o.Target}' is not measured; give it a dc_supply_ohm above zero."),
                _ => throw R($"Output '{o.Name}': dc-current is defined for an average converter, an LCC bridge, a DC line and a VSC station with a dc_supply_v and a dc_supply_ohm; '{o.Target}' is none of these."),
            };
        string rail = o.Kind == "dc-voltage-positive" ? "p" : "n";
        return k switch
        {
            "dcbus" => new NodalProbe(o.Name, NodalProbeKind.NodeVoltage, $"{o.Target}.{rail}"),
            "hvdc-vsc" or "hvdc-vsc-supply" or "hvdc-vsc-ideal" => new NodalProbe(o.Name, NodalProbeKind.NodeVoltage, $"{o.Target}/dc.{rail}"),
            "hvdc-lcc" => new NodalProbe(o.Name, NodalProbeKind.NodeVoltage, $"{o.Target}.dc{rail}"),
            _ => throw R($"Output '{o.Name}': '{o.Target}' is not a DC bus, a VSC station or an LCC bridge."),
        };
    }

    private static void AddHvdc(NodalNetwork net, double stepS, IReadOnlyList<Emt3Hvdc> list, Dictionary<string, string> perPhase, Action<string> newName, List<IConverterModel> converters)
    {
        static Exception R(string x) => DomainRefusal.Mark(new ArgumentException(x));
        var stations = new Dictionary<string, Emt3HvdcStation>(StringComparer.Ordinal);
        foreach (Emt3Hvdc h in list)
        {
            newName(h.Name);
            if (h.Kind == "vsc")
            {
                Emt3HvdcStation s = h.Station ?? throw R($"{h.Name}: a VSC station needs its data.");
                VscStationConverter conv = s.Converter switch
                {
                    "average" => VscStationConverter.TwoLevelAverage,
                    "mmc-average" => VscStationConverter.MmcArmAverage,
                    "mmc-detailed" => VscStationConverter.MmcDetailed,
                    _ => throw R($"{h.Name}: the converter must be \"average\", \"mmc-average\" or \"mmc-detailed\" but is \"{s.Converter}\"."),
                };
                VscStationMode mode = s.Mode switch
                {
                    "power" => VscStationMode.Power,
                    "dc-voltage" => VscStationMode.DcVoltage,
                    _ => throw R($"{h.Name}: the mode must be \"power\" or \"dc-voltage\" but is \"{s.Mode}\"."),
                };
                if (!(s.GridVllRmsV > 0)) throw R($"{h.Name}: the grid voltage must be positive.");
                var spec = new VscStationSpec
                {
                    GridPeakVolts = s.GridVllRmsV * Math.Sqrt(2.0 / 3.0), FrequencyHz = s.FrequencyHz, GridAngleRad = s.GridAngleDeg * Math.PI / 180.0,
                    GridROhm = s.GridROhm, GridLHenry = s.GridLH, FilterROhm = s.FilterROhm, FilterLHenry = s.FilterLH, ConverterRsOhm = s.ConverterRsOhm,
                    DcNominalVolts = s.DcNominalV, DcCapF = s.DcCapF, Converter = conv, Mode = mode, SetPoint = s.SetPoint,
                    CurrentBandwidthRadPerS = s.CurrentBandwidthRadPerS, OuterBandwidthRadPerS = s.OuterBandwidthRadPerS, ControlEvery = s.ControlEvery,
                    ReactiveCurrentAmps = s.ReactiveCurrentA, CurrentLimitAmps = s.CurrentLimitA, InitialDcVolts = s.InitialDcV,
                    SuppressionBandwidthRadPerS = s.SuppressionBandwidthRadPerS,
                    Mmc = s.Mmc is { } mm ? new MmcParameters(mm.SubmodulesPerArm, mm.SubmoduleCapF, mm.ArmResistanceOhm, mm.ArmInductanceH, s.DcNominalV) : null,
                };
                VscHvdcStation st = VscHvdcStation.Add(net, h.Name, h.Name + "/", spec, stepS);
                converters.Add(st.Converter);
                if (s.DcSupplyV is { } vs)
                {
                    if (!(s.DcSupplyOhm >= 0)) throw R($"{h.Name}: the DC supply resistance must not be negative.");
                    net.Add(new NodalVoltageSource(h.Name + ".dcp", st.DcPositive, s.DcSupplyOhm, _ => 0.5 * vs));
                    net.Add(new NodalVoltageSource(h.Name + ".dcn", st.DcNegative, s.DcSupplyOhm, _ => -0.5 * vs));
                }
                perPhase[h.Name] = s.DcSupplyV is null ? "hvdc-vsc" : s.DcSupplyOhm > 0 ? "hvdc-vsc-supply" : "hvdc-vsc-ideal";
                stations[h.Name] = s;
            }
            else if (h.Kind == "lcc")
            {
                Emt3HvdcLcc l = h.Lcc ?? throw R($"{h.Name}: an LCC bridge needs its data.");
                if (!(l.LoadROhm > 0) || !(l.LoadLH > 0)) throw R($"{h.Name}: the DC load needs a positive resistance and inductance.");
                var spec = new LccBridgeSpec
                {
                    LineToLineRmsVolts = l.VllRmsV, FrequencyHz = l.FrequencyHz, CommutationLHenry = l.CommutationLH, SourceROhm = l.SourceROhm, Pulses = l.Pulses,
                    Valve = new ValveParameters(l.ValveRonOhm, l.ValveRoffOhm), NeutralReferenceOhm = l.NeutralReferenceOhm,
                };
                var steps = l.AlphaSteps.OrderBy(x => x.AtS).ToArray();
                double alpha0 = l.AlphaDeg * Math.PI / 180.0;
                Func<double, double> alpha = t =>
                {
                    double a = alpha0;
                    foreach (Emt3AlphaStep st in steps) if (t >= st.AtS) a = st.AlphaDeg * Math.PI / 180.0;
                    return a;
                };
                int p = net.Node(h.Name + ".dcp"), n = net.Node(h.Name + ".dcn");
                converters.Add(LccBridgeEmt.Add(net, h.Name, "", p, n, spec, alpha));
                net.Add(NodalBranch.SeriesRl(h.Name + ".load", p, n, l.LoadROhm, l.LoadLH));
                perPhase[h.Name] = "hvdc-lcc";
            }
            else if (h.Kind != "dc-line") throw R($"{h.Name}: the HVDC kind must be \"vsc\", \"lcc\" or \"dc-line\" but is \"{h.Kind}\".");
        }
        foreach (Emt3Hvdc h in list)
        {
            if (h.Kind != "dc-line") continue;
            Emt3DcLine d = h.Line ?? throw R($"{h.Name}: a DC line needs its data.");
            if (!stations.TryGetValue(d.From, out Emt3HvdcStation? a) || !stations.TryGetValue(d.To, out Emt3HvdcStation? b))
                throw R($"{h.Name}: a DC line joins two VSC stations of this study ('{d.From}' and '{d.To}').");
            if (!(d.ROhm > 0)) throw R($"{h.Name}: the loop resistance must be positive.");
            double rPole = 0.5 * d.ROhm;
            int lp = net.Node(h.Name + ".lp"), ln = net.Node(h.Name + ".ln");
            net.Add(NodalBranch.Resistor(h.Name + ".rp", net.Node(d.From + "/dc.p"), lp, rPole));
            net.Add(NodalBranch.Resistor(h.Name + ".rn", net.Node(d.From + "/dc.n"), ln, rPole));
            NodalBergeronLine lineP = NodalBergeronLine.Add(net, lp, NodalNetwork.Ground, net, net.Node(d.To + "/dc.p"), NodalNetwork.Ground, h.Name + ".p", d.SurgeOhm, d.DelayS);
            NodalBergeronLine lineN = NodalBergeronLine.Add(net, ln, NodalNetwork.Ground, net, net.Node(d.To + "/dc.n"), NodalNetwork.Ground, h.Name + ".n", d.SurgeOhm, d.DelayS);
            double v1 = a.InitialDcV > 0 ? a.InitialDcV : a.DcNominalV, v2 = b.InitialDcV > 0 ? b.InitialDcV : b.DcNominalV, i = d.InitialCurrentA;
            lineP.A.SetInitialState(0.5 * v1 - i * rPole, +i);
            lineP.B.SetInitialState(0.5 * v2, -i);
            lineN.A.SetInitialState(-0.5 * v1 + i * rPole, -i);
            lineN.B.SetInitialState(-0.5 * v2, +i);
            perPhase[h.Name] = "hvdc-dcline";
        }
    }

    private static IConverterModel AddConverter(NodalNetwork net, Emt3Converter c, Dictionary<string, (DcLink Link, double InitialV)> dcBuses, Dictionary<string, string> perPhase)
    {
        static Exception R(string x) => DomainRefusal.Mark(new ArgumentException(x));
        if (!dcBuses.TryGetValue(c.DcBus, out (DcLink Link, double InitialV) dc)) throw R($"{c.Name}: no DC bus '{c.DcBus}'.");
        if (c.Kind is not ("average" or "switched")) throw R($"{c.Name}: the converter kind must be \"average\" or \"switched\" but is \"{c.Kind}\".");
        bool switched = c.Kind == "switched";
        int[] grid = Nodes(net, c.AcBus), term = grid;
        var filt = new NodalElement[3];
        if (c.Filter is { } f)
        {
            if (!(f.ROhm >= 0) || !(f.LH > 0)) throw R($"{c.Name}: the filter needs a positive inductance and a resistance not negative.");
            term = new int[3];
            for (int k = 0; k < 3; k++)
            {
                term[k] = net.Node($"{c.Name}.cv{k}");
                filt[k] = net.Add(NodalBranch.SeriesRl($"{c.Name}.f{k}", term[k], grid[k], f.ROhm, f.LH));
            }
        }
        double vnom = c.NominalVdcV ?? dc.InitialV;
        Func<double, int, double> reference;
        if (c.Control is { } ct)
        {
            if (c.Filter is null) throw R($"{c.Name}: the current controller works on the filter currents, so the converter needs a filter.");
            var steps = ct.Steps.OrderBy(x => x.AtS).ToArray();
            Func<double, (double Id, double Iq)> refs = t =>
            {
                (double id, double iq) = (ct.IdA, ct.IqA);
                foreach (Emt3RefStep st in steps) if (t >= st.AtS) (id, iq) = (st.IdA, st.IqA);
                return (id, iq);
            };
            var ctl = net.Add(new VscCurrentController($"{c.Name}.ctl", filt, grid, dc.Link.Positive, dc.Link.Negative, ct.FrequencyHz,
                (ct.PhaseDeg - 90.0) * Math.PI / 180.0, c.Filter.ROhm, c.Filter.LH, ct.BandwidthRadPerS, vnom, ct.ControlEvery, refs));
            reference = ctl.Reference;
        }
        else if (c.Modulation is { } m)
            reference = SinePwm.SineReference(m.FrequencyHz, m.Index, m.PhaseDeg * Math.PI / 180.0);
        else throw R($"{c.Name}: a converter needs a modulation or a control.");
        perPhase[c.Name] = (c.Filter is not null ? "converter-filtered-" : "converter-") + (switched ? "switched" : "average");
        if (switched)
        {
            Emt3Pwm pwm = c.Pwm ?? throw R($"{c.Name}: a switched converter needs a pwm carrier.");
            return TwoLevelBridge.AddVsc(net, c.Name, dc.Link.Positive, dc.Link.Negative, term, new ValveParameters(c.RsOhm, c.RoffOhm), new SinePwm(pwm.CarrierHz, pwm.CarrierPhase, reference));
        }
        return net.Add(new AverageValueConverter(c.Name, dc.Link.Positive, dc.Link.Negative, term, c.RsOhm, vnom, reference));
    }

    private static NodalProbe MachineProbe(Emt3Output o, Dictionary<string, string> perPhase)
    {
        static Exception R(string m) => DomainRefusal.Mark(new ArgumentException(m));
        if (!perPhase.TryGetValue(o.Target, out string? k) || (k != "machine-synchronous" && k != "machine-induction")) throw R($"Output '{o.Name}': '{o.Target}' is not a machine.");
        bool sync = k == "machine-synchronous";
        // channels of the machine's ProbeStateAt: synchronous 0 speed, 1 load angle, 2 torque, 3 field current; induction 0 speed, 1 torque
        int ch = o.Kind switch
        {
            "speed" => 0,
            "torque" => sync ? 2 : 1,
            "rotor-angle" when sync => 1,
            "field-current" when sync => 3,
            _ => throw R($"Output '{o.Name}': kind {o.Kind} is not defined for the induction machine '{o.Target}'."),
        };
        return new NodalProbe(o.Name, NodalProbeKind.ElementState, o.Target, ch);
    }

    private static Emt3MachineReport AddMachine(NodalNetwork net, Emt3Machine m, Dictionary<string, string> perPhase)
    {
        static Exception R(string x) => DomainRefusal.Mark(new ArgumentException(x));
        int a = net.Node(Nd(m.Bus, 'a')), b = net.Node(Nd(m.Bus, 'b')), c = net.Node(Nd(m.Bus, 'c'));
        static Func<double, double>? Steps(IReadOnlyList<Emt3ValueStep> steps, double basis)
        {
            if (steps.Count == 0) return null;
            var sorted = steps.OrderBy(x => x.AtS).ToArray();
            return t =>
            {
                double v = basis;
                foreach (Emt3ValueStep s in sorted) if (t >= s.AtS) v = basis + s.DeltaPu;
                return v;
            };
        }
        if (m.Kind == "synchronous")
        {
            Emt3SynchronousData d = m.Synchronous ?? throw R($"{m.Name}: a synchronous machine needs its parameters.");
            var rms = new SubtransientMachine.Parameters(d.InertiaS, d.DampingPu, d.XdPu, d.XdpPu, d.XdppPu, d.XqPu, d.XqpPu, d.XqppPu,
                d.TdopS, d.TdoppS, d.TqopS, d.TqoppS, 0.0, 1.0, null, d.XlPu);
            int neutral = m.NeutralGrounded ? NodalNetwork.Ground : net.Node(m.Name + ".n");
            var mach = new SynchronousMachineEmt(m.Name, a, b, c, neutral, new SynchronousMachineEmtData(rms, m.RatingMva, m.RatedKvLl, m.FrequencyHz, d.RaPu, d.X0Pu));
            Emt3MachineInitial init = m.Initial ?? new Emt3MachineInitial("no-load", 1.0, 0.0, 0.0, 0.0);
            if (!(init.VPu > 0)) throw R($"{m.Name}: the initial terminal voltage must be positive.");
            // the Core phasor convention is Re(V e^{j w t}) (cosine); the study convention is sin(w t + angle), 90 degrees apart
            Complex v = DetMath.FromPolar(init.VPu, (init.AngleDeg - 90.0) * Math.PI / 180.0);
            Complex i = Complex.Zero;
            if (init.Mode == "phasor") i = Complex.Conjugate(new Complex(init.PMw, init.QMvar) / m.RatingMva / v);
            else if (init.Mode != "no-load") throw R($"{m.Name}: the initial mode must be \"no-load\" or \"phasor\".");
            SynchronousInitialState st = mach.InitialiseFromPhasors(v, i);
            mach.MechanicalTorquePu = Steps(m.TorqueSteps, st.MechanicalTorquePu);
            mach.FieldVoltagePu = Steps(m.FieldVoltageSteps, st.FieldVoltagePu);
            net.Add(mach);
            perPhase[m.Name] = "machine-synchronous";
            return new Emt3MachineReport(m.Name, m.Kind, st.LoadAngleRad, st.FieldVoltagePu, st.FieldCurrentPu, st.MechanicalTorquePu, st.IdPu, st.IqPu, null);
        }
        if (m.Kind == "induction")
        {
            Emt3InductionData d = m.Induction ?? throw R($"{m.Name}: an induction machine needs its parameters.");
            var circuit = new InductionMachine(0, m.Name, InductionPowerCode.Mechanical, 0.0, m.RatingMva, m.RatedKvLl, d.RaPu, d.XaPu, d.XmPu, d.R1Pu, d.X1Pu);
            var mach = new InductionMachineEmt(m.Name, a, b, c, new InductionMachineEmtData(circuit, m.FrequencyHz, d.InertiaS));
            if (m.Load is { } ld)
            {
                double k = ld.KPu, e = ld.SpeedExponent;
                mach.LoadTorquePu = e == 0.0 ? _ => k : e == 1.0 ? w => k * w : e == 2.0 ? w => k * w * w : w => k * DetMath.Pow(w, e);
            }
            mach.StartAtSpeed(m.StartSpeedPu);
            net.Add(mach);
            perPhase[m.Name] = "machine-induction";
            return new Emt3MachineReport(m.Name, m.Kind, null, null, null, null, null, null, m.StartSpeedPu);
        }
        throw R($"{m.Name}: the machine kind must be \"synchronous\" or \"induction\" but is \"{m.Kind}\".");
    }

    private static string Nd(string bus, char phase) => $"{bus}.{phase}";

    private static int[] Nodes(NodalNetwork net, string bus) => [net.Node(Nd(bus, 'a')), net.Node(Nd(bus, 'b')), net.Node(Nd(bus, 'c'))];

    private static IEnumerable<char> Letters(string who, string phases)
    {
        if (phases.Length == 0 || phases.Length > 3) throw DomainRefusal.Mark(new ArgumentException($"{who}: phases must be 1 to 3 letters of abc."));
        var seen = new HashSet<char>();
        foreach (char p in phases)
        {
            if (PhaseLetters.IndexOf(p, StringComparison.Ordinal) < 0 || !seen.Add(p))
                throw DomainRefusal.Mark(new ArgumentException($"{who}: phases must be distinct letters of abc but are '{phases}'."));
        }
        return phases;
    }
}
