# 37. Using GridSim from AI tools (MCP)

GridSim can be driven by an AI assistant. The GridSim MCP server is a small program that sits beside your GridSim
installation and offers GridSim's solver to any AI tool that speaks the Model Context Protocol (MCP): Claude Code,
desktop chat apps and other MCP clients. You ask in plain words ("build a five-bus 132 kV ring, solve it and check it
is N-1 secure"); the AI calls GridSim's tools, and every number in the answer comes from your own GridSim, with the
hashes to prove which build and which case produced it. Use it for rapid prototyping, for teaching, and for studies
where you want the AI's reasoning written down next to GridSim's evidence. The design is in
[technical ch. 47](<../technical/0047 MCP server design.md>).

## What MCP is

MCP is a standard plug between AI tools and the programs they can use. A program that offers itself through MCP is
called a server, and it advertises three things:

- **Tools**: functions the AI may call, each with a typed input and output (`solve_pf`, `physics_check`, ...).
- **Resources**: documents the AI may read (the verb allowlist, the evidence rules, the manual section of a verb).
- **Prompts**: ready-made instructions. The GridSim server offers tools and resources; its written workflow ships
  with the plugin as a skill, `gridsim-study`.

The AI tool starts the server on your machine and talks to it through the server's standard input and output. There
is no network connection between them, and the server has no network connection to anything else.

## Before you start

- **GridSim**, installed. The MCP server does not contain GridSim; it runs the `gridsim` program you point it at.
- **uv**, the Python launcher from Astral (`winget install --id astral-sh.uv -e` on Windows, or the installer at
  docs.astral.sh/uv). uv starts the server and fetches its one dependency, the official MCP Python SDK, at the exact
  versions fixed in the lock file beside the server.
- **An MCP client.** Claude Code loads the server as a plugin; any other client runs it from the command below.

The plugin lives in the repository at `mcp/gridsim-mcp/`.

## Installing the server

### As a Claude Code plugin

For one session, start Claude Code with the plugin folder:

```
claude --plugin-dir mcp/gridsim-mcp
```

To keep it installed, add the folder that holds the marketplace file (`mcp/`, which carries
`mcp/.claude-plugin/marketplace.json`) and install the plugin from it. In Claude Code:

```
/plugin marketplace add <path to the mcp folder>
/plugin install gridsim@gridsim-dev
```

Then type `/mcp`: the `gridsim` server is listed as connected, with its tools.

### In any other MCP client

Register a stdio server with this command and arguments:

```
uv run --quiet --script <plugin folder>/server/main.py
```

and set the options below as environment variables of that server. The server takes no command-line flags.

## Configuring GridSim, the licence and the workspace

| Plugin option | Environment variable | What it is |
|---|---|---|
| `gridsim_cli` | `GRIDSIM_MCP_CLI` | The full path of your `gridsim` program. Empty: `GRIDSIM_CLI`, then `gridsim` on the PATH. |
| `licence_file` | `GRIDSIM_MCP_LICENCE_FILE` | Your GridSim licence file, needed only for cases over 1,500 buses ([ch. 4](<0004 Licensing and activation.md>)). |
| `workspace` | `GRIDSIM_MCP_WORKSPACE` | The folder for cases, results and study records. Empty: `gridsim-workspace` in the folder the AI tool was started in. |
| `enable_compare` | `GRIDSIM_MCP_ENABLE_COMPARE` | Turns on the validation tools (below). Off by default. |
| `harness_path` | `GRIDSIM_MCP_HARNESS` | The Lab Mode harness folder (the one holding `run.py`). Used only with `enable_compare`. |
| `enable_caselab` | `GRIDSIM_MCP_ENABLE_CASELAB` | Turns on the case lab tools (below). Needs `enable_compare` and `harness_path` as well. Off by default. |
| `enable_rebuild` | `GRIDSIM_MCP_ENABLE_REBUILD` | Turns on `rebuild_on_demand` ([Tuning](#tuning)). Off by default. |
| `rebuild_dir` | `GRIDSIM_MCP_REBUILD_DIR` | The folder for rebuild copies, outside any GridSim checkout. Empty: `.gridsim-mcp/rebuild/` in the workspace. |

Claude Code asks for the plugin options when the plugin is enabled. The licence file is passed to GridSim as `GRIDSIM_LICENCE_FILE`; the server never opens it, prints it or records it,
and the study record says only whether a licence was configured.

The server reads case files in the workspace and in the folder the AI tool was started in, and writes only inside the
workspace:

| Folder | Holds |
|---|---|
| `cases/` | Seed cases from `case_new` and the cases you save. |
| `imports/<name>/` | Models brought in with `import_model`, one folder per import. |
| `records/` | Exported study records. |
| `.gridsim-mcp/cases/` | JSON v2 copies of edited cases, one folder per case state, for the verbs that read a file. |
| `.gridsim-mcp/results/` | Every result document, named by its hash. |
| `.gridsim-mcp/exports/` | Solve exports written by `explain_solve` and `break_it`, and MATPOWER copies of edited cases for them. |
| `.gridsim-mcp/compare/`, `crossval/`, `regression/` | Lab bundles from `compare`, sidecar outputs from `crossval_single` and reports from `build_regression` (validation tools only). |
| `.gridsim-mcp/findings/` | `findings.jsonl`, the private findings store `divergence_triage` appends to. |
| `caselab/<campaign>/` | One folder per case lab campaign: its cases, Lab bundles, reproducers, `findings.jsonl` and `summary.md` (case lab only). |
| `caselab/proposed/<id>/` | Cases an AI designed through `caselab_propose`, with their gate results (case lab only). |
| `.gridsim-mcp/rebuild/` | One folder per `rebuild_on_demand` build copy (rebuild tool only, unless `rebuild_dir` names another folder). |
| `.gridsim-mcp/journal/` | GridSim's own run journal for each session ([technical ch. 42](<../technical/0042 Run journal and breaking-change validation.md>)). It holds file paths from your machine; review it before you share it. |

Ask the AI to run `preflight` first. It reports the GridSim version, the program's SHA-256, the build status, the
compliance attestation, the CPU vector tier, and a physics check and power flow of case9 with their result hashes.

## The tools

Every tool also takes two optional arguments: `session` (default `default`) and `reasoning`, a short sentence saying
why the call is made, which is kept in the study record.

### Sessions and cases

| Tool | What it does |
|---|---|
| `session_open`, `session_close` | One resident GridSim host per session (`gridsim rpc`, over a pipe), with its own cases, results and record. Other tools open the session on first use. |
| `case_new` | Starts a network from scratch: a one-bus case with a slack bus and its generator, written to `cases/<name>.m` and loaded. |
| `case_load` | Loads a built-in case (`case9`, `case14`, `case118`, ...) or a file: MATPOWER, PSS/E RAW, a JSON case folder or a v2 manifest ([ch. 2](<0002 Loading networks.md>)). |
| `case_inspect` | Element counts; every element of a kind with its attributes; one element, including its solved flows. |
| `case_edit` | `set`, `add` or `delete` a bus, generator, load, branch or shunt. GridSim checks every edit first; a refused edit changes nothing. |
| `case_history` | The edit log: revision, element, attribute, before and after. |
| `case_save` | Writes the case as it stands as JSON v2 or PSS/E RAW 33 to 36 or RAWX. A file with other content is never overwritten. |
| `case_validate` | `gridsim validate`: the v2 schema and GridSim's semantic checks. |

Cases are named `c1`, `c2`, ... in the order a session loads them, and results `r1`, `r2`, ....

### Solving and studying

| Tool | What it does |
|---|---|
| `solve_pf` | The AC power flow of the case as it stands. By default the resident solve (convergence, iterations, mismatch, losses, every bus); with `full` the complete `gridsim pf` result document, the same bits as the command line. |
| `study` | One of `pf`, `nminus1`, `nminus2`, `iec60909`, `fault`, `acopf-full`, `dcopf`, `scopf`, `stability` on the case as it stands, edits included. |
| `run_verb` | Any verb on the allowlist, as `gridsim <verb> <args>` on the command line ([ch. 19](<0019 CLI reference.md>)). |
| `tunables_list` | The values you may tune, with their class, flag, default and safe range ([Tuning](#tuning)). `solve_pf`, `study`, `run_verb` and `reproduce_check` take them as `tuning`. |

### Checking and comparing

| Tool | What it does |
|---|---|
| `physics_check` | `gridsim physics-check` on the case as it stands: energy balance, Ohm's law, Kirchhoff's current law, complex power and the rest of the law registry ([ch. 27](<0027 Validation tools.md>)). |
| `diff_cases` | `gridsim case-diff` between two cases (a case id or a file): every changed, added, removed or reordered record, with units. |
| `diff_results` | `gridsim result-diff` between two results: every number, by ULP distance and absolute difference. |
| `reproduce_check` | The same study run two to five times, each in a fresh GridSim process: MATCH when every run gives the same bits. Takes `tuning` too. |
| `record_export`, `record_replay` | Writes the study record; replays it and compares every hash. |
| `preflight` | The build check described above. |

### Planning

| Tool | What it does |
|---|---|
| `import_model` | Brings a model into `imports/<name>/`: MATPOWER, PSS/E RAW, RAWX, UCTE-DEF, CGMES and JSON cases (and built-in names) through `gridsim convert`, which proves the round trip; PowerFactory DGS through `gridsim dgs-import`; IEC 61850 SCL through `gridsim scl-import` with a params file. Returns every `note:` line (what the target cannot hold, or what was dropped) and the round-trip statement, and loads the case. |
| `line_parameters` | R, X and B from conductor geometry with `gridsim line-constants` (overhead lines over rpc, single-core cables from a file), in ohms per km and, with a length and bases, in per unit. |
| `study_case_run` | `gridsim study-case` or `gridsim planning-study` into a new folder; returns the run manifest with each job's result SHA-256. |
| `report_render` | `gridsim report` of a result: Markdown, HTML or CSV, bound to the result by its SHA-256. With no template, the default template is written first. |

### Learning

| Tool | What it does |
|---|---|
| `explain_solve` | Re-solves the case with GridSim's solve export and summarises it: iterations, how the mismatch falls and the estimated order of convergence, PV to PQ switches, the bus with the largest mismatch, bus types, the physics gate; the Jacobian for cases of up to 300 buses. A solve that does not converge returns GridSim's message and the matching Troubleshooting entry ([ch. 30](<0030 Troubleshooting.md>)). |
| `break_it` | A guided failure on a copy of the case: trip a branch or generator, overload every load, remove the slack, island a bus, or apply a fault. Returns what happened, GridSim's own message (a refused edit, a solve that does not converge) and the Troubleshooting entry that explains it. The case itself is not changed. |

### Validation (maintainer tools)

The maintainer tools (validation, the case lab and rebuilding with changed constants) are in the development tree
only. The published plugin does not carry them, their options or their skill.

These four tools exist only when `enable_compare` is on and `harness_path` names the Lab Mode harness, with its rival
runtimes installed (pandapower, MATPOWER under Octave, PowerModels under Julia). Otherwise they are not offered at all.

| Tool | What it does |
|---|---|
| `compare` | One correctness-only Lab Mode cell: GridSim and every rival tool that supports the mode, each judged first by its own physics gate, then the cross-check and an element diff between every pair of passing tools (per bus dVm and dVa; per branch dP and dQ; per bus dPg for dcopf; each the largest absolute difference and where). Takes a case id, a MATPOWER case name or a `.m` file. The harness's `lab serve` daemon keeps the rival workers warm between calls. |
| `crossval_single` | One of GridSim's cross-validation sidecars on a case: `crossval` (against pandapower), `residual_check`, `physics_audit`, `crossval_smallsignal` or `crossval_classical`. Exit 77 means not run: a dependency is missing and nothing was validated. Machine paths in its output are shown as placeholders (`<python>`, `<gridsim>`, `<workspace>`, `<home>`, `<path>`). |
| `divergence_triage` | Sorts each tool of a compare bundle into GridSim gate fail, rival gate fail, documented model difference, genuine disagreement or agreement, with each side's independent evidence (its gate, GridSim's physics-check, and the residual check and physics audit of both answers). A disagreement is evidence, not proof. Writes to the private findings store. |
| `build_regression` | `gridsim breaking-change validate` over the session's run journal, on the configured build or another one ([technical ch. 42](<../technical/0042 Run journal and breaking-change validation.md>)). |

The `gridsim-validate` skill carries the protocol: a disagreement is evidence, not proof; no change to the solver
core or to determinism is proposed without the maintainer; a test change needs a mathematical proof; findings stay
in the workspace's private store. Cases above 1,500 buses need the licence configured, as everywhere.

### Case lab (maintainer tools)

The case lab runs the validation protocol on cases it builds itself. It generates small, plausible networks from a
seed, gates each one in GridSim, compares it with the rival tools mode by mode, triages every result that is not an
agreement, and shrinks each new finding to the smallest case that still shows it. The five tools exist only when
`enable_caselab` is on as well as the validation tools.

| Tool | What it does |
|---|---|
| `caselab_run` | A campaign: `n` generated cases from `seed` (families `radial`, `meshed`, `transmission`, taken in turn) plus any proposed cases, run in the modes asked for (default `pf`, `pfq`, `dcpf`, `dcopf`; `acopf` and `nminus1` when named). Stops cleanly at `budget_minutes`. With `wait` false it runs in the background. |
| `caselab_status` | Every campaign in the workspace, or one campaign's parameters, build, progress, counts by class and generator rejects. |
| `caselab_findings` | A campaign's findings, filtered by class, mode or tool; one finding in full with both sides' evidence and its reproducer's hashes. |
| `caselab_reproduce` | Rebuilds a finding's reproducer from its recipe, checks every file hash against the recorded one and re-runs its Lab cell: REPRODUCED or DIFFERS, with what differed. |
| `caselab_propose` | Without a case: a template, the class ranges and instructions for an AI to design a case itself. With a case: checks it, writes it and runs the same gates as a generated case. The server calls no model. |

What a campaign does, in order:

1. **Generate.** Case *i* comes from its family and a seed derived from the campaign seed and *i*, so the same seed
   gives byte-identical cases, whatever `n` is. The families are an 11 kV feeder behind a 33/11 kV transformer (OHL
   and cable sections, an optional embedded generator, capacitor and 0.4 kV tail), a small meshed 33 kV network fed
   from 132 kV through two or three grid transformers, and an IEEE-like 5-14 bus network at 132 or 275 kV, sometimes
   15-30 buses. Values are drawn from ranged typical values (per-km line and cable constants by voltage class,
   transformer impedance and X/R by rating, demand power factor, tap steps of 0.0125 pu) and every case is tagged
   Synthetic.
2. **Gate.** Each case is written as MATPOWER, converted to JSON v2 with `gridsim convert`, validated, converted back
   to the MATPOWER file the Lab runs, physics-checked and solved. A case that fails a gate, or whose bus voltages lie
   outside 0.90-1.10 pu, is a generator reject with its reason. It is never adjusted to pass.
3. **Compare and triage.** One correctness-only Lab cell per case and mode, through the Lab daemon, all cases of one
   mode before the next so the rival workers stay warm. Each rival tool is triaged into the classes of
   `divergence_triage`. Findings are deduplicated by class, mode, tool and signature (the documented difference, the
   gate reason without its numbers, or the quantities that exceeded the thresholds). Agreements are counted.
4. **Refine.** For each new finding the lab tries a few seeded perturbations around it (scale the load, move a tap
   one step, tighten a Q limit, remove a loop branch) and records which still show it. It then shrinks the case
   greedily: merge a leaf bus into its neighbour, drop a generator, a loop branch or a shunt, set a tap to nominal,
   keeping each step that still shows the same finding with both tools in the same status, until no single step
   does. Each candidate passes the same
   gates first.
5. **Record.** `findings.jsonl` holds one line per finding: its class, mode, tools, the element-diff maxima, the gate
   metrics, both sides' evidence, the first case and every occurrence, the reproducer (its recipe, size and file
   hashes) and a proposal. The proposal is a sentence, never a code change: a documented model difference proposes
   nothing, and a GridSim gate fail or a genuine disagreement is a question for the maintainer. `summary.md` says
   the same for a reader.

Two campaigns with the same parameters on the same build and tools give the same case files, the same counts and the
same `findings.jsonl`. Their Lab bundles differ only in timing and environment fields. A finding is evidence, not
proof, and the cases are Synthetic: GridSim's solver is verified, the generated cases are not validated.

### Skills, prompts and resources

The plugin carries skills that tell the AI how to use the tools: `gridsim-study` (the planner workflow), `gridsim-teach`
(lessons that build a network, then break it, explaining each step), `gridsim-validate` (the divergence protocol of
the validation tools and the case lab) and `gridsim-construct` (a short stub: its full content comes in a later phase). Each one asks for the build tag, case hash, result hash and weakest provenance tag with every claim,
says "verified", never "validated", of a result, and does not work around a refusal. The MCP prompts `lesson_per_unit`,
`lesson_pv_pq`, `lesson_reactive_limits`, `lesson_n_minus_1`, `lesson_fault_level`, `lesson_inertia_rocof` and
`study_writeup` start a lesson or a write-up. The resources include the starter-case list with bus counts
(`gridsim://plugin/starter-cases`), the Troubleshooting index, the per-unit conventions, the grounded parameter values
and the dynamic-model list (`gridsim://docs/...`). These sections ship with the plugin, so they work with a release
install; sections about services the plugin does not offer are left out.

### Examples

Build a case from scratch and solve it. In plain words to the AI: "Start a 132 kV case called three_bus, add buses 2
and 3, lines 1-2, 1-3 and 2-3 with r 0.01, x 0.08 and b 0.02 per unit, 50 MW and 20 MVAr at bus 2, 30 MW and 10 MVAr at
bus 3, and solve it." The calls it makes:

```
case_new    name=three_bus base_kv=132
case_edit   case_id=c1 op=add kind=bus key=2 values={base_kv: 132}
case_edit   case_id=c1 op=add kind=bus key=3 values={base_kv: 132}
case_edit   case_id=c1 op=add kind=branch key=1-2:1 values={r: 0.01, x: 0.08, b: 0.02}
case_edit   case_id=c1 op=add kind=branch key=1-3:1 values={r: 0.01, x: 0.08, b: 0.02}
case_edit   case_id=c1 op=add kind=branch key=2-3:1 values={r: 0.01, x: 0.08, b: 0.02}
case_edit   case_id=c1 op=add kind=load key=2 values={pd: 50, qd: 20}
case_edit   case_id=c1 op=add kind=load key=3 values={pd: 30, qd: 10}
solve_pf    case_id=c1
```

The power flow converges in 3 iterations with 0.374 MW of losses, and the answer carries the result hash and case
hash of that exact state. Load and shunt keys are bus ids, a branch key is `from-to:circuit` and needs `x`, a bus
needs `base_kv`, and a generator key is `bus:id`. The attribute names are the object model's
([technical ch. 41](<../technical/0041 Scripting object model and API.md>)); a wrong name is refused by GridSim with
the reason.

Further tasks, each one call:

- **N-1**: `study case_id=c1 verb=nminus1`. Exit 3 is a verdict (NOT SECURE) and still a result.
- **Physics check**: `physics_check case_id=c1`. Run it after every build or edit; a FAIL names the law broken and
  its residual.
- **Diff an edit**: `case_save case_id=c1 path=cases/base.json`, then the edit, then
  `diff_cases a=cases/base.json b=c1`. To see what the edit moved in the solution, solve with `full` before and after
  and `diff_results` the two result ids.
- **Reproduce**: `reproduce_check case_id=c1 verb=pf runs=3` answers MATCH or DIFFER.

## Reading an answer

Every answer has two parts. `document` is GridSim's own JSON, exactly as GridSim returned it: for a study, the
`gridsim-cli-result/1` document the command line writes. `evidence` is added by the server:

| Field | Meaning |
|---|---|
| `build`, `build_tag` | GridSim's version, the program's SHA-256 and the build status, `[RELEASE]` or `[UNSUPPORTED BUILD]`. |
| `case`, `case_hash` | The case id and the SHA-256 of the case as it stands: the hash of the file loaded plus every edit since. |
| `result`, `result_hash` | The result id and the SHA-256 of `document`. |
| `record_seq`, `record_hash` | This call's entry in the study record. |
| `tuned`, `tuning`, `off_standard` | Only on a tuned run: the tuned values, and for a standard value the standard it no longer complies with ([Tuning](#tuning)). The build tag then ends in `TUNED`. |
| `note` | The standing statement: GridSim's solver is verified; a case built or edited here, and a study on it, is not validated against measurements. |

A number moved only if its result hash changed. Ask the AI to cite the build tag, case hash and result hash with any
claim it makes, and to say "verified solver, unvalidated case". The resource `gridsim://plugin/evidence-rules` gives
the AI these rules.

## The study record and replay

Each session keeps a study record, `gridsim-mcp-record/1`: one JSON line per tool call, with the tool, its arguments,
the reasoning the AI gave, the outcome, the case hash, the result hash and the build. Each line carries the hash of the
line before it, so changing, removing or reordering any line breaks the chain from that line on.

`record_export` writes the record to `records/<name>.jsonl`. `record_replay` first checks the chain (a broken chain is
reported as TAMPERED, naming the first bad entry), then re-runs every call in a fresh session and compares each hash.
REPRODUCED means every call gave the same case, the same result and the same record entry. No clock time, session name
or host process detail is hashed, so a record replays on the same build to the same hashes. A record is a complete
account of what was asked and what GridSim answered; it is the thing to keep, share or attach to a study.

## Tuning

Physics is physics: the laws, the constants and the per-unit definitions are fixed. What you may change is how
GridSim reaches the answer and, knowingly, a value that a standard sets. `tunables_list` names every such value, and
the resource `gridsim://plugin/tunables` holds the same list. Each value is one of GridSim's own command-line options,
so a tuned run gives exactly what the command line gives with that flag.

| Class | Meaning |
|---|---|
| PHYSICS | Fixed. Never tunable, at run time or by a rebuild. |
| STANDARD | Tunable, but the default comes from a standard. A result made with another value says `off-standard: <standard> no longer complied with`. |
| NUMERICS | Free within the stated safe range. A converged answer meets the same convergence test as the default one, so it agrees with it to within that test, but it is not the same bits. |

The values on the list:

| Key | Class | Flag | Default |
|---|---|---|---|
| `pf.engine` | NUMERICS | `--engine` | `newton` (also `fdxb`, `fdbx`, `gs`) |
| `pf.gs_accel` | NUMERICS | `--gs-accel` | 1.0; above 0 and below 2, with `pf.engine` `gs` |
| `pf.max_iter` | NUMERICS | `--max-iter` | 100 (10000 for `gs`) |
| `pf.max_q_passes` | NUMERICS | `--max-q-passes` | 100 |
| `pf.q_revert` | NUMERICS | `--q-revert` | false |
| `pf.diverge_limit` | NUMERICS | `--diverge-limit` | 10 pu, or `off` |
| `nminus1.batched_lanes` | NUMERICS | `--batched-lanes` | not used (bit-identical when used) |
| `cpf.step`, `cpf.min_step`, `cpf.max_steps` | NUMERICS | `--step`, `--min-step`, `--max-steps` | 0.25, 0.001, 200 |
| `acopf-full.max_iter` | NUMERICS | `--max-iter` | 150 |
| `scopf.max_rounds`, `scopf.full_lp` | NUMERICS | `--max-rounds`, `--full-lp` | 50, false |
| `restore.vmin`, `restore.vmax` | STANDARD | `--vmin`, `--vmax` | 0.90, 1.10 pu (the statutory band) |
| `protection-check.uv`, `protection-check.ov` | STANDARD | `--uv`, `--ov` | 0.80, 1.10 pu (ENA EREC G99 Issue 2 Table 10.1) |
| `protection-check.uf`, `protection-check.of` | STANDARD | `--uf`, `--of` | 47.5, 52.0 Hz (G99, 50 Hz), given together |
| `harmonics.synth_spectrum` | STANDARD | `--synth-spectrum` | `g5` (ENA EREC G5 Issue 5 Table B.3), or `ideal` |

To tune, pass the keys as `tuning`: `solve_pf case_id=c1 tuning={pf.engine: fdxb}`, or `run_verb verb=protection-check
case_id=c1 tuning={protection-check.uv: 0.85}`. The map is checked in full before anything runs. An unknown key, a key
of another verb, a value of the wrong type or outside its safe range, or a missing companion key (`pf.gs_accel` needs
`pf.engine` `gs`) is refused, and nothing runs. A flag on the list is not accepted in the plain `args` of its verb:
pass it as `tuning`, so the result is always marked.

A tuned result is your own. Its build tag is the build's tag followed by `TUNED` (`[UNSUPPORTED BUILD] TUNED`, or
`[RELEASE] TUNED` on a release), its evidence carries `tuned: <key=value, ...>`, and its study-record entry carries the
same. A value equal to the default does not mark the result. `reproduce_check` and `record_replay` run with the same
tuning, so a tuned study reproduces and replays to the same hashes like any other.

The settings that protect GridSim's use limits cannot be tuned: they are not on the list, and a tuning key or value
that names one is refused whatever the list says.

### Rebuilding with changed constants

A few values exist only in GridSim's source code. With `enable_rebuild` on, `rebuild_on_demand` makes your own build
with one or more of them changed. The list is short on purpose: NUMERICS constants only, never physics.

| Symbol | Default | Safe range |
|---|---|---|
| `PowerFlowOptions.DefaultTolerance` | 1e-8 pu | 1e-12 to 1e-6 pu |
| `CliDefaults.RmsStepS` | 0.005 s | 0.0005 to 0.01 s |
| `CliDefaults.ClassicalStepS` | 0.002 s | 0.0002 to 0.01 s |

Give it a GridSim source checkout and the edits, for example `rebuild_on_demand source=<checkout>
edits=[{symbol: CliDefaults.ClassicalStepS, value: 0.001}]`. It never changes the checkout. It copies the files a CLI
build needs into a new folder under `rebuild_dir`, outside the checkout, applies the edits there, and then proves the
copy: every guard file (the files that enforce GridSim's use limits, its licence and its build stamp, and the build
logic itself) is byte-identical to the checkout and unmodified against the checkout's own commit, every other file is
byte-identical, and each edited file differs only at the constant. Any difference, an edit outside the list or a value
outside its range is refused, and nothing is built. `dry_run` (the default) stops there. With `dry_run=false` it runs
`dotnet build src/GridSim.Cli -c Release` in the copy and checks that the new program reports UNSUPPORTED BUILD. A
cold build takes several minutes. Point `gridsim_cli` at the new program to use it: every answer it gives is
`[UNSUPPORTED BUILD]`.

## RELEASE and UNSUPPORTED BUILD

Every answer is tagged with the status of the GridSim build that produced it. GridSim reports this itself: its result
files carry `buildStatus` and its rpc `hello` carries `build_status` ([ch. 19](<0019 CLI reference.md>)). The MCP
server reads GridSim's own report and checks it independently:

- **`[RELEASE]`**: GridSim reports RELEASE (a release build made by the release workflow from a clean checkout, naming
  its full commit), that commit is in the plugin's list of published release commits, and the program's SHA-256 is
  in the plugin's list of published release binaries (the SHA256SUMS of the releases).
- **`[UNSUPPORTED BUILD]`**: anything else, such as a build from source, a modified build or an unknown program. When
  GridSim's report and the server's check disagree, the answer is UNSUPPORTED BUILD and `preflight` says which side
  said what.

The compliance verdict of `gridsim compliance verify` is reported by `preflight` beside the tag; it does not decide the
tag. `preflight` shows both lookups: `commit_listed` and `program_listed`.

Only RELEASE builds that match the published release are supported. An unsupported build runs and answers in the same
way, and its results are as deterministic as any build's, but questions and bug reports about its results are not
supported. When you report a result, include the evidence block; the build tag and SHA-256 say which build it came from.

## What is refused

The server runs a fixed allowlist of GridSim verbs: the static studies that take a case and give a result. Anything
not on the list is not available through the MCP server, and the answer names what was refused:

- verbs outside the allowlist, including those that take recorded, live or time-stamped inputs, start a resident
  service, handle the licence, or belong to benchmarking and corpus-wide validation;
- flags that attach measurements or live inputs, open a network endpoint, load plug-in code or change the time horizon;
- a verb with no case named, when that verb would otherwise fall back to a default case;
- the shipped GB cases with any verb other than the static ones (power flow, contingency, short circuit, physics
  check, conversions and comparisons);
- files outside the workspace and the project folder, and network paths.

The full list, with the reason for each entry, is the resource `gridsim://plugin/allowlist`. Everything refused is
still available from the GridSim command line itself.

## Offline and no telemetry

The server talks to the AI tool through standard input and output only. It opens no socket, listens on no port and
sends no usage data, crash reports or analytics; the plugin declares no network hosts. The only download is the MCP
SDK, fetched by uv at the locked versions when the server first starts; after that, uv starts it from its cache. The
SDK's OpenTelemetry hook, inert without an exporter, is removed at start-up, and `OTEL_*` settings are cleared for
both the server and GridSim. The test suite runs the whole server under a guard that fails any network call.

What the AI tool does with the answers is governed by that tool: a hosted AI service sends tool results to its model
provider. Load only data you may share with it. The plugin's `PRIVACY.md` states the same.

## Pitfalls

- **"GridSim was not found"**: set `gridsim_cli` to the full path of the program, or put `gridsim` on the PATH.
- **The server does not start**: check `uv --version` in a new shell. The first start needs the package index once.
- **"lies outside the workspace"**: copy the file into the workspace, or start the AI tool in the folder that holds it.
- **A study after an edit is refused, naming a bus with no base voltage**: some cases (case14 among them) carry buses
  with no base kV, which a JSON v2 copy cannot hold. `solve_pf` still works; for other studies, save the case as RAW
  and run the verb on the saved file with `run_verb`.
- **"name the case explicitly"**: pass the case as the first argument of `run_verb`, or give `case_id`.
- **A refused edit**: GridSim's message names the attribute or rule; nothing was changed.
- **REPRODUCED expected but DIFFERS**: a record replays to the same hashes only on the same build. Compare the `build`
  of the header with `preflight`.
- **Cases over 1,500 buses fail with a licence message**: set `licence_file`.
- **"pass it as tuning"**: a flag on the tunables list was given in `args`; move it into `tuning`.

## Limits

- The server does not include GridSim; a GridSim installation is required.
- A record replays to identical hashes on the build that made it; on another build it reports DIFFERS even where the
  numbers agree. Use `diff_results` to see what moved.
- The build status is tagged RELEASE only for builds listed in the plugin's release list, which grows as releases are
  published.
- Tuning covers the options GridSim already has. The solver profile kind is not read from a case yet, so the power-flow
  tolerance can be changed only by a rebuild.
- Further tools are planned.

## See also

- [Technical ch. 47, MCP server design](<../technical/0047 MCP server design.md>)
- [Technical ch. 41, Scripting object model and API](<../technical/0041 Scripting object model and API.md>)
- [Determinism and reproducibility](<0036 Determinism and reproducibility.md>)
- [Validation tools](<0027 Validation tools.md>)
- [CLI reference: rpc](<0019 CLI reference.md#rpc>)
