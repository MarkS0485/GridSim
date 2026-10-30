# 28. The v2 JSON schema

The v2 JSON family describes a case as a manifest, one or more network files, and a library of fixed, standardised
templates, with every document tagged by type and version, every unknown member refused, and every error reported
with a JSON pointer. This chapter specifies the family as it exists on main: the schema files under `schemas/v2`, the
hand-rolled validator subset that enforces them (`JsonSchemaSubset`, `SchemaValidator`), the kind specs and their
`codeDefaults` (`KindSpecRegistry`), the library checker, the `validate` verb, the case loader (`V2CaseLoader`) that
reads a `gridsim-case/2` manifest into a `GridModel`, the test-only projector that proves v2 and v1 describe the same
network, and the placeholder sites. The v1 case folder (ch. 10, [File and wire formats](<0010 File and wire formats.md>))
stays the default format; a v2 case is a second door into the same model builder, so a v2 case and its v1 equivalent give
the same model, and the same default Newton solve bit for bit (pinned for the projection of every bundled case).

## Model

### Document types

Five schema files and sixteen kind specs make up the family. Every v2 document carries a `"schema"` member that names
its type and version and is matched exactly.

| Doc type | Schema file | Holds |
|---|---|---|
| (shared) | `schemas/v2/common.schema.json` | `$defs` only: unit vocabulary, provenance tag and object, quantity, parameter values, template id, revision and reference, status |
| `gridsim-library/2` | `schemas/v2/library.schema.json` | a file of templates |
| `gridsim-kind/2` | `schemas/v2/kind.schema.json` | one kind's parameter contract, as `schemas/v2/kinds/<kind>.kind.json` |
| `gridsim-case/2` | `schemas/v2/case.schema.json` | the case manifest: name, system block, network file list, imports, local templates, bindings, overrides, optional profiles |
| `gridsim-network/2` | `schemas/v2/network.schema.json` | the placed elements: buses, generators, demands, lines, transformers, oltcs, boundaries, protections |

`JsonSchemaSubset.LoadDirectory` loads every `*.schema.json` directly inside the folder and none from `kinds/`: kind
specs are documents validated by `kind.schema.json`, not schemas. The five schema files are also embedded in
`GridSim.Core` (`BundledSchemas`, resources `GridSim.Core.schemas.v2.*`) for the case editor, and the CLI ships the
whole `schemas` tree beside the binary so a release download validates against the exact schemas it understands.

### Principles

- **Strict.** Unknown members are refused everywhere: by the schema (`additionalProperties: false`), and for the open
  `params`, `provenance` and override maps by the kind and library checks; member names are case-sensitive,
  and duplicate member names in one object are refused when a v2 document is read.
- **The schema files are the source of truth and are enforced.** The validator refuses, when the schema set loads, any
  keyword it does not implement, so a schema can never be half-enforced silently.
- **Fail loud.** Every check collects every error, each with a JSON pointer (RFC 6901), and the caller fails on any
  error, naming the file. Nothing stops at the first problem.
- **Zero dependencies.** The validator is written on `System.Text.Json` inside `GridSim.Core`; no NuGet package.
- **Absence means the v1 default.** An optional member that is absent takes exactly the default the v1 loader gives
  the same key (`JsonDtos.cs`, `JsonDefaults`), so a v1 case and its v2 projection describe the same network.
- **Units and provenance on values.** A bare template value takes the kind spec's unit and the template's `_default`
  provenance. The object form `{ "v", "unit", "prov" }` gives one value its own provenance, and a unit written in a
  file must equal the spec's unit exactly: there is no conversion and no alias.
- **Reading.** Every v2 document is read with comments skipped and trailing commas allowed (the v1 reader's leniency,
  `JsonSchemaSubset.ReaderOptions`), so a hand-edited file can be annotated.

### Where v2 differs from v1

| Subject | v1 loader | v2 |
|---|---|---|
| Document identity | none | `"schema": "gridsim-<type>/2"`, exact match |
| Files | one file per element array, plus `system.json` | one manifest and one or more network files, each holding every element array it needs |
| `demand.json`, `transmission.json`, `onloadtapchanger.json` | file names | `demands`, `lines`, `oltcs` arrays |
| Case name, base MVA, frequency | `system.json`, folder name and 100 MVA / 50 Hz defaults | `name`, `system.baseMva`, `system.frequencyHz` all required |
| Bus `baseKv` | required | required |
| Bus `type` | `pq`, `pv`, `slack` and the aliases `gen`, `ref`, `swing`, any letter case | exactly `pq`, `pv`, `slack` |
| Generator `role` | any letter case | exactly `pv`, `slack`, `pq` |
| Demand power factor | clamped to 0.01 to 1 | refused outside (0, 1] |
| Element names | any string | at least one non-blank character (every element except a bus, whose `name` is any string) |
| `dynamicsClass` (generator, line, transformer) and protection `class` | class name | `template`, a pinned `id@rev` reference |
| Generator `converterInterfaced`, `converterFaultCurrentPu`, `gridForming` | flat members | the `converter` block: `interfaced`, `faultCurrentPu`, `gridForming` |
| Line `rPu0`, `xPu0` | as written | `r0Pu`, `x0Pu` |
| Asset classes and bindings | `asset-classes.json`, `asset-bindings.json` | `localTemplates`, `bindings`, plus `imports` and `overrides` |
| v2-only members | none | generator `subtransientXdPu`, `negativeSeqXPu`; line `type`, `inService`; transformer `type`, `r0Pu`, `x0Pu`, `windings`, `neutralXPu`, `inService` |

### The supported schema subset

The validator implements these 23 keywords and no others:

```
$schema $id $ref $defs type properties required additionalProperties items enum const minimum maximum
exclusiveMinimum minItems pattern oneOf anyOf if then else title description
```

`JsonSchemaSubset.SupportedKeywords` holds the list. Loading a schema set runs four passes, collects every problem and
throws one `InvalidDataException` that lists them all, each naming the file and pointer:

1. Every file parses. A schema file name must be a plain name with no `/`, `\` or `#`.
2. Every schema object is walked. A keyword outside the list is refused. Each supported keyword is checked for shape:
   `$schema` must be the 2020-12 URI and appear only at a file root; `$id` must equal the file name and appear only at a
   root; `then` and `else` need a sibling `if`; `enum` is non-empty with no repeated value; `minimum`, `maximum` and
   `exclusiveMinimum` are finite numbers; `minItems` is a non-negative integer; `required` lists each name once;
   `type` names are among `object array string number integer boolean null`, each at most once; `items` is a single
   schema; a boolean schema is accepted only as the value of `additionalProperties`.
3. Every `$ref` resolves to a registered schema node. A `$ref` is local (`#/$defs/x`) or cross-file by plain file name
   (`common.schema.json#/$defs/quantity`). Anchors are not supported. A `$ref` lands only on a place where a schema may
   legally appear, never on an `enum` value.
4. A `$ref` cycle that consumes no input is refused. This pass runs only when passes 1 to 3 found nothing. In-place edges are `$ref`, each `oneOf` / `anyOf` branch and
   `if` / `then` / `else`. `properties`, `additionalProperties` and `items` step into a child value, so a recursive
   tree schema is legal.

A pattern is an unanchored search, as in JSON Schema. Each unescaped `$` outside a character class is rewritten to
`\z` (absolute end), because .NET's `$` also matches before a final newline and ECMA-262's does not. Every pattern is
compiled with the invariant culture and a one-second match timeout. Rules the subset cannot express are checked in
code and documented at the check: `KindSpecRegistry`, `V2LibraryChecker` and `V2CaseLoader`.

### Common definitions

`common.schema.json` defines the vocabulary every other schema uses.

