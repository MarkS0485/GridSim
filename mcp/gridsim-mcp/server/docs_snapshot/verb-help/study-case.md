### study-case

`gridsim study-case <study-case.json> --out <dir> [--parallel 1] [--max-jobs N]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its output folder, not a shared-writer file.

Study cases (RC4 R4-01): one immutable master case, dated variations, named operation scenarios, grouping objects and
studies, expanded deterministically and run as one ordered batch. It builds on the `planning-study` machinery
(`gridsim-planning/1`, unchanged): variations are planning projects with an expansion-stage label, scenarios are expanded
to explicit per-element operations in the same vocabulary, and dated application, expiry, conflict refusal and the
topology and rating checks are those of `planning-study`. Groups are held in a sidecar document bound to the case, never
in the case itself.

The study case is strict JSON, `manifestVersion` `"gridsim-studycase/1"`. Unknown or duplicated fields, non-finite numbers
and wrong types are refused. Its members are:

- `master`: `case` (`path` and `sha256`) and `interpretation`, exactly as for `planning-study`.
- `groups` (optional): `path` (relative to the study case) and `sha256`, the SHA-256 of the groups file's bytes.
- `instants`: each an `id` and an `instant` (ISO 8601 with an explicit offset, as for `planning-study`).
- `variations`: each an `id`, an optional `expansionStage` label and ordered `operations`, written as the operations of a
  `planning-study` project (`target`, `field`, `expectedPrior`, `value`, `effectiveFrom`, optional `effectiveTo`).
  `variationOrder` lists variations whose operations may change one field at one instant; two unlisted variations that
  both do are refused as a conflict, naming both operations.
- `scenarios`: each an `id`, optional `instants` (the instant IDs it runs at; default all) and ordered `operations`. A
  scenario operation has an `id`, an optional `group` (default: every element of the kind in the case), a `field`, an
  `action` (`scale` or `set`) and a `value`. Fields: `load` (demand, MW and MVAr together; scale only), `generation` (the
  active-power set point; the same as `pgMw`), `pdMw`, `qdMvar`, `pgMw`, `qgMvar`, and `inService` (availability of the
  group's branches; set only, `true` or `false`). A scale multiplies the value the element holds once the variations
  active at the instant are applied; the new value is computed once in IEEE double, groups in file order and elements in
  model order. A scenario always applies after the variations. The name `base` is reserved for a study that names no
  scenario.
- `studies`: each an `id`, an optional `scenario`, a `mode` and `parameters`. The modes of `planning-study`
  (`transmission-security`, `transfer`, `corrective-n-1-1`) take the same parameters. Mode `group-summary` takes
  optional `groups` (group IDs; default all) and `solve` (default false) and reports per group the load, the scheduled
  generation and their balance; with `solve` the AC power flow is run and the solved net injection, the tie flow out of
  the group, the internal loss and the shunt loss are added (`netInjection = tieFlow + internalLoss + shuntLoss`).

The groups document is `gridsim-groups/1`: `case.sha256` (the master bundle digest) and `groups`, each an `id`, a `kind`
(`area`, `zone`, `owner`, `feeder` or `free`), and any of `buses` (bus IDs), `generators` (`index` into the whole fleet
and the expected `bus`) and `branches` (`index` and the expected `from` and `to`; parallel circuits stay distinct). A
feeder is ordered and has a `head` (`bus`, `branch` or `generator`, one of its members). Area, zone and owner are
exclusive: an element in two groups of one of those kinds is refused. An unresolved or repeated member, an empty group or
a case digest that does not match is refused before any study runs.

Jobs run scenario by scenario (`base` first when used), then by instant, then by study, each in declared order. For each
(scenario, instant) the case is resolved in three steps: the variations alone give the case the scenario scales, the
scenario is expanded to explicit element operations, and variations plus scenario are applied through the
`planning-study` expansion. Any refusal stops the run before a job starts and writes nothing.

Outputs, in `--out` (a new folder, an empty one, or an earlier study-case run folder, recognised by its marker file):
`jobs/<NNN>-<scenario>.<instant>--<study>/result.json` per job (as for `planning-study`, the audit of every operation
including the scenario's), `summary.txt`, `derivation.json` (every operation of every resolved case: variations with
their expansion stage, and each scenario's element-level changes with before and after values, with the dispositions
`Applied`, `NotYetEffective` and `Expired`), `run-manifest.json` (`gridsim-studycase-result/1`: every job in order with
its state, verdict, result path and SHA-256, the master and groups identities, and the case and derivation digests of each
resolved case) and `evidence.json` (elapsed times and reuse decisions only; the other files are byte-identical between
runs). Reuse of an earlier job result follows the `planning-study` rules.

Exit 0 when every job completed with its mode's passing status (`SECURE`, `VERIFIED`, or `SUMMARISED` for
`group-summary`). Exit 3 when the run completed with any other verdict, an `INVALID CASE` or a `FAILED` job, or left jobs
unattempted. Exit 1 for input it cannot accept (malformed document, digest mismatch, unresolvable target or group member,
conflict, an unusable output folder). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | required | Study-case run folder. |
| `--parallel` | 1 | Jobs run at once (1-64). Results and their order do not depend on it. |
| `--max-jobs` | all | Attempt at most this many jobs, in declared order; the rest are recorded as not attempted. |
