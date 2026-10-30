# 2. Data model and per-unit conventions

This chapter specifies the in-memory network model that every solver and study reads, and the per-unit system its
numbers are expressed in. The whole model is defined in one file, `src/GridSim.Core/Model/Model.cs`: `GridModel`
and its element records `Bus`, `Generator`, `Branch`, `Oltc`, `Boundary`, `ZipLoad` and `GeneratorCost`. The chapter
gives every field with its unit, default, validation rule and the loaders that set it; the per-unit base system; the
branch pi-model with tap and phase shift exactly as `AdmittanceStamp` implements it; the three Y-bus builders and how
they stay bit-identical; the unit normalisation that rescues raw-unit distribution files; the immutability rules; and
the invariants with the tests that pin them. The solvers that consume the model are specified in
[ch. 3, Power flow](<0003 Power flow.md>), the file formats that fill it in
[ch. 10, File and wire formats](<0010 File and wire formats.md>), and the JSON v2 case schema in
[ch. 28, The v2 JSON schema](<0028 The v2 JSON schema.md>).

## Model

### Shape of the model

`GridModel` is a `sealed class` whose properties are `init`-only. Its elements are `sealed record`s. By convention no code
changes a built model (see "Immutability and the copy helpers"). There is no
separate load type: demand is a property of the bus (the MATPOWER convention), and shunts are properties of the bus.

```
GridModel
  BaseMva                       system MVA base (required)
  Buses        : Bus[]          required; file order is the matrix order
  Generators   : Generator[]    required; the IN-SERVICE fleet (see "The generator fleet")
  AllGenerators / OutOfServiceGenerators     read-only views of the whole fleet
  Branches     : Branch[]       required; lines and transformers, file order
  Oltcs        : Oltc[]         default empty
  Boundaries   : Boundary[]     default empty
  FrequencyHz  : double         default 50.0
  Name         : string         default "grid"
  AssetDynamics: AssetDynamicsModel?   default null   (asset classes and bindings)
  Protections  : ProtectionModel?      default null   (placed protective devices)
  BusCount, SystemInertiaGvaS   computed
```

Result row `i` of every solver corresponds to `Buses[i]`; the solvers build an id-to-index map from `Bus.Id`, and
results carry `BusIds` so an external id can always be recovered. Bus ids are arbitrary integers and need not be
contiguous or sorted. They must be unique.

### Which loader sets what

Five paths build a `GridModel`: the MATPOWER parser (`src/GridSim.Core/IO/Matpower/MatpowerParser.cs`), the JSON v1
loader (`src/GridSim.Core/IO/Json/JsonGridLoader.cs`), the JSON v2 loader
(`src/GridSim.Core/IO/Json/V2CaseLoader.cs`, which maps a v2 case onto the v1 data carriers and builds through
`JsonGridLoader.Build`, so a v2 case and its v1 equivalent give the same model bit for bit, as the invariants pin), the CIM bridge
(`src/GridSim.Core/IO/Cim/CimGridModelBridge.cs`) and the GDA builder (`src/GridSim.Gda/GdaModelBuilder.cs`). A sixth,
`BridgeProtocol.BuildModel` (`src/GridSim.Core/IO/BridgeProtocol.cs`), rebuilds a model from a bridge `topology`
message or a solve export. It reads a subset of the fields, listed under each element table, and is the only loader that
sets a branch `InService` false and the winding data. The tables below name the loader behaviour field by field. "v1" and "v2" share one builder, so a field set by "JSON" is set by both unless the entry
says otherwise.

### `Bus`

`Bus(Id, Type, Pd, Qd, Gs, Bs, BaseKv, Vm, VaDeg, Name, X, Y, Zip, VminPu, VmaxPu)`.

| Field | Unit | Default | Validation (`GridModel.Validate`) | Meaning |
|---|---|---|---|---|
| `Id` | - | required | duplicate ids are an error, reported once per id | External bus number. |
| `Type` | `BusType` | required | no slack bus anywhere is an error; several are a warning | `Slack`: `Vm` and `Va` fixed, absorbs the balance. `Pv`: P and `Vm` fixed, Q solved. `Pq`: P and Q fixed, V solved. |
| `Pd`, `Qd` | MW, MVAr | 0 | must be finite | Demand at 1.0 pu voltage. |
| `Gs`, `Bs` | MW, MVAr at 1.0 pu | 0 | must be finite | Fixed shunt, entered as the power it draws at 1.0 pu. Positive `Gs` draws MW; positive `Bs` injects MVAr (a capacitor bank). |
| `BaseKv` | kV | 0 | none | Nominal bus voltage. Never enters the solve; it converts per-unit voltage back to kV. |
| `Vm` | pu | 1.0 | none | Initial voltage magnitude. At a `Slack` or `Pv` bus that has a generator the generator's `Vg` takes precedence; `FlatStart` replaces the rest with 1.0. |
| `VaDeg` | degrees | 0 | none | Initial angle. Always honoured at a `Slack` bus. |
| `Name` | - | null | none | Asset identity carried through exports. |
| `X`, `Y` | 0..1 | 0 | none | Layout coordinates for drawing. No solver reads them. |
| `Zip` | `ZipLoad?` | null | JSON: all six coefficients present and finite, each triple sums to 1 within `1e-9` | Voltage-dependent load; null is constant power. |
| `VminPu`, `VmaxPu` | pu | null | each given limit finite and above 0; `VminPu` not above `VmaxPu` (equal is allowed) | The bus's own voltage limits; null means the global operational band applies. |

Loaders:

| Field | MATPOWER | JSON v1 / v2 | CIM | GDA |
|---|---|---|---|---|
| `Id` | `mpc.bus` column 1 | `id` | 1-based position in the CIM bus list; three-winding star points are numbered after the CIM nodes | 0-based sequence in order of first use |
| `Type` | column 2: 3 is `Slack`, 2 is `Pv`, anything else `Pq`; type 4 (isolated) is dropped with its elements; a type 2 bus with no in-service generator is demoted to `Pq` and a note says so | `type` (`pq`; `pv` or `gen`; `slack`, `ref` or `swing`; any letter case; an unknown word or JSON null is refused); an in-service generator raises its bus to `Pv`, and a `slack` role makes its bus `Slack`; a `pq` role leaves the bus alone | the first generator's bus is `Slack`, later generators on other buses are `Pv`, everything else `Pq`; a network with no generator makes bus 1 the slack | `Slack` for each grid supply point infeed, `Pq` for the rest; an island left with no slack gets one at its highest-kV real bus (never a transformer star node) |
| `Pd`, `Qd` | columns 3, 4 | `demand.json` rows summed per bus: `pMw`, and `qMvar` or, when absent, `pMw * tan(acos(powerFactor))` with `powerFactor` defaulting to 0.97 and clamped to `[0.01, 1]` | summed `P`, `Q` of the CIM loads | demand rows summed per bus |
| `Gs`, `Bs` | columns 5, 6 | `gs`, `bs` | not set (0) | not set (0) |
| `BaseKv` | column 10 | `baseKv`, required: a bus without it is refused naming up to twenty buses | the CIM bus base voltage | the voltage of the merged node |
| `Vm`, `VaDeg` | columns 8, 9 | `vm`, `vaDeg` | 1.0, 0 | 1.0, 0 |
| `Name` | null | `name` | CIM name | substation name |
| `X`, `Y` | 0 | `x`, `y` | 0 | set only by the optional geolocation step |
| `Zip` | null | `zip` | null | null |
| `VminPu`, `VmaxPu` | columns 13, 12; a row of fewer than 13 columns leaves both null with no note; a longer row whose pair is non-finite, not above 0 or has `VMIN` above `VMAX` leaves both null and is counted in a parse note | `vminPu`, `vmaxPu` (refused naming the bus when unusable) | null | null |

`BridgeProtocol.BuildModel` reads `Type`, `Pd`, `Qd`, `Gs`, `Bs`, `BaseKv`, `Vm`, `Name`, `X`, `Y` and `Zip`; it does not
read `VaDeg` or the voltage limits. A bus type name that does not parse silently becomes `Pq`; only winding names are
refused.

**Actual kV recovery.** A solved result stores `Vm` in per-unit. The physical voltage at result index `i` is
`Result.Vm[i] * Buses[i].BaseKv`. No other place applies a voltage base.

### `ZipLoad`

`ZipLoad(Ap, Bp, Cp, Aq, Bq, Cq)` with defaults `(0, 0, 1, 0, 0, 1)`:

```
P(V) = Pd * (Ap*V^2 + Bp*V + Cp)          V in pu
Q(V) = Qd * (Aq*V^2 + Bq*V + Cq)
dP/dV = Pd * (2*Ap*V + Bp)                 Jacobian contribution
dQ/dV = Qd * (2*Aq*V + Bq)
```

