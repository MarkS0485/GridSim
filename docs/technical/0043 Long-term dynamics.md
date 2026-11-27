# Long-term dynamics (QSS)

GridSim runs long-term studies by quasi-steady-state (QSS) simulation, following Van Cutsem and Vournas, *Voltage
Stability of Electric Power Systems* (Kluwer, 1998), chapter 6, and Van Cutsem's QSS lecture notes (University of Liege).
The fast dynamics (rotor swings, exciters, the governors' own lags) are replaced by their equilibrium, a power flow. The
slow elements are stepped with a fixed QSS step h: automatic generation control, tap changer timers and recovering loads.
The code is in `src/GridSim.Core/Dynamics/LongTerm/` (`QssEngine`, `AgcModel`, `OltcTimers`, `LoadRecoveryQss`,
`LongTermCase`), the verb is `gridsim long-term` ([user manual ch. 19](<../manual/0019 CLI reference.md#long-term>)), and
the tests are `tests/GridSim.Tests/Rc5Wp21LongTermTests.cs`.

The same package gives the full RMS model a user-chosen step schedule and an error-controlled step; they are described
at the end of this chapter.

Both are additive. The new verb, the new study options and the new Core entry points change no existing output.

## The step

Step n is at time t = n h. In order:

1. **Disturbances.** Each disturbance takes effect at the first step with `n h + h 1e-6 >= t_event`, the RMS engine's
   stage rule. Simultaneous ones are applied in input order. The kinds are a branch trip, a generator trip, a
   constant-power load step and a change of a generator's scheduled power. A disturbance at t = 0 is refused: the start
   is the undisturbed state the loads and timers begin from.
2. **Equilibrium.** A Newton power flow with one system frequency deviation df (below).
3. **Sample.** df, the lowest voltage, the AGC set points, the taps and the recovering loads are recorded.
4. **Slow states.** AGC, then the tap changer timers, then the recovering loads advance over h from this step's
   equilibrium.

At t = 0 the sample is the starting power flow itself, with df = 0. The units at the reference bus are scheduled at
their solved output there, so the undisturbed system is in equilibrium.

## Equilibrium with one frequency

Each governed unit i has a droop R_i (pu on its base MBase_i). Its response is `1/R'_i = MBase_i / (R_i f0)` MW/Hz. The
loads give `D` MW/Hz between them, in proportion to their starting active power. The natural response is

    beta = D + sum_i 1/R'_i    (MW/Hz)

A governed unit produces `P_i = P_sched,i + P_agc,i - df / R'_i`, held within [Pmin, Pmax]. Other units hold their
schedule plus any AGC share. A voltage-controlled bus whose units have all tripped becomes a load bus. Tripping the last
unit at the reference bus is refused.

The equilibrium is found by an outer loop around an ordinary Newton solve. Each pass solves the power flow at the current
df and reads the reference bus's generation less its allocation, e (MW), from the solved injection plus the load the bus
draws. The frequency is then corrected:

    df <- df - e / (D + sum of 1/R'_i over the units not at a limit)

The loop stops when `|e| <= equilibriumToleranceMw` (default 1e-6 MW). It starts from the previous step's df. On a
lossless network one correction settles it, and then `df = -dP / beta` for an imbalance dP. When every governed unit is
at a limit and an imbalance is left, or a power flow does not converge, the run stops with "loss of short-term
equilibrium" and the time. It does not carry on. When GridSim's own frequency loop runs out of passes
(`maxEquilibriumPasses`), the run stops with "numerical failure" and the time instead: the loop failed, not the
system's equilibrium. The loop keeps the frequencies seen on each side of the balance and bisects whenever a step would leave
them, because a unit reaching or leaving a limit puts a kink in the balance that a plain step can cycle across.

The specification proposed `DistributedSlackPowerFlow` with factors 1/R. That solver stops at 1e-4 MW and has no load
damping, so the engine uses its own loop with the same reading of the reference bus's excess and a tolerance set in the
input.

## AGC

Single area. With the bias B (default beta):

    ACE    = B df                    (the tie-line deviation is 0 for one area)
    P_agc <- P_agc - Ki ACE h        (forward Euler of dP_agc/dt = -Ki ACE)

An optional rate limit clamps the change in a step to `+/- rate h`. Each AGC unit takes its participation factor's share,
normalised over the AGC units still in service. The integrator does not wind up: while every AGC unit
still in service is held at Pmax, a raise is not integrated, and while every one is held at Pmin, a lower is not, so the
control acts at once when the error changes sign.

After a load step dPL with the primary response at equilibrium, `df = -(dPL - P_agc) / beta`. With B = beta,
`ACE = -(dPL - P_agc)`, so

    P_agc(n+1) = P_agc(n) + Ki h (dPL - P_agc(n)),    P_agc(n) = dPL (1 - (1 - Ki h)^n)

The tests check this sequence exactly: 100 MW, Ki = 0.01 /s and h = 1 s give 63.396765872677086 MW at 100 s (the
continuous limit is 63.212055882855765 MW).

## Tap changer timers

The tap changer itself (target, deadband, step, limits) is the case's `Oltc` record; the timing (`Tdel`, `Tdelstep`) is
in the long-term input. Each step the timer reads the controlled voltage of that step's equilibrium. Out of the band
`target +/- deadband` it counts one more whole step. When the count reaches `ceil(Tdel / h)` steps (first move) or
`ceil(Tdelstep / h)` steps (later moves), the tap moves one step toward the band, effective at the next equilibrium, and
the count restarts. Back in band the count and the first-move state reset. The time is counted in whole steps, never
summed from h. The direction rule is the steady-state tap loop's: the tap sits on the From end, so a controlled From bus
rises with the tap and any other controlled bus falls with it. A tap at a limit holds. A tap changer of the case with no
timer holds its tap.

## Recovering loads

The exponential recovery load of Karlsson and Hill (IEEE Trans. Power Systems 9(1), 1994) replaces the bus's whole
constant-power load. With P0, Q0 the bus's starting load and V0 its starting voltage:

    Tp dxp/dt = -xp + Ps(V) - Pt(V),    Pd = xp + Pt(V)
    Ps = P0 (V/V0)^alphaS,              Pt = P0 (V/V0)^alphaT

and the same for Q with Tq, betaS and betaT. The state advances by forward Euler with the step's voltage held:

    xp(n+1) = xp(n) + h / Tp (-xp(n) + Ps(V) - Pt(V))

Under a held voltage that is the exact geometric sequence `xp(n) = (Ps - Pt)(1 - (1 - h/Tp)^n)`. The tests use it: V
stepped from 1.0 to 0.9 with alphaS = 0, alphaT = 2 and Tp = 60 s gives Pd = 0.9306894609568648 after 60 steps of 1 s
(the continuous value is 0.930102906177426). A step of 2 Tp or more is refused (forward Euler is unstable there).

The power flow holds the transient part as its polynomial load model, so alphaT and betaT must be 0, 1 or 2: the bus
carries P0 with the multiplier `(xp + relief) / P0 + (V/V0)^alphaT`, so the solved load is exactly `xp + relief + Pt(V)`.
The steady-state exponents may be any finite value.

## Output

`long-term.csv`: one row per step with t, df, the lowest voltage and its bus, each AGC unit's set point change, each
timed tap, P and Q of each recovering load, the equilibrium passes, the residual e and the disturbances applied at that
step. The result also lists each disturbance with its requested and applied time and every tap move.

## Scope

- QSS removes the fast dynamics. There are no electromechanical oscillations and no instability of the fast subsystem.
  When the equilibrium power flow fails, the run stops and says so.
- Frequency is one value for the system, and AGC is single area. A case must have exactly one reference bus.
- Not modelled: over-excitation limiters and timed switched shunts. Reactive limits are those the power flow applies.
- Verified against the exact discrete solutions above and against independent Newton solves for the tap timings. Not
  compared with PSS/E or PowerFactory long-term runs.

## Step schedule for the full RMS model

`RmsStepSchedule` lets the full model (`MultiMachineSubtransient`) take fixed steps of a different size in each window:
`[ { from 0, dt 0.001 }, { from 0.5, dt 0.005 } ]`, for example. Entry points: `RmsTransientStability.StudyEventsScheduled`,
`StudyEventsObservedScheduled`, and `integration.stepSchedule` in `dynamics-study`.

- The first window starts at 0, starts rise, and each window holds a whole number of its own steps (its length over its
  step is within 1e-6 of an integer, the tolerance the stage times use). So each window boundary lies on the step grid of
  the window before it.
- The sample times are `from + j dt` in each window; the last is the end of the run. In the first window that is the
  plain kernel's `k dt`, so a one-window schedule is the plain fixed-step run bit for bit.
- An event strictly inside a window whose step is larger than the schedule's smallest is refused: start a window at the
  event. Inside a window with the smallest step an event lands by the usual stage rule. Events are placed before the
  window grid is checked, so such an event is named even when its window is also off the grid.
- The units' own relays refuse any schedule. With more than one step size, the fuller composite load and every user or
  plug-in model (a user injection, a user dynamic load, a user or plug-in field controller) are refused, in the library
  API as in `dynamics-study`: GridSim cannot check that a user model takes the step on each call.
- The kernel passes the step in force to every component on every call. Each model class was checked for a step stored
  at construction: none stores one. The flux lags, exciters, stabilisers, converter and device models and dynamic loads all
  take the step per call. Three things are built for one step and refuse a schedule with more than one step size: the
  governor hook of the fixed-step path (a scheduled run builds a step-sized hook with the same expressions), the units'
  own relays (`FullModelUnitRelays`), and the fuller composite load parts (motor D, the under-voltage relays and the tap
  changer count whole steps). The MTDC droop check runs on every step with that step's size.
- With no schedule the kernel runs every line as before. A one-window schedule at dt is checked against digests taken
  from the 2.1.122 build before the change.

## Error-controlled step for the full RMS model

**EXPERIMENTAL.** The error-controlled step is opt-in and is not part of the verified contract. Its error figure is a
first-order step-doubling estimate, not a bound, and a run can cost up to 2 x `maxPasses` full simulations (24 with the
defaults). Results that use it say so (`RmsErrorControl.ExperimentalNote`).

`RmsErrorControl` chooses the schedule. A classic controller rejects a step and retries it from the saved state, but the
full model has about 40 component classes that each keep their own integrator state with no snapshot. The kernel is
deterministic, so a run can be repeated to the bit from its inputs. Instead of taking a step back, the controller re-runs
the whole simulation with a finer schedule. No component needs a snapshot.

- The run is cut into segments of `segmentSeconds` (default 0.1 s), with a segment starting at every event time, so every
  event lands exactly on its own time. Segment s steps by `dt_s = len_s / 2^L_s`, starting with the smallest L_s that
  keeps dt_s at or below `maxStepSeconds`.
- Each pass runs the schedule S and S/2 (every step halved). Every sample time of S is also a sample time of S/2, bit for
  bit: `(2j)(dt/2) = j dt` in binary floating point.
- At every sample of S every recorded state is compared. Machine angles are scaled by `atol + rtol x 1 rad`,
  because an angle has no natural size. Everything else is scaled by `atol + rtol max(|x_S|, |x_S/2|)`: each machine's
  speed and four flux states, each converter's terminal voltage, P, Q, active and reactive current (and a plant
  controller's regulated voltage), and every recorded state of each network device. Exciter, governor and dynamic-load
  internal states are not recorded by the kernel, so they count only through the machine and converter quantities they
  drive. A segment whose largest scaled difference is above the safety factor (default 0.5) has its step halved, and the
  next pass runs.
- The loop stops when no segment is above the safety factor, when every such segment is already at the smallest step,
  or after `maxPasses`. The run returned is S/2 of the last pass, with its schedule. Running that schedule again gives the same
  bits.

The difference is measured on the whole trajectory, so it includes error carried in from earlier segments: the control
is on the trajectory a user reads, not on a local per-step error. For a method of order p in its asymptotic range the
error of S/2 is about `|x_S - x_S/2| / (2^p - 1)`, at most the measured difference for p of 1 or more; the kernel's
splitting is first order. That is an estimate, not a bound. Outside the asymptotic range (a loss of synchronism, a limit
reached at a different step) it can be wrong, and the result says when the tolerance was not met. In `dynamics-study` a
row whose tolerance was not met is INCOMPLETE, unless both runs of the last pass lost synchronism.

Cost: each pass is two full runs, and a segment is never coarsened again. The tests check the controller on forward Euler
of `dx/dt = -x`, whose samples are known in closed form, with the expected step levels found by a separate search; and on
the full model for exact event landing, the re-run of the chosen schedule and determinism
(`Rc5Wp21ErrorControlTests.cs`, `Rc5Wp21StepScheduleTests.cs`, `Rc5Wp21BitIdentityTests.cs`).
