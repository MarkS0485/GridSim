# Paywalled references

Some values and citations in GridSim carry a `[VERIFY]` mark because the document that would settle them is behind a
paywall. This page lists each one: what GridSim uses, what free public sources confirm, which document would settle it,
and where you set the value yourself.

## Why these stay marked

The source data for these values is not free. Standards, textbooks and journal papers are sold as whole documents, and
each item below turns on one value or two, a table row or a section number. The author will not pay for access to a
whole document to check a single value or two: it is a poor return on investment. GridSim ships with the marks in place
instead, and says plainly what is and is not confirmed.

Many of these values can be set by the user in the network and modelling JSON files, or with a command-line flag. Where
you hold the document, or your own data, set the value there and the `[VERIFY]` default no longer applies to your
study. Where a value is fixed in the code and has no setting, this page says so.

Every public source named below was read in full where it was free to read. Nothing paywalled was bypassed.

## IEC 60909-0 (short-circuit currents)

**What GridSim uses.** The `iec60909` verb follows IEC 60909-0:2016 (Edition 2.0), with these values and rules taken
from it:

- the voltage factor c: 1.10 (HV) and 1.05 (LV) for maximum currents, 1.00 and 0.95 for minimum currents;
- Method B for the peak in a meshed network: kappa = 1.15 x kappa(R/X), capped at 2.0 (1.8 at 1 kV and below);
- Method C, the equivalent frequency method: every reactance at fc = 0.4 f;
- the Method C DC-component ratios fc / f, chosen by f x tb: 0.27 below 1 cycle, 0.15 below 2.5, 0.092 below 5 and
  0.055 below 10 (`Iec60909.MethodCDcFrequencyRatio`, `src/GridSim.Core/Protection/ShortCircuit/Iec60909.cs`);
- the impedance correction factors K_G, K_T, K_S and K_SO, and the factors m and n for the thermal-equivalent current;
- clause numbers in the code comments and the manuals (8.1.2 for Methods B and C, 9 and 9.2 for breaking current,
  10 for the DC component, 11.2.7 for the multiple-fed steady-state current, 14 and Annex A for m and n).

**What public evidence confirms.**

- The official free sample of IEC 60909-0:2016 (contents pages and clauses 1 to 3) confirms the clause numbering listed
  above, Table 1 "Voltage factor c", and the near-to-generator criterion of 3.17 Note 1. GridSim's clause citations
  follow that contents list.
- fc = 0.4 f for Method C is stated by two vendor help pages (EasyPower and Phase to Phase Vision).
- The 2001 edition placed the DC component in subclause 4.4; the 2016 edition places it in clause 10, which has no
  numbered table for the ratios.

**Not confirmed.**

- The clause text itself, including the K_S and K_SO formulas and whether the zero-sequence impedances are also
  corrected.
- The four Method C DC-component ratios. No free source reproduces them.
- The numbering of IEC 60909-0:2026 (Edition 3.0, published 2026-07-23). Its contents could not be read without
  payment. GridSim's citations use the 2016 numbering.

**Document that would settle it.** IEC 60909-0:2016, clauses 8 to 14 and Annex A; or IEC 60909-0:2026 Edition 3.0 for
the new numbering and any changed values.

**Where you set it.**

| Value | How to change it |
|---|---|
| Voltage factor c | `iec60909 --objective max` or `--objective min` picks the pair. The factor itself has no free setting. |
| Method B 1.15 factor | `iec60909 --peak-method c` uses Method C instead, which has no 1.15. The factor itself has no setting. |
| Method C fc = 0.4 f | No setting. f follows the case's nominal frequency: `frequencyHz` in a JSON case's `system.json`, or `--frequency-hz` for a MATPOWER `.m` file. |
| Method C DC-component ratios | No setting: fixed in the code. They apply only with `--peak-method c`. The default, `--peak-method b`, takes the DC component from the network's own R/X and does not use them. `--tb` sets the break time that selects the ratio. |
| K_G, K_T, K_S, K_SO | Off unless `iec60909 --corrections` is given. `--rated-pf` sets cos(phi_rG) (required; never assumed) and `--no-unit-oltc` selects K_SO instead of K_S. The formulas themselves have no setting. |
| Machine and transformer sequence data | In a JSON v2 case: a generator's `subtransientXdPu` and `negativeSeqXPu`; a transformer's `windings`, `r0Pu`, `x0Pu` and `neutralXPu`. |

