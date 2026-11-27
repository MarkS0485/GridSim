# 47. MCP server design

The GridSim MCP server (`mcp/gridsim-mcp/`) exposes GridSim to AI tools through the Model Context Protocol. It adds no
numerics: every number it returns is computed by an unchanged `gridsim` program over `gridsim rpc --protocol 2` or a
verb run, and is passed through as GridSim wrote it. What the server adds is a tool contract, a verb allowlist, a
hash-chained study record that replays, a build-status stamp, and a cleaned, offline process environment. This chapter
describes each, the properties the tests pin, and the limits. How to use it is in
[manual ch. 37](<../manual/0037 Using GridSim from AI tools (MCP).md>).

## 47.1 Architecture

```
AI tool (any MCP client)
   | stdio, MCP (JSON-RPC)
gridsim-mcp  (Python, the official MCP SDK)    allowlist, evidence, study record, build status
   | stdio pipe, gridsim-rpc/2 (vendored python/gridsim ModelSession), one host per session
gridsim rpc  (the user's GridSim installation, unchanged)
```

- **Transport.** The MCP client starts `uv run --quiet --script server/main.py` (`.mcp.json`) and speaks MCP over the
  child's standard input and output. The server is started with `run("stdio")`; it never starts an HTTP or SSE
  transport.
- **Dependencies.** `server/main.py` declares its dependencies inline (PEP 723): the MCP SDK at one exact version, and a
  `[tool.uv] exclude-newer` date. `server/main.py.lock` (written by `uv lock --script`) fixes every transitive package
  to an exact version and file hash; `uv run --script` uses it. GridSim.Core gains no dependency: the SDK lives only in
  the plugin.
- **Host pool.** `Pool` (`host.py`) holds named sessions. A `Session` owns one resident GridSim host (`_Host`, a
  subclass of the vendored `gridsim.ModelSession`), the cases it loaded, the result documents it kept, and its study
  record. A session opens on first use.
- **Vendored client.** `server/vendor/gridsim/` is a byte-identical copy of the repository's `python/gridsim` package
  (standard library only); `tools/sync_vendor.py` refreshes it.
- **One dispatch path.** Every tool is a plain function registered in `registry.TOOLS`. `registry.dispatch` runs it,
  turns guard refusals and GridSim errors into error results, computes the hashes, appends the record entry and shapes
  the answer. `record_replay` re-runs recorded calls through the same function, so a replay is checked exactly as the
  original call was made.

The host token of a level 2 rpc host is random per process ([ch. 41](<0041 Scripting object model and API.md>)). The
server never shows it: cases are renamed `c1`, `c2`, ... and results `r1`, `r2`, ... per session, and `scrub`
replaces the token with `<host>` in every response and error message before anything is shown or hashed.

## 47.2 The tool contract

Twenty-six tools are registered, plus four validation tools, five case lab tools and the rebuild tool, each offered
only when enabled (below, 47.10 and 47.11). Each has a
name of 64 characters or fewer (checked at registration), a title, a description, MCP annotations and an output
schema.

| Tool | readOnly | destructive | idempotent | GridSim operation |
|---|---|---|---|---|
| `session_open` | no | no | yes | starts `gridsim rpc --protocol 2` |
| `session_close` | no | yes | yes | ends the host |
| `case_new` | no | no | yes | writes a one-bus MATPOWER seed, rpc `load` |
| `case_load` | no | no | no | rpc `load` |
| `case_inspect` | yes | no | yes | rpc `elements`, `get` |
| `case_edit` | no | yes | no | rpc `set`, `add`, `delete` |
| `case_history` | yes | no | yes | rpc `edits` |
| `case_save` | no | no | yes | rpc `save` |
| `case_validate` | yes | no | yes | `validate` |
| `solve_pf` | yes | no | yes | rpc `solve`, or `study pf` with `full` or `tuning` |
| `study` | yes | no | yes | rpc `study` |
| `run_verb` | no | no | yes | rpc `run-verb` |
| `tunables_list` | yes | no | yes | none (reads `tunables.json`) |
| `physics_check` | yes | no | yes | `physics-check` |
| `diff_cases` | yes | no | yes | `case-diff` |
| `diff_results` | yes | no | yes | `result-diff` |
| `reproduce_check` | yes | no | yes | rpc `study`, two to five times |
| `record_export` | no | no | yes | none (writes the record) |
| `record_replay` | no | no | yes | re-dispatches the record |
| `preflight` | yes | no | yes | `--version`, `compliance verify`, `simdcheck`, `physics-check case9`, `pf case9` |
| `import_model` | no | no | no | `convert`, `dgs-import` or `scl-import`, then rpc `load` |
| `line_parameters` | yes | no | yes | rpc `line-constants`, or `line-constants --cable` |
| `study_case_run` | no | no | yes | `study-case` or `planning-study` |
| `report_render` | no | no | yes | `report --template-for`, `report` |
| `explain_solve` | yes | no | yes | `convert` (edited case to `.m`), `<case> --once --enableExport` |
| `break_it` | no | no | no | rpc `load` (a copy), `set`, `solve`, `study fault`; the solve export on non-convergence |
| `compare` | no | no | yes | the harness's `lab serve` daemon (`compare`), else `run.py --lab --correctness-only` |
| `crossval_single` | no | no | yes | `crossval`, `modal --export` or `stability --export`, then a `tools/crossval` sidecar |
| `divergence_triage` | no | no | yes | reads a Lab bundle; `physics-check`, `crossval`, `residual_check.py`, `physics_audit.py` |
| `build_regression` | no | no | yes | `breaking-change validate --journal <session journal> --json` |
| `caselab_run` | no | no | no | `convert`, `validate`, `physics-check`, `pf` per case; the Lab daemon per case and mode; the triage evidence |
| `caselab_status` | yes | no | yes | none (reads `campaign.json`) |
| `caselab_findings` | yes | no | yes | none (reads `findings.jsonl`) |
| `caselab_reproduce` | no | no | yes | the case gates, then the Lab daemon |
| `caselab_propose` | no | no | yes | none, or the case gates |
| `rebuild_on_demand` | no | no | no | `git ls-files`, `git status`, then `dotnet build src/GridSim.Cli -c Release` in a copy; `--version`, `compliance verify` and rpc `hello` of the new program |

