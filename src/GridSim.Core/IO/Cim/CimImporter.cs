using System.Globalization;
using System.Xml.Linq;

namespace GridSim.Core.IO.Cim;

/// <summary>A bus parsed from a CIM topological/connectivity node.</summary>
/// <param name="MRID">The node's rdf:ID (master resource id).</param>
/// <param name="Name">The IdentifiedObject.name.</param>
/// <param name="BaseKv">Nominal base voltage (kV) from the node's BaseVoltage, or 0 if unspecified.</param>
/// <param name="SvVKv">Export pack (2026-10): the node's <c>SvVoltage.v</c> (kV), read only with
/// <see cref="CimImportOptions.StartingVoltages"/>; null otherwise (as every bus was before). The bridge starts the
/// power flow from it (Bus.Vm = v / base kV).</param>
/// <param name="SvAngleDeg">Export pack: the node's <c>SvVoltage.angle</c> (degrees), read with
/// <see cref="CimImportOptions.StartingVoltages"/>; null otherwise. The bridge starts from it (Bus.VaDeg); at the slack
/// it is the angle reference.</param>
public sealed record CimBus(string MRID, string Name, double BaseKv = 0.0, double? SvVKv = null, double? SvAngleDeg = null);

/// <summary>A branch parsed from a CIM ACLineSegment plus its two terminals.</summary>
/// <param name="MRID">The line's rdf:ID.</param>
/// <param name="Name">The line name.</param>
/// <param name="FromNode">rdf:ID of the from-node.</param>
/// <param name="ToNode">rdf:ID of the to-node.</param>
/// <param name="R">Series resistance (ohms, as authored).</param>
/// <param name="X">Series reactance (ohms).</param>
/// <param name="B">Total shunt susceptance (siemens).</param>
/// <param name="BaseKv">D-DOC-101: the line's own base voltage (kV) from <c>ConductingEquipment.BaseVoltage</c>, or 0
/// when the document gives none. The bridge per-unitises against the nodes' base voltage and uses this only when
/// neither end node has one.</param>
/// <param name="Class">N295 (CIM3, 2026-10): the CIM class the branch was read from: "ACLineSegment" (the default, as
/// every branch was before) or "SeriesCompensator" (a series capacitor or reactor: <c>SeriesCompensator.r</c> /
/// <c>.x</c>, no charging). The exporter writes it back under the same class.</param>
/// <param name="G">CIM3 (2026-10, found by the conformity benchmark): the line's total shunt conductance
/// <c>ACLineSegment.gch</c> (siemens), 0 when not given. A GridSim branch has no shunt conductance, so the bridge puts
/// half of it at each end as bus shunt conductance (exact: the pi model's end shunts are bus shunts).</param>
/// <param name="RateAMva">Export pack (2026-10): the line's permanent apparent-power limit (MVA, the smallest PATL
/// <c>ApparentPowerLimit</c> on its terminals or on the line), read only with
/// <see cref="CimImportOptions.ApparentPowerRatings"/>; null otherwise. The bridge makes it the branch's rate A.</param>
public sealed record CimBranch(
    string MRID, string Name, string FromNode, string ToNode, double R, double X, double B, double BaseKv = 0.0,
    string Class = "ACLineSegment", double G = 0.0, double? RateAMva = null);

/// <summary>
/// N295 (CIM3, 2026-10): a CGMES StaticVarCompensator: a variable shunt susceptance between its inductive and capacitive
/// limits, regulating a voltage with a droop (slope). The ratings are kept as written (ohms), so a round trip is exact;
/// the bridge forms the susceptance limits from them.
/// </summary>
/// <param name="MRID">The SVC's mRID.</param>
/// <param name="Name">The SVC name.</param>
/// <param name="Node">The bus (mRID of a <see cref="CimBus"/>) it connects to.</param>
/// <param name="CapacitiveRatingOhm"><c>StaticVarCompensator.capacitiveRating</c> (ohm): the reactance at full
/// capacitive output, so the largest susceptance is 1 / |rating| (siemens, capacitive). Null when not given.</param>
/// <param name="InductiveRatingOhm"><c>StaticVarCompensator.inductiveRating</c> (ohm, negative in CGMES): the reactance
/// at full inductive output, so the smallest susceptance is -1 / |rating|. Null when not given.</param>
/// <param name="SlopeKvPerMvar"><c>StaticVarCompensator.slope</c> (kV per MVAr, zero or positive): the droop
/// V = Vset - slope x Q, Q the capacitive output. 0 when not given.</param>
/// <param name="Q">The SSH <c>StaticVarCompensator.q</c> (MVAr, LOAD sign as written: a negative value is capacitive
/// output). A plain solve holds the SVC at this q.</param>
/// <param name="Regulating">True when the SVC regulates a voltage (control enabled, an enabled voltage-mode
/// RegulatingControl with a target, or the CGMES 2.4.15 sVCControlMode voltage with a voltageSetPoint).</param>
/// <param name="TargetKv">The voltage set-point (kV) when <paramref name="Regulating"/>; else null.</param>
/// <param name="RegulatedNode">The bus whose voltage it regulates (its own <paramref name="Node"/> unless the control's
/// terminal is elsewhere); null when it does not regulate.</param>
/// <param name="BaseKv">The SVC's own base voltage (<c>ConductingEquipment.BaseVoltage</c>), 0 when not given; the
/// bridge uses its bus's base and this only when the bus has none.</param>
public sealed record CimStaticVarCompensator(
    string MRID, string Name, string Node, double? CapacitiveRatingOhm, double? InductiveRatingOhm, double SlopeKvPerMvar,
    double Q, bool Regulating, double? TargetKv, string? RegulatedNode, double BaseKv = 0.0);

/// <summary>A load parsed from a CIM EnergyConsumer or one of its subclasses (ConformLoad, NonConformLoad,
/// StationSupply), or from an AsynchronousMachine (N286, CIM2 2026-10: an induction machine is a fixed P / Q load in
/// the power flow, as powsybl maps it).</summary>
/// <param name="MRID">The consumer's rdf:ID.</param>
/// <param name="Name">The consumer name.</param>
/// <param name="Node">rdf:ID of the node it connects to.</param>
/// <param name="P">Active power (MW), load sign: positive = drawn from the network.</param>
/// <param name="Q">Reactive power (MVAr), load sign.</param>
/// <param name="Class">D-DOC-099: the CIM class the load was read from (EnergyConsumer, ConformLoad,
/// NonConformLoad, StationSupply or, since N286, AsynchronousMachine); the exporter writes it back under the same
/// class (an AsynchronousMachine with its RotatingMachine.p / q).</param>
public sealed record CimLoad(string MRID, string Name, string Node, double P, double Q, string Class = "EnergyConsumer");

/// <summary>
/// N286 (CIM2, 2026-10): a shunt compensator (LinearShuntCompensator, NonlinearShuntCompensator) or an EquivalentShunt,
/// resolved to its total admittance in the sections it is in for this case.
/// </summary>
/// <param name="MRID">The shunt's mRID.</param>
/// <param name="Name">The shunt name.</param>
/// <param name="Node">The bus (mRID of a <see cref="CimBus"/>) it connects to.</param>
/// <param name="G">Total positive-sequence conductance in service (siemens): the per-section conductance times the
/// sections in service (linear), the sum of the points up to the sections in service (non-linear), or
/// <c>EquivalentShunt.g</c>.</param>
/// <param name="B">Total positive-sequence susceptance in service (siemens; positive = capacitive), formed like
/// <paramref name="G"/>.</param>
/// <param name="Class">The CIM class (LinearShuntCompensator, NonlinearShuntCompensator, EquivalentShunt); the exporter
/// writes it back under the same class.</param>
/// <param name="BaseKv">The shunt's own base voltage (kV): <c>ConductingEquipment.BaseVoltage</c>, else
/// <c>ShuntCompensator.nomU</c>; 0 when neither is given. The bridge uses it only when the bus has no base voltage.</param>
/// <param name="Control">N296 (CIM3, 2026-10): the voltage control of a linear shunt under control (null otherwise, as
/// every shunt was before). <see cref="G"/> / <see cref="B"/> stay the admittance at the sections in service (what a
/// plain solve uses); the control says how a controlled solve may move them.</param>
public sealed record CimShunt(string MRID, string Name, string Node, double G, double B, string Class, double BaseKv = 0.0,
    CimShuntControl? Control = null);

/// <summary>
/// N296 (CIM3, 2026-10): the voltage control of a LinearShuntCompensator, as its RegulatingControl and EQ / SSH give it.
/// </summary>
/// <param name="TargetKv"><c>RegulatingControl.targetValue</c> (kV).</param>
/// <param name="DeadbandKv"><c>RegulatingControl.targetDeadband</c> (kV): the full width of the band a discrete control
/// keeps the voltage in (target +/- deadband / 2, as powsybl and PowerFactory read it); null when not given.</param>
/// <param name="RegulatedNode">The bus whose voltage it regulates (its own unless the control's terminal is elsewhere).</param>
/// <param name="Discrete"><c>RegulatingControl.discrete</c>: true (or absent) = switched in whole sections; false = the
/// data asks for continuous regulation.</param>
/// <param name="Sections">The sections in service (as the shunt's <see cref="CimShunt.B"/> holds them).</param>
/// <param name="MaximumSections"><c>ShuntCompensator.maximumSections</c>; null when not given (then it cannot add
/// sections).</param>
/// <param name="BPerSection"><c>LinearShuntCompensator.bPerSection</c> (siemens).</param>
/// <param name="GPerSection"><c>LinearShuntCompensator.gPerSection</c> (siemens; 0 when absent).</param>
public sealed record CimShuntControl(
    double TargetKv, double? DeadbandKv, string RegulatedNode, bool Discrete, double Sections, double? MaximumSections,
    double BPerSection, double GPerSection);

/// <summary>A generator parsed from a CIM SynchronousMachine.</summary>
/// <param name="MRID">The machine's rdf:ID.</param>
/// <param name="Name">The machine name.</param>
/// <param name="Node">rdf:ID of the node it connects to.</param>
/// <param name="P">Active power OUTPUT (MW), GENERATOR sign convention: positive means the machine is generating
/// (injecting into the network). CGMES itself stores <c>RotatingMachine.p</c> in the LOAD sign convention, so the
/// importer negates it on the way in and the exporter negates it on the way out (R267).</param>
/// <param name="Q">Reactive power OUTPUT (MVAr), generator sign convention (positive = the machine is producing
/// reactive power). Negated from CGMES <c>RotatingMachine.q</c> exactly as <paramref name="P"/> is. 0 when the
/// document gives no q.</param>
/// <param name="TargetKv">D-DOC-104: the voltage set-point (kV) of the machine's own (local) voltage
/// <c>RegulatingControl</c> (<c>RegulatingControl.targetValue</c>); null when there is none.</param>
/// <param name="VoltageControl">D-DOC-104: whether the machine controls its terminal voltage. True = it regulates
/// (a voltage-mode, enabled, local RegulatingControl); false = it does not (<c>RegulatingCondEq.controlEnabled</c>
/// false, a disabled or non-voltage control, or remote regulation, which GridSim does not represent: it is then
/// held at its <paramref name="Q"/>); null = the document says nothing, which the bridge treats as regulating.</param>
/// <param name="MinQ">D-DOC-104: <c>SynchronousMachine.minQ</c> (MVAr, generator convention); null = unlimited.</param>
/// <param name="MaxQ">D-DOC-104: <c>SynchronousMachine.maxQ</c> (MVAr); null = unlimited.</param>
/// <param name="MinP">D-DOC-104: <c>GeneratingUnit.minOperatingP</c> (MW) of the machine's unit; null = none.</param>
/// <param name="MaxP">D-DOC-104: <c>GeneratingUnit.maxOperatingP</c> (MW); null = none.</param>
/// <param name="RatedSMva">D-DOC-104: <c>RotatingMachine.ratedS</c> (MVA), the machine base; null = not given.</param>
/// <param name="H">D-DOC-104: the inertia constant (s, on <paramref name="RatedSMva"/>) from the dynamics profile's
/// <c>RotatingMachineDynamics.inertia</c>; null = not given.</param>
/// <param name="ReferencePriority">D-DOC-104: <c>SynchronousMachine.referencePriority</c>: 0 = no preference,
/// 1 = the highest priority to be the power-flow angle reference, 2 the next, and so on.</param>
/// <param name="Type">N254 (CIM1, 2026-10): the plant type, in GridSim's generator type vocabulary, taken from the
/// class of the machine's GeneratingUnit (see <see cref="CimImporter"/>, "Generator type"): "wind-onshore",
/// "wind-offshore" or "wind" (WindGeneratingUnit), "solar", "hydro", "pumped-storage", "nuclear", and for a
/// ThermalGeneratingUnit its single FossilFuel ("gas", "coal", "lignite", "oil") or "thermal". Empty when the
/// document gives no unit or a plain GeneratingUnit (as every CIM machine was before). The bridge copies it to
/// <c>Generator.Type</c>, so a wind or solar unit modelled in CGMES as a SynchronousMachine is converter-interfaced
/// plant to the fault studies (<c>ConverterPlant.IsConverterType</c>).</param>
/// <param name="Class">N286 (CIM2, 2026-10): the CIM class the injection was read from: "SynchronousMachine" (the
/// default, as every CIM generator was before), "ExternalNetworkInjection", "EquivalentInjection" or
/// "PowerElectronicsConnection" (CGMES 3.0). The exporter writes it back under the same class.</param>
/// <param name="ConverterPlant">N286: true for a CGMES 3.0 PowerElectronicsConnection, which is converter-interfaced
/// plant by definition whatever its units are; the bridge then sets <c>Generator.ConverterInterfaced</c>. False (the
/// default) leaves the converter question to <paramref name="Type"/>, as before.</param>
/// <param name="RegulatedNode">N295 (CIM3, 2026-10): the bus (mRID of a <see cref="CimBus"/>) whose voltage the
/// machine regulates when that is NOT its own node (remote voltage control: its RegulatingControl's terminal is on
/// another bus); null for local or no voltage control. Such a machine keeps <paramref name="VoltageControl"/> false and
/// no <paramref name="TargetKv"/>, so a plain solve holds it at its <paramref name="Q"/> exactly as before (GridSim's PV
/// bus holds its own voltage); <see cref="CimControlledPowerFlow"/> regulates the remote bus.</param>
/// <param name="RemoteTargetKv">N295: the remote voltage set-point (kV, <c>RegulatingControl.targetValue</c>) when
/// <paramref name="RegulatedNode"/> is set; else null.</param>
public sealed record CimGenerator(
    string MRID, string Name, string Node, double P, double Q = 0.0,
    double? TargetKv = null, bool? VoltageControl = null,
    double? MinQ = null, double? MaxQ = null, double? MinP = null, double? MaxP = null,
    double? RatedSMva = null, double? H = null, int ReferencePriority = 0, string Type = "",
    string Class = "SynchronousMachine", bool ConverterPlant = false,
    string? RegulatedNode = null, double? RemoteTargetKv = null);

