# 24. Standard dynamic-model library

This chapter specifies the dynamic-model library under `src/GridSim.Core/Dynamics/Models/` and the study built on
it: the block primitives, the synchronous-machine models (classical, one-axis, two-axis and the six-state
subtransient machine with magnetic saturation), the exciters (SEXS, IEEET1), the stabiliser (PSS1A), the
turbine-governors (TGOV1, IEEEG1 and a hydro governor), the converter-plant models the RMS engine carries beside
the machines, the initialisation of a fleet from a solved power flow (`SubtransientInit`), and the RMS
transient-stability integrator (`MultiMachineSubtransient`, `RmsTransientStability`) with its critical-clearing-time
search. It gives the equations as coded, every parameter with its default, unit and source symbol, the invariants
and the tests that pin them, and one machine initialised by hand against a captured run. The library also holds the
classical machine GENCLS, GENTPJ, the ESST1A, EXST1, SCRX and ESDC1A exciters and the IEEEST, PSS2A and PSS2B stabilisers
([Extended library](#extended-library-gencls-gentpj-st1-scrx-dc1a-ieeest-pss2a-pss2b)), the GGOV1 and WSIEG1 governors
([Turbine-governors](#turbine-governors)) and three dynamic load models: a third-order induction motor, a ZIP load with
exponential recovery and a subset of the WECC composite load CMLD ([Dynamic loads](#dynamic-loads)).

The library follows one design rule. Every dynamic element is a wiring of five blocks (`FirstOrderLag`, `LeadLag`,
`Washout`, `Integrator`, `PiController`) that advance by the exact zero-order-hold solution of their own pole. A
standard model adds routing and limits, never numerics of its own, so the block tests validate the arithmetic once
and the model tests validate the wiring. Machines add one more rule: over a step the flux inputs (`Id`, `Iq`, `Efd`) are evaluated at the start-of-step state
and held, the flux states advance on the block exact solution, and the rotor swing advances by RK4 with the EMFs
frozen, re-solving `Pe` at each RK4 stage (operator splitting).

The classical machine's swing, its Kron reduction and the small-signal linearisation of it are specified in
[ch. 30, Small-signal and impedance stability](<0030 Small-signal and impedance stability.md>); this chapter
repeats only the classical equations the library reuses. The task view of the same verbs is
[ch. 15, RMS, modal and impedance stability](<../manual/0015 RMS, modal and impedance stability.md>).

Quantities are per-unit. Machine reactances and inertia are converted to the system base `GridModel.BaseMva`
(ch. 2); a quantity "on the machine base" is converted by `BaseMva / MBase`. In the subtransient fleet (`SubtransientInit`)
`MBase <= 0` means `BaseMva`. The classical path (`ClassicalInit`) uses the same rule (`MachineBase.EffectiveMva`: a
non-positive `MBase` means the system base, and a NaN or infinite one is refused).
Angles are radians, speed deviation `omega` is per-unit of synchronous speed, `ws = 2 pi f0`.

## Model

### Block library

`src/GridSim.Core/Dynamics/Models/ControlBlocks.cs`. The three linear dynamic blocks advance by the closed-form
solution of `T dx/dt = K u - x` for an input held across the step:

```
a = exp(-dt / T)
x <- a*x + (1 - a)*K*u
```

This is exact for a held input and A-stable at every step size. Every block is a pure function of
`(state, input, dt)`: no static state, no threads, no clock, so a run is bit-identical to the next run.

| Block | Transfer function | Realisation as coded |
|---|---|---|
| `FirstOrderLag(K, T, min, max)` | `K / (1 + sT)` | `x <- clamp(a*x + (1-a)*K*u, min, max)`; `T <= 1e-12` (`ControlBlocks.InstantThresholdSeconds`) gives `x <- clamp(K*u)`; `max < min` throws |
| `LeadLag(Ta, Tb)` | `(1 + sTa)/(1 + sTb)` | `c = Ta/Tb`; `y = c*u + xL`; `xL <- a*xL + (1-a)*(1-c)*u` with `a = exp(-dt/Tb)`; `Ta < 0` or `Tb <= 0` throws |
| `Washout(T)` | `sT/(1 + sT)` | `xL <- a*xL + (1-a)*u`; `y = u - xL`; `T <= 0` throws |
| `Integrator(K, min, max)` | `K/s` | `x <- clamp(x + K*u*dt, min, max)` (exact for a held input) |
| `PiController(Kp, Ki, min, max)` | `Kp + Ki/s` | `y = clamp(Kp*u + I(u), min, max)`; `I` is an `Integrator` with the same limits |

Initialisation puts a block at its equilibrium: `FirstOrderLag.Initialize(y)` sets `x = clamp(y)`;
`LeadLag.Initialize(u)` sets `xL = (1 - c)*u`, so the output is `u` exactly; `Washout.Initialize(u)` sets
`xL = u`, so the output is zero; `PiController.Initialize(u, y)` sets the integral to `y - Kp*u`.

Limits clamp the state itself, not only the reported output. A lag or integrator at its limit therefore holds no
wound-up state and leaves the limit on the first step in which its input reverses (non-windup).

Two reductions are exact by construction. A `LeadLag` with `Ta == Tb` has `c = 1` and internal gain `1 - c = 0`, so
`y = u` bit for bit. A `FirstOrderLag` with `T = 0` is the gain `y = K*u` bit for bit.

### Rotor equations shared by every machine

```
ddelta/dt = ws * omega                                   (rotor angle)
domega/dt = (Pm - Pe - D*omega) / (2*H)                  (swing; H and Pe on one base, D in pu power per pu speed)
```

`H` is the inertia constant in seconds on the machine base; the multi-machine engine converts it to the system
base as `H_sys = H * MBase / BaseMva` (`SubtransientInit.FromSolvedCase`). `D` is used as given, in system-base pu,
and is not rescaled.

### Classical machine

`ClassicalMultiMachine` (`src/GridSim.Core/Dynamics/Distributed/ClassicalMultiMachine.cs`) is a constant internal
EMF `E' angle delta` behind `X'd`, with the machines coupled through the Kron-reduced admittance `Y = G + jB` of
the augmented network (internal nodes behind `X'd`, loads as constant admittance `conj(S)/|V|^2`):

```
Pe_i = E_i^2*G_ii + E_i * sum_{j != i} E_j * (G_ij*cos(d_i - d_j) + B_ij*sin(d_i - d_j))
```

The swing advances by RK4. Two optional hooks make it the host of the control models: `mechanicalPower(i, omega_i)`
replaces `Pm` once per step and is held across the RK4 stages, and `internalVoltage(i, E_i, Id_i)` replaces `E_i`
for the next step. With neither hook the integration is the constant-`Pm`, constant-`E'` swing byte for byte.
`ClassicalInit.FromSolvedCase` seeds it: `E' = Vt + j*X'd*It` with `It = conj(S_gen/Vt)` from the solved power flow,
`delta0 = arg(E')`, and `Pm` set to the computed `Pe` at `delta0`, so an undisturbed run is a fixed point. `X'd`
comes on the machine base and is converted to the system base by `BaseMva / MBase`; it defaults to `0.30` pu when
the caller supplies none.

### One-axis machine (flux decay), SMIB

`OneAxisMachine` is a machine whose transient EMF `E'q` is a state, on a single-machine infinite-bus network
(`Vinf` through the external reactance `Xe`). Three states:

```
ddelta/dt    = ws * omega
domega/dt    = (Pm - Pe - D*omega) / (2*H)
T'do dE'q/dt = Efd - E'q - (Xd - X'd)*Id
```

The stator is algebraic with `Rs = 0`: `Vd = Xq*Iq`, `Vq = E'q - X'd*Id`. Against the infinite bus the currents have
a closed form, so there is no network iteration:

```
Id = (E'q - Vinf*cos(delta)) / (X'd + Xe)
Iq =        Vinf*sin(delta)  / (Xq  + Xe)
Pe = E'q*Iq + (Xq - X'd)*Id*Iq                      (the second term is reluctance power)
Vt = sqrt(Vd^2 + Vq^2)
```

Over a step `Id` and `Efd` are held, so the flux equation is a first-order lag with input
`u = Efd - (Xd - X'd)*Id`; `E'q` advances on `FirstOrderLag(1, T'do)`. Rotor: RK4 with `E'q` frozen. An optional
`Sexs` closes the AVR on `Vt`: `Efd = Sexs.Step(Vref - Vt)` at the start of each step.
`Equilibrium(p, delta0, E'q0)` returns `Efd = E'q0 + (Xd - X'd)*Id` and `Pm = Pe`.

### Two-axis machine, SMIB

`TwoAxisMachine` adds the q-axis transient EMF `E'd` as a fourth state and puts `X'q` in the stator:

```
T'qo dE'd/dt = -E'd + (Xq - X'q)*Iq
Vd = E'd + X'q*Iq          Vq = E'q - X'd*Id
Id = (E'q - Vinf*cos(delta)) / (X'd + Xe)
Iq = (Vinf*sin(delta) - E'd) / (X'q + Xe)
Pe = E'd*Id + E'q*Iq + (X'q - X'd)*Id*Iq
```

The d-axis equation is the one-axis equation. The q-axis fixed point is
`E'd = (Xq - X'q)*Vinf*sin(delta)/(Xq + Xe)` (`TwoAxisMachine.EquilibriumEdp`).

### Subtransient machine (GENROU-style), SMIB

`SubtransientMachine` is the six-state machine: rotor `delta`, `omega`, and four flux EMFs, the two subtransient
ones cascaded off the transient ones:

```
T'do  dE'q/dt  = Efd - E'q - (Xd - X'd)*Id - E''q*Se(|E''|)
T''do dE''q/dt = E'q - E''q - (X'd - X''d)*Id
T'qo  dE'd/dt  = -E'd + (Xq - X'q)*Iq - E''d*Se(|E''|)*(Xq - Xl)/(Xd - Xl)
T''qo dE''d/dt = E'd - E''d + (X'q - X''q)*Iq
|E''| = sqrt(E''q^2 + E''d^2);  Xl = stator leakage reactance (0 when not given)
```

The stator sees the subtransient EMFs behind the subtransient reactances (`Rs = 0`):

```
Vd = E''d + X''q*Iq            Vq = E''q - X''d*Id
Id = (E''q - Vinf*cos(delta)) / (X''d + Xe)
Iq = (Vinf*sin(delta) - E''d) / (X''q + Xe)
Pe = E''d*Id + E''q*Iq + (X''q - X''d)*Id*Iq
```

Each flux state advances on `FirstOrderLag(1, T)` with the input frozen at the start-of-step state (currents,
`Efd`, and the transient EMF that drives the subtransient one). The rotor advances by RK4 with `E''q`, `E''d` frozen.

**Saturation.** `SaturationCurve(S1, S12)` is the scaled-quadratic open-circuit form fitted in closed form to the two
tabulated points `Se(1.0) = S1` and `Se(1.2) = S12` (`LowerFitPointPu = 1.0`, `UpperFitPointPu = 1.2`):

```
k  = sqrt(1.2 * S12 / S1)
A  = (k - 1.2) / (k - 1)
B  = S1 / (1 - A)^2
Se(x) = B * (x - A)^2 / x        for x > A, else 0
FieldIncrement(x) = x * Se(x) = B * (x - A)^2
```

`Se` is the dimensionless PSS/E saturation factor (`Ifd = x*(1 + Se(x))` in air-gap-line per-unit). The machine applies
it in the PSS/E GENROU form (`SaturationCurve.GenrouIncrements`): `Se` is evaluated on the subtransient flux magnitude
`|E''|`, the d-axis increment is `E''q * Se(|E''|)` and the q-axis increment is `E''d * Se(|E''|) * (Xq - Xl)/(Xd - Xl)`,
both frozen at the start-of-step subtransient EMFs. `Xl` must be finite, at least 0 and below `X''d`. `S1 = 0` is the
unsaturated machine (`A = B = 0`, `Se = 0`), and any `S12` given with it is ignored. A negative `S1` or `S12` throws,
and `S1 > 0` requires `S12 > S1`. For `S1 = 0.10`,
`S12 = 0.30` the increment `x * Se(x)` is `0.10` pu at 1.0 pu flux and `0.36` pu at 1.2 pu. At equilibrium a saturated
machine needs exactly the d-axis increment more `Efd` than the unsaturated one:
`Efd = E'q + (Xd - X'd)*Id + E''q*Se(|E''|)`.

The closed-form SMIB fixed point (`SubtransientMachine.Equilibrium`) is
`Iq_sync = Vinf*sin(delta0)/(Xq + Xe)`, `E'd = (Xq - X'q)*Iq_sync`, `E''d = (Xq - X''q)*Iq_sync`, and `E''q`
solved from `E''q = E'q - (X'd - X''d)*Id` with `Id = (E''q - Vinf*cos(delta0))/(X''d + Xe)`. When the machine is
saturated at that point (`Se(|E''|) > 0`) the q-axis fixed point moves: with `s = Se(|E''|) * (Xq - Xl)/(Xd - Xl)`,
`E''d = (Xq - X''q)*Vinf*sin(delta0) / ((1 + s)(X''q + Xe) + Xq - X''q)`, solved by fixed-point iteration from the
unsaturated value (at most `SaturatedEquilibriumMaxIterations` = 200 iterations, settled at a relative move of
`1e-15`; otherwise `InvalidOperationException`), then `E'd = E''d - (X'q - X''q)*Iq`. Below the knee the closed form
above is kept, bit for bit.

`SubtransientMachine.Simulate` takes an optional `Sexs` exciter with an explicit `Vref`.

### Multi-machine network interface

`SubtransientNetworkInterface` carries the dq machine into a coupled network. Each machine's subtransient EMF is
rotated from its rotor frame into the network frame by the load angle:

```
Er_i = E''q_i*cos(d_i) + E''d_i*sin(d_i)
Ei_i = E''q_i*sin(d_i) - E''d_i*cos(d_i)
I    = Y_red * E                                       (one complex matrix-vector product)
Pe_i = Er_i*Ir_i + Ei_i*Ii_i                           (Re(E conj(I)))
Iq_i = Ir_i*cos(d_i) + Ii_i*sin(d_i)                   (inverse rotation to the machine frame)
Id_i = Ir_i*sin(d_i) - Ii_i*cos(d_i)
Vt_i = E_i - j*X''_i*I_i                               (terminal voltage, for the AVR)
```

The reduced admittance holds the round subtransient reactance `X''` of each machine (`X''d = X''q`), so each machine
is a voltage behind one reactance at the interface while its flux dynamics stay fully salient. With `E''d = 0` the
interface is exactly the round-rotor injection of `MultiMachineFluxDecay.NetworkInjection`
(`Pe_i = E_i * sum_j E_j*(G_ij*cos(d_ij) + B_ij*sin(d_ij))`, `Id_i = Qe_i/E_i`).

`MultiMachineFluxDecay` is the round-rotor multi-machine model that uses this closed form: every machine carries
`E'q` with `Xq = X'd`, the flux advances on the same exact lag with input `Efd_i - (Xd_i - X'd_i)*Id_i`, and the
swing is `ClassicalMultiMachine` with the `internalVoltage` hook.

### Exciters

**SEXS** (`Sexs`): the simplified excitation system.

```
Verr = Vref - Vt + Vs
v1   = LeadLag_{Ta/Tb}(Verr)
Efd  = FirstOrderLag_{K, Te, [Emin, Emax]}(v1)
```

The steady field is `clamp(K*Verr, Emin, Emax)`. `Initialize(Efd0)` sets `Verr = Efd0/K`. `K <= 0` throws.

**IEEET1** (`Ieeet1`): the IEEE Type 1 DC exciter, with a regulator lag, a self-excited exciter with exponential
saturation, and rate feedback.

```
Vf  = (Kf/Tf) * Washout_Tf(Efd)                       sKf/(1 + sTf) of the field voltage
Vr  = FirstOrderLag_{Ka, Ta, [Vrmin, Vrmax]}(Verr - Vf)
Se  = Aex * exp(Bex * Efd)
Te dEfd/dt = Vr - (Ke + Se)*Efd                        Se frozen at the start-of-step Efd
```

The exciter ODE advances by the exact hold of its linearised pole: with `s = Ke + Se`,
`a = exp(-dt*s/Te)` and `Efd <- a*Efd + (1 - a)*Vr/s`; when `|s| <= 1e-12`
(`Ieeet1.ExciterPoleAtOriginTolerance`) the pole is at the origin and `Efd <- Efd + Vr/Te*dt`. The feedback
washout then advances on the new `Efd`. `Initialize(Efd0)` sets `Vr = (Ke + Se(Efd0))*Efd0` and the washout
output to zero, so the steady error is `Verr = (Ke + Se)*Efd/Ka`. `Ka <= 0`, `Te <= 0`, `Tf <= 0` or `Aex < 0` throws.
With `Se = 0`, `Ke = 1`, `Kf = 0` it is a two-lag AVR whose steady field is `Ka*Verr`.

**EXAC1 and ESAC1A** (`AcExciter`): the alternator-rectifier exciters of the IEEE AC1 family (EXAC1 the 1981 type
AC1, ESAC1A the 421.5-2005 type AC1A). They read the machine's field current `IFD` (non-reciprocal Xad base).

```
VFE = VE*(KE + SE(VE)) + KD*IFD           VE*SE(VE) = B*(VE - A)^2 above A (quadratic through (E1, SE1), (E2, SE2))
VF  = (KF/TF) * Washout_TF(VFE)
x   = LeadLag_{TC/TB}(Verr - VF)          (TB = TC = 0 bypasses it)
EXAC1:  VR = FirstOrderLag_{KA, TA, [VRMIN, VRMAX]}(x)
ESAC1A: VA = FirstOrderLag_{KA, TA, [VAMIN, VAMAX]}(x),  VR = clamp(VA, VRMIN, VRMAX)
TE dVE/dt = VR - VFE                      SE frozen at the start-of-step VE; ESAC1A: VE >= 0
Efd = VE * FEX(KC*IFD/VE)                 FEX the IEEE 421.5 rectifier regulation function
```

`Initialize(Efd0)` takes `IFD = Efd0` (true at every machine equilibrium), inverts `Efd0 = VE*FEX(KC*Efd0/VE)` branch
by branch for `VE`, and sets `VR = VFE` and the washout output to zero; `VR` (and `VA`) outside its limits throws. The
steady error is `VFE/KA`. The RMS kernel supplies `IFD = E'q + (Xd - X'd)*Id + the d-axis saturation increment` at
the start of each step to a controller that implements `IRmsFieldCurrentController` with `NeedsFieldCurrent` true; every
other controller takes the old path unchanged.
**ESST4B** (`Esst4b`): the IEEE 421.5-2005 type ST4B static exciter, fed from a potential and/or compound source,
with an outer voltage PI regulator and an inner field-voltage PI regulator.

```
VE   = | KP e^(j THETAP) VT + j (KI + KP e^(j THETAP) XL) IT |     (machine base)
VB   = min(VE * FEX(KC*IFD/VE), VBMAX)
VR'  = PI_{KPR, KIR, [VRMIN, VRMAX]}(Verr)      VR = lag_{TA}(VR')
VM   = PI_{KPM, KIM, [VMMIN, VMMAX]}(VR - KG*Efd)     (the loop through Efd = VM*VB solved exactly each step)
Efd  = VM * VB
```

It needs the terminal phasors, so it implements `IRmsTerminalFieldController`: the kernel passes `V_t = E'' - jX'' I` and
the machine current on the system base, which `RmsExciter` converts to the machine base bound by the study
(`BindMachineBase`, MBase / system base). A unit with a unit transformer folded into its reactances is refused, because
its terminal voltage is then not available. `Initialize(Efd0)` records the field and the first step sets every state
from that step's measurements (`VM0 = Efd0/VB0`, `VR0 = KG*Efd0` with KIM above 0); states outside their limits throw.
KIR above 0 gives a steady error of 0; KIR = 0 needs KIM above 0 (error `KG*Efd0/KPR`); KIR = KIM = 0 is refused. The
PSS/E data carry no VGMAX, so none is applied.
**RmsExciter** (`src/GridSim.Core/Dynamics/Models/RmsTransientStability.cs`) is the `IRmsFieldController` the RMS
engine drives. It wraps one `Sexs`, one `Ieeet1`, one `AcExciter` or one `Esst4b`, built by `RmsExciter.FromSexs`, `RmsExciter.FromIeeet1`, `RmsExciter.FromAc` or `RmsExciter.FromEsst4b`, and an
optional `Pss1a`:

```
Vs   = Pss1a.Step(omega)           (omega = this machine's speed deviation; 0 without a PSS)
Efd  = AVR.Step(Vref - Vt + Vs)
```

`Initialize(Efd0)` refuses an `Efd0` the limits cannot hold: SEXS needs `Efd0` in `[Emin, Emax]`, IEEET1 needs
`Vr = (Ke + Se)*Efd0` in `[Vrmin, Vrmax]`; either throws `InvalidOperationException` rather than clamping silently. A non-finite `Efd0` throws
`ArgumentOutOfRangeException`.
`FromSexs` throws when `Emax < Emin`, `FromIeeet1` when `Vrmax < Vrmin`; the `Sexs` and `Ieeet1` constructors throw
`ArgumentException` for the same inverted limits. `EquilibriumErrorPu(Efd0)` returns the steady
regulator error `Efd0/K` (SEXS) or `(Ke + Se)*Efd0/Ka` (IEEET1).

### Power-system stabiliser PSS1A

`Pss1a` is a subset of the IEEE 421.5 PSS1A:

```
g  = Ks * u                                  u = speed deviation (pu)
w  = Washout_Tw(g)
v1 = LeadLag_{T1/T2}(w)
v2 = LeadLag_{T3/T4}(v1)
Vs = clamp(v2, Vsmin, Vsmax)
```

A stage with `T1 = T2 = 0` (or `T3 = T4 = 0`) is bypassed and is exactly unity. A stage with a zero lag and a non-zero
lead is an improper pure derivative and throws, naming the parameter. `Vsmax < Vsmin` throws. A PSS adds nothing in
steady state, because the washout blocks any sustained input: `Initialize` sets `Vs = 0`.

### Extended library: GENCLS, GENTPJ, ST1, SCRX, DC1A, IEEEST, PSS2A, PSS2B

The library also holds the classical machine GENCLS, GENTPJ, four more exciters and three more stabilisers. They are
wired from the same five blocks and follow the PSS/E model documentation block diagrams (IEEE Std 421.5-2005 for the
ST1A, DC1A, PSS2A and PSS2B structures). Each starts at rest at the solved operating point; a quantity that needs the
terminal voltage or the electrical power is set on the first step, from that step's measurements on the undisturbed
network.

**GENCLS** (`Gencls`). PSS/E data `H D`; `X'd` is the RAW generator record's source reactance `ZX` (ZSORCE, on MBASE),
which the RAW reader records as `PsseParseResult.MachineSourceReactances`. The machine is the six-state machine with every
reactance at `X'd` (`Xd = X'd = X''d = Xq = X'q = X''q`), so the network sees `E''` behind `X'd`, the rotor angle is the
angle of `Vt + jX'd It`, `Id` and `Iq` drop out of every flux equation and `E''d` starts at 0. Every open-circuit time
constant is `Gencls.HeldFluxTimeSeconds = 1e30` s, so each flux lag's `a = exp(-dt/T)` rounds to exactly 1 and the flux
states are held bit for bit: the constant-`E'` classical model on the RMS engine. It takes no exciter or stabiliser (it has
no field to drive); a governor still drives its mechanical power. PSS/E's `H = 0` infinite bus is not a GridSim machine and
is refused.

**GENTPJ** (a `gridsim-dynamics/2` machine template with `model` GENTPJ and `saturationKis`; the PSS/E GENTPJ record
order is not in a public source, so the DYR reader refuses the record by name). The machine is GENROU with the stator-current term in the saturation input
(`SaturationCurve.GentpjIncrements`, `MachineData.SaturationKis`):

```
s = Se(|psi''| + Kis |It|)          (GENROU: s = Se(|psi''|))
d-axis increment  E''q s,   q-axis increment  E''d s (Xq - Xl)/(Xd - Xl)
```

`Kis` converts to the system base like a reactance; `|It|` is the start-of-step machine current, held across the step
like every other flux input, and `SubtransientInit` seeds the field and the rotor angle with the same term, so a GENTPJ
fleet starts at rest. With `Kis = 0` the sum is `|psi''|` exactly and the machine is GENROU bit for bit. PSS/E's GENTPJ
also scales its reactances by the saturation factor (the GENTPF form); GridSim keeps GENROU's additive increments, so an
unsaturated GENTPJ is exact and a saturated one is GridSim's form, not PSS/E's. `Kis` may be negative (WECC data run from
-0.3 to 0.4).

**EXST1 and ESST1A** (`St1Exciter`), the potential-source controlled-rectifier exciters (PSS/E EXST1: `TR VIMAX VIMIN TC
TB KA TA VRMAX VRMIN KC KF TF`; ESST1A: ICONs `UEL VOS`, then `TR VIMAX VIMIN TC TB TC1 TB1 KA TA VAMAX VAMIN VRMAX VRMIN
KC KF TF KLR ILR`):

```
Vc   = lag_{TR}(Vt)                                       (TR = 0: Vc = Vt)
VF   = sKF/(1 + sTF) * Efd                                (start-of-step Efd)
u    = clamp(Vref - Vc + Vs[VOS 1] - VF, VIMIN, VIMAX)    (input limiter)
x    = leadlag_{TC/TB}(u), then leadlag_{TC1/TB1}         (EXST1: one stage)
VA   = lag_{KA, TA, [VAMIN, VAMAX]}(x)                    (non-windup; EXST1: no limit)
v    = VA + Vs[VOS 2] - max(0, KLR (IFD - ILR))           (field current limiter; EXST1 has none)
Efd  = clamp(v, Vt VRMIN, Vt VRMAX - KC IFD)              (the lower bound wins if the two cross)
```

At rest `VA0 = Efd0 + max(0, KLR (Efd0 - ILR))` and the regulator error is `VA0/KA`; `Initialize` refuses a `VA0`
outside `VAMIN..VAMAX` or an error outside `VIMIN..VIMAX`, and the first step refuses an `Efd0` outside the output limits
at the measured `Vt`. The ceiling moves with the terminal voltage, so these exciters read the terminal phasors and, like
ESST4B, are refused on a unit with a unit transformer folded into its reactances. The UEL input of ESST1A is not modelled
(GridSim has no UEL model to drive it).

**SCRX** (`Scrx`), data `TA/TB TB K TE EMIN EMAX CSWITCH rc/rfd`:

```
v1  = leadlag_{TA/TB}(Vref - Vt + Vs)
x   = lag_{K, TE, [EMIN, EMAX]}(v1)                       (non-windup, as SEXS)
Efd = x Vt (CSWITCH 0, bus fed)  or  x (CSWITCH 1, solid fed)
Efd = -rc/rfd IFD while IFD < 0, when rc/rfd > 0           (the crowbar; rc/rfd = 0: negative current capability)
```

Solid fed, the regulator error at rest is `Efd0/K`. Bus fed, `x0 = Efd0/Vt0` needs the terminal voltage, so the first step
sets the states and completes the voltage reference: the kernel's error is offset by `x0/K` less its first error, which
is the reference `Vref = Vt0 + x0/K`.

**ESDC1A** (`Esdc1a`), the IEEE DC1A dc commutator exciter, data `TR KA TA TB TC VRMAX VRMIN KE TE KF TF1 SWITCH E1
SE(E1) E2 SE(E2)`:

```
x    = leadlag_{TC/TB}(Vref - Vc + Vs - VF),   VF = sKF/(1 + sTF1) * Efd
VR   = lag_{KA, TA, [VRMIN, VRMAX]}(x)                    (non-windup)
TE dEfd/dt = VR - (KE + SE(Efd)) Efd,    SE(Efd) Efd = B (Efd - A)^2 above A
```

The quadratic saturation is fitted through the two points as for the AC exciters; the exciter advances by the exact hold
of its start-of-step linearised pole, as IEEET1 does. At rest `VR0 = (KE + SE(Efd0)) Efd0` and the error is `VR0/KA`.

**ESAC1A from DYR.** The existing `AcExciter` ESAC1A reads the PSS/E record `TR TB TC KA TA VAMAX VAMIN TE KF TF KC KD KE
E1 SE(E1) E2 SE(E2) VRMAX VRMIN`: `VAMAX`/`VAMIN` are the regulator lag's limits and `VRMAX`/`VRMIN` the output limits. A
non-zero `TR` is refused, as for EXAC1.

**IEEEST** (`Ieeest`), ICONs `ICS IB`, data `A1 A2 A3 A4 A5 A6 T1 T2 T3 T4 T5 T6 KS LSMAX LSMIN VCU VCL`, on rotor speed
deviation (`ICS 1`) or electrical power on the machine base (`ICS 3`):

```
f   = (1 + A5 s)/((1 + A1 s)(1 + A3 s)) u                 (A2 = A4 = A6 = 0)
v   = leadlag_{T1/T2}(leadlag_{T3/T4}(f))
Vs  = clamp(KS (T5/T6) Washout_{T6}(v), LSMIN, LSMAX)     (KS sT5/(1 + sT6))
Vs  = 0 while Vt > VCU (VCU > 0) or Vt < VCL (VCL > 0)
```

The filter is a lead-lag (`A5` over `A1`, or over `A3` when `A1 = 0`) and a lag, exact with the library blocks. The
second-order terms `A2`, `A4`, `A6` have no block here and must be 0.

**PSS2A and PSS2B** (`Pss2`), ICONs `ICS1 IB1 ICS2 IB2 M N`, data `TW1 TW2 T6 TW3 TW4 T7 KS2 KS3 T8 T9 KS1 T1 T2 T3 T4`,
then PSS2B's `T10 T11 VS1MAX VS1MIN VS2MAX VS2MIN`, then `VSTMAX VSTMIN`:

```
n1 = lag_{T6}(Washout_{TW2}(Washout_{TW1}(clamp(u1, VS1MIN, VS1MAX))))
n2 = lag_{KS2, T7}(Washout_{TW4}(Washout_{TW3}(clamp(u2, VS2MIN, VS2MAX))))
r  = [(1 + sT8)/(1 + sT9)^M]^N (n1 + KS3 n2)               (M = 0 or N = 0 bypasses it; M N <= 8)
Vs = clamp(leadlag_{T1/T2} leadlag_{T3/T4} [leadlag_{T10/T11}] (KS1 (r - n2)), VSTMIN, VSTMAX)
```

PSS2A has no input limits and no third stage. `TW2 = 0` or `TW4 = 0` bypasses that washout; the ramp-tracking filter is
`N` cascades of a lead-lag `T8/T9` and `M - 1` lags `T9`. Both inputs are speed (`1`) or electrical power (`3`).

**Wiring.** A library exciter is `RmsExciter.FromModel(IRmsExciterModel, PSS1A or library stabiliser)`; a library
stabiliser joins any exciter, the existing ones included, through `RmsExciter.WithLibraryStabiliser`, and its output is
added where the AVR's diagram adds it. Every such exciter reads the field current; one whose model or stabiliser reads the
terminal voltage or the electrical power (`Pe = Re(Vt conj(It))` on the machine base) takes the terminal phasors, so the
study binds its machine base. Without a library model or stabiliser every exciter takes exactly its previous path.

**DYR.** `PsseDyrOptions.ExtendedLibrary` (gridsim: `--dyr-extended`) maps these records, value by value in their PSS/E
order, into `gridsim-dynamics/2` templates (`model` on the machine template, the `st1`, `st1a`, `scrx`, `dc1a`, `ieeest`,
`pss2` and `pss2b` parameters on the exciter and stabiliser templates; `schemas/schemaNotes.md`). Without it the reader
maps exactly the models it mapped before and lists these as models it does not map. The record orders were checked
against the public copy of the PSS/E model library data sheets and against NREL PowerSystems.jl's
`psse_dynamic_mapping.yaml` or the PowerWorld transient-model pages (each model against two of them). Refused, naming the record: a
GENTPJ record (no public record order), GENCLS with `H = 0` or a ZX that is not above 0, an exciter or stabiliser on a GENCLS unit, ESST1A UEL
not 1 to 3 or VOS not 1 or 2, SCRX CSWITCH not 0 or 1, ESAC1A `TR` not 0, ESDC1A `KE = 0`, `VRMAX = 0` or `SWITCH` not 0, IEEEST with
`A2`, `A4` or `A6`, and any stabiliser input other than speed or electrical power, or on a remote bus.

### More controllers: EXDC2, IEEEX1, ESAC8B, ESST3A, PSS2C, IEEEG3

Five more exciter and governor models and one stabiliser. Each is a library model on the same blocks and the same
interfaces as the extended library above, and each starts at rest at the solved point.

**EXDC2 and IEEEX1** (`Exdc2`), the IEEE dc commutator exciters type DC2 (1981) and type 1 (1979). Both take ESDC1A's
data, `TR KA TA TB TC VRMAX VRMIN KE TE KF TF1 SWITCH E1 SE(E1) E2 SE(E2)`, and its diagram. The one difference is the
regulator limit: the DC2 regulator is fed from the machine terminals, so its limits are `Vt VRMAX` and `Vt VRMIN`.

```
VR   = lag_{KA, TA}(x), held in [Vt VRMIN, Vt VRMAX]       (EXDC2; non-windup)
VR   = lag_{KA, TA}(x), held in [VRMIN, VRMAX]             (IEEEX1)
```

IEEEX1 is ESDC1A step for step (a test runs the two side by side, bit for bit). EXDC2 reads the terminal voltage, so its
check that `VR0` lies inside `Vt VRMIN .. Vt VRMAX` runs on the first step. ANDES swaps the two: constant limits on its
EXDC2 and Vt-scaled limits on its IEEEX1. GridSim scales EXDC2 and keeps IEEEX1 constant, which matches the IEEE DC2
diagram (a bus-fed regulator), the HYPERSIM EXDC2 model built on the PSS/E 34.2 library, and OpenIPSL's PSS/E IEEEX1.

**ESAC8B** (`Esac8b`), the IEEE 421.5-2005 AC8B exciter with a PID regulator, data `TR KP KI KD TD KA TA VRMAX VRMIN TE KE
E1 SE(E1) E2 SE(E2)`:

```
u    = KP e + KI/s e + s KD/(1 + s TD) e                   (e = Vref - Vc + Vs)
VR   = lag_{KA, TA, [VRMIN, VRMAX]}(u)                     (non-windup)
TE dVE/dt = VR - (KE + SE(VE)) VE - KDexc IFD
Efd  = VE FEX(KC IFD / VE)
```

The PSS/E record has no `KC` and no exciter demagnetising factor, so a DYR record gives `KC = KDexc = 0` and `Efd = VE`. A
`gridsim-dynamics/2` file may give both (`ac8bKc`, `ac8bKdExc`); `FEX`, the saturation fit and the inverse used at the start
are `AcExciter`'s. The integral is the exact hold of `KI/s` (a ramp for a held error), the derivative `KD/TD` times the
exact-hold washout of `TD`. At rest the integral holds `VR0/KA` with zero error when `KI` is above 0; with `KI = 0` the
error is `VR0/(KA KP)`.

**ESST3A** (`Esst3a`), the IEEE 421.5-2005 ST3A compound-source static exciter, data `TR VIMAX VIMIN KM TC TB KA TA VRMAX
VRMIN KG KP KI VBMAX KC XL VGMAX THETAP TM VMMAX VMMIN`:

```
x    = leadlag_{TC/TB}(clamp(Vref - Vc + Vs, VIMIN, VIMAX))
VR   = lag_{KA, TA, [VRMIN, VRMAX]}(x)
VM   = lag_{KM, TM, [VMMIN, VMMAX]}(VR - min(KG Efd, VGMAX))
VE   = | KP e^(j THETAP) VT + j (KI + KP e^(j THETAP) XL) IT |
VB   = min(VE FEX(KC IFD / VE), VBMAX),    Efd = VB VM
```

The inner loop closes through `Efd = VB VM`. With `VB` held over the step it is a linear lag with pole
`(1 + KM KG VB)/TM`, stepped by its exact hold, so it is stable at any step; when `KG Efd` is at `VGMAX` at the start of a
step the plain lag is used. ESST3A reads the terminal voltage and current phasors, so the RMS exciter passes them (the
current on the machine base). `VR0` depends on `VB0`, which needs the terminal quantities, so the model fixes its steady
input on the first step and holds it as a constant offset on the error; this stands where PSS/E's `VREF` carries
`VR0/KA`, and a reference step still moves the error one for one. Because ESST3A (and EXDC2, for its limit check)
reads the first step's measurements as the steady state, a run with a disturbance in force at `t = 0` is refused for
them, with automatic or given voltage references; schedule the disturbance after `t = 0`.

The same refusal covers every model that sets a state, or checks its start, from the first step's measurements:
ESST4B (every state from VT, IT and IFD), bus-fed SCRX (`x0 = Efd0/Vt0`), every library exciter with a voltage
transducer (`TR` above 0: EXDC2, IEEEX1, ESDC1A, ESAC8B, ESST1A, EXST1; the lag starts at the first measured `Vt`), a
library stabiliser that sets a state from the first step's network measurement (IEEEST, PSS2A, PSS2B or PSS2C on
electrical power, whose washouts start at the first step's input, and PSS2C with its output logic, switched on or off
from the first step's power; each stabiliser says so through `ReadsNetworkInputsAtStart`), and GGOV1 reading the electrical power (`Rselect` 1 or a supervisory load controller; `Pref`,
`Pmwset` and the power transducer start at the first step's power). With automatic voltage references every field
controller was already refused; the governor check applies with either. A stabiliser on rotor speed alone reads a state
that no network change moves at `t = 0`, so it runs, also with the IEEEST voltage cut-off (`VCU` or `VCL` above 0):
the cut-off reads `Vt` on every step but holds no state. No CLI verb can start a run with a disturbance in force at
`t = 0` (the `stability` fault and events come at 0.1 s or after the first step, and `dynamics-study` refuses an onset
or event time of 0), so the refusal is reached through the API only. The check is exact (the network in force at the first step is the
base network or it is not), and a run with no disturbance at `t = 0` is unchanged bit for bit.

**PSS2C** (`Pss2c`), the IEEE 421.5-2016 stabiliser: PSS2B with a fourth lead-lag `T12/T13` and an output logic on the
electrical power. It is built from `Pss2` without changing it:

```
Vs = clamp(leadlag_{T12/T13}(PSS2B chain without its output clamp), VSTMIN, VSTMAX)   while on, else 0
on when Pe rises above PPSSon, off when Pe falls below PPSSoff (PPSSoff <= PPSSon)
```

With `T12 = T13 = 0` and no output logic, PSS2C gives PSS2B's output bit for bit.

**IEEEG3** (`Ieeeg3`), the IEEE 1973 hydro governor, data `TG TP Uo Uc PMAX PMIN sigma delta TR TW a11 a13 a21 a23`:

```
err  = P0 - dw - sigma g - delta sTR/(1 + sTR) g
v    = err / (TG (1 + sTP)),  held in [Uc, Uo]             (gate velocity)
g    = integral of v,  held in [PMIN, PMAX]
Pm   = (a23 + (a11 a23 - a13 a21) TW s) / (1 + a11 TW s) g
```

The turbine is a feedthrough plus a lag, so its exact hold is exact at every sample; with the ideal-turbine values
`a11 = 0.5, a13 = 1, a21 = 1.5, a23 = 1` it is `(1 - TW s)/(1 + 0.5 TW s)` and a unit gate step gives
`Pm(t) = 1 - 3 e^(-2t/TW)`. The droop loop is explicit and refused when a step would flip a gate perturbation's sign
(`(sigma + delta) dt / TG` above 1). At rest `g0 = Pm0 / a23` and `P0 = sigma g0`.

**DYR.** With `--dyr-extended` the reader maps the EXDC2, IEEEX1, ESAC8B, ESST3A and IEEEG3 records value by value. Each
record order is given in the same order by two public sources: the public copy of the PSS/E 33 model library data
sheets (6.30 EXDC2, 6.44 IEEEX1, 6.15 ESAC8B, 6.20 ESST3A, 7.15 IEEEG3), with the ANDES `psse-dyr.yaml` input lists
(EXDC2, IEEEX1, ESST3A), NREL PowerSystems.jl's `psse_dynamic_mapping.yaml` (ESAC8B) or PowerWorld's IEEEG3_PTI page
(IEEEG3). EXDC2 and IEEEX1 refuse `KE = 0` and `VRMAX = 0` (PSS/E computes those for the DC exciters at initialisation,
as OpenIPSL's IEEEX1 does), and a `SWITCH` other than 0. ESAC8B refuses `VRMAX = 0`, and `KE = 0` because PSS/E's
handling of it for ESAC8B is not confirmed by a public source. Refused by name, with the reason in the message:

| Record | Why it is not read |
|---|---|
| PSS2C | Modelled (file route), but one public source only gives its record order (PowerWorld). |
| ESAC7B | One public source only for its order (PowerWorld), and no public text found gives its block diagram; not modelled. |
| HYGOV4 | One public source only (PowerWorld's parameter list; its block diagram is not in text); not modelled. |
| GENQEC | Record order and saturation function not public; not modelled. |

GENTPJ stays refused as before. Without `--dyr-extended` all of these are models the reader does not map, with exactly
the previous message.

**File route.** A `gridsim-dynamics/2` exciter template takes `model` EXDC2 or IEEEX1 with the `dc1a...` parameters, ESAC8B
with the `ac8b...` parameters and ESST3A with the `st3a...` parameters; a stabiliser template takes PSS2C with the PSS2B
parameters plus `pss2cT12S`, `pss2cT13S` and optionally `pss2cPssOnPu` and `pss2cPssOffPu`; a governor template takes
IEEEG3 with the `ieeeg3...` parameters, `droopPu` (sigma) and `valveMaxPu`/`valveMinPu` (PMAX, PMIN).

### Turbine-governors

Each maps speed deviation `dw` (pu) to mechanical power `Pmech` (pu on the machine base); GGOV1 also reads the machine's
electrical power. `Initialize(Pm0)` sets the reference and every state so that `dw = 0` holds `Pm0` exactly.

**TGOV1** (`Tgov1`):

```
valveCmd = Pref - dw/R
Pgv      = FirstOrderLag_{T1, [Vmin, Vmax]}(valveCmd)
Pt       = LeadLag_{T2/T3}(Pgv)
Pmech    = Pt - Dt*dw
```

With `Pref = Pm0` the steady droop in the unlimited region is `dPmech/dw = -1/R - Dt`. `R <= 0` throws.

**IEEEG1** (`Ieeeg1`), the tandem-compound steam governor:

```
s1   = LeadLag_{T2/T1}(-dw/R)
Pgv  = FirstOrderLag_{T3, [Pmin, Pmax]}(Pref + s1)
y4   = lag_{T4}(Pgv)     y5 = lag_{T5}(y4)     y6 = lag_{T6}(y5)     y7 = lag_{T7}(y6)
Pmech = K1*y4 + K3*y5 + K5*y6 + K7*y7
```

The steady droop is `-1/R` when the fractions sum to 1. With `K1 = 1` and `T2 = T1` it is a single-stage governor.

**HydroGovernor**, the transient-droop hydro governor with the ideal water column:

```
err   = Pref - dw
c     = (err - Rt*Washout_Tr(g)) / Rp
g     = FirstOrderLag_{Tg, [gmin, gmax]}(c)
Pmech = (1 - s*Tw)/(1 + 0.5*s*Tw) * g  =  3*lag_{0.5*Tw}(g) - 2*g
```

`Initialize(Pm0)` sets `Pref = Rp*Pm0`, the gate to `Pm0` and the water lag to `3*Pm0`. The partial-fraction form
is `3*lag - 2*g`, so a gate opening `dg` drives the power first to `-2*dg` and then settles at `+dg` (the water-hammer
inverse response).
`Rp <= 0` or `Tw <= 0` throws.

**GAST** (`Gast`), the simple gas turbine with its exhaust temperature load limit (PSS/E GAST, CGMES GovGAST; data R,
T1, T2, T3, AT, KT, VMAX, VMIN, DTURB):

```
pd     = Pref - dw/R
limit  = AT + KT*(AT - x3)          (x3 at the start of the step)
x1     = FirstOrderLag_{T1, [VMIN, VMAX]}(min(pd, limit))
x2     = lag_{T2}(x1)               x3 = lag_{T3}(x2)
Pmech  = x2 - DTURB*dw
```

On the temperature limit the steady state solves `x = AT + KT*(AT - x)`, so the turbine settles at `AT` whatever `KT`.
`Initialize(Pm0)` needs `Pm0` inside `[VMIN, VMAX]` and at most `AT` (above it the limit binds and the start is not an
equilibrium); `R <= 0` or `KT < 0` throws.
**GGOV1** (`Ggov1`), the GE general governor-turbine model (PSS/E GGOV1, data sheet 7.8; the block structure as
OpenIPSL's PSS/E-verified GGOV1). Per unit on the turbine rating `Trate` when it is given (converted to and from the
machine base in the study), else on the machine base:

```
Pef   = lag_{Tpelec}(Pelec)
fb    = Pef (Rselect 1) | valve stroke (-1) | fsr of the last step (-2) | 0 (isochronous, 0)
xMW   = integral Kimw (Pmwset - Pef), in [-1.1 R, 1.1 R]           (supervisory load control)
err   = clamp(Pref + xMW - R fb - dw, minerr, maxerr)
fsrn  = Kpgov err + xI + Kdgov s/(1 + s Tdgov) err,   xI = integral Kigov err, in [Vmin, Vmax]
fsrt  = min(1, Kpload e + xL),   e = Ldref/Kturb + Wfnl - TEXM,   dxL/dt = clamp(Kiload e, Rdown, Rup), xL <= 1
fsra  = fsr(last step) + Ka dt (Aset - s/(1 + s Ta) dw)
fsr   = clamp(min(fsrn, fsrt, fsra), Vmin, Vmax)
valve = lag_{Tact}(fsr), rate in [Rclose, Ropen], position in [Vmin, Vmax]
wf    = valve (1 + dw) (Flag 1) | valve (Flag 0)
Pmech = LeadLag_{Tc/Tb}(Kturb (wf - Wfnl)) - Dm (1 + dw)
TEXM  = lag_{Tfload}(LeadLag_{Tsa/Tsb}(wf))
```

At rest the turbine holds `Pm0 + Dm`, so the valve and `fsr` sit at `fsr0 = (Pm0 + Dm)/Kturb + Wfnl`, which must lie in
`[Vmin, Vmax]`. `Pref = R fb0` and `Pmwset` are set on the first step from the measured power, so a machine at
equilibrium starts with `err = 0`. The load limiter starts at `xL = fsr0` and must not bind there
(`Ldref/Kturb + Wfnl >= fsr0`). In the RMS study the kernel passes each machine's start-of-step air-gap power
`Re(E'' conj(I))` to a governor that reads it (`IRmsElectricalPowerGovernor`); no other governor sees a change. At a
settled state the PID integral stops, so `err = 0`: with `Rselect 1` the power settles at `Pm0 - dw/R`, with
`Rselect -1` the valve stroke at `fsr0 - dw/R`; on the load limit the fuel flow is `Ldref/Kturb + Wfnl` and the power
`Ldref`. Refused by name: `Teng > 0` (no transport delay), `db != 0` (the public sources place the deadband differently)
and `Dm < 0` (PSS/E's speed-dependent fuel limit). The load limiter is left out when `Kpload = Kiload = 0`, the
acceleration limiter when `Ka` or `Ta` is 0. `Rup` and `Rdown` limit the rate of the load-limit integral. The
acceleration limiter follows PSS/E's discrete form, so its effect depends on the step.

**WSIEG1** (`Wsieg1`), the WECC modified IEEE type 1 steam governor (PSS/E WSIEG1, data sheet 7.35): IEEEG1's structure
with the gate `GV` mapped through a piecewise-linear gain table to the steam flow `PGV`:

```
GV   = FirstOrderLag_{T3, rate [Uc, Uo], [PMIN, PMAX]}(Pref + LeadLag_{T2/T1}(-K dw))
PGV  = NGV(GV)       (through (0, 0), (GV1, PGV1) .. (GV5, PGV5); the last segment extended)
y4..y7 = lags T4, T5, T6, T7 from PGV
Pmech = K1 y4 + K3 y5 + K5 y6 + K7 y7  (+ K2 y4 + K4 y5 + K6 y6 + K8 y7 on the same shaft)
```

A table of zeros is the identity. Points after the first all-zero pair are unused (PSS/E data leaves them 0); the
points must rise in `GV` and not fall in `PGV`, so `Initialize` inverts the table: `GV0 = NGV^-1(Pm0)` and
`Pref = GV0`. `IBLOCK` 1 to 3 set a `PMIN`, a `PMAX` or both that are 0 to the initial gate. The unit is
tandem-compound, as IEEEG1. The intentional deadband `db1` with its hysteresis `err` and the backlash `db2` must be 0.

`GovernedMultiMachine` hosts `Tgov1` on the classical swing through the `mechanicalPower` hook, one optional governor
per machine. It initialises each governor at the machine's initial `Pm` and then applies a supplied `mechanicalReference`
with `SetReference` (R246), so a set-point step travels through the valve and reheat lags; it passes no hook at all
when no governor is present, so a governed run with no governor is the classical run bit for bit. An islanded pair
with a sustained reference step `dP` settles to `omega_ss = dP / sum_i(1/R_i + Dt_i + D_i)`, where `Dt_i` is the governor's turbine damping and `D_i` the
swing damping (the test uses `Dt = 0`). TGOV1 data is on the machine's own base: pass `machineToSystemBase` (MBase / system
base, one per machine, GR-560) and each governor is initialised at `Pm / k`, referenced at `reference / k` and its output
scaled back by `k`, so a machine-base droop `R` acts as `k / R` on the system base; without it the governor reads the
system-base powers directly (exact only for a machine rated at the system base).

### Dynamic loads

A dynamic load (`DynamicLoadData`) takes `PMw + j QMvar` of a bus's load: the part of `Bus.Pd`, `Bus.Qd` the RMS network
would otherwise hold as a constant admittance at the solved voltage. The rest of the bus load stays that admittance, and
with no dynamic load attached the network is stamped exactly as before. A study takes the loads through
`RmsLoadedMachineData` (the machine data list with the loads), in place of the data list, on every RMS entry point.
`SubtransientInit` initialises each load at its bus's solved voltage, stamps its Norton admittance `Y` and makes the bus
a converter node; the kernel then drives it through the converter interface: it injects `Y V - I(V)`, where `I(V)` is
the current the load draws, inside the converter / network fixed point, and advances its states over each step with the
start-of-step voltage held. Its converter trace carries P and Q as injections (the consumption with the sign reversed).

**Third-order induction motor** (`InductionMotorLoadData`, `InductionMotorParameters`; PSS/E CIM5 and CIMW with a single
cage, the CMLD motor with `X'' = X'`). From the equivalent circuit `Ra + jXa` in series with `jXm` in parallel with
`R1/s + jX1` (motor base): `X = Xa + Xm`, `X' = Xa + Xm X1/(Xm + X1)`, `T'o = (X1 + Xm)/(ws R1)`. With the motor current
`I = (V - E')/(Ra + jX')`:

```
dE'/dt   = -(E' - j (X - X') I)/T'o - j s ws E',    s = 1 - w
2H dw/dt = Te - TL(w),   Te = Re(E' conj(I))
TL       = T0 (w/w0)^D  (CIM5, CMLD)  |  T0 (A w^2 + B w + C0 + D w^E), C0 = 1 - A w0^2 - B w0 - D w0^E  (CIMW)
```

In steady state this is the circuit exactly: `Z(s) = Ra + jX' + j(X - X')/(1 + j s ws T'o)`. The initial slip is the
stable-branch slip at which the motor draws its power at the solved voltage (bisection below the breakdown slip, found by
a golden-section search of `Te(s)`; both a fixed number of steps). A motor that cannot draw its power even at the
breakdown slip is refused. The motor base is `MotorMva` (PSS/E: MBASE, or PMULT x the MW load). The reactive power the
circuit draws differs from `QMvar`; the difference is a constant shunt at the bus, as PSS/E's "admittance of initial
condition Mvar difference". The motor advances by classical RK4 over the step with `V` held; once its speed reaches 0 it
is held there (the load does not drive it backwards). It stalls when the voltage falls below
`Vb = sqrt(T0 / Tmax(1 pu))` for a constant torque, with `Tmax` the circuit's largest torque.

**ZIP with exponential recovery** (`ZipRecoveryLoadData`; Karlsson and Hill, IEEE Trans. Power Systems 9(1), 1994). With
`u = |V|/|V0|`, of `P0` the share `1 - fr` follows the static characteristic and the share `fr` recovers:

```
P = P0 ((1 - fr) (C1 u^E1 + C2 u^E2 + 1 - C1 - C2) + xp + fr u^at),   Tp dxp/dt = -xp + fr (u^as - u^at)
```

and likewise for Q (`fq`, `bs`, `bt`, `Tq`). A voltage step moves the recovering share at once by the transient exponent
and then, through `Tp`, to the steady exponent. Below `VLOW` (default 0.7 pu) every term is evaluated at `VLOW` and scaled
by `(|V|/VLOW)^2`, a constant impedance, so the current falls to 0 with the voltage. The Norton admittance is the initial
admittance `conj(S0)/|V0|^2`.

**CMLD subset** (`CompositeLoadData`; PSS/E CMLDBLU1, data sheet 9.6). Modelled: the substation transformer (a fixed
ratio, then `Xxf`), the substation shunt `Bss`, the feeder `Rfdr + jXfdr` with its compensation split `Fb` at the
substation end and `1 - Fb` at the far end, and at the far end motor A (the third-order motor above, taking `FmA` of the
far-end power on a rating of that power over its loading factor) beside the static load (two exponential terms and a
constant, the reactive power from its power factor). Impedances are on the load MVA base. The internal network is solved
at every trial bus voltage (the static load as an admittance at its last voltage, iterated to 1e-13 pu); motor A then
advances with the far-end voltage held. Initialisation finds the far-end voltage and power so that the bus voltage and
active power equal the solved ones to 1e-14 (a fixed iteration of 200 passes at most, refused if it does not settle);
the reactive difference is a shunt at the system bus. The Norton admittance is the linear driving-point admittance with
the static load at its initial admittance.

**Not modelled** in the CMLD subset on its own (the extensions below add some of these): motors B, C and D (the single-phase air conditioner motor with its stall, restart and
thermal logic), the electronic load, distributed generation, the load tap changer (the tap is fixed), the under-voltage
relays and contactors, the frequency dependence of the static load and motor A's subtransient level. Where WECC's model
computes the feeder compensation and the tap at initialisation, the subset takes them as data. The motors do not see the
bus frequency: slip is measured against the nominal synchronous speed.

PSS/E DYR, with `--dyr-extended`: a `CIM5BL` record (I 'CIM5BL' LID IT RA XA XM R1 X1 R2 X2 E1 SE(E1) E2 SE(E2) MBASE
PMULT H VI TI TB D TNOM; the data sheet 9.2 and the PowerWorld CIM5 page give this order) becomes a third-order motor
on its load record's `PL + IP`, `QL + IQ`; the `YP`, `YQ` part stays the bus shunt. Refused by name: a double cage, saturation,
the under-voltage relay (`VI` not 0), a load with distributed generation, and the subsystem forms (CIM5OW, CIM5ZN,
CIM5AR, CIM5AL). `CIMWBL` is refused because only the PSS/E data sheet orders its last four values (NREL
PowerSystems.jl's DYR mapping holds no CIMWBL either). `CMLDBLU2` is refused for two reasons. Its value order is
confirmed by one public source only, the PSS/E data sheet. And GridSim does not do the WECC initialisation the record
relies on: the substation shunt, the feeder compensation and the starting tap, which WECC computes from the power flow,
are data in GridSim. `CMLDBLU1`, the first CMLD version, is refused by name; give the load through the API. Both models
come through the API, with all the parts below.

**CMLD extensions** (`CompositeLoadData.Extensions`, a `CompositeLoadExtensions`). They add motors B and C, motor D, the
electronic load, under-voltage relays on the three-phase motors, the substation tap changer and distributed generation. With `Extensions` null the
composite load is the subset above, bit for bit. The equations follow WECC MVWG, "WECC Composite Load Model Specification"
(January 2015, re-issued April 2021, public on wecc.org).

- Fractions. The far-end power splits as `FmA + FmB + FmC + FmD + Fel + Fstatic = 1`. `FmA` is `MotorFraction`, and 0 is
  allowed with extensions. The static load takes the rest. Fractions adding up to more than `1 + 1e-12` are refused; the
  WECC specification would scale them down instead.
- Motors B and C (`CompositeLoadMotor`). Each is the third-order motor of motor A, with its own fraction, loading factor
  and parameters, and its own Norton current at the far-end node. The code adds each part in the order A, B, C, D,
  electronic, static and skips absent ones. So a lone motor B or C gives motor A's bits, and empty extensions give the
  subset's bits.
- Motor D (`AcMotorParameters`), the single-phase air-conditioner performance model. It is algebraic, on its MVA base
  (its share of the power over `LFm`):

```
run, V >= Vbrk:            P = Po + Kp1 (V - Vbrk)^Np1     Q = Q'o + Kq1 (V - Vbrk)^Nq1
run, V'stall < V < Vbrk:   P = Po + Kp2 (Vbrk - V)^Np2     Q = Q'o + Kq2 (Vbrk - V)^Nq2
run at or below V'stall, or stalled:   P = Gstall V^2      Q = Bstall V^2,   Gstall + j Bstall = 1/(Rstall - j Xstall)
Q'o = Po tan(acos CompPF) - Kq1 (1 - Vbrk)^Nq1
```

  The WECC values are `Vbrk` 0.86, `Kp1` 0, `Kq1` 6, `Nq1` 2, `Kp2` 12, `Np2` 3.2, `Kq2` 11 and `Nq2` 2.5
  (`AcMotorParameters.Wecc`). PSS/E's CMLD record carries them as data, so they are parameters here. `Po` is `LFm` less the
  run-curve term at the starting voltage, so the motor draws its share at the start (with the WECC `Kp1 = 0` above
  `Vbrk`, `Po = LFm` exactly). `V'stall`, where the run curve below `Vbrk` meets the stall curve, is found by bisection to
  the last bit. The specification's 0.01 pu scan is only indicative, and PowerWorld uses 1e-4 pu.
- Motor D's two parts. Part A (`1 - Frst`) cannot restart. Part B (`Frst`) restarts once V stays above `Vrst` for `Trst`.
  Both stall once V stays below `Vstall` for `Tstall`. A thermal relay acts on each part: `d theta/dt = (I^2 Rstall -
  theta)/Tth`. The part's fraction falls linearly from 1 at `Th1t` to 0 at `Th2t` and never rises again.
- Motor D's relays. The under-voltage relay trips `Fuvr` for good once V stays below `Vtr1` for `Ttr1`, or below `Vtr2`
  for `Ttr2`. The contactors drop linearly from 1 at `Vc1off` to 0 at `Vc2off`. On the way up, the dropped part comes back
  linearly from `Vc2on` (none) to `Vc1on` (all).
- How motor D enters the network. It is an admittance, set from its power at the far-end voltage at the end of each step
  and held over the next step. Its run curve below `Vbrk` draws more as the voltage falls, which a network solution cannot
  hold as a power. The GridPACK reference implementation (Huang et al., arXiv 1708.00939, section III.D) holds the
  admittance from the last step only below `Vbrk`. GridSim holds it in every run state, so above `Vbrk` it adds a lag of
  one step, first order in the step like the rest of the integration.
- Electronic load (`ElectronicLoadParameters`). A constant power at `PFel`, scaled by `Fv1`, the specification's pseudocode:
  1 above `Vd1`, 0 below `Vd2`, linear in between while falling. `Vmin` tracks the lowest voltage but not below `Vd2`. On
  recovery only `frcel` of the shed part returns. The specification's last line has a misplaced parenthesis. It is read as
  the continuation of the line before it at `V = Vd1`. The electronic and static loads are solved at the trial voltage.
- Three-phase motor relays (`MotorUnderVoltageRelay`). Two stages, `(Vtr1, Ttr1, Ftr1)` and `(Vtr2, Ttr2, Ftr2)`, each trip
  their fraction once the far-end voltage stays below `Vtr` for `Ttr`. The fractions add. A tripped part scales the motor's
  MVA; the rest of the motor runs on.
- Tap changer (`CompositeLoadLtcParameters`). If the low-side voltage stays outside `[Vmin, Vmax]` for `Tdel`, the ratio
  moves one `Step`, then one more every `Ttap` while it stays outside. A low voltage lowers the ratio, within
  `[Tmin, Tmax]`. The ratio after n net moves is the starting tap plus `n Step`, from the integer n. The internal network
  is solved with the present ratio at every trial voltage, so a move needs no new factorisation: the stamped Norton
  admittance is only the starting estimate, and the load injects the difference.
- Timers. Each timer counts the whole steps whose voltage meets its condition. It acts at the end of the step on which
  the count n reaches its delay T, that is at n = ceil(T/dt), and resets when the condition fails. The quotient is taken
  with a 4-ulp guard, so a delay that is a whole number of steps acts on that step whatever the step's binary form: 30 s
  at 0.005 s acts after 6000 steps, 0.1 s at 0.01 s after 10. (Summing the step lengths would act one step late in both
  cases, because the rounded sum falls just short.)
- Quiet start. A composite load whose start would set off a part with no disturbance is refused: the low-side voltage
  outside the tap changer's band, the far-end voltage below a three-phase motor's relay setting (`Vtr1` or `Vtr2` of a
  stage with a fraction), below motor D's under-voltage setting (with `Fuvr` above 0), below motor D's `Vc1off`, at or
  below its stall voltage, or below the electronic load's `Vd1`. Motor D's contactors, when in use (`Vc1off` above 0),
  need `Vc2on >= Vc1off` (reconnection starts no lower than dropping starts; `Vc1on >= Vc2on` already holds, so
  `Vc1on >= Vc1off` and `Vc2on >= Vc2off` follow) and both reconnection voltages above 0. With `Vc2on` below `Vc1off`, a
  steady voltage between the two drops part of motor D on one step and reconnects some of it on the next, without end.
  The WECC defaults (`Vc1off` 0.5, `Vc2off` 0.4, `Vc1on` 0.6, `Vc2on` 0.5) meet the rule.
- Distributed generation (`CompositeLoadExtensions.Generation`, a `CompositeLoadGeneration`). A WECC DER_A unit (the
  existing DER_A model, unchanged) at the far-end node. At the start it generates `Fdg` (in [0, 1)) of the far-end load
  power, the power the load parts draw, so the far end's net draw is `1 - Fdg` of it and the bus still draws its
  power-flow P and Q. Its reactive power follows its power factor (1 gives none), and its MVA rating is its starting
  power over its loading factor. In the internal network it is a current source: its current at the trial far-end voltage
  enters every solve, and its states advance from the far-end voltage at the start of each step, as the RMS kernel drives
  a DER_A unit on a bus. DER_A's own start checks apply (the voltage inside `(vl1, vh1)` and the rest). The WECC
  specification gives the CMLD generation in MW; GridSim gives it as this share.

Not in the extensions: the frequency terms (`Pfrq`, `Qfrq` and motor D's
frequency factors), motor D's voltage lag `Tv` and inverse-time stall (`Tstall < 0`), the relays' reconnection (`Vrc`,
`Trc`), the tap changer's line-drop compensation and mid-band start, the WECC rule that trips the extra vars with the
components (the system-bus shunt is fixed), point-on-wave stall, and generation models other than DER_A. The CMLD DYR
records stay refused.

### Converter plant in the RMS engine

A generator with `IsConverterInterfaced` (battery, PV, wind, VSC HVDC) has no rotor. `SubtransientInit` removes it
from the machine fleet and retains its terminal bus as a network node after the machine internal nodes; converters
on one bus and of one class are aggregated into one `RmsConverterUnit`. The unit is a phase-locked current source.
Parameters are `RmsConverterParameters`; its state is `RmsConverterState` (after the WECC REGC_A interface with
REEC-style control):

```
Vf   = FirstOrderLag_{Tr}(|Vt|)
normal:     Ip* = P0/max(Vf, 0.01)        Iq* = Q0/max(Vf, 0.01)    (MinControlVoltagePu)
dip (Vf < Vdip):  Iq* = Iq0 + Kqv*(V0 - Vf)*Rating     Ip* = Ip frozen at dip entry
limit:      |Iq*| <= Imax*Rating (reactive priority);   |Ip*| <= sqrt((Imax*Rating)^2 - Iq*^2)
REGC_A:     Ip, Iq follow Ip*, Iq* on lag Tg; any rise in |Ip| is capped at rrpwr*Rating*dt per step
LVACM:      injected Ip scaled by 0 at |V| <= LvPnt0, 1 at |V| >= LvPnt1, linear between
injection:  I = (Ip*Lvacm - j*Iq) * exp(j*angle),   angle = terminal-voltage angle while |V| > 1e-3
```

A unit with fault-current limit `k = 0` (line-commutated HVDC) has `BlockInDip`: it feeds no current while in the dip
and ramps back after it, and it is never aggregated with current-limited units.

A unit with `Generator.GridForming` is a virtual synchronous machine: an EMF `E angle delta` behind the virtual
reactance `Xv`, stamped into the network as the Norton admittance `y = Rating/(j*Xv)`:

```
2*Hv*domega/dt = Pref - Pe - Dv*omega          ddelta/dt = ws*omega        (H = 2 s, D = 20 pu on the rating)
dE/dt = Kv*(Vref - |V|)    clamped to [0.5, 1.5] pu, frozen while current-limited
current limit: when |(E - V)*y| > k*Rating the terminal current is held at the limit in the same direction
```

The speed is advanced by semi-implicit Euler, `omega <- (omega + dt*(Pref - Pe)/(2H)) / (1 + dt*D/(2H))`.

### WECC second-generation renewable models

A converter unit can instead be driven by the WECC second-generation generic renewable models, bound from a PSS/E DYR
file (`Dynamics/Models/Wecc/`). The block diagrams are those of EPRI 3002006525, *Model User Guide for Generic
Renewable Energy System Models* (2015, public): REGC_A Fig. D-4, REEC_A Fig. D-1, REEC_B Fig. D-2, REEC_C Fig. D-3 and
Fig. 3-4, REPC_A Fig. 3-5, the current limit logic App. B. The data path is `SubtransientInit.MachineData.Renewable` (a `WeccPlant`):
a unit that carries a chain is converter plant whatever its case type (a PSS/E RAW renewable is typed as a synchronous
unit), is never aggregated with other converters on its bus, is a current source (no Norton admittance) unless its
generator model is the voltage-source REGC_B or REGFM_A1 (below), and is driven by `WeccConverterState` through the kernel's `IRmsConverterModel` interface. A unit without a chain keeps the
converter model above, through an adapter that makes exactly the calls the kernel made before, so its numbers are
unchanged bit for bit. Every quantity of the WECC models is per unit on the unit's MBASE; the network sees it scaled
by MBASE / system base. Reactive current is held exporting positive (the diagrams' `-1/(1+sTg)` gives the
network-convention `Iq = -Iq_export`).

```
REGC_A   Ip  = lag(Ipcmd, Tg), rise <= Rrpwr, upper limit LVPL(Vf)       Vf = lag(|Vt|, Tfltr)
         Iqx = lag(Iqcmd, Tg), export fall <= Iqrmax when Q0 > 0, export rise <= -Iqrmin when Q0 < 0
         LVPL(Vf) = 0 at or below Zerox, Lvpl1 (Vf - Zerox)/(Brkpt - Zerox) between, none at or above Brkpt
         network:  Ip_out = Ip * LVACM(|Vt|)  (0 at Lvpnt0, 1 at Lvpnt1, linear between)
                   Iqx_out = max(Iqx - Khv max(|Vt| - Volim, 0), Iolim)                   (HVRCM)
         injection I = (Ip_out - j Iqx_out) exp(j angle(Vt)) * MBASE / system base
REEC_A   Vt_filt = lag(Vt, Trv);  Voltage_dip = Vt < Vdip or Vt > Vup (raw Vt)
         Qref = PfFlag ? tan(pf0) lag(Pe, Tp) : Qext,  Qlim = clamp(Qref, Qmin, Qmax)
         VFlag=1: Vsel = PI(Kqp, Kqi) on (Qlim - Qgen) in [Vmin, Vmax];   VFlag=0: Vsel = Vref1 + Qlim
         QFlag=1: Iq = PI(Kvp, Kvi) on (Vsel - Vt_filt) in [-Iqmax, Iqmax];  QFlag=0: Iq = lag(Qlim / Vt_filt, Tiq)
         Iqinj = Kqv db(Vref0 - Vt_filt, dbd1, dbd2) in [Iql1, Iqh1] in a dip; after it Iqfrz for Thld (Thld > 0),
                 Iqv for -Thld (Thld < 0), else 0;  Iqcmd = clamp(Iq + Iqinj, -Iqmax, Iqmax)
         Pord = lag(rate-limited Pref [x speed when PFlag = 1], Tpord) in [Pmin, Pmax]
         Ipcmd = clamp(Pord / Vt_filt, Ipmin, Ipmax)
         Q priority: Iqmax = min(VDL1, Imax), Ipmax = min(VDL2, sqrt(Imax^2 - Iqcmd^2))
         P priority: Ipmax = min(VDL2, Imax), Iqmax = min(VDL1, sqrt(Imax^2 - Ipcmd^2));  Ipmin = 0
         Thld2: Ipmax held at its dip value for Thld2 after the dip
         the PI integrals, the Tiq lag and Pord freeze while Voltage_dip = 1; with Tpord = 0, Pord = clamp(Psel,
                 Pmin, Pmax) in a dip too (no lag, no freeze)
         s2 and s3 are linked: s3 at a limit and pushing into it stops, and s2 stops too if it pushes the same way
REEC_B   REEC_A without the VDL tables, the Iqfrz/Thld/Thld2 state machine, PFlag and Vref1 (VFlag=0: Vsel = Qlim)
REEC_C   REEC_B with the VDL tables and the store: SOC = SOCini - (1/T) integral(Pe dt), Ipmin = -Ipmax,
         SOC <= SOCmin -> Ipmax = 0, SOC >= SOCmax -> Ipmin = 0
REPC_A   Vmeas = VCFlag ? |Vreg - (Rc + jXc) I| : Vreg + Kc Qbranch;  s0 = lag(Vmeas, Tfltr), s1 = lag(Qbranch, Tfltr)
         e = clamp(db(RefFlag ? Vref - s0 : Qref - s1, dbd1, dbd2), emin, emax)
         Qext = leadlag(Tft, Tfv) of PI(Kp, Ki) on e in [Qmin, Qmax], integral frozen while Vreg < Vfrz
         df = db(1 - f, fdbd1, fdbd2);  droop = Ddn min(df, 0) + Dup max(df, 0)
         Pref (Fflag = 1) = lag(PI(Kpg, Kig) on clamp(Plant_pref - lag(Pbranch, Tp) + droop, femin, femax) in [Pmin, Pmax], Tg)
```

**Plant controller measurements.** REPC_A's regulated bus (BUSR) and the buses of its flow branch (BUS1, BUS2) are
retained as converter nodes with no injection, after every unit's bus (`RmsConverterUnit.NodeBuses`, the one order the
network builder and the kernel share), so the controller reads the voltages the network solves each step. The branch
current at BUS1 is `I = Y11 V(BUS1) + Y12 V(BUS2)` from the branch's own pi-model stamp (`AdmittanceStamp`, tap and
phase shift included); its P and Q are converted to the unit's MBASE, and so are `I` and `(Rc + jXc)` (all REPC_A
quantities are on the unit's MBASE). With no branch the plant output is the unit's own P, Q and current. Frequency is
the backward difference of the regulated voltage angle over the step, `1 + wrap(theta_k - theta_k-1)/(2 pi f0 dt)`,
through a first-order lag of `RepcaParameters.FrequencyFilterSeconds` (default 0.02 s, 0 for none; a GridSim parameter, not
WECC data, so no DYR record carries it; at a steady state the estimate is constant and the lag moves nothing); a network event steps the angle,
which this measurement sees as a short frequency spike, as a PSS/E bus frequency estimate also does. At the start
Vref is the initial Vmeas, Qref the initial Qbranch, Plant_pref the initial Pbranch and the reference frequency 1 pu,
so the controller starts with zero error; its integrals carry the REEC model's initial Qext and Pref. The converter
trace carries the regulated bus voltage (`RmsConverterTrace.RegulatedBusVoltagePu`).

**Network interface and integration.** LVACM and HVRCM are algebraic in the terminal voltage and are evaluated inside
the converter / network fixed point each step (to 1e-10 pu), as PSS/E evaluates them in its network interface. Every
dynamic state advances once per step from the start-of-step terminal voltage and the power measured at the committed
current: lags by the exact zero-order hold, integrators by forward Euler with non-windup limits, rate limits as a bound
on the change over the step, all states from the same start-of-step inputs. REGC_A's Accel (a PSS/E network-solution
acceleration factor) has no counterpart: the fixed point is solved to 1e-10 pu whatever its value.

**Initialisation and its checks.** From the unit's share of the solved injection (P0, Q0, V0), each model works
backwards: REGC_A `Ip = (P0/V0)/LVACM(V0)`, `Iqx = Q0/V0`; REEC_A the Pref and the Qext that hold that point (Q0 for Q
control, V0 - Vref1 for local voltage control), the PI integrals, Pord = P0, Vref0 = V0 when the record gives 0 (the
PSS/E convention). A start the model cannot hold is refused, naming the quantity: inside the dip band, a reference
outside its limit, a current command above the current limit, LVACM below 1 with power flowing, the voltage above
Volim with HVRCM active. The composed unit then evaluates every state's residual (lag input minus state, integrator
input) at t = 0 and refuses the start if any exceeds 1e-9 pu (`WeccEquilibrium.Tolerance`), listing them. The REEC_C
state of charge is not checked: a store delivering power is genuinely not at rest. Where the sources differ (the
HVRCM sign, which signal the Iq rate limits bound, the LVPL above Brkpt) the choice and the alternatives are recorded
in the code comments.

**Wind turbine mechanics** (EPRI 3002006525 App. C). A type 4 turbine may add the WTGT_A drive train alone (its
mechanical power held; with REEC_A PFlag = 1 the drive train's torsional mode then shows in the electrical power, the
type 4A behaviour); a type 3 turbine has WTGT_A, WTGAR_A, WTGPT_A and WTGTRQ_A together with REEC_A PFlag = 1 (the torque
controller gives a torque, which REEC_A multiplies by the generator speed, the PSS/E convention).

```
WTGT_A    2 Ht dwt/dt = Pm/omega_t - K theta - Dshaft (wt - wg)      Ht = Htfrac H, Hg = H - Ht
          2 Hg dwg/dt = -Pe/omega_g + K theta + Dshaft (wt - wg) - DAMP wg
          dtheta/dt = wt - wg,   K = 2 Ht Hg (2 pi Freq1)^2 / H   (the undamped torsional mode is exactly Freq1 Hz)
          Htfrac = 0: one mass, 2 H dw/dt = (Pm - Pe)/omega - DAMP w
WTGAR_A   Pm = Pm0 - Ka theta (theta - theta0)                       (pitch in degrees)
WTGPT_A   ew = omega_t - omega_ref + Kcc (Pord - Psel);  theta = lag(PI(Kpw, Kiw) on ew + PI(Kpc, Kic) on (Pord - Psel), Tp)
          every PI and theta within [TetaMin, TetaMax], theta's rate within [RTetaMin, RTetaMax]
WTGTRQ_A  omega_ref = lag(f(lag(Pe, Tp)), Twref)  (f: the four-point power-speed table)
          Tref = PI(Kpp, Kip) on (Tflag ? (Pref0 - Pe_filt)/omega_g : omega_g - omega_ref) within [Temin, Temax],
          integral frozen in a voltage dip; Pref0 is the plant controller's Pref, or P0
```

The drive train's torques are held over a step and its linear part is advanced by its exact zero-order hold, from the
matrix exponential of the augmented `[[A, B], [0, 0]]` (scaling and squaring of the Taylor series in plain arithmetic),
so the discrete torsional mode is exact: forward Euler would grow an undamped mode by `|1 + j w dt|` every step (about
36 times over 10 s at 2 Hz and 5 ms). A type 3 unit starts at the speed its power-speed table gives for P0, with the
pitch at WTGAR_A's theta0 (inside the pitch limits), the shaft twist carrying the steady torque, and the pitch's
initial angle in its speed integral. The pitch controller compares the REEC power order with the REEC power reference
after the speed multiplication. WTGTRQ_A's TRATE must equal the unit's MBASE (or be 0): the scaling between the turbine
and machine bases is not publicly specified, so a different value is refused rather than guessed.

**DER_A** (aggregated distributed energy resources; P. Pourbeik, *Proposal for DER_A model*, WECC REMTF memo, revised
6/19/19, public: Fig. 2, Fig. 3, the Appendix pseudo code and Table 1). It stands alone (no REGC/REEC/REPC) and is
driven by `DerAState`.

```
DER_A   Vt_filt = lag(Vt, Trv);  Iqv = clamp(Kqv db(Vref0 - Vt_filt, dbd1, dbd2), Iql1, Iqh1)   (always active)
        s1 = lag(Pord, Tp)  (the power feedback is the power order);  s2 = lag((Pflag ? tan(pf0) s1 : Qref) / Vt_filt, Tiq)
        Iqcmd = clamp(s2 + Iqv, -Iqmax, Iqmax);  Frq_filt = lag(f, Trf);  droop = Ddn min(df, 0) + Dup max(df, 0)
        Pord = lag(rate-limited [Freq_flag ? PI(Kpg, Kig) on clamp(Pref - s1 + droop, femin, femax) : Pref], Tpord)
        Ipcmd = clamp(Pord / Vt_filt, Ipmin, Ipmax)    (Q or P priority; Ipmin = -Ipmax for storage, typeflag = 0)
        m = lag(Vrfrac multiplier of Vt_filt, Tv) when Vtripflag = 1;  m = 0 once the frequency trip has acted
        Iq = lag(m Iqcmd, Tg);  Ip = lag(m Ipcmd, Tg) with the rrpwr rise limit (and its charging mirror)
```

The partial voltage trip is the memo's Appendix pseudo code on Vt_filt: between vl0 and vl1 the multiplier is
`(V - vl0)/(vl1 - vl0)`; after the voltage stays below vl1 for more than tvl1 the trip latches, Vmin is taken at that
instant, and on recovery the multiplier is `Vrfrac (vl1 - Vmin)/(vl1 - vl0) + (Vmin - vl0)/(vl1 - vl0)` (the tripped part
recovers Vrfrac of itself); below vl0 for more than tvl0 the unit trips for good. The high side mirrors it (vh1, vh0,
tvh1, tvh0, Vmax); the two multipliers multiply. The frequency trip (Ftripflag) acts after the filtered frequency stays
below fl for tfl or above fh for tfh, and reads nominal while Vt_filt <= Vpr. The memo's voltage source behind Xe is a
numerical interface device: GridSim solves the current-source interface to its fixed point, so Xe has no effect (it is
range-checked and named in a note). A start must have vl1 < V0 < vh1 (memo note 9); with Kqv not zero, V0 - Vref0 must
be inside the deadband: the memo has the software move Vref0 with a warning, GridSim refuses instead (Vref0 = 0 means
the initial voltage). PSS/E's DERA1 / DERAU1 record order is not in any public source, so those records are refused by
name; a DYR file gives DER_A as a `'DER_A'` record with the 48 values of the memo's Table 1, in that order.

**REGC_B, REEC_D and the voltage-source interface** (P. Pourbeik, *Proposal for new features for the renewable energy
system generic models*, WECC REMWG memo Rev. 27b, 2/16/23, public: REGC_B Fig. 1 and Table 1, REEC_D Fig. 3; the full
REEC_D parameter list is Table 5 of the EPRI REGC_B/REEC_D test protocol, memo to the WECC REMTF, 8/17/2020). REGC_B
replaces REGC_A's current source by a voltage source behind `re + jXe`; REEC_D extends REEC_A. Either REEC_D or REEC_A/B/C
may drive either generator model.

```
REGC_B   Iqx = lag(Iqcmd, Tg) with REGC_A's Iqrmax / Iqrmin rate limits;  Vf = lag(|Vt|, Tfltr), vp = max(Vf, 0.01)
         s1 = lag(RateFlag ? Ipcmd vp : Ipcmd, Tg), a rising magnitude limited to rrpwr (either sign: a store charging
              recovers at -rrpwr);  Ip = RateFlag ? s1 / vp : s1
         E target (terminal-voltage frame: d along Vt, Vtq = 0, id = Ip):  |Vt| + (re + jXe)(Ip - j Iqx)
         Ed, Eq = lag(target, Te)
         network: source (Ed + j Eq) e^{j theta} behind Z = re + jXe, theta the angle of Vt in the SAME network solution;
                  terminal current I = (E e^{j theta} - V)/Z, above Imax limited on the frame axis with priority pqflag
                  (Q: Iqx clamped to +-Imax, then Ip to +-sqrt(Imax^2 - Iqx^2); P: the other way round);
                  a converter blocked by REEC_D passes no current
REEC_D   Vt_filt = lag(Vt, Trv); Voltage_dip from Vt_filt (Vdip, Vup);  Vcomp = lag(Vcmpflag ? |Vt - (rc + jXc) It| : Vt + Kc Qgen, Tr1)
         Q path as REEC_A on Vcomp; Iqinj = clamp(Kqv db(Vref0 - Vt_filt, dbd1, dbd2), Iql1, Iqh1), always active
         Iqcmd held after a dip for |Thld| (Thld > 0: its last dip value; Thld < 0: Iqfrz); Ipcmd and Ipmax held for Thld2
         VDLq, VDLp: ten-point tables (a negative VDLq gives Iqmin = Iqmax < 0); Ipmin = -Ke Ipmax
         blocking: Vt_filt <= vblkl or >= vblkh sets every current limit to 0 until Tblk_delay after it returns; REGC_B then
                  passes no current at once (the memo's link from REEC_D to the generator)
```

The Norton admittance `MBASE/SBASE / (re + jXe)` is stamped on the unit's bus and the unit injects `I + y V`. Each step the
unit's interface is solved EXACTLY with the network (`Regcb.SolveWithNetwork`, through the kernel's opt-in
`IRmsNetworkSolvedConverterModel` path): from the second pass of the converter iteration on, the kernel gives the unit
the Thevenin view of its node, `V c1 = a + c2 I` with `a` the node voltage without the unit's own injection,
`c1 = 1 - Zdd y`, `c2 = Zdd MBASE/SBASE` and `Zdd` the driving-point impedance of the stamped network (one network solve
per such node, once per network stage). Writing `V = x e^{j phi}` turns the frame condition into one scalar equation,
`|x c1 - c2 L((E - x)/Z)| = |a|`, `phi = arg a - arg(x c1 - c2 L)`, with `L` the limit. Between the break points of
the limit it is a quadratic (unlimited or saturated: closed form) or smooth (partly limited: a 24-point scan and
bisection to the last bit). Of its roots the one whose angle is nearest the last committed angle is taken. Where no
root exists (a bolted fault near the terminal while the source still has its quadrature part `Xe Ip`, so no terminal
angle is consistent) the frame is held at the last angle, as a phase-locked loop freezes, and the limited current is
solved in closed form in that frame. With several REGC_B units each pass takes one Newton step on their injections,
using each unit's 2 x 2 sensitivity to `a` (two extra local solves) and the network columns between their nodes; one
unit needs no Newton step. The kernel's 1e-10 pu convergence test is unchanged, and a run with no REGC_B unit takes the
historical iteration line for line. REGC_A units keep plain substitution, but when a step's iteration hits the 50-pass
cap the kernel restarts that step from its starting voltages with every REGC_A unit solved exactly too
(`CurrentSourceNetworkSolver`: the injected frame current `Ip LVACM(x) - j HVRCM(x)` is affine in the terminal magnitude
between Lvpnt0, Lvpnt1, Volim and the Iolim point, so the frame equation is a quadratic per piece), and counts the step in
`Result.ConverterRescuedSteps`; a step that converges keeps its historical numbers. On IEEE 118 with four large REGC_A
units and a bolted fault at bus 30, substitution alone left 95 steps at the cap; with the rescue none. Plain substitution, which the other converter models use, does not converge for
REGC_B on a weak node: with the limit or blocking acting, the `y V` part is fed back with gain `|Zth/(Zth + Ze)|` and the
limited current's free component with gain about `|Zth/Ze|`.

Because the source sits in the terminal frame of each solution, a terminal turning at an off-nominal frequency leaves
no error: on a Thevenin source turning at +1 % for 2000 steps P and Q stay within 1e-12 pu (holding the source at the
start-of-step angle instead leaves `|E||V| 2 pi f0 df dt / |Z|`, 0.094 pu at 1 %, 5 ms and Xe = 0.2). The states advance
once per step from the start-of-step terminal voltage with the commands held, so a change of terminal voltage magnitude
moves the current at once, the voltage-source behaviour REGC_B exists for.

**REGFM_A1** (droop grid-forming inverter; W. Du et al., PNNL-35110, *Model Specification of Droop-Controlled,
Grid-Forming Inverters (REGFM_A1)*, September 2023, public: Fig. 1-5, Table 1), driven by `RegfmA1State`:

```
REGFM_A1  Pinv = lag(P, TPf), Qinv = lag(Q, TQf), Vinv = lag(|V|, TVf)            (P, Q at the terminal, MBASE)
          d omega = omega0 (mp (Pref - Pinv) + dPmax + dPmin),  delta' = d omega
          dPmax = min(0, kppmax (Pmax - Pinv) + xPmax), xPmax' = kipmax (Pmax - Pinv) <= 0;  dPmin mirrors it
          Vcmd = Vref + mq (Qref - Qinv) + dQmax + dQmin                           (dQ limits as dP, on Qinv)
          VFlag 1: Edroop = PI(kpv, kiv) on (Vcmd - Vinv) in [Emin, Emax];  VFlag 0: Edroop = clamp(Vcmd, Emin, Emax)
          network: I = (Edroop e^{j delta} - V)/(j XL); |I| > ImaxF -> I = ImaxF I/|I| (the angle kept, Fig. 4)
```

Without a plant controller Pref, Qref and Vref keep the values that hold the solved point; omega0 is data and must agree
with 2 pi f0 of the case to 0.1 %; nothing is frozen while the current limit acts (the specification is silent).

**Record names.** No public source gives the PSS/E record orders of REGCB1, REECDU1, REGFMA1 or DERA1, so GridSim
refuses those names with the reason and reads WECC-named records in the order of the public specification tables:
`'REGC_B'` (Table 1 of the Rev. 27b memo, 11 values), `'REEC_D'` (six flags, then the 78 values of Table 5 of the EPRI
protocol), `'REGFM_A1'` (the 22 data values of PNNL-35110 Table 1, without the three set points the power flow gives) and
`'DER_A'` (the 48 values of the DER_A memo's Table 1). REGFM_B1, REPC_B and REPC_C are not modelled: REGFM_B1's public
text leaves its steady-state current limiter and several units unspecified, REPC_B has no public block diagram, and
REPC_C's shunt switching needs controller-to-shunt data no case format carries.

**From a DYR file.** `PsseDyrRenewables` maps REGCA1, REECA1, REECB1, REECCU1, REPCA1, WTDTA1, WTARA1, WTPTA1, WTTQA1,
DER_A, REGC_B, REEC_D and REGFM_A1 (and the V33 user-model names,
the WECC names and USRMDL records) value for value, range-checks every parameter (REPCA1's regulated bus must be a bus
of the RAW case and its flow branch a two-terminal branch of it, found by its buses and circuit identifier), and
returns the chains in
`PsseDyrResult.Renewables`; the CLI carries them to `stability --rms` (see manual ch. 2 and ch. 15).

### IEC 61400-27-1 style Type 4 wind turbines

A converter unit can instead be an IEC 61400-27-1 style full-converter wind turbine, Type 4A or Type 4B, with Dynawo's
2020 structure (`Dynamics/Models/Iec61400/`). **Source basis.** It is implemented from public open-source
implementations and the CGMES DY definitions; it is not checked against the IEC 61400-27-1 text, which was not used. The
module structure was read from Dynawo (RTE, MPL-2.0, commit 43812dfb): `Dynawo.Electrical.Wind.IEC.WT` (WT4ACurrentSource2020,
WT4BCurrentSource2020), `Dynawo.Electrical.Controls.IEC.IEC61400` (Control4A2020, Control4B2020, PControl4A2020,
PControl4B2020, QControl2020, CurrentLimiter2020, Mechanical, PLL, GridProtection2020) and `Dynawo.Electrical.Sources.IEC`
(GenSystem4, RefFrameRotation). No Dynawo code was copied; each block is written again as a fixed-step discrete model. The
parameter names are the CGMES 2.4.15 Dynamics profile names (WindGenType4IEC, WindContPType4aIEC / 4bIEC, WindContQIEC,
WindContCurrLimIEC, WindMechIEC, WindProtectionIEC). CGMES 2.4.15 follows the 2015 edition, so the 2020 parameters it does
not name take Dynawo's names in lower case (`dprefmax`, `dprefmin`, `tpwtref`, `mpuscale`, `updip`, `kpufrt`, `uqrise`,
`tuss`, `kpqu`, `upqumax`, `tpll`, `upll1`, `upll2` and the measurement filters).

The data path is `SubtransientInit.MachineData.Iec61400` (an `Iec61400Data`). A unit that carries it is converter plant of
its own, a current source with no Norton admittance, driven by `Iec61400State`. Without it nothing changes: the
pre-existing RMS digests and a dynamics-study result were pinned from the 2.1.127 build before the change and still hold.
Values are per unit on the unit's MVA base; the network sees the current times MBASE / system base.

```
injection  I = (ip + j iq) exp(j theta_pll)      IEC frame: iq > 0 absorbs, Q = -U iq (Dynawo RefFrameRotation)
measure    Uf, Pf, Qf = lag(U, tufilt), lag(P, tpfilt), lag(Q, tqfilt)     (own terminal P and Q, generating positive)
PLL        theta_pll = lag(angle(V), tpll) while the last step's U < upll1 (frozen below upll2), else angle(V) at once
P control  4A: pref = lag(P0, tpwtref) with rate [dprefmin, dprefmax];  4B: pref = ramp(P0, dprefmin, dprefmax)
           x = pref (times U when mpuscale and U < updip);  4B: Paero = lag(x, tpaero), order input x w_gen
           Pord = lag(input, tpord), rise <= dpmax, Pord <= ipmax U;   ipcmd = Pord / max(Uf, 0.01)
Q control  mode 0 voltage: uref = clamp(xref + uref0, umin, umax);  modes 1, 3: uref = PI(kpq, kiq) on clamp(xref) - Qf
           eU = uref - Udrop,  Udrop = |U + (rdroop + j xdroop)(Pf - j Qf) / U|  (Dynawo VDrop)
           iqbase = -(PI(kpu, kiu) on eU), proportional limited to qmin/Uf .. qmax/Uf (kpufrt, unlimited, in a fault)
           modes 2, 4 (open loop): iqbase = -clamp(lag(xref, tqord) / Uf, iqmin, iqmax), held in a fault
           xref = Q0 (modes 1, 2), drop0 - uref0 with drop0 the dropped U0 (mode 0), tan(phi0) |Pf| (modes 3, 4)
           iqv = kqv deadzone(Uf - Uss, udb1, udb2),  Uss = lag(Uf, tuss)
           state: fault while Uf < uqdip or Uf > uqrise; post-fault after a dip for tpost (whole steps); else normal
           fault: iqcmd = clamp(iqv (ride-through mode 0) or iqbase + iqv (modes 1, 2), iqmin, iqh1)
           post-fault: as in the fault, or iqbase + iqpost in mode 2;  normal: iqcmd = iqbase
limiter    imax (imaxdip in a fault); tables ipvdl(Uf), iqvdl(Uf); reactive priority (mqpri) only in and after a fault:
             active priority:   iqmax = min(sqrt(imax^2 - min(ipcmd/w, ipvdl)^2), iqvdl),  ipmax = w ipvdl
             reactive priority: iqmax = iqvdl,  ipmax = w min(ipvdl, sqrt(imax^2 - min(iqvdl, |iqcmd|)^2))
             iqmin = min(iqmax, max(-iqmax, kpqu (Uf - upqumax))),  w = w_gen with mdfslim, else 1
generator  ip = lag(ipcmd, tg), rise <= dipmax, ip <= ipmax;  iq = lag(iqcmd, tg), rate [diqmin, diqmax], iqmin..iqmax
drive (4B) T = cdrt (w_wtr - w_gen) + kdrt theta,  d theta/dt = w_wtr - w_gen
           2 hwtr dw_wtr/dt = Paero / w_wtr - T,   2 hgen dw_gen/dt = T - P / w_gen
protection over and under voltage and frequency: trip when the time beyond the threshold exceeds the table's time for the
           present level (whole steps); frequency from the terminal angle's derivative (the change per step, then
           lag(tffilt / 20): Dynawo's Derivative block), rate-limited (dfmax), lag(tffilt)
```

**Discretisation.** Each step holds its inputs (the start-of-step terminal voltage and the unit's own power at the last
commit). Lags are advanced by the exact zero-order hold, `y' = u + (y - u) exp(-dt/T)`, so an equilibrium stays exactly
where it is. A rate limit clamps the step's change. An absolute limit clamps the state (non-windup): Dynawo's finite
anti-windup gains Kpaw, Kipaw and Kiqaw are replaced by the limit of a large gain, and are not parameters. The PI integrals
take forward Euler steps and are held during a fault. The drive train takes the semi-implicit Euler step: the speeds from
the start-of-step torque, then the twist from the new speeds. Its undamped map has its eigenvalues on the unit circle at
`phi = 2 asin(h wn / 2)`, with `wn^2 = kdrt (1/(2 hwtr) + 1/(2 hgen))`, so a free oscillation keeps its amplitude. The unit
refuses a step with `h wn >= 2` or `h cdrt (1/(2 hwtr) + 1/(2 hgen)) >= 2`. Dynawo integrates the twist with gain 1 (no
angle base factor), so `wn` has none either. The fault and protection timers count whole steps, so a run takes one step
size: a change is refused, and `dynamics-study` refuses a step schedule or the error-controlled step with an IEC unit.

**Initialisation.** From the unit's solved point only: `ip0 = P0 / U0`, `iq0 = -Q0 / U0`, every filter at its input, both
speeds 1 pu, the twist at `P0 / kdrt`, the power references at P0 and the voltage reference at the dropped voltage. Dynawo
starts the reactive-power PI at U0; GridSim starts it at the dropped voltage, the exact equilibrium (the two agree when
rdroop = xdroop = 0). A start with a current or order outside its limit, inside the fault band (U0 below uqdip or above
uqrise), or beyond a protection threshold is refused. Nothing is read from the first step, so a disturbance at t = 0 does
not change the start.

**Differences from Dynawo, on purpose.** Lookup tables hold their end values outside their range (Dynawo's table default
extrapolates through the last two points; give points that cover the operating range and the two agree). Placeholder limits
of +-999 are read as no limit. The converter's series and shunt impedance (Dynawo ElecSystem) is not built: the unit injects
at its terminal. Ride-through mode 3 (a user hook) is not built and is refused. The protection tables `tuover`, `tuunder`,
`tfover` and `tfunder` follow Dynawo's 2020 tables (allowed time against the level) under the CGMES WindProtectionIEC
names; CGMES 2.4.15 gives those names scalar times, after the 2015 edition.

**Verification.** Analytical, in `Rc5Wp23Iec61400Tests`: a flat start on a stiff bus for both types and five control modes;
the generator lag against its exact step response at every sample; the rate limits step by step; the fault state machine,
the kqv injection and the post-fault time at 10 ms and 5 ms steps; the current-limit circle with reactive and with active
priority; the undamped drive train against the discrete closed form at every step and its zero crossings against the
two-mass frequency; the protection timer at two step sizes; the protection's frequency derivative and its lag against
their closed form on an angle ramp; a disturbance at t = 0 leaves the start unchanged.

**Dynawo's reference curves are not compared.** Dynawo ships reference curves for its Neplan WT4A and WT4B 2020 examples
(`Examples/references/Dynawo.Examples.Wind.IEC.Neplan.WT4ACurrentSource2020_res.csv` and the WT4B file: P, Q and U every
1 ms over 25 s). A decimated copy (every 50 ms) is kept with the tests, with its MPL-2.0 attribution. The comparison the
curves invite (drive the unit's terminal with the recorded voltage, compare P and Q) cannot be made with the model as
built, and a test checks each reason on the data itself (`Rc5Batch2VerifyIec61400Tests`):
- Every disturbance before and around the faults is a change of a reference: the active power reference steps from 1 to
  0.5 pu at 2 s, the reactive reference from -0.21 to 0.2 pu at 4 s, and the grid frequency reference dips 1 % at 20 s.
  GridSim's turbine holds its references at their initial values and has no reference input, so the state it would reach
  the faults (6 s and 12 s) in is not Dynawo's.
- The reactive control enters both faults held at a limit: Q settles at 0.085 pu, not at its 0.2 pu reference, with the
  terminal voltage at 1.096 pu, so Dynawo's controller is in a limited state there. GridSim starts every unit at
  an unconstrained equilibrium from its solved point, so even started at 5.95 s its controller state would differ.
- The files hold the voltage magnitude only, not its phase. The PLL and the injected current's angle follow the terminal
  angle, which jumps when a fault starts and clears, so the voltage alone is not a complete boundary condition.
- P, Q and U are Dynawo's control measurements at the grid side of its ElecSystem (the LV transformer, RLvTr 0.001,
  XLvTr 0.01, GLvTr 0.0005, BLvTr 0.001 pu), which is not built here.

A reference input for P and x, a start from a limited controller state, the ElecSystem and the terminal phase would each be
needed; with them the tolerance would follow from the 1 ms recording interval and the linear interpolation between samples.

**Scope.** Type 4A and 4B only; Types 1, 2 and 3 are not built. The 2020 edition structure only (the 2015 structure is
refused by name). Constant reactive power limits (the Q limiter's voltage and power tables are not built). No plant
controller (WindPlantIEC). Balanced RMS, positive sequence. No PSS/E DYR record (PSS/E has no standard IEC records), and
the CGMES DY export does not write these classes. The unit is given through the API or the `dynamics-study` file's
`iec61400` member (manual ch. 15 and ch. 19).

Machines and converters share one network. With the retained admittance partitioned machines-then-converters,
`[Ymm Ymc; Ycm Ycc]` (`ConverterNetwork`):

```
Vc  = Ycc^-1 * (Ic - Ycm*E)
Im  = Yeff*E + Ymc*Ycc^-1*Ic,         Yeff = Ymm - Ymc*Ycc^-1*Ycm
```

A singular `Ycc` (a retained converter bus connected to nothing) throws `ArgumentException` rather than simulating on
a fabricated inverse. Each network stage is partitioned once.

## Algorithm

### Initialisation from a solved case

`SubtransientInit.FromSolvedCase(model, result, data, faultBusId, convertersAsCurrentSources)` builds
the `Prepared` fleet. It calls `result.RequireConverged("Subtransient initialisation")` first and throws
`NonConvergedStateException` on a power flow that did not converge. Then:

1. **Classify generators.** A generator is *held* when it is `PowerFlowOnly`, or is not a converter and has `H <= 0`;
   a *converter* when `IsConverterInterfaced` or its data carries a WECC chain (`SubtransientInit.IsConverterUnit`;
   unless `convertersAsCurrentSources` is false); otherwise a *machine*.
   With no machine the call throws `InvalidOperationException`. `data` must have one entry per generator.
2. **Island references.** An island with no integrable machine gets a synthetic stiff source at its slack bus (else a
   PV bus): `H = 1e4` s, all reactances `1e-3` pu, `T'do = 8`, `T''do = 0.03`, `T'qo = 0.4`, `T''qo = 0.05` s
   (`SubtransientInit.IslandReference*`). References are excluded from the synchronism test.
3. **Saliency gate.** `X''d` and `X''q` must agree to `1e-9` relative (`SubtransientSaliencyRelativeTolerance`),
   because only `X''d` is folded into the network; otherwise it throws `ArgumentException`.
4. **Per-unit.** For a machine of rating `MBase` with unit-transformer reactance `xt`
   (`Generator.UnitTransformerXPu`, floored at 0): `X_sys = (X + xt) * BaseMva/MBase` for every reactance,
   `H_sys = H * MBase/BaseMva`.
5. **Terminal condition.** Co-located units split the solved bus generation: active power by scheduled `Pg` (equally
   when `|sum Pg| <= 1e-9`, `MinScheduledPowerMw`), reactive power by capability `Qmax - Qmin` (else `MBase`); a lone
   unit takes all of it. With `Vt` the solved bus voltage and `S` the unit's share:
   `It = conj(S/Vt)`.
6. **Rotor angle.** `delta0 = arg(Vt + j*Xq*It)`. Placing the rotor on the q-axis phasor makes `Vd = Xq*Iq`, the
   condition that zeroes the q-axis flux derivative.
7. **dq quantities.**

```
Vq = Re(Vt)*cos(d) + Im(Vt)*sin(d)        Vd = Re(Vt)*sin(d) - Im(Vt)*cos(d)
Iq = Re(It)*cos(d) + Im(It)*sin(d)        Id = Re(It)*sin(d) - Im(It)*cos(d)
E'q  = Vq + X'd*Id      E'd  = Vd - X'q*Iq
E''q = Vq + X''d*Id     E''d = Vd - X''q*Iq
Efd  = E'q + (Xd - X'd)*Id + E''q*Se(|E''|)
Pm   = Vd*Id + Vq*Iq
```

   Every one of the four flux derivatives and the swing derivative is zero by construction.
8. **Network.** Loads become constant shunts `conj(S)/|V|^2` with `|V|^2` floored at `1e-9`. Each machine enters as
   `1/(j*X''d)` between its terminal and an internal node; held units enter as a negative load `-conj(S)/|V|^2` at
   their bus; each grid-forming converter adds its Norton admittance. At every size the full network is kept as a
   `SparseRmsNetwork` (`Prepared.Network`, `Prepared.NetworkFaulted`) and no dense matrix is formed, so a 9-bus case and
   gb-full run the same arithmetic. The dense Kron reduction into `YReduced` (`KronReduction.Reduce`, machines first,
   then retained converter buses) survives only as a test oracle (`SubtransientInit.DenseKronReferenceScope`, internal);
   no production path can select it. For a fault at `faultBusId` the faulted network is the same with
   `BoltedFaultShuntPu = 1e7` pu added at that bus.

`SubtransientInit.Prepared` carries `H`, `Damping`, `Pm`, `Efd`, `Delta0`, `Eqp0`, `Edp0`, `Eqpp0`, `Edpp0`, the
networks, the converter units, the held-unit names and the island references.

### The RMS integrator

`MultiMachineSubtransient.Simulate` integrates the fleet with a fixed step `dt` for `round(duration/dt)` steps.
Step `k`, at `t = k*dt`:

1. **Network stage.** The network in force is the last `RmsNetworkStage` in the list (which must be in time order; it is not sorted)
   whose `AtSeconds` satisfies
   `t + 1e-6*dt >= AtSeconds` (`StageTimeToleranceSteps = 1e-6`); before the first stage it is the base network. An
   event therefore takes effect on the first step at or after it, and an event time one rounding unit above its grid
   point (`0.1 + 0.14` against `48*0.005`) still lands on that grid point.
2. **EMFs and currents.** `E` from `(E''q, E''d, delta)`; `Im = network.Solve(E, Ic)`. With converters, the
   phase-locked currents and their terminal voltages are iterated to the algebraic fixed point (at most 50
   iterations, converged when the largest change of a converter voltage is below `1e-10` pu,
   `MaxConverterIterations`, `ConverterVoltageTolerancePu`); a step that does not settle is counted in
   `Result.ConverterIterationFailures`, never hidden. The machine currents are then re-solved against the committed
   converter currents.
3. **Record and judge.** The state is appended to the traces and synchronism is tested (below).
4. **Field.** For machine `i` with a controller, `Vt = E - j*X''*Im` and
   `Efd_i = controller.Step(Vref_i - |Vt|, omega_i, dt)`; without one the field is the seeded `Efd_i`. With no
   `Vref` supplied, each controlled machine's reference is set once, on the first step, to
   `|Vt| + controller.EquilibriumErrorPu(Efd)`, so the fleet starts at equilibrium; this needs the base network in
   force at `t = 0` and throws `InvalidOperationException` if any network stage other than the base network is in force at `t = 0`.
   With `Vref` supplied, the same is refused for ESST3A and EXDC2, which finish their initialisation on the first
   step's measurements.
5. **Fluxes.** With the start-of-step `Id`, `Iq` and EMFs:

```
E'q  <- lag_{T'do }( Efd - (Xd - X'd)*Id - E''q*Se(|E''|) )
E''q <- lag_{T''do}( E'q - (X'd - X''d)*Id )
E'd  <- lag_{T'qo }( (Xq - X'q)*Iq - E''d*Se(|E''|)*(Xq - Xl)/(Xd - Xl) )
E''d <- lag_{T''qo}( E'd + (X'q - X''q)*Iq )
|E''| = sqrt(E''q^2 + E''d^2)
```

The saturation is the PSS/E GENROU form: `Se` is evaluated on the subtransient flux magnitude `|E''|` at the start of
the step, and applied on both axes, the q-axis increment scaled by `(Xq - Xl) / (Xd - Xl)`, where `Xl` is the stator
leakage reactance (0 when the data gives none, the leakage-free limit `Xq / Xd`). An unsaturated machine has both
increments exactly 0. `SubtransientInit` seeds the field and the rotor angle with the same terms, so a saturated fleet
starts at equilibrium.

6. **Mechanical power.** `Pm_i = mechanicalPower(i, omega_i)` when a governor hook is supplied, else the seeded `Pm_i`;
   held across the RK4 stages.
7. **Converter controls** advance from the start-of-step terminal voltage.
8. **Swing.** Coupled RK4 on `(delta, omega)` of all machines, with `E''q`, `E''d` and the converter currents frozen
   and `Pe_i = Re(E_i conj(Im_i))` re-solved from the network at each of the four stages.

The step is fixed by default. A step schedule (fixed steps of a different size in each window) or the error-controlled
step (the windows chosen by step doubling over whole runs; EXPERIMENTAL, outside the verified contract) is opt-in; see [chapter 43](<0043 Long-term dynamics.md#step-schedule-for-the-full-rms-model>).
On a schedule, step k starts at the schedule's sample time and every component is given that step's size. The
frozen-input splitting of steps 5 and 8 is designed to be first order in the
step (no test measures the order), and this is the reason the step is a stated parameter of every result (`StudyDefaultStepSeconds = 0.005` s for a
study, `CctDefaultStepSeconds = 0.002` s for the clearing-time search).

**Network solve.** `IRmsNetwork` takes machine EMFs and converter currents and returns machine currents and converter
voltages. `SparseRmsNetwork`, the production network, stamps each machine as a Norton source `E/(jX'')` behind `1/(jX'')` at its terminal and solves the
full network by `FaultNetworkSystem` (sparse LU, factored once per network stage): `Im = (E - V_terminal)/(jX'')`.
The test oracle's `DenseMachineNetwork` multiplies `Im = Yred*E` with `MachineAdmittanceColumns`: the product is
vectorised across rows and keeps each row's own ascending summation order with no fused multiply-add, so it is
bit-identical to the scalar product on every vector width. `ConverterNetwork` is the oracle's dense form with converter
buses retained.

**Loss of synchronism.** After each step record, with `syncCount` the machines in the test (all, less the island
references) and `COI = sum(H_i*delta_i)/sum(H_i)` over them, the run is lost when any `delta_i` is not finite, any
`omega_i` is not finite, or `|delta_i - COI| > slipLimitRad` (default `2 pi`, `DefaultSlipLimitRad`). The test is
written `!(|d - COI| <= limit)` so a NaN fails it. Once lost the verdict stays lost.

### The study and the clearing-time search

`RmsTransientStability.Study(model, result, data, faultBusId, onsetSeconds, clearingSeconds, ...)` prepares the fleet
with `SubtransientInit`, schedules a bolted three-phase fault at `faultBusId` from `onsetSeconds` and the base
network again from `onsetSeconds + max(0, clearingSeconds)`, and integrates `durationSeconds`
(default `StudyDefaultDurationSeconds = 10` s) at `stepSeconds` (default `0.005` s). It requires a converged power
flow. An optional `exciters` list, one `RmsExciter?` per entry of `model.Generators` (null holds that field),
installs the AVRs. The list must align to the generators, entries for converter or held units must be null, an
exciter or a PSS instance may serve one machine only, and each violation throws `ArgumentException`. With
`exciters` null the study passes no controllers and is bit for bit the held-field study; an all-null list takes the
controller path with every field held and is bit for bit the same. No governor enters the study: mechanical power is
held at `Pm`.

`CriticalClearingBracket` returns a `CctBracket` (`CriticalClearingTimeSeconds` is its `StableSeconds`). The fault
is applied at `onsetSeconds` (default `CctDefaultOnsetSeconds = 0.1` s) and cleared `m` whole steps later, so the
trial for `m` steps clears at `m*stepSeconds` and runs `onset + m*stepSeconds + PostClearWindowSeconds (5 s)`. The
step defaults to `0.002` s, the window to `CctDefaultMaxSeconds = 1.0` s, giving `M = floor(1.0/0.002 + 1e-6) = 500`
whole steps. Because the integrator switches the fault only on whole steps, every clearing time inside one step is the
same run; the search is therefore over integers. `CctSearch.Run` does:

1. **Bisection** over `0 .. M` for at most `iterations` halvings (`CctDefaultBisectionIterations = 18`, which is a cap:
   9 halvings reach step resolution at `M = 500`). No floating-point midpoint is formed. If step 0 loses synchronism the
   bracket is `UnstableAtZero`; if step `M` survives the bracket has no unstable end.
2. **Downward verification** of the `scanSteps` (default `CctSearch.DefaultScanSteps = 16`) step counts below the
   stable end. A loss found there means the bisection landed on a stable pocket; the stable end becomes a recorded
   pocket and the bisection restarts below the loss. After `CctSearch.MaxRefinementRounds = 4` such rounds it walks up
   from step 1 instead, which is exact.
3. **Upward probe** of as many step counts above the unstable end; a step that holds again is recorded as the pocket.

Every trial is memoised. The result is the first loss from below: `StableSteps` is the last step below the first loss
and `UnstableSteps` is the next step. A pocket above it may still hold: on classical case9 bus 2 the search reports
312 ms, and 320 ms holds. `CctBracket.NonMonotone` is true when a pocket
was found (`PocketStableSteps`), `Trials` is the number of simulations, and `scanSteps = 0` is the plain bisection.
The guarantee holds provided no stable pocket is wider than `scanSteps` steps. The arguments are gated: a
non-positive or non-finite step or window, a window shorter than one step, `iterations < 1` and `scanSteps < 0` throw
`ArgumentOutOfRangeException`. The classical search (`ClassicalInit.CriticalClearingBracket`) runs the same
`CctSearch` with the classical integrator.

### How the CLI exposes the library

`gridsim stability <case> --rms` runs `RunRmsStability` (`src/GridSim.Cli/Program.cs`); the verb's flags are in
[the CLI reference](<../manual/0019 CLI reference.md#stability>). Without `--dyn-data` (a `gridsim-dynamics/2` machine data
file bound to the case's generators) the CLI builds the machine data from `X'd`: Anderson-Fouad `X'd` for case9 (`0.0608`, `0.1198`, `0.1813` pu on the machine base), the
fixed `0.30` pu (`CliDefaults.SyntheticXdpPu`) for every other case, with the ratios of `CliDefaults`:

```
Xd = 3.0*X'd    X'q = 1.5*X'd    Xq = 2.5*X'd    X''d = X''q = 0.8*X'd
T'do = 8.0 s    T''do = 0.03 s   T'qo = 0.4 s    T''qo = 0.05 s      D = 0      no saturation
```

`H` is the case's own `H`. The flags that select library models:

| Flag | Effect |
|---|---|
| `--rms` | Selects the six-state engine; without it the classical swing runs. |
| `--avr none` (default) | Field held; the study is the held-field study. |
| `--avr sexs` | One SEXS per synchronous machine: `Ta = 0.1`, `Tb = 1.0`, `K = 100`, `Te = 0.05`, `[Emin, Emax] = [0, 8]`. |
| `--avr ieeet1` | One IEEET1 per synchronous machine: `Ka = 50`, `Ta = 0.02`, `Ke = 1.0`, `Te = 0.2`, `Kf = 0.05`, `Tf = 1.0`, `[Vrmin, Vrmax] = [-10, 10]`, no saturation. |
| `--pss` | Adds one PSS1A to each exciter: `Ks = 20`, `Tw = 10`, `T1 = T3 = 0.15`, `T2 = T4 = 0.05`, `[Vsmin, Vsmax] = [-0.1, 0.1]`. |

A machine receives an exciter when it is not power-flow-only, not converter-interfaced and has `H > 0`; all other
entries are null. `--avr` accepts `none`, `sexs` or `ieeet1` (case-insensitive) and exits 2 with a message for any other value;
`--avr` or `--pss` without `--rms` exits 2; `--pss` without an AVR exits 2. A non-converged base power flow exits 3. A
study error (no machines, an exciter the initial field cannot satisfy, a singular converter block) is printed as
`stability: <message>` and exits 2.

The single-study form (`--clear <ms>`) uses a fault onset of `0.1` s, step `0.005` s and a duration of `3.0` s
(`CliDefaults.FaultOnsetS`, `RmsStepS`, `RmsDurationS`); the clearing-time forms use the `CctSearch` defaults above.
The default fault bus is 7 for case9, else the first non-generator bus. Every case runs on the sparse network. The
RMS study integrates at the case's own nominal frequency (`GridModel.FrequencyHz`: 60 Hz for case9, 50 Hz for the GB
cases); the library's `f0` argument overrides it.

### RMS event studies

`RmsTransientStability.StudyEvents(model, result, data, events, f0, stepSeconds, durationSeconds, slipLimitRad,
exciters)` drives the same machines, control library and sparse network with a list of scheduled events
(`src/GridSim.Core/Dynamics/Models/RmsEvents.cs`):

| Event | Spec form (`RmsEventSpec.Parse`) | Effect |
|---|---|---|
| `RmsBusFault` | `fault:BUS@t` | bolted three-phase fault (`BoltedFaultShuntPu`) at the bus until cleared |
| `RmsFaultClear` | `clear:BUS@t` | removes that bus's fault shunt |
| `RmsBranchTrip` | `trip:BRANCH@t` | takes the branch (0-based index) out of service |
| `RmsBranchReclose` | `reclose:BRANCH@t` | returns a tripped branch to service |
| `RmsLoadStep` | `load:BUS:PMW:QMVAR@t` | adds a constant-admittance load step; steps on one bus add |
| `RmsGeneratorTrip` | `gentrip:GEN@t` | disconnects the machine (0-based generator index), zeroes its mechanical power and leaves it out of the synchronism verdict |
| `RmsSequenceFault` | `sfault:TYPE:BUS@t`, `sfault:TYPE:BUS:R:X@t` | a three-phase (`3ph`), single line to ground (`slg`), line to line (`ll`) or double line to ground (`llg`) fault at the bus, bolted or through Zf = R + jX pu (system base), until a `clear:BUS@t` |
| `RmsBranchSequenceFault` | `lfault:TYPE:BRANCH:FRACTION@t`, `lfault:TYPE:BRANCH:FRACTION:R:X@t` | the same fault a fraction (strictly between 0 and 1, from the From bus) along a line |
| `RmsBranchFaultClear` | `lclear:BRANCH@t` | removes the fault along that line |

Every event takes effect at the first integration step whose time has reached its `AtSeconds` and stays in effect
unless a later event undoes it. The events are taken in time order, input order for equal times. At each distinct event
time a network stage is built from the base network's own stamps (`SubtransientInit.Prepared.Basis`): the Y-bus of the
branches still in service (rebuilt by `YBus.BuildSparse` once any branch is out), the load, held-unit and converter
stamps, the Norton stamp of every machine still connected, the accumulated load steps and a fault shunt on every bus
faulted at that time. Every stage is built and factored before the run starts, so a stage that cannot be solved (a
branch trip that leaves a bus with no path to earth) fails before any integration. The study refuses, naming the
event: an empty list or a null entry; a time that is not finite, not after `t = 0` or after the run's end; an unknown
bus; a branch index out of range, already out of service or tripped twice; a second fault on a faulted bus or a
clearance with no fault in place; a load step that is not finite or zero in both parts; a generator index out of range,
a unit that is not an integrated synchronous machine, a machine tripped twice, or a trip of every machine. A fault
and its clearance reproduce the single-fault `Study` bit for bit.

### Unbalanced and impedance faults

`RmsSequenceFault` and `RmsBranchSequenceFault` (`src/GridSim.Core/Dynamics/Models/RmsSequenceFaults.cs`) use the
standard positive-sequence method of balanced-frame RMS simulation, the one PSS/E and PowerFactory RMS use. At every
network stage the positive-sequence network carries, at the fault point, an equivalent shunt built from the negative-
and zero-sequence Thevenin impedances Z2 and Z0 seen there:

| Type | Equivalent fault impedance |
|---|---|
| three-phase | Zf (Zf = 0 is the `BoltedFaultShuntPu` shunt of `RmsBusFault`, the same stamp) |
| single line to ground (phase a) | Z2 + Z0 + 3 Zf |
| line to line (b-c) | Z2 + Zf |
| double line to ground (b-c-g) | Z2 in parallel with (Z0 + 3 Zf) |

Z2 and Z0 come from the short-circuit sequence networks of `FaultAnalysis.Unbalanced`. The negative sequence is the
in-service branch network with phase shifts reversed, the loads and load steps as the same constant admittances the RMS
network holds, and each connected machine as 1 / j(X2 + Xt) on the system base, X2 being `Generator.NegativeSeqXPu`
when given and the RMS X'' otherwise. The zero sequence is the IEC 60909 network (`Iec60909.BuildZeroSequenceNetwork`:
winding connections, neutral reactances, line R0 / X0 or 3 Z1, machine zero-sequence impedances). Both are recomputed
for every stage, so a branch trip or a machine trip during the fault changes them. Converter plant and units held as
constant injections are open in both, as in the static study. An earth fault at a point with no zero-sequence earth path
draws no current (single line to ground) or is the line to line fault (double line to ground). An earth fault on a model
whose zero-sequence network cannot be represented (`Iec60909.RequireRepresentableZeroSequence`) is refused.

A fault along a line splits the line at the fault point exactly as `FaultAlongLine.Build` does (R, X, B, R0 and X0 in
the ratio d : 1 - d) for the sequence networks, and eliminates the fault point from the positive-sequence network by Kron
reduction, so the two end buses carry the reduced stamps of the split line with the fault shunt. Only a line can be
faulted along its length (no tap, phase shift, winding data or zero-sequence mutual coupling). While the line is tripped
the fault carries no current; a reclose with the fault still in place recloses onto it. The run starts from the
operating point of the whole line, and the split applies only while the fault is in place. For a line with charging,
two lumped pi sections are not quite the network of one, so the fault-instant values differ from the static
fault-along-line study (which solves its own power flow on the split line) by that difference, about 3e-5 relative on
the WSCC 9-bus case; for a line without charging they agree to the power-flow tolerance.

`RmsTransientStability.StudyEventsWithFaults(...)` runs the event study (the same machine traces, to the bit, as
`StudyEvents`) and returns an `RmsFaultTrace` per sequence fault: at every step while it is in place, the
positive-sequence voltage at the fault point from a second, read-only solve of the step's network, the sequence currents
into the fault, V2 = -Z2 I2 and V0 = -Z0 I0, the phase voltage and current magnitudes, and the stage's Z2 and Z0.
`CriticalClearingBracketSequenceFault(...)` is the critical clearing search of `CriticalClearingBracketEvents` with a
sequence fault in place of the bolted one.

Scope. This is a positive-sequence representation. The machines see the positive-sequence network only: the braking
torque of negative-sequence current in the rotor, the double-frequency torque and every per-phase waveform detail are
not modelled (the EMT engine, `emt3`, carries phases). The negative-sequence network carries each dynamic load by its
Norton admittance, as the positive-sequence network does (for an induction motor, its locked-rotor admittance), with
that share taken out of the bus's constant load admittance. A balanced fault in place at the same time as an unbalanced
one (a bolted bus fault, or a three-phase sequence fault) is in the unbalanced fault's negative- and zero-sequence
networks too: it is the same shunt from each phase to earth, so it appears unchanged in every sequence, and the result
is exact. Two unbalanced faults in place at once (a cross-country fault) couple the sequence networks at two points,
which one equivalent shunt per fault cannot represent, so the event list refuses the second.

Verification (`Rc5Wp2RmsSequenceFaultTests`). At t = 0+ the fault currents, Z2, Z0 and phase voltages equal the static
operating-point study (`FaultAnalysis.Unbalanced` and `FaultAnalysis.ThreePhase`) on an earthed WSCC 9-bus case to
1e-10 relative, and Z0 bit for bit; a fault along a line without charging equals the static study on the split line
to 1e-8. A
three-phase fault with Zf = 0 reproduces the bolted `RmsBusFault` run bit for bit, and a shrinking Zf converges on it. On
a single machine against a stiff source the electrical power during each fault type matches the hand-derived transfer
reactance to 1e-10, and the critical clearing times order three-phase, then double line to ground, line to line and
single line to ground. Dense and sparse power flows, repeated and parallel runs and the three SIMD modes give the same
bits.

`RmsTransientStability.CriticalClearingBracketEvents(model, result, data, faultBusId, clearingActions, background, ...)`
runs the shared `CctSearch` on event studies: each trial places the fault at the onset, its clearance `m` steps later,
re-times every clearing action (a branch trip, typically) to that instant, keeps the background events as given, and
runs over onset + clearing + `PostClearWindowSeconds`. A clearing action that is itself a fault or a clearance is
refused.

The CLI exposes both (`stability <case> --rms`):

- `--events <list>` runs one `StudyEvents` with the comma-separated spec list. It takes no `--fault-bus`, `--clear` or
  `--clear-by-trip`, and no `--screen`. The run lasts `CliDefaults.RmsDurationS` (3.0 s) or 3.0 s past the last event,
  whichever is later, at the 0.005 s step; it prints the events in time order and the verdict. A list with a
  sequence fault also prints each fault point at t = 0+ and at its last step, and `--events-out` then writes
  `rms-fault-points.csv` (`RmsFaultPointsExport`) beside the other files.
- `--clear-by-trip <i,j,...>` clears the fault at `--fault-bus` by tripping the listed branches: with `--clear <ms>` it
  is one event study at that clearing time; without it, the `CriticalClearingBracketEvents` search.
- `--events-out <dir>` (with `--rms --events` only; exit 2 otherwise) writes the event study's export
  (`RmsEventsExport`, `src/GridSim.Core/IO/RmsEventsExport.cs`): `rms-events-machines.csv`
  (`t_s,trace,bus,delta_rad,speed_pu,eqp_pu,edp_pu,eqpp_pu,eddp_pu`, every machine trace including island references,
  each double in round-trip form), `rms-events-converters.csv` when the fleet has converters
  (`t_s,converter,name,bus,v_pu,p_pu,q_pu,ip_pu,iq_pu,in_dip_mode`), `rms-events.json` (schema
  `gridsim-rms-events-export/1`: case, nominal frequency, step, duration, `samples`, `everySteps`, `samplesWritten`, the
  events in time order, the verdict, the converter-iteration failures, the machine and converter lists, the file names,
  provenance and the `indicative` flag with its note) and `run-metadata.json`. Both CSVs open with the run header lines
  and a `# provenance:` line; the export is tagged `Synthetic` (a model trajectory, not a record), and its note says
  whether the machine data was synthesised (INDICATIVE) or came from a `--dyn-data` file tagged Recorded or
  DerivedFromRecorded throughout. An output error exits 1; the verdict is unchanged.
- `--events-every <N>` (with `--events-out` only) writes every N-th integration step (`k = 0, N, 2N, ...`) to the CSVs;
  N is a whole number from 1 to `RmsEventsExport.MaxEverySteps` (1,000,000), default 1 (every step). The summary
  records `everySteps` and `samplesWritten`. A full-resolution export holds one row per machine per step (a 3 s run at
  0.005 s is 601 samples per machine).

The captured critical-clearing-time screens for case9 (fault at each generator bus, worst first) show the effect of
the library models on the same synthesised machines:

| Capture | Field | Bus 3 (ms) | Bus 2 (ms) | Bus 1 (ms) |
|---|---|---|---|---|
| `docs/_captures/stability/stability-case9-rms-screen.txt` | held | 320 | 358 | 530 |
| `docs/_captures/stability/stability-case9-rms-sexs-screen.txt` | SEXS | 340 | 410 | 694 |
| `docs/_captures/dynlib/stability-case9-rms-avr-ieeet1-screen.txt` | IEEET1 | 336 | 398 | 636 |
| `docs/_captures/dynlib/stability-case9-rms-avr-sexs-pss-screen.txt` | SEXS + PSS1A | 342 | 410 | 788 |

The exciters raise every clearing time and the PSS raises bus 1 further. The values are indicative because the
machine data is synthesised.

A complete captured error pair:

```
$ gridsim stability case9 --rms --avr foo
error: --avr expected none, sexs or ieeet1 but got 'foo'
[exit 2]
```

```
$ gridsim stability case9 --rms --pss
error: --pss needs an AVR (--avr sexs or --avr ieeet1).
[exit 2]
```

### SVC, STATCOM and HVDC links

Without device data the RMS network leaves every HVDC link, FACTS device and static var compensator out, as it always
has: the loads are stamped from `Bus.Pd`, `Bus.Qd`, and a generator at a device bus takes the bus's solved injection with
the device's power folded in. A study that gives device models (`src/GridSim.Core/Dynamics/Models/Facts/`, `Hvdc/`) gets a
converter node for each device terminal, initialised on the device's power-flow state, and the generators at a link's bus
then carry only their own power. Two ways to attach a model:

- `SubtransientInit.MachineData.Facts`: an SVC or STATCOM held as a generator (scheduled at 0 MW), as PSS/E holds one.
- `new RmsLoadedMachineData(machines, loads, devices)`: `SvcDeviceData` (a `StaticVarCompensator` of the case; its BINIT
  is already in the bus shunt, so the model injects only the susceptance above it), `StatcomDeviceData` (a FACTS device in
  STATCOM mode; a UPFC or series-impedance device is refused and stays at its power-flow injection), `LccLinkDynamicsData`,
  `VscLinkDynamicsData` and `MtdcGridDynamicsData` (a multi-terminal DC link), each bound by name. Unknown, ambiguous or repeated names are refused.

Every block uses the exact zero-order hold of `ControlBlocks` from the start-of-step voltage; within a step each device is
a current source at its held states (the two ends of a link share one state, advanced by the first end).

| Model | Equations | Initialisation |
|---|---|---|
| CSVGN1 | e = V - Vref; u1 = K (1 + sT1)/(1 + sT3) e; u2 = (1 + sT2)/(1 + sT4) u1 in [VMIN, VMAX]; x = 1/(1 + sT5) u2 in [RMIN/MBASE, 1]; Y = CBASE/Sb - x MBASE/Sb; I = -j (Y - Yheld) V | x0 = (CBASE/Sb - Y0)/(MBASE/Sb), Vref = V0 - x0/K; refused outside the range |
| STATCOM1 | e = Vref - V - Xs Iq; Iq_cmd = Kp e + Ki/s e (non-windup); Iq = 1/(1 + sTc) Iq_cmd in [-ImaxInd, ImaxCap]; I = -j Iq (MBASE/Sb) V/\|V\| | Iq0 = Q0/(V0 MBASE/Sb), Vref = V0 + Xs Iq0 (+ Iq0/Kp when Ki = 0) |
| LCC1 | Io = Pord_f / max(Vdc_m, floor) (or the current order), held in [C0, VDCOL(Vdc_m)]; Id = 1/(1 + sTIDC) Io; Vdi = N (k Eac cos(gamma) - (3/pi Xc + 2 Rc) Id), Vdr = Vdi + Rdc Id, alpha from Vdr; below ALFDY the rectifier sits at ALFDY and the inverter takes Id (1 - margin); Q = P tan(phi) | Id0, Vdc_m0, Pord0 and the taps from the power flow; the inverter at the power flow's extinction angle |
| VSC1 | P_f = 1/(1 + sTp) Pref, Id = P_f / V; Iq from a filtered Q order or Iq0 + Kv (V0 - V) through 1/(1 + sTq); current limit with priority; the DC side carries the power the power converter exchanges (P_f, or V Id when Id is clipped, 0 below its voltage floor), and the DC voltage converter delivers it less R I^2 and both converter losses (its own current limit is not fed back) | P_f0, Q0 from the power flow; refused when a converter is over Imax |
| MTDC1 | per DC bus H dW/dt = P - V (sum y (V - Vj) + g V), W = V^2; per converter P mode lag(Pord, Tp), droop lag(P0 - Kd (Vdc - Vdc0), Tp), or DC voltage (holds Vdc, takes what balances its bus); AC side as VSC1 | Vdc, P and Q from the power flow's MTDC solution; refused when a DC bus is out of balance by more than 1e-9 of the powers it balances |

VSC1 has an opt-in flag, `Vsc1Data.DcFollowsLimitedPower` (`dcFollowsLimitedPower` in `dynamics-study`). With it, the DC
voltage converter's own current limit is fed back too: when it cannot deliver the DC power at its AC bus, it delivers V Id
at its limit and the power converter's power is cut to that plus the line and converter losses, so the DC side balances.
The default is off, which keeps VSC1's results bit for bit.

**Multi-terminal DC (MTDC1).** `MtdcGridDynamicsData` binds a multi-terminal DC link of the case by name and gives each
converter a control (`MtdcTerminalControl`, by AC bus): `Power` (an order through the lag Tp), `Droop` (P = P0 - Kd (Vdc -
Vdc0) through Tp) or `DcVoltage` (an ideal controller holding its DC bus at the power-flow voltage). Each DC bus stores
energy in its capacitance C (the converters' capacitors, an MMC's equivalent capacitance and any extra bus capacitance, for
example half of each cable's): H = C Vbase^2 / (2 Sbase), and the state is W = Vdc^2, so a constant power imbalance is a
straight-line rise in W. The DC lines and grounds are the link's resistances. Each step is backward Euler, solved by
Newton's method, because a resistive DC network is stiff (a bus's time constant is about 2 H R, far below an RMS step);
the line flows are formed from voltage differences so a low-resistance line does not lose them to rounding. Forward
Euler would be unstable there, and trapezoidal, A-stable but not L-stable, would ring on those fast modes (its step factor
is -0.85 at dt / tau = 25, backward Euler's 0.038); backward Euler is first order, as the held-power coupling is. The
converters' powers are held over a step, as every device's are, and a DC voltage converter's power is whatever balances
its bus at the end of the step. The droop order uses the DC voltage at the start of the step, so the droop loop is
explicit: on a DC bus on its own it is stable exactly for dt < 2 H V0 / Kd (the Jury test on its step map, whatever Tp),
and a run whose step is that long or longer at any free droop bus is refused, naming the bus and the limit. Every converter's AC side is VSC1's: a current source with a current limit, its reactive
power held at the power-flow value or under AC voltage control. A Power or Droop converter sends into the DC grid the
power it actually exchanges after its current limit; a DC voltage converter's own limit is not fed back.

The power flow solves a multi-terminal link with PSS/E's line-commutated converter equations, and MTDC1 starts on that
solution: the DC voltages and each converter's P and Q. In the run every converter is a voltage-source converter. Its
firing angle and tap are not part of the RMS model, and its reactive power is a control quantity rather than the
line-commutated converter's. The power flow's converters are lossless, so MTDC1's are too.

**Averaged MMC (MMC1).** `Mmc1Data` on a converter follows the averaged model of Saad et al. (IEEE Trans. Power Delivery
28(3), 2013). The six arms of N submodules of capacitance C_SM hold the energy of one capacitor C_eq = 6 C_SM / N. With the
energy controller off (the default), C_eq is lumped onto the converter's DC bus. With it on (Kp_W or Ki_W above 0), the
converter keeps its stored energy E apart: dE/dt = P_ac - P_dc and P_dc = lag(P_ac + Kp_W (E - E0) + Ki_W integral(E -
E0), Tw), so E returns to its starting value. The arm inductance adds w L_arm / 2 to the coupling reactance. The converter
is a current source, so that reactance changes its internal voltage (traced as `e_conv_pu_<bus>`) and not its current.

The LCC link blocks (no current) when the rectifier's AC voltage falls below VBLOCK, unblocks once it is above VUNBL and
the link has been blocked for TBLOCK, and its current order then recovers at CRAMP (pu of the initial current per s) and
the VDCOL voltage at VRAMP. The power flow settles its AC/DC alternation to 1e-9 (1 + |P|) MW from the voltages of its
previous AC solve; that residual is held as a correction scaled by Id / Id0, so a link starts exactly on the injections
the power flow reports.

Verification: the flat start of every model; the SVC droop point (5 V^2 - 3.96 V - 1 = 0 on a 0.2 pu Thevenin source),
the SVC at its capacitive limit (V = 1/1.4) and the STATCOM at its current limit (V = 0.75, Q = 0.75 pu against the SVC's
0.51 pu); the STATCOM slope point; the SVC's linearised time constant within 2 %; the LCC and VSC order steps against the
closed-form lag response to rounding; LCC blocking and the recovery ramp slope; the VSC DC power balance at every step;
for MTDC1, the energy ramp of a constant imbalance (bit for bit with power-of-two values), a two-terminal DC line against
its closed form V2 = (1 + sqrt(1.02)) / 2 and against the power flow's MTDC solution to 1e-10, droop sharing in the ratio
of the gains, the flat start, the MMC's C_eq = 6 C_SM / N and its H, and the energy controller returning to E0; and the
VSC1 voltage converter limit balanced against the DC equations.
These are analytical checks; the models are not validated against PSS/E or PowerFactory runs.

Not modelled: SVC switched elements (TSC and MSS of SVSMO1 to 3) and the slow susceptance regulator; LCC commutation
failure, DC line dynamics, tap changes during the run, bypass and restart details beyond the ramps, and the forced current
mode (TCMODE); for MTDC1, DC line inductance and capacitance dynamics beyond the bus capacitors, DC faults and DC
breakers, a DC voltage converter's current limit, and the MMC's circulating-current control and submodule balancing; VSC1
DC voltage (capacitor) and PLL dynamics, grid-forming control and MMC
internals (the EMT engine covers these); UPFC and TCSC dynamics. Under the RMS sequence faults the devices see the
positive sequence only. The adaptive-step study refuses a case with device models, as it refuses all converter plant.

PSS/E DYR, with `--dyr-extended`: CSVGN1 (`IBUS 'CSVGN1' ID K T1 T2 T3 T4 T5 RMIN VMAX VMIN CBASE /`, MBASE the unit's)
and CDC4T (`'DCNAME' 'CDC4T' ALFDY GAMDY TVDC TIDC VBLOCK VUNBL TBLOCK VBYPAS VUNBY TBYPAS RSVOLT RSCUR VRAMP CRAMP C0 V1
C1 V2 C2 V3 C3 TCMODE /`; a non-zero VBYPAS, VUNBY, TBYPAS, RSVOLT, RSCUR or TCMODE is refused unless unmodelled dynamics
are allowed). A record led by a quoted name is read as a DC line record only with the option, so the reader without it is
unchanged error for error. SVSMO1 to 3, CSVGN3 to 6, CSTCON, CSTATT, VSCDCT and the other DC line models are refused by
name: STATCOM1, VSC1 and MTDC1 are API and `dynamics-study` JSON only. These records were checked again for a second
public source that gives the same order: PowerWorld's pages give orders for CSVGN3 (CSVGN1's ten values and VOV, an
override voltage) and SVSMO3 (45 values, with deadbands, I2t limits and switched elements), but NREL PowerSystems.jl maps
none of them, OpenIPSL has CSVGN1 only, and the PSS/E model library is not public text. So they stay refused.

### User models: plug-ins, injection and load roles, the expr block

Two routes put a model of the user's own into the RMS engine: a declarative `gridsim-model/1` document (interpreted by
`ModelInstance` with the coded models' `ControlBlocks` operators) and a .NET plug-in (`GridSim.Core.Dynamics.Plugins`,
contract version 1). Both reach the kernel through the same adapters.

**Plug-in contract.** A plug-in assembly carries `[GridSimModelPlugin(1)]` and one `IUserModelFactory` per model (`Id`,
`Role`, `Parameters`, `Create`). The role interfaces are narrow and separate from the kernel's: `IUserGovernor`
(`Initialize(pm0)`, `Step(dw, dt)`), `IUserExciter` (`Initialize(efd0)`, `EquilibriumErrorPu(efd0)`, `Step(verr, dw, dt)`),
`IUserStabiliser` (`Step(dw, vt, pe, dt)`), and `IUserInjection` / `IUserLoad` (a constant Norton admittance Y,
`Initialize(P0, Q0, V0, rating)`, `Current(v)` with the states held, `Commit(v)`, `Step(t, v, dt)`), all in per unit on the
machine base (control roles) or the system base (currents), phasors as (re, im) doubles in the network frame. The adapters
(`PluginGovernor : IRmsGovernor`, `PluginFieldController : IRmsFieldController`, `PluginStabiliser : IRmsStabiliser`, and
`UserDeviceState : IRmsConverterModel`) forward each call with the kernel's own arguments, so the route adds no arithmetic,
and refuse a non-finite output naming the model and the time. The field controller reads the equilibrium error from a
private scratch instance, as the declarative adapter does.

**Network side of injection and load.** The kernel injects `I(v) + Y v` for an injection and `Y v - I(v)` for a load on top of
the stamp Y, so the network sees exactly the model's current and Y only conditions the converter / network fixed point (the
WP3b dynamic-load convention). A unit whose `MachineData.UserInjection` is set is converter plant in its own group, as a
WECC or FACTS unit is; a `UserDynamicLoadData` joins the dynamic loads. With neither, `IsConverterUnit`, the grouping and the
stamps are the historical ones (`d.UserInjection is not null` is one more false disjunct). When the plan is made, the model's
current at the solved voltage must give the power-flow P + jQ to `1e-10 (1 + |S0|)` pu, or the start is refused.

**Declarative injection and load.** Role `injection`: input `vm`, outputs `ip`, `iq` on the MBASE, initialised from
`p0 = P0 / rating`, `q0 = Q0 / rating`, `v0 = |V0|`; `I = (ip - j iq)(v / |v|) rating` (the unit phasor `v / |v|` stands for
`e^{j theta}` with no transcendental call). Role `load`: input `vm`, outputs `p`, `q`; `S = p P0 + j q Q0`, `I = conj(S / v)`
drawn; the Norton admittance is `conj(P0 + j Q0) / max(|V0|^2, 1e-9)`, the expression the network builder stamps for a
constant-admittance bus load, so a model with `p = q = (vm / v0)^2` draws `Y v` up to rounding and leaves the network as the
static load did. The current is the held evaluation `ModelInstance.EvaluateHeld`: the compiled order runs once with each
stateful block giving its output at its present state (a lag, integrator, rate limiter, hold, delay or saturated pole its
state; a lead-lag, washout or PI its feedthrough of the present input on its present state) and nothing advances. `Step` is
unchanged.

**The expr block.** The text is parsed once (recursive descent) into a reverse Polish instruction array: inputs `in0`..`in7`,
parameters `@Name` (in first-appearance order), literals without an exponent, `+ - * /` left-associative with `*` and `/`
binding tighter, unary minus, and `min`, `max` (two or more arguments folded left to right with `Math.Min` / `Math.Max`),
`abs`, `sqrt` (`Math.Sqrt`, correctly rounded on every platform), `exp` (`DetMath.Exp`) and `clamp(x, lo, hi)` (lo > hi gives
NaN). Evaluation is the array in order on a preallocated stack, so the arithmetic is the text's left-to-right reading:
`in0 - in1 / @R` is `Pref - dw / R` operation for operation and an expr TGOV1 is bit-identical to `Tgov1`. Nesting and stack
depth are capped at 64. The output unit is declared (`unit`) and not inferred; the parameters in the text are checked
declared. A non-finite result throws `UserModelException` with code `non-finite` naming the block.

**Determinism lint.** `PluginVerifier` reads the assembly bytes with `System.Reflection.Metadata` before any load: the plug-in
attribute (a member reference to the GridSim.Core attribute, so a look-alike defined in the plug-in does not count), the
assembly references (the framework and GridSim.Core only), every method body's instruction stream (each member, field, type
and signature token, `ldc.r4` and `conv.r4`), the signatures of every method, field and local, P/Invoke imports, module
references and body-less (extern) methods, the type definitions (an `IgnoresAccessChecksTo` attribute the plug-in defines
itself), and every remaining type and member reference. Each reference is looked up in `PluginDenylist`, and each refusal
names the member and the referencing type and method. The lists are private `ImmutableArray<string>` fields,
ordinal-sorted: namespaces, types (a listed type takes every type nested in it), members, the allowed `System.Math` and
`System.Double` members, and the types whose `GetHashCode` is a pure function of the value (the numbers, bool, char,
enums). GridSim.Core is open only in `Dynamics.Models`, `Dynamics.Plugins` and `Numerics`, checked by name and, in the type
reference table, by the assembly the reference resolves to; the loader's types and `SimdCapabilities` are refused there.
The `in`-parameter modifier `InAttribute` and the attribute types directly in `System.Reflection` (written by the SDK) are
allowed. Past the deny lists, a framework type (`System.*`, `Microsoft.*`, `Internal.*`, `Windows.*`) must lie in one of
the allowed namespaces (`System`, `System.Collections.Generic`, `System.Collections.Immutable`,
`System.Collections.ObjectModel`, `System.Diagnostics.CodeAnalysis`, `System.Globalization`,
`System.Runtime.CompilerServices`, `System.Runtime.Versioning`, `System.Text`) or be an attribute type of
`System.ComponentModel` or `System.Diagnostics`, and an assembly reference must be on a short allow list (GridSim.Core and
the framework assemblies that hold those namespaces). So a file or network route the deny lists do not name
(`System.Formats.Tar`, `System.Data`, `System.Resources`, `TextWriterTraceListener`) is refused by default. It remains a
lint, not a sandbox: a plug-in is trusted code.

Static fields of another assembly (a member reference whose parent is not the plug-in's own type or a generic
instantiation of one): `stsfld` and `ldsflda` are refused, and `ldsfld` is refused unless the field holds a number, bool,
char or string, so no host array or object is reachable to write through. A plug-in's own statics are allowed; the
replay check catches their effect between instances.

What the lint covers is exactly the list in manual chapter 19 under `user-model`. It checks names, not data flow: implicit
current-culture text operations (interpolation, `ToString()` without a provider, culture-sensitive comparison) and a
framework method that takes `IReadOnlyCollection<T>` or `object` and enumerates a dictionary or set handed to it are not
refused.

**Replay check.** `PluginReplay`, run when a model is bound, at the study step for 200 steps with the input raised by
0.01 pu at step 100, from two operating points of the role. Point A: Pm0 = 0.5; Efd0 = 1 with its own equilibrium error; a
stabiliser at Vt = 1, Pe = 0.5; an injection at 0.5 pu, a load at 0.5 + j0.1 pu. Point B: Pm0 = 0.6; Efd0 = 1.2; Vt = 1.02,
Pe = 0.6; an injection at 0.4 + j0.05 pu, a load at 0.4 + j0.08 pu; all at V = 1. One instance runs alone from A, then one
alone from B; then two fresh instances run side by side, one from each point, initialised one after the other and stepped in
turn. Each paired output must equal its solo run's output bit for bit, every output must be finite, and an injection's or
load's current asked twice at the same voltage must give the same bits. Because the paired instances start at different
points, static state shared between instances that changes an output within the 200 steps is refused: a shared counter
(BAD_STATIC) and a set-point written in `Initialize` and read in `Step` (LEAK_INIT, which two instances at the same point
cannot show) both differ at step 1. It does not prove determinism: sharing that changes no output in those 200 steps from
those two points passes.

**Loading.** `PluginLoader` reads a `gridsim-plugins/1` configuration and, for every entry before anything is loaded, checks
the ids (unique, not a shipped model's), the path (`GridSim.Core.Security.PathConfinement`, the scripting `--confine` rule
moved into Core with its code unchanged; `GridSim.Scripting.PathConfinement` forwards to it), the SHA-256 of the bytes read
once, the attribute and contract version, and the lint. It then loads those same bytes with
`AssemblyLoadContext.LoadFromStream` into one collectible context per configuration whose `Load` resolves nothing (the
framework and GridSim.Core come from the host, so the interfaces are the host's), and instantiates the public factory
classes in ordinal order of their full names.

**Binding.** `UserModelOverlay.ApplyAll` binds declarative and plug-in assignments through one internal interface
(`IAssignableModel`); the declarative texts (status, unit tag, note) are the R4-08 ones, and `Apply` is `ApplyAll` with no
plug-ins. A plug-in record's status is `SUPPORTED (PLUGIN, sha256:<assembly hash>, OUTSIDE THE VERIFIED CONTRACT)` and its
note carries the id, role, assembly, hash, contract version and parameter values.

Evidence: a plug-in TGOV1 and SEXS written with the public `ControlBlocks` operators, and an expr TGOV1, are bit-identical to
the coded models over 50 000 pinned inputs at three step sizes, on IEEE 9 with all three machines governed and excited, and
in the dynamics study on the 14-bus fixture (API and CLI). A constant-power plug-in injection on a two-bus system holds the
closed-form operating point (d = asin(0.1) / 2, V2 = cos d) to 1e-12 pu for 1 s. A declarative constant-impedance load gives
the static load's bus voltage, and the closed-form faulted voltage 0.33081894288367963 pu through j0.05, to 1e-12.

### User machines and relays: plug-in contract 2

Contract 2 adds two roles to the five of contract 1, appended to `UserModelRole` so the old numbers stand: `Machine` and
`Relay`. Every contract 1 interface is unchanged, a `[GridSimModelPlugin(1)]` assembly loads exactly as before, and a
machine or relay model needs `[GridSimModelPlugin(2)]`. A configuration entry may say `"contract": 1` or `2`; when it does,
the assembly must declare the same version. An assembly that declares any other version is refused ("this build implements contracts 1 and
2"), and a contract 1 model whose factory gives a role number outside the five contract 1 roles is read and refused when
bound, as it was before contract 2 existed. Everything else about loading is as above: the confined path, the SHA-256 pin
of the bytes read once, the determinism lint (a relay that reads the clock is refused, `System.DateTime` and `Stopwatch`
being on the list) and the replay check, with a machine probed from 0.5 pu and 0.4 + j0.05 pu and a relay through a dip to
0.7 pu, once with a unit's inputs and once with a branch's (no frequency, rate of change or speed). The machine probe
compares the Norton current read twice; the admittance, speed, angle, field current and power are checked finite there, and
the admittance is held to the same bits at every step of a run. A plug-in may not take the name of an assembly GridSim
grants its internals to (an `InternalsVisibleTo` target, read from the attributes). Results that record a contract version for the plug-in route as a whole still say 1; each model's own provenance
names its assembly's version.

**Machine.** `IUserMachine` is a Norton source with a constant admittance on the system base: `Initialise(P0, Q0, V0,
MBASE)` presets the states so the machine exports the power-flow P0 + jQ0 at V0, `NortonAdmittancePu` is stamped once,
`NortonCurrentPu` is the source current from the present states, and `Step(dt, t, v)` advances them from the step's
converged terminal voltage. It also reports its speed, rotor angle, field current and electrical power, which must stay
finite. The kernel runs it by the injection route (`MachineData.UserInjection` holding a model of role machine): a unit on its
own bus, iterated with the network at each step, the net current `I_N - Y v`, stepped once per step. The start is checked as
an injection's is, to `1e-10 (1 + |S0|)` pu, and an admittance that changes during the run stops it, naming the model.

This is not the route the built-in machines take. Their rotor swing is integrated by the kernel itself, by RK4 with the
network re-solved at each sub-step, and no plug-in can join that integration. So a plug-in machine owns its rotor, its
field and its mechanical power, takes no bound exciter or governor, and is not judged in the synchronism verdict or offered
as a rotor-angle channel; a classical plug-in machine is close to the built-in GENCLS but not bit-identical to it. A change
of the machine's admittance (saturation, for example) must be carried in the Norton current, the same approximation the
built-in subtransient machines make. There is no declarative machine role: the block catalogue has no way to express the
network interface.

**Relay.** `IUserRelay.Evaluate` reads, at every step start, from that step's converged solve: the step number and size,
the time, the unit bus voltage magnitude, the machine's electrical frequency F0 (1 + w) and F0 dw/dt from the swing
equation (the quantities the built-in 81 and 81R stages read), the unit's active and reactive power and its speed. It
returns no action or a trip with a reason code. A relay is bound to a unit by name (`UserRelayBinding`) and run by
`RmsTransientStability.StudyEventsWithUserRelays`, with or without the units' own protection; it is evaluated after the
built-in stages and trips through their path: the unit is out from the next step, its mechanical power is 0 and its field
held, it is no longer judged, and a trip of the last connected machine ends the run with the reason. A unit trips once.
Timers count whole steps (the step number is given for that). Its record names the device `PLUGIN:<id>`, carries the reason
code in place of the operate quantity and the plug-in provenance, with OUTSIDE THE VERIFIED CONTRACT, as its settings. The
inputs also carry the magnitude of the machine's current.

**Branch relays.** The same relay may be bound to a branch instead (`UserRelayBinding.ForBranch`, the branch's index and the
bus at the end it reads). It then reads, from the same solve and after the unit relays, the bus voltage magnitude at that end
and the current, active and reactive power flowing into the branch there (the branch's own pi-model stamp applied to the
solved bus voltages); a branch has no machine, so the frequency, its rate of change and the speed are NaN, and a declarative
relay that reads any of them is refused when it is bound to a branch. A trip opens the branch at both ends from the next
step: the stage in force is rebuilt from its own state with the branch out, exactly as a scheduled branch trip builds it,
and the branch stays out (a later scheduled reclose does not bring it back). A branch out by a scheduled event is not
evaluated. When the network without the branch cannot be solved (a part of it left with no path to ground) the run ends at
that step with the reason. A fault along the relay's own branch (`RmsBranchSequenceFault`) is refused, because it splits the
branch and the current at its end is no longer the branch's. Branch relay records (`FullModelBranchRelayRecord`) name the
branch, the measuring bus, the device, the trip and disconnection times and the reason code.

**Declarative relays.** A `gridsim-model/1` document of role `relay` is a relay too (`UserRelayBinding.ForUnit` or
`ForBranch` with a `UserModel`; device `USER:<id>`). Its input ports, in this order, are `vm` (pu), `f` (Hz, declared `1`),
`p` and `q` (system pu), `dfdt` (Hz/s, declared `1/s`), `speed` (pu) and `i` (system pu): the relay inputs above, with no
conversion. Its one output, `trip`, is 0 for no action; any other value trips, and that value, a whole number, is the reason
code. It has no initialisation inputs. Two blocks join the catalogue for it, usable in any role:

- `ontimer(u; time)`: an on-delay timer counted in whole steps. It picks up at the first step its input is non-zero and gives 1
  from the first step k with `(k - k_pickup) h + h 1e-6 >= time`, exactly the built-in unit relay stages' rule; a step with
  the input 0 resets it. The step must keep one size. It is initialised with 0.
- `latch(u)`: a seal-in, 1 from the first step its input is non-zero to the end of the run, initialised with 0 or 1.

A comparison is a `switch` block (`vm` against a threshold gives the strict `V < pickup` of the built-in 27 stage). The
document is pinned and hashed like any declarative model, and its record carries its id, document hash and compiled order
digest with no label: it runs inside the verified block catalogue. The refusals of an unknown role or block type list the
roles and blocks they always listed.

**Injection, machine and load models in a dynamics study.** `DynamicsStudyRequest.DeviceModels` (on the CLI, the
`deviceModels` member of the `--model-assignments` file) binds an injection model (a plug-in or a declarative document) or
a machine plug-in to a unit, by its native identity, and a load model (a plug-in or a declarative document) to part of a bus
load. The unit becomes converter plant on its own bus, its DYR machine, exciter and governor data are not used, and the
coverage notes name the model and where it is bound; a plug-in's note carries its provenance and OUTSIDE THE VERIFIED
CONTRACT, a declarative model's carries its id and document hash and no label, because the declarative blocks are the
verified catalogue. A unit with an injection or machine model cannot also take a governor or exciter model, an IEC 61400
model, or be the angle reference, and device models do not run with the adaptive companion run, a step schedule or the
error-controlled step. The unit itself must still be bound by the DYR coverage (a machine record, or the `leaveOut` policy).

**Relays in a dynamics study.** `DynamicsStudyRequest.RelayModels` (on the CLI, a `deviceModels` entry of role `relay`)
binds a relay plug-in or a declarative relay to a unit, `"bind": { "machine": { "bus": 2, "id": "1" } }`, or to a branch,
`"bind": { "branch": { "from": 2, "to": 4, "circuit": "1" } }`, read at its `from` end as written. Each events row is then also
run on the full-model fixed-step engine with the relays acting (and the units' own relays, when unit protection is given), as
additional evidence: the study's own rows are unchanged and decide its status. On the CLI the companion run is the
`unitProtection.fullModel` member with unit protection and the `userRelays.fullModel` member without it, and
`provenance.relayModels` names each relay (a plug-in with OUTSIDE THE VERIFIED CONTRACT). A declarative relay's file is
listed in the run journal, so `breaking-change validate` re-checks its hash. Relays do not run with the adaptive companion
run, a step schedule or the error-controlled step.

Evidence: a plug-in under-voltage relay written as the built-in 27 stage is evaluated trips at the same step as that stage
on the 9-bus case, and the whole run is the same bits; a relay that never operates changes no bit of the run; a latched
relay trips once after its delay on a fault shorter than the delay, where the same relay unlatched does not trip. The same
three hold for the declarative relay (switch, ontimer, latch), and the declarative latch gives the plug-in latch's bits. A
declarative overcurrent relay on a 9-bus branch opens it inside a bus fault, and the run is the same bits as the same events
with a scheduled trip of that branch at the relay's disconnection step. Relays through the CLI give the API's records to the
bit and leave the study's own rows as they were. A
classical plug-in machine with an inertia too large to move meets the closed-form two-source fault voltage at every sample
to 1e-12 pu, and the same machine with a real inertia repeats its run bit for bit. An injection model through the CLI gives
the API run's bits on every channel sample of the 14-bus study.

## Parameters and defaults

Constructor defaults are those of the library; the CLI values are in the previous section. "-" means no default: the
caller must supply the value.

### Machine data (`SubtransientInit.MachineData`, `SubtransientMachine.Parameters`)

| Name | PSS/E symbol | Unit | Default | Validation |
|---|---|---|---|---|
| `H` | `H` | s, machine base | - | `> 0` or the unit is held |
| `Damping` | `D` | pu power / pu speed | - (CLI 0) | none |
| `Xd` | `Xd` | pu, machine base | - | none |
| `Xdp` | `X'd` (`Xpd`) | pu, machine base | - | `> 0` in the SMIB `Parameters` |
| `Xdpp` | `X''d` (`Xppd`) | pu, machine base | - | `> 0` in the SMIB `Parameters`; equal to `Xqpp` in the fleet |
| `Xq` | `Xq` | pu, machine base | - | none (the one-axis SMIB `Parameters` requires `> 0`) |
| `Xqp` | `X'q` (`Xpq`) | pu, machine base | - | `> 0` in the SMIB `Parameters` |
| `Xqpp` | `X''q` (`Xppq`) | pu, machine base | - | `> 0` in the SMIB `Parameters`; equal to `Xdpp` in the fleet |
| `Tdop` | `T'do` (`Tpdo`) | s | - | `> 0` |
| `Tdopp` | `T''do` (`Tppdo`) | s | - | `> 0` |
| `Tqop` | `T'qo` (`Tpqo`) | s | - | `> 0` |
| `Tqopp` | `T''qo` (`Tppqo`) | s | - | `> 0` |
| `Saturation` | `S(1.0)`, `S(1.2)` | dimensionless | `null` (none) | `0 <= S1 < S12` when `S1 > 0` |
| `Xl` | `Xl` | pu, machine base | 0 | finite, `>= 0` and `< X''d`; used only by the q-axis saturation scale |
| `ExternalReactance` (SMIB) | - | pu | - | `>= 0` |
| `InfiniteBusVoltagePu` (SMIB) | - | pu | - | none |

The stator is not resolved below `X''`. The leakage reactance `Xl` (default 0, finite, at least 0 and below `X''d`)
enters only the q-axis saturation scale `(Xq - Xl)/(Xd - Xl)`. Saturation acts on the flux magnitude `|E''|` on both
axes, as GENROU applies it.

### Blocks and control models

| Model | Parameter (code) | Symbol | Unit | Default | Rule |
|---|---|---|---|---|---|
| `FirstOrderLag` | `gain`, `timeConstantSeconds`, `min`, `max` | `K`, `T` | -, s, pu | `min = -inf`, `max = +inf` | `max >= min` |
| `LeadLag` | `leadSeconds`, `lagSeconds` | `Ta`, `Tb` | s | - | `Ta >= 0`, `Tb > 0` |
| `Washout` | `timeConstantSeconds` | `T` | s | - | `> 0` |
| `Integrator` | `gain`, `min`, `max` | `K` | 1/s | `-inf`, `+inf` | `max >= min` |
| `PiController` | `proportionalGain`, `integralGain`, `min`, `max` | `Kp`, `Ki` | -, 1/s | `-inf`, `+inf` | `max >= min` |
| `Tgov1` | `droopPu` | `R` | pu | - | `> 0` |
| | `valveTimeSeconds` | `T1` | s | - | `<= 1e-12` is instant |
| | `reheatLeadSeconds`, `reheatLagSeconds` | `T2`, `T3` | s | - | `T2 >= 0`, `T3 > 0` |
| | `turbineDampingPu` | `Dt` | pu | `0` | none |
| | `valveMinPu`, `valveMaxPu` | `Vmin`, `Vmax` | pu | `0`, `+inf` | `max >= min` |
| `Ieeeg1` | `droopPu` | `R` (`1/K`) | pu | - | `> 0` |
| | `governorLeadSeconds`, `governorLagSeconds` | `T2`, `T1` | s | - | `T2 >= 0`, `T1 > 0` |
| | `valveTimeSeconds` | `T3` | s | - | `<= 1e-12` is instant |
| | `chestSeconds`, `reheatSeconds`, `crossoverSeconds`, `lpSeconds` | `T4`, `T5`, `T6`, `T7` | s | - | `<= 1e-12` is instant |
| | `k1`, `k3`, `k5`, `k7` | `K1`, `K3`, `K5`, `K7` | - | `1`, `0`, `0`, `0` | none |
| | `valveMinPu`, `valveMaxPu` | `Pmin`, `Pmax` | pu | `0`, `+inf` | `max >= min` |
| `HydroGovernor` | `permanentDroopPu` | `Rp` (`R`) | pu | - | `> 0` |
| | `transientDroopPu` | `Rt` (`r`) | pu | - | finite, `>= 0` |
| | `resetTimeSeconds` | `Tr` | s | - | `> 0` |
| | `gateTimeSeconds` | `Tg` | s | - | finite; `<= 1e-12` is instant (the gate and transient-droop loop is then solved exactly, `g = (err + Rt xL) / (Rp + Rt)`); a finite `Tg` below `dt / ln(1 + Rt/Rp)` is refused at the step, because the explicit step would ring or diverge |
| | `waterTimeSeconds` | `Tw` | s | - | `> 0` |
| | `gateMinPu`, `gateMaxPu` | `GMIN`, `GMAX` | pu | `0`, `+inf` | `max >= min` |
| `Sexs` | `leadSeconds`, `lagSeconds` | `TA`, `TB` | s | - | `TA >= 0`, `TB > 0` |
| | `gain` | `K` | - | - | `> 0` |
| | `exciterTimeSeconds` | `TE` | s | - | `<= 1e-12` is instant |
| | `fieldMinPu`, `fieldMaxPu` | `EMIN`, `EMAX` | pu | `-inf`, `+inf` | `Emax >= Emin` (`Sexs`, `RmsExciter`) |
| `Ieeet1` | `regulatorGain` | `KA` | - | - | `> 0` |
| | `regulatorTimeSeconds` | `TA` | s | - | `<= 1e-12` is instant |
| | `exciterConstant` | `KE` | - | - | none |
| | `exciterTimeSeconds` | `TE` | s | - | `> 0` |
| | `feedbackGain`, `feedbackTimeSeconds` | `KF`, `TF` | -, s | - | `TF > 0` |
| | `regulatorMinPu`, `regulatorMaxPu` | `VRMIN`, `VRMAX` | pu | `-inf`, `+inf` | `Vrmax >= Vrmin` (`Ieeet1`, `RmsExciter`) |
| | `saturationA`, `saturationB` | `Aex`, `Bex` (fit of `E1, SE(E1), E2, SE(E2)`) | -, 1/pu | `0`, `0` | `Aex >= 0` |
| `Pss1a` | `gain` | `Ks` | - | - | none |
| | `washoutSeconds` | `Tw` | s | - | `> 0` |
| | `lead1Seconds`, `lag1Seconds` | `T1`, `T2` | s | - | `T2 > 0`, or both 0 (bypass) |
| | `lead2Seconds`, `lag2Seconds` | `T3`, `T4` | s | - | `T4 > 0`, or both 0 (bypass) |
| | `outputMinPu`, `outputMaxPu` | `VSMIN`, `VSMAX` | pu | `-inf`, `+inf` | `Vsmax >= Vsmin` |

### Converter plant (`RmsConverterParameters`)

| Name | Symbol | Unit | Default |
|---|---|---|---|
| `Tg` | `Tg` (REGC_A) | s | `0.02` |
| `Tr` | voltage measurement lag | s | `0.02` |
| `Vdip` | `Vdip` | pu | `0.9` |
| `Kqv` | reactive boost gain | - | `2.0` |
| `Imax` | current limit `k` | pu on rating | `Generator.DefaultConverterFaultCurrentPu = 1.2`; the unit's own `EffectiveConverterFaultCurrentPu` in a fleet |
| `LvPnt0`, `LvPnt1` | `Lvpnt0`, `Lvpnt1` | pu | `0.4`, `0.8` |
| `RampUpPuPerSecond` | `rrpwr` | pu/s on rating | `10.0` |
| `HoldSeconds` | dip hold | s | `0.0` |
| `BlockInDip` | LCC blocking | - | `false` (true when `k = 0`) |
| `GridForming` | - | - | `false` |
| `VirtualInertiaH`, `DampingPu` | `Hv`, `Dv` | s, pu on rating | `2.0`, `20.0` |
| `VirtualReactancePu` | `Xv` | pu on rating | `0.15` |
| `VoltageGain` | `Kv` | 1/s | `2.0` |
| `NominalHz` | - | Hz | `50.0` |

### Study, search and network constants

| Constant | Value | Source |
|---|---|---|
| `StudyDefaultStepSeconds` | `0.005` s | `RmsTransientStability` |
| `StudyDefaultDurationSeconds` | `10.0` s | `RmsTransientStability` |
| `CctDefaultStepSeconds` | `0.002` s | `RmsTransientStability` |
| `CctDefaultMaxSeconds` | `1.0` s | `RmsTransientStability` |
| `CctDefaultOnsetSeconds` | `0.1` s | `RmsTransientStability` |
| `PostClearWindowSeconds` | `5.0` s | `RmsTransientStability` |
| `CctDefaultBisectionIterations` | `18` | `RmsTransientStability` |
| `DefaultSlipLimitRad` | `2 pi` rad | `RmsTransientStability` |
| `CctSearch.DefaultScanSteps` | `16` | `CctSearch` |
| `CctSearch.MaxRefinementRounds` | `4` | `CctSearch` |
| `StageTimeToleranceSteps` | `1e-6` steps | `MultiMachineSubtransient` |
| `MaxConverterIterations` | `50` | `MultiMachineSubtransient` |
| `ConverterVoltageTolerancePu` | `1e-10` pu | `MultiMachineSubtransient` |
| `BoltedFaultShuntPu` | `1e7` pu | `SubtransientInit` |
| `SubtransientSaliencyRelativeTolerance` | `1e-9` | `SubtransientInit` |
| `MinVoltageSquaredPu` | `1e-9` pu | `SubtransientInit` |
| `IslandReferenceH`, `IslandReferenceX` | `1e4` s, `1e-3` pu | `SubtransientInit` |
| `RmsEventsExport.MaxEverySteps` | `1000000` steps | `src/GridSim.Core/IO/RmsEventsExport.cs` |
| `Ieeet1.ExciterPoleAtOriginTolerance` | `1e-12` | `Ieeet1` |
| `ControlBlocks.InstantThresholdSeconds` | `1e-12` s | `ControlBlocks` |

## Invariants

Test files are in `tests/GridSim.Tests/`. "To N decimals" is the xUnit `Assert.Equal(expected, actual, N)` precision,
that is `|expected - actual| < 0.5 * 10^-N`; "bit for bit" is `==` on every sample.

**Blocks** (`ControlBlocksTests`)
- A lag step response equals `K*(1 - exp(-t/T))` to 10 decimals at every sample
  (`FirstOrderLag_step_response_matches_the_analytic_exponential_to_epsilon`).
- `T = 0` is a bit-exact gain (`FirstOrderLag_with_zero_time_constant_is_a_bit_exact_gain`); `Ta == Tb` is a bit-exact
  pass-through (`LeadLag_with_equal_time_constants_is_a_bit_exact_passthrough`).
- Lead-lag has unity DC gain and leads (`LeadLag_has_unity_dc_gain`, `LeadLag_with_lead_over_lag_leads_the_input`); the
  washout blocks DC and passes a change (`Washout_blocks_dc_and_decays_to_zero`,
  `Washout_passes_a_change_unattenuated_then_decays`).
- Limits are non-windup: pinned at the limit, then below it on the very next reversed step
  (`FirstOrderLag_non_windup_limit_holds_and_releases`, `Integrator_non_windup_limit_clamps_and_releases`).
- A mixed block chain run twice is bit for bit identical (`Blocks_are_bit_for_bit_deterministic`).

**Control models** (`ControlBlocksTests`, `ControlModelFamiliesTests`, `R083Pss1aStageBypassTests`)
- TGOV1 is stationary to 13 decimals at `dw = 0` and its settled droop matches `-1/R - Dt` to 6 decimals; its valve
  pins at `Vmax` (`Tgov1_is_stationary_at_its_equilibrium`, `Tgov1_steady_droop_matches_the_analytic_gain`,
  `Tgov1_valve_limits_cap_the_response`).
- GAST (`GastGovernorTests`) is stationary to the bit at `dw = 0`; a held speed step follows the hand closed form of its
  two cascaded zero-order-hold lags to 1e-13; on the temperature limit it settles at `AT` for every `KT` and on the valve
  limit at `VMAX`; a start above `AT` or outside the valve limits is refused (`Gast_is_stationary_at_its_equilibrium_to_the_bit`,
  `Gast_speed_step_follows_the_closed_form_of_its_two_cascaded_lags`, `Gast_on_its_temperature_limit_settles_at_AT_whatever_KT`).
- EXAC1 and ESAC1A (`AcExciterTests`): FEX and the saturation fit match hand values; the initial `VE` gives the field
  back on every FEX branch; the linear reduction follows the closed form of its two lags to 1e-13; the full model settles
  on the operating point solved by bisection; a reduced EXAC1 is IEEET1 (`KE = 1`, no saturation) to the bit through a
  whole case 9 study (`A_reduced_exac1_is_ieeet1_in_the_rms_kernel_to_the_bit`,
  `The_full_exac1_is_stationary_and_settles_on_the_hand_solved_operating_point`).
- ESST4B (`Esst4bTests`): VE matches complex arithmetic done by hand; it starts at rest from its first measurements; its
  integrators force `Efd = VRMAX/KG` on the outer limit and `VMMAX*VB` on the inner one; VBMAX caps VB; case 9 stays at
  rest undisturbed (`The_power_source_voltage_matches_complex_arithmetic_done_by_hand`,
  `The_integrators_force_efd_to_vrmax_over_kg_or_to_the_inner_limit`).
- SEXS holds `K*Verr`, clamps at the ceiling and starts at equilibrium (`Sexs_steady_field_voltage_is_gain_times_error`,
  `Sexs_field_limits_clamp_the_ceiling`, `Sexs_initializes_to_equilibrium_and_holds`).
- IEEET1 reduces to a two-lag AVR without saturation or feedback, honours its ceiling, and saturation lowers the field a
  given error sustains (`Ieeet1_reduces_to_a_two_lag_avr_without_saturation_or_feedback`,
  `Ieeet1_ceiling_limits_and_saturation_raises_the_required_regulator_voltage`).
- IEEEG1 is stationary, has droop `-1/R` and reduces to one stage at `K1 = 1`
  (`Ieeeg1_is_stationary_and_has_the_expected_droop`, `Ieeeg1_reduces_to_a_single_stage_when_k1_is_one`).
- The hydro governor is stationary and shows the inverse response (`Hydro_governor_is_stationary_with_permanent_droop`,
  `Hydro_water_column_has_the_non_minimum_phase_inverse_response`).
- PSS1A washes out a sustained input, responds to a change and is limited
  (`Pss1a_washes_out_a_sustained_input`, `Pss1a_responds_then_decays_and_is_deterministic`); a switched-off stage is
  exactly unity and a zero lag with a non-zero lead is refused (`A_switched_off_stage_is_exactly_unity`,
  `Both_stages_off_leaves_the_washed_out_gain`, `A_zero_lag_with_a_non_zero_lead_is_refused`).

**Machines** (`OneAxisMachineTests`, `TwoAxisMachineTests`, `SubtransientMachineTests`, `SaturationFieldIncrementTests`)
- Open-circuit flux decay follows `Efd + (E'q0 - Efd)*exp(-t/T'do)` to 10 decimals at every sample
  (`Open_circuit_field_step_follows_the_exact_flux_decay_exponential`); the q-axis and both subtransient decays do the
  same (`Q_axis_flux_decays_on_the_exact_exponential`, `D_axis_subtransient_flux_decays_on_the_exact_exponential`,
  `Q_axis_subtransient_flux_decays_on_the_exact_exponential`).
- Reduction chain: the two-axis machine with `X'q = Xq` equals the one-axis machine to 10 decimals; the equilibrium, stator
  currents and power of the subtransient machine with `X'' = X'` equal those of the two-axis machine to 12 decimals at
  one operating point (no trajectory is compared)
  (`With_transient_q_reactance_equal_to_synchronous_it_reduces_to_the_one_axis_machine`,
  `With_subtransient_reactances_equal_to_transient_it_reduces_to_the_two_axis_machine`).
- With `Xd = X'd = Xq` and a held field the one-axis swing conserves `H*ws*omega^2 - Pm*delta - Pmax*cos(delta)` to
  `1e-4` and oscillates at the analytic mode within 1 % of the period
  (`Round_rotor_constant_field_swing_conserves_energy`, `Small_signal_oscillation_matches_the_analytic_SMIB_mode`); the
  reluctance power equals its closed form exactly (`Salient_pole_reluctance_power_matches_the_closed_form`).
- Every machine started at its equilibrium is stationary (`Started_in_equilibrium_the_machine_is_stationary` in
  `OneAxisMachineTests`, `TwoAxisMachineTests` and `SubtransientMachineTests`); the one-axis machine also with its AVR
  closed (`Avr_holds_the_machine_stationary_at_its_regulated_setpoint`), including saturated
  (`Saturated_machine_started_in_equilibrium_is_stationary`).
- The saturation curve passes through both fit points to 12 decimals and is zero below the knee
  (`Saturation_curve_fits_its_two_points`); the field increment is `0.10` at 1.0 pu and `0.36` at 1.2 pu for a
  `0.10 / 0.30` curve (`Field_increment_at_the_fit_points_is_the_PSSE_standard`); a saturated equilibrium needs
  exactly the saturation term more field (`Saturation_raises_the_equilibrium_field_by_the_saturation_term`), and the seeded
  fleet carries the same increment as the integrator subtracts
  (`Seeded_field_carries_exactly_the_field_increment`, `A_saturated_fleet_seeded_from_a_solved_case_stays_at_equilibrium`).
- Two identical runs are bit for bit equal (`Two_identical_runs_are_bit_for_bit_equal` in each machine file).

**Network interface and multi-machine** (`SubtransientNetworkInterfaceTests`, `MultiMachineFluxDecayTests`,
`MultiMachineSubtransientTests`, `GovernedMultiMachineTests`)
- With `E''d = 0` the interface equals the round-rotor injection to 12 decimals; powers sum to zero on a lossless
  network; `Pe = E''q*Iq + E''d*Id`; `Vt = |E''|` at `X'' = 0`
  (`With_no_q_axis_emf_it_reduces_to_the_round_rotor_injection`, `On_a_lossless_network_the_powers_sum_to_zero`,
  `Air_gap_power_matches_the_internal_emf_current_product`,
  `Terminal_voltage_with_zero_subtransient_reactance_is_the_internal_emf`).
- `Id = Qe/|E'|` equals an independent phasor current solution to 12 decimals
  (`Network_injection_matches_the_phasor_current_solution`).
- With flat reactances and a held field the six-state fleet reproduces the classical swing over a genuine transient
  (relative angle to 8 decimals, speed to 9) (`Flat_reactances_and_held_field_reduce_to_the_classical_swing`).
- A governed run with no governor is the classical run bit for bit; an islanded governed pair settles within 1 % of
  `dP/sum(1/R + D)` at one common speed (`Governed_swing_with_no_governors_is_bit_for_bit_the_classical_swing`,
  `Islanded_pair_settles_to_the_analytic_droop_speed`).
- A two-stage network schedule reproduces the single fault window bit for bit (`A_two_stage_schedule_reproduces_a_fault_window`).
- A per-machine AVR holds each terminal voltage (`Per_machine_avr_holds_the_terminal_voltages_stationary`).

**Initialisation** (`SubtransientInitTests`, `SubtransientKernelFixTests`)
- A fleet seeded from case9 is stationary: speed below `1e-6`, angle and `E''` unchanged to 5 decimals
  (`Seeded_from_a_solved_case_the_fleet_is_stationary`).
- With flat reactances the seed equals `ClassicalInit` to 9 decimals: the same angle, `|E''| = |E'|`, `E'd = E''d = 0`
  (`Flat_reactances_reduce_to_the_classical_init`).
- Unequal `X''d` and `X''q` fail at initialisation; equal ones initialise (`Unequal_subtransient_reactances_fail_loud_at_initialisation`,
  `Equal_subtransient_reactances_initialise_as_before`).
- A diverged run is never reported as in synchronism
  (`ConvergenceContractStudyTests.A_diverged_subtransient_run_is_never_in_synchronism`); the refusal of a non-converged
  power flow is the shared `RequireConverged` helper pinned in `ConvergenceContractTests`.

**Study and search** (`RmsTransientStabilityTests`, `RmsCctStepQuantisedTests`, `CctFirstLossSearchTests`,
`RmsExciterStudyTests`, `StabilityAvrFlagContractTests`, `SubtransientKernelFixTests`)
- Reference comparison: with every reactance equal to `X'd` the RMS critical clearing time at bus 7 of case9 agrees with
  the classical one within `0.02` s, and a clearing at half the CCT survives while one at twice it does not
  (`Subtransient_critical_clearing_time_reduces_to_the_classical`, `A_short_fault_survives_and_a_long_fault_does_not`).
- The case9 bus 7 CCT is exactly 302 steps, the reported CCT survives a direct study and one step later does not, and
  dense and sparse networks return an equal `CctBracket` (`Case9_bus7_cct_is_exactly_302_steps`,
  `The_cct_survives_in_a_direct_study_and_one_step_later_does_not`, `Dense_and_sparse_give_the_same_bracket_bit_for_bit`).
- The classical case9 CCT is the first loss of an exhaustive scan, pockets are flagged, and the RMS bus 7 search is
  monotone (`Classical_case9_cct_is_the_first_loss_of_an_exhaustive_scan_and_pockets_are_flagged`,
  `Rms_case9_bus7_is_monotone_and_unchanged`); the search fails loud on meaningless arguments
  (`The_search_fails_loud_on_meaningless_arguments`, `A_window_shorter_than_a_step_or_a_bad_step_fails_loud`).
- A clearing time one rounding unit high clears on its step; a stage between grid points waits
  (`A_clearing_time_rounded_one_ulp_high_still_clears_on_its_step`,
  `A_stage_genuinely_between_grid_points_still_waits_for_the_next_step`).
- Loss of synchronism is judged on the centre-of-inertia angle (`A_spread_between_2pi_and_4pi_is_in_synchronism_for_both_kernels`,
  `A_spread_beyond_4pi_is_lost_for_both_kernels`).
- With an all-null exciter list the study is bit for bit the held-field study
  (`No_exciters_reproduce_the_held_field_study_bit_for_bit`); the CCT test compares an omitted list with `null`, the
  same code path, so the all-null list on the CCT search is not pinned
  (`No_exciters_reproduce_the_held_field_cct_bit_for_bit`); SEXS, IEEET1 and SEXS plus PSS start at equilibrium; after a
  fault the AVR raises `E'q` above the held run; a mis-sized list, a shared exciter or PSS, a fault at `t = 0` with
  automatic references, an initial field outside the limits and inverted limits all throw (`RmsExciterStudyTests`).
- The CLI source contains the `--avr` validation and passes the exciters to every RMS call; these are source-text
  checks of `Program.cs` that run nothing (`Avr_flag_is_validated_and_none_passes_no_exciters`,
  `Every_rms_study_call_receives_the_exciters`).

**Networks and converters** (`RmsSparseNetworkTests`, `SubtransientSolveIntoBitIdentityTests`,
`MachineAdmittanceSimdBitIdentityTests`, `RmsConverterTests`, `RmsGridFormingTests`)
- The sparse production network agrees with the dense Kron oracle within `1e-6` rad on every step and the CCT within
  `0.005` s (`Sparse_network_run_matches_the_dense_kron_run`, `Sparse_critical_clearing_time_matches_dense`); the two
  are different eliminations of one linear network and are not bit-identical, which is why production uses only the
  sparse one.
- Event studies: a fault and its clearance reproduce the single-fault study bit for bit; the same events give the same
  bits twice; branch trips, load steps and generator trips move the fleet as expected; bad events are refused by name
  (`Cf4RmsEventTests`). The export carries the header, provenance and every sample exactly, lists the events in time
  order, and decimates exactly (`Cf6RmsEventsExportTests`).
- The allocation-free solve is bit for bit the allocating solve, and every vector tier of the dense product equals the
  scalar product bit for bit (`Trajectories_are_bit_identical_to_the_allocating_kernel`,
  `Dense_and_sparse_SolveInto_equal_their_Solve_bit_for_bit`, `Every_tier_matches_the_scalar_product_bit_for_bit`,
  `Dense_rms_runs_are_bit_identical_to_the_scalar_kernel`).
- A converter leaves the machine fleet; an undisturbed run is stationary; a fault at its bus rides through on the
  current limit; recovery is ramp-limited; an LCC link blocks and ramps back; opting out is the all-machine fleet
  exactly; held and zero-inertia units keep the run stationary; an island without a machine gets a reference; a
  converter-only fleet is refused (`RmsConverterTests`).
- A grid-forming unit is stationary at flat start, holds its current limit in a fault, holds its terminal voltage better
  than a grid-following unit at onset, and its virtual speed swings and is damped (`RmsGridFormingTests`).

**Extended library** (`Rc5Wp3aDynamicLibraryTests`)
- Flat start: each exciter alone holds its field to 1e-14 relative over 4000 steps, and each stabiliser stays within
  1e-13 of 0; on case 9 every library fleet (ESST1A + PSS2A, EXST1 + IEEEST on power, SCRX bus fed + IEEEST on speed,
  SCRX solid fed, ESDC1A + PSS2B, SEXS + PSS2A, ESAC1A + IEEEST) keeps every speed and E'q within the rounding bound
  `2 eps n 12` (2.7e-12 for the 1000 steps; two roundings per step on at most 12 pu of field-path state) of the held-field
  run for 5 s, and a fault moves the field; the case 14 DYR fleet (every DYR-mapped new model) stays within the same bound of
  its held-field run and rides through a fault (`A_library_fleet_holds_case9_at_rest_to_rounding_and_a_fault_moves_it`,
  `The_case14_dyr_fleet_starts_at_rest_and_rides_through_a_fault`).
- GENCLS: at rest to 1e-12 in speed, and through a fault every flux state is its initial value bit for bit
  (`A_gencls_fleet_starts_at_rest_and_holds_its_flux_states_bit_for_bit_through_a_fault`). GENTPJ with Kis above 0 starts at
  rest and needs more field than Kis = 0 (`A_gentpj_with_kis_starts_at_rest`).
- Step responses against `tests/tooling/dynamic-library-reference.py` (SciPy 1.18.1: the exact LTI step response by the
  matrix exponential, and solve_ivp Radau at rtol 1e-12 for the rate-feedback loops) for EXST1, ESDC1A, ESST1A, IEEEST on
  speed and PSS2A on each input: the worst error at seven sample times is below 1 % of the peak at a 0.1 ms step and falls
  with the step (first-order convergence of the chained zero-order holds). EXST1 with `TA = 0` is the lead-lag's closed
  form within its rounding bound (`KA 2 eps k |xL|` after k steps).
- Limits at their exact levels: ESST1A `VAMAX` (non-windup, released on the first reversed step), `Vt VRMAX - KC IFD`,
  `Vt VRMIN`, the field current limiter and VOS 2; the EXST1 input limiter; ESDC1A `VRMAX` (non-windup) with `Efd` settling
  at `VRMAX/KE` and, saturated, at the root of `VRMAX = KE x + B (x - A)^2` solved independently; SCRX `EMAX Vt`, the
  crowbar and negative current capability; IEEEST `LSMAX`/`LSMIN` and the `VCU`/`VCL` cut-offs; PSS2A `VSTMAX` and a washed-out
  speed step; PSS2B with `VS1MAX` is PSS2A on the clipped input bit for bit.
- GENTPJ with Kis = 0 is GENROU bit for bit: the increments for the same saturation inputs, the loader's machine data and a
  saturated case 9 run (`Gentpj_increments_with_kis_zero_are_genrou_bit_for_bit_for_the_same_saturation_inputs`,
  `A_gentpj_with_kis_zero_loads_and_runs_as_genrou_bit_for_bit`).
- DYR: every record maps value by value in its PSS/E order and the records re-read from their text give the same document
  bytes; the default reader still refuses the new models; each refusal names its record
  (`Every_extended_dyr_record_maps_value_by_value_in_its_psse_order`, `The_dyr_records_round_trip_through_text_to_the_same_document`).
- Determinism: dense and sparse power flows, repeated and parallel runs, and the built CLI in its three SIMD modes give the
  same bits (`The_cli_dyr_extended_run_is_the_same_bits_in_every_simd_mode`).

**More controllers** (`Rc5Wp19ControllerLibraryTests`, `Rc5Wp19BitIdentityTests`)
- Flat start: each new exciter alone (EXDC2, IEEEX1, ESAC8B with and without `KC`/`KDexc`, ESST3A) holds its field within
  `2 eps n 12` on every step for 400 steps; PSS2C alone outputs exactly 0 and IEEEG3 alone holds its gate exactly and its
  power within the same bound; on case 9 each fleet (EXDC2 + PSS2C, IEEEX1, ESAC8B + PSS2C with its fourth stage, ESAC8B
  with `KC`/`KDexc`, ESST3A + PSS2C with output logic, and an IEEEG3 governor fleet) stays within the bound of the held run for
  2 s and a fault moves it; the case 14 DYR fleet of the mapped records starts at rest and rides through a fault.
- Closed forms, on every step: EXDC2 settles at `0.5 VRMAX` exactly with `Vt = 0.5` and IEEEX1 at `VRMAX`; ESAC8B's two-lag
  response stays within the derived bound `(dt/TA) KA du` (1 % of the change at 1 ms) of
  `0.1 [1 - (TA e^(-t/TA) - TE e^(-t/TE))/(TA - TE)]` (0.05418351852854685 at 0.5 s) and within 1e-12 of the same
  zero-order-hold chain run by `scipy.signal.dlsim`; the ESAC8B integral is the ramp `0.02 t` within `n eps (1e-4 + 0.01)`;
  the ESST3A source voltage is 1.3078226179417451 to 1e-14; the IEEEG3 turbine gives `1 - 3 e^(-2t/TW)` at every sample
  (0.5939941502901619 at 1 s).
- Step responses against `tests/tooling/dynamic-library-reference.py` cases 15 to 19 (ESAC8B with the full PID, ESST3A,
  PSS2C with its fourth stage, IEEEG3 closed loop): below 1 % of the peak change at 0.1 ms, falling with the step.
- Reductions: PSS2C with `T12 = T13 = 0` and no logic is PSS2B bit for bit (directly and through the file route); IEEEX1 is
  ESDC1A bit for bit. The PSS2C output logic switches on and off at its thresholds on every step.
- DYR: each record maps value by value; a wrong count, `KE = 0`, `VRMAX = 0` and `SWITCH` are refused; PSS2C, ESAC7B,
  HYGOV4 and GENQEC are refused by name with their reasons. Bit identity: the default reader's message for all nine model
  names, the default and extended CLI runs of the earlier models, and the extended document of the WP3a file are hashed
  from the 2.1.116 build and unchanged.

## Worked example

One case9 machine is initialised by hand, then compared with a run of the library.

**Inputs.** The solved case9 power flow (Newton, 4 iterations) puts bus 1, the slack, at `Vt = 1.04 pu` angle
`0` and net injection `P = 71.6410214744825 MW`, `Q = 27.0459235334931 MVAr` on the 100 MVA base. The generator at bus 1
has `MBase = 247.5` MVA and `H = 23.64` s, so `H_sys = 23.64*247.5/100 = 58.509` s. The CLI data for the machine uses
`X'd = 0.0608` pu on its own base, so the base factor is `100/247.5 = 0.404040` and, on the system base:

```
Xd   = 3.0*0.0608*0.404040 = 0.0736970       Xq   = 2.5*0.0608*0.404040 = 0.0614141
X'd  = 0.0608*0.404040     = 0.0245657       X'q  = 1.5*0.0608*0.404040 = 0.0368485
X''d = X''q = 0.8*0.0608*0.404040 = 0.0196525
```

**Terminal current.** `S = 0.716410 + j0.270459` pu, so `It = conj(S/Vt) = 0.688856 - j0.260057` pu
(`|It| = 0.736310`).

**Rotor angle.** `Eq = Vt + j*Xq*It = 1.04 + j0.0614141*(0.688856 - j0.260057) = 1.055971 + j0.042305`, so
`delta0 = atan2(0.042305, 1.055971) = 0.0400417 rad = 2.29422 deg`.

**dq quantities.** With `cos(delta0) = 0.999199`, `sin(delta0) = 0.040031`:

```
Vq = 1.039166    Vd = 0.041632    Iq = 0.677893    Id = 0.287424
E'q  = 1.039166 + 0.0245657*0.287424 = 1.046227
E'd  = 0.041632 - 0.0368485*0.677893 = 0.016653
E''q = 1.039166 + 0.0196525*0.287424 = 1.044815
E''d = 0.041632 - 0.0196525*0.677893 = 0.028310
Efd  = 1.046227 + (0.0736970 - 0.0245657)*0.287424 = 1.060349
Pm   = 0.041632*0.287424 + 1.039166*0.677893 = 0.716410
```

`Pm` equals the solved bus-1 active power: the machine is lossless in the stator (`Rs = 0`), so air-gap power is
terminal power. The four flux derivatives evaluate to `-3.1e-17`, `2.0e-16`, `-6.9e-18` and `0` by the equations of the
model, a fixed point to double precision (the hand arithmetic above uses six digits; the checks used the full values).

**The library run.** `docs/_captures/dynlib/init-case9.txt` seeds the fleet with `SubtransientInit.FromSolvedCase`
from the same solve and the same CLI machine data, runs it undisturbed for 2 s on the prepared network at the case frequency, collapses the reactances to `X'd`, and
runs the critical-clearing-time search at bus 7. The script is
`docs/_captures/dynlib/init-case9.fsx` (it calls the library directly, so its first line is `dotnet fsi`, not
`gridsim`).

```
$ dotnet fsi docs/_captures/dynlib/init-case9.fsx
power flow converged=true iterations=4
bus 1 solved: Vm=1.04 VaDeg=0 P=71.6410214744825 MW Q=27.0459235334931 MVAr
machine 1 (bus 1): H=58.509 s on 100 MVA
  delta0=0.0400417046366223 rad (2.29422068018788 deg)
  E'q=1.04622713736649  E'd=0.0166528982585501  E''q=1.04481498500788  E''d=0.0283099270395352
  Efd=1.06034866095259  Pm=0.716410214744825
undisturbed 2 s: synchronism=true max|speed|=4.250e-017 pu max|delta-delta0|=3.886e-015 rad
flat reactances, machine 1: delta0=0.9265 deg |E|=1.0465 pu
bus 7 CCT: stable to 554 ms, lost at 556 ms (39 trials)
[exit 0]
```

Reading it against the hand calculation:

- `delta0`, `E'q`, `E'd`, `E''q`, `E''d`, `Efd` and `Pm` agree with the six-digit hand values to the digits shown, and
  a full-precision evaluation of the same equations outside the library (Python, double precision) reproduces all
  seven to 15 significant figures.
- Undisturbed for 2 s the largest speed deviation is `4.3e-17` pu and the largest angle excursion `3.9e-15` rad:
  the seed is a fixed point, as step 7 of the initialisation claims.
- With every reactance equal to `X'd`, machine 1 starts at `0.9265` deg with `|E| = 1.0465` pu. The classical
  verb prints the same machine as `0.9` deg and `1.047` pu in `docs/_captures/stability/stability-case9.txt`
  (its columns are rounded to one decimal and three decimals).
- The library capture prints a bus 7 critical clearing time of 554 ms with 556 ms lost (277 steps of 2 ms, 39
  trials). The script's call, `RmsTransientStability.CriticalClearingBracket(model, pf, data, 7)`, passes no `f0`, so
  the library takes `GridModel.FrequencyHz`, which is 60 Hz for `case9` (`IeeeCases.Wscc9NominalFrequencyHz`); the
  bracket is the one `Fb1FinalBatchFollowUpsTests.Pin_case9_at_60_hz_rms_cct` pins. On `case9` re-set to 50 Hz the same
  search gives 302 steps, 604 ms (`RmsCctStepQuantisedTests.Case9_bus7_cct_is_exactly_302_steps`). The CLI makes the
  same call, and `docs/_captures/stability/stability-case9-rms.txt` prints 554 ms for
  `gridsim stability case9 --rms`:

```
$ gridsim stability case9 --rms
...
fault bus 7  *  critical clearing time = 554 ms
  (bracket: clearing at 554 ms holds synchronism, at 556 ms it is lost)
...
```

The library search ran 39 distinct simulations (`Trials`), the bisection plus the memoised downward and upward scans.
Timings in captured output differ by machine and run; the single-study capture
`docs/_captures/stability/stability-case9-rms-clear.txt` shows `0.1 s` for one 400 ms fault-and-clear.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Dynamics/Models/ControlBlocks.cs` | `FirstOrderLag`, `LeadLag`, `Washout`, `Integrator`, `PiController` | Exact-ZOH blocks, limits, initialisation |
| `src/GridSim.Core/Dynamics/Models/Tgov1.cs` | `Tgov1` | TGOV1 governor |
| `src/GridSim.Core/Dynamics/Models/Ieeeg1.cs` | `Ieeeg1` | IEEEG1 governor |
| `src/GridSim.Core/Dynamics/Models/HydroGovernor.cs` | `HydroGovernor` | Transient-droop hydro governor |
| `src/GridSim.Core/Dynamics/Models/Gast.cs` | `Gast` | GAST gas-turbine governor |
| `src/GridSim.Core/Dynamics/Models/Ggov1.cs` | `Ggov1`, `Ggov1Data`, `IRmsElectricalPowerGovernor` | GGOV1 governor, the rate-limited lag |
| `src/GridSim.Core/Dynamics/Models/Wsieg1.cs` | `Wsieg1`, `Wsieg1Data` | WSIEG1 governor |
| `src/GridSim.Core/Dynamics/Models/Iec61400/Iec61400Data.cs` | `Iec61400Data` and its module records, `Iec61400Table` | IEC 61400-27-1 style Type 4A/4B data, checks, source basis |
| `src/GridSim.Core/Dynamics/Models/Iec61400/Iec61400Blocks.cs` | generator system, P and Q control, current limiter, drive train, PLL, protection | The discrete modules |
| `src/GridSim.Core/Dynamics/Models/Iec61400/Iec61400State.cs` | `Iec61400State` | The unit as the RMS kernel drives it |
| `src/GridSim.Core/Analysis/DynamicsStudy/Iec61400StudyFile.cs` | `Iec61400StudyFile` | The `dynamics-study` `iec61400` member |
| `src/GridSim.Core/Dynamics/Models/DynamicLoads.cs` | `DynamicLoadData`, `RmsLoadedMachineData` | Dynamic loads in the RMS study |
| `src/GridSim.Core/Dynamics/Models/InductionMotorLoad.cs` | `InductionMotorParameters`, `InductionMotorLoadData`, `InductionMotorTorque` | Third-order motor load |
| `src/GridSim.Core/Dynamics/Models/ZipRecoveryLoad.cs` | `ZipRecoveryLoadData`, `StaticLoadCharacteristic` | ZIP with exponential recovery |
| `src/GridSim.Core/Dynamics/Models/CompositeLoad.cs` | `CompositeLoadData` | CMLD subset |
| `src/GridSim.Core/Dynamics/Models/CompositeLoadExtensions.cs` | `CompositeLoadExtensions`, `CompositeLoadMotor`, `MotorUnderVoltageRelay` | CMLD motors B and C, relays, the extended network |
| `src/GridSim.Core/Dynamics/Models/AcMotorPerformance.cs` | `AcMotorParameters` | CMLD motor D (WECC air-conditioner performance model) |
| `src/GridSim.Core/Dynamics/Models/ElectronicLoad.cs` | `ElectronicLoadParameters` | CMLD electronic load |
| `src/GridSim.Core/Dynamics/Models/CompositeLoadLtc.cs` | `CompositeLoadLtcParameters` | CMLD substation tap changer |
| `src/GridSim.Core/Dynamics/Models/AcExciter.cs` | `AcExciter` | EXAC1 and ESAC1A alternator-rectifier exciters |
| `src/GridSim.Core/Dynamics/Models/Esst4b.cs` | `Esst4b` | ESST4B static exciter |
| `src/GridSim.Core/Dynamics/Models/Sexs.cs` | `Sexs` | SEXS exciter |
| `src/GridSim.Core/Dynamics/Models/Ieeet1.cs` | `Ieeet1` | IEEET1 exciter |
| `src/GridSim.Core/Dynamics/Models/Pss1a.cs` | `Pss1a` | PSS1A stabiliser |
| `src/GridSim.Core/Dynamics/Models/LibraryControllers.cs` | `IRmsExciterModel`, `IRmsStabiliser`, `FieldControlInputs` | The library exciter and stabiliser interfaces, the voltage transducer |
| `src/GridSim.Core/Dynamics/Models/St1Exciter.cs` | `St1Exciter` | EXST1 and ESST1A exciters |
| `src/GridSim.Core/Dynamics/Models/Scrx.cs` | `Scrx` | SCRX exciter |
| `src/GridSim.Core/Dynamics/Models/Esdc1a.cs` | `Esdc1a` | ESDC1A exciter |
| `src/GridSim.Core/Dynamics/Models/Exdc2.cs` | `Exdc2` | EXDC2 and IEEEX1 exciters |
| `src/GridSim.Core/Dynamics/Models/Esac8b.cs` | `Esac8b` | ESAC8B exciter |
| `src/GridSim.Core/Dynamics/Models/Esst3a.cs` | `Esst3a` | ESST3A exciter |
| `src/GridSim.Core/Dynamics/Models/Pss2c.cs` | `Pss2c` | PSS2C stabiliser |
| `src/GridSim.Core/Dynamics/Models/Ieeeg3.cs` | `Ieeeg3`, `Ieeeg3Data` | IEEEG3 hydro governor |
| `src/GridSim.Core/Dynamics/Models/Ieeest.cs` | `Ieeest` | IEEEST stabiliser |
| `src/GridSim.Core/Dynamics/Models/Pss2.cs` | `Pss2` | PSS2A and PSS2B stabilisers |
| `src/GridSim.Core/Dynamics/Models/Gencls.cs` | `Gencls` | GENCLS machine data |
| `tests/tooling/dynamic-library-reference.py` | script | SciPy reference step responses |
| `src/GridSim.Core/Dynamics/Models/OneAxisMachine.cs` | `OneAxisMachine` | One-axis SMIB machine |
| `src/GridSim.Core/Dynamics/Models/TwoAxisMachine.cs` | `TwoAxisMachine` | Two-axis SMIB machine |
| `src/GridSim.Core/Dynamics/Models/SubtransientMachine.cs` | `SubtransientMachine`, `SaturationCurve` | Six-state SMIB machine, saturation |
| `src/GridSim.Core/Dynamics/Models/MultiMachineFluxDecay.cs` | `MultiMachineFluxDecay` | Round-rotor multi-machine `E'q` model |
| `src/GridSim.Core/Dynamics/Models/GovernedMultiMachine.cs` | `GovernedMultiMachine` | TGOV1 on the classical swing |
| `src/GridSim.Core/Dynamics/Models/SubtransientNetworkInterface.cs` | `SubtransientNetworkInterface` | dq EMF to network current, `Pe`, `Vt` |
| `src/GridSim.Core/Dynamics/Models/MultiMachineSubtransient.cs` | `MultiMachineSubtransient`, `IRmsFieldController` | RMS integrator |
| `src/GridSim.Core/Dynamics/Models/SubtransientInit.cs` | `SubtransientInit` | Fleet seed, networks, held units, island references |
| `src/GridSim.Core/Dynamics/Models/RmsNetwork.cs` | `IRmsNetwork`, `DenseMachineNetwork`, `SparseRmsNetwork` | Network solves, stages |
| `src/GridSim.Core/Dynamics/Models/MachineAdmittanceColumns.cs` | `MachineAdmittanceColumns` | Bit-identical vectorised `Y*E` |
| `src/GridSim.Core/Dynamics/Models/RmsConverter.cs` | `RmsConverterParameters`, `RmsConverterState`, `ConverterNetwork` | Converter plant and its network coupling |
| `src/GridSim.Core/Dynamics/Models/RmsTransientStability.cs` | `RmsTransientStability`, `RmsExciter`, `CctBracket` | The study, exciter mapping, clearing-time bracket |
| `src/GridSim.Core/Dynamics/Models/IRmsConverterModel.cs` | `IRmsConverterModel`, `IRmsNetworkSolvedConverterModel`, `LegacyRmsConverterModel` | The kernel's converter interface |
| `src/GridSim.Core/Dynamics/Models/RmsNetworkSolvedIteration.cs` | `RmsNetworkSolvedIteration` | Exact network-interface pass for REGC_B units |
| `src/GridSim.Core/Dynamics/Models/Wecc/Regca.cs`, `Regcb.cs` | `Regca`, `Regcb` | REGC_A, REGC_B (and its exact network solution) |
| `src/GridSim.Core/Dynamics/Models/Wecc/Reec.cs`, `Reecd.cs` | `Reec`, `Reecd` | REEC_A/B/C, REEC_D |
| `src/GridSim.Core/Dynamics/Models/Wecc/Repca.cs` | `Repca`, `RepcaParameters` | REPC_A |
| `src/GridSim.Core/Dynamics/Models/Wecc/WindMechanics.cs` | `DriveTrain`, `Aerodynamics`, `PitchController`, `TorqueController` | WTGT_A, WTGAR_A, WTGPT_A, WTGTRQ_A |
| `src/GridSim.Core/Dynamics/Models/Wecc/DerA.cs`, `RegfmA1.cs` | `DerAState`, `RegfmA1State` | DER_A, REGFM_A1 |
| `src/GridSim.Core/Dynamics/Models/Wecc/WeccConverterState.cs` | `WeccConverterState` | The composed REGC/REEC/REPC/wind unit |
| `src/GridSim.Core/IO/Psse/PsseDyrRenewables.cs` | `PsseDyrRenewables` | DYR records of the renewable models |
| `src/GridSim.Core/Dynamics/Models/CctSearch.cs` | `CctSearch` | Integer bisection, verification, probe |
| `src/GridSim.Core/Dynamics/Distributed/ClassicalMultiMachine.cs` | `ClassicalMultiMachine` | Classical swing and its hooks |
| `src/GridSim.Core/Dynamics/Distributed/ClassicalInit.cs` | `ClassicalInit` | Classical seed and CCT |
| `src/GridSim.Cli/Program.cs` | `RunRmsStability` | The `stability --rms --avr --pss` verb |
| `src/GridSim.Cli/CliDefaults.cs` | `CliDefaults` | Synthesised machine ratios, step and duration |
| `docs/_captures/dynlib/init-case9.fsx` | script | Worked-example run |

## Limits

- Plug-in results are outside the verified contract: the route is verified (bit identity of re-expressed coded models),
  not a plug-in's maths. The lint and replay are a determinism check, not a sandbox; a plug-in runs with the user's rights.
  The PSS/E USRMDL/CONEC/CONET interface, PowerFactory DSL import and the IEEE/Cigre real-code DLL interface are not
  provided. A user machine is a contract 2 plug-in only (there is no declarative machine role), and it takes the injection
  route, not the built-in machines' integration. `dynamics-study` binds governor and exciter models, injection, machine and
  load models, and relays. `modal-full` takes declarative models only, and an `expr` block is not linearised there.

- The subtransient machine follows the GENROU saturation form, but no PSS/E trace of a saturated machine is compared
  against, so agreement with a PSS/E GENROU run on the same data is not demonstrated.
- The leakage reactance `Xl` enters only the q-axis saturation scale. The machines have no stator resistance and no
  stator transients; the network is positive-sequence and algebraic.
- The fleet needs `X''d = X''q`; `SubtransientInit` throws when they differ.
- Governors come with the machine data: `RmsTransientStability` builds the mechanical-power hook from each machine's
  `SubtransientInit.MachineData.Governor` (TGOV1, IEEEG1, the hydro governor or GAST), and the CLI reads it from a machine
  template's `governor` slot in `--dyn-data`. A machine with no governor, and every machine of a run without
  `--dyn-data`, holds its seeded `Pm`. `Tgov1` also runs on the classical swing through `GovernedMultiMachine`.
- IEEEG1 has the valve rate limits `Uo` and `Uc` and the low-pressure shaft fractions `K2`, `K4`, `K6`, `K8` (defaults:
  unlimited rates, `K1 = 1` and the other fractions 0); the hydro governor omits the velocity limit, the filter `Tf`,
  `At`, `Dturb` and `qNL`.
- PSS1A omits the input transducer `T6` and the second-order low-pass filter `A1` and `A2`, and takes speed deviation as its only
  input; IEEET1 omits the transducer `TR` and takes its saturation as `Aex*exp(Bex*Efd)`, which the caller fits
  to `E1, SE(E1), E2, SE(E2)`.
- The exciters are SEXS, IEEET1, EXAC1, ESAC1A, ESST4B, EXST1, ESST1A, SCRX, ESDC1A, EXDC2, IEEEX1, ESAC8B and ESST3A, and
  the stabilisers PSS1A, IEEEST, PSS2A, PSS2B and PSS2C; there is no over- or under-excitation limiter (the ESST1A UEL
  input is not driven, and the UEL/OEL inputs of ESAC8B, ESST3A and PSS2C are not modelled). Not modelled: EXAC2 to
  EXAC4, EXST2, EXST3, ESAC2A to ESAC7B, ESST2A, ESST5B to ESST7B, ESDC2A, IEEET2 to IEEET5, and other PSS/E exciters.
- ESAC8B omits the 2016 PID output limits, VFEMAX and VEMIN. PSS2C reads rotor speed and electrical power only.
- The governors are TGOV1, IEEEG1, the hydro governor, GAST, GGOV1, WSIEG1 and IEEEG3. IEEEG3 has no deadband. HYGOV4,
  URGS3T, WEHGOV, WPIDHY and the other PSS/E hydro governors are not modelled. GGOV1 has no transport delay `Teng`, no
  speed deadband and no negative `Dm`; WSIEG1 has no deadbands and no cross-compound second machine. Both are verified
  against SciPy integrations of their block diagrams and GGOV1's droop against its analytical steady state, not against a
  PSS/E run of the same data.
- The dynamic loads are verified against their equations (the motor's circuit, stall voltage and SciPy integration, the
  recovery load's closed form, the CMLD subset's SciPy integration), not against a PSS/E or PowerFactory run. The CMLD
  subset is not the WECC composite load: see [Dynamic loads](#dynamic-loads) for what it leaves out. The CMLD extensions
  are verified against the WECC specification's equations (closed forms of motor D's curves, the electronic load's
  pseudocode and every timer), bit-identity of motors B and C with motor A, and a SciPy integration of the extended model,
  not against a PSS/E or PSLF CMLD run. They have no distributed generation and no DYR import. Dynamic loads reach
  the RMS engine only; the small-signal and adaptive-step studies refuse them or do not see them.
- The extended library is verified against its block diagrams: SciPy reference integrations, closed forms and exact limit
  levels. It is not compared with a PSS/E or PowerFactory run of the same data, and the DYR record orders follow the PSS/E
  model documentation, not a vendor-written file.
- GENTPJ applies saturation in GENROU's additive form with `Kis |It|` added to the flux; PSS/E's GENTPJ scales the
  reactances instead, so only an unsaturated GENTPJ is the PSS/E model. The PSS/E GENTPJ record order is not public, so
  GENTPJ comes from a `gridsim-dynamics/2` file only. GENCLS has no `H = 0` infinite bus.
- IEEEST has no second-order filter terms (`A2`, `A4`, `A6`); IEEEST, PSS2A and PSS2B read rotor speed and electrical
  power only (no bus frequency, accelerating power or voltage input, no remote bus).
- Events are bolted three-phase faults, branch trips and recloses, load steps and generator trips; there is no
  unbalanced fault, fault impedance or breaker timing model.
- Without `--dyn-data` the CLI synthesises every machine parameter from `X'd`, and it fixes the exciter and PSS
  settings, so its results are indicative; the case files carry no machine dynamics. A converter's energy store is an API option that
  `SubtransientInit` does not attach, so `stability --rms` runs every converter without one.
- The integrator is fixed-step with first-order operator splitting. A step schedule changes the step between windows; the
  error-controlled step (EXPERIMENTAL) chooses the windows by re-running the whole simulation (no step is taken back), so
  its error figure is a first-order step-doubling estimate, not a bound, and a run can take up to 24 full simulations.
- WECC models against ANDES 2.0.0 (REGCA1 + REECA1, an infinite bus behind two lines, a 100 ms fault, 1 ms steps): at rest
  the two agree to 1.3e-10 pu in V and 5.4e-11 in P; during the fault to 1.9e-3 in V, 8.9e-4 in P, 3.7e-3 in Q (ANDES
  solves states and network together by the implicit trapezoid, GridSim advances the states from the start-of-step
  network); settled, to 2.7e-8 in P and Q (V 8.6e-6: GridSim's infinite bus is a 1,000,000 MVA machine). After clearing,
  P differs by up to 0.184 pu for about 40 ms because ANDES divides REEC_A's Pord by the raw terminal voltage, where the
  EPRI figure, OpenIPSL and GridSim use Vt_filt; with Trv = 0 that difference falls to 2.4e-2 at the first step after
  clearing and 3.6e-6 from 1.3 s. No published REGC_B, REEC_D, REPC_A, wind, DER_A or REGFM_A1 benchmark with numbers was
  obtainable (the EPRI REGC_B/REEC_D protocol lists tests without results), so those models are checked against their
  specifications' own equations and steady states only.
- RMS step cost per WECC unit (IEEE 118, 400 steps of 5 ms with a 100 ms fault, Debug build, this development laptop):
  REGC_B + REEC_D about 140 us per unit per step (the exact network solution with its Newton step for several units);
  REGC_A + REEC_A 44 to 72 us per unit per step with 8 or 16 units, most of it in the steps the rescue re-solves.

## See also

- [Ch. 30, Small-signal and impedance stability](<0030 Small-signal and impedance stability.md>) - the classical
  swing, its Kron reduction, the modal analysis and the impedance scan built from the same preparation.
- [Ch. 15, RMS, modal and impedance stability](<../manual/0015 RMS, modal and impedance stability.md>) - the task
  view of `gridsim stability --rms`.
- [CLI reference, `stability`](<../manual/0019 CLI reference.md#stability>) - the flags of the verb.
- [Ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>) - the centre-of-inertia swing and the inline lags the
  block library formalises.
- [Ch. 11, Validation](<0011 Validation.md>) - the machine-epsilon standard, and the SciPy cross-check of the
  classical swing.
- [Ch. 23, Contingency and security analysis](<0023 Contingency and security analysis (N-k).md>) - the same
  reduction-identity discipline.
