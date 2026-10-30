namespace GridSim.Core.Model;

/// <summary>How a bus behaves in the power-flow solve.</summary>
public enum BusType
{
    /// <summary>Reference bus: voltage magnitude and angle fixed; absorbs the power balance.</summary>
    Slack,
    /// <summary>Generator bus: real power and voltage magnitude fixed; reactive power solved.</summary>
    Pv,
    /// <summary>Load bus: real and reactive power fixed; voltage solved.</summary>
    Pq,
}

/// <summary>
/// A node in the network. Loads are folded onto the bus (MATPOWER convention):
/// <see cref="Pd"/>/<see cref="Qd"/> are the demand here, in MW / MVAr. A shunt
/// admittance (e.g. a capacitor bank) is given as <see cref="Gs"/>/<see cref="Bs"/>,
/// the MW / MVAr it would draw at 1.0 p.u. voltage.
/// </summary>
public sealed record Bus(
    int Id,
    BusType Type,
    double Pd = 0,
    double Qd = 0,
    double Gs = 0,
    double Bs = 0,
    double BaseKv = 0,
    double Vm = 1.0,   // initial / fixed voltage magnitude (p.u.)
    double VaDeg = 0,  // initial / fixed voltage angle (degrees)
    string? Name = null,
    double X = 0,      // layout coordinate for visualisation (0..1, optional)
    double Y = 0,
    ZipLoad? Zip = null, // voltage-dependent load model; null = constant power (unchanged behaviour)
    double? VminPu = null, // this bus's own lower voltage limit (pu); null = the global band applies (F17)
    double? VmaxPu = null); // this bus's own upper voltage limit (pu); null = the global band applies (F17)

/// <summary>
/// A ZIP (polynomial) load model: the bus load varies with voltage as a constant-impedance (Z),
/// constant-current (I) and constant-power (P) blend. The drawn power is
/// <c>P(V) = Pd * (Ap*V^2 + Bp*V + Cp)</c> and <c>Q(V) = Qd * (Aq*V^2 + Bq*V + Cq)</c> with V in p.u.;
/// the coefficient triple in each row should sum to 1 so the model draws exactly <see cref="Bus.Pd"/> /
/// <see cref="Bus.Qd"/> at nominal voltage. The default (0,0,1) is pure constant power - i.e. the
/// historical behaviour - so a bus with no <see cref="Bus.Zip"/> solves bit-for-bit as before.
/// </summary>
public sealed record ZipLoad(
    double Ap = 0, double Bp = 0, double Cp = 1,
    double Aq = 0, double Bq = 0, double Cq = 1)
{
    /// <summary>True when this is anything other than pure constant power (i.e. it actually varies with V).</summary>
    public bool VariesWithVoltage => Ap != 0 || Bp != 0 || Aq != 0 || Bq != 0;

    /// <summary>The p.u. real-load multiplier at voltage <paramref name="v"/> (1.0 at nominal for a valid ZIP).</summary>
    public double PMultiplier(double v) => Ap * v * v + Bp * v + Cp;

    /// <summary>The p.u. reactive-load multiplier at voltage <paramref name="v"/>.</summary>
    public double QMultiplier(double v) => Aq * v * v + Bq * v + Cq;

    /// <summary>d/dV of the real-load multiplier - the Jacobian contribution.</summary>
    public double DPMultiplier(double v) => 2 * Ap * v + Bp;

    /// <summary>d/dV of the reactive-load multiplier.</summary>
    public double DQMultiplier(double v) => 2 * Aq * v + Bq;

    /// <summary>
    /// Compose a bus ZIP from an <paramref name="inverterFraction"/> of constant-power inverter load
    /// (heat-pump / EV / PV drives regulate power independent of voltage, so they are pure constant power
    /// (0,0,1)) blended with the <paramref name="nonInverter"/> remainder's composition. Because P(V) is
    /// linear in the coefficients and the two load slices add, the blend is the linear combination
    /// <c>f*(0,0,1) + (1-f)*nonInverter</c>. As the inverter fraction f rises the blend's Cp -> 1 and the
    /// load sheds its voltage/frequency dependence - the load-damping loss that grows with heat-pump demand.
    /// The GDA <c>heatpump_region_demand</c> fraction feeds f. The result stays a valid ZIP (each triple sums
    /// to 1) exactly when <paramref name="nonInverter"/> does. f is clamped to [0,1].
    /// </summary>
    public static ZipLoad FromInverterFraction(double inverterFraction, ZipLoad nonInverter)
    {
        double f = Math.Clamp(inverterFraction, 0.0, 1.0);
        double g = 1.0 - f;
        return new ZipLoad(
            Ap: g * nonInverter.Ap, Bp: g * nonInverter.Bp, Cp: f + g * nonInverter.Cp,
            Aq: g * nonInverter.Aq, Bq: g * nonInverter.Bq, Cq: f + g * nonInverter.Cq);
    }
}

/// <summary>
/// A generator attached to a bus. <see cref="Vg"/> is its voltage setpoint (used at
/// Slack and PV buses). <see cref="H"/> (inertia constant, MWs/MVA on
/// <see cref="MBase"/>) is not used by the load flow; the frequency and dynamics studies read it, with system
/// inertia = sum of H x MBase (<see cref="GridModel.SystemInertiaGvaS"/>). (D-DOC-207: this used to say it was
/// "ready for the frequency-dynamics phase", which has long since been built.)
/// </summary>
public sealed record Generator(
    int Bus,
    double Pg,
    double Qg = 0,
    double Vg = 1.0,
    double Qmin = double.NegativeInfinity,
    double Qmax = double.PositiveInfinity,
    double Pmin = 0,
    double Pmax = double.PositiveInfinity,
    double MBase = 100,
    double H = 0,
    string? Name = null,
    string Type = "",
    string? DynamicsClass = null,    // optional authored asset-dynamics class bound to this unit
    double SubtransientXdPu = 0,     // X''d on MBase for short-circuit studies; <=0 = use the synthesised default
    double NegativeSeqXPu = 0,       // X2 on MBase; <=0 = defaults to the effective X''d (round-rotor convention)
    bool? ConverterInterfaced = null,     // full-converter plant (fault current limited); null = inferred from Type
    double? ConverterFaultCurrentPu = null, // converter fault current k (x rated); null = default, 0 = none (LCC HVDC)
    bool PowerFlowOnly = false,           // a power-flow device (slack balancer, voltage-control unit), not physical plant
    double UnitTransformerXPu = 0,        // generator step-up transformer reactance on MBase; 0 = none modelled
    bool GridForming = false,             // converter plant with grid-forming (virtual synchronous machine) control
    bool InService = true,                // false = out of service: kept in the model (AllGenerators), never solved (F16)
    GeneratorCost? Cost = null,           // active-power cost curve (MATPOWER gencost); read by the costed DC-OPF (dcopf / acopf --costs, F19)
    GeneratorCost? ReactiveCost = null)   // reactive-power cost curve (gencost rows ng+1..2ng); carried, read by no solver
{
    /// <summary>
    /// The unit's source reactance for the fault studies: X''d plus its step-up transformer
    /// (<see cref="UnitTransformerXPu"/>), both on <see cref="MBase"/>. The network bus of a transmission-connected
    /// machine is the HV side of its unit transformer, so the transformer is in series with the machine.
    /// </summary>
    public double FaultSourceReactancePu(double fallbackXdPu) =>
        EffectiveSubtransientXdPu(fallbackXdPu) + Math.Max(0.0, UnitTransformerXPu);

    /// <summary>The negative-sequence counterpart of <see cref="FaultSourceReactancePu"/>: X2 plus the unit transformer.</summary>
    public double FaultSourceNegativeSeqReactancePu(double fallbackXdPu) =>
        EffectiveNegativeSeqXPu(fallbackXdPu) + Math.Max(0.0, UnitTransformerXPu);

    /// <summary>The machine's zero-sequence impedance to earth at its bus (pu on <see cref="MBase"/>; a grounded
    /// neutral, any grounding impedance included as 3 Zn), or null (the usual case: the machine has no zero-sequence path
    /// of its own and the zero-sequence network reaches it only through transformer neutrals). Set from PSS/E sequence
    /// data; the zero-sequence network stamps it as a shunt to earth.</summary>
    public MachineZeroSequence? ZeroSequenceZPu { get; init; }

    /// <summary>The fault-current multiple <c>k = I_skPF / I_rG</c> assumed for a converter-interfaced unit that
    /// carries no <see cref="ConverterFaultCurrentPu"/>. IEC 60909-0:2016 takes it from the manufacturer;
    /// grid-code fast-fault-current-injection capability is typically 1.0-1.5 x rated, and 1.2 is a mid value.</summary>
    public const double DefaultConverterFaultCurrentPu = 1.2;

    /// <summary>
    /// Whether this unit connects through a full-size power converter (battery, PV, type-4 wind, VSC HVDC)
    /// rather than a directly-coupled synchronous machine. A converter has no internal EMF behind a
    /// subtransient reactance: its fault contribution is a controlled current capped near its rating, so the
    /// short-circuit modules treat it as a current source (IEC 60909-0:2016) instead of an X''d shunt. An
    /// explicit <see cref="ConverterInterfaced"/> wins; otherwise it is inferred from <see cref="Type"/>
    /// (<see cref="ConverterPlant.IsConverterType"/>).
    /// </summary>
    public bool IsConverterInterfaced => ConverterInterfaced ?? ConverterPlant.IsConverterType(Type);

    /// <summary>The converter fault-current multiple <c>k</c> (<c>I_skPF = k * I_rG</c>): its own
    /// <see cref="ConverterFaultCurrentPu"/> when supplied, else <see cref="DefaultConverterFaultCurrentPu"/>. Zero
    /// is meaningful: a unit that feeds no fault current (a line-commutated HVDC link, a power-flow-only control
    /// unit) - it is then neither a source impedance nor a current source in the fault studies.</summary>
    public double EffectiveConverterFaultCurrentPu =>
        ConverterFaultCurrentPu is double k ? Math.Max(0.0, k) : DefaultConverterFaultCurrentPu;

    /// <summary>
    /// The subtransient reactance X''d (pu on <see cref="MBase"/>) the short-circuit modules should use for
    /// this machine: its own <see cref="SubtransientXdPu"/> when a real value is supplied (&gt; 0), otherwise
    /// the caller's synthesised <paramref name="fallbackPu"/>. Keeps a model that carries no machine reactance
    /// bit-for-bit on the historical synthesised default.
    /// </summary>
    public double EffectiveSubtransientXdPu(double fallbackPu) => SubtransientXdPu > 0.0 ? SubtransientXdPu : fallbackPu;

    /// <summary>
    /// The negative-sequence reactance X2 (pu on <see cref="MBase"/>) for short-circuit studies: its own
    /// <see cref="NegativeSeqXPu"/> when supplied (&gt; 0), otherwise the machine's effective X''d
    /// (<see cref="EffectiveSubtransientXdPu"/>) - the round-rotor convention <c>X2 = X''d</c>. So a machine
    /// carrying no separate X2 makes the negative-sequence network equal to the positive-sequence one.
    /// </summary>
    public double EffectiveNegativeSeqXPu(double fallbackPu) =>
        NegativeSeqXPu > 0.0 ? NegativeSeqXPu : EffectiveSubtransientXdPu(fallbackPu);
}

