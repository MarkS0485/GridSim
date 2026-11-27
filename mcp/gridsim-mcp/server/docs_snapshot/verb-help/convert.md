### convert

`gridsim convert <input.m|input.raw|input.uct|case-dir|case> <output.m|output.raw|output.rawx|output.uct|output.xml|output.json|output-dir> [--raw-revision 33|34|35|36] [--ucte-country <CC>] [--public [--provenance <manifest.json>]]`

Convert a case between MATPOWER, PSS/E RAW, CGMES, a JSON case folder (v1) and a JSON v2 case. An output ending in `.m` is written as
MATPOWER (version 2); an output ending in `.raw` is written as PSS/E RAW revision 33, or the revision `--raw-revision` names (33 to 36; see below); an output ending in `.rawx` is written as PSS/E RAWX (JSON), revision 35; an output ending in `.uct` is written as UCTE-DEF (see below); an output ending in `.xml` is written as CGMES RDF/XML (see below); an output ending in `.json` is written as a JSON v2 case (N299, October 2026): the manifest at that
path and its network file beside it (`uk.case.json` writes `uk.network.json`); any other output is a JSON case folder. The input is loaded as every verb loads it (a `.m` file through the unit
normaliser, a PSS/E `.raw` file through the RAW reader ([ch. 2](<0002 Loading networks.md#psse-raw-files>)), a UCTE-DEF `.uct` file through the UCTE reader ([ch. 2](<0002 Loading networks.md#ucte-def-files>)), a CIM / CGMES input (below), a JSON folder dispatched as usual, or a built-in name). The conversion is then proved: the written case is
read back and both are solved with the contingency option set and the tap-changer loop, and every bus voltage, angle
and injection, both loss totals and every line flow must be the same bits. The last line says so (`round trip: the
written case solves bit-identically (...)`).

Everything a power flow reads is written exactly and in model order (buses, generators including out-of-service ones,
branches), so a MATPOWER case converts both ways without changing a bit. What the target format cannot hold is printed
as `note:` lines (and written into a `.m` file as comments): to MATPOWER, names, layout coordinates, ZIP loads,
generator types, inertia and fault data, OLTCs, boundaries and the asset bundles; to JSON, MATPOWER cost curves
(`gencost`, carried by the model but not by JSON v1) and DC lines. A JSON folder written by `convert` sets
`"dispatch": false` in `system.json` (the written outputs are the operating point) and keeps transformers in
`transmission.json`, in branch order (see [ch. 2](<0002 Loading networks.md>)). A bus with no per-bus voltage
limits is written to MATPOWER with VMAX / VMIN 1.06 / 0.94, the band GridSim's checks fall back to.

A CIM or CGMES input is a single RDF/XML `.xml` document, a `.zip` exchange, or a folder of `.xml` profile files that is not
a GridSim case folder (no `system.json`, `buses.json`). It is read with the Core importers the desktop app uses
(`CimImporter` with its default options, then `CimGridModelBridge` on a 100 MVA base; the profile documents EQ, TP, SSH and SV
are used, DL, GL and SC are left out): the bus-branch model that the TopologicalNodes make. The bridge's notes are printed on stderr as
`note:` lines. CGMES carries no system frequency, so `--frequency-hz` applies as for a `.m` file. The licence's bus limit applies as
for `cgmes-study`. The conversion and its round-trip proof then run as for any other input; what the output format cannot hold is
named in the `note:` lines, and a CIM object the importer does not model is dropped by the importer with its note. A node-breaker model
that you want to keep as switches goes to [substation-state](#substation-state) instead.

A PSS/E RAW output carries the devices the RAW reader models, each in its own section: two-terminal LCC, VSC and
multi-terminal DC links, FACTS devices (a STATCOM, a UPFC, a constant series impedance with its series element),
static var compensators (a continuous switched shunt whose BINIT the reader adds back to the bus shunt), induction
machines (custom single-cage design), on-load tap changers (COD 1) and transformer flow controls (COD 2 and 3). It is
read back with tap and shunt control applied. A tap changer or flow control is written only when the reader's formulas
give back its numbers to the bit (target and deadband from VMA and VMI, step from RMA, RMI and a whole NTP); one that
cannot be is left at its present tap and named in a `note:` line, so the proof then shows the difference. RAW keeps
lines and transformers in separate sections, so the read-back lists the branches in that order (lines, then
transformers, then FACTS series elements); the proof compares it with the input put in that order, and a second
`round trip:` line says whether the input in its own order also gives the same bits (a different branch order can
change the last bits of the admittance sums). Not written, with a note: generator dynamics and fault data, boundaries,
the asset bundles, MATPOWER DC lines; a name holding a single quote is written in double quotes. Refused (exit 1):
ZIP loads, zero-sequence data, an infinite generator limit, a transformer with line charging and a ratio or phase shift.

A revision 36 RAW output holds the revision 35 records under the revision 36 header, each record group under an `@!` heading
line that names its fields; the fields revision 36 added (load, generator and shunt names, DROOPNAME, BYPASS) are left to
their defaults. GridSim's revision 36 reader places fields by those headings, so it reads the file to the revision 35
model bit for bit. A RAWX output holds the same records as a revision 35 RAW output, field by field, with the section and
field names of the public RAWX layout and every number in the same round-trip form, and is read back the same way.

A UCTE-DEF output holds nodes (under `##Z` country blocks), lines, transformers and their regulation in the fixed
columns of the UCTE-DEF version 02 format. A bus whose base voltage is not one of the ten UCTE levels (750, 500, 380,
330, 220, 150, 120, 110, 70 or 27 kV, so 0 kV too) is refused, as are HVDC links, FACTS devices, static var
compensators, induction machines, ZIP loads, a second unit at one bus and a bus shunt with no transformer to carry it
(UCTE-DEF has no node shunt; a shunt rides on a transformer whose node 1 is the bus). A `.uct` input is written back
with its own node codes, country blocks and names, and each transformer with the file's own rated voltages, nominal
power and regulation record, so it reads back to the same model. Each line and transformer keeps its status code (0
real, 1 equivalent, 2 busbar coupler, or the matching out-of-operation code 8, 9 or 7 for one switched out), and the
file's own out-of-operation records are written back as they were. A branch with a negative R or X from another
format is written as an equivalent element (status 1), the only kind UCTE-DEF lets carry a negative impedance, and the
notes name it. For any other input the node codes are synthesised:
the country's code character, the bus id in five digits, the level code and busbar 1, under the country's `##Z` block.
`--ucte-country` names the country as its two-letter UCTE block name (for example `FR`, giving codes such as
`F0001251`); it is required when codes are synthesised (exit 1 without it, naming the flag), and a name that is not a
UCTE country, or `XX`, exits 2. A synthesised code never starts with `X`: UCTE keeps `X` for X-nodes, the boundary nodes
that other UCTE tools treat as boundary points. Transformers are written with nominal power from rating A (100 MVA,
the system base, when rating A is 0). Numbers are fixed-width text, written with as many decimals as their columns
hold, so a case from another format comes back to that precision, not bit for bit, and the proof then reports the
difference (exit 3) with the largest voltage difference; the notes name what UCTE-DEF cannot hold (start voltages of PQ
buses, voltage limits, ratings B and C, unit data beyond the power-flow record).

A CGMES output is one RDF/XML file holding the equipment, topology, steady-state hypothesis and state-variable content:
buses as TopologicalNodes with their base voltage and their stored voltage as an `SvVoltage` (where a solve starts),
one `EnergyConsumer` per bus with demand, one `LinearShuntCompensator` per bus with a fixed shunt, the in-service
generators as `SynchronousMachine`s (voltage control and target at slack and PV buses, the slack's machines with
`referencePriority` 1, reactive and active limits, `ratedS`, inertia), in-service branches as `ACLineSegment`s when
they are plain lines on one base voltage and otherwise as two-winding `PowerTransformer`s (ratio and phase shift as tap
changers; a ratio the linear changer cannot give back exactly as a one-point ratio table), and rate A as a permanent
`ApparentPowerLimit`. It is read back through GridSim's CIM reader with the starting voltages and the ratings, on the
input's system base (CGMES carries none); the read-back numbers buses 1..n in model order and lists lines before
transformers, so the proof compares it with the input put in that form, and a second `round trip:` line says whether the
input in its own numbering and order gives the same bits. CGMES holds ohms, siemens and kV, and the per-unit
conversion (a product or quotient by a base that is not a power of two) does not reach every double: about half the
values of a typical MATPOWER case have no CGMES value that converts back to the same bits. Each such value is written as
the nearest one (one ulp off after read-back), a `note:` counts them by kind and names the first, and the proof then
fails (exit 3) naming the first difference; a model whose values are images of physical values (one read from CIM, or
one already passed through CGMES once) converts exactly. Refused, every reason listed and nothing written (exit 1):
devices GridSim's CIM reader does not rebuild into the model the power flow solves (on-load tap changers and flow
controls, HVDC links, FACTS devices, static var compensators, induction machines), ZIP loads, a bus with no base
voltage, a slack bus with no in-service generator, machines at one voltage-controlled bus with different set-points.
Not written, with a note: out-of-service equipment, generator types outside the CGMES unit classes, converter flags,
fault and sequence data, cost curves, rates B and C, per-bus voltage limits, layout, boundaries and the asset bundles.

`--public` writes a public export: only what a `gridsim-provenance/1` manifest traces to published sources. The
manifest (`--provenance <file>`; by default `provenance.json` in the case folder, or `<file>.provenance.json` beside a
case file) lists its sources (id, title, publisher, URL, licence, and `published` true only for a source anyone can
obtain from its publisher) and, for each element, the sources it comes from: buses by id, branches and generators by
name, and optionally a bus's demand and fixed shunt by bus id and an on-load tap changer by its transformer's name. Any
other key, a cited source the manifest does not list, or an element citing no source is refused naming it. A bus is
kept when every source it cites is published; a branch, generator or tap changer when its name is unique, every source
it cites is published and its buses are kept. Unsourced demand and shunts on a kept bus are zeroed, layout coordinates
are zeroed, a PV bus left with no generator becomes PQ, and devices the manifest has no section for (HVDC links, FACTS
devices, static var compensators, induction machines, flow controls, mutual couplings, boundaries, the asset bundles,
MATPOWER DC lines) are stripped. Every strip is printed as a `public:` line (and in the result file's `public_stripped`
table), and the public model is then written and proved as any other conversion. No manifest, a manifest for another
case, or a public model with no slack bus or with a kept slack bus whose generators were stripped exits 1 with nothing
written: a public export is never made without evidence, and the built-in IEEE cases (no case folder) need
`--provenance`. `--provenance` without `--public` is a usage error (exit 2).

A JSON v2 output carries the fault sequence data JSON v1 cannot (N234: generator `subtransientXdPu` /
`negativeSeqXPu`, transformer `windings`, `r0Pu` / `x0Pu` and `neutralXPu`), so a v2 case converts to v2 and back
with its earth-fault data. Its manifest sets `"dispatch": false`; every branch is written in place as a line (a
transformer with `isTransformer`), except the trailing run of branches from the first one with winding data, which are
written as `transformers` elements (the v2 loader always builds those after the lines, so this keeps the branch order).
Its proof also compares every generator's X''d and X2 and every branch's R0, X0, windings and neutral reactances bit
for bit, and a second `round trip:` line says so. The files are byte-stable: the same model writes the same bytes on
every OS ("\n" line ends). The asset-dynamics and protection bundles are written too (N321, October 2026; they used to
be left out with a note): each asset class as a case-local template `gridsim.local.<class name>@1` (kinds
gridFollowingInverter, gridFormingInverter, oltc for a transformer class, lineType for a line class, protection), the
binding rules as the manifest's `bindings` (match types in lower case), each element's class as its `template`, and
the placed protective devices as the network file's `protections`, so the written case loads with the same classes,
rules and devices. A bundle that cannot be written exactly (a class name that cannot be a template id, such as one
taken from an imported template `id@rev`) is not written at all, and a note says why. Notes name what is not written:
such a bundle and the element template references into it, cost curves, DC lines, out-of-service branches (v2 refuses them for now), winding data
that would need the branches reordered, and the names given to unnamed generators (`unit-<n>`) and branches
(`branch-<n>`), since v2 requires names. A bus without a base voltage above 0 kV cannot be written to v2 (exit 1). The
v2 output needs the v2 schemas to be proved (found as [`validate`](#validate) finds them); without them nothing is
written (exit 1). An existing manifest or network file is never overwritten (exit 2).

Exit 0 when the round trip is bit-identical; 3 when it is not (a ZIP load or OLTCs written to MATPOWER, or tap changers
whose band RAW cannot give back exactly, or values CGMES cannot hold exactly, for example),
naming the first difference and, where the buses match, the largest voltage difference (|dVm|, |dVa|) and how many buses differ (the output is kept); 1 when the input does not load or the output cannot be written (a
NaN, or a value the format cannot hold, is refused naming it); 2 for a usage error or when the output already exists
(an existing `.m` file or a non-empty folder is never overwritten).

What each writer carries, notes and refuses for the full GB case, with the counts and the size of the resulting
difference: [gb-full through every writer](<../validation/studies/gb-full-export-loss-list.md>).

Result files: `--json <file.json>` or `--csv <file.csv>` write the result, including the round-trip verdict, to a file
([Result files](#result-files)); `convert` does not take `--out`, because its second argument is already its output.
