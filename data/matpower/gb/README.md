# GB reference networks (S2, October 2026)

Published, independent models of the Great Britain transmission network, bundled as cross-checks on GridSim's own
`gb-full` case and as GB-realistic solver benchmarks. Each is a built-in case name (`gridsim <verb> <name>`, manual
ch. 19 "Case arguments"). Checksums are SHA-256 of the file with LF line endings (git stores LF; a Windows checkout
may show CRLF).

| Built-in name | File(s) | Source | Licence / terms |
|---|---|---|---|
| `gb-reduced-29` | `GBreducednetwork.m` | Strathclyde reduced GB model (data by M. Belivanis, University of Strathclyde; MATPOWER file by W. A. Bukhsh, Oct 2012), as carried by PyPSA-GB @ 8e084af `data/network/reduced_network/GBreducednetwork.m` | PyPSA-GB repository: MIT (`LICENSE-PyPSA-GB.txt`). Edinburgh archive terms below. |
| `gb-network-2224` | `GBnetwork.m` | Bukhsh and McKinnon, "GB Network", Power Systems Test Case Archive, University of Edinburgh, <https://www.maths.ed.ac.uk/optenergy/NetworkData/fullGB/GBnetwork.m> (fetched 2026-10-04; page last updated 31 March 2013) | Edinburgh archive terms below. The same network is redistributed in pandapower 3.5.5 (`pandapower/networks/power_system_test_case_jsons/GBnetwork.json`) under BSD-3-Clause. |
| `pypsa-gb-reduced` | `pypsa-gb/reduced/buses.csv`, `lines.csv`, `links.csv` (+ `boundaries.json`, derived) | PyPSA-GB @ 8e084afe4fb2d4be86f270d3f12ad3315eee2a3a, `data/network/reduced_network/` (A. Lyden et al.) | MIT (`LICENSE-PyPSA-GB.txt`) |
| `pypsa-gb-zonal` | `pypsa-gb/zonal/buses.csv`, `links.csv` (+ `boundaries.json`, derived) | PyPSA-GB @ 8e084af, `data/network/zonal/` | MIT (`LICENSE-PyPSA-GB.txt`) |

| File | SHA-256 (LF) |
|---|---|
| `GBreducednetwork.m` | `6e46dc8c9cfd862971e9101b8b6486dad5bc4fb7917dfaa57b526d150f59d1f9` |
| `GBnetwork.m` | `d6c7d486d7c339fa2da926ad5ceeaa5b8a85286c079dc6bf12a09c8204c832c0` |
| `pypsa-gb/reduced/buses.csv` | `e0a1c7d792b56a4cc3d2186f99cc819335661955f2647ec2f2f52d02d8ab0b1d` |
| `pypsa-gb/reduced/lines.csv` | `a12251caad72989bc855074b4706f5ce4b9ac94cd4b914ea01fbf7688e652cf4` |
| `pypsa-gb/reduced/links.csv` | `ed4d5f70e9d6790b8974962c7627c3380b983d87da7d0f000ebfbbbcba8ddaa8` |
| `pypsa-gb/zonal/buses.csv` | `610f4da4d9cbc14a81ac8d1c3dda69f355a4a50c8cf7eec61ed193c4551fe468` |
| `pypsa-gb/zonal/links.csv` | `1e3e238ab870a8c089ca5c1224aec68d818fa480a259d3ecfcb9ad9edea97f87` |

## Licences and terms (read before any public release)

- **PyPSA-GB** (`andrewlyden/PyPSA-GB`) is MIT licensed, copyright (c) 2022 Andrew Lyden; the licence text is kept
  beside these files as `LICENSE-PyPSA-GB.txt`, as MIT requires. The CSV files and the PyPSA-GB copy of
  GBreducednetwork.m come from that repository (the whole folder is also in `docs/reference/etys-pypsa-gb/`).
