# GridSim schema notes (for a data-producing agent)

Written 2026-10-05 against GridSim 1.95.193. Read this before producing any data GridSim is meant to load. It explains
what the schemas are, what each one can express today, what GridSim will and will not accept, and the output rules that
apply from the first file you write. The schema files themselves are the authority: `schemas/v1/*.schema.json`,
`schemas/v2/*.schema.json` and `schemas/v2/kinds/*.kind.json` in the GridSim repository.

## 1. Two formats

| | v1 (default today) | v2 (newer, opt-in) |
|---|---|---|
| Shape | A case FOLDER of fixed-name JSON files | A case MANIFEST (`*.case.json`) + network files + template libraries |
| Tag | none (identified by folder layout) | `"schema": "gridsim-case/2"`, `gridsim-network/2`, `gridsim-library/2`, `gridsim-kind/2` |
| Loaded by | `JsonGridLoader` (any verb given a folder) | `V2CaseLoader` (any verb given a `.json` manifest) |
| Equipment types | `asset-classes.json` + `asset-bindings.json` | template libraries (kinds), bindings, explicit overrides |
| Used by | every bundled case (`data/uk`, `data/gb-full`, `data/gb-synthetic`); what `gda --out` writes today | new work that needs reusable, versioned, provenance-tagged equipment data |

Both formats describe the same network model; a bundled v1 case projected to v2 loads to the same model and solves to
the same bits. New producer work should target **v2**, unless it must feed a tool that only reads v1.

### 1.1 v1 case folder

Fixed file names (each optional unless noted; unknown keys are refused):

- `system.json`: `name`, `baseMva`, `frequencyHz`, `dispatch`.
- `buses.json` (required): `id`, `name`, `baseKv`, `type` (pq / pv / slack; anything else is refused), voltage start,
  shunts, coordinates, optional `zip` load model, optional `vminPu` / `vmaxPu`.
- `generation.json`: generators (`name`, `bus`, `type` = fuel, capacity, output, limits, `inertiaH`, `role`,
  `inService`, `pMinMw` / `pMaxMw` (the string `"Infinity"` allowed for an unlimited Pmax / Qmax, `"-Infinity"` for Qmin),
  converter fields).
- `demand.json`: `name`, `bus`, `pMw`, `qMvar` or `powerFactor`.
- `transmission.json`: lines (`name`, `from`, `to`, `rPu`, `xPu`, `bPu`, `ratingMva`, `ratingBMva`, `ratingCMva`,
  `circuits`, `lengthKm`, `tapRatio`, `phaseShiftDeg`, `isTransformer`).
- `transformer.json`: transformers (same electrical fields, ratings A / B / C).
- `onloadtapchanger.json`, `boundaries.json` (boundary name, MW limit, member branch names: every name must resolve to
  exactly one branch), `protections.json`.
- `asset-classes.json` / `asset-bindings.json`: dynamic behaviour classes (inverters, storage, protection settings,
  thermal) and which elements they bind to.

### 1.2 v2 documents

- **Case manifest** (`gridsim-case/2`): `name`, `system` (`baseMva`, `frequencyHz`, both required), `network` (paths of
  network files, relative to the manifest; the network is their union), `imports` (library file + which templates),
  `localTemplates`, `bindings` (element type -> template; exact match beats a prefix match ending in `*`), `overrides`.
  `gridCodeProfile` and `solverProfile` exist in the schema but are not bound from a case yet.
- **Network file** (`gridsim-network/2`): arrays `buses`, `generators`, `demands`, `lines`, `transformers`, `oltcs`,
  `boundaries`, `protections`. Required fields: bus `id` + `baseKv` (baseKv is REQUIRED in v2); generator `name` + `bus`;
  demand `name` + `bus` + `pMw`; line / transformer `name` + `from` + `to` + `rPu` + `xPu`; oltc `transformer` +
  `controlsBus`; boundary `name` + `limitMw`; protection `name` + `template` + `monitorBus`. Elements may name a
  `template` (`id@rev`). Absent optional fields take exactly the v1 default (the "absence rule").
- **Template library** (`gridsim-library/2`): `templates`, each with `id`, `rev`, `kind`, `status`, `title`, `params`,
  `provenance` (+ optional `description`, `controllers`).
- **Kind spec** (`gridsim-kind/2`, GridSim-owned, one per kind under `schemas/v2/kinds/`): the parameter contract for a
  kind (type, unit, min, max, allowed values, required, defaults).
