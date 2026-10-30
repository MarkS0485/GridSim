# Test-case libraries and the MATPOWER parser

How GridSim gets its networks. Every standard test system  -  from the WSCC 9-bus toy up to
~78k-bus continental models  -  enters the solver through one of two doors: a small set of
**built-in factories** (`IeeeCases`) or the **MATPOWER `.m` parser** reading files committed
under `data/matpower/`. This document describes both, the committed archive, how cases are
validated, how to add one, and the known non-convergence / parse-gap classes.

- Parser: `src/GridSim.Core/IO/Matpower/MatpowerParser.cs`
- Parse result record: `src/GridSim.Core/IO/Matpower/MatpowerCase.cs`
- Built-in factories: `src/GridSim.Core/IO/IeeeCases.cs`
- Embedding config: `src/GridSim.Core/GridSim.Core.csproj`
- Committed archive: `data/matpower/` (provenance in `data/matpower/README.md`)
- Fetch script: `tools/fetch-cases.sh`
- Tests: `tests/GridSim.Tests/IeeeCasesTests.cs`, `tests/GridSim.Tests/MatpowerParserTests.cs`
- Server run findings: `docs/developer/benchmarks/server-results/README-section.md`

---

## 1. The MATPOWER `.m` parser

`MatpowerParser` (`src/GridSim.Core/IO/Matpower/MatpowerParser.cs:24`) is a static class and the
**single import path** for the PEGASE, RTS, NESTA/PGLib and wider MATPOWER libraries, all of
which ship natively in MATPOWER `.m` format
(`src/GridSim.Core/IO/Matpower/MatpowerParser.cs:8-13`). It preserves the source operating point
verbatim  -  `mpc.gen` Pg/Qg/Vg and the stored bus voltages are kept as-is, no dispatch
reinterpretation  -  so the parsed model reproduces the file's snapshot.

### Public API

| Member | Signature | Behaviour |
|--------|-----------|-----------|
| `Parse` | `Parse(string text, string? name = null, double frequencyHz = 50.0) : GridModel` | Parse case text; thin wrapper over `ParseWithDiagnostics(...).Model`. `MatpowerParser.cs:28` |
| `ParseFile` | `ParseFile(string path) : GridModel` | `File.ReadAllText` then `Parse`; case name defaults to the file stem (`Path.GetFileNameWithoutExtension`). `MatpowerParser.cs:32` |
| `ParseWithDiagnostics` | `ParseWithDiagnostics(string text, string? name = null, double frequencyHz = 50.0) : MatpowerParseResult` | The real workhorse; returns the model plus a list of informational notes. `MatpowerParser.cs:36` |
| `MatpowerParseResult` | `record (GridModel Model, IReadOnlyList<string> Notes)` | Model + diagnostics. Notes are informational only  -  a case that produced notes is still a valid, solvable model. `MatpowerCase.cs:9` |
| `MatpowerUnitHandling` | optional last parameter of `Parse`, `ParseFile` and `ParseWithDiagnostics` (N165, October 2026) | Default `NormaliseRawFeeders`: a raw ohm / kW distribution feeder (case22, case33bw, case69, case85) is converted to per-unit / MW by `MatpowerUnitNormalizer.Normalize`, with a parse note and `MatpowerParseResult.RawUnitsConverted = true`; every other file is unaffected. `AsWritten` keeps the numbers exactly as the file writes them (the behaviour before N165). |

The case name resolves in priority order: the `name` argument, else the `function mpc = <name>`
declaration, else the literal `"matpower"` (`MatpowerParser.cs:26-27`, `:45`,
`ExtractFunctionName` at `:209-213`). `baseMVA` defaults to `100.0` when absent
(`MatpowerParser.cs:44`). Default frequency is 50 Hz (GB). Empty/whitespace text throws
`InvalidDataException` (`MatpowerParser.cs:38-39`).

### Tokenizing