`VariesWithVoltage` is true when any of `Ap`, `Bp`, `Aq`, `Bq` is non-zero; a bus whose ZIP does not vary is treated as
constant power, exactly as a bus with `Zip` null. `ZipLoad.FromInverterFraction(f, nonInverter)`
blends a constant-power inverter share `f` (clamped to `[0, 1]`) with a base composition:
`f*(0,0,1) + (1-f)*nonInverter`, so each triple still sums to 1 when `nonInverter`'s does. The Newton engines apply the
ZIP as a deviation from nominal in the mismatch and add its derivatives to the Jacobian diagonal. The drawn power at a
solved voltage comes from one helper, `LoadAtVoltage` (`src/GridSim.Core/PowerFlow/LoadAtVoltage.cs`), shared by the
reactive-limit check and slack reactive diagnostic (`NewtonShell`), the backward/forward sweep, the distributed-slack
power flow, the physics audit and feasibility check (`PhysicsLaws`, `PhysicsFeasibility`) and power-flow tracing. JSON `zip` blocks, the bridge `zip` block and the JSON v2 `zip` member set it; MATPOWER, CIM and GDA do not,
and the MATPOWER writer reports a ZIP bus as a loss because MATPOWER cannot hold one.

### `Generator`

`Generator(Bus, Pg, Qg, Vg, Qmin, Qmax, Pmin, Pmax, MBase, H, Name, Type, DynamicsClass, SubtransientXdPu,
NegativeSeqXPu, ConverterInterfaced, ConverterFaultCurrentPu, PowerFlowOnly, UnitTransformerXPu, GridForming,
InService, Cost, ReactiveCost)`.

| Field | Unit | Default | Validation | Meaning |
|---|---|---|---|---|
| `Bus` | - | required | the bus must exist (error), for in-service and out-of-service units alike | The bus it sits on. Several generators on one bus are summed by the solvers. |
| `Pg`, `Qg` | MW, MVAr | `Qg` 0 | finite (in-service units) | Dispatched output. At `Pv` and `Slack` buses the solve determines the actual Q. |
| `Vg` | pu | 1.0 | finite (in-service units) | Voltage setpoint at `Slack` and `Pv` buses. When several generators share a bus the last one listed sets it (`VoltageSetpoint.ForBus`, `src/GridSim.Core/PowerFlow/VoltageSetpoint.cs`), matching MATPOWER. |
| `Qmin`, `Qmax` | MVAr | `-Infinity`, `+Infinity` | none: infinity means unlimited and is exempt from the finite check | Reactive capability; the per-bus sums feed the reactive-limit loop. |
| `Pmin`, `Pmax` | MW | 0, `+Infinity` | none in `Validate`; the JSON loader refuses a non-finite `pMinMw`, a `-Infinity` `pMaxMw`, a `+Infinity` `qMinMvar` and a `-Infinity` `qMaxMvar` | Real-power capability, read by the dispatch and optimal-power-flow studies. The load flow does not read it. |
| `MBase` | MVA | 100 | none | Machine base for `H` and the machine reactances. |
| `H` | MW.s/MVA on `MBase` | 0 | none | Inertia constant. System inertia is `sum(H * MBase)`; `GridModel.SystemInertiaGvaS` divides by 1000. |
| `Name`, `Type` | - | null, `""` | JSON refuses a null `type` | Identity and fuel or technology tag read by the fuel taxonomy. |
| `DynamicsClass` | - | null | none | Inline asset-dynamics class; wins over a binding rule. |
| `SubtransientXdPu`, `NegativeSeqXPu` | pu on `MBase` | 0 | none | X''d and X2 for short-circuit studies; at or below 0 selects the synthesised default (X''d) and the round-rotor rule (X2 = X''d). |
| `ConverterInterfaced`, `ConverterFaultCurrentPu` | bool?, x rated | null | none | Full-converter plant flag (null is inferred from `Type` by `ConverterPlant.IsConverterType`) and its fault-current multiple (null means 1.2, `DefaultConverterFaultCurrentPu`; 0 means no fault current). |
| `PowerFlowOnly` | bool | false | none | A balancing or voltage-control device, not physical plant. |
| `UnitTransformerXPu` | pu on `MBase` | 0 | none | Step-up transformer reactance added in series with the machine in fault studies. |
| `GridForming` | bool | false | none | Converter plant with grid-forming control. |
| `InService` | bool | true | none | False keeps the unit in the model but never solves it. |
| `Cost`, `ReactiveCost` | `GeneratorCost?` | null | MATPOWER: see below | The MATPOWER `gencost` rows. `Cost` is read by the costed dispatch (`src/GridSim.Core/Analysis/DcOpfCosted.cs`, `dcopf --costs`, and `acopf --costs`, whose DC passes call it); `ReactiveCost` is carried and written back by the MATPOWER writer, and no solver reads it. |

`GeneratorCost(Model, StartupCost, ShutdownCost, Coefficients)` holds a MATPOWER cost row: `Model` 1 is piecewise
linear (`Coefficients` are the points x1, y1, x2, y2, ... as (MW, currency per hour)); `Model` 2 is a polynomial
(`Coefficients` are the NCOST coefficients, highest order first). `PointCount` is NCOST. The MATPOWER parser refuses a
`gencost` matrix that has neither one row per generator nor two, a row shorter than `MODEL STARTUP SHUTDOWN NCOST`, a
`MODEL` other than 1 or 2, an `NCOST` that is not a whole number of at least 1, a row too short for its `NCOST`, and any
non-finite cost number. The row-count refusal names the counts; every other refusal names the row.

Loaders:

| Field | MATPOWER (`mpc.gen`) | JSON (`generation.json`) | CIM | GDA |
|---|---|---|---|---|
| `Bus` | column 1 | `bus` | the generator's node | the matched bus |
| `Pg` | column 2 | `outputMw`, else `capacityMva * capacityFactor`, with the factor defaulting by `type` (`Dispatcher.DefaultCapacityFactor`); the case is then balanced by `Dispatcher.Balance` unless `system.json` sets `"dispatch": false` | CIM `P` | capacity times the fuel capacity factor (`FuelMap.CapacityFactor`); with `GdaBuildOptions.EmbeddedGenAsNegativeLoad` the output is subtracted from the bus `Pd` and no `Generator` is created |
| `Qg` | column 3 | `qOutputMvar`, default 0 | 0 | 0 |
| `Vg` | column 6 | `voltageSetpoint`, default 1.0 | 1.0 | 1.0 |
| `Qmin`, `Qmax` | columns 5, 4 (absent columns give `-Infinity`, `+Infinity`) | `qMinMvar`, `qMaxMvar`, defaulting to `-capacityMva`, `+capacityMva`; the strings `"Infinity"` and `"-Infinity"` are the only strings accepted | `-Infinity`, `+Infinity` | plus or minus 0.4 times capacity (`GdaBuildDefaults.EmbeddedGenQRatio`) |
| `Pmin`, `Pmax` | columns 10, 9 (`Pmax` defaults to `+Infinity` on a short row) | `pMinMw` default 0; `pMaxMw` default `capacityMva` | 0, `+Infinity` | 0, capacity |
| `MBase` | column 7 when above 0, else `baseMVA` | `capacityMva` | 100 | capacity |
| `H` | 0 | `inertiaH` | 0 | the fuel inertia constant (`kind.H`) |
| `InService` | column 8 above 0; a status 0 unit is kept, marked out of service, unless its bus is dropped or undefined | `inService`, default true | true | true |
| `Cost`, `ReactiveCost` | `mpc.gencost` rows i and ng+i | not read by the loader (a cost file is read separately by `src/GridSim.Core/IO/Json/GeneratorCostFile.cs`) | null | null |
| `Type`, `DynamicsClass`, machine and converter fields | not read | `type` (default `ccgt`), `dynamicsClass`, `converterInterfaced`, `converterFaultCurrentPu`, `powerFlowOnly`, `unitTransformerXPu`, `gridForming`; `subtransientXdPu` and `negativeSeqXPu` load from v2 (N234) and are absent from v1 | not read | fuel type |

`BridgeProtocol.BuildModel` reads `Pg`, `Qg`, `Vg`, `Qmin`, `Qmax`, `Pmin`, `Pmax`, `MBase`, `H`, `Name` and `Type`.

**The generator fleet.** `GridModel.Generators` is the in-service fleet in the order given; every engine, analysis and
report reads it, so an out-of-service unit is invisible to them. `GridModel.AllGenerators` is every unit in the order
given, which is what the writers save, and `GridModel.OutOfServiceGenerators` is the units marked out of service.
Setting `Generators` takes the whole fleet and holds the out-of-service units back from the in-service list. When every
unit is in service the list given is returned by reference. An out-of-service unit does not raise its JSON bus to `Pv`
and does not make it the slack; a JSON case whose only slack unit is out of service is refused with "No slack bus". A
`Pv` bus with no in-service generator is solved as `Pq`.

### `Branch`

`Branch(From, To, R, X, B, TapRatio, PhaseShiftDeg, RateAMva, Name, IsTransformer, DynamicsClass, InService,
LengthKm, Equipment, R0, X0, FromWinding, ToWinding, FromNeutralXPu, ToNeutralXPu, RateBMva, RateCMva)`. A branch is a
line or a transformer in one pi-model.