- **Machine dynamic data** (`gridsim-dynamics/2`, `dynamics.schema.json`, F23, October 2026): per-machine data for the
  RMS transient-stability study (`gridsim stability --rms --dyn-data <file>`). See section 2.1.
- **Three-phase feeder** (`gridsim-dist3ph/2`, `dist3ph.schema.json`, N339, October 2026): the v2 form of a
  `gridsim-dist3ph/1` distribution feeder, read by every `gridsim dist3ph` mode and written (from a v1 file) by
  `gridsim dist3ph <feeder> to-v2 --to <out.json>`. It is the solver-authoritative PER-UNIT model (series impedance and
  shunt admittance as complex 3x3 matrices `{re, im}`, regulator and transformer ratios, source voltages, load powers and
  capacitor ratings, all `pu@system`) beside `baseMva` (MVA) and each bus's `baseKvLL` (kV), so nothing is converted at
  load; `provenance._default` is required and any element may carry its own `prov`. The v1 file stays the authoring
  format in physical units (ohm/mile, ft, kW are outside the closed vocabulary, which is why v2 holds the per-unit form).
  The dist3ph element set is defined by the schema's `$defs`, not as kind specs (see the schema's description).

## 2. What can be expressed: the 16 kinds

Each kind is a parameter contract; a template is one concrete, versioned set of values for a kind.

| Kind | Params | What it describes |
|---|---|---|
| `synchronousMachine` | 24 | Rotating machine: inertia, GENROU reactances and time constants, leakage and saturation, sequence and step-up data, per-fuel capacity factor |
| `gridFollowingInverter` | 38 | PLL-behind current source (wind, solar, HVDC, DG): FFR, loss-of-mains trips, impedance and RMS parameters |
| `gridFormingInverter` | 25 | Voltage-source converter with synthetic inertia: ride-through, impedance, RMS parameters |
| `storage` | 27 | Energy store behind a converter: capacity, state of charge limits, chemistry, DC voltage |
| `load` | 9 | Demand characteristics: power factor, Q/P ratio, ZIP voltage dependence |
| `lineType` | 15 | Overhead line or cable electrical type (per unit, physical and length side by side) plus thermal time constant |
| `transformerType` | 13 | Transformer electrical type (per unit, percent on rating, X/R) |
| `oltc` | 10 | Tap changer control (target, step, deadband, limits, inter-tap delay) and transformer thermal state |
| `protection` | 36 | Relay settings (RoCoF, df/dt, under / over frequency and voltage, overcurrent and others) |
| `exciter` | 51 | SEXS, IEEE Type 1, alternator-rectifier (EXAC1, ESAC1A) or static ESST4B excitation system |
| `governor` | 36 | Speed governor (lumped and per-machine TGOV1, IEEEG1 with rate limits and LP fractions, hydro, GAST) |
| `pss` | 9 | PSS1A power system stabiliser |
| `harmonicSpectrum` | 100 | Harmonic current source: magnitude and phase per order 2-50 (G5 Issue 5 spectra exist) |
| `site` | 2 | A named site with generation / demand slots, for sites not modelled in any public data |
| `gridCodeProfile` | 14 | Grid-code frequency and voltage limits (not bound from a case yet) |
| `solverProfile` | 12 | Solver tolerances and limits (not bound from a case yet) |

Some parameters are marked `markGated` in their kind spec: GridSim validates them but does not yet feed them into a
solver (chiefly the inertia, governor, exciter and other dynamics wiring). A template may carry them; they simply have
no effect until that wiring lands. Check `params.<name>.markGated` in the kind file before relying on a value.
Exception since F23 (October 2026): the machine, exciter, PSS and governor parameters listed in section 2.1 ARE read,
by the RMS study, when they come through a `gridsim-dynamics/2` file (their `markGated` flags describe the case and
library route, which is still gated).

### 2.1 Machine dynamic data: `gridsim-dynamics/2` (F23)

A standalone document, given to the RMS study with `gridsim stability <case> --rms --dyn-data <file>` and checked by
`gridsim validate <file>`. JSON first; a PSS/E `.dyr` reader is a later step that would produce the same objects.

