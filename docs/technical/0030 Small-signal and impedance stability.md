# 30. Small-signal and impedance stability

This chapter specifies the three stability engines behind the verbs `stability`, `modal` and `impedance-stability`:
the classical multi-machine swing and its critical-clearing-time (CCT) search
(`src/GridSim.Core/Dynamics/Distributed/ClassicalInit.cs`, `ClassicalMultiMachine.cs` and
`src/GridSim.Core/Dynamics/Models/CctSearch.cs`), the linearised swing model and its eigen analysis
(`src/GridSim.Core/Dynamics/SmallSignal/`, `src/GridSim.Core/Numerics/GeneralEigen.cs`), and the converter-grid
impedance scan with its Nyquist criterion (`src/GridSim.Core/Dynamics/Impedance/`). The three share one machine
preparation: the classical reduction turns a solved power flow into per-machine inertia, internal EMF and a
Kron-reduced admittance, and the classical swing, the modal state matrix and the modal cross-check of the impedance
scan are all built from it.

The RMS engine behind `stability --rms` (six-state GENROU machines, SEXS or IEEET1 exciters, PSS1A, REGC_A/REEC-style
converter plant) is specified in
[ch. 24, Standard dynamic-model library](<0024 Standard dynamic-model library.md>). This chapter covers it only
where the verb shares code with the classical path: the CCT search, the loss-of-synchronism test and the fault-window
convention. The user view of all three verbs, with real output and a description of every line they print, is
[ch. 15, RMS, modal and impedance stability](<../manual/0015 RMS, modal and impedance stability.md>).

Per-unit conventions, the Y-bus and the generator fields (`H`, `MBase`, `Pg`, `Qg`, `PowerFlowOnly`,
`IsConverterInterfaced`) are defined in [ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>)
and the solved operating point comes from [ch. 3, Power flow](<0003 Power flow.md>). Both are assumed here.

## Model

### Which generators are machines

A generator is integrated as a rotor when `H > 0` and it is neither `PowerFlowOnly` nor `IsConverterInterfaced`. The
classical preparation is called with `holdNonMachines: true` by every verb, so every other generator is held: it stays
in the network as a constant admittance fixed by its solved injection (a negative load, `-conj(S)/|V|^2` stamped on
its bus diagonal). With `holdNonMachines: false` the preparation fails on the first generator with `H <= 0`.
A case with no integrable machine fails with `No synchronous machines with inertia: ...` (exit 2 from `stability` and
`modal`). Nothing in GridSim invents an inertia for a case that has none.

The small-signal assembler adds one rule: a generator bound to a grid-forming or grid-following asset class is an
inverter by declaration, so it is held whatever its `H`. In the inverter assembly its linear control block is its
only dynamic representation, never also a swing machine.

### Classical transient model

`ClassicalInit.FromSolvedCase` builds the reduced inputs on the system base `BaseMva` in five steps.

1. Every load becomes a constant shunt admittance on its bus diagonal, from the solved voltage `V`:

```
y_load = conj(Sd/BaseMva) / max(1e-9, |V|^2)         Sd = Pd + j Qd, consumed
```

2. Every integrated machine k gets an internal node, joined to its terminal bus by `jX'd` on the system base:

```
X'd_sys = X'd_machine * BaseMva / max(1e-6, MBase)
y_gen   = 1 / (j X'd_sys)
H_sys   = H * MBase / BaseMva
```

3. The augmented admittance (the n network buses, then one internal node per machine) is Kron-reduced onto the
   internal nodes with `KronReduction.Reduce` (sequential single-node elimination,
   `w[i,j] -= w[i,k] * w[k,j] / w[k,k]`; a pivot at or below `1e-12` times the largest entry is singular and fails
   loud). The result `Y = G + jB` is the reduced admittance among the machines.
4. The internal EMF comes from the terminal condition and the SOLVED generator output, not the scheduled one:

```
S_gen = (net bus injection + local load) * share / BaseMva      share = Pg_k / sum(Pg at the bus)
I_t   = conj(S_gen / V_t)
E'    = V_t + j X'd_sys I_t           |E'| held constant, delta0 = arg(E')
```

   Machines that share a bus split its solved output by scheduled `Pg`; when the scheduled total is zero the split is
   equal. `delta0` is in the power-flow angle frame, where the slack bus sits at its stored angle.
5. Mechanical power is set to the electrical output at `delta0`, so an undisturbed run is a fixed point:

```
Pm_i = |E_i|^2 G_ii + sum_{j != i} |E_i||E_j| (G_ij cos(d_ij) + B_ij sin(d_ij))      d_ij = delta_i - delta_j
```

A bolted three-phase fault at bus f adds a `1e7` pu shunt to ground on that bus before step 3, which yields a second
reduced matrix `Y_fault`. The fault is cleared by switching back to `Y`.

### The classical swing kernel

`ClassicalMultiMachine.Simulate` integrates, per machine, with classical RK4 at a fixed step `h`:

```
d(delta_i)/dt   = ws * w_i                                 ws = 2 pi f0,  w_i = per-unit speed deviation
d(w_i)/dt       = (Pm_i - Pe_i(delta) - D_i w_i) / (2 H_i)
Pe_i            = |E_i|^2 G_ii + |E_i| sum_{j != i} |E_j| (G_ij cos(d_ij) + B_ij sin(d_ij))
```

`D_i` is zero for `stability` (the classical model carries no damping). Initial speeds are zero. The fault window is
`[on, clear)`: the faulted admittance applies at an RK4 stage time `t` when

```
t + edgeTol >= on   and   t + edgeTol < clear              edgeTol = h * 1e-6
```

so an edge formed as `onset + m*h` that lands one ulp off the sample it means still counts as reached, and an edge
that lies genuinely between samples waits for the next one. The stage times of a step are `t`, `t + h/2`, `t + h/2`,
`t + h`, and each stage reads the network live at its own time. The tolerance `1e-6` steps is
`MultiMachineSubtransient.StageTimeToleranceSteps`, shared with the RMS engine so both apply the same edge tolerance.
The two do not sample a window the same way, though: the RMS engine picks its network once at the step start time and
holds it for all four RK4 stages, so a step whose later stages cross an edge sees the change in this kernel and not in
the RMS one.

### Loss of synchronism

At every sample `k = 0 .. steps`, including the last, the kernel forms the inertia-weighted centre-of-inertia (COI)
angle and tests every machine against it:

```
coi = sum_i H_i delta_i / sum_i H_i                 H_i on the system base
lost if  |delta_i - coi| > 2 pi   for any machine i,   or  delta_i or w_i is not finite
```

The reference is the COI, not a pairwise spread: a machine is lost when it is a full pole slip away from the
inertia-weighted centre. A non-finite state is a loss, never a pass, so a diverged run cannot read as stable.
The RMS engine applies the same test over the machines in its synchronism set (`MultiMachineSubtransient`, with the
COI weighted by system-base `H`), so with every reactance collapsed to `X'd` it matches the classical CCT within 20 ms.

### RMS path, as far as the verbs need it

`stability --rms` seeds the fleet with `SubtransientInit.FromSolvedCase`, applies the bolted fault as a two-stage
network schedule (faulted reduced network from the onset, healthy network from onset plus clearing time) and
integrates with `MultiMachineSubtransient`. The CLI synthesises each machine's GENROU data from `X'd` and the case's
`H` (see the parameters table) because no bundled case carries sub-transient data, so the verb labels every `--rms`
result INDICATIVE. `--avr sexs|ieeet1` builds one `RmsExciter` per integrated machine and `--pss` adds a PSS1A to
each; their voltage references are set from the pre-fault terminal voltage so the undisturbed fleet stays at rest.
The machine, exciter, PSS and converter equations are in ch. 24.

### Small-signal model

`SmallSignalAssembler.FromSolvedCase` linearises the classical swing around the solved operating point. The state
vector holds, in this order, the rotor-angle deviations `d.delta_gen<bus>` of the g integrated machines, then their
per-unit speed deviations `d.omega_gen<bus>` (`StateKind.SwingAngle`, `StateKind.SwingSpeed`). Both kinds are REAL
rotational energy; every other state kind is SYNTHETIC control emulation (`StateLabel.IsSynthetic`), and a synthetic
coefficient is never summed into a machine `H` or `E'`.

Differentiating `Pe` at the operating angles gives the synchronising coefficients and the state matrix:

```
K[i,j]   = |E_i||E_j| (B_ij cos(d_ij0) - G_ij sin(d_ij0))                (i != j)
K_L[i,j] = -K[i,j]   (i != j)           K_L[i,i] = sum_{j != i} K[i,j]         row sums of K_L are zero
M        = diag(2 H_i)   (system base)  D = diag(D_i)

      [   0          ws I ]
A  =  [ -M^-1 K_L   -M^-1 D ]            2g x 2g, angles first, then speeds
```

