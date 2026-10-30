# 27. Battery energy storage

This chapter specifies the battery model in `src/GridSim.Core/Storage/` and the way studies use it: the cell
equivalent circuit, the six chemistry presets, pack assembly and its limits, the thermal and auxiliary-load model, the
ageing model, efficiency and round trip, the dispatch policies and the linear-programme scheduler, the price series,
and the fleet that carries storage sites through a time series of AC power flows. A battery's grid behaviour is the
output of its chemistry. How much power it delivers, for how long and at what temperature is set by the cell's
open-circuit-voltage curve, its impedance and how that rises in the cold, its rate limits, its thermal mass and its
ageing. The converter is the output stage, bounded by what the DC side can deliver.

The model has four consumers:

- `gridsim chain --bess` and `gridsim gda-replay --bess` run sites through `StorageFleet`
  (`src/GridSim.Core/Storage/StorageFleet.cs`) and report them. The user chapter is
  [ch. 13, Battery storage studies](<../manual/0013 Battery storage studies.md>).
- `LiveSim` carries a `StorageFleet` tick to tick (the `storage` constructor argument); each `SimFrame` carries the
  site records. No CLI verb attaches one.
- The grid-following and grid-forming inverter assets draw through an `IDcSource`, and `BatteryDcSource` is the pack
  behind the inverter.
- `RmsConverterUnit.Store` puts an `IDcSource` behind an RMS converter.

Cell and pack state are SI throughout: volts, amperes, ohms, seconds, joules, kelvin. Dispatch and reporting are in
MW and MWh, discharge positive. Every chemistry preset is `ProvenanceTag.CuratedPlaceholder` with a `[VERIFY]` note:
a representative cell for its class, fit for comparing chemistries and exercising the model, to be replaced by a
specific cell's datasheet or fitted test data before a result is quoted for a real asset. The tag is defined in
[ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>).

## Model

### Cell: equivalent circuit (`CellModel`)

One representative cell carries the state of a pack: state of charge `SoC` (fraction of the present, faded capacity),
the two polarisation voltages `V1` and `V2`, temperature `T` (K) and capacity fade `fade` (fraction of rated capacity
lost). Current `I` is per cell and positive on discharge. Tables (`LookupTable`) are piecewise linear on strictly
ascending `x`, clamped at both ends, and need at least two points.

```
Rg   = 8.314462618 J/mol/K          Tref = 298.15 K
OCV(SoC, T)  = OCV25(SoC) + dUdT(SoC) * (T - Tref)
f_res(T)     = exp(Ea / Rg * (1/T - 1/Tref))                    Arrhenius, 1 at 25 C
R0           = R0_25(SoC) * f_res(T) * (1 + g * fade)
R1           = R1_25      * f_res(T) * (1 + g * fade)
R2           = R2_25      * f_res(T) * (1 + g * fade)
Q(fade)      = Q_rated * (1 - fade)
E            = OCV - V1 - V2                                    Thevenin source behind R0
V_t(I)       = E - I * R0
```

`Step(I, dt)` advances the electrochemical state by `dt` seconds at constant `I` and returns the heat of the step. R0,
R1, R2, the OCV and the entropic coefficient are evaluated once, at the start-of-step state.

```
a_k   = exp(-dt / tau_k)                                        k = 1, 2
V_k+  = V_k * a_k + I * R_k * (1 - a_k)                         exact zero-order hold
Qirr  = I^2 R0 dt + 0.5 (V1^2 + V1+^2) / R1 dt + 0.5 (V2^2 + V2+^2) / R2 dt
Qrev  = I * T * dUdT(SoC) * dt
heat  = Qirr + Qrev                                             J per cell
```

`Qirr` integrates the dissipation in each RC resistor with a trapezoid on the squared voltage. `Qrev` is the
reversible entropic term. Where `dU/dT` is negative, as for LFP and NMC, it cools the cell on discharge and heats it on charge. The charge and rate effects
are:

```
cRate  = I / Q_rated
I_eff  = I * (cRate / Cref)^(kP - 1)       I > 0, kP > 1 and cRate > Cref     Peukert
I_eff  = I                                 I > 0 otherwise
I_eff  = I * CE                            I < 0                              coulombic efficiency on charge
sd     = SelfDischargePerMonth / (30 * 86400)                                 per second
SoC+   = clamp(SoC - I_eff * dt / (3600 * Q(fade)) - sd * dt, 0, 1)
```

`Q_rated` in the Peukert test is the unfaded rated capacity. Self-discharge acts on every step, charging, discharging
or idle, and acts below the SoC floor and above the ceiling too: the window is enforced on current, not on state.
`Step` never changes `T`; the pack's thermal model does.

Rate capability is not a parameter. At higher current the terminal voltage sags further through `R0`, `V1` and `V2`,
so the cut-off voltage is reached with less charge delivered; the resistance rises in the cold through `f_res`; lead-acid
adds `kP` for the rate effect resistance alone does not capture. `NominalVoltage` is `OCV25(0.5)`. `NominalEnergyWh`
is the cell energy `Q_rated * (SocMax - SocMin) * mean(OCV25)`, with the mean taken over 100 mid-point samples of the
SoC window.

### Pack: assembly and sizing (`PackModel`)

A pack is `Ns x Np` identical cells (series count by parallel strings), lumped so that one representative cell carries
the state. `PackModel.Sized` builds a pack for a target nominal energy and DC voltage:

```
Ns = max(1, round(Vdc / Vnom_cell))
Np = max(1, round(E_target / (Ns * Ecell_nominal)))
NominalEnergyWh = Ecell_nominal * Ns * Np
```

`round` is `Math.Round`, which rounds halves to even. The pack nominal energy is close to, not equal to, the target.
The rated power `RatedPowerW` defaults to `NominalEnergyWh * MaxDischargeCRate`; the CLI and `DcSources` pass the
site's AC rating instead. It scales the auxiliary load and bounds the state-of-power search. Sizing for 10 MWh at
1000 V, from `docs/_captures/bess/api-probe-sizing.txt`:

```
$ API probe (GridSim.Core, Release build): sizing
chemistry   Vnom_V  cell_Wh   Ns   Np  pack_MWh
LFP         3.2900   824.40  304   40   10.0247
NMC         3.7800   219.10  265  172    9.9868
LTO         2.3000    64.87  435  354    9.9891
Na-ion      3.1000   466.73  323   66    9.9497
VRFB        1.4000   980.00  714   14    9.7961
Lead-acid   2.0400  1603.84  490   13   10.2164
[exit 0]
```

The BMS current limits `MaxDischargeCRate * Q(fade)` and `MaxChargeCRate * Q(fade)` are per cell in amperes, so they are
multiples of the Ah capacity, not of the windowed nominal energy. For the LFP pack above, 280 A and 140 A per cell over 40
strings at `304 * 3.29` = 1000.2 V are 11.2 MW and 5.6 MW before sag.

Pack quantities:

```
AvailableEnergyWh = max(0, SoC - SocMin) * Q(fade) * Np * Ns * Vnom_cell
OpenCircuitVoltage = Ocv(SoC, T) * Ns                      (no V1, V2)
```

### Pack: one step (`PackModel.Step`)

`Step(P, dt)` delivers a DC power request `P` (W, discharge positive) for `dt` seconds. It solves the cell current at the
start-of-step state, applies the battery-management limits, advances the cell, ageing and temperature, and reports what
was delivered and which limit bound.

1. **Per-cell power and unconstrained current.** `p = P / (Ns Np)`, with `E` and `R0` at the start of the step. The cell
   current solves `E I - R0 I^2 = p`, taking the smaller root:

   ```
   disc = E^2 - 4 R0 p
   I    = (E - sqrt(disc)) / (2 R0)          disc >= 0
   I    = E / (2 R0)                         disc < 0: beyond maximum power transfer; reason = "maximum power transfer"
   ```

2. **Limits.** The current limits (A, per cell, both positive) are:

   ```
   derate = clamp((TMax - Tc) / max(1e-9, TMax - TDerate), 0, 1)           Tc = cell temperature, C
   iDis   = min(MaxDischargeCRate * Q(fade) * derate, max(0, (E - VMin) / R0))
   iChg   = min(MaxChargeCRate    * Q(fade) * derate, max(0, (VMax - E) / R0))
   iDis   = 0   when SoC <= SocMin
   iChg   = 0   when SoC >= SocMax or Tc < TMinCharge
   ```

   The step never carries the SoC across its window. The current that would land exactly on the edge caps the step,
   so the battery-management system tapers into the limit:

   ```
   chargeRoom    = (SocMax - SoC) * 3600 * Q(fade) / dt / max(CE, 1e-9)
   dischargeRoom = (SoC - SocMin) * 3600 * Q(fade) / dt
   dischargeRoom = (dischargeRoom * Iref^(kP-1))^(1/kP)       kP > 1 and dischargeRoom > Cref * Q(fade);  Iref = Cref * Q_rated
   iChg = min(iChg, max(0, chargeRoom))        iDis = min(iDis, max(0, dischargeRoom))
   ```

   The last Peukert line inverts `I_eff = I (I / Iref)^(kP-1)`, so the discharge room is a current that depletes the
   charge to the floor and no further. Self-discharge is not in the room, so a pack at the floor drifts a little below it.

3. **Clip.** If `I > iDis` then `I = iDis`; if `I < -iChg` then `I = -iChg`. The limit is named in this order, and a
   reason already set by step 1 is kept:

   | Direction | Reason, first match wins |
   |---|---|
   | Discharge | `SoC floor` when `SoC - SocMin < 1e-3` or `iDis` equals the discharge room to `1e-9` A |
   | | `temperature derate` when `derate < 1` and `iDis` equals `MaxDischargeCRate * Q * derate` to `1e-9` A |
   | | `cut-off voltage` when `iDis` equals `(E - VMin) / R0` to `1e-9` A |
   | | `current limit` otherwise |
   | Charge | `SoC ceiling` when `SocMax - SoC < 1e-3` or (`iChg > 0` and `iChg` equals the charge room to `1e-9` A) |
   | | `too cold to charge` when `Tc < TMinCharge` |
   | | `charge voltage` when `iChg` equals `(VMax - E) / R0` to `1e-9` A |
   | | `current limit` otherwise |