**Validation tools.** `compare`, `crossval_single`, `divergence_triage` and `build_regression` are in the registry's
`compare` group. `registry.enabled_tools` lists them, and `build_server` registers them, only when
`GRIDSIM_MCP_ENABLE_COMPARE` is true and `GRIDSIM_MCP_HARNESS` names a folder holding `run.py` and `lab/serve.py`;
otherwise they are not listed, and a direct dispatch of one is refused with the neutral words. They run GridSim through
the CLI (not the rpc host), with the cleaned environment, so a configured licence reaches the CLI paths of cases above
1,500 buses; the harness also gets `GRIDSIM_CLI`, the configured program. They call the `crossval` verb, which
`run_verb` refuses, with a fixed argument list. The Python for the harness and the sidecars is
`GRIDSIM_MCP_LAB_PYTHON`, else the harness's own `lab/local.json`, else the server's own interpreter (on which a
sidecar exits 77, not run).

- `compare` starts the harness's `lab serve` once per server process (stdio JSON lines, no socket) and sends it one
  `compare` request per call; the daemon's workers stay warm between calls and the daemon is shut down by
  `Pool.close_all`. When the daemon cannot start, the harness command line runs the cell instead. The bundle is a
  normal `gridsim-lab-bundle/1` with run type `lab-correctness`, under `.gridsim-mcp/compare`.
- `divergence_triage` classifies each tool of each cell: GridSim gate fail first; then a rival gate fail, unless a
  documented model difference explains it (PowerModels dcpf on a case with an off-nominal tap or a phase shift,
  read from the bundle's input case or the harness's `modelDifference` mark; MATPOWER CPF outside GridSim's bracket;
  EXDC2); then, with both passing, a failed cross-check or an element diff above the thresholds (1e-6 pu, 1e-5 deg,
  1e-4 MW or MVAr for pf and pfq; 1e-6 deg and 1e-4 MW for dcpf; 1e-3 MW of dispatch per bus for dcopf) is a
  genuine disagreement; else agreement. The
  evidence runs `residual_check.py` and `physics_audit.py` on GridSim's tight-tolerance export of the bundle's input
  case and on a copy of it carrying the rival's bus state (GridSim's physics block removed).
- `crossval_single` passes its whole answer through `tools_compare.scrub`: the interpreter, the GridSim program, the
  sidecar folder, the harness, the workspace and the home folder are replaced by `<python>`, `<gridsim>`,
  `<crossval>`, `<harness>`, `<workspace>` and `<home>` (longest first, in both slash forms), and any other absolute
  path (a drive path, a UNC path, or a POSIX path under a system or user root) by `<path>`.

`openWorldHint` is false on every tool: none reaches outside the machine. `destructive` marks the two tools that discard
state (an edit replaces the held model; closing a session drops its cases). Every tool also takes `session` (default
`default`) and `reasoning`; `reasoning` goes into the record and is never sent to GridSim.

**Structured content.** A successful call returns structured content `{document, evidence, ...}` and the same as text,
headed by a one-line summary and the build tag. `document` is GridSim's JSON as returned: a `gridsim-cli-result/1`
document for a verb that writes one, the rpc `result` object for an object-model operation, or
`{verb, exitCode, stdout, stderr}` for a verb that only prints. A few tools add fields beside it (`case_id`, `file`,
`reproduce`, `stderr`). The output schema (`ToolOutput`) declares `document` as any JSON and `evidence` as below, and
allows the extra fields. A refused or failed call returns `isError` with the message as text, and still writes a
record entry.

**The evidence block.**

| Field | Content |
|---|---|
| `tool`, `status`, `session` | The call; `status` is `ok`, `refused` or `error`. |
| `build` | `version`, `sha256` and `build_status` of the GridSim program (47.5). |
| `build_tag` | `[RELEASE]` or `[UNSUPPORTED BUILD]`, followed by `TUNED` on a tuned run (47.10). |
| `tuned`, `tuning`, `off_standard` | Present only on a tuned run: the `tuned: <key=value, ...>` marker, the tuned values, and for STANDARD keys `off-standard: <standard> no longer complied with`. |
| `case`, `case_hash` | The case id and the state hash: SHA-256 of the canonical JSON `{load_sha256, edits}`, where `load_sha256` is the rpc `case_sha256` of the load and `edits` the rpc edit log. |
| `result`, `result_hash` | The result id (for a kept `gridsim-cli-result/1` document) and the SHA-256 of `document` as canonical JSON. |
| `record_seq`, `record_hash` | The record entry this call wrote. |
| `note` | The fixed statement that the solver is verified and the case and study are not validated. |

Canonical JSON is `json.dumps` with sorted keys, no spaces, ASCII escapes and `allow_nan=False`; GridSim writes
non-finite values as strings, so a NaN reaching the canonicaliser is an error rather than a silent hash.

**Files.** A study on an edited case goes through rpc `study`, which writes and checks a JSON v2 copy itself
([ch. 41](<0041 Scripting object model and API.md>)). A verb that reads a file (`physics_check`, `case_validate`,
`run_verb` with `case_id`) gets the source file when the case is unedited, and otherwise a JSON v2 copy written by rpc
`save` to `.gridsim-mcp/cases/<first 16 hex of the state hash>/case.json`. `diff_cases` writes both sides as JSON v2
under the same file name, a case id by its state and a file by its load hash (`f-<hash>`), because `case-diff` matches
network files by name. Result documents are kept as `.gridsim-mcp/results/<first 16 hex of result hash>.json`.

