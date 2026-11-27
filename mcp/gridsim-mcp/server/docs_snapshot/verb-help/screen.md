### screen

`gridsim screen [<case>] [--dir <path>] [--top 12] [--csv <path>]`

N-1 RoCoF screening: rank every credible single infeed loss against the GB limits (RoCoF 0.125 Hz/s, statutory
49.5 Hz, LFDD risk) and report the binding case's inertia floor and headroom.

Each loss is integrated at the case's own nominal frequency (N275, October 2026), as `trip` is: RoCoF =
f0 * deltaP / (2E), so the 60 Hz `case9` screens at 1.2 times the RoCoF a 50 Hz run of the same loss and inertia gives
(before N275 the screen ran every case at 50 Hz). The nadir is graded against the GB bands about the case's nominal
(N297, October 2026: the GB edges as hertz offsets, so 59.5 Hz statutory and 58.8 Hz LFDD for `case9`; before, a
non-50 Hz case was graded against 49.5 / 48.8 Hz, which its nadirs never reach). On such a case the header's statutory
figure and the `<... Hz` verdict show the moved edge, and a `note:` line under the header names both edges. A 50 Hz
case prints exactly as before.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `data/uk` | Case to load, or a JSON case directory ([Case arguments](#case-arguments)). |
| `--top` | 12 | Rows to print (all losses are still screened and counted). |
| `--csv` | - | Write the full screening table to CSV: `name,lost_mw,remaining_inertia_gva_s,peak_rocof_hz_per_s,nadir_hz,rocof_breach,statutory_breach,lfdd_risk`. The RoCoF column is the peak over the run. |

The last line is the verdict. Exit 3 (`Verdict: NOT SECURE, N of M single loss(es) breach a limit`) when any loss is
`LFDD RISK`, `<49.5 Hz` or `>0.125 Hz/s`; exit 0 (`Verdict: SECURE`) when every loss is `ok`. The CSV is written first
either way.

A case with no inertia data (every in-service generator has `H` = 0, as in the IEEE MATPOWER cases) is refused before
anything is printed or written: `error: screen: case '<name>' carries no inertia data ...`, exit 1. The screen floors
the remaining inertia at 1 MWs to avoid a division by zero, so such a case used to report every loss at an absurd RoCoF
(case14: -5810 Hz/s). The system inertia is the rotating-mass total, as `trip` uses: a unit whose type is a known
converter technology (wind, solar, storage, interconnector and the like) counts no inertia whatever `H` its data carries,
and its loss removes none (N321, October 2026; it used to count; no bundled case has such a unit with `H` above 0, so no
bundled screen moves). A case whose only `H` sits on converter-type units is refused like one with no `H`. A loss that leaves less than 1 MWs on a case that has inertia is still screened (it breaches every
limit) and named on stderr in a `warning: screen: ...` line, because its RoCoF and nadir are the floor's artefact.
