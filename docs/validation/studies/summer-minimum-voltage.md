# Summer minimum demand: high voltage on gb-full

Study run 2026-10-26 on `gb-full` (3,539 buses, 5,918 branches) with `gridsim qsts` and `gridsim pf`, driven by
`tools/studies/summer_minimum_voltage.py`, every number from one build. The demand, the embedded PV and the
commitment are synthetic (built by the rules below from the case files); no measured or private data enters the study.
`gb-full` itself contains synthesised elements (the supergrid transformers, a set of power-flow-only voltage-control
units at grid supply points, the transformer tap settings and the generators' reactive limits), so read the numbers as
the shape of the answer on this model, not as operating limits of the real system.

## The question

At summer minimum demand the transmission network makes more reactive power than anything consumes: cable and line
charging is fixed by the voltage, while the I^2 X absorption of the circuits falls with the square of the flow. The
voltage rises unless something absorbs the surplus. The questions:

1. Where does the voltage go above its limit, and by how much?
2. How much reactive absorption does the network need, and where does it come from today?
3. Does absorption placed by reactors, by embedded inverter volt-var or by synchronous condensers restore the limits,
   and how much of each is needed?

## Method

- **Snapshots.** Two summer minimum instants, each a 30 minute `qsts` step (one power flow with every tap changer
  settled at instant timing; tap changers, reactors and inverters act in the step):
  - **midday:** underlying demand 0.45 x the case demand (23,978 MW of 53,285 MW), with embedded PV of 0.20 x the case
    demand (10,657 MW) spread over the 1,958 demand rows pro rata to their demand, so the net demand the transmission
    generation meets is 0.25 x the case demand (13,321 MW); this is the deepest trough, made by embedded solar;
  - **night:** demand 0.33 x the case demand (17,584 MW), no PV.
  Reactive demand scales with active demand (the case's power factor at each bus).
- **Commitment**, two bounds:
  - **all:** every unit of the case scaled pro rata to the net demand plus a 2 % loss allowance (factor 0.2725 at
    midday, 0.3597 at night), so every unit's reactive range stays online;
  - **min:** the 99 transmission-connected synchronous thermal units (CCGT, OCGT, gas, biomass, CHP and other with
    voltage control) are left out, nuclear stays at its case output (5,120 MW), and every other unit is scaled to fill
    the rest (factor 0.2924 at midday, 0.4426 at night). The units left keep their reactive ranges (the case gives
    every unit +/- 0.4 x its MVA rating), including the converter-connected wind, interconnector and storage units.
- **The synthesised voltage-control units are left out of every variant.** `gb-full` holds 1,071 power-flow-only units
  at grid supply points, each holding 1.02 pu with up to 500 MVAr either way. They are modelling devices, and with them
  in place no bus can leave its band: they would absorb the very surplus the study asks about. The national slack (one
  400 kV bus at Melksham) stays for the real-power balance; its reactive output is reported.
- **Limits per voltage level** (GB Grid Code CC.6.1.4 for the transmission levels, ESQCR regulation 27 for
  distribution): 400 kV 0.95-1.05 pu, 275 and 132 kV 0.90-1.10 pu, 66 kV and below 0.94-1.06 pu, set on each bus of the
  study variants and judged with `"perBusVoltageLimits": true`.
- **Reactive balance** from `gridsim pf` on the same variant with the PV folded into the demand (the engine folds a
  unity power factor PV unit in as negative load, so the two are the same injection) and the taps at their case values:
  the generators' reactive output per bus is the bus's net injection plus its reactive demand.
- **Reactor plan:** greedy. At the worst over-voltage bus (largest excess over its own limit) a fixed shunt reactor is
  sized by the secant method on the settled voltage (every solve re-settles the taps) until the bus is 0.001 pu under
  its limit, within 2e-4 pu; the network is re-solved and the next worst bus is treated, until no bus is over.
- **Volt-var:** every PV unit on a PQ bus on IEEE 1547-2018 Category B volt-var (absorbing above 1.02 pu, 0.44 of its
  rating at 1.08 pu), with 1.1 MVA of inverter per MW.
- **Condensers:** a synchronous condenser (0 MW, holding 1.0 pu, reactive range +/- its rating) at each reactor site.
- **Run it:** `python -I tools/studies/summer_minimum_voltage.py --gridsim src/GridSim.Cli/bin/Release/net10.0/gridsim.exe
  --work <folder> --summary <folder>/summary.json`. The variants are written to the work folder, outside the repository.

## Results

### 1. Hotspots: one cluster, in West Yorkshire 132 kV

With the voltage-control units out, the transmission levels stay inside their limits in every snapshot: the highest
400 kV voltage is 1.0343 pu and the highest 275 kV voltage 1.0344 pu (both night, minimum commitment), held by the
generators that remain. Every bus above its limit is in one 132 kV cluster around Bradford and Leeds, the same 15
buses in all four snapshots:

| Snapshot | Highest 400 kV | Highest 275 kV | Highest 132 kV | Buses over | Tap operations to settle |
|---|---:|---:|---:|---:|---:|
| midday, all | 1.0240 | 1.0290 | 1.4605 (Menston T1) | 15 | 2,055 |
| midday, min | 1.0322 | 1.0319 | 1.4627 (Menston T1) | 15 | 2,548 |
| night, all | 1.0241 | 1.0290 | 1.4605 (Menston T1) | 15 | 2,316 |
| night, min | 1.0343 | 1.0344 | 1.4626 (Menston T1) | 15 | 2,889 |

The cluster at midday with minimum commitment (the worst snapshot, used for the mitigation below):

| Bus | Name | pu (limit 1.10) |
|---:|---|---:|
| 4929 | Menston T1 | 1.4627 |
| 4821 | Girlington T1 | 1.4522 |
| 4743 | Bradford West SGT4 | 1.4499 |
| 4738 | Bradford T1 | 1.4352 |
| 4822 | Girlington T2 | 1.2558 |
| 4741 | Bradford West SGT2 | 1.2537 |
| 4739 | Bradford T2 | 1.2465 |
| 4909 | Leeds North T2 | 1.1892 |
| 4907 | Leeds East Tee | 1.1890 |
| 4994 | Skel-Stou-Wake-Tee | 1.1881 |
| 5014 | Stourton GT1 | 1.1881 |
| 5031 | Wakefield North T1 | 1.1880 |
| 4995 | Skelton Grange B SGT1 | 1.1879 |
| 4916 | Low Road T1 | 1.1872 |
| 4904 | Leeds East T3 | 1.1858 |

The Bradford buses carry no demand and are joined by 132 kV circuits whose charging is large for their length
(Girlington T1 to Bradford West SGT4: b 0.506 pu on 100 MVA with x 0.00646 pu), fed through two 90 MVA supergrid
transformers. The charging has nowhere to go but back through those transformers, which run at up to 290.4 % of rating,
and the voltage rises until it does. The same cluster is over at every demand level studied, so it is a feature of the
case's data here, not of summer minimum alone (see the charging sensitivity in section 4).

### 2. The reactive balance: 11 to 12 GVAr absorbed by generators

The network's net reactive production (charging less I^2 X, the negative of the `pf` reactive loss):

| Snapshot | Network surplus (MVAr) | Absorbed by demand | Absorbed by generators and slack | of which the slack (Melksham) | Generator buses at their lower limit |
|---|---:|---:|---:|---:|---:|
| midday, all | 15,780.7 | 4,285.4 | 11,532.7 | 435.8 | 41 (3,036.0 MVAr) |
| midday, min | 15,385.4 | 4,285.4 | 11,111.2 | 1,420.6 | 48 (3,839.3 MVAr) |
| night, all | 15,316.4 | 3,142.6 | 12,200.3 | 445.8 | 46 (3,685.9 MVAr) |
| night, min | 14,808.9 | 3,142.6 | 11,654.6 | 1,551.9 | 50 (4,159.7 MVAr) |

All of the generators' absorption is at 400 kV (8,002 to 8,853 MVAr) and 275 kV (3,110 to 3,347 MVAr) buses: the case's
embedded units are fixed power factor (PQ) units and absorb nothing. With minimum commitment the slack takes 984.8 MVAr
more at midday (1,420.6 against 435.8 MVAr), because the thermal units left out were absorbing. The largest absorbers
at midday with minimum commitment: Melksham (the slack) 1,420.6 MVAr, Bramford 489.2, Kingsnorth 480.0, Creyke Beck
460.4, Fawley 460.0 and Bicker Fen 418.8 MVAr.

Night has less demand to absorb (3,142.6 against 4,285.4 MVAr), so the generators do more there (12,200.3 MVAr with
everything committed) even though the network surplus is smaller.

How much of the generators' capability is needed: every unit's lower reactive limit scaled down, at midday with
minimum commitment.

| Lower limits | Solution | Buses over | Highest 400 kV | Slack absorbs (MVAr) | Generator buses at their lower limit |
|---|---|---:|---:|---:|---:|
| 100 % | yes | 15 | 1.0322 | 1,420.6 | 48 (3,839.3 MVAr) |
| 75 % | yes | 15 | 1.0336 | 1,630.7 | 59 (4,050.0 MVAr) |
| 50 % | yes | 16 (adds Hunshelf T2, 1.1002) | 1.0362 | 1,907.3 | 76 (5,727.4 MVAr) |
| 25 % | low-voltage solution: 147 buses under their lower limit, north-east Scotland 275 kV at 0.78 pu | 15 | 1.0340 | 1,360.2 | 117 (7,433.6 MVAr) |
| 0 % | none: the power flow does not converge (mismatch 3.232 pu after 38 iterations) | - | - | - | - |

The transmission network holds its limits with half the generators' absorption capability, the slack taking up the
rest (1,907.3 MVAr at 50 %). Below that the case loses a credible solution. Read this with the limits below: the slack
is an ideal absorber at one bus, and every unit's range is the case's synthesised +/- 0.4 x rating.

### 3. Mitigation of the hotspot

**Reactors (greedy).** Four fixed reactors clear every bus at midday with minimum commitment:

| Order | Bus | Name | Before (pu) | Reactor (MVAr at 1 pu) | After (pu) | Buses still over |
|---:|---:|---|---:|---:|---:|---:|
| 1 | 4929 | Menston T1 | 1.4627 | 88.8 | 1.0991 | 14 |
| 2 | 4822 | Girlington T2 | 1.2515 | 47.5 | 1.0990 | 12 |
| 3 | 4909 | Leeds North T2 | 1.1870 | 28.9 | 1.0990 | 4 |
| 4 | 4821 | Girlington T1 | 1.1248 | 8.9 | 1.0990 | 0 |

174.1 MVAr in all. With the plan the highest 132 kV voltage is 1.0999 pu, and the same four reactors clear the other
three snapshots too (highest 132 kV 1.0987 pu midday and night with everything committed, 1.0999 pu at night with
minimum commitment). With the generators' absorption halved the greedy plan picks the same four buses with 175.5 MVAr
(89.2, 48.0, 29.4 and 8.9 MVAr).

**Embedded inverter volt-var.** 1,875 PV units on PQ buses (10,657 MW of PV, 11,723 MVA of inverter, able to absorb up
to 4,833.2 MVAr at 1.08 pu). It does not reach the hotspot: no demand, and so no PV, sits at the Bradford buses, and
the 33 kV buses the PV connects to are held near 1.0 pu by their tap changers, inside the 0.98-1.02 pu volt-var
deadband. At the unity power factor voltages 1,059 of the 1,875 units are above 1.02 pu, and the Category B curve
there asks for 312.8 MVAr in all, 6.5 % of the capability. The volt-var run settles at the default tolerance (1e-7 of each
rating) in 256 power flows: 271.3 MVAr absorbed, the Bradford buses unchanged (Menston T1 1.4617 pu) and the Leeds
buses lowered from 1.1892 to 1.1368 pu, still over (15 buses over, as without volt-var).

**Synchronous condensers.** A condenser holding 1.0 pu at each reactor site, rated at the reactor's MVAr, does not
clear the hotspot (highest 1.1661 pu, Girlington T1): a reactor's absorption grows with the square of the voltage
(88.8 MVAr at 1 pu is 107.3 MVAr at 1.0991 pu), while a condenser at its limit absorbs its rating. Rated at what each
reactor absorbs at its final voltage (MVAr x V^2), 206.7 MVA in all, the condensers clear it (with the generators'
absorption halved, highest 1.0999 pu, the same as the reactors). A condenser also adds inertia and fault level, which
this study does not value.

### 4. Sensitivity: the 132 kV charging data

103 of the case's 2,719 132 kV branches have b/x above 32 (b and x in pu on 100 MVA), more than any 132 kV cable
gives (for 132 kV XLPE cable C/L x Zbase^2 is close to 16); together they carry 1,128 of the 4,440 MVAr of 132 kV
charging. With those branches' b set to 16 x their x:

- the hotspot shrinks but stays: 7 buses over, the highest 1.2883 pu (Menston T1);
- the greedy plan needs 62.8 MVAr (Menston T1 51.7, Girlington T2 7.2, Girlington T1 3.9 MVAr) instead of 174.1;
- the network surplus falls to 14,529.9 MVAr and the generators and slack absorb 10,255.7 MVAr (slack 1,264.2).

So the hotspot's location is robust to the doubtful data; its size is not, and a decision on reactors there needs the
circuit data checked first.

## Answer

1. **Hotspots.** On this case the only buses above their limits at summer minimum are 15 buses of the 132 kV network
   around Bradford and Leeds, up to 1.4627 pu, in every snapshot and commitment studied. The transmission levels stay
   inside their limits (highest 1.0343 pu at 400 kV) because the generators hold them.
2. **Absorption needed.** The network makes 14.8 to 15.8 GVAr more than its circuits absorb. Demand absorbs 3.1 to 4.3
   GVAr; the generators and the slack absorb the remaining 11.1 to 12.2 GVAr, all at 400 and 275 kV, the slack's share
   rising from 0.4 to 1.6 GVAr when the thermal units are decommitted. Half the generators' capability is enough on this
   case; a quarter gives no credible solution and none gives no solution.
3. **Mitigation.** At the hotspot, 174.1 MVAr of reactors at four 132 kV buses (Menston T1, Girlington T2, Leeds North
   T2, Girlington T1) clears every snapshot; condensers need 206.7 MVA for the same effect; embedded volt-var cannot
   reach it. For the national 11 GVAr, volt-var behind tap-changer regulated 33 kV buses is worth at most 0.3 GVAr on
   this case.

## Limits

- Synthetic snapshots: the demand factors, the PV share, its spread pro rata to demand and the two commitments are study
  assumptions, not a forecast. Reactive demand is scaled with active demand, so the distribution networks' own cable
  charging (which makes many grid supply points export reactive power at minimum demand) is not modelled.
- The case contains synthesised elements: the supergrid transformers, the tap changer settings, the generators'
  reactive ranges and the voltage-control units the study leaves out. The hotspot sits on 132 kV circuits whose
  charging is doubtful (section 4).
- The slack is an ideal absorber at one 400 kV bus; its MVAr stands for whatever would absorb there.
- Snapshots only: the settled state at each trough. Tap changer and reactor switching timing, and the morning and
  evening ramps, are not studied.
- The volt-var run needs GridSim's accelerated inverter iteration (technical ch. 37): the per-unit loop alone does not
  settle this many coupled units. The volt-var figures are the curve at the unity power factor voltages (312.8 MVAr) and
  the settled state (271.3 MVAr); both are far below the generators' absorption, so the conclusion does not rest on them.
- The reactive balance uses `pf` with the taps at their case values; the voltages use `qsts` with the taps settled.
- Generator reactive capability is held at its full range down to the scaled output; real units lose range at low
  output.

## See also

- [User manual ch. 34, Time series and hosting capacity](<../../manual/0034 Time series and hosting capacity.md>).
- [Technical ch. 37, Quasi-static time series and hosting capacity](<../../technical/0037 Quasi-static time series and hosting capacity.md>).
- [gb-full licence and provenance audit](../../assurance/gb-full-licence-and-provenance-audit.md).