/// <summary>The two cost-curve forms of a MATPOWER <c>mpc.gencost</c> row (its MODEL column).</summary>
public enum GeneratorCostModel
{
    /// <summary>MODEL 1: piecewise linear, given as NCOST (x, y) points: (MW, currency per hour).</summary>
    PiecewiseLinear = 1,
    /// <summary>MODEL 2: polynomial, given as NCOST coefficients, highest order first: c(n-1) ... c1 c0.</summary>
    Polynomial = 2,
}

/// <summary>
/// A generator's cost curve, exactly as a MATPOWER <c>mpc.gencost</c> row gives it (F24, October 2026). It is parsed,
/// kept on the <see cref="Generator"/> and written back out by the MATPOWER writer. Since F19 the costed DC-OPF reads the
/// active-power curve (<see cref="Generator.Cost"/>) when asked to (<c>dcopf --costs</c>, <c>acopf --costs</c>,
/// <c>DcOpfCosted</c>); without that option the DC-OPF and its loss loop use the fuel-type merit order as before. The
/// reactive-power curve is carried only. D-DOC-207 (October 2026): this summary used to say no solver reads it.
/// </summary>
/// <param name="Model">Piecewise linear or polynomial.</param>
/// <param name="StartupCost">STARTUP column (currency).</param>
/// <param name="ShutdownCost">SHUTDOWN column (currency).</param>
/// <param name="Coefficients">The cost data in file order: for a polynomial the NCOST coefficients, highest order
/// first; for a piecewise-linear curve the 2 x NCOST numbers x1, y1, x2, y2, ...</param>
public sealed record GeneratorCost(GeneratorCostModel Model, double StartupCost, double ShutdownCost, IReadOnlyList<double> Coefficients)
{
    /// <summary>NCOST: the number of polynomial coefficients, or of piecewise-linear points.</summary>
    public int PointCount => Model == GeneratorCostModel.Polynomial ? Coefficients.Count : Coefficients.Count / 2;
}

/// <summary>
/// Classifies generator <see cref="Generator.Type"/> strings as converter-interfaced plant. Covers the type
/// vocabularies used by the GDA builder (<c>wind-offshore</c>, <c>storage</c>...), the JSON cases
/// (<c>wind_offshore</c>, <c>battery</c>...) and the synthetic GB. Pumped storage is a synchronous machine
/// and is deliberately <b>not</b> a converter type.
/// </summary>
public static class ConverterPlant
{
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "wind", "onshore", "offshore", "wind-onshore", "wind-offshore",
        "solar", "pv",
        "storage", "battery", "bess",
        "interconnector", "hvdc",
        "tidal",
    };

    /// <summary>True when <paramref name="type"/> names converter-interfaced plant (case-insensitive,
    /// <c>_</c> and <c>-</c> treated alike).</summary>
    public static bool IsConverterType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        string t = type.Trim().ToLowerInvariant().Replace('_', '-');
        return Types.Contains(t);
    }
}

/// <summary>
/// A transformer winding's connection, for building the <b>zero-sequence</b> network of a short-circuit
/// study. Zero-sequence current can flow through a winding only if it has a path for the zero-sequence
/// ampere-turns: a <see cref="Delta"/> winding blocks the external zero-sequence current (it circulates
/// internally, providing the return for a grounded wye on the other side); a <see cref="WyeGrounded"/>
/// winding passes it (through its earthed neutral); an <see cref="WyeUngrounded"/> winding blocks it (no
/// neutral path). See <c>Iec60909.ZeroSequenceThevenin</c> for how the pair of windings maps to the terminal
/// equivalent.
/// </summary>
public enum WindingConnection
{
    /// <summary>Delta: blocks external zero-sequence current; acts as the return path for a grounded wye opposite.</summary>
    Delta,
    /// <summary>Grounded (earthed-neutral) wye: passes zero-sequence current, optionally through a neutral impedance.</summary>
    WyeGrounded,
    /// <summary>Ungrounded wye: blocks zero-sequence current (no neutral return).</summary>
    WyeUngrounded,
}

/// <summary>
/// A transmission line or transformer between two buses, as a standard pi-model:
/// series impedance <see cref="R"/>+j<see cref="X"/> (p.u.) and total line charging
/// susceptance <see cref="B"/> (p.u., split half to each end). <see cref="TapRatio"/>
/// and <see cref="PhaseShiftDeg"/> model an off-nominal transformer (1.0 / 0 = a plain line).
/// </summary>
public sealed record Branch(
    int From,
    int To,
    double R,
    double X,
    double B = 0,
    double TapRatio = 1.0,
    double PhaseShiftDeg = 0.0,
    double RateAMva = 0.0,
    string? Name = null,
    bool IsTransformer = false,
    string? DynamicsClass = null,    // optional authored passive-plant dynamics class bound to this branch
    bool InService = true,           // false = out of service (opened by a recloser/switch); skipped by the solver
    double LengthKm = 0,             // circuit length (km), when GDA hands it over; 0 = unknown
    string? Equipment = null,        // "ohl" | "cable" | null - conductor type, for frequency-dependent (skin) modelling
    double R0 = 0,                   // zero-sequence series resistance (pu); 0 = not supplied
    double X0 = 0,                   // zero-sequence series reactance (pu); 0 = not supplied
    WindingConnection? FromWinding = null,  // transformer From-side winding for the zero-sequence network; null = a line
    WindingConnection? ToWinding = null,    // transformer To-side winding; null = a line
    double FromNeutralXPu = 0,       // grounding reactance in the From-side neutral (pu); adds 3*Xn in the zero-seq path
    double ToNeutralXPu = 0,         // grounding reactance in the To-side neutral (pu)
    double RateBMva = 0.0,           // F20: short-term rating (MATPOWER RATE_B); 0 = not given. Read only by a post-fault check
    double RateCMva = 0.0);          // F20: emergency rating (MATPOWER RATE_C); 0 = not given. Read only by a post-fault check

/// <summary>
/// An on-load tap changer: it nudges a transformer's tap up or down to hold a
/// controlled bus near <see cref="TargetVpu"/>, the way a Grid Supply Point keeps
/// the distribution-side voltage steady as demand swings. Referenced by the
/// transformer's <see cref="Branch.Name"/>.
/// </summary>
public sealed record Oltc(
    string Transformer,
    int ControlsBus,
    double TargetVpu = 1.0,
    double MinTap = 0.9,
    double MaxTap = 1.1,
    double StepPu = Oltc.DefaultStepPu,
    double DeadbandPu = 0.01)
{
    /// <summary>Default tap step (pu ratio per tap position): 1.25 %, a typical GB grid-transformer OLTC. Shared
    /// by this record's default and the tap optimiser's snap-to-grid fallback (R156).</summary>
    public const double DefaultStepPu = 0.0125;

    /// <summary>The transformer's impedance correction table (a PSS/E TAB), or null (the usual case: the impedance does
    /// not depend on the tap). When set, each tap the tap loop steps to also sets the branch's R and X from the table
    /// (<see cref="TapImpedanceCorrection.At"/>); with no table the loop changes only the tap, exactly as before.</summary>
    public TapImpedanceCorrection? ImpedanceCorrection { get; init; }
}

/// <summary>A machine's zero-sequence impedance R0 + j X0 (pu on its MBase).</summary>
public readonly record struct MachineZeroSequence(double RPu, double XPu);