4. **Advance.** `vCell = E - I R0` at the start-of-step state. `heatJ = CellModel.Step(I, dt) * Ns * Np`. If ageing is
   tracked, `AgeingModel.Advance(cell, I, dt)` runs next, on the post-step SoC and the pre-thermal temperature. The
   thermal model then sets the new temperature (next section).

5. **Report.**

   ```
   DcPowerW        = vCell * I * Ns * Np
   CurrentA        = I * Np               TerminalVoltageV = vCell * Ns       HeatW = heatJ / dt
   Limited         = reason is set  and  |DcPowerW - P| > 1e-6 * max(1 W, |P|)
   ```

   `vCell` is the start-of-step terminal voltage, so an unconstrained step delivers the requested power exactly (the
   quadratic in step 1 solves for it). A limit that binds but costs less than `1e-6` of the request is not reported.

`InstantaneousLimitsW()` is the power at the battery-management limits now, with no look-ahead, cheap enough to query
every control step. With `iDis` and `iChg` from the limits above (the discharge current also capped at `E / (2 R0)`,
the maximum-power-transfer current):

```
DischargeW = (E * iDis - R0 * iDis^2) * Ns * Np
ChargeW    = (E * iChg + R0 * iChg^2) * Ns * Np
```

`StateOfPower(horizon, substeps = 40)` is the largest constant DC power the pack sustains for the horizon with no limit
binding, for discharge and charge (both positive). `Sustains(P)` clones the pack without ageing, steps it `substeps`
times of `horizon / substeps` and fails if any step is `Limited`. The search brackets `[0, max(RatedPowerW, 20 *
NominalEnergyWh)]`. It first requires 1 W to be sustainable, else the state of power is 0, then bisects for at most 50
iterations and stops when the bracket is narrower than `1e-4 * max(1, hi)`. Because it simulates the horizon, it
includes the sag building up over it, the SoC window and heating. A 10 s pulse and a 30 minute delivery give different
answers, and chemistries differ exactly there. For packs sized to 1 MWh at 1000 V, from 90 % SoC, from
`docs/_captures/bess/api-probe-sop.txt`:

```
$ API probe (GridSim.Core, Release build): sop
chemistry  10 s pulse  30 min   2 h   10 s at -10 C   (MW, discharge)
LFP         1.104    1.071    0.465    0.955
NMC         2.258    1.758    0.458    1.725
LTO         6.210    1.771    0.452    4.851
Na-ion      2.352    1.716    0.472    1.998
[exit 0]
```

### Thermal model and auxiliary load

The pack temperature is one lumped mass per pack, advanced by an explicit Euler step inside `PackModel.Step`. The cells
are at the pack temperature. Heat enters from the cells, leaves passively to ambient, and leaves actively through the
HVAC while the temperature is above its setpoint:

```
mcp       = MassKg * SpecificHeat * Ns * Np                           J/K
passiveW  = PassiveCoolingWPerK * Ns * Np * (T - (Tambient + 273.15))
T_noHvac  = T + (heatJ - passiveW * dt) / mcp
hvacW     = min((T_noHvac - Tset) * mcp / dt, HvacCapacityFraction * RatedPowerW)    HvacCop > 0 and T_noHvac > Tset
hvacW     = 0                                                                         otherwise
T+        = T_noHvac - hvacW * dt / mcp
```

`hvacW` is heat removed, capped by the HVAC capacity as a fraction of the rated power. `Tset = HvacSetpointC + 273.15`.
Chemistries with `HvacCop = 0` (VRFB, lead-acid) have no HVAC and cool only passively. The pack ambient `AmbientC`
defaults to 25 C. The pack heat limits power through the temperature derate in the current limits.

The auxiliary (balance-of-plant) electrical load of the step, in W, is:

```
aux = StandbyFraction * RatedPowerW
    + (HvacCop > 0 ? hvacW / HvacCop : 0)
    + PumpFraction * |DcPowerW|
```

Standby covers controls, the battery-management system and idle fans. The HVAC term is the electrical power that removes
`hvacW` of heat. The pump term is the electrolyte pumps of a flow battery as a fraction of the power moved. `StorageFleet`
adds the mean auxiliary power to the site's bus as demand (see Time series). It is not in the round-trip figure.

### Ageing (`AgeingModel`, `Rainflow`)

`AgeingModel.Advance(cell, I, dt)` accumulates calendar fade and cycle fade and writes
`cell.CapacityFade = min(0.8, calendar + cycle)`. The cap `MaxCapacityFade` is 0.8: beyond it the cell would have almost
no capacity left and its resistance growth stops being meaningful. The reported `CalendarFade` and `CycleFade` are not
clamped. Capacity and resistance both feel the fade on the next step (`Q(fade)` and `1 + g * fade`).

```
Arr(E, T)  = exp(-E / Rg * (1/T - 1/Tref))                        T = temperature before this step's thermal update
stress     = k_cal * Arr(E_cal, T) * max(0.05, 1 + a_soc * (SoC - 0.5))     SoC after this step's cell update
tEq        = (calendar / stress)^2 days          if calendar > 0, else 0
calendar+  = stress * sqrt(tEq + dt / 86400)
```

The calendar law is `k_cal * sqrt(days)`, so one day is `sqrt(1/365)`, 5.2 %, of a year's fade at constant stress. For LFP at 25 C
and 50 % SoC that is `0.001 * sqrt(365)` = 1.91 % for a year; the captured LFP day reads 0.1369 %. The equivalent-time form maps the fade so far to the age at which the present
stress would have produced it, so the result does not depend on the step size and follows time-varying stress. At 25 C
and 50 % SoC, `calendar = k_cal * sqrt(days)` exactly, for any step size. The floor `0.05` on the SoC stress factor keeps
a negative `a_soc` from driving stress to zero.

```
if I != 0:
  cRate   = |I| / Q_rated
  cFactor = 1 + CRateSensitivity * max(0, cRate - 1)
  EFC     = |I| * dt / 3600 / (2 * Q_rated)                         equivalent full cycles of this step
  cycle+  = cycle + (0.2 / N80) * EFC * Arr(E_cyc, T) * cFactor
```

`0.2` is `EndOfLifeFade`, the 20 % fade that defines `N80`, the equivalent full cycles to 80 % capacity. One equivalent
full cycle is a charge plus a discharge of the full capacity (`ThroughputPerFullCycle = 2`). Cycle fade is linear in
throughput and independent of depth of discharge; the depth effect comes from rainflow counting below.

**Resistance growth** is `1 + g * fade` on R0, R1 and R2, with `g = ResistanceGrowthPerFade`. At the 20 % end-of-life
fade the resistance has grown by `g * 0.2`, 50 % for the default `g = 2.5`.

**Rainflow** (`Rainflow.Count`, ASTM E1049-85 three-point method) counts cycles in a SoC history as (range, count)
pairs. The history is reduced to turning points first: a value equal to the last is dropped, and a value continuing the
direction of the last segment replaces the last point. Each point is then pushed on a stack. While the stack holds at
least three points, with `X` the latest range and `Y` the one before it:

- if `X < Y`, stop;
- if the stack holds exactly three points, `Y` contains the start point: count `(Y, 0.5)` and drop the first point;
- otherwise count `(Y, 1.0)` and drop the two points of `Y`.

What remains on the stack at the end is the residue, and each of its consecutive ranges counts `(range, 0.5)`.
`AgeingModel.CycleFadeFromRainflow` turns the counts into a depth-aware cycle fade:

```
fade_rainflow = sum over (d, n) of  n * (0.2 / N80) * |d|^beta           beta = DodExponent
```

A full-window cycle, `d = 1`, costs the reference amount. Many shallow cycles cost less than one deep one for
`beta > 1`. The fleet counts the SoC trace of the whole run, the starting SoC followed by the SoC after every substep.
The rainflow fade is a report, shown beside the throughput fade; the fade fed back into the cell is calendar plus
throughput cycle fade.

### Converter (`BatteryDcSource`)

`BatteryDcSource` (`src/GridSim.Core/Assets/DcSource.cs`) puts a pack behind a converter of one-way efficiency `eta`,
which must lie in `[0.5, 1]` (anything else, or a value that is not finite, is refused), default 0.98. Its power is AC side, discharge positive:

```
Draw(acMw, dt):   dcW = acMw >= 0 ? acMw * 1e6 / eta : acMw * 1e6 * eta
                  r = pack.Step(dcW, dt)
                  delivered = r.DcPowerW >= 0 ? r.DcPowerW * eta / 1e6 : r.DcPowerW / eta / 1e6
Headroom():       (Up, Down) = (DischargeW * eta / 1e6, ChargeW / eta / 1e6)       from InstantaneousLimitsW()
```

A discharge draws `1/eta` more from the pack than it delivers; a charge stores `eta` less than it absorbs. `Reset()`
restores the pack from a clone made at construction, which has no ageing tracking.

### Efficiency and round trip

Energy is lost in four places: the converter in each direction (0.98), the cell's series resistance and the two
polarisation resistors (heat), the coulombic efficiency on charge, and the balance-of-plant load. The first three set the
round trip. For a lithium preset the product of the converter and the coulombic efficiency bounds it: for LFP,
`0.98^2 * 0.995 = 0.9556`, before any cell resistance.

The scheduler's envelope uses a linearised pack efficiency (next section). The study report measures the actual run
(`StorageFleet.Summaries`), with `out` and `in` the AC energy delivered and absorbed, summed over steps as
`max(delivered, 0) * h` and `max(-delivered, 0) * h`:

```
storedChange = AvailableEnergyWh(end) / 1e6 - AvailableEnergyWh(start) / 1e6            MWh
roundTrip    = (out + max(0, storedChange)) / (in + max(0, -storedChange))              NaN when in <= 1e-9 MWh
EFC          = (out + in) / (2 * NominalEnergyWh / 1e6)
```

