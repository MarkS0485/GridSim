# 2. Loading networks

Every GridSim study starts from a network, and the solver does not know or care where the network came from. This
chapter lists every way to give GridSim one: a built-in name, a MATPOWER `.m` file, a JSON case folder, one of the
shipped GB, international and distribution cases, or a JSON v2 case manifest. It says what each way accepts, what
each refuses and with which message, which folders are bundled (and so never count against a licence limit), and which
verbs take a network as a positional argument and which take `--dir`. Read it when you have a network and need to know
how to load it, or when a load fails.

## Worked example

Load the IEEE 14-bus case by name and solve it once:

```bash
gridsim case14 --once
```

```
$ gridsim case14 --once
GridSim  -  IEEE 14-bus
14 buses  20 branches  5 generators  base 100 MVA  50 Hz
========================================================================
Converged (2 NR iters, 0 OLTC passes, max mismatch 1.32E-010 p.u.)
Generator output 0.27 GW  Demand 0.26 GW  Losses 13.4 MW  Inertia not in the data (no generator H)

Bus  Name                            kV    Vm(pu)  Va(deg)    P(MW)   Q(MVAr)
------------------------------------------------------------------------------
  1                                   -   1.060     0.00     232.4     -16.5
  2                                   -   1.045    -4.98      18.3      30.9
  3                                   -   1.010   -12.73     -94.2       6.1
...
 14                                   -   1.036   -16.03     -14.9      -5.0
kV '-': base voltage not in the data for 14 of 14 bus(es) (the case gives 0).

Voltage range: 1.010 - 1.090 p.u. (OUT OF BAND)
Total losses: 13.39 MW, 8.94 MVAr
Protection simulation: no relay would operate on this state (generic settings).
[exit 0]
```

The report is the one described in [ch. 5, Running power flows](<0005 Running power flows.md>). Timings and
stamps in captured output differ by machine and run, and captures show file paths relative to the repository (the CLI itself
prints absolute paths). Two lines belong to this chapter.

- The header line `14 buses  20 branches  5 generators  base 100 MVA  50 Hz` states what loaded: the counts after the
  loader has dropped isolated buses and out-of-service branches, the system base, and the nominal frequency. Check it
  first when a result looks wrong; a case that loaded with fewer buses than you expected says so here.
- `Inertia not in the data (no generator H)` appears when no generator carries an inertia constant, as with every
  MATPOWER case except the built-in `case9`.

The `kV` column shows `-` because the `baseKV` column of `case14.m` is 0, so the file states no nominal voltage. The
per-unit results are unaffected.

## The ways in