- **Edinburgh Power Systems Test Case Archive** (W. A. Bukhsh, K. McKinnon; <https://www.maths.ed.ac.uk/optenergy/NetworkData/>)
  states no licence. Its introduction page asks users to cite "W. A. Bukhsh, Ken McKinnon, Network data of real
  transmission networks, April 2013" and says: "Contents of this archive are for research and educational purposes ...
  distributed with no guarantees regarding correctness, its quality or fitness for a specific application." The
  GBreducednetwork.m header asks users to cite Manolis Belivanis and the University of Strathclyde. GBnetwork.m's header
  gives its source as the GB SQSS review working group's publicly available data on the National Grid website.
- Both MATPOWER networks are also redistributed in pandapower (BSD-3-Clause, University of Kassel and Fraunhofer IEE)
  and GBreducednetwork.m in PyPSA-GB (MIT), which is why they are bundled here.
- **Decided (N125, Ruling R1, 2026-10-04): kept bundled, with attribution.** The two Edinburgh-archive files ship in
  every release that carries `data/` (the command line binaries) and stay unaltered. The attribution travels with
  them: this README (sources, citations, the archive's own words, checksums) and the "Bundled network data" section of
  [`THIRD-PARTY-NOTICES.md`](../../../THIRD-PARTY-NOTICES.md), which ships beside every binary. Anyone publishing
  results computed from these networks should cite: W. A. Bukhsh, K. McKinnon, "Network data of real transmission
  networks", April 2013 (both files), and M. Belivanis, University of Strathclyde (GBreducednetwork.m). The archive's
  "for research and educational purposes" line is a stated purpose, not a licence; questions about other use go to
  the authors through the archive's own page.

## What is in each, and the facts worth knowing

- **GBreducednetwork.m** (29 buses, 99 branches, 66 generators, base 100 MVA, slack bus 27). The PyPSA-GB copy differs
  from the Edinburgh original (`reducedGB/GBreducednetwork.m`, SHA-256 LF
  `ceca733f403ed2b98ea3a5f9712ba5ed7335284a4ee9703aec2296566e46d681`) only by three leading blank lines and two `//`
  comment lines: every number is identical (checked line by line). Four branches are 2-degree phase shifters (ratio 1,
  shift 2 deg). Its generator outputs are an OPF starting point, not a balanced dispatch: they sum to 75,012.14 MW
  against 56,325.86 MW of demand, so a power flow of the file as shipped makes the slack (Sellindge) absorb the
  surplus. `gencost` is labelled "Made up" in the file.
- **Decided (N130, Ruling R1, October 2026): gb-reduced-29 is NOT rebalanced.** Reasons, benchmarked against MATPOWER
  and pandapower: (1) the file is source data kept unaltered under N125, and its checksum above is its identity;
  (2) it is a cross-check case: MATPOWER `runpf` and pandapower `runpp` of this same file also put the whole
  imbalance (18,686.28 MW of generation over demand, less the losses) on the slack, so GridSim solving the file as
  shipped is the like-for-like comparison, and a GridSim-made rebalance would break it; (3) the source has no balanced
  dispatch to take: the .m file's PG is an OPF starting point (its own `gencost` is "Made up"), and PyPSA-GB's version
  of the network (`pypsa-gb-reduced`) carries no demand or generation at all (PyPSA-GB adds them from its time
  series), so any balanced dispatch would be invented here, not sourced. A balanced study of this network is a
  run-time choice, labelled as GridSim's: `acopf gb-reduced-29` / `dcopf gb-reduced-29` (an optimal dispatch on the
  file's limits) or `pf gb-reduced-29 --distributed-slack pmax` (the surplus shared over the units in proportion to
  their Pmax, as the P05 report does). Sums from the file: PG 75,012.14 MW, Pmax 82,384.8 MW, PD 56,325.86 MW,
  QD 16,249.5 MVAr; 66 units, all in service; the slack bus 27 (Sellindge) carries two units at PG 1,082 and 216 MW.
- **GBnetwork.m** (2,224 buses, 3,207 branches, 394 generators, base 100 MVA, slack bus 431). A balanced snapshot:
  generation 61,293.2301 MW against 60,077.56 MW of demand. 1,314 branches have a tap ratio (transformers), none a
  phase shift. No bus names or coordinates.
- **GBnetwork.m's stored voltages are not a solution of its own set-points (N128, confirmed October 2026).** Evaluated
  with the file's own data, the stored bus voltages and angles leave a largest real-power mismatch of
  28.283513095440938 MW (bus 1676, over the non-slack buses; 740.35 MW summed) and a largest reactive mismatch of
  210.6722185316424 MVAr (bus 1082, over the PQ buses; 5,462.72 MVAr summed). Two causes, both in the data:
  (1) the voltages are printed to 4 decimals (angles to 4 decimals of a degree) and many branches have x = 0.0001 pu,
  on which a rounding of 5e-5 pu moves tens of MW and MVAr: GridSim's own exact solution of the file (reactive
  limits off, mismatch 1.5e-9 MW / 4.5e-9 MVAr) rounded to the same 4 decimals shows 28.952260202150594 MW and
  210.07180761275723 MVAr, the same size and, for Q, the same bus; (2) the generator voltage set-points (mpc.gen VG)
  are given to 2 decimals and are not the stored PV-bus voltages (largest gap 0.0049 pu at bus 65: stored 1.0351,
  VG 1.04), so the snapshot was solved with set-points equal to the stored voltages: solving with every unit's VG set
  to its bus's stored voltage reproduces the stored state to 1.104e-4 pu and 1.96e-3 deg (losses 1,215.679 MW). A
  power flow of the file as shipped therefore holds VG, not the stored voltages, and differs from them by up to
  0.0049 pu at PV buses, 0.0046 pu at PQ buses (bus 724) and 0.382 deg (reactive limits off; losses 1,220.29 MW).
