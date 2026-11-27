### planning-study

`gridsim planning-study <manifest.json> --out <dir> [--parallel 1] [--max-jobs N]`

Versioned planning snapshots and repeatable studies (RC3). One immutable master case, dated changes authored as
ordered operations, and named snapshot instants are expanded deterministically into resolved cases; each snapshot is
then run through each study in the manifest, and every job writes its own result into its own folder under one ordered
result manifest.

The manifest is strict JSON, `manifestVersion` `"gridsim-planning/1"`. Unknown or duplicated fields, non-finite
numbers and wrong types are refused. Its separate objects are:

- `master`: `case` (`path` to a native JSON case directory or v2 case manifest, relative to the manifest file, and
  `sha256`, the SHA-256 of the UTF-8 consumed-file lines (path, TAB, lowercase file SHA-256, LF) in load order, the
  same value `transmission-security` takes for that case) and `interpretation` (`loader` `"native-json"`, `dispatch`
  `false`). Loader balancing is never run: only declared operations change the model. The master identity
  (`masterSha256`) binds the bundle digest to the interpretation.
- `projects`: each an `id` and ordered `operations`. An operation has an `id`, a `target`, a `field`, an
  `expectedPrior` and a replacement `value`, an `effectiveFrom` and an optional `effectiveTo`. Targets: a branch
  (`element` `branch`, zero-based `index`, expected `from` and `to` bus IDs), a generator (`generator`, zero-based
  `index` in the whole fleet including out-of-service units, expected `bus`) or a load (`load`, `bus`: the bus's
  aggregate constant-power demand); an optional `name` is an identity check. Fields: `inService` (branch, true or
  false), `rateAMva` (normal rating, MVA), `rateBMva` and `rateCMva` (emergency ratings, MVA; 0 means not given),
  `pgMw` and `qgMvar` (unit setpoints, within the unit's range), `pdMw` and `qdMvar` (bus demand).
- `projectOrder`: the explicit order of projects. Two projects active at one instant that change the same field are
  refused unless both are listed here; the earlier listed applies first.
- `snapshots`: each an `id` and an `instant`.
- `studies`: each an `id`, a `mode` and its `parameters`, which are the fields of that verb's own study file without
  `schemaVersion` and `case`, bound to the master. Mode `transmission-security` takes the `transmission-security`
  fields (`outages`, `voltage`, `postFaultRating`, optional `dc`, `costs`); mode `transfer` the `transfer-capability`
  fields (`sources`, `sinks`, `outages`, `voltage`, `postFaultRating`, `search`, optional `interfaces`); mode
  `corrective-n-1-1` the `corrective-sequence` fields (`voltage`, `ratings`, `policy`, `sequences`). Study unit
  indices follow those verbs (the in-service unit list); operation generator indices count the whole fleet.

Times are ISO 8601 with an explicit offset (`Z` or `+hh:mm`) and up to seven fractional digits; a local time without
an offset is refused as ambiguous. Every instant is normalised to UTC. An operation is active at an instant when
`effectiveFrom <= instant < effectiveTo` (from inclusive, to exclusive; no `effectiveTo` is open-ended), so a change
ends by expiring and the master value returns.

At each snapshot the active operations are applied to a copy of the master, projects in `projectOrder` then unlisted
projects in declaration order, operations in authoring order. Each `expectedPrior` must equal exactly the value it
replaces; a change shadowed by an earlier one is refused naming both operations. Any refusal stops the run before a
job starts. Each resolved case then has its topology (one connected network) and ratings checked; a case that fails is
`INVALID CASE` and is never given to a study. A requested outage of a branch that is out of service in the snapshot
(a planned outage) keeps an explicit `NOT APPLICABLE` row and is not studied; a corrective sequence needing such a
branch is likewise `NOT APPLICABLE`. `transmission-security` replaces unit P with its own DC candidate, so an authored
`pgMw` changes the resolved case and its identity but not that study's candidate; `transfer` and `corrective-n-1-1`
start from the scheduled unit P, so it moves their reference point.

Outputs, in `--out` (a new folder, an empty one, or an earlier planning run folder, recognised by its marker file):
`jobs/<NNN>-<snapshot>--<study>/result.json` per job (the audit of every operation with its original, prior and
effective value, the validation, the study parameters and the study's unaltered result, and the case identities),
`summary.txt`, `run-manifest.json` (every job in order with its state, verdict, result path and SHA-256, the master
input files and digests, `caseSha256` content identity and `derivationSha256` per snapshot) and `evidence.json`
(elapsed times and reuse decisions only; the other files are byte-identical between runs). The run manifest is written
last and the previous one is removed first, so an interrupted run never leaves an older manifest looking current. On a
rerun a job's earlier result is reused only when its completion record matches this build, the job's request and input
fingerprints, and the file's recorded length and SHA-256; otherwise the earlier files are deleted before the job runs,
so a failed job is never represented by an older result.

Exit 0 when every job completed with its study's passing status (`SECURE`; `VERIFIED` for `transfer`). Exit 3 when
the run completed with any other verdict (`NOT SECURE`, `QUALIFIED`, `INCOMPLETE`, `UNVERIFIED`), an `INVALID CASE` or
a `FAILED` job (the engine refused the request, with its message), or when jobs were left unattempted (`--max-jobs` or Ctrl+C: run state `CANCELLED`, with each completed job
kept). Exit 1 for input it cannot accept (malformed manifest, digest mismatch, unresolvable target, conflict, an
unusable output folder) or when a result could not be written. Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | required | Planning run folder. |
| `--parallel` | 1 | Jobs run at once (1-64). Results and their order do not depend on it. |
| `--max-jobs` | all | Attempt at most this many jobs, in declared order; the rest are recorded as not attempted. |