Parsing happens in two stages: `Clean` normalises the text, then regex-driven extractors pull out
the scalar and each matrix.

1. **Comment strip + continuation join** (`Clean`, `MatpowerParser.cs:176-194`). The text is split
   on `\n`; each line has its trailing `%` comment removed by `StripComment`
   (`MatpowerParser.cs:197-207`), which respects single-quoted strings so a `%` inside a bus name
   is not treated as a comment. A line whose trimmed content ends with `...` is a MATLAB line
   continuation: its content (minus the `...`) is buffered and joined onto the next line before
   emitting (`MatpowerParser.cs:184-192`).
2. **Scalar extraction** (`ExtractScalar`, `MatpowerParser.cs:215-219`). `mpc.baseMVA` is matched
   with a regex accepting an optional sign, digits/decimal, and scientific `e`/`E` exponent.
3. **Matrix extraction** (`ExtractMatrix`, `MatpowerParser.cs:222-246`). Finds the first
   `mpc.<field> = [` (whole-word `\b` match so `mpc.bus` does **not** also match `mpc.bus_name`,
   `MatpowerParser.cs:224-225`), takes everything up to the next `]`, splits rows on `;`, and
   splits each row on spaces/tabs/CR/LF/commas. Each token is parsed by `ParseNum`.
4. **Number parsing** (`ParseNum`, `MatpowerParser.cs:164-173`) reads with `InvariantCulture` and
   maps MATLAB infinity/NaN spellings: `Inf`/`inf`/`+Inf`/`Infinity` -> `+inf`,
   `-Inf`/`-inf`/`-Infinity` -> `-inf`, `NaN`/`nan` -> NaN. These infinities are how MATPOWER
   encodes open generator limits.

`At(row, i, dflt)` (`MatpowerParser.cs:159-160`) reads column `i` safely, returning `dflt` when
the row is shorter than expected  -  so cases that omit trailing optional columns still parse.

### Column mappings

MATPOWER columns are 1-based in the file spec; the parser uses 0-based indices. The mappings
below are exactly what the code reads.

**`mpc.bus`** (`MatpowerParser.cs:58-84`)

| Col (1-based) | Index | Field | Maps to |
|---|---|---|---|
| 1 | 0 | `bus_i` | `Bus.Id` (id preserved verbatim; not required to be contiguous/sorted) |
| 2 | 1 | `type` | `Bus.Type` (see rules below) |
| 3 | 2 | `Pd` | `Bus.Pd` |
| 4 | 3 | `Qd` | `Bus.Qd` |
| 5 | 4 | `Gs` | `Bus.Gs` |
| 6 | 5 | `Bs` | `Bus.Bs` |
| 8 | 7 | `Vm` | `Bus.Vm` (default `1.0`) |
| 9 | 8 | `Va` | `Bus.VaDeg` |
| 10 | 9 | `baseKV` | `Bus.BaseKv` |

**`mpc.gen`** (`MatpowerParser.cs:96-115`)

| Col (1-based) | Index | Field | Maps to |
|---|---|---|---|
| 1 | 0 | `bus` | `Generator.Bus` |
| 2 | 1 | `Pg` | `Generator.Pg` |
| 3 | 2 | `Qg` | `Generator.Qg` |
| 4 | 3 | `Qmax` | `Generator.Qmax` (default `+inf`) |
| 5 | 4 | `Qmin` | `Generator.Qmin` (default `-inf`) |
| 6 | 5 | `Vg` | `Generator.Vg` (default `1.0`) |
| 7 | 6 | `mBase` | `Generator.MBase` if `> 0`, else the system `baseMVA` (`MatpowerParser.cs:112`) |
| 8 | 7 | `status` | dropped if `<= 0` (`MatpowerParser.cs:99`) |
| 9 | 8 | `Pmax` | `Generator.Pmax` (default `+inf`) |
| 10 | 9 | `Pmin` | `Generator.Pmin` |

Inertia `H` is not present in MATPOWER data and is set to `0` (`MatpowerParser.cs:113`).

