### motor-start

`gridsim motor-start <study.json> [--out <result.json>]`

Motor acceleration study from a native JSON operating point (RC3 P8). It starts one placed induction motor of a
gridsim-case/2 case (a `motors` row of its network file: see the v2 schema guide), direct-on-line or through a
reduced-voltage starter, and integrates its acceleration with the network solved at every step. A group study starts
several motors, each at its own time and by its own method, on one network. It never calls the steady-state induction machine solve: the slip
is derived from rotor speed (1 at standstill) and the single-cage equivalent circuit is evaluated at that slip. The
existing power flow, the steady-state induction machine model and every other verb are unchanged.

The study file is strict JSON, schema version 1: `schemaVersion` (1); `case` (`path` of a gridsim-case/2 manifest,
relative to the study file, and `sha256`, the digest of the consumed-file manifest exactly as `transmission-security`
computes it for a native case: one `path<TAB>sha256` line per consumed file, the manifest, its network, asset and library
files and the v2 schema set; a mismatch is refused and prints the resolved identity); `motor` (the motor row's name);
`initial` (`speedPu`, at least 0 and below the running speed, and `connected`, which must be false: the start event
connects the motor, and a motor already connected before it is refused); `start` (`atSeconds`, `method`: `directOnLine`,
`starDelta`, `autotransformer`, `reactor`, `softStarter` or `vfd`, and for every method but `directOnLine` a `starter`
object, below); `integration.stepSeconds`; `termination` (`runningSpeedPu`, `horizonSeconds`,
`stallDwellSeconds`, `stallAcceleratingTorquePu`); `preStart` (`pgToleranceMw`, `vmTolerancePu`, `maxSlackMovementMw`);
optional `report` (`sampleEverySteps`, default 1, and `maxVoltageDipPercent`, a reported flag). The start time and the
horizon must be whole numbers of steps. Unknown or duplicated fields, non-finite numbers, a v1 case folder (it has no
motor rows) and an unknown motor are refused before any solve.

The `starter` object takes only the fields of its method; a field of another method is refused by name. Every time in
it must be a whole number of steps. For `starDelta`, `autotransformer` and `reactor`, an `initial.speedPu` at or above
the switch speed is refused: the starter would switch at once and the start would be direct-on-line.

| Method | Fields | What the motor sees |
|---|---|---|
| `starDelta` | `switchSpeedPu` (default 0.8), `switchTimeSeconds` (optional), `deadTimeSeconds` (default 0) | V/sqrt(3) on each winding until the switch to delta, at the switch speed or time, whichever comes first. The line current and the torque are a third of direct-on-line. The transition is open: during the dead time the motor is disconnected and coasts under its load. A connection in delta below the breakdown (pull-out torque) speed is noted as "transition current peak"; with a dead time the note is on the reconnection in delta, where the peak is. |
| `autotransformer` | `tapRatio` k (0 to 1), `switchSpeedPu`, `switchTimeSeconds` | k V. Motor current k times direct-on-line, line current and torque k squared. Ideal transformer, magnetising current ignored; the switch to full voltage is a step (the Korndorfer closed transition approximated as instantaneous). |
| `reactor` | `reactorRPu` and `reactorXPu` (pu on the motor base), or `reactorROhm` and `reactorXOhm` (ohms are taken only for a reactor; on another method they are refused by name); `switchSpeedPu`, `switchTimeSeconds` | A series impedance: I = V / \|Z_motor + Z_r\|, bypassed at the switch. |
| `softStarter` | `initialVoltageFraction` V0, `rampSeconds`, `currentLimitPu` (optional, pu of rated current) | A voltage ramping linearly from V0 to 1 over the ramp, then bypassed. With a current limit the fraction is lowered so the line current stays at or below the limit (settled to 1e-9 relative). It is a series controller: the line current is the motor current, the active power is the motor's, and the rest of the apparent power is reactive. Fundamental frequency only. |
| `vfd` | `initialFrequencyHz` f0, `ratedFrequencyHz` fn, `rampSeconds`, `driveEfficiency` (default 0.97), `currentLimitPu` (optional) | Plain constant V/f: V = f/fn of rated voltage, with no stator-resistance (IR) compensation, so the torque at low frequency is lower than a drive with boost would give; the frequency ramps from f0 to fn; the circuit's reactances scale with f/fn and the slip is taken against the supply frequency. The line side draws the motor's input power over the efficiency at unity power factor, a constant-power load at the bus (refused on a bus whose demand varies with voltage). The ramp pauses while the motor current is above the limit. A start with `initial.speedPu` above f0/fn is refused: the slip would be negative and the drive would regenerate (a flying restart), which is not modelled. |

Starter inputs are held over each step, and the predictor solve of Heun's method uses the same stage. With a starter the
sample's current, P and Q are what the bus supplies, and its terminal voltage is the motor's own; `starterSamples` (one
per sample: stage, voltage factor, drive frequency, motor voltage, motor current, line current and motor input power) and
`starterTransitions` (each stage change with the line current just before and just after, at the same speed) are added
to the result. A run without a starter writes neither. `RUNNING` needs the starter in its final stage (switched,
bypassed, or the drive at rated frequency); a motor already above the running speed then is `RUNNING` at that step. The
stall dwell is counted in whole steps and does not run while the motor is disconnected.

A group study replaces `motor`, `initial` and `start` with `group`: an array of objects, each with its own `motor`,
`initial` and `start` (method and starter included). The network is solved each step with every connected motor. The
result has one entry per motor in `motors` (status, acceleration and decision times, starting, peak and final states,
samples, starter samples and transitions) and the deepest dip at any bus over the whole run in `voltageDip`. The group
status is `RUNNING` only when every motor runs; otherwise `NO SOLUTION` if a step failed, `STALLED` if any motor
stalled, else `HORIZON ENDED`. The exit code follows the group status. A motor whose outcome is decided stays on the
network while the others run, and a stage change its starter still makes then (a timed switch after a stall, say) is
recorded in its transitions with a note. Results are deterministic for a given member order; with three or more motors
on one bus the order of the list fixes the order in which their loads are summed, so reordering the list can change
results at rounding level.

The starter models are fundamental-frequency and quasi-steady, as the direct-on-line study: no switching transients, no
closed-transition detail, no drive harmonics, single-cage motors only. They are verified against the closed forms above
on a stiff bus; they have not been compared with ETAP or PowerFactory motor-start runs.

The case is loaded with loader balancing suppressed at the loader seam, whatever its `system.dispatch` says, and the
authored generator schedule is read from the network files: every in-service generator must give `outputMw`, `pMinMw`
and `pMaxMw`. The result keeps three states apart: the authored schedule against the resolved model (a moved unit
stops the study), and the realised pre-start AC solution with the motor disconnected (incumbent Newton, 100 iterations,
reactive limits enforced, no OLTC loop): each non-slack generating bus at its schedule within `pgToleranceMw`, each
voltage-controlled bus at its setpoint within `vmTolerancePu` (a bus released at a reactive limit fails), each
generating bus within its summed unit bounds and the slack movement within `maxSlackMovementMw`. A failed check is
`INCOMPLETE` and no event is run.

From the event, at each step: the motor is the constant-impedance load its circuit is at the step's slip, the network is
solved once and verified (a step with no converged solution is `NO SOLUTION` with the solver's exact status), and the
swing equation 2 H dw/dt = Te - Tl is advanced by Heun's method (a predictor network solve, then the averaged
accelerating torque). Torques are per unit of the motor torque base (rated MVA over synchronous mechanical speed);
speed is per unit of synchronous speed. Every solve starts from the authored bus voltages.

Outcomes (`status`): `RUNNING` (the speed reached `runningSpeedPu`; the acceleration time is the interpolated crossing
less the start time), `STALLED` (the accelerating torque stayed at or below `stallAcceleratingTorquePu` for
`stallDwellSeconds` below the running speed: locked, decelerating or crawling), `NO SOLUTION`, `HORIZON ENDED` (no
decision by the horizon) and `INCOMPLETE` (pre-start point not reproduced). The result JSON (`schemaVersion`,
`provenance` with the study and case digests, every consumed input and the engine identity, and `result`: the status,
outcome and termination criterion, the motor row, the request, the load policy, the declared method, the generator
states, the pre-start verification, the acceleration and decision times, the starting, peak-current and final states,
the voltage dip at the motor bus and the deepest at any bus, the number of network solves, the sampled trajectory with
current in pu and kA, P and Q, speed, slip and torques, and the reasons) is printed to stdout and, with `--out`, written
to a new file via a temporary file and a final move. An existing `--out` path is refused.

Exit 0 `RUNNING`. Exit 1 for input it cannot accept (a malformed study, a case identity mismatch, an unreadable or
invalid case, a generator without an explicit schedule, an existing output path). Exit 2 for an invalid invocation.
Exit 3 `STALLED`, `NO SOLUTION`, `HORIZON ENDED` or `INCOMPLETE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
