### devices-check

`gridsim devices-check <case> --devices <set.json> [--curves <dir>]`

Validate a placed-device file and bind it to a case (RC4 R4-07, Core `GridSim.Core.Protection.Devices`). `<set.json>` is a
strict `gridsim-devices/1` document: `manifestVersion`, then `devices`, each with `id`, `type` (`relay`, `fuse`, `recloser`
or `breaker`), optional `function` (ANSI number: `50`, `51`, `50N`, `51N`, `67`, `21`, `87`; default `51` for a relay or
recloser), `branch` (`index`, `from`, `to`, `end` = `from` or `to`: the stable branch identity, so a parallel circuit stays
distinct), `ct` (`primaryA`, `secondaryA`), optional `vt`, `shots` (each `curve`, `pickupA` in CT secondary amps,
`timeDial`, `definiteTimeS`), `instantaneous` (`pickupA`, `delayS`), `recloseIntervalsS` (one fewer than the shots),
`fuse` (`meltCurve`, `clearCurve`, `ratingA`) and `rating` (a breaker's `ratedVoltageKv`, `makingPeakKa`, `breakingSymKa`,
`shortTimeWithstandKa`, `shortTimeSeconds`). A curve is a built-in equation family (`iec-si`, `iec-vi`, `iec-ei`,
`iec-lti`: IEC 60255-151; `ieee-mi`, `ieee-vi`, `ieee-ei`: IEEE C37.112; `dt`) or a tabulated curve from the curve data
files. Curve data lives in `data/protection-curves/*.json` (`gridsim-protection-curve/1`); every file must carry a `source`
(kind `standard`, `manufacturer` or `synthetic`, title, publisher, url, locator, retrieved, optional sha256 of the source
document) and a file without one is refused. Only publicly documented vendor data is accepted, as data, never as code;
proprietary settings-transfer formats are not supported. Distance, directional, earth-fault and differential functions are
accepted and reported as not evaluated by the three-phase study (`sc-trace`, `fault-line`, `protection-audit`);
`protection-eval` and `protection-grade` evaluate them. Three optional members carry their settings, each accepted only with
its own function: `distance` with `21` (`characteristic` `mho` or `quad`; `loops` `phase`, `earth` or `both`, default
`both`; optional `k0` as `magnitude` and `angleDeg`, default from the line's own Z0 and Z1; `zones`, one to three, each
`reachOhms`, `angleDeg`, `timeS` and for a quad `resistiveReachOhms` and optional `leftReachOhms`; `directionalLowDeg` and
`directionalHighDeg`, default -30 and 120, for a quad), `directional` with `67` (`polarisation` `quadrature`,
`zero-sequence` or `negative-sequence`; `characteristicAngleDeg`; `direction` `forward` or `reverse`, default `forward`;
`minPolarisingV` in pu, default 0.01) and `differential` with `87` (`is1A`, `k1`, `is2A`, `k2`; optional `remoteCt` as
`primaryA` and `secondaryA`, default the device's own CT). Reaches are CT/VT secondary ohms, currents CT secondary amps. A
`21` or `67` with these settings needs a `vt`; zone reaches and times must not fall from one zone to the next; `k2` must
not be below `k1`; an `87` with a `differential` setting on a transformer is refused (use `transformerDifferential`), and
so is a `21` with settings unless its `distance` sets `onTransformer: true`. An `87` may instead carry one of two
optional members (RC5 WP18):
- `transformerDifferential` (87T, on the device's own two-winding transformer): `vectorGroup` (`YNd1`, `Yd11`, `Dyn1`,
  `Dyn11`, `YNyn0`, `Yy0` or `Dd0`), `ratedMva`, and `is1`, `k1`, `is2`, `k2` in pu of the rating. Optional `hvCt` and
  `lvCt`, each `{ "branch": { index, from, to, end } }`, put that side's CT on a branch from the transformer's bus, at its
  other end; that bus must carry no load, shunt, unit, HVDC converter, static var compensator or FACTS device. The HV side is the bus with the higher base kV. The case's
  phase shift must be the group's, and its windings, where given, must match; a case without both windings is accepted
  for `YNyn0` only. Three-winding legs are refused;
- `busDifferential` (87B): `zone` (bus IDs), `cts` (each `branch` with its `end` at the zone, and `ct` as `primaryA` and
  `secondaryA`), `is1A` and `k1` in secondary amps of the CT with the largest primary, optional `is2A` and `k2` (together,
  for a dual slope), `bias` `sum` (default) or `max`, and optional `checkZone` (bus IDs, a superset of the zone). A `21` placed on a two-winding transformer that
way needs a phase shift that is a whole multiple of 30 degrees; its earth loops need no phase shift and an explicit `k0`.
The `distance` object takes these optional members (RC5 WP17):
- a zone may carry `reverse: true` (it looks behind the relay; reverse zones follow the forward zones, at most one). A
  reverse zone sends the DCB block and also trips on its own `timeS`, as a time-delayed back-up;
- `scheme`: `type` (`DUTT`, `PUTT`, `POTT` or `DCB`), `remote` (the 21 at the other end of the same branch), `channelS`
  and `coordinationS` (default 0). PUTT, POTT and DCB need a zone 2, POTT a remote zone 2, and DCB a remote reverse zone;
- `powerSwing`: `outerR`, `innerR`, `outerX`, `innerX` (ohm sec, outer larger than inner), `deltaTS`, `blockZones` (zone
  numbers) and `unblockS` (default 2);
- `loadEncroachment`: `fwdAngleDeg` and `revAngleDeg` (0 to 90) and `minLoadOhms`, for phase loops;
- `sotf`: `windowS` and `mode` (`Z2` or `nondirectional`). `windowS` (above 0) is how long the logic stays armed after
  the line closes. The static fault study is at the moment of closing, so every energised fault lies inside the window
  and the value is checked but does not change the result;
- `mutual`: `branch` (a parallel line between the same two buses) and optional `k0m` (`magnitude` and `angleDeg`, taken
  from the case's zero-sequence mutual data when absent), for earth loops.

Exit 0 valid; 1 the case, device file or curve data cannot be loaded or
is invalid (a branch whose ends differ from the case, an unknown curve, a duplicate ID, a recloser with the wrong number of
intervals); 2 usage.

| Flag | Default | Meaning |
|---|---|---|
| `--devices` | required | The `gridsim-devices/1` file. |
| `--curves` | the installed `data/protection-curves` | A directory of curve data files to use instead. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