The stored-change credit means a run that ends empty or full is neither flattered nor penalised. The round trip is NaN,
printed `-`, when the run absorbed nothing. Auxiliary energy is not in it and is reported separately. `EFC` is measured
against the nominal pack energy as sized, not the energy requested.

### Dispatch policies (`StorageDispatch.cs`)

A policy gives the AC power to request each step (MW, discharge positive). The chemistry then decides what is delivered.
A policy sees a `DispatchContext`: the step index, the hour of day at the start of the step, the step length in hours,
the system demand in MW, the price in GBP/MWh (NaN when there is no price series), the unit's SoC at the start of the
step and the unit's rated MW. `StorageFleet.Advance` clamps the request to `[-RatedMw, RatedMw]`.

| Policy | Request | Parameters and defaults |
|---|---|---|
| `idle` (`IdlePolicy`) | 0 | none |
| `peak-shave` (`PeakShavePolicy`) | `demand > High`: `min(Rated, demand - High)`; `demand < Low`: `-min(Rated, Low - demand)`; else 0 | `High` 0.9 and `Low` 0.7 of the peak demand of the run; `--shave-high`, `--shave-low` in absolute MW |
| `price-threshold` (`PriceThresholdPolicy`), CLI `price` | NaN price: 0; `price >= SellAbove`: `Rated`; `price <= BuyBelow`: `-Rated`; else 0 | `BuyBelow` 40, `SellAbove` 90 GBP/MWh; `--buy-below`, `--sell-above` |
| `response-holding` (`ResponseHoldingPolicy`), CLI `hold` | `SoC > Target + Band`: `Restore * Rated`; `SoC < Target - Band`: `-Restore * Rated`; else 0 | `Target` 0.5, `Band` 0.05, `Restore` 0.2; no CLI flag |
| `schedule` (`SchedulePolicy`), label `lp` on the CLI | `Mw[step]`, 0 beyond the array | the net MW array of a plan |

The comparisons in `peak-shave` and `price-threshold` against the thresholds are strict where written: a demand exactly
on a threshold requests nothing. The peak demand the CLI derives the `peak-shave` thresholds from is the case's total
load times the largest load factor of the run (`chain`) or of the series (`gda-replay`). `hold` reads the SoC at the start
of the step, so a step long enough to move the SoC across the whole band swings it either side of the band; it keeps the
SoC where a response service could be delivered and does not deliver the service.

**Which policy is reachable from which verb.** `--prices` is accepted on `dcopf`, `acopf` and `chain` with `--bess`; any
other verb refuses it (`error: --prices applies to dcopf, acopf and chain (with --bess) only (got '<verb>').`, exit 2).
On `chain` it takes a value: `synthetic` (the `SyntheticPrices.GbDay` day, one value per step) or a price file; a bare
`--prices` is refused. With prices and no `--policy`, the policy is `lp`.

| Policy | `gridsim chain` | `gridsim gda-replay` |
|---|---|---|
| `idle` | `--policy idle` | `--policy idle` |
| `peak-shave` | the default without prices; `--shave-high`, `--shave-low` | the default; the same flags |
| `hold` | `--policy hold` | `--policy hold` |
| `price` | with `--prices`; without it refused (`--policy price needs --prices ...`, exit 2) | refused: gda-replay takes no price series (exit 2) |
| `lp` | the default with `--prices`; without it refused (`--policy lp needs --prices`, exit 2) | refused, as `price` |

A storage flag (`--prices`, `--policy`, `--shave-high`, `--shave-low`, `--buy-below`, `--sell-above`,
`--degradation-gbp-per-mwh`, `--step-hours`) on `chain` without `--bess` exits 2. Each
`--bess chemistry:bus:MW:MWh[:soc]` spec is checked field by field (`StorageCli.ParseBessSpec`): four or five fields, a
known chemistry, an integer bus, MW and MWh finite and above 0, the starting state of charge (when given) from 0 to 1;
each refusal names the spec and the field (exit 2). A price file that is missing, unreadable or malformed exits 1.

### Optimal schedule: linear programme (`StorageLpScheduler`)

`StorageLpScheduler.Solve(env, prices, stepHours, w = 0, endEnergyMwh = null)` plans a net MW schedule over a price
series. With `T` steps, `dt = stepHours` and unknowns discharge `d_t` and charge `c_t` (AC MW) and stored energy `e_t`
(MWh), laid out as `[d_0..d_T-1, c_0..c_T-1, e_0..e_T-1]`:

```
minimise    sum_t [ (-price_t + w) d_t + (price_t + w) c_t ] dt            w = degradation GBP/MWh of throughput
subject to  e_t - e_{t-1} + d_t dt / eta_d - c_t dt eta_c = 0              e_{-1} = EnergyNow (moved to the right-hand side)
            0 <= d_t <= Dmax        0 <= c_t <= Cmax
            Emin <= e_t <= Emax
            e_{T-1} >= e_end        e_end = clamp(endEnergyMwh ?? EnergyNow, Emin, Emax)
```

That is `T` equality rows over `3T` bounded variables. `BoundedSimplex.Solve(a, b, cost, lo, hi)`
(`src/GridSim.Core/Numerics/BoundedSimplex.cs`) is a dense two-phase bounded-variable primal simplex that returns an
`LpStatus` (`Optimal`, `Infeasible`, `Unbounded`, `IterationLimit`), the vector and the objective. The plan is
`NetMw[t] = d_t - c_t`, `EnergyMwh[t] = e_t` and `ObjectiveGbp = -objective`, the price revenue less the degradation
cost. With no steps the plan is empty and `Optimal`. The default end energy is the starting energy, so the schedule
returns the battery to where it started.

**Envelope linearisation.** The LP is linear, the pack is not. `StorageEnvelope.FromPack(pack, ratedMw, eta,
stepHours)` reduces the pack to constants:

```
mid       = a pack of the same chemistry and size at SoC 0.5, no ageing tracking
sop       = mid.StateOfPower(min(stepHours * 3600, 1800) s, substeps = 20)           W, discharge and charge
Dmax      = min(ratedMw, sopDis * eta / 1e6)          Cmax = min(ratedMw, sopChg / eta / 1e6)
wh        = Q(fade) * Np * Ns * Vnom_cell                                            Wh per unit SoC
Emin, Emax, EnergyNow = SocMin * wh / 1e6, SocMax * wh / 1e6, SoC * wh / 1e6         MWh
iRated    = max(1e-9, Dmax * 1e6 / eta / (Ns * Np) / max(E_mid, 1e-6))               rated per-cell current, A
cellEta   = clamp(1 - iRated * R0_mid / E_mid, 0.5, 1)
eta_d     = eta * cellEta                              eta_c = eta * cellEta * CE
```

`E_mid` and `R0_mid` are the internal emf and series resistance of the mid-SoC pack. The power limits are therefore the
sustained state of power at mid SoC, so a pack that starts empty or full does not shrink the whole plan's limits. The
efficiencies are the converter times the chemistry's own resistive loss at the planned rate. Envelope for 5 MW / 10 MWh
at SoC 0.5, converter 0.98, step 0.5 h, from `docs/_captures/bess/api-probe-envelope.txt`:

```
$ API probe (GridSim.Core, Release build): envelope
chemistry   MaxDis_MW MaxChg_MW  Emin_MWh  Emax_MWh  Enow_MWh  eta_d    eta_c
LFP           5.0000    5.0000    0.5601   10.6417    5.6009  0.97050  0.96565
NMC           5.0000    5.0000    0.5513   10.4754    5.5134  0.97271  0.97076
LTO           5.0000    5.0000    0.3188   10.3066    5.3127  0.97509  0.97411
Na-ion        5.0000    5.0000    0.3172   10.2565    5.2869  0.96658  0.95691
VRFB          1.8169    2.3581    2.0992   11.8952    6.9972  0.86872  0.84266
Lead-acid     3.3572    2.7380    2.5990   12.7349    6.4974  0.93805  0.84425
[exit 0]
```

The flow battery and lead-acid are limited by their state of power below the 5 MW rating, which a 0.25C and 0.3C
battery-management limit and the rate effect produce. The plan is linear and execution is through the chemistry:
`SchedulePolicy` hands the plan's net MW to the pack step by step, so the tracking error and clipped steps show where
the envelope and the battery differ. Round trip and rainflow are measured on the execution, not the plan.

**Status.** The CLI wraps the plan in `OptimalLpPlan`: any status other than `Optimal` raises
`InvalidOperationException("--policy lp: the storage schedule LP did not solve (status ...); no schedule to run.")`. A
meaningless vector is never run as if it were the schedule.

**No complementarity.** The LP has no constraint that stops charging and discharging in the same step. When a later
charge repays the degradation cost of throughput that only dissipates energy, the plan can charge and discharge together,
and its energy trajectory then describes a path the net schedule does not follow. A full battery at a negative price
shows it (`docs/_captures/bess/api-probe-lp.txt`): with `w = 0` the net MW is zero for three steps while the stored
energy falls from 10.0816 MWh to 9.5960 MWh, then charges 2.3438 MW in step 3. With `w = 5` the plan charges once and
holds.

```
$ API probe (GridSim.Core, Release build): lp
LFP 5 MW/10 MWh, SoC 0.9, price -10 x4, w=0: status=Optimal
  net_MW  = 0.0000 0.0000 0.0000 -2.3438
  e_MWh   = 9.9198 9.7579 9.5960 10.6417   (Emax 10.6417, now 10.0816)
LFP 5 MW/10 MWh, SoC 0.9, price -10 x4, w=5: status=Optimal
  net_MW  = -1.1600 0.0000 0.0000 0.0000
  e_MWh   = 10.6417 10.6417 10.6417 10.6417   (Emax 10.6417, now 10.0816)
...
[exit 0]
```

On the synthetic GB day (48 steps of 0.5 h, NMC 5 MW / 10 MWh at SoC 0.5, `w = 0`) the same probe gives status
`Optimal`, an objective of GBP 1193.44, 11 charge steps and 8 discharge steps, and an end energy equal to the start
energy 5.5134 MWh.