| Field | Unit | Default | Validation | Meaning |
|---|---|---|---|---|
| `From`, `To` | - | required | both must be existing buses (error) | Terminals. `From` is the tap side. |
| `R`, `X` | pu on `BaseMva` | required | finite | Series impedance. |
| `B` | pu on `BaseMva` | 0 | finite | TOTAL line-charging susceptance; the stamp puts half at each end. |
| `TapRatio` | ratio | 1.0 | finite | Off-nominal tap magnitude on the `From` side. A value at or below 0 is treated as 1.0 by every stamp. |
| `PhaseShiftDeg` | degrees | 0 | finite | Phase-shifting angle, combined with the tap into one complex tap. |
| `RateAMva` | MVA | 0 | none | Continuous thermal rating. 0 is unrated: loading is reported as 0 percent. |
| `RateBMva`, `RateCMva` | MVA | 0 | none | Short-term and emergency ratings. Read only by a contingency screen asked to judge post-fault loading against them (`--post-fault-rating B` or `C`); a branch with 0 uses rating A. |
| `Name` | - | null | an `Oltc` and a `BoundaryBranch` find the branch by exact (ordinal) name | Identity. |
| `IsTransformer` | bool | false | none | Classification flag. The electrical model is only `TapRatio` and `PhaseShiftDeg`. |
| `InService` | bool | true | none | False is an open branch (a recloser or switch): every Y-bus builder skips it and its `LineFlow` is a zero placeholder. |
| `LengthKm`, `Equipment` | km, `"ohl"` or `"cable"` | 0, null | none | Circuit length and conductor type for frequency-dependent (skin-effect) modelling in the harmonic engine. |
| `R0`, `X0` | pu on `BaseMva` | 0 | none | Zero-sequence series impedance for fault studies; both 0 means not supplied (a line then uses the standard ratio times `R + jX`; a transformer uses `R + jX`). |
| `FromWinding`, `ToWinding` | `WindingConnection?` | null | the bridge refuses an unknown name | Transformer winding connection (`Delta`, `WyeGrounded`, `WyeUngrounded`) for the zero-sequence network; null on either side makes the branch a line in that network. |
| `FromNeutralXPu`, `ToNeutralXPu` | pu | 0 | none | Grounding reactance in the neutral; adds `3 * Xn` to the zero-sequence path. |
| `DynamicsClass` | - | null | none | Inline passive-plant dynamics class. |

Loaders:

| Field | MATPOWER (`mpc.branch`) | JSON (`transmission.json`, `transformer.json`) | CIM | GDA |
|---|---|---|---|---|
| `R`, `X`, `B` | columns 3, 4, 5 | line: `rPu / circuits`, `xPu / circuits`, `bPu * circuits`; transformer: `rPu`, `xPu`, `B` = 0 | line: ohm and siemens converted on the from bus base (a from bus with no base voltage takes the values as already per-unit); transformer: ohm on each end's rated voltage (a `ratedU` of 0 or less takes the value as per-unit), the two ends added, `B` = 0; a three-winding unit becomes a star point with one branch per winding | circuit and transformer values converted to the model base; transformer `B` = 0 |
| `TapRatio` | column 9; 0 means a line (1.0) | `tapRatio` (a line may carry one, so a converted case keeps its branch order) | the tap changer, referred to the from side (each winding of a three-winding unit carries its own) | 1.0 |
| `PhaseShiftDeg` | column 10 | `phaseShiftDeg` | the tap changer | 0 |
| `RateAMva` | column 6 | `ratingMva * circuits` (transformer: `ratingMva`) | not set | winter rating (line), nominal rating (transformer) |
| `RateBMva`, `RateCMva` | columns 7, 8 | `ratingBMva`, `ratingCMva` (times `circuits` on a line) | not set | not set |
| `IsTransformer` | true when the ratio column is non-zero, even if it is 1 | `isTransformer`; always true for a `transformer.json` row | true | true for a transformer |
| `InService` | a status 0 branch is dropped, so the parsed branch is always in service | no v1 key; v2 refuses `"inService": false` | true | true |
| `LengthKm`, `Equipment`, `R0`, `X0` | not read | `lengthKm`, `equipment`, `rPu0 / circuits`, `xPu0 / circuits` (lines only) | not set | not set |
| windings, neutral reactances | not read | not read; v2 `windings`, `neutralXPu`, `r0Pu` and `x0Pu` on a transformer (N234) | not set | not set |

`BridgeProtocol.BuildModel` reads every branch field except `LengthKm`, `Equipment`, `DynamicsClass`, `RateBMva` and
`RateCMva`; it is the only loader that sets `InService` false and the winding fields.

### `Oltc`

`Oltc(Transformer, ControlsBus, TargetVpu, MinTap, MaxTap, StepPu, DeadbandPu)` is an on-load tap changer on the branch
named `Transformer`.

| Field | Unit | Default | Meaning |
|---|---|---|---|
| `Transformer` | - | required | `Branch.Name` of the transformer it moves. |
| `ControlsBus` | - | required | The regulated bus. |
| `TargetVpu` | pu | 1.0 | Voltage target. |
| `MinTap`, `MaxTap` | ratio | 0.9, 1.1 | Tap range. |
| `StepPu` | ratio | `Oltc.DefaultStepPu` = 0.0125 | One tap step (1.25 percent). |
| `DeadbandPu` | pu | 0.01 | No movement while `|Vm - Target| <= Deadband`. |

`Validate` gives a warning, never an error, for an OLTC whose `ControlsBus` does not exist, whose `Transformer` names no
branch, that controls the higher-`BaseKv` winding, or that controls a bus that is not an end of the transformer. The
winding test is skipped only when the two end voltages are equal, both unknown included. If exactly one end has no
`BaseKv`, the unknown end counts as the low-voltage side and the test can fire.

`SteadyStateSolver` ([ch. 3, Power flow](<0003 Power flow.md>)) applies these rules on each pass:

- A record drives the first branch with its `Transformer` name that has `ControlsBus` at one end. When no same-named
  branch touches that bus, it falls back to the first branch of that name.
- A record naming no branch, or whose `ControlsBus` is not in the result, is skipped. Those are the only two warned
  cases that stop regulation. A warned high-voltage-side OLTC, and one whose bus is not an end, still regulate.
- A record whose controlled bus is generator-regulated as solved (a `Pv` or `Slack` bus that the reactive-limit loop has
  not pinned at a limit) holds its tap.
- The step direction depends on the end. With the controlled bus on the `To` (untapped) end, lowering the tap raises its
  voltage. With the controlled bus on the `From` (tapped) end, raising the tap raises it. A bus on neither end uses the
  `To` rule.

JSON reads `onloadtapchanger.json` and the v2 `oltcs` array. The GDA builder creates one per transformer only when
`GdaBuildOptions.CreateOltcs` is set, the two kV values differ and the published tap range is valid; a 0 %/0 % range or
an invalid one gives none and is counted in the build notes. The bridge reads them, and MATPOWER and CIM carry none.

### `Boundary` and `BoundaryBranch`

`BoundaryBranch(Branch, Direction = 1)` names one branch's contribution to a cut and the sign that counts as positive
transfer. `Boundary(Name, LimitMw, Branches)` is the cut: the flow across it is the signed sum of the named branches'
solved flows, compared against `LimitMw` by `src/GridSim.Core/Analysis/BoundaryAnalysis.cs`. Each branch name must
name exactly one branch of the model, compared ordinally (`Boundary.BranchNameProblems`). A name that matches nothing,
or that several branches share, is an error in `GridModel.Validate`, is refused by the JSON loader and the bridge model
reader, and makes `BoundaryAnalysis.Evaluate`, `LossAllocation.Allocate`, `PowerFlowTracing.BoundaryTransfers` and the
contingency screen throw `InvalidDataException` naming the boundary and the name. Out-of-service branches count as
real names. In the reduced post-outage model of the contingency screen an outaged boundary branch is absent and carries
no flow. JSON reads `boundaries.json` and the v2 `boundaries` array; the bridge reads boundaries and drops one with no
branches; MATPOWER, CIM and GDA carry none.

### Frequency, base MVA, name

`GridModel.FrequencyHz` defaults to `GridSim.Core.Dynamics.FrequencyLimits.NominalHz` (50 Hz), the same value the JSON
defaults and the bridge reader take. MATPOWER files carry no frequency:
the parser takes the caller's value, else `MatpowerParser.AssumedFrequencyHz` = 50.0 and writes a parse note saying
the value is assumed; a frequency that is not positive and finite is refused. JSON takes `system.frequencyHz`
(default 50.0 in v1; read from the system block in v2); the bridge takes `frequencyHz` and refuses a value that is not positive and finite; the
GDA builder uses `GdaBuildDefaults.SystemFrequencyHz` = 50.0; the CIM bridge leaves the default.