/// <summary>
/// N253 (CIM1, 2026-10): a CLOSED switch that joins two different buses of the imported network. A switch has
/// zero impedance, so its two buses are electrically one; they are kept as two buses here only because the CGMES
/// bus-branch model keeps them apart: a <b>retained</b> switch (<c>Switch.retained</c> = true) is, by definition,
/// "retained in a bus branch model" so that its flow can be calculated, and its two sides are different
/// TopologicalNodes. The bridge (<see cref="CimGridModelBridge"/>) solves the two buses as one bus. In the
/// bus-branch view a closed switch that is NOT retained but still spans two TopologicalNodes is also listed here
/// (the TopologicalNodes disagree with the switch states; the importer says so in the diagnostics).
/// </summary>
/// <param name="MRID">The switch's mRID.</param>
/// <param name="Name">The switch name.</param>
/// <param name="Class">The CIM class (Breaker, Disconnector, LoadBreakSwitch, ...); the exporter writes it back.</param>
/// <param name="NodeA">The bus (mRID of a <see cref="CimBus"/>) at its first terminal.</param>
/// <param name="NodeB">The bus at its second terminal.</param>
/// <param name="Retained">The switch's <c>Switch.retained</c>.</param>
public sealed record CimSwitch(string MRID, string Name, string Class, string NodeA, string NodeB, bool Retained);

/// <summary>N253 (CIM1, 2026-10): which node view the importer builds the buses from.</summary>
public enum CimTopology
{
    /// <summary>The TopologicalNodes when the document has any (the bus-branch view, as before), checked against
    /// the switch states when the document also has ConnectivityNodes; otherwise the ConnectivityNodes joined
    /// across closed switches (the node-breaker view, through the topology processor).</summary>
    Auto,
    /// <summary>The TopologicalNodes; a document without any is refused.</summary>
    TopologicalNodes,
    /// <summary>The ConnectivityNodes joined across closed switches, even when TopologicalNodes are present (they
    /// are then only checked against). Use it when the switch states (SSH) are newer than the topology (TP), as
    /// when a study opens or closes a switch. A document without ConnectivityNodes is refused.</summary>
    Switches,
}

/// <summary>N254 (CIM1, 2026-10): where a tap changer's position is read from when the document carries both.</summary>
public enum CimTapPositions
{
    /// <summary>The SSH <c>TapChanger.step</c>: the starting position of the case (the input of a power flow).
    /// The SV <c>SvTapStep.position</c> is used only when there is no SSH step. The default, and the choice of
    /// PowerFactory and of powsybl (whose default initial-values profile is SSH).</summary>
    SteadyStateHypothesis,
    /// <summary>The SV <c>SvTapStep.position</c>: the position in the published solved state (a tap changer under
    /// automatic control may have moved during that solve). Use it to reproduce the published SV state. The SSH
    /// step is used for a changer that has no SvTapStep.</summary>
    StateVariables,
}

/// <summary>N253 / N254 (CIM1, 2026-10): how <see cref="CimImporter"/> reads a document. The defaults keep every
/// document that imported before importing exactly as before, except where the importer used to be wrong (a
/// node-breaker document with switches, a closed switch between two TopologicalNodes; see the class summary).</summary>
/// <param name="Topology">The node view (see <see cref="CimTopology"/>).</param>
/// <param name="TapPositions">Which profile's tap position wins when both are present (see <see cref="CimTapPositions"/>).</param>
/// <param name="ShuntSections">N286 (CIM2, 2026-10): the same choice for a shunt compensator's sections in service:
/// <see cref="CimTapPositions.SteadyStateHypothesis"/> (the default) reads the SSH <c>ShuntCompensator.sections</c>, and
/// the SV <c>SvShuntCompensatorSections.sections</c> only when there is no SSH value; <see cref="CimTapPositions.StateVariables"/>
/// reads the SV value when there is one. With neither, the EQ <c>ShuntCompensator.normalSections</c> is used (listed).</param>
/// <param name="StartingVoltages">Export pack (2026-10): read each TopologicalNode's <c>SvVoltage</c> (v, angle) into
/// <see cref="CimBus.SvVKv"/> / <see cref="CimBus.SvAngleDeg"/>, so the bridge starts the power flow from the state the
/// document carries (as a MATPOWER or RAW case starts from its stored Vm / Va). Off by default: the bridge then starts
/// every bus at 1.0 pu, 0 degrees, as before. <c>gridsim convert</c> reads its CGMES output back with it on.</param>
/// <param name="ApparentPowerRatings">Export pack (2026-10): read each line's and two-winding transformer's permanent
/// (PATL) <c>ApparentPowerLimit</c> into <see cref="CimBranch.RateAMva"/> / <see cref="CimTransformer.RateAMva"/>. Off by
/// default (no rating is read, as before). Current limits (amperes) are not converted.</param>
public sealed record CimImportOptions(
    CimTopology Topology = CimTopology.Auto, CimTapPositions TapPositions = CimTapPositions.SteadyStateHypothesis,
    CimTapPositions ShuntSections = CimTapPositions.SteadyStateHypothesis, bool StartingVoltages = false,
    bool ApparentPowerRatings = false)
{
    /// <summary>The default options: <see cref="CimTopology.Auto"/>, <see cref="CimTapPositions.SteadyStateHypothesis"/>
    /// for the tap positions and for the shunt sections.</summary>
    public static CimImportOptions Default { get; } = new();
}

/// <summary>One winding of a CIM PowerTransformer (a PowerTransformerEnd).</summary>
/// <param name="EndNumber">TransformerEnd.endNumber (1 = the from/HV end by convention here).</param>
/// <param name="Node">rdf:ID of the node this end connects to (via its terminal).</param>
/// <param name="R">This end's series resistance (ohms, referred to its own ratedU).</param>
/// <param name="X">This end's series reactance (ohms).</param>
/// <param name="RatedU">This end's rated voltage (kV) - the per-unit base for its impedance.</param>
/// <param name="TapRatio">For a THREE-winding transformer only: the off-nominal tap of this winding's tap changer
/// (1.0 = nominal). A two-winding transformer carries its tap on <see cref="CimTransformer.TapRatio"/> instead.</param>
/// <param name="PhaseShiftDeg">For a three-winding transformer only: this winding's phase shift (degrees).</param>
/// <param name="G">CIM2 (2026-10): this end's magnetising conductance <c>PowerTransformerEnd.g</c> (siemens, at its
/// <paramref name="RatedU"/>); 0 when not given. Not represented by the bridge (a GridSim branch has no shunt
/// conductance): a non-zero value is listed there with the MW it stands for.</param>
/// <param name="B">CIM2 (2026-10): this end's magnetising susceptance <c>PowerTransformerEnd.b</c> (siemens, at its
/// <paramref name="RatedU"/>); 0 when not given. The bridge puts it on the transformer branch as its shunt susceptance
/// (half at each end, the pi model).</param>
public sealed record CimTransformerEnd(
    int EndNumber, string Node, double R, double X, double RatedU, double TapRatio = 1.0, double PhaseShiftDeg = 0.0,
    double G = 0.0, double B = 0.0);

/// <summary>A CIM PowerTransformer resolved to its ends and off-nominal tap / phase shift.</summary>
/// <param name="MRID">The transformer's rdf:ID.</param>
/// <param name="Name">The transformer name.</param>
/// <param name="Ends">The transformer ends, ordered by endNumber (Ends[0] = from, Ends[1] = to; a three-winding
/// transformer has a third, tertiary end).</param>
/// <param name="TapRatio">Two-winding: off-nominal tap ratio from the tap changers, referred to the from side
/// (1.0 = nominal). Three-winding: always 1.0 - each winding's tap is on its own end.</param>
/// <param name="PhaseShiftDeg">Two-winding: phase shift (degrees) from the phase tap changers, referred to the from
/// side (0 = none). Three-winding: always 0 - see the ends.</param>
/// <param name="RateAMva">Export pack (2026-10): a two-winding unit's permanent apparent-power limit (MVA), read only
/// with <see cref="CimImportOptions.ApparentPowerRatings"/>; null otherwise. The bridge makes it the branch's rate A.</param>
public sealed record CimTransformer(
    string MRID, string Name, IReadOnlyList<CimTransformerEnd> Ends, double TapRatio, double PhaseShiftDeg,
    double? RateAMva = null);

/// <summary>A network parsed from CIM: buses, line branches, transformers, loads and generators.</summary>
/// <param name="Buses">The nodes of the chosen view (TopologicalNodes for bus-branch, else ConnectivityNodes).</param>
/// <param name="Branches">The AC line segments resolved to from/to nodes.</param>
/// <param name="Loads">The energy consumers resolved to their node.</param>
/// <param name="Generators">The synchronous machines resolved to their node.</param>
/// <param name="Transformers">The two- and three-winding power transformers resolved to their ends + taps.</param>
/// <param name="Diagnostics">Everything the importer could not represent and therefore left out, one plain-English
/// line each (a dangling line segment, equipment with no node, a disconnected terminal, out-of-service equipment,
/// equipment of a class the importer does not read, a transformer with an unsupported number of windings, ...).
/// Empty for a fully represented document. Never silently skipped: if it is not in the network, it is listed
/// here.</param>
/// <param name="ClosedSwitches">N253 (CIM1, 2026-10): the closed switches that join two different buses (retained
/// switches; see <see cref="CimSwitch"/>), in mRID order. The bridge solves the buses each one joins as one bus.
/// Null or empty when there are none.</param>
/// <param name="ShuntCompensators">N286 (CIM2, 2026-10): the shunt compensators and equivalent shunts, in document
/// order. Null or empty when there are none.</param>
/// <param name="StaticVarCompensators">N295 (CIM3, 2026-10): the static var compensators, in document order. Null or
/// empty when there are none.</param>
public sealed record CimNetwork(
    IReadOnlyList<CimBus> Buses,
    IReadOnlyList<CimBranch> Branches,
    IReadOnlyList<CimLoad> Loads,
    IReadOnlyList<CimGenerator> Generators,
    IReadOnlyList<CimTransformer> Transformers,
    IReadOnlyList<string>? Diagnostics = null,
    IReadOnlyList<CimSwitch>? ClosedSwitches = null,
    IReadOnlyList<CimShunt>? ShuntCompensators = null,
    IReadOnlyList<CimStaticVarCompensator>? StaticVarCompensators = null)
{
    /// <summary><see cref="StaticVarCompensators"/>, never null.</summary>
    public IReadOnlyList<CimStaticVarCompensator> Svcs => StaticVarCompensators ?? Array.Empty<CimStaticVarCompensator>();

    /// <summary><see cref="Diagnostics"/>, never null.</summary>
    public IReadOnlyList<string> Notes => Diagnostics ?? Array.Empty<string>();

    /// <summary><see cref="ClosedSwitches"/>, never null.</summary>
    public IReadOnlyList<CimSwitch> Couplers => ClosedSwitches ?? Array.Empty<CimSwitch>();

    /// <summary><see cref="ShuntCompensators"/>, never null.</summary>
    public IReadOnlyList<CimShunt> Shunts => ShuntCompensators ?? Array.Empty<CimShunt>();
}

/// <summary>
/// A CIM / IEC 61970 (CGMES) network importer. It reads striped RDF/XML equipment + topology and extracts the
/// buses, line branches (ACLineSegment), two- and three-winding transformers, loads (EnergyConsumer and its
/// subclasses ConformLoad, NonConformLoad and StationSupply) and generators (SynchronousMachine) with their
/// terminal connectivity. It matches elements and attributes by <b>local name</b>, so it is robust across CIM
/// schema versions (cim14/16/100).
///
/// <para><b>Multi-profile CGMES (R064).</b> CGMES splits each object across profiles: the EQ description uses
/// <c>rdf:ID</c>, and the TP / SSH / SV descriptions of the same object use <c>rdf:about</c> with the same mRID.
/// All descriptions of one mRID are merged into one object first (the first value of each property wins), so a
/// merged EQ+TP(+SSH) document resolves an EQ Terminal's equipment and its TP node together, and an
/// EnergyConsumer's SSH p/q join its EQ description. <c>urn:uuid:</c> identifiers (CGMES 3) are normalised.</para>
///
/// <para><b>Identifiers (D-DOC-105).</b> An identifier is reduced to its mRID by removing ONE leading <c>#</c> (a
/// reference) and then ONE leading <c>_</c> (the CGMES 2.4 XML-ID prefix), or a <c>urn:uuid:</c> prefix. Two
/// different XML IDs that reduce to the same mRID (<c>a</c> and <c>_a</c>) are refused, never merged.</para>
///
/// <para><b>One node view.</b> When the document has TopologicalNodes (bus-branch view) they are the buses, and a
/// terminal that only names a ConnectivityNode is mapped through <c>ConnectivityNode.TopologicalNode</c>; the
/// ConnectivityNodes are not also turned into buses (they used to be, leaving orphan buses). With no
/// TopologicalNodes the buses come from the ConnectivityNodes through the topology processor (node-breaker view).
/// <see cref="CimImportOptions.Topology"/> can force either view.</para>
///
/// <para><b>Topology processor (N253, CIM1 2026-10).</b> In the node-breaker view the ConnectivityNodes joined by
/// CLOSED switches (Switch and every subclass: Breaker, Disconnector, LoadBreakSwitch, Fuse, Jumper, ...) are one
/// bus; an open switch keeps them apart. This is the CGMES definition of a TopologicalNode ("a set of connectivity
/// nodes that, in the current network state, are connected together through any type of closed switches,
/// including jumpers", IEC 61970-301 / CGMES 2.4.15 TP) and what PowerFactory, PSS/E and powsybl do on import.
/// The rules:
/// (1) the state is the SSH <c>Switch.open</c>, else the EQ <c>Switch.normalOpen</c>; a switch with neither is
/// refused; (2) a switch whose terminal is disconnected (<c>connected</c> = false) or which is out of service
/// conducts nothing (treated as open); (3) a closed <b>retained</b> switch does not merge: by the CGMES definition
/// it is kept in the bus-branch model, its sides are two TopologicalNodes, so here they are two buses and the
/// switch is listed in <see cref="CimNetwork.ClosedSwitches"/> (the bridge solves them as one bus: a closed switch
/// has zero impedance); (4) a GroundDisconnector never joins two nodes (it earths one), and a closed one is listed.
/// The result is deterministic: the joining is a union-find whose outcome does not depend on the order of the
/// switches; each bus takes the smallest (ordinal) ConnectivityNode mRID of its set as its mRID, the name of the
/// smallest-mRID BusbarSection on it (else that ConnectivityNode's name), and the buses are ordered by mRID, so
/// neither the file order nor any dictionary order shows in the result. Its base voltage is the one its
/// ConnectivityNodes share through their VoltageLevel (directly or through a Bay); two different ones joined by a
/// closed switch are refused. When the document ALSO has TopologicalNodes, the two views are compared and every
/// disagreement is listed by name (a bus spanning two TopologicalNodes, a TopologicalNode split over two buses).
/// Benchmarked on the ENTSO-E CGMES 2.4.15 conformity models (see the CIM1 notes): MiniGrid node-breaker,
/// 103 ConnectivityNodes and 90 switches give 13 buses, one to one with its 13 TopologicalNodes; SmallGrid
/// node-breaker, 1225 and 1107 give 118, one to one with its 118.</para>
///
/// <para><b>Generator type (N254).</b> The class of a machine's GeneratingUnit sets <see cref="CimGenerator.Type"/>
/// (it used to be ignored, so every CIM machine was synchronous plant to the fault studies). The SV profile's
/// <c>SvTapStep.position</c> is read (used when there is no SSH step, or always with
/// <see cref="CimTapPositions.StateVariables"/>; a difference from the SSH step is listed), and a phase tap changer's
/// <c>xMin</c> / <c>xMax</c> reactance curve is checked and, where it matters, listed as not represented.</para>
///
/// <para><b>Equipment (N286, CIM2 2026-10).</b> Shunt compensators (LinearShuntCompensator, NonlinearShuntCompensator)
/// and EquivalentShunt are read with their sections in service (<see cref="CimNetwork.Shunts"/>); ExternalNetworkInjection,
/// EquivalentInjection and the CGMES 3.0 PowerElectronicsConnection are generators (<see cref="CimGenerator.Class"/>);
/// an AsynchronousMachine is a fixed P / Q load. See CimImporter.Equipment.cs for each mapping and its sources. Each
/// used to be listed as equipment the importer does not represent.</para>
///
/// <para><b>Fail loud (D-DOC-100).</b> A present attribute that does not parse (a decimal comma, a unit suffix, an
/// empty element, NaN) is refused naming the element, its mRID and the attribute; it used to become 0.0.</para>
///
/// <para>Anything left out is listed in <see cref="CimNetwork.Diagnostics"/>. Network topology carries no
/// timestamp, so no horizon guard applies here; a CIM state (SV profile) or measurement import - which is dated -
/// would go through <see cref="Diagnostics.ForwardInferenceGuard"/> like every other dated ingester.</para>
/// </summary>
public static partial class CimImporter
{
    // D-DOC-099 (2026-10): the CIM load classes. IEC 61970-301 (and CGMES 2.4.15 / 3.0 EQ) defines ConformLoad,
    // NonConformLoad and StationSupply as SUBCLASSES of EnergyConsumer, so they carry the same inherited SSH
    // attributes EnergyConsumer.p / EnergyConsumer.q. Only EnergyConsumer itself used to be read, so a real CGMES
    // file (whose loads are typically ConformLoad / NonConformLoad) lost every load without a word.
    private static readonly HashSet<string> LoadClasses =
        new(StringComparer.Ordinal) { "EnergyConsumer", "ConformLoad", "NonConformLoad", "StationSupply" };

