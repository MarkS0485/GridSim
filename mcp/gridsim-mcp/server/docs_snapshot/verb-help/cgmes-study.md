### cgmes-study

`gridsim cgmes-study <study.json> [--out <result.json>] [--export <model.xml>] [--archive <bundle.zip>]`

CGMES as a normal study job (RC3). One CGMES profile bundle runs the whole job in a fixed order, each stage over the
existing CIM code that owns it: bundle inspection; import (`CimImporter`), with any switch actions applied before the
topology processor builds the buses; the connected electrical model and its identity maps (`CimGridModelBridge`);
control application and the AC solve (`CimControlledPowerFlow` around the Newton engine, reactive limits enforced);
absolute intact limits; optionally the `transmission-security` preventive N-1 verification over the imported model;
and export of the realised model (`CgmesWriter`) with a read-back that is compared with what was solved. A later stage
never bypasses a failed earlier one.

The study file is strict JSON, schema version 1. Required: `schemaVersion` (1), `bundle` (`sha256` and `profiles`) and
`voltage` (`minPu`, `maxPu`, applied to every solved bus). Each profile names its `profile` type (`EQ`, `TP`, `SSH`,
`SV`, `EQ_BD`, `TP_BD`, `DY`, `DL` or `GL`), its declared `version`, its `path` (relative to the study file) and the
`sha256` of its exact bytes. `bundle.sha256` is the SHA-256 of the UTF-8 manifest lines (profile, TAB, version, TAB,
path, TAB, lowercase file SHA-256, LF) in manifest order; a mismatch is refused and prints the resolved identity.
`--archive <bundle.zip>` (RC4 R4-11b) reads the profiles from a ZIP instead of from files: each profile `path` then names
an entry of the archive (relative, `/`-separated; one level of nested zip is `outer.zip/inner.xml`), the archive is read in sorted
entry order, and an entry path with `..`, a rooted path, a repeated entry or two documents of one profile is refused. The
digests and the bundle identity are checked exactly as for files; without the flag a `.zip` profile path is still refused.
Optional: `import` (`topology` `auto`, `topologicalNodes` or `switches`;
`tapPositions` and `shuntSections` `ssh` or `sv`; `startingVoltages`, `apparentPowerRatings` and
`requireSshSetPoints`, all true by default), `system` (`baseMva`, default 100; `frequencyHz`, default 50: CGMES
carries neither, and the resolved values and their source are recorded), `controls` (`apply`, default true; `shunts`
`asWritten`, `discrete`, `continuous` or `held`; `tolerancePu`; `maxSolves`), `switchActions` (each a switch `mrid`
and `open` true or false; needs `topology` `switches`) and `security` (`outages`, each a branch `mrid`;
`postFaultRating` `A`, `B` or `C`; optional `dc.maxRounds`).

The bundle is refused, naming the profile and the record, before anything is imported when: a digest does not match;
a required profile is missing (EQ and SSH always, TP unless `topology` is `switches`, SV when an option reads it); a
profile type is listed twice; a header's `md:Model.profile` contradicts the declared type; a `md:Model.DependentOn`
names no model of the bundle; a reference names an object no profile defines; or two profiles give one property of one
object different values. A missing load or machine set-point is refused by the importer (`requireSshSetPoints`); an
absent optional profile is only listed. No conformity to an external CGMES profile specification is checked or
claimed.

The result records each profile (declared and header identity, whether it is consumed by the import, what is preserved
and what the solve uses), the importer and bridge notes, the identity of every solved bus, branch (with the source of
its rating) and generator, the switch actions with the equipment terminals that changed bus, every control represented
(local and remote voltage control, static var compensators, regulating shunts) with where it ended, and the automatic
controls the study does not represent (automatic tap control: taps are held at the selected step). Ratings are the
permanent (PATL) apparent-power limits; temporary limits are not imported, so a post-fault `B` or `C` falls back to
`A`. The exchange output is one RDF/XML document holding the EQ, TP, SSH and SV content, written from the realised
model (each control folded in at its realised value, the solved voltages as the starting state); it is read back and
compared for equipment identity, topology, ratings bit for bit, model data (reported with exact differences) and the
controlled AC output (voltage within 1e-6 pu, angle within 1e-5 degrees, terminal P and Q within 1e-4 MW or MVAr, the
same limit disposition). The source mRIDs are not carried into the exported document; the result maps each one to its
exported identity. The deferred items are listed in the result.

The study `status` is `SECURE` only when the intact controlled state (and every listed outage, with `security`) is
evaluated within its limits and the read-back is equivalent; otherwise `NOT SECURE`, with `outcome` `Violations` or
`Incomplete` (missing evidence takes precedence). The intact state's disposition is `SECURE`, `VIOLATIONxN`,
`NO SOLUTION` or `INCOMPLETE`, as in `transmission-security`. The result JSON (`schemaVersion`, `provenance`,
`effectiveOptions`, `result`) is printed to stdout and, with `--out`, written to a new file; `--export` writes the
exchange document to a new file. Both go through a temporary file and a final move, and an existing path is refused.

Exit 0 `SECURE`. Exit 1 for input it cannot accept: a malformed or unknown-field study, a bundle identity or digest
mismatch, a refused bundle, an unreadable profile or an existing output path. Exit 2 for an invalid invocation. Exit 3
`NOT SECURE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
| `--export` | none | Write the exchange CGMES document to this new file. |
