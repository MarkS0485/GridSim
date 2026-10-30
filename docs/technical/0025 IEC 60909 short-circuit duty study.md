# 25. IEC 60909 short-circuit duty study

This chapter specifies the IEC 60909-0 short-circuit duty study: the standardised currents that switchgear and
protection must be rated for, as the code in `src/GridSim.Core/Protection/ShortCircuit/Iec60909.cs` computes them.
The study answers "what is the highest (or lowest) current this bus can see", independent of the dispatch. It drives
the fault from one equivalent voltage source `c*Un/sqrt(3)` at the fault bus, shorts every other source, leaves loads
out, and reports the family of currents `Ik''`, `ip`, `Ib`, `Ik`, `Ith` and `idc`, with the factors `kappa`, `mu`, `m`
and `n` that relate them. It is a different calculation from the operating-point fault study (`FaultAnalysis`,
`gridsim fault`), which superposes a bolted fault on the solved prefault voltages and feeds the relay verdicts of
[ch. 17, Protection assessment](<0017 Protection assessment (a passive relay overlay).md>). Relay elements and IDMT grading are in that
chapter and in [ch. 26, Overcurrent coordination](<0026 Overcurrent coordination (IDMT grading).md>); the protection stages of
assets are in [ch. 15, Protections](<0015 Protections.md>). They are not repeated here.

The study has one entry module, `Iec60909`, and one command, `gridsim iec60909`. Its flags are listed once, in
[the CLI reference](<../manual/0019 CLI reference.md#iec60909>). The model fields it reads (`Bus.BaseKv`, `Generator`,
`Branch`) are defined in [ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>).

The study is deterministic: the same case, bus, objective and times give the same bits on every run. Every fault
network, of any size, is factored by the same sparse LU, so a 9-bus case and gb-full run the same arithmetic (see
Network size and islands).

## Model

### Equivalent source and the networks

The duty is a property of the network and its sources. Per unit on the system base `GridModel.BaseMva`, with the fault
at bus `k` and nominal line-to-line voltage `Un = Bus.BaseKv` of that bus:

```
source at bus k:   c * Un / sqrt(3)          (c pu on the phase base)
positive network:  branch series impedances and taps (+ phase shifts)
                   + each synchronous generator as j*X to earth
                   loads: not stamped
                   line charging B/2 and bus shunts Gs/Bs: not stamped
```

`FaultNetworkSystem.ForModel(model, shunts, seriesOnly: true)` builds this network from the model's Y-bus after
removing every branch `B` and every bus `Gs`/`Bs`; series impedance, tap ratio and phase shift are kept exactly. The
operating-point study keeps the full network. One column of the bus-impedance matrix is solved, `Y x = e_k`, and the
driving-point impedance is `Zkk = x[k]`, the positive-sequence Thevenin impedance `Z1`.

Each in-service, non-converter, non-power-flow-only generator `g` on a bus of the model is a shunt to earth:

```
X_sys,g = FaultSourceReactancePu(0.20) * BaseMva / MBase_g          (pu on BaseMva)
FaultSourceReactancePu = X''d_eff + max(0, UnitTransformerXPu)      (pu on MBase)
X''d_eff = SubtransientXdPu if > 0, else the fallback 0.20
Y_g = 1 / (j * X_sys,g)
```

`MBase <= 0` is read as `BaseMva`. The unit transformer reactance is in series with the machine because the network bus
of a transmission-connected machine is the high-voltage side of its step-up transformer. A generator with
`PowerFlowOnly` set (a slack balancer, a voltage-control unit) is no source. A converter-interfaced generator is open
in the positive and negative networks and enters as a current source (below). Out-of-service units are not in
`GridModel.Generators` and take no part.

The negative-sequence network is the same passive network with each machine's reactance replaced by
`X2_eff + max(0, UnitTransformerXPu)`, where `X2_eff = NegativeSeqXPu` if above zero, else `X''d_eff` (the round-rotor
convention). With no `X2` supplied, `Z2 = Z1` exactly. Static plant has `Z2 = Z1`.

### Voltage factor c

`Iec60909.VoltageFactor(Un, objective)` returns these values from IEC 60909-0 Table 1 (for low voltage, the row for a
+6 % voltage tolerance; a `c_max` of 1.10 for low voltage is not offered):

| Nominal voltage | Maximum current (equipment duty) | Minimum current (protection sensitivity) |
|---|---|---|
| `Un <= 1 kV` (low voltage) | 1.05 | 0.95 |
| `Un > 1 kV` | 1.10 | 1.00 |

`ShortCircuitObjective.Maximum` and `Minimum` choose the column. The factor is taken at the fault bus's nominal voltage.

### Initial symmetrical current Ik''

```
Ik''_pu = c / |Zk|                         Zk = R + jX (pu, system base)
Ik''_kA = Ik''_pu * BaseMva / (sqrt(3) * Un)
R/X     = R / X                            (0 when X = 0)
```

`Iec60909.FromThevenin` is this calculation on a supplied `Zk`. It throws `ArgumentOutOfRangeException` for `Un <= 0`,
`BaseMva <= 0` or `|Zk| = 0`.

### Peak current, DC component and thermal current

```
kappa = 1.02 + 0.98 * exp(-3 * R/X)             PeakFactor
ip    = kappa * sqrt(2) * Ik''                  peak short-circuit current
idc   = sqrt(2) * Ik'' * exp(-2*pi*f*tb * R/X)  DC component at the breaker time tb
m     = (exp(4*f*Tk*ln(kappa-1)) - 1) / (2*f*Tk*ln(kappa-1))        ThermalDcFactor
n     = 1
Ith   = Ik'' * sqrt(m + n)                      thermal-equivalent current over Tk
```

`kappa` lies in `(1.02, 2.0]`: a purely reactive loop (`R/X = 0`) gives exactly 2, a resistive one tends to 1.02.

**Meshed networks (Method B, the default).** The study first decides whether the fault network is meshed
(`Iec60909.IsMeshed`): its graph has the buses of the fault bus's island plus one earth node as nodes, and every
in-service branch (each parallel circuit counted) plus one edge from each synchronous source to earth as edges. A
connected graph with one edge fewer than nodes is a tree, the radial single-source case; any further edge (a ring, a
parallel circuit, a second source) makes the network meshed. In a meshed network the peak takes IEC 60909-0 Method B's
safety factor (`Iec60909.PeakFactorMeshed`):

```
kappa_b = max( kappa, min( 1.15 * kappa, cap ) )      cap = 1.8 when Un <= 1 kV, else 2.0
ip      = kappa_b * sqrt(2) * Ik''
```

