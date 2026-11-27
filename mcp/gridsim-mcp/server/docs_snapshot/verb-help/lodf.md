### lodf

`gridsim lodf <case> --branch <idx> [--top 12]`

Line-outage distribution factors: how the flow of the outaged branch redistributes onto the others, largest first.
`--branch` is the 0-based branch index; out of range exits 2. A bridge outage that islands the system prints that the
LODF is undefined and exits 0.

| Flag | Default | Meaning |
|---|---|---|
| `--branch` | required | 0-based branch index to outage. |
| `--top` | 12 | Branches to list. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
