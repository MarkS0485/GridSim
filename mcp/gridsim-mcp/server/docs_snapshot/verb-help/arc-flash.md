### arc-flash

`gridsim arc-flash <study.json> [--out <result.json>]`

Incident energy and arc-flash boundary of one piece of equipment to IEEE 1584-2018 (RC3 P8): equations 1 to 25 with the
coefficients of Tables 1 to 7, three-phase AC only. The coefficients are as transcribed in the open-source (MIT licence) `arcflash`
library (Li-aung Yip); they were not checked against a copy of the standard here. The `fault` and `iec60909` verbs, their
duties and their equipment checks are independent and unchanged: this verb only reads `Iec60909.ThreePhase` outputs.

The study file is strict JSON, schema version 1: `schemaVersion` (1); `label`; `equipment` (`systemVoltageKv` Voc,
`configuration` `VCB`, `VCBB`, `HCB`, `VOA` or `HOA`, `gapMm`, `workingDistanceMm`, `frequencyHz`, `phases`, and
`enclosure` with `heightMm`, `widthMm`, `depthMm` for the box configurations only); `boltedFault`, either `ka` with
`source` (where the current came from) or `iec60909` (`case` with `builtIn` or a single case file `path` and its
`sha256`, `bus`, `objective` `Maximum` or `Minimum`: the bolted current is that bus's three-phase Ik'', and its base
voltage must match Voc to 0.5 %); `clearingTime` (`fullArcingCurrentMs`, `reducedArcingCurrentMs` and `source`: the
arc durations at the full and reduced arcing currents, read by the user from the protective device's curve; no device
curve is evaluated here).

Envelope (clause 4.2): Voc 0.208 kV to 15 kV, 50 or 60 Hz, three-phase; Ibf 0.5 kA to 106 kA at or below 0.6 kV and
0.2 kA to 65 kA above; gap 6.35 mm to 76.2 mm at or below 0.6 kV and 19.05 mm to 254 mm above; working distance at
least 305 mm; enclosure width at least four times the gap (a height or width above 1244.6 mm is taken at 1244.6 mm, as
Table 6 directs). Anything outside it gives `status` `OUT OF SCOPE` listing every breach, and no numbers.

The result gives VarCf, the enclosure correction (type, equivalent width and height, EES, CF), and for the full and the
reduced arcing current the intermediate and final arcing current (kA), the clearing time used (ms), the intermediate and
final incident energy (J/cm2 and cal/cm2) at the working distance, and the arc-flash boundary (mm, the distance at which
the energy is 1.2 cal/cm2); then the governing scenario (the higher energy) with its energy, boundary and time. Exit 0
`EVALUATED`, 3 `OUT OF SCOPE`, 1 for input it cannot accept (a malformed study, a case identity mismatch, a voltage
mismatch, an existing output path), 2 for an invalid invocation or an unknown built-in case name.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