## Kersting: open-bank and unequal-winding transformer examples

**What GridSim uses.** The three-phase feeder solver models open wye / open delta (`oy-od`) and open delta / open delta
(`od-od`) banks, and unequal winding impedances on the `y-d`, `d-d`, `oy-od` and `od-od` connections. They are proved
by self-consistency tests (the equal-winding limit matches the existing element bit for bit; losses, KCL and KVL
balance), not against the worked examples in W. H. Kersting, `Distribution System Modeling and Analysis`.

**What public evidence confirms.**

- The free IEEE 4 Node Test Feeder (revised 2006) prints an "Open Gr.Y-D" column. GridSim's open wye / open delta
  step-down results are pinned against it: 17 of 18 voltages and 10 of 10 currents within half a printed digit.
- The step-up open connection in that column is the leading connection, which GridSim does not model; such a case is
  refused, not approximated.
- The free IEEE `Wye-Delta Center Tapped Transformer Test Feeder` (2004) solves open banks with unequal 25 kVA and 10 kVA
  units, but it needs a centre-tapped secondary and an induction-motor load, which the solver does not model.

**Not confirmed.** Kersting's own worked examples for open banks and for unequal windings.

**Document that would settle it.** W. H. Kersting, `Distribution System Modeling and Analysis`, the chapter on
three-phase transformer connections.

**Where you set it.** Every transformer value is the user's, in the three-phase feeder JSON (`gridsim-dist3ph/1`),
`transformers` array: `connection`, `kva`, `kvLLFrom`, `kvLLTo`, `rPercent`, `xPercent`, and `windings` (an array of
per-winding `rPercent` / `xPercent` on the bank's kVA: three entries, ab / bc / ca, for `y-d` and `d-d`; two, ab / bc,
for the open banks). The open wye primaries sit on phases A and B; that choice has no setting.

## Li and Bo 2010: PJM 5-bus prices

**What GridSim uses.** The PJM 5-bus case ships as `data/matpower/nesta/pglib_opf_case5_pjm.m`. `gridsim dcopf
pglib_opf_case5_pjm.m --costs case --prices` gives LMPs of 16.9774, 26.3845, 30.0000, 39.9427 and 10.0000 per MWh
at buses 1 to 5, and a test compares them with the printed values 16.98, 26.38, 30.00, 39.94 and 10.00 from F. Li and
R. Bo, "Small Test Systems for Power System Economic Studies", IEEE PES General Meeting 2010.

**What public evidence confirms.**

- The dispatch (40, 170, 323.49, 0 and 466.51 MW) is recorded in the pglib case header and GridSim matches it.
- An independent HiGHS solve gives the same five prices to 1e-6.
- No free source prints the paper's five prices. The university repository record holds only the abstract.

**Not confirmed.** The five printed LMPs. They are quoted as remembered.

**Document that would settle it.** The IEEE Xplore paper (DOI 10.1109/PES.2010.5589973). A second source is F. Li and
R. Bo, "DCOPF-Based LMP Simulation: Algorithm, Comparison With ACOPF, and Sensitivity", IEEE Transactions on Power
Systems 22(4), 2007.

**Where you set it.** The LMPs are results, not inputs, so there is nothing to override. They follow from the case's
data, which is all the user's: generator cost curves in `mpc.gencost` (used with `--costs case`), or a `gridsim-costs/1`
JSON cost file given as `--costs <file.json>`, and the branch ratings in `mpc.branch`.

## IEEE PES TR-18: two-area benchmark

**What GridSim uses.** The two-area, four-machine model (`IeeeCases.TwoAreaFourMachine`, a library model used by the
tests) runs at 60 Hz. The code cites Kundur Example 12.6 and IEEE PES Technical Report TR-18, "Benchmark Systems for
Small-Signal Stability Analysis and Control" (2015), for the system.

**What public evidence confirms.**

- The IEEE PES task force's "Report on the 2-area, 4-generator system" (2014) states that its data corresponds to
  Kundur's Example 12.6.