**`mpc.branch`** (`MatpowerParser.cs:121-141`)

| Col (1-based) | Index | Field | Maps to |
|---|---|---|---|
| 1 | 0 | `fbus` | `Branch.From` |
| 2 | 1 | `tbus` | `Branch.To` |
| 3 | 2 | `r` | `Branch.R` |
| 4 | 3 | `x` | `Branch.X` |
| 5 | 4 | `b` | `Branch.B` |
| 6 | 5 | `rateA` | `Branch.RateAMva` |
| 9 | 8 | `ratio` | tap ratio  -  drives transformer detection (below) |
| 10 | 9 | `angle` | `Branch.PhaseShiftDeg` |
| 11 | 10 | `status` | dropped if `<= 0` (`MatpowerParser.cs:123`) |

### Mapping rules

- **Bus type** (`MatpowerParser.cs:62-69`): `3 -> Slack`, `2 -> Pv`, `1 -> Pq`; anything else falls
  through the `_` arm to `Pq`. **Type 4 (isolated)** buses are detected first and **dropped**
  (added to an `isolated` set, `continue`d; `MatpowerParser.cs:62`).
- **`Inf` handling**: open Q/P limits written as `Inf`/`-Inf` become `double.PositiveInfinity` /
  `double.NegativeInfinity` (`ParseNum`, `MatpowerParser.cs:164-173`); this is asserted by
  `Maps_Inf_generator_limits_to_infinity` (`MatpowerParserTests.cs:48-55`).
- **Transformer detection by tap ratio** (`MatpowerParser.cs:128-140`): a branch is a transformer
  iff its ratio column is non-zero (`ratio != 0.0`). If so, `IsTransformer = true` and
  `TapRatio = ratio`; otherwise it is a line with `TapRatio = 1.0`. (MATPOWER's convention: `0`
  means "not a transformer / nominal ratio".) Verified by `Line_vs_transformer_by_ratio`
  (`MatpowerParserTests.cs:57-75`).
- **Dropping isolated / out-of-service / dangling elements**:
  - Type-4 buses are removed and their ids never enter the `liveIds` set (`MatpowerParser.cs:62`,
    `:83`).
  - Generators with `status <= 0` are skipped (`genOff` counter, `MatpowerParser.cs:99`); so are
    generators on a bus that isn't in `liveIds`, i.e. on a dropped/isolated bus
    (`MatpowerParser.cs:100`).
  - Branches with `status <= 0` are skipped (`brOff`, `MatpowerParser.cs:123`); branches touching
    a dropped bus on either end are skipped (`brDangling`, `MatpowerParser.cs:126`). This guarantees
    no dangling reference can reach the solver (`MatpowerParser.cs:20-22`). Covered by
    `Drops_isolated_buses_and_incident_elements` (`MatpowerParserTests.cs:77-90`) and
    `Skips_out_of_service_elements` (`MatpowerParserTests.cs:92-103`).
- **Non-contiguous bus ids preserved** (`MatpowerParser.cs:18-22`): buses need not be contiguous or
  sorted. The solvers key on the bus id, and result row `i` maps to `model.Buses[i]`, so file order
  is preserved. `Handles_noncontiguous_bus_ids` parses ids `{100, 205, 9999}`, solves, and checks
  both `m.Buses` and `r.BusIds` come back in that exact order (`MatpowerParserTests.cs:105-118`).

### Reference-bus check

After building the bus list the parser counts slack (type-3) buses (`slackCount`,
`MatpowerParser.cs:70`) and enforces:

- **Zero slack -> throw.** `InvalidDataException("... has no reference (type 3) bus  -  the power
  flow would be singular.")` (`MatpowerParser.cs:86-87`). Tested by `Throws_when_no_reference_bus`
  (`MatpowerParserTests.cs:120-129`).