- **Losses compared with gb-full (N127, diagnosed October 2026; report only).** GBnetwork.m loses 1,224.91 MW (2.04 %
  of demand) against gb-full's 523.13 MW (0.98 %). The 701.8 MW gap, by element (both solved with reactive limits on):
  400 kV lines 504.53 against 205.64 MW, 275 kV lines 158.59 against 42.56, 132 kV lines 236.33 against 172.82, 33 kV
  lines 30.94 against none (gb-full has no 33 kV), and transformers 294.52 against 102.11 MW. Two data differences
  explain it: 1,229 of gb-full's 2,620 transformers have zero resistance (every 275 kV transformer loses nothing;
  GBnetwork.m: 29 of 1,314), and gb-full moves less power over its transmission circuits (the sum of |P| over the 400
  kV lines is 103.5 GW against 165.4 GW, and 55.9 against 65.1 GW at 275 kV, at 11 % less demand), so the loss per MW
  carried is lower too (275 kV lines 0.076 % against 0.244 %). The line R/X ratios are alike (400 kV median 0.083
  against 0.074; 132 kV 0.37 against 0.44). GridSim computes both consistently (the branch losses sum to the total);
  which model's figure is the better estimate of GB losses is a data question, not answered here.
- **PyPSA-GB reduced** is PyPSA-GB's own version of the 29-substation model: the same buses in the same order, with
  OSGB coordinates, 99 lines and 6 DC interconnector links, and NO demand or generation (PyPSA-GB adds those from its
  time series). GridSim takes demand and generation from GBreducednetwork.m. Its lines differ from the .m file's
  (the import prints these as notes): the two Keadby lines the .m file runs to Walpole go to Feckenham, the
  Errochty-Peterhead line is absent, a near-zero-impedance Neilston-Deeside line (x = 0.0001 pu, PyPSA-GB's stand-in
  for the Western HVDC link) is added, no line carries a phase shift, and Errochty is 275 kV (132 kV in the .m file).
  The 6 links are carried by the importer and not modelled.
- **PyPSA-GB zonal**: 20 zones and 37 links (31 between zones, 6 interconnectors), capacities only. Not AC solvable.
- **boundaries.json** (both PyPSA-GB folders) and `pypsa-gb/boundary-sides.json` are DERIVED, not source data:
  `tools/gb-reference/derive_boundaries.py` intersects each line or link (a straight segment between its end
  substations or zone points) with NESO's ETYS boundary GIS lines (Mar 2025, in `docs/reference/etys-pypsa-gb/network/GSP/`)
  for B4, B6, B7 and B8, checks that the crossing circuits split the network in two, and writes the cuts with no
  transfer limit (limitMw 0).

## Not bundled: the NESO PowerFactory models (P04, manual download needed)

NESO publishes two reduced GB models as DIgSILENT PowerFactory files. Neither is on disk, both need a browser download
from neso.energy (not reachable from the build environment), and NESO's redistribution terms for them are not stated
on the pages, so GridSim has no importer for them yet: a PowerFactory DGS importer will be written only against a
real exported file, never against the format description alone.

| Model | Where | What to fetch |
|---|---|---|
| NESO GB Reduced Model (ETYS 2024, 28 zones) | <https://www.neso.energy/publications/gb-reduced-model> | the `.pfdx` model and its release-note PDF |
| NESO GB 36-bus model (ETYS 2013 basis; the page now redirects to the 28-zone one) | <https://www.neso.energy/publications/gb-36-bus-electricity-transmission-network-model>, <https://www.neso.energy/document/316631/download> | the PowerFactory model file, if still offered |

To enable the work, put the files (unchanged, with their original names) in `data/reference-inbox/neso-gb-reduced-28/`
and `data/reference-inbox/neso-gb-36/` (a folder outside the bundled data tree, so nothing is shipped by accident), and,
if PowerFactory is available, also a DGS export of each model (File > Export > DGS, ASCII, all element classes). The
`.pfdx` will then be probed (zip / XML container or opaque binary) and the importer written against the DGS export.
`docs/reference/models/S267-2508.08945v1.pdf` (the GB-36 open dataset paper, arXiv 2508.08945) uses the NESO
PowerFactory model but contains no network data tables, so it cannot stand in for the files.
