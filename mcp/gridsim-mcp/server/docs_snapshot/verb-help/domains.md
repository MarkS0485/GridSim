### domains

`gridsim domains [<case>] [--dir <path>]`

Report the speed domains of the distributed-inertia model: per-domain machine count, GVA.s and GVAd stiffness, and
whether a dominant domain exists (deltaGVA at least 10 %). Default model `data/uk`; a positional case or `--dir`
selects another ([Case arguments](#case-arguments)). It is the diagnostic companion to `--inertia-model distributed`.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