The cap bounds what the safety factor adds; it never takes `kappa_b` below `kappa`. The factor is always applied in a
meshed network (the standard's permission to omit it while every branch has `R/X` below 0.3 is not used). `m` keeps
`kappa(R/X)` in both cases, because the DC heat is set by the network's own time constant; `idc` uses `R/X` directly.
The duty records the choice in `Duty.MeshedPeakFactor`.

**Method C (`PeakFactorMethod.MethodC`, verb `--peak-method c`).** The equivalent-frequency method rebuilds the same
fault network with every reactance (branch `X`, each machine's `X''d`, unit-transformer `X`) scaled by
`fc/f = 0.4` (`Iec60909.MethodCFrequencyRatio`), resistances, taps and phase shifts unchanged, and takes

```
R/X_C   = (R_c / X_c) * 0.4                 Z_c = R_c + j X_c, the driving point at fc
kappa   = 1.02 + 0.98 * exp(-3 * R/X_C)     no safety factor, no cap
idc     = sqrt(2) * Ik'' * exp(-2*pi*f*tb * R/X_dc)
R/X_dc  = (R_c / X_c) * r                   the network at fc/f = r, r set by f*tb
r       = 0.27 below 1, 0.15 below 2.5, 0.092 below 5, 0.055 below 10 cycles   (marked [VERIFY] in the source)
```

`f*tb` at or above 10 cycles is refused (`ArgumentOutOfRangeException`): the table ends there. For `ll`, `slg` and `dlg`
the loop is the fault's own sequence connection (`Z1 + Z2`, `Z1 + Z2 + Z0`, `Z1 + Z2 || Z0`), every sequence network at
the equivalent frequency, and an earth fault with no zero-sequence loop is refused. Method C moves only `ip`, `kappa` and
`idc`; `Ik''`, `Ib`, `Ik`, `Ith` and `m` are Method B's. A single series path gives exactly the network's own `R/X`, so
there Method C equals Method B without the 1.15. The duty carries `PeakMethod`, `PeakRoverX` and `DcRoverX`.

When
`|ln(kappa-1)| < 1e-9` (`kappa` = 2) the removable `0/0` of `m` is returned as its limit, 2; as `kappa` tends to 1,
`m` tends to 0. `f` is `GridModel.FrequencyHz` in the verb (50 Hz by default) and `FrequencyLimits.NominalHz` (50.0) as
the library default. `n` is fixed at 1 for every fault, near or far from a generator.

### Breaking current

`Ib` is the symmetrical current the breaker interrupts at its minimum time delay `tmin` (the verb's `--tb`). The AC
envelope of a machine's contribution decays during the fault. Two closed-form decay factors are provided, with break
times `0.02, 0.05, 0.10, 0.25 s` (the private table `Iec60909.BreakTimes`):

```
mu(r, tmin), r = Ik''_G / IrG, the machine's contribution over its rated current
  r <= 2 : mu = 1                                (far from generator: no decay)
  tmin = 0.02 s : mu = 0.84 + 0.26 * exp(-0.26 r)
  tmin = 0.05 s : mu = 0.71 + 0.51 * exp(-0.30 r)
  tmin = 0.10 s : mu = 0.62 + 0.72 * exp(-0.32 r)
  tmin >= 0.25 s: mu = 0.56 + 0.94 * exp(-0.38 r)
  between break times: linear interpolation of the two neighbouring values; below 0.02 s the
  0.02 s form, at or above 0.25 s the 0.25 s form; the result is capped at 1

q(m, tmin), here m = rated MW per pole pair of an asynchronous motor (m > 0, else ArgumentOutOfRangeException)
  tmin = 0.02 s : q = 1.03 + 0.12 ln(m)
  tmin = 0.05 s : q = 0.79 + 0.12 ln(m)
  tmin = 0.10 s : q = 0.57 + 0.12 ln(m)
  tmin >= 0.25 s: q = 0.26 + 0.10 ln(m)          interpolated and capped at 1 as for mu
```

`BreakingFactorMu` and `MotorBreakingFactorQ` are public pure functions. `SynchronousBreakingCurrentKa(Ik'', IrG, tmin)`
returns `mu * Ik''` for one machine that carries the whole fault; it throws for `IrG <= 0`. The model has no
asynchronous-motor data, so `q` is a function only; no duty uses it.

**Network breaking current (three-phase).** `ThreePhase` folds the per-machine `mu` into `Ib` by the meshed-network
(multiple-fed) formula of IEC 60909-0:2016 (subclause 9.1.7, breaking current in meshed networks, read from the 2016 contents list and
marked `[VERIFY]`):

```
Ib = Ik'' - sum_g ( |V_g| / c ) * (1 - mu_g) * I_g                      (kA, summed over synchronous machines)
V_g  = c * Z_gk / Z_kk                  during-fault voltage at machine g's bus (pu)
I_g  = V_g / (j * X_sys,g)              the machine's partial current (pu), I_g[kA] = |I_g| * BaseMva/(sqrt(3)*Un)
r_g  = |I_g| / (MBase_g / BaseMva)      contribution over rated current, base-independent
mu_g = BreakingFactorMu(r_g, tb)
```

`|V_g|/c` is the machine's terminal voltage dip relative to the source (`|dU''_G| / (c*Un/sqrt(3))`), so a machine at the
fault bus has weight 1 and a single machine at its own terminals gives exactly `Ib = mu * Ik''`. When every `mu_g` is 1
the deduction is exactly zero and `Ib = Ik''` bit for bit (the duty record is returned unchanged). The result is checked:
`Ib` outside `(0, Ik'']` or a non-finite deduction throws `InvalidOperationException`. Converter infeeds are added after
this step with `mu = 1`. `PartialThreePhase` uses the same code, so its `TotalBreakingIbKa` equals `ThreePhase`'s
`BreakingIbKa` to the bit when its `minTimeDelaySeconds` equals `ThreePhase`'s `breakTimeSeconds`.

Unbalanced faults report `Ib = Ik''`: no flux decay is taken into account for them (IEC 60909-0:2016 subclause 9.2, "Unbalanced short-circuits", read from the 2016 contents list and marked `[VERIFY]`).
The steady-state current is `Ik = Ik''` for every fault, and `n` is 1. `Duty.SteadyBasis` (`SteadyStateBasis`) says
what that number is:

| Basis | When | Meaning |
|---|---|---|
| `FarFromGenerator` | every machine feeds at most twice its rated current | `Ik = Ik''` exactly |
| `MeshedMultipleFed` | near a generator, several synchronous sources | the standard's `Ik = Ik''M` (subclause 11.2.7); no motors are modelled, so it is `Ik''` |
| `SingleFedUpperBound` | near the only synchronous source of the fault | an upper bound: the standard's `Ik = lambda * IrG` needs the machine's `x_dsat`, which the model does not carry; `Ith` with `n = 1` is an upper bound too |
| `UnbalancedNoDecay` | `ll`, `slg`, `dlg` | no decay for unbalanced faults, `Ik = Ib = Ik''` |

### Converter-interfaced plant

A generator is converter-interfaced when `Generator.ConverterInterfaced` is true, or when it is unset and `Type` is a
converter type: `wind`, `onshore`, `offshore`, `wind-onshore`, `wind-offshore`, `solar`, `pv`, `storage`, `battery`,
`bess`, `interconnector`, `hvdc`, `tidal` (`ConverterPlant.IsConverterType`, case-insensitive, `_` read as `-`; pumped
storage is synchronous). Such a unit has no internal EMF behind a reactance. It is left out of the source network and
enters as a current source:

```
I_skPF,j = k_j * MBase_j / BaseMva                 (pu, system base)   k = EffectiveConverterFaultCurrentPu
uplift   = sum_j |Z_kj| * I_skPF,j                 Z_kj = transfer impedance, bus j to fault bus
Ik''     = (c + uplift) / |Z_kk|                   (pu)
```

`k` is `Generator.ConverterFaultCurrentPu` when set (zero is meaningful: the unit feeds nothing, as a line-commutated
link), else `Generator.DefaultConverterFaultCurrentPu`, 1.2. The JSON v1 case folder sets the fields per generator as
`converterInterfaced`, `converterFaultCurrentPu`, `powerFlowOnly` and `unitTransformerXPu` (JSON v2 nests the first two
as `converter.interfaced` and `converter.faultCurrentPu`); the `uk` case gives
`converterFaultCurrentPu: 0.0` to Moyle, IFA and BritNed. `Z_kj` is row `k` of `Zbus`: the column already solved when
every in-service branch has zero phase shift (Y is then symmetric), else a second solve on the transposed system. Each
converter current is phased `arg I_j = -arg Z_kj`, so its share is in phase with the synchronous share and the arithmetic
sum is exact. A controlled current has no DC offset and no AC decay:

```
converter share  pf = uplift / |Z_kk|                       (pu), reported in kA as Duty.ConverterIkppKa
Ik''   = Ik''_sync + pf
ip     = ip_sync + sqrt(2) * pf                              (no kappa)
Ib     = Ib_sync + pf          (mu = 1)
Ik     = Ik_sync + pf
Ith    = sqrt(m * Ik''_sync^2 + Ik''^2)                      (n = 1)
```

`kappa`, `m`, `R/X` and `idc` stay those of the synchronous part. For `ll`, `slg` and `dlg` the converters inject balanced
positive-sequence current and are open in the negative sequence, so each sequence formula is evaluated once more with
the source `uplift` in place of `c`, and that share is added by the same rules. A model with no synchronous source is
refused with `InvalidOperationException`: the equivalent-source method needs one.

### Unbalanced faults

`Iec60909.FromSequenceImpedances(type, c, Un, Z1, Z2, Z0, BaseMva, ...)` connects the sequence networks. With
`i1 = c / loopZ`:

| Fault (`UnbalancedFaultType`) | Connection | loopZ | Reported `Ik''` (pu) |
|---|---|---|---|
| `SingleLineToGround` | `Z1`, `Z2`, `Z0` in series | `Z1 + Z2 + Z0` | `\|3c / loopZ\|` |
| `LineToLine` | `Z1`, `Z2` in series | `Z1 + Z2` | `\|sqrt(3) c / loopZ\|` |
| `DoubleLineToGround` | `Z2` parallel `Z0`, in series with `Z1` | `Z1 + Z2*Z0/(Z2+Z0)` | `\|3 I0\|`, `I0 = -i1 * Z2/(Z2+Z0)` |

For `DoubleLineToGround` the reported current is the earth current, not the line-conductor currents. `kappa`, `m` and `idc`
use `R/X = Re(loopZ)/Im(loopZ)`; `Duty.TheveninRPu` and `TheveninXPu` carry the positive-sequence `Z1`. `Ib` and `Ik`
equal `Ik''`; `Ith = Ik'' sqrt(m + 1)`. The model entry points are `LineToLine` (positive and negative networks only),
`SingleLineToGround` and `DoubleLineToGround` (all three). `Z1` is `PositiveSequenceThevenin`, `Z2` is
`NegativeSequenceThevenin`, `Z0` is `ZeroSequenceThevenin`.

### Zero-sequence network

`Iec60909.BuildZeroSequenceNetwork` builds the zero-sequence admittance network from each in-service branch.
A branch with no winding data (`FromWinding` or `ToWinding` null) is a line; one with both is a transformer.

| Branch | Zero-sequence element |
|---|---|
| Line | series `Z0 = R0 + jX0` if either is non-zero, else `lineZeroSeqRatio * (R + jX)` (default 3.0); nothing if that is zero |
| Transformer, `WyeGrounded` + `WyeGrounded` | series `Z0T + 3*FromNeutralXPu*j + 3*ToNeutralXPu*j` between the buses |
| Transformer, `WyeGrounded` (from) + `Delta` (to) | shunt `1/(Z0T + j*3*FromNeutralXPu)` at the from bus, which gains an earth path |
| Transformer, `Delta` (from) + `WyeGrounded` (to) | shunt `1/(Z0T + j*3*ToNeutralXPu)` at the to bus, which gains an earth path |
| Any other pairing (`Delta`+`Delta`, any `WyeUngrounded`, ...) | blocks the zero sequence |

`Z0T = R0 + jX0` of the branch when either is non-zero, else its positive-sequence `R + jX`. Generators add no
zero-sequence path. `ZeroSequenceThevenin` walks the series edges breadth-first from the fault bus; if the component
reaches no earth path the bus is ungrounded, `Grounded` is false and `Z0` is infinity. `SingleLineToGround` and
`DoubleLineToGround` then return an all-zero duty (every current 0, the `R/X` and factors of `Z1` for reference). Otherwise
`Z0` is the driving point of `Y_sub x = e_k` on that grounded component only, so an isolated zero-sequence island
elsewhere cannot make the solve singular. The operating-point earth-fault study uses the same network builder.

### Partial currents and branch currents

`PartialThreePhase` decomposes the three-phase fault with the same single `Zbus` column: `V_g = c*Z_gk/Z_kk` and
`I_g = V_g/(j X_sys,g)`. Each `MachinePartialCurrent` carries `IkppKa`, `IkppPu`, `RatedCurrentKa`, `Mu`, `BreakingIbKa`
(its own single-fed `mu_g * |I_g|`) and `IsConverter`. A converter's entry is `I_skPF * |Z_kj|/|Z_kk|` with `mu = 1`.
`TotalIkppPu` is `|c/Zkk|` plus the converter partials; the machine entries are separate magnitudes and are not
summed into it. For a lossless radial network the partials are in phase and the machine magnitudes sum to it; in a
lossy network they exceed it (WSCC-9 bus 5: 1.4690796589338535 kA against `Ik''` 1.4683989584292985 kA). `TotalBreakingIbKa` is the network formula above, not the sum of the single-fed values. Currents in kA are
referred to the fault bus voltage: the base is `BaseMva/(sqrt(3)*Un)` of the fault bus.

`BranchFaultCurrents(model, bus)` returns, for each in-service branch, the from-end and to-end current magnitudes during a
three-phase fault with the `c_max` source, whatever the objective. The bus voltages are `V_i = c Z_ik/Z_kk` plus the converter-driven voltages
(one extra solve on the same factorisation); each end current uses the shared series-plus-tap stamp
`AdmittanceStamp.Branch(R, X, 0, TapRatio, PhaseShiftDeg)` (charging zero, a `TapRatio` of 0 read as 1):

```
iFrom = Yff * Vf + Yft * Vt          iTo = Ytf * Vf + Ytt * Vt            (pu; kA = pu * BaseMva/(sqrt(3)*Un))
```

This is the current a protective relay on that branch measures; it is the input to grading a relay chain
([ch. 26](<0026 Overcurrent coordination (IDMT grading).md>)).

### Impedance-correction factors

Three closed forms correct machine and transformer impedances so that the equivalent source reproduces the true
contribution at rated conditions. All three are public pure functions.

```
K_G  = (Un / UrG) * c_max / (1 + x''d * sin(phi_rG))           GeneratorCorrectionKg
K_T  = 0.95 * c_max / (1 + 0.6 * x_T)                          TransformerCorrectionKt
K_S  = (UnQ/UrG)^2 * (UrTLV/UrTHV)^2 * c_max / (1 + |x''d - x_T| * sin(phi_rG))      PowerStationUnitCorrectionKs
K_SO = UnQ / (UrG*(1+p_G)) * (UrTLV/UrTHV) * (1 +/- p_T) * c_max / (1 + x''d*sin(phi_rG))   PowerStationUnitCorrectionKso
sin(phi_rG) = sqrt(1 - cos(phi_rG)^2)
```

`K_G` throws unless `UrG > 0` and `cos(phi_rG)` is in `[0, 1]`; `K_T` throws for `x_T < 0`; `K_S` and `K_SO` throw
unless every rated voltage is above zero and `cos(phi_rG)` is in `[0, 1]`. `K_G` equals `c_max` at matched voltage with
no reactive loading; `K_T` equals `0.95*c_max` at `x_T = 0`.

`Iec60909.ApplyCorrectionFactors(model, ratedPowerFactor, unitTransformersHaveOltc = true)` returns a corrected copy
of the model (the input is unchanged) for a maximum-current study. The duty methods never apply the factors themselves;
a study runs on the model it is given, and the verb applies them only under `--corrections`.

| Element | Condition | Factor | What is multiplied |
|---|---|---|---|
| Generator, directly connected | in service, not converter, not power-flow-only, `UnitTransformerXPu = 0` | `K_G` | `X''d_eff` and `X2_eff` |
| Power station unit | same, `UnitTransformerXPu > 0` | `K_S` (OLTC) or `K_SO` (`unitTransformersHaveOltc` false; `p_G = 0`, main tap) | `X''d_eff`, `X2_eff` and `UnitTransformerXPu` |
| Network transformer | `Branch.IsTransformer`, in service, `RateAMva > 0` | `K_T` | `R`, `X`, `R0`, `X0` |

The model is per unit on nominal voltages, so every voltage ratio in the formulas is 1. `c_max` is the Table 1 maximum
factor at the element's bus (the higher of a transformer's two buses). `x_T = |X| * RateAMva / BaseMva`, taking `RateAMva`
as the transformer's rated power. A transformer with no `RateAMva`, and any element on a bus with no base voltage
(`BaseKv` 0 or below, where no Table 1 row applies), is left uncorrected and listed in
`CorrectionReport.NotApplied`. The rated power factor is the caller's input and must be in `(0, 1]`, because the model
carries none; it is never assumed.

### Breaker rating check

`CheckBreaker(duty, rating, systemVoltageKv)` compares a `Duty` with a `BreakerRating(RatedVoltageKv, MakingPeakKa,
BreakingSymKa, ShortTimeWithstandKa, ShortTimeSeconds = 1.0)`. Margins are rating minus demand in kA, so a margin of
zero or more passes:

```
voltage  : RatedVoltageKv >= systemVoltageKv
making   : MakingPeakKa   - ip
breaking : BreakingSymKa  - Ib
thermal  : ShortTimeWithstandKa * sqrt(min(1, ShortTimeSeconds / FaultDurationSeconds)) - Ith
           (the rating is used unscaled when either time is not above zero)
Adequate = all four pass
```

The thermal rule is equal energy, `I_thr^2 * Tkr >= Ith^2 * Tk`, for a fault at least as long as the rated duration
(`Tk >= Tkr`). For a shorter fault the criterion is `Ith <= I_thr` (IEC 60865-1 clause 7): the rated short-time current
is also an electromechanical ceiling, so the referral only ever scales the rating down, never up. `Duty` carries its own `BreakTimeSeconds` and
`FaultDurationSeconds`, so the check needs no other input.

### Network size and islands

`FaultNetworkSystem` factors `Y` once per study and reuses it for every right-hand side (a `Zbus` column, a row, the
converter response). Every fault network, at every size, is factored by the real 2n-expansion sparse LU
(`RealExpandedComplexSolver`); there is no bus-count switch. The dense complex LU survives only as a test oracle
(`FaultNetworkSystem.DenseReferenceScope`, internal), which tests use to cross-check the sparse answers; no production
path can select it. A singular network fails loud: the sparse factor reports the singular matrix and the study throws
`InvalidOperationException`.

Before it builds the network the study keeps only the fault bus's island (`Iec60909.FaultIsland`): the buses connected
to the fault bus through in-service branches, the branches between them and the generators on them, in the model's own
order. A `Zbus` column of the fault bus is exactly zero outside its island, so dropping those buses changes no number,
and a dead island elsewhere in the model (no source, so no path to earth) does not make the matrix singular. A
connected model is used as it is. A fault bus whose own island has no synchronous source is refused by name.

## Algorithm

`ThreePhase(model, bus, objective, subtransientXdPu = 0.20, f0Hz, breakTimeSeconds = 0.10, faultDurationSeconds = 1.0)`:

```
1. index buses; Un = Bus.BaseKv of the fault bus (<= 0 -> InvalidOperationException; unknown bus -> ArgumentException)
2. for each generator in GridModel.Generators:
       PowerFlowOnly            -> skip
       converter-interfaced     -> record current source I_skPF
       else                     -> shunt Y_g = 1/(j X_sys,g)
   no synchronous machine       -> InvalidOperationException
3. factor Y (series-only network + shunts); solve Y x = e_k          -> Zkk
   converters present: Z_kj from row k (column k if Y symmetric, else a transposed solve)
4. c = VoltageFactor(Un, objective)
5. sync   = FromThevenin(c, Un, Re Zkk, Im Zkk, BaseMva, f0, tb, Tk)   -> Ik'', kappa, ip, idc, m, Ith, Ib = Ik = Ik''
6. sync   = WithNearGeneratorBreaking(sync)                           -> Ib from the multiple-fed formula
7. result = WithConverterInfeed(sync, uplift/|Zkk|)                   -> adds pf to Ik'', ip, Ib, Ik, Ith
```

The unbalanced entry points run steps 1 to 4, then `FromSequenceImpedances` with `Z1 = Zkk`, `Z2` and `Z0`, then, when the bus is
grounded, add the converter share by the rule above (an ungrounded bus returns the all-zero duty). `ZeroSequenceThevenin` is not solved for `ThreePhase` or `LineToLine`.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| `c` maximum | 1.10 (`Un > 1 kV`), 1.05 (`Un <= 1 kV`) | - | Voltage factor, equipment duty | `Iec60909.VoltageFactor` |
| `c` minimum | 1.00 (`Un > 1 kV`), 0.95 (`Un <= 1 kV`) | - | Voltage factor, protection sensitivity | `Iec60909.VoltageFactor` |
| `kappa` constants | 1.02, 0.98, 3 | - | `1.02 + 0.98*exp(-3 R/X)` | `Iec60909.PeakFactor` |
| `m` limit tolerance | `1e-9` on `\|ln(kappa-1)\|` | - | Below it `m` returns 2 | `Iec60909.ThermalDcFactor` |
| `n` | 1 | - | Thermal AC factor | `Iec60909.Assemble` |
| Break times | 0.02, 0.05, 0.10, 0.25 | s | Tabulated `tmin` for `mu` and `q` | `Iec60909.BreakTimes` |
| `mu` far-from-generator threshold | `Ik''_G/IrG <= 2` gives `mu = 1` | - | No AC decay | `Iec60909.BreakingFactorMu` |
| `mu` coefficients | 0.84/0.26/0.26, 0.71/0.51/0.30, 0.62/0.72/0.32, 0.56/0.94/0.38 | - | `a + b*exp(-d r)` at 0.02, 0.05, 0.10, 0.25 s | `Iec60909.BreakingFactorMu` |
| `q` coefficients | 1.03/0.12, 0.79/0.12, 0.57/0.12, 0.26/0.10 | - | `a + b*ln(m)` at 0.02, 0.05, 0.10, 0.25 s | `Iec60909.MotorBreakingFactorQ` |
| `K_T` constants | 0.95, 0.6 | - | Fixed constants of the transformer factor | `Iec60909.TransformerCorrectionKt` |
| Default `X''d` | 0.20 | pu on `MBase` | Machine with no `SubtransientXdPu` | `Iec60909.DefaultSubtransientXdPu` = `FaultAnalysis.DefaultSubtransientXdPu` |
| Default `X2` | `X''d_eff` | pu on `MBase` | Machine with no `NegativeSeqXPu` | `Generator.EffectiveNegativeSeqXPu` |
| Default converter `k` | 1.2 | x rated | Converter with no `ConverterFaultCurrentPu` | `Generator.DefaultConverterFaultCurrentPu` |
| Line zero-sequence ratio | 3.0 | x `Z1` | Line with no `R0`/`X0` | `lineZeroSeqRatio` of `SingleLineToGround`, `DoubleLineToGround`, `ZeroSequenceThevenin` |
| Transformer `Z0T` | `R + jX` | pu | Transformer with no `R0`/`X0` | `Iec60909.BuildZeroSequenceNetwork` |
| Neutral term | `3 * FromNeutralXPu`, `3 * ToNeutralXPu` | pu | Added to the earthed path | `Branch.FromNeutralXPu`, `Branch.ToNeutralXPu` |
| Breaker time `tb` | 0.10 | s | Breaker minimum time delay (`mu`, `idc`) | `breakTimeSeconds`; verb `--tb` |
| Fault duration `Tk` | 1.0 | s | Duration for `m` and `Ith` | `faultDurationSeconds`; verb `--tk` |
| Frequency `f` | `GridModel.FrequencyHz` (the case's own: 50 Hz for GB cases, 60 Hz for `case9`) | Hz | `idc`, `m` | `f0Hz`; `FrequencyLimits.NominalHz` (50.0) is the library default |
| Meshed safety factor | 1.15, capped at 2.0 (1.8 at `Un <= 1 kV`), never below `kappa` | - | `kappa_b` in a meshed network | `Iec60909.MeshedPeakSafetyFactor`, `PeakFactorMeshed` |
| Method C peak ratio `fc/f` | 0.4 | - | Equivalent frequency for `kappa` | `Iec60909.MethodCFrequencyRatio` |
| Method C DC ratios `fc/f` | 0.27, 0.15, 0.092, 0.055 below `f*tb` = 1, 2.5, 5, 10 | - | Equivalent frequency for `idc`; `[VERIFY]` | `Iec60909.MethodCDcRatioBelow1` to `MethodCDcRatioBelow10`, `MethodCDcTableEndFt` |
| Peak method | B | - | `--peak-method b` or `c` | `PeakFactorMethod.MethodB` |
| Breaker short-time duration | 1.0 | s | Duration of the withstand rating | `BreakerRating.ShortTimeSeconds` |
| Singular residual | `1e-9` | relative | Rejects a dense-oracle solve that misses `Y x = b` (tests only) | `FaultNetworkSystem.SingularResidualRel` |
| Generator reactance basis | `X_sys = X * BaseMva / MBase` | pu | Converts a machine reactance to the system base | `Iec60909.BuildPositiveNetwork` |

## The `iec60909` verb

```
gridsim iec60909 <case> (--bus <id> | --all-buses) [--type 3ph|ll|slg|dlg] [--objective max|min] [--tb 0.10] [--tk 1.0]
                 [--breaker kV,making_kA,breaking_kA,withstand_kA[,seconds[,asym_kA]]] [--branches [--top N]]
                 [--corrections --rated-pf <cos phi> [--no-unit-oltc]] [--peak-method b|c]
```

The verb maps `--type` to `ThreePhase`, `LineToLine`, `SingleLineToGround` or `DoubleLineToGround`, `--objective` to
`ShortCircuitObjective`, `--peak-method` to `PeakFactorMethod` (default `b`), and passes `GridModel.FrequencyHz`, `--tb`
and `--tk`. The flag semantics are in the
[CLI reference](<../manual/0019 CLI reference.md#iec60909>). The single-bus duty lines print at round-trip precision;
branch currents print to three decimals, and the sweep table and correction ranges to four.

**One bus.** The output is a header line (case, bus, fault type, objective, frequency, `tb`, `Tk`), then:

| Line | Fields |
|---|---|
| `Ik''` | kA, pu, and the converter share in kA when above zero |
| `ip` | kA, `kappa`, `R/X`; then `meshed network: 1.15 x kappa(R/X), Method B` when the meshed factor applies, or `Method C: kappa(R/X_C), R/X_C <value> at fc = 0.4 f` under `--peak-method c` |
| `Ib` and `Ik` | kA each; then a note on the steady-state basis (none far from every generator; the multiple-fed rule; `UPPER BOUND` single-fed; `unbalanced fault: IEC 60909-0 takes no decay`) |
| `Ith` | kA, `m`; and `idc(tb)` in kA |
| `Zth` | `Zkk = R + jX` of the positive sequence, pu |

With `--breaker` a verdict line (`ADEQUATE` or `NOT ADEQUATE`) follows, then the voltage status and the three margins
(making, breaking, thermal), and the asymmetrical breaking margin when the sixth value is given. The rated voltage is
compared with the highest system voltage `Um` (`Iec60909.HighestSystemVoltageKv`: the lowest IEC 60038 `Um` at or above
the nominal voltage above 1 kV, the nominal plus 10 % at or below 1 kV), because IEC rates a device by `Um`. A
double-line-to-earth duty reports the earth current `3 I0`; its breaker check scales every current demand by
`Duty.DlgLineToEarthRatio`, the largest line-conductor current over the earth current, when that is above 1. With `--branches` every in-service branch current in the fault bus's island follows, largest first (`--top N` prints the
first `N` and a footer counts the rest; the result file holds every branch); they
are always computed with `c_max`, so `--objective min --branches` prints `Ik''` at `c_min` beside branch currents at
`c_max`. With `--corrections` a summary of the factors applied (per
factor: element count, minimum and maximum value to four decimals) precedes the duty.

**All buses.** `--all-buses` prints one row per bus with a base kV (bus, kV, `Ik''`, `ip`, `Ib`, `Ith`, plus a breaker
verdict with `--breaker`, each bus checked at its own voltage), then the count studied and the bus with the largest
`Ik''`. A bus that cannot be studied (no base kV, no source) is listed with the reason.

**Result file.** With `--json`, `--csv` or `--out` the summary carries `case`, `bus`, `type`, `objective`, `frequency_hz`,
`tb_s`, `tk_s`, `ikpp_ka`, `ikpp_pu`, `converter_ikpp_ka`, `ip_ka`, `kappa`, `r_over_x`, `ib_ka`, `ik_ka`, `ith_ka`,
`thermal_m`, `idc_ka`, `thevenin_r_pu`, `thevenin_x_pu`, `kappa_meshed`, `ik_basis`; `peak_method` and `r_over_x_c`
under `--peak-method c`; `earthing_data` for `slg` and `dlg`; the breaker margins and
`breaker_adequate` with `--breaker`; tables `branch_currents`, `corrections` and (for `--all-buses`) `buses`.

**Exit codes.**

| Code | Condition |
|---|---|
| 0 | The duty was computed and every requested check passed |
| 1 | Case load error; `InvalidOperationException` or `ArgumentException` from the study (no base kV, no synchronous source, singular network); with `--all-buses`, at least one bus could not be studied; `--corrections` rejected by the library |
| 2 | Usage error: no case argument; a flag with a missing or non-numeric value; no `--bus` and no `--all-buses`, or both; unknown bus; `--type` or `--objective` not in its list; `--tb` or `--tk` not above 0; `--branches` with a type other than `3ph` or with `--all-buses`; `--corrections` without `--rated-pf`, `--rated-pf` or `--no-unit-oltc` without `--corrections`, `--rated-pf` outside `(0, 1]`, or `--corrections` with `--objective min`; `--breaker` not 4 or 5 positive finite numbers |
| 3 | A breaker rating is inadequate (at any bus with `--all-buses`); an earth fault (`slg`, `dlg`) on a model that carries no transformer winding connection |

Process tests cover the usage exit, the earth-fault exit 3 and the result file. The other rows were confirmed by
running the Release binary (usage, unknown bus, an inadequate breaker, gb-full) or by reading `Program.cs`.

The last case prints the duty, then says on stderr that the earth-fault duty reflects missing data. A model with no
winding connection has no earthed neutral, so the zero current is a data gap, not a network result. MATPOWER and JSON
v1 carry no winding connections, so every earth fault on such a case exits 3. A JSON v2 case can carry them (N234,
October 2026, phase P5 opened for fault sequence data): transformer `windings`, `r0Pu` / `x0Pu`, `neutralXPu`, and
generator `subtransientXdPu` / `negativeSeqXPu` ([ch. 28](<0028 The v2 JSON schema.md>)); a v2 case that gives windings is
studied with its own earth paths and exits 0. The library entry points accept models built with
`FromWinding`/`ToWinding` (and the bridge protocol carries them).

## Invariants

| Invariant | Test |
|---|---|
| `c` follows Table 1 for both objectives and both voltage classes | `Iec60909Tests.Voltage_factor_follows_table_1` |
| `kappa` equals its closed form; `R/X = 0` gives 2; a resistive loop stays in `(1.02, 1.03)` | `Iec60909Tests.Peak_factor_matches_its_closed_form_and_bounds` |
| A single 0.20 pu generator gives `Zk = j0.20`, `Ik'' = 5.5 pu`, `kappa = 2`, `ip = kappa*sqrt(2)*Ik''` | `Iec60909Tests.Single_generator_bolted_fault_matches_the_hand_calculation` |
| A series reactance 0.30 gives `Zk = j0.50`, `Ik'' = 2.2 pu` | `Iec60909Tests.A_series_reactance_reduces_the_current_by_the_series_impedance` |
| `idc` follows the exact exponential; no decay at `R/X = 0` | `Iec60909Tests.Dc_component_follows_the_exact_exponential_envelope` |
| `m` equals its closed form, equals 2 at `kappa = 2`; `Ith = Ik'' sqrt(m+1)` | `Iec60909Tests.Thermal_factor_matches_its_closed_form_and_removable_limit` |
| Far from the generator (`Ik''_G/IrG <= 2`) `Ib == Ik == Ik''`, exact equality | `Iec60909Tests.Far_from_generator_the_breaking_and_steady_currents_equal_the_initial` |
| Near a generator `Ib = Ik'' (1 - 0.4 (1 - mu))` for the 0.20 + 0.30 network; a single machine at its terminals gives `mu * Ik''`; a longer `tb` decays further; `Ik = Ik''` | `Iec60909Tests.Near_generator_breaking_current_follows_the_multiple_fed_formula` |
| Unbalanced faults keep `Ib == Ik''` | `Iec60909Tests.Unbalanced_faults_keep_the_breaking_current_equal_to_the_initial` |
| WSCC-9 `Ik''` and `Ib` at buses 1, 4, 5, 7 are pinned to the bit (`Assert.Equal` on doubles) | `Iec60909Tests.Case9_breaking_currents_are_pinned` |
| With `Z1 = Z2 = Z0` the single-line and double-line-to-earth currents equal the three-phase `c/\|Z1\|`; line-to-line is `sqrt(3)/2` of it | `Iec60909Tests.Sequence_faults_reduce_to_the_three_phase_for_symmetric_impedances`, `Line_to_line_from_the_model_is_root3_over_2_of_the_three_phase` |
| `Z0 = j0.60` gives `Ik1'' = 3.3 pu` | `Iec60909Tests.Single_line_to_ground_matches_the_hand_calculation` |
| The unbalanced `kappa` uses the connection's loop `R/X` | `Iec60909Tests.Unbalanced_peak_factor_uses_the_sequence_loop_r_over_x` |
| `mu` and `q` follow their closed forms at each tabulated time; `mu = 1` at `Ik''/IrG <= 2`; interpolation is linear; both capped at 1 | `Iec60909Tests.Breaking_factor_mu_matches_its_closed_forms_at_the_tabulated_times`, `Breaking_factor_mu_is_one_far_from_generator`, `Breaking_factor_mu_interpolates_linearly_between_break_times`, `Motor_breaking_factor_q_matches_its_closed_forms`, `Single_machine_breaking_current_applies_mu_to_the_initial_current` |
| One machine carries 100% of the fault; two identical machines split it evenly and their magnitudes sum to the total; `TotalBreakingIbKa` equals the hand multiple-fed formula and exceeds the single-fed sum | `Iec60909Tests.Partial_currents_put_the_whole_fault_on_a_single_machine`, `Partial_currents_split_evenly_between_identical_machines` |
| `PartialThreePhase.TotalBreakingIbKa` equals `ThreePhase.BreakingIbKa` bit for bit at every WSCC-9 bus and at `tmin` 0.05, 0.10, 0.25 s; at 0.10 s it does not exceed `Ik''` | `Iec60909PartialAgreementTests.Case9_partial_total_breaking_current_equals_ThreePhase`, `The_two_agree_at_every_break_time` |
| A radial branch carries the whole fault; identical parallel feeders split it; the current feeds relay grading | `Iec60909Tests.Branch_fault_current_on_a_radial_feeder_is_the_whole_fault`, `Branch_fault_currents_split_between_parallel_feeders`, `Branch_fault_current_feeds_the_coordination_grading` |
| A tap ratio of 0 gives the same branch currents as 1 | `ProtectionWs6Tests.Branch_fault_currents_handle_a_tap_ratio_of_zero_as_unity` |
| A machine's own `X''d` and `X2` replace the defaults (0.10 gives 11.0 pu, 0.40 gives 2.75 pu; `X2 = 0.24` feeds line-to-line) | `Iec60909Tests.Per_machine_subtransient_reactance_overrides_the_synthesised_default`, `Per_machine_negative_sequence_reactance_feeds_the_line_to_line_fault` |
| Delta + grounded-wye earths only the wye bus; `3*Xn` adds to the path; a line and a through transformer sum; no earth means ungrounded | `Iec60909Tests.Zero_sequence_delta_wye_transformer_grounds_only_the_wye_side`, `Zero_sequence_neutral_grounding_adds_three_xn`, `Zero_sequence_series_path_sums_line_and_through_transformer`, `Zero_sequence_without_a_ground_reference_is_ungrounded` |
| Model-wired earth faults match hand networks (`Z1 = Z2 = j0.28`, `Z0 = j0.10` gives `Ik1'' = 5.0 pu`); an ungrounded bus gives zero | `Iec60909Tests.Single_line_to_ground_from_the_model_matches_the_hand_calculation`, `Double_line_to_ground_from_the_model_matches_the_sequence_form`, `Earth_faults_on_an_ungrounded_bus_are_zero` |
| The operating-point earth-fault current times `c` equals the 60909 initial current on a no-load, no-charging network | `EarthFaultAndIec60909CompletionTests.With_no_load_or_charging_the_operating_point_slg_current_times_c_is_the_iec_60909_initial_current` |
| The zero-sequence driving point is unchanged by the shared builder (`j0.7` on the radial case) | `EarthFaultAndIec60909CompletionTests.The_zero_sequence_thevenin_is_unchanged_by_the_shared_builder` |
| `K_G`, `K_T`, `K_S`, `K_SO` follow their closed forms and reductions | `Iec60909Tests.Generator_correction_kg_matches_its_closed_form`, `Transformer_correction_kt_matches_its_closed_form`, `EarthFaultAndIec60909CompletionTests.Ks_and_kso_follow_their_closed_forms` |
| `ApplyCorrectionFactors` scales the right impedances, leaves the input untouched, lists an unrated transformer, rejects a power factor of 0 | `EarthFaultAndIec60909CompletionTests.Applying_the_corrections_scales_the_generator_and_transformer_impedances_and_leaves_the_input_untouched` |
| The four breaker checks and their exact margins; the equal-energy thermal scaling | `Iec60909Tests.Breaker_adequacy_checks_each_rating_against_the_duty`, `Breaker_thermal_check_scales_by_equal_energy` |
| Converter share: `k * rated` at the fault bus, transfer-scaled at a remote bus, no `kappa` on the share, `mu = 1`, zero `k` feeds nothing, converter-only model refused, power-flow-only unit is no source, unit transformer is in series | `ConverterFaultContributionTests` (`Iec_three_phase_adds_exactly_k_times_rated_for_a_converter_at_the_fault_bus`, `Iec_remote_converter_share_scales_by_transfer_over_driving_point_impedance`, `Iec_partial_currents_mark_the_converter_and_sum_to_the_total`, `Iec_branch_current_carries_the_converter_infeed_to_a_remote_fault`, `Iec_line_to_line_adds_the_sequence_formula_driven_by_the_converter_term`, `Iec_converter_only_model_is_refused_with_a_clear_reason`, `Zero_k_unit_feeds_no_fault_current_in_either_study`, `Power_flow_only_unit_is_no_fault_source`, `Unit_transformer_is_in_series_with_the_machine`) |
| Converter classification and the explicit-flag override | `ConverterFaultContributionTests.Converter_types_are_classified`, `Explicit_flag_overrides_the_type_and_k_defaults` |
| Line charging and bus shunts do not enter `Z1`, `Z2` or `Ik''` | `ProtectionWs6Tests.Iec60909_networks_neglect_line_charging_and_bus_shunts` |
| A sourceless island fails loud; a dead island elsewhere changes no bit of a live fault, and the sparse engine solves it | `ProtectionWs6Tests.A_fault_on_a_sourceless_island_fails_loud_on_the_dense_path`, `A_fault_in_the_live_network_still_solves_when_a_dead_island_is_present`, `Doc4ProtectionIec60909Tests.D096_a_sourceless_island_elsewhere_changes_no_bit_and_the_sparse_path_now_solves` |
| Every fault network is the sparse engine at every size, and the dense oracle does not leak out of its scope | `Doc16NumericsDeterminismTests.N233_every_fault_network_is_the_sparse_engine_at_every_size_and_the_oracle_does_not_leak` |
| The sparse `Zbus` agrees with the dense oracle to `1e-9` of the column's largest entry on three columns and three rows of IEEE 118 and 300 | `FaultNetworkSystemTests.Sparse_zbus_columns_and_rows_match_dense`, `Sparse_triplet_network_matches_dense` |
| Meshed `kappa_b` is 1.15 `kappa`, capped, never below `kappa`; the steady-state basis is reported | `Doc4ProtectionIec60909Tests.D098_meshed_kappa_is_one_point_one_five_times_kappa_capped`, `D097_the_steady_state_basis_says_exact_meshed_or_upper_bound` |
| Method C moves only the peak and `idc`; on a single series path it gives the network's own ratio; beyond the DC table it is refused | `Cf3OpenCodeFindingsTests.N134_method_c_moves_the_peak_only`, `N134_on_a_single_series_path_method_c_gives_the_networks_own_ratio`, `Cf4HarmonicAfeAndMethodCTests.N134_rest_three_phase_method_c_gives_the_dc_component_from_its_own_ratio`, `N134_rest_method_c_beyond_the_dc_table_is_refused`, `N134_rest_unbalanced_method_c_moves_the_peak_and_dc_only` |
| The factor-once dense LU equals the one-shot solve exactly, on a random 23x23 complex matrix | `ConverterFaultContributionTests.Factor_once_lu_is_bit_identical_to_the_one_shot_solve` |
| Result-file `ikpp_ka` has the exact bits the console prints | `CliResultFilesProcessTests.Json_numbers_are_the_exact_bits_the_console_prints_in_round_trip_form` |
| An earth fault without winding data exits 3 and says why; three-phase and line-to-line are unaffected; the result file records `earthing_data = false` | `CliS4CliAndLoaderProcessTests.N116_iec60909_earth_fault_without_winding_data_exits_3_and_says_why`, `N116_iec60909_faults_without_earth_path_are_unaffected`, `N116_iec60909_result_file_records_missing_earthing_data` |
| No literal 50 Hz in the module: `f0Hz` is a parameter defaulting to `FrequencyLimits.NominalHz`, and the verb passes the model's frequency (no test runs a non-50 Hz model) | `Ws13CoreConstantsAntiDriftTests` (no `f0Hz = 50.0` in `Iec60909.cs`) |

Most closed-form rows assert to 12 decimal places (`Assert.Equal(expected, actual, 12)`), not to the bit; the
single-generator and partial-current rows assert to 10, the converter, island and live-network rows to 9 or 10, and the
`K_S`/`K_SO` row to 12 and 15. The WSCC-9 pins, the far-from-generator row and the partial-versus-three-phase agreement
tests are exact. The suite checks the formulas against
hand-solved networks and against the textbook sequence-network connections. It holds no agreement test against a
published IEC 60909 worked example, such as the examples of IEC TR 60909-4 (see Limits).

## Worked example

Every number below comes from the captures in `docs/_captures/iec60909/`, except the bus-1 partial-current table,
which is recomputed offline from the `Zbus` column (no verb prints partial currents; an independent recomputation
agrees to about 1e-15). Timings do not appear; the values are
deterministic. WSCC-9 (`case9`) is a 345 kV network on 100 MVA with three generators of 247.5, 192 and 128 MVA.
`IeeeCases.Case9` carries no machine reactance, so each machine takes the 0.20 pu default on its own `MBase`, which is
`0.20 * 100 / 247.5 = 0.0808`, `0.1042` and `0.1563` pu on the system base.

`case9` carries its WSCC nominal frequency, 60 Hz, and is meshed (9 branches and 3 machine sources, 12 edges, over 9
buses and the earth node), so `ip` takes the Method B factor `kappa_b = min(1.15 * kappa, 2.0)`: the `kappa` the `ip`
line prints is `kappa_b`, and the line carries the `meshed network` note. The frequency enters `m`, `Ith` and `idc`.
`uk` runs at 50 Hz and is meshed too, so its `ip` takes `kappa_b` (`1.15 * 1.916` capped at 2.0) on the synchronous
share.

### Three-phase fault at bus 5

```
$ gridsim iec60909 case9 --bus 5
IEC 60909 - IEEE 9-bus (WSCC), bus 5, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 1.4683989584292985 kA  (8.774528526143639 pu)
  ip    = 4.143787818206106 kA  (kappa 1.995438943368623, R/X 0.10501342238071008, meshed network: 1.15 x kappa(R/X), Method B)
  Ib    = 1.4683989584292985 kA    Ik = 1.4683989584292985 kA
  Ith   = 1.4881526648788939 kA  (m 0.027086065637215752)    idc(tb) = 0.03963009392407432 kA
  Zth   = 0.013092788564404878 + j0.12467728665140516 pu
[exit 0]
```

(The full capture is `three-phase-bus5.txt`.) Recompute from the printed `Zth`:

```
|Zk|    = sqrt(0.013092788564404878^2 + 0.12467728665140516^2) = 0.125362861004162 pu
c       = 1.10                                  (Un = 345 kV, above 1 kV, maximum objective)
Ik''    = 1.10 / 0.125362861004162              = 8.77452852614364 pu      (printed 8.774528526143639)
Ibase   = 100 / (sqrt(3) * 345)                 = 0.167347904112935 kA
Ik''    = 8.77452852614364 * 0.167347904112935  = 1.4683989584293 kA       (printed 1.4683989584292985)
R/X     = 0.013092788564404878 / 0.12467728665140516 = 0.10501342238071    (printed 0.10501342238071008)
kappa   = 1.02 + 0.98 * exp(-3 * 0.10501342238071)   = 1.73516429858141
kappa_b = min(1.15 * 1.73516429858141, 2.0)          = 1.99543894336862    (printed 1.995438943368623)
ip      = 1.99543894336862 * sqrt(2) * 1.4683989584293 = 4.14378781820611 kA (printed 4.143787818206106)
m       = (exp(4 f Tk ln(kappa-1)) - 1) / (2 f Tk ln(kappa-1)) = 0.0270860656372157
                                                (series kappa, f = 60 Hz, Tk = 1 s; printed 0.027086065637215752)
Ith     = 1.4683989584293 * sqrt(1 + 0.0270860656372157) = 1.48815266487889 kA   (printed 1.4881526648788939)
idc(tb) = sqrt(2) * 1.4683989584293 * exp(-2*pi*60*0.1 * 0.10501342238071) = 0.0396300939240743 kA
                                                (printed 0.03963009392407432)
```

The hand figures agree with the printed ones to the last digit shown; the printed digits differ at the 15th to 16th
significant figure because the hand figures are rounded. An independent complex solve of the nine-bus positive network
(series impedances only, three machine shunts) agrees with the captured
`Zkk = 0.013092788564404878 + j0.12467728665140516` at bus 5 to 3e-17; the last digit depends on the elimination
order.

Bus 5 is far from all three machines (each machine's `Ik''_G/IrG` is at most 2), so every `mu` is 1 and `Ib = Ik = Ik''`.

### Near-generator breaking current at bus 1

```
$ gridsim iec60909 case9 --bus 1
IEC 60909 - IEEE 9-bus (WSCC), bus 1, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 2.934393338019216 kA  (17.534688310401165 pu)
  ip    = 8.299717711928068 kA  (kappa 2, R/X 0.019434794652556552, meshed network: 1.15 x kappa(R/X), Method B)
  Ib    = 2.3509284626333975 kA    Ik = 2.934393338019216 kA  (near a generator, fed from several sources: IEC 60909-0 Ik = Ik''M, the initial current without motors; no motors are modelled)
  Ith   = 3.1412149644062284 kA  (m 0.1459315044848038)    idc(tb) = 1.9945128315290945 kA
  Zth   = 0.0012189687934890236 + j0.06272095050557554 pu
[exit 0]
```

`Ib` is below `Ik''`. The multiple-fed formula, recomputed from the `Zbus` column of bus 1 at `tb = 0.10 s`:

| Machine | `\|V_g\|/c` | `I_g` (kA) | `r_g = \|I_g\|/IrG` | `mu_g` | `(\|V_g\|/c)(1-mu_g) I_g` (kA) |
|---|---|---|---|---|---|
| 1 (bus 1, at the fault) | 1 | 2.2780233447373277 | 5.5 | 0.743872301952596 | 0.58346487538581882 |
| 2 (bus 2) | 0.20538641019773948 | 0.36295760455544029 | 1.1296252560875673 | 1 | 0 |
| 3 (bus 3) | 0.25068149786064969 | 0.29533520379877126 | 1.3787482382335732 | 1 | 0 |

Machine 1 has `r = c / x''d = 1.10 / 0.20 = 5.5` and `mu = 0.62 + 0.72 * exp(-0.32 * 5.5) = 0.743872301952596`.
Machines 2 and 3 are far (`r <= 2`), so they take `mu = 1`. Then
`Ib = 2.934393338019216 - 0.58346487538581882 = 2.3509284626333975 kA`, the printed value to the last digit.

### Breaker check

```
$ gridsim iec60909 case9 --bus 5 --breaker 345,6,2,2
  ...
  breaker 345 kV / making 6 kA / breaking 2 kA / 2 kA for 1 s: NOT ADEQUATE
    voltage TOO LOW (system 345 kV, highest 362 kV)   making margin 1.856212181793894 kA   breaking margin 0.5316010415707015 kA   thermal margin 0.5118473351211061 kA
[exit 3]
```

The breaker is `NOT ADEQUATE` and the run exits 3, although all three current margins are positive:
`6 - 4.143787818206106 = 1.856212181793894`, `2 - 1.4683989584292985 = 0.5316010415707015` and
`2 * sqrt(1/1) - 1.4881526648788939 = 0.5118473351211061`. The voltage check fails: a 345 kV rating is below the
highest system voltage `Um` of the 345 kV bus, 362 kV, so the line reads `voltage TOO LOW` and the breaker fails
whatever its currents. At bus 1 the same rating also fails all three current checks (`making margin
-2.299717711928068 kA`, `breaking margin -0.3509284626333975 kA`, `thermal margin -1.1412149644062284 kA`;
`breaker-inadequate.txt`).

Rated for `Um`, the same currents pass: with 362 kV the voltage line reads `voltage ok`, the three margins are the ones
above, and the breaker is `ADEQUATE` with exit 0 (`breaker-adequate-um.txt`):

```
$ gridsim iec60909 case9 --bus 5 --breaker 362,6,2,2
  ...
  breaker 362 kV / making 6 kA / breaking 2 kA / 2 kA for 1 s: ADEQUATE
    voltage ok (system 345 kV, highest 362 kV)   making margin 1.856212181793894 kA   breaking margin 0.5316010415707015 kA   thermal margin 0.5118473351211061 kA
[exit 0]
```

### Converter infeed (the `uk` case)

```
$ gridsim iec60909 uk --bus 3
IEC 60909 - GB reduced (10-zone), bus 3, three-phase, Maximum (50 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 230.24585058250418 kA  (15.951900457632377 pu), of which converters 33.26107795539915 kA
  ip    = 604.195341604139 kA  (kappa 2, R/X 0.029872767022212653, meshed network: 1.15 x kappa(R/X), Method B)
  Ib    = 206.14092477227376 kA    Ik = 230.24585058250418 kA  (near a generator, fed from several sources: IEC 60909-0 Ik = Ik''M, the initial current without motors; no motors are modelled)
  Ith   = 239.6568418856552 kA  (m 0.11396670562621664)    idc(tb) = 108.98599257963483 kA
  Zth   = 0.002406695638187732 + j0.0805648715566982 pu
[exit 0]
```

The synchronous share is `Ik''_sync = 230.24585058250418 - 33.26107795539915 = 196.984772627105 kA`, which is
`1.10/|Zk| = 13.647505380302 pu` at `10000 / (sqrt(3) * 400) = 14.4337567297406 kA` per pu. The peak adds the two
shares without `kappa` on the converters, with `kappa_b = min(1.15 * 1.91599449644715, 2.0) = 2` on the synchronous
share:

```
ip  = kappa_b * sqrt(2) * 196.984772627105 + sqrt(2) * 33.26107795539915
    = 557.157074060465 + 47.0382675436743 = 604.195341604139 kA          (printed 604.195341604139)
Ith = sqrt(m * 196.984772627105^2 + 230.24585058250418^2) = 239.656841885655 kA   (printed 239.6568418856552)
```

### Impedance corrections

```
$ gridsim iec60909 case9 --bus 5 --corrections --rated-pf 0.85
IEC 60909-0 impedance corrections (rated power factor 0.85): 3 applied
  K_G: 3 element(s), 0.9952 to 0.9952
IEC 60909 - IEEE 9-bus (WSCC), bus 5, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 1.4704922481380072 kA  (8.787037136393673 pu)
  ...
[exit 0]
```

Each machine takes `K_G = 1.10 / (1 + 0.20 * sin(acos 0.85)) = 0.995154018321994`, which multiplies `X''d` and `X2`.
`Ik''` at bus 5 moves from `1.4683989584292985` to `1.4704922481380072 kA`. `case9` has no branch flagged as a transformer,
so only `K_G` applies.

### Unbalanced and refused cases

`gridsim iec60909 case9 --bus 5 --type ll --objective min` gives `Ik'' = 1.1560643644458017 kA`, `c` = 1.00 and the
`R/X` of `Z1 + Z2` (here equal to that of `Z1`, so `kappa` is unchanged). An earth fault on `case9` is refused:

```
$ gridsim iec60909 case9 --bus 5 --type slg
IEC 60909 - IEEE 9-bus (WSCC), bus 5, single-line-to-earth, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 0 kA  (0 pu)
  ip    = 0 kA  (kappa 1.995438943368623, R/X 0.10501342238071008, meshed network: 1.15 x kappa(R/X), Method B)
  Ib    = 0 kA    Ik = 0 kA  (unbalanced fault: IEC 60909-0 takes no decay, Ik = Ib = Ik'')
  Ith   = 0 kA  (m 0.027086065637215752)    idc(tb) = 0 kA
  Zth   = 0.013092788564404878 + j0.12467728665140516 pu
iec60909: the model carries no transformer winding connections at all (MATPOWER and JSON v1 cannot hold them), so it has no earthed neutral and the earth-fault duty above reflects missing data. Exit 3.
[exit 3]
```

Other captures in the directory: `all-buses.txt` (the nine-bus sweep), `branches.txt` (the branch currents at bus 5),
`line-to-line-min-bus5.txt`, `unknown-bus.txt` (exit 2), `usage.txt` (exit 2) and `gb-full-bramford.txt` (a fault on
gb-full).

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Protection/ShortCircuit/Iec60909.cs` | `Iec60909` (static) | Voltage factor, `PeakFactor`, `ThermalDcFactor`, `BreakingFactorMu`, `MotorBreakingFactorQ`, `FromThevenin`, `FromSequenceImpedances`, `ThreePhase`, `LineToLine`, `SingleLineToGround`, `DoubleLineToGround`, `PartialThreePhase`, `BranchFaultCurrents`, the three sequence Thevenin functions, `CheckBreaker`, the four correction factors, `ApplyCorrectionFactors` |
| same | `Iec60909.Duty`, `BreakerRating`, `BreakerAdequacy`, `FaultContribution`, `MachinePartialCurrent`, `BranchFaultCurrent`, `CorrectionReport` | Result records |
| same | `ShortCircuitObjective`, `UnbalancedFaultType` | The objective and the unbalanced fault choice |
| same | `PositiveNetwork` (private) | The factored positive network: machines, converter infeeds, `Zbus` column, converter voltages |
| `src/GridSim.Core/Protection/FaultNetworkSystem.cs` | `FaultNetworkSystem` | Factor once on the sparse LU at every size (dense LU as a test oracle only), transposed solve, series-only network, singular checks |
| `src/GridSim.Core/Model/Model.cs` | `Generator`, `Branch`, `WindingConnection`, `ConverterPlant` | Machine reactances, converter fields, winding and neutral data, converter classification |
| `src/GridSim.Core/PowerFlow/AdmittanceStamp.cs` | `AdmittanceStamp` | The series-plus-tap branch stamp shared with `BranchFaultCurrents` |
| `src/GridSim.Cli/Program.cs` | `RunIec60909`, `RunIec60909Sweep` | The verb, its flag checks, output lines, result file and exit codes |
| `tests/GridSim.Tests/Iec60909Tests.cs`, `Iec60909PartialAgreementTests.cs`, `ConverterFaultContributionTests.cs`, `EarthFaultAndIec60909CompletionTests.cs`, `ProtectionWs6Tests.cs`, `FaultNetworkSystemTests.cs` | Tests | The invariants above |

## Limits

- Method B takes one `R/X`, that of the driving-point impedance, for the whole network and adds the 1.15 safety factor when the network is meshed; it does not sum partial peaks source by source. The Method C DC ratios are marked `[VERIFY]` (no public source reproduces them).
- The steady-state current is `Ik = Ik''` and the thermal factor `n` is 1 for every fault, because `lambda` and `n` come from machine graphs that have no closed form here.
- `Ib` for unbalanced faults equals `Ik''`, so a near-generator unbalanced fault is not decayed.
- No asynchronous-motor contribution exists: the model carries no motor data and `MotorBreakingFactorQ` feeds no duty.
- `DoubleLineToGround` reports the earth current `|3 I0|` only, not the line-conductor currents.
- `K_T` covers two-winding network transformers only, `K_S` and `K_SO` are marked `[VERIFY]` in the source, and the generator rated power factor is the caller's input.
- The correction factors are offered for the maximum-current case only, and whether the standard also corrects the zero-sequence impedances is marked `[VERIFY]`.
- IEC 60909-0 is not in `docs/reference`, so its clause numbers (9.1.7, 9.2, 8.1.2, 11.2.7, read from the 2016 contents list) and the `K_S`/`K_SO` equation forms are unchecked against the standard's text. IEC 60909-0:2026 Edition 3.0 exists; its numbering is unchecked.
- There is no agreement test against a published IEC 60909 worked example; the formulas are pinned to closed forms, hand networks and the WSCC-9 case.
- Of the case file formats the CLI reads, only a JSON v2 case can carry transformer winding connections, `X''d` or `X2` (N234, above); every other file case has no earth paths for an earth fault and runs on the 0.20 pu default reactance.
- Currents in kA are referred to the fault bus voltage, including the rated currents of machines at other voltage levels.

## See also

- [ch. 17, Protection assessment](<0017 Protection assessment (a passive relay overlay).md>) - the operating-point fault study and the relay verdicts built on it.
- [ch. 26, Overcurrent coordination](<0026 Overcurrent coordination (IDMT grading).md>) - grading a relay chain against the branch currents this study produces.
- [ch. 15, Protections](<0015 Protections.md>) - the protection stages of assets.
- [ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>) - the model fields and the Y-bus stamp.
- [ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) - the sparse factorisations.
- [ch. 11, Validation](<0011 Validation.md>) - the machine-epsilon standard.
- [the `iec60909` section of the CLI reference](<../manual/0019 CLI reference.md#iec60909>).