**Resources.** `gridsim://plugin/allowlist`, `gridsim://plugin/evidence-rules`, `gridsim://plugin/record-format`,
`gridsim://schemas/index` and `gridsim://schemas/v2/{name}` (from a `schemas/v2` folder above the GridSim program, when
there is one), and `gridsim://manual/verb/{verb}` (the output of `gridsim help <verb>`, for allowlisted verbs only;
when the program gives no help, the verb's ch. 19 section from the snapshot below).
`gridsim://plugin/starter-cases` and `gridsim://plugin/construct-method` are plugin texts, and
`gridsim://plugin/tunables` is the tunables list (47.10). `gridsim://docs/troubleshooting`,
`gridsim://docs/per-unit`, `gridsim://docs/grounded-values` and `gridsim://docs/dynamic-models` serve sections of
GridSim's chapters, extracted by section heading.

- The plugin ships them in `server/docs_snapshot`, so they work with a release install, which carries only the CLI
  reference. `tools/sync_docs.py` writes the snapshot from a GridSim checkout: the ch. 30 index and the entries the
  message map points at, the ch. 2 per-unit section, the ch. 13 provenance tags and grounded values, the ch. 24 model
  list and the ch. 19 section of every allowlisted verb, one file per verb (`verb-help/<verb>.md`). `MANIFEST.json`
  names each source chapter with its SHA-256.
- Each extract passes a filter that drops every paragraph, list item and table row about services and data the server
  does not offer, that is material outside the public product scope. The Troubleshooting index also leaves out the
  sections about those services. GridSim's documents are not changed.
- At run time, when a `docs` folder sits above the program (a checkout), the server extracts the same sections and
  serves them only when they are identical to the snapshot; otherwise it serves the snapshot.

**Prompts.** Six lesson prompts and `study_writeup`, each taking a case name and ending with the evidence rules.

**Running GridSim's own messages.** `Session.run_raw` is rpc `run-verb` that returns a run with no result document
(a solve that does not converge, a refused import) as an answer with its exit code and printed text, with the
workspace's absolute path written as `<workspace>`. `explain_solve` and `break_it` use the bare solve form
(`gridsim <case> --once --enableExport=<file>`), which reads a built-in name or a MATPOWER file; an edited case is
converted to `.gridsim-mcp/exports/<state hash>.m` first, and the convert notes and round-trip line are returned. The
summary leaves out the export's `meta` (clock and machine), so it hashes the same on every run. `docs_index`
maps a message to a chapter 30 entry with an ordered list of patterns; a match that is not the entry for that exact
message says so (`match: closest`).

## 47.3 The allowlist

`allowlist.py` is an allowlist, not a denylist. `check_verb` runs before every GridSim call that names a verb and
refuses, raising `Refused` with a message, unless every rule holds:

1. The canonical verb (lower case, aliases `n-1`, `n-2`, `nminus1minus1` resolved) is in `ALLOWED_VERBS`. A verb in
   `REFUSED_VERBS`, or any other unlisted verb, is refused. A new GridSim verb is therefore refused until it is
   classified.
2. No argument is a flag in `REFUSED_FLAGS` (measurement and live inputs, network endpoints, devices, plug-in code,
   horizon and clock overrides, and `--json`, `--csv` and `--audit-log`, which the server names itself), in any
   `--flag=value` form, and no flag name contains a word of `REFUSED_FLAG_WORDS`.
3. No argument names a forbidden location (a path segment matching a private data or recorded-data folder name).
4. The first argument is not a refused sub-command (`user-model check-plugin`, `audit sign`, `audit keygen`,
   `breaking-change import`).
5. A shipped GB case (by built-in name or by path) is used only with a verb in `GB_STATIC_VERBS`.
6. **No-case refusal.** A verb outside `GB_STATIC_VERBS` with no case named, neither as its first argument nor as a held
   case, is refused unless it takes no case at all (`simdcheck`, `gpucheck`, `help`, `ref`, `user-model`, `audit`,
   `breaking-change`, `experimental`). Several verbs fall back to a GB case when none is given; the rule keeps rule 5
   from being bypassed by omission.

The allowed set is the static, case-in result-out verbs. Refused groups are the verbs that take recorded, live or
time-stamped inputs, estimation on recorded data, resident services, licence handling, the rpc host itself,
benchmarking, and the corpus-wide validation sweeps. Every refusal uses the same neutral words: the verb, flag or
location is not available through the GridSim MCP server, with at most a pointer to an alternative tool or to
`gridsim://plugin/allowlist`. The allowlist resource lists the allowed verbs and the flags the server manages itself.
A refusal is recorded like any call, with status `refused`.

Paths are confined by `paths.resolve`: a token is resolved against the workspace, and must lie inside the workspace
(write) or the workspace or the server's starting folder (read). UNC and network paths are refused. `run_verb` checks
every path-like argument, and `--out` targets must lie inside the workspace.

## 47.4 The study record, `gridsim-mcp-record/1`

`record.py`. JSON Lines, one canonical JSON object per line.

**Header** (line 1): `schema`, `server` (`gridsim-mcp <version>`), `build` (`version`, `sha256`, `build_status`),
`licensed` (`yes` or `no`), `protocol` (`gridsim-rpc/2`) and `hash`, the SHA-256 of the canonical header without
`hash`.

**Entries** (one per call): `seq` (1, 2, ...), `tool`, `arguments` (as given, without `session` and `reasoning`),
`reasoning`, `status`, `exitCode`, `case`, `caseHash`, `resultHash`, `result`, `build`, `message` (the refusal or
error text), `prevHash` (the previous entry's `hash`, or the header's for entry 1) and `hash`, the SHA-256 of the
canonical entry without `hash`. A tuned call also carries `tuned` (the marker) and, for STANDARD keys, `offStandard`;
an untuned call carries neither, so its entry is exactly what it would be without tuning support.

**Hashed**: every field above, so the arguments, the reasoning, the outcome, the case and result hashes and the build
identity are all bound into the chain. **Not hashed, and not present**: clock time, the session name, the host token,
temporary paths, the licence path and the licence content. `record_export` and `record_replay` are not recorded;
`session_open` and `session_close` are recorded and not re-run.

**Verification.** `record.verify` checks the header hash, then for each entry its `seq`, its `prevHash` against the
running head and its own hash, and names the first entry that fails (TAMPERED). An edit, a removal or a reordering is
caught at the first line it touches.

**Replay.** `record_replay` reads and verifies a record, opens a fresh session `replay-<n>`, and re-dispatches every
recorded call with its recorded arguments and reasoning. Per call it compares `status`, `caseHash`, `resultHash` and
the entry `hash`; the verdict is REPRODUCED only when every call matches, the header hash matches (same build, same
`licensed` value) and the replayed head equals the recorded head. Because case and result ids are assigned in call
order, a replay assigns the same ids.

## 47.5 Build status

`build_status.identify` decides `RELEASE` or `UNSUPPORTED BUILD`, offline. GridSim reports its own build status, from
its build provenance: `build_status` in the rpc `hello`, `buildStatus` in every `gridsim-cli-result/1` document
([manual ch. 19](<../manual/0019 CLI reference.md>)). The server prefers that report and keeps its own check as the
cross-check. GridSim's rule is: `RELEASE` when the binary is a release build (the 64-hex SBOM hash injected by the
release workflow, a clean source tree) and its stamped commit is a full git commit, so it names the exact release
checkout. The compliance verdict is not part of it.