    // The CIM switch classes (Switch and its subclasses). In the bus-branch view the TopologicalNodes are the
    // topology processor's result, so closed switches are already inside a node (a closed switch that still spans
    // two TopologicalNodes is listed in CimNetwork.ClosedSwitches); in the node-breaker view the topology processor
    // (CimImporter.Topology.cs, N253) joins the ConnectivityNodes across the closed ones. GroundDisconnector is an
    // earthing switch: it never joins two nodes.
    private static readonly HashSet<string> SwitchClasses = new(StringComparer.Ordinal)
    {
        "Switch", "Breaker", "Disconnector", "LoadBreakSwitch", "Fuse", "Jumper", "GroundDisconnector",
        "ProtectedSwitch", "Recloser", "Sectionaliser", "DisconnectingCircuitBreaker",
    };

    // N286 (CIM2, 2026-10): the injections read as generators besides SynchronousMachine (see ReadInjection).
    private static readonly HashSet<string> InjectionClasses = new(StringComparer.Ordinal)
    {
        "ExternalNetworkInjection", "EquivalentInjection", "PowerElectronicsConnection",
    };

    // N286: the shunt classes read into CimNetwork.Shunts (see ReadShunts). The abstract ShuntCompensator itself
    // (a CIM14-style document) is refused by name there.
    private static readonly HashSet<string> ShuntClasses = new(StringComparer.Ordinal)
    {
        "LinearShuntCompensator", "NonlinearShuntCompensator", "EquivalentShunt", "ShuntCompensator",
    };

    // Conducting equipment that is only a marker of a node (no impedance, no injection): nothing is lost by not
    // importing it, so it is not listed.
    private static readonly HashSet<string> NodeMarkerClasses = new(StringComparer.Ordinal) { "BusbarSection", "Junction" };

    // The concrete phase tap changer classes that are read (D-DOC-102).
    private static readonly HashSet<string> PhaseChangerClasses = new(StringComparer.Ordinal)
    {
        "PhaseTapChangerLinear", "PhaseTapChangerSymmetrical", "PhaseTapChangerAsymmetrical", "PhaseTapChangerTabular",
    };

    /// <summary>
    /// D-DOC-105 (NUM, 2026-10): the identifier table of one document. Every rdf:ID / rdf:about spelling that
    /// defines an object is recorded against the mRID it reduces to, so two DIFFERENT spellings that reduce to the
    /// same mRID (rdf:ID="a" and rdf:ID="_a") are refused instead of being merged into one object, and a reference
    /// (rdf:resource) whose spelling differs from the object it would land on is refused the same way.
    /// </summary>
    private sealed class IdTable
    {
        /// <summary>mRID -> the XML-ID spelling ("_a" or "a") that first defined it. A CGMES 3 urn:uuid: spelling
        /// is the bare mRID itself and is compatible with either, so it never registers a spelling.</summary>
        private readonly Dictionary<string, string> _spelling = new(StringComparer.Ordinal);