- **Multiple slack -> note, not error.** A diagnostic "N reference (slack) buses  -  treating as a
  multi-island system." is added (`MatpowerParser.cs:88-89`).

Other diagnostics appended to `Notes`: dropped isolated buses (`:90-91`), skipped out-of-service
generators (`:116`), skipped out-of-service branches (`:142`), and skipped dangling branches
(`:143`).

### Known limitation: no arithmetic-expression evaluation

The parser reads each token as a single number via `double.Parse` (`ParseNum`,
`MatpowerParser.cs:171`). It does **not** evaluate arithmetic expressions. Some ACTIVSg cases embed
literals like `135/sqrt(3)` in the data matrices; these fail to load. This is a **current gap**,
not a corruption  -  on the server run the two `case533mt` files were the only load failures:

```
LOAD FAIL: The input string '135/sqrt(3)' was not in a correct format.
```
(`docs/developer/benchmarks/server-results/README-section.md:93-94`, `docs/developer/benchmarks/server-results/20260701-101835/regime.log:110-111`).
Closing it would require a tiny expression evaluator in `ParseNum`.

---

## 2. Built-in cases (`IeeeCases`)

`IeeeCases` (`src/GridSim.Core/IO/IeeeCases.cs:13`) exposes standard systems as plain factory
methods so callers use them identically regardless of origin  -  the solver neither knows nor
cares where a `GridModel` came from (`IeeeCases.cs:6-12`).

### `Case9`  -  the MATPOWER file plus the WSCC machine data

