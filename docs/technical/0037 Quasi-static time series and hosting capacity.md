# 37. Quasi-static time series and hosting capacity

`GridSim.Core.Qsts`: the quasi-static time series (QSTS) engine with time-aware controllers, and the hosting-capacity
study built on it. The verbs are [`qsts`](<../manual/0019 CLI reference.md#qsts>) and
[`hosting`](<../manual/0019 CLI reference.md#hosting>); this chapter gives the formulation, the time model and the
validation.

## 37.1 What a QSTS run is

A QSTS run is a sequence of steady-state power flows at the instants t_k = k dt (k = 0 .. N-1, dt a whole number of
seconds, 1 s to hours). Each solve is a call to an existing engine (`IPowerFlowSolver`: the backward/forward sweep, dense
or sparse Newton); QSTS only builds the model each solve sees:

- bus demand scaled by its load profile, Pd(t) = Pd m(t), Qd(t) = Qd m(t);
- generator Pg scaled (or set in MW) by its profile; with `GenerationFollowsLoad`, every other unit off the slack bus
  scaled by the demand ratio and re-balanced by `Dispatcher.Balance` (`GridScenario.AtLoad`'s rule: with one profile on
  every bus the step model is exactly `AtLoad`'s, tested bit for bit on case118);
- added loads (EV, heat pumps) as +P + jQ at their bus; PV units and storage as negative loads (injections) P + jQ;
- tap ratios and capacitor steps from the controllers (a bank adds steps x StepMvar to Bs, exactly as the switched-shunt
  loop does);
- the warm start: Vm and angle of every bus from the previous step's converged result
  (`GridScenario.WithSolvedVoltages`).

Step 0 is not warm-started: it solves the case as given. A one-step run with nothing varying therefore hands the
engine the case model itself, and the result is the single power flow bit for bit (tested on the three engines).

Profiles are read at whole-second instants with integer arithmetic (sample i = t div step), held (default) or linear,
optionally wrapped; a study longer than a profile is refused rather than extrapolated.

## 37.2 Control modes

`steady` solves every step with the existing steady-state loops: the OLTC loop of `SteadyStateSolver` around
`SwitchedShuntPowerFlow` when banks are given. Controls settle instantly; tap ratios (from `RegulatedModel`) and bank
steps (as the next step's `InitialSteps`) carry forward. This is the case solve applied step by step, so its one-step
results equal those loops bit for bit by construction (tested).

`timed` is the time-aware framework of this chapter.

## 37.3 The time model and the order of events in a step

At step k:

1. **Advance over [t(k-1), t(k)).** Every discrete controller that ended step k-1 outside its band adds the elapsed
   time to its accumulated time, weighted by its characteristic (1 for definite time; |V - Vset| / half-band for an
   inverse-time OLTC, capped at Delay / MinDelay). Storage energy integrates the power held over the interval:
   E(k) = E(k-1) - P / eta_d dt (discharge) or E(k-1) + eta_c (-P) dt (charge), dt in hours.
2. **Build and solve** the step's model (37.1).
3. **Control iterations**, each followed by a re-solve, until nothing acts:
   a. continuous level: inverters and storage move towards their targets (fixed-point iteration, below);
   b. else every capacitor bank whose accumulated time covers its delay (and, switching in, whose dead time since it
      last switched out has passed) and whose quantity is still outside its band switches one step;
   c. else every tap changer whose accumulated time covers its delay moves one tap (with sequential coordination, only
      the first ready one by priority).
   Caps: 25 discrete iterations (the tap loop's pass cap) and 200 continuous ones; when the per-unit continuous
   iteration stalls (see the stall watch below) or reaches its cap, the accelerated iteration
   (below) takes over with a fresh cap of the same size. A step that
   hits a cap with a device still ready is reported unsettled (the verb exits 3 unless `--allow-unsettled`).
4. **Arm the timers** from the final state; **record** the step.

Fast devices act before slow ones (inverters in seconds, banks, then tap changers in tens of seconds); with only one
kind of device present each iteration is exactly one step per device per pass, which is what the existing loops do.

Re-solves within a step start from the step's own starting voltages, as the tap loop's passes do, so a pass is
reproducible from its inputs alone.

**Timers.** A controller is armed at the first sample (step solve) at which its quantity is outside its band; it acts
while its accumulated time covers the next delay (the initial delay for the first operation of an excursion, the
inter-tap delay for later ones under continuous timing, the initial delay again under sequential timing), re-sensing
after each re-solve; it resets when its quantity returns inside the band or reverses direction. At 1 s steps this is
exact delay timing. At steps longer than the delays a regulator makes, at one instant, every operation its timers allow
(catch-up), instead of one operation per step.

**Hunting guard.** A bank that reverses direction within a step is frozen for the rest of that step (as
`SwitchedShuntPowerFlow` does); the step is then reported unsettled.

## 37.4 Controllers

**OLTC.** The case's `Oltc` records (target, deadband, step, range, impedance correction) drive their transformers, mapped
to branches by `SteadyStateSolver.MapOltcRecordsToBranches` and held at generator-regulated buses by the tap loop's own
rule (`HeldRecords`). The tap step is the tap loop's expression (lowering the tap raises a controlled bus on the untapped
end, raising it raises one on the tapped end; a tap outside its range holds). Line drop compensation regulates
|V - (R + jX) I|, with I the current leaving the transformer into the regulated bus, I = conj(S / V), S = -(P + jQ) of the
branch at that end over the system base, and the phasor V from DetMath.SinCos.

**Capacitor and reactor banks.** Equal steps of StepMvar (MVAr at 1 pu). Voltage control (in below ON, out above OFF),
var control (reactive power into a branch at its From end), time control (in between two times of day). A bank times
only the operation the quantity asks for and it can make (see 37.6 for how OpenDSS differs).

**Inverters (IEEE 1547-2018).** Volt-var Q(V) (Table 8: Category A (0.90, +0.25), (1.00, 0), (1.10, -0.25); Category B
with VRef 1.0 (0.92, +0.44), (0.98, 0), (1.02, 0), (1.08, -0.44); Q as a fraction of the apparent power rating),
volt-watt P(V) (Table 10: 1.0 at 1.06 pu falling to P2 at 1.10 pu, P2 = 0 for PV), fixed power factor
(Q = P sqrt(1 - pf^2) / pf). The open-loop response time T (90 % of a change after T) is a first-order lag over the step,
beta = 1 - exp(-dt ln 10 / T) (DetMath). Var priority caps Q at S and curtails P to sqrt(S^2 - Q^2); watt priority caps Q
at sqrt(S^2 - P^2). The continuous iteration moves each unit a fraction alpha of the way to its target (alpha = 1,
halved whenever a unit's update stops shrinking) until no set point moves by more than 1e-7 of its rating.

**Accelerated continuous iteration.** With many units coupled through the network the per-unit factors cannot settle:
the undamped map overshoots in its coupled modes, each unit's alpha is halved on the rounds its change grows, alpha
never recovers, and the last units creep (on `gb-full` with 1,861 Category B units, 572 were still moving by under
1 kVAr per round after 1,000 rounds). When the per-unit iteration stalls or reaches its cap without settling, the run
switches,
for the rest of the run, to Anderson acceleration over all continuous set points together: x holds P and Q of every
reactive-function PV unit and P of every peak-shave or self-consumption battery, each over its rating; F(x) is "solve,
then evaluate every target"; with g = F(x) - x and the differences dX, dG of the last 8 iterates and residuals, the step
is x' = x + g - (dX + dG) gamma, gamma minimising ||g - dG gamma||_2 (modified Gram-Schmidt, newest column first, a
column dropped when its new direction is under 1e-10 of its norm). Set points are held within their ratings. On a
linear map this is GMRES on (I - J) x = b, so the coupled modes settle together. The history restarts at each step
and after each discrete action. The settle test is the per-unit one (every target within 1e-7 of the rating of its
set point), so "settled" means the same in both. The arithmetic is sequential in model order (no SIMD reductions, no
threads; only Math.Sqrt, correctly rounded), so the bits are the same on every run and SIMD tier.

**Stall watch.** The per-unit iteration's progress measure in a round is the largest change any unit still has to make,
over its rating. A converging loop shrinks it geometrically; a creeping one barely moves it, because its factors have
been halved to almost nothing. The engine keeps the best measure of the current continuous phase (restarted at each
step and after each discrete action) for every round, and calls the loop stalled at round k only when both hold: the
best has not halved over the last 25 rounds, and at the rate seen over those rounds (q = best[k] / best[k - 25] per 25
rounds) it cannot reach the settle tolerance by the cap (best[k] q^j stays above it for every j with k + 25 j within the
cap; q of 1 or more never reaches it). A loop that fails the second test would have run into the cap at its own rate and
switched there anyway; the watch moves the switch earlier. A slow loop that is on course to settle inside the cap keeps
the per-unit iteration. The extrapolation is repeated multiplication (no logarithm), so it is the same on every
platform. The watch only reads the measure, so a run whose per-unit iteration settles without stalling is unchanged bit
for bit (the pinned day digests hold). On the 20-unit coupled test feeder the step now settles in 45 continuous rounds
in all at the default cap (47 with a cap of 1,000) instead of over 200. The `gb-full` summer-minimum volt-var run of
2026-10-26 settled at the default tolerance in 256 power flows, 200 of them the per-unit cap; that run has not been
repeated with the stall watch.

**Storage.** Schedule (MW profile), peak shaving (keep the power into a branch between LowMw and HighMw: the request is
the excess of the flow the branch would carry without the unit, P_branch + P_unit), self-consumption (the bus's demand net
of PV). The power is limited so the energy stays within [SocMin, SocMax] over the coming step:
discharge <= (E - Emin) eta_d / dt, charge <= (Emax - E) / (eta_c dt).

## 37.5 Outputs and violations

Per step: bus voltages, branch loadings, losses, source power, PV available / delivered and reactive power, storage
power and SOC, tap ratios, bank steps, solves and iterations, settled flag. Every operation is logged (step, instant,
device, from, to, the sensed quantity, the control iteration). Each step stands for one step length: time above / below a
voltage limit and above the loading limit is counted in seconds, and the excess is integrated (pu.s, %.s). Energy totals
in MWh. `QstsResult.Digest` is SHA-256 over the bits of every number: two runs agree to the bit exactly when their digests
do.

## 37.6 Validation

- **Bit identity with the existing code** (exact `Assert.Equal`, `QstsEngineTests`): one step = the single power flow
  (dense, sparse, sweep; case14, case30, a radial feeder); steady one step = the tap loop and the tap + shunt loops; timed
  one step with zero delays = the tap loop (taps only) and the shunt loop (banks only); warm-started steps =
  `GridScenario.WithSolvedVoltages` + solve.
- **Determinism.** Repeated and parallel runs give one digest; dense and sparse Newton give the same digest over a day;
  the day's digest is pinned and identical in default, `DOTNET_EnableHWIntrinsic=0` and `DOTNET_EnableAVX2=0`.
- **Analytic control tests** (`QstsControlTests`): deadband, definite delay (first tap at exactly the delay, later ones
  at the inter-tap delay), sequential timing, inverse time (the first step whose accumulated weighted time covers the
  delay), the minimum delay, catch-up at 900 s steps, tap counting, LDC against a hand phasor calculation, bank delays,
  dead time, time and var control, volt-var / volt-watt break points, the response lag, var and watt priority, and the
  storage energy balance to the bit (dyadic values make every term exact).
- **OpenDSS cross-check** (`QstsOpenDssCrossCheckTests`; reference `tools/crossval/crossval_qsts_opendss.py`, data in
  `data/qsts`; DSS C-API 0.14.5 based on OpenDSS SVN 3723, DSS-Python 0.15.7, OpenDSSDirect.py 0.9.4). The same balanced
  feeder (stiff 33 kV source, 33/11 kV 10 MVA transformer with its regulator on the HV winding taps, four sections,
  constant-power loads, a voltage-controlled 0.3 MVAr bank):
  - daily, 96 x 15 min, ControlMode=STATIC (banks ordered before the regulator): tap and bank state equal at every
    step (3 tap and 4 bank operations); largest voltage difference 1.8e-11 pu;
  - 20 minutes at 1 s, ControlMode=TIME (regulator 30 s / 5 s, bank 60 s): taps at 30, 35 and 90 s and the bank at
    120 and 660 s in both; largest voltage difference 7.9e-12 pu.
  The residuals were traced to their sources on the way: OpenDSS's default tolerance 1e-4 (2e-5 pu), the transformer
  anti-float shunt (`ppm_antifloat`, about 5e-8 pu), and the source strength (a 1e9 MVA source drops 5.5e-9 pu; 1e13 is
  used).
  **One difference in control semantics**, pinned in its own test: OpenDSS starts a bank's delay at any exit of its band,
  even one asking for a state the bank is already in (the OFF setting crossed while open at 35 s), and at the end of the
  delay acts on the condition then present (closing at 95 s); GridSim times the operation the voltage asks for (from
  60 s, closing at 120 s).

## 37.7 Hosting capacity

For each candidate bus, the largest size of the resource (PV as a QSTS PV unit, or added load) such that the run breaks
no judged constraint: over-voltage, under-voltage, thermal, reverse power through a named branch (the protection proxy),
more tap operations than the base run plus an allowance (QSTS only), non-convergence. A constraint the base case already
breaks counts only where the resource makes its excess strictly larger.

Snapshot method: one regulated step per snapshot (demand multiplier, resource multiplier), every control delay taken as
zero. QSTS method: the full study with the resource driven by a profile; a violation counts after the allowed time.

Search: MaxMw first (if it passes, the capacity is "above MaxMw"), then bisection of [0, MaxMw], mid = lo + (hi - lo) / 2,
until hi - lo <= ToleranceMw: 1 + ceil(log2(MaxMw / ToleranceMw)) evaluations. The reported capacity is the passing end
(never above the limit, at most the tolerance below it when the limit is monotone in the size) and the limiting
constraints are those at the failing end. Candidates run in parallel with one engine each and land by index, so the thread
count changes no number. Tested (`HostingCapacityTests`): the bracket holds the analytic over-voltage limit of a two-bus
feeder (P = g V^2 - V (g cos d + b sin d) with Q = 0) within 0.001 MW in exactly 16 evaluations; thermal, reverse-power
and under-voltage limits are named; base violations are not blamed; the snapshot at minimum demand and full sun is no
less conservative than the time series; one thread and four give the same answers.

## 37.8 Limits of this release

- Positive sequence only: QSTS runs on the balanced `GridModel`; the unbalanced three-phase sweep of ch. 18 is not
  time-stepped.
- Timed mode does not model transformer flow controls (PSS/E COD 2 and 3); a case that has them is refused in timed mode
  (steady mode runs them through the tap loop).
- With both banks and tap changers at zero delay, the timed order (banks settle before each tap round) differs from the
  nested existing loops (the shunt loop re-settles inside every tap pass, from its initial steps), so the end state can
  differ on a knife edge; each alone is bit-identical.
- Storage is an energy reservoir with constant efficiencies; the chemistry, temperature and ageing model of ch. 27 stays
  with `chain`.
- The hosting-capacity bisection assumes a constraint that, once broken, stays broken at larger sizes; the tap-operation
  criterion need not be monotone, and the result is then one valid bracket, not necessarily the smallest failing size.
- With many coupled inverters the per-unit continuous settle runs until its stall watch trips (no halving over 25
  rounds and no prospect of settling by the cap) before the accelerated iteration takes over; before the watch it spent
  its full cap (200
  rounds by default, about 30 s of the first step on `gb-full` with 1,861 volt-var units on a laptop, Release build).
  Later steps of the run start in the accelerated iteration. A step that the accelerated iteration does not
  settle within its own cap is still reported unsettled (exit 3 without `--allow-unsettled`), never settled. A
  tolerance far below the power flow's own convergence (1e-12 of the rating on `gb-full`) cannot be met by either.
