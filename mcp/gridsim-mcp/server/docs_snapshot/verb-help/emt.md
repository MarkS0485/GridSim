### emt

`gridsim emt [<case>] [--dir <path>] [--dt-us 20] [--duration 0.1] [--source <bus>] [--amp 1.0] [--freq 0] [--probe <list>] [--event <spec> ...] [--rule trap|be] [--no-cda] [--lines] [--no-loads] [--allow-non-passive] [--out <dir>]`

Electromagnetic-transient time-domain solve: a Dommel nodal companion-model engine steps the instantaneous network at a
fixed microsecond timestep. The output is a forward model trajectory. Writes `timeseries.csv`, `summary.md` and
`run-metadata.json`. See [ch. 16, Electromagnetic transients](<0016 Electromagnetic transients (EMT).md>).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `data/uk` | [Case argument](#case-arguments) or JSON case directory. |
| `--dt-us` | 20 | Timestep (microseconds); must be positive (exit 2). |
| `--duration` | 0.1 | Simulated time (s). Must be positive and at least half of one `--dt-us` step (the run takes `round(duration / dt)` steps, at least one); anything else exits 2. |
| `--source` | first generator's bus | Source bus. |
| `--amp` | 1.0 | Source amplitude (pu). |
| `--freq` | 0 | Source frequency (Hz); 0 is DC. A negative value exits 2. |
| `--probe` | source-bus voltage + first series branch current | Comma list of `v:<bus>` and `i:<from>-<to>` (or `i:<from>-><to>`, in the case's from -> to direction). Every token must name a bus or an in-service series branch of the case; a token that does not (an unknown bus, a non-numeric bus, no such branch, or neither `v:` nor `i:`) exits 2 and nothing runs. Where several in-service series branches join the same two buses (parallel circuits, such as 49-54 in case118), `i:<from>-<to>` is ambiguous and exits 2 naming how many there are; `i:<from>-<to>#<k>` picks the k-th of them in case order (N241; it used to read the first without a word). The default branch probe is labelled `#1` when its branch has a parallel twin. |
| `--event` | - | Repeatable: `fault:<bus>@<t>:<Rpu>`, `clear:<bus>@<t>`, `switch:<id>@<t>:close\|open`. See the event rules below. |
| `--rule` | `trap` | `trap` (trapezoidal) or `be` (backward Euler); any other value exits 2. |
| `--no-cda` | off | Disable Critical Damping Adjustment (backward-Euler half-steps at switching events that remove the trapezoidal rule's spurious oscillation). CDA runs only under `--rule trap`; under `--rule be` it is off whatever this flag says. |
| `--lines` | off | Model charged, non-transformer branches as lossless Bergeron travelling-wave lines. |
| `--no-loads` | off | Leave out the constant-impedance load shunts (by default each bus's demand is one). |
| `--allow-non-passive` | off | Run a network that contains a non-passive element: a branch with a negative series resistance or a bus shunt with a negative conductance. Network-reduction equivalents carry these on purpose (IEEE 145 has 232, pglib case588 has 5). They generate energy, so the run may grow without bound. Without the flag such a case exits 2, naming the first ten elements; with it, stderr gives a warning with the count. |
| `--out` | `results/emt-out` | Output directory. |

Event rules. Each event time must be a number of seconds from 0 up to the end of the run. An event between 0 and one
step is applied at the first step; a negative time, or a time after the last step, exits 2. A
fault resistance must be a positive number of per-unit ohms: 0 and negative values exit 2 (a bolted fault is a small
resistance, for example `1e-4`). Each fault bus and resistance pair gets its own fault switch. A later `fault:` at a bus
that is already faulted changes that bus's fault to the new resistance: the bus's other fault switches open at that
instant and the new one closes. `clear:<bus>` opens every fault switch at the bus and needs a `fault:` at that bus
somewhere on the command line. Two `fault:` events at one bus at the same instant exit 2. The `switch:` action must be
`close` or `open` (any letter case); any other word exits 2. Every problem on the command line is listed before the
command exits.

`summary.md` reports `CDA: on`, `CDA: off (--no-cda)` or `CDA: off (not used under backward Euler)`. Its
`refactorizations` count is 1, plus 2 for every event that changes a switch when CDA is on, or plus 1 when CDA is off.
`run-metadata.json` holds the shared run fields and a `parameters` object. That object records the case, `dt`, the
duration and step count, the source, the rule, whether CDA ran, the `--no-cda`, `--lines`, `--no-loads` and
`--allow-non-passive` switches, every probe (name, kind and target), and every event. For each event it keeps the token
as given and what it became: the bus, the time, the resistance and the fault switch id.

Exit 1 on a case file or folder that cannot be loaded; exit 2 on an unknown case name, a network that cannot be built
(including a non-passive one without `--allow-non-passive`), a malformed or out-of-range `--event`, a probe that names
nothing, or a `--duration` that gives no step; exit 3 on an engine failure or a non-finite value in the trajectory.