### Time series: fleet and network (`StorageFleet`, `StorageTimeSeries`)

`StorageFleet` carries the sites of a sequential study. Each site is a `StorageSite`: name
`<chem> <MW>MW/<MWh>MWh @<bus>` (MW and MWh with no decimals), bus, rated MW, pack, policy and converter efficiency
(default 0.98). The fleet runs, per step:

1. **Request.** The site's policy gets the `DispatchContext` and the request is clamped to `[-RatedMw, RatedMw]`.
2. **Substeps.** The step is split into `30` substeps (`DefaultSubstepsPerStep`) of `subDt = stepHours * 3600 / 30` s.
   Each calls `BatteryDcSource.Draw(request, subDt)`. The fleet sums the delivered MW and the auxiliary MW over the
   substeps, divides by 30 for the step means, and records the SoC after every substep for rainflow. The first
   substep that is `Limited` names the step's limit reason.
3. **Books.** `out += max(delivered, 0) * h`, `in += max(-delivered, 0) * h`, `aux += aux * h`, and, only when the price is
   not NaN, `revenue += price * (delivered - aux) * h`. The `SiteStepRecord` carries the request, the mean delivered MW,
   the mean auxiliary MW, and the SoC, temperature and capacity fade at the end of the step.
4. **Inject.** `Apply(model)` adds, for each site, a generator at the site's bus with `Pg = delivered`, `Qg = 0`,
   `Qmin = Qmax = 0`, `Pmin = -RatedMw`, `Pmax = RatedMw`, `MBase = RatedMw`, type `storage`, converter interfaced. It
   adds the auxiliary MW to the bus demand `Pd`. The storage generator is appended after the case's generators and its
   `Vg` is the setpoint the bus already has: the existing generators' `Vg` by the shared `VoltageSetpoint` rule, else the
   bus's stored `Vm`. A site never moves a regulated bus's voltage. A storage site delivers real power only.

`StorageTimeSeries.Run(baseModel, sites, spec, solver, options)` drives the fleet:

```
hour    = (StartHour + step * StepHours) mod 24
m       = GridScenario.AtLoad(baseModel, LoadFactor(step))              scales Pd, Qd and non-slack Pg; rebalances
demand  = sum of m.Buses.Pd                                             before the battery's auxiliary load
records = fleet.Advance(step, hour, StepHours, demand, price)           price NaN without a series
model   = fleet.Apply(m), warm-started from the last converged step     GridScenario.WithSolvedVoltages
pf      = solver.Solve(model, options)
slackMw = pf.PInjMw[slack] + Pd[slack]                                  NaN when no slack bus
```

Only a converged solve becomes the next warm start. A step that does not converge is recorded with
`Converged = false` and `Vmin`, `Vmax` NaN; the CLI prints `-` in its slack and voltage columns and exits 3. Without `options` the run solves with 100 iterations and
reactive limits enforced. The `chain` verb passes `ContingencyAnalysis.StudyOptions` and wraps its solver in the
on-load tap-changer loop, so each step solves exactly as plain `chain` does. Without tap-changer records in the case the
loop returns the engine's answer to the bit (`S4SolverConvergenceTests`).

Validation is `StorageFleet.Validate(model)`: every site's bus must be in the model, else
`ArgumentException("storage site '<name>' is at unknown bus N.")`. `StorageTimeSeries.Run` and `LiveSim` call it at
construction; `GdaReplayLoop.Replay` calls it at its start. `Apply` throws `InvalidOperationException` for a site whose
bus is missing from the model it is given. Nothing else range-checks a site. `CellModel` clamps the starting SoC to
`[0, 1]`; `BatteryDcSource` refuses an `eta` outside `[0.5, 1]`; `PackModel` needs positive cell counts, which `Sized` guarantees;
`LookupTable` rejects fewer than two points and non-ascending `x`; `BatteryChemistryLibrary.ByName` rejects an unknown
name. A zero or negative energy sizes one string (the report of a zero-energy pack shows `maximum power transfer`,
`docs/_captures/bess/chain-zero-mwh.txt`), a zero rating delivers nothing, and a negative rating fails in the request
clamp (`docs/_captures/bess/chain-negative-mw.txt`).

**Entry points and step length.**

| Entry point | Step length | Hour seen by the policy | Price | Policy flags |
|---|---|---|---|---|
| `chain` (`StorageCli.Run`) | `--step-hours`, else `24 / steps` in `daily` mode, else `0.5` | `(step * StepHours) mod 24` | none (see reachability) | `idle`, `peak-shave`, `price`, `hold`, `lp` parsed; see the reachability table |
| `gda-replay` (`GdaReplayLoop.Replay`) | the gap to the next timestamp; the last step reuses the previous gap; a one-step series uses `0.5`; floored at `1e-6` h | the UTC clock time of the timestamp | NaN | `idle`, `peak-shave`, `hold` |
| `LiveSim` | the tick length in minutes over 60 | the tick's hour of day | NaN | set by the caller |

`gda-replay` takes no `--step-hours` flag; its step length is read from the series. The replay result is tagged
`ProvenanceTag.Counterfactual`, a plain reconstruction stays `DerivedFromRecorded`, and the CLI prints and writes it as a
counterfactual (`storage-counterfactual.csv`). The counterfactual CSV is not a time-series file that `--series` reads, so it cannot feed another replay
([ch. 22](<../manual/0022 Estimating GB history.md>), [ch. 9](<0009 GDA integration.md>)).

**Outputs.** `chain` writes `results/chain-storage-<case>.md` and `.csv`; `gda-replay` writes
`storage-counterfactual.csv` beside `state-estimate.csv`. The columns are documented in
[ch. 13](<../manual/0013 Battery storage studies.md>).

### Price data

`SyntheticPrices.GbDay(hour)` is a synthetic GB-shaped price day in GBP/MWh, synthetic in the sense of
`ProvenanceTag.Synthetic`, and labelled `Synthetic GB-shaped price day (not a market record)`. It is a shape for exercising dispatch logic, never a
market record. With `h = ((hour mod 24) + 24) mod 24`:

```
bump(c, w, a) = a * exp(-0.5 * ((h - c) / w)^2)
GbDay(h) = 45 + bump(8, 1.5, 35) - bump(13, 2, 20) + bump(18, 1.5, 85)
```

The overnight base is 45, the morning ramp adds 35 at 08:00, the solar dip removes 20 at 13:00 and the evening peak
adds 85 at 18:00. `GbDay(0) = 45.00`, `GbDay(8) = 79.12`, `GbDay(13) = 25.46`, `GbDay(18) = 129.12`.

A price file in `StorageCli.Run` is read line by line. Blank lines and lines whose first non-blank character is `#`
are skipped. Each other line contributes the last comma-separated field, which must be a finite number in invariant
culture (negative prices are allowed); anything else is refused naming the line (`PriceFileException`, exit 1), a header
line included. The file needs at least one value per step, else `--prices <file> has N values; S steps need one each.`
(exit 1); extra values are ignored with a `note:` on stderr. The report says `prices: file <name>`, or
`prices: Synthetic GB-shaped price day (not a market record).` for the synthetic day.

### Behind the inverter and behind the RMS converter

The grid-following and grid-forming inverter assets draw through an `IDcSource`:

- `SocBucket` is the linear store: `SoC -= P dt / 3600 / capacity`, no losses, no rate limit, headroom closed only at
  the SoC bounds. Every run without a chemistry is arithmetically the bucket.
- `BatteryDcSource` is a pack behind a converter (above). Its headroom is the pack's instantaneous battery-management
  limit, and a draw steps the pack. `Reset` restores the pack from a clone made without ageing tracking, and the
  asset-frequency integrators (`AssetFrequencyDynamics`, `AssetAwareFrequencyIntegrator`) reset the fleet at the start
  of every run, so a pack behind an inverter asset does not accumulate capacity fade or resistance growth. The
  `StorageFleet` sites do.

An asset class selects the model with the parameters `batteryChemistry` (1 LFP, 2 NMC, 3 LTO, 4 Na-ion, 5 VRFB,
6 lead-acid), `capacityMwh` (the nominal energy the pack is sized to) and optionally `dcVoltageV` (default 1000),
`converterEfficiency` (default 0.98) and `ambientC` (default 25). With `capacityMwh` set and no chemistry the class gets
a `SocBucket` between `socMin` and `socMax`; with `capacityMwh` unset or zero it has no store, whatever `batteryChemistry` says. The rated power of the pack is the inverter's
rating. A 2 MW / 1 MWh frequency-response service at full droop is held by NMC between 1.85 and 2.0 MW, held by LFP at the
1C limit (0.9 to 1.15 MW), and stops at the SoC floor after 20 to 30 minutes having delivered 0.70 to 0.90 MWh
(`BatteryAssetTests`). The grid-side control law is in
[ch. 13, Asset dynamics](<0013 Asset-dynamics data grounding.md>) and
[ch. 24, Standard dynamic model library](<0024 Standard dynamic-model library.md>).

`RmsConverterUnit.Store` puts an `IDcSource` (MW, with `BaseMva`) behind an RMS converter. The store's instantaneous
headroom caps the active current of a grid-following unit (`ipRef` clamped to `-Down / BaseMva / V` and
`Up / BaseMva / V`) and the power reference of a grid-forming unit. Each step then draws the active power the converter
injects. For a grid-following unit that is `|V| * Ip * Lvacm(|V|) * BaseMva` at the start-of-step terminal voltage, and a
shortfall scales `Ip` so the injection equals what the store delivered; a grid-forming unit draws `Pe * BaseMva`. A depleted, cold or hot pack cannot sustain its output. Reactive current (ride-through support) is not
drawn from the store. A 50 MW converter on a 30 MWh NMC pack at 7 % SoC holds 0.5 pu for 10 s, falls below 0.01 pu by
120 s with the store at its SoC floor, and still gives more than 0.5 pu of reactive current in a later dip
(`RmsConverterTests`). `Store` is an API option: `SubtransientInit` does not attach one, so `gridsim stability --rms`
runs without it.