| You type | What loads | Section |
|---|---|---|
| `gridsim case14`, `gridsim uk` | A built-in or shipped case by name | [Built-in names](#built-in-names) |
| `gridsim case33bw.m`, `gridsim --mpc <file>` | A MATPOWER `.m` file by path or by library name | [MATPOWER files](#matpower-files) |
| `gridsim grid.raw` | A PSS/E RAW file (revision 30, 33, 34 or 35) by path | [PSS/E RAW files](#pss-e-raw-files) |
| `gridsim grid.uct` | A UCTE-DEF file by path | [UCTE-DEF files](#ucte-def-files) |
| `gridsim --dir <folder>`, `gridsim pf <folder>` | A JSON case folder | [JSON case directories](#json-case-directories) |
| `gridsim pf <manifest>.json` | A JSON v2 case manifest | [JSON v2 files](#json-v2-files) |
| `gridsim dist3ph <feeder>.json` | A three-phase feeder file | [International and distribution cases](#international-and-distribution-cases) |

With no case at all, `gridsim` loads the reduced GB model in `data/uk`. Only the form listed for a verb works for that
verb; see [Which verbs take a case or `--dir`](#which-verbs-take-a-case-or---dir).

## Built-in names

Nine standard systems are compiled into `GridSim.Core`, so they load with no file on disk. Their bus, branch and
generator counts are those the loader reports after parsing:

| Name | Case | Buses | Branches | Generators |
|---|---|---:|---:|---:|
| `case9` (or `ieee9`) | WSCC 9-bus | 9 | 9 | 3 |
| `case14` | IEEE 14-bus | 14 | 20 | 5 |
| `case30` | IEEE 30-bus | 30 | 41 | 6 |
| `case57` | IEEE 57-bus | 57 | 80 | 7 |
| `case118` | IEEE 118-bus | 118 | 186 | 54 |
| `case300` | IEEE 300-bus | 300 | 411 | 69 |
| `case145` | IEEE 145-bus (dynamic) | 145 | 453 | 50 |
| `case24` (or `rts`) | IEEE RTS-79, 24-bus (`case24_ieee_rts`) | 24 | 38 | 33 |
| `cigre` | CIGRE MV benchmark (`cigre_mv`), 15-bus radial MV network: a 110 kV source bus and two 20 kV feeders | 15 | 14 | 1 |

`case14`, `case30`, `case57`, `case118`, `case300`, `case145` and `case24` are the stock MATPOWER 8.0 files, embedded
from `data/matpower/lib`. `cigre` is embedded from `data/matpower/benchmarks` and is authored to the CIGRE Task Force C6.04.02 European MV benchmark (the pandapower `create_cigre_network_mv`
reference, base configuration with the three tie switches open): one 110 kV source bus feeding two 20 kV feeders through
two 25 MVA 110/20 kV transformers.

`case9` is the file `data/matpower/lib/case9.m` with one addition: the published WSCC machine data (Anderson and Fouad)
laid over each unit, the machine rating as `MBase` (247.5, 192 and 128 MVA) and the inertia constant `H` on that base
(23.64, 6.40 and 3.01 s). The addition does not enter the power-flow or OPF equations, so `case9` and `case9.m` solve
bit for bit alike; it feeds the frequency, RoCoF and stability screens. Through `MBase` it also sets the machine
reactances that the fault and harmonic studies synthesise, so those two studies differ between `case9` and `case9.m`.
It is the only built-in case with generator
inertia. A MATPOWER file carries no inertia column, so every other built-in case, and every `.m` file you load, has
`H = 0`.

Names are matched without regard to letter case, and so is the `.m` suffix. The shipped GB cases also load by name wherever a verb takes a case:

| Name | Loads | Buses |
|---|---|---:|
| `uk`, `gb`, `gb-reduced` | the reduced GB model in `data/uk` | 20 |
| `gb-full` | the full GB transmission model in `data/gb-full` | 3,539 |
| `gb-synthetic`, `synthetic` | the synthetic GB model, the shipped folder `data/gb-synthetic` | 2,227 |
| `gb-reduced-29` | `data/matpower/gb/GBreducednetwork.m` | 29 |
| `gb-network-2224` | `data/matpower/gb/GBnetwork.m` | 2,224 |
| `pypsa-gb-reduced` | PyPSA-GB's 29-substation network with `GBreducednetwork.m`'s demand and generation | 29 |
| `pypsa-gb-zonal` | refused by every solving verb (a transport model, see [The GB models](#the-gb-models)) | 20 zones |

The bare run `gridsim <name>` is narrower than the verbs. It accepts the nine built-in names and `uk` only; `gb`,
`gb-reduced`, `gb-full`, `gb-synthetic` and the rest are refused there with exit 2 (see [Refusals](#refusals)). Use
`--dir data/gb-full` for the bare run, or give the name to a verb.

## MATPOWER files

A `.m` argument is resolved in this order (`Gated.ResolveMatpower`, `src/GridSim.Cli/Gated.cs`):

1. The path as given, if that file exists. Your own file always wins.
2. A path relative to the shipped `data/matpower` folder, such as `pegase/case1354pegase.m`.
3. A bare file name (no folder part), searched for through every subfolder of `data/matpower`. The matches are sorted
   by relative path, so the result never depends on the file system. Exactly one match loads. Two or more is
   ambiguous: the CLI lists every candidate, writes nothing else, and exits 2.

A name that matches nothing is reported as not found (exit 1). Every shipped `.m` file sits in a subfolder, so the bare
name works for all of them: `case33bw.m` finds `data/matpower/dist/case33bw.m`.

```bash
gridsim case33bw.m --once
gridsim pf pegase/case1354pegase.m
gridsim --mpc path/to/mycase.m --once
```

```
$ gridsim case33bw.m --once
GridSim  -  case33bw
33 buses  32 branches  1 generators  base 10 MVA  50 Hz
========================================================================
Converged on the voltage step (8 sweep iters, 0 OLTC passes, last |dV| 1.23E-009 p.u.; max mismatch 6.82E-011 p.u., which the sweep does not test)
Generator output 0.00 GW  Demand 0.00 GW  Losses 0.2 MW  Inertia not in the data (no generator H)

Bus  Name                            kV    Vm(pu)  Va(deg)    P(MW)   Q(MVAr)
------------------------------------------------------------------------------
  1                                  13   1.000     0.00       3.9       2.4
  2                                  13   0.997     0.01      -0.1      -0.1
...
 33                                  13   0.917     0.38      -0.1      -0.0

Voltage range: 0.913 - 1.000 p.u. (OUT OF BAND)
Total losses: 0.20 MW, 0.14 MVAr
Protection simulation: no relay would operate on this state (generic settings).
note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
note: case33bw.m: skipped 5 out-of-service branch(es).
note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
[exit 0]
```

The `note:` lines are the parse notes. The loader writes one per fact it applied, on stderr, for every verb that loads
a `.m` file; stdout is the verb's own report. The capture shows stdout then stderr. This run loaded the file by its bare
name, converted a raw-unit feeder, and skipped five open tie switches.

A bare name that exists in two subfolders is refused. The next capture adds a second `case33bw.m` under
`data/matpower/lib` of the install folder to show the message; no shipped name is duplicated.

```
$ gridsim case33bw.m --once
GridSim  -  case33bw
33 buses  32 branches  1 generators  base 10 MVA  50 Hz
========================================================================
Converged on the voltage step (8 sweep iters, 0 OLTC passes, last |dV| 1.23E-009 p.u.; max mismatch 6.82E-011 p.u., which the sweep does not test)
Generator output 0.00 GW  Demand 0.00 GW  Losses 0.2 MW  Inertia not in the data (no generator H)

Bus  Name                            kV    Vm(pu)  Va(deg)    P(MW)   Q(MVAr)
------------------------------------------------------------------------------
  1                                  13   1.000     0.00       3.9       2.4
  2                                  13   0.997     0.01      -0.1      -0.1
  3                                  13   0.983     0.10      -0.1      -0.0
  4                                  13   0.975     0.16      -0.1      -0.1
  5                                  13   0.968     0.23      -0.1      -0.0
  6                                  13   0.950     0.13      -0.1      -0.0
  7                                  13   0.946    -0.10      -0.2      -0.1
  8                                  13   0.941    -0.06      -0.2      -0.1
  9                                  13   0.935    -0.13      -0.1      -0.0
 10                                  13   0.929    -0.20      -0.1      -0.0
 11                                  13   0.928    -0.19      -0.0      -0.0
 12                                  13   0.927    -0.18      -0.1      -0.0
 13                                  13   0.921    -0.27      -0.1      -0.0
 14                                  13   0.919    -0.35      -0.1      -0.1
 15                                  13   0.917    -0.38      -0.1      -0.0
 16                                  13   0.916    -0.41      -0.1      -0.0
 17                                  13   0.914    -0.49      -0.1      -0.0
 18                                  13   0.913    -0.50      -0.1      -0.0
 19                                  13   0.997     0.00      -0.1      -0.0
 20                                  13   0.993    -0.06      -0.1      -0.0
 21                                  13   0.992    -0.08      -0.1      -0.0
 22                                  13   0.992    -0.10      -0.1      -0.0
 23                                  13   0.979     0.07      -0.1      -0.0
 24                                  13   0.973    -0.02      -0.4      -0.2
 25                                  13   0.969    -0.07      -0.4      -0.2
 26                                  13   0.948     0.17      -0.1      -0.0
 27                                  13   0.945     0.23      -0.1      -0.0
 28                                  13   0.934     0.31      -0.1      -0.0
 29                                  13   0.926     0.39      -0.1      -0.1
 30                                  13   0.922     0.50      -0.2      -0.6
 31                                  13   0.918     0.41      -0.1      -0.1
 32                                  13   0.917     0.39      -0.2      -0.1
 33                                  13   0.917     0.38      -0.1      -0.0

Voltage range: 0.913 - 1.000 p.u. (OUT OF BAND)
Total losses: 0.20 MW, 0.14 MVAr
Protection simulation: no relay would operate on this state (generic settings).
note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
note: case33bw.m: skipped 5 out-of-service branch(es).
note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
[exit 0]
```

### What the parser reads

- **Matrices.** `mpc.baseMVA`, `mpc.bus`, `mpc.gen`, `mpc.branch`, `mpc.gencost` and `mpc.dcline`. `mpc.bus` is
  required. `%` comments and `...` continuations are stripped, a row ends at `;` or at a newline, and numbers are
  read with the invariant culture.
- **Numbers.** `Inf`, `-Inf` and `NaN` are read, and so is simple arithmetic inside a matrix: `+ - * /`, parentheses,
  a unary sign, `sqrt(...)` and `pi`. The ACTIVSg and `case533mt` files write base voltages as `135/sqrt(3)`. A scalar
  such as `mpc.baseMVA` is evaluated the same way, up to its `;`: `case533mt_hi` writes `mpc.baseMVA = 50/3;`, which
  loads as 16.667 MVA. A scalar the evaluator cannot read completely (trailing text, an unfinished expression) is
  refused with an error naming the field; it is never truncated to its leading number.
- **Names and frequency.** The case name is the file name without its `.m`; the `function mpc = <name>` line is not used. A MATPOWER
  file carries no nominal frequency, so the loader assumes 50 Hz and says so in a note; the IEEE and WSCC systems are
  60 Hz systems, and the header line shows 50 Hz for them. `baseMVA` defaults to 100 when absent.
- **Bus types.** Type 3 is the slack, 2 is PV, 1 (and any other value except 4) is PQ. Type 4 (isolated) buses are
  dropped with every generator and branch incident to them. A case with no type 3 bus is refused. More than one is
  accepted as a multi-island system and noted.
- **Branches.** A branch whose ratio column is non-zero is a transformer with that tap ratio; ratio 0 is a line. A
  branch with status 0 is dropped. Bus ids need not be contiguous or sorted; the model keeps file order.
- **Generators.** A status 0 generator is kept in the model, marked out of service. No solver uses it, but `convert`
  writes it back out. A PV bus whose generators are all out of service is solved as PQ, as MATPOWER does. Of the
  shipped cases only `nesta/pglib_opf_case588_sdet.m` has any. A generator on a dropped or undefined bus is skipped
  and noted.
- **Voltage limits.** The bus VMAX and VMIN columns are read as per-bus limits. The contingency verbs `nminus1`,
  `nminus2` and `n-1-1` use them by default ([ch. 11](<0011 Contingency analysis.md>)): each bus is judged against
  its own limits where the case gives them and against the one operational band otherwise. `--global-vlimits` judges
  every bus against the band. `--bus-vlimits` is accepted and changes nothing.
- **Costs and DC lines.** `mpc.gencost` (polynomial or piecewise linear) is kept on each generator and is used by
  `dcopf` and `acopf` only when asked (`--costs case`). `mpc.dcline` is parsed and carried but not modelled: MATPOWER
  adds DC line flows as injections, GridSim does not, so a case with DC lines solves differently from MATPOWER. The
  loader prints a warning for such a case. A malformed `gencost` (wrong row count, a MODEL other than 1 or 2, an NCOST
  the row cannot hold) or a `dcline` row shorter than 17 columns stops the load naming the row. No shipped case has
  a DC line.
- **Raw units.** A few published feeders give impedance in ohms and demand in kW. When three tests all agree (every
  base voltage at or below 35 kV, total demand more than 30 times the base, and a median branch impedance that is
  implausible as per-unit but sane once divided by the from-bus base impedance), the loader converts the case to per-unit
  and MW and notes it. `case22`, `case33bw`, `case69` and `case85` are converted; every other shipped file is returned
  exactly as written. Every verb loads a `.m` file through this one path.

`gridsim convert` writes a case back out as MATPOWER or JSON and proves the round trip bit for bit
([ch. 19](<0019 CLI reference.md#convert>)).

## PSS/E RAW files

`gridsim <file>.raw` loads a PSS/E power-flow RAW file of revision 30, 33, 34 or 35, and any verb that takes a case
takes a `.raw` path in the same place as a `.m` file (`gridsim nminus1 grid.raw`, `gridsim convert grid.raw grid.m`).
The revision comes from the REV field of the first line, or from `PSS/E-NN` in its comment; a file that gives neither
needs `--psse-revision <n>` (the UKGDS distribution cases are such files, in the revision 30 layout:
`gridsim ehv2.raw --psse-revision 30 --frequency-hz 50`), and a file that gives a revision the reader does not read, or
one that differs from `--psse-revision`, is refused. Every refusal names the line, the section and the field.

Revision 30 differs from 33 in three places, all read: a bus record carries its own shunt (GL, BL, added to the bus
shunt as a fixed shunt is) and there is no fixed shunt section; a switched shunt has no STAT field (it is in service);
and the switched shunts come straight after the VSC DC links. Revisions 31 and 32 are refused: their layouts lie
between 30 and 33, and with no sample file or format reference to check them against, a guessed layout could read one
field as another without any error. The writer (used by `convert` round trips and the test fixtures) writes 33, 34
and 35 only.

### What the reader reads

- **Case identification.** SBASE is the system base. BASFRQ is the nominal frequency; when it is 0 or missing,
  `--frequency-hz` gives it, and otherwise 60 Hz (PSS/E's own default) is assumed and noted. A file that states BASFRQ
  and a different `--frequency-hz` is refused. IC must be 0: a change case is refused. The case name is the file name
  without `.raw`.
- **Buses.** IDE 3 is the slack, 2 PV, 1 PQ; IDE 4 (isolated) buses are dropped with everything connected to them. A
  PV bus with no in-service generator is solved as PQ. NVHI and NVLO, when written, are the bus's own voltage limits.
  Area, zone and owner numbers, and the names from the area, zone and owner data, are kept beside the model.
- **Loads.** Constant power (PL, QL) is the bus demand. Constant admittance (YP, YQ) is a bus shunt, which is exactly a
  constant-admittance load. Constant current (IP, IQ) makes the bus a [ZIP load](#zip-loads). In-service distributed
  generation on a load (revision 34 and 35) is netted off it. Out-of-service loads are skipped.
- **Shunts.** Fixed shunts add to the bus shunt. Switched shunts are fixed at their initial susceptance BINIT, with a
  note, unless `--shunt-control` switches them ([Shunt control](#shunt-control)).
- **Generators.** PG, QG, the Q and P limits, the set point VS and MBASE. Out-of-service units are kept, marked out of
  service. A unit that regulates a remote bus holds its own bus at VS instead, with a note, as MATPOWER does. The
  machine impedances are not used by a power flow.
- **Branches and switching devices.** R, X, B and the first three ratings. The line-end shunts GI, BI, GJ and BJ are
  added to the end buses. A branch or switching device written with R = X = 0 gets X = 0.0001 pu, PSS/E's
  zero-impedance threshold, with a note.
- **Transformers.** A two-winding transformer is one branch with ratio w1 / w2, each winding's voltage taken in per
  unit as CW says (pu, kV, or pu of NOMV), and its impedance moved to the system base as CZ says (system base, winding
  base, or load loss and impedance magnitude). A three-winding transformer is a star-point bus, numbered from the next
  power of ten above the highest bus number, and one branch per winding carrying that winding's ratio, phase shift and
  star impedance. The magnetising admittance is a shunt at winding 1's bus (two windings) or at the star point (three).
  Automatic tap and phase control is not applied unless asked for: the taps stay at the file's ratios, with a note.
  `--tap-control` applies voltage control by tap ([Tap control](#tap-control)).
- **Impedance correction tables.** A winding with a table (TAB) has its impedance (Z1-2 for a two-winding unit, its
  star impedance for a three-winding one) multiplied by the table's factor at the winding's ratio, or at its phase
  shift angle for a phase shifter (|COD| 3): linear interpolation between points, the end factor outside the table (as
  PSS/E does, with a note), a real factor in revision 33 and earlier and a complex one in revisions 34 and 35. With
  `--tap-control` a tap-changing winding's factor is recomputed at every tap the tap loop steps. A table with fewer than
  two points, T not increasing, or a zero factor is refused, naming its line.
- **Two-terminal DC links.** Each in-service line-commutated link (MDC 1 or 2) is solved with the AC power flow by
  PSS/E's two-terminal model: the inverter at its minimum extinction angle with its tap stepped to bring the compounded
  DC voltage nearest VSCHD, the current from the power order (at the rectifier for SETVL above 0, the inverter below)
  or the current order, the rectifier's firing angle and tap within their limits; if the rectifier cannot reach the
  voltage it sits at its minimum angle and the inverter takes the current less DELTI; a power order becomes a current
  order when the compounded voltage is below VCMOD. Each converter draws (rectifier) or delivers (inverter) its DC power
  and absorbs P tan(phi) of reactive power at its AC bus, alternating with the AC solve until both settle. An order no
  angle and tap can meet makes the solve NOT converged, naming the link. The case report prints each link's current,
  voltages, angles, taps and powers. Blocked links (MDC 0) are skipped with a note. Capacitor-commutated links (XCAP
  not 0) are not modelled. A model with links goes to a Newton engine (the backward/forward sweep refuses them), and the
  MATPOWER, JSON and PSS/E writers refuse it rather than write a case without them.
- **VSC DC links.** Each in-service link is solved with the AC power flow by PSS/E's VSC model: one converter holds the
  DC voltage (TYPE 1), the other an active power order (TYPE 2, DCSET MW drawn from its bus; negative sends power to
  it); converter losses max(MINLOSS, ALOSS + BLOSS x Idc) come from the AC side and the DC line loss is R Idc^2. A MODE
  1 converter holds its bus voltage at ACSET within MINQ..MAXQ (a limit reached releases the bus); a MODE 2 converter
  runs at power factor ACSET. When a converter's apparent power exceeds min(SMAX, sqrt(3) V base kV IMAX) its order and
  reactive power are reduced by its weighting PWF (at a fixed power factor both scale together; the DC voltage
  converter's reactive power alone). The case report prints each link's current and both converters' powers, DC
  voltages and losses. A converter regulating a remote bus (REMOT), or holding the voltage of a bus that also has
  generators, is not modelled. Out-of-service links (MDC 0 or a converter of TYPE 0) are skipped with a note.
- **Multi-terminal DC links.** Each in-service link is solved with the AC power flow: its line-commutated converters
  (the two-terminal converter equations) on a resistive DC network (DC line resistances RDC, grounding resistances
  RGRND). The voltage converter (VCONV) runs at its minimum angle with its tap stepped to bring its DC voltage nearest
  its schedule; every other converter holds its power (MDC 1) or current (MDC 2) order, positive for a rectifier and
  negative for an inverter; the voltage converter takes the current that balances the DC network. Each converter's
  angle follows from its DC bus voltage and current, its tap stepped to keep the angle within its limits. The mode
  switch voltage VCMOD, the alternate voltage converter VCONVN, the margin MARG and the participation factor DCPF are
  not applied: a converter that would need them makes the solve NOT converged, naming it. A converter connected to two
  DC buses (IDC2) is not modelled. The case report prints each converter's state.
- **FACTS devices.** Modes 1 to 3 are solved with the AC power flow. The shunt element holds its sending end bus I at
  VSET with its current limited to SHMX (MVA at unity voltage): over the rating it gives the most reactive power the
  rating allows at the solved voltage and the bus is released. With no terminal end bus J (or J 0) the device is a
  STATCOM. MODE 1 with J is a UPFC, modelled as its injections: PDES + j QDES delivered to bus J and PDES taken from bus
  I (lossless: the shunt converter supplies the series converter's active power), the series voltage V_J - V_I and the
  series current from S_J / V_J; after the solve the terminal end bus must be inside VTMN..VTMX and the series voltage,
  series current and bridge power within VSMX, IMX (when above 0) and TRMX, or the solve is NOT converged, naming the
  device and the limit (PSS/E would change the device's mode). MODE 2 with J bypasses the series element (a branch of
  0.0001 pu between I and J) and MODE 3 makes it a branch of SET1 + j SET2 pu; both keep the shunt element. A shunt
  element's bus must have no generator or voltage-controlling VSC converter and must not be the slack bus, or the solve
  is refused naming the device. Several FACTS shunt elements and static var compensators may hold one bus together
  (see "Shared buses" under [Shunt control](#shunt-control)). The case
  report prints each device's shunt and series quantities. Out-of-service devices (MODE 0) are skipped. A UPFC needs
  its shunt element (SHMX above 0); MODE 3 needs J; both are refused otherwise, naming the line.
- **Induction machines.** Each in-service machine of custom design (DCODE 0) with a single rotor cage is solved with
  the AC power flow from its equivalent circuit (pu on MBASE and RATEKV, RATEKV 0 meaning the bus base): stator RA +
  j XA, magnetising j XM, rotor R1 / s + j X1. The slip is the one on the stable branch (between synchronous speed and
  peak power) that meets PSET: shaft power out for PCODE 1, electrical power in for PCODE 2, positive for a motor and
  negative for a generator. The machine draws |V|^2 / conj(Zin) at its bus, re-taken at each solved voltage until it
  settles. A schedule beyond the machine's peak power at the solved voltage makes the solve NOT converged, naming the
  machine. The torque data (TCODE, H, A to E) belong to dynamic studies. The case report prints each machine's slip
  and powers. Out-of-service machines are skipped.
- **Devices GridSim does not model.** A file holding an in-service capacitor-commutated two-terminal link, a VSC
  converter regulating a remote bus, a multi-terminal converter on two DC buses, a FACTS device in modes 4 to 8
  (constant series voltage and interline power flow control) or regulating a remote bus, or an induction machine with
  a standard design code (DCODE 1 to 5, whose circuit PSS/E takes from design tables), a double cage or magnetic
  saturation is refused, because its power flow would differ from the file's. `--allow-unmodelled` reads such a file: the devices are read past and counted in a
  note. GNE devices (user-written models) are always refused. Revision 35's system-wide data and node-breaker substation
  data are read past; the bus-branch records are complete without them.

### Tap control

`--tap-control` applies the file's automatic voltage control by tap (COD 1). Without it nothing changes: every tap stays
at the file's ratio, as above.

- **Tap changers.** Each in-service winding with COD 1 becomes an on-load tap changer on its branch, which the
  steady-state solve's tap loop steps one position per pass until the controlled bus is in its band. The controlled bus
  is CONT; the band is VMI..VMA (target the middle, deadband half the width); the ratio limits are RMI..RMA, in the
  units of WINDV as CW says; the step is (RMA - RMI) / (NTP - 1). A two-winding unit's limits and step are divided by
  its winding 2 ratio, because its branch tap is w1 / w2. The taps start from the file's ratios. A tap changer whose
  controlled bus a generator holds (a PV bus) keeps its tap, as every GridSim tap changer does.
- **Names.** A tap changer finds its transformer by name, so a controlled transformer with no name, or with a name
  another branch shares (parallel units often do), is renamed `NAME (I-J CKT)` or `transformer I-J CKT`, with a note.
- **Ramp start.** A heavily loaded network can be past its voltage collapse point at the file's taps: Newton then has
  nothing to converge to, although the regulated network (taps moved) solves normally. When the solve from the file's
  taps does not converge, the case is solved at 10 %, 20 %, ... 90 % of its demand and generation first, the taps
  settling at each stage, and then at full load from those taps. The report says so on a `Ramp start:` line. A case that
  converges from the file's taps never ramps. If a stage does not converge, the result is the solve from the file's
  taps, and the line names the stage.
- **Flow control (COD 2 and 3).** A winding that regulates the reactive power flowing through it (COD 2) is stepped by
  tap, and one that regulates the active power (COD 3, a phase shifter) by phase shift angle, one position per pass in
  the same tap loop, until the flow into the transformer at the winding's bus is inside VMI..VMA (Mvar for COD 2, MW for
  COD 3). The limits are RMI..RMA (a ratio in the units of WINDV for COD 2, divided by winding 2's ratio for a
  two-winding unit as above; degrees for COD 3) and the step (RMA - RMI) / (NTP - 1). Raising the tap lowers the
  reactive power into the transformer; raising the angle lowers the active power. A control that runs out of range with
  its flow outside the band is named on the `Taps NOT SETTLED` line. An impedance correction table follows the ratio, or
  the angle for COD 3. Fewer than 2 positions, RMA not above RMI (or RMI not above 0 for a ratio) and VMA not above VMI
  are refused, naming the line.
- **Not applied, noted.** DC line (COD 4) and asymmetric (COD 5) control, and a COD 1 winding with CONT 0 or a
  controlled bus that was dropped as isolated.
- **Refused.** A negative CONT naming a bus other than the winding's own (a remote bus PSS/E takes as on the winding 1
  side; the tap loop steps a remote bus as one on the far side), a CONT naming a bus the file does not define, fewer
  than 2 tap positions, RMA not above RMI, and an empty voltage band. Each refusal names the line.

With `--tap-control` all 12 UKGDS cases solve (`gridsim ehv2.raw --psse-revision 30 --frequency-hz 50 --tap-control`).
EHV2, 3, 4 and 6 have no solution at the file's taps and solve from the ramp start; in EHV2 the two 132/66 kV units
(102-201, 103-201) end at the bottom of their range with their 66 kV bus still below its band, and the report names
them on its `Taps NOT SETTLED` line. The first ramp stage starts each bus angle from the transformer phase shifts on
the way to it from the slack (as PSS/E's flat start does): the UKGDS EHV cases put 30 degree vector-group shifts on
their 132/33 and 33/11 kV units, and from flat 0 degree angles Newton does not converge at any load.

### Shunt control

`--shunt-control` switches the file's switched shunts that have discrete local voltage control (MODSW 1) and makes
those with continuous local voltage control (MODSW 2) static var compensators. Without it every switched shunt stays
fixed at BINIT, as above.

- **Banks.** A switched shunt switches its blocks (reactor blocks negative, capacitor blocks positive; a revision 35
  block with status 0 is left out) one at a time: equal blocks by one block per step, unequal blocks one block at a time
  in the order they are listed (PSS/E's order), so each step adds the next block. BINIT must be one of the levels the
  blocks reach (a whole number of equal blocks). It regulates its own bus, or the remote bus SWREM (SWREG in revision
  35) the same way: switching in raises the regulated bus's voltage. It switches one block per pass, more
  capacitive when its bus is below VSWLO and less when above VSWHI, until the bus is in its band or the blocks run out
  (the `pf --switched-shunts` loop, which freezes a bank that hunts). BINIT stays in the bus shunt and the blocks count
  from it, so the case without the switching is the fixed-at-BINIT case.
- **Where.** The case solve runs the switching inside the tap loop (with `--tap-control`, taps and shunts together) and
  reports `Switched shunts: ... settled.` or `NOT SETTLED: ...`; `pf` switches them as it switches a `--switched-shunts`
  file's banks (giving both is refused, exit 2).
- **Static var compensators (MODSW 2).** The susceptance varies continuously from every reactor block in to every
  capacitor block in (block sizes may differ). With VSWHI equal to VSWLO it holds that voltage. Otherwise it stays at
  BINIT while its bus is inside VSWLO..VSWHI and holds the band edge its bus crossed: the continuous counterpart of the
  block switching above (PSS/E's manual does not spell out the band rule for this mode, so this is GridSim's reading).
  When holding the voltage would need more than its range, it is fixed at the end of its range and its bus released.
  The solve alternates with the AC solve (a Newton engine; the sweep refuses it) and the case report prints each
  compensator's susceptance, reactive power and state. Its bus must have no generator or voltage-controlling VSC
  converter and must not be the slack bus; it may share its bus with other compensators and FACTS shunt elements
  (below).
- **Shared buses (RMPCT).** When several FACTS shunt elements and static var
  compensators hold one bus, the bus is held at their common set-point and the reactive power their sources supply is
  shared by RMPCT, the PSS/E field defined as the percentage "of the total Mvar required to hold the voltage" that the
  device contributes: each device still holding the bus takes its RMPCT over the sum of their RMPCTs. A device whose
  share would pass its own limit (a shunt element's current rating, a compensator's susceptance range) is fixed at
  that limit and the others share the rest. Every device on a shared bus needs an RMPCT above 0 (a device alone ignores
  it). Two different VSETs or fixed set-points on one bus are refused naming both devices; a compensator that starts
  holding a band edge another device does not hold makes the solve NOT converged, naming both, because one bus cannot be
  held at two voltages. A bus held by a generator or a VSC converter as well is still refused.
- **Not switched, noted.** Other control modes (MODSW 3 to 6), a remote regulated bus that a generator holds (a swing
  bus, or a PV bus with an in-service unit: switching cannot move it) or that was dropped as isolated, a remote bus for a
  static var compensator, a BINIT that is none of the block levels (MODSW 1) or lies outside their range, and a shunt
  with no blocks. MODSW 0 (locked) is fixed without a note. A remote bus the file does not define is refused, naming the
  line.
- **Refused.** For MODSW 1, VSWHI not above VSWLO; for MODSW 2, VSWHI below VSWLO; VSWLO not above 0; a negative block
  count. Each refusal names the line.

### Sequence data (SEQ)

A PSS/E sequence data file (`.seq`, revision 30 layout) gives the fault studies (`fault`, `iec60909`) their sequence
data: `--seq-data grid.seq` applies it to the `.raw` case the run loads. The file states its revision 30 or none; with
none, the `.raw` case must be revision 30 (later revisions add transformer fields this layout would misread).

- **Machines.** ZXPOS is the machine's subtransient reactance X''d and ZXNEG its negative-sequence reactance X2, both on
  its MBASE and above 0. The resistances are not used (GridSim's machine fault source is a reactance), with a note. A
  zero-sequence record RZERO + jXZERO (on MBASE, any neutral grounding impedance included) is the machine's impedance to
  earth at its bus, a grounded neutral, which the zero-sequence network stamps as a shunt (IEC 60909's K_G correction
  and Method C's frequency scaling apply to it as to X''d); an all-zero record means no path at the machine.
- **Lines.** RLINZ and XLINZ are the line's zero-sequence impedance. A zero-sequence impedance of 0 gets X0 = 0.0001
  pu (in GridSim a zero Z0 means "not supplied"). Zero-sequence charging and line-end shunts are neglected, as IEC
  60909-0 neglects line capacitances, with a note.
- **Two-winding transformers.** Connection code CC 1 is grounded wye on both windings (a series path), CC 2 grounded
  wye on winding 1 and delta on winding 2 (a path to earth at winding 1's bus), CC 3 the reverse, CC 4 delta on both
  (no path). The neutral grounding impedance RG + jXG of CC 2 and 3 appears three times in the path to earth and is
  folded into the transformer's zero-sequence impedance, Z0 = (R1 + 3 RG) + j(X1 + 3 XG), with a note.
- **Mutual coupling.** A zero-sequence mutual impedance RM + jXM over the whole of both lines (BIJ1 = BKL1 = 0, BIJ2 =
  BKL2 = 1) couples the two lines: each group of mutually coupled lines is stamped from the inverse of its impedance
  matrix (the lines' own Z0 on the diagonal, the mutual impedances off it), the sign following the record's line
  directions, and the lines' zero-sequence currents in the fault results include the coupling.
- **Matching.** Records match the `.raw` case's machines by bus and ID and its branches and transformers by buses (in
  either order) and circuit. A record for an element that is out of service or on a dropped isolated bus is skipped and
  counted; one the `.raw` file does not hold, or a second record for one element in a section, is refused.
- **Refused.** A change case (IC not 0), a revision other than 30, a three-winding transformer's zero-sequence record,
  CC 5 to 9, CC 1 with a grounding impedance, a zero-sequence path of zero impedance, and a record after the last
  section. The three-winding and CC 5 to 9 connection codes stay refused because no definition of their zero-sequence
  networks was available to check them against: a guessed mapping could connect the wrong windings to earth without
  any error. Data GridSim's sequence networks have no element for (a negative- or zero-sequence shunt load, a mutual
  coupling over part of a line, a zero-sequence switched shunt block) is refused unless `--allow-unmodelled` is given;
  with it each kind is counted in a note and the unbalanced fault currents differ from PSS/E's.
- **Left as GridSim's default**, each counted in a note: a machine with no record keeps the synthesised X''d, a line
  with no record 3 x Z1, a transformer with no record is treated as a line in the zero sequence.

### Dynamic data (DYR)

A PSS/E dynamic data file (`.dyr`) is read with the `.raw` case it belongs to: `--dyn-data grid.dyr` on a verb that
takes `--dyn-data` (`stability --rms`, `trip`, `transient`, `modal`) maps its records onto the case's machines by bus
and machine ID, and the result is checked and bound exactly as a `gridsim-dynamics/2` file is. GENROU and GENSAL map to
the subtransient machine, SEXS, IEEET1, EXAC1 and ESST4B to the exciters, PSS1A to the stabiliser, TGOV1, IEEEG1, HYGOV and GAST to the
governors; each value is tagged with the file and line it came from. Where GridSim needs a value PSS/E gives in another
form, it is derived exactly and tagged as derived: the SEXS lead time TA = (TA/TB) x TB, the IEEEG1 droop R = 1 / K,
and for GENSAL, which has no q-axis transient circuit, X'q = Xq and T'qo = T''qo (that circuit then carries nothing). An EXAC1 record maps value by value (TR must be 0); ESAC1A is read only from a `gridsim-dynamics/2` file, because its PSS/E CON order is not in public text, so an ESAC1A record is a model this reader does not map.

Renewable units: REGCA1 (REGC_A) with REECA1, REECB1 or REECCU1 (REEC_A, REEC_B, REEC_C), and optionally the plant
controller REPCA1 (REPC_A) and the wind turbine models WTDTA1, WTARA1, WTPTA1 and WTTQA1 (WTGT_A, WTGAR_A, WTGPT_A,
WTGTRQ_A: the drive train alone for a type 4 turbine, all four with REECA1 PFLAG = 1 for a type 3), gives the unit a
WECC second-generation model chain for `stability --rms` (technical ch. 24). A distributed resource is a `'DER_A'`
record: the 48 values of the WECC DER_A specification's Table 1, in that order (PSS/E's DERA1 / DERAU1 are refused by
name: their record order is not public). In the same way the voltage-source generator/converter is a `'REGC_B'` record,
its electrical control a `'REEC_D'` record and the droop grid-forming inverter a `'REGFM_A1'` record, each in the order
of its public specification table (PSS/E's REGCB1, REECDU1 and REGFMA1 names are refused; chapter 15 lists the orders).
REPCA1's regulated bus and its flow branch (BUS1, BUS2 and the circuit identifier) must be the `.raw` case's. The PSS/E V33 user-model names (REGCAU1,
REECAU1, ...), the WECC names (REGC_A, REEC_A, ...) and a USRMDL record of these models are read the same way. The unit
becomes converter plant in the RMS study even if the `.raw` file types it as a machine. Every value is range-checked;
REGC_A's Accel is accepted and a note says it has no effect (it is a PSS/E solution acceleration factor). A file may
give only renewable units; the machines then keep the default data.

A file is refused, with every item listed, when it holds a model this reader does not map (each with its count and
first line), renewable data with a value out of range, a unit with both a machine record and a renewable chain, an
electrical control (REEC) with no REGC_A for its unit, an exciter, stabiliser or governor on a renewable unit, a controller on a
unit with no GENROU or GENSAL record, or a value a GridSim model has no block for: an IEEET1 transducer lag TR, SWITCH,
KE = 0 or exciter saturation; a PSS1A input other than speed, a remote bus, the A1 / A2 filter, T5 not equal to T6, or
the VCU / VCL cut-offs; an IEEEG1 second shaft; a HYGOV filter, gate rate limit, turbine gain other than 1, turbine
damping or no-load flow; GENSAL saturation. `--allow-unmodelled` leaves those out, each listed in a `note:` line, and
uses the rest. A record for a machine the `.raw` file does not hold, a second record of the same kind for one unit, a
record with the wrong number of values, or one not ended by a slash, is always refused, naming its line.

The model's branches are the lines, then the switching devices, then the two-winding and the three-winding
transformers, each in file order. A case written as RAW by GridSim and read back gives the same admittance matrix and
power flow as the model written, bit for bit, with the branches in that order; the IEEE 14, 39 and 118-bus cases are
checked this way against the MATPOWER files.

## UCTE-DEF files

`gridsim <file>.uct` loads a UCTE-DEF file (the UCTE data exchange format for load flow, version 02), and any verb that
takes a case takes a `.uct` path in the same place as a `.m` file. The file is read by its fixed columns; a field that
should hold a number and holds anything else, a node or element defined twice, an element naming a node the file does
not define, and blocks out of the order ##C, ##N, ##L, ##T, ##R, ##TT, ##E are refused, naming the line, the block and
the field.

- **Per unit.** System base 100 MVA. A node's base voltage is its nominal voltage from the 7th character of its code
  (0: 750, 1: 380, 2: 220, 3: 150, 4: 120, 5: 110, 6: 70, 7: 27, 8: 330, 9: 500 kV; any other character is refused).
  UCTE-DEF carries no system frequency: `--frequency-hz` gives it, otherwise 50 Hz is assumed and noted.
- **Nodes.** Type 3 is the slack, 2 PV, 0 PQ; type 1 (Q and angle held) has no GridSim equivalent and is refused. A
  PV or slack node holds its voltage reference. Generation is written with the load sign (production negative), so the
  model's output and limits are its negatives, as PowSyBl's UCTE importer reads them. X-nodes (boundary nodes) are
  buses with their written load and generation as fixed injections; equivalent nodes are ordinary nodes; both noted.
- **Lines** join nodes of one nominal voltage (otherwise refused): R and X in ohm and B in microsiemens become per unit
  of the nodes' base, the current limit a rating of sqrt(3) x U x I. A busbar coupler in operation, or a line written
  with R = X = 0, is a branch of X = 0.0001 pu. Elements out of operation are skipped.
- **Transformers.** R, X, G and B are referred to the non-regulated winding (node 1), as PowSyBl's importer reads
  them. The branch runs from the regulated winding (node 2, which carries the tap) to node 1, with the impedance in
  per unit of node 1's base and the ratio (U2 (1 + n' du / 100) / Ub2) / (U1 / Ub1). Angle regulation (##R) gives
  the ratio's size and the phase shift from its step, asymmetric or symmetric. G and B are a shunt at node 1. A
  transformer with both kinds of regulation off neutral is refused; voltage and flow targets are not applied, the taps
  stay at n' (noted). Special transformer tables (##TT) for a transformer in operation are refused unless
  `--allow-unmodelled` is given, which uses the transformer's own impedance instead.
- **##E** exchange schedules are read past (scheduling data, not network), with a note.

## JSON case directories

A JSON case is a folder of human-editable files, the format the GB models ship in and the format `gridsim gda --out`
and `gridsim synth` write. Load one with `--dir`, or as the positional case of a verb that takes one:

```bash
gridsim --dir data/gb-synthetic --once
gridsim pf data/gb-synthetic
```

```
$ gridsim --dir data/gb-synthetic --once
GridSim  -  GB synthetic
2227 buses  2268 branches  74 generators  base 100 MVA  50 Hz
========================================================================
Converged (5 NR iters, 11 OLTC passes, max mismatch 3.27E-010 p.u.)
Generator output 40.12 GW  Demand 39.29 GW  Losses 831.1 MW  Inertia 171.7 GVAs

Bus  Name                            kV    Vm(pu)  Va(deg)    P(MW)   Q(MVAr)
------------------------------------------------------------------------------
  1  North Scotland A 400           400   1.040    19.23     650.0      24.2
  2  North Scotland B 400           400   1.040    21.04    1500.0     -46.5
  3  South Scotland A 400           400   1.040    13.45     975.0     534.6
...
2227  SWW G3P3S4 11                   11   1.005    -2.52     -27.8      -7.0

Most-loaded branches:
  MID SGT 400/275 1               1465 MW   160.5%
  LDN SGT 400/275 1               1507 MW   157.7%
  MID SGT 400/275 2               1019 MW   108.5%
  YRK SGT 400/275 1                994 MW   105.8%
  LDN SGT 400/275 2                983 MW   104.2%

Voltage range: 0.950 - 1.040 p.u. (in band)
Total losses: 831.09 MW, 1955.72 MVAr
Protection simulation: 30 event(s) suggested  -  e.g. on MID SGT 400/275 1, relay 51 (Time overcurrent) would likely operate (MID SGT 400/275 1 current 1.54x rated over the 1.20x pickup; IEC standard-inverse trips in 2.8 s). Run `protection-check` for the full list.
[exit 0]
```

This run solved 2,227 buses with 11 OLTC passes. The `11 OLTC passes` figure is the number of times the tap-changer
loop re-solved the network ([ch. 5](<0005 Running power flows.md>)).

### The files in a folder

Every file is optional except `buses.json`. A folder given positionally to a verb also needs a `system.json`; `--dir`
does not. A folder with no `buses.json`, or an empty one, is refused. A case needs a
slack, which comes from a generator with role `slack` or from a bus with type `slack`.

| File | Contents |
|---|---|
| `system.json` | Name, `baseMva` (default 100), `frequencyHz` (default 50), and `dispatch`. A folder that holds a `system.json` is recognised as a case by the verbs that take a folder positionally. |
| `buses.json` | Per bus: `id`, `name`, `baseKv` (required), `type` (`pq`, `pv` or `slack`, in any letter case, with `gen` for `pv` and `ref` or `swing` for `slack`; default `pq`; any other word is refused), `vm`, `vaDeg`, shunts `gs` and `bs`, layout coordinates `x` and `y` (0 to 1), optional `zip`, optional `vminPu` and `vmaxPu`. |
| `generation.json` | Per generator: `name`, `bus`, `type` (default `ccgt`), `capacityMva`, `outputMw` or `capacityFactor`, `voltageSetpoint` (default 1.0), `qMinMvar`, `qMaxMvar`, `pMinMw`, `pMaxMw`, `qOutputMvar`, `inertiaH`, `role` (`pv`, `pq` or `slack`; default `pv`), `inService` (default true), plus converter, fault and dynamics fields. |
| `demand.json` | Per demand point: `name`, `bus`, `pMw`, and `qMvar` or `powerFactor` (default 0.97 lagging). Rows on one bus are summed onto it. |
| `transmission.json` | Lines: `name`, `from`, `to`, `rPu`, `xPu`, `bPu`, `ratingMva`, `circuits` (default 1), optional `ratingBMva` and `ratingCMva`, `tapRatio`, `phaseShiftDeg`, `isTransformer`. |
| `transformer.json` | Transformers: `name`, `from`, `to`, `rPu`, `xPu`, `tapRatio`, `phaseShiftDeg`, `ratingMva`, optional `ratingBMva` and `ratingCMva`. |
| `onloadtapchanger.json` | OLTCs: the `transformer` name, the `controlsBus`, `targetVpu` (1.0), `minTap` (0.9), `maxTap` (1.1), `stepPu` (0.0125), `deadbandPu` (0.01). |
| `boundaries.json` | Named transmission-boundary cuts: `name`, `limitMw`, and the `branches` that make the cut, each with a `direction` (1 or -1 by convention; the loader does not check it). |
| `asset-classes.json` | Authored per-asset dynamics and protection classes, each parameter provenance-tagged. |
| `asset-bindings.json` | Rules (`matchType`, `class`) that bind generators, lines and transformers to those classes. Needs `asset-classes.json`. |
| `protections.json` | Protection relay instances (`name`, `class`, `monitorBus`, `capacityMw`, `targetBranch`). Needs `asset-classes.json`. |

A circuit count above 1 on a line divides `rPu` and `xPu` by the count, multiplies `bPu` by it, and multiplies every
rating by it. Everything else in a folder (`diagnostics.json`, `MODEL_CARD.md`, `ATTRIBUTION.txt`, `README.md`) is
documentation and is not read as part of the case.

Keys are case-insensitive and `//` comments and trailing commas are allowed. An unknown or misspelt key is an error:
the load stops with the file and the key rather than using a default for the field you meant.

```bash
gridsim --dir docs/_captures/networks/inputs/misspelt-key --once
```

```
$ gridsim --dir docs/_captures/networks/inputs/misspelt-key --once
error: cannot load case folder 'docs/_captures/networks/inputs/misspelt-key': transmission.json: The JSON property 'rating_MVA' could not be mapped to any .NET member contained in type 'GridSim.Core.IO.Json.LineDto'.
[exit 1]
```

### Rules the loader enforces

- **`baseKv` on every bus.** A bus without it stops the load and names every such bus.
- **A slack per island.** At least one slack bus or slack generator, and one in every island (a set of buses joined by
  in-service branches); several are allowed. An island with no reference of its own is refused, with its size and its
  lowest-id buses named, so give every island its own slack.
- **Known buses.** A generator or demand row on an unknown bus stops the load, and so does a branch with an end that
  is not a bus. A protection instance must monitor a bus that exists and target a branch that exists.
- **Generator roles and limits.** A role other than `pv`, `pq` or `slack` stops the load. A `null` role or type is
  refused. `qMinMvar`, `qMaxMvar` and `pMaxMw` may be a number or the strings `"-Infinity"` or `"Infinity"` for an
  unlimited limit; no other string is accepted, and an impossible one (`qMinMvar` of `"Infinity"`, say) stops the load.
- **Out-of-service units.** A generator with `"inService": false` is kept but never solved. It does not make its bus
  PV or slack, and the dispatch balance ignores it.
- **Boundaries.** Every branch name in `boundaries.json` must match exactly one branch. A name that matches none, or
  several, stops the load and is listed.
- **Per-bus voltage limits.** `vminPu` and `vmaxPu` must be finite and above 0, with `vminPu` not above `vmaxPu`
  (equal is a fixed-voltage bus). The contingency verbs use them by default; `--global-vlimits` ignores them.
- **Dispatch.** Outputs are balanced on load by merit order with the slack as the marginal unit, so the model arrives
  ready to solve. `"dispatch": false` in `system.json` keeps the outputs as written, which is how `convert` writes a
  solved operating point.

```
$ gridsim --dir docs/_captures/networks/inputs/no-basekv --once
error: cannot load case folder 'docs/_captures/networks/inputs/no-basekv': buses.json: 2 bus(es) have no baseKv, which is required (the base voltage in kV; it no longer defaults to 400 kV): 1 ('North'), 2 ('South').
[exit 1]
```

```
$ gridsim --dir docs/_captures/networks/inputs/no-slack --once
error: cannot load case folder 'docs/_captures/networks/inputs/no-slack': No slack bus. Mark a generator role "slack" or a bus type "slack".
[exit 1]
```

### ZIP loads

A bus may carry a voltage-dependent load model for the demand aggregated onto it:

```json
{ "id": 12, "baseKv": 132,
  "zip": { "ap": 0.4, "bp": 0.3, "cp": 0.3, "aq": 0.6, "bq": 0.2, "cq": 0.2 } }
```

The bus then draws `P(V) = Pd * (ap*V^2 + bp*V + cp)` and `Q(V) = Qd * (aq*V^2 + bq*V + cq)`, V in per-unit. `a` is the
constant-impedance share, `b` the constant-current share and `c` the constant-power share. All six keys are required,
every value must be finite, and each of the P and Q triples must sum to 1 within `1e-9`, so the bus draws its stated
`Pd` and `Qd` at 1.0 per-unit; anything else stops the load naming `buses.json` and the bus. With no `zip` (or
`"zip": null`) the load is constant power. The keys are the ones the solve export (`--enableExport`) writes under
`model.buses[].zip`. The Newton and sweep solves, the reactive-limit check, the physics-law audit, the feasibility
check and power-flow tracing all take the load at the solved voltage from one shared rule. No shipped case sets a ZIP
model.

### OLTCs, boundaries and asset classes

An OLTC names a transformer by its `name` in `transformer.json` (or an off-nominal branch in `transmission.json`) and
the bus whose voltage it regulates. The default report solves with the OLTC loop on, which is the `OLTC passes` count in
the convergence line.

A boundary is a named cut across the network with a transfer limit in MW. The report prints a boundary table for a
case that defines boundaries: net flow, limit and loading for each, with `OVERLOAD` appended above 100 %.

An asset class holds the dynamics or protection parameters of a kind of plant: a `kind`
(`gridFollowingInverter`, `gridFormingInverter`, `transformer`, `line` or `protection`), a `mode` label, numeric
`params`, and a `provenance` entry per parameter (or a `_default`). The loader enforces these rules at load. An unknown
kind is refused, and so is `synchronousMachine`, for which no controller exists. A parameter key that no consumer of that
kind reads is refused. A `CuratedPlaceholder`
provenance must carry the literal token `[VERIFY]` in its note. A binding rule matches a generator, line or
transformer `type` exactly or by a prefix ending in `*` (an exact match wins; letter case is ignored), and a binding to an unknown class is
refused. A generator may also name its class directly with `dynamicsClass`, which beats a binding rule. A protection
instance must name a class of kind `protection`, and its monitored bus and target branch must exist.

### Writing a case folder

`gridsim synth` writes the synthetic GB case to a folder, `gridsim gda --out` writes a materialised GDA case, and
`gridsim convert <case> <folder>` writes any loaded case as a JSON folder ([ch. 19](<0019 CLI reference.md#convert>)). The
case editor ([ch. 33](<0033 The case editor.md>)) edits a folder in place. A v1 folder written by `convert` or
`gda --out` does not carry the authored files `asset-classes.json`, `asset-bindings.json` and `protections.json`; copy
them across by hand. A JSON v2 case written by `convert` (an output ending in `.json`) does carry them: each asset class
as a case-local template, the binding rules, each element's class and the placed protective devices
([JSON v2 files](#json-v2-files)).

## The GB models

GridSim ships three GB models of its own, one alias, and four published reference networks.

| Name | Folder or file | Buses | What it is |
|---|---|---:|---|
| `uk` (also `gb`, `gb-reduced`) | `data/uk` | 20 | The reduced 10-zone GB model: ten 400 kV zone buses and ten 132 kV buses, 29 generators, 10 OLTCs and four boundaries (B4 Upper North, B6 SCOTEX, B7/B8 North-Midlands, SW Peninsula and S Wales). The repository states no external source for it; base 10,000 MVA. It is the default network of the GB verbs. |
| `gb-synthetic` (also `synthetic`) | `data/gb-synthetic` | 2,227 | A procedurally generated GB-shaped network built from no licence-restricted data: ten demand zones (NSC, SSC, NEN, YRK, MER, MID, EAN, LDN, STH, SWW) down the 400, 275, 132, 33, 11 and 6.6 kV ladder, from a seeded generator (seed 20260605, scale 1.0). The name loads the shipped folder on every verb, with its authored asset classes and protections. |
| `gb-full` | `data/gb-full` | 3,539 | The full GB transmission model, assembled from the NESO transmission spine, the DNO LTDS tables and the NESO registers plus synthesised elements, and committed as a pinned reference: 3,298 lines, 2,620 transformers, 5,318 generation rows, 1,958 demand rows, 1,383 OLTCs and five boundaries. |
| `gb-reduced` | alias of `uk` | 20 | No `data/gb-reduced` folder exists; the name loads `data/uk`. |
| `gb-reduced-29` | `data/matpower/gb/GBreducednetwork.m` | 29 | The Strathclyde reduced GB model (data by M. Belivanis, University of Strathclyde; MATPOWER file by W. A. Bukhsh), as carried by PyPSA-GB. 99 branches, 66 generators. |
| `gb-network-2224` | `data/matpower/gb/GBnetwork.m` | 2,224 | The Edinburgh power-systems archive "GB Network" (Bukhsh and McKinnon). 3,207 branches, 394 generators. |
| `pypsa-gb-reduced` | `data/matpower/gb/pypsa-gb/reduced` | 29 | PyPSA-GB's own 29-substation network from its CSV files, with demand and generation taken from `GBreducednetwork.m`. Its import notes are printed on stderr. |
| `pypsa-gb-zonal` | `data/matpower/gb/pypsa-gb/zonal` | 20 zones | PyPSA-GB's zonal model: 20 zones and 37 links with MW capacities and no impedance. It has no power flow, so every solving verb refuses it. `gridsim zonal pypsa-gb-zonal` shows it. |

Provenance and terms:

- `gb-synthetic` is generated, and `gridsim synth --out data/gb-synthetic` regenerates it (with no `--out`, `synth` writes
  `results/synth/gb-synthetic`). Folder and builder are the same network: every bus,
  demand, generator and branch field is equal, and only the branch order differs because the JSON loader reads lines
  before transformers. A study on it should read its N-1 results with the design in mind: each Scottish zone joins the
  rest of GB by one 400 kV circuit, and 2,169 of its 2,268 branches are bridges, the radial 132, 33 and 11 kV layers
  under each zone.
- `gb-full` is a data model, not a measured system. Its README records what is known to be wrong or unverified in the
  data: 142 open line ends at 66 kV and above, 1,207 synthesised supergrid transformers with zero resistance, and a
  possible double count of supergrid transformer capacity at seven GSP groups. These are properties of the GDA export,
  not of the engine.
- The two Edinburgh-archive files state no licence beyond "research and educational purposes". They ship unaltered
  with their attribution in `data/matpower/gb/README.md` and the repository's `THIRD-PARTY-NOTICES.md`. Cite W. A.
  Bukhsh and K. McKinnon, "Network data of real transmission networks", April 2013, for both files, and M. Belivanis,
  University of Strathclyde, for `GBreducednetwork.m`. PyPSA-GB is released under the MIT licence (`LICENSE-PyPSA-GB.txt`); cite A. Lyden
  et al., "PyPSA-GB", Energy Strategy Reviews, 2024.
- The generation in `GBreducednetwork.m` is an OPF starting point, 75,012 MW against 56,326 MW of demand, so a power
  flow of the file as shipped makes the slack absorb the surplus. `GBnetwork.m` is a balanced snapshot, but its stored
  voltages are not a solution of its own set-points; a power flow holds the generator set-points and differs from the
  stored voltages by up to 0.0049 per-unit at PV buses.

The NESO PowerFactory models (the GB Reduced Model and the GB 36-bus model) are not bundled and cannot be loaded; there
is no importer.

### Real GB networks materialised from GDA

The real GB distribution networks are assembled from the GDA data lake outside the repository: apart from the committed
`data/gb-full`, the repository carries the schema mapping and never the data. `gridsim gda --root /gda --out <folder>`
materialises a footprint as a JSON case folder in a git-ignored directory (conventionally `gda-out/`). A materialised
case is an ordinary case folder from then on: load it with `--dir`, or pass the folder positionally to the verbs that
take one. A materialised network is a user network for licensing, so it counts against the tier's bus limit
([Licence tiers](#licence-tiers-and-bundled-folders)). See [ch. 24](<0024 The GDA bridge.md>) and
[ch. 19](<0019 CLI reference.md#gda>).

## International and distribution cases

| Folder | Cases | Buses | Source and licence of the data |
|---|---|---:|---|
| `data/intl/ireland-all-island` | Ireland (EirGrid and SONI) | 60 | ENTSO-E transmission map via GridKit and PyPSA-Eur. Geography (c) OpenStreetMap contributors, ODbL; packaging PyPSA-Eur, CC-BY-4.0. |
| `data/intl/iberia` | Spain and Portugal | 1,497 | As above. |
| `data/intl/france` | France (RTE) | 1,976 | As above. |
| `data/intl/continental-europe` | The continental European synchronous area | 5,824 | As above. |
| `data/dist3ph` | `ieee4-gyGy-stepdown-balanced`, `ieee4-gyGy-stepdown-unbalanced`, `ieee4-dGy-stepdown-balanced`, `ieee4-dGy-stepdown-unbalanced` (1.95.251), `ieee4-dGy-stepup-balanced`, `ieee4-dGy-stepup-unbalanced` (1.95.252), the eight `ieee4-yD-*` and `ieee4-dD-*` cases (1.95.253), `ieee123` (three-phase feeders, `gridsim-dist3ph/1`) | 4 to 123 nodes | IEEE PES Distribution System Analysis Subcommittee test feeders (Kersting). The files carry the published data and cite their source; they state no licence. |
| `data/matpower/lib` | `case9`, `case14`, `case30`, `case57`, `case118`, `case145`, `case300`, `case24_ieee_rts` | 9 to 300 | MATPOWER 8.0 `data/`, BSD-3-Clause (`data/matpower/LICENSE-MATPOWER.txt`). |
| `data/matpower/pegase` | `case1354pegase`, `case2869pegase`, `case9241pegase` | 1,354 to 9,241 | MATPOWER 8.0, BSD-3-Clause, with a required citation (below). |
| `data/matpower/rts` | `pglib_opf_case73_ieee_rts` (RTS-96, three areas) | 73 | PGLib-OPF master, CC BY 4.0. |
| `data/matpower/nesta` | `pglib_opf_case3_lmbd`, `5_pjm`, `30_as`, `162_ieee_dtc`, `240_pserc`, `588_sdet` | 3 to 588 | PGLib-OPF master, CC BY 4.0. |
| `data/matpower/benchmarks` | `cigre_mv` | 15 | Authored to CIGRE TF C6.04.02; the data follow the pandapower reference (BSD-3-Clause). |
| `data/matpower/dist` | `case18`, `case22`, `case33bw`, `case69`, `case85`, `case533mt_hi` | 18 to 533 | Published radial distribution feeders; the archive README lists no source or licence for this folder. |
| `data/matpower/gb` | `GBreducednetwork`, `GBnetwork`, and the PyPSA-GB CSV networks | 29 and 2,224 | See [The GB models](#the-gb-models). |

The `data/intl` maps are built by GDA from open transmission topology with synthesised line impedances (per voltage
class, where the source lacks R and X) and a synthetic demand snapshot. They are indicative maps, not planning-grade
models: the topology and geography are real, the electrical data are not. Each folder carries a `MODEL_CARD.md`
(counts, island structure, kV mix and notes) and an `ATTRIBUTION.txt`.

Only `ireland-all-island` has an AC power-flow solution as shipped, and its solved voltages run from 1.020 to 1.126
per-unit, so the report reads `OUT OF BAND`. `iberia`, `france` and `continental-europe` do not converge in a base
solve with reactive limits on or off, from their stored voltages or a flat start. The cause is the data: nearly every
unit is a fixed `pq` injection, so only the island slacks hold voltage against the line charging of a whole country.
The loader prints a note on stderr when you load one of the three, and any verb that solves it reports the
non-convergence and exits 3:

```
$ gridsim pf data/intl/iberia --top 3
Power flow - Iberian peninsula (Spain + Portugal) - open transmission model (1497 buses)
  NOT CONVERGED (MaxIterations) in 100 iteration(s), mismatch 3.43E-001 pu   losses 1121.355 MW / 986.331 MVAr
  voltage range 0.2830 - 1.0365 pu
  lowest voltages:
      bus 780    0.2830 pu     31.309 deg
      bus 779    0.2834 pu     32.390 deg
      bus 709    0.2838 pu      0.498 deg
note: data/intl/iberia: this indicative map has no AC power-flow solution as shipped: 225 of its 227 units are fixed (pq) injections, so only the two island slacks hold voltage against 22,811 MVAr of line charging (data, not the solver; see data/intl/README.md). A verb that solves it reports the non-convergence.
[exit 3]
```

Use these three as maps of the topology. `data/intl/README.md` holds the measurements behind each statement.

The `data/matpower` README requires these citations when you publish results: for PEGASE, S. Fliscounakis et al.,
"Contingency Ranking with Respect to Overloads in Very Large Power Systems...", IEEE Trans. Power Syst., 2013, and C. Josz et al., "AC Power Flow Data in
MATPOWER and QCQP Format: iTesla, RTE Snapshots, and PEGASE", 2016; for PGLib-OPF and the RTS-96 and NESTA lineage,
S. Babaeinejadsarookolaee et al., "The Power Grid Library for Benchmarking AC Optimal Power Flow Algorithms",
arXiv:1908.02788; for CIGRE MV, the CIGRE Task Force C6.04.02 final report, 2014.

The three-phase feeders in `data/dist3ph` are not network cases for the bare run or the positive-sequence verbs. They
load through `gridsim dist3ph <feeder>.json` ([ch. 19](<0019 CLI reference.md#dist3ph>)), which also accepts a radial
positive-sequence `.m` file or case folder and lifts it into a balanced three-phase feeder.

## The case library and its provenance tags

### The MATPOWER archive

`data/matpower` holds the committed archive, one folder per source. The table gives what `gridsim pf <file>` reports for
each case on this build; a case that does not converge is a property of its input, not a solver defect.

| Folder | Cases | Power flow from the file |
|---|---|---|
| `lib` | `case9`, `case14`, `case30`, `case57`, `case118`, `case145`, `case300`, `case24_ieee_rts` | Converges (2 to 7 iterations). |
| `pegase` | `case1354pegase`, `case2869pegase`, `case9241pegase` | Converges (9, 14 and 14 iterations). |
| `rts` | `pglib_opf_case73_ieee_rts` | Converges (12 iterations). |
| `nesta` | `pglib_opf_case30_as`, `pglib_opf_case5_pjm`, `pglib_opf_case588_sdet` | Converge (6, 3 and 11 iterations). |
| `nesta` | `pglib_opf_case3_lmbd`, `pglib_opf_case162_ieee_dtc`, `pglib_opf_case240_pserc` | Do not converge: 100 iterations, mismatch 6.76, 2.57 and 90.3 per-unit. |
| `benchmarks` | `cigre_mv` | Converges (11 iterations). |
| `dist` | `case18`, `case22`, `case33bw`, `case69`, `case85`, `case533mt_hi` | Converge (5 to 11 iterations). |
| `gb` | `GBreducednetwork`, `GBnetwork` | Converge (16 and 13 iterations). |

A `pglib_opf_` file ships an optimisation operating point, not a solved power-flow snapshot. Three of the six shipped
files carry a placeholder dispatch that no power flow can carry:

| Case | Demand MW | Stated generation MW | What the slack would have to absorb |
|---|---:|---:|---|
| `pglib_opf_case3_lmbd` | 315.0 | 2,000.0 | a 1,685 MW surplus over lines whose V^2/X capability is 111 to 161 MW |
| `pglib_opf_case162_ieee_dtc` | 7,239.1 | 5,516.0 | a 1,723.1 MW deficit, above the slack unit's 1,119 MW Pmax |
| `pglib_opf_case240_pserc` | 144,179.7 | 100,642.9 | a 43,536.9 MW deficit, above the slack unit's 5,893 MW Pmax |

At a balanced dispatch each solves in a few Newton iterations (6, 5 and 12 with reactive limits on, for the DC-OPF dispatch,
according to the `data/matpower` README). Use these three through `dcopf` and `acopf`, not as
power-flow snapshots ([ch. 6](<0006 OPF, CPF, taps, distribution factors and losses.md>)). The original NESTA archive
is retired; its cases are carried in PGLib-OPF, which is where the `nesta` folder sources them.

Two facts about the feeders. `case533mt_hi` writes its base voltages as `135/sqrt(3)` and its base as `50/3` MVA, both of
which the parser evaluates, and
after its out-of-service branches are dropped it is strictly radial: 533 buses and 532 branches. The raw-unit feeders
`case22`, `case33bw`, `case69` and `case85` are converted to per-unit and MW on load. Whether the sweep or the
Newton engine solves a radial case is the engine selection of [ch. 5](<0005 Running power flows.md>).

RTS-79 (the 24-bus `case24`) and RTS-96 (the 73-bus `pglib_opf_case73_ieee_rts`) are different systems. RTS-79 is a
built-in name; RTS-96 loads only from its file.

### Growing the archive

`tools/fetch-cases.sh` expands the archive into the full PGLib-OPF and MATPOWER universe, under `data/matpower/pglib/`
(every `pglib_opf_case*.m` of the PGLib-OPF master) and `data/matpower/matpower-all/` (every `case*.m` of the MATPOWER
8.0 tag). It skips files already present. Anything under `data/matpower/` is discovered recursively: the desktop
picker lists every file, and the case registry behind `bench`, `nminus1-sweep --all`, `stress --all` and
`conformance` enumerates the folder and skips a file whose stem equals an embedded name (the embedded copy wins). To add
your own case, drop a valid `.m` file anywhere under `data/matpower/`, or write a JSON case folder and load it with
`--dir`. A file under `data/matpower` is bundled, so it never counts against a bus limit.

### Provenance tags

Data in an authored asset class or a v2 library template carries a provenance tag that says where the value came from.
The tags are `Recorded` (from an archived record), `DerivedFromRecorded` (computed from recorded data),
`CuratedPlaceholder` (a stand-in for data that has not been sourced, always marked `[VERIFY]`), `Synthetic` (model-generated,
not from any record), `Counterfactual` (what a device would have done, never an input to another replay),
`Interpolated` (gap-filled input, never an event source) and `UnvalidatedAtScale` (a result with no GB-scale
reference). Each tag is described in the chapter that owns it; a `CuratedPlaceholder` without `[VERIFY]` in its note is
refused at load.

### The unmodelled sites

`data/library/placeholders/unmodelled-sites.json` is a `gridsim-library/2` file of 15 named sites that no public
dataset covers. Every one is a placeholder template (`status: "placeholder"`, kind `site`) whose `generation` and
`demand` values are deliberately empty (`null`), tagged `CuratedPlaceholder` with the note "Deliberately empty: not in
any public dataset [VERIFY]". Each is a named site and nothing more; no solver models any of them, and no value is
supplied or implied. The sites are:

- `Nuclear-Sub`
- `Carrier`
- `PCI-Datacentres`
- `Hyperscale-Datacentre`
- `AI-Training-Campus`
- `Naval-Base-Shore-Supply`
- `Military-Airfield`
- `SMR` (title: SMR (small modular reactor))
- `Private-Wire-Network`
- `Behind-The-Meter-BESS-Aggregate`
- `Electrolyser-Cluster`
- `Arc-Furnace-Steelworks`
- `Rail-Traction-Supply`
- `Megawatt-EV-Charging-Hub`
- `Crypto-Mining-Site`

The ids are `gridsim.placeholder.site.<name>`, each at revision 1. The file validates as a library, and a case that
binds one of these templates to an element is refused.

```
$ gridsim validate data/library/placeholders/unmodelled-sites.json
OK    data/library/placeholders/unmodelled-sites.json (gridsim-library/2)
validate: 1 of 1 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 0]
```

## JSON v2 files

JSON v2 is a stricter, versioned format: every document names its type and version in a `"schema"` member
(`gridsim-case/2`, `gridsim-network/2`, `gridsim-library/2`, `gridsim-kind/2`), unknown members are refused everywhere,
and values carry units and provenance. The schemas are in `schemas/v2`, and technical
[ch. 28](<../technical/0028 The v2 JSON schema.md>) specifies the format. What exists today:

- **`gridsim validate <file.json|folder>...`** checks v2 documents against the schemas by their `schema` tag and lists
  every error with its JSON pointer. A file with no tag, as every v1 file is, is reported as invalid. Exit 0 when every
  document is valid, 3 when any is not ([ch. 19](<0019 CLI reference.md#validate>)).
- **A v2 case manifest loads as a case.** A `<case>` argument ending in `.json` is read as a `gridsim-case/2` manifest:
  the manifest, the network files it lists (inside its own folder) and the library files it imports (relative to the
  shipped `data` folder) are validated exactly as `validate` validates them, and the model is built by the same builder
  as a v1 folder. A v2 case and its v1 equivalent give the same model and the same numbers bit for bit.
- **Not everything loads v2.** The bare run (`gridsim <manifest>.json`) refuses a manifest, and `--dir` reads v1
  folders only: a folder of v2 files is refused with `buses.json is empty or missing`. Give the manifest to a verb that
  takes a positional case.
- **Some v2 content the model cannot carry yet is refused by name, never dropped:** line and transformer `type`, an
  out-of-service line or transformer,
  grid-code and solver profiles, controller references, and non-numeric parameters on a template that becomes an
  asset class. Fault sequence data loads: generator `subtransientXdPu` and `negativeSeqXPu`, and transformer
  `windings`, `r0Pu`, `x0Pu` and `neutralXPu`, so a v2 case can carry the winding connections an earth-fault study needs
  ([ch. 12](<0012 Faults, short circuit and protection grading.md>); technical ch. 28).
- **`convert` writes v2.** An output ending in `.json` is written as a v2 case (the manifest and its network file
  beside it), with the fault sequence data and the asset and protection bundles, and proved by reading it back
  ([ch. 19](<0019 CLI reference.md#convert>)). Out-of-service branches and cost curves are not written, and a note says so.
- **The template library is the placeholder file.** `data/library` holds `placeholders/unmodelled-sites.json` and
  nothing else, so a manifest has no shipped template to import beyond those placeholders.

A two-bus case, written by hand (the files are in `docs/_captures/networks/inputs/v2-two-bus`):

```json
{
  "schema": "gridsim-case/2",
  "name": "Two-bus v2 example",
  "system": { "baseMva": 100, "frequencyHz": 50 },
  "network": ["two-bus.network.json"]
}
```

Validate it, then solve it:

```bash
gridsim validate docs/_captures/networks/inputs/v2-two-bus/two-bus.case.json
gridsim dcpf docs/_captures/networks/inputs/v2-two-bus/two-bus.case.json
```

```
$ gridsim validate docs/_captures/networks/inputs/v2-two-bus/two-bus.case.json
OK    docs/_captures/networks/inputs/v2-two-bus/two-bus.case.json (gridsim-case/2)
OK    docs\_captures\networks\inputs\v2-two-bus\two-bus.network.json (gridsim-network/2)
validate: 2 of 2 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 0]
```

```
$ gridsim dcpf docs/_captures/networks/inputs/v2-two-bus/two-bus.case.json
DC power flow - Two-bus v2 example (2 buses, 1 branches)  [linear: series reactance only; R, charging, shunts, taps and phase shifts ignored; |V| = 1 pu; lossless]
  island ref bus 1          2 buses  demand      300.0 MW  scheduled generation      306.0 MW  reference injects      300.0 MW
  0 of 1 rated branch(es) above 100% of RateA
  largest flows:
      branch 0     L1-2                  1 -> 2          300.00 MW     37.5% of 800 MVA
[exit 0]
```

A document that fails validation lists each error and exits 3. The fixture below has a bus type of `swing`:

```
$ gridsim validate tests/GridSim.Tests/fixtures/v2/networks/bad-bus-type.network.json
FAIL  tests/GridSim.Tests/fixtures/v2/networks/bad-bus-type.network.json (gridsim-network/2): 1 error(s)
        /buses/0/type: value "swing" is not one of the allowed values ["pq", "pv", "slack"].
validate: 0 of 1 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 3]
```

The bare run and `--dir` refuse the same case:

```
$ gridsim docs/_captures/networks/inputs/v2-two-bus/two-bus.case.json --once
error: unknown command or case 'docs/_captures/networks/inputs/v2-two-bus/two-bus.case.json'.
Run a case (case9|case14|case30|case57|case118|case300|case145|case24|cigre|uk), a .m file,
or a verb (transient, stability, modal, replay-event, scenario, fleet, evidence, gda-replay, crossval, estimate, bench, ...).
[exit 2]
```

```
$ gridsim --dir docs/_captures/networks/inputs/v2-two-bus --once
error: cannot load case folder 'docs/_captures/networks/inputs/v2-two-bus': buses.json is empty or missing.
[exit 1]
```

## Licence tiers and bundled folders

The quantity a licence limits is the bus count of a network model. The free Personal tier and the Academic tier allow 1,500
buses per user network, and the commercial and Network Operator tiers have no limit ([ch. 4, Licensing and
activation](<0004 Licensing and activation.md>)). A **bundled network** never counts. The CLI, the desktop app and
the web app share one list, `BundledNetworks.DataFolders` (`src/GridSim.Core/Licensing/BundledNetworks.cs`):

`matpower`, `uk`, `gb-synthetic`, `gb-full`, `intl`, `dist3ph`

Bundled means the shipped files themselves, judged by content. The folder must be one of those under the program's
own `data/` folder (the install's or the checkout's), and every file loaded must match the shipped-content manifest
`data/bundled.sha256`, which holds the SHA-256 of each shipped file. A file you add to a bundled folder, or a shipped
file you edit, is a user network and is counted; so is a copy of a shipped file placed anywhere else. Built-in names
(`case9` and the rest) never touch a file loader and are exempt. Anything else is a user network: a materialised
`gda-out/` tree, a folder under `docs`, a file you wrote. A user network above the limit is refused with exit 2 before
anything runs:

This command runs `case9241pegase.m` copied out of `data/`, which makes it a user network, on the free tier:

```
$ gridsim results/networks-tmp/mycase9241.m --once
error: This network has 9,241 buses; your Personal licence allows 1,500. A larger or commercial licence removes the limit: gdatooling@proton.me
[exit 2]
```

The same 9,241-bus network loads at every tier from `data/matpower/pegase`, because it is bundled. The full GB model
loads at every tier too:

```
$ gridsim pf gb-full --top 3
Power flow - GB full (3539-bus, 5918-branch) (3539 buses)
  converged in 11 iteration(s), mismatch 3.25E-011 pu   losses 523.821 MW / -7587.106 MVAr
  voltage range 0.9434 - 1.0457 pu
  lowest voltages:
      bus 4105   0.9434 pu    -10.342 deg
      bus 6875   0.9440 pu    -15.281 deg
      bus 3219   0.9484 pu     -7.365 deg
[exit 0]
```

Both licence captures were made with the licence tier forced to the free default (`GRIDSIM_LICENCE` set to an invalid
key falls back to the free tier). A three-phase feeder is counted in feeder nodes, one bus per node, not per phase.

## Which verbs take a case or `--dir`

The verbs differ in how they select a network, and the differences are deliberate refusals, not accidents. This chapter
gives the rules that belong to loading; the verb-by-verb table, with the messages and captures, is in
[ch. 30, Troubleshooting](<0030 Troubleshooting.md>), and the case-argument rules are in
[ch. 19](<0019 CLI reference.md#case-arguments>).

- **The bare run** (`gridsim <case>`) takes one case and refuses a second (exit 2). It accepts a built-in name from the nine above or
  `uk`, a `.m` path, `--mpc <file>.m`, or `--dir <folder>` (also `-d`). It does not accept `gb-full`, `gb-synthetic`,
  a folder positionally, or a `.json` manifest.
- **Verbs that take a case** (`pf`, `dcpf`, `dcopf`, `acopf`, `cpf`, `taps`, `ptdf`, `lodf`, `physics-check`,
  `protection-check`, `fault`, `estimate`, `crossval` and others) take it positionally: any name in this chapter, a
  `.m` file, a case folder that holds a `system.json`, or a v2 manifest. They refuse `--dir`.
- **Verbs that take both** (`trip`, `screen`, `nminus1`, `nminus2`, `n-1-1`, `domains`, `export`, `nminus1-sweep`,
  `stress`, `chain`, `cycle`, `emt`, `harmonics`, `impedance-stability`) accept the network as a positional case or as
  `--dir <folder>`, and refuse both together unless they name the same folder.
- **`stability` and `modal`** honour `--dir` and ignore a positional case without a message when both are given.
- **Verbs that read a result or a different file** (`validate`, `convert`, `dist3ph`, `replay-event`, `gda-replay` and
  the like) take their own inputs; see their sections in ch. 19.

## Selecting a case in the desktop app

The **NETWORK** picker in the desktop app lists what it finds at start-up. `GB reduced  10-zone` (`data/uk`),
`GB synthetic  distribution depth` and `GB full  3539-bus transmission` come from the `data` folder beside the app.
`GDA  <case>` entries are the materialised case folders found in a `gda-out/` folder above the app. `MATPOWER  <stem>`
entries are every `.m` file under `data/matpower`, discovered recursively. `Replay  <run>` entries are pre-solved run
folders under a nearby `results/` folder, and `Live bridge` appears while a bridge endpoint is set. The `data/intl`
maps are not in the picker; load them on the command line. An imported MATPOWER case carries no coordinates, so the
app lays it out itself; the electrical model and the solve are untouched. See
[ch. 8, The desktop app](<0008 The desktop app.md>).

## Refusals

Every refusal below is from a captured run. A case that cannot be loaded never produces a report.

An unknown first word of the bare run:

```
$ gridsim nosuchcase --once
error: unknown command or case 'nosuchcase'.
Run a case (case9|case14|case30|case57|case118|case300|case145|case24|cigre|uk), a .m file,
or a verb (transient, stability, modal, replay-event, scenario, fleet, evidence, gda-replay, crossval, estimate, bench, ...).
[exit 2]
```

A valid case name that the bare run does not take (`gb-full`) gives the same message and exit 2; use `--dir data/gb-full`
or a verb.

```
$ gridsim gb-full --once
error: unknown command or case 'gb-full'.
Run a case (case9|case14|case30|case57|case118|case300|case145|case24|cigre|uk), a .m file,
or a verb (transient, stability, modal, replay-event, scenario, fleet, evidence, gda-replay, crossval, estimate, bench, ...).
[exit 2]
```

An unknown case given to a verb exits 2 and lists every name:

```
$ gridsim screen nosuchcase_xyz
error: screen: unknown case 'nosuchcase_xyz' (built-in names: case118, case14, case145, case24, case30, case300, case57, case9, cigre, gb, gb-full, gb-network-2224, gb-reduced, gb-reduced-29, gb-synthetic, ieee9, pypsa-gb-reduced, pypsa-gb-zonal, rts, synthetic, uk; or a case folder, or a MATPOWER .m file, or a JSON v2 case manifest .json)
[exit 2]
```

Every verb follows the same rule: an unknown case name exits 2 with this list ([ch. 19, Case
arguments](<0019 CLI reference.md#case-arguments>)).

An ambiguous bare MATPOWER name exits 2 ([MATPOWER files](#matpower-files)), a network over the licence limit exits 2
([Licence tiers](#licence-tiers-and-bundled-folders)), a v1 file with a bad key, no `baseKv` or no slack exits 1
([JSON case directories](#json-case-directories)), and a `validate` failure exits 3
([JSON v2 files](#json-v2-files)). In summary:

| Exit | Cause |
|---:|---|
| 1 | The case cannot be loaded: file or folder missing, bad content, an unsolvable case such as `pypsa-gb-zonal`. |
| 2 | The case name is not recognised (on every verb), the name is ambiguous, or the licence limit is exceeded. |
| 3 | A verb that solved the case found it does not converge (the shipped `iberia`, `france`, `continental-europe` maps), or `validate` found an invalid document. |

## Limits

- A MATPOWER `.m` file carries no nominal frequency, so every loaded `.m` case runs at an assumed 50 Hz unless the global `--frequency-hz <Hz>` switch declares the real value (R269).
- `mpc.dcline` DC lines are parsed and carried but not modelled, so a case that has them solves differently from MATPOWER's own power flow.
- A v1 folder written by `gridsim convert` or `gda --out` does not carry `asset-classes.json`, `asset-bindings.json` or `protections.json`; the files are copied across by hand. A JSON v2 case written by `convert` (an output ending in `.json`) carries them, except a bundle it cannot represent exactly (for example classes that came from imported library templates), which it leaves out whole and names in its notes.
- `data/intl/iberia`, `data/intl/france` and `data/intl/continental-europe` have no AC power-flow solution as shipped, because their generators are nearly all fixed injections (the data, not the solver).
- JSON v2 loads through a positional `<case>` argument only; the bare run and `--dir` read v1 folders, and the model cannot carry some v2 content (it is refused by name).
- The NESO PowerFactory GB models cannot be loaded; GridSim has no importer for them.

## See also

- [ch. 1, Getting started](<0001 Getting started.md>) - build, test, first solve
- [ch. 4, Licensing and activation](<0004 Licensing and activation.md>) - tiers, bus limits and the `.gridsim` file
- [ch. 5, Running power flows](<0005 Running power flows.md>) - what happens after the case loads
- [ch. 8, The desktop app](<0008 The desktop app.md>) - the source picker, replay runs and the bridge
- [ch. 19, CLI reference](<0019 CLI reference.md#case-arguments>) - the case-argument rules, `validate`, `convert`, `synth` and `gda`
- [ch. 24, The GDA bridge](<0024 The GDA bridge.md>) - streaming GDA cases instead of materialising them
- [ch. 30, Troubleshooting](<0030 Troubleshooting.md>) - which verbs take a positional case and which take `--dir`
- [ch. 33, The case editor](<0033 The case editor.md>) - editing a case folder in place
- [Technical ch. 2, Data model and per-unit](<../technical/0002 Data model and per-unit conventions.md>) - the model every loader builds
- [Technical ch. 10, File and wire formats](<../technical/0010 File and wire formats.md>) - the JSON v1 files in detail
- [Technical ch. 28, The v2 JSON schema](<../technical/0028 The v2 JSON schema.md>) - the v2 format and its loader
