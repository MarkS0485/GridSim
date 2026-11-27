### ansi-sc

`gridsim ansi-sc <case|case.m|case-dir> (--bus <id> | --all-buses) --ansi-data <file.json> [--parting-cycles <n>] [--prefault 1.0] [--breaker kV,closing_latch_kA,interrupting_kA]`

An ANSI/IEEE C37.010-style breaker duty by the E/X method (RC4 R4-20a; `Protection/ShortCircuit/Ansi`). It is a new calculation
beside `iec60909` (which it only calls to find the faulted island) and reuses the fault network assembly. **Verification status:
implemented from public documentation, equations only; not verified against the standard.** The IEEE C37 texts are paid and were
not read; every run prints the list of items marked AWAITING STANDARD TEXT.

What is computed. Two reactance networks are built from the case: the first-cycle (momentary) network and the interrupting
(contact-parting) network, in each of which every source's X''d is multiplied by that source's multiplier (a unit transformer
reactance is not multiplied); loads, line charging and branch resistance are left out. A separate R network (every reactance zero,
branch R kept, each machine R = X''d / (X/R)) gives the fault-point resistance, so the fault X/R is X(network) / R(network) (a branch
with no resistance is held at 1e-9 pu there, which only matters as a numerical floor). Symmetrical currents are E/X with E the
prefault voltage. The first-cycle (close and latch) current is `MF x E/X`, with MF = 1.6 for a first-cycle X/R up to 25 (a published
constant, see below) and above that the half-cycle rms expression `sqrt(1 + 2 e^(-2 pi/(X/R)))`. The crest value is
`sqrt(2) (1 + e^(-pi/(X/R))) E/X`. The interrupting duty is `MF x E/X` of the interrupting network with the remote-source factor
`sqrt(1 + 2 e^(-4 pi n/(X/R)))` at `n` = `--parting-cycles` (DC offset physics: the DC part starts at the AC peak and decays with the
fault X/R). NACD is the share of the interrupting-network current fed by sources marked `local`. The current of each source is the
E/X current divider of each network.

Out of scope, said in the output rather than guessed: when NACD is above 0 (any local generation feeding the fault) the local
multiplying factors of the standard are AWAITING, so no interrupting duty is given; the remote-factor value is printed as a
reference only and is not a duty. The same holds when `--parting-cycles` is not given or a source has `interruptingMultiplier: null`.
The momentary duty is still given. Converter-interfaced plant is neglected and listed in the notes.

`--ansi-data` reads `gridsim-ansi-data/1` and nothing is defaulted: `{"schema": "gridsim-ansi-data/1", "sources": [{"bus": 1,
"name": "G1", "class": "turbine-generator", "xOverR": 40, "local": true, "firstCycleMultiplier": 1.0, "interruptingMultiplier": 1.0}],
"motors": [{"name": "M1", "bus": 3, "ratedMva": 5, "xdppPu": 0.2, "class": "induction-motor", "xOverR": 10, "local": false,
"firstCycleMultiplier": 1.0, "interruptingMultiplier": null}]}`. A `sources` entry describes the case's generator at that bus (by
`name`, or the only one at the bus); the case's X''d must be real, not the synthesised default. A motor entry adds a source with X''d
on its own base. The multiplier per machine type is the owner's to fill from the standard (`class` is only a label); public
descriptions confirm the idea (a larger multiplier in the interrupting network, for example 1.5 for large 1800 r/min induction
motors) but the full table is AWAITING STANDARD TEXT. A missing source makes the bus INCOMPLETE. Exit 0 done; 1 unreadable case or
data file or a study error; 2 usage (no bus, no data file, unknown bus, bad flag); 3 INCOMPLETE data or an inadequate breaker.

Public sources used (none is the standard): EasyPower ANSI short-circuit help, "Calculation and Theory" (separate R network for X/R;
multipliers on rotating-machine subtransient reactance for the interrupting network; NACD from local and remote contributions);
EC&M, "Short Circuit Current Duties of Circuit Breakers and Fuses, Part 1" (first-cycle MF = 1.6 for X/R up to 25 on constant-MVA
breakers); Siemens TechTopics on asymmetrical fault current (S = sqrt(1 + 2 (%dc/100)^2), DC decay set by the fault X/R). The
1.6 is cross-checked in the tests against the half-cycle rms expression (1.598 at X/R 25).

| Flag | Default | Meaning |
|---|---|---|
| `--bus` | required (or `--all-buses`) | Fault bus id. |
| `--all-buses` | off | Run every bus with a base kV (not both with `--bus`, else exit 2). |
| `--ansi-data` | required | Source data file (`gridsim-ansi-data/1`): X/R, local or remote, first-cycle and interrupting multipliers; optional motors. |
| `--parting-cycles` | none | Breaker contact parting time in cycles of system frequency (0 or more). Without it the interrupting duty is OUT OF SCOPE. |
| `--prefault` | 1.0 | Prefault voltage E in per unit; above 0. |
| `--breaker` | - | Breaker rating: rated maximum voltage kV, closing and latching capability kA rms, symmetrical interrupting capability kA; every value above 0. No credit is taken for a voltage below rated (the range factor K is AWAITING). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result (tables `buses`,
`contributions`; [Result files](#result-files)).