1. `gridsim --version` gives `gridsim <version>+<commit>`; the full commit (40 or 64 hex digits) is looked up in
   `RELEASE-COMMITS`, shipped beside the module (`<commit>  <tag>` lines, `#` comments), the list of published release
   commits.
2. The program file is hashed with SHA-256. For a framework-dependent build the apphost (`gridsim.exe`) is the same
   file for every build, so the managed `gridsim.dll` beside it is hashed instead; a `dotnet <dll>` command hashes the
   dll. The digest is looked up in `RELEASE-SHA256SUMS`, shipped beside the commit list in SHA256SUMS format
   (`<hex>  <name>`, `#` comments): the published release binaries. It is reported as `program_listed`.
3. `gridsim compliance verify` is run; its `version`, `commit`, `verdict`, `build hash` and `release build` lines are
   kept as the attestation, with its exit code. It is reported and does not decide the status.
4. `gridsim rpc` is asked for `hello`, and its `build_status` is kept as `gridsim_reported` (none from a GridSim too old
   to report one).

The server's check (`server_check`) is `RELEASE` only when the commit is listed and the program's SHA-256 is listed.
The build stamp is metadata, not integrity, so the commit alone cannot tell a published binary from a self-built
binary of the same commit; the SHA-256 can. The status
is `RELEASE` only when GridSim reports `RELEASE` and the server's check agrees; when they disagree it is `UNSUPPORTED
BUILD`, and `reasons` names both sides. With no report from GridSim the server's check decides alone. The identity is
computed once per server process and stamped on every answer and in every record header and entry. Each answer that
carries a result document is checked again: when the document's `buildStatus` differs from the status GridSim reported
at the start, that answer's `build_tag` is `[UNSUPPORTED BUILD]` and its evidence carries a `build_note`.

## 47.6 Environment, network and telemetry

**Environment cleaning.** `clean_environment` passes the GridSim host a fixed, minimal environment built from the
server's: it leaves out private data settings, OpenTelemetry settings and the server's own configuration variables;
sets `OTEL_SDK_DISABLED=true`; and sets `GRIDSIM_LICENCE_FILE` when a licence file is configured. Each session adds
`GRIDSIM_JOURNAL`, pointing at its journal in the workspace. The vendored `ModelSession` merges a given environment
over the caller's, which cannot remove a variable, so `_Host` sets the exact cleaned environment on start. The same
cleaned environment is used for `--version`, `compliance verify` and `help`.

**Configuration.** `GRIDSIM_MCP_CLI`, `GRIDSIM_MCP_WORKSPACE`, `GRIDSIM_MCP_LICENCE_FILE`, `GRIDSIM_MCP_ENABLE_COMPARE`,
`GRIDSIM_MCP_HARNESS`, `GRIDSIM_MCP_ENABLE_CASELAB`, `GRIDSIM_MCP_ENABLE_REBUILD` and `GRIDSIM_MCP_REBUILD_DIR` come from the client (in
Claude Code, from the plugin's `userConfig` through `.mcp.json`). An empty value, or an unexpanded `${...}`
placeholder, counts as unset; GridSim is then `GRIDSIM_CLI` or `gridsim` on the PATH, and the workspace is
`gridsim-workspace` in the starting folder. The licence is never opened by the server; `Config.licensed` tests only
that the named file exists.

**Telemetry.** The MCP SDK installs an OpenTelemetry middleware on every server, inert without an exporter; only the
OpenTelemetry API package is installed, with no SDK or exporter. `build_server` removes the middleware, deletes every
`OTEL_*` variable from its own environment and sets `OTEL_SDK_DISABLED=true`. The SDK's HTTP client and server
packages are imported only by the HTTP and SSE transports, which the server does not start. The plugin manifest
declares `network.hosts` empty.

**Network guard.** `tests/netguard.py` installs a Python audit hook that raises on `socket.connect`, `socket.bind`,
`socket.sendto`, `socket.sendmsg` and every name lookup, allowing only the loopback `socketpair` that Python's event
loop builds for itself on Windows. The end-to-end test starts the server through `tests/run_server_offline.py` under
that hook, and the scripted scenario completes with zero network attempts.

## 47.7 Determinism

What holds:

- The same calls, in the same order, on the same build give the same record: every entry hash and the head hash.
  Nothing volatile enters a hash (47.4), and GridSim's own result documents are bit-identical across runs
  ([ch. 46](<0046 Determinism - specification and evidence.md>)).
- An edit through `case_edit` is GridSim's `CaseEditor` edit, so the case state hash, the resident solve and a study of
  the edited case follow GridSim's own determinism rules.
- `reproduce_check` re-runs a study verb in fresh GridSim processes and compares result hashes, so a MATCH is a direct
  observation for that case, build and machine.

Its limits:

- Hashes bind the build identity. A replay on another build gives DIFFERS even where every number agrees; use
  `diff_results` to compare numbers across builds.
- The case state hash is the load hash plus the edit log, not a hash of the model. Two edit sequences that reach the
  same model have different state hashes; a result hash is the right test of equal outcomes.
- A replay assumes the workspace still holds the files the record read. `case_new` and `case_save` accept the same
  bytes again and refuse different content, so a changed file stops the replay at that call.
- GridSim's stated exceptions apply unchanged (the GPU path, dense and sparse WLS; `cycle` is not allowlisted).

## 47.8 Test coverage

The suite (`mcp/gridsim-mcp/tests`, pytest, a dev-only dependency) runs against a built `gridsim` named by
`GRIDSIM_MCP_CLI`; the tests that need GridSim are skipped without one. No .NET build is needed.

| File | What it pins |
|---|---|
| `test_allowlist.py` | Every refused verb and flag is refused, with neutral words only (the allowlist resource too); allowed and unknown verbs; GB cases with static verbs only; the no-case refusal; private and recorded-data paths; refused sub-commands; every verb in the manual's verb index is classified as allowed or refused. |
| `test_record.py` | The chain and its determinism; tampering and reordering are detected; canonical JSON refuses NaN. |
| `test_build_status.py` | RELEASE needs a listed release commit and a listed program SHA-256, and not a passing compliance verdict; either list alone is not enough; a short commit never matches; GridSim's own report is preferred and a disagreement with the server's check gives UNSUPPORTED BUILD with the reason; the shipped list formats (both shipped empty); a local build is UNSUPPORTED BUILD on both sides. |
| `test_netguard.py` | The guard blocks connects and lookups and still allows the socket pair; the server drops the SDK telemetry middleware. |
| `test_tools.py` | Against the real CLI on case9, case14 and a hand-built case: load, solve and output schema; no host token anywhere; a full `nminus1` document; a refused edit changes nothing; guards refused through dispatch and recorded; path confinement; the licence never recorded; the cleaned environment; the same calls give the same record hashes. |
| `test_e2e_stdio.py` | Over MCP stdio with the SDK's own client, under the network guard: a five-bus case built from scratch, power flow, N-1, physics check PASS, case save, an edit, `diff_cases` with exactly one change, `diff_results`, `reproduce_check` MATCH, every excluded request refused, record export and replay REPRODUCED, the planner and student tools, the prompts and the resources listed, a lesson prompt rendered, and zero network attempts. |
| `test_phase3.py` | Against the real CLI: `explain_solve` on case9 (bus types, quadratic order, Jacobian size, determinism, an edited case, a non-converged case mapped to its Troubleshooting entry); `break_it` overload, slack removal, a radial trip, a meshed trip and a fault, on a copy (the case hash is unchanged); `import_model` round trip and notes, no overwrite, a fail-loud v2 write, a MATPOWER target, DGS and SCL messages; `line_parameters` per unit; `study_case_run` on the planning fixture; `report_render` Markdown then HTML; the docs resources with and without a checkout, and every mapped heading present in chapter 30; the docs snapshot
matches the checkout byte for byte, its manifest hashes are right, the filter leaves no excluded subject, and a
checkout that differs gives the snapshot; every snapshot file is under 128 KiB. |
| `test_compare.py` | The validation tools are absent unless enabled and configured (each unset, placeholder or wrong path), and refused through dispatch; every triage class, including the PowerModels dcpf tap case; the tap test on a MATPOWER branch table; the substituted export. With `GRIDSIM_MCP_LAB_TESTS=1` and a harness: `compare` dcpf case14 through the daemon and its triage. |
| `test_caselab.py` | The generator: SplitMix64 reference outputs, the same seed giving the same bytes in every family, well-formed small cases, case *i* independent of `n`; every step and its refusals; seeded perturbations; signatures without numbers and dedup by class, mode, tool and signature; the shrink of a radial case with a tap to two buses and one tapped branch, checked against a closed-form DC power flow with and without taps, its recipe rebuilding it, and a spent budget marked not minimal; the propose template passing GridSim's gates with the same hashes twice; the tools absent unless both options are on. With `GRIDSIM_MCP_LAB_TESTS=1` and a harness: a one-case dcpf campaign whose PowerModels finding is the documented tap difference, shrunk to two buses, and reproduced. |
| `test_tuning.py` | The list is well formed (no PHYSICS key, every STANDARD key names its standard, every verb allowlisted, every flag in its verb's ch. 19 section, no guard setting anywhere in the file); every refusal (unknown key, another verb's key, type, range, cross-key bounds, missing companion, guard words, a tunable flag in plain arguments); the flags and markers, defaults not marking; the record fields only when tuned. Against the real CLI: a tuned `solve_pf` marked TUNED and recorded, a defaults-only map unmarked, a refused map running nothing, a STANDARD key flagged off-standard, `reproduce_check` MATCH with tuning, tuned and untuned results differing, and a tuned session replaying REPRODUCED. |
| `test_rebuild.py` | `rebuild_on_demand` is absent unless enabled and refused through dispatch; every edit refusal (physics, a guard symbol, range, another file, extra fields, a duplicate); a dry run on a small git checkout (edits applied in the copy only, guard files found by name and by symbol, uncopied files left out); a changed guard in the checkout, a build folder inside the checkout, a declaration found twice and a copy that differs are refused, the last with the copy removed; the build environment cannot stamp a release; on the real repository the named guard files are found and the allowlisted files are not guards and match once. |
| `test_scrub.py` | `crossval_single` output shows no machine path: the interpreter, the program, the workspace and the home folder by name, any other absolute path as `<path>`. |
| `test_vendor_and_repo.py` | The vendored package is byte-identical to `python/gridsim`; no forbidden strings or assistant files in the plugin; the manifest and `.mcp.json` (with the maintainer options in the full tree, without them in the public one); the licence file matches the repository's. |
| `test_export_public.py` | The public export: no maintainer file, option, environment variable or marked paragraph; the exported server imports and lists only the public tools with every maintainer option set; the forbidden-string scan; the directory limits (no file over 256 KiB, no symlink); its own repository tests pass on it; an export folder inside the checkout is refused. |

## 47.9 Source map

| File | Responsibility |
|---|---|
| `.mcp.json` | The launch command and the environment taken from `userConfig`. |
| `.claude-plugin/plugin.json` | Plugin name, version, licence, `userConfig`, `network.hosts`. |
| `server/main.py`, `server/main.py.lock` | Entry point with PEP 723 dependencies; the uv lock. |
| `server/gridsim_mcp/app.py` | `build_server`: tool wrappers, annotations, output schema, telemetry removal. |
| `server/gridsim_mcp/registry.py` | Tool registry, `dispatch`, the evidence block. |
| `server/gridsim_mcp/host.py` | `Config`, `clean_environment`, `Pool`, `Session`, `_Host`, case state hash, materialised copies. |
| `server/gridsim_mcp/allowlist.py` | Allowed and refused verbs, flags, sub-commands, GB rules. |
| `server/gridsim_mcp/paths.py` | Path confinement. |
| `server/gridsim_mcp/record.py` | `gridsim-mcp-record/1`: canonical JSON, chain, read, verify. |
| `server/gridsim_mcp/build_status.py` | RELEASE or UNSUPPORTED BUILD. |
| `server/gridsim_mcp/tools_case.py`, `tools_study.py`, `tools_check.py`, `tools_plan.py`, `tools_teach.py` | The tools. |
| `server/gridsim_mcp/resources.py` | The resources and the evidence rules text. |
| `server/gridsim_mcp/tools_compare.py` | The validation tools, the Lab daemon client, the triage rules and the output scrub. |
| `server/gridsim_mcp/tools_caselab.py`, `server/gridsim_mcp/caselab/` | The case lab tools; the seeded generator, case model and steps, triage signatures, shrink, propose template, and the campaign loop and store. |
| `server/gridsim_mcp/tunables.json`, `server/gridsim_mcp/tuning.py` | The tunables list and its strict checks, flags and markers. |
| `server/gridsim_mcp/tools_rebuild.py` | `rebuild_on_demand`: copy, edits, guard proof, build, status check. |
| `server/gridsim_mcp/prompts.py` | The lesson and write-up prompts. |
| `skills/` | `gridsim-study`, `gridsim-teach`, `gridsim-construct`, `gridsim-validate`. |
| `server/vendor/gridsim/`, `tools/sync_vendor.py` | The vendored client and its sync. |
| `server/gridsim_mcp/docs_index.py`, `server/docs_snapshot/`, `tools/sync_docs.py` | The served doc sections and their filter, the Troubleshooting map, the shipped snapshot and its sync. |
| `tools/export_public.py` | The public plugin tree, without the maintainer tools. |
| `assets/icon.svg` | The plugin icon. |

## 47.10 Tuning and rebuilds

**Where tuning can enter.** Without a GridSim change there is one path: the command-line flags of the verbs. The rpc
`solve` operation takes only a handle and solves with fixed options (the `pf` verb's engine, 100 iterations, reactive
limits enforced); rpc `study` and `run-verb` take the verb's arguments. The `solverProfile` kind lists the power-flow
options with their code defaults, but the v2 loader refuses a case that names one, so it is not a run-time path yet.
`PowerFlowOptions.Tolerance`, `FlatStart`, `EnforceQLimits` and `RetryFromDcStart` have no `pf` flag.

**The list.** `tunables.json` (`gridsim-mcp-tunables/1`) holds:

- `tunables`: each key (`<verb>.<name>`), its class, the verbs that take it, the GridSim flag, the type (`number`,
  `integer`, `boolean`, `enum`, `number_or_off`), the unit, the default (with `default_if` where it depends on another
  key: `pf.max_iter` is 10000 with `pf.engine` `gs`), the safe range (`min`, `max`, `exclusive_min`, `exclusive_max`,
  and `min_key` / `max_key` bounds on another key), companion rules (`requires`, `requires_also`) and, for STANDARD
  keys, the standard. Thirteen NUMERICS keys (pf engine, Gauss-Seidel acceleration, iteration and reactive-pass caps,
  reactive revert, divergence stop; the batched N-1 lanes; the cpf steps; the acopf-full iteration cap; the scopf
  rounds and full LP) and seven STANDARD keys (the restoration voltage band; the G99 voltage and frequency pickups of
  `protection-check`; the G5 synthetic spectrum of `harmonics`).
- `fixed`: what is never tunable, for information (the laws, per-unit definitions, physical constants, the case's
  nominal frequency, and the IEC 60909 voltage factor, which no run-time option sets).
- `compile_time`: the main solver constants, with file, value, unit and class, for information.
- `rebuild_allowlist`: the constants `rebuild_on_demand` may change (below).

Settings that protect GridSim's use limits are not in the file in any section.

**The checks.** `tuning.apply(verb, tuning, args)` runs before any GridSim call and raises `Refused` (status `refused`,
nothing run, no result) on: a key or value that names a guard (a fixed word list, checked whatever the file says); an
unknown key; a key of another verb; a value of the wrong type (a boolean is not a number, a fraction is not a whole
number, NaN and infinity are refused); a value outside its range; a cross-key bound broken; a companion key missing; a
flag of the list in the verb's plain arguments. A valid map becomes the flags, in key order, each checked by
`check_flag` as well. A value that differs from its default is a tuned value; the marker is `tuned: ` and the sorted
`key=value` pairs. `solve_pf` with any flag runs `study pf` (the resident solve has fixed options).

**Marking.** `Outcome.tuning` carries the checked map. `dispatch` appends ` TUNED` to the build tag, adds `tuned`,
`tuning` and `off_standard` to the evidence and `tuned` and `offStandard` to the record entry. `reproduce_check` applies
the map to every run. `record_replay` re-dispatches the recorded arguments, `tuning` included, through the same path,
so a tuned call replays to the same entry hash.

**Rebuild on demand.** `rebuild_on_demand` is in the `rebuild` group, offered only when `GRIDSIM_MCP_ENABLE_REBUILD`
is true. Given a source checkout and edits `[{symbol, value}]`:

1. Each edit must name a `rebuild_allowlist` symbol (NUMERICS only; the loader refuses any other class), optionally its
   own file and nothing else, once, with a value inside the range. The list holds `PowerFlowOptions.DefaultTolerance`
   (1e-12 to 1e-6 pu), `CliDefaults.RmsStepS` (0.0005 to 0.01 s) and `CliDefaults.ClassicalStepS` (0.0002 to 0.01 s).
2. The guard files are found in the checkout's tracked files: by name (the root build files, `data/bundled.sha256`,
   the CLI's `Program.cs`, the licensing and supply-chain folders of Core, the data-tooling project) and every
   `.csproj`, `.props` and `.targets` under `src`, plus every `.cs` file under `src` that uses an enforcement symbol
   (`GUARD_PATTERN`: the forward-inference guard and its horizon, the UTC clock and its override, the data-root and
   quarantine types, the licence gate, entitlement and bundled-network rule, the telemetry guard, the build status,
   attestation and provenance, the release-stamp properties). A guard moved to a new file is still found by its symbol.
   An edited file that is a guard file is refused.
3. `git status --porcelain` over the guard files must be empty: a guard changed in the checkout is never built.
4. The build folder (`GRIDSIM_MCP_REBUILD_DIR`, else `.gridsim-mcp/rebuild` in the workspace) must not contain or lie
   inside the checkout. A new numbered folder is made in it.
5. The tracked files a CLI build reads (`src/`, `data/`, `schemas/`, `tools/crossval/`, the CLI reference chapter it
   embeds, and the root build files) are copied, and each edit replaces the value in the constant's declaration, which
   must occur exactly once.
6. Proof: every guard file in the copy has the checkout's SHA-256; every other copied file too; each edited file is
   the checkout's text with exactly that replacement. Any difference removes the copy and refuses.
7. With `dry_run` false: `dotnet build src/GridSim.Cli -c Release -nologo -p:GridSimSbomSha256= -p:GridSimSourceTree=`
   in the copy, with an environment stripped of MSBuild property overrides (`GridSim*`, `GitCommit*`, CI and build
   variables) and `GIT_CEILING_DIRECTORIES` set, so git finds no repository above the copy. The release stamp needs
   the SBOM hash, a clean tree and a full commit, so the build is UNSUPPORTED by construction. The new program's
   `--version`, rpc `hello` and `compliance verify` are read through `build_status.identify`; the program is kept only
   when both GridSim's report and the server's check are UNSUPPORTED BUILD.

The checkout is never written. A cold Release build of the CLI and the projects it references took about seven
minutes on a small laptop.

## 47.11 The case lab

**Gating.** The five tools are the registry's `caselab` group. `tools_caselab.settings` is the compare settings
(47.2) when `GRIDSIM_MCP_ENABLE_CASELAB` is also true, else None; the group is listed and registered only then, and a
direct dispatch is refused with the neutral words.

**The generator.** `caselab/rng.py` is SplitMix64 on 64-bit integers; a float is a 53-bit fraction, every drawn value
is rounded to a fixed number of decimals or significant digits, and the MATPOWER writer formats numbers with `.12g`, so
a family and seed give the same bytes on any platform and Python version. Case *i* of a campaign has the seed
`derive(seed, i, family)` (the first eight bytes of a SHA-256) and the family `families[i mod k]`. The three families
and the ranges they draw from are in `caselab/generator.py`. Design rules applied before any solve (a primary
transformer at most half loaded, every radial OHL section at most 80 % of its rating counting the demand downstream,
the slack sized at twice the demand) are part of the generator, not adjustments after a solve.

**The case and its files.** The case model (`gridsim-mcp-caselab-case/1`) holds buses, generators with quadratic costs
and branches with a kind (`ohl`, `cable`, `tx`). `campaign.prepare` writes `model.json` and `source.m`, then runs,
through the session's rpc `run-verb` (allowlisted verbs only): `convert source.m case.json`, `validate case.json`,
`convert case.json <name>.m`, `physics-check <name>.m` and `pf <name>.m`. JSON v2 cannot hold a cost curve yet, so the
converted file has none; the case's own `mpc.gencost` block is appended to the Lab's file. A case is accepted when every
step exits 0, the power flow converges and every bus voltage lies in 0.90-1.10 pu; otherwise the reason is recorded.
`gates.json` keeps the exit codes and the SHA-256 of every file.

**The loop.** All cases are prepared first. Then for each mode, for each accepted case, one daemon `compare` request
with an output folder under `bundles/`; `tools_compare.classify` triages each rival tool, with n/a tools skipped as in
`divergence_triage`. Classes other than agreement and not compared are findings. The signature is `documented:<id>`
for a documented difference, the sorted element-diff keys over threshold (plus `cross-check`) for a genuine
disagreement, and the reasons with every number replaced by `#` otherwise; the finding id is `F-` and ten hex digits of
the SHA-256 of (class, mode, tool, signature). The first occurrence builds the finding and its evidence
(`tools_compare._evidence`, with the campaign folder shown as `<campaign>` and machine paths scrubbed); later ones add
an occurrence. `budget_minutes` is checked before every cell.

