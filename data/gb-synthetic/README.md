# gb-synthetic: a synthetic GB-at-scale network (2,227 buses, 2,268 branches)

A standard GridSim case directory built from no licensed data: `GridSim.Core.Synthetic.SyntheticGb.Build` places
ten GB demand zones (NSC, SSC, NEN, YRK, MER, MID, EAN, LDN, STH, SWW) with their 400 / 275 / 132 / 33 / 11 / 6.6 kV
layers, from a seeded random generator, and `gridsim synth` writes the result. The built-in case name `gb-synthetic`
(alias `synthetic`) is this folder, with its asset and protection bundle, for every verb (N316, October 2026: one name,
one case; it used to call the builder in memory for every verb but `trip`, which N238 had switched). `--dir
data/gb-synthetic`, the SchemaEditor and the v2 tests use this folder too. The builder itself is called only by `gridsim
synth` and by the tests that pin it. It is a test-scale stand-in for GB, not a model of the real network.

```bash
gridsim pf gb-synthetic                      # this folder (the name)
gridsim pf data/gb-synthetic                 # this folder (the path), the same run
gridsim synth --out <dir>                    # write the builder's output, seed 20260605, scale 1.0 (default
                                             # results/synth/gb-synthetic; --out data/gb-synthetic regenerates this folder)
```

## Design properties of the topology (N171, N191)

These follow from the builder's design and are kept as they are (Ruling R1, N191): changing them would move every
gb-synthetic number. A study on this network should read its N-1 results with them in mind.

- **Two-node zones get one 400 kV circuit.** Each zone gets max(2, round(30 x its demand share)) 400 kV nodes, joined
  in a ring; a zone with exactly two nodes gets a single circuit, named `<tag> 400 tie` (N171; it was `<tag> 400 ring
  1`). At the default options that is NSC (600 MW), SSC (2,200 MW), EAN (3,000 MW) and SWW (1,700 MW). The 400 kV
  corridors attach only to each zone's first node, so in NSC, EAN and SWW the second node and its plant hang on the
  tie alone: `NSC 400 tie`, `EAN 400 tie` and `SWW 400 tie` are bridges (their outage islands that node). SSC's second
  node also reaches the zone's 275 kV ring through a supergrid transformer, so `SSC 400 tie` is not a bridge.
- **Scotland is radial.** The corridors `NSC-SSC 400` and `SSC-NEN 400` are bridges: each Scottish zone joins the rest
  of GB by one 400 kV circuit, so an N-1 outage of either splits the network.
- **Most branches are bridges by construction.** 2,169 of the 2,268 branches are bridges: 2,137 of the 2,168
  transformers (the radial 132 / 33 / 11 kV layers under each zone) and 32 of the 100 lines. Only the 400 kV and 275 kV
  rings and the meshed corridors carry a second path.
- **Two supergrid transformers are overloaded whatever the dispatch (N210).** `MID SGT 400/275 1` and `LDN SGT
  400/275 1` carry 1,409.0 .. 1,446.0 MW and 1,258.1 .. 1,757.7 MW on 1,000 MVA over every dispatch the DC-OPF can
  choose (the 1.95.146 measurement in the table below; `dcopf` lists them as lines no dispatch can relieve, `acopf`
  leaves them out and says so). Reason: the builder gives each 275 kV node ONE 1,000 MVA supergrid transformer, and
  the Midlands and London 275 kV rings draw more from the 400 kV layer than one unit carries. Real GB substations of
  that duty have several interbus transformers in parallel. **Decided (Ruling R1, October 2026): kept, documented.**
  The fix is a topology change of the kind N191 ruled out (a second SGT, or n units sized to the flow, would halve
  the impedance and move every gb-synthetic number, and the pins on them), not a rating correction: raising only the
  rating would break the folder's own convention that n units are written as R / n, X / n, rating x n. A study that
  needs these two relieved should add the units in a copy of the folder.
- **Two radial transformers sit just past one unit at the AC-OPF's final pass (N209).** `YRK G8P4 132/33` (180.7 MW
  on 180) and `MID G2P1S3 33/11` (30.1 MW on 30), 0.4 % and 0.3 % over. Reason: N204 sizes each radial branch to the
  peak apparent demand of everything it feeds (the EREC P2 intact-network basis: installed capacity at least the
  group demand), but the flow at its sending end also carries the series losses downstream, which the loss-inflated
  final pass adds. `acopf` leaves both out and names them; it does not hide them.
  **Decided (Ruling R1, October 2026): kept, documented.** A loss margin in the sizing rule would double other branches too and move every gb-synthetic
  number for two overloads of under half a per cent; a real transformer of either size carries that much on its
  cyclic rating (IEC 60076-7 loading guide; not on disk, [VERIFY]), which the OPF does not model (it holds RateA as a hard limit).

## This folder is the builder's output (N192, N203, N204; regenerated October 2026)

