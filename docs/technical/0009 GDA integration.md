# 9. GDA integration

GridSim and the GDA data lake meet at five seams. The in-repo **LTDS builder** (`src/GridSim.Gda`) reads a distribution
network operator's Long Term Development Statement from the lake and assembles it into a `GridModel`
(`gridsim gda`). The **case writer** (`GdaModelWriter`) materialises that model as a GridSim case directory. The
**replay loop** (`GdaReplayLoop`, `gridsim gda-replay`) re-solves a case at every timestamp of a historical demand
series. The **bridge** is a WebSocket protocol, served by the GDA repository and spoken by `GridBridgeClient`, that
streams reconstructed grid states and answers on-demand physics queries. The **lake products** (estimated state,
presolved state, divergence, closure) are the Parquet stores the bridge streams from. This chapter specifies each seam
as the code implements it, including the Python scripts in the GDA repository that sit on the far side of the wire
(cited as `GDA NetworkModel/...`, `GDA Applications/...` and `GDA Scripts/...`, which abbreviate `NetworkModel/`,
`Applications/` and `Scripts/` in the `v1` directory of the GDA repository). Nothing from the lake is committed to this
repository.

Two rules cross every seam. Every dated input and every streamed frame is held behind the same 169-hour
no-forward-inference horizon, enforced independently on both ends of the bridge
([ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>)). Every figure the builder cannot take
from a published table is a named default in `GdaBuildDefaults`, and every row the builder drops is counted and named
in a diagnostic note.