        /// <summary>Record that <paramref name="raw"/> (an rdf:ID or rdf:about value) defines an object and return
        /// its mRID. Throws <see cref="InvalidDataException"/> on a collision.</summary>
        public string Define(string raw, string elementName)
        {
            (string mrid, string? spelling) = Identify(raw);
            if (mrid.Length == 0 || spelling is null) return mrid;
            if (_spelling.TryGetValue(mrid, out string? first))
            {
                if (!string.Equals(first, spelling, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"CIM identifiers '{first}' and '{spelling}' (the second on a {elementName}) are different " +
                        $"rdf:IDs, but both reduce to the mRID '{mrid}'. Merging them would silently join two objects " +
                        "into one, so the document is refused; give the objects distinct identifiers.");
            }
            else
            {
                _spelling[mrid] = spelling;
            }
            return mrid;
        }

        /// <summary>Resolve a reference (rdf:resource) to the mRID it names. Throws when its spelling differs from
        /// the spelling of the object defined under that mRID (it would otherwise resolve to an object it does not
        /// name). A reference to an mRID not defined in this document (a boundary-set object) is returned as is.</summary>
        public string Reference(string raw, string where)
        {
            (string mrid, string? spelling) = Identify(raw);
            if (spelling is not null && _spelling.TryGetValue(mrid, out string? defined)
                && !string.Equals(defined, spelling, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"CIM reference '{raw}' in {where} names the rdf:ID '{spelling}', but the object with mRID '{mrid}' " +
                    $"is defined as '{defined}'. These are different identifiers; the reference is refused rather than " +
                    "resolved to an object it does not name.");
            return mrid;
        }
    }

    /// <summary>One CIM object: its class (element local name), mRID and merged properties.</summary>
    private sealed class CimObject
    {
        public required string Class { get; set; }
        public required string Mrid { get; init; }
        public required IdTable Ids { get; init; }
        public Dictionary<string, XElement> Props { get; } = new(StringComparer.Ordinal);

        public string? Value(string prop) => Props.TryGetValue(prop, out XElement? e) ? e.Value.Trim() : null;

        /// <summary>The raw rdf:resource text of a property, not normalised (an enumeration URI, for example).</summary>
        public string? RawResource(string prop)
        {
            if (!Props.TryGetValue(prop, out XElement? e)) return null;
            XAttribute? res = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "resource");
            return res?.Value.Trim();
        }

        /// <summary>
        /// N287 (CIM2, 2026-10): an enumeration value, written either as an rdf:resource (the CGMES form, a URI ending
        /// "...#Kind.value") or as element text ("Kind.value" or "value", as some non-ENTSO-E exporters write it). The
        /// raw string (for messages); null when the property is absent or empty. Only the resource form used to be
        /// read, so a text-form windGenUnitType, energyConversionCapability or fossilFuelType fell back to the plain
        /// type without a word, and a text-form RegulatingControl.mode was refused as missing. Compare values with
        /// <see cref="EnumLeaf"/>.
        /// </summary>
        public string? RawEnum(string prop)
        {
            string? r = RawResource(prop);
            if (!string.IsNullOrEmpty(r)) return r;
            string? text = Value(prop);
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>The mRID a reference property names (checked against the identifier table).</summary>
        public string? Resource(string prop)
        {
            string? raw = RawResource(prop);
            return raw is null ? null : Ids.Reference(raw, $"{Describe}, property {prop}");
        }

        public string Name => Value("IdentifiedObject.name") ?? Mrid;

        /// <summary>How a message names this object: class, name and mRID.</summary>
        public string Describe => $"{Class} '{Name}' (mRID {Mrid})";

        // ---- D-DOC-100 (2026-10): strict attribute parsing ---------------------------------------------------
        // The importer used to read every number through a TryParse that returned 0.0 on failure, so a value
        // written "1,25" (a decimal comma), "12kV" or "" (an empty element) silently became 0.0: a line with no
        // impedance, a load of nothing, a tap at neutral. Now an attribute that is PRESENT must parse completely,
        // as an invariant-culture finite number (or an integer / a boolean); otherwise the document is refused,
        // naming the element, its mRID, the attribute and the text found. An ABSENT attribute is still allowed
        // and takes the default stated at the call site (each call site says why that default is right).

        /// <summary>The first present one of <paramref name="props"/>, parsed as a finite double; null when none is
        /// present. Throws <see cref="InvalidDataException"/> on a present value that is not a finite number.</summary>
        public double? Number(params string[] props)
        {
            foreach (string prop in props)
            {
                string? s = Value(prop);
                if (s is null) continue;
                // NumberStyles.Float: optional sign, decimal point, exponent, surrounding white space; no
                // thousands separator, no currency symbol; invariant culture, so a decimal comma is refused.
                // double.IsFinite refuses "NaN", "Infinity" and "-Infinity", which TryParse would accept.
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
                    return v;
                throw new InvalidDataException(
                    $"CIM {Describe}: attribute {prop} = '{s}' is not a finite number (invariant culture, '.' as the " +
                    "decimal point). It used to be read as 0.0 without a word; it is refused instead.");
            }
            return null;
        }

        /// <summary>The first present one of <paramref name="props"/>, parsed as an integer (no decimal point, no
        /// exponent); null when none is present. Throws on a present value that is not an integer.</summary>
        public int? Integer(params string[] props)
        {
            foreach (string prop in props)
            {
                string? s = Value(prop);
                if (s is null) continue;
                if (int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
                    return v;
                throw new InvalidDataException(
                    $"CIM {Describe}: attribute {prop} = '{s}' is not an integer. It is refused rather than read as 0.");
            }
            return null;
        }

        /// <summary>The first present one of <paramref name="props"/>, parsed as an XML Schema boolean ("true",
        /// "false" in any letter case, "1", "0"); null when none is present. Throws on anything else.</summary>
        public bool? Boolean(params string[] props)
        {
            foreach (string prop in props)
            {
                string? s = Value(prop);
                if (s is null) continue;
                if (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                throw new InvalidDataException(
                    $"CIM {Describe}: attribute {prop} = '{s}' is not a boolean (true, false, 1 or 0).");
            }
            return null;
        }
    }

    /// <summary>Parse a CGMES/CIM RDF/XML document into a <see cref="CimNetwork"/>.</summary>
    /// <exception cref="InvalidDataException">The document is malformed in a way the importer refuses to guess
    /// past: an unparsable attribute, colliding identifiers, an unsupported tap changer, an ambiguous winding
    /// order, and so on. The message names the element, its mRID and what is wrong.</exception>
    public static CimNetwork Parse(string rdfXml) => Parse(rdfXml, null);

    /// <summary>Parse one CGMES/CIM RDF/XML document with the given options (null = <see cref="CimImportOptions.Default"/>).</summary>
    /// <exception cref="InvalidDataException">As <see cref="Parse(string)"/>.</exception>
    public static CimNetwork Parse(string rdfXml, CimImportOptions? options) =>
        ParseDocuments(new[] { ParseXml(rdfXml) }, options ?? CimImportOptions.Default);

    /// <summary>
    /// GR-667: read one RDF/XML document with DTD processing PROHIBITED and no resolver. XDocument.Parse processes a
    /// DTD (entity expansion), which an import of untrusted exchange files has no use for: CGMES documents carry no DTD,
    /// so a document that declares one is refused (XmlException) rather than expanded. Otherwise the same reading as
    /// XDocument.Parse (insignificant whitespace ignored, no line information).
    /// </summary>
    private static XDocument ParseXml(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
        };
        using var sr = new StringReader(xml);
        using var reader = System.Xml.XmlReader.Create(sr, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    /// <summary>
    /// N253 (CIM1, 2026-10): parse a CGMES model held as several documents (one per profile: EQ, TP, SSH, SV, DY,
    /// and the boundary set's EQ_BD / TP_BD), as CGMES exchanges them. The descriptions of one object across the
    /// documents are merged by mRID exactly as within one document (the first value of each property wins, in the
    /// order the documents are given). Diagram (DL) and geographical (GL) documents may be passed; they carry no
    /// conducting equipment and add nothing.
    /// </summary>
    /// <exception cref="ArgumentException">No documents, or a null one.</exception>
    /// <exception cref="InvalidDataException">As <see cref="Parse(string)"/>.</exception>
    public static CimNetwork Parse(IEnumerable<string> documents, CimImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var docs = new List<XDocument>();
        foreach (string? d in documents)
            docs.Add(ParseXml(d ?? throw new ArgumentException("A CIM document in the list is null.", nameof(documents))));
        if (docs.Count == 0)
            throw new ArgumentException("No CIM documents were given.", nameof(documents));
        return ParseDocuments(docs, options ?? CimImportOptions.Default);
    }

    private static CimNetwork ParseDocuments(IReadOnlyList<XDocument> docs, CimImportOptions options)
    {
        var notes = new List<string>();
        var ids = new IdTable();
        List<CimObject> objects = MergeByMrid(docs, ids, notes);
        var byId = objects.ToDictionary(o => o.Mrid, StringComparer.Ordinal);
        IEnumerable<CimObject> OfClass(string cls) => objects.Where(o => o.Class == cls);

        // Base voltages (kV) keyed by mRID, and voltage levels -> their base voltage, so nodes can be per-unitised.
        // A BaseVoltage without a positive nominalVoltage cannot be a per-unit base: refused (it used to give 0,
        // which the bridge then read as "no base voltage").
        var baseVoltage = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (CimObject o in OfClass("BaseVoltage"))
        {
            double? kv = o.Number("BaseVoltage.nominalVoltage");
            if (kv is not > 0)
                throw new InvalidDataException(
                    $"CIM {o.Describe}: BaseVoltage.nominalVoltage is {(kv is null ? "missing" : F(kv.Value) + " kV")}; " +
                    "a base voltage must be a positive number of kV.");
            baseVoltage[o.Mrid] = kv.GetValueOrDefault();
        }
        double NodeKv(CimObject node, string ownRef, string containerRef)
        {
            string? bv = node.Resource(ownRef);
            if (bv is null && node.Resource(containerRef) is string vl && byId.TryGetValue(vl, out CimObject? level))
            {
                // N253: in a node-breaker model most ConnectivityNodes sit in a Bay, which sits in a VoltageLevel
                // (CGMES EQ: Bay.VoltageLevel); the base voltage is the VoltageLevel's. A node directly in a
                // VoltageLevel reads it as before.
                if (level.Class == "Bay" && level.Resource("Bay.VoltageLevel") is string bayLevel
                    && byId.TryGetValue(bayLevel, out CimObject? viaBay))
                    level = viaBay;
                bv = level.Resource("VoltageLevel.BaseVoltage");
            }
            return bv is not null && baseVoltage.TryGetValue(bv, out double v) ? v : 0.0;
        }

        // Out-of-service equipment (D-DOC-103). CGMES 3 carries Equipment.inService (SSH: the state of this case)
        // and Equipment.normallyInService (EQ: the normal state); the SSH value wins when both are present. False
        // means the equipment is not part of the network in this case, so it is left out and listed.
        bool OutOfService(CimObject o)
        {
            bool? inService = o.Boolean("Equipment.inService", "Equipment.normallyInService");
            if (inService != false) return false;
            notes.Add($"{o.Describe} is out of service (Equipment.inService / normallyInService = false); left out.");
            return true;
        }

        // ---- the node view -------------------------------------------------------------------------------
        // N253: Auto takes the TopologicalNodes when there are any (the bus-branch view, exactly as before), else
        // the ConnectivityNodes through the topology processor; the caller can force either view.
        bool hasTn = OfClass("TopologicalNode").Any();
        bool hasCn = OfClass("ConnectivityNode").Any();
        bool busBranch = options.Topology switch
        {
            CimTopology.Auto => hasTn,
            CimTopology.TopologicalNodes => hasTn ? true : throw new InvalidDataException(
                "CIM import with CimTopology.TopologicalNodes: the document has no TopologicalNode (no TP profile), so " +
                "there is no bus-branch view to read."),
            CimTopology.Switches => hasCn ? false : throw new InvalidDataException(
                "CIM import with CimTopology.Switches: the document has no ConnectivityNode, so the buses cannot be " +
                "built from the switch states."),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Topology, "Unknown CimTopology."),
        };
        var buses = new List<CimBus>();
        var cnToTn = new Dictionary<string, string>(StringComparer.Ordinal);
        var boundaryBusIds = new List<string>();   // N295: CGMES 3.0 boundary ConnectivityNodes made buses (bus-branch view)
        // N295 / N306: the boundary ConnectivityNodes (named by a BoundaryPoint, or flagged ConnectivityNode.boundaryPoint as
        // in a 2.4.15 EQ_BD), used by both views below.
        var boundaryCns = new HashSet<string>(StringComparer.Ordinal);
        foreach (CimObject bp in OfClass("BoundaryPoint"))
            if (bp.Resource("BoundaryPoint.ConnectivityNode") is string bcn) boundaryCns.Add(bcn);
        foreach (CimObject cn in OfClass("ConnectivityNode"))
            if (cn.Boolean("ConnectivityNode.boundaryPoint") == true) boundaryCns.Add(cn.Mrid);
        if (busBranch)
        {
            var tnIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CimObject tn in OfClass("TopologicalNode"))
            {
                buses.Add(new CimBus(tn.Mrid, tn.Name,
                    NodeKv(tn, "TopologicalNode.BaseVoltage", "TopologicalNode.ConnectivityNodeContainer")));
                tnIds.Add(tn.Mrid);
            }
            // D-DOC-164 (2026-10): the diagnostic used to say every ConnectivityNode "is mapped onto" a bus, also
            // those that name no TopologicalNode (a terminal reaching the network only through one of those is
            // not connected) and those that name a TopologicalNode this document does not hold (a boundary node:
            // equipment on it is refused by the bridge as naming an unknown bus). They are now counted apart.
            int cnMapped = 0;
            var cnNoTn = new List<string>();
            var cnMissingTn = new List<string>();
            // N295 (CIM3, 2026-10): the CGMES 3.0 boundary. CGMES 3.0 no longer exchanges a boundary TP (TP_BD): the
            // boundary set is an EQ_BD of ConnectivityNodes named by BoundaryPoints, and an IGM's TP gives the terminals
            // on them no TopologicalNode. Such a boundary ConnectivityNode (named by a BoundaryPoint, or flagged
            // ConnectivityNode.boundaryPoint as in a 2.4.15 EQ_BD) with no TopologicalNode is a topological node of its
            // own: nothing can be switched at a boundary point, so it is one bus. It used to be "on no bus", which left
            // every tie line and every boundary injection of a CGMES 3.0 model unconnected (the MicroGrid BE's five).
            // Its base voltage is filled in below, from the equipment on it. A 2.4.15 model with its TP_BD is unchanged:
            // there the boundary ConnectivityNodes name their TopologicalNodes. (boundaryCns: gathered above.)
            var boundaryBuses = new List<CimObject>();
            foreach (CimObject cn in OfClass("ConnectivityNode"))
            {
                if (cn.Resource("ConnectivityNode.TopologicalNode") is string tn)
                {
                    cnToTn[cn.Mrid] = tn;   // kept even when tn is absent, so the bridge names it (behaviour as before)
                    if (tnIds.Contains(tn)) cnMapped++;
                    else cnMissingTn.Add($"{cn.Mrid} (-> {tn})");
                }
                else if (boundaryCns.Contains(cn.Mrid) && !tnIds.Contains(cn.Mrid))
                {
                    boundaryBuses.Add(cn);
                }
                else
                {
                    cnNoTn.Add(cn.Mrid);
                }
            }
            // Ordinal mRID order, so neither the file order nor the order of the boundary set shows in the buses.
            foreach (CimObject cn in boundaryBuses.OrderBy(c => c.Mrid, StringComparer.Ordinal))
            {
                buses.Add(new CimBus(cn.Mrid, cn.Name, 0.0));
                cnToTn[cn.Mrid] = cn.Mrid;
                boundaryBusIds.Add(cn.Mrid);
            }
            if (boundaryBuses.Count > 0)
                notes.Add($"CGMES 3.0 boundary: {boundaryBuses.Count} boundary ConnectivityNode(s) have no TopologicalNode (a boundary " +
                          "set without TP_BD); each is a bus of its own: " +
                          $"{string.Join(", ", boundaryBuses.OrderBy(c => c.Mrid, StringComparer.Ordinal).Select(c => $"'{c.Name}' ({c.Mrid})"))}.");
            int cnCount = cnMapped + cnNoTn.Count + cnMissingTn.Count;
            if (cnCount > 0)
                notes.Add($"Bus-branch view: {tnIds.Count} TopologicalNode(s) are the buses; {cnMapped} ConnectivityNode(s) " +
                          "are mapped onto them, not imported as extra buses.");
            if (cnNoTn.Count > 0)
                notes.Add($"Bus-branch view: {cnNoTn.Count} ConnectivityNode(s) name no TopologicalNode, so they are on no " +
                          $"bus and a terminal that reaches the network only through one of them is not connected: " +
                          $"{string.Join(", ", cnNoTn)}.");
            if (cnMissingTn.Count > 0)
                notes.Add($"Bus-branch view: {cnMissingTn.Count} ConnectivityNode(s) name a TopologicalNode that is not in " +
                          $"this document (a boundary node?), so they are on no bus of this network: " +
                          $"{string.Join(", ", cnMissingTn)}.");
        }
        // ---- N253: switches and the topology processor -------------------------------------------------------
        // The switches are read in both views: in the node-breaker view the processor joins the ConnectivityNodes
        // across the closed ones (it used to make every ConnectivityNode its own bus, so a substation with breakers
        // imported as separate islands); in the bus-branch view a closed switch still spanning two TopologicalNodes
        // (a retained switch) is listed for the bridge to join. When both views are present, the processor also
        // runs as a check of the TopologicalNodes (only when the document has switches: without them there is
        // nothing to check the TP's joins against).
        Dictionary<string, List<CimObject>> terminalsOf = TerminalsByEquipment(objects);
        List<SwitchState> switches = ReadSwitches(objects, terminalsOf, notes);
        TopologyResult? topo = null;
        if (!busBranch || (hasCn && switches.Count > 0))
            topo = RunTopology(objects, switches,
                cn => NodeKv(cn, "ConnectivityNode.BaseVoltage", "ConnectivityNode.ConnectivityNodeContainer"),
                terminalsOf, notes);
        if (!busBranch)
        {
            buses.AddRange(topo!.Buses);
            notes.Add(TopologySummary(OfClass("ConnectivityNode").Count(), topo));
            // N306 (LF3, October 2026): a CGMES 3.0 boundary ConnectivityNode (a boundary one naming no TopologicalNode,
            // the bus-branch rule above) sits in a Line container, not a VoltageLevel, so its bus here has no base voltage
            // either; it used to stay without one in this view (the base fill-in below ran for the bus-branch view only).
            // Its bus now takes the base the equipment on it declares, by the same rule and note. Only a bus that has no
            // base of its own (no other ConnectivityNode joined to it brings one), so every bus that had a base keeps it,
            // and a 2.4.15 boundary node with its TP_BD (it names a TopologicalNode) is unchanged.
            foreach (CimObject cn in OfClass("ConnectivityNode").OrderBy(c => c.Mrid, StringComparer.Ordinal))
            {
                if (!boundaryCns.Contains(cn.Mrid) || cn.Resource("ConnectivityNode.TopologicalNode") is not null) continue;
                if (!topo!.CnToBus.TryGetValue(cn.Mrid, out string? busId) || busId is null) continue;
                CimBus? onBus = buses.FirstOrDefault(b => string.Equals(b.MRID, busId, StringComparison.Ordinal));
                if (onBus is not null && !(onBus.BaseKv > 0) && !boundaryBusIds.Contains(busId)) boundaryBusIds.Add(busId);
            }
            if (hasTn)
                notes.Add($"Node-breaker view chosen (CimTopology.Switches): the document's {OfClass("TopologicalNode").Count()} " +
                          "TopologicalNode(s) are not the buses; they are only checked against.");
        }
        if (topo is not null && hasTn) CompareWithTopologicalNodes(topo, objects, byId, notes);

        // ---- terminals: equipment -> node, and each terminal's own node ------------------------------------
        var terminals = new List<(string Equipment, string Node, int Seq, int Order)>();
        var terminalNode = new Dictionary<string, string>(StringComparer.Ordinal);
        var equipmentWithTerminals = new HashSet<string>(StringComparer.Ordinal);
        int order = 0;
        foreach (CimObject t in OfClass("Terminal"))
        {
            string? equip = t.Resource("Terminal.ConductingEquipment");
            if (equip is not null) equipmentWithTerminals.Add(equip);
            // D-DOC-103 (2026-10): Terminal.connected (CGMES 2.4.15 SSH: ACDCTerminal.connected) = false means the
            // terminal is open (its breaker or disconnector is open in this case): the equipment is not connected
            // to the node there. It used to be ignored, leaving an open line in service. An absent value is the
            // EQ-only case and means connected (the topology profile has already placed the terminal).
            if (t.Boolean("ACDCTerminal.connected", "Terminal.connected") == false)
            {
                notes.Add($"Terminal {t.Mrid}{(equip is null ? "" : $" of {equip}")} is disconnected (connected = false); " +
                          "the equipment is not connected there.");
                continue;
            }
            string? node;
            if (busBranch)
            {
                node = t.Resource("Terminal.TopologicalNode");
                if (node is null && t.Resource("Terminal.ConnectivityNode") is string cn)
                    node = cnToTn.GetValueOrDefault(cn);
            }
            else
            {
                // N253: the ConnectivityNode's bus from the topology processor. A ConnectivityNode that is not in
                // the document (a boundary node whose boundary set was not loaded) keeps its own mRID, so the bridge
                // refuses equipment on it by name, as before.
                node = t.Resource("Terminal.ConnectivityNode") is string cn
                    ? topo!.CnToBus.GetValueOrDefault(cn, cn)
                    : null;
            }
            if (node is null)
            {
                notes.Add($"Terminal {t.Mrid}{(equip is null ? "" : $" of {equip}")} has no node in the " +
                          $"{(busBranch ? "bus-branch" : "node-breaker")} view; it is not connected.");
                continue;
            }
            // An absent sequence number is 0 (document order then decides, below); a present one must be an integer.
            int seq = t.Integer("ACDCTerminal.sequenceNumber", "Terminal.sequenceNumber") ?? 0;
            if (equip is not null) terminals.Add((equip, node, seq, order++));
            terminalNode[t.Mrid] = node;
        }
        // Terminals of one equipment in sequence-number order (document order when unnumbered). OrderBy is a
        // stable sort and the document order breaks every tie, so the order is fully defined.
        var nodesOf = terminals
            .GroupBy(t => t.Equipment, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Seq).ThenBy(t => t.Order).Select(t => t.Node).ToList(),
                StringComparer.Ordinal);
        List<string> NodesOf(string mrid) => nodesOf.TryGetValue(mrid, out List<string>? l) ? l : new List<string>();

        // ---- N295 (CIM3): the base voltage of a CGMES 3.0 boundary bus -----------------------------------------
        // A boundary ConnectivityNode sits in a Line container, not a VoltageLevel, so it has no base voltage of its own.
        // It takes the BaseVoltage the equipment on it declares (ConductingEquipment.BaseVoltage): of the equipment with a
        // terminal on it that declares one, the smallest mRID (ordinal), so the choice is defined whatever the file order.
        // The choice is only a per-unit label: when the equipment disagree (MicroGrid BE: a 380 kV line and a 400 kV
        // boundary injection on one node), the line to it is per-unit on each end's base with the ratio as a tap (N294,
        // exact), so the solved kV and MW do not depend on it. Said in a note, with any disagreement.
        for (int bi = 0; bi < buses.Count; bi++)
        {
            CimBus bus = buses[bi];
            if (!boundaryBusIds.Contains(bus.MRID)) continue;
            var declared = terminals
                .Where(t => string.Equals(t.Node, bus.MRID, StringComparison.Ordinal))
                .Select(t => t.Equipment)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(e => e, StringComparer.Ordinal)
                .Select(e => byId.TryGetValue(e, out CimObject? eq) && eq.Resource("ConductingEquipment.BaseVoltage") is string bvRef
                             && baseVoltage.TryGetValue(bvRef, out double ekv) ? (Equipment: eq, Kv: ekv) : (Equipment: (CimObject?)null, Kv: 0.0))
                .Where(x => x.Equipment is not null)
                .ToList();
            if (declared.Count == 0)
            {
                notes.Add($"CGMES 3.0 boundary bus '{bus.Name}' ({bus.MRID}): no equipment on it declares a BaseVoltage, so it has none.");
                continue;
            }
            buses[bi] = bus with { BaseKv = declared[0].Kv };
            var others = declared.Where(x => x.Kv != declared[0].Kv).Select(x => $"{x.Equipment!.Describe} {F(x.Kv)} kV").ToList();
            notes.Add($"CGMES 3.0 boundary bus '{bus.Name}' ({bus.MRID}): base voltage {F(declared[0].Kv)} kV, from {declared[0].Equipment!.Describe}" +
                      (others.Count == 0 ? "." : $"; other equipment on it declare {string.Join(", ", others)} (a line to it is per-unit on each end's base, N294)."));
        }

        // ---- lines ---------------------------------------------------------------------------------------
        var branches = new List<CimBranch>();
        // N295 (CIM3, 2026-10): a SeriesCompensator (a series capacitor or reactor) is read here too: it is a series
        // branch of SeriesCompensator.r + j SeriesCompensator.x ohm with no charging, per-unitised exactly as a line.
        // CGMES 2.4.15 / 3.0 EQ: "A Series Compensator is a series capacitor or reactor or an AC transmission line
        // without charging susceptance"; powsybl maps it to a line with b = 0, PowerFactory to a series reactance. It used
        // to be listed as equipment the importer does not represent and left out (the CGMES 3.0 MicroGrid BE's
        // SER-RLC-1230822986, x = -31.83 ohm, carries 100 MW). Its varistor (a fault-current protection that conducts
        // only above varistorVoltageThreshold) plays no part in a load flow and is said; its zero sequence r0 / x0 too.
        foreach (CimObject line in objects.Where(o => o.Class is "ACLineSegment" or "SeriesCompensator"))
        {
            if (OutOfService(line)) continue;
            List<string> ends = NodesOf(line.Mrid);
            if (ends.Count < 2)
            {
                notes.Add($"{line.Class} {line.Name} ({line.Mrid}) has {ends.Count} connected terminal(s); a {(line.Class == "ACLineSegment" ? "line" : "series compensator")} needs " +
                          "two, so it is left out.");
                continue;
            }
            if (ends.Count > 2)
                throw new InvalidDataException(
                    $"CIM {line.Describe} has {ends.Count} connected terminals; a line segment has exactly two.");
            if (!busBranch && string.Equals(ends[0], ends[1], StringComparison.Ordinal)
                && terminalsOf.TryGetValue(line.Mrid, out List<CimObject>? lineTerms)
                && lineTerms.Select(t => t.Resource("Terminal.ConnectivityNode")).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                // N253: the line's two ends are on two DIFFERENT ConnectivityNodes that closed switches join into one
                // bus (the switches bypass the line). It is short-circuited and carries no current; a branch from a
                // bus to itself is not a real element, so it is left out and said. (Two terminals on the SAME
                // ConnectivityNode are a data error and still reach the bridge, which refuses the self-loop.)
                notes.Add($"{line.Describe}: both its ends are on bus {ends[0]} once the closed switches are joined (the " +
                          "switches bypass it); it carries no current and is left out.");
                continue;
            }
            // The line's own base voltage (ConductingEquipment.BaseVoltage), carried for the bridge's per-unit
            // fallback when neither end node has one (D-DOC-101).
            double lineKv = line.Resource("ConductingEquipment.BaseVoltage") is string bvRef
                            && baseVoltage.TryGetValue(bvRef, out double lk) ? lk : 0.0;
            if (line.Class == "SeriesCompensator")
            {
                // N295: r absent = 0 (a pure capacitor or reactor); x is the element itself, so it is required.
                double sx = line.Number("SeriesCompensator.x")
                    ?? throw new InvalidDataException(
                        $"CIM {line.Describe} has no SeriesCompensator.x; a series compensator is its series reactance, so it cannot be mapped without it.");
                if (line.Boolean("SeriesCompensator.varistorPresent") == true)
                    notes.Add($"{line.Describe}: its varistor (a protection that conducts only above varistorVoltageThreshold, " +
                              "a fault condition) is not represented; in a load flow the compensator is its r + jx.");
                branches.Add(new CimBranch(line.Mrid, line.Name, ends[0], ends[1],
                    line.Number("SeriesCompensator.r") ?? 0.0, sx, 0.0, lineKv, "SeriesCompensator"));
                continue;
            }
            // An absent r / x / bch is 0 (the CGMES EQ profile makes r and x required, so in a valid file absence
            // only happens for bch on a line without charging); a present but unparsable one is refused.
            // CIM3 (2026-10): ACLineSegment.gch (the line's shunt conductance, siemens) used to be ignored without a word.
            // The ENTSO-E MicroGrid BE lines carry it (BE-Line_5: 4.2e-5 S, 3.5 MW at 410 kV), and its published SV is
            // reproduced only with it (see the CIM3 notes); absent = 0.
            branches.Add(new CimBranch(line.Mrid, line.Name, ends[0], ends[1],
                line.Number("ACLineSegment.r") ?? 0.0,
                line.Number("ACLineSegment.x") ?? 0.0,
                line.Number("ACLineSegment.bch") ?? 0.0,
                lineKv, G: line.Number("ACLineSegment.gch") ?? 0.0));
        }

        // ---- inertia from the dynamics profile (D-DOC-104) -------------------------------------------------
        // A CGMES DY synchronous machine model (SynchronousMachineSimplified, ...TimeConstantReactance,
        // ...EquivalentCircuit) names its machine by SynchronousMachineDynamics.SynchronousMachine and carries
        // H as RotatingMachineDynamics.inertia (seconds, on the machine's ratedS).
        var inertia = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (CimObject o in objects)
        {
            if (o.Resource("SynchronousMachineDynamics.SynchronousMachine") is not string sm) continue;
            double? h = o.Number("RotatingMachineDynamics.inertia");
            if (h is null) continue;   // a dynamics model with no inertia carries nothing the bridge reads
            if (h < 0)
                throw new InvalidDataException($"CIM {o.Describe}: RotatingMachineDynamics.inertia = {F(h.Value)} s is negative.");
            if (!inertia.TryAdd(sm, h.Value))
                throw new InvalidDataException(
                    $"CIM {o.Describe}: a second dynamics model gives machine {sm} an inertia; which one applies is ambiguous.");
        }

        // N254: the fuels of the thermal units, by the unit they name (FossilFuel.ThermalGeneratingUnit; the
        // association is held on the fuel side), built once.
        ILookup<string, CimObject> fuelsOf = objects
            .Where(f => f.Class == "FossilFuel" && f.Resource("FossilFuel.ThermalGeneratingUnit") is not null)
            .ToLookup(f => f.Resource("FossilFuel.ThermalGeneratingUnit")!, StringComparer.Ordinal);

        // ---- N286: the units of each CGMES 3.0 PowerElectronicsConnection --------------------------------
        // PowerElectronicsUnit.PowerElectronicsConnection names the connection (the association is held on the unit
        // side), so the units are gathered once, by the connection they name.
        ILookup<string, CimObject> unitsOf = objects
            .Where(u => u.Resource("PowerElectronicsUnit.PowerElectronicsConnection") is not null)
            .ToLookup(u => u.Resource("PowerElectronicsUnit.PowerElectronicsConnection")!, StringComparer.Ordinal);

        // ---- N295 (CIM3): the points of each reactive capability curve, by the curve they name -------------
        ILookup<string, CimObject> curvePointsOf = objects
            .Where(o => o.Class == "CurveData" && o.Resource("CurveData.Curve") is not null)
            .ToLookup(o => o.Resource("CurveData.Curve")!, StringComparer.Ordinal);

        // ---- loads and generators ------------------------------------------------------------------------
        var loads = new List<CimLoad>();
        var generators = new List<CimGenerator>();
        int asynchronous = 0;
        foreach (CimObject o in objects)
        {
            if (LoadClasses.Contains(o.Class))
            {
                if (OutOfService(o)) continue;
                string? node = NodesOf(o.Mrid).FirstOrDefault();
                if (node is null) { notes.Add($"{o.Class} {o.Name} ({o.Mrid}) has no connected terminal; left out."); continue; }
                // EnergyConsumer.p / .q are inherited by every subclass under the same (EnergyConsumer.) name. An
                // absent value is 0: an EQ-only document has no SSH profile and so no set-point.
                loads.Add(new CimLoad(o.Mrid, o.Name, node,
                    o.Number("EnergyConsumer.p") ?? 0.0, o.Number("EnergyConsumer.q") ?? 0.0, o.Class));
            }
            else if (o.Class == "AsynchronousMachine")
            {
                // N286 (CIM2, 2026-10): an induction machine (motor or generator) is a fixed P / Q in the power flow.
                // EE call, as powsybl's CGMES import (AsynchronousMachineConversion: "Will convert it to a Load (a
                // fixed injection). We make no difference based on the type (motor/generator)") and PowerFactory's
                // load-flow treatment of an asynchronous machine with a P / Q set-point. Its SSH RotatingMachine.p /
                // .q are in the LOAD sign convention (CGMES 2.4.15 / 3.0 SSH: "positive sign means flow into the
                // equipment from the network"), which is CimLoad's own convention, so they are copied as they are
                // (an induction GENERATOR has a negative p: a negative load). Its slip, its torque curve and its
                // motor-starting behaviour are not represented (counted in one note). Before N286 it was listed as
                // conducting equipment the importer does not represent, and its load was lost.
                if (OutOfService(o)) continue;
                string? node = NodesOf(o.Mrid).FirstOrDefault();
                if (node is null) { notes.Add($"AsynchronousMachine {o.Name} ({o.Mrid}) has no connected terminal; left out."); continue; }
                loads.Add(new CimLoad(o.Mrid, o.Name, node,
                    o.Number("RotatingMachine.p") ?? 0.0, o.Number("RotatingMachine.q") ?? 0.0, o.Class));
                asynchronous++;
            }
            else if (o.Class == "SynchronousMachine")
            {
                if (OutOfService(o)) continue;
                string? node = NodesOf(o.Mrid).FirstOrDefault();
                if (node is null) { notes.Add($"SynchronousMachine {o.Name} ({o.Mrid}) has no connected terminal; left out."); continue; }
                generators.Add(ReadMachine(o, node, byId, terminalNode, inertia, fuelsOf, curvePointsOf, notes));
            }
            else if (InjectionClasses.Contains(o.Class))
            {
                // N286: ExternalNetworkInjection, EquivalentInjection and (CGMES 3.0) PowerElectronicsConnection are
                // generators (see ReadInjection for each mapping and its sources).
                if (OutOfService(o)) continue;
                string? node = NodesOf(o.Mrid).FirstOrDefault();
                if (node is null) { notes.Add($"{o.Class} {o.Name} ({o.Mrid}) has no connected terminal; left out."); continue; }
                generators.Add(ReadInjection(o, node, byId, terminalNode, unitsOf, curvePointsOf, notes));
            }
        }
        if (asynchronous > 0)
            notes.Add($"{asynchronous} AsynchronousMachine(s) are read as fixed P / Q loads (their RotatingMachine.p / q, load " +
                      "sign), as powsybl maps them; their slip and motor dynamics are not represented.");

        // ---- N286: shunt compensators ----------------------------------------------------------------------
        List<CimShunt> shunts = ReadShunts(objects, NodesOf, OutOfService,
            o => o.Resource("ConductingEquipment.BaseVoltage") is string bvRef && baseVoltage.TryGetValue(bvRef, out double k) ? k : 0.0,
            options, notes, byId, terminalNode);

        // ---- N295 (CIM3): static var compensators ------------------------------------------------------------
        List<CimStaticVarCompensator> svcs = ReadStaticVarCompensators(objects, NodesOf, OutOfService,
            o => o.Resource("ConductingEquipment.BaseVoltage") is string bvRef && baseVoltage.TryGetValue(bvRef, out double k) ? k : 0.0,
            byId, terminalNode, notes);

        // ---- equipment of a class the importer does not represent -----------------------------------------
        // The Diagnostics contract is "if it is not in the network, it is listed". Conducting equipment of any
        // class not read above (a ShuntCompensator, an EquivalentInjection, an AsynchronousMachine, ...) used to
        // vanish without a word; each is now listed. Switches are represented by the node view (N253): the
        // topology processor or the TopologicalNodes hold their result, and ClosedSwitches the rest; not listed here.
        foreach (CimObject o in objects)
        {
            if (!equipmentWithTerminals.Contains(o.Mrid)) continue;
            if (o.Class is "ACLineSegment" or "PowerTransformer" or "SynchronousMachine" or "AsynchronousMachine"
                    or "SeriesCompensator" or "StaticVarCompensator"
                || LoadClasses.Contains(o.Class) || InjectionClasses.Contains(o.Class) || ShuntClasses.Contains(o.Class)
                || NodeMarkerClasses.Contains(o.Class) || SwitchClasses.Contains(o.Class))
                continue;
            notes.Add($"{o.Describe} is conducting equipment of a class the importer does not represent; left out.");
        }

        // ---- N253: closed switches between two different buses ---------------------------------------------
        // Bus-branch view: a switch is not a branch; the TopologicalNodes already hold the result of the switch
        // states, except for a closed switch whose two sides are still on two TopologicalNodes (a retained one, or
        // a TP that disagrees with the switch state). Such a switch used to be dropped, leaving its two buses
        // unjoined (an island where the network is connected); it is now listed in ClosedSwitches for the bridge.
        // Node-breaker view: the closed retained switches (the processor does not merge them).
        var busLabel = buses.GroupBy(b => b.MRID, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        List<CimSwitch> couplers = ClosedCouplers(switches, NodesOf,
            mrid => busLabel.TryGetValue(mrid, out string? nm) ? $"'{nm}' ({mrid})" : mrid, busBranch, notes);
        if (busBranch && switches.Count > 0)
            notes.Add($"Bus-branch view: {switches.Count} switch(es) are not imported as branches; the TopologicalNodes " +
                      "already hold the result of the switch states" +
                      (couplers.Count > 0
                          ? $", except {couplers.Count} closed switch(es) between two TopologicalNodes (listed), which the " +
                            "bridge joins at zero impedance."
                          : "."));

        IReadOnlyList<CimTransformer> transformers = ParseTransformers(objects, terminalNode, notes, OutOfService, options);
        // Export pack (2026-10): the starting voltages and the apparent-power ratings, each only when asked for (off by
        // default, so every document imports exactly as before).
        if (options.StartingVoltages) buses = WithStartingVoltages(objects, buses, notes);
        if (options.ApparentPowerRatings)
        {
            Dictionary<string, double> rating = ApparentPowerRatings(objects, notes);
            branches = branches.Select(b => rating.TryGetValue(b.MRID, out double r) ? b with { RateAMva = r } : b).ToList();
            transformers = transformers.Select(t => rating.TryGetValue(t.MRID, out double r) && t.Ends.Count == 2 ? t with { RateAMva = r } : t).ToList();
        }
        return new CimNetwork(buses, branches, loads, generators, transformers, notes, couplers, shunts, svcs);
    }

    /// <summary>
    /// Read one SynchronousMachine (already known to be connected at <paramref name="node"/>) with everything the
    /// bridge needs to make it a GridSim generator (D-DOC-104): the SSH p / q, the voltage control, the reactive and
    /// active limits, the machine base, the inertia and the reference priority.
    /// </summary>
    private static CimGenerator ReadMachine(
        CimObject o, string node, Dictionary<string, CimObject> byId, Dictionary<string, string> terminalNode,
        Dictionary<string, double> inertia, ILookup<string, CimObject> fuelsOf, ILookup<string, CimObject> curvePointsOf,
        List<string> notes)
    {
        // R267 (fixed 2026-10, Q2 authorised by Mark): the SIGN CONVENTION of the SSH machine set-points.
        //
        // CGMES (ENTSO-E CGMES 2.4.15, IEC 61970-600-2, Steady State Hypothesis profile, RotatingMachine.p
        // and RotatingMachine.q) stores the active and reactive power of EVERY rotating machine in the LOAD
        // sign convention: "Load sign convention is used, i.e. positive sign means flow into the equipment
        // from the network." (the same wording sits on EnergyConsumer.p / .q). A machine that is GENERATING
        // 40 MW is therefore written as p = -40. GridSim's CimGenerator (and the GridModel Generator it is
        // bridged into) uses the GENERATOR convention, positive = injecting into the network. So both values
        // are negated here. The importer used to copy p verbatim, which turned every generating machine in
        // a real CGMES file into a 40 MW motor, and every motor into a generator.
        //
        // 'SynchronousMachine.p' / '.q' are not CGMES attribute names; they are accepted as aliases of the
        // RotatingMachine attributes (the same SSH quantity under a non-standard prefix), so they take the
        // same convention and the same negation. '0.0 - x' rather than '-x' so that an absent or zero value
        // is +0.0, never -0.0 (a negative zero would print as "-0" and is not a value anyone wrote). An absent
        // value is 0: an EQ-only document has no SSH set-point.
        double pLoadSign = o.Number("RotatingMachine.p", "SynchronousMachine.p") ?? 0.0;
        double qLoadSign = o.Number("RotatingMachine.q", "SynchronousMachine.q") ?? 0.0;
        double pGen = 0.0 - pLoadSign;   // generator convention: positive = generating
        double qGen = 0.0 - qLoadSign;   // generator convention: positive = producing vars

        // The machine base (RotatingMachine.ratedS, MVA) and the reactive limits (SynchronousMachine.minQ / maxQ,
        // MVAr; CGMES gives them in the generator sense, maxQ = the most the unit can produce).
        double? ratedS = o.Number("RotatingMachine.ratedS");
        if (ratedS is <= 0)
            throw new InvalidDataException($"CIM {o.Describe}: RotatingMachine.ratedS = {F(ratedS.GetValueOrDefault())} MVA is not positive.");
        double? minQ = o.Number("SynchronousMachine.minQ"), maxQ = o.Number("SynchronousMachine.maxQ");
        if (minQ is not null && maxQ is not null && minQ > maxQ)
            throw new InvalidDataException(
                $"CIM {o.Describe}: SynchronousMachine.minQ = {F(minQ.Value)} is above maxQ = {F(maxQ.Value)} MVAr.");
        // N295 (CIM3, 2026-10): the machine's InitialReactiveCapabilityCurve, evaluated at its active power. It used to be
        // listed as not represented, and the limits came from minQ / maxQ only: the ENTSO-E MicroGrid BE-G1 gives
        // minQ = maxQ = 0 with a curve of +/- 200 to 300 MVAr, so it was pinned at 0 MVAr. See CapabilityAt for the rule.
        if (o.Resource("SynchronousMachine.InitialReactiveCapabilityCurve") is string curveRef)
        {
            if (!byId.TryGetValue(curveRef, out CimObject? curve))
            {
                notes.Add($"{o.Describe} names reactive capability curve {curveRef}, which is not in this document; the reactive " +
                          "limits come from SynchronousMachine.minQ / maxQ.");
            }
            else
            {
                (double cMin, double cMax) = CapabilityAt(curve, curvePointsOf[curve.Mrid], pGen);
                string written = minQ is null && maxQ is null
                    ? "."
                    : $" instead of SynchronousMachine.minQ / maxQ ({(minQ is null ? "-" : F(minQ.Value))} / " +
                      $"{(maxQ is null ? "-" : F(maxQ.Value))}): the curve is the machine's capability at its operating point.";
                notes.Add($"{o.Describe}: its reactive capability curve {curve.Describe} at its P = {F(pGen)} MW gives " +
                          $"{F(cMin)} .. {F(cMax)} MVAr, used as its reactive limits{written}");
                minQ = cMin;
                maxQ = cMax;
            }
        }

        // The active limits live on the machine's GeneratingUnit (any subclass: Thermal, Hydro, Wind, ...).
        double? minP = null, maxP = null;
        string type = "";
        if (o.Resource("RotatingMachine.GeneratingUnit") is string unitRef && byId.TryGetValue(unitRef, out CimObject? unit))
        {
            minP = unit.Number("GeneratingUnit.minOperatingP");
            maxP = unit.Number("GeneratingUnit.maxOperatingP");
            if (minP is not null && maxP is not null && minP > maxP)
                throw new InvalidDataException(
                    $"CIM {unit.Describe}: GeneratingUnit.minOperatingP = {F(minP.Value)} is above maxOperatingP = {F(maxP.Value)} MW.");
            type = UnitType(unit, fuelsOf, notes);
        }

        // SynchronousMachine.referencePriority (SSH): 0 = no preference, 1 = highest priority as the power-flow
        // angle reference, 2 the next, ... The bridge chooses the slack by it.
        int priority = o.Integer("SynchronousMachine.referencePriority") ?? 0;
        if (priority < 0)
            throw new InvalidDataException($"CIM {o.Describe}: SynchronousMachine.referencePriority = {priority} is negative.");

        // Inertia: H is on the machine's own rating, so without ratedS it has no base and cannot be used.
        double? h = inertia.TryGetValue(o.Mrid, out double hv) ? hv : null;
        if (h is not null && ratedS is null)
            throw new InvalidDataException(
                $"CIM {o.Describe}: the dynamics profile gives an inertia H = {F(h.Value)} s, but the machine has no " +
                "RotatingMachine.ratedS, which is the base H is expressed on.");

        // Voltage control (shared with the other injections since N286; N295 adds the remote regulated bus).
        (bool? voltageControl, double? targetKv, string? regulatedNode, double? remoteKv) =
            ReadVoltageControl(o, node, qGen, byId, terminalNode, notes);

        return new CimGenerator(o.Mrid, o.Name, node, pGen, qGen,
            TargetKv: targetKv, VoltageControl: voltageControl, MinQ: minQ, MaxQ: maxQ, MinP: minP, MaxP: maxP,
            RatedSMva: ratedS, H: h, ReferencePriority: priority, Type: type,
            RegulatedNode: regulatedNode, RemoteTargetKv: remoteKv);
    }

    /// <summary>
    /// N254 (CIM1, 2026-10): a GeneratingUnit's plant type in GridSim's generator type vocabulary (the strings the
    /// bundled cases and the GDA importer use, which <c>FuelTypes.Classify</c> and
    /// <c>ConverterPlant.IsConverterType</c> read). The CGMES 2.4.15 / 3.0 EQ subclasses of GeneratingUnit:
    /// <list type="bullet">
    /// <item>WindGeneratingUnit: "wind-onshore" / "wind-offshore" from <c>WindGeneratingUnit.windGenUnitType</c>
    /// (WindGenUnitKind.onshore / offshore), else "wind". Converter plant to the fault studies. EE call: CGMES 2.4.15
    /// has no converter class, so wind and PV are exchanged as SynchronousMachines; the unit class is the only
    /// statement of what the plant is (powsybl's CGMES import reads it the same way, as the generator's energy source).</item>
    /// <item>SolarGeneratingUnit: "solar" (converter plant).</item>
    /// <item>HydroGeneratingUnit: "pumped-storage" when <c>HydroGeneratingUnit.energyConversionCapability</c> is
    /// pumpAndGenerator, else "hydro" (both synchronous).</item>
    /// <item>NuclearGeneratingUnit: "nuclear".</item>
    /// <item>ThermalGeneratingUnit: its fuel when exactly one <c>FossilFuel</c> names it (FossilFuel.fossilFuelType
    /// gas -> "gas"; coal or hardCoal -> "coal"; lignite or brownCoalLignite -> "lignite"; oil or oilShale -> "oil"),
    /// else "thermal" (several fuels, another fuel, or none: listed when a fuel was given but not used).</item>
    /// <item>a plain GeneratingUnit: "" (the document does not say; as every machine was before).</item>
    /// <item>any other class (a CGMES extension): "" and listed.</item>
    /// </list>
    /// </summary>
    private static string UnitType(CimObject unit, ILookup<string, CimObject> fuelsOf, List<string> notes)
    {
        // The value of an enumeration ("...#WindGenUnitKind.offshore" -> "offshore"); N287: written as an rdf:resource
        // or, since CIM2, as element text.
        static string? Kind(string? raw) => EnumLeaf(raw);
        switch (unit.Class)
        {
            case "WindGeneratingUnit":
                return Kind(unit.RawEnum("WindGeneratingUnit.windGenUnitType")) switch
                {
                    "onshore" => "wind-onshore",
                    "offshore" => "wind-offshore",
                    null => "wind",
                    string other => throw new InvalidDataException(
                        $"CIM {unit.Describe}: WindGeneratingUnit.windGenUnitType '{other}' is not onshore or offshore."),
                };
            case "SolarGeneratingUnit":
                return "solar";
            case "HydroGeneratingUnit":
                return Kind(unit.RawEnum("HydroGeneratingUnit.energyConversionCapability")) == "pumpAndGenerator"
                    ? "pumped-storage" : "hydro";
            case "NuclearGeneratingUnit":
                return "nuclear";
            case "ThermalGeneratingUnit":
            {
                // FossilFuel.ThermalGeneratingUnit names the unit (the association is held on the fuel side).
                List<string> fuels = fuelsOf[unit.Mrid]
                    .OrderBy(f => f.Mrid, StringComparer.Ordinal)
                    .Select(f => Kind(f.RawEnum("FossilFuel.fossilFuelType")) ?? "")
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (fuels.Count == 0) return "thermal";
                string? mapped = fuels.Count == 1
                    ? fuels[0] switch
                    {
                        "gas" => "gas",
                        "coal" or "hardCoal" => "coal",
                        "lignite" or "brownCoalLignite" => "lignite",
                        "oil" or "oilShale" => "oil",
                        _ => null,
                    }
                    : null;
                if (mapped is not null) return mapped;
                notes.Add($"{unit.Describe}: its fuel(s) {string.Join(", ", fuels.Select(f => f.Length == 0 ? "(none given)" : f))} " +
                          "do not name one fuel GridSim distinguishes; its type is \"thermal\".");
                return "thermal";
            }
            case "GeneratingUnit":
                return "";
            default:
                notes.Add($"{unit.Describe}: the generating unit class {unit.Class} is not one the importer maps to a plant " +
                          "type; the machine's type is left empty.");
                return "";
        }
    }

    /// <summary>
    /// Merge every description of one mRID (the EQ <c>rdf:ID</c> and the TP/SSH/SV <c>rdf:about</c> of the same
    /// object) into one <see cref="CimObject"/>, in first-appearance order. The first value of each property
    /// wins; the class comes from the first description that is not a bare <c>rdf:Description</c>. Two different
    /// spellings of one mRID are refused (D-DOC-105, see <see cref="IdTable"/>).
    /// <para>
    /// N342 (FB1, 2026-10): only ONE description of an object may DEFINE it with <c>rdf:ID</c>; the others describe
    /// it by <c>rdf:about</c>. That is the CGMES exchange rule (IEC 61970-552: the profile that owns the object, EQ for
    /// equipment, writes <c>rdf:ID</c>; TP / SSH / SV add to it with <c>rdf:about</c>), and within one document it is
    /// also the RDF/XML rule (W3C RDF 1.1 XML Syntax, section 2.14: an <c>rdf:ID</c> value must be unique within the
    /// document, since it names one node relative to the document's base URI). The importer used to merge a second
    /// <c>rdf:ID</c> of the same mRID into the first object without a word (first value of each property wins), so two
    /// different objects that happened to share an identifier became one: CimEquipmentTests' nonlinear shunt with two
    /// "section 1" points written under one rdf:ID is how it was found (the duplicate-point check never saw the second
    /// point). Now:
    /// <list type="bullet">
    /// <item>two <c>rdf:ID</c> definitions of one mRID in ONE document: refused, naming the identifier and both
    /// elements (invalid RDF/XML; which description is the object cannot be decided).</item>
    /// <item>two <c>rdf:ID</c> definitions of one mRID in DIFFERENT documents (two profiles each claiming to define the
    /// object; legal RDF/XML, since each document has its own base, but not the CGMES pattern): merged exactly as
    /// before (first value of each property wins, in document order) and listed in the notes by name, so the reader
    /// sees it. EE call (Ruling R1): not refused, because a real exchange that repeats a boundary definition in two
    /// files is plausible and nothing in it is contradictory until a property differs, which the first-wins merge
    /// then states.</item>
    /// <item>an <c>rdf:ID</c> plus any number of <c>rdf:about</c> descriptions (the legitimate cross-profile merge,
    /// in one document or across several): unchanged, bit for bit (no note, same object, same properties).</item>
    /// </list>
    /// </para>
    /// </summary>
    private static List<CimObject> MergeByMrid(IReadOnlyList<XDocument> docs, IdTable ids, List<string> notes)
    {
        var list = new List<CimObject>();
        var byId = new Dictionary<string, CimObject>(StringComparer.Ordinal);
        // N342: mRID -> the first element that DEFINED it with rdf:ID, and the index of its document (0-based, in
        // the order the documents were given). Used only to detect a second rdf:ID definition; the merge itself is
        // unchanged.
        var definedBy = new Dictionary<string, (XElement Element, int Doc)>(StringComparer.Ordinal);
        // N253: several documents (one per CGMES profile) are read in the order given, exactly as if they were one
        // document; with one document this is the loop it always was. Each element carries the index of its document
        // (N342) so a second rdf:ID can be told apart within one document and across two.
        foreach ((XElement el, int docIndex) in docs.SelectMany((d, i) => d.Descendants().Select(e => (e, i))))
        {
            XAttribute? id = el.Attributes().FirstOrDefault(a => a.Name.LocalName is "ID" or "about");
            if (id is null) continue;
            string mrid = ids.Define(id.Value, el.Name.LocalName);
            if (mrid.Length == 0) continue;
            if (id.Name.LocalName == "ID")
            {
                if (definedBy.TryGetValue(mrid, out (XElement Element, int Doc) first))
                {
                    string firstWhat = DescribeElement(first.Element, mrid);
                    string secondWhat = DescribeElement(el, mrid);
                    if (first.Doc == docIndex)
                        throw new InvalidDataException(
                            $"CIM: the rdf:ID '{id.Value}' is defined twice in one document (document {docIndex + 1}): " +
                            $"first by {firstWhat}, then by {secondWhat}. An rdf:ID must be unique within a document " +
                            "(RDF/XML; CGMES writes one rdf:ID per object and describes it elsewhere by rdf:about), so " +
                            "which element is the object cannot be decided; the document is refused rather than the two " +
                            "merged into one. Give each object its own identifier.");
                    notes.Add(
                        $"The rdf:ID '{id.Value}' is defined in two documents: document {first.Doc + 1} ({firstWhat}) and " +
                        $"document {docIndex + 1} ({secondWhat}). CGMES defines an object with rdf:ID in one profile and " +
                        "describes it by rdf:about in the others; the two definitions are merged as one object, the first " +
                        "value of each property winning in document order.");
                }
                else
                {
                    definedBy[mrid] = (el, docIndex);
                }
            }
            if (!byId.TryGetValue(mrid, out CimObject? obj))
            {
                obj = new CimObject { Class = el.Name.LocalName, Mrid = mrid, Ids = ids };
                byId[mrid] = obj;
                list.Add(obj);
            }
            else if (obj.Class == "Description" && el.Name.LocalName != "Description")
            {
                obj.Class = el.Name.LocalName;
            }
            foreach (XElement prop in el.Elements())
                obj.Props.TryAdd(prop.Name.LocalName, prop);
        }
        return list;
    }

    /// <summary>N342: an element for a message, before it is merged: its class, its own IdentifiedObject.name when it
    /// has one, and the mRID, in the importer's usual "Class 'Name' (mRID x)" form.</summary>
    private static string DescribeElement(XElement el, string mrid)
    {
        string? name = el.Elements().FirstOrDefault(p => p.Name.LocalName == "IdentifiedObject.name")?.Value.Trim();
        return $"{el.Name.LocalName} '{(string.IsNullOrEmpty(name) ? mrid : name)}' (mRID {mrid})";
    }

    // Two-and three-winding PowerTransformers: group each transformer's PowerTransformerEnds (impedance on their
    // own ratedU) and match the tap changers by the end they regulate. CGMES holds the impedance on the ends, and
    // the tap on a tap changer that references one end.
    private static IReadOnlyList<CimTransformer> ParseTransformers(
        List<CimObject> objects, Dictionary<string, string> terminalNode, List<string> notes,
        Func<CimObject, bool> outOfService, CimImportOptions options)
    {
        // ---- N254: the SV tap positions (SvTapStep), changer mRID -> position --------------------------------
        // SvTapStep.position (SV profile) is the tap position in the published solved state; it is a float (a
        // continuous tap is allowed). One per changer; a second, or one with no changer or no position, is refused.
        var svPosition = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (CimObject o in objects)
        {
            if (o.Class != "SvTapStep") continue;
            string changer = o.Resource("SvTapStep.TapChanger")
                ?? throw new InvalidDataException($"CIM {o.Describe} names no tap changer (SvTapStep.TapChanger).");
            double position = o.Number("SvTapStep.position")
                ?? throw new InvalidDataException($"CIM {o.Describe} has no SvTapStep.position.");
            if (!svPosition.TryAdd(changer, position))
                throw new InvalidDataException(
                    $"CIM {o.Describe}: tap changer {changer} has two SvTapStep positions; which one applies is ambiguous.");
        }

        // ---- tap changer tables (D-DOC-102): table mRID -> step -> point -----------------------------------
        var ratioPoints = new Dictionary<string, Dictionary<int, CimObject>>(StringComparer.Ordinal);
        var phasePoints = new Dictionary<string, Dictionary<int, CimObject>>(StringComparer.Ordinal);
        foreach (CimObject o in objects)
        {
            Dictionary<string, Dictionary<int, CimObject>> into;
            string tableProp;
            if (o.Class == "RatioTapChangerTablePoint")
            {
                into = ratioPoints;
                tableProp = "RatioTapChangerTablePoint.RatioTapChangerTable";
            }
            else if (o.Class == "PhaseTapChangerTablePoint")
            {
                into = phasePoints;
                tableProp = "PhaseTapChangerTablePoint.PhaseTapChangerTable";
            }
            else
            {
                continue;
            }
            string table = o.Resource(tableProp)
                ?? throw new InvalidDataException($"CIM {o.Describe} names no table ({tableProp}).");
            int step = o.Integer("TapChangerTablePoint.step")
                ?? throw new InvalidDataException($"CIM {o.Describe} has no TapChangerTablePoint.step.");
            if (!into.TryGetValue(table, out Dictionary<int, CimObject>? byStep)) into[table] = byStep = new();
            if (!byStep.TryAdd(step, o))
                throw new InvalidDataException(
                    $"CIM {o.Describe}: table {table} has two points for step {step} ({byStep[step].Mrid} and {o.Mrid}).");
        }

        // ---- tap changers by the end they regulate -----------------------------------------------------------
        var ratioChanger = new Dictionary<string, CimObject>(StringComparer.Ordinal);
        var phaseChanger = new Dictionary<string, CimObject>(StringComparer.Ordinal);
        foreach (CimObject o in objects)
        {
            Dictionary<string, CimObject> into;
            string? end;
            if (o.Class == "RatioTapChanger")
            {
                into = ratioChanger;
                end = o.Resource("RatioTapChanger.TransformerEnd") ?? o.Resource("TapChanger.TransformerEnd");
            }
            else if (PhaseChangerClasses.Contains(o.Class))
            {
                into = phaseChanger;
                end = o.Resource("PhaseTapChanger.TransformerEnd") ?? o.Resource("TapChanger.TransformerEnd");
            }
            else if (o.Class.StartsWith("PhaseTapChanger", StringComparison.Ordinal)
                     && o.Class is not ("PhaseTapChangerTable" or "PhaseTapChangerTablePoint"))
            {
                // D-DOC-102: the abstract PhaseTapChanger / PhaseTapChangerNonLinear (or a CIM14-style plain
                // PhaseTapChanger with a type attribute) used to be read as a linear changer with no increment,
                // so it gave no phase shift without a word. Refused by name instead.
                throw new InvalidDataException(
                    $"CIM {o.Describe}: phase tap changer class {o.Class} is not supported; the importer reads " +
                    "PhaseTapChangerLinear, PhaseTapChangerSymmetrical, PhaseTapChangerAsymmetrical and PhaseTapChangerTabular.");
            }
            else
            {
                continue;
            }
            if (end is null) { notes.Add($"{o.Class} {o.Mrid} names no transformer end; ignored."); continue; }
            if (!into.TryAdd(end, o))
                throw new InvalidDataException(
                    $"CIM {o.Describe}: transformer end {end} already has a {(into == ratioChanger ? "ratio" : "phase")} tap " +
                    $"changer ({into[end].Mrid}); which one applies is ambiguous.");
        }

        // The position of a changer: TapChanger.step (SSH, this case). Without an SSH profile there is no step, and
        // the EQ TapChanger.normalStep (the normal operating position) is used and listed; with neither the
        // position is unknown and refused (it used to be 0, which with a neutral step of, say, 10 is ten steps off).
        //
        // N254 (CIM1, 2026-10): the SV SvTapStep.position used to be ignored. Now, by CimImportOptions.TapPositions:
        //  - SteadyStateHypothesis (default): the SSH step; the SV position only when there is no SSH step (before
        //    the EQ normalStep, since it is this case's state and the normal step is not). An SV position that
        //    differs from the SSH step is listed (a changer under automatic control moved in the published solve).
        //  - StateVariables: the SV position when there is one (to reproduce the published solved state), the SSH
        //    step otherwise; a difference is listed the same way.
        // With no SvTapStep in the document every path is exactly what it was (SSH step, else normalStep).
        double Step(CimObject c, string classAlias)
        {
            double? ssh = c.Number("TapChanger.step", classAlias + ".step");
            double? sv = svPosition.TryGetValue(c.Mrid, out double p) ? p : null;
            double? step = ssh;
            if (sv is double svStep && (ssh is null || options.TapPositions == CimTapPositions.StateVariables))
            {
                step = svStep;
                if (ssh is null)
                    notes.Add($"{c.Describe} has no TapChanger.step (SSH); its SV position SvTapStep.position {F(svStep)} is used.");
                else if (ssh.Value != svStep)
                    notes.Add($"{c.Describe}: its SV position {F(svStep)} is used (CimTapPositions.StateVariables), not its " +
                              $"SSH step {F(ssh.Value)}.");
            }
            else if (sv is double other && ssh is double s && other != s)
            {
                notes.Add($"{c.Describe}: its SSH step {F(s)} is used; the published solved state (SV) has it at " +
                          $"{F(other)} (a changer under automatic control moved in that solve). Import with " +
                          "CimTapPositions.StateVariables to reproduce the SV state.");
            }
            if (step is null)
            {
                step = c.Number("TapChanger.normalStep")
                    ?? throw new InvalidDataException(
                        $"CIM {c.Describe} has no TapChanger.step (SSH) and no TapChanger.normalStep (EQ); its position is unknown.");
                notes.Add($"{c.Describe} has no TapChanger.step (no SSH profile); its normalStep {F(step.Value)} is used.");
            }
            double? low = c.Number("TapChanger.lowStep"), high = c.Number("TapChanger.highStep");
            if ((low is not null && step < low) || (high is not null && step > high))
                throw new InvalidDataException(
                    $"CIM {c.Describe}: step {F(step.Value)} is outside lowStep..highStep " +
                    $"({(low is null ? "-" : F(low.Value))}..{(high is null ? "-" : F(high.Value))}).");
            return step.Value;
        }
        double Neutral(CimObject c) => c.Number("TapChanger.neutralStep")
            ?? throw new InvalidDataException($"CIM {c.Describe} has no TapChanger.neutralStep.");
        double Required(CimObject c, string prop) => c.Number(prop)
            ?? throw new InvalidDataException($"CIM {c.Describe} has no {prop}.");

        // A tabular changer's point for the current step: its ratio (and angle for a phase table). The table
        // point's r / x / g / b are percentage corrections to the transformer's impedance at that step; they are
        // not represented, so a non-zero one is refused by name rather than dropped.
        (double Ratio, double Angle) TablePoint(CimObject c, Dictionary<string, Dictionary<int, CimObject>> points,
            string table, double step, bool phase)
        {
            if (step != Math.Floor(step) || Math.Abs(step) > int.MaxValue)
                throw new InvalidDataException($"CIM {c.Describe}: tabular step {F(step)} is not a whole step number.");
            int k = (int)step;
            if (!points.TryGetValue(table, out Dictionary<int, CimObject>? byStep) || !byStep.TryGetValue(k, out CimObject? p))
                throw new InvalidDataException($"CIM {c.Describe}: its table {table} has no point for step {k}.");
            foreach (string corr in new[] { "r", "x", "g", "b" })
            {
                double? v = p.Number("TapChangerTablePoint." + corr);
                if (v is not null && v != 0.0)
                    throw new InvalidDataException(
                        $"CIM {p.Describe}: TapChangerTablePoint.{corr} = {F(v.Value)} % is an impedance correction at step " +
                        $"{k}; per-step impedance corrections are not supported.");
            }
            double ratio = Required(p, "TapChangerTablePoint.ratio");
            double angle = phase ? Required(p, "PhaseTapChangerTablePoint.angle") : 0.0;
            return (ratio, angle);
        }

        // One end's off-nominal voltage ratio (1 = nominal) and phase shift (degrees) from the changers that
        // regulate it, whether any changer regulates it (Tapped) and whether a phase changer does (Phased).
        (double Ratio, double Angle, bool Tapped, bool Phased) EndTap(string endMrid)
        {
            double ratio = 1.0, angle = 0.0;
            bool tapped = false, phased = false;
            if (ratioChanger.TryGetValue(endMrid, out CimObject? rc))
            {
                tapped = true;
                double step = Step(rc, "RatioTapChanger");
                if (rc.Resource("RatioTapChanger.RatioTapChangerTable") is string table)
                {
                    ratio = TablePoint(rc, ratioPoints, table, step, phase: false).Ratio;
                }
                else
                {
                    // Linear ratio changer (as before, the same expression): 1 + n * du / 100.
                    ratio = 1.0 + (step - Neutral(rc)) * Required(rc, "RatioTapChanger.stepVoltageIncrement") / 100.0;
                }
            }
            if (phaseChanger.TryGetValue(endMrid, out CimObject? pc))
            {
                tapped = true;
                phased = true;
                (double pr, double pa) = PhaseTap(pc);
                ratio *= pr;   // exact when pr is 1.0 (linear, symmetrical): the ratio is unchanged
                angle = pa;
            }
            if (!(ratio > 0.0) || !double.IsFinite(ratio))
                throw new InvalidDataException(
                    $"CIM transformer end {endMrid}: its tap changers give a voltage ratio of {F(ratio)}, which is not positive.");
            return (ratio, angle, tapped, phased);
        }

        // D-DOC-102 (2026-10): every concrete CGMES phase tap changer. n = step - neutralStep.
        //  - Linear:        angle = n * stepPhaseShiftIncrement (degrees), ratio 1 (as before).
        //  - Symmetrical:   du = n * voltageStepIncrement / 100; angle = 2 * atan(du / 2), ratio 1.
        //  - Asymmetrical:  du as above, theta = windingConnectionAngle; with dx = 1 + du * cos(theta) and
        //                   dy = du * sin(theta): angle = atan2(dy, dx), ratio = sqrt(dx^2 + dy^2).
        //  - Tabular:       the table point of the current step gives the angle and the ratio.
        // Formulae as given for these classes by IEC 61970-301 / ENTSO-E CGMES 2.4.15 EQ (the PhaseTapChanger
        // class descriptions) [VERIFY: the standard is not on disk; checked against the class descriptions as
        // published, and the asymmetrical ratio is taken in the same sense as a RatioTapChanger's 1 + n du, so an
        // asymmetrical changer with theta = 0 equals a ratio changer, step for step]. DetMath, not System.Math,
        // for the transcendentals (D1: the same bits on every CPU and operating system).
        // N254 (CIM1, 2026-10): PhaseTapChangerLinear.xMin / xMax and PhaseTapChangerNonLinear.xMin / xMax (ohms) used
        // to be ignored without a word. CGMES (IEC 61970-301) describes them as a "u" shaped curve: the series
        // reactance of the phase shifter is xMin at the mid (neutral) step and rises to xMax at the end steps; CGMES
        // 3.0 adds that PowerTransformerEnd.x shall be consistent with xMin and, when the two disagree,
        // PowerTransformerEnd.x is used. GridSim does not represent the curve: the transformer's PowerTransformerEnd
        // impedance is used at every step. So: a negative value or xMin above xMax is refused (a contradiction);
        // a curve that actually rises (xMax > xMin) on a changer that is OFF its neutral step is listed by name,
        // with both values, because the reactance used there is understated (by up to xMax - xMin at the end
        // steps). [VERIFY] the exact curve between the two points (the CGMES text gives only the two end values;
        // the ENTSO-E phase shift transformer modelling guide has the shape) before representing it.
        void ReactanceCurve(CimObject c, string cls, double step)
        {
            double? xMin = c.Number(cls + ".xMin"), xMax = c.Number(cls + ".xMax");
            if (xMin is < 0 || xMax is < 0)
                throw new InvalidDataException(
                    $"CIM {c.Describe}: {cls}.xMin / xMax ({(xMin is null ? "-" : F(xMin.Value))} / " +
                    $"{(xMax is null ? "-" : F(xMax.Value))} ohm) is negative; a phase shifter's series reactance is not.");
            if (xMin is double lo && xMax is double hi)
            {
                if (lo > hi)
                    throw new InvalidDataException(
                        $"CIM {c.Describe}: {cls}.xMin = {F(lo)} ohm is above xMax = {F(hi)} ohm; the reactance curve is contradictory.");
                double neutral = Neutral(c);
                if (hi > lo && step != neutral)
                    notes.Add($"{c.Describe}: its reactance varies with the tap (\"u\" curve, {cls}.xMin = {F(lo)} ohm at the " +
                              $"neutral step {F(neutral)} to xMax = {F(hi)} ohm at the end steps); the curve is not represented, " +
                              $"so at step {F(step)} the transformer's PowerTransformerEnd impedance is used and its series " +
                              "reactance may be understated.");
            }
        }

        (double Ratio, double Angle) PhaseTap(CimObject c)
        {
            double step = Step(c, "PhaseTapChanger");
            ReactanceCurve(c, c.Class == "PhaseTapChangerLinear" ? "PhaseTapChangerLinear" : "PhaseTapChangerNonLinear", step);
            switch (c.Class)
            {
                case "PhaseTapChangerLinear":
                    return (1.0, (step - Neutral(c)) * Required(c, "PhaseTapChangerLinear.stepPhaseShiftIncrement"));
                case "PhaseTapChangerSymmetrical":
                {
                    double du = (step - Neutral(c)) * Required(c, "PhaseTapChangerNonLinear.voltageStepIncrement") / 100.0;
                    return (1.0, 2.0 * DetMath.Atan(du / 2.0) * 180.0 / Math.PI);
                }
                case "PhaseTapChangerAsymmetrical":
                {
                    double du = (step - Neutral(c)) * Required(c, "PhaseTapChangerNonLinear.voltageStepIncrement") / 100.0;
                    double theta = Required(c, "PhaseTapChangerAsymmetrical.windingConnectionAngle") * Math.PI / 180.0;
                    double dx = 1.0 + du * DetMath.Cos(theta);
                    double dy = du * DetMath.Sin(theta);
                    return (Math.Sqrt(dx * dx + dy * dy), DetMath.Atan2(dy, dx) * 180.0 / Math.PI);
                }
                default: // PhaseTapChangerTabular
                {
                    string table = c.Resource("PhaseTapChangerTabular.PhaseTapChangerTable")
                        ?? throw new InvalidDataException($"CIM {c.Describe} names no PhaseTapChangerTable.");
                    return TablePoint(c, phasePoints, table, step, phase: true);
                }
            }
        }

        // ---- PowerTransformerEnds grouped by their parent PowerTransformer ------------------------------------
        // Every end is kept here, connected or not, so the winding numbering below is checked over all of them.
        var endsByTx = new Dictionary<string, List<EndRow>>(StringComparer.Ordinal);
        int endOrder = 0;
        foreach (CimObject e in objects)
        {
            if (e.Class != "PowerTransformerEnd") continue;
            string? tx = e.Resource("PowerTransformerEnd.PowerTransformer") ?? e.Resource("TransformerEnd.PowerTransformer");
            string? termRef = e.Resource("TransformerEnd.Terminal");
            if (tx is null) { notes.Add($"PowerTransformerEnd {e.Mrid} names no PowerTransformer; ignored."); continue; }
            string? node = null;
            if (termRef is null || !terminalNode.TryGetValue(termRef, out node))
                notes.Add($"PowerTransformerEnd {e.Mrid} of {tx} has no connected terminal; that winding is missing.");
            int? endNum = e.Integer("TransformerEnd.endNumber");
            if (endNum is <= 0)
                throw new InvalidDataException($"CIM {e.Describe}: TransformerEnd.endNumber = {endNum} is not a winding number (1, 2, 3).");
            if (!endsByTx.TryGetValue(tx, out List<EndRow>? list)) endsByTx[tx] = list = new();
            list.Add(new EndRow(endNum, endOrder++, e.Mrid, node,
                e.Number("PowerTransformerEnd.r") ?? 0.0,
                e.Number("PowerTransformerEnd.x") ?? 0.0,
                e.Number("PowerTransformerEnd.ratedU") ?? 0.0,
                // CIM2 (2026-10): the magnetising admittance (siemens at ratedU; absent = 0, as CGMES EQ makes b and g
                // required only where the transformer has one). They used to be ignored without a word.
                e.Number("PowerTransformerEnd.g") ?? 0.0,
                e.Number("PowerTransformerEnd.b") ?? 0.0));
        }

        var transformers = new List<CimTransformer>();
        foreach (CimObject t in objects)
        {
            if (t.Class != "PowerTransformer") continue;
            if (outOfService(t)) continue;
            List<EndRow> all = endsByTx.TryGetValue(t.Mrid, out List<EndRow>? found) ? found : new List<EndRow>();

            // D-DOC-106 (NUM, 2026-10): a defined winding order. An end with no endNumber used to take
            // "ends collected so far + 1", which can equal an explicit number on another end, and the unstable
            // List.Sort that followed then left two ends of equal number in an undefined order (which one is
            // the from side, and so which side the tap is referred to). Now:
            //  - every end numbered: the numbers must be distinct (a clash is refused);
            //  - no end numbered: document order, 1, 2, 3 (listed in the diagnostics);
            //  - some numbered and some not: refused (the order is ambiguous).
            // The order is then by number with a stable sort (OrderBy), so it is fully defined.
            int numbered = all.Count(r => r.Number is not null);
            List<EndRow> numberedEnds;
            if (numbered == 0)
            {
                numberedEnds = all.OrderBy(r => r.Order).Select((r, i) => r with { Number = i + 1 }).ToList();
                if (all.Count > 0)
                    notes.Add($"PowerTransformer {t.Name} ({t.Mrid}): no end has a TransformerEnd.endNumber, so the ends are " +
                              "numbered in document order.");
            }
            else if (numbered < all.Count)
            {
                throw new InvalidDataException(
                    $"CIM {t.Describe}: ends {string.Join(", ", all.Where(r => r.Number is not null).Select(r => r.EndMrid))} " +
                    $"carry a TransformerEnd.endNumber and {string.Join(", ", all.Where(r => r.Number is null).Select(r => r.EndMrid))} " +
                    "do not; the winding order is ambiguous.");
            }
            else
            {
                foreach (IGrouping<int?, EndRow> clash in all.GroupBy(r => r.Number).Where(g => g.Count() > 1))
                    throw new InvalidDataException(
                        $"CIM {t.Describe}: ends {string.Join(" and ", clash.Select(r => r.EndMrid))} both have " +
                        $"TransformerEnd.endNumber {clash.Key}; the winding order is ambiguous.");
                numberedEnds = all;
            }
            List<EndRow> ends = numberedEnds.Where(r => r.Node is not null).OrderBy(r => r.Number).ToList();

            if (ends.Count < 2)
            {
                notes.Add($"PowerTransformer {t.Name} ({t.Mrid}) has {ends.Count} connected winding(s); it needs " +
                          "at least two, so it is left out.");
                continue;
            }
            if (ends.Count > 3)
            {
                notes.Add($"PowerTransformer {t.Name} ({t.Mrid}) has {ends.Count} windings; only two- and three-winding " +
                          "transformers are represented, so it is left out.");
                continue;
            }

            if (ends.Count == 2)
            {
                // Two-winding: one tapped pi-branch with the tap / phase referred to the from side (ends[0]).
                // D-DOC-102: changers on BOTH ends used to keep only the first end's (a second ratio changer was not
                // read). Two ideal tapped windings in series give from-side ratio r1 / r2 and shift a1 - a2. The
                // one-sided cases keep their old expressions exactly (r1 alone; 1 / r2 alone; a1 alone; -a2 alone),
                // so every result without a changer of the same kind on each end is bit-for-bit what it was. (With
                // a ratio changer on one end and a linear phase changer on the other, r1 / r2 divides by exactly
                // 1.0 or divides 1.0 by r2, which is the old expression; the phase uses the phase flags only, so a
                // zero shift on end 2 alone is still -0.0, as before.)
                (double r1, double a1, bool t1, bool p1) = EndTap(ends[0].EndMrid);
                (double r2, double a2, bool t2, bool p2) = EndTap(ends[1].EndMrid);
                double tap = t1 && t2 ? r1 / r2 : t1 ? r1 : t2 ? 1.0 / r2 : 1.0;
                double phase = p1 && p2 ? a1 - a2 : p1 ? a1 : p2 ? -a2 : 0.0;
                var two = ends.Select(e => new CimTransformerEnd(e.Number!.Value, e.Node!, e.R, e.X, e.RatedU, G: e.G, B: e.B)).ToList();
                transformers.Add(new CimTransformer(t.Mrid, t.Name, two, tap, phase));
            }
            else
            {
                // Three-winding (R064): every winding kept, each with its own tap. In CGMES each end's r/x is that
                // winding's star-equivalent impedance, so the bridge models the unit as three branches meeting at
                // a star-point bus. (The tertiary used to be dropped without a note, leaving its node floating.)
                var three = ends.Select(e =>
                {
                    (double tap, double phase, _, _) = EndTap(e.EndMrid);
                    return new CimTransformerEnd(e.Number!.Value, e.Node!, e.R, e.X, e.RatedU, tap, phase, e.G, e.B);
                }).ToList();
                transformers.Add(new CimTransformer(t.Mrid, t.Name, three, 1.0, 0.0));
            }
        }
        return transformers;
    }

    /// <summary>One PowerTransformerEnd while the transformer's windings are gathered: its endNumber (null when the
    /// document gives none), document order, mRID, node (null when its terminal is not connected) and impedance.</summary>
    private sealed record EndRow(
        int? Number, int Order, string EndMrid, string? Node, double R, double X, double RatedU, double G = 0.0, double B = 0.0);

    /// <summary>
    /// An identifier as written in rdf:ID / rdf:about / rdf:resource, reduced to the bare mRID, and its XML-ID
    /// spelling. D-DOC-105 (NUM, 2026-10): ONE leading '#' (a same-document reference) and then ONE leading '_' (the
    /// CGMES 2.4 XML-ID prefix: an XML ID may not start with a digit, so "_" + UUID is written) are removed, or a
    /// 'urn:uuid:' prefix (CGMES 3, any letter case). It used to strip EVERY leading '#' and '_', so "__a", "_a" and
    /// "a" all became "a" and different objects merged without a word; now "__a" is the mRID "_a", and two spellings
    /// that still meet ("a" and "_a") are refused by <see cref="IdTable"/>. The spelling is null for urn:uuid:, which
    /// is the mRID itself.
    /// </summary>
    private static (string Mrid, string? Spelling) Identify(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase))
        {
            // GR-639 (October 2026): a UUID is case-insensitive (RFC 9562 s.4: compare as hex digits; write lower case),
            // so the same UUID written in upper case in one profile and lower case in another is ONE object. It used to
            // keep its letter case, so the two spellings did not merge. A well-formed UUID is folded to lower case; any
            // other urn:uuid: value keeps its spelling (it is not a UUID, so no case rule applies to it).
            string id = s["urn:uuid:".Length..];
            return (System.Guid.TryParseExact(id, "D", out _) ? id.ToLowerInvariant() : id, null);
        }
        if (s.StartsWith('#')) s = s[1..];
        return (s.StartsWith('_') ? s[1..] : s, s);
    }

    /// <summary>A number in a message: shortest round-trip form, invariant culture.</summary>
    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// N287 (CIM2, 2026-10): the value of an enumeration as written in either form (see <see cref="CimObject.RawEnum"/>):
    /// the part after the last '.' or '#' ("...cim16#WindGenUnitKind.offshore", "WindGenUnitKind.offshore" and
    /// "offshore" all give "offshore"); null for null.
    /// </summary>
    private static string? EnumLeaf(string? raw)
    {
        if (raw is null) return null;
        int cut = Math.Max(raw.LastIndexOf('.'), raw.LastIndexOf('#'));
        return raw[(cut + 1)..];
    }
}
