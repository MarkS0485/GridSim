# 24. Standard dynamic-model library

This chapter specifies the dynamic-model library under `src/GridSim.Core/Dynamics/Models/` and the study built on
it: the block primitives, the synchronous-machine models (classical, one-axis, two-axis and the six-state
subtransient machine with magnetic saturation), the exciters (SEXS, IEEET1), the stabiliser (PSS1A), the
turbine-governors (TGOV1, IEEEG1 and a hydro governor), the converter-plant models the RMS engine carries beside
the machines, the initialisation of a fleet from a solved power flow (`SubtransientInit`), and the RMS
transient-stability integrator (`MultiMachineSubtransient`, `RmsTransientStability`) with its critical-clearing-time
search. It gives the equations as coded, every parameter with its default, unit and source symbol, the invariants
and the tests that pin them, and one machine initialised by hand against a captured run.

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

### Turbine-governors

All four map speed deviation `dw` (pu) to mechanical power `Pmech` (pu on the machine base). `Initialize(Pm0)` sets
the reference and every state so that `dw = 0` holds `Pm0` exactly.

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
`GovernedMultiMachine` hosts `Tgov1` on the classical swing through the `mechanicalPower` hook, one optional governor
per machine. It initialises each governor at the machine's initial `Pm` and then applies a supplied `mechanicalReference`
with `SetReference` (R246), so a set-point step travels through the valve and reheat lags; it passes no hook at all
when no governor is present, so a governed run with no governor is the classical run bit for bit. An islanded pair
with a sustained reference step `dP` settles to `omega_ss = dP / sum_i(1/R_i + Dt_i + D_i)`, where `Dt_i` is the governor's turbine damping and `D_i` the
swing damping (the test uses `Dt = 0`). TGOV1 data is on the machine's own base: pass `machineToSystemBase` (MBase / system
base, one per machine, GR-560) and each governor is initialised at `Pm / k`, referenced at `reference / k` and its output
scaled back by `k`, so a machine-base droop `R` acts as `k / R` on the system base; without it the governor reads the
system-base powers directly (exact only for a machine rated at the system base).

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
         the PI integrals, the Tiq lag and Pord freeze while Voltage_dip = 1
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

The step is fixed (no error control). The frozen-input splitting of steps 5 and 8 is designed to be first order in the
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

`RmsTransientStability.CriticalClearingBracketEvents(model, result, data, faultBusId, clearingActions, background, ...)`
runs the shared `CctSearch` on event studies: each trial places the fault at the onset, its clearance `m` steps later,
re-times every clearing action (a branch trip, typically) to that instant, keeps the background events as given, and
runs over onset + clearing + `PostClearWindowSeconds`. A clearing action that is itself a fault or a clearance is
refused.

The CLI exposes both (`stability <case> --rms`):

- `--events <list>` runs one `StudyEvents` with the comma-separated spec list. It takes no `--fault-bus`, `--clear` or
  `--clear-by-trip`, and no `--screen`. The run lasts `CliDefaults.RmsDurationS` (3.0 s) or 3.0 s past the last event,
  whichever is later, at the 0.005 s step; it prints the events in time order and the verdict.
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
| `src/GridSim.Core/Dynamics/Models/AcExciter.cs` | `AcExciter` | EXAC1 and ESAC1A alternator-rectifier exciters |
| `src/GridSim.Core/Dynamics/Models/Esst4b.cs` | `Esst4b` | ESST4B static exciter |
| `src/GridSim.Core/Dynamics/Models/Sexs.cs` | `Sexs` | SEXS exciter |
| `src/GridSim.Core/Dynamics/Models/Ieeet1.cs` | `Ieeet1` | IEEET1 exciter |
| `src/GridSim.Core/Dynamics/Models/Pss1a.cs` | `Pss1a` | PSS1A stabiliser |
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
- The exciters are SEXS, IEEET1, EXAC1, ESAC1A and ESST4B; there is no other ESAC, EXAC or ESST model, no GGOV1, no PSS2B, and no over- or
  under-excitation limiter.
- Events are bolted three-phase faults, branch trips and recloses, load steps and generator trips; there is no
  unbalanced fault, fault impedance or breaker timing model.
- Without `--dyn-data` the CLI synthesises every machine parameter from `X'd`, and it fixes the exciter and PSS
  settings, so its results are indicative; the case files carry no machine dynamics. A converter's energy store is an API option that
  `SubtransientInit` does not attach, so `stability --rms` runs every converter without one.
- The integrator is fixed-step with first-order operator splitting.
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
