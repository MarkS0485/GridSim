# GridSim plugin for AI tools

GridSim's deterministic power-system solver, available to Claude Code and other MCP clients. Build a network from
scratch or load one, solve it, run contingency and fault studies, check the solved state against the laws of physics,
see exactly what an edit changed, and keep a hash-chained study record that replays to the same bits.

GridSim itself is not bundled. Install a GridSim release, then point the plugin at it.

## What runs

- **The MCP server** (`server/main.py`): a small Python program the AI tool starts on your machine. It talks MCP over
  stdin and stdout. It opens no network connection and has no telemetry.
- **GridSim**, your installed copy: the server starts `gridsim rpc` (a pipe to a resident GridSim host) and, for some
  checks, `gridsim <verb>` directly. Every number comes from GridSim.
- **Dependencies**: the official MCP Python SDK, pinned (`mcp==2.3.0`), with every transitive package fixed by
  `server/main.py.lock`, fetched once by `uv` when the server first starts. After that nothing is downloaded.

## What is sent: nothing

Nothing leaves your machine. The server reads and writes only the workspace folder (and reads case files in the
project folder you start the AI tool in). See [PRIVACY.md](PRIVACY.md).

## Setup

In Claude Code, for one session: `claude --plugin-dir <plugin folder>`. To keep it installed, add the marketplace
that carries the plugin with `/plugin marketplace add`, then `/plugin install gridsim@<marketplace>`.
<!-- maintainer-only -->
In the development checkout the local marketplace is `mcp/` (named `gridsim-dev`):

    /plugin marketplace add <path to the mcp folder>
    /plugin install gridsim@gridsim-dev
<!-- /maintainer-only -->

Any other MCP client: run `uv run --quiet --script <plugin folder>/server/main.py` as a stdio server, with the
options below as the environment variables `GRIDSIM_MCP_CLI`, `GRIDSIM_MCP_LICENCE_FILE` and `GRIDSIM_MCP_WORKSPACE`.

