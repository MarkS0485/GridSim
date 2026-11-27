### case-diff

`gridsim case-diff <a> <b> [--out <file.json>]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its diff JSON, not a shared-writer file.

Read-only comparison of the authored JSON files of two native cases (RC4 R4-01): a v1 case folder, or a v2 case
manifest with its network and asset files. No case is loaded as a model or solved. Records inside an array are matched
by identity (`id`, then `name`, then `from` and `to`, then `bus`, with repeats numbered so parallel circuits stay
distinct), so a permuted record order is reported as `REORDERED` (note that branch and unit indices follow file order),
not as a change. Each changed field is listed with its path, old value, new value and unit (read from the case schema);
`ADDED` and `REMOVED` list records and members, `FILEADDED` and `FILEREMOVED` whole files. Numbers compare as IEEE
doubles, so `1` and `1.0` are the same. A case compared with itself reports no differences; a pair that differs only in
formatting reports none and says so.

Exit 0 when no authored value differs, 3 when they do, 1 for a case it cannot read or an existing `--out` path, 2 for an
invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the differences as JSON (`gridsim-case-diff/1`) to this new file. |