The lake is addressed by the `--root` flag, else the `GDA_DATA_ROOT` environment variable, else `GDA_ROOT` (D06).
This chapter writes the lake root as `$GDA_ROOT`. The GDA repository's Python builders and the bridge server read no environment variable for the lake;
each resolves it as `DataSources/` in the `v1` directory of the repository, from its own location. User-level
procedures are in
[ch. 22](<../manual/0022 Estimating GB history.md>),
[ch. 23](<../manual/0023 Replaying estimated states.md>) and
[ch. 24](<../manual/0024 The GDA bridge.md>); the verbs' flags are in
[the CLI reference](<../manual/0019 CLI reference.md#gda>).

```
GDA lake (read-only)                         GridSim.Gda / GridSim.Core
--------------------                         --------------------------
NationalGrid/RAWData  (NGED LTDS, CSV)  -->  GdaPaths + LtdsReader  --\
UkPowerNetworks/RAWData (CSV)           -->  UkpnSource ---------------+--> LtdsDataset
NorthernPowerGrid/{Parquet,RAWData}     -->  NpgSource ---------------/        |
                                                                              v
NationalGrid/.../Network Capacity/substations.csv --> GeoLocator <--- GdaModelBuilder.Build
                                                                              |
                                      GridModel + GdaDiagnostics -------------+
                                           |                |
                              GdaModelWriter (case dir)   SteadyStateSolver (--solve)
                                           |
                                    GdaReplayLoop (gda-replay)

Derived/{estimated_state,presolved_state,...}  <-- GDA Python builders
        |                                            |
        +-- bridge_server.py --WebSocket--> GridBridgeClient --> SimFrame --> desktop / web views
```

## Model

### The lake and its paths

`GdaPaths` (`src/GridSim.Gda/GdaPaths.cs`) resolves logical dataset names under the root. The constructor throws
`DirectoryNotFoundException` for a missing root. A required file that is absent throws `FileNotFoundException` naming
the path; the builder never substitutes a default for a missing required table.

| Property | Location under the root | Read by |
|---|---|---|
| `LtdsNodes` | `NationalGrid/RAWData/LTDS Tabular Model/ltds-attachment-8-node.csv` | NGED builder: node code -> bus name |
| `LtdsCircuits` | `.../LTDS Tabular Model/ltds-table-1-circuit.csv` | NGED builder |
| `LtdsTwoWinding` | `.../LTDS Tabular Model/ltds-table-2a-two-winding-transformer.csv` | NGED builder |
| `LtdsThreeWinding` | `.../LTDS Tabular Model/ltds-table-2b-three-winding-transformer.csv` | NGED builder |
| `LtdsDemand` | newest `*table-3*.csv` in the same folder | NGED builder |
| `LtdsGeneration` | `.../LTDS Tabular Model/table-5-generation.csv` | NGED builder; optional (absent means no generation) |
| `Substations` | `NationalGrid/RAWData/Network Capacity/substations.csv` | geolocation |
| `TecRegister` | newest `tec-register-*.csv` in `NESO/RAWData/Transmission Entry Capacity (TEC) register` | `--fleet` |
| `InterconnectorRegister` | newest `interconnector-register-*.csv` in `NESO/RAWData/Interconnector Register` | `--fleet` |
| `EmbeddedRegister` | newest `embedded-register-*.csv` in `NESO/RAWData/Embedded Register` | `--fleet` |
| `ConstraintLimits24Mo` | newest `24-months-ahead-constraint-limit*.csv` in `NESO/RAWData/24 Months Ahead Constraint Limits` | `--fleet` boundary table |
| `LtdsFaultLevel`, `BusMapping` | `ltds-table-4-fault-level.csv`; `Curtailment Bus Index/bus_mapping.csv` | declared, read by no verb |

**Register vintage.** Publication dates are carried in file names (`tec-register-05-may-2026.csv`), parsed by
`GdaPaths.TryParseDateFromName` as `d-MMMM-yyyy` or `d-MMM-yyyy`. `GdaPaths.PickNewest` orders candidates by that date.
Modification times are never used (they differ between machines, as for `DataSourceTable` below): a single candidate is
taken whether or not its name is dated, but when there are several, every one must carry a name date, otherwise the
pick throws `InvalidDataException` naming the undated candidates. Equal dates break by descending case-insensitive
name. When the paths carry an as-of instant (`AsOfUtc`), every candidate whose *name* proves a later
publication is excluded first, so a model materialised for a historical replay cannot draw on a register published
inside the replayed window. An undated name is never excluded by the as-of (it has no publication date to compare);
a lone undated file under an as-of may therefore post-date it, which the name cannot rule out. `gridsim gda` sets the as-of to `ForwardInferenceGuard.Horizon(UtcNow)`
(now minus 169 h); `--as-of` overrides it and accepts `yyyy-MM-dd`, `yyyy-MM-ddTHH:mm`, `yyyy-MM-ddTHH:mm:ss`,
`yyyy-MM-ddTHH:mm:ssZ` or `o`, read as UTC. Any other text is a usage error (exit 2). The as-of governs the NGED demand
glob and the four NESO registers. The UKPN and NPg readers do not take it.

**Directory datasets.** `DataSourceTable.Open` (`src/GridSim.Gda/Csv/Tabular.cs`) accepts a `.parquet` file, a `.csv`
file, or a directory. A directory holding Parquet parts reads all of them in ordinal path order. A directory holding
CSVs picks the one whose name carries the strictly newest publication date and throws, naming the candidates, when
two files tie or any candidate is undated. Enumeration order and modification times are never consulted, so the
Windows and Linux builds read the same file. A Parquet column absent from one part reads as null on that part's rows
and never shifts the others. Nested Parquet columns read as null.

**CSV reading.** `CsvTable` (`src/GridSim.Gda/Csv/Csv.cs`) is a streaming RFC-4180 reader. It strips a UTF-8 byte-order
mark, trims and tab-folds header names, accesses cells by case-insensitive column name, and supports quoted commas,
doubled quotes, embedded newlines and short rows (missing trailing cells read as empty). A row with more cells than
headers is refused when an extra cell is not blank (an unquoted comma in a value would shift the rest of the row), and
a column named twice in the header (after trimming and case folding) is refused when it is read; the Parquet reader
refuses a doubly named column the same way. `CsvRow.Double` (and the Parquet and UKPN readers' text cells, through
`GdaNumber`) removes `%` and thousands commas, parses with `NumberStyles.Float` and the invariant culture and returns
the fallback for a blank cell or text that is not a number in any form. NGED's `-` placeholder therefore reads as 0.
Decision (GR-379, October 2026): a number written in a form GridSim would have to guess at is refused with an
`InvalidDataException` naming the column and cell: a comma that is not a thousands separator in its proper place
(`0,5`, a decimal comma, used to read as 5), parentheses (`(5)` used to read as -5), a trailing sign or a currency
symbol. Refusing stops the build where silently reading the wrong value would have built a wrong model.

### LTDS records

Every operator is normalised into one `LtdsDataset`: a name, an `ImpedanceConvention`, node, circuit, two-winding,
three-winding, demand and generation lists, and the flag `TransformerPercentOnRating`
(`src/GridSim.Gda/GdaModelBuilder.cs`, `src/GridSim.Gda/Ltds/LtdsModel.cs`).

| Record | Fields |
|---|---|
| `LtdsNode` | `Licence`, `Code`, `Name`, `BspGroup`, `GspGroup`, `Status` |
| `LtdsCircuit` | `Licence`, `GspGroup`, `BspGroup`, `Node1`, `Node2`, `Status`, `KvBase`, `CircuitCode`, `R1Ohm`, `X1Ohm`, `B1MicroS`, `R0Ohm`, `X0Ohm`, `WinterRatingMva`, `SummerRatingMva`, `LengthKm`, `Equipment` |
| `LtdsTransformer2` | `Licence`, `GspGroup`, `Node1`, `Kv1`, `Node2`, `Kv2`, `Status`, `VectorGroup`, `R1Ohm`, `X1Ohm`, `X0Ohm`, `MinTap`, `MaxTap` (percent of nominal ratio), `NominalRatingMva`, `EmergencyRatingMva` |
| `LtdsTransformer3` | `Licence`, `GspGroup`, `Node1`, `Kv1`, `Node2`, `Kv2`, `Node3`, `Kv3`, `Status`, `VectorGroup`, `R1Ohm`, `X1HvLvOhm`, `X1LvTertOhm`, `X1HvTertOhm`, `MinTap`, `MaxTap`, `NominalRatingMva`, `EmergencyRatingMva` (no `X0Ohm`; the builder does not use `MinTap` and `MaxTap`) |
| `LtdsDemand` | `GspGroup`, `BspGroup`, `PrimarySubstation`, `Node`, `Kv`, `MaxDemandMw`, `PowerFactor`, `FirmCapacityMva`, `Status` |
| `LtdsGeneration` | `GspGroup`, `Bsp`, `PrimarySubstation`, `Kv`, `ExportCapacityMw`, `FuelType`, `Status` |

The field names carry the unit the builder assumes. `LtdsCircuit.IsSeries` is true when `|R1| + |X1|` exceeds
`GdaBuildDefaults.TieImpedanceThresholdOhm` (1e-9 ohm). The field is named `...Ohm` for the ohmic convention and holds
a percent figure under the percent convention.

### Assembly, step by step

`GdaModelBuilder.Build(LtdsDataset, GdaBuildOptions)` is a pure function of its two arguments. The steps run in this
order. Every count named below is a field of `GdaDiagnostics` and is printed by the verb.

**1. Region and status filters.** A circuit or transformer is in region when its `Licence` equals
`GdaBuildOptions.Licence` (case-insensitive; null passes everything) and its GSP group equals `GspGroup` after
canonicalisation. `GspKey` lower-cases the name, turns punctuation into spaces, deletes a voltage token
(`\b\d{2,3}( \d{2})?\s*kv\b`) and the station words `s g p`, `g s p`, `gsp`, `sgp`, `s stn`, `stn`,
`grid supply point`, `power station`, `grid`, `substation`, `bsp`, `main`, then removes whitespace. The circuit table's
"Berkswell 132kV" and the generation table's "Berkswell 132 kV S Stn" therefore carry the same key. An element is alive
unless `EnergisedOnly` is set (default true) and its status contains, case-insensitively, one of `out of service`,
`decommiss`, `removed`, `future`, `planned`, `abandon`, `proposed`. Node-table statuses are not tested: the node table
only supplies names.

**2. Voltage cut.** Circuits are kept when `KvBase >= MinKv`, two-winding transformers when `max(Kv1, Kv2) >= MinKv`,
three-winding transformers when the largest of the three voltages is at or above it. The region-and-status-filtered
lists are retained alongside the cut lists because the elements removed only by the cut are the network below the cut,
which step 7 needs.

**3. Node voltages.** Every endpoint of a retained element records a voltage, the circuit's `KvBase` or the winding's
voltage, keeping the maximum per node code. An endpoint with a non-positive voltage records nothing and so never
enters the union-find; an element touching it is skipped.

**4. Busbar ties.** A union-find (`Dsu`, case-insensitive node codes, path compression) merges the two nodes of every
retained circuit that is not series. `BusbarTiesMerged` counts the unions that joined two different sets. A zero
impedance row cannot be a branch (its admittance is infinite), so it is always merged. When no word of its `Equipment`
text starts with any of `busbar`, `bus bar`, `bus-bar`, `bus section`, `coupler`, `disconnector`, `isolator`, `switch`,
`breaker`, `jumper`, `link` (so "Switchgear" is a tie and "Interlink cable" is not), the row is a cable or line whose R and X cells were blank. It is still merged, counted in
`ZeroImpedanceNonTieCircuits`, and the first five are named in a `WARNING ties` note. The UKPN and NPg readers label
every row `Circuit`, so every zero-impedance row of those datasets is reported. The kV of a merged group is the maximum
kV of its members.

**5. Buses.** `GetBus(code)` assigns bus ids lazily, in creation order, to union-find roots when an element touches
them. The order is: series circuits in file order (from-node first), two-winding transformers (first node first), then
three-winding transformers (first node, second node, star node, third node). An element whose two ends sit in one tie
group still creates that bus before it is skipped, so a bus can carry no branch; it is isolated and counted in `Buses`
and `Islands`. A tie group that no series circuit or transformer touches never becomes a bus. A bus takes the voltage of its group, the name of the root's node-table entry
(the root's own code when the table has none), and the GSP group of the first element that reached it. The root is the
set a merge keeps, so a bus name depends on the order of the tie rows; the order is the file order and is therefore
reproducible. A synthesised three-winding star node is named `STAR <first node prefix>`, carries the highest winding
voltage and is never a slack candidate.

**6. Branches.** The equations below use `Zbase = kV_ref^2 / BaseMva` with `kV_ref = RefKv` when positive and otherwise
`GdaModelBuilder.ParseGspKv(gspGroup)`. `ParseGspKv` returns the first of 400, 275, 132, 66, 33, 25, 11 that equals any
integer token in the group name, else `GdaBuildDefaults.DefaultGspKv` (132 kV). `RefKv` is an option of the library;
the verb does not expose it.

```
ohmic convention (no shipped reader uses it)     percent convention (NGED, UKPN, NPg)
  R_pu = R1_ohm / Zbase                            s       = BaseMva / 100
  X_pu = X1_ohm / Zbase                            R_pu    = R1_pct / 100 * s          (circuits)
  B_pu = B1_uS * SusceptanceToSiemens * Zbase      X_pu    = X1_pct / 100 * s
                                                   B_pu    = B1_pct / 100 / s          (admittance: the inverse factor)
  transformer R_pu, X_pu = ohm / Zbase             transformer, percent on rating (no reader uses it now):
  (the rating plays no part)                         Z_pu = Z_pct / 100 * BaseMva / rating_MVA
                                                     (rating <= 0 -> TransformerRatingFallbackMva = 100 MVA)
                                                   transformer, percent on 100 MVA (NGED, UKPN, NPg):
                                                     Z_pu = Z_pct / 100 * BaseMva / 100
```

- *Series circuits.* Each retained, live, series circuit becomes a `Branch(from, to, R_pu, X_pu, B_pu)` with
  `RateAMva = WinterRatingMva` and name `<node1 prefix>-<node2 prefix> <kV>kV`. A circuit whose two ends merged into
  one bus is skipped. Names are made unique by appending `#2`, `#3`, ... in order. The prefix is the node code up to
  its first underscore.
- *Two-winding transformers.* The branch is a series impedance at tap 1.0 with no shunt and no phase shift, because the
  per-unit bases carry the voltage ratio. A reactance with `|X_pu|` below 1e-6 is replaced by 1e-4
  (`DegenerateTransformerXPu`, `MinTransformerXPu`) so the admittance stays finite. Every transformer branch is built
  high voltage to low voltage: when `Kv1 < Kv2` the ends are swapped. The solver's OLTC loop lowers a tap to raise the
  bus it controls, which is correct only when that bus is the To end, so the swap puts every regulated bus on the To
  end. Bus ids still follow listing order. `RateAMva` is the nominal rating. A transformer with `Kv1 == Kv2` is not
  swapped and gets no OLTC.
- *Three-winding transformers.* A star (T) equivalent from the three measured reactances:

```
xH = 0.5 * (X_HL + X_HT - X_LT)       xL = 0.5 * (X_HL + X_LT - X_HT)       xT = 0.5 * (X_HT + X_LT - X_HL)
leg(x) = x                                      if x > 0
leg(x) = 0.01 * |X_HL| + 1e-6                   if x <= 0       (StarLegFloorFraction, StarLegFloorAbs)
H leg: branch  Node1 bus -> star  R_pu = TxPu(R1),  X_pu = TxPu(leg(xH))
L leg: branch  star -> Node2 bus  R_pu = 0,         X_pu = TxPu(leg(xL))
T leg: branch  star -> Node3 bus  R_pu = 0,         X_pu = TxPu(leg(xT))       (only when Node3 is on the network and Kv3 > 0)
```

  The builder compares no voltages for the legs: the table is taken to list the high-voltage winding first and the
  low-voltage winding second. The largest of the three voltages sets only the star bus kV. All three legs carry the
  unit's nominal rating. The star bus is created for every retained three-winding unit whose first two nodes are on the
  network. A three-winding unit gets no OLTC. The NGED and UKPN readers produce
  three-winding records (UKPN from its Table 2b file).
- *OLTCs.* With `CreateOltcs` (default true) every two-winding transformer with `Kv1 != Kv2` is classified by its
  published tap range, in percent of nominal ratio. `Valid`: finite, `min <= max`, `min <= 0 <= max`, both within
  +/-50 %, and not 0 %/0 %. The OLTC is then `Oltc(name, lv_bus, TargetVpu 1.0, MinTap 1 + min/100, MaxTap 1 + max/100, StepPu 0.0125,
  DeadbandPu 0.01)`. The tap is on the high-voltage (From) end, so +5 % is ratio 1.05, which lowers the low-voltage
  voltage, the sense the solver uses. A published -10 %/+10 % gives the limits 0.9 and 1.1 exactly. A range of
  0 %/0 % is an off-load unit: no OLTC, counted in `OltcsWithoutTapRange` with an `OLTC:` note. Every other range (not
  finite, minimum above maximum, excluding 0 %, or beyond +/-50 %, which is where tap positions such as 1..17 land) is
  invalid: no OLTC, counted in `OltcsWithInvalidTapRange`, with a `WARNING OLTC:` note naming the first five units. The
  builder asserts, throwing `InvalidOperationException`, that every OLTC it emits controls the To end of its branch.

**7. Below-cut owner map.** When `MinKv > 0`, the network below the cut is not built, but its demand is real load
drawn through the boundary. A simultaneous breadth-first search starts from every *boundary* node, a node that lies in
the below-cut graph and is also a built bus, over the in-region, live elements that only the cut removed (circuits
below the cut, transformers whose larger voltage is below it, three-winding units whose largest voltage is below it).
Seeds are enqueued in ascending bus id, then ascending node code; neighbours in data order. A node equidistant in hops
from two boundary buses therefore goes to the lower bus id. The result is `node -> boundary bus`.

**8. Demand.** Every `LtdsDemand` row with `MaxDemandMw > 0` and a live status is joined by *node code alone*; the GSP
group does not filter demand. A node on the built network adds to its bus. A node not on the network but in the
owner map adds to its boundary bus (`AggregatedDemand`, `AggregatedDemandMw`). Any other node is off-network
(`OutOfScopeDemand`, `OutOfScopeDemandMw`; a `Demand:` note under a `--gsp` or `--licence` carve, a `WARNING demand:`
note in an uncarved build). A node that is in the union-find but whose group never became a bus is a dangling leaf
(`UnmatchedDemand`). For each matched row:

```
pf = PowerFactor                      if 0 < PowerFactor <= 1
pf = DemandPowerFactor (0.98)         otherwise
Pd += MaxDemandMw                     Qd += MaxDemandMw * tan(acos(pf))
```

  A blank NGED or UKPN power-factor cell reads as 1.0 and gives `Qd = 0`; the 0.98 default replaces only an
  impossible figure. A row with a real `PrimarySubstation` name (at least one letter or digit; `-` and blanks are NGED
  null markers) also records `(GspKey(group), key(primary)) -> bus` for the generation join, where the key is `NormKey`
  (lower-case with whitespace removed) for NPg, UKPN and caller-built datasets, and for NGED (`LtdsDataset.
  CanonicalPrimaryJoin`, set by `LoadNged`; N184, October 2026) `CanonicalPrimaryKey`: lower-case, a `.` between digits
  kept and any other punctuation a space, voltage tokens with or without `kV` removed (`33 11 kV`, `66/11kV`, `11`,
  `6.6`), the station words `s stn`, `stn`, `substation`, `primary`, `prim`, `p s`, `ps`, `s s`, `ss`, `grid`, `bsp`,
  `main` and a lone `t` removed, then only letters and digits kept (`Wigston Magna 33 11 kV S Stn` and `Wigston Magna
  11` both give `wigstonmagna`). A name the key empties is not a join key. The canonical key folds a site's voltages
  together (`Courthouse Green 11` and `Courthouse Green 6.6`), which the `Demand:` note below counts. Within one group the last-listed row's bus wins, and a primary whose rows sit on
  more than one bus is counted in `PrimariesOnSeveralBuses` with a `Demand:` note. `DemandPoints` counts matched plus
  dangling rows, and `TotalDemandMw` the matched MW.

**9. Embedded generation.** A dataset-wide register maps each normalised primary name to the set of GSP groups that
list it in the demand table (every live row, whatever the carve, so a carve cannot make a colliding name look unique).
For each generation row with `ExportCapacityMw > 0`:

1. Under a `--gsp` carve, a row whose group key differs from the carve's is skipped without a count.
2. A `-` or blank primary is unmatched. A primary absent from the register is unmatched.
3. The owning group is the row's own group when that group lists the primary. Otherwise, when exactly one group lists
   it, that group (`GenJoinedByUniqueName`, `Generation join:` note). Otherwise the row is left unplaced
   (`GenAmbiguousPrimary`, `GenAmbiguousPrimaryMw`, `WARNING generation join:` note naming five).
4. `(owner, primary)` must be in the demand map, else the row is unmatched.
5. With `ConnectedGenerationOnly` (default true; `--all-gen` clears it) the status is classified. The not-connected
   words are tested first, because several contain "connected" and `commissioned` sits inside `decommissioned`.

| Class | Status contains (case-insensitive) | Treatment |
|---|---|---|
| Not connected | `accept`, `offer`, `not yet`, `not connected`, `unconnected`, `disconnect`, `pending`, `contract`, `applied`, `application`, `enquir`, `quot`, `consent`, `scoping`, `construct`, `withdrawn`, `terminat`, `out of service`, `decommiss`, `removed`, `future`, `planned`, `abandon`, `proposed` | Left out; `GenNotConnected`, `GenNotConnectedMw`, `Generation:` note listing the statuses |
| Connected | `connected`, `energised`, `energized`, `in service`, `commissioned`, `operational` | Built |
| Unknown | anything else, including blank | Left out, never guessed; `GenUnknownStatus`, `GenUnknownStatusMw`, `WARNING generation:` note |

6. The fuel string is classified by `FuelMap.Classify` into a type and an inertia constant `H` (the keywords are Core's one
   fuel taxonomy, `GridSim.Core.Model.FuelTypes`, GDA reader, matched as whole words by their start; the
   `H` per canonical type is `FuelMap.InertiaH`), and the unit's dispatch
   is `output = capacity * FuelMap.CapacityFactor(type)` (nameplate is not dispatched). Without `--neg-gen`, the
   builder emits `Generator(bus, Pg = output, Qg = 0, Vg = 1.0, Qmin = -0.4 * cap, Qmax = +0.4 * cap, Pmin = 0,
   Pmax = cap, MBase = cap, H, name = "<primary> <type>")` (`EmbeddedGenQRatio` = 0.4). With `--neg-gen` it instead
   subtracts `output` from the bus `Pd`, so a bus can end up with negative demand. `TotalGenCapacityMw` accumulates
   capacity in both modes.

The counts cover only rows that joined a bus, so the reported MW is exactly what left the model; unplaced rows stay in
`UnmatchedGen`.

| Fuel rule (tested in this order) | Type | H (s) | Capacity factor |
|---|---|---:|---:|
| `nuclear` | `nuclear` | 5.0 | 0.90 |
| `pumped`, `pump storage`, `pump-storage` | `pumped-storage` | 3.0 | 0.10 |
| `ccgt`, `combined cycle` | `ccgt` | 5.0 | 0.45 |
| `ocgt`, `open cycle`, `gas turbine` | `ocgt` | 4.0 | 0.05 |
| `chp`, `cogen` | `chp` | 2.5 | 0.55 |
| `coal` | `coal` | 4.0 | 0.00 |
| `biomass`, `biogas`, `anaerobic`, `landfill`, `waste`, `energy from waste` | `biomass` | 4.0 | 0.65 |
| `hydrogen`, `fuel cell`, `fuel-cell`, `electrolys`, `electrolyz` (tested before `hydro`) | `storage` | 0.0 | 0.05 |
| `hydro` | `hydro` | 3.0 | 0.35 |
| `tidal`, `wave`, `marine` | `tidal` | 0.0 | 0.20 |
| `wind offshore`, `offshore wind` | `wind-offshore` | 0.0 | 0.45 |
| `wind` | `wind-onshore` | 0.0 | 0.30 |
| `solar`, `photovoltaic`, `pv` | `solar` | 0.0 | 0.10 |
| `storage`, `battery`, `bess` | `storage` | 0.0 | 0.05 |
| `interconnector`, `hvdc` | `interconnector` | 0.0 | 0.50 |
| `gas` | `gas` | 4.0 | 0.35 |
| `diesel`, `oil`, `reciprocating`, `engine` | `recip` | 1.0 | 0.10 |
| blank or anything else | `other` | 0.0 | 0.30 |

**10. Slack buses and island labelling.** A DNO network is fed from the transmission system at every grid supply
point, so the builder makes one slack per GSP group, not one per model. For each non-star bus the key is the
normalised GSP group of the bus (`NormKey`), or `_isl<component>` when the bus has none. Per key the slack is the bus
with the highest voltage; a voltage tie within 1e-6 kV goes to the bus with more branches, and a remaining tie to the
lowest bus id. Islands are the connected components of the branch graph, labelled by an iterative depth-first
traversal. A dataset with `InfeedPerGspGroupPerIsland` (UKPN and NPg; false for NGED) instead keys each bus by
GSP group and island, treats a bus whose name begins with a group's name (trailing voltage word removed, whole words)
as a site bus of that group whatever its own tag, and makes every site bus at the highest site voltage of a key a slack
(each is a section of the GSP board); a key with no site bus keeps the single choice above. Any island with no slack
gets its highest-voltage non-star bus (lowest id on a tie). Every other bus is
`Pq`. Slack buses start at `Vm = 1.0`, `VaDeg = 0`. The report's "primary" slack is the highest-voltage slack in the
largest island. A model with several slack buses is a multi-island model; the validator says so as a warning.

**11. Model, geolocation, validation.** The model name is `<dataset name>[ [<gsp>]][ >=<kV>kV]`, `BaseMva` is the
option, `FrequencyHz` is 50 (`SystemFrequencyHz`). With `Geolocate` (default true) and an existing, non-empty
`SubstationsPath`, `GeoLocator.Apply` places the buses (see Geolocation); an empty file or a reader exception becomes a
note and never fails the build. `GridModel.Validate()` findings are appended to the notes as `validation: ...` and
never block the build.

`GdaDiagnostics` fields, all printed or noted by the verb:

| Field | Meaning |
|---|---|
| `RawNodes`, `Buses`, `BusbarTiesMerged` | Node-table rows, built buses, effective ties |
| `SeriesBranches`, `Transformers2`, `Transformers3`, `StarNodes` | Retained element counts after the cut (`Transformers2` and `Transformers3` count retained rows, including any whose ends merged) |
| `DemandPoints`, `TotalDemandMw`, `UnmatchedDemand` | Matched plus dangling rows; matched MW; dangling rows |
| `AggregatedDemand(Mw)`, `OutOfScopeDemand(Mw)` | Folded below the cut; off-network |
| `Generators`, `TotalGenCapacityMw`, `UnmatchedGen` | Built units, their capacity, unplaced rows |
| `GenNotConnected(Mw)`, `GenUnknownStatus(Mw)`, `GenJoinedByUniqueName`, `GenAmbiguousPrimary(Mw)`, `PrimariesOnSeveralBuses` | Generation filters and join outcomes |
| `OltcsWithoutTapRange`, `OltcsWithInvalidTapRange`, `ZeroImpedanceNonTieCircuits` | Tap-range and tie warnings |
| `Islands`, `LargestIslandBuses`, `BusesByKv`, `Notes` | Components, largest component, bus count per rounded kV, the note list |

### Source adapters

**NGED** (`GdaModelBuilder.LoadNged`, `src/GridSim.Gda/Ltds/LtdsReader.cs`) is the reference schema and the default
(`--dno nged`). Convention `PercentOn100Mva` (`GdaModelBuilder.NgedCircuitConvention`) for the circuits, with R1, X1
and B1 read together on one base, and `TransformerPercentOn100Mva` for the transformers: every impedance is a percentage
on 100 MVA, whatever the voltage. The proof is on the constant: for circuits that NGED's LTDS Table 4 also lists as a
single-circuit impedance, the percent reading matches the table's per-unit value in both R and X to the four published
decimals at 33, 66 and 132 kV, while the GSP-referred ohmic reading misses by the factor `Zbase / 100` (10.89 at 33 kV,
1.7424 at 132 kV). Columns read, by name:

| Table | Columns |
|---|---|
| Nodes | `Licence`, `Code`, `Name`, `BSP Group`, `GSP Group`, `Status` |
| Circuits | `Licence`, `GSP Group`, `BSP Group`, `Node 1`, `Node 2`, `Circuit Status`, `Operating Voltage (kV)`, `Circuit Code`, `R1`, `X1`, `B1`, `R0`, `X0`, `Winter`, `Summer`, `Circuit Length (km)`, `Equipment` |
| Two-winding | `Licence`, `GSP Group`, `Node 1`, `Operating Voltage 1`, `Node 2`, `Operating Voltage 2`, `Status`, `Vector Group`, `R1`, `X1`, `X0`, `Min Tap`, `Max Tap`, `Nominal Rating`, `Emergency Rating` |
| Three-winding | the same with `Node 3`, `Operating Voltage 3`, `X1 HV-LV`, `X1 LV-Tertiary`, `X1 HV-Tertiary` |
| Demand | `GSP Group`, `BSP Group`, `Primary Substation`, `Node`, `Operating Voltage`, `Current Year Max Demand`, `Current Year Power Factor`, `Firm Capacity of Substation`, `Status` |
| Generation | `GSP Group`, `Bulk Supply Point`, `Primary Substation`, `Connection Voltage`, `Agreed Export Capacity`, `Fuel Type`, `Connection Status` |

The NGED reader skips a row at read time when: a node-table row has an empty `Code`; a circuit, two-winding or
three-winding row has an empty `Node 1` or `Node 2` (an empty `Node 3` is kept); a demand row has an empty `Node` and an
empty `Primary Substation`; a generation row has an empty `Primary Substation` and an empty `Bulk Supply Point`. The
UKPN and NPg adapters below apply their own row rules. The licences in the `Licence` column are `EMIDS`, `WMIDS`,
`SWEST` and `SWALES`.

**UKPN** (`UkpnSource.Load(root)`, `--dno ukpn`) reads `UkPowerNetworks/RAWData`. Convention `PercentOn100Mva` with
`TransformerPercentOnRating = false` (Table 2a X falls with rating and X x rating / 100 has median 23.0 %, so the
published percent is on 100 MVA, as NPg's headers for the same table state) and `InfeedPerGspGroupPerIsland = true`. Files are found by name anywhere under that folder, with or without the `ukpn-`
prefix: `ltds-table-1-circuit-data.csv`, `ltds-table-2a-transformer-2w.csv`,
`ltds-table-3a-load-data-observed.csv` and the optional `ltds-table-5-generation.csv`. Two equally close matches throw,
naming them. UKPN publishes no node dictionary, so nodes are the circuit and transformer end nodes; each substation
resolves to its lowest-voltage node, which is where load connects. Circuits read `FromNode`, `ToNode`,
`OperatingVoltage (kV)`, `GridSupplyPoint`, `LicenceArea`, `Positive_Sequence_Impedance_R_Percent`,
`Positive_Sequence_Impedance_X_Percent`, the susceptance column `Susceptance_B_ Percent` (the header carries a stray
space; two spellings are also tried) and the ratings in amperes, converted by `MVA = sqrt(3) * kV * A / 1000`.
Transformers read `HV_Node`, `LV_Node`, `Voltage_HV (kV)`, `Voltage_LV (kV)`, `Vector_Group`, the percent R, X and zero
sequence X, `Tap_Range_Min_Percent`, `Tap_Range_Max_Percent` and `Transformer_Rating_MVA_Winter` and `_Summer`. UKPN
publishes the tap range as percent OF the nominal ratio (90 / 110), not as the offset NGED and NPg publish (-10 / +10),
so the reader subtracts 100 (exact for whole numbers; 100 / 100 becomes 0 / 0, a fixed-ratio unit; a blank cell
becomes NaN, which the tap-range check refuses with a WARNING naming the unit). Demand
takes the newest column matching `Maximum_Demand_YY_YY_MW` (and its `_PF` partner) and keeps, per substation, the
season with the largest demand. Generation reads `InstalledCapacity_MVA` as the export capacity in MW,
`Connection_Voltage_kV`, `FuelType` and the status column `Connected_Accepted`. Status is always `Energised`, so
`EnergisedOnly` removes nothing. For UKPN only, the reader also takes the optional three-winding table
`ltds-table-2b-transformer-data-3w.csv` (HV, LV1, LV2 nodes; X HV-LV1, LV1-LV2, HV-LV2; R HV-LV1; the HV tap range and
ratings); node codes used for different substations in different licence areas are keyed `<code>@<area>` (a code
whose rows in every area share the first word of the substation and of the GSP name is one boundary node); a demand row
is shared over the LV nodes of the transformers whose LV substation it is, in proportion to their winter ratings (a row
whose node no transformer feeds moves to the one feeder node); a row whose board reaches, through circuits only, the HV
side of a transformer feeding another substation with its own demand row is the sum of those rows and is kept as a
join-only `LtdsDemand` (no load; generation naming it still joins its bus). The reader's notes on these go into
`LtdsDataset.SourceNotes`, which the builder prints first among its notes. A table with no demand column, or no positive
demand on any known substation, throws `InvalidDataException`: an empty network that "converged" is never produced. The
UKPN CSVs on the reference lake hold headers only, so `--dno ukpn` exits 1 there (see "A failing source" in the worked
example).

**NPg** (`NpgSource.Load(root)`, `--dno npg`) reads `NorthernPowerGrid`. Convention `PercentOn100Mva` with
`TransformerPercentOnRating = false`: transformer percents are on 100 MVA, unlike UKPN. Each dataset (`LTDS_Appendix_3_Circuit_Data`,
`LTDS_Appendix_4_Transformer_Data`, `LTDS_Appendix_5_Load_Information`, `LTDS_Appendix_7_Generation`) is opened
Parquet-first from `Parquet/<dataset>`, falling back to the CSV in `RAWData/<dataset>` when Parquet is absent or
unreadable. A node is the pair (busbar name upper-cased with whitespace collapsed, kV formatted `0.#`), so a circuit
joins two same-voltage busbars and a transformer joins one substation's primary and secondary busbars (a transformer
with equal voltages or a non-positive voltage is skipped). A transformer winding whose own pair names no node joins,
by site name (`NpgSource.SiteName`, with no ` T<n>` dropped: T1 and T2 are different units), the circuit busbar of
that site whose voltage is nearest the winding's within 5 %, when exactly one is nearest: the
transformer table writes `Harrogate GT2B` where the circuit table writes `Harrogate GT2B 132kV` and `Harrogate GT2B
33kV`, and without the site join 308 windings would name no busbar. The build notes
open with the count (`Transformer windings: <n> named no busbar ...`). NPg sets `InfeedPerGspGroupPerIsland`. A load row joins the node its own (name, kV) pair names; when
that pair names no circuit or transformer node, it joins by site name (`NpgSource.SiteName`: voltage
tokens with `kV` such as `66/20kV` removed, upper-cased, whitespace collapsed, and a trailing ` T<n>` dropped from the
load's name) the node of that site whose voltage is nearest the load's within 5 % (`SiteJoinVoltageTolerance`; NPg
publishes transformer secondaries at 11.5 kV where the load table says 11.0), else it keeps its own key and stays
off-network. The GSP group is `S/S Group` (circuits, transformers, load)
or `Grid Supply Point` (generation). Demand takes the newest column matching `YYYY/YY Net (Observed) Maximum Load (MVA)`
(an unversioned name is the fallback), takes the power factor from the `(PF)` partner (default 0.98 when outside
0 to 1) and sets `MaxDemandMw = MVA * PF`. Generation reads `Installed Capacity (MVA)` as MW and the status column
`Connected/Accepted`. As for UKPN, a missing demand column or a table with no positive demand throws.

**NESO** (`src/GridSim.Gda/Neso`) is the transmission overlay, read by `gridsim gda --fleet` and not by the network
build. `NesoReader` reads three CSV registers and one limits table:

| Reader | Columns | Connected rule |
|---|---|---|
| `Tec` -> `TecEntry` | `Project Name`, `Customer Name`, `Connection Site`, `Stage`, `MW Connected`, `Cumulative Total Capacity (MW)`, `Project Status`, `HOST TO`, `Plant Type`, `Project ID` | status or stage contains `Connect`, or `MW Connected > 0`; capacity is `MW Connected` when positive, else the cumulative figure |
| `Interconnectors` -> `InterconnectorEntry` | `Project Name`, `Connection Site`, `MW Import - Total`, `MW Export - Total`, `MW Import - Current`, `MW Export - Current`, `Project Status`, `HOST TO` | status contains `Built` or `Connect`, or current capacity > 0; capacity is the larger current figure (connected fleet) or the larger total figure |
| `Embedded` -> `EmbeddedEntry` | `Project Name`, `Connection Site`, `MW Connected`, `Cumulative Total Capacity (MW)`, `Project Status`, `HOST TO`, `Plant Type` | status contains `Built` or `Connect`, or `MW Connected > 0`; capacity as for TEC |
| `BoundaryLimits` -> `BoundaryLimit` | the wide 24-months-ahead table: one column per boundary code beyond `YEAR`, `Week No` | per boundary: minimum, median (middle sample of the sorted positive values), maximum, sample count |

`NationalFleetBuilder.Build(paths, connectedOnly: true)` classifies each unit with `FuelMap` (TEC and embedded by
`Plant Type`, interconnectors always `interconnector` with H = 0) and builds `NationalFleet`: total capacity,
synchronous capacity (H > 0), converter capacity, `InertiaGvaS = sum(H * capacity) / 1000`, and per-technology
categories with dispatch `capacity * CapacityFactor`. `--fleet` then prints the category table, the inertia at full
dispatch (the whole synchronous fleet online) and at typical dispatch (`sum(H * capacity * CapacityFactor) / 1000` over
the synchronous units), the largest-infeed trip (RoCoF, nadir, limits assessment, TSGB sizing), an N-1 RoCoF screen of
the fleet and the boundary limit table. The reference lake holds the NESO registers as Parquet only, so `--fleet`
exits 1 there (see Limits). The verb checks `--fleet` before `--schema` and `--dno`, and it is independent of the DNO
source. `PhaseAngleInferedReader` and `QuarantineLiveStore` also live in `GridSim.Gda`; they serve the
estimator's angle reference ([ch. 6](<0006 State estimation.md>)) and the RTU interposer
([ch. 20](<0020 The RTU and SCADA interposer.md>)) respectively and are not part of the network build.

### Geolocation

`GeoLocator` (`src/GridSim.Gda/Geo/GeoLocator.cs`) gives a built model real coordinates on the unit square used by
the map views.

```
key(name)   = upper-case, whitespace collapsed, then cut at the first voltage token  \s*\d+(\s*/\s*\d+)*\s*KV\b.*$
X           = (lon - (-8.2)) / (1.9 - (-8.2)),      Y = (lat - 49.8) / (59.0 - 49.8)     both clamped to [0, 1]
```

`ReadSubstations` keeps, per key, the first row whose `longitude` and `latitude` fall inside the box
(-8.2 to 1.9, 49.8 to 59.0); out-of-box, blank and unparseable rows are skipped. A bus
whose normalised name matches takes the projected position. Draping runs only when at least one bus matched and at
least one did not: for up to 60 passes, each unplaced bus takes the mean of its already-placed branch neighbours, and a
bus never reached keeps its coordinates. With no match no bus is placed, so none moves and none is jittered. Finally
every placed bus is offset by a deterministic jitter, `(hash(id, salt) mod 1000) / 1000 - 0.5` times 0.004 on
each axis, with `hash = id * 2654435761 + salt * 40503` in 32-bit unsigned arithmetic (salt 1 for X, 2 for Y), to
separate co-located buses. The verb passes the NGED `substations.csv` whenever it exists; `--no-geo` skips it. The
projection is equirectangular and serves layout only. The note `Geolocated m/n buses from substations.csv.` reports
the match rate.

### Case writer

`GdaModelWriter.WriteDirectory(model, dir, addGridInfeed)` (`src/GridSim.Gda/GdaModelWriter.cs`) writes the multi-file
JSON case that `JsonGridLoader.LoadDirectory` reads ([ch. 10](<0010 File and wire formats.md>)). Property names are
camelCase, output is indented, null properties are omitted. The `gda` verb writes exactly seven files:

| File | Content |
|---|---|
| `system.json` | `name`, `baseMva`, `frequencyHz` |
| `buses.json` | per bus `id`, `name`, `baseKv`, `type` (`slack`, `pv`, `pq`), `vm`, `vaDeg`, `gs`, `bs`, `x`, `y`; when set, `zip`, `vminPu`, `vmaxPu` (GR-332) |
| `generation.json` | grid infeeds first, then embedded units: `name`, `bus`, `type`, `capacityMva`, `outputMw`, `voltageSetpoint`, `qMinMvar`, `qMaxMvar`, `inertiaH`, `role` |
| `demand.json` | one entry per bus with non-zero `Pd` or `Qd`: `name`, `bus`, `pMw`, `qMvar` |
| `transmission.json` | the non-transformer branches: `name`, `from`, `to`, `rPu`, `xPu`, `bPu`, `ratingMva`, `circuits` (always 1); when not at the loader's default, `ratingBMva`, `ratingCMva`, `tapRatio`, `phaseShiftDeg`, `rPu0`, `xPu0` (GR-332) |
| `transformer.json` | the transformer branches: `name`, `from`, `to`, `rPu`, `xPu`, `tapRatio`, `phaseShiftDeg`, `ratingMva`; when given, `ratingBMva`, `ratingCMva`. A transformer's shunt `B` and zero-sequence data have no field here and are counted in a note |
| `onloadtapchanger.json` | `transformer`, `controlsBus`, `targetVpu`, `minTap`, `maxTap`, `stepPu`, `deadbandPu` |

An embedded unit on a `Pq` bus is written with `role = "pq"` (a fixed injection on reload); on any other bus `"pv"`.
With `addGridInfeed` true (every `gda` write) a synthetic **grid infeed** is inserted at each slack bus: type `grid`,
`role = "slack"`, `outputMw = 0`, voltage setpoint 1.0, `inertiaH = 0`, name `Grid infeed @ <bus name>`, and capacity
and symmetric reactive limits of

```
infeed_MW = max(GridInfeedFloorMw = 1000, demand of the island the slack sits in)
capacityMva = qMaxMvar = -qMinMvar = infeed_MW * GridInfeedHeadroomFactor (5)
```

The infeed is an oversized source that must never bind. `GdaModelWriter.IslandDemandBySlack` computes the island demand
as the sum of bus `Pd` over the connected component, using a union-find over every written branch (union by smaller
root id, summation in bus order), and fails loud with `InvalidOperationException` when a branch names a bus the model
lacks; `GridInfeedSizingMw` throws `ArgumentOutOfRangeException` for a non-finite demand. Several slacks in one island
each receive that island's whole demand. The `synth` verb calls the writer with `addGridInfeed: false`, and the model's
own generator at a slack bus then takes `role = "slack"`. The writer emits no `boundaries.json`, `diagnostics.json` or
`MODEL_CARD.md`; those belong to the Python assembler below.

## Algorithm

### The `gda` verb

`RunGda` (`src/GridSim.Cli/Program.cs`) runs, in order: root resolution, the register as-of, geolocation switch,
`--fleet`, `--schema`, source load, `--all-gsps`, build, report, `--out`, `--solve`.

- **Root.** `--root`, else `GDA_DATA_ROOT`, else `GDA_ROOT` (D06: `GdaRootEnvironment.Resolve` in GridSim.Gda). With
  none, the usage text prints and the exit code is 2. A root that does not exist prints `GDA root error: ...` and exits
  1; for a variable the message names the variable, never its value, and a set-but-missing `GDA_DATA_ROOT` is not
  replaced by `GDA_ROOT`.
- **Source.** `--dno nged|ukpn|npg` (default `nged`; anything else is exit 2). A load failure prints
  `Load failed: ...` and exits 1. A build exception prints `Build failed: ...` and exits 1.
- **`--gsp` check.** After the source load and before the build, a `--gsp` name must select something:
  `GdaModelBuilder.IsKnownGspGroup` compares its canonical key (`GspGroupKey`, the carve's own key) with the keys of
  every group name on the circuits and transformers (`GspGroups`). An unknown name prints
  `error: --gsp '<name>' is not a GSP group of <dataset>. Nearest: ...` (up to three names from `SuggestGspGroups`,
  Levenshtein distance between keys, ties by name) and exits 2.
- **Empty carve.** A build with 0 buses prints `error: the carve selected no live element (0 buses) ...` after the
  report and exits 3 before `--out` and `--solve`, so no empty model is written.
- **Report.** Header, build time, then the counts `Raw LTDS nodes`, `Electrical buses`, `Series circuits`,
  `Transformers (2w/3w)`, `Total branches`, `Demand points`, `Generators`, `System inertia`, `OLTCs`,
  `Connected islands`, `Buses by voltage` and every note.
- **`--out <dir>`** calls `GdaModelWriter.WriteDirectory`.
- **`--solve`** solves with `PowerFlowOptions { MaxIterations = 60, EnforceQLimits = false }` through
  `SteadyStateSolver` (OLTC loop default passes). `--dense` selects the dense backend and `--sparse` the sparse one;
  otherwise the shared backend rule applies: sparse on the CPU (`SolverSelector.DefaultSparseThreshold` is 0), and on
  a GPU backend dense up to 200 buses (`SolverSelector.DenseSparseCrossoverBuses`) and sparse above. A non-converged solve prints
  `DID NOT CONVERGE` and exits 3; a solver exception prints `Solve failed: ...` and exits 1.
- **`--all-gsps`** builds every GSP group named in the circuit table, with no region or status filter, in turn from the
  in-memory dataset (carrying `--minkv`, `--base`, `--no-oltc`, `--neg-gen` and `--all-gen`; `--licence` and
  geolocation do not apply), solves each with the shared dense/sparse rule, and prints a table. Spellings with the same
  `GspGroupKey` select the same elements, so each key is built once, under its first spelling, and the other spellings
  are listed after the table (`Iron Acton 132 kV` and `Iron Acton 132kV` are one area). A name whose build has 0 buses is set aside, listed after the table with a
  `spelling variant of` hint when another group name begins with it, and not counted. It exits 3 when any area that builds fails to build or converge, or when no area builds.
- **`--schema <path>`** prints the column names and row count of a CSV or Parquet dataset and exits.
- An unknown flag prints `error: unknown flag '<name>' (misspelled?).` followed by a pointer to the help, and exits 2; a
  malformed `--as-of` or `--dno` exits 2.

Exit codes: 0 success, 1 load, build, root or solve-exception failure, 2 usage, 3 non-convergence.

### The replay loop

`GdaReplayLoop.Replay(baseModel, series, kind, warmStart, utcNow, onSolved, storage, passivePlant)`
(`src/GridSim.Core/Simulation/GdaReplayLoop.cs`) reconstructs the grid state at every timestamp of a
`GdaTimeSeries` (`src/GridSim.Core/Gda/GdaTimeSeries.cs`). It cannot nowcast: `ForwardInferenceGuard.AssertHistorical`
is applied to every stamp at ingestion, again inside `Replay`, and by `GdaTimeSeries.Load`.

*Series.* A series is JSON `{ "name", "steps": [ { "timeUtc", "systemLoadFactor", "perBusFactor"? } ] }`, camelCase and
case-insensitive on read, with null properties omitted on write. `perBusFactor` maps bus id to factor. `Synthetic(name,
start, steps, stepMinutes)` generates the shape of `LoadProfile.DailyFactor`:

```
x = ((hour - 4 + 24) mod 24) / 24          factor(hour) = 0.60 + 0.40 * (0.5 - 0.5 * cos(2 * pi * x))
```

a daily shape with trough 0.60 at 04:00 and peak 1.00 at 16:00.

*Per step.* For step `i`:

1. `step.ApplyTo(base)`. Without per-bus factors this is `GridScenario.AtLoad`: every bus `Pd` and `Qd` scaled by the
   factor, every generator not on the first slack bus scaled by the factor, then `Dispatcher.Balance` re-balances the
   slack. With per-bus factors each listed bus is scaled by its own factor, then `Dispatcher.Balance`.
2. With storage, `StorageFleet.Advance` and `Apply` inject the hypothetical sites. The step length is the gap to the next
   timestamp (the last step reuses the previous gap; a one-step series uses 0.5 h), floored at `GdaMinStepHours`
   (1e-6 h).
3. With passive plant, `PassivePlantFleet.WithoutSupersededOltcs` hands governed transformers' tap regulation to the
   delayed passive controllers (it removes a solver OLTC record only when the transformer the record drives, matched
   by name and end bus as the solver matches it, is one the fleet binds, GR-137), and `ApplyTo` applies the taps and derated ratings left by the previous converged step.
4. With `warmStart` and a previous converged result, `GridScenario.WithSolvedVoltages` seeds the voltages. A
   non-converged result never seeds the next step.
5. `SteadyStateSolver.Solve`. The engine comes from `SolverSelector.Create(kind, base, 1500)`: a radial network
   (in-service branches = buses - 1 and exactly one slack) uses the sweep, a network of more than 1500 buses uses sparse
   Newton (`MaxIterations = 40`, `EnforceQLimits = false`), anything else dense Newton. The sweep and sparse Newton run
   no OLTC loop; dense Newton runs the solver's default OLTC passes.
6. With passive plant and a converged solve, the plant advances over the step's length against that solve's loading and
   controlled-bus voltage. A non-converged solve leaves the previous updates in force.
7. The step records `TimeUtc`, `SystemLoadFactor`, `Converged`, `Iterations`, `MaxMismatch`, `TotalLossMw`, and the
   minimum and maximum bus voltage, then `onSolved(i, step, solvedModel, result, trace)` fires.

*Result.* `GdaReplayResult` holds the series name, step count, non-converged count and the step list. Its `Provenance`
is `DerivedFromRecorded`, or `Counterfactual` when storage was supplied (see Provenance tags below).

*Passive plant.* The bound passive plant is the transformer on-load tap changers and the line and transformer thermal
classes that a case's `asset-bindings.json` binds ([ch. 13](<0013 Asset-dynamics data grounding.md>)). The tap
delay and thermal derate act step to step. A case with nothing bound takes exactly the path of the flag off.

*Storage.* `--bess chemistry:bus:MW:MWh[:soc]` (repeatable) adds a hypothetical battery site
([ch. 27](<0027 Battery energy storage.md>)). The chemistries are `lfp`, `nmc`, `lto`, `na-ion`, `vrfb` and
`lead-acid`; the initial state of charge defaults to 0.5; the pack is sized on a 1000 V DC bus. `--policy` accepts
`idle`, `peak-shave` (default; discharge above 0.9 and charge below 0.7 of the peak demand, the peak being the case's
total `Pd` times the series' largest load factor), `price` and `hold`; `lp` needs prices that `gda-replay` does not
take and exits 2.

*The verb.* `RunGdaReplay` requires `--series` or `--synth` (neither: usage, exit 2). `--case` (default `case118`) is
resolved by `LoadNamed`: `case9`/`ieee9`, `case14`, `case30`, `case57`, `case118`, `case300`, `case145`,
`case24`/`rts`, `cigre`, `uk`/`gb`/`gb-reduced`, `gb-full`, `gb-synthetic`/`synthetic`, or a file name under
`data/matpower`. The synthetic series takes `--start` (default `2024-01-01`), `--steps` (48) and `--step-min` (30.0).
When both `--series` and `--synth` are given, `--synth` is used. The output directory is `--out`, else
`results/gda-replay-<case>` resolved against the executable's directory. It contains:

| File | Content |
|---|---|
| `run-metadata.json` | the run header (timestamp, backend, git commit, build, runtime, SIMD tier, machine, run description) |
| `state-estimate.csv` | `#` header lines, then `index,time_utc,load_factor,converged,iterations,max_mismatch,total_loss_mw,vmin_pu,vmax_pu`, one row per step |
| `exports/state-NNNNNN.json` | a `gridsim-solve-export/1` document ([ch. 10](<0010 File and wire formats.md>)) for step 0 and for every step divisible by `--export-every N` (N > 0) |
| `storage-counterfactual.csv` | with `--bess`: `index,site,request_mw,delivered_mw,aux_mw,soc,temp_c,limited`, headed by a `COUNTERFACTUAL` comment |

Exit codes: 0 every step converged, 1 case or series load failure, 2 usage, a forward-inference rejection, or a bad
storage specification, 3 one or more non-converged steps. A rejection prints
`REJECTED (forward inference): <message>`; a series whose first offending stamp is newer than now minus 169 h is
refused before any solve.

### The bridge protocol

#### Transport and roles

The server is `GDA Applications/GridSim-Bridge/bridge_server.py`, a FastAPI application served by uvicorn. It binds
`BRIDGE_HOST` (default `127.0.0.1`) on `BRIDGE_PORT` (default 8770). One WebSocket, `/ws/bridge`, carries both the
state stream and request/response. Every frame is one JSON object in a text message. The server serialises with
`allow_nan = False` after replacing every non-finite number by null; the model block's `"Infinity"` and `"NaN"` string
tokens (generator limits) pass through unchanged. Three HTTP routes exist: `GET /` returns `{app, ws, cases,
horizonCutoffUtc}`; `GET /meta` returns `{cases, scalar_min_timestamp, scalar_max_timestamp, frequency_min_timestamp,
frequency_max_timestamp, horizon_cutoff_utc}`; `GET /favicon.ico` returns 204. Cases are `gb-full`, `gb-spine` and every
`NetworkModel/zones/zone-*` directory found at start-up.

The client is `GridBridgeClient` (`src/GridSim.Core/Net/GridBridgeClient.cs`); the wire schema is
`src/GridSim.Core/IO/BridgeProtocol.cs`. The desktop app connects to `BridgeEndpoint` in its settings (default
`wss://simstream.twinscrollgridbalancer.co.uk/ws/bridge`; a local server is `ws://127.0.0.1:8770/ws/bridge`); the web
app reads `GridSim:BridgeEndpoint`. The client sends through one semaphore-guarded send path, assembles each received
message from 64 KiB chunks until end-of-message, and decodes UTF-8. `BridgeProtocol.JsonOptions` is case-insensitive on
read, camelCase on write, skips comments and trailing commas, omits null properties and accepts the named literals
`Infinity`, `-Infinity` and `NaN`. Instants go out as ISO-8601 UTC (`GridBridgeClient.Iso`: an unspecified-kind
`DateTime` is relabelled UTC, never shifted by the machine's offset). On connect the server sends
`{type:"info", message:"GridSim-Bridge connected", cases:[...]}`.

#### Client to server

Every command is `{action, ...}`; the server lower-cases `action`.

| Action | Fields | Server behaviour |
|---|---|---|
| `start` | `case`, `start`, `fps`, `source`, `loop` | Stops any running stream, then streams `case` from `start`. `case` defaults to the connection's current case (initially `zone-nged`, else the first sorted case). `start` must be a string (otherwise `info` "Missing start timestamp") and is an ISO instant or, for stored lakes, `"first"` or `"earliest"`. `fps` is clamped to 1..144 (integer part); an omitted `fps` keeps the connection's current rate (4 on a new connection) and an `fps` that is not an integer is ignored without a reply. `source` is `live` (default when absent or unknown), `presolved`, `estimated`, `solves` or `raw`, case-insensitive. `loop` (default false) wraps a stored replay. |
| `stop` | none | Sets the stream's stop event and replies `info` "Stop requested", or "No active stream". A stopped stream still ends with `done`. |
| `set_speed` | `fps` | Clamps to 1..144 and replies `info` "Playback speed updated to N FPS"; an unparseable value replies "Invalid fps value". |
| `seek` | `start`, optional `case`, optional `source` | As `start` with the new instant (it does not read `fps` or `loop`); replies `info` "Seeking to <start>". A missing `start` replies "Missing seek timestamp". |
| `query` | `op`, `at`, `params`, `id` | Runs one on-demand query as an independent task, alongside any stream. Answered by one `result`. |

A body that is not valid JSON replies `info` "Invalid JSON command"; an unknown action replies `info` "Unknown action:
<name>". Neither closes the connection. The client API is `StartAsync` (an instant, default source `presolved`, or a
literal `"first"` with `loop`), `StopAsync`, `SetSpeedAsync`, `SeekAsync` and `Query(op, at, params)`.

#### Server to client

`GridBridgeClient.Dispatch` routes by the top-level `type`, read by `BridgeProtocol.PeekType`, a forward-only
`Utf8JsonReader` scan that is case-insensitive on the key and returns null for a truncated frame or a non-string value.

| `type` | Fields | Client handling |
|---|---|---|
| `topology` | `case`, `model` | Builds and caches the `GridModel`, resets the tick counter, raises `TopologyReceived`. |
| `state` | `t`, `result`, `scalars`, `overlays`, `operator`, `market`, `source`, `estimate`, `generationMix`, `weather`, `interconnectors` | Guarded, then raises the frame events. |
| `result` | `op`, `id`, `at`, `payload` | Completes the pending query with that `id`. |
| `error` | `message` | Faults the client: `Faulted` with a `BridgeStreamException`. |
| `info` | `message`, and `cases` on connect | A well-formed message of a type the client does not route: ignored. |
| `done` | `framesSent`, optional `source` | Ignored by the client; the server sends it when a stream ends, including after `stop`. |

A message of any other `type` is also a no-op, so a newer server stays compatible. A message that is not valid JSON, or
has no string `type`, makes `Dispatch` throw `InvalidDataException`. `DispatchGuarded` turns that, and every other
unreadable-message error listed under "Faults and rejections", into a `MessageRejected` event and an increment of
`RejectedMessages`; the stream continues. A `ForwardInferenceException` is the one content error that ends the receive
loop.

**`topology`.** `model` is exactly the `model` block of a `gridsim-solve-export/1` document, which
`gridsim_export.to_solve_export` (`GDA NetworkModel/gridsim_export.py`) produces. `BridgeProtocol.BuildModel` rebuilds
it, and it is the same reader `SolveImport` uses. Fields: `name`, `baseMva` (default 100; a non-positive value reads as
100), `frequencyHz` (optional; absent reads as 50 Hz, a present value must be finite and positive or the build throws),
`buses` (`id`, `type`, `baseKv`, `pd`, `qd`, `gs`, `bs`, `vm`, `name`, `x`, `y`, optional `zip` with `ap`, `bp`, `cp`,
`aq`, `bq`, `cq`), `generators` (`bus`, `pg`, `qg`, `vg`, `qmin`, `qmax`, `pmin`, `pmax`, `mBase`, `h`, `type`, `name`),
`branches` (`from`, `to`, `r`, `x`, `b`, `tap` where 0 reads as 1, `phaseShiftDeg`, `rateAMva`, `name`, `isTransformer`,
`inService` default true, `r0`, `x0`, `fromWinding`, `toWinding`, `fromNeutralXPu`, `toNeutralXPu`; an unrecognised
winding name throws), `oltcs` (`transformer`, `controlsBus`, `targetVpu`, `minTap`, `maxTap`, `stepPu`, `deadbandPu`)
and `boundaries` (`name`, `limitMw`, `branches` of `branch` and `direction`; a boundary with no branches is dropped).
Bus `x` and `y` are the 0..1 layout coordinates; without them a streamed model draws at one point. An unknown bus `type`
reads as `Pq`. The field-by-field reference is [ch. 10](<0010 File and wire formats.md>).

**`state`.** The `result` block is slim; the network is not resent.

```jsonc
{ "type": "state", "t": "<iso-utc>",
  "result":   { "converged", "iterations", "maxMismatch", "totalLossMw", "totalLossMvar",
                "buses":     [ { "id", "vm", "vaDeg", "pInjMw", "qInjMvar" } ],
                "lineFlows": [ { "from", "to", "pFromMw", "qFromMvar", "pToMw", "qToMvar",
                                 "pLossMw", "qLossMvar", "name", "ratingMva", "loadingPercent" } ] },
  "scalars":  { "freqHz", "rocofHzPerS", "genMw", "demandMw", "inertiaMwS", "conditionIndex", "rocofHeadroomHzS" },
  "overlays": { "stressByBranch": { "<branch name>": frac }, "conditionByBus": { "<bus id>": frac } },
  "operator": { ... }, "market": { ... },
  "source":   "presolved" | "estimated" | "solves",
  "estimate": { ... } }
```

`BridgeProtocol.StateMessage.ToSimFrame` assembles the `SimFrame`. An absent `converged` reads as true and absent
`iterations`, `maxMismatch` and loss totals as 0. Generation falls back to the sum of positive bus injections when
`genMw` is absent. An absent `freqHz` reads as the topology's nominal frequency, an absent `rocofHzPerS` as 0. An absent
`demandMw` reads as 0 (unknown), never the cached topology's load-time demand, which is frozen at topology time and
wrong for other ticks. A line flow with an empty `name` or a `ratingMva` of 0 is backfilled from the cached topology
*in order*: the k-th flow on a bus pair (either orientation) takes the k-th topology branch on that pair, the order the
solver emits flows in, so parallel circuits keep their own names and ratings. A flow that needs a backfill but has no
branch left on its pair means the state does not match the topology and throws `InvalidDataException`. The clock hour
of the frame is the time of day of `t`.

`source` names the reconstruction: `presolved` (the demand-scaled re-solve lake), `estimated` (the measurement-fitted
WLS lake) or `solves` (the GDA-SOLVES library). The `live` and `raw` paths send no `source`, which reads as null. The
`estimate` block rides `estimated` frames and `solves` frames:

| Wire key | Lake column | Meaning |
|---|---|---|
| `objectiveJ` | `objective_j` | WLS objective J at the fitted state |
| `chiThreshold` | `chi_threshold` | the estimator's Wilson-Hilferty approximation of the 95 % chi-square quantile for the fit's degrees of freedom |
| `chiPassed` | `chi_passed` | whether J is at or below the threshold; a bool, so an absent key reads as false |
| `chiTest` | `chi_test` | optional (N214): `passed`, `failed` or `not-run`; when present both shells show `chi-square not run` for a test that did not run (it used to read FAIL). Absent (an older bridge) reads exactly as before from `chiPassed`. An unknown value, or one that contradicts `chiPassed`, rejects the frame |
| `dof` | `dof` | degrees of freedom (integer) |
| `measurementsN` | `measurements_n` | measurements in the set (integer) |
| `removedN` | `removed_n` | bad-data removals (integer) |
| `iterations` | `iterations` | Gauss-Newton iterations (integer) |
| `reducedChi2` | `reduced_chi2` | `solves` frames only: the reduced chi-square of the stored solve; the client's `EstimateBlock` has no field for it and ignores it |

The server writes the four integer keys as JSON integers, because the client binds them to `int?` and refuses `632.0`.

`operator` and `market` are per-tick side channels joined from `Derived/operator_market_frames`, every field nullable:
`operator` carries `largestLossMw`, `responseMw`, `reserveMw`, `deratedMarginMw`, `netInterconnectorMw`,
`synchronousInertiaGvaS`, `outturnInertiaGvaS`, `marketInertiaGvaS`, `warning`, `balancingActions`, `constraints`,
`warnings`; `market` carries the imbalance, system sell, market index, day-ahead and BSUoS prices, net imbalance volume,
constraint cost, carbon intensity, demand forecast, the balancing-cost and balancing-mechanism totals and counts, a feed
of timed acceptances and per-interconnector flows. The `raw` source adds `generationMix` (`gas`, `coal`, `nuclear`,
`wind`, `windEmb`, `hydro`, `imports`, `biomass`, `other`, `solar`, `storage`), `weather` (`temperatureC`,
`windSpeed100mKph`, `solarRadiationWm2`) and `interconnectors` (`ifa`, `ifa2`, `britNed`, `moyle`, `eastWest`, `nemo`,
`nsl`, `elecLink`, `viking`, `greenlink`, `net`), raised through `RawDataReceived`; its `result` is degenerate (empty
`buses` and `lineFlows`). Fields are listed in [ch. 10](<0010 File and wire formats.md>).

The client raises, for each accepted `state`, `FrameReceived`, `StateReceived` (frame, scalars, overlays),
`RawDataReceived`, `MarketDataReceived` (frame, operator, market) and `EstimateReceived` (frame, source, estimate), in
that order, after the horizon guard has passed.

**`result`.** `{type:"result", op, id, at, payload}`. Every reply, including every rejection, echoes the request's
`op`, `id` and `at` verbatim. The client (`GridBridgeClient.HandleResult`) pairs a reply with its query by `id` only.
A reply with no `id`, or with an `id` that matches no pending query, completes nothing and is reported through
`MessageRejected`; a reply whose `id` names a query of a different `op` fails that query at once; a paired reply whose
payload cannot be read fails its query at once with the read error. (Before D-DOC-078 an `id`-less reply was paired
with the oldest pending query of the same `op`, which could hand one query another's answer.) `payload.solveExport`, when present, is a whole `gridsim-solve-export/1` document
captured raw and read by `SolveImport.Read` into `QueryResult.Solve`. `payload.verdict`, `bindingConstraint` and
`violations[]` (`kind`, `element`, `value`, `limit`, `message`) are projected into `QueryResult`. Other payload keys
(`scalars`, `summary`, `frequency`, `headroom`, `reportPaths`) are not projected by the client.

#### Query operations

The server runs each query in a worker thread as an independent task, so replies may complete out of order. `case` in
`params` selects the network (an unknown case falls back to `gb-full`); queries on a state solve at the settled demand
scale `QUERY_DEMAND_SCALE` = 0.70. `inertia` and `condition` with no `at` use the horizon cutoff.

| `op` | `params` | Payload |
|---|---|---|
| `inertia`, `condition` | none | the nearest record at or before `at` (backward only, tolerance 3600 s) from the reconstructed-inertia or condition-index table in `scalars`; `verdict` is `OK`, or `NO_DATA` |
| `scenario` | `trip` (or `selector`), required; a case-insensitive substring of the name is matched against non-slack generators, then lines, then transformers; a name that matches none is solved as a generator trip that removes nothing | the trip is solved on the base state; `verdict` is `SECURE`, `INSECURE` (the first violation is the `bindingConstraint`) or `NON_CONVERGENT`; `solveExport`; `scalars` (`action`, `removed`, `lost_mw`, `vm_min`, `vm_max`, `losses_mw`); optional `frequency` and `headroom` |
| `feasibility` | `kind`: `new_load` (`bus`, `mw`, `pf` default 0.95), `loss` (`selector` or `trip`, `largest_interconnector`, `largest_generator`) or `dispatch` (`sync_fraction` or `target_sync_mw`) | the engine's `verdict` and binding constraint, violations, `solveExport` for the network kinds |
| `post_event` | `event_id` (or `event`), else `at`; `window_min` (default 20) | `verdict` `OK`, `summary`, `scalars`, `reportPaths`; violations when peak RoCoF is at or above 0.5 Hz/s or the frequency deviation is at or above 0.5 Hz |
| `asset` | `busId` or `bmUnit`, optional `case` | `bmUnit`, `busId` and four row lists, `envelope`, `bessSoc`, `tapWear`, `consumers`, whose snake_case Parquet columns are projected to camelCase; a block the unit lacks is omitted. The client renders each row as key/value fields. |
| `propagation` | optional `fromBus`, `toBus` | `corridors[]` (`from`, `to`, `tau` seconds, `km`, `vKmS`, `class`), `count` and `provenance`; with both bus ids the server answers with one `corridor` and `provenance` instead, and when the table is unbuilt with an empty `corridors`, `count` 0 and a `note`. The client's `QueryPropagation` sends no parameters and reads only `corridors[]` and `provenance`. |

Violation limits used by the engines: voltage 0.94 to 1.06 pu, thermal loading 100 %, RoCoF 0.5 Hz/s
(`ROCOF_LIMIT_HZ_S`). The client validates `propagation` at the protocol boundary: a corridor `tau` that is negative,
non-finite or above `PropagationData.MaxPlausibleTauSeconds` (10 s) is dropped and counted in the provenance note, never
passed to the swing model. `GridBridgeClient.QueryPropagation` returns the surviving `(from, to) -> tau` map for
`DistributedConfig.ProvidedTauSeconds`.

Error replies carry `verdict = "ERROR"` and a `bindingConstraint` message: an unknown `op`
(`unknown op '<op>'. Available: [...]`), an unparseable `at`, a missing mandatory parameter (`scenario requires
params.trip`, `new_load requires params.bus and params.mw`, `post_event requires params.event_id or at`), an unknown
feasibility kind, or an engine failure (`<op> failed: <exception>`). A query-level error rides a `result`; it does not
fault the stream.

#### Sources

| `source` | What the server streams | Cadence |
|---|---|---|
| `live` | One converged sparse-NR plus OLTC solve of the case (`nr_check.compute`, 60 iterations, 10 OLTC passes, cached per case) re-emitted every tick; only `scalars`, `operator` and `market` vary | 30-minute settlement periods from the next half hour at or after `start` to the earlier of the horizon cutoff and the newest scalar record (`scalar_max_timestamp`); a `start` later than that record is moved to 7 days before it |
| `presolved` | Stored frames of `Derived/presolved_state/<case>` | one per stored settlement period |
| `estimated` | Stored frames of `Derived/estimated_state/<case>`, with the `estimate` block; scalars the row lacks are filled from the reconstructed-scalar tables | one per stored period |
| `solves` | The GDA-SOLVES library (`gb-spine` only), rebuilt per period from stored per-bus WLS states, one day at a time | one per period, paging 35-day windows |
| `raw` | The public-data replay: the 1 Hz frequency clock with generation mix, demand, interconnectors, weather, inertia and market joined backward; no solve | one per 1 Hz sample |

A `presolved` or `estimated` start with an instant falls back to the `live` path, with an `info` message naming the
builder to run, when the case has no `topology.json` or no frame lies in the window. `start = "first"` snaps to the
case's earliest stored partition; when the case has none, the reply is `info` "No <source> frames stored for '<case>'
yet; nothing to replay." and no stream starts, with no `done` and no fallback. A `presolved` or `estimated` replay is
capped at `MAX_REPLAY_DAYS` = 35 days per stream (`info` "Replay window capped ..."); `live` has no cap. With `loop`
the frames repeat until stopped, still horizon-masked. Frames are paced at `1 / fps` seconds. `solves` serves only `gb-spine`
(an `info` otherwise). Scalars attached to a tick come from a strictly backward lookup, never a record after the tick,
and `freqHz` in a settled snapshot is the constant 50.0.

#### Sessions

State is per connection: the current case, `fps`, `loop`, the active stream task and the in-flight query tasks. Starting
a stream or seeking stops the previous stream first. Query tasks are cancelled on disconnect. The client caches one
`GridModel` per connection, replaced by each `topology`; `state` frames build against it. Frames carry UTC instants and
the client does no local-time conversion. Queries are correlated by `id`, never by arrival order: the client assigns an
increasing integer string per request, awaits the matching `result` for at most `QueryTimeout` (60 s, then
`TimeoutException`), and cancels the awaiter on cancellation or disposal. A reply whose `id` matches no pending query
(an absent `id`, or one already removed after a timeout) is matched to a pending query of the same `op`; which one is
taken follows the enumeration order of the pending table and is not defined. Server-side lazy caches (case bundles, scalar tables, engines,
asset tables) are guarded by locks so two concurrent first requests do not each launch the same multi-second solve.

#### The 169-hour horizon, on both ends

The no-forward-inference horizon is `ForwardInferenceGuard.HorizonHours` = 169.0 hours (7 days plus 1 hour;
`src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs`). A stamp is rejected when it is strictly newer than
`now - 169 h`; the stamp exactly on the boundary passes.

*Server* (`GDA Scripts/horizon.py`, `HORIZON_DAYS = 7 + 1/24`, `cutoff()` = now minus that):

- `start` and `seek` with an instant after the cutoff reply `error` "start ... is past the no-forward-inference horizon
  (cutoff ..., now - 7d 1h). Rejected." for `live`, `presolved`, `estimated` and `solves`. For `presolved` and
  `estimated` the instant is the resolved one, so `first` is rejected when the earliest stored frame is newer than the
  cutoff; `solves` with `first` skips the check and skips every frame newer than the cutoff.
- No tick newer than the cutoff is sent: a stored or generated tick with `t` after the cutoff ends the stream (its `done`
  follows), even when a lake holds a newer row. The window end is the cutoff.
- The `live` window runs from the next half hour at or after `start` to the earlier of the cutoff and the newest scalar
  record, the newest `datetime_utc` of the three scalar tables (`scalar_max_timestamp`). When `start` is later than that
  record, the server moves the start to 7 days before the record, no earlier than the oldest scalar record, and sends
  `info` "Requested start is past available scalar data; clamped to <start> -> <end>". A window with no settlement
  period replies `error` "No settlement periods in range." and then `done` with `framesSent` 0.
- A `query` whose `at` is after the cutoff replies `payload.verdict = "INFEASIBLE_HORIZON"` and runs no engine.
- `raw` does not reject: its frequency bounds are clamped to the cutoff, and a `start` beyond the newest bounded sample
  is moved to two hours before it with an `info`. Every raw-lake context table is clamped to the cutoff on load.
- Scalar lookups are backward-only. The same constant is built into the GDA presolve, estimated-state and measurement
  builders, which refuse a period after the cutoff.

*Client*: `GridBridgeClient.HandleState` first refuses an undated frame (`t` missing parses to `DateTime.MinValue`,
which would pass any guard by construction), then calls `ForwardInferenceGuard.RejectFuture(t, DateTime.UtcNow)`. The
comparison is exact against the client's own clock with no skew allowance: a tick newer than now minus 169 h is refused,
and a tick at or older than that instant is accepted. A server whose clock runs ahead and sends a tick newer than the
client's cutoff has it refused. The check precedes every frame build and event, so a future tick never reaches a view.
A violation throws `ForwardInferenceException`, which `DispatchGuarded` re-throws: it ends the receive loop, fails every
pending query and raises `Faulted`. The socket stays open but nothing reads it, so a later `start` on the same
connection is never answered and recovery needs a new connection. This is the only content-level error that is
loop-fatal. A server `error` message also raises `Faulted`, but the receive loop keeps running.

The two enforcements are independent and use the same constant, so each is a complete defence if the other fails.

#### Faults and rejections

| Condition | Where | Effect |
|---|---|---|
| Unknown case, invalid `start`, start newer than the cutoff, an empty `live` window ("No settlement periods in range.", followed by `done`) | server | `error`; client `Faulted` (`BridgeStreamException` with the message); the stream is dead and the receive loop keeps running |
| `presolved` or `estimated` start with an instant: lake missing for the case or empty for the window | server | `info`; the `live` solve streams instead |
| `first` with no stored frames, `solves` for another case or without a library | server | `info`; no stream starts and no `done` follows |
| Unexpected exception in a stream | server | `error` "Stream error: ..." (stored: "Presolved replay error: ...", `solves`: "Solves replay error: ...", `raw`: "Raw stream error: ...") |
| `start`/`seek` missing its timestamp, invalid JSON, unknown action | server | `info`; connection stays open |
| Query error, horizon rejection, engine failure | server | `result` with `verdict` `ERROR` or `INFEASIBLE_HORIZON` |
| Remote close | client | pending queries fail with `BridgeStreamException` "The bridge closed the connection."; `Faulted` |
| Socket failure | client | pending queries fail with the exception; `Faulted` |
| Tick newer than now minus 169 h | client | `ForwardInferenceException`; loop-fatal as above |
| Unreadable single message: invalid or truncated JSON, no string `type`, a bad or missing `t` or unparseable timestamp, a `state` before any `topology`, a flow that cannot be backfilled, an invalid winding name, an invalid `frequencyHz` | client | `MessageRejected` raised with the first 120 characters of the message, `RejectedMessages` incremented, the stream continues |

`DispatchGuarded` treats `JsonException`, `InvalidDataException`, `FormatException`, `InvalidOperationException`,
`ArgumentException`, `KeyNotFoundException` and `OverflowException` as a bad message; any other exception propagates as
a genuine bug.

### Case assembly on the GDA side

The GDA repository assembles the GB case directories the bridge serves and the estimator fits (each a case folder
`JsonGridLoader.LoadDirectory` reads).

**Normalised tables to `gb-full`.** Ingestion normalises each licence's LTDS into six parquet tables per licence,
`nodes`, `circuits`, `tx2`, `tx3`, `demand` and `gen`, under `GDA NetworkModel/normalized/{licence}_{table}.parquet`
(`assemble/run.py:TABLE_NAMES`). `python -m assemble.run gb-full` then loads the England and Wales licences (EMID,
WMID, SWEST, SWALES, EPN, LPN, SPN, NPGN, NPGY, SPM, SEPD) and the ENW synthetic subnet (repairing NaN primary demand
and implausible synthetic line charging at load), runs `assemble.core.assemble` with an append-only **bus-id registry**
(`ids.BusIdRegistry`: the same physical node always receives the same id across rebuilds), `--min-kv` 132 by default and
a national slack, geolocates (`assemble.geo`), integrates the transmission **spine** (`assemble.spine.integrate`; the
Scottish DNO meshes attach as equivalent BSP infeeds at spine GSPs and are not assembled directly), writes the case
(`assemble.writer.write_case`: `system.json`, `buses.json`, `generation.json`, `demand.json`, `transmission.json`,
`transformer.json`, `onloadtapchanger.json`, `boundaries.json`, `diagnostics.json`, `MODEL_CARD.md`) plus the AssetGraph
parquets, and **bakes a warm start** (`assemble.warmstart.bake`: the converged `vm`, `vaDeg` and settled OLTC taps,
skippable with `--no-warm-start`). The result, `GDA NetworkModel/out/gb-full`, has 3539 buses and 5918 branches (3298
lines and 2620 transformers), the same case as `data/gb-full` in this repository.

**The spine.** `GDA NetworkModel/spine/build_spine.py` builds one spine bus per deduplicated NESO GSP site with a
synthetic-topology, real-nodes branch set: a Delaunay triangulation filtered to the Gabriel graph, pruned above
`PRUNE_KM` = 120 km and re-unioned with the Euclidean minimum spanning tree so the graph stays one island. A curated
overlay (`spine/corridors.yaml`, provenance `curated-corridor`) fixes the voltage and circuit counts of known corridors,
and `electrify` converts per-kilometre typical impedances to `r/x/b/rating`. Boundary sets (B4, B6, B7, B8, SEIMP) are
classified by documented geographic and PES rules, their limits set to the median of the NESO 24-months-ahead columns,
and circuit counts raised until each boundary's capacity meets its limit. Outputs: `spine_out/spine_nodes.parquet`,
`spine_out/spine_branches.parquet` and a GridSim-shaped `boundaries.json`. With `SPINE_ETYS=1`, `apply_etys_circuits`
replaces synthetic edges with the real ETYS Appendix B circuits (`resolved/etys_circuits.parquet`): the parallel
combination `z_eq = 1 / sum(1 / z)` with susceptances and winter ratings summed, tagged `etys-appendix-b`. The overlay
is off by default because ETYS describes today's network and its precision exposes reinforcements that did not exist
on historical days.

**`gb-spine`.** `GDA NetworkModel/emit_spine_case.py` folds `gb-full` onto its spine buses: 316 buses, one per anchor
GSP, matching the per-GSP measurement layer one to one. A simultaneous breadth-first search from all spine buses over
the `gb-full` branch and transformer graph (seeds in ascending spine-bus id) gives every other bus an owner. Each folded
bus's demand and generation sum onto its owner; sub-GSP impedances are dropped, so the reduced case supplies topology
and demand proportions and the measurements supply the physics. Units of type `grid` (the national slack and the
var-only GSP voltage-control units) are not folded; interconnectors are kept. The slack is Melksham (`MELK_1`). Bus ids
are the `gb-full` registry ids (`NGET:<gsp>` keys), so the measurement join is identical across both cases.
`fold_map.json` (`{owner: {gb-full bus -> spine bus}, pes: {spine bus -> PES group}}`) is written to both case
directories, and `boundaries.json` is copied verbatim from `gb-full`.

### The lake products

All four are weekly-partitioned Parquet under `$GDA_ROOT/Derived/<product>/<case>/year=YYYY/week=WW/`, with
`year` and `week` both from the ISO calendar. Each builder writes to a temporary file and `os.replace`s it, merges new
rows into an existing partition (deduplicated on the key, last wins), and re-checks the horizon per period. Field
tables are in [ch. 10](<0010 File and wire formats.md>).

| Product | Grain | Builder | Reads | Notes |
|---|---|---|---|---|
| `presolved_state` | one row per 30-minute instant (48 a day) | `GDA NetworkModel/presolve_lake.py` | the case, the scalar tables | A deterministic demand-scaled re-solve. `demand_scale = clamp(0.70 * demandMw(t) / demand_ref, 0.30, 1.00)`, where `demand_ref` is the median reconstructed system demand; 0.70 when the demand is unknown. Each period is solved by `nr_check.compute` (60 iterations, 10 OLTC passes). Row: `datetime_utc`, `case`, `converged`, `iterations`, `max_mismatch_pu`, `vm_min`, `vm_max`, the seven scalars, `demand_scale`, `result_json`. A once-per-case `topology.json` holds the model block. |
| `estimated_state` | one row per settlement period (46, 48 or 50 a day) | `GDA NetworkModel/build_estimated_state.py` | B1610 and interconnector measurements, the case | The WLS fit of that period's telemetry. Periods that run and do not converge are stored as `converged = False` with no result block; a period whose `gridsim estimate` call writes no export aborts the day and stores nothing. Columns: `datetime_utc`, `case`, `status`, `converged`, `iterations`, `max_state_update`, `objective_j`, `chi_threshold`, `chi_passed`, `dof`, `measurements_n`, `removed_n`, `removed_json` (`[{kind, bus, rn}]`), `vm_min`, `vm_max`, `boundary_json` (`[{name, flowMw, limitMw}]`: the signed sum of each cut branch's from-end MW), `result_json`. `topology.json` carries the case's `boundaries.json`, merged in because the solve export does not carry boundaries. |
| `state_divergence` | one row per period that both lakes cover with a converged estimate | `GDA NetworkModel/build_state_divergence.py` | the two lakes above | `buses_compared`, `flows_compared`, `dvm_max_pu`, `dvm_mean_pu`, `dva_max_deg`, `dflow_max_mw`, `worst_vm_bus`, `worst_va_bus`, `worst_flow_from`, `worst_flow_to`, and the estimate's `est_objective_j`, `est_chi_passed`, `est_removed_n`. Where the solve and estimate routes disagree, the divergence localises model or dispatch error. |
| `estimation_closure` | one row per day | `GDA NetworkModel/report_estimation_closure.py` | `estimated_state`, `demand_all_srcs` | `periods`, `converged_n`, `chi_passed_n`, `removed_total`, `est_gen_gwh`, `est_load_gwh` (each converged period's positive and negative bus injections, times 0.5 h), `nd_gwh` (the NESO national demand series), `load_vs_nd_pct`, and `outlier_league`, the five measurements removed most often as `kind@busN xCOUNT`. |

The estimated-state builder composes the measurement set (`build_estimator_measurements.build`) and runs

```
gridsim estimate <case-dir> --measurements m.json --prior solve --warm-start --export-solve --max-iter 100 --lean
        --backfill-removals --out <dir>
```

per period. It locates the CLI through the `GRIDSIM_CLI` environment variable. `estimate` writes
`estimated-solve-export.json` only for a converged fit and exits 3 for a fit that does not converge or a network that is
unobservable ([ch. 6](<0006 State estimation.md>)).

**The measurement products.** `build_gsp_measurements.py` reads one settlement day's B1610 file once and emits three
products by merge-only aggregation (no interpolation, no gap filling), allocated through the BMU crosswalk. Unallocated
energy is reported, never smeared.

| Product | Content and sigma |
|---|---|
| `gsp_generation_measurements` | one row per period and GSP: `generation_mw = 2 * MWh`, `sigma_mw`, `units_n`, `min_confidence`, `provenance`. Physical production units only; interconnectors excluded. A per-period `gsp = NULL` row holds the energy the crosswalk could not bind at the confidence floor. Sigma `= max(2 MW, 2 % * abs(MW)) / min_confidence`; the NULL row `30 % * abs(MW)`. |
| `gsp_group_demand` | 14 PES-group net demand series: `demand_mw = -2 * sum(quantity)` over supplier legs, net of embedded export behind the meter; sigma `max(10 MW, 3 % * abs(MW))`. |
| `gsp_ic_flows` | interconnector flow at each landing GSP: `ic_mw = 2 * sum(MWh)`, import positive. Applied in the estimator as `max(5 MW, 2 % * abs(MW))`. |

A day with no B1610 file, or fewer than 40 periods, is built from Physical Notification levels (time-weighted period
means over the `levelFrom`/`levelTo` ramps) with `provenance = "pn"`, sigma `max(10 MW, 10 % * abs(MW)) / min_confidence`, and no group-demand
product. The crosswalk (`build_bmu_crosswalk.py`, `resolved/bmu_crosswalk.parquet`) binds each BM unit once by its best
method; allocation requires confidence at or above `--min-confidence` (default 0.6).

| Confidence | Method | Rule |
|---:|---|---|
| 0.9 | `name-match` | `bmUnitName` against `Derived/demand_consumers.name`, giving the register asset's GSP and bus id |
| 0.75 | `tec-register` (strong) | for `T_*` units, a curated station-code entry or a match of at least two project-name tokens |
| 0.7 | `osuked-nearest` | OSUKED Power Station Dictionary coordinate to the nearest NESO anchor GSP, haversine, capped at 60 km |
| 0.75 | `tec-register` (weak) | `T_*` units OSUKED does not know: token match of lead party and unit name, with ordinal disambiguation, after the coordinate rung |
| 0.6 | `interconnector`, `interconnector-landing` | `I_*` units by curated id, register tokens, then the curated landing-substation table |
| 0.5 | `gsp-group-only` | the Elexon `gspGroupId`, one of 14 regions |
| 0.0 | `unmapped` | nothing bound the unit |

## Provenance tags applied

`ProvenanceTag` is defined in [ch. 8](<0008 Provenance and invariants.md>). The GDA seams apply it in exactly two
places. `GdaReplayResult.Provenance` is `DerivedFromRecorded` for a plain replay, and `Counterfactual` when hypothetical
storage ran: a "what if a battery had been there" run on real history is a conclusion and never an input to another
replay. The `--bess` run also writes a `COUNTERFACTUAL` comment into `state-estimate.csv` and
`storage-counterfactual.csv` and prints a `COUNTERFACTUAL` summary line. A model assembled by `gridsim gda` carries no tag
in its case files. On the GDA side the measurement products record their own
`provenance` column (`observed` for B1610, `pn` for Physical Notification) and the propagation payload carries a
`provenance` note that distances and inertia are real while wave speed and tau are modelled. The engine defaults in
`GdaBuildDefaults` are engineering defaults for missing data; a published figure always replaces them.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| System base | 100 | MVA | per-unit base of the built model; `--base` | `GdaBuildOptions.BaseMva` |
| Reference voltage | 0 (auto) | kV | NGED referred-impedance voltage; 0 reads it from the GSP-group name | `GdaBuildOptions.RefKv`, `GdaModelBuilder.ParseGspKv` |
| Default GSP voltage | 132 | kV | when a group name carries no system voltage | `GdaBuildDefaults.DefaultGspKv` |
| Voltage cut | 0 (none) | kV | keep elements at or above; `--minkv` | `GdaBuildOptions.MinKv` |
| GSP / licence carve | none | - | `--gsp`, `--licence` | `GdaBuildOptions.GspGroup`, `.Licence` |
| Susceptance unit | 1e-6 | S per LTDS unit | NGED B1 is microsiemens | `GdaBuildOptions.SusceptanceToSiemens` |
| Energised only | true | - | drop dead-status elements | `GdaBuildOptions.EnergisedOnly` |
| OLTCs | true | - | one per unequal-voltage two-winding transformer; `--no-oltc` clears | `GdaBuildOptions.CreateOltcs` |
| Embedded generation as load | false | - | `--neg-gen` | `GdaBuildOptions.EmbeddedGenAsNegativeLoad` |
| Connected generation only | true | - | `--all-gen` clears | `GdaBuildOptions.ConnectedGenerationOnly` |
| Substation table | none; the verb sets it to `NationalGrid/RAWData/Network Capacity/substations.csv` when the file exists | - | `--no-geo` leaves it null; `Geolocate` (default true) has no effect without a path | `GdaBuildOptions.SubstationsPath`, `GdaBuildOptions.Geolocate` |
| Tie threshold | 1e-9 | ohm | `abs(R1) + abs(X1)` at or below is a tie | `GdaBuildDefaults.TieImpedanceThresholdOhm` |
| Demand power factor | 0.98 | - | when the published figure is outside (0, 1] | `GdaBuildDefaults.DemandPowerFactor` |
| Embedded Q capability | 0.4 | x capacity | `Qmin = -0.4 cap`, `Qmax = +0.4 cap` | `GdaBuildDefaults.EmbeddedGenQRatio` |
| OLTC target | 1.0 | pu | regulated voltage | literal in `GdaModelBuilder.Build` |
| OLTC step | 0.0125 | pu | tap step | `GdaBuildDefaults.OltcStepPu` |
| OLTC deadband | 0.01 | pu | | `GdaBuildDefaults.OltcDeadbandPu` |
| Largest plausible tap | 50 | percent | beyond it a range is invalid | `GdaModelBuilder.MaxPlausibleTapPercent` |
| Degenerate transformer X | 1e-6 / 1e-4 | pu | threshold / replacement | `DegenerateTransformerXPu`, `MinTransformerXPu` |
| Star-leg floor | 0.01 and 1e-6 | x abs(X_HL), then ohm | non-positive star leg | `StarLegFloorFraction`, `StarLegFloorAbs` |
| Transformer rating fallback | 100 | MVA | percent-on-rating with no rating | `TransformerRatingFallbackMva` |
| Model frequency | 50 | Hz | | `SystemFrequencyHz` |
| Grid infeed floor / headroom | 1000 / 5 | MW / x | infeed sizing | `GridInfeedFloorMw`, `GridInfeedHeadroomFactor` |
| Examples named per note | 5 | rows | | `GdaModelBuilder.NoteExampleLimit` |
| Geo box | lon -8.2 to 1.9, lat 49.8 to 59.0 | degrees | maps to the unit square | `GeoLocator.LonMin` ... `LatMax` |
| Geo drape passes / jitter | 60 / 0.004 | passes / unit | | `GeoLocator.Apply` |
| Register as-of | now - 169 h | - | `--as-of` overrides | `ForwardInferenceGuard.Horizon` |
| Horizon | 169.0 | hours | 7 days plus 1 hour | `ForwardInferenceGuard.HorizonHours` |
| `gda --solve` | 60 iterations, no Q limits | - | dense with `--dense`, sparse with `--sparse`; otherwise sparse on the CPU, and on a GPU backend dense up to 200 buses | `SolverSelector.DefaultSparseThreshold`, `DenseSparseCrossoverBuses` |
| Replay sparse threshold | 1500 | buses | sparse above it |  `LiveSolveSettings.SparseThresholdBuses` |
| Replay sparse iterations | 40 | - | | `LiveSolveSettings.SparseMaxIterations` |
| Replay OLTC passes (dense) | 25 | - | | `SteadyStateSolver.DefaultMaxOltcPasses` |
| Single-step / minimum step | 0.5 / 1e-6 | h | storage and plant step | `LiveSolveSettings.GdaSingleStepHours`, `GdaMinStepHours` |
| Synthetic series | start 2024-01-01, 48 steps, 30 min | - | `--start`, `--steps`, `--step-min` | `GdaTimeSeries.Synthetic` |
| Default replay case | `case118` | - | `--case` | `RunGdaReplay` |
| Storage defaults | initial SoC 0.5; peak-shave 0.9 / 0.7 of peak; 1000 V pack | - | | `StorageCli.DefaultInitialSoc`, `PeakShaveHighFraction`, `PeakShaveLowFraction`, `DefaultPackDcVoltage` |
| Query timeout | 60 | s | | `GridBridgeClient.QueryTimeout` |
| Largest plausible tau | 10 | s | | `PropagationData.MaxPlausibleTauSeconds` |
| Idle interconnector cut | 0.001 | MW | display only | `BridgeProtocol.MinDisplayFlowMw` |
| Bridge bind | 127.0.0.1:8770 | - | | `BRIDGE_HOST`, `BRIDGE_PORT` |
| Playback rate | 1 to 144 | fps | server clamp | `bridge_server.py` |
| Replay window cap | 35 | days | per `presolved` or `estimated` stream | `MAX_REPLAY_DAYS` |
| Query demand scale | 0.70 | - | | `QUERY_DEMAND_SCALE` |
| Presolve demand scale | 0.70, clamp 0.30 to 1.00 | - | | `presolve_lake.py:BASE_SCALE`, `SCALE_LO`, `SCALE_HI` |
| Scalar lookup tolerance | 3600 | s | backward only | `bridge_server.py:_nearest` |
| Crosswalk floor | 0.6 | confidence | `--min-confidence` | `build_gsp_measurements.py` |
| B1610 minimum periods | 40 | periods | below it the PN fallback is used | `B1610_MIN_PERIODS` |

## Invariants

| Invariant | Pinned by |
|---|---|
| A zero-impedance busbar tie collapses to one bus and is counted once | `GdaBuildTests.Collapses_busbar_tie_into_one_bus` |
| An ohmic impedance is referred to the GSP voltage: `X_pu = X_ohm / (132^2 / 100)` | `GdaBuildTests.Refers_transformer_impedance_to_gsp_voltage` |
| A percent circuit is pu on 100 MVA; a percent transformer is on its rating | `PercentConventionTests.Circuit_percent_is_pu_on_100mva`, `Transformer_percent_is_on_rating`, `Percent_model_solves_in_band` |
| Demand folds onto the bus and generation attaches with its fuel type | `GdaBuildTests.Folds_demand_and_attaches_generation` |
| A single-GSP build has one slack, at the highest-voltage bus | `GdaBuildTests.Picks_highest_voltage_bus_as_single_slack` |
| A three-winding star node is never a slack | `AdversarialReviewGdaTests.Three_winding_star_node_is_not_a_slack` |
| The built network is one island and solves in band | `GdaBuildTests.Builds_a_single_connected_island_that_solves_in_band` |
| The materialised case reloads and solves | `GdaBuildTests.Materialised_model_round_trips_through_the_json_loader` |
| Every transformer is built high to low voltage and every OLTC controls the To end, whatever the listing order | `GdaBuildDataContractTests.R014_lv_first_transformer_is_oriented_hv_to_lv_and_the_oltc_controls_the_to_end`, `R014_every_emitted_oltc_controls_the_to_end_of_its_transformer`, `R014_listing_order_of_the_windings_does_not_change_the_regulated_answer` |
| OLTC limits come from the published range; -10/+10 percent is bit-identical to 0.9/1.1; 0/0 and invalid ranges build no OLTC and say so | `R097_published_tap_range_sets_the_oltc_limits`, `R097_symmetric_ten_percent_range_is_bit_identical_to_the_old_hard_coded_limits`, `R097_zero_tap_range_creates_no_oltc_and_says_so`, `R097_invalid_tap_range_creates_no_oltc_and_warns_loudly` (class `GdaBuildDataContractTests`) |
| A zero-impedance cable is merged and reported; a busbar or switch is a silent tie | `R097_zero_impedance_cable_is_merged_but_reported`, `R097_zero_impedance_busbar_or_switch_is_a_tie_without_warning` |
| Only connected generation is built; an unknown status is excluded with a warning; the status vocabulary is classified exactly | `R015_accepted_and_offered_generation_is_excluded_by_default`, `R015_blank_or_unrecognised_status_is_left_out_with_a_warning`, `R015_status_vocabulary_is_classified_connected_or_not`, `R015_opting_out_restores_the_old_every_row_behaviour` |
| Below-cut demand folds onto the nearest boundary bus, deterministically (lower bus id on a tie), and is reported when unreachable or off-network | `R047_minkv_folds_downstream_demand_onto_the_boundary_bus`, `R047_equidistant_boundary_buses_resolve_deterministically_to_the_lower_bus_id`, `R047_below_cut_load_reachable_only_through_a_removed_circuit_is_reported_not_folded`, `R047_demand_on_a_node_with_no_element_is_reported_in_a_full_build` |
| Generation joins on (GSP group, primary), never the name alone | `R048_same_named_primaries_in_two_gsps_each_receive_their_own_generation`, `R048_unique_primary_name_still_joins_when_the_gsp_spelling_does_not_match`, `R048_ambiguous_primary_name_is_left_unplaced_with_a_warning`, `R048_licence_carve_does_not_import_another_licences_same_named_plant` |
| A `-` primary and a `Removed` demand row never join | `AdversarialReviewGdaTests.Dash_placeholder_generation_is_unmatched_not_collapsed_onto_one_bus`, `Removed_demand_status_is_excluded` |
| The GSP carve matches the generation table's differently spelled group | `AdversarialReviewGdaTests.Gsp_carve_matches_generation_tables_different_spelling` |
| The newest demand vintage is read; no demand column, or no demand, fails loud; ambiguous files fail loud | `GdaDeterministicInputTests.Ukpn_reads_the_newest_demand_vintage`, `Ukpn_with_no_demand_column_fails_loud`, `Ukpn_with_a_demand_column_but_no_demand_fails_loud`, `Ukpn_with_two_equally_good_file_matches_fails_loud`, `Two_undated_csvs_in_a_dataset_folder_are_ambiguous_and_fail_loud`, `The_csv_with_the_strictly_newest_date_in_its_name_is_chosen` |
| A Parquet column missing from a middle part does not shift later rows | `GdaDeterministicInputTests.A_column_missing_from_a_middle_part_does_not_shift_later_rows` |
| The connected fleet excludes unbuilt embedded projects | `NationalFleetEmbeddedTests.Connected_fleet_excludes_unbuilt_embedded_projects_and_counts_connected_mw`, `Embedded_entry_connection_and_capacity_rules` |
| Each grid infeed is sized on its own island's demand; a single island is unchanged | `FrequencyRiskPresenterTests.R200_each_infeed_is_sized_on_its_own_island_demand`, `R200_a_single_island_model_is_bit_identical_to_the_old_whole_network_sizing`, `R200_fails_loud_on_a_branch_to_a_missing_bus_and_on_non_finite_demand` |
| Geolocation projects the GB box to the unit square, north up; an unmatched bus keeps its coordinates | `GeoLocatorTests.Project_maps_corners_of_gb_box_to_unit_square_corners`, `Northern_substation_gets_higher_Y_than_southern`, `Unmatched_bus_keeps_existing_coordinates` |
| Replay reconstructs every timestamp, heavier demand gives more loss, the callback fires once per step | `GdaReplayLoopTests.Reconstructs_every_timestamp_of_a_synthetic_day`, `Heavier_demand_reconstructs_higher_loss`, `OnSolved_fires_once_per_step` |
| A future-dated series is rejected at ingestion and on load; the exact boundary loads | `GdaReplayLoopTests.A_future_dated_series_is_rejected_at_ingestion`, `Load_itself_rejects_a_series_dated_just_inside_the_horizon`, `ForwardInferenceGuardTests.The_horizon_boundary_is_exactly_HorizonHours` |
| Storage in a replay is counterfactual; passive plant with nothing bound is bit-identical to off | `StorageLoopTests.Replay_with_hypothetical_storage_is_counterfactual`, `PassivePlantWiringTests.Replay_with_passive_plant_on_but_nothing_bound_is_bit_identical_to_off`, `Replay_with_bound_passive_plant_is_deterministic` |
| The bridge state binds topology plus state by id, backfills parallel circuits in order, and fails loud when it cannot | `BridgeProtocolTests.State_maps_bus_voltages_and_injections_by_id`, `SolveExportRoundTripTests.Parallel_circuits_each_get_their_own_name_and_rating`, `More_unnamed_flows_than_circuits_on_a_pair_fails_loud` |
| The topology carries branch service state, ZIP, OLTCs, boundaries and frequency, and an older document reads with model defaults | `SolveExportRoundTripTests.Every_carried_network_field_round_trips_exactly`, `The_bridge_topology_reads_an_opened_branch`, `An_older_document_without_the_new_fields_reads_with_model_defaults`, `BridgeProtocolTests.Topology_boundaries_ride_the_model_block`, `Q13WsThirteenDecisionTests.A_model_block_without_frequency_reads_as_50_Hz_exactly_as_before`, `An_invalid_model_block_frequency_fails_loud` |
| The estimate block, operator and market blocks and propagation payload bind and validate | `BridgeProtocolTests.Estimate_block_rides_the_state_message`, `A_state_carries_the_operator_and_market_blocks_streamed_from_the_lake`, `Propagation_result_projects_to_a_provided_tau_map` |
| A query result runs its `solveExport` through `SolveImport` | `BridgeProtocolTests.Query_result_runs_the_payload_solveExport_through_SolveImport` |
| A tick newer than now minus 169 h is refused before a frame is built and ends the receive loop, exactly at 169 h, with no skew allowance; a tick just older is accepted | `BridgeProtocolTests.A_state_with_a_future_timestamp_is_rejected_before_a_frame_is_built`, `Real_zone_state_stamped_now_is_rejected_by_the_forward_inference_guard`, `BridgeClientRobustnessTests.A_forward_inference_violation_is_still_loop_fatal`, `A_frame_inside_the_horizon_by_ten_minutes_is_refused`, `A_frame_just_outside_the_horizon_is_accepted` |
| One unreadable message is rejected and counted and the stream continues; an untyped message is an error; an unknown type is a no-op | `BridgeClientRobustnessTests.A_malformed_message_is_rejected_and_counted_and_the_stream_carries_on`, `A_state_with_an_unreadable_timestamp_is_rejected_not_loop_fatal`, `A_state_before_any_topology_is_rejected_through_the_guard`, `A_message_with_no_type_is_an_error_not_a_silent_skip`, `A_well_formed_message_of_an_unknown_type_is_still_a_forward_compatible_no_op` |
| A request instant is sent as the same UTC instant, never shifted | `BridgeClientRobustnessTests.Iso_sends_an_unspecified_instant_as_that_same_utc_instant`, `Normalise_relabels_unspecified_as_utc_without_shifting_it` |

## Worked example

All timings in the captures differ by machine and run; the counts and numbers hold on the reference lake. The lake root
is written `$GDA_ROOT`: `gda` reads `--root`, else the `GDA_ROOT` environment variable. The capture directory is
`docs/_captures/gda/`.

### A small NGED carve: Hindlow and Eyam, hand-recomputed

```
$ gridsim gda --root $GDA_ROOT --gsp "Stalybridge 132kV" --no-geo --out results/gda-stalybridge --solve
GridSim  -  GDA ingestion from $GDA_ROOT  (NGED)
==========================================================================
Built in 200 ms  -  GB/NGED ex-GDA [Stalybridge 132kV]

  Raw LTDS nodes          22,590
  Electrical buses             9   (busbar/disconnector ties merged: 18)
  Series circuits              5   (cables & lines)
  Transformers (2w/3w)         4 / 0   (+0 star nodes)
  Total branches               9
  Demand points                5   matched 5, total 0.02 GW
  Generators                   0   (0.00 GW capacity, 0 unmatched)
  System inertia             0.0 GVAs
  OLTCs                        4
  Connected islands            1   (largest 9 buses)

  Buses by voltage:
       33 kV         6
       11 kV         3

   No embedded generators matched; GSP infeeds carry all supply.
   1 GSP infeed(s) modelled as slack buses; primary = bus 0 (BUXT3_REDUCED_3, 33 kV).
   Demand: 2113 row(s), 23210.9 MW are on nodes outside this build's network (other areas of the carve, or nodes on no in-scope element) and were not included.
   GSP group = Stalybridge 132kV.

Materialised replica -> results\gda-stalybridge (git-ignored). Run it: gridsim --dir "results/gda-stalybridge"

Solving with the dense backend...
Converged in 28 ms (3 NR, 1 OLTC, mismatch 1.51E-011)
Generation 0.02 GW  Demand 0.02 GW  Losses 0.1 MW  V 0.991-1.000 p.u.
[exit 0]
```

(Capture: `gda-stalybridge.txt`.) Recomputing the build from the raw LTDS rows, independently of the builder:

- **Counts.** The group has 28 circuit rows; 5 are `Removed` and are dropped (step 1), leaving 23. Of those, 18 have
  `R1` and `X1` of `-` (read as 0), so they are ties; 5 are series. The 23 live circuits and 4 live transformers
  (5 rows, 1 `Removed`) touch 27 distinct node codes. The 18 ties merge 27 nodes into 9 groups, so 9 buses, matching
  `Electrical buses 9` and `ties merged: 18`. Branches are 5 series plus 4 transformers, 9, with no star nodes.
- **A circuit.** `BUXT3_#9L5 -> HINL3_TA`, 33 kV, `R1` 6.4093, `X1` 21.0502, `B1` 0.3141, all percent on 100 MVA. With
  `BaseMva` 100, `s = 1`:

```
R_pu = 6.4093 / 100 * 1           = 0.064093
X_pu = 21.0502 / 100 * 1          = 0.210502
B_pu = 0.3141 / 100 / 1           = 0.003141
```

  `ratingMva` is 484 from the `Winter` column.
- **A transformer.** `HINL3_TA (33 kV) -> HINL5_#1T0 (11 kV)`, `R1` 4.119, `X1` 65.579, 15 MVA: `R_pu = 4.119 / 100 =
  0.04119`, `X_pu = 65.579 / 100 = 0.65579`, tap 1.0, rating 15, high voltage first (bus 3 to bus 8). The rating plays
  no part in the conversion, because the percentages are on 100 MVA, not on the unit's rating.

  The captured run above and its `results/gda-stalybridge/transmission.json` are from an earlier build that read the
  NGED tables as ohms referred to the 132 kV GSP voltage (`Zbase = 132^2 / 100 = 174.24` ohm), so the stored branch has
  `rPu` 0.0367843204775023, `xPu` 0.1208115243... and `bPu` 5.4728784e-05, and the transformer 0.0236398 and 0.376372.
  The current build writes the values above; the run (and its solve line) has not been retaken.
- **Its OLTC.** The published range is -12 % to +4 %, so `minTap = 1 - 0.12 = 0.88` and `maxTap = 1 + 0.04 = 1.04`,
  step 0.0125, deadband 0.01, controlling bus 8. This is the `onloadtapchanger.json` entry.
- **Demand.** Bus 8 gathers `HINL5_BB_1` (6.24 MW) and `HINL5_BB_2` (4.58 MW), both at power factor 0.96:
  `Pd = 10.82 MW`, `Qd = 10.82 * tan(acos(0.96)) = 10.82 * 0.291667 = 3.1558 MVAr`. Five demand rows land on three
  buses: demand is matched by node, so `Eyam (TA)` (3.79 MW, listed under the `Chesterfield 132kV` group) joins bus 6
  beside `Eyam (TB)` (2.95 MW), giving 6.74 MW there.
- **Slack.** One GSP group gives one slack: among the 33 kV buses, bus 0 and bus 1 both have three branches; the first
  in id order, bus 0, wins. The writer sizes its infeed on this island's demand, 20.78 MW, which is below the floor, so
  `capacityMva = qMaxMvar = 5 * 1000 = 5000`.
- **Solve.** Three Newton iterations and one OLTC pass reach mismatch 1.51e-11 pu with voltages 0.991 to 1.000 pu.

The writer's files are captured as `gda-stalybridge-system.txt`, `gda-stalybridge-buses.txt`,
`gda-stalybridge-transmission.txt`, `gda-stalybridge-transformer.txt`, `gda-stalybridge-oltc.txt`,
`gda-stalybridge-demand.txt` and `gda-stalybridge-generation.txt`. For example, the grid infeed:

```
$ cat results/gda-stalybridge/generation.json
[
  {
    "name": "Grid infeed @ BUXT3_REDUCED_3",
    "bus": 0,
    "type": "grid",
    "capacityMva": 5000,
    "outputMw": 0,
    "voltageSetpoint": 1,
    "qMinMvar": -5000,
    "qMaxMvar": 5000,
    "inertiaH": 0,
    "role": "slack"
  }
]
[exit 0]
```

With geolocation left on, the same carve reports `Geolocated 3/9 buses from substations.csv.` (capture
`gda-stalybridge-geo.txt`): three buses matched the substation table by normalised name and the other six were draped
from them.

### The voltage cut and the three-winding units

```
$ gridsim gda --root $GDA_ROOT --licence EMIDS --minkv 132 --no-geo
GridSim  -  GDA ingestion from $GDA_ROOT  (NGED)
==========================================================================
Built in 209 ms  -  GB/NGED ex-GDA >=132kV

  Raw LTDS nodes          22,590
  Electrical buses           474   (busbar/disconnector ties merged: 770)
  Series circuits            323   (cables & lines)
  Transformers (2w/3w)       190 / 2   (+2 star nodes)
  Total branches             519
  Demand points              824   matched 824, total 8.90 GW
  Generators                   2   (0.00 GW capacity, 894 unmatched)
  System inertia             0.0 GVAs
  OLTCs                      190
  Connected islands            3   (largest 418 buses)

  Buses by voltage:
      132 kV       280
       66 kV         1
       33 kV       147
       25 kV         6
       11 kV        40

   16 GSP infeed(s) modelled as slack buses; primary = bus 3 (Warwick 132kV Main 2, 132 kV).
   ...
   Demand: 814 row(s), 8699.8 MW below the 132 kV cut were folded onto their nearest boundary bus (losses and voltage drop below the cut are not modelled).
   Demand: 1294 row(s), 14331.8 MW are on nodes outside this build's network (other areas of the carve, or nodes on no in-scope element) and were not included.
   Filtered to >=132 kV.
   Licence = EMIDS.
   validation: warning: 16 slack (reference) buses  -  treating as a multi-island system.
[exit 0]
```

(Capture: `gda-emids-132.txt`, with the intermediate notes elided here.) Of the 824 East Midlands demand rows, 814 sit
below the 132 kV cut and are folded onto their nearest boundary bus (8699.8 MW), so the 474-bus model still carries the
whole licence's load; the other 1294 rows belong to other licences. Sixteen GSP groups give sixteen slacks over three
islands. The 2 three-winding units add 2 star nodes and 6 branches, so `Total branches` is `323 + 190 + 6 = 519`
(each three-winding unit contributes an H leg, an L leg and, when a tertiary is on the network, a T leg), and there are
190 OLTCs for the 190 two-winding transformers because no published range was zero or invalid. The embedded generation
join is the weak point of the NGED tables (see Limits): 894 of the 897 generation rows are unmatched.

Connected generation only is the default. `--all-gen` readmits the one accepted unit that the default run reports as
`1 matched row(s), 5.0 MW ... not connected yet` (capture `gda-emids-132-all-gen.txt`: `Generators 3`).

### A failing source

```
$ gridsim gda --root $GDA_ROOT --dno ukpn --no-geo
Load failed: UKPN ukpn-ltds-table-3a-load-data-observed.csv: column Maximum_Demand_24_25_MW gave no positive demand on any known substation, so the replica would have no load.
[exit 1]
```

(Capture `gda-ukpn.txt`, taken on a lake whose UKPN LTDS CSVs contain headers only.) The reader refuses to build a
network with no load.

### Replay on the real GB case

```
$ gridsim gda-replay --synth --case gb-full --start 2024-01-01 --steps 6 --export-every 3 --out results/gda-replay-gbfull

=== Historical state estimate: gb-full over synthetic-gb-full ===
  reconstructed 6 timestamps  (2024-01-01T00:00:00.0000000Z .. 2024-01-01T02:30:00.0000000Z)
  non-converged: 0
  system loss over the series: 199.9 .. 257.9 MW

  -> results\gda-replay-gbfull
[exit 0]
```

The case has 3539 buses, so the replay selects sparse Newton. The CSV (`gda-replay-gbfull-csv.txt`):

```
$ cat results/gda-replay-gbfull/state-estimate.csv
...
index,time_utc,load_factor,converged,iterations,max_mismatch,total_loss_mw,vmin_pu,vmax_pu
0,2024-01-01T00:00:00.0000000Z,0.7000,True,6,3.528E-011,257.944,0.97135,1.04571
1,2024-01-01T00:30:00.0000000Z,0.6782,True,2,2.201E-009,242.344,0.97318,1.04572
2,2024-01-01T01:00:00.0000000Z,0.6586,True,2,1.412E-009,228.665,0.97482,1.04572
3,2024-01-01T01:30:00.0000000Z,0.6413,True,2,8.048E-010,217.002,0.97624,1.04572
4,2024-01-01T02:00:00.0000000Z,0.6268,True,2,3.930E-010,207.417,0.97742,1.04572
5,2024-01-01T02:30:00.0000000Z,0.6152,True,2,1.538E-010,199.945,0.97835,1.04573
[exit 0]
```

The load factors are the daily shape recomputed by hand: at 00:00, `x = 20 / 24`, `0.60 + 0.40 * (0.5 - 0.5 *
cos(2 * pi * 20/24)) = 0.60 + 0.40 * 0.25 = 0.7000`; at 00:30, `x = 20.5 / 24`, giving 0.678248, shown as 0.6782. Step 0 is
solved cold (6 iterations); every later step warm-starts from the previous converged state and needs 2. Loss falls with
the load factor. `exports/state-000000.json` and `exports/state-000003.json` are `gridsim-solve-export/1` documents for
steps 0 and 3.

The same replay with `--passive-plant` (capture `gda-replay-passive-csv.txt`) differs from step 1 on, because the bound
transformer taps move with delay from the previous converged state; step 0 is identical:

```
$ cat results/gda-replay-passive/state-estimate.csv
...
0,2024-01-01T00:00:00.0000000Z,0.7000,True,6,3.528E-011,257.944,0.97135,1.04571
1,2024-01-01T00:30:00.0000000Z,0.6782,True,3,2.505E-011,242.349,0.98182,1.04572
2,2024-01-01T01:00:00.0000000Z,0.6586,True,3,3.478E-011,228.657,0.98323,1.04572
...
[exit 0]
```

Hypothetical storage on a 48-step day of `case118` (capture `gda-replay-bess.txt`) prints the counterfactual summary:

```
$ gridsim gda-replay --synth --case case118 --steps 48 --bess lfp:10:20:40 --policy peak-shave --out results/gda-replay-bess

=== Historical state estimate: case118 over synthetic-case118 ===
  reconstructed 48 timestamps  (2024-01-01T00:00:00.0000000Z .. 2024-01-01T23:30:00.0000000Z)
  non-converged: 0
  system loss over the series: 50.3 .. 132.5 MW
  COUNTERFACTUAL (Counterfactual): hypothetical storage on recorded history - not a reconstruction.
  LFP 20MW/40MWh @10: out 38.5 MWh, in 21.2 MWh, aux 1.09 MWh, stored change -20.2 MWh, round trip 93.1 %, 0.75 equivalent full cycles
      ageing over the run: calendar 0.1365 %, cycle 0.0026 % (rainflow, depth-aware: 0.0020 %), deepest cycle 90 % SoC

  -> results\gda-replay-bess
[exit 0]
```

### The horizon, enforced

A series stamped inside the 169-hour window is refused before any solve. The capture `gda-replay-rejected.txt` was
taken more than 169 hours after its 2026-10-01 stamps, so on that run the series is historical and replays, exit 0:

```
$ gridsim gda-replay --synth --start 2026-10-01 --steps 4 --out results/gda-replay-rejected

=== Historical state estimate: case118 over synthetic-case118 ===
  reconstructed 4 timestamps  (2026-10-01T00:00:00.0000000Z .. 2026-10-01T01:30:00.0000000Z)
  non-converged: 0
  system loss over the series: 56.7 .. 66.6 MW

  -> results\gda-replay-rejected
[exit 0]
```

Run within 169 hours of 2026-10-01, the same command prints `REJECTED (forward inference): ...` with the horizon, the
run's UTC clock minus 169 hours, and exits 2. A stamp exactly on the horizon loads.

### One stored estimated frame

The estimated-state lake on the reference machine holds one `gb-spine` period (capture
`lake-estimated-state-row.txt`):

```
$ python (read one partition of the estimated_state lake; result_json, removed_json and boundary_json omitted)
partition: $GDA_ROOT/Derived/estimated_state/gb-spine/year=2026/week=36/estimated_state.parquet
rows: 1  columns: 17
...
datetime_utc      2026-09-01 11:00:00+00:00
case              gb-spine
status            Converged
converged         True
iterations        8
max_state_update  1.4822059173716063e-09
objective_j       225.414916686831
chi_threshold     359.5207249007415
chi_passed        True
dof               317
measurements_n    948
removed_n         0
vm_min            0.999361616293283
vm_max            1.051805170833602
[exit 0]
```

The row checks by hand. `gb-spine` has 316 buses, so the state has `2 * 316 - 1 = 631` unknowns (every voltage magnitude
and every angle but the reference), and `dof = 948 - 631 = 317`. The estimator's 95 % threshold is the Wilson-Hilferty
cube-root approximation `317 * (1 - 2/(9 * 317) + z * sqrt(2/(9 * 317)))^3` with `z` = 1.6448536, the 95 % normal
quantile, which is 359.52072; the stored value is 359.5207249 (`ChiSquare.Quantile` in
`src/GridSim.Core/Estimation/ChiSquare.cs`, which takes `z` from Acklam's rational approximation). The exact
chi-square 95 % quantile for 317 degrees of freedom is 359.52184, so the approximation sits 0.0011 below it. `J = 225.4 <
359.5` is why `chi_passed` is true. Manual ch. 22 re-runs the same period and reaches `J` = 330.37 after 10 iterations;
the stored row is a separate run. The `result_json` of that row holds 316 buses and 579 line flows, which
is what a bridge `state` frame carries for the `estimated` source.

## Source map

`GDA Applications`, `GDA Scripts` and `GDA NetworkModel` abbreviate `Applications/`, `Scripts/` and `NetworkModel/` in the
`v1` directory of the GDA repository.

| File | Type or script | Responsibility |
|---|---|---|
| `src/GridSim.Gda/GdaPaths.cs` | `GdaPaths` | Logical dataset paths, publication-date parsing, as-of newest-file selection |
| `src/GridSim.Gda/Csv/Csv.cs`, `Csv/Tabular.cs`, `Csv/VintageColumns.cs` | `CsvTable`, `DataSourceTable`, `VintageColumns` | CSV and Parquet readers, deterministic file choice, newest-vintage column choice |
| `src/GridSim.Gda/Ltds/LtdsModel.cs`, `Ltds/LtdsReader.cs` | `Ltds*` records, `LtdsReader` | NGED row types and reader |
| `src/GridSim.Gda/Ukpn/UkpnSource.cs`, `Npg/NpgSource.cs` | `UkpnSource`, `NpgSource` | UKPN and NPg normalisation into `LtdsDataset` |
| `src/GridSim.Gda/Neso/NesoReader.cs`, `NesoModel.cs`, `NationalFleet.cs` | `NesoReader`, `TecEntry`, `NationalFleetBuilder` | NESO registers, boundary limits, the national fleet |
| `src/GridSim.Gda/Neso/PhaseAngleInferedReader.cs` | `PhaseAngleInferedReader` | System phase-angle series for the estimator (ch. 6) |
| `src/GridSim.Gda/Interpose/QuarantineLiveStore.cs` | `QuarantineLiveStore` | Live-telemetry quarantine store (ch. 20) |
| `src/GridSim.Gda/FuelMap.cs` | `FuelMap` | Fuel string to type, inertia constant and capacity factor |
| `src/GridSim.Gda/GdaBuildDefaults.cs` | `GdaBuildDefaults` | Named engineering defaults and guards |
| `src/GridSim.Gda/GdaModelBuilder.cs` | `GdaModelBuilder`, `GdaBuildOptions`, `GdaDiagnostics`, `Dsu` | The assembly of section "Assembly, step by step" |
| `src/GridSim.Gda/Geo/GeoLocator.cs` | `GeoLocator` | Name join, projection, draping, jitter |
| `src/GridSim.Gda/GdaModelWriter.cs` | `GdaModelWriter` | Case directory writer, grid infeed sizing |
| `src/GridSim.Core/Gda/GdaTimeSeries.cs` | `GdaTimeSeries`, `GdaTimeStep` | Series load with horizon, synthetic series, per-step case |
| `src/GridSim.Core/Simulation/GdaReplayLoop.cs` | `GdaReplayLoop`, `GdaReplayResult` | The replay loop |
| `src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs` | `ForwardInferenceGuard` | The 169-hour horizon |
| `src/GridSim.Core/IO/BridgeProtocol.cs` | `BridgeProtocol` and its message, block and DTO classes | The wire schema and its projection to `GridModel`, `SimFrame`, `QueryResult` |
| `src/GridSim.Core/Net/GridBridgeClient.cs` | `GridBridgeClient` | The client: connection, commands, receive loop, guard, faults |
| `src/GridSim.Cli/Program.cs` | `RunGda`, `RunGdaReplay`, `RunGdaFleet`, `RunGdaAllGsps`, `RunSchema` | The verbs |
| `GDA Applications/GridSim-Bridge/bridge_server.py`, `raw_replay.py` | bridge server | WebSocket and HTTP routes, streams, query dispatch, horizon masking |
| `GDA Scripts/horizon.py` | horizon guard | The server-side 169-hour cutoff |
| `GDA NetworkModel/gridsim_export.py` | `to_solve_export` | The `gridsim-solve-export/1` writer used by the bridge |
| `GDA NetworkModel/validate/nr_check.py` | `nr_check.compute` | The sparse Newton plus OLTC solve behind `live` and the presolved lake |
| `GDA NetworkModel/assemble/`, `spine/`, `emit_spine_case.py` | assembler, spine, fold | `gb-full`, spine, `gb-spine` |
| `GDA NetworkModel/presolve_lake.py`, `build_estimated_state.py`, `build_state_divergence.py`, `report_estimation_closure.py` | lake builders | The four lake products |
| `GDA NetworkModel/build_gsp_measurements.py`, `build_bmu_crosswalk.py`, `build_estimator_measurements.py` | measurement builders | Measurement products and the BMU crosswalk |
| `tests/GridSim.Tests/Gda/`, `GdaReplayLoopTests.cs`, `BridgeProtocolTests.cs`, `BridgeClientRobustnessTests.cs`, `SolveExportRoundTripTests.cs` | tests | The invariants above |

## Limits

- Embedded generation joins on the primary-substation name: for NGED on the canonical primary key (N184, October 2026),
  because its generation table names primaries differently from its demand table. On the full NGED lake (no carve) the
  whitespace key built 2 generators (3 rows joined, one left out as not connected; 894 unmatched); the canonical key
  builds 284 (1.09 GW of capacity; 297 rows joined, 13 left out as not connected, 600 unmatched). 214 named primaries
  in the generation table have no demand-table primary at all. The East Midlands captures above predate N184.
- NPg demand joins a circuit or transformer node by its (name, kV) pair, else by site name and the nearest voltage within
  5 % (N184): 86 of the 657 positive-load rows (2.27 GW) on the full lake, the most the published tables allow (it was
  34, 0.26 GW); most other loads are 11 kV primaries whose 11 kV busbar is in neither Appendix 3 nor 4. The rest is
  reported as off-network demand and not modelled. With that demand the full-lake NPg build's power flow stopped
  converging (60 iterations, mismatch 4.63E-001, exit 3): a 26-bus, 33 kV-only island carried Harrogate 33kV (51.9 MW)
  and Wormald Green 33kV (70.8 MW) from one infeed at Coneythorpe T1 33kV (voltage down to 0.519 pu). The island had
  no path to 132 kV because the two sites' grid transformers were built floating (the winding names above). Since N202
  the windings join their busbars and NPg takes the per-island infeed rule: 3,197 -> 2,892 buses, 649 -> 364 islands
  (largest 137 -> 174 buses), 652 -> 461 GSP infeeds, and the power flow converges (sparse Newton 4 iterations, 12 OLTC
  passes, mismatch 1.16E-010; losses 23.3 MW; V 0.948-1.017 pu; worst grid infeed 5.98 % of its 5,000 MVA, West Boldon
  66kV, 298.9 MVA). The infeed rule alone did not converge (751 infeeds, mismatch 1.84E-001, V 0.464-1.024 pu).
- Below a `--minkv` cut, losses and voltage drop are not modelled; the folded demand sits on the boundary bus.
- `gda --all-gsps` does not apply `--licence` or geolocation, and takes its group list from every GSP name in the circuit
  table.
- A three-winding unit is built with `Node1` as the high-voltage end and `Node2` as the low-voltage end whatever their
  voltages.
- The NESO reader reads CSV registers from `NESO/RAWData/...`, so a lake that holds them only as Parquet makes
  `gda --fleet` fail with "GDA dataset folder not found".
- The UKPN and NPg readers ignore `--as-of` and take the files as found.
- Only the NGED and UKPN readers produce three-winding transformers.
- `gda-replay --case` accepts `case9` (`ieee9`), `case14`, `case30`, `case57`, `case118`, `case300`, `case145`, `case24`
  (`rts`), `cigre`, `uk` (`gb`, `gb-reduced`), `gb-full`, `gb-synthetic` (`synthetic`), a file name under
  `data/matpower`, and a JSON case directory holding `system.json`, such as one written by
  `gda --out`. Its default `--out` is `results/gda-replay-<case>` under the current directory.
- When `--series` and `--synth` are both given, `--synth` is used without a message.
- `gb-spine` is a documented fold of `gb-full`, not a boundary equivalent: sub-GSP impedances are dropped.
- The ETYS circuit overlay (`SPINE_ETYS=1`) is off by default because it is not era-aware.
- A stored-source `start` of `first` on a case whose lake holds frames but no `topology.json` falls back to `live` with
  the literal `first`, which replies `error` "Invalid start date".
- The client matches a reply with an unknown or absent `id` to any pending query of the same `op`, not the oldest.
- `build_estimated_state.py` finds the GridSim CLI from `GRIDSIM_CLI` and otherwise from a fixed default location that
  does not exist on every machine.

## See also

- [ch. 6, State estimation](<0006 State estimation.md>) - the WLS formulation, the chi-square test and the `estimate` verb the estimated-state builder drives
- [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) - `ProvenanceTag` and the forward-inference guard the bridge enforces
- [ch. 10, File and wire formats](<0010 File and wire formats.md>) - field-by-field schemas for the bridge messages, the case files and the lake products
- [ch. 11, Validation](<0011 Validation.md>) - how the estimated history is validated and closed
- [ch. 13, Asset dynamics data grounding](<0013 Asset-dynamics data grounding.md>) - the passive plant a replay can run
- [ch. 27, Battery energy storage](<0027 Battery energy storage.md>) - the storage model behind `--bess`
- [ch. 22, Estimating GB history](<../manual/0022 Estimating GB history.md>) - running the GDA pipeline day by day
- [ch. 23, Replaying estimated states](<../manual/0023 Replaying estimated states.md>) - replaying estimated states in the clients
- [ch. 24, The GDA bridge](<../manual/0024 The GDA bridge.md>) - starting and driving the bridge as a user
- [the CLI reference, `gda`](<../manual/0019 CLI reference.md#gda>) and [`gda-replay`](<../manual/0019 CLI reference.md#gda-replay>) - every flag