/// <summary>
/// Zero-sequence mutual coupling between two lines over their whole length (PSS/E sequence data): the model branches
/// <paramref name="BranchA"/> and <paramref name="BranchB"/> (indices into <see cref="GridModel.Branches"/>, with their end
/// buses as a check) and the mutual impedance R + jX (pu on the system base), signed for each branch's From-to-To
/// direction. The zero-sequence network stamps each group of mutually coupled lines from the inverse of its impedance
/// matrix.
/// </summary>
public sealed record ZeroSequenceMutual(int BranchA, int FromA, int ToA, int BranchB, int FromB, int ToB, double RPu, double XPu);

/// <summary>What a transformer flow control regulates (PSS/E COD 2 and 3).</summary>
public enum TransformerFlowKind
{
    /// <summary>COD 2: the reactive power flowing into the transformer at its From (winding 1) end, by tap ratio.</summary>
    ReactivePower = 2,
    /// <summary>COD 3: the active power flowing into the transformer at its From (winding 1) end, by phase shift angle.</summary>
    ActivePower = 3,
}

/// <summary>
/// A transformer that regulates the power flowing through it (PSS/E automatic control COD 2 and 3): the transformer
/// (branch name and From bus), what it regulates, the band FlowMin..FlowMax (Mvar for reactive power, MW for active
/// power, flowing into the transformer at its From end), and the range and step of what it moves: the tap ratio (pu of
/// the branch tap) for reactive power, the phase shift angle (degrees) for active power. The steady-state solve's tap
/// loop steps it one position per pass until the flow is in its band. Read with PSS/E tap control.
/// </summary>
public sealed record TransformerFlowControl(string Transformer, int FromBus, TransformerFlowKind Kind, double FlowMin, double FlowMax,
    double Min, double Max, double Step)
{
    /// <summary>The transformer's impedance correction table, on its ratio (reactive power) or its angle (active power),
    /// or null.</summary>
    public TapImpedanceCorrection? ImpedanceCorrection { get; init; }
}

/// <summary>How a two-terminal LCC link is ordered (PSS/E MDC): blocked, a power order or a current order.</summary>
public enum LccMode
{
    /// <summary>Blocked (MDC 0): no current.</summary>
    Blocked,
    /// <summary>A power order (MDC 1): |Setpoint| MW at the rectifier (Setpoint &gt; 0) or the inverter (Setpoint &lt; 0).</summary>
    Power,
    /// <summary>A current order (MDC 2): Setpoint A.</summary>
    Current,
}

/// <summary>One converter of a two-terminal LCC link: its AC bus, bridges in series, angle limits (firing angle alpha
/// for the rectifier, extinction angle gamma for the inverter, degrees), commutating resistance and reactance per bridge
/// (ohms), the converter transformer's AC base voltage (kV), ratio and tap with its limits and step.</summary>
public sealed record LccConverter(int Bus, int Bridges, double AngleMaxDeg, double AngleMinDeg, double RcOhms, double XcOhms,
    double BaseKv, double TransformerRatio, double Tap, double TapMax, double TapMin, double TapStep);

/// <summary>A two-terminal line-commutated HVDC link (PSS/E two-terminal DC record): its order, the DC line resistance
/// (ohms), the scheduled compounded DC voltage (kV) the inverter holds, the voltage below which a power order becomes a
/// current order (kV), the compounding resistance (ohms), the current margin (pu) and the two converters. Solved by
/// <see cref="PowerFlow.LccDcLinks"/>.</summary>
public sealed record LccDcLink(string Name, LccMode Mode, double ResistanceOhms, double Setpoint, double ScheduledDcKv,
    double ModeSwitchDcKv, double CompoundingOhms, double MarginPu, LccConverter Rectifier, LccConverter Inverter);

/// <summary>A converter of a multi-terminal DC link (PSS/E multi-terminal DC converter line): AC bus, bridges, angle limits
/// (degrees), commutating resistance and reactance per bridge (ohms), transformer base kV, ratio, tap with limits and step,
/// its set-point (kV for the voltage converter; otherwise MW or A, positive for a rectifier) and the DC bus it is on.</summary>
public sealed record MtdcConverter(int Bus, int Bridges, double AngleMaxDeg, double AngleMinDeg, double RcOhms, double XcOhms,
    double BaseKv, double TransformerRatio, double Tap, double TapMax, double TapMin, double TapStep, double Setpoint, int DcBus);

/// <summary>A DC bus of a multi-terminal DC link: its number, the AC bus of the converter on it (0 when none), its
/// grounding resistance (ohms; 0 = not grounded) and name.</summary>
public sealed record MtdcDcBus(int Number, int AcBus, double GroundOhms, string? Name);

/// <summary>A DC line of a multi-terminal DC link between two of its DC buses, with its resistance (ohms).</summary>
public sealed record MtdcDcLine(int FromDcBus, int ToDcBus, double ResistanceOhms);

/// <summary>A multi-terminal line-commutated DC link (PSS/E multi-terminal DC record): its order kind (power or current),
/// the AC bus of the converter that holds the DC voltage, its converters, DC buses and DC lines. Solved by
/// <see cref="PowerFlow.MtdcLinks"/>.</summary>
public sealed record MtdcLink(string Name, LccMode Mode, int VoltageConverterBus, IReadOnlyList<MtdcConverter> Converters,
    IReadOnlyList<MtdcDcBus> DcBuses, IReadOnlyList<MtdcDcLine> Lines);

/// <summary>What a VSC converter holds on its DC side (PSS/E TYPE).</summary>
public enum VscDcControl
{
    /// <summary>TYPE 1: the DC voltage, DCSET kV.</summary>
    DcVoltage = 1,
    /// <summary>TYPE 2: an active power order, DCSET MW drawn from its AC bus (negative: sent to the bus).</summary>
    Power = 2,
}

/// <summary>What a VSC converter holds on its AC side (PSS/E MODE).</summary>
public enum VscAcControl
{
    /// <summary>MODE 1: its AC bus voltage, ACSET pu, within MINQ..MAXQ.</summary>
    Voltage = 1,
    /// <summary>MODE 2: a fixed power factor, ACSET.</summary>
    PowerFactor = 2,
}

/// <summary>One converter of a VSC link (PSS/E VSC DC converter line): AC bus, controls and set-points, losses
/// max(MINLOSS, ALOSS + BLOSS x Idc) (kW, Idc in A), MVA and AC current ratings (0 = none), the power weighting fraction
/// and the reactive limits (Mvar).</summary>
public sealed record VscConverter(int Bus, VscDcControl DcControl, VscAcControl AcControl, double DcSetpoint, double AcSetpoint,
    double LossAKw, double LossBKwPerA, double MinLossKw, double SmaxMva, double ImaxA, double PowerWeight, double QmaxMvar, double QminMvar);

/// <summary>A two-terminal VSC HVDC link (PSS/E VSC DC record): one converter holding the DC voltage, one an active power
/// order, joined by a DC line of <paramref name="ResistanceOhms"/>. Solved by <see cref="PowerFlow.VscDcLinks"/>.</summary>
public sealed record VscDcLink(string Name, double ResistanceOhms, VscConverter First, VscConverter Second);

/// <summary>How a FACTS device operates (from PSS/E's FACTS MODE and its terminal end bus).</summary>
public enum FactsMode
{
    /// <summary>Shunt element only (a STATCOM): no terminal end bus, or PSS/E MODE 2 with the series element bypassed.</summary>
    Statcom,
    /// <summary>Series and shunt elements (a UPFC, PSS/E MODE 1 with a terminal end bus): the series element delivers
    /// PDES + j QDES to the terminal end bus, the shunt element holds the sending end bus at VSET and supplies the series
    /// element's active power.</summary>
    Upfc,
    /// <summary>The series element at a constant impedance SET1 + j SET2 (a TCSC, PSS/E MODE 3), with the shunt element
    /// (when SHMX is above 0) holding the sending end bus.</summary>
    SeriesImpedance,
}

/// <summary>
/// A FACTS device (PSS/E FACTS device record, modes 1 to 3): its name and mode, sending end bus I and terminal end bus J
/// (0 when none), the series element's set-points (PDES MW and QDES Mvar arriving at J), the shunt element's voltage
/// set-point VSET (pu) and current rating SHMX (MVA at unity voltage; 0 = no shunt element), the bridge active power
/// rating TRMX (MW), the terminal end voltage band VTMN..VTMX (pu), the series voltage rating VSMX (pu), the series
/// current rating IMX (MVA at unity voltage; 0 = none). A bypassed series element (<see cref="FactsMode.Statcom"/> with
/// a terminal end bus) and a constant series impedance (<see cref="FactsMode.SeriesImpedance"/>) are ordinary branches of
/// the model between I and J (the PSS/E reader adds them); this record then describes the shunt element only.
/// Solved with the AC power flow by <see cref="PowerFlow.FactsDevices"/>.
/// </summary>
public sealed record FactsDevice(string Name, FactsMode Mode, int SendingBus, int TerminalBus, double PdesMw, double QdesMvar,
    double VsetPu, double ShuntMaxMva, double BridgeMaxMw, double TerminalVminPu, double TerminalVmaxPu, double SeriesVmaxPu,
    double SeriesImaxMva)
{
    /// <summary>PSS/E RMPCT: the percentage of the reactive power needed to hold the sending end bus that this shunt element
    /// supplies when other FACTS shunt elements or static var compensators hold the same bus (100 by default). The share
    /// is RMPCT over the sum of the RMPCTs of the devices holding the bus at that moment; a device alone supplies all of
    /// it, whatever its RMPCT.</summary>
    public double RmpctPercent { get; init; } = 100;
}