## Parameters and defaults

The six presets are in `src/GridSim.Core/Storage/BatteryChemistryLibrary.cs`. `ByName` matches case-insensitively and
accepts the aliases `sodium-ion`, `naion`, `sib` (Na-ion), `flow`, `vanadium` (VRFB) and `lead`, `vrla` (lead-acid). All
values are per cell. The source symbol is the `ChemistryParameters` property.

### Scalar constants per chemistry

| Parameter (source symbol) | Unit | LFP | NMC | LTO | Na-ion | VRFB | Lead-acid |
|---|---|---|---|---|---|---|---|
| Rated capacity (`CapacityAh`) | Ah | 280 | 64 | 30 | 160 | 1000 | 1000 |
| Nominal voltage (`NominalVoltage`, OCV at 50 %) | V | 3.29 | 3.78 | 2.30 | 3.10 | 1.40 | 2.04 |
| Nominal cell energy (`NominalEnergyWh`) | Wh | 824.40 | 219.10 | 64.87 | 466.73 | 980.00 | 1603.84 |
| Fast pair (`R1`, `Tau1`) | mOhm, s | 0.12, 10 | 0.55, 8 | 0.25, 5 | 0.30, 8 | 0.30, 2 | 0.20, 30 |
| Slow pair (`R2`, `Tau2`) | mOhm, s | 0.18, 400 | 0.75, 300 | 0.30, 150 | 0.40, 300 | 0.20, 60 | 0.30, 1200 |
| Resistance activation energy (`ResistanceActivationEnergy`) | J/mol | 32000 | 35000 | 25000 | 20000 | 15000 | 25000 |
| Entropic coefficient (`EntropicCoefficient`, constant) | mV/K | -0.05 | -0.10 | 0 | 0 | 0 | 0 |
| Coulombic efficiency (`CoulombicEfficiency`) | fraction | 0.995 | 0.998 | 0.999 | 0.99 | 0.97 | 0.90 |
| Self-discharge (`SelfDischargePerMonth`) | fraction per 30 d | 0.03 | 0.02 | 0.01 | 0.03 | 0.02 | 0.03 |
| Peukert exponent, reference (`PeukertExponent`, `PeukertReferenceCRate`) | -, C | 1, 0.1 | 1, 0.1 | 1, 0.1 | 1, 0.1 | 1, 0.1 | 1.15, 0.1 |
| Cut-off, charge voltage (`VMin`, `VMax`) | V | 2.5, 3.65 | 3.0, 4.2 | 1.5, 2.8 | 2.0, 3.95 | 1.10, 1.65 | 1.75, 2.40 |
| Max discharge, charge rate (`MaxDischargeCRate`, `MaxChargeCRate`) | C | 1.0, 0.5 | 2.0, 1.0 | 6.0, 6.0 | 2.0, 1.0 | 0.25, 0.25 | 0.3, 0.2 |
| SoC window (`SocMin`, `SocMax`) | fraction | 0.05, 0.95 | 0.05, 0.95 | 0.03, 0.97 | 0.03, 0.97 | 0.15, 0.85 | 0.20, 0.98 |
| Derate start, limit (`TDerateC`, `TMaxC`) | C | 45, 55 | 40, 50 | 50, 60 | 45, 55 | 38, 45 | 40, 50 |
| Minimum charge temperature (`TMinChargeC`) | C | 0 | 5 | -20 | -10 | 5 | -10 |
| Cell mass (`MassKg`) | kg | 5.4 | 0.9 | 1.0 | 3.0 | 45 | 60 |
| Specific heat (`SpecificHeat`) | J/kg/K | 1100 | 1000 | 1000 | 1050 | 3000 | 800 |
| Passive cooling (`PassiveCoolingWPerK`) | W/K per cell | 0.08 | 0.02 | 0.03 | 0.06 | 0.4 | 0.5 |

`ChemistryParameters` defaults for any field a preset leaves out: `ResistanceActivationEnergy` 30000, `CoulombicEfficiency`
0.998, `SelfDischargePerMonth` 0.02, `PeukertExponent` 1, `PeukertReferenceCRate` 0.1, `SocMin` 0.05, `SocMax` 0.95,
`TDerateC` 45, `TMaxC` 55, `TMinChargeC` 0, `SpecificHeat` 1000, `PassiveCoolingWPerK` 0.05, entropic coefficient 0.

### Ageing constants per chemistry (`AgeingParameters`)

| Parameter (source symbol) | Unit | LFP | NMC | LTO | Na-ion | VRFB | Lead-acid |
|---|---|---|---|---|---|---|---|
| Calendar fade (`CalendarFadePerSqrtDay`, `k_cal`) | per sqrt day | 0.0010 | 0.0016 | 0.0005 | 0.0014 | 0.0002 | 0.0025 |
| Calendar activation energy (`CalendarActivationEnergy`, `E_cal`) | J/mol | 50000 | 55000 | 50000 | 50000 | 50000 | 50000 |
| Calendar SoC sensitivity (`CalendarSocSensitivity`, `a_soc`) | - | 0.6 | 0.6 | 0.6 | 0.6 | 0.6 | 0.6 |
| Cycles to 80 % (`CyclesTo80Percent`, `N80`) | cycles | 6000 | 3500 | 20000 | 4000 | 20000 | 1200 |
| Cycle activation energy (`CycleActivationEnergy`, `E_cyc`) | J/mol | 30000 | 30000 | 30000 | 30000 | 30000 | 30000 |
| C-rate sensitivity (`CRateSensitivity`) | - | 0.25 | 0.35 | 0.05 | 0.3 | 0 | 0.3 |
| Depth exponent (`DodExponent`, `beta`) | - | 1.3 | 1.3 | 1.3 | 1.3 | 1.3 | 1.6 |
| Resistance growth (`ResistanceGrowthPerFade`, `g`) | per unit fade | 2.5 | 2.5 | 2.5 | 2.5 | 2.5 | 2.5 |

`AgeingParameters` defaults: `CalendarFadePerSqrtDay` 0.0012, `CalendarActivationEnergy` 50000,
`CalendarSocSensitivity` 0.6, `CyclesTo80Percent` 6000, `CycleActivationEnergy` 30000, `CRateSensitivity` 0.3,
`DodExponent` 1.3, `ResistanceGrowthPerFade` 2.5.

### Auxiliary constants per chemistry (`AuxiliaryModel`)

| Parameter (source symbol) | Unit | LFP | NMC | LTO | Na-ion | VRFB | Lead-acid |
|---|---|---|---|---|---|---|---|
| Standby (`StandbyFraction`) | of rated power | 0.002 | 0.002 | 0.002 | 0.002 | 0.003 | 0.001 |
| HVAC coefficient of performance (`HvacCop`) | - | 3.0 | 3.0 | 3.0 | 3.0 | 0 | 0 |
| HVAC setpoint (`HvacSetpointC`) | C | 30 | 28 | 35 | 30 | not used | not used |
| HVAC capacity (`HvacCapacityFraction`) | of rated power | 0.05 | 0.06 | 0.08 | 0.05 | not used | not used |
| Pump (`PumpFraction`) | of power moved | 0 | 0 | 0 | 0 | 0.04 | 0 |

`AuxiliaryModel` defaults: `StandbyFraction` 0.002, `HvacCop` 3.0, `HvacSetpointC` 30, `HvacCapacityFraction` 0.05,
`PumpFraction` 0.

### Open-circuit-voltage and resistance tables

OCV in V on the SoC grid `0, 0.05, 0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90, 0.95, 1.0`
(`BatteryChemistryLibrary.Ocv`):

| Chemistry | OCV at the 13 grid points |
|---|---|
| LFP | 2.50, 3.00, 3.17, 3.22, 3.25, 3.27, 3.29, 3.30, 3.31, 3.33, 3.34, 3.36, 3.45 |
| NMC | 3.00, 3.45, 3.55, 3.62, 3.66, 3.72, 3.78, 3.86, 3.94, 4.02, 4.10, 4.14, 4.20 |
| LTO | 1.50, 2.00, 2.15, 2.22, 2.25, 2.28, 2.30, 2.33, 2.36, 2.40, 2.46, 2.52, 2.80 |
| Na-ion | 2.00, 2.50, 2.70, 2.85, 2.95, 3.03, 3.10, 3.18, 3.27, 3.38, 3.52, 3.62, 3.90 |
| Lead-acid | linear from 1.95 at SoC 0 to 2.13 at SoC 1 (two points) |

The VRFB curve is the Nernst equation `E = 1.40 + (2 Rg Tref / F) * ln(s / (1 - s))` with `F = 96485.332` and `s`
clamped to `[0.01, 0.99]`, tabulated at SoC `0, 0.02, 0.05, 0.10, 0.15, 0.25, 0.35, 0.50, 0.65, 0.75, 0.85, 0.90, 0.95,
0.98, 1.0` (from `docs/_captures/bess/api-probe-vrfb.txt`):

```
$ API probe (GridSim.Core, Release build): vrfb
VRFB OCV (SoC:V): 0.00:1.1639 0.02:1.2000 0.05:1.2487 0.10:1.2871 0.15:1.3109 0.25:1.3435 0.35:1.3682 0.50:1.4000 0.65:1.4318 0.75:1.4565 0.85:1.4891 0.90:1.5129 0.95:1.5513 0.98:1.6000 1.00:1.6361
[exit 0]
```

R0 at 25 C in mOhm:

| Chemistry | SoC grid | R0 |
|---|---|---|
| LFP | 0, 0.1, 0.5, 0.9, 1.0 | 0.45, 0.30, 0.25, 0.27, 0.35 |
| NMC | 0, 0.1, 0.5, 0.9, 1.0 | 1.6, 1.1, 0.95, 1.0, 1.2 |
| Na-ion | 0, 0.1, 0.5, 0.9, 1.0 | 0.9, 0.65, 0.55, 0.6, 0.75 |
| Lead-acid | 0, 0.2, 1.0 | 0.6, 0.35, 0.3 |
| LTO | constant | 0.8 |
| VRFB | constant | 1.2 |

