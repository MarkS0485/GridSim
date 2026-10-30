# 17. Protection assessment (a passive relay overlay)

This chapter specifies the passive protection overlay: a library of relay elements that read a solved state and
report which of them would operate and why, without altering the solve. It covers the registry of relay functions
and the equation and settings of each, the operating quantities every relay reads, the balanced and unbalanced
fault studies that give the overlay a real fault to judge, the cascade simulation that follows a trip through the
network, the sequence-of-events cross-reference against a real alarm log, and the `protection-check` and `fault`
verbs that expose them.

The overlay is read-only. It never calls the power-flow solver on the caller's model, never mutates the model or the
result, and is a pure function of its inputs, so the same state and settings give the same verdicts on every run.
The operating quantities (voltages, currents, loadings, apparent impedances, busbar residuals) always come from the
solved state. The pickups they are compared against are generic standard-practice values, each graded
(`ProtectionConfidence`):

| Grade | Meaning | Elements |
|---|---|---|
| `Grounded` | A statutory or grid-code GB value. | 81U, 81O, 81R |
| `Generic` | A standard-practice default that a real scheme would grade per site. | 27, 59, 32, 37, 49, 50, 51, 21, 78, 87, 50N, 51N, 21N, 46 |
| `NotCheckable` | The element needs data the state does not carry; the verdict says so and never operates. | 81R and 78 without a trajectory; the earth and negative-sequence group without an unbalanced study |

This is a screening tool, not a certified protection study: relay settings are site-specific and are not public.
The scope boundary with neighbouring chapters is fixed. The protection stages of an asset that act during a
simulation and change the trajectory (infeed trip, load shed, branch opening) are
[ch. 15, Protections](<0015 Protections.md>). The IEC 60909 short-circuit duty (Ik'', ip, Ib, Ith, the breaker
adequacy check) is [ch. 25, Short-circuit IEC 60909](<0025 IEC 60909 short-circuit duty study.md>). The grading of
inverse-time relay chains (`gridsim grade`, the curve table, the coordination time interval) is
[ch. 26, Overcurrent coordination](<0026 Overcurrent coordination (IDMT grading).md>). This chapter uses their results and does
not repeat them.

## Model

### Assessment entry points

`ProtectionAssessment` (`src/GridSim.Core/Protection/ProtectionAssessment.cs`) has three entry points.

| Entry point | What it does |
|---|---|
| `Assess(model, result, settings?, trajectory?, faultCurrentPuByBus?, sequence?)` | Requires a converged `result`, builds one `ProtectionContext`, runs every relay in `ProtectionRegistry.All` over it and returns a `ProtectionReport`. |
| `AssessThreePhaseFault(model, prefault, faultBusId, faultReactancePu = 0, settings?)` | Requires a converged prefault state, runs `FaultAnalysis.ThreePhase`, then `Assess` on the faulted state with the fault current passed to the busbar differential. Returns the `FaultResult` and the report. |
| `AssessUnbalancedFault(model, prefault, faultBusId, type, faultReactancePu = 0, settings?)` | The same for `FaultAnalysis.Unbalanced`: `Assess` runs on the faulted positive-sequence state with the `UnbalancedFaultResult` as the `sequence` argument, and the busbar differential sees the largest phase fault current at the faulted bus. |

Every entry point starts with `ConvergenceContract.RequireConverged`
(`src/GridSim.Core/PowerFlow/ConvergenceContract.cs`), which throws `NonConvergedStateException` naming the study
(`Protection assessment`, `Protection assessment (fault)`, `Fault analysis`, `Protection cascade`), the status, the
mismatch and the iteration count. A non-converged state never gets a verdict.

