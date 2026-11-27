### sc-trace

`gridsim sc-trace <case> --devices <set.json> (--bus <id> | --branch <i> --fraction <d>) [--objective max|min] [--cti 0.4] [--curves <dir>]`

Short-circuit trace for one fault (RC4 R4-07): the current each placed device measures through its CT, its operating time,
its confidence (`Grounded` for a tabulated manufacturer curve, `Generic` for an equation on a user setting, `NotCheckable`
for a function not evaluated here: a 21, 67, 50N, 51N or 87 is evaluated by [`protection-eval`](#protection-eval)), the sequence of operation (earliest first, ties by device order), the primary and backup
device, and the grading margin of each device against the nearest upstream device on its supply path. A device's direction
is decided by the bridge rule on the fault network; a device in a mesh is marked `meshed` and takes no part in pairing. A
fuse is timed at its total clear time as a primary and its minimum melt as a backup. A device on an out-of-service branch is
reported not evaluated. Exit 0 traced; 3 the fault could not be applied (no source in its island, or a fault along a
transformer); 1 invalid input; 2 usage.

| Flag | Default | Meaning |
|---|---|---|
| `--devices` | required | The `gridsim-devices/1` file. |
| `--bus` | - | Fault bus ID. Exactly one of `--bus` or `--branch`. |
| `--branch` | - | Branch index for a fault along a line; needs `--fraction`. |
| `--fraction` | 0.5 | Position along the branch in [0, 1]. |
| `--objective` | `max` | `max` or `min`. |
| `--cti` | 0.40 | Required coordination time interval (s). |
| `--curves` | installed data | Curve data directory. |

Result files: `--json`, `--csv` or `--out` write the tables `devices` and `pairs` ([Result files](#result-files)).
