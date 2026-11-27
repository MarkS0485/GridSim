### report

`gridsim report <template.json> <result.json> --out <file> [--format md|html|csv]`
`gridsim report --template-for <result.json> --out <template.json>`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names the report file, not a shared-writer file.

Report generator of the workbench (RC4 R4-10). It renders a result document through a `gridsim-report-template/1` template
and writes the report, so a report made in the Avalonia or web workbench can be made again headless, byte for byte. The
template is JSON: a `title`, an optional `resultSha256`, and `sections` of blocks that are bound to the result document by
JSON pointers (RFC 6901): `text` (literal), `value` (a label and a `path`), `table` (a `path` to an array of objects, with
optional `columns` of `key`, `title`, `unit` and `decimals`) and `plot` (`x` and `y` pointers to number arrays; `kind` is
`line`, `spectrum` or `eigen`; `logX` and `logY` select log axes). Any result JSON can be reported, including the result of
another verb and the workbench's own `gridsim-workbench-result/1` document. The reader is strict: an unknown or duplicate
member is refused, naming the place.

Order of operations: load the template; verify the result's SHA-256 against `resultSha256` when the template names one (a
mismatch writes nothing); resolve the bindings in template order; build the tables and plots; render; write once. A binding
that finds nothing is a visible gap (`n/a` in a cell, a "not drawn" or "no data" note for a block), never a zero. The report
records the SHA-256 of the template and of the result and the generator (`gridsim-report/1`); it holds no timestamp, so the
same inputs give the same bytes. Markdown lists the tables and gives each plot's SHA-256; HTML is self-contained (inline CSS
and SVG, no script); CSV carries every table. The output format follows the `--out` extension (`.md`, `.html`, `.csv`)
unless `--format` is given. `--template-for` writes the default template for a result: a summary, then a section per table
and per plot. It takes a workbench result or a verb's `--json` result (`gridsim-cli-result/1`). For a verb's result the
summary lists the verb, the exit code and every summary value, and each table section binds the table's rows, with the
columns of its first row.

Exit 0 when written, 1 for an unusable template or result, a result that does not match the template's hash, or an existing
`--out` path, 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | required | The new file to write; an existing path is refused. |
| `--format` | from the `--out` extension | `md`, `html` or `csv`. |
| `--template-for` | none | Write the default template for this result document instead of a report. |