`ProtectionReport` holds `Verdicts` (every relay's output), `Settings` (the settings used), `HadTrajectory`, and three
views: `Operating` (the verdicts with `Operate = true`: timed elements first, fastest first; then the untimed ones,
largest margin relative to the pickup (`ProtectionReport.RelativeMargin`, the margin over `|Pickup|`, or the raw
margin when the pickup is zero or not finite) first; ties keep the verdict row order), `AnyOperate` and
`OperatingByFunction` (a count per function name). `PrimaryClearance` is the element that clears the fault first: an
operating busbar differential (87) when there is one, otherwise the first operating branch element with an operating
time (49, 50, 51, the 21 zones, 50N, 51N, the 21N zones, 46), and null when neither operates.

### RelayVerdict

Every relay returns `RelayVerdict` records:

| Field | Meaning |
|---|---|
| `Device` | ANSI device number as shown, for example `27`, `51`, `21-Z2`, `21N-Z3`. |
| `Function` | Human name, for example `Undervoltage`. |
| `LocationKind`, `LocationId`, `LocationName` | `Bus` (bus id), `Branch` (branch index in `Model.Branches`, equal to the `LineFlow` index), `Generator` (generator index) or `System` (`-1`, `system`). |
| `Operate` | Whether the element picks up. |
| `Quantity`, `Unit` | The operating quantity and its unit (`pu`, `xRateA`, `xIn`, `MW`, `Hz`, `Hz/s`, `deg`, and `-` for the not-checkable ground verdict, whose quantity and pickup are NaN). |
| `Pickup` | The setting the quantity is compared against. |
| `Margin` | Signed distance past pickup in the quantity's own unit; positive once operated. |
| `TimeToTripS` | Operating time in seconds where the element has a curve or a definite time; `0.0` for instantaneous; `null` where there is none. |
| `Basis` | The plain-language sentence, for example `Vm 0.000 pu at bus 7 is below the 0.80 pu undervoltage pickup`. |
| `Confidence` | The grade above. |

A relay that scans many locations (every bus, every branch) emits every operating verdict plus the single closest
non-operating one (`VerdictCollector`: the non-operating verdict with the largest, least negative, margin), so a
report can say both where an element fires and how near the next location is to firing. 81U, 81O, 81R and 78 are
system elements and return one `System` verdict each. The ground group returns one `System` verdict when there is no
sequence study, and the collector pattern per branch when there is one; each of these has one
`System` verdict.

### The operating quantities

`ProtectionContext` (`src/GridSim.Core/Protection/ProtectionContext.cs`) computes each quantity once from the result
and the model. All are positive-sequence phasor values in per unit on `GridModel.BaseMva`.

```
Vm[i]            = result.Vm[i]                                         per bus
S_from, S_to     = sqrt(P^2 + Q^2) at each end, from LineFlow, MVA
IFromPu[l]       = (S_from / BaseMva) / Vm_from      (0 if Vm_from <= 1e-9)
IToPu[l]         = (S_to   / BaseMva) / Vm_to        (0 if Vm_to   <= 1e-9)
IRatedPu[l]      = RatingMva / BaseMva               (0 if RatingMva <= 0)
LoadingFraction  = max(S_from, S_to) / RatingMva     (0 if unrated)
ZAppFrom[l]      = Vm_from^2 / conj(P_from + jQ_from)   in pu on BaseMva
ZAppTo[l]        = Vm_to^2   / conj(P_to   + jQ_to)
                   (infinite if |S| <= 1e-12 pu: no measurable flow)
ZLine[l]         = R + jX of the branch (series impedance, pu)
```

`LineFlow` records the power flowing into the branch at each end, so each `ZApp` is what the relay at that end sees
looking into the line. The thermal loading is taken on the heavier end, the same `max(|S_from|, |S_to|) / RateA`
that `LineFlow.LoadingPercent` carries on solver results and on the faulted state `FaultAnalysis.FaultedFlows`
returns (the context forms its own loading from both ends in any case), so no verdict depends on which way round a branch is stored. A line flow
that matches no branch of the model (a result solved on a different model) throws `InvalidOperationException` naming
the flow; the context never borrows the impedance of another branch.

The busbar differential quantity is the Kirchhoff current residual at each bus, with the bus shunt netted off so a
capacitor bank does not read as a fault:

```
netP[i] = PInj[i]/BaseMva - Gs[i]/BaseMva * Vm[i]^2
netQ[i] = QInj[i]/BaseMva + Bs[i]/BaseMva * Vm[i]^2
for each LineFlow: netP[from] -= P_from/BaseMva;  netQ[from] -= Q_from/BaseMva
                   netP[to]   -= P_to/BaseMva;    netQ[to]   -= Q_to/BaseMva
DiffCurrentPu[i] = sqrt(netP^2 + netQ^2) / Vm[i]       (no division if Vm <= 1e-9)
```

On a converged solve this is rounding error. When a fault study supplies `faultCurrentPuByBus`, the faulted bus
takes `max(DiffCurrentPu[i], If)`, because a fault inside a bus zone is a current path the relay sees as the sum of
its circuit currents and that the nodal balance does not contain.

The per-branch settings are resolved through `ProtectionContext.SettingsFor(branchIndex)`: the record in
`ProtectionSettings.PerAsset` whose key is the branch's `LineFlow.Name`, else the generic settings. A branch with an
empty name always gets the generic settings. Elements 49, 50,
51, 21, 50N, 51N, 46 and 21N read the branch's own settings; 27, 59, 32, 37, 81x, 78 and 87 read the generic ones.

### The registry

`ProtectionRegistry.All` (`src/GridSim.Core/Protection/ProtectionRelays.cs`) runs the relays in this order. The order
is the order of verdicts in `ProtectionReport.Verdicts`.

| # | Class | Device | Function | Location | Operating quantity | Operates when |
|---|---|---|---|---|---|---|
| 1 | `UnderVoltageRelay` | 27 | Undervoltage | each bus | lowest phase voltage `VmPhaseMinPu` (equal to `Vm` without a sequence study), pu | `vm < UnderVoltagePu` |
| 2 | `OverVoltageRelay` | 59 | Overvoltage | each bus | highest phase voltage `VmPhaseMaxPu`, pu | `vm > OverVoltagePu` |
| 3 | `DirectionalPowerRelay` | 32 | Reverse power | each generator | the generator's scheduled `Pg`, MW | `Pg < -ReversePowerMw` |
| 4 | `UnderCurrentRelay` | 37 | Undercurrent | each branch | `max(IFromPu, IToPu)`, pu | `i < UnderCurrentFloorPu` |
| 5 | `ThermalOverloadRelay` | 49 | Thermal overload | each rated branch | `LoadingFraction`, xRateA | `L > ThermalPickupFraction` |
| 6 | `InstantaneousOvercurrentRelay` | 50 | Instantaneous overcurrent | each rated branch | `max(IFromPu, IToPu) / IRatedPu`, xIn | `m > InstantaneousOcMultiple` |
| 7 | `TimeOvercurrentRelay` | 51 | Time overcurrent | each rated branch | `max(IFromPu, IToPu) / IRatedPu`, xIn | `m / TimeOcMultiple > 1` |
| 8 | `DistanceRelay` | 21, `21-Z1` to `21-Z3` | Distance | each line | `ZApp` at each end, pu | `ZApp` inside a forward zone |
| 9 | `UnderFrequencyRelay` | 81U | Underfrequency | system | run minimum frequency, else `Model.FrequencyHz`, Hz | `f < UnderFrequencyHz` |
| 10 | `OverFrequencyRelay` | 81O | Overfrequency | system | run maximum frequency, else `Model.FrequencyHz`, Hz | `f > OverFrequencyHz` |
| 11 | `RocofRelay` | 81R | RoCoF | system | trajectory peak `|df/dt|`, Hz/s | `r > RocofTripHzPerS` |
| 12 | `VectorShiftLomRelay` | 78 | Vector shift / LoM | system | trajectory peak vector shift, deg | `vs > VectorShiftTripDeg` |
| 13 | `DifferentialRelay` | 87 | Differential | each bus | `DiffCurrentPu`, pu | `d > DifferentialPickupPu` |
| 14 | `GroundFaultRelay` | 50N, 51N, 21N, 46 | Ground / negative-sequence | see below | needs `Sequence` | see below |

The registry is not in ascending device-number order (the source comment says so and gives the grouping); the table is the
order the code runs.

Rated branches are those with `RatingMva > 0`; 49, 50 and 51 skip an unrated branch. 21 skips a line whose series
impedance magnitude is at or below `DistanceRelay.MinLineImpedancePu` (`1e-9` pu), a bus coupler with no usable
reach. 32 reads `Generator.Pg` of the model, the scheduled output, not a solved output.

### Relay equations

**27 and 59.** Margin is `pickup - vm` for 27 and `vm - pickup` for 59.

**32.** Margin is `-Pg - ReversePowerMw`. The reported pickup is `-ReversePowerMw`.

**37.** The floor is an absolute current in per unit, not a fraction of rating. It detects a branch that carries essentially no current (an open-ended or de-energised circuit). It skips an
out-of-service branch (which keeps a zero-flow entry) with no verdict, so a switched-out circuit does not operate it; a lightly loaded line carries a small
real current and does not operate it. Margin is `floor - i`.

**49 thermal overload.** With `L = LoadingFraction`, `P = ThermalPickupFraction`, `tau = ThermalTimeConstantS`:

```
operate:  L > P
t = tau * ln(L^2 / (L^2 - P^2))          first-order thermal replica, cold start
```

`TimeToTripS` is `t`. The loading is the loading before the trip with no preload history: the replica starts cold.

**50 instantaneous overcurrent.**

```
m = max(IFromPu, IToPu) / IRatedPu
operate:  m > InstantaneousOcMultiple        TimeToTripS = 0.0 (printed "inst")
```

**51 time overcurrent.** The IEC 60255 standard-inverse curve with its argument `M` the current as a multiple of
the pickup setting, not of the rated current:

```
m = max(IFromPu, IToPu) / IRatedPu
M = m / TimeOcMultiple
operate:  M > 1
t = TimeOcTms * 0.14 / (M^0.02 - 1)
```

The constants `k = 0.14` and `alpha = 0.02` come from the shared curve table
`OvercurrentCoordination.IecConstants(OvercurrentCurve.IecStandardInverse)`
(`src/GridSim.Core/Protection/Coordination/`), the table [ch. 26](<0026 Overcurrent coordination (IDMT grading).md>) owns.
Margin is `m - TimeOcMultiple`. The time tends to infinity as `M` tends to 1.

**21 distance.** Each line carries a relay at both ends. For one end:

```
angle test:  |angle(ZApp) - angle(ZLine)| <= DistanceAngleWindowDeg          (forward)
zone:        forward and |ZApp| <= DistanceZone1Reach * |ZLine|   -> Zone 1, t = DistanceZone1TimeS
             else           |ZApp| <= DistanceZone2Reach * |ZLine|   -> Zone 2, t = DistanceZone2TimeS
             else           |ZApp| <= DistanceZone3Reach * |ZLine|   -> Zone 3, t = DistanceZone3TimeS
margin:      (DistanceZone3Reach * |ZLine| - |ZApp|) / |ZLine|   if forward, else -infinity
```

A reverse element never operates and never wins the "closest to firing" pick. One verdict is reported per line,
from the end that decides: an end that operates beats one that does not, a lower zone beats a higher zone, between
two non-operating ends the larger margin wins, and a tie keeps the From end. A verdict from the To end says so in its
basis (`relay at the to end, bus N`). The device reads `21-Z1`, `21-Z2` or `21-Z3` when it operates and `21`
otherwise. The reported pickup is the reach of the zone that operated, else the outer reach. Load encroachment shows
as a heavily loaded line whose apparent impedance shrinks into Zone 3; the element has no load blinder.

**81U, 81O.** The frequency is the trajectory's minimum (81U) or maximum (81O) when a `ProtectionTrajectory` is
supplied, else the model's nominal `FrequencyHz`. A steady state therefore operates 81U or 81O only if the setting is
moved across the nominal frequency.

**81R, 78.** Without a trajectory each reports `NotCheckable` with margin minus infinity and never operates: a
steady snapshot has no `df/dt` and no voltage-angle transient. With a trajectory the quantity is
`MaxAbsRocofHzPerS` (81R) or `MaxAbsVectorShiftDeg` (78), compared with `RocofTripHzPerS` and `VectorShiftTripDeg`.
`ProtectionTrajectory(MaxAbsRocofHzPerS, MinFrequencyHz, MaxFrequencyHz, MaxAbsVectorShiftDeg = 0)` is the
caller-supplied summary of a dynamic run.

**87 busbar differential.** Margin is `d - DifferentialPickupPu`. On a healthy converged state the residual is
rounding error and the element is secure; it operates once a fault current is injected at a bus.

**The ground group (50N, 51N, 21N, 46).** Over a positive-sequence state (a power flow, or the balanced fault) there
is no zero- or negative-sequence quantity to measure, and the group returns one `System` verdict, `NotCheckable`,
with the reason in its basis. When an `UnbalancedFaultResult` is supplied it evaluates four elements on the study's
sequence currents. Residual current is `|3 I0|`, negative-sequence current is `|I2|`, each taken as the larger of
the branch's two ends and expressed in xIn on `IRatedPu`:

```
50N  m = Ires / rated;   operate m > EarthFaultInstantaneousMultiple;  t = 0
51N  M = (Ires / rated) / EarthFaultTimeMultiple;  operate M > 1;
     t = EarthFaultTms * 0.14 / (M^0.02 - 1)                  same IEC curve as 51
46   m = max(|I2_from|, |I2_to|) / rated;  operate m > NegativeSequenceMultiple;
     t = NegativeSequenceTimeS                                definite time
21N  lines only, in service, |Z1L| > 1e-9:
       k0 = (Z0L - Z1L) / (3 Z1L)
       for each end, each phase p in {a, b, c}:  Z = V_p / (I_p + 3 k0 I0)
       zones, angle window and times as 21; the end and phase in the fastest zone decide
```

`Z0L` is the branch's `R0 + jX0` when either is non-zero, else `LineZeroSeqRatio * Z1L` (3.0). 21N skips
transformers. 50N, 51N and 46 skip unrated branches.

### Unbalanced studies on the phase elements

When a sequence study is supplied the phase elements measure phase quantities, not the positive sequence alone. The
phase phasors come from the sequence phasors with `a = e^(j 120 deg)`:

```
Ia = I0 + I1 + I2
Ib = I0 + a^2 I1 + a I2
Ic = I0 + a I1 + a^2 I2            (voltages the same way from V0, V1, V2)
```

Per branch end the current is the largest of `|Ia|`, `|Ib|`, `|Ic|` (`BranchSequenceCurrents.MaxPhaseFromPu`,
`MaxPhaseToPu`), so 37, 50 and 51 read it and 49's loading is the larger end's phase current over `IRatedPu`. The
context also computes a from-end power `Re(V1 conj(I1) + V2 conj(I2) + V0 conj(I0)) * BaseMva`
(`ProtectionContext.PFromMw`), but no shipped relay reads it; 32 reads the generator's `Pg`. 27 and 59
read each bus's lowest and highest phase-voltage magnitude. 21 measures the three phase-to-phase loops at each end,

```
Z = (Vx - Vy) / (Ix - Iy)        loops a-b, b-c, c-a      (infinite if |Ix - Iy| <= 1e-12)
```

and the loop in the fastest zone decides, named in the verdict (`[b-c loop]`). A power flow and a balanced fault are
assessed with the positive-sequence quantities, unchanged. The study and the state must have been solved on the same
model: a branch-count or endpoint mismatch throws `InvalidOperationException`.

## Algorithm

### The passive overlay on a solved state

```
Assess(model, result, settings, trajectory, faultCurrentPuByBus, sequence):
    result.RequireConverged("Protection assessment")
    s   = settings or ProtectionSettings defaults
    ctx = ProtectionContext(model, result, s, trajectory, faultCurrentPuByBus, sequence)
    verdicts = []
    for relay in ProtectionRegistry.All (order above):
        verdicts += relay.Evaluate(ctx)
    return ProtectionReport(verdicts, s, trajectory is not null)
```

No relay calls a solver. The assessment is a deterministic function of its arguments; it does not change them.

### Fault injection: balanced three-phase

`FaultAnalysis.ThreePhase(model, prefault, faultBusId, faultReactancePu = 0)`
(`src/GridSim.Core/Protection/FaultAnalysis.cs`) is the Thevenin bus-impedance superposition on the converged
prefault state. It throws `ArgumentException` for a bus that is not in the model, and `NonConvergedStateException`
for a non-converged prefault.

1. **Prefault voltages.** `V0[i] = Vm[i] e^(j VaDeg[i] pi/180)`, keyed to the model bus order.
2. **Fault-study admittance.** The network `Ybus` (branch pi-models with tap and phase shift, and bus shunts) plus
   diagonal stamps added in this order:
   - Each bus with `Pd` or `Qd` non-zero: a constant-impedance load, `Y = conj(Sload) / max(|V0|^2, 1e-6)` with
     `Sload = (Pd + jQd) / BaseMva`. The nominal load is used, not the solved voltage-dependent load.
   - Each synchronous generator that is not `PowerFlowOnly` and not converter-interfaced: its source reactance,
     `Y = 1 / (j Xsys)` with `Xsys = Generator.FaultSourceReactancePu(0.20) * BaseMva / MBase` (`MBase` at or below
     zero means `BaseMva`). `FaultSourceReactancePu` is the machine's `SubtransientXdPu` when it is above zero, else
     the synthesised default `FaultAnalysis.DefaultSubtransientXdPu` (0.20 pu), plus `max(0, UnitTransformerXPu)`.
     A source with `Xsys` at or below zero is not stamped. A `PowerFlowOnly` device is not a fault source.
3. **One Zbus column.** `FaultNetworkSystem.ForModel` factors the matrix once; `Zcol = Y^-1 e_k` for the fault bus
   `k`. `Zth = Zcol[k]`.
4. **Superposition**, with `Zf = j * faultReactancePu` (a pure reactance):

```
If        = V0_k / (Zth_kk + Zf)
V_faulted = V0 - Zcol * If
```

5. **Faulted flows.** Branch flows are formed from `V_faulted` with the same pi-model as `YBus`
   (`S = V conj(Y V) * BaseMva` at each end); an out-of-service branch keeps a zero-flow entry so `LineFlows` stays
   aligned with `Branches`. Nodal injections are `Ynet * V_faulted` over the unstamped network admittance, summed per
   row in ascending column order from the sparse cells, which is bit-for-bit the dense row sum
   (`FaultInjectionWalkPinTests`).
6. **Result.** `FaultResult`: the faulted `PowerFlowResult` (`Converged = true`, `Iterations = 0`,
   `MaxMismatch = 0`, justified because the prefault was required to converge and the faulted state is a direct
   linear solve), `FaultCurrentPu = |If|`, `FaultCurrentKa = |If| * BaseMva / (sqrt(3) * BaseKv)` when the fault bus
   has `BaseKv > 0` and 0 otherwise, `TheveninZPu = |Zth|`, `DifferentialCurrentPuByBus = { k: |If| }`, and the
   converter infeeds.

The fault admittance is factored by the sparse LU on the real 2n expansion (`FaultNetworkSystem`) at every size, so
a 9-bus case and `gb-full` run the same arithmetic; the dense complex LU is a test oracle only. A singular fault
network (an island with no source or earth path) fails loud with
`the fault-network admittance matrix is singular (an island with no source or earth path?)`.

#### Converter-interfaced units

A converter-interfaced unit (`Generator.IsConverterInterfaced`: an explicit `ConverterInterfaced` flag, else the
unit's `Type`) has no EMF behind a reactance. It is left open in the fault admittance and enters as a current-limited
injection on the grid-code fast-fault-current characteristic. Per unit, with `ir = MBase / BaseMva`,
`imax = k * ir` where `k` is `ConverterFaultCurrentPu` or the default `Generator.DefaultConverterFaultCurrentPu`
(1.2), and the prefault active and reactive currents `ip0 = Pg / BaseMva / |V0|`, `iq0 = Qg / BaseMva / |V0|`:

```
dip   = |V0| - |V|
q     = clamp(iq0 + K * dip * ir, -imax, imax)          K = DefaultConverterReactiveGain = 2
pRoom = sqrt(max(0, imax^2 - q^2))                       reactive priority
p     = sign(ip0) * min(|ip0|, pRoom)
I     = (p - j q) * e^(j theta)       theta = angle(V) if |V| > 1e-3, else the prefault angle
```

The network and the converter currents are solved as an adaptively damped fixed point on the one factorisation:

```
dI = I_now - I_prefault   at each converter bus
x  = Y^-1 dI
If = (V0_k + x_k) / (Zth_kk + Zf)
V  = V0 + x - Zcol * If
update:  I_now += damping * (target - I_now)       damping starts at 0.5, halves (floor 0.02) when the change grows
stop:    max change < 1e-9, or 1000 iterations
```

`FaultResult.ConvertersConverged` records the outcome, and `FaultResult.Converters` lists each unit's terminal
voltage, current (pu and as a multiple of its rating), active and reactive split and whether it sits on its limit.
A converter with `k = 0` feeds no fault current.

### Fault injection: unbalanced

`FaultAnalysis.Unbalanced(model, prefault, faultBusId, type, faultReactancePu = 0, subtransientXdPu = 0.20,
lineZeroSeqRatio = 3.0)` solves the fault on three sequence networks superposed on the prefault state.
`FaultType.ThreePhase` is refused with `ArgumentException`; it belongs to `ThreePhase`.

- **Positive sequence:** the balanced study's network.
- **Negative sequence:** the same passive network and loads, each unit as `Generator.FaultSourceNegativeSeqReactancePu`
  (its `NegativeSeqXPu`, default its X''d, plus the unit transformer), and every branch phase shift reversed.
- **Zero sequence:** the IEC 60909 zero-sequence network
  (`ShortCircuit.Iec60909.BuildZeroSequenceNetwork`, specified in
  [ch. 25](<0025 IEC 60909 short-circuit duty study.md>)): winding connections and neutral reactances, line `R0`/`X0` or
  `lineZeroSeqRatio * Z1`, loads neglected, no line charging. Only the part of the network connected to the fault bus
  through zero-sequence paths carries zero-sequence current. `FaultBusGrounded` is true when that component reaches an
  earthed neutral.

With `V = V0_k`, driving-point impedances `Z1`, `Z2`, `Z0` at the fault bus and `Zf = j * faultReactancePu`:

```
SLG  (phase a)  : I1 = I2 = I0 = V / (Z1 + Z2 + Z0 + 3 Zf)
LL   (b-c)      : I1 = -I2 = V / (Z1 + Z2 + Zf),   I0 = 0
DLG  (b-c-earth): Zg = Z0 + 3 Zf
                  I1 = V / (Z1 + Z2 Zg / (Z2 + Zg))
                  I2 = -I1 Zg / (Z2 + Zg)
                  I0 = -I1 Z2 / (Z2 + Zg)
bus voltages    : V1 = V0 - Z1col I1,   V2 = -Z2col I2,   V0seq = -Z0col I0
```

With no earth path in the zero sequence at the fault bus, SLG gives zero current and DLG is solved as LL.
`UnbalancedFaultResult` carries the sequence currents, the phase-current magnitudes `PhaseAPu`, `PhaseBPu`,
`PhaseCPu`, the earth current `|3 I0|`, the three driving-point impedances (`Z0` infinite when ungrounded), the
per-bus sequence voltages, the per-branch sequence currents (`BranchSequenceCurrents`) and the faulted
positive-sequence state, which is what the overlay's flow quantities read. Converter-interfaced units add no fault
current in this study: no negative- or zero-sequence fault behaviour is defined for them, so they keep their prefault
current in all three networks, and `ConvertersWithoutFaultInfeed` names them. The CLI prints a `note:` line with
their count and rated MVA whenever the case has any.

### The `fault` verb

`gridsim fault <case|case.m|case-dir> --bus <id> [--zf pu] [--type 3ph|slg|ll|dlg]` solves the prefault flow
(`MaxIterations = 100`, Q limits on), runs the fault study and assesses the faulted state with the default
`ProtectionSettings`. It prints:

- the fault current in pu, and in kA when the bus has a base voltage;
- the Thevenin impedance and the faulted-bus voltage (3ph), or for an unbalanced type the phase currents, the earth
  current, the sequence currents, the faulted bus's phase voltages and the driving-point `Z1`, `Z2`, `Z0`;
- the converter-interfaced fleet line (count, K, total infeed, units at their limit) when the case has any;
- the relays operating on the faulted state, in the table `device | function | location | quantity | pickup | time |
  basis`, in `Operating` order, with `inst` for a zero time and `-` for none;
- the closing line `PROTECTION SIMULATION: a three-phase fault at bus N would drive M relay operation(s) - primary
  clearance by D (Function) on LOCATION, T.` (an unbalanced study names the type, for example `a line to line (phases
  b-c) fault at bus 7`). The element named is `ProtectionReport.PrimaryClearance`, and `T` is its operating time to two
  decimals in seconds, or `instantaneously` for a zero time. When relays operate but no branch element with an
  operating time picks up, the line ends `- no branch element with an operating time picks up (no circuit is cleared by
  the elements above).`; with no operation it ends `operation(s).`.

`--zf` is a reactance in per unit (`Zf = j zf`). `fault` and `protection-check` refuse a negative or non-finite `--zf`
(exit 2).

### `protection-check`

`gridsim protection-check <case|case.m|case-dir> [--uv pu] [--ov pu] [--uf Hz] [--of Hz] [--oc xIn] [--thermal frac]
[--rocof Hz/s] [--cascade [--alarms <soe.csv>] | --fault-bus ID [--zf pu] [--fault-type 3ph|slg|ll|dlg]]` solves the
case (`MaxIterations = 100`, Q limits on) and assesses it. The flags set `UnderVoltagePu`, `OverVoltagePu`,
`UnderFrequencyHz`, `OverFrequencyHz`, `InstantaneousOcMultiple`, `ThermalPickupFraction` and `RocofTripHzPerS`; every
other setting is at its default on the command line. The verb prints the header, the operating-elements table, one
`not evaluated` line listing the `NotCheckable` verdicts (`not evaluated on a steady snapshot: ...` on a power flow,
`not evaluated without a trajectory or the needed sequence data: ...` on a fault study), and the closing line:

- `PROTECTION SIMULATION: no relay would operate on this state (within the generic settings).`, or
- `PROTECTION SIMULATION suggests N protection event(s) would occur. e.g. on LOCATION, relay D (Function) would likely
  operate: BASIS.` where the example is the first row of `Operating`, or
- with `--fault-bus` and no operation, `no relay would operate on the three-phase fault at bus N` (or `on the slg fault
  at bus N` for the other types).

With `--fault-bus` the same settings are assessed on the faulted state, and the fault current and Thevenin impedance
print first. The full flag set is in [0019 CLI reference.md](<../manual/0019 CLI reference.md#protection-check>) and
[the `fault` verb](<../manual/0019 CLI reference.md#fault>).

### End of a plain solve

The detailed solve report (`gridsim <case> --once`) ends with one line from the same overlay. On a converged state
it reads either `Protection simulation: no relay would operate on this state (generic settings).` or
`Protection simulation: N event(s) suggested  -  e.g. on LOCATION, relay D (Function) would likely operate (BASIS). Run
`protection-check` for the full list.` On a state that did not converge it reads
`Protection simulation: skipped - the power flow did not converge, so there is no state to assess.` The solve above
the line is untouched.

### Cascade simulation

`ProtectionCascade.Simulate(model, prefault, settings = null, maxSteps = 12)`
(`src/GridSim.Core/Protection/ProtectionCascade.cs`) follows one overload through the network. It requires a
converged prefault, never touches the caller's solve, and works on copies of the model.

```
state = prefault;  current = model
for step = 1 .. maxSteps:
    report = Assess(current, state, settings)
    next   = the operating branch verdict whose device is 49, 50, 51 or starts with 21,
             ordered by (TimeToTripS or 0) ascending, then row index (position in report.Verdicts);
             first one (N277: equal times were broken by Margin descending before)
    if none:  stop  ("stable - no branch relay would operate" if no step ran, else "settled - no further branch trips")
    open that branch (InService = false) in a copy of the model
    resolved = Newton solve of the copy (SolverSelector.CreateNewton, MaxIterations 100, Q limits on)
    record CascadeStep(step, device, function, branch, from, to, TripTimeS, basis,
                       resolved.Converged, branches then overloaded)
    if resolved did not converge (or threw): stop, state split or collapsed
    current = copy;  state = resolved
stop at the cap: "reached the 12-step cap (still cascading)"
```

The branch to open is the verdict's `LocationId`, the exact branch index, so a parallel circuit that shares a display
name is not confused with its twin. Branches then overloaded is the count of rated branches in the re-solved state
loaded above `ThermalPickupFraction * 100` percent by `LineFlow.LoadingPercent`. The elements that act are the branch
elements 49, 50, 51 and 21; voltage, frequency, reverse-power and differential elements shed or trip infeed or are
fault-only, and are left to the active layer ([ch. 15](<0015 Protections.md>)). `CascadeResult` carries the
steps, `FinalConverged` and `StopReason`; `Cascaded` is true when at least one step ran. The stop reasons are exactly:

| `StopReason` | Meaning |
|---|---|
| `stable - no branch relay would operate` | The prefault state trips nothing. |
| `settled - no further branch trips` | After at least one trip, the re-solved state trips nothing more. |
| `non-convergent after opening NAME - the network has split / collapsed (blackout)` | The re-solve after a trip did not converge. This is the terminal state. `NAME` is the branch name when set, else `F-T`. |
| `reached the N-step cap (still cascading)` | The cap was reached with the cascade still running; N is the caller's `maxSteps`, 12 by default (`ProtectionCascade.DefaultMaxSteps`). |
| `internal: could not resolve the tripped branch` | The verdict names no in-service branch; not reachable from the shipped relays. |

`gridsim protection-check <case> --cascade` prints the ordered sequence below the assessment. `--cascade` follows the
steady solved state and does not combine with `--fault-bus`.

### Sequence of events

`SequenceOfEvents.CrossReference(predicted, observed)` reconciles a predicted `CascadeResult` against a real
timestamped alarm log. `gridsim protection-check <case> --cascade --alarms <soe.csv>` reads the log and prints the
comparison. The match is by branch identity and relative order, not by wall-clock time: the cascade orders trips by
relay operating-time rank, and `at_s` is used only to order the observed alarms and to detect an out-of-order
arrival.

Log format (`AlarmLogCsv`, UTF-8): the exact header `at_s,device,from,to,description`, then one alarm per line.
`at_s` is seconds from the event start (a finite number, invariant culture; map a real UTC stamp to it first).
`device` is the ANSI id as logged and is non-empty. `from` and `to` are the branch end-bus ids, both 0 for a
non-branch alarm. `description` is everything after the fourth comma. The description and device are trimmed. Blank lines and lines
starting with `#` are skipped, including before the header, and the header is compared on the trimmed line. A line
with exactly one of `from` and `to` equal to 0, or with a negative or non-integer bus id, is refused.

Algorithm:

1. Take the observed alarms with `from != 0` and `to != 0`, ordered by `at_s`. A branch is identified by its unordered
   bus pair.
2. For each predicted step in predicted order, match the earliest unmatched alarm on the same branch. No alarm gives
   `PredictedNotObserved` (the model over-predicted).
3. Over the matched pairs in predicted order, a pair whose observed time is earlier than the previous matched pair's
   (by more than `1e-9` s) is `OrderMismatch`; otherwise `Agreement`.
4. Each unmatched branch alarm is `ObservedNotPredicted`: the model is missing something there.

`SoeCrossReference.FullyConsistent` is true when there is no `PredictedNotObserved`, `ObservedNotPredicted` or
`OrderMismatch` entry. `Divergences` lists the rest. The device column of the log is carried through but not
compared with the predicted device.

## Parameters and defaults

`ProtectionSettings` (`src/GridSim.Core/Protection/ProtectionTypes.cs`), an immutable record. Override with
`new ProtectionSettings() with { TimeOcTms = 0.05 }`.

| Name | Value | Unit | Meaning | Source symbol | Grade |
|---|---|---|---|---|---|
| 27 pickup | 0.80 | pu | Undervoltage. ENA EREC G99 Issue 2 Table 10.1, U/V, V - 20 % (2.5 s delay in the table, not modelled) | `UnderVoltagePu` | Generic |
| 59 pickup | 1.10 | pu | Overvoltage. G99 Issue 2 Table 10.1, O/V stage 1, V + 10 % (1.0 s delay in the table, not modelled) | `OverVoltagePu` | Generic |
| 81U pickup | 47.5 | Hz | Underfrequency. G99 Issue 2 Table 10.1, U/F stage 1 (20 s delay in the table, not modelled) | `UnderFrequencyHz` | Grounded |
| 81O pickup | 52.0 | Hz | Overfrequency. G99 Issue 2 Table 10.1 (0.5 s delay in the table, not modelled) | `OverFrequencyHz` | Grounded |
| 81R pickup | 1.0 | Hz/s | RoCoF. G99 Issue 2 Table 10.1, loss of mains | `RocofTripHzPerS` | Grounded |
| 78 pickup | 12.0 | deg | Vector shift. The legacy G59 high-impedance upper end (K1 = 2.0 times 6 degrees); not an acceptable loss-of-mains technique under G99 Issue 2 | `VectorShiftTripDeg` | Generic |
| 50 pickup | 2.50 | x rated | Instantaneous overcurrent | `InstantaneousOcMultiple` | Generic |
| 51 pickup | 1.20 | x rated | Time overcurrent pickup `Is` | `TimeOcMultiple` | Generic |
| 51 TMS | 0.10 | - | Time multiplier of the IEC standard-inverse curve | `TimeOcTms` | Generic |
| 37 floor | 0.001 | pu | Absolute undercurrent floor | `UnderCurrentFloorPu` | Generic |
| 49 pickup | 1.00 | x RateA | Thermal pickup | `ThermalPickupFraction` | Generic |
| 49 tau | 900.0 | s | Thermal time constant | `ThermalTimeConstantS` | Generic |
| 21 Zone 1 reach | 0.80 | x line Z | | `DistanceZone1Reach` | Generic |
| 21 Zone 2 reach | 1.25 | x line Z | | `DistanceZone2Reach` | Generic |
| 21 Zone 3 reach | 2.00 | x line Z | | `DistanceZone3Reach` | Generic |
| 21 Zone 1 time | 0.0 | s | | `DistanceZone1TimeS` | Generic |
| 21 Zone 2 time | 0.3 | s | | `DistanceZone2TimeS` | Generic |
| 21 Zone 3 time | 0.8 | s | | `DistanceZone3TimeS` | Generic |
| 21 angle window | 75.0 | deg | Half-width of the forward sector about the line angle | `DistanceAngleWindowDeg` | Generic |
| 32 pickup | 5.0 | MW | Reverse power magnitude | `ReversePowerMw` | Generic |
| 87 pickup | 0.10 | pu | Differential operate current | `DifferentialPickupPu` | Generic |
| 50N pickup | 1.00 | x rated | Residual instantaneous | `EarthFaultInstantaneousMultiple` | Generic |
| 51N pickup | 0.20 | x rated | Residual time-overcurrent pickup | `EarthFaultTimeMultiple` | Generic |
| 51N TMS | 0.10 | - | | `EarthFaultTms` | Generic |
| 46 pickup | 0.20 | x rated | Negative-sequence current | `NegativeSequenceMultiple` | Generic |
| 46 time | 1.0 | s | Definite time | `NegativeSequenceTimeS` | Generic |
| per-asset overrides | null | - | Full settings per branch name for 49, 50, 51, 21, 50N, 51N, 46, 21N | `PerAsset` | - |

The 50N, 51N and 46 values are marked `[VERIFY]` in the source: no published setting policy states them as multiples
of a circuit's rated current.

Constants and defaults of the studies:

| Name | Value | Meaning | Source symbol |
|---|---|---|---|
| Default X''d | 0.20 pu | Subtransient reactance of a machine that carries none, on its MBase | `FaultAnalysis.DefaultSubtransientXdPu` |
| Converter gain K | 2.0 | Reactive current per unit voltage dip, on the unit's rating | `FaultAnalysis.DefaultConverterReactiveGain` |
| Converter fault multiple | 1.2 | `k = I_skPF / I_rG` where a unit carries none | `Generator.DefaultConverterFaultCurrentPu` |
| Line Z0 ratio | 3.0 | `Z0 = 3 Z1` where a line carries no R0/X0 | `UnbalancedFaultResult.LineZeroSeqRatio` |
| Collapsed-voltage floor | 1e-9 pu | Below it no current is derived as `S/V` | `ProtectionContext.MinVoltageForCurrentPu` |
| No-flow floor | 1e-12 pu | At or below it the apparent impedance is infinite | `ProtectionContext.MinApparentPowerPu` |
| Minimum line impedance | 1e-9 pu | Below it the distance element skips the line | `DistanceRelay.MinLineImpedancePu` |
| Cascade step cap | 12 | | `ProtectionCascade.DefaultMaxSteps` |
| Cascade re-solve iterations | 100 | Newton cap of each post-trip solve | `ProtectionCascade.ResolveMaxIterations` |

## Exit codes

`protection-check`:

| Exit | Cause |
|---|---|
| 0 | The assessment ran, whether or not any element operates. A cascade or fault verdict list is a result, not a failure. |
| 1 | The case failed to load; the fault study failed (`InvalidOperationException` or `ArgumentException`, for example a singular fault network); the `--alarms` file is missing or malformed (the message names the line). |
| 2 | No case; a bad flag value (`error: --NAME expected ... but got '...'`); `--fault-bus` for a bus not in the case; `--fault-type` or `--zf` without `--fault-bus`; `--zf` negative; `--fault-bus` with `--cascade`; `--alarms` without `--cascade`. |
| 3 | The prefault power flow did not converge; the converter iteration of the fault study did not converge (the verdicts still print); an `slg` or `dlg` study on a model with no transformer winding connection anywhere (the verdicts still print); `--alarms` was given and the prediction does not reproduce the log (`SOE: DIVERGES`). |

`fault`: 0 on success; 1 for a load error or a fault-analysis error (including a bus not in the model); 2 for no case,
no `--bus`, or a `--type` other than `3ph`, `slg`, `ll`, `dlg`; 3 when the prefault flow did not converge, or for an
`slg` or `dlg` study on a model with no transformer winding connection (the result prints, the reason goes to
standard error). Like `protection-check`, `fault` exits 3 when the converter iteration does not converge (D-DOC-013),
and refuses a negative or non-finite `--zf` (exit 2).

No test runs either verb's exit codes: the CLI tests cited below check the text of `Program.cs`, and the codes in
this section were confirmed by running the Release binary.

No bundled case carries transformer winding connections (MATPOWER and JSON v1 cannot hold them), so `slg` and `dlg`
exit 3 on every bundled case, and `ll` is the unbalanced type that runs without them. All three unbalanced types run
without error from the API on a model built with windings, and from a JSON v2 case that gives them (N234, October
2026: phase P5 opened for fault sequence data; [ch. 28](<0028 The v2 JSON schema.md>)).

`--json`, `--csv` and `--out` write the result to a file. `protection-check` writes the settings, the fault study, an
`operating` table, the not-evaluated text, a `cascade` table and an `soe` table; `fault` writes the study at full
precision and a `relays` table, a `converters` table (3ph) or a `branches` table (unbalanced).

## Invariants

| Invariant | Test |
|---|---|
| The assessment never mutates the solved result. | `ProtectionAssessmentTests.Assessment_does_not_mutate_the_solved_result` |
| A healthy converged case raises no voltage or differential operation; the shunt is netted off the residual. | `ProtectionAssessmentTests.Healthy_case_in_band_has_no_voltage_or_differential_operations` |
| An inverted band (27 pickup 1.06 pu above 59 pickup 0.94 pu) puts every bus out of band, so 27 and 59 both operate. | `ProtectionAssessmentTests.Undervoltage_and_overvoltage_pick_up_when_the_band_is_tightened_around_the_state` |
| A branch rated below its flow fires 49, 50 and 51. | `ProtectionAssessmentTests.Thermal_and_overcurrent_operate_on_a_branch_rated_below_its_flow` |
| More current trips 51 sooner; the curve argument is the pickup multiple, not the rated multiple. | `ProtectionAssessmentTests.Time_overcurrent_is_faster_at_higher_current`, `Time_overcurrent_uses_pickup_multiple_not_rated_multiple` |
| 81R and 78 are `NotCheckable` and never operate without a trajectory; a trajectory at 47.0 Hz, 2.0 Hz/s and a 15 degree vector shift fires 81U, 81R and 78. | `ProtectionAssessmentTests.Frequency_and_rocof_relays_need_a_trajectory_and_then_operate` |
| A generator absorbing power past the pickup fires 32. | `ProtectionAssessmentTests.Reverse_power_flags_a_motoring_generator` |
| The ground group is `NotCheckable` on a power flow and operates on an unbalanced study. | `ProtectionAssessmentTests.Earth_and_negative_sequence_are_reported_not_checkable`, `EarthFaultAndIec60909CompletionTests.The_earth_fault_elements_operate_on_an_earth_fault_and_stay_not_checkable_on_a_power_flow` |
| Distance verdicts do not depend on the orientation a branch is stored in; thermal loading uses the heavier end. | `ProtectionWs6Tests.Distance_verdicts_do_not_depend_on_branch_orientation`, `Thermal_loading_uses_the_heavier_end` |
| A site-specific override changes only the named asset; a null map equals the generics. | `SiteSpecificRelayTests.A_site_specific_override_suppresses_only_the_named_asset`, `A_null_override_map_is_identical_to_the_generics` |
| The 27, 59, 81U, 81O, 81R and 78 defaults equal the cited G99 and G59 values; the zone times, the 1e-9 and 1e-12 floors, the 12 and 100 step limits and the IEC standard-inverse pair equal their literals. The other defaults (50, 51, TMS, 37, 49, the zone reaches, the angle window, 32, 87, 50N, 51N, 46) are pinned by no test. | `S3ProtectionGroundingTests`, `Ws13CoreConstantsAntiDriftTests.Protection_constants_equal_the_literals_they_replaced` |
| A bolted fault collapses the bus and draws a large current; a fault impedance lowers the current and leaves voltage on the bus. | `FaultAndCascadeTests.A_bolted_three_phase_fault_collapses_the_bus_and_draws_a_large_current`, `A_higher_fault_impedance_limits_the_fault_current` |
| The faulted state operates 87, 21 and 27. | `FaultAndCascadeTests.The_faulted_state_operates_the_differential_and_distance_and_undervoltage_elements` |
| `Operating` is fastest first and the primary clearance is a timed branch element (87 first); within one device the untimed order is by relative margin; equal times keep row order. | `Doc4ProtectionIec60909Tests.D158_operating_list_is_fastest_first_and_primary_clearance_is_a_timed_branch_element`, `D158_within_one_device_the_order_is_the_old_margin_order` |
| The sparse injection walk equals the dense row sum bit for bit. | `FaultInjectionWalkPinTests.Sparse_cell_injection_walk_is_bit_for_bit_the_dense_loop` |
| SLG, LL and DLG match their closed forms; with no earth path SLG is zero and DLG is LL; a 3ph type is refused by the unbalanced entry point. | `EarthFaultAndIec60909CompletionTests.Single_line_to_ground_matches_the_series_connection_closed_form`, `Line_to_line_and_double_line_to_ground_match_their_closed_forms`, `Without_an_earth_path_there_is_no_earth_current_and_dlg_is_line_to_line`, `A_three_phase_type_is_refused_by_the_unbalanced_entry_point` |
| The phase elements read phase currents and loop impedances in an unbalanced study; a power flow and a balanced fault are unchanged; converters add no unbalanced infeed and are named. | `EarthFaultPhaseElementsTests` |
| A converter holds its current limit with reactive priority and contributes far less than an X''d machine; the unit transformer is in series with the machine; a power-flow-only unit is no source. | `ConverterFaultContributionTests` |
| An unstressed case does not cascade; an overloaded branch is opened first. | `FaultAndCascadeTests.An_unstressed_case_does_not_cascade`, `A_branch_rated_below_its_flow_starts_a_cascade_that_opens_that_branch_first` |
| A non-converged state is refused by the assessment, the fault study and the cascade. | `ConvergenceContractTests.Fault_analysis_refuses_a_non_converged_prefault`, `Protection_cascade_and_assessment_refuse_a_non_converged_state` |
| Prediction against log: agreement, a missed trip, an unpredicted trip and an order error are each classed; non-branch alarms are ignored. | `SequenceOfEventsTests` |
| The log parser accepts the format exactly and refuses a malformed line, naming it; the CLI source contains the `--alarms` wiring (a source-text check that runs nothing). | `GradeAndSoeCliTests` |
| The fault assessment uses the given settings and a fault impedance lowers the current (API tests); the CLI source contains the `--fault-bus` flags and exit messages (a source-text check). | `ProtectionCheckFaultCliTests` |

## Worked example

Timings do not appear below; the trip times are computed from the state and are the same on every machine. All
outputs are pasted from `docs/_captures/protection-assess/`.

### A clean state, and one setting moved

`case9` on the generic settings trips nothing. The three elements the snapshot cannot evaluate are listed:

```
$ gridsim protection-check case9
Protection assessment  -  case9  (9 buses, converged=True)  [passive; solve unchanged]
  operating elements: 0
  not evaluated on a steady snapshot: 81U (Underfrequency); 81O (Overfrequency); 81R (RoCoF); 78 (Vector shift / LoM); 50N/51N/21N/46 (Ground / negative-sequence)

PROTECTION SIMULATION: no relay would operate on this state (within the generic settings).
[exit 0]
```

Raising the undervoltage pickup to 1.00 pu asks what a tighter relay would do (`check-case9-uv.txt`). The lowest bus
voltage is 0.996 pu, so 27 picks up on bus 9 and nothing else does:

```
  | 27 | Undervoltage | bus 9 | 0.996 pu | 1.000 | - | Vm 0.996 pu at bus 9 is below the 1.00 pu undervoltage pickup |
```

Moving an 81 setting across the nominal frequency operates it on a steady state, because the quantity is the model's
nominal 50 Hz (`check-case9-uf.txt`, `--uf 50.5`):

```
  | 81U | Underfrequency | system | 50.000 Hz | 50.500 | - | operating frequency 50.000 Hz is below the 50.5 Hz underfrequency pickup |
```

### The end of a plain solve

`gridsim case30 --once` ends with the overlay line (`solve-case30.txt`, elided):

```
Voltage range: 0.961 - 1.000 p.u. (in band)
Total losses: 2.44 MW, -6.79 MVAr
Protection simulation: 2 event(s) suggested  -  e.g. on branch 6-8, relay 49 (Thermal overload) would likely operate (branch 6-8 loaded to 109% of RateA (32 MVA); thermal replica trips in 1674 s). Run `protection-check` for the full list.
```

### Cascade on case30

`case30` has branch 6-8 loaded to 1.088 of its 32 MVA rating (`cascade-case30.txt`). The cascade opens it, re-solves,
and finds the next trip:

```
$ gridsim protection-check case30 --cascade
Protection assessment  -  case30  (30 buses, converged=True)  [passive; solve unchanged]
  operating elements: 2
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 49 | Thermal overload | branch 6-8 | 1.088 xRateA | 1.000 | 1673.68s | branch 6-8 loaded to 109% of RateA (32 MVA); thermal replica trips in 1674 s |
  | 37 | Undercurrent | branch 9-11 | 0.000 pu | 0.001 | - | branch 9-11 carrying 0.00E+000 pu current (effectively de-energized, below the 1.0E-003 pu undercurrent floor) |
  not evaluated on a steady snapshot: 81R (RoCoF); 78 (Vector shift / LoM); 50N/51N/21N/46 (Ground / negative-sequence)

PROTECTION SIMULATION suggests 2 protection event(s) would occur. e.g. on branch 6-8, relay 49 (Thermal overload) would likely operate: branch 6-8 loaded to 109% of RateA (32 MVA); thermal replica trips in 1674 s.

Cascade propagation (2 trip(s)):
  1. relay 49 (Thermal overload) opens branch 6-8 in 1673.68s  ->  2 branch(es) then overloaded
  2. relay 51 (Time overcurrent) opens branch 8-28 in 2.84s  ->  re-solve did NOT converge
  stop: non-convergent after opening 8-28 - the network has split / collapsed (blackout).
[exit 0]
```

The thermal replica reproduces the time: `L = 1.088`, `t = 900 ln(1.088^2 / (1.088^2 - 1))` is 1,677 s from the
rounded loading, 1,673.68 s from the unrounded one. The 37 verdict on branch 9-11 is a real dead stub (zero current,
both ends at the same voltage) and is not a branch-trip element, so the cascade ignores it. Step 1 opens 6-8 and the
re-solve converges with two branches then overloaded. Step 2 opens 8-28 by its 51 element in 2.84 s and the re-solve
does not converge, so the cascade stops at the terminal state.

### Sequence of events against a log

The log `docs/_captures/protection-assess/case30-alarms.csv` records both predicted trips and one bus alarm:

```
# Alarm log for the case30 cascade, seconds from the fault start.
at_s,device,from,to,description
12.4,49,6,8,Thermal alarm on 6-8
15.1,51,8,28,Overcurrent trip on 8-28
15.3,27,0,0,Bus 28 undervoltage (not a branch alarm, not matched)
```

`gridsim protection-check case30 --cascade --alarms docs/_captures/protection-assess/case30-alarms.csv` prints the
cascade above, then (`cascade-case30-alarms.txt`):

```
Sequence of events vs prediction (3 alarm(s), 1 non-branch not matched): 2 agree, 0 predicted not observed, 0 observed not predicted, 0 out of order
  Agreement: predicted and observed on branch 6-8 (predicted #1, observed at 12.4 s)
  Agreement: predicted and observed on branch 8-28 (predicted #2, observed at 15.1 s)
SOE: FULLY CONSISTENT - the predicted cascade reproduces the alarm log.
[exit 0]
```

Drop the second branch row from the log and the verb reports `PredictedNotObserved`, prints `SOE: DIVERGES` and exits 3.

### A bolted fault on case9

`gridsim protection-check case9 --fault-bus 7` superposes a bolted three-phase fault on the converged state
(`check-case9-fault7.txt`, elided):

```
$ gridsim protection-check case9 --fault-bus 7
Protection assessment  -  case9  (9 buses)  three-phase fault at bus 7, Zf=j0.000 pu  [passive; prefault solve unchanged]
  fault current: 9.26 pu (1.55 kA)   Thevenin Z: 0.1098 pu
  (generator subtransient reactance synthesised at 0.20 pu; positive-sequence, so three-phase only)
  operating elements: 17
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 50 | Instantaneous overcurrent | branch 6-7 | 2.655 xIn | 2.500 | inst | branch 6-7 current 2.65x rated exceeds the 2.50x instantaneous pickup (trips instantly) |
  | 87 | Differential | bus 7 | 9.256 pu | 0.100 | inst | busbar differential 9.26E+000 pu at bus 7 exceeds the 0.10 pu bias pickup (internal fault) |
  | 21-Z2 | Distance | branch 6-7 | 0.103 pu | 0.127 | 0.30s | branch 6-7 apparent Z 0.103 pu is inside Zone 2 (reach 0.127 pu, line Z 0.102); operates in 0.3 s |
  | 21-Z2 | Distance | branch 7-8 | 0.073 pu | 0.091 | 0.30s | branch 7-8 apparent Z 0.073 pu is inside Zone 2 (reach 0.091 pu, line Z 0.072); operates in 0.3 s (relay at the to end, bus 8) |
  | 51 | Time overcurrent | branch 6-7 | 2.655 xIn | 1.200 | 0.87s | branch 6-7 current 2.65x rated over the 1.20x pickup; IEC standard-inverse trips in 0.9 s |
...
  | 49 | Thermal overload | branch 6-7 | 1.084 xRateA | 1.000 | 1711.28s | branch 6-7 loaded to 108% of RateA (150 MVA); thermal replica trips in 1711 s |
...
  | 27 | Undervoltage | bus 7 | 0.000 pu | 0.800 | - | Vm 0.000 pu at bus 7 is below the 0.80 pu undervoltage pickup |
...
  not evaluated without a trajectory or the needed sequence data: 81U (Underfrequency); 81O (Overfrequency); 81R (RoCoF); 78 (Vector shift / LoM); 50N/51N/21N/46 (Ground / negative-sequence)

PROTECTION SIMULATION suggests 17 protection event(s) would occur. e.g. on branch 6-7, relay 50 (Instantaneous overcurrent) would likely operate: branch 6-7 current 2.65x rated exceeds the 2.50x instantaneous pickup (trips instantly).
[exit 0]
```

Reading the numbers. The Thevenin impedance at bus 7 is 0.1098 pu, so `V0_7 = If * |Zth| = 9.256 * 0.1098 = 1.016` pu is
the prefault voltage, and the 87 quantity is the fault current itself (9.256 pu; 1.55 kA on the 345 kV bus:
`9.256 * 100 / (sqrt(3) * 345)`). Branch 6-7 is rated 150 MVA, so its rated current is 1.5 pu and the 51 quantity
`m = 2.655` is 3.98 pu; `M = 2.655 / 1.2 = 2.2125` and `t = 0.1 * 0.14 / (2.2125^0.02 - 1) = 0.87` s. The distance
element on 6-7 sees `Z = 0.103` pu against a line impedance of 0.102 pu: the fault is at the far end of the line, so
the apparent impedance equals the line impedance, which is inside Zone 2 (1.25 x 0.102 = 0.127 pu reach) and outside
Zone 1 (0.8 x 0.102 = 0.082 pu), and operates at the Zone 2 time of 0.3 s. On 7-8 the deciding end is the To end, at
bus 8, as the basis says.

A fault impedance of `--zf 0.5` (`check-case9-fault7-zf.txt`) limits the current to
`If = V0_7 / |Zth + j 0.5|`, which is 1.67 pu, and only the differential operates, because every phase element's
current and every bus voltage stays inside its setting:

```
$ gridsim protection-check case9 --fault-bus 7 --zf 0.5
Protection assessment  -  case9  (9 buses)  three-phase fault at bus 7, Zf=j0.500 pu  [passive; prefault solve unchanged]
  fault current: 1.67 pu (0.28 kA)   Thevenin Z: 0.1098 pu
  (generator subtransient reactance synthesised at 0.20 pu; positive-sequence, so three-phase only)
  operating elements: 1
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 87 | Differential | bus 7 | 1.671 pu | 0.100 | inst | busbar differential 1.67E+000 pu at bus 7 exceeds the 0.10 pu bias pickup (internal fault) |
  not evaluated without a trajectory or the needed sequence data: 81U (Underfrequency); 81O (Overfrequency); 81R (RoCoF); 78 (Vector shift / LoM); 50N/51N/21N/46 (Ground / negative-sequence)

PROTECTION SIMULATION suggests 1 protection event(s) would occur. e.g. on bus 7, relay 87 (Differential) would likely operate: busbar differential 1.67E+000 pu at bus 7 exceeds the 0.10 pu bias pickup (internal fault).
[exit 0]
```

### The `fault` verb on case14

`gridsim fault case14 --bus 4` (`fault-case14-bus4.txt`, elided) collapses the bus to 0.000 pu, draws 12.41 pu, and
operates 23 elements. 87 on bus 4 operates at the fault current, 21 operates in Zone 2 on the five lines at bus 4
(from the far end where that is the deciding end) and in Zone 3 on three remote lines, and 27 operates across the
depressed network:

```
$ gridsim fault case14 --bus 4
Balanced three-phase fault  -  case14  bus 4  Zf=j0.000 pu
  fault current: 12.41 pu   Thevenin Z: 0.0820 pu   faulted bus voltage: 0.000 pu
  (generator subtransient reactance synthesised at 0.20 pu; positive-sequence, so three-phase only)
  relays operating on the faulted state: 23
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 87 | Differential | bus 4 | 12.415 pu | 0.100 | inst | busbar differential 1.24E+001 pu at bus 4 exceeds the 0.10 pu bias pickup (internal fault) |
...
  | 21-Z2 | Distance | branch 4-7 | 0.209 pu | 0.261 | 0.30s | branch 4-7 apparent Z 0.209 pu is inside Zone 2 (reach 0.261 pu, line Z 0.209); operates in 0.3 s (relay at the to end, bus 7) |
...
  | 21-Z3 | Distance | branch 1-5 | 0.353 pu | 0.459 | 0.80s | branch 1-5 apparent Z 0.353 pu is inside Zone 3 (reach 0.459 pu, line Z 0.229); operates in 0.8 s |
...

PROTECTION SIMULATION: a three-phase fault at bus 4 would drive 23 relay operation(s) - primary clearance by 87 (Differential) on bus 4, instantaneously.
[exit 0]
```

Bus 4 has no base kV in `case14`, so no kA figure prints.

### An unbalanced fault

A line-to-line fault at bus 7 of `case9` runs without winding data (`fault-case9-bus7-ll.txt`, elided):

```
$ gridsim fault case9 --bus 7 --type ll
Line to line (phases b-c) fault  -  case9  bus 7  Zf=j0.000 pu
  phase currents: Ia 0.000 pu (0.000 kA)  Ib 8.016 pu (1.341 kA)  Ic 8.016 pu (1.341 kA)
  earth current |3I0|: 0.000 pu (0.000 kA)   sequence |I1| 4.628  |I2| 4.628  |I0| 0.000 pu
  faulted bus phase voltages: Va 1.016  Vb 0.508  Vc 0.508 pu
  driving-point Z1 0.0225+j0.1074  Z2 0.0225+j0.1074  Z0 infinite (no earth path in the zero sequence)
...
  | 21-Z1 | Distance | branch 6-7 | 0.000 pu | 0.081 | inst | branch 6-7 apparent Z 0.000 pu is inside Zone 1 (reach 0.081 pu, line Z 0.102); operates in 0.0 s (relay at the to end, bus 7) [b-c loop] |
  | 21-Z1 | Distance | branch 7-8 | 0.000 pu | 0.058 | inst | branch 7-8 apparent Z 0.000 pu is inside Zone 1 (reach 0.058 pu, line Z 0.072); operates in 0.0 s [b-c loop] |
  | 87 | Differential | bus 7 | 8.016 pu | 0.100 | inst | busbar differential 8.02E+000 pu at bus 7 exceeds the 0.10 pu bias pickup (internal fault) |
...
  | 46 | Negative sequence | branch 6-7 | 1.308 xIn | 0.200 | 1.00s | branch 6-7 negative-sequence current 1.31x rated exceeds the 0.20x pickup; definite time 1.00 s |
...
```

The closed form checks: `I1 = V / (Z1 + Z2) = 1.016 / (2 * 0.1097) = 4.63` pu, `I2 = -I1`, and the faulted phases carry
`|Ib| = |Ic| = sqrt(3) * I1 = 8.02` pu. The phase-to-phase loop `b-c` is named in the distance verdict. An `slg` request
on the same case reports the missing earthing data and exits 3 (`check-case9-slg.txt`):

```
$ gridsim protection-check case9 --fault-bus 7 --fault-type slg
Protection assessment  -  case9  (9 buses)  slg fault at bus 7, Zf=j0.000 pu  [passive; prefault solve unchanged]
  phase currents Ia 0.00  Ib 0.00  Ic 0.00 pu   earth current |3I0| 0.00 pu   (no earth path in the zero sequence at the fault bus)
  (phase elements on the phase currents and voltages; 50N/51N/21N/46 on the sequence currents; generic pickups)
  operating elements: 0
  not evaluated without a trajectory or the needed sequence data: 81U (Underfrequency); 81O (Overfrequency); 81R (RoCoF); 78 (Vector shift / LoM)

PROTECTION SIMULATION: no relay would operate on the slg fault at bus 7 (within the generic settings).
protection-check: the model carries no transformer winding connections at all, so it has no earthed neutral; the earth-fault verdicts below reflect missing data. Exit 3.
[exit 3]
```

### Converter-interfaced units

`gridsim fault data/gb-synthetic --bus 100` solves the fault with 50 converter-interfaced units
(`fault-gbsynthetic-bus100.txt`, elided):

```
$ gridsim fault data/gb-synthetic --bus 100
Balanced three-phase fault  -  data/gb-synthetic  bus 100  Zf=j0.000 pu
  fault current: 3.54 pu (18.60 kA)   Thevenin Z: 0.3113 pu   faulted bus voltage: 0.000 pu
  (generator subtransient reactance synthesised at 0.20 pu; positive-sequence, so three-phase only)
  converter-interfaced units: 50 (current-limited, K=2.0 reactive priority)  total infeed 133.33 pu, 3 at current limit
  relays operating on the faulted state: 41
...
```

The fleet line is the converter fixed point above: 50 units, each at the grid-code characteristic with reactive
priority, three of them on their current limit. A `WARNING: converter current iteration did not converge` is appended
to that line when `ConvertersConverged` is false; the faulted state is then marked not converged, `Assess` refuses it, and
`fault` and `protection-check --fault-bus` print the study without a relay table and exit 3.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Protection/ProtectionTypes.cs` | `ProtectionLocation`, `ProtectionConfidence`, `RelayVerdict`, `ProtectionTrajectory`, `ProtectionSettings`, `ProtectionReport` | The records: verdicts, grades, settings and defaults, the report views. |
| `src/GridSim.Core/Protection/ProtectionContext.cs` | `ProtectionContext` | The operating quantities computed once; per-branch settings; the phase quantities of an unbalanced study. |
| `src/GridSim.Core/Protection/ProtectionRelays.cs` | `IProtectionRelay`, `ProtectionRegistry`, `VerdictCollector`, the 14 relay classes | Each relay's equation; the registry order. |
| `src/GridSim.Core/Protection/ProtectionAssessment.cs` | `ProtectionAssessment` | `Assess`, `AssessThreePhaseFault`, `AssessUnbalancedFault`. |
| `src/GridSim.Core/Protection/FaultAnalysis.cs` | `FaultAnalysis`, `FaultType`, `FaultResult`, `UnbalancedFaultResult`, `BranchSequenceCurrents`, `ConverterFaultInfeed` | The balanced Thevenin study, the unbalanced sequence-network study, the converter fixed point. |
| `src/GridSim.Core/Protection/FaultNetworkSystem.cs` | `FaultNetworkSystem` | The factored fault admittance: the sparse LU at every size (dense LU as a test oracle only); singular fails loud. |
| `src/GridSim.Core/Protection/ProtectionCascade.cs` | `ProtectionCascade`, `CascadeStep`, `CascadeResult` | The trip, re-solve, re-assess chain. |
| `src/GridSim.Core/Protection/SequenceOfEvents.cs` | `SequenceOfEvents`, `AlarmEvent`, `SoeEntry`, `SoeCrossReference` | Prediction against alarm log. |
| `src/GridSim.Core/Protection/AlarmLogCsv.cs` | `AlarmLogCsv` | The alarm-log CSV reader. |
| `src/GridSim.Core/PowerFlow/ConvergenceContract.cs` | `ConvergenceContract`, `NonConvergedStateException` | The converged-state gate. |
| `src/GridSim.Core/Protection/Coordination/` | `OvercurrentCoordination` | The IEC curve constants 51 and 51N use ([ch. 26](<0026 Overcurrent coordination (IDMT grading).md>)). |
| `src/GridSim.Core/Protection/ShortCircuit/` | `Iec60909` | The zero-sequence network the unbalanced study uses ([ch. 25](<0025 IEC 60909 short-circuit duty study.md>)). |
| `src/GridSim.Cli/Program.cs` | `RunProtectionCheck`, `RunFault`, `RunUnbalancedFault`, `PrintDetailed` | The verbs, their flags and exit codes, the end-of-solve line. |

## Limits

- Every pickup is a generic standard-practice value; real relay settings are site-specific and not public, so a verdict is a screen and not a certified protection study.
- `protection-check` exposes seven settings as flags; the distance zones, the time multipliers, the 37 floor, the 87 pickup and `PerAsset` are reachable only through the API.
- No verb supplies a `ProtectionTrajectory`, so 81R and 78 report `NotCheckable` on every command-line run, and 81U and 81O read the model's nominal frequency.
- `PerAsset` overrides are keyed by branch name, and MATPOWER branches carry none, so no override applies to a MATPOWER case.
- `fault` and `protection-check` both refuse a negative or non-finite `--zf` (exit 2) and both exit 3 when the converter iteration does not converge.
- The from-end power on the context is computed but read by no relay.
- 32 reads each generator's scheduled `Pg` from the model, not its solved output, so a slack machine that absorbs power in the solution is not seen.
- 49 starts the thermal replica cold and carries no preload history.
- 21 has no load blinder and no mutual-coupling or infeed compensation; it is the positive-sequence impedance screen described above.
- `--zf` and `faultReactancePu` are a pure reactance, `Zf = j zf`; a resistive fault impedance cannot be entered.
- The balanced fault stamps each load as a constant impedance at its nominal demand and each synchronous machine as X''d plus its unit transformer; a machine with no X''d takes 0.20 pu, so fault currents on cases without machine data are a synthesised screen.
- The unbalanced study gives converter-interfaced units no fault infeed, because the model defines no negative- or zero-sequence behaviour for them, and it neglects loads and line charging in the zero sequence.
- No bundled case carries transformer winding connections, so `slg` and `dlg` exit 3 on every bundled case.
- The cascade opens one branch per step with no breaker timing, and treats any non-convergent re-solve as a split or collapse without distinguishing islanding from voltage collapse.
- `ProtectionReport.Operating` ranks untimed elements by margin relative to their pickup, which makes different units comparable but is not a severity measure.
- The sequence-of-events match compares branch identity and order, not the predicted and observed times, and does not compare the device.

## See also

- [Ch. 15, Protections](<0015 Protections.md>): the active protection stages that change the trajectory.
- [Ch. 25, Short-circuit IEC 60909](<0025 IEC 60909 short-circuit duty study.md>): the standards duty and the zero-sequence network.
- [Ch. 26, Overcurrent coordination](<0026 Overcurrent coordination (IDMT grading).md>): the curve table and relay-chain grading.
- [Ch. 3, Power flow](<0003 Power flow.md>): the solved state the overlay reads, and `ConvergenceContract`.
- [Ch. 16, Physics conformance](<0016 Physics conformance and the constrained estimator.md>): the other read-only overlay on a solved state.
- [`protection-check`](<../manual/0019 CLI reference.md#protection-check>) and [`fault`](<../manual/0019 CLI reference.md#fault>) in the CLI reference.