**Refinement.** For each new finding, in order of first occurrence, the case is re-run with GridSim and the one rival
tool only (all tools when the pair alone does not show it). Up to four seeded perturbations (`model.perturbations`,
seeded by the campaign seed and the finding id) are run and recorded. Then `shrink.shrink` applies the first reduction
from `model.reductions` that keeps the same class and signature with GridSim and the rival in the same status as
when the finding was made (a candidate on which one side no longer runs shows something else), and starts again, until none does or `refine_budget`
evaluations are spent. A reduction that does not make the case smaller under `model.size` (buses, branches,
generators, details) is never tried, and every candidate passes `prepare` first. The reproducer's recipe is the source
(family and seed, or the stored model) plus the steps; its files are prepared again under `reproducers/<id>/` and their
hashes recorded. `caselab_reproduce` rebuilds the recipe, prepares it under `reproduce/`, compares every hash and
re-runs the cell.

**What is deterministic.** With the same parameters, build and rival tools: every case file, the gate results, the
counts and `findings.jsonl` (paths relative to the campaign folder). A bundle's manifest, report, attempt log and
environment captures carry wall-clock times, timings, process lists and the output folder, so bundles differ between
campaigns. The campaign id is `cl-<seed>-<parameter hash>`, with `-2`, `-3`, ... for a repeat.

