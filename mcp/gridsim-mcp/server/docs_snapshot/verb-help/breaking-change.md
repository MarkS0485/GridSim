### breaking-change

`gridsim breaking-change validate [--journal <file.jsonl>] [--since <version>] [--verb <verb>] [--case <glob>] [--parallel N] [--scratch <dir>] [--keep-scratch] [--accept <Bn>] [--json <file>|--csv <file>|--out <file>]`
`gridsim breaking-change import <dir|file.json>... [--journal <file.jsonl>]`
`gridsim breaking-change list [--since <version>]`

Re-run the jobs in your run journal on this build and see, bit for bit, what changed and which breaking change explains
it. The journal records each run you make while it is switched on (`--journal`, `GRIDSIM_JOURNAL` or the config file,
see Global switches and [ch. 3](<0003 Configuration.md>)). It is off by default, so switch it on before you upgrade:
the first useful `validate` compares the release you recorded with against the one you upgrade to.

`validate` reads every entry (or those the filters keep) in journal order. Each entry is re-run as a fresh
`gridsim` process of this build, in a scratch folder of its own, with its inputs read from where they were (or from the
snapshot store) and every output path rewritten into the scratch folder, so your original results are only read, never
written. Each output is then compared with the recorded one, field by field, bit for bit. Fields that carry only build
or run identity (the `tool` and `args` of a result file, a study's `provenance.engine`, timings) are left out; the list
is in technical chapter 42. Each entry gets one status:

| Status | Meaning |
|---|---|
| `IDENTICAL` | Same exit code, same output files, every compared field the same bits. |
| `CHANGED` | Something moved. The report names each field that moved, with the largest absolute and relative difference, the largest distance in ulps and the fewest agreeing bits (53 when identical, 52 for one ulp, 0 for a sign change), and checks it against the breaking changes (`B1`, `B2`, ...) that can explain it (the claim check, below). |
| `CANNOT-REPLAY` | It could not be re-run, with the reason: an input missing or changed since the run (including a file named inside a study, model assignments or plug-in configuration file), a job that would load plug-in code (`--model-plugins`, `user-model check-plugin`: a re-run never loads code), a console-only run (no result file), a verb that is never re-run (live, network, timing and hardware verbs, verbs that write outside their output flags, and the dynamics verbs listed below), a GPU run, a result file that changed since the run, or a re-run that newly exits 1 or 2 when no breaking change claims that refusal (one that does makes the entry `CHANGED` on `(exit code)` alone, explained by it). |
| `SKIPPED-SAME-BUILD` | Recorded by this very build (same version and commit). By determinism it cannot change, so it is not re-run. |

| Flag | Default | Meaning |
|---|---|---|
| `--journal <file.jsonl>` | the configured journal | The journal to read (`validate`) or append to (`import`). A network or device path exits 2; no journal at all exits 1. |
| `--since <version>` | all | Only entries recorded by this release or later (`2.1.090`; a `v` prefix and an `-RC-n` suffix are allowed). |
| `--verb <verb>` | all | Only entries of this verb (aliases resolve: `n-1` is `nminus1`). |
| `--case <glob>` | all | Only entries with an input whose path or file name matches the glob (`*` and `?`; ignoring case on Windows). |
| `--parallel N` | 0 (automatic) | Re-run up to N entries at once. Automatic is the smallest of the processor count, 80 % of installed memory in 256 MiB slots, and the number of entries. A job starts only when its memory estimate (1.25 x its recorded peak, at least 256 MiB) fits in 80 % of installed memory; a larger one runs alone. The report is the same for every N. |
| `--scratch <dir>` | a new folder in the temporary folder | Where the re-runs write. It must be new or empty, and not inside the journal's folder (exit 2 otherwise). |
| `--keep-scratch` | off | Keep the scratch folder (it is deleted at the end otherwise). |
| `--accept <Bn>` | none | Acknowledge the jobs of this run that are UNBOUNDED under Bn (only a breaking change with no bound, B3, can be accepted; any other ID exits 2). They are reported REVIEWED, and the acknowledgement is written to `<journal name>.accept.json` beside the journal. |

**The claim check.** Matching a breaking change is not enough to explain a change, or a second regression in the same
field could hide behind its ID. Each breaking change states what it does to the fields it moves: a bound (the largest
absolute or relative change of each value, from the magnitude measured in the CHANGELOG), an exact transform (new = f x
old, such as a sign flip or a ratio squared), or, where no bound means anything, the exact transition of a status or
the exit code (`Optimal` to `SecuredOutageFailsScreen`). Each changed field gets one verdict:

| Verdict | Meaning |
|---|---|
| `EXPLAINED (Bn)` | The field moved within what the breaking changes in its window claim. When several claim bounds on one field, their bounds add. |
| `EXCEEDS-CLAIM (Bn)` | The field moved, but beyond the claim: past the bound, off the transform, or through another transition. The report gives what was observed and what was claimed. |
| `AMBIGUOUS` | Several changes claim the field and their claims cannot be added (a transform or a transition beside another claim). |
| `UNEXPLAINED` | No breaking change in the window claims the field. |
| `UNBOUNDED (Bn)` | The field moved under a breaking change that has no bound (B3, below). Expected, but no number can check it: it needs your review. |
| `REVIEWED (Bn)` | An UNBOUNDED field you acknowledged with `--accept Bn`, that has moved no further since. |

An entry takes the worst verdict of its fields. Only an entry whose every changed field is EXPLAINED counts as
explained; EXCEEDS-CLAIM, AMBIGUOUS and UNEXPLAINED are flagged on the console and in the summary, and are likely a bug:
please report them. All of them are CHANGED, so the exit code is 3.

**Dynamics jobs.** `validate` re-runs `stability`, `dynamics-study`, `long-term`, `trip` and `modal`. Their result
files are the ones they write: `stability --events-out` (the RMS event export and its `run-metadata.json`) and
`--export` (the classical trajectory), the `dynamics-study --out` result, the `long-term --out` CSV (without `--out` the
CSV goes beside the input, so such a run is not re-run), and the `trip` and `modal` `--export` files. A run that printed
its result only is not re-run. The DYR or dynamics file of `--dyn-data` is an input like any other. The journal also
hashes the files an input names inside itself: a study file's RAW case, DYR, unit protection, device and curve files;
the user model files of `--model-assignments`; the assemblies of `--model-plugins`. With `--journal-snapshot` they are
kept in the snapshot store, apart from plug-in assemblies, which are code and are never copied there. On a re-run each
is read where it was if its bytes are unchanged; otherwise the input and every file it names are placed together in the
scratch folder, in the same layout, from the snapshot store. A file that is gone or changed with no copy makes the entry
`CANNOT-REPLAY: input missing: <file>` (or `input changed`). A plug-in job (`--model-plugins`, or `user-model
check-plugin`) is never re-run: a journal line or an imported result file carries its own command line, so a plug-in
configuration and its assembly could arrive with it, and a re-run never loads code. Such an entry is `CANNOT-REPLAY: not
replayable: --model-plugins loads plug-in code, and a re-run never loads code` whatever the state of its assemblies, and
nothing is loaded; run the job again by hand to compare it. These dynamics verbs are
never re-run, and the entry says why: `transient`, `emt`, `replay-event`, `scenario`, `h0-study` and `evidence` (their
result files carry wall time or the run header, and markdown is compared byte for byte), `impedance-stability` (its
export carries the time and host), `replay` and `fit-frequency` (no result file) and `fleet` (not yet shown to give the
same result twice). The run header lines at the top of a result CSV and the host, build and time fields of
`run-metadata.json` are left out of the comparison; the run description and the provenance line are compared. A
`stability` job given its case folder with a relative `--dir` names the folder in its run description as typed, and the
re-run is given its full path, so that job shows CHANGED on the run description (the `run` field and the `# run:` line)
with nothing else moved. Give the folder as a full path, or the case positionally, and the job replays IDENTICAL. A
plug-in assembly is never taken from the snapshot store, whatever the journal says.

**B3 and `--accept`.** B3 (with `--dyr-extended --allow-unmodelled`, a PSS/E DYR CSVGN1 record is now modelled as an
SVC) has no bound: how far the CSVGN1 control moves a trajectory depends on the unit's data and the disturbance. It
appears only for a job recorded before 2.1.083 whose command line holds both `--dyr-extended` and
`--allow-unmodelled` and whose DYR holds a CSVGN1 record: in practice a `stability`, `trip` or `modal` job with a
`.dyr` file as its `--dyn-data`. The DYR is always inspected: one that cannot be read makes the job CANNOT-REPLAY
before it is re-run, so B3 is never assumed. A `dynamics-study` job is never raised: that verb refuses
`--allow-unmodelled` and reads its DYR without the extended library. A job with no DYR input at all (a `pf` run, or one whose `--dyn-data` is a JSON file) read no DYR,
so B3 cannot have acted and is not raised. Any other job is judged without B3. For such a job, every field the other changes do not explain is UNBOUNDED, and the entry reads
`UNBOUNDED (B3)` once, with each moved field and its size. Nothing else wrong, the exit code is 4 (review needed).
Look at the fields; when you are content, run `validate --accept B3`. Those jobs then read REVIEWED, exit 0, and the
acknowledgement is kept in `<journal name>.accept.json` beside the journal. Later runs read REVIEWED only for the same
job with every field moved no further than accepted; a new job, a new field or a larger movement reads UNBOUNDED
again. B27 (from 2.1.117, EXDC2, IEEEX1, ESAC8B, ESST3A and IEEEG3 records modelled under the same two options)
follows the same rules, with those five records in place of CSVGN1. B28 (an RMS run refused when a model that starts
from the first step's measurements meets a disturbance at t = 0) is reached through the library API only: every
`stability` disturbance starts after the first step (a `--events` time of 0 is refused) and `dynamics-study` refuses
an event at 0, so it explains no journal job.
B33 (from 2.1.144, the DC model of MATPOWER's `makeBdc`) has no bound either: how far a tap ratio, a phase shift or
a bus Gs moves a DC result depends on the whole network. It appears only for a job that runs the DC model on a case
that has one of those terms: an in-service tap other than 0 or 1, a non-zero phase shift, or a bus Gs. A case with none
is computed exactly as before, so a change there is not B33's and reads UNEXPLAINED. The case is the job's case
argument (a case file, a case folder or a built-in name), or the case its study file names for `transmission-security`,
`transfer-capability`, `corrective-sequence` and `probabilistic`, loaded where the re-run loads it; a case that cannot
be read makes the job CANNOT-REPLAY before it is re-run. The AC modes of B33's verbs never raise it: `nminus1` (an AC
study in every mode) and `sensitivity --kind tap`, `dvdq` or `effectiveness`. A `planning-study` job is raised whatever
its master case holds, because its projects can add or change branches.

The entries are re-run in parallel, longest recorded run first; each re-run is single-threaded and is killed after ten
times its recorded run time (at least 60 s). A re-run that crashes or is killed is run once more on its own after the
others, and only that result counts. Progress (`[n/N] seq S done`) goes to stderr as runs finish; the report lists the
entries in journal order and never contains the parallelism, timings or scratch paths.

Exit codes: 0 when every entry is IDENTICAL, REVIEWED or skipped as the same build; 3 when anything else CHANGED; 4
(review needed) when the only changes are UNBOUNDED; 1 when anything cannot be re-run (this wins over 3 and 4, as in
every batch verb, and 3 wins over 4); 2 on a usage error, or when the filters name nothing.

The report (`--json`, `--csv`, `--out`) has the summary `changed_unexplained`, `changed_exceeds_claim`,
`changed_ambiguous`, `journal_entries`, `selected`, `identical`, `changed`, `changed_explained`, `cannot_replay`,
`skipped_same_build`, `build_version`, `oldest_recorded_version`, `map_sha256`, `changed_unbounded`,
`changed_reviewed` and `accepted_now`, and the tables `reviews` (one row per UNBOUNDED or REVIEWED job: accepted now,
accepted earlier, or needs review with the reason), `entries` (one row
per entry, with its status, claim check, reason, explaining IDs and worst field), `field_differences` (one row per
field that moved, with its verdict, the claim and the observation) and `breaking_changes` (how many entries each
breaking change explains, and how many it was matched to but did not cover).

```
gridsim --journal work/journal.jsonl nminus1 case14 --json out/n1.json
