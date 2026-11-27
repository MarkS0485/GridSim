### fault-line

`gridsim fault-line <case> --branch <i> --devices <set.json> [--fractions 0,0.25,0.5,0.75,1] [--objective max|min] [--cti 0.4] [--curves <dir>]`

Three-phase fault along a line (RC4 R4-07). For each fraction `d` of branch `i` from its From end, a bus is inserted by
splitting the branch (R, X, B, R0, X0 and the length in the ratio d : 1 - d; both segments keep the thermal rating), the
fault current comes from the IEC 60909 engine (read only), and every placed device's current and operating time are
traced; at `d` = 0 or 1 the fault is at the end bus and nothing is inserted. A transformer is refused for an interior
point (not a line). The line per fraction gives Ik'', the first and second device to operate and the worst pair margin
against `--cti`. Exit 0 every fraction coordinated; 3 a pair is not coordinated; 1 invalid input; 2 usage (a branch outside
the case, a fraction outside 0 to 1, a bad objective).

| Flag | Default | Meaning |
|---|---|---|
| `--branch` | required | Zero-based branch index to fault along. |
| `--devices` | required | The `gridsim-devices/1` file. |
| `--fractions` | `0,0.25,0.5,0.75,1` | Comma list of positions in [0, 1], evaluated in ascending order. |
| `--objective` | `max` | `max` (c = 1.10 HV) or `min` (c = 1.00 HV, the protection-sensitivity case). |
| `--cti` | 0.40 | Required coordination time interval (s). |
| `--curves` | installed data | Curve data directory. |

Result files: `--json`, `--csv` or `--out` write the table `fault_line` ([Result files](#result-files)).