**AI-designed cases.** `caselab_propose` returns `propose.template()` (a 3-bus example), the instructions and the
generator's ranges. A case sent back is normalised (defaults, family `proposed`, six significant digits), checked by
`model.check_structure`, and prepared under `caselab/proposed/<name>-<hash>/`. The server makes no model call.

## 47.12 The public plugin

The plugin folder on the development branch is the full tree. The public plugin is the same server without the
maintainer tools, written by `python tools/export_public.py <folder>` to a folder outside the checkout:

- Left out: `tools_compare.py`, `tools_caselab.py`, `caselab/`, `tools_rebuild.py`, their tests, the
  `gridsim-validate` skill, the export script and its test; the options `enable_compare`, `harness_path`,
  `enable_caselab`, `enable_rebuild` and `rebuild_dir` and the environment variables they feed; and every README
  paragraph between `<!-- maintainer-only -->` and `<!-- /maintainer-only -->`.
- Everything else is copied byte for byte.
- `registry.MAINTAINER_MODULES` names each maintainer group's module. `build_server` imports the ones present, and a
  group whose module is absent is off, so the exported server starts and lists only the public tools whatever the
  environment says.
- `test_export_public.py` exports to a temporary folder and checks it; `claude plugin validate --strict --profile
  directory` runs on the result.