- The task force's open journal summary (Canizares et al., IEEE Transactions on Power Systems, 2016) lists the
  two-area system as its Benchmark 3. A chapter number for TR-18 is therefore not cited.
- No free source states 60 Hz for the two-area system in words.

**Not confirmed.** The TR-18 chapter that holds the two-area system, and the 60 Hz in TR-18's own words.

**Document that would settle it.** IEEE PES TR-18 itself. It is free to PES members only.

**Where you set it.** The nominal frequency is the user's for any case they supply: `frequencyHz` in a JSON case's
`system.json`, or `--frequency-hz` for a MATPOWER `.m` file. The built-in library model's 60 Hz has no setting.

## Anderson and Fouad, and Kundur: section numbers

**What GridSim uses.**

- P. M. Anderson and A. A. Fouad, "Power System Control and Stability" (2nd ed., 2003): ch. 2 and Example 2.6 for the
  WSCC 9-bus system at 60 Hz, and ch. 3 for how a sudden power impact is shared among machines in proportion to their
  synchronising power (the distributed-inertia model's impact sharing).
- P. Kundur, "Power System Stability and Control" (1994): section 12.2 for the zero eigenvalue of the swing model (the
  rigid-body modes skipped by the continuum-wave model).

**What public evidence confirms.**

- The Library of Congress contents list confirms the chapter titles: Anderson and Fouad ch. 2 "The Elementary
  Mathematical Model" and ch. 3 "System Response to Small Disturbances"; Kundur ch. 12 "Small Signal Stability".
- Sauer and Pai, "Power System Dynamics and Stability", Example 7.1 (a copy is posted on a university course site)
  states that the WSCC 3-machine, 9-bus system runs at 60 Hz with a 100 MVA base, and names Anderson and Fouad as a
  source of the same system.

**Not confirmed.** The example number in Anderson and Fouad ch. 2, the section in Anderson and Fouad ch. 3, and Kundur
section 12.2.

**Document that would settle it.** The two books.

**Where you set it.** These citations name the source of a method, not of a number, so there is nothing to override:
the impact shares and the modes are computed from the case's own impedances and inertia, which are the user's (for
example `inertiaH` per generator in a JSON case). The WSCC 9-bus frequency is set as for TR-18 above: the built-in
`case9` carries 60 Hz with no setting, and `data/matpower/lib/case9.m --frequency-hz <Hz>` loads the same network at
any frequency.

## Mili, Cheniae, Vichare and Rousseeuw: sparse projection statistics

**What GridSim uses.** `ProjectionStatistics` (`src/GridSim.Core/Estimation/ProjectionStatistics.cs`) holds a dense
and a sparse form of the projection statistics from L. Mili, M. G. Cheniae, N. S. Vichare and P. J. Rousseeuw, "Robust
State Estimation Based on Projection Statistics", IEEE Transactions on Power Systems 11(2), 1996. Both use the plain
median and the 1.4826 MAD. The sparse form takes each direction's median and MAD over the rows that share a column
with it.

**What public evidence confirms.** A later free paper by Zhao and Mili (arXiv 1703.04790, eq. 29), which credits the
1996 paper, uses the plain median and the 1.4826 MAD. That matches GridSim's choice for the dense form.

**Not confirmed.** The sparse form, and the published projection statistics for the IEEE 14-bus system that would
validate it.

**Document that would settle it.** The 1996 paper (DOI 10.1109/59.496203).

**Where you set it.** Not user-settable, and no result depends on it: no estimator uses projection statistics. The
`schweppe-gm` method of `gridsim estimate` weights rows by the hat-matrix leverage, and will not switch to projection
statistics until the sparse form is validated against the paper.

## See also

- [Technical ch. 25, Short circuit (IEC 60909)](<docs/technical/0025 IEC 60909 short-circuit duty study.md>)
- [User manual ch. 7, Three-phase distribution feeders](<docs/manual/0007 Three-phase distribution feeders.md>)
- [User manual ch. 21, State estimation](<docs/manual/0021 State estimation.md>)
- [CLI reference: iec60909](<docs/manual/0019 CLI reference.md#iec60909>), [dcopf](<docs/manual/0019 CLI reference.md#dcopf>),
  [estimate](<docs/manual/0019 CLI reference.md#estimate>)