### Preset character

| Preset | Character |
|---|---|
| LFP (280 Ah) | flat OCV plateau, 1C energy-storage rating, 6000 cycles, thermally robust |
| NMC (64 Ah) | sloped OCV, 2C, higher energy density, 3500 cycles, more temperature-sensitive |
| LTO (30 Ah) | low voltage, 6C for fast frequency-response duty, 20000 cycles, charges down to -20 C |
| Na-ion (160 Ah) | sloped OCV, 2C, lower activation energy so it keeps power in the cold, cycle life still maturing |
| VRFB (1000 Ah tank share per cell) | Nernst OCV, 0.25C, 15 to 85 % window, pumps 4 % of the power moved, lowest cycle fade of the six in the daily comparison (0.0006 %), `CRateSensitivity` 0 |
| Lead-acid (1000 Ah, 2 V) | near-linear OCV, Peukert 1.15, 90 % coulombic efficiency, 1200 cycles, deeper-cycle exponent 1.6 |

### Model and CLI constants

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Gas constant | 8.314462618 | J/mol/K | Arrhenius laws | `ChemistryParameters.GasConstant` |
| Reference temperature | 298.15 | K | 25 C reference of every Arrhenius law | `ChemistryParameters.TRefK` |
| Celsius offset | 273.15 | K | C to K | `ChemistryParameters.Kelvin0` |
| Self-discharge month | 30 | days | the "month" the self-discharge rate is quoted per | `CellModel.SelfDischargeMonthDays` |
| End-of-life fade | 0.2 | fraction | fade that defines `N80` | `AgeingModel.EndOfLifeFade` |
| Fade cap | 0.8 | fraction | cap on the fade written to the cell | `AgeingModel.MaxCapacityFade` |
| Minimum SoC stress | 0.05 | - | floor on the calendar SoC factor | `AgeingModel.MinSocStressFactor` |
| SoC stress reference | 0.5 | - | SoC where the factor is 1 | `AgeingModel.SocStressReference` |
| Seconds per day | 86400 | s | calendar law is in sqrt days | `AgeingModel.SecondsPerDay` |
| Throughput per full cycle | 2 | capacities | charge plus discharge | `AgeingModel.ThroughputPerFullCycle` |
| Divisor guard | 1e-9 | - | floors on the derate span and coulombic efficiency | `PackModel.DivisorGuard` |
| SoC edge proximity | 1e-3 | SoC | within this counts as at the edge when naming a limit | `PackModel.SocEdgeProximity` |
| Limit match tolerance | 1e-9 | A | two currents this close are the same limit | `PackModel.LimitMatchToleranceA` |
| Limited tolerance | 1e-6 | of max(1 W, request) | a shortfall below this is not reported | `PackModel.LimitedRelativeTolerance` |
| State-of-power search ceiling | 20 | C | upper bound of the bisection, times nominal energy | `PackModel.MaxSearchCRate` |
| State-of-power probe | 1 | W | a pack that cannot sustain this has zero state of power | `PackModel.ProbePowerW` |
| Bisection iterations | 50 | - | cap | `PackModel.MaxBisectionIterations` |
| Bisection tolerance | 1e-4 | of max(1 W, hi) | bracket width that stops it | `PackModel.BisectionRelativeTolerance` |
| Planning SoC | 0.5 | SoC | the envelope's operating point | `StorageEnvelope.PlanningSoc` |
| Planning horizon cap | 1800 | s | cap on the envelope's state-of-power horizon | `StorageEnvelope.MaxSopHorizonSeconds` |
| Envelope substeps | 20 | - | substeps of that simulation | `StorageEnvelope.SopSubsteps` |
| Rated cell current floor | 1e-9 | A | floor in the cell-efficiency estimate | `StorageEnvelope.MinRatedCellCurrentA` |
| Cell emf floor | 1e-6 | V | floor where the emf divides | `StorageEnvelope.MinCellEmfV` |
| Cell efficiency floor | 0.5 | - | lower clamp on `cellEta` | `StorageEnvelope.MinCellEfficiency` |
| Substeps per step | 30 | - | pack substeps per network step | `StorageFleet.DefaultSubstepsPerStep` |
| Power-flow iterations | 100 | - | default Newton cap of a storage step | `StorageTimeSeries.DefaultMaxIterations` |
| Converter efficiency | 0.98 | - | one way; default of `StorageSite` and `BatteryDcSource` | `BatteryDcSource.DefaultConverterEfficiency`, `StorageCli.DefaultConverterEfficiency` |
| Converter efficiency floor | 0.5 | - | lowest accepted value | `BatteryDcSource.MinConverterEfficiency` |
| Step length | 0.5 | h | non-daily `chain` modes and `gda-replay` single-step series | `StorageCli.DefaultStepHours`, `LiveSolveSettings.GdaSingleStepHours` |
| Replay step floor | 1e-6 | h | no zero-length replay step | `LiveSolveSettings.GdaMinStepHours` |
| Initial SoC | 0.5 | SoC | when the `--bess` spec gives none | `StorageCli.DefaultInitialSoc` |
| Pack DC voltage | 1000 | V | sizes the series count | `StorageCli.DefaultPackDcVoltage` |
| Peak-shave thresholds | 0.9, 0.7 | of peak demand | high and low | `StorageCli.PeakShaveHighFraction`, `StorageCli.PeakShaveLowFraction` |
| Price thresholds | 40, 90 | GBP/MWh | buy below, sell above | `StorageCli.DefaultBuyBelowGbpPerMwh`, `StorageCli.DefaultSellAboveGbpPerMwh` |
| Simplex tolerance, iteration cap | 1e-9, 20000 | - | `BoundedSimplex` defaults | `BoundedSimplex.DefaultTolerance`, `BoundedSimplex.DefaultMaxIterations` |

## Invariants

Every invariant below is pinned by the named test, in `tests/GridSim.Tests/`.

| Invariant | Test |
|---|---|
| Delivered charge to cut-off falls with discharge rate for every chemistry, and a C/20 discharge delivers more than 85 % of `Q * SocMax` | `StorageChemistryTests.Delivered_charge_falls_with_discharge_rate` (theory over all six presets) |
| The two RC voltages relax on their time constants, `V1 * exp(-1)` and `V2 * exp(-tau1/tau2)` after one `tau1` at rest, to 12 decimals | `StorageChemistryTests.Rc_pairs_relax_on_their_time_constants` |
| Energy balances over a discharge with the entropic coefficient set to zero and the temperature held at 25 C: `integral(OCV I) = integral(P) + heat + stored RC energy`, residual below `1e-3` of the OCV energy | `StorageChemistryTests.Energy_balance_closes_over_a_discharge` |
| Cell heating scales with `I^2`: the temperature rise at 2C over 1C lies in 3.6 to 4.2 | `StorageChemistryTests.Cell_heat_scales_with_current_squared` |
| A 10 s pulse exceeds a 30 minute delivery for LFP and NMC, NMC's pulse exceeds 1.5 times LFP's, NMC's 30 minute power exceeds LFP's, and NMC's 30 minute delivery at 0.8 SoC never exceeds the energy above the SoC floor (within 2 %) | `StorageChemistryTests.Pulse_power_exceeds_sustained_power_and_nmc_out_powers_lfp_on_both` |
| Na-ion keeps a larger fraction of its 10 s power at -10 C than NMC | `StorageChemistryTests.Sodium_ion_keeps_more_power_in_the_cold_than_nmc` |
| Lead-acid at 0.3C delivers a smaller fraction of its C/20 charge than LFP at 1C, by more than 0.05 | `StorageChemistryTests.Lead_acid_loses_capacity_at_rate_far_more_than_lfp` |
| NMC charging at -5 C is blocked: `Limited`, reason `too cold to charge`, zero DC power | `StorageChemistryTests.Charging_is_blocked_below_the_minimum_charge_temperature` |
| A flow battery's pumps draw `PumpFraction` of the power moved (4 % of 200 kW) on top of the standby load, and the step is not limited | `StorageChemistryTests.Flow_battery_pumps_draw_a_share_of_the_power_moved` |
| Rainflow reproduces the ASTM E1049-85 worked example: ranges 3 (0.5), 4 (1.5), 6 (0.5), 8 (1.0), 9 (0.5) | `StorageChemistryTests.Rainflow_matches_the_astm_e1049_example` |
| Calendar fade is step-size independent (1 step against 365 steps agree to 10 decimals), equals `k_cal * sqrt(365)` at 25 C and 50 % SoC to 12 decimals, and 40 C ages more than twice as fast as 25 C | `StorageChemistryTests.Calendar_fade_integrates_consistently_and_accelerates_with_heat` |
| Every preset is `CuratedPlaceholder` and its note contains `[VERIFY]` | `StorageChemistryTests.Presets_are_curated_placeholders_with_verify_notes` |
| A peak-shaving day converges at every step, keeps SoC within `[SocMin - self-discharge per day, SocMax]`, discharges only above the high threshold and charges only below the low one (checked for deliveries above 1 MW), and the slack relief equals the delivered power less auxiliary within 5 % | `StorageTimeSeriesTests.Peak_shaving_day_relieves_the_slack_and_keeps_soc_in_its_window` (IEEE 118, 100 MW / 200 MWh LFP, 48 steps) |
| The LP plan is `Optimal`, discharges only in the dear half, charges only in the cheap half, and executes within 10 % of the planned revenue and 5 MW of tracking error | `StorageTimeSeriesTests.Lp_arbitrage_plan_buys_low_sells_high_and_survives_execution_through_the_chemistry` |
| Energy books close: round trip in 0.85 to 0.97, `out <= in + max(0, -storedChange)`, the deepest rainflow cycle in 0.2 to 0.95, and calendar and rainflow fade are positive | `StorageTimeSeriesTests.Energy_books_close_with_a_plausible_round_trip_and_rainflow_counts_the_daily_cycle` |
| The live loop carries storage state across 24 hours, converges every tick, moves SoC by at most one tick's worth, and the solved model contains the storage generator at its delivered output | `StorageLoopTests.Live_loop_carries_storage_state_across_a_day` |
| `LiveSim` without storage is unchanged: `Storage` is null and the voltages are equal | `StorageLoopTests.Live_loop_without_storage_is_unchanged` |
| A replay with storage is tagged `Counterfactual`, a plain replay stays `DerivedFromRecorded`, and the counterfactual carries 48 step records | `StorageLoopTests.Replay_with_hypothetical_storage_is_counterfactual` |
| A storage site on a regulated bus keeps the bus's existing `Vg` (1.04), and on a bus without a generator takes the bus `Vm` | `PowerFlowPhysicsConsistencyTests.Storage_on_a_regulated_bus_keeps_the_existing_setpoint`, `Storage_on_a_bus_without_a_generator_takes_the_bus_voltage` |
| An NMC pack behind a 2 MW inverter holds the service, LFP is limited at 1C, delivery stops at the SoC floor, `Reset` restores the pack, and the bucket path is unchanged | `BatteryAssetTests` (four tests) |
| An RMS converter's store sustains active power until empty and not the reactive support | `RmsConverterTests.Energy_store_sustains_the_active_power_until_it_is_empty_but_not_the_reactive_support` |
| `chain --bess` passes the study option set and the tap-changer loop (a source-text check of `Program.cs` and `StorageCli.cs`), and without tap-changers is bit-identical to the raw engine on IEEE 118 (a behavioural check of Vmin, Vmax, slack and loss) | `S4SolverConvergenceTests.N084_chain_bess_passes_the_study_set_and_the_oltc_loop_to_the_storage_time_series`, `N084_without_oltcs_the_storage_chain_is_unchanged_to_the_bit` |
| The named constants equal the literals they replaced (ageing, cell, pack, envelope, fleet, time series) | `Ws13CoreConstantsAntiDriftTests.Simulation_and_storage_constants_equal_the_literals_they_replaced` |
| The CLI storage defaults are named and `gda-replay` reuses the step length | `CliLiteralsAndFlagsContractTests.Storage_defaults_are_named_and_reused_by_gda_replay` |