`BaseMva` is required. MATPOWER takes `mpc.baseMVA`, defaulting to 100 when the scalar is absent; JSON takes
`system.baseMva`, which becomes `GridModel.DefaultBaseMva` = 100 only when it is absent; a present value that is zero,
negative or not finite is refused by `Validate` (below); the CIM bridge takes its `baseMva` argument (100); the GDA
builder takes `GdaBuildOptions.BaseMva` (100); the bridge takes `baseMva`, reads an absent value as
`GridModel.DefaultBaseMva`, and refuses a present value that is not finite and above 0. `Name` for MATPOWER is the
caller's name, else the `function mpc = <name>`, else `"matpower"`; the file loader
passes the file stem, so the stem wins over the function name, and a built-in case passes a display name such as
"IEEE 14-bus". For JSON it is `system.name` or the folder name, for CIM the caller's argument, and for GDA the dataset and
filter description. `"grid"` is only the `GridModel.Name` default.

### Asset bindings and protections

`GridModel.AssetDynamics` is an `AssetDynamicsModel(Library, Bindings)`: an `AssetClassLibrary` of authored asset
classes and an `AssetBindingSet` of `AssetBinding(MatchType, ClassName)` rules. `AssetBindingSet.Resolve(inlineClass,
assetType)` returns the inline `DynamicsClass` of the element when it has one; otherwise the first rule whose
`MatchType` equals the element's `Type` (case-insensitive, exact rules first); otherwise the first prefix rule, a
`MatchType` ending in `*` (`wind*` matches `wind-onshore` and `wind-offshore`); otherwise null, and the element stays
passive. `GridModel.Protections` is a `ProtectionModel(Library, Instances)` of placed `ProtectionInstance(Name,
ClassName, MonitorBus, CapacityMw, TargetBranch)` devices. Both are null for a case that ships no `asset-classes.json`
or `protections.json`. The JSON loader builds them from `asset-classes.json`, `asset-bindings.json` and
`protections.json` and fails loud on a binding to an unknown class, bindings with no classes, a class kind or provenance
tag it does not know, and an instance whose class is not a protection class; after the model is built it checks every
placed device against the network. The classes are specified in
[ch. 13, Asset dynamics and data grounding](<0013 Asset-dynamics data grounding.md>) and
[ch. 15, Protections](<0015 Protections.md>).

## Per-unit conventions

- **One system base.** All power is converted with `GridModel.BaseMva` alone. The specified injection at a bus is
  `Pspec = (sum Pg - Pd) / BaseMva` and `Qspec = (sum Qg - Qd) / BaseMva`
  (`NewtonShell.BuildBusSpec`, `src/GridSim.Core/PowerFlow/NewtonShell.cs`). `Pspec` and `Qspec` hold the nominal load;
  a ZIP load is applied as a deviation inside the iteration.
- **Voltage bases.** `Bus.BaseKv` is the nominal line-to-line voltage of the bus. The solve never reads it. A branch's
  `R`, `X` and `B` are entered per-unit on `BaseMva` and on the voltage bases of its ends, so no impedance conversion
  happens inside the solvers. Converting an impedance given in ohms is a loader job and uses the base impedance of the
  bus voltage:

```
Zbase = BaseKv^2 / BaseMva            ohm   (BaseKv in kV, BaseMva in MVA)
r_pu  = r_ohm / Zbase,   x_pu = x_ohm / Zbase
b_pu  = b_siemens * Zbase             a susceptance converts the other way
```

  The CIM bridge applies this to a line on the from-bus base and to each transformer winding on that winding's own rated
  voltage `ratedU` (the per-unit impedance is invariant across the ideal ratio, so the two windings' values add with no
  referral factor). The GDA builder converts percent data in three ways. A circuit impedance is
  `pu = percent / 100 * (BaseMva / 100)` and a circuit susceptance is `pu = percent / 100 / (BaseMva / 100)`. A
  transformer impedance is `pu = percent / 100 * (BaseMva / txBase)`, where `txBase` is the transformer rating
  (`GdaBuildDefaults.TransformerRatingFallbackMva` when the rating is 0 or less) if the dataset gives percent on rating,
  else 100. Ohm data is divided by the base impedance of `RefZBase`, which uses `GdaBuildOptions.RefKv` when it is set and
  otherwise the grid supply point group voltage. The MATPOWER unit normaliser (below) applies the formula above to raw
  distribution files.
- **Shunts** are entered in engineering units (MW and MVAr at 1.0 pu) and divided by `BaseMva` at stamp time, the only
  place a bus field is converted inside the Y-bus builders.
- **Voltages** are per-unit throughout; `BaseKv` converts back to kV for display.
- **Angles** are degrees in `Bus.VaDeg` and `PowerFlowResult.VaDeg` and radians inside every solver loop.
- **Results return to engineering units** at assembly: `PInjMw = P_pu * BaseMva`, and line flows likewise
  (`NewtonShell.AssembleResult`, `NewtonShell.ComputeLineFlows`).
- **Convergence tolerances** are per-unit power mismatches; the default `1e-8` is 1 W on a 100 MVA base.

### Transformer conventions

A transformer is one `Branch`. Its off-nominal ratio and phase shift form one complex tap on the **from** side:

```
a   = TapRatio                         (a <= 0 is replaced by 1.0)
phi = PhaseShiftDeg * pi / 180
tap = a * e^(j*phi)                    DetMath.FromPolar(a, phi)
```

The from terminal sees the pi-model through an ideal transformer of complex ratio `tap : 1`, as in MATPOWER; the
to terminal connects to the pi-model directly. A tap of 1.0 with zero shift is a plain line. `Oltc` limits (`MinTap`,
`MaxTap`) are tap ratios in the same convention. The phase shift acts only through the asymmetric mutual terms below.
The magnetising branch is not modelled: no loader sets a shunt on a transformer branch, and `B` is 0 for every transformer a loader builds.

## Y-bus construction

### The branch stamp

The branch stamp has one source, `AdmittanceStamp.Branch`
(`src/GridSim.Core/PowerFlow/AdmittanceStamp.cs`), which returns a `BranchStamp(Yff, Ytt, Yft, Ytf)`. Every builder of
the network matrix uses it: the dense `YBus.Build`, its cell-only twin `YBus.BuildSparse`, and the sparse power-flow Y
`SparseNewtonRaphsonPowerFlow.BuildY`. So do the branch flows (`NewtonShell.ComputeLineFlows`), the estimator's flow
measurement model, the fault studies, the physics audit and the harmonic `Y(h)`, which at `h = 1` agrees with the fundamental
Y-bus to within `1e-9` per cell (`FrequencyDependentYBusTests`); that agreement is a tolerance, not a bit-identity claim.

```
ys  = 1 / (R + jX)                        series admittance
bc  = j * B / 2                           half the line charging, per end
a   = TapRatio  (<= 0 treated as 1.0)
tap = a * e^(j * PhaseShiftDeg * pi/180)  complex tap, on the FROM side

Yff = (ys + bc) / a^2                     from-end self term
Ytt =  ys + bc                            to-end self term
Yft = -ys / conj(tap)                     from-to mutual
Ytf = -ys / tap                           to-from mutual
```

These are the terms of the branch current equations

```
I_from =  (ys + bc)/a^2 * V_from  -  ys/conj(tap) * V_to
I_to   = -ys/tap        * V_from  + (ys + bc)     * V_to
```

Points to audit:

- The ideal transformer sits on the from side. The from-end self term, including the from half of the line charging, is
  referred through the tap by `1/a^2`; the to-end self term is unscaled.
- The phase shift never appears in the self terms, because `|tap|^2 = a^2`. It rotates only the mutual terms, and
  asymmetrically (`conj(tap)` against `tap`), which is what makes a phase shifter move flow: `Yft != Ytf` whenever the shift
  is not a multiple of 180 degrees.
- With no shift the stamp is symmetric: `Yft = Ytf = -ys / a`.
- A branch with `R = 0` and `X = 0` has no finite series admittance; `Validate` refuses it (below), so no loaded model
  reaches the stamp with one.
- `DetMath.FromPolar` is `a * (cos phi + j sin phi)` over the deterministic fdlibm `DetMath.SinCos`, not the platform's
  `Complex.FromPolarCoordinates`, so the tap has the same bits on every operating system.
- The harmonic and long-line builders call `AdmittanceStamp.FromAdmittance` with an already computed series admittance
  and per-end shunt; the tap arithmetic is the same lines.

### The bus matrix

For every in-service branch the builder adds the four stamp terms to `Y[f,f]`, `Y[t,t]`, `Y[f,t]` and `Y[t,f]`, where
`f` and `t` are the matrix indices of `From` and `To` (their positions in `Buses`). Branches are visited in model
order. Then every bus with `Gs != 0` or `Bs != 0` adds `(Gs + jBs) / BaseMva` to its diagonal, in bus order. Cells start
at +0.0 and accumulate by complex addition, so the result is a fixed sum in a fixed order:

```
Y[i,i] = sum over in-service branches with From = i of Yff
       + sum over in-service branches with To   = i of Ytt
       + (Gs_i + j*Bs_i) / BaseMva                                   added last
Y[i,k] = sum over in-service branches From = i, To = k of Yft
       + sum over in-service branches From = k, To = i of Ytf        (i != k)
```