`K_L` keeps the transfer conductances `G_ij`, so it is not symmetric, and nothing is clamped negative. That is why
the eigen solver is a general real one. The zero row sums make the rigid rotation of all machines a free mode.
`D_i` is the `--damping` value for every machine, in per-unit power on the system base per per-unit speed (the same
unit as the swing kernel's `D`); the default is 0. `Kext` is `K_L` in per-unit power per radian on the system base.

#### Inverter states (`--enable-inverters`)

`SmallSignalAssembler.FromSolvedCaseWithInverters` appends the linear control block of every generator bound to an
asset class of kind `GridFormingInverter` or `GridFollowingInverter` (`LinearInverterModel`). The machine block is
identical to the swing-only one. Each block `b` has an internal matrix `Aself`, a frequency input row `FreqInputRow`
(coefficient on the measured frequency deviation, Hz), a rate input row `RocofInputRow` (coefficient on measured
df/dt, Hz/s) and a power output row `PowerOutputRow` (MW). The inverters sense the system COI, a documented
archetype, and inject back into the COI power balance:

```
M_T       = sum_i M_i
df   [Hz]    = f0 * sum_i (M_i / M_T) d(omega)_i                                   dfRow[g+i] = f0 M_i / M_T
rocof [Hz/s] = -(f0 / M_T) * ( sum_j (sum_i K_L[i,j]) d(delta)_j + sum_i D_i d(omega)_i )     machine-driven part
x_b'   = Aself x_b + FreqInputRow * df + RocofInputRow * rocof                      one row per block state
d(omega)_i' += (1 / M_T) * (PowerOutputRow . x_b) / BaseMva                         every machine i
```

The rate term uses only the machine-driven part of df/dt, which breaks the algebraic loop between the inverter
output and the COI.

Grid-forming block, one state (`StateKind.DroopActuatorLag`, the first-order lag `p_lag` of the injected MW):

```
tau      = max(1e-3, ActuateTau)                                      default 0.05 s
Aself    = [ -1/tau ]    FreqInputRow = [ -damping/tau ]    RocofInputRow = [ -synInertia/tau ]    PowerOutputRow = [ 1 ]
```

At `synInertia = 0` (the class default) it reduces to a pure droop actuator with no singularity. The df/dt coefficient
is carried on the block as `SyntheticInertiaMwsPerHz` so a test can assert it is never folded into a machine `H`.

Grid-following block, three states (`PllSpeed` = `f_est`, `PllFrequencyRate` = its derivative, `DroopActuatorLag`):

```
droop    = DroopGain * MBase                                          DroopGain default 2.0 MW/Hz per MW
tau      = max(1e-3, ActuateTau)                                      default 0.25 s
bw       = PllBandwidthHz, else 1/(2 pi DetectionLatencyS) if DetectionLatencyS > 1e-6, else 20 Hz
wn       = 2 pi max(1e-3, bw)
zeta     = PllDampingRatio when authored (above 0); else 1.0 for the latency stand-in (a delay has no overshoot);
           else 0.707 (ConverterDefaults.PllDampingRatio)

          [ 0           1            0      ]       FreqInputRow  = [ 0, wn^2, 0 ]
Aself  =  [ -wn^2      -2 zeta wn    0      ]       RocofInputRow = [ 0, 0, 0 ]
          [ -droop/tau  0           -1/tau  ]       PowerOutputRow = [ 0, 0, 1 ]
```

The time-domain grid-following model has a transport delay and no PLL gains, so this block is an archetype
substitution (an SRF-PLL estimator in place of the delay), not a linearisation of that code. The CLI prints
`Inverter states are a documented archetype (COI-coupled; GFL PLL is an SRF-PLL substitution of the transport delay).`
when the block is present. A state-of-charge integrator would be a decoupled origin pole and is excluded by design:
`includeSoc` throws `NotSupportedException`, and the CLI refuses `--include-soc` with exit 2. A case that binds no
inverter class fails loud under `--enable-inverters`.

### Converter admittance models

Both models implement `IConverterImpedanceModel`: a 2x2 dq admittance `Y_dq(jw)` that the converter DRAWS, in per-unit
on the converter's own rating, and the positive-sequence reduction

```
Y_pp = 1/2 [ (Y_dd + Y_qq) + j (Y_qd - Y_dq) ]
```

#### Operating point

`ConverterOperatingPoint.Guarded` aligns the dq frame to the terminal voltage and guards it:

```
Vd0 = max(0.05, |V|)      Vq0 = 0          (0.05 pu = ConverterOperatingPoint.MinVd0Pu; Vd0WasClamped records the clamp)
Id0 =  P_pu / max(0.05, |V|)               P_pu = Pg / MBase
Iq0 = -Q_pu / max(0.05, |V|)               Q_pu = Qg / MBase      (MBase, or BaseMva when MBase <= 0)
```

`Pg` and `Qg` are the generator's scheduled values from the case, `|V|` is the solved voltage magnitude at the
converter bus. The 0.05 pu floor keeps the PLL gain `1/Vd0` finite; a floored run prints `(CLAMPED - weak/depressed)`.

#### Grid-following (`GridFollowingImpedance`)

The PLL is the standard SRF-PLL closed loop from the terminal q-axis voltage perturbation to the estimated angle. The PI
gains follow from the natural frequency and damping so the closed-loop denominator is `s^2 + 2 zeta wn s + wn^2`:

```
wn    = 2 pi max(1e-6, f_bw)        zeta = max(1e-6, damping)
kp    = 2 zeta wn / Vd0             ki   = wn^2 / Vd0
G_pll(s) = (kp s + ki) / (s^2 + Vd0 kp s + Vd0 ki)           G_pll(0) = 1/Vd0, no free integrator
G_ci(s)  = wci / (s + wci)
Y_dd =  G_ci Id0/Vd0        Y_dq =  Iq0 G_pll
Y_qd =  G_ci Iq0/Vd0        Y_qq = -Id0 G_pll
Y_pp =  1/2 (G_ci/Vd0 - G_pll) (Id0 + j Iq0)
```

`Y_qq = -Id0 G_pll` has a negative real part around the PLL bandwidth: the negative-resistance band that destabilises a
weak grid. Both off-diagonals are proportional to `Iq0`, so a unity-power-factor unit decouples.

The detail level fixes `wci` and the PLL:

| Detail | PLL | Current loop `wci` |
|---|---|---|
| `OuterLoopOnly` | ideal: `G_pll` frozen at `1/Vd0` | `2 pi 1000` rad/s (`IdealCurrentLoopHz`) |
| `PllOuter` (default) | SRF-PLL as above | `2 pi 1000` rad/s |
| `PllCurrentOuter` | SRF-PLL as above | `2 pi f_ci` rad/s from `--current-bw` or the class's `CurrentLoopBandwidthHz`; `2 pi 1000` when the class has none |

The current loop is never unity: the 1000 Hz roll-off makes `Y_conv -> 0` at high frequency so the return ratio is
proper and the Nyquist contour closes at infinity. With no authored PLL bandwidth the model uses 20 Hz and damping 0.707
(`ConverterDefaults.PllBandwidthHz`, `PllDampingRatio`), the one PLL default the EMT converter, the impedance model and
the small-signal block share, so an unauthored device is the same device in all three.

When the case binds an asset class to the chosen generator, `FromAssetClass` reads `PllBandwidthHz`, `PllDampingRatio`
and `CurrentLoopBandwidthHz` from it; each CLI flag overrides only its own parameter, and any override makes the model
`CuratedPlaceholder`. An override must be finite and above 0. Provenance is `Recorded` only when the bandwidth used came
from the class, no flag overrode anything, and the class tags its bandwidth `Recorded` or `DerivedFromRecorded`.
A flag-built model is always `CuratedPlaceholder`.

#### Grid-forming (`GridFormingImpedance`)

A voltage source behind a virtual impedance with a first-order power-synchronising resistance. The admittance does not
depend on the operating point and does not use the detail level.

```
Lv  = Xv / w0                  Rv = 0.05 Xv  (GfmVirtualROverX)              w0 = 2 pi f0
Dp  = m_p = S / (D f0)   if D = dampingMwPerHz > 0,  else 0.30 pu         S = rating (MVA), on which the impedance is
H_v = J f0 / (2 S)                                                          J = synInertiaMwsPerHz (s, virtual inertia constant)
tau_p = max(1e-3, 1/(2 pi max(1e-3, f_pl)) + 2 H_v Dp)                      (= filter + J / D with an authored damping)
Z(s)  = [ (Rv + s Lv) + Dp/(1 + s tau_p) ] I + w0 Lv J             J = [[0,-1],[1,0]]
Y(s)  = Z(s)^-1                                                    (2x2 inverse)
```

`Z_dq = [[d, -w0 Lv],[w0 Lv, d]]` with `d = Rv + s Lv + Dp/(1 + s tau_p)` and `w0 Lv = Xv`. The flag-built model uses
`Xv = 0.20 pu`, `f_pl = 5 Hz`, no damping and no inertia, so `Dp = 0.30 pu` and `tau_p = 1/(2 pi 5) = 31.83 ms`. A bound
class supplies `VirtualReactancePu`, `PowerLoopBandwidthHz`, `DampingMwPerHz` and `SynInertiaMwsPerHz`.

### Grid impedance models

**Canonical Thevenin.** The grid is an ideal source behind `Z_th`. The rating `PRatedPu` is the converter's rating on the
system base, `max(1e-3, MBase / BaseMva)`:

```
|Z_th| = 1 / (SCR * PRatedPu)          X/R = 10 (ImpedanceStabilityOptions.XOverR)
R = |Z_th| / sqrt(1 + (X/R)^2)         X = |Z_th| (X/R) / sqrt(1 + (X/R)^2)
Z_th(f) = R + j X (f/f0)                                                    stationary frame
Z_pp(f) = Z_th(f + f0),  Z_nn(f) = Z_th(f - f0)                             SISO, converter dq frame
Z_dq(jw) = (R + j w L) I + w0 L J        L = X / w0                          dq form, off-diagonal = X
```

`SCR <= 0` or `PRatedPu <= 0` throws. The model is exact and closed-form, and it is checked to 12 decimal places against
an independent derivation.

**Real network.** `GridImpedanceAssembler.DrivingPointAtRatio` builds the harmonic admittance `Y(h)` at the signed ratio
`h = f/f0` with `FrequencyDependentYBus.Build` (see [ch. 19, Harmonics](<0019 Harmonics.md>)), solves `Y(h) x = e_k`
for the converter bus k, and returns `x[k]`. Passing the signed `h` makes `Z(-f) = conj(Z(f))` exact. The dq form uses the
modified-sequence transform with `Zp = Z(f_p + f0)` and `Zn = Z(f_p - f0)`:

```
Z_dq = 1/2 [[ Zp + Zn,       j (Zp - Zn) ],
            [ -j (Zp - Zn),  Zp + Zn     ]]
```

A solve at `|h| < 1e-4` is the dq DC arm, where a lossless network is singular; it throws and the scan never substitutes
a canonical value. A singular solve at any frequency throws with that frequency in the message.

### Loop gain

Every quantity on the loop is on the system base. The converter admittance, in per-unit on its rating, is multiplied by
`PRatedPu` component-wise:

```
Y_sys(jw) = PRatedPu * Y_conv(jw)
L_pp(jw)  = Z_pp(f) * Y_sys_pp(jw)                          SISO, positive sequence
L_nn(jw)  = Z_nn(f) * Y_sys_nn(jw)                          SISO, negative sequence
T(jw)     = Y_sys_dq(jw) * Z_dq(jw)                         MIMO, 2x2
```

`Y_pp = 1/2 [(Y_dd + Y_qq) + j (Y_qd - Y_dq)]` and `Y_nn = 1/2 [(Y_dd + Y_qq) - j (Y_qd - Y_dq)]` are the modified-sequence
reductions of the dq admittance at dq frequency f. The grid impedance paired with each must be the same reduction of the
grid's dq impedance: for the RL Thevenin `Z_dq = (R + j w L) I + w0 L J` that is `R + j (w + w0) L = Z_th(f + f0)`, and
for the real network it is the `Zp = Z(f_p + f0)` of the dq form above, so the SISO loop reads the grid at `f + f0` and
`f - f0`, not at `f`. SISO neglects the off-diagonal (cross-sequence) terms of the 2x2 sequence matrices and equals the
MIMO verdict when the converter has none (`BugHunt1029Tests.BH14_siso_and_mimo_agree_at_every_scr_for_a_converter_without_cross_sequence_coupling`).
On the canonical grid `PRatedPu * Z_th = (1 + j (X/R)(f/f0)) / (SCR sqrt(1 + (X/R)^2))`, so the rating cancels:
`L` depends on the converter's own-base admittance, the SCR and `f/f0` only.

## Algorithms

### The CCT search

`CctSearch.Run` (`src/GridSim.Core/Dynamics/Models/CctSearch.cs`) is the one search both engines call, over whole
integration steps. The window holds `M = floor(maxSeconds/step + 1e-6)` steps (500 for 1.0 s at 2 ms; the `1e-6` absorbs
that `1.0/0.002` is not exactly 500 in binary). A trial with `m` steps clears the fault at exactly `m * step` after the
onset and runs to `onset + m*step + 5 s`; the trial returns whether synchronism held. The classical trial is built as the
fault window `[onset, onset + m*step)`, the window `stability --clear` builds, so feeding the reported CCT to a single
classical study at the same step gives the same verdict. Every trial is memoised, so a step count is never simulated twice.

The CCT is defined as the first loss of synchronism from below: the largest `m` such that every clearing time from 0 to
`m` steps holds. Three stages find it.

1. **Bisection.** Test `m = 0`. If it loses, the result is `UnstableAtZero`, CCT 0, one trial. Test `m = M`; if it holds, the
   stable end is `M` with no unstable end. Otherwise integer-bisect between a stable `lo` and an unstable `hi`
   (`mid = lo + (hi - lo)/2`) until they are adjacent or the iteration cap is reached. No floating-point midpoint is formed.
2. **Downward verification.** Test the `scanSteps` step counts below `lo` (down to step 1). If one loses synchronism, `lo`
   was a stable pocket above an earlier loss: record `lo` as a pocket, and bisect again between 0 and that loss. After
   `MaxRefinementRounds` such rounds the search stops guessing and walks up from step 1 to the lowest loss, which is the
   exact first loss.
3. **Upward probe.** Test the `scanSteps` step counts above `hi` (up to `M`). A step that holds is a stable pocket. The CCT
   does not move, but the bracket carries the pocket and the verb prints `NON-MONOTONE`.

The returned `CctBracket` holds `StableSteps`, `UnstableSteps` (null when the whole window held), `UnstableAtZero`,
`PocketStableSteps`, `ScanSteps` and `Trials`. `PocketStableSteps` is the HIGHEST step in the pocket that held, so the
printed clearing time is the highest stable step the search saw above the first loss. `NonMonotone` is `PocketStableSteps
is not null`. The reported CCT is `StableSteps * step`.

Guarantee: the stable end is the first loss from below provided no stable pocket is wider than `scanSteps` steps; a wider
pocket would hide the losses beneath it from the downward verification. Only a linear scan from 0 removes that proviso.
Cost: when the case is monotone near its CCT the search adds at most `2 * scanSteps` trials to the bisection (32 at the
default 16). A scan depth of 0 gives the bisection alone. Bad arguments (a step or window that is not positive and finite,
a window shorter than one step or longer than `int.MaxValue` steps, fewer than 1 halving, a negative scan depth) throw
`ArgumentOutOfRangeException`.

The `--rms` verbs run the search twice per bus, once through `CriticalClearingTimeSeconds` and once through
`CriticalClearingBracket`, and throw if the two disagree, so an RMS CCT costs two searches.

### Eigen solver, deflation and participation

`GeneralEigen` (`src/GridSim.Core/Numerics/GeneralEigen.cs`) computes the eigenvalues of the real non-symmetric `A` by
Parlett-Reinsch balancing, Householder reduction to upper Hessenberg form, and the Francis double-shift implicit QR
iteration (the EISPACK `hqr` kernel; a cap of 60 iterations per eigenvalue, then it fails loud). Eigenvectors:

- Right vectors by inverse iteration on the original `A`, 40 sweeps, with the eigenvalue as shift offset by
  `1e-10 * max(scale, |mu| + 1)` skewed into the complex plane by 0.37, from a deterministic non-symmetric start vector.
- Repeated but non-defective eigenvalues (identical co-located machines) are recomputed with deflation against the vectors
  already found. A cluster whose deflated vector leaves a residual `|A v - lambda v|` above `1e-6 * scale` is defective.
- Left vectors are the rows of `W = V^-1`, formed by Gauss-Jordan with partial pivoting; a pivot at or below
  `1e-12 * max|V|` means `V` is singular. Each pair is then checked: `|psi^H phi - 1|` above `max(1e-9, 1e-6) = 1e-6` throws.

A defective matrix throws `DefectiveEigenproblemException`; the solver never fabricates a participation vector.

`ModalAnalysis.Analyze` takes one of two paths.

**Undamped swing-only path** (no inverter states, and the speed-speed block of `A` is exactly zero). The rigid-body
rotation is a defective double zero, and Francis QR would split it into a spurious tiny real pair that miscounts the
right-half-plane set. The analysis deflates it. It picks a reference machine r, the one with the smallest
`|A[speed_i, angle_i]|` (the largest inertia relative to its own synchronising stiffness), forms relative states
`phi_i = d(delta)_i - d(delta)_r` and `nu_i = d(omega)_i - d(omega)_r` for `i != r`, and solves the `2(g-1)` system

```
d(phi_i)/dt = ws nu_i
d(nu_i)/dt  = sum_{l != r} ( A[S_i, A_l] - A[S_r, A_l] ) phi_l          (S = speed row, A = angle column)
```

whose eigenvalues are exactly the electromechanical modes. The two missing eigenvalues are appended as exact `s = 0`
with an all-zero participation vector, and the reference machine's own states carry zero participation. If the reduced
matrix is itself defective the analysis falls back to eigenvalues only and records why.

**Damped or inverter-augmented path.** `GeneralEigen.Decompose(A)` runs on the full matrix. A defective result falls back
to eigenvalues only (`GeneralEigen.Eigenvalues`).

In both fallbacks `ModalResult.ParticipationUnavailable` is true, every participation vector is all zero, and
`ParticipationUnavailableReason` carries the decomposition's own message; the CLI prints it on stderr and the export
carries `participationAvailable: false`.

For each eigenvalue `s = sigma + j omega` the analysis reports

```
|s|    = sqrt(sigma^2 + omega^2)
zeta   = -sigma / |s|           if |s| > 1e-12,  else 0
f      = |omega| / (2 pi)
mode   = oscillatory if |omega| > 1e-9, else real
RHP    : counted when sigma > 1e-9                                           (ModalAnalysis.DefaultRhpTolerance)
p_k    = |psi_k| |phi_k| / sum_k |psi_k| |phi_k|                             participation of state k, sums to 1
```

The modes are sorted oscillatory first, lowest `zeta` first, then the real modes by ascending `sigma`. The minimum damping
ratio is the lowest `zeta` over the oscillatory modes, `NaN` (printed `n/a`) when there are none. The CLI tags the
least-damped oscillatory mode `(UNSTABLE)` when its `zeta` is below 0 and `(poorly damped)` when it is below 0.05, and
exits 1 when the right-half-plane count is above 0.

### Nyquist winding, margins and the SCR sweep

`ImpedanceStabilityScan.Run` evaluates two frequency grids. The reporting grid is `LogGrid(fmin, fmax, ppd)`, which holds
`max(2, round(decades * ppd) + 1)` log-spaced points inclusive of both ends (171 for `0.1` to `250` Hz at 50 per decade).
It is what the export carries. The winding grid decides the verdict:

```
canonical : LogGrid( min(fmin, 1e-3),  max(fmax, 1e5),  max(ppd, 60) )          481 points at ppd 50
real      : LogGrid( min(fmin, 2 * 1e-4 * f0), max(fmax, 1e5), max(ppd, 60) )   and fmin must exceed 1e-4 * f0
```

so `--fmin`, `--fmax` and `--ppd` change the band written to the export; they change the grid the verdict is counted on
only when they widen it: `--fmin` below the grid's own floor, `--fmax` above 1e5 Hz or `--ppd` above 60. Under MIMO on the real
network, under either criterion (both read `Z(f - f0)`), winding points within `1e-4` of the dq DC arm (`|f - f0|/f0`) are
dropped, and a reporting point there throws.

**Winding count.** Both criteria wind a scalar return difference `w(f)` around the origin:
`w = (1 + L_pp)(1 + L_nn)` for SISO, `w = det(I + T)` for MIMO. Both are conjugate-symmetric, `w(-jw) = conj(w(jw))`
(for real dq dynamics `L_pp(-jw) = conj(L_nn(jw))`), so the positive arm and its mirror close the contour. The full contour is the conjugate mirror of the positive arm followed by the positive arm, in
ascending frequency: `[conj(w) from fmax down to fmin]`, then `[w from fmin up to fmax]`. The count is

```
total = sum over consecutive points of wrap(arg(next) - arg(prev)) into (-pi, pi]
N     = round(total / (2 pi))
```

`Encirclements = N`, `UnstableModeCount = |N|`, `Stable = (N == 0)`. `MinDistToMinusOne` is the smallest of
`|1 + L_pp|` and `|1 + L_nn|` over the positive grid for SISO and `min |w|` for MIMO. An oscillatory closed-loop mode is a
complex pair and counts 2 under both criteria, as in the modal check. The verdict also carries `MaxPhaseStepRad`, the largest single wrapped step, and `PhaseStepGuardTripped`, true when
that step exceeds a quarter turn (`MinorLoopNyquist.MaxTrustedPhaseStepRad = pi/2`): the grid may then have skipped part of
a sharp resonance and the smallest-angle unwrap can count it the wrong way. Decided (GR-096): the verdict and exit code are
still those of the count, and the command line prints a WARNING on stderr naming the step and asking for a larger `--ppd`.
The count equals the number of unstable closed-loop modes when the grid and the converter are each stable on their own
(`P = 0` open-loop right-half-plane poles), which holds for these models: the SRF-PLL closed loop is Hurwitz with no free
integrator, `G_pll(0) = 1/Vd0` is finite, and the converter admittance has no pole on the imaginary axis, so no contour
indentation is needed. An unstable loop counts a negative `N` in this implementation.

**Margins** (SISO only, from `L_pp`; MIMO reports `inf` for both). The phase of `L_pp` is unwrapped continuously along the
positive grid.
The gain margin is `-20 log10 |L|` at the phase crossover (unwrapped phase passes an odd multiple of `-pi`), taking the
crossing with the smallest `|GM|`. The phase margin is the phase excess over `-180 deg` at the gain crossover
(`|L| = 1`), taking the smallest `|PM|`. Both interpolate linearly between grid points. `inf` is reported when there is
no crossing. The margins support the verdict and do not decide it: a STABLE verdict may carry a negative `PM`.

**SCR sweep** (canonical grid only, `ScrSteps >= 2`). For `j = 0 .. ScrSteps - 1`:

```
SCR_j = ScrMin + (ScrMax - ScrMin) * j / (ScrSteps - 1)          defaults 1.0, 10.0, 37 points: step 0.25
```

the winding grid is reused, `Z_grid` is rebuilt at `SCR_j` through the same assembler, `Y` is evaluated once, and the winding
count, stable flag and `minDist` are recorded. The critical SCR is the midpoint of the last unstable and the first stable
sweep point, taken at the first change from unstable at low SCR to stable at high SCR; it is `NaN` with `BoundaryFound`
false when the sweep has no such change.

### Modal cross-check

The CLI builds the small-signal model of the case with no `X'd` override and no damping (so every machine uses the
synthetic `0.30 pu`, even on `case9`), with inverter states when a generator is bound to an inverter class, and runs
`ModalAnalysis.Analyze` on it. `WithModalCrossCheck` records the right-half-plane count and sets
`AgreesWithModal = (Nyquist.UnstableModeCount == rhpCount)`. The two models have different structures: the modal model has
inverter states only for bound classes, and couples them to the COI, while the scan models one converter against a
Thevenin grid. Where they differ the Routh oracle in `ImpedanceModalAgreementTests` is the decisive check. A case with no
integrable machine has no modal model; the scan then prints `n/a on this case` and writes the reason to stderr.

## Parameters and defaults

### Classical stability and the CCT search

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Fault onset | 0.1 | s | Start of the fault in every CCT trial and every single study | `ClassicalInit.CctOnsetSeconds`, `RmsTransientStability.CctDefaultOnsetSeconds`, `CliDefaults.FaultOnsetS` |
| Post-clear window | 5.0 | s | Simulated time after clearing in a CCT trial and in the classical `--clear` study | `ClassicalInit.CctPostClearWindowSeconds`, `RmsTransientStability.PostClearWindowSeconds`, `CliDefaults.ClassicalPostClearS` |
| Integration step | 0.002 | s | RK4 step and CCT resolution, classical and RMS search | `ClassicalInit.CctDefaultStepSeconds`, `RmsTransientStability.CctDefaultStepSeconds`, `CliDefaults.ClassicalStepS` |
| Search window | 1.0 | s | Upper end of the CCT search; 500 steps | `ClassicalInit.CctDefaultMaxSeconds`, `RmsTransientStability.CctDefaultMaxSeconds` |
| Bisection cap | 18 | halvings | Cap on each bisection; the 500-step window needs 9 | `CriticalClearingBracket` `iterations`, `RmsTransientStability.CctDefaultBisectionIterations` |
| Scan depth | 16 | steps | Downward verification and upward probe depth (32 ms at 2 ms) | `CctSearch.DefaultScanSteps` |
| Refinement rounds | 4 | rounds | Re-bisections before the exact walk from step 1 | `CctSearch.MaxRefinementRounds` |
| Fault-edge tolerance | 1e-6 | steps | An RK4 sample within this of a fault edge has reached it | `MultiMachineSubtransient.StageTimeToleranceSteps` |
| Slip limit | 2 pi | rad | Rotor-angle distance from the COI that counts as loss of synchronism | `slipLimitRad`, `RmsTransientStability.DefaultSlipLimitRad` |
| Fault shunt | 1e7 | pu | Bolted-fault conductance to ground at the fault bus | literal in `ClassicalInit.FromSolvedCase` |
| Load voltage floor | 1e-9 | pu^2 | Floor on `|V|^2` in the load admittance | literal in `ClassicalInit.FromSolvedCase` |
| Nominal frequency of the search | case value | Hz | `f0` of every CCT trial: the case's own frequency unless the caller passes one (60 Hz for `case9`; the CLI passes none) | `f0` default of `CriticalClearingBracket`, `GridModel.FrequencyHz` |
| Nominal frequency of `--clear` | case value | Hz | `f0` of the classical single study | `GridModel.FrequencyHz` |
| Synthetic `X'd` | 0.30 | pu, machine base | Used for every machine of every case but `case9` | `CliDefaults.SyntheticXdpPu` |
| WSCC-9 `X'd` | 0.0608, 0.1198, 0.1813 | pu, machine base | Buses 1, 2, 3 (Anderson and Fouad) | `IeeeCases.Wscc9TransientReactancePu` |
| Default fault bus | 7 on `case9`, else the first bus with no generator, else the first bus of the model | bus id | Used when `--fault-bus` is absent | `DefaultFaultBus` in `Program.cs` |
| Kron pivot floor | 1e-12 | relative | Singular-pivot threshold of the reduction | `NumericTolerances.RelativePivotFloor` |
| Classical export | step 0.001 s, 5.0 s, kick 0.10 rad | - | No-fault perturbed run written by `--export`, sampled every 50 steps (50 ms) | `CliDefaults.ClassicalExportStepS`, `ClassicalExportDurationS`, `ClassicalExportKickRad` |

### RMS study inputs set by the CLI

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Single study step, duration | 0.005 s, 3.0 s | - | `--rms --clear` run (the library default duration is 10 s) | `CliDefaults.RmsStepS`, `RmsDurationS`, `RmsTransientStability.StudyDefaultDurationSeconds` |
| GENROU ratios | `Xd` 3.0, `X''d` 0.8, `Xq` 2.5, `X'q` 1.5, `X''q` 0.8, all times `X'd` | - | Synthesised machine reactances | `CliDefaults.GenrouXdOverXdp`, `GenrouXdppOverXdp`, `GenrouXqOverXdp`, `GenrouXqpOverXdp`, `GenrouXqppOverXdp` |
| GENROU time constants | `T'do` 8.0, `T''do` 0.03, `T'qo` 0.4, `T''qo` 0.05 | s | Synthesised open-circuit time constants | `CliDefaults.GenrouTdopS`, `GenrouTdoppS`, `GenrouTqopS`, `GenrouTqoppS` |
| SEXS | lead 0.1 s, lag 1.0 s, gain 100, `Te` 0.05 s, field limits 0 and 8 pu | - | `--avr sexs` | `RmsExciter.FromSexs` call in `RunRmsStability` |
| IEEET1 | `Ka` 50, `Ta` 0.02 s, `Ke` 1.0, `Te` 0.2 s, `Kf` 0.05, `Tf` 1.0 s, regulator limits -10 and 10 pu | - | `--avr ieeet1` | `RmsExciter.FromIeeet1` call in `RunRmsStability` |
| PSS1A | gain 20, washout 10 s, lead 0.15 s, lag 0.05 s (two stages), limits -0.1 and 0.1 pu | - | `--pss` | `Pss1a` constructor call in `RunRmsStability` |

### Small-signal model and modal analysis

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Damping `D` | 0 | pu power per pu speed | Same for every machine; `--damping` | `machineDamping` of `FromSolvedCase` |
| Zero-mode magnitude | 1e-12 | 1/s | `\|s\|` at or below this reports `zeta = 0` | `ModalAnalysis.ZeroModeMagnitude` |
| Oscillatory threshold | 1e-9 | rad/s | `\|omega\|` above this is oscillatory | `ModalAnalysis.OscillatoryImagTolerance` |
| Right-half-plane threshold | 1e-9 | 1/s | `sigma` above this counts as unstable; absolute, not scaled by the size of `A` | `ModalAnalysis.DefaultRhpTolerance` |
| Poorly damped | 0.05 | - | Tag threshold on the least-damped mode | literal in `RunModal` |
| Participation print | top 3 states, floor 1e-6, first 6 oscillatory rows | - | Console table only; the export carries every factor | literals in `RunModal` |
| s-plane plot | 51 columns by 21 rows | - | Scales set by the largest `\|sigma\|` and `\|omega\|`; `X` marks `sigma > 1e-9` | `SPlane` in `Program.cs` |
| Inverse-iteration sweeps | 40 | sweeps | Per eigenvector | `GeneralEigen.DefaultInverseIterations` |
| Biorthonormality tolerance | 1e-9, slack 1e-6 | - | A pair fails when `\|psi^H phi - 1\|` exceeds the larger | `GeneralEigen.DefaultBiorthonormalityTolerance`, `BiorthonormalitySlack` |
| QR iteration cap | 60 | per eigenvalue | Then the decomposition fails loud | `GeneralEigen.QrMaxIterationsPerEigenvalue` |
| Shift offset, skew | 1e-10, 0.37 | relative, ratio | Inverse-iteration shift | `GeneralEigen.ShiftOffsetRelative`, `ShiftImaginarySkew` |
| Start vector | `(1 + 0.017 i) + j 0.003 (i + 1)` | - | Deterministic start | `GeneralEigen.StartVectorRealStep`, `StartVectorImagStep` |
| Early stop | 1e-14 | - | Inverse iteration stops when successive iterates agree this closely | `GeneralEigen.InverseIterationStopTolerance` |
| Cluster, parallel, residual tolerances | 1e-6, 1e-6, 1e-6 | relative | Repeated-eigenvalue handling | `NumericTolerances.EigenClusterRelativeTolerance`, `EigenParallelTolerance`, `EigenResidualRelativeTolerance` |
| Parallel inverse iteration | order 32 and above | states | Result does not depend on the worker count | `GeneralEigen.ParallelInverseIterationMinOrder` |
| GFM actuator `tau` | 0.05 | s | Default first-order lag | `ConverterDefaults.SmallSignalGfmActuateTauSeconds` |
| GFL actuator `tau` | 0.25 | s | Default first-order lag | `ConverterDefaults.SmallSignalGflActuateTauSeconds` |
| GFL droop | 2.0 | MW/Hz per MW | Default droop gain | `ConverterDefaults.SmallSignalGflDroopMwPerHzPerMw` |
| GFL PLL | 20 Hz, damping 0.707; 1.0 for the latency stand-in | - | Fallback bandwidth and damping | `ConverterDefaults.PllBandwidthHz`, `PllDampingRatio`, `SmallSignalDelayEstimatorDampingRatio` |
| GFL latency floor | 1e-6 | s | A detection latency at or below this is "not authored" | `ConverterDefaults.SmallSignalLatencyFloorSeconds` |
| PLL bandwidth floor | 1e-3 | Hz | Lower clamp of the small-signal PLL bandwidth | `ConverterDefaults.SmallSignalPllBandwidthFloorHz` |
| Actuator floor | 1e-3 | s | Lower clamp of both actuator time constants | `LinearInverterModel.TauFloor` |

### Impedance scan

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Reporting band | 0.1 to 250, 50 per decade | Hz | `--fmin`, `--fmax`, `--ppd`; export only | `ImpedanceStabilityOptions.FMinHz`, `FMaxHz`, `PointsPerDecade` |
| Winding grid | 1e-3 to 1e5, at least 60 per decade | Hz | Decides the verdict | `ImpedanceStabilityScan.WindingGridMinHz`, `WindingGridMaxHz`, `WindingGridMinPointsPerDecade` |
| Real-network winding start | 2 x guard frequency | Hz | `2 * 1e-4 * f0` | `ImpedanceStabilityScan.RealNetworkWindingGuardMultiple` |
| DC-arm guard | 1e-4 | fraction of `f0` | Real-network solves below this fail | `GridImpedanceAssembler.DcArmGuardFraction` |
| Base SCR | 3.0 | - | `--scr` | `ImpedanceStabilityOptions.Scr` |
| Thevenin `X/R` | 10 | - | Fixed; no flag | `ImpedanceStabilityOptions.XOverR` |
| SCR sweep | 1.0 to 10.0, 37 points | - | `--scr-min`, `--scr-max`, `--scr-steps` | `ImpedanceStabilityOptions.ScrMin`, `ScrMax`, `ScrSteps` |
| Converter rating | `max(1e-3, MBase / BaseMva)` | pu on system base | Scales `Y` and sets `\|Z_th\|` | `ImpedanceStabilityOptions.PRatedPu` (set in `RunImpedanceStability`) |
| `Vd0` floor | 0.05 | pu | Clamp of the PLL-aligned voltage and of the voltage in `Id0`, `Iq0` | `ConverterOperatingPoint.MinVd0Pu`, `CliDefaults.OperatingPointVmFloorPu` |
| GFL current loop | 1000 | Hz | Roll-off at `PllOuter` and `OuterLoopOnly` | `GridFollowingImpedance.IdealCurrentLoopHz` |
| GFL PLL | 20 Hz, damping 0.707 | - | Flag-built and unauthored-class defaults | `ConverterDefaults.PllBandwidthHz`, `PllDampingRatio`, `CliDefaults.PlaceholderPllBandwidthHz`, `PlaceholderPllDamping` |
| PLL parameter floor | 1e-6 | Hz, ratio | Lower clamp of bandwidth and damping | `ConverterDefaults.ImpedancePllParameterFloor` |
| GFM virtual reactance | 0.20 | pu | Default `Xv` | `ConverterDefaults.GfmVirtualReactancePu` |
| GFM virtual `R/X` | 0.05 | - | `Rv = 0.05 Xv` | `ConverterDefaults.GfmVirtualROverX` |
| GFM power loop | 5 Hz | Hz | Default `f_pl` | `ConverterDefaults.GfmPowerLoopBandwidthHz` |
| GFM `Dp` | 0.30 default; `S / (D f0)` with an authored damping | pu on the rating | Power-loop synchronising resistance, the per-unit droop | `ConverterDefaults.GfmDefaultSyncResistancePu`, `GridFormingImpedance` |
| GFM `tau_p` | floor 1e-3, `+ 2 H_v Dp`, bandwidth floor 1e-3 | s | Power-loop time constant | `ConverterDefaults.GfmMinPowerLoopTauSeconds`, `GfmMinPowerLoopBandwidthHz` |
| Test-only PCC shunt | 1e-3 | pu | Ground path of the two-bus `GridModel` that `GridImpedanceAssembler.CanonicalThevenin` builds for tests; the scan uses the closed-form `CanonicalTheveninImpedance` and never builds that model | `GridImpedanceAssembler.CanonicalPccShuntPu` |
| Test-only base voltage | 400 | kV | Label only on the same test-only model; it is per-unit | `GridImpedanceAssembler.CanonicalBaseKv` |

## Invariants

Each invariant is pinned by the named test in `tests/GridSim.Tests`.

**Classical initial state and swing**

- The classical operating point is a fixed point: with `Pm` set to `Pe` at `delta0`, an undisturbed run does not move.
  `ClassicalMultiMachineTests.Wscc9_prefault_operating_point_is_stationary`.
- The kernel reproduces closed forms: the single-machine small-signal period against its analytic value
  (`Smib_small_signal_mode_matches_the_analytic_frequency`), the equal-area critical clearing time bound
  (`Equal_area_critical_clearing_time_bounds_stability`), bounded undamped oscillation
  (`Undamped_oscillation_amplitude_is_bounded_over_many_cycles`) and the COI of a two-machine pair
  (`Two_machine_pair_conserves_the_centre_of_inertia`).
- A fault edge one ulp off its RK4 sample counts as reached; an edge genuinely between samples waits.
  `ClassicalFaultEdgeToleranceTests.A_clearing_edge_one_ulp_past_a_sample_counts_as_reached`,
  `An_onset_edge_one_ulp_past_a_sample_counts_as_reached`, `An_edge_genuinely_between_samples_still_waits_for_the_next_sample`.
- A case with no integrable machine fails loud and no rotor-less model is integrated: `SmallSignalHeldUnitsTests.A_case_with_no_machine_at_all_still_fails_loud`.

**CCT search**

- At its own 60 Hz, `case9` bus 7 has a classical bracket of 275 steps stable (550 ms), 276 lost, with a stable pocket at
  279: `D2NominalFrequencyTests.R238_classical_cct_scales_with_the_square_root_of_the_frequency`.
- The search tests below pin the algorithm on `case9` re-set to 50 Hz (`CopyWith(frequencyHz: 50.0)`), the network their
  numbers were captured on. There bus 7 has a classical CCT of exactly 610 ms (305 steps) and its RMS twin is 302 steps:
  `ClassicalCctStepQuantisedTests.Case9_bus7_cct_is_exactly_610_ms`, `RmsCctStepQuantisedTests.Case9_bus7_cct_is_exactly_302_steps`.
- The bracket is at step resolution and the verdict agrees with a single study: the CCT holds and one step later loses,
  in a CLI-style classical study and in a direct RMS study.
  `ClassicalCctStepQuantisedTests.The_cct_survives_in_a_cli_style_study_and_one_step_later_does_not`,
  `RmsCctStepQuantisedTests.The_cct_survives_in_a_direct_study_and_one_step_later_does_not`,
  `ClassicalFaultEdgeToleranceTests.Case9_bus6_classical_cct_is_334_ms_and_the_cli_style_study_agrees`.
- A window that survives to its end reports the last grid point and no unstable end:
  `ClassicalCctStepQuantisedTests.Case9_bus5_survives_the_whole_window`,
  `RmsCctStepQuantisedTests.A_window_that_survives_reports_its_last_grid_point_and_no_unstable_end`.
- The search is the first loss from below: a monotone case returns the bisection answer with no flag; a bisection that
  lands on a pocket is sent back; a pocket above a correct landing is flagged without moving the CCT; repeated pockets fall
  back to the exact walk; unstable at zero costs one trial; scan depth 0 equals the bisection trial for trial.
  `CctFirstLossSearchTests.A_monotone_case_gives_the_bisection_answer_and_no_flag`,
  `A_bisection_that_lands_on_a_pocket_is_sent_back_to_the_first_loss`,
  `A_pocket_above_a_correct_landing_is_flagged_without_moving_the_cct`,
  `Repeated_pockets_fall_back_to_an_exact_walk_and_still_find_the_first_loss`,
  `A_whole_window_that_survives_at_its_end_is_still_verified_below`,
  `Unstable_at_zero_is_unchanged_and_costs_one_trial`,
  `A_scan_depth_of_zero_is_the_pre_N048_bisection_trial_for_trial`.
  On `case9` bus 2 the unchecked bisection lands on the pocket:
  `ClassicalFaultEdgeToleranceTests.Without_the_N048_checks_bus_2_would_land_on_its_pocket`.
- The search is deterministic, and the dense and sparse RMS networks give the same bracket (the same integer step counts) on
  `case9` bus 7:
  `ClassicalCctStepQuantisedTests.The_search_is_bit_identical_across_repeated_runs`,
  `RmsCctStepQuantisedTests.Dense_and_sparse_give_the_same_bracket_bit_for_bit`.
- Meaningless arguments fail loud: `CctFirstLossSearchTests.The_search_fails_loud_on_meaningless_arguments`,
  `ClassicalCctStepQuantisedTests.A_meaningless_step_window_or_cap_fails_loud`,
  `RmsCctStepQuantisedTests.A_window_shorter_than_a_step_or_a_bad_step_fails_loud`.

**RMS path**

- With every reactance collapsed to `X'd` the subtransient CCT matches the classical one to within 20 ms on `case9` bus 7:
  `RmsTransientStabilityTests.Subtransient_critical_clearing_time_reduces_to_the_classical`; a short fault survives and a
  long one does not: `A_short_fault_survives_and_a_long_fault_does_not`.
- `--avr` is validated, `none` passes no exciters, and every RMS study call receives the exciters:
  `StabilityAvrFlagContractTests.Avr_flag_is_validated_and_none_passes_no_exciters`, `Every_rms_study_call_receives_the_exciters`.

**Small-signal model and modal analysis**

- The single-machine eigenvalue equals `Im(s) = sqrt(ws Ps / 2H)` undamped (to 6 decimals) and `Re(s) = -D/(4H)` damped:
  `SmallSignalModalTests.SMIB_oscillatory_mode_matches_the_analytic_small_signal_frequency`,
  `SMIB_damped_mode_matches_the_analytic_real_part`.
- The undamped swing has the defective COI pair, deflated, with participation on the deflated subspace; `case9` has two
  electromechanical modes in 0.8 to 2.2 Hz and no false right-half-plane count:
  `Undamped_swing_has_exactly_the_defective_COI_pair_and_participation_is_deflated`, `Wscc9_has_two_electromechanical_modes_in_band`.
- With transfer conductances zeroed the model collapses to the symmetric ContinuumWave model to `1e-9`:
  `Collapses_to_a_lossless_symmetric_ContinuumWave_to_1e_minus_9`.
- Held units: an `H = 0` generator is held, damping aligns to generators and maps onto machines, a wrong-length or
  non-finite damping fails loud, a unit bound to an inverter class is not also a machine, and an unbound all-machine case is
  bit for bit unchanged. `SmallSignalHeldUnitsTests.An_H_zero_generator_is_held_instead_of_failing_the_modal_assembly`,
  `Damping_is_aligned_to_the_generators_and_mapped_onto_the_machines`,
  `Damping_with_the_wrong_length_or_a_non_finite_entry_fails_loud`,
  `A_unit_bound_to_an_inverter_class_is_not_also_a_swing_machine`, `An_unbound_all_machine_case_is_unchanged_bit_for_bit`.
- Inverters never alter the machine block, real and synthetic states stay separate, and nothing bound fails loud:
  `SmallSignalModalTests.Enabling_inverters_leaves_the_machine_block_byte_identical_and_adds_control_modes`,
  `Real_and_synthetic_states_are_structurally_separated`, `With_inverters_fails_loud_when_nothing_is_bound`,
  `Gfm_faithful_reduction_is_graceful_at_zero_synthetic_inertia`,
  `SmallSignalLabelAndSocTests.Gfl_reduction_labels_its_second_state_as_the_frequency_rate`, `Include_soc_fails_loud_in_core_and_cli`.
- A defective state matrix reports participation unavailable and never fabricates it:
  `ModalAnalysisDefectiveTests.Damped_path_with_a_jordan_block_reports_participation_unavailable`,
  `Undamped_swing_path_with_a_defective_reduced_matrix_reports_instead_of_throwing`,
  `Non_defective_model_reports_participation_available`.
- Export contract: schema tag, a square real `aMatrix`, a kind tag on every state, aligned participation rows, and the
  exit code and availability flags. `SmallSignalExportContractTests.*`, `N179N180ModalExportAndMessagesTests.*`
  (`Matpower_gb_case_names_the_missing_inertia` pins the no-inertia message).
- The eigen solver matches closed forms, biorthonormality holds to `1e-10`, the vectors solve the eigen equations, and a
  defective matrix fails loud: `GeneralEigenTests.Complex_eigenvalues_of_a_rotation_block_match_to_machine_precision`,
  `Left_and_right_vectors_are_biorthonormal_to_1e_minus_10`, `Right_and_left_vectors_actually_solve_the_eigen_equations`,
  `A_defective_matrix_fails_loud_and_never_fabricates`.

**Impedance scan**

- The PLL transfer, the full dq admittance, the positive-sequence reduction, the Thevenin impedance and the closed-form
  single-converter loop gain each match an independent derivation to 12 decimal places:
  `ImpedanceAnalyticTests.Pll_transfer_matches_independent_arithmetic_to_machine_epsilon`,
  `Dq_admittance_matches_independent_hand_computed_oracle`, `Positive_sequence_equals_modified_sequence_transform_closed_form`,
  `Canonical_thevenin_impedance_is_R_plus_jX_scaled_by_frequency`, `Single_gfl_vs_thevenin_return_ratio_matches_closed_form`.
- The decisive stability check is independent of the winding code: a Routh-Hurwitz count of right-half-plane roots of the
  closed-loop characteristic polynomial equals the Nyquist unstable-mode count at a stable and an unstable SCR:
  `ImpedanceModalAgreementTests.Routh_rhp_count_equals_nyquist_unstable_count_on_a_stable_scr`,
  `Routh_rhp_count_equals_nyquist_unstable_count_on_an_unstable_scr`, `Workstream_c_modal_corroborates_swing_rhp_count_on_the_canonical_case`.
- A strong grid is stable and a weak-grid instability region exists: `ImpedanceScrReferenceTests.Strong_grid_is_stable_and_a_weak_grid_instability_region_exists`.
- Decoupled limit: a unity-power-factor unit's SISO positive-sequence loop equals the dq diagonal average, and under
  coupling the full 2x2 loci differ from the diagonal ones:
  `ImpedanceStabilityPropertyTests.Siso_positive_sequence_equals_dq_diagonal_average_in_decoupled_limit`,
  `Full_2x2_eigenloci_differ_from_the_diagonal_siso_loci_under_coupling`.
- A low `Vd0` is clamped to 0.05 pu and gives a finite admittance:
  `ImpedanceStabilityPropertyTests.Low_vd0_operating_point_is_clamped_and_produces_finite_admittance`.
- The driving point is conjugate symmetric and the DC arm is an explicit error:
  `Driving_point_mirror_is_conjugate_symmetric_on_a_shunt_reactor_network`, `Dc_arm_driving_point_is_an_explicit_error_not_a_silent_zero`.
- Every sample is on the system base and unchanged at unit rating:
  `ImpedanceBaseAndFallbackTests.Samples_are_on_the_system_base_and_unchanged_at_unit_rating`.
- The real-network path uses the real driving point at every frequency and never substitutes the Thevenin: it fails loud
  with its frequency, without a model, with a band that reaches the DC arm, and with a reporting point on `f0`:
  `Real_network_path_uses_the_real_driving_point_at_every_reporting_frequency`, `Real_network_path_without_a_model_fails_loud`,
  `Real_network_band_reaching_the_dc_arm_fails_loud`, `Real_network_reporting_point_on_f0_fails_loud_for_the_dq_arm`,
  `Real_network_failure_propagates_with_its_frequency_instead_of_a_thevenin_substitute`.
- A bandwidth override keeps the authored damping, a damping override is honoured, and any override drops the `Recorded`
  tag: `Bandwidth_override_keeps_the_authored_damping`, `Damping_override_is_honoured`, `Any_override_drops_the_recorded_provenance`.
- Flag contracts: `ImpedanceGridFlagContractTests.Grid_flag_selects_the_real_network_path_and_rejects_typos`,
  `Scr_sweep_flags_are_refused_with_the_real_grid`. Export contract:
  `ImpedanceExportContractTests.Export_locks_the_impedance_stability_contract`.

## Worked example

All numbers come from `case9` (`WSCC-9`), the captures in `docs/_captures/stability/` and the exports of the same runs.
Wall times and file stamps in captures differ by machine and run; every number below is deterministic.

### Classical initial state of machine 1

The captured run:

```
$ gridsim stability case9
GridSim  -  TRANSIENT STABILITY (classical multi-machine)  *  WSCC-9
==================================================================
3 machines (0 converter / power-flow-only / zero-inertia units held)  *  fault bus 7  *  X'd = Anderson-Fouad

  Bus   H sys (s)   |E'| pu  delta0 (deg)
    1      58.509     1.047           0.9
    2      12.288     1.034          14.8
    3       3.853     1.017          11.3
...
```

Recompute the bus 1 row by hand. Inputs: the solved bus 1 voltage `1.04 / 0 deg` (the slack), the solved injection
`71.641 MW + j27.046 MVAr` (bus 1 carries no load, so this is the generator output), `H = 23.64 s`, `MBase = 247.5 MVA`,
`BaseMva = 100 MVA`, `X'd = 0.0608 pu` on the machine base.

```
H_sys   = 23.64 * 247.5 / 100             = 58.509 s                                   matches 58.509
X'd_sys = 0.0608 * 100 / 247.5            = 0.0245657 pu
S_gen   = (71.641 + j27.046) / 100        = 0.71641 + j0.270459 pu
I_t     = conj(S_gen / V_t)               = (0.71641 - j0.270459) / 1.04 = 0.688856 - j0.260057 pu
j X'd I_t = j 0.0245657 (0.688856 - j0.260057) = 0.006388 + j0.016922 pu
E'      = 1.04 + 0.006388 + j0.016922     = 1.046388 + j0.016922 pu
|E'|    = sqrt(1.046388^2 + 0.016922^2)   = 1.046525 pu                                  matches 1.047
delta0  = atan2(0.016922, 1.046388)       = 0.016171 rad = 0.9265 deg                    matches 0.9
Pm_1    = Pe at delta0 = Re(E' conj(I_t)) = 0.71641 pu = 71.641 MW
```

`Pm_1` equals the generator's real output because `X'd` is purely reactive and the Kron reduction is exact. Machines 2 and
3 follow the same steps: bus 2 (`V = 1.025 / 9.2800 deg`, `X'd_sys = 0.1198 * 100 / 192 = 0.0623958`) gives
`E' = 0.999581 + j0.263870`, so `|E'| = 1.033823` and `delta0 = 14.7876 deg`; bus 3 (`V = 1.025 / 4.6648 deg`,
`X'd_sys = 0.1813 * 100 / 128 = 0.1416406`) gives `E' = 0.997096 + j0.199207`, so `|E'| = 1.016800` and
`delta0 = 11.2982 deg`. They match the printed `1.034 / 14.8` and `1.017 / 11.3`.

### The CCT search at bus 7

The classical CCT at bus 7 is 275 steps (550 ms), and the upward probe finds a stable pocket at step 279 (558 ms). The
trace below is the search for a monotone case that holds to step 305 and loses from step 306, with no loss below and no
pocket above, so it is the exact path for that case:

```
Stage 1, bisection:  S(0) S(500) S(250) S(375) S(312) S(281) S(296) S(304) S(308) S(306) S(305)
                     hold, lose,  hold,   lose,  lose,  hold,  hold,  hold,  lose,  lose,  hold    11 trials, lo = 305, hi = 306
Stage 2, downward:   steps 304 .. 289 (16 steps; 304 and 296 are already known)                     14 new trials, all hold
Stage 3, upward:     steps 307 .. 322 (16 steps; 308 and 312 are already known)                     14 new trials, all lose
Total                11 + 14 + 14 = 39 trials        CCT = 305 * 2 ms = 610 ms,  bracket 610 / 612 ms,  no pocket
```

Each trial at `m` steps simulates `0.1 + m*0.002 + 5` seconds in 2 ms steps (2,855 steps at `m = 305`). The capture's
bracket line, `clearing at 550 ms holds synchronism, at 552 ms it is lost`, is `StableSteps * 2 ms` and
`UnstableSteps * 2 ms`.

The pocket flag is real at bus 2:

```
$ gridsim stability case9 --fault-bus 2
...
Critical clearing time for a 3-phase fault at bus 2:  312 ms
(the longest the fault can persist before the fleet loses synchronism on self-clear)
  (bracket: clearing at 312 ms holds synchronism, at 314 ms it is lost)
  NON-MONOTONE: clearing at 320 ms holds synchronism again (a stable pocket above the first loss); the reported CCT is the first loss from below, so do not read a longer clearing time as safe.
...
```

312 ms is step 156, 314 ms step 157, and 320 ms step 160. The upward probe covers steps 158 to 173 and holds at 160, the
highest stable step it sees. The pocket is 3 steps above the first loss, inside the 16-step depth.

### One modal eigenvalue by hand

`gridsim modal case9 --export` writes the `kext` field, the synchronising Laplacian `K_L` in per-unit power per radian on
the system base:

```
K_L  = [  3.6373  -2.1370  -1.5003 ]          M = 2 H_sys = diag( 117.018, 24.576, 7.7056 ) s
       [ -2.0116   3.4156  -1.4040 ]
       [ -1.4419  -1.4296   2.8715 ]
```

The matrix is not symmetric (`K_L[1,2] = -2.1370` against `K_L[2,1] = -2.0116`): the transfer conductances remain. The
state matrix is `A = [[0, ws I],[-M^-1 K_L, 0]]` with `ws = 2 pi 60 = 376.991 rad/s` (`case9` is a 60 Hz case). The row of `A` for the speed of
machine 1 and angle of machine 2 is `-K_L[1,2]/M_1 = 2.1370/117.018 = 0.018262`.

The undamped path deflates the rigid mode. The reference machine is the one with the smallest `|A[speed_i, angle_i]|`,
`3.6373/117.018 = 0.031083` for machine 1. With machine 1 as the reference, the reduced `2 x 2` block that multiplies the
relative angles `(phi_2, phi_3)` has the entries `B[i,l] = -K_L[i,l]/M_i + K_L[1,l]/M_1`:

```
B[2,2] = -3.4156/24.576 + (-2.1370)/117.018  = -0.138982 - 0.018262 = -0.157244
B[2,3] =  1.4040/24.576 + (-1.5003)/117.018  =  0.057129 - 0.012821 =  0.044307
B[3,2] =  1.4296/7.7056 + (-2.1370)/117.018  =  0.185525 - 0.018262 =  0.167263
B[3,3] = -2.8715/7.7056 + (-1.5003)/117.018  = -0.372647 - 0.012821 = -0.385469

trace(B) = -0.542713        det(B) = 0.157244 * 0.385469 - 0.044307 * 0.167263 = 0.0532017
```

The reduced system obeys `s^2 phi = ws B phi`, so `s^2 = ws mu` with `mu` an eigenvalue of `B`. The characteristic
polynomial `mu^2 + 0.542713 mu + 0.0532017 = 0` has discriminant `0.542713^2 - 4 * 0.0532017 = 0.081731`, whose square root
is `0.285885`, so

```
mu_1 = (-0.542713 + 0.285885)/2 = -0.128414           s = +/- j sqrt(376.991 * 0.128414) = +/- j 6.9578 rad/s     f = 1.1074 Hz
mu_2 = (-0.542713 - 0.285885)/2 = -0.414299           s = +/- j sqrt(376.991 * 0.414299) = +/- j 12.4975 rad/s    f = 1.9890 Hz
```

These are the captured undamped rows, `+/-6.9578` rad/s (1.1074 Hz) and `+/-12.4975` rad/s (1.9890 Hz), each with
`sigma = 0` and `zeta = 0`. The two remaining eigenvalues are the deflated rigid mode, appended as exact zeros.

### Damping ratio and frequency from the damped capture

```
$ gridsim modal case9 --damping 5
...
   sigma (1/s)  omega (rad/s)    f (Hz)      zeta   mode
       -0.0995        -6.9568    1.1072    0.0143   oscillatory
       -0.0995         6.9568    1.1072    0.0143   oscillatory
       -0.2967       -12.4929    1.9883    0.0237   oscillatory
       -0.2967        12.4929    1.9883    0.0237   oscillatory
       -0.1027         0.0000    0.0000    1.0000   real
        0.0000         0.0000    0.0000    0.0000   real
...
```

For the first pair, `s = -0.0995 + j6.9568`:

```
|s|  = sqrt(0.0995^2 + 6.9568^2) = sqrt(48.40697) = 6.95751 rad/s
zeta = -sigma / |s|              = 0.0995 / 6.95751 = 0.014301       prints 0.0143
f    = omega / (2 pi)            = 6.9568 / 6.283185 = 1.10721 Hz     prints 1.1072
```

For the second pair, `s = -0.2967 + j12.4929`: `|s| = 12.49642`, `zeta = 0.2967/12.49642 = 0.023743` (prints 0.0237),
`f = 12.4929/6.283185 = 1.98831 Hz` (prints 1.9883). The least-damped mode is the first pair, so the summary line reads
`1.107 Hz, zeta = 0.0143  (poorly damped)`: its `zeta` is below the 0.05 tag threshold. The real eigenvalue `-0.1027` has
`zeta = -sigma/|s| = 1.0000`, and the `0.0000` is the common angle, a zero mode whose `zeta` is defined as 0 because
`|s| <= 1e-12`. The right-half-plane count is 0.

### Impedance: the operating point and one frequency of the loop gain

The default criterion for this grid-following unit is MIMO (`--criterion auto`); the hand calculation below follows the
SISO positive-sequence loop, so it quotes the SISO run of the same unit:

```
$ gridsim impedance-stability case9 --criterion siso
impedance-stability: case IEEE 9-bus (WSCC), inverter bus 2, mode GridFollowingInverter, detail PllOuter, criterion Siso
  operating point : Vd0=1.0250 pu, Id0=0.8283, Iq0=-0.0338 pu
  nyquist         : encirclements 0  unstable-modes 0  GM 19.1 dB  PM -113.2 deg  minDist 0.860  -> STABLE
  SCR boundary    : none in [1.0, 10.0]  (stable across the swept SCR range)
  cross-check (C) : RHP eigenvalues 0  agreement OK  [swing model: X'd Anderson-Fouad, zero damping (no damping data)]
  provenance      : CuratedPlaceholder [VERIFY] pll/current-loop params literature-typical
[exit 0]
```

The converter sits at bus 2, the generator with the largest scheduled `Pg`. Its `Pg = 163 MW` and `MBase = 192 MVA`;
the solved voltage is `1.025 pu`. The export of the same run, `results/exports-docs/impedance-stability.json`, carries
the operating point to full precision (`qPu` 0.034654480825144954, the unit's solved 6.6537 MVAr on its rating):

```
P_pu = 163 / 192  = 0.848958       Q_pu = 0.0346545                     PRatedPu = 192 / 100 = 1.92
Vd0  = 1.025
Id0  = 0.848958 / 1.025  =  0.82825                                      prints 0.8283
Iq0  = -0.0346545 / 1.025 = -0.0338092                                   prints -0.0338
```

The flag-built PLL has `f_bw = 20 Hz` and `zeta = 0.707` (the export's `pllKp` 173.35461510930605 and `pllKi`
15406.211748041926):

```
wn = 2 pi 20 = 125.664 rad/s         kp = 2 * 0.707 * 125.664 / 1.025 = 173.355          ki = 125.664^2 / 1.025 = 15406.2
wci = 2 pi 1000 = 6283.19 rad/s
```

The reporting grid `0.1 .. 250 Hz` at 50 points per decade contains 5 Hz exactly (index 85, zero-based, of 171 points; the geometric mean of
the end points). Evaluate the loop at `f = 5 Hz`, `s = j 31.4159`:

```
G_pll = (kp s + ki) / (s^2 + Vd0 kp s + Vd0 ki) = (15406.2 + j5446.10) / (14804.4 + j5582.25) = 1.032554 - j0.021472
G_ci  = wci / (s + wci) = 1 / (1 + j 0.0050000)                                     = 0.999975 - j0.004999875
Y_pp  = 1/2 (G_ci/Vd0 - G_pll) (Id0 + j Iq0) * PRatedPu
      = 1/2 (-0.056969 + j0.016594) (0.828252 - j0.033809) * 1.92                  = -0.044759 + j0.015043
```

The canonical grid at `SCR = 3`, read in the converter's dq frame at `f + f0 = 65 Hz` (`case9` is a 60 Hz case):

```
|Z_th| = 1 / (3 * 1.92) = 0.173611        R = 0.173611 / sqrt(101) = 0.017275        X = 10 R = 0.172750
Z_pp(5 Hz) = Z_th(65 Hz) = R + j X (65/60)                                          = 0.017275 + j0.187145
L_pp = Z_pp * Y_pp = (0.017275 + j0.187145)(-0.044759 + j0.015043)                   = -0.0035885 - j0.0081165
1 + L_pp                                                                            = 0.99641 - j0.0081165      |1 + L_pp| = 0.99644
```

The exported sample for that frequency has `Z_grid = 0.01727495121892342 + j0.18714530487167041` and
`L = -0.0035884636731112643 - j0.008116473773823404` (the export carries `Z_grid` = `Z_pp`, `Y_conv` = `Y_pp` and
`L` = `L_pp` for all 171 reporting points). The hand value `|1 + L_pp| = 0.99644` at 5 Hz is above the printed `minDist`
of `0.860` (0.8602759878103645 in the export), the smallest of `|1 + L_pp|` and `|1 + L_nn|` over the whole winding grid.
The loop never nears `(-1, 0)`, the winding count is 0, and the verdict is STABLE. The printed `PM` of -113.2 deg is a
SISO read-out at the gain crossover and the encirclement count decides the verdict. The default MIMO run of the same unit
counts 0 encirclements of `det(I + T)` with `minDist` 0.874, and prints `inf` margins.

### The critical SCR

The same unit with a 300 Hz PLL finds a boundary inside the default sweep:

```
$ gridsim impedance-stability case9 --pll-bw 300
impedance-stability: case IEEE 9-bus (WSCC), inverter bus 2, mode GridFollowingInverter, detail PllOuter, criterion Mimo
  operating point : Vd0=1.0250 pu, Id0=0.8283, Iq0=-0.0338 pu
  nyquist         : encirclements -1  unstable-modes 1  GM inf dB  PM inf deg  minDist 0.927  -> UNSTABLE
  SCR boundary    : critical SCR 5.62  (weak-grid instability below this)
  cross-check (C) : RHP eigenvalues 0  agreement differs (structures differ; Routh oracle is decisive - see tests)  [swing model: X'd Anderson-Fouad, zero damping (no damping data)]
  provenance      : CuratedPlaceholder [VERIFY] pll/current-loop params literature-typical
[exit 0]
```

The default sweep has 37 points at steps of `(10 - 1)/36 = 0.25`. The critical SCR is the midpoint between the last
unstable and the first stable sweep point, and the only midpoint that prints `5.62` is `(5.5 + 5.75)/2 = 5.625`: the loop
is unstable at `SCR = 5.5` and stable at `SCR = 5.75`. The Routh-Hurwitz count of the closed-loop characteristic polynomial
(the numerator of `det(I + Y_dq Z_dq)`, no Nyquist winding involved) finds one right-half-plane root at SCR 1.25, 3 and 5.5
and none at 5.75 and 10 (`ImpedanceWeakGridExampleTests.Fast_PLL_MIMO_agrees_with_the_Routh_oracle`). Under
`--criterion siso` the same unit reads STABLE at SCR 3 with a critical SCR of 1.12, `(1.0 + 1.25)/2 = 1.125`: the SISO
pairing drops the PLL's cross-sequence coupling and misses that root.
`gridsim impedance-stability case9 --pll-bw 80 --scr 0.3 --scr-min 0.1` steps by `(10 - 0.1)/36 = 0.275` and prints
`critical SCR 1.61`, `(1.475 + 1.75)/2 = 1.6125`: stable at 0.1 to 0.65, unstable from 0.925 to 1.475, stable from 1.75.
The Routh count agrees at every point; SISO (critical SCR 0.51) is wrong at seven of ten
(`ImpedanceWeakGridExampleTests.MIMO_agrees_with_the_Routh_oracle_and_SISO_does_not_where_the_coupling_matters`).

**Which criterion is the default.** `--criterion auto` runs MIMO for a grid-following converter with its PLL modelled,
because its admittance couples the sequences (the PLL acts on the q axis only, so `Y_dq` is not of the form `a I + b J`),
and SISO for a grid-forming converter, whose admittance is of that form (SISO is then exact and keeps the margins). With
`--outer-loop-only` the PLL is frozen at `1/Vd0`, so `Y_qq = -Id0/Vd0` never rolls off and `|det(I + T)|` grows like `|w|`
along the jw axis: the dq loop is improper, the contour has no finite closing arc, and the winding misses the real
right-half-plane root the closed loop has on `case9` at every SCR from 1 to 10. SISO counts it, so `auto` runs SISO there
and `--criterion mimo` with `--outer-loop-only` exits 2
(`ImpedanceWeakGridExampleTests.Outer_loop_only_SISO_agrees_with_the_Routh_oracle_and_the_improper_MIMO_loop_does_not`).

## Determinism

- **CCT search.** Integer step counts only; no floating-point midpoint is formed, so every CPU visits the same clearing
  times, and every trial is memoised. The trial's clearing time is formed as `steps * step`, the product the integrator
  uses for its own sample times, and the fault edge tolerance of `1e-6` steps absorbs the one-ulp rounding of
  `onset + clearing`.
- **Machine preparation.** Scalar `Math.SinCos`, `Math.Cos` and `Math.Sin` and sequential Kron elimination in a fixed node
  order; `KronReduction` updates every entry with the same expression in a fixed order.
- **Modal analysis.** The assembler is scalar. Francis QR is scalar. The inverse-iteration factorisation
  (`DenseComplexLuSolver.FactorDense`, `src/GridSim.Core/Numerics/ComplexLinearSolvers.cs`) calls the vectorised
  `SimdKernels.SubtractScaledComplexColumn` kernels, which perform exactly the scalar's IEEE operations per lane
  (`N186DenseComplexFactorBitIdentityTests`). The inverse iterations run in parallel for orders of 32 and above, and each is
  the same deterministic sequence wherever it runs, so one worker and many workers give the same bits. The eigenvalues, eigenvectors and participation of 13 fixtures (`case9`,
  `case14`, `case118`, `case300` undamped and damped, random matrices of order 6, 23, 64 and 130, and a repeated-eigenvalue
  block) are pinned by SHA-256 digest, with the same digests in normal mode, with AVX2 off and with hardware intrinsics off:
  `N186ParticipationBitIdentityTests.Every_eigenpair_and_participation_factor_is_bit_identical_to_1_95_133`,
  `One_worker_and_many_workers_give_the_same_bits`.
- **Impedance scan.** The canonical path is closed-form `System.Numerics.Complex` arithmetic over fixed grids, with no SIMD
  kernel; the real-network path adds the harmonic `Y(h)` solve, specified in
  [ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) and ch. 19.
- **Classical swing kernel, scope of the guarantee.** The electrical-power sum in `ClassicalMultiMachine` calls
  `SimdKernels.Dot` and `SimdKernels.SinCos`. `Dot` sums in a width- and FMA-dependent order, and `SinCos` vectorises with
  `Vector<double>.Count` lanes and falls back to `Math.SinCos` on the tail. With 3 machines (`case9`) both kernels run scalar sums on the AVX2 and AVX-512 tiers (`Vector<double>.Count` is 4 there) and agree. On
  the Sse128 tier (`Vector<double>.Count` is 2) `SinCos` vectorises two of the three angles with its minimax polynomial, accurate to 1e-13 and
  not bit-identical to `Math.SinCos`, and no test compares tiers on this kernel. From 4 machines on (`uk` has 13) the tiers can differ in the last bits of a trajectory. A CCT is an integer step and a trial is a verdict, so a result changes only if a trial sits within
  rounding of the `2 pi` limit. Repeated runs on one machine are bit-identical
  (`ClassicalCctStepQuantisedTests.The_search_is_bit_identical_across_repeated_runs`).
- **Not deterministic by design.** The wall time printed by `stability --rms --clear`, and the `meta` stamp (time, machine,
  commit) in the impedance export.

## Cross-validation

Three sidecars in `tools/crossval/` re-derive an export with an independent implementation. Each reads a JSON written by
the verb and exits non-zero on a mismatch; the user view of running them is
[ch. 27, Validation tools](<../manual/0027 Validation tools.md>).

| Sidecar | Export | Reference | Tolerance |
|---|---|---|---|
| `crossval_classical.py` | `gridsim-classical-export/1` from `stability <case> --export <file>` | SciPy `solve_ivp` RK45 (`rtol` 1e-9, `atol` 1e-9, `max_step` 0.01 s) re-integrating the identical reduced swing from the exported `H`, `Pm`, `\|E'\|`, `delta0` and `G + jB` | `--tol` 1e-3 rad on the maximum rotor-angle difference over every sample |
| `crossval_smallsignal.py` | `gridsim-smallsignal/1` from `modal <case> --export <file>` | `scipy.linalg.eig` (LAPACK) of the exported `aMatrix` | per-mode relative distance `\|gs - sp\| / max(\|sp\|, 1)` at most `4096 eps = 9.09e-13` times `max(1, cond(V))`; eigenvalues paired by optimal assignment (`linear_sum_assignment`); participation compared and reported but not gated |
| `crossval_impedance.py` | `gridsim-impedance-stability/1` from `impedance-stability <case> --enableExport[=file]` | NumPy only, no GridSim code: rebuilds `G_pll`, `G_ci`, `Y_pp`, `Y_nn`, `Z_pp = Z_th(f + f0)`, `Z_nn = Z_th(f - f0)`, `L`, the winding count of `(1 + L_pp)(1 + L_nn)` and the critical SCR from `converterParams` | `rtol = atol = 4096 eps = 9.09e-13`; the winding must give the exported unstable-mode count and stable flag; the critical SCR must agree within 1.5 sweep steps |

The classical export runs the swing from the machine of lowest inertia kicked by 0.10 rad, at a 1 ms step for 5 s, and
writes every 50th sample. The small-signal gate scales by `cond(V)` because the agreement of two non-symmetric QR
implementations is limited by the conditioning of the eigenvector matrix, not by machine epsilon: it is a per-mode
relative check, not a bit-identity check. The sidecar's own measurements over every bundled case it exports, damped and
undamped, need at most 0.2165 machine epsilons of tolerance after the `cond(V)` scaling (`gb` with `--damping 2`), and the
near-defective undamped cases (`case9` has `cond(V) = 1.928e10`) need at most 0.0874, so the default is 18,900 times
the worst measured need, to three significant figures. The impedance sidecar's worst measured need over 25 flag-built grid-following scans is 28.06
machine epsilons (re-measured 2026-10-31; 25.55 before the frame pairing and the MIMO default); the default is 146 times
that, to three significant figures. It exits 77 (not checked) when the export has no `converterParams`, which is the case for a grid-forming export,
never a silent pass. A `--grid real` export does carry `converterParams` and has `canonical: false`; the sidecar then
compares `Y_conv` and `L = Z_grid(exported) * Y_conv` only, says so on its first line, and does not re-derive the grid
impedance, the winding count or the boundary (`gridsim impedance-stability case9 --grid real` gives 342 comparisons and
`RESULT: PASS`). It re-derives the grid impedance, the winding and the boundary on the canonical grid only.

The captured impedance run, on the export of `gridsim impedance-stability case9 --criterion siso --enableExport=impedance-stability-export.json`
(the sidecar re-derives the winding and the boundary for a SISO export on the canonical grid; for a MIMO export it compares
the per-frequency quantities only):

```
$ python tools/crossval/crossval_impedance.py --export impedance-stability-export.json
impedance cross-validation: case 'IEEE 9-bus (WSCC)', criterion Siso, mode GridFollowingInverter
  converterParams present: True   grid impedance: canonical Thevenin
  per-frequency: max|dZ|=0.00e+00  max|dY|=2.29e-16  max|dL|=1.11e-16  (rtol=9.09495e-13 atol=9.09495e-13)
  winding: numpy encirclements=0  export unstable-modes=0  export stable=True  -> OK
  boundary: numpy critical SCR=None  export=NaN  found=False  -> OK
  independent comparisons made: 515
RESULT: PASS - all within tolerance
[exit 0]
```

The 515 comparisons are 171 frequencies times three quantities (`Z`, `Y`, `L`), the winding count and the boundary.
The largest difference, `2.29e-16` pu, is 1.03 machine epsilons. The small-signal and classical sidecars need SciPy and are not
captured here.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Dynamics/Distributed/ClassicalInit.cs` | `ClassicalInit` | Machine selection, load and held-unit admittances, Kron reduction, `E'`, `delta0`, `Pm`, faulted admittance; `CriticalClearingBracket` and its constants |
| `src/GridSim.Core/Dynamics/Distributed/ClassicalMultiMachine.cs` | `ClassicalMultiMachine` | RK4 swing kernel, fault window, COI loss-of-synchronism test |
| `src/GridSim.Core/PowerFlow/KronReduction.cs` | `KronReduction` | Sequential single-node elimination onto the retained nodes |
| `src/GridSim.Core/Dynamics/Models/CctSearch.cs` | `CctSearch` | Bisection, downward verification, upward probe, memoisation |
| `src/GridSim.Core/Dynamics/Models/RmsTransientStability.cs` | `RmsTransientStability`, `CctBracket`, `RmsExciter` | RMS study and CCT, the bracket record, exciter wrapper (see ch. 24) |
| `src/GridSim.Core/Dynamics/Models/MultiMachineSubtransient.cs` | `MultiMachineSubtransient` | RMS integrator, synchronism test, `StageTimeToleranceSteps` |
| `src/GridSim.Core/Dynamics/SmallSignal/SmallSignalAssembler.cs` | `SmallSignalAssembler`, `SmallSignalModel` | Machine preparation, `K_L`, `A`, inverter assembly with COI coupling |
| `src/GridSim.Core/Dynamics/SmallSignal/StateVector.cs` | `StateKind`, `StateLabel` | State kinds and the real/synthetic separation |
| `src/GridSim.Core/Dynamics/SmallSignal/LinearInverterModel.cs` | `LinearInverterModel`, `InverterLinearBlock` | Grid-forming and grid-following linear blocks |
| `src/GridSim.Core/Dynamics/SmallSignal/ModalAnalysis.cs` | `ModalAnalysis`, `Mode`, `ModalResult` | Rigid-mode deflation, damping ratio, frequency, participation, right-half-plane count |
| `src/GridSim.Core/Numerics/GeneralEigen.cs` | `GeneralEigen` | Balancing, Hessenberg, Francis QR, inverse iteration, biorthonormal vectors, defect detection |
| `src/GridSim.Core/Dynamics/Impedance/IConverterImpedanceModel.cs` | `IConverterImpedanceModel`, `ConverterOperatingPoint`, `ImpedanceModelDetail` | Admittance interface, guarded operating point, detail levels |
| `src/GridSim.Core/Dynamics/Impedance/GridFollowingImpedance.cs` | `GridFollowingImpedance` | SRF-PLL and current-loop admittance |
| `src/GridSim.Core/Dynamics/Impedance/GridFormingImpedance.cs` | `GridFormingImpedance` | Virtual-impedance and power-loop admittance |
| `src/GridSim.Core/Dynamics/Impedance/GridImpedanceAssembler.cs` | `GridImpedanceAssembler` | Canonical Thevenin, real-network driving point, dq transform, DC-arm guard |
| `src/GridSim.Core/Dynamics/Impedance/MinorLoopNyquist.cs` | `MinorLoopNyquist`, `NyquistVerdict` | SISO and MIMO winding count, margins |
| `src/GridSim.Core/Dynamics/Impedance/ImpedanceStabilityScan.cs` | `ImpedanceStabilityScan`, `ImpedanceStabilityOptions`, `ImpedanceStabilityResult` | Frequency grids, loop gain, SCR sweep, critical SCR, modal cross-check hook |
| `src/GridSim.Core/Dynamics/ConverterDefaults.cs` | `ConverterDefaults` | Placeholder converter parameters of the three converter models |
| `src/GridSim.Core/IO/SmallSignalExport.cs`, `ImpedanceStabilityExport.cs` | exporters | `gridsim-smallsignal/1` and `gridsim-impedance-stability/1` (formats in [ch. 10](<0010 File and wire formats.md>)) |
| `src/GridSim.Core/IO/IeeeCases.cs` | `IeeeCases` | WSCC-9 case and its Anderson-Fouad `X'd` |
| `src/GridSim.Cli/CliDefaults.cs` | `CliDefaults` | CLI study timing, synthetic machine data, placeholder converter values |
| `src/GridSim.Cli/Program.cs` | `RunStability`, `RunRmsStability`, `RunStabilityScreen`, `RunModal`, `RunImpedanceStability` | Verb wiring, output text, exit codes |
| `tools/crossval/crossval_classical.py`, `crossval_smallsignal.py`, `crossval_impedance.py` | sidecars | Independent re-derivation of the three exports |

## Limits

- The classical model holds `|E'|` constant and has no governor, exciter or damping, and the modal model shares that
  structure; the RMS engine has exciters and PSS1A but no governors.
- No bundled case carries sub-transient machine data, so every `--rms` result uses GENROU data synthesised from `X'd`, and
  every case but `case9` uses a synthetic `X'd` of 0.30 pu.
- A study applies one self-clearing bolted three-phase fault at one bus; it has no fault impedance, line trip, reclosing or
  event sequence.
- A trial ends 5 s after clearing, so a loss of synchronism that develops later is not seen.
- The CCT is the first loss from below only when no stable pocket is wider than 16 steps (32 ms).
- The classical swing kernel's trajectories are not guaranteed bit-identical across SIMD tiers for 4 or more machines, or
  for 3 on the Sse128 tier, and no test pins them across tiers.
- `modal` applies one damping value to every machine, has no exciter or governor states, and reports participation
  factors as unavailable for a defective state matrix; the right-half-plane threshold `1e-9` is absolute and is not scaled
  by the size of `A` (R172).
- The modal grid-following block is an SRF-PLL archetype substitution of a transport delay, and the grid-forming damping
  and synthetic-inertia coefficients are absolute MW/Hz and MW.s/Hz that are not scaled by the unit's rating (R085).
- Every converter placeholder (the 20 Hz PLL, damping 0.707, the GFM virtual reactance, power-loop bandwidth and droop) is a
  literature-typical `CuratedPlaceholder`, not a measured setting.
- `impedance-stability` models one converter at one bus against a canonical Thevenin grid or the real network's driving-point
  impedance; it does not scan several converters together, and its converter parameters are placeholders unless a bound asset
  class gives recorded values.
- The operating point uses the generator's scheduled `Pg` and `Qg` with the solved voltage, not the solved reactive output of
  a voltage-controlled bus, and the grid-forming admittance ignores the operating point.
- The Nyquist count equals the unstable-mode count only for individually stable grid and converter (`P = 0`); the models
  satisfy that, but a future model with an imaginary-axis pole needs contour indentation the code does not perform.
- The SCR boundary is reported only for a sweep that goes from unstable to stable as SCR rises, and the console line reads
  `stable across the swept SCR range` for an all-unstable sweep and for `--grid real`, which has no sweep.
- The modal cross-check always uses the synthetic 0.30 pu `X'd` and zero damping, and compares a different model structure
  from the scan, so `agreement OK` on a case with no inverter class means only that both counts are 0.
- The real-network scan needs `--fmin` above `1e-4 * f0` (0.005 Hz at 50 Hz) and has no SCR sweep.
- `stability --fault-bus N` for a bus that does not exist is refused before any solve (exit 2, on every engine), and a
  negative, non-finite or over-long `--clear` is refused as a bad flag value (exit 2).
- The classical `--screen` header counts every generator as a machine, including held units.
- `tools/crossval/crossval_impedance.py` and its docstring disagree: the docstring says it exits 77 for a grid-forming,
  asset-class or real-network export, but the script exits 77 only when `converterParams` is absent (a grid-forming export),
  so a `--grid real` export fails with exit 1 instead of exiting 77.

## See also

- [ch. 15, RMS, modal and impedance stability](<../manual/0015 RMS, modal and impedance stability.md>) - the user view of the three verbs, with every output line explained
- [ch. 24, Standard dynamic-model library](<0024 Standard dynamic-model library.md>) - the machine, exciter, stabiliser and converter models behind `--rms`
- [ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>) and [ch. 3, Power flow](<0003 Power flow.md>) - the generator fields and the solved operating point
- [ch. 13, Asset dynamics and data grounding](<0013 Asset-dynamics data grounding.md>) - the asset classes the inverter models read
- [ch. 19, Harmonics](<0019 Harmonics.md>) - the `Y(h)` stack behind the real-network driving point
- [ch. 21, Electromagnetic transient](<0021 Electromagnetic transient (EMT) simulation.md>) - the average-value converter, the third representation of a converter
- [ch. 10, File and wire formats](<0010 File and wire formats.md>) - the schemas of the exports
- [ch. 11, Validation](<0011 Validation.md>) and [ch. 16, Physics conformance](<0016 Physics conformance and the constrained estimator.md>) - how the engines are validated
- [ch. 19, CLI reference](<../manual/0019 CLI reference.md#stability>), [`modal`](<../manual/0019 CLI reference.md#modal>) and [`impedance-stability`](<../manual/0019 CLI reference.md#impedance-stability>) - every flag and exit code
- [ch. 27, Validation tools](<../manual/0027 Validation tools.md>) - running the three sidecars
