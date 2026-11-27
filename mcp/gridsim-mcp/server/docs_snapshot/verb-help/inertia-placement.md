### inertia-placement

`gridsim inertia-placement [<case>] [--placements fleet,demand,spine-t,south,optimised] [--budgets 120,180,240,360,480,700,900,1200] [--losses sizewell,hinkley,kent,scotland] [--residual 0.25] [--condenser-h 8] [--rocof-limits 0.125,0.25,0.5,1] [--post-event 6] [--avr none|sexs|ieeet1] [--optimise-budget 240] [--optimise-passes 1]`

The inertia placement study: with a network's synchronous inertia in better places, can it run on less of it? Default
case `gb-full`. Today's synchronous fleet keeps `--residual` of its inertia (each machine's H scaled; dispatch and
locations unchanged); the rest of each budget is synchronous condensers of inertia constant `--condenser-h`, at no
power and no reactive power at the solved state (so the power flow is the case's), placed by each placement:

- `fleet`: where today's synchronous inertia is, in proportion to it;
- `demand`: in proportion to demand, each bus's demand gathered to its nearest 275/400 kV bus;
- `spine-t`: equal shares at eleven sites forming an inverted T / ladder (Inverness, the central belt, the Borders, the
  east and west England corridors, the Midlands, Cornwall, the South West, the Thames, Kent and East Anglia), each on its
  nearest 275/400 kV bus;
- `south`: everything at the Thames site, a deliberately concentrated contrast;
- `optimised`: the same sites with weights from a coordinate search at `--optimise-budget` (each pass tries each site's
  weight doubled and halved and keeps the change that lowers the worst bus RoCoF over every loss most; up to
  `--optimise-passes` passes, stopping early when a pass changes nothing). A local search, not a proven optimum.

Each loss is a 1.8 GW step (the SQSS infeed loss; constant admittance at the bus's solved voltage, the RMS engine's load
step) at the 275/400 kV bus nearest Sizewell, Hinkley Point, Sellindge (the Kent interconnectors) or Torness (Scotland
over B6), at t = 1 s, run on the RMS engine (`stability --rms`'s synthetic GENROU machines with `--avr` exciters on every
machine and condenser, mechanical power held: no frequency response) for `--post-event` seconds after it on a 5 ms step. Every bus at 132 kV and above is
measured: its frequency from its voltage angle over one nominal cycle, its RoCoF over a 500 ms window (the GB
loss-of-mains window) with the voltage angle jump at the loss taken out and reported on its own.

Prints, for each run, the centre-of-inertia RoCoF and the worst bus RoCoF of the RMS run and K, their ratio (the
spatial penalty); the loss the machines felt (`2 E RoCoF / f0`: not exactly 1.8 GW, because the RMS loads are constant
admittances and the converter plant holds its current, so both change their power as the voltage moves); the full-loss
worst bus RoCoF (K times the swing-equation figure `f0 x 1.8 GW / 2E`); the worst angle jump; the north-south
inter-area oscillation (damping ratio and frequency, fitted over every half-cycle of the difference of the north and
south machines' mean speeds, smoothed over 0.4 s to average out local swings; `NaN` with fewer than four half-cycles);
and the time the RMS run's centre-of-inertia frequency took to fall to
49.8 Hz. Then, per placement, the smallest scanned budget meeting criterion A (the full-loss worst bus RoCoF at or below
each of `--rocof-limits`) and criterion B (at least 3 s and 5 s to 49.8 Hz for the full loss with no response, from the
swing equation, so the same for every placement), with the linear estimate between the budget that fails and the one
that passes; and the time to 49.8 Hz with the case's frequency response from the lumped model (the default GB response,
which placement does not change). Each run is a full RMS run of the network (about 10 s each on `gb-full`), so the
default study takes a long time; it prints one line per run as it goes.

The results of the study on `gb-full`, with its method, sensitivity runs and limits, are in
[`docs/validation/studies/inertia-placement.md`](<../validation/studies/inertia-placement.md>).

| Flag | Default | Meaning |
|---|---|---|
| `--placements` | all five | A comma list of `fleet`, `demand`, `spine-t`, `south`, `optimised`, each at most once. |
| `--budgets` | 120,180,240,360,480,700,900,1200 | The total synchronous inertia budgets (GVA.s), ascending; each must exceed the residual. |
| `--losses` | all four | A comma list of `sizewell`, `hinkley`, `kent`, `scotland`, each at most once. |
| `--residual` | 0.25 | The share of today's fleet inertia kept on the fleet, above 0 and at most 1. |
| `--condenser-h` | 8 | The added condensers' inertia constant (s, on their own rating), above 0. |
| `--rocof-limits` | 0.125,0.25,0.5,1 | Criterion A's limits on the worst bus RoCoF (Hz/s), each above 0; one minimum per limit. |
| `--post-event` | 6 | Simulated seconds after the loss, 1 to 60. |
| `--avr` | `ieeet1` | The exciter on every synchronous machine and condenser: `ieeet1`, `sexs` or `none` (field held); the typical, indicative settings of `stability --rms --avr`. |
| `--optimise-budget` | 240 | The budget (GVA.s) the `optimised` placement's weights are searched at. |
| `--optimise-passes` | 1 | Passes of the coordinate search, at least 1. |

Exit 0 when the study ran; exit 1 for a case it cannot study (no 275/400 kV bus with a map position, a budget at or below
the residual, a power flow that does not converge; the reason is printed); exit 2 for a malformed flag.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary and the tables `runs` (one row per placement, budget and loss),
`minimums`, `lumped`, `sites` and `placements` (every bus share).
