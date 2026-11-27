### ptdf

`gridsim ptdf <case> --bus <id> [--top 12]`

Power transfer distribution factors: +1 MW injected at `--bus` and withdrawn at the slack, and the share that flows on
each branch, largest first (factors below 1e-4 are not listed). In a case with several DC islands the injection is
withdrawn at the reference bus of the injection bus's own island (its slack, otherwise its largest generator), and the
header says how many islands there are. Exit 2 without `--bus`, or for a `--bus` the case does not have (`error: ptdf
--bus <id>: case '<name>' has no bus <id>.`; it used to print an empty list and exit 0); exit 1 if the factors cannot be
built.

| Flag | Default | Meaning |
|---|---|---|
| `--bus` | required | Injection bus id. |
| `--top` | 12 | Branches to list. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