1. Install GridSim and [uv](https://docs.astral.sh/uv/).
2. Install the plugin, then set its options:
   - `gridsim_cli`: the path of the `gridsim` program (or leave empty to use `gridsim` on the PATH);
   - `licence_file` (optional): your GridSim licence file, needed for cases over 1,500 buses. GridSim reads it; the
     plugin passes only the path and never reads, prints or records it;
   - `workspace` (optional): where cases, results and records go (default: `gridsim-workspace` in the project).
3. Ask the AI to run `preflight`. It shows the GridSim version, the build status and the CPU tier.

## Tools

| Tool | What it does |
|---|---|
| `session_open`, `session_close` | One resident GridSim host per session, with its own cases, results and record |
| `case_new` | A one-bus slack seed to build a network on |
| `case_load` | A built-in case (case9, case14, ...) or a file (MATPOWER, PSS/E RAW, JSON) |
| `case_inspect`, `case_history` | Elements and attributes (solved flows too), and the edit log |
| `case_edit` | set, add or delete elements; GridSim checks every edit before applying it |
| `case_save`, `case_validate` | Write JSON v2 or PSS/E RAW; check against the v2 schema |
| `solve_pf`, `study` | The power flow; pf, nminus1, nminus2, iec60909, fault, acopf-full, dcopf, scopf, stability |
| `run_verb` | Any verb on the allowlist, as on the command line |
| `tunables_list` | The values you may tune (STANDARD or NUMERICS), with flag, default and safe range |
| `physics_check` | The solved state against energy balance, Ohm's law, KCL and more |
| `diff_cases`, `diff_results` | What an edit changed; what moved between two results, by ULP |
| `reproduce_check` | The same study run several times: MATCH when the bits agree |
| `record_export`, `record_replay` | The study record, and its replay with hash comparison |
| `preflight` | Version, build status, compliance attestation, CPU tier, a case9 check |
| `import_model` | Bring in MATPOWER, RAW, RAWX, UCTE, CGMES, JSON, DGS or SCL, with the dropped-feature notes and the round-trip statement |
| `line_parameters` | R, X and B from conductor geometry (overhead lines and single-core cables) |
| `study_case_run` | A declarative study case or planning study, with each job's result hash |
| `report_render` | A hash-bound Markdown, HTML or CSV report of a result |
| `explain_solve` | Inside one solve: iterations, mismatch decay, PV to PQ switches, worst bus, Jacobian for small cases |
| `break_it` | A guided failure on a copy of the case, with GridSim's message and the Troubleshooting entry |

<!-- maintainer-only -->
Validation tools, for the maintainer, offered only when the option `enable_compare` is on and `harness_path` names the
Lab Mode harness with its rival runtimes:

| Tool | What it does |
|---|---|
| `compare` | One correctness-only Lab Mode cell against pandapower, MATPOWER and PowerModels: each tool's physics gate, the cross-check, and an element diff between every pair of passing tools |
| `crossval_single` | One of GridSim's own cross-validation sidecars on a case (exit 77: not run) |
| `divergence_triage` | GridSim gate fail, rival gate fail, documented model difference or genuine disagreement, with both sides' evidence |
| `build_regression` | `breaking-change validate` over the session's run journal |

Case lab, for the maintainer, offered only when the option `enable_caselab` is on as well as the validation tools:
`caselab_run` generates small, plausible cases from a seed (same seed, same bytes), gates each in GridSim, compares it
with the rival tools mode by mode, triages and deduplicates every non-agreement and shrinks each new finding to a
minimal reproducer; `caselab_status`, `caselab_findings` and `caselab_reproduce` read and re-check a campaign;
`caselab_propose` hands an AI a template to design a case itself and gates what comes back. Findings stay in
`caselab/` in the workspace; every proposal is text only.

Rebuild on demand, for the maintainer, offered only when the option `enable_rebuild` is on: `rebuild_on_demand`
copies a GridSim checkout to a folder outside it, changes allowlisted solver constants there, proves every guard file
byte-identical, builds the CLI and checks it reports UNSUPPORTED BUILD.
<!-- /maintainer-only -->

Skills: `gridsim-study` (planner workflow), `gridsim-teach` (build it, then break it) and `gridsim-construct` (a
short stub). Prompts: six lessons (per-unit, PV and PQ, reactive limits, N-1, fault level, inertia and RoCoF) and a
study write-up.
<!-- maintainer-only -->
Maintainer skill: `gridsim-validate` (the divergence protocol and the case lab).
<!-- /maintainer-only -->

Every answer carries GridSim's own JSON and an evidence block: the build and its tag, the case hash, the result hash
and the record entry. Every tool also takes an optional `reasoning` string, kept in the record.

## Tuning

`solve_pf`, `study`, `run_verb` and `reproduce_check` take an optional `tuning` map, checked strictly against
`server/gridsim_mcp/tunables.json` (`gridsim-mcp-tunables/1`): an unknown key or a value outside its safe range is
refused and nothing runs. Each key is one of GridSim's own flags. A tuned result is tagged `TUNED` with its values;
a STANDARD key also says which standard is no longer complied with. Physics is never tunable, and neither are the
settings that protect GridSim's use limits.

## Build status

An answer is tagged `[RELEASE]` only when GridSim reports itself a CI release build of a clean checkout and the
commit it names is listed as a published release commit, and the program's SHA-256 is listed as a published release
binary. Any other build (a local or tuned build) is tagged
`[UNSUPPORTED BUILD]`. The compliance verdict of `gridsim compliance verify` is reported beside the tag and does not
decide it.

## The study record

`gridsim-mcp-record/1`: one JSON line per call, each holding the previous line's SHA-256, so any change breaks the
chain. No clock time, session name or host token is hashed, so replaying a record on the same build gives the same
hashes. The format is served as the resource `gridsim://plugin/record-format`.

## Guards

Only allowlisted verbs run. Verbs outside the public product scope (those that take recorded, live or time-stamped
inputs, resident services, and any override of the time horizon) are refused by name, with a reason. Shipped GB
cases are offered to static verbs only. The list is the resource `gridsim://plugin/allowlist`.

## Resources

The allowlist, the evidence rules, the record format, the manual section of every allowlisted verb (rendered by your
GridSim from its embedded manual), GridSim's Troubleshooting index, per-unit conventions, grounded parameter values
and dynamic-model list (a snapshot ships in `server/docs_snapshot`), and, when your GridSim installation carries them,
the v2 schemas.

## Development

Tests use pytest and a built GridSim (`GRIDSIM_MCP_CLI` names it):

    python -m pytest tests

`server/vendor/gridsim` is a byte-identical copy of GridSim's `python/gridsim` package: `python tools/sync_vendor.py`
refreshes it and a test checks it. `server/docs_snapshot` holds the doc sections the resources serve:
`python tools/sync_docs.py` refreshes it from the checkout and a test checks it.

## Manuals

- [Using GridSim from AI tools (MCP)](<../../docs/manual/0037 Using GridSim from AI tools (MCP).md>): installing,
  configuring and using the tools.
- [MCP server design](<../../docs/technical/0047 MCP server design.md>): the tool contract, allowlist, study record,
  build status and tests.

## Licence

GridSim licence, see [LICENSE.md](LICENSE.md). Source-available, not open source.
