### result-diff

`gridsim result-diff <a.json> <b.json> [--out <file.json>] [--max-differences 100]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its diff JSON, not a shared-writer file.

Read-only comparison of two result documents (RC4 R4-01), member by member in document order. A number is the same only
when it is the identical IEEE double; a differing number is reported with its distance in units in the last place (ULP)
and its absolute difference, and the first difference in document order is marked `FIRST`. Two runs of one request
compare equal and byte-identical.

Exit 0 when equal, 3 when they differ, 1 for a document it cannot read or an existing `--out` path, 2 for an invalid
invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the differences as JSON (`gridsim-result-diff/1`) to this new file. |
| `--max-differences` | 100 | List at most this many differences (all are counted); 0 lists every one. |