```json
{
  "schema": "gridsim-dynamics/2",
  "name": "WSCC 9-bus machines",
  "templates": [
    { "id": "gridsim.example.gen2", "rev": 1, "kind": "synchronousMachine", "status": "active", "title": "Unit 2",
      "params": { "inertiaH": 6.4, "xdPu": 0.8958, "xdpPu": 0.1198, "xdppPu": 0.089, "xqPu": 0.8645,
                  "xqpPu": 0.1969, "xqppPu": 0.089, "tdopS": 6.0, "tdoppS": 0.033, "tqopS": 0.535, "tqoppS": 0.07 },
      "controllers": { "exciter": "gridsim.example.sexs@1", "governor": "gridsim.example.tgov1@1" },
      "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "Illustration only [VERIFY]" } } },
    { "id": "gridsim.example.sexs", "rev": 1, "kind": "exciter", "status": "active", "title": "SEXS",
      "params": { "model": "SEXS", "sexsLeadS": 0.1, "sexsLagS": 1.0, "sexsGain": 100, "sexsExciterTimeS": 0.05,
                  "sexsFieldMinPu": 0, "sexsFieldMaxPu": 8 },
      "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "Typical values [VERIFY]" } } },
    { "id": "gridsim.example.tgov1", "rev": 1, "kind": "governor", "status": "active", "title": "TGOV1",
      "params": { "model": "TGOV1", "droopPu": 0.05, "tgov1ValveTimeS": 0.5, "tgov1ReheatLeadS": 3.0,
                  "tgov1ReheatLagS": 10.0, "valveMaxPu": 3.0 },
      "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "Typical values [VERIFY]" } } }
  ],
  "machines": [ { "bus": 2, "template": "gridsim.example.gen2@1" } ]
}
```

