### audit

`gridsim audit verify <file> [--anchor-count N --anchor-head HEX] [--pubkey <public.pem> [--anchor <file>] [--sig <file>]]`
`gridsim audit show <file> [--anchor-count N --anchor-head HEX]`
`gridsim audit sign <file> --key <private.pem> [--anchor <file>] [--sig <file>]`
`gridsim audit keygen <private.pem> <public.pem>`

Verify a tamper-evident audit log (`gridsim-audit/1`, [technical ch. 31](<../technical/0031 Audit log and diagnostics.md>)):
recompute the hash chain line by line. Prints `OK <file>: N entries, head H` and exits 0 when it verifies; prints
`TAMPERED <file>: entry i: <reason>` and exits 3 at the first edited, deleted, inserted, reordered or truncated entry;
exits 1 when the file is missing or unreadable and 2 on a usage error. Tail truncation and a whole-log rewrite keep a
self-consistent chain, so they are caught only against an anchor kept outside the log: pass the entry count and head
hash from an earlier verify, from the `audit:` line a run printed, or from the log's anchor file
(`gridsim-audit-anchor/1 count=N head=H`, see `--audit-anchor`). The audited verbs and the web host also check the
anchor file themselves before they append.

`show` runs the same verification and, only when the whole log verifies, first prints every entry, one per line, as
`#<seq> run=<UTC> state=<UTC or -> provenance=<tag> hash=<hash> <summary>` (the summary is the recorded `actor=...
action=... inputs=... outcome=...` text), then the same `OK` line. A log that does not verify prints no entries at all,
only the `TAMPERED` line, and exits 3: nothing after a broken link can be trusted. Exit codes are as for `verify`.

| Flag | Default | Meaning |
|---|---|---|
| `--anchor-count` | - | The entry count of a trusted earlier state (with `--anchor-head`; one without the other exits 2, and a negative count exits 2). |
| `--anchor-head` | - | The head hash of that state (with `--anchor-count`). |
| `--pubkey` | - | `verify` only: an ECDSA P-256 public key (PEM). The signature file over the anchor file must exist and verify (`TAMPERED`, exit 3, otherwise), then the log is verified against that anchor. Does not combine with `--anchor-count` / `--anchor-head` (exit 2). |
| `--anchor` / `--sig` | `<log>.anchor` / `<log>.anchor.sig` | `verify --pubkey` and `sign`: the anchor file and its detached signature file. |
| `--key` | - | `sign` only: the ECDSA P-256 private key (PEM). `sign` verifies the log against its anchor first and signs nothing when it does not verify (exit 3). |

`keygen` writes a fresh P-256 key pair (the private key for the signing station, the public key for the host) and
refuses to overwrite an existing file (exit 1).