The tests build `IdlePolicy`, `PeakShavePolicy` and `SchedulePolicy` (with an `lp` plan from an explicit price array)
and nothing else. Stated in this chapter with no dedicated test: the exact limit-reason order and matching tolerances, the
asymmetric converter formulas, the `price-threshold` and `response-holding` policies, the `peak-shave` thresholds as
formulas (the peak-shave behaviour is covered by the time-series test), the round-trip and `EFC` formulas as exact
expressions, the `OptimalLpPlan` status check, `SyntheticPrices.GbDay`, the price-file reader and the
`StorageFleet.Validate` message. They are read from the
source named in the source map.

## Determinism

A storage run is sequential and has no random input and no clock input. Sites are processed in `--bess` order, every
loop has a fixed bound or a fixed iteration cap (30 substeps, 50 bisection iterations, the simplex iteration cap), the
simplex breaks ratio-test ties by the first row, and the CLI sets the invariant culture for the whole process (`Program.cs`, `CultureInfo.CurrentCulture`), which is how numbers are formatted. The same inputs
give the same outputs run to run, and the CLI report carries no timings, so the same command prints the same text. The
network solves underneath are the validated Newton engines of [ch. 3, Power flow](<0003 Power flow.md>) with their
bit-identity contract. `S4SolverConvergenceTests` compares two storage configurations bit for bit; no test repeats a
storage run and compares the bits across CPUs.

## Worked example

The run is a 5 MW / 10 MWh LFP battery at bus 5 of `case9` over the 24 h demand day in six 4 h steps, shaving the
peak. The case has 315 MW of load, so the thresholds are 283.5 MW and 220.5 MW. The timings and stamps of any output
differ by machine and run; this report holds none.

```
$ gridsim chain case9 --mode daily --steps 6 --bess lfp:5:5:10 --policy peak-shave
GridSim - storage time series  *  case9  *  6 x 4 h  *  mode=daily  *  policy=peak-shave
prices: none.  Chemistry presets are CuratedPlaceholder [VERIFY] (representative cells).

...
|---:|---:|---:|---:|---:|---|---:|---:|:-:|
| 0 | 0.0 | 0.700 | 220 | - |     0.0 / 0.500 / 25.0 | 49 | 1.024 | y |
| 1 | 4.0 | 0.600 | 189 | - |    -1.3 / 0.950 / 29.4 (SoC ceiling) | 43 | 1.025 | y |
| 2 | 8.0 | 0.700 | 220 | - |     0.0 / 0.950 / 28.6 | 49 | 1.024 | y |
| 3 | 12.0 | 0.900 | 283 | - |     0.0 / 0.950 / 28.0 | 64 | 1.005 | y |
| 4 | 16.0 | 1.000 | 315 | - |     2.4 / 0.050 / 29.6 (SoC floor) | 69 | 0.996 | y |
| 5 | 20.0 | 0.900 | 284 | - |     0.0 / 0.050 / 28.8 | 64 | 1.005 | y |

LFP 5MW/10MWh @5: out 9.7 MWh, in 5.3 MWh, aux 0.27 MWh, stored change -5.0 MWh, round trip 93.5 %, 0.75 equivalent full cycles
    ageing over the run: calendar 0.1372 %, cycle 0.0026 % (rainflow, depth-aware: 0.0020 %), deepest cycle 90 % SoC
[chain] done -> results\chain-storage-case9.md
[exit 0]
```

The same run's CSV, `docs/_captures/bess/chain-csv-head.txt`, holds the numbers to six significant figures (`StorageCli.cs`, `F(v)` is `ToString("G6")`; NaN is an empty field):

```
$ head -4 results/bess-demo.csv
step,hour,load_factor,demand_mw,price,s0_request_mw,s0_delivered_mw,s0_aux_mw,s0_soc,s0_temp_c,s0_fade,s0_limited,slack_mw,vmin,vmax,loss_mw,converged
0,0,0.7,220.5,,0,0,0.01,0.499833,25,0.000408227,0,49.0992,1.02356,1.04076,2.18915,1
1,4,0.6,189,,-5,-1.32387,0.0109119,0.949994,29.3712,0.000796927,1,43.1443,1.025,1.04385,1.60956,1
2,8,0.7,220.5,,0,0,0.01,0.949828,28.5984,0.0010474,0,49.0992,1.02356,1.04076,2.18915,1
[exit 0]
```

### Step 0: idle at the threshold, closed form