`Case9()` is the WSCC 9-bus system (MATPOWER `case9`), 3 generators, 3 loads, 100 MVA base, 345 kV. Since N199
(October 2026) it reads the embedded `case9.m` itself, so its buses (with the file's 0.9-1.1 pu limits), branches
(RATE_A, RATE_B, RATE_C), generators (Pmin 10 MW each) and cost curves are the file's, and lays over each unit only
the published WSCC machine data from Anderson and Fouad: the machine rating as MBase (247.5 / 192 / 128 MVA) and the
inertia constant H on that base (23.64 / 6.40 / 3.01 s), which no `.m` file carries. Before N199 it was typed out by
hand without the limits, Pmin, RATE_B / RATE_C and costs. By project convention it is the physics ground truth.

### Embedded `.m` factories

The remaining factories parse a `.m` file embedded in the assembly via the private helper `Mpc`
(`IeeeCases.cs:90-97`), which loads the manifest resource `GridSim.Core.cases.<res>.m` and calls
`MatpowerParser.Parse`.

| Factory | `.m` resource | Display name | Source |
|---------|---------------|--------------|--------|
| `Case14()` `:60` | `case14` | IEEE 14-bus | MATPOWER 8.0 |
| `Case30()` `:63` | `case30` | IEEE 30-bus | MATPOWER 8.0 |
| `Case57()` `:66` | `case57` | IEEE 57-bus | MATPOWER 8.0 |
| `Case118()` `:69` | `case118` | IEEE 118-bus | MATPOWER 8.0 |
| `Case300()` `:72` | `case300` | IEEE 300-bus | MATPOWER 8.0 |
| `Case145()` `:75` | `case145` | IEEE 145-bus (dynamic) | MATPOWER 8.0 |
| `Case24Rts()` `:81` | `case24_ieee_rts` | IEEE RTS-79 24-bus | MATPOWER 8.0 |
| `CigreMv()` `:87` | `cigre_mv` | CIGRE MV benchmark (TF C6.04.02) | authored |

### How embedding works

`GridSim.Core.csproj` marks the nine `.m` files as `<EmbeddedResource>` with an explicit
`LogicalName`, e.g.:

```xml
<EmbeddedResource Include="..\..\data\matpower\lib\case14.m"
                  LogicalName="GridSim.Core.cases.case14.m" />
```
(`src/GridSim.Core/GridSim.Core.csproj`, the `<ItemGroup>` of embedded cases). The `LogicalName`
gives a stable resource id `GridSim.Core.cases.<name>.m` independent of the on-disk folder, which is
exactly the string `Mpc` reconstructs (`IeeeCases.cs:92`). The **single source of truth is the
committed `.m` files under `data/matpower`**; the classic IEEE cases + CIGRE MV are pulled from
`lib/` and `benchmarks/` at build time. `case9.m` is embedded too: `Case9()` reads it (N199).

### RTS-79 (24-bus) vs RTS-96 (73-bus)

These are distinct systems, easily confused:

- **RTS-79** is the single-area 24-bus IEEE Reliability Test System, exposed as `Case24Rts()` /
  `case24_ieee_rts` and embedded (`IeeeCases.cs:77-81`).
- **RTS-96** is the three-area **73-bus** system, a separate and larger case shipped as
  `data/matpower/rts/pglib_opf_case73_ieee_rts.m` (RTS-96, 73-bus 3-area) and **not** embedded  -
  it is loaded from disk (`IeeeCases.cs:78-80`, `data/matpower/README.md:15`).

### CIGRE MV

`CigreMv()` / `cigre_mv` (`IeeeCases.cs:83-87`) is a ~15-node, 20 kV **radial distribution feeder**
(not a transmission system), authored to the **CIGRE Task Force C6.04.02** European MV distribution
benchmark spec ("Benchmark Systems for Network Integration of Renewable and Distributed Energy
Resources", 2014). The `.m` header documents the derivation: one 110 kV source (Bus 0) feeding two
20 kV feeders through two 25 MVA 110/20 kV transformers, base (no-DER) configuration with the three
normally-open tie switches S1/S2/S3 open (so the network is a tree), line/transformer/load data
reproducing pandapower's `create_cigre_network_mv`, converted to per-unit on a 100 MVA system base
(`data/matpower/benchmarks/cigre_mv.m:1-20`). The test expects 15 buses / 14 branches and
convergence (`MatpowerParserTests.cs:181-189`).

---

## 3. The committed archive (`data/matpower/`)

The repo commits a curated subset so a lean checkout can build, embed, and test without any network
fetch. Layout (provenance table in `data/matpower/README.md:11-17`):

| Folder | Committed contents | Source | Version | Licence |
|--------|--------------------|--------|---------|---------|
| `lib/` | `case9/14/30/57/118/145/300`, `case24_ieee_rts` | MATPOWER `data/` | 8.0 | BSD-3-Clause (`LICENSE-MATPOWER.txt`) |
| `pegase/` | `case1354pegase`, `case2869pegase`, `case9241pegase` | MATPOWER `data/` | 8.0 | BSD-3-Clause + citation |
| `rts/` | `pglib_opf_case73_ieee_rts` (RTS-96, 73-bus 3-area) | PGLib-OPF | master | CC BY 4.0 |
| `nesta/` | `pglib_opf_case{3_lmbd,5_pjm,30_as,162_ieee_dtc,240_pserc,588_sdet}` | PGLib-OPF | master | CC BY 4.0 |
| `benchmarks/` | `cigre_mv` (authored, not upstream) | CIGRE TF C6.04.02 / pandapower ref | - | see README |

The `lib/` files double as the embedded-resource source for `IeeeCases`. Provenance, required
academic citations (PEGASE, PGLib-OPF/RTS-96/NESTA-lineage, CIGRE MV), the NESTA-retirement note,
and the CIGRE MV derivation are all maintained in **`data/matpower/README.md`**  -  see there
rather than duplicating. The BSD-3-Clause text for the MATPOWER case data is in
`data/matpower/LICENSE-MATPOWER.txt`.

### After the server fetch (`tools/fetch-cases.sh`)

`tools/fetch-cases.sh` (`tools/fetch-cases.sh:1-35`) expands the committed archive into the **full
standard-case universe** on a build/benchmark host. It is idempotent (skips files already present,
`fetch-cases.sh:17`, `:28`) and drops files into two new folders under `data/matpower`:

- **`pglib/`** (`fetch-cases.sh:11-20`): the entire **PGLib-OPF** archive. It hits the GitHub trees
  API for `power-grid-lib/pglib-opf` (`master`), greps out every `pglib_opf_case*.m` path, and
  downloads each from `raw.githubusercontent.com/.../master/`.
- **`matpower-all/`** (`fetch-cases.sh:22-31`): every `data/case*.m` from the **MATPOWER** repo,
  pinned to tag `8.0`, via the same trees-API-then-raw pattern.

Nothing is embedded from these; they are **disk-discovered only** (see below). After the fetch the
tree holds roughly **~161 `.m` cases** spanning up to **~78k buses** (e.g. the ACTIVSg25k / 70k
networks), the union of PGLib-OPF and MATPOWER's `data/`. The server run confirmed clean scaling and
reached 10,000 buses (`case_ACTIVSg10k`) in its partial snapshot
(`docs/developer/benchmarks/server-results/README-section.md:57-63`).

### Auto-discovery

Both the CLI and the WPF picker enumerate `data/matpower` **recursively** for `*.m`, so whatever
`fetch-cases.sh` drops in is picked up automatically:

- CLI registry `BuildCases` (`src/GridSim.Cli/Program.cs:793-826`): starts with the nine embedded
  classics, then `Directory.EnumerateFiles(mp, "*.m", SearchOption.AllDirectories)`
  (`Program.cs:814`), **skipping any file whose stem duplicates an embedded classic** so disk copies
  of `case9`/`case14`/... don't double-register (`Program.cs:798-799`, `:817-818`). A rough bus-count
  hint is parsed from digits in the filename for ordering (`Program.cs:819-821`).
- WPF `ModelCatalog.Discover` (`src/GridSim.Wpf/ModelCatalog.cs:38-44`): adds one `ModelSource` per
  discovered `.m` labelled `"MATPOWER  <stem>"`, alongside the GB reduced/synthetic/GDA sources.

---

## 4. Validation

### `IeeeCasesTests` (`tests/GridSim.Tests/IeeeCasesTests.cs`)

Validates the built-in systems on three axes:

1. **Dimensions** (`Has_expected_dimensions`, `IeeeCasesTests.cs:48-64`): a `[Theory]` asserting the
   exact `(buses, gens, branches)` for each case (14/30/57/118/300/24/145), `BaseMva == 100`, and
   **exactly one slack bus**. The counts are post-parse (i.e. after dropped isolated/out-of-service
   elements): e.g. `("118", 118, 54, 186)`, `("300", 300, 69, 411)`, `("24", 24, 33, 38)`
   (`IeeeCasesTests.cs:49-55`).
2. **Convergence + balance** (`Converges_and_balances`, `IeeeCasesTests.cs:66-86`): solves with the
   default `NewtonRaphsonPowerFlow` (generator Q-limits **on**, like the case9 tests), asserts
   `Converged`, `MaxMismatch < 1e-6`, that net injections sum to total losses
   (`r.TotalLossMw == r.PInjMw.Sum()`), and that losses are non-negative.
3. **Published Vm/Va with slack re-anchoring** (`AssertPublished`, `IeeeCasesTests.cs:97-126`).
   Each case has MATPOWER's stored solved `Vm`/`Va` arrays inlined (`IeeeCasesTests.cs:16-33`).
   The test solves, maps results by bus id, then compares. Because the source's angle reference need
   not be the slack bus, **both sides are re-referenced to the slack** before comparing angles:
   `dva = (va[id] - solvedSlack) - (expVa[i] - offset)` (`IeeeCasesTests.cs:113-124`). Per-case
   tolerances are ~2x the observed deviation:

   | Case | Vm tol | Va tol (deg) | Ref |
   |------|-------:|-------------:|-----|
   | 14 | 0.005 | 0.2 | `:91` |
   | 57 | 0.02 | 1.5 | `:92` |
   | 118 | 0.03 | 1.0 | `:93` |
   | 300 | 0.02 | 12.0 | `:94` |
   | 145 | 0.01 | 0.1 | `:95` |

   The case300 angle bound is deliberately loose because its stored classic angles are only roughly
   converged; its voltages still match tightly (`IeeeCasesTests.cs:88-90`).

### `MatpowerParserTests` (`tests/GridSim.Tests/MatpowerParserTests.cs`)

Two layers:

- **Tokenizer / mapping units** over inline `.m` fragments (`MatpowerParserTests.cs:17-129`): scalar
  + matrix parse, `Inf` -> infinity, line-vs-transformer by ratio, isolated-bus dropping,
  out-of-service skipping, non-contiguous ids, and the no-reference-bus throw (all cited in section
  1 above).
- **Round-trip + library integration.** `Case9_from_file_matches_handcoded_structure_and_solution`
  (`MatpowerParserTests.cs:133-158`) parses `lib/case9.m` and asserts it matches
  `IeeeCases.Case9()` in structure and, after solving both, in Vm (6 dp) / Va (4 dp)  -  proving the
  parser reproduces the ground truth (since N199 `N199AlignedCase9Tests` proves the two bit-identical). `Large_case_parses_and_converges`
  (`MatpowerParserTests.cs:162-179`) is a `[Theory]` over the three PEGASE cases + RTS-96, **guarded
  on file existence** (`if (!File.Exists(path)) return;`, `:170`) so a lean checkout stays green; it
  solves with the **sparse** solver (Q-limits off, 100 iters) and requires `MaxMismatch < 1e-4`.
  `CigreMv_parses_and_converges` (`:181-189`) checks the 15-bus/14-branch radial feeder solves.

### Dense-vs-sparse agreement

The trust signal for the large cases is that the **dense** (`NewtonRaphsonPowerFlow`) and **sparse**
(`SparseNewtonRaphsonPowerFlow`, direct sparse LU with an ILU0/BiCGSTAB fallback) solvers agree case-for-case on
iteration count and final mismatch. Since 1.95.001 they are bit-identical on the CPU (same Newton step;
`DenseSparseParityTests`), so this cross-check now tests the two assemblies, not two numerical answers. The server regime runs both sweeps to cross-check. In the
partial snapshot the **dense reference sweep had not yet started**, so its column is marked
*pending* and the cross-check is explicitly **not yet available**
(`docs/developer/benchmarks/server-results/README-section.md:6-11`, `:32-35`, `:123-126`). The sparse half alone landed
every converged case at mismatch <= ~1e-9 (mostly 1e-11/1e-12).

---

## 5. How to add a case

Two paths, depending on whether the case should be built into `GridSim.Core` or just available to
the tools.

### A. Disk case (usual  -  no rebuild of Core needed)

1. Drop a valid MATPOWER `.m` file anywhere under `data/matpower/` (any subfolder; discovery is
   recursive).
2. It is **auto-discovered**: the CLI `bench`/`sweep`/N-1/stress verbs and the WPF source picker
   enumerate `data/matpower/**/*.m` (`src/GridSim.Cli/Program.cs:814`,
   `src/GridSim.Wpf/ModelCatalog.cs:41`). No code change.
3. Constraints: the file must have an `mpc.bus` matrix (`MatpowerParser.cs:47-48`) and at least one
   type-3 slack bus (`MatpowerParser.cs:86-87`). If the filename stem duplicates an embedded classic
   (`case9`, `case14`, ..., `cigre_mv`), the CLI skips the disk copy in favour of the embedded one
   (`Program.cs:798-799`, `:817-818`).
4. If the case has real provenance/licence terms, add a row to `data/matpower/README.md`.

### B. Embedded factory (built into `GridSim.Core`)

Use this only when the case must ship inside the Core assembly (as the classics do).

1. Place the `.m` file under `data/matpower/` (e.g. `lib/` or `benchmarks/`).
2. Add an `<EmbeddedResource>` line to `src/GridSim.Core/GridSim.Core.csproj` with a
   `LogicalName="GridSim.Core.cases.<stem>.m"` (match the existing pattern).
3. Add a factory to `IeeeCases` returning `Mpc("<stem>", "<display name>")`
   (`IeeeCases.cs:90-97`).
4. Add it to the embedded-classic skip set in the CLI (`Program.cs:798-799`) so the disk copy isn't
   also registered.
5. Add a test: dimensions + convergence (and published Vm/Va if a solved snapshot exists), following
   `IeeeCasesTests`.

---

## 6. Known non-convergence classes (from the server run)

Findings below are from the partial server sweep, `docs/developer/benchmarks/server-results/README-section.md:65-94`
(mirrored in `README.md:240-245`). These are **expected outcomes, not parser bugs** or solver
failures.

### PGLib-OPF operating points (flat-start power flow)

PGLib-OPF cases ship an **optimisation (OPF) benchmark** operating point, not a solved
power-flow snapshot. Run as a plain flat-start Newton-Raphson power flow they hit the 100-iteration
cap with large residuals, while their **non-OPF twins converge trivially**
(`README-section.md:66-84`):

| PGLib OPF case | Buses | OPF result (conv / iters / mismatch) | Non-OPF twin |
|----------------|------:|:-------------------------------------|:-------------|
| `..._case1888_rte` | 1888 | NO / 100 / 2.35E+02 | `case1888rte` yes / 2 / 118 ms |
| `..._case1951_rte` | 1951 | NO / 100 / 9.97E+01 | `case1951rte` yes / 3 / 132 ms |
| `..._case2848_rte` | 2848 | NO / 100 / 2.12E+02 | `case2848rte` yes / 2 / 440 ms |
| `..._case2868_rte` | 2868 | NO / 100 / 2.98E+01 | `case2868rte` yes / 5 / 448 ms |
| `..._case6470_rte` | 6470 | NO / 100 / 1.15E+02 | `case6470rte` yes / 3 / 2687 ms |

Plus `..._case2000_goc`, `..._case3022_goc`, `..._case4917_goc`, `..._case6495_rte`,
`..._case6515_rte`, `..._case2853_sdet`, `..._case240_pserc`, `..._case300_ieee`,
`..._case179_goc`, `..._case162_ieee_dtc`, `..._case1803_snem`, `..._case39_epri`,
`..._case3_lmbd` (`README-section.md:82-84`). The NESTA note in `data/matpower/README.md:19-25`
flags the same: heavily-loaded OPF benchmarks may not converge under a plain NR power flow.

### Radial distribution feeders

Small single-generator distribution feeders don't converge under the transmission-oriented polar
Newton-Raphson solver: `case12da`, `case15da`, `case16am`, `case18nbr`, `case22`, `case28da`,
`case33bw`, `case33mg`, `case34sa`, `case38si`, `case51he/51ga`, `case69`, `case74ds`, `case85`,
`case94pi`, `case118zh`, `case136ma`, `case141`, `case10ba`
(`README-section.md:86-89`). These motivate a dedicated **backward/forward-sweep distribution
solver** (tracked). Note CIGRE MV is a radial feeder too, but small enough that the transmission NR
still converges on it (`data/matpower/benchmarks/cigre_mv.m:19-20`).

### Parse-gap load failures (not a convergence class)

`case533mt_hi` / `case533mt_lo` fail to **load** (not to converge) because of the unevaluated
`135/sqrt(3)` arithmetic literal  -  the section-1 parser gap (`README-section.md:93-94`).

**Bottom line:** on the 28-core EPYC 7763 the sparse solver converged the full IEEE/PEGASE line-up
to machine-tight mismatches (<= ~1e-9) from 9 to 10,000 buses; the only non-convergences are the two
expected classes above, and the only load failures are the two `sqrt(3)` cases
(`README-section.md:114-126`).