## Limits

- A study after an edit needs a case that JSON v2 can hold. A bus with no positive base voltage (case14 has such
  buses) makes rpc `study` refuse an edited case; `solve_pf`'s resident solve still works, and other verbs run on the
  case saved as RAW.
- `diff_cases` compares JSON v2 rewrites of both sides, so a field the JSON v2 writer does not carry is not compared.
- The case state hash is derived by the server, because rpc `case_sha256` stays the load hash after edits.
- `RELEASE` needs the release's commit in the plugin's `RELEASE-COMMITS` and the published binary's SHA-256 in its
  `RELEASE-SHA256SUMS`, both filled in as releases are published; until a release is listed every build is stamped
  UNSUPPORTED BUILD. A build made outside the release workflow from a clean checkout of a release commit, with an SBOM
  hash passed in by hand, reads RELEASE in GridSim's own report; its SHA-256 is not listed, so the server's check
  stamps it UNSUPPORTED BUILD. Authenticode signatures are not checked.
- The network guard is a test-time audit hook over the server's Python process. GridSim itself, a separate process,
  is covered by its own offline properties ([ch. 35](<0035 Deployment and security.md>)).
- uv fetches the locked packages from the package index on the first start; later starts use its cache.
- Tuning reaches only the options GridSim's verbs already have. The GPU backend is not on the list: its arithmetic is
  outside the bit-identity contract that `reproduce_check` and `record_replay` rest on.
- `rebuild_on_demand` proves that the copy's guard files equal the checkout's committed ones. It does not prove that
  the checkout's commit is a published one; the build is stamped UNSUPPORTED BUILD whatever the checkout holds.
  `dotnet build` restores packages from the local cache, and from the package index when a package is missing there.
- The case lab's shrink is greedy: its reproducer is 1-minimal under the listed reductions (no single step keeps the
  finding), not the smallest case that shows it. Its plausibility band and value ranges are working placeholders, and
  its cases are Synthetic.
- Further tools are planned.

## See also

- [Manual ch. 37, Using GridSim from AI tools (MCP)](<../manual/0037 Using GridSim from AI tools (MCP).md>)
- [Ch. 41, Scripting object model and API](<0041 Scripting object model and API.md>)
- [Ch. 42, Run journal and breaking-change validation](<0042 Run journal and breaking-change validation.md>)
- [Ch. 46, Determinism: specification and evidence](<0046 Determinism - specification and evidence.md>)
- [Manual ch. 19, CLI reference: rpc](<../manual/0019 CLI reference.md#rpc>)