/// <summary>
/// A static var compensator (a PSS/E switched shunt with continuous voltage control, MODSW 2): its bus, the susceptance
/// range (Mvar at 1 pu: every reactor block in, negative, to every capacitor block in), the initial susceptance BINIT
/// (already in the bus shunt), the voltage band VSWLO..VSWHI (pu; equal for a fixed set-point) and a name. Solved with
/// the AC power flow by <see cref="PowerFlow.FactsDevices"/>.
/// </summary>
public sealed record StaticVarCompensator(int Bus, double BminMvar, double BmaxMvar, double BinitMvar, double VloPu, double VhiPu, string Name)
{
    /// <summary>PSS/E RMPCT: the percentage of the reactive power needed to hold the bus that this compensator supplies
    /// when other compensators or FACTS shunt elements hold the same bus (100 by default); see
    /// <see cref="FactsDevice.RmpctPercent"/>.</summary>
    public double RmpctPercent { get; init; } = 100;
}

/// <summary>What an induction machine's schedule PSET is (PSS/E PCODE).</summary>
public enum InductionPowerCode
{
    /// <summary>PCODE 1: mechanical (shaft) power out, MW (negative for a generator: shaft power in).</summary>
    Mechanical = 1,
    /// <summary>PCODE 2: electrical active power drawn from the bus, MW (negative for a generator).</summary>
    Electrical = 2,
}

/// <summary>
/// An induction machine (PSS/E induction machine record, custom design with a single rotor cage): its bus and name, what
/// its schedule is and the schedule (MW, positive for a motor), the machine base (MVA) and rated voltage (kV; 0 = the bus
/// base) its impedances are on, and the equivalent circuit (pu): stator RA + j XA, magnetising j XM, rotor R1 + j X1.
/// Solved with the AC power flow by <see cref="PowerFlow.InductionMachines"/>.
/// </summary>
public sealed record InductionMachine(int Bus, string Name, InductionPowerCode PowerCode, double ScheduledMw, double MBase, double RatedKv,
    double RaPu, double XaPu, double XmPu, double R1Pu, double X1Pu);

/// <summary>One point of an impedance correction table: at ratio (or phase shift angle) <paramref name="T"/>, the
/// transformer's nominal series impedance is multiplied by the complex factor <paramref name="ReF"/> + j <paramref name="ImF"/>.</summary>
public readonly record struct ImpedanceCorrectionPoint(double T, double ReF, double ImF);

/// <summary>
/// A transformer's series impedance as a function of its tap (PSS/E impedance correction tables, TAB): R + jX = (BaseR +
/// j BaseX) x F(tap x <see cref="RatioScale"/>), F interpolated in <see cref="Points"/>. <see cref="RatioScale"/> turns the
/// branch tap into the table's variable (a two-winding unit's branch tap is w1 / w2 and its table runs on w1, so the scale
/// is w2; 1 for a three-winding unit's winding branch).
/// </summary>
public sealed record TapImpedanceCorrection(double BaseR, double BaseX, double RatioScale, IReadOnlyList<ImpedanceCorrectionPoint> Points)
{
    /// <summary>The branch's R and X at branch tap <paramref name="tap"/>.</summary>
    public (double R, double X) At(double tap)
    {
        (double fr, double fi) = Factor(Points, tap * RatioScale);
        return (BaseR * fr - BaseX * fi, BaseR * fi + BaseX * fr);
    }

    /// <summary>The correction factor at <paramref name="t"/>: linear interpolation between the two points around it
    /// (the points' T strictly increasing), the first or last point's factor outside the table (PSS/E's rule).</summary>
    public static (double Re, double Im) Factor(IReadOnlyList<ImpedanceCorrectionPoint> points, double t)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) throw new ArgumentException("An impedance correction table needs at least one point.", nameof(points));
        if (t <= points[0].T) return (points[0].ReF, points[0].ImF);
        int last = points.Count - 1;
        if (t >= points[last].T) return (points[last].ReF, points[last].ImF);
        int k = 0;
        while (points[k + 1].T < t) k++;
        ImpedanceCorrectionPoint a = points[k], b = points[k + 1];
        if (t == b.T) return (b.ReF, b.ImF);
        double u = (t - a.T) / (b.T - a.T);
        return (a.ReF + u * (b.ReF - a.ReF), a.ImF + u * (b.ImF - a.ImF));
    }
}

/// <summary>One branch's contribution to a boundary cut, with the sign that counts as "positive" transfer.</summary>
public sealed record BoundaryBranch(string Branch, int Direction = 1);

/// <summary>
/// A transmission boundary (a cut across the network, like NESO's ETYS B6
/// Scotland-England) with a maximum permitted power transfer. The flow across it is
/// the signed sum of the named branches' flows; comparing that to <see cref="LimitMw"/>
/// is exactly the constraint NESO manages to keep the system secure.
/// </summary>
public sealed record Boundary(string Name, double LimitMw, IReadOnlyList<BoundaryBranch> Branches)
{
    /// <summary>
    /// N097 / N138 (October 2026): the rule every boundary branch name must meet: it names exactly one branch of the
    /// model (ordinal comparison, as the readers look names up). A name that matches no branch used to be skipped
    /// without a word (its flow silently left out of the boundary), and a name several branches share took the last
    /// of them (the boundary readers) or the first (loss allocation). Returns one plain-English problem per bad name,
    /// in boundary order; empty when every name resolves. Out-of-service branches count (they carry no flow, but the
    /// name is real). Used by the JSON loader, <see cref="GridModel.Validate"/>, the bridge model reader and every
    /// boundary reader, so a model built in code meets the same rule as a JSON case.
    /// </summary>
    public static List<string> BranchNameProblems(IReadOnlyList<Boundary> boundaries, IReadOnlyList<Branch> branches)
    {
        var bad = new List<string>();
        if (boundaries.Count == 0) return bad;
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Branch br in branches)
            if (!string.IsNullOrEmpty(br.Name)) count[br.Name] = count.GetValueOrDefault(br.Name) + 1;
        foreach (Boundary b in boundaries)
            foreach (BoundaryBranch bb in b.Branches)
            {
                int n = bb.Branch is null ? 0 : count.GetValueOrDefault(bb.Branch);
                if (n == 0) bad.Add($"boundary '{b.Name}' names branch '{bb.Branch}', which matches no branch of the case");
                else if (n > 1) bad.Add($"boundary '{b.Name}' names branch '{bb.Branch}', which {n} branches share (a boundary branch must name exactly one)");
            }
        return bad;
    }

    /// <summary>N138: throw <see cref="InvalidDataException"/> listing (up to twenty) every boundary branch name of
    /// <paramref name="model"/> that does not resolve to exactly one branch (<see cref="BranchNameProblems"/>).</summary>
    public static void RequireResolvedBranchNames(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        List<string> bad = BranchNameProblems(model.Boundaries, model.Branches);
        if (bad.Count > 0)
            throw new InvalidDataException(
                $"Model '{model.Name}': {bad.Count} boundary branch name(s) do not resolve to exactly one branch: " +
                $"{string.Join("; ", bad.Take(20))}{(bad.Count > 20 ? "; ..." : "")}.");
    }
}

/// <summary>
/// A complete grid: a system base (MVA) plus its buses, generators and branches.
/// All per-unit conversions in the solver are taken against <see cref="BaseMva"/>.
/// </summary>
public sealed class GridModel
{
    /// <summary>The conventional 100 MVA system base, used ONLY where a source carries no (or a non-positive)
    /// base: the live-bridge model block and the JSON system file (R177, R179). Every GridSim case states its
    /// own base; this is the fallback, named once.</summary>
    public const double DefaultBaseMva = 100.0;

    public required double BaseMva { get; init; }
    public required IReadOnlyList<Bus> Buses { get; init; }

    // ---- The generator fleet (F16, October 2026) -------------------------------------------------------------
    //
    // A generator can now be OUT OF SERVICE (Generator.InService false: a MATPOWER status-0 unit, or a JSON unit
    // written with "inService": false). Such a unit is real data and is KEPT in the model, but no engine may solve
    // it. Rather than teach every one of the ~260 places that read Generators to skip it (and risk one being
    // missed), the split is made once, here:
    //   * Generators             = the IN-SERVICE fleet, in the order given. Every engine, analysis and report
    //                              reads this, exactly as before, so a model with no out-of-service unit behaves
    //                              bit for bit as it always did (the list given is even returned by reference).
    //   * AllGenerators          = every unit, in service or not, in the order given (what a writer must save).
    //   * OutOfServiceGenerators = the units marked out of service, in the order given.
    // Setting Generators therefore accepts the WHOLE fleet; any unit marked out of service is held back from the
    // in-service list automatically. Neither new list is settable, so GridModelCopyTests' list of settable
    // properties is unchanged; CopyWith carries the full fleet (see there).

    private IReadOnlyList<Generator> _allGenerators = Array.Empty<Generator>();
    private IReadOnlyList<Generator> _inServiceGenerators = Array.Empty<Generator>();

