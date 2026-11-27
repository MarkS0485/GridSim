### stability

`gridsim stability [<case>] [--dir <path>] [--rms] [--avr none|sexs|ieeet1] [--pss] [--dyn-data <file>] [--screen] [--fault-bus N] [--clear ms] [--clear-by-trip K[,K...]] [--events <list> [--events-out <dir> [--events-every N]]] [--export <json>]`

Classical multi-machine transient stability (constant E' behind X'd, Kron-reduced network, RK4 swing). Default case
`case9` (Anderson-Fouad X'd; any other case uses a synthetic 0.30 pu default and is flagged INDICATIVE). `<case>` is a
[case argument](#case-arguments); `--dir` loads a JSON case directory instead. A positional case and `--dir` together
exit 2 unless they name the same folder, and a second positional case exits 2.

| Flag | Default | Meaning |
|---|---|---|
| `--rms` | off | Use the salient/subtransient **RMS engine** (six-state GENROU machines) instead of the classical swing. GENROU parameters are synthesised from X'd, so the result is INDICATIVE; the engine itself is validated (it reduces to the classical CCT). Works with `--screen`, `--fault-bus` and `--clear`; with `--clear` it also reports the converter fleet's fault ride-through. |
| `--avr` | `none` | With `--rms`: `none` (field held), `sexs` or `ieeet1` exciters on every synchronous machine (typical, indicative settings); any other value exits 2. |
| `--pss` | off | With `--rms` and an AVR: add a PSS1A stabiliser to each exciter. Without an AVR it exits 2. |
| `--dyn-data` | - | A machine dynamic data file (`gridsim-dynamics/2`, see `schemas/schemaNotes.md`), or a PSS/E `.dyr` file when the case is a PSS/E `.raw` file (its records are mapped onto the case's machines, then checked and bound the same way; [ch. 2](<0002 Loading networks.md#dynamic-data-dyr>); `--allow-unmodelled` leaves out what GridSim cannot represent; every `--dyn-data` verb takes a `.dyr` file this way). Classical engine (no `--rms`, single bus and `--screen`; N314, October 2026, it used to exit 2 there): the classical swing is built from X'd, H and the damping D, and each machine the file binds takes all three from it, exactly as [`modal`](#modal) does (X'd on the machine base; D per unit on the system base, the same quantity as the RMS study's; H the case's own where the template gives none); the other machines keep the X'd map (Anderson-Fouad on case9, else the synthetic 0.30 pu) and D = 0. The header says how many X'd values came from the file and adds a `machine data:` line with the loader's notes; the result stays INDICATIVE unless no X'd is synthetic and every value is tagged `Recorded` or `DerivedFromRecorded`. A file whose values equal the built-in ones gives the same clearing times. `--export` stays the undamped kernel check (the export records the file's H and X'd). With `--rms` (F23): Each machine it binds takes the file's GENROU reactances and time constants, inertia (the case's own H when the template gives none, said in a `note:` line), damping, saturation and leakage, and the exciter (SEXS, IEEET1, EXAC1, ESAC1A or ESST4B, with a PSS1A), and governor (TGOV1, IEEEG1, HYGOV or GAST) its template names; the other machines keep the synthesised data. A file exciter replaces `--avr` on its machine; `--avr` and `--pss` fill the rest. ([`modal`](#modal), [`trip`](#trip) and [`transient`](#transient) take the same file for the part of it their models use.) The header says how many machines the file covers, which governors it brought and the provenance tags of its values. The INDICATIVE footer goes only when the file covers every machine and every value is tagged `Recorded` or `DerivedFromRecorded`. On either engine a missing file or one that fails the v2 schema, kind-spec or binding checks exits 1 with every problem listed. |
| `--screen` | off | Critical clearing time at every generator bus, worst first. It has no single fault bus and no fixed clearing time, so with `--fault-bus` or `--clear` it exits 2 before any work, naming the flag (N266, October 2026; both used to be accepted and silently ignored); with `--export` it exits 2. The header counts the integrated machines and the units held as constant injections (converter, power-flow-only and zero-inertia units), as the single-bus run does. |
| `--fault-bus` | 7 (case9) / first non-generator bus | Fault location. |
| `--clear` | - | Fixed clearing time in ms, giving a STABLE / UNSTABLE verdict; omit for the critical clearing time. Classical and `--rms`. Must be a finite number, 0 or more (0 is an instantaneous clear); a negative value, `NaN` or `Infinity` exits 2 (a negative value used to run a study with no fault in it and report STABLE). At most 1000 ms (N266, October 2026): above it exits 2. Why 1000 ms: the slowest clearance GB plant coordinates with is back-up protection within 800 ms in England and Wales (300 ms in Scotland), Grid Code CC.6.2.2.2.2; 1000 ms is the window both CCT searches use, so a fixed clearing time stays inside the range the CCT answers; and the RMS study runs a fixed 3 s, so a much later clear would end the run with the fault still on. Without the bound a large value also grew the run's time and memory without limit. Both engines (N246, October 2026; the `--rms` engine since CF2): under the verdict a `law 9 (synchronism, scored from this run): HOLDS` (or `VIOLATED`) line scores the physics law from the run's own rotor-angle trajectory with the out-of-step criterion of PSS/E and PowerFactory, any machine more than 180 degrees from the centre of inertia at any sample: `largest rotor angle from the centre of inertia X deg (limit 180 deg, the out-of-step criterion); peak speed deviation Y Hz, reported, not scored (no frequency band in a fault study)`. The verdict itself is unchanged (the integrator's pole-slip test, 360 degrees from the centre of inertia); when a machine passes 180 but not 360 degrees a `note:` line says the two criteria differ. On `--rms` the centre of inertia and the maximum cover the machines the RMS verdict judges (island reference machines are left out of both, as the verdict leaves them out). The steady-state law 9 row of `physics-check` names this verb as the one that scores it. |
| `--clear-by-trip` | - | N351, `--rms` only: the fault at `--fault-bus` is cleared by also tripping these branches (comma list of 0-based indices into the case's branch list), as a line fault is cleared by its breakers. Without `--clear` it is the critical clearing time search with those trips at every trial's clearing instant (the same step grid, window and first-loss rule as below); with `--clear` a STABLE / UNSTABLE verdict for that clearing time. Not with `--screen` or `--events` (exit 2). |
| `--events` | - | N351, `--rms` only: one scheduled RMS event study. A comma list of `kind:arguments@seconds` items: `fault:BUS@t` (bolted fault), `clear:BUS@t` (its clearance), `trip:K@t` (branch K trips, 0-based), `reclose:K@t` (it closes again: auto-reclose), `load:BUS:P:Q@t` (a load step of P MW and Q Mvar, negative to shed), `gentrip:G@t` (generator G trips, 0-based), `sfault:TYPE:BUS@t` or `sfault:TYPE:BUS:R:X@t` (a `3ph`, `slg`, `ll` or `llg` fault at the bus, bolted or through R + jX pu; cleared by `clear:BUS@t`), `lfault:TYPE:K:D@t` or `lfault:TYPE:K:D:R:X@t` (the same fault a fraction D along line K) and `lclear:K@t` (its clearance). For example `--events "fault:7@0.1,clear:7@0.183,trip:5@0.183,reclose:5@0.683"`. With a sequence fault the run also prints the fault point (sequence and phase currents, phase voltages, Z2, Z0) and `--events-out` adds `rms-fault-points.csv`. Prints the events and a STABLE / UNSTABLE verdict over the connected machines. The run lasts 3 s, or 3 s past the last event. It owns the whole disturbance: with `--screen`, `--fault-bus`, `--clear` or `--clear-by-trip` it exits 2; a malformed list exits 2 naming the item; an event the study refuses (a bus not in the case, a reclose of a branch that is not tripped, a time after the end) exits 2 with the reason. |
| `--events-out` | - | N363, with `--rms --events` only (otherwise exit 2): writes the event study's export to the folder (created if missing): `rms-events-machines.csv` (every machine trace at every integration step: `t_s,trace,bus,delta_rad,speed_pu,eqp_pu,edp_pu,eqpp_pu,eddp_pu`, each number in its shortest round-trip form), `rms-events-converters.csv` when the fleet has converter plant (`t_s,converter,name,bus,v_pu,p_pu,q_pu,ip_pu,iq_pu,in_dip_mode`), `rms-devices.csv` when the run has SVC, STATCOM or HVDC models (`t_s,device,name,model,state,value`), `rms-events.json` (schema `gridsim-rms-events-export/1`: case, nominal frequency, step, duration, the events in time order, the verdict, converter-iteration failures, the machine list with island references marked, provenance `Synthetic` and whether the result is INDICATIVE) and `run-metadata.json`. Both CSVs start with the run header and a `# provenance:` line (`Synthetic`, a model trajectory, not a record; INDICATIVE when the GENROU data is synthesised). A file that cannot be written exits 1. The run and its verdict are unchanged. |
| `--events-every` | 1 | With `--events-out` only (otherwise exit 2): write every N-th integration step to the export CSVs (steps 0, N, 2N, ...; a whole number from 1 to 1000000, anything else exits 2). The default 1 writes every step, so an export without the flag is the full-resolution one. `rms-events.json` records `everySteps` and `samplesWritten`; the run itself is unchanged. |
| `--export` | - | Write a `gridsim-classical-export/1` trajectory for the scipy cross-check (`tools/crossval/crossval_classical.py`). Classical single-bus run only: with `--rms` or `--screen` it exits 2 before any work (`error: stability: --export writes the classical single-bus trajectory and is not available with --rms (no file would be written).`); it used to be accepted with no file written. A missing folder is created ([Output files](#output-files)). |

With `--rms` the critical clearing time is step-quantised: the fault is switched only on whole 2 ms integration steps,
so the reported CCT is the longest clearing time on that step grid that keeps synchronism, and clearing one step (2 ms)
later loses it. The output says so, and a single-bus run prints the two ends of the bracket. Digits below the step
carry no information and are not reported.

The classical critical clearing time (no `--rms`) is step-quantised in the same way: the longest clearing time on the
2 ms step grid that keeps synchronism, with one step (2 ms) more losing it. Both the single-bus run and `--screen` say
so, and a single-bus run prints the bracket (for example "clearing at 550 ms holds synchronism, at 552 ms it is lost"
for case9 bus 7). 0 ms means even an instantaneous clear loses synchronism; 1000 ms means nothing in the 1000 ms
search window loses it.

Both CCTs (classical and `--rms`) are the first loss of synchronism from below. Stability is not always monotone in
the clearing time: after the bisection, the 16 step counts (32 ms) below the reported CCT are checked to hold and as
many above the first loss are probed. A later clearing time that holds again (a stable pocket) is flagged: `--screen`
adds `NON-MONOTONE: holds again at N ms` to that bus's row, and a single-bus run prints a warning line. On case9
(classical) bus 2 reports 312 ms with a pocket at 320 ms, and bus 1 reports 498 ms with a pocket at 502 ms:

```
$ gridsim stability case9 --screen
GridSim  -  TRANSIENT STABILITY SCREEN (classical multi-machine)  *  WSCC-9
==================================================================
3 machines (0 converter / power-flow-only / zero-inertia units held)  *  3 generator-bus faults  *  X'd = Anderson-Fouad

 Rank  Fault bus   CCT (ms)
    1          3        302   <- most critical
    2          2        312   NON-MONOTONE: holds again at 320 ms
    3          1        498   NON-MONOTONE: holds again at 502 ms

Self-clearing 3-phase fault at each generator bus; lower critical clearing time = less margin.
CCT = longest clearing time on the 2 ms integration-step grid that keeps synchronism (clearing one step, 2 ms, later loses it); resolution 2 ms.
A CCT of 0 ms means even an instantaneous clear loses synchronism; 1000 ms means no clearing time in the 1000 ms search window loses it.
It is the first loss from below: the 16 steps (32 ms) below it are checked to hold and as many above it are probed; NON-MONOTONE marks a later clearing time that holds again (a stable pocket).
Each trial is judged over the 5 s after the fault clears (the documented default window: first-swing stability, the range in which the classical constant-E' model holds); an undamped system can lose synchronism on a later swing, so a longer window can give a shorter CCT.
[exit 0]
```

The checks cost up to 32 extra trial runs per bus (3.5 times the bisection alone on case9). With `--rms` the verb runs
one search per bus as well; its stable end is the CCT and the same search gives the flag.

The criterion and the window (N341 / N343, October 2026). The CCT is the first loss: the largest clearing time below
which every trial holds synchronism, which is what PSS/E and PowerFactory report; a stable pocket above it is printed as
a note (`NON-MONOTONE`), never as the CCT. Each trial (classical and `--rms`) is judged over a fixed window after the
fault clears, 5 s by default (`ClassicalInit.CctPostClearWindowSeconds`, `RmsTransientStability.PostClearWindowSeconds`):
first-swing stability, the range in which the classical constant-E' model is valid. Every CCT explanation ends with a line
saying so: `Each trial is judged over the 5 s after the fault clears (the documented default window: first-swing
stability, ...); an undamped system can lose synchronism on a later swing, so a longer window can give a shorter CCT.`
The undamped classical swing keeps every swing it starts, so the number depends on the window: WSCC-9 bus 7 at 60 Hz is
550 ms with the 5 s window and 436 ms with a 20 s window (the Core API takes the window as an optional argument; the CLI
uses the default). The stable pockets case9 shows at 60 Hz (buses 1, 2, 5, 7 and 9) are the same multi-swing effect.

Exit 3 on a non-converged base solve; exit 2 on a zero-inertia case, an unknown case name, a `--fault-bus` that is
not in the case (`error: stability: fault bus <id> is not in case '<name>'.`, checked before any solve), a negative or
non-finite `--clear` or one above 1000 ms, `--export` with `--rms` or `--screen`, `--fault-bus` or `--clear` with `--screen`, a positional case that
disagrees with `--dir`, or an RMS study error (including a governor or exciter whose limits cannot hold the solved
operating point); exit 1 for a case file or `--dir` folder that is missing or cannot be loaded, and for a `--dyn-data`
file that is missing or invalid. A STABLE or UNSTABLE verdict does not change the exit code.
