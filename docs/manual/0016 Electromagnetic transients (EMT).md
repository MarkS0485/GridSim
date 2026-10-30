# 16. Electromagnetic transients (EMT)

`gridsim emt` steps the instantaneous network (`L di/dt`, `C dv/dt`) through time at a fixed step of microseconds and
records the waveform itself: every sample of a bus voltage or branch current, not a phasor. You use it when the
question is about what happens in the first milliseconds after something changes: a fault applied and cleared, a
breaker or capacitor switched, the voltage that recovers across a switch, or a wave travelling along a line. The output
is a trajectory of the model. It is never a statement about the real grid.

## When to use EMT, and when not to

| Question | Use |
|---|---|
| What are the steady-state voltages and flows? | A power flow ([5. Running power flows](<0005 Running power flows.md>)). |
| How does the system frequency move over seconds after a loss of generation? | [14. Frequency studies](<0014 Frequency studies.md>), a centre-of-inertia model of the system frequency over seconds. |
| What do harmonic currents do to the network at steady state? | [17. Running a harmonic study](<0017 Harmonic studies.md>), the frequency-domain method. |
| What is the peak voltage or current in the first cycles after a switching event, and how does it ring? | `gridsim emt`. |

An EMT run does one forward and back substitution of the stored LU factors per time step (20 microseconds by default)
and refactorises only at a switching event, so it is meant for milliseconds to a few cycles. The 0.05 s run of the
worked example is 2,500 steps and took 16 ms on the captured machine (LAPTOP, 8 logical cores); the table under
[Choosing the time step](#choosing-the-time-step) lists wall times from 250 to 50,000 steps. A phasor study cannot show the DC offset of a fault
current, a recovery overshoot or a travelling wave, because it never represents the waveform. EMT does not model
machines, controls or protection: the network is passive R, L and C elements, one ideal source and any switches you
schedule (see [Limits](#limits)).

## Before you start

- A case, in any form [19. CLI reference](<0019 CLI reference.md#case-arguments>) lists. With no case argument the run uses
  the `uk` model. All the examples here use bundled MATPOWER cases.
- All quantities are per-unit on the case's system base. Time is in real seconds. Inductance and capacitance are
  derived from the case's per-unit reactance and susceptance at the case's own nominal frequency
  (`L = X/omega0`, `C = (B/2)/omega0`, `omega0 = 2 pi f0`). The MATPOWER cases run at 50 Hz, because MATPOWER files
  carry no frequency and the importer assumes 50 Hz (`--frequency-hz` declares another); `case9` is the 60 Hz WSCC
  system and runs at 60 Hz.
- The source frequency (`--freq`) is independent of the case frequency. Set `--freq` to the case frequency for an AC
  study (see [Pitfalls](#pitfalls)).
- The full flag list is in [the `emt` section of the CLI reference](<0019 CLI reference.md#emt>).

## Worked example

Apply a fault of 0.01 pu resistance at bus 5 of the IEEE 14-bus case at 10 ms, clear it at 30 ms, and
record the bus 5 voltage and the current in branch 1-5, with a 50 Hz source at bus 1:

```bash
gridsim emt case14 --duration 0.05 --freq 50 --event fault:5@0.01:0.01 --event clear:5@0.03 --dt-us 20 --probe v:5,i:1-5 --out emt-fault
```

```
$ gridsim emt case14 --duration 0.05 --freq 50 --event fault:5@0.01:0.01 --event clear:5@0.03 --dt-us 20 --probe v:5,i:1-5 --out emt-fault
emt case14: 2,500 steps @ 20us, 5 factorization(s), peak |probe| 4.364, 16 ms -> emt-fault
emt: 2 floating node(s) pinned at 0 V in the initial-condition solve.
[exit 0]
```

Three files appear in `emt-fault`. `summary.md` restates the run:

```
$ cat emt-fault/summary.md
# EMT run: case14

- steps: 2,500 @ 20 us (dur 0.05s)
- source: bus 1, amp 1 pu, 50 Hz
- refactorizations: 5
- peak |probe|: 4.3636
- wall: 15.9 ms (156,918 steps/s)
- rule: Trapezoidal, CDA: True

A forward model trajectory, not a claim about the real grid.
[exit 0]
```

`timeseries.csv` holds the waveform. These are its header and the rows around the three moments of interest (the start,
the fault at 10 ms, the clearing at 30 ms and the end):

```
$ cat emt-fault/timeseries.csv
# GridSim  -  2026-10-03 07:48:23 UTC
# code:    git 71c4e64f-dirty  |  GridSim.Core v1.95.91.0  |  build Release
# runtime: .NET 10.0.12  X64  GC=Server
# machine: LAPTOP  Microsoft Windows 10.0.26200
# cpu:     X64  -  8 logical cores  -  8 GB RAM
# simd:    Avx256  (4 doubles/step)  FMA=True
# backend: cpu
# run:     emt case14 dt=20us dur=0.05s src=bus1 amp=1 freq=50Hz rule=Trapezoidal
t_seconds,v:5,i:1-5
0,0,0
2E-05,0.00208537629,0.0281196147
4E-05,0.0082627115,0.0560790564
...
0.00996,-0.89793361,-0.665286924
0.00998,-0.89879204,-0.667134707
0.01,-0.0150150838,-0.692540163
0.01002,0.00481759349,-0.719492316
0.01004,-0.0139926538,-0.746416757
0.01006,-0.00313357157,-0.773186115
0.01008,-0.0148277463,-0.799899673
...
0.02998,-0.0341363571,-1.29817955
0.03,-0.540118702,-1.31267861
0.03002,-1.03093107,-1.31671919
0.03004,-1.4906826,-1.30737368
0.03006,-1.90437717,-1.28574674
0.03008,-2.25935233,-1.25333063
...
0.03016,-2.94333215,-1.05481048
...
0.05,-0.898367441,-0.663102626
[exit 0]
```

Read it as follows.

- Before 10 ms the bus 5 voltage swings to -0.8988 pu, a 50 Hz waveform of amplitude 0.9098 pu once the start-up
  has passed. The branch 1-5 current is -0.667 pu at that instant.
- At 10 ms the fault switch closes. The row at `t = 0.01` already shows the faulted state: the bus voltage falls from
  -0.8988 pu to -0.0150 pu, a collapse limited only by the 0.01 pu fault resistance, and the branch current starts to
  climb (the peak of 4.364 pu in the console line is that current).
- The small values at 0.01002 to 0.01008 s (+0.0048, -0.0140, -0.0031, -0.0148) are numerical ringing that Critical
  Damping Adjustment leaves behind; the first two alternate in sign (see [Numerical chatter and CDA](#numerical-chatter-and-cda)).
- At 30 ms the fault opens at the instant requested, with whatever current the fault switch is carrying. There is no
  wait for a current zero, so the bus voltage does not recover to its steady 0.9 pu amplitude. It overshoots to
  -2.943 pu at 0.03016 s, 3.2 times the pre-fault amplitude, before settling back. That overshoot is the transient
  recovery voltage of the modelled circuit, and it is why a step small enough to resolve it matters (see
  [Choosing the time step](#choosing-the-time-step)).

The time stamps, machine lines and wall times differ by machine and run. On one machine and runtime the same command
gives the same probe values every time. This run uses an AC source, which evaluates the deterministic `DetMath.Cos`; no AC
run is pinned by a digest yet. The cross-machine pins are SHA-256 digests of DC runs of five cases (gb-full, uk, case9,
case30 and case33bw: sampled node voltages and the first 20 branch currents over 40 ms at 20 microseconds), listed in
the technical chapter.

## Reading the output

### The console

The last console line has a fixed shape. In the worked example it is
`emt case14: 2,500 steps @ 20us, 5 factorization(s), peak |probe| 4.364, 16 ms -> emt-fault`, which is these parts in order:

- `emt case14` is `emt` and the case argument as typed, or `uk` when there is none.
- `2,500 steps` is `duration / dt` rounded to the nearest whole step, with a thousands separator. The CSV has one more row
  than this, because it also holds the row for `t = 0`.
- `@ 20us` is the step in microseconds as given to `--dt-us`.
- `5 factorization(s)` counts the nodal matrix factorisations. One is made at the start. Each step at which a
  switching event actually changes a conductance adds two with Critical Damping Adjustment (the matrix is refactored for
  the half step and again for the full step) and one without it, and with `--rule be`. The worked example has two such
  steps (the fault and the clearing) and reports 5 = 1 + 2 + 2. The same run with `--no-cda` reports 3.
- `peak |probe| 4.364` is the largest absolute value of any probe at any step from step 1 onwards. The `t = 0` row is excluded.
  With several probes it is the maximum over all of them, so it can be a current when you meant a voltage: in the
  worked example the 4.364 is the branch current, and the bus voltage alone peaks at 2.943 (see the table under
  [Choosing the time step](#choosing-the-time-step)). The console prints four significant figures.
- `16 ms` is the wall time in the integration loop. It differs by machine and run.
- `-> emt-fault` is the directory the files were written to.

When the initial-condition solve finds buses with no resistive path to a reference, one earlier line goes to the
error stream:

```
emt: 2 floating node(s) pinned at 0 V in the initial-condition solve.
```

It is information, not a failure: the run continues and the exit code stays 0. See [Starting from rest](#starting-from-rest).

### `summary.md`

Every line of the file, in order:

| Line | Meaning |
|---|---|
| `# EMT run: case14` | The case label, as on the console. |
| `- steps: 2,500 @ 20 us (dur 0.05s)` | Step count, the step in microseconds and the requested duration in seconds. |
| `- source: bus 1, amp 1 pu, 50 Hz` | The one energising source: its bus, its amplitude in pu and its frequency. A DC source prints `DC` where the frequency would be. |
| `- refactorizations: 5` | The same count as the console's factorisations. |
| `- peak |probe|: 4.3636` | The peak over all probes and steps, to at most six significant figures (the console prints four). |
| `- wall: 15.9 ms (156,918 steps/s)` | The integration wall time and the steps per second it implies. |
| `- rule: Trapezoidal, CDA: on` | The base integration rule and whether CDA ran: `on`, `off (--no-cda)`, or `off (not used under backward Euler)` under `--rule be`, where CDA never acts. |
| `A forward model trajectory...` | The standing reminder that the output describes the model. |

### `timeseries.csv`

- Comment lines beginning with `# ` come first: the run stamp, code version, runtime, machine, CPU, SIMD tier, backend
  and a `run:` line that records the case, step, duration, source bus, amplitude, frequency and rule.
- The column header is `t_seconds` followed by one column per probe, named exactly as you wrote the probe (`v:5`,
  `i:1-5`). With no `--probe` the columns are `v:<source bus>` and `i:<from>-><to>` of the first series branch, as in
  `i:1->2` below.
- Each following row is a time in seconds and the probe values, written with nine significant figures (`2E-05` is
  20 microseconds). Voltages are per-unit; currents are per-unit on the system base and positive from the `from` bus to the
  `to` bus.
- The first row is `t = 0` and is always zero for voltage probes. It is a placeholder, not the rest solution (see
  [Starting from rest](#starting-from-rest)). The first real sample is at `t = dt`.
- Loaders that do not skip `#` lines need to be told to; most plotting tools take a comment character.

The default probes, with the first rows of a default run:

```
$ gridsim emt case14 --duration 0.01 --out emt-default
emt: 2 floating node(s) pinned at 0 V in the initial-condition solve.
emt case14: 500 steps @ 20us, 1 factorization(s), peak |probe| 2.902, 5 ms -> emt-default
[exit 0]

$ sed -n 9,13p emt-default/timeseries.csv
t_seconds,v:1,i:1->2
0,0,0
2E-05,1,0.105868066
4E-05,1,0.210679488
6E-05,1,0.313621015
[exit 0]
```

This run uses the default DC source of 1 pu. The source bus sits at 1 pu from the first step. The first branch current
sample is 0.105868 pu, against `V dt / L` = 0.106189 pu for the inductance of branch 1-2 (0.30 % lower).

### `run-metadata.json`

The provenance record every GridSim run writes:

```
$ cat emt-fault/run-metadata.json
{
  "timestampUtc": "2026-10-03T07:48:23.1587691Z",
  "backend": "cpu",
  "gitCommit": "71c4e64f-dirty",
  "buildConfig": "Release",
  "coreVersion": "1.95.91.0",
  "runtime": ".NET 10.0.12",
  "arch": "X64",
  "serverGc": true,
  "simdTier": "Avx256",
  "simdLanes": 4,
  "simdFma": true,
  "machineName": "LAPTOP",
  "os": "Microsoft Windows 10.0.26200",
  "cpu": "X64",
  "logicalCores": 8,
  "ramGb": 8.3,
  "run": "emt case14 dt=20us dur=0.05s src=bus1 amp=1 freq=50Hz rule=Trapezoidal"
}
[exit 0]
```

The `run` field repeats the CSV's `run:` line. A `parameters` object follows the shared fields: `verb`, `case`,
`modelName`, `dtMicroseconds`, `durationSeconds`, `steps`, `sourceBus`, `amplitudePu`, `frequencyHz`, `rule` (`trap` or
`be`), `criticalDampingAdjustment` (whether CDA ran), `noCdaFlag`, `travellingWaveLines`, `loads`, `allowNonPassive`,
`nonPassiveElements`, every probe (`name`, `kind`, `target`) and every event, each with the token as given and what it
became (bus, time, resistance and fault switch id, or the switch ids a `clear:` opened).

## Probes and events

### Probes

`--probe` takes a comma-separated list.

| Probe | Records |
|---|---|
| `v:<bus>` | The node voltage at that bus, in pu. |
| `i:<from>-<to>` or `i:<from>-><to>` | The current in the series branch from bus `from` to bus `to`, in pu, positive from `from` to `to`. |

- Bus numbers are the case's bus numbers. An `i:` probe must name a branch that is in service and modelled as a series
  arm, with the buses in the branch's `from`-`to` order. Shunt elements and loads cannot be probed.
- Where several in-service series branches join the same two buses, `i:<from>-<to>` is ambiguous and is refused;
  `i:<from>-<to>#<k>` names the k-th of them in case order. The default branch probe is labelled `#1` when its branch
  has a parallel twin.
- Under `--lines`, an `i:` probe on a travelling-wave line reads the current at the `from` end.
- Every probe must name something. A `v:` probe on a bus that is not in the case, a non-numeric bus, a branch that does
  not exist, or a token that is neither `v:` nor `i:` exits 2 before anything runs, with one `error: emt: probe '...':
  ...` line per problem, for example `error: emt: probe 'v:99': bus 99 is not in case 'IEEE 14-bus'`.

### Events

`--event` is repeatable. Each token schedules one switching action.

| Event | Effect |
|---|---|
| `fault:<bus>@<t>:<Rpu>` | At time `t` seconds, close a switch from `<bus>` to ground with closed resistance `Rpu` per-unit. |
| `clear:<bus>@<t>` | At `t`, open the fault switch that an earlier `fault:` token created at `<bus>`. |
| `switch:<id>@<t>:close` or `:open` | At `t`, close or open the switch element with that id. The action word is `close` or `open`, in any letter case; any other word exits 2. |

- Each fault bus and resistance pair adds a bus-to-ground switch to the network, open until the event closes it. Its open
  resistance is 1e9 pu. Switch ids are numbered from 1,000,000 in the order the pairs first appear, so the first fault in
  a command is switch `1000000`. A later `fault:` at a bus that is already faulted, with a different resistance, changes
  the bus's fault to the new resistance: the bus's other fault switches open at that instant and the new one closes. The
  same resistance again re-closes the same switch. The only switches the verb can create are these fault switches, so `switch:` is the way to
  open a fault without `clear:` or to re-close it:

```
$ gridsim emt case14 --duration 0.03 --freq 50 --probe v:5 --event fault:5@0.01:0.01 --event switch:1000000@0.02:open --out emt-switch
emt case14: 1,500 steps @ 20us, 5 factorization(s), peak |probe| 3.504, 11 ms -> emt-switch
emt: 2 floating node(s) pinned at 0 V in the initial-condition solve.
[exit 0]
```

- An event time is a number of seconds from 0 to the end of the run. An event is applied at the first step whose time is
  at or after `t`, less a tolerance of 1e-6 of a step. An event on a step boundary is not delayed by a step, and the sample
  at that time already shows its effect. An event at `t = 0` acts at the first step. A negative time, or a time after the
  last step, exits 2, because such an event would never be applied.
- Events at the same step are applied together. They count as one discontinuity for CDA.
- `clear:<bus>` opens every fault switch at that bus and needs a `fault:` at that bus somewhere on the command line (it
  may come first on the line). Two `fault:` events at one bus at the same instant exit 2.
- A fault resistance must be a positive number of per-unit ohms; 0 and negative values exit 2 (a bolted fault is a small
  resistance, for example `1e-4`).
- A `fault:` naming a bus that is not in the case, an unparsable time or resistance, or any other leading word exits 2.
  Every problem on the command line is listed, one `error: emt: ...` line each, before the verb exits; a token of the
  wrong shape reads `error: emt: bad --event '...' (use fault:<bus>@<t>:<Rpu>, clear:<bus>@<t>, or
  switch:<id>@<t>:close|open).`
- A `switch:` token naming an id that is not a switch exits 2 before any step is taken, and the message lists the valid
  ids: `EMT case has events that cannot act: switch event at 0.01 s names element 7, which is not a switch. Valid switch ids: none.`
- Closing a closed switch or opening an open one changes nothing: no refactorisation and no CDA at that step.

## Integration rules

The engine replaces each inductor and capacitor by a conductance in parallel with a history current source, built from
the previous step's voltage and current, and solves the resulting resistive network every step. `--rule` chooses the
formula that builds the companion.

| Rule | Flag | Order | Behaviour |
|---|---|---|---|
| Trapezoidal | `--rule trap` (default) | Second order | Accurate for smooth waveforms: halving the step quarters the error. It does not damp anything, so a discontinuity excites a sustained numerical oscillation at half the sampling frequency (25 kHz at 20 microseconds) that decays only through real resistance. |
| Backward Euler | `--rule be` | First order | Unconditionally stable and strongly damping: a discontinuity leaves no ringing, but the rule also damps real oscillations and halving the step only halves the error. |

Choose trapezoidal (the default) for results you intend to quote. Pair it with CDA for runs that have switching events:
in the worked example CDA cuts the first sample-to-sample swing after the fault from 0.184 pu to 0.020 pu (9.3 times)
and keeps second-order accuracy away from the events. Choose backward Euler for a quick look at the overall shape of a response with a coarse
step, for a stiff study in which any ringing would be a distraction, or to cross-check a trapezoidal result: a feature that
survives both rules is physical, and one that exists only in the trapezoidal trace is numerical.

The accuracy difference is measurable. This is the overshoot at bus 5 after the fault in the worked example, as a peak
|`v:5`|, for both rules at three steps. The reference is the 1 microsecond trapezoidal run, 2.999 pu (see the table
under [Choosing the time step](#choosing-the-time-step)):

| Step | Trapezoidal peak | Backward Euler peak |
|---|---|---|
| 100 microseconds | 2.513 pu (16.2 % low) | 1.992 pu (33.6 % low) |
| 20 microseconds | 2.943 pu (1.9 % low) | 2.671 pu (10.9 % low) |
| 5 microseconds | 2.990 pu (0.3 % low) | 2.909 pu (3.0 % low) |

A whole-run backward Euler capture, with a longer step and without CDA:

```
$ gridsim emt case24 --duration 0.02 --dt-us 50 --rule be --probe v:1 --no-cda
emt case24: 400 steps @ 50us, 1 factorization(s), peak |probe| 1, 7 ms -> results\emt-out
[exit 0]
```

The DC source holds bus 1 at 1 pu, so the peak is exactly 1. `--rule` accepts `trap` or `be`, in any letter case; anything else exits 2
(see [Refusals](#refusals-and-exit-codes)).

## Numerical chatter and CDA

A trapezoidal step carries the previous step's inductor and capacitor currents forward. When a switch changes a
conductance between two steps, the history no longer matches the new network, and the mismatch rings between the two
signs from one sample to the next, at half the sampling frequency. That ringing is numerical chatter: it is not in the
real circuit.

Critical Damping Adjustment (CDA) reduces it: in the worked example the first swing falls 9.3 times (below). At a step where an event changes a conductance, the engine
switches to backward Euler, takes two half-steps (each of `dt/2`) across the discontinuity, and then returns to
trapezoidal at the full step. The recorded sample at that time is the end of the second half-step. CDA is on by default
and acts only under the trapezoidal rule. `--no-cda` turns it off.

The effect on the worked example, at the fault, with CDA (the default) and with `--no-cda`:

```
$ gridsim emt case14 --duration 0.05 --dt-us 20 --freq 50 --probe v:5,i:1-5 --event fault:5@0.01:0.01 --event clear:5@0.03 --no-cda --out emt-nocda
emt: 2 floating node(s) pinned at 0 V in the initial-condition solve.
emt case14: 2,500 steps @ 20us, 3 factorization(s), peak |probe| 4.366, 18 ms -> emt-nocda
[exit 0]

$ sed -n 510,520p emt-nocda/timeseries.csv
0.01,-0.107233042,-0.680109603
0.01002,0.0771907976,-0.706801354
0.01004,-0.067548886,-0.734009938
0.01006,0.0395528143,-0.760645177
0.01008,-0.0458202103,-0.787542396
0.0101,0.0161538229,-0.81406493
0.01012,-0.0343911902,-0.84070243
0.01014,0.00129813165,-0.867083662
0.01016,-0.0287675828,-0.893496145
0.01018,-0.00834457229,-0.919723853
0.0102,-0.0263301481,-0.945935981
[exit 0]

$ sed -n 510,520p emt-fault/timeseries.csv
0.01,-0.0150150838,-0.692540163
0.01002,0.00481759349,-0.719492316
0.01004,-0.0139926538,-0.746416757
0.01006,-0.00313357157,-0.773186115
0.01008,-0.0148277463,-0.799899673
0.0101,-0.00911614127,-0.826483955
0.01012,-0.0165407435,-0.85299814
0.01014,-0.0137305444,-0.879400387
0.01016,-0.0185499403,-0.905726424
0.01018,-0.0173321358,-0.931952843
0.0102,-0.0205287672,-0.958101528
[exit 0]
```

Without CDA the bus voltage alternates +0.077, -0.068, +0.040, -0.046 pu: the first sample-to-sample swing is
0.184 pu, and the alternation is still visible ten steps later at the end of the window. With CDA the first swing is
0.020 pu, a factor of 9.3 smaller, and the trace stays within 0.021 pu of zero throughout the window. The branch current
`i:1-5` is smooth in both runs, because an inductor current cannot jump.

CDA does not remove all ringing. The values at 0.01002 s and 0.01004 s alternate in sign even with CDA, which is the
residue. Treat any alternation at the sampling Nyquist frequency straight after an event as numerical.

Use `--no-cda` only to see what CDA is hiding, or to compare. A run with no switching events has no discontinuity, so CDA
never acts and the factorisation count stays at 1.

CDA is not free. Its backward Euler half-steps are first order, so at the same step the peak of a sharp transient reads
lower with CDA than without. At 20 microseconds the worked example's `v:5` overshoot reads 2.943 pu with CDA
and 2.972 pu with `--no-cda`; at 10 microseconds 2.976 and 2.989; at 5 microseconds 2.990 and 2.996 (the 1 microsecond
reference is 2.999, see the table below, and the no-CDA runs are in `docs/_captures/emt/dt-sweep-nocda.txt`). The gap
closes as the step shrinks, which is the other way to reduce chatter: a smaller step moves the ringing to a higher
frequency and shrinks it.

## Choosing the time step

The step is fixed for the whole run. `--dt-us` sets it in microseconds, the default is 20 and it must be positive.

Three things set the step you need.

1. **Fastest phenomenon in the answer.** The trapezoidal rule shifts the frequency of a waveform by a relative error of
   `(omega dt)^2 / 12`, where `omega = 2 pi f`. With a 20 microsecond step, a 1 kHz component is off by 0.13 % and a 5 kHz
   component by 3.3 % (that formula is the leading term; the exact warp, `1 - atan(omega dt/2)/(omega dt/2)`, gives
   3.1 % at 5 kHz). A step of 1/50 of the period gives 0.13 %: at 5 kHz that is 4 microseconds.
2. **Sharpness of the event.** A peak reached in a fraction of a cycle needs samples that land on it. The table below
   is the bus 5 overshoot of the worked example, which peaks 0.16 ms after the clearing.
3. **Line travel time, with `--lines`.** A travelling-wave line has a delay `tau`, which the engine rounds to a whole
   number of steps (`max(1, round(tau/dt))`). The delay error is up to `dt/2` once `tau` is at least `dt/2`; a line with
   `tau` below `dt/2` is held at one step, an error of up to `dt - tau`. Choose `dt` at most one tenth of the
   shortest `tau` you care about. The charged lines of case30 have `tau` between 78 and 201 microseconds at 50 Hz, so
   `--dt-us 10` gives them 8 to 20 steps of delay.

| `--dt-us` | Steps | Peak `v:5` (pu) | Error against the 1 microsecond run | Wall time (ms) |
|---|---|---|---|---|
| 200 | 250 | 1.804 | 39.8 % | 5 |
| 100 | 500 | 2.513 | 16.2 % | 5 |
| 50 | 1,000 | 2.804 | 6.5 % | 10 |
| 20 | 2,500 | 2.943 | 1.9 % | 18 |
| 10 | 5,000 | 2.976 | 0.8 % | 43 |
| 5 | 10,000 | 2.990 | 0.3 % | 60 |
| 2 | 25,000 | 2.997 | 0.07 % | 122 |
| 1 | 50,000 | 2.999 | reference | 208 |

These are the `peak |probe|` values of eight runs of `gridsim emt case14 --duration 0.05 --freq 50 --event
fault:5@0.01:0.01 --event clear:5@0.03 --dt-us <N> --probe v:5`, kept in `docs/_captures/emt/dt-sweep-trap.txt`. The wall
times differ by machine and run. The peaks are reproducible on one machine and runtime (the source is AC).

Guidance in practice:

- 20 microseconds (the default) resolves power-frequency waveforms; in the table above it reads the sharp fault-clearing
  peak 1.9 % low. It is the step to start from, then halve it and re-run.
- 5 to 10 microseconds for the peak of a fault-clearing or capacitor-switching transient that you intend to quote, and for
  `--lines` studies on lines with `tau` below 100 microseconds.
- 50 to 200 microseconds only to preview the shape of a long run. A 200 microsecond step lost 40 % of the peak above.
- Halve the step and re-run until the quantity you quote stops changing in the digits you need. In the table above the
  error of the peak falls by a factor of 2.4 from 20 to 10 microseconds and 2.6 from 10 to 5 microseconds, so the check is
  worth making and one halving is not proof of convergence.

Wall time grows with the step count but less than in proportion: 2,500 steps took 18 ms and 50,000 steps took 208 ms
(11.6 times for 20 times the steps), with 5 ms at 250 steps (machine LAPTOP, 8 logical cores). The CSV grows with the
step count: 50,000 steps is 50,001 data rows.

`duration / dt` is rounded to a whole number of steps. A `--duration` that is not positive, or shorter than half of one
step (so the run would take no step), exits 2 naming the flag.

## Starting from rest

The run starts from rest, not from the power-flow solution. The initial state is: every inductor carries zero current,
every capacitor holds zero voltage, and the source is on at its `t = 0` value (the DC level, or `amp` for an AC source,
whose waveform is `amp * cos(2 pi f t)`). The engine solves the resistive network that this leaves (inductors open,
capacitors as 0 V, resistors conducting) to seed the histories, so that the source is a clean step at `t = 0` and the first
step does not see a spurious ramp.

What that means for the first milliseconds:

- **The start-up is an energisation transient.** The source steps from nothing to full voltage at `t = 0`. An AC source
  starts at its peak (a cosine), which is the worst case for the step. The network settles under the damping of the load
  conductances. This is the per-10-millisecond peak of `v:5` for a 100 ms AC run with no events:

```
$ gridsim emt case14 --duration 0.1 --freq 50 --probe v:5 --out emt-settle
emt: 2 floating node(s) pinned at 0 V in the initial-condition solve.
emt case14: 5,000 steps @ 20us, 1 factorization(s), peak |probe| 0.9098, 35 ms -> emt-settle
[exit 0]

$ awk -F, '!/^#/ && $1+0>0 {w=int($1/0.01); a=$2<0?-$2:$2; if(a>m[w])m[w]=a} END{for(i=0;i<10;i++)printf "%d-%d ms  %.4f\n", i*10,i*10+10,m[i]}' emt-settle/timeseries.csv
0-10 ms  0.8988
10-20 ms  0.9096
20-30 ms  0.9098
30-40 ms  0.9095
40-50 ms  0.9098
50-60 ms  0.9095
60-70 ms  0.9098
70-80 ms  0.9095
80-90 ms  0.9098
90-100 ms  0.9095
[exit 0]
```

  In the first 10 ms the first positive peak is 0.8911 pu at 0.84 ms (2.1 % below the steady 0.9098) and the largest
  value in the window is 0.8988 pu at 9.98 ms (1.2 % below); from 10 ms on every window peaks between 0.9095 and 0.9098 pu. A case with `--no-loads`, or a lightly damped one, takes longer. Schedule the first event after the start-up has settled,
  as the worked example does, and do not read the first cycle as steady state.
- **The `t = 0` row is a placeholder.** The probes record the state after each step, and the row at `t = 0` is written
  before any step: voltage probes read 0 whatever the source. The step from `t = 0` to `t = dt` is the first that is computed.
- **Floating buses start at 0 V.** A bus with no resistive path to ground, a source or a capacitor has no defined rest
  voltage: every element at it is an inductor, which is open at rest. A transformer-only bus is the common case. In case14 buses 7 and 8 are
  of this kind. The engine pins such buses at 0 V in the initial-condition solve, solves the rest of the network properly
  and prints the count as the `floating node(s)` line. The pin sets only their starting voltage and history; from the
  first step they follow the network like any other bus.
- **A DC source is a step input.** With `--freq 0` (the default), the network settles to the resistive DC solution:
  inductors become shorts and capacitors open. The DC run is the cleanest way to see a settling transient:

```
$ gridsim emt case9 --duration 0.01 --dt-us 20 --probe v:5
emt case9: 500 steps @ 20us, 1 factorization(s), peak |probe| 1.079, 3 ms -> results\emt-out
emt: 2 floating node(s) pinned at 0 V in the initial-condition solve.
[exit 0]
```

  Bus 5 of case9 peaks at 1.079 pu at 3.74 ms, above the 1 pu source at bus 1. This is the step response of the
  network's L and C elements. In a 0.2 s run the same probe reads 0.6100 pu at the last sample and is still falling slowly.

## Variations

**Source.** The source is one ideal voltage source (no internal impedance) at the first generator's bus. `--source <bus>` moves
it, `--amp` sets its amplitude in pu and `--freq` its frequency in Hz (0 is DC). A bus that is not in the case exits 2:
`emt: source bus 99 is not in the model. (Parameter 'options')`. Other generators are not modelled: their buses are
ordinary nodes.

**Loads.** By default each bus's demand `Pd`/`Qd` is a constant-impedance shunt at 1 pu: a conductance `Pd/BaseMva`
plus an inductor for `Qd > 0` or a capacitor for `Qd < 0`. They damp the transient to a physical settling.
`--no-loads` omits them, which leaves a lightly damped network that rings for longer (and, in case14, 8 floating nodes instead
of 2). A bus with negative demand (generation netted into the load) is modelled as a bounded current source of
`|Pd|/BaseMva` pu shaped like the source, since a negative conductance is not passive and would diverge.

**Travelling-wave lines.** `--lines` models every charged, untapped, non-transformer branch (`B > 0`, `X > 0`) as a
lossless Bergeron line with surge impedance `sqrt(X/B)` and travel time `sqrt(X B)/omega0`, in place of lumped pi
sections. The series resistance is dropped in this mode. Transformers, tapped branches and branches without charging stay
lumped. Use it for wave-propagation studies, and choose the step from the shortest `tau` (see
[Choosing the time step](#choosing-the-time-step)). The same case30 run, lumped and with lines:

```
$ gridsim emt case30 --duration 0.01 --dt-us 10 --freq 50 --probe v:2 --out emt-lumped
emt: 6 floating node(s) pinned at 0 V in the initial-condition solve.
emt case30: 1,000 steps @ 10us, 1 factorization(s), peak |probe| 1.061, 14 ms -> emt-lumped
[exit 0]

$ gridsim emt case30 --duration 0.01 --dt-us 10 --freq 50 --probe v:2 --lines --out emt-lines
emt: 8 floating node(s) pinned at 0 V in the initial-condition solve.
emt case30: 1,000 steps @ 10us, 1 factorization(s), peak |probe| 1.263, 11 ms -> emt-lines
[exit 0]
```

The line run reports a higher peak (1.263 pu against 1.061 pu). The lines are lossless and carry no series resistance, so
the two models differ in damping as well as in wave behaviour; compare them for the wave behaviour only. The two extra
floating nodes are buses that the lines join with no resistive path at rest.

**Other network elements the build models.** An off-nominal transformer tap is an ideal `a : 1` transformer on the
`from` side, with the `from`-end charging divided by `a^2`, as the power flow does. A branch with negative reactance
(series compensation) is a series capacitor, `C = 1/(|X| omega0)`. A branch with `X = 0` and `R` not zero is a pure
resistance. Bus shunts become a conductance and a capacitor or reactor.

## Refusals and exit codes

The verb fails loud: it does not run a case it cannot model faithfully and it does not report a failed run as a result.

```
$ gridsim emt case14 --rule euler
error: --rule expected trap or be but got 'euler'
[exit 2]
$ gridsim emt case14 --dt-us 0
error: --dt-us expected a positive step (microseconds) but got '0'
[exit 2]
$ gridsim emt nosuchcase
emt: error loading case 'nosuchcase': unknown case 'nosuchcase'
[exit 1]
$ gridsim emt case14 --source 99
emt: source bus 99 is not in the model. (Parameter 'options')
[exit 2]
$ gridsim emt cigre
emt: EMT build: branch #12 (0->1) is a phase-shifting transformer (shift 30 deg). A phase shift multiplies the voltage phasor by a complex number, which a real-valued single-phase time-domain network cannot represent; it would be silently dropped. Remove the shift or use a phasor study. (Parameter 'model')
[exit 2]
$ gridsim emt case14 --event clear:5@0.03
emt: bad --event 'clear:5@0.03' (use fault:<bus>@<t>:<Rpu>, clear:<bus>@<t>, or switch:<id>@<t>:close|open).
[exit 2]
$ gridsim emt case14 --event switch:7@0.01:open
emt: EMT case has events that cannot act: switch event at 0.01 s names element 7, which is not a switch. Valid switch ids: none.
[exit 2]
$ gridsim emt case14 --probe v:99
emt: unrecognised probe 'v:99' (use v:<bus> or i:<from>-<to>); skipped.
emt: no valid probes.
[exit 2]
$ gridsim emt case14 --event fault:5@0.01:0 --duration 0.02
emt: EMT element 1000000 (4->-1) has a non-finite companion conductance (Infinity) at dt = 9.999999999999999E-06 s: its resistance is zero or cancels its companion resistance.
[exit 3]
```

The build refuses, naming the branch, what a single-phase real-valued network cannot represent: a phase-shifting transformer
(the CIGRE MV benchmark and the PEGASE cases carry them), and a zero-impedance branch (`R = X = 0`). It also refuses an
in-service branch whose end bus is not in the case, instead of silently dropping the branch.

A network with a non-passive element, a branch with a negative series resistance or a bus shunt with a negative
conductance, generates energy and can grow without bound. Network-reduction equivalents carry them on purpose (`case145`
has 232, pglib case588 has 5). Such a case exits 2 naming the first ten elements, with `error: emt: pass
--allow-non-passive to run this network anyway.`; with `--allow-non-passive` it runs, and standard error carries a
warning with the count. An unknown case name exits 2; a case file that does not load exits 1.

| Exit | Meaning |
|---|---|
| 0 | The run completed and every probe value is finite. Floating-node notes do not change the exit code. |
| 1 | A case file or folder failed to load. |
| 2 | Usage error: an unknown case name, a bad flag value (including a negative `--freq`), a malformed, inconsistent or out-of-range event, a `--duration` that gives no step, a source bus that is not in the case, a probe that names nothing, a network the build refuses, or a non-passive network without `--allow-non-passive`. |
| 3 | Numerical failure: a singular nodal or initial-condition matrix, a non-finite companion conductance (a zero-resistance fault), or a non-finite value in any probe trajectory. The output files are not written. |

## Output location

`--out <dir>` names the output directory (default `results/emt-out`, relative to the working directory). The verb creates it if
needed and overwrites `timeseries.csv`, `summary.md` and `run-metadata.json` in it. Use a separate directory for each run
you want to keep.

## Pitfalls

- **`--freq` is not the case frequency.** `--freq` sets the source; the reactances come from the case's own frequency. A
  50 Hz case driven at `--freq 60` has the wrong impedance at 60 Hz. Set `--freq` to the case frequency (50 for the
  MATPOWER cases, 60 for `case9`) for an AC study, or leave it at 0 for DC.
- **Do not read the pre-event samples as steady state.** The run starts from rest, so the first cycle is an energisation
  transient. Schedule events after it has settled, as the worked example does at 10 ms.
- **Peak `|probe|` mixes probes.** It is the largest of all probe columns. Probe one quantity at a time when you quote a peak.
- **A coarse step under-reads peaks without warning.** A 200 microsecond step lost 40 % of an overshoot and the run
  reported nothing unusual. Run the study at a second, smaller step and compare.
- **Ringing straight after an event is numerical.** Alternating sign sample to sample at half the sampling
  frequency is chatter (see [Numerical chatter and CDA](#numerical-chatter-and-cda)). Do not quote it, and do not
  switch CDA off to make a trace look cleaner.
- **A fault resistance must be positive.** `Rpu` of 0 or below exits 2; use a small resistance such as `1e-4` for a bolted
  fault.
- **A factorisation count lower than you expect means an event did nothing.** Closing a closed switch, or opening an open
  one, is a no-op that adds no factorisation.
- **`--allow-non-passive` can diverge.** A reduction equivalent with negative resistances generates energy; always check
  the peak against what is plausible.
- **The CSV `run:` line is short.** It records the case, step, duration, source, amplitude, frequency and rule; the probes,
  events and switches are in the `parameters` object of `run-metadata.json`.
- **One output directory per run.** Re-using `--out` overwrites the earlier files without a warning.

## Limits

- The `emt` verb is single-phase (one node per bus, positive sequence). The three-phase engine (`ThreePhaseEmtEngine`),
  with inter-phase coupling and unbalanced faults, is a library API only: it needs per-phase sequence data that a case file
  does not carry, so there is no CLI verb for it. See the technical chapter.
- A phase-shifting transformer cannot be modelled and the build refuses it, so the CIGRE MV benchmark and the PEGASE cases
  do not run.
- The CLI energises the network with one ideal source (no source impedance, no phase offset: the waveform is
  `amp * cos(2 pi f t)`). It cannot place several sources, give the source an internal impedance or start it at another phase.
- Generators, converters, controls and protection have no model in the `emt` verb; only the passive network, the source, loads
  as constant impedances and the switches you schedule act.
- Faults from the CLI are bus-to-ground only, through one resistance. There is no line-to-line or mid-line fault, and no
  wait for a current zero at clearing.
- Under `--lines` the lines are lossless: series resistance is dropped.
- A run starts from rest, not from a power-flow operating point, so a study that needs the network loaded to a power-flow
  state before the event cannot be started from the CLI.
- The `t = 0` row of voltage probes is zero by construction, not the initial state.

## See also

- [Technical manual, ch. 21: Electromagnetic transient (EMT) simulation](<../technical/0021 Electromagnetic transient (EMT) simulation.md>) - companion models, the engine, CDA, the Bergeron line, the three-phase engine and the validation.
- [19. CLI reference, `emt`](<0019 CLI reference.md#emt>) - every flag and its default.
- [5. Running power flows](<0005 Running power flows.md>) - the steady-state solve that EMT results are compared with.
- [14. Frequency studies](<0014 Frequency studies.md>) - the phasor-timescale transient (seconds), which EMT complements.
- [17. Running a harmonic study](<0017 Harmonic studies.md>) - the frequency-domain counterpart.