    /// <summary>
    /// The IN-SERVICE generators, in the order given: what every engine solves. Setting this property takes the
    /// whole fleet; a unit with <see cref="Generator.InService"/> false is kept (see <see cref="AllGenerators"/>)
    /// but left out of this list. When every unit is in service, the list given is returned unchanged (the same
    /// reference), so existing models behave exactly as before (F16).
    /// </summary>
    public required IReadOnlyList<Generator> Generators
    {
        get => _inServiceGenerators;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _allGenerators = value;
            _inServiceGenerators = AnyOutOfService(value) ? value.Where(g => g.InService).ToArray() : value;
        }
    }

    /// <summary>Every generator, in service or not, in the order given (F16). A writer saves this list; no engine
    /// reads it.</summary>
    public IReadOnlyList<Generator> AllGenerators => _allGenerators;

    /// <summary>The generators marked out of service (<see cref="Generator.InService"/> false), in the order given;
    /// empty for a model whose whole fleet is in service (F16).</summary>
    public IReadOnlyList<Generator> OutOfServiceGenerators =>
        _allGenerators.Count == _inServiceGenerators.Count
            ? Array.Empty<Generator>()
            : _allGenerators.Where(g => !g.InService).ToArray();

    /// <summary>True when any generator in <paramref name="fleet"/> is marked out of service.</summary>
    private static bool AnyOutOfService(IReadOnlyList<Generator> fleet)
    {
        for (int i = 0; i < fleet.Count; i++) if (!fleet[i].InService) return true;
        return false;
    }

    public required IReadOnlyList<Branch> Branches { get; init; }

    public IReadOnlyList<Oltc> Oltcs { get; init; } = Array.Empty<Oltc>();
    public IReadOnlyList<Boundary> Boundaries { get; init; } = Array.Empty<Boundary>();

    /// <summary>Two-terminal line-commutated HVDC links (PSS/E two-terminal DC records), solved with the AC power flow
    /// by <see cref="PowerFlow.LccDcLinks"/>. Empty by default: a model with none solves exactly as before.</summary>
    public IReadOnlyList<LccDcLink> LccLinks { get; init; } = Array.Empty<LccDcLink>();

    /// <summary>Two-terminal VSC HVDC links (PSS/E VSC DC records), solved with the AC power flow by
    /// <see cref="PowerFlow.VscDcLinks"/>. Empty by default: a model with none solves exactly as before.</summary>
    public IReadOnlyList<VscDcLink> VscLinks { get; init; } = Array.Empty<VscDcLink>();

    /// <summary>Multi-terminal line-commutated DC links (PSS/E multi-terminal DC records), solved with the AC power flow by
    /// <see cref="PowerFlow.MtdcLinks"/>. Empty by default: a model with none solves exactly as before.</summary>
    public IReadOnlyList<MtdcLink> MtdcLinks { get; init; } = Array.Empty<MtdcLink>();

    /// <summary>FACTS devices (PSS/E FACTS device records, modes 1 to 3), solved with the AC power flow by
    /// <see cref="PowerFlow.FactsDevices"/>. Empty by default: a model with none solves exactly as before.</summary>
    public IReadOnlyList<FactsDevice> FactsDevices { get; init; } = Array.Empty<FactsDevice>();

    /// <summary>Static var compensators (PSS/E switched shunts with continuous voltage control, read with shunt control),
    /// solved with the AC power flow by <see cref="PowerFlow.FactsDevices"/>. Empty by default: a model with none solves
    /// exactly as before.</summary>
    public IReadOnlyList<StaticVarCompensator> StaticVarCompensators { get; init; } = Array.Empty<StaticVarCompensator>();

    /// <summary>Induction machines (PSS/E induction machine records), solved with the AC power flow by
    /// <see cref="PowerFlow.InductionMachines"/>. Empty by default: a model with none solves exactly as before.</summary>
    public IReadOnlyList<InductionMachine> InductionMachines { get; init; } = Array.Empty<InductionMachine>();

    /// <summary>Transformers regulating the reactive or active power through them (PSS/E COD 2 and 3), stepped by the
    /// steady-state solve's tap loop (<see cref="PowerFlow.SteadyStateSolver"/>) with the tap changers. Empty by default:
    /// a model with none solves exactly as before.</summary>
    public IReadOnlyList<TransformerFlowControl> TransformerFlowControls { get; init; } = Array.Empty<TransformerFlowControl>();

    /// <summary>Zero-sequence mutual couplings between lines (PSS/E sequence data), used by the fault studies'
    /// zero-sequence network. Empty by default: a model with none builds exactly the network it always did.</summary>
    public IReadOnlyList<ZeroSequenceMutual> ZeroSequenceMutuals { get; init; } = Array.Empty<ZeroSequenceMutual>();

    /// <summary>True when the model holds a device the Newton engines solve in an outer loop with the AC solve
    /// (<see cref="PowerFlow.HvdcCoupling"/>): an HVDC link, a FACTS device, a static var compensator or an induction
    /// machine. Such a model goes
    /// to Newton (never the backward/forward sweep), and the case writers refuse it.</summary>
    public bool HasCoupledDevices => LccLinks.Count > 0 || VscLinks.Count > 0 || MtdcLinks.Count > 0
        || FactsDevices.Count > 0 || StaticVarCompensators.Count > 0 || InductionMachines.Count > 0;
    /// <summary>
    /// The system's nominal frequency (Hz). Every dynamic study of this model takes it as f0: the swing equation's
    /// synchronous speed 2 * pi * f0 (classical and RMS stability, CCT), the small-signal state matrix, the EMT branch
    /// inductances L = X / (2 * pi * f0), the impedance scans and the IEC 60909 frequency terms. The default is the GB
    /// nominal (<see cref="Dynamics.FrequencyLimits.NominalHz"/>, 50 Hz; R196 / D2 named the literal, same value);
    /// a 60 Hz system must say so (the WSCC 9-bus and the Kundur-structured two-area case do, N017; a MATPOWER file
    /// carries no frequency, so its loader takes one, N187).
    /// </summary>
    public double FrequencyHz { get; init; } = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz;
    public string Name { get; init; } = "grid";

    /// <summary>
    /// Authored per-asset dynamics (class library + bindings), or null when the case ships no
    /// <c>asset-classes.json</c>. Null keeps every existing case and the reduction gate untouched.
    /// </summary>
    public Assets.AssetDynamicsModel? AssetDynamics { get; init; } = null;

    /// <summary>
    /// Authored protective devices (class library + placed instances), or null when the case ships no
    /// <c>protections.json</c>. Null keeps every existing case and the reduction gate untouched.
    /// </summary>
    public Assets.Protection.ProtectionModel? Protections { get; init; } = null;

    /// <summary>
    /// How many generator rows of the source file the loader could not keep (a MATPOWER <c>mpc.gen</c> row on an
    /// isolated or undefined bus has no bus to sit on and is skipped). Zero for every loader that keeps every row.
    /// When it is above zero, position k of <see cref="AllGenerators"/> is no longer row k of the file, so anything
    /// that addresses generators by file position (a cost file's <c>index</c>) must refuse rather than land on the
    /// wrong unit.
    /// </summary>
    public int DroppedGeneratorRows { get; init; }

    public int BusCount => Buses.Count;

    /// <summary>
    /// A copy of this model with the given parts replaced and EVERY other property carried across unchanged
    /// (R270). <see cref="GridModel"/> is a class with init-only properties, so each copy site used to re-list
    /// them by hand, and several quietly dropped <see cref="AssetDynamics"/> and <see cref="Protections"/> (and
    /// one also the OLTCs and boundaries) - the same class of defect as the earlier PassivePlantFleet and
    /// GraphLayout cases. Copying through this one method means a new property only has to be added here, and
    /// <c>GridModelCopyTests</c> fails if it is not.
    ///
    /// <para>Generators (F16). With <paramref name="generators"/> null the WHOLE fleet is carried, out-of-service
    /// units included, and the copy's <see cref="Generators"/> is the very same in-service list. A caller that
    /// passes <paramref name="generators"/> almost always built it from <see cref="Generators"/> (the in-service
    /// list, e.g. a redispatch), so when the list given holds no out-of-service unit, this model's out-of-service
    /// units are appended after it (they keep their data; the copy's <see cref="Generators"/> is exactly the list
    /// given). When the list given itself marks any unit out of service, it is taken as the complete fleet.</para>
    ///
    /// <para><paramref name="frequencyHz"/> (D2 nominal frequency, October 2026): the copy's nominal frequency; null keeps
    /// this model's. Used to declare the frequency of a model whose source carries none (a MATPOWER file, N187) and by
    /// the built-in 60 Hz cases (N017). It is not validated here; <see cref="Validate"/> refuses a frequency that is not
    /// finite and above 0 Hz, exactly as for any other model.</para>
    ///
    /// <para><paramref name="assetDynamics"/> (N314, LF5, October 2026): the copy's asset-dynamics bundle; null keeps this
    /// model's. Added so <c>AssetEnvelope</c>, which swaps the class library per sweep corner, copies through this one
    /// method instead of hand-building a GridModel (a new property would have been dropped there silently).</para>
    ///
    /// <para><paramref name="droppedGeneratorRows"/>: the copy's <see cref="DroppedGeneratorRows"/>; null keeps this model's.
    /// <paramref name="generatorsAreWholeFleet"/>: when true, <paramref name="generators"/> is taken as the complete
    /// fleet exactly as given (out-of-service units included, in file order) and none of this model's units is
    /// re-appended. Added for the island pre-pass, which drops the units of a dead island (out-of-service ones too) and
    /// used to hand-build its copy, losing every coupled-device collection.</para>
    /// </summary>
    public GridModel CopyWith(
        IReadOnlyList<Bus>? buses = null,
        IReadOnlyList<Generator>? generators = null,
        IReadOnlyList<Branch>? branches = null,
        IReadOnlyList<Oltc>? oltcs = null,
        IReadOnlyList<Boundary>? boundaries = null,
        string? name = null,
        double? frequencyHz = null,
        Assets.AssetDynamicsModel? assetDynamics = null,
        IReadOnlyList<LccDcLink>? lccLinks = null,
        IReadOnlyList<VscDcLink>? vscLinks = null,
        IReadOnlyList<MtdcLink>? mtdcLinks = null,
        IReadOnlyList<FactsDevice>? factsDevices = null,
        IReadOnlyList<StaticVarCompensator>? staticVarCompensators = null,
        IReadOnlyList<InductionMachine>? inductionMachines = null,
        IReadOnlyList<TransformerFlowControl>? transformerFlowControls = null,
        IReadOnlyList<ZeroSequenceMutual>? zeroSequenceMutuals = null,
        int? droppedGeneratorRows = null,
        bool generatorsAreWholeFleet = false)
    {
        // Which full fleet the copy holds, and (where it is known) which in-service list it must return by reference.
        IReadOnlyList<Generator> fleet;
        IReadOnlyList<Generator>? inServiceSame = null;
        if (generators is not null && generatorsAreWholeFleet)
        {
            // The caller built the complete fleet itself (out-of-service units included, in file order), e.g. the
            // island pre-pass dropping the units of a dead island: take it exactly as given, never re-append ours.
            fleet = generators;
        }
        else if (generators is null)
        {
            fleet = _allGenerators;
            inServiceSame = _inServiceGenerators;
        }
        else if (_allGenerators.Count == _inServiceGenerators.Count || AnyOutOfService(generators))
        {
            fleet = generators;   // nothing of ours to carry, or the caller gave the complete fleet
        }
        else if (generators.Count == _inServiceGenerators.Count)
        {
            // The caller replaced the in-service list one for one (a redispatch: same units, new set-points). Put
            // each replacement back in the position its unit holds in the full fleet, so AllGenerators keeps the
            // file order, out-of-service units included. Appending the out-of-service units at the end (as before)
            // reordered the fleet: [G1, G2 off, G3] became [G1, G3, G2], and a cost file's file-order "index" 2
            // then landed on G3. The in-service list itself is exactly the list given, in its order, as before.
            var merged = new List<Generator>(_allGenerators.Count);
            int next = 0;
            foreach (Generator g in _allGenerators)
                merged.Add(g.InService ? generators[next++] : g);
            fleet = merged;
            inServiceSame = generators;
        }
        else
        {
            // A different number of in-service units: no one-to-one position to keep. The out-of-service units
            // follow the list given (their data is kept).
            var merged = new List<Generator>(generators.Count + _allGenerators.Count - _inServiceGenerators.Count);
            merged.AddRange(generators);
            merged.AddRange(OutOfServiceGenerators);
            fleet = merged;
            inServiceSame = generators;
        }

        var copy = new GridModel
        {
            BaseMva = BaseMva,
            Buses = buses ?? Buses,
            Generators = fleet,
            Branches = branches ?? Branches,
            Oltcs = oltcs ?? Oltcs,
            Boundaries = boundaries ?? Boundaries,
            FrequencyHz = frequencyHz ?? FrequencyHz,
            Name = name ?? Name,
            // N314 (LF5, October 2026): a caller may replace the asset-dynamics bundle (AssetEnvelope's per-corner class
            // library); null keeps this model's, exactly as before.
            AssetDynamics = assetDynamics ?? AssetDynamics,
            Protections = Protections,
            DroppedGeneratorRows = droppedGeneratorRows ?? DroppedGeneratorRows,
            LccLinks = lccLinks ?? LccLinks,
            VscLinks = vscLinks ?? VscLinks,
            MtdcLinks = mtdcLinks ?? MtdcLinks,
            FactsDevices = factsDevices ?? FactsDevices,
            StaticVarCompensators = staticVarCompensators ?? StaticVarCompensators,
            InductionMachines = inductionMachines ?? InductionMachines,
            TransformerFlowControls = transformerFlowControls ?? TransformerFlowControls,
            ZeroSequenceMutuals = zeroSequenceMutuals ?? ZeroSequenceMutuals,
        };
        // Keep the in-service list by reference where it is unchanged (the setter above would otherwise rebuild an
        // equal but new array whenever the fleet holds an out-of-service unit).
        if (inServiceSame is not null) copy._inServiceGenerators = inServiceSame;
        return copy;
    }

    /// <summary>Total system inertia,  HMBase, in GVAs  -  the headline number for frequency stability.</summary>
    /// <remarks>
    /// N289 (October 2026 review, stream B3; Ruling R1: Mark delegated EE calls 2026-10-04): only ROTATING mass counts.
    /// A unit whose type the shared technology map classifies explicitly as converter plant (storage / battery / bess,
    /// wind, solar, tidal, interconnector, hvdc, converter: <see cref="Dynamics.Distributed.PoleMap.ForType"/> with a known
    /// type and a non-synchronous kind) contributes nothing, whatever H its data carries: such an H is a converter's
    /// synthetic-inertia figure, and virtual inertia is never real mass (NESO's system inertia is the stored kinetic
    /// energy of synchronous machines; grid-forming virtual inertia is accounted separately, as H0StudyEngine and the
    /// asset dynamics do). This is the rule the distributed model already applies since R239, so its centre-of-inertia
    /// energy and this lumped total agree again. Unknown or blank types keep counting their H (the distributed model
    /// treats them as 2-pole machines). No bundled case has a converter type with H &gt; 0 (data/gb-full and
    /// data/gb-synthetic checked by type at R239), so no shipped number moves; for every model without such a unit the
    /// sum runs over exactly the same terms in the same order, bit for bit.
    /// <para>Bug hunt 2026-10-29, finding 75 (decided, documented): a unit with <c>MBase = 0</c> ("not given") has three
    /// readings in the code. This sum, RotatingInertiaMwS, DomainAssignment, DomainCoupling and PropagationDelay take H x
    /// MBase raw, so it adds no inertia; MachineBase.EffectiveMva (the RMS and classical machine models, ShareLostPower)
    /// reads it as the system base; LiveSim falls back to |Pg|. No shipped case reaches it: the JSON loader sets MBase to
    /// the unit's capacity, the Generator record defaults to 100, and the MATPOWER parser replaces an mBase of 0 by the
    /// system base at load. Only a hand-built or bridge-built model with MBase = 0 sees different totals in the frequency
    /// and the RMS studies. One shared helper would end the divergence but moves numbers on such models in every one of
    /// those engines, so it is left for a deliberate change; give MBase on hand-built models.</para>
    /// </remarks>
    public double SystemInertiaGvaS => SystemInertiaMwS / 1000.0;

    /// <summary>
    /// The same N289 total as <see cref="SystemInertiaGvaS"/> in MWs (sum of H x MBase over the units that count as
    /// rotating mass, in fleet order), before the division by 1000. N314 (LF5, October 2026): <c>gridsim trip</c> works in
    /// MWs and used to sum H x MBase over EVERY unit; it now reads this total, so the trip, the lumped total and the
    /// distributed model agree on what inertia is. Kept as its own property (not SystemInertiaGvaS * 1000) because
    /// (x / 1000) * 1000 need not give x back bit for bit; SystemInertiaGvaS is this sum divided by 1000, exactly the
    /// expression it always was.
    /// </summary>
    public double SystemInertiaMwS => Generators.Where(CountsAsRotatingMass).Sum(g => g.H * g.MBase);

    /// <summary>
    /// N314: the inertia (MWs) one unit contributes to <see cref="SystemInertiaMwS"/>: H x MBase for rotating mass, exactly
    /// 0 for a known converter technology (N289). Used by <c>trip</c> to take the tripped unit's own share out of the total.
    /// </summary>
    public static double RotatingInertiaMwS(Generator g) => CountsAsRotatingMass(g) ? g.H * g.MBase : 0.0;

    /// <summary>N289: true unless the unit's type is a KNOWN converter technology (see <see cref="SystemInertiaGvaS"/>).</summary>
    internal static bool CountsAsRotatingMass(Generator g)
    {
        Dynamics.Distributed.PoleMap.MachineSpeed sp = Dynamics.Distributed.PoleMap.ForType(g.Type);
        return !(sp.IsKnownType && sp.Kind != Dynamics.Distributed.DomainKind.Synchronous);
    }

    /// <summary>
    /// Structurally validate the model, returning a list of human-readable diagnostics
    /// (never throws). <see cref="ValidationSeverity.Error"/> diagnostics describe faults
    /// that would break the solver or make its answer meaningless: duplicate bus ids, no reference (slack) bus, an
    /// island with no reference bus of its own, a branch / generator that points at a bus which does not exist, a
    /// branch from a bus to itself, a branch with zero series impedance, a non-finite number, or a system base or
    /// nominal frequency that is not a finite positive number (D-DOC-082 / D-DOC-083, October 2026). Everything else
    /// is a <see cref="ValidationSeverity.Warning"/>: informational (multiple slacks = multi-island) or an OLTC whose
    /// record does not fit its transformer (see step 5 for what the tap-changer loop then does). Entry-point loaders
    /// decide what to do with these: hard fail on errors (the JSON, MATPOWER and PyPSA loaders), or (for a data-merge
    /// pipeline like the GDA builder) collect them as notes and carry on.
    /// </summary>
    public IReadOnlyList<GridDiagnostic> Validate()
    {
        var diags = new List<GridDiagnostic>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // 0. The system base and the nominal frequency (D-DOC-083, October 2026). Every per-unit conversion divides by
        //    BaseMva, so a zero, negative or non-finite base turns every number of the solve into Infinity or NaN (or
        //    flips every sign); every dynamic study scales by FrequencyHz in the same way. Neither used to be checked:
        //    a JSON system.json with "baseMva": 0 loaded and solved to nonsense. Both are refused by name. (The bridge
        //    reader already refuses a bad frequencyHz and replaces a non-positive base with the 100 MVA default.)
        if (!(double.IsFinite(BaseMva) && BaseMva > 0))
            diags.Add(new GridDiagnostic(ValidationSeverity.Error,
                $"System base BaseMva {BaseMva.ToString("R", inv)} is not a finite power above 0 MVA; every per-unit value divides by it."));
        if (!(double.IsFinite(FrequencyHz) && FrequencyHz > 0))
            diags.Add(new GridDiagnostic(ValidationSeverity.Error,
                $"Nominal frequency FrequencyHz {FrequencyHz.ToString("R", inv)} is not a finite frequency above 0 Hz."));

        // 1. Unique bus ids. Duplicates silently overwrite the solver's index map, so a
        //    later element resolves to the wrong physical bus. Build the id set / kV lookup
        //    from the first occurrence of each id.
        var busIds = new HashSet<int>();
        var busKv = new Dictionary<int, double>();
        var dupReported = new HashSet<int>();
        foreach (Bus b in Buses)
        {
            if (busIds.Add(b.Id)) busKv[b.Id] = b.BaseKv;
            else if (dupReported.Add(b.Id))
                diags.Add(new GridDiagnostic(ValidationSeverity.Error, $"Duplicate bus id {b.Id}."));
        }

        // 2. At least one slack. Zero => the power flow is singular. More than one is not an
        //    error: it's how a multi-island system carries one reference per island.
        int slackCount = Buses.Count(b => b.Type == BusType.Slack);
        if (slackCount == 0)
            diags.Add(new GridDiagnostic(ValidationSeverity.Error,
                "No slack (reference) bus; the power flow would be singular."));
        else if (slackCount > 1)
            diags.Add(new GridDiagnostic(ValidationSeverity.Warning,
                $"{slackCount} slack (reference) buses  -  treating as a multi-island system."));

        // 3. Every branch endpoint must resolve to a bus (else the solver throws KeyNotFound).
        for (int i = 0; i < Branches.Count; i++)
        {
            Branch br = Branches[i];
            string label = string.IsNullOrEmpty(br.Name) ? $"#{i} ({br.From}->{br.To})" : $"'{br.Name}'";
            if (!busIds.Contains(br.From))
                diags.Add(new GridDiagnostic(ValidationSeverity.Error,
                    $"Branch {label} From references nonexistent bus {br.From}."));
            if (!busIds.Contains(br.To))
                diags.Add(new GridDiagnostic(ValidationSeverity.Error,
                    $"Branch {label} To references nonexistent bus {br.To}."));

            // 3a. D-DOC-083 (October 2026): a branch from a bus to itself. Its series admittance is stamped on the
            //     diagonal twice with opposite signs and its charging adds a phantom shunt, so it is never a real
            //     element: a data error (a mistyped end bus), refused by name whatever its service state.
            if (br.From == br.To)
                diags.Add(new GridDiagnostic(ValidationSeverity.Error,
                    $"Branch {label} connects bus {br.From} to itself (a self-loop); a branch must join two different buses."));

            // 3b. D-DOC-083: zero series impedance (R = X = 0, either sign of zero). The series admittance is
            //     1 / (R + jX), so the stamp is non-finite and poisons the whole Y-bus (every voltage of the solve
            //     becomes NaN). Refused whatever the service state: switching such a branch on later would do the
            //     same. A bus-tie that is really zero impedance is modelled by merging its two buses, or by a small
            //     reactance (MATPOWER convention, 1e-4 pu). A non-finite R or X is reported by step 6 instead.
            if (br.R == 0.0 && br.X == 0.0)
                diags.Add(new GridDiagnostic(ValidationSeverity.Error,
                    $"Branch {label} ({br.From}->{br.To}) has zero series impedance (R = X = 0), so its admittance 1 / (R + jX) is not finite; give it a reactance or merge its two buses."));
        }

        // 3c. D-DOC-082 (October 2026): every island needs its own reference bus. An island is a set of buses joined by
        //     IN-SERVICE branches (the solver's own view); an island with no slack has no angle reference and no bus to
        //     balance it, so its power flow is singular (or, with no injection at all, "converges" at the flat start to
        //     voltages that mean nothing). A multi-island case with ONE slack used to load and solve with exit 0 and no
        //     word about the island left floating. Each such island is now refused by name: its size and its first
        //     (lowest-id) few buses. Checked only when the model has at least one slack and no duplicate bus ids (the
        //     errors above already cover those cases), and only on buses that exist. A contingency that opens a branch
        //     and splits the grid is a different thing: IslandPartition elects or blacks out the new island there.
        //     Every bundled case has one slack per island (checked when this rule was added), so none of them changes.
        if (slackCount > 0 && dupReported.Count == 0)
            foreach (string island in IslandsWithoutSlack())
                diags.Add(new GridDiagnostic(ValidationSeverity.Error, island));

        // 4. Every generator must sit on a real bus.
        for (int i = 0; i < Generators.Count; i++)
        {
            Generator g = Generators[i];
            if (!busIds.Contains(g.Bus))
            {
                string label = string.IsNullOrEmpty(g.Name) ? $"#{i}" : $"'{g.Name}'";
                diags.Add(new GridDiagnostic(ValidationSeverity.Error,
                    $"Generator {label} references nonexistent bus {g.Bus}."));
            }
        }

        // 4b. Every OUT-OF-SERVICE generator must also sit on a real bus (F16): it is kept in the model, and a
        //     writer or a later switch-on must not meet a dangling reference.
        foreach (Generator g in OutOfServiceGenerators)
        {
            if (!busIds.Contains(g.Bus))
            {
                string label = string.IsNullOrEmpty(g.Name) ? "(unnamed)" : $"'{g.Name}'";
                diags.Add(new GridDiagnostic(ValidationSeverity.Error,
                    $"Out-of-service generator {label} references nonexistent bus {g.Bus}."));
            }
        }

        // 4c. Per-bus voltage limits (F17), when given, must be finite and above 0 pu, and the lower not above the
        //     upper (equal limits are a fixed-voltage bus: the bundled MATPOWER feeders give their slack 1.0 / 1.0).
        foreach (Bus b in Buses)
        {
            if (b.VminPu is double lo && !(double.IsFinite(lo) && lo > 0))
                diags.Add(new GridDiagnostic(ValidationSeverity.Error, $"Bus {b.Id} VminPu {lo} is not a finite voltage above 0 pu."));
            if (b.VmaxPu is double hi && !(double.IsFinite(hi) && hi > 0))
                diags.Add(new GridDiagnostic(ValidationSeverity.Error, $"Bus {b.Id} VmaxPu {hi} is not a finite voltage above 0 pu."));
            if (b.VminPu is double l2 && b.VmaxPu is double h2 && double.IsFinite(l2) && double.IsFinite(h2) && !(l2 <= h2))
                diags.Add(new GridDiagnostic(ValidationSeverity.Error, $"Bus {b.Id} VminPu {l2} is above its VmaxPu {h2}."));
        }

        // 5. OLTCs. Warnings only, because the tap-changer loop (SteadyStateSolver) copes with each case without
        //    failing: a record naming no existing transformer is skipped (its tap never moves); a record whose
        //    ControlsBus is not in the model is skipped too; a record controlling the HV end, or a bus that is not an
        //    end of its transformer, is still REGULATED, with the step direction chosen for the end it controls (R013)
        //    and, for a remote bus, the first transformer of that name (N046). D-DOC-207 (October 2026): this comment
        //    used to say a bad OLTC "just fails to regulate", which has not been true since R013 / N046. The usual
        //    arrangement, which these warnings point at, is the way a GSP holds its distribution-side voltage: the
        //    ControlsBus resolves, names a real transformer and sits on its LV (lower-BaseKv) winding.
        // GR-505: each record is judged against the branch the OLTC loop will actually drive, by the loop's own rule
        // (SteadyStateSolver.MapOltcRecordsToBranches: the same-named branch touching the controlled bus, else the first
        // of that name). Validate used to take the first branch of the name, so with repeated transformer names the
        // HV-side and not-an-endpoint warnings could describe a different unit from the one the loop steps.
        int[] oltcBranch = PowerFlow.SteadyStateSolver.MapOltcRecordsToBranches(this);
        var oltcRecordOfBranch = new Dictionary<int, int>();
        for (int k = 0; k < Oltcs.Count; k++)
        {
            Oltc o = Oltcs[k];
            if (!busIds.Contains(o.ControlsBus))
                diags.Add(new GridDiagnostic(ValidationSeverity.Warning,
                    $"OLTC on transformer '{o.Transformer}' controls nonexistent bus {o.ControlsBus}."));

            Branch? tx = oltcBranch[k] >= 0 ? Branches[oltcBranch[k]] : null;
            // GR-503: two records driving one branch. The loop steps the branch once per pass, for the first record.
            if (oltcBranch[k] >= 0 && !oltcRecordOfBranch.TryAdd(oltcBranch[k], k))
                diags.Add(new GridDiagnostic(ValidationSeverity.Warning,
                    $"OLTC records {oltcRecordOfBranch[oltcBranch[k]]} and {k} (controlling buses {Oltcs[oltcRecordOfBranch[oltcBranch[k]]].ControlsBus} and {o.ControlsBus}) both drive transformer '{o.Transformer}' (branch {oltcBranch[k]}); it steps once per pass, for the first record."));
            if (tx is null)
            {
                diags.Add(new GridDiagnostic(ValidationSeverity.Warning,
                    $"OLTC controlling bus {o.ControlsBus} names transformer '{o.Transformer}', which does not exist."));
                continue;
            }

            double fromKv = busKv.GetValueOrDefault(tx.From);
            double toKv = busKv.GetValueOrDefault(tx.To);
            if (fromKv == toKv) continue;   // equal / unknown kV: can't tell the windings apart
            int lvBus = fromKv < toKv ? tx.From : tx.To;
            int hvBus = fromKv < toKv ? tx.To : tx.From;
            if (o.ControlsBus == hvBus)
                diags.Add(new GridDiagnostic(ValidationSeverity.Warning,
                    $"OLTC on transformer '{o.Transformer}' controls the HV-side bus {hvBus}; expected the LV (lower-kV) side bus {lvBus}."));
            else if (o.ControlsBus != lvBus)
                diags.Add(new GridDiagnostic(ValidationSeverity.Warning,
                    $"OLTC on transformer '{o.Transformer}' controls bus {o.ControlsBus}, which is not an endpoint of the transformer (LV side is bus {lvBus})."));
        }

        // 5b. Boundary branch names (N138): each must name exactly one branch, or the boundary's flow silently leaves a
        //     branch out (or picks one of several namesakes). An Error, as the JSON loader has refused it since N097.
        foreach (string problem in Boundary.BranchNameProblems(Boundaries, Branches))
            diags.Add(new GridDiagnostic(ValidationSeverity.Error, char.ToUpperInvariant(problem[0]) + problem[1..] + "."));

        // 6. Finite inputs. A NaN/Inf in any of these feeds straight into the Ybus / Jacobian and
        //    silently poisons the whole solve (every downstream voltage becomes NaN), so a bad number
        //    from a data merge must be caught here as an Error, not discovered as a non-finite result.
        //    Generator Qmin/Qmax/Pmin/Pmax are intentionally excluded: +/-Infinity is the "no limit" default.
        void RequireFinite(double value, string what)
        {
            if (!double.IsFinite(value))
                diags.Add(new GridDiagnostic(ValidationSeverity.Error, $"{what} is not a finite number ({value})."));
        }
        foreach (Bus b in Buses)
        {
            RequireFinite(b.Pd, $"Bus {b.Id} Pd");
            RequireFinite(b.Qd, $"Bus {b.Id} Qd");
            RequireFinite(b.Gs, $"Bus {b.Id} Gs");
            RequireFinite(b.Bs, $"Bus {b.Id} Bs");
            // The stored state is a start point (warm start, the sweep's source): a non-finite one poisons the first
            // Jacobian. A non-positive magnitude is legal data but not a usable start, so it is a warning.
            RequireFinite(b.Vm, $"Bus {b.Id} Vm");
            RequireFinite(b.VaDeg, $"Bus {b.Id} VaDeg");
            if (double.IsFinite(b.Vm) && b.Vm <= 0)
                diags.Add(new GridDiagnostic(ValidationSeverity.Warning,
                    $"Bus {b.Id} stored Vm {b.Vm} is not above 0 pu; solve it with a flat start (a warm start from it cannot be used)."));
        }
        for (int i = 0; i < Branches.Count; i++)
        {
            Branch br = Branches[i];
            string label = string.IsNullOrEmpty(br.Name) ? $"#{i} ({br.From}->{br.To})" : $"'{br.Name}'";
            RequireFinite(br.R, $"Branch {label} R");
            RequireFinite(br.X, $"Branch {label} X");
            RequireFinite(br.B, $"Branch {label} B");
            RequireFinite(br.TapRatio, $"Branch {label} TapRatio");
            RequireFinite(br.PhaseShiftDeg, $"Branch {label} PhaseShiftDeg");
        }
        for (int i = 0; i < Generators.Count; i++)
        {
            Generator g = Generators[i];
            string label = string.IsNullOrEmpty(g.Name) ? $"#{i}" : $"'{g.Name}'";
            RequireFinite(g.Pg, $"Generator {label} Pg");
            RequireFinite(g.Qg, $"Generator {label} Qg");
            RequireFinite(g.Vg, $"Generator {label} Vg");
            // A voltage set-point must be a positive magnitude: Vg <= 0 solved to a mirrored state (Newton) or was
            // replaced by 1.0 without a word (the sweep).
            if (double.IsFinite(g.Vg) && !(g.Vg > 0))
                diags.Add(new GridDiagnostic(ValidationSeverity.Error, $"Generator {label} Vg {g.Vg} is not a voltage set-point above 0 pu."));
            // The limits may be +/-Infinity ("no limit") but never NaN: a NaN limit makes every comparison false, so
            // reactive-limit enforcement and every audit silently skipped the unit.
            foreach ((double v, string what) in new[] { (g.Qmin, "Qmin"), (g.Qmax, "Qmax"), (g.Pmin, "Pmin"), (g.Pmax, "Pmax") })
                if (double.IsNaN(v))
                    diags.Add(new GridDiagnostic(ValidationSeverity.Error, $"Generator {label} {what} is NaN (use +/-Infinity for no limit)."));
            if (g.Qmin > g.Qmax)
                diags.Add(new GridDiagnostic(ValidationSeverity.Error, $"Generator {label} Qmin {g.Qmin} is above its Qmax {g.Qmax}."));
        }

        return diags;
    }

    /// <summary>
    /// D-DOC-082: one plain-English problem per island (a set of buses joined by in-service branches) that holds no
    /// slack bus, in order of each island's lowest bus id. Each names the island's size and up to its first five bus
    /// ids. Branch ends that do not resolve to a bus are ignored here (step 3 reports them). Empty when every island
    /// has a reference, which is always the case for a connected model with a slack.
    /// </summary>
    private List<string> IslandsWithoutSlack()
    {
        var problems = new List<string>();
        int n = Buses.Count;
        var indexOf = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) indexOf.TryAdd(Buses[i].Id, i);

        // Union-find over bus positions, joined by in-service branches (the same rule as IslandPartition).
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (Branch br in Branches)
            if (br.InService && indexOf.TryGetValue(br.From, out int a) && indexOf.TryGetValue(br.To, out int b))
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

        // Group the bus ids by island root and note which islands hold a slack.
        var members = new Dictionary<int, List<int>>();
        var hasSlack = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!members.TryGetValue(r, out List<int>? list)) members[r] = list = new List<int>();
            list.Add(Buses[i].Id);
            if (Buses[i].Type == BusType.Slack) hasSlack.Add(r);
        }
        if (members.Count == 1) return problems;   // one island, and the caller has checked there is a slack

        // Deterministic order: by each island's lowest bus id.
        foreach (List<int> island in members.Where(kv => !hasSlack.Contains(kv.Key)).Select(kv => kv.Value)
                     .OrderBy(ids => ids.Min()))
        {
            List<int> ids = island.OrderBy(x => x).ToList();
            string shown = string.Join(", ", ids.Take(5)) + (ids.Count > 5 ? ", ..." : "");
            problems.Add($"An island of {ids.Count} bus(es) (bus {shown}) has no slack (reference) bus: it is not connected " +
                "to any slack through in-service branches, so its power flow would be singular. Give it a slack bus (one per " +
                "island) or connect it.");
        }
        return problems;
    }
}

/// <summary>Severity of a <see cref="GridDiagnostic"/> from <see cref="GridModel.Validate"/>.</summary>
public enum ValidationSeverity
{
    /// <summary>A structural fault that would break the solver or void its answer (duplicate id, no slack, an island
    /// with no slack, a dangling reference, a self-loop, zero impedance, a non-finite number, a bad base or frequency).</summary>
    Error,
    /// <summary>Informational or a benign misconfiguration (multi-island, an OLTC record that does not fit its
    /// transformer: skipped, or regulating an unusual end).</summary>
    Warning,
}

/// <summary>One human-readable validation finding. <see cref="ToString"/> renders as "error: ..." / "warning: ...".</summary>
public sealed record GridDiagnostic(ValidationSeverity Severity, string Message)
{
    public override string ToString() => $"{(Severity == ValidationSeverity.Error ? "error" : "warning")}: {Message}";
}