**Unit vocabulary.** The vocabulary is closed: `1` (dimensionless), `pu@system`, `pu@machine`, `pu@transformer`
(per unit on the system, machine or transformer base), `s`, `Hz`, `Hz/s`, `MW`, `MVA`, `Mvar`, `kV`, `km`, `ohm/km`,
`uS/km`, `%`, `MWh`, `MW/min`, `MW/Hz/MW`, `degC`, `deg`, `MW.s/Hz` (power per rate of change of frequency),
`MW/Hz` (absolute damping), `MW/s/MW` (slew per MW of rating), `V` and `min`, then the six asset overlay units `m`,
`mm`, `mm2`, `A`, `ohm` and `ohm.m` (Phase 5a; only the overlay kinds use them, see
[Asset overlay](#asset-overlay-gridsim-assets2)). A template parameter whose natural unit is not in
the list is not declared: the kind descriptions record each omission (RMS active-current recovery rate in pu/s, the
grid-forming RMS voltage gain in 1/s, load self-damping in percent per Hz, cell capacity in Ah, resistances in ohm,
activation energies in J/mol and similar).

**Provenance.** `provenanceTag` is exactly the member names of `GridSim.Core.Diagnostics.ProvenanceTag`, matched
case-sensitively: `Recorded`, `DerivedFromRecorded`, `CuratedPlaceholder`, `Synthetic`, `Counterfactual`,
`Interpolated`, `UnvalidatedAtScale`. A provenance object is `{ "tag", "note" }`, both required, no other member. When
the tag is `CuratedPlaceholder` the note must contain the literal token `[VERIFY]` (the honesty rule of
`AssetClass.ValidateProvenance`, ch. 8, [Provenance and invariants](<0008 Provenance and invariants.md>)).

**Values.** `quantity` is a number or `{ "v", "unit", "prov" }` with only `v` required. `valueObject` is the object form
of a template parameter value, with `v` a number, string or boolean. `presentParamValue` is a bare number, string or
boolean, or a `valueObject`, and is never null. `paramValue` is null or a `presentParamValue`; null is legal only in a
placeholder template.

**Identity.** `templateId` is `gridsim.` followed by dot-separated segments of letters, digits, `_` and `-`, each
segment starting with a letter or digit. `templateRev` is an integer of at least 1. `templateRef` is a `templateId`
plus `@` and a revision written without leading zeros (`gridsim.exciter.sexs@1`). `status` is `active` or
`placeholder`.

### Library files and templates

A `gridsim-library/2` file has `schema`, optional `title` and `description`, and a non-empty `templates` array. A
template has `id`, `rev`, `kind` (lower camel case), `status`, `title` (at least one character other than a space; a tab-only title passes), `params`,
`provenance` and optional `description` and `controllers`. A published `id@rev` is immutable: a change is a new `rev`.

```json
{
  "id": "gridsim.local.wind-gfl",
  "rev": 1,
  "kind": "gridFollowingInverter",
  "status": "active",
  "title": "Onshore wind, grid-following",
  "params": {
    "deadbandHz": 0.015,
    "droopGainMwPerHzPerMw": { "v": 20, "unit": "MW/Hz/MW", "prov": { "tag": "Synthetic", "note": "Chosen for this example" } }
  },
  "provenance": { "_default": { "tag": "Synthetic", "note": "Example values, not a measured plant" } }
}
```

`library.schema.json` enforces the placeholder rule with `if` / `then` / `else` on `status`: a placeholder has every
parameter value and every controller slot null; an active template has none null (a key is left out instead), and its
controller slots are pinned `templateRef` values. `controllers` has the three slots `exciter`, `pss` and `governor`.
`provenance` requires `_default` and allows per-parameter entries keyed by parameter name.

### Kind specs

A kind spec is one `gridsim-kind/2` file, `schemas/v2/kinds/<kind>.kind.json`, that states what a template of that kind
may hold. Sixteen kinds ship.

| Kind | Holds | Maps to a v1 asset-class kind | Required parameters |
|---|---|---|---|
| `synchronousMachine` | inertia, GENROU reactances and time constants, sequence and step-up data, per-fuel capacity factor | `synchronousMachine` | none |
| `gridFollowingInverter` | fast frequency response, loss-of-mains trips, impedance-scan and RMS parameters, DC store keys | `gridFollowingInverter` | none |
| `gridFormingInverter` | synthetic inertia, ride-through, impedance-scan and RMS parameters, DC store keys | `gridFormingInverter` | none |
| `protection` | relay settings: the v1 protection keys and the generic network relay settings | `protection` | none |
| `lineType` | per-unit, physical and length values of a line or cable, plus the v1 `line` class thermal keys | `line` | none |
| `oltc` | tap-changer settings and the thermal state, the v1 `transformer` class and the network OLTC element | `transformer` | none |
| `transformerType` | per-unit, percent-on-rating and X/R values of a transformer, zero-sequence and winding data | none | none |
| `exciter` | SEXS and IEEE Type 1 excitation settings | none | `model` |
| `pss` | PSS1A stabiliser settings | none | `model` |
| `governor` | the lumped frequency-model response and the TGOV1, IEEEG1 and hydro governor parameters | none | none |
| `gridCodeProfile` | frequency and voltage limits of a grid code | none | none |
| `solverProfile` | power-flow, dispatch and dynamic solver settings | none | none |
| `load` | power factor, Q/P ratio and ZIP coefficients | none | none |
| `storage` | the energy store behind a converter: DC source keys and chemistry presets | none | none |
| `harmonicSpectrum` | harmonic current source magnitude (percent of fundamental) and phase per order 2 to 50 | none | none |
| `site` | the two slots `generation` and `demand` of a named site | none | none |

A kind file has `schema`, `kind`, `title`, `description` and `params`. A parameter spec has `type` (`number`,
`integer`, `enum`, `string` or `boolean`), `required`, `description`, and optionally `unit`, `min`, `max`, `values`,
`markGated` and `codeDefaults`.

```json
"inertiaH": {
  "type": "number", "unit": "s", "min": 0, "max": 20, "required": false, "markGated": true,
  "description": "Inertia constant on the machine base.",
  "codeDefaults": [ { "consumer": "...", "ref": "...", "value": 0 } ]
}
```

`markGated` marks a parameter whose wiring into a consumer needs the project owner's sign-off (frequency, inertia and
dynamics). `codeDefaults` lists the C# value each consumer uses, one entry per consumer, never merged. A `codeDefaults`
value is a number, string or boolean; for a number parameter the exact strings `"+Infinity"` and `"-Infinity"` are also
accepted, because the C# fallback is `double.PositiveInfinity` and JSON has no infinite number. A template value is
never infinite. A `codeDefaults` value is not range-checked against `min` and `max`: it records what the code does, and
a value outside the declared range is a finding for the agreement tests, not something the registry hides.

Nothing at run time reads `codeDefaults`. They are the anti-drift contract between the specs and the code: a test
resolves every entry's `ref` and compares the code value with the entry exactly (see Invariants).

### Template addressing and import patterns

A template is addressed as `id@rev`. Every reference in an element, binding, override, controller slot or profile is a
pinned `templateRef`; an unpinned id there fails the schema. An import is the one place an unpinned id is accepted, and
an import `templates` entry is one of:

| Pattern | Takes |
|---|---|
| `gridsim.exciter.sexs@1` | exactly that revision |
| `gridsim.exciter.sexs` | the highest revision of that id in the file; the resolution is reported as a note |
| `gridsim.exciter.*` | every template whose id starts `gridsim.exciter.` |
| `gridsim.*` | every template in the file |

### The case manifest

`case.schema.json` requires `schema`, `name` (at least one non-space character), `system` and `network`. `system`
requires `baseMva` and `frequencyHz`, both greater than 0, and accepts `dispatch` (boolean; false keeps generator
outputs as written, absent is true). `network` is a non-empty array of relative paths: forward slashes only, no drive,
no leading slash, no empty segment, ending `.json` (`^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*\.json$`). `imports` entries
are `{ "library", "templates" }` with a non-empty `templates` array of the patterns above. `localTemplates` are
library-shaped templates. `bindings` entries are `{ "matchType", "template" }`: an element whose type matches
`matchType` takes the template unless the element names its own `template`; a `matchType` ending `*` is a prefix rule
and an exact rule beats a prefix rule. `overrides` entries require `template`, `params`, `reason` (non-blank) and
`"shadow": "explicit"`, and accept `provenance`; a silent shadow is not possible. `gridCodeProfile` and `solverProfile`
are optional `templateRef` members.

### The network file

`network.schema.json` requires `schema` and holds any of the arrays `buses`, `generators`, `demands`, `lines`,
`transformers`, `oltcs`, `boundaries`, `protections`. The case's network is the union of the files its manifest lists.
Values are bare numbers on network elements; the object form `{ v, unit, prov }` belongs to template values. The
required members are:

| Element | Required | Notable constraints |
|---|---|---|
| bus | `id`, `baseKv` | `baseKv` > 0; `type` in `pq pv slack`; `vm` >= 0; `vminPu`, `vmaxPu` > 0; `zip` needs all six of `ap bp cp aq bq cq` |
| generator | `name`, `bus` | `type` matches `^([a-z][a-z0-9-]*)?$` (the empty string is a unit with no technology); `capacityMva` >= 0; `capacityFactor` in [0, 1]; `voltageSetpoint` > 0; `role` in `pv slack pq`; `qMinMvar` a number or `"-Infinity"`; `qMaxMvar` and `pMaxMw` a number or `"Infinity"`; `converter` block; `subtransientXdPu` and `negativeSeqXPu` > 0 |
| demand | `name`, `bus`, `pMw` | `powerFactor` in (0, 1] |
| line | `name`, `from`, `to`, `rPu`, `xPu` | `circuits` integer >= 1; `equipment` in `ohl cable`; `tapRatio` > 0; ratings >= 0 |
| transformer | `name`, `from`, `to`, `rPu`, `xPu` | `tapRatio` > 0; `windings` needs `from` and `to` in `Delta WyeGrounded WyeUngrounded`; `neutralXPu` members >= 0 |
| oltc | `transformer`, `controlsBus` | `targetVpu`, `minTap`, `maxTap`, `stepPu` > 0; `deadbandPu` >= 0 |
| boundary | `name`, `limitMw` | `branches` entries need `branch`; `direction` is `1` or `-1` |
| protection | `name`, `template`, `monitorBus` | `capacityMw` >= 0 |

Every element name except a bus name holds at least one non-blank character; a bus `name` is any string. The winding connection names are exactly the member names of
`GridSim.Core.Model.WindingConnection`.

### Asset overlay (gridsim-assets/2)

An asset overlay describes physical assets the electrical model does not hold, and points at the elements of the case
they belong to. It is **carried, not used by the solver**: the authored buses, lines, transformers and generators stay
the electrical truth, and a case loads to the same model and results with or without its overlay.

**The document.** A manifest lists its overlay files in the optional `assets` member (a non-empty array of relative
paths, the same form as `network`; each file inside the case folder and listed once). Each file is a
`gridsim-assets/2` document (`assets.schema.json`) holding `records`, each record:

| Member | Required | Meaning |
|---|---|---|
| `id` | yes | unique across every overlay file of the case; a letter or digit, then letters, digits, `.`, `_`, `:` or `-` |
| `kind` | yes | an asset overlay kind |
| `parent` | no | the id of the record this one sits under; nesting is by parent, never by inner arrays |
| `refs` | yes | role to target, each target an object with one member naming its type: `{ "bus": 12 }`, `{ "line": "L1" }`, `{ "transformer": "T1" }`, `{ "generator": "G1" }`, `{ "demand": "D1" }` or `{ "record": "sub.north" }` |
| `params` | yes | scalar values, bare or in the `{ v, unit, prov }` object form |
| `prov`, `note` | no | the record's own provenance and a free-text note |

Buses are addressed by id; lines, transformers, generators and demands by name, exactly as the network files spell
them.

**The kinds.** The overlay kinds are kind specs (`gridsim-kind/2`) in their own folder, `schemas/v2/kinds/overlay`,
loaded apart from the template kinds by `OverlayKindSet`, so a library template can never be of an overlay kind and an
overlay record can never be of a template kind. Each carries an `overlay` block (`kind.schema.json`): `refs` (each
role's `target`, `required` and, for a record target, its `recordKinds`), `parentKinds` (empty = top level only) and
`parentRequired`. A new component kind is therefore data only. The thirteen kinds are `substation`, `busbarSection`
(collapses to an authored bus), `switchingDevice` (`device` breaker, disconnector or earthSwitch, and `normalOpen`),
`conductorType`, `towerType` with its `conductorPosition` children, `lineRoute` (over an authored line) with its `span`
and `earthWire` children, `soilZone` (`ohm.m`), `earthPath`, `transformer3W` (over the authored star bus and its three
two-winding legs, with the pairwise impedances on a common MVA base) and `hvdcLink` (over the authored converter
generators).

**The checker.** `V2AssetChecker` checks a case's overlay files as one set and lists every problem, each message naming
the record id and each JSON pointer the field. First the schema; then, when every file has the right shape: the kind
is an overlay kind; `params` against the kind spec (`KindSpecRegistry.CheckParams`, unchanged: unknown or missing
parameter, type, unit, range); ids unique across the files; the parent exists, is of one of the kind's `parentKinds`
(a top-level kind takes none, a kind that requires one has one) and no parent chain loops; every role is declared by
the kind with the target type it gives, every required role is present, and every reference resolves: another record
by id (of one of the role's `recordKinds`, never itself), and a bus, line, transformer, generator or demand of the case
(a name two elements of one type share is refused as ambiguous). `validate` runs it on every overlay file a manifest
lists, resolving against that manifest's network files; an overlay file given alone is checked without a case, so its
references to network elements are not resolved.

**The carry.** `V2CaseLoader` refuses a case whose overlay fails the checker, with every problem listed. It reads the
overlay only after the model is complete, into `V2CaseLoadResult.Overlay` (a `CaseAssetOverlay`), and adds the note
`overlay: N records carried, not used by the solver`. Nothing in the overlay reaches `JsonGridLoader.Build` or any
solver. A case without `assets` loads exactly as before: the same model, the same notes, and no overlay. A build older
than Phase 5a refuses the `assets` member as unknown. Two tests hold the rule: a pin test loads every bundled case (by
its v2 projection) and two fixtures with a full overlay (every kind, every reference type) and without one, and
requires the same model in every field, the same power flow and the same frequency run, bit for bit; a guard test
refuses any GridSim.Core source outside the overlay namespace and the case editor that names an overlay type, apart
from the loader's result and the document validator.

## Algorithm

### The validator

`SchemaValidator.Validate(instance, schemaFile)` applies the root schema of a file to a value and returns every error in
document order. `Apply` evaluates one schema node against one value in this order: `$ref` (apply the target to the same
value), `type`, `enum`, `const`, numeric bounds (numbers), `pattern` (strings), array keywords (arrays), object keywords
(objects), `anyOf`, `oneOf`, then `if` with `then` or `else`.

```
type          integer accepts any number with no fractional part (1.0 passes; the loader does not, see Limits);
              "number" accepts any number
enum, const   compared with JsonElement.DeepEquals (numbers by value)
minimum       error when x <  min        maximum  error when x > max        exclusiveMinimum  error when x <= bound
minItems      error when n < minItems
required      one error per absent name, at the object
properties    each present member is applied to its own property schema, at its own pointer
additionalProperties
              false: one error per unknown member listing the allowed names; schema: applied to each unlisted member;
              it sees only the properties written in the same schema object, never those of a sibling $ref or if/then
anyOf         passes when any branch has no error; else one error plus every branch's errors, labelled [anyOf alternative i]
oneOf         passes when exactly one branch has no error; else "matches none" or "matches N oneOf alternatives"
if            never reports an error itself; it selects then (when it passes) or else (when it fails)
```

Numeric keywords apply only to numbers, `minItems` only to arrays, `pattern` only to strings and `properties` /
`required` / `additionalProperties` only to objects. A value of another type is left to `type` to judge. A numeric
literal outside the range of a double is refused rather than compared. Termination follows from the load-time cycle
check plus the finite depth of the document. A pointer token escapes `~` as `~0` and `/` as `~1`; the empty root
pointer prints as `(root)`.

`SchemaValidator.ValidateOrThrow` and `ThrowIfAny` raise one `InvalidDataException` with the document name, the error
count and every error on its own line.

### Checking a template's parameters against its kind

`KindSpecRegistry.CheckParams(kind, params, placeholder, pointer)` returns every problem:

```
unknown kind                      error, stop
params not an object              error, stop
for each required parameter of the kind: absent -> error      (placeholders too: a placeholder lists the required
                                                               keys with null values, so its shape is explicit)
for each member m of params:
    m not declared by the kind    error, next member
    value null                    error when active; fine when placeholder; next member
    value not null, placeholder   error, next member
    object form                   unit written: error if the parameter has no unit, error if it differs from the
                                  spec's unit by ordinal comparison; "v" absent: error, next member; v is judged next
    type check                    number: finite number; integer: whole number (1.0 passes); enum: a listed string;
                                  string: a string; boolean: true or false
    range                         number and integer: x < min or x > max is an error; both bounds inclusive
```

### Loading and checking kind specs

`KindSpecRegistry.LoadDirectory` loads every `*.kind.json` in a folder. Each file is validated against
`kind.schema.json`; only a schema-valid file goes on to the semantic layer. The semantic layer refuses: a number or
integer without a unit; a unit on a parameter that is not a number or integer; `min` or `max` on a parameter that is not
a number or integer; `min` above `max`; an enum without values; `values` on a parameter that is not an enum; a repeated
enum value; a `codeDefaults` value of the wrong type or, for an enum, not among its values; a file not named
`<kind>.kind.json`; a kind declared twice. Every problem across every file is collected and thrown together. An empty
folder gives an empty registry, and a later check then fails on any kind it needs.

### Checking library files

`V2LibraryChecker.Check` validates a set of `gridsim-library/2` documents together and returns every error with its
file. Layer 1 validates each document against `library.schema.json`; if anything fails, layer 2 is skipped, because it
assumes the shape the schema guarantees. Layer 2 runs across the whole set:

1. Index every template by `id@rev`; a repeat is an error.
2. For each template, run `CheckParams` against its kind.
3. Each per-parameter provenance key other than `_default` must name a parameter the template sets.
4. Each non-null controller slot must resolve to a template in the set, must not be a placeholder, and must be a
   template of the slot's own kind (the `exciter` slot to an `exciter` template, and so on).

### `validate` and `V2DocumentValidator`

`gridsim validate <file.json|folder>... [--schemas <dir>]` runs `V2DocumentValidator.ValidateFiles` and adds no rule of
its own. It reads each file, takes the `schema` tag and routes the document:

| Tag | Check |
|---|---|
| `gridsim-case/2` | `case.schema.json`; when the manifest is schema-valid, each listed network file (relative to the manifest) must exist, and one the caller did not list is then checked against `network.schema.json` and reported after the files given |
| `gridsim-network/2` | `network.schema.json` |
| `gridsim-library/2` | all library files given are checked together by `V2LibraryChecker` and each error is returned to its own file's report |
| `gridsim-kind/2` | `kind.schema.json` |

A file that is not JSON, a document with no `schema` tag (a v1 case file, for example) and a document with an unknown
tag are reported as errors on that file; nothing throws for a bad document. Manifest `imports` are not resolved,
because they name files relative to a data root a lone manifest does not know; pass the library files to check them. A
folder argument is searched recursively for `*.json`, in ordinal order. The verb prints `OK <path> (<tag>)` or
`FAIL <path> (<tag>): N error(s)` followed by each error as `<pointer>: <message>`, then
`validate: K of N document(s) valid (schemas: <dir>)`. Exit 0 when every document is valid, 3 when any is not, 1 when a
path does not exist, no `.json` file is found or the schemas cannot be found or loaded, 2 for a usage error. The schemas
are `--schemas <dir>`, else `schemas/v2` beside the program, else the nearest `schemas/v2` above the program folder or
the current folder. Verb detail: [validate](<../manual/0019 CLI reference.md#validate>).

### The case loader

`V2CaseLoader.LoadWithNotes(manifestPath, schemasDir, dataRoot, dispatch)` loads a `gridsim-case/2` manifest with its
network files and library imports into a `GridModel`. It runs the stages below in order. A stage that finds problems
throws one `InvalidDataException`; every problem of that stage is listed, up to 50, then `... and N more`.

```
0  manifest tag      file must carry "schema": "gridsim-case/2"                       (refused by name, no stage list)
1  schema checks     V2DocumentValidator on the manifest and its network files        "the schema checks"
2  network file list each file lies inside the manifest's folder and is listed once   "the network file list"
3  imports           data root given and present; each library file inside the data
                     root and existing                                                 "the import list"
                     library files validated as one set (V2LibraryChecker)             "the imported library files"
                     each import pattern matches at least one template; an unpinned
                     id resolves to its highest revision and is noted                  "the import patterns"
4  local templates   CheckParams per template; provenance keys name parameters; a
                     local id may not equal an imported id (any revision); id@rev
                     unique                                                            "the local templates"
5  controllers       local templates' controller references resolve, are not
                     placeholders, and target the slot's kind                          "the template controller references"
6  overrides         see below                                                         "the overrides"
7  network elements  map every element to its v1 DTO, resolving template references;
                     bind rules; bus ids unique across files; boundary names unique   "the network elements"
8  asset classes     every active template of a modelled kind becomes an asset class   "the templates that become asset classes"
9  profiles          gridCodeProfile and solverProfile refused                         "the case profiles"
10 build             JsonGridLoader.Build, the v1 model builder; a builder refusal is
                     re-thrown with the v1 file names translated
```

**Overrides.** An override must name a template the case can see (local or imported) that is active, must set at least
one parameter, and a parameter of a template may be overridden once across all overrides. The merged parameters (the
template's own, in order, with the override's values replacing or appended) pass `CheckParams` again. An overridden
value takes the override's `provenance`; with none given it takes `CuratedPlaceholder` with the note
`[VERIFY] set by an override in case '<case name>': <reason>`, because its source is not stated.

**Template references.** An element's `template`, a binding's `template` and a protection's `template` must resolve to a
template of the case, be active, and be of a kind that maps to a v1 asset-class kind:

| v2 kind | v1 asset-class kind |
|---|---|
| `gridFollowingInverter` | `gridFollowingInverter` |
| `gridFormingInverter` | `gridFormingInverter` |
| `oltc` | `transformer` |
| `lineType` | `line` |
| `protection` | `protection` |

A template of any other kind (`exciter`, `governor`, `storage`, `site`, `transformerType` and the rest) is validated and
visible to the case but is not an asset class, and binding an element to it is refused.

**Asset classes.** Local templates come first in manifest order, then imported templates in import order. A local
`gridsim.local.NAME@1` keeps the class name `NAME`, the rule the v1 projector uses so projected cases keep their class
names; every other template is named by its `id@rev`. The class holds the template's numeric parameters; an object-form
value contributes its `prov` as that parameter's provenance. The template's `_default` provenance is kept except when it
equals v1's own fallback (`CuratedPlaceholder`, `[VERIFY] unspecified provenance`), which is dropped so a projected v1
class is the v1 class. A template that has controller references or a non-numeric parameter cannot become an asset class
and is refused.

**Fault sequence data.** A generator's `subtransientXdPu` (X''d)
and `negativeSeqXPu` (X2), both pu on the unit rating, and a transformer's `windings` (`from` and `to`: `Delta`,
`WyeGrounded` or `WyeUngrounded`), `r0Pu` / `x0Pu` (zero-sequence leakage impedance, pu on the case base) and
`neutralXPu` (`from` / `to`, neutral earthing reactance, pu on the case base) reach the model
(`Generator.SubtransientXdPu`, `NegativeSeqXPu`; `Branch.FromWinding`, `ToWinding`, `R0`, `X0`, `FromNeutralXPu`,
`ToNeutralXPu`), so `fault`, `iec60909` and `protection-check --fault-type slg|dlg` work from a v2 case that supplies
the windings. Absent fields keep the model defaults (X''d synthesised at 0.20 pu, X2 = X''d, transformer Z0 = Z1, no
neutral reactance), and a case that gives none of them loads to the same model as before, bit for bit. The loader
refuses, naming the element: an X''d or X2 on a converter-interfaced unit (IEC 60909-0:2016 models it as a current
source, `converter.faultCurrentPu`, so the value would never be read); `r0Pu`, `x0Pu` or `neutralXPu` without
`windings` (the zero-sequence network would treat the transformer as a line); `windings` with one side missing;
`r0Pu` without `x0Pu`; `x0Pu` not positive or `r0Pu` negative; a `neutralXPu` on a side whose winding is not
`WyeGrounded` (it has no earthed neutral). v1 has no keys for these fields and is unchanged; the v1 writer reports them
as content v1 cannot carry. Network elements carry no per-value provenance in v2: the values are the case author's, and
the case's own description is the place to say where they came from.

**Content refused by name.** v2 content that the model cannot carry is refused, never dropped: line and transformer
`type`, a line or transformer with `"inService": false`, and a `gridCodeProfile` or `solverProfile`. Descriptions
are text and are accepted. Limit values follow the v1 rule: `qMinMvar` is a number or `"-Infinity"`, `qMaxMvar` and
`pMaxMw` a number or `"Infinity"`, and no other string loads.

**Notes.** A note is information, never a problem. The only note is the revision an unpinned import resolved to. The CLI
prints each on standard error as `note: <manifest file>: ...`, so standard output stays the verb's own report.

### What loads a v2 case

The CLI loads a manifest wherever a verb resolves a `<case>` argument through the shared case resolver and the argument
ends in `.json` (`LoadNamed` in `src/GridSim.Cli/Program.cs` calls `Gated.V2Case`). `Gated.V2Case` finds the schemas as
`validate` does, takes library imports relative to the `data` folder beside the program, applies the licence bus-limit
gate like any other load (exit 2 above the tier limit), and turns a content failure into a load failure: exit 1 from the
verb, or a LOAD FAIL row in a batch verb. Checked on the build: `pf`, `dcpf`, `losses`, `nminus1`, `screen`, `domains`,
`taps`, `convert` (as its input), and `trip` (with the generator name as a second argument).

The following do not load a manifest:

- the bare live view (`gridsim <manifest.json>`): the argument is not a verb or a built-in name, exit 2;
- `--dir <folder>`, which takes a v1 case folder only;
- `trip <manifest.json>` with no second argument, which reads the path as a generator name;
- `export`, which does not recognise a `.json` argument as a case: a `.json` token is taken as the output file, so
  `export <manifest.json>` writes the default case to that path.

`convert` writes v2 when its output ends in `.json` (`V2CaseWriter.Write`): a `gridsim-case/2` manifest at that path and
`<stem>.network.json` beside it, never overwriting either. Every number a power flow reads is written exactly (shortest
round-trip form) and in model order (buses, the whole generator fleet with out-of-service units, branches), the
system block says `"dispatch": false`, unlimited limits are the strings `"Infinity"` / `"-Infinity"`, and a bus's demand
is one row with its exact `Pd` / `Qd`. Branches are written in place as lines (a transformer as a line with
`isTransformer`, `tapRatio` and `phaseShiftDeg`), except the trailing run from the first branch with winding data, written
as `transformers` elements so the fault sequence data (generator X''d / X2, windings, zero-sequence impedance, neutral
reactances) is carried; winding data that cannot be placed without reordering is reported in the notes. The asset-dynamics
and protection bundles are written as case-local templates `gridsim.local.<class name>@1` in library order, the binding
rules as `bindings`, each element's class as its `template`, and the placed devices as the network file's `protections`;
a bundle the writer cannot represent exactly is left out whole, with a note. The output is byte-stable (fixed member
order, two-space indentation, `\n` line ends and a final newline). `convert` then reads the written case back through
`V2CaseLoader` and requires the same solve bits and, for the sequence data, `V2CaseWriter.FirstSequenceDifference` to find
none (exit 3 naming the first difference).

The case editor opens a v2 manifest or network file (`GridSim.SchemaEditor <x.case.json|x.network.json>`, ch. 33 of the
user manual, [The case editor](<../manual/0033 The case editor.md>)). Its save gate validates against the embedded
v2 schemas and `V2CaseChecks` (network files present and inside the folder; bus ids unique; every bus reference
resolves; branch and boundary names unique; ZIP triples sum to 1). When those pass and the manifest imports nothing, the
gate also runs `V2CaseLoader` over a staged copy of the case and reports its verdict as a note; a v2 save the loader
refuses is refused. A case that imports library templates is not loaded at save time, because imports resolve against
the data folder of the program that loads the case.

### The v1 to v2 projector

`tests/GridSim.Tests/V2/V1Projector.cs` is a test-only projector between a v1 case folder and its v2 form. It reads a
v1 folder into the loader's own DTOs, writes a v2 manifest plus one network file, and maps a v2 pair back to v1 DTOs,
refusing any v2 content v1 cannot carry. No production code projects.

- An absent v2 member means exactly the v1 DTO default. The projector omits a value that equals the default bit for bit
  and the back-mapper starts from a fresh DTO.
- `system.json` becomes `name` and `system`; `asset-classes.json` becomes `localTemplates` (id
  `gridsim.local.<class name>`, revision 1, active); `asset-bindings.json` becomes `bindings`; every other file becomes
  an array of the one network file.
- A v1 class name used by an element becomes `gridsim.local.<class name>@1`. `dynamicsClass` becomes `template` on
  generators, lines and transformers, and protection `class` becomes `template`.
- `unitTransformerXPu` stays on the generator, not inside `converter`.
- v1 asset-class kinds map one to one onto the v2 kinds `synchronousMachine`, `gridFollowingInverter`,
  `gridFormingInverter`, `protection`, `lineType` (v1 `line`) and `oltc` (v1 `transformer`), and the table covers every
  member of `AssetKind`.
- A v1 class with no `_default` provenance receives v1's own fallback (`[VERIFY] unspecified provenance`) because a v2
  template requires `_default`; the back-mapper drops exactly that entry.

## Parameters and defaults

| Name | Value | Meaning | Source symbol |
|---|---|---|---|
| Library tag | `gridsim-library/2` | tag every library file carries | `V2LibraryChecker.SchemaTag` |
| Kind tag | `gridsim-kind/2` | tag every kind spec carries | `KindSpecRegistry.SchemaTag` |
| Case tag | `gridsim-case/2` | manifest tag | `V2CaseLoader.CaseSchemaTag` |
| Network tag | `gridsim-network/2` | network file tag | `V2CaseLoader.NetworkSchemaTag` |
| Known tags | the four tags above | tags `validate` routes | `V2DocumentValidator.KnownTags` |
| Schema dialect | `https://json-schema.org/draft/2020-12/schema` | only `$schema` a schema file may declare | `JsonSchemaSubset.Draft202012` |
| Schema file suffix | `.schema.json` | files `LoadDirectory` picks up | `JsonSchemaSubset.SchemaFileSuffix` |
| Kind file suffix | `.kind.json` | file name a kind spec must carry after its kind | `KindSpecRegistry.KindFileSuffix` |
| Kind schema file | `kind.schema.json` | schema kind specs are validated against | `KindSpecRegistry.KindSchemaFile` |
| Library schema file | `library.schema.json` | schema library files are validated against | `V2LibraryChecker.LibrarySchemaFile` |
| Pattern timeout | 1 s | match timeout of every compiled schema pattern | `JsonSchemaSubset.CompilePattern` |
| Local id prefix | `gridsim.local.` | a local `NAME@1` keeps class name `NAME` | `V2CaseLoader.LocalIdPrefix` |
| Problems listed | 50 | most problems one refusal lists | `V2CaseLoader.MaxListedProblems` |
| Reader options | comments skipped, trailing commas allowed, duplicate members refused | how every v2 document is parsed | `JsonSchemaSubset.ReaderOptions` |
| Dispatch loss allowance | 0.02 (unit `1`) | demand uplift when the dispatcher sets the marginal plant (v1 and v2 alike) | `Dispatcher.Balance`, parameter `lossAllowance` |

## The inconsistency register

Where the code disagrees with itself about a constant, each disagreeing value is kept with its consumer in the kind
spec's `codeDefaults`. None is merged or ruled on in the specs.

| Quantity | Kind and parameter | Values in code |
|---|---|---|
| Nuclear inertia H | `synchronousMachine` `inertiaH` | 5.0 s (GDA `FuelMap.Classify`) and 4.0 s (SyntheticGb transmission fleet: Torness, Heysham, Sizewell B, Hinkley Point) |
| Grid-forming virtual reactance | `gridFormingInverter` `virtualReactancePu` | 0.20 (impedance placeholder) and 0.15 (`RmsConverterParameters`); 0 where `GridFormingImpedance.FromAssetClass` reads an absent value |
| Governor droop | `governor` `droopPu` | 0.05 (`DynamicsConfig.DroopPu`) and 0.04 (`EnergyEconomicsConstants.GovernorDroopFraction`) |
| OLTC tap step | `oltc` `tapStepPu` | 0.0125 and 0.00625 (SyntheticGb GSP supergrid transformer, half step) |
| OLTC deadband | `oltc` `tapDeadbandPu` | 0.01 and 0.015 (SyntheticGb 132/33 kV primary) |
| Load power factor | `load` `powerFactor` | 0.97 (`JsonDefaults.LoadPowerFactor`) and 0.98 (`GdaBuildDefaults.DemandPowerFactor`) |
| PLL damping ratio | `gridFollowingInverter` `pllDampingRatio` | 0.707 (`ConverterDefaults.PllDampingRatio`, the one PLL default) and 1.0 (`LinearInverterModel.ForGfl` delay stand-in only, when the bandwidth comes from the detection latency) |
| Capacity factor by fuel | `synchronousMachine`, `gridFollowingInverter`, `storage` `capacityFactor` | GDA `FuelMap`, Core `Dispatcher` and SyntheticGb embedded generation each carry their own: ccgt 0.45 and 0.50, gas 0.35 and 0.50, hydro 0.35 and 0.40, biomass 0.65 and 0.70, other-type fallback 0.30 and 0.40, solar 0.10, 0.15 and 0.25, onshore wind 0.30, 0.30 and 0.35, interconnector 0.5 and 0.6, storage 0.05, 0.20 and 0.10 (the `Dispatcher` 0.20 arm is shared with pumped storage), pumped storage 0.10 and 0.20 |
| Frequency deadband | `gridFollowingInverter`, `gridFormingInverter` `deadbandHz` | 0.015 Hz (grid-following) and 0 (grid-forming) |
| Thermal derate at full state | `lineType`, `oltc` `derateAtFullThermal` | 0.1 (`LineThermalDynamics`) and 0.0 (`TransformerDynamics`) |
| Thermal time constant | `lineType`, `oltc` `thermalTauMin` | 15 min (line) and 20 min (transformer) |
| RoCoF trip | `protection`, `gridCodeProfile`, inverters | 1.0 Hz/s (generic 81R relay), 0.125 Hz/s (`FrequencyLimits.RocofLimitLegacyHzPerS`), 1.0 Hz/s (`RocofLimitModernHzPerS`, ALoMCP) and `+Infinity` (inverter fallback) |
| Vector shift trip | `protection`, `gridFollowingInverter` `vectorShiftTripDeg` | 12 deg (generic 78 relay) and `+Infinity` (inverter fallback) |
| Subtransient X''d | `synchronousMachine` `xdppPu` | 0.20 (fault-study fallback, harmonic fallback); the parameter description records that GENROU synthesis from the CLI's synthetic X'd of 0.30 gives 0.8 x 0.30 = 0.24 |

`V2KindSpecCodeDefaultTests.InconsistencyRows` pins 34 rows of the register value for value: for each row exactly one
entry names the consumer, it carries the value, and the code agrees bit for bit. Several register entries have no row:
the gas, hydro, biomass, wind and interconnector capacity factors, storage 0.10, pumped storage 0.10 and 0.20 on the
machine kind, the modern RoCoF trip and the `+Infinity` inverter fallbacks, the vector-shift trip, the GFM virtual
reactance and the subtransient X''d. A value that moves in either direction fails the
test and has to be ruled on deliberately.

## The placeholder sites

`data/library/placeholders/unmodelled-sites.json` is a `gridsim-library/2` file of 15 placeholder templates of kind
`site`. Each is a named site with the two slots `generation` and `demand`, and every value is deliberately null because
no public dataset covers the site. Each carries `CuratedPlaceholder` provenance with the note
`Deliberately empty: not in any public dataset [VERIFY]`. The 15 sites, each at revision 1 with the id prefix
`gridsim.placeholder.site.`:

| Id suffix | Title |
|---|---|
| `Nuclear-Sub` | Nuclear-Sub |
| `Carrier` | Carrier |
| `PCI-Datacentres` | PCI-Datacentres |
| `Hyperscale-Datacentre` | Hyperscale-Datacentre |
| `AI-Training-Campus` | AI-Training-Campus |
| `Naval-Base-Shore-Supply` | Naval-Base-Shore-Supply |
| `Military-Airfield` | Military-Airfield |
| `SMR` | SMR (small modular reactor) |
| `Private-Wire-Network` | Private-Wire-Network |
| `Behind-The-Meter-BESS-Aggregate` | Behind-The-Meter-BESS-Aggregate |
| `Electrolyser-Cluster` | Electrolyser-Cluster |
| `Arc-Furnace-Steelworks` | Arc-Furnace-Steelworks |
| `Rail-Traction-Supply` | Rail-Traction-Supply |
| `Megawatt-EV-Charging-Hub` | Megawatt-EV-Charging-Hub |
| `Crypto-Mining-Site` | Crypto-Mining-Site |

The code does not model these sites. A placeholder template holds no values, so no solver reads one; the rules below
keep it that way.

- A placeholder has every parameter value null (schema, and `CheckParams` refuses any non-null value with
  `a placeholder template must have every value null.`) and lists the kind's required keys.
- An active template has no null value.
- A placeholder is never a controller target (`V2LibraryChecker` and the loader).
- A placeholder is never bound to an element or a binding rule, and its parameters are never overridden (loader).
- A placeholder's provenance tag is `CuratedPlaceholder` with `[VERIFY]` in the note (`common.schema.json`).
- A case may import a placeholder template; importing it gives the case nothing to bind, and the `dcpf` and `pf`
  captures in the worked example import two of them.
- When a site becomes active, each slot holds an `id@rev` reference to a machine template (`generation`) or a load
  template (`demand`); the kind checks that the slot is a string, and a loader does not resolve it.

`V2LibraryFilesValidateTests` validates every library file under `data/library` as one set.

## Rules and refusal messages

Messages are quoted as written. `<...>` marks a value filled in at run time.

### Schema set load (`JsonSchemaSubset`)

| Rule | Message |
|---|---|
| Keyword outside the subset | `<file> at '<pointer>': unsupported schema keyword '<kw>'. The v2 validator supports only: <list>. An unsupported keyword is refused rather than silently ignored, so a schema can never be half-enforced.` |
| Schema is not an object | `<file> at '<pointer>': a schema must be a JSON object, found <kind>.` |
| Bad `$schema` | `... '$schema' must be "https://json-schema.org/draft/2020-12/schema".` and `'$schema' is only allowed at a file root.` |
| Bad `$id` | `... '$id' must equal the file name "<file>", so a cross-file $ref by name is unambiguous.` and `'$id' is only allowed at a file root (embedded resources are not supported).` |
| `$ref` problems | `'$ref' must be a non-empty string.`; `$ref '<ref>' names file '<file>', which is not in the schema set (<files>).`; `... has a fragment that is not a JSON pointer (anchors are not supported; write '#/$defs/name').`; `... does not resolve to a schema: there is no schema at '<pointer>' in '<file>'.` |
| `then` / `else` without `if` | `'<kw>' without a sibling 'if' would be silently ignored; add the 'if' or remove it.` |
| Shape of other keywords | `'<kw>' must be an object of schemas.`; `'<kw>' must be a non-empty array of schemas.`; `'enum' must be a non-empty array.`; `'enum' lists the same value twice (entries <i> and <j>).`; `'<kw>' must be a finite number.`; `'minItems' must be a non-negative integer.`; `'pattern' must be a string.`; `'pattern' is not a valid regular expression: <reason>`; `'<kw>' must be a string.` |
| `type` problems | `unknown type name '<name>'.`; `'type' must be a type name or a non-empty array of type names.`; `type '<name>' is listed twice.` |
| `required` problems | `'required' must be an array of strings.`; `'required' lists '<name>' twice.` |
| In-place cycle | `$ref cycle that never consumes input (validation would never terminate): <file> at '<pointer>' -> ...` |
| File problems | `<file>: not valid JSON: <reason>`; `<file>: a schema file name must be a plain file name (no '/', '\' or '#').`; `Schema directory not found: <dir>`; `Schema directory <dir> holds no *.schema.json files.` |
| Summary | `Invalid v2 schema set (<n> problem(s)):` then each problem on its own line |

### Validation (`SchemaValidator`)

| Rule | Message |
|---|---|
| `type` | `expected type <names>, found <kind>.` (a whole number reports as `integer`, other numbers as `number`) |
| `enum` | `value <v> is not one of the allowed values <list>.` |
| `const` | `value <v> must be exactly <c>.` |
| `minimum`, `maximum`, `exclusiveMinimum` | `<x> is below the minimum <m>.`; `<x> is above the maximum <m>.`; `<x> must be strictly greater than <m>.` |
| Out of double range | `number <literal> is outside the range of a double.` |
| `pattern` | `string <v> does not match the pattern <p>.` |
| `minItems` | `array has <n> item(s); at least <m> required.` |
| `required` | `missing required member '<name>'.` |
| `additionalProperties: false` | `unknown member '<name>' is not allowed here. Allowed: <names>.` |
| `anyOf` | `value <v> matches none of the <n> anyOf alternatives:` then each branch's errors prefixed `[anyOf alternative <i>]` |
| `oneOf` | `value <v> matches none of the <n> oneOf alternatives:` with branch errors; or `value <v> matches <k> oneOf alternatives (<indices>); exactly one is required.` |
| Document | `<name>: <n> validation error(s):` then each error as `<pointer>: <message>` |
| Wrong file | `Schema file '<file>' is not in the loaded schema set (<files>).` |

A container value prints as `(an object)` or `(an array)`; a scalar prints in full.

### Kind specs (`KindSpecRegistry`)

| Rule | Message |
|---|---|
| Number or integer without unit | `<file>: /params/<name>: a number parameter must declare its unit.` (or `integer`) |
| Unit on another type | `... only number and integer parameters may declare a unit.` |
| `min` or `max` on another type | `... only number and integer parameters may declare min or max.` |
| `min` above `max` | `... min <a> is above max <b>.` |
| Enum values | `... an enum parameter must list its values.`; `... only an enum parameter may list values.`; `... values lists the same value twice.` |
| `codeDefaults` value | `<file>: /params/<name>/codeDefaults/<i>: value <v> is not a finite number.` (or `is not a whole number.`, `is not one of the allowed values: <list>.`, `is not a string.`, `is not true or false.`) |
| File name | `<file>: declares kind '<kind>' but must then be named '<kind>.kind.json'.` |
| Duplicate kind | `<file>: kind '<kind>' is already declared in <file>.` |
| Unknown kind | `Unknown kind '<kind>': no <kind>.kind.json is loaded (known: <list>).` |
| Missing pieces | `Kind spec directory not found: <dir>`; `The schema set has no kind.schema.json, so kind specs cannot be validated.` |
| Summary | `Invalid v2 kind spec(s) (<n> problem(s)):` |

### Template parameters (`CheckParams`)

| Rule | Message (at the parameter's pointer, or at its `/unit` or `/v` member for the object form) |
|---|---|
| Unknown kind | `unknown kind '<kind>': no <kind>.kind.json is loaded.` |
| `params` not an object | `params must be an object, found <kind>.` |
| Missing required | `kind '<kind>' requires parameter '<name>', which is missing.` |
| Unknown key | `unknown parameter '<name>' for kind '<kind>' (declared: <list>).` |
| Null in an active template | `an active template may not have a null value; set it or leave the key out.` |
| Value in a placeholder | `a placeholder template must have every value null.` |
| Object form without `v` | `the object form needs a 'v' member.` |
| Unit on a unitless parameter | `parameter '<name>' is a <type> and takes no unit, but '<unit>' is written.` |
| Unit differs | `unit '<unit>' does not match the kind's unit '<spec unit>' for '<name>' (no conversion is done; write the spec's unit).` |
| Wrong type | `value <v> is not a finite number.` / `is not a whole number.` / `is not one of the allowed values: <list>.` / `is not a string.` / `is not true or false.` |
| Range | `<x> <unit> is below the minimum <m> for '<name>'.` and `<x> <unit> is above the maximum <m> for '<name>'.` |

### Library set (`V2LibraryChecker`)

| Rule | Message |
|---|---|
| Duplicate `id@rev` | `template <id@rev> is declared twice (first in <file> at <pointer>).` |
| Provenance key | `provenance is given for '<key>', which is not a parameter of this template.` |
| Unresolved controller | `controller reference <ref> does not resolve to any template in the loaded libraries.` |
| Placeholder target | `controller reference <ref> is a placeholder template (<file> at <pointer>); a placeholder can never be a controller target.` |
| Wrong slot kind | `the '<slot>' slot must reference a '<slot>' template, but <ref> is kind '<kind>'.` |
| Throwing form | `v2 template library (<n> file(s)): <e> validation error(s):` |

### `validate` (`V2DocumentValidator` and the verb)

| Rule | Message |
|---|---|
| Missing path | `error: validate: '<path>' does not exist.` (exit 1) |
| No files | `error: validate: no .json files found in the paths given.` (exit 1) |
| No schemas | `error: validate: cannot find the v2 schemas (looked for schemas/v2 beside the program and above it, and above the current folder); pass --schemas <dir>.`; with `--schemas <dir>`, `error: validate: cannot find the v2 schemas in '<dir>'; pass --schemas <dir>.` (exit 1) |
| Schema set invalid | `error: validate: the schema set in '<dir>' cannot be loaded: <reason>` (exit 1) |
| Not JSON | `(root): not valid JSON: <file>: <reason>`, with the tag shown as `no tag` |
| No tag | `/schema: not a GridSim v2 document: no "schema" tag (expected one of gridsim-case/2, gridsim-network/2, gridsim-library/2, gridsim-kind/2).` |
| Unknown tag | `/schema: unknown schema tag '<tag>' (expected one of <list>).` |
| Missing network file | `/network/<i>: network file '<rel>' does not exist (looked for <path>).` |

### Case load (`V2CaseLoader`)

A refusal at stages 1 to 9 begins `<manifest file>: JSON v2 case refused at <stage>: <n> problem(s):` (stage names in the
table above) and lists each problem as `<file>: <pointer>: <message>`. The other refusals in this table carry their
own text: a manifest that does not parse gives `<file>: <reason>`, and the model-builder refusal begins
`<manifest>: the case fails a check of the model builder`.

| Rule | Message |
|---|---|
| Manifest missing | `JSON v2 case manifest not found: <path>`; schemas folder: `JSON v2 schema folder not found: <dir>`; data root: `JSON v2 data root not found: <dir>` |
| Not a manifest | `<file>: not a JSON v2 case manifest: it has no "schema": "gridsim-case/2" tag (a v1 case is a folder of JSON files; give the folder).` and `<file>: not a JSON v2 case manifest: its "schema" is '<tag>', not 'gridsim-case/2'.` |
| Network file outside | `<manifest>: /network/<i>: network file '<rel>' lies outside the case folder (<dir>).` |
| Network file repeated | `<manifest>: /network/<i>: network file '<rel>' is listed more than once.` |
| No data root | `<manifest>: the case imports library templates, whose files are named relative to the data root, but no data root was given.` |
| Library outside | `<manifest>: /imports/<i>/library: '<rel>' lies outside the data root (<dir>).` |
| Library missing | `<manifest>: /imports/<i>/library: library file '<rel>' does not exist (looked for <path>).` |
| Import is not a library | `<file>: imported as a library, but its "schema" is '<tag>', not 'gridsim-library/2'.` |
| Pattern matches nothing | `<manifest>: /imports/<i>/templates/<j>: pattern '<pattern>' matches no template in '<rel>'.` |
| Unpinned resolution (note) | `import '<pattern>' from '<rel>' resolved to <id@rev> (the highest revision there).` |
| Local id shadows import | `<manifest>: /localTemplates/<i>/id: local template '<id>' has the id of an imported template; a local template may not shadow an import (use an override to change an imported value).` |
| Local template repeated | `<manifest>: /localTemplates/<i>: local template <id@rev> is defined twice.` |
| Local controller | `<where>: controller reference <ref> does not resolve to a template of the case (local or imported).`; `... is a placeholder template; a placeholder can never be a controller target.`; `the '<slot>' slot must reference a '<slot>' template, but <ref> is kind '<kind>'.` |
| Override target | `/overrides/<i>/template: <ref> is neither imported nor a local template of the case.`; `... <ref> is a placeholder template; a placeholder holds no values, so none can be overridden.` |
| Override content | `/overrides/<i>/params: an override must set at least one parameter.`; `/overrides/<i>/params/<name>: '<name>' of <ref> is overridden more than once.`; the merged `CheckParams` error followed by `(after applying the override to <ref>)` |
| Unresolved template | `<at>: template <ref> is neither imported nor a local template of the case.` |
| Placeholder bound | `<at>: template <ref> is a placeholder; a placeholder holds no values and can never be bound to an element.` |
| Kind not modelled | `<at>: template <ref> is kind '<kind>'; an element or binding can be bound only to a template of a kind GridSim's asset classes model today (gridFollowingInverter, gridFormingInverter, oltc, lineType, protection).` |
| Duplicate bus id | `<manifest>: bus id <id> is used by <n> buses; ids must be unique across every network file.` |
| Duplicate boundary | `<manifest>: <n> boundaries are named '<name>'; boundary names must be unique.` |
| Content refused | `<file>: <pointer>: <what> is valid JSON v2, but GridSim's model does not carry it yet (<reason>); remove it to load this case today.` with `<what>` one of `a line type`, `a transformer type` (reason `the v2 loader does not bind line or transformer types yet`), `an out-of-service line`, `an out-of-service transformer` (reason `of the network fields v1 cannot hold, the v2 loader reads only fault sequence data so far`) |
| Sequence data refused | `<file>: <pointer>: ...` naming the element and the rule: X''d / X2 on a converter-interfaced unit, `r0Pu` / `x0Pu` / `neutralXPu` without `windings`, one-sided `windings`, `r0Pu` without `x0Pu`, `x0Pu` not positive, `r0Pu` negative, `neutralXPu` on a side that is not `WyeGrounded` |
| Controllers on a class | `<where>: template <ref> has controller references; an asset class cannot carry them, so they would be dropped.` |
| Non-numeric class value | `<where>: template <ref> parameter '<name>' is <kind> <value>; an asset class holds numbers only.` |
| Profile | `<manifest>: /<gridCodeProfile or solverProfile>: GridSim does not yet bind a <profile> from a case (the built-in settings apply); remove it to load this case today.` |
| Builder refusal | `<manifest>: the case fails a check of the model builder it shares with v1 cases (file names in the message are the v1 equivalents: buses.json = network buses, generation.json = generators, demand.json = demands, transmission.json = lines, transformer.json = transformers, onloadtapchanger.json = oltcs, boundaries.json = boundaries, protections.json = protections, asset-classes.json = templates, asset-bindings.json = bindings): <builder message>` |
| Verb exit | an uncaught load failure exits 1 as `error loading case '<path>': cannot load JSON v2 case '<path>': <message>`; the schemas not found exits 1 as `cannot load JSON v2 case '<path>': the v2 schemas were not found (...)` |

The shared builder (`JsonGridLoader.Build`) applies the v1 rules to the mapped elements, with its own messages:
`buses.json is empty or missing.`; a bus without `baseKv`; `Generator '<name>' references unknown bus <id>.`;
`Demand '<name>' references unknown bus <id>.`; `No slack bus. Mark a generator role "slack" or a bus type "slack".`; ZIP
coefficient triples that do not sum to 1 (`zip real-power coefficients ap + bp + cp sum to <x>, not 1.` and the reactive
form); impossible generator limits and per-bus voltage limits; boundary branch names that do not resolve to exactly one
branch; `asset-bindings.json rule for '<type>' references unknown class '<class>'.`; a protection whose class is not a
protection; a failed model validation (`Invalid grid model:`). Their wording is specified in ch. 10.

## Invariants

| # | Invariant | Test (`tests/GridSim.Tests/V2/`) |
|---|---|---|
| 1 | The supported keyword list is exactly the 23 keywords; every one loads in one schema. | `JsonSchemaSubsetTests.The_supported_keyword_list_is_exactly_the_plan_subset`, `Every_supported_keyword_loads_together_in_one_schema` |
| 2 | An unsupported keyword is refused with file and pointer; a property named like one is fine; all are found at once. | `An_unsupported_keyword_is_refused_naming_file_and_pointer`, `A_property_named_like_an_unsupported_keyword_is_fine`, `Unsupported_keywords_inside_defs_items_and_combinators_are_all_found_at_once` |
| 3 | Badly shaped keywords, duplicate members, malformed JSON, bad file names, empty sets and missing directories are refused loudly; comments and trailing commas are accepted. | `A_badly_shaped_keyword_is_refused`, `A_duplicate_member_name_is_refused`, `Malformed_json_is_refused_naming_the_file`, `A_file_name_with_a_path_separator_is_refused`, `An_empty_source_set_is_refused`, `A_missing_directory_is_refused`, `Comments_and_trailing_commas_are_accepted_like_v1` |
| 4 | `$ref` resolves locally and across files; a ref that lands on no schema is refused naming both files. | `Local_and_cross_file_refs_resolve`, `A_ref_that_does_not_land_on_a_schema_is_refused`, `A_ref_into_another_file_with_a_missing_def_is_refused_naming_both_files` |
| 5 | An in-place `$ref` cycle (self, two defs, across files, through a combinator or conditional) is refused; a recursive tree through `properties` or `items` is not a cycle. | `A_schema_that_refers_to_itself_in_place_is_a_cycle`, `A_two_def_cycle_is_refused`, `A_cycle_across_files_is_refused`, `A_cycle_through_a_combinator_or_conditional_is_refused`, `A_recursive_tree_through_properties_or_items_is_not_a_cycle` |
| 6 | `$` in a pattern becomes absolute end outside classes, so an anchored pattern refuses a trailing newline. | `Dollar_anchors_are_rewritten_to_absolute_end_outside_classes`, `SchemaValidatorTests.An_anchored_pattern_refuses_a_trailing_newline` |
| 7 | Each supported keyword validates with the stated semantics: type, properties, required, additionalProperties, items, minItems, enum, const, inclusive bounds, exclusive minimum, pattern, `oneOf`, `anyOf`, `if`. | `SchemaValidatorTests` (`Type_accepts_its_own_values` through `If_without_else_applies_nothing_when_it_fails`) |
| 8 | `additionalProperties` does not see the properties of a sibling `$ref`. | `Additional_properties_does_not_see_properties_of_a_sibling_ref` |
| 9 | Every error is returned with its pointer, not only the first; `ValidateOrThrow` names the document and lists every error. | `Every_error_is_returned_with_its_pointer_not_just_the_first`, `Validate_or_throw_names_the_document_and_lists_every_error`, `Validate_or_throw_is_silent_when_clean`, `Parse_file_names_the_file_on_bad_json` |
| 10 | Every file under `schemas/v2` parses and is a schema or a kind spec; the set loads with only supported keywords and every `$ref` resolved; each schema declares the 2020-12 dialect and its own id. | `V2SchemaWellFormedTests.Every_file_under_schemas_v2_parses`, `Every_file_under_schemas_v2_is_either_a_schema_or_a_kind_spec`, `The_schema_set_loads_with_only_supported_keywords_and_every_ref_resolved`, `Every_schema_file_declares_the_2020_12_dialect_and_its_own_id` |
| 11 | The unit vocabulary is exactly the closed 31-unit list (25 units, then the six asset overlay units); the provenance tags are exactly the `ProvenanceTag` names. | `The_unit_vocabulary_is_exactly_the_closed_plan_list`, `The_provenance_tags_are_exactly_the_ProvenanceTag_names`, `V2KindSpecStructureTests.Every_unit_used_is_in_the_closed_vocabulary_and_the_vocabulary_has_twenty_five_units` |
| 12 | A `CuratedPlaceholder` note must contain `[VERIFY]`; a quantity is a number or `{v, unit, prov}`; a template ref is `id@rev`; status is `active` or `placeholder`. | `Provenance_carries_the_VERIFY_rule_for_curated_placeholders`, `Quantity_is_a_number_or_v_unit_prov`, `Template_ref_is_id_at_rev`, `Status_is_active_or_placeholder` |
| 13 | A kind spec loads with every field; unknown members, schema breaks and semantic breaks are refused; the file is named after its kind; every file problem is reported together. | `KindSpecRegistryTests.A_well_formed_kind_loads_with_every_field`, `An_unknown_member_anywhere_in_a_kind_file_is_refused`, `A_kind_file_breaking_the_schema_is_refused`, `A_kind_file_breaking_a_semantic_rule_is_refused`, `The_file_must_be_named_after_its_kind`, `Every_kind_file_problem_is_reported_together` |
| 14 | `CheckParams`: complete active params pass; the object form with the exact unit passes; unknown parameter, missing required (active and placeholder), differing unit, unit on a unitless parameter, out of range, wrong type, null in active, value in placeholder and an unknown kind are errors; bounds are inclusive; every problem is reported. | `KindSpecRegistryTests` (`A_complete_active_params_object_passes` through `Every_params_problem_is_reported_not_just_the_first`) |
| 15 | The kinds folder holds the fifteen listed kinds plus `site` and nothing else; each of the fifteen has the fixed format, is plain ASCII, declares no parameter twice, and uses lower camel case names. | `V2KindSpecStructureTests.The_kinds_folder_holds_the_fifteen_slice3_kinds_and_nothing_unexpected`, `Each_kind_file_has_exactly_the_fixed_format`, `Each_kind_file_is_plain_ascii`, `No_parameter_name_is_declared_twice_within_a_kind_and_names_are_lower_camel_case` |
| 16 | Infinity is accepted in `codeDefaults` only as the two named strings on a number; each malformation is reported. | `Infinity_is_accepted_only_as_the_two_named_strings_on_a_number`, `Each_malformation_is_reported` |
| 17 | Every `codeDefaults` entry resolves to the code value and equals it exactly, one test per kind; one ulp away is a mismatch; bad references are reported; the entry total is pinned. | `V2KindSpecCodeDefaultTests.Every_code_default_resolves_and_equals_the_code_value_exactly`, `A_value_one_ulp_away_is_a_mismatch_because_comparison_is_exact`, `Bad_references_and_wrong_values_are_reported`, `Each_reference_form_resolves_a_known_value_exactly`, `Every_kind_with_code_in_GridSim_today_carries_code_defaults_and_the_total_is_pinned` |
| 18 | The inconsistency register is pinned value for value, and each registered disagreement really has two values. | `The_inconsistency_register_is_pinned_value_for_value`, `Each_registered_disagreement_really_has_two_different_values_on_one_parameter` |
| 19 | The seven keys with the units `MW.s/Hz`, `MW/Hz`, `MW/s/MW`, `V` and `min` carry exactly the code defaults of their consumers. | `The_Q24_keys_carry_exactly_the_code_defaults_of_their_consumers`, `The_Q24_rows_cover_every_code_default_of_the_seven_keys` |
| 20 | Every `AssetParams` constant is declared by exactly its kinds; every key of every bundled v1 asset-class file is known to its v2 kind; the v1 kind mapping mirrors every alias the loader accepts. | `V2KindSpecCoverageTests.Every_AssetParams_constant_is_declared_by_exactly_its_kinds`, `Every_key_of_every_bundled_v1_asset_class_is_known_to_its_kind_apart_from_the_pinned_unit_gaps`, `The_v1_kind_mapping_mirrors_every_alias_the_loader_accepts`, `The_former_unit_gaps_are_declared_on_exactly_their_kinds_with_their_unit`, `The_registers_name_real_constants_and_do_not_overlap` |
| 21 | A placeholder has every value and controller slot null; an active template has none null; a placeholder is never a controller target (same file or another file); a controller ref that resolves nowhere or has the wrong slot kind fails; a duplicate `id@rev` fails; a provenance key naming no parameter fails; an unknown kind fails; schema errors come before semantic checks. | `V2PlaceholderRuleTests` (`The_valid_site_fixture_passes` through `Validate_files_or_throw_names_each_file`) |
| 22 | Every library file under `data/library` validates as one set. | `V2LibraryFilesValidateTests.Every_data_library_file_validates` |
| 23 | The case and network schemas load with the set; a good case and network pass; each bad fixture fails at its expected pointer; every fixture is named in a test; overrides need a pinned template, present values, a reason and explicit shadow; import patterns are a ref, an id or a dotted prefix; the winding connections are exactly the `WindingConnection` names. | `V2CaseSchemaTests` |
| 24 | Every bundled v1 case (`uk`, `gb-full`, `gb-synthetic`, every `intl` case) projects to valid v2 and maps back field for field, doubles by their bits. | `V1ToV2ProjectionTests.A_bundled_v1_case_projects_to_valid_v2_and_maps_back_field_for_field` |
| 25 | The projector maps every property of every v1 DTO; a fixture sets every field; an absent v2 member means the v1 default; the synthesised default provenance is v1's own fallback; the kind table is one to one and covers every `AssetKind`. | `The_projector_maps_every_property_of_every_v1_dto`, `The_every_field_fixture_really_sets_every_field`, `A_v1_case_with_every_field_set_projects_to_valid_v2_and_maps_back_field_for_field`, `An_absent_v2_member_means_exactly_what_the_absent_v1_key_means`, `The_synthesised_default_provenance_is_v1s_own_fallback`, `The_kind_table_is_one_to_one_and_covers_every_AssetKind` |
| 26 | v2-only network and case content has no v1 spelling and the back-mapper refuses it; generator `inService` maps both ways. | `V2_only_network_content_cannot_be_mapped_back_to_v1`, `V2_only_case_content_cannot_be_mapped_back_to_v1`, `An_out_of_service_generator_maps_back_to_v1`, `In_service_true_is_what_v1_means_and_maps_back` |
| 27 | A bundled case loaded through its v2 projection gives the v1 model field for field by bits and a bit-identical solve (voltages, angles, injections, losses, iterations), with no notes. | `V2CaseLoaderTests.A_bundled_case_loaded_through_its_v2_projection_gives_the_v1_model_and_solve_bit_for_bit` |
| 28 | Splitting the network over two files gives the same model. | `Splitting_the_network_over_two_files_gives_the_same_model` |
| 29 | An imported template bound by a rule carries the override and its provenance; an override's own provenance is kept; a local template keeps the v1 class name and an element's template wins over a binding; the infinity strings load as in v1. | `An_imported_template_bound_by_a_rule_carries_the_override_and_its_provenance`, `An_override_with_its_own_provenance_keeps_it`, `A_local_template_keeps_the_v1_class_name_and_an_element_template_wins_over_a_binding`, `The_infinity_strings_load_as_in_v1` |
| 30 | The loader refuses: a bus without `baseKv`, a bad override, a parameter overridden twice, a shadowing local id, a local template breaking its kind, a binding to a placeholder or a kind no asset class models, v2 content the model does not carry (all problems listed), an import pattern matching nothing, imports with no data root, a library path outside the data root, a network file outside the case folder, a profile, a file that is not a manifest, duplicate bus ids across files. | `V2CaseLoaderTests` (`A_bus_without_baseKv_is_refused_by_the_v2_schema` through `Duplicate_bus_ids_across_network_files_are_refused`) |
| 31 | A builder refusal names the v1 file equivalents. | `A_failing_builder_check_names_the_v1_file_equivalents` |
| 32 | The CLI solves a v2 manifest exactly as the v1 folder (same `pf` report) and refuses a bad manifest with exit 1. | `The_cli_solves_a_v2_manifest_exactly_as_the_v1_folder`, `The_cli_refuses_a_bad_v2_manifest_with_exit_1` |

## Worked example

The case below has three buses: a CCGT at the slack bus, a wind farm bound to a local grid-following template, and one
load. It imports two of the placeholder sites, one unpinned and one pinned. Both files are under
`docs/_captures/v2-schema/inputs/small/`.

`small.case.json`:

```json
{
  "schema": "gridsim-case/2",
  "name": "Small v2 example",
  "description": "Three buses: a CCGT, a wind farm bound to a local grid-following template, and one load.",
  "system": { "baseMva": 100, "frequencyHz": 50 },
  "network": ["small.network.json"],
  "imports": [
    {
      "library": "library/placeholders/unmodelled-sites.json",
      "templates": ["gridsim.placeholder.site.SMR", "gridsim.placeholder.site.Crypto-Mining-Site@1"]
    }
  ],
  "localTemplates": [
    {
      "id": "gridsim.local.wind-gfl",
      "rev": 1,
      "kind": "gridFollowingInverter",
      "status": "active",
      "title": "Onshore wind, grid-following",
      "params": {
        "deadbandHz": 0.015,
        "droopGainMwPerHzPerMw": { "v": 20, "unit": "MW/Hz/MW", "prov": { "tag": "Synthetic", "note": "Chosen for this example" } },
        "rocofTripHzPerS": 1.0
      },
      "provenance": { "_default": { "tag": "Synthetic", "note": "Example values, not a measured plant" } }
    }
  ],
  "bindings": [ { "matchType": "wind-*", "template": "gridsim.local.wind-gfl@1" } ],
  "overrides": [
    {
      "template": "gridsim.local.wind-gfl@1",
      "params": { "deadbandHz": 0.02 },
      "reason": "Wider deadband for this example",
      "shadow": "explicit"
    }
  ]
}
```

`small.network.json`:

```json
{
  "schema": "gridsim-network/2",
  "buses": [
    { "id": 1, "name": "North 400", "baseKv": 400, "type": "slack" },
    { "id": 2, "name": "Wind 400", "baseKv": 400 },
    { "id": 3, "name": "South 400", "baseKv": 400 }
  ],
  "generators": [
    { "name": "North CCGT", "bus": 1, "type": "ccgt", "capacityMva": 600, "outputMw": 200, "role": "slack" },
    { "name": "Wind farm", "bus": 2, "type": "wind-onshore", "capacityMva": 200, "outputMw": 120 }
  ],
  "demands": [
    { "name": "South load", "bus": 3, "pMw": 300, "qMvar": 80 }
  ],
  "lines": [
    { "name": "L1-2", "from": 1, "to": 2, "rPu": 0.002, "xPu": 0.02, "bPu": 0.1, "ratingMva": 500 },
    { "name": "L2-3", "from": 2, "to": 3, "rPu": 0.003, "xPu": 0.03, "bPu": 0.1, "ratingMva": 500 },
    { "name": "L1-3", "from": 1, "to": 3, "rPu": 0.004, "xPu": 0.04, "bPu": 0.1, "ratingMva": 500 }
  ]
}
```

The wind generator's `type` is `wind-onshore`, so the binding rule `wind-*` gives it the template
`gridsim.local.wind-gfl@1`; the override changes that template's `deadbandHz` from 0.015 to 0.02 and, naming no
provenance, marks the value `CuratedPlaceholder` with the note
`[VERIFY] set by an override in case 'Small v2 example': Wider deadband for this example`. The class the model holds is
named `wind-gfl`.

### Validate

`validate` checks the manifest, then reads the network file the manifest lists and checks it too. The network file and the
schema folder print as absolute paths, which the blocks here shorten to repo-relative text; only the manifest path is
echoed as given.

```
$ gridsim validate docs/_captures/v2-schema/inputs/small/small.case.json
OK    docs/_captures/v2-schema/inputs/small/small.case.json (gridsim-case/2)
OK    docs\_captures\v2-schema\inputs\small\small.network.json (gridsim-network/2)
validate: 2 of 2 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 0]
```

### Load and solve

`pf` loads the manifest through `V2CaseLoader`. The note on the first line is the unpinned import `gridsim.placeholder.site.SMR`
resolving to revision 1; the pinned import `Crypto-Mining-Site@1` produces no note. Both placeholder templates load as
templates of the case and, holding no values, bind to nothing.

```
$ gridsim pf docs/_captures/v2-schema/inputs/small/small.case.json
Power flow - Small v2 example (3 buses)
  converged in 3 iteration(s), mismatch 6.90E-010 pu   losses 1.734 MW / -12.283 MVAr
  voltage range 0.9811 - 1.0000 pu
  lowest voltages:
      bus 3      0.9811 pu     -3.205 deg
      bus 1      1.0000 pu      0.000 deg
      bus 2      1.0000 pu     -0.473 deg
note: small.case.json: import 'gridsim.placeholder.site.SMR' from 'library/placeholders/unmodelled-sites.json' resolved to gridsim.placeholder.site.SMR@1 (the highest revision there).
[exit 0]
```

The dispatcher sets the marginal plant so that scheduled generation equals demand plus the 0.02 loss allowance: 300 MW
x 1.02 = 306 MW, which leaves the CCGT at 186 MW beside the wind farm's 120 MW. `dcpf` loads the same manifest and
shows that figure:

```
$ gridsim dcpf docs/_captures/v2-schema/inputs/small/small.case.json
DC power flow - Small v2 example (3 buses, 3 branches)  [linear: series reactance only; R, charging, shunts, taps and phase shifts ignored; |V| = 1 pu; lossless]
  island ref bus 1          3 buses  demand      300.0 MW  scheduled generation      306.0 MW  reference injects      180.0 MW
  0 of 3 rated branch(es) above 100% of RateA
  largest flows:
      branch 1     L2-3                  2 -> 3          160.00 MW     32.0% of 500 MVA
      branch 2     L1-3                  1 -> 3          140.00 MW     28.0% of 500 MVA
      branch 0     L1-2                  1 -> 2           40.00 MW      8.0% of 500 MVA
note: small.case.json: import 'gridsim.placeholder.site.SMR' from 'library/placeholders/unmodelled-sites.json' resolved to gridsim.placeholder.site.SMR@1 (the highest revision there).
[exit 0]
```

The DC engine is lossless, so the reference bus injects the demand less the wind output, 300 - 120 = 180 MW, split 140 MW
down L1-3 and 40 MW down L1-2. L2-3 carries the 40 MW arriving from L1-2 plus the wind farm's 120 MW, 160 MW in all, 32.0% of its 500 MVA rating.

### The same case as v1

`convert` accepts the manifest as its input and writes a v1 case folder. It proves the conversion by reading the folder
back and solving both with the study solver, bit for bit. It does not write the asset-class and binding files.

```
$ gridsim convert docs/_captures/v2-schema/inputs/small/small.case.json results/v2-schema/small-v1
converted docs/_captures/v2-schema/inputs/small/small.case.json -> results/v2-schema/small-v1: 3 buses, 2 generators (0 out of service), 3 branches
  note: the authored asset-dynamics bundle (asset-classes.json / asset-bindings.json) is not written; copy those files across by hand if the written case needs them.
  round trip: the written case solves bit-identically (converged; 3 bus voltages and angles, 3 line flows and the losses compared bit for bit, study options with the OLTC loop)
note: small.case.json: import 'gridsim.placeholder.site.SMR' from 'library/placeholders/unmodelled-sites.json' resolved to gridsim.placeholder.site.SMR@1 (the highest revision there).
[exit 0]
```

### Refusals

Each refusal below comes from a case under `docs/_captures/v2-schema/inputs/refused/`. A case with schema errors fails
at the first stage, whether it is checked with `validate` or loaded. `schema.network.json` has a bus without `baseKv`, a
bus type `swing` (a v1 alias) and a misspelt generator member.

```
$ gridsim validate docs/_captures/v2-schema/inputs/refused/schema.case.json
OK    docs/_captures/v2-schema/inputs/refused/schema.case.json (gridsim-case/2)
FAIL  docs\_captures\v2-schema\inputs\refused\schema.network.json (gridsim-network/2): 3 error(s)
        /buses/0: missing required member 'baseKv'.
        /buses/1/type: value "swing" is not one of the allowed values ["pq", "pv", "slack"].
        /generators/0/capacityMVA: unknown member 'capacityMVA' is not allowed here. Allowed: name, bus, type, template, capacityMva, outputMw, capacityFactor, voltageSetpoint, qMinMvar, qMaxMvar, inertiaH, role, powerFlowOnly, unitTransformerXPu, converter, subtransientXdPu, negativeSeqXPu, inService, pMinMw, pMaxMw, qOutputMvar.
validate: 1 of 2 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 3]
```

The loader lists the same three problems and exits 1:

```
$ gridsim dcpf docs/_captures/v2-schema/inputs/refused/schema.case.json
error loading case 'docs/_captures/v2-schema/inputs/refused/schema.case.json': cannot load JSON v2 case 'docs/_captures/v2-schema/inputs/refused/schema.case.json': schema.case.json: JSON v2 case refused at the schema checks: 3 problem(s):
  schema.network.json: /buses/0: missing required member 'baseKv'.
  schema.network.json: /buses/1/type: value "swing" is not one of the allowed values ["pq", "pv", "slack"].
  schema.network.json: /generators/0/capacityMVA: unknown member 'capacityMVA' is not allowed here. Allowed: name, bus, type, template, capacityMva, outputMw, capacityFactor, voltageSetpoint, qMinMvar, qMaxMvar, inertiaH, role, powerFlowOnly, unitTransformerXPu, converter, subtransientXdPu, negativeSeqXPu, inService, pMinMw, pMaxMw, qOutputMvar.
[exit 1]
```

A case whose documents are valid can still fail the later stages. `override.case.json` carries one override that sets
`deadbandHz` to 9 Hz (the kind's maximum is 5) and a parameter the kind does not declare, and a second override of a
template the case does not have. The range error is the merged `CheckParams` result, which is why it carries the
`(after applying the override ...)` suffix:

```
$ gridsim dcpf docs/_captures/v2-schema/inputs/refused/override.case.json
error loading case 'docs/_captures/v2-schema/inputs/refused/override.case.json': cannot load JSON v2 case 'docs/_captures/v2-schema/inputs/refused/override.case.json': override.case.json: JSON v2 case refused at the overrides: 3 problem(s):
  override.case.json: /overrides/0/params/deadbandHz: 9 Hz is above the maximum 5 for 'deadbandHz'. (after applying the override to gridsim.local.wind-gfl@1)
  override.case.json: /overrides/0/params/deadbandKhz: unknown parameter 'deadbandKhz' for kind 'gridFollowingInverter' (declared: deadbandHz, droopGainMwPerHzPerMw, rocofGainMwsPerHz, detectionLatencyS, computeLatencyS, actuateTauS, dispatchRampMwPerMin, fastSlewUpMwPerSPerMw, fastSlewDownMwPerSPerMw, headroomUpFraction, headroomDownFraction, rocofTripHzPerS, rocofTripConfirmS, localRocofUpliftFactor, vectorShiftTripDeg, lvrtVoltagePu, minOutputFraction, pllBandwidthHz, pllDampingRatio, currentLoopBandwidthHz, faultCurrentPu, rmsCurrentLagS, rmsVoltageFilterS, rmsDipVoltagePu, rmsReactiveCurrentGain, rmsLvacmZeroPu, rmsLvacmFullPu, rmsDipHoldS, rmsBlockInDip, capacityFactor, capacityMwh, socInit, socMin, socMax, batteryChemistry, dcVoltageV, converterEfficiency, ambientC). (after applying the override to gridsim.local.wind-gfl@1)
  override.case.json: /overrides/1/template: gridsim.local.missing@1 is neither imported nor a local template of the case.
[exit 1]
```

`elements.case.json` is schema-valid and has valid templates, and fails at the network elements with three problems: a
line with a `type` and `"inService": false`, and a binding to the placeholder site `SMR`. Every problem is listed, content
the model does not carry first in file order, then the binding. Its generator's `subtransientXdPu` is fault sequence data
on a synchronous unit (a ccgt), which the loader reads, so it is not among the problems:

```
$ gridsim dcpf docs/_captures/v2-schema/inputs/refused/elements.case.json
error loading case 'docs/_captures/v2-schema/inputs/refused/elements.case.json': cannot load JSON v2 case 'docs/_captures/v2-schema/inputs/refused/elements.case.json': elements.case.json: JSON v2 case refused at the network elements: 3 problem(s):
  elements.network.json: /lines/0/type: a line type is valid JSON v2, but GridSim's model does not carry it yet (the v2 loader does not bind line or transformer types yet); remove it to load this case today.
  elements.network.json: /lines/0/inService: an out-of-service line is valid JSON v2, but GridSim's model does not carry it yet (of the network fields v1 cannot hold, the v2 loader reads only fault sequence data so far); remove it to load this case today.
  elements.case.json: /bindings/0/template: template gridsim.placeholder.site.SMR@1 is a placeholder; a placeholder holds no values and can never be bound to an element.
[exit 1]
```

Giving a verb a network file instead of a manifest names the mistake:

```
$ gridsim pf docs/_captures/v2-schema/inputs/small/small.network.json
error loading case 'docs/_captures/v2-schema/inputs/small/small.network.json': cannot load JSON v2 case 'docs/_captures/v2-schema/inputs/small/small.network.json': small.network.json: not a JSON v2 case manifest: its "schema" is 'gridsim-network/2', not 'gridsim-case/2'.
[exit 1]
```

### Libraries and kind specs

The shipped library validates:

```
$ gridsim validate data/library/placeholders/unmodelled-sites.json
OK    data/library/placeholders/unmodelled-sites.json (gridsim-library/2)
validate: 1 of 1 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 0]
```

`shape.library.json` breaks the schema layer: a placeholder with a non-null value and a note without `[VERIFY]`, and an
active template with a null value. Layer 2 does not run, so no semantic error appears:

```
$ gridsim validate docs/_captures/v2-schema/inputs/library/shape.library.json
FAIL  docs/_captures/v2-schema/inputs/library/shape.library.json (gridsim-library/2): 5 error(s)
        /templates/0/provenance/_default/note: string "No source" does not match the pattern "\\[VERIFY\\]".
        /templates/0/params/generation: expected type null, found string.
        /templates/1/params/model: value null matches none of the 2 anyOf alternatives:
        /templates/1/params/model: [anyOf alternative 0] expected type number or string or boolean, found null.
        /templates/1/params/model: [anyOf alternative 1] expected type object, found null.
validate: 0 of 1 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 3]
```

`meaning.library.json` is schema-valid and breaks the cross-template layer: a repeated `id@rev`, a wrong unit, an
out-of-range value, an undeclared parameter, an orphan provenance key, a placeholder target, a wrong slot kind and an
unresolved reference:

```
$ gridsim validate docs/_captures/v2-schema/inputs/library/meaning.library.json
FAIL  docs/_captures/v2-schema/inputs/library/meaning.library.json (gridsim-library/2): 8 error(s)
        /templates/1/id: template gridsim.exciter.demo@1 is declared twice (first in docs/_captures/v2-schema/inputs/library/meaning.library.json at /templates/0).
        /templates/3/params/inertiaH/unit: unit 'MW' does not match the kind's unit 's' for 'inertiaH' (no conversion is done; write the spec's unit).
        /templates/3/params/inertiaH/v: 25 s is above the maximum 20 for 'inertiaH'.
        /templates/3/params/inertiaZ: unknown parameter 'inertiaZ' for kind 'synchronousMachine' (declared: inertiaH, damping, xdPu, xdpPu, xdppPu, xqPu, xqpPu, xqppPu, negativeSeqXPu, unitTransformerXPu, tdopS, tdoppS, tqopS, tqoppS, xdOverXdpRatio, xdppOverXdpRatio, xqOverXdpRatio, xqpOverXdpRatio, xqppOverXdpRatio, capacityFactor, fuelType, xlPu, saturationS10, saturationS12).
        /templates/3/provenance/inertiaX: provenance is given for 'inertiaX', which is not a parameter of this template.
        /templates/3/controllers/exciter: controller reference gridsim.placeholder.site.Example@1 is a placeholder template (docs/_captures/v2-schema/inputs/library/meaning.library.json at /templates/2); a placeholder can never be a controller target.
        /templates/3/controllers/governor: the 'governor' slot must reference a 'governor' template, but gridsim.exciter.demo@1 is kind 'exciter'.
        /templates/3/controllers/pss: controller reference gridsim.pss.nothing@1 does not resolve to any template in the loaded libraries.
validate: 0 of 1 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 3]
```

A folder argument validates every kind spec in it. All sixteen pass:

```
$ gridsim validate schemas/v2/kinds
OK    schemas/v2/kinds\exciter.kind.json (gridsim-kind/2)
OK    schemas/v2/kinds\governor.kind.json (gridsim-kind/2)
...
OK    schemas/v2/kinds\transformerType.kind.json (gridsim-kind/2)
validate: 16 of 16 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 0]
```

A v1 case file carries no `schema` tag and is reported as invalid:

```
$ gridsim validate data/uk/buses.json
FAIL  data/uk/buses.json (no tag): 1 error(s)
        /schema: not a GridSim v2 document: no "schema" tag (expected one of gridsim-case/2, gridsim-network/2, gridsim-library/2, gridsim-kind/2, gridsim-dynamics/2, gridsim-dist3ph/2).
validate: 0 of 1 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 3]
```

## Source map

| File | Type | Responsibility |
|---|---|---|
| `schemas/v2/common.schema.json` | schema | units, provenance, values, template identity, status |
| `schemas/v2/library.schema.json` | schema | library file and template shape, placeholder if/then/else |
| `schemas/v2/kind.schema.json` | schema | kind spec format |
| `schemas/v2/case.schema.json` | schema | case manifest |
| `schemas/v2/network.schema.json` | schema | network element arrays |
| `schemas/v2/kinds/*.kind.json` | kind specs | the sixteen parameter contracts and their `codeDefaults` |
| `data/library/placeholders/unmodelled-sites.json` | library | the 15 placeholder sites |
| `src/GridSim.Core/IO/Json/Schema/JsonSchemaSubset.cs` | `JsonSchemaSubset`, `SchemaNode` | schema set load: keyword whitelist, shape checks, `$ref` resolution, cycle check, pattern compile, reader options |
| `src/GridSim.Core/IO/Json/Schema/SchemaValidator.cs` | `SchemaValidator`, `SchemaError` | applies a schema node to a value, collects every error |
| `src/GridSim.Core/IO/Json/Schema/KindSpecRegistry.cs` | `KindSpecRegistry`, `KindSpec`, `KindParamSpec`, `KindCodeDefault`, `KindParamType` | kind spec load and semantic rules; `CheckParams` |
| `src/GridSim.Core/IO/Json/Schema/V2LibraryChecker.cs` | `V2LibraryChecker` | library set checks: schema, kind params, unique `id@rev`, provenance keys, controller targets |
| `src/GridSim.Core/IO/Json/Schema/V2DocumentValidator.cs` | `V2DocumentValidator`, `V2DocumentReport` | routes documents by tag; the engine of `validate` |
| `src/GridSim.Core/IO/Json/V2CaseLoader.cs` | `V2CaseLoader`, `V2CaseLoadResult` | manifest to `GridModel`: imports, templates, overrides, element mapping, asset classes |
| `src/GridSim.Core/IO/Json/JsonGridLoader.cs` | `JsonGridLoader.Build`, `CaseDtos` | the model builder shared by v1 and v2 |
| `src/GridSim.Core/IO/Json/CaseEditing/BundledSchemas.cs` | `BundledSchemas` | embedded v1 and v2 schema sets for the case editor |
| `src/GridSim.Core/IO/Json/CaseEditing/V2CaseChecks.cs` | `V2CaseChecks` | the editor's v2 semantic checks |
| `src/GridSim.Cli/Gated.cs` | `Gated.V2Case`, `Gated.FindV2Schemas` | CLI load of a manifest with the licence gate; schema folder search |
| `src/GridSim.Cli/Program.cs` | `RunValidate`, `LoadNamed`, `RunConvert` | the `validate` verb, case resolution, `convert` |
| `tests/GridSim.Tests/V2/V1Projector.cs` | `V1Projector` | test-only v1 to v2 projector and back-mapper |
| `tests/GridSim.Tests/V2/CodeDefaultRefResolver.cs` | `CodeDefaultRefResolver` | resolves a `codeDefaults` `ref` (`member:`, `instance:`, `param:`; a `member:GridSim.Cli:...` value is read from the CLI the build put beside the tests; `source:`, a quoted literal in a source file, is still resolved but no kind file uses it) and compares exactly |
| `tests/GridSim.Tests/V2/KindFileSet.cs`, `V2TestSupport.cs` | helpers | kind file loading, repository paths |
| `tests/GridSim.Tests/V2/*Tests.cs` | tests | the invariants above |
| `tests/GridSim.Tests/fixtures/v2/` | fixtures | good and bad cases, networks, libraries and kind specs |

## Limits

- `validate` accepts an integer written as `2.0`, but the loader reads integers with `GetInt32()` and refuses it with a
  bare `FormatException` that names no file or pointer.
- `JSON v2 data root not found` is a `DirectoryNotFoundException`, which the batch verbs' `LOAD FAIL` handling does not
  catch.
- An imported template that nothing binds still refuses the whole load if it holds controller references or a
  non-numeric parameter.

- A v2 case carries line and transformer `type` and out-of-service lines and transformers in valid JSON, and the loader refuses them because the model does not carry them. The fault sequence data (generator `subtransientXdPu` / `negativeSeqXPu`, transformer windings, zero-sequence impedance and neutral reactance) loads.
- `gridCodeProfile` and `solverProfile` validate in a manifest and the loader refuses them, so a case cannot bind a grid code or solver settings.
- Only templates of the kinds `gridFollowingInverter`, `gridFormingInverter`, `oltc`, `lineType` and `protection` become asset classes; the other eleven kinds are validated and bound to nothing. An active `synchronousMachine` template is validated and not turned into an asset class (no controller exists for it); binding an element to one is refused at the binding.
- A template that becomes an asset class cannot hold controller references or a non-numeric parameter, because asset classes hold numbers only.
- No consumer reads `codeDefaults` or the physical-unit values of `lineType` and `transformerType`; per-unit values stay authoritative and nothing converts physical values at load.
- Library imports resolve only under the `data` folder beside the program, so a user library has to be placed there.
- `validate` does not resolve a manifest's imports, so import errors appear only when the case is loaded.
- `export` does not accept a v2 manifest as its case, and `trip` reads a lone `.json` argument as a generator name.
- The case editor's v2 save gate does not run `V2CaseLoader` on a case that imports library templates, so such a saved case can still be refused when loaded.
- `convert` writes v2 (an output ending in `.json`, through `V2CaseWriter`) with the model's asset classes as case-local templates `gridsim.local.<class>@1`, its binding rules and its protective devices, but not the original manifest's structure: library imports and per-element overrides are resolved into the written classes, and a bundle whose class names cannot be local template ids (classes from imported templates, `id@rev`) is not written at all, with a note saying why.
- The unit vocabulary has no unit for pu/s, 1/s, percent per Hz, Ah, ohm, J/mol and similar, so parameters in those units are not declared in any kind.
- `data/library` ships only the placeholder sites; no active template library ships.

## See also

- [ch. 10, File and wire formats](<0010 File and wire formats.md>) for the v1 case files the shared builder reads.
- [ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>) for the model the loader builds.
- [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) for the provenance tags and the `[VERIFY]` rule.
- [Loading networks](<../manual/0002 Loading networks.md>) and [The case editor](<../manual/0033 The case editor.md>) in the user manual.
- The `validate` verb in [the CLI reference](<../manual/0019 CLI reference.md#validate>), and its `convert` section at [convert](<../manual/0019 CLI reference.md#convert>).
