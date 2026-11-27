### modal

`gridsim modal [<case>] [--dir <path>] [--damping D] [--enable-inverters] [--include-soc] [--json] [--export <json>] [--dyn-data <file>]`

Small-signal (modal) stability of the linearised multi-machine swing: eigenvalues, frequency and damping ratio of every
mode, the least-damped oscillatory mode, right-half-plane count, the dominant participating states per mode and an
ASCII s-plane. Default case `case9`; the X'd map is the same as `stability` (Anderson-Fouad for case9, else a
synthetic 0.30 pu, flagged INDICATIVE).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case9` | [Case argument](#case-arguments) or JSON case directory; both together exit 2 unless they name the same folder. |
| `--damping` | 0 (undamped) | Machine damping D applied to every machine (MW per rad/s). |
| `--enable-inverters` | off | Add the inverter states of the case's bound converter classes (a documented archetype). A grid-following class's frequency estimator takes the class's `pllBandwidthHz` / `pllDampingRatio` when authored; otherwise its detection latency as a critically damped delay stand-in (every bundled class); otherwise the one PLL default, 20 Hz at 0.707 (R138, October 2026; it was 5 Hz, critically damped). |
| `--include-soc` | - | Not implemented: exits 2 (a state-of-charge integrator is a decoupled origin pole, left out of the stability matrix by design). |
| `--json` | off | Print the model and modes as JSON on the console instead of the tables. Takes no value: unlike the shared writer's `--json <file.json>` ([Result files](#result-files)), it writes no file; `--export <file.json>` does. `--json <name>.json` exits 2 and points at `--export`. |
| `--export` | - | Write the small-signal model and modes to a JSON file (`gridsim-smallsignal/1`). The file also carries `participationAvailable` (false when the state matrix is defective: the `participation` rows are then all-zero placeholders, not participation), `participationUnavailableReason` (only when false) and `exitCode` (the exit code the run ends with: 1 for a right-half-plane eigenvalue, else 0). `--json` prints the same fields. |
| `--dyn-data` | - | A machine dynamic data file (`gridsim-dynamics/2`, the file [`stability --rms`](#stability) reads). The classical small-signal model is built from three numbers per machine, X'd, H and the damping D, and each machine the file binds takes all three from it (X'd on the machine base, as the built-in map; D per unit on the system base, the same quantity as the RMS study's; H the case's own where the template gives none); `--damping` and the X'd map fill the other machines. The header says how many X'd values came from the file and adds a `machine data:` line (the file's subtransient data, exciters and governors are not part of this model) with the loader's notes; the result stays INDICATIVE unless every machine is bound and every value is tagged `Recorded` or `DerivedFromRecorded`. A file can give a MATPOWER case the inertia it lacks. A missing or invalid file exits 1, before any solve. |

Exit 1 if any eigenvalue lies in the right half-plane (small-signal unstable), for a case file or `--dir` folder
that is missing or cannot be loaded, or for a `--dyn-data` file that is missing or invalid; exit 3 on a non-converged base solve; exit 2 on an unknown case name, a positional
case that disagrees with `--dir`, or a model that cannot be assembled. An unstable result is still a
complete analysis, so the export is written and says `exitCode: 1`. When the state matrix is defective (for example
`data/gb-synthetic --enable-inverters`) the eigenvalues are reported but participation factors are undefined: stderr
says why, the participation table prints `unavailable`, and the export says `participationAvailable: false`.

A MATPOWER case (the GB ones `gb-reduced-29`, `gb-network-2224`, `pypsa-gb-reduced`, and the IEEE cases other than
`case9` / `ieee9`) exits 2: a `.m` file carries no inertia data, so every unit loads with H = 0 and the swing model has
no rotor. The parser's "nominal frequency not given ... assumed 50 Hz" note printed above it is a separate assumption
(right for GB) and not the cause; the error message says so. Use a JSON case with `inertiaH` per machine (`gb`,
`gb-reduced`, `gb-synthetic`, `uk`, `gb-full`). `gb-full` (952 machines, 1,902 reduced states) is far beyond the dense
eigenvector path's size: participation needs one complex dense LU per eigenvalue (1,902 of them), so a participation run
takes hours.