- **Templates** have exactly the library template shape and pass every library rule (kind specs, closed units, ranges,
  value forms, provenance with `[VERIFY]` on a `CuratedPlaceholder`, unique `id@rev`, controller references that
  resolve within the file to active templates of the slot's kind). So a dynamics file's templates can later move into a
  library unchanged.
- **Machines** bind one generator each, by `generator` (its name; exactly one unit of that name) or by `bus` (with
  `unit`, the 0-based index among the bus's in-service units in case order, when the bus has more than one). A generator
  is bound at most once; converter plant and power-flow-only units are refused (the RMS study does not integrate them).
- **Machine template** (`synchronousMachine`): required `xdPu`, `xdpPu`, `xdppPu`, `xqPu`, `xqpPu`, `xqppPu` (X''d must
  equal X''q, the round-rotor network fold), `tdopS`, `tdoppS`, `tqopS`, `tqoppS`; optional `inertiaH` (absent: the
  case's own H, said in a note), `damping` (absent: 0), `xlPu` (stator leakage, below X''d; absent: 0),
  `saturationS10` with `saturationS12` (PSS/E S(1.0), S(1.2); GENROU saturation on |psi''| on both axes, N045). Any other
  machine parameter (`unitTransformerXPu`, `fuelType`, `capacityFactor`, the synthesis ratios) is refused: it belongs to
  the case or to synthesis, and a number that would be ignored is an error. All on the machine base.
- **Controllers** (the machine template's `controllers` slots): `exciter` with `model` `SEXS` (required `sexsLeadS`,
  `sexsLagS`, `sexsGain`, `sexsExciterTimeS`; optional field limits) or `IEEET1` (required `ieeet1RegulatorGain`,
  `ieeet1RegulatorTimeS`, `ieeet1ExciterConstant`, `ieeet1ExciterTimeS`, `ieeet1FeedbackGain`, `ieeet1FeedbackTimeS`;
  optional regulator limits and saturation A, B), `EXAC1` or `ESAC1A` (the alternator-rectifier exciters, which read the
  field current; all required: `acLeadS` TC, `acLagS` TB, `acRegulatorGain` KA, `acRegulatorTimeS` TA, `acRegulatorMinPu` /
  `acRegulatorMaxPu` (VRMIN / VRMAX for EXAC1, VAMIN / VAMAX for ESAC1A), `acExciterTimeS` TE, `acFeedbackGain` KF,
  `acFeedbackTimeS` TF, `acRectifierKc` KC, `acDemagnetisingKd` KD, `acExciterConstant` KE, `acSatE1`, `acSatSe1`,
  `acSatE2`, `acSatSe2`; ESAC1A also `acOutputMinPu` / `acOutputMaxPu` VRMIN / VRMAX) or `ESST4B` (the static exciter,
  which reads the terminal voltage and current and the field current; all required: `st4bKpr`, `st4bKir`, `st4bVrMaxPu`,
  `st4bVrMinPu`, `st4bTaS`, `st4bKpm`, `st4bKim`, `st4bVmMaxPu`, `st4bVmMinPu`, `st4bKg`, `st4bKp`, `st4bKi`, `st4bVbMaxPu`,
  `st4bKc`, `st4bXlPu`, `st4bThetapDeg`); `pss` with `model` `PSS1A` (required `gain`, `washoutS`, `lead1S`,
  `lag1S`, `lead2S`, `lag2S`; optional output limits; needs an exciter); `governor` with `model` `TGOV1` (required
  `droopPu`, `tgov1ValveTimeS`, `tgov1ReheatLeadS`, `tgov1ReheatLagS`; optional `turbineDampingPu`, `valveMinPu`,
  `valveMaxPu`), `IEEEG1` (required `droopPu`, `ieeeg1LagS` T1, `ieeeg1LeadS` T2, `ieeeg1ValveTimeS` T3, `ieeeg1ChestS`
  T4, `ieeeg1ReheatS` T5, `ieeeg1CrossoverS` T6, `ieeeg1LpS` T7; optional `ieeeg1K1`..`ieeeg1K8` (summing to 1; absent:
  K1 = 1, the rest 0), `valveMinPu`, `valveMaxPu`, `ieeeg1UoPuPerS`, `ieeeg1UcPuPerS` (valve rate limits, unit
  `MW/s/MW`, i.e. pu of the machine base per second)), `GAST` (the simple gas turbine: required `droopPu` R, `gastValveTimeS` T1,
  `gastTurbineTimeS` T2, `gastExhaustTimeS` T3, `gastAmbientLimitPu` AT, `gastTemperatureGain` KT; optional `valveMinPu`,
  `valveMaxPu`, `turbineDampingPu` DTURB) or `HYGOV` (GridSim's transient-droop hydro governor with the
  ideal water column: required `droopPu` (Rp), `hygovTransientDroopPu`, `hygovResetTimeS`, `hygovGateTimeS`,
  `hygovWaterTimeS`; optional gate limits as `valveMinPu` / `valveMaxPu`). The lumped governor (`model` `lumped`) and
  its `governorTimeConstantS` / `responsiveFraction` are the frequency model's, not a machine's, and are refused here.
  Absent valve floors are 0, so a motoring unit needs an explicit negative `valveMinPu`; every governor and exciter must
  be able to hold the solved operating point inside its limits, or the study stops with the reason (R252).
- **Provenance** decides the label: the RMS output drops INDICATIVE only when the file covers every machine and every
  value is `Recorded` or `DerivedFromRecorded`.

## 3. The rules GridSim enforces (output requirements from the off)

1. **Unknown members are refused everywhere.** A misspelt or invented key is an error, not ignored. Only write keys the
   schema and kind spec define.
2. **Closed unit vocabulary.** A unit written in a file must equal the kind spec's unit exactly: no conversion, no
   aliases. The allowed units are: `1`, `pu@system`, `pu@machine`, `pu@transformer`, `s`, `Hz`, `Hz/s`, `MW`, `MVA`,
   `Mvar`, `kV`, `km`, `ohm/km`, `uS/km`, `%`, `MWh`, `MW/min`, `MW/Hz/MW`, `degC`, `deg`, `MW.s/Hz`, `MW/Hz`, `MW/s/MW`,
   `V`, `min`. Per unit stays authoritative for the solver; physical values are carried alongside, not converted at load.
3. **Values.** A parameter value is a bare number (takes the kind's unit and the template's default provenance) or an
   object `{"v": ..., "unit": ..., "prov": ...}` to give that one value its own provenance. Value types (number, integer,
   string, boolean, enum) and min / max ranges are checked against the kind spec.
4. **Provenance on everything.** Every template carries `provenance._default` (and optionally per-parameter entries).
   Tags are exactly: `Recorded`, `DerivedFromRecorded`, `CuratedPlaceholder`, `Synthetic`, `Counterfactual`,
   `Interpolated`, `UnvalidatedAtScale` (case-sensitive). Each has a plain-English `note`; a `CuratedPlaceholder` note
   MUST contain the literal token `[VERIFY]`. Say where a number came from (document id, table, dataset, date).
5. **Template identity is immutable.** Ids look like `gridsim.<segment>.<segment>...` (letters, digits, `_`, `-`).
   A published `id@rev` never changes: a corrected value is a new revision. Element references are pinned (`id@rev`).
   Imports may take an exact `id@rev`, an id (its highest revision, which is reported), an id prefix ending `.*`, or
   `gridsim.*`.
6. **Status.** `active`: every value set. `placeholder`: every value null (a named slot for something not publicly
   modelled; never usable as a controller target). Nothing in between.
7. **Overrides are explicit.** A case that changes a template value must say why (`reason`, non-blank) and acknowledge
   it (`"shadow": "explicit"`). There is no silent shadowing.
8. **Names and ids.** Bus ids unique; element names unique per type (the SchemaEditor's save rule refuses duplicates;
   parallel circuits need distinct names); every bus reference must resolve; every boundary branch name must match
   exactly one branch.
9. **Plain data, no code.** Files hold data only. Nothing a producer writes can change GridSim's maths (see
   `SUBMODULES.md`): data is ordinary input through the same loaders and checks as any user file.
10. **Dated data passes the no-forward-inference guard.** Anything time-stamped from the real grid (measurement sets,
    event traces, replays) must carry its date and is refused if it is newer than GridSim's horizon (now minus 169 h)
    or undated where a date is required. Assumed, typical or library values (for example a typical spectrum) may be
    undated. Never write "live" data for GridSim to consume.
11. **Text.** Plain ASCII, EN-GB spelling, no em / en dashes or smart quotes in names, notes and descriptions.
12. **Validate before handing over.** `gridsim validate <file or folder>` checks v2 documents against the schemas;
    loading the case with any verb (for example `gridsim pf <case>`) runs the loader's own checks. A file that does not
    pass both is not finished.

## 4. What GridSim does with it

- **Power flow and analysis:** buses, branches, generators, demand, taps, ratings and voltage limits feed the power
  flow (always sparse Newton), contingency (N-1 / N-2 / N-1-1 with per-bus limits and emergency ratings), DC / AC OPF,
  continuation, losses, distribution factors.
- **Faults and protection:** line / transformer sequence data and relay settings feed IEC 60909, sequence fault studies,
  relay assessment and grading. Earth faults need winding and sequence data, which a v2 network file now carries (phase
  P5, opened for sequence data only, N234, October 2026): generator `subtransientXdPu` / `negativeSeqXPu` (pu on the
  unit rating) and transformer `windings` (`from` / `to`: `Delta`, `WyeGrounded`, `WyeUngrounded`), `r0Pu` / `x0Pu`
  and `neutralXPu` (pu on the case base). The loader refuses them where they would be ignored or misread: X''d / X2 on
  a converter-interfaced unit; r0 / x0 / neutral without windings; r0 without x0; a neutral on a side that is not
  `WyeGrounded`. v1 cannot carry them.
- **Dynamics:** inverter, storage and protection templates feed asset dynamics and the frequency studies; machine and
  controller templates feed the RMS study through a `gridsim-dynamics/2` file (section 2.1, F23); from a case
  manifest or library binding they are still gated (the `markGated` parameters).
- **Harmonics, estimation, EMT, three-phase:** spectra, load models and line types feed those studies where wired.

## 5. What is NOT supported yet (do not invent it)

- **Time-varying profiles** (generation, demand, loading or rating over time): no schema kind exists. Mark is building
  the profile data first (Parquet lakes, intended to ship as data submodules); the GridSim integration will be designed
  afterwards. Do not invent a profile format inside these schemas; keep profiles in your own Parquet design and expect
  a dedicated kind and binding to be added later, additively, within v2.
- **Parquet inside GridSim Core:** Core reads JSON only; Parquet reading exists only in the GDA bridge layer.
- **Case-bound grid-code and solver profiles, unit conversion at load, dynamics wiring from templates:** later phases.
- **Earth-fault sequence data in v1 case folders:** v1 has no keys for it; use a v2 case (P5, open for sequence data
  only since N234). The rest of P5 (out-of-service lines and transformers in v2) is still to come.

## 6. Where to look

- Schemas: `schemas/v1/`, `schemas/v2/` (start with `case.schema.json`, `network.schema.json`, `library.schema.json`,
  `common.schema.json`), kinds in `schemas/v2/kinds/`.
- Examples: the bundled v1 folders under `data/` (`data/uk` is small); v2 test fixtures under
  `tests/GridSim.Tests/fixtures/v2/`; library placeholders under `data/library/placeholders/`.
- Docs: the GridSim technical chapters on file formats and the v2 schema (`docs/technical/`), and `SUBMODULES.md`.
- Questions about units or ranges: the kind file's `params` block is the contract.
