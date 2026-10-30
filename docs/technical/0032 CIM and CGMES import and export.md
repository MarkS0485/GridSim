# 32. CIM/CGMES import and export

This chapter specifies GridSim's CIM (IEC 61970) and CGMES exchange code in `src/GridSim.Core/IO/Cim/`: `CimImporter.Parse` reads one RDF/XML document, or a CGMES model held as one document per profile, into a `CimNetwork`; `CimGridModelBridge.ToGridModel` (and `Bridge`, which also reports where each CIM bus went) turns that `CimNetwork` into a solvable `GridModel`; `CimControlledPowerFlow` solves a bridged network with the CIM voltage controls a PV / PQ bus cannot express; and `CimExporter.Export` writes a `CimNetwork` back out as RDF/XML. It covers the classes and attributes that are read and written, how the descriptions of one mRID are merged, the topology processor, the per-unit conversions, transformers and tap changers, shunts and injections, the slack rule, de-energised islands, the controlled solve, what is refused and what is listed.

The subset is steady-state and balanced. The design record, which also describes a larger target than the code implements, is [design/cim-cgmes-mapping.md](design/cim-cgmes-mapping.md). Where the record and this chapter differ, this chapter describes the code.

## How a user reaches it

CIM is a library API only. No `gridsim` verb reads or writes CIM: `gridsim convert` converts between MATPOWER and the JSON case formats ([`convert` in the CLI reference](<../manual/0019 CLI reference.md#convert>)). The case editor and the desktop and web front ends have no CIM entry. A .NET program that references `GridSim.Core` reaches the code like this:

```csharp
CimNetwork net = CimImporter.Parse(new[] { eq, tp, ssh, sv });        // one string per profile document
foreach (string note in net.Notes) Console.WriteLine(note);            // everything left out or decided
var notes = new List<string>();
CimBridgeResult b = CimGridModelBridge.Bridge(net, baseMva: 100.0, notes: notes);
PowerFlowResult r = new CimControlledPowerFlow(engine, b).Solve(b.Model);  // engine: any IPowerFlowSolver
string xml = CimExporter.Export(net);                                  // CimNetwork in, RDF/XML out
```

There is no `GridModel` to `CimNetwork` converter, so a solved or edited `GridModel` cannot be exported to CIM. The extension surface is listed in [ch. 29, Extending GridSim](<../manual/0029 Extending GridSim.md>).

## Model

### Documents and options

`CimImporter.Parse(string rdfXml, CimImportOptions? options = null)` takes one document; `Parse(IEnumerable<string> documents, options)` takes a CGMES model held as several documents (EQ, TP, SSH, SV, DY, and the boundary set's EQ_BD and TP_BD), merged by mRID exactly as within one document, in the order given. Diagram (DL) and geographical (GL) documents may be passed; they add nothing. No documents, or a null one, is an `ArgumentException`; malformed XML throws `XmlException`. Elements and attributes are matched by local name only, so the `cim` namespace (cim14, cim16, cim100) does not matter.

`CimImportOptions` (default `CimImportOptions.Default`):

| Option | Values | Meaning |
|---|---|---|
| `Topology` | `CimTopology.Auto` (default), `CimTopology.TopologicalNodes`, `CimTopology.Switches` | `Auto`: the TopologicalNodes when the document has any, checked against the switch states when it also has ConnectivityNodes; otherwise the ConnectivityNodes joined across closed switches. `TopologicalNodes` (the bus-branch view): the TopologicalNodes (a document without any is refused). `Switches` (the node-breaker view): the ConnectivityNodes joined across closed switches even when TopologicalNodes exist (they are then only compared), for a case whose switch states (SSH) are newer than its topology (TP); a document without ConnectivityNodes is refused. |
| `TapPositions` | `SteadyStateHypothesis` (default), `StateVariables` | Where a tap position is read when both are present: the SSH `TapChanger.step` (the starting position of the case; the SV `SvTapStep.position` only when there is no SSH step), or the SV position (to reproduce the published solved state). A difference is listed. |
| `ShuntSections` | as `TapPositions` | The same choice for a shunt compensator's sections in service. |

Every present attribute must parse completely (invariant culture, `NumberStyles.Float`, finite; integers with no decimal point; booleans `true`, `false`, `1`, `0`, with `true` and `false` accepted in any letter case although xsd:boolean is lower case): a decimal comma, a unit suffix, an empty element or `NaN` is refused (`InvalidDataException` naming the class, name, mRID, attribute and text). An absent attribute keeps its documented default. A `BaseVoltage.nominalVoltage` that is missing or not above 0 is refused. Enumerations are read in either form, an `rdf:resource` URI or element text (`RegulatingControlModeKind.voltage` or `voltage`).

### Identifiers and merging

An identifier is reduced to its mRID by removing a `urn:uuid:` prefix (CGMES 3), or one leading `#` (a reference) and then one leading `_` (the CGMES 2.4 XML-ID prefix): `_n1`, `#_n1` and `urn:uuid:n1` are `n1`, and `__n1` is `_n1`. An `IdTable` records the spelling that defined each mRID: two different spellings that reduce to one mRID (`a` and `_a`), or a reference spelled differently from its object (`#l1` to `_l1`), are refused naming both.

CGMES splits one object across profiles. The defining profile writes `rdf:ID`; the others describe the same object with `rdf:about`. `MergeByMrid` walks every element in document order: the first description creates the object, later ones add only the properties it does not have yet (the first value of each property wins), and the class comes from the first description that is not a bare `rdf:Description`. Within one document only one description may define an object with `rdf:ID` (an RDF/XML identifier is unique within its document); a second definition in the same document is refused, naming both elements. A second `rdf:ID` definition of the same mRID in another document of the set is merged like any other description (the first value of each property winning in document order), and a note names both documents.

### Node views and the topology processor

In the bus-branch view the TopologicalNodes are the buses, and a terminal that names only a ConnectivityNode is mapped through `ConnectivityNode.TopologicalNode`; the ConnectivityNodes are not also buses. A diagnostic line counts the ConnectivityNodes that map onto a TopologicalNode of the document, and separate lines list the ones that name none and the ones that name a TopologicalNode not in the document (a boundary).

In the node-breaker view the topology processor (`CimImporter.Topology.cs`) joins the ConnectivityNodes across conducting switches, the CGMES definition of a TopologicalNode:

- A switch (`Switch` and every subclass: `Breaker`, `Disconnector`, `LoadBreakSwitch`, `Fuse`, `Jumper`, ...) conducts when it is closed (SSH `Switch.open` = false, else EQ `Switch.normalOpen` = false), in service, has exactly two terminals and both are connected. A switch with no state at all is refused.
- A closed **retained** switch (`Switch.retained` = true) does not merge: by definition it is kept in the bus-branch model, so its two sides stay two buses and it is listed in `CimNetwork.ClosedSwitches`. A `GroundDisconnector` never joins two nodes; a closed one is listed.
- The joining is a union-find that always keeps the smaller (ordinal mRID) node as the root. A bus takes its root's mRID, the name of the smallest-mRID `BusbarSection` on it (else the root's name), and the buses are ordered by mRID, so neither file order nor dictionary order shows in the result.
- A bus's base voltage is the one its ConnectivityNodes share through their `VoltageLevel` (directly or through a `Bay`); two different ones joined by a closed switch are refused.
- When the document also has TopologicalNodes, every disagreement is listed by name: a bus spanning two TopologicalNodes, a TopologicalNode split over two buses. In the bus-branch view a closed non-retained switch that spans two TopologicalNodes is also listed in `ClosedSwitches`.

On the ENTSO-E CGMES 2.4.15 conformity models the node-breaker MiniGrid (103 ConnectivityNodes, 90 switches) gives 13 buses, one to one with its 13 TopologicalNodes, and the SmallGrid (1,225 and 1,107) gives 118, one to one with its 118.

A bus is `CimBus(MRID, Name, BaseKv)`. `BaseKv` is the node's `BaseVoltage.nominalVoltage` (kV), directly or through its `VoltageLevel`; 0 means unspecified.

### Terminals and in-service state

Every `Terminal` is resolved to a node in the chosen view. The terminals of one piece of equipment are ordered by `ACDCTerminal.sequenceNumber` (an integer), then document order. `ACDCTerminal.connected` (or `Terminal.connected`) = false means the terminal is not connected: a line with an open end is left out, a transformer winding on it is missing (a three-winding unit with an open winding becomes the two-winding series of the other two), and a load or machine is left out; each is listed. `Equipment.inService` (SSH), else `Equipment.normallyInService` (EQ), = false leaves the equipment out, listed. Absent means connected and in service.

### Equipment read

| CIM class | Becomes | Notes |
|---|---|---|
| `ACLineSegment` | `CimBranch(R, X, B, G)`, class `ACLineSegment` | `r`, `x` (ohm), `bch`, `gch` (siemens), `ConductingEquipment.BaseVoltage` into `CimBranch.BaseKv`; first terminal in sequence order is the from node |
| `SeriesCompensator` | `CimBranch`, class `SeriesCompensator` | `r`, `x`, no charging |
| `EnergyConsumer`, `ConformLoad`, `NonConformLoad`, `StationSupply` | `CimLoad(P, Q)` with its class | `EnergyConsumer.p`, `.q` (load sign) |
| `AsynchronousMachine` | `CimLoad`, class `AsynchronousMachine` | a fixed P / Q load (`RotatingMachine.p`, `.q`) |
| `SynchronousMachine` | `CimGenerator` | below |
| `ExternalNetworkInjection`, `EquivalentInjection`, `PowerElectronicsConnection` | `CimGenerator` with its class | below |
| `LinearShuntCompensator`, `NonlinearShuntCompensator`, `EquivalentShunt` | `CimShunt(G, B)` | below |
| `StaticVarCompensator` | `CimStaticVarCompensator` | ratings, slope, SSH `q`, voltage control |
| `PowerTransformer`, `PowerTransformerEnd` | `CimTransformer`, `CimTransformerEnd` | below |
| `RatioTapChanger`, `PhaseTapChangerLinear`, `PhaseTapChangerSymmetrical`, `PhaseTapChangerAsymmetrical`, `PhaseTapChangerTabular` | the tap and shift of the end they regulate | below |
| `Switch` and subclasses | the topology processor; closed retained switches in `ClosedSwitches` | |
| `BaseVoltage`, `VoltageLevel`, `Bay`, `TopologicalNode`, `ConnectivityNode`, `BusbarSection`, `Junction` | node data | `BusbarSection` and `Junction` are node markers |

Conducting equipment of any other class named by a terminal is listed in the diagnostics; nothing is dropped without a line. An abstract class that cannot be mapped (`ShuntCompensator` in a CIM14-style document, `PhaseTapChanger`, `PhaseTapChangerNonLinear`, an abstract `PowerElectronicsUnit`) is refused by name.

### Generators

`RotatingMachine.p` and `.q` are in the load sign convention (positive is flow into the machine), so the importer computes `P = 0.0 - p`, `Q = 0.0 - q` (a zero reads `+0.0`); the exporter negates back. A `SynchronousMachine` also carries `RotatingMachine.ratedS`, `SynchronousMachine.minQ` / `maxQ`, its `GeneratingUnit`'s `minOperatingP` / `maxOperatingP`, `SynchronousMachine.referencePriority`, and the inertia `H` from the dynamics profile (`RotatingMachineDynamics.inertia` of the model naming the machine). Refused: `minQ > maxQ`, `minP > maxP`, `ratedS <= 0`, a negative priority or `H`, two dynamics models giving one machine an `H`, an `H` with no `ratedS`. A machine's `InitialReactiveCapabilityCurve` (and an `EquivalentInjection`'s `ReactiveCapabilityCurve`) is evaluated at its SSH active power and replaces `minQ` / `maxQ` (noted): each `CurveData` point is P with the minimum and maximum Q, `curveStyle` `constantYValue` steps and `straightLineYValues` (or none) interpolates, and outside the points the end values are held; a multiplier other than M, a point with y1 above y2, two points at one P, an empty curve or another style is refused. The slack machine's curve is evaluated at its set-point, not its solved P.

**Voltage control.** `RegulatingCondEq.controlEnabled` = false means the machine does not regulate. Otherwise its `RegulatingControl` decides: a voltage-mode, enabled control at the machine's own node gives the set-point `targetValue` (kV; a unit multiplier other than `k`, or a target not above 0, is refused). A disabled or non-voltage control, or no target, leaves it held at its SSH `q`. Remote control (the control's terminal on another bus) keeps the regulated bus and target (`CimGenerator.RegulatedNode`, `RemoteTargetKv`) but leaves the machine held at its `q` in a plain solve; `CimControlledPowerFlow` regulates the remote bus. A machine whose document says nothing is read as regulating.

**Plant type.** The class of the machine's `GeneratingUnit` sets `CimGenerator.Type`: `WindGeneratingUnit` gives `wind-onshore`, `wind-offshore` or `wind` (by `windGenUnitType`; another value is refused); `SolarGeneratingUnit` `solar`; `HydroGeneratingUnit` `hydro`, or `pumped-storage` when its `energyConversionCapability` is `pumpAndGenerator`; `NuclearGeneratingUnit` `nuclear`; `ThermalGeneratingUnit` its single `FossilFuel` (`gas`; `coal` or `hardCoal` as `coal`; `lignite` or `brownCoalLignite` as `lignite`; `oil` or `oilShale` as `oil`), else `thermal` (listed when its fuels name no single one); a plain `GeneratingUnit` or none leaves it empty. The bridge copies it to `Generator.Type`, so a wind or solar unit modelled as a `SynchronousMachine` is converter-interfaced plant to the fault studies.

**Injections.** An `ExternalNetworkInjection` takes `minP` / `maxP`, `minQ` / `maxQ` and `referencePriority` as written and regulates through its `RegulatingControl`; without one it does not regulate. An `EquivalentInjection` regulates only when `regulationCapability` (EQ) and `regulationStatus` (SSH) are true, at `regulationTarget` (kV); its `r` / `x` are not used. A CGMES 3.0 `PowerElectronicsConnection` takes `minQ` / `maxQ` and `ratedS` from the connection, `minP` / `maxP` as the sums of its units' (when every unit gives them), type `solar` (`PhotoVoltaicUnit`), `wind` (`PowerElectronicsWindUnit`) or `battery` (`BatteryUnit`) when its units are of one kind (else empty, listed), and is always converter-interfaced plant; it regulates as an `ExternalNetworkInjection`. All three negate `p` and `q` as a machine does.

### Shunts and static var compensators

A shunt's sections in service come from the SSH `ShuntCompensator.sections`, else the SV `SvShuntCompensatorSections.sections` (or the SV value always, by option), else the EQ `normalSections` (listed); with none of the three it is refused, and negative sections or more than `maximumSections` are refused. A linear shunt gives `B = bPerSection x sections`, `G = gPerSection x sections` (non-integer sections allowed); a non-linear one the sum of its `NonlinearShuntCompensatorPoint` `b` and `g` for points 1 up to the sections in service (whole sections only; a missing or duplicate point is refused); an `EquivalentShunt` its `g` and `b`. A shunt with no `bPerSection` (or no point) has no admittance and is left out, listed. Zero-sequence `b0` / `g0` are listed as not represented. A linear shunt under voltage control (an enabled voltage `RegulatingControl` with a target) keeps its control (`CimShuntControl`: target, deadband, regulated bus, `discrete` flag, sections, per-section admittance); a non-linear shunt's control is listed and not kept.

A `StaticVarCompensator` keeps its `capacitiveRating` and `inductiveRating` (ohm), `slope` (kV per MVAr), its SSH `q` (load sign) and its voltage control (an enabled voltage-mode `RegulatingControl` with a target, or the CGMES 2.4.15 `sVCControlMode` voltage with a `voltageSetPoint`).

### Transformers and tap changers

`ParseTransformers` groups the `PowerTransformerEnd` objects by parent transformer. An end with no parent, or with no connected terminal, is listed. `TransformerEnd.endNumber` orders the ends: when every end is numbered the numbers must be distinct and above 0; when none is, document order gives 1, 2, 3 (listed); a mix is refused. The order is a stable sort on unique numbers, so which side is "from" is always defined. A transformer with fewer than two or more than three connected windings is left out, listed. Each end carries `r`, `x` (ohm on its own `ratedU`, kV), and its magnetising `g` and `b` (siemens).

A tap changer names its end through `TapChanger.TransformerEnd`. Its position `step` is the SSH `TapChanger.step` (or SV by option), else the EQ `normalStep` (listed); with neither it is refused, and a step outside `lowStep..highStep` is refused. With `n = step - neutralStep`:

```
RatioTapChanger:              tap   = 1 + n * stepVoltageIncrement / 100           (tabular: the table point's ratio)
PhaseTapChangerLinear:        phase = n * stepPhaseShiftIncrement                  (degrees)
PhaseTapChangerSymmetrical:   du = n * voltageStepIncrement / 100;  phase = 2 atan(du / 2);  ratio 1
PhaseTapChangerAsymmetrical:  theta = windingConnectionAngle;  dx = 1 + du cos(theta);  dy = du sin(theta)
                              phase = atan2(dy, dx);  ratio = sqrt(dx^2 + dy^2)
PhaseTapChangerTabular:       the table point of the current step gives the phase and the ratio
```

The trigonometry uses GridSim's own `DetMath` functions, so the result is the same on every CPU; at `theta = 0` the asymmetrical changer equals a ratio changer bit for bit. Refused by name: a missing increment, connection angle or neutral step; a tabular step with no table point or a non-integer step; a non-zero per-step impedance correction (`TapChangerTablePoint.r`, `x`, `g`, `b`); two changers of one kind on one end; a non-positive resulting ratio. A phase changer's `xMin` / `xMax` reactance curve is checked and, where it matters, listed as not represented. The formulae are taken from the class descriptions of CGMES 2.4.15 EQ and are marked `[VERIFY]` in the source (IEC 61970-301 is not on disk).

**Two windings.** One `CimTransformer` with a single tap and phase referred to the from side: changers on both ends combine as two ideal tapped windings in series, `tap = r1 / r2` and `phase = a1 - a2`; one end alone gives `r1` or `1 / r2`, and `a1` or `-a2`. **Three windings.** Each winding keeps its own tap and phase on its `CimTransformerEnd`; the transformer's own `TapRatio` is 1 and `PhaseShiftDeg` 0.

### The bridge to GridModel

`CimGridModelBridge.ToGridModel(net, baseMva = 100.0, name = "cim-import", unbasedImpedancesArePerUnit = false, notes = null)` builds the `GridModel`; `Bridge(...)` builds the same model and returns a `CimBridgeResult` (the model, the network that was solved, `BusIdOfNode`, `DroppedNodes`, the switch-flow calculation and the controls). Every choice the bridge makes is written to `notes`.

**Buses and closed switches.** Bus ids follow `net.Buses`. The buses joined by each closed switch in `ClosedSwitches` are solved as one bus (zero impedance); the joined bus keeps the name and place of the first, and a line left with both ends on one joined bus is left out (noted). After a converged solve `CimBridgeResult.SwitchFlows` gives the flow through each such switch from the power balance of one side, when that is determined: not for a switch in a loop of closed switches (parallel zero-impedance paths), and not when one side holds a machine whose output the solve decided; `CimSwitchFlow.Basis` then says why.

**Per-unit.** `Zbase = kV^2 / baseMva`. A line uses its from node's base, else its to node's, else its own `ConductingEquipment.BaseVoltage`; each fallback is noted. Two end bases within `LineBaseRatioBound` (1.1) of each other are two names for one voltage: the line is per-unit on each end's own base, with `TapRatio = kV(to) / kV(from)` on the line (still not a transformer) and the impedance and charging on the to-bus base, which is exact. Further apart the line is refused (two voltage levels). Half of a line's `gch` goes to each end as bus shunt conductance. A winding's ohms use its own `ratedU`, else its node's base. With no base anywhere a non-zero impedance is refused, unless the caller passes `unbasedImpedancesArePerUnit: true`, an explicit declaration that the values are already per-unit; a zero impedance needs no base.

**Transformers.** A two-winding transformer is one branch from end 1's node to end 2's node with `R = Pu(R1) + Pu(R2)`, `X = Pu(X1) + Pu(X2)` (per-unit impedance is invariant across the ideal ratio). When a winding's `ratedU` differs from its bus base, the nominal turns ratio is off-nominal on the bus bases: the tap becomes `tap x n1 / n2` (`n = ratedU / bus base`) with the impedance referred to the to-bus base. A three-winding transformer is a star-point bus (id after the CIM buses, `Pq`, no load, base = winding 1's rating, named `<transformer> star point`) and one branch per winding (`<transformer>/w<endNumber>`) carrying that winding's own tap (`tap x n_k`); the star point starts at `1 / tap` of winding 1. The magnetising `b` is the branch's shunt susceptance (`b x ratedU^2 / baseMva`, half at each end) and `g` is end-bus shunt conductance placed where each half of `b` goes. A transformer whose ratings equal its bus bases and whose `b` and `g` are zero bridges with the plain formulas above.

**Loads, shunts and injections.** A bus's `Pd`, `Qd` are the sums of its loads. Each `CimShunt` adds `G x kV^2` MW and `B x kV^2` MVAr (at 1.0 pu of its bus base) to the bus's `Gs`, `Bs`. A regulating SVC is held at its SSH `q` (added to `Qd`) in the plain model.

**Generators.** Each `CimGenerator` keeps `Pg`, `Qg`, `Vg = targetKv / bus base kV` (a target with no bus base is refused), `Qmin` / `Qmax`, `Pmin` / `Pmax`, `MBase` (`ratedS`), `H` and `Type`; a `PowerElectronicsConnection` sets `ConverterInterfaced`. A bus with a voltage-controlling machine is `Pv`; a bus whose machines all have control off is `Pq` with their `Q` injected; two different targets at one bus are refused; a controlled bus with no target is held at 1.0 pu (noted).

**Slack.** Chosen from the voltage-controlling machines (all machines when none controls), by: (1) the lowest positive `referencePriority` (1 is the highest priority, 0 no preference); (2) the largest unit, by `maxOperatingP`, else `ratedS`; (3) the lowest bus number. The choice and the rule that decided it are noted. A network with no generator has bus 1 as the slack.

**Islands.** An island (buses joined by lines, transformers and closed switches) that holds neither the slack nor a voltage-controlling machine is de-energised: it is left out of the `GridModel`, its buses named in one note and listed in `CimBridgeResult.DroppedNodes`. An energised island without the slack gets its own slack, elected by the same rule within the island. A network with no dead island is bridged as the same object.

**Refusals.** A load, generator, line, transformer winding or shunt that names a node that is not a bus throws `InvalidDataException` naming it; a transformer with a winding count other than two or three throws (only a hand-built `CimNetwork` can have one).

### The controlled solve

`CimControlledPowerFlow` (`CimControlledPowerFlow.cs`) solves a bridged network with the controls GridSim's PV / PQ bus cannot express, as an outer loop around an unchanged engine:

- **Remote voltage control** (`CimBridgeResult.RemoteVoltageControls`): the machines' buses are made `Pv` and their set-points chosen so the remote bus is at its target. When the machines' reactive limits bind, the remote bus cannot be held and the control is reported at its limit. A control shared by machines at several buses shares the reactive output in proportion to each bus's machines' reactive range `maxQ - minQ` (equal shares, said, when a range is not finite and positive).
- **Susceptance controls** (`SusceptanceControls`: regulating SVCs, and linear shunts under continuous control): a susceptance `B` between its limits with `V_regulated + slope x B x V_bus^2 / baseMva = target`; at a limit it stays a fixed susceptance.
- **Switched shunts** (`ShuntRegulations`): by `CimControlOptions.Shunts` (`CimShuntControlMode`): as the data asks (whole sections through `SwitchedShuntPowerFlow` when `discrete` is true or absent, continuous when false), all discrete, all continuous, or all held.

A regulated bus already held by a machine's own control is not regulated a second time, and a machine bus that holds its own voltage cannot follow a remote target (both said, the device held at its `q`); two machines at one bus regulating two buses, or one bus regulated to two targets, are refused. The control variables are found by a damped Newton iteration with finite-difference sensitivities from whole engine solves, updated by Broyden's rank-one rule; a variable at a limit that wants to go further is held. It ends when every free control is within `CimControlOptions.TolerancePu` (default `1e-8` pu; reactive sharing within `1e-6` pu on the system base) or fails loud: the cap (`MaxSolves`, default 200 solves per loop) reached, singular sensitivities, an engine solve that does not converge, or a machine held at a limit on the wrong side give a result with `Converged = false` and `LastNote` saying why. With no control it is one engine solve of the model. Every solve is an ordinary engine solve, so dense and sparse engines give the same bits.

### The exporter

`CimExporter.Export(net)` returns one RDF/XML string in the `http://iec.ch/TC57/2013/CIM-schema-cim16#` namespace; identifiers are `_` plus the mRID and references `#_` plus the mRID; numbers use the round-trip format. It writes one `BaseVoltage` per distinct positive bus base, one `TopologicalNode` per bus, each line under its class (`ACLineSegment` with `r`, `x`, `bch`, `gch` and its own base voltage, or `SeriesCompensator`), each load under its class (an `AsynchronousMachine` with `RotatingMachine.p` / `q`), each generator under its class with `p` / `q` negated back to the load convention and, when present, `ratedS`, `minQ` / `maxQ`, `referencePriority`, `controlEnabled`, a voltage `RegulatingControl` (a remote one names a terminal written on the regulated bus), a `GeneratingUnit` of the subclass its type came from (with its `FossilFuel`), and its inertia as a `SynchronousMachineSimplified` dynamics model; injections in their class's own terms (a `PowerElectronicsConnection` with a unit of its kind); shunts as one section holding their whole admittance; closed switches of their class with the `retained` flag; and each transformer with its ends and tap changers. Each optional value is written only when present. Tap changers are written as the inverse of the importer's algebra with `neutralStep = 0` and an increment of 1:

```
RatioTapChanger:          step = 100 * (tap - 1)      neutralStep = 0    stepVoltageIncrement = 1
PhaseTapChangerLinear:    step = phase (degrees)      neutralStep = 0    stepPhaseShiftIncrement = 1
```

A two-winding transformer writes its changers on end 1 only when `|TapRatio - 1| > 1e-12` and `|PhaseShiftDeg| > 1e-12` respectively; a three-winding transformer writes one of each per winding that passes the same test. Re-import computes `1 + step / 100`, which reproduces the tap to rounding, not always to the bit. The output is one EQ-style document with the topology inline; it writes no `ACDCTerminal.sequenceNumber` (the importer recovers terminal order from document order, which the exporter keeps) and no diagnostics. A network that would define one `rdf:ID` twice (two objects sharing an mRID, or an identifier the exporter makes up, such as `bv_<kV>`, `t_<mRID>_1` or `rc_<mRID>`, equal to another object's mRID) is refused with an `InvalidOperationException` naming the identifier and both element classes, since the importer could not read the document back.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Base power | 100.0 | MVA | per-unit base of the bridge; a parameter of the call | `CimGridModelBridge.ToGridModel(baseMva)` |
| Model name | `cim-import` | | `GridModel.Name` unless the caller passes one | `ToGridModel(name)` |
| Unspecified base voltage | 0 | kV | a node with no resolvable `BaseVoltage` | `CimBus.BaseKv` |
| Unbased impedances | refused | | `unbasedImpedancesArePerUnit: true` declares them per-unit | `ToGridModel(unbasedImpedancesArePerUnit)` |
| Line base ratio bound | 1.1 | ratio | two end bases closer than this are one voltage | `CimGridModelBridge.LineBaseRatioBound` |
| Tap default | 1.0 | ratio | winding or transformer with no ratio changer | `CimTransformerEnd.TapRatio`, `CimTransformer.TapRatio` |
| Phase default | 0 | degrees | no phase changer | `PhaseShiftDeg` |
| Tap position | SSH step, else SV, else EQ `normalStep` | | by `CimImportOptions.TapPositions` | `CimTapPositions` |
| Shunt sections | SSH, else SV, else EQ `normalSections` | | by `CimImportOptions.ShuntSections` | `CimTapPositions` |
| Controlled-solve tolerance | `1e-8` | pu | control law | `CimControlOptions.TolerancePu` |
| Controlled-solve cap | 200 | solves | per control loop or switching pass | `CimControlOptions.MaxSolves` |
| Reactive sharing tolerance | `1e-6` | pu on the system base | shared remote control | `CimControlledPowerFlow` |
| Supported windings | 2 or 3 | | other counts are dropped with a diagnostic | `ParseTransformers` |
| Export tap threshold | 1e-12 | ratio or degrees | smaller offsets write no tap changer | `CimExporter.Export` |
| Export namespace | `http://iec.ch/TC57/2013/CIM-schema-cim16#` | | CIM16 | `CimExporter` |

## Invariants

Each invariant is pinned by the test named. Tests are in `tests/GridSim.Tests/`.

| Invariant | Test |
|---|---|
| Nodes, a line with its r, x and bch, loads and generators resolve to their nodes through terminals | `CimImporterTests.Parses_nodes_and_line_segments_with_connectivity`, `Parses_loads_and_generators_with_their_nodes` |
| A machine written in the load convention (`p = -40`, `q = -12.5`) imports as generator-positive 40 and 12.5 | `CimImporterTests.Parses_loads_and_generators_with_their_nodes` |
| EQ `rdf:ID` and TP and SSH `rdf:about` descriptions of one mRID merge; `urn:uuid:` identifiers match across descriptions | `CimMultiProfileTests.A_merged_eq_tp_ssh_document_resolves_lines_loads_and_generators`, `Cgmes3_urn_uuid_identifiers_match_across_profiles` |
| ConnectivityNodes map onto TopologicalNodes and are not extra buses | `CimMultiProfileTests.Connectivity_nodes_map_onto_topological_nodes_instead_of_becoming_orphan_buses` |
| A three-winding transformer keeps all three ends; the bridge gives a star point and it solves; it survives export and re-import | `CimMultiProfileTests.A_three_winding_transformer_keeps_its_tertiary`, `The_bridge_models_a_three_winding_unit_as_a_star_point_and_it_solves`, `A_three_winding_unit_round_trips_through_the_exporter` |
| Everything the importer drops is listed; the bridge fails loud on an unresolved node | `CimMultiProfileTests.Everything_left_out_is_listed_in_the_diagnostics`, `The_bridge_fails_loud_on_an_unresolved_node` |
| A two-winding transformer imports with its tap and bridges to a tapped branch that solves | `CimTransformerImportTests.Parses_a_two_winding_transformer_with_its_tap`, `Bridges_the_transformer_to_a_tapped_branch_that_solves` |
| Ohm values convert on the node base; a network declared per-unit bridges and solves | `CimGridModelBridgeTests.Ohm_impedances_are_per_unitised_against_base_voltage`, `Bridges_to_a_valid_solvable_grid_model` |
| Load subclasses are read; equipment of an unread class is listed; an unparsable number, a bad boolean or a non-integer sequence number is refused; a base voltage needs a positive nominal voltage; the from-bus base is exact and the fallbacks are noted; tap changers of every class, on one or both ends, follow their formulas; disconnected and out-of-service equipment is left out and listed; generator data and the slack rule; identifier spellings; winding numbering; the mapped-node count | `CimDocStream10Tests` (`D099_*` to `D106_*`, `D164_*`) |
| The topology processor joins nodes across closed switches, keeps retained switches as two buses, orders deterministically, refuses a switch with no state, and lists disagreements with the TopologicalNodes; generator types and SV tap positions | `CimTopologyProcessorTests` |
| Shunts, injections, power-electronics connections and asynchronous machines are read and exported; dead islands are left out and switch flows are derived; transformer rated ratios and magnetising admittance | `CimEquipmentTests`, `CimIslandTests` |
| A line between two base voltages is exact as a tap; SVCs, series compensators, remote control and regulating shunts are solved by the controlled solve; each energised island gets a slack | `CimRemainingGapsTests` |
| Import, export, import preserves the network; export then import keeps loads, generator output, base voltages and a transformer tap | `CimImporterTests.Import_export_round_trip_is_information_preserving`, `Wave6IoTests.CimExporter_round_trips_loads_generators_base_voltages_and_transformers` |
| Two generators on one node leave exactly one slack bus | `Wave6IoTests.CimGridModelBridge_keeps_the_slack_when_two_units_share_a_node` |

`CimConformityBenchmarkTests` reproduce the published solved state of the ENTSO-E CGMES conformity models when the models are on disk; they are opt-in and skip otherwise.

## Worked example

The scratch program `results/cim/Program.cs` (a console project that references the built `GridSim.Core.dll`; it lives under `results/` and is not committed) parses a document, prints the `CimNetwork`, bridges it, solves it with `NewtonRaphsonPowerFlow`, exports the network and parses the export again. The three documents are the EQ+TP+SSH fixture of `CimMultiProfileTests.MergedCgmes`, the two-winding fixture of `CimTransformerImportTests` and the three-winding fixture of `CimMultiProfileTests.ThreeWinding`, saved verbatim as `results/cim/merged.xml`, `two-winding.xml` and `three-winding.xml`. The output below was captured before the importer listed its node-view counts and the bridge wrote its notes, so the `notes` counts and the slack wording are not the current output; the numbers of the two transformer examples follow the formulas above.

### A merged EQ, TP and SSH document

`merged.xml` is the EQ description (a line, a load, a machine and their terminals, with no nodes), then the TP description (two topological nodes and each terminal's node, by `rdf:about`), then the SSH description (the load's p and q and the machine's `p = -13`, by `rdf:about`). Run: `dotnet run --project results/cim -c Release -- merged print`.

```
$ dotnet run --project results/cim -c Release -- merged print
parsed: 2 buses, 1 lines, 0 transformers, 1 loads, 1 generators, 0 notes
bus n1 name=Node1 baseKv=0
bus n2 name=Node2 baseKv=0
line l1 n1->n2 r=0.5 x=2.5 b=0.0001
load ld1 node=n2 p=12.5 q=3.2
gen g1 node=n1 p=13 q=0
model: baseMva=100 name=cim-import
  bus 1 Slack Pd=0 Qd=0 BaseKv=0 Name=Node1
  bus 2 Pq Pd=12.5 Qd=3.2 BaseKv=0 Name=Node2
  gen bus=1 Pg=13 Vg=1
  branch 1->2 R=0.5 X=2.5 B=0.0001 tap=1 shift=0 tx=False Line1
validate errors: 0
solve: status=Converged converged=True iterations=6 maxMismatch=1.076E-014
  bus 1 Vm=1.000000 Va=0.0000 deg
  bus 2 Vm=0.705489 Va=-24.8531 deg
```

Every attribute that sits in a different description in the source is joined: the line's two nodes come from the TP terminals, the load's 12.5 MW and 3.2 MVAr and the machine's output come from SSH. The machine's `p = -13` imports as `P = 13`. The model has two buses, `Node1` as the slack because it carries the only generator, and one branch. The document gives no `BaseVoltage`, so `BaseKv` is 0. The bridge now refuses a non-zero impedance with no base voltage anywhere unless the caller passes `unbasedImpedancesArePerUnit: true`; the model printed above is the one that call gives, the line's `r = 0.5`, `x = 2.5`, `b = 0.0001` passed to the branch as per-unit: a 2.5 pu reactance carrying 12.5 MW is a stressed network, which is why bus 2 solves at 0.705489 pu. The solve converges in 6 iterations.

The export of the same network, elided:

```
<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
...
  <cim:ACLineSegment rdf:ID="_l1">
    <cim:IdentifiedObject.name>Line1</cim:IdentifiedObject.name>
    <cim:ACLineSegment.r>0.5</cim:ACLineSegment.r>
    <cim:ACLineSegment.x>2.5</cim:ACLineSegment.x>
    <cim:ACLineSegment.bch>0.0001</cim:ACLineSegment.bch>
  </cim:ACLineSegment>
  <cim:Terminal rdf:ID="_t_l1_1">
    <cim:Terminal.TopologicalNode rdf:resource="#_n1" />
    <cim:Terminal.ConductingEquipment rdf:resource="#_l1" />
  </cim:Terminal>
...
  <cim:SynchronousMachine rdf:ID="_g1">
    <cim:IdentifiedObject.name>Gen1</cim:IdentifiedObject.name>
    <cim:RotatingMachine.p>-13</cim:RotatingMachine.p>
    <cim:RotatingMachine.q>0</cim:RotatingMachine.q>
  </cim:SynchronousMachine>
...
</rdf:RDF>
exported 1857 chars; re-imported: 2 buses, 1 lines, 0 transformers, 1 loads, 1 generators, 0 notes; counts equal: True
load ld1 p=12.5 q=3.2
gen g1 p=13 q=0
[exit 0]
```

The machine is written as `p = -13`, the load convention. The last lines of the output are the re-import: the same counts, no notes, and the load and generator values identical.

### A two-winding transformer

`two-winding.xml` has a 230 kV node, a 132 kV node and a transformer with `x = 52.9` ohm on end 1, `x = 0` on end 2 and a ratio changer on end 1 at step 12, neutral 10, 1.25 percent per step. Run: `dotnet run --project results/cim -c Release -- two-winding`.

```
$ dotnet run --project results/cim -c Release -- two-winding
parsed: 2 buses, 0 lines, 1 transformers, 1 loads, 1 generators, 0 notes
bus n1 name=HV baseKv=230
bus n2 name=LV baseKv=132
transformer tx1 name=GSP-T1 tap=1.025 phase=0
  end 1 node=n1 r=0 x=52.9 ratedU=230 tap=1 phase=0
  end 2 node=n2 r=0 x=0 ratedU=132 tap=1 phase=0
load ld1 node=n2 p=28 q=8
gen g1 node=n1 p=-30 q=0
model: baseMva=100 name=cim-import
  bus 1 Slack Pd=0 Qd=0 BaseKv=230 Name=HV
  bus 2 Pq Pd=28 Qd=8 BaseKv=132 Name=LV
  gen bus=1 Pg=-30 Vg=1
  branch 1->2 R=0 X=0.09999999999999999 B=0 tap=1.025 shift=0 tx=True GSP-T1
validate errors: 0
solve: status=Converged converged=True iterations=3 maxMismatch=8.597E-011
  bus 1 Vm=1.000000 Va=0.0000 deg
  bus 2 Vm=0.966906 Va=-1.7009 deg
exported 3283 chars; re-imported: 2 buses, 0 lines, 1 transformers, 1 loads, 1 generators, 0 notes; counts equal: True
load ld1 p=28 q=8
gen g1 p=-30 q=0
transformer tx1 tap=1.025
  end 1 node=n1 x=52.9 tap=1
  end 2 node=n2 x=0 tap=1
[exit 0]
```

The tap is `1 + (12 - 10) * 1.25 / 100 = 1.025`. The impedance is `52.9 / (230^2 / 100) + 0 / (132^2 / 100)`, printed as `0.09999999999999999` because the division rounds. `BaseKv` of each bus comes from its `BaseVoltage`. The fixture writes the machine as `p = 30.0`, so by the sign rule it imports as `P = -30`: a motor on the slack bus, and the solve still converges. The ratings equal the bus bases and the magnetising admittance is zero, so the rated-ratio and magnetising rules leave the branch as printed. The round trip returns the tap `1.025` and the same end nodes and impedances.

### A three-winding transformer

`three-winding.xml` is a 400/132/13 kV supergrid transformer with `x = 160`, `17.424` and `0.338` ohm on its three ends and a ratio changer on the 400 kV end at 1.025. Run: `dotnet run --project results/cim -c Release -- three-winding`.

```
$ dotnet run --project results/cim -c Release -- three-winding
parsed: 3 buses, 0 lines, 1 transformers, 2 loads, 1 generators, 0 notes
bus hv name=hv baseKv=400
bus lv name=lv baseKv=132
bus tv name=tv baseKv=13
transformer sgt name=SGT1 tap=1 phase=0
  end 1 node=hv r=0 x=160 ratedU=400 tap=1.025 phase=0
  end 2 node=lv r=0 x=17.424 ratedU=132 tap=1 phase=0
  end 3 node=tv r=0 x=0.338 ratedU=13 tap=1 phase=0
load l132 node=lv p=40 q=10
load l13 node=tv p=15 q=5
gen g node=hv p=-60 q=0
model: baseMva=100 name=cim-import
  bus 1 Slack Pd=0 Qd=0 BaseKv=400 Name=hv
  bus 2 Pq Pd=40 Qd=10 BaseKv=132 Name=lv
  bus 3 Pq Pd=15 Qd=5 BaseKv=13 Name=tv
  bus 4 Pq Pd=0 Qd=0 BaseKv=400 Name=SGT1 star point
  gen bus=1 Pg=-60 Vg=1
  branch 1->4 R=0 X=0.1 B=0 tap=1.025 shift=0 tx=True SGT1/w1
  branch 2->4 R=0 X=0.09999999999999999 B=0 tap=1 shift=0 tx=True SGT1/w2
  branch 3->4 R=0 X=0.2 B=0 tap=1 shift=0 tx=True SGT1/w3
validate errors: 0
solve: status=Converged converged=True iterations=4 maxMismatch=3.634E-015
  bus 1 Vm=1.000000 Va=0.0000 deg
  bus 2 Vm=0.944100 Va=-5.9230 deg
  bus 3 Vm=0.944517 Va=-5.2867 deg
  bus 4 Vm=0.955632 Va=-3.3820 deg
exported 4606 chars; re-imported: 3 buses, 0 lines, 1 transformers, 2 loads, 1 generators, 0 notes; counts equal: True
load l132 p=40 q=10
load l13 p=15 q=5
gen g p=-60 q=0
transformer sgt tap=1
  end 1 node=hv x=160 tap=1.025
  end 2 node=lv x=17.424 tap=1
  end 3 node=tv x=0.338 tap=1
[exit 0]
```

The fixture's machine is written as `p = 60`, so it imports as `Pg = -60`: a 60 MW motor on the slack bus, and the voltages below are for that case. The importer keeps all three windings, each with its own `ratedU`, and puts the 1.025 on end 1 only; the transformer's own tap is 1. The bridge adds bus 4, `SGT1 star point`, at 400 kV, and three branches `SGT1/w1`, `SGT1/w2`, `SGT1/w3` into it, with per-unit `0.1`, `0.09999999999999999` and `0.2`. The star point solves at 0.955632 pu between the 400 kV bus at 1.0 and the two loaded windings at 0.944100 and 0.944517 pu. After export and import the tap on end 1 is still 1.025 and the tertiary reactance is still `0.338`.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/IO/Cim/CimImporter.cs` | `CimNetwork`, `CimBus`, `CimBranch`, `CimLoad`, `CimGenerator`, `CimShunt`, `CimTransformer`, `CimTransformerEnd`, `CimImportOptions` | the records of a parsed network and the import options |
| | `CimImporter.Parse` | one document, or one per profile, to a `CimNetwork`: identifiers, merging, node view, terminals, lines, series compensators, loads, synchronous machines, transformers and tap changers |
| | `CimImporter.MergeByMrid`, `Identify`, `IdTable` (private) | the merge by mRID, first value wins; identifier reduction and spelling checks |
| `src/GridSim.Core/IO/Cim/CimImporter.Topology.cs` | topology processor | ConnectivityNodes joined across closed switches; retained and ground switches; comparison with the TopologicalNodes |
| `src/GridSim.Core/IO/Cim/CimImporter.Equipment.cs` | equipment | shunts and their sections, injections (energy sources, external network injections and power-electronics connections) with their voltage-control data and limits |
| `src/GridSim.Core/IO/Cim/CimImporter.Controls.cs` | controls | `RegulatingControl` targets and modes, remote and shunt control, reactive capability curves, static var compensators |
| `src/GridSim.Core/IO/Cim/CimGridModelBridge.cs` | `CimGridModelBridge.ToGridModel`, `Bridge`, `CimBridgeResult` | `CimNetwork` to `GridModel`: per-unit, closed switches, transformers, slack choice, star points; refuses unresolved nodes |
| `src/GridSim.Core/IO/Cim/CimGridModelBridge.Islands.cs` | islands | de-energised islands dropped, a slack per energised island, switch flows |
| `src/GridSim.Core/IO/Cim/CimGridModelBridge.Controls.cs` | controls | the remote voltage, susceptance and shunt controls handed to the controlled solve |
| `src/GridSim.Core/IO/Cim/CimControlledPowerFlow.cs` | `CimControlledPowerFlow`, `CimControlOptions`, `CimShuntControlMode` | the outer control loop around an engine |
| `src/GridSim.Core/IO/Cim/CimExporter.cs` | `CimExporter.Export` | `CimNetwork` to RDF/XML |
| `tests/GridSim.Tests/Cim*Tests.cs`, `Wave6IoTests.cs` | | the tests named under Invariants |
| `results/cim/` | scratch program | the worked example; not committed (`results/` is git-ignored). The XML inputs equal the test fixtures `MergedCgmes`, `CimTransformerImportTests.Rdf` and `CimMultiProfileTests.ThreeWinding`. |

## Limits

- No `gridsim` verb, no loader and no editor reads or writes CIM; the code is reachable only from a .NET program that references `GridSim.Core`.
- `CimExporter.Export` takes a `CimNetwork`; there is no `GridModel` to `CimNetwork` converter, so a solved or edited `GridModel` cannot be exported to CIM. The export is one EQ-style document, not a profile set.
- There is no `md:FullModel` header handling and no measurement import; the SV voltages and angles of a CGMES set are not read into the model (the SV tap and shunt positions are, by option).
- Operational limits (line and transformer ratings) are not read. A reactive capability curve gives the limits at the set-point P only, so the slack machine's limits do not follow its solved P.
- A tap changer with a non-zero per-step impedance correction is refused; a phase changer's `xMin` / `xMax` curve is not represented. The tap changer formulae are marked `[VERIFY]` against IEC 61970-301, which is not on disk.
- A transformer with four or more windings is dropped with a diagnostic and never reaches the model.
- Exported taps are `100 * (tap - 1)` percent steps, so a re-imported tap equals the original to rounding, not always to the bit.

## See also

- [design/cim-cgmes-mapping.md](design/cim-cgmes-mapping.md): the design record, including the measurement mapping and the guard design that the code does not implement.
- [ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>): the `GridModel`, `Bus`, `Branch` and `Generator` fields the bridge fills, and the per-unit stamp.
- [ch. 3, Power flow](<0003 Power flow.md>): the solver the worked example runs.
- [ch. 10, File and wire formats](<0010 File and wire formats.md>): the other import and export formats.
- [ch. 29, Extending GridSim](<../manual/0029 Extending GridSim.md>): the library surface and the extension points.
- [`convert` in the CLI reference](<../manual/0019 CLI reference.md#convert>): the format conversion that does exist, MATPOWER and JSON.