with each sum taken in branch order. Parallel circuits between one pair of buses accumulate, so duplicated branches
are summed admittances. With `Y = G + jB`, the shunt sign gives a drawn complex power `|V|^2 * (Gs - jBs)` at voltage
`V` pu: positive `Gs` draws MW, positive `Bs` injects MVAr. Line charging is the only shunt on a branch.

### Out-of-service branches and islands

An out-of-service branch (`InService` false) is skipped by every builder and contributes no admittance. A model with
every branch in service builds the same matrix as a model with no such flag. `NewtonShell.ComputeLineFlows` emits a
zero-flow `LineFlow` for the skipped branch, so `LineFlows[i]` stays aligned with `Branches[i]`.

The Y-bus builders do not partition the network. A split network gives a block-diagonal Y, and a block with no
reference bus is singular. Two rules keep that from reaching the solver. First, a model may carry several `Slack`
buses, one reference per island, each held at its own setpoint (`Validate` warns, it does not refuse). Second,
`IslandPartition.Prepare` (`src/GridSim.Core/PowerFlow/IslandPartition.cs`), applied by `TopologyOps.Apply` for the live
simulator's recloser-opened branches, finds the connected components over in-service branches and, per island: keeps
an island that has a slack; elects as slack the bus with the largest single `|Pg|` in an island that has generation
(a bus counts as generating when its units' `|Pg|` sum is above 0) and no slack; and blacks out an island with no
generation, removing its buses, branches and generators from the model and booking the lost demand (`Pd`) and lost
generation (`Pg`) in the `IslandReport`. A model that is one connected component with a slack is returned unchanged
(the same object). The pre-pass keeps the out-of-service generators of live buses (`AllGenerators`).

### Dense and sparse builders, and how they stay identical

| Builder | Output | Used by |
|---|---|---|
| `YBus.Build` (`src/GridSim.Core/PowerFlow/YBus.cs`) | `Complex[n,n]` | the dense Newton engine, the distributed-dynamics initialisation and the fault network system |
| `YBus.BuildSparse` | `SparseYBus`: a dictionary of stamped cells (a cell that sums to exactly zero is kept) keyed `(row, col)` by bus index, no dense array | the estimator's measurement model, the fault analysis and the subtransient initialisation of a large model |
| `SparseNewtonRaphsonPowerFlow.BuildY` (`src/GridSim.Core/PowerFlow/SparseNewtonRaphsonPowerFlow.cs`) | `ComplexCsr` (`src/GridSim.Core/Numerics/Sparse.cs`): `RowPtr`, `ColIdx`, separate `G` and `B` arrays, a diagonal pointer per row | the sparse Newton engine |

All three read the same `AdmittanceStamp.Branch`, visit branches and then shunts in the same order, and begin every
cell from +0.0. Three details make the sums bit-identical:

1. **Signed zeros.** The dense array starts every cell at +0.0, so the first stamp is `0.0 + v`, which turns a `-0.0`
   component into `+0.0`. `BuildSparse` adds `Complex.Zero` to the first stamp of a cell for the same reason, and
   `ComplexCsr.Builder.Build` writes `0.0 + g`, `0.0 + b` for the first entry of a cell.
2. **Summation order.** `ComplexCsr.Builder.Build` sorts each row by `(column, insertion index)`, a unique key, so the
   entries of one cell keep their insertion order whatever the sort algorithm does. That order is the dense order
   (branches, then shunts). An unstable sort would sum the diagonal of a bus with many branches in a different order.
3. **Shunt division.** The dense builder divides the complex number `(Gs, Bs)` by the real `BaseMva`; the sparse builder
   divides `Gs` and `Bs` separately. The dense division uses the `Complex / double` operator, which divides each component,
   so the values are equal; the dense-sparse parity tests pin that on the cases with shunts (case14, case30, case118).

`ComplexCsr.Builder.Add` skips a stamp whose real and imaginary parts are both exactly 0; a skipped stamp changes no
sum. The CSR keeps a diagonal entry in every row, stored as zero when no stamp lands there.

## Unit normalisation for distribution MATPOWER files

Four published radial feeders (`case22`, `case33bw`, `case69`, `case85` in `data/matpower/dist`) declare `baseMVA` and
per-bus `baseKV` but give branch impedance in ohms and bus demand in kW and kVAr. Read as per-unit and MW, a 3.715 MW feeder
would be 3715 MW. `MatpowerParser` therefore passes every parse through `MatpowerUnitNormalizer`
(`src/GridSim.Core/IO/Matpower/MatpowerUnitNormalizer.cs`) by default (`MatpowerUnitHandling.NormaliseRawFeeders`).

**Detection.** `LooksRawUnits(model)` is true only when all three tells agree; unit-ness is not recorded in the file,
so the test errs towards leaving a file alone.

| Tell | Rule | Constant |
|---|---|---|
| Distribution scale | at least one `BaseKv` above 0, and no bus above `DistributionKvCeiling` = 35 kV | `DistributionKvCeiling` |
| Load magnitude | `sum(abs(Pd)) / BaseMva` at least `LoadToBaseRatioTrigger` = 30 | `LoadToBaseRatioTrigger` |
| Ohmic impedance | the median `sqrt(R^2 + X^2)` as given is at least `OhmicImpedanceFloor` = 0.05, and the median of that value divided by the from-bus `Zbase` is below `ConvertedPuImpedanceCeiling` = 1.0 | `OhmicImpedanceFloor`, `ConvertedPuImpedanceCeiling` |

A model with `BaseMva` at or below 0, no buses or no branches is never flagged. A 110 kV source bus vetoes the file,
which is what keeps the already-per-unit `cigre_mv` case untouched.

**Conversion.** `Normalize(model)` returns the model unchanged when it does not look raw; otherwise it returns a copy
(built through `CopyWith`) in which:

```
R, X     <- R, X / Zbase(from bus)         Zbase = BaseKv^2 / BaseMva
Pd, Qd, Gs, Bs <- value / 1000             kW, kVAr -> MW, MVAr
```

`B` and the generators are not changed: the known raw feeders carry no line charging, and their only generator is the
slack, whose output the solve computes. A branch whose from bus has no `BaseKv` keeps its raw impedance.

**Guards.** `MatpowerParser.ParseWithDiagnostics` adds a parse note naming the conversion and sets
`MatpowerParseResult.RawUnitsConverted`. It then checks that the converted model no longer looks raw and throws
`InvalidDataException` if it does, because a caller that wraps `Normalize` again would otherwise convert a second time.
`MatpowerUnitHandling.AsWritten` keeps the numbers as the file writes them and adds a note that the case looks raw and
was not converted. An undefined `MatpowerUnitHandling` value throws `ArgumentOutOfRangeException`. A file that does not
look raw is returned as the same result object, so every other bundled file parses exactly as written.

## Immutability and the copy helpers

The element types are `record`s. A "modified" element is a `with` copy; `Normalize` builds its buses with
`b with { Pd = ..., Qd = ... }` and `TopologyOps.Apply` opens a branch with `b with { InService = false }`. Record
equality compares fields, and compares the list-valued fields (`Boundary.Branches`, `GeneratorCost.Coefficients`) by
reference. `GridModel` is a class with `init`-only properties and `IReadOnlyList<T>` collections, and no code changes a model after construction, so it is shared across parallel N-1 solves
without a defensive copy. The language does not enforce this for the list contents: the loaders pass `List<T>` instances.
A "modified" network is a new `GridModel`.

`GridModel.CopyWith(buses, generators, branches, oltcs, boundaries, name, frequencyHz, assetDynamics)` is the one copy
method. Every argument left null is carried across unchanged, and every property it does not take (`BaseMva`,
`Protections`) is carried too. Its generator rule:

- With `generators` null, the whole fleet is carried, out-of-service units included, and the copy's `Generators` is the
  same in-service list.
- With a `generators` list that holds no out-of-service unit (a redispatch builds it from `Generators`),
  this model's out-of-service units are appended after it; the copy's `Generators` is exactly the list given.
- With a `generators` list that itself marks a unit out of service, that list is taken as the complete fleet.

Other helpers build on it: `Dispatcher.Clone` (`src/GridSim.Core/Dispatch/Dispatcher.cs`) passes through `CopyWith`;
`TopologyOps.Apply` copies with an opened branch set; `MatpowerUnitNormalizer.Normalize` copies with replaced buses and
branches. `AssetEnvelope`, which swaps the asset-dynamics bundle per sweep corner, copies through `CopyWith` too
(`assetDynamics`, N314), so no copy is built by hand.

## Validation

`GridModel.Validate()` returns a list of `GridDiagnostic(Severity, Message)` and never throws; `ToString()` renders
`error: ...` or `warning: ...`, the format the CLI prints. Entry-point loaders throw `InvalidDataException` on any error
(the MATPOWER parser and the JSON loader run it after building); the GDA merge pipeline collects the findings as notes.

Errors:

1. A `BaseMva` or a `FrequencyHz` that is not a finite number above 0.
2. Duplicate bus id, once per id.
3. No slack bus.
4. A branch `From` or `To` that is not a bus.
5. A branch whose `From` equals its `To` (a self-loop), whatever its service state.
6. A branch with `R` and `X` both 0 (either sign of zero), whatever its service state: its admittance `1 / (R + jX)`
   is not finite. A real zero-impedance tie is modelled by merging its two buses or by a small reactance.
7. An island (a set of buses joined by in-service branches) with no slack bus of its own, reported with its size and
   its lowest-id buses. Checked only when the model has a slack and no duplicate bus id.
8. An in-service generator, or an out-of-service generator, on a bus that does not exist.
9. A bus voltage limit that is not finite and above 0 pu, or a `VminPu` above its `VmaxPu`.
10. A boundary branch name that matches no branch or more than one.
11. A non-finite `Pd`, `Qd`, `Gs` or `Bs` per bus; `R`, `X`, `B`, `TapRatio` or `PhaseShiftDeg` per branch; `Pg`, `Qg` or `Vg`
   per in-service generator. A NaN or infinity here would feed the Y-bus and Jacobian and poison the solve, so it is
   caught as a model fault. Generator `Qmin`, `Qmax`, `Pmin` and `Pmax` are excluded because infinity means no limit.

Warnings: more than one slack bus (a multi-island system); an OLTC whose `ControlsBus` does not exist, whose
`Transformer` names no branch, that controls the higher-`BaseKv` winding, or that controls a bus that is not an end of
its transformer.

## Parameters and defaults

Constants that fix the data-model defaults and thresholds:

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Default system base | 100 | MVA | Used where the JSON system file gives no `baseMva` (absent), and where the bridge model block gives a value at or below 0; MATPOWER, CIM and GDA use their own literal 100.0 | `GridModel.DefaultBaseMva` |
| Default frequency | 50.0 | Hz | The GB nominal and the `GridModel.FrequencyHz` default (a literal 50.0 there); the JSON defaults and bridge reader take it from `FrequencyLimits.NominalHz` | `GridModel.FrequencyHz`, `FrequencyLimits.NominalHz` |
| Assumed MATPOWER frequency | 50.0 | Hz | Taken when the caller gives none; noted in the parse result | `MatpowerParser.AssumedFrequencyHz` |
| Default OLTC step | 0.0125 | ratio | One tap step | `Oltc.DefaultStepPu` |
| Default OLTC range | 0.9 to 1.1 | ratio | `MinTap`, `MaxTap` | `Oltc` constructor defaults |
| Default OLTC deadband | 0.01 | pu | No movement inside it | `Oltc.DeadbandPu` |
| JSON load power factor | 0.97 | - | Used when a demand row gives no `qMvar` | `JsonDefaults.LoadPowerFactor` |
| JSON minimum power factor | 0.01 | - | Lower clamp on a demand row's power factor | `JsonDefaults.MinPowerFactor` |
| ZIP sum tolerance | `1e-9` | - | How far a coefficient triple may sum from 1 | `JsonGridLoader.ZipSumTolerance` |
| Converter fault current | 1.2 | x rated | Default `k` of a converter-interfaced unit | `Generator.DefaultConverterFaultCurrentPu` |
| Operational voltage band | 0.94 to 1.06 | pu | Applies on a side a bus leaves null | `VoltageLimits.OperationalLowPu`, `OperationalHighPu` |
| Distribution kV ceiling | 35 | kV | Raw-feeder tell 1 | `MatpowerUnitNormalizer.DistributionKvCeiling` |
| Load-to-base trigger | 30 | x `BaseMva` | Raw-feeder tell 2 | `MatpowerUnitNormalizer.LoadToBaseRatioTrigger` |
| Ohmic impedance floor | 0.05 | as given | Raw-feeder tell 3 | `MatpowerUnitNormalizer.OhmicImpedanceFloor` |
| Converted impedance ceiling | 1.0 | pu | Raw-feeder tell 3 | `MatpowerUnitNormalizer.ConvertedPuImpedanceCeiling` |
| Default tap | 1.0 | ratio | `Branch.TapRatio` default; a MATPOWER ratio of 0 and any ratio at or below 0 at stamp time mean 1.0 | `Branch.TapRatio`, `AdmittanceStamp.FromAdmittance` |

The per-field defaults are in the element tables above.

The per-bus limits are used by the contingency verbs `nminus1`, `nminus2` and `n-1-1` by default, through
`VoltageLimits.OperationalLowFor` and `OperationalHighFor`, which fall back to the operational band on a side the bus
leaves null; `--global-vlimits` restores the single band. The API default, `PowerFlowOptions.PerBusVoltageLimits`, is
false, and the CLI sets it unless `--global-vlimits` is given.

## Invariants

| Invariant | Test |
|---|---|
| The branch stamp equals a separately written reference of the textbook pi-model with a from-side tap, bit for bit (the reference repeats the production formulas, so it guards drift, not the convention), for a plain line, a lossless reactance, an off-nominal tap, a phase shifter and a tap at or below 0 | `AdmittanceStampPinTests.Branch_stamp_matches_an_independent_textbook_derivation_bit_for_bit` |
| `YBus.BuildSparse` cells equal `YBus.Build` cells bit for bit, and an absent cell is exactly zero in the dense matrix (case9, case30, case118, case300) | `YBusSparseEquivalencePinTests.BuildSparse_cells_are_bit_for_bit_the_dense_build` |
| The CSR builder is byte-identical to a reference for shuffled and duplicate entries (a six-row case with at most three entries in a row, so the order above 16 entries is not exercised there), and the fundamental `Y(h)` build agrees with the dense Y-bus to within `1e-9` | `ComplexCsrBuilderPinTests.Build_is_byte_identical_to_a_reference_for_shuffled_and_duplicate_entries`, `Fundamental_Y_h_build_is_unchanged_by_the_sort_port` |
| The dense and sparse Newton engines give the same bits on built-in, MATPOWER and JSON cases | `DenseSparseParityTests.Built_in_case_is_bit_identical_dense_vs_sparse`, `Matpower_file_is_bit_identical_dense_vs_sparse`, `Json_case_is_bit_identical_dense_vs_sparse` |
| Rows above 16 entries sum in dense order: the largest bundled buses (17 incident branches in `case1354pegase.m` and `case2869pegase.m`) solve bit-identically on both engines | `DenseSparseParityTests.Matpower_file_is_bit_identical_dense_vs_sparse` |
| An out-of-service branch is skipped by the sparse Y as by the dense one | `SparsePowerFlowTests.Matches_dense_when_a_branch_is_out_of_service` |
| The island pre-pass is a no-op with every branch in service, elects a slack for a generating island, blacks out a dead island and books its lost load, and the adjusted model solves | `IslandPartitionTests.All_in_service_is_a_no_op`, `Islanding_a_generating_region_elects_a_slack`, `Islanding_a_dead_region_blacks_it_out_and_books_the_lost_load`, `The_adjusted_islanded_model_solves` |
| `CopyWith` knows every settable `GridModel` property, so a new property cannot be silently dropped | `GridModelCopyTests.CopyWith_knows_every_settable_property` |
| `CopyWith()` carries every property by reference, and replaces only what it is given | `GridModelCopyTests.CopyWith_with_no_arguments_carries_every_property_by_reference`, `CopyWith_replaces_only_what_it_is_given` |
| A MATPOWER status 0 generator is kept and marked out of service; solving with it present is bit-identical to solving without it; an all-in-service fleet is returned by reference; `CopyWith` keeps the out-of-service units | `GeneratorInServiceTests.A_status_0_matpower_generator_is_kept_and_marked_out_of_service`, `Solving_a_model_with_out_of_service_units_is_bit_identical_to_solving_without_them`, `An_all_in_service_fleet_is_returned_by_reference`, `CopyWith_carries_out_of_service_units_and_keeps_the_in_service_list` |
| A JSON unit marked out of service is kept, not solved and does not type its bus; a case whose only slack unit is out of service is refused | `GeneratorInServiceTests.A_json_unit_marked_out_of_service_is_kept_but_not_solved_and_does_not_type_its_bus`, `A_json_case_whose_only_slack_unit_is_out_of_service_is_refused` |
| Derived models (scaled loads, warm-started copies, scenarios, laid-out copies, opened branches) keep the out-of-service units | `DerivedModelsKeepOutOfServiceUnitsTests` |
| Duplicate bus ids, a missing slack, dangling branch or generator references and a non-finite branch reactance are errors; several slacks and infinite Q limits are not; case9 validates clean. The other non-finite checks and the OLTC "nonexistent bus" and "not an endpoint" warnings have no test | `GridModelValidateTests` (`Duplicate_bus_id_is_an_error`, `Missing_slack_is_an_error`, `Multiple_slacks_is_a_warning_not_an_error`, `Dangling_branch_endpoint_is_an_error`, `Generator_on_nonexistent_bus_is_an_error`, `Non_finite_branch_reactance_is_an_error`, `Infinite_generator_q_limits_are_not_flagged`, `Clean_case9_produces_no_diagnostics`) |
| An out-of-service generator on a missing bus is an error | `GeneratorInServiceTests.Validate_refuses_an_out_of_service_unit_on_a_missing_bus` |
| OLTC warnings: missing transformer, HV-side control, clean LV-side control | `GridModelValidateTests.Oltc_naming_a_nonexistent_transformer_warns`, `Oltc_on_the_hv_side_warns`, `Oltc_on_the_lv_side_is_clean` |
| Per-bus limits are read from MATPOWER columns 12 and 13, a row too short to carry them leaves the global band silently, an unusable pair leaves it with a note, and `Validate` refuses unusable limits | `PerBusVoltageLimitTests.Matpower_bus_columns_12_and_13_are_read_as_vmax_and_vmin`, `A_short_or_unusable_limit_pair_leaves_the_bus_on_the_global_band_with_a_note`, `Validate_refuses_unusable_per_bus_limits` |
| A ZIP block loads exactly, a missing or non-finite coefficient or a triple not summing to 1 is refused, and a decimal triple with binary rounding is accepted | `ZipLoadWiringTests.A_valid_zip_block_loads_onto_the_bus_exactly`, `A_zip_missing_a_coefficient_fails_loud_naming_the_bus_and_key`, `A_zip_real_triple_not_summing_to_one_fails_loud`, `A_decimal_triple_with_binary_rounding_is_accepted` |
| Boundary branch names must resolve to exactly one branch in code, JSON, the bridge, `Validate` and the studies | `BoundaryNamesInCodeTests` |
| A raw feeder parses to exactly the model the product paths built, every other bundled file parses as before, `case33bw` solves to the published Baran and Wu values, an undefined unit handling is refused, and a file that still looks raw after one conversion is refused | `N165MatpowerUnitDefaultTests` |
| Export and import round trip, for one decorated case9 model, the ZIP load, a generator `Pmin`, branch `R0` and `X0`, the windings, `ToNeutralXPu`, the OLTCs and one boundary; an opened branch stays out of service; and that model re-solves to the bit-identical state. `VaDeg` and the voltage limits are not carried (see Limits) | `SolveExportRoundTripTests.Every_carried_network_field_round_trips_exactly`, `An_opened_branch_stays_out_of_service_through_export_and_import`, `The_reimported_model_resolves_to_the_bit_identical_state` |
| A bundled case loaded through its v2 projection gives the v1 model and solve bit for bit | `V2CaseLoaderTests.A_bundled_case_loaded_through_its_v2_projection_gives_the_v1_model_and_solve_bit_for_bit` |
| The MATPOWER parser classifies a branch as line or transformer by its ratio, drops isolated buses and out-of-service branches, handles non-contiguous ids, and refuses a case with no reference bus | `MatpowerParserTests.Line_vs_transformer_by_ratio`, `Drops_isolated_buses_and_incident_elements`, `Skips_out_of_service_elements`, `Handles_noncontiguous_bus_ids`, `Throws_when_no_reference_bus` |

## Worked example

The example builds by hand the Y-bus entries of one line and one transformer of `case14` and compares them with the
Y-bus triplets of a real run. The run writes a solve export, which holds the model as solved and the Y-bus as
`ybus` triplets (`I`, `J` are zero-based matrix indices, `G` and `B` the real and imaginary parts, per-unit):

```
gridsim case14 --once --enableExport
```

```
$ gridsim case14 --once --enableExport
[export] dense-newton: converged=True iters=2 maxMis=1.32E-010  ->  results\exports\case14-solve.json
[exit 0]
```

The iteration count and mismatch are as captured; they and any timing differ by machine and run. The captures show the
export path relative to the repository; the command prints the absolute path of the file. The model rows used
below, from the export's `model` block (`baseMva` is 100):

```
$ python -c "import json;m=json.load(open('results/exports/case14-solve.json'))['model'];print(m['baseMva']);[print(json.dumps(b)) for b in m['branches'] if (b['from'],b['to']) in {(1,2),(4,7)}];print(json.dumps(m['buses'][8]))"
100
{"from": 1, "to": 2, "r": 0.01938, "x": 0.05917, "b": 0.0528, "tap": 1, "phaseShiftDeg": 0, "rateAMva": 0, "isTransformer": false, "inService": true, "r0": 0, "x0": 0, "fromNeutralXPu": 0, "toNeutralXPu": 0, "lengthKm": 0, "rateBMva": 0, "rateCMva": 0}
{"from": 4, "to": 7, "r": 0, "x": 0.20912, "b": 0, "tap": 0.978, "phaseShiftDeg": 0, "rateAMva": 0, "isTransformer": true, "inService": true, "r0": 0, "x0": 0, "fromNeutralXPu": 0, "toNeutralXPu": 0, "lengthKm": 0, "rateBMva": 0, "rateCMva": 0}
{"id": 9, "type": "Pq", "baseKv": 0, "pd": 29.5, "qd": 16.6, "gs": 0, "bs": 19, "vm": 1.056, "vaDeg": -14.94, "x": 0, "y": 0, "vminPu": 0.94, "vmaxPu": 1.06}
[exit 0]
```

`case14` is the built-in IEEE case, so every `baseKv` is 0: the per-unit solve does not need it. The exported Y-bus
cells for the buses involved (bus `k` is matrix index `k - 1`):

```
$ python -c "import json;[print(json.dumps(t)) for t in json.load(open('results/exports/case14-solve.json'))['solve']['ybus'] if (t['I'],t['J']) in {(0,0),(0,1),(3,3),(3,6),(6,3),(6,6),(8,8)}]"
{"I": 0, "J": 0, "G": 6.025029055768224, "B": -19.447070205514382}
{"I": 0, "J": 1, "G": -4.999131600798035, "B": 15.263086523179553}
{"I": 3, "J": 3, "G": 10.512989522036175, "B": -38.654171207607796}
{"I": 3, "J": 6, "G": 0, "B": 4.889512660317341}
{"I": 6, "J": 3, "G": 0, "B": 4.889512660317341}
{"I": 6, "J": 6, "G": 0, "B": -19.549005948264654}
{"I": 8, "J": 8, "G": 5.3260550394673585, "B": -24.092506375267877}
[exit 0]
```

**The line, bus 1 to bus 2.** `R = 0.01938`, `X = 0.05917`, `B = 0.0528`, tap 1.0, shift 0.

```
|z|^2 = 0.01938^2 + 0.05917^2 = 0.0038766733
ys    = 1/(0.01938 + j0.05917) = 4.999131600798035 - j15.263086523179553
bc    = j * 0.0528 / 2         = j0.0264
a = 1,  tap = 1

Yff = Ytt = ys + bc            = 4.999131600798035 - j15.236686523179552
Yft = Ytf = -ys                = -4.999131600798035 + j15.263086523179553
```

`Y[1,2]` is only this branch, so the exported cell is the stamp: `G = -4.999131600798035`,
`B = 15.263086523179553`, equal to the last printed digit. `Y[1,1]` collects the from end of the two branches that leave
bus 1 (1-2 and 1-5; bus 1 has no shunt). Branch 1-5 gives `1.025897454970189 - j4.210383682334831`, so

```
Y[1,1] = (4.999131600798035 - j15.236686523179552) + (1.025897454970189 - j4.210383682334831)
       =  6.025029055768224 - j19.447070205514382
```

which is the exported `I = 0, J = 0` cell.

**The transformer, bus 4 to bus 7.** `R = 0`, `X = 0.20912`, `B = 0`, `TapRatio = 0.978`, shift 0, so
`tap = 0.978` and `a^2 = 0.956484`.

```
ys  = 1/(j0.20912)             = -j4.781943381790359
bc  = 0
Yff = ys / a^2 = ys / 0.956484 = -j4.999501697665993     stamped on Y[4,4]
Ytt = ys                       = -j4.781943381790359     stamped on Y[7,7]
Yft = -ys / conj(tap)          = +j4.889512660317341     = ys * (-1/0.978), Y[4,7]
Ytf = -ys / tap                = +j4.889512660317341     Y[7,4]
```

The exported cells `I = 3, J = 6` and `I = 6, J = 3` hold `G = 0`, `B = 4.889512660317341`, equal to `Yft` and `Ytf`.
The off-diagonal terms are scaled by `1/a` and the from self term by `1/a^2`: `|Yft| = 4.78194 / 0.978`, and `Yff` is
larger than `Ytt` by the factor `1/0.956484`. Bus 7 has no shunt and three branches, 4-7 (to end), 7-8 and 7-9 (from end,
both untapped, no charging), which contribute `-j4.781943381790359`, `-j5.676979846721544` and `-j9.09008271975275`; the
sum, added in branch order, is `-j19.549005948264654`, the exported `I = 6, J = 6` cell. Bus 4 collects five stamps in
branch order:

```
2-4  Ytt   1.686033150614943  - j5.098838325872083
3-4  Ytt   1.9859757099255606 - j5.06241697759392
4-5  Yff   6.840980661495671  - j21.578553981691588
4-7  Yff   0                  - j4.999501697665993      (the tapped from end, 1/a^2)
4-9  Yff   0                  - j1.914860224784211      (tap 0.969, 1/a^2)
sum        10.512989522036175 - j38.654171207607796     = exported I = 3, J = 3
```

**The shunt.** Bus 9 has `Bs = 19` MVAr, so `19 / 100 = j0.19` pu is added last to `Y[9,9]`. The branch stamps at bus 9 are
the to end of 4-9 (`-j1.7979790715236075`), the to end of 7-9 (`-j9.09008271975275`) and the from ends of 9-10
(`3.9020495524474277 - j10.365394127060915`) and 9-14 (`1.424005487019931 - j3.0290504569306034`). Their sum plus the
shunt is `5.3260550394673585 - j24.092506375267877`, the exported `I = 8, J = 8` cell. Without the shunt the imaginary
part would be `-24.282506375267877`.

Every hand-computed cell equals the exported cell to the printed digits. `AdmittanceStampPinTests` and
`YBusSparseEquivalencePinTests` assert the stamp and the sparse-dense equality in IEEE doubles, not to printed digits.

### Unit normalisation of a raw feeder

`case33bw` stores its branch impedance in ohms and its demand in kW and kVAr. The parse converts it and says so:

```
$ gridsim data/matpower/dist/case33bw.m --once --enableExport
[export] bfs: converged=True iters=8 maxMis=6.82E-011  ->  results\exports\case33bw-solve.json
note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
note: case33bw.m: skipped 5 out-of-service branch(es).
note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
[exit 0]
```

The raw rows, then the same rows in the model the solver received:

```
$ grep -nE '^\s(2\s1\s100|1\s2\s0.0922)' data/matpower/dist/case33bw.m
23:	2	1	100	60	0	0	1	1	0	12.66	1	1.1	0.9;
66:	1	2	0.0922	0.0470	0	0	0	0	0	0	1	-360	360;
[exit 0]
```

```
$ python -c "import json;m=json.load(open('results/exports/case33bw-solve.json'))['model'];print(m['baseMva']);print(json.dumps(m['buses'][1]));print(json.dumps(m['branches'][0]))"
10
{"id": 2, "type": "Pq", "baseKv": 12.66, "pd": 0.1, "qd": 0.06, "gs": 0, "bs": 0, "vm": 1, "vaDeg": 0, "x": 0, "y": 0, "vminPu": 0.9, "vmaxPu": 1.1}
{"from": 1, "to": 2, "r": 0.005752591161723931, "x": 0.002932448856844086, "b": 0, "tap": 1, "phaseShiftDeg": 0, "rateAMva": 0, "isTransformer": false, "inService": true, "r0": 0, "x0": 0, "fromNeutralXPu": 0, "toNeutralXPu": 0, "lengthKm": 0, "rateBMva": 0, "rateCMva": 0}
[exit 0]
```

With `BaseMva = 10` and `BaseKv = 12.66`, `Zbase = 12.66^2 / 10 = 16.02756` ohm, so `R = 0.0922 / 16.02756 =
0.005752591161723931` pu and `X = 0.0470 / 16.02756 = 0.002932448856844086` pu; the 100 kW, 60 kVAr load of bus 2 is
`0.1` MW and `0.06` MVAr. The five skipped branches are the file's status 0 tie switches. The export solved this radial
feeder with the backward/forward sweep (`bfs`).

## Source map

| File | Type or function | Responsibility |
|---|---|---|
| `src/GridSim.Core/Model/Model.cs` | `GridModel`, `Bus`, `Generator`, `Branch`, `Oltc`, `Boundary`, `BoundaryBranch`, `ZipLoad`, `GeneratorCost`, `WindingConnection`, `ConverterPlant`, `GridDiagnostic` | The model, `Validate`, `CopyWith`, the generator fleet split |
| `src/GridSim.Core/PowerFlow/AdmittanceStamp.cs` | `AdmittanceStamp.Branch`, `FromAdmittance`, `BranchStamp` | The single pi-model stamp with tap and phase shift |
| `src/GridSim.Core/PowerFlow/YBus.cs` | `YBus.Build`, `YBus.BuildSparse`, `SparseYBus` | Dense and cell-only Y-bus |
| `src/GridSim.Core/PowerFlow/SparseNewtonRaphsonPowerFlow.cs` | `BuildY` | Sparse (CSR) Y-bus for the sparse engine |
| `src/GridSim.Core/Numerics/Sparse.cs` | `ComplexCsr`, `ComplexCsr.Builder` | CSR storage and the order-preserving summation |
| `src/GridSim.Core/PowerFlow/NewtonShell.cs` | `BuildBusSpec`, `ComputeLineFlows`, `AssembleResult` | Per-unit injections, start point, flows back in MW |
| `src/GridSim.Core/PowerFlow/VoltageSetpoint.cs` | `VoltageSetpoint.ForBus` | The last-generator-wins setpoint rule |
| `src/GridSim.Core/PowerFlow/LoadAtVoltage.cs` | `LoadAtVoltage` | The one ZIP load rule |
| `src/GridSim.Core/PowerFlow/IslandPartition.cs`, `TopologyOps.cs` | `IslandPartition.Prepare`, `TopologyOps.Apply` | Island election and blackout, opened-branch application |
| `src/GridSim.Core/IO/Matpower/MatpowerParser.cs` | `MatpowerParser`, `MatpowerUnitHandling` | MATPOWER import, `gencost`, `dcline`, unit handling |
| `src/GridSim.Core/IO/Matpower/MatpowerUnitNormalizer.cs` | `LooksRawUnits`, `Normalize` | Raw ohm and kW feeder detection and conversion |
| `src/GridSim.Core/IO/Matpower/MatpowerWriter.cs` | `MatpowerWriter` | MATPOWER export and its loss notes |
| `src/GridSim.Core/IO/Json/JsonGridLoader.cs`, `JsonDtos.cs` | `JsonGridLoader.Build`, DTOs | JSON v1 import and the shared builder |
| `src/GridSim.Core/IO/Json/V2CaseLoader.cs` | `V2CaseLoader` | JSON v2 case import onto the v1 builder |
| `src/GridSim.Core/IO/Json/JsonCaseWriter.cs` | `JsonCaseWriter` | JSON v1 export and its loss notes |
| `src/GridSim.Core/IO/Cim/CimGridModelBridge.cs` | `ToGridModel` | CIM to model, ohm to per-unit |
| `src/GridSim.Gda/GdaModelBuilder.cs` | `GdaModelBuilder` | GDA dataset to model |
| `src/GridSim.Core/IO/BridgeProtocol.cs` | `BuildModel` | Bridge and solve-export model reader |
| `src/GridSim.Core/Dispatch/Dispatcher.cs` | `Clone`, `Balance` | Copy through `CopyWith`, dispatch of the in-service fleet |
| `src/GridSim.Core/Assets/AssetBinding.cs`, `AssetDynamicsFleet.cs` | `AssetBindingSet.Resolve`, `AssetDynamicsModel` | Asset class bindings |
| `src/GridSim.Core/Assets/Protection/ProtectionModel.cs` | `ProtectionModel`, `ProtectionInstance` | Placed protective devices |
| `src/GridSim.Core/Analysis/VoltageLimits.cs` | `OperationalLowFor`, `OperationalHighFor` | Per-bus limits with band fallback |

## Limits

- A transformer has no magnetising shunt: `Branch` carries one `B`, and every loader sets it to 0 for a transformer.
- JSON v1 and v2 cannot hold an out-of-service branch (the v2 loader refuses `inService` false on a line or
  transformer rather than dropping it). JSON v1 cannot hold transformer winding connections, neutral reactances,
  transformer zero-sequence data or generator subtransient and negative-sequence reactance; JSON v2 carries them into the
  model, as the bridge and solve-export reader do.
- MATPOWER drops status 0 branches and isolated (type 4) buses at parse time, so a parsed case never holds an open
  branch; MATPOWER, CIM and GDA carry no windings, zero-sequence data or ZIP load, and MATPOWER and CIM carry no OLTC.
- The bridge and solve-export reader does not read a bus `VaDeg` or its voltage limits, so a re-imported slack with a
  non-zero angle, or a case with per-bus limits, does not round trip those fields.
- `Generator.ReactiveCost` is carried and written by the MATPOWER writer, and no solver reads it.
- `MatpowerParser` keeps `mpc.dcline` in the parse result only; no solver models a DC line, so a case with DC lines
  solves differently from MATPOWER.
- `ProtectionCascade.WithBranches` and `GridInertiaTransforms.With` build their copies by hand from the in-service
  generator list, so those copies do not hold the out-of-service units of the model they copy.
- The island pre-pass is applied only by `TopologyOps.Apply`, which the live simulator calls for its opened branches.

## See also

- [ch. 3, Power flow](<0003 Power flow.md>) - how the solvers consume these fields.
- [ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) - the sparse Y-bus and Jacobian storage.
- [ch. 10, File and wire formats](<0010 File and wire formats.md>) - the schemas that fill the model.
- [ch. 28, The v2 JSON schema](<0028 The v2 JSON schema.md>) - the v2 case and network files.
- [ch. 1, Architecture](<0001 Architecture.md>) - where models come from and go.
- [Loading networks](<../manual/0002 Loading networks.md>) - the case formats in practice.
- [Exports and formats](<../manual/0031 Exports and formats.md>) - how model fields appear in exports.