Step 0 has a demand of 220.5 MW, exactly the low threshold, and the comparison is strict, so the request is 0. Over
4 h the SoC falls only by self-discharge: `0.03 / 30 * (4 / 24) = 0.000166667`, so `0.5 - 0.000166667 = 0.499833`, the
CSV's `s0_soc`. The auxiliary power is the standby load, `0.002 * 5 MW = 0.01 MW`, the CSV's `s0_aux_mw`. The calendar
fade at 25 C and SoC 0.5 is `0.001 * sqrt(4 / 24) = 0.000408248` as a single step; the 30 substeps give
`0.000408227` (the CSV's `s0_fade`) because the SoC factor `1 + 0.6 * (SoC - 0.5)` is below 1 as the SoC drifts down by
`1.7e-4`.

### Step 1: the charge step by hand

The re-computation below is an independent implementation of the equations in this chapter, in
`docs/_captures/bess/hand-recompute-chain-lfp-step1.txt` (the first of 30 substeps), against the CSV's step 1. The pack is
`Ns = 304`, `Np = 40`, 12160 cells, 10.02466 MWh nominal.

```
$ python hand.py   (independent re-computation of step 1 of: gridsim chain case9 --mode daily --steps 6 --bess lfp:5:5:10 --policy peak-shave, first of 30 substeps)
start of step 1: SoC=0.4998333 T=25.00 C V1=0 V2=0 fade=0.000408227
pack: Ns=304 Np=40 cells=12160  nominal 10.02466 MWh
OCV = 3.27 + 0.998333*(3.29-3.27) = 3.2899667 V (entropic term 0 at 298.15 K)
R0(25C) = 2.5002083e-04 ohm; ageing factor 1+2.5*fade = 1.0010206; R0 = 2.5027600e-04 ohm
DC request = -5 MW * 0.98 = -4900000 W; per cell p = -402.9605 W
disc = E^2 - 4 R0 p = 11.227286; I = (E - sqrt(disc))/(2 R0) = -121.3612 A
limits: C-rate 139.94 A, SoC-ceiling room 949.7 A -> neither binds (|I|=121.36 A)
terminal V = E - I R0 = 3.320340 V; DC power = V*I*cells = -4900000.0 W; AC = DC/0.98 = -5.000000 MW
V1 = -0.014578 V, V2 = -0.015281 V
heat per cell: I2R0 1769.4 + RC1 424.6 + RC2 311.0 + entropic 868.4 = 3373.4 J; pack 4.1021e+07 J
m cp = 5.4*1100*12160 = 72230400.0 J/K; T no HVAC = 298.15 + 4.1021e+07/72230400.0 = 298.7179 K = 25.568 C
I_eff = I*0.995 = -120.7544 A; dSoC charge = 0.057526; self-discharge = 5.556e-06; SoC = 0.557353
calendar: stress 1.034412e-03, tEq 0.155746 d, fade 0.000415444; cycle: EFC 0.028896, fade 9.6318e-07
[exit 0]
```

Reading it against the equations: the request is -5 MW AC, so the converter takes `5 * 0.98 = 4.9 MW` from the grid
side to the DC side (charge multiplies by `eta`). The OCV is interpolated between the grid points 0.4 and 0.5. `R0` is
interpolated between 0.1 and 0.5 and scaled by `1 + 2.5 * fade`; the Arrhenius factor is 1 at 25 C. The smaller root of
`E I - R0 I^2 = p` is `I = -121.36 A` per cell, which neither the 0.5C limit (139.94 A) nor the SoC-ceiling room
(949.7 A) binds, so the step delivers the request exactly, `-5.000000 MW`. The heat is the sum of the four terms, 41.02 MJ
for the pack, and raises the 72.23 MJ/K pack mass by 0.568 K to 25.568 C. Charging stores `I * CE` and the SoC rises
`0.057526`, to 0.557353.

The remaining substeps, `docs/_captures/bess/hand-recompute-chain-lfp-step1-substeps.txt` (AC MW delivered, SoC, T in C):

```
$ python run2.py   (all 30 substeps of step 1, same run: AC MW delivered, SoC, T in C)
after step0 0.4998333333333339 298.15 0.00040822719780544584 0.00040822719780544584
0 -5.0 0.557353 25.568
1 -5.0 0.614276 26.278
2 -5.0 0.671053 27.017
3 -5.0 0.727742 27.75
4 -5.0 0.784312 28.466
5 -5.0 0.840721 29.161
6 -5.0 0.897038 29.836
7 -4.70531 0.949994 30.0
8 -0.00049 0.949994 30.0
9 -0.00049 0.949994 29.976
...
29 -0.00049 0.949994 29.371
[exit 0]
```

Seven substeps run at the full -5 MW. In substep 7 the charge room `(0.95 - 0.897038) * 3600 * Q / 480 / 0.995` is below the
requested current, so the current is capped and the step lands on `0.949994` (the ceiling less one substep of
self-discharge), delivering -4.70531 MW. The temperature reaches the 30 C HVAC setpoint in substep 7, where the HVAC engages and
holds the pack at 30.0 C; from substep 9 the pack, with almost no current, cools passively. From substep 8 only a -0.00049 MW trickle flows: the room above the
ceiling is the self-discharge of one substep. The step's mean is `(7 * 5 + 4.70531 + 22 * 0.00049) / 30 = 1.32387`, the
CSV's `s0_delivered_mw` of `-1.32387`, and the limit reason is `SoC ceiling`. The end state is SoC 0.949994 and
29.3712 C, the CSV's `s0_soc` and `s0_temp_c`.

### The whole run

`docs/_captures/bess/hand-recompute-chain-lfp-run.txt` re-computes all six steps and the summary.

```
$ python run1b.py   (independent re-computation of all six steps of: gridsim chain case9 --mode daily --steps 6 --bess lfp:5:5:10 --policy peak-shave)
step request_MW delivered_MW aux_MW SoC T_C total_fade
0   0.00   0.00000  0.010000 0.499833  25.000 0.000408227
1  -5.00  -1.32387  0.010912 0.949994  29.371 0.000796927
2   0.00   0.00000  0.010000 0.949828  28.598 0.001047403
3   0.00   0.00000  0.010000 0.949661  27.962 0.001231714
4   5.00   2.41494  0.017145 0.049911  29.570 0.001340755
5   0.00   0.00000  0.010000 0.049744  28.762 0.001398409
out 9.65975 MWh, in 5.29548 MWh, aux 0.27223 MWh
calendar fade 0.0013721 (0.1372 %), cycle fade 2.6283e-05 (0.0026 %)
stored change = 0 - 5.0408 = -5.0408 MWh (SoC 0.04974 is below the 0.05 floor)
round trip = 9.65975 / (5.29548 + 5.0408) = 0.93455
EFC = (9.65975 + 5.29548) / (2 * 10.02466) = 0.74592
[exit 0]
```

It reproduces the report: `out 9.7`, `in 5.3`, `aux 0.27`, calendar `0.1372 %`, cycle `0.0026 %`. The stored change is
the energy above the SoC floor at the end less at the start: at the start `0.45 * 280 * 40 * 304 * 3.29 = 5.0408 MWh`, at
the end 0 because the SoC 0.04974 is below the floor. The round trip is `9.65975 / (5.29548 + 5.0408) = 0.93455`, printed
`93.5 %`, and `EFC = (9.65975 + 5.29548) / (2 * 10.02466) = 0.74592`, printed `0.75`. Step 4 delivers a mean 2.41 MW
instead of the 5 MW asked for, because the discharge room to the 0.05 floor is exhausted partway through the step; the
reason is `SoC floor`.

Two losses are visible in the report. The converter costs 2 % each way: a 5 MW charge takes 4.9 MW into the pack and a
5 MW discharge draws 5.102 MW from it. The cell's resistance and polarisation heat the pack from 25.0 C to 29.4 C in step
1 alone, and the HVAC load shows in the step-4 auxiliary power, 0.017 MW against the 0.010 MW standby.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Storage/CellModel.cs` | `CellModel` | equivalent circuit, RC relaxation, Peukert, coulombic efficiency, self-discharge, heat |
| `src/GridSim.Core/Storage/ChemistryParameters.cs` | `ChemistryParameters`, `AgeingParameters`, `AuxiliaryModel`, `LookupTable` | the per-chemistry constants, defaults, Arrhenius factor, nominal voltage and energy |
| `src/GridSim.Core/Storage/BatteryChemistryLibrary.cs` | `BatteryChemistryLibrary` | the six presets, `ByName`, the Nernst table |
| `src/GridSim.Core/Storage/PackModel.cs` | `PackModel`, `PackStepResult` | sizing, the pack step, limits and limit reasons, thermal model, auxiliary load, state of power |
| `src/GridSim.Core/Storage/AgeingModel.cs` | `AgeingModel`, `Rainflow` | calendar and cycle fade, resistance growth feed-back, the fade cap, rainflow counting |
| `src/GridSim.Core/Storage/StorageDispatch.cs` | `IStorageDispatchPolicy` and the five policies, `StorageEnvelope`, `StorageLpScheduler` | dispatch rules, envelope linearisation, the LP |
| `src/GridSim.Core/Numerics/BoundedSimplex.cs` | `BoundedSimplex`, `LpStatus` | the bounded-variable simplex behind the LP |
| `src/GridSim.Core/Storage/StorageFleet.cs` | `StorageFleet` | substeps, books, injection into the model, validation, per-site summaries |
| `src/GridSim.Core/Storage/StorageTimeSeries.cs` | `StorageTimeSeries`, `StorageSite`, `StorageTimeSeriesSpec` and the record types | the step loop, warm start, slack bookkeeping |
| `src/GridSim.Core/Storage/SyntheticPrices.cs` | `SyntheticPrices` | the synthetic GB price day |
| `src/GridSim.Core/Assets/DcSource.cs` | `IDcSource`, `SocBucket`, `BatteryDcSource`, `DcSources` | the pack behind a converter, the asset-class factory |
| `src/GridSim.Core/Dynamics/Models/RmsConverter.cs` | `RmsConverterUnit.Store` | the store behind an RMS converter |
| `src/GridSim.Core/Simulation/GdaReplayLoop.cs` | `GdaReplayLoop.Replay` | storage on recorded history, step length from the series |
| `src/GridSim.Core/Simulation/LiveSim.cs` | `LiveSim` | storage carried tick to tick |
| `src/GridSim.Cli/StorageCli.cs` | `StorageCli` | `--bess` parsing, defaults, price reading, policies, reports |
| `src/GridSim.Cli/Program.cs` | `chain` and `gda-replay` branches, the `--prices` guard | verb wiring, option set and tap-changer loop, refusals |

## Limits

- `gda-replay` takes no price series, so its sites run `idle`, `peak-shave` or `hold` only.
- `gridsim gda-replay` checks every `--bess` site's bus against the replay case (`StorageFleet.Validate`) inside the
  verb's error handling, so a site on a bus the case does not have is refused with exit 2 before the replay starts.
- The LP has no charge and discharge complementarity, so at a negative price it can charge and discharge in one step and
  its energy trajectory then differs from the net schedule.
- The LP envelope is linear: power limits come from the state of power at mid SoC and efficiencies from the rated
  current, so sag, fade and temperature during the day are not in the plan.
- Each site is planned independently of the network and of the other sites.
- A site injects real power only: its reactive range is zero and it holds the voltage set-point its bus already had.
- The pack is one lumped cell: no cell-to-cell imbalance, no module thermal gradient, one temperature per pack.
- The Peukert test and the cycle C-rate use the unfaded rated capacity.
- The limit-reason comparisons use an absolute `1e-9` A tolerance, where a relative test would be more robust for large
  currents (R198).
- The chain and replay verbs fix the converter efficiency (0.98 one way), the pack DC voltage (1000 V) and the ambient
  temperature (25 C); no flag changes them.
- All sites in a run share one policy and one set of thresholds, and `chain` scales every load in the case uniformly.
- The rainflow fade is a report beside the throughput fade and is not fed back into the cell.
- A site's SoC, rating and energy are not range-checked beyond the case's bus list; a negative rating fails in the
  request clamp with a message that does not name the flag.
- The chemistry presets are `CuratedPlaceholder` representative cells, not products.
- No test repeats a storage run to compare its bits across runs or CPUs.
- `stability --rms` runs without an energy store behind its converters.

## See also

- [Ch. 13, Battery storage studies](<../manual/0013 Battery storage studies.md>), the user chapter for `chain --bess`
  and `gda-replay --bess`.
- [Ch. 19, CLI reference: `chain`](<../manual/0019 CLI reference.md#chain>) and
  [`gda-replay`](<../manual/0019 CLI reference.md#gda-replay>) for every flag.
- [Ch. 3, Power flow](<0003 Power flow.md>), the solves under every storage step.
- [Ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>), for `CuratedPlaceholder`, `Synthetic` and
  `Counterfactual`.
- [Ch. 9, GDA integration](<0009 GDA integration.md>) and
  [ch. 22, Estimating GB history](<../manual/0022 Estimating GB history.md>), where `gda-replay` lives.
- [Ch. 13, Asset dynamics](<0013 Asset-dynamics data grounding.md>) and
  [ch. 24, Standard dynamic model library](<0024 Standard dynamic-model library.md>), for the converter control laws
  that draw through an `IDcSource`.