`gridsim synth --out data/gb-synthetic` (seed 20260605, scale 1.0) wrote `buses.json`, `demand.json`,
`generation.json`, `transformer.json`, `transmission.json`, `onloadtapchanger.json` and `system.json`, so the builder
in memory (`SyntheticGb.Build`) and this folder are the same network: every bus, demand, generator and branch
field is equal (pinned by `R1EGbSyntheticTests`). Only the branch order differs: the JSON loader reads the lines
before the transformers, so branch numbers in a report differ, and where the DC-OPF has several optimal dispatches it
can report another one (the binding flow on `LDN SGT 400/275 1` is 1,673.6 MW from the builder and 1,676.3 MW from this
folder; the cost is the same). `asset-bindings.json`, `asset-classes.json` and `protections.json` are hand-authored and
exist only here (the builder writes none).

Before the regeneration (N192) the folder was an older copy: its 14 corridor names carried an en dash (`NSC` U+2013
`SSC 400`, now `NSC-SSC 400`), and it alone marked Moyle, IFA and BritNed line-commutated. Two builder changes made the
copy and the builder agree (Ruling R1 follow-up, Mark delegated 2026-10-04):

- **N203, line-commutated links.** The builder marks Moyle, IFA and BritNed as line-commutated (LCC) HVDC links:
  `"converterFaultCurrentPu": 0` (no fault current, and the line-commutated harmonic profile). The other
  interconnectors (North Sea Link, EWIC, Viking Link, ElecLink, NemoLink, IFA2, Greenlink) keep the default, a
  voltage-source link at 1.2 x rated. `gridsim synth` now writes the value when a unit sets one.
- **N204, the radial ladder sized to its load.** Every branch below the supergrid (GSP transformers 750 MVA, 132 kV
  industrial spurs 240 MVA, 132/33 kV 180 MVA, 33/11 kV 30 MVA, 11/6.6 kV 12 MVA, each the size of one standard unit)
  feeds a radial subtree. It is now built as n = ceil(S / unit) identical units in parallel, S being the peak apparent
  demand of everything it feeds, and written as their equivalent branch the way the loader reads `circuits: n`
  (R / n, X / n, B x n, rating x n). Basis (best judgement): installed capacity at least the peak group demand, the
  intact-network part of ENA EREC P2 Issue 8 (docs/reference S142) Table 1, with Group Demand including the demand
  embedded generation masks (clause 3.8), so embedded plant is not netted off; this ladder has no second circuit to
  secure (N191). 84 of the 2,164 radial branches became two units (none needed three): 65 of the 1,532 33/11 kV
  secondaries, 8 of the 248 11/6.6 kV pockets, 6 of the 278 132/33 kV transformers, 4 of the 79 GSP transformers and 1
  of the 27 industrial spurs. The meshed 400/275 kV supergrid transformers keep 1,000 MVA.

Effect, before -> after (the name `gb-synthetic`, which then called the builder, the 1.95.146 CLI against this one):

| Study | Before | After |
|---|---|---|
| `dcopf`: lines no dispatch can relieve | 53 (36 secondaries 33/11, 7 pockets 11/6.6, 5 at 132/33, 2 GSP transformers, 1 spur, `MID SGT 400/275 1`, `LDN SGT 400/275 1`) | 2 (`MID SGT 400/275 1`, 1,409.0 .. 1,446.0 MW; `LDN SGT 400/275 1`, 1,258.1 .. 1,757.7 MW, both on 1,000 MVA) |
| `dcopf`: cost, exit | 602,945 GBP/h, exit 0 | 602,945 GBP/h, exit 0 (the dispatch does not change) |
| `acopf`: lines left out at the final pass | 86 | 5 (the three 400/275 kV transformers, `YRK G8P4 132/33` at 180.7 MW and `MID G2P1S3 33/11` at 30.1 MW, which the loss-inflated final pass pushes just past one unit) |
| `acopf`: losses, cost, passes | 1,425.828 MW, 686,468 GBP/h, 5 | 1,385.085 MW, 684,072 GBP/h, 4 |
| `dcpf`: branches above 100 % of rating | 54 of 2,268 | 3 of 2,268 |
| `pf`: losses, lowest voltage | 863.577 MW / 2,518.372 MVAr, 0.8785 pu (bus 1007) | 836.743 MW / 1,950.587 MVAr, 0.8839 pu (bus 1125); 6 iterations both |
| `harmonics`: worst voltage THD | 2.45 % at bus 805 | 2.24 % at bus 820 |
| `stress`: max loadability, Vmin at collapse | 1.10 x base, 0.923 pu | 1.10 x base, 0.924 pu |
| `synth`: check solve (OLTC loop) | converged, 5 NR, mismatch 7.1E-010, V 0.949-1.040 pu, losses 853 MW | converged, 5 NR, mismatch 3.3E-010, V 0.950-1.040 pu, losses 831 MW |
