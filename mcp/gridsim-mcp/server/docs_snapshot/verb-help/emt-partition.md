### emt-partition

`gridsim emt-partition <study.json> [--partition auto|<cut.json>] [--threads 1] [--monolithic] [--out <file.json>]`

The partitioned parallel EMT run of the Core (`PartitionedEngine`, `LinePartitioner`). The Core cuts a network only at
single-conductor Bergeron lines, while an `emt3` study builds three-phase buses whose lines are modal, so this verb has its
own input. `<study.json>` is a strict `gridsim-emt-partition/1` document: `time_step_s`, `duration_s`, `sample_every`
(default 1), and the lists

- `sources`: `name`, `node`, `r_ohm` (default 0), `amplitude_v`, `frequency_hz`, `phase_deg` (default 0): the EMF
  `amplitude_v sin(2 pi f t + phase)`;
- `branches`: `name`, `kind` (`resistor`, `inductor`, `capacitor` or `series-rl`), `from`, `to`, and `r_ohm`, `l_h` or
  `c_f` as the kind needs;
- `switches`: `name`, `from`, `to`, `ron_ohm` (default 1e-6), `roff_ohm` (default 1e9), `closed` (default false),
  `events` (`at_s`, `close`);
- `lines`: `name`, `from`, `to`, `surge_ohm`, `travel_s`, `attenuation` (default 1): a Bergeron line with ground return;
- `outputs` (required): `name`, `kind` (`voltage` of a node, `current` of a branch or switch), `target`.

A node named `gnd` or `ground` is the reference. The network is split into partitions: the connected parts left when the cut
lines are removed, named p1, p2, ... in the order their first node appears in the document. A cut line must have a travel
time of at least one step; partitions then run between exchanges for the shortest cut travel time in whole steps, and the
result does not depend on the thread count. Nonlinear elements and the backward-Euler event rule are not offered (the Core
refuses them in a partitioned run). See [ch. 16](<0016 Electromagnetic transients (EMT).md>).

| Flag | Default | Meaning |
|---|---|---|
| `--partition` | none (nothing cut) | `auto` cuts every line whose travel time is at least the step; otherwise a `gridsim-emt-cut/1` file, `{"schema": "gridsim-emt-cut/1", "cut": ["line names"]}`. |
| `--threads` | 1 | Worker threads for the partitions. The traces are the same bits for any count; the result file records the count used (`threads`), as provenance, so two files made with different counts differ in that field only. |
| `--monolithic` | off | Build the same partitions as one network and run the single-network nodal solver (the engine's reference: the same bits). |
| `--out` | none | Write the result as JSON (`gridsim-emt-partition/1`: the plan, the times and one trace per output); an existing file is never overwritten. |

The verb is not on the shared `--json`/`--csv` writer.

Exit 0 when the run completed. Exit 3 when the run ended with a named status other than completed. Exit 1 for a missing or
invalid file, or a network or cut the Core refuses (a cut line shorter than the step). Exit 2 for usage.
