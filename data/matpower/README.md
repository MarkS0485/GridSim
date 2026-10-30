# MATPOWER-format case libraries

Standard power-system test cases in MATPOWER `.m` format, parsed by
`GridSim.Core.IO.Matpower.MatpowerParser`. Small classic cases are also embedded in
`GridSim.Core` and exposed through `IeeeCases` (Case14 … Case300, Case24Rts, CigreMv);
the larger libraries are discovered from this folder by the CLI (`gridsim <file>.m`,
`gridsim bench`) and the WPF source picker.

## Layout & provenance

| Folder | Contents | Source | Version | Licence |
|--------|----------|--------|---------|---------|
| `lib/` | `case9/14/30/57/118/145/300`, `case24_ieee_rts` | [MATPOWER](https://github.com/MATPOWER/matpower) `data/` | 8.0 | MATPOWER case file, not covered by MATPOWER's BSD-3-Clause code licence (see `LICENSE-MATPOWER.txt`) |
| `pegase/` | `case1354pegase`, `case2869pegase`, `case9241pegase` | MATPOWER 8.0 `data/` | 8.0 | MATPOWER case file, not covered by the BSD-3-Clause code licence; citation (below) |
| `rts/` | `pglib_opf_case73_ieee_rts` (RTS-96, 73-bus 3-area) | [PGLib-OPF](https://github.com/power-grid-lib/pglib-opf) | master | CC BY 4.0 |
| `nesta/` | `pglib_opf_case{3_lmbd,5_pjm,30_as,162_ieee_dtc,240_pserc,588_sdet}` | PGLib-OPF | master | CC BY 4.0 |
| `benchmarks/` | `cigre_mv` (authored, not upstream) | CIGRE TF C6.04.02 report / pandapower reference | — | see below |
| `gb/` | `GBreducednetwork` (29 bus), `GBnetwork` (2,224 bus), and the PyPSA-GB reduced and zonal CSV networks | Strathclyde / Edinburgh test case archive, PyPSA-GB @ 8e084af | 2012-2013 | MIT (PyPSA-GB) and the Edinburgh archive's terms: see `gb/README.md` |

### NESTA note

The original NESTA archive (`nicta/nesta`) has been retired. Its cases are carried
forward and maintained in **PGLib-OPF**, the community successor, so the NESTA-lineage
subset here is sourced from PGLib-OPF. These are optimisation (OPF) benchmarks: some ship a
flat/stressed operating point rather than a solved power-flow snapshot, so the more heavily
loaded ones may not converge under a plain Newton-Raphson power flow (see `gridsim bench`).

Measured (October 2026, N052): three of them carry a placeholder dispatch that no power flow can
carry, so a plain power flow on the file as shipped has no solution at any iteration cap. This is
the data, not the solver:

| Case | Demand MW | Stated generation MW | What the slack would have to absorb |
|---|---:|---:|---|
| `pglib_opf_case3_lmbd` | 315.0 | 2,000.0 (1,000 on each of two units) | a 1,685 MW surplus, over lines whose V^2/X capability is 111-161 MW |
| `pglib_opf_case162_ieee_dtc` | 7,239.1 | 5,516.0 | a 1,723.1 MW deficit, above the slack unit's 1,119 MW Pmax |
| `pglib_opf_case240_pserc` | 144,179.7 | 100,642.9 | a 43,536.9 MW deficit, above the slack unit's 5,893 MW Pmax |

At a balanced dispatch each solves in a few Newton iterations (the DC-OPF dispatch: 6, 5 and 12
iterations with reactive limits on), and `case3_lmbd` at the published PGLib OPF point in its own
header (Pg 148.07 / 170.01 / 0 MW, Vm 1.100 / 0.926 / 0.900 pu) reproduces the published angles and
reactive outputs to the header's two decimals. Use them through `dcopf` / `acopf`, not as power-flow
snapshots.

### CIGRE MV (`benchmarks/cigre_mv.m`)

Hand-authored to the CIGRE Task Force C6.04.02 European MV distribution benchmark
("Benchmark Systems for Network Integration of Renewable and Distributed Energy Resources",
2014). Topology, line/transformer/load data follow the pandapower reference implementation
`create_cigre_network_mv` (e2nIEE/pandapower, BSD-3-Clause). Base radial configuration
(tie switches S1/S2/S3 open), converted to per-unit on a 100 MVA system base.

## Required citations

- **PEGASE**: S. Fliscounakis et al., "Contingency Ranking with Respect to Overloads in
  Very Large Power Systems…", IEEE Trans. Power Syst., 2013; and C. Josz et al., "AC Power
  Flow Data in MATPOWER and QCQP Format: iTesla, RTE Snapshots, and PEGASE", 2016.
- **PGLib-OPF / RTS-96 / NESTA-lineage**: S. Babaeinejadsarookolaee et al., "The Power Grid
  Library for Benchmarking AC Optimal Power Flow Algorithms", arXiv:1908.02788.
- **CIGRE MV**: CIGRE Task Force C6.04.02 final report, 2014.
- **GB reduced (29 bus) and GB network (2,224 bus)**: W. A. Bukhsh, K. McKinnon, "Network data of real transmission
  networks", April 2013; the 29-bus data by M. Belivanis, University of Strathclyde. **PyPSA-GB**: A. Lyden et al.,
  "PyPSA-GB: An open-source model of Great Britain's power system for simulating future energy scenarios", Energy
  Strategy Reviews, 2024. Details in `gb/README.md`.

Pinned upstream: MATPOWER `8.0`; PGLib-OPF fetched from `master`. Regenerate by re-fetching
the same paths from the repositories above.
