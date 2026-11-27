GridSim manual ch. 30 Troubleshooting: index of entries (each heading is the message or symptom).
Read an entry with break_it, or in the manual. Sections about services this server does not offer are left out.

## Exit codes
- (section text only)

## Loading networks
- `error: unknown command or case '<name>'.`
- `error: MATPOWER case file not found: '<name>.m' (full path ...).`
- `error: case folder not found: '<folder>' (full path ...).`
- A positional case and `--dir` disagree, or one is ignored
- `error: This network has N buses; your Personal licence allows 1,500. ...`

## Power flow does not converge
- `DID NOT CONVERGE` and exit 3
- A meshed case diverges under `--bfs`
- A `pglib_opf_case*` does not converge
- `Dense engine reports OOM on a large case`
- `<study>: the power-flow state it starts from did not converge (status ..., max mismatch ... p.u. after N iterations)`
- `Taps NOT SETTLED: ...` under a converged report
- A Q-limit study reports `MaxIterations` although the last Newton pass converged
- A case that converged before no longer converges

## Contingency
- `ISLANDED`, `NO SOLUTION` and `solver error:` rows
- `base only` is not violation-free
- `trip found no eligible generator to trip in <case>`
- `error: stress --from (2) is above --to (1); the load range is empty ...`

## Dynamics and stability
- `stability: No synchronous machines with inertia: the classical stability model has no rotor to integrate.`
- `error: stability: fault bus N is not in case '<name>'.`
- `error: --avr and --pss apply to the RMS engine only (add --rms).`
- `impedance-stability` exits 0 on an `UNSTABLE` verdict
- `REJECTED (forward inference)` from `freq`, `replay` or an event verb
- `error: frequency file '<file>' holds no data rows ...` or `... line N is not a 'timestamp,Hz' data row ...`
- `--trip-at` is refused or ignored
- An event verb exits 3 with a complete report

## Electromagnetic transients
- `emt: EMT build: branch #N (a->b) is a phase-shifting transformer ...`
- `emt: EMT element ... has a non-finite companion conductance (Infinity) ...`
- `error: emt: --duration expected a positive number of seconds but got ...`
- Other `emt:` messages

## Harmonics
- `error: harmonics: Harmonic source at bus N (...) injects order 3, a triplen (zero sequence). ...`
- `error: harmonics: N source component(s) are at orders this study does not analyse ...`
- `error: harmonics: --bus N is not a bus of <case>.`
- `harmonics: FAILED - Harmonic penetration: the harmonic network admittance Y(h) is singular at order 5 ...`
- Other harmonics refusals

## How to report a problem
- (section text only)

## Limits
- (section text only)

## See also
- (section text only)
